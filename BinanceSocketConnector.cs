using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using TradingBot.Models;

namespace TradingBot
{
    public class BinanceSocketConnector : IDisposable
    {
        private BinanceSocketClient _socketClient;
        private bool _disposed = false;

        public BinanceSocketConnector()
        {
            _socketClient = new BinanceSocketClient(options =>
            {
                if (!string.IsNullOrEmpty(AppConfig.BinanceApiKey) && !string.IsNullOrEmpty(AppConfig.BinanceApiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                }
            });
        }

        public async void SubscribeTicker(string symbol)
        {
            // 바이낸스 선물(Futures) 실시간 가격 구독 예시
            var subscribeResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTickerUpdatesAsync(symbol, data =>
            {
                // UI 업데이트를 위해 메인 윈도우 인스턴스에 전달
                MainWindow.Instance?.Dispatcher.Invoke(() =>
                {
                    var viewModel = new MultiTimeframeViewModel
                    {
                        Symbol = data.Data.Symbol,
                        LastPrice = data.Data.LastPrice
                    };
                    MainWindow.Instance.RefreshSignalUI(viewModel);
                });
            });
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
}
