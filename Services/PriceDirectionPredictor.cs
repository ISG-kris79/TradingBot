using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TradingBot.Services
{
    /// <summary>
    /// [v3.8.1] 5분봉 가격 방향 예측기 (Binary: Up/Down)
    /// + 변동성 예측기 (Regression: 다음 봉 ATR%)
    /// </summary>
    public class PriceDirectionPredictor
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _directionModel;
        private ITransformer? _volatilityModel;
        private PredictionEngine<DirectionFeature, DirectionPrediction>? _directionEngine;
        private PredictionEngine<VolatilityFeature, VolatilityPrediction>? _volatilityEngine;

        private static readonly string ModelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "Models");
        private static readonly string DirectionModelPath = Path.Combine(ModelDir, "direction_model.zip");
        private static readonly string VolatilityModelPath = Path.Combine(ModelDir, "volatility_model.zip");

        public bool IsDirectionModelLoaded => _directionEngine != null;
        public bool IsVolatilityModelLoaded => _volatilityEngine != null;

        public event Action<string>? OnLog;

        // ═══════════════════════════════════════════════════════════
        // Direction Feature: 5분 후 가격이 올라갈까 내려갈까
        // ═══════════════════════════════════════════════════════════
        public class DirectionFeature
        {
            public float RSI { get; set; }
            public float MACD { get; set; }
            public float MACD_Signal { get; set; }
            public float MACD_Hist { get; set; }
            public float BB_Position { get; set; }      // 0~1
            public float BB_Width { get; set; }
            public float ATR_Ratio { get; set; }        // ATR/Price %
            public float Volume_Ratio { get; set; }     // 현재/평균 거래량
            public float Price_Change_1 { get; set; }   // 직전 1봉 변화%
            public float Price_Change_3 { get; set; }   // 직전 3봉 변화%
            public float Price_Change_6 { get; set; }   // 직전 6봉 변화%
            public float SMA20_Distance { get; set; }   // 가격-SMA20 %
            public float ADX { get; set; }
            public float Stoch_K { get; set; }
            public float Stoch_D { get; set; }
            public float HourOfDay { get; set; }

            [ColumnName("Label")]
            public bool GoesUp { get; set; }            // true = 15분 내 +0.3%+ 큰 상승
        }

        public class DirectionPrediction
        {
            [ColumnName("PredictedLabel")]
            public bool GoesUp { get; set; }
            [ColumnName("Probability")]
            public float Probability { get; set; }
            [ColumnName("Score")]
            public float Score { get; set; }
        }

        // ═══════════════════════════════════════════════════════════
        // Volatility Feature: 다음 봉의 변동성 (ATR% 예측)
        // ═══════════════════════════════════════════════════════════
        public class VolatilityFeature
        {
            public float ATR_Ratio { get; set; }
            public float BB_Width { get; set; }
            public float Volume_Ratio { get; set; }
            public float ADX { get; set; }
            public float RSI { get; set; }
            public float Price_Range_1 { get; set; }    // 직전 1봉 (High-Low)/Close %
            public float Price_Range_3 { get; set; }    // 직전 3봉 평균 레인지%
            public float HourOfDay { get; set; }

            [ColumnName("Label")]
            public float NextATR { get; set; }          // 다음 봉 ATR/Price %
        }

        public class VolatilityPrediction
        {
            [ColumnName("Score")]
            public float PredictedATR { get; set; }
        }

        // ═══════════════════════════════════════════════════════════
        // 학습 데이터 생성 (5분봉 캔들에서 자동 레이블링)
        // ═══════════════════════════════════════════════════════════
        public (List<DirectionFeature> direction, List<VolatilityFeature> volatility) BuildTrainingData(List<IBinanceKline> candles)
        {
            var dirData = new List<DirectionFeature>();
            var volData = new List<VolatilityFeature>();

            if (candles == null || candles.Count < 30) return (dirData, volData);

            // [v3.9.2] 다음 3봉(15분) 이내 큰 움직임 예측 (±0.3%+)
            for (int i = 20; i < candles.Count - 3; i++)
            {
                var current = candles[i];
                // 다음 3봉 중 최대 상승/하락 계산
                decimal maxHigh = 0, minLow = decimal.MaxValue;
                for (int j = 1; j <= 3 && i + j < candles.Count; j++)
                {
                    if (candles[i + j].HighPrice > maxHigh) maxHigh = candles[i + j].HighPrice;
                    if (candles[i + j].LowPrice < minLow) minLow = candles[i + j].LowPrice;
                }
                decimal maxUpPct = current.ClosePrice > 0 ? (maxHigh - current.ClosePrice) / current.ClosePrice * 100 : 0;
                decimal maxDownPct = current.ClosePrice > 0 ? (current.ClosePrice - minLow) / current.ClosePrice * 100 : 0;
                var next = candles[i + 1];
                var window = candles.Skip(Math.Max(0, i - 19)).Take(20).ToList();
                if (window.Count < 20) continue;

                double rsi = IndicatorCalculator.CalculateRSI(window, 14);
                var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(window);
                double atr = IndicatorCalculator.CalculateATR(window, 14);
                double sma20 = IndicatorCalculator.CalculateSMA(window, 20);
                double avgVol = window.Average(k => (double)k.Volume);
                double curVol = (double)current.Volume;

                float bbPos = bb.Upper > bb.Lower
                    ? (float)((double)current.ClosePrice - bb.Lower) / (float)(bb.Upper - bb.Lower)
                    : 0.5f;
                float bbWidth = bb.Mid > 0 ? (float)((bb.Upper - bb.Lower) / bb.Mid * 100) : 0;
                float atrRatio = (double)current.ClosePrice > 0 ? (float)(atr / (double)current.ClosePrice * 100) : 0;
                float volRatio = avgVol > 0 ? (float)(curVol / avgVol) : 1f;
                float sma20Dist = sma20 > 0 ? (float)(((double)current.ClosePrice - sma20) / sma20 * 100) : 0;

                float pc1 = i >= 1 && candles[i - 1].ClosePrice > 0
                    ? (float)((current.ClosePrice - candles[i - 1].ClosePrice) / candles[i - 1].ClosePrice * 100) : 0;
                float pc3 = i >= 3 && candles[i - 3].ClosePrice > 0
                    ? (float)((current.ClosePrice - candles[i - 3].ClosePrice) / candles[i - 3].ClosePrice * 100) : 0;
                float pc6 = i >= 6 && candles[i - 6].ClosePrice > 0
                    ? (float)((current.ClosePrice - candles[i - 6].ClosePrice) / candles[i - 6].ClosePrice * 100) : 0;

                // Stochastic
                float stochK = 0.5f, stochD = 0.5f;
                try
                {
                    var (sk, sd) = IndicatorCalculator.CalculateStochastic(window, 14, 3, 3);
                    stochK = (float)(sk / 100); stochD = (float)(sd / 100);
                }
                catch { }

                // ADX
                float adx = 0;
                try { var (a, _, _) = IndicatorCalculator.CalculateADX(window, 14); adx = (float)(a / 100); } catch { }

                // [v3.9.2] 큰 움직임 레이블: 15분 내 +0.3% 이상 상승 && 상승폭 > 하락폭
                bool bigMoveUp = maxUpPct >= 0.3m && maxUpPct > maxDownPct;
                float nextRange = (double)next.ClosePrice > 0
                    ? (float)((double)(next.HighPrice - next.LowPrice) / (double)next.ClosePrice * 100) : 0;

                dirData.Add(new DirectionFeature
                {
                    RSI = (float)(rsi / 100), MACD = (float)macd.Macd, MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist, BB_Position = bbPos, BB_Width = bbWidth,
                    ATR_Ratio = atrRatio, Volume_Ratio = volRatio,
                    Price_Change_1 = pc1, Price_Change_3 = pc3, Price_Change_6 = pc6,
                    SMA20_Distance = sma20Dist, ADX = adx, Stoch_K = stochK, Stoch_D = stochD,
                    HourOfDay = current.OpenTime.Hour,
                    GoesUp = bigMoveUp
                });

                float pr1 = (double)current.ClosePrice > 0
                    ? (float)((double)(current.HighPrice - current.LowPrice) / (double)current.ClosePrice * 100) : 0;
                float pr3 = 0;
                if (i >= 3)
                {
                    pr3 = (float)Enumerable.Range(i - 2, 3).Average(j =>
                        (double)candles[j].ClosePrice > 0
                            ? (double)(candles[j].HighPrice - candles[j].LowPrice) / (double)candles[j].ClosePrice * 100 : 0);
                }

                volData.Add(new VolatilityFeature
                {
                    ATR_Ratio = atrRatio, BB_Width = bbWidth, Volume_Ratio = volRatio,
                    ADX = adx, RSI = (float)(rsi / 100),
                    Price_Range_1 = pr1, Price_Range_3 = (float)pr3,
                    HourOfDay = current.OpenTime.Hour,
                    NextATR = nextRange
                });
            }

            return (dirData, volData);
        }

        // ═══════════════════════════════════════════════════════════
        // 학습
        // ═══════════════════════════════════════════════════════════
        public async Task<(double dirAccuracy, double volR2)> TrainAsync(
            List<DirectionFeature> dirData, List<VolatilityFeature> volData, CancellationToken token = default)
        {
            double dirAcc = 0, volR2 = 0;

            await Task.Run(() =>
            {
                Directory.CreateDirectory(ModelDir);

                // Direction Model
                if (dirData.Count >= 100)
                {
                    var dirView = _mlContext.Data.LoadFromEnumerable(dirData);
                    var split = _mlContext.Data.TrainTestSplit(dirView, 0.2);

                    var dirFeatureCols = new[] {
                        nameof(DirectionFeature.RSI), nameof(DirectionFeature.MACD),
                        nameof(DirectionFeature.MACD_Signal), nameof(DirectionFeature.MACD_Hist),
                        nameof(DirectionFeature.BB_Position), nameof(DirectionFeature.BB_Width),
                        nameof(DirectionFeature.ATR_Ratio), nameof(DirectionFeature.Volume_Ratio),
                        nameof(DirectionFeature.Price_Change_1), nameof(DirectionFeature.Price_Change_3),
                        nameof(DirectionFeature.Price_Change_6), nameof(DirectionFeature.SMA20_Distance),
                        nameof(DirectionFeature.ADX), nameof(DirectionFeature.Stoch_K),
                        nameof(DirectionFeature.Stoch_D), nameof(DirectionFeature.HourOfDay) };
                    var pipeline = _mlContext.Transforms.Concatenate("Features", dirFeatureCols)
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                            labelColumnName: "Label", featureColumnName: "Features",
                            numberOfLeaves: 31, minimumExampleCountPerLeaf: 10,
                            learningRate: 0.05, numberOfIterations: 300));

                    _directionModel = pipeline.Fit(split.TrainSet);
                    var metrics = _mlContext.BinaryClassification.Evaluate(
                        _directionModel.Transform(split.TestSet));
                    dirAcc = metrics.Accuracy;

                    _mlContext.Model.Save(_directionModel, dirView.Schema, DirectionModelPath);
                    _directionEngine = _mlContext.Model.CreatePredictionEngine<DirectionFeature, DirectionPrediction>(_directionModel);
                    OnLog?.Invoke($"🧠 [Direction] 학습 완료 | {dirData.Count}건 | Acc={dirAcc:P1} AUC={metrics.AreaUnderRocCurve:F3}");
                }

                // Volatility Model
                if (volData.Count >= 100)
                {
                    var volView = _mlContext.Data.LoadFromEnumerable(volData);
                    var volSplit = _mlContext.Data.TrainTestSplit(volView, 0.2);

                    var volFeatureCols = new[] {
                        nameof(VolatilityFeature.ATR_Ratio), nameof(VolatilityFeature.BB_Width),
                        nameof(VolatilityFeature.Volume_Ratio), nameof(VolatilityFeature.ADX),
                        nameof(VolatilityFeature.RSI), nameof(VolatilityFeature.Price_Range_1),
                        nameof(VolatilityFeature.Price_Range_3), nameof(VolatilityFeature.HourOfDay) };
                    var volPipeline = _mlContext.Transforms.Concatenate("Features", volFeatureCols)
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.FastTree(
                            labelColumnName: "Label", featureColumnName: "Features",
                            numberOfLeaves: 20, numberOfTrees: 100, learningRate: 0.05));

                    _volatilityModel = volPipeline.Fit(volSplit.TrainSet);
                    var volMetrics = _mlContext.Regression.Evaluate(
                        _volatilityModel.Transform(volSplit.TestSet));
                    volR2 = volMetrics.RSquared;

                    _mlContext.Model.Save(_volatilityModel, volView.Schema, VolatilityModelPath);
                    _volatilityEngine = _mlContext.Model.CreatePredictionEngine<VolatilityFeature, VolatilityPrediction>(_volatilityModel);
                    OnLog?.Invoke($"🧠 [Volatility] 학습 완료 | {volData.Count}건 | R²={volR2:F3} MAE={volMetrics.MeanAbsoluteError:F4}");
                }
            }, token);

            return (dirAcc, volR2);
        }

        // ═══════════════════════════════════════════════════════════
        // 예측
        // ═══════════════════════════════════════════════════════════
        public DirectionPrediction? PredictDirection(DirectionFeature feature)
        {
            return _directionEngine?.Predict(feature);
        }

        public float PredictVolatility(VolatilityFeature feature)
        {
            return _volatilityEngine?.Predict(feature)?.PredictedATR ?? 0;
        }

        // ═══════════════════════════════════════════════════════════
        // 모델 로드
        // ═══════════════════════════════════════════════════════════
        public void TryLoadModels()
        {
            try
            {
                if (File.Exists(DirectionModelPath))
                {
                    _directionModel = _mlContext.Model.Load(DirectionModelPath, out _);
                    _directionEngine = _mlContext.Model.CreatePredictionEngine<DirectionFeature, DirectionPrediction>(_directionModel);
                    OnLog?.Invoke("🧠 [Direction] 기존 모델 로드 완료");
                }
                if (File.Exists(VolatilityModelPath))
                {
                    _volatilityModel = _mlContext.Model.Load(VolatilityModelPath, out _);
                    _volatilityEngine = _mlContext.Model.CreatePredictionEngine<VolatilityFeature, VolatilityPrediction>(_volatilityModel);
                    OnLog?.Invoke("🧠 [Volatility] 기존 모델 로드 완료");
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ 방향/변동성 모델 로드 실패: {ex.Message}"); }
        }
    }
}
