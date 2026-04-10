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
    /// [v4.7.0] Binance 과거 캔들 대량 다운로드 + DB 저장
    /// 페이지네이션으로 startTime/endTime 사이의 모든 캔들 수집
    /// rate limit 준수: 분당 2,400 weight 한도, 요청간 100ms
    /// </summary>
    public class HistoricalDataDownloader
    {
        private readonly BinanceRestClient _restClient = new();
        private readonly DbManager _dbManager;
        private readonly DatabaseService _databaseService;

        public event Action<string>? OnLog;
        public event Action<int, int, string>? OnProgress; // current, total, message

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

                    // rate limit 준수 (분당 2400 weight, weight=2 per kline = 1200 req/min = 50ms 간격)
                    // 보수적으로 100ms 간격 사용
                    await Task.Delay(100, token);
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
        /// 다운로드 + DB 저장 (지표 계산 포함)
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

            // DB 저장 — CandleData 모델로 변환 후 SaveCandleDataBulkAsync
            var intervalText = IntervalToText(interval);
            var batches = new List<CandleData>();
            int saved = 0;
            const int chunkSize = 500; // 500개씩 청크 저장

            // 지표는 슬라이딩 윈도우로 계산
            for (int i = 0; i < klines.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                // 최소 30봉 이후부터 지표 계산 가능
                int windowStart = Math.Max(0, i - 99);
                var window = klines.GetRange(windowStart, i - windowStart + 1);
                if (window.Count < 30) continue;

                double rsi = IndicatorCalculator.CalculateRSI(window, 14);
                var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(window);
                double atr = IndicatorCalculator.CalculateATR(window, 14);

                var current = klines[i];
                batches.Add(new CandleData
                {
                    Symbol = symbol,
                    Interval = intervalText,
                    OpenTime = current.OpenTime,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                });

                if (batches.Count >= chunkSize)
                {
                    try
                    {
                        await _databaseService.SaveCandleDataBulkAsync(batches);
                        saved += batches.Count;
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [HistoricalDownloader] {symbol} 청크 저장 실패: {ex.Message}");
                    }
                    batches.Clear();
                }
            }

            if (batches.Count > 0)
            {
                try
                {
                    await _databaseService.SaveCandleDataBulkAsync(batches);
                    saved += batches.Count;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [HistoricalDownloader] {symbol} 마지막 청크 실패: {ex.Message}");
                }
            }

            OnLog?.Invoke($"✅ [HistoricalDownloader] {symbol} {interval} → {saved}봉 저장 ({startTime:yyyy-MM-dd}~{endTime:yyyy-MM-dd})");
            return saved;
        }

        /// <summary>
        /// 전체 일괄 다운로드: 메이저 + PUMP 알트
        /// </summary>
        public async Task<DownloadSummary> DownloadAllAsync(
            int monthsBack,
            int topAltCount,
            bool includeOneMinuteForSpike,
            CancellationToken token)
        {
            var summary = new DownloadSummary { StartTime = DateTime.Now };

            var majors = new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };

            // 1) 알트 상위 N개 추출
            OnLog?.Invoke($"📡 [HistoricalDownloader] 알트 상위 {topAltCount}개 조회 중...");
            var alts = await GetTopAltSymbolsAsync(topAltCount, token);
            OnLog?.Invoke($"📡 [HistoricalDownloader] 알트 {alts.Count}개 확보");

            var allSymbols = majors.Concat(alts).ToList();
            int total = allSymbols.Count + (includeOneMinuteForSpike ? alts.Take(50).Count() : 0);
            int current = 0;

            // 2) 메이저 + 알트 5분봉
            foreach (var sym in allSymbols)
            {
                if (token.IsCancellationRequested) break;
                current++;
                OnProgress?.Invoke(current, total, $"{sym} 5분봉 다운로드");
                try
                {
                    int saved = await DownloadAndSaveAsync(sym, KlineInterval.FiveMinutes, monthsBack, token);
                    summary.SymbolResults[sym] = saved;
                    summary.TotalSaved += saved;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [HistoricalDownloader] {sym} 실패: {ex.Message}");
                    summary.ErrorCount++;
                }
            }

            // 3) Spike 모델용 1분봉 (상위 50 알트만)
            if (includeOneMinuteForSpike)
            {
                foreach (var sym in alts.Take(50))
                {
                    if (token.IsCancellationRequested) break;
                    current++;
                    OnProgress?.Invoke(current, total, $"{sym} 1분봉 다운로드 (Spike)");
                    try
                    {
                        int saved = await DownloadAndSaveAsync(sym, KlineInterval.OneMinute, Math.Min(monthsBack, 1), token);
                        summary.TotalSaved += saved;
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [HistoricalDownloader] {sym} 1m 실패: {ex.Message}");
                        summary.ErrorCount++;
                    }
                }
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
            public int TotalSaved { get; set; }
            public int ErrorCount { get; set; }
            public Dictionary<string, int> SymbolResults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
