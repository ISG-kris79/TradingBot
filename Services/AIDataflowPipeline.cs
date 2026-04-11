using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows;

namespace TradingBot.Services
{
    /// <summary>
    /// [WPF최적화 1] TPL Dataflow 기반 AI 추론 파이프라인
    ///
    /// 구조:
    ///   1분 봉 완성 이벤트
    ///      ↓
    ///   BufferBlock (입력 큐, 용량 제한)
    ///      ↓
    ///   TransformBlock (Feature 추출, 병렬 가능)
    ///      ↓
    ///   ActionBlock (ML.NET + TF 추론, 순차 처리)
    ///      ↓
    ///   Dispatcher.InvokeAsync (UI 업데이트, 결과만 전달)
    ///
    /// 이점:
    ///   - UI 스레드 완전 차단 없음
    ///   - 백프레셔 자동 관리 (BufferBlock 용량 초과 시 드랍)
    ///   - AI 추론이 느려도 UI는 계속 반응
    /// </summary>
    public class AIDataflowPipeline : IDisposable
    {
        private readonly EntryTimingMLTrainer _mlTrainer;
        private readonly BufferBlock<AIInferenceRequest> _inputBuffer;
        private readonly TransformBlock<AIInferenceRequest, AIInferenceResult> _inferenceBlock;
        private readonly ActionBlock<AIInferenceResult> _outputBlock;
        private bool _disposed;

        // 성능 모니터링
        private long _totalProcessed;
        private long _totalDropped;
        private long _totalInferenceMs;
        private readonly ConcurrentDictionary<string, DateTime> _lastInferenceTime = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? OnLog;
        public event Action<AIInferenceResult>? OnInferenceCompleted;

        /// <summary>처리된 총 추론 수</summary>
        public long TotalProcessed => _totalProcessed;
        /// <summary>백프레셔로 드랍된 요청 수</summary>
        public long TotalDropped => _totalDropped;
        /// <summary>평균 추론 시간(ms)</summary>
        public double AverageInferenceMs => _totalProcessed > 0 ? (double)_totalInferenceMs / _totalProcessed : 0;

        public AIDataflowPipeline(
            EntryTimingMLTrainer mlTrainer,
            int maxBufferSize = 100,
            int maxDegreeOfParallelism = 1)
        {
            _mlTrainer = mlTrainer;

            // 1. 입력 버퍼: 용량 초과 시 오래된 요청 무시
            _inputBuffer = new BufferBlock<AIInferenceRequest>(new DataflowBlockOptions
            {
                BoundedCapacity = maxBufferSize
            });

            // 2. 추론 블록: ML.NET + TF 동시 추론
            _inferenceBlock = new TransformBlock<AIInferenceRequest, AIInferenceResult>(
                request => ProcessInferenceAsync(request),
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    BoundedCapacity = maxDegreeOfParallelism * 2
                });

            // 3. 출력 블록: UI 스레드로 결과 전달
            _outputBlock = new ActionBlock<AIInferenceResult>(
                result => DeliverToUI(result),
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = 1 // UI 업데이트는 순차
                });

            // 파이프라인 연결
            _inputBuffer.LinkTo(_inferenceBlock, new DataflowLinkOptions { PropagateCompletion = true });
            _inferenceBlock.LinkTo(_outputBlock, new DataflowLinkOptions { PropagateCompletion = true });
        }

        /// <summary>
        /// 추론 요청 제출 (비동기, non-blocking)
        /// 버퍼가 가득 차면 드랍 (백프레셔)
        /// </summary>
        public bool TrySubmit(AIInferenceRequest request)
        {
            // 동일 심볼 디바운싱: 최소 500ms 간격
            if (_lastInferenceTime.TryGetValue(request.Symbol, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime).TotalMilliseconds < 500)
                    return false;
            }

            bool accepted = _inputBuffer.Post(request);
            if (!accepted)
            {
                Interlocked.Increment(ref _totalDropped);
            }
            else
            {
                _lastInferenceTime[request.Symbol] = DateTime.UtcNow;
            }
            return accepted;
        }

        /// <summary>
        /// 추론 요청 제출 (비동기, awaitable)
        /// </summary>
        public async Task<bool> SubmitAsync(AIInferenceRequest request, CancellationToken token = default)
        {
            return await _inputBuffer.SendAsync(request, token);
        }

        private async Task<AIInferenceResult> ProcessInferenceAsync(AIInferenceRequest request)
        {
            var sw = Stopwatch.StartNew();
            var result = new AIInferenceResult
            {
                Symbol = request.Symbol,
                RequestTimestamp = request.Timestamp
            };

            try
            {
                // ML.NET 추론 (Thread Pool에서 실행)
                if (request.Feature != null && _mlTrainer.IsModelLoaded)
                {
                    var mlPrediction = await Task.Run(() => _mlTrainer.Predict(request.Feature));
                    if (mlPrediction != null)
                    {
                        result.MLShouldEnter = mlPrediction.ShouldEnter;
                        result.MLProbability = mlPrediction.Probability;
                        result.MLScore = mlPrediction.Score;
                    }
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                OnLog?.Invoke($"[AIDataflow] {request.Symbol} 추론 오류: {ex.Message}");
            }

            sw.Stop();
            result.InferenceMs = (int)sw.ElapsedMilliseconds;
            Interlocked.Increment(ref _totalProcessed);
            Interlocked.Add(ref _totalInferenceMs, result.InferenceMs);

            return result;
        }

        private void DeliverToUI(AIInferenceResult result)
        {
            try
            {
                // UI 스레드로 결과 전달 (WPF Dispatcher)
                if (Application.Current != null)
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        OnInferenceCompleted?.Invoke(result);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    OnInferenceCompleted?.Invoke(result);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[AIDataflow] UI 전달 오류: {ex.Message}");
            }
        }

        /// <summary>파이프라인 종료 (진행 중 작업 완료 대기)</summary>
        public async Task CompleteAsync()
        {
            _inputBuffer.Complete();
            await _outputBlock.Completion;
        }

        /// <summary>성능 요약 문자열</summary>
        public string GetPerformanceSummary()
        {
            return $"처리={_totalProcessed}, 드랍={_totalDropped}, " +
                   $"평균추론={AverageInferenceMs:F1}ms, " +
                   $"버퍼대기={_inputBuffer.Count}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inputBuffer.Complete();
        }
    }

    // ─── Dataflow 모델 ──────────────────────────────────────

    /// <summary>AI 추론 요청</summary>
    public class AIInferenceRequest
    {
        public string Symbol { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public MultiTimeframeEntryFeature? Feature { get; set; }
        public System.Collections.Generic.List<MultiTimeframeEntryFeature>? TransformerSequence { get; set; }
        public decimal CurrentPrice { get; set; }
        public string Decision { get; set; } = "";  // "LONG" or "SHORT"
    }

    /// <summary>AI 추론 결과</summary>
    public class AIInferenceResult
    {
        public string Symbol { get; set; } = "";
        public DateTime RequestTimestamp { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }

        // ML.NET 결과
        public bool MLShouldEnter { get; set; }
        public float MLProbability { get; set; }
        public float MLScore { get; set; }

        // 성능
        public int InferenceMs { get; set; }
    }
}
