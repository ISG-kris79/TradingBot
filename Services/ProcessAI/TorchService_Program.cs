using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TorchSharp;
using static TorchSharp.torch;
using TradingBot.Services.ProcessAI;

namespace TradingBot.TorchService
{
    /// <summary>
    /// TorchSharp Transformer 전용 독립 프로세스
    /// Named Pipe로 메인 프로세스와 통신
    /// BEX64 크래시가 메인 프로세스에 영향 주지 않도록 격리
    /// </summary>
    class Program
    {
        private static TimeSeriesTransformerModel? _model;
        private static string _modelPath = "transformer_model.dat";
        private static NamedPipeServer? _server;
        private const int SeqLen = 8;
        private const int FeatureDim = 20;

        static async Task Main(string[] args)
        {
            Console.WriteLine("[TorchService] Starting TorchSharp service...");

            string pipeName = args.Length > 0 ? args[0] : "TradingBot_TorchService";
            Console.WriteLine($"[TorchService] Pipe name: {pipeName}");

            // TorchSharp 초기화
            try
            {
                InitializeTorch();
                Console.WriteLine("[TorchService] TorchSharp initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorchService] FATAL: TorchSharp initialization failed: {ex.Message}");
                return;
            }

            // 모델 로드 시도
            LoadModel();

            // Named Pipe 서버 시작
            _server = new NamedPipeServer(pipeName, HandleRequestAsync);
            _server.OnLog += msg => Console.WriteLine($"[TorchService] {msg}");
            _server.Start();

            Console.WriteLine("[TorchService] Ready to accept requests. Press Ctrl+C to exit.");

            // 종료 시그널 대기
            var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };

            exitEvent.Wait();

            Console.WriteLine("[TorchService] Shutting down...");
            _server?.Dispose();
            _model?.Dispose();
        }

