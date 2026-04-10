using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Shared.Models;
using TradingBot.Services;
using CandleData = TradingBot.Models.CandleData;

namespace TradingBot.Services
{
    public class MarketHistoryService
    {
        private static readonly TimeSpan CandleInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeZoneInfo SeoulTimeZone = GetSeoulTimeZone();
        private readonly MarketDataManager _marketDataManager;
        private readonly DatabaseService _databaseService;
        private readonly ConcurrentDictionary<string, DateTime> _latestSavedOpenTimes = new();
        private readonly BinanceRestClient _restClient = new();
        // [v4.5.6] 알트 캔들 수집 전용 상태
        private DateTime _lastAltCollectionTime = DateTime.MinValue;
        private bool _firstAltCollectionDone;
        private static readonly HashSet<string> MajorSymbolsSet = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"
        };

        /// <summary>
        /// [v4.5.7] 최초 알트 캔들 수집 완료 시 발화 (ML 학습 즉시 트리거용)
        /// </summary>
        public event Action? OnFirstAltCollectionComplete;

        public MarketHistoryService(MarketDataManager marketDataManager, string connectionString)
        {
            _marketDataManager = marketDataManager;
            _databaseService = new DatabaseService((msg) => MainWindow.Instance?.AddLog(msg));

            // [실시간 저장] 새 봉 추가 이벤트 구독
            _marketDataManager.OnNewKlineAdded += OnNewCandleReceived;
        }

