using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        }

        public async Task StartAsync(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var internalToken = _cts.Token;

            var exchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);

            // Start Binance streams
            _ = StartUserDataStreamAsync(internalToken);
            _ = StartAllMarketTickerStreamAsync(internalToken);
            _ = StartPriceWebSocketAsync(internalToken);
            _ = StartKlineStreamAsync(internalToken);
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
