using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    public class BinanceOrderService
    {
        private readonly IBinanceRestClient _client;

        public BinanceOrderService(IBinanceRestClient client)
        {
            _client = client;
        }

        public async Task<bool> PlaceOrderAsync(string symbol, OrderSide side, FuturesOrderType type, decimal quantity, decimal? price = null, TimeInForce? timeInForce = null, CancellationToken ct = default)
        {
            // 1. 거래소 정보 조회 (Precision 보정용)
            var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
            if (exchangeInfo.Success)
            {
                var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    // 수량 보정 (StepSize)
                    if (symbolData.LotSizeFilter != null)
                    {
                        decimal stepSize = symbolData.LotSizeFilter.StepSize;
                        if (stepSize > 0)
                        {
                            quantity = Math.Floor(quantity / stepSize) * stepSize;
                        }
                    }

                    // 가격 보정 (TickSize)
                    if (price.HasValue && symbolData.PriceFilter != null)
                    {
                        decimal tickSize = symbolData.PriceFilter.TickSize;
                        if (tickSize > 0)
                        {
                            price = Math.Floor(price.Value / tickSize) * tickSize;
                        }
                    }
                }
            }

            if (quantity <= 0) return false;

            var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                side,
                type,
                quantity,
                price,
                timeInForce: timeInForce,
                ct: ct);

            return result.Success;
        }
    }
}