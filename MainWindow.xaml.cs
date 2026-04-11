using System.ComponentModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.IO;
using System.Runtime.InteropServices; // 추가 필요
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.ViewModels;
using TradingBot.Shared.Models;
using TradeLog = TradingBot.Shared.Models.TradeLog;

namespace TradingBot
{
    public partial class MainWindow : Window
    {
        // --- Windows API 호출을 위한 구조체 및 메서드 정의 ---
        // x86/x64 아키텍처별 기본 레이아웃을 사용해야 FLASHWINFO 크기가 올바르게 매핑됩니다.
        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }
        public const uint FLASHW_ALL = 3;         // 캡션과 작업표시줄 모두 깜빡임
        public const uint FLASHW_TIMERNOFG = 12;  // 창이 활성화될 때까지 계속 깜빡임
        private const uint FLASHW_STOP = 0;       // 깜빡임 중지

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        // 아이콘 깜빡임 실행 메서드
        public void FlashWindow()
        {
            if (!CheckAccess()) 
            { 
                try 
                { 
                    _ = Dispatcher.BeginInvoke(new Action(FlashWindow), DispatcherPriority.Background);
                } 
                catch 
                { 
                    // Dispatcher shutdown 중 호출 시 무시
                } 
                return; 
            }

            // 현재 창의 핸들 가져오기
            WindowInteropHelper helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return;

            try
            {
                // 아키텍처별 구조체 크기 검증 (x86:20, x64:32)
                int structSize = Marshal.SizeOf(typeof(FLASHWINFO));
                int expectedSize = IntPtr.Size == 8 ? 32 : 20;
                if (structSize != expectedSize)
                {
                    System.Diagnostics.Debug.WriteLine($"[FlashWindow] 경고: 구조체 크기 불일치 ({structSize} != {expectedSize})");
                    return;
                }

                FLASHWINFO fi = new FLASHWINFO
                {
                    cbSize = (uint)structSize,
                    hwnd = helper.Handle,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = 3, // [FIX] 3회로 축소 (안전성 향상)
                    dwTimeout = 0
                };

                bool success = FlashWindowEx(ref fi);
                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine($"[FlashWindow] Win32 오류: {error}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FlashWindow] 오류: {ex.Message}");
                // P/Invoke 실패 시 조용히 무시 (UI 알림은 필수 기능 아님)
            }
        }
        public static MainWindow? Instance { get; private set; }

        // [MVVM] ViewModel 추가
        public MainViewModel ViewModel { get; set; }

        // 트레이 아이콘
        private TaskbarIcon? _trayIcon;
        private SymbolChartWindow? _symbolChartWindow;
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        // [LEGACY] 배치 UI 업데이트 타이머 (신호/티커 처리는 ViewModel에서 담당, 이 타이머는 NO-OP)
        private readonly DispatcherTimer _uiBatchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

        // [Phase 14] 고급 기능 서비스
        private ArbitrageExecutionService? _arbitrageService;
        private FundTransferService? _fundTransferService;
        private PortfolioRebalancingService? _rebalancingService;
        private CancellationTokenSource? _advancedFeaturesCts;

        // [WaveAI] TensorFlow 전환 중 임시 비활성화
        // private WaveAIManager? _waveAIManager;

        // GeneralSettings 캐시 (앱 전체에서 사용)
        public static TradingSettings? CurrentGeneralSettings { get; private set; }

        public static void ApplyGeneralSettings(TradingSettings settings)
        {
            var nextSettings = settings ?? new TradingSettings();

            if (CurrentGeneralSettings == null)
            {
                CurrentGeneralSettings = new TradingSettings();
            }

            CopyTradingSettings(CurrentGeneralSettings, nextSettings);
            CurrentGeneralSettings.MajorTrendProfile = string.Equals(CurrentGeneralSettings.MajorTrendProfile, "Aggressive", StringComparison.OrdinalIgnoreCase)
                ? "Aggressive"
                : "Balanced";

            if (AppConfig.Current?.Trading != null)
            {
                AppConfig.Current.Trading.GeneralSettings = CurrentGeneralSettings;
            }

            Instance?.ViewModel?.UpdateMajorProfileStatus(CurrentGeneralSettings.MajorTrendProfile);

            Instance?.AddLog($"[GeneralSettings] ✅ 런타임 적용 완료 | [MAJOR] Leverage:{CurrentGeneralSettings.MajorLeverage}x MarginPct:{CurrentGeneralSettings.MajorMarginPercent:F1}% SL:{CurrentGeneralSettings.MajorStopLossRoe:F0}% BE:{CurrentGeneralSettings.MajorBreakEvenRoe:F1}% Tp1:{CurrentGeneralSettings.MajorTp1Roe:F0}% Tp2:{CurrentGeneralSettings.MajorTp2Roe:F0}% Trail:{CurrentGeneralSettings.MajorTrailingStartRoe:F0}%/{CurrentGeneralSettings.MajorTrailingGapRoe:F1}% | [PUMP] Margin:{CurrentGeneralSettings.PumpMargin:F0} SL:{CurrentGeneralSettings.PumpStopLossRoe:F0}% BE:{CurrentGeneralSettings.PumpBreakEvenRoe:F0}% Tp1:{CurrentGeneralSettings.PumpTp1Roe:F0}% Tp2:{CurrentGeneralSettings.PumpTp2Roe:F0}% Trail:{CurrentGeneralSettings.PumpTrailingStartRoe:F0}%/{CurrentGeneralSettings.PumpTrailingGapRoe:F0}% 1차익절비중:{CurrentGeneralSettings.PumpFirstTakeProfitRatioPct:F1}% 계단:{CurrentGeneralSettings.PumpStairStep1Roe:F0}/{CurrentGeneralSettings.PumpStairStep2Roe:F0}/{CurrentGeneralSettings.PumpStairStep3Roe:F0}%");
        }

