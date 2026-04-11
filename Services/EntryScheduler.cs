using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.0] 예약 진입 스케줄러 — Forecaster 예측 결과를 받아 LIMIT 주문 예약 관리
    ///
    /// 기능:
    /// 1. Register: LIMIT 주문 발주 + 관리
    /// 2. 가격 Watchdog: 조기 돌파 시 Market Fallback
    /// 3. 만료 체크: Expiry 경과 시 자동 취소 → 재예측 대기
    /// 4. 중복 방지: 심볼당 1개 예약만 허용
    /// </summary>
    public class EntryScheduler : IDisposable
    {
        private readonly IExchangeService _exchange;
        private readonly ConcurrentDictionary<string, PendingEntry> _pending = new();
        private readonly Func<string, string, (bool allowed, string reason)>? _mtfGuardian;
        private readonly Func<string, string, decimal, CancellationToken, Task>? _marketFallbackExecutor;

        private readonly Timer _expiryTimer;
        private bool _disposed;

        public event Action<string>? OnLog;
        public event Action<PendingEntry>? OnEntryRegistered;
        public event Action<PendingEntry, string>? OnEntryCancelled;   // (entry, reason)
        public event Action<PendingEntry, decimal>? OnMarketFallback;  // (entry, actualPrice)

        public int PendingCount => _pending.Count;
        public IReadOnlyDictionary<string, PendingEntry> Snapshot => _pending;

        public EntryScheduler(
            IExchangeService exchange,
            Func<string, string, (bool allowed, string reason)>? mtfGuardian = null,
            Func<string, string, decimal, CancellationToken, Task>? marketFallbackExecutor = null)
        {
            _exchange = exchange;
            _mtfGuardian = mtfGuardian;
            _marketFallbackExecutor = marketFallbackExecutor;

            // 만료 체크 타이머 (매 10초)
            _expiryTimer = new Timer(async _ => await CheckExpirationsAsync(), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        // ═══════════════════════════════════════════════════════════════
        // 등록
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Forecaster 예측 결과로 예약 등록. MTF Guardian 통과 + LIMIT 주문 발주.
        /// </summary>
        public async Task<bool> RegisterAsync(
            string symbol,
            ForecastResult forecast,
            decimal currentPrice,
            decimal quantity,
            int leverage,
            CancellationToken token = default)
        {
            if (!forecast.HasOpportunity)
            {
                return false;
            }

            // MTF Guardian 사전 검증
            if (_mtfGuardian != null)
            {
                var (allowed, reason) = _mtfGuardian(symbol, forecast.Direction);
                if (!allowed)
                {
                    OnLog?.Invoke($"⛔ [SCHED] {symbol} {forecast.Direction} MTF 차단: {reason}");
                    return false;
                }
            }

            // 기존 pending 있으면 취소 후 교체
            if (_pending.TryRemove(symbol, out var existing) && !string.IsNullOrEmpty(existing.LimitOrderId))
            {
                try
                {
                    await _exchange.CancelOrderAsync(symbol, existing.LimitOrderId!, token);
                    OnLog?.Invoke($"🔄 [SCHED] {symbol} 기존 예약 취소 후 재등록");
                }
                catch (Exception ex) { OnLog?.Invoke($"⚠️ [SCHED] {symbol} 기존 취소 실패: {ex.Message}"); }
            }

            // 타겟 가격 계산 (현재가 대비 offset)
            decimal targetPrice = currentPrice * (1m + (decimal)forecast.PriceOffsetPct / 100m);
            if (targetPrice <= 0)
            {
                OnLog?.Invoke($"⚠️ [SCHED] {symbol} 타겟가격 오류 ({targetPrice})");
                return false;
            }

            // 예측 시점 계산 (5분봉 기준: offset × 5분, 1분봉이면 호출측에서 조정 필요)
            DateTime predictedTime = DateTime.Now.AddMinutes(forecast.OffsetBars * 5);
            DateTime expiry = predictedTime.AddMinutes(10); // 10분 버퍼

            // LIMIT 주문 발주
            try
            {
                var (ok, orderId) = await _exchange.PlaceLimitOrderAsync(
                    symbol, forecast.Direction, quantity, targetPrice, token);

                if (!ok || string.IsNullOrEmpty(orderId))
                {
                    OnLog?.Invoke($"❌ [SCHED] {symbol} LIMIT 발주 실패");
                    return false;
                }

                var entry = new PendingEntry
                {
                    Symbol = symbol,
                    Direction = forecast.Direction,
                    TargetPrice = targetPrice,
                    Quantity = quantity,
                    Leverage = leverage,
                    PredictedEntryTime = predictedTime,
                    Expiry = expiry,
                    Confidence = forecast.Probability,
                    Source = forecast.SymbolType,
                    LimitOrderId = orderId,
                    RegisteredAt = DateTime.Now,
                    OriginalForecast = forecast
                };

                _pending[symbol] = entry;
                OnEntryRegistered?.Invoke(entry);

                OnLog?.Invoke(
                    $"📋 [SCHED] {symbol} {forecast.Direction} LIMIT ${targetPrice:F6} " +
                    $"(현재${currentPrice:F6} offset={forecast.PriceOffsetPct:+0.00;-0.00}%) " +
                    $"예정={predictedTime:HH:mm} 만료={expiry:HH:mm} 신뢰={forecast.Probability:P0} src={forecast.SymbolType}");

                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [SCHED] {symbol} 발주 예외: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 가격 Watchdog — 실시간 틱마다 호출
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 실시간 가격 업데이트 시 호출. 조기 돌파 감지되면 Market Fallback 실행.
        /// OnTickerUpdate 이벤트 구독자에서 호출하는 걸 권장.
        /// </summary>
        public async Task OnPriceTickAsync(string symbol, decimal price, CancellationToken token = default)
        {
            if (!_pending.TryGetValue(symbol, out var entry)) return;
            if (price <= 0) return;

            // 조기 돌파 판정
            bool earlyBreakout;
            if (entry.Direction == "LONG")
            {
                // LONG: 타겟가보다 이미 +1% 이상 올라가버림 → 기회 놓치기 전에 즉시 진입
                earlyBreakout = price > entry.TargetPrice * 1.010m;
            }
            else
            {
                // SHORT: 타겟가보다 이미 -1% 이상 내려감
                earlyBreakout = price < entry.TargetPrice * 0.990m;
            }

            if (earlyBreakout)
            {
                await ExecuteMarketFallbackAsync(entry, price, token);
            }
        }

        private async Task ExecuteMarketFallbackAsync(PendingEntry entry, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // 기존 LIMIT 취소
                if (!string.IsNullOrEmpty(entry.LimitOrderId))
                {
                    try { await _exchange.CancelOrderAsync(entry.Symbol, entry.LimitOrderId!, token); }
                    catch (Exception ex) { OnLog?.Invoke($"⚠️ [SCHED] {entry.Symbol} LIMIT 취소 실패: {ex.Message}"); }
                }

                _pending.TryRemove(entry.Symbol, out _);

                OnLog?.Invoke(
                    $"⚡ [SCHED] {entry.Symbol} 조기 돌파 ${currentPrice:F6} > ${entry.TargetPrice:F6} " +
                    $"→ Market Fallback (예정={entry.PredictedEntryTime:HH:mm} 실제={DateTime.Now:HH:mm})");

                OnMarketFallback?.Invoke(entry, currentPrice);

                // 외부 executor 가 있으면 Market 진입 위임
                if (_marketFallbackExecutor != null)
                {
                    await _marketFallbackExecutor(entry.Symbol, entry.Direction, currentPrice, token);
                }
                else
                {
                    // 기본: 직접 Market 주문
                    await _exchange.PlaceMarketOrderAsync(entry.Symbol, entry.Direction, entry.Quantity, token);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [SCHED] {entry.Symbol} Market Fallback 예외: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 만료 체크 (매 10초)
        // ═══════════════════════════════════════════════════════════════

        private async Task CheckExpirationsAsync()
        {
            if (_disposed) return;

            var now = DateTime.Now;
            var expired = _pending
                .Where(kvp => now > kvp.Value.Expiry)
                .Select(kvp => kvp.Value)
                .ToList();

            foreach (var entry in expired)
            {
                try
                {
                    // 거래소 상태 체크 — 이미 체결됐으면 취소 안 함
                    if (!string.IsNullOrEmpty(entry.LimitOrderId))
                    {
                        var (filled, _, _) = await _exchange.GetOrderStatusAsync(entry.Symbol, entry.LimitOrderId!);
                        if (filled)
                        {
                            _pending.TryRemove(entry.Symbol, out _);
                            OnLog?.Invoke($"✅ [SCHED] {entry.Symbol} LIMIT 체결 완료 → pending 제거");
                            continue;
                        }

                        await _exchange.CancelOrderAsync(entry.Symbol, entry.LimitOrderId!);
                    }

                    _pending.TryRemove(entry.Symbol, out _);
                    OnEntryCancelled?.Invoke(entry, "expired");
                    OnLog?.Invoke(
                        $"⏰ [SCHED] {entry.Symbol} 예약 만료 (예정={entry.PredictedEntryTime:HH:mm}) → 취소, 재예측 대기");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [SCHED] {entry.Symbol} 만료 처리 오류: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 수동 취소 / 조회
        // ═══════════════════════════════════════════════════════════════

        public async Task<bool> CancelAsync(string symbol, string reason = "manual")
        {
            if (!_pending.TryRemove(symbol, out var entry)) return false;

            if (!string.IsNullOrEmpty(entry.LimitOrderId))
            {
                try { await _exchange.CancelOrderAsync(symbol, entry.LimitOrderId!); }
                catch (Exception ex) { OnLog?.Invoke($"⚠️ [SCHED] {symbol} 취소 실패: {ex.Message}"); }
            }

            OnEntryCancelled?.Invoke(entry, reason);
            OnLog?.Invoke($"🗑️ [SCHED] {symbol} 수동 취소 ({reason})");
            return true;
        }

        public bool HasPending(string symbol) => _pending.ContainsKey(symbol);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _expiryTimer?.Dispose();
        }
    }
}
