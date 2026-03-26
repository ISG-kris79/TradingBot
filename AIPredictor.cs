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
        // private MLServiceClient? _mlServiceClient; // TensorFlow 전환 중 임시 비활성화
        private readonly string _modelPath;
        private readonly string _baseDir;
        private bool _disposed = false;
        // [Stage1] PredictionEngine Thread-safety를 위한 lock
        private readonly object _predictLock = new();

        public AIPredictor(bool preferExternalMlService = true)
        {
            _mlContext = new MLContext(seed: 42);
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _modelPath = Path.Combine(_baseDir, "scalping_model.zip");

            // [FIX] 즉시 로컬 모델로드 (UI 스레드 블로킹 방지)
            LoadLocalModel();

            /* TensorFlow 전환 중 외부 ML 서비스 비활성화
            // [비동기] 백그라운드에서 외부 ML 서비스 연결 시도
            if (preferExternalMlService)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _mlServiceClient = new MLServiceClient();
                        _mlServiceClient.OnLog += msg => System.Diagnostics.Debug.WriteLine($"[AIPredictor][MLService] {msg}");

                        // 최대 5초 타임아웃으로 외부 서비스 연결 시도
                        using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        {
                            bool started = await _mlServiceClient.StartAsync();
                            if (started)
                            {
                                _useExternalMlService = true;
                                System.Diagnostics.Debug.WriteLine("[AIPredictor] MLService 프로세스 연결 성공 (외부 프로세스 모드)");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("[AIPredictor] MLService 시작 실패 - 로컬 모델 사용");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[AIPredictor] MLService 연결 타임아웃 (5초) - 로컬 모델 사용");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AIPredictor] MLService 연결 실패 - 로컬 ML.NET 폴백: {ex.Message}");
                    }
                });
            }
            */
        }

        private void LoadLocalModel()
        {
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

        /// <summary>
        /// 스캘핑 모델 예측 (LightGBM)
        /// [Stage1] PredictionEngine은 Thread-safe하지 않으므로 lock으로 보호
        /// </summary>
        public ScalpingPrediction? PredictScalping(CandleData data)
        {
            lock (_predictLock)
            {
                return _scalpingEngine?.Predict(data);
            }
        }

        /// <summary>
        /// 기존 호환용 예측
        /// [Stage1] lock으로 Thread-safety 보장
        /// </summary>
        public PredictionResult Predict(CandleData data)
        {
            lock (_predictLock)
            {
                if (_scalpingEngine != null)
                {
                    var sp = _scalpingEngine.Predict(data);
                    float normalizedProbability = NormalizeProbability(sp.Probability, sp.Score);
                    return new PredictionResult
                    {
                        Prediction = sp.PredictedLabel,
                        Probability = normalizedProbability,
                        Score = sp.Score
                    };
                }

                if (_legacyEngine != null)
                {
                    var legacy = _legacyEngine.Predict(data);
                    legacy.Probability = NormalizeProbability(legacy.Probability, legacy.Score);
                    return legacy;
                }

                return new PredictionResult { Prediction = false, Probability = 0.5f };
            }
        }

        private static float NormalizeProbability(float probability, float score)
        {
            // [FIX] 0.0과 1.0도 유효한 확률값으로 인정
            bool probabilityLooksValid =
                !float.IsNaN(probability) &&
                !float.IsInfinity(probability) &&
                probability >= 0f &&
                probability <= 1f;

            if (probabilityLooksValid)
                return probability;

            if (!float.IsNaN(score) && !float.IsInfinity(score))
            {
                float boundedScore = Math.Clamp(score, -12f, 12f);
                float inferredProbability = 1f / (1f + (float)Math.Exp(-boundedScore));
                return Math.Clamp(inferredProbability, 0.001f, 0.999f);
            }

            return 0.5f;
        }

        public bool IsModelLoaded => _scalpingEngine != null || _legacyEngine != null; // TensorFlow 전환 중 외부 서비스 제거

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                // _mlServiceClient?.Dispose(); // TensorFlow 전환 중 비활성화
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
