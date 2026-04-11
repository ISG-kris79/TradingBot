using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.7.3] Binance 과거 캔들 대량 다운로드 + DB 저장
    /// - 심볼 병렬 다운로드 (MaxParallel=6)
    /// - SqlBulkCopy 기반 DB 저장 (1000배 빠름)
    /// - 지표 계산 루프 제거 (DB에 저장도 안 되는 낭비 작업이었음)
    /// </summary>
    public class HistoricalDataDownloader
    {
        private readonly BinanceRestClient _restClient = new();
        private readonly DbManager _dbManager;
        private readonly DatabaseService _databaseService;

        // Binance 분당 2400 weight, kline weight=1~2. 6병렬 * 100ms = 안전
        private const int MaxParallelSymbols = 6;
        private const int PerRequestDelayMs = 80;

        public event Action<string>? OnLog;
        public event Action<int, int, string>? OnProgress; // current, total, message
        public event Action<DownloadProgress>? OnDetailedProgress; // [v4.7.3] ETA 계산용 구조화 이벤트
        // [v4.7.4] 심볼별 "다운로드 완료" 이벤트 (phase = "major" | "alt_5m" | "alt_1m")
        public event Action<string, string>? OnSymbolReady;
        public event Action? OnMajorsCompleted; // [v4.7.4] 메이저 4개 모두 완료 시 1회 발생

        public class DownloadProgress
        {
            public int Current { get; set; }
            public int Total { get; set; }
            public string CurrentSymbol { get; set; } = "";
            public long TotalCandlesSaved { get; set; }
            public TimeSpan Elapsed { get; set; }
            public TimeSpan? EstimatedRemaining { get; set; }
            public double PercentComplete => Total > 0 ? (double)Current / Total * 100d : 0d;
            public string Phase { get; set; } = "";             // "major" | "alt_5m" | "alt_1m"
            public int MajorReady { get; set; }                  // 완료된 메이저 수 (0~4)
            public int AltReady { get; set; }                    // 완료된 알트 수
            public int AltTotal { get; set; }                    // 총 알트 수
        }

        public HistoricalDataDownloader(DbManager dbManager, DatabaseService databaseService)
        {
            _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        }

        /// <summary>
        /// 거래량 상위 N개 알트 심볼 추출 (메이저 제외)
        /// </summary>
        public async Task<List<string>> GetTopAltSymbolsAsync(int top, CancellationToken token)
        {
            try
            {
                var ticker24h = await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync(token);
                if (!ticker24h.Success || ticker24h.Data == null) return new();

                var majorSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"
                };

                return ticker24h.Data
                    .Where(t => t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                                && !majorSet.Contains(t.Symbol)
                                && t.QuoteVolume > 0)
                    .OrderByDescending(t => t.QuoteVolume)
                    .Take(top)
                    .Select(t => t.Symbol)
                    .ToList();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [HistoricalDownloader] 알트 목록 조회 실패: {ex.Message}");
                return new();
            }
        }

        /// <summary>
        /// 단일 심볼 + 단일 인터벌 — startTime~endTime 사이 모든 캔들 페이지네이션 다운로드
        /// </summary>
        public async Task<List<IBinanceKline>> DownloadSymbolAsync(
            string symbol,
            KlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            CancellationToken token)
        {
            var allKlines = new List<IBinanceKline>();
            var currentStart = startTime;

            // Binance 한 번 요청 최대 1500봉
            const int batchSize = 1500;

            try
            {
                while (currentStart < endTime && !token.IsCancellationRequested)
                {
                    var result = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                        symbol, interval, startTime: currentStart, endTime: endTime, limit: batchSize, ct: token);

                    if (!result.Success || result.Data == null || !result.Data.Any())
                        break;

                    var batch = result.Data.Cast<IBinanceKline>().ToList();
                    allKlines.AddRange(batch);

                    // 다음 페이지: 마지막 봉의 다음 봉부터
                    var lastTime = batch.Last().OpenTime;
                    var intervalMinutes = GetIntervalMinutes(interval);
                    currentStart = lastTime.AddMinutes(intervalMinutes);

                    // rate limit: 6병렬 × 80ms = 분당 4500 req (weight=1 → 4500 weight, 한도 6000 OK)
                    await Task.Delay(PerRequestDelayMs, token);
                }

                // 중복 제거 (혹시 모를 경계 봉 중복)
                return allKlines
                    .GroupBy(k => k.OpenTime)
                    .Select(g => g.First())
                    .OrderBy(k => k.OpenTime)
                    .ToList();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [HistoricalDownloader] {symbol} {interval} 다운로드 오류: {ex.Message}");
                return allKlines;
            }
        }

        /// <summary>
        /// 다운로드 + DB 저장 (OHLCV만, 지표는 DB에 저장되지 않으므로 제외)
        /// </summary>
        public async Task<int> DownloadAndSaveAsync(
            string symbol,
            KlineInterval interval,
            int monthsBack,
            CancellationToken token)
        {
            var endTime = DateTime.UtcNow;
            var startTime = endTime.AddMonths(-monthsBack);

            var klines = await DownloadSymbolAsync(symbol, interval, startTime, endTime, token);
            if (klines.Count == 0)
            {
                OnLog?.Invoke($"⚠️ [HistoricalDownloader] {symbol} {interval} 다운로드 결과 0봉");
                return 0;
            }

            var intervalText = IntervalToText(interval);

            // [v4.7.3] 지표 계산 루프 제거. SqlBulkCopy로 전체를 단일 호출에 저장
            var batches = klines.Select(k => new CandleData
            {
                Symbol = symbol,
                Interval = intervalText,
                OpenTime = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = (float)k.Volume,
            }).ToList();

            try
            {
                await _databaseService.SaveCandleDataBulkAsync(batches);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [HistoricalDownloader] {symbol} 벌크 저장 실패: {ex.Message}");
                return 0;
            }

            OnLog?.Invoke($"✅ [HistoricalDownloader] {symbol} {interval} → {batches.Count}봉 저장 ({startTime:yyyy-MM-dd}~{endTime:yyyy-MM-dd})");
            return batches.Count;
        }

        /// <summary>
        /// 전체 일괄 다운로드 — 단계적 처리
        /// Phase 1: 메이저 4개 병렬 (즉시 활성화 목적)
        /// Phase 2: 알트 5분봉 병렬 gate(6)
        /// Phase 3: 알트 1분봉 병렬 gate(6)
        /// ETA는 최근 10개 심볼 소요시간의 EMA 기반 (누적평균 대비 안정적)
        /// </summary>
        public async Task<DownloadSummary> DownloadAllAsync(
            int monthsBack,
            int topAltCount,
            bool includeOneMinuteForSpike,
            CancellationToken token)
        {
            var summary = new DownloadSummary { StartTime = DateTime.Now };

            var majors = new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };

            OnLog?.Invoke($"📡 [HistoricalDownloader] 알트 상위 {topAltCount}개 조회 중...");
            var alts = await GetTopAltSymbolsAsync(topAltCount, token);
            OnLog?.Invoke($"📡 [HistoricalDownloader] 알트 {alts.Count}개 확보");

            int altsCount5m = alts.Count;
            int altsCount1m = includeOneMinuteForSpike ? Math.Min(50, alts.Count) : 0;
            int total = majors.Count + altsCount5m + altsCount1m;

            // ── ETA: 최근 10개 심볼 완료 시간을 EMA로 추적 ─────────────────
            int current = 0;
            int majorReady = 0;
            int altReady = 0;
            var recentDurations = new Queue<double>();
            const int EmaWindow = 10;
            DateTime lastSymbolTime = DateTime.UtcNow;
            var etaLock = new object();

            void ReportProgress(string sym, string phase)
            {
                int now = Interlocked.Increment(ref current);

                TimeSpan? eta = null;
                lock (etaLock)
                {
                    var nowUtc = DateTime.UtcNow;
                    double dt = (nowUtc - lastSymbolTime).TotalSeconds;
                    lastSymbolTime = nowUtc;

                    // 첫 심볼은 샘플 없음 → 전체 경과시간 기반 대체 추정
                    recentDurations.Enqueue(dt);
                    while (recentDurations.Count > EmaWindow) recentDurations.Dequeue();

                    if (now < total && recentDurations.Count > 0)
                    {
                        double avgPerSym = recentDurations.Average();
                        // 병렬 실행 중이면 실제 시간은 1/MaxParallelSymbols 로 줄어듦
                        double parallelFactor = Math.Min(MaxParallelSymbols, Math.Max(1, total - now));
                        double effectivePer = avgPerSym / parallelFactor;
                        eta = TimeSpan.FromSeconds(effectivePer * (total - now));
                    }
                }

                OnProgress?.Invoke(now, total, $"{sym} ({now}/{total})");
                OnDetailedProgress?.Invoke(new DownloadProgress
                {
                    Current = now,
                    Total = total,
                    CurrentSymbol = sym,
                    TotalCandlesSaved = summary.TotalSaved,
                    Elapsed = DateTime.Now - summary.StartTime,
                    EstimatedRemaining = eta,
                    Phase = phase,
                    MajorReady = Volatile.Read(ref majorReady),
                    AltReady = Volatile.Read(ref altReady),
                    AltTotal = altsCount5m
                });
            }

            async Task ProcessSymbol(string sym, KlineInterval interval, int months, string label, string phase, SemaphoreSlim? gate)
            {
                if (gate != null) await gate.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;
                    int saved = await DownloadAndSaveAsync(sym, interval, months, token);
                    lock (summary)
                    {
                        summary.SymbolResults[sym + "_" + label] = saved;
                        summary.TotalSaved += saved;
                    }

                    // 심볼 완료 이벤트 (phase별)
                    OnSymbolReady?.Invoke(sym, phase);

                    if (phase == "major") Interlocked.Increment(ref majorReady);
                    else if (phase == "alt_5m") Interlocked.Increment(ref altReady);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [HistoricalDownloader] {sym} {label} 실패: {ex.Message}");
                    Interlocked.Increment(ref summary.ErrorCount);
                }
                finally
                {
                    ReportProgress(sym, phase);
                    gate?.Release();
                }
            }

            // ── Phase 1: 메이저 4개 병렬 (gate 없이 즉시 전개) ──────────────
            OnLog?.Invoke("🚀 [HistoricalDownloader] Phase 1 — 메이저 4개 다운로드 시작");
            lock (etaLock) { lastSymbolTime = DateTime.UtcNow; }
            var majorTasks = majors
                .Select(sym => ProcessSymbol(sym, KlineInterval.FiveMinutes, monthsBack, "5m", "major", null))
                .ToList();
            await Task.WhenAll(majorTasks);
            OnLog?.Invoke($"✅ [HistoricalDownloader] Phase 1 완료 — 메이저 {majorReady}/4");
            OnMajorsCompleted?.Invoke();

            if (token.IsCancellationRequested)
            {
                summary.EndTime = DateTime.Now;
                summary.Duration = summary.EndTime - summary.StartTime;
                return summary;
            }

            // ── Phase 2: 알트 5분봉 병렬 (gate 6) ──────────────────────────
            OnLog?.Invoke($"🚀 [HistoricalDownloader] Phase 2 — 알트 {altsCount5m}개 5분봉 병렬 다운로드");
            using (var altGate5m = new SemaphoreSlim(MaxParallelSymbols, MaxParallelSymbols))
            {
                var altTasks = alts
                    .Select(sym => ProcessSymbol(sym, KlineInterval.FiveMinutes, monthsBack, "5m", "alt_5m", altGate5m))
                    .ToList();
                await Task.WhenAll(altTasks);
            }
            OnLog?.Invoke($"✅ [HistoricalDownloader] Phase 2 완료 — 알트 5m {altReady}/{altsCount5m}");

            if (token.IsCancellationRequested)
            {
                summary.EndTime = DateTime.Now;
                summary.Duration = summary.EndTime - summary.StartTime;
                return summary;
            }

            // ── Phase 3: 알트 1분봉 (Spike 모델용, 상위 50) ─────────────────
            if (includeOneMinuteForSpike && altsCount1m > 0)
            {
                OnLog?.Invoke($"🚀 [HistoricalDownloader] Phase 3 — 알트 {altsCount1m}개 1분봉 병렬 다운로드");
                using var altGate1m = new SemaphoreSlim(MaxParallelSymbols, MaxParallelSymbols);
                var spikeTasks = alts.Take(50)
                    .Select(sym => ProcessSymbol(sym, KlineInterval.OneMinute, Math.Min(monthsBack, 1), "1m", "alt_1m", altGate1m))
                    .ToList();
                await Task.WhenAll(spikeTasks);
                OnLog?.Invoke($"✅ [HistoricalDownloader] Phase 3 완료");
            }

            summary.EndTime = DateTime.Now;
            summary.Duration = summary.EndTime - summary.StartTime;
            OnLog?.Invoke($"🏁 [HistoricalDownloader] 완료 | {summary.TotalSaved:N0}봉 / {summary.Duration.TotalMinutes:F1}분 / 에러 {summary.ErrorCount}");
            return summary;
        }

        private static int GetIntervalMinutes(KlineInterval interval) => interval switch
        {
            KlineInterval.OneMinute => 1,
            KlineInterval.ThreeMinutes => 3,
            KlineInterval.FiveMinutes => 5,
            KlineInterval.FifteenMinutes => 15,
            KlineInterval.ThirtyMinutes => 30,
            KlineInterval.OneHour => 60,
            KlineInterval.TwoHour => 120,
            KlineInterval.FourHour => 240,
            KlineInterval.OneDay => 1440,
            _ => 5
        };

        private static string IntervalToText(KlineInterval interval) => interval switch
        {
            KlineInterval.OneMinute => "1m",
            KlineInterval.FiveMinutes => "5m",
            KlineInterval.FifteenMinutes => "15m",
            KlineInterval.OneHour => "1h",
            KlineInterval.FourHour => "4h",
            KlineInterval.OneDay => "1d",
            _ => "5m"
        };

        public class DownloadSummary
        {
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public int TotalSaved; // Interlocked.Add 대상 (lock 기반)
            public int ErrorCount; // Interlocked.Increment 대상
            public Dictionary<string, int> SymbolResults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
