using Moq;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Shared.Models;
using Xunit;

namespace TradingBot.Tests.Services
{
    /// <summary>
    /// PortfolioRebalancingService 단위 테스트
    /// Phase 14: 포트폴리오 리밸런싱 서비스 SimulationMode 및 안전 가드 검증
    /// </summary>
    public class PortfolioRebalancingServiceTests
    {
        private readonly Mock<IExchangeService> _mockBinance;
        private readonly Mock<IExchangeService> _mockBybit;
        private readonly Mock<DbManager> _mockDbManager;
        private readonly Mock<TelegramService> _mockTelegram;

        public PortfolioRebalancingServiceTests()
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
            var settings = new PortfolioRebalancingSettings
            {
                RebalanceThreshold = 5.0m,
                SimulationMode = true
            };

            // Act
            var service = new PortfolioRebalancingService(settings);

            // Assert
            Assert.NotNull(service);
        }

        [Fact]
        public void AddExchange_AddsExchangeSuccessfully()
        {
            // Arrange
            var settings = new PortfolioRebalancingSettings { SimulationMode = true };
            var service = new PortfolioRebalancingService(settings);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);

            // Assert
            Assert.Single(logMessages);
            Assert.Contains("Binance", logMessages[0]);
        }

        [Fact]
        public void SimulationMode_DefaultsToTrue_ForSafety()
        {
            // Arrange & Act
            var settings = new PortfolioRebalancingSettings();

            // Assert - 안전을 위해 기본값은 true여야 함
            Assert.True(settings.SimulationMode);
        }

        [Fact]
        public void Stop_StopsMonitoring()
        {
            // Arrange
            var settings = new PortfolioRebalancingSettings { SimulationMode = true };
            var service = new PortfolioRebalancingService(settings);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            service.Stop();

            // Assert
            Assert.Contains(logMessages, msg => msg.Contains("중지"));
        }

        [Theory]
        [InlineData(3.0)]
        [InlineData(5.0)]
        [InlineData(10.0)]
        public void RebalanceThreshold_CanBeConfigured(decimal threshold)
        {
            // Arrange & Act
            var settings = new PortfolioRebalancingSettings
            {
                RebalanceThreshold = threshold,
                SimulationMode = true
            };

            // Assert
            Assert.Equal(threshold, settings.RebalanceThreshold);
        }

        [Fact]
        public void TargetAllocation_DefaultConfigurationIsValid()
        {
            // Arrange & Act
            var settings = new PortfolioRebalancingSettings { SimulationMode = true };

            // Assert
            Assert.NotNull(settings.TargetAllocation);
            Assert.NotEmpty(settings.TargetAllocation);
            
            // 모든 배분 비율의 합이 100%이어야 함
            var totalAllocation = settings.TargetAllocation.Values.Sum();
            Assert.Equal(100m, totalAllocation);
        }

        [Fact]
        public void CheckIntervalHours_DefaultsTo24Hours()
        {
            // Arrange & Act
            var settings = new PortfolioRebalancingSettings();

            // Assert
            Assert.Equal(24, settings.CheckIntervalHours);
        }

        [Fact]
        public async Task StartMonitoringAsync_InSimulationMode_CompletesSuccessfully()
        {
            // Arrange
            var settings = new PortfolioRebalancingSettings
            {
                SimulationMode = true,
                CheckIntervalHours = 1,
                TargetAllocation = new Dictionary<string, decimal>
                {
                    { "BTC", 40m },
                    { "ETH", 30m },
                    { "USDT", 30m }
                }
            };
            var service = new PortfolioRebalancingService(settings, null, null); // DbManager, Telegram nullable
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);

            var cts = new CancellationTokenSource(100);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act & Assert
            var exception = await Record.ExceptionAsync(async () =>
            {
                await service.StartMonitoringAsync(cts.Token);
            });

            Assert.Null(exception);
        }
    }
}
