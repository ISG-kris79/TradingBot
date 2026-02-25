using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Services.BacktestStrategies
{
    public class RsiBacktestStrategy : IBacktestStrategy
    {
        public string Name => "RSI Strategy";

        // [추가] 최적화를 위한 파라미터
        public int RsiPeriod { get; set; } = 14;
        public double BuyThreshold { get; set; } = 30;
        public double SellThreshold { get; set; } = 70;

        public void Execute(List<CandleData> candles, BacktestResult result)
        {
            decimal currentBalance = result.InitialBalance;
            decimal positionQuantity = 0;
            bool inPosition = false;

            // RSI 시리즈 계산 (Period가 14가 아닐 경우를 대비하거나 일관성을 위해)
            List<double> rsiValues;
            if (RsiPeriod == 14)
            {
                rsiValues = candles.Select(c => (double)c.RSI).ToList();
            }
            else
            {
                var prices = candles.Select(c => (double)c.Close).ToList();
                rsiValues = IndicatorCalculator.CalculateRSISeries(prices, RsiPeriod);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal currentPrice = (decimal)candle.Close;
                double currentRsi = rsiValues[i];

                // [매수] RSI BuyThreshold 미만
                if (!inPosition && currentRsi < BuyThreshold && currentRsi > 0)
                {
                    decimal amountToInvest = currentBalance * 0.95m;
                    positionQuantity = amountToInvest / currentPrice;
                    currentBalance -= amountToInvest;

                    inPosition = true;
                    result.TradeHistory.Add(new TradeLog(result.Symbol, "BUY", "Backtest_RSI", currentPrice, 0, candle.OpenTime));
                }
                // [매도] RSI SellThreshold 초과
                else if (inPosition && currentRsi > SellThreshold)
                {
                    decimal revenue = positionQuantity * currentPrice;
                    decimal profit = revenue - (positionQuantity * (decimal)result.TradeHistory.Last().Price);

                    currentBalance += revenue;
                    result.TotalTrades++;
                    if (profit > 0) result.WinCount++; else result.LossCount++;

                    result.TradeHistory.Add(new TradeLog(result.Symbol, "SELL", "Backtest_RSI", currentPrice, 0, candle.OpenTime));

                    // 수익 곡선 기록
                    result.EquityCurve.Add(currentBalance);
                    result.TradeDates.Add(candle.OpenTime.ToString("MM/dd HH:mm"));

                    inPosition = false;
                    positionQuantity = 0;
                }
            }
            result.FinalBalance = currentBalance + (inPosition ? positionQuantity * (decimal)candles.Last().Close : 0);
        }
    }
}