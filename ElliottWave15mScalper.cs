using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 엘리엇 파동 15분봉 황금존 단타 전략
    ///
    /// 진입 흐름:
    ///   1파 상승 확인 → 2파 황금존(61.8~78.6%) 조정 → V-Turn 반전 → 40% 초기 진입
    ///   → 3파 진행 → 4파 38.2% 조정 지지 확인 → 60% 불타기 추가
    ///
    /// 강제청산 조건:
    ///   4파 저점이 1파 고점 하향 이탈 → 엘리엇 규칙 위반 → 전량 즉시 청산
    ///
    /// 손절:
    ///   2파 진입 시: Fib 0.786 하향 이탈
    ///   4파 불타기 후: 4파 저점 하향 이탈
    /// </summary>
    public class ElliottWave15mScalper
    {
        // ═══ 파라미터 ═══
        private const double MIN_WAVE1_PCT     = 1.0;    // 1파 최소 상승폭 (%)
        private const double MIN_VOL_RATIO     = 1.3;    // 1파 거래량 배율
        private const double FIB_GOLDEN_LOW    = 0.618;  // 황금존 하단
        private const double FIB_GOLDEN_HIGH   = 0.786;  // 황금존 상단
        private const double FIB_WAVE4_RETRACE = 0.382;  // 4파 되돌림 목표
        private const double VTURN_RSI_FLOOR   = 30.0;   // V-Turn RSI 바닥
        private const double VTURN_RSI_DELTA   = 2.5;    // V-Turn RSI 반등폭
        private const double ZONE_TOLERANCE    = 0.003;  // 피보나치 구간 허용 오차 (0.3%)
        private const double INITIAL_ENTRY_PCT = 0.40;   // 초기 진입 비율
        private const double ADDON_ENTRY_PCT   = 0.60;   // 불타기 비율

        // ═══ 파동 상태 ═══
        public enum Phase
        {
            Idle,            // 대기
            Wave1Detected,   // 1파 확정
            GoldenZoneWatch, // 황금존 진입 대기
            Wave2Entry,      // 2파 황금존 진입 신호 (40% 진입)
            Wave3Active,     // 3파 진행 중
            Wave4Watch,      // 4파 조정 감시 (불타기 대기)
            Wave4AddOn,      // 4파 불타기 신호 (60% 추가)
            Completed        // 청산 완료
        }

        public class ScalperState
        {
            public Phase   CurrentPhase    { get; set; } = Phase.Idle;
            public double  Wave1Low        { get; set; }
            public double  Wave1High       { get; set; }
            public double  Wave1AvgVolume  { get; set; }
            public double  Wave3High       { get; set; } // 3파 고점 (4파 기준)
            public double  Wave4Low        { get; set; } // 4파 저점 추적

            // 피보나치 레벨 (1파 기준)
            public double  Fib382         { get; set; }
            public double  Fib500         { get; set; }
            public double  Fib618         { get; set; }
            public double  Fib786         { get; set; }
            public double  Fib1618        { get; set; }

            // 4파 피보나치 (3파 고점 → 4파 조정)
            public double  Wave4Fib382    { get; set; }
            public double  Wave4StopLoss  { get; set; }

            // V-Turn 추적
            public double  PrevRsi        { get; set; }
            public DateTime LastUpdated   { get; set; }

            // 진입 시 손절
            public double  StopLoss       { get; set; }
        }

        private readonly Dictionary<string, ScalperState> _states = new();

        public ScalperState GetOrCreate(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var s))
            {
                s = new ScalperState();
                _states[symbol] = s;
            }
            return s;
        }

        public void Reset(string symbol)
        {
            _states[symbol] = new ScalperState();
        }

        /// <summary>
        /// 메인 분석: 15분봉 캔들 리스트를 받아 현재 신호 반환
        /// </summary>
        /// <returns>신호: null=없음, Wave2Entry=40% 진입, Wave4AddOn=60% 불타기, ForceExit=강제청산</returns>
        public ScalperSignal? Analyze(
            string symbol,
            List<IGrouping<int, IBinanceKline>> _unused, // 호환성 위해 제거 예정
            List<IBinanceKline> candles,
            double currentRsi,
            double ema20)
        {
            if (candles == null || candles.Count < 20) return null;

            var state = GetOrCreate(symbol);
            int n = candles.Count;
            double price = (double)candles[n - 1].ClosePrice;
            double prevRsi = state.PrevRsi;
            state.PrevRsi = currentRsi;
            state.LastUpdated = DateTime.UtcNow;

            // ══ [PHASE: Idle] 1파 상승 감지 ══
            if (state.CurrentPhase == Phase.Idle)
            {
                if (TryDetectWave1(candles, out double w1Low, out double w1High, out double w1AvgVol))
                {
                    state.Wave1Low       = w1Low;
                    state.Wave1High      = w1High;
                    state.Wave1AvgVolume = w1AvgVol;

                    double range = w1High - w1Low;
                    state.Fib382  = w1High - range * 0.382;
                    state.Fib500  = w1High - range * 0.500;
                    state.Fib618  = w1High - range * 0.618;
                    state.Fib786  = w1High - range * 0.786;
                    state.Fib1618 = w1High + range * 0.618;
                    state.StopLoss = state.Fib786 * (1 - ZONE_TOLERANCE);

                    state.CurrentPhase = Phase.GoldenZoneWatch;
                }
                return null;
            }

            // ══ [PHASE: GoldenZoneWatch] 황금존(61.8~78.6%) 진입 대기 ══
            if (state.CurrentPhase == Phase.GoldenZoneWatch)
            {
                // 엘리엇 규칙 위반: 2파 저점이 1파 시작점 하향 이탈 → 리셋
                if (price < state.Wave1Low * 0.998)
                {
                    Reset(symbol);
                    return null;
                }

                // 황금존 진입 확인
                bool inGoldenZone = price >= state.Fib786 * (1 - ZONE_TOLERANCE)
                                 && price <= state.Fib618 * (1 + ZONE_TOLERANCE);

                // V-Turn: RSI가 바닥(30 미만)에서 반등 + 가격이 EMA20 위로
                bool isVTurn = prevRsi < VTURN_RSI_FLOOR
                            && currentRsi > prevRsi + VTURN_RSI_DELTA
                            && price > ema20;

                if (inGoldenZone && isVTurn)
                {
                    state.CurrentPhase = Phase.Wave2Entry;
                    return new ScalperSignal
                    {
                        Type           = SignalType.Wave2Entry,
                        Symbol         = symbol,
                        Price          = price,
                        SizeMultiplier = INITIAL_ENTRY_PCT,
                        StopLoss       = state.StopLoss,
                        TakeProfit1    = state.Wave1High,
                        TakeProfit2    = state.Fib1618,
                        Reason         = $"황금존({state.Fib618:F4}~{state.Fib786:F4}) + V-Turn (RSI:{prevRsi:F1}→{currentRsi:F1})"
                    };
                }
                return null;
            }

            // ══ [PHASE: Wave2Entry → Wave3Active] 진입 이후 3파 진행 추적 ══
            if (state.CurrentPhase == Phase.Wave2Entry)
            {
                // 진입 확인 후 Wave3Active로 전환
                state.CurrentPhase = Phase.Wave3Active;
                state.Wave3High    = price;
                return null;
            }

            if (state.CurrentPhase == Phase.Wave3Active)
            {
                // 3파 고점 갱신
                if (price > state.Wave3High)
                    state.Wave3High = price;

                // 손절 체크: Fib786 하향
                if (price < state.StopLoss)
                {
                    Reset(symbol);
                    return new ScalperSignal
                    {
                        Type   = SignalType.StopLossHit,
                        Symbol = symbol,
                        Price  = price,
                        Reason = $"손절: Fib786({state.Fib786:F4}) 하향 이탈"
                    };
                }

                // 3파 고점이 1파 고점 돌파 → 4파 감시 시작
                if (state.Wave3High > state.Wave1High * 1.005 && price < state.Wave3High * 0.99)
                {
                    double w3Range = state.Wave3High - state.Fib618; // 3파 상승폭 대략
                    state.Wave4Fib382  = state.Wave3High - w3Range * 0.382;
                    state.Wave4StopLoss = state.Wave3High - w3Range * 0.618; // 4파 손절
                    state.Wave4Low     = price;
                    state.CurrentPhase = Phase.Wave4Watch;
                }
                return null;
            }

            // ══ [PHASE: Wave4Watch] 4파 조정 → 38.2% 지지 확인 ══
            if (state.CurrentPhase == Phase.Wave4Watch)
            {
                // 4파 저점 추적
                if (price < state.Wave4Low)
                    state.Wave4Low = price;

                // ★ 강제청산 조건: 4파 저점이 1파 고점 하향 이탈 (엘리엇 규칙 위반)
                if (state.Wave4Low < state.Wave1High * 0.998)
                {
                    Reset(symbol);
                    return new ScalperSignal
                    {
                        Type   = SignalType.ForceExit,
                        Symbol = symbol,
                        Price  = price,
                        Reason = $"⚠️ 4파 저점({state.Wave4Low:F4}) < 1파 고점({state.Wave1High:F4}) — 엘리엇 규칙 위반, 전량 청산"
                    };
                }

                // 4파 38.2% 지지 + 반등 확인 → 60% 불타기
                bool atFib382Support = price >= state.Wave4Fib382 * (1 - ZONE_TOLERANCE)
                                    && price <= state.Wave4Fib382 * (1 + ZONE_TOLERANCE);
                bool reboundConfirm  = currentRsi > prevRsi + 1.5 && price > ema20;

                if (atFib382Support && reboundConfirm)
                {
                    state.CurrentPhase = Phase.Wave4AddOn;
                    return new ScalperSignal
                    {
                        Type           = SignalType.Wave4AddOn,
                        Symbol         = symbol,
                        Price          = price,
                        SizeMultiplier = ADDON_ENTRY_PCT,
                        StopLoss       = state.Wave4StopLoss,
                        TakeProfit1    = state.Fib1618,
                        Reason         = $"4파 38.2% 지지({state.Wave4Fib382:F4}) + 반등 확인 → 60% 불타기"
                    };
                }
                return null;
            }

            // Wave4AddOn 이후 완료 처리 (외부에서 청산 완료 시 Reset 호출)
            return null;
        }

        // ══ 분석 엔트리: IBinanceKline 리스트 직접 받는 오버로드 ══
        public ScalperSignal? Analyze(
            string symbol,
            List<IBinanceKline> candles,
            double currentRsi,
            double ema20)
        {
            return Analyze(symbol, null!, candles, currentRsi, ema20);
        }

        private static bool TryDetectWave1(
            List<IBinanceKline> candles,
            out double wave1Low,
            out double wave1High,
            out double avgVolume)
        {
            wave1Low = 0; wave1High = 0; avgVolume = 0;
            int n = candles.Count;
            if (n < 10) return false;

            // 최근 10~30봉 범위에서 swing low → swing high 탐색
            int searchStart = Math.Max(0, n - 30);
            int swingLowIdx = -1, swingHighIdx = -1;
            double swingLow = double.MaxValue, swingHigh = 0;

            for (int i = searchStart; i < n - 3; i++)
            {
                double lo = (double)candles[i].LowPrice;
                if (lo < swingLow)
                {
                    swingLow = lo;
                    swingLowIdx = i;
                }
            }
            if (swingLowIdx < 0) return false;

            for (int i = swingLowIdx + 2; i < n - 1; i++)
            {
                double hi = (double)candles[i].HighPrice;
                if (hi > swingHigh)
                {
                    swingHigh = hi;
                    swingHighIdx = i;
                }
            }
            if (swingHighIdx <= swingLowIdx) return false;

            // 최소 상승폭 검증
            double upPct = (swingHigh - swingLow) / swingLow * 100;
            if (upPct < MIN_WAVE1_PCT) return false;

            // 거래량 확인
            double w1Vol = 0; int w1Count = 0;
            double baseVol = 0; int baseCount = 0;
            for (int i = swingLowIdx; i <= swingHighIdx; i++)
            { w1Vol += (double)candles[i].Volume; w1Count++; }
            int baseStart = Math.Max(searchStart, swingLowIdx - 6);
            for (int i = baseStart; i < swingLowIdx; i++)
            { baseVol += (double)candles[i].Volume; baseCount++; }

            double w1Avg = w1Count > 0 ? w1Vol / w1Count : 0;
            double baseAvg = baseCount > 0 ? baseVol / baseCount : w1Avg;
            if (baseAvg > 0 && w1Avg < baseAvg * MIN_VOL_RATIO) return false;

            wave1Low  = swingLow;
            wave1High = swingHigh;
            avgVolume = w1Avg;
            return true;
        }

        /// <summary>
        /// EMA 계산 (간단 구현)
        /// </summary>
        public static double ComputeEma(List<IBinanceKline> candles, int period)
        {
            if (candles.Count < period) return (double)candles[^1].ClosePrice;
            double k = 2.0 / (period + 1);
            double ema = candles.Take(period).Average(c => (double)c.ClosePrice);
            for (int i = period; i < candles.Count; i++)
                ema = (double)candles[i].ClosePrice * k + ema * (1 - k);
            return ema;
        }

        /// <summary>
        /// RSI 계산 (14기간)
        /// </summary>
        public static double ComputeRsi(List<IBinanceKline> candles, int period = 14)
        {
            if (candles.Count < period + 1) return 50;
            double gain = 0, loss = 0;
            int start = candles.Count - period;
            for (int i = start; i < candles.Count; i++)
            {
                double diff = (double)candles[i].ClosePrice - (double)candles[i - 1].ClosePrice;
                if (diff > 0) gain += diff; else loss -= diff;
            }
            if (loss == 0) return 100;
            double rs = (gain / period) / (loss / period);
            return 100.0 - 100.0 / (1.0 + rs);
        }
    }

    // ═══ 신호 타입 ═══
    public enum SignalType
    {
        Wave2Entry,   // 2파 황금존 초기 40% 진입
        Wave4AddOn,   // 4파 지지 60% 불타기
        ForceExit,    // 강제 전량청산 (엘리엇 규칙 위반)
        StopLossHit   // 손절가 도달
    }

    public class ScalperSignal
    {
        public SignalType Type           { get; set; }
        public string     Symbol         { get; set; } = "";
        public double     Price          { get; set; }
        public double     SizeMultiplier { get; set; } // 포지션 비율
        public double     StopLoss       { get; set; }
        public double     TakeProfit1    { get; set; }
        public double     TakeProfit2    { get; set; }
        public string     Reason         { get; set; } = "";
    }
}
