using System;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class DbManagerTests
    {
        private const string TestConnectionString = "Server=localhost;Database=TestTradingDB;Integrated Security=true;TrustServerCertificate=True";

        [Fact(Skip = "Requires database connection")]
        public async Task SaveTradeLogAsync_SavesSuccessfully()
        {
            // Arrange
            var dbManager = new DbManager(TestConnectionString);
            var tradeLog = new TradeLog
            {
                Symbol = "BTCUSDT",
                Side = "LONG",
                Strategy = "PumpScan",
                Price = 50000,
                AiScore = 0.85,
                Time = DateTime.Now,
                PnL = 100,
                PnLPercent = 5.0m
            };

            // Act & Assert
            await dbManager.SaveTradeLogAsync(tradeLog);
            // 실제 DB 확인 필요
        }

        [Fact(Skip = "Requires database connection")]
        public async Task GetTradeLogsAsync_ReturnsData()
        {
            // Arrange
            var dbManager = new DbManager(TestConnectionString);

            // Act
            var logs = await dbManager.GetTradeLogsAsync(limit: 10);

            // Assert
            Assert.NotNull(logs);
        }

        [Fact]
        public void Constructor_InitializesWithConnectionString()
        {
            // Arrange & Act
            var dbManager = new DbManager(TestConnectionString);

            // Assert
            Assert.NotNull(dbManager);
        }
    }
}
