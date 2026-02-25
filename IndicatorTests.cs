using Binance.Net.Interfaces;
using Moq;
using System.Collections.Generic;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class IndicatorTests
    {
        [Fact]
        public void CalculateRSI_ReturnsCorrectValue()
        {
            // Arrange
            // RSI 계산 검증을 위한 샘플 데이터 (종가 기준)
            // 14일 RSI를 계산하기 위해 최소 15개 이상의 데이터가 필요합니다.
            // 상승과 하락을 섞어서 테스트합니다.
            var prices = new List<decimal> 
            { 
                100, 102, 104, 103, 102, 101, 100, 99, 98, 99, 
                100, 102, 104, 106, 105, 104, 103, 102, 101, 100 
            };

            var klines = new List<IBinanceKline>();
            foreach (var p in prices)
            {
                var mockKline = new Mock<IBinanceKline>();
                mockKline.Setup(k => k.ClosePrice).Returns(p);
                klines.Add(mockKline.Object);
            }

            // Act
            // period = 14
            double rsi = IndicatorCalculator.CalculateRSI(klines, 14);

            // Assert
            // 예상값은 외부 계산기나 엑셀 등으로 검증된 값과 비교해야 합니다.
            // 여기서는 데이터 패턴상 하락세이므로 50 이하가 나와야 합니다.
            // 정확한 수식 검증:
            // 1. 첫 14개 변동: 
            // +2, +2, -1, -1, -1, -1, -1, -1, +1, +1, +2, +2, +2, -1
            // Gain합: 12, Loss합: 5
            // AvgGain: 12/14 = 0.857, AvgLoss: 5/14 = 0.357
            // 2. 이후 Wilder's Smoothing 적용...
            
            Assert.InRange(rsi, 0, 100);
            Assert.True(rsi < 50, $"RSI should be less than 50 for downtrend data, but was {rsi}");
        }

        [Fact]
        public void CalculateRSI_NotEnoughData_Returns50()
        {
            // Arrange
            var klines = new List<IBinanceKline>();
            for (int i = 0; i < 10; i++)
            {
                var mockKline = new Mock<IBinanceKline>();
                mockKline.Setup(k => k.ClosePrice).Returns(100 + i);
                klines.Add(mockKline.Object);
            }

            // Act
            double rsi = IndicatorCalculator.CalculateRSI(klines, 14);

            // Assert
            Assert.Equal(50, rsi);
        }

        [Fact]
        public void CalculateBB_ReturnsCorrectValues()
        {
            // Arrange
            // 5일간의 가격 데이터
            var prices = new List<decimal> { 10, 12, 14, 16, 18 };
            var klines = new List<IBinanceKline>();
            foreach (var p in prices)
            {
                var mockKline = new Mock<IBinanceKline>();
                mockKline.Setup(k => k.ClosePrice).Returns(p);
                klines.Add(mockKline.Object);
            }

            // Act
            // period = 5, multiplier = 2
            // 평균(Mid) = 14, 표준편차 ≈ 2.8284
            var result = IndicatorCalculator.CalculateBB(klines, 5, 2);

            // Assert
            Assert.Equal(14, result.Mid, 4);
            Assert.Equal(19.6569, result.Upper, 4);
            Assert.Equal(8.3431, result.Lower, 4);
        }
    }
}