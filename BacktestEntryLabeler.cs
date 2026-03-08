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
    }
}
