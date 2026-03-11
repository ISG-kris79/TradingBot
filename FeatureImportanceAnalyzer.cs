using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services.AI
{
    /// <summary>
    /// 학습 데이터의 피처와 타겟 간의 상관관계를 분석하여 피처의 유효성을 검증합니다.
    /// </summary>
    public static class FeatureImportanceAnalyzer
    {
        public static readonly string[] FeatureNames = new[]
        {
            "Open", "High", "Low", "Close", "Volume", "RSI", "BB_Upper", "BB_Lower",
            "MACD", "MACD_Signal", "MACD_Hist", "ATR", "Fib_236", "Fib_382", "Fib_500", "Fib_618",
            "Sentiment", "ElliottWave", "SMA_20", "SMA_60", "SMA_120", "ADX", "PlusDI", "MinusDI",
            "Stoch_K", "Stoch_D", "Price_Change_Pct", "Volume_Ratio", "Hour_Sin", "Hour_Cos", "Day_Sin", "Day_Cos"
        };

        public static Dictionary<string, double> AnalyzeCorrelation(List<CandleData> data)
        {
            var results = new Dictionary<string, double>();
            if (data.Count < 2) return results;

            // 타겟 생성: 다음 봉의 가격 변화율 (Close to Close)
            var targets = new List<double>();
            for (int i = 0; i < data.Count - 1; i++)
            {
                targets.Add((double)((data[i + 1].Close - data[i].Close) / data[i].Close));
            }

            for (int f = 0; f < FeatureNames.Length; f++)
            {
                var featureValues = new List<double>();
                for (int i = 0; i < data.Count - 1; i++)
                {
                    /* TensorFlow 전환 중 비활성화
                    var vector = TransformerFeatureMapper.CreateFeatureVector(data[i], FeatureNames.Length);
                    featureValues.Add(vector[f]);
                    */
                    // 임시 폴백: 0.0으로 채우기
                    featureValues.Add(0.0);
                }

                double correlation = CalculateCorrelation(featureValues, targets);
                results[FeatureNames[f]] = correlation;
            }

            return results.OrderByDescending(x => Math.Abs(x.Value)).ToDictionary(x => x.Key, x => x.Value);
        }

        private static double CalculateCorrelation(List<double> x, List<double> y)
        {
            if (x.Count != y.Count) return 0;
            int n = x.Count;
            double avgX = x.Average();
            double avgY = y.Average();

            double sumXY = 0, sumX2 = 0, sumY2 = 0;
            for (int i = 0; i < n; i++)
            {
                double diffX = x[i] - avgX;
                double diffY = y[i] - avgY;
                sumXY += diffX * diffY;
                sumX2 += diffX * diffX;
                sumY2 += diffY * diffY;
            }

            double denominator = Math.Sqrt(sumX2 * sumY2);
            return denominator == 0 ? 0 : sumXY / denominator;
        }

        public static string GetAnalysisReport(Dictionary<string, double> correlations)
        {
            var report = "📊 [AI 피처 상관관계 분석 보고서]\n";
            foreach (var kvp in correlations.Take(10)) // 상위 10개 출력
                report += $"  - {kvp.Key}: {kvp.Value:F4}\n";
            return report;
        }
    }
}
