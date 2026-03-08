using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using Binance.Net.Enums;
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

        public MultiTimeframeFeatureExtractor(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        }

        /// <summary>
        /// 특정 시점의 Multi-Timeframe Feature 추출 (실시간 예측용)
        /// </summary>
        public async Task<MultiTimeframeEntryFeature?> ExtractRealtimeFeatureAsync(
            string symbol,
            DateTime timestamp,
            CancellationToken token = default)
        {
            try
            {
                // 각 타임프레임 캔들 수집 (병렬 실행)
                var tasks = new[]
                {
                    _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneDay, 50, token),
                    _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour, 120, token),
                    _exchangeService.GetKlinesAsync(symbol, KlineInterval.TwoHour, 120, token),
                    _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneHour, 200, token),
                    _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 260, token)
                };

                await Task.WhenAll(tasks);

                var d1Klines = tasks[0].Result?.ToList();
                var h4Klines = tasks[1].Result?.ToList();
                var h2Klines = tasks[2].Result?.ToList();
                var h1Klines = tasks[3].Result?.ToList();
                var m15Klines = tasks[4].Result?.ToList();

                if (d1Klines == null || d1Klines.Count < 30 ||
                    h4Klines == null || h4Klines.Count < 60 ||
                    h2Klines == null || h2Klines.Count < 60 ||
                    h1Klines == null || h1Klines.Count < 100 ||
                    m15Klines == null || m15Klines.Count < 130)
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

            feature.H4_Trend = h4.Trend;
            feature.H4_RSI = h4.RSI;
            feature.H4_MACD = h4.MACD;
            feature.H4_Signal = h4.Signal;
            feature.H4_BBPosition = h4.BBPosition;
            feature.H4_Volume_Ratio = h4.VolumeRatio;
            feature.H4_DistanceToSupport = CalculateDistanceToSupport(h4Klines);
            feature.H4_DistanceToResist = CalculateDistanceToResistance(h4Klines);

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

            feature.M15_RSI = m15.RSI;
            feature.M15_MACD = m15.MACD;
            feature.M15_Signal = m15.Signal;
            feature.M15_BBPosition = m15.BBPosition;
            feature.M15_Volume_Ratio = m15.VolumeRatio;
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
        }

        private struct TimeframeFeatures
        {
            public float Trend;
            public float RSI;
            public float MACD;
            public float Signal;
            public float BBPosition;
            public float VolumeRatio;
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
            var (macd, signal, _) = IndicatorCalculator.CalculateMACD(klines);
            features.MACD = (float)macd;
            features.Signal = (float)signal;

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

        /// <summary>
        /// 피보나치 되돌림 레벨 계산 (AI 특징으로 사용)
        /// </summary>
        private (float distanceTo0382, float distanceTo0618, float distanceTo0786, float inEntryZone) 
            CalculateFibonacciFeatures(List<IBinanceKline> klines, decimal currentPrice)
        {
            if (klines.Count < 50)
                return (0f, 0f, 0f, 0f);

            var recent50 = klines.TakeLast(50).ToList();
            decimal high = recent50.Max(k => k.HighPrice);
            decimal low = recent50.Min(k => k.LowPrice);
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
    }
}
