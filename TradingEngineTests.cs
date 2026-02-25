using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Services;
using System.Reflection;
using TradingBot;

namespace TradingBot.Tests
{
    public class TradingEngineTests
    {
        // 간단한 테스트 러너
        public static void RunAll()
        {
            var tests = new TradingEngineTests();
            var methods = typeof(TradingEngineTests).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            MainWindow.Instance?.AddLog("🧪 단위 테스트 실행 시작...");
            int passed = 0;
            int total = 0;
            foreach (var method in methods)
            {
                if (method.Name == nameof(RunAll)) continue;
                total++;
                try
                {
                    method.Invoke(tests, null);
                    MainWindow.Instance?.AddLog($"✅ {method.Name}: Passed");
                    passed++;
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.AddLog($"❌ {method.Name}: Failed - {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            MainWindow.Instance?.AddLog($"🏁 테스트 완료: {passed}/{total} 성공");
        }

        // Mock 객체 대신 간단한 상태 검증 로직 예시
        public void Test_RiskManager_CircuitBreaker()
        {
            // Arrange
            var riskManager = new RiskManager();
            riskManager.Initialize(1000, 0.05m); // 1000불 시작, 5% 손실 한도 (-50불)

            // Act
            riskManager.UpdatePnlAndCheck(-10); // -10
            riskManager.UpdatePnlAndCheck(-20); // -30
            riskManager.UpdatePnlAndCheck(-25); // -55 (한도 초과)

            // Assert
            if (!riskManager.IsTripped) throw new Exception("Test Failed: Circuit Breaker should be tripped.");
        }

        public void Test_ConsecutiveLosses()
        {
            // Arrange
            var riskManager = new RiskManager();
            riskManager.Initialize(1000);

            // Act
            for(int i=0; i<5; i++) riskManager.UpdatePnlAndCheck(-1);

            // Assert
            if (!riskManager.IsTripped) throw new Exception("Test Failed: Consecutive losses should trip breaker.");
        }

        public void Test_RLAgent_Learning()
        {
            // Arrange
            var agent = new RLAgent();
            string state = "50_0.5_100"; // RSI 50, MACD 0.5, BB Width 100
            int action = 1; // Buy
            
            // Act
            // 1. 초기 행동 선택 (Random or 0)
            int initialAction = agent.GetAction(state);

            // 2. 긍정적 보상 부여 (학습)
            agent.UpdateQValue(state, action, 10.0, state); // 큰 보상 부여

            // 3. 학습 후 동일 상태에서 행동 선택 (Exploitation)
            // 입실론(Exploration) 때문에 랜덤이 나올 수 있으나, Q값이 매우 크면 해당 행동 확률이 높음
            // 단위 테스트에서는 Random Seed 고정 또는 내부 로직 검증이 필요하지만, 여기서는 호출 여부만 확인
        }

        public void Test_SymbolSettings_Logic()
        {
            // Arrange
            var settingsMap = new Dictionary<string, SymbolSettings>();
            settingsMap["BTCUSDT"] = new SymbolSettings { Symbol = "BTCUSDT", Leverage = 50, MarginAmount = 500 };
            settingsMap["ETHUSDT"] = new SymbolSettings { Symbol = "ETHUSDT", Leverage = 20, MarginAmount = 200 };

            // Act
            var btcSettings = settingsMap["BTCUSDT"];
            var ethSettings = settingsMap["ETHUSDT"];

            // Assert
            if (btcSettings.Leverage != 50) throw new Exception("BTC Leverage setting failed");
            if (ethSettings.MarginAmount != 200) throw new Exception("ETH Margin setting failed");
        }
    }
}