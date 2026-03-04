using System;
using System.Collections.Generic;
using TradingBot.Models;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// [v2.1.18] 지표 결합형 동적 익절 시스템
    /// 
    /// 5개 지표(엘리엇 파동, 피보나치, RSI, BB, MACD)를 통합하여
    /// ROE 20% 이상에서 추격 간격(ATR 멀티플라이어)을 동적으로 조정
    /// 
    /// 핵심 로직:
    /// - tightModifier (1.0 ~ 0.1): 스탑 간격의 공격성을 제어
    /// - floorPrice: 절대로 내려가지 않을 최소 익절 보장선
    /// - 최종 스탑 = max(지표 기반 스탑, 3단계 스탑)
    /// </summary>
    public class AdvancedExitStopCalculator
    {
        private readonly ILogger _logger;

        public event Action<string>? OnLog;

        // 지표 신호 임계값 (설정 가능)
        public double RsiOverboughtLevel { get; set; } = 75.0;
        public double RsiExtremeLevel { get; set; } = 80.0;
        public decimal FiboFloorRatio { get; set; } = 0.85m;  // Fibo 1.618을 최소선으로 설정할 때 사용

        public AdvancedExitStopCalculator()
        {
            _logger = Log.ForContext<AdvancedExitStopCalculator>();
        }

        /// <summary>
        /// [메인 로직] 5개 지표를 결합하여 동적 익절 스탑 계산
        /// 
        /// ROE 20% 이상에서만 활용되며, 기존 3단계 스탑과의 max() 비교로 최종 결정
        /// </summary>
        public AdvancedExitSignal CalculateAdvancedExitStop(
            decimal stage3StopPrice,    // 기존 3단계 스탑 (절대값)
            TechnicalData tech,
            bool isLong)
        {
            var signal = new AdvancedExitSignal();

            // [1단계] 과열 감지 (엘리엇 5파동 + RSI 과매수)
            double tightModifier = 1.0;
            decimal floorPrice = stage3StopPrice;

            if (tech.IsWave5 || tech.IsRsiExtreme)
            {
                tightModifier *= 0.4;  // 스탑 간격을 60% 좁힘
                signal.ActiveSignals.Add(isLong ? "🔥 엘리엇 5파동 + 극단 과매수" : "🔥 엘리엇 5파동 + 극단 과매도");
                LogSignal("🔥 [과열 감지] 엘리엇 5파동 또는 극단적 RSI 신호. 스탑 라인을 가격에 초밀착.", isLong);
            }

            // [2단계] MACD 모멘텀 약화 (히스토그램 감소 + 데드크로스 임박)
            if (tech.IsMacdHistogramDecreasing || tech.IsMacdDeadCross)
            {
                tightModifier *= 0.7;  // 추격 간격을 30% 더 축소
                signal.ActiveSignals.Add("📉 MACD 히스토그램 감소");
                LogSignal("📉 [모멘텀 약화] MACD 히스토그램이 감소 중. 상승 힘이 빠지는 신호.", isLong);
            }

            // [3단계] 피보나치 확장 레벨 (수익 보장선 상향)
            if (tech.IsFibo1618Hit)
            {
                floorPrice = Math.Max(floorPrice, tech.Fibo1618);
                signal.ActiveSignals.Add("📊 피보나치 1.618 도달");
                LogSignal($"📊 [수익 보장] 피보나치 1.618 레벨({tech.Fibo1618:F8})에 도달. 이 가격 이상으로 스탑 고정.", isLong);
            }

            // [4단계] 볼린저 밴드 회귀 (상단 이탈 후 안으로 복귀 = 익절 신호)
            if (tech.IsReturningToMidBand)
            {
                signal.ShouldTakeProfitNow = true;
                signal.ActiveSignals.Add("⚠️ BB 상단 회귀");
                LogSignal("⚠️ [BB 회귀] 밴드 상단을 탈출하고 안으로 복귀. 즉시 익절 추천!", isLong);
                
                // BB 회귀 시 현재가 바짝 붙여서 즉시 익절
                signal.RecommendedStopPrice = tech.CurrentPrice;
                signal.TightModifier = tightModifier;
                return signal;
            }

            // [5단계] RSI 과매수 상태 (극단은 아니지만 70 초과)
            if (tech.IsRsiOverbought && !tech.IsRsiExtreme)
            {
                tightModifier *= 0.85;  // 약간만 좁힘 (15%)
                signal.ActiveSignals.Add("🔴 RSI 과매수");
                LogSignal($"🔴 [과매수] RSI {tech.Rsi:F2}. 이미 과매수 구간.", isLong);
            }

            // [최종 계산] ATR 기반 트레일링 스탑
            decimal trailDistance = (tech.Atr * tech.AtrMultiplier) * (decimal)tightModifier;
            decimal calculatedStop = isLong 
                ? tech.HighestPrice - trailDistance  // 롱: 최고가 - 거리
                : tech.HighestPrice + trailDistance; // 숏: 최고가 + 거리

            // 플로어 가격(최소 보장선) 확인
            if (isLong)
            {
                if (calculatedStop < floorPrice)
                {
                    signal.ActiveSignals.Add("🛡️ 최소 보장선 활성화");
                    calculatedStop = floorPrice;
                }
            }
            else
            {
                if (calculatedStop > floorPrice)
                {
                    signal.ActiveSignals.Add("🛡️ 최소 보장선 활성화");
                    calculatedStop = floorPrice;
                }
            }

            signal.RecommendedStopPrice = calculatedStop;
            signal.TightModifier = tightModifier;

            return signal;
        }

        /// <summary>
        /// ROE 20% 도달 시 "분할 익절" 로직
        /// - 50% 즉시 시장가 익절
        /// - 50% 남겨두고 지표 기반 트레일링 적용
        /// </summary>
        public (bool ShouldExecutePartial, decimal PartialQuantity) EvaluatePartialExit(
            decimal positionQuantity,
            decimal currentPrice,
            double currentRoe,
            bool isLong)
        {
            // ROE 20% 도달 시 분할 익절 추천
            if (Math.Abs(currentRoe - 20.0) < 0.1)  // ROE가 20% 근처일 때
            {
                decimal halfQuantity = positionQuantity / 2;
                LogSignal($"💰 [분할 익절] ROE 20% 도달. 50% 즉시 익절 ({halfQuantity:F8}). 나머지 50%는 지표 추격.", isLong);
                return (true, halfQuantity);
            }

            return (false, 0m);
        }

        /// <summary>
        /// BB 상단 돌파 후 복귀 확인
        /// 고점이 상단 밴드 위에 있었고, 현재가가 상단 밴드 아래로 돌아온 상태
        /// </summary>
        public bool IsBollingerReversalSignal(TechnicalData tech)
        {
            return tech.HighWasAboveUpperBand && tech.CurrentPrice < tech.UpperBand;
        }

        /// <summary>
        /// 종합 익절 신호 평가
        /// 여러 신호가 동시에 나타나면 더욱 강력한 익절 신호
        /// </summary>
        public bool ShouldExecuteImmediateExit(TechnicalData tech, AdvancedExitSignal signal)
        {
            // 다음 조건 중 하나라도 만족하면 즉시 익절
            return signal.ShouldTakeProfitNow ||                    // BB 회귀
                   (tech.IsReturningToMidBand) ||                  // BB 명시적 회귀
                   (signal.ActiveSignals.Count >= 3);              // 3개 이상 신호 동시발생
        }

        private void LogSignal(string message, bool isLong)
        {
            _logger.Information($"[{(isLong ? "롱" : "숏")}] {message}");
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}
