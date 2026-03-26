using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;
using System.IO;
using TradingBot;
using TradingBot.Models;
using TradingBot.Services;

/// <summary>
/// ML.NET 기반 코인 선물 5분봉 단타 매매 AI 서비스
/// - LightGBM 기반 이진 분류 (LONG 성공 여부)
/// - 20배 레버리지 기준 레이블링 (목표 +2.5%, 손절 -1.0%)
/// - 정규화된 파생 피처 사용 (가격 이격도, 거래량 비율, 캔들 패턴 등)
/// </summary>
public class MLService : IDisposable
{
    private MLContext _mlContext = new MLContext(seed: 42);
    private ITransformer? _model;
    private PredictionEngine<CandleData, ScalpingPrediction>? _predictionEngine;
    private readonly string _modelPath;
    private readonly string _modelDir;
    private bool _disposed = false;
    // [Stage1] PredictionEngine Thread-safety 보호
    private readonly object _predictLock = new();

    // 학습에 사용할 전체 피처 목록 (정규화된 파생 지표 중심)
    public static readonly string[] FeatureColumns = new[]
    {
        // 보조지표
        nameof(CandleData.RSI),
        nameof(CandleData.MACD_Hist),
        nameof(CandleData.ATR),
        nameof(CandleData.BB_Width),

        // 가격 파생 지표 (이미 정규화됨: 0 기준 %값)
        nameof(CandleData.Price_Change_Pct),
        nameof(CandleData.Price_To_BB_Mid),
        nameof(CandleData.Price_To_SMA20_Pct),

        // 캔들 패턴 (0~1 비율)
        nameof(CandleData.Candle_Body_Ratio),
        nameof(CandleData.Upper_Shadow_Ratio),
        nameof(CandleData.Lower_Shadow_Ratio),

        // 거래량 분석
        nameof(CandleData.Volume_Ratio),
        nameof(CandleData.Volume_Change_Pct),

        // 피보나치 & 추세
        nameof(CandleData.Fib_Position),
        nameof(CandleData.Trend_Strength),
        nameof(CandleData.RSI_Divergence),
        nameof(CandleData.ElliottWaveState),

        // 뉴스 감성
        nameof(CandleData.SentimentScore),
    };

    public MLService()
    {
        _modelDir = AppDomain.CurrentDomain.BaseDirectory;
        _modelPath = Path.Combine(_modelDir, "scalping_model.zip");

        if (File.Exists(_modelPath)) LoadModel();
    }

    /// <summary>
    /// LightGBM 기반 이진 분류 학습 (Long 진입 성공 여부)
    /// </summary>
    public TrainingMetrics Train(List<CandleData> trainingData)
    {
        if (trainingData.Count < 200)
        {
            MainWindow.Instance?.AddLog($"⚠️ 학습 데이터 부족: {trainingData.Count}건 (최소 200건 필요)");
            return new TrainingMetrics();
        }

        // SchemaDefinition을 사용하여 타입 호환성 보장
        var schemaDefinition = Microsoft.ML.Data.SchemaDefinition.Create(typeof(CandleData));
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData, schemaDefinition);

        // 시간순 분할 (80% 학습 / 20% 검증) ── 데이터 누수 방지!
        int trainCount = (int)(trainingData.Count * 0.8);
        var trainData = _mlContext.Data.LoadFromEnumerable(trainingData.Take(trainCount), schemaDefinition);
        var testData = _mlContext.Data.LoadFromEnumerable(trainingData.Skip(trainCount), schemaDefinition);

