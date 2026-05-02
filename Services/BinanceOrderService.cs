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
            // [v5.22.62] CHOKEPOINT — SHORT 신규 진입 절대 차단 (이 wrapper 는 reduceOnly 파라미터 없음 = 모든 SELL 차단)
            //   reduceOnly 청산은 PositionMonitor / BinanceExecutionService 의 명시적 reduceOnly 호출 사용
            if (side == OrderSide.Sell)
            {
                Console.WriteLine($"⛔ [SHORT_BLOCK] {symbol} BinanceOrderService SELL 차단 — 봇 전체 LONG 전용");
                return false;
            }

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