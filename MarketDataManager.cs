using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Interfaces;
using System.Collections.Concurrent;
using TradingBot.Models;
using BinanceEnums = Binance.Net.Enums;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot.Services
{
    public class MarketDataManager : IDisposable
    {
        private bool _disposed = false;
        private readonly IBinanceRestClient _restClient;
        private BinanceSocketClient _socketClient = null!; // [v5.22.12] readonly 제거 — Watchdog 에서 재생성 필요
        private readonly List<string> _majorSymbols;
        private CancellationTokenSource? _cts;
        private string? _currentListenKey;
        private volatile bool _userStreamAlive = false;

        public ConcurrentDictionary<string, TickerCacheItem> TickerCache { get; } = new();
        public ConcurrentDictionary<string, List<IBinanceKline>> KlineCache { get; } = new();

        // [v5.10.77 Phase 5-A] BookTicker (Best Bid/Ask) 실시간 캐시 — 호가창 선행 지표 학습용
        public ConcurrentDictionary<string, BookTickerCacheItem> BookTickerCache { get; } = new();

        // [v5.10.79 Phase 5-C] aggTrade 1분 슬라이딩 통계 (taker buy/sell volume)
        public ConcurrentDictionary<string, AggTradeStatsItem> AggTradeStats { get; } = new();
        // 60초 슬라이딩 buffer: List<(timestamp, isBuyerMaker, qty)>
        private readonly ConcurrentDictionary<string, System.Collections.Generic.Queue<(DateTime ts, bool isBuyerMaker, decimal qty)>> _aggTradeBuffer
            = new();
        private readonly object _aggTradeBufferLock = new();

        // [v5.10.79 Phase 5-C] markPrice + Funding Rate 캐시
        public ConcurrentDictionary<string, MarkPriceCacheItem> MarkPriceCache { get; } = new();

        // [v5.10.80 Phase 5-D] Open Interest 1분 주기 REST 캐시 (15분 변화율 추적)
        public ConcurrentDictionary<string, OpenInterestCacheItem> OpenInterestCache { get; } = new();
        private Timer? _oiPollerTimer;

        // [v5.10.80 Phase 5-D] OrderBook 5단계 depth 캐시 (Cumulative Bid/Ask)
        public ConcurrentDictionary<string, DepthCacheItem> DepthCache { get; } = new();

        // [v5.22.16] 멀티TF WebSocket 캐시 전체 제거 — 사용자 12시간 요구
        //   원인: 메이저 12심볼 × M1/M15/H1 = 36 WebSocket 스트림 → CPU/메모리 폭주 + watchdog 폭주
        //   해결: GetCachedKlines / MultiTfKlineCache / MultiTfIntervals / PumpIntervals 전부 삭제
        //   호출자 (TradingEngine.cs / MacdCrossSignalService.cs) 는 5분봉 KlineCache 또는 REST 직접 호출로 변경

        // [v4.5.15] 전역 싱글턴 참조 (IExchangeService만 받는 클래스에서 5분봉 캐시 조회용)
        public static MarketDataManager? Instance { get; private set; }

        // Events for TradingEngine
        public event Action<BinanceFuturesStreamAccountUpdate>? OnAccountUpdate;
        public event Action<BinanceFuturesStreamOrderUpdate>? OnOrderUpdate; // [추가] 주문 업데이트 이벤트
        public event Action<IEnumerable<IBinance24HPrice>>? OnAllTickerUpdate;
        public event Action<IBinance24HPrice>? OnTickerUpdate;
        public event Action<string, IBinanceKline>? OnNewKlineAdded; // [실시간 저장] 새 봉 추가 이벤트
        public event Action<string>? OnLog;

        public MarketDataManager(IBinanceRestClient restClient, List<string> majorSymbols)
        {
            _restClient = restClient;
            _majorSymbols = majorSymbols;
            Instance = this; // [v4.5.15] 정적 접근용

            var exchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);

            if (exchange == ExchangeType.Binance)
            {
                // [FIX] 시뮬레이션 모드면 테스트넷 WebSocket 사용
                bool isSim = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                var tKey = AppConfig.Current?.Trading?.TestnetApiKey ?? "";
                var tSecret = AppConfig.Current?.Trading?.TestnetApiSecret ?? "";
                bool useTestnet = isSim && !string.IsNullOrWhiteSpace(tKey) && !string.IsNullOrWhiteSpace(tSecret);

                _socketClient = new BinanceSocketClient(options =>
                {
                    if (useTestnet)
                    {
                        options.ApiCredentials = new ApiCredentials(tKey, tSecret);
                        options.Environment = BinanceEnvironment.Testnet;
                    }
                    else if (!string.IsNullOrWhiteSpace(AppConfig.BinanceApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BinanceApiSecret))
                    {
                        options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                    }
                    options.ReconnectInterval = TimeSpan.FromSeconds(10);
                });
            }
        }

        public Task StartAsync(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var internalToken = _cts.Token;

            var exchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);

            // Start Binance streams
            _ = StartUserDataStreamAsync(internalToken);
            _ = RunUserStreamWatchdogAsync(internalToken);
            _ = StartAllMarketTickerStreamAsync(internalToken);
            _ = StartPriceWebSocketAsync(internalToken);
            _ = StartKlineStreamAsync(internalToken);
            // [v5.22.16] 멀티 TF WebSocket 캐시 가동 통째로 제거 — 사용자 요구
            //   - StartMultiTfKlineStreamAsync 호출 + 메서드 본체 삭제
            //   - StartPumpMultiTfRefreshLoopAsync 호출 + 메서드 본체 삭제

            // [v5.22.12] WebSocket Watchdog — 5분 동안 끊김만 있고 복구 0건이면 강제 전체 재구독
            _ = StartWebSocketWatchdogAsync(internalToken);

            // [v5.22.21] REST 폴링 fallback — WebSocket 메이저 ticker 콜백 무동작 회귀 우회
            //   원인: Binance.Net 12.8.1 SubscribeToTickerUpdatesAsync(_majorSymbols) + SubscribeToAllTickerUpdatesAsync
            //         둘 다 구독 성공 로그만 떴고 실제 callback 호출 0건 (v5.22.19 4분 / v5.22.20 2분 가동 확인).
            //   해결: 5초마다 /fapi/v1/ticker/price?symbol=... 직접 폴링 → OnTickerUpdate?.Invoke 로 emit
            //         → TradingEngine.HandleTickerUpdate → channel → ProcessTickerChannelAsync → UI.
            //   weight: 1/symbol → 4*12 = 48/min (한도 2400 의 2%, 안전).
            _ = StartMajorRestPollingAsync(internalToken);

            return Task.CompletedTask;
        }

        // [v5.22.21] 메이저 4개 가격 REST 폴링 — WebSocket 무동작 fallback
        private async Task StartMajorRestPollingAsync(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            OnLog?.Invoke("📊 [REST_POLL] 메이저 4개 5초 주기 가격 폴링 시작 (WebSocket fallback)");
            var majorSet = new HashSet<string>(_majorSymbols, StringComparer.OrdinalIgnoreCase);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 한 번의 GetTickersAsync (전체 ticker) 호출 → 메이저 4개만 추출
                    var pr = await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync(ct: token);
                    if (pr.Success && pr.Data != null)
                    {
                        foreach (var t in pr.Data)
                        {
                            if (t == null || string.IsNullOrEmpty(t.Symbol)) continue;
                            // TickerCache 는 항상 갱신 (전체 종목)
                            TickerCache.AddOrUpdate(t.Symbol,
                                _ => new TickerCacheItem { Symbol = t.Symbol, LastPrice = t.LastPrice, HighPrice = t.HighPrice, OpenPrice = t.OpenPrice, QuoteVolume = t.QuoteVolume, PriceChangePercent = t.PriceChangePercent },
                                (_, v) => { v.LastPrice = t.LastPrice; v.HighPrice = t.HighPrice; v.OpenPrice = t.OpenPrice; v.QuoteVolume = t.QuoteVolume; v.PriceChangePercent = t.PriceChangePercent; return v; });
                            // 메이저만 OnTickerUpdate emit
                            if (majorSet.Contains(t.Symbol))
                                OnTickerUpdate?.Invoke(t);
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { /* 일시 오류 무시 */ }

                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch (OperationCanceledException) { return; }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════
        // [v5.22.12] WebSocket Watchdog — 끊김 폭주 + 복구 0건 시 전체 강제 재구독
        //   원인: -1003 rate limit / 네트워크 일시 끊김 후 SDK 자동 재연결 실패 케이스
        //   증상: ConnectionLost 로그만 5분에 4000+개, ConnectionRestored 0건
        //   해결: 5분마다 Connect/Restore 카운터 비교 → 끊김>10 + 복구=0 시 전체 재시작
        // ═══════════════════════════════════════════════════════════════════════════════════
        private long _wsLostCount;
        private long _wsRestoredCount;
        public void IncrementWsLost() => System.Threading.Interlocked.Increment(ref _wsLostCount);
        public void IncrementWsRestored() => System.Threading.Interlocked.Increment(ref _wsRestoredCount);

        private async Task StartWebSocketWatchdogAsync(CancellationToken token)
        {
            try
            {
                // [v5.22.15] 부팅 후 30초 대기 (기존 2분 → 30초) + 1분 주기 (기존 5분 → 1분)
                //   원인: 사용자 보고 v5.22.14 — 봇 가동 1분 만에 WebSocket 전체 끊김 폭주
                //         기존 watchdog 첫 작동까지 7분 (2분 대기 + 5분 주기) 동안 무방비
                //   해결: 30초 대기 + 1분 주기 + 끊김>3 트리거 → 1.5분 내 자동 복구
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                long lastLostSnapshot = System.Threading.Interlocked.Read(ref _wsLostCount);
                long lastRestoredSnapshot = System.Threading.Interlocked.Read(ref _wsRestoredCount);
                // [v5.22.12] 재구독 후 grace period — 새 SocketClient 가 안정화될 시간 확보 (무한 재구독 루프 방지)
                DateTime resubscribeGraceUntil = DateTime.MinValue;
                while (!token.IsCancellationRequested)
                {
                    long curLost = System.Threading.Interlocked.Read(ref _wsLostCount);
                    long curRestored = System.Threading.Interlocked.Read(ref _wsRestoredCount);
                    long lostDelta = curLost - lastLostSnapshot;
                    long restoredDelta = curRestored - lastRestoredSnapshot;

                    // 조건: 1분 동안 끊김>3 + 복구=0 → 즉시 강제 재구독
                    //   (기존 끊김>10 + 5분 → 너무 느림. 사용자 사례 1분 4000+건 끊김 → 30초 안에 트리거됨)
                    // [v5.22.12] grace period 내에는 트리거 스킵 (재구독 직후 새 연결의 일시적 끊김 누적 방지)
                    bool inGrace = DateTime.UtcNow < resubscribeGraceUntil;
                    if (lostDelta > 3 && restoredDelta == 0 && !inGrace)
                    {
                        OnLog?.Invoke($"🚨 [WS-WATCHDOG] 1분간 끊김={lostDelta} 복구={restoredDelta} → 전체 강제 재구독 시도");
                        try
                        {
                            // SocketClient 자체 재생성 (모든 구독 끊고 처음부터)
                            try { await _socketClient.UnsubscribeAllAsync(); } catch { }
                            try { _socketClient.Dispose(); } catch { }
                            _socketClient = new BinanceSocketClient(options =>
                            {
                                if (!string.IsNullOrWhiteSpace(AppConfig.BinanceApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BinanceApiSecret))
                                    options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                                options.ReconnectInterval = TimeSpan.FromSeconds(10);
                            });
                            // [v5.22.16] 재구독: User + Ticker + Kline (멀티TF 제거됨)
                            _ = StartUserDataStreamAsync(token);
                            _ = StartAllMarketTickerStreamAsync(token);
                            _ = StartPriceWebSocketAsync(token);
                            _ = StartKlineStreamAsync(token);
                            OnLog?.Invoke("✅ [WS-WATCHDOG] SocketClient 재생성 + 핵심 4종 재구독 완료 (3분 grace period 적용)");

                            // [v5.22.15] 재구독 후 카운터 리셋 — 새 SocketClient 의 끊김/복구만 카운트
                            // [v5.22.12] curLost/curRestored 도 동시 리셋 → 다음 lastSnapshot 도 0 → 무한 루프 방지
                            System.Threading.Interlocked.Exchange(ref _wsLostCount, 0);
                            System.Threading.Interlocked.Exchange(ref _wsRestoredCount, 0);
                            curLost = 0;
                            curRestored = 0;
                            // 3분 grace period — 새 연결이 자리잡을 시간
                            resubscribeGraceUntil = DateTime.UtcNow.AddMinutes(3);
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"❌ [WS-WATCHDOG] 재구독 실패: {ex.Message}");
                        }
                    }
                    else if (inGrace && lostDelta > 3 && restoredDelta == 0)
                    {
                        OnLog?.Invoke($"⏳ [WS-WATCHDOG] grace period 중 — 끊김={lostDelta} 복구={restoredDelta} 무시 (재구독 안정화 대기)");
                    }
                    lastLostSnapshot = curLost;
                    lastRestoredSnapshot = curRestored;
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                }
            }
            catch (OperationCanceledException) { }
        }

        // [v5.22.16] StartPumpMultiTfRefreshLoopAsync / RefreshPumpMultiTfSubscriptionsAsync 통째 삭제
        //   원인: PUMP 카테고리 차단 (v5.22.5) + 멀티TF WebSocket 자체 폐기
        //   효과: PUMP 알트 50종 × 2TF = 100 스트림 절감

        public async Task PreloadRecentKlinesAsync(int limit, CancellationToken token)
        {
            if (limit <= 0) return;

            var exchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);

            if (exchange == ExchangeType.Binance)
            {
                foreach (var symbol in _majorSymbols)
                {
                    try
                    {
                        var klines = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, BinanceEnums.KlineInterval.FiveMinutes, limit: limit, ct: token);
                        if (klines.Success && klines.Data != null && klines.Data.Any())
                        {
                            KlineCache[symbol] = klines.Data.Cast<IBinanceKline>().ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] Binance 캔들 선로딩 실패: {ex.Message}");
                    }
                }

                OnLog?.Invoke($"📥 Binance 5분봉 선로딩 완료 (symbol={_majorSymbols.Count}, limit={limit})");
                return;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _socketClient?.UnsubscribeAllAsync();
        }

        private async Task StartUserDataStreamAsync(CancellationToken token)
        {
            try
            {
                var startStream = await _restClient.UsdFuturesApi.Account.StartUserStreamAsync(ct: token);
                if (!startStream.Success)
                {
                    OnLog?.Invoke($"❌ 유저 스트림 시작 실패: {startStream.Error}");
                    return;
                }

                _currentListenKey = startStream.Data;

                var subResult = await _socketClient.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
                    startStream.Data,
                    onAccountUpdate: data => OnAccountUpdate?.Invoke(data.Data),
                    onOrderUpdate: data => OnOrderUpdate?.Invoke(data.Data),
                    onListenKeyExpired: async _ =>
                    {
                        OnLog?.Invoke("⚠️ ListenKey 만료. 완전 재구독 시도...");
                        _userStreamAlive = false;
                        await StartUserDataStreamAsync(token);
                    },
                    ct: token);

                if (subResult.Success)
                {
                    _userStreamAlive = true;
                    subResult.Data.ConnectionLost += () =>
                    {
                        _userStreamAlive = false;
                        OnLog?.Invoke("⚠️ 유저 스트림 연결 끊김. 재연결 시도...");
                    };
                    subResult.Data.ConnectionRestored += (ts) =>
                    {
                        _userStreamAlive = true;
                        OnLog?.Invoke($"✅ 유저 스트림 복구 ({ts.TotalSeconds:F1}초)");
                    };
                    OnLog?.Invoke("📡 사용자 데이터 스트림(포지션 감시) 가동");
                }
                else OnLog?.Invoke($"❌ 유저 스트림 구독 실패: {subResult.Error}");
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ 유저 스트림 에러: {ex.Message}"); }
        }

        /// <summary>
        /// ListenKey KeepAlive (30분 주기) + 끊김 시 완전 재구독
        /// Binance ListenKey는 60분 후 만료되므로 30분마다 갱신 필수
        /// </summary>
        private async Task RunUserStreamWatchdogAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(25), token);

                    // KeepAlive 호출
                    if (!string.IsNullOrWhiteSpace(_currentListenKey))
                    {
                        var keepAlive = await _restClient.UsdFuturesApi.Account.KeepAliveUserStreamAsync(_currentListenKey, ct: token);
                        if (keepAlive.Success)
                        {
                            OnLog?.Invoke("🔄 유저 스트림 ListenKey 갱신 완료");
                        }
                        else
                        {
                            OnLog?.Invoke($"⚠️ ListenKey 갱신 실패: {keepAlive.Error?.Message} → 완전 재구독");
                            _userStreamAlive = false;
                        }
                    }

                    // 끊긴 상태면 완전 재구독
                    if (!_userStreamAlive)
                    {
                        OnLog?.Invoke("🔧 유저 스트림 끊김 감지 → 완전 재구독 시도...");
                        await StartUserDataStreamAsync(token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ 유저 스트림 워치독 에러: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
            }
        }

        private async Task StartAllMarketTickerStreamAsync(CancellationToken token)
        {
            // Initial snapshot
            try
            {
                var snapshot = await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync(ct: token);
                if (snapshot.Success)
                {
                    foreach (var t in snapshot.Data)
                    {
                        // [v5.22.12] PriceChangePercent 채우기 — ActiveTrackingPool 동적 8개 정렬 정상화
                        TickerCache[t.Symbol] = new TickerCacheItem { Symbol = t.Symbol, LastPrice = t.LastPrice, HighPrice = t.HighPrice, OpenPrice = t.OpenPrice, QuoteVolume = t.QuoteVolume, PriceChangePercent = t.PriceChangePercent };
                    }
                }
            }
            catch { /* Ignore */ }

            // Real-time stream
            var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllTickerUpdatesAsync(data =>
            {
                foreach (var t in data.Data)
                {
                    // [v5.22.12] PriceChangePercent 채우기 — WebSocket 실시간 업데이트도 반영
                    TickerCache.AddOrUpdate(t.Symbol,
                        k => new TickerCacheItem { Symbol = t.Symbol, LastPrice = t.LastPrice, HighPrice = t.HighPrice, OpenPrice = t.OpenPrice, QuoteVolume = t.QuoteVolume, PriceChangePercent = t.PriceChangePercent },
                        (k, v) => { v.LastPrice = t.LastPrice; v.HighPrice = t.HighPrice; v.OpenPrice = t.OpenPrice; v.QuoteVolume = t.QuoteVolume; v.PriceChangePercent = t.PriceChangePercent; return v; });
                }
                OnAllTickerUpdate?.Invoke(data.Data);
            }, ct: token);

            if (subResult.Success)
            {
                subResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke("⚠️ 전체 시세 스트림 연결 끊김..."); };
                subResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ 전체 시세 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                OnLog?.Invoke("📡 전 종목 실시간 시세 스트림 가동 (고속 스캔)");
            }
            else OnLog?.Invoke($"❌ 전 종목 스트림 실패: {subResult.Error}");
        }

        // [v5.22.19] 메이저 ticker 도착 진단 카운터 — 1분마다 OnLog 로 노출
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _majorTickRecv = new();
        private DateTime _lastMajorTickHeartbeatLog = DateTime.MinValue;

        private async Task StartPriceWebSocketAsync(CancellationToken token)
        {
            var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTickerUpdatesAsync(_majorSymbols, data =>
            {
                // [v5.22.19] 도착 카운트 — UI/로그에서 ticker flow 가시화
                try
                {
                    var sym = data.Data?.Symbol ?? string.Empty;
                    if (!string.IsNullOrEmpty(sym))
                        _majorTickRecv.AddOrUpdate(sym, 1L, (_, v) => v + 1);

                    var now = DateTime.UtcNow;
                    if ((now - _lastMajorTickHeartbeatLog).TotalSeconds >= 60)
                    {
                        _lastMajorTickHeartbeatLog = now;
                        var summary = string.Join(" ", _majorSymbols.Select(s =>
                        {
                            var c = _majorTickRecv.TryGetValue(s, out var n) ? n : 0;
                            return $"{s.Replace("USDT", "")}={c}";
                        }));
                        OnLog?.Invoke($"📊 [TICK_HEARTBEAT] 메이저 1분 수신: {summary}");
                        _majorTickRecv.Clear();
                    }
                }
                catch { }

                OnTickerUpdate?.Invoke(data.Data);
            }, ct: token);

            if (subResult.Success)
            {
                subResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke("⚠️ 주요 종목 시세 스트림 연결 끊김..."); };
                subResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ 주요 종목 시세 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                OnLog?.Invoke("📡 주요 종목 실시간 가격 웹소켓 연결 성공");
            }
            else OnLog?.Invoke($"❌ 주요 종목 웹소켓 연결 실패: {subResult.Error}");

            // [v5.10.77 Phase 5-A] BookTicker (Best Bid/Ask) 구독 — 호가창 imbalance feature 학습용
            try
            {
                var bookSubResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToBookTickerUpdatesAsync(_majorSymbols, data =>
                {
                    var d = data.Data;
                    if (string.IsNullOrEmpty(d.Symbol)) return;
                    BookTickerCache[d.Symbol] = new BookTickerCacheItem
                    {
                        Symbol = d.Symbol,
                        BestBidPrice = d.BestBidPrice,
                        BestBidQty = d.BestBidQuantity,
                        BestAskPrice = d.BestAskPrice,
                        BestAskQty = d.BestAskQuantity,
                        UpdatedAt = DateTime.UtcNow
                    };
                }, ct: token);

                if (bookSubResult.Success)
                {
                    bookSubResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke("⚠️ BookTicker 스트림 연결 끊김..."); };
                    bookSubResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ BookTicker 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                    OnLog?.Invoke("📡 BookTicker 실시간 구독 성공 (Bid/Ask Imbalance feature 활성)");
                }
                else
                {
                    OnLog?.Invoke($"⚠️ BookTicker 구독 실패 (무해, 호가 feature 0으로 처리): {bookSubResult.Error}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ BookTicker 구독 예외 (무해): {ex.Message}");
            }

            // [v5.10.79 Phase 5-C] aggTrade 구독 — 체결 매수/매도 비율 (1분 슬라이딩)
            try
            {
                var aggResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(_majorSymbols, data =>
                {
                    var t = data.Data;
                    if (string.IsNullOrEmpty(t.Symbol)) return;
                    var now = DateTime.UtcNow;
                    var buf = _aggTradeBuffer.GetOrAdd(t.Symbol, _ => new System.Collections.Generic.Queue<(DateTime, bool, decimal)>());
                    lock (_aggTradeBufferLock)
                    {
                        buf.Enqueue((now, t.BuyerIsMaker, t.Quantity));
                        // 60초 이상 오래된 항목 제거
                        while (buf.Count > 0 && (now - buf.Peek().ts).TotalSeconds > 60)
                            buf.Dequeue();

                        decimal buyVol = 0m, sellVol = 0m;
                        foreach (var (_, isBuyerMaker, qty) in buf)
                        {
                            // BuyerIsMaker = true → 매도자가 시장가 (taker sell)
                            // BuyerIsMaker = false → 매수자가 시장가 (taker buy)
                            if (isBuyerMaker) sellVol += qty;
                            else buyVol += qty;
                        }
                        AggTradeStats[t.Symbol] = new AggTradeStatsItem
                        {
                            Symbol = t.Symbol,
                            BuyVolume1m = buyVol,
                            SellVolume1m = sellVol,
                            UpdatedAt = now
                        };
                    }
                }, ct: token);

                if (aggResult.Success)
                {
                    aggResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke("⚠️ AggTrade 스트림 연결 끊김..."); };
                    aggResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ AggTrade 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                    OnLog?.Invoke("📡 AggTrade 실시간 구독 성공 (체결 매수/매도 비율 feature 활성)");
                }
                else
                {
                    OnLog?.Invoke($"⚠️ AggTrade 구독 실패 (무해): {aggResult.Error}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ AggTrade 구독 예외 (무해): {ex.Message}");
            }

            // [v5.10.79 Phase 5-C] markPrice 구독 — Funding Rate feature
            try
            {
                var markResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToMarkPriceUpdatesAsync(_majorSymbols, 1000, data =>
                {
                    var m = data.Data;
                    if (m == null || string.IsNullOrEmpty(m.Symbol)) return;
                    MarkPriceCache[m.Symbol] = new MarkPriceCacheItem
                    {
                        Symbol = m.Symbol,
                        MarkPrice = m.MarkPrice,
                        FundingRate = m.FundingRate ?? 0m,
                        NextFundingTime = m.NextFundingTime,
                        UpdatedAt = DateTime.UtcNow
                    };
                }, ct: token);

                if (markResult.Success)
                {
                    markResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke("⚠️ MarkPrice 스트림 연결 끊김..."); };
                    markResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ MarkPrice 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                    OnLog?.Invoke("📡 MarkPrice 실시간 구독 성공 (Funding Rate feature 활성)");
                }
                else
                {
                    OnLog?.Invoke($"⚠️ MarkPrice 구독 실패 (무해): {markResult.Error}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ MarkPrice 구독 예외 (무해): {ex.Message}");
            }

            // [v5.10.80 Phase 5-D] OrderBook depth 5단계 구독 (100ms) — 깊은 호가 압력
            try
            {
                var depthResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToPartialOrderBookUpdatesAsync(
                    _majorSymbols, 5, 100, data =>
                    {
                        var d = data.Data;
                        if (d == null || string.IsNullOrEmpty(d.Symbol)) return;
                        decimal bidVol = 0m, askVol = 0m, bidVal = 0m, askVal = 0m;
                        if (d.Bids != null) foreach (var b in d.Bids) { bidVol += b.Quantity; bidVal += b.Price * b.Quantity; }
                        if (d.Asks != null) foreach (var a in d.Asks) { askVol += a.Quantity; askVal += a.Price * a.Quantity; }
                        DepthCache[d.Symbol] = new DepthCacheItem
                        {
                            Symbol = d.Symbol,
                            Top5_BidVolume = bidVol,
                            Top5_AskVolume = askVol,
                            Top5_BidValue = bidVal,
                            Top5_AskValue = askVal,
                            UpdatedAt = DateTime.UtcNow
                        };
                    }, ct: token);

                if (depthResult.Success)
                {
                    depthResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke("⚠️ Depth5 스트림 연결 끊김..."); };
                    depthResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ Depth5 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                    OnLog?.Invoke("📡 Depth5 실시간 구독 성공 (5단계 호가 압력 feature 활성)");
                }
                else
                {
                    OnLog?.Invoke($"⚠️ Depth5 구독 실패 (무해): {depthResult.Error}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ Depth5 구독 예외 (무해): {ex.Message}");
            }

            // [v5.10.80 Phase 5-D] Open Interest 1분 주기 REST poller 시작
            StartOpenInterestPoller(token);
        }

        /// <summary>[v5.10.80] OI는 WebSocket 미지원 → 1분 주기 REST 폴링 (15분 변화율 학습용)</summary>
        private void StartOpenInterestPoller(CancellationToken token)
        {
            try
            {
                _oiPollerTimer?.Dispose();
                _oiPollerTimer = new Timer(async _ =>
                {
                    if (token.IsCancellationRequested) return;
                    foreach (var symbol in _majorSymbols)
                    {
                        try
                        {
                            var res = await _restClient.UsdFuturesApi.ExchangeData.GetOpenInterestAsync(symbol, token);
                            if (!res.Success || res.Data == null) continue;
                            decimal currentOi = res.Data.OpenInterest;
                            var now = DateTime.UtcNow;

                            if (OpenInterestCache.TryGetValue(symbol, out var existing))
                            {
                                // 15분 이상 지났으면 snapshot 갱신
                                if ((now - existing.LastSnapshotAt).TotalMinutes >= 15)
                                {
                                    OpenInterestCache[symbol] = new OpenInterestCacheItem
                                    {
                                        Symbol = symbol,
                                        OpenInterest = currentOi,
                                        OpenInterest15mAgo = existing.OpenInterest,
                                        LastSnapshotAt = now,
                                        UpdatedAt = now
                                    };
                                }
                                else
                                {
                                    existing.OpenInterest = currentOi;
                                    existing.UpdatedAt = now;
                                }
                            }
                            else
                            {
                                OpenInterestCache[symbol] = new OpenInterestCacheItem
                                {
                                    Symbol = symbol,
                                    OpenInterest = currentOi,
                                    OpenInterest15mAgo = currentOi,  // 첫 수집은 자기자신
                                    LastSnapshotAt = now,
                                    UpdatedAt = now
                                };
                            }
                        }
                        catch { /* 개별 심볼 실패 무시 */ }
                        await Task.Delay(150, token);  // rate limit 보호
                    }
                }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
                OnLog?.Invoke("📊 OpenInterest 폴러 시작 (1분 주기, 15분 변화율 추적)");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ OI 폴러 시작 실패 (무해): {ex.Message}");
            }
        }

        private async Task StartKlineStreamAsync(CancellationToken token)
        {
            // 1. Initial Snapshot (초기 데이터 로드)
            foreach (var symbol in _majorSymbols)
            {
                try
                {
                    var klines = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, BinanceEnums.KlineInterval.FiveMinutes, limit: 120, ct: token);
                    if (klines.Success)
                    {
                        KlineCache[symbol] = klines.Data.Cast<IBinanceKline>().ToList();
                        OnLog?.Invoke($"📥 [{symbol}] 초기 5분봉 로드: {KlineCache[symbol].Count}개");
                    }
                    else
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] 초기 5분봉 로드 실패: {klines.Error}");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] 초기 5분봉 로드 예외: {ex.Message}");
                }
            }

            // 2. Subscribe (실시간 업데이트)
            foreach (var symbol in _majorSymbols)
            {
                var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync(symbol, BinanceEnums.KlineInterval.FiveMinutes, data =>
                {
                    var klineSymbol = data.Symbol;
                    var kline = data.Data.Data;
                    bool isNewCandle = false;

                    KlineCache.AddOrUpdate(klineSymbol ?? "",
                        k => new List<IBinanceKline> { kline },
                        (k, list) =>
                        {
                            lock (list)
                            {
                                var last = list.LastOrDefault();
                                if (last != null && last.OpenTime == kline.OpenTime)
                                {
                                    list[list.Count - 1] = kline; // 현재 봉 갱신
                                }
                                else
                                {
                                    list.Add(kline); // 새 봉 추가
                                    if (list.Count > 120) list.RemoveAt(0); // 개수 유지
                                    isNewCandle = true; // [실시간 저장] 새 봉 플래그
                                }
                            }
                            return list;
                        });

                    // [실시간 저장] 새 봉이 추가되었으면 즉시 DB 저장 이벤트 발생
                    if (isNewCandle && klineSymbol != null)
                    {
                        OnNewKlineAdded?.Invoke(klineSymbol, kline);
                    }
                }, ct: token);

                if (subResult.Success)
                {
                    subResult.Data.ConnectionLost += () => { IncrementWsLost(); OnLog?.Invoke($"⚠️ [{symbol}] 캔들 스트림 연결 끊김..."); };
                    subResult.Data.ConnectionRestored += (ts) => { IncrementWsRestored(); OnLog?.Invoke($"✅ [{symbol}] 캔들 스트림 복구 ({ts.TotalSeconds:F1}초)"); };
                    OnLog?.Invoke($"📡 [{symbol}] 5분봉 캔들 스트림 가동");
                }
                else
                {
                    OnLog?.Invoke($"❌ [{symbol}] 캔들 스트림 실패: {subResult.Error}");
                }
            }
        }

        // [v5.22.16] StartMultiTfKlineStreamAsync 통째 삭제
        //   원인: 메이저 12심볼 × M1/M15/H1 = 36 WebSocket 스트림 폭주
        //   호출자: TradingEngine.cs (HIGH_TOP_CHASING / ENGINE_151) → 5분봉 KlineCache 또는 REST 직접 호출로 변경
        //          MacdCrossSignalService.cs → REST 직접 호출로 변경

        public void UpdateCandle(string symbol, CandleData candle)
        {
            // 외부에서 수신한 캔들 데이터를 캐시에 업데이트하는 로직
            if (string.IsNullOrEmpty(symbol) || candle == null) return;

            // Convert CandleData to IBinanceKline compatible object
            var kline = new BinanceKlineAdapter(candle, BinanceEnums.KlineInterval.OneMinute);

            KlineCache.AddOrUpdate(symbol,
                k => new List<IBinanceKline> { kline },
                (k, list) =>
                {
                    lock (list)
                    {
                        var last = list.LastOrDefault();
                        if (last != null && last.OpenTime == kline.OpenTime) list[list.Count - 1] = kline; // Update current candle
                        else { list.Add(kline); if (list.Count > 60) list.RemoveAt(0); } // Add new candle, maintain size
                    }
                    return list;
                });
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                _oiPollerTimer?.Dispose();
                _cts?.Cancel();
                _cts?.Dispose();

                _socketClient?.UnsubscribeAllAsync().Wait(TimeSpan.FromSeconds(5));
                _socketClient?.Dispose();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[MarketDataManager] Dispose 오류: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
            
            GC.SuppressFinalize(this);
        }
    }
}
