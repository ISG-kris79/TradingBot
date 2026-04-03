using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TradingBot.Services
{
    /// <summary>
    /// 시장 상태 3분류 모델: TRENDING(0) / SIDEWAYS(1) / VOLATILE(2)
    /// 5분봉 캔들 데이터 기반, 이후 30분 가격 변동폭으로 자동 라벨링
    /// </summary>
    public class MarketRegimeClassifier : IDisposable
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _model;
        private PredictionEngine<RegimeFeature, RegimePrediction>? _predictionEngine;
        private readonly object _predictLock = new();
        private bool _disposed;

        private static readonly string DefaultModelDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "Models");
        private const string ModelFileName = "market_regime_model.zip";

        public bool IsModelLoaded => _model != null && _predictionEngine != null;
        public event Action<string>? OnLog;

        // 라벨링 기준 (이후 6봉=30분간 가격 변동)
        private const float TrendingThresholdPct = 2.0f;   // ±2% 이상 → TRENDING
        private const float SidewaysThresholdPct = 0.5f;   // ±0.5% 이하 → SIDEWAYS

        /// <summary>5분봉 캔들 리스트에서 학습 데이터 생성</summary>
        public static List<RegimeFeature> BuildTrainingData(List<Binance.Net.Interfaces.IBinanceKline> candles, int lookAhead = 6)
        {
            if (candles == null || candles.Count < 30 + lookAhead) return new();

            var features = new List<RegimeFeature>();

            for (int i = 20; i < candles.Count - lookAhead; i++)
            {
                var window = candles.Skip(i - 20).Take(21).ToList();
                var current = candles[i];
                decimal currentClose = current.ClosePrice;

                // 이후 lookAhead 봉 중 최고/최저
                decimal futureHigh = 0, futureLow = decimal.MaxValue;
                for (int j = i + 1; j <= i + lookAhead && j < candles.Count; j++)
                {
                    if (candles[j].HighPrice > futureHigh) futureHigh = candles[j].HighPrice;
                    if (candles[j].LowPrice < futureLow) futureLow = candles[j].LowPrice;
                }

                decimal maxUpPct = currentClose > 0 ? (futureHigh - currentClose) / currentClose * 100m : 0;
                decimal maxDownPct = currentClose > 0 ? (currentClose - futureLow) / currentClose * 100m : 0;
                decimal maxMovePct = Math.Max(maxUpPct, maxDownPct);

                // 라벨링
                uint label;
                if ((float)maxMovePct >= TrendingThresholdPct)
                    label = 0; // TRENDING
                else if ((float)maxMovePct <= SidewaysThresholdPct)
                    label = 1; // SIDEWAYS
                else
                    label = 2; // VOLATILE

                var feature = ExtractFeature(window);
                if (feature != null)
                {
                    feature.Label = label;
                    features.Add(feature);
                }
            }

            return features;
        }

        /// <summary>20봉 윈도우에서 피처 추출</summary>
        public static RegimeFeature? ExtractFeature(List<Binance.Net.Interfaces.IBinanceKline> window)
        {
            if (window == null || window.Count < 20) return null;

            var closes = window.Select(k => (double)k.ClosePrice).ToList();
            var highs = window.Select(k => (double)k.HighPrice).ToList();
            var lows = window.Select(k => (double)k.LowPrice).ToList();
            var volumes = window.Select(k => (double)k.Volume).ToList();

            double sma20 = closes.Average();
            double stdDev = Math.Sqrt(closes.Average(c => Math.Pow(c - sma20, 2)));
            double bbUpper = sma20 + 2 * stdDev;
            double bbLower = sma20 - 2 * stdDev;
            double bbWidth = sma20 > 0 ? (bbUpper - bbLower) / sma20 * 100 : 0;

            // ATR 계산
            double atrSum = 0;
            for (int i = 1; i < window.Count; i++)
            {
                double tr = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i] - closes[i - 1])));
                atrSum += tr;
            }
            double atr = atrSum / (window.Count - 1);
            double atrRatio = closes.Last() > 0 ? atr / closes.Last() * 100 : 0;

            // RSI (간이)
            double gainSum = 0, lossSum = 0;
            for (int i = 1; i < closes.Count; i++)
            {
                double diff = closes[i] - closes[i - 1];
                if (diff > 0) gainSum += diff; else lossSum += Math.Abs(diff);
            }
            double avgGain = gainSum / (closes.Count - 1);
            double avgLoss = lossSum / (closes.Count - 1);
            double rs = avgLoss > 0 ? avgGain / avgLoss : 100;
            double rsi = 100 - (100 / (1 + rs));

            // ADX (간이 — DI 방향성)
            double plusDmSum = 0, minusDmSum = 0;
            for (int i = 1; i < window.Count; i++)
            {
                double upMove = highs[i] - highs[i - 1];
                double downMove = lows[i - 1] - lows[i];
                if (upMove > downMove && upMove > 0) plusDmSum += upMove;
                if (downMove > upMove && downMove > 0) minusDmSum += downMove;
            }
            double plusDi = atrSum > 0 ? plusDmSum / atrSum * 100 : 0;
            double minusDi = atrSum > 0 ? minusDmSum / atrSum * 100 : 0;
            double diDiff = Math.Abs(plusDi - minusDi);
            double diSum = plusDi + minusDi;
            double adx = diSum > 0 ? diDiff / diSum * 100 : 0;

            // MACD 히스토그램 기울기
            var sma12 = closes.TakeLast(12).Average();
            var sma26 = closes.Count >= 26 ? closes.TakeLast(26).Average() : sma20;
            double macdNow = sma12 - sma26;
            var prevCloses = closes.Take(closes.Count - 1).ToList();
            var sma12Prev = prevCloses.TakeLast(12).Average();
            var sma26Prev = prevCloses.Count >= 26 ? prevCloses.TakeLast(26).Average() : prevCloses.Average();
            double macdPrev = sma12Prev - sma26Prev;
            double macdSlope = macdNow - macdPrev;

            // 거래량 변화율
            double avgVol = volumes.Take(volumes.Count - 1).Average();
            double volChange = avgVol > 0 ? (volumes.Last() - avgVol) / avgVol * 100 : 0;

            // SMA 정배열 강도
            double sma10 = closes.TakeLast(10).Average();
            double trendStrength = 0;
            if (sma10 > sma20) trendStrength += 0.5;
            if (closes.Last() > sma10) trendStrength += 0.5;
            if (sma10 < sma20) trendStrength -= 0.5;
            if (closes.Last() < sma10) trendStrength -= 0.5;

            // 캔들 크기 대비 꼬리 비율 (횡보지표)
            double avgBodyRatio = 0;
            for (int i = Math.Max(0, window.Count - 5); i < window.Count; i++)
            {
                double range = highs[i] - lows[i];
                double body = Math.Abs((double)window[i].ClosePrice - (double)window[i].OpenPrice);
                avgBodyRatio += range > 0 ? body / range : 0;
            }
            avgBodyRatio /= 5;

            return new RegimeFeature
            {
                BB_Width = (float)bbWidth,
                ADX = (float)adx,
                ATR_Ratio = (float)atrRatio,
                RSI = (float)rsi,
                MACD_Slope = (float)macdSlope,
                Volume_Change_Pct = (float)volChange,
                Trend_Strength = (float)trendStrength,
                Avg_Body_Ratio = (float)avgBodyRatio,
                PlusDI = (float)plusDi,
                MinusDI = (float)minusDi,
            };
        }

        public async Task<bool> TrainAndSaveAsync(List<RegimeFeature> data, CancellationToken token = default)
        {
            if (data == null || data.Count < 100)
            {
                OnLog?.Invoke($"[Regime] 학습 데이터 부족: {data?.Count ?? 0}건 (최소 100건 필요)");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);

                    string[] featureCols = { "BB_Width", "ADX", "ATR_Ratio", "RSI", "MACD_Slope",
                        "Volume_Change_Pct", "Trend_Strength", "Avg_Body_Ratio", "PlusDI", "MinusDI" };

                    var pipeline = _mlContext.Transforms.Conversion
                        .MapValueToKey("Label")
                        .Append(_mlContext.Transforms.Concatenate("Features", featureCols))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.MulticlassClassification.Trainers.LightGbm(
                            labelColumnName: "Label",
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 10,
                            learningRate: 0.1,
                            numberOfIterations: 200))
                        .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

                    OnLog?.Invoke($"[Regime] LightGBM 3분류 학습 시작 ({data.Count}건)...");
                    _model = pipeline.Fit(split.TrainSet);

                    var predictions = _model.Transform(split.TestSet);
                    var metrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");
                    OnLog?.Invoke($"[Regime] 학습 완료 | MicroAcc={metrics.MicroAccuracy:P2}, MacroAcc={metrics.MacroAccuracy:P2}");

                    Directory.CreateDirectory(DefaultModelDir);
                    string path = Path.Combine(DefaultModelDir, ModelFileName);
                    _mlContext.Model.Save(_model, dataView.Schema, path);

                    lock (_predictLock)
                    {
                        _predictionEngine?.Dispose();
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<RegimeFeature, RegimePrediction>(_model);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Regime] 학습 실패: {ex.Message}");
                    return false;
                }
            }, token);
        }

        public bool TryLoadModel()
        {
            try
            {
                string path = Path.Combine(DefaultModelDir, ModelFileName);
                if (!File.Exists(path)) return false;

                _model = _mlContext.Model.Load(path, out _);
                lock (_predictLock)
                {
                    _predictionEngine?.Dispose();
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<RegimeFeature, RegimePrediction>(_model);
                }
                OnLog?.Invoke($"[Regime] 모델 로드 완료: {path}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Regime] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>시장 상태 예측: 0=TRENDING, 1=SIDEWAYS, 2=VOLATILE</summary>
        public (MarketRegime regime, float confidence) Predict(RegimeFeature feature)
        {
            lock (_predictLock)
            {
                if (_predictionEngine == null)
                    return (MarketRegime.Unknown, 0f);

                var pred = _predictionEngine.Predict(feature);
                float maxScore = pred.Score?.Length > 0 ? pred.Score.Max() : 0f;
                float sumExp = pred.Score?.Sum(s => (float)Math.Exp(s)) ?? 1f;
                float confidence = sumExp > 0 ? (float)Math.Exp(maxScore) / sumExp : 0f;

                var regime = pred.PredictedLabel switch
                {
                    0 => MarketRegime.Trending,
                    1 => MarketRegime.Sideways,
                    2 => MarketRegime.Volatile,
                    _ => MarketRegime.Unknown
                };

                return (regime, confidence);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_predictLock) { _predictionEngine?.Dispose(); }
        }
    }

    public enum MarketRegime { Unknown = -1, Trending = 0, Sideways = 1, Volatile = 2 }

    public class RegimeFeature
    {
        [ColumnName("Label")]
        public uint Label { get; set; }

        public float BB_Width { get; set; }
        public float ADX { get; set; }
        public float ATR_Ratio { get; set; }
        public float RSI { get; set; }
        public float MACD_Slope { get; set; }
        public float Volume_Change_Pct { get; set; }
        public float Trend_Strength { get; set; }
        public float Avg_Body_Ratio { get; set; }
        public float PlusDI { get; set; }
        public float MinusDI { get; set; }
    }

    public class RegimePrediction
    {
        [ColumnName("PredictedLabel")]
        public uint PredictedLabel { get; set; }

        [ColumnName("Score")]
        public float[]? Score { get; set; }
    }
}
