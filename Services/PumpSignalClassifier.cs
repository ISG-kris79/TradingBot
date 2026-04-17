using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.5.8] PUMP 모델 타입 — 일반진입 vs 급등진입 분리
    /// </summary>
    public enum PumpSignalType
    {
        /// <summary>일반 진입 — 완만한 상승 추세 포착 (+1.5% / 30분)</summary>
        Normal,
        /// <summary>급등 진입 — 순간 폭발 포착 (+3% / 10분)</summary>
        Spike
    }

    /// <summary>
    /// PUMP 코인 전용 ML 모델 — 급등 패턴 특화 이진 분류
    /// [v4.5.8] Normal/Spike 모델 분리 지원
    /// [v3.0.9] 규칙 기반 PumpScanStrategy를 ML로 보강
    /// </summary>
    public class PumpSignalClassifier : IDisposable
    {
        private readonly MLContext _mlContext;
        private ITransformer? _model;
        private PredictionEngine<PumpFeature, PumpPrediction>? _predictionEngine;
        private readonly object _predictLock = new();
        private bool _disposed;
        private readonly PumpSignalType _signalType;

        // 학습 데이터 버퍼
        private readonly ConcurrentQueue<PumpFeature> _trainingBuffer = new();
        private const int MinTrainingSamples = 100;
        private const int MaxBufferSize = 10000;
        private DateTime _lastTrainTime = DateTime.MinValue;

        private static readonly string DefaultModelDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "Models");

        /// <summary>타입별 모델 파일명</summary>
        private string ModelFileName => _signalType == PumpSignalType.Spike
            ? "pump_signal_spike.zip"
            : "pump_signal_normal.zip";

        public bool IsModelLoaded => _model != null && _predictionEngine != null;
        public int BufferCount => _trainingBuffer.Count;
        public PumpSignalType SignalType => _signalType;

        public event Action<string>? OnLog;

        /// <summary>라벨링 기준: 미래 window 내 최대 상승률 목표 (%)</summary>
        public float EntryThresholdPct { get; set; }
        /// <summary>라벨링 기준 봉 수 (5분봉 기준)</summary>
        public int LookAheadCandles { get; set; }

        // ═══════════════════════════════════════════
        // [v4.9.8] 타입별 라벨링 파라미터 (Normal vs Spike)
        // ═══════════════════════════════════════════
        /// <summary>Swing Low 탐지 lookback 범위 (봉 수)</summary>
        public int LabelSwingLookback { get; private set; }
        /// <summary>Swing Low 이후 최대 경과 봉 수 (진입 타이밍 제한)</summary>
        public int LabelMaxBarsSinceSwing { get; private set; }
        /// <summary>현재가 swing low 대비 최대 상승률 (%) — 초기 구간 제한</summary>
        public float LabelMaxDistFromLowPct { get; private set; }
        /// <summary>미래 window 내 swing low 대비 검증 목표 상승률 (%)</summary>
        public float LabelValidationTargetPct { get; private set; }
        /// <summary>거래량 회복 최소 배수</summary>
        public float LabelVolumeRecoveryMultiplier { get; private set; }
        /// <summary>미래 드로다운 허용 (swing low 대비 %, 음수 = 깨짐 허용)</summary>
        public float LabelMaxDrawdownFromLow { get; private set; }

        public PumpSignalClassifier(PumpSignalType signalType = PumpSignalType.Normal)
        {
            _mlContext = new MLContext(seed: 42);
            Directory.CreateDirectory(DefaultModelDir);
            _signalType = signalType;

            // [v4.9.8] 타입별 라벨링 파라미터 — Normal은 완만, Spike는 급격
            if (signalType == PumpSignalType.Spike)
            {
                // 급등 진입: 빠르게 폭발하는 자리
                EntryThresholdPct = 4.0f;             // 목표 +4%
                LookAheadCandles = 5;                  // 25분 (5분봉 5개)
                LabelSwingLookback = 5;                // 25분 윈도우
                LabelMaxBarsSinceSwing = 2;            // swing low 2봉 이내 (매우 초기)
                LabelMaxDistFromLowPct = 2.5f;         // 현재가 swing low 대비 +2.5% 이내
                LabelValidationTargetPct = 4.0f;       // 미래 swing low 대비 +4% 검증
                LabelVolumeRecoveryMultiplier = 1.5f;  // 거래량 1.5x+
                LabelMaxDrawdownFromLow = -1.0f;       // 드로다운 swing low -1% 이내
            }
            else
            {
                // 일반 진입: 완만한 상승 추세
                EntryThresholdPct = 2.5f;              // 목표 +2.5%
                LookAheadCandles = 12;                 // 60분 (5분봉 12개)
                LabelSwingLookback = 10;               // 50분 윈도우
                LabelMaxBarsSinceSwing = 4;            // swing low 4봉 이내
                LabelMaxDistFromLowPct = 3.0f;         // 현재가 swing low 대비 +3% 이내
                LabelValidationTargetPct = 3.0f;       // 미래 swing low 대비 +3% 검증
                LabelVolumeRecoveryMultiplier = 1.1f;  // 거래량 1.1x+ (완만)
                LabelMaxDrawdownFromLow = -1.5f;       // 드로다운 swing low -1.5% 이내
            }
        }

        // ═══════════════════════════════════════════
        // 피처 추출 (5분봉 리스트에서)
        // ═══════════════════════════════════════════

        /// <summary>5분봉 캔들 리스트에서 PUMP 전용 피처 추출</summary>
        public static PumpFeature? ExtractFeature(List<IBinanceKline> candles, int index = -1)
        {
            if (candles == null || candles.Count < 30)
                return null;

            int idx = index >= 0 ? index : candles.Count - 1;
            if (idx < 20 || idx >= candles.Count)
                return null;

            var current = candles[idx];
            decimal close = current.ClosePrice;
            decimal open = current.OpenPrice;
            decimal high = current.HighPrice;
            decimal low = current.LowPrice;

            if (close <= 0 || open <= 0)
                return null;

            // 거래량 급증
            var recent3 = candles.Skip(idx - 2).Take(3).ToList();
            var recent5 = candles.Skip(idx - 4).Take(5).ToList();
            var prev10 = candles.Skip(Math.Max(0, idx - 12)).Take(10).ToList();
            var prev20 = candles.Skip(Math.Max(0, idx - 24)).Take(20).ToList();

            double avgVol3 = recent3.Average(k => (double)k.Volume);
            double avgVol5 = recent5.Average(k => (double)k.Volume);
            double avgVolPrev10 = prev10.Count > 0 ? prev10.Average(k => (double)k.Volume) : 1;
            double avgVolPrev20 = prev20.Count > 0 ? prev20.Average(k => (double)k.Volume) : 1;

            float volSurge3 = avgVolPrev10 > 0 ? (float)(avgVol3 / avgVolPrev10) : 1f;
            float volSurge5 = avgVolPrev20 > 0 ? (float)(avgVol5 / avgVolPrev20) : 1f;

            // 가격 변화율
            decimal close3Ago = candles[idx - 3].ClosePrice;
            decimal close5Ago = candles[idx - 5].ClosePrice;
            decimal close10Ago = idx >= 10 ? candles[idx - 10].ClosePrice : close5Ago;

            float priceChange3 = close3Ago > 0 ? (float)((close - close3Ago) / close3Ago * 100) : 0f;
            float priceChange5 = close5Ago > 0 ? (float)((close - close5Ago) / close5Ago * 100) : 0f;
            float priceChange10 = close10Ago > 0 ? (float)((close - close10Ago) / close10Ago * 100) : 0f;

            // RSI (간이 계산)
            float rsi = CalculateRsiSimple(candles, idx, 14);
            float rsi5Ago = idx >= 5 ? CalculateRsiSimple(candles, idx - 5, 14) : rsi;
            float rsiChange = rsi - rsi5Ago;

            // 볼린저 밴드
            var sma20vals = candles.Skip(idx - 19).Take(20).Select(k => (double)k.ClosePrice).ToList();
            double sma20 = sma20vals.Average();
            double stdDev = Math.Sqrt(sma20vals.Sum(v => (v - sma20) * (v - sma20)) / sma20vals.Count);
            double bbUpper = sma20 + 2 * stdDev;
            double bbLower = sma20 - 2 * stdDev;
            double bbWidth = sma20 > 0 ? (bbUpper - bbLower) / sma20 * 100 : 0;
            double bbBreakout = (bbUpper - bbLower) > 0 ? ((double)close - bbUpper) / (bbUpper - bbLower) : 0;

            // MACD 히스토그램
            double ema12 = CalculateEmaSimple(candles, idx, 12);
            double ema26 = CalculateEmaSimple(candles, idx, 26);
            double macdLine = ema12 - ema26;
            double ema12Prev = CalculateEmaSimple(candles, idx - 1, 12);
            double ema26Prev = CalculateEmaSimple(candles, idx - 1, 26);
            double macdPrev = ema12Prev - ema26Prev;
            float macdHist = (float)(macdLine * 0.7); // 근사 히스토그램
            float macdHistChange = (float)(macdLine - macdPrev);

            // ATR 비율
            var atrRecent = candles.Skip(idx - 4).Take(5).Select(k => (double)(k.HighPrice - k.LowPrice)).Average();
            var atrPrev = candles.Skip(Math.Max(0, idx - 24)).Take(20).Select(k => (double)(k.HighPrice - k.LowPrice)).Average();
            float atrRatio = atrPrev > 0 ? (float)(atrRecent / atrPrev) : 1f;

            // Higher Lows 카운트
            int higherLows = 0;
            for (int i = idx - 1; i >= Math.Max(1, idx - 5); i--)
            {
                if (candles[i].LowPrice > candles[i - 1].LowPrice)
                    higherLows++;
                else break;
            }

            // 연속 양봉 수
            int consecBullish = 0;
            for (int i = idx; i >= Math.Max(0, idx - 9); i--)
            {
                if (candles[i].ClosePrice > candles[i].OpenPrice)
                    consecBullish++;
                else break;
            }

            // 캔들 패턴 평균 (최근 3봉)
            float bodyRatioAvg = 0f, upperShadowAvg = 0f;
            foreach (var k in recent3)
            {
                decimal range = k.HighPrice - k.LowPrice;
                if (range > 0)
                {
                    bodyRatioAvg += (float)(Math.Abs(k.ClosePrice - k.OpenPrice) / range);
                    upperShadowAvg += (float)((k.HighPrice - Math.Max(k.OpenPrice, k.ClosePrice)) / range);
                }
            }
            bodyRatioAvg /= 3f;
            upperShadowAvg /= 3f;

            // SMA20 이격도
            float priceVsSma20 = sma20 > 0 ? (float)(((double)close - sma20) / sma20 * 100) : 0f;

            // ═══════════════════════════════════════════════════════════════
            // [v4.9.7] Swing Low 기반 추세 전환점 피처
            // ═══════════════════════════════════════════════════════════════
            const int SwingLookback = 10;
            int startJ = Math.Max(1, idx - SwingLookback);

            // swing low 탐지: 로컬 최소 (직전봉/다음봉보다 낮음)
            int swingLowIdx = -1;
            decimal swingLowPrice = decimal.MaxValue;
            for (int j = startJ; j <= idx - 1; j++)
            {
                if (j - 1 < 0 || j + 1 >= candles.Count) continue;
                bool isLocalMin = candles[j].LowPrice <= candles[j - 1].LowPrice
                               && candles[j].LowPrice <= candles[j + 1].LowPrice;
                if (isLocalMin && candles[j].LowPrice < swingLowPrice)
                {
                    swingLowPrice = candles[j].LowPrice;
                    swingLowIdx = j;
                }
            }
            // 로컬 최소 못 찾으면 윈도우 최저값으로 대체
            if (swingLowIdx < 0)
            {
                for (int j = startJ; j <= idx - 1; j++)
                {
                    if (candles[j].LowPrice < swingLowPrice)
                    {
                        swingLowPrice = candles[j].LowPrice;
                        swingLowIdx = j;
                    }
                }
            }

            float distFromSwingLow = 0f;
            int barsSinceSwingLow = 0;
            float volumeAtLowRatio = 1f;
            if (swingLowIdx >= 0 && swingLowPrice > 0)
            {
                distFromSwingLow = (float)((close - swingLowPrice) / swingLowPrice * 100);
                barsSinceSwingLow = idx - swingLowIdx;
                double volAtLow = (double)candles[swingLowIdx].Volume;
                double volNow = (double)current.Volume;
                volumeAtLowRatio = volAtLow > 0 ? (float)(volNow / volAtLow) : 1f;
            }

            // Swing Low Depth: 직전 윈도우 고점 대비 swing low 깊이
            decimal windowHigh = decimal.MinValue;
            for (int j = startJ; j <= idx - 1; j++)
                if (candles[j].HighPrice > windowHigh) windowHigh = candles[j].HighPrice;
            float swingLowDepth = (windowHigh > 0 && swingLowPrice > 0 && swingLowPrice < decimal.MaxValue)
                ? (float)((windowHigh - swingLowPrice) / windowHigh * 100)
                : 0f;

            // Lower Lows Count (직전 10봉)
            int lowerLowsCount = 0;
            for (int j = Math.Max(1, idx - SwingLookback); j <= idx - 1; j++)
            {
                if (candles[j].LowPrice < candles[j - 1].LowPrice) lowerLowsCount++;
            }

            // Structure Break: 직전 윈도우 최고가 돌파 여부
            float structureBreak = (windowHigh > 0 && close > windowHigh) ? 1f : 0f;

            // ═══════════════════════════════════════════════════════════════
            // [v4.9.8] 고급 지표 피처 12개
            // ═══════════════════════════════════════════════════════════════

            // MACD 메인/시그널 (이미 계산한 ema12/ema26 재사용)
            float macdMain = (float)macdLine;
            float macdSignal = (float)CalculateMacdSignal(candles, idx);

            // ADX + DI (14)
            var (adx, plusDi, minusDi) = CalculateAdx(candles, idx, 14);

            // Price_To_BB_Mid
            float priceToBbMid = sma20 > 0 ? (float)(((double)close - sma20) / sma20 * 100) : 0f;

            // Lower_Shadow_Avg (최근 3봉)
            float lowerShadowAvg = 0f;
            foreach (var k in recent3)
            {
                decimal range = k.HighPrice - k.LowPrice;
                if (range > 0)
                    lowerShadowAvg += (float)((Math.Min(k.OpenPrice, k.ClosePrice) - k.LowPrice) / range);
            }
            lowerShadowAvg /= 3f;

            // Volume_Change_Pct — 최근 3봉 평균 vs 직전 3봉 평균
            float volumeChangePct = 0f;
            if (idx >= 6)
            {
                double prev3 = candles.Skip(idx - 5).Take(3).Average(k => (double)k.Volume);
                if (prev3 > 0) volumeChangePct = (float)((avgVol3 - prev3) / prev3 * 100);
            }

            // Trend_Strength — SMA 정배열 + ADX 결합
            float trendStrength = 0f;
            if (sma20 > 0 && close > 0)
            {
                double sma50Val = CalculateSmaSimple(candles, idx, 50);
                bool bullAlign = sma20 > sma50Val && (double)close > sma20;
                bool bearAlign = sma20 < sma50Val && (double)close < sma20;
                float alignScore = bullAlign ? 1f : (bearAlign ? -1f : 0f);
                trendStrength = alignScore * (adx / 100f);  // -1~+1 범위
            }

            // Fib_Position — 최근 100봉 고점/저점 기준
            float fibPosition = 0.5f;
            int fibStart = Math.Max(0, idx - 99);
            decimal fibHigh = decimal.MinValue, fibLow = decimal.MaxValue;
            for (int j = fibStart; j <= idx; j++)
            {
                if (candles[j].HighPrice > fibHigh) fibHigh = candles[j].HighPrice;
                if (candles[j].LowPrice < fibLow) fibLow = candles[j].LowPrice;
            }
            if (fibHigh > fibLow)
                fibPosition = (float)((close - fibLow) / (fibHigh - fibLow));

            // Stochastic %K, %D (14, 3)
            var (stochK, stochD) = CalculateStochastic(candles, idx, 14, 3);

            return new PumpFeature
            {
                Volume_Surge_3 = Sanitize(volSurge3),
                Volume_Surge_5 = Sanitize(volSurge5),
                Price_Change_3 = Sanitize(priceChange3),
                Price_Change_5 = Sanitize(priceChange5),
                Price_Change_10 = Sanitize(priceChange10),
                RSI = Sanitize(rsi),
                RSI_Change = Sanitize(rsiChange),
                BB_Width = Sanitize((float)bbWidth),
                BB_Breakout = Sanitize((float)bbBreakout),
                MACD_Hist = Sanitize(macdHist),
                MACD_Hist_Change = Sanitize(macdHistChange),
                ATR_Ratio = Sanitize(atrRatio),
                Higher_Lows_Count = higherLows,
                Consec_Bullish = consecBullish,
                Body_Ratio_Avg = Sanitize(bodyRatioAvg),
                Upper_Shadow_Avg = Sanitize(upperShadowAvg),
                OI_Change_Pct = 0f, // 실시간에서만 채움
                Price_vs_SMA20 = Sanitize(priceVsSma20),
                // [v4.9.7] 추세 전환 구조 피처
                Dist_From_Swing_Low = Sanitize(distFromSwingLow),
                Bars_Since_Swing_Low = barsSinceSwingLow,
                Swing_Low_Depth = Sanitize(swingLowDepth),
                Volume_At_Low_Ratio = Sanitize(volumeAtLowRatio),
                Lower_Lows_Count_Prev = lowerLowsCount,
                Structure_Break = structureBreak,
                // [v4.9.8] 고급 지표 피처 (12개)
                MACD_Main = Sanitize(macdMain),
                MACD_Signal = Sanitize(macdSignal),
                ADX = Sanitize(adx),
                Plus_DI = Sanitize(plusDi),
                Minus_DI = Sanitize(minusDi),
                Price_To_BB_Mid = Sanitize(priceToBbMid),
                Lower_Shadow_Avg = Sanitize(lowerShadowAvg),
                Volume_Change_Pct = Sanitize(volumeChangePct),
                Trend_Strength = Sanitize(trendStrength),
                Fib_Position = Sanitize(fibPosition),
                Stoch_K = Sanitize(stochK),
                Stoch_D = Sanitize(stochD)
            };
        }

        // ═══════════════════════════════════════════
        // 라벨링 + 학습 데이터 수집
        // ═══════════════════════════════════════════

        /// <summary>
        /// [v4.9.8] 타입별 이중 경로 라벨링
        /// 경로 A: 추세 전환점 (swing low 직후) — RAVE 17:30 케이스
        /// 경로 B: 추세 지속 조정 (higher low에서 재반등) — 상승 중 pullback 매수
        /// Normal은 완만/느슨, Spike는 급격/엄격
        /// </summary>
        public List<PumpFeature> LabelCandleData(List<IBinanceKline> candles)
        {
            int swingLookback = LabelSwingLookback;
            int maxBarsSinceSwing = LabelMaxBarsSinceSwing;
            float maxDistFromLowPct = LabelMaxDistFromLowPct;
            float validationTargetPct = LabelValidationTargetPct;
            float volMultiplier = LabelVolumeRecoveryMultiplier;
            float maxDrawdownFromLow = LabelMaxDrawdownFromLow;

            int futureWindow = Math.Max(LookAheadCandles, 6);

            if (candles == null || candles.Count < 30 + futureWindow)
                return new List<PumpFeature>();

            var dataset = new List<PumpFeature>();
            int pathATurningPoint = 0;
            int pathBContinuation = 0;

            for (int i = 20; i < candles.Count - futureWindow; i++)
            {
                var feature = ExtractFeature(candles, i);
                if (feature == null) continue;

                decimal currentClose = candles[i].ClosePrice;
                if (currentClose <= 0) continue;

                // ═══════════════════════════════════════
                // 공통: swing low 탐지
                // ═══════════════════════════════════════
                int swingLowIdx = -1;
                decimal swingLowPrice = decimal.MaxValue;
                int startJ = Math.Max(1, i - swingLookback);
                for (int j = startJ; j <= i - 2; j++)
                {
                    if (j - 1 < 0 || j + 1 >= candles.Count) continue;
                    bool isLocalMin = candles[j].LowPrice <= candles[j - 1].LowPrice
                                   && candles[j].LowPrice <= candles[j + 1].LowPrice;
                    if (isLocalMin && candles[j].LowPrice < swingLowPrice)
                    {
                        swingLowPrice = candles[j].LowPrice;
                        swingLowIdx = j;
                    }
                }

                // 공통: 미래 window 계산 (경로 A, B 공유)
                decimal futureMaxHigh = decimal.MinValue;
                decimal futureMinLow = decimal.MaxValue;
                int futureEnd = Math.Min(candles.Count - 1, i + futureWindow);
                for (int j = i + 1; j <= futureEnd; j++)
                {
                    if (candles[j].HighPrice > futureMaxHigh) futureMaxHigh = candles[j].HighPrice;
                    if (candles[j].LowPrice < futureMinLow) futureMinLow = candles[j].LowPrice;
                }

                bool isEntry = false;

                // ═══════════════════════════════════════
                // 경로 A: 추세 전환점 (swing low 직후)
                // ═══════════════════════════════════════
                if (swingLowIdx >= 0 && swingLowPrice > 0)
                {
                    int barsSinceLow = i - swingLowIdx;
                    float distFromLow = (float)((currentClose - swingLowPrice) / swingLowPrice * 100);

                    bool nearLow = barsSinceLow >= 1 && barsSinceLow <= maxBarsSinceSwing;
                    bool stillEarly = distFromLow >= 0f && distFromLow <= maxDistFromLowPct;
                    bool hasHigherLow = candles[i].LowPrice > swingLowPrice;

                    double volAtLow = (double)candles[swingLowIdx].Volume;
                    double volNow = (double)candles[i].Volume;
                    bool volumeRecovery = volAtLow > 0 && volNow > volAtLow * volMultiplier;

                    float futureMaxUpFromLow = (float)((futureMaxHigh - swingLowPrice) / swingLowPrice * 100);
                    bool realReversal = futureMaxUpFromLow >= validationTargetPct;

                    float futureMinDownFromLow = (float)((futureMinLow - swingLowPrice) / swingLowPrice * 100);
                    bool structureHold = futureMinDownFromLow >= maxDrawdownFromLow;

                    bool turningPoint = nearLow && stillEarly && hasHigherLow && volumeRecovery && realReversal && structureHold;
                    if (turningPoint)
                    {
                        isEntry = true;
                        pathATurningPoint++;
                    }
                }

                // ═══════════════════════════════════════
                // 경로 B: 추세 지속 조정 (pullback continuation)
                // "이미 상승 중인데 잠깐 눌렸다가 재반등할 자리"
                // ═══════════════════════════════════════
                if (!isEntry)
                {
                    // 직전 N봉 추세가 상승 (SMA10 상승 or higher lows >=2)
                    double sma10Now = CalculateSmaSimple(candles, i, 10);
                    double sma10Prev = i >= 10 ? CalculateSmaSimple(candles, i - 5, 10) : sma10Now;
                    bool uptrendSma = sma10Now > sma10Prev * 1.002;

                    // 현재 봉이 직전 3봉 대비 눌림
                    decimal recentMaxHigh = decimal.MinValue;
                    for (int j = Math.Max(0, i - 3); j < i; j++)
                        if (candles[j].HighPrice > recentMaxHigh) recentMaxHigh = candles[j].HighPrice;
                    float pullbackPct = recentMaxHigh > 0 ? (float)((recentMaxHigh - currentClose) / recentMaxHigh * 100) : 0;
                    bool shallowPullback = pullbackPct >= 0.3f && pullbackPct <= 2.5f;

                    // 미래에 현재가 대비 목표 도달
                    float futureUpFromCurrent = (float)((futureMaxHigh - currentClose) / currentClose * 100);
                    bool profitTarget = futureUpFromCurrent >= EntryThresholdPct;

                    // 드로다운 제한
                    float futureDownFromCurrent = (float)((currentClose - futureMinLow) / currentClose * 100);
                    // [v5.10] 버퍼 제거 → 진입 직후 급락 패턴을 positive 라벨에서 제거
                    bool riskOk = futureDownFromCurrent <= Math.Abs(maxDrawdownFromLow);

                    if (uptrendSma && shallowPullback && profitTarget && riskOk)
                    {
                        isEntry = true;
                        pathBContinuation++;
                    }
                }

                feature.Label = isEntry;
                dataset.Add(feature);
            }

            int entries = dataset.Count(d => d.Label);
            float entryRatio = dataset.Count > 0 ? (float)entries / dataset.Count * 100 : 0f;
            OnLog?.Invoke($"[PumpML-{_signalType}] [v4.9.8] 라벨링: {dataset.Count}건 (Entry={entries}={entryRatio:F1}% / A전환={pathATurningPoint} B지속={pathBContinuation}) | type={_signalType} target={EntryThresholdPct}% window={futureWindow}봉");
            return dataset;
        }

        /// <summary>실시간 캔들에서 피처 추출 후 학습 버퍼에 추가 (라벨은 나중에)</summary>
        public void CollectFeature(PumpFeature feature)
        {
            if (feature == null) return;

            _trainingBuffer.Enqueue(feature);
            while (_trainingBuffer.Count > MaxBufferSize)
                _trainingBuffer.TryDequeue(out _);
        }

        // ═══════════════════════════════════════════
        // 학습
        // ═══════════════════════════════════════════

        /// <summary>학습 데이터로 LightGBM 이진분류 모델 학습 + 저장</summary>
        public async Task<PumpModelMetrics> TrainAndSaveAsync(
            List<PumpFeature> trainingData,
            CancellationToken token = default)
        {
            if (trainingData == null || trainingData.Count < MinTrainingSamples)
            {
                OnLog?.Invoke($"[PumpML] 학습 데이터 부족: {trainingData?.Count ?? 0}건 (최소 {MinTrainingSamples}건)");
                return new PumpModelMetrics();
            }

            return await Task.Run(() =>
            {
                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);

                    var featureColumns = new[]
                    {
                        "Volume_Surge_3", "Volume_Surge_5",
                        "Price_Change_3", "Price_Change_5", "Price_Change_10",
                        "RSI", "RSI_Change",
                        "BB_Width", "BB_Breakout",
                        "MACD_Hist", "MACD_Hist_Change",
                        "ATR_Ratio",
                        "Higher_Lows_Count_F", "Consec_Bullish_F",
                        "Body_Ratio_Avg", "Upper_Shadow_Avg",
                        "OI_Change_Pct", "Price_vs_SMA20",
                        // [v4.9.7] 추세 전환 구조 피처 (6개)
                        "Dist_From_Swing_Low",
                        "Bars_Since_Swing_Low_F",
                        "Swing_Low_Depth",
                        "Volume_At_Low_Ratio",
                        "Lower_Lows_Count_Prev_F",
                        "Structure_Break",
                        // [v4.9.8] 고급 지표 피처 (12개)
                        "MACD_Main", "MACD_Signal",
                        "ADX", "Plus_DI", "Minus_DI",
                        "Price_To_BB_Mid",
                        "Lower_Shadow_Avg",
                        "Volume_Change_Pct",
                        "Trend_Strength",
                        "Fib_Position",
                        "Stoch_K", "Stoch_D"
                    };

                    var pipeline = _mlContext.Transforms.Conversion
                        .ConvertType("Higher_Lows_Count_F", "Higher_Lows_Count", Microsoft.ML.Data.DataKind.Single)
                        .Append(_mlContext.Transforms.Conversion
                            .ConvertType("Consec_Bullish_F", "Consec_Bullish", Microsoft.ML.Data.DataKind.Single))
                        .Append(_mlContext.Transforms.Conversion
                            .ConvertType("Bars_Since_Swing_Low_F", "Bars_Since_Swing_Low", Microsoft.ML.Data.DataKind.Single))
                        .Append(_mlContext.Transforms.Conversion
                            .ConvertType("Lower_Lows_Count_Prev_F", "Lower_Lows_Count_Prev", Microsoft.ML.Data.DataKind.Single))
                        .Append(_mlContext.Transforms.Concatenate("Features", featureColumns))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                            labelColumnName: "Label",
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 5,
                            learningRate: 0.1,
                            numberOfIterations: 200));

                    OnLog?.Invoke($"[PumpML-{_signalType}] [v4.9.8] LightGBM 학습 시작 ({trainingData.Count}건, 피처 36개=기본18+구조6+고급12)...");
                    _model = pipeline.Fit(split.TrainSet);

                    var predictions = _model.Transform(split.TestSet);
                    var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");

                    var result = new PumpModelMetrics
                    {
                        Accuracy = metrics.Accuracy,
                        F1Score = metrics.F1Score,
                        AUC = metrics.AreaUnderRocCurve,
                        SampleCount = trainingData.Count,
                        TrainedAt = DateTime.Now
                    };

                    OnLog?.Invoke($"[PumpML-{_signalType}] 학습 완료 | Acc={metrics.Accuracy:P2}, F1={metrics.F1Score:F3}, AUC={metrics.AreaUnderRocCurve:F3}");

                    string modelPath = Path.Combine(DefaultModelDir, ModelFileName);
                    _mlContext.Model.Save(_model, dataView.Schema, modelPath);
                    OnLog?.Invoke($"[PumpML-{_signalType}] 모델 저장: {modelPath}");

                    lock (_predictLock)
                    {
                        _predictionEngine?.Dispose();
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<PumpFeature, PumpPrediction>(_model);
                    }

                    _lastTrainTime = DateTime.Now;
                    return result;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[PumpML] 학습 실패: {ex.Message}");
                    return new PumpModelMetrics();
                }
            }, token);
        }

        /// <summary>저장된 모델 로드</summary>
        public bool LoadModel()
        {
            try
            {
                string modelPath = Path.Combine(DefaultModelDir, ModelFileName);
                if (!File.Exists(modelPath))
                {
                    OnLog?.Invoke($"[PumpML-{_signalType}] 모델 파일 없음 — 학습 필요");
                    return false;
                }

                _model = _mlContext.Model.Load(modelPath, out _);
                lock (_predictLock)
                {
                    _predictionEngine?.Dispose();
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<PumpFeature, PumpPrediction>(_model);
                }

                OnLog?.Invoke($"[PumpML-{_signalType}] 모델 로드 완료: {modelPath}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[PumpML-{_signalType}] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════
        // 추론
        // ═══════════════════════════════════════════

        /// <summary>현재 캔들 기반 PUMP 진입 확률 예측</summary>
        public PumpPrediction? Predict(PumpFeature feature)
        {
            if (!IsModelLoaded || feature == null)
                return null;

            try
            {
                lock (_predictLock)
                {
                    return _predictionEngine!.Predict(feature);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[PumpML] 추론 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>5분봉 리스트에서 직접 추론</summary>
        public PumpPrediction? PredictFromCandles(List<IBinanceKline> candles)
        {
            var feature = ExtractFeature(candles);
            return feature != null ? Predict(feature) : null;
        }

        // ═══════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════

        private static float CalculateRsiSimple(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period) return 50f;

            double gainSum = 0, lossSum = 0;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
            {
                double change = (double)(candles[i].ClosePrice - candles[i - 1].ClosePrice);
                if (change > 0) gainSum += change;
                else lossSum -= change;
            }

            double avgGain = gainSum / period;
            double avgLoss = lossSum / period;
            if (avgLoss == 0) return 100f;

            double rs = avgGain / avgLoss;
            return (float)(100 - 100 / (1 + rs));
        }

        private static double CalculateEmaSimple(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period) return (double)candles[endIdx].ClosePrice;

            double multiplier = 2.0 / (period + 1);
            double ema = (double)candles[endIdx - period].ClosePrice;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
            {
                ema = ((double)candles[i].ClosePrice - ema) * multiplier + ema;
            }
            return ema;
        }

        // ═══════════════════════════════════════════
        // [v4.9.8] 고급 지표 헬퍼
        // ═══════════════════════════════════════════

        /// <summary>MACD Signal 라인 (MACD의 EMA9 근사)</summary>
        private static double CalculateMacdSignal(List<IBinanceKline> candles, int endIdx)
        {
            if (endIdx < 34) return 0;
            // MACD 최근 9개 값의 EMA
            double[] macdValues = new double[9];
            for (int k = 0; k < 9; k++)
            {
                int bi = endIdx - 8 + k;
                if (bi < 26) { macdValues[k] = 0; continue; }
                double e12 = CalculateEmaSimple(candles, bi, 12);
                double e26 = CalculateEmaSimple(candles, bi, 26);
                macdValues[k] = e12 - e26;
            }
            double multiplier = 2.0 / (9 + 1);
            double signal = macdValues[0];
            for (int k = 1; k < 9; k++)
            {
                signal = (macdValues[k] - signal) * multiplier + signal;
            }
            return signal;
        }

        /// <summary>ADX / +DI / -DI (14 period, Wilder's smoothing 근사)</summary>
        private static (float adx, float plusDi, float minusDi) CalculateAdx(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period * 2) return (0f, 0f, 0f);

            int startIdx = endIdx - period * 2 + 1;
            double sumTr = 0, sumPlusDm = 0, sumMinusDm = 0;
            var dxList = new List<double>();

            for (int i = startIdx; i <= endIdx; i++)
            {
                if (i < 1) continue;
                double high = (double)candles[i].HighPrice;
                double low  = (double)candles[i].LowPrice;
                double prevHigh = (double)candles[i - 1].HighPrice;
                double prevLow  = (double)candles[i - 1].LowPrice;
                double prevClose = (double)candles[i - 1].ClosePrice;

                double tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                double upMove = high - prevHigh;
                double downMove = prevLow - low;

                double plusDm  = (upMove > downMove && upMove > 0) ? upMove : 0;
                double minusDm = (downMove > upMove && downMove > 0) ? downMove : 0;

                sumTr += tr;
                sumPlusDm += plusDm;
                sumMinusDm += minusDm;

                if (i >= startIdx + period - 1)
                {
                    double plusDiVal  = sumTr > 0 ? 100.0 * sumPlusDm / sumTr : 0;
                    double minusDiVal = sumTr > 0 ? 100.0 * sumMinusDm / sumTr : 0;
                    double dx = (plusDiVal + minusDiVal) > 0
                        ? 100.0 * Math.Abs(plusDiVal - minusDiVal) / (plusDiVal + minusDiVal)
                        : 0;
                    dxList.Add(dx);
                }
            }

            double finalPlusDi  = sumTr > 0 ? 100.0 * sumPlusDm / sumTr : 0;
            double finalMinusDi = sumTr > 0 ? 100.0 * sumMinusDm / sumTr : 0;
            double adxVal = dxList.Count > 0 ? dxList.Average() : 0;

            return ((float)adxVal, (float)finalPlusDi, (float)finalMinusDi);
        }

        /// <summary>Stochastic Oscillator (%K, %D)</summary>
        private static (float k, float d) CalculateStochastic(List<IBinanceKline> candles, int endIdx, int kPeriod, int dPeriod)
        {
            if (endIdx < kPeriod) return (50f, 50f);

            // %K 배열 (최근 dPeriod개)
            double[] kValues = new double[dPeriod];
            for (int offset = 0; offset < dPeriod; offset++)
            {
                int ei = endIdx - offset;
                if (ei < kPeriod - 1) { kValues[offset] = 50; continue; }

                decimal highestHigh = decimal.MinValue;
                decimal lowestLow = decimal.MaxValue;
                for (int j = ei - kPeriod + 1; j <= ei; j++)
                {
                    if (candles[j].HighPrice > highestHigh) highestHigh = candles[j].HighPrice;
                    if (candles[j].LowPrice < lowestLow) lowestLow = candles[j].LowPrice;
                }

                decimal curClose = candles[ei].ClosePrice;
                double k;
                if (highestHigh > lowestLow)
                    k = (double)((curClose - lowestLow) / (highestHigh - lowestLow)) * 100.0;
                else
                    k = 50;
                kValues[offset] = k;
            }

            float kNow = (float)kValues[0];
            float dNow = (float)kValues.Average();
            return (kNow, dNow);
        }

        /// <summary>SMA 간이 계산</summary>
        private static double CalculateSmaSimple(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period - 1) return (double)candles[endIdx].ClosePrice;
            double sum = 0;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
                sum += (double)candles[i].ClosePrice;
            return sum / period;
        }

        private static float Sanitize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _predictionEngine?.Dispose();
            _disposed = true;
        }
    }

    // ═══════════════════════════════════════════
    // 데이터 모델
    // ═══════════════════════════════════════════

    /// <summary>PUMP 전용 피처 (v4.9.8: 36개 = 18 기본 + 6 구조 + 12 고급지표)</summary>
    public class PumpFeature
    {
        [ColumnName("Label")]
        public bool Label { get; set; } // true=진입, false=관망

        // 거래량 급증
        public float Volume_Surge_3 { get; set; }
        public float Volume_Surge_5 { get; set; }

        // 가격 변화율
        public float Price_Change_3 { get; set; }
        public float Price_Change_5 { get; set; }
        public float Price_Change_10 { get; set; }

        // 기본 지표
        public float RSI { get; set; }
        public float RSI_Change { get; set; }

        // 볼린저
        public float BB_Width { get; set; }
        public float BB_Breakout { get; set; }

        // MACD
        public float MACD_Hist { get; set; }
        public float MACD_Hist_Change { get; set; }

        // 변동성
        public float ATR_Ratio { get; set; }

        // 구조 패턴
        public int Higher_Lows_Count { get; set; }
        public int Consec_Bullish { get; set; }

        // 캔들 패턴
        public float Body_Ratio_Avg { get; set; }
        public float Upper_Shadow_Avg { get; set; }

        // 기타
        public float OI_Change_Pct { get; set; }
        public float Price_vs_SMA20 { get; set; }

        // ═══════════════════════════════════════════
        // [v4.9.7] 추세 전환점 구조 피처 (6개)
        // ═══════════════════════════════════════════
        public float Dist_From_Swing_Low { get; set; }
        public int Bars_Since_Swing_Low { get; set; }
        public float Swing_Low_Depth { get; set; }
        public float Volume_At_Low_Ratio { get; set; }
        public int Lower_Lows_Count_Prev { get; set; }
        public float Structure_Break { get; set; }

        // ═══════════════════════════════════════════
        // [v4.9.8] 고급 지표 피처 (12개) — 사용자 요청 확장
        // ═══════════════════════════════════════════
        /// <summary>MACD 메인 라인 (EMA12 - EMA26)</summary>
        public float MACD_Main { get; set; }
        /// <summary>MACD Signal 라인 (MACD의 EMA9)</summary>
        public float MACD_Signal { get; set; }
        /// <summary>ADX (14) — 추세 강도 0~100</summary>
        public float ADX { get; set; }
        /// <summary>+DI (14) — 상승 방향 강도</summary>
        public float Plus_DI { get; set; }
        /// <summary>-DI (14) — 하락 방향 강도</summary>
        public float Minus_DI { get; set; }
        /// <summary>현재가가 BB 중간선 대비 거리 (%)</summary>
        public float Price_To_BB_Mid { get; set; }
        /// <summary>아래 꼬리 길이 / 캔들 전체 길이 (최근 3봉 평균)</summary>
        public float Lower_Shadow_Avg { get; set; }
        /// <summary>최근 3봉 거래량 변화율 (%)</summary>
        public float Volume_Change_Pct { get; set; }
        /// <summary>추세 강도 점수 (SMA 정배열 + ADX 기반)</summary>
        public float Trend_Strength { get; set; }
        /// <summary>피보나치 위치 (0=0.786, 1=0.382) — 최근 100봉 기준</summary>
        public float Fib_Position { get; set; }
        /// <summary>Stochastic %K (14)</summary>
        public float Stoch_K { get; set; }
        /// <summary>Stochastic %D (14,3) — %K의 SMA3</summary>
        public float Stoch_D { get; set; }
    }

    /// <summary>PUMP ML 예측 결과</summary>
    public class PumpPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool ShouldEnter { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }
    }

    /// <summary>PUMP ML 학습 메트릭</summary>
    public class PumpModelMetrics
    {
        public double Accuracy { get; set; }
        public double F1Score { get; set; }
        public double AUC { get; set; }
        public int SampleCount { get; set; }
        public DateTime TrainedAt { get; set; }
    }
}
