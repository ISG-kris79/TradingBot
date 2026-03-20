using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [백그라운드 AI 데이터 수집 서비스]
    ///
    /// 앱 시작시 자동으로 백그라운드에서 실행:
    ///   1. 캐시에서 마지막 수집 시점 확인
    ///   2. 그 이후~현재까지만 API 수집
    ///   3. 캐시 업데이트
    ///   4. 주기적으로 반복 (기본 30분마다)
    ///
    /// 첫 실행시 3년치 전체 수집, 이후는 증분만.
    /// </summary>
    public class AIDataCollectorService
    {
        private static AIDataCollectorService? _instance;
        public static AIDataCollectorService Instance => _instance ??= new AIDataCollectorService();

        private CancellationTokenSource? _cts;
        private Task? _runningTask;
        private bool _isCollecting;

        public bool IsRunning => _runningTask != null && !_runningTask.IsCompleted;
        public bool IsCollecting => _isCollecting;
        public DateTime? LastCollectTime { get; private set; }
        public string LastStatus { get; private set; } = "대기";

        public event Action<string>? OnLog;

        // 설정
        public int CollectIntervalMinutes { get; set; } = 30;
        public int HistoryMonths { get; set; } = 36; // 3년

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "KlineCache");

        private static readonly string[] Symbols = {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT",
            "DOGEUSDT", "PEPEUSDT", "WIFUSDT", "BONKUSDT"
        };

        private static readonly (KlineInterval interval, string label)[] Intervals = {
            (KlineInterval.FiveMinutes,    "5m"),
            (KlineInterval.FifteenMinutes, "15m"),
            (KlineInterval.OneHour,        "1H"),
            (KlineInterval.FourHour,       "4H"),
        };

        // ═══ 시작 (앱 시작시 호출) ═══
        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _runningTask = Task.Run(() => BackgroundLoop(_cts.Token));
            Log("백그라운드 데이터 수집 서비스 시작");
        }

        // ═══ 중지 ═══
        public void Stop()
        {
            _cts?.Cancel();
            Log("백그라운드 데이터 수집 서비스 중지");
        }

        // ═══ 수동 트리거 (즉시 수집) ═══
        public void TriggerNow()
        {
            if (_isCollecting)
            {
                Log("이미 수집 중입니다");
                return;
            }
            _ = Task.Run(() => CollectAll(CancellationToken.None));
        }

        // ═══ 백그라운드 루프 ═══
        private async Task BackgroundLoop(CancellationToken ct)
        {
            // 앱 시작 후 10초 대기 (초기화 완료 대기)
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CollectAll(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"수집 오류: {ex.Message}");
                    LastStatus = $"오류: {ex.Message}";
                }

                // 다음 수집까지 대기
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(CollectIntervalMinutes), ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        // ═══ 전체 수집 ═══
        private async Task CollectAll(CancellationToken ct)
        {
            _isCollecting = true;
            LastStatus = "수집 중...";

            var endDate = DateTime.UtcNow;
            var startDate = endDate.AddMonths(-HistoryMonths);
            int totalNew = 0;

            using var client = new BinanceRestClient();

            for (int si = 0; si < Symbols.Length; si++)
            {
                ct.ThrowIfCancellationRequested();
                var sym = Symbols[si];

                foreach (var (interval, label) in Intervals)
                {
                    ct.ThrowIfCancellationRequested();

                    var cached = LoadCache(sym, label);
                    DateTime fetchFrom = cached.Count > 0
                        ? cached.Max(c => c.Time).AddMinutes(1)
                        : startDate;

                    if (fetchFrom >= endDate.AddMinutes(-5))
                        continue; // 이미 최신

                    var newKlines = await FetchKlinesAsync(client, sym, interval, fetchFrom, endDate, ct);
                    if (newKlines.Count == 0) continue;

                    var lastCachedTime = cached.Count > 0 ? cached[^1].Time : DateTime.MinValue;
                    var deduped = newKlines
                        .Where(k => k.OpenTime > lastCachedTime)
                        .Select(k => new CachedBar
                        {
                            Time = k.OpenTime, O = (double)k.OpenPrice, H = (double)k.HighPrice,
                            L = (double)k.LowPrice, C = (double)k.ClosePrice, Vol = (double)k.Volume
                        }).ToList();

                    if (deduped.Count > 0)
                    {
                        cached.AddRange(deduped);

                        // 3년 이전 데이터 정리 (메모리 절약)
                        cached = cached.Where(c => c.Time >= startDate).ToList();

                        SaveCache(sym, label, cached);
                        totalNew += deduped.Count;
                    }
                }
            }

            LastCollectTime = DateTime.UtcNow;
            _isCollecting = false;

            if (totalNew > 0)
            {
                LastStatus = $"완료: +{totalNew:N0}개 ({LastCollectTime:HH:mm:ss})";
                Log($"수집 완료: +{totalNew:N0}개 신규 캔들");
            }
            else
            {
                LastStatus = $"최신 상태 ({LastCollectTime:HH:mm:ss})";
            }
        }

        // ═══ 캐시 상태 조회 ═══
        public Dictionary<string, (int count, DateTime? lastTime)> GetCacheStatus()
        {
            var result = new Dictionary<string, (int, DateTime?)>();
            foreach (var sym in Symbols)
            foreach (var (_, label) in Intervals)
            {
                string key = $"{sym}_{label}";
                var cached = LoadCache(sym, label);
                result[key] = (cached.Count, cached.Count > 0 ? cached[^1].Time : null);
            }
            return result;
        }

        // ═══ 캐시 구조 ═══
        private class CachedBar
        {
            public DateTime Time;
            public double O, H, L, C, Vol;
        }

        // ═══ 캐시 저장 (CSV) ═══
        private static void SaveCache(string symbol, string interval, List<CachedBar> bars)
        {
            Directory.CreateDirectory(CacheDir);
            string path = Path.Combine(CacheDir, $"{symbol}_{interval}.csv");
            using var sw = new StreamWriter(path, false, Encoding.UTF8);
            foreach (var b in bars)
                sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:o},{1},{2},{3},{4},{5}", b.Time, b.O, b.H, b.L, b.C, b.Vol));
        }

        // ═══ 캐시 로드 ═══
        private static List<CachedBar> LoadCache(string symbol, string interval)
        {
            string path = Path.Combine(CacheDir, $"{symbol}_{interval}.csv");
            if (!File.Exists(path)) return new List<CachedBar>();

            var bars = new List<CachedBar>();
            foreach (var line in File.ReadLines(path))
            {
                var p = line.Split(',');
                if (p.Length < 6) continue;
                if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                bars.Add(new CachedBar
                {
                    Time = time,
                    O = double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o) ? o : 0,
                    H = double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h) ? h : 0,
                    L = double.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0,
                    C = double.TryParse(p[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 0,
                    Vol = double.TryParse(p[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0,
                });
            }
            return bars;
        }

        // ═══ API 수집 ═══
        private static async Task<List<IBinanceKline>> FetchKlinesAsync(
            BinanceRestClient client, string symbol, KlineInterval interval,
            DateTime startUtc, DateTime endUtc, CancellationToken ct)
        {
            var result = new List<IBinanceKline>();
            var cursor = startUtc;

            while (cursor < endUtc)
            {
                ct.ThrowIfCancellationRequested();

                var resp = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, interval, startTime: cursor, endTime: endUtc, limit: 1500);

                if (!resp.Success || resp.Data == null || !resp.Data.Any())
                {
                    if (!resp.Success) await Task.Delay(2000, ct);
                    break;
                }

                result.AddRange(resp.Data);
                cursor = resp.Data.Last().CloseTime.AddMilliseconds(1);
                await Task.Delay(80, ct);
            }

            return result;
        }

        private void Log(string msg)
        {
            OnLog?.Invoke($"[AI수집] {msg}");
            System.Diagnostics.Debug.WriteLine($"[AIDataCollector] {msg}");
        }
    }
}
