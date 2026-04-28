using System.Windows;
using Velopack;
using Velopack.Sources;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Linq;
using System.Text.Json;
using TradingBot.Services;
using TradingBot.Services.Backtest;
using TradingBot.Models;

namespace TradingBot
{
    public partial class App : Application
    {
        private static Mutex? _mutex = null;
        private static bool _ownsMutex = false;
        private static bool _updateErrorShown = false;

        public App()
        {
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void CurrentDomain_FirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
        {
            try
            {
                if (e.Exception is not IndexOutOfRangeException && e.Exception is not ArgumentOutOfRangeException)
                    return;

                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FIRST_CHANCE_RANGE_ERROR.txt");
                string currentStackTrace = new StackTrace(true).ToString();
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception.GetType().Name}\n" +
                    $"Message: {e.Exception.Message}\n" +
                    $"ExceptionStackTrace: {e.Exception.StackTrace}\n" +
                    $"CurrentStackTrace: {currentStackTrace}\n\n";
                System.IO.File.AppendAllText(logPath, logContent);
            }
            catch
            {
                // 로깅 실패는 무시
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BACKGROUND_UNHANDLED_ERROR.txt");
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BACKGROUND UNHANDLED EXCEPTION\n" +
                    $"IsTerminating: {e.IsTerminating}\n" +
                    $"Type: {ex?.GetType().FullName ?? e.ExceptionObject?.GetType().FullName}\n" +
                    $"Message: {ex?.Message ?? e.ExceptionObject?.ToString()}\n" +
                    $"StackTrace: {ex?.StackTrace}\n\n";
                System.IO.File.AppendAllText(logPath, logContent);
            }
            catch { }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UNOBSERVED_TASK_ERROR.txt");
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNOBSERVED TASK EXCEPTION\n" +
                    $"Message: {e.Exception.Message}\n" +
                    $"StackTrace: {e.Exception.StackTrace}\n\n";
                System.IO.File.AppendAllText(logPath, logContent);
            }
            catch { }

            e.SetObserved();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // [v5.10.15] 엔진 Stop 시 Task 취소로 발생하는 정상 예외 — 무시
            if (e.Exception is OperationCanceledException || e.Exception is TaskCanceledException)
            {
                e.Handled = true;
                return;
            }

            if (e.Exception is ArgumentException argumentException
                && argumentException.Message.Contains("'NaN'")
                && argumentException.Message.Contains("'Y1'"))
            {
                try
                {
                    global::TradingBot.MainWindow.Instance?.ViewModel?.RecoverFromChartRenderError();
                    Debug.WriteLine($"[App] LiveCharts NaN 렌더링 오류 복구: {argumentException.Message}");
                }
                catch { }

                e.Handled = true;
                return;
            }

            // [FIX] 배열 인덱스 범위 초과 오류 처리
            if (e.Exception is IndexOutOfRangeException indexEx)
            {
                Debug.WriteLine($"[App] 배열 인덱스 범위 초과 오류: {indexEx.Message}");
                Debug.WriteLine($"StackTrace: {indexEx.StackTrace}");
                
                try
                {
                    string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "INDEX_OUT_OF_RANGE_ERROR.txt");
                    string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INDEX OUT OF RANGE ERROR\n" +
                        $"Message: {indexEx.Message}\n" +
                        $"StackTrace: {indexEx.StackTrace}\n\n";
                    System.IO.File.AppendAllText(logPath, logContent);
                }
                catch { }

                global::TradingBot.MainWindow.Instance?.AddAlert($"⚠️ 데이터 처리 오류 발생 (복구됨)");
                e.Handled = true;
                return;
            }

            // [FIX] ArgumentOutOfRange 오류 처리
            if (e.Exception is ArgumentOutOfRangeException argOutEx)
            {
                Debug.WriteLine($"[App] 인수 범위 초과 오류: {argOutEx.Message}");
                Debug.WriteLine($"StackTrace: {argOutEx.StackTrace}");
                
                try
                {
                    string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ARG_OUT_OF_RANGE_ERROR.txt");
                    string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ARGUMENT OUT OF RANGE ERROR\n" +
                        $"Message: {argOutEx.Message}\n" +
                        $"StackTrace: {argOutEx.StackTrace}\n\n";
                    System.IO.File.AppendAllText(logPath, logContent);
                }
                catch { }

                global::TradingBot.MainWindow.Instance?.AddAlert($"⚠️ 데이터 범위 오류 발생 (복구됨)");
                e.Handled = true;
                return;
            }

