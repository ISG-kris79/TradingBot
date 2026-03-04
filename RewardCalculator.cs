using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Services.AI.RL
{
    public static class RewardCalculator
    {
        /// <summary>
        /// 샤프 지수와 MDD를 반영한 보상 계산
        /// </summary>
        /// <param name="pnlPercent">현재 거래의 수익률 (%)</param>
        /// <param name="recentReturns">최근 N개 거래의 수익률 리스트</param>
        /// <param name="currentDrawdown">현재 낙폭 (%)</param>
        /// <returns>조정된 보상값</returns>
        public static float CalculateReward(float pnlPercent, List<float> recentReturns, float currentDrawdown)
        {
            // 1. 기본 보상: 수익률
            float reward = pnlPercent;

            // 2. 리스크 페널티 (MDD)
            // 낙폭이 클수록 페널티 부여 (예: MDD 5% -> -0.5점)
            if (currentDrawdown > 0)
            {
                reward -= (currentDrawdown * 0.1f);
            }

            // 3. 변동성 페널티 (Sharpe Ratio 개념 적용)
            if (recentReturns != null && recentReturns.Count > 1)
            {
                float avg = recentReturns.Average();
                float sumSq = recentReturns.Sum(x => (x - avg) * (x - avg));
                float stdDev = (float)Math.Sqrt(sumSq / (recentReturns.Count - 1));

                // 변동성이 너무 크면 보상 삭감 (안정적 수익 유도)
                if (stdDev > 0.05f) reward *= 0.8f; // 변동성 5% 초과 시 20% 삭감
            }

            return reward;
        }
    }
}