using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [Stage3] 하이브리드 AI 예측 서비스
    ///
    /// 전략: IPC 우선, 인프로세스 폴백
    ///   1. Named Pipe로 외부 AI 엔진에 예측 요청
    ///   2. 연결 실패/타임아웃 시 → 인프로세스 ML.NET/TF 직접 호출
    ///
    /// 이점:
    ///   - 외부 프로세스가 돌고 있으면 UI 스레드 영향 제로
    ///   - 외부 프로세스 없어도 기존처럼 100% 동작 (무중단)
    ///   - 점진적 마이그레이션 가능 (프로세스 분리는 선택적)
    /// </summary>
    public class HybridAIPredictionService : IDisposable
    {
        private readonly EntryTimingMLTrainer _mlTrainer;
        private readonly TensorFlowEntryTimingTrainer _tfTrainer;
        private readonly AIPipelineIpcService _ipcService;
        private readonly AIPipelineServer? _inProcessServer;
        private bool _useIpc;
        private bool _disposed;
        private int _ipcFailCount;
        private const int MaxIpcFailBeforeFallback = 3;
        private DateTime _nextIpcRetryTime = DateTime.MinValue;
        private static readonly TimeSpan IpcRetryInterval = TimeSpan.FromMinutes(1);

        public event Action<string>? OnLog;

        /// <summary>현재 IPC 모드 사용 여부</summary>
        public bool IsUsingIpc => _useIpc;

        /// <summary>현재 모드 설명</summary>
        public string CurrentMode => _useIpc ? "IPC (외부 프로세스)" : "InProcess (직접 추론)";

        public HybridAIPredictionService(
            EntryTimingMLTrainer mlTrainer,
            TensorFlowEntryTimingTrainer tfTrainer,
            bool enableInProcessServer = true)
        {
            _mlTrainer = mlTrainer;
            _tfTrainer = tfTrainer;
            _ipcService = new AIPipelineIpcService();
            _ipcService.OnLog += msg => OnLog?.Invoke(msg);
            _ipcService.OnError += msg => OnLog?.Invoke(msg);

            // 같은 프로세스 내에서 서버도 시작 (별도 프로세스 전환 전 테스트용)
            if (enableInProcessServer)
            {
                _inProcessServer = new AIPipelineServer(mlTrainer, tfTrainer);
                _inProcessServer.OnLog += msg => OnLog?.Invoke(msg);
            }
        }

        /// <summary>
        /// IPC 연결 시도. 실패해도 인프로세스로 동작.
        /// </summary>
        public async Task InitializeAsync(CancellationToken token = default)
        {
            // 인프로세스 서버 먼저 시작
            _inProcessServer?.Start();

            // 잠시 대기 후 클라이언트 연결
            await Task.Delay(200, token);

            _useIpc = await _ipcService.ConnectAsync(token);
            if (_useIpc)
            {
                OnLog?.Invoke("[HybridAI] IPC 모드 활성화 — 외부 AI 엔진 사용");
            }
            else
            {
                OnLog?.Invoke("[HybridAI] 인프로세스 모드 — 직접 추론 사용");
            }
        }

        /// <summary>
        /// ML.NET 예측 (IPC 우선, 인프로세스 폴백)
        /// </summary>
        public async Task<EntryTimingPrediction?> PredictMLAsync(
            string symbol,
            MultiTimeframeEntryFeature feature,
            CancellationToken token = default)
        {
            // IPC 모드이고 연속 실패가 임계치 미만이면 IPC 시도
            if (_useIpc && _ipcFailCount < MaxIpcFailBeforeFallback)
            {
                try
                {
                    var request = new AIPipelinePredictionRequest
                    {
                        Symbol = symbol,
                        ModelType = "ml",
                        FeatureJson = JsonSerializer.Serialize(feature)
                    };

                    var result = await _ipcService.PredictAsync(request, token);
                    if (result != null && result.Error == null)
                    {
                        Interlocked.Exchange(ref _ipcFailCount, 0);
                        return new EntryTimingPrediction
                        {
                            ShouldEnter = result.ShouldEnter,
                            Probability = result.Probability,
                            Score = result.Score
                        };
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _ipcFailCount);
                }
            }
            else if (_useIpc && DateTime.UtcNow > _nextIpcRetryTime)
            {
                // 주기적 IPC 재시도
                _nextIpcRetryTime = DateTime.UtcNow + IpcRetryInterval;
                Interlocked.Exchange(ref _ipcFailCount, 0);
                OnLog?.Invoke("[HybridAI] IPC 재연결 시도...");
            }

            // 인프로세스 폴백
            return _mlTrainer.Predict(feature);
        }

        /// <summary>
        /// TensorFlow 예측 (IPC 우선, 인프로세스 폴백)
        /// </summary>
        public async Task<(float candlesToTarget, float confidence)> PredictTFAsync(
            string symbol,
            System.Collections.Generic.List<MultiTimeframeEntryFeature> sequence,
            CancellationToken token = default)
        {
            if (_useIpc && _ipcFailCount < MaxIpcFailBeforeFallback)
            {
                try
                {
                    var request = new AIPipelinePredictionRequest
                    {
                        Symbol = symbol,
                        ModelType = "tf",
                        FeatureJson = JsonSerializer.Serialize(sequence)
                    };

                    var result = await _ipcService.PredictAsync(request, token);
                    if (result != null && result.Error == null)
                    {
                        Interlocked.Exchange(ref _ipcFailCount, 0);
                        return (result.CandlesToTarget, result.Confidence);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _ipcFailCount);
                }
            }

            // 인프로세스 폴백
            return _tfTrainer.Predict(sequence);
        }

        /// <summary>헬스체크</summary>
        public async Task<bool> HealthCheckAsync(CancellationToken token = default)
        {
            if (!_useIpc) return true; // 인프로세스는 항상 OK
            return await _ipcService.PingAsync(token);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ipcService.Dispose();
            _inProcessServer?.Dispose();
        }
    }
}
