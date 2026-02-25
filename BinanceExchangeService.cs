using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class BinanceExchangeService : IExchangeService
    {
        private readonly BinanceRestClient _client;

        public string ExchangeName => "Binance";

        public BinanceExchangeService(string apiKey, string apiSecret)
        {
            _client = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            });
        }

        public async Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (!result.Success) return 0;

            var balance = result.Data.FirstOrDefault(b => b.Asset == asset);
            return balance?.AvailableBalance ?? 0;
        }

        public async Task<decimal> GetPriceAsync(string symbol, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct);
            return result.Success ? result.Data.Price : 0;
        }

        public async Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default)
        {
            OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
            FuturesOrderType orderType = price.HasValue ? FuturesOrderType.Limit : FuturesOrderType.Market;

            var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                orderType,
                quantity,
                price,
                ct: ct);

            return result.Success;
        }

        public async Task<bool> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
        {
            OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
            var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                FuturesOrderType.StopMarket,
                quantity,
                stopPrice: stopPrice,
                reduceOnly: true,
                ct: ct);
            return result.Success;
        }

        public async Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            if (!long.TryParse(orderId, out long id)) return false;
            var result = await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, id, ct: ct);
            return result.Success;
        }

        public async Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: ct);
            return result.Success;
        }

        public async Task<List<PositionInfo>> GetPositionsAsync(CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!result.Success) return new List<PositionInfo>();

            return result.Data
                .Where(p => Math.Abs(p.Quantity) > 0)
                .Select(p => new PositionInfo
                {
                    Symbol = p.Symbol,
                    Side = p.Quantity > 0 ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell,
                    IsLong = p.Quantity > 0,
                    Quantity = Math.Abs(p.Quantity),
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnl = p.UnrealizedPnl
                })
                .ToList();
        }

        public async Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit, ct: ct);
            if (!result.Success) return new List<IBinanceKline>();
            return result.Data.Cast<IBinanceKline>().ToList();
        }

        public async Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
            if (!result.Success || result.Data == null) return null;

            var exchangeInfo = new ExchangeInfo();
            foreach (var s in result.Data.Symbols)
            {
                var symbolInfo = new SymbolInfo
                {
                    Name = s.Name,
                    LotSizeFilter = s.LotSizeFilter != null ? new SymbolFilter
                    {
                        StepSize = s.LotSizeFilter.StepSize,
                        TickSize = 0 // Not available in LotSizeFilter
                    } : null,
                    PriceFilter = s.PriceFilter != null ? new SymbolFilter
                    {
                        StepSize = 0, // Not available in PriceFilter
                        TickSize = s.PriceFilter.TickSize
                    } : null
                };
                exchangeInfo.Symbols.Add(symbolInfo);
            }
            return exchangeInfo;
        }
    }
}