        // 파이프라인 구성
        var pipeline = _mlContext.Transforms.Concatenate("Features", FeatureColumns)
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.Transforms.Conversion.ConvertType("Label", nameof(CandleData.LabelLong), DataKind.Boolean))
            .Append(_mlContext.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
            {
                LabelColumnName = "Label",
                FeatureColumnName = "Features",
                NumberOfLeaves = 31,
                MinimumExampleCountPerLeaf = 20,
                LearningRate = 0.05,
                NumberOfIterations = 300,
                NumberOfThreads = Math.Max(2, Environment.ProcessorCount - 2)
            }));

        MainWindow.Instance?.AddLog($"🚀 ML.NET LightGBM 학습 시작 (Train: {trainCount}, Test: {trainingData.Count - trainCount})");

        _model = pipeline.Fit(trainData);

        // 검증
        var predictions = _model.Transform(testData);
        var metrics = _mlContext.BinaryClassification.Evaluate(predictions, "Label");

        var result = new TrainingMetrics
        {
            Accuracy = metrics.Accuracy,
            F1Score = metrics.F1Score,
            Precision = metrics.PositivePrecision,
            Recall = metrics.PositiveRecall,
            AUC = metrics.AreaUnderRocCurve,
            TrainCount = trainCount,
            TestCount = trainingData.Count - trainCount
        };

        MainWindow.Instance?.AddLog($"✅ 학습 완료 | Acc: {result.Accuracy:P1} | F1: {result.F1Score:F3} | AUC: {result.AUC:F3}");

        // 모델 저장
        SaveModelWithVersion(dataView.Schema, result);
        _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleData, ScalpingPrediction>(_model);

        return result;
    }

    /// <summary>
    /// 이전 호환용: 기존 Train(List) 시그니처 유지 (FeatureColumns→FastTree)
    /// </summary>
    public void TrainLegacy(List<CandleData> trainingData)
    {
        var schemaDef = Microsoft.ML.Data.SchemaDefinition.Create(typeof(CandleData));
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData, schemaDef);
        var pipeline = _mlContext.Transforms.Concatenate("Features", FeatureColumns)
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(labelColumnName: "Label", featureColumnName: "Features"));

        _model = pipeline.Fit(dataView);
        _mlContext.Model.Save(_model, dataView.Schema, Path.Combine(_modelDir, "model.zip"));
    }

    private void SaveModelWithVersion(DataViewSchema schema, TrainingMetrics metrics)
    {
        if (_model == null) return;

        _mlContext.Model.Save(_model, schema, _modelPath);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupPath = Path.Combine(_modelDir, $"scalping_model_{timestamp}.zip");
        _mlContext.Model.Save(_model, schema, backupPath);

        string metricsFile = Path.Combine(_modelDir, "model_metrics.txt");
        File.AppendAllLines(metricsFile, new[] { $"scalping_model_{timestamp}.zip|{metrics.Accuracy:F4}|{metrics.F1Score:F4}|{metrics.AUC:F4}" });

        // 오래된 백업 정리 (최근 5개만 유지)
        try
        {
            var backups = Directory.GetFiles(_modelDir, "scalping_model_*.zip").OrderByDescending(f => f).Skip(5).ToList();
            foreach (var old in backups) File.Delete(old);
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.AddLog($"⚠️ 오래된 ML 백업 정리 실패: {ex.Message}");
        }
    }

    public void LoadModel()
    {
        try
        {
            string bestPath = GetBestModelPath();
            _model = _mlContext.Model.Load(bestPath, out _);
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleData, ScalpingPrediction>(_model);
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.AddLog($"⚠️ ML 모델 로드 실패: {ex.Message}");
        }
    }

    private string GetBestModelPath()
    {
        string metricsFile = Path.Combine(_modelDir, "model_metrics.txt");
        if (!File.Exists(metricsFile)) return _modelPath;
        try
        {
            var best = File.ReadAllLines(metricsFile)
                .Select(l => l.Split('|'))
                .Where(p => p.Length >= 3)
                .Select(p => new { Path = Path.Combine(_modelDir, p[0]), F1 = double.TryParse(p[2], out var f) ? f : 0 })
                .Where(x => File.Exists(x.Path))
                .OrderByDescending(x => x.F1)
                .FirstOrDefault();
            return best?.Path ?? _modelPath;
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.AddLog($"⚠️ ML 최적 모델 탐색 실패: {ex.Message}");
            return _modelPath;
        }
    }

    /// <summary>[Stage1] Thread-safe 예측</summary>
    public ScalpingPrediction? Predict(CandleData current)
    {
        if (_predictionEngine == null) return null;
        lock (_predictLock)
        {
            return _predictionEngine.Predict(current);
        }
    }

    /// <summary>이전 호환용 PredictionResult 반환</summary>
    public PredictionResult? PredictLegacy(CandleData current)
    {
        var sp = Predict(current);
        if (sp == null) return null;
        return new PredictionResult { Prediction = sp.PredictedLabel, Probability = sp.Probability, Score = sp.Score };
    }

    public bool IsModelLoaded => _predictionEngine != null;

    public async Task RunHourlyLearningTask(CancellationToken token)
    {
        DbManager db = new DbManager(AppConfig.ConnectionString);
        while (!token.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextHour = now.AddHours(1).Date.AddHours(now.Hour + 1);
            int delayMs = (int)(nextHour - now).TotalMilliseconds;
            await Task.Delay(delayMs, token);
            _ = Task.Run(async () =>
            {
                try { await db.TrainNeuralNetworkModel(); }
                catch (Exception ex) { MainWindow.Instance?.AddLog($"학습 에러: {ex.Message}"); }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            _predictionEngine?.Dispose();
            _predictionEngine = null;
            _model = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MLService] Dispose 오류: {ex.Message}");
        }
        finally
        {
            _disposed = true;
        }
        
        GC.SuppressFinalize(this);
    }
}

/// <summary>LightGBM 예측 결과</summary>
public class ScalpingPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }

    [ColumnName("Probability")]
    public float Probability { get; set; }

    [ColumnName("Score")]
    public float Score { get; set; }
}

/// <summary>학습 결과 메트릭</summary>
public class TrainingMetrics
{
    public double Accuracy { get; set; }
    public double F1Score { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double AUC { get; set; }
    public int TrainCount { get; set; }
    public int TestCount { get; set; }
}
