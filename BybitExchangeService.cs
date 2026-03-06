using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// Bybit 거래소 서비스 (V5 API)
    /// Phase 12: 완전 호환 및 안정화 - 재시도 로직, Rate Limiting, Circuit Breaker 구현
    /// </summary>
    public class BybitExchangeService : IExchangeService, IDisposable
    {
        private readonly BybitRestClient _client;
        private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim(10, 10); // 동시 요청 10개 제한
        private DateTime _lastApiCall = DateTime.MinValue;
        private readonly TimeSpan _minApiInterval = TimeSpan.FromMilliseconds(100); // 최소 100ms 간격
        private int _consecutiveErrors = 0;
        private DateTime _circuitBreakerUntil = DateTime.MinValue;
        private const int MAX_RETRY_ATTEMPTS = 3;
        private const int CIRCUIT_BREAKER_THRESHOLD = 5;
        private bool _disposed = false;

        public string ExchangeName => "Bybit";

        public BybitExchangeService(string apiKey, string apiSecret)
        {
            _client = new BybitRestClient(options =>
            {
                if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                }
                // [Phase 4] 서버 시간 동기화 설정
                options.AutoTimestamp = true;
                options.TimestampRecalculationInterval = TimeSpan.FromHours(3);
            });
        }

        public async Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default)
        {
            decimal? result = await ExecuteWithRetryAsync<decimal>(async () =>
            {
                // Unified Trading Account 기준 (Bybit.Net V5 API)
                var apiResult = await _client.V5Api.Account.GetBalancesAsync(Bybit.Net.Enums.AccountType.Unified, ct: ct);
                if (!HandleError(apiResult)) return 0m;
                if (apiResult.Data?.List == null) return 0m;

                var accountData = apiResult.Data.List.FirstOrDefault();
                if (accountData == null) return 0m;

                var coin = accountData.Assets?.FirstOrDefault(c => c.Asset == asset);
                return coin?.WalletBalance ?? 0m;
            }, $"GetBalance({asset})", ct);

            return result ?? 0m;
        }

        public async Task<decimal> GetPriceAsync(string symbol, CancellationToken ct = default)
        {
            decimal? result = await ExecuteWithRetryAsync<decimal>(async () =>
            {
                var apiResult = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Bybit.Net.Enums.Category.Linear, symbol, ct: ct);
                if (!HandleError(apiResult)) return 0m;
                if (apiResult.Data?.List == null) return 0m;

                var ticker = apiResult.Data.List.FirstOrDefault();
                return ticker?.LastPrice ?? 0m;
            }, $"GetPrice({symbol})", ct);

            return result ?? 0m;
        }

        public async Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default, bool reduceOnly = false)
        {
            // [추가] 시뮬레이션 모드 체크
            if (AppConfig.Current?.Trading?.IsSimulationMode == true)
            {
                Console.WriteLine($"[시뮬레이션] {symbol} {side} 주문 (수량: {quantity}, 가격: {price?.ToString() ?? "시장가"}, ReduceOnly: {reduceOnly})");
                return true; // 시뮬레이션 모드에서는 항상 성공
            }

            bool? result = await ExecuteWithRetryAsync<bool>(async () =>
            {
                var orderSide = side.ToUpper() == "BUY" ? Bybit.Net.Enums.OrderSide.Buy : Bybit.Net.Enums.OrderSide.Sell;
                var orderType = price.HasValue ? Bybit.Net.Enums.NewOrderType.Limit : Bybit.Net.Enums.NewOrderType.Market;

                var apiResult = await _client.V5Api.Trading.PlaceOrderAsync(
                    Bybit.Net.Enums.Category.Linear,
                    symbol,
                    orderSide,
                    orderType,
                    quantity,
                    price: price,
                    reduceOnly: reduceOnly,
                    ct: ct
                );

                if (apiResult.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Bybit] 주문 성공: {symbol} {side} {quantity} @ {price?.ToString() ?? "Market"}");
                    Console.WriteLine($"✅ [Bybit] 주문 성공 - {symbol} {side} {quantity} (OrderId: {apiResult.Data?.OrderId})");
                    return true;
                }

                // 에러 처리
                Console.WriteLine($"❌ [Bybit] 주문 실패 - {symbol} {side} {quantity}");
                Console.WriteLine($"   에러 코드: {apiResult.Error?.Code}");
                Console.WriteLine($"   에러 메시지: {apiResult.Error?.Message}");
                HandleError(apiResult);
                return false;
            }, $"PlaceOrder({symbol},{side})", ct);

            return result ?? false;
        }

        public async Task<bool> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
        {
            var orderSide = side.ToUpper() == "BUY" ? Bybit.Net.Enums.OrderSide.Buy : Bybit.Net.Enums.OrderSide.Sell;

            // Bybit V5 Stop Order (Conditional Order)
            var result = await _client.V5Api.Trading.PlaceOrderAsync(
                Bybit.Net.Enums.Category.Linear,
                symbol,
                orderSide,
                Bybit.Net.Enums.NewOrderType.Market, // Stop Market
                quantity,
                triggerPrice: stopPrice,
                triggerDirection: orderSide == Bybit.Net.Enums.OrderSide.Buy ? Bybit.Net.Enums.TriggerDirection.Rise : Bybit.Net.Enums.TriggerDirection.Fall, // 롱이면 상승돌파, 숏이면 하락돌파
                ct: ct
            );

            return HandleError(result);
        }

        public async Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            var result = await _client.V5Api.Trading.CancelOrderAsync(Bybit.Net.Enums.Category.Linear, symbol, orderId, ct: ct);
            if (result.Success) return true;
            HandleError(result);
            return false;
        }

        public async Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken ct = default)
        {
            var result = await _client.V5Api.Account.SetLeverageAsync(Bybit.Net.Enums.Category.Linear, symbol, leverage, leverage, ct: ct);
            // 110043: Leverage not modified (이미 설정된 값과 동일) - 성공으로 간주
            if (result.Success || result.Error?.Code == 110043) return true;
            // 에러 로그
            if (result.Error != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Bybit Leverage Error] {result.Error.Code}: {result.Error.Message}");
            }
            return false;
        }

        public async Task<List<PositionInfo>> GetPositionsAsync(CancellationToken ct = default)
        {
            var result = await _client.V5Api.Trading.GetPositionsAsync(Bybit.Net.Enums.Category.Linear, ct: ct);
            if (!HandleError(result)) return new List<PositionInfo>();
            if (result.Data?.List == null) return new List<PositionInfo>();

            return result.Data.List
                .Where(p => p.Quantity > 0)
                .Select(p => new PositionInfo
                {
                    Symbol = p.Symbol,
                    Side = p.Side == Bybit.Net.Enums.PositionSide.Buy ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell,
                    IsLong = p.Side == Bybit.Net.Enums.PositionSide.Buy,
                    Quantity = p.Quantity,
                    EntryPrice = p.AveragePrice ?? 0,
                    UnrealizedPnL = p.UnrealizedPnl ?? 0,
                    Leverage = (int)(p.Leverage ?? 0m)
                })
                .ToList();
        }

        public async Task<List<IBinanceKline>> GetKlinesAsync(string symbol, Binance.Net.Enums.KlineInterval interval, int limit, CancellationToken ct = default)
        {
            var bybitInterval = ConvertInterval(interval);
            var result = await _client.V5Api.ExchangeData.GetKlinesAsync(Bybit.Net.Enums.Category.Linear, symbol, bybitInterval, limit: limit, ct: ct);

            if (!HandleError(result)) return new List<IBinanceKline>();
            if (result.Data?.List == null) return new List<IBinanceKline>();

            // Bybit Kline -> IBinanceKline 변환
            return result.Data.List.Select(k => new BybitKlineWrapper(k, interval)).Cast<IBinanceKline>().ToList();
        }

        public async Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken ct = default)
        {
            var result = await _client.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(Bybit.Net.Enums.Category.Linear, ct: ct);
            if (!HandleError(result)) return null;
            if (result.Data?.List == null) return null;

            var info = new ExchangeInfo();
            foreach (var item in result.Data.List)
            {
                if (item.LotSizeFilter == null || item.PriceFilter == null) continue;

                info.Symbols.Add(new SymbolInfo
                {
                    Name = item.Name,
                    LotSizeFilter = new SymbolFilter { StepSize = item.LotSizeFilter.QuantityStep },
                    PriceFilter = new SymbolFilter { TickSize = item.PriceFilter.TickSize }
                });
            }
            return info;
        }

        public async Task<decimal> GetFundingRateAsync(string symbol, CancellationToken token = default)
        {
            var result = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Bybit.Net.Enums.Category.Linear, symbol, ct: token);
            if (!HandleError(result)) return 0;
            if (result.Data?.List == null || !result.Data.List.Any()) return 0;

            return result.Data.List.First().FundingRate ?? 0;
        }

        public async Task<(decimal bestBid, decimal bestAsk)?> GetOrderBookAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.V5Api.ExchangeData.GetOrderbookAsync(Bybit.Net.Enums.Category.Linear, symbol, limit: 5, ct: ct);
                if (!HandleError(result) || result.Data == null) return null;

                var bestBid = result.Data.Bids.FirstOrDefault()?.Price ?? 0;
                var bestAsk = result.Data.Asks.FirstOrDefault()?.Price ?? 0;

                return (bestBid, bestAsk);
            }
            catch { return null; }
        }

        // [Phase 12: PUMP 전략 지원] 지정가 주문
        public async Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            CancellationToken ct = default)
        {
            try
            {
                // 정밀도 보정
                var exchangeInfo = await GetExchangeInfoAsync(ct);
                if (exchangeInfo != null)
                {
                    var symbolData = exchangeInfo.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolData != null)
                    {
                        decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                        quantity = Math.Floor(quantity / stepSize) * stepSize;

                        decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.01m;
                        price = Math.Floor(price / tickSize) * tickSize;
                    }
                }

                if (quantity <= 0) return (false, string.Empty);

                var orderSide = side.ToUpper() == "BUY" ? Bybit.Net.Enums.OrderSide.Buy : Bybit.Net.Enums.OrderSide.Sell;

                var apiResult = await _client.V5Api.Trading.PlaceOrderAsync(
                    Bybit.Net.Enums.Category.Linear,
                    symbol,
                    orderSide,
                    Bybit.Net.Enums.NewOrderType.Limit,
                    quantity,
                    price: price,
                    timeInForce: Bybit.Net.Enums.TimeInForce.GoodTillCanceled,
                    ct: ct
                );

                if (apiResult.Success && apiResult.Data != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Bybit] 지정가 주문 성공: {symbol} {side} {quantity} @ {price}");
                    return (true, apiResult.Data.OrderId);
                }

                HandleError(apiResult);
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bybit] PlaceLimitOrder 오류: {ex.Message}");
                return (false, string.Empty);
            }
        }

        // [Phase 12: PUMP 전략 지원] 주문 상태 확인
        public async Task<(bool Filled, decimal FilledQuantity, decimal AveragePrice)> GetOrderStatusAsync(
            string symbol,
            string orderId,
            CancellationToken ct = default)
        {
            try
            {
                var apiResult = await _client.V5Api.Trading.GetOrdersAsync(
                    Bybit.Net.Enums.Category.Linear,
                    symbol: symbol,
                    orderId: orderId,
                    ct: ct
                );

                if (!apiResult.Success || apiResult.Data?.List == null)
                {
                    HandleError(apiResult);
                    return (false, 0m, 0m);
                }

                var order = apiResult.Data.List.FirstOrDefault();
                if (order == null)
                {
                    return (false, 0m, 0m);
                }

                bool isFilled = order.Status == Bybit.Net.Enums.OrderStatus.Filled;
                bool isPartiallyFilled = order.Status == Bybit.Net.Enums.OrderStatus.PartiallyFilled;

                decimal filledQty = order.QuantityFilled ?? 0;
                decimal avgPrice = order.AveragePrice ?? order.Price ?? 0;

                return (isFilled || isPartiallyFilled, filledQty, avgPrice);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bybit] GetOrderStatus 오류: {ex.Message}");
                return (false, 0m, 0m);
            }
        }

        // [Phase 12] 강화된 에러 핸들링 및 로깅
        private bool HandleError<T>(CryptoExchange.Net.Objects.WebCallResult<T> result)
        {
            if (!result.Success)
            {
                // 110043: Leverage not modified (이미 설정된 값과 동일) - 성공으로 간주
                if (result.Error?.Code == 110043) return true;

                _consecutiveErrors++;

                string msg = $"[Bybit Error #{result.Error?.Code}] {result.Error?.Message}";

                // 더 상세한 에러 코드 매핑
                msg += GetErrorDescription(result.Error?.Code ?? 0);

                System.Diagnostics.Debug.WriteLine(msg);

                // Circuit Breaker 활성화 체크
                if (_consecutiveErrors >= CIRCUIT_BREAKER_THRESHOLD)
                {
                    _circuitBreakerUntil = DateTime.UtcNow.AddMinutes(5);
                    System.Diagnostics.Debug.WriteLine($"[Bybit] Circuit Breaker 활성화 - {_circuitBreakerUntil:HH:mm:ss}까지 대기");
                }

                return false;
            }

            // 성공 시 에러 카운터 리셋
            _consecutiveErrors = 0;
            return true;
        }

        private string GetErrorDescription(int code)
        {
            return code switch
            {
                10001 => " (파라미터 오류 - 입력값 확인 필요)",
                10002 => " (유효하지 않은 요청)",
                10003 => " (인증 오류 - API Key 확인)",
                10004 => " (잘못된 서명)",
                10005 => " (권한 거부)",
                10006 => " (Rate Limit 초과 - 요청 속도 조절 필요)",
                10007 => " (IP 차단 - VPN 또는 IP 화이트리스트 확인)",
                10016 => " (서버 오류 - 잠시 후 재시도)",
                10017 => " (요청 시간 초과)",
                10018 => " (타임스탬프 오류 - 시스템 시간 동기화 필요)",
                110001 => " (심볼 없음)",
                110003 => " (수량 오류 - 최소/최대 수량 확인)",
                110004 => " (가격 오류 - 가격 범위 확인)",
                110007 => " (레버리지 오류 - 허용 범위 초과)",
                110043 => " (레버리지 미변경 - 이미 설정됨)",
                170124 => " (마진 부족)",
                170131 => " (잔고 부족 - 입금 필요)",
                170136 => " (포지션 없음)",
                170137 => " (포지션 수량 부족)",
                _ => string.Empty
            };
        }

        /// <summary>
        /// API 호출 전 Rate Limiting 및 Circuit Breaker 체크
        /// </summary>
        private async Task<bool> PreApiCallCheckAsync(CancellationToken ct)
        {
            // Circuit Breaker 체크
            if (DateTime.UtcNow < _circuitBreakerUntil)
            {
                var waitTime = _circuitBreakerUntil - DateTime.UtcNow;
                System.Diagnostics.Debug.WriteLine($"[Bybit] Circuit Breaker 활성 - {waitTime.TotalSeconds:F0}초 대기 중");
                return false;
            }

            // Rate Limiting
            await _rateLimiter.WaitAsync(ct);
            try
            {
                var timeSinceLastCall = DateTime.UtcNow - _lastApiCall;
                if (timeSinceLastCall < _minApiInterval)
                {
                    var delay = _minApiInterval - timeSinceLastCall;
                    await Task.Delay(delay, ct);
                }
                _lastApiCall = DateTime.UtcNow;
            }
            finally
            {
                _rateLimiter.Release();
            }

            return true;
        }

        /// <summary>
        /// 재시도 로직이 포함된 API 호출 래퍼
        /// </summary>
        private async Task<T?> ExecuteWithRetryAsync<T>(Func<Task<T>> apiCall, string operationName, CancellationToken ct = default)
        {
            for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
            {
                try
                {
                    if (!await PreApiCallCheckAsync(ct))
                        return default;

                    var result = await apiCall();
                    return result;
                }
                catch (Exception ex) when (attempt < MAX_RETRY_ATTEMPTS)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    System.Diagnostics.Debug.WriteLine($"[Bybit] {operationName} 실패 (시도 {attempt}/{MAX_RETRY_ATTEMPTS}) - {delay.TotalSeconds}초 후 재시도: {ex.Message}");
                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Bybit] {operationName} 최종 실패: {ex.Message}");
                    throw;
                }
            }

            return default;
        }

        private Bybit.Net.Enums.KlineInterval ConvertInterval(Binance.Net.Enums.KlineInterval interval)
        {
            return interval switch
            {
                Binance.Net.Enums.KlineInterval.OneMinute => Bybit.Net.Enums.KlineInterval.OneMinute,
                Binance.Net.Enums.KlineInterval.ThreeMinutes => Bybit.Net.Enums.KlineInterval.ThreeMinutes,
                Binance.Net.Enums.KlineInterval.FiveMinutes => Bybit.Net.Enums.KlineInterval.FiveMinutes,
                Binance.Net.Enums.KlineInterval.FifteenMinutes => Bybit.Net.Enums.KlineInterval.FifteenMinutes,
                Binance.Net.Enums.KlineInterval.ThirtyMinutes => Bybit.Net.Enums.KlineInterval.ThirtyMinutes,
                Binance.Net.Enums.KlineInterval.OneHour => Bybit.Net.Enums.KlineInterval.OneHour,
                Binance.Net.Enums.KlineInterval.FourHour => Bybit.Net.Enums.KlineInterval.FourHours,
                Binance.Net.Enums.KlineInterval.OneDay => Bybit.Net.Enums.KlineInterval.OneDay,
                _ => Bybit.Net.Enums.KlineInterval.OneHour
            };
        }

        // IBinanceKline 인터페이스 구현을 위한 래퍼 클래스
        private class BybitKlineWrapper : IBinanceKline
        {
            private readonly Bybit.Net.Objects.Models.V5.BybitKline _kline;
            private readonly Binance.Net.Enums.KlineInterval _interval;

            public BybitKlineWrapper(Bybit.Net.Objects.Models.V5.BybitKline kline, Binance.Net.Enums.KlineInterval interval)
            {
                _kline = kline;
                _interval = interval;
            }

            public decimal OpenPrice
            {
                get => _kline.OpenPrice;
                set => _kline.OpenPrice = value;
            }
            public decimal HighPrice
            {
                get => _kline.HighPrice;
                set => _kline.HighPrice = value;
            }
            public decimal LowPrice
            {
                get => _kline.LowPrice;
                set => _kline.LowPrice = value;
            }
            public decimal ClosePrice
            {
                get => _kline.ClosePrice;
                set => _kline.ClosePrice = value;
            }
            public decimal Volume
            {
                get => _kline.Volume;
                set => _kline.Volume = value;
            }
            public DateTime OpenTime
            {
                get => _kline.StartTime;
                set => _kline.StartTime = value;
            }
            public DateTime CloseTime
            {
                get => _kline.StartTime.AddSeconds(GetSeconds(_interval));
                set { /* Read-only derived */ }
            }
            public decimal QuoteVolume
            {
                get => _kline.QuoteVolume;
                set => _kline.QuoteVolume = value;
            }
            public int TradeCount { get; set; }
            public decimal TakerBuyBaseVolume { get; set; }
            public decimal TakerBuyQuoteVolume { get; set; }

            private int GetSeconds(Binance.Net.Enums.KlineInterval interval)
            {
                return interval switch
                {
                    Binance.Net.Enums.KlineInterval.OneMinute => 60,
                    Binance.Net.Enums.KlineInterval.FifteenMinutes => 900,
                    Binance.Net.Enums.KlineInterval.OneHour => 3600,
                    _ => 3600
                };
            }
        }

        /// <summary>
        /// Batch Order 실행 (Bybit는 REST API로 순차 처리)
        /// </summary>
        public async Task<BatchOrderResult> PlaceBatchOrdersAsync(List<BatchOrderRequest> orders, CancellationToken ct = default)
        {
            var result = new BatchOrderResult();
            if (orders == null || orders.Count == 0) return result;

            foreach (var order in orders)
            {
                try
                {
                    var orderSide = order.Side.ToUpper() == "BUY" ? Bybit.Net.Enums.OrderSide.Buy : Bybit.Net.Enums.OrderSide.Sell;
                    var orderType = order.OrderType?.ToUpper() == "MARKET"
                        ? Bybit.Net.Enums.NewOrderType.Market
                        : Bybit.Net.Enums.NewOrderType.Limit;

                    var orderResult = await _client.V5Api.Trading.PlaceOrderAsync(
                        Bybit.Net.Enums.Category.Linear,
                        order.Symbol,
                        orderSide,
                        orderType,
                        order.Quantity,
                        price: orderType == Bybit.Net.Enums.NewOrderType.Limit ? order.Price : null,
                        ct: ct
                    );

                    if (orderResult.Success)
                    {
                        result.SuccessCount++;
                        result.OrderIds.Add(orderResult.Data?.OrderId ?? "");
                    }
                    else
                    {
                        result.FailureCount++;
                        result.Errors.Add($"{orderResult.Error?.Code}: {orderResult.Error?.Message}");
                    }

                    // Rate limit 방지
                    await Task.Delay(100, ct);
                }
                catch (Exception ex)
                {
                    result.FailureCount++;
                    result.Errors.Add($"예외: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Multi-Assets Mode 조회 (Bybit는 Unified Account에서 기본 지원)
        /// </summary>
        public Task<bool> GetMultiAssetsModeAsync(CancellationToken ct = default)
        {
            // Bybit Unified Account는 기본적으로 멀티 에셋 담보 지원
            return Task.FromResult(true);
        }

        /// <summary>
        /// Multi-Assets Mode 설정 (Bybit는 Unified Account에서 기본 지원, 설정 불필요)
        /// </summary>
        public Task<bool> SetMultiAssetsModeAsync(bool enabled, CancellationToken ct = default)
        {
            // Bybit는 Unified Account에서 자동으로 멀티 에셋 지원
            System.Diagnostics.Debug.WriteLine("[Bybit] Multi-Assets Mode는 Unified Account에서 기본 지원됩니다.");
            return Task.FromResult(true);
        }

        /// <summary>
        /// Position Mode 조회 (Bybit는 One-way/Hedge 모드 지원)
        /// </summary>
        public async Task<bool> GetPositionModeAsync(CancellationToken ct = default)
        {
            try
            {
                // Bybit V5 API에서는 계정 설정을 통해 포지션 모드를 확인
                // 현재 Bybit.Net에서 직접 API가 없으므로 기본 One-way 모드로 가정
                System.Diagnostics.Debug.WriteLine("[Bybit] Position Mode API not available in current Bybit.Net version");
                return false; // One-way mode 기본값
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bybit] GetPositionMode Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Position Mode 설정 (Hedge Mode vs One-way Mode)
        /// </summary>
        public async Task<bool> SetPositionModeAsync(bool hedgeMode, CancellationToken ct = default)
        {
            try
            {
                var mode = hedgeMode ? Bybit.Net.Enums.PositionMode.BothSides : Bybit.Net.Enums.PositionMode.MergedSingle;
                var result = await _client.V5Api.Account.SwitchPositionModeAsync(
                    Bybit.Net.Enums.Category.Linear,
                    mode,
                    ct: ct
                );

                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Bybit] Position Mode: {(hedgeMode ? "Hedge" : "One-way")} 설정 완료");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Bybit] SetPositionMode Error: {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bybit] SetPositionMode Exception: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                _rateLimiter?.Dispose();
                _client?.Dispose();
            }
            catch { }
            finally
            {
                _disposed = true;
            }
            
            GC.SuppressFinalize(this);
        }
    }
}
