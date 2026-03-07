using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using System.Collections.Concurrent;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    public class PumpScanStrategy : ITradingStrategy
    {
        private static readonly TimeZoneInfo SeoulTimeZone = GetSeoulTimeZone();
        private readonly IBinanceRestClient _client;
        private readonly List<string> _majorSymbols;
        private readonly PumpScanSettings _settings;
        private DateTime _lastProfileLogTime = DateTime.MinValue;

        // 이벤트: 분석 결과 알림
        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string, decimal, string, double, double>? OnPumpDetected; // symbol, price, decision, rsi, atr
        public event Action<string>? OnLog; // [추가] 로그 이벤트

        private void PumpSignalLog(string stage, string detail)
        {
            OnLog?.Invoke($"📡 [SIGNAL][PUMP][{stage}] {detail}");
        }

        public PumpScanStrategy(IBinanceRestClient client, List<string> majorSymbols, PumpScanSettings settings)
        {
            _client = client;
            _majorSymbols = majorSymbols;
            _settings = settings ?? new PumpScanSettings();
        }

        // [Agent 1] ITradingStrategy 구현: 단일 심볼 분석 인터페이스
        public async Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            var profile = GetScanProfile();
            // 단일 분석 요청 시 블랙리스트 체크 없이(또는 빈 목록으로) 즉시 분석 수행
            await AnalyzeSymbolAsync(symbol, new ConcurrentDictionary<string, DateTime>(), token, profile);
        }

        public async Task ExecuteScanAsync(
            ConcurrentDictionary<string, TickerCacheItem> tickerCache,
            ConcurrentDictionary<string, DateTime> blacklistedSymbols,
            CancellationToken token)
        {
            var profile = GetScanProfile();

            // 1. 스캔 대상 필터링 (로컬 캐시 사용)
            var allTickers = tickerCache.Values
                .Where(t => !string.IsNullOrEmpty(t.Symbol) && t.Symbol.EndsWith("USDT") && !_majorSymbols.Contains(t.Symbol))
                .ToList();

            var afterHighPriceFilter = allTickers
                .Where(t => t.HighPrice > 0 && t.LastPrice >= t.HighPrice * 0.7m) // 고점 대비 30% 이상 하락 제외
                .ToList();

            var afterCandleFilter = afterHighPriceFilter
                .Where(t => t.LastPrice >= t.OpenPrice) // 음봉 제외
                .ToList();

            var topTickers = afterCandleFilter
                .OrderByDescending(t => t.QuoteVolume)
                .Take(profile.CandidateCount)
                .ToList();

            // 디버깅 로그
            PumpSignalLog("SCAN", $"total={allTickers.Count} highPriceFilter={afterHighPriceFilter.Count} bullishFilter={afterCandleFilter.Count} top={topTickers.Count} candidateCap={profile.CandidateCount}");

            if ((DateTime.Now - _lastProfileLogTime).TotalMinutes >= 5)
            {
                PumpSignalLog("PROFILE", $"name={profile.Name} minChangePct={profile.MinPriceChangePercentage:F2} minVolRatio={profile.MinVolumeRatio:F2} minVolRatio5m={profile.MinVolumeRatio5m:F2} minOrderBook={profile.MinOrderBookRatio:F2} minTakerBuy={profile.MinTakerBuyRatio:F2}");
                _lastProfileLogTime = DateTime.Now;
            }

            if (topTickers.Count > 0)
            {
                var symbols = string.Join(", ", topTickers.Take(5).Select(t => t.Symbol));
                PumpSignalLog("CANDIDATE", $"symbols={symbols}");
            }

            // 2. 병렬 분석 실행
            var tasks = topTickers.Select(async ticker =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(ticker.Symbol))
                        await AnalyzeSymbolAsync(ticker.Symbol, blacklistedSymbols, token, profile);
                }
                catch (Exception ex)
                {
                    PumpSignalLog("ERROR", $"sym={ticker.Symbol} source=parallelScan detail={ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task AnalyzeSymbolAsync(string symbol, ConcurrentDictionary<string, DateTime> blacklist, CancellationToken token, ScanProfile profile)
        {
            try
            {
                // 블랙리스트 확인
                if (blacklist.TryGetValue(symbol, out var expiry))
                {
                    if (DateTime.Now < expiry) return;
                    blacklist.TryRemove(symbol, out _);
                }

                // [1단계] 1분봉 조회 (빠른 필터링)
                var k1mRes = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneMinute, limit: 30, ct: token);
                if (!k1mRes.Success || k1mRes.Data == null) return;
                var data1m = k1mRes.Data.ToList();
                if (data1m.Count < 20) return;

                var last1m = data1m[data1m.Count - 1];
                double rangePercent = (double)((last1m.HighPrice - last1m.LowPrice) / last1m.OpenPrice * 100);
                double avgVol1m = data1m.Take(20).Average(c => (double)c.Volume);
                double volRatio = avgVol1m > 0 ? (double)last1m.Volume / avgVol1m : 0;

                // 디버깅 로그
                PumpSignalLog("CHECK_1M", $"sym={symbol} rangePct={rangePercent:F2} volRatio={volRatio:F2}");

                if (rangePercent < profile.MinPriceChangePercentage || volRatio < profile.MinVolumeRatio)
                {
                    PumpSignalLog("REJECT", $"sym={symbol} reason=threshold rangePct={rangePercent:F2}/{profile.MinPriceChangePercentage:F2} volRatio={volRatio:F2}/{profile.MinVolumeRatio:F2}");
                    return;
                }

                // [2단계] 정밀 데이터 조회
                var k5mTask = _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, limit: 30, ct: token);
                var k15mTask = _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, limit: 30, ct: token);
                var k1hTask = _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 100, ct: token);
                var depthTask = _client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, limit: 20, ct: token);

                await Task.WhenAll(k5mTask, k15mTask, k1hTask, depthTask);
                if (!k5mTask.Result.Success || !k15mTask.Result.Success || !k1hTask.Result.Success || !depthTask.Result.Success) return;
                if (k5mTask.Result.Data == null || k15mTask.Result.Data == null || k1hTask.Result.Data == null || depthTask.Result.Data == null) return;

                var data5m = k5mTask.Result.Data.ToList();
                var data15m = k15mTask.Result.Data.ToList();
                var data1h = k1hTask.Result.Data.ToList();
                if (data5m.Count < 20 || data15m.Count < 20 || data1h.Count < 99) return;

                decimal currentPrice = last1m.ClosePrice;

                // 지표 계산
                double ma99 = IndicatorCalculator.CalculateSMA(data1h, 99);
                bool isAboveMA99 = (double)currentPrice > ma99;
                bool isElliottUptrend = IndicatorCalculator.AnalyzeElliottWave(data1m);
                var bb15m = IndicatorCalculator.CalculateBB(data15m, 20, 2);
                double rsi15m = IndicatorCalculator.CalculateRSI(data15m, 14);
                double atr15m = IndicatorCalculator.CalculateATR(data15m, 14);

                // 호가창 분석
                var depth = depthTask.Result.Data;
                decimal totalBids = depth.Bids.Sum(b => b.Quantity);
                decimal totalAsks = depth.Asks.Sum(a => a.Quantity);
                bool isOrderBookBullish = totalAsks > 0 && (double)(totalBids / totalAsks) >= profile.MinOrderBookRatio;

                // 점수 산출
                int aiScore = CalculateScore(rsi15m, bb15m, currentPrice, isElliottUptrend);
                if ((double)currentPrice > bb15m.Upper) aiScore += 15;
                if (rsi15m >= 85) aiScore -= 40;

                // Decision Logic
                string decision = "WAIT";
                var last5m = data5m[data5m.Count - 1];
                double avgVol5m = data5m.Take(20).Average(c => (double)c.Volume);
                double volRatio5m = avgVol5m > 0 ? (double)last5m.Volume / avgVol5m : 0;
                bool is5mBullish = last5m.ClosePrice >= last5m.OpenPrice;

                // 체결강도
                var recentCandles = data1m.TakeLast(3).ToList();
                if (recentCandles.Count == 0) return;
                decimal sumBuyVol = recentCandles.Sum(k => k.TakerBuyBaseVolume);
                decimal sumSellVol = recentCandles.Sum(k => k.Volume) - sumBuyVol;
                bool isTakerStrong = sumSellVol > 0 && (double)(sumBuyVol / sumSellVol) >= profile.MinTakerBuyRatio;

                if (aiScore >= profile.PumpScoreThreshold && volRatio >= profile.PumpVolumeRatio && volRatio5m >= profile.MinVolumeRatio5m && is5mBullish && isOrderBookBullish && isTakerStrong && isAboveMA99) decision = "🚀 PUMP";
                else if (aiScore >= profile.MomentumScoreThreshold) decision = "MOMENTUM";

                // 결과 알림 (의미있는 결과만 UI에 전송)
                if (decision != "WAIT" || aiScore >= 60)
                {
                    try
                    {
                        OnSignalAnalyzed?.Invoke(new MultiTimeframeViewModel
                        {
                            Symbol = symbol,
                            LastPrice = currentPrice,
                            RSI_1H = rsi15m,
                            AIScore = aiScore,
                            Decision = decision,
                            StrategyName = "Pump Scan"
                        });
                    }
                    catch (Exception eventEx)
                    {
                        PumpSignalLog("ERROR", $"sym={symbol} source=signalEvent detail={eventEx.Message}");
                    }
                }

                if (decision == "🚀 PUMP")
                {
                    try
                    {
                        PumpSignalLog("EMIT", $"sym={symbol} side=LONG decision=PUMP score={aiScore} rsi={rsi15m:F1} price={currentPrice:F4}");
                        OnPumpDetected?.Invoke(symbol, currentPrice, decision, rsi15m, atr15m);
                    }
                    catch (Exception eventEx)
                    {
                        PumpSignalLog("ERROR", $"sym={symbol} source=pumpEvent detail={eventEx.Message}");
                    }
                }

                // [추가] 분석 결과 로그 출력 (동작 확인용)
                if (aiScore >= 60 || decision != "WAIT")
                {
                    PumpSignalLog("ANALYZE", $"sym={symbol} score={aiScore} rsi={rsi15m:F1} decision={decision}");
                }
            }
            catch (Exception ex)
            {
                PumpSignalLog("ERROR", $"sym={symbol} source=analyze detail={ex.Message}");
            }
        }

        private int CalculateScore(double rsi, BBResult bb, decimal currentPrice, bool isElliottUptrend)
        {
            int score = 50;
            if (isElliottUptrend) score += 20; else score -= 10;
            if (rsi >= 70) score += 15;
            else if (rsi <= 30) score -= 20;

            double price = (double)currentPrice;
            if (price >= bb.Upper) score += 15;
            else if (price <= bb.Lower) score -= 30;

            return Math.Clamp(score, 0, 100);
        }

        private ScanProfile GetScanProfile()
        {
            var nowSeoul = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SeoulTimeZone);
            bool isKstDaytime = nowSeoul.Hour >= 10 && nowSeoul.Hour < 19;

            if (!isKstDaytime)
            {
                return new ScanProfile(
                    Name: "DEFAULT",
                    MinPriceChangePercentage: (double)_settings.MinPriceChangePercentage,
                    MinVolumeRatio: _settings.MinVolumeRatio,
                    MinVolumeRatio5m: _settings.MinVolumeRatio5m,
                    MinOrderBookRatio: _settings.MinOrderBookRatio,
                    MinTakerBuyRatio: _settings.MinTakerBuyRatio,
                    PumpScoreThreshold: 85,
                    MomentumScoreThreshold: 70,
                    PumpVolumeRatio: 3.5,
                    CandidateCount: 10);
            }

            return new ScanProfile(
                Name: "KST_DAY_10_19",
                MinPriceChangePercentage: Math.Max(0.20, (double)_settings.MinPriceChangePercentage * 0.65),
                MinVolumeRatio: Math.Max(1.20, _settings.MinVolumeRatio * 0.70),
                MinVolumeRatio5m: Math.Max(1.20, _settings.MinVolumeRatio5m * 0.70),
                MinOrderBookRatio: Math.Max(1.10, _settings.MinOrderBookRatio * 0.70),
                MinTakerBuyRatio: Math.Max(1.05, _settings.MinTakerBuyRatio * 0.85),
                PumpScoreThreshold: 78,
                MomentumScoreThreshold: 65,
                PumpVolumeRatio: 2.6,
                CandidateCount: 20);
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

        private readonly record struct ScanProfile(
            string Name,
            double MinPriceChangePercentage,
            double MinVolumeRatio,
            double MinVolumeRatio5m,
            double MinOrderBookRatio,
            double MinTakerBuyRatio,
            int PumpScoreThreshold,
            int MomentumScoreThreshold,
            double PumpVolumeRatio,
            int CandidateCount);
    }
}
