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

                int aiScore = CalculateScore(
                    rsi,
                    bb,
                    currentPrice,
                    isUptrend,
                    macd,
                    fib,
                    sma20,
                    sma50,
                    sma60,
                    sma120,
                    volumeMomentum,
                    isMakingHigherLows,
                    allowLowVolumeTrendBypass,
                    profile);

                // [야수 모드] 피보나치 0.618~0.786 황금 반등 구간 가점
                double fibBonus = CalculateFibScore(list, currentPrice);
                if (fibBonus > 0)
                {
                    aiScore = Math.Clamp(aiScore + (int)fibBonus, 0, 100);
                }

                // [v3.2.3] 가격 모멘텀 가점 — SMA 역배열이어도 실제 반등 중이면 보정
                if (isPriceRecovering) aiScore = Math.Clamp(aiScore + 15, 0, 100);
                if (isStrongBounce) aiScore = Math.Clamp(aiScore + 10, 0, 100);

                // [v3.2.3] 24시간 동일 기준
                int longThreshold = CalculateDynamicThreshold(volumeMomentum, isMakingHigherLows, profile);
                int shortThreshold = 30;

                string decision = "WAIT";
                if (aiScore >= longThreshold)
                {
                    // [v3.2.3] bullishStructure 완화: SMA 상승추세 OR HigherLows OR 가격 모멘텀 반등
                    bool bullishStructure = isUptrend
                        || (isMakingHigherLows && currentPrice > (decimal)sma20)
                        || isPriceRecovering
                        || isStrongBounce;
                    bool longConfirm = bullishStructure &&
                        (macd.Hist >= -0.01 || isPriceRecovering) && // MACD 조건도 완화 (반등 시)
                        (volumeMomentum >= profile.LongConfirmVolumeMin || allowLowVolumeTrendBypass || isPriceRecovering);
                    if (longConfirm) decision = "LONG";
                }
                else if (aiScore <= shortThreshold)
                {
                    // [v3.2.1] SHORT 조건 완화: 5개 AND → 핵심 3개 이상이면 진입
                    int bearishCount = 0;
                    if (!isUptrend) bearishCount++;
                    if (macd.Hist < 0) bearishCount++;
                    if (currentPrice < (decimal)sma20) bearishCount++;
                    if (volumeRatio >= 1.10) bearishCount++;
                    if (currentPrice < (decimal)fib.Level618) bearishCount++;

                    // 필수: 가격 < SMA20 (최소한의 하락 확인)
                    bool priceBelow = currentPrice < (decimal)sma20;
                    if (priceBelow && bearishCount >= 3)
                    {
                        decision = "SHORT";
                    }
                }

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

                string reason = "";
                if (decision == "WAIT")
                {
                    var reasons = new List<string>();
                    if (volumeMomentum < 1.00 && !allowLowVolumeTrendBypass) reasons.Add("거래량 부족");
                    if (!isUptrend && !isMakingHigherLows) reasons.Add("2파 횡보장 인식");
                    if (aiScore < longThreshold && aiScore > shortThreshold) reasons.Add("스코어 불충분");

                    if (reasons.Any())
                        reason = $"holdReason={string.Join("/", reasons)}";
                }

                // [v2.4.2] 세련된 로그 형식
                if (decision == "WAIT")
                {
                    // WAIT는 조용히 건너뜀 (너무 많은 로그 방지)
                }
                else
                {
                    int targetThreshold = decision == "LONG" ? longThreshold : shortThreshold;
                    string holdReasonStr = string.IsNullOrWhiteSpace(reason) ? "" : $" | {reason}";
                    OnLog?.Invoke($"📊 [{symbol}] {decisionKr} 진입 후보 포착 | 가격 ${currentPrice:F2} | 점수 {aiScore}/{targetThreshold} | RSI {rsi:F1} | Vol {volumeMomentum:F2}x | {aiFilterInfo}{holdReasonStr}");
                    
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

            if (isUptrend) score += 12;
            else score -= 12;

            if (sma20 > sma60) score += 10;
            else score -= 10;

            if (sma60 > sma120) score += 8;
            else score -= 8;

            if (currentPrice > (decimal)sma20 && sma20 > sma50) score += 10;

            if (rsi >= 45 && rsi <= 68) score += 10;
            else if (rsi > 75) score -= 10;
            else if (rsi < 35) score -= 6;

            if (macd.Hist > 0) score += 10;
            else score -= 10;

            if (currentPrice >= (decimal)fib.Level382) score += 6;
            if (currentPrice < (decimal)fib.Level618) score -= 10;

            double price = (double)currentPrice;
            if (price >= bb.Mid) score += 6;
            else score -= 6;

            if (price > bb.Upper && rsi > 72) score -= 8;
            else if (price < bb.Lower && rsi < 30) score += 6;

            if (volumeMomentum >= 1.10) score += 10;
            else if (volumeMomentum >= 1.00) score += 5;
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

            decimal fib618 = high - (range * 0.618m);
            decimal fib786 = high - (range * 0.786m);

            if (currentPrice <= fib618 && currentPrice >= fib786)
            {
                OnLog?.Invoke("🎯 [피보나치 타점] 황금 반등 구간 진입! 가점 +20점");
                return 20.0;
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
