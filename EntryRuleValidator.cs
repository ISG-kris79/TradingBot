using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot
{
    /// <summary>
    /// 진입 규칙 검증기 (거부 필터)
    /// 
    /// **핵심 철학:**
    /// - 엘리엇 파동: 주관적 해석이므로 '진입하면 안 되는 자리'를 걸러내는 필터로만 사용
    /// - 피보나치: 객관적 수치이므로 AI 특징값으로 사용
    /// - 거래량: 캔들 패턴과 결합하여 진입 타이밍 검증
    /// 
    /// AI가 승인해도 이 필터를 통과하지 못하면 진입 불가
    /// </summary>
    public class EntryRuleValidator
    {
        private readonly DoubleCheckConfig _config;

        public EntryRuleValidator(DoubleCheckConfig? config = null)
        {
            _config = config ?? new DoubleCheckConfig();
        }

        /// <summary>
        /// 진입 규칙 검증 (거부 필터)
        /// </summary>
        public (bool passed, string reason) ValidateEntryRules(
            List<IBinanceKline> candles,
            string symbol,
            decimal currentPrice,
            PositionSide side,
            float mlScore,
            float tfScore,
            float rsi,
            float bbPosition,
            out ElliottWaveState waveState,
            out FibonacciLevels fibLevels)
        {
            waveState = new ElliottWaveState();
            fibLevels = new FibonacciLevels();

            if (candles == null || candles.Count < 50)
                return (false, "Insufficient_Candle_Data");

            try
            {
                // 1. 엘리엇 파동 규칙 위배 체크 (거부 조건)
                var elliottCheck = CheckElliottWaveRules(candles, currentPrice, side, out waveState);
                if (!elliottCheck.passed)
                    return elliottCheck;

                // 2. 거래량 기반 캔들 패턴 검증
                var volumeCheck = CheckVolumePattern(candles, symbol, side, tfScore, bbPosition);
                if (!volumeCheck.passed)
                    return volumeCheck;

                // 3. 피보나치 레벨 계산
                fibLevels = CalculateFibonacciLevels(candles, currentPrice);

                // 4. 최종 점수/방향별 피보나치 체크
                var finalCheck = ApplyFinalDirectionalCheck(
                    currentPrice,
                    side,
                    mlScore,
                    tfScore,
                    rsi,
                    bbPosition,
                    waveState,
                    fibLevels);
                if (!finalCheck.passed)
                    return finalCheck;

                return (true, "All_Rules_Passed");
            }
            catch (Exception ex)
            {
                return (false, $"Validation_Error_{ex.Message}");
            }
        }

        /// <summary>
        /// 엘리엇 파동 규칙 위배 체크 (거부 조건)
        /// </summary>
        private (bool passed, string reason) CheckElliottWaveRules(
            List<IBinanceKline> candles,
            decimal currentPrice,
            PositionSide side,
            out ElliottWaveState state)
        {
            state = new ElliottWaveState();

            // 최근 스윙 고점/저점 탐지 (간단한 버전)
            var swings = DetectSwingPoints(candles);
            if (swings.Count < 3)
            {
                state.IsValid = false;
                return (true, "Elliott_Insufficient_Data"); // 데이터 부족은 거부 사유 아님
            }

            if (side == PositionSide.Long)
            {
                // 롱 진입 시 엘리엇 규칙 위배 체크
                // 가정: swings[0]=1파 시작, swings[1]=1파 고점, swings[2]=2파 저점

                if (swings.Count >= 4)
                {
                    decimal wave1Start = swings[0];
                    decimal wave1High = swings[1];
                    decimal wave2Low = swings[2];
                    decimal wave3Attempt = swings[3];

                    // **규칙 1: 2파가 1파 시작점 아래로 내려가면 무효 (절대 규칙)**
                    if (wave2Low <= wave1Start)
                    {
                        state.IsValid = false;
                        state.Rule1Violated = true;
                        state.RejectReason = "Wave2_Below_Wave1_Start";
                        return (false, "Elliott_Rule1_Violation_Wave2_Below_Wave1");
                    }

                    // **규칙 2: 4파가 1파 고점을 침범하면 무효 (절대 규칙)**
                    if (swings.Count >= 6)
                    {
                        decimal wave4Low = swings[4];
                        if (wave4Low <= wave1High)
                        {
                            state.IsValid = false;
                            state.Rule2Violated = true;
                            state.RejectReason = "Wave4_Overlap_Wave1";
                            return (false, "Elliott_Rule2_Violation_Wave4_Overlap");
                        }
                    }

                    // **규칙 3: 3파가 가장 짧으면 안 됨 (경고 수준)**
                    decimal wave1Length = wave1High - wave1Start;
                    decimal wave3Length = wave3Attempt - wave2Low;

                    if (wave3Length < wave1Length * 0.8m) // 3파가 1파의 80% 미만
                    {
                        state.Rule3Violated = true;
                        state.RejectReason = "Wave3_Too_Short";
                    }

                    state.IsValid = true;
                    state.Wave1Length = (double)wave1Length;
                    state.Wave2RetracePct = (double)((wave1High - wave2Low) / wave1Length);
                    state.Wave3Length = (double)wave3Length;
                }
            }
            else
            {
                // 숏 진입 시 (반대 로직)
                if (swings.Count >= 4)
                {
                    decimal wave1Start = swings[0];
                    decimal wave1Low = swings[1];
                    decimal wave2High = swings[2];

                    // **규칙 1: 2파가 1파 시작점 위로 올라가면 무효**
                    if (wave2High >= wave1Start)
                    {
                        state.IsValid = false;
                        state.Rule1Violated = true;
                        state.RejectReason = "Wave2_Above_Wave1_Start_Short";
                        return (false, "Elliott_Rule1_Violation_Short_Wave2");
                    }

                    state.IsValid = true;
                }
            }

            return (true, "Elliott_Rules_OK");
        }

        // ── [Staircase Uptrend 헬퍼] ─────────────────────────────────────────────────
        /// <summary>최근 n+1개 봉에서 연속 저점 상승(Higher Lows) 여부 판단</summary>
        private static bool HasSuccessiveHigherLows(List<IBinanceKline>? candles, int count = 3)
        {
            if (candles == null || candles.Count < count + 1) return false;
            var recent = candles.TakeLast(count + 1).ToList();
            for (int i = 1; i < recent.Count; i++)
                if (recent[i].LowPrice <= recent[i - 1].LowPrice) return false;
            return true;
        }

        /// <summary>
        /// 거래량 기반 캔들 패턴 검증
        /// </summary>
        private (bool passed, string reason) CheckVolumePattern(
            List<IBinanceKline> candles,
            string symbol,
            PositionSide side,
            float tfScore,
            float bbPosition)
        {
            if (candles.Count < 5)
                return (true, "Volume_Check_Skipped");

            var recentCandles = candles.TakeLast(5).ToList();
            var currentCandle = recentCandles.Last();

            // 평균 거래량 계산
            decimal avgVolume = recentCandles.Take(4).Average(c => c.Volume);
            if (avgVolume == 0)
                return (true, "Volume_Data_Unavailable");

            decimal currentVolume = currentCandle.Volume;
            decimal volumeRatio = currentVolume / avgVolume;

            // **규칙: 거래량이 평균의 70% 미만이면 신뢰도 낮음 (거부)**
            if (volumeRatio < (decimal)_config.LowVolumeRejectRatio)
            {
                // recentCandles를 함께 전달 → 계단식 상승 바이패스 판단에 사용
                var lowVolumeBypass = ShouldAllowLowVolumeBypass(symbol, side, tfScore, bbPosition, volumeRatio, currentCandle, recentCandles);
                if (!lowVolumeBypass.allowed)
                {
                    return (false, $"Low_Volume_Ratio={volumeRatio:F2}");
                }

                return (true, lowVolumeBypass.reason);
            }

            // **추가 규칙: 피보나치 진입 구간에서는 거래량이 실려야 함 (1.5배 이상)**
            // 이 부분은 피보나치 체크와 결합할 수 있지만 여기서는 간단히 구현
            bool hasVolumeConfirmation = volumeRatio >= 1.5m;

            // 망치/역망치 캔들 패턴 감지 (옵션)
            bool isHammerPattern = DetectHammerPattern(currentCandle);
            bool isInvertedHammerPattern = DetectInvertedHammerPattern(currentCandle);

            if (hasVolumeConfirmation && (isHammerPattern || isInvertedHammerPattern))
            {
                return (true, "Strong_Volume_With_Pattern");
            }

            return (true, "Volume_Check_Passed");
        }

        private (bool allowed, string reason) ShouldAllowLowVolumeBypass(
            string symbol,
            PositionSide side,
            float tfScore,
            float bbPosition,
            decimal volumeRatio,
            IBinanceKline currentCandle,
            List<IBinanceKline>? recentCandles = null)
        {
            // ── [Staircase Pursuit 바이패스] ────────────────────────────────────────────
            // 조건: LONG + BB 중단 위(>50%) + 3연속 Higher Lows + TF 기준 85%+ + 최소 거래량
            bool isStaircaseUptrend = side == PositionSide.Long
                && bbPosition > 0.5f
                && HasSuccessiveHigherLows(recentCandles, 3)
                && SanitizeScore(tfScore) >= _config.LowVolumeBypassTfThreshold * 0.94f  // ~85%
                && volumeRatio >= 0.1m; // 형님 요청: 계단식 상승 중 중심선 위면 LowVolumeRatio 0.1로 완화

            if (isStaircaseUptrend)
                return (true, $"Staircase_Uptrend_Bypass_Vol={volumeRatio:F2}_BB={bbPosition:P0}");

            // [V-Turn & Squeeze 가속 로직 - 모멘텀 우선주의] 볼륨 0.15 극단적 완화
            bool isSqueezeBreakout = side == PositionSide.Long
                && bbPosition >= 0.5f && bbPosition <= 1.05f 
                && SanitizeScore(tfScore) >= 0.85f 
                && volumeRatio >= 0.15m; // Pre-Breakout 모드 인정 20% 내외

            if (isSqueezeBreakout)
                return (true, $"V_Turn_Squeeze_Bypass_Vol={volumeRatio:F2}_BB={bbPosition:P0}");

            if (side != PositionSide.Long)
                return (false, "LowVolume_Bypass_Not_Long");

            if (!IsMajorSymbol(symbol))
                return (false, "LowVolume_Bypass_Not_Major");

            if (volumeRatio < (decimal)_config.LowVolumeBypassMinRatio)
                return (false, "LowVolume_Bypass_TooLowVolume");

            if (SanitizeScore(tfScore) < _config.LowVolumeBypassTfThreshold)
                return (false, "LowVolume_Bypass_WeakTF");

            float normalizedBb = Math.Clamp(bbPosition, 0f, 1f);
            if (normalizedBb < _config.LowVolumeBypassBbLower || normalizedBb > _config.LowVolumeBypassBbUpper)
                return (false, "LowVolume_Bypass_NotMidBand");

            decimal candleRange = currentCandle.HighPrice - currentCandle.LowPrice;
            if (candleRange <= 0)
                return (false, "LowVolume_Bypass_InvalidRange");

            decimal body = Math.Abs(currentCandle.ClosePrice - currentCandle.OpenPrice);
            decimal effectiveBody = body > 0m ? body : candleRange * 0.1m;
            decimal lowerWick = Math.Min(currentCandle.OpenPrice, currentCandle.ClosePrice) - currentCandle.LowPrice;

            bool bullishOrDoji = currentCandle.ClosePrice >= currentCandle.OpenPrice;
            bool hasSupportTail = lowerWick >= effectiveBody * (decimal)_config.LowVolumeBypassLowerWickBodyRatio;
            bool isHammerPattern = DetectHammerPattern(currentCandle);

            if (!bullishOrDoji)
                return (false, "LowVolume_Bypass_NotBullish");

            if (!hasSupportTail && !isHammerPattern)
                return (false, "LowVolume_Bypass_WeakSupportTail");

            return (true, $"LowVolume_Bypass_MajorMidBand_Ratio={volumeRatio:F2}");
        }

        private static bool IsMajorSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            return symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("SOL", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 피보나치 레벨 계산
        /// </summary>
        private FibonacciLevels CalculateFibonacciLevels(List<IBinanceKline> candles, decimal currentPrice)
        {
            var levels = new FibonacciLevels();

            // 최근 스윙 고점/저점 찾기
            var recent50 = candles.TakeLast(50).ToList();
            decimal highPrice = recent50.Max(c => c.HighPrice);
            decimal lowPrice = recent50.Min(c => c.LowPrice);
            decimal range = highPrice - lowPrice;

            if (range == 0)
                return levels;

            levels.High = highPrice;
            levels.Low = lowPrice;
            levels.Range = range;

            // 피보나치 되돌림 레벨
            levels.Fib0000 = highPrice;
            levels.Fib0236 = highPrice - range * 0.236m;
            levels.Fib0382 = highPrice - range * 0.382m;
            levels.Fib0500 = highPrice - range * 0.500m;
            levels.Fib0618 = highPrice - range * 0.618m;
            levels.Fib0786 = highPrice - range * 0.786m;
            levels.Fib1000 = lowPrice;

            // 확장 레벨
            levels.Fib1618 = highPrice + range * 0.618m; // 1.618 확장

            // 현재가가 어느 레벨에 근접한지 계산 (거리 %)
            levels.DistanceTo0382_Pct = (double)Math.Abs((currentPrice - levels.Fib0382) / currentPrice * 100m);
            levels.DistanceTo0618_Pct = (double)Math.Abs((currentPrice - levels.Fib0618) / currentPrice * 100m);
            levels.DistanceTo0786_Pct = (double)Math.Abs((currentPrice - levels.Fib0786) / currentPrice * 100m);

            // 현재가가 피보나치 진입 구간(0.382~0.618) 안에 있는지
            levels.InEntryZone = currentPrice >= levels.Fib0618 && currentPrice <= levels.Fib0382;

            return levels;
        }

        /// <summary>
        /// Rule3 감점 + 방향별 피보나치 극단 체크
        /// </summary>
        private (bool passed, string reason) ApplyFinalDirectionalCheck(
            decimal currentPrice,
            PositionSide side,
            float mlScore,
            float tfScore,
            float rsi,
            float bbPosition,
            ElliottWaveState waveState,
            FibonacciLevels levels)
        {
            float finalScore = SanitizeScore(mlScore);
            if (waveState.Rule3Violated)
            {
                finalScore -= _config.ElliottRule3Penalty;
            }

            finalScore = Math.Clamp(finalScore, 0f, 1f);
            waveState.FinalScore = finalScore;

            bool isSuperTrend = SanitizeScore(tfScore) >= _config.SuperTrendTfThreshold
                && SanitizeScore(mlScore) >= _config.SuperTrendMlThreshold;
            waveState.IsSuperTrend = isSuperTrend;

            float fibLevel = CalculateFibLevel(levels, currentPrice);
            waveState.FibLevel = fibLevel;

            if (!isSuperTrend)
            {
                if (side == PositionSide.Long && fibLevel >= _config.LongFibExtremeLevel)
                {
                    if (!(rsi <= _config.LongFibBlockRsi && bbPosition <= _config.LongFibBlockBbPosition))
                    {
                        return (false, $"Fibonacci_Long_FakeBottom_RSI={rsi:F1}_BB={bbPosition:P0}");
                    }
                }
                else if (side == PositionSide.Short && fibLevel <= _config.ShortFibExtremeLevel)
                {
                    if (rsi >= _config.ShortFibBlockRsi && bbPosition >= _config.ShortFibBlockBbPosition)
                    {
                        return (false, $"Fibonacci_Short_Extreme_Overbought_RSI={rsi:F1}_BB={bbPosition:P0}");
                    }
                }
            }

            if (finalScore < _config.RuleFilterFinalScoreThreshold)
            {
                return (false, waveState.Rule3Violated
                    ? $"Elliott_Rule3_Penalized_FinalScore={finalScore:F2}"
                    : $"FinalScore_Below_Threshold={finalScore:F2}");
            }

            return (true, "Final_Check_Passed");
        }

        private static float CalculateFibLevel(FibonacciLevels levels, decimal currentPrice)
        {
            if (levels.Range <= 0)
                return 0.5f;

            decimal fibLevel = (levels.High - currentPrice) / levels.Range;
            return (float)fibLevel;
        }

        private static float SanitizeScore(float score)
        {
            if (float.IsNaN(score) || float.IsInfinity(score))
                return 0f;

            return Math.Clamp(score, 0f, 1f);
        }

        /// <summary>
        /// 스윙 고점/저점 탐지 (단순 버전)
        /// </summary>
        private List<decimal> DetectSwingPoints(List<IBinanceKline> candles)
        {
            var swings = new List<decimal>();
            if (candles.Count < 3)
                return swings;

            for (int i = 1; i < candles.Count - 1; i++)
            {
                decimal prev = candles[i - 1].ClosePrice;
                decimal curr = candles[i].ClosePrice;
                decimal next = candles[i + 1].ClosePrice;

                // 고점
                if (curr > prev && curr > next)
                    swings.Add(curr);

                // 저점
                if (curr < prev && curr < next)
                    swings.Add(curr);
            }

            return swings.TakeLast(10).ToList(); // 최근 10개만
        }

        /// <summary>
        /// 망치 캔들 패턴 감지
        /// </summary>
        private bool DetectHammerPattern(IBinanceKline candle)
        {
            decimal body = Math.Abs(candle.ClosePrice - candle.OpenPrice);
            decimal lowerWick = Math.Min(candle.OpenPrice, candle.ClosePrice) - candle.LowPrice;
            decimal upperWick = candle.HighPrice - Math.Max(candle.OpenPrice, candle.ClosePrice);

            // 망치: 아래 꼬리가 몸통의 2배 이상, 위 꼬리는 짧음
            if (body > 0 && lowerWick >= body * 2m && upperWick <= body * 0.5m)
                return true;

            return false;
        }

        /// <summary>
        /// 역망치 캔들 패턴 감지
        /// </summary>
        private bool DetectInvertedHammerPattern(IBinanceKline candle)
        {
            decimal body = Math.Abs(candle.ClosePrice - candle.OpenPrice);
            decimal upperWick = candle.HighPrice - Math.Max(candle.OpenPrice, candle.ClosePrice);
            decimal lowerWick = Math.Min(candle.OpenPrice, candle.ClosePrice) - candle.LowPrice;

            // 역망치: 위 꼬리가 몸통의 2배 이상, 아래 꼬리는 짧음
            if (body > 0 && upperWick >= body * 2m && lowerWick <= body * 0.5m)
                return true;

            return false;
        }
    }

    /// <summary>
    /// 엘리엇 파동 상태
    /// </summary>
    public class ElliottWaveState
    {
        public bool IsValid { get; set; }
        public string RejectReason { get; set; } = "";
        public bool Rule1Violated { get; set; }
        public bool Rule2Violated { get; set; }
        public bool Rule3Violated { get; set; }
        public bool IsSuperTrend { get; set; }
        public float FinalScore { get; set; }
        public float FibLevel { get; set; }
        public double Wave1Length { get; set; }
        public double Wave2RetracePct { get; set; }
        public double Wave3Length { get; set; }
    }

    /// <summary>
    /// 피보나치 레벨 (AI 특징으로 사용)
    /// </summary>
    public class FibonacciLevels
    {
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Range { get; set; }

        // 되돌림 레벨
        public decimal Fib0000 { get; set; } // 100% (고점)
        public decimal Fib0236 { get; set; } // 23.6%
        public decimal Fib0382 { get; set; } // 38.2%
        public decimal Fib0500 { get; set; } // 50%
        public decimal Fib0618 { get; set; } // 61.8% (황금비)
        public decimal Fib0786 { get; set; } // 78.6%
        public decimal Fib1000 { get; set; } // 0% (저점)

        // 확장 레벨
        public decimal Fib1618 { get; set; } // 161.8% 확장

        // 현재가 근접도 (AI 특징)
        public double DistanceTo0382_Pct { get; set; }
        public double DistanceTo0618_Pct { get; set; }
        public double DistanceTo0786_Pct { get; set; }

        // 진입 구간 여부
        public bool InEntryZone { get; set; } // 0.382 ~ 0.618 구간
    }
}
