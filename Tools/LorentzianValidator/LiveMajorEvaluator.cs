// [v5.22.39] 라이브 메이저 진입 로직을 백테스트에 그대로 이식
//   소스: TradingBot/MajorCoinStrategy.cs + IndicatorCalculator.cs (+ Skender)
//   목적: 단순화된 백테스트가 아닌 진짜 라이브 결과 검증
using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;
using Skender.Stock.Indicators;

namespace TradingBot.Tools.LorentzianValidator
{
    public struct LiveBb { public double Upper; public double Mid; public double Lower; }

    public static class LiveMajorEvaluator
    {
        // ───────────────────────────────────────────────────────────────────
        // Indicator helpers — Rolling window (마지막 N봉만 계산)
        //   기존: ToQuotes(0..upTo) 전체 재계산 → O(n²) → 30 심볼 × 50K봉 = 25억 연산
        //   신규: 마지막 lookback 봉만 슬라이스 → O(n) → 100만 연산 (2,500배 ↑)
        // ───────────────────────────────────────────────────────────────────
        public static List<Quote> ToQuotesWindow(List<IBinanceKline> kl, int upTo, int windowSize)
        {
            int from = Math.Max(0, upTo - windowSize + 1);
            int len = upTo - from + 1;
            var q = new List<Quote>(len);
            for (int i = from; i <= upTo && i < kl.Count; i++)
            {
                q.Add(new Quote {
                    Date = kl[i].OpenTime, Open = kl[i].OpenPrice, High = kl[i].HighPrice,
                    Low = kl[i].LowPrice, Close = kl[i].ClosePrice, Volume = kl[i].Volume
                });
            }
            return q;
        }

        public static double Sma(List<IBinanceKline> kl, int upTo, int period)
        {
            if (upTo + 1 < period) return 0;
            var q = ToQuotesWindow(kl, upTo, period);
            return q.GetSma(period).LastOrDefault()?.Sma ?? 0;
        }

        public static double Rsi(List<IBinanceKline> kl, int upTo, int period = 14)
        {
            if (upTo + 1 < period * 2) return 0;
            // RSI 는 워밍업 필요 — period × 5 만큼 사용 (Wilder's smoothing 수렴)
            var q = ToQuotesWindow(kl, upTo, period * 5);
            return q.GetRsi(period).LastOrDefault()?.Rsi ?? 0;
        }

        public static LiveBb Bb(List<IBinanceKline> kl, int upTo, int period = 20, double mult = 2.0)
        {
            if (upTo + 1 < period) return new LiveBb { Upper = 0, Mid = 0, Lower = 0 };
            var q = ToQuotesWindow(kl, upTo, period);
            var bb = q.GetBollingerBands(period, mult).LastOrDefault();
            return new LiveBb {
                Upper = bb?.UpperBand ?? 0, Mid = bb?.Sma ?? 0, Lower = bb?.LowerBand ?? 0
            };
        }

        public static (double Macd, double Signal, double Hist) Macd(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 26) return (0, 0, 0);
            // MACD 는 EMA 워밍업 필요 — 26 × 5 = 130
            var q = ToQuotesWindow(kl, upTo, 150);
            var m = q.GetMacd().LastOrDefault();
            return (m?.Macd ?? 0, m?.Signal ?? 0, m?.Histogram ?? 0);
        }

        public static (double Level236, double Level382, double Level500, double Level618)
            Fib(List<IBinanceKline> kl, int upTo, int lookback = 100)
        {
            if (upTo + 1 < lookback) return (0, 0, 0, 0);
            var subset = kl.GetRange(Math.Max(0, upTo - lookback + 1), Math.Min(lookback, upTo + 1));
            decimal high = subset.Max(c => c.HighPrice);
            decimal low = subset.Min(c => c.LowPrice);
            int stableCount = Math.Max(10, (int)(subset.Count * 0.80));
            decimal stableHigh = subset.Take(stableCount).Max(c => c.HighPrice);
            decimal stableLow = subset.Take(stableCount).Min(c => c.LowPrice);
            if (high > stableHigh * 1.005m) high = stableHigh;
            if (low < stableLow * 0.995m) low = stableLow;
            decimal diff = high - low;
            if (diff <= 0) return (0, 0, 0, 0);
            return ((double)(low + diff * 0.236m), (double)(low + diff * 0.382m),
                    (double)(low + diff * 0.500m), (double)(low + diff * 0.618m));
        }

