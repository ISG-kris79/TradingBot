using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Authentication;
using Binance.Net.Objects.Models.Futures.Socket;
using System.Collections.Concurrent;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Strategies;
using System.Threading.Channels; // [Agent 3] 추가

namespace TradingBot
{
    public class TradingEngine
    {
        public bool IsBotRunning { get; private set; } = false;
        private readonly IBinanceRestClient _client;
        private CancellationTokenSource _cts;
        private readonly string apiKey;
        private readonly string apiSecret;

        // 감시 종목 리스트
        private readonly List<string> _symbols;

        private decimal _initialBalance = 0; // 프로그램 시작 시점의 자산
        private DateTime _engineStartTime = DateTime.Now;
        private DateTime _lastReportTime = DateTime.MinValue;

        // 현재 보유 중인 포지션 정보 (심볼, 진입가)
        private Dictionary<string, PositionInfo> _activePositions = new Dictionary<string, PositionInfo>();
        private readonly object _posLock = new object();
        // 블랙리스트 (심볼, 해제시간) - 지루함 청산 종목 재진입 방지
        private Dictionary<string, DateTime> _blacklistedSymbols = new Dictionary<string, DateTime>();
        // 슬롯 설정
        private const int MAX_MAJOR_SLOTS = 4; // BTC, ETH, SOL, XRP 전용
        private const int MAX_PUMP_SLOTS = 2;  // 실시간 급등주 전용

        // 전략 인스턴스
        private PumpScanStrategy? _pumpStrategy;
        private MajorCoinStrategy? _majorStrategy;
        private GridStrategy _gridStrategy;
        private ArbitrageStrategy _arbitrageStrategy;
        private NewsSentimentService _newsService;
        
        private IExchangeService _exchangeService;
        
        private BinanceOrderService _orderService;
        
        private PositionMonitorService _positionMonitor;

        private SoundService _soundService;

        private WebServerService _webServer;

        private NotificationService _notificationService;

        private DbManager _dbManager;

        private ConcurrentDictionary<string, DateTime> _lastTickerUpdateTimes = new ConcurrentDictionary<string, DateTime>();

        private ConcurrentDictionary<string, DateTime> _lastAnalysisTimes = new ConcurrentDictionary<string, DateTime>();

        private DateTime _lastCleanupTime = DateTime.Now;

        private Channel<IBinance24HPrice> _tickerChannel;

        private readonly MarketDataManager _marketDataManager;
        private readonly RiskManager _riskManager;
        private AIPredictor? _aiPredictor;
        private RLAgent _rlAgent;
        
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

        private DateTime _lastHeartbeatTime = DateTime.MinValue;

        public TradingEngine()
        {
            _cts = new CancellationTokenSource();

            apiKey = AppConfig.BinanceApiKey;
            apiSecret = AppConfig.BinanceApiSecret;

            LoggerService.Initialize();

            // 실제 사용 시 API Key와 Secret을 입력하세요. 조회 전용은 Key 없이도 일부 가능합니다.
            // v12.x 초기화 방식
            _client = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            });
            
            bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;

