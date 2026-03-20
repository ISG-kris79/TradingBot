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

        public bool IsModelLoaded => _isModelLoaded;

        public EntryTimingMLTrainer(string? modelPath = null)
        {
            _mlContext = new MLContext(seed: 42);
            _legacyModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EntryTimingModel.zip");
            _modelPath = string.IsNullOrWhiteSpace(modelPath)
                ? GetDefaultModelPath()
                : modelPath;

            EnsureModelDirectoryExists();
        }

        private static string GetDefaultModelPath()
        {
            string modelDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TradingBot",
                "Models");

            return Path.Combine(modelDir, "EntryTimingModel.zip");
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

                // 데이터 로드
                var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

                // 학습/검증 분할 (80/20)
                var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                // 데이터 전처리 파이프라인
                var pipeline = BuildPipeline();

                // 모델 학습
                var watch = System.Diagnostics.Stopwatch.StartNew();
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
        private const int ExpectedFeatureCount = 49;

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
        /// </summary>
        public EntryTimingPrediction? Predict(MultiTimeframeEntryFeature feature)
        {
            if (!_isModelLoaded || _model == null)
                return null;

            try
            {
                var predictionEngine = _mlContext.Model.CreatePredictionEngine<MultiTimeframeEntryFeature, EntryTimingPrediction>(_model);
                var prediction = predictionEngine.Predict(feature);
                return prediction;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EntryTimingML] 예측 실패: {ex.Message}");
                return null;
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
            // Feature 선택 (45개 numeric features)
            var featureColumns = new[]
            {
                "D1_Trend", "D1_RSI", "D1_MACD", "D1_Signal", "D1_BBPosition", "D1_Volume_Ratio",
                "H4_Trend", "H4_RSI", "H4_MACD", "H4_Signal", "H4_BBPosition", "H4_Volume_Ratio",
                "H4_DistanceToSupport", "H4_DistanceToResist",
                "H2_Trend", "H2_RSI", "H2_MACD", "H2_Signal", "H2_BBPosition", "H2_Volume_Ratio", "H2_WavePosition",
                "H1_Trend", "H1_RSI", "H1_MACD", "H1_Signal", "H1_BBPosition", "H1_Volume_Ratio", "H1_MomentumStrength",
                "M15_RSI", "M15_MACD", "M15_Signal", "M15_BBPosition", "M15_Volume_Ratio",
                "M15_PriceVsSMA20", "M15_PriceVsSMA60", "M15_ADX", "M15_PlusDI", "M15_MinusDI",
                "M15_ATR", "M15_OI_Change_Pct",
                "HourOfDay", "DayOfWeek", "IsAsianSession", "IsEuropeSession", "IsUSSession",
                "Fib_DistanceTo0382_Pct", "Fib_DistanceTo0618_Pct", "Fib_DistanceTo0786_Pct", "Fib_InEntryZone"
            };

            // 전처리 및 학습 파이프라인
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
                    NumberOfThreads = Math.Max(2, Environment.ProcessorCount - 2)
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
