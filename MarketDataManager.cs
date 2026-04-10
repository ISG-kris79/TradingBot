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
        private readonly BinanceSocketClient _socketClient = null!;
        private readonly List<string> _majorSymbols;
        private CancellationTokenSource? _cts;
        private string? _currentListenKey;
        private volatile bool _userStreamAlive = false;

        public ConcurrentDictionary<string, TickerCacheItem> TickerCache { get; } = new();
        public ConcurrentDictionary<string, List<IBinanceKline>> KlineCache { get; } = new();

        // [v4.5.15] 멀티 타임프레임 WebSocket 캐시 — REST 호출 제거용
        // Key: "{symbol}|{interval}" (예: "BTCUSDT|1m")
        public ConcurrentDictionary<string, List<IBinanceKline>> MultiTfKlineCache { get; } = new();

        // [v4.5.15] 전역 싱글턴 참조 (IExchangeService만 받는 클래스에서 캐시 조회용)
        public static MarketDataManager? Instance { get; private set; }

        // [v4.5.16] PUMP 알트 동적 멀티TF 구독 상태
        // - 거래량 상위 알트만 M1/M15 캐시, 5분 주기 갱신
        // - 한 번 구독한 심볼은 계속 캐시 유지 (200개 상한)
        private readonly ConcurrentDictionary<string, bool> _pumpSubscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastPumpSubRefreshTime = DateTime.MinValue;
        private const int MaxPumpSubscribedSymbols = 200;
        private const int TopAltRefreshCount = 50;
        private static readonly BinanceEnums.KlineInterval[] PumpIntervals = new[]
        {
            BinanceEnums.KlineInterval.OneMinute,
            BinanceEnums.KlineInterval.FifteenMinutes
        };

        // [v4.5.15] 캐시 대상 타임프레임 (메이저 코인만 멀티 TF 구독)
        private static readonly BinanceEnums.KlineInterval[] MultiTfIntervals = new[]
        {
            BinanceEnums.KlineInterval.OneMinute,
            BinanceEnums.KlineInterval.FifteenMinutes,
            BinanceEnums.KlineInterval.OneHour,
            BinanceEnums.KlineInterval.FourHour,
            BinanceEnums.KlineInterval.OneDay
        };

        private static string MultiTfKey(string symbol, BinanceEnums.KlineInterval interval)
            => $"{symbol}|{interval}";

        /// <summary>
        /// [v4.5.15] 캐시에서 캔들 조회. 없으면 null 반환 (호출 측이 REST fallback)
        /// </summary>
        public List<IBinanceKline>? GetCachedKlines(string symbol, BinanceEnums.KlineInterval interval, int minCount = 30)
        {
            if (MultiTfKlineCache.TryGetValue(MultiTfKey(symbol, interval), out var list))
            {
                lock (list)
                {
                    if (list.Count >= minCount)
                        return list.ToList(); // 스냅샷 복사
                }
            }
            return null;
        }

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
            // [v4.5.15] 멀티 TF WebSocket 캐시 가동 (M1/M15/H1/H4/D1) — REST 호출 제거용
            _ = StartMultiTfKlineStreamAsync(internalToken);
            // [v4.5.16] PUMP 알트 동적 멀티TF 구독 루프 (5분 주기)
            _ = StartPumpMultiTfRefreshLoopAsync(internalToken);

            return Task.CompletedTask;
        }

        /// <summary>
        /// [v4.5.16] 거래량 상위 PUMP 알트를 5분마다 M1/M15 WebSocket 구독
        /// - 첫 실행: 30초 대기 (TickerCache 데이터 수집 시간 확보)
        /// - 이후: 5분 주기로 top 50 알트 추출 → 미구독 심볼만 신규 구독
        /// - 총 구독 상한 200개 (1024 Binance 한도의 20%)
        /// - rate limit: 150ms 간격 (10 msg/sec 준수)
        /// </summary>
        private async Task StartPumpMultiTfRefreshLoopAsync(CancellationToken token)
        {
            try
            {
                // 1회: TickerCache 워밍업 대기
                await Task.Delay(TimeSpan.FromSeconds(30), token);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await RefreshPumpMultiTfSubscriptionsAsync(token);
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [PUMP 멀티TF] 루프 오류: {ex.Message}");
                    }
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task RefreshPumpMultiTfSubscriptionsAsync(CancellationToken token)
        {
            // 상한 체크
            if (_pumpSubscribedSymbols.Count >= MaxPumpSubscribedSymbols)
            {
                OnLog?.Invoke($"ℹ️ [PUMP 멀티TF] 구독 상한 도달 ({_pumpSubscribedSymbols.Count}/{MaxPumpSubscribedSymbols}) → 스킵");
                return;
            }

            // 거래량 상위 알트 추출 (메이저 제외)
            var majorSet = new HashSet<string>(_majorSymbols, StringComparer.OrdinalIgnoreCase);
            var topAlts = TickerCache.Values
                .Where(t => !string.IsNullOrEmpty(t.Symbol)
                            && t.Symbol!.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                            && !majorSet.Contains(t.Symbol!)
                            && t.QuoteVolume > 0)
                .OrderByDescending(t => t.QuoteVolume)
                .Take(TopAltRefreshCount)
                .Select(t => t.Symbol!)
                .ToList();

            if (topAlts.Count == 0)
            {
                OnLog?.Invoke("ℹ️ [PUMP 멀티TF] TickerCache 비어있음 → 다음 주기 대기");
                return;
            }

            // 미구독 심볼만 필터
            var newSymbols = topAlts
                .Where(s => !_pumpSubscribedSymbols.ContainsKey(s))
                .Take(MaxPumpSubscribedSymbols - _pumpSubscribedSymbols.Count)
                .ToList();

            if (newSymbols.Count == 0)
            {
                return; // 전부 이미 구독됨
            }

            OnLog?.Invoke($"📡 [PUMP 멀티TF] 신규 {newSymbols.Count}개 심볼 구독 시작 (현재 {_pumpSubscribedSymbols.Count}개)");

            foreach (var interval in PumpIntervals)
            {
                if (token.IsCancellationRequested) return;

                foreach (var symbol in newSymbols)
                {
                    if (token.IsCancellationRequested) return;
                    string cacheKey = MultiTfKey(symbol, interval);

                    // 1. 초기 선로딩 (REST)
                    try
                    {
                        int limit = interval == BinanceEnums.KlineInterval.OneMinute ? 60 : 120;
                        var klines = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                            symbol, interval, limit: limit, ct: token);
                        if (klines.Success && klines.Data != null)
                        {
                            MultiTfKlineCache[cacheKey] = klines.Data.Cast<IBinanceKline>().ToList();
                        }
                    }
                    catch { /* 개별 심볼 실패 무시 */ }

                    // 2. WebSocket 구독
                    try
                    {
                        var sub = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                            symbol, interval, data =>
                            {
                                var kline = data.Data.Data;
                                var sym = data.Symbol;
                                if (string.IsNullOrEmpty(sym)) return;
                                string cKey = MultiTfKey(sym, interval);
                                MultiTfKlineCache.AddOrUpdate(cKey,
                                    _ => new List<IBinanceKline> { kline },
                                    (_, list) =>
                                    {
                                        lock (list)
                                        {
                                            var last = list.LastOrDefault();
                                            if (last != null && last.OpenTime == kline.OpenTime)
                                                list[list.Count - 1] = kline;
                                            else
                                            {
                                                list.Add(kline);
                                                int maxSize = interval == BinanceEnums.KlineInterval.OneMinute ? 60 : 120;
                                                if (list.Count > maxSize) list.RemoveAt(0);
                                            }
                                        }
                                        return list;
                                    });
                            }, ct: token);

                        if (sub.Success)
                        {
                            _pumpSubscribedSymbols[symbol] = true;
                        }
                    }
                    catch { /* 개별 실패 무시 */ }

                    // [rate limit] 10 msg/sec → 150ms 간격 유지
                    await Task.Delay(150, token);
                }
            }

            OnLog?.Invoke($"✅ [PUMP 멀티TF] 구독 완료 | 총 {_pumpSubscribedSymbols.Count}개 심볼 × {PumpIntervals.Length}개 TF = {_pumpSubscribedSymbols.Count * PumpIntervals.Length}개 스트림");
        }

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
                        TickerCache[t.Symbol] = new TickerCacheItem { Symbol = t.Symbol, LastPrice = t.LastPrice, HighPrice = t.HighPrice, OpenPrice = t.OpenPrice, QuoteVolume = t.QuoteVolume };
                    }
                }
            }
            catch { /* Ignore */ }

            // Real-time stream
            var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllTickerUpdatesAsync(data =>
            {
                foreach (var t in data.Data)
                {
                    TickerCache.AddOrUpdate(t.Symbol,
                        k => new TickerCacheItem { Symbol = t.Symbol, LastPrice = t.LastPrice, HighPrice = t.HighPrice, OpenPrice = t.OpenPrice, QuoteVolume = t.QuoteVolume },
                        (k, v) => { v.LastPrice = t.LastPrice; v.HighPrice = t.HighPrice; v.OpenPrice = t.OpenPrice; v.QuoteVolume = t.QuoteVolume; return v; });
                }
                OnAllTickerUpdate?.Invoke(data.Data);
            }, ct: token);

            if (subResult.Success)
            {
                subResult.Data.ConnectionLost += () => OnLog?.Invoke("⚠️ 전체 시세 스트림 연결 끊김...");
                subResult.Data.ConnectionRestored += (ts) => OnLog?.Invoke($"✅ 전체 시세 스트림 복구 ({ts.TotalSeconds:F1}초)");
                OnLog?.Invoke("📡 전 종목 실시간 시세 스트림 가동 (고속 스캔)");
            }
            else OnLog?.Invoke($"❌ 전 종목 스트림 실패: {subResult.Error}");
        }

        private async Task StartPriceWebSocketAsync(CancellationToken token)
        {
            var subResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTickerUpdatesAsync(_majorSymbols, data =>
            {
                OnTickerUpdate?.Invoke(data.Data);
            }, ct: token);

            if (subResult.Success)
            {
                subResult.Data.ConnectionLost += () => OnLog?.Invoke("⚠️ 주요 종목 시세 스트림 연결 끊김...");
                subResult.Data.ConnectionRestored += (ts) => OnLog?.Invoke($"✅ 주요 종목 시세 스트림 복구 ({ts.TotalSeconds:F1}초)");
                OnLog?.Invoke("📡 주요 종목 실시간 가격 웹소켓 연결 성공");
            }
            else OnLog?.Invoke($"❌ 주요 종목 웹소켓 연결 실패: {subResult.Error}");
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
                    subResult.Data.ConnectionLost += () => OnLog?.Invoke($"⚠️ [{symbol}] 캔들 스트림 연결 끊김...");
                    subResult.Data.ConnectionRestored += (ts) => OnLog?.Invoke($"✅ [{symbol}] 캔들 스트림 복구 ({ts.TotalSeconds:F1}초)");
                    OnLog?.Invoke($"📡 [{symbol}] 5분봉 캔들 스트림 가동");
                }
                else
                {
                    OnLog?.Invoke($"❌ [{symbol}] 캔들 스트림 실패: {subResult.Error}");
                }
            }
        }

        /// <summary>
        /// [v4.5.15] 메이저 심볼에 대해 멀티 타임프레임 WebSocket 구독 + 초기 선로딩
        /// - M1: 최근 60봉 (MACD/RSI 계산)
        /// - M15/H1/H4/D1: 최근 120봉 (AI Feature 추출)
        /// </summary>
        public async Task StartMultiTfKlineStreamAsync(CancellationToken token)
        {
            foreach (var symbol in _majorSymbols)
            {
                foreach (var interval in MultiTfIntervals)
                {
                    if (token.IsCancellationRequested) return;
                    string cacheKey = MultiTfKey(symbol, interval);

                    // 1. 초기 선로딩
                    try
                    {
                        int limit = interval == BinanceEnums.KlineInterval.OneMinute ? 60 : 120;
                        var klines = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                            symbol, interval, limit: limit, ct: token);
                        if (klines.Success && klines.Data != null)
                        {
                            MultiTfKlineCache[cacheKey] = klines.Data.Cast<IBinanceKline>().ToList();
                            OnLog?.Invoke($"📥 [{symbol}] {interval} 초기 선로딩: {klines.Data.Count()}개");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] {interval} 선로딩 예외: {ex.Message}");
                        continue;
                    }

                    // 2. WebSocket 구독 (실시간 업데이트)
                    try
                    {
                        var sub = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                            symbol, interval, data =>
                            {
                                var kline = data.Data.Data;
                                MultiTfKlineCache.AddOrUpdate(cacheKey,
                                    _ => new List<IBinanceKline> { kline },
                                    (_, list) =>
                                    {
                                        lock (list)
                                        {
                                            var last = list.LastOrDefault();
                                            if (last != null && last.OpenTime == kline.OpenTime)
                                                list[list.Count - 1] = kline; // 현재 봉 갱신
                                            else
                                            {
                                                list.Add(kline); // 새 봉 추가
                                                int maxSize = interval == BinanceEnums.KlineInterval.OneMinute ? 60 : 120;
                                                if (list.Count > maxSize) list.RemoveAt(0);
                                            }
                                        }
                                        return list;
                                    });
                            }, ct: token);

                        if (sub.Success && sub.Data != null)
                        {
                            sub.Data.ConnectionLost += () => OnLog?.Invoke($"⚠️ [{symbol}] {interval} 스트림 끊김");
                            sub.Data.ConnectionRestored += (ts) => OnLog?.Invoke($"✅ [{symbol}] {interval} 스트림 복구 ({ts.TotalSeconds:F1}초)");
                            OnLog?.Invoke($"📡 [{symbol}] {interval} 멀티TF 스트림 가동");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] {interval} 구독 실패: {ex.Message}");
                    }
                }
            }
            OnLog?.Invoke("✅ [멀티TF] 전 타임프레임 WebSocket 캐시 가동 완료");
        }

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
