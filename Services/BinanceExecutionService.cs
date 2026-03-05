using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;

namespace TradingBot.Services
{
    /// <summary>
    /// 바이낸스 20배 레버리지 단타 전용 실행 서비스
    /// ────────────────────────────────────────────
    /// - 진입과 동시에 초기 스탑로스 설정
    /// - ATR 기반 동적 트레일링 스탑 실시간 갱신 (Cancel & Replace)
    /// - API Rate Limit 방지 (최소 0.1% 변동 시에만 갱신)
    /// - 틱 사이즈 자동 보정
    /// </summary>
    public class BinanceExecutionService
    {
        private readonly IBinanceRestClient _client;
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;

        // 심볼별 활성 스탑로스 주문 ID 추적
        private readonly ConcurrentDictionary<string, StopOrderState> _stopOrders = new();

        // 심볼별 틱 사이즈 캐싱 (API 호출 최소화)
        private readonly ConcurrentDictionary<string, SymbolPrecision> _symbolPrecisions = new();

        // 갱신 최소 간격 (기존 스탑 대비 최소 0.1% 변동 시에만)
        private const decimal MinUpdatePercentage = 0.001m; // 0.1%

        public BinanceExecutionService(IBinanceRestClient client)
        {
            _client = client;
        }

        /// <summary>
        /// 1. 레버리지 설정 + 시장가 진입 + 초기 스탑로스 설정 (One-Shot)
        /// </summary>
        public async Task<bool> ExecuteEntryWithStopAsync(
            string symbol,
            PositionSide positionSide,
            decimal quantity,
            decimal initialStopPrice,
            CancellationToken ct = default)
        {
            try
            {
                // 심볼 정밀도 정보 가져오기 (틱 사이즈/수량 사이즈)
                var precision = await GetSymbolPrecisionAsync(symbol, ct);
                if (precision == null)
                {
                    OnAlert?.Invoke($"❌ [{symbol}] 심볼 정보 조회 실패");
                    return false;
                }

                // 수량 및 가격 보정
                quantity = RoundQuantity(quantity, precision.QuantityPrecision);
                initialStopPrice = RoundPrice(initialStopPrice, precision.PricePrecision);

                // [1] 레버리지 20배로 설정
                var leverageResult = await _client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, 20, ct: ct);
                if (!leverageResult.Success)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] 레버리지 설정 경고: {leverageResult.Error?.Message} (이미 설정되어 있을 수 있음)");
                }

