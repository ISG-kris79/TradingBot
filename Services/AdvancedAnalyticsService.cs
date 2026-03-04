using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class AdvancedAnalyticsService
    {
        /// <summary>
        /// 몬테카를로 시뮬레이션: 과거 매매 데이터를 무작위로 재배열하여 전략의 리스크와 기대 수익을 분석합니다.
        /// </summary>
        public MonteCarloResult RunMonteCarlo(List<decimal> tradePnLs, int iterations = 1000, decimal startingCapital = 10000)
        {
            var result = new MonteCarloResult();
            var random = new Random();

            for (int i = 0; i < iterations; i++)
            {
                decimal currentEquity = startingCapital;
                decimal maxEquity = startingCapital;
                decimal maxDrawdown = 0;

                // 매매 데이터를 무작위로 추출하여 시뮬레이션 (복원 추출)
                for (int j = 0; j < tradePnLs.Count; j++)
                {
                    int index = random.Next(tradePnLs.Count);
                    currentEquity += tradePnLs[index];

                    if (currentEquity > maxEquity) maxEquity = currentEquity;
                    decimal dd = (maxEquity - currentEquity) / maxEquity;
                    if (dd > maxDrawdown) maxDrawdown = dd;

                    if (currentEquity <= 0) // 파산
                    {
                        result.RuinCount++;
                        break;
                    }
                }

                result.FinalEquities.Add(currentEquity);
                result.MaxDrawdowns.Add(maxDrawdown);
            }

            result.AverageFinalEquity = result.FinalEquities.Average();
            result.MedianFinalEquity = result.FinalEquities.OrderBy(e => e).ElementAt(iterations / 2);
            result.AverageMaxDrawdown = result.MaxDrawdowns.Average();
            result.MaxDrawdown95 = result.MaxDrawdowns.OrderByDescending(d => d).ElementAt((int)(iterations * 0.05));
            result.RuinProbability = (double)result.RuinCount / iterations;

            return result;
        }

        /// <summary>
        /// 전진 분석(Walk-Forward Analysis): 데이터를 구간별로 나누어 학습(최적화)과 검증(테스트)을 반복합니다.
        /// </summary>
        public WalkForwardResult RunWalkForward(List<CandleData> data, int windowCount = 5, double trainRatio = 0.7)
        {
            var result = new WalkForwardResult();
            int totalCount = data.Count;
            int windowSize = totalCount / windowCount;

            for (int i = 0; i < windowCount; i++)
            {
                int startIdx = i * (totalCount / windowCount / 2); // 슬라이딩 윈도우 (겹침 허용)
                int endIdx = Math.Min(startIdx + windowSize, totalCount);
                
                if (endIdx - startIdx < 10) break;

                var windowData = data.GetRange(startIdx, endIdx - startIdx);
                int trainSize = (int)(windowData.Count * trainRatio);
                
                var trainData = windowData.Take(trainSize).ToList();
                var testData = windowData.Skip(trainSize).ToList();

                // 여기서 실제로는 여러 파라미터를 돌려보고 최적을 찾아 테스트 데이터에 적용하는 로직이 들어감
                // 현재는 프레임워크 구조만 생성
                result.Steps.Add(new WfaStep
                {
                    StepIndex = i,
                    TrainPeriod = $"{trainData.First().OpenTime} ~ {trainData.Last().OpenTime}",
                    TestPeriod = $"{testData.First().OpenTime} ~ {testData.Last().OpenTime}"
                });
            }

            return result;
        }
    }

    public class MonteCarloResult
    {
        public List<decimal> FinalEquities { get; set; } = new();
        public List<decimal> MaxDrawdowns { get; set; } = new();
        public decimal AverageFinalEquity { get; set; }
        public decimal MedianFinalEquity { get; set; }
        public decimal AverageMaxDrawdown { get; set; }
        public decimal MaxDrawdown95 { get; set; }
        public double RuinProbability { get; set; }
        public int RuinCount { get; set; }
    }

    public class WalkForwardResult
    {
        public List<WfaStep> Steps { get; set; } = new();
        public double EfficiencyRatio { get; set; } // (Out-of-sample CAGR) / (In-sample CAGR)
    }

    public class WfaStep
    {
        public int StepIndex { get; set; }
        public string? TrainPeriod { get; set; }
        public string? TestPeriod { get; set; }
        public decimal InSampleProfit { get; set; }
        public decimal OutOfSampleProfit { get; set; }
    }
}
