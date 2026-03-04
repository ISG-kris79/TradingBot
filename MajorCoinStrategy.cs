using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    public class MajorCoinStrategy : ITradingStrategy
    {
        private static readonly TimeZoneInfo SeoulTimeZone = GetSeoulTimeZone();
        private readonly MarketDataManager _marketData;

        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string, string, decimal>? OnTradeSignal; // symbol, decision, price
        public event Action<string>? OnLog;

        public MajorCoinStrategy(MarketDataManager marketData)
        {
            _marketData = marketData;
        }

#pragma warning disable CS1998 // 비동기 메서드에 await 연산자가 없습니다.
        public Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
#pragma warning restore CS1998
        {
            if (!_marketData.KlineCache.TryGetValue(symbol, out var cache)) return Task.CompletedTask;

            List<IBinanceKline> list;
            lock (cache)
            {
                list = cache.ToList(); // Thread-safe copy
            }

            if (list.Count < 120) return Task.CompletedTask;

            double rsi = IndicatorCalculator.CalculateRSI(list, 14);
            var bb = IndicatorCalculator.CalculateBB(list, 20, 2);
            bool isUptrend = IndicatorCalculator.AnalyzeElliottWave(list);
            var macd = IndicatorCalculator.CalculateMACD(list);
            var fib = IndicatorCalculator.CalculateFibonacci(list, 100);
            double sma20 = IndicatorCalculator.CalculateSMA(list, 20);
            double sma50 = IndicatorCalculator.CalculateSMA(list, 50);
            double sma60 = IndicatorCalculator.CalculateSMA(list, 60);
            double sma120 = IndicatorCalculator.CalculateSMA(list, 120);
            string maState = sma20 > sma60 && sma60 > sma120 ? "BULL" : (sma20 < sma60 && sma60 < sma120 ? "BEAR" : "MIX");
            string fibPos = currentPrice >= (decimal)fib.Level382 ? "ABOVE382" : (currentPrice <= (decimal)fib.Level618 ? "BELOW618" : "MID");

            var recent20 = list.TakeLast(20).ToList();
            double avgVolume20 = recent20.Any() ? recent20.Average(k => (double)k.Volume) : 0;
            double currentVolume = recent20.LastOrDefault() != null ? (double)recent20.Last().Volume : 0;
            double volumeRatio = avgVolume20 > 0 ? currentVolume / avgVolume20 : 1;

            var recent3 = list.TakeLast(3).ToList();
            var previous10 = list.Skip(Math.Max(0, list.Count - 13)).Take(10).ToList();
            double avgVolume3 = recent3.Any() ? recent3.Average(k => (double)k.Volume) : 0;
            double avgVolumePrev10 = previous10.Any() ? previous10.Average(k => (double)k.Volume) : 0;
            double volumeMomentum = avgVolumePrev10 > 0 ? avgVolume3 / avgVolumePrev10 : 1;

            bool isMakingHigherLows = IsMakingHigherLows(list);

            int aiScore = CalculateScore(
                rsi,
                bb,
                currentPrice,
                isUptrend,
                macd,
                fib,
                sma20,
                sma50,
                sma60,
                sma120,
                volumeMomentum,
                isMakingHigherLows);

            var nowSeoul = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SeoulTimeZone);
            bool isKstDaytime = nowSeoul.Hour >= 10 && nowSeoul.Hour < 19;
            int longThreshold = CalculateDynamicThreshold(isKstDaytime, volumeMomentum, isMakingHigherLows);
            int shortThreshold = isKstDaytime ? 30 : 25;

            string decision = "WAIT";
            if (aiScore >= longThreshold)
            {
                bool bullishStructure = isUptrend || (isMakingHigherLows && currentPrice > (decimal)sma20);
                bool longConfirm = bullishStructure && macd.Hist >= -0.001 && volumeMomentum >= 1.00;
                if (longConfirm) decision = "LONG";
            }
            else if (aiScore <= shortThreshold)
            {
                bool isStrongBearish =
                    !isUptrend &&
                    macd.Hist < 0 &&
                    currentPrice < (decimal)sma20 &&
                    volumeRatio >= 1.10 &&
                    currentPrice < (decimal)fib.Level618;

                if (isStrongBearish)
                {
                    decision = "SHORT";
                }
            }

            OnSignalAnalyzed?.Invoke(new MultiTimeframeViewModel
            {
                Symbol = symbol,
                LastPrice = currentPrice,
                RSI_1H = rsi,
                AIScore = aiScore,
                Decision = decision,
                StrategyName = "Major Scalping(5m)",
                SignalSource = "MAJOR",
                ShortLongScore = aiScore,
                ShortShortScore = 100 - aiScore,
                MacdHist = macd.Hist,
                ElliottTrend = isUptrend ? "UP" : "DOWN",
                MAState = maState,
                FibPosition = fibPos,
                VolumeRatioValue = volumeMomentum,
                VolumeRatio = $"{volumeMomentum:F2}x",
                BBPosition = currentPrice >= (decimal)bb.Upper ? "Upper" : (currentPrice <= (decimal)bb.Lower ? "Lower" : "Mid")
            });

            if (!isUptrend && isMakingHigherLows && currentPrice > (decimal)sma20)
            {
                OnLog?.Invoke("ℹ️ 횡보 중이나 저점 상승 확인 - 추세 점수 가산");
            }

            string profile = isKstDaytime ? "DAY" : "DEFAULT";
            string decisionKr = decision switch
            {
                "LONG" => "롱 진입",
                "SHORT" => "숏 진입",
                _ => "대기 중"
            };

            string reason = "";
            if (decision == "WAIT")
            {
                var reasons = new List<string>();
                if (volumeMomentum < 1.00) reasons.Add("거래량 부족");
                if (!isUptrend && !isMakingHigherLows) reasons.Add("2파 횡보장 인식");
                if (aiScore < longThreshold && aiScore > shortThreshold) reasons.Add("스코어 불충분");
                
                if (reasons.Any())
                    reason = $" (사유: {string.Join(" ", reasons)})";
            }

            string logMsg = $"{nowSeoul:HH:mm:ss} {symbol} ${currentPrice:F2} {decisionKr}{reason}";
            OnLog?.Invoke(logMsg);

            if (decision != "WAIT")
            {
                OnTradeSignal?.Invoke(symbol, decision, currentPrice);
            }

            return Task.CompletedTask;
        }

        private int CalculateScore(
            double rsi,
            BBResult bb,
            decimal currentPrice,
            bool isUptrend,
            (double Macd, double Signal, double Hist) macd,
            (double Level236, double Level382, double Level500, double Level618) fib,
            double sma20,
            double sma50,
            double sma60,
            double sma120,
            double volumeMomentum,
            bool isMakingHigherLows)
        {
            int score = 50;

            // 엘리엇 파동(추세 proxy)
            if (isUptrend) score += 12;
            else score -= 12;

            // 이동평균선 구조
            if (sma20 > sma60) score += 10;
            else score -= 10;

            if (sma60 > sma120) score += 8;
            else score -= 8;

            if (currentPrice > (decimal)sma20 && sma20 > sma50) score += 10;

            // RSI
            if (rsi >= 45 && rsi <= 68) score += 10;
            else if (rsi > 75) score -= 10;
            else if (rsi < 35) score -= 6;

            // MACD
            if (macd.Hist > 0) score += 10;
            else score -= 10;

            // 피보나치 되돌림
            if (currentPrice >= (decimal)fib.Level382) score += 6;
            if (currentPrice < (decimal)fib.Level618) score -= 10;

            double price = (double)currentPrice;
            if (price >= bb.Mid) score += 6;
            else score -= 6;

            if (price > bb.Upper && rsi > 72) score -= 8;
            else if (price < bb.Lower && rsi < 30) score += 6;

            // 거래량 (최근 3개 평균 / 이전 10개 평균)
            if (volumeMomentum >= 1.10) score += 10;
            else if (volumeMomentum >= 1.00) score += 5;

            if (isMakingHigherLows && currentPrice > (decimal)sma20) score += 15;

            return Math.Clamp(score, 0, 100);
        }

        private static int CalculateDynamicThreshold(bool isKstDaytime, double volumeMomentum, bool isMakingHigherLows)
        {
            int threshold = isKstDaytime ? 60 : 65;

            if (volumeMomentum >= 1.10) threshold -= 3;
            if (isMakingHigherLows) threshold -= 2;

            return Math.Max(55, threshold);
        }

        private static bool IsMakingHigherLows(List<IBinanceKline> candles)
        {
            const int segmentSize = 4;
            const int requiredCandles = segmentSize * 3;
            if (candles.Count < requiredCandles) return false;

            var window = candles.TakeLast(requiredCandles).ToList();

            decimal low1 = window.Take(segmentSize).Min(c => c.LowPrice);
            decimal low2 = window.Skip(segmentSize).Take(segmentSize).Min(c => c.LowPrice);
            decimal low3 = window.Skip(segmentSize * 2).Take(segmentSize).Min(c => c.LowPrice);

            return low2 > low1 && low3 > low2;
        }

        private static TimeZoneInfo GetSeoulTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
            }
        }
    }
}
