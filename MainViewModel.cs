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
        // [AI 제거] _pendingAiEntryProbUpdates 제거
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
        // [데이터 컨플레이션] Dispatcher 처리 중 재진입 방지 플래그
        private int _tickerFlushRunning;
        // [Stage2] 적응형 타이머 간격 — 부하에 따라 ticker flush 주기 자동 조절
        private int _tickerFlushIntervalMs = 300; // [v5.10.7] 200→300ms: 초기값을 최소값과 일치
        private const int TickerFlushMinMs = 300; // [v5.10.3] 100→300ms: CPU 과부하 방지
        private const int TickerFlushMaxMs = 500;
        private readonly Queue<int> _tickerFlushDurationSamples = new();
        private const int TickerFlushSampleWindow = 30;
        private DateTime _nextTickerTuneTime = DateTime.UtcNow.AddSeconds(15);
        private int _liveLogDbDrainRunning;
        private int _footerLogDbDrainRunning;
        private const int MaxBufferedLiveLogs = 1200;
        private const int MaxUiLiveLogCount = 200;
        private const int MaxLiveLogBatchPerTick = 80;       // [병목 해결] 24→80 (1초 딜레이 해결)
        private const int MaxLiveLogUiWorkBudgetMsPerTick = 12;
        private const int MaxTickerBatchPerTick = 80;        // [병목 해결] 180→80 (PropertyChanged 폭주 방지)
        private const int MaxSignalBatchPerTick = 40;        // [병목 해결] 120→40 (BeginUpdate로 보완)
        private const int MaxDbWritesPerDrain = 80;
        private const int FooterLogFlushIntervalMs = 500; // [v5.10.7] 200→500ms: CPU 절감
        private const int MaxUiWorkBudgetMsPerTick = 6;      // [병목 해결] 8→6ms (UI 스레드 여유 확보)
        private const int MaxAlertBatchPerTick = 20;
        private const int MaxUiAlertCount = 100;
        private const int LiveLogBackpressureSoftThreshold = 700;
        private const int LiveLogBackpressureHardThreshold = 1200;
        private const int LiveLogBackpressureNoticeIntervalSec = 10;
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
        private int _liveLogBackpressureDropped;
        private DateTime _nextLiveLogBackpressureNoticeUtc = DateTime.MinValue;
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
        public ObservableCollection<string> FastLogs { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<TradeLog> TradeHistory { get; set; } = new ObservableCollection<TradeLog>();

        // [v3.2.49] Performance 탭
        public SeriesCollection PerformanceSeries { get; set; } = new SeriesCollection();
        public string[] PerformanceLabels { get; set; } = Array.Empty<string>();
        public ObservableCollection<DayPnlEntry> CalendarEntries { get; set; } = new ObservableCollection<DayPnlEntry>();
        private string _performancePeriod = "일별";
        public string PerformancePeriod { get => _performancePeriod; set { _performancePeriod = value; OnPropertyChanged(); _ = LoadPerformanceDataAsync(); } }
        private string _performanceSummary = "";
        public string PerformanceSummary { get => _performanceSummary; set { _performanceSummary = value; OnPropertyChanged(); } }
        public Func<double, string> YFormatter { get; set; } = val => $"${val:N0}";

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

        // [WaveAI] 실시간 ML.NET 점수
        private string _waveMLScoreText = "ML: --%";
        public string WaveMLScoreText
        {
            get => _waveMLScoreText;
            set { _waveMLScoreText = value; OnPropertyChanged(); }
        }

        private string _waveStatusText = "대기 중";
        public string WaveStatusText
        {
            get => _waveStatusText;
            set { _waveStatusText = value; OnPropertyChanged(); }
        }

        // [v4.9.0] AI Insight Panel — Top Candidates + Focused Position + Detect Health
        public ObservableCollection<TradingBot.Models.CandidateItem> TopCandidates { get; }
            = new ObservableCollection<TradingBot.Models.CandidateItem>();

        private TradingBot.Models.PositionDetailViewModel? _focusedPosition;
        public TradingBot.Models.PositionDetailViewModel? FocusedPosition
        {
            get => _focusedPosition;
            set { _focusedPosition = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFocusedPosition)); }
        }
        public bool HasFocusedPosition => _focusedPosition != null;

        // [v4.9.8] 모든 활성 포지션을 동시에 표시 (기존 FocusedPosition은 1개만 표시하는 버그 수정)
        public ObservableCollection<TradingBot.Models.PositionDetailViewModel> ActivePositions { get; }
            = new ObservableCollection<TradingBot.Models.PositionDetailViewModel>();
        public bool HasAnyActivePosition => ActivePositions.Count > 0;

        public TradingBot.Models.DetectHealthViewModel DetectHealth { get; }
            = new TradingBot.Models.DetectHealthViewModel();

        // [v5.0.3] 카테고리별 오늘(KST 00:00 기준) 통계 카드
        public TradingBot.Models.CategoryStatsViewModel MajorStats { get; }
            = new TradingBot.Models.CategoryStatsViewModel { Category = "MAJOR", Icon = "💎", Title = "MAJOR" };
        public TradingBot.Models.CategoryStatsViewModel PumpStats { get; }
            = new TradingBot.Models.CategoryStatsViewModel { Category = "PUMP", Icon = "🚀", Title = "PUMP" };
        public TradingBot.Models.CategoryStatsViewModel SpikeStats { get; }
            = new TradingBot.Models.CategoryStatsViewModel { Category = "SPIKE", Icon = "⚡", Title = "SPIKE" };
        // [v5.21.3] SQUEEZE 카테고리 — SPIKE 차단 후 메인 수익원 (90일 +$30K, WR 94%)
        public TradingBot.Models.CategoryStatsViewModel SqueezeStats { get; }
            = new TradingBot.Models.CategoryStatsViewModel { Category = "SQUEEZE", Icon = "📊", Title = "SQUEEZE" };
        private System.Threading.Timer? _categoryStatsTimer;

        // [v5.22.13] 초기학습 진행 배너 — 모든 필드/프로퍼티 통째 제거 (AI 시스템 폐기, 2026-04-29)

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

        // ─── AI Prediction Summary 카드 ──────────────
        private double _battleMLConfidence;
        public double BattleMLConfidence
        {
            get => _battleMLConfidence;
            set { _battleMLConfidence = value; OnPropertyChanged(); }
        }

        // ─── SSA 시계열 예측 밴드 ──────────────────────────
        private float _ssaUpperBound;
        public float SsaUpperBound
        {
            get => _ssaUpperBound;
            set { _ssaUpperBound = value; OnPropertyChanged(); }
        }

        private float _ssaLowerBound;
        public float SsaLowerBound
        {
            get => _ssaLowerBound;
            set { _ssaLowerBound = value; OnPropertyChanged(); }
        }

        private float _ssaForecastPrice;
        public float SsaForecastPrice
        {
            get => _ssaForecastPrice;
            set { _ssaForecastPrice = value; OnPropertyChanged(); }
        }

        private string _battleBBPositionText = "Position: --";
        public string BattleBBPositionText
        {
            get => _battleBBPositionText;
            set { _battleBBPositionText = value; OnPropertyChanged(); }
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

        private string _battleFastLog4 = "";
        public string BattleFastLog4
        {
            get => _battleFastLog4;
            set { _battleFastLog4 = value; OnPropertyChanged(); }
        }

        private string _battleFastLog5 = "";
        public string BattleFastLog5
        {
            get => _battleFastLog5;
            set { _battleFastLog5 = value; OnPropertyChanged(); }
        }

        private string _battleFastLog6 = "";
        public string BattleFastLog6 { get => _battleFastLog6; set { _battleFastLog6 = value; OnPropertyChanged(); } }
        private string _battleFastLog7 = "";
        public string BattleFastLog7 { get => _battleFastLog7; set { _battleFastLog7 = value; OnPropertyChanged(); } }
        private string _battleFastLog8 = "";
        public string BattleFastLog8 { get => _battleFastLog8; set { _battleFastLog8 = value; OnPropertyChanged(); } }

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

        private bool _battleBbSupportActive;
        public bool BattleBbSupportActive
        {
            get => _battleBbSupportActive;
            set { _battleBbSupportActive = value; OnPropertyChanged(); }
        }

        private string _battleBbSupportText = "BB Mid Support: 대기";
        public string BattleBbSupportText
        {
            get => _battleBbSupportText;
            set { _battleBbSupportText = value; OnPropertyChanged(); }
        }

        private Brush _battleBbSupportBrush = Brushes.LightGray;
        public Brush BattleBbSupportBrush
        {
            get => _battleBbSupportBrush;
            set { _battleBbSupportBrush = value; OnPropertyChanged(); }
        }

        // ── [Trend-Rider] 계단식 추세 지속 위젯 ────────────────────────────────
        private bool _battleTrendRiderActive;
        public bool BattleTrendRiderActive
        {
            get => _battleTrendRiderActive;
            set { _battleTrendRiderActive = value; OnPropertyChanged(); }
        }

        private string _battleTrendRiderText = "TREND: 대기";
        public string BattleTrendRiderText
        {
            get => _battleTrendRiderText;
            set { _battleTrendRiderText = value; OnPropertyChanged(); }
        }

        private Brush _battleTrendRiderBrush = Brushes.LightGray;
        public Brush BattleTrendRiderBrush
        {
            get => _battleTrendRiderBrush;
            set { _battleTrendRiderBrush = value; OnPropertyChanged(); }
        }

        private string _battleTrendRiderActionText = "Action: 대기 중";
        public string BattleTrendRiderActionText
        {
            get => _battleTrendRiderActionText;
            set { _battleTrendRiderActionText = value; OnPropertyChanged(); }
        }

        private string _battleWeightingText = "가중치: ML 50 | TF 50";
        public string BattleWeightingText
        {
            get => _battleWeightingText;
            set { _battleWeightingText = value; OnPropertyChanged(); }
        }

        private const double MonthlyGoalTargetUsd = 10000d;

        public string MonthlyGoalTargetText => $"MONTHLY GOAL: ${MonthlyGoalTargetUsd:N0}";

        private double _monthlyGoalCurrentUsd;
        public double MonthlyGoalCurrentUsd
        {
            get => _monthlyGoalCurrentUsd;
            set
            {
                _monthlyGoalCurrentUsd = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MonthlyGoalCurrentText));
            }
        }

        private double _monthlyGoalProgressPercent;
        public double MonthlyGoalProgressPercent
        {
            get => _monthlyGoalProgressPercent;
            set
            {
                _monthlyGoalProgressPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MonthlyGoalCurrentText));
            }
        }

        public string MonthlyGoalCurrentText => $"Current: ${MonthlyGoalCurrentUsd:N0} ({MonthlyGoalProgressPercent:F1}%)";

        private string _monthlyGoalPaceText = "Daily Pace: 대기";
        public string MonthlyGoalPaceText
        {
            get => _monthlyGoalPaceText;
            set { _monthlyGoalPaceText = value; OnPropertyChanged(); }
        }

        private Brush _monthlyGoalPaceBrush = Brushes.LightGray;
        public Brush MonthlyGoalPaceBrush
        {
            get => _monthlyGoalPaceBrush;
            set { _monthlyGoalPaceBrush = value; OnPropertyChanged(); }
        }

        private string _monthlyGoalXrpBonusText = "XRP Scenario Bonus: 계산 대기";
        public string MonthlyGoalXrpBonusText
        {
            get => _monthlyGoalXrpBonusText;
            set { _monthlyGoalXrpBonusText = value; OnPropertyChanged(); }
        }

        private Brush _monthlyGoalXrpBonusBrush = Brushes.LightGray;
        public Brush MonthlyGoalXrpBonusBrush
        {
            get => _monthlyGoalXrpBonusBrush;
            set { _monthlyGoalXrpBonusBrush = value; OnPropertyChanged(); }
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

        private string _netTransferInfo = "";
        public string NetTransferInfo { get => _netTransferInfo; set { _netTransferInfo = value; OnPropertyChanged(); } }

        private Brush _netTransferColor = Brushes.White;
        public Brush NetTransferColor { get => _netTransferColor; set { _netTransferColor = value; OnPropertyChanged(); } }

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
        public ICommand ManualLongCommand { get; private set; }
        public ICommand ManualShortCommand { get; private set; }
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

            // [v5.22.17] 메이저 4개 (BTC/ETH/SOL/XRP) 강제 prefill — 봇 가동 직후 가격 도착 전이라도
            //   "실시간 시장 신호" DataGrid 가 빈 상태로 보이지 않도록 placeholder row 미리 생성.
            //   이후 OnTickerUpdate 도착 시 GetOrCreateMarketDataItem 이 동일 row 를 재사용하여 LastPrice/ProfitPercent 갱신.
            try
            {
                foreach (var sym in new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" })
                {
                    if (!_marketDataIndex.ContainsKey(sym))
                    {
                        var placeholder = new MultiTimeframeViewModel { Symbol = sym };
                        placeholder.EntryStatus = ResolveEntryStatus(placeholder.SignalSource, placeholder.Decision, placeholder.IsPositionActive);
                        MarketDataList.Add(placeholder);
                        _marketDataIndex[sym] = placeholder;
                    }
                }
            }
            catch { /* 초기화 실패해도 정상 흐름은 OnTickerUpdate 도착 시 자동 추가됨 */ }

            // [v4.4.2] 퍼포먼스 탭 초기 로드
            _ = LoadPerformanceDataAsync();

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

            // [v5.0.3] 카테고리별 오늘 통계 5분 타이머 (시작 5초 후 첫 호출)
            if (!DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            {
                _categoryStatsTimer = new System.Threading.Timer(
                    async _ => await RefreshCategoryStatsAsync(),
                    null,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMinutes(5));
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
                bool hasTestnetKey = !string.IsNullOrWhiteSpace(AppConfig.Current?.Trading?.TestnetApiKey);
                bool startCancelled = false;

                if (MainWindow.Instance != null)
                {
                    var txtAccountMode = MainWindow.Instance.FindName("txtAccountMode") as System.Windows.Controls.TextBlock;
                    if (txtAccountMode != null)
                        txtAccountMode.Text = isSimulation ? "🎮 SIMULATION" : "";
                }

                if (isSimulation)
                {
                    string mode = hasTestnetKey ? "바이낸스 테스트넷" : "MockExchange";
                    var result = System.Windows.MessageBox.Show(
                        $"🎮 시뮬레이션 모드로 시작합니다.\n\n" +
                        $"모드: {mode}\n" +
                        $"초기 잔고: ${simBalance:N2}\n" +
                        $"실제 자금 사용: 없음\n\n" +
                        (hasTestnetKey
                            ? "테스트넷 연결 — 실거래와 동일한 체결/DB저장"
                            : "MockExchange — 가상 체결") +
                        "\n\n계속 진행하시겠습니까?",
                        "시뮬레이션 모드 확인",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Information);

                    if (result == System.Windows.MessageBoxResult.No)
                    {
                        IsStartEnabled = true;
                        IsStopEnabled = false;
                        AddLog("시뮬레이션 시작 취소됨");
                        startCancelled = true;
                    }
                    else
                    {
                        AddLog($"🎮 [Start] 시뮬레이션 모드 ({mode}) | 잔고: ${simBalance:N2}");
                    }
                }
                else
                {
                    AddLog($"💰 [Start] 실거래 모드로 시작합니다.");
                }

                if (startCancelled) return;

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
                            // [v5.19.9] 청산 + DB 갱신 모두 background — UI 즉시 응답 (사용자 체감 ~50ms)
                            //   기존: ClosePositionAsync(5초 timeout) + LoadTradeHistory(1-3초) 순차 await → UI 5-8초 블록
                            //   수정: 둘 다 fire-and-forget. 청산 결과는 텔레그램 + 인메모리 _activePositions 즉시 반영됨
                            AddLog($"⚡ {symbol} 수동 청산 요청 전송 — 결과는 텔레그램/포지션패널 확인");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _engine.ClosePositionAsync(symbol);
                                    await LoadTradeHistory();
                                }
                                catch (Exception ex)
                                {
                                    Application.Current?.Dispatcher.Invoke(() =>
                                        AddLog($"⚠️ {symbol} 청산 백그라운드 실패: {ex.Message}"));
                                }
                            });
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

            // [수동 진입] LONG/SHORT 버튼 커맨드
            ManualLongCommand = new RelayCommand(async _ =>
            {
                await ExecuteManualEntry("LONG");
            });

            ManualShortCommand = new RelayCommand(async _ =>
            {
                await ExecuteManualEntry("SHORT");
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

        public async Task LoadPerformanceDataAsync()
        {
            try
            {
                int userId = AppConfig.CurrentUser?.Id ?? 0;
                if (userId <= 0) return;

                var db = new DbManager(AppConfig.ConnectionString);
                DateTime start, end = DateTime.Now;

                if (_performancePeriod == "월별")
                    start = end.AddMonths(-12);
                else if (_performancePeriod == "주별")
                    start = end.AddMonths(-3);
                else
                    start = end.AddDays(-30);

                var trades = await db.GetTradeHistoryAsync(userId, start, end, 5000);
                var closed = trades.Where(t => t.PnL != 0 && t.ExitTime > DateTime.MinValue).ToList();

                // 일별/주별/월별 집계
                var grouped = _performancePeriod switch
                {
                    "월별" => closed.GroupBy(t => t.ExitTime.ToString("yyyy-MM")).OrderBy(g => g.Key),
                    "주별" => closed.GroupBy(t =>
                    {
                        var cal = System.Globalization.CultureInfo.CurrentCulture.Calendar;
                        int week = cal.GetWeekOfYear(t.ExitTime, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Monday);
                        return $"{t.ExitTime:yyyy}-W{week:D2}";
                    }).OrderBy(g => g.Key),
                    _ => closed.GroupBy(t => t.ExitTime.ToString("MM/dd")).OrderBy(g => g.Key)
                };

                // 일별 그룹 데이터를 Dictionary로
                var dailyPnl = new Dictionary<string, (decimal pnl, int count)>();
                foreach (var g in grouped)
                {
                    dailyPnl[g.Key] = (g.Sum(t => t.PnL), g.Count());
                }

                // [v3.3.1] 빈 날짜 0원으로 채우기 (달력 형태)
                var labels = new List<string>();
                var pnlValues = new ChartValues<double>();
                var calEntries = new List<DayPnlEntry>();
                decimal totalPnl = 0;
                int winCount = 0, totalCount = 0, dayCount = 0;

                if (_performancePeriod == "일별")
                {
                    for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
                    {
                        string key = d.ToString("MM/dd");
                        decimal pnl = 0; int cnt = 0;
                        if (dailyPnl.TryGetValue(key, out var data)) { pnl = data.pnl; cnt = data.count; }

                        totalPnl += pnl; totalCount += cnt; dayCount++;
                        if (pnl > 0) winCount++;

                        labels.Add(key);
                        pnlValues.Add((double)pnl);
                        calEntries.Add(new DayPnlEntry { Label = key, Date = d, PnlUsdt = pnl, TradeCount = cnt, IsProfit = pnl >= 0 });
                    }
                }
                else
                {
                    foreach (var kvp in dailyPnl.OrderBy(k => k.Key))
                    {
                        totalPnl += kvp.Value.pnl; totalCount += kvp.Value.count; dayCount++;
                        if (kvp.Value.pnl > 0) winCount++;
                        labels.Add(kvp.Key);
                        pnlValues.Add((double)kvp.Value.pnl);
                        calEntries.Add(new DayPnlEntry { Label = kvp.Key, PnlUsdt = kvp.Value.pnl, TradeCount = kvp.Value.count, IsProfit = kvp.Value.pnl >= 0 });
                    }
                }

                double winRate = dayCount > 0 ? (double)winCount / dayCount * 100 : 0;

                RunOnUI(() =>
                {
                    // [v3.7.0] 수익/손실 각각 다른 색 + 그라데이션
                    var profitValues = new LiveCharts.ChartValues<double>();
                    var lossValues = new LiveCharts.ChartValues<double>();
                    foreach (var v in pnlValues)
                    {
                        profitValues.Add(v > 0 ? v : 0);
                        lossValues.Add(v < 0 ? v : 0);
                    }
                    PerformanceSeries = new SeriesCollection
                    {
                        new ColumnSeries
                        {
                            Title = "Profit",
                            Values = profitValues,
                            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xE6, 0x76)),
                            StrokeThickness = 0,
                            ColumnPadding = 2,
                            MaxColumnWidth = 60,
                            DataLabels = true,
                            LabelPoint = p => p.Y != 0 ? $"${p.Y:N0}" : "",
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7E, 0xE7, 0x87)),
                            FontSize = 9,
                            FontWeight = System.Windows.FontWeights.Bold
                        },
                        new ColumnSeries
                        {
                            Title = "Loss",
                            Values = lossValues,
                            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x53, 0x70)),
                            StrokeThickness = 0,
                            ColumnPadding = 2,
                            MaxColumnWidth = 60,
                            DataLabels = true,
                            LabelPoint = p => p.Y != 0 ? $"${p.Y:N0}" : "",
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7B, 0x72)),
                            FontSize = 9,
                            FontWeight = System.Windows.FontWeights.Bold
                        }
                    };
                    OnPropertyChanged(nameof(PerformanceSeries));

                    PerformanceLabels = labels.ToArray();
                    OnPropertyChanged(nameof(PerformanceLabels));

                    CalendarEntries = new ObservableCollection<DayPnlEntry>(calEntries);
                    OnPropertyChanged(nameof(CalendarEntries));

                    PerformanceSummary = $"총 PnL: ${totalPnl:+#,##0.00;-#,##0.00} | 승률: {winRate:F0}% ({winCount}/{labels.Count}) | 거래: {totalCount}건";
                });
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Performance 로드 실패: {ex.Message}");
            }
        }

        private void CalculateTradeStatistics()
        {
            if (TradeHistory == null || TradeHistory.Count == 0)
            {
                WinRate = 0;
                TotalProfit = 0;
                AverageRoe = 0;
                UpdateMonthlyGoalTracker(0d);
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
                UpdateMonthlyGoalTracker(0d);
                return;
            }

            var profitableTrades = closedTrades.Where(t => t.PnL > 0).ToList();
            WinRate = (double)profitableTrades.Count / closedTrades.Count * 100;
            TotalProfit = (double)closedTrades.Sum(t => t.PnL);
            AverageRoe = (double)closedTrades.Average(t => t.PnLPercent);

            DateTime now = DateTime.Now;
            DateTime monthStart = new DateTime(now.Year, now.Month, 1);
            double monthlyClosedProfit = (double)closedTrades
                .Where(t =>
                {
                    DateTime refTime = ResolveTradeReferenceTime(t);
                    return refTime >= monthStart && refTime <= now;
                })
                .Sum(t => t.PnL);

            UpdateMonthlyGoalTracker(monthlyClosedProfit);
        }

        private static DateTime ResolveTradeReferenceTime(TradeLog trade)
        {
            if (trade.ExitTime > DateTime.MinValue)
                return trade.ExitTime;
            if (trade.Time > DateTime.MinValue)
                return trade.Time;
            if (trade.EntryTime > DateTime.MinValue)
                return trade.EntryTime;
            return DateTime.MinValue;
        }

        private void UpdateMonthlyGoalTracker(double? monthlyClosedProfit = null)
        {
            if (monthlyClosedProfit.HasValue)
            {
                MonthlyGoalCurrentUsd = monthlyClosedProfit.Value;
            }

            double progress = MonthlyGoalTargetUsd <= 0d
                ? 0d
                : Math.Clamp((MonthlyGoalCurrentUsd / MonthlyGoalTargetUsd) * 100d, 0d, 999d);
            MonthlyGoalProgressPercent = progress;

            int daysInMonth = DateTime.DaysInMonth(DateTime.Now.Year, DateTime.Now.Month);
            double expectedProfitByToday = MonthlyGoalTargetUsd * DateTime.Now.Day / daysInMonth;

            bool onTrack = MonthlyGoalCurrentUsd >= expectedProfitByToday;
            MonthlyGoalPaceText = $"Daily Pace: {(onTrack ? "On Track" : "Below Pace")}";
            MonthlyGoalPaceBrush = onTrack ? Brushes.LimeGreen : Brushes.Gold;

            UpdateMonthlyGoalXrpScenarioBonus();
        }

        private void UpdateMonthlyGoalXrpScenarioBonus()
        {
            var xrpVm = FindMarketDataItem("XRPUSDT");
            if (xrpVm == null ||
                !xrpVm.IsPositionActive ||
                xrpVm.LastPrice <= 0m ||
                xrpVm.TargetPrice <= 0m ||
                xrpVm.Quantity <= 0m)
            {
                MonthlyGoalXrpBonusText = "XRP Scenario Bonus: 계산 대기";
                MonthlyGoalXrpBonusBrush = Brushes.LightGray;
                return;
            }

            bool isShort = string.Equals(xrpVm.PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase);
            decimal remainingMove = isShort
                ? xrpVm.LastPrice - xrpVm.TargetPrice
                : xrpVm.TargetPrice - xrpVm.LastPrice;
            decimal expectedBonus = remainingMove * xrpVm.Quantity;

            if (expectedBonus > 0m)
            {
                MonthlyGoalXrpBonusText = $"XRP Scenario Bonus: +${expectedBonus:N2} (TP 기준)";
                MonthlyGoalXrpBonusBrush = Brushes.DeepSkyBlue;
                return;
            }

            MonthlyGoalXrpBonusText = "XRP Scenario Bonus: 목표가 재확인 필요";
            MonthlyGoalXrpBonusBrush = Brushes.Gold;
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
            _engine.OnTrailingStopPriceUpdate += (symbol, price) => UpdateTrailingStopPrice(symbol, price);

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

            // [AI 제거] OnAiEntryProbUpdate 이벤트 구독 제거

            // [AI Command Center] 상태 업데이트
            _engine.OnAiCommandUpdate += (symbol, confidence, direction, h4, h1, m15, bull, bear) =>
            {
                RunOnUI(() => UpdateAiCommandState(symbol, confidence, direction, h4, h1, m15, bull, bear));
            };

            // [Entry Pipeline] Block Reason / Volume Gauge / Daily Profit / Price Energy
            _engine.OnBlockReasonUpdate += reason => RunOnUI(() => UpdateBlockReason(reason));
            _engine.OnVolumeGaugeUpdate += pct => RunOnUI(() => UpdateVolumeGauge(pct));
            _engine.OnDailyProfitUpdate += profit => RunOnUI(() => UpdateDailyProfit(profit));
            _engine.OnPriceProgressUpdate += (bbPos, mlConf, tfConvDiv) => RunOnUI(() => UpdatePriceProgress(bbPos, mlConf, tfConvDiv));
            _engine.OnExitScoreUpdate += (sym, score, prob, stop) => RunOnUI(() => UpdateExitScore(sym, score, prob, stop));

            // [SSA] 시계열 예측 밴드 → SkiaSharp 차트 전달
            _engine.OnSsaForecastUpdate += (upper, lower, forecast) => RunOnUI(() =>
            {
                SsaUpperBound = upper;
                SsaLowerBound = lower;
                SsaForecastPrice = forecast;
            });

            // [WaveAI] ML.NET 점수 업데이트 구독
            _engine.OnWaveAIScoreUpdate += (symbol, mlScore, _, status) =>
            {
                RunOnUI(() =>
                {
                    if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                        return;

                    WaveMLScoreText = mlScore <= 0 ? "ML: 대기" : $"ML: {mlScore:P0}";
                    WaveStatusText = status;

                    var symbolVm = MarketDataList.FirstOrDefault(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
                    if (symbolVm != null)
                    {
                        symbolVm.MLProbability = mlScore;
                    }
                });
            };

            // [v5.22.13] 초기학습 진행 배너 이벤트 구독 + Start/Stop 메서드 통째 제거 (AI 시스템 폐기, 2026-04-29)
        }

        private void UnsubscribeFromEngineEvents()
        {
            _engine?.ClearEventSubscriptions();
        }

        private void HandleStatusLog(string msg)
        {
            AddLog(msg);

            // [v4.9.2] Serilog 파일 기록 — 그동안 QueueFooterLog만 호출되어
            // OnStatusLog 내용이 log-YYYYMMDD.txt 파일에 안 남던 문제 해결
            try { LoggerService.Info(msg); } catch { }

            // [v5.22.17] FastLog ListView 직결 — 사용자 요청
            //   "OnStatusLog → HandleStatusLog → AddLog → FastLog" 파이프라인이 끊겨 있어
            //   FastLog 패널이 비어 보이던 문제 해결. PushBattleFastLog 가 250ms throttle 자체 보유.
            try
            {
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    var compact = SimplifyLiveLogMessage(LocalizeLiveLogMessage(msg));
                    RunOnUI(() => PushBattleFastLog(compact));
                }
            }
            catch { /* FastLog push 실패는 trading flow 영향 없음 */ }

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

            // [v4.9.0] AI Insight Panel 갱신 — PumpScan 로그에서 후보 추출
            TryUpdateTopCandidates(msg);

            var localizedMessage = LocalizeLiveLogMessage(msg);
            var compactMessage = SimplifyLiveLogMessage(localizedMessage);
            var isMajor = IsMajorSymbolLog(msg);
            var category = DetermineLiveLogCategory(msg, isMajor);
            var symbolMatch = SymbolRegex.Match(msg);
            var symbol = symbolMatch.Success ? symbolMatch.Groups[1].Value : null;
            bool persistToDb = ShouldPersistLiveLog(localizedMessage);

            if (TryDropLowPriorityLiveLogByBackpressure(msg, localizedMessage, category, persistToDb))
                return;

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

        private bool TryDropLowPriorityLiveLogByBackpressure(
            string rawMessage,
            string localizedMessage,
            string category,
            bool persistToDb)
        {
            int queueDepth = Math.Max(_pendingLiveLogDbWrites.Count, _pendingLiveLogs.Count);
            if (queueDepth < LiveLogBackpressureSoftThreshold)
                return false;

            if (IsGateLog(rawMessage) || IsOrderErrorLog(rawMessage) || IsEssentialTradeFlowLog(rawMessage))
                return false;

            bool isNoise = IsLiveLogNoise(localizedMessage);
            bool isHighFrequency = IsHighFrequencyIngressLog(localizedMessage);
            bool isPumpCategory = string.Equals(category, "PUMP", StringComparison.OrdinalIgnoreCase);

            if (queueDepth >= LiveLogBackpressureHardThreshold)
            {
                if (isNoise || isHighFrequency || isPumpCategory || !persistToDb)
                {
                    RecordLiveLogBackpressureDrop(queueDepth, "hard");
                    return true;
                }

                return false;
            }

            if (isNoise || (isHighFrequency && !persistToDb))
            {
                RecordLiveLogBackpressureDrop(queueDepth, "soft");
                return true;
            }

            return false;
        }

        private void RecordLiveLogBackpressureDrop(int queueDepth, string mode)
        {
            int dropped = Interlocked.Increment(ref _liveLogBackpressureDropped);
            var nowUtc = DateTime.UtcNow;
            if (nowUtc < _nextLiveLogBackpressureNoticeUtc)
                return;

            _nextLiveLogBackpressureNoticeUtc = nowUtc.AddSeconds(LiveLogBackpressureNoticeIntervalSec);
            LoggerService.Warning($"[PERF][LIVELOG][BACKPRESSURE] mode={mode} queueDepth={queueDepth} dropped={dropped}");
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
                    Interval = TimeSpan.FromMilliseconds(_tickerFlushIntervalMs)
                };
                _tickerFlushTimer.Tick += (_, _) =>
                {
                    // [데이터 컨플레이션] 이전 tick 처리가 아직 끝나지 않았으면 스킵
                    if (Interlocked.CompareExchange(ref _tickerFlushRunning, 1, 0) != 0)
                        return;

                    try
                    {
                        var sw = Stopwatch.StartNew();
                        FlushPendingSignalUpdatesToUi();
                        FlushPendingAiEntryProbUpdatesToUi();
                        FlushPendingTickerUpdatesToUi();
                        RefreshBattleDashboardForSelectedSymbol();
                        RefreshMajorBattlePanel();
                        UpdateFocusedPositionFromEngine(); // [v4.9.0] AI Insight Panel
                        if (_engine != null)
                        {
                            // [v5.22.13] TrainedSymbolCount 제거 — AI 시스템 폐기 (2026-04-29)
                            DetectHealth.TrainedCount = 0;
                            DetectHealth.CandidatesNow = TopCandidates.Count;
                        }
                        sw.Stop();

                        // [Stage2] 적응형 타이머 간격 조정
                        AdaptTickerFlushInterval((int)sw.ElapsedMilliseconds);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _tickerFlushRunning, 0);
                    }
                };
                _tickerFlushTimer.Start();
            });
        }

        /// <summary>
        /// [Stage2] ticker flush 실행 시간에 따라 타이머 간격을 자동 조절
        /// 실행 시간이 길면 간격을 늘려 UI 스레드 부하를 줄이고,
        /// 실행 시간이 짧으면 간격을 줄여 반응성을 높입니다.
        /// </summary>
        private void AdaptTickerFlushInterval(int elapsedMs)
        {
            _tickerFlushDurationSamples.Enqueue(elapsedMs);
            while (_tickerFlushDurationSamples.Count > TickerFlushSampleWindow)
                _tickerFlushDurationSamples.Dequeue();

            var now = DateTime.UtcNow;
            if (now < _nextTickerTuneTime || _tickerFlushDurationSamples.Count < 10)
                return;
            _nextTickerTuneTime = now.AddSeconds(10);

            double avgMs = _tickerFlushDurationSamples.Average();
            int newInterval = _tickerFlushIntervalMs;

            // flush가 간격의 50% 이상 차지하면 → 간격 늘림
            if (avgMs > _tickerFlushIntervalMs * 0.5)
                newInterval = Math.Min(TickerFlushMaxMs, _tickerFlushIntervalMs + 50);
            // flush가 간격의 10% 미만이면 → 간격 줄임
            else if (avgMs < _tickerFlushIntervalMs * 0.1)
                newInterval = Math.Max(TickerFlushMinMs, _tickerFlushIntervalMs - 25);

            if (newInterval != _tickerFlushIntervalMs)
            {
                _tickerFlushIntervalMs = newInterval;
                _tickerFlushTimer!.Interval = TimeSpan.FromMilliseconds(newInterval);
            }
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
                    BattleBbSupportActive = false;
                    BattleBbSupportText = "BB Mid Support: 대기";
                    BattleBbSupportBrush = Brushes.LightGray;
                    BattleWeightingText = "가중치: ML 100";
                    BattleTrendRiderActive = false;
                    BattleTrendRiderText = "TREND: 대기";
                    BattleTrendRiderBrush = Brushes.LightGray;
                    BattleTrendRiderActionText = "Action: 대기 중";
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

                float mlProbability = symbolVm.MLProbability > 0
                    ? Math.Clamp(symbolVm.MLProbability, 0f, 1f)
                    : 0f;

                bool bbMidSupport = IsBattleBbMidSupport(symbolVm.BBPosition, symbolVm.Decision);

                BattleBbSupportActive = bbMidSupport;
                BattleBbSupportText = bbMidSupport
                    ? "SUPPORT CONFIRMED · BB Middle Line"
                    : "BB Mid Support: 대기";
                BattleBbSupportBrush = bbMidSupport
                    ? Brushes.DeepSkyBlue
                    : Brushes.LightGray;

                BattleWeightingText = bbMidSupport
                    ? "가중치: ML 100 (BB Support)"
                    : "가중치: ML 100";

                // ── [Trend-Rider] ML 확률 기반 추세 지속 감지 ────────────────────────────────
                bool bbAboveMid = symbolVm.BBPosition?.Contains("Upper", StringComparison.OrdinalIgnoreCase) == true
                    || symbolVm.BBPosition?.Contains("Mid", StringComparison.OrdinalIgnoreCase) == true;
                bool stairMlReady   = mlProbability >= 0.65f;
                bool stairPursuit   = mlProbability >= 0.85f;

                bool stairActive = bbAboveMid && stairMlReady;
                BattleTrendRiderActive = stairActive;

                if (stairPursuit && bbAboveMid)
                {
                    BattleTrendRiderText = "TREND: STAIRCASE UPTREND (활성)";
                    BattleTrendRiderBrush = Brushes.Gold;
                    BattleTrendRiderActionText = "Action: Pursuit Ready — 정찰대 20% 대기 휴";
                }
                else if (stairActive)
                {
                    BattleTrendRiderText = "TREND: STAIRCASE MONITORING";
                    BattleTrendRiderBrush = Brushes.DeepSkyBlue;
                    BattleTrendRiderActionText = "Status: Low Volume Persistence — Adjusting Filters";
                }
                else
                {
                    BattleTrendRiderText = "한산드 대기";
                    BattleTrendRiderBrush = Brushes.LightGray;
                    BattleTrendRiderActionText = "Action: 조건 비충 중";
                }

                double gateThreshold = ResolveBattleGateThresholdValue();
                bool aiPassed = score >= gateThreshold || confidenceRatio >= 0.35f;
                UpdateBattleExecutionSteps(symbolVm, aiPassed, goldenZoneReady, atrArmed);
            });
        }

        private static bool IsBattleBbMidSupport(string? bbPosition, string? decision)
        {
            if (string.IsNullOrWhiteSpace(bbPosition))
                return false;

            string normalizedDecision = decision?.ToUpperInvariant() ?? string.Empty;
            if (!normalizedDecision.Contains("LONG", StringComparison.Ordinal))
                return false;

            return bbPosition.Contains("MID", StringComparison.OrdinalIgnoreCase);
        }

        private static float ResolveBattleConfidence(MultiTimeframeViewModel symbolVm, double fallbackScore)
        {
            if (symbolVm.AiEntryProb >= 0)
                return Math.Clamp(symbolVm.AiEntryProb, 0f, 1f);

            if (symbolVm.MLProbability > 0)
                return Math.Clamp(symbolVm.MLProbability, 0f, 1f);

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
                    UpdateMonthlyGoalTracker();
                    return;
                }

                if (xrpVm.IsPositionActive)
                {
                    BattleXrpScenarioText = $"XRP 시나리오: {xrpVm.PositionSide} 보유 {xrpVm.ProfitRate:+0.0;-0.0;0.0}%";
                    BattleXrpScenarioBrush = xrpVm.ProfitRate >= 0 ? Brushes.LimeGreen : Brushes.Tomato;
                    UpdateMonthlyGoalTracker();
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

                UpdateMonthlyGoalTracker();
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

            // [v3.2.27] ListBox 기반으로 변경
            FastLogs.Insert(0, line);
            if (FastLogs.Count > 50) FastLogs.RemoveAt(FastLogs.Count - 1);

            // 기존 프로퍼티도 유지 (호환)
            BattleFastLog8 = BattleFastLog7;
            BattleFastLog7 = BattleFastLog6;
            BattleFastLog6 = BattleFastLog5;
            BattleFastLog5 = BattleFastLog4;
            BattleFastLog4 = BattleFastLog3;
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

        // [v5.20.6] OnLog 폭주 throttle — 동일 메시지 200ms 내 중복 제거
        private string _lastFooterMsg = "";
        private DateTime _lastFooterAt = DateTime.MinValue;
        private readonly object _footerThrottleLock = new();

        private void QueueFooterLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;

            // [v5.20.6] throttle: 동일 메시지 200ms 내 dedupe
            lock (_footerThrottleLock)
            {
                var now = DateTime.UtcNow;
                if (msg == _lastFooterMsg && (now - _lastFooterAt).TotalMilliseconds < 200)
                    return;
                _lastFooterMsg = msg;
                _lastFooterAt = now;
            }

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

        // [AI 제거] FlushPendingAiEntryProbUpdatesToUi 본체 제거
        private void FlushPendingAiEntryProbUpdatesToUi() { }

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
                // [v5.10.12] 배치 수집 후 단일 커넥션으로 INSERT — 커넥션 풀 고갈 방지
                var batch = new List<(string Category, string Message, string? Symbol)>(MaxDbWritesPerDrain);
                while (batch.Count < MaxDbWritesPerDrain && _pendingLiveLogDbWrites.TryDequeue(out var logItem))
                    batch.Add((logItem.Category, logItem.Message, logItem.Symbol));

                if (batch.Count > 0)
                    await _dbService.SaveLiveLogsBatchAsync(batch);
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
                // [v5.10.12] 배치 수집 후 단일 커넥션으로 INSERT — 커넥션 풀 고갈 방지
                var batch = new List<(DateTime Timestamp, string Message)>(MaxDbWritesPerDrain);
                while (batch.Count < MaxDbWritesPerDrain && _pendingFooterLogDbWrites.TryDequeue(out var logItem))
                    batch.Add((logItem.Timestamp, logItem.Message));

                if (batch.Count > 0)
                    await _dbService.SaveFooterLogsBatchAsync(batch);
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
        /// [v5.22.19] 자동업데이트 안전 재시작 시점 판정용 — TradingEngine 활성 포지션 수
        /// </summary>
        public int GetActivePositionCount()
        {
            try
            {
                return _engine?.GetActivePositionSnapshot()?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// [큐 분리] 라이브 로그를 큐에 넣기 (외부에서도 호출 가능, UI 스레드 직접 접근 제거)
        /// </summary>
        public void EnqueueLiveLog(string msg) => AddLiveLog(msg);

        // ═══════════════════════════════════════════════════════════════
        // [v4.9.0] AI Insight Panel — Top Candidates 파싱
        // PumpScan/MarketCrash 로그를 파싱하여 상위 후보 리스트 유지
        // [v5.22.12] PumpScan 제거 후 ENGINE_151/MAJOR/BB_SQUEEZE/GATE 트리거도 파싱
        // ═══════════════════════════════════════════════════════════════
        private static readonly System.Text.RegularExpressions.Regex InsightSymbolRegex
            = new(@"sym=(\w+)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex InsightProbRegex
            = new(@"prob=(\d+(?:\.\d+)?)%", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex InsightRejectRegex
            = new(@"reason=(\w+)", System.Text.RegularExpressions.RegexOptions.Compiled);
        // [v5.22.12] ENGINE_151 / GATE / MAJOR / BB_SQUEEZE / ETA_TRIGGER 등 트리거 로그에서 심볼 추출
        //   패턴: "[TAG] SYMBOLUSDT ..." (TAG 뒤 첫 토큰이 심볼)
        private static readonly System.Text.RegularExpressions.Regex GenericSymbolRegex
            = new(@"\]\s+([A-Z0-9]{2,15}USDT)\b", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex GateBlockReasonRegex
            = new(@"reason=([^\s|()]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private const int MaxCandidateItems = 8;
        private DateTime _detectStatsWindowStart = DateTime.Now;
        private int _pumpScanCount;
        private int _volumeSurgeCount;
        private int _spikeCount;

        private void TryUpdateTopCandidates(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            try
            {
                // DetectHealth 카운터 (1분 윈도우)
                if ((DateTime.Now - _detectStatsWindowStart).TotalMinutes >= 1)
                {
                    RunOnUI(() =>
                    {
                        DetectHealth.PumpScanPerMin = _pumpScanCount;
                        DetectHealth.VolumeSurgePerMin = _volumeSurgeCount;
                        DetectHealth.SpikePerMin = _spikeCount;
                    });
                    _pumpScanCount = 0;
                    _volumeSurgeCount = 0;
                    _spikeCount = 0;
                    _detectStatsWindowStart = DateTime.Now;
                }

                if (msg.Contains("[SIGNAL][PUMP][SCAN]", StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Interlocked.Increment(ref _pumpScanCount);
                    return;
                }
                if (msg.Contains("거래량 급증", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("VolumeSurge", StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Interlocked.Increment(ref _volumeSurgeCount);
                    return;
                }
                if (msg.Contains("급등 감지", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("급락 감지", StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Interlocked.Increment(ref _spikeCount);
                    return;
                }

                // AI_ENTRY / REJECT / CANDIDATE 파싱
                bool isAiEntry = msg.Contains("[SIGNAL][PUMP][AI_ENTRY]", StringComparison.OrdinalIgnoreCase);
                bool isReject  = msg.Contains("[SIGNAL][PUMP][REJECT]", StringComparison.OrdinalIgnoreCase);
                bool isCand    = msg.Contains("[SIGNAL][PUMP][CANDIDATE]", StringComparison.OrdinalIgnoreCase);
                bool isEmit    = msg.Contains("[SIGNAL][PUMP][EMIT]", StringComparison.OrdinalIgnoreCase);

                // [v5.22.12] PumpScan 제거 후 — ENGINE_151/MAJOR/BB_SQUEEZE/GATE 로그도 파싱
                //   GATE-CHECK = 가드 체크 시작 (CANDIDATE)
                //   GATE 차단 = REJECT(reason)
                //   ENGINE_151 차단 / 체결 → 각각 REJECT / AI_ENTRY
                //   MAJOR_ANALYZE / ElliottWave3Wave / ETA_TRIGGER → CANDIDATE
                bool isGateCheck = msg.Contains("[GATE-CHECK]", StringComparison.OrdinalIgnoreCase);
                bool isGateBlock = msg.Contains("[GATE]", StringComparison.OrdinalIgnoreCase) && msg.Contains("차단", StringComparison.OrdinalIgnoreCase) && !isGateCheck;
                bool isEngine151Block = msg.Contains("[ENGINE_151]", StringComparison.OrdinalIgnoreCase) && msg.Contains("차단", StringComparison.OrdinalIgnoreCase);
                bool isMajorAnalyze = msg.Contains("[MAJOR_ANALYZE]", StringComparison.OrdinalIgnoreCase) || msg.Contains("[MAJOR ATR]", StringComparison.OrdinalIgnoreCase);
                bool isBbSqueeze = msg.Contains("BB_SQUEEZE", StringComparison.OrdinalIgnoreCase) || msg.Contains("BB_WALK", StringComparison.OrdinalIgnoreCase);
                bool isElliott = msg.Contains("ElliottWave3Wave", StringComparison.OrdinalIgnoreCase) || msg.Contains("ETA_TRIGGER", StringComparison.OrdinalIgnoreCase) || msg.Contains("FORECAST_FALLBACK", StringComparison.OrdinalIgnoreCase);

                bool isLegacyPump = isAiEntry || isReject || isCand || isEmit;
                bool isGenericTrigger = isGateCheck || isGateBlock || isEngine151Block || isMajorAnalyze || isBbSqueeze || isElliott;

                if (!isLegacyPump && !isGenericTrigger) return;

                string symbol;
                double prob = 0;

                if (isLegacyPump)
                {
                    var symMatch = InsightSymbolRegex.Match(msg);
                    if (!symMatch.Success) return;
                    symbol = symMatch.Groups[1].Value;

                    var probMatch = InsightProbRegex.Match(msg);
                    if (probMatch.Success && double.TryParse(probMatch.Groups[1].Value, out var p))
                        prob = p / 100.0;
                }
                else
                {
                    // GENERIC: 태그 뒤 첫 토큰이 심볼 (BTCUSDT 같은 형태)
                    var gMatch = GenericSymbolRegex.Match(msg);
                    if (!gMatch.Success) return;
                    symbol = gMatch.Groups[1].Value;
                }

                string status;
                if (isAiEntry || isEmit)
                    status = "AI_ENTRY";
                else if (isReject)
                {
                    var r = InsightRejectRegex.Match(msg);
                    status = r.Success ? $"REJECT({r.Groups[1].Value})" : "REJECT";
                }
                else if (isGateBlock || isEngine151Block)
                {
                    var rg = GateBlockReasonRegex.Match(msg);
                    string rsn = rg.Success ? rg.Groups[1].Value : "BLOCKED";
                    if (rsn.Length > 24) rsn = rsn.Substring(0, 24);
                    status = $"REJECT({rsn})";
                }
                else if (isGateCheck || isMajorAnalyze || isBbSqueeze || isElliott)
                    status = "CANDIDATE";
                else
                    status = "CANDIDATE";

                RunOnUI(() =>
                {
                    var existing = TopCandidates.FirstOrDefault(c => c.Symbol == symbol);
                    if (existing != null)
                    {
                        existing.Status = status;
                        if (prob > 0) existing.MLProbability = prob;
                        existing.DetectedAt = DateTime.Now;
                        // 최상단으로 이동
                        int idx = TopCandidates.IndexOf(existing);
                        if (idx > 0) TopCandidates.Move(idx, 0);
                    }
                    else
                    {
                        TopCandidates.Insert(0, new TradingBot.Models.CandidateItem
                        {
                            Symbol = symbol,
                            MLProbability = prob,
                            Status = status,
                            DetectedAt = DateTime.Now
                        });
                        while (TopCandidates.Count > MaxCandidateItems)
                            TopCandidates.RemoveAt(TopCandidates.Count - 1);
                    }
                });
            }
            catch { /* 파싱 오류 무시 */ }
        }

        /// <summary>
        /// [v5.0.3] DB 에서 카테고리별 오늘 통계 조회 → MajorStats/PumpStats/SpikeStats 업데이트
        /// 5분 주기 타이머 호출 + 필요 시 즉시 호출 가능
        /// </summary>
        public async Task RefreshCategoryStatsAsync()
        {
            try
            {
                string cs = AppConfig.ConnectionString;
                if (string.IsNullOrEmpty(cs)) return;

                var dbManager = new DbManager(cs);
                var stats = await dbManager.GetTodayStatsByCategoryAsync();

                (int e, int w, int l, decimal p) majorT = stats.TryGetValue("MAJOR", out var mv) ? mv : (0, 0, 0, 0m);
                (int e, int w, int l, decimal p) pumpT = stats.TryGetValue("PUMP", out var pv) ? pv : (0, 0, 0, 0m);
                (int e, int w, int l, decimal p) spikeT = stats.TryGetValue("SPIKE", out var sv) ? sv : (0, 0, 0, 0m);
                (int e, int w, int l, decimal p) squeezeT = stats.TryGetValue("SQUEEZE", out var qv) ? qv : (0, 0, 0, 0m);

                RunOnUI(() =>
                {
                    MajorStats.Update(majorT.e, majorT.w, majorT.l, majorT.p);
                    PumpStats.Update(pumpT.e, pumpT.w, pumpT.l, pumpT.p);
                    SpikeStats.Update(spikeT.e, spikeT.w, spikeT.l, spikeT.p);
                    SqueezeStats.Update(squeezeT.e, squeezeT.w, squeezeT.l, squeezeT.p);
                });
            }
            catch (Exception ex)
            {
                try { AddLiveLog($"⚠️ [CategoryStats] 통계 조회 실패: {ex.Message}"); } catch { }
            }
        }

        /// <summary>
        /// [v4.9.8] 활성 포지션 전체 업데이트 — 기존 FocusedPosition 1개만 표시하던 버그 수정.
        /// ActivePositions 컬렉션에 모든 포지션을 담고, FocusedPosition은 최근 1개(호환용).
        /// </summary>
        public void UpdateFocusedPositionFromEngine()
        {
            if (_engine == null) return;
            try
            {
                List<TradingBot.Shared.Models.PositionInfo> snapshot;
                lock (typeof(TradingBot.TradingEngine))
                {
                    snapshot = _engine.GetActivePositionSnapshot()
                        .OrderByDescending(p => p.EntryTime)
                        .ToList();
                }

                if (snapshot.Count == 0)
                {
                    RunOnUI(() =>
                    {
                        FocusedPosition = null;
                        ActivePositions.Clear();
                        OnPropertyChanged(nameof(HasAnyActivePosition));
                    });
                    return;
                }

                // 모든 포지션을 ViewModel로 변환
                var vmList = new List<TradingBot.Models.PositionDetailViewModel>(snapshot.Count);
                foreach (var pos in snapshot)
                {
                    decimal currentPrice = 0m;
                    if (_engine.TryGetTickerPrice(pos.Symbol, out var px)) currentPrice = px;
                    if (currentPrice <= 0) currentPrice = pos.EntryPrice;

                    decimal roePct = 0m;
                    if (pos.EntryPrice > 0)
                    {
                        roePct = pos.IsLong
                            ? (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100m
                            : (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100m;
                        roePct *= pos.Leverage > 0 ? pos.Leverage : 1;
                    }

                    // [v5.11.0] Panel 1(실시간 시장 신호) Leverage 를 Panel 2(Binance 실제값)로 강제 동기화
                    //   기존: MultiTimeframeViewModel.Leverage 기본 20 (구 하드코딩)
                    //   수정: PositionInfo.Leverage (Binance positionRisk sync 값)을 즉시 반영
                    if (pos.Leverage > 0 && TryNormalizeTradingSymbol(pos.Symbol, out var normSym))
                    {
                        if (_marketDataIndex.TryGetValue(normSym, out var mdItem) && mdItem != null)
                        {
                            int posLev = (int)Math.Round(pos.Leverage);
                            if (mdItem.Leverage != posLev)
                                mdItem.Leverage = posLev;
                            if (mdItem.EntryPrice != pos.EntryPrice && pos.EntryPrice > 0)
                                mdItem.EntryPrice = pos.EntryPrice;
                        }
                    }

                    decimal progress = 0m;
                    if (pos.TakeProfit > 0 && pos.EntryPrice > 0)
                    {
                        decimal span = Math.Abs(pos.TakeProfit - pos.EntryPrice);
                        if (span > 0)
                        {
                            decimal moved = Math.Abs(currentPrice - pos.EntryPrice);
                            progress = Math.Min(100m, moved / span * 100m);
                        }
                    }

                    vmList.Add(new TradingBot.Models.PositionDetailViewModel
                    {
                        Symbol = pos.Symbol,
                        Side = pos.IsLong ? "LONG" : "SHORT",
                        EntryPrice = pos.EntryPrice,
                        CurrentPrice = currentPrice,
                        RoePct = roePct,
                        UnrealizedPnlUsd = (decimal)((double)pos.Quantity * ((double)currentPrice - (double)pos.EntryPrice) * (pos.IsLong ? 1 : -1)),
                        HoldingTime = pos.EntryTime == default ? TimeSpan.Zero : DateTime.Now - pos.EntryTime,
                        TpPrice = pos.TakeProfit,
                        SlPrice = pos.StopLoss,
                        ProgressToTpPct = (double)progress
                    });
                }

                RunOnUI(() =>
                {
                    ActivePositions.Clear();
                    foreach (var v in vmList) ActivePositions.Add(v);
                    FocusedPosition = vmList.FirstOrDefault(); // 호환용 (기존 바인딩 유지)
                    OnPropertyChanged(nameof(HasAnyActivePosition));
                });
            }
            catch { }
        }

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
        // [AI 제거] UpdateAiEntryProb 제거

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

                // 투입금 대비 수익률
                double netTransfer = (double)(_engine?.NetTransferAmount ?? 0m);
                if (netTransfer > 0)
                {
                    double profitFromTransfer = equity - netTransfer;
                    double profitPct = profitFromTransfer / netTransfer * 100;
                    NetTransferInfo = $"투입 ${netTransfer:N0} | PnL ${profitFromTransfer:+#,##0.00;-#,##0.00} ({profitPct:+0.0;-0.0}%)";
                    NetTransferColor = profitFromTransfer >= 0 ? Brushes.LimeGreen : Brushes.Tomato;
                }

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
                    existing.EntryPrice = 0;
                    existing.Quantity = 0;
                    existing.TargetPrice = 0;
                    existing.StopLossPrice = 0;
                    existing.TrailingStopPrice = 0;
                    // [v5.11.0] Leverage 도 0 으로 리셋 — 청산 후 stale ROI 잔류 버그 수정
                    //   ProfitRate getter 가 !IsPositionActive 시 cached _profitPercent 반환하므로
                    //   Leverage 를 리셋해야 다음 계산이 0 으로 나옴
                    existing.Leverage = 0;
                }

                string resolvedEntryStatus = ResolveEntryStatus(existing.SignalSource, existing.Decision, existing.IsPositionActive);
                if (!string.Equals(existing.EntryStatus, resolvedEntryStatus, StringComparison.Ordinal))
                    existing.EntryStatus = resolvedEntryStatus;

                if (activeChanged)
                    ConfigureMarketDataSorting();
            });
        }

        private void UpdateTrailingStopPrice(string symbol, decimal price)
        {
            RunOnUI(() =>
            {
                if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                    return;

                var existing = GetOrCreateMarketDataItem(normalizedSymbol);
                if (existing == null || !existing.IsPositionActive)
                    return;

                existing.TrailingStopPrice = price;
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

        // ═══════════════════════════════════════════════════════════════
        // [AI Command Center] HelloQuant UI 프로퍼티
        // ═══════════════════════════════════════════════════════════════

        private string _aiCmdSymbol = "-";
        public string AiCmdSymbol { get => _aiCmdSymbol; set { _aiCmdSymbol = value; OnPropertyChanged(); } }

        private double _aiCmdConfidence;
        public double AiCmdConfidence
        {
            get => _aiCmdConfidence;
            set
            {
                _aiCmdConfidence = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AiCmdConfidenceText));
                OnPropertyChanged(nameof(AiCmdGaugeColor));
                OnPropertyChanged(nameof(AiCmdGaugeGlowColor));
                OnPropertyChanged(nameof(AiCmdIsHighConfidence));
            }
        }
        public string AiCmdConfidenceText => $"{AiCmdConfidence:F0}%";
        public bool AiCmdIsHighConfidence => AiCmdConfidence >= 85.0;

        public Brush AiCmdGaugeColor => AiCmdConfidence >= 85
            ? new SolidColorBrush(Color.FromRgb(0, 229, 255))
            : AiCmdConfidence >= 65
                ? new SolidColorBrush(Color.FromRgb(255, 179, 0))
                : new SolidColorBrush(Color.FromRgb(107, 114, 128));

        public string AiCmdGaugeGlowColor => AiCmdConfidence >= 85 ? "#00E5FF" : AiCmdConfidence >= 65 ? "#FFB300" : "#444";

        private string _aiCmdDirection = "NONE";
        public string AiCmdDirection
        {
            get => _aiCmdDirection;
            set
            {
                _aiCmdDirection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsShortOpportunity));
                OnPropertyChanged(nameof(IsLongScanning));
                OnPropertyChanged(nameof(AiCmdBiasColor));
            }
        }

        public bool IsShortOpportunity => _aiCmdDirection == "SHORT" && AiCmdConfidence >= 65;
        public bool IsLongScanning     => _aiCmdDirection == "LONG"  && AiCmdConfidence >= 55;

        private string _tfH4Status = "NEUTRAL";
        public string TfH4Status { get => _tfH4Status; set { _tfH4Status = value; OnPropertyChanged(); OnPropertyChanged(nameof(TfH4Color)); } }

        private string _tfH1Status = "NEUTRAL";
        public string TfH1Status { get => _tfH1Status; set { _tfH1Status = value; OnPropertyChanged(); OnPropertyChanged(nameof(TfH1Color)); } }

        private string _tf15mStatus = "SCANNING";
        public string Tf15mStatus { get => _tf15mStatus; set { _tf15mStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(Tf15mColor)); OnPropertyChanged(nameof(IsSniperReady)); } }

        public Brush TfH4Color  => TfStatusToBrush(_tfH4Status);
        public Brush TfH1Color  => TfStatusToBrush(_tfH1Status);
        public Brush Tf15mColor => Tf15mStatusToBrush(_tf15mStatus);

        private double _bullPower = 50;
        private bool _aiCommandDataReceived;
        public bool IsAiCommandEmpty => !_aiCommandDataReceived;
        public double BullPower
        {
            get => _bullPower;
            set { _bullPower = Math.Max(0, Math.Min(100, value)); _aiCommandDataReceived = true; OnPropertyChanged(); OnPropertyChanged(nameof(BearPower)); OnPropertyChanged(nameof(CurrentBias)); OnPropertyChanged(nameof(AiCmdBiasColor)); OnPropertyChanged(nameof(IsAiCommandEmpty)); }
        }
        public double BearPower => 100.0 - _bullPower;

        public string CurrentBias => _bullPower >= 70 ? "STRONGLY BULLISH"
            : _bullPower >= 55 ? "BULLISH"
            : _bullPower >= 45 ? "NEUTRAL"
            : _bullPower >= 30 ? "BEARISH"
            : "STRONGLY BEARISH";

        public Brush AiCmdBiasColor => _bullPower >= 55
            ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
            : _bullPower >= 45
                ? new SolidColorBrush(Color.FromRgb(224, 231, 255))
                : new SolidColorBrush(Color.FromRgb(255, 83, 112));

        public bool IsSniperReady => _tf15mStatus is "READY TO SHOOT" or "FIRE";

        private int _sniperCountdown;
        public int SniperCountdown { get => _sniperCountdown; set { _sniperCountdown = value; OnPropertyChanged(); OnPropertyChanged(nameof(SniperCountdownText)); } }
        public string SniperCountdownText => _sniperCountdown > 0 ? $"진입 {_sniperCountdown}초 전" : string.Empty;

        public void UpdateAiCommandState(
            string symbol, float confidence, string direction,
            string h4, string h1, string m15,
            double bullPower, double bearPower)
        {
            AiCmdSymbol     = symbol;
            AiCmdConfidence = Math.Round(confidence * 100, 1);
            AiCmdDirection  = direction;
            TfH4Status  = h4;
            TfH1Status  = h1;
            Tf15mStatus = m15;
            BullPower   = bullPower;
        }

        // ═══════════════════════════════════════════════════════════════
        // [Entry Pipeline] Block Reason + Volume Gauge + Profit Target
        // ═══════════════════════════════════════════════════════════════

        private string _blockReason = "";
        public string BlockReason
        {
            get => _blockReason;
            set { _blockReason = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBlockReason)); }
        }
        public bool HasBlockReason => !string.IsNullOrEmpty(_blockReason);

        private double _volumeGauge;
        public double VolumeGauge
        {
            get => _volumeGauge;
            set { _volumeGauge = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(VolumeGaugeText)); OnPropertyChanged(nameof(VolumeGaugeColor)); }
        }
        public string VolumeGaugeText => $"{_volumeGauge:F0}%";
        public Brush VolumeGaugeColor => _volumeGauge >= 80
            ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
            : _volumeGauge >= 50
                ? new SolidColorBrush(Color.FromRgb(255, 179, 0))
                : new SolidColorBrush(Color.FromRgb(107, 114, 128));

        // ─── [상승 에너지 잔량] Price Position in Forecast Band ──────────────────
        // ML.NET이 0.382~0.5 피보나치 눌림목을 가리킬 때만 초록색 진입 허용
        // TF가 수렴 후 발산 패턴을 감지하면 깜빡임으로 시각적 경고
        private double _priceProgress;
        private float _currentMLConfidence;
        private bool _tfConvergenceDivergence;

        public double PriceProgress
        {
            get => _priceProgress;
            set
            {
                _priceProgress = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(PriceProgressText));
                OnPropertyChanged(nameof(PriceProgressColor));
                OnPropertyChanged(nameof(PriceProgressZone));
                OnPropertyChanged(nameof(PriceEnergyPulseActive));
            }
        }
        public string PriceProgressText => $"{_priceProgress:F1}% (ML:{_currentMLConfidence:P0})";

        /// <summary>
        /// 구간 판정:
        /// ML 예측이 Fib 0.382~0.5 눌림목(bbPos 38.2~50%)을 가리킬 때만 ENTRY ZONE
        /// 그 외에는 bbPos에 따라 CAUTION / OVERHEATED
        /// </summary>
        public string PriceProgressZone
        {
            get
            {
                bool isFibPullback = IsFibonacciPullbackZone(_priceProgress, _currentMLConfidence);
                if (_tfConvergenceDivergence)
                    return "SQUEEZE FIRE";     // 수렴 후 발산 임박
                if (isFibPullback)
                    return "ENTRY ZONE";       // Fib 0.382~0.5 눌림목
                return _priceProgress switch
                {
                    <= 50 => "ACCUMULATE",     // 하단 축적 (아직 진입 신호 아님)
                    <= 70 => "CAUTION",        // 중간: 추격 주의
                    _     => "OVERHEATED"      // 과열: 진입 금지
                };
            }
        }

        /// <summary>
        /// 색상 로직:
        /// 1. ML이 Fib 눌림목(0.382~0.5) 가리킬 때만 초록색
        /// 2. TF 수렴 후 발산 → 금색 (SQUEEZE FIRE)
        /// 3. 그 외 → bbPos 기반 점진적 색상
        /// </summary>
        public Brush PriceProgressColor
        {
            get
            {
                // TF 수렴 후 발산 패턴 → 금색
                if (_tfConvergenceDivergence)
                    return new SolidColorBrush(Color.FromRgb(255, 215, 0));    // Gold

                // ML이 Fib 0.382~0.5 눌림목을 가리키는지 확인
                bool isFibPullback = IsFibonacciPullbackZone(_priceProgress, _currentMLConfidence);

                if (isFibPullback)
                    return new SolidColorBrush(Color.FromRgb(0, 230, 118));    // 초록 (ENTRY ZONE)

                // 그 외: bbPos 기반 단계적 색상 (진입 금지 방향)
                return _priceProgress switch
                {
                    <= 30 => new SolidColorBrush(Color.FromRgb(107, 114, 128)),  // 회색 (신호 없음)
                    <= 50 => new SolidColorBrush(Color.FromRgb(0, 229, 255)),    // 시안 (축적 중)
                    <= 70 => new SolidColorBrush(Color.FromRgb(255, 179, 0)),    // 주황 (CAUTION)
                    <= 85 => new SolidColorBrush(Color.FromRgb(255, 83, 112)),   // 빨강 (위험)
                    _     => new SolidColorBrush(Color.FromRgb(213, 0, 0))       // 진홍 (OVERHEATED)
                };
            }
        }

        /// <summary>
        /// TF 수렴 후 발산 감지 시 깜빡임 활성화
        /// XAML에서 이 프로퍼티가 true이면 Storyboard 깜빡임 트리거
        /// </summary>
        public bool PriceEnergyPulseActive => _tfConvergenceDivergence;

        /// <summary>
        /// Fibonacci 0.382~0.5 눌림목 판정
        /// bbPos가 38.2~50% 구간이고, ML 확률이 40% 이상이면 유효한 눌림목
        /// </summary>
        private static bool IsFibonacciPullbackZone(double bbPositionPct, float mlConfidence)
        {
            // BB 밴드 내 위치가 Fib 0.382~0.5 구간 (38.2%~50%)
            bool inFibZone = bbPositionPct >= 38.2 && bbPositionPct <= 50.0;
            // ML.NET이 진입 신호를 줄 정도의 확률 (40% 이상)
            bool mlSignal = mlConfidence >= 0.40f;
            return inFibZone && mlSignal;
        }

        /// <summary>
        /// 에너지 잔량 업데이트 (bbPos + ML확률 + TF수렴발산)
        /// </summary>
        public void UpdatePriceProgress(double bbPosition, float mlConfidence, bool tfConvergenceDivergence)
        {
            _currentMLConfidence = mlConfidence;
            _tfConvergenceDivergence = tfConvergenceDivergence;
            // bbPosition: 0.0 (하단밴드) ~ 1.0 (상단밴드) → 0~100%
            PriceProgress = bbPosition * 100.0;

            // [핀테크] AI Prediction Summary 카드 업데이트
            BattleMLConfidence = mlConfidence * 100.0;
            string bbLabel = bbPosition switch
            {
                <= 0.30 => $"Position: {bbPosition:P0} (하단 진입구간)",
                <= 0.50 => $"Position: {bbPosition:P0} (눌림목)",
                <= 0.70 => $"Position: {bbPosition:P0} (중립)",
                _ => $"Position: {bbPosition:P0} (과열 주의)"
            };
            BattleBBPositionText = bbLabel;
        }

        // ─── [동적 트레일링] Exit Score 게이지 + 동적 손절가 ────────────────────
        private double _exitScore;
        private float _trendReversalProb;
        private decimal _dynamicStopPrice;
        private string _exitScoreSymbol = "";

        public double ExitScore
        {
            get => _exitScore;
            set
            {
                _exitScore = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExitScoreText));
                OnPropertyChanged(nameof(ExitScoreColor));
                OnPropertyChanged(nameof(ExitScoreZone));
                OnPropertyChanged(nameof(ExitScorePulseActive));
            }
        }
        public string ExitScoreText => $"{_exitScore:F0}% ({_trendReversalProb:P0} 반전)";
        public string ExitScoreZone => _exitScore switch
        {
            <= 30 => "HOLD",           // 홀딩 유지
            <= 60 => "PREPARE EXIT",   // 익절 준비
            <= 80 => "EXIT NOW",       // 익절 권장
            _     => "EMERGENCY"       // 즉시 탈출
        };
        public Brush ExitScoreColor => _exitScore switch
        {
            <= 30 => new SolidColorBrush(Color.FromRgb(0, 230, 118)),     // 초록 (안전)
            <= 50 => new SolidColorBrush(Color.FromRgb(0, 229, 255)),     // 시안
            <= 70 => new SolidColorBrush(Color.FromRgb(255, 179, 0)),     // 주황
            <= 85 => new SolidColorBrush(Color.FromRgb(255, 83, 112)),    // 빨강
            _     => new SolidColorBrush(Color.FromRgb(213, 0, 0))        // 진홍 (긴급)
        };
        /// <summary>Exit Score 80+ 시 깜빡임</summary>
        public bool ExitScorePulseActive => _exitScore >= 80;
        public string DynamicStopPriceText => _dynamicStopPrice > 0 ? $"SL: {_dynamicStopPrice:F4}" : "SL: --";

        /// <summary>SkiaCandleChart 등 외부 컨트롤이 구독하여 실시간 손절가 반영</summary>
        public event Action<double, double, double>? OnAIStopPriceChanged; // (stopPrice, currentPrice, exitScore)

        public void UpdateExitScore(string symbol, double exitScore, float reversalProb, decimal stopPrice)
        {
            _exitScoreSymbol = symbol;
            _trendReversalProb = reversalProb;
            _dynamicStopPrice = stopPrice;
            ExitScore = exitScore;
            OnPropertyChanged(nameof(DynamicStopPriceText));

            // SkiaCandleChart 등 차트 컨트롤에 실시간 전달
            OnAIStopPriceChanged?.Invoke((double)stopPrice, (double)_dynamicStopPrice, exitScore);
        }

        private decimal _dailyProfitTarget = 250m;
        private decimal _dailyProfitCurrent = 0m;
        public decimal DailyProfitTarget { get => _dailyProfitTarget; set { _dailyProfitTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(DailyProfitText)); OnPropertyChanged(nameof(DailyProfitProgress)); } }
        public decimal DailyProfitCurrent
        {
            get => _dailyProfitCurrent;
            set { _dailyProfitCurrent = value; OnPropertyChanged(); OnPropertyChanged(nameof(DailyProfitText)); OnPropertyChanged(nameof(DailyProfitProgress)); OnPropertyChanged(nameof(DailyProfitColor)); }
        }
        public string DailyProfitText => $"${_dailyProfitCurrent:N2} / ${_dailyProfitTarget:N2}";
        public double DailyProfitProgress => _dailyProfitTarget > 0 ? (double)Math.Clamp(_dailyProfitCurrent / _dailyProfitTarget * 100, 0, 100) : 0;
        public Brush DailyProfitColor => _dailyProfitCurrent >= _dailyProfitTarget
            ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
            : _dailyProfitCurrent > 0
                ? new SolidColorBrush(Color.FromRgb(0, 229, 255))
                : new SolidColorBrush(Color.FromRgb(255, 83, 112));

        /// <summary>수동 진입 실행 (LONG/SHORT)</summary>
        private async Task ExecuteManualEntry(string direction)
        {
            var sym = SelectedSymbol;
            if (sym == null || string.IsNullOrWhiteSpace(sym.Symbol))
            {
                AddLog("⚠️ 수동 진입 실패: 심볼을 먼저 선택하세요.");
                return;
            }

            if (_engine == null || !_engine.IsBotRunning)
            {
                AddLog("⚠️ 수동 진입 실패: 봇이 실행 중이어야 합니다. START를 먼저 누르세요.");
                return;
            }

            string symbol = sym.Symbol;
            var result = MessageBox.Show(
                $"{symbol} {direction} 수동 진입하시겠습니까?\n\n" +
                $"현재가: {sym.LastPrice}\n" +
                $"AI 게이트를 우회하여 즉시 시장가 주문합니다.",
                $"수동 {direction} 진입",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            AddLog($"⚡ [{symbol}] 수동 {direction} 진입 요청 중...");

            var (success, message) = await _engine.ManualEntryAsync(symbol, direction);
            AddLog(success ? $"✅ {message}" : $"❌ {message}");

            if (success)
                await LoadTradeHistory();
        }

        public void UpdateBlockReason(string reason)
        {
            BlockReason = reason;
        }

        public void UpdateVolumeGauge(double pct)
        {
            VolumeGauge = pct;
        }

        public void UpdateDailyProfit(decimal current)
        {
            DailyProfitCurrent = current;
        }

        private static Brush TfStatusToBrush(string status) => status switch
        {
            "TRENDING UP"   => new SolidColorBrush(Color.FromRgb(0, 230, 118)),
            "STRENGTHENING" => new SolidColorBrush(Color.FromRgb(0, 230, 118)),
            "WATCHING"      => new SolidColorBrush(Color.FromRgb(255, 179, 0)),
            _               => new SolidColorBrush(Color.FromRgb(107, 114, 128)),
        };

        private static Brush Tf15mStatusToBrush(string status) => status switch
        {
            "FIRE"           => new SolidColorBrush(Color.FromRgb(255, 83, 112)),
            "READY TO SHOOT" => new SolidColorBrush(Color.FromRgb(255, 179, 0)),
            _                => new SolidColorBrush(Color.FromRgb(107, 114, 128)),
        };

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
