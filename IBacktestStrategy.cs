using TradingBot.Models;

namespace TradingBot.Services.BacktestStrategies
{
    public interface IBacktestStrategy
    {
        string Name { get; }
        void Execute(List<CandleData> candles, BacktestResult result);
    }
}