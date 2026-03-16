using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot
{
    public class PatternResult
    {
        public bool IsDetected { get; set; }
        public string PatternType { get; set; } = string.Empty; // "H&S" or "InverseH&S"
        public decimal LeftShoulder { get; set; }
        public decimal Head { get; set; }
        public decimal RightShoulder { get; set; }
        public decimal Neckline { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public static class HeadAndShouldersDetector
    {
        public static PatternResult DetectPattern(List<IBinanceKline> candles, int lookbackPeriod = 70)
        {
            var result = new PatternResult { IsDetected = false };
            if (candles == null || candles.Count < lookbackPeriod) return result;

            var recentCandles = candles.TakeLast(lookbackPeriod).ToList();
            
            var peaks = FindPeaks(recentCandles, 4);
            var valleys = FindValleys(recentCandles, 4);

            // 1. 일반 헤드앤숄더 (하락 반전)
            if (peaks.Count >= 3 && valleys.Count >= 2)
            {
                var p1 = peaks[peaks.Count - 3]; // Left Shoulder
                var p2 = peaks[peaks.Count - 2]; // Head
                var p3 = peaks[peaks.Count - 1]; // Right Shoulder

                var v1 = valleys.Where(v => v.OpenTime > p1.OpenTime && v.OpenTime < p2.OpenTime).OrderBy(v => v.LowPrice).FirstOrDefault();
                var v2 = valleys.Where(v => v.OpenTime > p2.OpenTime && v.OpenTime < p3.OpenTime).OrderBy(v => v.LowPrice).FirstOrDefault();

                if (v1 != null && v2 != null)
                {
                    decimal neckLinePrice = Math.Min((decimal)v1.LowPrice, (decimal)v2.LowPrice);
                    
                    if (p2.HighPrice > p1.HighPrice && p2.HighPrice > p3.HighPrice)
                    {
                        var lastPrice = (decimal)recentCandles.Last().ClosePrice;
                        // 오른쪽 어깨 형성 후 넥라인 이탈 위험 구간
                        if (lastPrice <= neckLinePrice * 1.015m)
                        {
                            result.IsDetected = true;
                            result.PatternType = "H&S";
                            result.LeftShoulder = (decimal)p1.HighPrice;
                            result.Head = (decimal)p2.HighPrice;
                            result.RightShoulder = (decimal)p3.HighPrice;
                            result.Neckline = neckLinePrice;
                            result.Message = $"🚨 일반 헤드앤숄더 패턴 감지 (Neckline: {neckLinePrice:F2}) - 추세 하락 전환 위험";
                            return result;
                        }
                    }
                }
            }

            // 2. 역 헤드앤숄더 (상승 반전)
            if (valleys.Count >= 3 && peaks.Count >= 2)
            {
                var v1 = valleys[valleys.Count - 3]; // Left Shoulder
                var v2 = valleys[valleys.Count - 2]; // Head
                var v3 = valleys[valleys.Count - 1]; // Right Shoulder

                var p1 = peaks.Where(p => p.OpenTime > v1.OpenTime && p.OpenTime < v2.OpenTime).OrderByDescending(p => p.HighPrice).FirstOrDefault();
                var p2 = peaks.Where(p => p.OpenTime > v2.OpenTime && p.OpenTime < v3.OpenTime).OrderByDescending(p => p.HighPrice).FirstOrDefault();

                if (p1 != null && p2 != null)
                {
                    decimal neckLinePrice = Math.Max((decimal)p1.HighPrice, (decimal)p2.HighPrice);
                    
                    if (v2.LowPrice < v1.LowPrice && v2.LowPrice < v3.LowPrice)
                    {
                        var lastPrice = (decimal)recentCandles.Last().ClosePrice;
                        // 역헤숄 완성 후 넥라인 돌파
                        if (lastPrice >= neckLinePrice * 0.985m)
                        {
                            result.IsDetected = true;
                            result.PatternType = "InverseH&S";
                            result.LeftShoulder = (decimal)v1.LowPrice;
                            result.Head = (decimal)v2.LowPrice;
                            result.RightShoulder = (decimal)v3.LowPrice;
                            result.Neckline = neckLinePrice;
                            result.Message = $"🔥 역 헤드앤숄더 패턴 감지 (Neckline: {neckLinePrice:F2}) - 강력한 상승 전환 시그널";
                            return result;
                        }
                    }
                }
            }

            return result;
        }

        private static List<IBinanceKline> FindPeaks(List<IBinanceKline> candles, int surround)
        {
            var peaks = new List<IBinanceKline>();
            for (int i = surround; i < candles.Count - surround; i++)
            {
                bool isPeak = true;
                for (int j = i - surround; j <= i + surround; j++)
                {
                    if (i != j && candles[j].HighPrice > candles[i].HighPrice)
                    {
                        isPeak = false; break;
                    }
                }
                if (isPeak) peaks.Add(candles[i]);
            }
            return peaks;
        }

        private static List<IBinanceKline> FindValleys(List<IBinanceKline> candles, int surround)
        {
            var valleys = new List<IBinanceKline>();
            for (int i = surround; i < candles.Count - surround; i++)
            {
                bool isValley = true;
                for (int j = i - surround; j <= i + surround; j++)
                {
                    if (i != j && candles[j].LowPrice < candles[i].LowPrice)
                    {
                        isValley = false; break;
                    }
                }
                if (isValley) valleys.Add(candles[i]);
            }
            return valleys;
        }
    }
}
