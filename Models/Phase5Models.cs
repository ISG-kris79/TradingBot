using System;
using System.Collections.Generic;
using Binance.Net.Enums;
using TradingBot.Shared.Models;

namespace TradingBot.Models
{
    public enum BacktestAnnualizationMode
    {
        Auto,
        None,
        TradingDays252,
        CalendarDays365,
        Crypto5m
    }

    public class BacktestMetricOptions
    {
        public double RiskFreeRateAnnualPct { get; set; } = 0.0;
        public BacktestAnnualizationMode AnnualizationMode { get; set; } = BacktestAnnualizationMode.Auto;
    }

    public class BacktestResult
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal InitialBalance { get; set; }
        public decimal FinalBalance { get; set; }
        public decimal TotalProfit => FinalBalance - InitialBalance;
        public int TotalTrades { get; set; }
        public int WinCount { get; set; }
        public int LossCount { get; set; }
        public decimal ProfitPercentage => InitialBalance > 0 ? (TotalProfit / InitialBalance) * 100 : 0;
        public decimal WinRate => TotalTrades > 0 ? (decimal)WinCount / TotalTrades * 100 : 0;
        public decimal MaxDrawdown { get; set; }
        public double SharpeRatio { get; set; }
        public double SortinoRatio { get; set; }
        public string MetricsComputationNote { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string StrategyConfiguration { get; set; } = string.Empty;

        public List<CandleData> Candles { get; set; } = new();
        public List<TradeLog> TradeHistory { get; set; } = new();
        public List<decimal> EquityCurve { get; set; } = new();
        public List<string> TradeDates { get; set; } = new();
        public List<OptimizationTrialItem> TopTrials { get; set; } = new();
    }

    public class OptimizationTrialItem
    {
        public int Rank { get; set; }
        public string Medal => Rank switch
        {
            1 => "🥇",
            2 => "🥈",
            3 => "🥉",
            _ => "·"
        };
        public int TrialId { get; set; }
        public decimal FinalBalance { get; set; }
        public decimal ProfitPercent { get; set; }
        public string Parameters { get; set; } = string.Empty;
    }

    public class ExchangeInfoModel
    {
        // 필요한 정보 추가
    }

    public class TradeHistoryModel
    {
        public int Id { get; set; }
        public string Symbol { get; set; }
        public string Side { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal PnL { get; set; }
        public decimal PnLPercent { get; set; }
        public string ExitReason { get; set; }
        public DateTime ExitTime { get; set; }
    }
}
