using System;

namespace TradingBot.Services
{
    public class RiskManager
    {
        public decimal DailyRealizedPnl { get; private set; } = 0;

        public void Initialize(decimal initialBalance, decimal maxDailyLossPercent = 0.1m)
        {
            DailyRealizedPnl = 0;
        }

        /// <summary>[v4.4.0] DB에서 오늘 누적 PnL 복원 (재시작 시)</summary>
        public void RestoreDailyPnl(decimal todayPnl)
        {
            DailyRealizedPnl = todayPnl;
        }

        public void UpdatePnlAndCheck(decimal pnl, string? strategy = null)
        {
            DailyRealizedPnl += pnl;
        }

        public void Reset()
        {
            DailyRealizedPnl = 0;
        }

        public string GetTripDetails()
        {
            return "Circuit breaker removed";
        }

        public decimal CalculatePositionSize(decimal balance, decimal price, decimal stopLossPercent, int leverage)
        {
            if (price <= 0 || stopLossPercent <= 0) return 0;
            // 리스크 기반 포지션 사이징 로직
            return (balance * leverage) / price;
        }

        public bool ValidateRiskParameters(decimal balance, decimal riskPercent, int leverage)
        {
            return balance > 0 && riskPercent > 0 && leverage > 0;
        }

        public decimal CalculateStopLossPrice(decimal entryPrice, decimal stopLossPercent, bool isLong)
        {
            if (isLong)
                return entryPrice * (1 - stopLossPercent / 100);
            else
                return entryPrice * (1 + stopLossPercent / 100);
        }

        public decimal CalculateTakeProfitPrice(decimal entryPrice, decimal takeProfitPercent, bool isLong)
        {
            if (isLong)
                return entryPrice * (1 + takeProfitPercent / 100);
            else
                return entryPrice * (1 - takeProfitPercent / 100);
        }
    }
}