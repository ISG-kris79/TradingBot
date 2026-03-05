using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Shared.Models;
using TradeLog = TradingBot.Shared.Models.TradeLog;
using CandleData = TradingBot.Models.CandleData;

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

            result.EquityCurve.Clear();
            result.TradeDates.Clear();

            if (candles.Count > 0)
            {
                result.EquityCurve.Add(currentBalance);
                result.TradeDates.Add(candles[0].OpenTime.ToString("MM/dd HH:mm"));
            }

            // RSI는 DB 저장값이 0인 경우가 있어, 항상 종가 기반으로 재계산
            var prices = candles.Select(c => (double)c.Close).ToList();
            var rsiValues = IndicatorCalculator.CalculateRSISeries(prices, RsiPeriod);

            for (int i = 0; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal currentPrice = (decimal)candle.Close;
                double currentRsi = rsiValues[i];

                if (currentPrice <= 0 || double.IsNaN(currentRsi) || double.IsInfinity(currentRsi))
                {
                    decimal markToMarketInvalid = currentBalance + (inPosition ? positionQuantity * currentPrice : 0m);
                    result.EquityCurve.Add(markToMarketInvalid);
                    result.TradeDates.Add(candle.OpenTime.ToString("MM/dd HH:mm"));
                    continue;
                }

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

                    inPosition = false;
                    positionQuantity = 0;
                }

                decimal markToMarketEquity = currentBalance + (inPosition ? positionQuantity * currentPrice : 0m);
                result.EquityCurve.Add(markToMarketEquity);
                result.TradeDates.Add(candle.OpenTime.ToString("MM/dd HH:mm"));
            }
            result.FinalBalance = currentBalance + (inPosition ? positionQuantity * (decimal)candles.Last().Close : 0);
        }
    }
}
