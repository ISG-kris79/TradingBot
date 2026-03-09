using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        private decimal _balance;
        private Dictionary<string, PositionInfo> _positions = new();
        private Dictionary<string, decimal> _currentPrices = new();
        private Dictionary<string, int> _symbolLeverages = new();
        private Dictionary<string, MockOrderStatus> _filledOrders = new();
        private Dictionary<string, MockStopOrder> _stopOrders = new();
        // [수정] 거래소별 수수료 시뮬레이션 (기본값: Binance Taker 0.05%)
        private decimal _makerFeeRate = 0.0002m; // 0.02%
        private decimal _takerFeeRate = 0.0005m; // 0.05%
        private readonly Random _random = new Random();
        private readonly object _syncLock = new object();

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
                TriggerStopOrders(symbol, price);
            }
        }

        public Task<decimal> GetBalanceAsync(string asset, CancellationToken token = default)
        {
            return Task.FromResult(_balance);
        }

        public Task<decimal> GetPriceAsync(string symbol, CancellationToken token = default)
        {
            lock (_syncLock)
            {
                return Task.FromResult(_currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0m);
            }
        }

        public Task<List<PositionInfo>> GetPositionsAsync(CancellationToken token = default)
        {
            lock (_syncLock)
            {
                return Task.FromResult(_positions.Values.Select(ClonePosition).ToList());
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

        public Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            lock (_syncLock)
            {
                _stopOrders.Remove(orderId);
                _filledOrders.Remove(orderId);
                return Task.FromResult(true);
            }
        }

        public Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default)
        {
            // 백테스팅 엔진에서 직접 캔들 데이터를 제공하므로 빈 리스트 반환
            return Task.FromResult(new List<IBinanceKline>());
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
            // 백테스팅용 빈 리스트 반환
            return Task.FromResult(new List<IBinanceKline>());
        }

        public Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken ct = default)
        {
            // 백테스팅용 기본 ExchangeInfo 반환
            return Task.FromResult<ExchangeInfo?>(new ExchangeInfo());
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
            return Task.FromResult<(decimal, decimal)?>(null);
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
        /// [Phase 12: PUMP 전략 지원] 지정가 주문 (Mock: 즉시 체결로 시뮬레이션)
        /// </summary>
        public Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            CancellationToken ct = default)
        {
            var result = ExecuteOrder(symbol, side, quantity, price, reduceOnly: false);
            if (!result.Success)
            {
                return Task.FromResult((false, string.Empty));
            }

            lock (_syncLock)
            {
                string orderId = Guid.NewGuid().ToString();
                _filledOrders[orderId] = new MockOrderStatus
                {
                    FilledQuantity = result.ExecutedQuantity,
                    AveragePrice = result.ExecutedPrice
                };

                return Task.FromResult((true, orderId));
            }
        }

        /// <summary>
        /// [Phase 12: PUMP 전략 지원] 주문 상태 확인 (Mock: 항상 체결 완료)
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

                decimal price = _currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0m;
                return Task.FromResult((false, 0m, price));
            }
        }

        /// <summary>
        /// [시장가 주문] 즉시 체결 (Mock: 100% 성공)
        /// </summary>
        public Task<(bool Success, decimal FilledQuantity, decimal AveragePrice)> PlaceMarketOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            CancellationToken ct = default)
        {
            lock (_syncLock)
            {
                decimal currentPrice = _currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 50000m;

                // 시뮬레이션: 0.1% 슬리피지 적용
                decimal slippage = side.ToUpper() == "BUY" ? 1.001m : 0.999m;
                decimal avgPrice = currentPrice * slippage;

                _balance += side.ToUpper() == "BUY" ? -(quantity * avgPrice) : (quantity * avgPrice);

                if (_positions.TryGetValue(symbol, out var position))
                {
                    if ((side.ToUpper() == "BUY" && position.IsLong) || (side.ToUpper() == "SELL" && !position.IsLong))
                    {
                        position.Quantity += quantity;
                    }
                    else
                    {
                        position.Quantity -= quantity;
                        if (position.Quantity <= 0)
                        {
                            _positions.Remove(symbol);
                        }
                    }
                }
                else if (side.ToUpper() == "BUY")
                {
                    _positions.Add(symbol, new PositionInfo
                    {
                        Symbol = symbol,
                        Quantity = quantity,
                        EntryPrice = avgPrice,
                        IsLong = true,
                        Side = Binance.Net.Enums.OrderSide.Buy,
                        Leverage = 20
                    });
                }

                Console.WriteLine($"✅ [Mock] 시장가 체결 완료 - {symbol} {side} {quantity} @ {avgPrice:F4}");
                return Task.FromResult((true, quantity, avgPrice));
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

            if (_random.NextDouble() < 0.01)
            {
                return (false, 0m, 0m);
            }

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

                OpenNewPosition(symbol, normalizedSide == "BUY", quantity, execPrice);
                return (true, quantity, execPrice);
            }

            bool closesExisting = (normalizedSide == "BUY" && !existingPosition.IsLong) || (normalizedSide == "SELL" && existingPosition.IsLong);
            if (closesExisting)
            {
                decimal closeQty = Math.Min(quantity, existingPosition.Quantity);
                if (closeQty > 0)
                {
                    decimal pnl = existingPosition.IsLong
                        ? (execPrice - existingPosition.EntryPrice) * closeQty
                        : (existingPosition.EntryPrice - execPrice) * closeQty;
                    decimal fee = (existingPosition.EntryPrice * closeQty * feeRate) + (execPrice * closeQty * feeRate);
                    _balance += (pnl - fee);

                    existingPosition.Quantity -= closeQty;
                    if (existingPosition.Quantity <= 0)
                    {
                        _positions.Remove(symbol);
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

            decimal oldQty = existingPosition.Quantity;
            decimal newQty = oldQty + quantity;
            existingPosition.EntryPrice = ((existingPosition.EntryPrice * oldQty) + (execPrice * quantity)) / newQty;
            existingPosition.Quantity = newQty;
            existingPosition.Leverage = _symbolLeverages.TryGetValue(symbol, out var leverage) ? leverage : existingPosition.Leverage;

            return (true, quantity, execPrice);
        }

        private void OpenNewPosition(string symbol, bool isLong, decimal quantity, decimal execPrice)
        {
            _positions[symbol] = new PositionInfo
            {
                Symbol = symbol,
                EntryPrice = execPrice,
                Quantity = quantity,
                IsLong = isLong,
                Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                Leverage = _symbolLeverages.TryGetValue(symbol, out var leverage) ? leverage : 20,
                EntryTime = DateTime.Now
            };
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
