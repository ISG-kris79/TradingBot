using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
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

        public Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken token = default)
        {
            // 간단한 시뮬레이션: 시장가 즉시 체결, 지정가는 현재가 도달 시 체결 가정(여기선 즉시 체결로 단순화)
            decimal execPrice = price ?? (_currentPrices.ContainsKey(symbol) ? _currentPrices[symbol] : 0);
            if (execPrice == 0) return Task.FromResult(false);

            decimal cost = execPrice * quantity; // 레버리지 미적용 단순 계산 (증거금 차감 로직은 생략하고 PnL만 추적)

            if (side.ToUpper() == "BUY") // LONG 진입 or SHORT 청산
            {
                if (_positions.ContainsKey(symbol) && !_positions[symbol].IsLong) // Short Close
                {
                    var pos = _positions[symbol];
                    decimal pnl = (pos.EntryPrice - execPrice) * pos.Quantity;
                    _balance += pnl;
                    _positions.Remove(symbol);
                }
                else // Long Open
                {
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
                    _balance += pnl;
                    _positions.Remove(symbol);
                }
                else // Short Open
                {
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
            return PlaceOrderAsync(symbol, side, quantity, stopPrice, ct);
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
    }
}