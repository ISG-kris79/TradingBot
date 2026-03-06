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
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    public class BinanceExchangeService : IExchangeService, IDisposable
    {
        private readonly BinanceRestClient _client;
        private bool _disposed = false;

        public string ExchangeName => "Binance";

        public BinanceExchangeService(string apiKey, string apiSecret)
        {
            _client = new BinanceRestClient(options =>
            {
                // API Key가 설정되어 있을 때만 자격 증명 추가
                if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                }
                // API Key가 없으면 공개 API만 사용 가능
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

        public async Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default, bool reduceOnly = false)
        {
            // [추가] 시뮬레이션 모드 체크
            if (AppConfig.Current?.Trading?.IsSimulationMode == true)
            {
                Console.WriteLine($"[시뮬레이션] {symbol} {side} 주문 (수량: {quantity}, 가격: {price?.ToString() ?? "시장가"}, ReduceOnly: {reduceOnly})");
                return true; // 시뮬레이션 모드에서는 항상 성공
            }

            // [추가] 주문 전 정밀도 보정
            var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
            if (exchangeInfo.Success)
            {
                var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;

                    if (price.HasValue)
                    {
                        decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.01m;
                        price = Math.Floor(price.Value / tickSize) * tickSize;
                    }
                }
            }
            else
            {
                Console.WriteLine($"❌ [Binance] ExchangeInfo 조회 실패: {exchangeInfo.Error?.Message}");
            }

            if (quantity <= 0)
            {
                Console.WriteLine($"❌ [Binance] 주문 수량이 0 이하입니다: {quantity}");
                return false;
            }

            OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
            FuturesOrderType orderType = price.HasValue ? FuturesOrderType.Limit : FuturesOrderType.Market;

            try
            {
                var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    orderType,
                    quantity,
                    price,
                    reduceOnly: reduceOnly,
                    ct: ct);

                if (!result.Success)
                {
                    Console.WriteLine($"❌ [Binance] 주문 실패 - {symbol} {side} {quantity}");
                    Console.WriteLine($"   에러 코드: {result.Error?.Code}");
                    Console.WriteLine($"   에러 메시지: {result.Error?.Message}");
                    return false;
                }

                Console.WriteLine($"✅ [Binance] 주문 성공 - {symbol} {side} {quantity} (OrderId: {result.Data?.Id})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] 주문 예외 발생 - {symbol} {side} {quantity}");
                Console.WriteLine($"   예외: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
                return false;
            }
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
                    UnrealizedPnL = p.UnrealizedPnl
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

        public async Task<(decimal bestBid, decimal bestAsk)?> GetOrderBookAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, limit: 5, ct: ct);
                if (!result.Success || result.Data == null) return null;

                var bestBid = result.Data.Bids.FirstOrDefault()?.Price ?? 0;
                var bestAsk = result.Data.Asks.FirstOrDefault()?.Price ?? 0;

                return (bestBid, bestAsk);
            }
            catch { return null; }
        }

        public async Task<decimal> GetFundingRateAsync(string symbol, CancellationToken token = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(symbol, limit: 1, ct: token);
            if (!result.Success || result.Data == null || !result.Data.Any()) return 0;

            return result.Data.First().FundingRate;
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
                var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
                if (exchangeInfo.Success)
                {
                    var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolData != null)
                    {
                        decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                        quantity = Math.Floor(quantity / stepSize) * stepSize;

                        decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.01m;
                        price = Math.Floor(price / tickSize) * tickSize;
                    }
                }

                if (quantity <= 0) return (false, string.Empty);

                OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;

                var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    FuturesOrderType.Limit,
                    quantity,
                    price: price,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);

                if (result.Success && result.Data != null)
                {
                    return (true, result.Data.Id.ToString());
                }

                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] PlaceLimitOrder 실패: {ex.Message}");
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
                if (!long.TryParse(orderId, out long id))
                {
                    return (false, 0, 0);
                }

                var result = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, id, ct: ct);

                if (!result.Success || result.Data == null)
                {
                    return (false, 0, 0);
                }

                var order = result.Data;
                bool isFilled = order.Status == OrderStatus.Filled;
                bool isPartiallyFilled = order.Status == OrderStatus.PartiallyFilled;

                decimal filledQty = order.QuantityFilled;
                decimal avgPrice = order.AveragePrice > 0 ? order.AveragePrice : order.Price;

                return (isFilled || isPartiallyFilled, filledQty, avgPrice);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetOrderStatus 실패: {ex.Message}");
                return (false, 0, 0);
            }
        }

        /// <summary>
        /// Batch Order 실행 (Grid Strategy 최적화)
        /// Binance Futures는 최대 5개의 주문을 한 번에 처리할 수 있습니다.
        /// </summary>
        public async Task<BatchOrderResult> PlaceBatchOrdersAsync(List<BatchOrderRequest> orders, CancellationToken ct = default)
        {
            var result = new BatchOrderResult();
            if (orders == null || orders.Count == 0) return result;

            // Binance API는 한 번에 5개까지 처리 가능하므로 청크로 분할
            const int batchSize = 5;
            for (int i = 0; i < orders.Count; i += batchSize)
            {
                var batch = orders.Skip(i).Take(batchSize).ToList();

                try
                {
                    // PlaceMultipleOrdersAsync 사용
                    var batchOrders = new List<Binance.Net.Objects.Models.Futures.BinanceFuturesBatchOrder>();

                    foreach (var order in batch)
                    {
                        var orderSide = order.Side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
                        var orderType = order.OrderType?.ToUpper() == "MARKET"
                            ? FuturesOrderType.Market
                            : FuturesOrderType.Limit;

                        batchOrders.Add(new Binance.Net.Objects.Models.Futures.BinanceFuturesBatchOrder
                        {
                            Symbol = order.Symbol,
                            Side = orderSide,
                            Type = orderType,
                            Quantity = order.Quantity,
                            Price = orderType == FuturesOrderType.Limit ? order.Price : null
                        });
                    }

                    var batchResult = await _client.UsdFuturesApi.Trading.PlaceMultipleOrdersAsync(batchOrders, ct: ct);

                    if (batchResult.Success && batchResult.Data != null)
                    {
                        foreach (var orderResult in batchResult.Data)
                        {
                            if (orderResult.Success)
                            {
                                result.SuccessCount++;
                                result.OrderIds.Add(orderResult.Data?.Id.ToString() ?? "");
                            }
                            else
                            {
                                result.FailureCount++;
                                result.Errors.Add($"{orderResult.Error?.Code}: {orderResult.Error?.Message}");
                            }
                        }
                    }
                    else
                    {
                        result.FailureCount += batch.Count;
                        result.Errors.Add($"Batch 실패: {batchResult.Error?.Message}");
                    }
                }
                catch (Exception ex)
                {
                    result.FailureCount += batch.Count;
                    result.Errors.Add($"Batch 예외: {ex.Message}");
                }

                // API Rate Limit 방지를 위한 지연
                if (i + batchSize < orders.Count)
                {
                    await Task.Delay(200, ct);
                }
            }

            return result;
        }

        /// <summary>
        /// Multi-Assets Mode 조회 (Portfolio Margin)
        /// </summary>
        public async Task<bool> GetMultiAssetsModeAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.GetMultiAssetsModeAsync(ct: ct);
                if (result.Success && result.Data != null)
                {
                    // Binance.Net v12.x: MultiAssetMode 속성 사용
                    return result.Data.MultiAssetMode;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetMultiAssetsMode Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Multi-Assets Mode 설정 (Portfolio Margin 활성화/비활성화)
        /// </summary>
        public async Task<bool> SetMultiAssetsModeAsync(bool enabled, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.SetMultiAssetsModeAsync(enabled, ct: ct);
                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] Multi-Assets Mode {(enabled ? "활성화" : "비활성화")} 완료");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] SetMultiAssetsMode Error: {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] SetMultiAssetsMode Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Position Mode 조회 (Hedge Mode 여부)
        /// </summary>
        public async Task<bool> GetPositionModeAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct);
                return result.Success && result.Data.IsHedgeMode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetPositionMode Error: {ex.Message}");
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
                var result = await _client.UsdFuturesApi.Account.ModifyPositionModeAsync(hedgeMode, ct: ct);
                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] Position Mode: {(hedgeMode ? "Hedge" : "One-way")} 설정 완료");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] SetPositionMode Error: {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] SetPositionMode Exception: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
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
