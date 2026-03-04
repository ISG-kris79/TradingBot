using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;
using Microsoft.Data.SqlClient;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Shared.Models;

namespace TradingBot.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private static readonly HashSet<string> MajorSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTC", "ETH", "SOL", "XRP"
        };

        private TradingEngine? _engine;
        private DatabaseService? _dbService;

        // TradingEngine 속성 노출
        public decimal InitialBalance => _engine?.InitialBalance ?? 0;
        // 데이터 컬렉션
        public ObservableCollection<MultiTimeframeViewModel> MarketDataList { get; set; } = new ObservableCollection<MultiTimeframeViewModel>();
        public ChartValues<double> ProfitHistory { get; set; } = new ChartValues<double>();
        public ObservableCollection<string> LiveLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> MajorLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> PumpLogs { get; set; } = new ObservableCollection<string>();
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

        // [추가] RL 보상 차트 데이터
        private SeriesCollection _rlRewardSeries = new SeriesCollection();
        public SeriesCollection RLRewardSeries { get => _rlRewardSeries; set { _rlRewardSeries = value; OnPropertyChanged(); } }

        // [AI 모니터링] 예측 정확도 추적
        public ObservableCollection<AIModelPerformance> ModelPerformances { get; set; } = new ObservableCollection<AIModelPerformance>();

        // [AI 모니터링] 예측 vs 실제 비교 차트
        private SeriesCollection _predictionComparisonSeries = new SeriesCollection();
        public SeriesCollection PredictionComparisonSeries
        {
            get => _predictionComparisonSeries;
            set { _predictionComparisonSeries = value; OnPropertyChanged(); }
        }

        public string[] PredictionLabels { get; set; } = Array.Empty<string>();

        // [AI 모니터링] 실시간 정확도
        private double _mlNetAccuracy = 0.0;
        public double MLNetAccuracy { get => _mlNetAccuracy; set { _mlNetAccuracy = value; OnPropertyChanged(); } }

        private double _transformerAccuracy = 0.0;
        public double TransformerAccuracy { get => _transformerAccuracy; set { _transformerAccuracy = value; OnPropertyChanged(); } }

        // [추가] RL 탭 가시성
        private bool _isAiMonitorVisible = true;

        private MultiTimeframeViewModel? _selectedSymbol;
        public MultiTimeframeViewModel? SelectedSymbol
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
        public ICommand RecollectDataCommand { get; private set; }
        public ICommand ToggleThemeCommand { get; private set; }
        public ICommand LoadHistoryCommand { get; private set; }
        public ICommand ClosePositionCommand { get; private set; }
        public ICommand ToggleWidgetCommand { get; private set; }
        public ICommand ExportHistoryCommand { get; private set; } // [Agent 3] 추가

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

                // Initialize DatabaseService
                try
                {
                    _dbService = new DatabaseService((msg) => AddLiveLog(msg));
                }
                catch (Exception ex)
                {
                    AddLiveLog($"⚠️ DB 초기화 실패: {ex.Message}");
                }
            }

            // 테마 초기화
            ApplyTheme();
            ToggleThemeCommand = new RelayCommand(_ =>
            {
                _isDarkTheme = !_isDarkTheme;
                ApplyTheme();
            });

            StartCommand = new RelayCommand(_ =>
            {
                IsStartEnabled = false;
                IsStopEnabled = true;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_engine != null)
                            await _engine.StartScanningOptimizedAsync();
                    }
                    catch (Exception ex)
                    {
                        RunOnUI(() => AddLog($"실행 오류: {ex.Message}"));
                    }
                    finally
                    {
                        RunOnUI(() =>
                        {
                            IsStartEnabled = true;
                            IsStopEnabled = false;
                        });
                    }
                });
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

            RunBacktestCommand = new RelayCommand(_ =>
            {
                // 심볼 선택 확인
                if (SelectedSymbol == null)
                {
                    AddLog("⚠️ 백테스트를 실행하려면 먼저 종목을 선택하세요");
                    MessageBox.Show(
                        "백테스트를 실행하려면 아래 시장 그리드에서\n" +
                        "종목을 더블클릭하여 선택한 후 다시 시도해주세요.\n\n" +
                        "예: BTCUSDT, ETHUSDT 등의 행을 더블클릭",
                        "종목 선택 필요",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var symbol = SelectedSymbol.Symbol;
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    AddLog("⚠️ Invalid symbol selected");
                    return;
                }
                AddLog($"🔄 {symbol} Backtest starting (30 days, ElliottWave strategy)...");

                // UI 블로킹 방지: 백그라운드 실행 (fire-and-forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var service = new BacktestService();
                        var endDate = DateTime.Now;
                        var startDate = endDate.AddDays(-30);

                        RunOnUI(() => AddLog($"📅 데이터 조회: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}"));

                        // [수정] 기본 백테스트 전략을 RSI에서 ElliottWave로 변경
                        BacktestResult result = await service.RunBacktestAsync(symbol, startDate, endDate, BacktestStrategyType.ElliottWave);

                        if (result == null)
                        {
                            RunOnUI(() => AddLog($"❌ 백테스트 결과가 null입니다."));
                            return;
                        }

                        if (result.Candles == null || result.Candles.Count == 0)
                        {
                            MessageBoxResult recollectChoice = MessageBoxResult.No;
                            RunOnUI(() =>
                            {
                                AddLog($"⚠️ {symbol} 데이터가 없습니다. DB에 과거 데이터가 저장되어 있는지 확인하세요.");
                                recollectChoice = MessageBox.Show(
                                    $"'{symbol}' 종목의 과거 데이터가 없습니다.\n\n" +
                                    "지금 최근 30일 데이터를 재수집할까요?\n" +
                                    "(완료 후 백테스트를 자동 재시도합니다)",
                                    "데이터 없음",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);
                            });

                            if (recollectChoice == MessageBoxResult.Yes)
                            {
                                RunOnUI(() => AddLog($"🔄 {symbol} 과거 데이터 재수집 시작..."));
                                int recollectedCount = await service.RecollectRecentCandleDataAsync(symbol, 30);
                                RunOnUI(() => AddLog($"✅ {symbol} 재수집 완료: {recollectedCount}개"));

                                result = await service.RunBacktestAsync(symbol, startDate, endDate, BacktestStrategyType.ElliottWave);
                            }

                            if (result.Candles == null || result.Candles.Count == 0)
                            {
                                RunOnUI(() =>
                                {
                                    MessageBox.Show(
                                        $"'{symbol}' 종목 데이터가 여전히 부족합니다.\n\n" +
                                        "1) START로 실시간 수집을 켜두고\n" +
                                        "2) 몇 분 후 다시 백테스트를 실행해주세요.",
                                        "데이터 부족",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Warning);
                                });
                                return;
                            }
                        }

                        RunOnUI(() => AddLog($"✅ {result.Candles.Count}개 캔들 데이터 로드 완료"));

                        // UI 업데이트는 메인 스레드에서
                        RunOnUI(() =>
                        {
                            try
                            {
                                UpdateBacktestChart(result);

                                var window = new BacktestWindow(result, $"📊 BACKTEST - {symbol}");
                                window.Show();

                                AddLog($"✅ 백테스팅 완료: 수익률 {result.ProfitPercentage:F2}%, MDD {result.MaxDrawdown:F2}%, 승률 {result.WinRate:F1}%");
                            }
                            catch (Exception uiEx)
                            {
                                AddLog($"❌ UI 업데이트 실패: {uiEx.Message}");
                                MessageBox.Show($"백테스트 결과 창 표시 오류:\n{uiEx.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        });
                    }
                    catch (SqlException sqlEx)
                    {
                        RunOnUI(() => 
                        {
                            AddLog($"❌ 데이터베이스 오류: {sqlEx.Message}");
                            MessageBox.Show(
                                $"데이터베이스 연결 오류:\n{sqlEx.Message}\n\n" +
                                "appsettings.json의 ConnectionString을 확인하세요.",
                                "DB 오류",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUI(() => 
                        {
                            AddLog($"❌ 백테스팅 실패: {ex.Message}");
                            AddLog($"📍 Stack Trace: {ex.StackTrace}");
                            MessageBox.Show(
                                $"백테스트 실행 중 오류:\n\n{ex.Message}\n\n" +
                                $"타입: {ex.GetType().Name}\n\n" +
                                "자세한 내용은 로그를 확인하세요.",
                                "백테스트 오류",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            });

            OptimizeCommand = new RelayCommand(_ =>
            {
                // 심볼 선택 확인
                if (SelectedSymbol == null)
                {
                    AddLog("⚠️ 최적화를 실행하려면 먼저 종목을 선택하세요");
                    MessageBox.Show(
                        "최적화를 실행하려면 아래 시장 그리드에서\n" +
                        "종목을 더블클릭하여 선택한 후 다시 시도해주세요.\n\n" +
                        "예: BTCUSDT, ETHUSDT 등의 행을 더블클릭",
                        "종목 선택 필요",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var symbol = SelectedSymbol.Symbol;
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    AddLog("⚠️ Invalid symbol selected");
                    return;
                }

                AddLog($"⚙ {symbol} 최적화 시작 (Optuna 20회)...");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var service = new BacktestService();
                        var endDate = DateTime.Now;
                        var startDate = endDate.AddDays(-30);

                        // 데이터 존재 확인
                        var precheck = await service.RunBacktestAsync(symbol, startDate, endDate, BacktestStrategyType.RSI);
                        if (precheck.Candles == null || precheck.Candles.Count == 0)
                        {
                            MessageBoxResult recollectChoice = MessageBoxResult.No;
                            RunOnUI(() =>
                            {
                                recollectChoice = MessageBox.Show(
                                    $"'{symbol}' 최적화용 데이터가 없습니다.\n\n" +
                                    "최근 30일 데이터를 재수집할까요?",
                                    "데이터 없음",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question);
                            });

                            if (recollectChoice == MessageBoxResult.Yes)
                            {
                                RunOnUI(() => AddLog($"🔄 {symbol} 최적화용 데이터 재수집 시작..."));
                                int recollectedCount = await service.RecollectRecentCandleDataAsync(symbol, 30);
                                RunOnUI(() => AddLog($"✅ {symbol} 재수집 완료: {recollectedCount}개"));

                                precheck = await service.RunBacktestAsync(symbol, startDate, endDate, BacktestStrategyType.RSI);
                            }
                        }

                        if (precheck.Candles == null || precheck.Candles.Count == 0)
                        {
                            RunOnUI(() => MessageBox.Show(
                                $"'{symbol}' 데이터가 부족해 최적화를 실행할 수 없습니다.",
                                "최적화 실패",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning));
                            return;
                        }

                        var optimizeResult = await service.OptimizeWithOptunaAsync(symbol, startDate, endDate, 1000m, 20);
                        optimizeResult.Symbol = symbol;
                        if (optimizeResult.Candles == null || optimizeResult.Candles.Count == 0)
                            optimizeResult.Candles = precheck.Candles;
                        if (optimizeResult.FinalBalance <= 0)
                            optimizeResult.FinalBalance = optimizeResult.InitialBalance;

                        RunOnUI(() =>
                        {
                            var window = new BacktestWindow(optimizeResult, $"⚙ OPTIMIZE - {symbol}");
                            window.Show();
                            AddLog($"✅ 최적화 완료: {optimizeResult.StrategyConfiguration}");
                            AddLog($"ℹ️ {optimizeResult.Message}");
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUI(() =>
                        {
                            AddLog($"❌ 최적화 실패: {ex.Message}");
                            MessageBox.Show(
                                $"최적화 실행 중 오류:\n\n{ex.Message}",
                                "최적화 오류",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            });

            RecollectDataCommand = new RelayCommand(_ =>
            {
                if (SelectedSymbol == null || string.IsNullOrWhiteSpace(SelectedSymbol.Symbol))
                {
                    MessageBox.Show(
                        "재수집할 종목을 먼저 선택해주세요.\n(시장 그리드에서 행 더블클릭)",
                        "종목 선택 필요",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var symbol = SelectedSymbol.Symbol;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var service = new BacktestService();
                        RunOnUI(() => AddLog($"🔄 {symbol} 최근 30일 데이터 재수집 시작..."));

                        int count = await service.RecollectRecentCandleDataAsync(symbol, 30);

                        RunOnUI(() =>
                        {
                            AddLog($"✅ {symbol} 재수집 완료: {count}개 캔들 저장");
                            MessageBox.Show(
                                $"{symbol} 재수집 완료\n저장 건수: {count}개\n\n이제 BACKTEST/OPTIMIZE를 다시 실행하세요.",
                                "재수집 완료",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUI(() =>
                        {
                            AddLog($"❌ 데이터 재수집 실패: {ex.Message}");
                            MessageBox.Show(
                                $"데이터 재수집 중 오류:\n\n{ex.Message}",
                                "재수집 오류",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            });

            // [Agent 3] 매매 이력 내보내기 커맨드
            ExportHistoryCommand = new RelayCommand(async _ =>
            {
                try
                {
                    var sfd = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "CSV File (*.csv)|*.csv",
                        FileName = $"TradeHistory_{DateTime.Now:yyyyMMdd}.csv"
                    };

                    if (sfd.ShowDialog() == true)
                    {
                        var db = new DatabaseService();
                        await db.ExportTradeHistoryToCsvAsync(sfd.FileName);
                        AddLog($"✅ 매매 이력 저장 완료: {sfd.FileName}");
                    }
                }
                catch (Exception ex) { AddLog($"❌ 이력 저장 실패: {ex.Message}"); }
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
            BindingOperations.EnableCollectionSynchronization(MajorLogs, new object());
            BindingOperations.EnableCollectionSynchronization(PumpLogs, new object());
            BindingOperations.EnableCollectionSynchronization(Alerts, new object());
            BindingOperations.EnableCollectionSynchronization(TradeHistory, new object());
            BacktestFormatter = value => value.ToString("C0"); // 통화 형식 포맷터

            LiveChartXFormatter = val => new DateTime((long)val).ToString("HH:mm");
            LiveChartYFormatter = val => val.ToString("N4");
            PnLFormatter = value => value.ToString("F2") + "%";

            // [추가] RL 차트 초기화
            RLRewardSeries.Add(new LineSeries
            {
                Title = "Scalping Reward",
                Values = new ChartValues<double>(),
                PointGeometry = null,
                LineSmoothness = 0,
                Stroke = Brushes.Cyan,
                Fill = Brushes.Transparent
            });
            RLRewardSeries.Add(new LineSeries
            {
                Title = "Swing Reward",
                Values = new ChartValues<double>(),
                PointGeometry = null,
                LineSmoothness = 0,
                Stroke = Brushes.Orange,
                Fill = Brushes.Transparent
            });

            // [AI 모니터링] 예측 비교 차트 초기화
            PredictionComparisonSeries.Add(new LineSeries
            {
                Title = "Predicted Price",
                Values = new ChartValues<decimal>(),
                PointGeometry = DefaultGeometries.Circle,
                PointGeometrySize = 8,
                Stroke = Brushes.Cyan,
                Fill = Brushes.Transparent
            });
            PredictionComparisonSeries.Add(new LineSeries
            {
                Title = "Actual Price",
                Values = new ChartValues<decimal>(),
                PointGeometry = DefaultGeometries.Diamond,
                PointGeometrySize = 8,
                Stroke = Brushes.LimeGreen,
                Fill = Brushes.Transparent
            });

            // [AI 모니터링] 모델 성능 초기화
            BindingOperations.EnableCollectionSynchronization(ModelPerformances, new object());
            ModelPerformances.Add(new AIModelPerformance
            {
                ModelName = "ML.NET",
                StatusColor = Brushes.Gray
            });
            ModelPerformances.Add(new AIModelPerformance
            {
                ModelName = "Transformer",
                StatusColor = Brushes.Gray
            });

            // 초기 실행 시 이력 로드
            _ = LoadTradeHistory();
        }

        public async Task LoadTradeHistory()
        {
            try
            {
                var db = new DatabaseService();
                var start = StartDate.Date;
                var end = EndDate.Date.AddDays(1).AddTicks(-1);
                var historyModels = await db.GetTradeHistoryAsync(start, end, 5000);

                RunOnUI(() =>
                {
                    TradeHistory.Clear();
                    foreach (var model in historyModels)
                    {
                        // TradeHistoryModel -> TradeLog 변환
                        TradeHistory.Add(new TradeLog(model.Symbol, model.Side, model.ExitReason, model.ExitPrice, 0, model.ExitTime, model.PnL, model.PnLPercent));
                    }

                    // 통계 계산
                    CalculateTradeStatistics();
                });
                AddLog($"📜 매매 이력 로드 완료 ({historyModels.Count}건)");
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

        // [AI 모니터링] 예측 기록 추가
        public void AddPredictionRecord(AIPredictionRecord record)
        {
            RunOnUI(() =>
            {
                // 예측 vs 실제 차트 업데이트 (최근 20개만 유지)
                var predictedValues = PredictionComparisonSeries[0].Values as ChartValues<decimal>;
                var actualValues = PredictionComparisonSeries[1].Values as ChartValues<decimal>;

                if (predictedValues != null && actualValues != null)
                {
                    predictedValues.Add(record.PredictedPrice);
                    actualValues.Add(record.ActualPrice);

                    if (predictedValues.Count > 20) predictedValues.RemoveAt(0);
                    if (actualValues.Count > 20) actualValues.RemoveAt(0);
                }

                // 모델 성능 업데이트
                var model = ModelPerformances.FirstOrDefault(m => m.ModelName == record.ModelName);
                if (model != null)
                {
                    model.TotalPredictions++;
                    if (record.IsCorrect) model.CorrectPredictions++;

                    model.Accuracy = model.TotalPredictions > 0
                        ? (double)model.CorrectPredictions / model.TotalPredictions * 100
                        : 0;

                    // 색상 업데이트
                    if (model.Accuracy >= 60) model.StatusColor = Brushes.LimeGreen;
                    else if (model.Accuracy >= 50) model.StatusColor = Brushes.Orange;
                    else model.StatusColor = Brushes.Tomato;

                    // 실시간 정확도 업데이트
                    switch (record.ModelName)
                    {
                        case "ML.NET":
                            MLNetAccuracy = model.Accuracy;
                            break;
                        case "Transformer":
                            TransformerAccuracy = model.Accuracy;
                            break;
                    }
                }
            });
        }

        // [AI 모니터링] RL 보상 업데이트
        public void UpdateRLReward(double scalpingReward, double swingReward)
        {
            RunOnUI(() =>
            {
                var scalpingValues = RLRewardSeries[0].Values;
                var swingValues = RLRewardSeries[1].Values;

                scalpingValues.Add(scalpingReward);
                swingValues.Add(swingReward);

                // 최근 100개만 유지
                if (scalpingValues.Count > 100) scalpingValues.RemoveAt(0);
                if (swingValues.Count > 100) swingValues.RemoveAt(0);
            });
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

            // [추가] RL 통계 구독
            _engine.OnRLStatsUpdate += (modelName, scalpingReward, swingReward) =>
            {
                UpdateRLReward(scalpingReward, swingReward);
            };

            // [AI 모니터링] 예측 기록 구독
            _engine.OnAIPrediction += (record) =>
            {
                AddPredictionRecord(record);
            };
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
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                LiveLogs.Insert(0, line);
                if (LiveLogs.Count > 200) LiveLogs.RemoveAt(200);

                var isMajor = IsMajorSymbolLog(msg);
                var category = isMajor ? "MAJOR" : "PUMP";

                if (isMajor)
                {
                    MajorLogs.Insert(0, line);
                    if (MajorLogs.Count > 200) MajorLogs.RemoveAt(200);
                }
                else
                {
                    PumpLogs.Insert(0, line);
                    if (PumpLogs.Count > 200) PumpLogs.RemoveAt(200);
                }

                // DB에 비동기로 저장 (Fire-and-Forget)
                if (_dbService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 심볼 추출 (XXXUSDT 패턴)
                            var symbolMatch = System.Text.RegularExpressions.Regex.Match(msg, @"\b([A-Z]+USDT)\b");
                            var symbol = symbolMatch.Success ? symbolMatch.Groups[1].Value : null;

                            await _dbService.SaveLiveLogAsync(category, msg, symbol);
                        }
                        catch { /* 로그 저장 실패 시 무시 */ }
                    });
                }
            });
        }

        private static bool IsMajorSymbolLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return false;

            var msgUpper = msg.ToUpperInvariant();

            // 정확한 패턴 매칭: XXXUSDT 형식에서 메이저 코인만 추출
            return msgUpper.Contains("BTCUSDT") ||
                   msgUpper.Contains("ETHUSDT") ||
                   msgUpper.Contains("SOLUSDT") ||
                   msgUpper.Contains("XRPUSDT");
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
                    if (signal.AIScore > 0)
                    {
                        existing.AIScore = signal.AIScore;
                        existing.TouchAIScoreUpdatedAt();
                    }
                    if (!string.IsNullOrEmpty(signal.Decision)) existing.Decision = signal.Decision;
                    if (!string.IsNullOrEmpty(signal.BBPosition)) existing.BBPosition = signal.BBPosition;
                    if (!string.IsNullOrEmpty(signal.SignalSource)) existing.SignalSource = signal.SignalSource;
                    existing.ShortLongScore = signal.ShortLongScore;
                    existing.ShortShortScore = signal.ShortShortScore;
                    existing.MacdHist = signal.MacdHist;
                    if (!string.IsNullOrEmpty(signal.ElliottTrend)) existing.ElliottTrend = signal.ElliottTrend;
                    if (!string.IsNullOrEmpty(signal.MAState)) existing.MAState = signal.MAState;
                    if (!string.IsNullOrEmpty(signal.FibPosition)) existing.FibPosition = signal.FibPosition;
                    existing.VolumeRatioValue = signal.VolumeRatioValue;
                    if (!string.IsNullOrEmpty(signal.VolumeRatio)) existing.VolumeRatio = signal.VolumeRatio;
                    if (signal.IsPositionActive) existing.IsPositionActive = true;
                    if (signal.EntryPrice > 0) existing.EntryPrice = signal.EntryPrice;
                    if (!string.IsNullOrEmpty(signal.PositionSide)) existing.PositionSide = signal.PositionSide;
                    if (signal.Quantity > 0) existing.Quantity = signal.Quantity;
                    if (signal.Leverage > 0) existing.Leverage = signal.Leverage;

                    // [Phase 7] Transformer 예측값 업데이트
                    if (signal.TransformerPrice > 0) existing.TransformerPrice = signal.TransformerPrice;
                    if (signal.TransformerChange != 0) existing.TransformerChange = signal.TransformerChange;

                    // Trigger animation via event or property if needed. 
                    // For now, we rely on property changes.
                }
                else
                {
                    if (signal.AIScore > 0)
                        signal.TouchAIScoreUpdatedAt();

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
                var symbol = SelectedSymbol.Symbol;
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    LiveChartSeries = new SeriesCollection();
                    OnPropertyChanged(nameof(IsLiveChartEmpty));
                    return;
                }

                using var client = new BinanceRestClient();
                var klinesResult = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    limit: 20 // 최근 20개 캔들 (요청사항)
                );

                if (!klinesResult.Success) return;

                var closeValues = new ChartValues<double>();
                var xLabels = new List<string>();
                foreach (var kline in klinesResult.Data)
                {
                    closeValues.Add((double)kline.ClosePrice);
                    xLabels.Add(kline.OpenTime.ToLocalTime().ToString("HH:mm"));
                }

                LiveChartXFormatter = val =>
                {
                    int index = (int)Math.Round(val);
                    return (index >= 0 && index < xLabels.Count) ? xLabels[index] : string.Empty;
                };

                LiveChartSeries = new SeriesCollection
                {
                    new LineSeries
                    {
                        Title = $"{symbol} Close",
                        Values = closeValues,
                        PointGeometry = null,
                        LineSmoothness = 0,
                        Stroke = Brushes.DeepSkyBlue,
                        Fill = Brushes.Transparent
                    },
                    new ScatterSeries { Title = "Buy", Values = new ChartValues<ScatterPoint>(), PointGeometry = DefaultGeometries.Triangle, Fill = Brushes.LimeGreen, MinPointShapeDiameter = 15, DataLabels = false },
                    new ScatterSeries { Title = "Sell", Values = new ChartValues<ScatterPoint>(), PointGeometry = DefaultGeometries.Triangle, Fill = Brushes.Red, MinPointShapeDiameter = 15, DataLabels = false }
                };
                OnPropertyChanged(nameof(LiveChartSeries));
                OnPropertyChanged(nameof(LiveChartXFormatter));
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
                if (result == null || result.EquityCurve == null || result.EquityCurve.Count == 0)
                {
                    BacktestSeries = new SeriesCollection();
                    BacktestLabels = Array.Empty<string>();
                    return;
                }

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

                    if (index >= 0 && index < result.EquityCurve.Count)
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
                    // API Key가 설정되어 있을 때만 자격 증명 추가
                    if (!string.IsNullOrWhiteSpace(AppConfig.BinanceApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BinanceApiSecret))
                    {
                        options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                    }
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
                var candles = klines.Data.Select(k => new TradingBot.Models.CandleData
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
                    Candles = candles.ToList(),
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
