using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TradingBot.Services
{
    /// <summary>
    /// TorchSharp 초기화를 안전하게 처리하는 유틸리티 클래스.
    /// 네이티브 라이브러리 크래시(0xc0000005)는 .NET try-catch로 잡을 수 없으므로
    /// 서브프로세스 프로브 방식으로 사전 호환성 검증 후 초기화합니다.
    /// </summary>
    public static class TorchInitializer
    {
        private const string ProbePathEnvVar = "TRADINGBOT_TORCH_PROBE_PATH";
        private static bool _initialized = false;
        private static bool _available = false;
        private static string? _errorMessage = null;
        private static readonly string _probeCachePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", ".torch_probe_result");

        /// <summary>
        /// TorchSharp가 사용 가능한지 확인
        /// </summary>
        public static bool IsAvailable => _available;

        /// <summary>
        /// 초기화 오류 메시지 (있는 경우)
        /// </summary>
        public static string? ErrorMessage => _errorMessage;

        /// <summary>
        /// 서브프로세스 프로브 전용 진입점.
        /// 프로세스 인수에 --torch-probe가 있으면 호출하여 TorchSharp 호환성만 테스트하고 종료합니다.
        /// </summary>
        /// <returns>프로브 모드이면 true (앱을 즉시 종료해야 함), 아니면 false</returns>
        public static bool HandleProbeIfRequested(string[] args)
        {
            bool isProbe = false;
            foreach (var arg in args)
            {
                if (arg == "--torch-probe")
                {
                    isProbe = true;
                    break;
                }
            }
            if (!isProbe) return false;

            try
            {
                string probeResultPath = GetProbeResultPath();

                // TorchSharp의 static 초기화 트리거 — 네이티브 크래시 시 이 프로세스만 종료됨
                var device = TorchSharp.torch.CPU;
                File.WriteAllText(probeResultPath, "OK");
                Environment.Exit(0);
            }
            catch (Exception)
            {
                try
                {
                    string probeResultPath = GetProbeResultPath();
                    File.WriteAllText(probeResultPath, "FAIL");
                }
                catch { /* 파일 쓰기 실패 시 무시 */ }
                Environment.Exit(1);
            }
            return true; // 도달하지 않지만 컴파일러용
        }

        /// <summary>
        /// TorchSharp 초기화 시도 (서브프로세스 프로브 → 실제 초기화 순서)
        /// </summary>
        public static bool TryInitialize()
        {
            if (_initialized)
                return _available;

            _initialized = true;

            string? disableTorch = Environment.GetEnvironmentVariable("TRADINGBOT_DISABLE_TORCH");
            if (string.Equals(disableTorch, "1", StringComparison.OrdinalIgnoreCase))
            {
                _errorMessage = "환경 변수(TRADINGBOT_DISABLE_TORCH=1)에 의해 TorchSharp 기능이 비활성화되었습니다.";
                _available = false;
                TrySaveProbeFail();
                return false;
            }

            // ── 1단계: 캐시된 프로브 결과 확인 ──
            if (File.Exists(_probeCachePath))
            {
                try
                {
                    string cached = File.ReadAllText(_probeCachePath).Trim();
                    if (cached == "FAIL")
                    {
                        _errorMessage = "이전 프로브에서 TorchSharp 환경 비호환이 감지되었습니다.\n" +
                                        "Transformer 기능이 비활성화됩니다.\n\n" +
                                        "해결 방법:\n" +
                                        "1. Visual C++ Redistributable 2015-2022 x64 설치\n" +
                                        "   다운로드: https://aka.ms/vs/17/release/vc_redist.x64.exe\n" +
                                        $"2. 프로브 캐시 파일 삭제 후 앱 재시작: {_probeCachePath}";
                        Debug.WriteLine($"[TorchInitializer] 캐시된 프로브 결과: FAIL — 초기화 건너뜀");
                        _available = false;
                        return false;
                    }
                    // cached == "OK" → 프로브 성공 캐시, 아래로 계속 진행
                    if (string.IsNullOrWhiteSpace(cached))
                    {
                        _errorMessage = "TorchSharp 프로브 캐시가 비정상(빈 값)입니다. 안전 모드로 Transformer 기능을 비활성화합니다.";
                        Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                        _available = false;
                        TrySaveProbeResult("FAIL");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TorchInitializer] 프로브 캐시 읽기 실패: {ex.Message}");
                    // 캐시 손상 → 프로브 재실행
                }
            }

            // ── 2단계: 서브프로세스 프로브 실행 ──
            bool probeResult = RunSubprocessProbe();
            if (!probeResult)
            {
                _available = false;
                return false;
            }

            // ── 3단계: 프로브 통과 후 실제 초기화 ──
            try
            {
                var device = TorchSharp.torch.CPU;
                Debug.WriteLine($"[TorchInitializer] TorchSharp 초기화 성공 - Device: {device}");

                Debug.WriteLine("[TorchInitializer] CUDA 체크 생략 (안전 모드: CPU 기본)");

                _available = true;
                return true;
            }
            catch (TypeInitializationException ex)
            {
                _errorMessage = $"TorchSharp 초기화 실패: {ex.InnerException?.Message ?? ex.Message}\n\n" +
                               "해결 방법:\n" +
                               "1. Visual C++ Redistributable 2015-2022 x64 설치\n" +
                               "   다운로드: https://aka.ms/vs/17/release/vc_redist.x64.exe\n" +
                               "2. 앱 재시작\n\n" +
                               "TorchSharp 기능(PPO 에이전트, Transformer)은 비활성화됩니다.\n" +
                               "ML.NET 기반 예측은 정상 작동합니다.";
                Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                _available = false;
                TrySaveProbeFail(); // 다음 실행 시 바로 건너뛰도록 캐시
                return false;
            }
            catch (Exception ex)
            {
                _errorMessage = $"TorchSharp 초기화 중 예외 발생: {ex.Message}";
                Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                _available = false;
                TrySaveProbeFail();
                return false;
            }
        }

        /// <summary>
        /// 프로브 실패 결과를 캐시 파일에 저장
        /// </summary>
        public static void ClearProbeCache()
        {
            try { if (File.Exists(_probeCachePath)) File.Delete(_probeCachePath); }
            catch { /* ignore */ }
        }

        /// <summary>
        /// 서브프로세스로 TorchSharp 호환성 프로브 실행.
        /// 네이티브 크래시(0xc0000005)가 발생해도 부모 프로세스는 안전합니다.
        /// </summary>
        private static bool RunSubprocessProbe()
        {
            try
            {
                // 네이티브 DLL 사전 로드 검증 (빠른 실패)
                if (!PreCheckNativeLibraries())
                {
                    TrySaveProbeResult("FAIL");
                    return false;
                }

                string exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TradingBot.exe");

                if (!File.Exists(exePath))
                {
                    Debug.WriteLine($"[TorchInitializer] 프로브 실행 파일 없음: {exePath} — 직접 초기화 시도");
                    TrySaveProbeResult("OK"); // 개발 환경에서는 OK 가정
                    return true;
                }

                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--torch-probe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                process.StartInfo.EnvironmentVariables[ProbePathEnvVar] = _probeCachePath;

                // 프로브 전에 이전 캐시 파일 삭제 (프로브 프로세스가 새로 작성하도록)
                if (File.Exists(_probeCachePath))
                {
                    try { File.Delete(_probeCachePath); } catch { }
                }

                process.Start();
                bool exited = process.WaitForExit(15000); // 15초 타임아웃

                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    _errorMessage = "TorchSharp 프로브 타임아웃 (15초). Transformer 기능이 비활성화됩니다.";
                    Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                    TrySaveProbeResult("FAIL");
                    return false;
                }

                // 프로브 프로세스가 직접 작성한 결과 파일 확인
                string probeFileResult = "";
                if (File.Exists(_probeCachePath))
                {
                    try { probeFileResult = File.ReadAllText(_probeCachePath).Trim(); } catch { }
                }

                if (process.ExitCode == 0 && probeFileResult == "OK")
                {
                    Debug.WriteLine("[TorchInitializer] 서브프로세스 프로브 성공 — TorchSharp 사용 가능");
                    return true;
                }
                else
                {
                    string detail = $"Exit code: {process.ExitCode}";
                    if (process.ExitCode != 0 && string.IsNullOrEmpty(probeFileResult))
                    {
                        // 프로브 프로세스가 네이티브 크래시(0xc0000005)로 비정상 종료
                        detail = $"네이티브 라이브러리 크래시 (Exit: 0x{process.ExitCode:X})";
                    }

                    _errorMessage = $"TorchSharp 환경 비호환 ({detail}).\n" +
                                    "Transformer 기능이 비활성화됩니다.\n\n" +
                                    "해결 방법:\n" +
                                    "1. Visual C++ Redistributable 2015-2022 x64 설치\n" +
                                    "   다운로드: https://aka.ms/vs/17/release/vc_redist.x64.exe\n" +
                                    $"2. 프로브 캐시 파일 삭제 후 앱 재시작: {_probeCachePath}";
                    Debug.WriteLine($"[TorchInitializer] 프로브 실패 — {detail}");
                    TrySaveProbeResult("FAIL");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TorchInitializer] 프로브 실행 예외: {ex.Message} — 직접 초기화 시도");
                // 프로브 자체가 실행 불가능하면 직접 초기화 시도 (위험하지만 fallback)
                return true;
            }
        }

        /// <summary>
        /// 네이티브 DLL 존재/로드 가능 여부 사전 검증
        /// </summary>
        private static bool PreCheckNativeLibraries()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // LibTorchSharp.dll 확인 (TorchSharp managed → native 브릿지)
            string libTorchSharpPath = Path.Combine(baseDir, "LibTorchSharp.dll");
            if (!File.Exists(libTorchSharpPath))
            {
                // runtimes 폴더 확인
                string runtimePath = Path.Combine(baseDir, "runtimes", "win-x64", "native", "LibTorchSharp.dll");
                if (!File.Exists(runtimePath))
                {
                    _errorMessage = "LibTorchSharp.dll을 찾을 수 없습니다. TorchSharp NuGet 패키지를 확인하세요.";
                    Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                    return false;
                }
            }

            // torch_cpu.dll 존재 확인 (실제 로드는 서브프로세스에서)
            string torchCpuPath = Path.Combine(baseDir, "torch_cpu.dll");
            if (!File.Exists(torchCpuPath))
            {
                string runtimePath = Path.Combine(baseDir, "runtimes", "win-x64", "native", "torch_cpu.dll");
                if (!File.Exists(runtimePath))
                {
                    _errorMessage = "torch_cpu.dll을 찾을 수 없습니다. libtorch-cpu NuGet 패키지를 확인하세요.";
                    Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                    return false;
                }
            }

            return true;
        }

        private static void TrySaveProbeResult(string result)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_probeCachePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_probeCachePath, result);
            }
            catch (Exception ex) { Debug.WriteLine($"[TorchInitializer] 프로브 결과 저장 실패: {ex.Message}"); }
        }

        private static void TrySaveProbeFail()
        {
            TrySaveProbeResult("FAIL");
        }

        private static string GetProbeResultPath()
        {
            string? envPath = Environment.GetEnvironmentVariable(ProbePathEnvVar);
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return envPath;
            }

            return _probeCachePath;
        }

        /// <summary>
        /// 안전하게 TorchSharp 기능을 실행
        /// </summary>
        public static bool TryExecute(Action action, out string? error)
        {
            error = null;

            if (!TryInitialize())
            {
                error = _errorMessage;
                return false;
            }

            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                error = $"TorchSharp 실행 중 오류: {ex.Message}";
                Debug.WriteLine($"[TorchInitializer] {error}");
                return false;
            }
        }

        /// <summary>
        /// 안전하게 TorchSharp 기능을 실행하고 결과 반환
        /// </summary>
        public static bool TryExecute<T>(Func<T> func, out T? result, out string? error)
        {
            result = default;
            error = null;

            if (!TryInitialize())
            {
                error = _errorMessage;
                return false;
            }

            try
            {
                result = func();
                return true;
            }
            catch (Exception ex)
            {
                error = $"TorchSharp 실행 중 오류: {ex.Message}";
                Debug.WriteLine($"[TorchInitializer] {error}");
                return false;
            }
        }

        /// <summary>
        /// Torch 디바이스 선택 (기본: CPU)
        /// TRADINGBOT_ENABLE_CUDA=1 일 때만 CUDA 확인을 시도합니다.
        /// </summary>
        public static TorchSharp.torch.Device ResolveDevice()
        {
            if (!TryInitialize())
            {
                return TorchSharp.torch.CPU;
            }

            string? envValue = Environment.GetEnvironmentVariable("TRADINGBOT_ENABLE_CUDA");
            bool allowCuda = string.Equals(envValue, "1", StringComparison.OrdinalIgnoreCase);
            if (!allowCuda)
            {
                return TorchSharp.torch.CPU;
            }

            try
            {
                return TorchSharp.torch.cuda.is_available() ? TorchSharp.torch.CUDA : TorchSharp.torch.CPU;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TorchInitializer] CUDA 디바이스 확인 실패, CPU로 폴백: {ex.Message}");
                return TorchSharp.torch.CPU;
            }
        }
    }
}
