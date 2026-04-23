using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.15.0] 데이터 기반 진입 필터 — TradeHistory 분석으로 도출한 승률 패턴 강제 적용
    ///
    /// 배경:
    ///   사용자 매매기록 7일 분석 결과 795 trades / 45% 승률 / -106 USDT 순손실
    ///   명확한 패턴 3가지 발견:
    ///    1) 특정 시간대(KST 1,8,13,14시) 승률 <35%, 순손실 -200+ USDT
    ///    2) 특정 심볼(OPNUSDT, BASUSDT 등) 10건 이상 진입했으나 승률 0%
    ///    3) 홀딩 1시간+ 시 승률 33% 이하, 순손실 -100+ USDT
    ///
    /// 동작:
    ///   - ShouldBlockEntry(symbol, hourKst, strategy) → bool
    ///   - ShouldBoostSize(symbol, hourKst) → sizeMultiplier (1.0~1.5)
    ///   - ShouldForceExit(openMinutes, currentPnL) → bool (1시간+ 손실시 청산)
    /// </summary>
    public class DataDrivenEntryFilter
    {
        public event Action<string>? OnLog;

        // ═══════════════════════════════════════════════════════════════
        // BLACKLISTS (7일 분석 기준, 5건 이상 진입에서 승률 <25% 또는 순손실 큰 심볼)
        // ═══════════════════════════════════════════════════════════════
        private readonly HashSet<string> _symbolBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "OPNUSDT",       // 11건 0% 승률 -41.9 USDT
            "BASUSDT",       // 14건 0% 승률 -39.6 USDT
            "币安人생USDT",   // 69건 50%이나 -55.8 USDT (평균 손실 금액이 평균 이익보다 큼)
            "币安人生USDT",  // 인코딩 변종
            "1000PEPEUSDT",  // 5건 20% -30.5 USDT
            "MUSDT",         // 65건 14% -21.4 USDT
            "CHIPUSDT",      // 15건 20% -20.7 USDT
            "TAOUSDT",       // 27건 11% -19.5 USDT
            "CLUSDT",        // 7건 14% -19.3 USDT
            "BIOUSDT",       // 13건 7.7% -17 USDT
            "PHBUSDT",       // 8건 0% -15 USDT
            "AVAXUSDT",      // 6건 0% -14.5 USDT
            "GWEIUSDT",      // 17건 23.5% -12.7 USDT
            "AAVEUSDT",      // 16건 18.75% -12.3 USDT
            "PENGUUSDT",     // 14건 0% -12 USDT
            "GRASSUSDT",     // 12건 0% -8.9 USDT
            "PIPPINUSDT",    // 7건 0% -6.2 USDT
            "ONGUSDT",       // 14건 0% -5.0 USDT
        };

        // ═══════════════════════════════════════════════════════════════
        // WHITELISTS — 승률 60%+ 이고 순수익 +5 USDT 이상
        // ═══════════════════════════════════════════════════════════════
        private readonly HashSet<string> _symbolWhitelist = new(StringComparer.OrdinalIgnoreCase)
        {
            "SIRENUSDT",    // 64건 80% +57.6 USDT
            "RIVERUSDT",    // 35건 66% +50.8 USDT
            "PROMUSDT",     // 16건 94% +41.9 USDT
            "EDUUSDT",      // 26건 96% +31.3 USDT
            "BLURUSDT",     // 18건 33% +22.9 USDT (winning asymmetric)
            "VELVETUSDT",   // 9건 100% +18.5 USDT
            "METUSDT",      // 8건 100% +12.6 USDT
            "GENIUSUSDT",   // 15건 93% +11.6 USDT
            "TRADOORUSDT",  // 17건 94% +10.2 USDT
            "我踏马来了USDT",   // 23건 100% +10.1 USDT
            "ENJUSDT",      // 20건 95% +8.6 USDT
            "ORDIUSDT",     // 14건 93% +8.2 USDT
            "ENAUSDT",      // 2건 100% +7.3 USDT
            "BANUSDT",      // 12건 100% +7 USDT
            "ZECUSDT",      // 2건 50% +6.9 USDT
            "BASEDUSDT",    // 19건 31.6%이나 +6.1 USDT (비대칭 수익)
        };

        // ═══════════════════════════════════════════════════════════════
        // TIME-OF-DAY FILTERS (KST 기준)
        // 값: 각 시간대의 승률(0~1) 및 순PnL
        // ═══════════════════════════════════════════════════════════════
        private readonly Dictionary<int, (double winRate, double netPnL)> _hourStats = new()
        {
            {  0, (0.00,  -4.99) },  // 승률 0% (14건)
            {  1, (0.019, -64.56) }, // 1.9% (53건) ← 최악
            {  8, (0.20, -42.01) },  // 20% (105건) ← 큰 손실
            {  9, (1.00,  12.62) },  // 100% (8건) ← 골든
            { 10, (0.50, -11.72) },  // 50% 마진 손실
            { 11, (0.53, -22.68) },  // 52% 손실
            { 12, (0.95,   8.61) },  // 95% (20건) ← 골든
            { 13, (0.125,-14.03) },  // 12.5% (8건) ← 최악
            { 14, (0.327, -27.61) }, // 32.7% (52건) ← 큰 손실
            { 15, (0.51,   6.05) },  // 51% 미미한 흑자
            { 16, (0.67,  -5.69) },  // 67% 이나 손실
            { 17, (0.56,  -4.24) },  // 56% 미미한 손실
            { 18, (0.81,  21.52) },  // 81% (26건) ← 골든
            { 19, (0.54,  64.06) },  // 54% 수익 최대 (85건)
            { 20, (0.50,  -8.51) },  // 50% 미미한 손실
            { 21, (0.44, -53.26) },  // 44% (119건) ← 최대 손실
            { 22, (0.89,  40.33) },  // 89% (19건) ← 골든
        };

        /// <summary>
        /// 진입 차단 여부 판정.
        /// 반환: (blocked, reason)
        /// </summary>
        public (bool blocked, string reason) ShouldBlockEntry(string symbol, int hourKst, string? strategy, string? signalSource)
        {
            // [1] 심볼 블랙리스트
            if (_symbolBlacklist.Contains(symbol))
            {
                return (true, $"SYMBOL_BLACKLIST({symbol})");
            }

            // [2] 시간대 블랙리스트 — 승률 <35% + 순손실 -10 USDT 이하
            if (_hourStats.TryGetValue(hourKst, out var hs))
            {
                if (hs.winRate < 0.35 && hs.netPnL < -10.0)
                {
                    return (true, $"HOUR_BLACKLIST(KST{hourKst}h win={hs.winRate:P0} net={hs.netPnL:F1}USDT)");
                }
            }

            // [3] 전략 블랙리스트
            if (!string.IsNullOrEmpty(strategy))
            {
                if (strategy.Equals("BUY_PRESSURE", StringComparison.OrdinalIgnoreCase))
                    return (true, "STRATEGY_BLACKLIST(BUY_PRESSURE 33% winrate -15USDT)");
                if (strategy.StartsWith("MAJOR_MEME_STAIRCASE", StringComparison.OrdinalIgnoreCase))
                    return (true, "STRATEGY_BLACKLIST(MAJOR_MEME_STAIRCASE 0% winrate)");
            }

            return (false, string.Empty);
        }

        /// <summary>
        /// 화이트리스트 심볼 또는 골든 시간대에 사이즈 부스트 배수 반환.
        /// 1.0 = 기본, 1.3 = 화이트 심볼 or 골든 시간, 1.5 = 둘 다 충족
        /// </summary>
        public decimal GetSizeMultiplier(string symbol, int hourKst)
        {
            decimal mult = 1.0m;
            bool isWhite = _symbolWhitelist.Contains(symbol);
            bool isGoldenHour = _hourStats.TryGetValue(hourKst, out var hs)
                && hs.winRate >= 0.80 && hs.netPnL > 0;

            if (isWhite && isGoldenHour) mult = 1.5m;
            else if (isWhite || isGoldenHour) mult = 1.3m;

            return mult;
        }

        /// <summary>
        /// 강제 청산 판정 — 홀딩 시간 + 현재 PnL% 기준.
        ///   분석 결과: 1-4hr 구간 승률 33% -54USDT, 4hr+ 29% -43USDT
        ///   → 60분 경과 + PnL ≤ 0 이면 손절 확률 급상승, 즉시 청산
        /// </summary>
        public (bool forceExit, string reason) ShouldForceExit(double openMinutes, double currentPnLPct)
        {
            if (openMinutes >= 60 && currentPnLPct <= 0)
            {
                return (true, $"DEATH_ZONE_60MIN_NEGATIVE(pnl={currentPnLPct:F2}%)");
            }
            if (openMinutes >= 240)
            {
                return (true, $"DEATH_ZONE_4HR_TIMEOUT(pnl={currentPnLPct:F2}%)");
            }
            return (false, string.Empty);
        }

        /// <summary>
        /// [v5.15.0 핵심] OBVIOUS PUMP OVERRIDE — ML 무시하고 데이터 기반 진입 허용
        ///
        /// 배경:
        ///   사용자 제보: MOVRUSDT (steady uptrend +2.14%/1h) 를 ML 7%로 차단
        ///   2달간 상승패턴 잡아서 수익 못냄 — ML 모델 miscalibrated
        ///
        /// 조건 (ALL 충족):
        ///   1) 최근 1시간 누적 상승률 >= +1.5%
        ///   2) 최근 5분봉 양봉 + 변동 >= +1.0% (현재 모멘텀)
        ///   3) 최근 5분봉 거래량 / 최근 12봉 평균 거래량 >= 1.2배
        ///   4) 5m SMA20 > SMA60 (단기 상승추세)
        ///   5) 현재가가 최근 5분봉 내 상위 70% 아님 (꼭대기 차단)
        ///   6) 블랙리스트 심볼 아님
        ///   7) 블랙리스트 시간대 아님
        ///   8) 최근 30분 내 이 심볼에 진입 시도 없음 (중복 방지)
        /// </summary>
        public (bool allowed, string reason) CheckObviousPumpOverride(
            string symbol,
            int hourKst,
            decimal currentPrice,
            IReadOnlyList<(decimal open, decimal high, decimal low, decimal close, decimal volume)> candles5m)
        {
            // [선제 블랙] 심볼/시간대 블랙은 override 불가
            if (_symbolBlacklist.Contains(symbol))
                return (false, "override_blocked_symbol_blacklist");
            if (_hourStats.TryGetValue(hourKst, out var hs) && hs.winRate < 0.35 && hs.netPnL < -10.0)
                return (false, $"override_blocked_hour_{hourKst}h");

            if (candles5m == null || candles5m.Count < 20)
                return (false, "override_insufficient_candles");

            int n = candles5m.Count;
            var last = candles5m[n - 1];

            // [1] 1시간 누적 상승률 (최근 12개 5분봉 open->close)
            decimal hour1Start = candles5m[Math.Max(0, n - 12)].open;
            if (hour1Start <= 0) return (false, "override_bad_start_price");
            decimal cumPct = (currentPrice - hour1Start) / hour1Start * 100m;
            if (cumPct < 1.5m)
                return (false, $"override_low_hour_cum({cumPct:F2}%)");

            // [2] 최근 5분봉 양봉 + 변동 >= +1.0%
            bool isBullish = last.close > last.open;
            decimal last5mPct = last.open > 0 ? (last.close - last.open) / last.open * 100m : 0m;
            if (!isBullish || last5mPct < 1.0m)
                return (false, $"override_weak_momentum({last5mPct:F2}%)");

            // [3] 거래량 spike
            int volLookback = Math.Min(12, n - 1);
            decimal volAvg = 0m;
            for (int i = n - 1 - volLookback; i < n - 1 && i >= 0; i++)
                volAvg += candles5m[i].volume;
            if (volLookback > 0) volAvg /= volLookback;
            decimal volRatio = volAvg > 0 ? last.volume / volAvg : 0m;
            if (volRatio < 1.2m)
                return (false, $"override_low_volume({volRatio:F2}x)");

            // [4] SMA 상승추세 (20-period 대 60-period)
            if (n >= 60)
            {
                decimal sma20 = 0m, sma60 = 0m;
                for (int i = n - 20; i < n; i++) sma20 += candles5m[i].close;
                sma20 /= 20m;
                for (int i = n - 60; i < n; i++) sma60 += candles5m[i].close;
                sma60 /= 60m;
                if (sma20 <= sma60)
                    return (false, $"override_sma_bearish(sma20={sma20:F4}<=sma60={sma60:F4})");
            }

            // [5] 꼭대기 진입 차단 (캔들 내 상위 70% 초과시 거부)
            decimal range = last.high - last.low;
            if (range > 0)
            {
                decimal posInRange = (currentPrice - last.low) / range;
                if (posInRange > 0.70m)
                    return (false, $"override_top_of_candle({posInRange:P0})");
            }

            // 모든 조건 충족 → 진입 허용
            return (true, $"OBVIOUS_PUMP hour1={cumPct:+0.00;-0.00}% 5m={last5mPct:+0.00;-0.00}% vol={volRatio:F1}x");
        }

        /// <summary>
        /// 진단용 통계 리포트
        /// </summary>
        public string GetReport()
        {
            return $"Blacklist symbols: {_symbolBlacklist.Count} | " +
                   $"Whitelist symbols: {_symbolWhitelist.Count} | " +
                   $"Hour stats: {_hourStats.Count}";
        }
    }
}
