﻿﻿﻿using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.IO;
using System.Runtime.InteropServices; // 추가 필요
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TradingBot.Models;
using TradingBot.ViewModels;
using Velopack;

namespace TradingBot
{
    public partial class MainWindow : Window
    {
        // --- Windows API 호출을 위한 구조체 및 메서드 정의 ---
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        // 아이콘 깜빡임 실행 메서드
        public void FlashWindow()
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => FlashWindow()); return; }

            // 현재 창의 핸들 가져오기
            WindowInteropHelper helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return;

            FLASHWINFO fi = new FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(fi);
            fi.hwnd = helper.Handle;
            fi.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
            fi.uCount = uint.MaxValue; // 무한 반복
            fi.dwTimeout = 0;

            FlashWindowEx(ref fi);
        }
        public static MainWindow? Instance { get; private set; }

        // [MVVM] ViewModel 추가
        public MainViewModel ViewModel { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            ViewModel = new MainViewModel();
            this.DataContext = ViewModel;

            if (!Resources.Contains("InvertBooleanConverter"))
                Resources.Add("InvertBooleanConverter", new MainViewModel.InvertBooleanConverter());

            dgMultiTimeframe.ItemsSource = ViewModel.MarketDataList;
            lstLiveLog.ItemsSource = ViewModel.LiveLogs;
            lstAlerts.ItemsSource = ViewModel.Alerts;

            // 텍스트 및 색상 바인딩
            txtTotalEquity.SetBinding(TextBlock.TextProperty, new Binding("TotalEquity"));
            txtTotalEquity.SetBinding(TextBlock.ForegroundProperty, new Binding("EquityColor"));
            txtAvailableBalance.SetBinding(TextBlock.TextProperty, new Binding("AvailableBalance"));
            txtTelegramStatus.SetBinding(TextBlock.TextProperty, new Binding("TelegramStatus"));
            txtTelegramStatus.SetBinding(TextBlock.ForegroundProperty, new Binding("TelegramStatusColor"));
            txtMajorSlot.SetBinding(TextBlock.TextProperty, new Binding("MajorSlotText"));
            txtMajorSlot.SetBinding(TextBlock.ForegroundProperty, new Binding("MajorSlotColor"));
            txtPumpSlot.SetBinding(TextBlock.TextProperty, new Binding("PumpSlotText"));
            txtPumpSlot.SetBinding(TextBlock.ForegroundProperty, new Binding("PumpSlotColor"));
            txtTotalPositionInfo.SetBinding(TextBlock.TextProperty, new Binding("TotalPositionInfo"));
            lblFooter.SetBinding(TextBlock.TextProperty, new Binding("FooterText"));
            pgScanning.SetBinding(ProgressBar.ValueProperty, new Binding("ScanProgress"));
            pgScanning.SetBinding(ProgressBar.ForegroundProperty, new Binding("ScanProgressColor"));

            // 버튼 활성/비활성 상태 바인딩
            btnStart.SetBinding(Button.IsEnabledProperty, new Binding("IsStartEnabled"));
            btnStop.SetBinding(Button.IsEnabledProperty, new Binding("IsStopEnabled"));

            LoadWindowSettings();

            this.Loaded += async (s, e) => await CheckForUpdatesAsync();
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
            ViewModel.RunBacktestCommand.Execute(null);
        }

        private void btnOptimize_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OptimizeCommand.Execute(null);
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
            var chartWindow = new ActivePnLWindow(ViewModel);
            chartWindow.Show();
        }

        private void btnStatistics_Click(object sender, RoutedEventArgs e)
        {
            var statsWindow = new TradeStatisticsWindow(ViewModel.TradeHistory);
            statsWindow.Owner = this;
            statsWindow.Show();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveWindowSettings();
            base.OnClosing(e);
        }

        // 엔진에서 호출할 수익률 업데이트 메서드
        public void UpdateProfitChart(double currentEquity)
        {
            Dispatcher.Invoke(() =>
            {
                ViewModel.ProfitHistory.Add(currentEquity);
                // 데이터가 너무 많아지면 앞부분 삭제 (최근 50개 유지)
                if (ViewModel.ProfitHistory.Count > 50) ViewModel.ProfitHistory.RemoveAt(0);
            });
        }

        public void RefreshSignalUI(MultiTimeframeViewModel signal)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var existingItem = ViewModel.MarketDataList.FirstOrDefault(x => x.Symbol == signal.Symbol);

                if (existingItem != null)
                {
                    // 1. 가격 변동 방향 확인
                    bool isPriceUp = signal.LastPrice > existingItem.LastPrice;
                    bool isPriceDown = signal.LastPrice < existingItem.LastPrice;

                    // 2. 데이터 업데이트
                    existingItem.LastPrice = signal.LastPrice;
                    existingItem.RSI_1H = signal.RSI_1H;
                    existingItem.AIScore = signal.AIScore;
                    existingItem.Decision = signal.Decision;
                    existingItem.BBPosition = signal.BBPosition;

                    // 3. 애니메이션 실행 (DataGrid의 특정 셀 찾기)
                    if (isPriceUp || isPriceDown)
                    {
                        TriggerPriceAnimation(signal.Symbol ?? "", isPriceUp);
                    }
                }
                else
                {
                    ViewModel.MarketDataList.Add(signal);
                }
            });
        }
        public void RefreshTradeGrid()
        {
            Dispatcher.Invoke(() =>
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
            });
        }
        private void TriggerPriceAnimation(string symbol, bool isUp)
        {
            // DataGrid에서 해당 심볼의 행과 가격 컬럼(보통 index 1)을 찾아 애니메이션 적용
            var row = dgMultiTimeframe.ItemContainerGenerator.ContainerFromItem(
                ViewModel.MarketDataList.FirstOrDefault(x => x.Symbol == symbol)) as DataGridRow;

            if (row != null)
            {
                // 가격 컬럼(Index 1)의 시각적 요소 가져오기
                var cell = dgMultiTimeframe.Columns[1].GetCellContent(row) as TextBlock;
                if (cell != null)
                {
                    // 리소스에서 스토리보드 찾아서 실행
                    var sb = (Storyboard)this.Resources[isUp ? "FlashGreen" : "FlashRed"];
                    sb.Begin(cell);
                }
            }
        }

        // 1. 단순 활동 기록 (스캔 중..., 대기 중...)
        public void AddLiveLog(string msg)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => AddLiveLog(msg)); return; }
            ViewModel.LiveLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (ViewModel.LiveLogs.Count > 50) ViewModel.LiveLogs.RemoveAt(50);
        }

        // 2. 중요 매매 알림 (매수 완료!, 손절!, 급등 포착!)
        public void AddAlert(string msg)
        {
            if (!CheckAccess()) { Dispatcher.Invoke(() => AddAlert(msg)); return; }
            ViewModel.Alerts.Insert(0, $"▶ {DateTime.Now:HH:mm:ss} | {msg}");
            if (ViewModel.Alerts.Count > 100) ViewModel.Alerts.RemoveAt(100);
        }

        // 하단 상태바 메시지 업데이트
        public void AddLog(string msg)
        {
            if (!CheckAccess()) // UI 쓰레드 체크
            {
                Dispatcher.Invoke(() => AddLog(msg));
                return;
            }
            ViewModel.FooterText = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        }

        // 스캔 진행률 업데이트 (ProgressBar 및 텍스트)
        public void UpdateProgress(int current, int total)
        {
            if (!CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateProgress(current, total));
                return;
            }

            if (total > 0)
            {
                double percentage = (double)current / total * 100;
                ViewModel.ScanProgress = percentage;

                if (current >= total)
                {
                    // 스캔 완료 시 파란색으로 변경 (대기 상태)
                    ViewModel.ScanProgressColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3"));
                    ViewModel.FooterText = $"모든 종목 스캔 완료. 다음 주기 대기 중...";
                    AddLog("전체 스캔 완료. 다음 주기 대기 중...");
                }
                else
                {
                    // 스캔 중에는 다시 초록색으로 유지
                    ViewModel.ScanProgressColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E676"));
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



        public void UpdateProfitDashboardUI(double equity, double available, int totalPosCount)
        {
            Dispatcher.Invoke(() =>
            {
                // 1. 기본 값 표시
                ViewModel.TotalEquity = $"${equity:N2}";
                ViewModel.AvailableBalance = $"${available:N2}";

                // 2. 미실현 손익(PNL) 계산 
                // (현재 자산 - 초기 자산) 또는 엔진에서 계산된 pnl 값을 파라미터로 받아도 됩니다.
                double pnl = equity - (double)available;

                // 3. 색상 분기 로직 적용
                if (pnl > 0)
                {
                    ViewModel.EquityColor = Brushes.LimeGreen;
                }
                else if (pnl < 0)
                {
                    ViewModel.EquityColor = Brushes.Tomato;
                }
                else
                {
                    ViewModel.EquityColor = Brushes.White;
                }
            });
        }

        public void UpdateTelegramStatus(bool isConnected, string text)
        {
            Dispatcher.Invoke(() =>
            {
                ViewModel.TelegramStatus = text;
                ViewModel.TelegramStatusColor = isConnected ? Brushes.DeepSkyBlue : Brushes.Gray;
            });
        }

        public void UpdateSlotStatusUI(int majorCount, int majorMax, int pumpCount, int pumpMax)
        {
            Dispatcher.Invoke(() =>
            {
                // 메이저 슬롯 UI 업데이트 (예: "메이저: 1 / 2")
                ViewModel.MajorSlotText = $"{majorCount} / {majorMax}";
                ViewModel.MajorSlotColor = majorCount >= majorMax ? Brushes.Orange : Brushes.White;

                // 급등주 슬롯 UI 업데이트 (예: "급등주: 0 / 2")
                ViewModel.PumpSlotText = $"{pumpCount} / {pumpMax}";
                ViewModel.PumpSlotColor = pumpCount >= pumpMax ? Brushes.Orange : Brushes.White;

                // 전체 포지션 요약 정보
                ViewModel.TotalPositionInfo = $"Active: {majorCount + pumpCount} 명";
            });
        }
        public void OnPositionEntered()
        {
            // UI 스레드 안전하게 호출
            Dispatcher.Invoke(() =>
            {
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
            });
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

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                // TODO: 실제 배포 URL로 변경하세요 (예: GitHub Releases URL 또는 S3 버킷 경로)
                string updateUrl = "https://github.com/YourUser/CoinFF-TradingBot";
                var updateManager = new UpdateManager(updateUrl);

                var newVersion = await updateManager.CheckForUpdatesAsync();
                if (newVersion != null)
                {
                    string version = newVersion.TargetFullRelease.Version.ToString();
                    string releaseNotes = "릴리스 정보를 불러오는 중...";

                    // GitHub API를 통해 릴리스 노트 가져오기 시도
                    try
                    {
                        var uri = new Uri(updateUrl);
                        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length >= 2)
                        {
                            releaseNotes = await GetReleaseNotesAsync(segments[0], segments[1], version);
                        }
                    }
                    catch { releaseNotes = "릴리스 노트를 불러올 수 없습니다."; }

                    var result = MessageBox.Show($"새 버전({version})이 발견되었습니다.\n\n[변경 사항]\n{releaseNotes}\n\n지금 업데이트하시겠습니까?", 
                                                 "업데이트 확인", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        // 진행률 업데이트 콜백
                        Action<int> progressCallback = (progress) =>
                        {
                            Dispatcher.Invoke(() => ViewModel.FooterText = $"업데이트 다운로드 중... {progress}%");
                        };

                        await updateManager.DownloadUpdatesAsync(newVersion, progressCallback);
                        updateManager.ApplyUpdatesAndRestart(newVersion);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ 업데이트 확인 실패: {ex.Message}");
            }
        }

        private async Task<string> GetReleaseNotesAsync(string owner, string repo, string version)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TradingBot"); // GitHub API 필수 헤더

            // 태그명 추측 (v1.0.0 또는 1.0.0)
            string[] tags = { $"v{version}", version };

            foreach (var tag in tags)
            {
                try
                {
                    string url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";
                    var response = await client.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("body", out var body))
                    {
                        return body.GetString() ?? "내용 없음";
                    }
                }
                catch { continue; }
            }
            return "릴리스 노트를 찾을 수 없습니다.";
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
                    var settings = JsonSerializer.Deserialize<WindowSettings>(settingsJson);

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
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings));
        }
    }
}