using System;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.1.0] 진입 직후 거래소 API 로 SL / TP(부분익절) / 트레일링 스탑 주문 등록
    ///
    /// 기존 문제:
    /// - 내부 모니터링(PositionMonitor)이 주기적으로 가격 체크 → 빠른 급등락에 대응 못함
    /// - 트레일링 스탑 거래소 등록 실패 → GIGGLE 케이스 (259% → 45% 청산)
    /// - 1분봉 조기청산 로직이 너무 타이트해서 정상 조정에도 손절
    ///
    /// 해결:
    /// - 진입 성공 직후 거래소에 SL/TP 즉시 등록 → 봇 다운타임에도 거래소가 보호
    /// - 트레일링 스탑은 1차 익절(TP) 도달 시 등록 (수익 구간에서만)
    /// - 긴급 대응(CRASH_REVERSE 등)은 제외 — 기존 내부 모니터링 유지
    /// </summary>
    public class EntryOrderRegistrar
    {
        private readonly IExchangeService _exchange;
        public event Action<string>? OnLog;

        // [v5.4.4] 중복 등록 방지 — 심볼별 최근 등록 시각
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastRegistered = new(StringComparer.OrdinalIgnoreCase);
        private const int REGISTER_COOLDOWN_SECONDS = 30;

        public EntryOrderRegistrar(IExchangeService exchange)
        {
            _exchange = exchange;
        }

        /// <summary>[v5.5.1] 쿨다운 초기화 — 재시작 시 재등록 허용</summary>
        public void ResetCooldown(string symbol)
        {
            _lastRegistered.TryRemove(symbol, out _);
        }

        /// <summary>
        /// 진입 직후 SL + TP 주문을 거래소에 등록
        /// </summary>
        /// <param name="symbol">심볼</param>
        /// <param name="isLong">LONG/SHORT</param>
        /// <param name="entryPrice">진입가</param>
        /// <param name="quantity">수량</param>
        /// <param name="leverage">레버리지</param>
        /// <param name="stopLossRoePct">손절 ROE % (예: -40 = ROE -40%)</param>
        /// <param name="takeProfitRoePct">1차 익절 ROE % (예: 25)</param>
        /// <param name="tpPartialRatio">1차 익절 시 부분청산 비율 (0.6 = 60%)</param>
        /// <param name="token">취소 토큰</param>
        /// <returns>(slOrderId, tpOrderId) — 실패 시 빈 문자열</returns>
        public async Task<(string SlOrderId, string TpOrderId)> RegisterEntryOrdersAsync(
            string symbol,
            bool isLong,
            decimal entryPrice,
            decimal quantity,
            int leverage,
            decimal stopLossRoePct = -40m,
            decimal takeProfitRoePct = 25m,
            decimal tpPartialRatio = 0.6m,
            decimal trailingCallbackRate = 2.0m,
            CancellationToken token = default)
        {
            string slId = "";
            string tpId = "";

            if (entryPrice <= 0 || quantity <= 0) return (slId, tpId);

            // [v5.4.4] 30초 쿨다운 — 동일 심볼 중복 등록 방지
            if (_lastRegistered.TryGetValue(symbol, out var lastTime) && (DateTime.Now - lastTime).TotalSeconds < REGISTER_COOLDOWN_SECONDS)
            {
                OnLog?.Invoke($"ℹ️ [SL/TP] {symbol} 최근 {REGISTER_COOLDOWN_SECONDS}초 내 등록됨 → 스킵");
                return (slId, tpId);
            }
            _lastRegistered[symbol] = DateTime.Now;

            // [v5.4.7] 조건부 주문만 취소 (일반 LIMIT 주문은 유지)
            // CancelAllOrdersAsync가 일반 + 조건부 모두 취소해서 LIMIT 취소 무한 반복 유발
            // → 조건부만 취소하도록 분리 불가 (SDK 제한) → 취소 자체 제거
            // 진입 시 1회만 호출되므로 기존 주문 없음

            string closeSide = isLong ? "SELL" : "BUY";

            // ─── SL (손절) ────────────────────────────────────
            try
            {
                // ROE → 실가격 변환: ROE% = (priceChange / entry) * leverage * 100
                // priceChange = ROE% * entry / (leverage * 100)
                decimal slPriceChange = stopLossRoePct * entryPrice / (leverage * 100m);
                decimal slPrice = isLong
                    ? entryPrice + slPriceChange  // LONG: entry - |change| (slPriceChange 음수)
                    : entryPrice - slPriceChange; // SHORT: entry + |change|

                if (slPrice <= 0) slPrice = entryPrice * 0.95m;  // 안전장치

                var (slOk, slOrderId) = await _exchange.PlaceStopOrderAsync(
                    symbol, closeSide, quantity, slPrice, token);

                if (slOk)
                {
                    slId = slOrderId;
                    OnLog?.Invoke($"✅ [SL] {symbol} 거래소 등록 | {closeSide} qty={quantity} stop=${slPrice:F6} (ROE{stopLossRoePct}%)");
                }
                else
                {
                    OnLog?.Invoke($"⚠️ [SL] {symbol} 거래소 등록 실패 → 내부 모니터링 대체");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [SL] {symbol} 예외: {ex.Message}");
            }

            // ─── TP (1차 부분 익절 — TAKE_PROFIT_MARKET) ────────────────────────────────
            // [v5.1.2] LIMIT → TAKE_PROFIT_MARKET 변경
            // 바이낸스 TP/SL Advanced 모드와 연동 + OCO 자동 취소
            try
            {
                decimal tpPriceChange = takeProfitRoePct * entryPrice / (leverage * 100m);
                decimal tpPrice = isLong
                    ? entryPrice + tpPriceChange
                    : entryPrice - tpPriceChange;

                decimal tpQty = Math.Floor(quantity * tpPartialRatio * 100m) / 100m;
                if (tpQty <= 0) tpQty = quantity;

                var (tpOk, tpOrderId) = await _exchange.PlaceTakeProfitOrderAsync(
                    symbol, closeSide, tpQty, tpPrice, token);

                if (tpOk)
                {
                    tpId = tpOrderId;
                    OnLog?.Invoke($"✅ [TP] {symbol} TAKE_PROFIT_MARKET 등록 | {closeSide} qty={tpQty} stop=${tpPrice:F6} (ROE+{takeProfitRoePct}% 부분{tpPartialRatio:P0})");
                }
                else
                {
                    OnLog?.Invoke($"⚠️ [TP] {symbol} TAKE_PROFIT_MARKET 실패 → 내부 PartialClose 대체");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [TP] {symbol} 예외: {ex.Message}");
            }

            // ─── TRAILING STOP (진입 시 즉시 등록 — 전체 수량) ────────────────
            // [v5.1.2] 진입 직후 트레일링도 바로 등록
            // activationPrice = TP 가격 (TP 도달 시 트레일링 활성화)
            try
            {
                decimal tpPriceForActivation = isLong
                    ? entryPrice * (1m + takeProfitRoePct / (leverage * 100m))
                    : entryPrice * (1m - takeProfitRoePct / (leverage * 100m));

                // 전체 수량의 잔여(TP 부분청산 후) 에 대해 트레일링
                decimal trailingQty = quantity - Math.Floor(quantity * tpPartialRatio * 100m) / 100m;
                if (trailingQty > 0)
                {
                    var (trailOk, trailId) = await _exchange.PlaceTrailingStopOrderAsync(
                        symbol, closeSide, trailingQty, trailingCallbackRate, tpPriceForActivation, token);

                    if (trailOk)
                    {
                        OnLog?.Invoke($"✅ [TRAILING] {symbol} 등록 | {closeSide} qty={trailingQty} callback={trailingCallbackRate}% activation=${tpPriceForActivation:F6}");
                    }
                    else
                    {
                        OnLog?.Invoke($"⚠️ [TRAILING] {symbol} 등록 실패 → 내부 트레일링 대체");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [TRAILING] {symbol} 예외: {ex.Message}");
            }

            return (slId, tpId);
        }

        /// <summary>
        /// 1차 TP 체결 후 잔여 수량에 대해 트레일링 스탑 등록
        /// </summary>
        public async Task<string> RegisterTrailingStopAsync(
            string symbol,
            bool isLong,
            decimal remainingQuantity,
            decimal callbackRatePct = 2.0m,
            decimal? activationPrice = null,
            CancellationToken token = default)
        {
            if (remainingQuantity <= 0) return "";

            try
            {
                string closeSide = isLong ? "SELL" : "BUY";
                var (ok, orderId) = await _exchange.PlaceTrailingStopOrderAsync(
                    symbol, closeSide, remainingQuantity,
                    callbackRatePct, activationPrice, token);

                if (ok)
                {
                    OnLog?.Invoke($"✅ [TRAILING] {symbol} 거래소 등록 | {closeSide} qty={remainingQuantity} callback={callbackRatePct}%");
                    return orderId;
                }
                else
                {
                    OnLog?.Invoke($"⚠️ [TRAILING] {symbol} 거래소 등록 실패 → 내부 트레일링 대체");
                    return "";
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [TRAILING] {symbol} 예외: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 기존 SL/TP 주문 취소 (포지션 청산 시 / 주문 갱신 시)
        /// </summary>
        public async Task CancelExistingOrdersAsync(string symbol, string? slOrderId, string? tpOrderId, CancellationToken token = default)
        {
            if (!string.IsNullOrEmpty(slOrderId))
            {
                try { await _exchange.CancelOrderAsync(symbol, slOrderId!, token); }
                catch (Exception ex) { OnLog?.Invoke($"⚠️ [CANCEL] {symbol} SL 취소 실패: {ex.Message}"); }
            }
            if (!string.IsNullOrEmpty(tpOrderId))
            {
                try { await _exchange.CancelOrderAsync(symbol, tpOrderId!, token); }
                catch (Exception ex) { OnLog?.Invoke($"⚠️ [CANCEL] {symbol} TP 취소 실패: {ex.Message}"); }
            }
        }
    }
}
