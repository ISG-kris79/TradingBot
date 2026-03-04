using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using Bybit.Net.Clients;
using Bybit.Net.Objects.Models.V5;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Interfaces;
using System.Collections.Concurrent;
using TradingBot.Models;
using BinanceEnums = Binance.Net.Enums;
using BybitEnums = Bybit.Net.Enums;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot.Services
{
    public class MarketDataManager
    {
        private readonly IBinanceRestClient _restClient;
        private readonly BinanceSocketClient _socketClient;
        private readonly BybitSocketClient? _bybitSocketClient;
        private readonly BybitRestClient? _bybitRestClient;
        private readonly List<string> _majorSymbols;
        private CancellationTokenSource? _cts;

        public ConcurrentDictionary<string, TickerCacheItem> TickerCache { get; } = new();
        public ConcurrentDictionary<string, List<IBinanceKline>> KlineCache { get; } = new();

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

            var exchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);

            if (exchange == ExchangeType.Binance)
            {
                _socketClient = new BinanceSocketClient(options =>
                {
                    if (!string.IsNullOrWhiteSpace(AppConfig.BinanceApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BinanceApiSecret))
                    {
                        options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                    }
                    options.ReconnectInterval = TimeSpan.FromSeconds(10);
                });
            }
            else if (exchange == ExchangeType.Bybit)
            {
                _bybitRestClient = new BybitRestClient(options =>
                {
                    if (!string.IsNullOrWhiteSpace(AppConfig.BybitApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BybitApiSecret))
                    {
                        options.ApiCredentials = new ApiCredentials(AppConfig.BybitApiKey, AppConfig.BybitApiSecret);
                    }
                });

                _bybitSocketClient = new BybitSocketClient(options =>
                {
                    if (!string.IsNullOrWhiteSpace(AppConfig.BybitApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BybitApiSecret))
                    {
                        options.ApiCredentials = new ApiCredentials(AppConfig.BybitApiKey, AppConfig.BybitApiSecret);
                    }
                    options.ReconnectInterval = TimeSpan.FromSeconds(10);
                });
            }
        }

        public async Task StartAsync(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var internalToken = _cts.Token;

            var exchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);

            if (exchange == ExchangeType.Binance)
            {
                // Start Binance streams
                _ = StartUserDataStreamAsync(internalToken);
                _ = StartAllMarketTickerStreamAsync(internalToken);
                _ = StartPriceWebSocketAsync(internalToken);
                _ = StartKlineStreamAsync(internalToken);
            }
            else if (exchange == ExchangeType.Bybit)
            {
                // Start Bybit streams
                await StartBybitStreamsAsync(internalToken);
            }
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

            if (exchange == ExchangeType.Bybit && _bybitRestClient != null)
            {
                foreach (var symbol in _majorSymbols)
                {
                    try
                    {
                        var result = await _bybitRestClient.V5Api.ExchangeData.GetKlinesAsync(BybitEnums.Category.Linear, symbol, BybitEnums.KlineInterval.FiveMinutes, limit: limit, ct: token);
                        if (result.Success && result.Data?.List != null && result.Data.List.Any())
                        {
                            var list = result.Data.List
                                .OrderBy(k => k.StartTime)
                                .Select(k => (IBinanceKline)new BybitRestKlineAdapter(k, BinanceEnums.KlineInterval.FiveMinutes))
                                .ToList();

                            KlineCache[symbol] = list;
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] Bybit 캔들 선로딩 실패: {ex.Message}");
                    }
                }

                OnLog?.Invoke($"📥 Bybit 5분봉 선로딩 완료 (symbol={_majorSymbols.Count}, limit={limit})");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _socketClient?.UnsubscribeAllAsync();
            _bybitSocketClient?.UnsubscribeAllAsync();
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

                var subResult = await _socketClient.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
                    startStream.Data,
                    onAccountUpdate: data => OnAccountUpdate?.Invoke(data.Data),
                    onOrderUpdate: data => OnOrderUpdate?.Invoke(data.Data), // [수정] 주문 업데이트 연결
                    onListenKeyExpired: async _ =>
                    {
                        OnLog?.Invoke("⚠️ ListenKey 만료. 갱신 및 재구독을 시도합니다.");
                        await StartUserDataStreamAsync(token); // Re-subscribe
                    },
                    ct: token);

                if (subResult.Success)
                {
                    subResult.Data.ConnectionLost += () => OnLog?.Invoke("⚠️ 유저 스트림 연결 끊김. 재연결 시도...");
                    subResult.Data.ConnectionRestored += (ts) => OnLog?.Invoke($"✅ 유저 스트림 복구 ({ts.TotalSeconds:F1}초)");
                    OnLog?.Invoke("📡 사용자 데이터 스트림(포지션 감시) 가동");
                }
                else OnLog?.Invoke($"❌ 유저 스트림 구독 실패: {subResult.Error}");
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ 유저 스트림 에러: {ex.Message}"); }
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

        private async Task StartBybitStreamsAsync(CancellationToken token)
        {
            if (_bybitSocketClient == null) return;

            OnLog?.Invoke("🚀 Bybit 실시간 스트림 시작...");

            // 1. Ticker Stream (Linear)
            // Bybit는 전체 심볼 구독이 제한적일 수 있으므로 주요 심볼 위주로 구독하거나 필요한 심볼만 구독
            // 여기서는 _majorSymbols를 구독합니다.
            foreach (var symbol in _majorSymbols)
            {
                var subResult = await _bybitSocketClient.V5LinearApi.SubscribeToTickerUpdatesAsync(symbol, data =>
                {
                    var t = data.Data;

                    // TickerCache 업데이트
                    TickerCache.AddOrUpdate(symbol,
                        k => new TickerCacheItem
                        {
                            Symbol = symbol,
                            LastPrice = t.LastPrice ?? 0,
                            HighPrice = t.HighPrice24h ?? 0,
                            OpenPrice = t.LastPrice ?? 0, // v6.x: OpenPrice 속성 없음, LastPrice 사용
                            QuoteVolume = t.Volume24h ?? 0
                        },
                        (k, v) =>
                        {
                            v.LastPrice = t.LastPrice ?? v.LastPrice;
                            v.HighPrice = t.HighPrice24h ?? v.HighPrice;
                            v.OpenPrice = t.LastPrice ?? v.OpenPrice; // v6.x: OpenPrice 속성 없음
                            v.QuoteVolume = t.Volume24h ?? v.QuoteVolume;
                            return v;
                        });

                    // UI 업데이트용 이벤트 (Binance 타입으로 변환 필요 시 어댑터 사용, 여기서는 생략하거나 별도 처리)
                    // OnTickerUpdate?.Invoke(...); 
                }, ct: token);

                if (!subResult.Success)
                {
                    OnLog?.Invoke($"❌ Bybit Ticker 구독 실패 ({symbol}): {subResult.Error}");
                }
            }

            // 2. Kline Stream (Linear, 5m)
            foreach (var symbol in _majorSymbols)
            {
                var subResult = await _bybitSocketClient.V5LinearApi.SubscribeToKlineUpdatesAsync(symbol, BybitEnums.KlineInterval.FiveMinutes, data =>
                {
                    foreach (var k in data.Data)
                    {
                        // Bybit Kline -> IBinanceKline Adapter
                        var kline = new BybitKlineAdapter(k, BinanceEnums.KlineInterval.FiveMinutes);
                        bool isNewCandle = false;

                        KlineCache.AddOrUpdate(symbol,
                            key => new List<IBinanceKline> { kline },
                            (key, list) =>
                            {
                                lock (list)
                                {
                                    var last = list.LastOrDefault();
                                    if (last != null && last.OpenTime == kline.OpenTime)
                                        list[list.Count - 1] = kline; // Update
                                    else
                                    {
                                        list.Add(kline);
                                        if (list.Count > 120) list.RemoveAt(0);
                                        isNewCandle = true;
                                    }
                                }
                                return list;
                            });

                        if (isNewCandle)
                        {
                            OnNewKlineAdded?.Invoke(symbol, kline);
                        }
                    }
                }, ct: token);

                if (!subResult.Success)
                {
                    OnLog?.Invoke($"❌ Bybit Kline 구독 실패 ({symbol}): {subResult.Error}");
                }
            }

            // 3. Private Stream (Optional: Position/Order)
            // API Key가 있을 때만 시도
            if (!string.IsNullOrWhiteSpace(AppConfig.BybitApiKey))
            {
                // Position Updates
                var posSub = await _bybitSocketClient.V5PrivateApi.SubscribeToPositionUpdatesAsync(data =>
                {
                    foreach (var p in data.Data)
                    {
                        // Bybit 수량은 항상 양수, Side로 방향 구분
                        // Binance PositionAmount는 롱(+), 숏(-)
                        decimal amount = p.Quantity;
                        if (p.Side == BybitEnums.PositionSide.Sell) amount = -amount;

                        // Bybit Position 업데이트를 별도로 처리 (Binance 타입 변환 불가)
                        OnLog?.Invoke($"📊 Bybit Position: {p.Symbol} Amount:{amount:F4} Entry:{p.AveragePrice:F2} PnL:{p.UnrealizedPnl ?? 0:F2}");
                    }
                }, ct: token);

                if (!posSub.Success) OnLog?.Invoke($"❌ Bybit Position 구독 실패: {posSub.Error}");

                // Order Updates
                var orderSub = await _bybitSocketClient.V5PrivateApi.SubscribeToOrderUpdatesAsync(data =>
                {
                    foreach (var o in data.Data)
                    {
                        // Bybit Order 업데이트를 별도로 처리 (Binance 타입 변환 불가)
                        OnLog?.Invoke($"📝 Bybit Order: {o.Symbol} {o.Side} {o.OrderType} Status:{o.Status} Filled:{o.QuantityFilled ?? 0:FPRIVATE}/{o.Quantity:F4}");
                    }
                }, ct: token);

                if (!orderSub.Success) OnLog?.Invoke($"❌ Bybit Order 구독 실패: {orderSub.Error}");

                OnLog?.Invoke("📡 Bybit Private Stream (Position/Order) 가동");
            }

            OnLog?.Invoke("📡 Bybit 실시간 시세 스트림 가동 완료");
        }

        private BinanceEnums.FuturesOrderType ConvertBybitOrderType(BybitEnums.OrderType type)
        {
            return type == BybitEnums.OrderType.Limit ? BinanceEnums.FuturesOrderType.Limit : BinanceEnums.FuturesOrderType.Market;
        }

        private BinanceEnums.OrderStatus ConvertBybitOrderStatus(BybitEnums.OrderStatus status)
        {
            if (status == BybitEnums.OrderStatus.Filled) return BinanceEnums.OrderStatus.Filled;
            if (status == BybitEnums.OrderStatus.PartiallyFilled) return BinanceEnums.OrderStatus.PartiallyFilled;
            if (status == BybitEnums.OrderStatus.Cancelled) return BinanceEnums.OrderStatus.Canceled;
            if (status == BybitEnums.OrderStatus.New) return BinanceEnums.OrderStatus.New;
            return BinanceEnums.OrderStatus.Rejected;
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

        // Bybit Kline을 IBinanceKline으로 변환하는 어댑터
        private class BybitKlineAdapter : IBinanceKline
        {
            private readonly Bybit.Net.Objects.Models.V5.BybitKlineUpdate _kline;
            private readonly BinanceEnums.KlineInterval _interval;

            public BybitKlineAdapter(Bybit.Net.Objects.Models.V5.BybitKlineUpdate kline, BinanceEnums.KlineInterval interval)
            {
                _kline = kline;
                _interval = interval;
            }

            public decimal OpenPrice { get => _kline.OpenPrice; set { } }
            public decimal HighPrice { get => _kline.HighPrice; set { } }
            public decimal LowPrice { get => _kline.LowPrice; set { } }
            public decimal ClosePrice { get => _kline.ClosePrice; set { } }
            public decimal Volume { get => _kline.Volume; set { } }
            public DateTime OpenTime { get => _kline.StartTime; set { } }
            public DateTime CloseTime
            {
                get
                {
                    // Bybit v6.x: EndTime 속성 없음, StartTime + interval로 계산
                    int seconds = _interval switch
                    {
                        BinanceEnums.KlineInterval.OneMinute => 60,
                        BinanceEnums.KlineInterval.FiveMinutes => 300,
                        BinanceEnums.KlineInterval.FifteenMinutes => 900,
                        BinanceEnums.KlineInterval.OneHour => 3600,
                        _ => 3600
                    };
                    return _kline.StartTime.AddSeconds(seconds);
                }
                set { }
            }
            public decimal QuoteVolume { get => _kline.Volume * _kline.ClosePrice; set { } } // Turnover 대체
            public int TradeCount { get; set; }
            public decimal TakerBuyBaseVolume { get; set; }
            public decimal TakerBuyQuoteVolume { get; set; }
        }

        // Bybit REST Kline을 IBinanceKline으로 변환하는 어댑터
        private class BybitRestKlineAdapter : IBinanceKline
        {
            private readonly Bybit.Net.Objects.Models.V5.BybitKline _kline;
            private readonly BinanceEnums.KlineInterval _interval;

            public BybitRestKlineAdapter(Bybit.Net.Objects.Models.V5.BybitKline kline, BinanceEnums.KlineInterval interval)
            {
                _kline = kline;
                _interval = interval;
            }

            public decimal OpenPrice { get => _kline.OpenPrice; set { } }
            public decimal HighPrice { get => _kline.HighPrice; set { } }
            public decimal LowPrice { get => _kline.LowPrice; set { } }
            public decimal ClosePrice { get => _kline.ClosePrice; set { } }
            public decimal Volume { get => _kline.Volume; set { } }
            public DateTime OpenTime { get => _kline.StartTime; set { } }
            public DateTime CloseTime
            {
                get
                {
                    int seconds = _interval switch
                    {
                        BinanceEnums.KlineInterval.OneMinute => 60,
                        BinanceEnums.KlineInterval.FiveMinutes => 300,
                        BinanceEnums.KlineInterval.FifteenMinutes => 900,
                        BinanceEnums.KlineInterval.OneHour => 3600,
                        _ => 3600
                    };
                    return _kline.StartTime.AddSeconds(seconds);
                }
                set { }
            }
            public decimal QuoteVolume { get => _kline.Volume * _kline.ClosePrice; set { } }
            public int TradeCount { get; set; }
            public decimal TakerBuyBaseVolume { get; set; }
            public decimal TakerBuyQuoteVolume { get; set; }
        }
    }
}
