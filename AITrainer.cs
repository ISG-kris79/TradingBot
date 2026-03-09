using Microsoft.ML;
using Microsoft.ML.Data;
using TradingBot.Models;

public class AITrainer
{
    private readonly MLContext _mlContext = new MLContext(seed: 42);
    private string _modelPath = "scalping_model.zip";
    private string _legacyModelPath = "model.zip";

    /// <summary>
    /// 스캘핑 모델 학습 (LightGBM + 17개 파생 피처)
    /// MLService.Train() 과 동일한 파이프라인을 사용하되
    /// 간단한 인터페이스로 호출 가능
    /// </summary>
    public void TrainAndSave(List<CandleData> data)
    {
        if (data.Count < 200)
        {
            Console.WriteLine($"⚠️ 학습 데이터 부족: {data.Count}건 (최소 200건 필요)");
            return;
        }

        // 시간순 분할 (80% 학습 / 20% 검증)
        int trainCount = (int)(data.Count * 0.8);
        // SchemaDefinition을 사용하여 타입 호환성 보장
        var schemaDefinition = Microsoft.ML.Data.SchemaDefinition.Create(typeof(CandleData));
        var trainData = _mlContext.Data.LoadFromEnumerable(data.Take(trainCount), schemaDefinition);
        var testData = _mlContext.Data.LoadFromEnumerable(data.Skip(trainCount), schemaDefinition);
        var fullData = _mlContext.Data.LoadFromEnumerable(data, schemaDefinition);

        var pipeline = _mlContext.Transforms.Concatenate("Features", MLService.FeatureColumns)
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.Transforms.Conversion.ConvertType("Label", nameof(CandleData.LabelLong), DataKind.Boolean))
            .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 31,
                minimumExampleCountPerLeaf: 20,
                learningRate: 0.05,
                numberOfIterations: 300));

        Console.WriteLine($"🚀 ML.NET LightGBM 학습 시작 (Train: {trainCount}, Test: {data.Count - trainCount})");
        var model = pipeline.Fit(trainData);

        // 검증
        var predictions = model.Transform(testData);
        var metrics = _mlContext.BinaryClassification.Evaluate(predictions, "Label");
        Console.WriteLine($"✅ Acc: {metrics.Accuracy:P1} | F1: {metrics.F1Score:F3} | AUC: {metrics.AreaUnderRocCurve:F3}");

        // 모델 저장
        _mlContext.Model.Save(model, fullData.Schema, _modelPath);
        Console.WriteLine($"✅ 모델 저장 완료: {_modelPath}");
    }

    /// <summary>이전 호환용: LightGBM + MLService.FeatureColumns</summary>
    public void TrainAndSaveLegacy(List<CandleData> data)
    {
        var schemaDefinition = Microsoft.ML.Data.SchemaDefinition.Create(typeof(CandleData));
        IDataView trainingData = _mlContext.Data.LoadFromEnumerable(data, schemaDefinition);
        var pipeline = _mlContext.Transforms.Concatenate("Features", MLService.FeatureColumns)
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(labelColumnName: "Label", featureColumnName: "Features"));

        var model = pipeline.Fit(trainingData);
        _mlContext.Model.Save(model, trainingData.Schema, _legacyModelPath);
    }
}
