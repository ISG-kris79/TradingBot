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
    /// Named Pipe 서버 (AI 서비스 프로세스용)
    /// </summary>
    public class NamedPipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<string, Task<string>> _messageHandler;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private bool _disposed;

        public event Action<string>? OnLog;
        public bool IsRunning { get; private set; }

        public NamedPipeServer(string pipeName, Func<string, Task<string>> messageHandler)
        {
            _pipeName = pipeName;
            _messageHandler = messageHandler;
        }

        public void Start()
        {
            if (IsRunning)
                return;

            _cts = new CancellationTokenSource();
            IsRunning = true;
            _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
            OnLog?.Invoke($"[NamedPipeServer] Started on pipe: {_pipeName}");
        }

        public void Stop()
        {
            if (!IsRunning)
                return;

            _cts?.Cancel();
            IsRunning = false;
            OnLog?.Invoke($"[NamedPipeServer] Stopped");
        }

        private async Task ServerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipeServer = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    OnLog?.Invoke($"[NamedPipeServer] Waiting for connection...");
                    await pipeServer.WaitForConnectionAsync(token);
                    OnLog?.Invoke($"[NamedPipeServer] Client connected");

                    await HandleClientAsync(pipeServer, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[NamedPipeServer] Error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipeServer, CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipeServer, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                string? requestJson = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestJson))
                    return;

                OnLog?.Invoke($"[NamedPipeServer] Received: {requestJson.Substring(0, Math.Min(100, requestJson.Length))}...");

                string responseJson = await _messageHandler(requestJson);

                await writer.WriteLineAsync(responseJson);
                OnLog?.Invoke($"[NamedPipeServer] Sent response");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[NamedPipeServer] HandleClient error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _cts?.Dispose();
            _disposed = true;
        }
    }
}
