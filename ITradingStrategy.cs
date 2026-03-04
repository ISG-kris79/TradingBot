using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Strategies
{
    public interface ITradingStrategy
    {
        Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token);
        event Action<MultiTimeframeViewModel> OnSignalAnalyzed;
    }
}