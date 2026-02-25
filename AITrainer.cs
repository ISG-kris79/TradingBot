using Microsoft.ML;
using TradingBot.Models;

public class AITrainer
{
    private readonly MLContext _mlContext = new MLContext(seed: 1);
    private string _modelPath = "model.zip";

    public void TrainAndSave(List<CandleData> data)
    {
        // 1. 데이터 로드
        IDataView trainingData = _mlContext.Data.LoadFromEnumerable(data);

        // 2. 데이터 전처리 파이프라인
        // - Concatenate: 여러 컬럼을 하나의 'Features' 벡터로 묶음
        // - NormalizeMinMax: 데이터 수치를 0~1 사이로 정규화 (학습 효율 상승)
        var pipeline = _mlContext.Transforms.Concatenate("Features",
                nameof(CandleData.Open), nameof(CandleData.High),
                nameof(CandleData.Low), nameof(CandleData.Close),
                nameof(CandleData.Volume))
            .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
            // 3. 알고리즘 선택 (이진 분류: FastTree)
            .Append(_mlContext.BinaryClassification.Trainers.FastTree(labelColumnName: "Label", featureColumnName: "Features"));

        // 4. 모델 학습
        Console.WriteLine("🚀 ML.NET 학습 시작...");
        var model = pipeline.Fit(trainingData);

        // 5. 모델 저장
        _mlContext.Model.Save(model, trainingData.Schema, _modelPath);
        Console.WriteLine($"✅ 모델 저장 완료: {_modelPath}");
    }
}