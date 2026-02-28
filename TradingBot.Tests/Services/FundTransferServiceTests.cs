using Moq;
using TradingBot.Services;
using TradingBot.Shared.Models;
using Xunit;

namespace TradingBot.Tests.Services
{
    /// <summary>
    /// FundTransferService 단위 테스트
    /// Phase 14: 자금 이동 서비스 SimulationMode 및 안전 가드 검증
    /// </summary>
    public class FundTransferServiceTests
    {
        private readonly Mock<IExchangeService> _mockBinance;
        private readonly Mock<IExchangeService> _mockBybit;
        private readonly Mock<DbManager> _mockDbManager;
        private readonly Mock<TelegramService> _mockTelegram;

        public FundTransferServiceTests()
        {
            _mockBinance = new Mock<IExchangeService>();
            _mockBybit = new Mock<IExchangeService>();
            _mockDbManager = new Mock<DbManager>();
            _mockTelegram = new Mock<TelegramService>();
        }

        [Fact]
        public void Constructor_InitializesCorrectly()
        {
            // Arrange
            var settings = new FundTransferSettings
            {
                MinTransferAmount = 100m,
                SimulationMode = true
            };

            // Act
            var service = new FundTransferService(settings);

            // Assert
            Assert.NotNull(service);
        }

        [Fact]
        public void AddExchange_AddsExchangeSuccessfully()
        {
            // Arrange
            var settings = new FundTransferSettings { SimulationMode = true };
            var service = new FundTransferService(settings);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);

            // Assert
            Assert.Single(logMessages);
            Assert.Contains("Binance", logMessages[0]);
        }

        [Fact]
        public async Task StartMonitoringAsync_InSimulationMode_CompletesSuccessfully()
        {
            // Arrange
            var settings = new FundTransferSettings
            {
                CheckIntervalMinutes = 1,
                SimulationMode = true
            };
            var service = new FundTransferService(settings);
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);
            service.AddExchange(ExchangeType.Bybit, _mockBybit.Object);

            var cts = new CancellationTokenSource(500);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            await service.StartMonitoringAsync(cts.Token);
            await Task.Delay(600);
            service.Stop();

            // Assert
            Assert.Contains(logMessages, msg => msg.Contains("모니터링 시작"));
        }

        [Fact]
        public void SimulationMode_DefaultsToTrue_ForSafety()
        {
            // Arrange & Act
            var settings = new FundTransferSettings();

            // Assert - 안전을 위해 기본값은 true여야 함
            Assert.True(settings.SimulationMode);
        }

        [Fact]
        public void Stop_StopsMonitoring()
        {
            // Arrange
            var settings = new FundTransferSettings { SimulationMode = true };
            var service = new FundTransferService(settings);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            service.Stop();

            // Assert
            Assert.Contains(logMessages, msg => msg.Contains("중지"));
        }

        [Theory]
        [InlineData(50)]
        [InlineData(100)]
        [InlineData(1000)]
        public void MinTransferAmount_CanBeConfigured(decimal amount)
        {
            // Arrange & Act
            var settings = new FundTransferSettings
            {
                MinTransferAmount = amount,
                SimulationMode = true
            };

            // Assert
            Assert.Equal(amount, settings.MinTransferAmount);
        }

        [Fact]
        public async Task RequestTransferAsync_InSimulationMode_CompletesSuccessfully()
        {
            // Arrange
            var settings = new FundTransferSettings { SimulationMode = true };
            var service = new FundTransferService(settings, null, null); // DbManager, Telegram nullable
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);
            service.AddExchange(ExchangeType.Bybit, _mockBybit.Object);

            // Act
            var result = await service.RequestTransferAsync(
                ExchangeType.Binance,
                ExchangeType.Bybit,
                100m  // amount in USDT
            );

            // Assert - SimulationMode에서는 성공해야 함
            Assert.NotNull(result);
        }
    }
}
