using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.10.54] 주문 라이프사이클 단일 진입점
    /// 진입 시 SL+TP+Trailing 3개를 거래소에 한 번만 등록하고, SL/전체청산 이벤트 수신 시 잔여 조건부 주문을 자동 취소.
    ///
    /// 기존 문제:
    /// - 진입 흐름에서 여러 곳에서 개별적으로 PlaceStopOrder/PlaceTakeProfit/PlaceTrailingStop 호출 → 중복 주문 → Binance -4120
    /// - 포지션 종료 시 잔여 조건부 주문 누수
    ///
    /// 호출 규칙:
    /// - 신규 진입 직후 단 1곳(TradingEngine)에서 RegisterOnEntryAsync 호출
    /// - 재시작/동기화 시 동일 메서드 재호출 (내부에서 CancelAll 선행 → 안전)
    /// - WebSocket OrderUpdate에서 SL 체결 감지 → OnStopLossFilledAsync
    /// - 포지션 완전 종료 시 OnPositionClosedAsync
    /// - 본절 전환(ROE 임계 도달) 시 ReplaceSlAsync
    /// </summary>
    public class OrderLifecycleManager
    {
        public record BracketIds(string SlOrderId, string TpOrderId, string TrailingOrderId, bool Success);

        private readonly IExchangeService _exchange;
        public event Action<string>? OnLog;

        // 동일 심볼 중복 등록 방지 (30초 쿨다운)
        private readonly ConcurrentDictionary<string, DateTime> _lastRegistered = new(StringComparer.OrdinalIgnoreCase);
        private const int REGISTER_COOLDOWN_SECONDS = 30;

        // 거래소 주문 취소 후 재등록까지 대기 (서버 처리 반영)
        private const int CANCEL_SETTLE_DELAY_MS = 300;

        public OrderLifecycleManager(IExchangeService exchange)
        {
            _exchange = exchange;
        }

        public void ResetCooldown(string symbol) => _lastRegistered.TryRemove(symbol, out _);

        /// <summary>
        /// 진입 직후 호출 — 기존 조건부 주문 전부 취소 후 SL + TP + Trailing 순차 등록.
        /// 재진입 시 쿨다운으로 중복 호출 자동 방지.
        /// </summary>
        public async Task<BracketIds> RegisterOnEntryAsync(
            string symbol,
            bool isLong,
            decimal entryPrice,
            decimal quantity,
            int leverage,
            decimal stopLossRoePct,      // 음수 (예: -40 = ROE -40%)
            decimal takeProfitRoePct,    // 양수 (예: 25)
            decimal tpPartialRatio,      // 0~1 (예: 0.6 = 60%)
            decimal trailingCallbackRate, // 0.1~5.0 (%)
            CancellationToken ct = default)
        {
            if (entryPrice <= 0 || quantity <= 0)
            {
                OnLog?.Invoke($"⚠️ [OrderLifecycle] {symbol} 유효하지 않은 진입 (price={entryPrice} qty={quantity})");
                return new BracketIds("", "", "", false);
            }

            // 0. 쿨다운 확인 (중복 등록 방지)
            if (_lastRegistered.TryGetValue(symbol, out var last) && (DateTime.Now - last).TotalSeconds < REGISTER_COOLDOWN_SECONDS)
            {
                OnLog?.Invoke($"ℹ️ [OrderLifecycle] {symbol} 최근 {REGISTER_COOLDOWN_SECONDS}초 내 등록됨 → 스킵");
                return new BracketIds("", "", "", false);
            }
            _lastRegistered[symbol] = DateTime.Now;

            // 1. 기존 조건부 주문 전부 취소 — 재시작/중복 호출 시 -4120 방지
            try
            {
                await _exchange.CancelAllOrdersAsync(symbol, ct);
                await Task.Delay(CANCEL_SETTLE_DELAY_MS, ct);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OrderLifecycle] {symbol} 기존 주문 취소 예외: {ex.Message}");
            }

            string closeSide = isLong ? "SELL" : "BUY";

            // 2. SL 가격 계산
            decimal slPriceChange = stopLossRoePct * entryPrice / (leverage * 100m);
            decimal slPrice = isLong ? entryPrice + slPriceChange : entryPrice - slPriceChange;
            if (slPrice <= 0) slPrice = entryPrice * 0.95m;

            // 3. TP 가격/수량 계산
            decimal tpPriceChange = takeProfitRoePct * entryPrice / (leverage * 100m);
            decimal tpPrice = isLong ? entryPrice + tpPriceChange : entryPrice - tpPriceChange;
            decimal tpQty = Math.Floor(quantity * tpPartialRatio * 100m) / 100m;
            if (tpQty <= 0) tpQty = quantity;

            // 4. Trailing 수량 (TP 부분청산 후 잔여분)
            decimal trailQty = quantity - tpQty;
            decimal callback = Math.Clamp(trailingCallbackRate, 0.1m, 5.0m);

            string slId = "";
            string tpId = "";
            string trId = "";

            // ── SL ──────────────────────────────────────────────
            try
            {
                var (ok, orderId) = await _exchange.PlaceStopOrderAsync(symbol, closeSide, quantity, slPrice, ct);
                if (ok)
                {
                    slId = orderId;
                    OnLog?.Invoke($"✅ [SL] {symbol} 등록 | {closeSide} qty={quantity} stop=${slPrice:F6} (ROE{stopLossRoePct}%)");
                }
                else
                {
                    OnLog?.Invoke($"❌ [SL] {symbol} 등록 실패 → 다음 tick 재시도 대상");
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"❌ [SL] {symbol} 예외: {ex.Message}"); }

            // ── TP ──────────────────────────────────────────────
            try
            {
                var (ok, orderId) = await _exchange.PlaceTakeProfitOrderAsync(symbol, closeSide, tpQty, tpPrice, ct);
                if (ok)
                {
                    tpId = orderId;
                    OnLog?.Invoke($"✅ [TP] {symbol} 등록 | {closeSide} qty={tpQty} stop=${tpPrice:F6} (ROE+{takeProfitRoePct}% 부분{tpPartialRatio:P0})");
                }
                else
                {
                    OnLog?.Invoke($"❌ [TP] {symbol} 등록 실패");
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"❌ [TP] {symbol} 예외: {ex.Message}"); }

            // ── TRAILING (TP 활성화 가격에서 시작) ──────────────
            if (trailQty > 0)
            {
                try
                {
                    decimal activation = isLong
                        ? entryPrice * (1m + takeProfitRoePct / (leverage * 100m))
                        : entryPrice * (1m - takeProfitRoePct / (leverage * 100m));
                    var (ok, orderId) = await _exchange.PlaceTrailingStopOrderAsync(
                        symbol, closeSide, trailQty, callback, activation, ct);
                    if (ok)
                    {
                        trId = orderId;
                        OnLog?.Invoke($"✅ [TRAILING] {symbol} 등록 | {closeSide} qty={trailQty} callback={callback}% activation=${activation:F6}");
                    }
                    else
                    {
                        OnLog?.Invoke($"❌ [TRAILING] {symbol} 등록 실패");
                    }
                }
                catch (Exception ex) { OnLog?.Invoke($"❌ [TRAILING] {symbol} 예외: {ex.Message}"); }
            }

            bool anyOk = !string.IsNullOrEmpty(slId) || !string.IsNullOrEmpty(tpId) || !string.IsNullOrEmpty(trId);
            return new BracketIds(slId, tpId, trId, anyOk);
        }

        /// <summary>
        /// 본절 전환 — 기존 SL 취소 후 새 SL(본절가) 등록.
        /// PumpMonitor 본절 로직 및 MonitorPositionStandard Smart Protective Stop에서 호출.
        /// </summary>
        public async Task<string> ReplaceSlAsync(
            string symbol, bool isLong, decimal newStopPrice, decimal quantity,
            string? oldSlOrderId, CancellationToken ct = default)
        {
            if (quantity <= 0 || newStopPrice <= 0) return "";

            // 1. 기존 SL 취소
            if (!string.IsNullOrEmpty(oldSlOrderId))
            {
                try
                {
                    await _exchange.CancelOrderAsync(symbol, oldSlOrderId!, ct);
                    await Task.Delay(CANCEL_SETTLE_DELAY_MS, ct);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [SL교체] {symbol} 기존 SL({oldSlOrderId}) 취소 실패: {ex.Message}");
                }
            }

            // 2. 새 SL 등록
            string closeSide = isLong ? "SELL" : "BUY";
            try
            {
                var (ok, orderId) = await _exchange.PlaceStopOrderAsync(symbol, closeSide, quantity, newStopPrice, ct);
                if (ok)
                {
                    OnLog?.Invoke($"✅ [SL교체] {symbol} 새 SL=${newStopPrice:F6} qty={quantity}");
                    return orderId;
                }
                OnLog?.Invoke($"❌ [SL교체] {symbol} 새 SL 등록 실패");
                return "";
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [SL교체] {symbol} 예외: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// SL 체결 수신 (OrderUpdate WebSocket) → 남은 TP+Trailing 전부 취소.
        /// Binance OCO가 일부 타입에만 적용되므로 명시적으로 잔여 조건부 주문 정리.
        /// </summary>
        public async Task OnStopLossFilledAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                await _exchange.CancelAllOrdersAsync(symbol, ct);
                _lastRegistered.TryRemove(symbol, out _);
                OnLog?.Invoke($"🗑️ [SL발동] {symbol} 잔여 TP/Trailing 취소 완료");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [SL발동] {symbol} 잔여 취소 예외: {ex.Message}");
            }
        }

        /// <summary>
        /// 포지션 완전 청산 (TP 전체 체결 / 수동 청산 / 외부 청산) → 모든 잔여 조건부 주문 취소.
        /// </summary>
        public async Task OnPositionClosedAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                await _exchange.CancelAllOrdersAsync(symbol, ct);
                _lastRegistered.TryRemove(symbol, out _);
                OnLog?.Invoke($"🗑️ [청산완료] {symbol} 잔여 주문 전부 취소");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [청산완료] {symbol} 취소 예외: {ex.Message}");
            }
        }

        /// <summary>
        /// 부분청산 후 잔여 수량에 대한 Trailing 재등록.
        /// 기존 Trailing 명시적 취소 + 새 수량으로 재등록 (race 방지).
        /// </summary>
        public async Task<string> AdjustTrailingAfterPartialCloseAsync(
            string symbol, bool isLong, decimal remainingQty, decimal callbackRate,
            string? oldTrailingOrderId, CancellationToken ct = default)
        {
            if (remainingQty <= 0) return "";

            if (!string.IsNullOrEmpty(oldTrailingOrderId))
            {
                try
                {
                    await _exchange.CancelOrderAsync(symbol, oldTrailingOrderId!, ct);
                    await Task.Delay(CANCEL_SETTLE_DELAY_MS, ct);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [부분청산Trailing] {symbol} 기존 취소 실패: {ex.Message}");
                }
            }

            string closeSide = isLong ? "SELL" : "BUY";
            decimal callback = Math.Clamp(callbackRate, 0.1m, 5.0m);
            try
            {
                var (ok, orderId) = await _exchange.PlaceTrailingStopOrderAsync(
                    symbol, closeSide, remainingQty, callback, activationPrice: null, ct);
                if (ok)
                {
                    OnLog?.Invoke($"✅ [부분청산Trailing] {symbol} 재등록 qty={remainingQty} callback={callback}%");
                    return orderId;
                }
                OnLog?.Invoke($"❌ [부분청산Trailing] {symbol} 재등록 실패");
                return "";
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [부분청산Trailing] {symbol} 예외: {ex.Message}");
                return "";
            }
        }
    }
}
