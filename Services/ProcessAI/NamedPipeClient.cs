using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services.ProcessAI
{
    /// <summary>
    /// Named Pipe 클라이언트 (메인 TradingBot 프로세스용)
    /// </summary>
    public class NamedPipeClient : IDisposable
    {
        private readonly string _pipeName;
        private readonly int _timeoutMs;
        private bool _disposed;

        public event Action<string>? OnLog;

        public NamedPipeClient(string pipeName, int timeoutMs = 30000)
        {
            _pipeName = pipeName;
            _timeoutMs = timeoutMs;
        }

        public async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken token = default)
            where TRequest : AIServiceRequest
            where TResponse : AIServiceResponse
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                OnLog?.Invoke($"[NamedPipeClient] Connecting to {_pipeName}...");
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(_timeoutMs);
                
                await pipeClient.ConnectAsync(cts.Token);
                OnLog?.Invoke($"[NamedPipeClient] Connected");

                using var writer = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipeClient, Encoding.UTF8, leaveOpen: true);

                string requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                await writer.WriteLineAsync(requestJson);
                OnLog?.Invoke($"[NamedPipeClient] Sent request: {request.Command}");

                string? responseJson = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(responseJson))
                {
                    OnLog?.Invoke($"[NamedPipeClient] Empty response");
                    return null;
                }

                var response = JsonSerializer.Deserialize<TResponse>(responseJson);
                OnLog?.Invoke($"[NamedPipeClient] Received response: Success={response?.Success}");

                return response;
            }
            catch (TimeoutException)
            {
                OnLog?.Invoke($"[NamedPipeClient] Timeout connecting to {_pipeName}");
                return null;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NamedPipeClient] Error: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }
    }
}
