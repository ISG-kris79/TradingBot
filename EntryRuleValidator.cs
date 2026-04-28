// EntryRuleValidator removed (AI-dependent, no live callers).
// ElliottWaveState / FibonacciLevels are still referenced by Models.cs / TradingEngine.cs / DatabaseService.cs.

namespace TradingBot
{
    /// <summary>
    /// 엘리엇 파동 상태
    /// </summary>
    public class ElliottWaveState
    {
        public bool IsValid { get; set; }
        public string RejectReason { get; set; } = "";
        public bool Rule1Violated { get; set; }
        public bool Rule2Violated { get; set; }
        public bool Rule3Violated { get; set; }
        public bool IsSuperTrend { get; set; }
        public float FinalScore { get; set; }
        public float FibLevel { get; set; }
        public double Wave1Length { get; set; }
        public double Wave2RetracePct { get; set; }
        public double Wave3Length { get; set; }
    }

    /// <summary>
    /// 피보나치 레벨 (AI 특징으로 사용)
    /// </summary>
    public class FibonacciLevels
    {
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Range { get; set; }

        // 되돌림 레벨
        public decimal Fib0000 { get; set; } // 100% (고점)
        public decimal Fib0236 { get; set; } // 23.6%
        public decimal Fib0382 { get; set; } // 38.2%
        public decimal Fib0500 { get; set; } // 50%
        public decimal Fib0618 { get; set; } // 61.8% (황금비)
        public decimal Fib0786 { get; set; } // 78.6%
        public decimal Fib1000 { get; set; } // 0% (저점)

        // 확장 레벨
        public decimal Fib1618 { get; set; } // 161.8% 확장

        // 현재가 근접도 (AI 특징)
        public double DistanceTo0382_Pct { get; set; }
        public double DistanceTo0618_Pct { get; set; }
        public double DistanceTo0786_Pct { get; set; }

        // 진입 구간 여부
        public bool InEntryZone { get; set; } // 0.382 ~ 0.618 구간
    }
}
