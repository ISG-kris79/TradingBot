using Moq;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Shared.Models;
using Xunit;

namespace TradingBot.Tests.Services
{
    /// <summary>
    /// ArbitrageExecutionService 단위 테스트
    /// Phase 14: 차익거래 서비스 SimulationMode 및 주문 실행 로직 검증
    /// </summary>
    public class ArbitrageExecutionServiceTests
    {
        private readonly Mock<IExchangeService> _mockBinance;
        private readonly Mock<IExchangeService> _mockBybit;
        private readonly Mock<DbManager> _mockDbManager;
        private readonly Mock<TelegramService> _mockTelegram;

        public ArbitrageExecutionServiceTests()
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
            var settings = new ArbitrageSettings
            {
                MinProfitPercent = 0.5m,
                SimulationMode = true
            };

            // Act
            var service = new ArbitrageExecutionService(settings);

            // Assert
            Assert.NotNull(service);
            Assert.Equal(settings, service.Settings);
        }

        [Fact]
        public void AddExchange_AddsExchangeSuccessfully()
        {
            // Arrange
            var settings = new ArbitrageSettings { SimulationMode = true };
            var service = new ArbitrageExecutionService(settings);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);

            // Assert
            Assert.Single(logMessages);
            Assert.Contains("Binance", logMessages[0]);
        }

        [Fact]
        public async Task StartAsync_InSimulationMode_CompletesSuccessfully()
        {
            // Arrange
            var settings = new ArbitrageSettings
            {
                ScanIntervalSeconds = 1,
                SimulationMode = true
            };
            var service = new ArbitrageExecutionService(settings);
            service.AddExchange(ExchangeType.Binance, _mockBinance.Object);

            var cts = new CancellationTokenSource(500); // 500ms 후 중지
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            var startTask = service.StartAsync(new List<string> { "BTCUSDT" }, cts.Token);
            await Task.Delay(600); // 모니터링 실행 대기
            service.Stop();

            // Assert
            Assert.Contains(logMessages, msg => msg.Contains("차익거래") || msg.Contains("Arbitrage"));
        }

        [Theory]
        [InlineData(true)]  // SimulationMode = true
        [InlineData(false)] // SimulationMode = false
        public void Settings_SimulationMode_ReflectsConfiguration(bool simulationMode)
        {
            // Arrange & Act
            var settings = new ArbitrageSettings { SimulationMode = simulationMode };
            var service = new ArbitrageExecutionService(settings);

            // Assert
            Assert.Equal(simulationMode, service.Settings.SimulationMode);
        }

        [Fact]
        public void Stop_StopsMonitoring()
        {
            // Arrange
            var settings = new ArbitrageSettings { SimulationMode = true };
            var service = new ArbitrageExecutionService(settings);
            var logMessages = new List<string>();
            service.OnLog += logMessages.Add;

            // Act
            service.Stop();

            // Assert
            Assert.Contains(logMessages, msg => msg.Contains("중지"));
        }

        [Fact]
        public void SimulationMode_DefaultsToTrue_ForSafety()
        {
            // Arrange & Act
            var settings = new ArbitrageSettings();

            // Assert - 안전을 위해 기본값은 true여야 함
            Assert.True(settings.SimulationMode);
        }
    }
}
