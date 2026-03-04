using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.Authentication;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// Bybit WebSocket 실시간 데이터 수신 및 이벤트 처리
    /// Phase 12: 재연결 로직 강화 및 안정성 개선
    /// </summary>
    public class BybitSocketConnector
    {
        private readonly BybitSocketClient _socketClient;
        private readonly List<string> _subscribedSymbols = new();
        private CancellationTokenSource? _cts;
        private bool _isReconnecting = false;
        private int _reconnectAttempts = 0;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private DateTime _lastSuccessfulConnection = DateTime.MinValue;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);

        // 이벤트: 실시간 Ticker 업데이트
        public event Action<string, decimal>? OnTickerUpdate;

        // 이벤트: 실시간 캔들 업데이트
        public event Action<string, TradingBotBybitKlineUpdate>? OnKlineUpdate;

        // 이벤트: 계좌 포지션 업데이트
        public event Action<TradingBotBybitPositionUpdate>? OnPositionUpdate;

        // 이벤트: 주문 업데이트
        public event Action<TradingBotBybitOrderUpdate>? OnOrderUpdate;

        // 이벤트: 로그 메시지
        public event Action<string>? OnLog;
        
        // 이벤트: 연결 상태 변경
        public event Action<bool>? OnConnectionStateChanged;

        public BybitSocketConnector()
        {
            _socketClient = new BybitSocketClient(options =>
            {
                if (!string.IsNullOrEmpty(AppConfig.BybitApiKey) &&
                    !string.IsNullOrEmpty(AppConfig.BybitApiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(
                        AppConfig.BybitApiKey,
                        AppConfig.BybitApiSecret
                    );
                }
                options.ReconnectInterval = TimeSpan.FromSeconds(10);
            });
        }

        /// <summary>
        /// 실시간 Ticker 구독 (Bybit V5 Linear Futures)
        /// </summary>
        public async Task SubscribeTickerAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var subscribeResult = await _socketClient.V5LinearApi.SubscribeToTickerUpdatesAsync(
                    symbol,
                    data =>
                    {
                        try
                        {
                            _lastSuccessfulConnection = DateTime.UtcNow; // Health check 업데이트
                            
                            var ticker = data.Data;
                            var lastPrice = ticker.LastPrice ?? 0;
                            OnTickerUpdate?.Invoke(symbol, lastPrice);

                            // UI 업데이트 (MainWindow 인스턴스 사용)
                            MainWindow.Instance?.Dispatcher.Invoke(() =>
                            {
                                var viewModel = new MultiTimeframeViewModel
                                {
                                    Symbol = symbol,
                                    LastPrice = lastPrice
                                };
                                MainWindow.Instance.RefreshSignalUI(viewModel);
                            });
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"❌ Ticker 업데이트 처리 오류 ({symbol}): {ex.Message}");
                        }
                    },
                    ct: ct
                );

                if (subscribeResult.Success)
                {
                    _subscribedSymbols.Add(symbol);
                    OnLog?.Invoke($"✅ Bybit Ticker 구독 성공: {symbol}");
                }
                else
                {
                    OnLog?.Invoke($"❌ Bybit Ticker 구독 실패 ({symbol}): {subscribeResult.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Ticker 구독 예외 ({symbol}): {ex.Message}");
            }
        }

        /// <summary>
        /// 실시간 캔들 구독 (Kline/Candlestick)
        /// </summary>
        public async Task SubscribeKlineAsync(string symbol, KlineInterval interval, CancellationToken ct = default)
        {
            try
            {
                var subscribeResult = await _socketClient.V5LinearApi.SubscribeToKlineUpdatesAsync(
                    symbol,
                    interval,
                    data =>
                    {
                        try
                        {
                            foreach (var kline in data.Data)
                            {
                                OnKlineUpdate?.Invoke(symbol, new TradingBotBybitKlineUpdate
                                {
                                    Symbol = symbol,
                                    OpenPrice = kline.OpenPrice,
                                    HighPrice = kline.HighPrice,
                                    LowPrice = kline.LowPrice,
                                    ClosePrice = kline.ClosePrice,
                                    Volume = kline.Volume,
                                    StartTime = kline.StartTime
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"❌ Kline 업데이트 처리 오류 ({symbol}): {ex.Message}");
                        }
                    },
                    ct: ct
                );

                if (subscribeResult.Success)
                {
                    OnLog?.Invoke($"✅ Bybit Kline 구독 성공: {symbol} ({interval})");
                }
                else
                {
                    OnLog?.Invoke($"❌ Bybit Kline 구독 실패 ({symbol}): {subscribeResult.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Kline 구독 예외 ({symbol}): {ex.Message}");
            }
        }

        /// <summary>
        /// 계좌 포지션 실시간 구독 (Private Stream)
        /// </summary>
        public async Task SubscribePositionUpdatesAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(AppConfig.BybitApiKey))
            {
                OnLog?.Invoke("⚠️ Bybit API Key 없음 - Position 구독 불가");
                return;
            }

            try
            {
                var subscribeResult = await _socketClient.V5PrivateApi.SubscribeToPositionUpdatesAsync(
                    data =>
                    {
                        try
                        {
                            foreach (var pos in data.Data)
                            {
                                OnPositionUpdate?.Invoke(new TradingBotBybitPositionUpdate
                                {
                                    Symbol = pos.Symbol,
                                    Quantity = pos.Quantity,
                                    Side = pos.Side ?? Bybit.Net.Enums.PositionSide.Buy,
                                    EntryPrice = pos.AveragePrice ?? 0,
                                    UnrealizedPnl = pos.UnrealizedPnl ?? 0,
                                    Leverage = (int)(pos.Leverage ?? 0)
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"❌ Position 업데이트 처리 오류: {ex.Message}");
                        }
                    },
                    ct: ct
                );

                if (subscribeResult.Success)
                {
                    OnLog?.Invoke("✅ Bybit Position 구독 성공");
                }
                else
                {
                    OnLog?.Invoke($"❌ Bybit Position 구독 실패: {subscribeResult.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Position 구독 예외: {ex.Message}");
            }
        }

        /// <summary>
        /// 주문 실시간 구독 (Private Stream)
        /// </summary>
        public async Task SubscribeOrderUpdatesAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(AppConfig.BybitApiKey))
            {
                OnLog?.Invoke("⚠️ Bybit API Key 없음 - Order 구독 불가");
                return;
            }

            try
            {
                var subscribeResult = await _socketClient.V5PrivateApi.SubscribeToOrderUpdatesAsync(
                    data =>
                    {
                        try
                        {
                            foreach (var order in data.Data)
                            {
                                OnOrderUpdate?.Invoke(new TradingBotBybitOrderUpdate
                                {
                                    Symbol = order.Symbol,
                                    OrderId = order.OrderId,
                                    Side = order.Side,
                                    Price = order.Price ?? 0m,
                                    Quantity = order.Quantity,
                                    QuantityFilled = order.QuantityFilled ?? 0m,
                                    OrderStatus = order.Status.ToString()
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"❌ Order 업데이트 처리 오류: {ex.Message}");
                        }
                    },
                    ct: ct
                );

                if (subscribeResult.Success)
                {
                    OnLog?.Invoke("✅ Bybit Order 구독 성공");
                }
                else
                {
                    OnLog?.Invoke($"❌ Bybit Order 구독 실패: {subscribeResult.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Order 구독 예외: {ex.Message}");
            }
        }

        /// <summary>
        /// 모든 구독 시작 (Public + Private)
        /// </summary>
        public async Task StartAllAsync(List<string> symbols, CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            OnLog?.Invoke("🚀 Bybit WebSocket 스트림 시작...");

            try
            {
                // Public Streams
                foreach (var symbol in symbols)
                {
                    await SubscribeTickerAsync(symbol, token);
                    await SubscribeKlineAsync(symbol, KlineInterval.FifteenMinutes, token);
                    await Task.Delay(100, token); // Rate limiting
                }

                // Private Streams (API 키가 있을 때만)
                if (!string.IsNullOrEmpty(AppConfig.BybitApiKey))
                {
                    await SubscribePositionUpdatesAsync(token);
                    await SubscribeOrderUpdatesAsync(token);
                }

                _lastSuccessfulConnection = DateTime.UtcNow;
                _reconnectAttempts = 0;
                OnConnectionStateChanged?.Invoke(true);
                OnLog?.Invoke("📡 Bybit WebSocket 스트림 가동 완료");
                
                // Health check 백그라운드 작업 시작
                _ = HealthCheckLoopAsync(symbols, token);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ Bybit WebSocket 시작 실패: {ex.Message}");
                OnConnectionStateChanged?.Invoke(false);
                _ = AttemptReconnectAsync(symbols, token);
            }
        }

        /// <summary>
        /// 자동 재연결 로직 (Exponential Backoff)
        /// </summary>
        private async Task AttemptReconnectAsync(List<string> symbols, CancellationToken ct)
        {
            await _reconnectLock.WaitAsync(ct);
            try
            {
                if (_isReconnecting) return;
                _isReconnecting = true;

                while (_reconnectAttempts < MAX_RECONNECT_ATTEMPTS && !ct.IsCancellationRequested)
                {
                    _reconnectAttempts++;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, _reconnectAttempts)); // 2, 4, 8, 16, 32초
                    
                    OnLog?.Invoke($"🔄 Bybit WebSocket 재연결 시도 {_reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS} - {delay.TotalSeconds}초 대기...");
                    
                    await Task.Delay(delay, ct);

                    try
                    {
                        // 기존 연결 정리
                        await StopAllAsync();
                        await Task.Delay(1000, ct);
                        
                        // 재연결 시도
                        await StartAllAsync(symbols, ct);
                        
                        OnLog?.Invoke("✅ Bybit WebSocket 재연결 성공!");
                        return;
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"❌ 재연결 실패 ({_reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS}): {ex.Message}");
                    }
                }

                if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
                {
                    OnLog?.Invoke($"⛔ Bybit WebSocket 재연결 포기 - 최대 시도 횟수 초과");
                    OnConnectionStateChanged?.Invoke(false);
                }
            }
            finally
            {
                _isReconnecting = false;
                _reconnectLock.Release();
            }
        }

        /// <summary>
        /// 연결 상태 Health Check (30초마다)
        /// </summary>
        private async Task HealthCheckLoopAsync(List<string> symbols, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    
                    var timeSinceLastSuccess = DateTime.UtcNow - _lastSuccessfulConnection;
                    if (timeSinceLastSuccess > TimeSpan.FromMinutes(2))
                    {
                        OnLog?.Invoke("⚠️ Bybit WebSocket 응답 없음 - 재연결 시도");
                        _ = AttemptReconnectAsync(symbols, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ Health Check 오류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 모든 구독 중지
        /// </summary>
        public async Task StopAllAsync()
        {
            _cts?.Cancel();
            await _socketClient.UnsubscribeAllAsync();
            _subscribedSymbols.Clear();
            OnLog?.Invoke("🛑 Bybit WebSocket 스트림 중지");
        }
    }

    // 업데이트 데이터 모델 (Bybit.Net 타입 충돌 방지를 위해 TradingBot 접두사 사용)
    public class TradingBotBybitKlineUpdate
    {
        public string Symbol { get; set; } = "";
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class TradingBotBybitPositionUpdate
    {
        public string Symbol { get; set; } = "";
        public decimal Quantity { get; set; }
        public PositionSide Side { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public int Leverage { get; set; }
    }

    public class TradingBotBybitOrderUpdate
    {
        public string Symbol { get; set; } = "";
        public string OrderId { get; set; } = "";
        public OrderSide Side { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public decimal QuantityFilled { get; set; }
        public string OrderStatus { get; set; } = "";
    }
}