                // [2] 시장가 진입
                OrderSide entrySide = positionSide == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;
                var entryResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: entrySide,
                    type: FuturesOrderType.Market,
                    quantity: quantity,
                    positionSide: positionSide,
                    ct: ct
                );

                if (!entryResult.Success)
                {
                    OnAlert?.Invoke($"❌ [{symbol}] 진입 주문 실패: {entryResult.Error?.Message}");
                    return false;
                }

                decimal entryPrice = entryResult.Data.AveragePrice > 0
                    ? entryResult.Data.AveragePrice
                    : entryResult.Data.Price;
                OnAlert?.Invoke($"✅ [{symbol}] {entrySide} 진입 성공 | 체결가: {entryPrice:F8} | 수량: {quantity:F8}");

                // [3] 초기 스탑로스 설정 (Stop-Market + ClosePosition)
                OrderSide stopSide = positionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
                var stopResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: stopSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: null, // ClosePosition = true일 때는 수량 생략
                    stopPrice: initialStopPrice,
                    positionSide: positionSide,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    closePosition: true, // ⭐ 핵심: 포지션 전량 종료
                    ct: ct
                );

                if (stopResult.Success)
                {
                    _stopOrders[symbol] = new StopOrderState
                    {
                        Symbol = symbol,
                        OrderId = stopResult.Data.Id,
                        StopPrice = initialStopPrice,
                        PositionSide = positionSide,
                        LastUpdateTime = DateTime.Now,
                    };
                    OnAlert?.Invoke($"🛡️ [{symbol}] 초기 스탑로스 설정 완료 | 스탑: {initialStopPrice:F8}");
                    return true;
                }
                else
                {
                    OnAlert?.Invoke($"🚨 [{symbol}] 스탑로스 설정 실패! 수동 대응 필요: {stopResult.Error?.Message}");
                    // 극단적 안전장치: 스탑 실패 시 포지션 즉시 시장가 종료
                    await EmergencyClosePositionAsync(symbol, positionSide, ct);
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnAlert?.Invoke($"💥 [{symbol}] 주문 실행 중 예외: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 2. 동적 트레일링 스탑 가격 업데이트 (Cancel & Replace)
        /// </summary>
        public async Task UpdateTrailingStopAsync(
            string symbol,
            decimal newStopPrice,
            bool forceUpdate = false,
            CancellationToken ct = default)
        {
            if (!_stopOrders.TryGetValue(symbol, out var state))
            {
                OnLog?.Invoke($"⚠️ [{symbol}] 트레일링 스탑 대상이 아님 (등록되지 않은 심볼)");
                return;
            }

            try
            {
                // 틱 사이즈 보정
                var precision = await GetSymbolPrecisionAsync(symbol, ct);
                if (precision != null)
                {
                    newStopPrice = RoundPrice(newStopPrice, precision.PricePrecision);
                }

                // [API Rate Limit 방지] 최소 0.1% 이상 변동 시에만 갱신
                decimal changePercent = Math.Abs((newStopPrice - state.StopPrice) / state.StopPrice);
                if (!forceUpdate && changePercent < MinUpdatePercentage)
                {
                    // 변동폭이 너무 작으면 갱신 안 함 (API 절약)
                    return;
                }

                // [방향 검증] 트레일링 스탑은 한 방향으로만 이동해야 함
                bool isImprovement = state.PositionSide == PositionSide.Long
                    ? newStopPrice > state.StopPrice  // 롱: 스탑이 위로만 올라가야 함
                    : newStopPrice < state.StopPrice; // 숏: 스탑이 아래로만 내려가야 함

                if (!isImprovement && !forceUpdate)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] 트레일링 스탑 역행 시도 무시 (현재: {state.StopPrice:F8}, 요청: {newStopPrice:F8})");
                    return;
                }

                // [1] 기존 스탑로스 주문 취소
                var cancelResult = await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, state.OrderId, ct: ct);
                if (!cancelResult.Success)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] 기존 스탑로스 취소 실패 (이미 체결됨): {cancelResult.Error?.Message}");
                    _stopOrders.TryRemove(symbol, out _); // 체결된 것으로 간주하고 제거
                    return;
                }

                // [2] 새로운 가격으로 스탑로스 재등록
                OrderSide stopSide = state.PositionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
                var replaceResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: stopSide,
                    type: FuturesOrderType.StopMarket,
                    quantity: null,
                    stopPrice: newStopPrice,
                    positionSide: state.PositionSide,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    closePosition: true,
                    ct: ct
                );

                if (replaceResult.Success)
                {
                    state.OrderId = replaceResult.Data.Id;
                    decimal oldStop = state.StopPrice;
                    state.StopPrice = newStopPrice;
                    state.LastUpdateTime = DateTime.Now;

                    string direction = state.PositionSide == PositionSide.Long ? "상향" : "하향";
                    OnLog?.Invoke($"📈 [{symbol}] 트레일링 스탑 {direction} | {oldStop:F8} → {newStopPrice:F8} ({changePercent * 100:F2}% 변동)");
                }
                else
                {
                    OnAlert?.Invoke($"❌ [{symbol}] 트레일링 스탑 재등록 실패: {replaceResult.Error?.Message}");
                }
            }
            catch (Exception ex)
            {
                OnAlert?.Invoke($"💥 [{symbol}] 스탑로스 업데이트 중 예외: {ex.Message}");
            }
        }

        /// <summary>
        /// 3. 스탑로스 상태 제거 (포지션 종료 시 호출)
        /// </summary>
        public void RemoveStopOrder(string symbol)
        {
            _stopOrders.TryRemove(symbol, out _);
            OnLog?.Invoke($"🗑️ [{symbol}] 스탑로스 상태 제거");
        }

        /// <summary>
        /// 스탑로스 존재 여부 확인
        /// </summary>
        public bool HasStopOrder(string symbol) => _stopOrders.ContainsKey(symbol);

        /// <summary>
        /// 현재 스탑 가격 조회
        /// </summary>
        public decimal? GetCurrentStopPrice(string symbol)
        {
            return _stopOrders.TryGetValue(symbol, out var state) ? state.StopPrice : null;
        }

        /// <summary>
        /// 4. 외부에서 설정한 스탑 주문을 ExecutionService에 등록 (Cancel & Replace 트레일링 연동)
        /// PositionMonitorService에서 설정한 서버사이드 스탑 주문의 ID를 등록하여
        /// HybridExitManager 등의 트레일링 스탑 갱신이 정상 동작하도록 합니다.
        /// </summary>
        public void RegisterExternalStopOrder(string symbol, long orderId, decimal stopPrice, bool isLong)
        {
            _stopOrders[symbol] = new StopOrderState
            {
                Symbol = symbol,
                OrderId = orderId,
                StopPrice = stopPrice,
                PositionSide = isLong ? PositionSide.Long : PositionSide.Short,
                LastUpdateTime = DateTime.Now,
            };
            OnLog?.Invoke($"🔗 [{symbol}] 외부 스탑 주문 등록 (OrderId: {orderId}, StopPrice: {stopPrice:F8})");
        }

        // ════════════════ 헬퍼 메서드 ════════════════

        /// <summary>
        /// 심볼 정밀도 정보 조회 및 캐싱
        /// </summary>
        private async Task<SymbolPrecision?> GetSymbolPrecisionAsync(string symbol, CancellationToken ct = default)
        {
            if (_symbolPrecisions.TryGetValue(symbol, out var cached))
                return cached;

            try
            {
                var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
                if (!exchangeInfo.Success) return null;

                var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData == null) return null;

                var precision = new SymbolPrecision
                {
                    Symbol = symbol,
                    PricePrecision = symbolData.PriceFilter?.TickSize ?? 0.01m,
                    QuantityPrecision = symbolData.LotSizeFilter?.StepSize ?? 0.001m,
                };

                _symbolPrecisions[symbol] = precision;
                return precision;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 가격 틱 사이즈 보정
        /// </summary>
        private decimal RoundPrice(decimal price, decimal tickSize)
        {
            if (tickSize <= 0) return Math.Round(price, 8);
            return Math.Floor(price / tickSize) * tickSize;
        }

        /// <summary>
        /// 수량 스텝 사이즈 보정
        /// </summary>
        private decimal RoundQuantity(decimal quantity, decimal stepSize)
        {
            if (stepSize <= 0) return Math.Round(quantity, 8);
            return Math.Floor(quantity / stepSize) * stepSize;
        }

        /// <summary>
        /// 긴급 포지션 종료 (스탑로스 설정 실패 시)
        /// </summary>
        private async Task EmergencyClosePositionAsync(string symbol, PositionSide positionSide, CancellationToken ct = default)
        {
            try
            {
                OrderSide closeSide = positionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
                await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol: symbol,
                    side: closeSide,
                    type: FuturesOrderType.Market,
                    quantity: null,
                    positionSide: positionSide,
                    closePosition: true,
                    ct: ct
                );
                OnAlert?.Invoke($"⚠️ [{symbol}] 긴급 포지션 종료 실행 (스탑로스 미설정으로 인한 안전장치 발동)");
            }
            catch (Exception ex)
            {
                OnAlert?.Invoke($"🚨 [{symbol}] 긴급 종료 실패: {ex.Message} - 수동 대응 필요!");
            }
        }
    }

    // ════════════════ 데이터 클래스 ════════════════

    public class StopOrderState
    {
        public string Symbol { get; set; } = string.Empty;
        public long OrderId { get; set; }
        public decimal StopPrice { get; set; }
        public PositionSide PositionSide { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    public class SymbolPrecision
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal PricePrecision { get; set; }    // 틱 사이즈
        public decimal QuantityPrecision { get; set; } // 수량 스텝 사이즈
    }
}