        private static void CopyTradingSettings(TradingSettings target, TradingSettings source)
        {
            target.DefaultLeverage = source.DefaultLeverage;
            target.DefaultMargin = source.DefaultMargin;
            target.SidewaysTakeProfitRoe = source.SidewaysTakeProfitRoe;
            target.TargetRoe = source.TargetRoe;
            target.StopLossRoe = source.StopLossRoe;
            target.TrailingStartRoe = source.TrailingStartRoe;
            target.TrailingDropRoe = source.TrailingDropRoe;
            target.MajorTrendProfile = source.MajorTrendProfile;
            // PUMP 전용 설정
            target.PumpLeverage = source.PumpLeverage;
            target.PumpTp1Roe = source.PumpTp1Roe;
            target.PumpTp2Roe = source.PumpTp2Roe;
            target.PumpTimeStopMinutes = source.PumpTimeStopMinutes;
            target.PumpStopDistanceWarnPct = source.PumpStopDistanceWarnPct;
            target.PumpStopDistanceBlockPct = source.PumpStopDistanceBlockPct;
            target.PumpBreakEvenRoe = source.PumpBreakEvenRoe;
            target.PumpTrailingStartRoe = source.PumpTrailingStartRoe;
            target.PumpTrailingGapRoe = source.PumpTrailingGapRoe;
            target.PumpStopLossRoe = source.PumpStopLossRoe;
            target.PumpMargin = source.PumpMargin;
            target.PumpFirstTakeProfitRatioPct = source.PumpFirstTakeProfitRatioPct;
            target.PumpStairStep1Roe = source.PumpStairStep1Roe;
            target.PumpStairStep2Roe = source.PumpStairStep2Roe;
            target.PumpStairStep3Roe = source.PumpStairStep3Roe;
            // [메이저/PUMP 완전 분리] 메이저 코인 전용 설정
            target.MajorLeverage = source.MajorLeverage;
            target.MajorMargin = source.MajorMargin;
            target.MajorMarginPercent = source.MajorMarginPercent;
            target.MajorBreakEvenRoe = source.MajorBreakEvenRoe;
            target.MajorTp1Roe = source.MajorTp1Roe;
            target.MajorTp2Roe = source.MajorTp2Roe;
            target.MajorTrailingStartRoe = source.MajorTrailingStartRoe;
            target.MajorTrailingGapRoe = source.MajorTrailingGapRoe;
            target.MajorStopLossRoe = source.MajorStopLossRoe;
        }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            // 버전 정보를 제목 표시줄에 표시
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string versionText = $"v{version?.Major}.{version?.Minor}.{version?.Build}";
            this.Title = $"COINFF TRADINGBOT {versionText}";
            txtHeaderVersion.Text = $" {versionText}";

            // ViewModel 먼저 초기화 (AddLog에서 사용되므로)
            ViewModel = new MainViewModel();
            this.DataContext = ViewModel;

            // [v4.9.0] AI Command Center 탭 제거로 관련 구독 제거

            // 트레이 아이콘 초기화 (ViewModel 이후에 호출)
            InitializeTrayIcon();

            dgMultiTimeframe.ItemsSource = ViewModel.MarketDataList;
            lstAlerts.ItemsSource = ViewModel.Alerts;

            // 텍스트 및 색상 바인딩
            txtTotalEquity.SetBinding(TextBlock.TextProperty, new Binding("TotalEquity"));
            txtTotalEquity.SetBinding(TextBlock.ForegroundProperty, new Binding("EquityColor"));
            txtAvailableBalance.SetBinding(TextBlock.TextProperty, new Binding("AvailableBalance"));
            txtTelegramStatus.SetBinding(TextBlock.TextProperty, new Binding("TelegramStatus"));
            txtTelegramStatus.SetBinding(TextBlock.ForegroundProperty, new Binding("TelegramStatusColor"));
            txtMajorProfileStatus.SetBinding(TextBlock.TextProperty, new Binding("MajorProfileStatusText"));
            txtMajorProfileStatus.SetBinding(TextBlock.ForegroundProperty, new Binding("MajorProfileStatusColor"));
            txtTotalPositionInfo.SetBinding(TextBlock.TextProperty, new Binding("TotalPositionInfo"));
            lblFooter.SetBinding(TextBlock.TextProperty, new Binding("FooterText"));
            pgScanning.SetBinding(ProgressBar.ValueProperty, new Binding("ScanProgress"));
            pgScanning.SetBinding(ProgressBar.ForegroundProperty, new Binding("ScanProgressColor"));

