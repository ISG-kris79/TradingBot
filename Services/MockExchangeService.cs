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
        private decimal _balance;
        private Dictionary<string, PositionInfo> _positions = new();
        private Dictionary<string, decimal> _currentPrices = new();
        // [수정] 거래소별 수수료 시뮬레이션 (기본값: Binance Taker 0.05%)
        private decimal _makerFeeRate = 0.0002m; // 0.02%
        private decimal _takerFeeRate = 0.0005m; // 0.05%
        private readonly Random _random = new Random();

        public string ExchangeName => "MockExchange";

        public MockExchangeService(decimal initialBalance)
        {
            _balance = initialBalance;
        }

        // 백테스팅 엔진에서 현재 가격을 주입
        public void SetCurrentPrice(string symbol, decimal price)
        {
            _currentPrices[symbol] = price;
        }

        public Task<decimal> GetBalanceAsync(string asset, CancellationToken token = default)
        {
            return Task.FromResult(_balance);
        }

        public Task<decimal> GetPriceAsync(string symbol, CancellationToken token = default)
        {
            return Task.FromResult(_currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0m);
        }

        public Task<List<PositionInfo>> GetPositionsAsync(CancellationToken token = default)
        {
            return Task.FromResult(_positions.Values.ToList());
        }

        public Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken token = default, bool reduceOnly = false)
        {
            // 간단한 시뮬레이션: 시장가 즉시 체결, 지정가는 현재가 도달 시 체결 가정(여기선 즉시 체결로 단순화)
            decimal execPrice = price ?? (_currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0);
            if (execPrice == 0) return Task.FromResult(false);

            // [추가] 주문 실패 시뮬레이션 (1% 확률로 실패)
            if (_random.NextDouble() < 0.01)
            {
                return Task.FromResult(false);
            }

            // [추가] 슬리피지 시뮬레이션 (0 ~ 0.1% 불리한 가격 체결)
            decimal slippage = (decimal)(_random.NextDouble() * 0.001);
            if (side.ToUpper() == "BUY") execPrice *= (1 + slippage);
            else execPrice *= (1 - slippage);

            bool isMaker = price.HasValue; // 지정가 주문은 Maker로 가정
            decimal feeRate = isMaker ? _makerFeeRate : _takerFeeRate;

            decimal cost = execPrice * quantity; // 레버리지 미적용 단순 계산 (증거금 차감 로직은 생략하고 PnL만 추적)

            if (reduceOnly && !_positions.ContainsKey(symbol))
            {
                return Task.FromResult(true);
            }

            if (side.ToUpper() == "BUY") // LONG 진입 or SHORT 청산
            {
                if (_positions.ContainsKey(symbol) && !_positions[symbol].IsLong) // Short Close
                {
                    var pos = _positions[symbol];
                    decimal pnl = (pos.EntryPrice - execPrice) * pos.Quantity;
                    decimal fee = (pos.EntryPrice * pos.Quantity * feeRate) + (execPrice * pos.Quantity * feeRate);

                    _balance += (pnl - fee);
                    _positions.Remove(symbol);
                }
                else // Long Open
                {
                    if (reduceOnly)
                    {
                        return Task.FromResult(true);
                    }

                    _positions[symbol] = new PositionInfo
                    {
                        Symbol = symbol,
                        EntryPrice = execPrice,
                        Quantity = quantity,
                        IsLong = true,
                        Side = OrderSide.Buy
                    };
                }
            }
            else // SELL: SHORT 진입 or LONG 청산
            {
                if (_positions.ContainsKey(symbol) && _positions[symbol].IsLong) // Long Close
                {
                    var pos = _positions[symbol];
                    decimal pnl = (execPrice - pos.EntryPrice) * pos.Quantity;
                    decimal fee = (pos.EntryPrice * pos.Quantity * feeRate) + (execPrice * pos.Quantity * feeRate);

                    _balance += (pnl - fee);
                    _positions.Remove(symbol);
                }
                else // Short Open
                {
                    if (reduceOnly)
                    {
                        return Task.FromResult(true);
                    }

                    _positions[symbol] = new PositionInfo
                    {
                        Symbol = symbol,
                        EntryPrice = execPrice,
                        Quantity = quantity,
                        IsLong = false,
                        Side = OrderSide.Sell
                    };
                }
            }

            return Task.FromResult(true);
        }

        public Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken token = default) => Task.FromResult(true);

        public Task<bool> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
        {
            // 스탑 오더 시뮬레이션 (간단히 즉시 실행으로 처리)
            return PlaceOrderAsync(symbol, side, quantity, stopPrice, ct, reduceOnly: true);
        }

        public Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            // Mock은 Order ID 추적 안 함 - 항상 성공 반환
            return Task.FromResult(true);
        }

        public Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default)
        {
            // 백테스팅 엔진에서 직접 캔들 데이터를 제공하므로 빈 리스트 반환
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
            // Mock: 지정가 주문을 즉시 체결로 시뮬레이션
            bool success = PlaceOrderAsync(symbol, side, quantity, price, ct).Result;
            string orderId = success ? Guid.NewGuid().ToString() : string.Empty;
            return Task.FromResult((success, orderId));
        }

        /// <summary>
        /// [Phase 12: PUMP 전략 지원] 주문 상태 확인 (Mock: 항상 체결 완료)
        /// </summary>
        public Task<(bool Filled, decimal FilledQuantity, decimal AveragePrice)> GetOrderStatusAsync(
            string symbol,
            string orderId,
            CancellationToken ct = default)
        {
            // Mock: 항상 체결 완료로 반환
            decimal price = _currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0m;
            return Task.FromResult((true, 1m, price));
        }
    }
}
