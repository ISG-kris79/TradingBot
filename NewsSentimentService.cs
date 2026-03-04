using System;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    public class NewsSentimentService
    {
        // 실제 구현 시 외부 뉴스 API (CryptoPanic, LunarCrush 등) 연동 필요
        // 현재는 시뮬레이션 값을 반환
        private readonly Random _random = new Random();

        public async Task<double> GetMarketSentimentAsync()
        {
            // API 호출 시뮬레이션
            await Task.Delay(100);
            
            // -1.0 (Extreme Fear) ~ 1.0 (Extreme Greed)
            return (_random.NextDouble() * 2.0) - 1.0;
        }

        public string GetSentimentLabel(double score)
        {
            if (score >= 0.5) return "Extreme Greed 🤑";
            if (score >= 0.1) return "Greed 🙂";
            if (score <= -0.5) return "Extreme Fear 😱";
            if (score <= -0.1) return "Fear 😨";
            return "Neutral 😐";
        }
    }
}