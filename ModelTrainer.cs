﻿﻿﻿using Microsoft.ML;
using System.IO;
using TradingBot;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class ModelTrainer
    {
        private readonly MLContext _mlContext;
        private readonly string _modelPath;

        public ModelTrainer()
        {
            _mlContext = new MLContext(seed: 1);
            // ML.NET 모델 파일 경로 (model.zip)
            _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model.zip");
        }

        public void TrainAndSave(IEnumerable<CandleData> data)
        {
            try
            {
                // 1. 데이터 로드 (SchemaDefinition으로 [NoColumn] 명시적 반영)
                var schemaDef = Microsoft.ML.Data.SchemaDefinition.Create(typeof(CandleData));
                IDataView trainingData = _mlContext.Data.LoadFromEnumerable(data, schemaDef);

                // 2. 파이프라인 구성
                // Feature 컬럼 목록 직접 정의
                string[] featureColumns = new[]
                {
                    nameof(CandleData.Volume), nameof(CandleData.RSI),
                    nameof(CandleData.MACD), nameof(CandleData.MACD_Signal), 
                    nameof(CandleData.BollingerUpper), nameof(CandleData.BollingerLower),
                    nameof(CandleData.ATR), nameof(CandleData.Price_Change_Pct)
                };
                
                var pipeline = _mlContext.Transforms.Concatenate("Features", featureColumns)
                    .Append(_mlContext.Transforms.NormalizeMinMax("Features")) // 정규화
                    .Append(_mlContext.BinaryClassification.Trainers.FastTree(labelColumnName: "Label", featureColumnName: "Features")); // FastTree 알고리즘

                // 3. 학습 실행
                MainWindow.Instance?.AddLog("🧠 ML.NET 모델 학습 시작...");
                var model = pipeline.Fit(trainingData);

                // [추가] 모델 평가 (정확도 확인)
                var predictions = model.Transform(trainingData);
                var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");
                MainWindow.Instance?.AddLog($"📊 모델 평가 - 정확도: {metrics.Accuracy:P2}, AUC: {metrics.AreaUnderRocCurve:F2}, F1: {metrics.F1Score:F2}");

                // 4. 모델 저장
                _mlContext.Model.Save(model, trainingData.Schema, _modelPath);
                MainWindow.Instance?.AddLog($"✅ 모델 저장 완료: {_modelPath}");

                // [Agent 3] 모델 버전 관리 (타임스탬프 백업)
                string backupPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"model_backup_{DateTime.Now:yyyyMMdd_HHmm}.zip");
                File.Copy(_modelPath, backupPath, true);
                MainWindow.Instance?.AddLog($"📦 모델 백업 생성됨: {Path.GetFileName(backupPath)}");

                // [추가] 모델 성능 메타데이터 저장 (파일명|정확도|F1)
                string metricsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model_metrics.txt");
                string logLine = $"{Path.GetFileName(backupPath)}|{metrics.Accuracy:F4}|{metrics.F1Score:F4}";
                File.AppendAllText(metricsFile, logLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ 모델 학습 실패: {ex.Message}");
            }
        }
    }
}
