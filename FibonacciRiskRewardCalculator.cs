using System;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// 피보나치 기반 손익비 계산기 (20배 레버리지 최적화)
    /// 
    /// 진입(Entry): Fib 0.382 ~ 0.618
    /// 손절(Stop Loss): Fib 0.786 또는 전저점 이탈 (약 -1.3% 내외)
    /// 익절(Take Profit): Fib 확장 1.618 (ROE 약 20%)
    /// 
    /// 20배 레버리지 기준:
    /// - 가격 변동 1% = ROE 20%
    /// - 손절 1.3% = 최대 손실 26%
    /// - 익절 1% = ROE 20%
    /// 
    /// 손익비 = 20% / 26% ≈ 0.77 (공격적이지만 진입율 0.5%로 보상)
    /// </summary>
    public class FibonacciRiskRewardCalculator
    {
        private readonly ILogger _logger;

        public event Action<string>? OnLog;

        // 피보나치 레벨 (표준)
        public const decimal FIB_ENTRY_MIN = 0.382m;   // 진입 최소
        public const decimal FIB_ENTRY_MAX = 0.786m;   // 진입 최대 (확장됨: 0.618→0.786, 약한 타점도 포함)
        public const decimal FIB_STOP_LOSS = 0.786m;   // 손절
        public const decimal FIB_TAKE_PROFIT = 1.618m; // 익절 (확장)

        // 20배 레버리지 기준 ROE
        public const int LEVERAGE = 20;
        public const double TARGET_ROE_PERCENT = 20.0;  // 목표 수익률
        public const double MAX_LOSS_PERCENT = 26.0;    // 최대 손실률

        public FibonacciRiskRewardCalculator()
        {
            _logger = Log.ForContext<FibonacciRiskRewardCalculator>();
        }

        /// <summary>
        /// 진입가, 손절가, 익절가 계산
        /// </summary>
        public FibLevels CalculateLevels(decimal highPrice, decimal lowPrice, bool isLong)
        {
            var levels = new FibLevels();

            decimal range = highPrice - lowPrice;
            if (range <= 0)
            {
                LogWarning("⚠️ 고가와 저가가 동일하거나 역전됨. 기본값 반환.");
                return levels;
            }

            // 피보나치 되돌림 계산
            decimal fib382 = highPrice - (range * FIB_ENTRY_MIN);
            decimal fib618 = highPrice - (range * FIB_ENTRY_MAX);
            decimal fib786 = highPrice - (range * FIB_STOP_LOSS);
            decimal fib1618 = highPrice + (range * (FIB_TAKE_PROFIT - 1.0m));  // 확장

            if (isLong)
            {
                // 롱 포지션
                levels.EntryMin = fib618;   // 더 낮은 가격
                levels.EntryMax = fib382;   // 더 높은 가격
                levels.StopLoss = fib786;   // 손절 (전저점 이탈)
                levels.TakeProfit = fib1618;  // 익절 (확장)
                
                // 권장 진입가 (중간값)
                levels.RecommendedEntry = (fib382 + fib618) / 2m;
                
                LogInfo($"📊 [LONG] 진입: ${fib618:F2} ~ ${fib382:F2} | 손절: ${fib786:F2} | 익절: ${fib1618:F2}");
            }
            else
            {
                // 숏 포지션 (반대)
                levels.EntryMin = fib382;   // 더 높은 가격
                levels.EntryMax = fib618;   // 더 낮은 가격
                levels.StopLoss = lowPrice - (range * (FIB_STOP_LOSS - FIB_ENTRY_MAX));  // 손절 (전고점 돌파)
                levels.TakeProfit = lowPrice - (range * (FIB_TAKE_PROFIT - 1.0m));  // 익절 (확장)
                
                levels.RecommendedEntry = (fib382 + fib618) / 2m;
                
                LogInfo($"📊 [SHORT] 진입: ${fib382:F2} ~ ${fib618:F2} | 손절: ${levels.StopLoss:F2} | 익절: ${levels.TakeProfit:F2}");
            }

            return levels;
        }

        /// <summary>
        /// 손익비 계산
        /// </summary>
        public RiskRewardRatio CalculateRiskReward(decimal entryPrice, decimal stopLoss, decimal takeProfit, bool isLong)
        {
            var ratio = new RiskRewardRatio();

            if (isLong)
            {
                ratio.Risk = entryPrice - stopLoss;
                ratio.Reward = takeProfit - entryPrice;
            }
            else
            {
                ratio.Risk = stopLoss - entryPrice;
                ratio.Reward = entryPrice - takeProfit;
            }

            if (ratio.Risk <= 0)
            {
                LogWarning("⚠️ 리스크가 0 이하입니다. 손절가를 확인하세요.");
                return ratio;
            }

            ratio.Ratio = (double)(ratio.Reward / ratio.Risk);
            
            // 20배 레버리지 적용
            ratio.RiskPercent = (double)(ratio.Risk / entryPrice) * 100 * LEVERAGE;
            ratio.RewardPercent = (double)(ratio.Reward / entryPrice) * 100 * LEVERAGE;

            LogInfo($"💰 손익비: {ratio.Ratio:F2}:1 | 리스크: {ratio.RiskPercent:F1}% | 보상: {ratio.RewardPercent:F1}%");

            return ratio;
        }

        /// <summary>
        /// 진입 가능 여부 검증
        /// </summary>
        public bool ValidateEntry(decimal currentPrice, FibLevels levels, bool isLong)
        {
            if (isLong)
            {
                bool inRange = currentPrice >= levels.EntryMin && currentPrice <= levels.EntryMax;
                
                if (!inRange)
                {
                    LogWarning($"❌ 현재가 ${currentPrice:F2}가 진입 범위 밖입니다. (${levels.EntryMin:F2} ~ ${levels.EntryMax:F2})");
                    return false;
                }
                
                LogInfo($"✅ 현재가 ${currentPrice:F2}가 진입 범위 내에 있습니다.");
                return true;
            }
            else
            {
                bool inRange = currentPrice >= levels.EntryMax && currentPrice <= levels.EntryMin;
                
                if (!inRange)
                {
                    LogWarning($"❌ 현재가 ${currentPrice:F2}가 진입 범위 밖입니다. (${levels.EntryMax:F2} ~ ${levels.EntryMin:F2})");
                    return false;
                }
                
                LogInfo($"✅ 현재가 ${currentPrice:F2}가 진입 범위 내에 있습니다.");
                return true;
            }
        }

        /// <summary>
        /// 20배 레버리지 기준 ROE 계산
        /// </summary>
        public double CalculateROE(decimal entryPrice, decimal currentPrice, bool isLong)
        {
            if (entryPrice == 0) return 0;

            double priceChangePercent = isLong
                ? (double)((currentPrice - entryPrice) / entryPrice) * 100
                : (double)((entryPrice - currentPrice) / entryPrice) * 100;

            double roe = priceChangePercent * LEVERAGE;
            return roe;
        }

        /// <summary>
        /// 목표 수익률 도달 여부 확인
        /// </summary>
        public bool HasReachedTargetROE(decimal entryPrice, decimal currentPrice, bool isLong)
        {
            double roe = CalculateROE(entryPrice, currentPrice, isLong);
            bool reached = roe >= TARGET_ROE_PERCENT;

            if (reached)
            {
                LogInfo($"🎯 목표 ROE {TARGET_ROE_PERCENT}% 도달! 현재 ROE: {roe:F2}%");
            }

            return reached;
        }

        private void LogInfo(string message)
        {
            _logger.Information(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void LogWarning(string message)
        {
            _logger.Warning(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    /// <summary>
    /// 피보나치 레벨
    /// </summary>
    public class FibLevels
    {
        public decimal EntryMin { get; set; }
        public decimal EntryMax { get; set; }
        public decimal RecommendedEntry { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
    }

    /// <summary>
    /// 손익비
    /// </summary>
    public class RiskRewardRatio
    {
        public decimal Risk { get; set; }
        public decimal Reward { get; set; }
        public double Ratio { get; set; }
        public double RiskPercent { get; set; }
        public double RewardPercent { get; set; }
    }
}
