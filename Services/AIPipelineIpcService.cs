using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [Stage3] AI 엔진 프로세스 분리를 위한 Named Pipes IPC 인프라
    ///
    /// 구조:
    ///   UI Process (TradingBot.exe)  ←──Named Pipe──→  AI Engine Process
    ///   - 차트/주문/UI 렌더링                           - ML.NET 추론
    ///   - 사용자 입력                                   - TensorFlow 추론
    ///   - 포지션 관리                                   - 학습/재학습
    ///
    /// 이점:
    ///   - UI가 뻗어도 AI 엔진은 계속 작동 (주문 실행 안정성)
    ///   - AI 메모리(GC) 이슈가 UI 렌더링에 영향 없음
    ///   - AI 엔진만 독립적으로 업데이트 가능
    /// </summary>
    public class AIPipelineIpcService : IDisposable
    {
        private const string PipeName = "TradingBot_AI_Pipeline";
        private const int ConnectionTimeoutMs = 5000;
        private const int ReadTimeoutMs = 3000;

        private NamedPipeClientStream? _pipeClient;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private CancellationTokenSource? _readLoopCts;
        private Task? _readLoopTask;
        private bool _isConnected;
        private bool _disposed;
        private int _requestId;

        public event Action<string>? OnLog;
        public event Action<string>? OnError;
        public bool IsConnected => _isConnected;

        /// <summary>
        /// AI 엔진 프로세스에 연결 시도
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            try
            {
                _pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(ConnectionTimeoutMs);

                await _pipeClient.ConnectAsync(cts.Token);

                _reader = new StreamReader(_pipeClient, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_pipeClient, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                _isConnected = true;

                // 응답 수신 루프 시작
                _readLoopCts = new CancellationTokenSource();
                _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));

                OnLog?.Invoke("[IPC] AI 엔진 프로세스 연결 성공");
                return true;
            }
            catch (OperationCanceledException)
            {
                OnLog?.Invoke("[IPC] AI 엔진 연결 타임아웃 — 인프로세스 모드 사용");
                return false;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[IPC] AI 엔진 연결 실패: {ex.Message} — 인프로세스 모드 사용");
                return false;
            }
        }

        /// <summary>
        /// ML.NET 예측 요청 (Named Pipe 경유)
        /// </summary>
        public async Task<AIPipelinePredictionResult?> PredictAsync(AIPipelinePredictionRequest request, CancellationToken token = default)
        {
            if (!_isConnected)
                return null;

            try
            {
                var id = Interlocked.Increment(ref _requestId).ToString();
                var envelope = new IpcEnvelope
                {
                    Id = id,
                    Type = "predict",
                    Payload = JsonSerializer.Serialize(request)
                };

                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests[id] = tcs;

                // 타임아웃 등록
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(ReadTimeoutMs);
                using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

                await SendAsync(JsonSerializer.Serialize(envelope));

                var responseJson = await tcs.Task;
                return JsonSerializer.Deserialize<AIPipelinePredictionResult>(responseJson);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"[IPC] 예측 요청 실패: {ex.Message}");
                _isConnected = false;
                return null;
            }
        }

        /// <summary>
        /// 학습 요청 전송 (비동기, fire-and-forget 가능)
        /// </summary>
        public async Task<bool> RequestTrainingAsync(string trainingDataPath, CancellationToken token = default)
        {
            if (!_isConnected)
                return false;

            try
            {
                var id = Interlocked.Increment(ref _requestId).ToString();
                var envelope = new IpcEnvelope
                {
                    Id = id,
                    Type = "train",
                    Payload = trainingDataPath
                };

                await SendAsync(JsonSerializer.Serialize(envelope));
                OnLog?.Invoke($"[IPC] 학습 요청 전송 완료: {trainingDataPath}");
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"[IPC] 학습 요청 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 헬스체크 핑
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken token = default)
        {
            if (!_isConnected)
                return false;

            try
            {
                var id = Interlocked.Increment(ref _requestId).ToString();
                var envelope = new IpcEnvelope { Id = id, Type = "ping", Payload = "" };
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRequests[id] = tcs;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(1000);
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled());

                await SendAsync(JsonSerializer.Serialize(envelope));
                await tcs.Task;
                return true;
            }
            catch
            {
                _isConnected = false;
                return false;
            }
        }

        private async Task SendAsync(string message)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_writer != null)
                    await _writer.WriteLineAsync(message);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _reader != null)
                {
                    var line = await _reader.ReadLineAsync(token);
                    if (line == null)
                    {
                        _isConnected = false;
                        OnLog?.Invoke("[IPC] AI 엔진 연결 종료");
                        break;
                    }

                    try
                    {
                        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line);
                        if (envelope?.Id != null && _pendingRequests.TryRemove(envelope.Id, out var tcs))
                        {
                            tcs.TrySetResult(envelope.Payload ?? "");
                        }
                    }
                    catch (JsonException ex)
                    {
                        OnError?.Invoke($"[IPC] 응답 파싱 실패: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _isConnected = false;
                OnError?.Invoke($"[IPC] 수신 루프 오류: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _readLoopCts?.Cancel();
            _reader?.Dispose();
            _writer?.Dispose();
            _pipeClient?.Dispose();
            _writeLock.Dispose();

            foreach (var kv in _pendingRequests)
                kv.Value.TrySetCanceled();
            _pendingRequests.Clear();
        }
    }

    // ─── IPC 프로토콜 모델 ─────────────────────────────────

    /// <summary>Named Pipe 메시지 봉투</summary>
    public class IpcEnvelope
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";   // "predict", "train", "ping", "response"
        public string? Payload { get; set; }
    }

    /// <summary>예측 요청 (UI → AI 엔진)</summary>
    public class AIPipelinePredictionRequest
    {
        public string Symbol { get; set; } = "";
        public string ModelType { get; set; } = "ml";  // "ml" or "tf"
        public string FeatureJson { get; set; } = "";   // MultiTimeframeEntryFeature JSON
    }

    /// <summary>예측 결과 (AI 엔진 → UI)</summary>
    public class AIPipelinePredictionResult
    {
        public bool ShouldEnter { get; set; }
        public float Probability { get; set; }
        public float Score { get; set; }
        public float CandlesToTarget { get; set; }
        public float Confidence { get; set; }
        public string? Error { get; set; }
    }
}
