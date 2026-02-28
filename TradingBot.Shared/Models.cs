using System;

namespace TradingBot.Shared.Models
{
    public enum StrategyType { Major, Scanner, Listing }
    public enum ExchangeType { Binance, Bybit }

    public record TradeLog(string? Symbol, string? Side, string? Strategy, decimal Price, float AiScore, DateTime Time, decimal PnL = 0, decimal PnLPercent = 0);

    public class PositionInfo
    {
        public string? Symbol { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal Leverage { get; set; }
        public decimal UnrealizedPnL { get; set; }
        public decimal Roe { get; set; }
        
        // Exchange-specific fields
        public object? Side { get; set; } // Binance.Net.Enums.OrderSide or compatible
        
        // Extended fields for TradingEngine
        public decimal HighestPrice { get; set; }
        public bool IsPumpStrategy { get; set; }
        public int TakeProfitStep { get; set; } = 0;
        public bool IsLong { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal StopLoss { get; set; }
        public DateTime EntryTime { get; set; }
        public bool IsAveragedDown { get; set; } = false;
        public float AiScore { get; set; }
        public long StopOrderId { get; set; } = 0;
    }

    public class CandleModel
    {
        public string? Symbol { get; set; }
        public DateTime OpenTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
    }

    public class CandleData
    {
        public string? Symbol { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public float Volume { get; set; }
        public string? Interval { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public float RSI { get; set; }
        public float BollingerUpper { get; set; }
        public float BollingerLower { get; set; }
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float MACD_Hist { get; set; }
    }

    public class PredictionResult
    {
        public bool Prediction { get; set; }
        public float Probability { get; set; }
        public float Score { get; set; }
    }

    public enum PositionStatus { None, Monitoring, TakeProfitReady, Danger }

    public enum NotificationChannel
    {
        Log,
        Alert,
        Profit
    }
}
