using System;

namespace TradingBot.Services
{
    public class RiskManager
    {
        public bool IsTripped { get; private set; } = false;
        public DateTime TripTime { get; private set; }
        public decimal DailyRealizedPnl { get; private set; } = 0;

        private decimal _maxDailyLoss;
        private decimal _initialBalance;
        private int _consecutiveLosses = 0;
        private const int MAX_CONSECUTIVE_LOSSES = 5;
        private string _tripReason = string.Empty;

        public event Action<string>? OnTripped;

        public void Initialize(decimal initialBalance, decimal maxDailyLossPercent = 0.1m)
        {
            _initialBalance = initialBalance;
            _maxDailyLoss = initialBalance * maxDailyLossPercent;
            DailyRealizedPnl = 0;
            IsTripped = false;
            _consecutiveLosses = 0;
            _tripReason = string.Empty;
        }

        /// <summary>[v4.4.0] DB에서 오늘 누적 PnL 복원 (재시작 시)</summary>
        public void RestoreDailyPnl(decimal todayPnl)
        {
            DailyRealizedPnl = todayPnl;
        }

        /// <summary>
        /// [v5.1.5] strategy 파라미터 추가 — MANUAL 진입 손실은 서킷 브레이커 누적 제외
        /// PLAY -$705 수동 테스트 → 서킷 브레이커 발동 → PUMP 하루 종일 진입 차단 방지
        /// </summary>
        public void UpdatePnlAndCheck(decimal pnl, string? strategy = null)
        {
            // [v5.1.5] MANUAL/외부 진입은 PnL 누적만 하고 서킷 브레이커 트리거 안 함
            bool isManualOrExternal = !string.IsNullOrEmpty(strategy) &&
                (strategy.Equals("MANUAL", StringComparison.OrdinalIgnoreCase) ||
                 strategy.StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase));

            DailyRealizedPnl += pnl;

            if (isManualOrExternal) return;  // 서킷 브레이커 판정 스킵

            if (pnl < 0) _consecutiveLosses++;
            else _consecutiveLosses = 0;

            if (!IsTripped)
            {
                if (DailyRealizedPnl <= -_maxDailyLoss)
                {
                    IsTripped = true;
                    TripTime = DateTime.Now;
                    _tripReason = "Daily loss limit exceeded";
                    OnTripped?.Invoke(_tripReason);
                }
                else if (_consecutiveLosses >= MAX_CONSECUTIVE_LOSSES)
                {
                    IsTripped = true;
                    TripTime = DateTime.Now;
                    _tripReason = "Maximum consecutive losses exceeded";
                    OnTripped?.Invoke(_tripReason);
                }
            }
        }

        public void Reset()
        {
            IsTripped = false;
            DailyRealizedPnl = 0;
            _consecutiveLosses = 0;
            _tripReason = string.Empty;
        }

        public string GetTripDetails()
        {
            return IsTripped ? $"{_tripReason} (Triggered at {TripTime:yyyy-MM-dd HH:mm:ss})" : "Not tripped";
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