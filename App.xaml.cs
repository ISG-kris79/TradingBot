using System.Windows;
using Velopack;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using TradingBot.Services;
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
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // 예외 메시지 팝업 및 로그 기록
            string msg = $"치명적 오류 발생: {e.Exception.Message}\n\n{e.Exception.StackTrace}";

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
            // 중복 실행 방지 (전역 Mutex 사용)
            const string mutexName = @"Global\TradingBot_SingleInstance_8F9A2B3C";

            try
            {
                _mutex = new Mutex(true, mutexName, out bool createdNew);
                _ownsMutex = createdNew;

                if (!createdNew)
                {
                    Debug.WriteLine("[App] 중복 실행 방지: 이미 실행 중인 인스턴스가 있습니다.");
                    MessageBox.Show("TradingBot이 이미 실행 중입니다.\n실행 중인 창을 확인해 주세요.", "중복 실행 감지", MessageBoxButton.OK, MessageBoxImage.Information);
                    this.Shutdown();
                    return;
                }

                Debug.WriteLine("[App] 단일 인스턴스 확인 완료");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Mutex 생성 오류: {ex.Message}");
                // Mutex 생성 실패 시에도 계속 진행 (보안 문제보다는 사용성 우선)
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

            // [추가] TorchSharp 초기화 시도 (PPO, Transformer 기능)
            bool torchAvailable = Services.TorchInitializer.TryInitialize();
            if (!torchAvailable)
            {
                Debug.WriteLine("[App] TorchSharp 초기화 실패 - RL 기능 비활성화");
                Debug.WriteLine($"[App] Error: {Services.TorchInitializer.ErrorMessage}");

                // 사용자에게 알림 (방해하지 않도록 Debug로만 출력)
                // 필요한 경우에만 MessageBox 표시
                if (Services.TorchInitializer.ErrorMessage != null &&
                    Services.TorchInitializer.ErrorMessage.Contains("Visual C++"))
                {
                    MessageBox.Show(
                        Services.TorchInitializer.ErrorMessage,
                        "TorchSharp 초기화 실패",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            else
            {
                Debug.WriteLine("[App] TorchSharp 초기화 성공");
            }

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

            // 로그인 창 이후 업데이트 확인 (강제 종료 없이 안전 실행)
            _ = loginWindow.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await CheckForUpdatesAfterLoginAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] 로그인 후 업데이트 확인 중 오류: {ex.Message}");
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
        /// 로그인 창 표시 전 업데이트 확인 및 자동 적용
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

        private async System.Threading.Tasks.Task<bool> CheckForUpdatesCoreAsync(string phase)
        {
            try
            {
#if DEBUG
                Debug.WriteLine($"[Update] 디버그 모드 - 업데이트 확인 건너뜀 ({phase})");
                await System.Threading.Tasks.Task.CompletedTask;
                return false;
#else
                var updateUrl = "https://github.com/ISG-kris79/TradingBot/releases/latest/download";
                var mgr = new UpdateManager(updateUrl);

                Debug.WriteLine($"[Update] {phase} 업데이트 확인: {updateUrl}");
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

            Debug.WriteLine("[App] 애플리케이션 종료 - Mutex 해제됨");
            base.OnExit(e);
        }
    }
}
