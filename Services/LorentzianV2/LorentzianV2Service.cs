using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    /// <summary>
    /// [v5.20.0] per-symbol Lorentzian ANN 풀 관리 서비스
    ///
    /// 학습 흐름:
    ///   1) BackfillFromCandlesAsync(symbol, klines):
    ///      - 5m 캔들 시계열을 받아 매 봉마다 (i%4 정렬은 Engine 내부에서)
    ///        feature 추출 + 4봉 후 라벨링 → AddSample
    ///   2) Predict(symbol, currentKlines):
    ///      - 현재 시점 feature 추출 → engine.Predict
    ///
    /// API3USDT 같은 단일 심볼 특화 효과를 위해 글로벌 풀이 아닌 Dictionary&lt;symbol, engine&gt;
    /// </summary>
    public sealed class LorentzianV2Service
    {
        private readonly ConcurrentDictionary<string, LorentzianAnnEngine> _engines = new();
        public event Action<string>? OnLog;

        public int NeighborsCount { get; set; } = 8;
        public int MaxBarsBack { get; set; } = 2000;
        public int FeatureCount { get; set; } = 7;  // [v5.20.0] 5 → 7 (f6: 최대상승폭, f7: H1 기울기)

        public LorentzianAnnEngine GetOrCreate(string symbol)
        {
            return _engines.GetOrAdd(symbol, s => new LorentzianAnnEngine(s, NeighborsCount, MaxBarsBack, FeatureCount));
        }

        public bool IsReady(string symbol) => _engines.TryGetValue(symbol, out var e) && e.IsReady;

        public LorentzianAnnPrediction Predict(string symbol, List<IBinanceKline> currentKlines)
        {
            var feat = LorentzianFeatures.Extract(currentKlines);
            if (feat == null)
                return new LorentzianAnnPrediction { Symbol = symbol, IsReady = false, K = NeighborsCount };
            return GetOrCreate(symbol).Predict(feat);
        }

        /// <summary>
        /// 과거 5m 캔들 → sliding window 로 모든 봉에 대해 feature 추출 + 4봉 후 라벨링 → 학습
        /// 30일 5m = ~8640봉, 50봉 lookback 후부터 라벨 가능 = 약 8580 샘플 / 심볼
        /// (engine 내부 maxBarsBack=2000 으로 trim)
        /// </summary>
        public int BackfillFromCandles(string symbol, List<IBinanceKline> ascCandles)
        {
            if (ascCandles == null || ascCandles.Count < 305) return 0;  // [v5.20.0] H1 EMA20 5봉 = 5m × 300 필요
            var engine = GetOrCreate(symbol);
            int added = 0;
            // i = 300 부터 (H1 EMA20 lookback) ... 마지막 4봉 전까지 (forward label 필요)
            for (int i = 300; i < ascCandles.Count - 4; i++)
            {
                var slice = ascCandles.GetRange(0, i + 1); // 봉 i 까지
                var feat = LorentzianFeatures.Extract(slice);
                if (feat == null) continue;

                // Pine 라벨링: src[4] vs src[0] = future close vs current close
                decimal nowClose = ascCandles[i].ClosePrice;
                decimal future4 = ascCandles[i + 4].ClosePrice;
                int label = future4 > nowClose ? 1 : future4 < nowClose ? -1 : 0;

                engine.AddSample(feat, label);
                added++;
            }
            return added;
        }

        public async Task<int> BackfillAllAsync(
            DbManager db,
            IEnumerable<string> symbols,
            int daysBack = 30,
            CancellationToken token = default)
        {
            int totalSamples = 0;
            int processed = 0;
            foreach (var sym in symbols)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    int max5m = daysBack * 24 * 12 + 200;
                    var raw = await db.GetCandleDataByIntervalAsync(sym, "5m", max5m);
                    if (raw == null || raw.Count < 100) continue;

                    var ordered = raw.OrderBy(c => c.OpenTime)
                                     .Select(c => new TradingBot.Services.KlineAdapter(c))
                                     .Cast<IBinanceKline>()
                                     .ToList();

                    int added = BackfillFromCandles(sym, ordered);
                    totalSamples += added;
                    processed++;
                    if (processed % 10 == 0)
                        OnLog?.Invoke($"📚 [LorentzianV2 Backfill] {processed} 심볼 / {totalSamples} 샘플 누적");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [LorentzianV2 Backfill] {sym} 실패: {ex.Message}");
                }
            }
            OnLog?.Invoke($"✅ [LorentzianV2 Backfill 완료] {processed} 심볼 / {totalSamples} 샘플");
            return totalSamples;
        }

        public Dictionary<string, (int Samples, bool Ready)> GetStatusSnapshot()
        {
            var dict = new Dictionary<string, (int, bool)>();
            foreach (var kv in _engines) dict[kv.Key] = (kv.Value.SampleCount, kv.Value.IsReady);
            return dict;
        }
    }
}
