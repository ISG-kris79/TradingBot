using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Services.BacktestStrategies;
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
        private static readonly Regex SymbolRegex = new(@"\b([A-Z]+USDT)\b", RegexOptions.Compiled);
        private static readonly Regex GateThresholdRegex = new(@"mlTh=(?<ml>\d+(?:\.\d+)?)%\s+tfTh=(?<tf>\d+(?:\.\d+)?)%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GateAutoTuneRegex = new(@"mlThr=(?<oldMl>\d+(?:\.\d+)?)%->(?<newMl>\d+(?:\.\d+)?)%.*tfThr=(?<oldTf>\d+(?:\.\d+)?)%->(?<newTf>\d+(?:\.\d+)?)%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly ConcurrentQueue<BufferedLiveLog> _pendingLiveLogs = new();
        private readonly ConcurrentQueue<(string Category, string Message, string? Symbol)> _pendingLiveLogDbWrites = new();
        private readonly ConcurrentDictionary<string, (decimal Price, double? Pnl)> _pendingTickerUpdates = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _liveLogFlushTimer;
        private DispatcherTimer? _tickerFlushTimer;
        private int _liveLogDbDrainRunning;
        private const int MaxBufferedLiveLogs = 1200;
        private const int MaxUiLiveLogCount = 200;
        private const int MaxLiveLogBatchPerTick = 48;
        private const int MaxTickerBatchPerTick = 180;
        private const int MaxDbWritesPerDrain = 80;
        private bool _liveLogPerfEnabled = true;
        private bool _liveLogAutoTuneEnabled = true;
        private int _liveLogBaseWarnMs = 40;
        private int _liveLogBasePerfLogIntervalSec = 10;
        private int _liveLogFlushWarnMs = 40;
        private int _liveLogPerfLogIntervalSec = 10;
        private int _liveLogWarnMinMs = 20;
        private int _liveLogWarnMaxMs = 250;
        private int _liveLogPerfLogIntervalMinSec = 5;
        private int _liveLogPerfLogIntervalMaxSec = 60;
        private int _liveLogTuneSampleWindow = 60;
        private int _liveLogTuneMinIntervalSec = 30;
        private readonly Queue<int> _liveLogRecentFlushSamples = new();
        private DateTime _nextLiveLogPerfLogTime = DateTime.UtcNow.AddSeconds(10);
        private DateTime _nextLiveLogTuneTime = DateTime.UtcNow.AddSeconds(30);
        private DateTime _nextLiveLogWarnLogTime = DateTime.MinValue;
        private long _liveLogFlushTotalMs;
        private int _liveLogFlushSamples;
        private int _liveLogFlushMaxMs;
        private int _liveLogQueueHighWater;
        private int _liveLogDbQueueHighWater;

        private readonly record struct BufferedLiveLog(DateTime Timestamp, string Message, string RawMessage, string Category, bool IsMajor, string? Symbol);

        // TradingEngine 속성 노출
        public decimal InitialBalance => _engine?.InitialBalance ?? 0;
        // 데이터 컬렉션
        public ObservableCollection<MultiTimeframeViewModel> MarketDataList { get; set; } = new ObservableCollection<MultiTimeframeViewModel>();
        public ChartValues<double> ProfitHistory { get; set; } = new ChartValues<double>();
        public ObservableCollection<string> LiveLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> TradeLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> MajorLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> PumpLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> GateLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> Alerts { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> DbFailureAlerts { get; } = new ObservableCollection<string>();
        public ObservableCollection<TradeLog> TradeHistory { get; set; } = new ObservableCollection<TradeLog>();

        private int _gatePassCount;
        public int GatePassCount
        {
            get => _gatePassCount;
            set { _gatePassCount = value; OnPropertyChanged(); }
        }

        private int _gateBlockCount;
        public int GateBlockCount
        {
            get => _gateBlockCount;
            set { _gateBlockCount = value; OnPropertyChanged(); }
        }

        private string _gateThresholdSummaryText = "ML 55.0% / TF 52.0%";
        public string GateThresholdSummaryText
        {
            get => _gateThresholdSummaryText;
            set { _gateThresholdSummaryText = value; OnPropertyChanged(); }
        }

        private string _gateAutoTuneStatusText = "AUTO_TUNE 대기";
        public string GateAutoTuneStatusText
        {
            get => _gateAutoTuneStatusText;
            set { _gateAutoTuneStatusText = value; OnPropertyChanged(); }
        }

        private int _dbFailureAlertCount;
        public int DbFailureAlertCount
        {
            get => _dbFailureAlertCount;
            set { _dbFailureAlertCount = value; OnPropertyChanged(); }
        }

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

        private string _totalPositionInfo = "Active: 0 / 0";
        public string TotalPositionInfo { get => _totalPositionInfo; set { _totalPositionInfo = value; OnPropertyChanged(); } }

        // 하단 상태바 및 진행률
        private string _footerText = "Ready";
        public string FooterText { get => _footerText; set { _footerText = value; OnPropertyChanged(); } }

        private string _majorProfileStatusText = "● Major Profile: Balanced";
        public string MajorProfileStatusText
        {
            get => _majorProfileStatusText;
            set { _majorProfileStatusText = value; OnPropertyChanged(); }
        }

        private Brush _majorProfileStatusColor = Brushes.LightGray;
        public Brush MajorProfileStatusColor
        {
            get => _majorProfileStatusColor;
            set { _majorProfileStatusColor = value; OnPropertyChanged(); }
        }

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

        private double _activePnLAxisMin = -1d;
        public double ActivePnLAxisMin { get => _activePnLAxisMin; set { _activePnLAxisMin = value; OnPropertyChanged(); } }

        private double _activePnLAxisMax = 1d;
        public double ActivePnLAxisMax { get => _activePnLAxisMax; set { _activePnLAxisMax = value; OnPropertyChanged(); } }

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

        private double _liveChartAxisMin = 0d;
        public double LiveChartAxisMin { get => _liveChartAxisMin; set { _liveChartAxisMin = value; OnPropertyChanged(); } }

        private double _liveChartAxisMax = 1d;
        public double LiveChartAxisMax { get => _liveChartAxisMax; set { _liveChartAxisMax = value; OnPropertyChanged(); } }

        // [추가] RL 보상 차트 데이터
        private SeriesCollection _rlRewardSeries = new SeriesCollection();
        public SeriesCollection RLRewardSeries { get => _rlRewardSeries; set { _rlRewardSeries = value; OnPropertyChanged(); } }

        // [AI 모니터링] 예측 정확도 추적
        public ObservableCollection<AIModelPerformance> ModelPerformances { get; set; } = new ObservableCollection<AIModelPerformance>();
        private const double AccuracyTargetPercent = 65.0;
        private const double AccuracyWarnPercent = 55.0;
        private const float AccuracyConfidenceFloor = 0.60f;

        // [AI 모니터링] 예측 vs 실제 비교 차트
        private SeriesCollection _predictionComparisonSeries = new SeriesCollection();
        public SeriesCollection PredictionComparisonSeries
        {
            get => _predictionComparisonSeries;
            set { _predictionComparisonSeries = value; OnPropertyChanged(); }
        }

        public string[] PredictionLabels { get; set; } = Array.Empty<string>();

        private double _predictionChartAxisMin = 0d;
        public double PredictionChartAxisMin { get => _predictionChartAxisMin; set { _predictionChartAxisMin = value; OnPropertyChanged(); } }

        private double _predictionChartAxisMax = 1d;
        public double PredictionChartAxisMax { get => _predictionChartAxisMax; set { _predictionChartAxisMax = value; OnPropertyChanged(); } }

        // [AI 모니터링] 실시간 정확도
        private double _mlNetAccuracy = 0.0;
        public double MLNetAccuracy { get => _mlNetAccuracy; set { _mlNetAccuracy = value; OnPropertyChanged(); } }

        private double _transformerAccuracy = 0.0;
        public double TransformerAccuracy { get => _transformerAccuracy; set { _transformerAccuracy = value; OnPropertyChanged(); } }

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
                    _ = LoadLiveChartDataAsync();
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
        public ICommand SyncPositionsCommand { get; private set; } // [FIX] 거래소 포지션 동기화

        // Command Properties for Button State
        private bool _isStartEnabled = true;
        public bool IsStartEnabled { get => _isStartEnabled; set { _isStartEnabled = value; OnPropertyChanged(); } }

        private bool _isStopEnabled = false;
        public bool IsStopEnabled { get => _isStopEnabled; set { _isStopEnabled = value; OnPropertyChanged(); } }

        public MainViewModel()
        {
            UpdateMajorProfileStatus(AppConfig.Current?.Trading?.GeneralSettings?.MajorTrendProfile);
            ApplyLiveLogPerformanceSettings();
            InitializeLiveLogPipeline();
            InitializeTickerUpdatePipeline();

            // Initialize services
            if (!DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            {
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

                try
                {
                    // 최신 설정(손절/익절/시뮬레이션/거래소)을 반영하기 위해 시작 시마다 엔진을 새로 생성
                    _engine?.StopEngine();
                    _engine = new TradingEngine();
                    SubscribeToEngineEvents();
                    OnPropertyChanged(nameof(InitialBalance));
                }
                catch (Exception ex)
                {
                    AddLog($"엔진 초기화 오류: {ex.Message}");
                    IsStartEnabled = true;
                    IsStopEnabled = false;
                    return;
                }

                // 시뮬레이션 모드 표시 및 확인
                bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                decimal simBalance = AppConfig.Current?.Trading?.SimulationInitialBalance ?? 10000m;
                
                RunOnUI(() =>
                {
                    if (MainWindow.Instance != null)
                    {
                        var txtAccountMode = MainWindow.Instance.FindName("txtAccountMode") as System.Windows.Controls.TextBlock;
                        if (txtAccountMode != null)
                        {
                            txtAccountMode.Text = isSimulation ? "🎮 SIMULATION" : "";
                        }
                    }
                    
                    // 시작 시 현재 모드 로그 출력
                    if (isSimulation)
                    {
                        AddLog($"🎮 [Start] 시뮬레이션 모드로 시작합니다. 초기 잔고: ${simBalance:N2}");
                    }
                    else
                    {
                        AddLog($"💰 [Start] 실거래 모드로 시작합니다.");
                    }
                });

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
                    {
                        UnsubscribeFromEngineEvents();
                        _engine.StopEngine();
                        _engine = null;
                        OnPropertyChanged(nameof(InitialBalance));
                    }
                    IsStopEnabled = false;
                    IsStartEnabled = true;
                    AddLog("엔진 정지 명령을 보냈습니다.");
                    
                    // 시뮬레이션 모드 표시 제거
                    RunOnUI(() =>
                    {
                        if (MainWindow.Instance != null)
                        {
                            var txtAccountMode = MainWindow.Instance.FindName("txtAccountMode") as System.Windows.Controls.TextBlock;
                            if (txtAccountMode != null)
                            {
                                txtAccountMode.Text = "";
                            }
                        }
                    });
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

                AddLog("📝 백테스트 옵션 다이얼로그 표시 중...");

                // 옵션 다이얼로그 표시 (Owner 설정)
                var optionsDialog = new BacktestOptionsDialog
                {
                    Owner = Application.Current.MainWindow
                };
                
                var dialogResult = optionsDialog.ShowDialog();
                
                AddLog($"📝 다이얼로그 결과: {dialogResult}, IsConfirmed: {optionsDialog.IsConfirmed}");

                if (dialogResult != true || !optionsDialog.IsConfirmed)
                {
                    AddLog("ℹ️ 백테스트가 취소되었습니다");
                    return;
                }

                var selectedStrategy = optionsDialog.SelectedStrategy;
                var selectedDays = optionsDialog.SelectedDays;
                var selectedMetricOptions = optionsDialog.SelectedMetricOptions;
                var strategyName = selectedStrategy.ToString();

                AddLog($"🔄 {symbol} Backtest starting ({selectedDays}일, {strategyName} 전략)...");
                AddLog($"📐 지표 기준: RF {selectedMetricOptions.RiskFreeRateAnnualPct:F2}% | {selectedMetricOptions.AnnualizationMode}");
                
                // 진행 상태 초기화 (이미 UI 스레드)
                FooterText = $"📋 BACKTEST 실행 중: {symbol} | {strategyName} | {selectedDays}일";
                ScanProgress = 10;
                ScanProgressColor = new SolidColorBrush(Color.FromRgb(0, 229, 255)); // Cyan

                // UI 블로킹 방지: 백그라운드 실행 (fire-and-forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var service = new BacktestService();
                        var endDate = DateTime.Now;
                        var startDate = endDate.AddDays(-selectedDays);

                        RunOnUI(() => 
                        {
                            AddLog($"📅 데이터 조회: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");
                            ScanProgress = 30;
                        });

                        BacktestResult result = await service.RunBacktestAsync(symbol, startDate, endDate, selectedStrategy, 1000m, selectedMetricOptions);

                        if ((result?.Candles?.Count ?? 0) > 0 && (result?.TotalTrades ?? 0) == 0)
                        {
                            RunOnUI(() => AddLog("ℹ️ 체결 0회로 RSI(40/60) 대체 백테스트를 자동 시도합니다."));
                            var fallbackStrategy = new RsiBacktestStrategy
                            {
                                RsiPeriod = 14,
                                BuyThreshold = 40,
                                SellThreshold = 60
                            };

                            var fallbackResult = await service.RunBacktestAsync(symbol, startDate, endDate, fallbackStrategy, 1000m, selectedMetricOptions);
                            if (fallbackResult != null && fallbackResult.TotalTrades > 0)
                            {
                                fallbackResult.Message = "[자동 대체 실행] 선택 전략 체결이 없어 RSI(40/60) 결과를 표시합니다. | " + (fallbackResult.Message ?? string.Empty);
                                result = fallbackResult;
                            }
                        }

                        if (result == null)
                        {
                            RunOnUI(() => 
                            {
                                AddLog($"❌ 백테스트 결과가 null입니다.");
                                FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                                ScanProgress = 0;
                            });
                            return;
                        }

                        RunOnUI(() => ScanProgress = 60);

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
                                RunOnUI(() => ScanProgress = 40);
                                int recollectedCount = await service.RecollectRecentCandleDataAsync(symbol, 30);
                                RunOnUI(() => 
                                {
                                    AddLog($"✅ {symbol} 재수집 완료: {recollectedCount}개");
                                    ScanProgress = 50;
                                });

                                result = await service.RunBacktestAsync(symbol, startDate, endDate, selectedStrategy, 1000m, selectedMetricOptions);
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
                                
                                FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                                ScanProgress = 100;
                                
                                // 진행률 리셋
                                Task.Delay(1000).ContinueWith(_ => RunOnUI(() => ScanProgress = 0));
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
                            FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                            ScanProgress = 0;
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
                            FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                            ScanProgress = 0;
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

                AddLog("📝 최적화 옵션 다이얼로그 표시 중...");

                // 옵션 다이얼로그 표시 (Owner 설정)
                var optionsDialog = new OptimizeOptionsDialog
                {
                    Owner = Application.Current.MainWindow
                };
                
                var dialogResult = optionsDialog.ShowDialog();
                
                AddLog($"📝 다이얼로그 결과: {dialogResult}, IsConfirmed: {optionsDialog.IsConfirmed}");

                if (dialogResult != true || !optionsDialog.IsConfirmed)
                {
                    AddLog("ℹ️ 최적화가 취소되었습니다");
                    return;
                }

                var selectedStrategy = optionsDialog.SelectedStrategy;
                var selectedDays = optionsDialog.SelectedDays;
                var selectedTrials = optionsDialog.SelectedTrials;
                var strategyName = selectedStrategy.ToString();

                AddLog($"⚙ {symbol} 최적화 시작 ({selectedDays}일, {strategyName}, Optuna {selectedTrials}회)...");
                
                // 진행 상태 초기화 (이미 UI 스레드)
                FooterText = $"⚙️ OPTIMIZE 실행 중: {symbol} | {strategyName} | {selectedDays}일 | {selectedTrials}회";
                ScanProgress = 10;
                ScanProgressColor = new SolidColorBrush(Color.FromRgb(124, 77, 255)); // Purple

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var service = new BacktestService();
                        var endDate = DateTime.Now;
                        var startDate = endDate.AddDays(-selectedDays);

                        RunOnUI(() => ScanProgress = 20);

                        // 데이터 존재 확인
                        var precheck = await service.RunBacktestAsync(symbol, startDate, endDate, selectedStrategy);
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
                                RunOnUI(() => 
                                {
                                    AddLog($"🔄 {symbol} 최적화용 데이터 재수집 시작...");
                                    ScanProgress = 30;
                                });
                                int recollectedCount = await service.RecollectRecentCandleDataAsync(symbol, selectedDays);
                                RunOnUI(() => 
                                {
                                    AddLog($"✅ {symbol} 재수집 완료: {recollectedCount}개");
                                    ScanProgress = 40;
                                });

                                precheck = await service.RunBacktestAsync(symbol, startDate, endDate, selectedStrategy);
                            }
                        }

                        if (precheck.Candles == null || precheck.Candles.Count == 0)
                        {
                            RunOnUI(() => 
                            {
                                FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                                ScanProgress = 0;
                                MessageBox.Show(
                                $"'{symbol}' 데이터가 부족해 최적화를 실행할 수 없습니다.",
                                "최적화 실패",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            });
                            return;
                        }

                        RunOnUI(() => 
                        {
                            AddLog($"⏳ Optuna 최적화 진행 중 ({selectedTrials}회 시도)...");
                            ScanProgress = 50;
                        });
                        
                        var optimizeResult = await service.OptimizeWithOptunaAsync(symbol, startDate, endDate, 1000m, selectedTrials);
                        if (optimizeResult == null)
                        {
                            RunOnUI(() => 
                            {
                                AddLog("❌ 최적화 결과가 null입니다.");
                                FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                                ScanProgress = 0;
                            });
                            return;
                        }

                        var finalOptimizeResult = optimizeResult;

                        finalOptimizeResult.Symbol = symbol;
                        if (finalOptimizeResult.Candles == null || finalOptimizeResult.Candles.Count == 0)
                            finalOptimizeResult.Candles = precheck.Candles;
                        if (finalOptimizeResult.FinalBalance <= 0)
                            finalOptimizeResult.FinalBalance = finalOptimizeResult.InitialBalance;

                        if ((finalOptimizeResult.Candles?.Count ?? 0) > 0 && finalOptimizeResult.TotalTrades == 0)
                        {
                            RunOnUI(() => AddLog("ℹ️ 최적화 결과 체결 0회로 RSI(40/60) 대체 백테스트를 자동 시도합니다."));
                            var fallbackStrategy = new RsiBacktestStrategy
                            {
                                RsiPeriod = 14,
                                BuyThreshold = 40,
                                SellThreshold = 60
                            };

                            var fallbackResult = await service.RunBacktestAsync(symbol, startDate, endDate, fallbackStrategy, 1000m);
                            if (fallbackResult != null && fallbackResult.TotalTrades > 0)
                            {
                                fallbackResult.TopTrials = finalOptimizeResult.TopTrials;
                                fallbackResult.Symbol = symbol;
                                fallbackResult.Message = "[자동 대체 실행] 최적화 결과 체결이 없어 RSI(40/60) 결과를 표시합니다. | " + (fallbackResult.Message ?? string.Empty);
                                fallbackResult.StrategyConfiguration = $"{finalOptimizeResult.StrategyConfiguration ?? string.Empty} | Fallback RSI(40/60)";
                                finalOptimizeResult = fallbackResult;
                            }
                        }

                        RunOnUI(() => 
                        {
                            ScanProgress = 90;
                            var window = new BacktestWindow(finalOptimizeResult, $"⚙ OPTIMIZE - {symbol}");
                            window.Show();
                            AddLog($"✅ 최적화 완료: {finalOptimizeResult.StrategyConfiguration ?? string.Empty}");
                            AddLog($"ℹ️ {finalOptimizeResult.Message ?? string.Empty}");
                            
                            FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                            ScanProgress = 100;
                            
                            // 진행률 리셋
                            Task.Delay(1000).ContinueWith(_ => RunOnUI(() => ScanProgress = 0));
                        });
                    }
                    catch (Exception ex)
                    {
                        RunOnUI(() =>
                        {
                            AddLog($"❌ 최적화 실패: {ex.Message}");
                            FooterText = "SYSTEM READY  •  WAITING FOR COMMAND";
                            ScanProgress = 0;
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
                        int userId = AppConfig.CurrentUser?.Id ?? 0;
                        if (userId <= 0)
                        {
                            AddLog("⚠️ 로그인 사용자 ID를 확인할 수 없어 매매 이력을 내보낼 수 없습니다.");
                            return;
                        }

                        var db = new DbManager(AppConfig.ConnectionString);
                        var start = StartDate.Date;
                        var end = EndDate.Date.AddDays(1).AddTicks(-1);
                        await db.ExportTradeHistoryToCsvAsync(sfd.FileName, userId, start, end);
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
                        {
                            await _engine.ClosePositionAsync(symbol);
                            AddLog($"⚡ {symbol} 수동 청산 명령 전송됨");
                            // [FIX] 청산 후 TradeHistory 즉시 갱신
                            await LoadTradeHistory();
                        }
                        else
                        {
                            AddLog($"⚠️ {symbol} 수동 청산 실패: 엔진이 실행 중이 아닙니다.");
                        }
                    }
                }
            });

            // [FIX] 거래소 포지션 동기화 명령
            SyncPositionsCommand = new RelayCommand(async _ =>
            {
                if (_engine != null)
                {
                    AddLog("🔄 거래소 포지션 동기화 시작...");
                    await _engine.SyncExchangePositionsAsync();
                    AddLog("✅ 거래소 포지션 동기화 완료");
                    await LoadTradeHistory();
                }
                else
                {
                    AddLog("⚠️ 거래소 동기화 실패: 엔진이 실행 중이 아닙니다.");
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
            BindingOperations.EnableCollectionSynchronization(TradeLogs, new object());
            BindingOperations.EnableCollectionSynchronization(MajorLogs, new object());
            BindingOperations.EnableCollectionSynchronization(PumpLogs, new object());
            BindingOperations.EnableCollectionSynchronization(GateLogs, new object());
            BindingOperations.EnableCollectionSynchronization(Alerts, new object());
            BindingOperations.EnableCollectionSynchronization(DbFailureAlerts, new object());
            BindingOperations.EnableCollectionSynchronization(TradeHistory, new object());
            BacktestFormatter = value => value.ToString("C0"); // 통화 형식 포맷터

            Alerts.CollectionChanged += Alerts_CollectionChanged;
            RefreshDbFailureAlertMetrics();

            LiveChartXFormatter = val => new DateTime((long)val).ToString("HH:mm");
            LiveChartYFormatter = val => val.ToString("N4");
            PnLFormatter = value => value.ToString("F2") + "%";

            ResetRLRewardSeries();
            ResetPredictionComparisonSeries();

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

        public void UpdateMajorProfileStatus(string? profile)
        {
            bool isAggressive = string.Equals(profile, "Aggressive", StringComparison.OrdinalIgnoreCase);

            MajorProfileStatusText = isAggressive
                ? "⚡ Major Profile: Aggressive"
                : "● Major Profile: Balanced";
            MajorProfileStatusColor = isAggressive ? Brushes.Orange : Brushes.LightGray;
        }

        public async Task LoadTradeHistory()
        {
            try
            {
                int userId = AppConfig.CurrentUser?.Id ?? 0;
                if (userId <= 0)
                {
                    AddLog("⚠️ 로그인 사용자 ID를 확인할 수 없어 매매 이력을 로드할 수 없습니다.");
                    return;
                }

                var db = new DbManager(AppConfig.ConnectionString);
                var start = StartDate.Date;
                var end = EndDate.Date.AddDays(1).AddTicks(-1);
                var historyModels = await db.GetTradeHistoryAsync(userId, start, end, 5000);

                RunOnUI(() =>
                {
                    TradeHistory.Clear();
                    foreach (var model in historyModels)
                    {
                        TradeHistory.Add(model);
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

            var closedTrades = TradeHistory
                .Where(t => !string.Equals(t.ExitReason, "OPEN_POSITION", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (closedTrades.Count == 0)
            {
                WinRate = 0;
                TotalProfit = 0;
                AverageRoe = 0;
                return;
            }

            var profitableTrades = closedTrades.Where(t => t.PnL > 0).ToList();
            WinRate = (double)profitableTrades.Count / closedTrades.Count * 100;
            TotalProfit = (double)closedTrades.Sum(t => t.PnL);
            AverageRoe = (double)closedTrades.Average(t => t.PnLPercent);
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
                string normalizedModelName = NormalizeModelName(record.ModelName);

                // 예측 vs 실제 차트 업데이트 (최근 20개만 유지)
                // IndexOutOfRangeException 방지: Count 체크 추가
                if (PredictionComparisonSeries.Count < 2)
                {
                    AddLog("⚠️ PredictionComparisonSeries가 초기화되지 않았습니다.");
                    return;
                }

                var predictedValues = PredictionComparisonSeries[0].Values as ChartValues<double>;
                var actualValues = PredictionComparisonSeries[1].Values as ChartValues<double>;

                if (predictedValues != null && actualValues != null)
                {
                    decimal? previousPredicted = predictedValues.Count > 0 ? (decimal)predictedValues[^1] : null;
                    decimal? previousActual = actualValues.Count > 0 ? (decimal)actualValues[^1] : null;
                    var safePredicted = SanitizeChartPrice(record.PredictedPrice, previousPredicted);
                    var safeActual = SanitizeChartPrice(record.ActualPrice, previousActual);

                    if (!safePredicted.HasValue || !safeActual.HasValue)
                    {
                        AddLog($"⚠️ 예측 차트 업데이트 건너뜀: Predicted={record.PredictedPrice}, Actual={record.ActualPrice}");
                        return;
                    }

                    predictedValues.Add((double)safePredicted.Value);
                    actualValues.Add((double)safeActual.Value);

                    if (predictedValues.Count > 20) predictedValues.RemoveAt(0);
                    if (actualValues.Count > 20) actualValues.RemoveAt(0);

                    UpdateAxisRange(
                        predictedValues.Concat(actualValues),
                        min => PredictionChartAxisMin = min,
                        max => PredictionChartAxisMax = max,
                        defaultMin: 0d,
                        defaultMax: 1d,
                        requirePositive: true);
                }

                // 모델 성능 업데이트
                var model = ModelPerformances.FirstOrDefault(m => m.ModelName == normalizedModelName);
                if (model != null)
                {
                    if (record.Confidence < AccuracyConfidenceFloor)
                    {
                        return;
                    }

                    int previousTotal = model.TotalPredictions;
                    double previousConfidenceSum = model.AvgConfidence * previousTotal;
                    double confidencePercent = Math.Clamp((double)record.Confidence, 0.0, 1.0) * 100.0;

                    model.TotalPredictions++;
                    if (record.IsCorrect) model.CorrectPredictions++;

                    model.Accuracy = model.TotalPredictions > 0
                        ? (double)model.CorrectPredictions / model.TotalPredictions * 100
                        : 0;

                    model.AvgConfidence = model.TotalPredictions > 0
                        ? (previousConfidenceSum + confidencePercent) / model.TotalPredictions
                        : confidencePercent;

                    // 색상 업데이트
                    if (model.Accuracy >= AccuracyTargetPercent) model.StatusColor = Brushes.LimeGreen;
                    else if (model.Accuracy >= AccuracyWarnPercent) model.StatusColor = Brushes.Orange;
                    else model.StatusColor = Brushes.Tomato;

                    // 실시간 정확도 업데이트
                    switch (normalizedModelName)
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

        private static string NormalizeModelName(string modelName)
        {
            if (string.Equals(modelName, "MLNET", StringComparison.OrdinalIgnoreCase))
                return "ML.NET";

            if (string.Equals(modelName, "ML.NET", StringComparison.OrdinalIgnoreCase))
                return "ML.NET";

            if (string.Equals(modelName, "Transformer", StringComparison.OrdinalIgnoreCase))
                return "Transformer";

            return modelName;
        }

        // [AI 모니터링] RL 보상 업데이트
        public void UpdateRLReward(double scalpingReward, double swingReward)
        {
            RunOnUI(() =>
            {
                // IndexOutOfRangeException 방지: Count 체크 추가
                if (RLRewardSeries.Count < 2)
                {
                    AddLog("⚠️ RLRewardSeries가 초기화되지 않았습니다.");
                    return;
                }

                var scalpingValues = RLRewardSeries[0].Values as ChartValues<double>;
                var swingValues = RLRewardSeries[1].Values as ChartValues<double>;
                if (scalpingValues == null || swingValues == null)
                    return;

                var safeScalping = ToFinite(scalpingReward, scalpingValues.Count > 0 ? scalpingValues[^1] : double.NaN);
                var safeSwing = ToFinite(swingReward, swingValues.Count > 0 ? swingValues[^1] : double.NaN);

                if (!IsFinite(safeScalping) || !IsFinite(safeSwing))
                {
                    AddLog($"⚠️ RL 보상 차트 업데이트 건너뜀: Scalping={scalpingReward}, Swing={swingReward}");
                    return;
                }

                scalpingValues.Add(safeScalping);
                swingValues.Add(safeSwing);

                // 최근 100개만 유지
                if (scalpingValues.Count > 100) scalpingValues.RemoveAt(0);
                if (swingValues.Count > 100) swingValues.RemoveAt(0);
            });
        }

        private void SubscribeToEngineEvents()
        {
            if (_engine == null) return;
            // 기존 구독 해제 후 재구독 (이중 구독 방지)
            UnsubscribeFromEngineEvents();
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
            _engine.OnCloseIncompleteStatusChanged += (symbol, isIncomplete, detail) => UpdateCloseIncompleteStatus(symbol, isIncomplete, detail);
            _engine.OnExternalSyncStatusChanged += (symbol, status, detail) => UpdateExternalSyncStatus(symbol, status, detail);
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

            // [FIX] 청산 시 TradeHistory 자동 갱신
            _engine.OnTradeHistoryUpdated += () =>
            {
                _ = LoadTradeHistory();
            };
        }

        private void UnsubscribeFromEngineEvents()
        {
            _engine?.ClearEventSubscriptions();
        }

        private void HandleTradeExecuted(string symbol, string side, decimal price, decimal qty)
        {
            UpdateExternalSyncStatus(symbol, null, null);

            // 1. 알림 로그 추가
            AddAlert($"[거래 체결] {symbol} {side} | 가격: {price:F4} | 수량: {qty}");

            // 2. 실시간 차트에 마커 추가
            RunOnUI(() =>
            {
                if (SelectedSymbol != null && SelectedSymbol.Symbol == symbol && LiveChartSeries.Count > 2)
                {
                    // NaN/Infinity 검증 강화
                    var y = ToFinite((double)price);
                    if (!IsFinite(y) || y <= 0)
                    {
                        AddLog($"⚠️ 거래 차트 마커 추가 실패: 가격 값이 유효하지 않음 (price={price}, y={y})");
                        return;
                    }

                    var count = LiveChartSeries[0].Values?.Count ?? 0;
                    if (count == 0)
                    {
                        AddLog("⚠️ LiveChartSeries[0].Values가 비어있습니다.");
                        return;
                    }

                    var x = Math.Max(0, count - 1);
                    var point = new ScatterPoint(x, y, 10);

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
            if (string.IsNullOrWhiteSpace(msg))
                return;

            int pendingBefore = _pendingLiveLogs.Count;
            if (pendingBefore >= (int)(MaxBufferedLiveLogs * 0.8) && IsLiveLogNoise(msg))
                return;

            var localizedMessage = LocalizeLiveLogMessage(msg);
            var isMajor = IsMajorSymbolLog(msg);
            var category = DetermineLiveLogCategory(msg, isMajor);
            var symbolMatch = SymbolRegex.Match(msg);
            var symbol = symbolMatch.Success ? symbolMatch.Groups[1].Value : null;

            _pendingLiveLogs.Enqueue(new BufferedLiveLog(DateTime.Now, localizedMessage, msg, category, isMajor, symbol));
            int pendingQueue = _pendingLiveLogs.Count;
            if (pendingQueue > _liveLogQueueHighWater)
                _liveLogQueueHighWater = pendingQueue;

            while (_pendingLiveLogs.Count > MaxBufferedLiveLogs)
            {
                _pendingLiveLogs.TryDequeue(out _);
            }
        }

        private void InitializeLiveLogPipeline()
        {
            RunOnUI(() =>
            {
                if (_liveLogFlushTimer != null)
                    return;

                _liveLogFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(120)
                };
                _liveLogFlushTimer.Tick += (_, _) => FlushPendingLiveLogsToUi();
                _liveLogFlushTimer.Start();
            });
        }

        private void InitializeTickerUpdatePipeline()
        {
            RunOnUI(() =>
            {
                if (_tickerFlushTimer != null)
                    return;

                _tickerFlushTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(120)
                };
                _tickerFlushTimer.Tick += (_, _) => FlushPendingTickerUpdatesToUi();
                _tickerFlushTimer.Start();
            });
        }

        private void FlushPendingTickerUpdatesToUi()
        {
            if (_pendingTickerUpdates.IsEmpty)
                return;

            int processed = 0;
            foreach (var kvp in _pendingTickerUpdates.ToArray())
            {
                if (processed >= MaxTickerBatchPerTick)
                    break;

                if (!_pendingTickerUpdates.TryRemove(kvp.Key, out var payload))
                    continue;

                var existing = MarketDataList.FirstOrDefault(x => x.Symbol == kvp.Key);
                if (existing == null)
                {
                    existing = new MultiTimeframeViewModel { Symbol = kvp.Key };
                    MarketDataList.Add(existing);
                }

                if (payload.Price > 0)
                    existing.LastPrice = payload.Price;

                if (payload.Pnl.HasValue)
                {
                    var safePnl = ToFinite(payload.Pnl.Value, existing.ProfitPercent);
                    existing.ProfitPercent = safePnl;
                }

                processed++;
            }
        }

        private void FlushPendingLiveLogsToUi()
        {
            try
            {
                Stopwatch? flushWatch = _liveLogPerfEnabled ? Stopwatch.StartNew() : null;
                int pendingBefore = _pendingLiveLogs.Count;
                int dbQueueBefore = _pendingLiveLogDbWrites.Count;

                int processed = 0;
                while (processed < MaxLiveLogBatchPerTick && _pendingLiveLogs.TryDequeue(out var item))
                {
                    string displayMessage = SimplifyLiveLogMessage(item.Message);
                    var line = $"[{item.Timestamp:HH:mm:ss}] {displayMessage}";
                    LiveLogs.Insert(0, line);
                    if (LiveLogs.Count > MaxUiLiveLogCount) LiveLogs.RemoveAt(MaxUiLiveLogCount);

                    if (string.Equals(item.Category, "GATE", StringComparison.OrdinalIgnoreCase))
                    {
                        GateLogs.Insert(0, line);
                        if (GateLogs.Count > MaxUiLiveLogCount) GateLogs.RemoveAt(MaxUiLiveLogCount);
                        UpdateGateDashboardFromLog(item.RawMessage, line);
                    }
                    else
                    {
                        TradeLogs.Insert(0, line);
                        if (TradeLogs.Count > MaxUiLiveLogCount) TradeLogs.RemoveAt(MaxUiLiveLogCount);

                        if (item.IsMajor)
                        {
                            MajorLogs.Insert(0, line);
                            if (MajorLogs.Count > MaxUiLiveLogCount) MajorLogs.RemoveAt(MaxUiLiveLogCount);
                        }
                        else
                        {
                            PumpLogs.Insert(0, line);
                            if (PumpLogs.Count > MaxUiLiveLogCount) PumpLogs.RemoveAt(MaxUiLiveLogCount);
                        }
                    }

                    if (_dbService != null && ShouldPersistLiveLog(item.Message))
                    {
                        _pendingLiveLogDbWrites.Enqueue((item.Category, item.Message, item.Symbol));
                        int dbQueueLength = _pendingLiveLogDbWrites.Count;
                        if (dbQueueLength > _liveLogDbQueueHighWater)
                            _liveLogDbQueueHighWater = dbQueueLength;
                    }

                    processed++;
                }

                if (processed > 0 && _dbService != null && !_pendingLiveLogDbWrites.IsEmpty)
                {
                    _ = DrainLiveLogDbQueueAsync();
                }

                if (_liveLogPerfEnabled && flushWatch != null)
                {
                    flushWatch.Stop();
                    RecordLiveLogPerfMetrics(
                        processed,
                        pendingBefore,
                        _pendingLiveLogs.Count,
                        dbQueueBefore,
                        _pendingLiveLogDbWrites.Count,
                        (int)flushWatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                LoggerService.Error("LiveLog flush 오류", ex);
            }
        }

        private void RecordLiveLogPerfMetrics(
            int processed,
            int pendingBefore,
            int pendingAfter,
            int dbQueueBefore,
            int dbQueueAfter,
            int flushMs)
        {
            if (!_liveLogPerfEnabled)
                return;

            if (pendingBefore > _liveLogQueueHighWater) _liveLogQueueHighWater = pendingBefore;
            if (pendingAfter > _liveLogQueueHighWater) _liveLogQueueHighWater = pendingAfter;
            if (dbQueueBefore > _liveLogDbQueueHighWater) _liveLogDbQueueHighWater = dbQueueBefore;
            if (dbQueueAfter > _liveLogDbQueueHighWater) _liveLogDbQueueHighWater = dbQueueAfter;

            AutoTuneLiveLogPerfThresholds(flushMs);

            _liveLogFlushTotalMs += flushMs;
            _liveLogFlushSamples++;
            if (flushMs > _liveLogFlushMaxMs)
                _liveLogFlushMaxMs = flushMs;

            bool isWarn = flushMs >= _liveLogFlushWarnMs;
            bool isPeriodic = DateTime.UtcNow >= _nextLiveLogPerfLogTime;
            if (!isWarn && !isPeriodic)
                return;

            double avgMs = _liveLogFlushSamples > 0
                ? (double)_liveLogFlushTotalMs / _liveLogFlushSamples
                : 0;

            string level = isWarn ? "WARN" : "INFO";
            string perfMessage =
                $"[PERF][LIVELOG][{level}] flushMs={flushMs} avgMs={avgMs:F1} maxMs={_liveLogFlushMaxMs} " +
                $"processed={processed} queue={pendingBefore}->{pendingAfter} dbQueue={dbQueueBefore}->{dbQueueAfter} " +
                $"queueHwm={_liveLogQueueHighWater} dbQueueHwm={_liveLogDbQueueHighWater} " +
                $"warnMs={_liveLogFlushWarnMs} intervalSec={_liveLogPerfLogIntervalSec} autoTune={_liveLogAutoTuneEnabled}";

            LoggerService.Info(perfMessage);

            if (isWarn && DateTime.UtcNow >= _nextLiveLogWarnLogTime)
            {
                _nextLiveLogWarnLogTime = DateTime.UtcNow.AddSeconds(10);
                AddLog($"⚠️ [PERF][LIVELOG] flush {flushMs}ms, queue {pendingBefore}->{pendingAfter}");
            }

            if (isPeriodic)
            {
                _nextLiveLogPerfLogTime = DateTime.UtcNow.AddSeconds(_liveLogPerfLogIntervalSec);
                _liveLogFlushTotalMs = 0;
                _liveLogFlushSamples = 0;
                _liveLogFlushMaxMs = 0;
                _liveLogQueueHighWater = pendingAfter;
                _liveLogDbQueueHighWater = dbQueueAfter;
            }
        }

        private void ApplyLiveLogPerformanceSettings()
        {
            var settings = AppConfig.Current?.Trading?.PerformanceMonitoring;
            if (settings == null)
            {
                _nextLiveLogPerfLogTime = DateTime.UtcNow.AddSeconds(_liveLogPerfLogIntervalSec);
                _nextLiveLogTuneTime = DateTime.UtcNow.AddSeconds(_liveLogTuneMinIntervalSec);
                return;
            }

            // 프로파일 기반 프리셋 적용
            settings.ApplyProfile();

            _liveLogPerfEnabled = settings.EnableMetrics;
            _liveLogAutoTuneEnabled = settings.EnableAutoTune;

            _liveLogBaseWarnMs = Math.Max(1, settings.LiveLogFlushWarnMs);
            _liveLogBasePerfLogIntervalSec = Math.Max(1, settings.LiveLogPerfLogIntervalSec);

            _liveLogWarnMinMs = Math.Max(1, settings.LiveLogFlushWarnMinMs);
            _liveLogWarnMaxMs = Math.Max(_liveLogWarnMinMs, settings.LiveLogFlushWarnMaxMs);
            _liveLogPerfLogIntervalMinSec = Math.Max(1, settings.PerfLogIntervalMinSec);
            _liveLogPerfLogIntervalMaxSec = Math.Max(_liveLogPerfLogIntervalMinSec, settings.PerfLogIntervalMaxSec);

            _liveLogTuneSampleWindow = Math.Clamp(settings.AutoTuneSampleWindow, 20, 240);
            _liveLogTuneMinIntervalSec = Math.Clamp(settings.AutoTuneMinIntervalSec, 10, 300);

            _liveLogFlushWarnMs = Math.Clamp(_liveLogBaseWarnMs, _liveLogWarnMinMs, _liveLogWarnMaxMs);
            _liveLogPerfLogIntervalSec = Math.Clamp(_liveLogBasePerfLogIntervalSec, _liveLogPerfLogIntervalMinSec, _liveLogPerfLogIntervalMaxSec);

            DateTime now = DateTime.UtcNow;
            _nextLiveLogPerfLogTime = now.AddSeconds(_liveLogPerfLogIntervalSec);
            _nextLiveLogTuneTime = now.AddSeconds(_liveLogTuneMinIntervalSec);
        }

        private void AutoTuneLiveLogPerfThresholds(int flushMs)
        {
            if (!_liveLogAutoTuneEnabled)
                return;

            _liveLogRecentFlushSamples.Enqueue(flushMs);
            while (_liveLogRecentFlushSamples.Count > _liveLogTuneSampleWindow)
            {
                _liveLogRecentFlushSamples.Dequeue();
            }

            DateTime now = DateTime.UtcNow;
            if (now < _nextLiveLogTuneTime)
                return;

            int minSampleCount = Math.Min(20, Math.Max(10, _liveLogTuneSampleWindow / 2));
            if (_liveLogRecentFlushSamples.Count < minSampleCount)
                return;

            var ordered = _liveLogRecentFlushSamples.OrderBy(v => v).ToArray();
            int p50 = GetPercentileValue(ordered, 0.50);
            int p90 = GetPercentileValue(ordered, 0.90);

            var settings = AppConfig.Current?.Trading?.PerformanceMonitoring;
            double multiplier = settings?.GetLiveLogMultiplier() ?? 1.35;

            int targetWarnMs = Math.Clamp(
                Math.Max(_liveLogBaseWarnMs, (int)Math.Ceiling(p90 * multiplier)),
                _liveLogWarnMinMs,
                _liveLogWarnMaxMs);

            int targetIntervalSec = _liveLogBasePerfLogIntervalSec;
            if (p90 >= targetWarnMs * 0.90 || p50 >= targetWarnMs * 0.60)
            {
                targetIntervalSec = Math.Max(_liveLogPerfLogIntervalMinSec, _liveLogBasePerfLogIntervalSec / 2);
            }
            else if (p90 <= targetWarnMs * 0.40)
            {
                targetIntervalSec = Math.Min(_liveLogPerfLogIntervalMaxSec, _liveLogBasePerfLogIntervalSec + 5);
            }

            targetIntervalSec = Math.Clamp(targetIntervalSec, _liveLogPerfLogIntervalMinSec, _liveLogPerfLogIntervalMaxSec);

            bool warnChanged = targetWarnMs != _liveLogFlushWarnMs;
            bool intervalChanged = targetIntervalSec != _liveLogPerfLogIntervalSec;

            if (warnChanged || intervalChanged)
            {
                LoggerService.Info(
                    $"[PERF][LIVELOG][AUTOTUNE] sample={ordered.Length} p50={p50}ms p90={p90}ms " +
                    $"warnMs={_liveLogFlushWarnMs}->{targetWarnMs} intervalSec={_liveLogPerfLogIntervalSec}->{targetIntervalSec}");

                _liveLogFlushWarnMs = targetWarnMs;
                _liveLogPerfLogIntervalSec = targetIntervalSec;
                _nextLiveLogPerfLogTime = now.AddSeconds(_liveLogPerfLogIntervalSec);
            }

            _nextLiveLogTuneTime = now.AddSeconds(_liveLogTuneMinIntervalSec);
        }

        private static int GetPercentileValue(int[] ordered, double percentile)
        {
            if (ordered.Length == 0)
                return 0;

            double clamped = Math.Clamp(percentile, 0, 1);
            int index = (int)Math.Ceiling(clamped * ordered.Length) - 1;
            index = Math.Clamp(index, 0, ordered.Length - 1);
            return ordered[index];
        }

        private static bool ShouldPersistLiveLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return !message.Contains("[DB]", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("DB ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLiveLogNoise(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("[SIGNAL][PUMP][CHECK_1M]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[PERF][LIVELOG]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[PERF][MAIN_LOOP]", StringComparison.OrdinalIgnoreCase);
        }

        private static string SimplifyLiveLogMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "로그 메시지가 비어 있습니다.";

            string simplified = message.Trim();

            simplified = ReplaceInsensitive(simplified, "[SIGNAL][PUMP][TRACE]", "[펌프 신호]");
            simplified = ReplaceInsensitive(simplified, "[신호][PUMP][추적]", "[펌프 신호]");
            simplified = ReplaceInsensitive(simplified, "[SIGNAL][TRANSFORMER][TRACE]", "[TF 신호]");
            simplified = ReplaceInsensitive(simplified, "[신호][TRANSFORMER][추적]", "[TF 신호]");
            simplified = ReplaceInsensitive(simplified, "[ENTRY][ORDER][ERROR]", "[주문 오류]");
            simplified = ReplaceInsensitive(simplified, "[진입][ORDER][오류]", "[주문 오류]");
            simplified = ReplaceInsensitive(simplified, "[ENTRY][15M_GATE][PASS]", "[15M 게이트 통과]");
            simplified = ReplaceInsensitive(simplified, "[ENTRY][15M_GATE][BLOCK]", "[15M 게이트 차단]");
            simplified = ReplaceInsensitive(simplified, "[GATE][AUTO_TUNE]", "[게이트 자동튜닝]");
            simplified = ReplaceInsensitive(simplified, "[신호][MAJOR][신호발생]", "[메이저 신호]");

            simplified = ReplaceInsensitive(simplified, "전략=", "");
            simplified = ReplaceInsensitive(simplified, "심볼=", "");
            simplified = ReplaceInsensitive(simplified, "방향=", "");
            simplified = ReplaceInsensitive(simplified, "원인=", "");
            simplified = ReplaceInsensitive(simplified, "사유=", "");
            simplified = ReplaceInsensitive(simplified, "판정=", "");

            simplified = simplified.Replace(" | ", " · ");
            simplified = Regex.Replace(simplified, @"\s+", " ").Trim();

            return simplified;
        }

        private async Task DrainLiveLogDbQueueAsync()
        {
            if (_dbService == null)
                return;

            if (Interlocked.Exchange(ref _liveLogDbDrainRunning, 1) == 1)
                return;

            try
            {
                int processed = 0;
                while (processed < MaxDbWritesPerDrain && _pendingLiveLogDbWrites.TryDequeue(out var logItem))
                {
                    try
                    {
                        await _dbService.SaveLiveLogAsync(logItem.Category, logItem.Message, logItem.Symbol);
                    }
                    catch
                    {
                    }

                    processed++;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _liveLogDbDrainRunning, 0);

                if (!_pendingLiveLogDbWrites.IsEmpty)
                {
                    _ = DrainLiveLogDbQueueAsync();
                }
            }
        }

        private static string LocalizeLiveLogMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "로그 메시지가 비어 있습니다.";

            string localized = message;

            localized = ReplaceInsensitive(localized, "[SIGNAL]", "[신호]");
            localized = ReplaceInsensitive(localized, "[ENTRY]", "[진입]");
            localized = ReplaceInsensitive(localized, "[TRACE]", "[추적]");
            localized = ReplaceInsensitive(localized, "[ERROR]", "[오류]");
            localized = ReplaceInsensitive(localized, "[SCAN]", "[스캔]");
            localized = ReplaceInsensitive(localized, "[PROFILE]", "[프로필]");
            localized = ReplaceInsensitive(localized, "[CANDIDATE]", "[후보]");
            localized = ReplaceInsensitive(localized, "[CHECK_1M]", "[1분검증]");
            localized = ReplaceInsensitive(localized, "[REJECT]", "[제외]");
            localized = ReplaceInsensitive(localized, "[EMIT]", "[신호발생]");
            localized = ReplaceInsensitive(localized, "[ANALYZE]", "[분석]");
            localized = ReplaceInsensitive(localized, "[BLOCK]", "[차단]");
            localized = ReplaceInsensitive(localized, "[WARN]", "[경고]");
            localized = ReplaceInsensitive(localized, "[PASS]", "[통과]");
            localized = ReplaceInsensitive(localized, "[SKIP]", "[건너뜀]");

            localized = ReplaceInsensitive(localized, "source=", "원인=");
            localized = ReplaceInsensitive(localized, "detail=", "상세=");
            localized = ReplaceInsensitive(localized, "src=", "전략=");
            localized = ReplaceInsensitive(localized, "sym=", "심볼=");
            localized = ReplaceInsensitive(localized, "side=", "방향=");
            localized = ReplaceInsensitive(localized, "reason=", "사유=");
            localized = ReplaceInsensitive(localized, "decision=", "판정=");
            localized = ReplaceInsensitive(localized, "warmupRemainingSec=", "워밍업남은초=");
            localized = ReplaceInsensitive(localized, "pumpSlots=", "펌프슬롯=");
            localized = ReplaceInsensitive(localized, "entryNotFilled=", "진입미체결=");

            localized = ReplaceInsensitive(localized, "reason=threshold", "사유=임계치미달");

            return localized;
        }

        private static string ReplaceInsensitive(string source, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(oldValue))
                return source;

            return source.Contains(oldValue, StringComparison.OrdinalIgnoreCase)
                ? source.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase)
                : source;
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

        private static string DetermineLiveLogCategory(string msg, bool isMajor)
        {
            if (IsGateLog(msg))
                return "GATE";

            return isMajor ? "MAJOR" : "PUMP";
        }

        private static bool IsGateLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return false;

            return msg.Contains("[ENTRY][15M_GATE]", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("[15M Gate]", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("[GATE][AUTO_TUNE]", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateGateDashboardFromLog(string rawMessage, string displayLine)
        {
            if (rawMessage.Contains("[ENTRY][15M_GATE][PASS]", StringComparison.OrdinalIgnoreCase))
            {
                GatePassCount++;
            }
            else if (rawMessage.Contains("[ENTRY][15M_GATE][BLOCK]", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("[15M Gate]", StringComparison.OrdinalIgnoreCase))
            {
                GateBlockCount++;
            }

            var gateThresholdMatch = GateThresholdRegex.Match(rawMessage);
            if (gateThresholdMatch.Success
                && float.TryParse(gateThresholdMatch.Groups["ml"].Value, out float mlThreshold)
                && float.TryParse(gateThresholdMatch.Groups["tf"].Value, out float tfThreshold))
            {
                GateThresholdSummaryText = $"ML {mlThreshold:F1}% / TF {tfThreshold:F1}%";
            }

            var autoTuneMatch = GateAutoTuneRegex.Match(rawMessage);
            if (autoTuneMatch.Success
                && float.TryParse(autoTuneMatch.Groups["newMl"].Value, out float tunedMl)
                && float.TryParse(autoTuneMatch.Groups["newTf"].Value, out float tunedTf))
            {
                GateThresholdSummaryText = $"ML {tunedMl:F1}% / TF {tunedTf:F1}%";
            }

            if (rawMessage.Contains("[GATE][AUTO_TUNE]", StringComparison.OrdinalIgnoreCase))
            {
                GateAutoTuneStatusText = displayLine;
            }
        }

        private void AddAlert(string msg)
        {
            RunOnUI(() =>
            {
                Alerts.Insert(0, $"▶ {DateTime.Now:HH:mm:ss} | {msg}");
                if (Alerts.Count > 100) Alerts.RemoveAt(100);
            });
        }

        private void Alerts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is string alertLine)
                        ProcessDbFailureAlert(alertLine);
                }
            }

            RefreshDbFailureAlertMetrics();
        }

        private void ProcessDbFailureAlert(string alertLine)
        {
            if (!IsDbFailureAlertMessage(alertLine))
                return;

            string symbol = ExtractSymbolFromAlert(alertLine);
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            string detail = ExtractAlertMessageBody(alertLine);
            UpdateExternalSyncStatus(symbol, "DB실패", detail);
        }

        private void RefreshDbFailureAlertMetrics()
        {
            var filtered = Alerts
                .Where(IsDbFailureAlertMessage)
                .ToList();

            DbFailureAlertCount = filtered.Count;

            DbFailureAlerts.Clear();
            foreach (var line in filtered)
                DbFailureAlerts.Add(line);
        }

        private static bool IsDbFailureAlertMessage(string? alertLine)
        {
            if (string.IsNullOrWhiteSpace(alertLine))
                return false;

            bool hasDbToken = alertLine.Contains("[DB", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains(" DB", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains("DB ", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains("DB저장", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains("DB 저장", StringComparison.OrdinalIgnoreCase);

            if (!hasDbToken)
                return false;

            return alertLine.Contains("실패", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains("오류", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains("error", StringComparison.OrdinalIgnoreCase)
                || alertLine.Contains("fail", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractSymbolFromAlert(string alertLine)
        {
            if (string.IsNullOrWhiteSpace(alertLine))
                return string.Empty;

            var symbolMatch = Regex.Match(alertLine, @"\b([A-Z0-9]+USDT)\b");
            return symbolMatch.Success ? symbolMatch.Groups[1].Value : string.Empty;
        }

        private static string ExtractAlertMessageBody(string alertLine)
        {
            if (string.IsNullOrWhiteSpace(alertLine))
                return string.Empty;

            int sepIndex = alertLine.IndexOf("| ", StringComparison.Ordinal);
            if (sepIndex >= 0 && sepIndex + 2 < alertLine.Length)
                return alertLine.Substring(sepIndex + 2).Trim();

            return alertLine.Trim();
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
                if (!IsFinite(equity) || !IsFinite(available))
                {
                    AddLiveLog("⚠️ 대시보드 값에 NaN/Infinity가 감지되어 업데이트를 건너뜁니다.");
                    return;
                }

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
                var activePositions = MarketDataList
                    .Where(x => x.IsPositionActive)
                    .Select(x => new
                    {
                        Symbol = x.Symbol ?? "Unknown",
                        ProfitPercent = ToFinite(x.ProfitPercent)
                    })
                    .Where(x => IsFinite(x.ProfitPercent))
                    .ToList();

                if (activePositions.Any())
                {
                    var values = new ChartValues<double>(activePositions.Select(x => x.ProfitPercent));

                    UpdateAxisRange(
                        values,
                        min => ActivePnLAxisMin = min,
                        max => ActivePnLAxisMax = max,
                        defaultMin: -1d,
                        defaultMax: 1d,
                        requirePositive: false);

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
                    ActivePnLLabels = activePositions.Select(x => x.Symbol).ToArray();
                    OnPropertyChanged(nameof(ActivePnLLabels));
                }
                else
                {
                    ActivePnLSeries = new SeriesCollection();
                    ActivePnLAxisMin = -1d;
                    ActivePnLAxisMax = 1d;
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
                TotalPositionInfo = $"Active: {major + pump} / {majorMax + pumpMax}";
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
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            _pendingTickerUpdates[symbol] = (price, pnl);
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
                    existing.HasCloseIncomplete = false;
                    existing.CloseIncompleteDetail = null;
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

        private void UpdateCloseIncompleteStatus(string symbol, bool isIncomplete, string? detail)
        {
            RunOnUI(() =>
            {
                var existing = MarketDataList.FirstOrDefault(x => x.Symbol == symbol);
                if (existing == null)
                {
                    existing = new MultiTimeframeViewModel { Symbol = symbol };
                    MarketDataList.Add(existing);
                }

                existing.HasCloseIncomplete = isIncomplete;
                existing.CloseIncompleteDetail = detail;
            });
        }

        private void UpdateExternalSyncStatus(string symbol, string? status, string? detail)
        {
            RunOnUI(() =>
            {
                var existing = MarketDataList.FirstOrDefault(x => x.Symbol == symbol);
                if (existing == null)
                {
                    existing = new MultiTimeframeViewModel { Symbol = symbol };
                    MarketDataList.Add(existing);
                }

                existing.ExternalSyncStatus = status;
                existing.ExternalSyncDetail = detail;
            });
        }

        private async Task LoadLiveChartDataAsync()
        {
            if (SelectedSymbol == null)
            {
                LiveChartSeries = new SeriesCollection();
                LiveChartAxisMin = 0d;
                LiveChartAxisMax = 1d;
                OnPropertyChanged(nameof(IsLiveChartEmpty));
                return;
            }

            try
            {
                var symbol = SelectedSymbol.Symbol;
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    LiveChartSeries = new SeriesCollection();
                    LiveChartAxisMin = 0d;
                    LiveChartAxisMax = 1d;
                    OnPropertyChanged(nameof(IsLiveChartEmpty));
                    return;
                }

                using var client = new BinanceRestClient();
                var klinesResult = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    limit: 20 // 최근 20개 캔들 (요청사항)
                );

                if (!klinesResult.Success)
                {
                    LiveChartSeries = new SeriesCollection();
                    LiveChartAxisMin = 0d;
                    LiveChartAxisMax = 1d;
                    OnPropertyChanged(nameof(IsLiveChartEmpty));
                    return;
                }

                var closeValues = new ChartValues<double>();
                var xLabels = new List<string>();
                double? lastValidClose = null;
                foreach (var kline in klinesResult.Data)
                {
                    var close = ToFinite((double)kline.ClosePrice, lastValidClose ?? double.NaN);
                    if (!IsFinite(close) || close <= 0)
                        continue;

                    lastValidClose = close;
                    closeValues.Add(close);
                    xLabels.Add(kline.OpenTime.ToLocalTime().ToString("HH:mm"));
                }

                if (closeValues.Count == 0)
                {
                    LiveChartSeries = new SeriesCollection();
                    LiveChartAxisMin = 0d;
                    LiveChartAxisMax = 1d;
                    OnPropertyChanged(nameof(IsLiveChartEmpty));
                    return;
                }

                UpdateAxisRange(
                    closeValues,
                    min => LiveChartAxisMin = min,
                    max => LiveChartAxisMax = max,
                    defaultMin: 0d,
                    defaultMax: 1d,
                    requirePositive: true);

                LiveChartXFormatter = val =>
                {
                    int index = (int)Math.Round(val);
                    return (index >= 0 && index < xLabels.Count) ? xLabels[index] : string.Empty;
                };

                LiveChartYFormatter = val => val.ToString("N2");

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
                    new ScatterSeries 
                    { 
                        Title = "Buy", 
                        Values = new ChartValues<ScatterPoint>(), 
                        PointGeometry = DefaultGeometries.Triangle, 
                        Fill = Brushes.LimeGreen, 
                        MinPointShapeDiameter = 15, 
                        DataLabels = false 
                    },
                    new ScatterSeries 
                    { 
                        Title = "Sell", 
                        Values = new ChartValues<ScatterPoint>(), 
                        PointGeometry = DefaultGeometries.Triangle, 
                        Fill = Brushes.Red, 
                        MinPointShapeDiameter = 15, 
                        DataLabels = false 
                    }
                };
                
                OnPropertyChanged(nameof(LiveChartSeries));
                OnPropertyChanged(nameof(LiveChartXFormatter));
                OnPropertyChanged(nameof(LiveChartYFormatter));
                OnPropertyChanged(nameof(IsLiveChartEmpty));
            }
            catch (Exception ex)
            {
                AddLog($"차트 로딩 실패: {ex.Message}");
                LiveChartSeries = new SeriesCollection();
                LiveChartAxisMin = 0d;
                LiveChartAxisMax = 1d;
                OnPropertyChanged(nameof(IsLiveChartEmpty));
            }
        }

        public void RecoverFromChartRenderError()
        {
            RunOnUI(() =>
            {
                AddLog("⚠️ LiveCharts 렌더링 오류를 감지하여 차트 상태를 안전하게 초기화합니다.");
                LiveChartSeries = new SeriesCollection();
                ActivePnLSeries = new SeriesCollection();
                ResetPredictionComparisonSeries();
                ResetRLRewardSeries();
                LiveChartAxisMin = 0d;
                LiveChartAxisMax = 1d;
                PredictionChartAxisMin = 0d;
                PredictionChartAxisMax = 1d;
                ActivePnLAxisMin = -1d;
                ActivePnLAxisMax = 1d;
                OnPropertyChanged(nameof(IsLiveChartEmpty));
            });
        }

        public void UpdateBacktestChart(BacktestResult result)
        {
            RunOnUI(() =>
            {
                if (result == null || result.EquityCurve == null || result.EquityCurve.Count == 0)
                {
                    BacktestSeries = new SeriesCollection();
                    BacktestLabels = Array.Empty<string>();
                    AddLiveLog("⚠️ 백테스트 차트 데이터가 없습니다.");
                    return;
                }

                var values = new ChartValues<double>();
                var buyPoints = new ChartValues<ScatterPoint>();
                var sellPoints = new ChartValues<ScatterPoint>();

                // EquityCurve 데이터 변환 (NaN/Infinity 필터링)
                for (int i = 0; i < result.EquityCurve.Count; i++)
                {
                    var equityValue = ToFinite((double)result.EquityCurve[i], 1000d);
                    
                    // 추가 검증: 음수나 0 방지
                    if (equityValue <= 0)
                        equityValue = i > 0 ? values[i - 1] : 1000d;
                    
                    values.Add(equityValue);
                }

                // 유효한 데이터 확인
                if (values.Count == 0 || values.All(v => !IsFinite(v)))
                {
                    BacktestSeries = new SeriesCollection();
                    BacktestLabels = Array.Empty<string>();
                    AddLiveLog("⚠️ 백테스트 차트에 유효한 데이터가 없습니다.");
                    return;
                }

                // 매매 기록을 차트 좌표에 매핑 (NaN 방지)
                foreach (var trade in result.TradeHistory)
                {
                    string timeStr = trade.Time.ToString("MM/dd HH:mm");
                    int index = result.TradeDates.IndexOf(timeStr);

                    if (index >= 0 && index < values.Count)
                    {
                        var y = values[index];
                        
                        // ScatterPoint는 유한한 값만 허용
                        if (!IsFinite(y) || y <= 0)
                            continue;
                        
                        if (trade.Side == "BUY")
                            buyPoints.Add(new ScatterPoint(index, y, 10));
                        else if (trade.Side == "SELL")
                            sellPoints.Add(new ScatterPoint(index, y, 10));
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
            var app = Application.Current;
            if (app == null) return;
            var dispatcher = app.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            try
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                    return;
                }

                _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            }
            catch
            {
                // UI 종료/Dispatcher 중단 구간 예외는 무시
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static decimal? SanitizeChartPrice(decimal value, decimal? fallback = null)
        {
            if (value > 0m)
                return value;

            if (fallback.HasValue && fallback.Value > 0m)
                return fallback.Value;

            return null;
        }

        private static double ToFinite(double value, double fallback = 0d)
        {
            if (IsFinite(value))
                return value;

            return IsFinite(fallback) ? fallback : 0d;
        }

        private void ResetPredictionComparisonSeries()
        {
            PredictionComparisonSeries = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "Predicted Price",
                    Values = new ChartValues<double>(),
                    PointGeometry = DefaultGeometries.Circle,
                    PointGeometrySize = 8,
                    Stroke = Brushes.Cyan,
                    Fill = Brushes.Transparent
                },
                new LineSeries
                {
                    Title = "Actual Price",
                    Values = new ChartValues<double>(),
                    PointGeometry = DefaultGeometries.Diamond,
                    PointGeometrySize = 8,
                    Stroke = Brushes.LimeGreen,
                    Fill = Brushes.Transparent
                }
            };

            PredictionChartAxisMin = 0d;
            PredictionChartAxisMax = 1d;
        }

        private void ResetRLRewardSeries()
        {
            RLRewardSeries = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "Scalping Reward",
                    Values = new ChartValues<double>(),
                    PointGeometry = null,
                    LineSmoothness = 0,
                    Stroke = Brushes.Cyan,
                    Fill = Brushes.Transparent
                },
                new LineSeries
                {
                    Title = "Swing Reward",
                    Values = new ChartValues<double>(),
                    PointGeometry = null,
                    LineSmoothness = 0,
                    Stroke = Brushes.Orange,
                    Fill = Brushes.Transparent
                }
            };
        }

        private void UpdateAxisRange(
            IEnumerable<double> rawValues,
            Action<double> setMin,
            Action<double> setMax,
            double defaultMin,
            double defaultMax,
            bool requirePositive)
        {
            var values = rawValues
                .Where(IsFinite)
                .Where(v => !requirePositive || v > 0d)
                .ToList();

            if (values.Count == 0)
            {
                setMin(defaultMin);
                setMax(defaultMax);
                return;
            }

            var min = values.Min();
            var max = values.Max();

            if (!IsFinite(min) || !IsFinite(max))
            {
                setMin(defaultMin);
                setMax(defaultMax);
                return;
            }

            if (Math.Abs(max - min) < 1e-9)
            {
                var baseValue = Math.Max(Math.Abs(max), 1d);
                var padding = baseValue * 0.01;
                min -= padding;
                max += padding;
            }

            if (requirePositive && min <= 0d)
                min = Math.Max(max * 0.99, 1e-6);

            if (!IsFinite(min) || !IsFinite(max) || min >= max)
            {
                setMin(defaultMin);
                setMax(defaultMax);
                return;
            }

            setMin(min);
            setMax(max);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
