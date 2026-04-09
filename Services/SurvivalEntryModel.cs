using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.0] 생존 기반 진입 모델
    /// 핵심: "손절(-SL%)에 닿지 않고 익절(+TP%)에 먼저 도달하는가?"
    /// + IID Spike Detection (거래량 급증 실시간 감지)
    /// + 1분+5분 하이브리드 피처 (PUMP용)
    /// </summary>
    public class SurvivalEntryModel
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _majorModel;
        private ITransformer? _pumpModel;
        private PredictionEngine<SurvivalFeature, SurvivalPrediction>? _majorEngine;
        private PredictionEngine<SurvivalFeature, SurvivalPrediction>? _pumpEngine;

        private static readonly string ModelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "Models");
        private static readonly string MajorModelPath = Path.Combine(ModelDir, "survival_major.zip");
        private static readonly string PumpModelPath = Path.Combine(ModelDir, "survival_pump.zip");

        public bool IsMajorReady => _majorEngine != null;
        public bool IsPumpReady => _pumpEngine != null;
        public event Action<string>? OnLog;

        // ═══════════════════════════════════════════════════════════
        // Feature: 생존 기반 진입 판단 피처 (메이저/PUMP 공용)
        // ═══════════════════════════════════════════════════════════
        public class SurvivalFeature
        {
            // 현재 봉 지표
            public float RSI { get; set; }
            public float MACD_Hist { get; set; }
            public float ADX { get; set; }
            public float BB_Position { get; set; }
            public float BB_Width { get; set; }
            public float ATR_Ratio { get; set; }
            public float Volume_Ratio { get; set; }         // 현재/평균20 거래량
            public float Stoch_K { get; set; }
            public float SMA20_Distance { get; set; }

            // 시간 지연 피처 (과거 봉)
            public float RSI_Lag1 { get; set; }              // 1봉 전 RSI
            public float RSI_Lag3 { get; set; }              // 3봉 전 RSI
            public float MACD_Hist_Lag1 { get; set; }
            public float Volume_Ratio_Lag1 { get; set; }
            public float Volume_Ratio_Lag3 { get; set; }
            public float Price_Change_1 { get; set; }        // 1봉 변화%
            public float Price_Change_3 { get; set; }        // 3봉 변화%
            public float Price_Change_6 { get; set; }        // 6봉 변화%

            // 1분봉 하이브리드 (PUMP용, 메이저는 0)
            public float M1_Volume_Surge { get; set; }       // 1분봉 거래량/평균
            public float M1_Price_Accel { get; set; }        // 1분 가격 가속도
            public float M1_RSI { get; set; }                // 1분 RSI

            // 선물 데이터
            public float OI_Change { get; set; }
            public float Funding_Rate { get; set; }

            // 시간
            public float HourOfDay { get; set; }

            [ColumnName("Label")]
            public bool Survived { get; set; }               // true = TP 도달 (SL 미도달)
        }

        public class SurvivalPrediction
        {
            [ColumnName("PredictedLabel")]
            public bool Survived { get; set; }
            [ColumnName("Probability")]
            public float Probability { get; set; }
            [ColumnName("Score")]
            public float Score { get; set; }
        }

        // ═══════════════════════════════════════════════════════════
        // 학습 데이터 생성 — 손절/익절 기반 라벨링
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 5분봉에서 생존 기반 학습 데이터 생성
        /// TP: +tpPct% 도달, SL: -slPct% 도달, lookAhead봉 이내
        /// </summary>
        public List<SurvivalFeature> BuildTrainingData(
            List<IBinanceKline> candles5m,
            List<IBinanceKline>? candles1m = null,
            float tpPct = 1.0f,    // 메이저: +1.0%, PUMP: +3.0%
            float slPct = 0.5f,    // 메이저: -0.5%, PUMP: -1.5%
            int lookAhead = 20)    // 메이저: 20봉(100분), PUMP: 6봉(30분)
        {
            var data = new List<SurvivalFeature>();
            if (candles5m == null || candles5m.Count < 30) return data;

            // 1분봉 인덱스 맵 (시간 → 데이터)
            var m1Map = new Dictionary<long, (double vol, double rsi, double accel)>();
            if (candles1m != null && candles1m.Count >= 10)
            {
                for (int mi = 5; mi < candles1m.Count; mi++)
                {
                    var mc = candles1m[mi];
                    long key = mc.OpenTime.Ticks / TimeSpan.TicksPerMinute;
                    double avgVol1m = candles1m.Skip(Math.Max(0, mi - 19)).Take(20).Average(k => (double)k.Volume);
                    double vol1m = avgVol1m > 0 ? (double)mc.Volume / avgVol1m : 1;
                    double rsi1m = 50;
                    try { rsi1m = IndicatorCalculator.CalculateRSI(candles1m.Take(mi + 1).TakeLast(14).ToList(), 7); } catch { }
                    // 가속도: 현재 변화 - 이전 변화
                    double pc0 = mi > 0 && candles1m[mi - 1].ClosePrice > 0
                        ? (double)(mc.ClosePrice - candles1m[mi - 1].ClosePrice) / (double)candles1m[mi - 1].ClosePrice * 100 : 0;
                    double pc1 = mi > 1 && candles1m[mi - 2].ClosePrice > 0
                        ? (double)(candles1m[mi - 1].ClosePrice - candles1m[mi - 2].ClosePrice) / (double)candles1m[mi - 2].ClosePrice * 100 : 0;
                    double accel = pc0 - pc1;
                    m1Map[key] = (vol1m, rsi1m, accel);
                }
            }

            for (int i = 20; i < candles5m.Count - lookAhead; i++)
            {
                var c = candles5m[i];
                decimal close = c.ClosePrice;
                if (close <= 0) continue;

                // 라벨링: 다음 lookAhead 봉 중 TP/SL 어디에 먼저 도달?
                decimal tpPrice = close * (1 + (decimal)tpPct / 100);
                decimal slPrice = close * (1 - (decimal)slPct / 100);
                bool hitTP = false, hitSL = false;
                for (int j = 1; j <= lookAhead && i + j < candles5m.Count; j++)
                {
                    var future = candles5m[i + j];
                    if (future.HighPrice >= tpPrice) { hitTP = true; break; }
                    if (future.LowPrice <= slPrice) { hitSL = true; break; }
                }
                bool survived = hitTP && !hitSL;

                // 윈도우 지표 계산
                var window = candles5m.Skip(Math.Max(0, i - 19)).Take(20).ToList();
                if (window.Count < 14) continue;

                double rsi = 50, adx = 25, bbPos = 0.5, bbWidth = 2, atrRatio = 0.5, sma20Dist = 0;
                float macdHist = 0, stochK = 0.5f;
                try { rsi = IndicatorCalculator.CalculateRSI(window, 14); } catch { }
                try
                {
                    var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
                    bbPos = bb.Upper > bb.Lower ? ((double)close - bb.Lower) / (bb.Upper - bb.Lower) : 0.5;
                    bbWidth = bb.Mid > 0 ? (bb.Upper - bb.Lower) / bb.Mid * 100 : 2;
                }
                catch { }
                try { var m = IndicatorCalculator.CalculateMACD(window); macdHist = (float)m.Hist; } catch { }
                try { var (a, _, _) = IndicatorCalculator.CalculateADX(window, 14); adx = a; } catch { }
                try { double atr = IndicatorCalculator.CalculateATR(window, 14); atrRatio = (double)close > 0 ? atr / (double)close * 100 : 0.5; } catch { }
                try { double sma = IndicatorCalculator.CalculateSMA(window, 20); sma20Dist = sma > 0 ? ((double)close - sma) / sma * 100 : 0; } catch { }
                try { var (sk, _) = IndicatorCalculator.CalculateStochastic(window, 14, 3, 3); stochK = (float)(sk / 100); } catch { }

                double avgVol = window.Average(k => (double)k.Volume);
                float volRatio = avgVol > 0 ? (float)((double)c.Volume / avgVol) : 1;

                // 시간 지연 피처
                float rsiLag1 = 0.5f, rsiLag3 = 0.5f, macdLag1 = 0, volLag1 = 1, volLag3 = 1;
                float pc1 = 0, pc3 = 0, pc6 = 0;
                if (i >= 1 && candles5m[i - 1].ClosePrice > 0)
                {
                    pc1 = (float)((close - candles5m[i - 1].ClosePrice) / candles5m[i - 1].ClosePrice * 100);
                    volLag1 = avgVol > 0 ? (float)((double)candles5m[i - 1].Volume / avgVol) : 1;
                }
                if (i >= 3 && candles5m[i - 3].ClosePrice > 0)
                {
                    pc3 = (float)((close - candles5m[i - 3].ClosePrice) / candles5m[i - 3].ClosePrice * 100);
                    volLag3 = avgVol > 0 ? (float)((double)candles5m[i - 3].Volume / avgVol) : 1;
                }
                if (i >= 6 && candles5m[i - 6].ClosePrice > 0)
                    pc6 = (float)((close - candles5m[i - 6].ClosePrice) / candles5m[i - 6].ClosePrice * 100);
                // 과거 RSI (간략 계산)
                try
                {
                    if (i >= 1)
                    {
                        var w1 = candles5m.Skip(Math.Max(0, i - 20)).Take(20).ToList();
                        rsiLag1 = (float)(IndicatorCalculator.CalculateRSI(w1, 14) / 100);
                    }
                    if (i >= 3)
                    {
                        var w3 = candles5m.Skip(Math.Max(0, i - 22)).Take(20).ToList();
                        rsiLag3 = (float)(IndicatorCalculator.CalculateRSI(w3, 14) / 100);
                    }
                }
                catch { }
                try
                {
                    if (i >= 1)
                    {
                        var w1 = candles5m.Skip(Math.Max(0, i - 20)).Take(20).ToList();
                        macdLag1 = (float)IndicatorCalculator.CalculateMACD(w1).Hist;
                    }
                }
                catch { }

                // 1분봉 하이브리드 (시간 매칭)
                float m1VolSurge = 0, m1Accel = 0, m1Rsi = 0.5f;
                long m1Key = c.OpenTime.Ticks / TimeSpan.TicksPerMinute;
                for (long offset = 0; offset < 5; offset++) // 5분봉 안의 1분봉 검색
                {
                    if (m1Map.TryGetValue(m1Key + offset, out var m1Data))
                    {
                        m1VolSurge = Math.Max(m1VolSurge, (float)m1Data.vol);
                        m1Accel = (float)m1Data.accel;
                        m1Rsi = (float)(m1Data.rsi / 100);
                        break;
                    }
                }

                data.Add(new SurvivalFeature
                {
                    RSI = (float)(rsi / 100), MACD_Hist = macdHist, ADX = (float)(adx / 100),
                    BB_Position = (float)bbPos, BB_Width = (float)bbWidth, ATR_Ratio = (float)atrRatio,
                    Volume_Ratio = volRatio, Stoch_K = stochK, SMA20_Distance = (float)sma20Dist,
                    RSI_Lag1 = rsiLag1, RSI_Lag3 = rsiLag3, MACD_Hist_Lag1 = macdLag1,
                    Volume_Ratio_Lag1 = volLag1, Volume_Ratio_Lag3 = volLag3,
                    Price_Change_1 = pc1, Price_Change_3 = pc3, Price_Change_6 = pc6,
                    M1_Volume_Surge = m1VolSurge, M1_Price_Accel = m1Accel, M1_RSI = m1Rsi,
                    OI_Change = 0, Funding_Rate = 0, HourOfDay = c.OpenTime.Hour,
                    Survived = survived
                });
            }

            return data;
        }

        // ═══════════════════════════════════════════════════════════
        // 학습 (언더샘플링 포함)
        // ═══════════════════════════════════════════════════════════
        public async Task<(double majorAcc, double pumpAcc)> TrainAsync(
            List<SurvivalFeature> majorData,
            List<SurvivalFeature> pumpData,
            CancellationToken token = default)
        {
            double mAcc = 0, pAcc = 0;
            Directory.CreateDirectory(ModelDir);

            await Task.Run(() =>
            {
                // 메이저 모델
                if (majorData.Count >= 200)
                {
                    var balanced = BalanceSamples(majorData);
                    mAcc = TrainModel(balanced, MajorModelPath, out _majorModel);
                    if (_majorModel != null)
                    {
                        var schema = _mlContext.Data.LoadFromEnumerable(balanced.Take(1)).Schema;
                        _majorEngine = _mlContext.Model.CreatePredictionEngine<SurvivalFeature, SurvivalPrediction>(_majorModel);
                    }
                    OnLog?.Invoke($"🧠 [Survival Major] 학습 완료 | {balanced.Count}건 (원본{majorData.Count}) | Acc={mAcc:P1}");
                }

                // PUMP 모델
                if (pumpData.Count >= 200)
                {
                    var balanced = BalanceSamples(pumpData);
                    pAcc = TrainModel(balanced, PumpModelPath, out _pumpModel);
                    if (_pumpModel != null)
                    {
                        _pumpEngine = _mlContext.Model.CreatePredictionEngine<SurvivalFeature, SurvivalPrediction>(_pumpModel);
                    }
                    OnLog?.Invoke($"🧠 [Survival Pump] 학습 완료 | {balanced.Count}건 (원본{pumpData.Count}) | Acc={pAcc:P1}");
                }
            }, token);

            return (mAcc, pAcc);
        }

        /// <summary>1:1 언더샘플링</summary>
        private List<SurvivalFeature> BalanceSamples(List<SurvivalFeature> data)
        {
            var positives = data.Where(d => d.Survived).ToList();
            var negatives = data.Where(d => !d.Survived).ToList();
            int minCount = Math.Min(positives.Count, negatives.Count);
            if (minCount < 50) return data; // 샘플 부족 시 원본 반환

            var rng = new Random(42);
            var balancedPos = positives.OrderBy(_ => rng.Next()).Take(minCount).ToList();
            var balancedNeg = negatives.OrderBy(_ => rng.Next()).Take(minCount).ToList();
            var result = balancedPos.Concat(balancedNeg).OrderBy(_ => rng.Next()).ToList();
            return result;
        }

        private double TrainModel(List<SurvivalFeature> data, string modelPath, out ITransformer? model)
        {
            model = null;
            try
            {
                var view = _mlContext.Data.LoadFromEnumerable(data);
                var split = _mlContext.Data.TrainTestSplit(view, 0.2);

                var featureCols = new[]
                {
                    "RSI", "MACD_Hist", "ADX", "BB_Position", "BB_Width", "ATR_Ratio",
                    "Volume_Ratio", "Stoch_K", "SMA20_Distance",
                    "RSI_Lag1", "RSI_Lag3", "MACD_Hist_Lag1",
                    "Volume_Ratio_Lag1", "Volume_Ratio_Lag3",
                    "Price_Change_1", "Price_Change_3", "Price_Change_6",
                    "M1_Volume_Surge", "M1_Price_Accel", "M1_RSI",
                    "OI_Change", "Funding_Rate", "HourOfDay"
                };

                var pipeline = _mlContext.Transforms.Concatenate("Features", featureCols)
                    .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                    .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                        labelColumnName: "Label", featureColumnName: "Features",
                        numberOfLeaves: 31, minimumExampleCountPerLeaf: 10,
                        learningRate: 0.05, numberOfIterations: 300));

                model = pipeline.Fit(split.TrainSet);
                var metrics = _mlContext.BinaryClassification.Evaluate(model.Transform(split.TestSet));
                _mlContext.Model.Save(model, view.Schema, modelPath);

                OnLog?.Invoke($"  Acc={metrics.Accuracy:P1} AUC={metrics.AreaUnderRocCurve:F3} F1={metrics.F1Score:F3}");
                return metrics.Accuracy;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ 모델 학습 실패: {ex.Message}");
                return 0;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 예측
        // ═══════════════════════════════════════════════════════════
        public SurvivalPrediction? PredictMajor(SurvivalFeature feature) => _majorEngine?.Predict(feature);
        public SurvivalPrediction? PredictPump(SurvivalFeature feature) => _pumpEngine?.Predict(feature);

        // ═══════════════════════════════════════════════════════════
        // 실시간 피처 생성 (CandleData → SurvivalFeature)
        // ═══════════════════════════════════════════════════════════
        public static SurvivalFeature? BuildRealtimeFeature(
            List<IBinanceKline> candles5m,
            List<IBinanceKline>? candles1m = null)
        {
            if (candles5m == null || candles5m.Count < 20) return null;

            var c = candles5m[^1];
            var window = candles5m.TakeLast(20).ToList();
            decimal close = c.ClosePrice;
            if (close <= 0) return null;

            double rsi = 50, adx = 25, sma20Dist = 0;
            float bbPos = 0.5f, bbWidth = 2, atrRatio = 0.5f, macdHist = 0, stochK = 0.5f;
            try { rsi = IndicatorCalculator.CalculateRSI(window, 14); } catch { }
            try { var bb = IndicatorCalculator.CalculateBB(window, 20, 2); bbPos = bb.Upper > bb.Lower ? (float)(((double)close - bb.Lower) / (bb.Upper - bb.Lower)) : 0.5f; bbWidth = bb.Mid > 0 ? (float)((bb.Upper - bb.Lower) / bb.Mid * 100) : 2; } catch { }
            try { macdHist = (float)IndicatorCalculator.CalculateMACD(window).Hist; } catch { }
            try { var (a, _, _) = IndicatorCalculator.CalculateADX(window, 14); adx = a; } catch { }
            try { double atr = IndicatorCalculator.CalculateATR(window, 14); atrRatio = (float)((double)close > 0 ? atr / (double)close * 100 : 0.5); } catch { }
            try { double sma = IndicatorCalculator.CalculateSMA(window, 20); sma20Dist = sma > 0 ? ((double)close - sma) / sma * 100 : 0; } catch { }
            try { var (sk, _) = IndicatorCalculator.CalculateStochastic(window, 14, 3, 3); stochK = (float)(sk / 100); } catch { }

            double avgVol = window.Average(k => (double)k.Volume);
            float volRatio = avgVol > 0 ? (float)((double)c.Volume / avgVol) : 1;

            int cnt = candles5m.Count;
            float pc1 = cnt >= 2 && candles5m[cnt - 2].ClosePrice > 0 ? (float)((close - candles5m[cnt - 2].ClosePrice) / candles5m[cnt - 2].ClosePrice * 100) : 0;
            float pc3 = cnt >= 4 && candles5m[cnt - 4].ClosePrice > 0 ? (float)((close - candles5m[cnt - 4].ClosePrice) / candles5m[cnt - 4].ClosePrice * 100) : 0;
            float pc6 = cnt >= 7 && candles5m[cnt - 7].ClosePrice > 0 ? (float)((close - candles5m[cnt - 7].ClosePrice) / candles5m[cnt - 7].ClosePrice * 100) : 0;

            // 1분봉 하이브리드
            float m1Vol = 0, m1Accel = 0, m1Rsi = 0.5f;
            if (candles1m != null && candles1m.Count >= 10)
            {
                var last1m = candles1m[^1];
                double avg1m = candles1m.TakeLast(20).Average(k => (double)k.Volume);
                m1Vol = avg1m > 0 ? (float)((double)last1m.Volume / avg1m) : 1;
                try { m1Rsi = (float)(IndicatorCalculator.CalculateRSI(candles1m.TakeLast(14).ToList(), 7) / 100); } catch { }
                if (candles1m.Count >= 3)
                {
                    double d0 = candles1m[^2].ClosePrice > 0 ? (double)(last1m.ClosePrice - candles1m[^2].ClosePrice) / (double)candles1m[^2].ClosePrice * 100 : 0;
                    double d1 = candles1m[^3].ClosePrice > 0 ? (double)(candles1m[^2].ClosePrice - candles1m[^3].ClosePrice) / (double)candles1m[^3].ClosePrice * 100 : 0;
                    m1Accel = (float)(d0 - d1);
                }
            }

            return new SurvivalFeature
            {
                RSI = (float)(rsi / 100), MACD_Hist = macdHist, ADX = (float)(adx / 100),
                BB_Position = bbPos, BB_Width = bbWidth, ATR_Ratio = atrRatio,
                Volume_Ratio = volRatio, Stoch_K = stochK, SMA20_Distance = (float)sma20Dist,
                RSI_Lag1 = (float)(rsi / 100), RSI_Lag3 = (float)(rsi / 100), // 실시간은 근사값
                MACD_Hist_Lag1 = macdHist, Volume_Ratio_Lag1 = volRatio, Volume_Ratio_Lag3 = volRatio,
                Price_Change_1 = pc1, Price_Change_3 = pc3, Price_Change_6 = pc6,
                M1_Volume_Surge = m1Vol, M1_Price_Accel = m1Accel, M1_RSI = m1Rsi,
                OI_Change = 0, Funding_Rate = 0, HourOfDay = DateTime.Now.Hour
            };
        }

        // ═══════════════════════════════════════════════════════════
        // 모델 로드
        // ═══════════════════════════════════════════════════════════
        public void TryLoadModels()
        {
            try
            {
                if (File.Exists(MajorModelPath))
                {
                    _majorModel = _mlContext.Model.Load(MajorModelPath, out _);
                    _majorEngine = _mlContext.Model.CreatePredictionEngine<SurvivalFeature, SurvivalPrediction>(_majorModel);
                    OnLog?.Invoke("🧠 [Survival Major] 모델 로드 완료");
                }
                if (File.Exists(PumpModelPath))
                {
                    _pumpModel = _mlContext.Model.Load(PumpModelPath, out _);
                    _pumpEngine = _mlContext.Model.CreatePredictionEngine<SurvivalFeature, SurvivalPrediction>(_pumpModel);
                    OnLog?.Invoke("🧠 [Survival Pump] 모델 로드 완료");
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ Survival 모델 로드 실패: {ex.Message}"); }
        }
    }
}
