﻿using Microsoft.ML;
using System.IO;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class AIPredictor
    {
        private MLContext? _mlContext;
        private ITransformer? _model;
        private PredictionEngine<CandleData, PredictionResult>? _predictionEngine;
        private readonly string _modelPath;

        public AIPredictor()
        {
            _mlContext = new MLContext(seed: 1);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _modelPath = Path.Combine(baseDir, "model.zip");

            // [추가] 가장 성능이 좋은 백업 모델 찾기
            string bestModelPath = GetBestModelPath(baseDir);
            if (!string.IsNullOrEmpty(bestModelPath) && File.Exists(bestModelPath))
            {
                _modelPath = bestModelPath;
                // MainWindow.Instance?.AddLog($"🧠 Best Model Loaded: {Path.GetFileName(_modelPath)}"); // UI 접근 불가 시 생략
            }

            if (File.Exists(_modelPath) && _mlContext != null)
            {
                _model = _mlContext.Model.Load(_modelPath, out _);
                _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleData, PredictionResult>(_model);
            }
            // 모델 파일이 없으면 예외 대신 null 처리하여 봇 가동은 되도록 함 (TradingEngine에서 처리)
        }

        private string GetBestModelPath(string baseDir)
        {
            try
            {
                string metricsFile = Path.Combine(baseDir, "model_metrics.txt");
                if (!File.Exists(metricsFile)) return _modelPath;

                var lines = File.ReadAllLines(metricsFile);
                var bestModel = lines
                    .Select(line => line.Split('|'))
                    .Where(parts => parts.Length == 3)
                    .Select(parts => new
                    {
                        FileName = parts[0],
                        Accuracy = double.Parse(parts[1]),
                        F1 = double.Parse(parts[2])
                    })
                    .OrderByDescending(x => x.F1) // F1 Score 기준 정렬
                    .FirstOrDefault();

                if (bestModel != null)
                {
                    return Path.Combine(baseDir, bestModel.FileName);
                }
            }
            catch { /* 파싱 에러 무시 */ }
            return _modelPath;
        }

        public PredictionResult Predict(CandleData data)
        {
            return _predictionEngine?.Predict(data) ?? new PredictionResult { Prediction = false, Probability = 0.5f };
        }
    }
}