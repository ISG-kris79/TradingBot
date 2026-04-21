using Microsoft.Extensions.ML;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.LightGbm;
using Microsoft.ML.Transforms;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TradingBot
{
    /// <summary>
    /// ML.NET 기반 Entry Timing Binary Classification 트레이너
    /// 목표: "지금 진입하면 수익날까?" → 1(진입) / 0(대기)
    /// </summary>
    public class EntryTimingMLTrainer
    {
        private readonly MLContext _mlContext;
        private ITransformer? _model;
        private DataViewSchema? _modelSchema;
        private readonly string _modelPath;
        private readonly string _legacyModelPath;
        private bool _isModelLoaded = false;

        // [WPF최적화 2] PredictionEnginePool — Thread-safe 풀링, lock 제거
        // Microsoft.Extensions.ML 공식 풀: 스레드별 인스턴스 자동 관리
        private PredictionEnginePool<MultiTimeframeEntryFeature, EntryTimingPrediction>? _enginePool;
        // fallback: 풀 생성 실패 시 수동 캐싱
        private PredictionEngine<MultiTimeframeEntryFeature, EntryTimingPrediction>? _cachedEngine;
        private readonly object _engineLock = new();

        public bool IsModelLoaded => _isModelLoaded;

        // [v5.10.76 Phase 3] 모델 variant (메이저/PUMP/SPIKE 분리)
        public enum ModelVariant { Default, Major, Pump, Spike }
        public ModelVariant Variant { get; private set; } = ModelVariant.Default;
        public string ModelDescription => Variant switch
        {
            ModelVariant.Major => "Major (BTC/ETH/SOL/XRP)",
            ModelVariant.Pump => "Pump (일반 알트)",
            ModelVariant.Spike => "Spike (1분봉 초단타)",
            _ => "Default (통합)"
        };

        public EntryTimingMLTrainer(string? modelPath = null)
            : this(ModelVariant.Default, modelPath)
        {
        }

        public EntryTimingMLTrainer(ModelVariant variant, string? modelPath = null)
        {
            _mlContext = new MLContext(seed: 42);
            Variant = variant;
            _legacyModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EntryTimingModel.zip");
            _modelPath = string.IsNullOrWhiteSpace(modelPath)
                ? GetDefaultModelPath(variant)
                : modelPath;

            EnsureModelDirectoryExists();
        }

        private static string GetDefaultModelPath(ModelVariant variant = ModelVariant.Default)
        {
            string modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TradingBot",
                "Models");

            string fileName = variant switch
            {
                ModelVariant.Major => "EntryTimingModel_Major.zip",
                ModelVariant.Pump  => "EntryTimingModel_Pump.zip",
                ModelVariant.Spike => "EntryTimingModel_Spike.zip",
                _ => "EntryTimingModel.zip"
            };
            return Path.Combine(modelDir, fileName);
        }

        private void EnsureModelDirectoryExists()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_modelPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EntryTimingML] 모델 디렉터리 생성 실패: {ex.Message}");
            }
        }

        private string ResolveLoadPath()
        {
            if (File.Exists(_modelPath))
                return _modelPath;

            if (File.Exists(_legacyModelPath))
            {
                try
                {
                    EnsureModelDirectoryExists();
                    File.Copy(_legacyModelPath, _modelPath, overwrite: true);
                    Console.WriteLine($"[EntryTimingML] 레거시 모델을 사용자 경로로 마이그레이션: {_modelPath}");
                    return _modelPath;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EntryTimingML] 레거시 모델 마이그레이션 실패: {ex.Message}");
                    return _legacyModelPath;
                }
            }

            return _modelPath;
        }

        /// <summary>
        /// 학습 데이터로 모델 학습 및 저장
        /// </summary>
        public async Task<ModelMetrics> TrainAndSaveAsync(
            List<MultiTimeframeEntryFeature> trainingData,
            string? validationDataPath = null)
        {
            if (trainingData == null || trainingData.Count < 10)
                throw new ArgumentException($"학습 데이터가 부족합니다 (현재 {trainingData?.Count ?? 0}개, 최소 10개 필요)");

            return await Task.Run(() =>
            {
                Console.WriteLine($"[EntryTimingML] 학습 시작: {trainingData.Count}개 샘플");

                // [v5.10.73 Phase 1-B] 클래스 불균형 2단계 보정 — positive oversampling + negative 다운샘플링
                // 기존 (v5.10.26): negative만 5:1로 다운샘플링 → minority class 학습 부실 지속
                // 개선: 1) positive < 50개면 oversample (bootstrap 3배) 2) 최종 비율 2:1로 강화 3) LightGbm UnbalancedSets=true 병행
                {
                    var positives = trainingData.Where(d => d.ShouldEnter).ToList();
                    var negatives = trainingData.Where(d => !d.ShouldEnter).ToList();
                    if (positives.Count == 0)
                    {
                        Console.WriteLine("[EntryTimingML] ⚠️ positive 샘플 0개 — 기존 모델 유지 (재학습 스킵)");
                        return new ModelMetrics { TrainingSamples = trainingData.Count, Accuracy = -1f };
                    }

                    var rng = new Random(42);
                    int origPositives = positives.Count;

                    // 1) positive < 50개면 bootstrap oversampling (3배 복제)
                    if (positives.Count < 50)
                    {
                        var oversampled = new List<MultiTimeframeEntryFeature>(positives);
                        for (int k = 0; k < 2; k++) // 원본 + 2회 복제 = 3배
                            oversampled.AddRange(positives);
                        positives = oversampled;
                        Console.WriteLine($"[EntryTimingML] positive oversample: {origPositives} → {positives.Count} (bootstrap 3x)");
                    }

                    // 2) negative 다운샘플링 (positive × 2 이내)
                    int targetNeg = positives.Count * 2;
                    if (negatives.Count > targetNeg)
                    {
                        negatives = negatives.OrderBy(_ => rng.Next()).Take(targetNeg).ToList();
                    }

                    trainingData = positives.Concat(negatives).OrderBy(_ => rng.Next()).ToList();
                    Console.WriteLine($"[EntryTimingML] 밸런싱 완료: pos={positives.Count} (orig={origPositives}), neg={negatives.Count}, total={trainingData.Count} (ratio 1:{(double)negatives.Count / Math.Max(1, positives.Count):F1})");
                }

                // 데이터 로드
                var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

                // 학습/검증 분할 (80/20)
                var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                // 데이터 전처리 파이프라인
                var pipeline = BuildPipeline();

                // 모델 학습
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // [Stage1] 학습 전 캐시된 엔진 무효화
                lock (_engineLock) { _cachedEngine?.Dispose(); _cachedEngine = null; }
                _model = pipeline.Fit(split.TrainSet);
                watch.Stop();

                Console.WriteLine($"[EntryTimingML] 학습 완료: {watch.Elapsed.TotalSeconds:F1}초");

                // 모델 평가
                var predictions = _model.Transform(split.TestSet);
                var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");

                PrintMetrics(metrics);

                // 모델 저장
                EnsureModelDirectoryExists();
                _mlContext.Model.Save(_model, dataView.Schema, _modelPath);
                Console.WriteLine($"[EntryTimingML] 모델 저장: {_modelPath}");

                _isModelLoaded = true;
                _modelSchema = dataView.Schema;

                return new ModelMetrics
                {
                    Accuracy = metrics.Accuracy,
                    AUC = metrics.AreaUnderRocCurve,
                    F1Score = metrics.F1Score,
                    Precision = metrics.PositivePrecision,
                    Recall = metrics.PositiveRecall,
                    TrainingSamples = trainingData.Count,
                    TrainingTimeSeconds = watch.Elapsed.TotalSeconds
                };
            });
        }

        // 현재 파이프라인이 기대하는 Feature 수 (BuildPipeline의 featureColumns 길이)
        // 49 기존 + 19 확장(v3.4.2) + 5 휩소(v4.5.2) + 5 하락추세(v4.5.6) + 3 DailyPnl(v4.5.11) + 5 단타지표(v4.6.2) + 4 스퀴즈/ST/Pivot(v4.6.3) + 11 고점학습(v5.10.75) = 101개
        private const int ExpectedFeatureCount = 101;

        /// <summary>
        /// 저장된 모델 로드 (스키마 호환성 검증 포함)
        /// </summary>
        public bool LoadModel()
        {
            try
            {
                string loadPath = ResolveLoadPath();

                if (!File.Exists(loadPath))
                {
                    Console.WriteLine($"[EntryTimingML] 모델 파일 없음: {_modelPath}");
                    return false;
                }

                // [Stage1] 모델 재로드 시 캐시된 엔진 무효화
                lock (_engineLock) { _cachedEngine?.Dispose(); _cachedEngine = null; }

                _model = _mlContext.Model.Load(loadPath, out _modelSchema);

                // 스키마 호환성 검증: Feature 수 확인
                if (_modelSchema != null)
                {
                    int schemaColumnCount = 0;
                    foreach (var col in _modelSchema)
                    {
                        // NoColumn이나 메타 컬럼 제외, 실제 Feature 컬럼만 카운트
                        if (col.Type is Microsoft.ML.Data.NumberDataViewType)
                            schemaColumnCount++;
                    }
                    // Label(Boolean)은 제외하고 Feature만 비교
                    // 스키마에 "Label" + N float columns가 있어야 함
                    if (schemaColumnCount != ExpectedFeatureCount)
                    {
                        Console.WriteLine($"[EntryTimingML] ⚠️ 모델 스키마 불일치: {schemaColumnCount}개 Feature (기대: {ExpectedFeatureCount}개). 모델 삭제 후 재학습 필요.");
                        _model = null;
                        _isModelLoaded = false;
                        // 호환되지 않는 모델 파일 삭제
                        try { File.Delete(_modelPath); } catch { }
                        return false;
                    }
                }

                // 예측 엔진 생성 테스트 (스키마 타입 호환성 최종 검증)
                try
                {
                    var testEngine = _mlContext.Model.CreatePredictionEngine<MultiTimeframeEntryFeature, EntryTimingPrediction>(_model);
                    testEngine.Dispose();
                }
                catch (Exception schemaEx)
                {
                    Console.WriteLine($"[EntryTimingML] ⚠️ 모델 스키마 타입 불일치: {schemaEx.Message}. 모델 삭제 후 재학습 필요.");
                    _model = null;
                    _isModelLoaded = false;
                    try { File.Delete(_modelPath); } catch { }
                    return false;
                }

                _isModelLoaded = true;
                Console.WriteLine($"[EntryTimingML] 모델 로드 성공: {loadPath}");

                // [WPF최적화 2] PredictionEnginePool 초기화 (실패해도 진입에 영향 없음)
                try { InitializeEnginePool(); }
                catch (Exception poolEx)
                {
                    Console.WriteLine($"[EntryTimingML] PredictionEnginePool 초기화 예외 무시: {poolEx.Message}");
                    _enginePool = null;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EntryTimingML] 모델 로드 실패: {ex.Message}");
                // 손상된 모델 파일 삭제
                try { File.Delete(_modelPath); } catch { }
                _model = null;
                _isModelLoaded = false;
                return false;
            }
        }

        /// <summary>
        /// 실시간 예측: 지금 진입할지 말지 판단
        /// [WPF최적화 2] PredictionEnginePool 우선 → 수동 캐싱 폴백
        /// 풀 사용 시 lock 불필요 (Thread-safe), 다중 스레드 동시 추론 가능
        /// </summary>
        public EntryTimingPrediction? Predict(MultiTimeframeEntryFeature feature)
        {
            if (!_isModelLoaded || _model == null)
                return null;

            // 입력 피처 유효성 검사 (NaN/Infinity가 있으면 ML.NET이 0 반환)
            SanitizeFeature(feature);

            EntryTimingPrediction? result = null;

            // 1순위: PredictionEnginePool (Thread-safe, 풀링)
            var pool = _enginePool;
            if (pool != null)
            {
                try { result = pool.Predict(feature); }
                catch { _enginePool = null; }
            }

            // 2순위: 수동 캐싱 (lock 기반 폴백)
            if (result == null)
            {
                try
                {
                    lock (_engineLock)
                    {
                        if (_cachedEngine == null)
                            _cachedEngine = _mlContext.Model.CreatePredictionEngine<MultiTimeframeEntryFeature, EntryTimingPrediction>(_model);
                        result = _cachedEngine.Predict(feature);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EntryTimingML] 예측 실패: {ex.Message}");
                    lock (_engineLock) { _cachedEngine?.Dispose(); _cachedEngine = null; }
                    return null;
                }
            }

            // 결과값 0/NaN 보정: ML.NET이 0을 뱉으면 무효 처리
            if (result != null)
            {
                if (float.IsNaN(result.Probability) || float.IsInfinity(result.Probability))
                    result.Probability = 0f;
                if (float.IsNaN(result.Score) || float.IsInfinity(result.Score))
                    result.Score = 0f;
            }

            return result;
        }

        /// <summary>
        /// 입력 피처의 NaN/Infinity를 0으로 치환
        /// ML.NET은 NaN 입력 시 0 또는 비정상 결과를 반환하므로 사전 제거
        /// </summary>
        private static void SanitizeFeature(MultiTimeframeEntryFeature f)
        {
            // 리플렉션 대신 주요 피처 직접 체크 (성능 우선)
            f.D1_Trend = SanitizeFloat(f.D1_Trend);
            f.D1_RSI = SanitizeFloat(f.D1_RSI);
            f.D1_MACD = SanitizeFloat(f.D1_MACD);
            f.D1_Signal = SanitizeFloat(f.D1_Signal);
            f.D1_BBPosition = SanitizeFloat(f.D1_BBPosition);
            f.D1_Volume_Ratio = SanitizeFloat(f.D1_Volume_Ratio);
            f.H4_Trend = SanitizeFloat(f.H4_Trend);
            f.H4_RSI = SanitizeFloat(f.H4_RSI);
            f.H4_MACD = SanitizeFloat(f.H4_MACD);
            f.H4_Signal = SanitizeFloat(f.H4_Signal);
            f.H4_BBPosition = SanitizeFloat(f.H4_BBPosition);
            f.H4_Volume_Ratio = SanitizeFloat(f.H4_Volume_Ratio);
            f.H4_DistanceToSupport = SanitizeFloat(f.H4_DistanceToSupport);
            f.H4_DistanceToResist = SanitizeFloat(f.H4_DistanceToResist);
            f.H2_Trend = SanitizeFloat(f.H2_Trend);
            f.H2_RSI = SanitizeFloat(f.H2_RSI);
            f.H2_MACD = SanitizeFloat(f.H2_MACD);
            f.H2_Signal = SanitizeFloat(f.H2_Signal);
            f.H2_BBPosition = SanitizeFloat(f.H2_BBPosition);
            f.H2_Volume_Ratio = SanitizeFloat(f.H2_Volume_Ratio);
            f.H2_WavePosition = SanitizeFloat(f.H2_WavePosition);
            f.H1_Trend = SanitizeFloat(f.H1_Trend);
            f.H1_RSI = SanitizeFloat(f.H1_RSI);
            f.H1_MACD = SanitizeFloat(f.H1_MACD);
            f.H1_Signal = SanitizeFloat(f.H1_Signal);
            f.H1_BBPosition = SanitizeFloat(f.H1_BBPosition);
            f.H1_Volume_Ratio = SanitizeFloat(f.H1_Volume_Ratio);
            f.H1_MomentumStrength = SanitizeFloat(f.H1_MomentumStrength);
            f.M15_RSI = SanitizeFloat(f.M15_RSI);
            f.M15_MACD = SanitizeFloat(f.M15_MACD);
            f.M15_Signal = SanitizeFloat(f.M15_Signal);
            f.M15_BBPosition = SanitizeFloat(f.M15_BBPosition);
            f.M15_Volume_Ratio = SanitizeFloat(f.M15_Volume_Ratio);
            f.M15_ATR = SanitizeFloat(f.M15_ATR);
            f.M15_ADX = SanitizeFloat(f.M15_ADX);
        }

        private static float SanitizeFloat(float v)
            => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        /// <summary>
        /// [WPF최적화 2] PredictionEnginePool 초기화
        /// 모델 로드 후 호출하면 다중 스레드에서 lock 없이 예측 가능
        /// 초기화 실패해도 진입 로직에 절대 영향 없음 (캐시 폴백)
        /// </summary>
        private void InitializeEnginePool()
        {
            if (_model == null || _modelSchema == null)
                return;

            try
            {
                // FromFile: 로컬 파일 경로에서 모델 로드 (FromUri는 HTTP 전용)
                var services = new ServiceCollection();
                services.AddPredictionEnginePool<MultiTimeframeEntryFeature, EntryTimingPrediction>()
                    .FromFile(
                        modelName: "EntryTiming",
                        filePath: _modelPath,
                        watchForChanges: true); // 파일 변경 시 자동 리로드

                var provider = services.BuildServiceProvider();
                _enginePool = provider.GetRequiredService<PredictionEnginePool<MultiTimeframeEntryFeature, EntryTimingPrediction>>();
                Console.WriteLine("[EntryTimingML] PredictionEnginePool 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EntryTimingML] PredictionEnginePool 초기화 실패 (캐시 폴백 사용): {ex.Message}");
                _enginePool = null;
                // 폴백: 캐시 엔진이 Predict에서 자동 생성됨 — 진입 영향 없음
            }
        }

        /// <summary>
        /// 배치 예측 (백테스트용)
        /// </summary>
        public List<EntryTimingPrediction> PredictBatch(List<MultiTimeframeEntryFeature> features)
        {
            if (!_isModelLoaded || _model == null)
                return new List<EntryTimingPrediction>();

            try
            {
                var dataView = _mlContext.Data.LoadFromEnumerable(features);
                var predictions = _model.Transform(dataView);
                return _mlContext.Data.CreateEnumerable<EntryTimingPrediction>(predictions, reuseRowObject: false).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EntryTimingML] 배치 예측 실패: {ex.Message}");
                return new List<EntryTimingPrediction>();
            }
        }

        private IEstimator<ITransformer> BuildPipeline()
        {
            // Feature 선택 (49 기존 + 19 확장 + 5 휩소 = 73개)
            var featureColumns = new[]
            {
                // ── 기존 49개 ──
                "D1_Trend", "D1_RSI", "D1_MACD", "D1_Signal", "D1_BBPosition", "D1_Volume_Ratio",
                "H4_Trend", "H4_RSI", "H4_MACD", "H4_Signal", "H4_BBPosition", "H4_Volume_Ratio",
                "H4_DistanceToSupport", "H4_DistanceToResist",
                "H2_Trend", "H2_RSI", "H2_MACD", "H2_Signal", "H2_BBPosition", "H2_Volume_Ratio", "H2_WavePosition",
                "H1_Trend", "H1_RSI", "H1_MACD", "H1_Signal", "H1_BBPosition", "H1_Volume_Ratio", "H1_MomentumStrength",
                "M15_RSI", "M15_MACD", "M15_Signal", "M15_BBPosition", "M15_Volume_Ratio",
                "M15_PriceVsSMA20", "M15_PriceVsSMA60", "M15_ADX", "M15_PlusDI", "M15_MinusDI",
                "M15_ATR", "M15_OI_Change_Pct",
                "HourOfDay", "DayOfWeek", "IsAsianSession", "IsEuropeSession", "IsUSSession",
                "Fib_DistanceTo0382_Pct", "Fib_DistanceTo0618_Pct", "Fib_DistanceTo0786_Pct", "Fib_InEntryZone",
                // ── [v3.4.2] 기존 추출되었으나 미등록이던 확장 피처 19개 ──
                "D1_Stoch_K", "D1_Stoch_D", "D1_MACD_Cross", "D1_ADX", "D1_PlusDI", "D1_MinusDI",
                "H4_Stoch_K", "H4_Stoch_D", "H4_MACD_Cross", "H4_ADX", "H4_PlusDI", "H4_MinusDI", "H4_MomentumStrength",
                "H1_Stoch_K", "H1_Stoch_D", "H1_MACD_Cross",
                "M15_Stoch_K", "M15_Stoch_D",
                "DirectionBias",
                // ── [v4.5.2] 1분봉 MACD 휩소 피처 5개 ──
                "M1_MACD_CrossFlipCount", "M1_MACD_SecsSinceOppCross", "M1_MACD_SignalGapRatio",
                "M1_RSI_ExtremeZone", "M1_MACD_HistStrength",
                // ── [v4.5.6] M15/H1 하락추세 방어 피처 5개 ──
                "M15_IsDowntrend", "H1_IsDowntrend", "M15_ConsecBearishCount",
                "H1_PriceBelowSma60", "M15_RSI_BelowNeutral",
                // ── [v4.5.11] 일일 수익 상태 피처 3개 (목표 달성 후 보수 판단 학습) ──
                "DailyPnlRatio", "IsAboveDailyTarget", "DailyTradeCount",
                // ── [v4.6.2] M15 단타 보조지표 5개 (트레이딩뷰 검증) ──
                "M15_EMA_CrossState", "M15_Price_VWAP_Distance_Pct",
                "M15_StochRSI_K", "M15_StochRSI_D", "M15_StochRSI_Cross",
                // ── [v4.6.3] BB 스퀴즈 / SuperTrend / Daily Pivot 4개 ──
                "M15_BB_Width_Pct", "M15_SuperTrend_Direction",
                "M15_DailyPivot_R1_Dist_Pct", "M15_DailyPivot_S1_Dist_Pct",
                // ── [v5.10.75 Phase 2] 고점 진입 학습 + 여유도 + 심볼 성과 + 다중TF confluence 11개 ──
                "Price_Position_In_Prev5m_Range", "M1_Rise_From_Low_Pct", "M1_Pullback_From_High_Pct",
                "Prev_5m_Rise_From_Low_Pct", "Symbol_Recent_WinRate_30d", "Symbol_Recent_AvgPnLPct_30d",
                "M15_Position_In_Range", "M15_Upper_Shadow_Ratio", "M15_Is_Red_Candle",
                "M15_Rise_From_Low_Pct", "MultiTF_Top_Confluence_Score"
            };

            // 전처리 및 학습 파이프라인
            // [v5.10.73 Phase 1-B] UnbalancedSets=true 추가 — LightGBM 자체 클래스 불균형 보정
            // 현재: AI 점수 역상관 (AiScore ≥0.80 승률 13%, <0.50 승률 71%) = positive class 학습 부실
            // UnbalancedSets=true 적용 시 LightGBM이 minority class에 가중치 자동 부여
            var pipeline = _mlContext.Transforms.Concatenate("Features", featureColumns)
                .Append(_mlContext.Transforms.NormalizeMinMax("Features")) // Feature 정규화
                .Append(_mlContext.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
                {
                    LabelColumnName = "Label",
                    FeatureColumnName = "Features",
                    NumberOfLeaves = 31,
                    MinimumExampleCountPerLeaf = 10,
                    LearningRate = 0.1,
                    NumberOfIterations = 300,
                    UnbalancedSets = true, // [v5.10.73] positive minority class 자동 가중
                    // [v4.5.14] 메인 루프 블로킹 방지: CPU 절반 + 상한 4로 제한
                    NumberOfThreads = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2))
                })); // LightGBM: 빠르고 정확한 트리 기반 모델

            return pipeline;
        }

        private void PrintMetrics(BinaryClassificationMetrics metrics)
        {
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"  📊 ML.NET Entry Timing Model Metrics");
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"  정확도 (Accuracy):    {metrics.Accuracy:P2}");
            Console.WriteLine($"  AUC:                  {metrics.AreaUnderRocCurve:F4}");
            Console.WriteLine($"  F1 Score:             {metrics.F1Score:F4}");
            Console.WriteLine($"  정밀도 (Precision):   {metrics.PositivePrecision:P2}");
            Console.WriteLine($"  재현율 (Recall):      {metrics.PositiveRecall:P2}");
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        }

        /// <summary>
        /// 증분 학습 (Incremental Learning)
        /// 기존 모델에 새 데이터를 추가 학습
        /// </summary>
        public async Task<bool> IncrementalTrainAsync(List<MultiTimeframeEntryFeature> newData)
        {
            if (newData == null || newData.Count < 10)
            {
                Console.WriteLine("[EntryTimingML] 증분 학습 데이터 부족");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    Console.WriteLine($"[EntryTimingML] 증분 학습 시작: {newData.Count}개 샘플");

                    var newDataView = _mlContext.Data.LoadFromEnumerable(newData);

                    if (_model == null)
                    {
                        Console.WriteLine("[EntryTimingML] 기존 모델 없음, 전체 학습으로 전환");
                        var result = TrainAndSaveAsync(newData).GetAwaiter().GetResult();
                        return result.Accuracy > 0.5;
                    }

                    // 기존 모델에 새 데이터 추가 학습 (LightGBM은 증분 학습 미지원 → Retrain)
                    // 실전에서는 기존 데이터 + 새 데이터 합쳐서 재학습
                    Console.WriteLine("[EntryTimingML] LightGBM은 증분 학습 불가, 전체 재학습 권장");
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EntryTimingML] 증분 학습 실패: {ex.Message}");
                    return false;
                }
            });
        }
    }

    /// <summary>
    /// 모델 평가 지표
    /// </summary>
    public class ModelMetrics
    {
        public double Accuracy { get; set; }
        public double AUC { get; set; }
        public double F1Score { get; set; }
        public double Precision { get; set; }
        public double Recall { get; set; }
        public int TrainingSamples { get; set; }
        public double TrainingTimeSeconds { get; set; }
    }
}
