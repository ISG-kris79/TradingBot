using Dapper;
using Microsoft.Data.SqlClient;
using TradingBot.Shared.Models;
using TradingBot.Shared.Services;
using System.Text.Json;

namespace TradingBot.Web.Services
{
    public class DashboardService
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public DashboardService(IConfiguration configuration)
        {
            _configuration = configuration;
            var encryptedConn = _configuration.GetConnectionString("DefaultConnection");
            var isEncrypted = _configuration.GetValue<bool>("ConnectionStrings:IsEncrypted");

            if (isEncrypted && !string.IsNullOrEmpty(encryptedConn))
            {
                _connectionString = SecurityService.DecryptString(encryptedConn);
            }
            else
            {
                _connectionString = encryptedConn ?? "";
            }
        }

        public async Task<List<TradeLog>> GetRecentTradeLogsAsync(int count = 50)
        {
            using var db = new SqlConnection(_connectionString);
            string sql = "SELECT TOP (@Count) * FROM TradeLogs ORDER BY Time DESC";
            var logs = await db.QueryAsync<TradeLog>(sql, new { Count = count });
            return logs.ToList();
        }

        public async Task<decimal> GetTotalPnLAsync()
        {
            using var db = new SqlConnection(_connectionString);
            string sql = "SELECT SUM(PnL) FROM TradeLogs";
            return await db.ExecuteScalarAsync<decimal>(sql);
        }

        public async Task<List<CandleModel>> GetCandleDataAsync(string symbol, int count = 100)
        {
            using var db = new SqlConnection(_connectionString);
            string sql = "SELECT TOP (@Count) * FROM MarketCandles WHERE Symbol = @Symbol ORDER BY OpenTime DESC";
            var candles = await db.QueryAsync<CandleModel>(sql, new { Symbol = symbol, Count = count });
            return candles.OrderBy(c => c.OpenTime).ToList();
        }

        public async Task<List<string>> GetAvailableSymbolsAsync()
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                string sql = "SELECT DISTINCT Symbol FROM MarketCandles";
                var symbols = await db.QueryAsync<string>(sql);
                return symbols.ToList();
            }
            catch
            {
                return new List<string> { "BTCUSDT" };
            }
        }

        public async Task<List<PositionInfo>> GetActivePositionsAsync()
        {
            try
            {
                using var client = new HttpClient();
                var json = await client.GetStringAsync("http://localhost:8080/positions");
                return JsonSerializer.Deserialize<List<PositionInfo>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<PositionInfo>();
            }
            catch
            {
                return new List<PositionInfo>();
            }
        }

        public async Task<bool> ClosePositionAsync(string symbol)
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.PostAsync($"http://localhost:8080/close?symbol={symbol}", null);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
