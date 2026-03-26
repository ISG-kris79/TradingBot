using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TradingBot.Services
{
    /// <summary>
    /// AI 및 주문 전담 워커 스레드 (UI 스레드와 완전 격리)
    ///
    /// 핵심 원칙:
    ///   - AI 추론 + 주문 실행은 UI 스레드보다 높은 우선순위로 실행
    ///   - UI가 밀려도 주문은 즉시 실행됨
    ///   - 결과만 Dispatcher.BeginInvoke(Background)로 UI에 전달
    ///
    /// 구조:
    ///   [1분 봉 완성] → BlockingCollection.Add()
    ///                         ↓
    ///   [전용 스레드] Take() → AI 추론 → 주문 실행
    ///                         ↓ (결과만)
    ///   [UI 스레드] Dispatcher.BeginInvoke(Background)
    /// </summary>
    public class AIDedicatedWorkerThread : IDisposable
    {
        private readonly BlockingCollection<AIWorkItem> _workQueue;
        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _cts;
        private bool _disposed;

        // AI 모델 참조 (워커 스레드에서 직접 호출)
        private readonly EntryTimingMLTrainer? _mlTrainer;
        private readonly TensorFlowEntryTimingTrainer? _tfTrainer;

        // 성능 카운터
        private long _totalProcessed;
        private long _totalOrdersExecuted;
        private long _maxLatencyMs;
        private readonly ConcurrentDictionary<string, DateTime> _symbolDebounce = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? OnLog;
        public event Action<AIWorkerResult>? OnResultReady;

        public long TotalProcessed => _totalProcessed;
        public long TotalOrdersExecuted => _totalOrdersExecuted;
        public long MaxLatencyMs => _maxLatencyMs;

        public AIDedicatedWorkerThread(
            EntryTimingMLTrainer? mlTrainer = null,
            TensorFlowEntryTimingTrainer? tfTrainer = null,
            int maxQueueSize = 200)
        {
            _mlTrainer = mlTrainer;
            _tfTrainer = tfTrainer;
            _cts = new CancellationTokenSource();
            _workQueue = new BlockingCollection<AIWorkItem>(maxQueueSize);

            _workerThread = new Thread(WorkerLoop)
            {
                Name = "AI-Order-Worker",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal  // UI 스레드(Normal)보다 높은 우선순위
            };
        }

        /// <summary>워커 스레드 시작</summary>
        public void Start()
        {
            _workerThread.Start();
            OnLog?.Invoke($"[AIWorker] 전용 스레드 시작 (Priority={_workerThread.Priority}, ID={_workerThread.ManagedThreadId})");
        }

        /// <summary>
        /// 작업 제출 (non-blocking, 큐가 가득 차면 false 반환)
        /// </summary>
        public bool TrySubmit(AIWorkItem item)
        {
            if (_disposed || _cts.IsCancellationRequested)
                return false;

            // 심볼별 디바운싱 (300ms)
            if (_symbolDebounce.TryGetValue(item.Symbol, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime).TotalMilliseconds < 300)
                    return false;
            }
            _symbolDebounce[item.Symbol] = DateTime.UtcNow;

            return _workQueue.TryAdd(item);
        }

        /// <summary>작업 제출 (blocking, 큐에 공간이 날 때까지 대기)</summary>
        public void Submit(AIWorkItem item)
        {
            if (_disposed || _cts.IsCancellationRequested)
                return;

            _workQueue.Add(item, _cts.Token);
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var item in _workQueue.GetConsumingEnumerable(_cts.Token))
                {
                    var sw = Stopwatch.StartNew();
                    var result = new AIWorkerResult { Symbol = item.Symbol };

                    try
                    {
                        // 1. ML.NET 추론 (이 스레드에서 직접 실행 — UI 차단 없음)
                        if (item.Feature != null && _mlTrainer != null && _mlTrainer.IsModelLoaded)
                        {
                            var prediction = _mlTrainer.Predict(item.Feature);
                            if (prediction != null)
                            {
                                result.MLShouldEnter = prediction.ShouldEnter;
                                result.MLProbability = prediction.Probability;
                            }
                        }

                        // 2. TensorFlow 추론
                        if (item.TransformerSequence != null && _tfTrainer != null && _tfTrainer.IsModelReady)
                        {
                            var (candles, confidence) = _tfTrainer.Predict(item.TransformerSequence);
                            result.TFCandlesToTarget = candles;
                            result.TFConfidence = confidence;
                        }

                        // 3. 주문 조건 충족 시 즉시 실행 (UI 거치지 않음)
                        if (item.OrderAction != null && result.MLShouldEnter)
                        {
                            try
                            {
                                item.OrderAction(result);
                                result.OrderExecuted = true;
                                Interlocked.Increment(ref _totalOrdersExecuted);
                            }
                            catch (Exception orderEx)
                            {
                                result.OrderError = orderEx.Message;
                                OnLog?.Invoke($"[AIWorker] {item.Symbol} 주문 실행 오류: {orderEx.Message}");
                            }
                        }

                        result.Success = true;
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                    }

                    sw.Stop();
                    result.LatencyMs = (int)sw.ElapsedMilliseconds;

                    // 최대 지연 시간 추적
                    long currentMax;
                    do
                    {
                        currentMax = _maxLatencyMs;
                        if (result.LatencyMs <= currentMax) break;
                    } while (Interlocked.CompareExchange(ref _maxLatencyMs, result.LatencyMs, currentMax) != currentMax);

                    Interlocked.Increment(ref _totalProcessed);

                    // 4. 결과만 UI 스레드에 비동기로 전달 (낮은 우선순위)
                    DeliverResultToUI(result);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[AIWorker] 워커 루프 치명 오류: {ex.Message}");
            }
        }

        private void DeliverResultToUI(AIWorkerResult result)
        {
            try
            {
                if (Application.Current != null)
                {
                    // UI 스레드에는 Background 우선순위로 전달 (UI 렌더링보다 낮게)
                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(() => OnResultReady?.Invoke(result)),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    OnResultReady?.Invoke(result);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[AIWorker] UI 전달 오류: {ex.Message}");
            }
        }

        /// <summary>큐 상태 요약</summary>
        public string GetStatus()
        {
            return $"Queue={_workQueue.Count}/{_workQueue.BoundedCapacity}, " +
                   $"Processed={_totalProcessed}, Orders={_totalOrdersExecuted}, " +
                   $"MaxLatency={_maxLatencyMs}ms";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _workQueue.CompleteAdding();

            // 워커 스레드 종료 대기 (최대 3초)
            if (_workerThread.IsAlive)
                _workerThread.Join(3000);

            _workQueue.Dispose();
            _cts.Dispose();
        }
    }

    // ─── 워커 모델 ──────────────────────────────────────────

    /// <summary>AI 워커 작업 항목</summary>
    public class AIWorkItem
    {
        public string Symbol { get; set; } = "";
        public MultiTimeframeEntryFeature? Feature { get; set; }
        public System.Collections.Generic.List<MultiTimeframeEntryFeature>? TransformerSequence { get; set; }
        public decimal CurrentPrice { get; set; }
        public string Decision { get; set; } = "";

        /// <summary>
        /// 주문 실행 콜백 (조건 충족 시 워커 스레드에서 직접 호출)
        /// null이면 추론만 수행
        /// </summary>
        public Action<AIWorkerResult>? OrderAction { get; set; }
    }

    /// <summary>AI 워커 결과 (UI로 전달)</summary>
    public class AIWorkerResult
    {
        public string Symbol { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }

        // ML.NET
        public bool MLShouldEnter { get; set; }
        public float MLProbability { get; set; }

        // TensorFlow
        public float TFCandlesToTarget { get; set; }
        public float TFConfidence { get; set; }

        // 주문
        public bool OrderExecuted { get; set; }
        public string? OrderError { get; set; }

        // 성능
        public int LatencyMs { get; set; }
    }
}
