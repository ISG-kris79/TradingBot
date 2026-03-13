using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Concurrent;
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

        // [캐시] 심볼별 스텝사이즈/틱사이즈 - GetExchangeInfo 반복 호출 방지 (TTL 1시간)
        private static readonly ConcurrentDictionary<string, (decimal stepSize, decimal tickSize, DateTime cachedAt)> _symbolInfoCache = new();
        private static readonly TimeSpan _symbolInfoCacheTtl = TimeSpan.FromHours(1);

        public string ExchangeName => "Binance";

        // [추가] 로그 이벤트 (상위 레이어로 전달)
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;

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
                OnLog?.Invoke($"[시뮬레이션] {symbol} {side} 주문 (수량: {quantity}, 가격: {price?.ToString() ?? "시장가"}, ReduceOnly: {reduceOnly})");
                return true; // 시뮬레이션 모드에서는 항상 성공
            }

            // [수정] 정밀도 보정: reduceOnly(청산) 시 거래소 반환 수량이 이미 유효하므로 스킵
            // 진입 주문 또는 지정가 청산 시에만 캐시 기반 보정 수행
            if (!reduceOnly || price.HasValue)
            {
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0)
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (price.HasValue && tickSize > 0)
                    price = Math.Floor(price.Value / tickSize) * tickSize;
            }

            if (quantity <= 0)
            {
                OnLog?.Invoke($"❌ [Binance] 주문 수량이 0 이하입니다: {quantity}");
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
                    timeInForce: price.HasValue ? TimeInForce.GoodTillCanceled : null,
                    reduceOnly: reduceOnly,
                    ct: ct);

                if (!result.Success)
                {
                    string errorDetail = $"Code={result.Error?.Code}, Msg={result.Error?.Message}";
                    OnLog?.Invoke($"❌ [Binance API] 주문 실패 - {symbol} {side} {quantity}");
                    OnLog?.Invoke($"   📋 오류 상세: {errorDetail}");

                    int? errCode = result.Error?.Code;
                    if (errCode == -2019)
                        OnAlert?.Invoke($"⚠️ [{symbol}] 잔고 부족 오류 - 사용 가능한 마진 확인 필요");
                    else if (errCode == -1021)
                        OnAlert?.Invoke($"⚠️ [{symbol}] 타임스탬프 오류 - 시스템 시간 동기화 확인 필요");
                    else if (errCode == -2022)
                        OnLog?.Invoke($"⚠️ [{symbol}] ReduceOnly 주문 거부 (-2022) - 서버사이드 Stop이 이미 체결됐을 가능성 있음");
                    else if (errCode == -4061)
                        OnLog?.Invoke($"⚠️ [{symbol}] positionSide 불일치 (-4061) - Hedge Mode 설정 확인 필요");
                    else if (errCode == -1003)
                        OnLog?.Invoke($"⚠️ [{symbol}] API Rate Limit 초과 (-1003)");

                    return false;
                }

                OnLog?.Invoke($"✅ [Binance] 주문 성공 - {symbol} {side} {quantity} (OrderId: {result.Data?.Id})");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [Binance 예외] 주문 중 예외 발생 - {symbol} {side} {quantity}");
                OnLog?.Invoke($"   🔥 예외: {ex.Message}");
                if (ex.InnerException != null)
                    OnLog?.Invoke($"   🔍 내부 예외: {ex.InnerException.Message}");
                return false;
            }
        }

        /// <summary>
        /// 심볼 정밀도 정보 조회 (캐시 우선, TTL 1시간)
        /// </summary>
        private async Task<(decimal stepSize, decimal tickSize)> GetSymbolPrecisionAsync(string symbol, CancellationToken ct)
        {
            if (_symbolInfoCache.TryGetValue(symbol, out var cached) && (DateTime.UtcNow - cached.cachedAt) < _symbolInfoCacheTtl)
                return (cached.stepSize, cached.tickSize);

            try
            {
                var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
                if (!exchangeInfo.Success)
                {
                    OnLog?.Invoke($"⚠️ [Binance] ExchangeInfo 조회 실패: {exchangeInfo.Error?.Message}");
                    return (0, 0);
                }

                var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData == null) return (0, 0);

                decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0;
                decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0;

                _symbolInfoCache[symbol] = (stepSize, tickSize, DateTime.UtcNow);
                return (stepSize, tickSize);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [Binance] 심볼 정밀도 조회 예외: {ex.Message}");
                return (0, 0);
            }
        }

        public async Task<(bool Success, string OrderId)> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
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
            return result.Success && result.Data != null
                ? (true, result.Data.Id.ToString())
                : (false, string.Empty);
        }

        public async Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            if (!long.TryParse(orderId, out long id))
            {
                Console.WriteLine($"❌ [Binance] 주문 취소 실패 - 잘못된 OrderId 형식: {orderId}");
                return false;
            }

            var result = await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, id, ct: ct);

            if (result.Success)
            {
                Console.WriteLine($"✅ [Binance] 주문 취소 성공 - {symbol} OrderId={orderId}");
            }
            else
            {
                Console.WriteLine($"❌ [Binance] 주문 취소 실패 - {symbol} OrderId={orderId}");
                Console.WriteLine($"   에러: {result.Error?.Message}");
            }

            return result.Success;
        }

        public async Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: ct);

            if (result.Success)
            {
                Console.WriteLine($"✅ [Binance] 레버리지 설정 성공 - {symbol} Leverage={leverage}x");
            }
            else
            {
                Console.WriteLine($"❌ [Binance] 레버리지 설정 실패 - {symbol} Leverage={leverage}x");
                Console.WriteLine($"   에러: {result.Error?.Message}");
            }

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
                    UnrealizedPnL = p.UnrealizedPnl,
                    Leverage = p.Leverage
                })
                .ToList();
        }

        public async Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit, ct: ct);
            if (!result.Success) return new List<IBinanceKline>();
            return result.Data.Cast<IBinanceKline>().ToList();
        }

        /// <summary>
        /// [v2.4.2] 날짜 범위 기반 캔들 조회 (HistoricalDataLabeler용 6개월 데이터 수집)
        /// 현재는 최근 데이터만 반환하며, 향후 pagination 추가
        /// </summary>
        public async Task<List<IBinanceKline>> GetKlinesAsync(
            string symbol,
            KlineInterval interval,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 1000,
            CancellationToken ct = default)
        {
            try
            {
                // 간단한 구현: Binance API에서 최근 limit개 캔들 조회
                // TODO: startTime/endTime 파라미터로 진정한 범위 조회 구현 (pagination 필요)
                var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    startTime: startTime,
                    endTime: endTime,
                    limit: limit,
                    ct: ct);

                if (!result.Success)
                    return new List<IBinanceKline>();

                var allKlines = result.Data.Cast<IBinanceKline>().ToList();
                
                // 중복 제거 및 정렬
                return allKlines
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetOrderBookAsync failed: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal> GetFundingRateAsync(string symbol, CancellationToken token = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(symbol, limit: 1, ct: token);
                if (!result.Success || result.Data == null || !result.Data.Any()) 
                    return 0;

                var fundingData = result.Data.FirstOrDefault();
                if (fundingData == null)
                    return 0;

                return fundingData.FundingRate;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] GetFundingRate 예외 - {symbol}: {ex.Message}");
                return 0;
            }
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

                        decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.0000001m;
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
                    Console.WriteLine($"✅ [Binance] 지정가 주문 성공 - {symbol} {side} {quantity}@{price} (OrderId: {result.Data.Id})");
                    return (true, result.Data.Id.ToString());
                }

                Console.WriteLine($"❌ [Binance] 지정가 주문 실패 - {symbol} {side} {quantity}@{price}");
                Console.WriteLine($"   에러 코드: {result.Error?.Code} | 메시지: {result.Error?.Message}");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] PlaceLimitOrder 예외 - {symbol} {side} {quantity}@{price}");
                Console.WriteLine($"   예외: {ex.Message}");
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
                    Console.WriteLine($"❌ [Binance] 주문 상태 조회 실패 - {symbol} OrderId={orderId}");
                    Console.WriteLine($"   에러: {result.Error?.Message}");
                    return (false, 0, 0);
                }

                var order = result.Data;
                bool isFilled = order.Status == OrderStatus.Filled;
                bool isPartiallyFilled = order.Status == OrderStatus.PartiallyFilled;

                decimal filledQty = order.QuantityFilled;
                decimal avgPrice = order.AveragePrice > 0 ? order.AveragePrice : order.Price;

                Console.WriteLine($"📊 [Binance] 주문 상태: {symbol} OrderId={orderId} | Status={order.Status} | Filled={filledQty}/{order.Quantity} | AvgPrice={avgPrice}");

                return (isFilled || isPartiallyFilled, filledQty, avgPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] GetOrderStatus 예외 - {symbol} OrderId={orderId}");
                Console.WriteLine($"   예외: {ex.Message}");
                return (false, 0, 0);
            }
        }

        // [시장가 주문] 즉시 체결 + 체결 정보 반환 (지정가 3초 대기 제거)
        public async Task<(bool Success, decimal FilledQuantity, decimal AveragePrice)> PlaceMarketOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            CancellationToken ct = default)
        {
            try
            {
                // 1. 수량 정밀도 보정
                var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
                if (exchangeInfo.Success)
                {
                    var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolData != null)
                    {
                        decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                        quantity = Math.Floor(quantity / stepSize) * stepSize;
                    }
                }

                if (quantity <= 0)
                {
                    Console.WriteLine($"❌ [Binance] 시장가 주문 실패 - 수량 0: {symbol}");
                    return (false, 0, 0);
                }

                OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;

                // 2. 시장가 주문 실행
                Console.WriteLine($"📤 [Binance] 시장가 주문 전송 - {symbol} {side} {quantity}");
                var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    FuturesOrderType.Market,
                    quantity,
                    ct: ct);

                if (!result.Success || result.Data == null)
                {
                    Console.WriteLine($"❌ [Binance] 시장가 주문 실패 - {symbol} {side} {quantity}");
                    Console.WriteLine($"   에러: {result.Error?.Message}");
                    return (false, 0, 0);
                }

                var orderId = result.Data.Id.ToString();
                Console.WriteLine($"✅ [Binance] 시장가 주문 접수 - OrderId={orderId}");

                // 3. 짧은 대기 (시장가는 500ms 내 체결)
                await Task.Delay(500, ct);

                // 4. 체결 확인
                var statusResult = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, result.Data.Id, ct: ct);

                if (!statusResult.Success || statusResult.Data == null)
                {
                    Console.WriteLine($"⚠️ [Binance] 체결 확인 실패 (주문은 성공) - OrderId={orderId}");
                    // 주문은 성공했으므로 포지션 조회로 복구 가능
                    return (true, quantity, 0);
                }

                var order = statusResult.Data;
                bool isFilled = order.Status == OrderStatus.Filled || order.Status == OrderStatus.PartiallyFilled;
                decimal filledQty = order.QuantityFilled;
                decimal avgPrice = order.AveragePrice > 0 ? order.AveragePrice : order.Price;

                Console.WriteLine($"✅ [Binance] 시장가 체결 완료 - {symbol} | Filled={filledQty} | AvgPrice={avgPrice:F4}");

                return (isFilled, filledQty, avgPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] PlaceMarketOrder 예외 - {symbol} {side} {quantity}");
                Console.WriteLine($"   예외: {ex.Message}");
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
