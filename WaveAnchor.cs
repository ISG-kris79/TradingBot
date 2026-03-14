using System;
using TradingBot.Models;

namespace TradingBot.Strategies
{
    public class WaveAnchor
    {
        public decimal LowPoint { get; set; }
        public decimal HighPoint { get; set; }
        public bool IsConfirmed { get; set; }
        public bool IsLocked { get; set; }
        public DateTime ConfirmedAtUtc { get; set; }
        public int LowPivotStrength { get; set; }
        public int HighPivotStrength { get; set; }

        public void Confirm(decimal lowPoint, decimal highPoint, int lowPivotStrength, int highPivotStrength)
        {
            LowPoint = lowPoint;
            HighPoint = highPoint;
            LowPivotStrength = lowPivotStrength;
            HighPivotStrength = highPivotStrength;
            IsConfirmed = true;
            IsLocked = true;
            ConfirmedAtUtc = DateTime.UtcNow;
        }

        public bool IsInvalidated(decimal currentLow, decimal fib786Level, decimal invalidationBuffer)
        {
            if (!IsConfirmed || fib786Level <= 0m)
                return false;

            return currentLow < fib786Level * invalidationBuffer;
        }

        public void UpdateWave(CandleData current, bool isPivotHigh, bool isWithinRetracement)
        {
            if (current == null)
                return;

            if (isPivotHigh && !IsConfirmed)
            {
                HighPoint = current.High;
                IsConfirmed = true;
                IsLocked = true;
                ConfirmedAtUtc = DateTime.UtcNow;
                return;
            }

            if (!IsLocked && current.High > HighPoint && !isWithinRetracement)
            {
                HighPoint = current.High;
            }
        }

        public void Reset()
        {
            LowPoint = 0m;
            HighPoint = 0m;
            IsConfirmed = false;
            IsLocked = false;
            ConfirmedAtUtc = default;
            LowPivotStrength = 0;
            HighPivotStrength = 0;
        }
    }
}
