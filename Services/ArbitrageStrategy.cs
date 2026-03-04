using Binance.Net.Interfaces.Clients;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Strategies
{
    public class ArbitrageStrategy
    {
        private readonly IBinanceRestClient _client;
        private readonly HttpClient _httpClient;
        private const decimal ThresholdPercent = 1.5m; // 1.5% 차이 발생 시 알림

        public ArbitrageStrategy(IBinanceRestClient client)
        {
            _client = client;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        public async Task AnalyzeAsync(string symbol, decimal binancePrice, CancellationToken token)
        {
            // 1. 타 거래소(Bitget) 가격 조회 (REST API)
            decimal otherExchangePrice = await GetBitgetPriceAsync(symbol, token);
            if (otherExchangePrice == 0) return;

            decimal diff = Math.Abs(binancePrice - otherExchangePrice);
            decimal diffPercent = (diff / binancePrice) * 100;

            if (diffPercent >= ThresholdPercent)
            {
                // 알림 발생 로직 (Event 호출 등)
                System.Diagnostics.Debug.WriteLine($"[Arbitrage] {symbol} 가격 차이 발생! {diffPercent:F2}%");
            }
        }

        private async Task<decimal> GetBitgetPriceAsync(string symbol, CancellationToken token)
        {
            try
            {
                // Bitget 심볼 포맷 변환 (BTCUSDT -> BTCUSDT_UMCBL) - 선물 기준
                string bitgetSymbol = symbol.EndsWith("USDT") ? symbol + "_UMCBL" : symbol;
                
                // Bitget V1 Market Ticker API
                string url = $"https://api.bitget.com/api/mix/v1/market/ticker?symbol={bitgetSymbol}";
                var response = await _httpClient.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) return 0;

                var content = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("last", out var last))
                {
                    return decimal.Parse(last.GetString() ?? "0");
                }
                return 0;
            }
            catch { return 0; }
        }
    }
}