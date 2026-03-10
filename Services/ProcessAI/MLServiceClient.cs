using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services.ProcessAI
{
    /// <summary>
    /// ML.NET 서비스 클라이언트 래퍼
    /// 기존 AIPredictor 인터페이스를 유지하면서 IPC로 통신
    /// </summary>
    public class MLServiceClient : IDisposable
    {
        private readonly AIServiceProcessManager _processManager;
        private readonly NamedPipeClient _client;
        private bool _disposed;

        public event Action<string>? OnLog;
        public bool IsModelLoaded { get; private set; }

        public MLServiceClient(string pipeName = "TradingBot_MLService", string exeName = "TradingBot.MLService.exe")
        {
            _processManager = new AIServiceProcessManager("MLService", exeName, pipeName);
            _processManager.OnLog += msg => OnLog?.Invoke(msg);
            _processManager.OnError += msg => OnLog?.Invoke($"ERROR: {msg}");

            _client = new NamedPipeClient(pipeName);
            _client.OnLog += msg => OnLog?.Invoke(msg);
        }

        public async Task<bool> StartAsync()
        {
            bool started = await _processManager.StartAsync();
            if (started)
            {
                // Health check로 모델 로드 상태 확인
                var health = await GetHealthAsync();
                IsModelLoaded = health?.ModelLoaded ?? false;
            }
            return started;
        }

        public void Stop()
        {
            _processManager.Stop();
            IsModelLoaded = false;
        }

        public async Task<PredictionResult?> PredictAsync(CandleData candle, CancellationToken token = default)
        {
            try
            {
                var request = new MLPredictRequest
                {
                    Command = "predict",
                    Candle = ConvertToDto(candle)
                };

                var response = await _client.SendRequestAsync<MLPredictRequest, MLPredictResponse>(request, token);

                if (response?.Success == true)
                {
                    return new PredictionResult
                    {
                        Prediction = response.ShouldEnter,
                        Probability = response.Probability,
                        Score = response.Confidence
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[MLServiceClient] Predict error: {ex.Message}");
                return null;
            }
        }

        public async Task<MLTrainingMetrics?> TrainAsync(
            List<MultiTimeframeEntryFeature> features,
            int maxEpochs = 100,
            CancellationToken token = default)
        {
            try
            {
                OnLog?.Invoke($"[MLServiceClient] Training with {features.Count} samples...");

                var request = new MLTrainRequest
                {
                    Command = "train",
                    Features = features.Select(ConvertFeatureToDto).ToList(),
                    MaxEpochs = maxEpochs
                };

                var response = await _client.SendRequestAsync<MLTrainRequest, MLTrainResponse>(request, token);

                if (response?.Success == true)
                {
                    IsModelLoaded = true;
                    OnLog?.Invoke($"[MLServiceClient] Training completed - Accuracy: {response.Accuracy:P2}");

                    return new MLTrainingMetrics
                    {
                        Accuracy = response.Accuracy,
                        F1Score = response.F1Score,
                        AUC = response.AUC
                    };
                }

                OnLog?.Invoke($"[MLServiceClient] Training failed: {response?.ErrorMessage}");
                return null;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[MLServiceClient] Training error: {ex.Message}");
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

        private CandleDataDto ConvertToDto(CandleData candle)
        {
            return new CandleDataDto
            {
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,
                OpenTime = candle.OpenTime,
                CloseTime = candle.CloseTime,
                RSI = candle.RSI,
                MACD = candle.MACD,
                MACD_Signal = candle.MACD_Signal,
                BollingerUpper = candle.BollingerUpper,
                BollingerLower = candle.BollingerLower,
                ATR = candle.ATR,
                Price_Change_Pct = candle.Price_Change_Pct
            };
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

    public class MLTrainingMetrics
    {
        public double Accuracy { get; set; }
        public double F1Score { get; set; }
        public double AUC { get; set; }
    }
}
