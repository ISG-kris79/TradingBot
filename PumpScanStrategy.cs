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
        private const int PumpCandidateCount = 60;
        private const int PumpRecoveryCandidateCount = 100;
        private const decimal VolumeWeight = 0.25m;
        private const decimal VolatilityWeight = 0.35m;
        private const decimal MomentumWeight = 0.40m;

        private readonly IBinanceRestClient _client;
        private readonly PumpScanSettings _settings;
        private readonly TradingBot.Services.PumpSignalClassifier? _pumpML;

        // [v5.10.71 D+B] top60 후보의 rank/score 캐시 — AnalyzeSymbolAsync에서 TOP_SCORE_ENTRY 조건 평가용,
        // TradingEngine 슬롯 포화 큐 등록 시 fallback score로도 활용 (RAVE 같은 고거래량 코인 대응)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Rank, float Score, DateTime Time)> _topCandidateScores
            = new(StringComparer.OrdinalIgnoreCase);
        public System.Collections.Generic.IReadOnlyDictionary<string, (int Rank, float Score, DateTime Time)> TopCandidateScores => _topCandidateScores;
        private DateTime _lastProfileLogTime = DateTime.MinValue;

        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string, string, decimal>? OnTradeSignal;
        public event Action<string, decimal, string, double, double>? OnPumpDetected;
        public event Action<string>? OnLog;

        private void PumpSignalLog(string stage, string detail)
        {
            OnLog?.Invoke($"📡 [SIGNAL][PUMP][{stage}] {detail}");
        }

        public PumpScanStrategy(IBinanceRestClient client, List<string> watchSymbols, PumpScanSettings settings, TradingBot.Services.PumpSignalClassifier? pumpML = null)
        {
            _client = client;
            _settings = settings ?? new PumpScanSettings();
            _pumpML = pumpML;
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

            // [v5.10.71 D+B] top10 candidate rank/score 캐시 갱신 — AnalyzeSymbolAsync TOP_SCORE_ENTRY + 큐 fallback
            int topN = Math.Min(10, candidates.Count);
            for (int i = 0; i < topN; i++)
            {
                string sym = candidates[i].Ticker.Symbol;
                if (!string.IsNullOrWhiteSpace(sym))
                    _topCandidateScores[sym] = (i + 1, (float)candidates[i].Score, DateTime.Now);
            }
            // 10분 지난 캐시 항목 제거
            foreach (var kvp in _topCandidateScores.ToArray())
            {
                if ((DateTime.Now - kvp.Value.Time).TotalMinutes > 10)
                    _topCandidateScores.TryRemove(kvp.Key, out _);
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
                .Where(t => !string.IsNullOrWhiteSpace(t.Symbol)
                         && IsEligiblePumpSymbol(t.Symbol)
                         && t.QuoteVolume >= 5_000_000m) // [v5.10.3] 24h 거래량 $5M 미만 제외 — 초저유동성 심볼 진입 방지
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
                if (list.Count < 30) // [v3.2.9] 120→30봉 완화 (신규/소형 코인 진입 기회 확보)
                {
                    PumpSignalLog("REJECT", $"sym={symbol} reason=insufficientCandles count={list.Count}");
                    return false;
                }

                decimal currentPrice = list[list.Count - 1].ClosePrice;

                // [v4.0.1] 1분봉 API는 bullishSignals 계산 후 유력 후보만 호출 (API 절약)
                bool hasM1VolumeSurge = false;
                double m1VolumeRatio = 0;

                // [v4.9.3] 하드코딩 price<0.001 / ATR/price>=5% 필터 제거
                // ATR은 이미 PumpSignalClassifier가 피처로 학습 중이고 Volatility도 학습됨.
                // 초저가 밈코인도 WebSocket 가격만 정상이면 ML이 스스로 판단.

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

                int aiScore = CalculateScore(rsi, bb, currentPrice, isUptrend, macd, fib,
                    sma20, sma50, sma60, sma120, volumeMomentum, isMakingHigherLows,
                    allowLowVolumeTrendBypass, profile);

                double fibBonus = CalculateFibScore(symbol, list, currentPrice);
                if (fibBonus > 0) aiScore = Math.Clamp(aiScore + (int)fibBonus, 0, 100);

                // 가격 모멘텀 직접 감지 (로그 용도로만 사용, 차단 아님)
                var recent6 = list.TakeLast(6).ToList();
                decimal price6Ago = recent6.First().ClosePrice;
                double priceRecoveryPct = price6Ago > 0 ? (double)((currentPrice - price6Ago) / price6Ago * 100) : 0;
                bool isPriceRecovering = priceRecoveryPct >= 1.5;

                // [v4.9.3] '이미 pumped +5% / 1시간 저점 +8%' 하드코딩 차단 제거
                // 메모리 원칙: 진입 판단은 ML만. Price_Momentum_30m, Price_Change_Pct 는 이미 PumpSignalClassifier 피처

                var recent12 = list.TakeLast(12).ToList();
                decimal recentLow = recent12.Min(k => k.LowPrice);
                double bounceFromLowPct = recentLow > 0 ? (double)((currentPrice - recentLow) / recentLow * 100) : 0;

                // [v5.10.5] 과열 감지 — 최근 12봉 중 10개(83%+) 양봉 = 1시간 랠리 과열
                // BIO 케이스: 15분봉 4연속 양봉 → 5분봉 12개 중 대부분 양봉 → 꼭대기 진입 방지
                int bullCount12 = list.TakeLast(12).Count(c => c.ClosePrice > c.OpenPrice);
                bool isOverextended = bullCount12 >= 10; // 83%+ 양봉
                if (isOverextended)
                    PumpSignalLog("OVEREXTENDED", $"sym={symbol} bullRatio={bullCount12}/12 → FALLBACK/MEGA_PUMP 차단");

                // ═══════════════════════════════════════════════════════════
                // [v4.1.1] AI 전용 진입 판단 — 하드코딩 조건 전부 제거
                // PumpSignalClassifier ML 모델이 직접 "진입할까?" 판단
                // ExecuteAutoOrder에서 AI Gate + Survival + Direction 추가 검증
                // ═══════════════════════════════════════════════════════════
                string decision = "WAIT";
                int bullishSignals = 0; // 로그용

                // ML 모델 예측 (PumpSignalClassifier)
                bool mlSignal = false;
                float mlProb = 0f;
                if (_pumpML != null && _pumpML.IsModelLoaded)
                {
                    var mlFeature = TradingBot.Services.PumpSignalClassifier.ExtractFeature(list);
                    if (mlFeature != null)
                    {
                        var pred = _pumpML.Predict(mlFeature);
                        if (pred != null) { mlSignal = pred.ShouldEnter; mlProb = pred.Probability; }
                    }
                }

                // [v5.10.70 Phase B-2] 1분봉 fast-path — 5분봉 종가 대기 안 함
                // 5분봉 기반 MEGA_PUMP가 최대 5분 후행 → 1분봉으로 1분 후행으로 단축
                // MarketDataManager WebSocket 1분봉 캐시 활용 (실시간)
                // AXLUSDT 09:00 케이스: 1분봉 +5% 거래량 폭발 → 5분봉 종가 대기 없이 즉시 진입
                {
                    var m1Cache = TradingBot.Services.MarketDataManager.Instance?
                        .GetCachedKlines(symbol, KlineInterval.OneMinute, 5);
                    if (m1Cache != null && m1Cache.Count >= 4 && !isOverextended && rsi < 75)
                    {
                        var latestM1 = m1Cache[m1Cache.Count - 1];
                        var prev3M1 = m1Cache.Skip(Math.Max(0, m1Cache.Count - 4)).Take(3).ToList();
                        double avgVol3M1 = prev3M1.Average(k => (double)k.Volume);
                        double m1VolRatio = avgVol3M1 > 0 ? (double)latestM1.Volume / avgVol3M1 : 0;
                        decimal m1Range = latestM1.HighPrice - latestM1.LowPrice;
                        float m1RangePct = latestM1.OpenPrice > 0
                            ? (float)(m1Range / latestM1.OpenPrice * 100) : 0f;
                        bool m1Bullish = latestM1.ClosePrice > latestM1.OpenPrice;
                        bool m1TrendOk = isUptrend || currentPrice > (decimal)sma20;

                        // 조건: 1분봉 +3% AND 거래량 10배 + 양봉 + 단기추세 + 과열 아님 + RSI<75
                        if (m1RangePct >= 3.0f && m1VolRatio >= 10.0 && m1Bullish && m1TrendOk)
                        {
                            decision = "LONG";
                            PumpSignalLog("M1_FAST_PUMP",
                                $"sym={symbol} m1Vol={m1VolRatio:F1}x m1Range={m1RangePct:F1}% rsi={rsi:F0} → 1분봉 fast 즉시 진입 (5분봉 종가 대기 X)");
                        }
                    }
                }

                // [v5.1.3] 메가 펌프 즉시 진입 — 거래량 5배+ & range 3%+ & 양봉
                // PLAYUSDT 케이스: 13:30 vol 34배, range 6.34% 양봉 → ML이 bull0 판정 → 놓침
                // 거래량 폭발 + 큰 양봉 = 급등 시작 확실 → ML 판단 없이 즉시 LONG
                if (decision == "WAIT")
                {
                    var latestCandle = list[list.Count - 1];
                    decimal candleRange = latestCandle.HighPrice - latestCandle.LowPrice;
                    float rangePctNow = latestCandle.OpenPrice > 0
                        ? (float)(candleRange / latestCandle.OpenPrice * 100) : 0f;
                    bool isBullish = latestCandle.ClosePrice > latestCandle.OpenPrice;

                    // [v5.10.5] MEGA_PUMP: 하락추세 차단 + 과열 RSI 강화
                    // 기존: isUptrend 체크 없음 → 하락추세 중 단봉 거래량 폭발에도 즉시 진입
                    // 수정: isUptrend 필수 + 과열 시 RSI 80→70
                    float megaPumpRsiCap = isOverextended ? 70f : 80f;
                    if (isUptrend && volumeMomentum >= 5.0 && rangePctNow >= 3.0f && isBullish && rsi < megaPumpRsiCap)
                    {
                        decision = "LONG";
                        PumpSignalLog("MEGA_PUMP", $"sym={symbol} vol={volumeMomentum:F1}x range={rangePctNow:F1}% rsi={rsi:F0} overext={isOverextended} → 즉시 진입");
                    }
                    else if (!isUptrend && volumeMomentum >= 5.0 && rangePctNow >= 3.0f && isBullish)
                    {
                        PumpSignalLog("MEGA_PUMP_SKIP", $"sym={symbol} 하락추세 차단 vol={volumeMomentum:F1}x range={rangePctNow:F1}%");
                    }
                }

                // [v5.10.71 D] TOP_SCORE_ENTRY — top60 1~3위 고점수 코인 강한 펌프 직접 진입
                // RAVE 케이스: top60 #2 score 0.54, 21:05 +5.58% 펌프였으나 volumeMomentum 0.66으로 bull0 → WAIT
                // (고거래량 코인은 20봉 평균 대비 스파이크 비율 낮아 기존 필터 우회 필요)
                if (decision == "WAIT" && !isOverextended && rsi < 75
                    && _topCandidateScores.TryGetValue(symbol, out var topInfo)
                    && topInfo.Rank <= 3 && topInfo.Score >= 0.5f)
                {
                    var lc = list[list.Count - 1];
                    decimal lc_range = lc.HighPrice - lc.LowPrice;
                    float lc_rangePct = lc.OpenPrice > 0 ? (float)(lc_range / lc.OpenPrice * 100) : 0f;
                    bool lc_bullish = lc.ClosePrice > lc.OpenPrice;
                    float lc_chgPct = lc.OpenPrice > 0
                        ? (float)((lc.ClosePrice - lc.OpenPrice) / lc.OpenPrice * 100) : 0f;
                    // 5분봉 +3% AND 양봉 AND 단기추세 (5분봉 SMA20 위)
                    bool m5TrendOk = isUptrend || currentPrice > (decimal)sma20;
                    if (lc_chgPct >= 3.0f && lc_bullish && lc_rangePct >= 3.0f && m5TrendOk)
                    {
                        decision = "LONG";
                        PumpSignalLog("TOP_SCORE_ENTRY",
                            $"sym={symbol} rank={topInfo.Rank} score={topInfo.Score:F2} 5mChg={lc_chgPct:F1}% rsi={rsi:F0} → top60 고점수 즉시 진입");
                    }
                }

                // AI가 진입 승인 → 하락추세면 임계값 강화 (65% → 78%) + RSI < 50 추가 차단
                // [v5.10.7] dead cat bounce 필터: 하락추세 + RSI 50 미만 = 약세 구간 → 차단
                float aiEntryThreshold = isUptrend ? 0.65f : 0.78f;
                bool aiDowntrendRsiBlock = !isUptrend && rsi < 50;
                if (decision == "WAIT" && mlSignal && mlProb >= aiEntryThreshold && !aiDowntrendRsiBlock)
                {
                    decision = "LONG";
                    PumpSignalLog("AI_ENTRY", $"sym={symbol} prob={mlProb:P0} rsi={rsi:F0} vol={volumeMomentum:F2} uptrend={isUptrend} threshold={aiEntryThreshold:P0}");
                }
                else if (decision == "WAIT" && mlSignal && mlProb >= aiEntryThreshold && aiDowntrendRsiBlock)
                {
                    PumpSignalLog("AI_ENTRY_SKIP", $"sym={symbol} 하락추세+RSI{rsi:F0}<50 dead cat bounce 차단 prob={mlProb:P0}");
                }
                // ML 모델 미로드 시 기본 조건
                else if (decision == "WAIT" && !(_pumpML?.IsModelLoaded ?? false))
                {
                    // [v5.10.4] 과열 구간에선 FALLBACK 차단 + 반등 임계값 강화 (1.5% → 2.5%)
                    bool hasMomentum = priceRecoveryPct >= 2.5 || bounceFromLowPct >= 3.0;
                    bool hasStructure = isUptrend && isMakingHigherLows && currentPrice > (decimal)sma20 && !isOverextended;
                    if (hasMomentum && hasStructure && rsi >= 40 && rsi <= 70 && volumeMomentum >= 1.1)
                    {
                        decision = "LONG";
                        PumpSignalLog("FALLBACK_ENTRY", $"sym={symbol} rsi={rsi:F0} noML=true");
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
                        StrategyName = $"PUMP Scan(5m) [{profile.Name}]",
                        SignalSource = "PUMP",
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
                    reason = $"holdReason=bull{bullishSignals}(3개미만)";
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

        private static readonly HashSet<string> StablecoinSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "USDCUSDT", "BUSDUSDT", "FDUSDUSDT", "TUSDUSDT", "DAIUSDT", "USDPUSDT", "EURUSDT"
        };

        private bool IsEligiblePumpSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
                return false;

            // 스테이블코인 제외
            if (StablecoinSymbols.Contains(symbol))
                return false;

            // 메이저 코인은 MajorCoinStrategy에서 별도 스캔 → PUMP 후보에서 제외
            if (IsMajorSymbol(symbol))
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

            // [v3.0.9] RSI 과매수 감점 완화 — 급등 코인은 RSI 높은 게 정상
            if (rsi >= 45 && rsi <= 75) score += 10;
            else if (rsi > 75) score += 3; // 급등 모멘텀 유지 (기존 -10 → +3)
            else if (rsi < 35) score -= 6;

            if (macd.Hist > 0) score += 10;
            else score -= 10;

            if (currentPrice >= (decimal)fib.Level382) score += 6;
            if (currentPrice < (decimal)fib.Level618) score -= 10;

            double price = (double)currentPrice;
            if (price >= bb.Mid) score += 6;
            else score -= 6;

            // [v3.0.9] BB 상단 돌파 감점 완화 — 급등 시 BB 상단 돌파는 강세 신호
            if (price > bb.Upper && rsi > 85) score -= 5; // 극단 과열만 감점 (기존 72 → 85)
            else if (price > bb.Upper) score += 5; // BB 돌파 = 강세
            else if (price < bb.Lower && rsi < 30) score += 6;

            if (volumeMomentum >= 1.10) score += 10;
            else if (volumeMomentum >= 1.00) score += 5;
            else if (allowLowVolumeTrendBypass) score += 15;

            if (isMakingHigherLows && currentPrice > (decimal)sma20) score += profile.HigherLowBonus;

            return Math.Clamp(score, 0, 100);
        }

        private static int CalculateDynamicThreshold(double volumeMomentum, bool isMakingHigherLows, MajorProfile profile)
        {
            // [v3.2.3] 24시간 동일 기준 50점
            int threshold = 50;

            if (volumeMomentum >= 1.10) threshold -= 5;
            if (isMakingHigherLows) threshold -= profile.HigherLowThresholdDiscount;

            return Math.Max(35, threshold); // 최소 40점 (기존 55)
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

            if (currentPrice <= fib618 && currentPrice >= fib786)
            {
                PumpSignalLog("FIB", $"🎯 [피보나치] 황금 구간 +20 (px={currentPrice:F4})");
                return 20.0;
            }
            if (currentPrice <= fib500 && currentPrice > fib618)
            {
                PumpSignalLog("FIB", $"🎯 [피보나치] 중간 구간 +15 (px={currentPrice:F4})");
                return 15.0;
            }
            if (currentPrice <= fib382 && currentPrice > fib500)
            {
                PumpSignalLog("FIB", $"🎯 [피보나치] 상단 구간 +10 (px={currentPrice:F4})");
                return 10.0;
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
                1.005m); // [v5.10.7] 1.000m→1.005m: 0.5% 이상 상승만 HigherLow 인정 (동일가 = Higher Low 오판 방지)
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
