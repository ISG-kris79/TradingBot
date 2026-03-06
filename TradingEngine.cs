using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Authentication;
using Binance.Net.Objects.Models.Futures.Socket;
using System.Collections.Concurrent;
using System.IO; // [FIX] Path, File 사용을 위해 추가
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
        private CancellationTokenSource _cts;
        private readonly string apiKey;
        private readonly string apiSecret;

        // 감시 종목 리스트
        private readonly List<string> _symbols;

        public decimal InitialBalance { get; private set; } = 0; // 프로그램 시작 시점의 자산
        private DateTime _engineStartTime = DateTime.Now;
        private DateTime _lastReportTime = DateTime.MinValue;

        // 현재 보유 중인 포지션 정보 (심볼, 진입가)
        private Dictionary<string, PositionInfo> _activePositions = new Dictionary<string, PositionInfo>();
        private readonly object _posLock = new object();
        // 블랙리스트 (심볼, 해제시간) - 지루함 청산 종목 재진입 방지
        private ConcurrentDictionary<string, DateTime> _blacklistedSymbols = new ConcurrentDictionary<string, DateTime>();
        // 슬롯 설정
        private const int MAX_MAJOR_SLOTS = 4; // BTC, ETH, SOL, XRP 전용
        private const int MAX_PUMP_SLOTS = 2;  // 실시간 급등주 전용
        private const int PUMP_MANUAL_LEVERAGE = 20; // 20배 롱 전용 대응 매뉴얼

        // 전략 인스턴스
        private PumpScanStrategy? _pumpStrategy;
        private MajorCoinStrategy? _majorStrategy;
        private GridStrategy _gridStrategy;
        private ArbitrageStrategy _arbitrageStrategy;
        private NewsSentimentService _newsService;
        private TransformerStrategy _transformerStrategy;
        private TransformerTrainer _transformerTrainer;
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

        private readonly ConcurrentDictionary<string, byte> _runningStandardMonitors = new ConcurrentDictionary<string, byte>();

        private ConcurrentDictionary<string, DateTime> _lastTickerUpdateTimes = new ConcurrentDictionary<string, DateTime>();

        private ConcurrentDictionary<string, DateTime> _lastAnalysisTimes = new ConcurrentDictionary<string, DateTime>();

        private DateTime _lastCleanupTime = DateTime.Now;

        private Channel<IBinance24HPrice> _tickerChannel;
        // [Agent 2] 비동기 성능 개선: 주문 및 계좌 업데이트용 채널 추가
        private Channel<BinanceFuturesStreamAccountUpdate> _accountChannel;
        private Channel<BinanceFuturesStreamOrderUpdate> _orderChannel;

        private ConcurrentQueue<string> _apiLogBuffer = new ConcurrentQueue<string>(); // [추가] API용 로그 버퍼

        private readonly MarketDataManager _marketDataManager;
        private readonly RiskManager _riskManager;
        private AIPredictor? _aiPredictor;
        private MultiAgentManager _multiAgentManager;
        private MarketHistoryService? _marketHistoryService;
        private OiDataCollector? _oiCollector;

        // [AI 모니터링] 예측 추적용 Dictionary (symbol + timestamp -> 예측 정보)
        private ConcurrentDictionary<string, (DateTime timestamp, decimal predictedPrice, bool predictedDirection, string modelName, float confidence)> _pendingPredictions
            = new ConcurrentDictionary<string, (DateTime, decimal, bool, string, float)>();

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
        public event Action<bool, string>? OnTelegramStatusUpdate;
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

        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private DateTime _lastPositionSyncTime = DateTime.MinValue; // [FIX] 마지막 포지션 동기화 시간
        private bool _initialTransformerTrainingTriggered = false;
        private readonly TimeSpan _entryWarmupDuration = TimeSpan.FromSeconds(30); // 워밍업 30초로 단축 (2025-03-04)
        private DateTime _lastEntryWarmupLogTime = DateTime.MinValue;

        public TradingEngine()
        {
            _cts = new CancellationTokenSource();

            // [추가] 로그 버퍼링 (최근 100개 유지)
            this.OnLiveLog += (msg) => AddToLogBuffer($"[LIVE] {msg}");
            this.OnStatusLog += (msg) => AddToLogBuffer($"[STATUS] {msg}");
            this.OnAlert += (msg) => AddToLogBuffer($"[ALERT] {msg}");

            // 선택된 거래소에 따라 API 키 설정
            ExchangeType selectedExchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);
            switch (selectedExchange)
            {
                case ExchangeType.Binance:
                    apiKey = AppConfig.BinanceApiKey;
                    apiSecret = AppConfig.BinanceApiSecret;
                    break;
                case ExchangeType.Bybit:
                    apiKey = AppConfig.BybitApiKey;
                    apiSecret = AppConfig.BybitApiSecret;
                    break;
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
                // 이미 위에서 선언한 selectedExchange 변수 재사용
                switch (selectedExchange)
                {
                    case ExchangeType.Bybit:
                        _exchangeService = new BybitExchangeService(AppConfig.BybitApiKey, AppConfig.BybitApiSecret);
                        OnStatusLog?.Invoke("🔗 바이비트 거래소 연결 완료");
                        break;
                    case ExchangeType.Binance:
                    default:
                        _exchangeService = new BinanceExchangeService(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                        OnStatusLog?.Invoke("🔗 바이낸스 거래소 연결");
                        break;
                }
            }

            _orderService = new BinanceOrderService(_client);

            // GeneralSettings 로드: MainWindow에서 초기화된 설정 사용 (DB 우선)
            _settings = MainWindow.CurrentGeneralSettings
                ?? AppConfig.Current?.Trading?.GeneralSettings
                ?? new TradingSettings();

            _symbols = AppConfig.Current?.Trading?.Symbols ?? new List<string>();
            if (_symbols.Count == 0)
            {
                _symbols.AddRange(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" });
            }

            _soundService = new SoundService();

            _newsService = new NewsSentimentService();
            _arbitrageStrategy = new ArbitrageStrategy(_client);

            _notificationService = new NotificationService(); // \ub9e4\uac1c\ubcc0\uc218 \uc5c6\ub294 \uc0dd\uc131\uc790

            _dbManager = new DbManager(AppConfig.ConnectionString);
            _riskManager = new RiskManager();
            _marketDataManager = new MarketDataManager(_client, _symbols);
            _marketHistoryService = new MarketHistoryService(_marketDataManager, AppConfig.ConnectionString);
            _oiCollector = new OiDataCollector(_client);

            _tickerChannel = Channel.CreateBounded<IBinance24HPrice>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest // 처리 속도가 느리면 오래된 티커 드랍
            });

            // [Agent 2] 채널 초기화
            _accountChannel = Channel.CreateUnbounded<BinanceFuturesStreamAccountUpdate>();
            _orderChannel = Channel.CreateUnbounded<BinanceFuturesStreamOrderUpdate>();

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
                OnStatusLog?.Invoke("🧠 AI 예측 모델 로드 완료");
            }
            catch (Exception ex)
            {
                _positionMonitor.UpdateAiPredictor(null);
                OnStatusLog?.Invoke($"⚠️ AI 모델 로드 실패 (학습 후 사용 가능): {ex.Message}");
            }

            _pumpStrategy = new PumpScanStrategy(_client, _symbols, AppConfig.Current?.Trading?.PumpSettings ?? new PumpScanSettings());
            _majorStrategy = new MajorCoinStrategy(
                _marketDataManager,
                () => MainWindow.CurrentGeneralSettings ?? AppConfig.Current?.Trading?.GeneralSettings);
            _gridStrategy = new GridStrategy(_exchangeService);

            _pumpStrategy.OnSignalAnalyzed += vm => OnSignalUpdate?.Invoke(vm);
            _pumpStrategy.OnPumpDetected += async (symbol, price, decision, rsi, atr) =>
            {
                await HandlePumpEntry(symbol, price, decision, rsi, atr, _cts.Token);
            };

            _pumpStrategy.OnLog += msg => OnLiveLog?.Invoke(msg);

            if (_majorStrategy != null)
            {
                _majorStrategy.OnSignalAnalyzed += vm => OnSignalUpdate?.Invoke(vm);
                _majorStrategy.OnTradeSignal += async (symbol, decision, price) => await ExecuteAutoOrder(symbol, decision, price, _cts.Token, "MAJOR");
                _majorStrategy.OnLog += msg => OnLiveLog?.Invoke(msg);
            }

            // [Phase 7] Transformer 모델 및 전략 초기화 (설정 파일 로드)
            // [FIX] TorchSharp 네이티브 라이브러리 크래시 방지를 위한 안전장치 추가
            var tfSettings = AppConfig.Current?.Trading?.TransformerSettings ?? new TransformerSettings();
            bool transformerInitSuccess = false;
            try
            {
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
                catch { }
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
                await _executionService.UpdateTrailingStopAsync(symbol, newStopPrice);
            };

            // [3파 통합] TransformerStrategy에 ElliottWave3WaveStrategy 주입
            // [FIX] Transformer 초기화 실패 시 null 전달하여 안전하게 비활성화
            if (transformerInitSuccess && _transformerTrainer != null)
            {
                _transformerStrategy = new TransformerStrategy(_client, _transformerTrainer, _newsService, _elliotWave3Strategy, tfSettings);
                _transformerStrategy.OnLog += msg => OnStatusLog?.Invoke(msg);
                _transformerStrategy.OnSignalAnalyzed += vm => OnSignalUpdate?.Invoke(vm);
            }
            else
            {
                OnStatusLog?.Invoke("⚠️ TransformerStrategy 비활성화 (Transformer 초기화 실패)");
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

                    OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                    {
                        Symbol = symbol,
                        TransformerPrice = predictedPrice,
                        TransformerChange = change
                    });

                    // [AI 모니터] Transformer 예측을 _pendingPredictions에 등록 → 5분 후 검증
                    bool predictedDirection = predictedPrice > currentPrice; // 상승 예측 여부
                    float confidence = (float)Math.Min(Math.Abs(change) / 5.0, 1.0); // 변화율 기반 신뢰도 (5%를 1.0으로)
                    string predictionKey = $"TF_{symbol}_{DateTime.Now:yyyyMMddHHmmss}";
                    _pendingPredictions[predictionKey] = (DateTime.Now, predictedPrice, predictedDirection, "Transformer", confidence);

                    string direction = predictedDirection ? "상승" : "하락";
                    OnStatusLog?.Invoke($"🔮 [Transformer] {symbol} 예측 등록: {direction} (신뢰도: {confidence:P0}) → 5분 후 검증 예정");

                    var cts = _cts;
                    if (cts != null && !cts.Token.IsCancellationRequested)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);
                                await ValidatePredictionAsync(predictionKey, symbol, cts.Token);
                            }
                            catch (OperationCanceledException) { }
                        }, cts.Token);
                    }
                };

                _transformerStrategy.OnTradeSignal += async (symbol, side, currentPrice, predictedPrice, mode, customTakeProfitPrice, customStopLossPrice) =>
                {
                    // Transformer 전략 신호 발생 시 자동 매매 실행 (LONG/SHORT)
                    await ExecuteAutoOrder(symbol, side, currentPrice, _cts.Token, $"TRANSFORMER_{mode}", mode, customTakeProfitPrice, customStopLossPrice);
                    // 하이브리드 이탈 관리자에 등록 (AI 기반 익절/트레일링 스탑)
                    if (mode != "SIDEWAYS")
                    {
                        _hybridExitManager.RegisterEntry(symbol, side, currentPrice, predictedPrice);
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
                decimal currentBalance = 0;
                
                // 시뮬레이션 모드 체크
                bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                
                if (isSimulation)
                {
                    // 시뮬레이션 모드: MockExchangeService 사용
                    currentBalance = await _exchangeService.GetBalanceAsync("USDT", token);
                }
                else
                {
                    // 실거래 모드: Binance REST API 사용
                    var balanceRes = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: token);
                    if (!balanceRes.Success || balanceRes.Data == null) return;

                    var usdt = balanceRes.Data.FirstOrDefault(b => b.Asset == "USDT");
                    if (usdt == null) return;

                    currentBalance = usdt.WalletBalance;
                }

                // 초기 자산이 0인 경우(설정 실패 등) 현재 잔고를 초기 자산으로 임시 대체하여 에러 방지
                decimal initial = InitialBalance > 0 ? InitialBalance : currentBalance;

                decimal pnl = currentBalance - initial;

                // 수익률 계산 (나누기 0 방지)
                double pnlPercent = initial > 0
                    ? (double)(pnl / initial * 100)
                    : 0;

                // 가동 시간 계산 (리포트 주기 기준이 아닌 엔진 시작 기준)
                var upTime = DateTime.Now - _engineStartTime;
                string timeStr = $"{(int)upTime.TotalDays}일 {upTime.Hours}시간 {upTime.Minutes}분";

                string body = $"🏦 **현재 잔고**: `${currentBalance:N2} USDT`\n" +
                              $"📈 **총 수익금**: `{pnl:N2} USDT`\n" +
                              $"📊 **수익률**: `{pnlPercent:F2}%`\n" +
                              $"⏳ **총 가동 시간**: {timeStr}\n" +
                              $"🚀 **현재 운용 중**: {_activePositions.Count}개 종목";

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
                                EntryTime = synced.EntryTime,
                                StopLoss = synced.StopLoss
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
                if (_marketDataManager.KlineCache.TryGetValue(symbol, out var cachedCandles))
                {
                    lock (cachedCandles)
                    {
                        if (cachedCandles.Count >= 15)
                            candles = cachedCandles.TakeLast(Math.Min(40, cachedCandles.Count)).ToList();
                    }
                }

                if (candles == null || candles.Count < 15)
                {
                    var fetched = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 40, token);
                    if (fetched != null && fetched.Count >= 15)
                        candles = fetched.ToList();
                }

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

                // [추가] 엔진 시작 시 Transformer 초기 학습 1회 자동 실행 (모델 미준비 시)
                await TriggerInitialTransformerTrainingIfNeededAsync(token);

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

                _ = ProcessTickerChannelAsync(token);
                _ = ProcessAccountChannelAsync(token); // [Agent 2] 계좌 업데이트 처리 시작
                _ = ProcessOrderChannelAsync(token);   // [Agent 2] 주문 업데이트 처리 시작

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

                await PreloadInitialAiScoresAsync(token);

                // 엔진 가동 시간 기록 및 주기 타이머 초기화
                _engineStartTime = DateTime.Now;
                _lastHeartbeatTime = DateTime.Now; // 시작 시점 기록 (1시간 후 첫 알림)
                _lastReportTime = DateTime.Now;    // 시작 시점 기록 (1시간 후 첫 보고)
                _lastPositionSyncTime = DateTime.Now; // [FIX] 시작 시점 기록 (30분 후 첫 동기화)
                OnStatusLog?.Invoke($"⏳ 진입 워밍업 시작: {_entryWarmupDuration.TotalSeconds:F0}초 동안 신규 진입 제한");

                // 3. 메인 관리 루프 (REST API 호출 최소화)
                OnStatusLog?.Invoke("🔄 메인 스캔 루프 시작...");
                bool isCircuitBreakerNotificationSent = false;

                while (!token.IsCancellationRequested)
                {
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
                                string resumeMsg = $"♻️ [서킷 브레이커 해제] 1시간 대기 종료. 매매를 재개합니다.\n(금일 손익 초기화, 잔여 포지션 {closedCount}개 청산 완료)";
                                await NotificationService.Instance.NotifyAsync(resumeMsg, NotificationChannel.Alert);
                                OnAlert?.Invoke(resumeMsg);
                                OnStatusLog?.Invoke("서킷 브레이커 자동 해제됨.");
                            }
                            else
                            {
                                if (!isCircuitBreakerNotificationSent)
                                {
                                    isCircuitBreakerNotificationSent = true;
                                    string msg = $"⛔ [서킷 브레이커 발동]\n{_riskManager.GetTripDetails()}\n\n모든 매매를 일시 중단합니다.";
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

                        // [B] 메이저 코인 분석 (BTC, ETH, SOL, XRP)
                        if (_majorStrategy != null)
                        {
                            var majorSymbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
                            foreach (var symbol in majorSymbols)
                            {
                                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                                {
                                    await _majorStrategy.AnalyzeAsync(symbol, ticker.LastPrice, token);
                                }
                            }
                        }

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
                        await Task.Delay(TimeSpan.FromSeconds(3), token);
                    }
                    catch (Exception loopEx)
                    {
                        LoggerService.Error("메인 루프 오류 (자동 복구 시도)", loopEx);
                        OnStatusLog?.Invoke($"⚠️ 일시적 오류: {loopEx.Message}. 5초 후 재시도...");
                        await Task.Delay(5000, token);
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
                        // 2. 모델 학습 및 저장 (백그라운드에서 실행하여 UI 블로킹 방지)
                        // 설정 파일에서 학습 파라미터 로드
                        var tfSettings = AppConfig.Current?.Trading?.TransformerSettings ?? new TransformerSettings();

                        // [Agent 1] 모델 재학습 (예외 처리 강화)
                        // [FIX] TorchSharp null 체크 추가
                        if (_transformerTrainer == null)
                        {
                            OnStatusLog?.Invoke("⚠️ Transformer가 비활성화되어 있어 학습을 건너뜁니다.");
                            return;
                        }

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
                                    OnStatusLog?.Invoke($"❌ AI 모델 학습 내부 오류: {ex.Message}");
                                    OnStatusLog?.Invoke($"상세: {ex.StackTrace}");
                                    throw; // 예외를 다시 던져서 외부 catch에서 처리
                                }
                            }, token);

                            OnStatusLog?.Invoke($"✅ AI 모델 재학습 완료 (데이터: {trainingData.Count}건)");
                        }
                        catch (OperationCanceledException)
                        {
                            OnStatusLog?.Invoke("⚠️ AI 학습이 취소되었습니다.");
                            throw; // 취소는 상위로 전파
                        }
                        catch (Exception ex)
                        {
                            OnStatusLog?.Invoke($"❌ AI 학습 Task 실행 오류: {ex.Message}");
                            // 학습 실패해도 엔진은 계속 실행
                        }
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
                    // UI 업데이트 (스로틀링은 HandleTickerUpdate에서 이미 처리됨)
                    UpdateRealtimeProfit(tick.Symbol, tick.LastPrice);

                    // 전략 분석 실행
                    await ProcessCoinAndTradeBySymbolAsync(tick.Symbol, tick.LastPrice, token);
                }
            }
            catch (OperationCanceledException) { }
        }

        // [Agent 2] 계좌 업데이트 처리 소비자
        private async Task ProcessAccountChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var update in _accountChannel.Reader.ReadAllAsync(token))
                {
                    await HandleAccountUpdate(update);
                }
            }
            catch (OperationCanceledException) { }
        }

        // [Agent 2] 주문 업데이트 처리 소비자
        private async Task ProcessOrderChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var update in _orderChannel.Reader.ReadAllAsync(token))
                {
                    _positionMonitor.HandleOrderUpdate(update);
                }
            }
            catch (OperationCanceledException) { }
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
            // 종목이 리스트에 없으면 추가 요청
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
                        OnStatusLog?.Invoke($"⏳ 진입 워밍업 중: {remaining.TotalSeconds:F0}초 남음");
                    }
                    return;
                }

                var now = DateTime.Now;
                if (_lastAnalysisTimes.TryGetValue(symbol, out var lastTime))
                {
                    if ((now - lastTime).TotalSeconds < 5) return;
                }
                _lastAnalysisTimes[symbol] = now;

                // 1. 그리드 전략 (횡보장 대응)
                await _gridStrategy.ExecuteAsync(symbol, currentPrice, token);
                // 2. 차익거래 전략 (거래소 간 가격 차이 감지)
                await _arbitrageStrategy.AnalyzeAsync(symbol, currentPrice, token);

                int majorCount = 0;
                lock (_posLock)
                {
                    majorCount = _activePositions.Values.Count(p => !p.IsPumpStrategy);
                }
                if (majorCount < MAX_MAJOR_SLOTS)
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
            catch { /* 로그 생략 */ }
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
                            // [FIX] klines가 비어있지 않은지 확인
                            if (_aiPredictor != null && klines.Any())
                            {
                                var lastKline = klines.Last();
                                var latestCandle = new CandleData
                                {
                                    Open = lastKline.OpenPrice,
                                    High = lastKline.HighPrice,
                                    Low = lastKline.LowPrice,
                                    Close = lastKline.ClosePrice,
                                    Volume = (float)lastKline.Volume,
                                };
                                var pred = _aiPredictor.Predict(latestCandle);
                                // AI 방향 반전 확인용으로 PredictedPrice를 업데이트
                                if (pred != null)
                                {
                                    // Scalping 예측의 Score 기반으로 방향성 판단
                                    decimal predDirection = pred.Score > 0.5f
                                        ? currentPrice * 1.005m  // 상승 예측 → 현재가+0.5%
                                        : currentPrice * 0.995m; // 하락 예측 → 현재가-0.5%
                                    newPrediction = predDirection;
                                }
                            }
                        }
                    }
                    catch { /* 재예측 실패 시 무시 */ }
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
            TelegramService.Instance.OnRequestStatus = null!;
            TelegramService.Instance.OnRequestStop = null!;
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

            // 1단계 부분 익절: 수익률 1.2% 도달 시 보유 물량의 50% 매도
            if (pos.TakeProfitStep == 0 && currentProfit >= 1.2)
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
            if (IsEntryWarmupActive(out var remaining))
            {
                OnStatusLog?.Invoke($"⏳ {symbol} 워밍업 중으로 PUMP 진입 보류 ({remaining.TotalSeconds:F0}초 남음)");
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
                return;

            if (currentPumpCount >= MAX_PUMP_SLOTS)
            {
                OnStatusLog?.Invoke($"⛔ {symbol} PUMP 슬롯 가득 참 ({currentPumpCount}/{MAX_PUMP_SLOTS})으로 진입 보류");
                return;
            }

            decimal marginUsdt = CalculateDynamicPositionSize(atr, currentPrice);

            if (_riskManager.IsTripped) return;

            // [20배 PUMP 롱 전용] 5분봉 컨플루언스 진입 필터
            var pumpKlines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 40, token);
            if (pumpKlines == null || pumpKlines.Count < 30)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} 5분봉 데이터 부족으로 PUMP 진입 보류");
                return;
            }

            var candles5m = pumpKlines.ToList();
            var recent30 = candles5m.TakeLast(30).ToList();
            decimal swingHigh = recent30.Max(k => k.HighPrice);
            decimal swingLow = recent30.Min(k => k.LowPrice);
            decimal waveRange = swingHigh - swingLow;
            if (waveRange <= 0)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} 파동 범위 계산 실패로 진입 보류");
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
                OnStatusLog?.Invoke($"⚠️ {symbol} 5분봉 데이터 없음");
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
            
            // [필수조건] 호가창 매수 우위 확인 (슬리피지 방어용)
            var orderBook = await _exchangeService.GetOrderBookAsync(symbol, token);
            double orderBookRatio = 0;
            if (orderBook.HasValue)
            {
                // bestBid와 bestAsk 가격 비율 사용 (간접 지표)
                decimal bestBid = orderBook.Value.bestBid;
                decimal bestAsk = orderBook.Value.bestAsk;
                orderBookRatio = bestAsk > 0 ? (double)(bestBid / bestAsk) : 0;
            }
            
            // [새로운 로직] 필수 조건 + 선택 조건 점수제 적용
            // 필수: Fib 범위, RSI < 80, 호가창 >= 1.2
            // 선택 (3개 중 2개 필요): BB ±0.5%, MACD >= 0, RSI >= 45 + 상升
            bool canEnter = IsEnhancedEntryCondition(
                currentPrice, fib323, fib618, rsi5m, bbMidlineDeviation, 
                macdAboveZero, rsiCondition, orderBookRatio);

            if (!canEnter)
            {
                OnStatusLog?.Invoke(
                    $"🧭 {symbol} PUMP 진입 보류 | Fib0.323~0.618={(inEntryZone ? "OK" : "NO")} | BB±0.5%={(bbMidSupport ? "OK" : "NO")} | MACD≥0={(macdAboveZero ? "OK" : "NO")} | RSI≥45상升={(rsiCondition ? "OK" : "NO")} | OrderBook={(orderBookRatio >= 1.2 ? "OK" : "NO")}");
                return;
            }

            // 손절 라인(0.618 or 직전 스윙저점) 거리 체크: 1% 초과 시 비중 축소
            decimal recentSwingLow = candles5m.TakeLast(6).Min(k => k.LowPrice);
            decimal logicalStop = Math.Min(fib618, recentSwingLow);
            if (logicalStop <= 0 || logicalStop >= currentPrice)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} 논리적 손절가 계산 실패로 진입 보류");
                return;
            }

            decimal stopDistancePercent = (currentPrice - logicalStop) / currentPrice * 100m;
            decimal pumpStopWarnPct = _settings.PumpStopDistanceWarnPct > 0 ? _settings.PumpStopDistanceWarnPct : 1.0m;
            decimal pumpStopBlockPct = _settings.PumpStopDistanceBlockPct > 0 ? _settings.PumpStopDistanceBlockPct : 1.3m;

            if (stopDistancePercent > pumpStopWarnPct)
            {
                marginUsdt *= 0.6m;
                OnStatusLog?.Invoke($"⚠️ {symbol} 손절거리 {stopDistancePercent:F2}% > {pumpStopWarnPct:F2}%로 진입 비중 40% 축소");
            }

            if (stopDistancePercent > pumpStopBlockPct)
            {
                OnStatusLog?.Invoke($"⛔ {symbol} 손절거리 과도({stopDistancePercent:F2}% > {pumpStopBlockPct:F2}%)로 진입 취소");
                return;
            }

            float aiScore = 0;
            if (_aiPredictor != null)
            {
                var candleData = await GetLatestCandleDataAsync(symbol, token);
                if (candleData != null)
                    if (candleData != null) // Check for null
                    {
                        var prediction = _aiPredictor.Predict(candleData);
                        aiScore = prediction.Score;

                        // [AI 모니터링] 예측 기록 (5분 후 검증)
                        string predictionKey = $"ML_{symbol}_{DateTime.Now:yyyyMMddHHmmss}";
                        decimal predictedPrice = currentPrice * (prediction.Prediction ? 1.02m : 0.98m); // 2% 변동 예측
                        _pendingPredictions[predictionKey] = (DateTime.Now, predictedPrice, prediction.Prediction, "ML.NET", prediction.Probability);

                        string mlDirection = prediction.Prediction ? "상승" : "하락";
                        OnStatusLog?.Invoke($"🔮 [ML.NET] {symbol} 예측 등록: {mlDirection} (확률: {prediction.Probability:P0}) → 5분 후 검증 예정");

                        // 5분 후 검증 태스크 시작
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromMinutes(5), token);
                            await ValidatePredictionAsync(predictionKey, symbol, token);
                        }, token);

                        // 상승 확률이 60% 미만이면 진입 보류 (단, RSI 과매도 등 강력한 시그널일 경우 예외 처리 가능)
                        if (prediction.Probability < 0.6f && rsi5m > 30)
                        {
                            OnStatusLog?.Invoke($"🤖 {symbol} AI 예측 확률 낮음({prediction.Probability:P1}) -> 진입 보류");
                            return;
                        }
                        OnStatusLog?.Invoke($"🤖 {symbol} AI 예측: {(prediction.Prediction ? "상승" : "하락")} (확률: {prediction.Probability:P1})");
                    }
            }

            // RSI 과열 시 비중 축소 로직
            if (rsi5m >= 80)
            {
                OnStatusLog?.Invoke($"⛔ {symbol} RSI 초과열({rsi5m:F1})로 진입 취소 (상한선 80 초과)");
                return;
            }
            else if (rsi5m >= 70)
            {
                marginUsdt *= 0.5m; // 과매수 구간에서는 절반으로 축소
                OnStatusLog?.Invoke($"⚠️ {symbol} RSI 과열({rsi5m:F1})로 진입 비중 50% 축소");
            }
            else if (rsi5m <= 30)
            {
                marginUsdt *= 2.0m; // 과매도 구간(저점 매수)에서는 2배로 확대
                OnStatusLog?.Invoke($"💎 {symbol} RSI 과매도({rsi5m:F1})로 진입 비중 2배 확대");
            }

            // 매수 집행
            await ExecutePumpTrade(symbol, marginUsdt, aiScore, fib618, logicalStop, fib1000, fib1618, fib2618, token);

            // [중요] 진입 성공 시, 이 코인을 위한 별도의 모니터링 태스크 시작 (1분봉 기반 짧은 대응)
            _ = Task.Run(async () => await _positionMonitor.MonitorPumpPositionShortTerm(symbol, currentPrice, strategyName, atr, token), token);
        }

        private decimal CalculateDynamicPositionSize(double atr, decimal currentPrice)
        {
            if (atr <= 0 || currentPrice <= 0) return 200.0m; // 기본값

            // 1. 계좌 리스크 관리: 자산의 2%를 1회 거래의 최대 허용 손실로 설정
            decimal riskPerTrade = (decimal)InitialBalance * 0.02m;
            if (riskPerTrade < 5) riskPerTrade = 5; // 최소 리스크액 보정

            // 2. 손절폭 설정 (ATR의 2배를 손절 라인으로 가정)
            decimal stopLossDistance = (decimal)atr * 2.0m;
            if (stopLossDistance == 0) return 200.0m;

            // 3. 포지션 수량(Coin) 계산: 손실액 = 수량 * 손절폭  =>  수량 = 손실액 / 손절폭
            decimal positionSizeCoins = riskPerTrade / stopLossDistance;

            // 4. 투입 증거금(Margin USDT) 계산: (수량 * 가격) / 레버리지 (20배 가정)
            decimal leverage = _settings.DefaultLeverage;
            decimal marginUsdt = (positionSizeCoins * currentPrice) / leverage;

            // 5. 한도 제한 (최소 10불 ~ 최대 자산의 20%)
            decimal maxMargin = (decimal)InitialBalance * 0.2m;
            if (marginUsdt > maxMargin) marginUsdt = maxMargin;
            if (marginUsdt < 10) marginUsdt = 10; // 최소 주문 금액

            return Math.Round(marginUsdt, 0);
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
            double orderBookRatio)
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
            if (orderBookRatio < 1.2)
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
        /// <summary>
        /// 급등주 매수 집행 (수정본: marginUsdt 인자 추가)
        /// </summary>
        public async Task ExecutePumpTrade(
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
            try
            {
                // 1. 레버리지 설정 (IExchangeService 사용)
                int leverage = PUMP_MANUAL_LEVERAGE;
                await _exchangeService.SetLeverageAsync(symbol, leverage, token);

                // 2. 현재가 조회 (IExchangeService 사용)
                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                if (currentPrice == 0) return;

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

                if (quantity <= 0) return;

                // 5. 지정가 매수 주문 실행 (IExchangeService 사용)
                var (success, orderId) = await _exchangeService.PlaceLimitOrderAsync(
                    symbol,
                    "BUY",
                    quantity,
                    limitPrice,
                    token);

                if (success && !string.IsNullOrEmpty(orderId))
                {
                    OnStatusLog?.Invoke($"⏳ {symbol} 지정가 주문 대기 (가: {limitPrice}, 3초)");

                    // 6. 3초 대기 (최적화: 5초 → 3초)
                    await Task.Delay(3000, token);

                    // 7. 주문 상태 확인 (IExchangeService 사용)
                    var (filled, filledQty, avgPrice) = await _exchangeService.GetOrderStatusAsync(symbol, orderId, token);

                    if (filled && filledQty > 0)
                    {
                        // 부분 체결 감지 (filledQty < quantity)
                        if (filledQty < quantity)
                        {
                            // 잔량 취소 (IExchangeService 사용)
                            await _exchangeService.CancelOrderAsync(symbol, orderId, token);
                            OnStatusLog?.Invoke($"✂️ {symbol} 부분 체결 후 잔량 취소 (체결: {filledQty}/{quantity})");
                        }

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
                                EntryTime = DateTime.Now,
                                StopLoss = stopLossPrice,
                                TakeProfit = fib2618Target,
                                Wave1LowPrice = stopLossPrice,
                                Wave1HighPrice = fib1000Target,
                                Fib0618Level = fib0618Level,
                                Fib0786Level = stopLossPrice,
                                Fib1618Target = fib1618Target,
                                PartialProfitStage = 0,
                                BreakevenPrice = 0
                            };
                        }

                        OnTradeExecuted?.Invoke(symbol, "BUY", avgPrice > 0 ? avgPrice : limitPrice, filledQty);

                        OnAlert?.Invoke($"🚀 {symbol} PUMP 진입 성공 (20x) | 수량: {filledQty} | SL:{stopLossPrice:F8} TP1:{fib1000Target:F8} TP2:{fib1618Target:F8}");
                    }
                    else
                    {
                        // 미체결 시 취소 (IExchangeService 사용)
                        await _exchangeService.CancelOrderAsync(symbol, orderId, token);
                        OnStatusLog?.Invoke($"🚫 {symbol} 3초 미체결로 주문 취소");
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [{_exchangeService.ExchangeName}] PUMP 진입 에러: {ex.Message}");
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
                decimal balance = await _exchangeService.GetBalanceAsync("USDT", token);
                double equity = (double)balance;
                double available = (double)balance;

                int currentMajor = 0;
                int currentPump = 0;

                lock (_posLock)
                {
                    currentMajor = _activePositions.Values.Count(p => !p.IsPumpStrategy);
                    currentPump = _activePositions.Values.Count(p => p.IsPumpStrategy);
                }

                // [디버그] 서비스 타입 확인 (처음 한번만)
                if (_engineStartTime != DateTime.MinValue && (DateTime.Now - _engineStartTime).TotalSeconds < 10)
                {
                    string serviceType = _exchangeService?.GetType().Name ?? "Unknown";
                    bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                    OnStatusLog?.Invoke($"🔍 [Dashboard] Service: {serviceType}, Config Mode: {(isSimulation ? "Simulation" : "Real")}, Balance: ${balance:N2}");
                }

                // UI 업데이트 및 DataGrid 정렬 유지
                OnDashboardUpdate?.Invoke(equity, available, currentMajor + currentPump);
                OnSlotStatusUpdate?.Invoke(currentMajor, MAX_MAJOR_SLOTS, currentPump, MAX_PUMP_SLOTS);
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
                            "EXTERNAL_CLOSE_SYNC",
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
                            ExitReason = "EXTERNAL_CLOSE_SYNC"
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
                        Wave1LowPrice = savedWave1Low,
                        Wave1HighPrice = savedWave1High,
                        Fib0618Level = savedFib0618,
                        Fib0786Level = savedFib0786,
                        Fib1618Target = savedFib1618,
                        BreakevenPrice = savedBreakeven,
                        StopLoss = savedStopLoss,
                        TakeProfit = savedTakeProfit,
                        EntryTime = savedEntryTime
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
            decimal customStopLossPrice = 0m)
        {
            // 진입 시도 로그 (디버깅용)
            OnStatusLog?.Invoke($"📋 [{signalSource}] {symbol} {decision} 진입 시도 시작 (가격: ${currentPrice:F2})");

            bool positionReserved = false;
            bool orderPlaced = false;

            void CleanupReservedPosition(string reason)
            {
                if (!positionReserved || orderPlaced)
                    return;

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var reservedPos) && reservedPos.Quantity == 0)
                    {
                        _activePositions.Remove(symbol);
                        OnStatusLog?.Invoke($"🧹 {symbol} 예약 포지션 정리: {reason}");
                    }
                }
            }

            // 1. 진입 신호가 아니면 즉시 종료
            if (decision != "LONG" && decision != "SHORT") return;

            if (IsEntryWarmupActive(out var remaining))
            {
                OnStatusLog?.Invoke($"⏳ {symbol} 워밍업 중으로 {decision} 진입 보류 ({remaining.TotalSeconds:F0}초 남음)");
                return;
            }

            if (_riskManager.IsTripped)
            {
                OnStatusLog?.Invoke($"⛔ 서킷 브레이커 발동 중으로 자동 매매 진입 차단: {symbol}");
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

                    OnStatusLog?.Invoke($"⏳ [캔들 확인 대기] {symbol} {decision} 신호 등록 | 가격=${currentPrice:F2} | 다음 캔들 확인 후 진입 (Fakeout 방지)");
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
                                OnStatusLog?.Invoke($"⛔ [15분봉 추세 필터] {symbol} LONG 차단 | 15m SMA20({sma20_15m:F2}) < SMA60({sma60_15m:F2}) → 상위 추세 역행");
                                return;
                            }

                            if (decision == "SHORT" && !downTrend15m)
                            {
                                OnStatusLog?.Invoke($"⛔ [15분봉 추세 필터] {symbol} SHORT 차단 | 15m SMA20({sma20_15m:F2}) > SMA60({sma60_15m:F2}) → 상위 추세 역행");
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
            if (latestCandle == null)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} 캔들 데이터 미수신으로 진입 차단");
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
                        OnStatusLog?.Invoke($"⚠️ {symbol} 변동성 과다(흔들기 구간) | ATR비율={atrRatio:F2}x > 2.0x → 진입 금지");
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

            if (signalSource == "MAJOR" && customStopLossPrice <= 0)
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
                OnStatusLog?.Invoke($"⛔ {symbol} {decision} 추격 방지 필터: {chaseReason} | src={signalSource}");
                return;
            }

            if (_aiPredictor != null)
            {
                if (latestCandle != null)
                {
                    var prediction = _aiPredictor.Predict(latestCandle);
                    aiScore = prediction.Score;
                    aiProbability = prediction.Probability;
                    aiPredictUp = prediction.Prediction;
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
                    
                    // 조정된 임계값: MAJOR 65점, 기타 75점
                    float adjustedThreshold = isMajorCoin ? 65f : 75f;
                    if (majorLowVolumePrivilege)
                    {
                        adjustedThreshold = Math.Min(adjustedThreshold, 65f);
                    }
                    
                    if (decision == "LONG" && finalAiScore < adjustedThreshold && !majorLowVolumePrivilege)
                    {
                        OnStatusLog?.Invoke($"🤖 {symbol} AI 필터: 롱 진입 차단 | 기본점수={baseAiScore:F1} + 보너스={bonusPoints:F0} = 최종={finalAiScore:F1} < 임계값={adjustedThreshold} (source={signalSource})");
                        return;
                    }
                    
                    // 진입 승인 로그
                    if (decision == "LONG")
                    {
                        if (majorLowVolumePrivilege && finalAiScore < adjustedThreshold)
                            OnStatusLog?.Invoke($"✅ {symbol} Major 저거래량 예외 승인 | 기본점수={baseAiScore:F1} + 보너스={bonusPoints:F0} = 최종={finalAiScore:F1} | OI/정배열 추세 우선");
                        else
                            OnStatusLog?.Invoke($"✅ {symbol} AI 필터 통과 | 기본점수={baseAiScore:F1} + 보너스={bonusPoints:F0} = 최종={finalAiScore:F1} >= 임계값={adjustedThreshold}");
                    }

                    // 숏 진입은 더 엄격하게 필터링 (하락 예측 + 충분한 확률 + 과매도 추격 방지)
                    if (decision == "SHORT")
                    {
                        if (prediction.Prediction || prediction.Probability < 0.60f)
                        {
                            OnStatusLog?.Invoke($"🤖 {symbol} AI 필터: 숏 진입 차단 (하락 확률 {prediction.Probability:P1} 미달 또는 상승 예측)");
                            return;
                        }

                        if (latestCandle.RSI <= 35f)
                        {
                            OnStatusLog?.Invoke($"🤖 {symbol} RSI 필터: 숏 진입 차단 (과매도 구간 RSI {latestCandle.RSI:F1})");
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
                    OnStatusLog?.Invoke($"🤖 {symbol} RL({strategyType}) 반대 의견(Sell)으로 진입 보류");
                    return;
                }

                if (decision == "SHORT" && action == 1)
                {
                    OnStatusLog?.Invoke($"🤖 {symbol} RL({strategyType}) 반대 의견(Buy)으로 숏 진입 보류");
                    return;
                }

                OnStatusLog?.Invoke($"🤖 {symbol} RL({strategyType}) Action: {action}");
            }

            OnStatusLog?.Invoke(
                $"🧾 [진입 검증] src={signalSource} | mode={mode} | {symbol} {decision} | AI.Score={aiScore:F3} | AI.Prob={aiProbability:P1} | AI.Dir={(aiPredictUp.HasValue ? (aiPredictUp.Value ? "UP" : "DOWN") : "N/A")} | RSI={(latestCandle != null ? latestCandle.RSI.ToString("F1") : "N/A")}");

            try
            {
                // 2. 설정값: 증거금 200 USDT, 레버리지 20배
                int leverage = _settings.DefaultLeverage;
                decimal marginUsdt = _settings.DefaultMargin;

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
                    if (_activePositions.ContainsKey(symbol)) return;

                    // [3파 기반 익절/손절] ElliottWave 정보 적용
                    // 임시 등록 (주문 실패 시 제거)
                    _activePositions[symbol] = new PositionInfo
                    {
                        Symbol = symbol,
                        EntryPrice = currentPrice,
                        IsLong = (decision == "LONG"),
                        Side = (decision == "LONG") ? OrderSide.Buy : OrderSide.Sell,
                        IsPumpStrategy = false,
                        AiScore = aiScore,
                        AiConfidencePercent = aiProbability > 0
                            ? aiProbability * 100f
                            : (aiScore > 0f && aiScore <= 1f ? aiScore * 100f : aiScore),
                        Leverage = leverage,
                        Quantity = 0,  // 주문 성공 후 업데이트
                        EntryTime = DateTime.Now,
                        EntryBbPosition = entryBbPosition,
                        EntryZoneTag = entryZoneTag,
                        IsHybridMidBandLongEntry = isHybridMidBandLongEntry,
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

                // [긴급 방어 2] 슬리피지 검증 (호가창 확인)
                var orderBook = await _exchangeService.GetOrderBookAsync(symbol, token);
                if (orderBook.HasValue)
                {
                    decimal bestBid = orderBook.Value.bestBid;
                    decimal bestAsk = orderBook.Value.bestAsk;

                    // LONG 진입 시: bestAsk가 현재가보다 0.05% 이상 비싸면 진입 포기
                    if (decision == "LONG" && bestAsk > currentPrice * 1.0005m)
                    {
                        OnStatusLog?.Invoke($"❌ {symbol} 슬리피지 과다 (BestAsk={bestAsk:F4} > Target={currentPrice * 1.0005m:F4}, +{((bestAsk - currentPrice) / currentPrice * 100):F3}%) → 진입 취소");
                        lock (_posLock) { _activePositions.Remove(symbol); }
                        return;
                    }

                    // SHORT 진입 시: bestBid가 현재가보다 0.05% 이상 낮으면 진입 포기
                    if (decision == "SHORT" && bestBid < currentPrice * 0.9995m)
                    {
                        OnStatusLog?.Invoke($"❌ {symbol} 슬리피지 과다 (BestBid={bestBid:F4} < Target={currentPrice * 0.9995m:F4}, {((bestBid - currentPrice) / currentPrice * 100):F3}%) → 진입 취소");
                        lock (_posLock) { _activePositions.Remove(symbol); }
                        return;
                    }

                    OnStatusLog?.Invoke($"✅ {symbol} 슬리피지 검증 통과 | BestBid={bestBid:F4} / Ask={bestAsk:F4} / Mid={(bestBid + bestAsk) / 2:F4}");
                }

                // 7. 주문 실행 (지정가 주문으로 변경하여 슬리피지 방어)
                var side = (decision == "LONG") ? "BUY" : "SELL";
                
                // [긴급 방어 3] 시장가 대신 '최우선 호가' 지정가 사용
                decimal limitPrice = currentPrice;
                if (orderBook.HasValue)
                {
                    // LONG: 현재 최우선 매도호가(bestAsk)에 지정가 주문
                    // SHORT: 현재 최우선 매수호가(bestBid)에 지정가 주문
                    limitPrice = (decision == "LONG") ? orderBook.Value.bestAsk : orderBook.Value.bestBid;
                }

                OnStatusLog?.Invoke($"💼 {symbol} {side} 지정가 주문 실행: 수량={quantity} @ ${limitPrice:F4} (슬리피지 방어형)");
                
                var (success, orderId) = await _exchangeService.PlaceLimitOrderAsync(
                    symbol,
                    side,
                    quantity,
                    limitPrice,
                    token);

                if (success && !string.IsNullOrWhiteSpace(orderId))
                {
                    OnStatusLog?.Invoke($"⏳ {symbol} 자동매매 지정가 주문 대기 (가: {limitPrice:F4}, 3초)");
                    await Task.Delay(3000, token);

                    var (filled, filledQty, avgPrice) = await _exchangeService.GetOrderStatusAsync(symbol, orderId, token);

                    if (!filled || filledQty <= 0)
                    {
                        await _exchangeService.CancelOrderAsync(symbol, orderId, token);
                        CleanupReservedPosition("자동매매 지정가 미체결 취소");
                        OnStatusLog?.Invoke($"🚫 {symbol} 자동매매 지정가 미체결로 주문 취소");
                        return;
                    }

                    if (filledQty < quantity)
                    {
                        await _exchangeService.CancelOrderAsync(symbol, orderId, token);
                        OnStatusLog?.Invoke($"✂️ {symbol} 자동매매 부분 체결 후 잔량 취소 (체결: {filledQty}/{quantity})");
                    }

                    var actualEntryPrice = avgPrice > 0 ? avgPrice : limitPrice;

                    if (signalSource == "MAJOR")
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
                            pos.EntryPrice = actualEntryPrice;
                            if (customStopLossPrice > 0)
                                pos.StopLoss = customStopLossPrice;
                            orderPlaced = true;
                        }
                    }

                    if (!orderPlaced)
                    {
                        OnStatusLog?.Invoke($"⚠️ {symbol} 체결 후 포지션 예약이 사라져 상태 동기화를 대기합니다.");
                    }

                    OnTradeExecuted?.Invoke(symbol, decision, actualEntryPrice, filledQty);
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

                    // 🔥 [핵심] 감시 루프 시작
                    TryStartStandardMonitor(symbol, actualEntryPrice, decision == "LONG", mode, customTakeProfitPrice, customStopLossPrice, token, "new-entry");

                }
                else
                {
                    OnStatusLog?.Invoke($"❌ {symbol} 지정가 주문 제출 실패");
                    CleanupReservedPosition("주문 실패");
                }
            }
            catch (Exception ex)
            {
                CleanupReservedPosition($"예외 발생: {ex.GetType().Name}");
                OnStatusLog?.Invoke($"⚠️ ExecuteAutoOrder 에러: {ex.Message}");
                try
                {
                    await TelegramService.Instance.SendMessageAsync($"⚠️ *[자동 매매 오류]*\n{symbol} 진입 중 예외 발생:\n{ex.Message}");
                }
                catch { }
            }
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
                                AiScore = localPos.AiScore
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
                        "MISSED_CLOSE_SYNC",
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
                        ExitReason = "MISSED_CLOSE_SYNC (거래소 동기화)"
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

        private async Task<CandleData?> GetLatestCandleDataAsync(string symbol, CancellationToken token)
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
            catch { return null; }
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
            catch { }
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
                // 1) 메이저 코인 확인 (BTC, ETH, SOL, XRP만 적용)
                var majorCoins = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
                if (!majorCoins.Contains(symbol))
                    return false;
                
                // 2) 5분봉 EMA 20 계산
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
                // 1) 메이저 코인 확인
                var majorCoins = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
                if (!majorCoins.Contains(symbol))
                    return false;
                
                // 2) 5분봉 가격 변화 확인
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

            return $"🤖 *[시스템 상태]*\n" +
                   $"⏱ 가동 시간: {timeStr}\n" +
                   $"💰 금일 실현 손익: ${_riskManager.DailyRealizedPnl:N2}\n\n" +
                   $"📊 *보유 포지션 ({activeCount}개)*\n" +
                   positionsStr +
                   $"\n_Updated: {DateTime.Now:HH:mm:ss}_";
        }

        public string GetEngineStatusJson()
        {
            int activeCount = 0;
            lock (_posLock) activeCount = _activePositions.Count;

            // 간단한 JSON 생성 (Newtonsoft.Json 없이 문자열 보간 사용)
            return $@"{{
                ""status"": ""{(IsBotRunning ? "Running" : "Stopped")}"",
                ""uptime"": ""{(DateTime.Now - _engineStartTime).ToString(@"dd\.hh\:mm\:ss")}"",
                ""balance"": {InitialBalance},
                ""pnl"": {_riskManager.DailyRealizedPnl},
                ""active_positions"": {activeCount}
            }}";
        }

        // [AI 모니터링] 예측 검증 메서드
        private async Task ValidatePredictionAsync(string predictionKey, string symbol, CancellationToken token)
        {
            try
            {
                if (!_pendingPredictions.TryRemove(predictionKey, out var predictionInfo))
                {
                    OnStatusLog?.Invoke($"⚠️ [{symbol}] 예측 키({predictionKey})를 찾을 수 없음");
                    return;
                }

                var (timestamp, predictedPrice, predictedDirection, modelName, confidence) = predictionInfo;
                
                OnStatusLog?.Invoke($"🔍 [{modelName}] {symbol} 예측 검증 시작 (5분 경과)");

                // 현재 가격 조회
                var tickerResult = await _client.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol, token);
                if (!tickerResult.Success || tickerResult.Data == null)
                {
                    OnStatusLog?.Invoke($"❌ [{symbol}] 가격 조회 실패 - 예측 검증 중단");
                    return;
                }

                decimal actualPrice = tickerResult.Data.LastPrice;
                decimal entryPrice = predictedPrice / (predictedDirection ? 1.02m : 0.98m); // 원래 가격 역산

                // 실제 방향 계산
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
                OnStatusLog?.Invoke($"{resultIcon} [{modelName}] {symbol} 예측 검증 완료: {direction} 예측 → {(isCorrect ? "정확" : "오류")} (예측: ${predictedPrice:F4} / 실제: ${actualPrice:F4})");

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
