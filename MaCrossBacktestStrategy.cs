using TradingBot.Models;
using TradingBot.Shared.Models;
using TradeLog = TradingBot.Shared.Models.TradeLog;
using CandleData = TradingBot.Models.CandleData;

namespace TradingBot.Services.BacktestStrategies
{
    public class MaCrossBacktestStrategy : IBacktestStrategy
    {
        public string Name => "MA Cross Strategy";

        public void Execute(List<CandleData> candles, BacktestResult result)
        {
            var closes = candles.Select(c => (double)c.Close).ToList();
            var smaShort = IndicatorCalculator.CalculateSMASeries(closes, 9);
            var smaLong = IndicatorCalculator.CalculateSMASeries(closes, 21);

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

            for (int i = 21; i < candles.Count; i++)
            {
                decimal currentPrice = (decimal)candles[i].Close;
                bool goldenCross = smaShort[i - 1] < smaLong[i - 1] && smaShort[i] > smaLong[i];
                bool deathCross = smaShort[i - 1] > smaLong[i - 1] && smaShort[i] < smaLong[i];

                if (!inPosition && goldenCross)
                {
                    decimal amountToInvest = currentBalance * 0.95m;
                    positionQuantity = amountToInvest / currentPrice;
                    currentBalance -= amountToInvest;

                    inPosition = true;
                    result.TradeHistory.Add(new TradeLog(result.Symbol, "BUY", "Backtest_MA", currentPrice, 0, candles[i].OpenTime));
                }
                else if (inPosition && deathCross)
                {
                    decimal revenue = positionQuantity * currentPrice;
                    decimal profit = revenue - (positionQuantity * (decimal)result.TradeHistory.Last().Price);

                    currentBalance += revenue;
                    result.TotalTrades++;
                    if (profit > 0) result.WinCount++; else result.LossCount++;

                    result.TradeHistory.Add(new TradeLog(result.Symbol, "SELL", "Backtest_MA", currentPrice, 0, candles[i].OpenTime));

                    inPosition = false;
                    positionQuantity = 0;
                }

                decimal markToMarketEquity = currentBalance + (inPosition ? positionQuantity * currentPrice : 0m);
                
                // 추가 안전장치: 음수나 0 방지
                if (markToMarketEquity <= 0)
                {
                    markToMarketEquity = result.EquityCurve.Count > 0 
                        ? result.EquityCurve[^1] 
                        : result.InitialBalance;
                }
                
                result.EquityCurve.Add(markToMarketEquity);
                result.TradeDates.Add(candles[i].OpenTime.ToString("MM/dd HH:mm"));
            }
            
            // FinalBalance 계산 시에도 검증
            decimal finalEquity = currentBalance + (inPosition ? positionQuantity * (decimal)candles.Last().Close : 0);
            if (finalEquity <= 0)
            {
                finalEquity = result.EquityCurve.LastOrDefault(result.InitialBalance);
            }
            result.FinalBalance = finalEquity;
        }
    }
}