            // 버튼 활성/비활성 상태 바인딩
            btnStart.SetBinding(Button.IsEnabledProperty, new Binding("IsStartEnabled"));
            btnStop.SetBinding(Button.IsEnabledProperty, new Binding("IsStopEnabled"));

            LoadWindowSettings();

            _clockTimer.Tick += (_, __) => UpdateDateTimeDisplay();
            _clockTimer.Start();
            UpdateDateTimeDisplay();

            // [병목 해결] 배치 UI 업데이트 타이머 초기화
            _uiBatchTimer.Tick += (_, __) => ProcessUIBatchQueue();
            _uiBatchTimer.Start();

            // 관리자 메뉴 표시 여부 설정
            CheckAdminMenu();

            // 트레이 아이콘 이벤트 연결
            this.StateChanged += MainWindow_StateChanged;
            this.Closing += MainWindow_Closing;

            // [Phase 14] Loaded 이벤트에서 고급 기능 서비스 초기화
            // [병목 해결] 독립 작업을 병렬 실행 (순차→병렬) — 초기 로딩 지연 감소
            this.Loaded += async (s, e) =>
            {
                var updateTask = App.EnsureUpdateCheckAsync();
                var settingsTask = InitializeGeneralSettingsAsync();
                await Task.WhenAll(updateTask, settingsTask);
                await InitializeAdvancedFeaturesAsync();

                AddLog("[WaveAI] ℹ️ 앱 시작 시 MainWindow Torch 초기화는 비활성화되었습니다. (외부 AI 서비스 경로 사용)");
            };
        }

        /// <summary>
        /// 현재 사용자가 관리자인지 확인하고 관리자 메뉴 표시
        /// </summary>
        private async void CheckAdminMenu()
        {
            var currentUser = AppConfig.CurrentUser;
            if (currentUser != null && currentUser.IsAdmin)
            {
                mnuAdmin.Visibility = Visibility.Visible;
                await UpdatePendingApprovalCount();
            }
            else
            {
                mnuAdmin.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 승인 대기 중인 사용자 수 업데이트
        /// </summary>
        private async Task UpdatePendingApprovalCount()
        {
            try
            {
                var dbService = new DatabaseService();
                int pendingCount = await dbService.GetPendingApprovalCountAsync();
                mnuPendingCount.Header = $"⏳ 승인 대기: {pendingCount}명";

                if (pendingCount > 0)
                {
                    mnuPendingCount.Foreground = new SolidColorBrush(Colors.Orange);
                }
                else
                {
                    mnuPendingCount.Foreground = new SolidColorBrush(Colors.LightGray);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] 승인 대기 수 조회 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 사용자 관리 창 열기 (관리자 전용)
        /// </summary>
        private void mnuUserManagement_Click(object sender, RoutedEventArgs e)
        {
            var currentUser = AppConfig.CurrentUser;
            if (currentUser != null && currentUser.IsAdmin)
            {
                var userManagementWindow = new UserManagementWindow(currentUser.Username);
                userManagementWindow.Owner = this;
                userManagementWindow.ShowDialog();

                // 창을 닫은 후 승인 대기 수 업데이트
                _ = UpdatePendingApprovalCount();
            }
            else
            {
                MessageBox.Show("관리자 권한이 필요합니다.", "권한 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.StartCommand.Execute(null);
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.StopCommand.Execute(null);
        }

        private void btnBacktest_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🧪 btnBacktest_Click 진입");
            ViewModel.RunBacktestCommand.Execute(null);
        }

        private void btnOptimize_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🧪 btnOptimize_Click 진입");
            ViewModel.OptimizeCommand.Execute(null);
        }

        private void btnRecollect_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RecollectDataCommand.Execute(null);
        }

        private void UpdateDateTimeDisplay()
        {
            var now = DateTime.Now;
            txtDateDisplay.Text = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            txtTimeDisplay.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void btnLogout_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("로그아웃 하시겠습니까?", "Logout", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                // 1. 자동 로그인 파일 삭제
                if (File.Exists("login.config")) File.Delete("login.config");

                // 2. 메모리 상의 자격 증명 초기화
                AppConfig.ClearCredentials();

                // 3. 로그인 창으로 이동
                new LoginWindow().Show();
                this.Close();
            }
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.Owner = this;
            settingsWindow.ShowDialog();
        }

        private void btnProfile_Click(object sender, RoutedEventArgs e)
        {
            var profileWindow = new ProfileWindow();
            profileWindow.Owner = this;
            profileWindow.ShowDialog();
        }

        private void btnSignUp_Click(object sender, RoutedEventArgs e)
        {
            var signUpWindow = new SignUpWindow();
            signUpWindow.Owner = this;
            signUpWindow.ShowDialog();
        }

        private void btnPopOutChart_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                MessageBox.Show("ViewModel이 초기화되지 않았습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var chartWindow = new ActivePnLWindow(ViewModel);
                chartWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"차트 창을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnStatistics_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.TradeHistory == null)
            {
                MessageBox.Show("거래 내역이 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var statsWindow = new TradeStatisticsWindow(ViewModel.TradeHistory);
                statsWindow.Owner = this;
                statsWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"통계 창을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 윈도우 컨트롤 버튼 이벤트 핸들러
        private void OnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnMaximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void OnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveWindowSettings();
            base.OnClosing(e);
        }



        // 엔진에서 호출할 수익률 업데이트 메서드
        public void UpdateProfitChart(double currentEquity)
        {
            void UpdateChart()
            {
                // NaN/Infinity 검증 추가 (LiveCharts Y1 축 오류 방지)
                if (!double.IsNaN(currentEquity) && !double.IsInfinity(currentEquity) && currentEquity >= 0)
                {
                    ViewModel.ProfitHistory.Add(currentEquity);
                    // 데이터가 너무 많아지면 앞부분 삭제 (최근 50개 유지)
                    if (ViewModel.ProfitHistory.Count > 50) ViewModel.ProfitHistory.RemoveAt(0);
                }
                // else: 무효한 값은 무시 (이전 차트 유지)
            }

            if (Dispatcher.CheckAccess())
            {
                UpdateChart();
            }
            else
            {
                _ = Dispatcher.BeginInvoke(new Action(UpdateChart), DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// [큐 분리] WebSocket 티커 신호를 ViewModel 큐에 전달 (UI 스레드 직접 접근 X)
        /// </summary>
        public void RefreshSignalUI(MultiTimeframeViewModel signal)
        {
            if (signal == null || ViewModel == null)
                return;

            // 가격 전용 티커 업데이트는 ViewModel의 ConcurrentDictionary 큐로 직접 라우팅
            ViewModel.EnqueueTickerUpdate(signal.Symbol ?? "", signal.LastPrice, null);
        }

        /// <summary>
        /// [DISABLED] ProcessUIBatchQueue는 ViewModel의 FlushPendingSignalUpdatesToUi/FlushPendingTickerUpdatesToUi로 대체됨.
        /// 타이머 유지하되 처리 내용 없음 (NO-OP).
        /// </summary>
        private void ProcessUIBatchQueue()
        {
            // ViewModel의 큐에서 일괄 처리하므로, 이 경로는 더 이상 사용하지 않음.
            // 애니메이션도 DataGrid 멈춤의 원인이었으므로 제거.
        }

        public void RefreshTradeGrid()
        {
            void Refresh()
            {
                var view = CollectionViewSource.GetDefaultView(dgMultiTimeframe.ItemsSource);
                if (view != null)
                {
                    view.SortDescriptions.Clear();
                    // 1순위: 포지션 활성화 여부 (내림차순 - True가 위로)
                    view.SortDescriptions.Add(new SortDescription("IsPositionActive", ListSortDirection.Descending));
                    // 2순위: AI 스코어 높은 순
                    view.SortDescriptions.Add(new SortDescription("AIScore", ListSortDirection.Descending));

                    view.Refresh();
                }
            }

            if (Dispatcher.CheckAccess())
            {
                Refresh();
            }
            else
            {
                _ = Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
            }
        }

        // 1. 단순 활동 기록 (스캔 중..., 대기 중...)
        // [큐 분리] ViewModel의 버퍼 큐로 라우팅 (Dispatcher.BeginInvoke 제거)
        public void AddLiveLog(string msg)
        {
            ViewModel?.EnqueueLiveLog(msg);
        }

        // 2. 중요 매매 알림 (매수 완료!, 손절!, 급등 포착!)
        // [큐 분리] ViewModel의 Alert 큐로 라우팅 (Dispatcher.BeginInvoke 제거)
        public void AddAlert(string msg)
        {
            ViewModel?.EnqueueAlert(msg);
        }

        // 하단 상태바 메시지 업데이트
        // [큐 분리] ViewModel의 Footer 큐로 라우팅 (Dispatcher.BeginInvoke 제거)
        public void AddLog(string msg)
        {
            ViewModel?.EnqueueFooterLog(msg);
        }

        // [병목 해결] 스캔 진행색 정적 캐시 (ColorConverter.ConvertFromString 반복 호출 방지)
        private static readonly SolidColorBrush s_scanCompleteColor = FreezeB(new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)));
        private static readonly SolidColorBrush s_scanProgressColor = FreezeB(new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76)));
        private static SolidColorBrush FreezeB(SolidColorBrush b) { b.Freeze(); return b; }

        // 스캔 진행률 업데이트 (ProgressBar 및 텍스트)
        public void UpdateProgress(int current, int total)
        {
            if (!CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => UpdateProgress(current, total)), DispatcherPriority.Background);
                return;
            }

            if (total > 0)
            {
                double percentage = (double)current / total * 100;
                ViewModel.ScanProgress = percentage;

                if (current >= total)
                {
                    // 스캔 완료 시 파란색으로 변경 (대기 상태)
                    ViewModel.ScanProgressColor = s_scanCompleteColor;
                    ViewModel.FooterText = $"모든 종목 스캔 완료. 다음 주기 대기 중...";
                    AddLog("전체 스캔 완료. 다음 주기 대기 중...");
                }
                else
                {
                    // 스캔 중에는 다시 초록색으로 유지
                    ViewModel.ScanProgressColor = s_scanProgressColor;
                    ViewModel.FooterText = $"스캔 진행 중: {current}/{total} ({percentage:F0}%)";
                    AddLog($"스캔 진행 중: {current}/{total} ({percentage:F0}%)");
                }
            }
        }

        private async void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid grid && grid.SelectedItem is TradeLog log)
            {
                await ViewModel.ShowTradeChartAsync(log);
            }
        }

        private void PerfDaily_Checked(object sender, RoutedEventArgs e) { if (ViewModel != null) ViewModel.PerformancePeriod = "일별"; }
        private void PerfWeekly_Checked(object sender, RoutedEventArgs e) { if (ViewModel != null) ViewModel.PerformancePeriod = "주별"; }
        private void PerfMonthly_Checked(object sender, RoutedEventArgs e) { if (ViewModel != null) ViewModel.PerformancePeriod = "월별"; }

        private async void dgMultiTimeframe_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (ViewModel == null) return;
                if (dgMultiTimeframe.SelectedItem is not MultiTimeframeViewModel selectedItem) return;

                ViewModel.SelectedSymbol = selectedItem;
                await Task.Delay(500);

                if (_symbolChartWindow == null || !_symbolChartWindow.IsVisible)
                {
                    _symbolChartWindow = new SymbolChartWindow(ViewModel);
                    _symbolChartWindow.Closed += (_, _) => _symbolChartWindow = null;
                    _symbolChartWindow.Show();
                    return;
                }

                if (_symbolChartWindow.WindowState == WindowState.Minimized)
                {
                    _symbolChartWindow.WindowState = WindowState.Normal;
                }

                ViewModel.RefreshLiveChart();
                _symbolChartWindow.Activate();
                _symbolChartWindow.Focus();
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 차트 창 오류: {ex.Message}");
                MessageBox.Show($"차트 창을 열 수 없습니다.\n\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                // 오류 발생 시 기존 창 초기화
                _symbolChartWindow = null;
            }
        }



        public void UpdateProfitDashboardUI(double equity, double available, int totalPosCount)
        {
            if (!CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => UpdateProfitDashboardUI(equity, available, totalPosCount)), DispatcherPriority.Background);
                return;
            }

            // 1. 기본 값 표시
            ViewModel.TotalEquity = $"${equity:N2}";
            ViewModel.AvailableBalance = $"${available:N2}";

            // 2. 총 수익률(ROI %) 계산
            double roi = 0;
            decimal initialBalance = ViewModel.InitialBalance;
            if (initialBalance > 0)
            {
                roi = (equity - (double)initialBalance) / (double)initialBalance * 100;
            }
            ViewModel.AverageRoe = roi;

            // 3. 색상 분기 로직 적용
            if (roi > 0)
            {
                ViewModel.EquityColor = Brushes.LimeGreen;
            }
            else if (roi < 0)
            {
                ViewModel.EquityColor = Brushes.Tomato;
            }
            else
            {
                ViewModel.EquityColor = Brushes.White;
            }
        }

        public void UpdateTelegramStatus(bool isConnected, string text)
        {
            if (!CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => UpdateTelegramStatus(isConnected, text)), DispatcherPriority.Background);
                return;
            }

            ViewModel.TelegramStatus = text;
            ViewModel.TelegramStatusColor = isConnected ? Brushes.DeepSkyBlue : Brushes.Gray;
        }


        public void UpdateSlotStatusUI(int majorCount, int majorMax, int pumpCount, int pumpMax)
        {
            if (!CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => UpdateSlotStatusUI(majorCount, majorMax, pumpCount, pumpMax)), DispatcherPriority.Background);
                return;
            }

            // 메이저 슬롯 UI 업데이트 (예: "메이저: 1 / 2")
            ViewModel.MajorSlotText = $"{majorCount} / {majorMax}";
            ViewModel.MajorSlotColor = majorCount >= majorMax ? Brushes.Orange : Brushes.White;

            // 급등주 슬롯 UI 업데이트 (예: "급등주: 0 / 2")
            ViewModel.PumpSlotText = $"{pumpCount} / {pumpMax}";
            ViewModel.PumpSlotColor = pumpCount >= pumpMax ? Brushes.Orange : Brushes.White;

            // 전체 포지션 요약 정보(메인 UI 통합 표시)
            ViewModel.TotalPositionInfo = $"Active: {majorCount + pumpCount} / {majorMax + pumpMax}";
        }

        public void UpdateAiLearningStatusUI(int totalDecisions, int labeledCount, int markToMarketCount, int tradeCloseCount, int activeDecisions, bool modelsLoaded)
        {
            if (!CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => UpdateAiLearningStatusUI(totalDecisions, labeledCount, markToMarketCount, tradeCloseCount, activeDecisions, modelsLoaded)), DispatcherPriority.Background);
                return;
            }

            ViewModel.AiTotalDecisions = totalDecisions;
            ViewModel.AiLabeledCount = labeledCount;
            ViewModel.AiMarkToMarketCount = markToMarketCount;
            ViewModel.AiTradeCloseCount = tradeCloseCount;
            ViewModel.AiActiveDecisions = activeDecisions;
            ViewModel.AiModelsLoaded = modelsLoaded;
        }

        public void OnPositionEntered()
        {
            if (!CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(OnPositionEntered), DispatcherPriority.Background);
                return;
            }

            var view = CollectionViewSource.GetDefaultView(dgMultiTimeframe.ItemsSource);
            if (view != null)
            {
                view.SortDescriptions.Clear();

                // 1순위: 포지션 있는 종목 상단 (IsPositionActive: True -> False 순)
                view.SortDescriptions.Add(new SortDescription("IsPositionActive", ListSortDirection.Descending));

                // 2순위: 수익률 높은 순 (포지션끼리 비교)
                view.SortDescriptions.Add(new SortDescription("ProfitRate", ListSortDirection.Descending));

                // 3순위: AI 점수 높은 순 (나머지 대기 종목들)
                view.SortDescriptions.Add(new SortDescription("AIScore", ListSortDirection.Descending));

                view.Refresh(); // 정렬 즉시 적용
            }
        }
        public void ApplyCustomSort()
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(dgMultiTimeframe.ItemsSource);
            if (view != null)
            {
                view.SortDescriptions.Clear();
                // 1순위: 포지션 잡힌 종목 우선 (내림차순)
                view.SortDescriptions.Add(new SortDescription("IsPositionActive", ListSortDirection.Descending));
                // 2순위: 수익률 높은 순
                view.SortDescriptions.Add(new SortDescription("ProfitRate", ListSortDirection.Descending));
                view.Refresh();
            }
        }


        private class WindowSettings
        {
            public double Top { get; set; }
            public double Left { get; set; }
            public double Height { get; set; }
            public double Width { get; set; }
            public WindowState WindowState { get; set; }
        }

        private void LoadWindowSettings()
        {
            string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "window.settings.json");

            if (File.Exists(settingsFile))
            {
                try
                {
                    var settingsJson = File.ReadAllText(settingsFile);
                    var options = new JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    };
                    var settings = JsonSerializer.Deserialize<WindowSettings>(settingsJson, options);

                    // 화면 밖으로 나가는 것 방지
                    this.Left = Math.Max(0, Math.Min(settings?.Left ?? 0, SystemParameters.VirtualScreenWidth - (settings?.Width ?? 800)));
                    this.Top = Math.Max(0, Math.Min(settings?.Top ?? 0, SystemParameters.VirtualScreenHeight - (settings?.Height ?? 600)));
                    this.Height = settings?.Height ?? 600;
                    this.Width = settings?.Width ?? 800;

                    // 최소화 상태로 시작하는 것 방지
                    this.WindowState = settings?.WindowState == WindowState.Minimized ? WindowState.Normal : settings?.WindowState ?? WindowState.Normal;
                }
                catch (Exception ex)
                {
                    AddLog($"⚠️ 창 위치 복원 실패: {ex.Message}");
                }
            }
        }

        private void SaveWindowSettings()
        {
            var settings = new WindowSettings { WindowState = this.WindowState };

            if (this.WindowState == WindowState.Normal)
            {
                settings.Top = this.Top;
                settings.Left = this.Left;
                settings.Height = this.Height;
                settings.Width = this.Width;
            }
            else // Maximized or Minimized
            {
                settings.Top = this.RestoreBounds.Top;
                settings.Left = this.RestoreBounds.Left;
                settings.Height = this.RestoreBounds.Height;
                settings.Width = this.RestoreBounds.Width;
            }

            string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot");
            Directory.CreateDirectory(settingsDir);
            string settingsFile = Path.Combine(settingsDir, "window.settings.json");
            var options = new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings, options));
        }

        #region 트레이 아이콘 관련

        /// <summary>
        /// 트레이 아이콘 초기화
        /// </summary>
        private void InitializeTrayIcon()
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "TradingBot - 실행 중"
            };

            // 아이콘 로드 시도 (실패 시 기본 아이콘 사용)
            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trading_bot.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 트레이 아이콘 로드 실패 (기본 아이콘 사용): {ex.Message}");
            }

            // 컨텍스트 메뉴 생성
            var contextMenu = new ContextMenu();

            var openMenuItem = new MenuItem { Header = "열기", FontWeight = FontWeights.Bold };
            openMenuItem.Click += (s, e) => ShowMainWindow();
            contextMenu.Items.Add(openMenuItem);

            contextMenu.Items.Add(new Separator());

            var exitMenuItem = new MenuItem { Header = "종료" };
            exitMenuItem.Click += (s, e) => this.Close();
            contextMenu.Items.Add(exitMenuItem);

            _trayIcon.ContextMenu = contextMenu;

            // 더블클릭 이벤트
            _trayIcon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();
        }

        /// <summary>
        /// 창 상태 변경 시 (최소화 시 트레이로 숨김)
        /// </summary>
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                _trayIcon?.ShowBalloonTip("TradingBot", "트레이로 최소화되었습니다.\n더블클릭으로 다시 열 수 있습니다.", BalloonIcon.Info);
            }
        }

        /// <summary>
        /// 창 닫기 시 (프로그램 종료)
        /// </summary>
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // X 버튼 클릭 시 프로그램 종료
            _clockTimer.Stop();
            SaveWindowSettings();
            _symbolChartWindow?.Close();
            _trayIcon?.Dispose();

            // ShutdownMode가 OnExplicitShutdown이므로 명시적으로 종료
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 메인 창 표시 (트레이에서 복원)
        /// </summary>
        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Focus();
        }

        // ============================================================================
        // [Phase 14] 고급 기능 서비스 초기화 및 관리
        // ============================================================================

        /// <summary>
        /// 고급 기능 서비스 초기화
        /// </summary>
        /// <summary>
        /// GeneralSettings 초기화 (앱 시작 시 한 번 로드)
        /// </summary>
        private async Task InitializeGeneralSettingsAsync()
        {
            try
            {
                AddLog("[GeneralSettings] ⚙️ 기본 설정 로드 중...");

                // 1. appsettings.json에서 기본값 로드
                if (AppConfig.Current?.Trading?.GeneralSettings != null)
                {
                    CurrentGeneralSettings = AppConfig.Current.Trading.GeneralSettings;
                    AddLog($"[GeneralSettings] ✅ appsettings.json 로드 완료 (Leverage: {CurrentGeneralSettings.DefaultLeverage}x, Margin: {CurrentGeneralSettings.DefaultMargin})");
                }

                // 2. DB에서 사용자별 설정 로드 (있으면 덮어쓰기)
                if (!string.IsNullOrEmpty(AppConfig.ConnectionString) && AppConfig.CurrentUser != null)
                {
                    var dbManager = new DbManager(AppConfig.ConnectionString);
                    var dbSettings = await dbManager.LoadGeneralSettingsAsync(AppConfig.CurrentUser.Id);

                    if (dbSettings != null)
                    {
                        if (string.IsNullOrWhiteSpace(dbSettings.MajorTrendProfile) &&
                            !string.IsNullOrWhiteSpace(CurrentGeneralSettings?.MajorTrendProfile))
                        {
                            dbSettings.MajorTrendProfile = CurrentGeneralSettings.MajorTrendProfile;
                        }

                        CurrentGeneralSettings = dbSettings;
                        AddLog($"[GeneralSettings] ✅ DB 사용자 설정 로드 완료 (Leverage: {CurrentGeneralSettings.DefaultLeverage}x, Margin: {CurrentGeneralSettings.DefaultMargin})");
                    }
                }

                if (CurrentGeneralSettings == null)
                {
                    // Fallback: 기본값 생성
                    CurrentGeneralSettings = new TradingSettings();
                    AddLog("[GeneralSettings] ⚠️ 기본값 사용");
                }

                CurrentGeneralSettings.MajorTrendProfile = string.Equals(CurrentGeneralSettings.MajorTrendProfile, "Aggressive", StringComparison.OrdinalIgnoreCase)
                    ? "Aggressive"
                    : "Balanced";

                ApplyGeneralSettings(CurrentGeneralSettings);

                AddLog("[GeneralSettings] ✅ 기본 설정 로드 완료");
            }
            catch (Exception ex)
            {
                AddAlert($"❌ 기본 설정 로드 실패: {ex.Message}");
                CurrentGeneralSettings = new TradingSettings(); // Fallback
            }
        }

        /// <summary>
        /// [Phase 14] 고급 기능 서비스 초기화 (차익거래, 자금 이동, 리밸런싱)
        /// </summary>
        private Task InitializeAdvancedFeaturesAsync()
        {
            try
            {
                AddLog("[Advanced Features] 🚀 고급 기능 서비스 초기화 중...");

                // 설정 객체 준비
                var fundTransferSettings = AppConfig.Current?.Trading?.FundTransferSettings
                    ?? new FundTransferSettings();
                var rebalancingSettings = AppConfig.Current?.Trading?.PortfolioRebalancingSettings
                    ?? new PortfolioRebalancingSettings();

                // [Phase 14] DbManager 생성
                DbManager? dbManager = null;
                if (!string.IsNullOrEmpty(AppConfig.ConnectionString))
                {
                    dbManager = new DbManager(AppConfig.ConnectionString);
                    AddLog("[Advanced Features] ✅ DB 로깅 활성화");
                }

                // [Phase 14] TelegramService (이미 초기화되어 있음)
                var telegramService = TelegramService.Instance;
                AddLog("[Advanced Features] ✅ Telegram 알림 활성화");

                // 취소 토큰 생성
                _advancedFeaturesCts = new CancellationTokenSource();

                // 1. 차익거래 서비스 초기화
                _arbitrageService = new ArbitrageExecutionService(
                    AppConfig.Current?.Trading?.ArbitrageSettings
                    ?? new ArbitrageSettings(),
                    dbManager,
                    telegramService
                );
                _arbitrageService.OnLog += (msg) => AddLog($"[Arbitrage] {msg}");
                _arbitrageService.OnOpportunityDetected += (opp) =>
                {
                    AddAlert($"💰 차익 기회 발견! {opp.BuyExchange} → {opp.SellExchange} (수익률: {opp.ProfitPercent:F2}%)");
                };

                AddLog("[Advanced Features] ✅ 차익거래 서비스 준비 완료");

                // 2. 자금 이동 서비스 초기화
                _fundTransferService = new FundTransferService(fundTransferSettings, dbManager, telegramService);
                _fundTransferService.OnLog += (msg) => AddLog($"[FundTransfer] {msg}");
                _fundTransferService.OnTransferCompleted += (result) =>
                {
                    if (result.Success)
                    {
                        AddAlert($"✅ 자금 이동 완료: {result.Request.FromExchange} → {result.Request.ToExchange} ({result.Request.Amount} USDT)");
                    }
                    else
                    {
                        AddAlert($"❌ 자금 이동 실패: {result.ErrorMessage}");
                    }
                };

                AddLog("[Advanced Features] ✅ 자금 이동 서비스 준비 완료");

                // 3. 리밸런싱 서비스 초기화
                _rebalancingService = new PortfolioRebalancingService(rebalancingSettings, dbManager, telegramService);
                _rebalancingService.OnLog += (msg) => AddLog($"[Rebalancing] {msg}");
                _rebalancingService.OnRebalancingCompleted += (report) =>
                {
                    if (report.Success)
                    {
                        AddAlert($"✅ 리밸런싱 완료: {report.ExecutedActions.Count}개 액션 실행");
                    }
                    else
                    {
                        AddAlert($"❌ 리밸런싱 실패: {report.ErrorMessage}");
                    }
                };

                AddLog("[Advanced Features] ✅ 리밸런싱 서비스 준비 완료");

                AddLog("[Advanced Features] ✅ 모든 고급 기능 서비스 초기화 완료");
            }
            catch (Exception ex)
            {
                AddAlert($"❌ 고급 기능 서비스 초기화 실패: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// [WaveAI] 엘리엇 파동 AI 시스템 초기화 (자동 학습+로드)
        /// </summary>
        private Task InitializeWaveAIAsync()
        {
            try
            {
                bool torchFeaturesEnabled = AppConfig.Current?.Trading?.TransformerSettings?.Enabled ?? false;
                if (!torchFeaturesEnabled)
                {
                    AddLog("[WaveAI] 🛡️ Torch/Transformer 비활성화 상태라 WaveAI 자동 초기화를 건너뜁니다.");
                    return Task.CompletedTask;
                }

                /* TensorFlow 전환 중 비활성화
                if (!TorchInitializer.IsAvailable && !TorchInitializer.TryInitialize())
                {
                    AddLog($"[WaveAI] 🛡️ Torch 안전모드로 WaveAI 자동 초기화를 건너뜁니다. ({TorchInitializer.ErrorMessage})");
                    return;
                }
                */

                AddLog("[WaveAI] 🌊 엘리엇 파동 AI 시스템 초기화 중...");

                // IExchangeService 생성 (Simulation 또는 실제 Binance)
                bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                IExchangeService exchangeService;

                if (isSimulation)
                {
                    decimal simulationBalance = AppConfig.Current?.Trading?.SimulationInitialBalance ?? 10000m;
                    exchangeService = new MockExchangeService(simulationBalance);
                    AddLog("[WaveAI] 🎮 가상 거래소 모드");
                }
                else
                {
                    exchangeService = new BinanceExchangeService(
                        AppConfig.BinanceApiKey,
                        AppConfig.BinanceApiSecret
                    );
                    AddLog("[WaveAI] 🔗 바이낸스 연결");
                }

                /* TensorFlow 전환 중 임시 비활성화
                // WaveAIManager 초기화 (모델 없으면 자동 학습)
                _waveAIManager = new WaveAIManager(exchangeService);
                await _waveAIManager.InitializeAsync(CancellationToken.None);

                // TradingEngine에 WaveEngine 전달 (ViewModel._engine이 private이므로 공개 메서드 필요)
                ViewModel?.SetWaveEngine(_waveAIManager.WaveEngine);

                AddLog("[WaveAI] ✅ 엘리엇 파동 AI 시스템 초기화 완료");
                */
                AddLog("[WaveAI] ℹ️ 엘리엇 파동 AI 시스템은 TensorFlow.NET 전환 작업 중입니다 (임시 비활성화)");
            }
            catch (Exception ex)
            {
                AddAlert($"❌ WaveAI 초기화 실패: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 고급 기능 서비스 시작
        /// </summary>
        public async Task StartAdvancedFeaturesAsync()
        {
            try
            {
                if (_advancedFeaturesCts?.IsCancellationRequested != false)
                {
                    _advancedFeaturesCts?.Dispose();
                    _advancedFeaturesCts = new CancellationTokenSource();
                }

                var token = _advancedFeaturesCts?.Token ?? default;

                // 서비스 시작
                if (_arbitrageService != null)
                {
                    await _arbitrageService.StartAsync(_arbitrageService.Settings?.DefaultQuantity.ToString() != null ?
                        new() { "BTC/USDT", "ETH/USDT" } : new(), token);
                }

                if (_fundTransferService != null)
                {
                    await _fundTransferService.StartMonitoringAsync(token);
                }

                if (_rebalancingService != null)
                {
                    await _rebalancingService.StartMonitoringAsync(token);
                }

                AddLog("🟢 [Advanced Features] 모든 고급 기능 서비스 시작됨");
            }
            catch (Exception ex)
            {
                AddAlert($"❌ 고급 기능 서비스 시작 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 고급 기능 서비스 중지
        /// </summary>
        public void StopAdvancedFeatures()
        {
            try
            {
                _arbitrageService?.Stop();
                _fundTransferService?.Stop();
                _rebalancingService?.Stop();
                _advancedFeaturesCts?.Cancel();

                AddLog("🔴 [Advanced Features] 모든 고급 기능 서비스 중지됨");
            }
            catch (Exception ex)
            {
                AddAlert($"❌ 고급 기능 서비스 중지 중 오류: {ex.Message}");
            }
        }
        #endregion
    }
}
