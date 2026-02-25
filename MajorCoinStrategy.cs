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

        public MajorCoinStrategy(MarketDataManager marketData)
        {
            _marketData = marketData;
        }

        public async Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            if (!_marketData.KlineCache.TryGetValue(symbol, out var cache)) return;

            List<IBinanceKline> list;
            lock (cache)
            {
                list = cache.ToList(); // Thread-safe copy
            }

            if (list.Count < 20) return;

            double rsi = IndicatorCalculator.CalculateRSI(list, 14);
            var bb = IndicatorCalculator.CalculateBB(list, 20, 2);
            bool isUptrend = IndicatorCalculator.AnalyzeElliottWave(list);
            var stoch = IndicatorCalculator.CalculateStochastic(list, 14, 3, 3);

            int aiScore = CalculateScore(rsi, bb, currentPrice, isUptrend, stoch);

            string decision = "WAIT";
            if (aiScore >= 80) decision = "LONG";
            else if (aiScore <= 20) decision = "SHORT";

            OnSignalAnalyzed?.Invoke(new MultiTimeframeViewModel
            {
                Symbol = symbol,
                LastPrice = currentPrice,
                AIScore = aiScore,
                Decision = decision,
                StrategyName = "Major Scalp"
            });

            if (decision != "WAIT")
            {
                OnTradeSignal?.Invoke(symbol, decision, currentPrice);
            }
        }

        private int CalculateScore(double rsi, BBResult bb, decimal currentPrice, bool isUptrend, (double K, double D) stoch)
        {
            int score = 50;
            if (isUptrend) score += 20; else score -= 10;
            if (rsi >= 70) score += 15;
            else if (rsi <= 30) score -= 20;

            // [추가] 스토캐스틱 조건 반영
            // 과매도 구간에서 골든크로스 발생 시 강력 매수 신호
            if (stoch.K <= 20 && stoch.K > stoch.D) score += 20;
            // 과매수 구간에서 데드크로스 발생 시 매도 신호
            else if (stoch.K >= 80 && stoch.K < stoch.D) score -= 20;

            double price = (double)currentPrice;
            if (price >= bb.Upper) score += 15;
            else if (price <= bb.Lower) score -= 30;

            return Math.Clamp(score, 0, 100);
        }
    }
}