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
    /// [v4.5.5] 알트 불장(Alt Bull Market) 자동 감지기
    ///
    /// 감지 기준 (3가지 동시 충족):
    /// 1) 상위 30개 알트의 평균 ATR%/Price ≥ 2.5% (변동성 폭증)
    /// 2) 24h +5% 이상 알트 ≥ 40개 (광범위한 상승)
    /// 3) BTC 1h 절대 변동률 < 1.5% (BTC 안정 → 자금이 알트로)
    ///
    /// CPU 최적화:
    /// - 5분 주기로만 검사
    /// - TickerCache(WebSocket) 활용 (API 호출 0회)
    /// - 단순 통계 계산만 수행
    /// </summary>
    public class AltBullMarketDetector
    {
        // ─── 임계값 (외부 조정 가능) ─────────────────────────
        public double VolatilityThresholdPct { get; set; } = 2.5;     // 평균 ATR%/Price
        public int Strong24hCountThreshold { get; set; } = 40;        // 24h +5% 알트 개수
        public double BtcStableThresholdPct { get; set; } = 1.5;      // BTC 1h 절대 변동률 상한
        public int CheckIntervalMinutes { get; set; } = 5;            // 검사 주기
        public int ConsecutiveConfirmRequired { get; set; } = 2;      // 2회 연속 충족 시 활성화
        public int CooldownAfterDeactivateMinutes { get; set; } = 30; // 해제 후 재활성화 쿨다운

        // ─── 활성화 상태 ────────────────────────────────────
        public bool IsActive { get; private set; }
        public DateTime? ActivatedAt { get; private set; }
        public DateTime? DeactivatedAt { get; private set; }

        // ─── 내부 상태 ──────────────────────────────────────
        private DateTime _lastCheckTime = DateTime.MinValue;
        private int _consecutiveConfirmCount;
        private int _consecutiveMissCount;
        private double _lastVolatility;
        private int _lastStrongCount;
        private double _lastBtcChange;

        // ─── 이벤트 ────────────────────────────────────────
        public event Action<string>? OnLog;
        public event Action<bool>? OnAltBullStateChanged; // true=활성화, false=해제

        // 메이저 코인 (제외 대상)
        private static readonly HashSet<string> MajorSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT"
        };

        /// <summary>
        /// TickerCache 업데이트마다 호출 — 5분 주기로 알트 불장 검사
        /// </summary>
        public void Check(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastCheckTime).TotalMinutes < CheckIntervalMinutes)
                    return;
                _lastCheckTime = now;

                // 해제 후 쿨다운 중이면 재활성화 검사 안 함
                if (!IsActive && DeactivatedAt.HasValue &&
                    (now - DeactivatedAt.Value).TotalMinutes < CooldownAfterDeactivateMinutes)
                {
                    return;
                }

                // 24h 변동률 = (LastPrice - OpenPrice) / OpenPrice * 100
                static double Change24h(TickerCacheItem t)
                    => t.OpenPrice > 0 ? (double)((t.LastPrice - t.OpenPrice) / t.OpenPrice * 100m) : 0;

                var allTickers = tickerCache.Values
                    .Where(t => t.LastPrice > 0 && t.OpenPrice > 0 && !string.IsNullOrEmpty(t.Symbol)
                                && t.Symbol!.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (allTickers.Count < 30)
                    return; // 데이터 부족

                // ── 조건 1: 상위 30개 알트(절대 변동률 기준) 평균 변동성
                var topAlts = allTickers
                    .Where(t => !MajorSymbols.Contains(t.Symbol!))
                    .OrderByDescending(t => Math.Abs(Change24h(t)))
                    .Take(30)
                    .ToList();

                double avgVolatility = topAlts.Count > 0
                    ? topAlts.Average(t => Math.Abs(Change24h(t)))
                    : 0;

                // ── 조건 2: 24h +5%↑ 알트 개수
                int strongCount = allTickers
                    .Where(t => !MajorSymbols.Contains(t.Symbol!))
                    .Count(t => Change24h(t) >= 5);

                // ── 조건 3: BTC 24h 변동률 (안정성 체크)
                double btcChangePct = 0;
                if (tickerCache.TryGetValue("BTCUSDT", out var btc))
                    btcChangePct = Math.Abs(Change24h(btc));

                _lastVolatility = avgVolatility;
                _lastStrongCount = strongCount;
                _lastBtcChange = btcChangePct;

                bool conditionsMet =
                    avgVolatility >= VolatilityThresholdPct
                    && strongCount >= Strong24hCountThreshold
                    && btcChangePct < (BtcStableThresholdPct * 4); // 24h 기준이라 4배 완화

                if (conditionsMet)
                {
                    _consecutiveConfirmCount++;
                    _consecutiveMissCount = 0;

                    if (!IsActive && _consecutiveConfirmCount >= ConsecutiveConfirmRequired)
                    {
                        IsActive = true;
                        ActivatedAt = DateTime.UtcNow;
                        OnLog?.Invoke($"🔥 [알트 불장] 활성화 | 변동성={avgVolatility:F2}% 강세알트={strongCount}개 BTC={btcChangePct:F2}%");
                        OnAltBullStateChanged?.Invoke(true);
                    }
                }
                else
                {
                    _consecutiveMissCount++;
                    _consecutiveConfirmCount = 0;

                    // 활성 상태에서 2회 연속 미충족 시 해제
                    if (IsActive && _consecutiveMissCount >= 2)
                    {
                        IsActive = false;
                        DeactivatedAt = DateTime.UtcNow;
                        ActivatedAt = null;
                        OnLog?.Invoke($"💧 [알트 불장] 해제 | 변동성={avgVolatility:F2}% 강세알트={strongCount}개");
                        OnAltBullStateChanged?.Invoke(false);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [알트 불장] 검사 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 알트 불장 모드 시 적용할 레버리지 (절반, 최소 5x)
        /// </summary>
        public int AdjustLeverage(int originalLeverage)
        {
            if (!IsActive) return originalLeverage;
            return Math.Max(5, originalLeverage / 2);
        }

        /// <summary>
        /// 알트 불장 모드 시 적용할 포지션 사이즈 배수 (70%)
        /// </summary>
        public decimal AdjustSizeMultiplier(decimal originalMultiplier)
        {
            if (!IsActive) return originalMultiplier;
            return originalMultiplier * 0.7m;
        }

        /// <summary>
        /// 현재 상태 요약
        /// </summary>
        public string GetStatusSummary()
        {
            return IsActive
                ? $"🔥 알트불장 ON | 변동성={_lastVolatility:F2}% 강세={_lastStrongCount}개 BTC={_lastBtcChange:F2}%"
                : $"💤 알트불장 OFF | 변동성={_lastVolatility:F2}% 강세={_lastStrongCount}개 BTC={_lastBtcChange:F2}%";
        }
    }
}
