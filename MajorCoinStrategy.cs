using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    public class MajorCoinStrategy : ITradingStrategy
    {
        private static readonly TimeZoneInfo SeoulTimeZone = GetSeoulTimeZone();
        private readonly MarketDataManager _marketData;
        private readonly Func<TradingSettings?>? _settingsAccessor;

        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string, string, decimal>? OnTradeSignal; // symbol, decision, price
        public event Action<string>? OnLog;

        public MajorCoinStrategy(MarketDataManager marketData, Func<TradingSettings?>? settingsAccessor = null)
        {
            _marketData = marketData;
            _settingsAccessor = settingsAccessor;
        }

#pragma warning disable CS1998 // 비동기 메서드에 await 연산자가 없습니다.
        public Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
#pragma warning restore CS1998
        {
            try
            {
                if (!_marketData.KlineCache.TryGetValue(symbol, out var cache)) return Task.CompletedTask;

                List<IBinanceKline> list;
                lock (cache)
                {
                    list = cache.ToList(); // Thread-safe copy
                }

                if (list.Count < 120) return Task.CompletedTask;

                double rsi = IndicatorCalculator.CalculateRSI(list, 14);
                var bb = IndicatorCalculator.CalculateBB(list, 20, 2);
                bool isUptrend = IndicatorCalculator.AnalyzeElliottWave(list);
                var macd = IndicatorCalculator.CalculateMACD(list);
                var fib = IndicatorCalculator.CalculateFibonacci(list, 100);
                double sma20 = IndicatorCalculator.CalculateSMA(list, 20);
                double sma50 = IndicatorCalculator.CalculateSMA(list, 50);
                double sma60 = IndicatorCalculator.CalculateSMA(list, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(list, 120);
                string maState = sma20 > sma60 && sma60 > sma120 ? "BULL" : (sma20 < sma60 && sma60 < sma120 ? "BEAR" : "MIX");
                string fibPos = currentPrice >= (decimal)fib.Level382 ? "ABOVE382" : (currentPrice <= (decimal)fib.Level618 ? "BELOW618" : "MID");

                var recent20 = list.TakeLast(20).ToList();
                double avgVolume20 = recent20.Any() ? recent20.Average(k => (double)k.Volume) : 0;
                // [안전성] recent20 비어있으면 0 반환
                double currentVolume = (recent20.Count > 0) ? (double)recent20[recent20.Count - 1].Volume : 0;
                double volumeRatio = avgVolume20 > 0 ? currentVolume / avgVolume20 : 1;

                var recent3 = list.TakeLast(3).ToList();
                var previous10 = list.Skip(Math.Max(0, list.Count - 13)).Take(10).ToList();
                double avgVolume3 = recent3.Any() ? recent3.Average(k => (double)k.Volume) : 0;
                double avgVolumePrev10 = previous10.Any() ? previous10.Average(k => (double)k.Volume) : 0;
                double volumeMomentum = avgVolumePrev10 > 0 ? avgVolume3 / avgVolumePrev10 : 1;

                MajorProfile profile = ResolveProfile();
                bool isMakingHigherLows = IsMakingHigherLows(list, profile.HigherLowSegmentSize, profile.HigherLowMinRiseRatio);
                bool isTrendHealthyOnLowVolume = currentPrice > (decimal)sma20 && sma20 > sma50 && rsi > 50;
                bool allowLowVolumeTrendBypass = volumeMomentum < profile.LongConfirmVolumeMin &&
                                                 (isTrendHealthyOnLowVolume || (isMakingHigherLows && currentPrice > (decimal)sma20));

                // [v3.2.3] 가격 모멘텀 직접 감지 — SMA 지연 보완
                // 최근 6봉(30분) 가격 변화율로 실시간 상승 판단
                var recent6 = list.TakeLast(6).ToList();
                decimal price6Ago = recent6.First().ClosePrice;
                double priceRecoveryPct = price6Ago > 0 ? (double)((currentPrice - price6Ago) / price6Ago * 100) : 0;
                bool isPriceRecovering = priceRecoveryPct >= 1.5; // 30분간 +1.5% 이상 상승 (20배 = ROE 30%)

                // 최근 12봉(1시간) 저점 대비 상승
                var recent12 = list.TakeLast(12).ToList();
                decimal recentLow = recent12.Min(k => k.LowPrice);
                double bounceFromLowPct = recentLow > 0 ? (double)((currentPrice - recentLow) / recentLow * 100) : 0;
                bool isStrongBounce = bounceFromLowPct >= 3.0; // 1시간 저점 대비 +3% 반등

                // [v3.2.4] 하락 모멘텀 직접 감지 (SHORT 용)
                bool isMakingLowerHighs = IsMakingLowerHighs(list, profile.HigherLowSegmentSize);
                double priceDropPct = price6Ago > 0 ? (double)((price6Ago - currentPrice) / price6Ago * 100) : 0;
                bool isPriceDropping = priceDropPct >= 1.5; // 30분간 -1.5% 하락
                decimal recentHigh = recent12.Max(k => k.HighPrice);
                double dropFromHighPct = recentHigh > 0 ? (double)((recentHigh - currentPrice) / recentHigh * 100) : 0;
                bool isStrongDrop = dropFromHighPct >= 3.0; // 1시간 고점 대비 -3% 하락

                // [v3.2.7] AI 최우선 진입: 규칙 기반 점수는 참고용, 방향은 모멘텀으로 판단 → AI가 최종 결정
                int aiScore = CalculateScore(rsi, bb, currentPrice, isUptrend, macd, fib,
                    sma20, sma50, sma60, sma120, volumeMomentum, isMakingHigherLows,
                    allowLowVolumeTrendBypass, profile);
                double fibBonus = CalculateFibScore(list, currentPrice);
                if (fibBonus > 0) aiScore = Math.Clamp(aiScore + (int)fibBonus, 0, 100);

                // [v5.0.2] 경계 추가 완화 — UserId=1 PC#1 이틀간 메이저 진입 0건 해결
                // 원인: 메이저 5m 변동성이 작아 30m +1.5% / 1h +3% 모멘텀 조건 도달 불가
                // 해결: 3단계 OR 조건으로 aiScore 기반 진입 허용
                //  [1] aiScore >= 70 → 순수 점수 단독 LONG (메이저는 70점 넘으면 확실한 상승)
                //  [2] aiScore >= 62 + 모멘텀 확인 → LONG
                //  [3] aiScore >= 58 + Higher Lows + sma20 위 → LONG (구조 기반)
                string decision = "WAIT";

                // [v5.1.7] 메이저 로직 전면 재설계 — 30일 통계 기반
                // SHORT 승률 5% (-$1,004) → 완전 차단
                // LONG 승률 20% (-$100) → 강한 조건만 허용
                // 횡보 필터 유지
                decimal rangeHigh1h = recent12.Max(k => k.HighPrice);
                decimal rangeLow1h = recent12.Min(k => k.LowPrice);
                float rangePercent1h = rangeLow1h > 0 ? (float)((rangeHigh1h - rangeLow1h) / rangeLow1h * 100) : 0;

                if (rangePercent1h < 0.5f)
                {
                    decision = "WAIT";
                }
                // LONG 만 허용 — 강한 조건
                // [v5.4.9] Tier 1,2: BB 중심선(SMA20) 위에서만 LONG (하락 추세 진입 방지)
                // Tier 3 (하단 반등)은 SMA20 아래 허용
                {
                    bool aboveBbMid = currentPrice > (decimal)sma20;
                    if (aiScore >= 70 && aboveBbMid)
                        decision = "LONG";
                    else if (aiScore >= 62 && (isPriceRecovering || isStrongBounce) && aboveBbMid)
                        decision = "LONG";
                    else if (isStrongBounce && isMakingHigherLows && rsi > 52)
                        decision = "LONG"; // 하단 반등은 SMA20 아래 허용
                }
                // SHORT 완전 차단 — 30일 승률 5%, -$1,004
                // else if (aiScore <= 30) decision = "SHORT"; ← 제거

                try
                {
                    OnSignalAnalyzed?.Invoke(new MultiTimeframeViewModel
                    {
                        Symbol = symbol,
                        LastPrice = currentPrice,
                        RSI_1H = rsi,
                        AIScore = aiScore,
                        Decision = decision,
                        StrategyName = $"Major Scalping(5m) [{profile.Name}]",
                        SignalSource = "MAJOR",
                        ShortLongScore = aiScore,
                        ShortShortScore = 100 - aiScore,
                        MacdHist = macd.Hist,
                        ElliottTrend = isUptrend ? "UP" : "DOWN",
                        MAState = maState,
                        FibPosition = fibPos,
                        VolumeRatioValue = volumeMomentum,
                        VolumeRatio = $"{volumeMomentum:F2}x",
                        BBPosition = currentPrice >= (decimal)bb.Upper ? "Upper" : (currentPrice <= (decimal)bb.Lower ? "Lower" : "Mid")
                    });
                }
                catch (Exception eventEx)
                {
                    OnLog?.Invoke($"⚠️ [MajorCoinStrategy] {symbol} 시그널 UI 반영 오류: {eventEx.Message}");
                    System.Diagnostics.Debug.WriteLine(eventEx);
                }

                if (!isUptrend && isMakingHigherLows && currentPrice > (decimal)sma20)
                {
                    OnLog?.Invoke("ℹ️ 횡보 중이나 저점 상승 확인 - 추세 점수 가산");
                }

                if (allowLowVolumeTrendBypass)
                {
                    OnLog?.Invoke("ℹ️ 거래량은 적으나 EMA/RSI 추세 지지 확인 - 거래량 감점 우회");
                }

                string decisionKr = decision switch
                {
                    "LONG" => "LONG",
                    "SHORT" => "SHORT",
                    _ => "WAIT"
                };

                // AI 필터는 엔진(ExecuteAutoOrder)에서 최종 판정되므로, 여기서는 사전 체크포인트만 안내
                string aiFilterInfo = "";
                if (decision == "LONG" || decision == "SHORT")
                {
                    try
                    {
                        var settings = _settingsAccessor?.Invoke();
                        
                        // [개선] AI 필터 통과 조건 미리 안내
                        var filterHints = new List<string>();
                        
                        if (volumeMomentum < 1.0)
                            filterHints.Add($"거래량{volumeMomentum:F2}x");
                        
                        if (rsi < 40)
                            filterHints.Add($"RSI{rsi:F0}↓");
                        
                        if (!isUptrend && !(sma20 > sma60))
                            filterHints.Add("정배열✗");
                        
                        _ = settings;

                        string hintText = filterHints.Count > 0 ? string.Join(", ", filterHints) : "없음";
                        aiFilterInfo = filterHints.Count > 0
                            ? $"AI 사전체크: {hintText}"
                            : "AI 사전체크: 기본조건 통과";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MajorCoinStrategy] Settings access failed: {ex.Message}");
                        aiFilterInfo = "AI 사전체크: 평가 예정";
                    }
                }

                if (decision == "WAIT")
                {
                    // WAIT는 조용히 건너뜀
                }
                else
                {
                    OnLog?.Invoke($"📊 [{symbol}] {decisionKr} → AI 판단 요청 | 가격 ${currentPrice:F2} | aiScore={aiScore} | RSI {rsi:F1} | Vol {volumeMomentum:F2}x");

                    try
                    {
                        OnLog?.Invoke(TradingStateLogger.EvaluatingAIGate(symbol, decision, currentPrice));
                        OnTradeSignal?.Invoke(symbol, decision, currentPrice);
                    }
                    catch (Exception eventEx)
                    {
                        OnLog?.Invoke($"⚠️ [MajorCoinStrategy] {symbol} 주문 이벤트 반영 오류: {eventEx.Message}");
                        System.Diagnostics.Debug.WriteLine(eventEx);
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [MajorCoinStrategy] {symbol} 분석 오류: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex);
                return Task.CompletedTask;
            }
        }

        private int CalculateScore(
            double rsi,
            BBResult bb,
            decimal currentPrice,
            bool isUptrend,
            (double Macd, double Signal, double Hist) macd,
            (double Level236, double Level382, double Level500, double Level618) fib,
            double sma20,
            double sma50,
            double sma60,
            double sma120,
            double volumeMomentum,
            bool isMakingHigherLows,
            bool allowLowVolumeTrendBypass,
            MajorProfile profile)
        {
            int score = 50;

            // [v4.9.8] 대칭 스코어링 — 기존 LONG 편향 수정 (SHORT 21시간 0건 원인)
            if (isUptrend) score += 12;
            else score -= 12;

            if (sma20 > sma60) score += 10;
            else score -= 10;

            if (sma60 > sma120) score += 8;
            else score -= 8;

            // [v4.9.8] 가격-SMA 정/역배열 대칭
            if (currentPrice > (decimal)sma20 && sma20 > sma50) score += 10;
            else if (currentPrice < (decimal)sma20 && sma20 < sma50) score -= 10;

            // [v4.9.8] RSI 중립 보너스 제거 — sideway 박스 탈출
            if (rsi >= 55 && rsi <= 68) score += 8;          // 상승 모멘텀 구간
            else if (rsi >= 32 && rsi <= 45) score -= 8;     // 하락 모멘텀 구간
            else if (rsi > 75) score -= 10;                   // 과매수
            else if (rsi < 25) score += 6;                    // 과매도 (반등 가능)

            if (macd.Hist > 0) score += 10;
            else score -= 10;

            if (currentPrice >= (decimal)fib.Level382) score += 6;
            if (currentPrice < (decimal)fib.Level618) score -= 10;

            double price = (double)currentPrice;
            if (price >= bb.Mid) score += 6;
            else score -= 6;

            if (price > bb.Upper && rsi > 72) score -= 8;
            else if (price < bb.Lower && rsi < 30) score += 6;

            // [v4.9.8] 거래량 대칭 — 하락 추세 + 거래량 터지면 SHORT 가속
            if (volumeMomentum >= 1.10)
            {
                score += (sma20 > sma60 ? 10 : -10);  // 추세 방향으로 가점/감점
            }
            else if (volumeMomentum >= 1.00)
            {
                score += (sma20 > sma60 ? 5 : -5);
            }
            else if (allowLowVolumeTrendBypass) score += 15;

            if (isMakingHigherLows && currentPrice > (decimal)sma20) score += profile.HigherLowBonus;

            return Math.Clamp(score, 0, 100);
        }

        private static int CalculateDynamicThreshold(double volumeMomentum, bool isMakingHigherLows, MajorProfile profile)
        {
            // [v3.2.3] 24시간 동일 기준 60점
            int threshold = 60;

            if (volumeMomentum >= 1.10) threshold -= 3;
            if (isMakingHigherLows) threshold -= profile.HigherLowThresholdDiscount;

            return Math.Max(55, threshold);
        }

        private static bool IsMakingHigherLows(List<IBinanceKline> candles, int segmentSize, decimal minRiseRatio)
        {
            const int requiredSegments = 3;
            int requiredCandles = segmentSize * requiredSegments;
            if (candles.Count < requiredCandles) return false;

            var window = candles.TakeLast(requiredCandles).ToList();

            decimal low1 = window.Take(segmentSize).Min(c => c.LowPrice);
            decimal low2 = window.Skip(segmentSize).Take(segmentSize).Min(c => c.LowPrice);
            decimal low3 = window.Skip(segmentSize * 2).Take(segmentSize).Min(c => c.LowPrice);

            return low2 >= low1 * minRiseRatio && low3 >= low2 * minRiseRatio;
        }

        /// <summary>[v3.2.4] 계단식 하락 감지: 3연속 고점 하락 (Lower Highs)</summary>
        private static bool IsMakingLowerHighs(List<IBinanceKline> candles, int segmentSize)
        {
            const int requiredSegments = 3;
            int requiredCandles = segmentSize * requiredSegments;
            if (candles.Count < requiredCandles) return false;

            var window = candles.TakeLast(requiredCandles).ToList();

            decimal high1 = window.Take(segmentSize).Max(c => c.HighPrice);
            decimal high2 = window.Skip(segmentSize).Take(segmentSize).Max(c => c.HighPrice);
            decimal high3 = window.Skip(segmentSize * 2).Take(segmentSize).Max(c => c.HighPrice);

            return high2 < high1 && high3 < high2;
        }

        private double CalculateFibScore(List<IBinanceKline> candles, decimal currentPrice)
        {
            if (candles == null || candles.Count < 30)
                return 0;

            var recent = candles.TakeLast(100).ToList();
            decimal high = recent.Max(c => c.HighPrice);
            decimal low = recent.Min(c => c.LowPrice);
            decimal range = high - low;
            if (range <= 0m)
                return 0;

            decimal fib382 = high - (range * 0.382m);
            decimal fib500 = high - (range * 0.500m);
            decimal fib618 = high - (range * 0.618m);
            decimal fib786 = high - (range * 0.786m);

            // [v3.2.11] 피보나치 확장: 0.382~0.786 단계별 가점
            if (currentPrice <= fib618 && currentPrice >= fib786)
            {
                OnLog?.Invoke("🎯 [피보나치] 황금 구간 (0.618~0.786) +20점");
                return 20.0;
            }
            if (currentPrice <= fib500 && currentPrice > fib618)
            {
                OnLog?.Invoke("🎯 [피보나치] 중간 구간 (0.500~0.618) +15점");
                return 15.0;
            }
            if (currentPrice <= fib382 && currentPrice > fib500)
            {
                OnLog?.Invoke("🎯 [피보나치] 상단 구간 (0.382~0.500) +10점");
                return 10.0;
            }

            return 0;
        }

        private MajorProfile ResolveProfile()
        {
            string? configuredProfile = _settingsAccessor?.Invoke()?.MajorTrendProfile;

            if (string.Equals(configuredProfile, "Aggressive", StringComparison.OrdinalIgnoreCase))
            {
                return MajorProfile.Aggressive;
            }

            return MajorProfile.Balanced;
        }

        private readonly record struct MajorProfile(
            string Name,
            double LongConfirmVolumeMin,
            int HigherLowBonus,
            int HigherLowThresholdDiscount,
            int HigherLowSegmentSize,
            decimal HigherLowMinRiseRatio)
        {
            public static MajorProfile Balanced { get; } = new(
                "Balanced",
                1.02,
                12,
                1,
                5,
                1.001m);

            public static MajorProfile Aggressive { get; } = new(
                "Aggressive",
                1.00,
                15,
                2,
                4,
                1.000m);
        }

        private static TimeZoneInfo GetSeoulTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
            }
        }
    }
}
