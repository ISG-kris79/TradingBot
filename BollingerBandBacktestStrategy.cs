using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services.BacktestStrategies
{
    public class BollingerBandBacktestStrategy : IBacktestStrategy
    {
        public string Name => "Bollinger Band Strategy";
        public int Period { get; set; } = 20;
        public double Multiplier { get; set; } = 2.0;

        public void Execute(List<CandleData> candles, BacktestResult result)
        {
            decimal currentBalance = result.InitialBalance;
            decimal positionQuantity = 0;
            bool inPosition = false;

            var closes = candles.Select(c => (double)c.Close).ToArray();
            var uppers = new double[candles.Count];
            var lowers = new double[candles.Count];

            // 볼린저 밴드 계산 (SMA 20, StdDev 2)
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) continue;

                double sum = 0;
                for (int j = 0; j < Period; j++) sum += closes[i - j];
                double sma = sum / Period;

                double sumSq = 0;
                for (int j = 0; j < Period; j++) sumSq += Math.Pow(closes[i - j] - sma, 2);
                double stdDev = Math.Sqrt(sumSq / Period);

                uppers[i] = sma + (Multiplier * stdDev);
                lowers[i] = sma - (Multiplier * stdDev);
            }

            for (int i = Period; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal currentPrice = (decimal)candle.Close;
                double lower = lowers[i];
                double upper = uppers[i];

                // [매수] 가격이 하단 밴드보다 낮아질 때 (과매도 -> 반등 기대)
                if (!inPosition && (double)currentPrice < lower)
                {
                    decimal amountToInvest = currentBalance * 0.95m;
                    positionQuantity = amountToInvest / currentPrice;
                    currentBalance -= amountToInvest;

                    inPosition = true;
                    result.TradeHistory.Add(new TradeLog(result.Symbol, "BUY", "Backtest_BB", currentPrice, 0, candle.OpenTime));
                }
                // [매도] 가격이 상단 밴드보다 높아질 때 (과매수 -> 조정 기대)
                else if (inPosition && (double)currentPrice > upper)
                {
                    decimal revenue = positionQuantity * currentPrice;
                    decimal profit = revenue - (positionQuantity * (decimal)result.TradeHistory.Last().Price);

                    currentBalance += revenue;
                    result.TotalTrades++;
                    if (profit > 0) result.WinCount++; else result.LossCount++;

                    result.TradeHistory.Add(new TradeLog(result.Symbol, "SELL", "Backtest_BB", currentPrice, 0, candle.OpenTime));
                    
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