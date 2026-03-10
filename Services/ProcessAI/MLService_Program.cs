using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ML;
using TradingBot.Services.ProcessAI;

namespace TradingBot.MLService
{
    /// <summary>
    /// ML.NET 전용 독립 프로세스
    /// Named Pipe로 메인 프로세스와 통신
    /// </summary>
    class Program
    {
        private static MLContext? _mlContext;
        private static ITransformer? _model;
        private static PredictionEngine<CandleDataInternal, PredictionResultInternal>? _predictionEngine;
        private static string _modelPath = "scalping_model.zip";
        private static NamedPipeServer? _server;

        static async Task Main(string[] args)
        {
            Console.WriteLine("[MLService] Starting ML.NET service...");

            string pipeName = args.Length > 0 ? args[0] : "TradingBot_MLService";
            Console.WriteLine($"[MLService] Pipe name: {pipeName}");

            // ML.NET 초기화
            _mlContext = new MLContext(seed: 42);
            
            // 모델 로드 시도
            LoadModel();

            // Named Pipe 서버 시작
            _server = new NamedPipeServer(pipeName, HandleRequestAsync);
            _server.OnLog += msg => Console.WriteLine($"[MLService] {msg}");
            _server.Start();

            Console.WriteLine("[MLService] Ready to accept requests. Press Ctrl+C to exit.");

            // 종료 시그널 대기
            var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };

            exitEvent.Wait();

            Console.WriteLine("[MLService] Shutting down...");
            _server?.Dispose();
        }

        private static void LoadModel()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string fullPath = Path.Combine(baseDir, _modelPath);

                if (!File.Exists(fullPath))
                {
                    fullPath = Path.Combine(baseDir, "..", _modelPath);
                }

