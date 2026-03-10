using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services.ProcessAI
{
    /// <summary>
    /// AI 서비스 프로세스 관리자
    /// </summary>
    public class AIServiceProcessManager : IDisposable
    {
        private readonly string _serviceName;
        private readonly string _exePath;
        private readonly string _pipeName;
        private Process? _process;
        private bool _disposed;
        private CancellationTokenSource? _healthCheckCts;

        public event Action<string>? OnLog;
        public event Action<string>? OnError;

        public bool IsRunning => _process != null && !_process.HasExited;
        public int? ProcessId => _process?.Id;

        public AIServiceProcessManager(string serviceName, string exeName, string pipeName)
        {
            _serviceName = serviceName;
            _pipeName = pipeName;
            
            // 실행 파일 경로 (메인 앱과 같은 폴더 또는 하위 폴더)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _exePath = Path.Combine(baseDir, exeName);
            
            // Services 폴더에 있을 수도 있음
            if (!File.Exists(_exePath))
            {
                _exePath = Path.Combine(baseDir, "Services", exeName);
            }
        }

        public async Task<bool> StartAsync(int maxRetries = 3)
        {
            if (IsRunning)
            {
                OnLog?.Invoke($"[{_serviceName}] Already running (PID: {ProcessId})");
                return true;
            }

            if (!File.Exists(_exePath))
            {
                OnError?.Invoke($"[{_serviceName}] Executable not found: {_exePath}");
                return false;
            }

            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    OnLog?.Invoke($"[{_serviceName}] Starting process... (attempt {retry + 1}/{maxRetries})");

                    _process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = _exePath,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(_exePath)
                        }
                    };

                    _process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            OnLog?.Invoke($"[{_serviceName}] {e.Data}");
                    };

                    _process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                            OnError?.Invoke($"[{_serviceName}] ERROR: {e.Data}");
                    };

                    _process.Exited += (s, e) =>
                    {
                        OnError?.Invoke($"[{_serviceName}] Process exited unexpectedly (PID: {ProcessId})");
                    };

                    _process.EnableRaisingEvents = true;
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();

                    OnLog?.Invoke($"[{_serviceName}] Process started (PID: {_process.Id})");

                    // 프로세스가 시작되고 Named Pipe가 준비될 때까지 대기
                    await Task.Delay(2000);

                    // Health check로 준비 상태 확인
                    bool isHealthy = await PerformHealthCheckAsync();
                    if (isHealthy)
                    {
                        OnLog?.Invoke($"[{_serviceName}] Service is healthy and ready");
                        StartHealthCheckMonitoring();
                        return true;
                    }

                    OnError?.Invoke($"[{_serviceName}] Health check failed, retrying...");
                    Stop();
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"[{_serviceName}] Start error: {ex.Message}");
                    Stop();
                    await Task.Delay(1000);
                }
            }

            OnError?.Invoke($"[{_serviceName}] Failed to start after {maxRetries} attempts");
            return false;
        }

        public void Stop()
        {
            StopHealthCheckMonitoring();

            if (_process == null || _process.HasExited)
                return;

            try
            {
                OnLog?.Invoke($"[{_serviceName}] Stopping process (PID: {_process.Id})...");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
                OnLog?.Invoke($"[{_serviceName}] Process stopped");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"[{_serviceName}] Stop error: {ex.Message}");
            }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        private async Task<bool> PerformHealthCheckAsync()
        {
            try
            {
                using var client = new NamedPipeClient(_pipeName, timeoutMs: 5000);
                var request = new HealthCheckRequest { Command = "health" };
                var response = await client.SendRequestAsync<HealthCheckRequest, HealthCheckResponse>(request);
                return response?.Success == true;
            }
            catch
            {
                return false;
            }
        }

        private void StartHealthCheckMonitoring()
        {
            _healthCheckCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!_healthCheckCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(30000, _healthCheckCts.Token); // 30초마다 체크

                    if (!IsRunning)
                    {
                        OnError?.Invoke($"[{_serviceName}] Process died, attempting restart...");
                        await StartAsync();
                    }
                    else
                    {
                        bool isHealthy = await PerformHealthCheckAsync();
                        if (!isHealthy)
                        {
                            OnError?.Invoke($"[{_serviceName}] Health check failed, restarting...");
                            Stop();
                            await StartAsync();
                        }
                    }
                }
            }, _healthCheckCts.Token);
        }

        private void StopHealthCheckMonitoring()
        {
            _healthCheckCts?.Cancel();
            _healthCheckCts?.Dispose();
            _healthCheckCts = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }
    }
}