            // 예외 메시지 팝업 및 로그 기록
            string exceptionType = e.Exception.GetType().FullName ?? e.Exception.GetType().Name;
            string msg = $"치명적 오류 발생\n" +
                $"Type: {exceptionType}\n" +
                $"Message: {e.Exception.Message}\n" +
                $"HResult: 0x{e.Exception.HResult:X8}\n\n" +
                $"StackTrace:\n{e.Exception.StackTrace}";
            
            if (e.Exception.InnerException != null)
            {
                msg += $"\n\nInner Exception: {e.Exception.InnerException.Message}";
            }

            // 파일로 에러 로그 저장
            try
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CRITICAL_ERROR.txt");
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRITICAL ERROR\n{msg}\n\n";
                System.IO.File.AppendAllText(logPath, logContent);
            }
            catch { }

            MessageBox.Show(msg, "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            System.Diagnostics.Debug.WriteLine($"[App] Unhandled Exception: {msg}");
            e.Handled = true;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            /* TensorFlow 전환 중 비활성화 - TorchSharp Probe
            // ── TorchSharp 서브프로세스 프로브 모드 ──
            // --torch-probe 인수로 실행된 경우, TorchSharp 호환성만 테스트하고 즉시 종료
            if (TorchInitializer.HandleProbeIfRequested(Environment.GetCommandLineArgs()))
            {
                this.Shutdown();
                return;
            }

            // ── [v2.4.22] 비정상 종료 감지 기반 Torch 안전모드 run-state 등록 ──
            TorchInitializer.RegisterStartupRunState();

            // ── [v2.4.21] 앱 버전 변경 시 기존 Transformer 모델 파일 자동 정리 ──
            // 이전 버전의 모델이 현재 아키텍처와 불일치하면 C++ abort(BEX64) 크래시 발생
            TorchInitializer.InvalidateModelsIfVersionChanged();
            */

            // ── Hybrid 주문 로직 백테스트 CLI 모드 ──
            if (HandleHybridBacktestCliIfRequested(e.Args))
            {
                this.Shutdown();
                return;
            }

            // ── 워크포워드 최적화 백테스트 CLI 모드 ──
            if (HandleWalkForwardOptimizeCliIfRequested(e.Args))
            {
                this.Shutdown();
                return;
            }

            // [v5.21.4] 중복 실행 방지 강화 — Local\ (권한 문제 회피) + 기존 프로세스 자동 종료 옵션
            //   기존 Global\ + catch 진행 = mutex 우회로 실제 중복 실행 발생
            const string mutexName = @"Local\TradingBot_SingleInstance_8F9A2B3C";
            try
            {
                _mutex = new Mutex(false, mutexName, out bool createdNew);
                _ownsMutex = false;
                bool got = false;
                try { got = _mutex.WaitOne(TimeSpan.Zero, false); }
                catch (AbandonedMutexException) { got = true; }

                if (!got)
                {
                    Debug.WriteLine("[App] 중복 실행 감지");
                    var result = MessageBox.Show(
                        "TradingBot이 이미 실행 중입니다.\n\n" +
                        "[예] 기존 인스턴스를 종료하고 새로 시작합니다.\n" +
                        "[아니오] 종료합니다.",
                        "중복 실행 감지",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            int curPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                            foreach (var p in System.Diagnostics.Process.GetProcessesByName("TradingBot"))
                            {
                                if (p.Id != curPid)
                                {
                                    try { p.Kill(true); p.WaitForExit(3000); }
                                    catch (Exception kex) { Debug.WriteLine($"kill {p.Id}: {kex.Message}"); }
                                }
                            }
                            // mutex 다시 획득 시도
                            try { got = _mutex.WaitOne(TimeSpan.FromSeconds(5), false); }
                            catch (AbandonedMutexException) { got = true; }
                        }
                        catch (Exception ex2) { Debug.WriteLine($"replace failed: {ex2.Message}"); }
                    }
                    if (!got) { this.Shutdown(); return; }
                }
                _ownsMutex = true;
                Debug.WriteLine("[App] 단일 인스턴스 확인 완료");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Mutex 생성 오류: {ex.Message}");
                // [v5.21.4] catch 시 계속 진행 X — mutex 실패 = 중복 실행 가능 → 안전 종료
                MessageBox.Show($"단일 인스턴스 검사 실패:\n{ex.Message}\n\n중복 실행 위험으로 종료합니다.",
                    "보안 종료", MessageBoxButton.OK, MessageBoxImage.Warning);
                this.Shutdown();
                return;
            }

            // [중요] Velopack 런타임 체크 비활성화 (자체 포함 배포이므로 불필요)
            // SelfContained=true로 설정되어 있으므로 Velopack의 런타임 강제 설치 제거
            // Velopack 초기화로 인한 ".NET Runtime 설치" 메시지 완전 제거
            try
            {
                // Velopack은 주로 업데이트 체크 목적이므로 try-catch로 안전하게 처리
                // 실패해도 앱 실행에는 영향 없음
                VelopackApp.Build().Run();
            }
            catch
            {
                // Velopack 실패 - 무시하고 계속 (이미 .NET Runtime이 실행 중)
                Debug.WriteLine("[App] Velopack 초기화 스킵됨 (자체 포함 배포)");
            }

            // 명령줄 인자 확인: ConnectionString 암호화 도구
            if (e.Args.Length > 0 && e.Args[0] == "--encrypt-connection")
            {
                ConnectionStringEncryptor.RunInteractive();
                this.Shutdown();
                return;
            }

            // 명령줄 인자 확인: 복호화 테스트
            if (e.Args.Length > 0 && e.Args[0] == "--test-decrypt")
            {
                DecryptionTester.RunTest();
                this.Shutdown();
                return;
            }

            // 설정 로드
            AppConfig.Load();

            // [Phase 15] 사용자별 GeneralSettings는 로그인 후 LoginWindow에서 로드
            // (로그인 전에는 사용자 정보가 없으므로 appsettings.json의 기본값 사용)

            // [Phase 16] TorchSharp 실행에 필요한 VC++ 재배포 패키지 확인
            // (자체 포함 배포로 인해 개발 환경에서는 스킵)
