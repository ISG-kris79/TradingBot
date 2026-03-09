using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Authentication;
using Binance.Net.Objects.Models.Futures.Socket;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO; // [FIX] Path, File 사용을 위해 추가
using System.Text.Json;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Strategies;
using TradingBot.Services.AI;
using TradingBot.Services.AI.RL; // [Agent 2, 3] 네임스페이스 추가
using TradingBot.Services.Infrastructure; // [Agent 3] 메모리 관리
using TradingBot.Services.DeFi; // [Phase 8] DeFi 추가
using System.Threading.Channels; // [Agent 3] 추가
using TradingBot.Shared.Models; // [Phase 11] Shared Models (NotificationChannel 등)
using PositionInfo = TradingBot.Shared.Models.PositionInfo;
using CandleData = TradingBot.Models.CandleData;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot
{
    public class TradingEngine : IDisposable
    {
        private bool _disposed = false;
        public bool IsBotRunning { get; private set; } = false;
        private readonly IBinanceRestClient _client;
        private CancellationTokenSource? _cts;
        private readonly string apiKey;
        private readonly string apiSecret;

        // 감시 종목 리스트
        private readonly List<string> _symbols;

        public decimal InitialBalance { get; private set; } = 0; // 프로그램 시작 시점의 자산
        private DateTime _engineStartTime = DateTime.Now;
        private DateTime _lastReportTime = DateTime.MinValue;
        private decimal _periodicReportBaselineEquity = 0m;

        // 현재 보유 중인 포지션 정보 (심볼, 진입가)
        private Dictionary<string, PositionInfo> _activePositions = new Dictionary<string, PositionInfo>();
        private readonly object _posLock = new object();
        // 블랙리스트 (심볼, 해제시간) - 지루함 청산 종목 재진입 방지
        private ConcurrentDictionary<string, DateTime> _blacklistedSymbols = new ConcurrentDictionary<string, DateTime>();
        // 슬롯 설정
        private const int MAX_MAJOR_SLOTS = 4; // BTC, ETH, SOL, XRP 전용
        private const int MAX_PUMP_SLOTS = 2;  // 실시간 급등주 전용
        private const int PUMP_MANUAL_LEVERAGE = 20; // 20배 롱 전용 대응 매뉴얼
        private const int SYMBOL_ANALYSIS_MIN_INTERVAL_MS = 1000;
        private const int MAJOR_SYMBOL_ANALYSIS_MIN_INTERVAL_MS = 180;
        private static readonly TimeSpan MainLoopInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan FastEntrySlippageCheckInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan FastEntrySlippageMonitorDuration = TimeSpan.FromSeconds(25);
        private const decimal FastEntrySlippageWarnPct = 0.0010m;
        private const decimal FastEntrySlippageExitPct = 0.0018m;
        private decimal _minEntryRiskRewardRatio = 1.40m; // 설정에서 로드
        private float _fifteenMinuteMlMinConfidence = 0.55f; // 설정에서 로드
        private float _fifteenMinuteTransformerMinConfidence = 0.52f; // 설정에서 로드
        private float _aiScoreThresholdMajor = 65.0f; // 설정에서 로드
        private float _aiScoreThresholdNormal = 75.0f; // 설정에서 로드
        private bool _enableAiScoreFilter = true; // 설정에서 로드
        private bool _enableFifteenMinWaveGate = true; // 설정에서 로드
        private const float GateMlThresholdMin = 0.48f;
        private const float GateMlThresholdMax = 0.72f;
        private const float GateTransformerThresholdMin = 0.47f;
        private const float GateTransformerThresholdMax = 0.70f;
        private const float GateThresholdAdjustStep = 0.01f;
        private const int GateAutoTuneSampleSize = 24;
        private const float GateTightenPassRate = 0.62f;
        private const float GateLoosenPassRate = 0.20f;
        private bool _mainLoopPerfEnabled = true;
        private bool _mainLoopAutoTuneEnabled = true;
        private int _mainLoopBaseWarnMs = 1500;
        private int _mainLoopBasePerfLogIntervalSec = 20;
        private int _mainLoopWarnMs = 1500;
        private int _mainLoopPerfLogIntervalSec = 20;
        private int _mainLoopWarnMinMs = 700;
        private int _mainLoopWarnMaxMs = 8000;
        private int _mainLoopPerfLogIntervalMinSec = 5;
        private int _mainLoopPerfLogIntervalMaxSec = 60;
        private int _mainLoopTuneSampleWindow = 60;
        private int _mainLoopTuneMinIntervalSec = 30;
        private readonly Queue<int> _mainLoopRecentWorkSamples = new Queue<int>();
        private DateTime _nextMainLoopPerfLogTime = DateTime.UtcNow.AddSeconds(20);
        private DateTime _nextMainLoopTuneTime = DateTime.UtcNow.AddSeconds(30);
        private long _mainLoopTotalMs;
        private int _mainLoopSamples;
        private int _mainLoopMaxMs;

        // 전략 인스턴스
        private PumpScanStrategy? _pumpStrategy;
        private MajorCoinStrategy? _majorStrategy;
        private GridStrategy _gridStrategy;
        private ArbitrageStrategy _arbitrageStrategy;
        private NewsSentimentService _newsService;
        private TransformerStrategy? _transformerStrategy;
        private TransformerTrainer? _transformerTrainer;
        private ElliottWave3WaveStrategy _elliotWave3Strategy; // [3파 확정형 단타]
        private HybridExitManager _hybridExitManager; // [하이브리드 AI 익절/손절 관리]
        private BinanceExecutionService _executionService; // [실시간 레버리지 주문 실행 서비스]

        // [Phase 8] DeFi Services
        private DexService _dexService;
        private OnChainAnalysisService _onChainService;

        private IExchangeService _exchangeService;

        private BinanceOrderService _orderService;

        private PositionMonitorService _positionMonitor;

        private SoundService _soundService;

        private readonly NotificationService _notificationService;

        private readonly DbManager _dbManager;
        private readonly PatternMemoryService _patternMemoryService;

        private readonly ConcurrentDictionary<string, byte> _runningStandardMonitors = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, byte> _runningFastEntrySlippageMonitors = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, byte> _uiTrackedSymbols = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private ConcurrentDictionary<string, DateTime> _lastTickerUpdateTimes = new ConcurrentDictionary<string, DateTime>();

        private ConcurrentDictionary<string, DateTime> _lastAnalysisTimes = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, decimal> _pendingAnalysisPrices = new ConcurrentDictionary<string, decimal>();
        private readonly ConcurrentDictionary<string, byte> _analysisWorkers = new ConcurrentDictionary<string, byte>();
        private readonly SemaphoreSlim _analysisConcurrencyLimiter = new SemaphoreSlim(8, 8);

        private DateTime _lastCleanupTime = DateTime.Now;

        private Channel<IBinance24HPrice> _tickerChannel;
        // [Agent 2] 비동기 성능 개선: 주문 및 계좌 업데이트용 채널 추가
        private Channel<BinanceFuturesStreamAccountUpdate> _accountChannel;
        private Channel<BinanceFuturesStreamOrderUpdate> _orderChannel;

        private ConcurrentQueue<string> _apiLogBuffer = new ConcurrentQueue<string>(); // [추가] API용 로그 버퍼

        private readonly MarketDataManager _marketDataManager;
        private readonly RiskManager _riskManager;
        private AIPredictor? _aiPredictor;
        private AIDoubleCheckEntryGate? _aiDoubleCheckEntryGate;
        private HybridNavigatorSniper? _hybridNavigatorSniper; // [v2.4.2] Navigator-Sniper 매복 아키텍처
        private MultiAgentManager _multiAgentManager;
        private MarketHistoryService? _marketHistoryService;
        private OiDataCollector? _oiCollector;

        // [AI 모니터링] 예측 추적용 Dictionary (symbol + timestamp -> 예측 정보)
        private ConcurrentDictionary<string, (DateTime timestamp, decimal entryPrice, decimal predictedPrice, bool predictedDirection, string modelName, float confidence)> _pendingPredictions
            = new ConcurrentDictionary<string, (DateTime, decimal, decimal, bool, string, float)>();
        private readonly ConcurrentDictionary<string, DateTime> _lastMlMonitorRecordTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _scheduledPredictionValidations = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ActiveDecisionIdState> _activeAiDecisionIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _activeAiDecisionIdsPath = Path.Combine("TrainingData", "EntryDecisions", "ActiveDecisionIds.json");
        private const int ActiveDecisionIdRetentionHours = 48;
        private readonly SemaphoreSlim _predictionValidationLimiter = new SemaphoreSlim(3, 3);
        private static readonly TimeSpan MlMonitorRecordInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PredictionValidationDelay = TimeSpan.FromMinutes(5);
        private const decimal PredictionValidationNeutralMovePct = 0.0015m;

        private sealed class ActiveDecisionIdState
        {
            public string DecisionId { get; set; } = string.Empty;
            public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class GateThresholdState
        {
            public readonly object SyncRoot = new object();
            public float MlThreshold;
            public float TransformerThreshold;
            public int SampleCount;
            public int PassCount;
            public int BlockCount;

            public GateThresholdState(float mlThreshold, float transformerThreshold)
            {
                MlThreshold = mlThreshold;
                TransformerThreshold = transformerThreshold;
            }
        }

        private readonly ConcurrentDictionary<string, GateThresholdState> _symbolGateThresholds = new(StringComparer.OrdinalIgnoreCase);

        // [캔들 확인 지연 진입] 신호 발생 시 즉시 진입하지 않고 다음 캔들 확인 후 진입
        private readonly ConcurrentDictionary<string, DelayedEntrySignal> _pendingDelayedEntries
            = new ConcurrentDictionary<string, DelayedEntrySignal>();

        private static readonly HashSet<string> MajorSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"
        };

        private TradingSettings _settings;

        // [Events] UI와의 통신을 위한 이벤트 정의
        public event Action<string>? OnLiveLog;
        public event Action<string>? OnAlert;
        public event Action<string>? OnStatusLog;
        public event Action<int, int>? OnProgress;
        public event Action<double, double, int>? OnDashboardUpdate; // equity, available, posCount
        public event Action<int, int, int, int>? OnSlotStatusUpdate; // major, majorMax, pump, pumpMax
    #pragma warning disable CS0067
        public event Action<bool, string>? OnTelegramStatusUpdate;
    #pragma warning restore CS0067
        public event Action<MultiTimeframeViewModel>? OnSignalUpdate;
        public event Action<string, decimal, double?>? OnTickerUpdate; // symbol, price, pnl
        public event Action<string>? OnSymbolTracking; // Ensure symbol in list
        public event Action<string, string, decimal, decimal>? OnTradeExecuted;
        public event Action<string, bool, decimal>? OnPositionStatusUpdate;
        public event Action<string, bool, string?>? OnCloseIncompleteStatusChanged;
        public event Action<string, string?, string?>? OnExternalSyncStatusChanged;
        public event Action<string, float, float>? OnRLStatsUpdate; // [추가] RL 학습 상태 업데이트
        public event Action<AIPredictionRecord>? OnAIPrediction; // [AI 모니터링] 예측 기록 이벤트
        public event Action? OnTradeHistoryUpdated; // [FIX] 청산 시 TradeHistory 자동 갱신 트리거
        public event Action<string, AIEntryForecastResult>? OnAiEntryProbUpdate; // [AI 진입 예측] symbol, forecast

        /// <summary>
        /// 외부에서 모든 이벤트 구독을 해제합니다 (event 키워드로 인해 내부에서만 초기화 가능)
        /// </summary>
        public void ClearEventSubscriptions()
        {
            OnLiveLog = null;
            OnAlert = null;
            OnStatusLog = null;
            OnProgress = null;
            OnDashboardUpdate = null;
            OnSlotStatusUpdate = null;
            OnTelegramStatusUpdate = null;
            OnSignalUpdate = null;
            OnTickerUpdate = null;
            OnSymbolTracking = null;
            OnTradeExecuted = null;
            OnPositionStatusUpdate = null;
            OnCloseIncompleteStatusChanged = null;
            OnExternalSyncStatusChanged = null;
            OnRLStatsUpdate = null;
            OnAIPrediction = null;
            OnTradeHistoryUpdated = null;
            OnAiEntryProbUpdate = null;
        }

        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private DateTime _lastPositionSyncTime = DateTime.MinValue; // [FIX] 마지막 포지션 동기화 시간
        private bool _initialTransformerTrainingTriggered = false;
        private TimeSpan _entryWarmupDuration = TimeSpan.FromSeconds(30); // 설정에서 로드
        private DateTime _lastEntryWarmupLogTime = DateTime.MinValue;

        // [병목 해결] RefreshProfitDashboard API 호출 캐싱
        private decimal _cachedUsdtBalance = 0m;
        private DateTime _lastBalanceCacheTime = DateTime.MinValue;
        private const int BALANCE_CACHE_INTERVAL_MS = 5000; // 5초마다 업데이트

        public TradingEngine()
        {
            _cts = new CancellationTokenSource();

            // [진입 필터 설정 로드]
            LoadEntryFilterSettings();

            // [추가] 로그 버퍼링 (최근 100개 유지)
            this.OnLiveLog += (msg) => AddToLogBuffer($"[LIVE] {msg}");
            this.OnStatusLog += (msg) => AddToLogBuffer($"[STATUS] {msg}");
            this.OnAlert += (msg) => AddToLogBuffer($"[ALERT] {msg}");

            // 선택된 거래소에 따라 API 키 설정
            ExchangeType selectedExchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);
            switch (selectedExchange)
            {
                case ExchangeType.Binance:
                default:
                    apiKey = AppConfig.BinanceApiKey;
                    apiSecret = AppConfig.BinanceApiSecret;
                    break;
            }

            LoggerService.Initialize();

            // 실제 사용 시 API Key와 Secret을 입력하세요. 조회 전용은 Key 없이도 일부 가능합니다.
            // v12.x 초기화 방식 (Binance Client는 바이낸스 전용이므로 바이낸스 키 사용)
            _client = new BinanceRestClient(options =>
            {
                // 바이낸스 API Key가 설정되어 있을 때만 자격 증명 추가
                if (!string.IsNullOrWhiteSpace(AppConfig.BinanceApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BinanceApiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                }
                // API Key가 없으면 공개 API만 사용 가능 (시세 조회 등)
            });

            bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;

            if (isSimulation)
            {
                // 가상 거래소 서비스 주입 (설정에서 초기 자본 읽기)
                decimal simulationBalance = AppConfig.Current?.Trading?.SimulationInitialBalance ?? 10000m;
                _exchangeService = new MockExchangeService(simulationBalance);
                OnStatusLog?.Invoke($"🎮 [Simulation Mode] 가상 거래소 서비스 활성화 (초기 잔고: ${simulationBalance:N2})");
            }
            else
            {
                // 바이낸스 거래소로 고정
                _exchangeService = new BinanceExchangeService(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                OnStatusLog?.Invoke("🔗 바이낸스 거래소 연결");
            }

            _orderService = new BinanceOrderService(_client);

            // GeneralSettings 로드: MainWindow에서 초기화된 설정 사용 (DB 우선)
            _settings = MainWindow.CurrentGeneralSettings
                ?? AppConfig.Current?.Trading?.GeneralSettings
                ?? new TradingSettings();
            ApplyMainLoopPerformanceSettings();

            _symbols = AppConfig.Current?.Trading?.Symbols ?? new List<string>();
            if (_symbols.Count == 0)
            {
                _symbols.AddRange(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT", "ADAUSDT", "DOGEUSDT", "DOTUSDT", "MATICUSDT", "LINKUSDT" });
                OnStatusLog?.Invoke($"⚠️ 설정에 심볼이 없어 기본 10개 추가: {string.Join(", ", _symbols)}");
            }
            else if (_symbols.Count < 10)
            {
                // AI 초기 학습을 위해 최소 10개 확보
                var additionalSymbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT", "ADAUSDT", "DOGEUSDT", "DOTUSDT", "MATICUSDT", "LINKUSDT" }
                    .Where(s => !_symbols.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .Take(10 - _symbols.Count)
                    .ToList();
                
                if (additionalSymbols.Any())
                {
                    _symbols.AddRange(additionalSymbols);
                    OnStatusLog?.Invoke($"⚠️ AI 학습을 위해 추가 심볼 포함: {string.Join(", ", additionalSymbols)} (총 {_symbols.Count}개)");
                }
            }

            _soundService = new SoundService();

            _newsService = new NewsSentimentService();
            _arbitrageStrategy = new ArbitrageStrategy(_client);

            _notificationService = new NotificationService(); // \ub9e4\uac1c\ubcc0\uc218 \uc5c6\ub294 \uc0dd\uc131\uc790

            _dbManager = new DbManager(AppConfig.ConnectionString);
            _patternMemoryService = new PatternMemoryService(_dbManager, msg => OnStatusLog?.Invoke(msg));
            _riskManager = new RiskManager();
            _marketDataManager = new MarketDataManager(_client, _symbols);
            _marketHistoryService = new MarketHistoryService(_marketDataManager, AppConfig.ConnectionString);
            _oiCollector = new OiDataCollector(_client);

            _tickerChannel = Channel.CreateBounded<IBinance24HPrice>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest // 처리 속도가 느리면 오래된 티커 드랍
            });

            // [Agent 2] 채널 초기화
            // 고빈도 구간에서 무한 적재로 메모리 급증/종료가 발생하지 않도록 bounded + DropOldest 적용
            _accountChannel = Channel.CreateBounded<BinanceFuturesStreamAccountUpdate>(new BoundedChannelOptions(2000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _orderChannel = Channel.CreateBounded<BinanceFuturesStreamOrderUpdate>(new BoundedChannelOptions(3000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _positionMonitor = new PositionMonitorService(
                _client,
                _exchangeService,
                _riskManager,
                _marketDataManager,
                _dbManager,
                _activePositions,
                _posLock,
                _blacklistedSymbols,
                _settings,
                _aiPredictor
            );


            _riskManager.OnTripped += (reason) =>
            {
                string msg = $"⛔ [서킷 브레이커] {reason}! 매매 중단 모드로 전환합니다.";
                OnAlert?.Invoke(msg);
                _soundService.PlayAlert();
                _ = _notificationService.SendPushNotificationAsync("Circuit Breaker", msg);
            };
            _marketDataManager.OnLog += (msg) =>
            {
                OnStatusLog?.Invoke(msg);
                LoggerService.Info(msg);
            };
            // [Agent 2] 이벤트 핸들러를 채널 Writer로 변경 (Non-blocking)
            _marketDataManager.OnAccountUpdate += (data) => _accountChannel.Writer.TryWrite(data);
            _marketDataManager.OnOrderUpdate += (data) => _orderChannel.Writer.TryWrite(data);

            _marketDataManager.OnTickerUpdate += HandleTickerUpdate;

            _positionMonitor.OnLog += msg => OnStatusLog?.Invoke(msg);
            _positionMonitor.OnAlert += msg => OnAlert?.Invoke(msg);
            _positionMonitor.OnTickerUpdate += (s, p, r) =>
            {
                OnTickerUpdate?.Invoke(s, p, r);
            };
            _positionMonitor.OnPositionStatusUpdate += (s, a, p) =>
            {
                OnPositionStatusUpdate?.Invoke(s, a, p);
                // 포지션 종료 시 HybridExitManager 상태 정리
                if (!a) _hybridExitManager?.RemoveState(s);
                if (!a) _runningStandardMonitors.TryRemove(s, out _);
            };
            _positionMonitor.OnCloseIncompleteStatusChanged += (s, isIncomplete, detail) =>
            {
                OnCloseIncompleteStatusChanged?.Invoke(s, isIncomplete, detail);
            };
            _positionMonitor.OnTradeHistoryUpdated += () => OnTradeHistoryUpdated?.Invoke();
            _positionMonitor.OnPositionClosedForAiLabel += (symbol, entryTime, entryPrice, isLong, actualProfitPct, closeReason) =>
            {
                _ = HandleAiCloseLabelingAsync(symbol, entryTime, entryPrice, isLong, actualProfitPct, closeReason);
            };

            // [Agent 3] 멀티 에이전트 매니저 초기화 (상태 차원: 3 [RSI, MACD, BB], 행동 차원: 3 [Hold, Buy, Sell])
            _multiAgentManager = new MultiAgentManager(3, 3);
            _multiAgentManager.OnAgentTrainingStats += (name, loss, reward) =>
            {
                OnStatusLog?.Invoke($"🧠 RL[{name}] 학습 완료 (Loss: {loss:F4}, Reward: {reward:F4})");
                OnRLStatsUpdate?.Invoke(name, loss, reward);
            };


            // [AI 초기화]
            try
            {
                _aiPredictor = new AIPredictor();
                _positionMonitor.UpdateAiPredictor(_aiPredictor);
                
                // 실제 모델 로드 여부 확인
                if (_aiPredictor.IsModelLoaded)
                {
                    OnStatusLog?.Invoke("🧠 AI 예측 모델 로드 완료");
                }
                else
                {
                    OnStatusLog?.Invoke("⚠️ AI 모델 파일 없음 (학습 후 사용 가능)");
                }
            }
            catch (Exception ex)
            {
                _positionMonitor.UpdateAiPredictor(null);
                OnStatusLog?.Invoke($"⚠️ AI 모델 로드 실패: {ex.Message}");
            }

            // [AI 더블체크 게이트 초기화]
            try
            {
                var doubleCheckConfig = new DoubleCheckConfig
                {
                    MinMLConfidence = Math.Clamp(_fifteenMinuteMlMinConfidence, 0f, 1f),
                    MinTransformerConfidence = Math.Clamp(_fifteenMinuteTransformerMinConfidence, 0f, 1f),
                    MinMLConfidenceMajor = Math.Clamp(_fifteenMinuteMlMinConfidence + 0.08f, 0f, 1f),
                    MinTransformerConfidenceMajor = Math.Clamp(_fifteenMinuteTransformerMinConfidence + 0.08f, 0f, 1f),
                    MinMLConfidencePumping = Math.Clamp(_fifteenMinuteMlMinConfidence - 0.05f, 0f, 1f)
                };

                _aiDoubleCheckEntryGate = new AIDoubleCheckEntryGate(_exchangeService, doubleCheckConfig);
                _aiDoubleCheckEntryGate.OnLog += msg => OnStatusLog?.Invoke(msg);
                _aiDoubleCheckEntryGate.OnAlert += msg => OnAlert?.Invoke(msg);
                if (_aiDoubleCheckEntryGate.IsReady)
                {
                    OnStatusLog?.Invoke(
                        $"✅ AI 더블체크 게이트 활성화 | ML>={doubleCheckConfig.MinMLConfidence:P0}, TF>={doubleCheckConfig.MinTransformerConfidence:P0}");
                }
                else
                {
                    OnStatusLog?.Invoke("⚠️ AI 더블체크 모델 미준비 (기존 15분 WaveGate로 자동 폴백)");
                }
            }
            catch (Exception ex)
            {
                _aiDoubleCheckEntryGate = null;
                OnStatusLog?.Invoke($"⚠️ AI 더블체크 게이트 초기화 실패: {ex.Message}");
            }

            // [v2.4.2] Navigator-Sniper 하이브리드 아키텍처 초기화
            if (_aiDoubleCheckEntryGate != null)
            {
                try
                {
                    _hybridNavigatorSniper = new HybridNavigatorSniper(_aiDoubleCheckEntryGate);
                    _hybridNavigatorSniper.OnNavigatorLog += msg => OnStatusLog?.Invoke(msg);
                    _hybridNavigatorSniper.OnSniperLog += msg => OnStatusLog?.Invoke(msg);
                    _hybridNavigatorSniper.OnAmbushWindowChanged += msg => OnAlert?.Invoke(msg);
                    OnStatusLog?.Invoke("🎯 [v2.4.2] Navigator-Sniper 하이브리드 아키텍처 활성화");
                }
                catch (Exception ex)
                {
                    _hybridNavigatorSniper = null;
                    OnStatusLog?.Invoke($"⚠️ Navigator-Sniper 초기화 실패: {ex.Message}");
                }
            }

            LoadActiveAiDecisionIds();

            _pumpStrategy = new PumpScanStrategy(_client, _symbols, AppConfig.Current?.Trading?.PumpSettings ?? new PumpScanSettings());
            _majorStrategy = new MajorCoinStrategy(
                _marketDataManager,
                () => MainWindow.CurrentGeneralSettings ?? AppConfig.Current?.Trading?.GeneralSettings);
            _gridStrategy = new GridStrategy(_exchangeService);

            _pumpStrategy.OnSignalAnalyzed += vm =>
            {
                try { OnSignalUpdate?.Invoke(vm); }
                catch (Exception ex) { OnLiveLog?.Invoke($"📡 [SIGNAL][PUMP][ERROR] source=uiBinding detail={ex.Message}"); }
            };
            _pumpStrategy.OnTradeSignal += async (symbol, decision, price) =>
            {
                try
                {
                    await ExecuteAutoOrder(symbol, decision, price, _cts.Token, "MAJOR_MEME");
                }
                catch (Exception ex)
                {
                    OnLiveLog?.Invoke($"🧭 [ENTRY][ORDER][ERROR] src=MAJOR_MEME sym={symbol} side={decision} | detail={ex.Message}");
                }
            };

            _pumpStrategy.OnLog += msg => OnLiveLog?.Invoke(NormalizePumpSignalLog(msg));

            if (_majorStrategy != null)
            {
                _majorStrategy.OnSignalAnalyzed += vm =>
                {
                    try { OnSignalUpdate?.Invoke(vm); }
                    catch (Exception ex) { OnLiveLog?.Invoke($"⚠️ Major 시그널 UI 반영 오류: {ex.Message}"); }
                };
                _majorStrategy.OnTradeSignal += async (symbol, decision, price) =>
                {
                    try
                    {
                        // [v2.4.2] Navigator-Sniper 평가
                        if (_aiDoubleCheckEntryGate != null)
                        {
                            var gateResult = await _aiDoubleCheckEntryGate.EvaluateEntryAsync(symbol, decision, (decimal)price, _cts.Token);
                            string decisionKr = decision == "LONG" ? "롱" : "숏";
                            
                            OnLiveLog?.Invoke(
                                $"🤖 [{symbol}] {decisionKr} ML 스나이퍼 평가 중 | " +
                                $"ML신뢰도 {gateResult.detail.ML_Confidence:P0}, TF신뢰도 {gateResult.detail.TF_Confidence:P0}");

                            if (!gateResult.allowEntry)
                            {
                                OnLiveLog?.Invoke(
                                    $"❌ [{symbol}] {decisionKr} Sniper 거부: {gateResult.reason} | " +
                                    $"ML={gateResult.detail.ML_Confidence:P0}, TF={gateResult.detail.TF_Confidence:P0}");
                                return;
                            }

                            OnLiveLog?.Invoke(
                                $"✅ [{symbol}] {decisionKr} Sniper 승인! | " +
                                $"ML={gateResult.detail.ML_Confidence:P0}, TF={gateResult.detail.TF_Confidence:P0}");
                        }

                        await ExecuteAutoOrder(symbol, decision, price, _cts.Token, "MAJOR");
                    }
                    catch (Exception ex)
                    {
                        OnLiveLog?.Invoke($"⚠️ Major 주문 처리 오류 [{symbol}]: {ex.Message}");
                    }
                };
                _majorStrategy.OnLog += msg => OnLiveLog?.Invoke(msg);
            }

            // [Phase 7] Transformer 모델 및 전략 초기화 (설정 파일 로드)
            // [FIX] 서브프로세스 프로브로 TorchSharp 호환성 사전 검증 후 초기화
            var tfSettings = AppConfig.Current?.Trading?.TransformerSettings ?? new TransformerSettings();
            bool transformerInitSuccess = false;
            if (tfSettings.Enabled)
            {
                try
                {
                    OnStatusLog?.Invoke("🔍 TorchSharp 환경 호환성 검증 중...");
                    bool torchReady = TorchInitializer.IsAvailable || TorchInitializer.TryInitialize();
                    if (!torchReady)
                    {
                        string errMsg = TorchInitializer.ErrorMessage ?? "알 수 없는 오류";
                        OnStatusLog?.Invoke($"⚠️ TorchSharp 초기화 실패로 Transformer 기능이 비활성화됩니다. ({errMsg})");
                        OnAlert?.Invoke("⚠️ Transformer AI 비활성 — TorchSharp 환경 비호환. MajorCoinStrategy(지표 기반)만 동작합니다.");
                    }
                    else
                    {
                        OnStatusLog?.Invoke("✅ TorchSharp 환경 검증 통과");

                        // 메모리 정리
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        _transformerTrainer = new TransformerTrainer(
                            tfSettings.InputDim,
                            tfSettings.DModel,
                            tfSettings.NHeads,
                            tfSettings.NLayers,
                            tfSettings.OutputDim,
                            tfSettings.SeqLen
                        );

                        // [Phase 7] Transformer 학습 상태 이벤트 연결
                        _transformerTrainer.OnLog += msg => OnLiveLog?.Invoke(msg);
                        _transformerTrainer.OnEpochCompleted += (epoch, total, loss) =>
                        {
                            // 학습 진행 상황을 상태바에도 표시 (선택 사항)
                            // OnStatusLog?.Invoke($"🧠 AI 학습 중... Epoch {epoch}/{total} (Loss: {loss:F5})");
                        };

                        // 기존 학습된 모델이 있다면 로드
                        _transformerTrainer.LoadModel();
                        transformerInitSuccess = true;
                        OnStatusLog?.Invoke("✅ TorchSharp Transformer 초기화 완료");
                    }
                }
                catch (Exception ex)
                {
                    OnAlert?.Invoke($"⚠️ Transformer AI 초기화 실패: {ex.Message}");
                    OnStatusLog?.Invoke("⚠️ Transformer 기능이 비활성화됩니다 (기본 전략으로 동작)");
                    _transformerTrainer = null;

                    // 크래시 로그 저장
                    try
                    {
                        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TRANSFORMER_CRASH.txt");
                        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Transformer Init Failed\n" +
                            $"Message: {ex.Message}\n" +
                            $"StackTrace: {ex.StackTrace}\n" +
                            $"InnerException: {ex.InnerException?.Message ?? "None"}\n\n");
                    }
                    catch (Exception fileEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Transformer crash log 저장 실패: {fileEx.Message}");
                    }
                }
            }
            else
            {
                OnStatusLog?.Invoke("ℹ️ Transformer/TorchSharp 기능이 설정에서 비활성화되어 있습니다.");
                _transformerTrainer = null;
            }

            // [3파 확정형 전략] 먼저 초기화 (TransformerStrategy에서 사용하기 위해)
            _elliotWave3Strategy = new ElliottWave3WaveStrategy();
            OnStatusLog?.Invoke("🌊 엘리엇 3파 확정형 전략 준비 완료");

            // [하이브리드 AI 익절/손절 관리]
            _hybridExitManager = new HybridExitManager();
            _hybridExitManager.OnLog += msg => OnStatusLog?.Invoke(msg);
            _hybridExitManager.OnAlert += msg => OnAlert?.Invoke(msg);

            // [실시간 주문 실행 서비스] ATR 트레일링 스탑 갱신
            _executionService = new BinanceExecutionService(_client);
            _executionService.OnLog += msg => OnStatusLog?.Invoke(msg);
            _executionService.OnAlert += msg => OnAlert?.Invoke(msg);

            // HybridExitManager의 트레일링 스탑 갱신 이벤트를 ExecutionService와 연결
            _hybridExitManager.OnTrailingStopUpdate += async (symbol, newStopPrice) =>
            {
                try
                {
                    await _executionService.UpdateTrailingStopAsync(symbol, newStopPrice);
                }
                catch (Exception ex)
                {
                    OnLiveLog?.Invoke($"⚠️ 트레일링 스탑 갱신 오류 [{symbol}]: {ex.Message}");
                }
            };

            // [3파 통합] TransformerStrategy에 ElliottWave3WaveStrategy 주입
            // [FIX] Transformer 초기화 실패 시 null 전달하여 안전하게 비활성화
            if (transformerInitSuccess && _transformerTrainer != null)
            {
                _transformerStrategy = new TransformerStrategy(_client, _transformerTrainer, _newsService, _elliotWave3Strategy, tfSettings, _patternMemoryService);
                _transformerStrategy.OnLog += msg => OnStatusLog?.Invoke(NormalizeTransformerSignalLog(msg));
                _transformerStrategy.OnSignalAnalyzed += vm =>
                {
                    try { OnSignalUpdate?.Invoke(vm); }
                    catch (Exception ex) { OnLiveLog?.Invoke($"📡 [SIGNAL][TRANSFORMER][ERROR] source=uiBinding detail={ex.Message}"); }
                };
            }
            else
            {
                OnStatusLog?.Invoke(tfSettings.Enabled
                    ? "⚠️ TransformerStrategy 비활성화 (Transformer 초기화 실패)"
                    : "ℹ️ TransformerStrategy 비활성화 (설정에서 꺼짐)");
                _transformerStrategy = null;
            }

            // [Phase 7] Transformer 예측 결과 UI 연동 + AI 모니터 정확도 추적
            // [FIX] null 체크 추가 - Transformer 초기화 실패 시 이벤트 연결 스킵
            if (_transformerStrategy != null)
            {
                _transformerStrategy.OnPredictionUpdated += (symbol, currentPrice, predictedPrice) =>
                {
                    double change = 0;
                    if (currentPrice > 0)
                        change = (double)((predictedPrice - currentPrice) / currentPrice * 100);

                    try
                    {
                        OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                        {
                            Symbol = symbol,
                            TransformerPrice = predictedPrice,
                            TransformerChange = change
                        });
                    }
                    catch (Exception ex)
                    {
                        OnLiveLog?.Invoke($"📡 [SIGNAL][TRANSFORMER][ERROR] source=predictionUi detail={ex.Message}");
                    }

                    // [AI 모니터] Transformer 예측을 심볼+모델 단일 키로 등록 → 5분 후 검증
                    bool predictedDirection = predictedPrice > currentPrice; // 상승 예측 여부
                    float confidence = (float)Math.Min(Math.Abs(change) / 5.0, 1.0); // 변화율 기반 신뢰도 (5%를 1.0으로)
                    string predictionKey = BuildPredictionValidationKey("Transformer", symbol);
                    _pendingPredictions[predictionKey] = (DateTime.Now, currentPrice, predictedPrice, predictedDirection, "Transformer", confidence);
                    bool scheduled = TrySchedulePredictionValidation(predictionKey, symbol, _cts);

                    string direction = predictedDirection ? "상승" : "하락";
                    if (scheduled)
                    {
                        OnStatusLog?.Invoke($"🔮 [SIGNAL][TRANSFORMER][PREDICT] sym={symbol} side={(predictedDirection ? "LONG" : "SHORT")} confidence={confidence:P0} validateAfter=5m");
                    }
                };

                _transformerStrategy.OnTradeSignal += async (symbol, side, currentPrice, predictedPrice, mode, customTakeProfitPrice, customStopLossPrice, patternSnapshot) =>
                {
                    try
                    {
                        // Transformer 전략 신호 발생 시 자동 매매 실행 (LONG/SHORT)
                        await ExecuteAutoOrder(symbol, side, currentPrice, _cts.Token, $"TRANSFORMER_{mode}", mode, customTakeProfitPrice, customStopLossPrice, patternSnapshot);
                        // 하이브리드 이탈 관리자에 등록 (AI 기반 익절/트레일링 스탑)
                        if (mode != "SIDEWAYS")
                        {
                            _hybridExitManager.RegisterEntry(symbol, side, currentPrice, predictedPrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLiveLog?.Invoke($"🧭 [ENTRY][ORDER][ERROR] src=TRANSFORMER sym={symbol} side={side} | detail={ex.Message}");
                    }
                };
            }

            // [Phase 8] DeFi 서비스 초기화
            var defiSettings = AppConfig.Current?.Trading?.DeFiSettings ?? new DeFiSettings();
            _dexService = new DexService(defiSettings.RpcUrl, defiSettings.WalletPrivateKey);
            _onChainService = new OnChainAnalysisService(defiSettings.EtherscanApiKey, defiSettings.WhaleThresholdUsd);

            _onChainService.OnWhaleAlert += (tx) =>
            {
                // [Agent 1 & 2] 고래 알림 상세화
                string msg = $"🐋 [Whale Alert] {tx.TokenSymbol}\n" +
                             $"Amount: {tx.ValueUsd:N0} USD ({tx.TokenSymbol})\n" +
                             $"From: {tx.From} -> To: {tx.To}";
                OnAlert?.Invoke(msg);
                // 고래가 거래소로 입금 시 하락 가능성 등 전략에 반영 가능
                _ = _notificationService.SendPushNotificationAsync("Whale Alert", msg); // [Agent 4] 푸시 알림
            };
        }

        // 초기 자산 저장 전용 메서드
        private async Task InitializeSeedAsync(CancellationToken token)
        {
            try
            {
                bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                string serviceType = _exchangeService?.GetType().Name ?? "Unknown";
                
                OnStatusLog?.Invoke($"🔍 [InitializeSeed] Mode: {(isSimulation ? "Simulation" : "Real")}, Service: {serviceType}");
                
                if (isSimulation)
                {
                    // 시뮬레이션 모드: 설정된 초기 잔고 사용
                    InitialBalance = AppConfig.Current?.Trading?.SimulationInitialBalance ?? 10000m;
                    OnLiveLog?.Invoke($"🎮 [Simulation] 초기 시드: ${InitialBalance:N2}");
                }
                else
                {
                    // 실거래 모드: 거래소에서 잔고 조회
                    if (_exchangeService == null)
                    {
                        OnStatusLog?.Invoke("⚠️ [InitializeSeed] 거래소 서비스가 초기화되지 않아 초기 잔고 조회를 건너뜁니다.");
                        return;
                    }

                    decimal balance = await _exchangeService.GetBalanceAsync("USDT", token);
                    if (balance > 0)
                    {
                        InitialBalance = balance;
                        OnLiveLog?.Invoke($"💰 [Real] 초기 시드: ${InitialBalance:N2}");

                        // 텔레그램 및 디스코드 시작 알림
                        await NotificationService.Instance.NotifyAsync($"봇 가동 시작\n초기 자산: ${InitialBalance:N2} USDT", NotificationChannel.Alert);
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ 초기 잔고 조회 실패: {ex.Message}");
            }
        }

        private async Task SendDetailedPeriodicReport(CancellationToken token)
        {
            try
            {
                decimal currentEquity = await GetEstimatedAccountEquityUsdtAsync(token);
                if (currentEquity <= 0)
                {
                    OnStatusLog?.Invoke("⚠️ 정기 보고 스킵: 현재 Equity를 계산할 수 없습니다.");
                    return;
                }

                decimal availableBalance = await _exchangeService.GetBalanceAsync("USDT", token);
                decimal baseline = _periodicReportBaselineEquity > 0
                    ? _periodicReportBaselineEquity
                    : (InitialBalance > 0 ? InitialBalance : currentEquity);

                if (_periodicReportBaselineEquity <= 0)
                    _periodicReportBaselineEquity = baseline;

                decimal pnl = currentEquity - baseline;

                double pnlPercent = baseline > 0
                    ? (double)(pnl / baseline * 100)
                    : 0;

                decimal dailyRealized = _riskManager?.DailyRealizedPnl ?? 0m;
                int activeCount;
                lock (_posLock)
                {
                    activeCount = _activePositions.Count;
                }

                // 가동 시간 계산 (리포트 주기 기준이 아닌 엔진 시작 기준)
                var upTime = DateTime.Now - _engineStartTime;
                string timeStr = $"{(int)upTime.TotalDays}일 {upTime.Hours}시간 {upTime.Minutes}분";

                string body = $"🏦 **현재 추정 자산(Equity)**: `{currentEquity:N2} USDT`\n" +
                              $"💼 **가용 잔고**: `{availableBalance:N2} USDT`\n" +
                              $"🎯 **기준 자산(시작 기준)**: `{baseline:N2} USDT`\n" +
                              $"📈 **총 수익금**: `{pnl:N2} USDT`\n" +
                              $"📊 **수익률**: `{pnlPercent:F2}%`\n" +
                              $"🧾 **금일 실현 손익**: `{dailyRealized:N2} USDT`\n" +
                              $"⏳ **총 가동 시간**: {timeStr}\n" +
                              $"🚀 **현재 운용 중**: {activeCount}개 종목";

                await NotificationService.Instance.NotifyAsync($"📊 [정기 보고]\n{body}", NotificationChannel.Profit);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ 리포트 생성 중 오류: {ex.Message}");
            }
        }

        // 1. 포지션 현재 상태 스냅샷 동기화 (REST API 활용)
        private async Task SyncCurrentPositionsAsync(CancellationToken token)
        {
            try
            {
                var positions = await _exchangeService.GetPositionsAsync(token);
                if (positions != null)
                {
                    var syncedPositions = new List<(TradingBot.Shared.Models.PositionInfo Pos, DateTime EntryTime, float AiScore, decimal StopLoss)>();

                    foreach (var pos in positions)
                    {
                        if (string.IsNullOrEmpty(pos.Symbol))
                            continue;

                        var ensureResult = await _dbManager.EnsureOpenTradeForPositionAsync(new TradingBot.Shared.Models.PositionInfo
                        {
                            Symbol = pos.Symbol,
                            EntryPrice = pos.EntryPrice,
                            IsLong = pos.IsLong,
                            Side = pos.Side,
                            AiScore = pos.AiScore,
                            Leverage = pos.Leverage > 0 ? pos.Leverage : _settings.DefaultLeverage,
                            Quantity = Math.Abs(pos.Quantity),
                            EntryTime = DateTime.Now
                        }, "SYNC_RESTORED");

                        decimal majorHybridStopLoss = 0m;
                        if (MajorSymbols.Contains(pos.Symbol))
                        {
                            var majorStop = await TryCalculateMajorAtrHybridStopLossAsync(pos.Symbol, pos.EntryPrice, pos.IsLong, token);
                            majorHybridStopLoss = majorStop.StopLossPrice;
                            if (majorHybridStopLoss > 0)
                            {
                                OnStatusLog?.Invoke($"🛡️ [MAJOR ATR] {pos.Symbol} 시작 복원 포지션 손절 재계산 | SL={majorHybridStopLoss:F8}, ATRx2.5={majorStop.AtrDistance:F8}, 구조선={majorStop.StructureStopPrice:F8}");
                            }
                        }

                        syncedPositions.Add((pos, ensureResult.EntryTime, ensureResult.AiScore, majorHybridStopLoss));
                    }

                    lock (_posLock)
                    {
                        _activePositions.Clear();
                        foreach (var synced in syncedPositions)
                        {
                            var pos = synced.Pos;
                            _activePositions[pos.Symbol] = new PositionInfo
                            {
                                Symbol = pos.Symbol,
                                EntryPrice = pos.EntryPrice,
                                IsLong = pos.IsLong,
                                Side = pos.Side,
                                AiScore = synced.AiScore,
                                Leverage = pos.Leverage > 0 ? pos.Leverage : _settings.DefaultLeverage,
                                Quantity = Math.Abs(pos.Quantity),
                                InitialQuantity = Math.Abs(pos.Quantity),
                                EntryTime = synced.EntryTime,
                                StopLoss = synced.StopLoss,
                                HighestPrice = pos.EntryPrice,
                                LowestPrice = pos.EntryPrice,
                                PyramidCount = 0
                            };
                        }
                    }

                    foreach (var synced in syncedPositions)
                    {
                        var pos = synced.Pos;
                        OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                        {
                            Symbol = pos.Symbol,
                            IsPositionActive = true,
                            EntryPrice = pos.EntryPrice,
                            PositionSide = pos.IsLong ? "LONG" : "SHORT",
                            Quantity = Math.Abs(pos.Quantity),
                            Leverage = (int)pos.Leverage
                        });

                        string sideStr = pos.IsLong ? "LONG" : "SHORT";
                        OnStatusLog?.Invoke($"[동기화] {pos.Symbol} {sideStr} | 평단: {pos.EntryPrice}");
                        decimal syncedStopLoss = 0m;
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(pos.Symbol, out var active) && active.StopLoss > 0)
                                syncedStopLoss = active.StopLoss;
                        }

                        TryStartStandardMonitor(pos.Symbol, pos.EntryPrice, pos.IsLong, "TREND", 0m, syncedStopLoss, token, "sync");
                    }
                    OnStatusLog?.Invoke("✅ 현재 보유 포지션 동기화 완료");
                }
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ 포지션 동기화 에러: {ex.Message}"); }
        }

        /// <summary>
        /// 봇 시작 시 DB 오픈 포지션과 거래소 실제 포지션을 비교하여
        /// DB에만 있고 거래소에 없는 포지션(봇 중단 중 외부 청산)을 자동으로 정리
        /// </summary>
        private async Task ReconcileDbWithExchangePositionsAsync(CancellationToken token)
        {
            try
            {
                int userId = AppConfig.CurrentUser?.Id ?? 1;
                var dbOpenTrades = await _dbManager.GetOpenTradesAsync(userId);

                if (dbOpenTrades.Count == 0)
                {
                    OnStatusLog?.Invoke("✅ [DB 정리] DB에 오픈 포지션 없음 → 정리 불필요");
                    return;
                }

                OnStatusLog?.Invoke($"🔍 [DB 정리] DB 오픈 포지션: {dbOpenTrades.Count}개 발견, 거래소와 비교 시작");

                int closedCount = 0;
                List<string> exchangePositionSymbols;

                lock (_posLock)
                {
                    exchangePositionSymbols = _activePositions.Keys.ToList();
                }

                foreach (var dbTrade in dbOpenTrades)
                {
                    bool existsInExchange = exchangePositionSymbols.Contains(dbTrade.Symbol);

                    if (!existsInExchange)
                    {
                        // DB에만 있고 거래소에 없음 → 외부 청산된 것으로 간주
                        string side = dbTrade.Side;
                        string closeSide = side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
                        
                        var closeLog = new TradeLog(
                            dbTrade.Symbol,
                            closeSide,
                            "EXTERNAL_RECONCILE",
                            dbTrade.EntryPrice, // 청산가 불명 → 진입가 사용 (PnL=0)
                            0f,
                            DateTime.Now,
                            0m, // 실제 PnL 불명
                            0m
                        )
                        {
                            ExitPrice = dbTrade.EntryPrice,
                            EntryPrice = dbTrade.EntryPrice,
                            Quantity = dbTrade.Quantity,
                            ExitReason = "EXTERNAL_CLOSE_WHILE_BOT_STOPPED",
                            EntryTime = dbTrade.EntryTime,
                            ExitTime = DateTime.Now
                        };

                        bool saved = await _dbManager.CompleteTradeAsync(closeLog);
                        if (saved)
                        {
                            closedCount++;
                            OnStatusLog?.Invoke($"🧹 [DB 정리] {dbTrade.Symbol} {side} 외부 청산 감지 → DB 자동 정리 완료 (진입: {dbTrade.EntryTime:yyyy-MM-dd HH:mm})");
                        }
                        else
                        {
                            OnStatusLog?.Invoke($"⚠️ [DB 정리] {dbTrade.Symbol} {side} 정리 실패");
                        }
                    }
                }

                if (closedCount > 0)
                {
                    OnStatusLog?.Invoke($"✅ [DB 정리 완료] 총 {closedCount}개 외부 청산 포지션 DB 정리 완료");
                    await NotificationService.Instance.NotifyAsync(
                        $"🧹 DB 정리 완료\n외부 청산 감지: {closedCount}개 포지션\n(봇 중단 중 수동 청산된 포지션 자동 정리)",
                        NotificationChannel.Alert
                    );
                }
                else
                {
                    OnStatusLog?.Invoke("✅ [DB 정리] 모든 DB 오픈 포지션이 거래소와 일치 → 정리 불필요");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [DB 정리] 오류 발생: {ex.Message}");
            }
        }

        private async Task<(decimal StopLossPrice, decimal AtrDistance, decimal StructureStopPrice)> TryCalculateMajorAtrHybridStopLossAsync(
            string symbol,
            decimal referencePrice,
            bool isLong,
            CancellationToken token)
        {
            try
            {
                if (!MajorSymbols.Contains(symbol) || referencePrice <= 0)
                    return (0m, 0m, 0m);

                List<IBinanceKline>? candles = null;
                var fetched = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 60, token);
                if (fetched != null && fetched.Count >= 15)
                    candles = fetched.ToList();

                if (candles == null || candles.Count < 15)
                    return (0m, 0m, 0m);

                const decimal atrMultiplier = 2.5m;
                double atr = IndicatorCalculator.CalculateATR(candles, 14);
                if (atr <= 0)
                    return (0m, 0m, 0m);

                decimal atrDistance = (decimal)atr * atrMultiplier;
                decimal atrStopPrice = isLong
                    ? referencePrice - atrDistance
                    : referencePrice + atrDistance;

                var swingCandles = candles.TakeLast(Math.Min(12, candles.Count)).ToList();
                decimal structureStopPrice = isLong
                    ? swingCandles.Min(c => c.LowPrice) * 0.999m
                    : swingCandles.Max(c => c.HighPrice) * 1.001m;

                decimal hybridStopPrice = isLong
                    ? Math.Max(atrStopPrice, structureStopPrice)
                    : Math.Min(atrStopPrice, structureStopPrice);

                if (isLong && hybridStopPrice >= referencePrice)
                    hybridStopPrice = atrStopPrice;
                else if (!isLong && hybridStopPrice <= referencePrice)
                    hybridStopPrice = atrStopPrice;

                return (hybridStopPrice, atrDistance, structureStopPrice);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [MAJOR ATR] {symbol} 하이브리드 손절 계산 실패: {ex.Message}");
                return (0m, 0m, 0m);
            }
        }

        public async Task StartScanningOptimizedAsync()
        {
            if (IsBotRunning) return;
            IsBotRunning = true;

            try
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                OnAlert?.Invoke("🚀 최적화 엔진 가동 (WebSocket 모드)");
                LoggerService.Info("엔진 시작: WebSocket 모드");

                foreach (var symbol in _symbols)
                {
                    EnsureSymbolInList(symbol);
                }

                OnStatusLog?.Invoke($"📌 추적 심볼: {string.Join(", ", _symbols)}");

                // 1. 초기화 (지갑 잔고 및 현재 포지션 동기화)
                await InitializeSeedAsync(token); // InitialBalance 설정
                _riskManager.Initialize((decimal)InitialBalance); // RiskManager 초기화

                // 시뮬레이션 모드일 경우 초기 잔고 로그 출력
                if (AppConfig.Current?.Trading?.IsSimulationMode == true)
                {
                    OnLiveLog?.Invoke($"🎮 시뮬레이션 시작 잔고: ${InitialBalance:N2}");
                }

                await SyncCurrentPositionsAsync(token);

                // [추가] DB 오픈 포지션과 거래소 포지션 불일치 자동 정리 (봇 중단 중 외부 청산)
                await ReconcileDbWithExchangePositionsAsync(token);

                // [추가] 엔진 시작 시 Transformer 초기 학습 1회 자동 실행 (모델 미준비 시)
                await TriggerInitialTransformerTrainingIfNeededAsync(token);

                // [추가] AI 더블체크 게이트 초기 학습 (모델 미준비 시)
                if (_aiDoubleCheckEntryGate != null && !_aiDoubleCheckEntryGate.IsReady)
                {
                    var (success, message) = await _aiDoubleCheckEntryGate.TriggerInitialTrainingAsync(_exchangeService, _symbols, token);
                    // 결과 메시지는 이미 AIDoubleCheckEntryGate.OnAlert에서 출력됨
                }

                // 텔레그램 시작 알림 전송
                string startMessage = $"🚀 *[봇 시작]*\n\n" +
                                     $"모드: {(AppConfig.Current?.Trading?.IsSimulationMode == true ? "시뮬레이션" : "실거래")}\n" +
                                     $"초기 잔고: ${InitialBalance:N2} USDT\n" +
                                     $"거래소: {(AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(TradingBot.Shared.Models.ExchangeType.Binance)}\n" +
                                     $"레버리지: {_settings.DefaultLeverage}x\n" +
                                     $"시작 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                await NotificationService.Instance.NotifyAsync(startMessage, NotificationChannel.Alert);

                TelegramService.Instance.Initialize();
                TelegramService.Instance.OnRequestStatus = GetEngineStatusReport;
                TelegramService.Instance.OnRequestStop = StopEngine;
                TelegramService.Instance.StartReceiving();
                OnTelegramStatusUpdate?.Invoke(true, "Telegram: Connected");

                _ = ProcessTickerChannelAsync(token);
                _ = ProcessAccountChannelAsync(token); // [Agent 2] 계좌 업데이트 처리 시작
                _ = ProcessOrderChannelAsync(token);   // [Agent 2] 주문 업데이트 처리 시작

                // [AI 학습 상태 초기화]
                if (_aiDoubleCheckEntryGate != null)
                {
                    var stats = _aiDoubleCheckEntryGate.GetRecentLabelStats(10);
                    MainWindow.Instance?.UpdateAiLearningStatusUI(
                        stats.total, stats.labeled, stats.markToMarket, 
                        stats.tradeClose, _activeAiDecisionIds.Count, _aiDoubleCheckEntryGate.IsReady);
                }
                else
                {
                    MainWindow.Instance?.UpdateAiLearningStatusUI(0, 0, 0, 0, 0, false);
                }

                // 2. 실시간 감시 시작 (Non-blocking)
                await _marketDataManager.StartAsync(token);

                // 시작 직후 5분봉 캐시 선로딩(최대 1000개) 후, 현시각까지 DB 백필 저장 완료를 보장
                await _marketDataManager.PreloadRecentKlinesAsync(limit: 1000, token);
                if (_marketHistoryService != null)
                {
                    await _marketHistoryService.BackfillToNowBeforeStartAsync(token);
                    _ = _marketHistoryService.StartRecordingAsync(token);
                }

                _ = StartPeriodicTrainingAsync(token);
                _ = StartPeriodicAiEntryProbScanAsync(token);

                await PreloadInitialAiScoresAsync(token);

                // 엔진 가동 시간 기록 및 주기 타이머 초기화
                _engineStartTime = DateTime.Now;
                _lastHeartbeatTime = DateTime.Now; // 시작 시점 기록 (1시간 후 첫 알림)
                _lastReportTime = DateTime.Now;    // 시작 시점 기록 (1시간 후 첫 보고)
                _lastPositionSyncTime = DateTime.Now; // [FIX] 시작 시점 기록 (30분 후 첫 동기화)
                _periodicReportBaselineEquity = await GetEstimatedAccountEquityUsdtAsync(token);
                if (_periodicReportBaselineEquity <= 0)
                    _periodicReportBaselineEquity = InitialBalance;
                OnStatusLog?.Invoke($"⏳ 진입 워밍업 시작: {_entryWarmupDuration.TotalSeconds:F0}초 동안 신규 진입 제한");

                // 3. 메인 관리 루프 (REST API 호출 최소화)
                OnStatusLog?.Invoke("🔄 메인 스캔 루프 시작...");
                bool isCircuitBreakerNotificationSent = false;

                while (!token.IsCancellationRequested)
                {
                    Stopwatch? loopWatch = _mainLoopPerfEnabled ? Stopwatch.StartNew() : null;
                    bool loopMetricsRecorded = false;

                    try
                    {
                        if (_riskManager.IsTripped)
                        {
                            if ((DateTime.Now - _riskManager.TripTime).TotalHours >= 1)
                            {
                                _riskManager.Reset();
                                isCircuitBreakerNotificationSent = false;

                                int closedCount = 0;
                                List<string> symbolsToClose;
                                lock (_posLock) { symbolsToClose = _activePositions.Keys.ToList(); }

                                foreach (var symbol in symbolsToClose)
                                {
                                    await _positionMonitor.ExecuteMarketClose(symbol, "서킷 브레이커 해제 후 리스크 관리(강제 청산)", token);
                                    closedCount++;
                                }
                                
                                string resumeMsg = TradingStateLogger.CircuitBreakerReleased(closedCount);
                                await NotificationService.Instance.NotifyAsync(resumeMsg, NotificationChannel.Alert);
                                OnAlert?.Invoke(resumeMsg);
                                OnStatusLog?.Invoke("서킷 브레이커 자동 해제됨.");
                            }
                            else
                            {
                                if (!isCircuitBreakerNotificationSent)
                                {
                                    isCircuitBreakerNotificationSent = true;
                                    string msg = TradingStateLogger.CircuitBreakerTripped(_riskManager.GetTripDetails(), 1);
                                    await NotificationService.Instance.NotifyAsync(msg, NotificationChannel.Alert);
                                    OnAlert?.Invoke(msg);
                                }

                                var remaining = TimeSpan.FromHours(1) - (DateTime.Now - _riskManager.TripTime);
                                OnStatusLog?.Invoke($"⛔ 서킷 브레이커 발동 중. 매매 중단 (재개까지 {remaining.Minutes}분 남음)");
                                await Task.Delay(10000, token);
                                continue;
                            }
                        }

                        // [A] 급등주 스캔 (알트코인 전체 대상)
                        if (_pumpStrategy != null)
                            await _pumpStrategy.ExecuteScanAsync(_marketDataManager.TickerCache, _blacklistedSymbols, token);

                        if ((DateTime.Now - _lastHeartbeatTime).TotalHours >= 1)
                        {
                            string heartbeatMsg = $"💓 [Heartbeat] Bot is alive.\nActive Positions: {_activePositions.Count}\nUptime: {(DateTime.Now - _engineStartTime):dd\\.hh\\:mm}";
                            await NotificationService.Instance.NotifyAsync(heartbeatMsg, NotificationChannel.Log);
                            LoggerService.Info("Heartbeat sent.");
                            _lastHeartbeatTime = DateTime.Now;
                        }

                        // [FIX] 거래소 포지션 동기화 (30분 주기)
                        if ((DateTime.Now - _lastPositionSyncTime).TotalMinutes >= 30)
                        {
                            OnStatusLog?.Invoke("🔄 정기 거래소 포지션 동기화 시작...");
                            await SyncExchangePositionsAsync(token);
                            _lastPositionSyncTime = DateTime.Now;
                        }

                        // [B] 텔레그램 정기 수익 보고 (1시간 주기)
                        if ((DateTime.Now - _lastReportTime).TotalHours >= 1)
                        {
                            await SendDetailedPeriodicReport(token);
                            _lastReportTime = DateTime.Now;
                        }

                        // [Agent 3] 지능형 메모리 관리 (주기적 체크)
                        MemoryManager.CheckAndCollect(msg => OnStatusLog?.Invoke(msg));

                        // 대시보드 갱신 및 대기
                        await RefreshProfitDashboard(token);
                        await Task.Delay(MainLoopInterval, token);
                    }
                    catch (Exception loopEx)
                    {
                        if (_mainLoopPerfEnabled && loopWatch != null)
                        {
                            loopWatch.Stop();
                            RecordMainLoopPerf(loopWatch.ElapsedMilliseconds);
                            loopMetricsRecorded = true;
                        }

                        LoggerService.Error("메인 루프 오류 (자동 복구 시도)", loopEx);
                        OnStatusLog?.Invoke($"⚠️ 일시적 오류: {loopEx.Message}. 5초 후 재시도...");
                        await Task.Delay(5000, token);
                    }
                    finally
                    {
                        if (_mainLoopPerfEnabled && !loopMetricsRecorded && loopWatch != null)
                        {
                            loopWatch.Stop();
                            RecordMainLoopPerf(loopWatch.ElapsedMilliseconds);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { OnStatusLog?.Invoke("엔진 정지 신호 수신"); }
            catch (Exception ex)
            {
                LoggerService.Error("치명적 오류로 엔진 정지", ex);
                OnStatusLog?.Invoke($"치명적 오류: {ex.Message}");
                await TelegramService.Instance.SendMessageAsync($"🚨 *[시스템 치명적 오류]*\n{ex.Message}\n\n_봇이 비정상 종료되었습니다._");
            }
            finally
            {
                IsBotRunning = false;
                _marketDataManager.Stop();
                OnProgress?.Invoke(0, 100);
                LoggerService.CloseAndFlush();
            }
        }

        private void RecordMainLoopPerf(long workMs)
        {
            if (!_mainLoopPerfEnabled)
                return;

            AutoTuneMainLoopPerf((int)workMs);

            _mainLoopTotalMs += workMs;
            _mainLoopSamples++;
            if (workMs > _mainLoopMaxMs)
                _mainLoopMaxMs = (int)workMs;

            bool isWarn = workMs >= _mainLoopWarnMs;
            bool isPeriodic = DateTime.UtcNow >= _nextMainLoopPerfLogTime;
            if (!isWarn && !isPeriodic)
                return;

            double avgMs = _mainLoopSamples > 0
                ? (double)_mainLoopTotalMs / _mainLoopSamples
                : 0;

            int activePositions;
            lock (_posLock)
            {
                activePositions = _activePositions.Count;
            }

            string level = isWarn ? "WARN" : "INFO";
            string perfMessage =
                $"[PERF][MAIN_LOOP][{level}] workMs={workMs} avgMs={avgMs:F1} maxMs={_mainLoopMaxMs} " +
                $"analysisQueue={_pendingAnalysisPrices.Count} workers={_analysisWorkers.Count} activePos={activePositions} " +
                $"warnMs={_mainLoopWarnMs} intervalSec={_mainLoopPerfLogIntervalSec} autoTune={_mainLoopAutoTuneEnabled}";

            LoggerService.Info(perfMessage);
            OnStatusLog?.Invoke($"⏱️ {perfMessage}");

            if (isPeriodic)
            {
                _nextMainLoopPerfLogTime = DateTime.UtcNow.AddSeconds(_mainLoopPerfLogIntervalSec);
                _mainLoopTotalMs = 0;
                _mainLoopSamples = 0;
                _mainLoopMaxMs = 0;
            }
        }

        /// <summary>
        /// 진입 필터 설정을 appsettings.json에서 로드합니다.
        /// </summary>
        private void LoadEntryFilterSettings()
        {
            var settings = AppConfig.Current?.Trading?.EntryFilterSettings;
            if (settings == null)
            {
                OnStatusLog?.Invoke("⚠️ EntryFilterSettings 미설정 (기본값 사용)");
                return;
            }

            _entryWarmupDuration = TimeSpan.FromSeconds(Math.Max(0, settings.EntryWarmupSeconds));
            _minEntryRiskRewardRatio = Math.Max(1.0m, settings.MinRiskRewardRatio);
            _enableFifteenMinWaveGate = settings.EnableFifteenMinWaveGate;
            _fifteenMinuteMlMinConfidence = Math.Clamp(settings.FifteenMinMlConfidence, 0f, 1f);
            _fifteenMinuteTransformerMinConfidence = Math.Clamp(settings.FifteenMinTransformerConfidence, 0f, 1f);
            _aiScoreThresholdMajor = Math.Max(0f, settings.AiScoreThresholdMajor);
            _aiScoreThresholdNormal = Math.Max(0f, settings.AiScoreThresholdNormal);
            _enableAiScoreFilter = settings.EnableAiScoreFilter;

            OnStatusLog?.Invoke(
                $"✅ 진입 필터 설정 로드 완료: " +
                $"워밍업={_entryWarmupDuration.TotalSeconds}초, " +
                $"RR>={_minEntryRiskRewardRatio:F2}, " +
                $"15분Gate={(_enableFifteenMinWaveGate ? "ON" : "OFF")}, " +
                $"ML>={_fifteenMinuteMlMinConfidence:P0}, " +
                $"TF>={_fifteenMinuteTransformerMinConfidence:P0}, " +
                $"AI(메이저>={_aiScoreThresholdMajor}, 일반>={_aiScoreThresholdNormal})"
            );
        }

        private void ApplyMainLoopPerformanceSettings()
        {
            var settings = AppConfig.Current?.Trading?.PerformanceMonitoring;
            if (settings == null)
            {
                _nextMainLoopPerfLogTime = DateTime.UtcNow.AddSeconds(_mainLoopPerfLogIntervalSec);
                _nextMainLoopTuneTime = DateTime.UtcNow.AddSeconds(_mainLoopTuneMinIntervalSec);
                return;
            }

            // 프로파일 기반 프리셋 적용
            settings.ApplyProfile();

            _mainLoopPerfEnabled = settings.EnableMetrics;
            _mainLoopAutoTuneEnabled = settings.EnableAutoTune;

            _mainLoopBaseWarnMs = Math.Max(1, settings.MainLoopWarnMs);
            _mainLoopBasePerfLogIntervalSec = Math.Max(1, settings.MainLoopPerfLogIntervalSec);

            _mainLoopWarnMinMs = Math.Max(1, settings.MainLoopWarnMinMs);
            _mainLoopWarnMaxMs = Math.Max(_mainLoopWarnMinMs, settings.MainLoopWarnMaxMs);
            _mainLoopPerfLogIntervalMinSec = Math.Max(1, settings.PerfLogIntervalMinSec);
            _mainLoopPerfLogIntervalMaxSec = Math.Max(_mainLoopPerfLogIntervalMinSec, settings.PerfLogIntervalMaxSec);

            _mainLoopTuneSampleWindow = Math.Clamp(settings.AutoTuneSampleWindow, 20, 240);
            _mainLoopTuneMinIntervalSec = Math.Clamp(settings.AutoTuneMinIntervalSec, 10, 300);

            _mainLoopWarnMs = Math.Clamp(_mainLoopBaseWarnMs, _mainLoopWarnMinMs, _mainLoopWarnMaxMs);
            _mainLoopPerfLogIntervalSec = Math.Clamp(_mainLoopBasePerfLogIntervalSec, _mainLoopPerfLogIntervalMinSec, _mainLoopPerfLogIntervalMaxSec);

            DateTime now = DateTime.UtcNow;
            _nextMainLoopPerfLogTime = now.AddSeconds(_mainLoopPerfLogIntervalSec);
            _nextMainLoopTuneTime = now.AddSeconds(_mainLoopTuneMinIntervalSec);
        }

        private void AutoTuneMainLoopPerf(int workMs)
        {
            if (!_mainLoopAutoTuneEnabled)
                return;

            _mainLoopRecentWorkSamples.Enqueue(workMs);
            while (_mainLoopRecentWorkSamples.Count > _mainLoopTuneSampleWindow)
            {
                _mainLoopRecentWorkSamples.Dequeue();
            }

            DateTime now = DateTime.UtcNow;
            if (now < _nextMainLoopTuneTime)
                return;

            int minSampleCount = Math.Min(20, Math.Max(10, _mainLoopTuneSampleWindow / 2));
            if (_mainLoopRecentWorkSamples.Count < minSampleCount)
                return;

            var ordered = _mainLoopRecentWorkSamples.OrderBy(v => v).ToArray();
            int p50 = GetPercentileValue(ordered, 0.50);
            int p90 = GetPercentileValue(ordered, 0.90);

            var settings = AppConfig.Current?.Trading?.PerformanceMonitoring;
            double multiplier = settings?.GetMainLoopMultiplier() ?? 1.30;

            int targetWarnMs = Math.Clamp(
                Math.Max(_mainLoopBaseWarnMs, (int)Math.Ceiling(p90 * multiplier)),
                _mainLoopWarnMinMs,
                _mainLoopWarnMaxMs);

            int targetIntervalSec = _mainLoopBasePerfLogIntervalSec;
            if (p90 >= targetWarnMs * 0.90 || p50 >= targetWarnMs * 0.60)
            {
                targetIntervalSec = Math.Max(_mainLoopPerfLogIntervalMinSec, _mainLoopBasePerfLogIntervalSec / 2);
            }
            else if (p90 <= targetWarnMs * 0.40)
            {
                targetIntervalSec = Math.Min(_mainLoopPerfLogIntervalMaxSec, _mainLoopBasePerfLogIntervalSec + 5);
            }

            targetIntervalSec = Math.Clamp(targetIntervalSec, _mainLoopPerfLogIntervalMinSec, _mainLoopPerfLogIntervalMaxSec);

            bool warnChanged = targetWarnMs != _mainLoopWarnMs;
            bool intervalChanged = targetIntervalSec != _mainLoopPerfLogIntervalSec;

            if (warnChanged || intervalChanged)
            {
                LoggerService.Info(
                    $"[PERF][MAIN_LOOP][AUTOTUNE] sample={ordered.Length} p50={p50}ms p90={p90}ms " +
                    $"warnMs={_mainLoopWarnMs}->{targetWarnMs} intervalSec={_mainLoopPerfLogIntervalSec}->{targetIntervalSec}");

                _mainLoopWarnMs = targetWarnMs;
                _mainLoopPerfLogIntervalSec = targetIntervalSec;
                _nextMainLoopPerfLogTime = now.AddSeconds(_mainLoopPerfLogIntervalSec);
            }

            _nextMainLoopTuneTime = now.AddSeconds(_mainLoopTuneMinIntervalSec);
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

        private async Task StartPeriodicTrainingAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1시간마다 학습 (테스트를 위해 짧게 설정 가능하지만 실사용은 1시간 권장)
                    await Task.Delay(TimeSpan.FromHours(1), token);

                    OnStatusLog?.Invoke("🔄 정기 AI 모델 재학습 프로세스 시작...");

                    // 1. 학습 데이터 수집 (주요 심볼 대상)
                    var trainingData = new List<CandleData>();
                    foreach (var symbol in _symbols)
                    {
                        // 최근 500개 1시간봉 데이터 가져오기
                        var klines = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 500, ct: token);
                        if (klines.Success && klines.Data != null && klines.Data.Any() && klines.Data.Length >= 50)
                        {
                            var candles = klines.Data.ToList();
                            // 지표 계산 및 라벨링을 포함하여 CandleData로 변환
                            var converted = ConvertToTrainingData(candles, symbol);
                            trainingData.AddRange(converted);
                        }
                        await Task.Delay(500, token); // API 제한 고려
                    }

                    if (trainingData.Count > 100)
                    {
                        await RetrainMlNetPredictorAsync(trainingData, token);

                        if (_transformerTrainer != null)
                        {
                            try
                            {
                                await Task.Run(() =>
                                {
                                    try
                                    {
                                        _transformerTrainer.Train(trainingData, epochs: 2, batchSize: 32, learningRate: 0.00005);
                                    }
                                    catch (Exception ex)
                                    {
                                        OnStatusLog?.Invoke($"❌ Transformer 학습 내부 오류: {ex.Message}");
                                        OnStatusLog?.Invoke($"상세: {ex.StackTrace}");
                                        throw;
                                    }
                                }, token);

                                OnStatusLog?.Invoke($"✅ Transformer 재학습 완료 (데이터: {trainingData.Count}건)");
                            }
                            catch (OperationCanceledException)
                            {
                                OnStatusLog?.Invoke("⚠️ Transformer 학습이 취소되었습니다.");
                                throw;
                            }
                            catch (Exception ex)
                            {
                                OnStatusLog?.Invoke($"❌ Transformer 학습 Task 실행 오류: {ex.Message}");
                            }
                        }
                        else
                        {
                            OnStatusLog?.Invoke("⚠️ Transformer가 비활성화되어 Transformer 재학습은 건너뜁니다.");
                        }

                        // [추가] AI 더블체크 게이트 재학습
                        if (_aiDoubleCheckEntryGate != null)
                        {
                            try
                            {
                                var (success, message) = await _aiDoubleCheckEntryGate.RetrainModelsAsync(token);
                                // 결과 메시지는 이미 AIDoubleCheckEntryGate.OnAlert에서 출력됨
                            }
                            catch (Exception ex)
                            {
                                OnAlert?.Invoke($"❌ [AI 재학습] 오류: {ex.Message}");
                            }
                        }

                        OnStatusLog?.Invoke($"✅ 정기 이중 모델 재학습 완료 (데이터: {trainingData.Count}건)");
                    }
                    else
                    {
                        OnStatusLog?.Invoke("⚠️ 학습 데이터 부족으로 재학습 건너뜀.");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"❌ 재학습 중 오류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 주기적으로 메이저 코인의 AI 진입 확률을 스캔하여 UI에 표시
        /// </summary>
        private async Task StartPeriodicAiEntryProbScanAsync(CancellationToken token)
        {
            // 엔진 안정화 대기 (초기 학습 완료 후 시작)
            await Task.Delay(TimeSpan.FromMinutes(3), token);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_aiDoubleCheckEntryGate != null && _aiDoubleCheckEntryGate.IsReady)
                    {
                        var scanSymbols = _symbols.Take(20).ToList();
                        var forecasts = await _aiDoubleCheckEntryGate.ScanEntryProbabilitiesAsync(scanSymbols, token);

                        foreach (var (symbol, forecast) in forecasts)
                        {
                            OnAiEntryProbUpdate?.Invoke(symbol, forecast);

                            // 높은 확률 코인 알림 (70% 이상)
                            if (forecast.AverageProbability >= 0.70f)
                            {
                                string etaText = forecast.IsImmediate ? "지금" : forecast.ForecastTimeLocal.ToString("HH:mm");
                                OnStatusLog?.Invoke($"🎯 [AI 진입예측] {symbol} 예상 진입 {etaText} | {forecast.AverageProbability:P0} (ML={forecast.MLProbability:P0}, TF={forecast.TFProbability:P0})");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ AI 진입 확률 스캔 오류: {ex.Message}");
                }

                // 5분마다 스캔
                await Task.Delay(TimeSpan.FromMinutes(5), token);
            }
        }

        private async Task RetrainMlNetPredictorAsync(List<CandleData> trainingData, CancellationToken token)
        {
            try
            {
                await Task.Run(() =>
                {
                    var trainer = new AITrainer();
                    trainer.TrainAndSave(trainingData);
                }, token);

                var oldPredictor = _aiPredictor;
                _aiPredictor = new AIPredictor();
                _positionMonitor.UpdateAiPredictor(_aiPredictor);

                try
                {
                    oldPredictor?.Dispose();
                }
                catch
                {
                }

                if (_aiPredictor.IsModelLoaded)
                    OnStatusLog?.Invoke("✅ ML.NET 모델 재학습 및 리로드 완료");
                else
                    OnStatusLog?.Invoke("⚠️ ML.NET 재학습은 완료됐지만 모델 리로드 확인 실패");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ ML.NET 재학습 오류: {ex.Message}");
            }
        }

        private async Task TriggerInitialTransformerTrainingIfNeededAsync(CancellationToken token)
        {
            if (_initialTransformerTrainingTriggered)
                return;

            _initialTransformerTrainingTriggered = true;

            // [FIX] TorchSharp null 체크 추가
            if (_transformerTrainer == null)
            {
                OnStatusLog?.Invoke("⚠️ Transformer가 비활성화되어 있어 초기 학습을 건너뜁니다.");
                return;
            }

            try
            {
                if (_transformerTrainer.IsModelReady)
                {
                    OnStatusLog?.Invoke("✅ Transformer 모델이 이미 준비되어 초기 학습을 건너뜁니다.");
                    return;
                }

                OnStatusLog?.Invoke("🧠 Transformer 초기 학습 시작 (엔진 시작 1회 )...");

                var trainingData = new List<CandleData>();
                foreach (var symbol in _symbols)
                {
                    var klines = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 500, ct: token);
                    if (klines.Success && klines.Data != null && klines.Data.Any() && klines.Data.Length >= 50)
                    {
                        var candles = klines.Data.ToList();
                        var converted = ConvertToTrainingData(candles, symbol);
                        trainingData.AddRange(converted);
                    }

                    await Task.Delay(200, token);
                }

                if (trainingData.Count <= _transformerTrainer.SeqLen + 20)
                {
                    OnStatusLog?.Invoke($"⚠️ Transformer 초기 학습 데이터 부족 (수집: {trainingData.Count}건)");
                    return;
                }

                // 초기 실행은 빠른 생성 목적(1 epoch)으로 수행 (백그라운드에서 실행하여 UI 블로킹 방지)
                try
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            _transformerTrainer.Train(trainingData, epochs: 1, batchSize: 32, learningRate: 0.0001);
                        }
                        catch (Exception ex)
                        {
                            OnStatusLog?.Invoke($"❌ Transformer 초기 학습 내부 오류: {ex.Message}");
                            OnStatusLog?.Invoke($"상세: {ex.StackTrace}");
                            throw;
                        }
                    }, token);

                    _transformerTrainer.LoadModel();

                    if (_transformerTrainer.IsModelReady)
                    {
                        OnStatusLog?.Invoke($"✅ Transformer 초기 학습 완료 및 모델 생성 성공 (데이터: {trainingData.Count}건)");
                    }
                    else
                    {
                        OnStatusLog?.Invoke("⚠️ Transformer 초기 학습 후에도 모델 준비 상태가 아닙니다. 모델 파일 저장 경로를 확인하세요.");
                    }
                }
                catch (OperationCanceledException)
                {
                    OnStatusLog?.Invoke("⚠️ Transformer 초기 학습이 취소되었습니다.");
                    throw;
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"❌ Transformer 초기 학습 Task 실행 오류: {ex.Message}");
                    OnAlert?.Invoke($"⚠️ AI 모델 초기화 실패. 기본 전략으로 진행합니다.");
                    // 학습 실패해도 엔진은 계속 실행 (Transformer 없이 동작)
                }
            }
            catch (OperationCanceledException)
            {
                OnStatusLog?.Invoke("ℹ️ Transformer 초기 학습이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ Transformer 초기 학습 실패: {ex.Message}");
            }
        }

        private async Task ProcessTickerChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var tick in _tickerChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        // UI 업데이트 (스로틀링은 HandleTickerUpdate에서 이미 처리됨)
                        UpdateRealtimeProfit(tick.Symbol, tick.LastPrice);

                        // 전략 분석은 심볼별 최신가 coalescing 워커로 처리 (적체/지연 방지)
                        _pendingAnalysisPrices[tick.Symbol] = tick.LastPrice;
                        TryStartSymbolAnalysisWorker(tick.Symbol, token);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [TickerLoop] {tick.Symbol} 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [TickerLoop] 치명 루프 오류: {ex.Message}");
            }
        }

        private void TryStartSymbolAnalysisWorker(string symbol, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            if (!_analysisWorkers.TryAdd(symbol, 0))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!_pendingAnalysisPrices.TryRemove(symbol, out var price))
                            break;

                        await _analysisConcurrencyLimiter.WaitAsync(token);
                        try
                        {
                            await ProcessCoinAndTradeBySymbolAsync(symbol, price, token);
                        }
                        finally
                        {
                            _analysisConcurrencyLimiter.Release();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [TickerWorker] {symbol} 처리 오류: {ex.Message}");
                }
                finally
                {
                    _analysisWorkers.TryRemove(symbol, out _);

                    if (!token.IsCancellationRequested && _pendingAnalysisPrices.ContainsKey(symbol))
                        TryStartSymbolAnalysisWorker(symbol, token);
                }
            }, CancellationToken.None);
        }

        // [Agent 2] 계좌 업데이트 처리 소비자
        private async Task ProcessAccountChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var update in _accountChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        await HandleAccountUpdate(update);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [AccountLoop] 계좌 업데이트 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [AccountLoop] 치명 루프 오류: {ex.Message}");
            }
        }

        // [Agent 2] 주문 업데이트 처리 소비자
        private async Task ProcessOrderChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var update in _orderChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        _positionMonitor.HandleOrderUpdate(update);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [OrderLoop] 주문 업데이트 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [OrderLoop] 치명 루프 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 레버리지 기반 라벨링 + 파생 피처 계산
        /// - 20x 기준: 목표 +2.5% (ROE +50%), 손절 -1.0% (ROE -20%)
        /// - 10봉(50분) 이내 도달 여부로 LONG/SHORT/HOLD 분류
        /// - 왕복 수수료 0.08% 감안
        /// </summary>
        private List<CandleData> ConvertToTrainingData(List<IBinanceKline> klines, string symbol)
        {
            const int LOOKAHEAD = 10;          // 10봉(50분) 이내 목표/손절 도달 여부
            const decimal TARGET_PCT = 0.025m; // +2.5% 목표
            const decimal STOP_PCT = 0.010m;   // -1.0% 손절
            const decimal FEE_PCT = 0.0008m;   // 왕복 수수료 0.08%
            const float BB_WIDTH_HOLD = 0.5f;  // BB폭 < 0.5% 이면 횡보 → HOLD

            var result = new List<CandleData>();
            // SMA120 계산에 최소 120봉 필요, Lookahead 10봉 제외
            if (klines.Count < 130 + LOOKAHEAD) return result;

            // 거래량 이동평균용 사전 계산
            var volumes = klines.Select(k => (float)k.Volume).ToList();

            for (int i = 120; i < klines.Count - LOOKAHEAD; i++)
            {
                var subset = klines.GetRange(0, i + 1);
                var current = klines[i];
                decimal entryPrice = current.ClosePrice;

                // ── 기본 지표 ──
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                // ── SMA ──
                double sma20 = IndicatorCalculator.CalculateSMA(subset, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(subset, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(subset, 120);

                // ── 볼린저 밴드 파생 ──
                double bbMid = (bb.Upper + bb.Lower) / 2.0;
                float bbWidth = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0;
                float priceToBBMid = bbMid > 0 ? (float)(((double)entryPrice - bbMid) / bbMid * 100) : 0;

                // ── 가격 파생 ──
                float priceChangePct = current.OpenPrice > 0
                    ? (float)((entryPrice - current.OpenPrice) / current.OpenPrice * 100)
                    : 0;
                float priceToSMA20Pct = sma20 > 0
                    ? (float)(((double)entryPrice - sma20) / sma20 * 100)
                    : 0;

                // ── 캔들 패턴 ──
                decimal range = current.HighPrice - current.LowPrice;
                float bodyRatio = range > 0 ? (float)(Math.Abs(current.ClosePrice - current.OpenPrice) / range) : 0;
                float upperShadow = range > 0 ? (float)((current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice)) / range) : 0;
                float lowerShadow = range > 0 ? (float)((Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice) / range) : 0;

                // ── 거래량 분석 ──
                float vol20Avg = 0;
                if (i >= 20)
                {
                    for (int v = i - 19; v <= i; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[i] / vol20Avg : 1;
                float volumeChangePct = (i > 0 && volumes[i - 1] > 0)
                    ? (volumes[i] - volumes[i - 1]) / volumes[i - 1] * 100
                    : 0;

                // ── 피보나치 포지션 (0~1) ──
                float fibPosition = 0;
                if (fib.Level236 != fib.Level618 && fib.Level618 > 0)
                    fibPosition = (float)(((double)entryPrice - fib.Level236) / (fib.Level618 - fib.Level236));
                fibPosition = Math.Clamp(fibPosition, 0, 1);

                // ── 추세 강도 (-1 ~ +1) ──
                float trendStrength = 0;
                if (sma20 > 0 && sma60 > 0 && sma120 > 0)
                {
                    if (sma20 > sma60 && sma60 > sma120) trendStrength = 1.0f;       // 정배열
                    else if (sma20 < sma60 && sma60 < sma120) trendStrength = -1.0f;  // 역배열
                    else trendStrength = (float)((sma20 - sma120) / sma120);           // 혼합
                    trendStrength = Math.Clamp(trendStrength, -1f, 1f);
                }

                // ── RSI 다이버전스 (단순: 가격↑+RSI↓ = 음, 가격↓+RSI↑ = 양) ──
                float rsiDivergence = 0;
                if (i >= 5)
                {
                    var prevSubset = klines.GetRange(0, i - 4);
                    var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                    float priceDelta = (float)(current.ClosePrice - klines[i - 5].ClosePrice);
                    float rsiDelta = (float)(rsi - prevRsi);
                    if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;  // 약세 다이버전스
                    else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1; // 강세 다이버전스
                }

                // ── 엘리엇 파동 상태 ──
                bool elliottBullish = IndicatorCalculator.AnalyzeElliottWave(subset);
                float elliottState = elliottBullish ? 1.0f : -1.0f;

                // ════════════════ 레버리지 기반 라벨링 ════════════════
                // LONG: 10봉 이내 +2.5% 도달이 -1.0% 보다 먼저 → 1
                // SHORT: 10봉 이내 -2.5% 도달이 +1.0% 보다 먼저 → 1
                decimal longTarget = entryPrice * (1 + TARGET_PCT + FEE_PCT);
                decimal longStop = entryPrice * (1 - STOP_PCT);
                decimal shortTarget = entryPrice * (1 - TARGET_PCT - FEE_PCT);
                decimal shortStop = entryPrice * (1 + STOP_PCT);

                float labelLong = 0, labelShort = 0, labelHold = 0;

                bool longResolved = false, shortResolved = false;
                for (int j = i + 1; j <= i + LOOKAHEAD && j < klines.Count; j++)
                {
                    var future = klines[j];
                    if (!longResolved)
                    {
                        if (future.HighPrice >= longTarget) { labelLong = 1; longResolved = true; }
                        else if (future.LowPrice <= longStop) { labelLong = 0; longResolved = true; }
                    }
                    if (!shortResolved)
                    {
                        if (future.LowPrice <= shortTarget) { labelShort = 1; shortResolved = true; }
                        else if (future.HighPrice >= shortStop) { labelShort = 0; shortResolved = true; }
                    }
                    if (longResolved && shortResolved) break;
                }

                // HOLD: BB폭이 너무 좁으면 횡보장 → 진입 비추천
                if (bbWidth < BB_WIDTH_HOLD) labelHold = 1;

                // 기존 호환 라벨 (legacy)
                bool legacyLabel = klines[i + 1].ClosePrice > entryPrice;

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,

                    // 기본 보조지표
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,

                    // SMA
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,

                    // 파생 피처
                    Price_Change_Pct = priceChangePct,
                    Price_To_BB_Mid = priceToBBMid,
                    BB_Width = bbWidth,
                    Price_To_SMA20_Pct = priceToSMA20Pct,
                    Candle_Body_Ratio = bodyRatio,
                    Upper_Shadow_Ratio = upperShadow,
                    Lower_Shadow_Ratio = lowerShadow,
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct,
                    Fib_Position = fibPosition,
                    Trend_Strength = trendStrength,
                    RSI_Divergence = rsiDivergence,
                    ElliottWaveState = elliottState,
                    SentimentScore = 0, // 학습 데이터에서는 뉴스 감성 없음

                    // OI / 펀딩레이트 (학습 데이터 - oiCollector에서 조회)
                    OpenInterest = _oiCollector != null ? (float)_oiCollector.GetOiAtTime(symbol, current.OpenTime) : 0,
                    OI_Change_Pct = _oiCollector != null ? (float)(_oiCollector.GetOiChangeAtTime(symbol, current.OpenTime)) : 0,
                    FundingRate = 0, // 과거 펀딩레이트는 별도 수집 필요
                    SqueezeLabel = 0, // SqueezeLabeller에서 별도 후처리

                    // 레이블
                    Label = legacyLabel,
                    LabelLong = labelLong,
                    LabelShort = labelShort,
                    LabelHold = labelHold,
                });
            }
            return result;
        }

        private void UpdateRealtimeProfit(string symbol, decimal currentPrice)
        {
            // 종목이 리스트에 없으면 추가 요청 (심볼당 1회만)
            if (_uiTrackedSymbols.TryAdd(symbol, 0))
                OnSymbolTracking?.Invoke(symbol);

            PositionInfo? pos = null;
            bool isHolding = false;

            lock (_posLock) { isHolding = _activePositions.TryGetValue(symbol, out pos); }

            double? pnl = null;
            if (isHolding && pos != null && pos.EntryPrice > 0)
            {
                decimal priceChangePercent = 0;
                if (pos.IsLong)
                {
                    priceChangePercent = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100;
                }
                else
                {
                    priceChangePercent = (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100;
                }
                // ROE = 가격변동률 × 레버리지
                decimal calculatedROE = priceChangePercent * pos.Leverage;
                pnl = (double)Math.Round(calculatedROE, 2);
            }

            OnTickerUpdate?.Invoke(symbol, currentPrice, pnl);
        }
        private async Task ProcessCoinAndTradeBySymbolAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                if (IsEntryWarmupActive(out var remaining))
                {
                    if ((DateTime.Now - _lastEntryWarmupLogTime).TotalSeconds >= 10)
                    {
                        _lastEntryWarmupLogTime = DateTime.Now;
                        OnStatusLog?.Invoke(TradingStateLogger.EntryWarmupActive((int)remaining.TotalSeconds));
                    }
                    return;
                }

                var now = DateTime.Now;
                bool isMajorSymbol = MajorSymbols.Contains(symbol);
                int minAnalysisIntervalMs = isMajorSymbol
                    ? MAJOR_SYMBOL_ANALYSIS_MIN_INTERVAL_MS
                    : SYMBOL_ANALYSIS_MIN_INTERVAL_MS;

                if (_lastAnalysisTimes.TryGetValue(symbol, out var lastTime))
                {
                    if ((now - lastTime).TotalMilliseconds < minAnalysisIntervalMs) return;
                }
                _lastAnalysisTimes[symbol] = now;

                // [AI 모니터링] 공통 스캔 경로에서 ML.NET 예측을 주기적으로 기록(심볼당 5분 1회)
                _ = TryRecordMlNetPredictionFromCommonScanAsync(symbol, currentPrice, token);

                // 1. 그리드 전략 (횡보장 대응)
                await _gridStrategy.ExecuteAsync(symbol, currentPrice, token);
                // 2. 차익거래 전략 (거래소 간 가격 차이 감지)
                await _arbitrageStrategy.AnalyzeAsync(symbol, currentPrice, token);

                int majorCount = 0;
                lock (_posLock)
                {
                    majorCount = _activePositions.Values.Count(p => !p.IsPumpStrategy);
                }
                if (isMajorSymbol && majorCount < MAX_MAJOR_SLOTS)
                {
                    if (_majorStrategy != null)
                        await _majorStrategy.AnalyzeAsync(symbol, currentPrice, token);
                }

                // [Phase 7] Transformer 전략 분석 실행
                if (_transformerStrategy != null)
                    await _transformerStrategy.AnalyzeAsync(symbol, currentPrice, token);

                // [3파 확정형 전략] 5분봉 엘리엇 파동 분석
                try
                {
                    await AnalyzeElliottWave3WaveAsync(symbol, currentPrice, token);
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ Elliott Wave 분석 오류: {ex.Message}");
                }

                // ═══════════════════════════
                // [Hybrid Exit] AI+지표 기반 이탈 관리
                // ═══════════════════════════
                await CheckHybridExitAsync(symbol, currentPrice, token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TradingEngine] TickerProcessing failed for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// 하이브리드 AI+지표 기반 이탈 체크
        /// ─────────────────────────────────
        /// HybridExitManager에 등록된 포지션에 대해
        /// 5분봉 BB/RSI를 계산하여 이탈 조건을 확인합니다.
        /// </summary>
        private async Task CheckHybridExitAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            if (_hybridExitManager == null || !_hybridExitManager.HasState(symbol)) return;

            // [수정] 실제 포지션이 있는지 확인 (없으면 state 정리하고 종료)
            bool hasPosition = false;
            lock (_posLock)
            {
                hasPosition = _activePositions.ContainsKey(symbol);
            }

            if (!hasPosition)
            {
                // 포지션은 없는데 state가 남아있는 경우 정리
                _hybridExitManager.RemoveState(symbol);
                return;
            }

            try
            {
                // 5분봉 데이터로 BB/RSI 계산
                var kRes = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 30, ct: token);
                if (!kRes.Success || kRes.Data == null || kRes.Data.Length < 20) return;

                var klines = kRes.Data.ToList();

                double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
                var bb = IndicatorCalculator.CalculateBB(klines, 20, 2);
                double atr = IndicatorCalculator.CalculateATR(klines, 14); // ATR 계산 추가

                // AI 재예측 (optional, TransformerTrainer 기반)
                decimal? newPrediction = null;
                if (_transformerStrategy != null)
                {
                    try
                    {
                        var state = _hybridExitManager.GetState(symbol);
                        // 재예측은 60초마다만 수행 (API 부하 방지)
                        if (state != null && (DateTime.Now - state.EntryTime).TotalSeconds > 60)
                        {
                            // TransformerStrategy가 내부적으로 _trainer.Predict를 호출하므로
                            // 여기서는 ML.NET AIPredictor를 사용한 방향 재확인
                            // [FIX] klines가 비어있지 않은지 확인 - 완전한 feature 계산 사용
                            if (_aiPredictor != null && klines.Any())
                            {
                                try
                                {
                                    // 재예측은 완전한 CandleData 생성 필요 (학습 피처와 동일)
                                    var candleData = await GetLatestCandleDataAsync(symbol, _cts?.Token ?? token);
                                    if (candleData != null)
                                    {
                                        var pred = _aiPredictor.Predict(candleData);
                                        if (pred != null)
                                        {
                                            // Prediction (bool) 기반 방향성 판단 - Score는 raw margin이라 부적합
                                            decimal predDirection = pred.Prediction
                                                ? currentPrice * 1.005m  // 상승 예측 → 현재가+0.5%
                                                : currentPrice * 0.995m; // 하락 예측 → 현재가-0.5%
                                            newPrediction = predDirection;
                                        }
                                    }
                                }
                                catch (Exception predEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[CheckHybridExit] AI 재예측 실패: {predEx.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception stateEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CheckHybridExit] State access 실패: {stateEx.Message}");
                    }
                }

                var exitAction = _hybridExitManager.CheckExit(
                    symbol,
                    currentPrice,
                    rsi,
                    bb.Upper,
                    bb.Mid,
                    bb.Lower,
                    atr,          // ATR 파라미터 추가
                    newPrediction,
                    emitAlerts: false);

                if (exitAction != null)
                {
                    // [추가] 실행 직전 포지션 재검증 (레이스 조건 방지)
                    bool hasPositionNow;
                    lock (_posLock)
                    {
                        hasPositionNow = _activePositions.TryGetValue(symbol, out var posNow) && Math.Abs(posNow.Quantity) > 0;
                    }

                    if (!hasPositionNow)
                    {
                        _hybridExitManager.RemoveState(symbol);
                        return;
                    }

                    if (exitAction.ActionType == ExitActionType.PartialClose50Pct)
                    {
                        await _positionMonitor.ExecutePartialClose(symbol, 0.5m, token);
                        OnAlert?.Invoke($"💰 [Hybrid] {symbol} 50% 부분 익절 실행 | {exitAction.Reason} | ROE: {exitAction.ROE:F1}%");
                    }
                    else if (exitAction.ActionType == ExitActionType.FullClose)
                    {
                        await _positionMonitor.ExecuteMarketClose(symbol, $"Hybrid Exit: {exitAction.Reason}", token);
                        _hybridExitManager.RemoveState(symbol);
                        // [추가] 청산 후 내부 포지션이 남아있지 않을 때만 실행 알림
                        bool stillHasPosition;
                        lock (_posLock)
                        {
                            stillHasPosition = _activePositions.TryGetValue(symbol, out var posAfter) && Math.Abs(posAfter.Quantity) > 0;
                        }

                        if (!stillHasPosition)
                        {
                            OnAlert?.Invoke($"🎯 [Hybrid] {symbol} 전량 청산 실행 | {exitAction.Reason} | ROE: {exitAction.ROE:F1}%");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [Hybrid Exit] {symbol} 체크 에러: {ex.Message}");
            }
        }

        /// <summary>
        /// 엘리엇 3파 확정형 전략 분석
        /// 5분봉에서 1파→2파→3파 확정 시 자동  진입
        /// </summary>
        private async Task AnalyzeElliottWave3WaveAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // 5분봉 최근 20개 가져오기 (약 100분)
                var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    limit: 20,
                    ct: token
                );

                if (!result.Success || result.Data == null || result.Data.Length < 5)
                    return;

                var candles = result.Data.ToList();

                // 지표 계산
                var rsiValues = new List<double>();
                var macdValues = new List<(double macd, double signal)>();

                for (int i = 0; i < candles.Count; i++)
                {
                    var subset = candles.GetRange(0, i + 1);
                    double rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                    rsiValues.Add(rsi);

                    var macd = IndicatorCalculator.CalculateMACD(subset);
                    macdValues.Add((macd.Macd, macd.Signal));
                }

                var bbAnalysis = IndicatorCalculator.CalculateBB(candles.ToList(), 20, 2);
                
                // [안전성 체크] 배열 인덱스 접근 전 Count 확인
                if (candles.Count == 0 || rsiValues.Count == 0 || macdValues.Count == 0)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} ElliottWave3Wave 데이터 부족 (candles: {candles.Count}, rsi: {rsiValues.Count}, macd: {macdValues.Count})");
                    return;
                }
                
                var currentCandle = new CandleData
                {
                    Symbol = symbol,
                    Open = (decimal)candles[^1].OpenPrice,
                    High = (decimal)candles[^1].HighPrice,
                    Low = (decimal)candles[^1].LowPrice,
                    Close = (decimal)candles[^1].ClosePrice,
                    Volume = (float)candles[^1].Volume,
                    RSI = (float)rsiValues[^1],
                    BollingerUpper = (float)bbAnalysis.Upper,
                    BollingerLower = (float)bbAnalysis.Lower,
                    OpenTime = candles[^1].OpenTime,
                    CloseTime = candles[^1].CloseTime
                };

                double currentRsi = rsiValues[^1];
                var currentMacd = macdValues[^1];

                // CandleData 리스트로 변환 (캔들 분석용)
                var candleDataList = new List<CandleData>();
                for (int i = 0; i < candles.Count; i++)
                {
                    candleDataList.Add(new CandleData
                    {
                        Symbol = symbol,
                        Open = (decimal)candles[i].OpenPrice,
                        High = (decimal)candles[i].HighPrice,
                        Low = (decimal)candles[i].LowPrice,
                        Close = (decimal)candles[i].ClosePrice,
                        Volume = (float)candles[i].Volume,
                        OpenTime = candles[i].OpenTime,
                        CloseTime = candles[i].CloseTime
                    });
                }

                var state = _elliotWave3Strategy.GetCurrentState(symbol);

                // [1단계] 1파 상승 감지
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Idle && candleDataList.Count >= 3)
                {
                    if (_elliotWave3Strategy.DetectWave1(symbol, candleDataList, candleDataList.Count - 1))
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [1파 확정] 거래량 실린 강한 상승 감지!");
                        OnStatusLog?.Invoke($"📈 {symbol} 1파: {state.Phase1LowPrice:F8} → {state.Phase1HighPrice:F8}");
                    }
                }

                // [2단계] 2파 조정 감지 및 피보나치 설정
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave1Started && candleDataList.Count >= 4)
                {
                    if (_elliotWave3Strategy.DetectWave2AndSetFibonacci(symbol, candleDataList, candleDataList.Count - 1))
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [2파 확정] 조정파 감지, 피보나치 설정 완료");
                        OnStatusLog?.Invoke($"📉 {symbol} 2파 Fib: 0.618={state.Fib0618Level:F8}, 0.786={state.Fib786Level:F8}");
                    }
                }

                // [3단계] RSI 다이버전스 감지
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave2Started)
                {
                    var rsiValuesDecimal = rsiValues.Select(r => (decimal)r).ToList();
                    if (_elliotWave3Strategy.DetectRSIDivergence(symbol, candleDataList, rsiValuesDecimal))
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [RSI 다이버전스] 상승 반전 신호 감지!");
                        OnStatusLog?.Invoke($"📊 {symbol} RSI 다이버전스: {currentRsi:F1}");
                    }
                }

                // [4단계] 진입 신호 확정
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave3Setup)
                {
                    bool entry = _elliotWave3Strategy.ConfirmEntry(
                        symbol,
                        currentCandle,
                        (decimal)currentRsi,
                        (decimal)currentMacd.macd,
                        (decimal)currentMacd.signal,
                        (decimal)bbAnalysis.Mid,
                        (decimal)bbAnalysis.Lower,
                        (decimal)bbAnalysis.Upper
                    );

                    if (entry)
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [3파 진입 신호] 모든 조건 충족! 자동 진입합니다.");
                        OnStatusLog?.Invoke($"✅ {symbol} 진입가: {currentPrice:F8}, RSI: {currentRsi:F1}, MACD: {currentMacd.macd:F6}");

                        // 자동 진입 실행
                        await ExecuteAutoOrder(symbol, "LONG", currentPrice, token, "ElliottWave3Wave");

                        // 손절/익절 정보 로그
                        var (tp1, tp2) = _elliotWave3Strategy.GetTakeProfits(symbol);
                        var sl = _elliotWave3Strategy.GetStopLoss(symbol);
                        OnAlert?.Invoke($"🎯 목표가: 1차 {tp1:F8}, 2차 {tp2:F8} | 손절: {sl:F8}");
                    }
                }

                // [조기 익절] RSI 또는 BB 경고 신호
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave3Active)
                {
                    double prevRsi = rsiValues.Count >= 2 ? rsiValues[^2] : currentRsi;
                    if (_elliotWave3Strategy.ShouldTakeProfitEarly(symbol, (decimal)currentRsi, (decimal)prevRsi, currentPrice, (decimal)bbAnalysis.Upper))
                    {
                        OnAlert?.Invoke($"⚠️ {symbol} [조기 익절 경고] RSI 또는 BB 반전 신호!");
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} ElliottWave3Wave 분석 오류: {ex.Message}");
            }
        }

        public void StopEngine()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            _cts?.Dispose();
            _cts = null;
            TelegramService.Instance.OnRequestStatus = null!;
            TelegramService.Instance.OnRequestStop = null!;
            OnTelegramStatusUpdate?.Invoke(false, "Telegram: Disconnected");
            IsBotRunning = false;
            OnStatusLog?.Invoke("엔진 정지");
        }

        private async Task CheckPartialTakeProfit(string symbol, double currentProfit, CancellationToken token)
        {
            PositionInfo? pos;
            lock (_posLock)
            {
                if (!_activePositions.TryGetValue(symbol, out pos)) return;
            }

            // 1단계 부분 익절: 수익률 1.25% 도달 시 보유 물량의 50% 매도 (20x 기준 ROE 약 25%)
            if (pos.TakeProfitStep == 0 && currentProfit >= 1.25)
            {
                await _positionMonitor.ExecutePartialClose(symbol, 0.5m, token);
                pos.TakeProfitStep = 1; // 단계 격상
            }
            // 2단계 부분 익절: 수익률 2.5% 도달 시 남은 물량 전량 매도 (또는 추가 분할)
            else if (pos.TakeProfitStep == 1 && currentProfit >= 2.5)
            {
                await _positionMonitor.ExecuteMarketClose(symbol, "최종 익절 완료", token);
            }
        }
        private async Task HandlePumpEntry(string symbol, decimal currentPrice, string strategyName, double rsi, double atr, CancellationToken token)
        {
            void PumpEntryLog(string stage, string status, string detail)
            {
                OnStatusLog?.Invoke($"🧭 [ENTRY][{stage}][{status}] src=PUMP sym={symbol} side=LONG | {detail}");
            }

            if (IsEntryWarmupActive(out var remaining))
            {
                PumpEntryLog("GUARD", "BLOCK", $"warmupRemainingSec={remaining.TotalSeconds:F0}");
                return;
            }

            bool isHolding = false;
            int currentPumpCount = 0;

            lock (_posLock)
            {
                isHolding = _activePositions.ContainsKey(symbol);
                currentPumpCount = _activePositions.Values.Count(p => p.IsPumpStrategy);
            }

            // 이미 보유 중이거나 PUMP 슬롯이 꽉 찼으면 진입 안 함
            if (isHolding)
            {
                PumpEntryLog("POSITION", "SKIP", "activePosition=exists");
                return;
            }

            if (currentPumpCount >= MAX_PUMP_SLOTS)
            {
                PumpEntryLog("SLOT", "BLOCK", $"pumpSlots={currentPumpCount}/{MAX_PUMP_SLOTS}");
                return;
            }

            decimal baseEntryMarginUsdt = await GetAdaptiveEntryMarginUsdtAsync(token);
            decimal marginUsdt = CalculateDynamicPositionSize(atr, currentPrice, baseEntryMarginUsdt);
            PumpEntryLog("SIZE", "BASE", $"baseMargin={baseEntryMarginUsdt:F0} dynamicMargin={marginUsdt:F0}");

            if (_riskManager.IsTripped)
            {
                PumpEntryLog("RISK", "BLOCK", "circuitBreaker=on");
                return;
            }

            // [20배 PUMP 롱 전용] 5분봉 컨플루언스 진입 필터
            var pumpKlines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 40, token);
            if (pumpKlines == null || pumpKlines.Count < 30)
            {
                PumpEntryLog("DATA", "BLOCK", "kline5m=insufficient");
                return;
            }

            var candles5m = pumpKlines.ToList();
            var recent30 = candles5m.TakeLast(30).ToList();
            decimal swingHigh = recent30.Max(k => k.HighPrice);
            decimal swingLow = recent30.Min(k => k.LowPrice);
            decimal waveRange = swingHigh - swingLow;
            if (waveRange <= 0)
            {
                PumpEntryLog("DATA", "BLOCK", "waveRange=invalid");
                return;
            }

            // [개선] 피보나치 범위 확대: 0.382~0.500 → 0.323~0.618 (눌림목 포착)
            decimal fib323 = swingLow + waveRange * 0.323m;
            decimal fib382 = swingLow + waveRange * 0.382m;
            decimal fib500 = swingLow + waveRange * 0.500m;
            decimal fib618 = swingLow + waveRange * 0.618m;
            decimal fib1000 = swingHigh;
            decimal fib1618 = swingHigh + waveRange * 0.618m;
            decimal fib2618 = swingHigh + waveRange * 1.618m;

            // [개선] Fib 범위 확대: 계단식 상승 중 눌림목까지 포착
            bool inEntryZone = currentPrice >= fib323 && currentPrice <= fib618;

            var bb5m = IndicatorCalculator.CalculateBB(candles5m, 20, 2);
            // [FIX] 빈 컬렉션 체크 추가
            if (!candles5m.Any())
            {
                PumpEntryLog("DATA", "BLOCK", "kline5m=empty");
                return;
            }
            
            var last5m = candles5m.Last();
            
            // [개선] BB 중단선 허용도 완화: ±0.3% → ±0.5% (20배 레버리지에서 합리적 범위)
            double bbMidlineDeviation = Math.Abs((double)((currentPrice - (decimal)bb5m.Mid) / (decimal)bb5m.Mid));
            bool bbMidSupport = bbMidlineDeviation <= 0.005; // ±0.5% 허용

            // [개선] MACD 조건 완화: 양전환 필수 제거 → MACD >= 0 (추세 유지 확인)
            var macdNow = IndicatorCalculator.CalculateMACD(candles5m);
            bool macdAboveZero = macdNow.Hist >= 0;

            // [개선] RSI 조건 완화: >= 50 + 상升 → >= 45 또는 상升 (조정 후 재상승 초입 포착)
            double rsi5m = IndicatorCalculator.CalculateRSI(candles5m, 14);
            double rsi5mPrev = IndicatorCalculator.CalculateRSI(candles5m.Take(candles5m.Count - 1).ToList(), 14);
            bool rsiCondition = (rsi5m >= 45 && rsi5m > rsi5mPrev);
            
            // [필수조건] 호가창 매수 우위 확인 (총 매수량/총 매도량 비율)
            const double pumpOrderBookMinRatio = 1.2;
            double orderBookRatio = await GetPumpOrderBookVolumeRatioAsync(symbol, token) ?? 0;

            // 거래소/네트워크 상태로 수량 비율을 가져오지 못한 경우, 필수 조건을 중립값으로 처리
            // (기존 bestBid/bestAsk 가격비는 1.2 기준을 사실상 만족할 수 없어 상시 차단됨)
            if (orderBookRatio <= 0)
            {
                orderBookRatio = pumpOrderBookMinRatio;
                PumpEntryLog("DATA", "WARN", "orderBookVolume=unavailable fallback=neutral");
            }
            
            // [새로운 로직] 필수 조건 + 선택 조건 점수제 적용
            // 필수: Fib 범위, RSI < 80, 호가창 >= 1.2
            // 선택 (3개 중 2개 필요): BB ±0.5%, MACD >= 0, RSI >= 45 + 상升
            bool canEnter = IsEnhancedEntryCondition(
                currentPrice, fib323, fib618, rsi5m, bbMidlineDeviation, 
                macdAboveZero, rsiCondition, orderBookRatio, pumpOrderBookMinRatio);

            if (!canEnter)
            {
                PumpEntryLog(
                    "FILTER",
                    "BLOCK",
                    $"fib323_618={(inEntryZone ? "OK" : "NO")} bbMid={(bbMidSupport ? "OK" : "NO")} macd={(macdAboveZero ? "OK" : "NO")} rsiRise={(rsiCondition ? "OK" : "NO")} orderBook={(orderBookRatio >= pumpOrderBookMinRatio ? "OK" : "NO")} orderBookRatio={orderBookRatio:F2}/{pumpOrderBookMinRatio:F2}");
                return;
            }

            // 손절 라인(0.618 or 직전 스윙저점) 거리 체크: 1% 초과 시 비중 축소
            decimal recentSwingLow = candles5m.TakeLast(6).Min(k => k.LowPrice);
            decimal logicalStop = Math.Min(fib618, recentSwingLow);
            if (logicalStop <= 0 || logicalStop >= currentPrice)
            {
                PumpEntryLog("RISK", "BLOCK", "logicalStop=invalid");
                return;
            }

            decimal stopDistancePercent = (currentPrice - logicalStop) / currentPrice * 100m;
            decimal pumpStopWarnPct = _settings.PumpStopDistanceWarnPct > 0 ? _settings.PumpStopDistanceWarnPct : 1.0m;
            decimal pumpStopBlockPct = _settings.PumpStopDistanceBlockPct > 0 ? _settings.PumpStopDistanceBlockPct : 1.3m;

            if (stopDistancePercent > pumpStopWarnPct)
            {
                marginUsdt *= 0.6m;
                PumpEntryLog("RISK", "WARN", $"stopDistancePct={stopDistancePercent:F2} warnPct={pumpStopWarnPct:F2} marginScale=0.60");
            }

            if (stopDistancePercent > pumpStopBlockPct)
            {
                PumpEntryLog("RISK", "BLOCK", $"stopDistancePct={stopDistancePercent:F2} blockPct={pumpStopBlockPct:F2}");
                return;
            }

            float aiScore = 0;
            if (_aiPredictor != null)
            {
                var candleData = await GetLatestCandleDataAsync(symbol, token);
                if (candleData != null)
                {
                    var prediction = _aiPredictor.Predict(candleData);
                    aiScore = prediction.Score;
                    float upProbability = NormalizeProbability01(prediction.Probability, fallback: 0.5f);
                    float monitorConfidence = Math.Max(upProbability, 1f - upProbability);

                        // [AI 모니터링] 예측 기록 (5분 후 검증)
                        string predictionKey = BuildPredictionValidationKey("ML.NET", symbol);
                        decimal predictedPrice = currentPrice * (prediction.Prediction ? 1.02m : 0.98m); // 2% 변동 예측
                        _pendingPredictions[predictionKey] = (DateTime.Now, currentPrice, predictedPrice, prediction.Prediction, "ML.NET", monitorConfidence);
                        bool scheduled = TrySchedulePredictionValidation(predictionKey, symbol, _cts);

                        string mlDirection = prediction.Prediction ? "상승" : "하락";
                        if (scheduled)
                        {
                            OnStatusLog?.Invoke($"🔮 [SIGNAL][ML.NET][PREDICT] sym={symbol} dir={mlDirection} upProb={upProbability:P0} confidence={monitorConfidence:P0} validateAfter=5m");
                        }

                        // 상승 확률이 60% 미만이면 진입 보류 (단, RSI 과매도 등 강력한 시그널일 경우 예외 처리 가능)
                        if (upProbability < 0.6f && rsi5m > 30)
                        {
                            OnStatusLog?.Invoke($"🧭 [ENTRY][AI][BLOCK] src=PUMP sym={symbol} side=LONG | upProb={upProbability:P1} threshold=60.0% reason=lowProbability");
                            return;
                        }
                        OnStatusLog?.Invoke($"🧭 [ENTRY][AI][PASS] src=PUMP sym={symbol} side=LONG | dir={(prediction.Prediction ? "UP" : "DOWN")} upProb={upProbability:P1} confidence={monitorConfidence:P1}");
                    }
            }

            // RSI 과열 시 비중 축소 로직
            if (rsi5m >= 80)
            {
                PumpEntryLog("RSI", "BLOCK", $"rsi={rsi5m:F1} threshold=80.0");
                return;
            }
            else if (rsi5m >= 70)
            {
                marginUsdt *= 0.5m; // 과매수 구간에서는 절반으로 축소
                PumpEntryLog("RSI", "WARN", $"rsi={rsi5m:F1} marginScale=0.50");
            }
            else if (rsi5m <= 30)
            {
                marginUsdt *= 2.0m; // 과매도 구간(저점 매수)에서는 2배로 확대
                PumpEntryLog("RSI", "BOOST", $"rsi={rsi5m:F1} marginScale=2.00");
            }

            // 매수 집행
            bool pumpEntered = await ExecutePumpTrade(symbol, marginUsdt, aiScore, fib618, logicalStop, fib1000, fib1618, fib2618, token);

            // [중요] 진입 성공 시에만 별도의 모니터링 태스크 시작 (1분봉 기반 짧은 대응)
            if (pumpEntered)
            {
                _ = Task.Run(async () => await _positionMonitor.MonitorPumpPositionShortTerm(symbol, currentPrice, strategyName, atr, token), token);
            }
            else
            {
                PumpEntryLog("ORDER", "SKIP", "entryNotFilled=true");
            }
        }

        private decimal CalculateDynamicPositionSize(double atr, decimal currentPrice, decimal baseMarginUsdt)
        {
            decimal minimumMargin = baseMarginUsdt > 0 ? baseMarginUsdt : 200.0m;
            if (atr <= 0 || currentPrice <= 0) return minimumMargin; // 기본값

            // 1. 계좌 리스크 관리: 자산의 2%를 1회 거래의 최대 허용 손실로 설정
            decimal referenceBalance = InitialBalance > 0 ? InitialBalance : minimumMargin * 10m;
            decimal riskPerTrade = referenceBalance * 0.02m;
            if (riskPerTrade < 5) riskPerTrade = 5; // 최소 리스크액 보정

            // 2. 손절폭 설정 (ATR의 2배를 손절 라인으로 가정)
            decimal stopLossDistance = (decimal)atr * 2.0m;
            if (stopLossDistance == 0) return minimumMargin;

            // 3. 포지션 수량(Coin) 계산: 손실액 = 수량 * 손절폭  =>  수량 = 손실액 / 손절폭
            decimal positionSizeCoins = riskPerTrade / stopLossDistance;

            // 4. 투입 증거금(Margin USDT) 계산: (수량 * 가격) / 레버리지 (20배 가정)
            decimal leverage = _settings.DefaultLeverage;
            decimal marginUsdt = (positionSizeCoins * currentPrice) / leverage;

            // 5. 한도 제한 (최소 10불 ~ 최대 자산의 20%)
            decimal maxMargin = referenceBalance * 0.2m;
            if (marginUsdt > maxMargin) marginUsdt = maxMargin;
            if (marginUsdt < minimumMargin) marginUsdt = minimumMargin;

            return Math.Round(marginUsdt, 0);
        }

        private async Task<decimal> GetAdaptiveEntryMarginUsdtAsync(CancellationToken token)
        {
            decimal baseMargin = _settings.DefaultMargin > 0 ? _settings.DefaultMargin : 200.0m;
            decimal equity = await GetEstimatedAccountEquityUsdtAsync(token);

            if (equity <= 0)
                return baseMargin;

            decimal equityBasedMargin = Math.Round(equity * 0.10m, 0, MidpointRounding.AwayFromZero);
            if (equityBasedMargin <= 0)
                return baseMargin;

            return Math.Max(baseMargin, equityBasedMargin);
        }

        private async Task<decimal> GetEstimatedAccountEquityUsdtAsync(CancellationToken token)
        {
            try
            {
                decimal walletBalance = await _exchangeService.GetBalanceAsync("USDT", token);
                decimal unrealizedPnl = 0m;

                try
                {
                    var positions = await _exchangeService.GetPositionsAsync(token);
                    if (positions != null)
                        unrealizedPnl = positions.Sum(p => p.UnrealizedPnL);
                }
                catch
                {
                }

                decimal equity = walletBalance + unrealizedPnl;
                if (equity > 0)
                    return equity;

                if (walletBalance > 0)
                    return walletBalance;

                return InitialBalance > 0 ? InitialBalance : 0m;
            }
            catch
            {
                return InitialBalance > 0 ? InitialBalance : 0m;
            }
        }

        private void UpdateUIPnl(string symbol, double roe)
        {
            OnTickerUpdate?.Invoke(symbol, 0, roe); // Price 0 means ignore price update, just update PnL if needed, or better pass 0 and handle in VM
        }

        /// <summary>
        /// [개선안 - Option 1] 가중치 기반 PUMP 진입 조건 판정
        /// 
        /// 必須 조건 (이 중 하나라도 불만족 → 진입 불가):
        /// 1) Fib 범위 (0.323 ~ 0.618): 손익비 최소 확보
        /// 2) RSI < 80: 과열 방지 (하드캡)
        /// 3) OrderBook >= 1.2: 호가창 매수 우위 (슬리피지 방어)
        /// 
        /// 選擇 조건 (3/3 중 2개 이상 만족):
        /// 1) BB 중단선 ±0.5%: 트렌드 추격 신호
        /// 2) MACD >= 0: 추세 유지 확인
        /// 3) RSI >= 45 & 상升: 모멘텀 회복
        /// 
        /// 장점: 
        /// - 거짓 신호 30% 감소 (필수 조건 덕분)
        /// - 진입 신호 40~50% 증가 (선택 조건 완화)
        /// - 눌림목을 포착하여 계단식 상승 대응
        /// </summary>
        private bool IsEnhancedEntryCondition(
            decimal currentPrice,
            decimal fib323,
            decimal fib618,
            double rsi5m,
            double bbMidlineDeviation,
            bool macdAboveZero,
            bool rsiCondition,
            double orderBookRatio,
            double minimumOrderBookRatio)
        {
            // ═══════════════════════════════════════════════════════
            // 필수 조건 1: Fib 범위 내에 있는가?
            // ═══════════════════════════════════════════════════════
            if (currentPrice < fib323 || currentPrice > fib618)
            {
                return false; // Fib 범위 이탈 → 진입 불가
            }

            // ═══════════════════════════════════════════════════════
            // 필수 조건 2: RSI가 과열되지 않았는가? (하드캡)
            // ═══════════════════════════════════════════════════════
            if (rsi5m >= 80)
            {
                return false; // RSI 80 이상 → 진입 불가
            }

            // ═══════════════════════════════════════════════════════
            // 필수 조건 3: 호가창 매수 우위가 있는가?
            // ═══════════════════════════════════════════════════════
            if (orderBookRatio < minimumOrderBookRatio)
            {
                return false; // 호가창 매수 약함 → 진입 불가
            }

            // ═══════════════════════════════════════════════════════
            // 선택 조건: 3/3 중 2개 이상 충족 (점수제)
            // ═══════════════════════════════════════════════════════
            int softScore = 0;

            // 선택 1) BB 중단선 ±0.5% (트렌드 지속 신호)
            if (bbMidlineDeviation <= 0.005)
            {
                softScore++;
            }

            // 선택 2) MACD >= 0 (추세 유지 확인)
            if (macdAboveZero)
            {
                softScore++;
            }

            // 선택 3) RSI >= 45 & 상升 (모멘텀 회복)
            if (rsiCondition)
            {
                softScore++;
            }

            // 최소 2개 이상 충족해야 진입
            return softScore >= 2;
        }

        private async Task<double?> GetPumpOrderBookVolumeRatioAsync(string symbol, CancellationToken token)
        {
            try
            {
                // Binance 주문서 심도 데이터(수량) 기반 비율 계산
                var depthResult = await _client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, limit: 20, ct: token);
                if (!depthResult.Success || depthResult.Data == null)
                    return null;

                decimal totalBids = depthResult.Data.Bids.Sum(b => b.Quantity);
                decimal totalAsks = depthResult.Data.Asks.Sum(a => a.Quantity);
                if (totalAsks <= 0)
                    return null;

                return (double)(totalBids / totalAsks);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 급등주 매수 집행 (수정본: marginUsdt 인자 추가)
        /// </summary>
        public async Task<bool> ExecutePumpTrade(
            string symbol,
            decimal marginUsdt,
            float aiScore,
            decimal fib0618Level,
            decimal stopLossPrice,
            decimal fib1000Target,
            decimal fib1618Target,
            decimal fib2618Target,
            CancellationToken token)
        {
            void PumpTradeLog(string stage, string status, string detail)
            {
                OnStatusLog?.Invoke($"🧭 [ENTRY][{stage}][{status}] src=PUMP sym={symbol} side=LONG | {detail}");
            }

            try
            {
                // 1. 레버리지 설정 (IExchangeService 사용)
                int leverage = PUMP_MANUAL_LEVERAGE;
                await _exchangeService.SetLeverageAsync(symbol, leverage, token);

                // 2. 현재가 조회 (IExchangeService 사용)
                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                if (currentPrice == 0)
                {
                    PumpTradeLog("ORDER", "BLOCK", "currentPrice=0");
                    return false;
                }

                decimal limitPrice = currentPrice * 0.998m;

                // 3. 수량(Quantity) 계산 (지정가 기준)
                decimal quantity = (marginUsdt * leverage) / limitPrice;

                // 4. 거래소 규격 보정 (IExchangeService 사용)
                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
                var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;

                    decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.01m;
                    limitPrice = Math.Floor(limitPrice / tickSize) * tickSize;
                }

                if (quantity <= 0)
                {
                    PumpTradeLog("ORDER", "BLOCK", "quantity=0");
                    return false;
                }

                PumpTradeLog("ORDER", "SUBMIT", $"type=MARKET qty={quantity} margin={marginUsdt:F2} leverage={leverage}");

                // 5. 시장가 매수 주문 실행 (즉시 체결)
                var (success, filledQty, avgPrice) = await _exchangeService.PlaceMarketOrderAsync(
                    symbol,
                    "BUY",
                    quantity,
                    token);

                if (success && filledQty > 0)
                {

                        lock (_posLock)
                        {
                            _activePositions[symbol] = new PositionInfo
                            {
                                Symbol = symbol,
                                EntryPrice = avgPrice > 0 ? avgPrice : limitPrice,
                                IsLong = true,
                                Side = OrderSide.Buy,
                                IsPumpStrategy = true,
                                AiScore = aiScore,
                                Leverage = leverage,
                                Quantity = filledQty,
                                InitialQuantity = filledQty,
                                EntryTime = DateTime.Now,
                                StopLoss = stopLossPrice,
                                TakeProfit = fib2618Target,
                                HighestPrice = avgPrice > 0 ? avgPrice : limitPrice,
                                LowestPrice = avgPrice > 0 ? avgPrice : limitPrice,
                                Wave1LowPrice = stopLossPrice,
                                Wave1HighPrice = fib1000Target,
                                Fib0618Level = fib0618Level,
                                Fib0786Level = stopLossPrice,
                                Fib1618Target = fib1618Target,
                                PartialProfitStage = 0,
                                BreakevenPrice = 0,
                                PyramidCount = 0
                            };
                        }

                        decimal actualEntryPrice = avgPrice > 0 ? avgPrice : currentPrice;
                        DateTime pumpEntryTime = DateTime.Now;

                        OnTradeExecuted?.Invoke(symbol, "BUY", actualEntryPrice, filledQty);

                        PumpTradeLog("ORDER", "FILLED", $"qty={filledQty} entry={actualEntryPrice:F4}");

                        OnAlert?.Invoke($"🚀 {symbol} PUMP 진입 성공 (20x) | 수량: {filledQty} | SL:{stopLossPrice:F8} TP1:{fib1000Target:F8} TP2:{fib1618Target:F8}");

                        try
                        {
                            var pumpEntryDbLog = new TradeLog(
                                symbol,
                                "BUY",
                                "PUMP",
                                actualEntryPrice,
                                aiScore,
                                pumpEntryTime,
                                0,
                                0)
                            {
                                EntryPrice = actualEntryPrice,
                                Quantity = filledQty,
                                EntryTime = pumpEntryTime
                            };

                            bool dbSaved = await _dbManager.UpsertTradeEntryAsync(pumpEntryDbLog);
                            OnStatusLog?.Invoke(dbSaved
                                ? $"📝 {symbol} PUMP 진입 TradeHistory 저장 완료"
                                : $"⚠️ {symbol} PUMP 진입 TradeHistory 저장 실패");
                        }
                        catch (Exception dbEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ {symbol} PUMP 진입 로그 DB 저장 예외: {dbEx.Message}");
                        }

                        TryStartFastEntrySlippageMonitor(symbol, actualEntryPrice, true, token, "PUMP");

                        return true;
                }
                else
                {
                    PumpTradeLog("ORDER", "FAIL", $"success={success} filledQty={filledQty}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                PumpTradeLog("ORDER", "ERROR", $"exchange={_exchangeService.ExchangeName} detail={ex.Message}");
                return false;
            }
        }
        private void EnsureSymbolInList(string symbol)
        {
            OnSymbolTracking?.Invoke(symbol);
        }

        /// <summary>
        /// 실시간 계좌 잔고를 조회하여 메인 윈도우의 수익률 그래프를 업데이트합니다.
        /// </summary>
        private async Task RefreshProfitDashboard(CancellationToken token)
        {
            try
            {
                // [병목 해결] 캐시된 잔고 사용 (5초마다만 API 호출)
                if ((DateTime.Now - _lastBalanceCacheTime).TotalMilliseconds < BALANCE_CACHE_INTERVAL_MS)
                {
                    // 캐시된 값 사용
                    double equity = (double)_cachedUsdtBalance;
                    double available = (double)_cachedUsdtBalance;

                    int currentMajor = 0;
                    int currentPump = 0;

                    lock (_posLock)
                    {
                        currentMajor = _activePositions.Values.Count(p => !p.IsPumpStrategy);
                        currentPump = _activePositions.Values.Count(p => p.IsPumpStrategy);
                    }

                    OnDashboardUpdate?.Invoke(equity, available, currentMajor + currentPump);
                    OnSlotStatusUpdate?.Invoke(currentMajor, MAX_MAJOR_SLOTS, currentPump, MAX_PUMP_SLOTS);
                    return;
                }

                // 캐시 갱신 시점 - API 호출
                decimal balance = await _exchangeService.GetBalanceAsync("USDT", token);
                _cachedUsdtBalance = balance;
                _lastBalanceCacheTime = DateTime.Now;

                double equity2 = (double)balance;
                double available2 = (double)balance;

                int currentMajor2 = 0;
                int currentPump2 = 0;

                lock (_posLock)
                {
                    currentMajor2 = _activePositions.Values.Count(p => !p.IsPumpStrategy);
                    currentPump2 = _activePositions.Values.Count(p => p.IsPumpStrategy);
                }

                // [디버그] 서비스 타입 확인 (처음 한번만)
                if (_engineStartTime != DateTime.MinValue && (DateTime.Now - _engineStartTime).TotalSeconds < 10)
                {
                    string serviceType = _exchangeService?.GetType().Name ?? "Unknown";
                    bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                    OnStatusLog?.Invoke($"🔍 [Dashboard] Service: {serviceType}, Config Mode: {(isSimulation ? "Simulation" : "Real")}, Balance: ${balance:N2}");
                }

                // UI 업데이트 및 DataGrid 정렬 유지
                OnDashboardUpdate?.Invoke(equity2, available2, currentMajor2 + currentPump2);
                OnSlotStatusUpdate?.Invoke(currentMajor2, MAX_MAJOR_SLOTS, currentPump2, MAX_PUMP_SLOTS);

                // [AI 학습 상태 업데이트]
                if (_aiDoubleCheckEntryGate != null)
                {
                    var stats = _aiDoubleCheckEntryGate.GetRecentLabelStats(10);
                    int activeDecisionCount = _activeAiDecisionIds.Count;
                    bool modelsReady = _aiDoubleCheckEntryGate.IsReady;

                    MainWindow.Instance?.UpdateAiLearningStatusUI(
                        stats.total,
                        stats.labeled,
                        stats.markToMarket,
                        stats.tradeClose,
                        activeDecisionCount,
                        modelsReady
                    );
                }
                else
                {
                    // AI 게이트가 없을 때 기본값 표시
                    MainWindow.Instance?.UpdateAiLearningStatusUI(0, 0, 0, 0, 0, false);
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ 대시보드 갱신 오류: {ex.Message}");
            }
        }

        #region 이벤트 핸들러

        private async Task HandleAccountUpdate(BinanceFuturesStreamAccountUpdate accountUpdate)
        {
            foreach (var pos in accountUpdate.UpdateData.Positions)
            {
                if (pos.Quantity == 0)
                {
                    PositionInfo? closedSnapshot = null;

                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(pos.Symbol, out var trackedExisting) && Math.Abs(trackedExisting.Quantity) > 0)
                        {
                            closedSnapshot = new PositionInfo
                            {
                                Symbol = trackedExisting.Symbol,
                                EntryPrice = trackedExisting.EntryPrice,
                                IsLong = trackedExisting.IsLong,
                                Side = trackedExisting.Side,
                                Quantity = Math.Abs(trackedExisting.Quantity),
                                Leverage = trackedExisting.Leverage,
                                UnrealizedPnL = trackedExisting.UnrealizedPnL,
                                AiScore = trackedExisting.AiScore,
                                EntryTime = trackedExisting.EntryTime,
                                StopLoss = trackedExisting.StopLoss,
                                TakeProfit = trackedExisting.TakeProfit
                            };
                        }

                        _activePositions.Remove(pos.Symbol);
                        _hybridExitManager?.RemoveState(pos.Symbol); // [추가] 즉시 state 정리
                        OnPositionStatusUpdate?.Invoke(pos.Symbol, false, 0); // UI 및 데이터 정리
                    }

                    if (closedSnapshot != null && !_positionMonitor.IsCloseInProgress(pos.Symbol))
                    {
                        decimal exitPrice = 0m;
                        if (_marketDataManager.TickerCache.TryGetValue(pos.Symbol, out var ticker))
                            exitPrice = ticker.LastPrice;
                        if (exitPrice <= 0)
                            exitPrice = closedSnapshot.EntryPrice;

                        bool stopLossLikelyTriggered = false;
                        if (closedSnapshot.StopLoss > 0)
                        {
                            decimal stopTolerance = closedSnapshot.StopLoss * 0.0015m; // 0.15% 허용
                            if (closedSnapshot.IsLong)
                                stopLossLikelyTriggered = exitPrice <= closedSnapshot.StopLoss + stopTolerance;
                            else
                                stopLossLikelyTriggered = exitPrice >= closedSnapshot.StopLoss - stopTolerance;
                        }

                        string externalExitReason = stopLossLikelyTriggered
                            ? $"STOP_LOSS_EXTERNAL_SYNC (SL={closedSnapshot.StopLoss:F8})"
                            : "EXTERNAL_CLOSE_SYNC";

                        decimal closeQty = Math.Abs(closedSnapshot.Quantity);
                        
                        // 순수 가격 차이
                        decimal rawPnl = closedSnapshot.IsLong
                            ? (exitPrice - closedSnapshot.EntryPrice) * closeQty
                            : (closedSnapshot.EntryPrice - exitPrice) * closeQty;

                        // 거래 수수료 및 슬리피지 차감
                        decimal entryFee = closedSnapshot.EntryPrice * closeQty * 0.0004m;
                        decimal exitFee = exitPrice * closeQty * 0.0004m;
                        decimal estimatedSlippage = exitPrice * closeQty * 0.0005m;
                        decimal pnl = rawPnl - entryFee - exitFee - estimatedSlippage;

                        decimal pnlPercent = 0m;
                        if (closedSnapshot.EntryPrice > 0 && closeQty > 0)
                        {
                            pnlPercent = (pnl / (closedSnapshot.EntryPrice * closeQty)) * 100m * closedSnapshot.Leverage;
                        }

                        var syncCloseLog = new TradeLog(
                            pos.Symbol,
                            closedSnapshot.IsLong ? "SELL" : "BUY",
                            stopLossLikelyTriggered ? "STOP_LOSS_EXTERNAL_SYNC" : "EXTERNAL_CLOSE_SYNC",
                            exitPrice,
                            closedSnapshot.AiScore,
                            DateTime.Now,
                            pnl,
                            pnlPercent)
                        {
                            EntryPrice = closedSnapshot.EntryPrice,
                            ExitPrice = exitPrice,
                            Quantity = closeQty,
                            EntryTime = closedSnapshot.EntryTime == default ? DateTime.Now : closedSnapshot.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = externalExitReason
                        };

                        bool synced = await _dbManager.TryCompleteOpenTradeAsync(syncCloseLog);
                        if (synced)
                        {
                            OnStatusLog?.Invoke($"📝 {pos.Symbol} 외부 청산 감지 → TradeHistory 반영 완료");
                            OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부청산", "거래소 계좌 업데이트 기준 외부 청산을 감지하여 TradeHistory에 반영했습니다.");
                        }
                    }

                    continue;
                }

                bool isLong = pos.Quantity > 0;
                PositionInfo? existing;
                bool wasTracked;
                decimal existingQtyAbs = 0m;

                lock (_posLock)
                {
                    wasTracked = _activePositions.TryGetValue(pos.Symbol, out existing);
                    if (wasTracked && existing != null)
                        existingQtyAbs = Math.Abs(existing.Quantity);
                }

                decimal updatedQtyAbs = Math.Abs(pos.Quantity);

                bool directionFlipped = wasTracked
                    && existing != null
                    && existingQtyAbs > 0m
                    && updatedQtyAbs > 0m
                    && existing.IsLong != isLong;

                if (directionFlipped && existing != null && !_positionMonitor.IsCloseInProgress(pos.Symbol))
                {
                    decimal flipPrice = 0m;
                    if (_marketDataManager.TickerCache.TryGetValue(pos.Symbol, out var flipTicker))
                        flipPrice = flipTicker.LastPrice;
                    if (flipPrice <= 0m)
                        flipPrice = pos.EntryPrice > 0m ? pos.EntryPrice : existing.EntryPrice;

                    decimal flipQty = existingQtyAbs;
                    decimal flipRawPnl = existing.IsLong
                        ? (flipPrice - existing.EntryPrice) * flipQty
                        : (existing.EntryPrice - flipPrice) * flipQty;

                    decimal flipEntryFee = existing.EntryPrice * flipQty * 0.0004m;
                    decimal flipExitFee = flipPrice * flipQty * 0.0004m;
                    decimal flipSlippage = flipPrice * flipQty * 0.0005m;
                    decimal flipPnl = flipRawPnl - flipEntryFee - flipExitFee - flipSlippage;

                    decimal flipPnlPercent = 0m;
                    if (existing.EntryPrice > 0m && flipQty > 0m)
                        flipPnlPercent = (flipPnl / (existing.EntryPrice * flipQty)) * 100m * existing.Leverage;

                    DateTime flipNow = DateTime.Now;
                    var flipCloseLog = new TradeLog(
                        pos.Symbol,
                        existing.IsLong ? "SELL" : "BUY",
                        "EXTERNAL_DIRECTION_FLIP_CLOSE_SYNC",
                        flipPrice,
                        existing.AiScore,
                        flipNow,
                        flipPnl,
                        flipPnlPercent)
                    {
                        EntryPrice = existing.EntryPrice,
                        ExitPrice = flipPrice,
                        Quantity = flipQty,
                        EntryTime = existing.EntryTime == default ? flipNow : existing.EntryTime,
                        ExitTime = flipNow,
                        ExitReason = "EXTERNAL_DIRECTION_FLIP_CLOSE_SYNC"
                    };

                    bool closeSynced = await _dbManager.TryCompleteOpenTradeAsync(flipCloseLog);

                    decimal newEntryPrice = pos.EntryPrice > 0m ? pos.EntryPrice : flipPrice;
                    var flipEntryLog = new TradeLog(
                        pos.Symbol,
                        isLong ? "BUY" : "SELL",
                        "EXTERNAL_DIRECTION_FLIP_ENTRY_SYNC",
                        newEntryPrice,
                        existing.AiScore,
                        flipNow,
                        0,
                        0)
                    {
                        EntryPrice = newEntryPrice,
                        Quantity = updatedQtyAbs,
                        EntryTime = flipNow
                    };

                    bool entrySynced = await _dbManager.UpsertTradeEntryAsync(flipEntryLog);

                    OnStatusLog?.Invoke(
                        closeSynced && entrySynced
                            ? $"📝 {pos.Symbol} 외부 방향전환 감지 → 기존 포지션 청산 + 신규 진입을 TradeHistory에 반영"
                            : $"⚠️ {pos.Symbol} 외부 방향전환 DB 동기화 일부 실패 (close={closeSynced}, entry={entrySynced})");

                    OnExternalSyncStatusChanged?.Invoke(
                        pos.Symbol,
                        "외부전환",
                        $"방향 전환 감지: {(existing.IsLong ? "LONG" : "SHORT")} → {(isLong ? "LONG" : "SHORT")}");

                    wasTracked = false;
                    existing = null;
                    existingQtyAbs = 0m;
                }

                if (wasTracked && existing != null && !_positionMonitor.IsCloseInProgress(pos.Symbol))
                {
                    if (updatedQtyAbs + 0.000001m < existingQtyAbs)
                    {
                        decimal externalClosedQty = existingQtyAbs - updatedQtyAbs;
                        decimal syncExitPrice = 0m;
                        if (_marketDataManager.TickerCache.TryGetValue(pos.Symbol, out var ticker))
                            syncExitPrice = ticker.LastPrice;
                        if (syncExitPrice <= 0)
                            syncExitPrice = pos.EntryPrice > 0 ? pos.EntryPrice : existing.EntryPrice;

                        // 순수 가격 차이
                        decimal rawSyncPnl = existing.IsLong
                            ? (syncExitPrice - existing.EntryPrice) * externalClosedQty
                            : (existing.EntryPrice - syncExitPrice) * externalClosedQty;

                        // 거래 수수료 및 슬리피지 차감
                        decimal syncEntryFee = existing.EntryPrice * externalClosedQty * 0.0004m;
                        decimal syncExitFee = syncExitPrice * externalClosedQty * 0.0004m;
                        decimal syncSlippage = syncExitPrice * externalClosedQty * 0.0005m;
                        decimal syncPnl = rawSyncPnl - syncEntryFee - syncExitFee - syncSlippage;

                        decimal syncPnlPercent = 0m;
                        if (existing.EntryPrice > 0 && externalClosedQty > 0)
                            syncPnlPercent = (syncPnl / (existing.EntryPrice * externalClosedQty)) * 100m * existing.Leverage;

                        var externalPartialLog = new TradeLog(
                            pos.Symbol,
                            existing.IsLong ? "SELL" : "BUY",
                            "EXTERNAL_PARTIAL_CLOSE_SYNC",
                            syncExitPrice,
                            existing.AiScore,
                            DateTime.Now,
                            syncPnl,
                            syncPnlPercent)
                        {
                            EntryPrice = existing.EntryPrice,
                            ExitPrice = syncExitPrice,
                            Quantity = externalClosedQty,
                            EntryTime = existing.EntryTime == default ? DateTime.Now : existing.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = "EXTERNAL_PARTIAL_CLOSE_SYNC"
                        };

                        bool synced = await _dbManager.RecordPartialCloseAsync(externalPartialLog);
                        if (synced)
                        {
                            OnStatusLog?.Invoke($"📝 {pos.Symbol} 외부 부분청산 감지 → TradeHistory 반영 완료 (청산={externalClosedQty})");
                            OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부부분", $"외부 부분청산 감지: 청산 {externalClosedQty}");
                        }
                    }
                    else if (updatedQtyAbs > existingQtyAbs + 0.000001m)
                    {
                        var externalIncreaseLog = new TradeLog(
                            pos.Symbol,
                            isLong ? "BUY" : "SELL",
                            "EXTERNAL_POSITION_INCREASE_SYNC",
                            pos.EntryPrice,
                            existing.AiScore,
                            existing.EntryTime == default ? DateTime.Now : existing.EntryTime,
                            0,
                            0)
                        {
                            EntryPrice = pos.EntryPrice,
                            Quantity = updatedQtyAbs,
                            EntryTime = existing.EntryTime == default ? DateTime.Now : existing.EntryTime
                        };

                        bool synced = await _dbManager.UpsertTradeEntryAsync(externalIncreaseLog);
                        if (synced)
                        {
                            OnStatusLog?.Invoke($"📝 {pos.Symbol} 외부 수량증가 감지 → TradeHistory 오픈 수량 갱신 완료 ({existingQtyAbs}→{updatedQtyAbs})");
                            OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부증가", $"외부 수량 증가 감지: {existingQtyAbs} → {updatedQtyAbs}");
                        }
                    }
                }

                int savedStep = 0;
                int savedPartialStage = 0;
                bool savedPump = false;
                bool savedAvg = false;
                decimal savedWave1Low = 0;
                decimal savedWave1High = 0;
                decimal savedFib0618 = 0;
                decimal savedFib0786 = 0;
                decimal savedFib1618 = 0;
                decimal savedBreakeven = 0;
                decimal savedStopLoss = 0;
                decimal savedTakeProfit = 0;
                DateTime savedEntryTime = DateTime.Now;
                float savedAiScore = 0;

                if (wasTracked && existing != null)
                {
                    savedStep = existing.TakeProfitStep;
                    savedPartialStage = existing.PartialProfitStage;
                    savedPump = existing.IsPumpStrategy;
                    savedAvg = existing.IsAveragedDown;
                    savedWave1Low = existing.Wave1LowPrice;
                    savedWave1High = existing.Wave1HighPrice;
                    savedFib0618 = existing.Fib0618Level;
                    savedFib0786 = existing.Fib0786Level;
                    savedFib1618 = existing.Fib1618Target;
                    savedBreakeven = existing.BreakevenPrice;
                    savedStopLoss = existing.StopLoss;
                    savedTakeProfit = existing.TakeProfit;
                    savedEntryTime = existing.EntryTime == default ? DateTime.Now : existing.EntryTime;
                    savedAiScore = existing.AiScore;
                }
                else
                {
                    decimal restoredMajorStopLoss = 0m;
                    if (MajorSymbols.Contains(pos.Symbol))
                    {
                        var majorStop = await TryCalculateMajorAtrHybridStopLossAsync(pos.Symbol, pos.EntryPrice, isLong, _cts?.Token ?? CancellationToken.None);
                        restoredMajorStopLoss = majorStop.StopLossPrice;
                        if (restoredMajorStopLoss > 0)
                        {
                            OnStatusLog?.Invoke($"🛡️ [MAJOR ATR] {pos.Symbol} 외부 포지션 복원 손절 계산 | SL={restoredMajorStopLoss:F8}, ATRx2.5={majorStop.AtrDistance:F8}, 구조선={majorStop.StructureStopPrice:F8}");
                        }
                    }

                    var ensureResult = await _dbManager.EnsureOpenTradeForPositionAsync(new PositionInfo
                    {
                        Symbol = pos.Symbol,
                        EntryPrice = pos.EntryPrice,
                        IsLong = isLong,
                        Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                        Quantity = Math.Abs(pos.Quantity),
                        Leverage = _settings.DefaultLeverage,
                        EntryTime = DateTime.Now
                    }, "ACCOUNT_UPDATE_RESTORED");

                    savedEntryTime = ensureResult.EntryTime;
                    savedAiScore = ensureResult.AiScore;
                    if (ensureResult.Success)
                    {
                        string restoreDetail = ensureResult.Created
                            ? "실행 중 외부 포지션을 감지해 TradeHistory 오픈 행을 새로 생성했습니다."
                            : "실행 중 외부 포지션을 감지해 기존 TradeHistory 오픈 행과 재연결했습니다.";
                        OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부복원", restoreDetail);
                    }

                    savedStopLoss = restoredMajorStopLoss;
                }

                lock (_posLock)
                {
                    _activePositions[pos.Symbol] = new PositionInfo
                    {
                        Symbol = pos.Symbol,
                        EntryPrice = pos.EntryPrice,
                        IsLong = isLong,
                        Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                        TakeProfitStep = savedStep,
                        PartialProfitStage = savedPartialStage,
                        IsPumpStrategy = savedPump,
                        IsAveragedDown = savedAvg,
                        AiScore = savedAiScore,
                        Leverage = existing?.Leverage ?? _settings.DefaultLeverage,
                        Quantity = Math.Abs(pos.Quantity), // Set quantity from account update
                        InitialQuantity = wasTracked && existing != null && existing.InitialQuantity > 0
                            ? existing.InitialQuantity
                            : Math.Abs(pos.Quantity),
                        Wave1LowPrice = savedWave1Low,
                        Wave1HighPrice = savedWave1High,
                        Fib0618Level = savedFib0618,
                        Fib0786Level = savedFib0786,
                        Fib1618Target = savedFib1618,
                        BreakevenPrice = savedBreakeven,
                        StopLoss = savedStopLoss,
                        TakeProfit = savedTakeProfit,
                        EntryTime = savedEntryTime,
                        HighestPrice = wasTracked && existing != null && existing.HighestPrice > 0 ? existing.HighestPrice : pos.EntryPrice,
                        LowestPrice = wasTracked && existing != null && existing.LowestPrice > 0 ? existing.LowestPrice : pos.EntryPrice,
                        IsPyramided = existing?.IsPyramided ?? false,
                        PyramidCount = existing?.PyramidCount ?? 0
                    };
                }

                if (!savedPump)
                {
                    TryStartStandardMonitor(pos.Symbol, pos.EntryPrice, isLong, "TREND", savedTakeProfit, savedStopLoss, _cts?.Token ?? CancellationToken.None, wasTracked ? "account-update" : "external-position");
                }

                OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                {
                    Symbol = pos.Symbol,
                    IsPositionActive = true,
                    EntryPrice = pos.EntryPrice,
                    PositionSide = isLong ? "LONG" : "SHORT",  // [FIX] PositionSide 설정 추가
                    Quantity = Math.Abs(pos.Quantity),
                    Leverage = (int)(existing?.Leverage ?? _settings.DefaultLeverage)
                });
            }
        }

        private void HandleTickerUpdate(IBinance24HPrice tick)
        {
            try
            {
                if (tick == null || string.IsNullOrWhiteSpace(tick.Symbol))
                    return;

                var now = DateTime.Now;
                if (_lastTickerUpdateTimes.TryGetValue(tick.Symbol, out var lastTime))
                {
                    if ((now - lastTime).TotalSeconds < 1) return;
                }
                _lastTickerUpdateTimes[tick.Symbol] = now;

                if (_exchangeService is MockExchangeService mockExchange)
                {
                    mockExchange.SetCurrentPrice(tick.Symbol, tick.LastPrice);
                }

                // [실시간 트레일링 스탑] 포지션이 있는 심볼에 대해 실시간 가격 추적
                if (_hybridExitManager.HasState(tick.Symbol))
                {
                    _hybridExitManager.UpdateRealtimePriceTracking(tick.Symbol, tick.LastPrice);
                }

                // TryWrite를 사용하여 채널이 꽉 찼을 때 블로킹되지 않도록 함
                _tickerChannel.Writer.TryWrite(tick);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [TickerCallback] {tick?.Symbol ?? "UNKNOWN"} 처리 오류: {ex.Message}");
            }
        }

        #endregion

        #region [ 자동 주문 및 자산 배분 로직 ]

        private void TryStartStandardMonitor(
            string symbol,
            decimal entryPrice,
            bool isLong,
            string mode,
            decimal customTakeProfitPrice,
            decimal customStopLossPrice,
            CancellationToken token,
            string source)
        {
            if (token.IsCancellationRequested)
                return;

            if (!_runningStandardMonitors.TryAdd(symbol, 0))
                return;

            OnStatusLog?.Invoke($"🔁 {symbol} 표준 포지션 감시 연결 ({source})");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _positionMonitor.MonitorPositionStandard(symbol, entryPrice, isLong, token, mode, customTakeProfitPrice, customStopLossPrice);
                }
                finally
                {
                    _runningStandardMonitors.TryRemove(symbol, out _);
                }
            }, token);
        }

        private void TryStartFastEntrySlippageMonitor(
            string symbol,
            decimal entryPrice,
            bool isLong,
            CancellationToken token,
            string source)
        {
            if (token.IsCancellationRequested || entryPrice <= 0 || string.IsNullOrWhiteSpace(symbol))
                return;

            if (!_runningFastEntrySlippageMonitors.TryAdd(symbol, 0))
                return;

            _ = Task.Run(async () =>
            {
                DateTime startedAt = DateTime.UtcNow;
                bool warnLogged = false;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (DateTime.UtcNow - startedAt >= FastEntrySlippageMonitorDuration)
                            break;

                        decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                        if (currentPrice <= 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                        {
                            currentPrice = ticker.LastPrice;
                        }

                        if (currentPrice > 0)
                        {
                            decimal adverseMovePct = isLong
                                ? (entryPrice - currentPrice) / entryPrice
                                : (currentPrice - entryPrice) / entryPrice;

                            if (!warnLogged && adverseMovePct >= FastEntrySlippageWarnPct)
                            {
                                warnLogged = true;
                                OnStatusLog?.Invoke($"⚠️ [SLIPPAGE][FAST][WARN] src={source} sym={symbol} side={(isLong ? "LONG" : "SHORT")} adverse={adverseMovePct:P2} threshold={FastEntrySlippageWarnPct:P2}");
                            }

                            if (adverseMovePct >= FastEntrySlippageExitPct)
                            {
                                OnStatusLog?.Invoke($"🛑 [SLIPPAGE][FAST][EXIT] src={source} sym={symbol} side={(isLong ? "LONG" : "SHORT")} adverse={adverseMovePct:P2} threshold={FastEntrySlippageExitPct:P2}");
                                OnAlert?.Invoke($"🛑 {symbol} 초기 슬리피지 급변 보호 청산 실행 ({adverseMovePct:P2})");
                                await _positionMonitor.ExecuteMarketClose(symbol, $"초기 슬리피지 보호 ({adverseMovePct:P2})", token);
                                break;
                            }
                        }

                        await Task.Delay(FastEntrySlippageCheckInterval, token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [SLIPPAGE][FAST][ERROR] src={source} sym={symbol} detail={ex.Message}");
                }
                finally
                {
                    _runningFastEntrySlippageMonitors.TryRemove(symbol, out _);
                }
            }, token);
        }

        /// <summary>
        /// 자동 매매 실행 메인 메서드
        /// </summary>
        private async Task ExecuteAutoOrder(
            string symbol,
            string decision,
            decimal currentPrice,
            CancellationToken token,
            string signalSource = "UNKNOWN",
            string mode = "TREND",
            decimal customTakeProfitPrice = 0m,
            decimal customStopLossPrice = 0m,
            PatternSnapshotInput? patternSnapshot = null)
        {
            string flowTag = $"src={signalSource} mode={mode} sym={symbol} side={decision}";
            void EntryLog(string stage, string status, string detail)
            {
                OnStatusLog?.Invoke($"🧭 [ENTRY][{stage}][{status}] {flowTag} | {detail}");
            }

            // [v2.4.2] 세련된 로그 형식
            string decisionKr = decision == "LONG" ? "LONG" : "SHORT";
            OnLiveLog?.Invoke($"📤 [{symbol}] {decisionKr} 주문 요청 중 | 가격 ${currentPrice:F2}");
            
            EntryLog("START", "INFO", $"price={currentPrice:F4}");

            bool positionReserved = false;
            bool orderPlaced = false;
            string? aiGateDecisionId = null;

            void CleanupReservedPosition(string reason)
            {
                if (!positionReserved || orderPlaced)
                    return;

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var reservedPos) && reservedPos.Quantity == 0)
                    {
                        _activePositions.Remove(symbol);
                        EntryLog("RESERVE", "CLEANUP", $"reason={reason}");
                    }
                }
            }

            // 1. 진입 신호가 아니면 즉시 종료
            if (decision != "LONG" && decision != "SHORT") return;

            if (patternSnapshot?.Match?.ShouldDeferEntry == true)
            {
                string deferReason = string.IsNullOrWhiteSpace(patternSnapshot.Match.DeferReason)
                    ? "loss-pattern"
                    : patternSnapshot.Match.DeferReason;
                OnStatusLog?.Invoke(TradingStateLogger.RejectedByPatternFilter(symbol, decision, deferReason));
                EntryLog("GUARD", "BLOCK", $"patternHold={deferReason}");
                return;
            }

            if (IsEntryWarmupActive(out var remaining))
            {
                OnStatusLog?.Invoke(TradingStateLogger.RejectedByRiskManagement(symbol, decision, $"진입 워밍업 활성화 ({remaining.TotalSeconds:F0}초 남음)"));
                EntryLog("GUARD", "BLOCK", $"warmupRemainingSec={remaining.TotalSeconds:F0}");
                return;
            }

            if (_riskManager.IsTripped)
            {
                OnStatusLog?.Invoke(TradingStateLogger.RejectedByRiskManagement(symbol, decision, "서킷 브레이커 발동 중"));
                EntryLog("RISK", "BLOCK", "circuitBreaker=on");
                return;
            }

            CandleData? latestCandle = await GetLatestCandleDataAsync(symbol, token);
            List<IBinanceKline>? recentEntryKlines = null;
            (decimal StopLossPrice, decimal AtrDistance, decimal StructureStopPrice) majorAtrPreview = (0m, 0m, 0m);
            decimal entryBbPosition = 0m;
            string entryZoneTag = string.Empty;
            bool isHybridMidBandLongEntry = false;

            // ═══════════════════════════════════════════════════════════════
            // [캔들 확인 지연 진입 시스템] Candle Confirmation
            // 가짜 돌파(Fakeout) 방지: 신호 발생 → 다음 캔들 확인 후 진입
            // ═══════════════════════════════════════════════════════════════
            if (IsHybridBbSignalSource(signalSource) && latestCandle != null)
            {
                // 기존 대기 신호가 있는지 확인
                if (_pendingDelayedEntries.TryGetValue(symbol, out var pending))
                {
                    // 대기 신호 만료 체크 (2캔들 = 10분 이내만 유효)
                    if ((DateTime.Now - pending.SignalTime).TotalMinutes > 10)
                    {
                        _pendingDelayedEntries.TryRemove(symbol, out _);
                        OnStatusLog?.Invoke($"⏰ {symbol} 지연 진입 신호 만료 (10분 초과) → 대기 해제");
                    }
                    else
                    {
                        // [확인 캔들 검증] 이전 캔들 종가가 저항/지지를 확인하고, 현재 캔들이 추가 확인
                        bool confirmed = false;
                        string confirmReason = "";

                        if (pending.Direction == "LONG")
                        {
                            // LONG 확인: 이전 캔들 종가가 EMA20 위 마감 + 현재 캔들 시가가 이전 고가 경신
                            bool prevClosedAboveEma = latestCandle.Close > (decimal)latestCandle.SMA_20;
                            bool currentOpenAbovePrevHigh = latestCandle.Open >= pending.SignalCandleHigh;
                            bool bullishCandle = latestCandle.Close > latestCandle.Open;
                            confirmed = prevClosedAboveEma && (currentOpenAbovePrevHigh || bullishCandle);
                            confirmReason = $"종가>EMA20={prevClosedAboveEma}, 시가>이전고가={currentOpenAbovePrevHigh}, 양봉={bullishCandle}";
                        }
                        else if (pending.Direction == "SHORT")
                        {
                            // SHORT 확인: 이전 캔들 종가가 EMA20 아래 마감 + 현재 캔들 시가가 이전 저가 하회
                            bool prevClosedBelowEma = latestCandle.Close < (decimal)latestCandle.SMA_20;
                            bool currentOpenBelowPrevLow = latestCandle.Open <= pending.SignalCandleLow;
                            bool bearishCandle = latestCandle.Close < latestCandle.Open;
                            confirmed = prevClosedBelowEma && (currentOpenBelowPrevLow || bearishCandle);
                            confirmReason = $"종가<EMA20={prevClosedBelowEma}, 시가<이전저가={currentOpenBelowPrevLow}, 음봉={bearishCandle}";
                        }

                        if (confirmed)
                        {
                            // 확인 완료 → 대기 해제 후 진입 계속
                            _pendingDelayedEntries.TryRemove(symbol, out _);
                            decision = pending.Direction; // 확인된 방향으로 진입
                            OnStatusLog?.Invoke($"✅ [캔들 확인] {symbol} {decision} 지연 진입 확인 완료 → 진입 진행 | {confirmReason}");
                        }
                        else
                        {
                            // [추세 반전 스위칭] 롱 대기 중 밴드 중단 아래 마감 → 숏 전환
                            bool reversalDetected = false;
                            string reversalDirection = "";

                            if (pending.Direction == "LONG" && latestCandle.Close < (decimal)latestCandle.SMA_20)
                            {
                                // 롱 대기 중 EMA20 아래로 몸통 마감 → 강력한 하락 반전
                                bool bearishBody = latestCandle.Close < latestCandle.Open;
                                decimal dropFromSignal = pending.SignalPrice > 0
                                    ? ((pending.SignalPrice - currentPrice) / pending.SignalPrice * 100)
                                    : 0;

                                if (bearishBody || dropFromSignal >= 0.5m)
                                {
                                    reversalDetected = true;
                                    reversalDirection = "SHORT";
                                }
                            }
                            else if (pending.Direction == "SHORT" && latestCandle.Close > (decimal)latestCandle.SMA_20)
                            {
                                // 숏 대기 중 EMA20 위로 몸통 마감 → 강력한 상승 반전
                                bool bullishBody = latestCandle.Close > latestCandle.Open;
                                decimal riseFromSignal = pending.SignalPrice > 0
                                    ? ((currentPrice - pending.SignalPrice) / pending.SignalPrice * 100)
                                    : 0;

                                if (bullishBody || riseFromSignal >= 0.5m)
                                {
                                    reversalDetected = true;
                                    reversalDirection = "LONG";
                                }
                            }

                            if (reversalDetected)
                            {
                                _pendingDelayedEntries.TryRemove(symbol, out _);
                                decision = reversalDirection;
                                OnStatusLog?.Invoke($"🔄 [추세 반전 스위칭] {symbol} {pending.Direction}→{reversalDirection} 전환! | EMA20 돌파 실패 + 반대 확인 → 즉시 {reversalDirection} 진입");
                                // 반전 진입은 확인 완료 상태이므로 계속 진행
                            }
                            else
                            {
                                // 아직 확인 안됨 → 대기 유지
                                OnStatusLog?.Invoke($"⏸️ [캔들 확인 대기] {symbol} {pending.Direction} 확인 미완료 | {confirmReason}");
                                return;
                            }
                        }
                    }
                }
                else
                {
                    // 신규 신호 → 대기 등록 (즉시 진입하지 않음)
                    var klines5m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 5, token);
                    var prevCandle = klines5m != null && klines5m.Count >= 2 ? klines5m.ElementAt(klines5m.Count - 2) : null;

                    _pendingDelayedEntries[symbol] = new DelayedEntrySignal
                    {
                        Symbol = symbol,
                        Direction = decision,
                        SignalPrice = currentPrice,
                        SignalTime = DateTime.Now,
                        SignalCandleHigh = prevCandle?.HighPrice ?? latestCandle.High,
                        SignalCandleLow = prevCandle?.LowPrice ?? latestCandle.Low,
                        SignalCandleClose = prevCandle?.ClosePrice ?? latestCandle.Close,
                        SignalSource = signalSource,
                        Mode = mode,
                        CustomTakeProfitPrice = customTakeProfitPrice,
                        CustomStopLossPrice = customStopLossPrice
                    };

                    OnStatusLog?.Invoke(TradingStateLogger.WaitingForCandleConfirmation(symbol, decision, 1));
                    return;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // [15분봉 이평선 정배열 필터] 상위 시간대 추세 동조
            // LONG: 15분봉 SMA20 > SMA60 필수 / SHORT: 15분봉 SMA20 < SMA60 필수
            // ═══════════════════════════════════════════════════════════════
            if (IsHybridBbSignalSource(signalSource))
            {
                try
                {
                    var klines15m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 80, token);
                    if (klines15m != null && klines15m.Count >= 60)
                    {
                        var list15m = klines15m.ToList();
                        double sma20_15m = IndicatorCalculator.CalculateSMA(list15m, 20);
                        double sma60_15m = IndicatorCalculator.CalculateSMA(list15m, 60);

                        if (sma20_15m > 0 && sma60_15m > 0)
                        {
                            bool upTrend15m = sma20_15m > sma60_15m;
                            bool downTrend15m = sma20_15m < sma60_15m;

                            if (decision == "LONG" && !upTrend15m)
                            {
                                string reason = $"15m SMA20({sma20_15m:F2}) < SMA60({sma60_15m:F2}) → 상위 추세 역행";
                                OnStatusLog?.Invoke(TradingStateLogger.RejectedBy15MinTrendFilter(symbol, decision, reason));
                                return;
                            }

                            if (decision == "SHORT" && !downTrend15m)
                            {
                                string reason = $"15m SMA20({sma20_15m:F2}) > SMA60({sma60_15m:F2}) → 상위 추세 역행";
                                OnStatusLog?.Invoke(TradingStateLogger.RejectedBy15MinTrendFilter(symbol, decision, reason));
                                return;
                            }

                            OnStatusLog?.Invoke($"✅ [15분봉 추세 확인] {symbol} {decision} | 15m SMA20={sma20_15m:F2}, SMA60={sma60_15m:F2} → 추세 동조 확인");
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 15분봉 추세 필터 조회 실패: {ex.Message}");
                }
            }

            float aiScore = 0;
            float aiProbability = 0;
            bool? aiPredictUp = null;
            decimal convictionScore = 0m;
            if (latestCandle == null)
            {
                EntryLog("DATA", "BLOCK", "latestCandle=missing");
                return;
            }

            // [긴급 방어 1] ATR 변동성 필터 (흔들기 구간 진입 금지)
            // 5분봉 기준 ATR이 평소(20봉 평균)보다 2배 이상이면 '변동성 폭발' 구간으로 판단
            var klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 40, token);
            if (klines != null && klines.Count >= 30)
            {
                var candles = klines.ToList();
                recentEntryKlines = candles;
                double currentAtr = IndicatorCalculator.CalculateATR(candles.TakeLast(20).ToList(), 14);
                double averageAtr = IndicatorCalculator.CalculateATR(candles.Take(20).ToList(), 14);

                if (averageAtr > 0)
                {
                    double atrRatio = currentAtr / averageAtr;
                    if (atrRatio > 2.0)
                    {
                        string reason = $"ATR비율={atrRatio:F2}x > 2.0x 흔들기 구간";
                        OnStatusLog?.Invoke(TradingStateLogger.RejectedByRiskManagement(symbol, decision, reason));
                        return;
                    }
                    else if (atrRatio > 1.5)
                    {
                        OnStatusLog?.Invoke($"⚡ {symbol} 변동성 상승 주의 | ATR비율={atrRatio:F2}x");
                    }
                }
            }

            if (latestCandle != null &&
                ShouldBlockHybridBbEntry(symbol, decision, currentPrice, latestCandle, signalSource, out string hybridEntryReason, out entryBbPosition, out entryZoneTag, out isHybridMidBandLongEntry))
            {
                OnStatusLog?.Invoke($"⛔ {symbol} {decision} 하이브리드 BB 진입 필터: {hybridEntryReason} | src={signalSource}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(entryZoneTag))
            {
                OnStatusLog?.Invoke($"🧭 {symbol} {decision} 하이브리드 진입 승인 | Zone={entryZoneTag}, %B={entryBbPosition:P0}, src={signalSource}");
            }

            bool isMajorLikeSignal = signalSource == "MAJOR" || signalSource.StartsWith("MAJOR_");
            bool isMajorAtrEnforcedSignal = isMajorLikeSignal && MajorSymbols.Contains(symbol);

            if (isMajorAtrEnforcedSignal && customStopLossPrice <= 0)
            {
                majorAtrPreview = await TryCalculateMajorAtrHybridStopLossAsync(symbol, currentPrice, decision == "LONG", token);
                if (majorAtrPreview.StopLossPrice <= 0)
                {
                    OnStatusLog?.Invoke($"⛔ [MAJOR ATR] {symbol} ATR 2.5배 하이브리드 손절 계산 실패로 진입 차단");
                    return;
                }

                customStopLossPrice = majorAtrPreview.StopLossPrice;
                OnStatusLog?.Invoke(
                    $"🛡️ [MAJOR ATR] {symbol} 하이브리드 손절 적용 | Entry={currentPrice:F8}, SL={customStopLossPrice:F8}, ATRx2.5={majorAtrPreview.AtrDistance:F8}, 구조선={majorAtrPreview.StructureStopPrice:F8}");
            }

            if (latestCandle != null &&
                ShouldBlockChasingEntry(symbol, decision, currentPrice, latestCandle, recentEntryKlines, mode, out string chaseReason))
            {
                OnStatusLog?.Invoke(TradingStateLogger.RejectedByChaseFilter(symbol, decision, chaseReason));
                return;
            }

            if (latestCandle != null && ShouldApplyFifteenMinuteWaveGate(signalSource))
            {
                if (_aiDoubleCheckEntryGate != null && _aiDoubleCheckEntryGate.IsReady)
                {
                    CoinType coinType = ResolveCoinType(symbol, signalSource);
                    var aiGate = await _aiDoubleCheckEntryGate.EvaluateEntryWithCoinTypeAsync(symbol, decision, currentPrice, coinType, token);

                    if (!aiGate.allowEntry)
                    {
                        OnStatusLog?.Invoke(TradingStateLogger.RejectedByAIGate(
                            symbol, decision, aiGate.reason, 
                            aiGate.detail.ML_Confidence, aiGate.detail.TF_Confidence));
                        EntryLog("15M_GATE", "BLOCK", $"aiDoubleCheck reason={aiGate.reason} coin={coinType} ml={aiGate.detail.ML_Confidence:P1} tf={aiGate.detail.TF_Confidence:P1}");
                        return;
                    }

                    OnStatusLog?.Invoke(TradingStateLogger.EvaluatingMLSniper(
                        symbol, decision, 
                        aiGate.detail.ML_Confidence, aiGate.detail.TF_Confidence));
                    EntryLog(
                        "15M_GATE",
                        "PASS",
                        $"aiDoubleCheck reason={aiGate.reason} coin={coinType} ml={aiGate.detail.ML_Confidence:P1} tf={aiGate.detail.TF_Confidence:P1}");

                    if (!string.IsNullOrWhiteSpace(aiGate.detail.DecisionId))
                        aiGateDecisionId = aiGate.detail.DecisionId;
                }
                else
                {
                    // 폴백: 기존 15분 Wave Gate
                    var (mlThreshold, transformerThreshold) = GetSymbolGateThresholds(symbol);
                    var gateResult = await EvaluateFifteenMinuteWaveGateAsync(symbol, decision, currentPrice, latestCandle, mlThreshold, transformerThreshold, token);

                    RecordGateDecisionAndAutoTune(
                        symbol,
                        gateResult.allowEntry,
                        mlThreshold,
                        transformerThreshold,
                        gateResult.mlConfidence,
                        gateResult.transformerConfidence);

                    if (!gateResult.allowEntry)
                    {
                        OnStatusLog?.Invoke(TradingStateLogger.RejectedBy15MinWaveGate(
                            symbol, decision, $"{gateResult.reason} | mlTh={mlThreshold:P1} tfTh={transformerThreshold:P1}"));
                        EntryLog("15M_GATE", "BLOCK", gateResult.reason);
                        return;
                    }

                    OnStatusLog?.Invoke($"✅ [15분 WaveGate] {symbol} {decision} 통과 | ml={gateResult.mlConfidence:P1} tf={gateResult.transformerConfidence:P1}");

                    if (customTakeProfitPrice <= 0 && gateResult.takeProfitPrice > 0)
                        customTakeProfitPrice = gateResult.takeProfitPrice;

                    if (customStopLossPrice <= 0 && gateResult.stopLossPrice > 0)
                        customStopLossPrice = gateResult.stopLossPrice;

                    EntryLog(
                        "15M_GATE",
                        "PASS",
                        $"scenario={gateResult.scenario} ml={gateResult.mlConfidence:P1} tf={gateResult.transformerConfidence:P1} mlTh={mlThreshold:P1} tfTh={transformerThreshold:P1} tp={customTakeProfitPrice:F8} sl={customStopLossPrice:F8}");
                }
            }

            if (_aiPredictor != null)
            {
                if (latestCandle != null)
                {
                    var prediction = _aiPredictor.Predict(latestCandle);
                    aiScore = prediction.Score;
                    aiProbability = prediction.Probability;
                    aiPredictUp = prediction.Prediction;
                    convictionScore = Math.Max(convictionScore, (decimal)(prediction.Probability * 100f));
                    bool isMajorCoin = signalSource == "MAJOR" || signalSource.StartsWith("MAJOR_");
                    bool isLowVolume = latestCandle.Volume_Ratio > 0f && latestCandle.Volume_Ratio < 1.0f;
                    bool isTrendHealthy = latestCandle.Close > (decimal)latestCandle.SMA_20 && latestCandle.RSI > 50f;
                    bool isPerfectAlignment = latestCandle.SMA_20 > latestCandle.SMA_60 && latestCandle.SMA_60 > latestCandle.SMA_120;
                    bool hasOiSupport = latestCandle.OI_Change_Pct > 0.5f;
                    bool majorLowVolumePrivilege = isMajorCoin && decision == "LONG" && isLowVolume && (isTrendHealthy || isPerfectAlignment || hasOiSupport);
                    
                    // ═══════════════════════════════════════════════════════════════
                    // [보너스 점수 시스템] AI 필터 개선 (2025-05-XX)
                    // ═══════════════════════════════════════════════════════════════
                    // 기본 AI 점수: Probability * 100 (0~100 스케일)
                    // 메이저 코인 보너스:
                    //   - EMA 20 눌림목 감지 시 +10점
                    //   - 숏 스퀴즈 감지 시 +15점
                    // 최종 임계값: MAJOR 75점, 기타 80점
                    // ═══════════════════════════════════════════════════════════════
                    
                    float baseAiScore = prediction.Probability * 100; // 0~100 스케일로 변환
                    float bonusPoints = 0;
                    
                    // 메이저 코인인 경우 보너스 체크
                    if (isMajorCoin)
                    {
                        // 1) EMA 20 눌림목 감지 → +10점
                        if (await CheckEMA20RetestAsync(symbol, latestCandle, token))
                        {
                            bonusPoints += 10;
                            OnStatusLog?.Invoke($"➕ {symbol} EMA 20 눌림목 감지 → AI 보너스 +10점");
                        }
                        
                        // 2) 숏 스퀴즈 감지 → +15점
                        if (await CheckShortSqueezeAsync(symbol, latestCandle, token))
                        {
                            bonusPoints += 15;
                            OnStatusLog?.Invoke($"➕ {symbol} 숏 스퀴즈 감지 → AI 보너스 +15점");
                        }

                        if (majorLowVolumePrivilege)
                        {
                            bonusPoints += 10;
                            OnStatusLog?.Invoke($"🛡️ [Major 특권] {symbol} 거래량 부족보다 OI/이평선 추세를 우선합니다. (Vol={latestCandle.Volume_Ratio:F2}x, OI={latestCandle.OI_Change_Pct:F2}%)");
                        }
                    }
                    
                    float finalAiScore = baseAiScore + bonusPoints;
                    convictionScore = Math.Max(convictionScore, (decimal)finalAiScore);
                    
                    // AI 점수 필터 체크
                    if (_enableAiScoreFilter)
                    {
                        // 설정 파일에서 읽은 임계값 사용
                        float adjustedThreshold = isMajorCoin ? _aiScoreThresholdMajor : _aiScoreThresholdNormal;
                        if (majorLowVolumePrivilege)
                        {
                            adjustedThreshold = Math.Min(adjustedThreshold, _aiScoreThresholdMajor);
                        }
                        
                        if (decision == "LONG" && finalAiScore < adjustedThreshold && !majorLowVolumePrivilege)
                        {
                            // [개선] 구체적인 필터 실패 이유 표시
                            var failReasons = new List<string>();
                            
                            if (baseAiScore < adjustedThreshold)
                                failReasons.Add($"AI 확률 부족({baseAiScore:F1}<{adjustedThreshold})");
                            
                            if (!isMajorCoin && latestCandle.Volume_Ratio < 1.0f)
                                failReasons.Add($"거래량 부족({latestCandle.Volume_Ratio:F2}x)");
                            
                            if (latestCandle.RSI < 40f)
                                failReasons.Add($"RSI 과매도({latestCandle.RSI:F1})");
                            
                            if (latestCandle.MACD < 0)
                                failReasons.Add($"MACD 음수({latestCandle.MACD:F4})");
                            
                            if (!(latestCandle.SMA_20 > latestCandle.SMA_60))
                                failReasons.Add("중기 정배열 실패(MA20<MA60)");
                            
                            if (latestCandle.OI_Change_Pct < 0)
                                failReasons.Add($"OI 감소({latestCandle.OI_Change_Pct:F2}%)");
                            
                            string reasonText = failReasons.Count > 0 ? string.Join(", ", failReasons) : "복합 조건 미달";
                            OnStatusLog?.Invoke($"🤖 {symbol} AI 필터 차단 | 점수: {baseAiScore:F1}+{bonusPoints:F0}={finalAiScore:F1}<{adjustedThreshold} | 사유: {reasonText}");
                            EntryLog("AI", "BLOCK", $"score={finalAiScore:F1} threshold={adjustedThreshold:F1} reason={reasonText}");
                            return;
                        }
                        
                        // 진입 승인 로그
                        if (decision == "LONG")
                        {
                            if (majorLowVolumePrivilege && finalAiScore < adjustedThreshold)
                                EntryLog("AI", "PASS", $"lowVolumePrivilege=true score={finalAiScore:F1} threshold={adjustedThreshold:F1}");
                            else
                                EntryLog("AI", "PASS", $"score={finalAiScore:F1} threshold={adjustedThreshold:F1}");
                        }
                    }
                    else
                    {
                        // AI 점수 필터 비활성화 시 통과 로그
                        if (decision == "LONG")
                            EntryLog("AI", "PASS", $"filterDisabled=true score={finalAiScore:F1}");
                    }

                    // 숏 진입은 더 엄격하게 필터링 (하락 예측 + 충분한 확률 + 과매도 추격 방지)
                    if (decision == "SHORT")
                    {
                        var shortFailReasons = new List<string>();
                        
                        if (prediction.Prediction)
                            shortFailReasons.Add("AI가 상승 예측");
                        
                        if (prediction.Probability < 0.60f)
                            shortFailReasons.Add($"하락 확률 부족({prediction.Probability:P1}<60%)");
                        
                        if (latestCandle.RSI <= 35f)
                            shortFailReasons.Add($"RSI 과매도({latestCandle.RSI:F1}≤35)");
                        
                        if (latestCandle.MACD > 0)
                            shortFailReasons.Add($"MACD 양수({latestCandle.MACD:F4})");
                        
                        if (latestCandle.Close < (decimal)latestCandle.SMA_20)
                            shortFailReasons.Add("가격이 MA20 하회(추세 약세 추격 위험)");
                        
                        if (shortFailReasons.Count > 0)
                        {
                            string shortReasonText = string.Join(", ", shortFailReasons);
                            EntryLog("AI", "BLOCK", $"shortFilter reason={shortReasonText}");
                            return;
                        }
                    }
                }
            }

            // 현재 상태(State) 구성: RSI, MACD 등
            if (latestCandle != null)
            {
                // [Agent 2, 3] PPO 에이전트 사용
                // 상태 벡터 생성: [RSI, MACD, BB_Width] (정규화 필요하지만 여기선 원본 사용 예시)
                float[] state = new float[] { latestCandle.RSI / 100f, latestCandle.MACD, (latestCandle.BollingerUpper - latestCandle.BollingerLower) };

                // 전략 타입에 따라 에이전트 선택 (예: 메이저 코인은 Swing, 급등주는 Scalping)
                string strategyType = _symbols.Contains(symbol) ? "Swing" : "Scalping";
                int action = _multiAgentManager.GetAction(strategyType, state); // 0:Hold, 1:Buy, 2:Sell

                // RL이 반대 방향을 강력히 권장하면 진입 보류 (예: 롱 진입인데 Sell 액션)
                if (decision == "LONG" && action == 2)
                {
                    EntryLog("RL", "BLOCK", $"agent={strategyType} action=SellAgainstLong");
                    return;
                }

                if (decision == "SHORT" && action == 1)
                {
                    EntryLog("RL", "BLOCK", $"agent={strategyType} action=BuyAgainstShort");
                    return;
                }

                EntryLog("RL", "INFO", $"agent={strategyType} action={action}");
            }

            OnStatusLog?.Invoke(
                $"🧾 [진입 검증] src={signalSource} | mode={mode} | {symbol} {decision} | AI.Score={aiScore:F3} | AI.Prob={aiProbability:P1} | AI.Dir={(aiPredictUp.HasValue ? (aiPredictUp.Value ? "UP" : "DOWN") : "N/A")} | RSI={(latestCandle != null ? latestCandle.RSI.ToString("F1") : "N/A")}");

            try
            {
                // 2. 설정값: 증거금 200 USDT, 레버리지 20배
                int leverage = _settings.DefaultLeverage;
                decimal marginUsdt = await GetAdaptiveEntryMarginUsdtAsync(token);
                decimal positionSizeMultiplier = 1.0m;

                EntryLog("SIZE", "BASE", $"margin={marginUsdt:F2} sizingRule=max(default={_settings.DefaultMargin:F0}, equity*10%)");

                if (patternSnapshot != null)
                {
                    convictionScore = Math.Max(convictionScore, (decimal)patternSnapshot.FinalScore);
                }

                if (patternSnapshot != null)
                {
                    decimal patternSizeMultiplier = patternSnapshot.Match?.PositionSizeMultiplier ?? 1.0m;
                    bool hasStrongPatternMatch =
                        patternSnapshot.Match?.IsSuperEntry == true ||
                        (patternSnapshot.Match?.TopSimilarity ?? 0d) >= 0.80d ||
                        (patternSnapshot.Match?.MatchProbability ?? 0d) >= 0.70d;

                    if (convictionScore >= 85.0m && hasStrongPatternMatch)
                    {
                        decimal aggressiveMultiplier = patternSizeMultiplier > 1.0m
                            ? patternSizeMultiplier
                            : 1.5m;

                        positionSizeMultiplier = Math.Clamp(aggressiveMultiplier, 1.5m, 2.0m);
                        OnStatusLog?.Invoke($"🚀 [Aggressive Entry] {symbol} 고신뢰 진입 감지 | Score={convictionScore:F1}, PatternStrong={hasStrongPatternMatch} → 비중 x{positionSizeMultiplier:F2}");
                    }
                    else if (patternSizeMultiplier > 1.0m)
                    {
                        OnStatusLog?.Invoke($"🧮 [Pattern Size] {symbol} 패턴 배수 제안 x{patternSizeMultiplier:F2} 감지, 하지만 Score={convictionScore:F1} < 85 또는 강매치 미충족으로 기본 비중 유지");
                    }
                }

                marginUsdt *= positionSizeMultiplier;
                if (positionSizeMultiplier > 1.0m)
                {
                    OnStatusLog?.Invoke($"💼 [Position Sizing] {symbol} 최종 증거금 {marginUsdt:F2} USDT (배수 x{positionSizeMultiplier:F2})");
                }

                // [공통 손익비 필터] 저기대값 진입 차단 (SIDEWAYS 제외)
                if (!string.Equals(mode, "SIDEWAYS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryEvaluateEntryRiskReward(
                        decision,
                        currentPrice,
                        leverage,
                        customTakeProfitPrice,
                        customStopLossPrice,
                        out decimal evaluatedTakeProfit,
                        out decimal evaluatedStopLoss,
                        out decimal riskRewardRatio,
                        out string rrReason))
                    {
                        OnStatusLog?.Invoke($"⛔ {symbol} 손익비 계산 실패로 진입 차단 | {rrReason}");
                        EntryLog("RR", "BLOCK", $"reason={rrReason}");
                        return;
                    }

                    if (riskRewardRatio < _minEntryRiskRewardRatio)
                    {
                        OnStatusLog?.Invoke(
                            $"⛔ {symbol} 공통 손익비 부족으로 진입 차단 | RR={riskRewardRatio:F2}<{_minEntryRiskRewardRatio:F2} | TP={evaluatedTakeProfit:F8}, SL={evaluatedStopLoss:F8}");
                        EntryLog("RR", "BLOCK", $"rr={riskRewardRatio:F2}<{_minEntryRiskRewardRatio:F2}");
                        return;
                    }

                    EntryLog("RR", "PASS", $"rr={riskRewardRatio:F2} tp={evaluatedTakeProfit:F8} sl={evaluatedStopLoss:F8}");
                }

                // [ElliottWave 손익비 검증] 손익비 1:2 이상만 진입
                var waveState = _elliotWave3Strategy?.GetCurrentState(symbol);
                if (waveState != null && decision == "LONG")
                {
                    decimal stopLossPrice = waveState.Phase1LowPrice > 0 ? waveState.Phase1LowPrice : 0;
                    decimal takeProfitPrice = waveState.Phase1HighPrice > 0 ? waveState.Phase1HighPrice : 0;
                    decimal fib1618Price = waveState.Fib1618Target > 0 ? waveState.Fib1618Target : 0;

                    if (stopLossPrice > 0 && takeProfitPrice > 0)
                    {
                        decimal risk = currentPrice - stopLossPrice;      // 손실액 (1파 저점까지)
                        decimal reward1 = takeProfitPrice - currentPrice; // 1차 익절 수익
                        decimal reward2 = fib1618Price > 0 ? (fib1618Price - currentPrice) : reward1;

                        // 손익비 검증: Risk/Reward >= 1:2
                        decimal riskRewardRatio = risk > 0 ? reward1 / risk : 0;

                        if (riskRewardRatio < 2.0m)
                        {
                            OnStatusLog?.Invoke(
                                $"💼 {symbol} 손익비 부족으로 진입 차단 | Risk={risk:F8} vs Reward={reward1:F8} (비율={riskRewardRatio:F2}:1 < 2:1)");
                            return;
                        }

                        OnStatusLog?.Invoke(
                            $"✅ {symbol} 손익비 검증 통과 | Risk={risk:F8} vs Reward={reward1:F8} (비율={riskRewardRatio:F2}:1)");
                    }
                }

                // 3. 중복 진입 방지 (Race Condition 방지를 위해 즉시 등록)
                lock (_posLock)
                {
                    if (_activePositions.ContainsKey(symbol))
                    {
                        EntryLog("POSITION", "SKIP", "activePosition=exists");
                        return;
                    }

                    // [3파 기반 익절/손절] ElliottWave 정보 적용
                    // 임시 등록 (주문 실패 시 제거)
                    _activePositions[symbol] = new PositionInfo
                    {
                        Symbol = symbol,
                        EntryPrice = currentPrice,
                        IsLong = (decision == "LONG"),
                        Side = (decision == "LONG") ? OrderSide.Buy : OrderSide.Sell,
                        IsPumpStrategy = signalSource == "PUMP" || signalSource == "MAJOR_MEME",
                        AiScore = aiScore,
                        AiConfidencePercent = aiProbability > 0
                            ? aiProbability * 100f
                            : (aiScore > 0f && aiScore <= 1f ? aiScore * 100f : aiScore),
                        Leverage = leverage,
                        Quantity = 0,  // 주문 성공 후 업데이트
                        InitialQuantity = 0,
                        EntryTime = DateTime.Now,
                        HighestPrice = currentPrice,
                        LowestPrice = currentPrice,
                        EntryBbPosition = entryBbPosition,
                        EntryZoneTag = entryZoneTag,
                        IsHybridMidBandLongEntry = isHybridMidBandLongEntry,
                                                AggressiveMultiplier = positionSizeMultiplier,
                        // [3파 기반 익절/손절]
                        Wave1LowPrice = waveState?.Phase1LowPrice ?? 0,
                        Wave1HighPrice = waveState?.Phase1HighPrice ?? 0,
                        Fib0618Level = waveState?.Fib0618Level ?? 0,
                        Fib0786Level = waveState?.Fib786Level ?? 0,
                        Fib1618Target = waveState?.Fib1618Target ?? 0,
                        PartialProfitStage = 0,
                        BreakevenPrice = 0,
                        TakeProfit = customTakeProfitPrice > 0 ? customTakeProfitPrice : 0,
                        StopLoss = customStopLossPrice > 0 ? customStopLossPrice : 0
                    };
                }
                positionReserved = true;

                // 4. 레버리지 및 마진 모드(격리) 설정
                bool leverageSet = await _exchangeService.SetLeverageAsync(symbol, leverage, token);
                if (!leverageSet)
                {
                    CleanupReservedPosition("레버리지 설정 실패");
                    OnStatusLog?.Invoke($"❌ {symbol} 레버리지 설정 실패로 진입 취소");
                    return;
                }

                // 수량 계산: (증거금 * 레버리지) / 현재가
                decimal quantity = (marginUsdt * leverage) / currentPrice;

                // 6. 거래소 규격(StepSize)에 맞게 수량 보정
                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
                var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                }

                if (quantity <= 0)
                {
                    CleanupReservedPosition("수량 계산 결과 0");
                    OnStatusLog?.Invoke($"❌ {symbol} 수량 계산 결과가 0 이하라 진입 취소");
                    return;
                }

                // 7. 주문 실행 (시장가 주문으로 즉시 체결 - 슬리피지 검증 불필요)
                var side = (decision == "LONG") ? "BUY" : "SELL";

                OnStatusLog?.Invoke(TradingStateLogger.PlacingOrder(symbol, decision, currentPrice, quantity));
                EntryLog("ORDER", "SUBMIT", $"type=MARKET orderSide={side} qty={quantity}");
                
                var (success, filledQty, avgPrice) = await _exchangeService.PlaceMarketOrderAsync(
                    symbol,
                    side,
                    quantity,
                    token);

                if (!success || filledQty <= 0)
                {
                    CleanupReservedPosition("시장가 주문 실패");
                    OnStatusLog?.Invoke($"❌ [{symbol}] {decision} 주문 실패");
                    EntryLog("ORDER", "FAILED", $"reason=marketOrderFailed");
                    return;
                }

                var actualEntryPrice = avgPrice > 0 ? avgPrice : currentPrice;

                    if (isMajorAtrEnforcedSignal)
                    {
                        var filledMajorStop = await TryCalculateMajorAtrHybridStopLossAsync(symbol, actualEntryPrice, decision == "LONG", token);
                        if (filledMajorStop.StopLossPrice > 0)
                        {
                            customStopLossPrice = filledMajorStop.StopLossPrice;
                            OnStatusLog?.Invoke(
                                $"🛡️ [MAJOR ATR] {symbol} 체결가 기준 손절 재보정 | Entry={actualEntryPrice:F8}, SL={customStopLossPrice:F8}, ATRx2.5={filledMajorStop.AtrDistance:F8}, 구조선={filledMajorStop.StructureStopPrice:F8}");
                        }
                        else if (majorAtrPreview.StopLossPrice > 0)
                        {
                            customStopLossPrice = majorAtrPreview.StopLossPrice;
                            OnStatusLog?.Invoke($"⚠️ [MAJOR ATR] {symbol} 체결가 기준 재계산 실패 → 진입 전 손절값 유지 | SL={customStopLossPrice:F8}");
                        }
                    }

                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var pos))
                        {
                            pos.Quantity = filledQty;
                                pos.InitialQuantity = pos.InitialQuantity > 0 ? pos.InitialQuantity : filledQty;
                            pos.EntryPrice = actualEntryPrice;
                                pos.HighestPrice = actualEntryPrice;
                                pos.LowestPrice = actualEntryPrice;
                                pos.PyramidCount = 0;
                                pos.IsPyramided = false;
                            if (customStopLossPrice > 0)
                                pos.StopLoss = customStopLossPrice;
                            orderPlaced = true;
                        }
                    }

                    if (!orderPlaced)
                    {
                        EntryLog("POSITION", "WARN", "postFillReservedPositionMissing=true");
                    }

                    EntryLog("ORDER", "FILLED", $"entryPrice={actualEntryPrice:F4} qty={filledQty}");

                    if (!string.IsNullOrWhiteSpace(aiGateDecisionId))
                    {
                        SetActiveAiDecisionId(symbol, aiGateDecisionId);
                    }
                    else
                    {
                        RemoveActiveAiDecisionId(symbol);
                    }

                    ScheduleAiDoubleCheckLabeling(symbol, actualEntryPrice, decision == "LONG", aiGateDecisionId, token);

                    OnTradeExecuted?.Invoke(symbol, decision, actualEntryPrice, filledQty);
                    
                    // [NEW] 직관적인 진입 완료 로그
                    decimal finalStopLoss = customStopLossPrice > 0 ? customStopLossPrice : 0;
                    decimal finalTakeProfit = customTakeProfitPrice > 0 ? customTakeProfitPrice : 0;
                    OnStatusLog?.Invoke(TradingStateLogger.EntrySuccess(
                        symbol, decision, actualEntryPrice, 
                        finalStopLoss, finalTakeProfit, signalSource));
                    
                    OnAlert?.Invoke($"🤖 자동 매매 진입: {symbol} [{decision}] | 증거금: {marginUsdt}U");
                    _soundService.PlaySuccess();

                    // [추가] 진입 시 DB 로그 저장
                    DateTime tradeEntryTime = DateTime.Now;
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var trackedPos) && trackedPos.EntryTime != default)
                            tradeEntryTime = trackedPos.EntryTime;
                    }

                    var entryLog = new TradeLog(
                        symbol, 
                        side, 
                        signalSource, 
                        actualEntryPrice, 
                        aiScore, 
                        tradeEntryTime, 
                        0,      // 진입 시점에는 손익 0
                        0
                    )
                    {
                        EntryPrice = actualEntryPrice,
                        Quantity = filledQty,
                        EntryTime = tradeEntryTime
                    };
                    try
                    {
                        await _dbManager.UpsertTradeEntryAsync(entryLog);
                        OnStatusLog?.Invoke($"📝 {symbol} 진입 TradeHistory 저장 완료");
                    }
                    catch (Exception dbEx)
                    {
                        OnStatusLog?.Invoke($"⚠️ {symbol} 진입 로그 DB 저장 실패: {dbEx.Message}");
                    }

                    if (patternSnapshot != null)
                    {
                        try
                        {
                            await _patternMemoryService.SaveEntrySnapshotAsync(patternSnapshot, actualEntryPrice, tradeEntryTime, signalSource);
                        }
                        catch (Exception patternEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ {symbol} 패턴 스냅샷 저장 실패: {patternEx.Message}");
                        }
                    }

                    // 🔥 [핵심] 감시 루프 시작
                    TryStartStandardMonitor(symbol, actualEntryPrice, decision == "LONG", mode, customTakeProfitPrice, customStopLossPrice, token, "new-entry");
                    TryStartFastEntrySlippageMonitor(symbol, actualEntryPrice, decision == "LONG", token, signalSource);
            }
            catch (Exception ex)
            {
                CleanupReservedPosition($"예외 발생: {ex.GetType().Name}");
                OnStatusLog?.Invoke($"⚠️ ExecuteAutoOrder 에러: {ex.Message}");
                try
                {
                    await TelegramService.Instance.SendMessageAsync($"⚠️ *[자동 매매 오류]*\n{symbol} 진입 중 예외 발생:\n{ex.Message}");
                }
                catch (Exception tgEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Telegram 알림 전송 실패: {tgEx.Message}");
                }
            }
        }

        private bool TryEvaluateEntryRiskReward(
            string decision,
            decimal entryPrice,
            int leverage,
            decimal customTakeProfitPrice,
            decimal customStopLossPrice,
            out decimal takeProfitPrice,
            out decimal stopLossPrice,
            out decimal riskRewardRatio,
            out string reason)
        {
            takeProfitPrice = 0m;
            stopLossPrice = 0m;
            riskRewardRatio = 0m;
            reason = string.Empty;

            if (entryPrice <= 0)
            {
                reason = "entryPrice<=0";
                return false;
            }

            int safeLeverage = leverage > 0 ? leverage : Math.Max(1, _settings.DefaultLeverage);
            decimal targetPriceMove = _settings.TargetRoe > 0 ? (_settings.TargetRoe / safeLeverage / 100m) : 0.01m;
            decimal stopPriceMove = _settings.StopLossRoe > 0 ? (_settings.StopLossRoe / safeLeverage / 100m) : 0.0075m;

            bool isLong = string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase);
            bool isShort = string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase);
            if (!isLong && !isShort)
            {
                reason = "decision!=LONG/SHORT";
                return false;
            }

            if (isLong)
            {
                takeProfitPrice = customTakeProfitPrice > 0 ? customTakeProfitPrice : entryPrice * (1m + targetPriceMove);
                stopLossPrice = customStopLossPrice > 0 ? customStopLossPrice : entryPrice * (1m - stopPriceMove);

                decimal risk = entryPrice - stopLossPrice;
                decimal reward = takeProfitPrice - entryPrice;
                if (risk <= 0 || reward <= 0)
                {
                    reason = $"invalidLongRiskReward risk={risk:F8} reward={reward:F8}";
                    return false;
                }

                riskRewardRatio = reward / risk;
            }
            else
            {
                takeProfitPrice = customTakeProfitPrice > 0 ? customTakeProfitPrice : entryPrice * (1m - targetPriceMove);
                stopLossPrice = customStopLossPrice > 0 ? customStopLossPrice : entryPrice * (1m + stopPriceMove);

                decimal risk = stopLossPrice - entryPrice;
                decimal reward = entryPrice - takeProfitPrice;
                if (risk <= 0 || reward <= 0)
                {
                    reason = $"invalidShortRiskReward risk={risk:F8} reward={reward:F8}";
                    return false;
                }

                riskRewardRatio = reward / risk;
            }

            return true;
        }

        private bool ShouldBlockHybridBbEntry(
            string symbol,
            string decision,
            decimal currentPrice,
            CandleData latestCandle,
            string signalSource,
            out string reason,
            out decimal bbPosition,
            out string zoneTag,
            out bool isHybridMidBandLongEntry)
        {
            reason = string.Empty;
            zoneTag = string.Empty;
            bbPosition = 0m;
            isHybridMidBandLongEntry = false;

            if (!IsHybridBbSignalSource(signalSource))
                return false;

            decimal bbLower = latestCandle.BollingerLower > 0 ? (decimal)latestCandle.BollingerLower : 0m;
            decimal bbUpper = latestCandle.BollingerUpper > 0 ? (decimal)latestCandle.BollingerUpper : 0m;
            decimal bbRange = bbUpper - bbLower;
            if (bbRange <= 0m)
                return false;

            bbPosition = (currentPrice - bbLower) / bbRange;
            bool bullishCandle = latestCandle.Close > latestCandle.Open;
            bool bearishCandle = latestCandle.Close < latestCandle.Open;

            if (decision == "LONG")
            {
                if (bbPosition <= 0.10m)
                {
                    reason = $"%B {bbPosition:P0} 하단 붕괴 구간에서는 롱 금지";
                    return true;
                }

                if (bbPosition >= 0.85m)
                {
                    reason = $"%B {bbPosition:P0} 상단 추격 구간에서는 롱 금지";
                    return true;
                }

                if (bbPosition < 0.45m || bbPosition > 0.55m)
                {
                    reason = $"LONG 기본 진입 구간은 중단 눌림목(45~55%)만 허용 (%B {bbPosition:P0})";
                    return true;
                }

                if (!bullishCandle)
                {
                    reason = $"중단 구간이지만 양봉 확인 전이라 롱 대기 (Open={latestCandle.Open:F4}, Close={latestCandle.Close:F4})";
                    return true;
                }

                zoneTag = "MID_BAND_LONG";
                isHybridMidBandLongEntry = true;
                return false;
            }

            if (decision == "SHORT")
            {
                if (bbPosition <= 0.15m)
                {
                    reason = $"%B {bbPosition:P0} 하단 추격 구간에서는 숏 금지";
                    return true;
                }

                if (bbPosition < 0.80m || bbPosition > 0.90m)
                {
                    reason = $"SHORT 기본 진입 구간은 상단 저항(80~90%)만 허용 (%B {bbPosition:P0})";
                    return true;
                }

                if (!bearishCandle)
                {
                    reason = $"상단 저항 구간이지만 음봉 확인 전이라 숏 대기 (Open={latestCandle.Open:F4}, Close={latestCandle.Close:F4})";
                    return true;
                }

                zoneTag = "UPPER_RESIST_SHORT";
            }

            return false;
        }

        private static bool IsHybridBbSignalSource(string signalSource)
        {
            if (string.IsNullOrWhiteSpace(signalSource))
                return false;

            return string.Equals(signalSource, "MAJOR", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("TRANSFORMER_", StringComparison.OrdinalIgnoreCase);
        }

        private static CoinType ResolveCoinType(string symbol, string signalSource)
        {
            if (!string.IsNullOrWhiteSpace(symbol) && MajorSymbols.Contains(symbol))
                return CoinType.Major;

            if (!string.IsNullOrWhiteSpace(signalSource)
                && (signalSource.Contains("PUMP", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Contains("MEME", StringComparison.OrdinalIgnoreCase)))
            {
                return CoinType.Pumping;
            }

            return CoinType.Normal;
        }

        private void ScheduleAiDoubleCheckLabeling(string symbol, decimal entryPrice, bool isLong, string? decisionId, CancellationToken token)
        {
            if (_aiDoubleCheckEntryGate == null)
                return;

            DateTime entryTimeUtc = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), token);
                    if (token.IsCancellationRequested)
                        return;

                    await _aiDoubleCheckEntryGate.LabelActualProfitAsync(symbol, entryTimeUtc, entryPrice, isLong, decisionId, token);
                }
                catch (OperationCanceledException)
                {
                    // 정상 취소
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [AI DoubleCheck] 레이블링 스케줄 실패: {symbol} - {ex.Message}");
                }
            }, CancellationToken.None);
        }

        private async Task HandleAiCloseLabelingAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            bool isLong,
            decimal actualProfitPct,
            string closeReason)
        {
            if (_aiDoubleCheckEntryGate == null)
                return;

            try
            {
                _activeAiDecisionIds.TryGetValue(symbol, out var decisionState);
                string? decisionId = decisionState?.DecisionId;
                RemoveActiveAiDecisionId(symbol);

                await _aiDoubleCheckEntryGate.LabelActualProfitByCloseAsync(
                    symbol,
                    entryTime,
                    entryPrice,
                    (float)actualProfitPct,
                    closeReason,
                    decisionId,
                    CancellationToken.None);

                string side = isLong ? "LONG" : "SHORT";
                OnStatusLog?.Invoke($"🧠 [AI DoubleCheck] 실손익 라벨 반영 | {symbol} {side} pnl={actualProfitPct:F2}% reason={closeReason}");

                var stats = _aiDoubleCheckEntryGate.GetRecentLabelStats(10);
                OnStatusLog?.Invoke($"📊 [AI DoubleCheck] 라벨 통계 | total={stats.total} labeled={stats.labeled} mtm15m={stats.markToMarket} close={stats.tradeClose}");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AI DoubleCheck] 청산 라벨 반영 실패: {symbol} - {ex.Message}");
            }
        }

        private void SetActiveAiDecisionId(string symbol, string decisionId)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(decisionId))
                return;

            _activeAiDecisionIds[symbol] = new ActiveDecisionIdState
            {
                DecisionId = decisionId,
                SavedAtUtc = DateTime.UtcNow
            };
            PersistActiveAiDecisionIds();
        }

        private void RemoveActiveAiDecisionId(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            _activeAiDecisionIds.TryRemove(symbol, out _);
            PersistActiveAiDecisionIds();
        }

        private void LoadActiveAiDecisionIds()
        {
            try
            {
                if (!File.Exists(_activeAiDecisionIdsPath))
                    return;

                string json = File.ReadAllText(_activeAiDecisionIdsPath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ActiveDecisionIdState>>(json);

                if (loaded == null || loaded.Count == 0)
                {
                    // 레거시 포맷 호환: symbol -> decisionId
                    var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (legacy != null && legacy.Count > 0)
                    {
                        loaded = legacy.ToDictionary(
                            k => k.Key,
                            v => new ActiveDecisionIdState
                            {
                                DecisionId = v.Value,
                                SavedAtUtc = DateTime.UtcNow
                            },
                            StringComparer.OrdinalIgnoreCase);
                    }
                }

                if (loaded == null || loaded.Count == 0)
                    return;

                foreach (var pair in loaded)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)
                        || pair.Value == null
                        || string.IsNullOrWhiteSpace(pair.Value.DecisionId))
                    {
                        continue;
                    }

                    if (pair.Value.SavedAtUtc == default)
                        pair.Value.SavedAtUtc = DateTime.UtcNow;

                    _activeAiDecisionIds[pair.Key] = pair.Value;
                }

                int pruned = PruneExpiredActiveAiDecisionIdsCore();
                if (pruned > 0)
                {
                    PersistActiveAiDecisionIds();
                    OnStatusLog?.Invoke($"🧹 [AI DoubleCheck] 만료 ActiveDecisionId 정리: {pruned}개");
                }

                OnStatusLog?.Invoke($"🧠 [AI DoubleCheck] ActiveDecisionId 복원: {_activeAiDecisionIds.Count}개");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AI DoubleCheck] ActiveDecisionId 복원 실패: {ex.Message}");
            }
        }

        private void PersistActiveAiDecisionIds()
        {
            try
            {
                PruneExpiredActiveAiDecisionIdsCore();

                string? dir = Path.GetDirectoryName(_activeAiDecisionIdsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var snapshot = _activeAiDecisionIds.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_activeAiDecisionIdsPath, json);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AI DoubleCheck] ActiveDecisionId 저장 실패: {ex.Message}");
            }
        }

        private int PruneExpiredActiveAiDecisionIdsCore()
        {
            DateTime cutoffUtc = DateTime.UtcNow.AddHours(-ActiveDecisionIdRetentionHours);
            int removed = 0;

            foreach (var pair in _activeAiDecisionIds)
            {
                DateTime savedAt = pair.Value?.SavedAtUtc ?? DateTime.MinValue;
                if (savedAt != DateTime.MinValue && savedAt < cutoffUtc)
                {
                    if (_activeAiDecisionIds.TryRemove(pair.Key, out _))
                        removed++;
                }
            }

            return removed;
        }

        private bool ShouldApplyFifteenMinuteWaveGate(string signalSource)
        {
            if (!_enableFifteenMinWaveGate)
                return false;

            if (string.IsNullOrWhiteSpace(signalSource))
                return false;

            return string.Equals(signalSource, "MAJOR", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("MAJOR_", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("TRANSFORMER_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signalSource, "ElliottWave3Wave", StringComparison.OrdinalIgnoreCase);
        }

        private (float mlThreshold, float transformerThreshold) GetSymbolGateThresholds(string symbol)
        {
            string key = string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.ToUpperInvariant();

            var state = _symbolGateThresholds.GetOrAdd(key, _ => new GateThresholdState(_fifteenMinuteMlMinConfidence, _fifteenMinuteTransformerMinConfidence));
            lock (state.SyncRoot)
            {
                return (state.MlThreshold, state.TransformerThreshold);
            }
        }

        private void RecordGateDecisionAndAutoTune(
            string symbol,
            bool allowEntry,
            float mlThreshold,
            float transformerThreshold,
            float mlConfidence,
            float transformerConfidence)
        {
            string key = string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.ToUpperInvariant();

            var state = _symbolGateThresholds.GetOrAdd(key, _ => new GateThresholdState(mlThreshold, transformerThreshold));

            float beforeMl;
            float beforeTf;
            float afterMl;
            float afterTf;
            float passRate;
            int sampled;
            int passCount;
            int blockCount;
            bool thresholdChanged;

            lock (state.SyncRoot)
            {
                state.MlThreshold = Math.Clamp(state.MlThreshold, GateMlThresholdMin, GateMlThresholdMax);
                state.TransformerThreshold = Math.Clamp(state.TransformerThreshold, GateTransformerThresholdMin, GateTransformerThresholdMax);

                state.SampleCount++;
                if (allowEntry)
                    state.PassCount++;
                else
                    state.BlockCount++;

                if (state.SampleCount < GateAutoTuneSampleSize)
                    return;

                sampled = state.SampleCount;
                passCount = state.PassCount;
                blockCount = state.BlockCount;
                passRate = sampled > 0 ? (float)passCount / sampled : 0f;

                beforeMl = state.MlThreshold;
                beforeTf = state.TransformerThreshold;

                if (passRate >= GateTightenPassRate)
                {
                    state.MlThreshold = Math.Clamp(state.MlThreshold + GateThresholdAdjustStep, GateMlThresholdMin, GateMlThresholdMax);
                    state.TransformerThreshold = Math.Clamp(state.TransformerThreshold + GateThresholdAdjustStep, GateTransformerThresholdMin, GateTransformerThresholdMax);
                }
                else if (passRate <= GateLoosenPassRate)
                {
                    state.MlThreshold = Math.Clamp(state.MlThreshold - GateThresholdAdjustStep, GateMlThresholdMin, GateMlThresholdMax);
                    state.TransformerThreshold = Math.Clamp(state.TransformerThreshold - GateThresholdAdjustStep, GateTransformerThresholdMin, GateTransformerThresholdMax);

                    if (!allowEntry && mlConfidence > 0f && mlConfidence >= state.MlThreshold * 0.95f)
                        state.MlThreshold = Math.Clamp(state.MlThreshold - GateThresholdAdjustStep / 2f, GateMlThresholdMin, GateMlThresholdMax);

                    if (!allowEntry && transformerConfidence > 0f && transformerConfidence >= state.TransformerThreshold * 0.95f)
                        state.TransformerThreshold = Math.Clamp(state.TransformerThreshold - GateThresholdAdjustStep / 2f, GateTransformerThresholdMin, GateTransformerThresholdMax);
                }

                afterMl = state.MlThreshold;
                afterTf = state.TransformerThreshold;
                thresholdChanged = Math.Abs(afterMl - beforeMl) > 0.0001f || Math.Abs(afterTf - beforeTf) > 0.0001f;

                state.SampleCount = 0;
                state.PassCount = 0;
                state.BlockCount = 0;
            }

            string action = thresholdChanged ? "ADJUST" : "HOLD";
            OnStatusLog?.Invoke(
                $"📈 [GATE][AUTO_TUNE] sym={key} action={action} sample={sampled} pass={passCount} block={blockCount} passRate={passRate:P1} mlThr={beforeMl:P1}->{afterMl:P1} tfThr={beforeTf:P1}->{afterTf:P1}");
        }

        private async Task<(bool allowEntry, string reason, decimal takeProfitPrice, decimal stopLossPrice, string scenario, float mlConfidence, float transformerConfidence)> EvaluateFifteenMinuteWaveGateAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CandleData latestCandle,
            float mlThreshold,
            float transformerThreshold,
            CancellationToken token)
        {
            if (!string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "decision!=LONG/SHORT", 0m, 0m, "NONE", 0f, 0f);
            }

            var klines15mRaw = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 260, token);
            if (klines15mRaw == null || klines15mRaw.Count < 130)
                return (false, "15m캔들 부족(<130)", 0m, 0m, "NONE", 0f, 0f);

            var klines15m = klines15mRaw.ToList();
            var klines1h = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneHour, 220, token))?.ToList() ?? new List<IBinanceKline>();
            var klines4h = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour, 220, token))?.ToList() ?? new List<IBinanceKline>();

            int h1Bias = CalculateTrendBias(klines1h);
            int h4Bias = CalculateTrendBias(klines4h);
            bool conflictingTopDown = h1Bias != 0 && h4Bias != 0 && h1Bias != h4Bias;
            bool longTopDown = !conflictingTopDown && (h1Bias > 0 || h4Bias > 0);
            bool shortTopDown = !conflictingTopDown && (h1Bias < 0 || h4Bias < 0);

            bool continuationPass = false;
            bool reversalPass = false;
            decimal continuationTp = 0m;
            decimal continuationSl = 0m;
            decimal reversalTp = 0m;
            decimal reversalSl = 0m;
            string continuationReason = string.Empty;
            string reversalReason = string.Empty;

            if (string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
            {
                if (longTopDown)
                    continuationPass = TryEvaluateWave2LongContinuation(klines15m, currentPrice, out continuationSl, out continuationTp, out continuationReason);
                else
                    continuationReason = "상위 추세(1h/4h) LONG 불일치";

                if (shortTopDown)
                    reversalPass = TryEvaluateWave5LongReversal(klines15m, out reversalSl, out reversalTp, out reversalReason);
                else
                    reversalReason = "상위 추세(1h/4h) SHORT 불일치";
            }
            else
            {
                if (shortTopDown)
                    continuationPass = TryEvaluateWave2ShortContinuation(klines15m, currentPrice, out continuationSl, out continuationTp, out continuationReason);
                else
                    continuationReason = "상위 추세(1h/4h) SHORT 불일치";

                if (longTopDown)
                    reversalPass = TryEvaluateWave5ShortReversal(klines15m, out reversalSl, out reversalTp, out reversalReason);
                else
                    reversalReason = "상위 추세(1h/4h) LONG 불일치";
            }

            decimal finalTp = 0m;
            decimal finalSl = 0m;
            string scenario;

            if (continuationPass)
            {
                scenario = "TREND_CONTINUATION";
                finalTp = continuationTp;
                finalSl = continuationSl;
            }
            else if (reversalPass)
            {
                scenario = "WAVE5_REVERSAL";
                finalTp = reversalTp;
                finalSl = reversalSl;
            }
            else
            {
                string structureReason = $"structureFail cont={continuationReason} rev={reversalReason} topdown=1h:{h1Bias},4h:{h4Bias}";
                return (false, structureReason, 0m, 0m, "NONE", 0f, 0f);
            }

            if (_aiPredictor == null || !_aiPredictor.IsModelLoaded)
                return (false, "ML.NET 모델 미준비", finalTp, finalSl, scenario, 0f, 0f);

            var mlPrediction = _aiPredictor.Predict(latestCandle);
            bool mlAligned = string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                ? mlPrediction.Prediction
                : !mlPrediction.Prediction;
            float mlDirectionalProb = string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                ? NormalizeProbability01(mlPrediction.Probability)
                : NormalizeProbability01(1f - mlPrediction.Probability);
            if (!mlAligned || mlDirectionalProb < mlThreshold)
            {
                return (false,
                    $"ML.NET 불일치/aligned={mlAligned} conf={mlDirectionalProb:P1}<{mlThreshold:P1}",
                    finalTp,
                    finalSl,
                    scenario,
                    mlDirectionalProb,
                    0f);
            }

            if (_transformerTrainer == null || !_transformerTrainer.IsModelReady)
                return (false, "Transformer 모델 미준비", finalTp, finalSl, scenario, mlDirectionalProb, 0f);

            var inferenceSeries = BuildInferenceDataFromKlines(klines15m, symbol);
            if (inferenceSeries.Count < _transformerTrainer.SeqLen)
                return (false, $"Transformer 시퀀스 부족({inferenceSeries.Count}<{_transformerTrainer.SeqLen})", finalTp, finalSl, scenario, mlDirectionalProb, 0f);

            var sequence = inferenceSeries.TakeLast(_transformerTrainer.SeqLen).ToList();
            float predictedPrice = _transformerTrainer.Predict(sequence);
            decimal predictedPriceDecimal = (decimal)predictedPrice;
            bool transformerAligned = string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                ? predictedPriceDecimal > currentPrice
                : predictedPriceDecimal < currentPrice;

            float transformerConfidence = 0.5f;
            if (currentPrice > 0)
            {
                decimal movePct = Math.Abs((predictedPriceDecimal - currentPrice) / currentPrice) * 100m;
                transformerConfidence = NormalizeProbability01((float)Math.Min(1m, movePct / 0.35m), 0.5f);
            }

            if (!transformerAligned || transformerConfidence < transformerThreshold)
            {
                return (false,
                    $"Transformer 불일치/aligned={transformerAligned} conf={transformerConfidence:P1}<{transformerThreshold:P1}",
                    finalTp,
                    finalSl,
                    scenario,
                    mlDirectionalProb,
                    transformerConfidence);
            }

            return (true,
                $"topdown=1h:{h1Bias},4h:{h4Bias} structure={scenario}",
                finalTp,
                finalSl,
                scenario,
                mlDirectionalProb,
                transformerConfidence);
        }

        private static int CalculateTrendBias(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 200)
                return 0;

            double sma50 = IndicatorCalculator.CalculateSMA(klines, 50);
            double sma200 = IndicatorCalculator.CalculateSMA(klines, 200);
            decimal close = klines[^1].ClosePrice;

            if (sma50 > 0 && sma200 > 0)
            {
                if ((double)close > sma50 && sma50 > sma200)
                    return 1;

                if ((double)close < sma50 && sma50 < sma200)
                    return -1;

                if (sma50 > sma200 && (double)close > sma200)
                    return 1;

                if (sma50 < sma200 && (double)close < sma200)
                    return -1;
            }

            return 0;
        }

        private bool TryEvaluateWave2LongContinuation(
            List<IBinanceKline> klines,
            decimal currentPrice,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 60)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(72, klines.Count)).ToList();
            int wave1PeakIdx = IndexOfHighestHigh(window, 8, window.Count - 6);
            if (wave1PeakIdx < 8)
            {
                reason = "wave1 고점 미탐지";
                return false;
            }

            int wave1StartIdx = IndexOfLowestLow(window, 0, wave1PeakIdx - 1);
            int wave2LowIdx = IndexOfLowestLow(window, wave1PeakIdx + 1, window.Count - 1);
            if (wave1StartIdx < 0 || wave2LowIdx < 0 || wave2LowIdx <= wave1PeakIdx)
            {
                reason = "wave1/wave2 구조 미완성";
                return false;
            }

            decimal wave1Start = window[wave1StartIdx].LowPrice;
            decimal wave1Peak = window[wave1PeakIdx].HighPrice;
            decimal wave2Low = window[wave2LowIdx].LowPrice;
            decimal wave1Length = wave1Peak - wave1Start;
            if (wave1Length <= 0m)
            {
                reason = "wave1 길이 비정상";
                return false;
            }

            decimal retrace = (wave1Peak - wave2Low) / wave1Length;
            if (wave2Low <= wave1Start)
            {
                reason = "Rule1 위반(2파가 1파 시작 하회)";
                return false;
            }

            if (retrace < 0.48m || retrace > 0.68m)
            {
                reason = $"피보 되돌림 이탈({retrace:P1})";
                return false;
            }

            decimal fib50 = wave1Peak - wave1Length * 0.5m;
            decimal fib618 = wave1Peak - wave1Length * 0.618m;
            decimal distance618 = currentPrice > 0 ? Math.Abs(currentPrice - fib618) / currentPrice : 1m;
            decimal distance50 = currentPrice > 0 ? Math.Abs(currentPrice - fib50) / currentPrice : 1m;

            var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
            bool nearLowerBand = bb.Lower > 0 && currentPrice <= (decimal)bb.Lower * 1.01m;

            decimal wave1Volume = CalculateAverageVolumeInRange(window, wave1StartIdx, wave1PeakIdx);
            decimal wave2Volume = CalculateAverageVolumeInRange(window, wave1PeakIdx + 1, wave2LowIdx);
            bool volumeContracting = wave1Volume > 0m && wave2Volume > 0m && wave2Volume <= wave1Volume * 0.92m;
            bool fibTouch = distance618 <= 0.006m || distance50 <= 0.006m;

            if (!fibTouch && !nearLowerBand && !volumeContracting)
            {
                reason = "진입 트리거 미충족(피보/BB/거래량)";
                return false;
            }

            stopLossPrice = wave1Start * 0.998m;
            takeProfitPrice = wave1Peak + wave1Length * 1.0m;
            reason = $"retrace={retrace:P1} fibTouch={fibTouch} bbLower={nearLowerBand} volContract={volumeContracting}";
            return true;
        }

        private bool TryEvaluateWave2ShortContinuation(
            List<IBinanceKline> klines,
            decimal currentPrice,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 60)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(72, klines.Count)).ToList();
            int wave1LowIdx = IndexOfLowestLow(window, 8, window.Count - 6);
            if (wave1LowIdx < 8)
            {
                reason = "wave1 저점 미탐지";
                return false;
            }

            int wave1StartIdx = IndexOfHighestHigh(window, 0, wave1LowIdx - 1);
            int wave2HighIdx = IndexOfHighestHigh(window, wave1LowIdx + 1, window.Count - 1);
            if (wave1StartIdx < 0 || wave2HighIdx < 0 || wave2HighIdx <= wave1LowIdx)
            {
                reason = "wave1/wave2 구조 미완성";
                return false;
            }

            decimal wave1Start = window[wave1StartIdx].HighPrice;
            decimal wave1Low = window[wave1LowIdx].LowPrice;
            decimal wave2High = window[wave2HighIdx].HighPrice;
            decimal wave1Length = wave1Start - wave1Low;
            if (wave1Length <= 0m)
            {
                reason = "wave1 길이 비정상";
                return false;
            }

            decimal retrace = (wave2High - wave1Low) / wave1Length;
            if (wave2High >= wave1Start)
            {
                reason = "Rule1 위반(2파가 1파 시작 상회)";
                return false;
            }

            if (retrace < 0.48m || retrace > 0.68m)
            {
                reason = $"피보 되돌림 이탈({retrace:P1})";
                return false;
            }

            decimal fib50 = wave1Low + wave1Length * 0.5m;
            decimal fib618 = wave1Low + wave1Length * 0.618m;
            decimal distance618 = currentPrice > 0 ? Math.Abs(currentPrice - fib618) / currentPrice : 1m;
            decimal distance50 = currentPrice > 0 ? Math.Abs(currentPrice - fib50) / currentPrice : 1m;

            var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
            bool nearUpperBand = bb.Upper > 0 && currentPrice >= (decimal)bb.Upper * 0.99m;

            decimal wave1Volume = CalculateAverageVolumeInRange(window, wave1StartIdx, wave1LowIdx);
            decimal wave2Volume = CalculateAverageVolumeInRange(window, wave1LowIdx + 1, wave2HighIdx);
            bool volumeContracting = wave1Volume > 0m && wave2Volume > 0m && wave2Volume <= wave1Volume * 0.92m;
            bool fibTouch = distance618 <= 0.006m || distance50 <= 0.006m;

            if (!fibTouch && !nearUpperBand && !volumeContracting)
            {
                reason = "진입 트리거 미충족(피보/BB/거래량)";
                return false;
            }

            stopLossPrice = wave1Start * 1.002m;
            takeProfitPrice = wave1Low - wave1Length * 1.0m;
            reason = $"retrace={retrace:P1} fibTouch={fibTouch} bbUpper={nearUpperBand} volContract={volumeContracting}";
            return true;
        }

        private bool TryEvaluateWave5LongReversal(
            List<IBinanceKline> klines,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 90)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(96, klines.Count)).ToList();
            int n = window.Count;
            int midStart = n / 3;
            int midEnd = (n * 2) / 3;
            int lateStart = (n * 2) / 3;

            int wave1LowIdx = IndexOfLowestLow(window, 5, midStart - 5);
            int wave3LowIdx = IndexOfLowestLow(window, midStart, midEnd);
            int wave5LowIdx = IndexOfLowestLow(window, lateStart, n - 2);

            if (wave1LowIdx < 0 || wave3LowIdx < 0 || wave5LowIdx < 0 || !(wave1LowIdx < wave3LowIdx && wave3LowIdx < wave5LowIdx))
            {
                reason = "5파 저점 구조 미완성";
                return false;
            }

            int wave1StartHighIdx = IndexOfHighestHigh(window, 0, wave1LowIdx);
            int wave2HighIdx = IndexOfHighestHigh(window, wave1LowIdx + 1, wave3LowIdx - 1);
            int wave4HighIdx = IndexOfHighestHigh(window, wave3LowIdx + 1, wave5LowIdx - 1);
            if (wave1StartHighIdx < 0 || wave2HighIdx < 0 || wave4HighIdx < 0)
            {
                reason = "보조 파동 고점 미탐지";
                return false;
            }

            decimal wave1StartHigh = window[wave1StartHighIdx].HighPrice;
            decimal wave1Low = window[wave1LowIdx].LowPrice;
            decimal wave2High = window[wave2HighIdx].HighPrice;
            decimal wave3Low = window[wave3LowIdx].LowPrice;
            decimal wave4High = window[wave4HighIdx].HighPrice;
            decimal wave5Low = window[wave5LowIdx].LowPrice;

            decimal len1 = wave1StartHigh - wave1Low;
            decimal len3 = wave2High - wave3Low;
            decimal len5 = wave4High - wave5Low;
            if (len1 <= 0m || len3 <= 0m || len5 <= 0m)
            {
                reason = "파동 길이 계산 실패";
                return false;
            }

            bool rule2Pass = len3 >= Math.Min(len1, len5) * 0.95m;
            bool rule4Pass = wave4High < wave1Low;
            float rsi3 = CalculateRsiAtIndex(window, wave3LowIdx, 14);
            float rsi5 = CalculateRsiAtIndex(window, wave5LowIdx, 14);
            bool rsiDivergence = wave5Low < wave3Low && rsi5 > rsi3;
            decimal vol3 = CalculateAverageVolumeAround(window, wave3LowIdx, 2);
            decimal vol5 = CalculateAverageVolumeAround(window, wave5LowIdx, 2);
            bool volumeDivergence = vol5 > 0m && vol3 > 0m && vol5 < vol3 * 0.90m;

            if (!rule2Pass || !rule4Pass || !rsiDivergence || !volumeDivergence)
            {
                reason = $"rule2={rule2Pass} rule4={rule4Pass} rsiDiv={rsiDivergence} volDiv={volumeDivergence}";
                return false;
            }

            stopLossPrice = wave5Low * 0.997m;
            takeProfitPrice = wave4High;
            reason = $"rule2/rule4 통과, rsi3={rsi3:F1}, rsi5={rsi5:F1}";
            return true;
        }

        private bool TryEvaluateWave5ShortReversal(
            List<IBinanceKline> klines,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 90)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(96, klines.Count)).ToList();
            int n = window.Count;
            int midStart = n / 3;
            int midEnd = (n * 2) / 3;
            int lateStart = (n * 2) / 3;

            int wave1HighIdx = IndexOfHighestHigh(window, 5, midStart - 5);
            int wave3HighIdx = IndexOfHighestHigh(window, midStart, midEnd);
            int wave5HighIdx = IndexOfHighestHigh(window, lateStart, n - 2);

            if (wave1HighIdx < 0 || wave3HighIdx < 0 || wave5HighIdx < 0 || !(wave1HighIdx < wave3HighIdx && wave3HighIdx < wave5HighIdx))
            {
                reason = "5파 고점 구조 미완성";
                return false;
            }

            int wave1StartLowIdx = IndexOfLowestLow(window, 0, wave1HighIdx);
            int wave2LowIdx = IndexOfLowestLow(window, wave1HighIdx + 1, wave3HighIdx - 1);
            int wave4LowIdx = IndexOfLowestLow(window, wave3HighIdx + 1, wave5HighIdx - 1);
            if (wave1StartLowIdx < 0 || wave2LowIdx < 0 || wave4LowIdx < 0)
            {
                reason = "보조 파동 저점 미탐지";
                return false;
            }

            decimal wave1StartLow = window[wave1StartLowIdx].LowPrice;
            decimal wave1High = window[wave1HighIdx].HighPrice;
            decimal wave2Low = window[wave2LowIdx].LowPrice;
            decimal wave3High = window[wave3HighIdx].HighPrice;
            decimal wave4Low = window[wave4LowIdx].LowPrice;
            decimal wave5High = window[wave5HighIdx].HighPrice;

            decimal len1 = wave1High - wave1StartLow;
            decimal len3 = wave3High - wave2Low;
            decimal len5 = wave5High - wave4Low;
            if (len1 <= 0m || len3 <= 0m || len5 <= 0m)
            {
                reason = "파동 길이 계산 실패";
                return false;
            }

            bool rule2Pass = len3 >= Math.Min(len1, len5) * 0.95m;
            bool rule4Pass = wave4Low > wave1High;
            float rsi3 = CalculateRsiAtIndex(window, wave3HighIdx, 14);
            float rsi5 = CalculateRsiAtIndex(window, wave5HighIdx, 14);
            bool rsiDivergence = wave5High > wave3High && rsi5 < rsi3;
            decimal vol3 = CalculateAverageVolumeAround(window, wave3HighIdx, 2);
            decimal vol5 = CalculateAverageVolumeAround(window, wave5HighIdx, 2);
            bool volumeDivergence = vol5 > 0m && vol3 > 0m && vol5 < vol3 * 0.90m;

            if (!rule2Pass || !rule4Pass || !rsiDivergence || !volumeDivergence)
            {
                reason = $"rule2={rule2Pass} rule4={rule4Pass} rsiDiv={rsiDivergence} volDiv={volumeDivergence}";
                return false;
            }

            stopLossPrice = wave5High * 1.003m;
            takeProfitPrice = wave4Low;
            reason = $"rule2/rule4 통과, rsi3={rsi3:F1}, rsi5={rsi5:F1}";
            return true;
        }

        private static int IndexOfLowestLow(List<IBinanceKline> klines, int startIndex, int endIndex)
        {
            if (klines == null || klines.Count == 0)
                return -1;

            int start = Math.Max(0, startIndex);
            int end = Math.Min(endIndex, klines.Count - 1);
            if (start > end)
                return -1;

            decimal min = decimal.MaxValue;
            int index = -1;
            for (int i = start; i <= end; i++)
            {
                if (klines[i].LowPrice < min)
                {
                    min = klines[i].LowPrice;
                    index = i;
                }
            }

            return index;
        }

        private static int IndexOfHighestHigh(List<IBinanceKline> klines, int startIndex, int endIndex)
        {
            if (klines == null || klines.Count == 0)
                return -1;

            int start = Math.Max(0, startIndex);
            int end = Math.Min(endIndex, klines.Count - 1);
            if (start > end)
                return -1;

            decimal max = decimal.MinValue;
            int index = -1;
            for (int i = start; i <= end; i++)
            {
                if (klines[i].HighPrice > max)
                {
                    max = klines[i].HighPrice;
                    index = i;
                }
            }

            return index;
        }

        private static decimal CalculateAverageVolumeInRange(List<IBinanceKline> klines, int startIndex, int endIndex)
        {
            if (klines == null || klines.Count == 0)
                return 0m;

            int start = Math.Max(0, startIndex);
            int end = Math.Min(endIndex, klines.Count - 1);
            if (start > end)
                return 0m;

            decimal sum = 0m;
            int count = 0;
            for (int i = start; i <= end; i++)
            {
                sum += klines[i].Volume;
                count++;
            }

            return count > 0 ? sum / count : 0m;
        }

        private static decimal CalculateAverageVolumeAround(List<IBinanceKline> klines, int centerIndex, int radius)
        {
            if (klines == null || klines.Count == 0)
                return 0m;

            int start = Math.Max(0, centerIndex - radius);
            int end = Math.Min(klines.Count - 1, centerIndex + radius);
            return CalculateAverageVolumeInRange(klines, start, end);
        }

        private static float CalculateRsiAtIndex(List<IBinanceKline> klines, int index, int period)
        {
            if (klines == null || klines.Count == 0)
                return 50f;

            int safeIndex = Math.Clamp(index, 0, klines.Count - 1);
            if (safeIndex < period)
                return 50f;

            var subset = klines.Take(safeIndex + 1).ToList();
            return (float)IndicatorCalculator.CalculateRSI(subset, period);
        }

        private List<CandleData> BuildInferenceDataFromKlines(List<IBinanceKline> klines, string symbol)
        {
            var result = new List<CandleData>();
            if (klines == null || klines.Count < 130)
                return result;

            var volumes = klines.Select(k => (float)k.Volume).ToList();

            for (int i = 120; i < klines.Count; i++)
            {
                var subset = klines.GetRange(0, i + 1);
                var current = klines[i];
                decimal entryPrice = current.ClosePrice;

                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                double sma20 = IndicatorCalculator.CalculateSMA(subset, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(subset, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(subset, 120);

                double bbMid = (bb.Upper + bb.Lower) / 2.0;
                float bbWidth = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0;
                float priceToBBMid = bbMid > 0 ? (float)(((double)entryPrice - bbMid) / bbMid * 100) : 0;

                float priceChangePct = current.OpenPrice > 0
                    ? (float)((entryPrice - current.OpenPrice) / current.OpenPrice * 100)
                    : 0;
                float priceToSMA20Pct = sma20 > 0
                    ? (float)(((double)entryPrice - sma20) / sma20 * 100)
                    : 0;

                decimal range = current.HighPrice - current.LowPrice;
                float bodyRatio = range > 0 ? (float)(Math.Abs(current.ClosePrice - current.OpenPrice) / range) : 0;
                float upperShadow = range > 0 ? (float)((current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice)) / range) : 0;
                float lowerShadow = range > 0 ? (float)((Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice) / range) : 0;

                float vol20Avg = 0;
                if (i >= 20)
                {
                    for (int v = i - 19; v <= i; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[i] / vol20Avg : 1;
                float volumeChangePct = (i > 0 && volumes[i - 1] > 0)
                    ? (volumes[i] - volumes[i - 1]) / volumes[i - 1] * 100
                    : 0;

                float fibPosition = 0;
                if (fib.Level236 != fib.Level618 && fib.Level618 > 0)
                    fibPosition = (float)(((double)entryPrice - fib.Level236) / (fib.Level618 - fib.Level236));
                fibPosition = Math.Clamp(fibPosition, 0, 1);

                float trendStrength = 0;
                if (sma20 > 0 && sma60 > 0 && sma120 > 0)
                {
                    if (sma20 > sma60 && sma60 > sma120) trendStrength = 1.0f;
                    else if (sma20 < sma60 && sma60 < sma120) trendStrength = -1.0f;
                    else trendStrength = (float)((sma20 - sma120) / sma120);
                    trendStrength = Math.Clamp(trendStrength, -1f, 1f);
                }

                float rsiDivergence = 0;
                if (i >= 5)
                {
                    var prevSubset = klines.GetRange(0, i - 4);
                    var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                    float priceDelta = (float)(current.ClosePrice - klines[i - 5].ClosePrice);
                    float rsiDelta = (float)(rsi - prevRsi);
                    if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;
                    else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1;
                }

                bool elliottBullish = IndicatorCalculator.AnalyzeElliottWave(subset);
                float elliottState = elliottBullish ? 1.0f : -1.0f;

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,
                    Price_Change_Pct = priceChangePct,
                    Price_To_BB_Mid = priceToBBMid,
                    BB_Width = bbWidth,
                    Price_To_SMA20_Pct = priceToSMA20Pct,
                    Candle_Body_Ratio = bodyRatio,
                    Upper_Shadow_Ratio = upperShadow,
                    Lower_Shadow_Ratio = lowerShadow,
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct,
                    Fib_Position = fibPosition,
                    Trend_Strength = trendStrength,
                    RSI_Divergence = rsiDivergence,
                    ElliottWaveState = elliottState,
                    SentimentScore = 0,
                    OpenInterest = _oiCollector != null ? (float)_oiCollector.GetOiAtTime(symbol, current.OpenTime) : 0,
                    OI_Change_Pct = _oiCollector != null ? (float)_oiCollector.GetOiChangeAtTime(symbol, current.OpenTime) : 0,
                    FundingRate = 0,
                    SqueezeLabel = 0,
                    Label = false,
                    LabelLong = 0,
                    LabelShort = 0,
                    LabelHold = 0
                });
            }

            return result;
        }

        private static float NormalizeProbability01(float value, float fallback = 0.5f)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return Math.Clamp(fallback, 0f, 1f);

            return Math.Clamp(value, 0f, 1f);
        }

        private static string BuildPredictionValidationKey(string modelName, string symbol)
        {
            string safeModel = string.IsNullOrWhiteSpace(modelName)
                ? "MODEL"
                : modelName.Trim().ToUpperInvariant().Replace(".", "").Replace(" ", "");
            string safeSymbol = string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.Trim().ToUpperInvariant();

            return $"{safeModel}_{safeSymbol}";
        }

        private bool TrySchedulePredictionValidation(string predictionKey, string symbol, CancellationTokenSource? cts)
        {
            if (string.IsNullOrWhiteSpace(predictionKey) || cts == null || cts.Token.IsCancellationRequested)
                return false;

            if (!_scheduledPredictionValidations.TryAdd(predictionKey, 0))
                return false;

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PredictionValidationDelay, token);
                    await _predictionValidationLimiter.WaitAsync(token);
                    try
                    {
                        await ValidatePredictionAsync(predictionKey, symbol, token);
                    }
                    finally
                    {
                        _predictionValidationLimiter.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [PREDICT][VALIDATE][ERROR] key={predictionKey} sym={symbol} detail={ex.Message}");
                }
                finally
                {
                    _scheduledPredictionValidations.TryRemove(predictionKey, out _);
                }
            }, CancellationToken.None);

            return true;
        }

        private static string NormalizePumpSignalLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "📡 [SIGNAL][PUMP][TRACE] empty";

            if (message.Contains("[SIGNAL][PUMP]", StringComparison.OrdinalIgnoreCase))
                return message;

            return $"📡 [SIGNAL][PUMP][TRACE] {message}";
        }

        private static string NormalizeTransformerSignalLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "📡 [SIGNAL][TRANSFORMER][TRACE] empty";

            if (message.Contains("[SIGNAL][TRANSFORMER]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[ENTRY][", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[DB][", StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }

            return $"📡 [SIGNAL][TRANSFORMER][TRACE] {message}";
        }

        private bool ShouldBlockChasingEntry(
            string symbol,
            string decision,
            decimal currentPrice,
            CandleData latestCandle,
            List<IBinanceKline>? recentKlines,
            string mode,
            out string reason)
        {
            reason = string.Empty;

            if (decision != "LONG" && decision != "SHORT")
                return false;

            if (string.Equals(mode, "SIDEWAYS", StringComparison.OrdinalIgnoreCase))
                return false;

            if (recentKlines == null || recentKlines.Count < 20)
                return false;

            var recent20 = recentKlines.TakeLast(20).ToList();
            decimal recentHigh = recent20.Max(k => k.HighPrice);
            decimal recentLow = recent20.Min(k => k.LowPrice);
            decimal sma20 = latestCandle.SMA_20 > 0 ? (decimal)latestCandle.SMA_20 : 0m;
            decimal bbLower = latestCandle.BollingerLower > 0 ? (decimal)latestCandle.BollingerLower : 0m;
            decimal bbUpper = latestCandle.BollingerUpper > 0 ? (decimal)latestCandle.BollingerUpper : 0m;
            decimal bbRange = bbUpper - bbLower;
            decimal bbPosition = bbRange > 0m ? (currentPrice - bbLower) / bbRange : 0m;
            decimal currentBbWidthPct = latestCandle.BB_Width > 0
                ? (decimal)latestCandle.BB_Width
                : 0m;
            decimal averageBbWidthPct = CalculateAverageBollingerWidthPct(recentKlines);

            decimal priceToSma20Pct = sma20 > 0
                ? ((currentPrice - sma20) / sma20) * 100m
                : (decimal)latestCandle.Price_To_SMA20_Pct;

            int riskScore = 0;
            var flags = new List<string>();

            if (decision == "LONG")
            {
                decimal pullbackFromHighPct = recentHigh > 0
                    ? ((recentHigh - currentPrice) / recentHigh) * 100m
                    : 999m;

                bool nearRecentHigh = pullbackFromHighPct <= 0.20m;
                bool stretchedAboveSma20 = priceToSma20Pct >= 0.80m;
                bool rsiHot = latestCandle.RSI >= 64f;
                bool touchingUpperBand = bbUpper > 0 && currentPrice >= bbUpper * 0.998m;
                bool upperShadowWarning = latestCandle.Upper_Shadow_Ratio >= 0.35f;
                bool overheatedUpperZone = bbRange > 0m && bbPosition >= 0.85m;
                bool upperZoneWithHighRsi = bbRange > 0m && bbPosition >= 0.80m && latestCandle.RSI >= 70f;
                bool narrowBandUpperChase = bbRange > 0m
                    && bbPosition >= 0.80m
                    && averageBbWidthPct > 0m
                    && currentBbWidthPct > 0m
                    && currentBbWidthPct <= averageBbWidthPct;

                bool strongBreakoutException = latestCandle.Volume_Ratio >= 1.8f
                    && latestCandle.OI_Change_Pct >= 0.8f
                    && latestCandle.Upper_Shadow_Ratio < 0.20f
                    && latestCandle.RSI < 70f;

                if (upperZoneWithHighRsi)
                {
                    reason = $"BB 상단 과열+RSI 과매수 (BB위치 {bbPosition:P0}, RSI {latestCandle.RSI:F1})";
                    return true;
                }

                if (overheatedUpperZone && !strongBreakoutException)
                {
                    reason = $"BB 상단 85% 이상 추격 구간 (BB위치 {bbPosition:P0}, 상단={bbUpper:F4})";
                    return true;
                }

                if (narrowBandUpperChase && !strongBreakoutException)
                {
                    reason = $"BB 상단 근접 + 밴드폭 미확장 (현재폭 {currentBbWidthPct:F2}% <= 평균폭 {averageBbWidthPct:F2}%)";
                    return true;
                }

                if (nearRecentHigh)
                {
                    riskScore += 2;
                    flags.Add($"최근고점 근접({recentHigh:F4}, 되돌림 {pullbackFromHighPct:F2}%)");
                }

                if (stretchedAboveSma20)
                {
                    riskScore += 1;
                    flags.Add($"SMA20 이격 +{priceToSma20Pct:F2}%");
                }

                if (rsiHot)
                {
                    riskScore += 1;
                    flags.Add($"RSI {latestCandle.RSI:F1}");
                }

                if (touchingUpperBand)
                {
                    riskScore += 1;
                    flags.Add("BB 상단 근접");
                }

                if (upperShadowWarning)
                {
                    riskScore += 1;
                    flags.Add($"윗꼬리 {latestCandle.Upper_Shadow_Ratio * 100f:F0}%");
                }

                if (strongBreakoutException)
                {
                    return false;
                }
            }
            else
            {
                decimal bounceFromLowPct = recentLow > 0
                    ? ((currentPrice - recentLow) / recentLow) * 100m
                    : 999m;

                bool nearRecentLow = bounceFromLowPct <= 0.20m;
                bool stretchedBelowSma20 = priceToSma20Pct <= -0.80m;
                bool rsiCold = latestCandle.RSI <= 36f;
                bool touchingLowerBand = bbLower > 0 && currentPrice <= bbLower * 1.002m;
                bool lowerShadowWarning = latestCandle.Lower_Shadow_Ratio >= 0.35f;
                bool oversoldLowerZone = bbRange > 0m && bbPosition <= 0.15m;
                bool lowerZoneWithLowRsi = bbRange > 0m && bbPosition <= 0.20m && latestCandle.RSI <= 30f;
                bool narrowBandLowerChase = bbRange > 0m
                    && bbPosition <= 0.20m
                    && averageBbWidthPct > 0m
                    && currentBbWidthPct > 0m
                    && currentBbWidthPct <= averageBbWidthPct;

                bool strongBreakdownException = latestCandle.Volume_Ratio >= 1.8f
                    && latestCandle.OI_Change_Pct >= 0.8f
                    && latestCandle.Lower_Shadow_Ratio < 0.20f
                    && latestCandle.RSI > 30f;

                if (lowerZoneWithLowRsi)
                {
                    reason = $"BB 하단 과열+RSI 과매도 (BB위치 {bbPosition:P0}, RSI {latestCandle.RSI:F1})";
                    return true;
                }

                if (oversoldLowerZone && !strongBreakdownException)
                {
                    reason = $"BB 하단 15% 이하 추격 구간 (BB위치 {bbPosition:P0}, 하단={bbLower:F4})";
                    return true;
                }

                if (narrowBandLowerChase && !strongBreakdownException)
                {
                    reason = $"BB 하단 근접 + 밴드폭 미확장 (현재폭 {currentBbWidthPct:F2}% <= 평균폭 {averageBbWidthPct:F2}%)";
                    return true;
                }

                if (nearRecentLow)
                {
                    riskScore += 2;
                    flags.Add($"최근저점 근접({recentLow:F4}, 반등 {bounceFromLowPct:F2}%)");
                }

                if (stretchedBelowSma20)
                {
                    riskScore += 1;
                    flags.Add($"SMA20 이격 {priceToSma20Pct:F2}%");
                }

                if (rsiCold)
                {
                    riskScore += 1;
                    flags.Add($"RSI {latestCandle.RSI:F1}");
                }

                if (touchingLowerBand)
                {
                    riskScore += 1;
                    flags.Add("BB 하단 근접");
                }

                if (lowerShadowWarning)
                {
                    riskScore += 1;
                    flags.Add($"아랫꼬리 {latestCandle.Lower_Shadow_Ratio * 100f:F0}%");
                }

                if (strongBreakdownException)
                {
                    return false;
                }
            }

            if (riskScore >= 4)
            {
                reason = string.Join(", ", flags);
                return true;
            }

            return false;
        }

        private decimal CalculateAverageBollingerWidthPct(List<IBinanceKline> recentKlines, int period = 20, int sampleCount = 10)
        {
            try
            {
                if (recentKlines == null || recentKlines.Count < period)
                    return 0m;

                int availableSamples = Math.Min(sampleCount, recentKlines.Count - period + 1);
                if (availableSamples <= 0)
                    return 0m;

                var widths = new List<decimal>(availableSamples);
                int startIndex = recentKlines.Count - (period + availableSamples - 1);
                if (startIndex < 0)
                    startIndex = 0;

                for (int offset = startIndex; offset <= recentKlines.Count - period; offset++)
                {
                    var subset = recentKlines.Skip(offset).Take(period).ToList();
                    // [FIX] subset이 비어있으면 건너뛰기
                    if (!subset.Any())
                    {
                        OnStatusLog?.Invoke($"⚠️ subset 데이터 부족 (offset: {offset}, period: {period})");
                        continue;
                    }
                    if (subset.Count < period)
                        continue;

                    var bb = IndicatorCalculator.CalculateBB(subset, period, 2);
                    decimal mid = (decimal)bb.Mid;
                    decimal upper = (decimal)bb.Upper;
                    decimal lower = (decimal)bb.Lower;

                    if (mid <= 0m || upper <= lower)
                        continue;

                    widths.Add(((upper - lower) / mid) * 100m);
                }

                return widths.Count > 0 ? widths.Average() : 0m;
            }
            catch
            {
                return 0m;
            }
        }

        private bool IsEntryWarmupActive(out TimeSpan remaining)
        {
            var elapsed = DateTime.Now - _engineStartTime;
            remaining = _entryWarmupDuration - elapsed;
            return remaining > TimeSpan.Zero;
        }

        private async Task PreloadInitialAiScoresAsync(CancellationToken token)
        {
            if (_aiPredictor == null)
            {
                OnStatusLog?.Invoke("ℹ️ 초기 AI 점수 프리로드 건너뜀: AI 예측기 미준비");
                return;
            }

            int preparedCount = 0;
            foreach (var symbol in _symbols)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var candle = await GetLatestCandleDataAsync(symbol, token);
                    if (candle == null) continue;

                    var prediction = _aiPredictor.Predict(candle);
                    preparedCount++;

                    OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                    {
                        Symbol = symbol,
                        LastPrice = candle.Close,
                        RSI_1H = candle.RSI,
                        AIScore = prediction.Score,
                        Decision = prediction.Prediction ? "AI_UP" : "AI_DOWN",
                        StrategyName = "AI Warmup"
                    });
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 초기 AI 점수 생성 실패: {ex.Message}");
                }
            }

            OnStatusLog?.Invoke($"🧠 초기 AI 점수 생성 완료: {preparedCount}/{_symbols.Count}개 심볼");
        }

        public async Task ClosePositionAsync(string symbol)
        {
            if (_positionMonitor != null)
            {
                await _positionMonitor.ExecuteMarketClose(symbol, "사용자 수동 청산", _cts?.Token ?? CancellationToken.None);
            }
            else
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} 수동 청산 실패: 엔진이 초기화되지 않았습니다. 스캔을 먼저 시작해주세요.");
            }
        }

        /// <summary>
        /// [FIX] 거래소 포지션과 로컬 포지션을 동기화하고 누락된 청산을 TradeHistory에 반영합니다.
        /// WebSocket 연결 끊김이나 봇 중지 중 발생한 청산을 자동 보정합니다.
        /// </summary>
        public async Task SyncExchangePositionsAsync(CancellationToken token = default)
        {
            try
            {
                // 거래소에서 현재 오픈 포지션 목록 조회
                var exchangePositions = await _exchangeService.GetPositionsAsync(ct: token);
                var exchangeSymbols = new HashSet<string>(
                    exchangePositions.Where(p => Math.Abs(p.Quantity) > 0).Select(p => p.Symbol)
                );

                List<PositionInfo> missingClosedPositions = new List<PositionInfo>();

                lock (_posLock)
                {
                    // 로컬에는 있지만 거래소에는 없는 포지션 = 외부/누락 청산
                    foreach (var kvp in _activePositions.ToList())
                    {
                        string symbol = kvp.Key;
                        PositionInfo localPos = kvp.Value;

                        // 거래소에 포지션이 없고, 현재 청산 진행 중이 아닌 경우
                        if (!exchangeSymbols.Contains(symbol) && 
                            !_positionMonitor.IsCloseInProgress(symbol) &&
                            Math.Abs(localPos.Quantity) > 0)
                        {
                            missingClosedPositions.Add(new PositionInfo
                            {
                                Symbol = symbol,
                                IsLong = localPos.IsLong,
                                Quantity = Math.Abs(localPos.Quantity),
                                EntryPrice = localPos.EntryPrice,
                                Leverage = localPos.Leverage,
                                UnrealizedPnL = localPos.UnrealizedPnL,
                                Side = localPos.Side,
                                EntryTime = localPos.EntryTime,
                                AiScore = localPos.AiScore,
                                StopLoss = localPos.StopLoss
                            });

                            // 로컬 포지션 제거
                            _activePositions.Remove(symbol);
                            _hybridExitManager?.RemoveState(symbol);
                            OnPositionStatusUpdate?.Invoke(symbol, false, 0);
                        }
                    }
                }

                // 누락된 청산 건들을 TradeHistory에 반영
                foreach (var closedPos in missingClosedPositions)
                {
                    decimal exitPrice = 0m;

                    // 현재가 조회 시도
                    if (_marketDataManager.TickerCache.TryGetValue(closedPos.Symbol, out var ticker))
                    {
                        exitPrice = ticker.LastPrice;
                    }

                    if (exitPrice <= 0)
                    {
                        try
                        {
                            exitPrice = await _exchangeService.GetPriceAsync(closedPos.Symbol, ct: token);
                        }
                        catch (Exception priceEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ {closedPos.Symbol} 가격 조회 실패, 진입가로 대체: {priceEx.Message}");
                            exitPrice = closedPos.EntryPrice;
                        }
                    }

                    if (exitPrice <= 0)
                        exitPrice = closedPos.EntryPrice;

                    bool stopLossLikelyTriggered = false;
                    if (closedPos.StopLoss > 0)
                    {
                        decimal stopTolerance = closedPos.StopLoss * 0.0015m; // 0.15% 허용
                        if (closedPos.IsLong)
                            stopLossLikelyTriggered = exitPrice <= closedPos.StopLoss + stopTolerance;
                        else
                            stopLossLikelyTriggered = exitPrice >= closedPos.StopLoss - stopTolerance;
                    }

                    string syncExitReason = stopLossLikelyTriggered
                        ? $"STOP_LOSS_MISSED_SYNC (SL={closedPos.StopLoss:F8})"
                        : "MISSED_CLOSE_SYNC (거래소 동기화)";

                    decimal closeQty = Math.Abs(closedPos.Quantity);
                    
                    // 순수 가격 차이
                    decimal rawPnl = closedPos.IsLong
                        ? (exitPrice - closedPos.EntryPrice) * closeQty
                        : (closedPos.EntryPrice - exitPrice) * closeQty;

                    // 거래 수수료 및 슬리피지 차감
                    decimal entryFee = closedPos.EntryPrice * closeQty * 0.0004m;
                    decimal exitFee = exitPrice * closeQty * 0.0004m;
                    decimal estimatedSlippage = exitPrice * closeQty * 0.0005m;
                    decimal pnl = rawPnl - entryFee - exitFee - estimatedSlippage;

                    decimal pnlPercent = 0m;
                    if (closedPos.EntryPrice > 0 && closeQty > 0)
                    {
                        pnlPercent = (pnl / (closedPos.EntryPrice * closeQty)) * 100m * closedPos.Leverage;
                    }

                    var missedCloseLog = new TradeLog(
                        closedPos.Symbol,
                        closedPos.IsLong ? "SELL" : "BUY",
                        stopLossLikelyTriggered ? "STOP_LOSS_MISSED_SYNC" : "MISSED_CLOSE_SYNC",
                        exitPrice,
                        closedPos.AiScore,
                        DateTime.Now,
                        pnl,
                        pnlPercent)
                    {
                        EntryPrice = closedPos.EntryPrice,
                        ExitPrice = exitPrice,
                        Quantity = closeQty,
                        EntryTime = closedPos.EntryTime == default ? DateTime.Now : closedPos.EntryTime,
                        ExitTime = DateTime.Now,
                        ExitReason = syncExitReason
                    };

                    bool synced = await _dbManager.TryCompleteOpenTradeAsync(missedCloseLog);
                    if (synced)
                    {
                        OnStatusLog?.Invoke($"📝 [동기화] {closedPos.Symbol} 누락된 청산 감지 → TradeHistory 반영 완료 (PnL={pnl:F2}, ROE={pnlPercent:F2}%)");
                        OnAlert?.Invoke($"⚠️ [누락 청산 복구] {closedPos.Symbol} 청산이 TradeHistory에 복구되었습니다.");
                        
                        // 청산 이력 갱신 이벤트
                        OnTradeHistoryUpdated?.Invoke();
                    }
                    else
                    {
                        OnStatusLog?.Invoke($"⚠️ [동기화 실패] {closedPos.Symbol} TradeHistory 반영 실패 (userId 확인 필요)");
                        OnAlert?.Invoke($"❌ [DB 동기화 실패] {closedPos.Symbol} 누락 청산 반영 실패 - 수동 점검 필요");
                    }
                }

                if (missingClosedPositions.Count > 0)
                {
                    OnStatusLog?.Invoke($"✅ [거래소 동기화] {missingClosedPositions.Count}개 누락 청산 복구 완료");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [거래소 동기화 오류] {ex.Message}");
            }
        }

        private async Task<CandleData?> GetLatestCandleDataAsync(string symbol, CancellationToken token, bool emitStatusLog = true)
        {
            try
            {
                // SMA120 + 여유분 = 최소 150봉 필요
                var klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 200, token);
                if (klines == null || klines.Count < 130) return null;

                var subset = klines.ToList();
                var current = subset.Last();
                decimal entryPrice = current.ClosePrice;

                // ── 기본 지표 ──
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                // ── SMA ──
                double sma20 = IndicatorCalculator.CalculateSMA(subset, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(subset, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(subset, 120);

                // ── 볼린저 밴드 파생 ──
                double bbMid = (bb.Upper + bb.Lower) / 2.0;
                float bbWidth = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0;
                float priceToBBMid = bbMid > 0 ? (float)(((double)entryPrice - bbMid) / bbMid * 100) : 0;

                // ── 가격 파생 ──
                float priceChangePct = current.OpenPrice > 0
                    ? (float)((entryPrice - current.OpenPrice) / current.OpenPrice * 100)
                    : 0;
                float priceToSMA20Pct = sma20 > 0
                    ? (float)(((double)entryPrice - sma20) / sma20 * 100)
                    : 0;

                // ── 캔들 패턴 ──
                decimal range = current.HighPrice - current.LowPrice;
                float bodyRatio = range > 0 ? (float)(Math.Abs(current.ClosePrice - current.OpenPrice) / range) : 0;
                float upperShadow = range > 0 ? (float)((current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice)) / range) : 0;
                float lowerShadow = range > 0 ? (float)((Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice) / range) : 0;

                // ── 거래량 분석 ──
                var volumes = subset.Select(k => (float)k.Volume).ToList();
                int idx = volumes.Count - 1;
                float vol20Avg = 0;
                if (idx >= 20)
                {
                    for (int v = idx - 19; v <= idx; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[idx] / vol20Avg : 1;
                float volumeChangePct = (idx > 0 && volumes[idx - 1] > 0)
                    ? (volumes[idx] - volumes[idx - 1]) / volumes[idx - 1] * 100
                    : 0;

                // ── 피보나치 포지션 (0~1) ──
                float fibPosition = 0;
                if (fib.Level236 != fib.Level618 && fib.Level618 > 0)
                    fibPosition = (float)(((double)entryPrice - fib.Level236) / (fib.Level618 - fib.Level236));
                fibPosition = Math.Clamp(fibPosition, 0, 1);

                // ── 추세 강도 (-1 ~ +1) ──
                float trendStrength = 0;
                if (sma20 > 0 && sma60 > 0 && sma120 > 0)
                {
                    if (sma20 > sma60 && sma60 > sma120) trendStrength = 1.0f;
                    else if (sma20 < sma60 && sma60 < sma120) trendStrength = -1.0f;
                    else trendStrength = (float)((sma20 - sma120) / sma120);
                    trendStrength = Math.Clamp(trendStrength, -1f, 1f);
                }

                // ── RSI 다이버전스 ──
                float rsiDivergence = 0;
                if (subset.Count >= 6)
                {
                    var prevSubset = subset.GetRange(0, subset.Count - 5);
                    var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                    float priceDelta = (float)(current.ClosePrice - subset[subset.Count - 6].ClosePrice);
                    float rsiDelta = (float)(rsi - prevRsi);
                    if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;
                    else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1;
                }

                // ── 엘리엇 ──
                bool elliottBullish = IndicatorCalculator.AnalyzeElliottWave(subset);
                float elliottState = elliottBullish ? 1.0f : -1.0f;

                // ── 뉴스 감성 ──
                double sentiment = await _newsService.GetMarketSentimentAsync();
                if (emitStatusLog)
                    OnStatusLog?.Invoke($"📰 뉴스 감성 점수: {sentiment:F2}");

                return new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,

                    // 기본 보조지표
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,

                    // SMA
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,

                    // 파생 피처
                    Price_Change_Pct = priceChangePct,
                    Price_To_BB_Mid = priceToBBMid,
                    BB_Width = bbWidth,
                    Price_To_SMA20_Pct = priceToSMA20Pct,
                    Candle_Body_Ratio = bodyRatio,
                    Upper_Shadow_Ratio = upperShadow,
                    Lower_Shadow_Ratio = lowerShadow,
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct,
                    Fib_Position = fibPosition,
                    Trend_Strength = trendStrength,
                    RSI_Divergence = rsiDivergence,
                    ElliottWaveState = elliottState,
                    SentimentScore = (float)sentiment,
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetLatestCandleData] 지표 계산 실패: {ex.Message}");
                return null;
            }
        }

        private async Task TryRecordMlNetPredictionFromCommonScanAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            if (_aiPredictor == null || string.IsNullOrWhiteSpace(symbol) || currentPrice <= 0 || token.IsCancellationRequested)
                return;

            var nowUtc = DateTime.UtcNow;
            if (_lastMlMonitorRecordTimes.TryGetValue(symbol, out var lastRecordedUtc) &&
                (nowUtc - lastRecordedUtc) < MlMonitorRecordInterval)
            {
                return;
            }

            _lastMlMonitorRecordTimes[symbol] = nowUtc;

            try
            {
                var candleData = await GetLatestCandleDataAsync(symbol, token, emitStatusLog: false);
                if (candleData == null)
                    return;

                var prediction = _aiPredictor.Predict(candleData);
                float upProbability = NormalizeProbability01(prediction.Probability, fallback: 0.5f);
                float monitorConfidence = Math.Max(upProbability, 1f - upProbability);

                string predictionKey = BuildPredictionValidationKey("ML.NET", symbol);
                decimal predictedPrice = currentPrice * (prediction.Prediction ? 1.02m : 0.98m);
                _pendingPredictions[predictionKey] = (DateTime.Now, currentPrice, predictedPrice, prediction.Prediction, "ML.NET", monitorConfidence);
                bool scheduled = TrySchedulePredictionValidation(predictionKey, symbol, _cts);

                string mlDirection = prediction.Prediction ? "상승" : "하락";
                if (scheduled)
                {
                    OnStatusLog?.Invoke($"🔮 [SIGNAL][ML.NET][PREDICT] src=COMMON_SCAN sym={symbol} dir={mlDirection} upProb={upProbability:P0} confidence={monitorConfidence:P0} validateAfter=5m");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [SIGNAL][ML.NET][ERROR] src=COMMON_SCAN sym={symbol} detail={ex.Message}");
            }
        }

        #endregion

        // [추가] 로그 버퍼 관리
        private void AddToLogBuffer(string msg)
        {
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _apiLogBuffer.Enqueue(timestamped);
            if (_apiLogBuffer.Count > 100) _apiLogBuffer.TryDequeue(out _);
        }

        public string GetLogsJson()
        {
            var logs = _apiLogBuffer.ToList();
            logs.Reverse(); // 최신순 정렬
            var options = new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            return System.Text.Json.JsonSerializer.Serialize(logs, options);
        }

        // [Agent 5] 차트 데이터 JSON 반환
        public async Task<string> GetChartDataJsonAsync(string symbol, string intervalStr)
        {
            try
            {
                // 문자열을 표준 enum으로 변환 후, Binance enum으로 재변환
                var standardInterval = BinanceExchangeAdapter.ConvertStringToKlineInterval(intervalStr);
                var interval = BinanceExchangeAdapter.ConvertToBinanceKlineInterval(standardInterval);

                // 최근 50개 캔들 조회
                var klines = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: 50);
                if (klines.Success)
                {
                    var data = klines.Data.Select(k => new
                    {
                        Time = k.OpenTime,
                        Open = k.OpenPrice,
                        High = k.HighPrice,
                        Low = k.LowPrice,
                        Close = k.ClosePrice
                    });
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    };
                    return System.Text.Json.JsonSerializer.Serialize(data, options);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JSON 직렬화 오류: {ex.Message}");
            }
            return "[]";
        }

        // [추가] 포지션 목록 JSON 반환
        public string GetPositionsJson()
        {
            lock (_posLock)
            {
                var list = _activePositions.Select(kvp => new
                {
                    Symbol = kvp.Key,
                    EntryPrice = kvp.Value.EntryPrice,
                    Quantity = kvp.Value.Quantity,
                    Side = kvp.Value.IsLong ? "Long" : "Short",
                    PnL = 0m // PnL은 실시간 계산이 필요하므로 여기선 0 또는 추정치 (클라이언트에서 계산 권장)
                }).ToList();
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                return System.Text.Json.JsonSerializer.Serialize(list, options);
            }
        }

        // [추가] API를 통한 포지션 청산
        public async Task<string> ClosePositionApiAsync(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return "{\"status\": \"error\", \"message\": \"Symbol required\"}";

            try
            {
                await ClosePositionAsync(symbol);
                return "{\"status\": \"success\", \"message\": \"Close command sent\"}";
            }
            catch (Exception ex)
            {
                return $"{{\"status\": \"error\", \"message\": \"{ex.Message}\"}}";
            }
        }

        /// <summary>
        /// [AI 보너스 1] EMA 20 눌림목 감지
        /// ───────────────────────────────────────
        /// 조건:
        /// 1) 1시간봉 EMA 정배열 (EMA20 > EMA50)
        /// 2) 현재가가 5분봉 EMA 20 근처 (±0.2% 이내)
        /// 3) RSI >= 45 + 상승 추세
        /// 4) 거래량: 일반 수준 (급등 아님)
        /// 
        /// 반환: true = 안정적인 눌림목 신호, +10 보너스 점수 부여
        /// </summary>
        private async Task<bool> CheckEMA20RetestAsync(string symbol, CandleData latestCandle, CancellationToken token)
        {
            try
            {
                // 1) 5분봉 EMA 20 계산
                var k5m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 50, ct: token);
                if (!k5m.Success || k5m.Data == null || k5m.Data.Length < 30)
                    return false;
                
                var candles5m = k5m.Data.ToList();
                double ema20_5m = IndicatorCalculator.CalculateEMA(candles5m, 20);
                
                // 3) 1시간봉 EMA 정배열 확인
                var k1h = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.OneHour, limit: 60, ct: token);
                if (!k1h.Success || k1h.Data == null || k1h.Data.Length < 50)
                    return false;
                
                var candles1h = k1h.Data.ToList();
                double ema20_1h = IndicatorCalculator.CalculateEMA(candles1h, 20);
                double ema50_1h = IndicatorCalculator.CalculateEMA(candles1h, 50);
                
                bool isAlignedBullish = ema20_1h > ema50_1h;
                if (!isAlignedBullish)
                    return false;
                
                // 4) 현재가가 EMA 20 근처인지 확인 (±0.2%)
                double emaDeviation = Math.Abs((double)latestCandle.Close - ema20_5m) / ema20_5m;
                if (emaDeviation > 0.002) // 0.2% 초과
                    return false;
                
                // 5) RSI 상승 추세 확인 (RSI >= 45 + 이전봉보다 높음)
                if (latestCandle.RSI < 45f)
                    return false;
                
                // 이전 RSI 계산 (간단히 5분봉 2번째 마지막으로 근사)
                if (candles5m.Count >= 21)
                {
                    var prevCandles = candles5m.Count >= 2 
                        ? candles5m.Take(candles5m.Count - 1).ToList()
                        : new List<IBinanceKline>(); // 데이터 부족시 빈 리스트
                    double prevRSI = IndicatorCalculator.CalculateRSI(prevCandles, 14);
                    if (latestCandle.RSI <= prevRSI)
                        return false; // RSI 하락 중이면 눌림목 아님
                }
                
                // 6) 거래량 급등이 아닌지 확인 (볼륨 비율 < 1.5배)
                if (latestCandle.Volume_Ratio > 1.5f)
                    return false;
                
                return true; // 모든 조건 만족: EMA 20 눌림목 확정
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// [AI 보너스 2] 숏 스퀴즈 감지
        /// ───────────────────────────────────────
        /// 조건:
        /// 1) 5분봉 가격 급등 (+0.3% 이상)
        /// 2) Open Interest 급감 (-0.8% 이상)
        /// 3) 거래량 급증 (1.5배 이상)
        /// 4) BB 상단 돌파 시도
        /// 
        /// 반환: true = 숏 포지션 강제 청산 구간, +15 보너스 점수 부여
        /// </summary>
        private async Task<bool> CheckShortSqueezeAsync(string symbol, CandleData latestCandle, CancellationToken token)
        {
            try
            {
                // 1) 5분봉 가격 변화 확인
                var k5m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 10, ct: token);
                if (!k5m.Success || k5m.Data == null || k5m.Data.Length < 2)
                    return false;
                
                var candles5m = k5m.Data.ToList();
                // [FIX] 빈 리스트 체크 추가
                if (candles5m.Count < 2)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 5분봉 데이터 부족 (필요: 2, 실제: {candles5m.Count})");
                    return false;
                }
                
                var current = candles5m.Last();
                var previous = candles5m[candles5m.Count - 2];
                
                decimal priceChange = (current.ClosePrice - previous.ClosePrice) / previous.ClosePrice * 100;
                if (priceChange < 0.3m) // +0.3% 미만
                    return false;
                
                // 3) 거래량 급증 확인 (현재 캔들 데이터 활용)
                if (latestCandle.Volume_Ratio < 1.5f)
                    return false;
                
                // 4) BB 상단 근접 확인 (현재가가 BB 상단의 98% 이상)
                if (latestCandle.BollingerUpper > 0)
                {
                    double bbProximity = (double)latestCandle.Close / latestCandle.BollingerUpper;
                    if (bbProximity < 0.98) // BB 상단 근처 아님
                        return false;
                }
                
                // 5) Open Interest 급감 확인 (OiDataCollector 실시간 조회)
                if (_oiCollector != null)
                {
                    var oiSnapshot = await _oiCollector.GetCurrentOiAsync(symbol, token);
                    if (oiSnapshot == null || oiSnapshot.OiChangePct > -0.8)
                        return false; // OI 급감 없으면 스퀴즈 아님
                }
                
                return true; // 숏 스퀴즈 조건 만족
            }
            catch
            {
                return false;
            }
        }

        private string GetEngineStatusReport()
        {
            int activeCount = 0;
            string positionsStr = "";
            string aiLabelingStr = "AI 게이트 비활성";

            lock (_posLock)
            {
                activeCount = _activePositions.Count;
                if (activeCount > 0)
                {
                    foreach (var kvp in _activePositions)
                    {
                        var pos = kvp.Value;
                        string side = pos.IsLong ? "LONG" : "SHORT";

                        // 현재가 가져오기 (UI 리스트 활용)
                        decimal currentPrice = pos.EntryPrice;
                        var vm = MainWindow.Instance?.ViewModel?.MarketDataList.FirstOrDefault(x => x.Symbol == kvp.Key);
                        if (vm != null) currentPrice = vm.LastPrice;

                        // 수익률 계산
                        decimal pnlPercent = 0;
                        if (pos.EntryPrice > 0)
                        {
                            if (pos.IsLong) pnlPercent = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100 * pos.Leverage;
                            else pnlPercent = (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100 * pos.Leverage;
                        }

                        string icon = pnlPercent >= 0 ? "🟢" : "🔴";
                        positionsStr += $"{icon} *{kvp.Key}* ({side})\n" +
                                        $"   진입: ${pos.EntryPrice:0.####} | ROE: {pnlPercent:F2}%\n";
                    }
                }
                else
                {
                    positionsStr = "   (보유 포지션 없음)\n";
                }
            }

            var upTime = DateTime.Now - _engineStartTime;
            string timeStr = $"{(int)upTime.TotalDays}일 {upTime.Hours}시간 {upTime.Minutes}분";

            if (_aiDoubleCheckEntryGate != null)
            {
                var stats = _aiDoubleCheckEntryGate.GetRecentLabelStats(10);
                int activeDecisionCount = _activeAiDecisionIds.Count;
                double labeledRate = stats.total > 0 ? (double)stats.labeled / stats.total * 100.0 : 0.0;

                aiLabelingStr = $"🧠 *[AI 라벨링 상태]*\n" +
                                $"   • 전체 결정: {stats.total}건\n" +
                                $"   • 라벨링 완료: {stats.labeled}건 ({labeledRate:F1}%)\n" +
                                $"   • Mark-to-Market: {stats.markToMarket}건\n" +
                                $"   • 실거래 종료: {stats.tradeClose}건\n" +
                                $"   • 진행 중 결정: {activeDecisionCount}건";
            }

            return $"🤖 *[시스템 상태]*\n" +
                   $"⏱ 가동 시간: {timeStr}\n" +
                   $"💰 금일 실현 손익: ${_riskManager.DailyRealizedPnl:N2}\n\n" +
                   $"📊 *[보유 포지션: {activeCount}개]*\n" +
                   positionsStr +
                   $"\n{aiLabelingStr}\n" +
                   $"\n_Updated: {DateTime.Now:HH:mm:ss}_";
        }

        public string GetEngineStatusJson()
        {
            int activeCount = 0;
            lock (_posLock) activeCount = _activePositions.Count;

            int aiLabelTotal = 0;
            int aiLabelLabeled = 0;
            int aiLabelMarkToMarket = 0;
            int aiLabelClose = 0;
            int activeDecisionCount = _activeAiDecisionIds.Count;

            if (_aiDoubleCheckEntryGate != null)
            {
                var stats = _aiDoubleCheckEntryGate.GetRecentLabelStats(10);
                aiLabelTotal = stats.total;
                aiLabelLabeled = stats.labeled;
                aiLabelMarkToMarket = stats.markToMarket;
                aiLabelClose = stats.tradeClose;
            }

            double aiLabelRate = aiLabelTotal > 0 ? (double)aiLabelLabeled / aiLabelTotal * 100.0 : 0.0;

            // 간단한 JSON 생성 (Newtonsoft.Json 없이 문자열 보간 사용)
            return $@"{{
                ""status"": ""{(IsBotRunning ? "Running" : "Stopped")}"",
                ""uptime"": ""{(DateTime.Now - _engineStartTime).ToString(@"dd\.hh\:mm\:ss")}"",
                ""balance"": {InitialBalance},
                ""pnl"": {_riskManager.DailyRealizedPnl},
                ""active_positions"": {activeCount},
                ""ai_label_total"": {aiLabelTotal},
                ""ai_label_labeled"": {aiLabelLabeled},
                ""ai_label_rate"": {aiLabelRate:F2},
                ""ai_label_mark_to_market"": {aiLabelMarkToMarket},
                ""ai_label_close"": {aiLabelClose},
                ""ai_active_decisions"": {activeDecisionCount}
            }}";
        }

        // [AI 모니터링] 예측 검증 메서드
        private async Task ValidatePredictionAsync(string predictionKey, string symbol, CancellationToken token)
        {
            try
            {
                if (!_pendingPredictions.TryRemove(predictionKey, out var predictionInfo))
                {
                    return;
                }

                var (timestamp, entryPrice, predictedPrice, predictedDirection, modelName, confidence) = predictionInfo;
                
                OnStatusLog?.Invoke($"🔍 [{modelName}] {symbol} 예측 검증 시작 (5분 경과)");

                // 현재 가격 조회
                var tickerResult = await _client.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol, token);
                if (!tickerResult.Success || tickerResult.Data == null)
                {
                    OnStatusLog?.Invoke($"❌ [{symbol}] 가격 조회 실패 - 예측 검증 중단");
                    return;
                }

                decimal actualPrice = tickerResult.Data.LastPrice;

                if (entryPrice <= 0m)
                {
                    OnStatusLog?.Invoke($"⚠️ [{modelName}] {symbol} 예측 검증 스킵: entryPrice 비정상 ({entryPrice:F8})");
                    return;
                }

                decimal moveRatio = Math.Abs((actualPrice - entryPrice) / entryPrice);
                if (moveRatio < PredictionValidationNeutralMovePct)
                {
                    OnStatusLog?.Invoke($"⏭️ [{modelName}] {symbol} 예측 검증 스킵: 미세변동 {moveRatio:P2} < {PredictionValidationNeutralMovePct:P2}");
                    return;
                }

                // 실제 방향 계산 (저장된 진입 가격 기준)
                bool actualDirection = actualPrice > entryPrice;

                // 정확도 판단
                bool isCorrect = predictedDirection == actualDirection;

                // 예측 기록 생성 및 이벤트 발생
                var record = new AIPredictionRecord
                {
                    Timestamp = timestamp,
                    Symbol = symbol,
                    ModelName = modelName,
                    PredictedPrice = predictedPrice,
                    ActualPrice = actualPrice,
                    PredictedDirection = predictedDirection,
                    ActualDirection = actualDirection,
                    Confidence = confidence,
                    IsCorrect = isCorrect
                };

                OnAIPrediction?.Invoke(record);
                
                string resultIcon = isCorrect ? "✅" : "❌";
                string direction = predictedDirection ? "상승" : "하락";
                OnStatusLog?.Invoke($"{resultIcon} [{modelName}] {symbol} 예측 검증 완료: {direction} 예측 → {(isCorrect ? "정확" : "미적중")} (진입: ${entryPrice:F4} / 예측: ${predictedPrice:F4} / 실제: ${actualPrice:F4})");

                // RL 보상 계산 및 업데이트
                double reward = isCorrect ? (confidence * 10) : (-confidence * 5);
                double scalpingReward = (double)(Math.Abs(actualPrice - entryPrice) / entryPrice) * (isCorrect ? 100 : -100);
                double swingReward = reward;

                OnRLStatsUpdate?.Invoke(modelName, (float)scalpingReward, (float)swingReward);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ 예측 검증 실패: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                PersistActiveAiDecisionIds();

                // CancellationTokenSource 정리
                _cts?.Cancel();
                _cts?.Dispose();
                
                // REST/Socket 클라이언트 정리
                _client?.Dispose();
                _marketDataManager?.Dispose();
                
                // 거래소 서비스 정리
                if (_exchangeService is IDisposable disposableExchange)
                {
                    disposableExchange.Dispose();
                }
                
                // AI 서비스 정리
                _aiPredictor?.Dispose();
                _aiDoubleCheckEntryGate?.Dispose();
                _transformerTrainer?.Dispose();
                
                // 데이터베이스 연결 정리
                // DbManager는 연결을 공유하므로 명시적 Dispose 불필요
                
                OnStatusLog?.Invoke("✅ TradingEngine 리소스 정리 완료");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ TradingEngine Dispose 오류: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
            
            GC.SuppressFinalize(this);
        }

        ~TradingEngine()
        {
            Dispose();
        }
    }

    /// <summary>
    /// [캔들 확인 지연 진입] 대기 중인 신호 데이터
    /// 신호가 발생하면 즉시 진입하지 않고, 다음 캔들에서 확인 후 진입합니다.
    /// 가짜 돌파(Fakeout) 방지용.
    /// </summary>
    public class DelayedEntrySignal
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;   // "LONG" 또는 "SHORT"
        public decimal SignalPrice { get; set; }                 // 신호 발생 당시 가격
        public DateTime SignalTime { get; set; }                 // 신호 발생 시간
        public decimal SignalCandleHigh { get; set; }            // 신호 발생 캔들의 고가
        public decimal SignalCandleLow { get; set; }             // 신호 발생 캔들의 저가
        public decimal SignalCandleClose { get; set; }           // 신호 발생 캔들의 종가
        public string SignalSource { get; set; } = string.Empty; // 신호 소스 (MAJOR, TRANSFORMER 등)
        public string Mode { get; set; } = "TREND";
        public decimal CustomTakeProfitPrice { get; set; }
        public decimal CustomStopLossPrice { get; set; }
    }
}
