using System;

namespace TradingBot.Shared.Models
{
    public enum ExchangeType
    {
        Binance,
        Bybit,
        Unknown
    }

    public enum NotificationChannel
    {
        Log,
        Alert,
        Profit,
        Telegram,
        Email
    }

    public class CandleData : TradingBot.Models.CandleData
    {
    }

    public record TradeLog(string? Symbol, string? Side, string? Strategy, decimal Price, float AiScore, DateTime Time, decimal PnL = 0, decimal PnLPercent = 0)
    {
        public TradeLog() : this(default, default, default, 0, 0, default, 0, 0)
        {
        }

        public int Id { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal Quantity { get; set; }
        public string ExitReason { get; set; } = string.Empty;
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public bool IsSimulation { get; set; }
        public bool IsOpenPosition => string.Equals(ExitReason, "OPEN_POSITION", StringComparison.OrdinalIgnoreCase);
        public string PositionStatus => IsOpenPosition ? "OPEN" : "CLOSED";
    }

    public class PositionInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public object? Side { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal InitialQuantity { get; set; }
        public decimal Leverage { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal Roe { get; set; }
        public DateTime EntryTime { get; set; }

        public decimal HighestPrice { get; set; }
        public decimal LowestPrice { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal StopLoss { get; set; }
        public string StopOrderId { get; set; } = string.Empty;
        /// <summary>[v5.1.3] 거래소 TP 등록 완료 → 내부 PartialClose 비활성화</summary>
        public bool TpRegisteredOnExchange { get; set; }
        public decimal EntryBbPosition { get; set; }
        public string EntryZoneTag { get; set; } = string.Empty;
        public float AiConfidencePercent { get; set; }
        public bool IsHybridMidBandLongEntry { get; set; }

        public bool IsLong { get; set; }
        public bool IsPumpStrategy { get; set; }
        public float AiScore { get; set; }
        public int TakeProfitStep { get; set; }
        public bool IsAveragedDown { get; set; }
        public bool IsPyramided { get; set; }
        public int PyramidCount { get; set; }
        public bool IsProfitRunHoldActive { get; set; }
        public bool IsVolatilityRecovery { get; set; }     // [v3.3.6] 급변동 후 회복 진입 (넓은 손절)
        public decimal RecoveryExtremePrice { get; set; }  // [v3.3.6] 급변동 극단가 (CRASH low / PUMP high)
    public decimal AggressiveMultiplier { get; set; } = 1.0m;  // 공격형 진입 배수 (1.0~2.0)

    // [엘리엇 파동 기반 익절/손절]
        public decimal Wave1LowPrice { get; set; }      // 1파의 저점 (절대 손절선)
        public decimal Wave1HighPrice { get; set; }     // 1파의 고점 (1차 익절)
        public decimal Fib0618Level { get; set; }       // 피보나치 0.618 지지
        public decimal Fib0786Level { get; set; }       // 피보나치 0.786 (손절선)
        public decimal Fib1618Target { get; set; }      // 피보나치 1.618 (2차 익절)
        public decimal HighestROEForTrailing { get; set; } // 트레일링스탑 기준 ROE
        public int PartialProfitStage { get; set; }     // 0:초기, 1:1차익절완료, 2:2차익절완료
        public decimal BreakevenPrice { get; set; }     // 본절가 (1차 익절 후 적용)
        /// <summary>[v5.2.2] 이 봇(UserId)이 직접 진입한 포지션 여부 — false면 슬롯 카운트 제외</summary>
        public bool IsOwnPosition { get; set; } = true;
    }
}
