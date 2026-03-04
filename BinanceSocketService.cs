// Binance.Net 라이브러리 활용
using Binance.Net.Clients;
using TradingBot;
using TradingBot.Models;

public class BinanceSocketService
{
    private BinanceSocketClient _socketClient = new BinanceSocketClient();

    public async Task StartPriceStream(IEnumerable<string> symbols)
    {
        foreach (var symbol in symbols)
        {
            await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTickerUpdatesAsync(symbol, data =>
            {
                MainWindow.Instance?.Dispatcher.Invoke(() =>
                {
                    // 수신된 가격 데이터로 UI 업데이트
                    MainWindow.Instance.RefreshSignalUI(new MultiTimeframeViewModel
                    {
                        Symbol = data.Data.Symbol,
                        LastPrice = data.Data.LastPrice
                    });
                });
            });
        }
    }
}