#if !DEBUG  // 릴리스 빌드에서만 체크
            if (!Services.VisualCppRedistributableInstaller.IsInstalledX64())
            {
                var installResult = MessageBox.Show(
                    "TorchSharp 기능을 위해 Visual C++ Redistributable 2015-2022 x64가 필요합니다.\n\n" +
                    "지금 자동 설치를 진행할까요? (관리자 권한 필요)",
                    "필수 구성요소 설치",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (installResult == MessageBoxResult.Yes)
                {
                    if (Services.VisualCppRedistributableInstaller.TryInstallX64(out var installError))
                    {
                        MessageBox.Show(
                            "설치가 완료되었습니다. TorchSharp 기능 활성화를 위해 앱을 재시작해주세요.",
                            "설치 완료",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Visual C++ 설치 실패:\n{installError}",
                            "설치 실패",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
            }
#else
            Debug.WriteLine("[App] 🔧 DEBUG 모드 - VC++ Redistributable 체크 스킵");
#endif

            // [안정성] TorchSharp 초기화는 앱 시작 시 수행하지 않습니다.
            // 네이티브 라이브러리(torch_cpu.dll)가 프로세스 크래시(0xc0000005)를 유발할 수 있으므로,
            // TradingEngine 시작 시 서브프로세스 프로브로 안전하게 검증 후 초기화합니다.
            Debug.WriteLine("[App] TorchSharp 초기화는 엔진 시작 시 서브프로세스 프로브로 지연 실행됩니다.");

            // [추가] DB 연결 문자열 확인 및 설정 유도
            if (string.IsNullOrEmpty(AppConfig.ConnectionString))
            {
                var result = MessageBox.Show("데이터베이스 연결 설정이 되어있지 않습니다.\n설정 창으로 이동하시겠습니까?",
                    "설정 필요", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var settingsWindow = new SettingsWindow();
                    settingsWindow.ShowDialog();
                    AppConfig.Load(); // 설정 후 다시 로드
                }
            }

            base.OnStartup(e);

            // 로그인 창을 먼저 띄움
            var loginWindow = new LoginWindow();
            this.MainWindow = loginWindow;
            loginWindow.Show();

            // [v5.0.8] 로그인 창 표시 직후 업데이트 확인 (로그인 전에도 체크)
            _ = loginWindow.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    bool updated = await CheckForUpdatesBeforeLoginAsync();
                    if (updated) return;  // 업데이트 적용 시 재시작됨
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] 로그인 전 업데이트 확인 중 오류: {ex.Message}");
                }
            });

        }
        /// <summary>
        /// 로그인 후 업데이트 체크 및 적용 (업데이트 적용 시 true 반환)
        /// </summary>
        /// <returns>업데이트가 적용되어 재시작 예정이면 true</returns>
        private async System.Threading.Tasks.Task<bool> CheckForUpdatesAfterLoginAsync()
        {
            return await CheckForUpdatesCoreAsync("로그인 후");
        }

        /// <summary>
        /// [v5.0.8] 로그인 창 표시 직후 업데이트 확인 및 자동 적용
        /// </summary>
        /// <returns>업데이트가 적용되어 재시작 예정이면 true</returns>
        private async System.Threading.Tasks.Task<bool> CheckForUpdatesBeforeLoginAsync()
        {
            return await CheckForUpdatesCoreAsync("앱 시작");
        }

        public static System.Threading.Tasks.Task EnsureUpdateCheckAsync()
        {
            // 이제 앱 시작 시 업데이트를 먼저 체크하므로 이 메서드는 사용되지 않음
            return System.Threading.Tasks.Task.CompletedTask;
        }

        [Obsolete("앱 시작 시 CheckForUpdatesBeforeLoginAsync로 대체됨")]
        private System.Threading.Tasks.Task StartUpdateCheckAsync()
        {
            // 앱 시작 시 이미 업데이트를 체크했으므로 추가 체크 불필요
            return System.Threading.Tasks.Task.CompletedTask;
        }

        [Obsolete("앱 시작 시 CheckForUpdatesBeforeLoginAsync로 대체됨")]
        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            await CheckForUpdatesCoreAsync("수동 확인");
        }

        private async System.Threading.Tasks.Task DownloadAndApplyUpdateAsync(UpdateManager mgr, Velopack.UpdateInfo updateInfo)
        {
            try
            {
                Debug.WriteLine("[Update] 업데이트 다운로드 시작...");
                await this.Dispatcher.InvokeAsync(() =>
                {
                    TradingBot.MainWindow.Instance?.AddLog($"📥 업데이트 다운로드 중... (v{updateInfo.TargetFullRelease.Version})");
                });

                UpdateDialogWindow? progressWindow = null;

                await this.Dispatcher.InvokeAsync(() =>
                {
                    progressWindow = new UpdateDialogWindow(
                        "업데이트 다운로드",
                        "최신 버전 준비 중",
                        "업데이트 파일을 다운로드하고 있습니다...",
                        isProgressMode: true);

                    if (Current?.MainWindow != null)
                    {
                        progressWindow.Owner = Current.MainWindow;
                    }

                    progressWindow.Show();
                });

                await mgr.DownloadUpdatesAsync(updateInfo);
                Debug.WriteLine("[Update] 업데이트 다운로드 완료");

                await this.Dispatcher.InvokeAsync(() =>
                {
                    progressWindow?.AllowClose();
                    progressWindow?.Close();
                    TradingBot.MainWindow.Instance?.AddLog("✅ 업데이트 다운로드 완료");
                });

                bool restartNow = await this.Dispatcher.InvokeAsync(() =>
                {
                    var completedDialog = new UpdateDialogWindow(
                        "업데이트 완료",
                        $"v{updateInfo.TargetFullRelease.Version} 다운로드 완료",
                        "지금 재시작해서 최신 버전을 적용하시겠습니까?",
                        primaryButtonText: "지금 재시작",
                        secondaryButtonText: "나중에");

                    if (Current?.MainWindow != null)
                    {
                        completedDialog.Owner = Current.MainWindow;
                    }

                    return completedDialog.ShowDialog() == true;
                });

                if (restartNow)
                {
                    Debug.WriteLine("[Update] 업데이트 적용 및 재시작...");
                    mgr.ApplyUpdatesAndRestart(updateInfo);
                    Current?.Shutdown();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] 다운로드/적용 오류: {ex.Message}");
                await this.Dispatcher.InvokeAsync(() =>
                {
                    TradingBot.MainWindow.Instance?.AddLog("❌ 업데이트 다운로드 실패");

                    var errorDialog = new UpdateDialogWindow(
                        "업데이트 오류",
                        "다운로드에 실패했습니다",
                        $"업데이트 다운로드 실패:\n{ex.Message}",
                        primaryButtonText: "확인",
                        showSecondaryButton: false);

                    if (Current?.MainWindow != null)
                    {
                        errorDialog.Owner = Current.MainWindow;
                    }

                    errorDialog.ShowDialog();
                });
            }
        }

        private static bool IsInstalledViaVelopack(out string reason)
        {
            reason = string.Empty;

            try
            {
                string baseDir = AppContext.BaseDirectory.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar);

                string currentDirName = new System.IO.DirectoryInfo(baseDir).Name;
                string? parentDir = System.IO.Directory.GetParent(baseDir)?.FullName;
                string updateExeInBase = System.IO.Path.Combine(baseDir, "Update.exe");
                string updateExeInParent = parentDir != null
                    ? System.IO.Path.Combine(parentDir, "Update.exe")
                    : string.Empty;

                bool looksInstalled = string.Equals(currentDirName, "current", StringComparison.OrdinalIgnoreCase)
                    || System.IO.File.Exists(updateExeInBase)
                    || (!string.IsNullOrEmpty(updateExeInParent) && System.IO.File.Exists(updateExeInParent));

                if (looksInstalled)
                    return true;

                reason = $"Velopack 설치 경로가 아닙니다. 현재 경로: {baseDir}";
                return false;
            }
            catch (Exception ex)
            {
                reason = $"설치 경로 확인 실패: {ex.Message}";
                return false;
            }
        }

        private async System.Threading.Tasks.Task<bool> CheckForUpdatesCoreAsync(string phase)
        {
            try
            {
#if DEBUG
                Debug.WriteLine($"[Update] 디버그 모드 - 업데이트 확인 건너뜀 ({phase})");
                await System.Threading.Tasks.Task.CompletedTask;
                return false;
#else
                if (!IsInstalledViaVelopack(out string installReason))
                {
                    Debug.WriteLine($"[Update] 자동 업데이트 비활성: {installReason}");

                    if (!_updateErrorShown)
                    {
                        _updateErrorShown = true;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var infoDialog = new UpdateDialogWindow(
                                "자동 업데이트 안내",
                                "자동 업데이트를 사용할 수 없습니다",
                                "현재 실행 파일은 Setup.exe로 설치된 Velopack 경로가 아닙니다.\n\n" +
                                "GitHub Releases의 TradingBot-win-Setup.exe로 설치한 버전에서만 자동 업데이트가 동작합니다.\n\n" +
                                installReason,
                                primaryButtonText: "확인",
                                showSecondaryButton: false);

                            if (Current?.MainWindow != null)
                            {
                                infoDialog.Owner = Current.MainWindow;
                            }

                            infoDialog.ShowDialog();
                        });
                    }

                    return false;
                }

                var githubSource = new GithubSource(
                    "https://github.com/ISG-kris79/TradingBot",
                    null,  // 인증 토큰 없음 (공개 저장소)
                    false  // 프리릴리스 제외
                );
                var mgr = new UpdateManager(githubSource);

                Debug.WriteLine($"[Update] {phase} 업데이트 확인: GitHub Releases API");
                var updateInfo = await mgr.CheckForUpdatesAsync();

                if (updateInfo == null)
                {
                    Debug.WriteLine("[Update] 최신 버전 사용 중");
                    return false;
                }

                Debug.WriteLine($"[Update] 새 버전 발견: {updateInfo.TargetFullRelease.Version}");

                bool shouldDownload = await Dispatcher.InvokeAsync(() =>
                {
                    var promptDialog = new UpdateDialogWindow(
                        "업데이트 사용 가능",
                        $"새 버전 v{updateInfo.TargetFullRelease.Version}",
                        "지금 업데이트를 진행하시겠습니까?\n업데이트 후 앱이 자동으로 재시작됩니다.",
                        primaryButtonText: "다운로드",
                        secondaryButtonText: "나중에");

                    if (Current?.MainWindow != null)
                    {
                        promptDialog.Owner = Current.MainWindow;
                    }

                    return promptDialog.ShowDialog() == true;
                });

                if (!shouldDownload)
                {
                    return false;
                }

                UpdateDialogWindow? progressWindow = null;

                await Dispatcher.InvokeAsync(() =>
                {
                    progressWindow = new UpdateDialogWindow(
                        "업데이트 다운로드",
                        "최신 버전 준비 중",
                        "업데이트 파일을 다운로드하고 있습니다...",
                        isProgressMode: true);

                    if (Current?.MainWindow != null)
                    {
                        progressWindow.Owner = Current.MainWindow;
                    }

                    progressWindow.Show();
                });

                try
                {
                    await mgr.DownloadUpdatesAsync(updateInfo);
                    Debug.WriteLine("[Update] 업데이트 다운로드 완료");
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        progressWindow?.AllowClose();
                        progressWindow?.Close();

                        var errorDialog = new UpdateDialogWindow(
                            "업데이트 오류",
                            "다운로드에 실패했습니다",
                            $"업데이트 다운로드 실패:\n{ex.Message}\n\n기존 버전으로 계속 진행합니다.",
                            primaryButtonText: "확인",
                            showSecondaryButton: false);

                        if (Current?.MainWindow != null)
                        {
                            errorDialog.Owner = Current.MainWindow;
                        }

                        errorDialog.ShowDialog();
                    });

                    return false;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    progressWindow?.AllowClose();
                    progressWindow?.Close();
                });

                bool restartNow = await Dispatcher.InvokeAsync(() =>
                {
                    var completedDialog = new UpdateDialogWindow(
                        "업데이트 완료",
                        $"v{updateInfo.TargetFullRelease.Version} 다운로드 완료",
                        "지금 재시작해서 최신 버전을 적용하시겠습니까?",
                        primaryButtonText: "지금 재시작",
                        secondaryButtonText: "나중에");

                    if (Current?.MainWindow != null)
                    {
                        completedDialog.Owner = Current.MainWindow;
                    }

                    return completedDialog.ShowDialog() == true;
                });

                if (restartNow)
                {
                    mgr.ApplyUpdatesAndRestart(updateInfo);
                    Current?.Shutdown();
                    return true;
                }

                return false;
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] 오류 발생: {ex.Message}");

                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found"))
                {
                    return false;
                }

                if (!_updateErrorShown)
                {
                    _updateErrorShown = true;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var errorDialog = new UpdateDialogWindow(
                            "업데이트 확인",
                            "업데이트 확인에 실패했습니다",
                            $"{ex.Message}\n\n기존 버전으로 계속 진행합니다.",
                            primaryButtonText: "확인",
                            showSecondaryButton: false);

                        if (Current?.MainWindow != null)
                        {
                            errorDialog.Owner = Current.MainWindow;
                        }

                        errorDialog.ShowDialog();
                    });
                }

                return false;
            }
        }

        /// <summary>
        /// DB에서 GeneralSettings 로드하는 비동기 메서드 (현재는 LoginWindow에서 로드)
        /// </summary>
        private async System.Threading.Tasks.Task LoadGeneralSettingsFromDbAsync()
        {
            try
            {
                if (AppConfig.CurrentUser == null || string.IsNullOrEmpty(AppConfig.ConnectionString))
                {
                    Debug.WriteLine("[App] 로그인 전 또는 DB 연결 없음 - GeneralSettings 로드 스킵");
                    return;
                }

                var dbManager = new DbManager(AppConfig.ConnectionString);
                var dbSettings = await dbManager.LoadGeneralSettingsAsync(AppConfig.CurrentUser.Id);

                if (dbSettings != null && AppConfig.Current != null)
                {
                    AppConfig.Current.Trading.GeneralSettings = dbSettings;
                    Debug.WriteLine($"[App] ✅ [{AppConfig.CurrentUser.Username}] GeneralSettings DB에서 로드 완료");
                }
                else
                {
                    Debug.WriteLine($"[App] ⚠️ [{AppConfig.CurrentUser.Username}] GeneralSettings DB 로드 실패 - 기본값 사용");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] GeneralSettings 로드 중 오류: {ex.Message}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Mutex 해제
            if (_ownsMutex)
            {
                _mutex?.ReleaseMutex();
            }
            _mutex?.Dispose();
            _mutex = null;
            _ownsMutex = false;

            /* TensorFlow 전환 중 비활성화
            // Torch run-state 정리 (정상 종료 마킹)
            TorchInitializer.RegisterCleanShutdown();
            */

            Debug.WriteLine("[App] 애플리케이션 종료 - Mutex 해제됨");
            base.OnExit(e);
        }

        private bool HandleWalkForwardOptimizeCliIfRequested(string[] args)
        {
            if (args == null || !args.Contains("--optimize-backtest", StringComparer.OrdinalIgnoreCase))
                return false;

            try
            {
                decimal balance = 2500m;
                int years = 3;
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i].Equals("--balance", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(args[i + 1], out decimal b)) balance = b;
                    if (args[i].Equals("--years",   StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int y))   years   = y;
                }

                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine("[워크포워드 최적화] 비활성화 — ThreeYearBacktestRunner는 AI 시스템과 함께 제거됨");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
            return true;
        }

        private bool HandleHybridBacktestCliIfRequested(string[] args)
        {
            if (args == null || args.Length == 0 || !args.Contains("--hybrid-backtest", StringComparer.OrdinalIgnoreCase))
                return false;

            try
            {
                int days = 365;
                string[] symbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
                bool perfectAi = !args.Contains("--noise-ai", StringComparer.OrdinalIgnoreCase);
                bool enableGate = !args.Contains("--no-gate", StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--days", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedDays))
                    {
                        days = Math.Max(1, parsedDays);
                    }

                    if (string.Equals(args[i], "--symbols", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        symbols = args[i + 1]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToArray();
                    }
                }

                Console.WriteLine("═══════════ Hybrid 주문 로직 백테스트 시작 ═══════════");
                Console.WriteLine($"Days={days} | Symbols={string.Join(",", symbols)} | PerfectAI={perfectAi} | Gate={enableGate}");

                var backtester = new HybridStrategyBacktester
                {
                    PerfectAI = perfectAi,
                    EnableComponentGate = enableGate
                };

                var results = backtester
                    .RunMultiAsync(symbols, days, null)
                    .GetAwaiter()
                    .GetResult();

                foreach (var result in results.OrderByDescending(r => r.TotalPnL))
                {
                    Console.WriteLine(
                        $"{result.Symbol} | Trades={result.TotalTrades} | WinRate={result.WinRate:F1}% | PnL={result.TotalPnL:F2} USDT | Return={result.TotalPnLPercent:F2}% | PF={result.ProfitFactor:F2} | MDD={result.MaxDrawdownPercent:F2}% | Signals={result.TotalSignals} | GateReject={result.GateRejections}");
                }

                var output = new
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    Days = days,
                    Symbols = symbols,
                    PerfectAI = perfectAi,
                    EnableGate = enableGate,
                    TotalTrades = results.Sum(r => r.TotalTrades),
                    TotalPnL = results.Sum(r => r.TotalPnL),
                    AverageWinRate = results.Count > 0 ? results.Average(r => r.WinRate) : 0,
                    Results = results
                };

                string outputPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hybrid_backtest_result.json");
                string json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(outputPath, json);

                Console.WriteLine($"RESULT_JSON={outputPath}");
                Console.WriteLine("═══════════ Hybrid 주문 로직 백테스트 종료 ═══════════");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"HYBRID_BACKTEST_ERROR: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }

            return true;
        }
    }
}