            if (isSimulation)
            {
                // 가상 거래소 서비스 주입 (초기 자본 10,000 USDT)
                _exchangeService = new MockExchangeService(10000);
                OnStatusLog?.Invoke("🎮 [Simulation Mode] 가상 거래소 서비스가 활성화되었습니다.");
            }
            else
            {
                var selectedExchange = AppConfig.Current?.Trading?.SelectedExchange ?? ExchangeType.Binance;
                switch (selectedExchange)
                {
                    case ExchangeType.Bybit:
                        _exchangeService = new BybitExchangeService(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                        break;
                    case ExchangeType.Bitget:
                        _exchangeService = new BitgetExchangeService(AppConfig.BitgetApiKey, AppConfig.BitgetApiSecret, AppConfig.BitgetPassphrase);
                        break;
                    case ExchangeType.Binance:
                    default:
                        _exchangeService = new BinanceExchangeService(apiKey, apiSecret);
                        break;
                }
            }
            
            _orderService = new BinanceOrderService(_client);
            _settings = AppConfig.Current?.Trading?.GeneralSettings ?? new TradingSettings();

            _symbols = AppConfig.Current?.Trading?.Symbols ?? new List<string>();
            if (_symbols.Count == 0)
            {
                _symbols.AddRange(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" });
            }

            _soundService = new SoundService();

            _newsService = new NewsSentimentService();
            _arbitrageStrategy = new ArbitrageStrategy(_client);

            _webServer = new WebServerService(() => GetEngineStatusJson());

            _notificationService = new NotificationService(""); 

            _riskManager = new RiskManager();
            _marketDataManager = new MarketDataManager(_client, _symbols);
            
            _tickerChannel = Channel.CreateBounded<IBinance24HPrice>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest // 처리 속도가 느리면 오래된 티커 드랍
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
                _settings
            );
            

            _riskManager.OnTripped += (reason) => 
            {
                string msg = $"⛔ [서킷 브레이커] {reason}! 매매 중단 모드로 전환합니다.";
                OnAlert?.Invoke(msg);
                _soundService.PlayAlert();
                _notificationService.SendPushNotificationAsync("Circuit Breaker", msg);
            };
            _marketDataManager.OnLog += (msg) => 
            {
                OnStatusLog?.Invoke(msg);
                LoggerService.Info(msg);
            };
            _marketDataManager.OnAccountUpdate += HandleAccountUpdate;
            _marketDataManager.OnOrderUpdate += (data) => _positionMonitor.HandleOrderUpdate(data);
            _marketDataManager.OnTickerUpdate += HandleTickerUpdate;

            _positionMonitor.OnLog += msg => OnStatusLog?.Invoke(msg);
            _positionMonitor.OnAlert += msg => OnAlert?.Invoke(msg);
            _positionMonitor.OnTickerUpdate += (s, p, r) => 
            {
                OnTickerUpdate?.Invoke(s, p, r);
            };
            _positionMonitor.OnPositionStatusUpdate += (s, a, p) => OnPositionStatusUpdate?.Invoke(s, a, p);

            _rlAgent = new RLAgent();
            

            // [AI 초기화]
            try
            {
                _aiPredictor = new AIPredictor();
                OnStatusLog?.Invoke("🧠 AI 예측 모델 로드 완료");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ AI 모델 로드 실패 (학습 후 사용 가능): {ex.Message}");
            }

            _pumpStrategy = new PumpScanStrategy(_client, _symbols, AppConfig.Current?.Trading?.PumpSettings ?? new PumpScanSettings());
            _majorStrategy = new MajorCoinStrategy(_marketDataManager);
            _gridStrategy = new GridStrategy(_client, _orderService);

            _pumpStrategy.OnSignalAnalyzed += vm => OnSignalUpdate?.Invoke(vm);
            _pumpStrategy.OnPumpDetected += async (symbol, price, decision, rsi, atr) =>
            {
                await HandlePumpEntry(symbol, price, decision, rsi, atr, _cts.Token);
            };

            _pumpStrategy.OnLog += msg => OnLiveLog?.Invoke(msg);

            if (_majorStrategy != null)
            {
                _majorStrategy.OnSignalAnalyzed += vm => OnSignalUpdate?.Invoke(vm);
                _majorStrategy.OnTradeSignal += async (symbol, decision, price) => await ExecuteAutoOrder(symbol, decision, price, _cts.Token);
            }
        }

        // 초기 자산 저장 전용 메서드
        private async Task InitializeSeedAsync(CancellationToken token)
        {
            try
            {
                decimal balance = await _exchangeService.GetBalanceAsync("USDT", token);
                if (balance > 0)
                {
                    _initialBalance = balance;
                    OnLiveLog?.Invoke($"💰 초기 시드 설정 완료: ${_initialBalance:N2}");

                    // 텔레그램으로도 시작 알림을 보낼 수 있습니다.
                    await TelegramService.Instance.SendMessageAsync($"봇 가동 시작\n초기 자산: ${_initialBalance:N2} USDT");
                }
            }
            catch
            {
                OnStatusLog?.Invoke("❌ 초기 잔고 조회 실패. 수익률 계산이 정확하지 않을 수 있습니다.");
            }
        }

