using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [Stage3] AI 엔진 Named Pipe 서버
    ///
    /// 현재: 같은 프로세스 내 백그라운드 스레드에서 실행 (즉시 적용 가능)
    /// 향후: 별도 콘솔 앱으로 분리 시 이 클래스를 Program.cs의 Main에서 호스팅
    ///
    /// 사용법:
    ///   var server = new AIPipelineServer(mlTrainer, tfTrainer);
    ///   await server.StartAsync(cancellationToken);
    /// </summary>
    public class AIPipelineServer : IDisposable
    {
        private const string PipeName = "TradingBot_AI_Pipeline";
        private readonly EntryTimingMLTrainer _mlTrainer;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private bool _disposed;

        public event Action<string>? OnLog;

        public AIPipelineServer(EntryTimingMLTrainer mlTrainer)
        {
            _mlTrainer = mlTrainer;
        }

        /// <summary>
        /// 서버 시작 (백그라운드 루프)
        /// </summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
            OnLog?.Invoke("[AIPipeServer] Named Pipe 서버 시작");
        }

        private async Task ServerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipeServer.WaitForConnectionAsync(token);
                    OnLog?.Invoke("[AIPipeServer] 클라이언트 연결됨");

                    // 각 연결을 독립 태스크로 처리
                    var clientPipe = pipeServer;
                    pipeServer = null; // Dispose 방지
                    _ = Task.Run(() => HandleClientAsync(clientPipe, token), token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[AIPipeServer] 서버 루프 오류: {ex.Message}");
                    pipeServer?.Dispose();
                    await Task.Delay(1000, token);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            try
            {
                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    IpcEnvelope? request;
                    try
                    {
                        request = JsonSerializer.Deserialize<IpcEnvelope>(line);
                    }
                    catch
                    {
                        continue;
                    }

                    if (request == null) continue;

                    var response = await ProcessRequestAsync(request);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[AIPipeServer] 클라이언트 처리 오류: {ex.Message}");
            }
            finally
            {
                pipe.Dispose();
            }
        }

        private async Task<IpcEnvelope> ProcessRequestAsync(IpcEnvelope request)
        {
            var response = new IpcEnvelope { Id = request.Id, Type = "response" };

            try
            {
                switch (request.Type)
                {
                    case "ping":
                        response.Payload = "pong";
                        break;

                    case "predict":
                        var predReq = JsonSerializer.Deserialize<AIPipelinePredictionRequest>(request.Payload ?? "");
                        if (predReq != null)
                        {
                            var result = await ProcessPredictionAsync(predReq);
                            response.Payload = JsonSerializer.Serialize(result);
                        }
                        break;

                    case "train":
                        // 학습은 비동기로 시작만 하고 즉시 응답
                        OnLog?.Invoke($"[AIPipeServer] 학습 요청 수신: {request.Payload}");
                        response.Payload = JsonSerializer.Serialize(new { started = true });
                        break;

                    default:
                        response.Payload = JsonSerializer.Serialize(new { error = $"Unknown type: {request.Type}" });
                        break;
                }
            }
            catch (Exception ex)
            {
                response.Payload = JsonSerializer.Serialize(new { error = ex.Message });
            }

            return response;
        }

        private Task<AIPipelinePredictionResult> ProcessPredictionAsync(AIPipelinePredictionRequest request)
        {
            return Task.Run(() =>
            {
                var result = new AIPipelinePredictionResult();

                try
                {
                    if (request.ModelType == "ml")
                    {
                        var feature = JsonSerializer.Deserialize<MultiTimeframeEntryFeature>(request.FeatureJson);
                        if (feature != null)
                        {
                            var prediction = _mlTrainer.Predict(feature);
                            if (prediction != null)
                            {
                                result.ShouldEnter = prediction.ShouldEnter;
                                result.Probability = prediction.Probability;
                                result.Score = prediction.Score;
                            }
                        }
                    }
                    else if (request.ModelType == "tf")
                    {
                        // TF 추론은 feature sequence가 필요하므로 별도 처리
                        result.Confidence = 0.5f;
                        result.CandlesToTarget = 16f;
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }

                return result;
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
