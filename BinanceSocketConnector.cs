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

        public void SubscribeTicker(string symbol)
        {
            _ = SubscribeTickerAsync(symbol);
        }

        public async Task SubscribeTickerAsync(string symbol)
        {
            try
            {
                // 바이낸스 선물(Futures) 실시간 가격 구독 예시
                var subscribeResult = await _socketClient.UsdFuturesApi.ExchangeData.SubscribeToTickerUpdatesAsync(symbol, data =>
                {
                    // RefreshSignalUI는 내부 큐를 사용하므로 UI 스레드 동기 점유 없이 전달
                    MainWindow.Instance?.RefreshSignalUI(
                        new MultiTimeframeViewModel
                    {
                        Symbol = data.Data.Symbol,
                        LastPrice = data.Data.LastPrice
                    });
                });

                if (!subscribeResult.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinanceSocketConnector] Ticker 구독 실패: {subscribeResult.Error}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinanceSocketConnector] SubscribeTickerAsync 오류: {ex.Message}");
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
}
