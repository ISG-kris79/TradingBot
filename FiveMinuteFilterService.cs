using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// [6단계] 5분봉 필터 - 유연한 가중치 설계
    /// 
    /// 필수 조건 (3개): 모두 통과해야 함
    /// 1. Fib 범위 (0.382 ~ 0.618)
    /// 2. RSI < 80
    /// 3. 호가창 비율 (매도호가 / 매수호가 < 1.5)
    /// 
    /// 선택 조건 (3개 중 2개): 유연성 확보
    /// 1. BB 중단선 근처 (±5%)
    /// 2. MACD >= 0
    /// 3. RSI 상승 추세 (최근 5봉)
    /// 
    /// 이 설계로 계단식 상승장에서도 진입 기회를 확보합니다.
    /// </summary>
    public class FiveMinuteFilterService
    {
        private readonly ILogger _logger;
        
        public event Action<string>? OnLog;

        // 필수 조건 임계값
        public double RsiMaxLevel { get; set; } = 80.0;
        public double OrderBookRatioMax { get; set; } = 1.5;  // 매도호가/매수호가
        public decimal FibMinLevel { get; set; } = 0.382m;
        public decimal FibMaxLevel { get; set; } = 0.618m;
        
        // 선택 조건 임계값
        public double BbMidBandTolerancePct { get; set; } = 5.0;  // ±5%
        public int RsiTrendPeriod { get; set; } = 5;  // RSI 상승 추세 확인 기간

        public FiveMinuteFilterService()
        {
            _logger = Log.ForContext<FiveMinuteFilterService>();
        }

        /// <summary>
        /// 5분봉 필터 검증 (필수 3개 + 선택 2/3)
        /// </summary>
        public FilterResult EvaluateFilter(FilterInput input)
        {
            var result = new FilterResult { Symbol = input.Symbol };

            // ================== 필수 조건 검증 ==================
            
            // [필수 1] Fib 범위 체크
            bool fibPass = input.CurrentPrice >= input.FibLevel382 &&
                          input.CurrentPrice <= input.FibLevel618;
            result.MandatoryChecks.Add(new CheckResult
            {
                Name = "Fib 범위 (0.382~0.618)",
                Passed = fibPass,
                Value = $"${input.CurrentPrice:F2} ({GetFibPosition(input)})"
            });

            // [필수 2] RSI < 80 체크
            bool rsiPass = input.Rsi < RsiMaxLevel;
            result.MandatoryChecks.Add(new CheckResult
            {
                Name = "RSI < 80",
                Passed = rsiPass,
                Value = $"{input.Rsi:F2}"
            });

            // [필수 3] 호가창 비율 체크
            double orderBookRatio = input.AskVolume > 0 
                ? input.BidVolume / input.AskVolume 
                : 0.0;
            bool orderBookPass = orderBookRatio < OrderBookRatioMax;
            result.MandatoryChecks.Add(new CheckResult
            {
                Name = "호가창 비율 < 1.5",
                Passed = orderBookPass,
                Value = $"{orderBookRatio:F2}"
            });

            // 필수 조건 중 하나라도 실패하면 즉시 반환
            if (!fibPass || !rsiPass || !orderBookPass)
            {
                result.Passed = false;
                result.Reason = "필수 조건 미달";
                LogWarning($"❌ [{input.Symbol}] 필수 조건 미달: Fib={fibPass}, RSI={rsiPass}, 호가창={orderBookPass}");
                return result;
            }

            // ================== 선택 조건 검증 (3개 중 2개) ==================
            
            int optionalPassCount = 0;

            // [선택 1] BB 중단선 근처 (±5%)
            double bbMidDistance = Math.Abs((double)((input.CurrentPrice - input.BbMidBand) / input.BbMidBand)) * 100;
            bool bbMidPass = bbMidDistance <= BbMidBandTolerancePct;
            result.OptionalChecks.Add(new CheckResult
            {
                Name = "BB 중단선 근처 (±5%)",
                Passed = bbMidPass,
                Value = $"{bbMidDistance:F2}% 차이"
            });
            if (bbMidPass) optionalPassCount++;

            // [선택 2] MACD >= 0
            bool macdPass = input.MacdHistogram >= 0;
            result.OptionalChecks.Add(new CheckResult
            {
                Name = "MACD >= 0",
                Passed = macdPass,
                Value = $"{input.MacdHistogram:F4}"
            });
            if (macdPass) optionalPassCount++;

            // [선택 3] RSI 상승 추세 (최근 5봉)
            bool rsiTrendPass = IsRsiRising(input.RsiHistory);
            result.OptionalChecks.Add(new CheckResult
            {
                Name = "RSI 상승 추세",
                Passed = rsiTrendPass,
                Value = rsiTrendPass ? "상승" : "하락/횡보"
            });
            if (rsiTrendPass) optionalPassCount++;

            // 선택 조건 3개 중 2개 이상 통과 필요
            result.OptionalPassCount = optionalPassCount;
            result.Passed = optionalPassCount >= 2;

            if (result.Passed)
            {
                LogInfo($"✅ [{input.Symbol}] 5분봉 필터 통과! 필수 3개 + 선택 {optionalPassCount}/3개 충족.");
            }
            else
            {
                result.Reason = $"선택 조건 부족 ({optionalPassCount}/3, 최소 2개 필요)";
                LogWarning($"⚠️ [{input.Symbol}] 선택 조건 부족: {optionalPassCount}/3개만 통과. 최소 2개 필요.");
            }

            return result;
        }

        /// <summary>
        /// RSI 상승 추세 확인 (최근 5개 데이터 포인트)
        /// </summary>
        private bool IsRsiRising(List<double> rsiHistory)
        {
            if (rsiHistory == null || rsiHistory.Count < RsiTrendPeriod)
                return false;

            var recent = rsiHistory.TakeLast(RsiTrendPeriod).ToList();
            
            // 연속 상승 확인 (최소 3개 이상 상승)
            int risingCount = 0;
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i] > recent[i - 1])
                    risingCount++;
            }

            return risingCount >= 3;
        }

        /// <summary>
        /// Fib 위치 표시 (로깅용)
        /// </summary>
        private string GetFibPosition(FilterInput input)
        {
            if (input.CurrentPrice < input.FibLevel382)
                return "< 0.382";
            if (input.CurrentPrice > input.FibLevel618)
                return "> 0.618";
            
            decimal range = input.FibLevel618 - input.FibLevel382;
            if (range == 0) return "0.382~0.618";
            
            decimal position = (input.CurrentPrice - input.FibLevel382) / range;
            double fibLevel = 0.382 + (double)position * (0.618 - 0.382);
            return $"≈{fibLevel:F3}";
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

        /// <summary>
        /// ═══════════════════════════════════════════════════════
        /// [메이저 코인 전용] 6단계 컨플루언스 필터
        /// ═══════════════════════════════════════════════════════
        /// 
        /// BTC, ETH, SOL, XRP 전용 필터:
        /// - 전략 A: EMA 20 눌림목 지지 (안정형)
        /// - 전략 B: 숏 스퀴즈 포착 (공격형)
        /// 
        /// 메이저 코인은 추세가 쉽게 꺾이지 않지만,
        /// 개미를 털기 위한 눌림목이 반드시 존재합니다.
        /// </summary>
        public FilterResult EvaluateMajorCoinFilter(
            FilterInput input,
            MajorTechnicalData tech,
            MajorMarketData market)
        {
            var result = new FilterResult { Symbol = input.Symbol };

            // 메이저 코인 여부 확인
            if (!MajorCoinRetestStrategy.IsMajorCoin(input.Symbol))
            {
                result.Passed = false;
                result.Reason = "메이저 코인 아님";
                return result;
            }

            // ================== [공통 필수 조건] ==================
            
            // [필수 1] RSI 하드캡 (과매수 끝단 진입 방지)
            if (tech.Rsi >= 80)
            {
                result.Passed = false;
                result.Reason = "RSI 과매수 (≥80)";
                result.MandatoryChecks.Add(new CheckResult
                {
                    Name = "RSI < 80",
                    Passed = false,
                    Value = $"{tech.Rsi:F2}"
                });
                LogWarning($"❌ [{input.Symbol}] RSI 과매수: {tech.Rsi:F2}");
                return result;
            }

            result.MandatoryChecks.Add(new CheckResult
            {
                Name = "RSI < 80",
                Passed = true,
                Value = $"{tech.Rsi:F2}"
            });

            // [필수 2] 호가창 기반 (매수벽 미달 시 탈락)
            bool orderBookPass = market.OrderBookRatio >= 1.2;
            result.MandatoryChecks.Add(new CheckResult
            {
                Name = "호가창 비율 ≥ 1.2",
                Passed = orderBookPass,
                Value = $"{market.OrderBookRatio:F2}"
            });

            if (!orderBookPass)
            {
                result.Passed = false;
                result.Reason = "매수벽 부족";
                LogWarning($"❌ [{input.Symbol}] 호가창 비율 부족: {market.OrderBookRatio:F2}");
                return result;
            }

            // ================== [전략 A] EMA 20 눌림목 지지 (안정형) ==================
            
            double emaDeviation = tech.Ema20 > 0 
                ? Math.Abs((double)((tech.CurrentPrice - tech.Ema20) / tech.Ema20))
                : 1.0;
            
            bool isEmaRetest = emaDeviation <= 0.002;  // EMA 20선 기준 ±0.2% 근접
            bool isBullishSupport = tech.IsMakingHigherLows && tech.Rsi > 45;  // 저점 높이며 지지
            
            result.OptionalChecks.Add(new CheckResult
            {
                Name = "EMA 20 근접 (±0.2%)",
                Passed = isEmaRetest,
                Value = $"{emaDeviation:P2}"
            });

            result.OptionalChecks.Add(new CheckResult
            {
                Name = "저점 상승 + RSI 지지",
                Passed = isBullishSupport,
                Value = $"저점상승={tech.IsMakingHigherLows}, RSI={tech.Rsi:F2}"
            });

            // ================== [전략 B] 숏 스퀴즈 포착 (공격형) ==================
            
            // 가격 상승 중 OI(미결제약정) 감소는 숏 포지션의 청산 신호
            bool isShortSqueeze = market.PriceChange_5m > 0.3m && market.OiChange_5m < -0.8m;
            bool isHighVolumeBurst = tech.VolumeRatio > 1.5;  // 거래량 동반 확인
            
            result.OptionalChecks.Add(new CheckResult
            {
                Name = "숏 스퀴즈 (가격↑ OI↓)",
                Passed = isShortSqueeze,
                Value = $"가격{market.PriceChange_5m:P2}, OI{market.OiChange_5m:P2}"
            });

            result.OptionalChecks.Add(new CheckResult
            {
                Name = "거래량 급증 (>1.5x)",
                Passed = isHighVolumeBurst,
                Value = $"{tech.VolumeRatio:F2}x"
            });

            // ================== [최종 판정] ==================
            
            // [전략 A 통과] EMA 20 눌림목
            if (isEmaRetest && isBullishSupport)
            {
                result.Passed = true;
                result.Reason = "EMA 20 눌림목 지지 확인 (안정형)";
                result.OptionalPassCount = 2;
                LogInfo($"✅ [{input.Symbol}] [EMA 20 눌림목] 메이저 추세 지속 확인 - 진입 승인");
                return result;
            }

            // [전략 B 통과] 숏 스퀴즈
            if (isShortSqueeze && isHighVolumeBurst)
            {
                result.Passed = true;
                result.Reason = "숏 스퀴즈 감지 (공격형)";
                result.OptionalPassCount = 2;
                LogInfo($"🚀 [{input.Symbol}] [숏 스퀴즈] 청산 물량 동반 분출 확인 - 공격적 진입 승인");
                return result;
            }

            // 두 조건 모두 미달
            result.Passed = false;
            result.Reason = "EMA 20 눌림목 미형성 & 숏 스퀴즈 미감지";
            result.OptionalPassCount = 0;
            
            LogWarning($"❌ [{input.Symbol}] 메이저 필터 미달: 눌림목={isEmaRetest && isBullishSupport}, 스퀴즈={isShortSqueeze && isHighVolumeBurst}");
            
            return result;
        }
    }

    /// <summary>
    /// 필터 입력 데이터
    /// </summary>
    public class FilterInput
    {
        public string Symbol { get; set; } = "";
        public decimal CurrentPrice { get; set; }
        
        // 필수 조건 데이터
        public decimal FibLevel382 { get; set; }
        public decimal FibLevel618 { get; set; }
        public double Rsi { get; set; }
        public double BidVolume { get; set; }  // 매수호가 총량
        public double AskVolume { get; set; }  // 매도호가 총량
        
        // 선택 조건 데이터
        public decimal BbMidBand { get; set; }
        public double MacdHistogram { get; set; }
        public List<double> RsiHistory { get; set; } = new();
    }

    /// <summary>
    /// 필터 결과
    /// </summary>
    public class FilterResult
    {
        public string Symbol { get; set; } = "";
        public bool Passed { get; set; }
        public string Reason { get; set; } = "";
        
        public List<CheckResult> MandatoryChecks { get; set; } = new();
        public List<CheckResult> OptionalChecks { get; set; } = new();
        public int OptionalPassCount { get; set; }
    }

    /// <summary>
    /// 개별 체크 결과
    /// </summary>
    public class CheckResult
    {
        public string Name { get; set; } = "";
        public bool Passed { get; set; }
        public string Value { get; set; } = "";
    }
}
