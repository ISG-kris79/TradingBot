// Binance.Net 라이브러리 활용
using Binance.Net.Clients;
using TradingBot;
using TradingBot.Models;

public class BinanceSocketService : IDisposable
{
    private BinanceSocketClient _socketClient = new BinanceSocketClient();
    private bool _disposed = false;

    public async Task StartPriceStream(IEnumerable<string> symbols)
    {
        foreach (var symbol in symbols)
        {
            await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTickerUpdatesAsync(symbol, data =>
            {
                // RefreshSignalUI는 내부 큐를 사용하므로 UI 스레드 동기 점유 없이 전달
                MainWindow.Instance?.RefreshSignalUI(new MultiTimeframeViewModel
                {
                    Symbol = data.Data.Symbol,
                    LastPrice = data.Data.LastPrice
                });
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            _socketClient?.UnsubscribeAllAsync().Wait(TimeSpan.FromSeconds(5));
            _socketClient?.Dispose();
        }
        catch { }
        finally
        {
            _disposed = true;
        }
        
        GC.SuppressFinalize(this);
    }
}
