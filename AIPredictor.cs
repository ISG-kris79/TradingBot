﻿using Microsoft.ML;
using System.IO;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class AIPredictor : IDisposable
    {
        private MLContext? _mlContext;
        private ITransformer? _model;
        private PredictionEngine<CandleData, ScalpingPrediction>? _scalpingEngine;
        private PredictionEngine<CandleData, PredictionResult>? _legacyEngine;
        private readonly string _modelPath;
        private readonly string _baseDir;
        private bool _disposed = false;

        public AIPredictor()
        {
            _mlContext = new MLContext(seed: 42);
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _modelPath = Path.Combine(_baseDir, "scalping_model.zip");

            // 가장 성능이 좋은 모델 찾기 (F1 기준)
            string bestModelPath = GetBestModelPath();
            string loadPath = bestModelPath;

            // scalping_model 없으면 기존 model.zip fallback
            if (!File.Exists(loadPath))
                loadPath = Path.Combine(_baseDir, "model.zip");

            if (File.Exists(loadPath) && _mlContext != null)
            {
                try
                {
                    _model = _mlContext.Model.Load(loadPath, out _);
                    _scalpingEngine = _mlContext.Model.CreatePredictionEngine<CandleData, ScalpingPrediction>(_model);
                }
                catch (Exception ex)
                {
                    // ScalpingPrediction 스키마 불일치 시 Legacy로 fallback
                    System.Diagnostics.Debug.WriteLine($"[AIPredictor] ScalpingPrediction 엔진 생성 실패(Legacy 시도): {ex.Message}");
                    try
                    {
                        _model = _mlContext.Model.Load(loadPath, out _);
                        _legacyEngine = _mlContext.Model.CreatePredictionEngine<CandleData, PredictionResult>(_model);
                    }
                    catch (Exception innerEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AIPredictor] Legacy 엔진도 생성 실패: {innerEx.Message}");
                    }
                }
            }
        }

        private string GetBestModelPath()
        {
            try
            {
                string metricsFile = Path.Combine(_baseDir, "model_metrics.txt");
                if (!File.Exists(metricsFile)) return _modelPath;

                var lines = File.ReadAllLines(metricsFile);
                var bestModel = lines
                    .Select(line => line.Split('|'))
                    .Where(parts => parts.Length >= 3)
                    .Select(parts => new
                    {
                        FileName = parts[0],
                        F1 = double.TryParse(parts[2], out var f) ? f : 0
                    })
                    .Where(x => File.Exists(Path.Combine(_baseDir, x.FileName)))
                    .OrderByDescending(x => x.F1)
                    .FirstOrDefault();

                if (bestModel != null)
                    return Path.Combine(_baseDir, bestModel.FileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIPredictor] 최적 모델 탐색 실패: {ex.Message}");
            }
            return _modelPath;
        }

        /// <summary>스캘핑 모델 예측 (LightGBM)</summary>
        public ScalpingPrediction? PredictScalping(CandleData data)
        {
            return _scalpingEngine?.Predict(data);
        }

        /// <summary>기존 호환용 예측</summary>
        public PredictionResult Predict(CandleData data)
        {
            if (_scalpingEngine != null)
            {
                var sp = _scalpingEngine.Predict(data);
                return new PredictionResult
                {
                    Prediction = sp.PredictedLabel,
                    Probability = sp.Probability,
                    Score = sp.Score
                };
            }
            return _legacyEngine?.Predict(data) ?? new PredictionResult { Prediction = false, Probability = 0.5f };
        }

        public bool IsModelLoaded => _scalpingEngine != null || _legacyEngine != null;

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                _scalpingEngine?.Dispose();
                _legacyEngine?.Dispose();
                _model = null;
                _mlContext = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIPredictor] Dispose 오류: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
