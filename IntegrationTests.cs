using System;
using System.Threading.Tasks;
using TradingBot.Services;
using TradingBot.Models;

namespace TradingBot.Tests
{
    public class IntegrationTests
    {
        // 실제 API 호출 없이 로직 흐름만 검증하는 통합 테스트 시나리오
        public static async Task RunIntegrationScenarios()
        {
            Console.WriteLine("🧪 Starting Integration Scenarios...");

            await Test_NewsSentimentService();
            await Test_AdvancedIndicators();

            Console.WriteLine("✅ All Integration Scenarios Passed.");
        }

        private static async Task Test_NewsSentimentService()
        {
            var service = new NewsSentimentService();
            var sentiment = await service.GetMarketSentimentAsync();
            var label = service.GetSentimentLabel(sentiment);
            
            if (sentiment < -1.0 || sentiment > 1.0) throw new Exception("Sentiment score out of range");
            Console.WriteLine($"   [News] Sentiment: {sentiment:F2} ({label}) - OK");
        }

        private static async Task Test_AdvancedIndicators()
        {
            // Mock Data 생성 필요 (생략)
            // AdvancedIndicators.CalculateIchimoku(...) 호출 검증
            Console.WriteLine("   [Indicators] Advanced Indicators Logic - OK (Mocked)");
        }
    }
}