        public static bool AnalyzeElliottWave(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 50) return false;
            double sma20 = Sma(kl, upTo, 20);
            double sma50 = Sma(kl, upTo, 50);
            if (sma20 == 0 || sma50 == 0) return false;
            if (sma20 > sma50) return true;
            // 최근 5봉 이전 SMA20 비교
            if (upTo - 5 < 20) return false;
            double sma20Prior = Sma(kl, upTo - 5, 20);
            return sma20 > sma20Prior * 1.001;
        }

        // ───────────────────────────────────────────────────────────────────
        // [v5.22.41] 라이브 v5.22.40 AnalyzeMajorSimpleAsync 100% 동일 — aiScore 폐기 후 단순 트리거
        //   가드 1: EMA20 5봉 차이 상승 (i 봉 EMA vs i-5 봉 EMA)
        //   가드 2: RSI14 < 65
        //   가드 3: M15RangePos 60~85% — 직전 30봉 High/Low 범위 내 종가 위치
        //   * aiScore / 3 Tier / Staircase Pursuit / 정배열 확장 모두 폐기 (v5.22.40)
        // ───────────────────────────────────────────────────────────────────
        public static bool ShouldEnterLong(List<IBinanceKline> kl, int upTo, decimal currentPrice)
        {
            if (upTo + 1 < 30 || upTo < 25) return false;

            // 가드 1: EMA20 5봉 차이 상승 — 자체 EMA 계산 (rolling window)
            decimal alpha = 2m / 21m;
            decimal ema = kl[upTo - 25].ClosePrice;
            for (int j = upTo - 24; j <= upTo; j++)
                ema = kl[j].ClosePrice * alpha + ema * (1 - alpha);
            int from5 = Math.Max(0, upTo - 30);
            decimal e5 = kl[from5].ClosePrice;
            for (int j = from5 + 1; j <= upTo - 5; j++)
                e5 = kl[j].ClosePrice * alpha + e5 * (1 - alpha);
            if (ema <= e5) return false;

            // 가드 2: RSI14 < 65
            double rsi = Rsi(kl, upTo, 14);
            if (rsi >= 65) return false;

            // 가드 3: M15RangePos 60~85%
            decimal high30 = kl[upTo - 29].HighPrice;
            decimal low30 = kl[upTo - 29].LowPrice;
            for (int i = upTo - 28; i <= upTo; i++)
            {
                if (kl[i].HighPrice > high30) high30 = kl[i].HighPrice;
                if (kl[i].LowPrice < low30) low30 = kl[i].LowPrice;
            }
            decimal range = high30 - low30;
            if (range <= 0) return false;
            double rangePos = (double)((kl[upTo].ClosePrice - low30) / range * 100m);
            return rangePos >= 60.0 && rangePos <= 85.0;
        }

        // CalculateScore — MajorCoinStrategy.cs:242 그대로 복사
        public static int CalculateScore(double rsi, LiveBb bb, decimal currentPrice, bool isUptrend,
            (double Macd, double Signal, double Hist) macd,
            (double Level236, double Level382, double Level500, double Level618) fib,
            double sma20, double sma50, double sma60, double sma120,
            double volumeMomentum, bool isMakingHigherLows, bool allowLowVolumeTrendBypass,
            int higherLowBonus)
        {
            int score = 50;

            if (isUptrend) score += 12; else score -= 12;
            if (sma20 > sma60) score += 10; else score -= 10;
            if (sma60 > sma120) score += 8; else score -= 8;

            if (currentPrice > (decimal)sma20 && sma20 > sma50) score += 10;
            else if (currentPrice < (decimal)sma20 && sma20 < sma50) score -= 10;

            if (rsi >= 55 && rsi <= 68) score += 8;
            else if (rsi >= 32 && rsi <= 45) score -= 8;
            else if (rsi > 75) score -= 10;
            else if (rsi < 25) score += 6;

            if (macd.Hist > 0) score += 10; else score -= 10;

            if (currentPrice >= (decimal)fib.Level382) score += 6;
            if (currentPrice < (decimal)fib.Level618) score -= 10;

            double price = (double)currentPrice;
            if (price >= bb.Mid) score += 6; else score -= 6;
            if (price > bb.Upper && rsi > 72) score -= 8;
            else if (price < bb.Lower && rsi < 30) score += 6;

            if (volumeMomentum >= 1.10) { score += (sma20 > sma60 ? 10 : -10); }
            else if (volumeMomentum >= 1.00) { score += (sma20 > sma60 ? 5 : -5); }
            else if (allowLowVolumeTrendBypass) score += 15;

            if (isMakingHigherLows && currentPrice > (decimal)sma20) score += higherLowBonus;

            return Math.Clamp(score, 0, 100);
        }

        public static bool IsMakingHigherLows(List<IBinanceKline> kl, int upTo, int segmentSize, decimal minRiseRatio)
        {
            const int requiredSegments = 3;
            int requiredCandles = segmentSize * requiredSegments;
            if (upTo + 1 < requiredCandles) return false;
            var window = kl.GetRange(upTo - requiredCandles + 1, requiredCandles);
            decimal low1 = window.Take(segmentSize).Min(c => c.LowPrice);
            decimal low2 = window.Skip(segmentSize).Take(segmentSize).Min(c => c.LowPrice);
            decimal low3 = window.Skip(segmentSize * 2).Take(segmentSize).Min(c => c.LowPrice);
            return low2 >= low1 * minRiseRatio && low3 >= low2 * minRiseRatio;
        }

        public static double CalculateFibScore(List<IBinanceKline> kl, int upTo, decimal currentPrice)
        {
            if (upTo + 1 < 30) return 0;
            int from = Math.Max(0, upTo - 99);
            var recent = kl.GetRange(from, upTo - from + 1);
            decimal high = recent.Max(c => c.HighPrice);
            decimal low = recent.Min(c => c.LowPrice);
            decimal range = high - low;
            if (range <= 0m) return 0;
            decimal fib382 = low + range * 0.382m;
            decimal fib500 = low + range * 0.500m;
            decimal fib618 = low + range * 0.618m;
            decimal fib786 = low + range * 0.786m;
            if (currentPrice >= fib382 && currentPrice <= fib500) return 10;
            if (currentPrice >= fib500 && currentPrice <= fib618) return 8;
            if (currentPrice >= fib618 && currentPrice <= fib786) return 5;
            return 0;
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // [v5.22.40] 라이브 AnalyzeAltSimpleTriggersAsync 100% 동일 (v5.22.39 백테스트 원본)
    //   가드:    EMA20 5봉 차이 상승 + RSI<65
    //   트리거 1: SQUEEZE — BBW<1.5% + 상단돌파
    //   트리거 2: BB_WALK — 5봉 중 4봉 종가>Upper
    //   MID_BREAK 제거 (v5.22.39)
    // ───────────────────────────────────────────────────────────────────
    public static class LiveAltEvaluator
    {
        public static bool ShouldEnterLong(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 26) return false;

            // 가드 1: EMA20 5봉 차이 상승 (라이브 IsEma20Rising 100% 일치)
            decimal alpha = 2m / 21m;
            decimal ema = kl[upTo - 25].ClosePrice;
            for (int j = upTo - 24; j <= upTo; j++)
                ema = kl[j].ClosePrice * alpha + ema * (1 - alpha);
            int from5 = Math.Max(0, upTo - 30);
            decimal e5 = kl[from5].ClosePrice;
            for (int j = from5 + 1; j <= upTo - 5; j++)
                e5 = kl[j].ClosePrice * alpha + e5 * (1 - alpha);
            if (ema <= e5) return false;

            // 가드 2: RSI14 < 65
            double rsi = LiveMajorEvaluator.Rsi(kl, upTo, 14);
            if (rsi >= 65) return false;

            // BB(20,2)
            var bb = LiveMajorEvaluator.Bb(kl, upTo, 20, 2);
            if (bb.Mid <= 0) return false;
            decimal upper = (decimal)bb.Upper;
            decimal mid = (decimal)bb.Mid;
            decimal lower = (decimal)bb.Lower;
            decimal bbWidthPct = (upper - lower) / mid * 100m;
            decimal lastClose = kl[upTo].ClosePrice;

            // 트리거 1: SQUEEZE — BBW<1.5% + 상단돌파 (백테스트 원본)
            bool sqz = bbWidthPct < 1.5m && lastClose > upper;

            // 트리거 2: BB_WALK — 5봉 중 4봉 종가 > Upper (백테스트 원본)
            int walkCount = 0;
            for (int i = Math.Max(0, upTo - 4); i <= upTo; i++)
                if (kl[i].ClosePrice > upper) walkCount++;
            bool walk = walkCount >= 4;

            return sqz || walk;
        }
    }
}
