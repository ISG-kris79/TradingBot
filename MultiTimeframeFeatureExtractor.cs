using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Binance.Net.Enums;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// Multi-Timeframe 캔들 데이터를 Entry Timing Feature로 변환
    /// 핵심: 1D/4H/2H/1H/15M 시장 상태를 하나의 Feature 벡터로 통합
    /// </summary>
    public class MultiTimeframeFeatureExtractor
    {
        private readonly IExchangeService _exchangeService;

        // [v4.5.11] 일일 세션 컨텍스트 (TradingEngine이 주기적으로 업데이트)
        // 모든 Extractor 인스턴스가 공유 (실시간 예측 + 학습 라벨링에서 활용)
        public static volatile float DailyPnlRatioContext = 0f;     // PnL / $250
        public static volatile float IsAboveDailyTargetContext = 0f; // 1=초과
        public static volatile float DailyTradeCountContext = 0f;   // 오늘 거래 수

        public MultiTimeframeFeatureExtractor(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        }

        /// <summary>
        /// 특정 시점의 Multi-Timeframe Feature 추출 (실시간 예측용)
        /// </summary>
        /// <summary>
        /// [v4.5.15] WebSocket 캐시 우선 조회, 없으면 REST fallback
        /// </summary>
        private async Task<List<IBinanceKline>?> GetKlinesFromCacheOrRestAsync(
            string symbol, KlineInterval interval, int restLimit, int minCount, CancellationToken token)
        {
            // 1) 캐시 우선 시도 (M1/M15/H1/H4/D1만 캐시 존재)
            var cached = Services.MarketDataManager.Instance?.GetCachedKlines(symbol, interval, minCount);
            if (cached != null) return cached;

            // 2) REST fallback
            var rest = await _exchangeService.GetKlinesAsync(symbol, interval, restLimit, token);
            return rest?.ToList();
        }

        public async Task<MultiTimeframeEntryFeature?> ExtractRealtimeFeatureAsync(
            string symbol,
            DateTime timestamp,
            CancellationToken token = default)
        {
            try
            {
                // [v4.5.15] 캐시 우선 조회 (메이저 코인은 전체 캐시 히트, PUMP는 REST fallback)
                var tasks = new[]
                {
                    GetKlinesFromCacheOrRestAsync(symbol, KlineInterval.OneDay, 50, 20, token),
                    GetKlinesFromCacheOrRestAsync(symbol, KlineInterval.FourHour, 120, 40, token),
                    _exchangeService.GetKlinesAsync(symbol, KlineInterval.TwoHour, 120, token).ContinueWith(t => t.Result?.ToList(), token), // H2는 캐시 없음
                    GetKlinesFromCacheOrRestAsync(symbol, KlineInterval.OneHour, 200, 50, token),
                    GetKlinesFromCacheOrRestAsync(symbol, KlineInterval.FifteenMinutes, 260, 100, token),
                    GetKlinesFromCacheOrRestAsync(symbol, KlineInterval.OneMinute, 40, 30, token)
                };

                await Task.WhenAll(tasks);

                var d1Klines = tasks[0].Result;
                var h4Klines = tasks[1].Result;
                var h2Klines = tasks[2].Result;
                var h1Klines = tasks[3].Result;
                var m15Klines = tasks[4].Result;
                var m1Klines = tasks[5].Result;

                // 요구 사항 완화: 초기 학습용으로 최소 데이터만 확보
                if (d1Klines == null || d1Klines.Count < 20 ||      // 1일봉 20개
                    h4Klines == null || h4Klines.Count < 40 ||      // 4시간봉 40개 (1주일)
                    h2Klines == null || h2Klines.Count < 40 ||      // 2시간봉 40개
                    h1Klines == null || h1Klines.Count < 50 ||      // 1시간봉 50개 (2일)
                    m15Klines == null || m15Klines.Count < 100)     // 15분봉 100개 (1일)
                {
                    return null;
                }

                var feature = new MultiTimeframeEntryFeature
                {
                    Symbol = symbol,
                    Timestamp = timestamp,
                    EntryPrice = m15Klines[^1].ClosePrice
                };

                // 각 타임프레임별 Feature 추출
                ExtractTimeframeFeatures(d1Klines, out var d1Features);
                ExtractTimeframeFeatures(h4Klines, out var h4Features);
                ExtractTimeframeFeatures(h2Klines, out var h2Features);
                ExtractTimeframeFeatures(h1Klines, out var h1Features);
                ExtractTimeframeFeatures(m15Klines, out var m15Features);

                // 1일봉
                feature.D1_Trend = d1Features.Trend;
                feature.D1_RSI = d1Features.RSI;
                feature.D1_MACD = d1Features.MACD;
                feature.D1_Signal = d1Features.Signal;
                feature.D1_BBPosition = d1Features.BBPosition;
                feature.D1_Volume_Ratio = d1Features.VolumeRatio;

                // 4시간봉
                feature.H4_Trend = h4Features.Trend;
                feature.H4_RSI = h4Features.RSI;
                feature.H4_MACD = h4Features.MACD;
                feature.H4_Signal = h4Features.Signal;
                feature.H4_BBPosition = h4Features.BBPosition;
                feature.H4_Volume_Ratio = h4Features.VolumeRatio;
                feature.H4_DistanceToSupport = CalculateDistanceToSupport(h4Klines);
                feature.H4_DistanceToResist = CalculateDistanceToResistance(h4Klines);

                // 2시간봉
                feature.H2_Trend = h2Features.Trend;
                feature.H2_RSI = h2Features.RSI;
                feature.H2_MACD = h2Features.MACD;
                feature.H2_Signal = h2Features.Signal;
                feature.H2_BBPosition = h2Features.BBPosition;
                feature.H2_Volume_Ratio = h2Features.VolumeRatio;
                feature.H2_WavePosition = EstimateWavePosition(h2Klines);

                // 1시간봉
                feature.H1_Trend = h1Features.Trend;
                feature.H1_RSI = h1Features.RSI;
                feature.H1_MACD = h1Features.MACD;
                feature.H1_Signal = h1Features.Signal;
                feature.H1_BBPosition = h1Features.BBPosition;
                feature.H1_Volume_Ratio = h1Features.VolumeRatio;
                feature.H1_MomentumStrength = CalculateMomentumStrength(h1Klines);

                // 15분봉 (현재 진입 시점)
                feature.M15_RSI = m15Features.RSI;
                feature.M15_MACD = m15Features.MACD;
                feature.M15_Signal = m15Features.Signal;
                feature.M15_BBPosition = m15Features.BBPosition;
                feature.M15_Volume_Ratio = m15Features.VolumeRatio;
                feature.M15_PriceVsSMA20 = CalculatePriceVsSMA(m15Klines, 20);
                feature.M15_PriceVsSMA60 = CalculatePriceVsSMA(m15Klines, 60);
                feature.M15_ADX = CalculateADX(m15Klines);
                (feature.M15_PlusDI, feature.M15_MinusDI) = CalculateDI(m15Klines);
                feature.M15_ATR = CalculateATR(m15Klines);
                feature.M15_OI_Change_Pct = 0f; // TODO: OI 데이터 연동

                // 피보나치 레벨 (객관적 수치 → AI 특징)
                var fibLevels = CalculateFibonacciFeatures(m15Klines, currentPrice: m15Klines[^1].ClosePrice);
                feature.Fib_DistanceTo0382_Pct = fibLevels.distanceTo0382;
                feature.Fib_DistanceTo0618_Pct = fibLevels.distanceTo0618;
                feature.Fib_DistanceTo0786_Pct = fibLevels.distanceTo0786;
                feature.Fib_InEntryZone = fibLevels.inEntryZone;

                // [v4.5.2] 1분봉 MACD 휩소 피처
                ExtractM1WhipsawFeatures(m1Klines, feature);

                // [v4.5.6] 다중 TF 하락추세 감지 피처 (PUMP 진입 방어)
                ExtractMultiTfDowntrendFeatures(m15Klines, h1Klines, feature);

                // [v4.5.11] 일일 세션 컨텍스트 피처 (목표 달성 후 보수 모드 학습)
                feature.DailyPnlRatio = DailyPnlRatioContext;
                feature.IsAboveDailyTarget = IsAboveDailyTargetContext;
                feature.DailyTradeCount = DailyTradeCountContext;

                // [v4.6.2/v4.6.3] M15 단타 보조지표 — EMA / VWAP / StochRSI / BB스퀴즈 / SuperTrend / DailyPivot
                ExtractM15TradingViewFeatures(m15Klines, d1Klines, feature);

                // [v5.10.75 Phase 2] 고점 진입 학습 + 여유도 + 심볼 성과 피처 (6개)
                ExtractEntryPositionFeatures(m15Klines, m1Klines, feature);
                ExtractSymbolRecentPerformanceFeatures(symbol, feature);

                // 시간 컨텍스트
                ExtractTimeContext(timestamp, feature);

                return feature;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FeatureExtractor] Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 학습/재검수용 Point-in-Time 피처 추출.
        /// candleIndex 시점까지의 M15 데이터만 사용하며, 이후 캔들은 절대 참조하지 않습니다.
        /// </summary>
        public MultiTimeframeEntryFeature? ExtractPointInTimeFeatureFromM15(
            string symbol,
            List<IBinanceKline> orderedM15Candles,
            int candleIndex)
        {
            if (orderedM15Candles == null || orderedM15Candles.Count == 0)
                return null;

            if (candleIndex < 0 || candleIndex >= orderedM15Candles.Count)
                return null;

            // 지표 계산 안정성을 위한 최소 길이 확보
            if (candleIndex < 60)
                return null;

            var m15Slice = orderedM15Candles
                .Take(candleIndex + 1)
                .OrderBy(k => k.OpenTime)
                .ToList();

            if (m15Slice.Count < 60)
                return null;

            var current = m15Slice[^1];
            var timestamp = current.OpenTime;

            var feature = new MultiTimeframeEntryFeature
            {
                Symbol = symbol,
                Timestamp = timestamp,
                EntryPrice = current.ClosePrice
            };

            // M15만으로 상위 타임프레임 재구성 (미래 캔들 미참조)
            var h1Klines = AggregateCandlesFromM15(m15Slice, TimeSpan.FromHours(1), KlineInterval.OneHour);
            var h2Klines = AggregateCandlesFromM15(m15Slice, TimeSpan.FromHours(2), KlineInterval.TwoHour);
            var h4Klines = AggregateCandlesFromM15(m15Slice, TimeSpan.FromHours(4), KlineInterval.FourHour);
            var d1Klines = AggregateCandlesFromM15(m15Slice, TimeSpan.FromDays(1), KlineInterval.OneDay);

            ExtractTimeframeFeatures(d1Klines, out var d1Features);
            ExtractTimeframeFeatures(h4Klines, out var h4Features);
            ExtractTimeframeFeatures(h2Klines, out var h2Features);
            ExtractTimeframeFeatures(h1Klines, out var h1Features);
            ExtractTimeframeFeatures(m15Slice, out var m15Features);

            AssignFeatures(
                feature,
                d1Features,
                h4Features,
                h2Features,
                h1Features,
                m15Features,
                d1Klines,
                h4Klines,
                h2Klines,
                h1Klines,
                m15Slice);

            ExtractTimeContext(timestamp, feature);
            return feature;
        }

        /// <summary>
        /// 백테스트용: 과거 캔들 데이터에서 Feature + Label 일괄 생성
        /// </summary>
        public List<MultiTimeframeEntryFeature> ExtractHistoricalFeatures(
            string symbol,
            List<IBinanceKline> d1Klines,
            List<IBinanceKline> h4Klines,
            List<IBinanceKline> h2Klines,
            List<IBinanceKline> h1Klines,
            List<IBinanceKline> m15Klines,
            BacktestEntryLabeler labeler,
            bool isLongStrategy)
        {
            var features = new List<MultiTimeframeEntryFeature>();

            // 15분봉을 기준으로 슬라이딩 윈도우
            for (int i = 130; i < m15Klines.Count - 20; i++) // 미래 20캔들 확보
            {
                var currentCandle = m15Klines[i];
                var futureCandles = m15Klines.Skip(i + 1).Take(20).ToList();

                // 레이블 생성 (실제 수익 여부)
                var (shouldEnter, actualProfitPct, reason) = isLongStrategy
                    ? labeler.EvaluateLongEntry(futureCandles, currentCandle.ClosePrice)
                    : labeler.EvaluateShortEntry(futureCandles, currentCandle.ClosePrice);

                // Feature 추출
                var feature = new MultiTimeframeEntryFeature
                {
                    Symbol = symbol,
                    Timestamp = currentCandle.OpenTime,
                    EntryPrice = currentCandle.ClosePrice,
                    ShouldEnter = shouldEnter,
                    ActualProfitPct = actualProfitPct
                };

                // 각 타임프레임의 해당 시점 캔들 찾기
                var d1Index = FindCandleIndex(d1Klines, currentCandle.OpenTime);
                var h4Index = FindCandleIndex(h4Klines, currentCandle.OpenTime);
                var h2Index = FindCandleIndex(h2Klines, currentCandle.OpenTime);
                var h1Index = FindCandleIndex(h1Klines, currentCandle.OpenTime);

                if (d1Index < 30 || h4Index < 60 || h2Index < 60 || h1Index < 100)
                    continue;

                // Feature 추출
                ExtractTimeframeFeatures(d1Klines.Take(d1Index + 1).ToList(), out var d1Features);
                ExtractTimeframeFeatures(h4Klines.Take(h4Index + 1).ToList(), out var h4Features);
                ExtractTimeframeFeatures(h2Klines.Take(h2Index + 1).ToList(), out var h2Features);
                ExtractTimeframeFeatures(h1Klines.Take(h1Index + 1).ToList(), out var h1Features);
                ExtractTimeframeFeatures(m15Klines.Take(i + 1).ToList(), out var m15Features);

                // Feature 할당 (실시간과 동일 로직)
                AssignFeatures(feature, d1Features, h4Features, h2Features, h1Features, m15Features,
                    d1Klines.Take(d1Index + 1).ToList(),
                    h4Klines.Take(h4Index + 1).ToList(),
                    h2Klines.Take(h2Index + 1).ToList(),
                    h1Klines.Take(h1Index + 1).ToList(),
                    m15Klines.Take(i + 1).ToList());

                ExtractTimeContext(currentCandle.OpenTime, feature);

                features.Add(feature);
            }

            return features;
        }

        private void AssignFeatures(
            MultiTimeframeEntryFeature feature,
            TimeframeFeatures d1, TimeframeFeatures h4, TimeframeFeatures h2,
            TimeframeFeatures h1, TimeframeFeatures m15,
            List<IBinanceKline> d1Klines, List<IBinanceKline> h4Klines,
            List<IBinanceKline> h2Klines, List<IBinanceKline> h1Klines,
            List<IBinanceKline> m15Klines)
        {
            feature.D1_Trend = d1.Trend;
            feature.D1_RSI = d1.RSI;
            feature.D1_MACD = d1.MACD;
            feature.D1_Signal = d1.Signal;
            feature.D1_BBPosition = d1.BBPosition;
            feature.D1_Volume_Ratio = d1.VolumeRatio;
            feature.D1_Stoch_K = d1.Stoch_K;
            feature.D1_Stoch_D = d1.Stoch_D;
            feature.D1_MACD_Cross = d1.MACD_Cross;
            feature.D1_ADX = d1.ADX;
            feature.D1_PlusDI = d1.PlusDI;
            feature.D1_MinusDI = d1.MinusDI;

            feature.H4_Trend = h4.Trend;
            feature.H4_RSI = h4.RSI;
            feature.H4_MACD = h4.MACD;
            feature.H4_Signal = h4.Signal;
            feature.H4_BBPosition = h4.BBPosition;
            feature.H4_Volume_Ratio = h4.VolumeRatio;
            feature.H4_DistanceToSupport = CalculateDistanceToSupport(h4Klines);
            feature.H4_DistanceToResist = CalculateDistanceToResistance(h4Klines);
            feature.H4_Stoch_K = h4.Stoch_K;
            feature.H4_Stoch_D = h4.Stoch_D;
            feature.H4_MACD_Cross = h4.MACD_Cross;
            feature.H4_ADX = h4.ADX;
            feature.H4_PlusDI = h4.PlusDI;
            feature.H4_MinusDI = h4.MinusDI;
            feature.H4_MomentumStrength = CalculateMomentumStrength(h4Klines);

            feature.H2_Trend = h2.Trend;
            feature.H2_RSI = h2.RSI;
            feature.H2_MACD = h2.MACD;
            feature.H2_Signal = h2.Signal;
            feature.H2_BBPosition = h2.BBPosition;
            feature.H2_Volume_Ratio = h2.VolumeRatio;
            feature.H2_WavePosition = EstimateWavePosition(h2Klines);

            feature.H1_Trend = h1.Trend;
            feature.H1_RSI = h1.RSI;
            feature.H1_MACD = h1.MACD;
            feature.H1_Signal = h1.Signal;
            feature.H1_BBPosition = h1.BBPosition;
            feature.H1_Volume_Ratio = h1.VolumeRatio;
            feature.H1_MomentumStrength = CalculateMomentumStrength(h1Klines);
            feature.H1_Stoch_K = h1.Stoch_K;
            feature.H1_Stoch_D = h1.Stoch_D;
            feature.H1_MACD_Cross = h1.MACD_Cross;

            feature.M15_RSI = m15.RSI;
            feature.M15_MACD = m15.MACD;
            feature.M15_Signal = m15.Signal;
            feature.M15_BBPosition = m15.BBPosition;
            feature.M15_Volume_Ratio = m15.VolumeRatio;
            feature.M15_Stoch_K = m15.Stoch_K;
            feature.M15_Stoch_D = m15.Stoch_D;

            // [v3.4.2] D1+H4 방향성 합산 (-2~+2)
            // D1 방향: MACD > Signal = +1, MACD < Signal = -1
            // H4 방향: MACD > Signal = +1, MACD < Signal = -1
            float d1Dir = d1.MACD > d1.Signal ? 1f : (d1.MACD < d1.Signal ? -1f : 0f);
            float h4Dir = h4.MACD > h4.Signal ? 1f : (h4.MACD < h4.Signal ? -1f : 0f);
            feature.DirectionBias = d1Dir + h4Dir;
            feature.M15_PriceVsSMA20 = CalculatePriceVsSMA(m15Klines, 20);
            feature.M15_PriceVsSMA60 = CalculatePriceVsSMA(m15Klines, 60);
            feature.M15_ADX = CalculateADX(m15Klines);
            (feature.M15_PlusDI, feature.M15_MinusDI) = CalculateDI(m15Klines);
            feature.M15_ATR = CalculateATR(m15Klines);
            feature.M15_OI_Change_Pct = 0f;

            // 피보나치 특징
            var fibLevels = CalculateFibonacciFeatures(m15Klines, currentPrice: m15Klines[^1].ClosePrice);
            feature.Fib_DistanceTo0382_Pct = fibLevels.distanceTo0382;
            feature.Fib_DistanceTo0618_Pct = fibLevels.distanceTo0618;
            feature.Fib_DistanceTo0786_Pct = fibLevels.distanceTo0786;
            feature.Fib_InEntryZone = fibLevels.inEntryZone;

            // [v4.6.2/v4.6.3] M15 단타 보조지표 (PIT/히스토리컬 경로 포함)
            ExtractM15TradingViewFeatures(m15Klines, d1Klines, feature);

            // [v5.10.75 Phase 2] 고점 진입 학습 피처 (PIT/히스토리컬 경로 — M1은 m15 직전 캔들로 근사)
            // PIT 경로에선 M1 데이터가 없으므로 null 전달 → M1 관련 피처는 0으로 기본값
            ExtractEntryPositionFeatures(m15Klines, null, feature);
            ExtractSymbolRecentPerformanceFeatures(feature.Symbol, feature);
        }

        private struct TimeframeFeatures
        {
            public float Trend;
            public float RSI;
            public float MACD;
            public float Signal;
            public float BBPosition;
            public float VolumeRatio;
            // [v3.4.2] 확장 피처
            public float Stoch_K;
            public float Stoch_D;
            public float MACD_Cross;     // 1=골든, -1=데드, 0=없음
            public float ADX;
            public float PlusDI;
            public float MinusDI;
        }

        private void ExtractTimeframeFeatures(List<IBinanceKline> klines, out TimeframeFeatures features)
        {
            features = new TimeframeFeatures();

            if (klines == null || klines.Count < 30)
                return;

            var latest = klines[^1];
            double sma20 = IndicatorCalculator.CalculateSMA(klines, 20);
            double sma50 = IndicatorCalculator.CalculateSMA(klines, 50);
            double sma200 = IndicatorCalculator.CalculateSMA(klines, 200);

            // 추세
            if (sma20 > sma50 && sma50 > sma200)
                features.Trend = 1.0f; // 강세
            else if (sma20 < sma50 && sma50 < sma200)
                features.Trend = -1.0f; // 약세
            else
                features.Trend = 0.0f; // 중립

            // RSI (0~100 → 0~1 정규화)
            double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
            features.RSI = (float)(rsi / 100.0);

            // MACD
            var (macd, signal, hist) = IndicatorCalculator.CalculateMACD(klines);
            features.MACD = (float)macd;
            features.Signal = (float)signal;

            // [v3.4.2] MACD 크로스 감지 (최근 3봉 이내)
            if (klines.Count >= 30)
            {
                try
                {
                    // 이전 봉의 MACD 계산 (현재 - 1봉)
                    var prevKlines = klines.Take(klines.Count - 1).ToList();
                    var (prevMacd, prevSignal, _) = IndicatorCalculator.CalculateMACD(prevKlines);
                    bool goldenCross = prevMacd <= prevSignal && macd > signal;
                    bool deadCross = prevMacd >= prevSignal && macd < signal;
                    features.MACD_Cross = goldenCross ? 1.0f : (deadCross ? -1.0f : 0.0f);
                }
                catch { features.MACD_Cross = 0f; }
            }

            // 볼린저밴드 위치 (0~1)
            var bb = IndicatorCalculator.CalculateBB(klines, 20, 2.0);
            decimal price = latest.ClosePrice;
            if (bb.Upper > bb.Lower)
                features.BBPosition = (float)((price - (decimal)bb.Lower) / ((decimal)bb.Upper - (decimal)bb.Lower));
            else
                features.BBPosition = 0.5f;

            // 거래량 비율
            double avgVolume = klines.TakeLast(20).Average(k => (double)k.Volume);
            features.VolumeRatio = avgVolume > 0 ? (float)((double)latest.Volume / avgVolume) : 1.0f;

            // [v3.4.2] Stochastic (14, 3, 3)
            try
            {
                var (stochK, stochD) = IndicatorCalculator.CalculateStochastic(klines, 14, 3, 3);
                features.Stoch_K = (float)(stochK / 100.0);
                features.Stoch_D = (float)(stochD / 100.0);
            }
            catch { features.Stoch_K = 0.5f; features.Stoch_D = 0.5f; }

            // [v3.4.2] ADX + DI
            if (klines.Count >= 28)
            {
                try
                {
                    var (adx, plusDI, minusDI) = IndicatorCalculator.CalculateADX(klines, 14);
                    features.ADX = (float)(adx / 100.0);
                    features.PlusDI = (float)(plusDI / 100.0);
                    features.MinusDI = (float)(minusDI / 100.0);
                }
                catch { }
            }
        }

        private float CalculateDistanceToSupport(List<IBinanceKline> klines)
        {
            if (klines.Count < 20) return 0f;
            decimal currentPrice = klines[^1].ClosePrice;
            decimal recentLow = klines.TakeLast(20).Min(k => k.LowPrice);
            return currentPrice > 0 ? (float)((currentPrice - recentLow) / currentPrice * 100m) : 0f;
        }

        private float CalculateDistanceToResistance(List<IBinanceKline> klines)
        {
            if (klines.Count < 20) return 0f;
            decimal currentPrice = klines[^1].ClosePrice;
            decimal recentHigh = klines.TakeLast(20).Max(k => k.HighPrice);
            return currentPrice > 0 ? (float)((recentHigh - currentPrice) / currentPrice * 100m) : 0f;
        }

        private float EstimateWavePosition(List<IBinanceKline> klines)
        {
            // 간단한 파동 추정 (실제로는 ElliottWaveCalculator 활용)
            if (klines.Count < 50) return 0f;
            var recent = klines.TakeLast(50).ToList();
            int peaks = 0;
            int troughs = 0;
            
            for (int i = 1; i < recent.Count - 1; i++)
            {
                if (recent[i].HighPrice > recent[i - 1].HighPrice && recent[i].HighPrice > recent[i + 1].HighPrice)
                    peaks++;
                if (recent[i].LowPrice < recent[i - 1].LowPrice && recent[i].LowPrice < recent[i + 1].LowPrice)
                    troughs++;
            }

            return Math.Min((peaks + troughs) / 5.0f, 5.0f); // 1~5 범위로 정규화
        }

        private float CalculateMomentumStrength(List<IBinanceKline> klines)
        {
            if (klines.Count < 20) return 0f;
            var recent = klines.TakeLast(20).ToList();
            decimal priceChange = (recent[^1].ClosePrice - recent[0].ClosePrice) / recent[0].ClosePrice;
            double volumeIncrease = recent.TakeLast(5).Average(k => (double)k.Volume) / 
                                   recent.Take(5).Average(k => (double)k.Volume);
            
            float momentum = (float)(Math.Abs((double)priceChange) * volumeIncrease);
            return Math.Clamp(momentum, 0f, 1f);
        }

        private float CalculatePriceVsSMA(List<IBinanceKline> klines, int period)
        {
            if (klines.Count < period) return 0f;
            double sma = IndicatorCalculator.CalculateSMA(klines, period);
            decimal currentPrice = klines[^1].ClosePrice;
            return sma > 0 ? (float)(((double)currentPrice - sma) / sma * 100.0) : 0f;
        }

        private float CalculateADX(List<IBinanceKline> klines)
        {
            if (klines.Count < 28) return 0f;
            var (adx, _, _) = IndicatorCalculator.CalculateADX(klines, 14);
            return (float)(adx / 100.0); // 0~1 정규화
        }

        private (float plusDI, float minusDI) CalculateDI(List<IBinanceKline> klines)
        {
            if (klines.Count < 28) return (0f, 0f);
            var (_, plusDI, minusDI) = IndicatorCalculator.CalculateADX(klines, 14);
            return ((float)(plusDI / 100.0), (float)(minusDI / 100.0));
        }

        private float CalculateATR(List<IBinanceKline> klines)
        {
            if (klines.Count < 14) return 0f;
            double atr = IndicatorCalculator.CalculateATR(klines, 14);
            decimal avgPrice = klines.TakeLast(14).Average(k => k.ClosePrice);
            return avgPrice > 0 ? (float)(atr / (double)avgPrice) : 0f; // 가격 대비 비율
        }

        /// <summary>
        /// [v4.5.2] 1분봉 MACD 휩소 피처 계산
        /// - 크로스 빈도, MACD-Signal 갭/ATR 비율, RSI 극단 여부, 히스토그램 강도
        /// </summary>
        private static void ExtractM1WhipsawFeatures(List<IBinanceKline>? m1Klines, MultiTimeframeEntryFeature feature)
        {
            if (m1Klines == null || m1Klines.Count < 30)
                return; // 기본값 0 유지

            var closes = m1Klines.Select(k => (double)k.ClosePrice).ToArray();
            int n = closes.Length;

            // MACD 계산
            double[] emaFast = CalcEMA(closes, 12);
            double[] emaSlow = CalcEMA(closes, 26);
            double[] macdLine = new double[n];
            for (int i = 0; i < n; i++)
                macdLine[i] = emaFast[i] - emaSlow[i];
            double[] signalLine = CalcEMA(macdLine, 9);

            // 1) 최근 10봉 내 크로스 횟수
            int crossCount = 0;
            int lookStart = Math.Max(1, n - 10);
            for (int i = lookStart; i < n; i++)
            {
                double prevDiff = macdLine[i - 1] - signalLine[i - 1];
                double currDiff = macdLine[i] - signalLine[i];
                if ((prevDiff > 0 && currDiff <= 0) || (prevDiff < 0 && currDiff >= 0))
                    crossCount++;
            }
            feature.M1_MACD_CrossFlipCount = crossCount;

            // 2) |MACD - Signal| / ATR(14)
            double macdNow = macdLine[^1];
            double signalNow = signalLine[^1];
            double atr14 = CalcATR14(m1Klines);
            feature.M1_MACD_SignalGapRatio = atr14 > 0 ? (float)(Math.Abs(macdNow - signalNow) / atr14) : 0f;

            // 3) RSI 극단 여부 (방향 반대인 극단)
            double rsi = CalcRSI14(m1Klines);
            // RSI < 30 = 과매도 (숏 위험), RSI > 70 = 과매수 (롱 위험)
            feature.M1_RSI_ExtremeZone = (rsi < 30 || rsi > 70) ? 1f : 0f;

            // 4) |hist| / avg|hist| (히스토그램 강도)
            double hist = macdNow - signalNow;
            double avgAbsHist = 0;
            int histLookback = Math.Min(14, n);
            for (int i = n - histLookback; i < n; i++)
                avgAbsHist += Math.Abs(macdLine[i] - signalLine[i]);
            avgAbsHist /= histLookback;
            feature.M1_MACD_HistStrength = avgAbsHist > 0 ? (float)(Math.Abs(hist) / avgAbsHist) : 0f;

            // 5) SecondsSinceOppositeCross: 현재 방향의 반대 크로스가 마지막으로 나온 봉 수 × 60초
            // 현재 방향 = macd > signal → Golden, else Dead
            bool isCurrentGolden = macdNow > signalNow;
            int barsSinceOpposite = 0;
            for (int i = n - 2; i >= Math.Max(0, n - 30); i--)
            {
                bool wasGolden = macdLine[i] > signalLine[i];
                if (wasGolden != isCurrentGolden)
                {
                    barsSinceOpposite = (n - 1) - i;
                    break;
                }
            }
            feature.M1_MACD_SecsSinceOppCross = barsSinceOpposite > 0
                ? barsSinceOpposite * 60f   // 봉 수 × 60초
                : 600f;                     // 30봉 내 반대 크로스 없음 = 안전
        }

        private static double[] CalcEMA(double[] data, int period)
        {
            double[] ema = new double[data.Length];
            double k = 2.0 / (period + 1);
            ema[0] = data[0];
            for (int i = 1; i < data.Length; i++)
                ema[i] = (data[i] - ema[i - 1]) * k + ema[i - 1];
            return ema;
        }

        private static double CalcATR14(List<IBinanceKline> candles)
        {
            int period = 14;
            if (candles.Count < period + 1) return 0;
            double sum = 0;
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                double h = (double)candles[i].HighPrice;
                double l = (double)candles[i].LowPrice;
                double pc = (double)candles[i - 1].ClosePrice;
                sum += Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            }
            return sum / period;
        }

        private static double CalcRSI14(List<IBinanceKline> candles)
        {
            int period = 14;
            if (candles.Count < period + 1) return 50;
            double gainSum = 0, lossSum = 0;
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                double diff = (double)(candles[i].ClosePrice - candles[i - 1].ClosePrice);
                if (diff > 0) gainSum += diff; else lossSum += Math.Abs(diff);
            }
            double avgGain = gainSum / period;
            double avgLoss = lossSum / period;
            if (avgLoss == 0) return 100;
            return 100 - (100 / (1 + avgGain / avgLoss));
        }

        /// <summary>
        /// [v4.5.6] M15/H1 다중 TF 하락추세 감지 (PUMP 진입 방어)
        /// </summary>
        private static void ExtractMultiTfDowntrendFeatures(
            List<IBinanceKline>? m15Klines,
            List<IBinanceKline>? h1Klines,
            MultiTimeframeEntryFeature feature)
        {
            // ── M15 하락추세 ──
            if (m15Klines != null && m15Klines.Count >= 60)
            {
                double sma20 = m15Klines.TakeLast(20).Average(k => (double)k.ClosePrice);
                double sma60 = m15Klines.TakeLast(60).Average(k => (double)k.ClosePrice);
                feature.M15_IsDowntrend = sma20 < sma60 ? 1f : 0f;

                // 연속 음봉 개수 (최근 5봉)
                int consecBearish = 0;
                for (int i = m15Klines.Count - 1; i >= Math.Max(0, m15Klines.Count - 5); i--)
                {
                    if (m15Klines[i].ClosePrice < m15Klines[i].OpenPrice)
                        consecBearish++;
                    else
                        break;
                }
                feature.M15_ConsecBearishCount = consecBearish;

                // M15 RSI 약세
                double m15Rsi = CalcRSI14(m15Klines);
                feature.M15_RSI_BelowNeutral = m15Rsi < 45 ? 1f : 0f;
            }

            // ── H1 하락추세 ──
            if (h1Klines != null && h1Klines.Count >= 60)
            {
                double sma20 = h1Klines.TakeLast(20).Average(k => (double)k.ClosePrice);
                double sma60 = h1Klines.TakeLast(60).Average(k => (double)k.ClosePrice);
                feature.H1_IsDowntrend = sma20 < sma60 ? 1f : 0f;

                double currentPrice = (double)h1Klines[^1].ClosePrice;
                feature.H1_PriceBelowSma60 = currentPrice < sma60 ? 1f : 0f;
            }
        }

        /// <summary>
        /// [v4.6.2/v4.6.3] M15 단타 보조지표 계산 (EMA, VWAP, StochRSI, BB스퀴즈, SuperTrend, DailyPivot)
        /// </summary>
        private static void ExtractM15TradingViewFeatures(
            List<IBinanceKline>? m15Klines,
            List<IBinanceKline>? d1Klines,
            MultiTimeframeEntryFeature feature)
        {
            if (m15Klines == null || m15Klines.Count < 50) return;

            // EMA 9/21/50
            double ema9 = CalcEMALast(m15Klines, 9);
            double ema21 = CalcEMALast(m15Klines, 21);
            double ema50 = CalcEMALast(m15Klines, 50);
            float emaCrossState = 0f;
            if (ema9 > 0 && ema21 > 0 && ema50 > 0)
            {
                if (ema9 > ema21 && ema21 > ema50) emaCrossState = 1f;
                else if (ema9 < ema21 && ema21 < ema50) emaCrossState = -1f;
            }
            feature.M15_EMA_CrossState = emaCrossState;

            // VWAP — 최근 60봉 거래량 가중 평균
            int vwapLookback = Math.Min(60, m15Klines.Count);
            double sumPV = 0, sumV = 0;
            foreach (var k in m15Klines.TakeLast(vwapLookback))
            {
                double typical = (double)(k.HighPrice + k.LowPrice + k.ClosePrice) / 3.0;
                double vol = (double)k.Volume;
                sumPV += typical * vol;
                sumV += vol;
            }
            double vwap = sumV > 0 ? sumPV / sumV : 0;
            double currClose = (double)m15Klines[^1].ClosePrice;
            feature.M15_Price_VWAP_Distance_Pct = vwap > 0
                ? (float)((currClose - vwap) / vwap * 100.0)
                : 0f;

            // StochRSI (14, 14, 3, 3) — 간이 계산
            var (stochK, stochD) = CalcStochRSILast(m15Klines, 14, 14, 3);
            feature.M15_StochRSI_K = (float)stochK;
            feature.M15_StochRSI_D = (float)stochD;
            feature.M15_StochRSI_Cross = stochK > stochD ? 1f : (stochK < stochD ? -1f : 0f);

            // ── [v4.6.3] 추가 단타 지표 ────────────────────────────────────────────

            // BB 밴드 폭 % (스퀴즈 감지 — 낮을수록 폭발 직전)
            if (m15Klines.Count >= 20)
            {
                var bbSlice = m15Klines.TakeLast(20).Select(k => (double)k.ClosePrice).ToList();
                double bbSma = bbSlice.Average();
                double bbVariance = bbSlice.Average(p => Math.Pow(p - bbSma, 2));
                double bbStd = Math.Sqrt(bbVariance);
                feature.M15_BB_Width_Pct = bbSma > 0
                    ? (float)((4.0 * bbStd) / bbSma * 100.0) // (upper-lower)/mid = 4*std/sma
                    : 0f;
            }

            // SuperTrend (ATR period=10, multiplier=3)
            if (m15Klines.Count >= 12)
            {
                const int stPeriod = 10;
                const double stMult = 3.0;

                // True Range 시리즈
                var tr = new double[m15Klines.Count - 1];
                for (int i = 1; i < m15Klines.Count; i++)
                {
                    double hl = (double)(m15Klines[i].HighPrice - m15Klines[i].LowPrice);
                    double hc = Math.Abs((double)(m15Klines[i].HighPrice - m15Klines[i - 1].ClosePrice));
                    double lc = Math.Abs((double)(m15Klines[i].LowPrice - m15Klines[i - 1].ClosePrice));
                    tr[i - 1] = Math.Max(hl, Math.Max(hc, lc));
                }

                double finalUpper = 0, finalLower = 0;
                int stDir = 1; // 1=상승, -1=하락

                for (int i = stPeriod - 1; i < tr.Length; i++)
                {
                    // SMA ATR (Wilder smoothing 근사)
                    double atr = 0;
                    for (int j = i - stPeriod + 1; j <= i; j++) atr += tr[j];
                    atr /= stPeriod;

                    var c = m15Klines[i + 1]; // tr[i] = candle[i+1]
                    double hl2 = ((double)c.HighPrice + (double)c.LowPrice) / 2.0;
                    double rawUpper = hl2 + stMult * atr;
                    double rawLower = hl2 - stMult * atr;

                    double prevClose = (double)m15Klines[i].ClosePrice;
                    double newUpper = (rawUpper < finalUpper || prevClose > finalUpper) ? rawUpper : finalUpper;
                    double newLower = (rawLower > finalLower || prevClose < finalLower) ? rawLower : finalLower;

                    double close = (double)c.ClosePrice;
                    if (i == stPeriod - 1)
                        stDir = close >= newLower ? 1 : -1;
                    else if (stDir == 1 && close < newLower)
                        stDir = -1;
                    else if (stDir == -1 && close > newUpper)
                        stDir = 1;

                    finalUpper = newUpper;
                    finalLower = newLower;
                }
                feature.M15_SuperTrend_Direction = (float)stDir;
            }

            // Daily Pivot R1 / S1 (전일 완성 캔들 기준)
            if (d1Klines != null && d1Klines.Count >= 2)
            {
                var prev = d1Klines[^2]; // 전일 완성봉 (미래 참조 방지)
                double pivH = (double)prev.HighPrice;
                double pivL = (double)prev.LowPrice;
                double pivC = (double)prev.ClosePrice;
                double pivot = (pivH + pivL + pivC) / 3.0;
                double r1 = 2.0 * pivot - pivL;
                double s1 = 2.0 * pivot - pivH;
                double price = currClose; // 위에서 계산한 currClose 재사용
                feature.M15_DailyPivot_R1_Dist_Pct = price > 0 ? (float)((r1 - price) / price * 100.0) : 0f;
                feature.M15_DailyPivot_S1_Dist_Pct = price > 0 ? (float)((s1 - price) / price * 100.0) : 0f;
            }
        }

        private static double CalcEMALast(List<IBinanceKline> candles, int period)
        {
            if (candles.Count < period) return 0;
            double mult = 2.0 / (period + 1);
            double ema = (double)candles.Take(period).Average(k => k.ClosePrice);
            for (int i = period; i < candles.Count; i++)
            {
                double price = (double)candles[i].ClosePrice;
                ema = (price - ema) * mult + ema;
            }
            return ema;
        }

        private static (double K, double D) CalcStochRSILast(
            List<IBinanceKline> candles, int rsiPeriod, int stochPeriod, int smooth)
        {
            int needed = rsiPeriod + stochPeriod + smooth + 1;
            if (candles.Count < needed) return (50, 50);

            // RSI 시리즈
            var rsiSeries = new List<double>();
            for (int i = rsiPeriod; i < candles.Count; i++)
            {
                double gain = 0, loss = 0;
                for (int j = i - rsiPeriod + 1; j <= i; j++)
                {
                    double diff = (double)(candles[j].ClosePrice - candles[j - 1].ClosePrice);
                    if (diff > 0) gain += diff; else loss -= diff;
                }
                double avgGain = gain / rsiPeriod;
                double avgLoss = loss / rsiPeriod;
                double rs = avgLoss == 0 ? 100 : avgGain / avgLoss;
                double rsi = 100.0 - (100.0 / (1 + rs));
                rsiSeries.Add(rsi);
            }
            if (rsiSeries.Count < stochPeriod) return (50, 50);

            // StochRSI = (RSI - RSI_low) / (RSI_high - RSI_low) * 100
            var stochSeries = new List<double>();
            for (int i = stochPeriod - 1; i < rsiSeries.Count; i++)
            {
                var window = rsiSeries.GetRange(i - stochPeriod + 1, stochPeriod);
                double low = window.Min();
                double high = window.Max();
                double stoch = (high - low) > 0 ? (rsiSeries[i] - low) / (high - low) * 100 : 50;
                stochSeries.Add(stoch);
            }
            if (stochSeries.Count < smooth * 2) return (stochSeries.Last(), stochSeries.Last());

            // K = SMA(stoch, smooth), D = SMA(K, smooth)
            double kVal = stochSeries.Skip(stochSeries.Count - smooth).Average();
            // D는 K의 이전 smooth개 평균 (여기선 단순화: 최근 smooth*2 평균)
            double dVal = stochSeries.Skip(stochSeries.Count - smooth * 2).Take(smooth).Average();
            return (kVal, dVal);
        }

        // [v5.10.75 Phase 2] 고점 진입 학습용 피처 — ChOP/M/AAVE 같은 "다중 TF 고점 + 윗꼬리 빨간봉" 케이스를 ML이 학습
        // 하드코딩 차단 대신 11개 feature 제공 → ML이 스스로 "이 진입 위험함" 판단
        private static void ExtractEntryPositionFeatures(List<IBinanceKline> m15Klines, List<IBinanceKline>? m1Klines, MultiTimeframeEntryFeature feature)
        {
            if (m15Klines == null || m15Klines.Count == 0) return;
            decimal currentPrice = m15Klines[^1].ClosePrice;

            // --- M1: 직전 1분봉 ---
            float m1Position = 0.5f;
            if (m1Klines != null && m1Klines.Count > 0)
            {
                var m1 = m1Klines[^1];
                decimal m1Range = m1.HighPrice - m1.LowPrice;
                if (m1Range > 0m)
                {
                    m1Position = (float)((currentPrice - m1.LowPrice) / m1Range);
                    feature.M1_Rise_From_Low_Pct = m1.LowPrice > 0m
                        ? (float)((currentPrice - m1.LowPrice) / m1.LowPrice * 100m) : 0f;
                    feature.M1_Pullback_From_High_Pct = m1.HighPrice > 0m
                        ? (float)((m1.HighPrice - currentPrice) / m1.HighPrice * 100m) : 0f;
                }
            }

            // --- M5: 직전 5분봉 (완성 봉 1개 전) - 현재 15분봉 캐시를 써야 하나 m5 없으므로 m15[^2] 사용 대신 m15 자체의 직전 캔들 활용 ---
            // 5분봉 별도 fetch 비용 때문에 m15 기반으로 근사: m15 직전 완성봉의 range 사용
            float m5Position = 0.5f;
            if (m15Klines.Count >= 2)
            {
                var prev15 = m15Klines[^2];
                decimal prev15Range = prev15.HighPrice - prev15.LowPrice;
                if (prev15Range > 0m)
                {
                    m5Position = (float)((currentPrice - prev15.LowPrice) / prev15Range);
                    feature.Price_Position_In_Prev5m_Range = m5Position;
                    feature.Prev_5m_Rise_From_Low_Pct = prev15.LowPrice > 0m
                        ? (float)((currentPrice - prev15.LowPrice) / prev15.LowPrice * 100m) : 0f;
                }
            }

            // --- M15: 현재 15분봉 진행중 ---
            var cur15 = m15Klines[^1];
            decimal cur15Range = cur15.HighPrice - cur15.LowPrice;
            decimal cur15Body = Math.Abs(cur15.ClosePrice - cur15.OpenPrice);
            float m15Position = 0.5f;
            if (cur15Range > 0m)
            {
                m15Position = (float)((currentPrice - cur15.LowPrice) / cur15Range);
                feature.M15_Position_In_Range = m15Position;
                feature.M15_Rise_From_Low_Pct = cur15.LowPrice > 0m
                    ? (float)((currentPrice - cur15.LowPrice) / cur15.LowPrice * 100m) : 0f;
                decimal bodyMax = cur15.ClosePrice > cur15.OpenPrice ? cur15.ClosePrice : cur15.OpenPrice;
                feature.M15_Upper_Shadow_Ratio = (float)((cur15.HighPrice - bodyMax) / cur15Range);
            }
            feature.M15_Is_Red_Candle = cur15.ClosePrice < cur15.OpenPrice ? 1f : 0f;

            // --- 다중 TF 고점 confluence ---
            feature.MultiTF_Top_Confluence_Score = (m1Position + m5Position + m15Position) / 3f;
        }

        // [v5.10.75 Phase 2] 심볼별 최근 30일 성과 피처 (DB 조회, 10분 캐시)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Time, float WinRate, float AvgPnlPct)> _symbolPerfCache
            = new(StringComparer.OrdinalIgnoreCase);

        private static void ExtractSymbolRecentPerformanceFeatures(string symbol, MultiTimeframeEntryFeature feature)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;

            if (_symbolPerfCache.TryGetValue(symbol, out var cached)
                && (DateTime.Now - cached.Time).TotalMinutes < 10)
            {
                feature.Symbol_Recent_WinRate_30d = cached.WinRate;
                feature.Symbol_Recent_AvgPnLPct_30d = cached.AvgPnlPct;
                return;
            }

            try
            {
                using var cn = new Microsoft.Data.SqlClient.SqlConnection(AppConfig.ConnectionString);
                cn.Open();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = @"
SELECT COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ISNULL(AVG(PnLPercent),0) AS AvgPct
FROM dbo.TradeHistory WITH (NOLOCK)
WHERE Symbol=@Symbol AND IsClosed=1 AND EntryTime>=DATEADD(day,-30,GETDATE())";
                cmd.CommandTimeout = 3;
                var p = cmd.CreateParameter(); p.ParameterName = "@Symbol"; p.Value = symbol;
                cmd.Parameters.Add(p);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    int n = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    int wins = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    decimal avgPct = r.IsDBNull(2) ? 0m : r.GetDecimal(2);
                    float winRate = n > 0 ? (float)wins / n : 0.5f;  // 데이터 없으면 중립 0.5
                    float avgPctNorm = Math.Clamp((float)avgPct / 100f, -1f, 1f);
                    _symbolPerfCache[symbol] = (DateTime.Now, winRate, avgPctNorm);
                    feature.Symbol_Recent_WinRate_30d = winRate;
                    feature.Symbol_Recent_AvgPnLPct_30d = avgPctNorm;
                }
            }
            catch { /* DB 실패 무시 — feature 기본값 0 */ }
        }

        private void ExtractTimeContext(DateTime timestamp, MultiTimeframeEntryFeature feature)
        {
            feature.HourOfDay = timestamp.Hour;
            feature.DayOfWeek = (float)timestamp.DayOfWeek;

            // 아시아 세션: 00:00~09:00 UTC
            feature.IsAsianSession = (timestamp.Hour >= 0 && timestamp.Hour < 9) ? 1f : 0f;

            // 유럽 세션: 07:00~16:00 UTC
            feature.IsEuropeSession = (timestamp.Hour >= 7 && timestamp.Hour < 16) ? 1f : 0f;

            // 미국 세션: 13:00~22:00 UTC
            feature.IsUSSession = (timestamp.Hour >= 13 && timestamp.Hour < 22) ? 1f : 0f;
        }

        private int FindCandleIndex(List<IBinanceKline> klines, DateTime targetTime)
        {
            for (int i = 0; i < klines.Count; i++)
            {
                if (klines[i].OpenTime >= targetTime)
                    return i;
            }
            return klines.Count - 1;
        }

        private List<IBinanceKline> AggregateCandlesFromM15(
            List<IBinanceKline> m15Candles,
            TimeSpan bucket,
            KlineInterval interval)
        {
            var result = new List<IBinanceKline>();

            if (m15Candles == null || m15Candles.Count == 0)
                return result;

            var grouped = m15Candles
                .OrderBy(k => k.OpenTime)
                .GroupBy(k => TruncateToBucketUtc(k.OpenTime, bucket))
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                var ordered = group.OrderBy(k => k.OpenTime).ToList();
                if (ordered.Count == 0)
                    continue;

                decimal open = ordered[0].OpenPrice;
                decimal close = ordered[^1].ClosePrice;
                decimal high = ordered.Max(k => k.HighPrice);
                decimal low = ordered.Min(k => k.LowPrice);
                decimal volume = ordered.Sum(k => k.Volume);

                var candle = new CandleData
                {
                    OpenTime = group.Key,
                    CloseTime = ordered[^1].CloseTime,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = (float)volume
                };

                result.Add(new BinanceKlineAdapter(candle, interval));
            }

            return result;
        }

        private static DateTime TruncateToBucketUtc(DateTime time, TimeSpan bucket)
        {
            DateTime utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
            long ticks = utc.Ticks / bucket.Ticks * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>
        /// 피보나치 되돌림 레벨 계산 (AI 특징으로 사용)
        /// </summary>
        private (float distanceTo0382, float distanceTo0618, float distanceTo0786, float inEntryZone) 
            CalculateFibonacciFeatures(List<IBinanceKline> klines, decimal currentPrice)
        {
            if (klines.Count < 50)
                return (0f, 0f, 0f, 0f);

            var recent50 = klines.TakeLast(50).ToList();

            var (hasConfirmedPivotRange, high, low) = TryGetConfirmedPivotRange(recent50, confirmationBars: 3);
            if (!hasConfirmedPivotRange)
                return (0f, 0f, 0f, 0f);

            decimal range = high - low;

            if (range == 0 || currentPrice == 0)
                return (0f, 0f, 0f, 0f);

            // 피보나치 레벨 계산
            decimal fib0382 = high - range * 0.382m;
            decimal fib0618 = high - range * 0.618m;
            decimal fib0786 = high - range * 0.786m;

            // 현재가에서 각 레벨까지 거리 (%)
            float distTo0382 = (float)(Math.Abs(currentPrice - fib0382) / currentPrice * 100m);
            float distTo0618 = (float)(Math.Abs(currentPrice - fib0618) / currentPrice * 100m);
            float distTo0786 = (float)(Math.Abs(currentPrice - fib0786) / currentPrice * 100m);

            // 진입 구간(0.382~0.618) 내부 여부
            bool inZone = currentPrice >= fib0618 && currentPrice <= fib0382;
            float inEntryZone = inZone ? 1f : 0f;

            return (distTo0382, distTo0618, distTo0786, inEntryZone);
        }

        private (bool success, decimal high, decimal low) TryGetConfirmedPivotRange(
            List<IBinanceKline> klines,
            int confirmationBars)
        {
            if (klines == null || klines.Count < confirmationBars * 2 + 5)
                return (false, 0m, 0m);

            int maxConfirmedIndex = klines.Count - 1 - confirmationBars;
            if (maxConfirmedIndex <= confirmationBars)
                return (false, 0m, 0m);

            int lastPivotHighIndex = -1;
            int lastPivotLowIndex = -1;

            for (int i = confirmationBars; i <= maxConfirmedIndex; i++)
            {
                bool isPivotHigh = true;
                bool isPivotLow = true;

                decimal currentHigh = klines[i].HighPrice;
                decimal currentLow = klines[i].LowPrice;

                for (int j = 1; j <= confirmationBars; j++)
                {
                    if (currentHigh <= klines[i - j].HighPrice || currentHigh < klines[i + j].HighPrice)
                        isPivotHigh = false;

                    if (currentLow >= klines[i - j].LowPrice || currentLow > klines[i + j].LowPrice)
                        isPivotLow = false;

                    if (!isPivotHigh && !isPivotLow)
                        break;
                }

                if (isPivotHigh)
                    lastPivotHighIndex = i;

                if (isPivotLow)
                    lastPivotLowIndex = i;
            }

            // 피벗 탐지 실패 시에도 tail(미확정 구간)을 제외한 확정 구간으로 안전 fallback
            var confirmedSlice = klines.Take(maxConfirmedIndex + 1).ToList();
            if (confirmedSlice.Count == 0)
                return (false, 0m, 0m);

            decimal high = lastPivotHighIndex >= 0 ? klines[lastPivotHighIndex].HighPrice : confirmedSlice.Max(k => k.HighPrice);
            decimal low = lastPivotLowIndex >= 0 ? klines[lastPivotLowIndex].LowPrice : confirmedSlice.Min(k => k.LowPrice);

            if (high <= low)
                return (false, 0m, 0m);

            return (true, high, low);
        }
    }
}
