using Binance.Net.Interfaces.Clients;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Services;
using TradingBot.Models;
using Binance.Net.Enums;

namespace TradingBot.Strategies
{
    public class GridStrategy
    {
        private readonly IBinanceRestClient _client;
        private readonly BinanceOrderService _orderService;
        private decimal _gridStepPercent = 0.01m; // 1% 간격
        private int _gridLevels = 5;

        public GridStrategy(IBinanceRestClient client, BinanceOrderService orderService)
        {
            _client = client;
            _orderService = orderService;
        }

        public async Task ExecuteAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            // 1. 기존 주문 확인 (너무 많은 주문 방지)
            var openOrders = await _client.UsdFuturesApi.Trading.GetOpenOrdersAsync(symbol, ct: token);
            if (openOrders.Success && openOrders.Data.Count() > _gridLevels * 2) return;

            // 주문 수량 계산 (최소 수량 보정 필요, 여기선 단순화)
            decimal quantity = 0.001m; 

            // 2. 그리드 주문 배치 (현재가 위아래로 지정가 주문)
            // 상단 매도 그리드
            for (int i = 1; i <= _gridLevels; i++)
            {
                decimal price = currentPrice * (1 + (_gridStepPercent * i));
                
                // 이미 해당 가격대 근처에 주문이 있으면 스킵
                if (openOrders.Success && openOrders.Data.Any(o => o.Side == OrderSide.Sell && Math.Abs(o.Price - price) / price < 0.001m))
                    continue;

                await _orderService.PlaceOrderAsync(symbol, OrderSide.Sell, FuturesOrderType.Limit, quantity, price, ct: token);
            }

            // 하단 매수 그리드
            for (int i = 1; i <= _gridLevels; i++)
            {
                decimal price = currentPrice * (1 - (_gridStepPercent * i));
                
                // 이미 해당 가격대 근처에 주문이 있으면 스킵
                if (openOrders.Success && openOrders.Data.Any(o => o.Side == OrderSide.Buy && Math.Abs(o.Price - price) / price < 0.001m))
                    continue;

                await _orderService.PlaceOrderAsync(symbol, OrderSide.Buy, FuturesOrderType.Limit, quantity, price, ct: token);
            }
        }
    }
}