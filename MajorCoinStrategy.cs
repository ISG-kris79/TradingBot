using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    public class MajorCoinStrategy : ITradingStrategy
    {
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
            double sma60 = IndicatorCalculator.CalculateSMA(list, 60);
            double sma120 = IndicatorCalculator.CalculateSMA(list, 120);
            string maState = sma20 > sma60 && sma60 > sma120 ? "BULL" : (sma20 < sma60 && sma60 < sma120 ? "BEAR" : "MIX");
            string fibPos = currentPrice >= (decimal)fib.Level382 ? "ABOVE382" : (currentPrice <= (decimal)fib.Level618 ? "BELOW618" : "MID");

            var recent20 = list.TakeLast(20).ToList();
            double avgVolume20 = recent20.Any() ? recent20.Average(k => (double)k.Volume) : 0;
            double currentVolume = recent20.LastOrDefault() != null ? (double)recent20.Last().Volume : 0;
            double volumeRatio = avgVolume20 > 0 ? currentVolume / avgVolume20 : 1;

            int aiScore = CalculateScore(
                rsi,
                bb,
                currentPrice,
                isUptrend,
                macd,
                fib,
                sma20,
                sma60,
                sma120,
                volumeRatio);

            string decision = "WAIT";
            if (aiScore >= 65)
            {
                bool longConfirm = isUptrend && macd.Hist >= -0.001 && volumeRatio >= 0.95;
                if (longConfirm) decision = "LONG";
            }
            else if (aiScore <= 25)
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
                VolumeRatioValue = volumeRatio,
                VolumeRatio = $"{volumeRatio:F2}x",
                BBPosition = currentPrice >= (decimal)bb.Upper ? "Upper" : (currentPrice <= (decimal)bb.Lower ? "Lower" : "Mid")
            });

            // 로그 출력 (항상)
            string logMsg = $"[MAJOR] {symbol} | Price: ${currentPrice:F2} | RSI: {rsi:F1} | Score: {aiScore} | MA: {maState} | Fib: {fibPos} | Vol: {volumeRatio:F2}x | Decision: {decision}";
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
            double sma60,
            double sma120,
            double volumeRatio)
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

            // 거래량
            if (volumeRatio >= 1.10) score += 6;
            if (volumeRatio < 0.75) score -= 6;

            return Math.Clamp(score, 0, 100);
        }
    }
}
