using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services.ProcessAI
{
    /// <summary>
    /// TorchSharp Transformer 서비스 클라이언트 래퍼
    /// 기존 Transformer 인터페이스를 유지하면서 IPC로 통신
    /// </summary>
    public class TorchServiceClient : IDisposable
    {
        private readonly AIServiceProcessManager _processManager;
        private readonly NamedPipeClient _client;
        private bool _disposed;

        public event Action<string>? OnLog;
        public bool IsModelReady { get; private set; }
        public int SeqLen { get; } = 8;

        public TorchServiceClient(string pipeName = "TradingBot_TorchService", string exeName = "TradingBot.TorchService.exe")
        {
            _processManager = new AIServiceProcessManager("TorchService", exeName, pipeName);
            _processManager.OnLog += msg => OnLog?.Invoke(msg);
            _processManager.OnError += msg => OnLog?.Invoke($"ERROR: {msg}");

            _client = new NamedPipeClient(pipeName, timeoutMs: 60000); // Transformer 예측은 더 오래 걸릴 수 있음
            _client.OnLog += msg => OnLog?.Invoke(msg);
        }

        public async Task<bool> StartAsync()
        {
            bool started = await _processManager.StartAsync();
            if (started)
            {
                // Health check로 모델 로드 상태 확인
                var health = await GetHealthAsync();
                IsModelReady = health?.ModelLoaded ?? false;
            }
            return started;
        }

        public void Stop()
        {
            _processManager.Stop();
            IsModelReady = false;
        }

        public async Task<(float candlesToTarget, float confidence)> PredictAsync(
            List<MultiTimeframeEntryFeature> sequence,
            CancellationToken token = default)
        {
            try
            {
                var request = new TransformerPredictRequest
                {
                    Command = "predict",
                    Sequence = sequence.Select(f => ConvertFeatureToDto(f)).ToList()
                };

                var response = await _client.SendRequestAsync<TransformerPredictRequest, TransformerPredictResponse>(request, token);

                if (response?.Success == true)
                {
                    return (response.CandlesToTarget, response.Confidence);
                }

                OnLog?.Invoke($"[TorchServiceClient] Predict failed: {response?.ErrorMessage}");
                return (-1f, 0f);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TorchServiceClient] Predict error: {ex.Message}");
                return (-1f, 0f);
            }
        }

        public async Task<TransformerTrainingMetrics?> TrainAsync(
            List<MultiTimeframeEntryFeature> features,
            int epochs = 5,
            int batchSize = 16,
            CancellationToken token = default)
        {
            try
            {
                OnLog?.Invoke($"[TorchServiceClient] Training with {features.Count} samples...");

                var request = new TransformerTrainRequest
                {
                    Command = "train",
                    Features = features.Select(ConvertFeatureToDto).ToList(),
                    Epochs = epochs,
                    BatchSize = batchSize
                };

                var response = await _client.SendRequestAsync<TransformerTrainRequest, TransformerTrainResponse>(request, token);

                if (response?.Success == true)
                {
                    IsModelReady = true;
                    OnLog?.Invoke($"[TorchServiceClient] Training completed - Best Loss: {response.BestValidationLoss:F4}");

                    return new TransformerTrainingMetrics
                    {
                        BestValidationLoss = response.BestValidationLoss,
                        FinalTrainLoss = response.FinalTrainLoss,
                        TrainedEpochs = response.TrainedEpochs
                    };
                }

                OnLog?.Invoke($"[TorchServiceClient] Training failed: {response?.ErrorMessage}");
                return null;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TorchServiceClient] Training error: {ex.Message}");
                return null;
            }
        }

        private async Task<HealthCheckResponse?> GetHealthAsync()
        {
            try
            {
                var request = new HealthCheckRequest { Command = "health" };
                return await _client.SendRequestAsync<HealthCheckRequest, HealthCheckResponse>(request);
            }
            catch
            {
                return null;
            }
        }

        private MultiTimeframeEntryFeatureDto ConvertFeatureToDto(MultiTimeframeEntryFeature feature)
        {
            return new MultiTimeframeEntryFeatureDto
            {
                Symbol = feature.Symbol,
                Timestamp = feature.Timestamp,
                EntryPrice = feature.EntryPrice,
                
                D1_Trend = feature.D1_Trend,
                D1_RSI = feature.D1_RSI,
                D1_MACD = feature.D1_MACD,
                D1_Signal = feature.D1_Signal,
                D1_BBPosition = feature.D1_BBPosition,
                D1_Volume_Ratio = feature.D1_Volume_Ratio,
                
                H4_Trend = feature.H4_Trend,
                H4_RSI = feature.H4_RSI,
                H4_MACD = feature.H4_MACD,
                H4_Signal = feature.H4_Signal,
                H4_BBPosition = feature.H4_BBPosition,
                H4_Volume_Ratio = feature.H4_Volume_Ratio,
                H4_DistanceToSupport = feature.H4_DistanceToSupport,
                H4_DistanceToResist = feature.H4_DistanceToResist,
                
                H2_Trend = feature.H2_Trend,
                H2_RSI = feature.H2_RSI,
                H2_MACD = feature.H2_MACD,
                H2_Signal = feature.H2_Signal,
                H2_BBPosition = feature.H2_BBPosition,
                H2_Volume_Ratio = feature.H2_Volume_Ratio,
                H2_WavePosition = feature.H2_WavePosition,
                
                H1_Trend = feature.H1_Trend,
                H1_RSI = feature.H1_RSI,
                H1_MACD = feature.H1_MACD,
                H1_Signal = feature.H1_Signal,
                H1_BBPosition = feature.H1_BBPosition,
                H1_Volume_Ratio = feature.H1_Volume_Ratio,
                H1_MomentumStrength = feature.H1_MomentumStrength,
                
                M15_RSI = feature.M15_RSI,
                M15_MACD = feature.M15_MACD,
                M15_Signal = feature.M15_Signal,
                M15_BBPosition = feature.M15_BBPosition,
                M15_Volume_Ratio = feature.M15_Volume_Ratio,
                M15_PriceVsSMA20 = feature.M15_PriceVsSMA20,
                M15_PriceVsSMA60 = feature.M15_PriceVsSMA60,
                M15_ADX = feature.M15_ADX,
                M15_PlusDI = feature.M15_PlusDI,
                M15_MinusDI = feature.M15_MinusDI,
                M15_ATR = feature.M15_ATR,
                M15_OI_Change_Pct = feature.M15_OI_Change_Pct,
                
                Fib_DistanceTo0382_Pct = feature.Fib_DistanceTo0382_Pct,
                Fib_DistanceTo0618_Pct = feature.Fib_DistanceTo0618_Pct,
                Fib_DistanceTo0786_Pct = feature.Fib_DistanceTo0786_Pct,
                Fib_InEntryZone = feature.Fib_InEntryZone,
                
                IsAsianSession = feature.IsAsianSession,
                IsEuropeSession = feature.IsEuropeSession,
                IsUSSession = feature.IsUSSession,
                HourOfDay = feature.HourOfDay,
                DayOfWeek = feature.DayOfWeek,
                
                ShouldEnter = feature.ShouldEnter,
                ActualProfitPct = feature.ActualProfitPct
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _processManager.Dispose();
            _client.Dispose();
            _disposed = true;
        }
    }

    public class TransformerTrainingMetrics
    {
        public float BestValidationLoss { get; set; }
        public float FinalTrainLoss { get; set; }
        public int TrainedEpochs { get; set; }
    }
}
