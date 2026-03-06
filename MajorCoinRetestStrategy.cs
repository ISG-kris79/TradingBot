using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// 메이저 코인(BTC, ETH, SOL, XRP) 전용 통합 전략
    /// 
    /// [전략 A] EMA 20 눌림목 진입 (안정적 추세 추종)
    /// - 장기 정배열 + EMA 20 근접 + RSI 지지 확인
    /// - 손절: EMA 50 이탈 또는 피보나치 0.618 (약 -1.2%)
    /// - 익절: RSI 75 도달 또는 ROE 20%
    /// 
    /// [전략 B] 숏 스퀴즈 감지 (폭발적 수익 구간)
    /// - 가격 상승 + OI 감소 + 청산 급증
    /// - 손절: 직전 1분봉 저점 (타이트)
    /// - 익절: RSI 85~90 또는 ROE 50~100%
    /// 
    /// 20배 레버리지 최적화: 메이저는 지지선이 뚫리면 하방 압력이 거세므로
    /// 칼손절과 본절가 스위칭이 생명입니다.
    /// </summary>
    public class MajorCoinRetestStrategy
    {
        private readonly ILogger _logger;
        
        public event Action<string>? OnLog;

        // 메이저 코인 리스트
        private static readonly HashSet<string> MajorCoins = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"
        };

        // EMA 20 눌림목 설정 (엄격화: 어설픈 자리 진입 방지)
        public double Ema20DeviationThreshold { get; set; } = 0.0012;  // ±0.12% (기존 0.2% → 더 정확한 터치만 허용)
        public double RsiSupportMin { get; set; } = 48.0;  // RSI 48 이상 (기존 45 → 더 강한 지지 요구)
        public double VolumeCorrectionMax { get; set; } = 0.85;  // 조정 구간 거래량 (기존 1.0 → 더 확실한 저볼륨 조정)

        // 숏 스퀴즈 설정
        public decimal PriceChangeThreshold { get; set; } = 0.3m;   // 5분 상승 0.3%
        public decimal OiDecreaseThreshold { get; set; } = -0.8m;   // OI 감소 0.8%
        public double SqueezeScoreThreshold { get; set; } = 75.0;   // 스퀴즈 점수
        public double SqueezeVolumeMultiplier { get; set; } = 1.5;  // 거래량 배수

        public MajorCoinRetestStrategy()
        {
            _logger = Log.ForContext<MajorCoinRetestStrategy>();
        }

        /// <summary>
        /// 메이저 코인 여부 확인
        /// </summary>
        public static bool IsMajorCoin(string symbol)
        {
            return MajorCoins.Contains(symbol);
        }

        /// <summary>
        /// [전략 A] EMA 20 눌림목 진입 로직
        /// </summary>
        public bool IsEMA20RetestEntry(MajorTechnicalData tech)
        {
            // [1] 장기 추세 확인 (1시간봉 기준 정배열)
            if (tech.Ema20_1h < tech.Ema50_1h)
            {
                LogDebug("❌ EMA 20 눌림목: 1시간봉 정배열 아님");
                return false;
            }

            // [2] 현재가가 EMA 20선에 충분히 접근했는지 확인 (이격도 ±0.2% 내외)
            decimal ema20 = tech.Ema20;
            decimal price = tech.CurrentPrice;
            
            if (ema20 == 0) return false;
            
            double deviation = Math.Abs((double)((price - ema20) / ema20));
            bool isTouchingEMA20 = deviation <= Ema20DeviationThreshold;

            if (!isTouchingEMA20)
            {
                LogDebug($"❌ EMA 20 눌림목: 이격도 {deviation:P2} > {Ema20DeviationThreshold:P2}");
                return false;
            }

            // [3] 지지 확인 (RSI가 과매도 근처에서 고개를 드는가?)
            bool isSupporting = tech.Rsi >= RsiSupportMin && tech.IsRsiUptrend;

            if (!isSupporting)
            {
                LogDebug($"❌ EMA 20 눌림목: RSI 지지 미확인 (RSI: {tech.Rsi:F2}, 추세: {tech.IsRsiUptrend})");
                return false;
            }

            // [4] 거래량 감소 확인 (조정 구간에서는 거래량이 줄어야 함)
            bool isLowVolumeCorrection = tech.VolumeRatio < VolumeCorrectionMax;

            if (!isLowVolumeCorrection)
            {
                LogDebug($"❌ EMA 20 눌림목: 거래량 과다 (비율: {tech.VolumeRatio:F2})");
                return false;
            }

            // [5] 저점 상승 패턴 확인 (선택적 가산점 → 엄격모드: 필수)
            if (!tech.IsMakingHigherLows)
            {
                LogDebug("❌ EMA 20 눌림목: 저점 상승 패턴 미확인 (Higher Lows 필요)");
                return false;
            }

            // [6] 현재가가 EMA20 아래에서 접근하는 경우만 허용 (위에서 떨어지는 하방 터치 차단)
            if (price > ema20)
            {
                // 위에서 내려오는 경우 이격도 더 엄격 적용 (0.06% 이내만)
                if (deviation > Ema20DeviationThreshold * 0.5)
                {
                    LogDebug($"❌ EMA 20 눌림목: 상방에서 접근 중 이격 과다 ({deviation:P2})");
                    return false;
                }
            }

            LogInfo($"✅ [EMA 20 눌림목] 진입 조건 충족! EMA20=${ema20:F2}, RSI={tech.Rsi:F2}, 거래량={tech.VolumeRatio:F2}x, HigherLows={tech.IsMakingHigherLows}");
            return true;
        }

        /// <summary>
        /// [전략 B] 숏 스퀴즈 감지 로직
        /// </summary>
        public SqueezeResult CalculateShortSqueezeScore(MajorMarketData market, MajorTechnicalData tech)
        {
            var result = new SqueezeResult();
            double score = 0;

            // [1] 미결제약정(OI) 변화 분석
            // 가격은 오르는데 OI가 급격히 줄어든다면 숏 포지션이 청산되고 있다는 증거
            if (market.PriceChange_5m > PriceChangeThreshold && market.OiChange_5m < OiDecreaseThreshold)
            {
                score += 40;
                result.Signals.Add($"💥 OI 급감 (가격+{market.PriceChange_5m:P2}, OI{market.OiChange_5m:P2})");
                LogInfo($"💥 숏 청산 신호: 가격 상승 {market.PriceChange_5m:P2} + OI 감소 {market.OiChange_5m:P2}");
            }

            // [2] 청산 데이터 확인 (최근 1분 내 숏 청산액 급증)
            if (market.RecentShortLiquidationUsdt > market.AvgLiquidation * 3)
            {
                score += 30;
                result.Signals.Add($"🔥 숏 청산 급증 (${market.RecentShortLiquidationUsdt:N0})");
                LogInfo($"🔥 숏 청산 급증: ${market.RecentShortLiquidationUsdt:N0} (평균 대비 3배)");
            }

            // [3] 펀딩비 분석 (펀딩비가 급격히 낮아지거나 마이너스로 갈 때)
            if (market.FundingRate < 0.01m)
            {
                score += 20;
                result.Signals.Add($"📉 낮은 펀딩비 ({market.FundingRate:P4})");
                LogInfo($"📉 펀딩비 낮음: {market.FundingRate:P4} (숏 포지션 과도)");
            }

            // [4] 볼린저 밴드 상단 돌파 (변동성 폭발 시작)
            if (tech.CurrentPrice > tech.UpperBand)
            {
                score += 10;
                result.Signals.Add("📈 BB 상단 돌파");
                LogDebug("📈 볼린저 밴드 상단 돌파 (변동성 증가)");
            }

            // [5] 거래량 급증 확인 (필수)
            bool isHighVolumeBurst = tech.VolumeRatio > SqueezeVolumeMultiplier;
            if (isHighVolumeBurst)
            {
                score += 10;
                result.Signals.Add($"🚀 거래량 급증 ({tech.VolumeRatio:F2}x)");
            }
            else
            {
                // 거래량 없으면 스퀴즈 아님
                score *= 0.5;
                result.Signals.Add("⚠️ 거래량 부족");
            }

            result.Score = score;
            result.IsSqueezeDetected = score >= SqueezeScoreThreshold;

            if (result.IsSqueezeDetected)
            {
                LogInfo($"🚀 [숏 스퀴즈 감지] 점수: {score:F0}/100");
            }

            return result;
        }

        /// <summary>
        /// 메이저 코인 통합 진입 평가
        /// </summary>
        public MajorEntryDecision EvaluateEntry(
            string symbol,
            MajorTechnicalData tech,
            MajorMarketData market)
        {
            var decision = new MajorEntryDecision { Symbol = symbol };

            if (!IsMajorCoin(symbol))
            {
                decision.Reason = "메이저 코인 아님";
                return decision;
            }

            // [전략 A] EMA 20 눌림목 진입 (안정적 수익)
            bool isRetestEntry = IsEMA20RetestEntry(tech);
            
            if (isRetestEntry)
            {
                decision.ShouldEnter = true;
                decision.EntryType = EntryType.EMA20_Retest;
                decision.PositionSizeMultiplier = 1.0;  // 기본 비중
                decision.Reason = "EMA 20 눌림목 지지 확인";
                decision.StopLossType = "EMA 50 이탈 또는 Fib 0.618";
                decision.TakeProfitTarget = "RSI 75 또는 ROE 20%";
                
                LogInfo($"✅ [{symbol}] EMA 20 눌림목 진입 승인 (안정형)");
                return decision;
            }

            // [전략 B] 숏 스퀴즈 포착 진입 (폭발적 수익)
            var squeezeResult = CalculateShortSqueezeScore(market, tech);
            
            if (squeezeResult.IsSqueezeDetected)
            {
                decision.ShouldEnter = true;
                decision.EntryType = EntryType.ShortSqueeze;
                decision.PositionSizeMultiplier = 1.5;  // 비중 1.5배 확대
                decision.Reason = $"숏 스퀴즈 감지 (점수: {squeezeResult.Score:F0})";
                decision.StopLossType = "직전 1분봉 저점 (타이트)";
                decision.TakeProfitTarget = "RSI 85~90 또는 ROE 50~100%";
                decision.SqueezeSignals = squeezeResult.Signals;
                
                LogInfo($"🚀 [{symbol}] 숏 스퀴즈 진입 승인! (공격형, 비중 1.5배)");
                LogInfo($"   신호: {string.Join(", ", squeezeResult.Signals)}");
                return decision;
            }

            // 두 조건 모두 미달
            decision.Reason = "EMA 20 눌림목 미형성 & 숏 스퀴즈 미감지";
            return decision;
        }

        /// <summary>
        /// 손절가 계산 (전략별 차별화)
        /// </summary>
        public decimal CalculateStopLoss(
            EntryType entryType,
            decimal entryPrice,
            MajorTechnicalData tech,
            bool isLong)
        {
            if (entryType == EntryType.EMA20_Retest)
            {
                // EMA 20 눌림목 진입: EMA 50 이탈 시 손절
                decimal stopLoss = isLong 
                    ? tech.Ema50 * 0.998m  // EMA 50 아래 0.2%
                    : tech.Ema50 * 1.002m;
                
                // 최대 손실 1.2% 제한 (20배 레버리지 = ROE -24%)
                decimal maxLoss = isLong
                    ? entryPrice * 0.988m
                    : entryPrice * 1.012m;
                
                return isLong 
                    ? Math.Max(stopLoss, maxLoss)
                    : Math.Min(stopLoss, maxLoss);
            }
            else // ShortSqueeze
            {
                // 숏 스퀴즈 진입: 직전 1분봉 저점 (타이트)
                // 더 공격적인 손절: 0.5% (20배 레버리지 = ROE -10%)
                return isLong
                    ? entryPrice * 0.995m
                    : entryPrice * 1.005m;
            }
        }

        /// <summary>
        /// 익절가 계산 (전략별 차별화)
        /// </summary>
        public decimal CalculateTakeProfit(
            EntryType entryType,
            decimal entryPrice,
            bool isLong)
        {
            if (entryType == EntryType.EMA20_Retest)
            {
                // 안정형: +1% (20배 레버리지 = ROE +20%)
                return isLong
                    ? entryPrice * 1.01m
                    : entryPrice * 0.99m;
            }
            else // ShortSqueeze
            {
                // 공격형: +2% (20배 레버리지 = ROE +40%, 하지만 트레일링으로 더 끌고 감)
                return isLong
                    ? entryPrice * 1.02m
                    : entryPrice * 0.98m;
            }
        }

        private void LogInfo(string message)
        {
            _logger.Information(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void LogDebug(string message)
        {
            _logger.Debug(message);
        }
    }

    /// <summary>
    /// 기술 지표 데이터 (메이저 코인 전용)
    /// </summary>
    public class MajorTechnicalData
    {
        public decimal CurrentPrice { get; set; }
        
        // EMA
        public decimal Ema20 { get; set; }
        public decimal Ema50 { get; set; }
        public decimal Ema20_1h { get; set; }  // 1시간봉 EMA 20
        public decimal Ema50_1h { get; set; }  // 1시간봉 EMA 50
        
        // RSI
        public double Rsi { get; set; }
        public bool IsRsiUptrend { get; set; }
        
        // 볼린저 밴드
        public decimal UpperBand { get; set; }
        public decimal LowerBand { get; set; }
        
        // 거래량
        public double VolumeRatio { get; set; }
        
        // 저점 상승 패턴
        public bool IsMakingHigherLows { get; set; }
    }

    /// <summary>
    /// 시장 데이터 (OI, 청산, 펀딩비) (메이저 코인 전용)
    /// </summary>
    public class MajorMarketData
    {
        // 가격 변화
        public decimal PriceChange_5m { get; set; }  // 5분 변화율
        
        // 미결제약정(Open Interest)
        public decimal OiChange_5m { get; set; }  // 5분 OI 변화율
        
        // 청산 데이터
        public double RecentShortLiquidationUsdt { get; set; }  // 최근 1분 숏 청산액
        public double AvgLiquidation { get; set; }  // 평균 청산액
        
        // 펀딩비
        public decimal FundingRate { get; set; }
        
        // 호가창
        public double OrderBookRatio { get; set; }  // 매수호가/매도호가
    }

    /// <summary>
    /// 숏 스퀴즈 결과
    /// </summary>
    public class SqueezeResult
    {
        public double Score { get; set; }
        public bool IsSqueezeDetected { get; set; }
        public List<string> Signals { get; set; } = new();
    }

    /// <summary>
    /// 메이저 코인 진입 결정
    /// </summary>
    public class MajorEntryDecision
    {
        public string Symbol { get; set; } = "";
        public bool ShouldEnter { get; set; }
        public EntryType EntryType { get; set; }
        public double PositionSizeMultiplier { get; set; } = 1.0;
        public string Reason { get; set; } = "";
        public string StopLossType { get; set; } = "";
        public string TakeProfitTarget { get; set; } = "";
        public List<string> SqueezeSignals { get; set; } = new();
    }

    /// <summary>
    /// 진입 유형
    /// </summary>
    public enum EntryType
    {
        None,
        EMA20_Retest,  // EMA 20 눌림목
        ShortSqueeze   // 숏 스퀴즈
    }
}
