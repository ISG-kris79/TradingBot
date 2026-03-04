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
        public int Id { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal Quantity { get; set; }
        public string ExitReason { get; set; } = string.Empty;
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
    }

    public class PositionInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public object? Side { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal Leverage { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal Roe { get; set; }
        public DateTime EntryTime { get; set; }

        public decimal HighestPrice { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal StopLoss { get; set; }
        public long StopOrderId { get; set; }

        public bool IsLong { get; set; }
        public bool IsPumpStrategy { get; set; }
        public float AiScore { get; set; }
        public int TakeProfitStep { get; set; }
        public bool IsAveragedDown { get; set; }

        // [엘리엇 파동 기반 익절/손절]
        public decimal Wave1LowPrice { get; set; }      // 1파의 저점 (절대 손절선)
        public decimal Wave1HighPrice { get; set; }     // 1파의 고점 (1차 익절)
        public decimal Fib0618Level { get; set; }       // 피보나치 0.618 지지
        public decimal Fib0786Level { get; set; }       // 피보나치 0.786 (손절선)
        public decimal Fib1618Target { get; set; }      // 피보나치 1.618 (2차 익절)
        public decimal HighestROEForTrailing { get; set; } // 트레일링스탑 기준 ROE
        public int PartialProfitStage { get; set; }     // 0:초기, 1:1차익절완료, 2:2차익절완료
        public decimal BreakevenPrice { get; set; }     // 본절가 (1차 익절 후 적용)
    }
}
