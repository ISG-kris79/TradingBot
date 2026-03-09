using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot
{
    /// <summary>
    /// 백테스팅 기반으로 Entry Timing 레이블을 생성하는 유틸리티
    /// 목적: 과거 캔들 데이터에서 "이 시점에 진입했으면 수익이 났는가?"를 판단
    /// </summary>
    public class BacktestEntryLabeler
    {
        private readonly EntryLabelConfig _config;

        public BacktestEntryLabeler(EntryLabelConfig? config = null)
        {
            _config = config ?? new EntryLabelConfig();
        }

        /// <summary>
        /// 특정 시점(현재 캔들)에서 LONG 진입했을 때 수익이 났는지 판단
        /// </summary>
        public (bool shouldEnter, float actualProfitPct, string reason) EvaluateLongEntry(
            List<IBinanceKline> futureCandles,
            decimal entryPrice)
        {
            if (futureCandles == null || futureCandles.Count < _config.EvaluationPeriodCandles)
                return (false, 0f, "insufficient_future_data");

            if (entryPrice <= 0)
                return (false, 0f, "invalid_entry_price");

            decimal targetPrice = entryPrice * (1 + _config.TargetProfitPct / 100m);
            decimal stopLossPrice = entryPrice * (1 + _config.StopLossPct / 100m);

            decimal highestPrice = 0m;
            decimal lowestPrice = decimal.MaxValue;
            bool targetHit = false;
            bool stopHit = false;
            int targetHitIndex = -1;
            int stopHitIndex = -1;

            for (int i = 0; i < Math.Min(futureCandles.Count, _config.EvaluationPeriodCandles); i++)
            {
                var candle = futureCandles[i];
                decimal high = candle.HighPrice;
                decimal low = candle.LowPrice;

                highestPrice = Math.Max(highestPrice, high);
                lowestPrice = Math.Min(lowestPrice, low);

                // 손절이 먼저 터졌는지 확인 (캔들 내 저점이 손절가 이하)
                if (!stopHit && low <= stopLossPrice)
                {
                    stopHit = true;
                    stopHitIndex = i;
                }

                // 목표가가 터졌는지 확인 (캔들 내 고점이 목표가 이상)
                if (!targetHit && high >= targetPrice)
                {
                    targetHit = true;
                    targetHitIndex = i;
                }

                // 손절이 목표가보다 먼저 터진 경우
                if (stopHit && (!targetHit || stopHitIndex < targetHitIndex))
                {
                    float lossP = (float)((stopLossPrice - entryPrice) / entryPrice * 100m);
                    return (false, lossP, $"stop_hit_first_at_candle_{stopHitIndex}");
                }

                // 목표가가 손절보다 먼저 터진 경우
                if (targetHit && (!stopHit || targetHitIndex < stopHitIndex))
                {
                    float profitPct = (float)((targetPrice - entryPrice) / entryPrice * 100m);
                    return (true, profitPct, $"target_hit_at_candle_{targetHitIndex}");
                }
            }

            // 평가 기간 내 목표/손절 모두 안 터진 경우 - 마지막 종가 기준 판단
            decimal finalPrice = futureCandles[Math.Min(futureCandles.Count - 1, _config.EvaluationPeriodCandles - 1)].ClosePrice;
            float finalProfitPct = (float)((finalPrice - entryPrice) / entryPrice * 100m);

            // 목표 수익의 절반 이상 달성 시 긍정으로 간주
            if (finalProfitPct >= (float)_config.TargetProfitPct / 2f)
            {
                return (true, finalProfitPct, $"partial_profit_{finalProfitPct:F2}%");
            }

            // 손절 기준 절반 이상 손실 시 부정
            if (finalProfitPct <= (float)_config.StopLossPct / 2f)
            {
                return (false, finalProfitPct, $"partial_loss_{finalProfitPct:F2}%");
            }

            // 중립 구간은 보수적으로 부정 처리 (노이즈 방지)
            return (false, finalProfitPct, $"neutral_{finalProfitPct:F2}%");
        }

        /// <summary>
        /// 특정 시점(현재 캔들)에서 SHORT 진입했을 때 수익이 났는지 판단
        /// </summary>
        public (bool shouldEnter, float actualProfitPct, string reason) EvaluateShortEntry(
            List<IBinanceKline> futureCandles,
            decimal entryPrice)
        {
            if (futureCandles == null || futureCandles.Count < _config.EvaluationPeriodCandles)
                return (false, 0f, "insufficient_future_data");

            if (entryPrice <= 0)
                return (false, 0f, "invalid_entry_price");

            // SHORT는 가격이 내려가야 수익
            decimal targetPrice = entryPrice * (1 - _config.TargetProfitPct / 100m);
            decimal stopLossPrice = entryPrice * (1 + Math.Abs(_config.StopLossPct) / 100m);

            bool targetHit = false;
            bool stopHit = false;
            int targetHitIndex = -1;
            int stopHitIndex = -1;

            for (int i = 0; i < Math.Min(futureCandles.Count, _config.EvaluationPeriodCandles); i++)
            {
                var candle = futureCandles[i];
                decimal high = candle.HighPrice;
                decimal low = candle.LowPrice;

                // 손절 체크 (고점이 손절가 이상)
                if (!stopHit && high >= stopLossPrice)
                {
                    stopHit = true;
                    stopHitIndex = i;
                }

                // 목표가 체크 (저점이 목표가 이하)
                if (!targetHit && low <= targetPrice)
                {
                    targetHit = true;
                    targetHitIndex = i;
                }

                if (stopHit && (!targetHit || stopHitIndex < targetHitIndex))
                {
                    float lossPct = (float)((entryPrice - stopLossPrice) / entryPrice * 100m);
                    return (false, lossPct, $"stop_hit_first_at_candle_{stopHitIndex}");
                }

                if (targetHit && (!stopHit || targetHitIndex < stopHitIndex))
                {
                    float profitPct = (float)((entryPrice - targetPrice) / entryPrice * 100m);
                    return (true, profitPct, $"target_hit_at_candle_{targetHitIndex}");
                }
            }

            decimal finalPrice = futureCandles[Math.Min(futureCandles.Count - 1, _config.EvaluationPeriodCandles - 1)].ClosePrice;
            float finalProfitPct = (float)((entryPrice - finalPrice) / entryPrice * 100m);

            if (finalProfitPct >= (float)_config.TargetProfitPct / 2f)
            {
                return (true, finalProfitPct, $"partial_profit_{finalProfitPct:F2}%");
            }

            if (finalProfitPct <= (float)Math.Abs(_config.StopLossPct) / 2f * -1f)
            {
                return (false, finalProfitPct, $"partial_loss_{finalProfitPct:F2}%");
            }

            return (false, finalProfitPct, $"neutral_{finalProfitPct:F2}%");
        }

        /// <summary>
        /// 진입 시점의 리스크/리워드 비율 계산
        /// </summary>
        public decimal CalculateRiskRewardRatio(decimal entryPrice, decimal targetPrice, decimal stopLossPrice, bool isLong)
        {
            if (isLong)
            {
                decimal reward = targetPrice - entryPrice;
                decimal risk = entryPrice - stopLossPrice;
                return risk > 0 ? reward / risk : 0m;
            }
            else
            {
                decimal reward = entryPrice - targetPrice;
                decimal risk = stopLossPrice - entryPrice;
                return risk > 0 ? reward / risk : 0m;
            }
        }

        /// <summary>
        /// 진입 조건 추가 필터링 (리스크/리워드, 변동성 등)
        /// </summary>
        public bool PassesEntryFilter(
            decimal entryPrice,
            decimal targetPrice,
            decimal stopLossPrice,
            float atr,
            bool isLong)
        {
            // 1. 리스크/리워드 비율 체크
            decimal rrRatio = CalculateRiskRewardRatio(entryPrice, targetPrice, stopLossPrice, isLong);
            if (rrRatio < _config.MinRiskRewardRatio)
                return false;

            // 2. ATR 대비 목표가 거리 체크 (너무 가까우면 제외)
            decimal targetDistance = Math.Abs(targetPrice - entryPrice);
            if (atr > 0 && targetDistance < (decimal)atr * 1.5m)
                return false;

            return true;
        }

        /// <summary>
        /// [Time-to-Target 회귀] 현재 시점부터 피보나치 0.618 목표가 도달까지의 캔들 개수 계산
        /// Transformer 네비게이터용 라벨링: "몇 개의 캔들 후 진입 타점에 도달하는가?"
        /// </summary>
        /// <param name="historicalCandles">현재 캔들 이전의 과거 캔들 (최소 24개 권장)</param>
        /// <param name="currentPrice">현재 가격</param>
        /// <param name="futureCandles">현재 캔들 이후의 미래 캔들 (최대 32개)</param>
        /// <param name="maxLookAhead">최대 탐색 범위 (기본 32캔들 = 8시간)</param>
        /// <param name="tolerancePct">목표가 도달 판정 허용 오차 (기본 ±0.5%)</param>
        /// <returns>목표가 도달까지의 캔들 개수, -1이면 범위 내 미도달</returns>
        public float CalculateCandlesToFibonacciTarget(
            List<IBinanceKline> historicalCandles,
            decimal currentPrice,
            List<IBinanceKline> futureCandles,
            int maxLookAhead = 32,
            float tolerancePct = 0.5f)
        {
            if (historicalCandles == null || historicalCandles.Count < 10)
                return -1f; // 과거 데이터 부족

            if (futureCandles == null || futureCandles.Count == 0)
                return -1f; // 미래 데이터 없음

            // 1. 최근 고점 탐색 (24시간 = 96개 캔들, 또는 가능한 모두)
            int lookBackPeriod = Math.Min(96, historicalCandles.Count);
            decimal recentHighPrice = historicalCandles
                .TakeLast(lookBackPeriod)
                .Max(k => k.HighPrice);

            // 2. 피보나치 0.618 되돌림 목표가 계산
            // 목표가 = 고점 - (고점 - 현재가) * 0.618
            decimal priceRange = recentHighPrice - currentPrice;
            if (priceRange <= 0)
                return -1f; // 상승 중이거나 고점이 현재가보다 낮음 → 하락 되돌림 시나리오 아님

            decimal fibTarget = recentHighPrice - (priceRange * 0.618m);

            // 3. 허용 범위 계산 (목표가 ±0.5%)
            decimal tolerance = fibTarget * (decimal)(tolerancePct / 100f);
            decimal lowerBound = fibTarget - tolerance;
            decimal upperBound = fibTarget + tolerance;

            // 4. 미래 캔들에서 목표가 도달 시점 탐색
            int searchLimit = Math.Min(maxLookAhead, futureCandles.Count);
            for (int i = 0; i < searchLimit; i++)
            {
                var candle = futureCandles[i];
                // 종가가 목표 범위 내에 진입했는가?
                if (candle.ClosePrice >= lowerBound && candle.ClosePrice <= upperBound)
                {
                    return (float)(i + 1); // 1-based 인덱스 (1캔들 후 = 15분 후)
                }
                // 또는 캔들이 목표 범위를 관통했는가? (저가 < 목표 < 고가)
                if (candle.LowPrice <= fibTarget && candle.HighPrice >= fibTarget)
                {
                    return (float)(i + 1);
                }
            }

            // 5. 범위 내 미도달
            return -1f;
        }
    }
}
