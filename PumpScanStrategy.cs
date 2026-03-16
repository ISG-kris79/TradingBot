using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using System.Collections.Concurrent;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    public class PumpScanStrategy : ITradingStrategy
    {
        private static readonly TimeZoneInfo SeoulTimeZone = GetSeoulTimeZone();
        private const int PumpCandidateCount = 20;
        private const int PumpRecoveryCandidateCount = 60;
        private const decimal VolumeWeight = 0.50m;
        private const decimal VolatilityWeight = 0.20m;
        private const decimal MomentumWeight = 0.30m;

        private readonly IBinanceRestClient _client;
        private readonly PumpScanSettings _settings;
        private DateTime _lastProfileLogTime = DateTime.MinValue;

        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string, string, decimal>? OnTradeSignal;
        public event Action<string, decimal, string, double, double>? OnPumpDetected;
        public event Action<string>? OnLog;

        private void PumpSignalLog(string stage, string detail)
        {
            OnLog?.Invoke($"📡 [SIGNAL][PUMP][{stage}] {detail}");
        }

        public PumpScanStrategy(IBinanceRestClient client, List<string> watchSymbols, PumpScanSettings settings)
        {
            _client = client;
            _settings = settings ?? new PumpScanSettings();
        }

        public async Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            if (!IsEligiblePumpSymbol(symbol))
                return;

            MajorProfile profile = ResolveProfile();
            _ = await AnalyzeSymbolAsync(symbol, new ConcurrentDictionary<string, DateTime>(), token, profile);
        }

        public async Task ExecuteScanAsync(
            ConcurrentDictionary<string, TickerCacheItem> tickerCache,
            ConcurrentDictionary<string, DateTime> blacklistedSymbols,
            CancellationToken token)
        {
            MajorProfile profile = ResolveProfile();
            int candidateCount = GetCandidateCount();
            int eligibleCount = tickerCache.Values.Count(t => !string.IsNullOrWhiteSpace(t.Symbol) && IsEligiblePumpSymbol(t.Symbol));

            var candidates = BuildCandidates(tickerCache, candidateCount);

            PumpSignalLog("SCAN", $"universe=USDT_FUTURES_ALL eligible={eligibleCount} tracked={candidates.Count} profile={profile.Name} theme=market-wide rank=mixed(volume50+volatility20+momentum30)Top{candidateCount}");

            if ((DateTime.Now - _lastProfileLogTime).TotalMinutes >= 5)
            {
                PumpSignalLog("PROFILE", $"name={profile.Name} candidateCap={candidateCount} customMinVol={_settings.MinVolumeRatio:F2} selection=market-wide-mixed(volume50+volatility20+momentum30)");
                _lastProfileLogTime = DateTime.Now;
            }

            if (candidates.Count > 0)
            {
                string rankedSymbols = string.Join(", ", candidates.Select((t, index) =>
                    $"{index + 1}:{t.Ticker.Symbol}(score={t.Score:F3},vol={t.Ticker.QuoteVolume:N0},var={t.Volatility:P1},mom={t.Momentum:P1})"));
                PumpSignalLog("CANDIDATE", $"top{candidates.Count}={rankedSymbols}");
            }

            var tasks = candidates.Select(async candidate =>
            {
                try
                {
                    var ticker = candidate.Ticker;
                    if (!string.IsNullOrWhiteSpace(ticker.Symbol))
                        _ = await AnalyzeSymbolAsync(ticker.Symbol, blacklistedSymbols, token, profile);
                }
                catch (Exception ex)
                {
                    PumpSignalLog("ERROR", $"sym={candidate.Ticker.Symbol} source=parallelScan detail={ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
        }

        public async Task<(string Symbol, string Decision, decimal Price)?> ExecuteRecoveryScanAsync(
            ConcurrentDictionary<string, TickerCacheItem> tickerCache,
            ConcurrentDictionary<string, DateTime> blacklistedSymbols,
            CancellationToken token,
            int candidateCount = PumpRecoveryCandidateCount)
        {
            MajorProfile profile = ResolveProfile();
            int cappedCount = Math.Clamp(candidateCount, PumpCandidateCount, PumpRecoveryCandidateCount);
            var candidates = BuildCandidates(tickerCache, cappedCount);

            PumpSignalLog("SCAN", $"mode=recovery-first-hit candidateCap={cappedCount} tracked={candidates.Count} profile={profile.Name}");

            foreach (var candidate in candidates)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var ticker = candidate.Ticker;
                    if (string.IsNullOrWhiteSpace(ticker.Symbol))
                        continue;

                    string? pickedDecision = null;
                    decimal pickedPrice = 0m;

                    bool matched = await AnalyzeSymbolAsync(
                        ticker.Symbol,
                        blacklistedSymbols,
                        token,
                        profile,
                        emitSignal: false,
                        signalCapture: (_, decision, price) =>
                        {
                            pickedDecision = decision;
                            pickedPrice = price;
                        });

                    if (matched && !string.IsNullOrWhiteSpace(pickedDecision) && pickedPrice > 0)
                    {
                        PumpSignalLog("SCAN", $"mode=recovery-first-hit selected={ticker.Symbol} side={pickedDecision} px={pickedPrice:F4}");
                        return (ticker.Symbol, pickedDecision!, pickedPrice);
                    }
                }
                catch (Exception ex)
                {
                    PumpSignalLog("ERROR", $"sym={candidate.Ticker.Symbol} source=recoveryScan detail={ex.Message}");
                }
            }

            PumpSignalLog("SCAN", "mode=recovery-first-hit selected=none");
            return null;
        }

        private List<CandidateScore> BuildCandidates(
            ConcurrentDictionary<string, TickerCacheItem> tickerCache,
            int candidateCount)
        {
            var eligibleTickers = tickerCache.Values
                .Where(t => !string.IsNullOrWhiteSpace(t.Symbol) && IsEligiblePumpSymbol(t.Symbol))
                .ToList();

            decimal maxQuoteVolume = eligibleTickers.Count > 0 ? eligibleTickers.Max(t => t.QuoteVolume) : 0m;
            decimal maxVolatility = eligibleTickers.Count > 0 ? eligibleTickers.Max(CalculateIntradayVolatility) : 0m;
            decimal maxMomentum = eligibleTickers.Count > 0 ? eligibleTickers.Max(CalculateMomentumScore) : 0m;

            return eligibleTickers
                .Select(t => new CandidateScore(
                    t,
                    CalculateMixedRankScore(t, maxQuoteVolume, maxVolatility, maxMomentum),
                    CalculateIntradayVolatility(t),
                    CalculateMomentumScore(t)))
                .OrderByDescending(t => t.Score)
                .ThenByDescending(t => t.Ticker.QuoteVolume)
                .Take(Math.Max(1, candidateCount))
                .ToList();
        }

        private async Task<bool> AnalyzeSymbolAsync(
            string symbol,
            ConcurrentDictionary<string, DateTime> blacklist,
            CancellationToken token,
            MajorProfile profile,
            bool emitSignal = true,
            Action<string, string, decimal>? signalCapture = null)
        {
            try
            {
                if (blacklist.TryGetValue(symbol, out var expiry))
                {
                    if (DateTime.Now < expiry) return false;
                    blacklist.TryRemove(symbol, out _);
                }

                var k5mRes = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, limit: 150, ct: token);
                if (!k5mRes.Success || k5mRes.Data == null)
                {
                    PumpSignalLog("REJECT", $"sym={symbol} reason=klineFetchFailed");
                    return false;
                }

                var list = k5mRes.Data.ToList();
                if (list.Count < 120)
                {
                    PumpSignalLog("REJECT", $"sym={symbol} reason=insufficientCandles count={list.Count}");
                    return false;
                }

                decimal currentPrice = list[list.Count - 1].ClosePrice;
                double rsi = IndicatorCalculator.CalculateRSI(list, 14);
                var bb = IndicatorCalculator.CalculateBB(list, 20, 2);
                bool isUptrend = IndicatorCalculator.AnalyzeElliottWave(list);
                var macd = IndicatorCalculator.CalculateMACD(list);
                var fib = IndicatorCalculator.CalculateFibonacci(list, 100);
                double sma20 = IndicatorCalculator.CalculateSMA(list, 20);
                double sma50 = IndicatorCalculator.CalculateSMA(list, 50);
                double sma60 = IndicatorCalculator.CalculateSMA(list, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(list, 120);
                double atr = IndicatorCalculator.CalculateATR(list, 14);

                string maState = sma20 > sma60 && sma60 > sma120 ? "BULL" : (sma20 < sma60 && sma60 < sma120 ? "BEAR" : "MIX");
                string fibPos = currentPrice >= (decimal)fib.Level382 ? "ABOVE382" : (currentPrice <= (decimal)fib.Level618 ? "BELOW618" : "MID");

                var recent20 = list.TakeLast(20).ToList();
                double avgVolume20 = recent20.Any() ? recent20.Average(k => (double)k.Volume) : 0;
                double currentVolume = recent20.Count > 0 ? (double)recent20[recent20.Count - 1].Volume : 0;
                double volumeRatio = avgVolume20 > 0 ? currentVolume / avgVolume20 : 1;

                var recent3 = list.TakeLast(3).ToList();
                var previous10 = list.Skip(Math.Max(0, list.Count - 13)).Take(10).ToList();
                double avgVolume3 = recent3.Any() ? recent3.Average(k => (double)k.Volume) : 0;
                double avgVolumePrev10 = previous10.Any() ? previous10.Average(k => (double)k.Volume) : 0;
                double volumeMomentum = avgVolumePrev10 > 0 ? avgVolume3 / avgVolumePrev10 : 1;

                bool isMakingHigherLows = IsMakingHigherLows(list, profile.HigherLowSegmentSize, profile.HigherLowMinRiseRatio);
                bool isTrendHealthyOnLowVolume = currentPrice > (decimal)sma20 && sma20 > sma50 && rsi > 50;
                bool allowLowVolumeTrendBypass = volumeMomentum < profile.LongConfirmVolumeMin &&
                                                 (isTrendHealthyOnLowVolume || (isMakingHigherLows && currentPrice > (decimal)sma20));

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
                double fibBonus = CalculateFibScore(symbol, list, currentPrice);
                if (fibBonus > 0)
                {
                    aiScore = Math.Clamp(aiScore + (int)fibBonus, 0, 100);
                }

                var nowSeoul = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SeoulTimeZone);
                bool isKstDaytime = nowSeoul.Hour >= 10 && nowSeoul.Hour < 19;
                int longThreshold = CalculateDynamicThreshold(isKstDaytime, volumeMomentum, isMakingHigherLows, profile);
                int shortThreshold = isKstDaytime ? 30 : 25;

                string decision = "WAIT";
                if (aiScore >= longThreshold)
                {
                    bool bullishStructure = isUptrend || (isMakingHigherLows && currentPrice > (decimal)sma20);
                    bool longConfirm = bullishStructure && macd.Hist >= -0.001 &&
                                       (volumeMomentum >= profile.LongConfirmVolumeMin || allowLowVolumeTrendBypass);
                    if (longConfirm) decision = "LONG";
                }
                else if (aiScore <= shortThreshold)
                {
                    bool isStrongBearish =
                        !isUptrend &&
                        macd.Hist < 0 &&
                        currentPrice < (decimal)sma20 &&
                        volumeRatio >= 1.10 &&
                        currentPrice < (decimal)fib.Level618;

                    if (isStrongBearish)
                        decision = "SHORT";
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
                        StrategyName = $"Top Volume Momentum Scalping(5m) [{profile.Name}]",
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
                    PumpSignalLog("ERROR", $"sym={symbol} source=signalEvent detail={eventEx.Message}");
                }

                if (!isUptrend && isMakingHigherLows && currentPrice > (decimal)sma20)
                {
                    PumpSignalLog("INFO", $"sym={symbol} higherLows=true trendAssist=on");
                }

                if (allowLowVolumeTrendBypass)
                {
                    PumpSignalLog("INFO", $"sym={symbol} lowVolumeBypass=on volumeMomentum={volumeMomentum:F2}");
                }

                string decisionKr = decision switch
                {
                    "LONG" => "LONG",
                    "SHORT" => "SHORT",
                    _ => "WAIT"
                };

                string aiFilterInfo = string.Empty;
                if (decision == "LONG" || decision == "SHORT")
                {
                    var filterHints = new List<string>();

                    if (volumeMomentum < 1.0)
                        filterHints.Add($"거래량{volumeMomentum:F2}x");

                    if (rsi < 40)
                        filterHints.Add($"RSI{rsi:F0}↓");

                    if (!isUptrend && !(sma20 > sma60))
                        filterHints.Add("정배열✗");

                    string hintText = filterHints.Count > 0 ? $" [{string.Join(", ", filterHints)}]" : string.Empty;
                    aiFilterInfo = filterHints.Count > 0
                        ? $"prefilter=need-ai-check{hintText}"
                        : "prefilter=need-ai-check";
                }

                string reason = string.Empty;
                if (decision == "WAIT")
                {
                    var reasons = new List<string>();
                    if (volumeMomentum < 1.00 && !allowLowVolumeTrendBypass) reasons.Add("거래량 부족");
                    if (!isUptrend && !isMakingHigherLows) reasons.Add("2파 횡보장 인식");
                    if (aiScore < longThreshold && aiScore > shortThreshold) reasons.Add("스코어 불충분");

                    if (reasons.Any())
                        reason = $"holdReason={string.Join("/", reasons)}";
                }

                PumpSignalLog(
                    "CANDIDATE",
                    $"sym={symbol} side={decisionKr} px={currentPrice:F4} {aiFilterInfo}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : " | " + reason)}");

                bool tradeSignalEmitted = false;
                if (decision != "WAIT")
                {
                    if (emitSignal)
                    {
                        try
                        {
                            PumpSignalLog("EMIT", $"sym={symbol} side={decisionKr} px={currentPrice:F4} src=MAJOR score={aiScore} atr={atr:F4}");
                            OnTradeSignal?.Invoke(symbol, decision, currentPrice);
                            OnPumpDetected?.Invoke(symbol, currentPrice, decision, rsi, atr);
                            tradeSignalEmitted = true;
                        }
                        catch (Exception eventEx)
                        {
                            PumpSignalLog("ERROR", $"sym={symbol} source=tradeEvent detail={eventEx.Message}");
                        }
                    }
                    else
                    {
                        signalCapture?.Invoke(symbol, decision, currentPrice);
                        tradeSignalEmitted = true;
                    }
                }

                return tradeSignalEmitted;
            }
            catch (Exception ex)
            {
                PumpSignalLog("ERROR", $"sym={symbol} source=analyze detail={ex.Message}");
                return false;
            }
        }

        private bool IsEligiblePumpSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private int GetCandidateCount()
        {
            return PumpCandidateCount;
        }

        private static decimal CalculateIntradayVolatility(TickerCacheItem ticker)
        {
            if (ticker.OpenPrice <= 0)
                return 0m;

            decimal highMove = ticker.HighPrice > 0 ? (ticker.HighPrice - ticker.OpenPrice) / ticker.OpenPrice : 0m;
            decimal closeMove = (ticker.LastPrice - ticker.OpenPrice) / ticker.OpenPrice;
            return Math.Max(Math.Abs(highMove), Math.Abs(closeMove));
        }

        private static decimal CalculateMomentumScore(TickerCacheItem ticker)
        {
            if (ticker.OpenPrice <= 0)
                return 0m;

            decimal change = (ticker.LastPrice - ticker.OpenPrice) / ticker.OpenPrice;
            return Math.Max(0m, change);
        }

        private static decimal CalculateMixedRankScore(TickerCacheItem ticker, decimal maxQuoteVolume, decimal maxVolatility, decimal maxMomentum)
        {
            decimal volumeScore = maxQuoteVolume > 0 ? ticker.QuoteVolume / maxQuoteVolume : 0m;
            decimal volatility = CalculateIntradayVolatility(ticker);
            decimal volatilityScore = maxVolatility > 0 ? volatility / maxVolatility : 0m;
            decimal momentum = CalculateMomentumScore(ticker);
            decimal momentumScore = maxMomentum > 0 ? momentum / maxMomentum : 0m;

            return (volumeScore * VolumeWeight) + (volatilityScore * VolatilityWeight) + (momentumScore * MomentumWeight);
        }

        private readonly record struct CandidateScore(TickerCacheItem Ticker, decimal Score, decimal Volatility, decimal Momentum);

        private static int CalculateScore(
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

        private static int CalculateDynamicThreshold(bool isKstDaytime, double volumeMomentum, bool isMakingHigherLows, MajorProfile profile)
        {
            int threshold = isKstDaytime ? 60 : 65;

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

        private double CalculateFibScore(string symbol, List<IBinanceKline> candles, decimal currentPrice)
        {
            if (!IsMajorSymbol(symbol))
                return 0;

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
                PumpSignalLog("FIB", $"🎯 [피보나치 타점] 황금 반등 구간 진입! score+20 (px={currentPrice:F4}, 0.618={fib618:F4}, 0.786={fib786:F4})");
                return 20.0;
            }

            return 0;
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

        private MajorProfile ResolveProfile()
        {
            string? configuredProfile = AppConfig.Current?.Trading?.GeneralSettings?.MajorTrendProfile;

            if (string.Equals(configuredProfile, "Aggressive", StringComparison.OrdinalIgnoreCase))
                return MajorProfile.Aggressive;

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