        private async Task SendDetailedPeriodicReport(CancellationToken token)
        {
            try
            {
                var balanceRes = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: token);
                if (!balanceRes.Success || balanceRes.Data == null) return;

                var usdt = balanceRes.Data.FirstOrDefault(b => b.Asset == "USDT");
                if (usdt == null) return;

                decimal currentBalance = usdt.WalletBalance;

                // 초기 자산이 0인 경우(설정 실패 등) 현재 잔고를 초기 자산으로 임시 대체하여 에러 방지
                decimal initial = _initialBalance > 0 ? _initialBalance : currentBalance;

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

                await TelegramService.Instance.SendFormattedMessage("📊 정기 수익 보고", body);
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
                    lock (_posLock)
                    {
                        _activePositions.Clear();
                        foreach (var pos in positions)
                        {
                            _activePositions[pos.Symbol] = new PositionInfo
                            {
                                EntryPrice = pos.EntryPrice,
                                IsLong = pos.IsLong,
                                Side = pos.Side,
                                AiScore = 0, // 재시작 시 AI 점수 정보는 소실됨
                                Leverage = _settings.DefaultLeverage
                            };

                            OnSignalUpdate?.Invoke(new MultiTimeframeViewModel { Symbol = pos.Symbol, IsPositionActive = true, EntryPrice = pos.EntryPrice });

                            // 롱/숏 상태를 로그에 표시 (선택 사항)
                            string sideStr = pos.Quantity > 0 ? "LONG" : "SHORT";
                            OnStatusLog?.Invoke($"[동기화] {pos.Symbol} {sideStr} | 평단: {pos.EntryPrice}");
                        }
                    }
                    OnStatusLog?.Invoke("✅ 현재 보유 포지션 동기화 완료");
                }
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ 포지션 동기화 에러: {ex.Message}"); }
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

                // 1. 초기화 (지갑 잔고 및 현재 포지션 동기화)
                await InitializeSeedAsync(token); // _initialBalance 설정
                _riskManager.Initialize(_initialBalance); // RiskManager 초기화
                
                // 시뮬레이션 모드일 경우 초기 잔고 로그 출력
                if (AppConfig.Current?.Trading?.IsSimulationMode == true)
                {
                    OnLiveLog?.Invoke($"🎮 시뮬레이션 시작 잔고: ${_initialBalance:N2}");
                }

                await SyncCurrentPositionsAsync(token);

                TelegramService.Instance.OnRequestStatus = GetEngineStatusReport;
                TelegramService.Instance.OnRequestStop = StopEngine;
                TelegramService.Instance.StartReceiving();

                _webServer.Start(8080);

                _ = ProcessTickerChannelAsync(token);

