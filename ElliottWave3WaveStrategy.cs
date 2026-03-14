using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 엘리엇 파동 3파 확정형 단타 전략 (5분봉)
    /// 
    /// 진입 조건:
    /// 1. 거래량 실린 1파 상승 감지
    /// 2. 2파 조정 중 피보나치 0.5~0.618 구간 도달
    /// 3. 거래량 감소 (매도세 소진)
    /// 4. RSI 상승 다이버전스 출현
    /// 5. MACD 골든크로스 + 히스토그램 축소
    /// 6. 볼린저 밴드 중단(20EMA) 위로 캔들 안착
    /// </summary>
    public class ElliottWave3WaveStrategy
    {
        public enum WavePhaseType
        {
            Idle,           // 대기
            Wave1Started,   // 1파 확정
            Wave2Started,   // 2파 확정
            Wave3Setup,     // 3파 진입 준비중
            Wave3Entry,     // 3파 진입 신호 확정
            Wave3Active     // 3파 진행중
        }

        public class WaveState
        {
            public WavePhaseType CurrentPhase { get; set; } = WavePhaseType.Idle;
            public DateTime Phase1StartTime { get; set; }
            public decimal Phase1LowPrice { get; set; }
            public decimal Phase1HighPrice { get; set; }
            public float Phase1Volume { get; set; }
            public WaveAnchor Anchor { get; set; } = new WaveAnchor();

            public DateTime Phase2StartTime { get; set; }
            public decimal Phase2LowPrice { get; set; }
            public decimal Phase2HighPrice { get; set; }
            public float Phase2Volume { get; set; }

            // 피보나치 레벨
            public decimal Fib500Level { get; set; }
            public decimal Fib0618Level { get; set; }
            public decimal Fib786Level { get; set; }
            public decimal Fib1618Target { get; set; }

            // 다이버전스 추적
            public List<decimal> PriceLows { get; set; } = new List<decimal>();
            public List<decimal> RsiLows { get; set; } = new List<decimal>();

            // MACD 상태
            public decimal LastMacDValue { get; set; }
            public decimal LastSignalValue { get; set; }
            public decimal LastHistogram { get; set; }

            // 볼린저 밴드 상태
            public decimal BollingerMiddle { get; set; } // 20EMA
            public decimal BollingerLower { get; set; }
            public decimal BollingerUpper { get; set; }
        }

        private readonly Dictionary<string, WaveState> _waveStates = new Dictionary<string, WaveState>();
        private const decimal MIN_WAVE1_PERCENT = 0.8m;  // 1파: 최소 0.8% 이상 상승
        private const float MIN_VOLUME_RATIO = 1.5f;     // 1파의 거래량이 이전의 1.5배 이상
        private const decimal DIVERGENCE_THRESHOLD = 5;  // RSI 다이버전스: 최소 5 포인트 이상
        private const int WAVE1_SWING_LOOKBACK = 18;
        private const int WAVE1_MIN_CANDLES = 3;
        private const int BASELINE_VOLUME_LOOKBACK = 6;
        private const int FRACTAL_MIN_BARS = 2;
        private const int FRACTAL_MAX_BARS = 5;
        private const int ATR_PERIOD = 14;
        private const decimal WAVE1_MIN_ATR_MULTIPLIER = 1.20m;
        private const decimal WAVE2_MIN_RETRACE = 0.50m;
        private const decimal WAVE2_MAX_RETRACE = 0.786m;
        private const decimal WAVE2_REBOUND_CONFIRM_PCT = 0.0010m;
        private const decimal WAVE2_INVALIDATION_BUFFER = 0.998m;
        private const decimal FIB_ENTRY_TOLERANCE_PCT = 0.0015m;

        public WaveState GetOrCreateState(string symbol)
        {
            if (!_waveStates.ContainsKey(symbol))
            {
                _waveStates[symbol] = new WaveState();
            }
            return _waveStates[symbol];
        }

        /// <summary>
        /// 1단계: 1파 상승파 감지
        /// - 거래량이 실린 강한 상승 캔들
        /// - 최소 0.8% 이상의 상승폭
        /// - 거래량이 이전 대비 1.5배 이상
        /// </summary>
        public bool DetectWave1(
            string symbol,
            List<CandleData> candles,
            int wave1CandleIndex)
        {
            if (wave1CandleIndex < (FRACTAL_MIN_BARS * 2 + 5) || candles.Count < 20)
                return false;

            var state = GetOrCreateState(symbol);

            // 프랙탈 피벗 확정: 좌우 최소 2봉이 필요하므로 최신 2봉은 피벗 판정에서 제외
            int pivotSearchEnd = wave1CandleIndex - FRACTAL_MIN_BARS;
            int windowStart = Math.Max(0, pivotSearchEnd - WAVE1_SWING_LOOKBACK);

            int swingPeakPivotStrength;
            int swingPeakIdx = FindLatestConfirmedPivotHighIndex(candles, windowStart, pivotSearchEnd, out swingPeakPivotStrength);
            if (swingPeakIdx < 0)
                return false;

            int swingLowPivotStrength;
            int swingStartIdx = FindLatestConfirmedPivotLowIndex(candles, windowStart, swingPeakIdx - FRACTAL_MIN_BARS, out swingLowPivotStrength);
            if (swingStartIdx < 0)
                return false;

            if (swingStartIdx >= swingPeakIdx)
                return false;

            decimal wave1Start = candles[swingStartIdx].Low;
            decimal wave1Peak = candles[swingPeakIdx].High;
            if (wave1Start <= 0 || wave1Peak <= wave1Start)
                return false;

            int wave1Candles = swingPeakIdx - swingStartIdx + 1;
            if (wave1Candles < WAVE1_MIN_CANDLES)
                return false;

            // 상승폭 확인
            decimal upPercent = ((wave1Peak - wave1Start) / wave1Start) * 100;
            if (upPercent < MIN_WAVE1_PERCENT)
                return false;

            // ATR 기반 최소 파동 필터: 잔파동/노이즈 제거
            decimal waveHeight = wave1Peak - wave1Start;
            decimal atr = CalculateAverageTrueRange(candles, ATR_PERIOD, swingPeakIdx);
            if (atr > 0 && waveHeight < atr * WAVE1_MIN_ATR_MULTIPLIER)
                return false;

            // 거래량 확인 (1파 구간 평균 거래량 vs 직전 베이스 구간)
            decimal wave1AvgVolume = CalculateAverageVolume(candles, swingStartIdx, swingPeakIdx);
            int baselineStart = Math.Max(windowStart, swingStartIdx - BASELINE_VOLUME_LOOKBACK);
            int baselineEnd = swingStartIdx - 1;
            decimal baselineAvgVolume = CalculateAverageVolume(candles, baselineStart, baselineEnd);
            if (baselineAvgVolume > 0 && wave1AvgVolume < baselineAvgVolume * (decimal)MIN_VOLUME_RATIO)
                return false;

            ResetStateInternal(state);

            // 1파 상태 업데이트
            state.CurrentPhase = WavePhaseType.Wave1Started;
            state.Phase1StartTime = candles[swingStartIdx].OpenTime;
            state.Phase1LowPrice = wave1Start;
            state.Phase1HighPrice = wave1Peak;
            state.Phase1Volume = (float)wave1AvgVolume;
            state.Anchor.Confirm(wave1Start, wave1Peak, swingLowPivotStrength, swingPeakPivotStrength);

            return true;
        }

        /// <summary>
        /// 2단계: 2파 조정파 감지 및 피보나치 설정
        /// - 1파 고점에서 하락 시작
        /// - 피보나치 0.5 ~ 0.618 구간으로 조정
        /// - 거래량 감소 (매도세 소진)
        /// </summary>
        public bool DetectWave2AndSetFibonacci(
            string symbol,
            List<CandleData> candles,
            int wave2CandleIndex)
        {
            if (wave2CandleIndex < 1) return false;

            var state = GetOrCreateState(symbol);
            if (state.CurrentPhase != WavePhaseType.Wave1Started && state.CurrentPhase != WavePhaseType.Wave2Started)
                return false;

            if (!state.Anchor.IsConfirmed || state.Anchor.LowPoint <= 0m || state.Anchor.HighPoint <= state.Anchor.LowPoint)
                return false;

            var currentCandle = candles[wave2CandleIndex];
            var prevCandle = candles[wave2CandleIndex - 1];
            decimal wave1Range = state.Anchor.HighPoint - state.Anchor.LowPoint;
            if (wave1Range <= 0)
                return false;

            // 앵커 잠금: 확정된 1파 기준점은 무효화 전까지 갱신 금지
            state.Phase1LowPrice = state.Anchor.LowPoint;
            state.Phase1HighPrice = state.Anchor.HighPoint;

            // 0.786 하향 돌파 시에만 파동 실패로 판정 후 리셋
            decimal fib786Level = state.Fib786Level > 0m
                ? state.Fib786Level
                : state.Anchor.HighPoint - (wave1Range * 0.786m);

            if (state.Anchor.IsInvalidated(currentCandle.Low, fib786Level, WAVE2_INVALIDATION_BUFFER))
            {
                ResetState(symbol);
                return false;
            }

            decimal candidateWave2Low = state.Phase2LowPrice > 0
                ? Math.Min(state.Phase2LowPrice, currentCandle.Low)
                : currentCandle.Low;

            // 엘리엇 규칙 위반: 2파 저점이 1파 시작점 하향 이탈
            if (candidateWave2Low <= state.Anchor.LowPoint * WAVE2_INVALIDATION_BUFFER)
            {
                ResetState(symbol);
                return false;
            }

            // 하락 확인 (1파 고점에서 하락 시작)
            if (currentCandle.Close >= prevCandle.Close && candidateWave2Low >= state.Fib500Level)
                return false;

            decimal retraceRatio = (state.Anchor.HighPoint - candidateWave2Low) / wave1Range;
            bool retraceInRange = retraceRatio >= WAVE2_MIN_RETRACE && retraceRatio <= WAVE2_MAX_RETRACE;
            if (!retraceInRange)
                return false;

            // 거래량 감소(매도세 소진) + 반등 시작 확인
            bool volumeContracting = state.Phase1Volume <= 0
                || currentCandle.Volume <= state.Phase1Volume * 0.90f;
            bool reboundStarted = currentCandle.Close >= candidateWave2Low * (1m + WAVE2_REBOUND_CONFIRM_PCT)
                || currentCandle.Close > prevCandle.Close;
            if (!volumeContracting || !reboundStarted)
                return false;

            // 2파 상태 업데이트
            state.CurrentPhase = WavePhaseType.Wave2Started;
            state.Phase2StartTime = currentCandle.OpenTime;
            state.Phase2LowPrice = candidateWave2Low;
            state.Phase2HighPrice = currentCandle.High;
            state.Phase2Volume = currentCandle.Volume;

            // 피보나치 레벨 계산
            state.Fib500Level = state.Anchor.HighPoint - (wave1Range * 0.500m);
            state.Fib0618Level = state.Anchor.HighPoint - (wave1Range * 0.618m);
            state.Fib786Level = state.Anchor.HighPoint - (wave1Range * 0.786m);
            state.Fib1618Target = state.Anchor.HighPoint + (wave1Range * 1.618m);
            state.PriceLows.Clear();
            state.RsiLows.Clear();

            return true;
        }

        /// <summary>
        /// 3단계: RSI 상승 다이버전스 감지
        /// - 가격 저점은 낮아지거나 수평
        /// - RSI 저점은 높아짐
        /// </summary>
        public bool DetectRSIDivergence(
            string symbol,
            List<CandleData> candles,
            List<decimal> rsiValues)
        {
            var state = GetOrCreateState(symbol);
            if (state.CurrentPhase != WavePhaseType.Wave2Started)
                return false;

            if (candles.Count < 5 || rsiValues.Count < 5)
                return false;

            // 앵커 무효화: 0.786 하향 돌파 시 파동 실패
            if (state.Fib786Level > 0m && candles[^1].Low < state.Fib786Level * WAVE2_INVALIDATION_BUFFER)
            {
                ResetState(symbol);
                return false;
            }

            // 2파 진행 중 가격과 RSI 저점 추적
            decimal currentPrice = candles[^1].Low;
            decimal currentRsi = rsiValues[^1];

            state.PriceLows.Add(currentPrice);
            state.RsiLows.Add(currentRsi);

            if (state.PriceLows.Count < 2)
                return false;

            // 다이버전스 확인: 가격은 낮아지거나 수평, RSI는 높아짐
            decimal prevPriceLow = state.PriceLows[^2];
            decimal prevRsiLow = state.RsiLows[^2];

            // 가격 저점이 낮아지거나 수평
            bool priceLowerOrFlat = currentPrice <= prevPriceLow;

            // RSI 저점이 높아짐 (상승 다이버전스)
            bool rsiHigher = currentRsi > (prevRsiLow + DIVERGENCE_THRESHOLD);

            if (priceLowerOrFlat && rsiHigher)
            {
                state.CurrentPhase = WavePhaseType.Wave3Setup;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 4단계: MACD 골든크로스 확인
        /// - MACD가 시그널선 위로 올라옴
        /// - 히스토그램이 작아지며 양수로 변함
        /// </summary>
        public bool DetectMACDGoldenCross(
            decimal currentMacd,
            decimal currentSignal,
            decimal currentHistogram,
            decimal prevMacd,
            decimal prevSignal)
        {
            var isCross = (prevMacd <= prevSignal) && (currentMacd > currentSignal);
            var isHistogramShrinking = Math.Abs(currentHistogram) < Math.Abs(prevMacd - prevSignal);

            return isCross && isHistogramShrinking;
        }

        /// <summary>
        /// 5단계: 진입 신호 확정
        /// - 피보나치 0.618 지지선 근처
        /// - 볼린저 밴드 중단(20EMA) 위로 캔들이 안착
        /// - RSI 다이버전스 + MACD 골든크로스 동시 확인
        /// </summary>
        public bool ConfirmEntry(
            string symbol,
            CandleData currentCandle,
            decimal currentRsi,
            decimal currentMacd,
            decimal currentSignal,
            decimal bollingerMiddle,
            decimal bollingerLower,
            decimal bollingerUpper)
        {
            var state = GetOrCreateState(symbol);
            if (state.CurrentPhase != WavePhaseType.Wave3Setup)
                return false;

            if (state.Phase1HighPrice <= state.Phase1LowPrice || state.Fib0618Level <= 0 || state.Fib786Level <= 0)
                return false;

            // 볼린저 밴드 상태 업데이트
            state.BollingerMiddle = bollingerMiddle;
            state.BollingerLower = bollingerLower;
            state.BollingerUpper = bollingerUpper;

            // 엘리엇 규칙 위반 시 상태 초기화
            if (state.Fib786Level > 0m && currentCandle.Low < state.Fib786Level * WAVE2_INVALIDATION_BUFFER)
            {
                ResetState(symbol);
                return false;
            }

            decimal fib50 = state.Fib500Level > 0
                ? state.Fib500Level
                : state.Phase1HighPrice - ((state.Phase1HighPrice - state.Phase1LowPrice) * 0.500m);
            decimal zoneTolerance = state.Fib0618Level * FIB_ENTRY_TOLERANCE_PCT;

            // 피보나치 구간(0.5~0.786) 터치 + 0.618 재돌파 확인
            bool touchedFibZone = currentCandle.Low <= fib50 + zoneTolerance
                && currentCandle.Low >= state.Fib786Level - zoneTolerance;
            bool reclaimedFib618 = currentCandle.Close >= state.Fib0618Level - zoneTolerance
                && currentCandle.Open <= state.Fib0618Level + zoneTolerance;
            bool closeNotOverExtended = currentCandle.Close <= fib50 * 1.015m;

            // 볼린저 밴드 중단 위로 안착 확인
            bool candleAboveMiddle = currentCandle.Close > bollingerMiddle &&
                                     currentCandle.Low <= bollingerMiddle;

            // RSI 상승 다이버전스 + MACD 골든크로스 확인
            bool hasMacdHistory = state.LastMacDValue != 0m || state.LastSignalValue != 0m;
            bool macDCross = hasMacdHistory && DetectMACDGoldenCross(currentMacd, currentSignal,
                                                    currentMacd - currentSignal,
                                                    state.LastMacDValue,
                                                    state.LastSignalValue);

            state.LastMacDValue = currentMacd;
            state.LastSignalValue = currentSignal;
            state.LastHistogram = currentMacd - currentSignal;

            if (touchedFibZone && reclaimedFib618 && closeNotOverExtended && candleAboveMiddle && macDCross && currentRsi < 70)
            {
                state.CurrentPhase = WavePhaseType.Wave3Entry;
                return true;
            }

            return false;
        }

        public void MarkWave3Active(string symbol)
        {
            var state = GetOrCreateState(symbol);
            if (state.CurrentPhase == WavePhaseType.Wave3Entry || state.CurrentPhase == WavePhaseType.Wave3Setup)
            {
                state.CurrentPhase = WavePhaseType.Wave3Active;
            }
        }

        /// <summary>
        /// 손절가 설정
        /// - 1파의 시작점 (0.0) 또는 피보나치 0.786 라인 하향 돌파 시
        /// </summary>
        public decimal GetStopLoss(string symbol)
        {
            var state = GetOrCreateState(symbol);
            // 더 보수적으로: 피보나치 0.786 라인
            return state.Fib786Level;
        }

        /// <summary>
        /// 익절가 설정
        /// - 1차: 1파 고점 부근 (안전 이익 확보)
        /// - 2차: 피보나치 확장 1.618 지점 (3파 타겟)
        /// </summary>
        public (decimal target1, decimal target2) GetTakeProfits(string symbol)
        {
            var state = GetOrCreateState(symbol);
            return (state.Phase1HighPrice, state.Fib1618Target);
        }

        /// <summary>
        /// 경고 신호: 조기 익절
        /// - RSI가 70을 넘긴 후 꺾임
        /// - 볼린저 상단을 이탈했다가 다시 들어옴
        /// </summary>
        public bool ShouldTakeProfitEarly(
            string symbol,
            decimal currentRsi,
            decimal prevRsi,
            decimal currentCandle,
            decimal bollingerUpper)
        {
            var state = GetOrCreateState(symbol);

            // RSI 반향 신호
            bool rsiReversal = (prevRsi > 70) && (currentRsi < prevRsi);

            // 볼린저 상단 이탈 후 다시 들어옴
            bool bollingerReversal = (currentCandle > bollingerUpper) ||
                                     (currentCandle < bollingerUpper && prevRsi > 70);

            return rsiReversal || bollingerReversal;
        }

        /// <summary>
        /// 상태 초기화 (포지션 종료 또는 전략 실패 시)
        /// </summary>
        public void ResetState(string symbol)
        {
            if (_waveStates.ContainsKey(symbol))
            {
                _waveStates[symbol] = new WaveState();
            }
        }

        public ElliottWaveAnchorState? BuildPersistentState(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return null;

            if (!_waveStates.TryGetValue(symbol, out var state) || state == null)
                return null;

            bool hasMeaningfulState = state.CurrentPhase != WavePhaseType.Idle
                || state.Anchor.IsConfirmed
                || state.Fib786Level > 0m;

            if (!hasMeaningfulState)
                return null;

            return new ElliottWaveAnchorState
            {
                Symbol = symbol,
                CurrentPhase = (int)state.CurrentPhase,
                Phase1StartTime = state.Phase1StartTime == default ? null : state.Phase1StartTime,
                Phase1LowPrice = state.Phase1LowPrice,
                Phase1HighPrice = state.Phase1HighPrice,
                Phase1Volume = state.Phase1Volume,
                Phase2StartTime = state.Phase2StartTime == default ? null : state.Phase2StartTime,
                Phase2LowPrice = state.Phase2LowPrice,
                Phase2HighPrice = state.Phase2HighPrice,
                Phase2Volume = state.Phase2Volume,
                Fib500Level = state.Fib500Level,
                Fib0618Level = state.Fib0618Level,
                Fib786Level = state.Fib786Level,
                Fib1618Target = state.Fib1618Target,
                AnchorLowPoint = state.Anchor.LowPoint,
                AnchorHighPoint = state.Anchor.HighPoint,
                AnchorIsConfirmed = state.Anchor.IsConfirmed,
                AnchorIsLocked = state.Anchor.IsLocked,
                AnchorConfirmedAtUtc = state.Anchor.ConfirmedAtUtc == default ? null : state.Anchor.ConfirmedAtUtc,
                LowPivotStrength = state.Anchor.LowPivotStrength,
                HighPivotStrength = state.Anchor.HighPivotStrength,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

        public bool RestorePersistentState(ElliottWaveAnchorState persisted)
        {
            if (persisted == null || string.IsNullOrWhiteSpace(persisted.Symbol))
                return false;

            var state = GetOrCreateState(persisted.Symbol);
            ResetStateInternal(state);

            state.CurrentPhase = Enum.IsDefined(typeof(WavePhaseType), persisted.CurrentPhase)
                ? (WavePhaseType)persisted.CurrentPhase
                : WavePhaseType.Idle;

            state.Phase1StartTime = persisted.Phase1StartTime ?? default;
            state.Phase1LowPrice = persisted.Phase1LowPrice;
            state.Phase1HighPrice = persisted.Phase1HighPrice;
            state.Phase1Volume = persisted.Phase1Volume;
            state.Phase2StartTime = persisted.Phase2StartTime ?? default;
            state.Phase2LowPrice = persisted.Phase2LowPrice;
            state.Phase2HighPrice = persisted.Phase2HighPrice;
            state.Phase2Volume = persisted.Phase2Volume;
            state.Fib500Level = persisted.Fib500Level;
            state.Fib0618Level = persisted.Fib0618Level;
            state.Fib786Level = persisted.Fib786Level;
            state.Fib1618Target = persisted.Fib1618Target;

            state.Anchor.LowPoint = persisted.AnchorLowPoint;
            state.Anchor.HighPoint = persisted.AnchorHighPoint;
            state.Anchor.IsConfirmed = persisted.AnchorIsConfirmed;
            state.Anchor.IsLocked = persisted.AnchorIsLocked;
            state.Anchor.ConfirmedAtUtc = persisted.AnchorConfirmedAtUtc ?? default;
            state.Anchor.LowPivotStrength = persisted.LowPivotStrength;
            state.Anchor.HighPivotStrength = persisted.HighPivotStrength;

            return true;
        }

        private static int FindLowestLowIndex(List<CandleData> candles, int start, int end)
        {
            if (candles == null || candles.Count == 0)
                return -1;

            int safeStart = Math.Max(0, start);
            int safeEnd = Math.Min(end, candles.Count - 1);
            if (safeStart > safeEnd)
                return -1;

            int bestIndex = safeStart;
            decimal bestValue = candles[safeStart].Low;

            for (int index = safeStart + 1; index <= safeEnd; index++)
            {
                if (candles[index].Low < bestValue)
                {
                    bestValue = candles[index].Low;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        private static int FindHighestHighIndex(List<CandleData> candles, int start, int end)
        {
            if (candles == null || candles.Count == 0)
                return -1;

            int safeStart = Math.Max(0, start);
            int safeEnd = Math.Min(end, candles.Count - 1);
            if (safeStart > safeEnd)
                return -1;

            int bestIndex = safeStart;
            decimal bestValue = candles[safeStart].High;

            for (int index = safeStart + 1; index <= safeEnd; index++)
            {
                if (candles[index].High > bestValue)
                {
                    bestValue = candles[index].High;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        private static decimal CalculateAverageVolume(List<CandleData> candles, int start, int end)
        {
            if (candles == null || candles.Count == 0)
                return 0m;

            int safeStart = Math.Max(0, start);
            int safeEnd = Math.Min(end, candles.Count - 1);
            if (safeStart > safeEnd)
                return 0m;

            decimal volumeSum = 0m;
            int count = 0;

            for (int index = safeStart; index <= safeEnd; index++)
            {
                volumeSum += (decimal)candles[index].Volume;
                count++;
            }

            return count > 0 ? volumeSum / count : 0m;
        }

        private static void ResetStateInternal(WaveState state)
        {
            state.CurrentPhase = WavePhaseType.Idle;
            state.Phase1StartTime = default;
            state.Phase1LowPrice = 0m;
            state.Phase1HighPrice = 0m;
            state.Phase1Volume = 0f;
            state.Phase2StartTime = default;
            state.Phase2LowPrice = 0m;
            state.Phase2HighPrice = 0m;
            state.Phase2Volume = 0f;
            state.Fib500Level = 0m;
            state.Fib0618Level = 0m;
            state.Fib786Level = 0m;
            state.Fib1618Target = 0m;
            state.PriceLows.Clear();
            state.RsiLows.Clear();
            state.LastMacDValue = 0m;
            state.LastSignalValue = 0m;
            state.LastHistogram = 0m;
            state.BollingerMiddle = 0m;
            state.BollingerLower = 0m;
            state.BollingerUpper = 0m;
            state.Anchor.Reset();
        }

        private static int FindLatestConfirmedPivotLowIndex(List<CandleData> candles, int start, int end, out int pivotStrength)
        {
            pivotStrength = 0;
            if (candles == null || candles.Count == 0)
                return -1;

            int safeStart = Math.Max(start, FRACTAL_MIN_BARS);
            int safeEnd = Math.Min(end, candles.Count - 1 - FRACTAL_MIN_BARS);
            if (safeStart > safeEnd)
                return -1;

            for (int index = safeEnd; index >= safeStart; index--)
            {
                int strength = GetPivotLowStrength(candles, index);
                if (strength >= FRACTAL_MIN_BARS)
                {
                    pivotStrength = strength;
                    return index;
                }
            }

            return -1;
        }

        private static int FindLatestConfirmedPivotHighIndex(List<CandleData> candles, int start, int end, out int pivotStrength)
        {
            pivotStrength = 0;
            if (candles == null || candles.Count == 0)
                return -1;

            int safeStart = Math.Max(start, FRACTAL_MIN_BARS);
            int safeEnd = Math.Min(end, candles.Count - 1 - FRACTAL_MIN_BARS);
            if (safeStart > safeEnd)
                return -1;

            for (int index = safeEnd; index >= safeStart; index--)
            {
                int strength = GetPivotHighStrength(candles, index);
                if (strength >= FRACTAL_MIN_BARS)
                {
                    pivotStrength = strength;
                    return index;
                }
            }

            return -1;
        }

        private static int GetPivotLowStrength(List<CandleData> candles, int index)
        {
            int maxPossible = Math.Min(FRACTAL_MAX_BARS, Math.Min(index, candles.Count - 1 - index));
            for (int bars = maxPossible; bars >= FRACTAL_MIN_BARS; bars--)
            {
                bool isPivot = true;
                for (int offset = 1; offset <= bars; offset++)
                {
                    if (candles[index].Low >= candles[index - offset].Low || candles[index].Low >= candles[index + offset].Low)
                    {
                        isPivot = false;
                        break;
                    }
                }

                if (isPivot)
                    return bars;
            }

            return 0;
        }

        private static int GetPivotHighStrength(List<CandleData> candles, int index)
        {
            int maxPossible = Math.Min(FRACTAL_MAX_BARS, Math.Min(index, candles.Count - 1 - index));
            for (int bars = maxPossible; bars >= FRACTAL_MIN_BARS; bars--)
            {
                bool isPivot = true;
                for (int offset = 1; offset <= bars; offset++)
                {
                    if (candles[index].High <= candles[index - offset].High || candles[index].High <= candles[index + offset].High)
                    {
                        isPivot = false;
                        break;
                    }
                }

                if (isPivot)
                    return bars;
            }

            return 0;
        }

        private static decimal CalculateAverageTrueRange(List<CandleData> candles, int period, int endIndex)
        {
            if (candles == null || candles.Count < 2 || period <= 0)
                return 0m;

            int safeEnd = Math.Min(endIndex, candles.Count - 1);
            int start = Math.Max(1, safeEnd - period + 1);
            if (start > safeEnd)
                return 0m;

            decimal trSum = 0m;
            int count = 0;

            for (int index = start; index <= safeEnd; index++)
            {
                var current = candles[index];
                decimal prevClose = candles[index - 1].Close;

                decimal tr1 = current.High - current.Low;
                decimal tr2 = Math.Abs(current.High - prevClose);
                decimal tr3 = Math.Abs(current.Low - prevClose);
                decimal trueRange = Math.Max(tr1, Math.Max(tr2, tr3));

                if (trueRange > 0m)
                {
                    trSum += trueRange;
                    count++;
                }
            }

            return count > 0 ? trSum / count : 0m;
        }

        /// <summary>
        /// 현재 파동 상태 조회
        /// </summary>
        public WaveState GetCurrentState(string symbol)
        {
            return GetOrCreateState(symbol);
        }
    }
}
