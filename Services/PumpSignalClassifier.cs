using System;
using System.Collections.Concurrent;
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
    /// [v4.5.8] PUMP 모델 타입 — 일반진입 vs 급등진입 분리
    /// </summary>
    public enum PumpSignalType
    {
        /// <summary>일반 진입 — 완만한 상승 추세 포착 (+1.5% / 30분)</summary>
        Normal,
        /// <summary>급등 진입 — 순간 폭발 포착 (+3% / 10분)</summary>
        Spike
    }

    /// <summary>
    /// PUMP 코인 전용 ML 모델 — 급등 패턴 특화 이진 분류
    /// [v4.5.8] Normal/Spike 모델 분리 지원
    /// [v3.0.9] 규칙 기반 PumpScanStrategy를 ML로 보강
    /// </summary>
    public class PumpSignalClassifier : IDisposable
    {
        private readonly MLContext _mlContext;
        private ITransformer? _model;
        private PredictionEngine<PumpFeature, PumpPrediction>? _predictionEngine;
        private readonly object _predictLock = new();
        private bool _disposed;
        private readonly PumpSignalType _signalType;

        // 학습 데이터 버퍼
        private readonly ConcurrentQueue<PumpFeature> _trainingBuffer = new();
        private const int MinTrainingSamples = 100;
        private const int MaxBufferSize = 10000;
        private DateTime _lastTrainTime = DateTime.MinValue;

        private static readonly string DefaultModelDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "Models");

        /// <summary>타입별 모델 파일명</summary>
        private string ModelFileName => _signalType == PumpSignalType.Spike
            ? "pump_signal_spike.zip"
            : "pump_signal_normal.zip";

        public bool IsModelLoaded => _model != null && _predictionEngine != null;
        public int BufferCount => _trainingBuffer.Count;
        public PumpSignalType SignalType => _signalType;

        public event Action<string>? OnLog;

        /// <summary>라벨링 기준: N봉 후 가격변동률 +X% 이상이면 진입</summary>
        public float EntryThresholdPct { get; set; }
        /// <summary>라벨링 기준 봉 수 (5분봉 기준)</summary>
        public int LookAheadCandles { get; set; }

        public PumpSignalClassifier(PumpSignalType signalType = PumpSignalType.Normal)
        {
            _mlContext = new MLContext(seed: 42);
            Directory.CreateDirectory(DefaultModelDir);
            _signalType = signalType;

            // [v4.5.8] 타입별 라벨링 기준
            if (signalType == PumpSignalType.Spike)
            {
                EntryThresholdPct = 3.0f;  // +3% 이상
                LookAheadCandles = 2;       // 10분 (5분봉 × 2)
            }
            else
            {
                EntryThresholdPct = 1.5f;  // +1.5% 이상
                LookAheadCandles = 6;       // 30분 (5분봉 × 6)
            }
        }

        // ═══════════════════════════════════════════
        // 피처 추출 (5분봉 리스트에서)
        // ═══════════════════════════════════════════

        /// <summary>5분봉 캔들 리스트에서 PUMP 전용 피처 추출</summary>
        public static PumpFeature? ExtractFeature(List<IBinanceKline> candles, int index = -1)
        {
            if (candles == null || candles.Count < 30)
                return null;

            int idx = index >= 0 ? index : candles.Count - 1;
            if (idx < 20 || idx >= candles.Count)
                return null;

            var current = candles[idx];
            decimal close = current.ClosePrice;
            decimal open = current.OpenPrice;
            decimal high = current.HighPrice;
            decimal low = current.LowPrice;

            if (close <= 0 || open <= 0)
                return null;

            // 거래량 급증
            var recent3 = candles.Skip(idx - 2).Take(3).ToList();
            var recent5 = candles.Skip(idx - 4).Take(5).ToList();
            var prev10 = candles.Skip(Math.Max(0, idx - 12)).Take(10).ToList();
            var prev20 = candles.Skip(Math.Max(0, idx - 24)).Take(20).ToList();

            double avgVol3 = recent3.Average(k => (double)k.Volume);
            double avgVol5 = recent5.Average(k => (double)k.Volume);
            double avgVolPrev10 = prev10.Count > 0 ? prev10.Average(k => (double)k.Volume) : 1;
            double avgVolPrev20 = prev20.Count > 0 ? prev20.Average(k => (double)k.Volume) : 1;

            float volSurge3 = avgVolPrev10 > 0 ? (float)(avgVol3 / avgVolPrev10) : 1f;
            float volSurge5 = avgVolPrev20 > 0 ? (float)(avgVol5 / avgVolPrev20) : 1f;

            // 가격 변화율
            decimal close3Ago = candles[idx - 3].ClosePrice;
            decimal close5Ago = candles[idx - 5].ClosePrice;
            decimal close10Ago = idx >= 10 ? candles[idx - 10].ClosePrice : close5Ago;

            float priceChange3 = close3Ago > 0 ? (float)((close - close3Ago) / close3Ago * 100) : 0f;
            float priceChange5 = close5Ago > 0 ? (float)((close - close5Ago) / close5Ago * 100) : 0f;
            float priceChange10 = close10Ago > 0 ? (float)((close - close10Ago) / close10Ago * 100) : 0f;

            // RSI (간이 계산)
            float rsi = CalculateRsiSimple(candles, idx, 14);
            float rsi5Ago = idx >= 5 ? CalculateRsiSimple(candles, idx - 5, 14) : rsi;
            float rsiChange = rsi - rsi5Ago;

            // 볼린저 밴드
            var sma20vals = candles.Skip(idx - 19).Take(20).Select(k => (double)k.ClosePrice).ToList();
            double sma20 = sma20vals.Average();
            double stdDev = Math.Sqrt(sma20vals.Sum(v => (v - sma20) * (v - sma20)) / sma20vals.Count);
            double bbUpper = sma20 + 2 * stdDev;
            double bbLower = sma20 - 2 * stdDev;
            double bbWidth = sma20 > 0 ? (bbUpper - bbLower) / sma20 * 100 : 0;
            double bbBreakout = (bbUpper - bbLower) > 0 ? ((double)close - bbUpper) / (bbUpper - bbLower) : 0;

            // MACD 히스토그램
            double ema12 = CalculateEmaSimple(candles, idx, 12);
            double ema26 = CalculateEmaSimple(candles, idx, 26);
            double macdLine = ema12 - ema26;
            double ema12Prev = CalculateEmaSimple(candles, idx - 1, 12);
            double ema26Prev = CalculateEmaSimple(candles, idx - 1, 26);
            double macdPrev = ema12Prev - ema26Prev;
            float macdHist = (float)(macdLine * 0.7); // 근사 히스토그램
            float macdHistChange = (float)(macdLine - macdPrev);

            // ATR 비율
            var atrRecent = candles.Skip(idx - 4).Take(5).Select(k => (double)(k.HighPrice - k.LowPrice)).Average();
            var atrPrev = candles.Skip(Math.Max(0, idx - 24)).Take(20).Select(k => (double)(k.HighPrice - k.LowPrice)).Average();
            float atrRatio = atrPrev > 0 ? (float)(atrRecent / atrPrev) : 1f;

            // Higher Lows 카운트
            int higherLows = 0;
            for (int i = idx - 1; i >= Math.Max(1, idx - 5); i--)
            {
                if (candles[i].LowPrice > candles[i - 1].LowPrice)
                    higherLows++;
                else break;
            }

            // 연속 양봉 수
            int consecBullish = 0;
            for (int i = idx; i >= Math.Max(0, idx - 9); i--)
            {
                if (candles[i].ClosePrice > candles[i].OpenPrice)
                    consecBullish++;
                else break;
            }

            // 캔들 패턴 평균 (최근 3봉)
            float bodyRatioAvg = 0f, upperShadowAvg = 0f;
            foreach (var k in recent3)
            {
                decimal range = k.HighPrice - k.LowPrice;
                if (range > 0)
                {
                    bodyRatioAvg += (float)(Math.Abs(k.ClosePrice - k.OpenPrice) / range);
                    upperShadowAvg += (float)((k.HighPrice - Math.Max(k.OpenPrice, k.ClosePrice)) / range);
                }
            }
            bodyRatioAvg /= 3f;
            upperShadowAvg /= 3f;

            // SMA20 이격도
            float priceVsSma20 = sma20 > 0 ? (float)(((double)close - sma20) / sma20 * 100) : 0f;

            return new PumpFeature
            {
                Volume_Surge_3 = Sanitize(volSurge3),
                Volume_Surge_5 = Sanitize(volSurge5),
                Price_Change_3 = Sanitize(priceChange3),
                Price_Change_5 = Sanitize(priceChange5),
                Price_Change_10 = Sanitize(priceChange10),
                RSI = Sanitize(rsi),
                RSI_Change = Sanitize(rsiChange),
                BB_Width = Sanitize((float)bbWidth),
                BB_Breakout = Sanitize((float)bbBreakout),
                MACD_Hist = Sanitize(macdHist),
                MACD_Hist_Change = Sanitize(macdHistChange),
                ATR_Ratio = Sanitize(atrRatio),
                Higher_Lows_Count = higherLows,
                Consec_Bullish = consecBullish,
                Body_Ratio_Avg = Sanitize(bodyRatioAvg),
                Upper_Shadow_Avg = Sanitize(upperShadowAvg),
                OI_Change_Pct = 0f, // 실시간에서만 채움
                Price_vs_SMA20 = Sanitize(priceVsSma20)
            };
        }

        // ═══════════════════════════════════════════
        // 라벨링 + 학습 데이터 수집
        // ═══════════════════════════════════════════

        /// <summary>캔들 리스트에서 학습용 라벨링된 데이터셋 생성</summary>
        public List<PumpFeature> LabelCandleData(List<IBinanceKline> candles)
        {
            if (candles == null || candles.Count < 30 + LookAheadCandles)
                return new List<PumpFeature>();

            var dataset = new List<PumpFeature>();
            for (int i = 20; i < candles.Count - LookAheadCandles; i++)
            {
                var feature = ExtractFeature(candles, i);
                if (feature == null) continue;

                decimal futureClose = candles[i + LookAheadCandles].ClosePrice;
                decimal currentClose = candles[i].ClosePrice;
                if (currentClose <= 0) continue;

                float pctChange = (float)((futureClose - currentClose) / currentClose * 100);
                feature.Label = pctChange >= EntryThresholdPct; // +2% 이상이면 진입

                dataset.Add(feature);
            }

            int entries = dataset.Count(d => d.Label);
            OnLog?.Invoke($"[PumpML-{_signalType}] 라벨링 완료: {dataset.Count}건 (Entry={entries}, Hold={dataset.Count - entries}) | TP={EntryThresholdPct:F1}% LookAhead={LookAheadCandles}봉");
            return dataset;
        }

        /// <summary>실시간 캔들에서 피처 추출 후 학습 버퍼에 추가 (라벨은 나중에)</summary>
        public void CollectFeature(PumpFeature feature)
        {
            if (feature == null) return;

            _trainingBuffer.Enqueue(feature);
            while (_trainingBuffer.Count > MaxBufferSize)
                _trainingBuffer.TryDequeue(out _);
        }

        // ═══════════════════════════════════════════
        // 학습
        // ═══════════════════════════════════════════

        /// <summary>학습 데이터로 LightGBM 이진분류 모델 학습 + 저장</summary>
        public async Task<PumpModelMetrics> TrainAndSaveAsync(
            List<PumpFeature> trainingData,
            CancellationToken token = default)
        {
            if (trainingData == null || trainingData.Count < MinTrainingSamples)
            {
                OnLog?.Invoke($"[PumpML] 학습 데이터 부족: {trainingData?.Count ?? 0}건 (최소 {MinTrainingSamples}건)");
                return new PumpModelMetrics();
            }

            return await Task.Run(() =>
            {
                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);

                    var featureColumns = new[]
                    {
                        "Volume_Surge_3", "Volume_Surge_5",
                        "Price_Change_3", "Price_Change_5", "Price_Change_10",
                        "RSI", "RSI_Change",
                        "BB_Width", "BB_Breakout",
                        "MACD_Hist", "MACD_Hist_Change",
                        "ATR_Ratio",
                        "Higher_Lows_Count_F", "Consec_Bullish_F",
                        "Body_Ratio_Avg", "Upper_Shadow_Avg",
                        "OI_Change_Pct", "Price_vs_SMA20"
                    };

                    var pipeline = _mlContext.Transforms.Conversion
                        .ConvertType("Higher_Lows_Count_F", "Higher_Lows_Count", Microsoft.ML.Data.DataKind.Single)
                        .Append(_mlContext.Transforms.Conversion
                            .ConvertType("Consec_Bullish_F", "Consec_Bullish", Microsoft.ML.Data.DataKind.Single))
                        .Append(_mlContext.Transforms.Concatenate("Features", featureColumns))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                            labelColumnName: "Label",
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 5,
                            learningRate: 0.1,
                            numberOfIterations: 200));

                    OnLog?.Invoke($"[PumpML-{_signalType}] LightGBM 학습 시작 ({trainingData.Count}건, 피처 18개)...");
                    _model = pipeline.Fit(split.TrainSet);

                    var predictions = _model.Transform(split.TestSet);
                    var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");

                    var result = new PumpModelMetrics
                    {
                        Accuracy = metrics.Accuracy,
                        F1Score = metrics.F1Score,
                        AUC = metrics.AreaUnderRocCurve,
                        SampleCount = trainingData.Count,
                        TrainedAt = DateTime.Now
                    };

                    OnLog?.Invoke($"[PumpML-{_signalType}] 학습 완료 | Acc={metrics.Accuracy:P2}, F1={metrics.F1Score:F3}, AUC={metrics.AreaUnderRocCurve:F3}");

                    string modelPath = Path.Combine(DefaultModelDir, ModelFileName);
                    _mlContext.Model.Save(_model, dataView.Schema, modelPath);
                    OnLog?.Invoke($"[PumpML-{_signalType}] 모델 저장: {modelPath}");

                    lock (_predictLock)
                    {
                        _predictionEngine?.Dispose();
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<PumpFeature, PumpPrediction>(_model);
                    }

                    _lastTrainTime = DateTime.Now;
                    return result;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[PumpML] 학습 실패: {ex.Message}");
                    return new PumpModelMetrics();
                }
            }, token);
        }

        /// <summary>저장된 모델 로드</summary>
        public bool LoadModel()
        {
            try
            {
                string modelPath = Path.Combine(DefaultModelDir, ModelFileName);
                if (!File.Exists(modelPath))
                {
                    OnLog?.Invoke($"[PumpML-{_signalType}] 모델 파일 없음 — 학습 필요");
                    return false;
                }

                _model = _mlContext.Model.Load(modelPath, out _);
                lock (_predictLock)
                {
                    _predictionEngine?.Dispose();
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<PumpFeature, PumpPrediction>(_model);
                }

                OnLog?.Invoke($"[PumpML-{_signalType}] 모델 로드 완료: {modelPath}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[PumpML-{_signalType}] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════
        // 추론
        // ═══════════════════════════════════════════

        /// <summary>현재 캔들 기반 PUMP 진입 확률 예측</summary>
        public PumpPrediction? Predict(PumpFeature feature)
        {
            if (!IsModelLoaded || feature == null)
                return null;

            try
            {
                lock (_predictLock)
                {
                    return _predictionEngine!.Predict(feature);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[PumpML] 추론 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>5분봉 리스트에서 직접 추론</summary>
        public PumpPrediction? PredictFromCandles(List<IBinanceKline> candles)
        {
            var feature = ExtractFeature(candles);
            return feature != null ? Predict(feature) : null;
        }

        // ═══════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════

        private static float CalculateRsiSimple(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period) return 50f;

            double gainSum = 0, lossSum = 0;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
            {
                double change = (double)(candles[i].ClosePrice - candles[i - 1].ClosePrice);
                if (change > 0) gainSum += change;
                else lossSum -= change;
            }

            double avgGain = gainSum / period;
            double avgLoss = lossSum / period;
            if (avgLoss == 0) return 100f;

            double rs = avgGain / avgLoss;
            return (float)(100 - 100 / (1 + rs));
        }

        private static double CalculateEmaSimple(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period) return (double)candles[endIdx].ClosePrice;

            double multiplier = 2.0 / (period + 1);
            double ema = (double)candles[endIdx - period].ClosePrice;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
            {
                ema = ((double)candles[i].ClosePrice - ema) * multiplier + ema;
            }
            return ema;
        }

        private static float Sanitize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _predictionEngine?.Dispose();
            _disposed = true;
        }
    }

    // ═══════════════════════════════════════════
    // 데이터 모델
    // ═══════════════════════════════════════════

    /// <summary>PUMP 전용 피처 (18개)</summary>
    public class PumpFeature
    {
        [ColumnName("Label")]
        public bool Label { get; set; } // true=진입, false=관망

        // 거래량 급증
        public float Volume_Surge_3 { get; set; }
        public float Volume_Surge_5 { get; set; }

        // 가격 변화율
        public float Price_Change_3 { get; set; }
        public float Price_Change_5 { get; set; }
        public float Price_Change_10 { get; set; }

        // 기본 지표
        public float RSI { get; set; }
        public float RSI_Change { get; set; }

        // 볼린저
        public float BB_Width { get; set; }
        public float BB_Breakout { get; set; }

        // MACD
        public float MACD_Hist { get; set; }
        public float MACD_Hist_Change { get; set; }

        // 변동성
        public float ATR_Ratio { get; set; }

        // 구조 패턴
        public int Higher_Lows_Count { get; set; }
        public int Consec_Bullish { get; set; }

        // 캔들 패턴
        public float Body_Ratio_Avg { get; set; }
        public float Upper_Shadow_Avg { get; set; }

        // 기타
        public float OI_Change_Pct { get; set; }
        public float Price_vs_SMA20 { get; set; }
    }

    /// <summary>PUMP ML 예측 결과</summary>
    public class PumpPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool ShouldEnter { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }
    }

    /// <summary>PUMP ML 학습 메트릭</summary>
    public class PumpModelMetrics
    {
        public double Accuracy { get; set; }
        public double F1Score { get; set; }
        public double AUC { get; set; }
        public int SampleCount { get; set; }
        public DateTime TrainedAt { get; set; }
    }
}
