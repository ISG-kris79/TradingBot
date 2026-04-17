using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Microsoft.ML;
using Microsoft.ML.Data;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.0] 예측형 Forecaster 베이스 — Classifier(기회있음) + Regressor B(시점) + Regressor C(가격)
    /// 하위 클래스: PumpForecaster / MajorForecaster / SpikeForecaster
    /// </summary>
    public abstract class OpportunityForecaster : IDisposable
    {
        protected readonly MLContext _mlContext;
        private ITransformer? _classifierA;   // 기회 확률
        private ITransformer? _regressorB;    // 몇 봉 후 (Offset bars)
        private ITransformer? _regressorC;    // 현재가 대비 % (Price offset pct)

        private PredictionEngine<ForecastFeature, ForecastClassifierPrediction>? _engineA;
        private PredictionEngine<ForecastFeature, ForecastRegressorPrediction>? _engineB;
        private PredictionEngine<ForecastFeature, ForecastRegressorPrediction>? _engineC;

        private readonly object _predictLock = new();
        private bool _disposed;

        protected static readonly string ModelDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "Models");

        public abstract string ModelPrefix { get; }        // 예: "forecast_pump"
        public abstract int FutureWindowBars { get; }      // 예측 window (봉 수)
        public abstract float TargetProfitPct { get; }     // 최적 진입점 찾을 때 필요 최소 수익률
        public abstract float MaxDrawdownPct { get; }      // 최적 진입 후 허용 드로다운
        public abstract float MinConfidence { get; }       // 진입 최소 신뢰도

        public bool IsModelLoaded => _engineA != null && _engineB != null && _engineC != null;

        public event Action<string>? OnLog;

        protected OpportunityForecaster()
        {
            _mlContext = new MLContext(seed: 42);
            Directory.CreateDirectory(ModelDir);
        }

        // ═══════════════════════════════════════════════════════════════
        // 학습 데이터 생성 — 같은 차트를 3개 라벨로 동시 생성
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// [v5.0.7] 라벨링 완화 — 포지티브 샘플 10~20% 확보
        /// 기존 문제: TargetProfitPct + MaxDrawdownPct 두 조건 모두 AND → 포지티브 거의 없음
        /// 개선:
        /// 1. 수익 조건만 체크 (드로다운은 "덜 엄격") → 포지티브 크게 증가
        /// 2. 최적 진입점 = "가장 좋은 수익률 봉" (drawdown 패널티만 최소화)
        /// 3. Regressor 라벨 (B/C) 은 포지티브일 때만 의미 있음
        /// </summary>
        public List<ForecastFeature> LabelCandleData(List<IBinanceKline> candles, string direction = "LONG")
        {
            var dataset = new List<ForecastFeature>();
            if (candles == null || candles.Count < 30 + FutureWindowBars)
                return dataset;

            int futureWindow = FutureWindowBars;
            int oppCount = 0;

            // [v5.10] 드로다운 한도 1.2배 (기존 2.0배에서 축소) — 상승장 SHORT 오라벨 방지
            float relaxedMaxDd = MaxDrawdownPct * 1.2f;

            for (int i = 20; i < candles.Count - futureWindow; i++)
            {
                var feature = ExtractFeature(candles, i);
                if (feature == null) continue;

                decimal currentClose = candles[i].ClosePrice;
                if (currentClose <= 0) continue;

                // [v5.10] 조기 역방향 이탈 체크
                // 진입 신호 후 earlyCheckBars 봉 내에 반대 방향으로 1%+ 이탈 → 라벨 0
                // SHORT 진입 후 즉시 상승 / LONG 진입 후 즉시 하락 패턴 제거
                {
                    int earlyCheckBars = Math.Max(3, futureWindow / 6);
                    bool earlyReversalFail = false;
                    for (int ec = i + 1; ec <= Math.Min(i + earlyCheckBars, candles.Count - 1); ec++)
                    {
                        if (direction == "LONG")
                        {
                            float earlyDown = (float)((currentClose - candles[ec].LowPrice) / currentClose * 100);
                            if (earlyDown > 1.0f) { earlyReversalFail = true; break; }
                        }
                        else
                        {
                            float earlyUp = (float)((candles[ec].HighPrice - currentClose) / currentClose * 100);
                            if (earlyUp > 1.0f) { earlyReversalFail = true; break; }
                        }
                    }
                    if (earlyReversalFail)
                    {
                        feature.LabelA_HasOpportunity = false;
                        feature.LabelB_OffsetBars = 0f;
                        feature.LabelC_PriceOffsetPct = 0f;
                        feature.LabelExpectedProfitPct = 0f;
                        dataset.Add(feature);
                        continue;
                    }
                }

                // 최적 진입 봉 탐색
                int bestJ = -1;
                float bestScore = float.MinValue;
                float bestFutureProfit = 0f;

                for (int j = i + 1; j <= i + futureWindow; j++)
                {
                    decimal entryPrice = candles[j].ClosePrice;
                    if (entryPrice <= 0) continue;

                    // j 진입 후 i+futureWindow 까지 최대 수익/드로다운
                    decimal fMaxH = decimal.MinValue, fMinL = decimal.MaxValue;
                    for (int k = j; k <= i + futureWindow; k++)
                    {
                        if (candles[k].HighPrice > fMaxH) fMaxH = candles[k].HighPrice;
                        if (candles[k].LowPrice < fMinL) fMinL = candles[k].LowPrice;
                    }

                    float futureUpFromJ, futureDdFromJ;
                    if (direction == "LONG")
                    {
                        futureUpFromJ = (float)((fMaxH - entryPrice) / entryPrice * 100);
                        futureDdFromJ = (float)((entryPrice - fMinL) / entryPrice * 100);
                    }
                    else
                    {
                        // SHORT: 수익 = 하락폭, 드로다운 = 상승폭
                        futureUpFromJ = (float)((entryPrice - fMinL) / entryPrice * 100);
                        futureDdFromJ = (float)((fMaxH - entryPrice) / entryPrice * 100);
                    }

                    // [v5.0.7] 수익 조건만 엄격, 드로다운은 완화
                    if (futureUpFromJ < TargetProfitPct) continue;
                    if (futureDdFromJ > relaxedMaxDd) continue;  // 완화: 기존 × 2

                    // risk-adjusted score
                    float score = futureUpFromJ - futureDdFromJ;  // 완화: 2× → 1×
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestJ = j;
                        bestFutureProfit = futureUpFromJ;
                    }
                }

                if (bestJ < 0)
                {
                    // 기회 없음 (Classifier 네거티브 샘플)
                    feature.LabelA_HasOpportunity = false;
                    feature.LabelB_OffsetBars = 0f;
                    feature.LabelC_PriceOffsetPct = 0f;
                    feature.LabelExpectedProfitPct = 0f;
                }
                else
                {
                    decimal entryPriceBest = candles[bestJ].ClosePrice;
                    feature.LabelA_HasOpportunity = true;
                    feature.LabelB_OffsetBars = bestJ - i;
                    feature.LabelC_PriceOffsetPct = (float)((entryPriceBest - currentClose) / currentClose * 100);
                    feature.LabelExpectedProfitPct = bestFutureProfit;
                    oppCount++;
                }

                dataset.Add(feature);
            }

            float ratio = dataset.Count > 0 ? (float)oppCount / dataset.Count * 100 : 0;
            OnLog?.Invoke($"[{ModelPrefix}] [v5.0.7] 라벨링: {dataset.Count}건 (기회={oppCount}={ratio:F1}%) | window={futureWindow} target={TargetProfitPct}% relaxedDd={relaxedMaxDd}% dir={direction}");
            return dataset;
        }

        // ═══════════════════════════════════════════════════════════════
        // 피처 추출 — 하위 클래스에서 override 가능 (기본: 36개 PUMP 피처 재사용)
        // ═══════════════════════════════════════════════════════════════

        public virtual ForecastFeature? ExtractFeature(List<IBinanceKline> candles, int index = -1)
        {
            var pumpFeature = PumpSignalClassifier.ExtractFeature(candles, index);
            if (pumpFeature == null) return null;
            return ForecastFeature.FromPumpFeature(pumpFeature);
        }

        // ═══════════════════════════════════════════════════════════════
        // 학습 — 3개 모델 병렬 학습
        // ═══════════════════════════════════════════════════════════════

        public async Task<ForecastModelMetrics> TrainAndSaveAsync(
            List<ForecastFeature> trainingData,
            CancellationToken token = default)
        {
            if (trainingData == null || trainingData.Count < 100)
            {
                OnLog?.Invoke($"[{ModelPrefix}] 학습 데이터 부족: {trainingData?.Count ?? 0}건");
                return new ForecastModelMetrics();
            }

            return await Task.Run(() =>
            {
                var metrics = new ForecastModelMetrics { SampleCount = trainingData.Count, TrainedAt = DateTime.Now };

                try
                {
                    var featureColumns = ForecastFeature.GetFeatureColumnNames();

                    // 공통 파이프라인 prefix (피처 concat + normalize)
                    var pipelinePrefix = _mlContext.Transforms.Concatenate("Features", featureColumns)
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"));

                    // ─── Model A: Classifier (기회있음 Binary) ───
                    var allDataView = _mlContext.Data.LoadFromEnumerable(trainingData);
                    var splitA = _mlContext.Data.TrainTestSplit(allDataView, testFraction: 0.2, seed: 42);

                    var pipelineA = pipelinePrefix
                        .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                            labelColumnName: "LabelA_HasOpportunity",
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 5,
                            learningRate: 0.1,
                            numberOfIterations: 200));

                    OnLog?.Invoke($"[{ModelPrefix}] [A-Classifier] 학습 시작 ({trainingData.Count}건)...");
                    _classifierA = pipelineA.Fit(splitA.TrainSet);
                    var evalA = _mlContext.BinaryClassification.Evaluate(
                        _classifierA.Transform(splitA.TestSet),
                        labelColumnName: "LabelA_HasOpportunity");
                    metrics.ClassifierAccuracy = evalA.Accuracy;
                    metrics.ClassifierF1 = evalA.F1Score;
                    OnLog?.Invoke($"[{ModelPrefix}] [A] Acc={evalA.Accuracy:P2} F1={evalA.F1Score:F3}");

                    // ─── Model B: Regressor (몇 봉 후) — 기회있는 샘플만 ───
                    var positiveSamples = trainingData.Where(f => f.LabelA_HasOpportunity).ToList();
                    if (positiveSamples.Count >= 50)
                    {
                        var posDataView = _mlContext.Data.LoadFromEnumerable(positiveSamples);
                        var splitB = _mlContext.Data.TrainTestSplit(posDataView, testFraction: 0.2, seed: 42);

                        var pipelineB = pipelinePrefix
                            .Append(_mlContext.Regression.Trainers.LightGbm(
                                labelColumnName: "LabelB_OffsetBars",
                                featureColumnName: "Features",
                                numberOfLeaves: 20,
                                minimumExampleCountPerLeaf: 3,
                                learningRate: 0.1,
                                numberOfIterations: 150));

                        OnLog?.Invoke($"[{ModelPrefix}] [B-Regressor] 학습 시작 ({positiveSamples.Count}건)...");
                        _regressorB = pipelineB.Fit(splitB.TrainSet);
                        var evalB = _mlContext.Regression.Evaluate(
                            _regressorB.Transform(splitB.TestSet),
                            labelColumnName: "LabelB_OffsetBars");
                        metrics.TimeRegressorMAE = evalB.MeanAbsoluteError;
                        metrics.TimeRegressorR2 = evalB.RSquared;
                        OnLog?.Invoke($"[{ModelPrefix}] [B] MAE={evalB.MeanAbsoluteError:F2}봉 R2={evalB.RSquared:F3}");

                        // ─── Model C: Regressor (가격 offset %) ───
                        var pipelineC = pipelinePrefix
                            .Append(_mlContext.Regression.Trainers.LightGbm(
                                labelColumnName: "LabelC_PriceOffsetPct",
                                featureColumnName: "Features",
                                numberOfLeaves: 20,
                                minimumExampleCountPerLeaf: 3,
                                learningRate: 0.1,
                                numberOfIterations: 150));

                        OnLog?.Invoke($"[{ModelPrefix}] [C-Regressor] 학습 시작...");
                        _regressorC = pipelineC.Fit(splitB.TrainSet);
                        var evalC = _mlContext.Regression.Evaluate(
                            _regressorC.Transform(splitB.TestSet),
                            labelColumnName: "LabelC_PriceOffsetPct");
                        metrics.PriceRegressorMAE = evalC.MeanAbsoluteError;
                        metrics.PriceRegressorR2 = evalC.RSquared;
                        OnLog?.Invoke($"[{ModelPrefix}] [C] MAE={evalC.MeanAbsoluteError:F3}% R2={evalC.RSquared:F3}");
                    }
                    else
                    {
                        OnLog?.Invoke($"[{ModelPrefix}] 포지티브 샘플 부족 ({positiveSamples.Count}건) → B/C 스킵");
                    }

                    // 저장
                    SaveModels(allDataView.Schema, positiveSamples.Count >= 50 ?
                        _mlContext.Data.LoadFromEnumerable(positiveSamples).Schema : allDataView.Schema);

                    // Engine 생성
                    lock (_predictLock)
                    {
                        _engineA?.Dispose();
                        _engineB?.Dispose();
                        _engineC?.Dispose();
                        _engineA = _classifierA != null ? _mlContext.Model.CreatePredictionEngine<ForecastFeature, ForecastClassifierPrediction>(_classifierA) : null;
                        _engineB = _regressorB != null ? _mlContext.Model.CreatePredictionEngine<ForecastFeature, ForecastRegressorPrediction>(_regressorB) : null;
                        _engineC = _regressorC != null ? _mlContext.Model.CreatePredictionEngine<ForecastFeature, ForecastRegressorPrediction>(_regressorC) : null;
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[{ModelPrefix}] 학습 실패: {ex.Message}");
                }

                return metrics;
            }, token);
        }

        private void SaveModels(DataViewSchema schemaA, DataViewSchema schemaBC)
        {
            try
            {
                if (_classifierA != null)
                    _mlContext.Model.Save(_classifierA, schemaA, Path.Combine(ModelDir, $"{ModelPrefix}_A.zip"));
                if (_regressorB != null)
                    _mlContext.Model.Save(_regressorB, schemaBC, Path.Combine(ModelDir, $"{ModelPrefix}_B.zip"));
                if (_regressorC != null)
                    _mlContext.Model.Save(_regressorC, schemaBC, Path.Combine(ModelDir, $"{ModelPrefix}_C.zip"));
                OnLog?.Invoke($"[{ModelPrefix}] 모델 3개 저장 완료: {ModelDir}");
            }
            catch (Exception ex) { OnLog?.Invoke($"[{ModelPrefix}] 저장 실패: {ex.Message}"); }
        }

        public bool LoadModels()
        {
            try
            {
                string pathA = Path.Combine(ModelDir, $"{ModelPrefix}_A.zip");
                string pathB = Path.Combine(ModelDir, $"{ModelPrefix}_B.zip");
                string pathC = Path.Combine(ModelDir, $"{ModelPrefix}_C.zip");

                if (!File.Exists(pathA)) { OnLog?.Invoke($"[{ModelPrefix}] 모델 없음 → 학습 필요"); return false; }

                _classifierA = _mlContext.Model.Load(pathA, out _);
                if (File.Exists(pathB)) _regressorB = _mlContext.Model.Load(pathB, out _);
                if (File.Exists(pathC)) _regressorC = _mlContext.Model.Load(pathC, out _);

                lock (_predictLock)
                {
                    _engineA?.Dispose(); _engineB?.Dispose(); _engineC?.Dispose();
                    _engineA = _mlContext.Model.CreatePredictionEngine<ForecastFeature, ForecastClassifierPrediction>(_classifierA);
                    _engineB = _regressorB != null ? _mlContext.Model.CreatePredictionEngine<ForecastFeature, ForecastRegressorPrediction>(_regressorB) : null;
                    _engineC = _regressorC != null ? _mlContext.Model.CreatePredictionEngine<ForecastFeature, ForecastRegressorPrediction>(_regressorC) : null;
                }
                OnLog?.Invoke($"[{ModelPrefix}] 모델 3개 로드 완료");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[{ModelPrefix}] 로드 실패: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 예측 — 3개 모델 동시 호출
        // ═══════════════════════════════════════════════════════════════

        public ForecastResult Forecast(List<IBinanceKline> candles, string direction = "LONG")
        {
            if (!IsModelLoaded) return ForecastResult.NoOpportunity;

            var feature = ExtractFeature(candles);
            if (feature == null) return ForecastResult.NoOpportunity;

            try
            {
                lock (_predictLock)
                {
                    var predA = _engineA!.Predict(feature);
                    if (predA.Probability < MinConfidence || !predA.ShouldEnter)
                        return ForecastResult.NoOpportunity;

                    var predB = _engineB!.Predict(feature);
                    var predC = _engineC!.Predict(feature);

                    int offsetBars = Math.Clamp((int)Math.Round(predB.Score), 0, FutureWindowBars);
                    float priceOffset = Math.Clamp(predC.Score, -5f, 5f);

                    return new ForecastResult
                    {
                        HasOpportunity = true,
                        Probability = predA.Probability,
                        Direction = direction,
                        OffsetBars = offsetBars,
                        PriceOffsetPct = priceOffset,
                        ExpectedProfitPct = Math.Abs(priceOffset) + TargetProfitPct,
                        GeneratedAt = DateTime.Now,
                        SymbolType = ModelPrefix
                    };
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[{ModelPrefix}] 예측 오류: {ex.Message}");
                return ForecastResult.NoOpportunity;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _engineA?.Dispose();
            _engineB?.Dispose();
            _engineC?.Dispose();
            _disposed = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // ML.NET 데이터 모델
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Forecaster 전용 피처 (PumpFeature 36개 재사용 + 3개 라벨)</summary>
    public class ForecastFeature
    {
        // [Label A] Classifier: 기회 있음?
        [ColumnName("LabelA_HasOpportunity")]
        public bool LabelA_HasOpportunity { get; set; }

        // [Label B] Regressor: 몇 봉 후 (Single for ML.NET)
        public float LabelB_OffsetBars { get; set; }

        // [Label C] Regressor: 가격 offset %
        public float LabelC_PriceOffsetPct { get; set; }

        // 디버그용
        public float LabelExpectedProfitPct { get; set; }

        // === 36개 피처 (PumpFeature 복사) ===
        public float Volume_Surge_3 { get; set; }
        public float Volume_Surge_5 { get; set; }
        public float Price_Change_3 { get; set; }
        public float Price_Change_5 { get; set; }
        public float Price_Change_10 { get; set; }
        public float RSI { get; set; }
        public float RSI_Change { get; set; }
        public float BB_Width { get; set; }
        public float BB_Breakout { get; set; }
        public float MACD_Hist { get; set; }
        public float MACD_Hist_Change { get; set; }
        public float ATR_Ratio { get; set; }
        public float Higher_Lows_Count { get; set; }
        public float Consec_Bullish { get; set; }
        public float Body_Ratio_Avg { get; set; }
        public float Upper_Shadow_Avg { get; set; }
        public float OI_Change_Pct { get; set; }
        public float Price_vs_SMA20 { get; set; }
        public float Dist_From_Swing_Low { get; set; }
        public float Bars_Since_Swing_Low { get; set; }
        public float Swing_Low_Depth { get; set; }
        public float Volume_At_Low_Ratio { get; set; }
        public float Lower_Lows_Count_Prev { get; set; }
        public float Structure_Break { get; set; }
        public float MACD_Main { get; set; }
        public float MACD_Signal { get; set; }
        public float ADX { get; set; }
        public float Plus_DI { get; set; }
        public float Minus_DI { get; set; }
        public float Price_To_BB_Mid { get; set; }
        public float Lower_Shadow_Avg { get; set; }
        public float Volume_Change_Pct { get; set; }
        public float Trend_Strength { get; set; }
        public float Fib_Position { get; set; }
        public float Stoch_K { get; set; }
        public float Stoch_D { get; set; }

        // [v5.10] 장기 추세 피처 (MajorForecaster에서만 채움, Pump/Spike는 0)
        /// <summary>60봉(5시간) 수익률 — 장기 상승/하락 추세 방향</summary>
        public float Price_Change_60 { get; set; }
        /// <summary>240봉(20시간) 수익률 — 거시 추세 방향</summary>
        public float Price_Change_240 { get; set; }
        /// <summary>SMA50 기울기 (5봉 전 대비 변화율) — 추세 가속/감속</summary>
        public float EmaSlope_50 { get; set; }

        public static string[] GetFeatureColumnNames() => new[]
        {
            "Volume_Surge_3", "Volume_Surge_5",
            "Price_Change_3", "Price_Change_5", "Price_Change_10",
            "RSI", "RSI_Change", "BB_Width", "BB_Breakout",
            "MACD_Hist", "MACD_Hist_Change", "ATR_Ratio",
            "Higher_Lows_Count", "Consec_Bullish",
            "Body_Ratio_Avg", "Upper_Shadow_Avg",
            "OI_Change_Pct", "Price_vs_SMA20",
            "Dist_From_Swing_Low", "Bars_Since_Swing_Low",
            "Swing_Low_Depth", "Volume_At_Low_Ratio",
            "Lower_Lows_Count_Prev", "Structure_Break",
            "MACD_Main", "MACD_Signal",
            "ADX", "Plus_DI", "Minus_DI",
            "Price_To_BB_Mid", "Lower_Shadow_Avg",
            "Volume_Change_Pct", "Trend_Strength",
            "Fib_Position", "Stoch_K", "Stoch_D",
            // [v5.10] 장기 추세 피처 (3개)
            "Price_Change_60", "Price_Change_240", "EmaSlope_50"
        };

        public static ForecastFeature FromPumpFeature(PumpFeature p)
        {
            return new ForecastFeature
            {
                Volume_Surge_3 = p.Volume_Surge_3,
                Volume_Surge_5 = p.Volume_Surge_5,
                Price_Change_3 = p.Price_Change_3,
                Price_Change_5 = p.Price_Change_5,
                Price_Change_10 = p.Price_Change_10,
                RSI = p.RSI,
                RSI_Change = p.RSI_Change,
                BB_Width = p.BB_Width,
                BB_Breakout = p.BB_Breakout,
                MACD_Hist = p.MACD_Hist,
                MACD_Hist_Change = p.MACD_Hist_Change,
                ATR_Ratio = p.ATR_Ratio,
                Higher_Lows_Count = p.Higher_Lows_Count,
                Consec_Bullish = p.Consec_Bullish,
                Body_Ratio_Avg = p.Body_Ratio_Avg,
                Upper_Shadow_Avg = p.Upper_Shadow_Avg,
                OI_Change_Pct = p.OI_Change_Pct,
                Price_vs_SMA20 = p.Price_vs_SMA20,
                Dist_From_Swing_Low = p.Dist_From_Swing_Low,
                Bars_Since_Swing_Low = p.Bars_Since_Swing_Low,
                Swing_Low_Depth = p.Swing_Low_Depth,
                Volume_At_Low_Ratio = p.Volume_At_Low_Ratio,
                Lower_Lows_Count_Prev = p.Lower_Lows_Count_Prev,
                Structure_Break = p.Structure_Break,
                MACD_Main = p.MACD_Main,
                MACD_Signal = p.MACD_Signal,
                ADX = p.ADX,
                Plus_DI = p.Plus_DI,
                Minus_DI = p.Minus_DI,
                Price_To_BB_Mid = p.Price_To_BB_Mid,
                Lower_Shadow_Avg = p.Lower_Shadow_Avg,
                Volume_Change_Pct = p.Volume_Change_Pct,
                Trend_Strength = p.Trend_Strength,
                Fib_Position = p.Fib_Position,
                Stoch_K = p.Stoch_K,
                Stoch_D = p.Stoch_D,
                // [v5.10] 장기 추세 피처 — Pump/Spike는 0, Major는 ExtractFeature override에서 채움
                Price_Change_60 = 0f,
                Price_Change_240 = 0f,
                EmaSlope_50 = 0f
            };
        }
    }

    public class ForecastClassifierPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool ShouldEnter { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }
    }

    public class ForecastRegressorPrediction
    {
        [ColumnName("Score")]
        public float Score { get; set; }
    }

    public class ForecastModelMetrics
    {
        public double ClassifierAccuracy { get; set; }
        public double ClassifierF1 { get; set; }
        public double TimeRegressorMAE { get; set; }
        public double TimeRegressorR2 { get; set; }
        public double PriceRegressorMAE { get; set; }
        public double PriceRegressorR2 { get; set; }
        public int SampleCount { get; set; }
        public DateTime TrainedAt { get; set; }
    }
}
