using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private TradingEngine? _engine;

        // 데이터 컬렉션
        public ObservableCollection<MultiTimeframeViewModel> MarketDataList { get; set; } = new ObservableCollection<MultiTimeframeViewModel>();
        public ChartValues<double> ProfitHistory { get; set; } = new ChartValues<double>();
        public ObservableCollection<string> LiveLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> Alerts { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<TradeLog> TradeHistory { get; set; } = new ObservableCollection<TradeLog>();

        // 대시보드 상태
        private string _totalEquity = "$0.00";
        public string TotalEquity { get => _totalEquity; set { _totalEquity = value; OnPropertyChanged(); } }

        private Brush _equityColor = Brushes.White;
        public Brush EquityColor { get => _equityColor; set { _equityColor = value; OnPropertyChanged(); } }

        private string _availableBalance = "$0.00";
        public string AvailableBalance { get => _availableBalance; set { _availableBalance = value; OnPropertyChanged(); } }

        // 텔레그램 상태
        private string _telegramStatus = "Telegram: Disconnected";
        public string TelegramStatus { get => _telegramStatus; set { _telegramStatus = value; OnPropertyChanged(); } }

        private Brush _telegramStatusColor = Brushes.Gray;
        public Brush TelegramStatusColor { get => _telegramStatusColor; set { _telegramStatusColor = value; OnPropertyChanged(); } }

        // 슬롯 상태
        private string _majorSlotText = "0 / 4";
        public string MajorSlotText { get => _majorSlotText; set { _majorSlotText = value; OnPropertyChanged(); } }

        private Brush _majorSlotColor = Brushes.White;
        public Brush MajorSlotColor { get => _majorSlotColor; set { _majorSlotColor = value; OnPropertyChanged(); } }

        private string _pumpSlotText = "0 / 2";
        public string PumpSlotText { get => _pumpSlotText; set { _pumpSlotText = value; OnPropertyChanged(); } }

        private Brush _pumpSlotColor = Brushes.White;
        public Brush PumpSlotColor { get => _pumpSlotColor; set { _pumpSlotColor = value; OnPropertyChanged(); } }

        private string _totalPositionInfo = "Active: 0 명";
        public string TotalPositionInfo { get => _totalPositionInfo; set { _totalPositionInfo = value; OnPropertyChanged(); } }

        // 하단 상태바 및 진행률
        private string _footerText = "Ready";
        public string FooterText { get => _footerText; set { _footerText = value; OnPropertyChanged(); } }

        private double _scanProgress = 0;
        public double ScanProgress { get => _scanProgress; set { _scanProgress = value; OnPropertyChanged(); } }

        private Brush _scanProgressColor = Brushes.LimeGreen;
        public Brush ScanProgressColor { get => _scanProgressColor; set { _scanProgressColor = value; OnPropertyChanged(); } }

        private double _winRate = 0;
        public double WinRate { get => _winRate; set { _winRate = value; OnPropertyChanged(); } }

        private double _totalProfit = 0;
        public double TotalProfit { get => _totalProfit; set { _totalProfit = value; OnPropertyChanged(); } }

        private double _averageRoe = 0;
        public double AverageRoe { get => _averageRoe; set { _averageRoe = value; OnPropertyChanged(); } }

        private SeriesCollection _backtestSeries = new();
        public SeriesCollection BacktestSeries
        {
            get => _backtestSeries;
            set { _backtestSeries = value; OnPropertyChanged(); }
        }

        private string[] _backtestLabels = Array.Empty<string>();
        public string[] BacktestLabels
        {
            get => _backtestLabels;
            set { _backtestLabels = value; OnPropertyChanged(); }
        }
        public Func<double, string> BacktestFormatter { get; set; } = val => val.ToString("N2");

        private SeriesCollection _activePnLSeries = new();
        public SeriesCollection ActivePnLSeries
        {
            get => _activePnLSeries;
            set { _activePnLSeries = value; OnPropertyChanged(); }
        }
        public string[] ActivePnLLabels { get; set; } = Array.Empty<string>();
        public Func<double, string> PnLFormatter { get; set; }

        public string LoggedInUser => $"User: {AppConfig.CurrentUsername}";

        private bool _isDarkTheme = true;
        private Brush _mainBackground = Brushes.Black;
        public Brush MainBackground { get => _mainBackground; set { _mainBackground = value; OnPropertyChanged(); } }

        private Brush _panelBackground = Brushes.DarkSlateGray;
        public Brush PanelBackground { get => _panelBackground; set { _panelBackground = value; OnPropertyChanged(); } }

        private Brush _textForeground = Brushes.White;
        public Brush TextForeground { get => _textForeground; set { _textForeground = value; OnPropertyChanged(); } }

        private Brush _subTextForeground = Brushes.LightGray;
        public Brush SubTextForeground { get => _subTextForeground; set { _subTextForeground = value; OnPropertyChanged(); } }

        private Brush _borderColor = Brushes.Gray;
        public Brush BorderColor { get => _borderColor; set { _borderColor = value; OnPropertyChanged(); } }

        private SeriesCollection _liveChartSeries = new SeriesCollection();
        public SeriesCollection LiveChartSeries { get => _liveChartSeries; set { _liveChartSeries = value; OnPropertyChanged(); } }
        public Func<double, string> LiveChartXFormatter { get; set; }
        public Func<double, string> LiveChartYFormatter { get; set; }
        public bool IsLiveChartEmpty => LiveChartSeries == null || LiveChartSeries.Count == 0;

        private MultiTimeframeViewModel _selectedSymbol;
        public MultiTimeframeViewModel SelectedSymbol
        {
            get => _selectedSymbol;
            set
            {
                if (_selectedSymbol != value)
                {
                    _selectedSymbol = value;
                    OnPropertyChanged();
                    LoadLiveChartData();
                }
            }
        }

        public class InvertBooleanConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => !(bool)value;
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => !(bool)value;
        }

        private DateTime _startDate = DateTime.Today.AddDays(-7);
        public DateTime StartDate { get => _startDate; set { _startDate = value; OnPropertyChanged(); } }

        private DateTime _endDate = DateTime.Today;
        public DateTime EndDate { get => _endDate; set { _endDate = value; OnPropertyChanged(); } }

        private bool _isAccountSummaryVisible = true;
        public bool IsAccountSummaryVisible { get => _isAccountSummaryVisible; set { _isAccountSummaryVisible = value; OnPropertyChanged(); } }

        private bool _isLiveLogVisible = true;
        public bool IsLiveLogVisible { get => _isLiveLogVisible; set { _isLiveLogVisible = value; OnPropertyChanged(); } }

        private bool _isChartVisible = true;
        public bool IsChartVisible { get => _isChartVisible; set { _isChartVisible = value; OnPropertyChanged(); } }

        // Commands
        public ICommand StartCommand { get; private set; }
        public ICommand StopCommand { get; private set; }
        public ICommand RunBacktestCommand { get; private set; }
        public ICommand OptimizeCommand { get; private set; }
        public ICommand ToggleThemeCommand { get; private set; }
        public ICommand LoadHistoryCommand { get; private set; }
        public ICommand ClosePositionCommand { get; private set; }
        public ICommand ToggleWidgetCommand { get; private set; }

        // Command Properties for Button State
        private bool _isStartEnabled = true;
        public bool IsStartEnabled { get => _isStartEnabled; set { _isStartEnabled = value; OnPropertyChanged(); } }

        private bool _isStopEnabled = false;
        public bool IsStopEnabled { get => _isStopEnabled; set { _isStopEnabled = value; OnPropertyChanged(); } }

        public MainViewModel()
        {
            // Initialize Engine
            if (!DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            {
                _engine = new TradingEngine();
                SubscribeToEngineEvents();
            }

            // 테마 초기화
            ApplyTheme();
            ToggleThemeCommand = new RelayCommand(_ =>
            {
                _isDarkTheme = !_isDarkTheme;
                ApplyTheme();
            });

            StartCommand = new RelayCommand(async _ =>
            {
                IsStartEnabled = false;
                IsStopEnabled = true;
                try
                {
                if (_engine != null)
                    await _engine.StartScanningOptimizedAsync();
                }
                catch (Exception ex)
                {
                    AddLog($"실행 오류: {ex.Message}");
                }
                finally
                {
                    IsStartEnabled = true;
                    IsStopEnabled = false;
                }
            }, _ => IsStartEnabled);

            StopCommand = new RelayCommand(_ =>
            {
                try
                {
                if (_engine != null)
                    _engine.StopEngine();
                    IsStopEnabled = false;
                    IsStartEnabled = true;
                    AddLog("엔진 정지 명령을 보냈습니다.");
                }
                catch (Exception ex)
                {
                    AddLog($"정지 중 오류: {ex.Message}");
                }
            }, _ => IsStopEnabled);

            RunBacktestCommand = new RelayCommand(async _ =>
            {
                try
                {
                    AddLog("🔄 백테스팅 시작 (BTCUSDT, 최근 30일, RSI 전략)...");
                    var service = new BacktestService();
                    var endDate = DateTime.Now;
                    var startDate = endDate.AddDays(-30);

                    // 기본값으로 BTCUSDT, RSI 전략 실행
                    var result = await service.RunBacktestAsync("BTCUSDT", startDate, endDate, BacktestStrategyType.RSI);

                    UpdateBacktestChart(result);

                    RunOnUI(() =>
                    {
                        var window = new BacktestWindow(result);
                        window.Show();
                    });

                    AddLog($"✅ 백테스팅 완료: 수익률 {result.ProfitPercentage:F2}%, MDD {result.MaxDrawdown:F2}%, 승률 {result.WinRate:F1}%");
                }
                catch (Exception ex)
                {
                    AddLog($"❌ 백테스팅 실패: {ex.Message}");
                }
            });

            OptimizeCommand = new RelayCommand(async _ =>
            {
                try
                {
                    AddLog("⚙️ RSI 전략 파라미터 최적화 시작 (Grid Search)...");
                    var service = new BacktestService();
                    var endDate = DateTime.Now;
                    var startDate = endDate.AddDays(-30);

                    var bestResult = await service.OptimizeRsiStrategyAsync("BTCUSDT", startDate, endDate, 1000);

                    UpdateBacktestChart(bestResult);

                    RunOnUI(() =>
                    {
                        var window = new BacktestWindow(bestResult);
                        window.Show();
                    });

                    AddLog($"🏆 최적화 완료! 설정: [{bestResult.StrategyConfiguration}]");
                    AddLog($"📊 결과: 수익률 {bestResult.ProfitPercentage:F2}%, 순수익 ${bestResult.TotalProfit:N2}, MDD {bestResult.MaxDrawdown:F2}%");
                }
                catch (Exception ex)
                {
                    AddLog($"❌ 최적화 실패: {ex.Message}");
                }
            });

            LoadHistoryCommand = new RelayCommand(async _ => await LoadTradeHistory());

            ClosePositionCommand = new RelayCommand(async param =>
            {
                if (param is string symbol)
                {
                    if (MessageBox.Show($"{symbol} 포지션을 현재가로 청산하시겠습니까?", "수동 청산", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        if (_engine != null)
                            await _engine.ClosePositionAsync(symbol);
                        AddLog($"⚡ {symbol} 수동 청산 명령 전송됨");
                    }
                }
            });

            ToggleWidgetCommand = new RelayCommand(param =>
            {
                if (param is string widgetName)
                {
                    switch (widgetName)
                    {
                        case "Account": IsAccountSummaryVisible = !IsAccountSummaryVisible; break;
                        case "Log": IsLiveLogVisible = !IsLiveLogVisible; break;
                        case "Chart": IsChartVisible = !IsChartVisible; break;
                    }
                }
            });

            // Enable collection synchronization for cross-thread updates
            BindingOperations.EnableCollectionSynchronization(MarketDataList, new object());
            BindingOperations.EnableCollectionSynchronization(LiveLogs, new object());
            BindingOperations.EnableCollectionSynchronization(Alerts, new object());
            BindingOperations.EnableCollectionSynchronization(TradeHistory, new object());
            BacktestFormatter = value => value.ToString("C0"); // 통화 형식 포맷터

            LiveChartXFormatter = val => new DateTime((long)val).ToString("HH:mm");
            LiveChartYFormatter = val => val.ToString("N4");
            PnLFormatter = value => value.ToString("F2") + "%";

            // 초기 실행 시 이력 로드
            _ = LoadTradeHistory();
        }

        public async Task LoadTradeHistory()
        {
            try
            {
                var db = new DbManager(AppConfig.ConnectionString);
                // EndDate의 시간을 23:59:59로 설정하여 해당 일자의 모든 데이터를 포함
                var end = EndDate.Date.AddDays(1).AddTicks(-1);
                var logs = await db.GetTradeLogsAsync(1000, StartDate, end); // 기간 조회 시 넉넉하게 1000건

                RunOnUI(() =>
                {
                    TradeHistory.Clear();
                    foreach (var log in logs) TradeHistory.Add(log);
                    
                    // 통계 계산
                    CalculateTradeStatistics();
                });
                AddLog($"📜 매매 이력 로드 완료 ({StartDate:MM/dd} ~ {EndDate:MM/dd}, {logs.Count}건)");
            }
            catch (Exception ex)
            {
                AddLog($"❌ 이력 로드 실패: {ex.Message}");
            }
        }

        private void CalculateTradeStatistics()
        {
            if (TradeHistory == null || TradeHistory.Count == 0)
            {
                WinRate = 0;
                TotalProfit = 0;
                AverageRoe = 0;
                return;
            }

            var profitableTrades = TradeHistory.Where(t => t.PnL > 0).ToList();
            WinRate = (double)profitableTrades.Count / TradeHistory.Count * 100;
            TotalProfit = (double)TradeHistory.Sum(t => t.PnL);
            AverageRoe = (double)TradeHistory.Average(t => t.PnLPercent);
        }

        private void ApplyTheme()
        {
            if (_isDarkTheme)
            {
                MainBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F111A"));
                PanelBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#161925"));
                TextForeground = Brushes.White;
                SubTextForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
                BorderColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D3142"));
            }
            else
            {
                MainBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F1F5F9")); // 밝은 회색 배경
                PanelBackground = Brushes.White;
                TextForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B")); // 진한 남색 텍스트
                SubTextForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
                BorderColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
            }
        }

        private void SubscribeToEngineEvents()
        {
            if (_engine == null) return;
            _engine.OnLiveLog += msg => AddLiveLog(msg);
            _engine.OnAlert += msg => AddAlert(msg);
            _engine.OnStatusLog += msg => AddLog(msg);
            _engine.OnProgress += (current, total) => UpdateProgress(current, total);
            _engine.OnDashboardUpdate += (equity, available, posCount) => UpdateProfitDashboard(equity, available, posCount);
            _engine.OnSlotStatusUpdate += (major, majorMax, pump, pumpMax) => UpdateSlotStatus(major, majorMax, pump, pumpMax);
            _engine.OnTelegramStatusUpdate += (isConnected, text) => UpdateTelegramStatus(isConnected, text);
            _engine.OnSignalUpdate += vm => UpdateSignal(vm);
            _engine.OnTickerUpdate += (symbol, price, pnl) => UpdateTicker(symbol, price, pnl);
            _engine.OnSymbolTracking += symbol => EnsureSymbolInList(symbol);
            _engine.OnPositionStatusUpdate += (symbol, isActive, entryPrice) => UpdatePositionStatus(symbol, isActive, entryPrice);
            _engine.OnTradeExecuted += HandleTradeExecuted;
        }

        private void HandleTradeExecuted(string symbol, string side, decimal price, decimal qty)
        {
            // 1. 알림 로그 추가
            AddAlert($"[거래 체결] {symbol} {side} | 가격: {price:F4} | 수량: {qty}");

            // 2. 실시간 차트에 마커 추가
            RunOnUI(() =>
            {
                if (SelectedSymbol != null && SelectedSymbol.Symbol == symbol && LiveChartSeries.Count > 2)
                {
                    var tradeTime = DateTime.Now;
                    var point = new ScatterPoint(tradeTime.Ticks, (double)price);

                    // side가 "BUY" 또는 "LONG"일 경우
                    if (side.ToUpper() == "BUY" || side.ToUpper() == "LONG")
                        (LiveChartSeries[1].Values as IChartValues)?.Add(point);
                    else // "SELL" 또는 "SHORT" 또는 청산
                        (LiveChartSeries[2].Values as IChartValues)?.Add(point);
                }
            });
        }

        private void AddLiveLog(string msg)
        {
            RunOnUI(() =>
            {
                LiveLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
                if (LiveLogs.Count > 50) LiveLogs.RemoveAt(50);
            });
        }

        private void AddAlert(string msg)
        {
            RunOnUI(() =>
            {
                Alerts.Insert(0, $"▶ {DateTime.Now:HH:mm:ss} | {msg}");
                if (Alerts.Count > 100) Alerts.RemoveAt(100);
            });
        }

        private void AddLog(string msg)
        {
            RunOnUI(() => FooterText = $"[{DateTime.Now:HH:mm:ss}] {msg}");
        }

        private void UpdateProgress(int current, int total)
        {
            RunOnUI(() =>
            {
                if (total > 0)
                {
                    double percentage = (double)current / total * 100;
                    ScanProgress = percentage;
                    if (current >= total)
                    {
                        ScanProgressColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3"));
                        FooterText = $"모든 종목 스캔 완료. 다음 주기 대기 중...";
                    }
                    else
                    {
                        ScanProgressColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E676"));
                        FooterText = $"스캔 진행 중: {current}/{total} ({percentage:F0}%)";
                    }
                }
            });
        }

        private void UpdateProfitDashboard(double equity, double available, int totalPosCount)
        {
            RunOnUI(() =>
            {
                TotalEquity = $"${equity:N2}";
                AvailableBalance = $"${available:N2}";
                double pnl = equity - available;
                EquityColor = pnl > 0 ? Brushes.LimeGreen : (pnl < 0 ? Brushes.Tomato : Brushes.White);

                ProfitHistory.Add(equity);
                if (ProfitHistory.Count > 50) ProfitHistory.RemoveAt(0);

                // [추가] 실시간 포지션 수익률 차트 업데이트
                UpdateActivePnLChart();
            });
        }

        private void UpdateActivePnLChart()
        {
            RunOnUI(() =>
            {
                var activePositions = MarketDataList.Where(x => x.IsPositionActive).ToList();
                
                if (activePositions.Any())
                {
                    var values = new ChartValues<double>(activePositions.Select(x => x.ProfitPercent));
                    
                    ActivePnLSeries = new SeriesCollection
                    {
                        new ColumnSeries
                        {
                            Title = "PnL %",
                            Values = values,
                            Fill = Brushes.DeepSkyBlue,
                            DataLabels = true
                        }
                    };
                    ActivePnLLabels = activePositions.Select(x => x.Symbol ?? "Unknown").ToArray();
                    OnPropertyChanged(nameof(ActivePnLLabels));
                }
                else
                {
                    ActivePnLSeries = new SeriesCollection();
                }
            });
        }

        private void UpdateSlotStatus(int major, int majorMax, int pump, int pumpMax)
        {
            RunOnUI(() =>
            {
                MajorSlotText = $"{major} / {majorMax}";
                MajorSlotColor = major >= majorMax ? Brushes.Orange : Brushes.White;
                PumpSlotText = $"{pump} / {pumpMax}";
                PumpSlotColor = pump >= pumpMax ? Brushes.Orange : Brushes.White;
                TotalPositionInfo = $"Active: {major + pump} 명";
            });
        }

        private void UpdateTelegramStatus(bool isConnected, string text)
        {
            RunOnUI(() =>
            {
                TelegramStatus = text;
                TelegramStatusColor = isConnected ? Brushes.DeepSkyBlue : Brushes.Gray;
            });
        }

        private void UpdateSignal(MultiTimeframeViewModel signal)
        {
            RunOnUI(() =>
            {
                var existing = MarketDataList.FirstOrDefault(x => x.Symbol == signal.Symbol);
                if (existing != null)
                {
                    // Update properties
                    if (signal.LastPrice > 0) existing.LastPrice = signal.LastPrice;
                    if (signal.RSI_1H > 0) existing.RSI_1H = signal.RSI_1H;
                    if (signal.AIScore > 0) existing.AIScore = signal.AIScore;
                    if (!string.IsNullOrEmpty(signal.Decision)) existing.Decision = signal.Decision;
                    if (!string.IsNullOrEmpty(signal.BBPosition)) existing.BBPosition = signal.BBPosition;
                    if (signal.IsPositionActive) existing.IsPositionActive = true;
                    if (signal.EntryPrice > 0) existing.EntryPrice = signal.EntryPrice;

                    // Trigger animation via event or property if needed. 
                    // For now, we rely on property changes.
                }
                else
                {
                    MarketDataList.Add(signal);
                }
            });
        }

        private void UpdateTicker(string symbol, decimal price, double? pnl)
        {
            RunOnUI(() =>
            {
                var existing = MarketDataList.FirstOrDefault(x => x.Symbol == symbol);
                if (existing == null)
                {
                    existing = new MultiTimeframeViewModel { Symbol = symbol };
                    MarketDataList.Add(existing);
                }
                if (price > 0) existing.LastPrice = price;
                if (pnl.HasValue) existing.ProfitPercent = pnl.Value;
            });
        }

        private void EnsureSymbolInList(string symbol)
        {
            RunOnUI(() =>
            {
                if (!MarketDataList.Any(x => x.Symbol == symbol))
                {
                    MarketDataList.Add(new MultiTimeframeViewModel { Symbol = symbol });
                    AddLog($"🔍 신규 급등주 감시 리스트 추가: {symbol}");
                }
            });
        }

        private void UpdatePositionStatus(string symbol, bool isActive, decimal entryPrice)
        {
            RunOnUI(() =>
            {
                var existing = MarketDataList.FirstOrDefault(x => x.Symbol == symbol);
                if (existing != null)
                {
                    existing.IsPositionActive = isActive;
                    existing.EntryPrice = entryPrice;
                    if (isActive)
                    {
                        existing.TargetPrice = entryPrice * 1.03m;
                        existing.StopLossPrice = entryPrice * 0.985m;
                    }
                    else
                    {
                        existing.Decision = "WAIT";
                        existing.ProfitPercent = 0;
                    }
                }
            });
        }

        private async void LoadLiveChartData()
        {
            if (SelectedSymbol == null)
            {
                LiveChartSeries = new SeriesCollection();
                OnPropertyChanged(nameof(IsLiveChartEmpty));
                return;
            }

            try
            {
                using var client = new BinanceRestClient();
                var klinesResult = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    SelectedSymbol.Symbol,
                    KlineInterval.FifteenMinutes,
                    limit: 100 // 최근 100개 캔들
                );

                if (!klinesResult.Success) return;

                var candleValues = new ChartValues<OhlcPoint>();
                foreach (var kline in klinesResult.Data)
                {
                    candleValues.Add(new OhlcPoint((double)kline.OpenPrice, (double)kline.HighPrice, (double)kline.LowPrice, (double)kline.ClosePrice));
                }

                LiveChartSeries = new SeriesCollection
                {
                    new OhlcSeries { Title = SelectedSymbol.Symbol, Values = candleValues },
                    new ScatterSeries { Title = "Buy", Values = new ChartValues<ScatterPoint>(), PointGeometry = DefaultGeometries.Triangle, Fill = Brushes.LimeGreen, MinPointShapeDiameter = 15, DataLabels = false },
                    new ScatterSeries { Title = "Sell", Values = new ChartValues<ScatterPoint>(), PointGeometry = DefaultGeometries.Triangle, Fill = Brushes.Red, MinPointShapeDiameter = 15, DataLabels = false }
                };
                OnPropertyChanged(nameof(LiveChartSeries));
                OnPropertyChanged(nameof(IsLiveChartEmpty));
            }
            catch (Exception ex)
            {
                AddLog($"차트 로딩 실패: {ex.Message}");
            }
        }

        public void UpdateBacktestChart(BacktestResult result)
        {
            RunOnUI(() =>
            {
                var values = new ChartValues<double>();
                var buyPoints = new ChartValues<ScatterPoint>();
                var sellPoints = new ChartValues<ScatterPoint>();

                // EquityCurve와 TradeHistory 매핑
                // EquityCurve는 매 캔들마다 기록된다고 가정 (BacktestService 수정 필요)
                // 현재 구조상 EquityCurve는 매도 시점에만 기록되므로, 이를 보완하거나
                // TradeHistory의 Time을 X축 인덱스로 변환해야 함.
                // 여기서는 EquityCurve의 인덱스를 기준으로 표시합니다.
                
                for (int i = 0; i < result.EquityCurve.Count; i++)
                {
                    values.Add((double)result.EquityCurve[i]);
                }

                // 매매 기록을 차트 좌표에 매핑
                // TradeDates 리스트와 TradeLog.Time을 비교하여 인덱스 찾기
                foreach (var trade in result.TradeHistory)
                {
                    string timeStr = trade.Time.ToString("MM/dd HH:mm");
                    int index = result.TradeDates.IndexOf(timeStr);
                    
                    if (index >= 0)
                    {
                        if (trade.Side == "BUY")
                            buyPoints.Add(new ScatterPoint(index, (double)result.EquityCurve[index], 10));
                        else if (trade.Side == "SELL")
                            sellPoints.Add(new ScatterPoint(index, (double)result.EquityCurve[index], 10));
                    }
                }

                BacktestSeries = new SeriesCollection
                {
                    new LineSeries
                    {
                        Title = "Equity",
                        Values = values,
                        PointGeometry = null, // 선만 표시
                        LineSmoothness = 0,
                        Stroke = Brushes.Cyan,
                        Fill = new SolidColorBrush(Color.FromArgb(30, 0, 255, 255))
                    },
                    new ScatterSeries
                    {
                        Title = "Buy",
                        Values = buyPoints,
                        PointGeometry = DefaultGeometries.Triangle,
                        Fill = Brushes.LimeGreen,
                        MinPointShapeDiameter = 10
                    },
                    new ScatterSeries
                    {
                        Title = "Sell",
                        Values = sellPoints,
                        PointGeometry = DefaultGeometries.Triangle, // 역삼각형이 없으므로 색상으로 구분
                        Fill = Brushes.Red,
                        MinPointShapeDiameter = 10
                    }
                };

                BacktestLabels = result.TradeDates.ToArray();
            });
        }

        public async Task ShowTradeChartAsync(TradeLog log)
        {
            try
            {
                AddLog($"📈 {log.Symbol} 차트 데이터 로딩 중...");

                // 1. 바이낸스 클라이언트 생성 (데이터 조회용)
                using var client = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                });

                // 2. 해당 매매 시점 전후 데이터 조회 (전 60개, 후 10개 정도)
                // Binance API는 endTime 기준으로 가져오거나 startTime 기준으로 가져옴.
                // 여기서는 해당 시간 기준 앞뒤를 보기 위해 넉넉히 가져옵니다.
                var endTime = log.Time.AddMinutes(15 * 10); // 거래 후 10개 봉 정도 여유
                var klines = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    log.Symbol ?? "UNKNOWN", 
                    KlineInterval.FifteenMinutes, 
                    endTime: endTime, 
                    limit: 100 // 100개 조회
                );

                if (!klines.Success)
                {
                    AddLog($"❌ 차트 데이터 조회 실패: {klines.Error}");
                    return;
                }

                // 3. CandleData 변환
                var candles = klines.Data.Select(k => new CandleData
                {
                    Symbol = log.Symbol ?? "UNKNOWN",
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = (float)k.Volume
                    // 지표는 BacktestViewModel에서 계산하므로 여기선 기본값
                }).ToList();

                // 4. BacktestResult 생성 (차트 뷰모델 재사용을 위해)
                var result = new BacktestResult
                {
                    Symbol = log.Symbol ?? "UNKNOWN",
                    Candles = candles,
                    TradeHistory = new List<TradeLog> { log }
                };

                // 5. 팝업 표시
                RunOnUI(() =>
                {
                    var window = new BacktestWindow(result);
                    window.Title = $"Trade Chart - {log.Symbol} ({log.Time:MM/dd HH:mm})";
                    window.Show();
                });
            }
            catch (Exception ex) { AddLog($"❌ 차트 열기 오류: {ex.Message}"); }
        }

        private void RunOnUI(Action action)
        {
            Application.Current?.Dispatcher.Invoke(action);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}