        // [실시간 저장] 새 봉이 완성되면 즉시 DB에 저장
        private void OnNewCandleReceived(string symbol, IBinanceKline newKline)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SaveSingleCandleAsync(symbol, newKline);
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.AddLog($"❌ [실시간 저장] {symbol} 캔들 저장 실패: {ex.Message}");
                }
            });
        }

        // 단일 캔들 즉시 저장 (지표 계산 포함)
        private async Task SaveSingleCandleAsync(string symbol, IBinanceKline newKline)
        {
            try
            {
                // KlineCache에서 최신 데이터 가져오기
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var klines)) return;

                List<IBinanceKline> klineSnapshot;
                lock (klines)
                {
                    klineSnapshot = klines.ToList();
                }

                // 최소 데이터가 너무 적으면 저장 생략
                if (klineSnapshot.Count < 2) return;

                // 새 봉이 생겼을 때 저장 대상은 '막 닫힌 이전 봉'
                var closedKline = klineSnapshot[^2];
                var closedOpenTimeUtc = NormalizeToUtc(closedKline.OpenTime);
                var closedOpenTimeSeoul = ConvertUtcToSeoul(closedOpenTimeUtc);
                if (!IsCandleClosed(closedOpenTimeUtc)) return;

                var latestSavedOpenTime = await GetLatestSavedOpenTimeAsync(symbol);
                if (latestSavedOpenTime.HasValue && closedOpenTimeSeoul <= latestSavedOpenTime.Value) return;

                int fibLookback = Math.Min(60, klineSnapshot.Count);

                // 보조지표 계산 (전체 캐시 기반)
                double rsi = IndicatorCalculator.CalculateRSI(klineSnapshot, 14);
                var bb = IndicatorCalculator.CalculateBB(klineSnapshot, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(klineSnapshot);
                double atr = IndicatorCalculator.CalculateATR(klineSnapshot, 14);
                var fib = IndicatorCalculator.CalculateFibonacci(klineSnapshot, fibLookback);
                bool elliottUptrend = klineSnapshot.Count >= 20 && IndicatorCalculator.AnalyzeElliottWave(klineSnapshot);

                var candleData = new CandleData
                {
                    Symbol = symbol,
                    Interval = "5m",
                    OpenTime = closedOpenTimeSeoul,
                    Open = closedKline.OpenPrice,
                    High = closedKline.HighPrice,
                    Low = closedKline.LowPrice,
                    Close = closedKline.ClosePrice,
                    Volume = (float)closedKline.Volume,
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    ElliottWaveState = elliottUptrend ? 3.0f : 1.0f,
                    Trend_Strength = elliottUptrend ? 1.0f : -1.0f,
                };

                // 4개 테이블에 모두 저장 (병렬 실행)
                var tasks = new List<Task>
                {
                    _databaseService.BulkInsertMarketDataAsync(new[] { candleData }),  // MarketCandles
                    _databaseService.SaveCandleDataBulkAsync(new[] { candleData }),    // CandleData
                    _databaseService.SaveCandleHistoryBulkAsync(new[] { candleData }), // CandleHistory
                    _databaseService.SaveMarketDataBulkAsync(new[] { candleData })     // MarketData
                };
                await Task.WhenAll(tasks);

                _latestSavedOpenTimes.AddOrUpdate(symbol, candleData.OpenTime,
                    (_, current) => candleData.OpenTime > current ? candleData.OpenTime : current);

                MainWindow.Instance?.AddLog($"💾 [실시간 저장] {symbol} OpenTime(KST) {candleData.OpenTime:yyyy-MM-dd HH:mm:ss} 저장 완료");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [실시간 저장] {symbol} 저장 중 예외: {ex.Message}");
            }
        }

        public async Task BackfillToNowBeforeStartAsync(CancellationToken token)
        {
            try
            {
                MainWindow.Instance?.AddLog("⏳ [MarketHistory] 시작 전 백필 동기화 시작 (현시각까지)");

                await WaitForKlineCacheWarmupAsync(token);
                await SaveAllSymbolsAsync(saveAllClosedCandles: true, token);

                MainWindow.Instance?.AddLog("✅ [MarketHistory] 시작 전 백필 동기화 완료");
            }
            catch (OperationCanceledException)
            {
                MainWindow.Instance?.AddLog("⚠️ [MarketHistory] 시작 전 백필 동기화 취소");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [MarketHistory] 시작 전 백필 동기화 실패: {ex.Message}");
            }
        }

        public async Task StartRecordingAsync(CancellationToken token)
        {
            MainWindow.Instance?.AddLog("📊 [MarketHistory] 캔들 데이터 자동 저장 시작 (5분 주기)");

            try
            {
                // 첫 저장은 즉시 실행
                await SaveAllSymbolsAsync(saveAllClosedCandles: false, token);
                _ = Task.Run(() => CollectTopAltCandlesAsync(token)); // 알트 수집 병렬 시작

                while (!token.IsCancellationRequested)
                {
                    // 5분마다 KlineCache의 데이터를 DB에 저장
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    await SaveAllSymbolsAsync(saveAllClosedCandles: false, token);

                    // [v4.5.6] 상위 50개 알트 5분봉 수집 (ML 학습용)
                    _ = Task.Run(() => CollectTopAltCandlesAsync(token));
                }
            }
            catch (OperationCanceledException)
            {
                MainWindow.Instance?.AddLog("📊 [MarketHistory] 캔들 데이터 저장 중지");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [MarketHistory] 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// [v4.5.6] 상위 50개 알트 5분봉 수집 (PUMP/SurvivalPump ML 학습용)
        /// - TickerCache에서 거래량 상위 알트 추출 (API 호출 0)
        /// - 각 심볼당 200개 5분봉 REST 폴링 (5분에 1회, 50회 REST)
        /// - CPU 무부하: 5분에 1회만 실행
        /// </summary>
        private async Task CollectTopAltCandlesAsync(CancellationToken token)
        {
            try
            {
                // 5분 주기 throttle
                if ((DateTime.UtcNow - _lastAltCollectionTime).TotalMinutes < 4.5)
                    return;
                _lastAltCollectionTime = DateTime.UtcNow;

                // 거래량(QuoteVolume) 상위 50개 알트 선정
                var topAlts = _marketDataManager.TickerCache.Values
                    .Where(t => !string.IsNullOrEmpty(t.Symbol)
                                && t.Symbol!.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                                && !MajorSymbolsSet.Contains(t.Symbol!)
                                && t.QuoteVolume > 0)
                    .OrderByDescending(t => t.QuoteVolume)
                    .Take(50)
                    .Select(t => t.Symbol!)
                    .ToList();

                if (topAlts.Count == 0)
                    return;

                MainWindow.Instance?.AddLog($"📥 [AltCollect] 상위 {topAlts.Count}개 알트 5분봉 수집 시작");

                int totalSaved = 0;
                int successSymbols = 0;
                int errorSymbols = 0;

                foreach (var symbol in topAlts)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        // 이미 최신 데이터가 있으면 스킵
                        var latestSaved = await GetLatestSavedOpenTimeAsync(symbol);
                        if (latestSaved.HasValue && (DateTime.UtcNow - latestSaved.Value.ToUniversalTime()).TotalMinutes < 5)
                            continue;

                        var klineResult = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                            symbol, KlineInterval.FiveMinutes, limit: 200, ct: token);

                        if (!klineResult.Success || klineResult.Data == null)
                        {
                            errorSymbols++;
                            continue;
                        }

                        var klines = klineResult.Data.Cast<IBinanceKline>().ToList();
                        if (klines.Count < 30) continue;

                        var closedKlines = klines
                            .Where(k => IsCandleClosed(NormalizeToUtc(k.OpenTime)))
                            .ToList();

                        if (latestSaved.HasValue)
                        {
                            closedKlines = closedKlines
                                .Where(k => ConvertUtcToSeoul(NormalizeToUtc(k.OpenTime)) > latestSaved.Value)
                                .ToList();
                        }

                        if (closedKlines.Count == 0) continue;

                        // 지표 계산 (전체 캐시 기반)
                        int fibLookback = Math.Min(60, klines.Count);
                        double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
                        var bb = IndicatorCalculator.CalculateBB(klines, 20, 2);
                        var macd = IndicatorCalculator.CalculateMACD(klines);
                        double atr = IndicatorCalculator.CalculateATR(klines, 14);
                        var fib = IndicatorCalculator.CalculateFibonacci(klines, fibLookback);
                        bool elliottUptrend = klines.Count >= 20 && IndicatorCalculator.AnalyzeElliottWave(klines);

                        var candleList = new List<CandleData>();
                        foreach (var k in closedKlines)
                        {
                            candleList.Add(new CandleData
                            {
                                Symbol = symbol,
                                Interval = "5m",
                                OpenTime = ConvertUtcToSeoul(NormalizeToUtc(k.OpenTime)),
                                Open = k.OpenPrice,
                                High = k.HighPrice,
                                Low = k.LowPrice,
                                Close = k.ClosePrice,
                                Volume = (float)k.Volume,
                                RSI = (float)rsi,
                                BollingerUpper = (float)bb.Upper,
                                BollingerLower = (float)bb.Lower,
                                MACD = (float)macd.Macd,
                                MACD_Signal = (float)macd.Signal,
                                MACD_Hist = (float)macd.Hist,
                                ATR = (float)atr,
                                Fib_236 = (float)fib.Level236,
                                Fib_382 = (float)fib.Level382,
                                Fib_500 = (float)fib.Level500,
                                Fib_618 = (float)fib.Level618,
                                ElliottWaveState = elliottUptrend ? 3.0f : 1.0f,
                                Trend_Strength = elliottUptrend ? 1.0f : -1.0f,
                            });
                        }

                        await Task.WhenAll(
                            _databaseService.BulkInsertMarketDataAsync(candleList),
                            _databaseService.SaveCandleDataBulkAsync(candleList),
                            _databaseService.SaveCandleHistoryBulkAsync(candleList),
                            _databaseService.SaveMarketDataBulkAsync(candleList)
                        );

                        totalSaved += candleList.Count;
                        successSymbols++;

                        var maxTime = candleList.Max(c => c.OpenTime);
                        _latestSavedOpenTimes.AddOrUpdate(symbol, maxTime, (_, c) => maxTime > c ? maxTime : c);

                        // API weight 제한 대응: 심볼 간 20ms 대기 (전체 50 * 20 = 1초)
                        await Task.Delay(20, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errorSymbols++;
                        MainWindow.Instance?.AddLog($"⚠️ [AltCollect] {symbol} 실패: {ex.Message}");
                    }
                }

                MainWindow.Instance?.AddLog($"✅ [AltCollect] {successSymbols}/{topAlts.Count} 심볼 저장 | {totalSaved}개 캔들 | 에러 {errorSymbols}");

                // [v4.5.7] 최초 수집 완료 시 ML 학습 즉시 트리거
                if (!_firstAltCollectionDone && totalSaved > 0)
                {
                    _firstAltCollectionDone = true;
                    MainWindow.Instance?.AddLog($"🎯 [AltCollect] 최초 수집 완료 → ML 재학습 트리거");
                    OnFirstAltCollectionComplete?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [AltCollect] 오류: {ex.Message}");
            }
        }

        private async Task SaveAllSymbolsAsync(bool saveAllClosedCandles, CancellationToken token)
        {
            try
            {
                int totalSaved = 0;
                DateTime? latestSavedSeoul = null;

                // 모든 KlineCache 심볼에 대해 저장
                foreach (var kvp in _marketDataManager.KlineCache)
                {
                    token.ThrowIfCancellationRequested();

                    string symbol = kvp.Key;
                    var klines = kvp.Value;

                    try
                    {
                        var candleDataList = new List<CandleData>();
                        List<IBinanceKline> klineSnapshot;

                        lock (klines)
                        {
                            klineSnapshot = klines.ToList();
                        }

                        // 최소 데이터가 너무 적으면 저장 생략
                        if (klineSnapshot.Count < 2) continue;

                        var closedKlines = klineSnapshot
                            .Where(k => IsCandleClosed(NormalizeToUtc(k.OpenTime)))
                            .ToList();

                        if (!closedKlines.Any())
                        {
                            MainWindow.Instance?.AddLog($"ℹ️ [MarketHistory] {symbol} 저장 대상(종료봉) 없음");
                            continue;
                        }

                        if (!saveAllClosedCandles)
                        {
                            closedKlines = closedKlines.TakeLast(20).ToList();
                        }

                        var latestSavedOpenTime = await GetLatestSavedOpenTimeAsync(symbol);
                        if (latestSavedOpenTime.HasValue)
                        {
                            closedKlines = closedKlines
                                .Where(k => ConvertUtcToSeoul(NormalizeToUtc(k.OpenTime)) > latestSavedOpenTime.Value)
                                .ToList();
                        }

                        if (!closedKlines.Any())
                        {
                            MainWindow.Instance?.AddLog($"ℹ️ [MarketHistory] {symbol} 신규 저장 대상 없음(이미 최신)");
                            continue;
                        }

                        int fibLookback = Math.Min(60, klineSnapshot.Count);

                        // 보조지표 계산 (전체 캐시 기반)
                        double rsi = IndicatorCalculator.CalculateRSI(klineSnapshot, 14);
                        var bb = IndicatorCalculator.CalculateBB(klineSnapshot, 20, 2);
                        var macd = IndicatorCalculator.CalculateMACD(klineSnapshot);
                        double atr = IndicatorCalculator.CalculateATR(klineSnapshot, 14);
                        var fib = IndicatorCalculator.CalculateFibonacci(klineSnapshot, fibLookback);
                        bool elliottUptrend = klineSnapshot.Count >= 20 && IndicatorCalculator.AnalyzeElliottWave(klineSnapshot);

                        foreach (var k in closedKlines)
                        {
                            candleDataList.Add(new CandleData
                            {
                                Symbol = symbol,
                                Interval = "5m",
                                OpenTime = ConvertUtcToSeoul(NormalizeToUtc(k.OpenTime)),
                                Open = k.OpenPrice,
                                High = k.HighPrice,
                                Low = k.LowPrice,
                                Close = k.ClosePrice,
                                Volume = (float)k.Volume,
                                RSI = (float)rsi,
                                BollingerUpper = (float)bb.Upper,
                                BollingerLower = (float)bb.Lower,
                                MACD = (float)macd.Macd,
                                MACD_Signal = (float)macd.Signal,
                                MACD_Hist = (float)macd.Hist,
                                ATR = (float)atr,
                                Fib_236 = (float)fib.Level236,
                                Fib_382 = (float)fib.Level382,
                                Fib_500 = (float)fib.Level500,
                                Fib_618 = (float)fib.Level618,
                                ElliottWaveState = elliottUptrend ? 3.0f : 1.0f,
                                Trend_Strength = elliottUptrend ? 1.0f : -1.0f,
                            });
                        }

                        if (candleDataList.Any())
                        {
                            // 4개 테이블에 모두 저장 (병렬 실행)
                            var tasks = new List<Task>
                            {
                                _databaseService.BulkInsertMarketDataAsync(candleDataList),  // MarketCandles
                                _databaseService.SaveCandleDataBulkAsync(candleDataList),    // CandleData
                                _databaseService.SaveCandleHistoryBulkAsync(candleDataList), // CandleHistory
                                _databaseService.SaveMarketDataBulkAsync(candleDataList)     // MarketData
                            };
                            await Task.WhenAll(tasks);
                            totalSaved += candleDataList.Count;

                            var maxOpenTime = candleDataList.Max(x => x.OpenTime);
                            _latestSavedOpenTimes.AddOrUpdate(symbol, maxOpenTime,
                                (_, current) => maxOpenTime > current ? maxOpenTime : current);

                            if (!latestSavedSeoul.HasValue || maxOpenTime > latestSavedSeoul.Value)
                            {
                                latestSavedSeoul = maxOpenTime;
                            }
                        }
                    }
                    catch (Exception symbolEx)
                    {
                        MainWindow.Instance?.AddLog($"❌ [MarketHistory] {symbol} 저장 실패: {symbolEx.Message}");
                    }
                }

                if (totalSaved > 0)
                {
                    MainWindow.Instance?.AddLog($"✅ [MarketHistory] {totalSaved}건 × 4개 테이블 저장 완료 (MarketCandles, CandleData, CandleHistory, MarketData)");
                    if (latestSavedSeoul.HasValue)
                    {
                        var closedAtLocal = latestSavedSeoul.Value.AddMinutes(5);
                        MainWindow.Instance?.AddLog($"🕒 [MarketHistory] 최신 저장 OpenTime(KST): {latestSavedSeoul:yyyy-MM-dd HH:mm:ss} (종가시각 KST {closedAtLocal:HH:mm:ss})");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [MarketHistory] 저장 실패: {ex.Message}");
            }
        }

        private async Task<DateTime?> GetLatestSavedOpenTimeAsync(string symbol)
        {
            if (_latestSavedOpenTimes.TryGetValue(symbol, out var cached))
                return cached;

            var latest = await _databaseService.GetLatestSyncedOpenTimeAcrossTablesAsync(symbol, "5m");
            if (latest.HasValue)
            {
                _latestSavedOpenTimes[symbol] = NormalizeDbOpenTimeToSeoul(latest.Value);
            }

            return _latestSavedOpenTimes.TryGetValue(symbol, out var normalized) ? normalized : latest;
        }

        private static bool IsCandleClosed(DateTime openTime)
        {
            return openTime.Add(CandleInterval) <= DateTime.UtcNow;
        }

        private static DateTime NormalizeToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static DateTime ConvertUtcToSeoul(DateTime utcTime)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(NormalizeToUtc(utcTime), SeoulTimeZone);
        }

        private static DateTime NormalizeDbOpenTimeToSeoul(DateTime dbTime)
        {
            // 기존 데이터가 UTC로 들어갔을 수 있으므로
            // 1) KST로 이미 저장된 값, 2) UTC로 저장된 값을 KST 변환한 값 중
            // 현재 시각(서울) 기준 유효한 쪽을 선택
            var nowSeoul = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SeoulTimeZone);

            var asSeoul = DateTime.SpecifyKind(dbTime, DateTimeKind.Unspecified);
            var asUtcToSeoul = ConvertUtcToSeoul(DateTime.SpecifyKind(dbTime, DateTimeKind.Utc));

            bool seoulValid = asSeoul <= nowSeoul.AddMinutes(5);
            bool utcValid = asUtcToSeoul <= nowSeoul.AddMinutes(5);

            if (seoulValid && utcValid)
                return asSeoul > asUtcToSeoul ? asSeoul : asUtcToSeoul;

            if (utcValid) return asUtcToSeoul;
            if (seoulValid) return asSeoul;

            return asSeoul;
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

        private async Task WaitForKlineCacheWarmupAsync(CancellationToken token)
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < timeoutAt)
            {
                token.ThrowIfCancellationRequested();

                bool hasReadySymbol = _marketDataManager.KlineCache.Any(kvp =>
                {
                    lock (kvp.Value)
                    {
                        return kvp.Value.Count >= 2;
                    }
                });

                if (hasReadySymbol)
                    return;

                await Task.Delay(500, token);
            }

            MainWindow.Instance?.AddLog("⚠️ [MarketHistory] KlineCache 워밍업 타임아웃 (가능한 데이터부터 백필 진행)");
        }
    }
}
