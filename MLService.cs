using Microsoft.ML;
using System.IO;
using TradingBot;
using TradingBot.Models;
using TradingBot.Services;

public class MLService
{
    private MLContext _mlContext = new MLContext(seed: 1);
    private ITransformer _model;
    private PredictionEngine<CandleData, PredictionResult> _predictionEngine;
    private readonly string _modelPath = "model.zip";

    public MLService()
    {
        // 기존 모델이 있다면 로드
        if (File.Exists(_modelPath)) LoadModel();
    }

    // [학습] MSSQL에서 가져온 리스트로 모델 생성
    public void Train(List<CandleData> trainingData)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        // 파이프라인 구성
        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(CandleData.Open), nameof(CandleData.High),
                nameof(CandleData.Low), nameof(CandleData.Close),
                nameof(CandleData.Volume))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features")) // 정규화
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(labelColumnName: "Label", featureColumnName: "Features"));

        _model = pipeline.Fit(dataView);
        _mlContext.Model.Save(_model, dataView.Schema, _modelPath);

        // 엔진 갱신
        _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleData, PredictionResult>(_model);
    }

    public void LoadModel()
    {
        _model = _mlContext.Model.Load(_modelPath, out _);
        _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleData, PredictionResult>(_model);
    }

    // [추론] 현재 캔들 데이터로 예측
    public PredictionResult Predict(CandleData current)
    {
        if (_predictionEngine == null) return null;
        return _predictionEngine.Predict(current);
    }
    public async Task RunHourlyLearningTask(CancellationToken token)
    {
        DbManager db = new DbManager(AppConfig.ConnectionString);
        while (!token.IsCancellationRequested)
        {
            // 1. 다음 정각까지 대기
            var now = DateTime.Now;
            var nextHour = now.AddHours(1).Date.AddHours(now.Hour + 1);
            int delayMs = (int)(nextHour - now).TotalMilliseconds;

            await Task.Delay(delayMs, token);

            // 2. 학습 프로세스 실행 (별도 스레드)
            _ = Task.Run(async () =>
            {
                try
                {
                    await db.TrainNeuralNetworkModel();
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.AddLog($"학습 에러: {ex.Message}");
                }
            });
        }
    }

}