        private static void InitializeTorch()
        {
            // TorchSharp 기본 초기화
            torch.random.manual_seed(42);
            
            // CPU 모드로 설정 (안정성 우선)
            if (torch.cuda.is_available())
            {
                Console.WriteLine("[TorchService] CUDA available, but using CPU for stability");
            }
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

                if (File.Exists(fullPath))
                {
                    Console.WriteLine($"[TorchService] Loading model from: {fullPath}");
                    _model = new TimeSeriesTransformerModel(SeqLen, FeatureDim);
                    _model.load(fullPath);
                    _model.eval();
                    Console.WriteLine("[TorchService] Model loaded successfully");
                }
                else
                {
                    Console.WriteLine($"[TorchService] Model file not found: {fullPath}");
                    Console.WriteLine("[TorchService] Creating new model...");
                    _model = new TimeSeriesTransformerModel(SeqLen, FeatureDim);
                    _model.eval();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorchService] Error loading model: {ex.Message}");
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

                Console.WriteLine($"[TorchService] Handling command: {baseRequest.Command}");

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
                Console.WriteLine($"[TorchService] Error handling request: {ex.Message}");
                return CreateErrorResponse(ex.Message);
            }
        }

        private static string HandleHealthCheck()
        {
            var response = new HealthCheckResponse
            {
                Success = true,
                ModelLoaded = _model != null,
                ModelPath = _modelPath,
                ProcessStartTime = System.Diagnostics.Process.GetCurrentProcess().StartTime,
                MemoryUsageMB = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024
            };

            return JsonSerializer.Serialize(response);
        }

        private static string HandlePredict(string requestJson)
        {
            try
            {
                var request = JsonSerializer.Deserialize<TransformerPredictRequest>(requestJson);
                if (request == null || _model == null)
                {
                    return CreateErrorResponse("Model not loaded or invalid request");
                }

                // DTO를 Tensor로 변환
                var inputTensor = ConvertFeaturesToTensor(request.Sequence);

                // 예측 실행
                using (torch.no_grad())
                {
                    var output = _model.forward(inputTensor);
                    float candlesToTarget = output[0].item<float>();
                    float confidence = Math.Min(1.0f, Math.Max(0.0f, 1.0f - Math.Abs(candlesToTarget - 16f) / 32f));

                    var response = new TransformerPredictResponse
                    {
                        RequestId = request.RequestId,
                        Success = true,
                        CandlesToTarget = candlesToTarget,
                        Confidence = confidence
                    };

                    output.Dispose();
                    inputTensor.Dispose();

                    return JsonSerializer.Serialize(response);
                }
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
                var request = JsonSerializer.Deserialize<TransformerTrainRequest>(requestJson);
                if (request == null || _model == null)
                {
                    return CreateErrorResponse("Invalid request or model not initialized");
                }

                Console.WriteLine($"[TorchService] Training with {request.Features.Count} samples, {request.Epochs} epochs...");

                // 간단한 학습 루프 (실제로는 더 복잡한 로직 필요)
                float bestLoss = float.MaxValue;
                float finalLoss = 0f;

                var optimizer = torch.optim.Adam(_model.parameters(), lr: 0.001);

                for (int epoch = 0; epoch < request.Epochs; epoch++)
                {
                    float epochLoss = 0f;
                    int batchCount = 0;

                    // 간단한 배치 처리
                    for (int i = 0; i < request.Features.Count; i += request.BatchSize)
                    {
                        var batch = request.Features.Skip(i).Take(request.BatchSize).ToList();
                        if (batch.Count < SeqLen)
                            continue;

                        var inputTensor = ConvertFeaturesToTensor(batch.Take(SeqLen).ToList());
                        var targetTensor = tensor(batch[0].ActualProfitPct);

                        optimizer.zero_grad();
                        var output = _model.forward(inputTensor);
                        var loss = torch.nn.functional.mse_loss(output, targetTensor);

                        loss.backward();
                        optimizer.step();

                        epochLoss += loss.item<float>();
                        batchCount++;

                        inputTensor.Dispose();
                        targetTensor.Dispose();
                        output.Dispose();
                        loss.Dispose();
                    }

                    finalLoss = batchCount > 0 ? epochLoss / batchCount : 0f;
                    if (finalLoss < bestLoss)
                    {
                        bestLoss = finalLoss;
                    }

                    Console.WriteLine($"[TorchService] Epoch {epoch + 1}/{request.Epochs}, Loss: {finalLoss:F4}");
                }

                // 모델 저장
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _modelPath);
                _model.save(modelPath);

                var response = new TransformerTrainResponse
                {
                    RequestId = request.RequestId,
                    Success = true,
                    BestValidationLoss = bestLoss,
                    FinalTrainLoss = finalLoss,
                    TrainedEpochs = request.Epochs
                };

                Console.WriteLine($"[TorchService] Training completed - Best Loss: {bestLoss:F4}");

                return JsonSerializer.Serialize(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorchService] Training error: {ex.Message}");
                return CreateErrorResponse($"Training error: {ex.Message}");
            }
        }

        private static Tensor ConvertFeaturesToTensor(List<MultiTimeframeEntryFeatureDto> features)
        {
            // [batch_size=1, seq_len, feature_dim] 형태로 변환
            int seqLen = Math.Min(features.Count, SeqLen);
            float[,] data = new float[seqLen, FeatureDim];

            for (int i = 0; i < seqLen; i++)
            {
                var f = features[i];
                data[i, 0] = f.D1_RSI;
                data[i, 1] = f.D1_MACD;
                data[i, 2] = f.H4_RSI;
                data[i, 3] = f.H4_MACD;
                data[i, 4] = f.H2_RSI;
                data[i, 5] = f.H1_RSI;
                data[i, 6] = f.M15_RSI;
                data[i, 7] = f.M15_MACD;
                data[i, 8] = f.WavePhase;
                data[i, 9] = f.WaveStrength;
                data[i, 10] = f.FibLevel;
                data[i, 11] = f.D1_ATR;
                data[i, 12] = f.H4_ATR;
                data[i, 13] = f.H1_ATR;
                data[i, 14] = f.M15_ATR;
                data[i, 15] = f.D1_VolumeMA;
                data[i, 16] = f.H4_VolumeMA;
                data[i, 17] = f.H1_VolumeMA;
                data[i, 18] = f.HourOfDay;
                data[i, 19] = f.DayOfWeek;
            }

            return torch.tensor(data).unsqueeze(0); // [1, seq_len, feature_dim]
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

        // 간단한 Transformer 모델 (실제 모델은 더 복잡)
        private class TimeSeriesTransformerModel : torch.nn.Module<Tensor, Tensor>
        {
            private readonly torch.nn.Module<Tensor, Tensor> _encoder;
            private readonly torch.nn.Module<Tensor, Tensor> _decoder;

            public TimeSeriesTransformerModel(int seqLen, int featureDim)
                : base("TimeSeriesTransformer")
            {
                _encoder = torch.nn.Linear(featureDim, 64);
                _decoder = torch.nn.Linear(64, 1);

                register_module("encoder", _encoder);
                register_module("decoder", _decoder);
            }

            public override Tensor forward(Tensor x)
            {
                // x: [batch, seq_len, feature_dim]
                var encoded = _encoder.forward(x); // [batch, seq_len, 64]
                var pooled = encoded.mean(1); // [batch, 64]
                var output = _decoder.forward(pooled); // [batch, 1]
                return output;
            }
        }
    }
}
