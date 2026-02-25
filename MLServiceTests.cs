using System;
using System.Collections.Generic;
using TradingBot.Models;
using Xunit;

namespace TradingBot.Tests
{
    public class MLServiceTests
    {
        [Fact]
        public void Train_CreatesModelFile()
        {
            // Arrange
            var mlService = new MLService();
            var trainingData = GenerateSampleData(100);

            // Act
            mlService.Train(trainingData);

            // Assert
            Assert.True(System.IO.File.Exists("model.zip"), "Model file should be created after training");
        }

        [Fact]
        public void Predict_ReturnsValidResult()
        {
            // Arrange
            var mlService = new MLService();
            var trainingData = GenerateSampleData(100);
            mlService.Train(trainingData);

            var testData = new CandleData
            {
                OpenPrice = 50000,
                HighPrice = 51000,
                LowPrice = 49500,
                ClosePrice = 50500,
                Volume = 1000,
                Label = true
            };

            // Act
            var result = mlService.Predict(testData);

            // Assert
            Assert.NotNull(result);
            Assert.InRange(result.Probability, 0f, 1f);
        }

        [Fact]
        public void LoadModel_LoadsExistingModel()
        {
            // Arrange
            var mlService = new MLService();
            var trainingData = GenerateSampleData(50);
            mlService.Train(trainingData);

            // Act
            var mlServiceNew = new MLService();
            var testData = new CandleData
            {
                OpenPrice = 50000,
                HighPrice = 51000,
                LowPrice = 49500,
                ClosePrice = 50500,
                Volume = 1000
            };
            var result = mlServiceNew.Predict(testData);

            // Assert
            Assert.NotNull(result);
        }

        private List<CandleData> GenerateSampleData(int count)
        {
            var random = new Random(42);
            var data = new List<CandleData>();

            for (int i = 0; i < count; i++)
            {
                var basePrice = 50000 + random.Next(-1000, 1000);
                data.Add(new CandleData
                {
                    OpenPrice = basePrice,
                    HighPrice = basePrice + random.Next(0, 500),
                    LowPrice = basePrice - random.Next(0, 500),
                    ClosePrice = basePrice + random.Next(-200, 200),
                    Volume = 1000 + random.Next(0, 2000),
                    Label = random.NextDouble() > 0.5
                });
            }

            return data;
        }
    }
}
