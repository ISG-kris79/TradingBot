using System.Collections.Generic;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class RiskManagerTests
    {
        [Fact]
        public void CalculatePositionSize_ReturnsValidSize()
        {
            // Arrange
            var riskManager = new RiskManager();
            decimal balance = 10000;
            decimal price = 50000;
            decimal stopLossPercent = 2.0m;
            int leverage = 10;

            // Act
            decimal positionSize = riskManager.CalculatePositionSize(balance, price, stopLossPercent, leverage);

            // Assert
            Assert.True(positionSize > 0, "Position size should be greater than 0");
            Assert.True(positionSize <= balance * leverage / price, "Position size should not exceed max buying power");
        }

        [Fact]
        public void ValidateRiskParameters_ReturnsTrueForValidInput()
        {
            // Arrange
            var riskManager = new RiskManager();
            decimal balance = 10000;
            decimal riskPercent = 2.0m;
            int leverage = 10;

            // Act
            bool isValid = riskManager.ValidateRiskParameters(balance, riskPercent, leverage);

            // Assert
            Assert.True(isValid, "Valid risk parameters should return true");
        }

        [Fact]
        public void ValidateRiskParameters_ReturnsFalseForInvalidInput()
        {
            // Arrange
            var riskManager = new RiskManager();
            decimal balance = -1000; // Invalid balance
            decimal riskPercent = 2.0m;
            int leverage = 10;

            // Act
            bool isValid = riskManager.ValidateRiskParameters(balance, riskPercent, leverage);

            // Assert
            Assert.False(isValid, "Invalid balance should return false");
        }

        [Fact]
        public void CalculateStopLossPrice_ReturnsCorrectPrice()
        {
            // Arrange
            var riskManager = new RiskManager();
            decimal entryPrice = 50000;
            decimal stopLossPercent = 2.0m;
            bool isLong = true;

            // Act
            decimal stopLossPrice = riskManager.CalculateStopLossPrice(entryPrice, stopLossPercent, isLong);

            // Assert
            decimal expectedStopLoss = entryPrice * (1 - stopLossPercent / 100);
            Assert.Equal(expectedStopLoss, stopLossPrice, 2);
        }

        [Fact]
        public void CalculateTakeProfitPrice_ReturnsCorrectPrice()
        {
            // Arrange
            var riskManager = new RiskManager();
            decimal entryPrice = 50000;
            decimal takeProfitPercent = 5.0m;
            bool isLong = true;

            // Act
            decimal takeProfitPrice = riskManager.CalculateTakeProfitPrice(entryPrice, takeProfitPercent, isLong);

            // Assert
            decimal expectedTakeProfit = entryPrice * (1 + takeProfitPercent / 100);
            Assert.Equal(expectedTakeProfit, takeProfitPrice, 2);
        }
    }
}
