using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.Win32;

namespace TradingBot.Services
{
    public static class VisualCppRedistributableInstaller
    {
        private const string VcRuntimeRegKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
        private const string VcRuntimeDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        public static bool IsInstalledX64()
        {
            try
            {
                // 방법 1: 핵심 DLL 파일 직접 확인 (가장 정확함)
                string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string[] criticalDlls = { "msvcp140.dll", "vcruntime140.dll", "msvcp140_1.dll" };

                bool allDllsFound = criticalDlls.All(dll => File.Exists(Path.Combine(system32, dll)));
                if (allDllsFound)
                {
                    Debug.WriteLine("[VC++] ✓ DLL 파일 확인: VC++ 2015-2022 x64 설치됨");
                    return true;
                }

                // 방법 2: 레지스트리 확인 (다양한 경로)
                string[] regPaths = new[]
                {
                    @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
                    @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
                    @"SOFTWARE\Classes\Installer\Dependencies\Microsoft.VS.VC_RuntimeMinimumRuntime-14.0-x64",
                    @"SOFTWARE\Microsoft\VisualStudio\17.0\VC\Runtimes\x64"
                };

                foreach (var regPath in regPaths)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                        using var key = baseKey.OpenSubKey(regPath);

                        if (key != null)
                        {
                            var installedValue = key.GetValue("Installed");
                            if (installedValue is int installedInt && installedInt == 1)
                            {
                                Debug.WriteLine($"[VC++] ✓ 레지스트리({regPath}) 확인: VC++ 설치됨");
                                return true;
                            }
                            if (installedValue is long installedLong && installedLong == 1)
                            {
                                Debug.WriteLine($"[VC++] ✓ 레지스트리({regPath}) 확인: VC++ 설치됨");
                                return true;
                            }
                        }
                    }
                    catch { /* 특정 경로가 없을 수 있음 */ }
                }

                Debug.WriteLine("[VC++] ⚠ VC++ 설치 감지 불가");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VC++] Failed to detect install state: {ex.Message}");
                // 에러 발생 시에도 파일 확인 시도
                try
                {
                    string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    return File.Exists(Path.Combine(system32, "vcruntime140.dll"));
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool TryInstallX64(out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "vc_redist.x64.exe");

                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    var bytes = client.GetByteArrayAsync(VcRuntimeDownloadUrl).GetAwaiter().GetResult();
                    File.WriteAllBytes(tempPath, bytes);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    errorMessage = "Failed to launch the installer.";
                    return false;
                }

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    errorMessage = $"Installer exit code: {process.ExitCode}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Debug.WriteLine($"[VC++] Install failed: {ex}");
                return false;
            }
        }
    }
}
