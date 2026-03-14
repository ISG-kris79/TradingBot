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
using System.Text;
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
        private static readonly Regex SymbolRegex = new(@"\b([A-Z0-9]+USDT)\b", RegexOptions.Compiled);
        private static readonly Regex GateThresholdRegex = new(@"mlTh=(?<ml>\d+(?:\.\d+)?)%\s+tfTh=(?<tf>\d+(?:\.\d+)?)%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GateAutoTuneRegex = new(@"mlThr=(?<oldMl>\d+(?:\.\d+)?)%->(?<newMl>\d+(?:\.\d+)?)%.*tfThr=(?<oldTf>\d+(?:\.\d+)?)%->(?<newTf>\d+(?:\.\d+)?)%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FibNumericRegex = new(@"(\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private readonly ConcurrentQueue<BufferedLiveLog> _pendingLiveLogs = new();
        private readonly ConcurrentQueue<(string Category, string Message, string? Symbol)> _pendingLiveLogDbWrites = new();
        private readonly ConcurrentQueue<(DateTime Timestamp, string Message)> _pendingFooterLogDbWrites = new();
        private readonly ConcurrentDictionary<string, (decimal Price, double? Pnl)> _pendingTickerUpdates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, MultiTimeframeViewModel> _pendingSignalUpdates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AIEntryForecastResult> _pendingAiEntryProbUpdates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MultiTimeframeViewModel> _marketDataIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _footerLogLock = new();
        // [병목 해결] LoadTradeHistory 디바운싱 — 연속 이벤트로 인한 중복 DB 쿼리 + UI 큐 폭주 방지
        private CancellationTokenSource? _tradeHistoryDebounceCts;
        private readonly object _tradeHistoryDebounceLock = new();
        // [병목 해결] LoadLiveChartDataAsync 취소 토큰 — SelectedSymbol 빠른 변경 시 이전 API 호출 취소
        private CancellationTokenSource? _liveChartCts;
        private DispatcherTimer? _liveLogFlushTimer;
        private DispatcherTimer? _tickerFlushTimer;
        private DispatcherTimer? _footerLogFlushTimer;
        private string? _pendingFooterLogMessage;
        private int _liveLogDbDrainRunning;
        private int _footerLogDbDrainRunning;
        private const int MaxBufferedLiveLogs = 1200;
        private const int MaxUiLiveLogCount = 200;
        private const int MaxLiveLogBatchPerTick = 80;       // [병목 해결] 24→80 (1초 딜레이 해결)
        private const int MaxLiveLogUiWorkBudgetMsPerTick = 12;
        private const int MaxTickerBatchPerTick = 80;        // [병목 해결] 180→80 (PropertyChanged 폭주 방지)
        private const int MaxSignalBatchPerTick = 40;        // [병목 해결] 120→40 (BeginUpdate로 보완)
        private const int MaxDbWritesPerDrain = 80;
        private const int FooterLogFlushIntervalMs = 200;
        private const int MaxUiWorkBudgetMsPerTick = 6;      // [병목 해결] 8→6ms (UI 스레드 여유 확보)
        private const int MaxAlertBatchPerTick = 20;
        private const int MaxUiAlertCount = 100;
        private static readonly TimeSpan ActivePnlChartRefreshInterval = TimeSpan.FromSeconds(5); // [병목 해결] 3초→5초
        private DateTime _lastActivePnlChartRefresh = DateTime.MinValue;
        private readonly ConcurrentQueue<string> _pendingAlerts = new();
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
        private readonly ConcurrentDictionary<string, DateTime> _liveLogMirrorThrottleMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan HighFrequencyMirrorInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MirrorThrottleRetention = TimeSpan.FromMinutes(5);
        private DateTime _nextLiveLogPerfLogTime = DateTime.UtcNow.AddSeconds(10);
        private DateTime _nextLiveLogTuneTime = DateTime.UtcNow.AddSeconds(30);
        private DateTime _nextLiveLogWarnLogTime = DateTime.MinValue;
        private long _liveLogFlushTotalMs;
        private int _liveLogFlushSamples;
        private int _liveLogFlushMaxMs;
        private int _liveLogQueueHighWater;
        private int _liveLogDbQueueHighWater;
        private MultiTimeframeViewModel? _observedBattleSymbol;
        private decimal _battleLastObservedPrice;
        private bool _battleHasLastPrice;
        private DateTime _battleFlashUntilUtc = DateTime.MinValue;
        private DateTime _battleLastFastLogUtc = DateTime.MinValue;

        private readonly record struct BufferedLiveLog(
            DateTime Timestamp,
            string Message,
            string RawMessage,
            string Category,
            bool IsMajor,
            string? Symbol,
            bool PersistToDb,
            bool DisplayOnUi,
            string? DisplayMessage);

        // TradingEngine 속성 노출
        public decimal InitialBalance => _engine?.InitialBalance ?? 0;
        // 데이터 컬렉션
        public ObservableCollection<MultiTimeframeViewModel> MarketDataList { get; set; } = new ObservableCollection<MultiTimeframeViewModel>();
        public ObservableCollection<BattleExecutionStep> BattleExecutionSteps { get; } = new ObservableCollection<BattleExecutionStep>();
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

        // [WaveAI] 실시간 ML/TF 점수
        private string _waveMLScoreText = "ML: --%";
        public string WaveMLScoreText
        {
            get => _waveMLScoreText;
            set { _waveMLScoreText = value; OnPropertyChanged(); }
        }

        private string _waveTFScoreText = "TF: --%";
        public string WaveTFScoreText
        {
            get => _waveTFScoreText;
            set { _waveTFScoreText = value; OnPropertyChanged(); }
        }

        private string _waveStatusText = "대기 중";
        public string WaveStatusText
        {
            get => _waveStatusText;
            set { _waveStatusText = value; OnPropertyChanged(); }
        }

        private string _battleFocusSymbol = "-";
        public string BattleFocusSymbol
        {
            get => _battleFocusSymbol;
            set { _battleFocusSymbol = value; OnPropertyChanged(); }
        }

        private double _battlePulseScore;
        public double BattlePulseScore
        {
            get => _battlePulseScore;
            set { _battlePulseScore = value; OnPropertyChanged(); }
        }

        private string _battlePulseScoreText = "0.0";
        public string BattlePulseScoreText
        {
            get => _battlePulseScoreText;
            set { _battlePulseScoreText = value; OnPropertyChanged(); }
        }

        private Brush _battlePulseBrush = Brushes.DeepSkyBlue;
        public Brush BattlePulseBrush
        {
            get => _battlePulseBrush;
            set { _battlePulseBrush = value; OnPropertyChanged(); }
        }

        private string _battleConfidenceText = "신뢰도: 대기";
        public string BattleConfidenceText
        {
            get => _battleConfidenceText;
            set { _battleConfidenceText = value; OnPropertyChanged(); }
        }

        private string _battleThresholdText = "기준: -";
        public string BattleThresholdText
        {
            get => _battleThresholdText;
            set { _battleThresholdText = value; OnPropertyChanged(); }
        }

        private string _battleLivePriceText = "-";
        public string BattleLivePriceText
        {
            get => _battleLivePriceText;
            set { _battleLivePriceText = value; OnPropertyChanged(); }
        }

        private string _battlePriceDirectionText = "• FLAT";
        public string BattlePriceDirectionText
        {
            get => _battlePriceDirectionText;
            set { _battlePriceDirectionText = value; OnPropertyChanged(); }
        }

        private Brush _battlePriceBrush = Brushes.White;
        public Brush BattlePriceBrush
        {
            get => _battlePriceBrush;
            set { _battlePriceBrush = value; OnPropertyChanged(); }
        }

        private string _battleEtaText = "ETA: -";
        public string BattleEtaText
        {
            get => _battleEtaText;
            set { _battleEtaText = value; OnPropertyChanged(); }
        }

        private string _battleFibCountdownText = "Fib 카운트다운: -";
        public string BattleFibCountdownText
        {
            get => _battleFibCountdownText;
            set { _battleFibCountdownText = value; OnPropertyChanged(); }
        }

        private string _battleRsiCountdownText = "RSI 카운트다운: -";
        public string BattleRsiCountdownText
        {
            get => _battleRsiCountdownText;
            set { _battleRsiCountdownText = value; OnPropertyChanged(); }
        }

        private string _battleGoldenZoneText = "0.618 GOLDEN ZONE: 대기";
        public string BattleGoldenZoneText
        {
            get => _battleGoldenZoneText;
            set { _battleGoldenZoneText = value; OnPropertyChanged(); }
        }

        private Brush _battleGoldenZoneBrush = Brushes.LightGray;
        public Brush BattleGoldenZoneBrush
        {
            get => _battleGoldenZoneBrush;
            set { _battleGoldenZoneBrush = value; OnPropertyChanged(); }
        }

        private string _battleAtrCloudText = "ATR STOP CLOUD: 대기";
        public string BattleAtrCloudText
        {
            get => _battleAtrCloudText;
            set { _battleAtrCloudText = value; OnPropertyChanged(); }
        }

        private Brush _battleAtrCloudBrush = Brushes.LightGray;
        public Brush BattleAtrCloudBrush
        {
            get => _battleAtrCloudBrush;
            set { _battleAtrCloudBrush = value; OnPropertyChanged(); }
        }

        private bool _battleStopPulseActive;
        public bool BattleStopPulseActive
        {
            get => _battleStopPulseActive;
            set { _battleStopPulseActive = value; OnPropertyChanged(); }
        }

        private string _battleFastLog1 = "로그 대기 중";
        public string BattleFastLog1
        {
            get => _battleFastLog1;
            set { _battleFastLog1 = value; OnPropertyChanged(); }
        }

        private string _battleFastLog2 = "";
        public string BattleFastLog2
        {
            get => _battleFastLog2;
            set { _battleFastLog2 = value; OnPropertyChanged(); }
        }

        private string _battleFastLog3 = "";
        public string BattleFastLog3
        {
            get => _battleFastLog3;
            set { _battleFastLog3 = value; OnPropertyChanged(); }
        }

        private string _battleMajorBtcText = "BTC: 대기";
        public string BattleMajorBtcText
        {
            get => _battleMajorBtcText;
            set { _battleMajorBtcText = value; OnPropertyChanged(); }
        }

        private Brush _battleMajorBtcBrush = Brushes.LightGray;
        public Brush BattleMajorBtcBrush
        {
            get => _battleMajorBtcBrush;
            set { _battleMajorBtcBrush = value; OnPropertyChanged(); }
        }

        private string _battleMajorEthText = "ETH: 대기";
        public string BattleMajorEthText
        {
            get => _battleMajorEthText;
            set { _battleMajorEthText = value; OnPropertyChanged(); }
        }

        private Brush _battleMajorEthBrush = Brushes.LightGray;
        public Brush BattleMajorEthBrush
        {
            get => _battleMajorEthBrush;
            set { _battleMajorEthBrush = value; OnPropertyChanged(); }
        }

        private string _battleMajorSolText = "SOL: 대기";
        public string BattleMajorSolText
        {
            get => _battleMajorSolText;
            set { _battleMajorSolText = value; OnPropertyChanged(); }
        }

        private Brush _battleMajorSolBrush = Brushes.LightGray;
        public Brush BattleMajorSolBrush
        {
            get => _battleMajorSolBrush;
            set { _battleMajorSolBrush = value; OnPropertyChanged(); }
        }

        private string _battleMajorXrpText = "XRP: 대기";
        public string BattleMajorXrpText
        {
            get => _battleMajorXrpText;
            set { _battleMajorXrpText = value; OnPropertyChanged(); }
        }

        private Brush _battleMajorXrpBrush = Brushes.LightGray;
        public Brush BattleMajorXrpBrush
        {
            get => _battleMajorXrpBrush;
            set { _battleMajorXrpBrush = value; OnPropertyChanged(); }
        }

        private string _battleXrpScenarioText = "XRP 시나리오: 데이터 대기";
        public string BattleXrpScenarioText
        {
            get => _battleXrpScenarioText;
            set { _battleXrpScenarioText = value; OnPropertyChanged(); }
        }

        private Brush _battleXrpScenarioBrush = Brushes.LightGray;
        public Brush BattleXrpScenarioBrush
        {
            get => _battleXrpScenarioBrush;
            set { _battleXrpScenarioBrush = value; OnPropertyChanged(); }
        }

        // AI 라벨링 상태
        private int _aiTotalDecisions;
        public int AiTotalDecisions
        {
            get => _aiTotalDecisions;
            set { _aiTotalDecisions = value; OnPropertyChanged(); OnPropertyChanged(nameof(AiLabelingRateText)); }
        }

        private int _aiLabeledCount;
        public int AiLabeledCount
        {
            get => _aiLabeledCount;
            set { _aiLabeledCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(AiLabelingRateText)); }
        }

        private int _aiMarkToMarketCount;
        public int AiMarkToMarketCount
        {
            get => _aiMarkToMarketCount;
            set { _aiMarkToMarketCount = value; OnPropertyChanged(); }
        }

        private int _aiTradeCloseCount;
        public int AiTradeCloseCount
        {
            get => _aiTradeCloseCount;
            set { _aiTradeCloseCount = value; OnPropertyChanged(); }
        }

        private int _aiActiveDecisions;
        public int AiActiveDecisions
        {
            get => _aiActiveDecisions;
            set { _aiActiveDecisions = value; OnPropertyChanged(); }
        }

        private bool _aiModelsLoaded;
        public bool AiModelsLoaded
        {
            get => _aiModelsLoaded;
            set { _aiModelsLoaded = value; OnPropertyChanged(); OnPropertyChanged(nameof(AiModelStatusText)); OnPropertyChanged(nameof(AiModelStatusColor)); }
        }

        public string AiLabelingRateText
        {
            get
            {
                if (AiTotalDecisions == 0) return "0.0%";
                double rate = (double)AiLabeledCount / AiTotalDecisions * 100.0;
                return $"{rate:F1}%";
            }
        }

        public string AiModelStatusText => AiModelsLoaded ? "✅ MODELS READY" : "⏳ LOADING...";
        
        public string AiModelStatusColor => AiModelsLoaded ? "#00E676" : "#FFB300";

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
                    DetachBattleSymbolObserver();
                    _selectedSymbol = value;
                    _battleHasLastPrice = false;
                    OnPropertyChanged();
                    AttachBattleSymbolObserver(_selectedSymbol);
                    RefreshBattleDashboardForSelectedSymbol();
                    RefreshMajorBattlePanel();
                    // [병목 해결] 이전 API 호출 취소 후 새 호출 시작
                    _liveChartCts?.Cancel();
                    _liveChartCts = new CancellationTokenSource();
                    _ = LoadLiveChartDataAsync(_liveChartCts.Token);
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
            ConfigureMarketDataSorting(refresh: false);
            ApplyLiveLogPerformanceSettings();
            InitializeBattleExecutionSteps();
            InitializeLiveLogPipeline();
            InitializeTickerUpdatePipeline();
            InitializeFooterLogPipeline();

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

                        if (selectedStrategy != BacktestStrategyType.LiveEntryParity
                            && (result?.Candles?.Count ?? 0) > 0
                            && (result?.TotalTrades ?? 0) == 0)
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
                            AddLog($"⚡ {symbol} 수동 청산 요청 처리됨 (실패 시 로그 확인 후 [동기화] 버튼 실행)");
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

        /// <summary>[병목 해결] 연속 이벤트를 2초 디바운싱하여 DB 쿼리 중복 방지</summary>
        private void ScheduleLoadTradeHistory()
        {
            lock (_tradeHistoryDebounceLock)
            {
                _tradeHistoryDebounceCts?.Cancel();
                _tradeHistoryDebounceCts = new CancellationTokenSource();
                var token = _tradeHistoryDebounceCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (!token.IsCancellationRequested)
                            await LoadTradeHistory();
                    }
                    catch (OperationCanceledException) { }
                });
            }
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
                    // [병목 해결] Clear() + N x Add() → 단일 Reset으로 대체
                    // 기존: 5001개의 CollectionChanged 이벤트 → 변경 후: 1개의 Reset 이벤트
                    var newCollection = new ObservableCollection<TradeLog>(historyModels);
                    BindingOperations.EnableCollectionSynchronization(newCollection, new object());
                    TradeHistory = newCollection;
                    OnPropertyChanged(nameof(TradeHistory));

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

        /* TensorFlow 전환 중 임시 비활성화
        /// <summary>
        /// [WaveAI] 엘리엇 파동 이중 검증 엔진 설정
        /// </summary>
        public void SetWaveEngine(DoubleCheckEntryEngine? waveEngine)
        {
            _engine?.SetWaveEngine(waveEngine);
        }
        */

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
            _engine.OnStatusLog += msg => HandleStatusLog(msg);
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

            // [FIX] 청산 시 TradeHistory 자동 갱신 — 디바운싱 적용
            _engine.OnTradeHistoryUpdated += () =>
            {
                ScheduleLoadTradeHistory();
            };

            // [AI 진입 예측] 확률 업데이트 구독
            _engine.OnAiEntryProbUpdate += (symbol, forecast) =>
            {
                UpdateAiEntryProb(symbol, forecast);
            };

            // [WaveAI] ML/TF 점수 업데이트 구독
            _engine.OnWaveAIScoreUpdate += (symbol, mlScore, tfScore, status) =>
            {
                RunOnUI(() =>
                {
                    if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                        return;

                    // [FIX] ML/TF가 0이면 "대기" 표시 (0%가 아니라 아직 실행 안 됨을 의미)
                    WaveMLScoreText = mlScore <= 0 ? "ML: 대기" : $"ML: {mlScore:P0}";
                    WaveTFScoreText = tfScore <= 0 ? "TF: 대기" : $"TF: {tfScore:P0}";
                    WaveStatusText = status;
                    
                    // [NEW] 해당 심볼의 ViewModel에도 ML/TF 확률 업데이트
                    var symbolVm = MarketDataList.FirstOrDefault(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
                    if (symbolVm != null)
                    {
                        symbolVm.MLProbability = mlScore;
                        symbolVm.TFConfidence = tfScore;
                    }
                });
            };
        }

        private void UnsubscribeFromEngineEvents()
        {
            _engine?.ClearEventSubscriptions();
        }

        private void HandleStatusLog(string msg)
        {
            AddLog(msg);

            if (ShouldMirrorStatusToLiveLog(msg))
            {
                AddLiveLog(msg);
            }
        }

        private bool ShouldMirrorStatusToLiveLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            if (IsGateLog(message) || IsOrderErrorLog(message))
                return true;

            if (!IsEssentialTradeFlowLog(message))
                return false;

            if (!IsHighFrequencyMirrorTarget(message))
                return true;

            return TryAcquireMirrorSlot(message);
        }

        private bool TryAcquireMirrorSlot(string message)
        {
            string key = BuildMirrorThrottleKey(message);
            var nowUtc = DateTime.UtcNow;

            if (_liveLogMirrorThrottleMap.TryGetValue(key, out var lastSeenUtc)
                && nowUtc - lastSeenUtc < HighFrequencyMirrorInterval)
            {
                return false;
            }

            _liveLogMirrorThrottleMap[key] = nowUtc;
            CleanupMirrorThrottleCache(nowUtc);
            return true;
        }

        private void CleanupMirrorThrottleCache(DateTime nowUtc)
        {
            if (_liveLogMirrorThrottleMap.Count < 512)
                return;

            foreach (var item in _liveLogMirrorThrottleMap)
            {
                if (nowUtc - item.Value > MirrorThrottleRetention)
                {
                    _liveLogMirrorThrottleMap.TryRemove(item.Key, out _);
                }
            }
        }

        private static bool IsHighFrequencyMirrorTarget(string message)
        {
            return message.Contains("진입대기", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[캔들 확인 대기]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("트레일링갱신", StringComparison.OrdinalIgnoreCase)
                || message.Contains("트레일링 갱신", StringComparison.OrdinalIgnoreCase)
                || message.Contains("지표모니터링", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMirrorThrottleKey(string message)
        {
            string tag;
            if (message.Contains("진입대기", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[캔들 확인 대기]", StringComparison.OrdinalIgnoreCase))
            {
                tag = "ENTRY_WAIT";
            }
            else if (message.Contains("트레일링갱신", StringComparison.OrdinalIgnoreCase)
                || message.Contains("트레일링 갱신", StringComparison.OrdinalIgnoreCase))
            {
                tag = "TRAIL_UPDATE";
            }
            else if (message.Contains("지표모니터링", StringComparison.OrdinalIgnoreCase))
            {
                tag = "INDICATOR_MONITOR";
            }
            else
            {
                tag = "ESSENTIAL";
            }

            var symbolMatch = SymbolRegex.Match(message);
            var symbol = symbolMatch.Success ? symbolMatch.Groups[1].Value : "GLOBAL";
            return $"{tag}:{symbol}";
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

            var localizedMessage = LocalizeLiveLogMessage(msg);
            var compactMessage = SimplifyLiveLogMessage(localizedMessage);
            var isMajor = IsMajorSymbolLog(msg);
            var category = DetermineLiveLogCategory(msg, isMajor);
            var symbolMatch = SymbolRegex.Match(msg);
            var symbol = symbolMatch.Success ? symbolMatch.Groups[1].Value : null;
            bool persistToDb = ShouldPersistLiveLog(localizedMessage);

            RunOnUI(() => PushBattleFastLog(compactMessage));

            if (persistToDb && _dbService != null)
            {
                _pendingLiveLogDbWrites.Enqueue((category, localizedMessage, symbol));
                int dbQueueLength = _pendingLiveLogDbWrites.Count;
                if (dbQueueLength > _liveLogDbQueueHighWater)
                    _liveLogDbQueueHighWater = dbQueueLength;

                _ = DrainLiveLogDbQueueAsync();
            }

            if (string.Equals(category, "GATE", StringComparison.OrdinalIgnoreCase))
            {
                RunOnUI(() => UpdateGateDashboardFromLog(msg, string.Empty));
            }
        }

        private void InitializeLiveLogPipeline()
        {
            RunOnUI(() =>
            {
                if (_liveLogFlushTimer != null)
                {
                    _liveLogFlushTimer.Stop();
                    _liveLogFlushTimer = null;
                }

                TradeLogs.Clear();
                GateLogs.Clear();
                LiveLogs.Clear();
                MajorLogs.Clear();
                PumpLogs.Clear();
            });
        }

        private void InitializeTickerUpdatePipeline()
        {
            RunOnUI(() =>
            {
                if (_tickerFlushTimer != null)
                    return;

                _tickerFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(200)  // [병목 해결] 120ms→200ms (UI 스레드 호흡 공간 확보)
                };
                _tickerFlushTimer.Tick += (_, _) =>
                {
                    FlushPendingSignalUpdatesToUi();
                    FlushPendingAiEntryProbUpdatesToUi();
                    FlushPendingTickerUpdatesToUi();
                    RefreshBattleDashboardForSelectedSymbol();
                    RefreshMajorBattlePanel();
                };
                _tickerFlushTimer.Start();
            });
        }

        private void InitializeFooterLogPipeline()
        {
            RunOnUI(() =>
            {
                if (_footerLogFlushTimer != null)
                    return;

                _footerLogFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(FooterLogFlushIntervalMs)
                };
                _footerLogFlushTimer.Tick += (_, _) =>
                {
                    FlushPendingFooterLogToUi();
                    FlushPendingAlertsToUi();
                };
                _footerLogFlushTimer.Start();
            });
        }

        private void InitializeBattleExecutionSteps()
        {
            BattleExecutionSteps.Clear();
            BattleExecutionSteps.Add(new BattleExecutionStep("01", "Market Sync"));
            BattleExecutionSteps.Add(new BattleExecutionStep("02", "Wave AI Gate"));
            BattleExecutionSteps.Add(new BattleExecutionStep("03", "0.618 Golden Zone"));
            BattleExecutionSteps.Add(new BattleExecutionStep("04", "ATR Close-Only"));
            UpdateBattleExecutionSteps(null, false, false, false);
        }

        private void UpdateBattleExecutionSteps(
            MultiTimeframeViewModel? symbolVm,
            bool aiPassed,
            bool goldenZoneReady,
            bool atrArmed)
        {
            if (BattleExecutionSteps.Count < 4)
                return;

            bool hasSymbol = symbolVm != null;
            bool syncReady = hasSymbol && symbolVm!.LastPrice > 0;
            bool aiReady = syncReady && aiPassed;
            bool fibReady = aiReady && goldenZoneReady;
            bool atrReady = fibReady && atrArmed;

            int activeIndex = -1;
            if (hasSymbol)
            {
                if (!syncReady) activeIndex = 0;
                else if (!aiReady) activeIndex = 1;
                else if (!fibReady) activeIndex = 2;
                else if (!atrReady) activeIndex = 3;
            }

            SetBattleStep(0,
                hasSymbol ? $"Stream {FormatBattlePrice(symbolVm!.LastPrice)}" : "종목 선택 대기",
                syncReady,
                activeIndex == 0);

            SetBattleStep(1,
                hasSymbol ? $"Pulse {BattlePulseScoreText} · {BattleThresholdText}" : "AI 대기",
                aiReady,
                activeIndex == 1);

            SetBattleStep(2,
                BattleGoldenZoneText,
                fibReady,
                activeIndex == 2);

            SetBattleStep(3,
                BattleAtrCloudText,
                atrReady,
                activeIndex == 3);
        }

        private void SetBattleStep(int index, string detail, bool completed, bool active)
        {
            if (index < 0 || index >= BattleExecutionSteps.Count)
                return;

            var step = BattleExecutionSteps[index];
            step.Detail = detail;
            step.IsActive = active;
            step.StateText = completed ? "완료" : active ? "진행" : "대기";
            step.StateBrush = completed
                ? Brushes.LimeGreen
                : active
                    ? Brushes.Gold
                    : Brushes.LightGray;
        }

        private double ResolveBattleGateThresholdValue()
        {
            if (string.IsNullOrWhiteSpace(GateThresholdSummaryText))
                return 55d;

            var match = GateThresholdRegex.Match(GateThresholdSummaryText);
            if (!match.Success)
                return 55d;

            var mlThreshold = 55d;
            var tfThreshold = 52d;

            if (double.TryParse(match.Groups["ml"].Value, out var parsedMl))
                mlThreshold = parsedMl;

            if (double.TryParse(match.Groups["tf"].Value, out var parsedTf))
                tfThreshold = parsedTf;

            return Math.Max(mlThreshold, tfThreshold);
        }

        private static bool ResolveGoldenZoneState(string? fibPosition, out string label)
        {
            if (string.IsNullOrWhiteSpace(fibPosition) || fibPosition == "-")
            {
                label = "대기";
                return false;
            }

            string normalized = fibPosition.Trim().ToUpperInvariant();

            if (normalized.Contains("MID", StringComparison.Ordinal) ||
                normalized.Contains("ABOVE618", StringComparison.Ordinal) ||
                normalized.Contains("BELOW618", StringComparison.Ordinal))
            {
                label = normalized;
                return true;
            }

            if (TryExtractFibRatio(normalized, out double fibRatio))
            {
                double distance = Math.Abs(fibRatio - 0.618d) * 100d;
                label = $"{fibRatio:0.000} ({distance:F1}%p)";
                return distance <= 6d;
            }

            label = normalized;
            return false;
        }

        private void AttachBattleSymbolObserver(MultiTimeframeViewModel? symbolVm)
        {
            if (symbolVm == null)
                return;

            _observedBattleSymbol = symbolVm;
            _observedBattleSymbol.PropertyChanged += ObservedBattleSymbolOnPropertyChanged;
        }

        private void DetachBattleSymbolObserver()
        {
            if (_observedBattleSymbol == null)
                return;

            _observedBattleSymbol.PropertyChanged -= ObservedBattleSymbolOnPropertyChanged;
            _observedBattleSymbol = null;
        }

        private void ObservedBattleSymbolOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RefreshBattleDashboardForSelectedSymbol();
        }

        private void RefreshBattleDashboardForSelectedSymbol()
        {
            RunOnUI(() =>
            {
                var symbolVm = SelectedSymbol;
                if (symbolVm == null)
                {
                    BattleFocusSymbol = "-";
                    BattlePulseScore = 0;
                    BattlePulseScoreText = "0.0";
                    BattleConfidenceText = "신뢰도: 대기";
                    BattleThresholdText = string.IsNullOrWhiteSpace(GateThresholdSummaryText) ? "기준: -" : $"기준: {GateThresholdSummaryText}";
                    BattleLivePriceText = "-";
                    BattlePriceDirectionText = "• FLAT";
                    BattlePriceBrush = Brushes.LightGray;
                    BattleEtaText = "ETA: -";
                    BattleFibCountdownText = "Fib 카운트다운: -";
                    BattleRsiCountdownText = "RSI 카운트다운: -";
                    BattleGoldenZoneText = "0.618 GOLDEN ZONE: 대기";
                    BattleGoldenZoneBrush = Brushes.LightGray;
                    BattleAtrCloudText = "ATR STOP CLOUD: 대기";
                    BattleAtrCloudBrush = Brushes.LightGray;
                    BattleStopPulseActive = false;
                    UpdateBattleExecutionSteps(null, false, false, false);
                    _battleHasLastPrice = false;
                    return;
                }

                BattleFocusSymbol = symbolVm.Symbol ?? "-";

                double score = Math.Clamp(symbolVm.AIScore, 0f, 100f);
                BattlePulseScore = score;
                BattlePulseScoreText = $"{score:F1}";
                BattlePulseBrush = score switch
                {
                    >= 80 => Brushes.LimeGreen,
                    >= 65 => Brushes.Gold,
                    >= 45 => Brushes.Orange,
                    _ => Brushes.OrangeRed
                };

                float confidenceRatio = ResolveBattleConfidence(symbolVm, score);
                BattleConfidenceText = $"신뢰도: {confidenceRatio * 100f:F1}%";
                BattleThresholdText = string.IsNullOrWhiteSpace(GateThresholdSummaryText)
                    ? "기준: -"
                    : $"기준: {GateThresholdSummaryText}";

                decimal livePrice = symbolVm.LastPrice;
                BattleLivePriceText = FormatBattlePrice(livePrice);

                var nowUtc = DateTime.UtcNow;
                if (livePrice > 0)
                {
                    if (_battleHasLastPrice)
                    {
                        if (livePrice > _battleLastObservedPrice)
                        {
                            BattlePriceDirectionText = "▲ UP";
                            BattlePriceBrush = Brushes.LimeGreen;
                            _battleFlashUntilUtc = nowUtc.AddMilliseconds(500);
                        }
                        else if (livePrice < _battleLastObservedPrice)
                        {
                            BattlePriceDirectionText = "▼ DOWN";
                            BattlePriceBrush = Brushes.Tomato;
                            _battleFlashUntilUtc = nowUtc.AddMilliseconds(500);
                        }
                        else if (nowUtc > _battleFlashUntilUtc)
                        {
                            BattlePriceDirectionText = "• FLAT";
                            BattlePriceBrush = Brushes.LightGray;
                        }
                    }
                    else
                    {
                        BattlePriceDirectionText = "• LIVE";
                        BattlePriceBrush = Brushes.White;
                    }

                    _battleLastObservedPrice = livePrice;
                    _battleHasLastPrice = true;
                }
                else
                {
                    BattlePriceDirectionText = "• FLAT";
                    BattlePriceBrush = Brushes.LightGray;
                }

                BattleFibCountdownText = BuildFibCountdownText(symbolVm.FibPosition);
                BattleRsiCountdownText = BuildRsiCountdownText(symbolVm.RSI_1H, symbolVm.Decision);

                bool goldenZoneReady = ResolveGoldenZoneState(symbolVm.FibPosition, out var goldenZoneLabel);
                BattleGoldenZoneText = $"0.618 GOLDEN ZONE: {goldenZoneLabel}";
                BattleGoldenZoneBrush = goldenZoneReady
                    ? Brushes.LimeGreen
                    : Brushes.Gold;

                bool atrArmed = symbolVm.IsPositionActive && symbolVm.StopLossPrice > 0m;
                double stopDistancePct = 0d;
                if (atrArmed && symbolVm.LastPrice > 0m)
                {
                    stopDistancePct = Math.Abs((double)((symbolVm.LastPrice - symbolVm.StopLossPrice) / symbolVm.LastPrice)) * 100d;
                }

                if (atrArmed)
                {
                    BattleAtrCloudText = $"ATR STOP CLOUD: {FormatBattlePrice(symbolVm.StopLossPrice)} | 거리 {stopDistancePct:F2}% | Close-Only";
                    BattleStopPulseActive = stopDistancePct <= 0.7d;
                    BattleAtrCloudBrush = BattleStopPulseActive
                        ? Brushes.OrangeRed
                        : stopDistancePct <= 1.5d
                            ? Brushes.Gold
                            : Brushes.DeepSkyBlue;
                }
                else
                {
                    BattleAtrCloudText = symbolVm.IsPositionActive
                        ? "ATR STOP CLOUD: 계산 대기 (Close-Only)"
                        : "ATR STOP CLOUD: 포지션 없음 (Close-Only)";
                    BattleAtrCloudBrush = Brushes.LightGray;
                    BattleStopPulseActive = false;
                }

                if (symbolVm.AiEntryForecastOffsetMinutes.HasValue && symbolVm.AiEntryForecastOffsetMinutes.Value > 1)
                {
                    string etaTime = symbolVm.AiEntryForecastTime?.ToString("HH:mm") ?? "--:--";
                    BattleEtaText = $"ETA: +{symbolVm.AiEntryForecastOffsetMinutes.Value}m ({etaTime})";
                }
                else if (symbolVm.AiEntryProb >= 0.35f)
                {
                    BattleEtaText = "ETA: NOW";
                }
                else if (symbolVm.AiEntryProb >= 0)
                {
                    BattleEtaText = "ETA: 관망";
                }
                else
                {
                    BattleEtaText = "ETA: 대기";
                }

                double gateThreshold = ResolveBattleGateThresholdValue();
                bool aiPassed = score >= gateThreshold || confidenceRatio >= 0.35f;
                UpdateBattleExecutionSteps(symbolVm, aiPassed, goldenZoneReady, atrArmed);
            });
        }

        private static float ResolveBattleConfidence(MultiTimeframeViewModel symbolVm, double fallbackScore)
        {
            if (symbolVm.AiEntryProb >= 0)
                return Math.Clamp(symbolVm.AiEntryProb, 0f, 1f);

            var candidates = new List<float>();
            if (symbolVm.MLProbability > 0)
                candidates.Add(Math.Clamp(symbolVm.MLProbability, 0f, 1f));
            if (symbolVm.TFConfidence >= 0)
                candidates.Add(Math.Clamp(symbolVm.TFConfidence, 0f, 1f));

            if (candidates.Count > 0)
                return candidates.Average();

            return (float)Math.Clamp(fallbackScore / 100d, 0d, 1d);
        }

        private static string FormatBattlePrice(decimal price)
        {
            if (price <= 0)
                return "-";

            if (price >= 1000m)
                return price.ToString("N2");
            if (price >= 1m)
                return price.ToString("N4");
            return price.ToString("N6");
        }

        private static string BuildRsiCountdownText(double rsi, string? decision)
        {
            if (!IsFinite(rsi) || rsi <= 0)
                return "RSI 카운트다운: -";

            string side = decision?.ToUpperInvariant() ?? string.Empty;
            if (side.Contains("LONG", StringComparison.Ordinal))
            {
                double gap = Math.Max(0d, rsi - 40d);
                return $"RSI 카운트다운: LONG 기준까지 {gap:F1}pt";
            }

            if (side.Contains("SHORT", StringComparison.Ordinal))
            {
                double gap = Math.Max(0d, 60d - rsi);
                return $"RSI 카운트다운: SHORT 기준까지 {gap:F1}pt";
            }

            double neutralGap = Math.Min(Math.Abs(rsi - 40d), Math.Abs(60d - rsi));
            return $"RSI 카운트다운: 트리거까지 {neutralGap:F1}pt";
        }

        private static string BuildFibCountdownText(string? fibPosition)
        {
            if (string.IsNullOrWhiteSpace(fibPosition) || fibPosition == "-")
                return "Fib 카운트다운: -";

            string normalized = fibPosition.Trim().ToUpperInvariant();
            if (normalized.Contains("BELOW618", StringComparison.Ordinal))
                return "Fib 카운트다운: 0.618 하단 진입";
            if (normalized.Contains("ABOVE618", StringComparison.Ordinal))
                return "Fib 카운트다운: 0.618 상단 유지";
            if (normalized.Contains("ABOVE382", StringComparison.Ordinal))
                return "Fib 카운트다운: 0.618까지 2스텝";
            if (normalized.Contains("MID", StringComparison.Ordinal))
                return "Fib 카운트다운: 0.618 근접 1스텝";

            if (TryExtractFibRatio(normalized, out double fibRatio))
            {
                double distance = Math.Abs(fibRatio - 0.618d) * 100d;
                return $"Fib 카운트다운: 0.618까지 {distance:F1}%p";
            }

            return $"Fib 카운트다운: {normalized}";
        }

        private static bool TryExtractFibRatio(string value, out double ratio)
        {
            ratio = 0d;
            var match = FibNumericRegex.Match(value);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, out double parsed))
                return false;

            if (parsed >= 100d)
                parsed /= 1000d;
            else if (parsed > 10d)
                parsed /= 100d;

            ratio = Math.Clamp(parsed, 0d, 2d);
            return true;
        }

        private void RefreshMajorBattlePanel()
        {
            RunOnUI(() =>
            {
                ApplyMajorBattleStatus("BTCUSDT", "BTC", text => BattleMajorBtcText = text, brush => BattleMajorBtcBrush = brush);
                ApplyMajorBattleStatus("ETHUSDT", "ETH", text => BattleMajorEthText = text, brush => BattleMajorEthBrush = brush);
                ApplyMajorBattleStatus("SOLUSDT", "SOL", text => BattleMajorSolText = text, brush => BattleMajorSolBrush = brush);
                ApplyMajorBattleStatus("XRPUSDT", "XRP", text => BattleMajorXrpText = text, brush => BattleMajorXrpBrush = brush);

                var xrpVm = FindMarketDataItem("XRPUSDT");
                if (xrpVm == null)
                {
                    BattleXrpScenarioText = "XRP 시나리오: 데이터 대기";
                    BattleXrpScenarioBrush = Brushes.LightGray;
                    return;
                }

                if (xrpVm.IsPositionActive)
                {
                    BattleXrpScenarioText = $"XRP 시나리오: {xrpVm.PositionSide} 보유 {xrpVm.ProfitRate:+0.0;-0.0;0.0}%";
                    BattleXrpScenarioBrush = xrpVm.ProfitRate >= 0 ? Brushes.LimeGreen : Brushes.Tomato;
                    return;
                }

                string decision = (xrpVm.Decision ?? "WAIT").ToUpperInvariant();
                string rsiText = xrpVm.RSI_1H > 0 ? $"RSI {xrpVm.RSI_1H:F1}" : "RSI 대기";
                string aiText = xrpVm.AIScore > 0 ? $"AI {xrpVm.AIScore:F1}" : "AI 대기";

                if (decision.Contains("LONG", StringComparison.Ordinal))
                {
                    BattleXrpScenarioText = $"XRP 시나리오: LONG 준비 · {aiText} · {rsiText}";
                    BattleXrpScenarioBrush = Brushes.DeepSkyBlue;
                }
                else if (decision.Contains("SHORT", StringComparison.Ordinal))
                {
                    BattleXrpScenarioText = $"XRP 시나리오: SHORT 준비 · {aiText} · {rsiText}";
                    BattleXrpScenarioBrush = Brushes.OrangeRed;
                }
                else
                {
                    BattleXrpScenarioText = $"XRP 시나리오: 대기 · {aiText} · {rsiText}";
                    BattleXrpScenarioBrush = Brushes.LightGray;
                }
            });
        }

        private void ApplyMajorBattleStatus(string symbol, string label, Action<string> textSetter, Action<Brush> brushSetter)
        {
            var vm = FindMarketDataItem(symbol);
            if (vm == null)
            {
                textSetter($"{label}: 대기");
                brushSetter(Brushes.LightGray);
                return;
            }

            if (vm.IsPositionActive)
            {
                textSetter($"{label} {vm.PositionSide} {vm.ProfitRate:+0.0;-0.0;0.0}%");
                brushSetter(vm.ProfitRate >= 0 ? Brushes.LimeGreen : Brushes.Tomato);
                return;
            }

            string decision = (vm.Decision ?? "WAIT").ToUpperInvariant();
            if (decision.Contains("LONG", StringComparison.Ordinal))
            {
                textSetter($"{label} LONG 감시");
                brushSetter(Brushes.DeepSkyBlue);
                return;
            }

            if (decision.Contains("SHORT", StringComparison.Ordinal))
            {
                textSetter($"{label} SHORT 감시");
                brushSetter(Brushes.OrangeRed);
                return;
            }

            textSetter($"{label}: 대기");
            brushSetter(Brushes.LightGray);
        }

        private MultiTimeframeViewModel? FindMarketDataItem(string symbol)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                return null;

            if (_marketDataIndex.TryGetValue(normalizedSymbol, out var cached))
                return cached;

            var existing = MarketDataList.FirstOrDefault(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                _marketDataIndex[normalizedSymbol] = existing;

            return existing;
        }

        private void PushBattleFastLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _battleLastFastLogUtc < TimeSpan.FromMilliseconds(250))
                return;

            _battleLastFastLogUtc = nowUtc;
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            BattleFastLog3 = BattleFastLog2;
            BattleFastLog2 = BattleFastLog1;
            BattleFastLog1 = line;
        }

        private void FlushPendingFooterLogToUi()
        {
            string? latestMessage;
            lock (_footerLogLock)
            {
                latestMessage = _pendingFooterLogMessage;
                _pendingFooterLogMessage = null;
            }

            if (string.IsNullOrWhiteSpace(latestMessage))
                return;

            FooterText = $"[{DateTime.Now:HH:mm:ss}] {latestMessage}";
        }

        private void QueueFooterLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;

            lock (_footerLogLock)
            {
                _pendingFooterLogMessage = msg;
            }

            if (_dbService != null)
            {
                _pendingFooterLogDbWrites.Enqueue((DateTime.Now, msg));
                _ = DrainFooterLogDbQueueAsync();
            }
        }

        private void FlushPendingSignalUpdatesToUi()
        {
            if (_pendingSignalUpdates.IsEmpty)
                return;

            // [병목 해결] AI 초기 학습 중에는 시그널 UI 업데이트 스킵
            if (UISuspensionManager.IsSignalUpdateSuspended)
                return;

            var watch = Stopwatch.StartNew();
            int processed = 0;
            bool requiresResort = false;
            // [병목 해결] ToArray() → Keys 스냅샷 사용 (전체 딕셔너리 lock+copy 방지)
            foreach (var key in _pendingSignalUpdates.Keys)
            {
                if (processed >= MaxSignalBatchPerTick || watch.ElapsedMilliseconds >= MaxUiWorkBudgetMsPerTick)
                    break;

                if (!_pendingSignalUpdates.TryRemove(key, out var signal))
                    continue;

                requiresResort |= ApplySignalUpdate(signal);
                processed++;
            }

            if (requiresResort)
                ConfigureMarketDataSorting();
        }

        private void FlushPendingAiEntryProbUpdatesToUi()
        {
            if (_pendingAiEntryProbUpdates.IsEmpty)
                return;

            var watch = Stopwatch.StartNew();
            int processed = 0;
            foreach (var key in _pendingAiEntryProbUpdates.Keys)
            {
                if (processed >= MaxSignalBatchPerTick || watch.ElapsedMilliseconds >= MaxUiWorkBudgetMsPerTick)
                    break;

                if (!_pendingAiEntryProbUpdates.TryRemove(key, out var payload))
                    continue;

                if (!TryNormalizeTradingSymbol(key, out var normalizedSymbol))
                    continue;

                var existing = GetOrCreateMarketDataItem(normalizedSymbol);
                if (existing == null)
                    continue;
                bool probChanged = Math.Abs(existing.AiEntryProb - payload.AverageProbability) > 0.001f;
                bool forecastTimeChanged = existing.AiEntryForecastTime != payload.ForecastTimeLocal;
                bool forecastOffsetChanged = existing.AiEntryForecastOffsetMinutes != payload.ForecastOffsetMinutes;
                bool timestampChanged = existing.AiEntryProbUpdatedAt != payload.GeneratedAtLocal;

                if (!probChanged && !forecastTimeChanged && !forecastOffsetChanged && !timestampChanged)
                    continue;

                existing.BeginUpdate();
                try
                {
                    if (probChanged)
                        existing.AiEntryProb = payload.AverageProbability;

                    if (forecastTimeChanged)
                        existing.AiEntryForecastTime = payload.ForecastTimeLocal;

                    if (forecastOffsetChanged)
                        existing.AiEntryForecastOffsetMinutes = payload.ForecastOffsetMinutes;

                    if (probChanged || timestampChanged)
                        existing.AiEntryProbUpdatedAt = payload.GeneratedAtLocal;
                }
                finally
                {
                    existing.EndUpdate();
                }

                processed++;
            }
        }

        private MultiTimeframeViewModel? GetOrCreateMarketDataItem(string symbol)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                return null;

            if (_marketDataIndex.TryGetValue(normalizedSymbol, out var cached))
                return cached;

            var existing = MarketDataList.FirstOrDefault(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _marketDataIndex[normalizedSymbol] = existing;
                return existing;
            }

            var created = new MultiTimeframeViewModel { Symbol = normalizedSymbol };
            created.EntryStatus = ResolveEntryStatus(created.SignalSource, created.Decision, created.IsPositionActive);
            MarketDataList.Add(created);
            _marketDataIndex[normalizedSymbol] = created;
            return created;
        }

        private static string ResolveEntryStatus(string? signalSource, string? decision, bool isPositionActive)
        {
            if (isPositionActive)
                return "진입중";

            if (string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase))
            {
                return "평가중";
            }

            if (!string.IsNullOrWhiteSpace(signalSource) && signalSource != "-")
            {
                if (signalSource.Contains("MEME", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Contains("PUMP", StringComparison.OrdinalIgnoreCase))
                {
                    return "펌프 감시";
                }

                if (signalSource.Contains("TRANSFORMER", StringComparison.OrdinalIgnoreCase))
                    return "TF 감시";

                if (signalSource.Contains("MAJOR", StringComparison.OrdinalIgnoreCase))
                    return "메이저 감시";

                return "신호 감시";
            }

            return "대기";
        }

        private bool ApplySignalUpdate(MultiTimeframeViewModel signal)
        {
            if (signal == null || !TryNormalizeTradingSymbol(signal.Symbol, out var symbol))
                return false;

            if (!string.Equals(signal.Symbol, symbol, StringComparison.Ordinal))
                signal.Symbol = symbol;

            if (!_marketDataIndex.TryGetValue(symbol, out var existing))
            {
                existing = MarketDataList.FirstOrDefault(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    if (signal.AIScore > 0)
                        signal.TouchAIScoreUpdatedAt();

                    signal.EntryStatus = ResolveEntryStatus(signal.SignalSource, signal.Decision, signal.IsPositionActive);

                    MarketDataList.Add(signal);
                    _marketDataIndex[symbol] = signal;
                    return signal.IsPositionActive;
                }

                _marketDataIndex[symbol] = existing;
            }

            // [병목 해결] BeginUpdate/EndUpdate로 PropertyChanged 폭주 방지
            bool requiresResort = false;
            existing.BeginUpdate();
            try
            {
                if (signal.LastPrice > 0) existing.LastPrice = signal.LastPrice;
                if (signal.RSI_1H > 0) existing.RSI_1H = signal.RSI_1H;
                if (signal.AIScore > 0 && Math.Abs(existing.AIScore - signal.AIScore) > 0.001f)
                {
                    existing.AIScore = signal.AIScore;
                    existing.TouchAIScoreUpdatedAt();
                }
                if (!string.IsNullOrEmpty(signal.Decision) && !string.Equals(existing.Decision, signal.Decision, StringComparison.Ordinal)) existing.Decision = signal.Decision;
                if (!string.IsNullOrEmpty(signal.BBPosition) && !string.Equals(existing.BBPosition, signal.BBPosition, StringComparison.Ordinal)) existing.BBPosition = signal.BBPosition;
                if (!string.IsNullOrEmpty(signal.SignalSource) && !string.Equals(existing.SignalSource, signal.SignalSource, StringComparison.Ordinal)) existing.SignalSource = signal.SignalSource;
                if (Math.Abs(existing.ShortLongScore - signal.ShortLongScore) > 0.001) existing.ShortLongScore = signal.ShortLongScore;
                if (Math.Abs(existing.ShortShortScore - signal.ShortShortScore) > 0.001) existing.ShortShortScore = signal.ShortShortScore;
                if (Math.Abs(existing.MacdHist - signal.MacdHist) > 0.0001) existing.MacdHist = signal.MacdHist;
                if (!string.IsNullOrEmpty(signal.ElliottTrend) && !string.Equals(existing.ElliottTrend, signal.ElliottTrend, StringComparison.Ordinal)) existing.ElliottTrend = signal.ElliottTrend;
                if (!string.IsNullOrEmpty(signal.MAState) && !string.Equals(existing.MAState, signal.MAState, StringComparison.Ordinal)) existing.MAState = signal.MAState;
                if (!string.IsNullOrEmpty(signal.FibPosition) && !string.Equals(existing.FibPosition, signal.FibPosition, StringComparison.Ordinal)) existing.FibPosition = signal.FibPosition;
                if (Math.Abs(existing.VolumeRatioValue - signal.VolumeRatioValue) > 0.001) existing.VolumeRatioValue = signal.VolumeRatioValue;
                if (!string.IsNullOrEmpty(signal.VolumeRatio) && !string.Equals(existing.VolumeRatio, signal.VolumeRatio, StringComparison.Ordinal)) existing.VolumeRatio = signal.VolumeRatio;
                if (signal.IsPositionActive && !existing.IsPositionActive)
                {
                    existing.IsPositionActive = true;
                    requiresResort = true;
                }
                if (signal.EntryPrice > 0) existing.EntryPrice = signal.EntryPrice;
                if (!string.IsNullOrEmpty(signal.PositionSide) && !string.Equals(existing.PositionSide, signal.PositionSide, StringComparison.Ordinal)) existing.PositionSide = signal.PositionSide;
                if (signal.Quantity > 0) existing.Quantity = signal.Quantity;
                if (signal.Leverage > 0) existing.Leverage = signal.Leverage;
                if (signal.TargetPrice > 0) existing.TargetPrice = signal.TargetPrice;
                if (signal.StopLossPrice > 0) existing.StopLossPrice = signal.StopLossPrice;

                if (signal.TransformerPrice > 0 && existing.TransformerPrice != signal.TransformerPrice) existing.TransformerPrice = signal.TransformerPrice;
                if (Math.Abs(existing.TransformerChange - signal.TransformerChange) > 0.001) existing.TransformerChange = signal.TransformerChange;
                if (signal.AiEntryProb >= 0)
                {
                    bool probChanged = Math.Abs(existing.AiEntryProb - signal.AiEntryProb) > 0.001f;
                    bool forecastTimeChanged = signal.AiEntryForecastTime.HasValue && existing.AiEntryForecastTime != signal.AiEntryForecastTime;
                    bool forecastOffsetChanged = signal.AiEntryForecastOffsetMinutes.HasValue && existing.AiEntryForecastOffsetMinutes != signal.AiEntryForecastOffsetMinutes;
                    var effectiveUpdatedAt = signal.AiEntryProbUpdatedAt ?? DateTime.Now;
                    bool timestampChanged = signal.AiEntryProbUpdatedAt.HasValue && existing.AiEntryProbUpdatedAt != signal.AiEntryProbUpdatedAt;

                    if (probChanged)
                        existing.AiEntryProb = signal.AiEntryProb;

                    if (forecastTimeChanged)
                        existing.AiEntryForecastTime = signal.AiEntryForecastTime;

                    if (forecastOffsetChanged)
                        existing.AiEntryForecastOffsetMinutes = signal.AiEntryForecastOffsetMinutes;

                    if (probChanged || forecastTimeChanged || forecastOffsetChanged || timestampChanged)
                        existing.AiEntryProbUpdatedAt = effectiveUpdatedAt;
                }

                string resolvedEntryStatus = ResolveEntryStatus(existing.SignalSource, existing.Decision, existing.IsPositionActive);
                if (!string.Equals(existing.EntryStatus, resolvedEntryStatus, StringComparison.Ordinal))
                    existing.EntryStatus = resolvedEntryStatus;
            }
            finally
            {
                existing.EndUpdate(); // 모든 변경을 하나의 PropertyChanged("") 으로 통합
            }

            return requiresResort;
        }

        private void ConfigureMarketDataSorting(bool refresh = true)
        {
            var view = CollectionViewSource.GetDefaultView(MarketDataList);
            if (view == null)
                return;

            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(MultiTimeframeViewModel.SortPriority), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(nameof(MultiTimeframeViewModel.IsPositionActive), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(MultiTimeframeViewModel.ProfitRate), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(MultiTimeframeViewModel.AIScore), ListSortDirection.Descending));
            }

            if (view is ICollectionViewLiveShaping liveView && liveView.CanChangeLiveSorting)
            {
                liveView.LiveSortingProperties.Clear();
                liveView.LiveSortingProperties.Add(nameof(MultiTimeframeViewModel.SortPriority));
                liveView.LiveSortingProperties.Add(nameof(MultiTimeframeViewModel.IsPositionActive));
                liveView.LiveSortingProperties.Add(nameof(MultiTimeframeViewModel.ProfitRate));
                liveView.LiveSortingProperties.Add(nameof(MultiTimeframeViewModel.AIScore));
                liveView.IsLiveSorting = true;
            }

            if (refresh)
                view.Refresh();
        }

        private void FlushPendingTickerUpdatesToUi()
        {
            if (_pendingTickerUpdates.IsEmpty)
                return;

            var watch = Stopwatch.StartNew();
            int processed = 0;
            // [병목 해결] ToArray() → Keys 스냅샷 사용 (전체 딕셔너리 lock+copy 방지)
            foreach (var key in _pendingTickerUpdates.Keys)
            {
                if (processed >= MaxTickerBatchPerTick || watch.ElapsedMilliseconds >= MaxUiWorkBudgetMsPerTick)
                    break;

                if (!_pendingTickerUpdates.TryRemove(key, out var payload))
                    continue;

                if (!TryNormalizeTradingSymbol(key, out var normalizedSymbol))
                    continue;

                var existing = GetOrCreateMarketDataItem(normalizedSymbol);
                if (existing == null)
                    continue;

                // [병목 해결] BeginUpdate/EndUpdate로 PropertyChanged 폭주 방지
                existing.BeginUpdate();
                try
                {
                    if (payload.Price > 0)
                        existing.LastPrice = payload.Price;

                    if (payload.Pnl.HasValue)
                    {
                        var safePnl = ToFinite(payload.Pnl.Value, existing.ProfitPercent);
                        existing.ProfitPercent = safePnl;
                    }
                }
                finally
                {
                    existing.EndUpdate();
                }

                processed++;
            }
        }

        private void FlushPendingLiveLogsToUi()
        {
            try
            {
                Stopwatch? flushWatch = _liveLogPerfEnabled ? Stopwatch.StartNew() : null;
                var uiBudgetWatch = Stopwatch.StartNew();
                int pendingBefore = _pendingLiveLogs.Count;
                int dbQueueBefore = _pendingLiveLogDbWrites.Count;

                // [병목 해결] Insert(0) 대신 미리 수집 후 일괄 삽입 패턴으로 변경
                // Insert(0)는 O(n) 배열 이동 + CollectionChanged → 매 항목 전체 ListView 재렌더
                List<string>? gateLogBatch = null;
                List<string>? tradeLogBatch = null;
                List<(string Raw, string Line)>? gateUpdateBatch = null;

                int processed = 0;
                while (processed < MaxLiveLogBatchPerTick)
                {
                    if (uiBudgetWatch.ElapsedMilliseconds >= MaxLiveLogUiWorkBudgetMsPerTick)
                        break;

                    if (!_pendingLiveLogs.TryDequeue(out var item))
                        break;

                    if (_dbService != null && item.PersistToDb)
                    {
                        _pendingLiveLogDbWrites.Enqueue((item.Category, item.Message, item.Symbol));
                        int dbQueueLength = _pendingLiveLogDbWrites.Count;
                        if (dbQueueLength > _liveLogDbQueueHighWater)
                            _liveLogDbQueueHighWater = dbQueueLength;
                    }

                    if (item.DisplayOnUi && !string.IsNullOrWhiteSpace(item.DisplayMessage))
                    {
                        var line = $"[{item.Timestamp:HH:mm:ss}] {item.DisplayMessage}";

                        if (string.Equals(item.Category, "GATE", StringComparison.OrdinalIgnoreCase))
                        {
                            (gateLogBatch ??= new()).Add(line);
                            (gateUpdateBatch ??= new()).Add((item.RawMessage, line));
                        }
                        else
                        {
                            (tradeLogBatch ??= new()).Add(line);
                        }
                    }

                    processed++;
                }

                // [병목 해결] 수집된 로그를 역순으로 한 번에 삽입
                if (gateLogBatch != null)
                {
                    for (int i = gateLogBatch.Count - 1; i >= 0; i--)
                        GateLogs.Insert(0, gateLogBatch[i]);
                    while (GateLogs.Count > MaxUiLiveLogCount) GateLogs.RemoveAt(GateLogs.Count - 1);
                }
                if (gateUpdateBatch != null)
                {
                    foreach (var (raw, line) in gateUpdateBatch)
                        UpdateGateDashboardFromLog(raw, line);
                }
                if (tradeLogBatch != null)
                {
                    for (int i = tradeLogBatch.Count - 1; i >= 0; i--)
                        TradeLogs.Insert(0, tradeLogBatch[i]);
                    while (TradeLogs.Count > MaxUiLiveLogCount) TradeLogs.RemoveAt(TradeLogs.Count - 1);
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
                && !message.Contains("DB ", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("주문 요청 중", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("[ENTRY][START]", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("[ENTRY][RR][PASS]", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("[ENTRY][RR][BLOCK]", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("[SIGNAL][PUMP][EMIT]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLiveLogNoise(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("[SIGNAL][PUMP][CHECK_1M]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SIGNAL][PUMP][SCAN]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SIGNAL][PUMP][CANDIDATE]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SIGNAL][PUMP][PROFILE]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SIGNAL][PUMP][INFO]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SIGNAL][PUMP][REJECT]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[PERF][LIVELOG]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[PERF][MAIN_LOOP]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHighFrequencyIngressLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("[SIGNAL][PUMP][EMIT]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("주문 요청 중", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[ENTRY][START]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[ENTRY][RR][PASS]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[ENTRY][RR][BLOCK]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldDisplayLiveLogOnUi(string category, string rawMessage)
        {
            if (string.Equals(category, "GATE", StringComparison.OrdinalIgnoreCase))
                return true;

            return IsOrderErrorLog(rawMessage)
                || IsEssentialTradeFlowLog(rawMessage);
        }

        private static bool IsOrderErrorLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("[ENTRY][ORDER][ERROR]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[진입][ORDER][오류]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("주문 처리 오류", StringComparison.OrdinalIgnoreCase)
                || message.Contains("주문 오류", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEssentialTradeFlowLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("진입대기", StringComparison.OrdinalIgnoreCase)
                || message.Contains("진입 시작", StringComparison.OrdinalIgnoreCase)
                || message.Contains("진입시작", StringComparison.OrdinalIgnoreCase)
                || message.Contains("진입 차단", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[ENTRY][START]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SLOT]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[WaveAI]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[캔들 확인 대기]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("트레일링스탑시작", StringComparison.OrdinalIgnoreCase)
                || message.Contains("트레일링 갱신", StringComparison.OrdinalIgnoreCase)
                || message.Contains("트레일링갱신", StringComparison.OrdinalIgnoreCase)
                || message.Contains("지표모니터링", StringComparison.OrdinalIgnoreCase)
                || message.Contains("손절시작", StringComparison.OrdinalIgnoreCase)
                || message.Contains("손절실행", StringComparison.OrdinalIgnoreCase)
                || message.Contains("익절실행", StringComparison.OrdinalIgnoreCase)
                || message.Contains("익절시작", StringComparison.OrdinalIgnoreCase)
                || message.Contains("손익비", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SLIPPAGE][FAST][WARN]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[SLIPPAGE][FAST][EXIT]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryNormalizeTradingSymbol(string? rawSymbol, out string normalizedSymbol)
        {
            normalizedSymbol = string.Empty;
            if (string.IsNullOrWhiteSpace(rawSymbol))
                return false;

            string upper = rawSymbol.Trim().ToUpperInvariant();
            var buffer = new StringBuilder(upper.Length);

            foreach (char ch in upper)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                    buffer.Append(ch);
            }

            if (buffer.Length < 6)
                return false;

            string candidate = buffer.ToString();
            if (!candidate.EndsWith("USDT", StringComparison.Ordinal))
                return false;

            normalizedSymbol = candidate;
            return true;
        }

        private static string SimplifyLiveLogMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "로그 메시지가 비어 있습니다.";

            string normalized = Regex.Replace(message.Trim(), @"\s+", " ");

            if (normalized.Contains("신호 감지", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("신호 발생", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("📊 [", StringComparison.Ordinal)
                || normalized.StartsWith("🎯 [", StringComparison.Ordinal)
                || normalized.StartsWith("📤 [", StringComparison.Ordinal)
                || normalized.StartsWith("🤖 [", StringComparison.Ordinal)
                || normalized.StartsWith("✅ [", StringComparison.Ordinal)
                || normalized.StartsWith("⛔ [", StringComparison.Ordinal)
                || normalized.StartsWith("🛡️ [", StringComparison.Ordinal)
                || normalized.StartsWith("🔍 [", StringComparison.Ordinal)
                || normalized.StartsWith("📈 [", StringComparison.Ordinal)
                || normalized.StartsWith("📉 [", StringComparison.Ordinal)
                || normalized.StartsWith("⏳ [", StringComparison.Ordinal)
                || normalized.StartsWith("⏸️ [", StringComparison.Ordinal)
                || normalized.StartsWith("🌊 [", StringComparison.Ordinal)
                || normalized.StartsWith("❌ [", StringComparison.Ordinal)
                || normalized.StartsWith("💰 [", StringComparison.Ordinal)
                || normalized.StartsWith("🏃 [", StringComparison.Ordinal))
            {
                return normalized;
            }

            string emoji = "";
            string label = "라이브";
            
            if (normalized.Contains("[15M_GATE]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("15M 게이트", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[GATE]", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "🛡️";
                label = "AI게이트";
            }
            else if (normalized.Contains("[ENTRY][ORDER][ERROR]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[진입][ORDER][오류]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("주문 처리 오류", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("주문 오류", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "❌";
                label = "주문실패";
            }
            else if (normalized.Contains("진입대기", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[캔들 확인 대기]", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "⏸️";
                label = "진입대기중";
            }
            else if (normalized.Contains("[ENTRY][START]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("진입 시작", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("진입시작", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "🚀";
                label = "진입실행";
            }
            else if (normalized.Contains("트레일링스탑시작", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("트레일링갱신", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("트레일링 갱신", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("지표모니터링", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "📊";
                label = "트레일링중";
            }
            else if (normalized.Contains("[ENTRY][RR][BLOCK]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("손익비 부족", StringComparison.OrdinalIgnoreCase)
                || (normalized.Contains("RR=", StringComparison.OrdinalIgnoreCase)
                    && normalized.Contains("<", StringComparison.OrdinalIgnoreCase)))
            {
                emoji = "📉";
                label = "손익비 차단";
            }
            else if (normalized.Contains("손절시작", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("손절실행", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "🛑";
                label = "손절실행";
            }
            else if (normalized.Contains("익절시작", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("익절실행", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "💰";
                label = "익절실행";
            }
            else if (normalized.Contains("[SIGNAL][PUMP]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[신호][PUMP]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("펌프", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "🚨";
                label = "펌프감지";
            }
            else if (normalized.Contains("TRANSFORMER", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[TF", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "🤖";
                label = "AI예측";
            }
            else if (normalized.Contains("[신호][MAJOR]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[SIGNAL][MAJOR]", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "⭐";
                label = "메이저신호";
            }
            else if (normalized.Contains("[ENTRY]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("[진입]", StringComparison.OrdinalIgnoreCase))
            {
                emoji = "▶️";
                label = "진입신호";
            }

            string symbol = string.Empty;
            var symbolMatch = SymbolRegex.Match(normalized);
            if (symbolMatch.Success)
                symbol = symbolMatch.Groups[1].Value;

            string side = string.Empty;
            if (normalized.Contains("LONG", StringComparison.OrdinalIgnoreCase))
                side = "🟢LONG";
            else if (normalized.Contains("SHORT", StringComparison.OrdinalIgnoreCase))
                side = "🔴SHORT";

            string status = string.Empty;
            if (normalized.Contains("차단", StringComparison.OrdinalIgnoreCase) || normalized.Contains("BLOCK", StringComparison.OrdinalIgnoreCase))
                status = "⛔차단";
            else if (normalized.Contains("통과", StringComparison.OrdinalIgnoreCase) || normalized.Contains("PASS", StringComparison.OrdinalIgnoreCase))
                status = "✅통과";
            else if (normalized.Contains("대기", StringComparison.OrdinalIgnoreCase) || normalized.Contains("HOLD", StringComparison.OrdinalIgnoreCase))
                status = "⏳대기";
            else if (normalized.Contains("오류", StringComparison.OrdinalIgnoreCase) || normalized.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                status = "❗오류";

            string detail = string.Empty;
            int detailIdx = normalized.IndexOf("상세=", StringComparison.OrdinalIgnoreCase);
            if (detailIdx >= 0)
            {
                detail = normalized.Substring(detailIdx + 3).Trim();
            }
            else
            {
                int reasonIdx = normalized.IndexOf("사유=", StringComparison.OrdinalIgnoreCase);
                if (reasonIdx >= 0)
                {
                    detail = normalized.Substring(reasonIdx + 3).Trim();
                }
                else
                {
                    int pipeIdx = normalized.IndexOf('|');
                    if (pipeIdx >= 0 && pipeIdx + 1 < normalized.Length)
                        detail = normalized.Substring(pipeIdx + 1).Trim();
                }
            }

            // [GATE 전용] 차단 이유 간소화
            if (label == "AI게이트" && !string.IsNullOrWhiteSpace(detail))
            {
                // ML.NET 신뢰도 부족
                if (detail.Contains("ML.NET 불일치", StringComparison.OrdinalIgnoreCase) || 
                    detail.Contains("ML.NET", StringComparison.OrdinalIgnoreCase) && detail.Contains("conf=", StringComparison.OrdinalIgnoreCase))
                {
                    var confMatch = Regex.Match(detail, @"conf=([0-9.]+)%", RegexOptions.IgnoreCase);
                    var thMatch = Regex.Match(detail, @"<([0-9.]+)%", RegexOptions.IgnoreCase);
                    if (confMatch.Success && thMatch.Success)
                        detail = $"ML 부족 {confMatch.Groups[1].Value}% < {thMatch.Groups[1].Value}%";
                    else if (confMatch.Success)
                        detail = $"ML 신뢰도 {confMatch.Groups[1].Value}%";
                    else
                        detail = "ML 모델 불일치";
                }
                // Transformer 신뢰도 부족
                else if (detail.Contains("Transformer 불일치", StringComparison.OrdinalIgnoreCase) || 
                         detail.Contains("Transformer", StringComparison.OrdinalIgnoreCase) && detail.Contains("conf=", StringComparison.OrdinalIgnoreCase))
                {
                    var confMatch = Regex.Match(detail, @"conf=([0-9.]+)%", RegexOptions.IgnoreCase);
                    var thMatch = Regex.Match(detail, @"<([0-9.]+)%", RegexOptions.IgnoreCase);
                    if (confMatch.Success && thMatch.Success)
                        detail = $"TF 부족 {confMatch.Groups[1].Value}% < {thMatch.Groups[1].Value}%";
                    else if (confMatch.Success)
                        detail = $"TF 신뢰도 {confMatch.Groups[1].Value}%";
                    else
                        detail = "TF 모델 불일치";
                }
                // 모델 미준비
                else if (detail.Contains("ML.NET 모델 미준비", StringComparison.OrdinalIgnoreCase))
                    detail = "❌ ML 모델 없음";
                else if (detail.Contains("Transformer 모델 미준비", StringComparison.OrdinalIgnoreCase))
                    detail = "❌ TF 모델 없음";
                // 구조 실패
                else if (detail.Contains("structureFail", StringComparison.OrdinalIgnoreCase))
                {
                    var topdownMatch = Regex.Match(detail, @"topdown=1h:(-?\d+),4h:(-?\d+)", RegexOptions.IgnoreCase);
                    if (topdownMatch.Success)
                        detail = $"엘리엇 구조실패 (1h:{topdownMatch.Groups[1].Value} 4h:{topdownMatch.Groups[2].Value})";
                    else
                        detail = "엘리엇 웨이브 구조 불일치";
                }
                // 캔들/시퀀스 부족
                else if (detail.Contains("15m캔들 부족", StringComparison.OrdinalIgnoreCase))
                {
                    var countMatch = Regex.Match(detail, @"<(\d+)", RegexOptions.IgnoreCase);
                    if (countMatch.Success)
                        detail = $"15분봉 부족 (< {countMatch.Groups[1].Value}개)";
                    else
                        detail = "15분봉 데이터 부족";
                }
                else if (detail.Contains("Transformer 시퀀스 부족", StringComparison.OrdinalIgnoreCase))
                    detail = "TF 시퀀스 데이터 부족";
                else if (detail.Contains("decision!=LONG/SHORT", StringComparison.OrdinalIgnoreCase))
                    detail = "방향 오류 (LONG/SHORT 아님)";
                
                // ML/TF 임계값 표시 간소화
                detail = Regex.Replace(detail, @"mlTh=([0-9.]+)%", "ML≥$1%", RegexOptions.IgnoreCase);
                detail = Regex.Replace(detail, @"tfTh=([0-9.]+)%", "TF≥$1%", RegexOptions.IgnoreCase);
            }
            else
            {
                // 일반 로그: price= 패턴을 "현재가"로 변경
                detail = Regex.Replace(detail, @"\bprice\s*=\s*([0-9.]+)", "현재가 $$$1", RegexOptions.IgnoreCase);
                
                // qty= 패턴을 "수량"으로 변경
                detail = Regex.Replace(detail, @"\bqty\s*=\s*([0-9.]+)", "수량 $1", RegexOptions.IgnoreCase);
                
                // ML/TF 퍼센트는 그대로 유지
                detail = Regex.Replace(detail, @"\bML\s+([0-9.]+)%", "ML $1%", RegexOptions.IgnoreCase);
                detail = Regex.Replace(detail, @"\bTF\s+([0-9.]+)%", "TF $1%", RegexOptions.IgnoreCase);
            }

            if (detail.Length > 90)
                detail = detail.Substring(0, 90) + "…";

            // [색상 코딩] 상태별 시각적 구분
            string statusPrefix = "";
            if (status.Contains("차단") || status.Contains("오류"))
                statusPrefix = "🔴"; // 빨강 - 차단/오류
            else if (status.Contains("통과"))
                statusPrefix = "🟢"; // 초록 - 통과
            else if (status.Contains("대기"))
                statusPrefix = "🟡"; // 노랑 - 대기

            // [심볼 강조] 메이저 코인 구분
            string symbolDisplay = symbol;
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                bool isMajor = symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ||
                               symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase) ||
                               symbol.StartsWith("SOL", StringComparison.OrdinalIgnoreCase) ||
                               symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);
                symbolDisplay = isMajor ? $"⭐{symbol}" : $"💎{symbol}";
            }

            // [조립] 색상 프리픽스 + 라벨 + 내용
            var compact = !string.IsNullOrWhiteSpace(statusPrefix)
                ? $"{statusPrefix} {emoji} {label}"
                : $"{emoji} {label}";

            if (!string.IsNullOrWhiteSpace(symbolDisplay)) compact += $" │ {symbolDisplay}";
            if (!string.IsNullOrWhiteSpace(side)) compact += $" │ {side}";
            if (!string.IsNullOrWhiteSpace(detail)) compact += $" │ {detail}";

            return compact;
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

        private async Task DrainFooterLogDbQueueAsync()
        {
            if (_dbService == null)
                return;

            if (Interlocked.Exchange(ref _footerLogDbDrainRunning, 1) == 1)
                return;

            try
            {
                int processed = 0;
                while (processed < MaxDbWritesPerDrain && _pendingFooterLogDbWrites.TryDequeue(out var logItem))
                {
                    try
                    {
                        await _dbService.SaveFooterLogAsync(logItem.Timestamp, logItem.Message);
                    }
                    catch
                    {
                    }

                    processed++;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _footerLogDbDrainRunning, 0);

                if (!_pendingFooterLogDbWrites.IsEmpty)
                {
                    _ = DrainFooterLogDbQueueAsync();
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

        private void AddAlert(string msg) => EnqueueAlert(msg);

        /// <summary>
        /// [큐 분리] Alert를 큐에 넣고 타이머로 일괄 플러시 (UI 스레드 직접 접근 제거)
        /// </summary>
        public void EnqueueAlert(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;
            _pendingAlerts.Enqueue($"▶ {DateTime.Now:HH:mm:ss} | {msg}");
            // overflow 방지
            while (_pendingAlerts.Count > MaxUiAlertCount * 2)
                _pendingAlerts.TryDequeue(out _);
        }

        private void FlushPendingAlertsToUi()
        {
            if (_pendingAlerts.IsEmpty)
                return;

            // [병목 해결] 일괄 수집 후 역순 삽입
            var batch = new List<string>();
            while (batch.Count < MaxAlertBatchPerTick && _pendingAlerts.TryDequeue(out var alertLine))
            {
                batch.Add(alertLine);
            }

            if (batch.Count > 0)
            {
                for (int i = batch.Count - 1; i >= 0; i--)
                    Alerts.Insert(0, batch[i]);
                while (Alerts.Count > MaxUiAlertCount) Alerts.RemoveAt(Alerts.Count - 1);
            }
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

        private void AddLog(string msg) => EnqueueFooterLog(msg);

        /// <summary>
        /// [큐 분리] 푸터 로그를 큐에 넣고 타이머로 플러시 (외부에서도 호출 가능)
        /// </summary>
        public void EnqueueFooterLog(string msg) => QueueFooterLog(msg);

        /// <summary>
        /// [큐 분리] 라이브 로그를 큐에 넣기 (외부에서도 호출 가능, UI 스레드 직접 접근 제거)
        /// </summary>
        public void EnqueueLiveLog(string msg) => AddLiveLog(msg);

        /// <summary>
        /// [큐 분리] 틱 데이터를 큐에 넣기 (소켓 서비스에서 호출, UI 스레드 접근 X)
        /// </summary>
        public void EnqueueTickerUpdate(string symbol, decimal price, double? pnl)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                return;
            _pendingTickerUpdates[normalizedSymbol] = (price, pnl);
        }

        /// <summary>
        /// AI 진입 확률을 해당 심볼의 MarketDataList 행에 업데이트
        /// </summary>
        private void UpdateAiEntryProb(string symbol, AIEntryForecastResult? forecast)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol) || forecast == null)
                return;

            if (TryNormalizeTradingSymbol(forecast.Symbol, out var normalizedForecastSymbol))
                forecast.Symbol = normalizedForecastSymbol;
            else
                forecast.Symbol = normalizedSymbol;

            _pendingAiEntryProbUpdates[normalizedSymbol] = forecast;
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

                // [추가] 실시간 포지션 수익률 차트 업데이트 (스로틀)
                var nowUtc = DateTime.UtcNow;
                if (nowUtc - _lastActivePnlChartRefresh >= ActivePnlChartRefreshInterval)
                {
                    _lastActivePnlChartRefresh = nowUtc;
                    UpdateActivePnLChart();
                }
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

                if (activePositions.Count > 0)
                {
                    var labels = activePositions.Select(x => x.Symbol).ToArray();

                    // [병목 해결] 기존 Series + ChartValues 재사용 (새 객체 생성 최소화)
                    if (ActivePnLSeries != null && ActivePnLSeries.Count > 0 && ActivePnLSeries[0] is ColumnSeries existingCol
                        && existingCol.Values is ChartValues<double> existingValues)
                    {
                        // in-place 업데이트: 개수가 같으면 값만 교체, 다르면 Clear+AddRange
                        if (existingValues.Count == activePositions.Count)
                        {
                            for (int i = 0; i < activePositions.Count; i++)
                                existingValues[i] = activePositions[i].ProfitPercent;
                        }
                        else
                        {
                            existingValues.Clear();
                            existingValues.AddRange(activePositions.Select(x => x.ProfitPercent));
                        }
                    }
                    else
                    {
                        ActivePnLSeries = new SeriesCollection
                        {
                            new ColumnSeries
                            {
                                Title = "PnL %",
                                Values = new ChartValues<double>(activePositions.Select(x => x.ProfitPercent)),
                                Fill = Brushes.DeepSkyBlue,
                                DataLabels = true
                            }
                        };
                    }

                    UpdateAxisRange(
                        ActivePnLSeries[0].Values as ChartValues<double> ?? new ChartValues<double>(),
                        min => ActivePnLAxisMin = min,
                        max => ActivePnLAxisMax = max,
                        defaultMin: -1d,
                        defaultMax: 1d,
                        requirePositive: false);

                    if (!labels.SequenceEqual(ActivePnLLabels ?? Array.Empty<string>()))
                    {
                        ActivePnLLabels = labels;
                        OnPropertyChanged(nameof(ActivePnLLabels));
                    }
                }
                else
                {
                    if (ActivePnLSeries != null && ActivePnLSeries.Count > 0)
                    {
                        // 빈 상태: 기존 values만 제거
                        if (ActivePnLSeries[0] is ColumnSeries col && col.Values is ChartValues<double> cv && cv.Count > 0)
                            cv.Clear();
                        else
                            ActivePnLSeries = new SeriesCollection();
                    }
                    ActivePnLAxisMin = -1d;
                    ActivePnLAxisMax = 1d;
                }
            });
        }

        private void UpdateSlotStatus(int totalCount, int maxSlots, int majorCount, int unused)
        {
            RunOnUI(() =>
            {
                MajorSlotText = $"Total: {totalCount} / {maxSlots}";
                MajorSlotColor = totalCount >= maxSlots ? Brushes.Orange : Brushes.White;
                PumpSlotText = $"Major: {majorCount}";
                PumpSlotColor = majorCount > 0 ? Brushes.DeepSkyBlue : Brushes.Gray;
                TotalPositionInfo = $"Active: {totalCount} (Major: {majorCount})";
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
            if (signal == null || !TryNormalizeTradingSymbol(signal.Symbol, out var normalizedSymbol))
                return;

            if (!string.Equals(signal.Symbol, normalizedSymbol, StringComparison.Ordinal))
                signal.Symbol = normalizedSymbol;

            _pendingSignalUpdates[normalizedSymbol] = signal;
        }

        private void UpdateTicker(string symbol, decimal price, double? pnl)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                return;

            _pendingTickerUpdates[normalizedSymbol] = (price, pnl);
        }

        private void EnsureSymbolInList(string symbol)
        {
            RunOnUI(() =>
            {
                if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                    return;

                if (_marketDataIndex.ContainsKey(normalizedSymbol) || MarketDataList.Any(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!_marketDataIndex.ContainsKey(normalizedSymbol))
                        _marketDataIndex[normalizedSymbol] = MarketDataList.First(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
                    return;
                }

                var created = new MultiTimeframeViewModel { Symbol = normalizedSymbol };
                MarketDataList.Add(created);
                _marketDataIndex[normalizedSymbol] = created;
                AddLog($"🔍 신규 급등주 감시 리스트 추가: {normalizedSymbol}");
            });
        }

        private void UpdatePositionStatus(string symbol, bool isActive, decimal entryPrice)
        {
            RunOnUI(() =>
            {
                if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                    return;

                var existing = GetOrCreateMarketDataItem(normalizedSymbol);
                if (existing == null)
                    return;
                bool activeChanged = existing.IsPositionActive != isActive;
                existing.IsPositionActive = isActive;
                existing.EntryPrice = entryPrice;
                existing.HasCloseIncomplete = false;
                existing.CloseIncompleteDetail = null;
                if (isActive)
                {
                    existing.ProfitPercent = 0;
                    if (entryPrice > 0)
                    {
                        existing.LastPrice = entryPrice;
                        if (existing.TargetPrice <= 0)
                            existing.TargetPrice = entryPrice * 1.03m;
                        if (existing.StopLossPrice <= 0)
                            existing.StopLossPrice = entryPrice * 0.985m;
                    }
                }
                else
                {
                    existing.Decision = "WAIT";
                    existing.ProfitPercent = 0;
                    existing.TargetPrice = 0;
                    existing.StopLossPrice = 0;
                }

                string resolvedEntryStatus = ResolveEntryStatus(existing.SignalSource, existing.Decision, existing.IsPositionActive);
                if (!string.Equals(existing.EntryStatus, resolvedEntryStatus, StringComparison.Ordinal))
                    existing.EntryStatus = resolvedEntryStatus;

                if (activeChanged)
                    ConfigureMarketDataSorting();
            });
        }

        private void UpdateCloseIncompleteStatus(string symbol, bool isIncomplete, string? detail)
        {
            RunOnUI(() =>
            {
                if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                    return;

                var existing = GetOrCreateMarketDataItem(normalizedSymbol);
                if (existing == null)
                    return;

                existing.HasCloseIncomplete = isIncomplete;
                existing.CloseIncompleteDetail = detail;
            });
        }

        private void UpdateExternalSyncStatus(string symbol, string? status, string? detail)
        {
            RunOnUI(() =>
            {
                if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                    return;

                var existing = GetOrCreateMarketDataItem(normalizedSymbol);
                if (existing == null)
                    return;

                existing.ExternalSyncStatus = status;
                existing.ExternalSyncDetail = detail;
            });
        }

        public void RefreshLiveChart()
        {
            _liveChartCts?.Cancel();
            _liveChartCts = new CancellationTokenSource();
            _ = LoadLiveChartDataAsync(_liveChartCts.Token);
        }

        private async Task LoadLiveChartDataAsync(CancellationToken ct = default)
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
                    limit: 20, // 최근 20개 캔들 (요청사항)
                    ct: ct
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
                DateTime lastKlineTime = DateTime.Now;
                foreach (var kline in klinesResult.Data)
                {
                    var close = ToFinite((double)kline.ClosePrice, lastValidClose ?? double.NaN);
                    if (!IsFinite(close) || close <= 0)
                        continue;

                    lastValidClose = close;
                    closeValues.Add(close);
                    lastKlineTime = kline.OpenTime.ToLocalTime();
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

                // ── 1H 예측: 선형 회귀로 12개 5분봉(=1시간) 추가 ────────────────
                int nPts = closeValues.Count;
                double sx = 0, sy = 0, sxy = 0, sx2 = 0;
                for (int i = 0; i < nPts; i++)
                {
                    double ci = closeValues[i];
                    sx += i; sy += ci; sxy += i * ci; sx2 += i * i;
                }
                double denom = nPts * sx2 - sx * sx;
                double linSlope = denom != 0 ? (nPts * sxy - sx * sy) / denom : 0;
                double linBase  = (sy / nPts) - linSlope * (sx / nPts);

                // NaN 패딩으로 역사 구간 건너뜀, 마지막 종가에서 이어지는 선으로 표시
                var forecastValues = new ChartValues<double>();
                for (int i = 0; i < nPts - 1; i++) forecastValues.Add(double.NaN);
                forecastValues.Add(closeValues[nPts - 1]); // anchor
                for (int i = 1; i <= 12; i++)
                {
                    double fp = ToFinite(linBase + linSlope * (nPts - 1 + i), closeValues[nPts - 1]);
                    fp = Math.Max(fp, 0);
                    forecastValues.Add(fp);
                    xLabels.Add(lastKlineTime.AddMinutes(i * 5).ToString("HH:mm"));
                }

                // ── 포지션 방향 및 가격 감지 ─────────────────────────────────────
                var sym = SelectedSymbol;
                string positionSide = sym?.PositionSide ?? "";
                if (string.IsNullOrEmpty(positionSide))
                    positionSide = sym?.Decision ?? "";
                bool isLong  = positionSide.Contains("LONG",  StringComparison.OrdinalIgnoreCase);
                bool isShort = positionSide.Contains("SHORT", StringComparison.OrdinalIgnoreCase);
                double entryP = (sym != null && (isLong || isShort)) ? ToFinite((double)sym.EntryPrice)   : double.NaN;
                double tpP    = (sym != null && (isLong || isShort)) ? ToFinite((double)sym.TargetPrice)  : double.NaN;
                double slP    = (sym != null && (isLong || isShort)) ? ToFinite((double)sym.StopLossPrice): double.NaN;

                // ── 진입타점 by SELL: Decision==SHORT 이면 마지막 봉 가격에 마커 ──
                var sellEntryValues = new ChartValues<ScatterPoint>();
                string? currentDecision = sym?.Decision;
                if (currentDecision?.Contains("SHORT", StringComparison.OrdinalIgnoreCase) == true)
                {
                    double ep = closeValues[nPts - 1];
                    if (IsFinite(ep) && ep > 0)
                        sellEntryValues.Add(new ScatterPoint(nPts - 1, ep, 12));
                }

                // 예측값 + 포지션 가격들 포함하여 Y 축 범위 계산
                var forecastFinite = forecastValues.Where(v => IsFinite(v) && v > 0);
                var positionPrices = new[] { entryP, tpP, slP }.Where(p => IsFinite(p) && p > 0);
                UpdateAxisRange(
                    closeValues.Concat(forecastFinite).Concat(positionPrices),
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

                string dirLabel  = isLong ? "LONG" : "SHORT";
                var entryBrush   = isLong ? Brushes.LimeGreen : Brushes.OrangeRed;
                int totalPts     = nPts + 13;

                var series = new SeriesCollection
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
                        Title = "▲ LONG 진입",
                        Values = new ChartValues<ScatterPoint>(),
                        PointGeometry = DefaultGeometries.Triangle,
                        Fill = Brushes.LimeGreen,
                        MinPointShapeDiameter = 15,
                        DataLabels = false
                    },
                    new ScatterSeries
                    {
                        Title = "▼ SHORT 진입",
                        Values = sellEntryValues,
                        PointGeometry = DefaultGeometries.Triangle,
                        Fill = Brushes.OrangeRed,
                        MinPointShapeDiameter = 18,
                        DataLabels = false
                    },
                    new LineSeries
                    {
                        Title = "1H 예측",
                        Values = forecastValues,
                        PointGeometry = null,
                        LineSmoothness = 0.3,
                        StrokeThickness = 1.5,
                        Stroke = Brushes.Orange,
                        Fill = Brushes.Transparent
                    }
                };

                // ── 진입가 / 익절가 / 청산가 수평선 ──────────────────────────────
                if (sym != null && (isLong || isShort))
                {
                    if (IsFinite(entryP) && entryP > 0)
                    {
                        var line = new ChartValues<double>();
                        for (int i = 0; i < totalPts; i++) line.Add(entryP);
                        series.Add(new LineSeries
                        {
                            Title = $"▶ {dirLabel} 진입  {entryP:N4}",
                            Values = line,
                            PointGeometry = null,
                            LineSmoothness = 0,
                            StrokeThickness = 1.2,
                            Stroke = entryBrush,
                            Fill = Brushes.Transparent
                        });
                    }

                    if (IsFinite(tpP) && tpP > 0)
                    {
                        var line = new ChartValues<double>();
                        for (int i = 0; i < totalPts; i++) line.Add(tpP);
                        series.Add(new LineSeries
                        {
                            Title = $"💰 {dirLabel} 익절  {tpP:N4}",
                            Values = line,
                            PointGeometry = null,
                            LineSmoothness = 0,
                            StrokeThickness = 1.2,
                            Stroke = Brushes.Cyan,
                            Fill = Brushes.Transparent
                        });
                    }

                    if (IsFinite(slP) && slP > 0)
                    {
                        var line = new ChartValues<double>();
                        for (int i = 0; i < totalPts; i++) line.Add(slP);
                        series.Add(new LineSeries
                        {
                            Title = $"✕ {dirLabel} 청산  {slP:N4}",
                            Values = line,
                            PointGeometry = null,
                            LineSmoothness = 0,
                            StrokeThickness = 1.2,
                            Stroke = Brushes.Tomato,
                            Fill = Brushes.Transparent
                        });
                    }
                }

                // ── AI 진입 예상 지점 마커 ────────────────────────────────────────
                if (sym != null && sym.AiEntryProb >= 0)
                {
                    int offsetMin = sym.AiEntryForecastOffsetMinutes ?? 0;
                    // 5분봉 기준 x 인덱스: 즉시(0~1분)는 마지막 봉, 이후는 예측 구간
                    double forecastXd = offsetMin <= 1
                        ? nPts - 1
                        : nPts - 1 + offsetMin / 5.0;
                    int forecastXi = (int)Math.Round(forecastXd);
                    forecastXi = Math.Max(0, Math.Min(forecastXi, nPts + 12));

                    // 해당 위치의 예측 가격 (선형 회귀)
                    double forecastY = ToFinite(linBase + linSlope * forecastXd, closeValues[nPts - 1]);
                    forecastY = Math.Max(forecastY, 0);

                    if (IsFinite(forecastY) && forecastY > 0)
                    {
                        string timeLabel = offsetMin <= 1
                            ? "즉시"
                            : (sym.AiEntryForecastTime.HasValue
                                ? sym.AiEntryForecastTime.Value.ToString("HH:mm")
                                : $"+{offsetMin}m");

                        series.Add(new ScatterSeries
                        {
                            Title = $"🎯 AI 진입예상  {timeLabel}  ({sym.AiEntryProb:P0})",
                            Values = new ChartValues<ScatterPoint>
                            {
                                new ScatterPoint(forecastXi, forecastY, 14)
                            },
                            PointGeometry = DefaultGeometries.Diamond,
                            Fill = Brushes.Gold,
                            DataLabels = false
                        });
                    }
                }

                LiveChartSeries = series;
                
                OnPropertyChanged(nameof(LiveChartSeries));
                OnPropertyChanged(nameof(LiveChartXFormatter));
                OnPropertyChanged(nameof(LiveChartYFormatter));
                OnPropertyChanged(nameof(IsLiveChartEmpty));
            }
            catch (OperationCanceledException)
            {
                // [병목 해결] SelectedSymbol 빈번 변경 시 이전 요청이 취소된 경우 무시
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

    public class BattleExecutionStep : INotifyPropertyChanged
    {
        public BattleExecutionStep(string order, string title)
        {
            Order = order;
            Title = title;
        }

        public string Order { get; }
        public string Title { get; }

        private string _detail = "대기";
        public string Detail
        {
            get => _detail;
            set
            {
                if (_detail == value) return;
                _detail = value;
                OnPropertyChanged();
            }
        }

        private string _stateText = "대기";
        public string StateText
        {
            get => _stateText;
            set
            {
                if (_stateText == value) return;
                _stateText = value;
                OnPropertyChanged();
            }
        }

        private Brush _stateBrush = Brushes.LightGray;
        public Brush StateBrush
        {
            get => _stateBrush;
            set
            {
                if (_stateBrush == value) return;
                _stateBrush = value;
                OnPropertyChanged();
            }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
