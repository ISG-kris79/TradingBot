using System;

namespace TradingBot.Models
{
    public sealed class ElliottWaveAnchorState
    {
        public int UserId { get; set; }
        public string Symbol { get; set; } = string.Empty;

        public int CurrentPhase { get; set; }

        public DateTime? Phase1StartTime { get; set; }
        public decimal Phase1LowPrice { get; set; }
        public decimal Phase1HighPrice { get; set; }
        public float Phase1Volume { get; set; }

        public DateTime? Phase2StartTime { get; set; }
        public decimal Phase2LowPrice { get; set; }
        public decimal Phase2HighPrice { get; set; }
        public float Phase2Volume { get; set; }

        public decimal Fib500Level { get; set; }
        public decimal Fib0618Level { get; set; }
        public decimal Fib786Level { get; set; }
        public decimal Fib1618Target { get; set; }

        public decimal AnchorLowPoint { get; set; }
        public decimal AnchorHighPoint { get; set; }
        public bool AnchorIsConfirmed { get; set; }
        public bool AnchorIsLocked { get; set; }
        public DateTime? AnchorConfirmedAtUtc { get; set; }
        public int LowPivotStrength { get; set; }
        public int HighPivotStrength { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