                if (File.Exists(fullPath) && _mlContext != null)
                {
                    Console.WriteLine($"[MLService] Loading model from: {fullPath}");
                    _model = _mlContext.Model.Load(fullPath, out _);
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleDataInternal, PredictionResultInternal>(_model);
                    Console.WriteLine("[MLService] Model loaded successfully");
                }
                else
                {
                    Console.WriteLine($"[MLService] Model file not found: {fullPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MLService] Error loading model: {ex.Message}");
            }
        }

        private static async Task<string> HandleRequestAsync(string requestJson)
        {
            try
            {
                var baseRequest = JsonSerializer.Deserialize<AIServiceRequest>(requestJson);
                if (baseRequest == null)
                {
                    return CreateErrorResponse("Invalid request format");
                }

                Console.WriteLine($"[MLService] Handling command: {baseRequest.Command}");

                return baseRequest.Command switch
                {
                    "health" => HandleHealthCheck(),
                    "predict" => HandlePredict(requestJson),
                    "train" => await HandleTrainAsync(requestJson),
                    _ => CreateErrorResponse($"Unknown command: {baseRequest.Command}")
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MLService] Error handling request: {ex.Message}");
                return CreateErrorResponse(ex.Message);
            }
        }

        private static string HandleHealthCheck()
        {
            var response = new HealthCheckResponse
            {
                Success = true,
                ModelLoaded = _predictionEngine != null,
                ModelPath = _modelPath,
                ProcessStartTime = Process.GetCurrentProcess().StartTime,
                MemoryUsageMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024
            };

            return JsonSerializer.Serialize(response);
        }

        private static string HandlePredict(string requestJson)
        {
            try
            {
                var request = JsonSerializer.Deserialize<MLPredictRequest>(requestJson);
                if (request == null || _predictionEngine == null)
                {
                    return CreateErrorResponse("Model not loaded or invalid request");
                }

                // DTO를 내부 형식으로 변환
                var candle = ConvertToInternal(request.Candle);
                var prediction = _predictionEngine.Predict(candle);

                var response = new MLPredictResponse
                {
                    RequestId = request.RequestId,
                    Success = true,
                    ShouldEnter = prediction.ShouldEnter,
                    Probability = prediction.Probability,
                    Confidence = prediction.Score
                };

                return JsonSerializer.Serialize(response);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Prediction error: {ex.Message}");
            }
        }

        private static async Task<string> HandleTrainAsync(string requestJson)
        {
            try
            {
                var request = JsonSerializer.Deserialize<MLTrainRequest>(requestJson);
                if (request == null || _mlContext == null)
                {
                    return CreateErrorResponse("Invalid request or ML context not initialized");
                }

                Console.WriteLine($"[MLService] Training with {request.Features.Count} samples...");

                // DTO를 내부 형식으로 변환
                var trainingData = request.Features
                    .Select(f => ConvertFeatureToInternal(f))
                    .ToList();

                // 데이터 로드
                var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

                // 학습 파이프라인 구성
                var pipeline = _mlContext.Transforms.Concatenate("Features",
                        nameof(MultiTimeframeFeatureInternal.D1_RSI),
                        nameof(MultiTimeframeFeatureInternal.D1_MACD),
                        nameof(MultiTimeframeFeatureInternal.H4_RSI),
                        nameof(MultiTimeframeFeatureInternal.H1_RSI),
                        nameof(MultiTimeframeFeatureInternal.M15_RSI))
                    .Append(_mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                        labelColumnName: "Label",
                        maximumNumberOfIterations: request.MaxEpochs));

                // 학습 실행
                var model = pipeline.Fit(dataView);

                // 메트릭 평가
                var predictions = model.Transform(dataView);
                var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");

                // 모델 저장
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _modelPath);
                _mlContext.Model.Save(model, dataView.Schema, modelPath);

                // 모델 리로드
                _model = model;
                _predictionEngine = _mlContext.Model.CreatePredictionEngine<CandleDataInternal, PredictionResultInternal>(_model);

                var response = new MLTrainResponse
                {
                    RequestId = request.RequestId,
                    Success = true,
                    Accuracy = metrics.Accuracy,
                    F1Score = metrics.F1Score,
                    AUC = metrics.AreaUnderRocCurve,
                    TrainedSamples = trainingData.Count
                };

                Console.WriteLine($"[MLService] Training completed - Accuracy: {metrics.Accuracy:P2}");

                return JsonSerializer.Serialize(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MLService] Training error: {ex.Message}");
                return CreateErrorResponse($"Training error: {ex.Message}");
            }
        }

        private static string CreateErrorResponse(string errorMessage)
        {
            var response = new AIServiceResponse
            {
                Success = false,
                ErrorMessage = errorMessage
            };
            return JsonSerializer.Serialize(response);
        }

        private static CandleDataInternal ConvertToInternal(CandleDataDto dto)
        {
            return new CandleDataInternal
            {
                RSI = dto.RSI,
                MACD = dto.MACD,
                Signal = dto.Signal,
                BollingerUpper = dto.BollingerUpper,
                BollingerLower = dto.BollingerLower,
                ATR = dto.ATR,
                VolumeMA = dto.VolumeMA,
                PriceChangePercent = dto.PriceChangePercent
            };
        }

        private static MultiTimeframeFeatureInternal ConvertFeatureToInternal(MultiTimeframeEntryFeatureDto dto)
        {
            return new MultiTimeframeFeatureInternal
            {
                D1_RSI = dto.D1_RSI,
                D1_MACD = dto.D1_MACD,
                H4_RSI = dto.H4_RSI,
                H1_RSI = dto.H1_RSI,
                M15_RSI = dto.M15_RSI,
                Label = dto.ShouldEnter
            };
        }

        // 내부 ML.NET 클래스들
        private class CandleDataInternal
        {
            public float RSI { get; set; }
            public float MACD { get; set; }
            public float Signal { get; set; }
            public float BollingerUpper { get; set; }
            public float BollingerLower { get; set; }
            public float ATR { get; set; }
            public float VolumeMA { get; set; }
            public float PriceChangePercent { get; set; }
        }

        private class PredictionResultInternal
        {
            public bool ShouldEnter { get; set; }
            public float Probability { get; set; }
            public float Score { get; set; }
        }

        private class MultiTimeframeFeatureInternal
        {
            public float D1_RSI { get; set; }
            public float D1_MACD { get; set; }
            public float H4_RSI { get; set; }
            public float H1_RSI { get; set; }
            public float M15_RSI { get; set; }
            public bool Label { get; set; }
        }
    }
}