                // 2. 실시간 감시 시작 (Non-blocking)
                await _marketDataManager.StartAsync(token);
                _ = StartPeriodicTrainingAsync(token);

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
                            await TelegramService.Instance.SendMessageAsync(resumeMsg);
                            OnAlert?.Invoke(resumeMsg);
                            OnStatusLog?.Invoke("서킷 브레이커 자동 해제됨.");
                        }
                        else
                        {
                            if (!isCircuitBreakerNotificationSent)
                            {
                                isCircuitBreakerNotificationSent = true;
                                string msg = $"⛔ [서킷 브레이커 발동]\n{_riskManager.GetTripDetails()}\n\n모든 매매를 일시 중단합니다.";
                                await TelegramService.Instance.SendMessageAsync(msg);
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
                            await TelegramService.Instance.SendMessageAsync(heartbeatMsg);
                            LoggerService.Info("Heartbeat sent.");
                            _lastHeartbeatTime = DateTime.Now;
                        }
                        
                    // [B] 텔레그램 정기 수익 보고 (1시간 주기)
                    if ((DateTime.Now - _lastReportTime).TotalHours >= 1)
                    {
                        await SendDetailedPeriodicReport(token);
                        _lastReportTime = DateTime.Now;
                    }

                    if ((DateTime.Now - _lastCleanupTime).TotalHours >= 4)
                    {
                        _marketDataManager.TickerCache.Clear(); // 오래된 캐시 정리
                        _lastAnalysisTimes.Clear(); // 분석 타임스탬프 정리
                        GC.Collect(); // 강제 GC (장시간 구동 시 메모리 파편화 방지)
                        _lastCleanupTime = DateTime.Now;
                        OnStatusLog?.Invoke("🧹 시스템 메모리 정리 및 캐시 초기화 완료");
                    }

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
                _webServer.Stop();
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
                        // 2. 모델 학습 및 저장
                        var trainer = new ModelTrainer();
                        trainer.TrainAndSave(trainingData);
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

        private List<CandleData> ConvertToTrainingData(List<IBinanceKline> klines, string symbol)
        {
            var result = new List<CandleData>();
            if (klines.Count < 50) return result;

            // 과거 데이터에 대해 지표 계산 및 라벨링 수행
            // i는 35부터 시작 (MACD 계산 최소 샘플 확보), 마지막 하나는 라벨용으로 제외
            for (int i = 35; i < klines.Count - 1; i++)
            {
                var subset = klines.GetRange(0, i + 1);

                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50); // 최근 50봉 기준 고점/저점 피보나치

                var current = klines[i];
                var next = klines[i + 1];

                // 실시간 예측 시에는 GetLatestCandleDataAsync에서 채워짐
                float sentiment = 0; 

                // 라벨: 다음 봉 종가가 현재 봉 종가보다 높으면 True(상승)
                bool label = next.ClosePrice > current.ClosePrice;

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = (decimal)current.OpenPrice,
                    High = (decimal)current.HighPrice,
                    Low = (decimal)current.LowPrice,
                    Close = (decimal)current.ClosePrice,
                    Volume = (float)current.Volume,
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
                    SentimentScore = sentiment,
                    Label = label
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
                decimal calculatedPnl = 0;
                if (pos.IsLong)
                {
                    calculatedPnl = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100;
                }
                else
                {
                    calculatedPnl = (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100;
                }
                pnl = (double)Math.Round(calculatedPnl, 2);
            }

            OnTickerUpdate?.Invoke(symbol, currentPrice, pnl);
        }
        private async Task ProcessCoinAndTradeBySymbolAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
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

            }
            catch { /* 로그 생략 */ }
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
            bool isHolding = false;
            int currentHoldCount = 0;

            lock (_posLock)
            {
                isHolding = _activePositions.ContainsKey(symbol);
                currentHoldCount = _activePositions.Count;
            }

            // 이미 보유 중이거나 포지션이 2개 꽉 찼으면 진입 안 함
            if (isHolding || currentHoldCount >= 2) return;

            decimal marginUsdt = CalculateDynamicPositionSize(atr, currentPrice);

            if (_riskManager.IsTripped) return;

            float aiScore = 0;
            if (_aiPredictor != null)
            {
                var candleData = await GetLatestCandleDataAsync(symbol, token);
                if (candleData != null)
                if (candleData != null) // Check for null
                {
                    var prediction = _aiPredictor.Predict(candleData);
                    aiScore = prediction.Score;
                    // 상승 확률이 60% 미만이면 진입 보류 (단, RSI 과매도 등 강력한 시그널일 경우 예외 처리 가능)
                    if (prediction.Probability < 0.6f && rsi > 30) 
                    {
                        OnStatusLog?.Invoke($"🤖 {symbol} AI 예측 확률 낮음({prediction.Probability:P1}) -> 진입 보류");
                        return;
                    }
                    OnStatusLog?.Invoke($"🤖 {symbol} AI 예측: {(prediction.Prediction ? "상승" : "하락")} (확률: {prediction.Probability:P1})");
                }
            }

            // RSI 과열 시 비중 축소 로직
            if (rsi >= 80)
            {
                OnStatusLog?.Invoke($"⛔ {symbol} RSI 초과열({rsi:F1})로 진입 취소 (상한선 80 초과)");
                return;
            }
            else if (rsi >= 70)
            {
                marginUsdt *= 0.5m; // 과매수 구간에서는 절반으로 축소
                OnStatusLog?.Invoke($"⚠️ {symbol} RSI 과열({rsi:F1})로 진입 비중 50% 축소");
            }
            else if (rsi <= 30)
            {
                marginUsdt *= 2.0m; // 과매도 구간(저점 매수)에서는 2배로 확대
                OnStatusLog?.Invoke($"💎 {symbol} RSI 과매도({rsi:F1})로 진입 비중 2배 확대");
            }

            // 매수 집행
            await ExecutePumpTrade(symbol, marginUsdt, aiScore, token);

            // [중요] 진입 성공 시, 이 코인을 위한 별도의 모니터링 태스크 시작 (1분봉 기반 짧은 대응)
            _ = Task.Run(async () => await _positionMonitor.MonitorPumpPositionShortTerm(symbol, currentPrice, strategyName, atr, token), token);
        }

        private decimal CalculateDynamicPositionSize(double atr, decimal currentPrice)
        {
            if (atr <= 0 || currentPrice <= 0) return 200.0m; // 기본값
            
            // 1. 계좌 리스크 관리: 자산의 2%를 1회 거래의 최대 허용 손실로 설정
            decimal riskPerTrade = _initialBalance * 0.02m;
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
            decimal maxMargin = _initialBalance * 0.2m;
            if (marginUsdt > maxMargin) marginUsdt = maxMargin;
            if (marginUsdt < 10) marginUsdt = 10; // 최소 주문 금액

            return Math.Round(marginUsdt, 0);
        }

        private void UpdateUIPnl(string symbol, double roe)
        {
            OnTickerUpdate?.Invoke(symbol, 0, roe); // Price 0 means ignore price update, just update PnL if needed, or better pass 0 and handle in VM
        }
        /// <summary>
        /// 급등주 매수 집행 (수정본: marginUsdt 인자 추가)
        /// </summary>
        public async Task ExecutePumpTrade(string symbol, decimal marginUsdt, float aiScore, CancellationToken token)
        {
            try
            {
                // 1. 설정값 정의
                int leverage = _settings.DefaultLeverage;
                // 이제 고정값 대신 매개변수로 받은 marginUsdt를 사용합니다.

                // 2. 레버리지 및 마진 모드 설정
                await _exchangeService.SetLeverageAsync(symbol, leverage, token);
                // MarginType 설정은 인터페이스에 없으므로 생략하거나 추가 필요

                // 3. 현재가 조회
                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                if (currentPrice == 0) return;

                decimal limitPrice = currentPrice * 0.998m;

                // 4. 수량(Quantity) 계산 (지정가 기준)
                decimal quantity = (marginUsdt * leverage) / limitPrice;

                // 5. 바이낸스 수량/가격 규격 보정
                var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: token);
                var symbolData = exchangeInfo.Data?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;

                    decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.01m;
                    limitPrice = Math.Floor(limitPrice / tickSize) * tickSize;
                }

                if (quantity <= 0) return;

                // 6. 지정가 매수 주문 실행 (Limit)
                var orderResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    OrderSide.Buy,
                    FuturesOrderType.Limit,
                    quantity: quantity,
                    price: limitPrice,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: token);

                if (orderResult.Success)
                {
                    long orderId = orderResult.Data.Id;
                    OnStatusLog?.Invoke($"⏳ {symbol} 지정가 주문 대기 (가: {limitPrice}, 5초)");

                    // 7. 5초 대기
                    await Task.Delay(5000, token);

                    // 8. 주문 상태 확인
                    var orderCheck = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, orderId, ct: token);
                    if (orderCheck.Success)
                    {
                        var status = orderCheck.Data.Status;
                        decimal filledQty = orderCheck.Data.QuantityFilled;

                        if (status == OrderStatus.Filled || (status == OrderStatus.PartiallyFilled && filledQty > 0))
                        {
                            // 부분 체결 시 잔량 취소
                            if (status == OrderStatus.PartiallyFilled)
                            {
                                await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId, ct: token);
                                OnStatusLog?.Invoke($"✂️ {symbol} 부분 체결 후 잔량 취소");
                            }

                            lock (_posLock)
                            {
                                _activePositions[symbol] = new PositionInfo
                                {
                                    EntryPrice = orderCheck.Data.AveragePrice > 0 ? orderCheck.Data.AveragePrice : limitPrice,
                                    IsLong = true,
                                    Side = OrderSide.Buy,
                                    IsPumpStrategy = true,
                                    AiScore = aiScore,
                                    Leverage = leverage,
                                    Quantity = filledQty
                                };
                            }

                            OnTradeExecuted?.Invoke(symbol, "BUY", orderCheck.Data.AveragePrice, filledQty);

                            OnAlert?.Invoke($"🚀 {symbol} 진입 성공 (지정가) | 수량: {filledQty}");
                        }
                        else
                        {
                            // 미체결 시 취소
                            await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId, ct: token);
                            OnStatusLog?.Invoke($"🚫 {symbol} 5초 미체결로 주문 취소");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ 진입 에러: {ex.Message}");
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
                double equity = (double)balance; // 미실현 손익 포함 여부는 거래소 API에 따라 다름
                double available = (double)balance;

                int currentMajor = 0;
                int currentPump = 0;

                lock (_posLock)
                {
                    currentMajor = _activePositions.Values.Count(p => !p.IsPumpStrategy);
                    currentPump = _activePositions.Values.Count(p => p.IsPumpStrategy);
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

        private void HandleAccountUpdate(BinanceFuturesStreamAccountUpdate accountUpdate)
        {
            foreach (var pos in accountUpdate.UpdateData.Positions)
            {
                lock (_posLock)
                {
                    if (pos.Quantity == 0)
                    {
                        _activePositions.Remove(pos.Symbol);
                        OnPositionStatusUpdate?.Invoke(pos.Symbol, false, 0); // UI 및 데이터 정리
                    }
                    else
                    {
                        bool isLong = pos.Quantity > 0;

                        int savedStep = 0;
                        bool savedPump = false;
                        bool savedAvg = false;
                        if (_activePositions.TryGetValue(pos.Symbol, out var existing))
                        {
                            savedStep = existing.TakeProfitStep;
                            savedPump = existing.IsPumpStrategy;
                            savedAvg = existing.IsAveragedDown;
                        }

                        _activePositions[pos.Symbol] = new PositionInfo
                        {
                            EntryPrice = pos.EntryPrice,
                            IsLong = isLong,
                            Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                            TakeProfitStep = savedStep,
                            IsPumpStrategy = savedPump,
                            IsAveragedDown = savedAvg,
                            AiScore = existing?.AiScore ?? 0,
                            Leverage = existing?.Leverage ?? _settings.DefaultLeverage,
                            Quantity = pos.Quantity // Set quantity from account update
                        };

                        OnSignalUpdate?.Invoke(new MultiTimeframeViewModel { Symbol = pos.Symbol, IsPositionActive = true, EntryPrice = pos.EntryPrice });
                    }
                }
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

            // TryWrite를 사용하여 채널이 꽉 찼을 때 블로킹되지 않도록 함
            _tickerChannel.Writer.TryWrite(tick);
        }

        #endregion

        #region [ 자동 주문 및 자산 배분 로직 ]

        /// <summary>
        /// 자동 매매 실행 메인 메서드
        /// </summary>
        private async Task ExecuteAutoOrder(string symbol, string decision, decimal currentPrice, CancellationToken token)
        {
            // 1. 진입 신호가 아니면 즉시 종료
            if (decision != "LONG" && decision != "SHORT") return;

            if (_riskManager.IsTripped)
            {
                OnStatusLog?.Invoke($"⛔ 서킷 브레이커 발동 중으로 자동 매매 진입 차단: {symbol}");
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
                    // 롱 진입인데 AI가 하락을 강하게 예측하거나 확률이 낮으면 차단
                    if (decision == "LONG" && prediction.Probability < 0.55f)
                    {
                        OnStatusLog?.Invoke($"🤖 {symbol} AI 필터: 롱 진입 차단 (상승 확률 {prediction.Probability:P1} 미달)");
                        return;
                    }
                }
            }

            // 현재 상태(State) 구성: RSI, MACD 등
            var latestCandle = await GetLatestCandleDataAsync(symbol, token);
            if (latestCandle != null)
            {
                string state = _rlAgent.GetStateKey(latestCandle.RSI, latestCandle.MACD, latestCandle.BollingerUpper - latestCandle.BollingerLower);
                int action = _rlAgent.GetAction(state); // 0:Hold, 1:Buy, 2:Sell

                // RL이 반대 방향을 강력히 권장하면 진입 보류 (예: 롱 진입인데 Sell 액션)
                if (decision == "LONG" && action == 2)
                {
                    OnStatusLog?.Invoke($"🤖 {symbol} RL 에이전트 반대 의견(Sell)으로 진입 보류");
                    return;
                }
                // RL 점수를 AI Score에 가산하거나 로깅
                OnStatusLog?.Invoke($"🤖 {symbol} RL Action: {action} (State: {state})");
            }

            try
            {
                // 2. 설정값: 증거금 200 USDT, 레버리지 20배
                int leverage = _settings.DefaultLeverage;
                decimal marginUsdt = _settings.DefaultMargin;

                // 3. 현재 보유 중인지 체크 (중복 진입 방지)
                lock (_posLock)
                {
                    if (_activePositions.ContainsKey(symbol)) return;
                }

                // 4. 레버리지 및 마진 모드(격리) 설정
                await _exchangeService.SetLeverageAsync(symbol, leverage, token);

                // 수량 계산: (증거금 * 레버리지) / 현재가
                decimal quantity = (marginUsdt * leverage) / currentPrice;

                // 6. 거래소 규격(StepSize)에 맞게 수량 보정
                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(ct: token);
                var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                }

                if (quantity <= 0) return;

                // 7. 주문 실행 (LONG 이면 Buy, SHORT 이면 Sell)
                var side = (decision == "LONG") ? "BUY" : "SELL";
                bool success = await _exchangeService.PlaceOrderAsync(
                    symbol,
                    side,
                    quantity: quantity,
                    ct: token);

                if (success)
                {
                    lock (_posLock)
                    {
                        _activePositions[symbol] = new PositionInfo
                        {
                            EntryPrice = currentPrice,
                            IsLong = (decision == "LONG"),
                            Side = (decision == "LONG") ? OrderSide.Buy : OrderSide.Sell,
                            IsPumpStrategy = false, // 메이저 전략이므로 false
                            AiScore = aiScore,
                            Leverage = leverage,
                            Quantity = quantity
                        };

                        OnTradeExecuted?.Invoke(symbol, decision, currentPrice, quantity);
                    }
                    OnAlert?.Invoke($"🤖 자동 매매 진입: {symbol} [{decision}] | 증거금: {marginUsdt}U");
                    _soundService.PlaySuccess();

                    // 🔥 [핵심] 감시 루프 시작 (Task.Run으로 별도 스레드에서 실행)
                    _ = Task.Run(() => _positionMonitor.MonitorPositionStandard(symbol, currentPrice, decision == "LONG", token), token);

                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ ExecuteAutoOrder 에러: {ex.Message}");
                try 
                {
                    await TelegramService.Instance.SendMessageAsync($"⚠️ *[자동 매매 오류]*\n{symbol} 진입 중 예외 발생:\n{ex.Message}");
                } catch { }
            }
        }

        public async Task ClosePositionAsync(string symbol)
        {
            if (_positionMonitor != null)
            {
                await _positionMonitor.ExecuteMarketClose(symbol, "사용자 수동 청산", _cts?.Token ?? CancellationToken.None);
            }
        }

        private async Task<CandleData?> GetLatestCandleDataAsync(string symbol, CancellationToken token)
        {
            try
            {
                // 1시간봉 기준 지표 산출 (학습 데이터와 동일 기준)
                var klines = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 60, ct: token);
                if (!klines.Success || klines.Data == null || klines.Data.Count() < 50) return null;

                var subset = klines.Data.ToList();
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                double sentiment = await _newsService.GetMarketSentimentAsync();
                OnStatusLog?.Invoke($"📰 뉴스 감성 점수: {sentiment:F2}");

                var current = subset.Last();

                return new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
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
                    SentimentScore = (float)sentiment
                };
            }
            catch { return null; }
        }

        #endregion

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

        private string GetEngineStatusJson()
        {
            int activeCount = 0;
            lock (_posLock) activeCount = _activePositions.Count;
            
            // 간단한 JSON 생성 (Newtonsoft.Json 없이 문자열 보간 사용)
            return $@"{{
                ""status"": ""{(IsBotRunning ? "Running" : "Stopped")}"",
                ""uptime"": ""{(DateTime.Now - _engineStartTime).ToString(@"dd\.hh\:mm\:ss")}"",
                ""balance"": {_initialBalance},
                ""pnl"": {_riskManager.DailyRealizedPnl},
                ""active_positions"": {activeCount}
            }}";
        }
    }
}