using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using TradingBot.Models;
using TradingBot.Shared.Models;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// 백테스팅을 위한 가상 거래소 서비스
    /// </summary>
    public class MockExchangeService : IExchangeService
    {
        private sealed class MockOrderStatus
        {
            public decimal FilledQuantity { get; set; }
            public decimal AveragePrice { get; set; }
        }

        private sealed class MockStopOrder
        {
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public decimal StopPrice { get; set; }
        }

        private sealed class MockLimitOrder
        {
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public decimal LimitPrice { get; set; }
            public DateTime CreatedAtUtc { get; set; }
        }

        private decimal _balance;
        private Dictionary<string, PositionInfo> _positions = new();
        private Dictionary<string, decimal> _currentPrices = new();
        private Dictionary<string, int> _symbolLeverages = new();
        private Dictionary<string, MockOrderStatus> _filledOrders = new();
        private Dictionary<string, MockLimitOrder> _limitOrders = new();
        private Dictionary<string, MockStopOrder> _stopOrders = new();
        private Dictionary<string, decimal> _reservedMargins = new();
        // [수정] 거래소별 수수료 시뮬레이션 (기본값: Binance Taker 0.05%)
        private decimal _makerFeeRate = 0.0002m; // 0.02%
        private decimal _takerFeeRate = 0.0005m; // 0.05%
        private decimal _fundingRate = 0.0001m; // 0.01%
        private DateTime _lastFundingSettlementUtc = DateTime.UtcNow;
        private static readonly TimeSpan FundingSettlementInterval = TimeSpan.FromHours(8);
        private readonly Random _random = new Random();
        private readonly object _syncLock = new object();
        private readonly BinanceRestClient _marketDataClient = new BinanceRestClient();

        public string ExchangeName => "MockExchange";

        public MockExchangeService(decimal initialBalance)
        {
            _balance = initialBalance;
        }

        // 백테스팅 엔진에서 현재 가격을 주입
        public void SetCurrentPrice(string symbol, decimal price)
        {
            lock (_syncLock)
            {
                _currentPrices[symbol] = price;
                TriggerLimitOrders(symbol, price);
                TriggerStopOrders(symbol, price);
                TryApplyFundingSettlement(DateTime.UtcNow);
            }
        }

        public Task<decimal> GetBalanceAsync(string asset, CancellationToken token = default)
        {
            return Task.FromResult(_balance);
        }

        /// <summary>
        /// 시뮬레이션 전용: 가용잔고 + 예약증거금 합산 = 선물 지갑잔고 (WalletBalance)
        /// GetBalanceAsync는 가용잔고(증거금 차감 후)만 반환하기 때문에
        /// Equity 계산 시 이 메서드를 사용해야 정확한 자산이 나온다.
        /// </summary>
        public decimal GetWalletBalance()
        {
            lock (_syncLock)
            {
                return _balance + _reservedMargins.Values.Sum();
            }
        }

        public Task<decimal> GetPriceAsync(string symbol, CancellationToken token = default)
        {
            lock (_syncLock)
            {
                if (_currentPrices.TryGetValue(symbol, out var cachedPrice) && cachedPrice > 0)
                {
                    return Task.FromResult(cachedPrice);
                }
            }

            return GetPublicPriceAsync(symbol, token);
        }

        public Task<List<PositionInfo>> GetPositionsAsync(CancellationToken token = default)
        {
            lock (_syncLock)
            {
                var snapshot = _positions.Values
                    .Select(ClonePositionWithRealtimePnl)
                    .ToList();

                return Task.FromResult(snapshot);
            }
        }

        public Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken token = default, bool reduceOnly = false)
        {
            var result = ExecuteOrder(symbol, side, quantity, price, reduceOnly);
            return Task.FromResult(result.Success);
        }

        public Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken token = default)
        {
            lock (_syncLock)
            {
                _symbolLeverages[symbol] = leverage;
            }

            return Task.FromResult(true);
        }

        public Task<(bool Success, string OrderId)> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
        {
            lock (_syncLock)
            {
                string orderId = Guid.NewGuid().ToString();
                _stopOrders[orderId] = new MockStopOrder
                {
                    Symbol = symbol,
                    Side = side,
                    Quantity = quantity,
                    StopPrice = stopPrice
                };

                return Task.FromResult((true, orderId));
            }
        }

        public Task<(bool Success, string OrderId)> PlaceTrailingStopOrderAsync(
            string symbol, string side, decimal quantity,
            decimal callbackRate, decimal? activationPrice = null,
            CancellationToken ct = default)
        {
            string orderId = Guid.NewGuid().ToString();
            Console.WriteLine($"[MOCK] TRAILING_STOP_MARKET {symbol} callback={callbackRate}%");
            return Task.FromResult((true, orderId));
        }

        public Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            lock (_syncLock)
            {
                _stopOrders.Remove(orderId);
                _limitOrders.Remove(orderId);
                _filledOrders.Remove(orderId);
                return Task.FromResult(true);
            }
        }

        public Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken ct = default)
            => GetBalanceAsync(asset, ct);

        public Task<(bool Success, string OrderId)> PlaceTakeProfitOrderAsync(
            string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
            => Task.FromResult((true, Guid.NewGuid().ToString()));

        public Task CancelAllOrdersAsync(string symbol, CancellationToken ct = default)
        {
            lock (_syncLock)
            {
                _stopOrders.Clear();
                _limitOrders.Clear();
                return Task.CompletedTask;
            }
        }

        public Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default)
        {
            return GetPublicKlinesAsync(symbol, interval, limit, ct);
        }

        // [v2.4.2] 날짜 범위 기반 캔들 조회 (시뮬레이션용)
        public Task<List<IBinanceKline>> GetKlinesAsync(
            string symbol,
            KlineInterval interval,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 1000,
            CancellationToken ct = default)
        {
            return GetPublicKlinesAsync(symbol, interval, startTime, endTime, limit, ct);
        }

        public Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken ct = default)
        {
            return GetPublicExchangeInfoAsync(ct);
        }

        public Task<decimal> GetFundingRateAsync(string symbol, CancellationToken token = default)
        {
            return Task.FromResult(0.0001m); // 기본 0.01% 가정
        }

        public Task<(decimal bestBid, decimal bestAsk)?> GetOrderBookAsync(string symbol, CancellationToken ct = default)
        {
            // Mock: 현재 시뮬레이션 가격 기준으로 ±0.01% 호가창 생성
            if (_currentPrices.TryGetValue(symbol, out decimal price))
            {
                decimal bestBid = price * 0.9999m;
                decimal bestAsk = price * 1.0001m;
                return Task.FromResult<(decimal, decimal)?>((bestBid, bestAsk));
            }

            return GetPublicOrderBookAsync(symbol, ct);
        }

        public async Task<BatchOrderResult> PlaceBatchOrdersAsync(List<BatchOrderRequest> orders, CancellationToken ct = default)
        {
            var result = new BatchOrderResult();
            if (orders == null || orders.Count == 0) return result;

            // Mock에서는 모든 주문이 즉시 성공
            foreach (var order in orders)
            {
                bool success = await PlaceOrderAsync(order.Symbol, order.Side, order.Quantity, order.Price, ct);
                if (success)
                {
                    result.SuccessCount++;
                    result.OrderIds.Add(Guid.NewGuid().ToString());
                }
                else
                {
                    result.FailureCount++;
                    result.Errors.Add("Mock order failed");
                }
            }

            return result;
        }

        /// <summary>
        /// Multi-Assets Mode 조회 (Mock: 항상 활성화로 반환)
        /// </summary>
        public Task<bool> GetMultiAssetsModeAsync(CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// Multi-Assets Mode 설정 (Mock: 항상 성공)
        /// </summary>
        public Task<bool> SetMultiAssetsModeAsync(bool enabled, CancellationToken ct = default)
        {
            Console.WriteLine($"[MockExchange] Multi-Assets Mode {(enabled ? "활성화" : "비활성화")}");
            return Task.FromResult(true);
        }

        /// <summary>
        /// Position Mode 조회 (Mock: 기본 One-way 모드)
        /// </summary>
        public Task<bool> GetPositionModeAsync(CancellationToken ct = default)
        {
            return Task.FromResult(false); // One-way Mode
        }

        /// <summary>
        /// Position Mode 설정 (Mock: 항상 성공)
        /// </summary>
        public Task<bool> SetPositionModeAsync(bool hedgeMode, CancellationToken ct = default)
        {
            Console.WriteLine($"[MockExchange] Position Mode: {(hedgeMode ? "Hedge" : "One-way")}");
            return Task.FromResult(true);
        }

        /// <summary>
        /// [Phase 12: PUMP 전략 지원] 지정가 주문 (Mock: 미체결 큐 등록 후 가격 도달 시 체결)
        /// </summary>
        public Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(side) || quantity <= 0 || price <= 0)
            {
                return Task.FromResult((false, string.Empty));
            }

            lock (_syncLock)
            {
                string orderId = Guid.NewGuid().ToString();
                _limitOrders[orderId] = new MockLimitOrder
                {
                    Symbol = symbol,
                    Side = side,
                    Quantity = quantity,
                    LimitPrice = price,
                    CreatedAtUtc = DateTime.UtcNow
                };

                if (_currentPrices.TryGetValue(symbol, out var currentPrice)
                    && currentPrice > 0
                    && ShouldFillLimitOrder(side, currentPrice, price))
                {
                    var result = ExecuteOrderCore(symbol, side, quantity, price, reduceOnly: false);
                    if (!result.Success || result.ExecutedQuantity <= 0)
                    {
                        _limitOrders.Remove(orderId);
                        return Task.FromResult((false, string.Empty));
                    }

                    _filledOrders[orderId] = new MockOrderStatus
                    {
                        FilledQuantity = result.ExecutedQuantity,
                        AveragePrice = result.ExecutedPrice
                    };

                    _limitOrders.Remove(orderId);
                }

                return Task.FromResult((true, orderId));
            }
        }

        /// <summary>
        /// [시장가 주문] 즉시 체결 (Mock)
        /// </summary>
        public Task<(bool Success, decimal FilledQuantity, decimal AveragePrice)> PlaceMarketOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            CancellationToken ct = default)
        {
            var result = ExecuteOrder(symbol, side, quantity, price: null, reduceOnly: false);
            return Task.FromResult((result.Success, result.ExecutedQuantity, result.ExecutedPrice));
        }

        /// <summary>
        /// [Phase 12: PUMP 전략 지원] 주문 상태 확인 (체결/미체결 모두 조회)
        /// </summary>
        public Task<(bool Filled, decimal FilledQuantity, decimal AveragePrice)> GetOrderStatusAsync(
            string symbol,
            string orderId,
            CancellationToken ct = default)
        {
            lock (_syncLock)
            {
                if (_filledOrders.TryGetValue(orderId, out var status))
                {
                    return Task.FromResult((true, status.FilledQuantity, status.AveragePrice));
                }

                if (_limitOrders.TryGetValue(orderId, out var pending))
                {
                    if (_currentPrices.TryGetValue(pending.Symbol, out var currentPrice)
                        && currentPrice > 0
                        && ShouldFillLimitOrder(pending.Side, currentPrice, pending.LimitPrice))
                    {
                        var result = ExecuteOrderCore(pending.Symbol, pending.Side, pending.Quantity, pending.LimitPrice, reduceOnly: false);
                        if (result.Success && result.ExecutedQuantity > 0)
                        {
                            var filled = new MockOrderStatus
                            {
                                FilledQuantity = result.ExecutedQuantity,
                                AveragePrice = result.ExecutedPrice
                            };

                            _filledOrders[orderId] = filled;
                            _limitOrders.Remove(orderId);
                            return Task.FromResult((true, filled.FilledQuantity, filled.AveragePrice));
                        }
                    }

                    return Task.FromResult((false, 0m, pending.LimitPrice));
                }

                decimal price = _currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0m;
                return Task.FromResult((false, 0m, price));
            }
        }

        private async Task<decimal> GetPublicPriceAsync(string symbol, CancellationToken token)
        {
            try
            {
                var result = await _marketDataClient.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, token);
                if (!result.Success)
                    return 0m;

                lock (_syncLock)
                {
                    _currentPrices[symbol] = result.Data.Price;
                }

                return result.Data.Price;
            }
            catch
            {
                return 0m;
            }
        }

        private async Task<List<IBinanceKline>> GetPublicKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct)
        {
            try
            {
                var result = await _marketDataClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit, ct: ct);
                if (!result.Success || result.Data == null)
                    return new List<IBinanceKline>();

                return result.Data.Cast<IBinanceKline>().ToList();
            }
            catch
            {
                return new List<IBinanceKline>();
            }
        }

        private async Task<List<IBinanceKline>> GetPublicKlinesAsync(
            string symbol,
            KlineInterval interval,
            DateTime? startTime,
            DateTime? endTime,
            int limit,
            CancellationToken ct)
        {
            try
            {
                var result = await _marketDataClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    startTime: startTime,
                    endTime: endTime,
                    limit: limit,
                    ct: ct);

                if (!result.Success || result.Data == null)
                    return new List<IBinanceKline>();

                return result.Data
                    .Cast<IBinanceKline>()
                    .GroupBy(k => k.CloseTime)
                    .Select(g => g.First())
                    .OrderBy(k => k.CloseTime)
                    .ToList();
            }
            catch
            {
                return new List<IBinanceKline>();
            }
        }

        private async Task<ExchangeInfo?> GetPublicExchangeInfoAsync(CancellationToken ct)
        {
            try
            {
                var result = await _marketDataClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
                if (!result.Success || result.Data == null)
                    return new ExchangeInfo();

                var exchangeInfo = new ExchangeInfo();
                foreach (var s in result.Data.Symbols)
                {
                    exchangeInfo.Symbols.Add(new SymbolInfo
                    {
                        Name = s.Name,
                        LotSizeFilter = s.LotSizeFilter != null
                            ? new SymbolFilter
                            {
                                StepSize = s.LotSizeFilter.StepSize,
                                TickSize = 0
                            }
                            : null,
                        PriceFilter = s.PriceFilter != null
                            ? new SymbolFilter
                            {
                                StepSize = 0,
                                TickSize = s.PriceFilter.TickSize
                            }
                            : null
                    });
                }

                return exchangeInfo;
            }
            catch
            {
                return new ExchangeInfo();
            }
        }

        private async Task<(decimal bestBid, decimal bestAsk)?> GetPublicOrderBookAsync(string symbol, CancellationToken ct)
        {
            try
            {
                var result = await _marketDataClient.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, 5, ct);
                if (!result.Success || result.Data == null)
                    return null;

                var bestBidEntry = result.Data.Bids.FirstOrDefault();
                var bestAskEntry = result.Data.Asks.FirstOrDefault();
                if (bestBidEntry == null || bestAskEntry == null)
                    return null;

                var bestBid = bestBidEntry.Price;
                var bestAsk = bestAskEntry.Price;
                if (bestBid <= 0 || bestAsk <= 0)
                    return null;

                return (bestBid, bestAsk);
            }
            catch
            {
                return null;
            }
        }

        private PositionInfo ClonePosition(PositionInfo pos)
        {
            return new PositionInfo
            {
                Symbol = pos.Symbol,
                EntryPrice = pos.EntryPrice,
                Quantity = pos.Quantity,
                IsLong = pos.IsLong,
                Side = pos.Side,
                Leverage = pos.Leverage,
                EntryTime = pos.EntryTime,
                IsPumpStrategy = pos.IsPumpStrategy,
                AiScore = pos.AiScore,
                TakeProfit = pos.TakeProfit,
                StopLoss = pos.StopLoss,
                StopOrderId = pos.StopOrderId,
                TakeProfitStep = pos.TakeProfitStep,
                IsAveragedDown = pos.IsAveragedDown,
                Wave1LowPrice = pos.Wave1LowPrice,
                Wave1HighPrice = pos.Wave1HighPrice,
                Fib0618Level = pos.Fib0618Level,
                Fib0786Level = pos.Fib0786Level,
                Fib1618Target = pos.Fib1618Target,
                HighestROEForTrailing = pos.HighestROEForTrailing,
                PartialProfitStage = pos.PartialProfitStage,
                BreakevenPrice = pos.BreakevenPrice
            };
        }

        private PositionInfo ClonePositionWithRealtimePnl(PositionInfo pos)
        {
            var cloned = ClonePosition(pos);

            decimal markPrice = _currentPrices.TryGetValue(pos.Symbol, out var currentPrice) && currentPrice > 0
                ? currentPrice
                : pos.EntryPrice;

            cloned.UnrealizedPnL = CalculateUnrealizedPnl(cloned, markPrice);
            return cloned;
        }

        private (bool Success, decimal ExecutedQuantity, decimal ExecutedPrice) ExecuteOrder(string symbol, string side, decimal quantity, decimal? price, bool reduceOnly)
        {
            lock (_syncLock)
            {
                return ExecuteOrderCore(symbol, side, quantity, price, reduceOnly);
            }
        }

        private (bool Success, decimal ExecutedQuantity, decimal ExecutedPrice) ExecuteOrderCore(string symbol, string side, decimal quantity, decimal? price, bool reduceOnly)
        {
            decimal basePrice = price ?? (_currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0m);
            if (basePrice <= 0 || quantity <= 0)
            {
                return (false, 0m, 0m);
            }

            // [v3.0.11] 시뮬레이션 랜덤 실패 제거 — 실거래에서만 발생하는 오류를 시뮬에서 재현할 필요 없음

            decimal slippage = (decimal)(_random.NextDouble() * 0.001);
            decimal execPrice = side.ToUpper() == "BUY"
                ? basePrice * (1 + slippage)
                : basePrice * (1 - slippage);

            bool isMaker = price.HasValue;
            decimal feeRate = isMaker ? _makerFeeRate : _takerFeeRate;
            string normalizedSide = side.ToUpperInvariant();

            if (!_positions.TryGetValue(symbol, out var existingPosition))
            {
                if (reduceOnly)
                {
                    return (true, 0m, execPrice);
                }

                if (!OpenNewPosition(symbol, normalizedSide == "BUY", quantity, execPrice))
                {
                    return (false, 0m, 0m);
                }

                return (true, quantity, execPrice);
            }

            bool closesExisting = (normalizedSide == "BUY" && !existingPosition.IsLong) || (normalizedSide == "SELL" && existingPosition.IsLong);
            if (closesExisting)
            {
                decimal closeQty = Math.Min(quantity, existingPosition.Quantity);
                if (closeQty > 0)
                {
                    decimal existingQty = existingPosition.Quantity;
                    decimal pnl = existingPosition.IsLong
                        ? (execPrice - existingPosition.EntryPrice) * closeQty
                        : (existingPosition.EntryPrice - execPrice) * closeQty;
                    decimal fee = (existingPosition.EntryPrice * closeQty * feeRate) + (execPrice * closeQty * feeRate);

                    decimal releasedMargin = 0m;
                    if (_reservedMargins.TryGetValue(symbol, out var currentReservedMargin) && currentReservedMargin > 0m && existingQty > 0m)
                    {
                        decimal closeRatio = closeQty / existingQty;
                        releasedMargin = currentReservedMargin * closeRatio;
                        decimal remainMargin = currentReservedMargin - releasedMargin;

                        if (remainMargin <= 0m)
                            _reservedMargins.Remove(symbol);
                        else
                            _reservedMargins[symbol] = remainMargin;
                    }

                    _balance += releasedMargin + (pnl - fee);

                    existingPosition.Quantity -= closeQty;
                    if (existingPosition.Quantity <= 0)
                    {
                        _positions.Remove(symbol);
                        _reservedMargins.Remove(symbol);
                    }
                }

                decimal remainingOpenQty = quantity - closeQty;
                if (!reduceOnly && remainingOpenQty > 0)
                {
                    OpenNewPosition(symbol, normalizedSide == "BUY", remainingOpenQty, execPrice);
                }

                return (true, closeQty, execPrice);
            }

            if (reduceOnly)
            {
                return (true, 0m, execPrice);
            }

            decimal leverageForExisting = existingPosition.Leverage > 0 ? existingPosition.Leverage : (_symbolLeverages.TryGetValue(symbol, out var lv) ? lv : 20);
            decimal additionalNotional = quantity * execPrice;
            decimal additionalMargin = leverageForExisting > 0 ? additionalNotional / leverageForExisting : additionalNotional;
            if (additionalMargin > 0m)
            {
                if (_balance < additionalMargin)
                {
                    return (false, 0m, 0m);
                }

                _balance -= additionalMargin;

                if (_reservedMargins.TryGetValue(symbol, out var existingMargin))
                    _reservedMargins[symbol] = existingMargin + additionalMargin;
                else
                    _reservedMargins[symbol] = additionalMargin;
            }

            decimal oldQty = existingPosition.Quantity;
            decimal newQty = oldQty + quantity;
            existingPosition.EntryPrice = ((existingPosition.EntryPrice * oldQty) + (execPrice * quantity)) / newQty;
            existingPosition.Quantity = newQty;
            existingPosition.Leverage = _symbolLeverages.TryGetValue(symbol, out var leverage) ? leverage : existingPosition.Leverage;

            return (true, quantity, execPrice);
        }

        private bool OpenNewPosition(string symbol, bool isLong, decimal quantity, decimal execPrice)
        {
            int leverage = _symbolLeverages.TryGetValue(symbol, out var configuredLeverage) ? Math.Max(1, configuredLeverage) : 20;
            decimal notional = quantity * execPrice;
            decimal requiredMargin = leverage > 0 ? notional / leverage : notional;

            if (requiredMargin > 0m)
            {
                if (_balance < requiredMargin)
                    return false;

                _balance -= requiredMargin;
            }

            _positions[symbol] = new PositionInfo
            {
                Symbol = symbol,
                EntryPrice = execPrice,
                Quantity = quantity,
                IsLong = isLong,
                Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                Leverage = leverage,
                EntryTime = DateTime.Now
            };

            _reservedMargins[symbol] = requiredMargin;
            return true;
        }

        private static decimal CalculateUnrealizedPnl(PositionInfo position, decimal markPrice)
        {
            if (markPrice <= 0m || position.EntryPrice <= 0m || position.Quantity <= 0m)
                return 0m;

            return position.IsLong
                ? (markPrice - position.EntryPrice) * position.Quantity
                : (position.EntryPrice - markPrice) * position.Quantity;
        }

        private static bool ShouldFillLimitOrder(string side, decimal currentPrice, decimal limitPrice)
        {
            if (currentPrice <= 0m || limitPrice <= 0m)
                return false;

            bool isBuy = string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase);
            return isBuy ? currentPrice <= limitPrice : currentPrice >= limitPrice;
        }

        private void TriggerLimitOrders(string symbol, decimal currentPrice)
        {
            var triggeredIds = new List<string>();

            foreach (var kvp in _limitOrders)
            {
                var order = kvp.Value;
                if (!string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ShouldFillLimitOrder(order.Side, currentPrice, order.LimitPrice))
                    continue;

                var result = ExecuteOrderCore(order.Symbol, order.Side, order.Quantity, order.LimitPrice, reduceOnly: false);
                if (!result.Success || result.ExecutedQuantity <= 0m)
                    continue;

                _filledOrders[kvp.Key] = new MockOrderStatus
                {
                    FilledQuantity = result.ExecutedQuantity,
                    AveragePrice = result.ExecutedPrice
                };

                triggeredIds.Add(kvp.Key);
            }

            foreach (var orderId in triggeredIds)
            {
                _limitOrders.Remove(orderId);
            }
        }

        private void TryApplyFundingSettlement(DateTime nowUtc)
        {
            if (_positions.Count == 0)
            {
                _lastFundingSettlementUtc = nowUtc;
                return;
            }

            var elapsed = nowUtc - _lastFundingSettlementUtc;
            int periods = (int)(elapsed.TotalSeconds / FundingSettlementInterval.TotalSeconds);
            if (periods <= 0)
                return;

            decimal totalFunding = 0m;
            foreach (var position in _positions.Values)
            {
                decimal markPrice = _currentPrices.TryGetValue(position.Symbol, out var price) && price > 0m
                    ? price
                    : position.EntryPrice;

                if (markPrice <= 0m || position.Quantity <= 0m)
                    continue;

                decimal notional = position.Quantity * markPrice;
                decimal onePeriodFunding = notional * _fundingRate;
                decimal signedFunding = position.IsLong ? -onePeriodFunding : onePeriodFunding;
                totalFunding += signedFunding * periods;
            }

            _balance += totalFunding;
            _lastFundingSettlementUtc = _lastFundingSettlementUtc.AddSeconds(FundingSettlementInterval.TotalSeconds * periods);
        }

        private void TriggerStopOrders(string symbol, decimal currentPrice)
        {
            var triggeredIds = new List<string>();

            foreach (var kvp in _stopOrders)
            {
                var stopOrder = kvp.Value;
                if (!string.Equals(stopOrder.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool shouldTrigger = stopOrder.Side.ToUpperInvariant() == "SELL"
                    ? currentPrice <= stopOrder.StopPrice
                    : currentPrice >= stopOrder.StopPrice;

                if (!shouldTrigger)
                {
                    continue;
                }

                ExecuteOrderCore(stopOrder.Symbol, stopOrder.Side, stopOrder.Quantity, currentPrice, reduceOnly: true);
                triggeredIds.Add(kvp.Key);
            }

            foreach (var orderId in triggeredIds)
            {
                _stopOrders.Remove(orderId);
            }
        }
    }
}
