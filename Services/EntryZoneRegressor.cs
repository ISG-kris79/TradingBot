using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.8.1] Entry Zone Regressor — Part B 라이브 활용
    ///
    /// 목적:
    ///   - 진입 시점에 최적 TP(익절가) 및 SL(손절가) 오프셋을 ML로 예측
    ///   - 고정 %TP/SL 대신 학습된 분포 기반 동적 TP/SL 제공
    ///
    /// 학습 데이터 생성:
    ///   - 과거 CandleData에서 각 시점을 "가상 진입점"으로 간주
    ///   - 향후 N봉(예: 48봉 = 4h) 관찰
    ///     → optimal TP = 현재 대비 최고가 상승 %
    ///     → optimal SL = 현재 대비 최저가 하락 %
    ///   - EntryZoneCollector가 수집하는 실시간 라벨도 추후 병합 가능
    ///
    /// 모델:
    ///   - 2개 내부 LightGBM Regressor (TP, SL 각각)
    ///   - ML.NET 기본 Multi-Output이 없어서 분리 학습
    /// </summary>
    public class EntryZoneRegressor
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _tpModel;
        private ITransformer? _slModel;
        private PredictionEngine<EntryZoneFeature, EntryZonePrediction>? _tpEngine;
        private PredictionEngine<EntryZoneFeature, EntryZonePrediction>? _slEngine;
        private readonly object _lock = new();

        private readonly ConcurrentQueue<EntryZoneFeature> _trainingBuffer = new();
        private const int MinTrainingSamples = 100;
        private const int MaxTrainingSamples = 30_000;

        private static readonly string ModelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "Models");
        private static readonly string TpModelPath = Path.Combine(ModelDir, "entry_zone_tp.zip");
        private static readonly string SlModelPath = Path.Combine(ModelDir, "entry_zone_sl.zip");

        public event Action<string>? OnLog;
        public bool IsModelReady => _tpEngine != null && _slEngine != null;

        public EntryZoneRegressor()
        {
            Directory.CreateDirectory(ModelDir);
            TryLoadModel();
        }

        /// <summary>과거 CandleData에서 가상 포지션 기반 학습 데이터 생성</summary>
        public int GenerateTrainingDataFromCandles(string symbol, List<CandleData> candles)
        {
            if (candles == null || candles.Count < 100) return 0;

            const int lookAheadBars = 48;       // 4h = 48봉 × 5m 관찰
            const int featureLookback = 30;

            int added = 0;
            for (int t = featureLookback; t < candles.Count - lookAheadBars; t++)
            {
                var baseCandle = candles[t];
                float basePrice = (float)baseCandle.Close;
                if (basePrice <= 0) continue;

                // 향후 구간 내 최고/최저 찾기
                float futureHigh = basePrice;
                float futureLow = basePrice;
                for (int k = 1; k <= lookAheadBars; k++)
                {
                    var c = candles[t + k];
                    if ((float)c.High > futureHigh) futureHigh = (float)c.High;
                    if ((float)c.Low < futureLow) futureLow = (float)c.Low;
                }

                float tpOffsetPct = (futureHigh - basePrice) / basePrice * 100f;
                float slOffsetPct = (basePrice - futureLow) / basePrice * 100f;

                // 유효 범위 필터 — 극단값 제거
                if (tpOffsetPct < 0.1f || tpOffsetPct > 20f) continue;
                if (slOffsetPct < 0.1f || slOffsetPct > 20f) continue;

                var window = candles.GetRange(t - featureLookback + 1, featureLookback);
                var feat = BuildFeature(window, baseCandle, tpOffsetPct, slOffsetPct);
                if (feat == null) continue;

                _trainingBuffer.Enqueue(feat);
                added++;
                while (_trainingBuffer.Count > MaxTrainingSamples)
                    _trainingBuffer.TryDequeue(out _);
            }

            OnLog?.Invoke($"[EntryZoneRegressor] {symbol} → {added}개 샘플 추가 (총 버퍼 {_trainingBuffer.Count})");
            return added;
        }

        private EntryZoneFeature? BuildFeature(List<CandleData> window, CandleData baseCandle, float tpTarget, float slTarget)
        {
            if (window.Count < 14) return null;

            // RSI 14
            double gain = 0, loss = 0;
            for (int i = window.Count - 14; i < window.Count; i++)
            {
                if (i <= 0) continue;
                double diff = (double)(window[i].Close - window[i - 1].Close);
                if (diff > 0) gain += diff; else loss += -diff;
            }
            double avgGain = gain / 14.0, avgLoss = loss / 14.0;
            float rsi = (avgLoss == 0) ? 70f : (float)(100.0 - (100.0 / (1.0 + avgGain / avgLoss)));

            // ATR 14
            double tr = 0;
            for (int i = window.Count - 14; i < window.Count; i++)
            {
                if (i <= 0) continue;
                double h = (double)window[i].High;
                double l = (double)window[i].Low;
                double pc = (double)window[i - 1].Close;
                tr += Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            }
            float atr = (float)(tr / 14.0);

            double avgVol = window.Take(window.Count - 1).Average(c => (double)c.Volume);
            float volRatio = avgVol > 0 ? (float)((double)baseCandle.Volume / avgVol) : 1f;

            float momentum = 0f;
            if (window.Count >= 6 && window[^6].Close > 0)
                momentum = (float)(((double)baseCandle.Close / (double)window[^6].Close - 1.0) * 100.0);

            var last20 = window.TakeLast(20).ToList();
            double minC = (double)last20.Min(c => c.Close);
            double maxC = (double)last20.Max(c => c.Close);
            float bbPos = (maxC - minC) > 0
                ? (float)(((double)baseCandle.Close - minC) / (maxC - minC))
                : 0.5f;

            var bodies = window.TakeLast(10).Select(c => (double)Math.Abs((double)(c.Close - c.Open))).ToList();
            double meanBodySize = bodies.Average();
            float volatility = (float)Math.Sqrt(bodies.Average(b => (b - meanBodySize) * (b - meanBodySize)));

            return new EntryZoneFeature
            {
                RSI = rsi,
                BBPosition = bbPos,
                ATR = atr,
                VolumeRatio = volRatio,
                Momentum = momentum,
                Volatility = volatility,
                HourOfDay = baseCandle.OpenTime.Hour,
                TpOffsetPct = tpTarget,
                SlOffsetPct = slTarget
            };
        }

        /// <summary>TP, SL 각각 분리 학습</summary>
        public async Task<bool> TrainAsync()
        {
            if (_trainingBuffer.Count < MinTrainingSamples)
            {
                OnLog?.Invoke($"[EntryZoneRegressor] 학습 데이터 부족: {_trainingBuffer.Count}/{MinTrainingSamples}");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var data = _trainingBuffer.ToArray().ToList();
                    OnLog?.Invoke($"[EntryZoneRegressor] 학습 시작: {data.Count}개 샘플");

                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                    // TP 모델
                    var tpPipeline = _mlContext.Transforms.Concatenate("Features",
                            nameof(EntryZoneFeature.RSI),
                            nameof(EntryZoneFeature.BBPosition),
                            nameof(EntryZoneFeature.ATR),
                            nameof(EntryZoneFeature.VolumeRatio),
                            nameof(EntryZoneFeature.Momentum),
                            nameof(EntryZoneFeature.Volatility),
                            nameof(EntryZoneFeature.HourOfDay))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            labelColumnName: nameof(EntryZoneFeature.TpOffsetPct),
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 10,
                            numberOfIterations: 200,
                            learningRate: 0.05));

                    var tpModel = tpPipeline.Fit(split.TrainSet);
                    var tpMetrics = _mlContext.Regression.Evaluate(
                        tpModel.Transform(split.TestSet),
                        labelColumnName: nameof(EntryZoneFeature.TpOffsetPct));

                    // SL 모델 (동일 피처, 다른 레이블)
                    var slPipeline = _mlContext.Transforms.Concatenate("Features",
                            nameof(EntryZoneFeature.RSI),
                            nameof(EntryZoneFeature.BBPosition),
                            nameof(EntryZoneFeature.ATR),
                            nameof(EntryZoneFeature.VolumeRatio),
                            nameof(EntryZoneFeature.Momentum),
                            nameof(EntryZoneFeature.Volatility),
                            nameof(EntryZoneFeature.HourOfDay))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            labelColumnName: nameof(EntryZoneFeature.SlOffsetPct),
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 10,
                            numberOfIterations: 200,
                            learningRate: 0.05));

                    var slModel = slPipeline.Fit(split.TrainSet);
                    var slMetrics = _mlContext.Regression.Evaluate(
                        slModel.Transform(split.TestSet),
                        labelColumnName: nameof(EntryZoneFeature.SlOffsetPct));

                    lock (_lock)
                    {
                        _tpModel = tpModel;
                        _slModel = slModel;
                        _tpEngine?.Dispose();
                        _slEngine?.Dispose();
                        _tpEngine = _mlContext.Model.CreatePredictionEngine<EntryZoneFeature, EntryZonePrediction>(tpModel);
                        _slEngine = _mlContext.Model.CreatePredictionEngine<EntryZoneFeature, EntryZonePrediction>(slModel);
                        _mlContext.Model.Save(tpModel, dataView.Schema, TpModelPath);
                        _mlContext.Model.Save(slModel, dataView.Schema, SlModelPath);
                    }

                    OnLog?.Invoke($"[EntryZoneRegressor] 학습 완료 | TP R²={tpMetrics.RSquared:F3} MAE={tpMetrics.MeanAbsoluteError:F2}% | SL R²={slMetrics.RSquared:F3} MAE={slMetrics.MeanAbsoluteError:F2}%");
                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[EntryZoneRegressor] 학습 실패: {ex.Message}");
                    return false;
                }
            });
        }

        public bool TryLoadModel()
        {
            try
            {
                if (!File.Exists(TpModelPath) || !File.Exists(SlModelPath)) return false;
                lock (_lock)
                {
                    _tpModel = _mlContext.Model.Load(TpModelPath, out _);
                    _slModel = _mlContext.Model.Load(SlModelPath, out _);
                    _tpEngine?.Dispose();
                    _slEngine?.Dispose();
                    _tpEngine = _mlContext.Model.CreatePredictionEngine<EntryZoneFeature, EntryZonePrediction>(_tpModel);
                    _slEngine = _mlContext.Model.CreatePredictionEngine<EntryZoneFeature, EntryZonePrediction>(_slModel);
                }
                OnLog?.Invoke("[EntryZoneRegressor] 기존 모델 로드 완료");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[EntryZoneRegressor] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 진입 시점 피처로 (TP offset %, SL offset %) 예측.
        /// 반환 값은 모두 양수 %:
        ///   tpOffsetPct  → LONG 기준 익절 %, SHORT 기준도 동일 의미 (기대 수익폭)
        ///   slOffsetPct  → LONG 기준 손절 %, SHORT 기준도 동일 의미 (기대 손실폭)
        /// </summary>
        public (float TpPct, float SlPct)? PredictTpSl(
            float rsi, float bbPos, float atr, float volRatio,
            float momentum, float volatility, int hourOfDay)
        {
            lock (_lock)
            {
                if (_tpEngine == null || _slEngine == null) return null;
            }

            try
            {
                var feat = new EntryZoneFeature
                {
                    RSI = rsi,
                    BBPosition = bbPos,
                    ATR = atr,
                    VolumeRatio = volRatio,
                    Momentum = momentum,
                    Volatility = volatility,
                    HourOfDay = hourOfDay
                };

                EntryZonePrediction tpPred, slPred;
                lock (_lock)
                {
                    tpPred = _tpEngine!.Predict(feat);
                    slPred = _slEngine!.Predict(feat);
                }

                if (float.IsNaN(tpPred.Score) || float.IsNaN(slPred.Score))
                    return null;

                // 합리적 clamp
                float tp = Math.Clamp(tpPred.Score, 0.3f, 10f);
                float sl = Math.Clamp(slPred.Score, 0.3f, 10f);
                return (tp, sl);
            }
            catch
            {
                return null;
            }
        }

        public int BufferedSampleCount => _trainingBuffer.Count;
    }

    public class EntryZoneFeature
    {
        public float RSI { get; set; }
        public float BBPosition { get; set; }
        public float ATR { get; set; }
        public float VolumeRatio { get; set; }
        public float Momentum { get; set; }
        public float Volatility { get; set; }
        public float HourOfDay { get; set; }

        // 레이블 (2개 모델이 각각 사용)
        public float TpOffsetPct { get; set; }
        public float SlOffsetPct { get; set; }
    }

    public class EntryZonePrediction
    {
        [ColumnName("Score")]
        public float Score { get; set; }
    }
}
