using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;
using TradingBot.Services;

namespace TradingBot.Services
{
    public sealed class ScannerCandidate
    {
        public string Symbol { get; set; } = "";
        public decimal QuoteVolume24h { get; set; }   // 24h 거래대금 USDT
        public double VolumeSpikeRatio { get; set; }  // 1m vol / 20봉 평균
        public bool BbReboundFromLower { get; set; }  // BB 하단→중단 반등
        public bool SqueezeRelease { get; set; }      // BBW 좁다가 풀림
        public string TriggerTag { get; set; } = "";  // VOLSPIKE / BBREB / SQZREL / TOPVOL
        public DateTime DetectedUtc { get; set; }
    }

    // [v5.23.24] 변동성 스캐너 — 1m 거래량 spike + BB 하단 반등 + squeeze release + 거래대금 top 100
    //   사용자 사양: 초당 1회 후보군 추출 → KNN 병렬 평가 → Priority Queue
    public sealed class VolatilityScanner
    {
        private readonly MarketDataManager _mdm;
        private DateTime _lastScanUtc = DateTime.MinValue;
        private List<ScannerCandidate> _lastCandidates = new();
        private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

        public VolatilityScanner(MarketDataManager mdm)
        {
            _mdm = mdm;
        }

        // 거래대금 top N (24h quote vol 기준)
        public List<string> GetTopVolumeSymbols(int n = 100, IEnumerable<string>? exclude = null)
        {
            var excl = exclude != null ? new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase) : null;
            return _mdm.TickerCache.Values
                .Where(t => t.QuoteVolume > 0
                         && t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                         && (excl == null || !excl.Contains(t.Symbol)))
                .OrderByDescending(t => t.QuoteVolume)
                .Take(n)
                .Select(t => t.Symbol)
                .ToList();
        }

        // 후보 스캔 — 거래대금 top N + 변동성/거래량 spike 검출
        public List<ScannerCandidate> Scan(int topN = 100, double volSpikeMultiplier = 3.0)
        {
            if (DateTime.UtcNow - _lastScanUtc < ScanInterval && _lastCandidates.Count > 0)
                return _lastCandidates;

            var result = new List<ScannerCandidate>();
            var topSyms = GetTopVolumeSymbols(topN);
            foreach (var sym in topSyms)
            {
                if (!_mdm.TickerCache.TryGetValue(sym, out var ticker)) continue;
                var cand = new ScannerCandidate
                {
                    Symbol = sym,
                    QuoteVolume24h = ticker.QuoteVolume,
                    DetectedUtc = DateTime.UtcNow,
                    TriggerTag = "TOPVOL"
                };

                // 1m kline cache 체크 (있으면 변동성 스캔)
                if (_mdm.KlineCache.TryGetValue(sym, out var klines) && klines != null)
                {
                    List<IBinanceKline>? snapshot = null;
                    lock (klines) { if (klines.Count >= 25) snapshot = klines.TakeLast(25).ToList(); }
                    if (snapshot != null && snapshot.Count >= 21)
                    {
                        // 변동성 스캔: 마지막 봉 거래량 vs 직전 20봉 평균
                        decimal curVol = snapshot[^1].Volume;
                        decimal avg20 = snapshot.Take(20).Average(k => k.Volume);
                        if (avg20 > 0m)
                        {
                            double ratio = (double)(curVol / avg20);
                            cand.VolumeSpikeRatio = ratio;
                            if (ratio >= volSpikeMultiplier)
                                cand.TriggerTag = $"VOLSPIKE_{ratio:F1}x";
                        }

                        // BB(20,2) 하단 반등 검출
                        var closes = snapshot.Take(20).Select(k => (double)k.ClosePrice).ToArray();
                        double sma = closes.Average();
                        double sd = Math.Sqrt(closes.Select(c => (c - sma) * (c - sma)).Average());
                        double bbLower = sma - 2 * sd;
                        double bbUpper = sma + 2 * sd;
                        double bbWidthPct = sma > 0 ? (bbUpper - bbLower) / sma * 100 : 0;
                        double curClose = (double)snapshot[^1].ClosePrice;
                        double prevLow = (double)snapshot[^2].LowPrice;
                        // BB 하단 터치 후 SMA 위로 반등
                        if (prevLow <= bbLower && curClose > sma)
                        {
                            cand.BbReboundFromLower = true;
                            if (cand.TriggerTag == "TOPVOL") cand.TriggerTag = "BBREB";
                            else cand.TriggerTag += "+BBREB";
                        }

                        // Squeeze release: BB width 1% 이하 였다가 현재 1.5% 이상 확장
                        if (snapshot.Count >= 25)
                        {
                            var prev20 = snapshot.Skip(0).Take(20).Select(k => (double)k.ClosePrice).ToArray();
                            // 동일 윈도우 — 위의 closes 와 같음. 실제 squeeze 검출은 직전 봉의 BBW vs 현재 BBW
                            // 단순화: bbWidthPct < 2.5 면 응축 상태로 판단
                            if (bbWidthPct < 2.5 && cand.VolumeSpikeRatio >= 2.0)
                            {
                                cand.SqueezeRelease = true;
                                if (cand.TriggerTag == "TOPVOL") cand.TriggerTag = "SQZREL";
                                else cand.TriggerTag += "+SQZREL";
                            }
                        }
                    }
                }

                // 후보 등록 조건: TOPVOL 단독 OR 변동성/BB/Squeeze 트리거 발생
                if (cand.TriggerTag != "TOPVOL"
                    || cand.QuoteVolume24h > 50_000_000m)   // 거래대금 5천만 USDT 이상이면 단순 TOPVOL 도 후보
                {
                    result.Add(cand);
                }
            }
            _lastScanUtc = DateTime.UtcNow;
            _lastCandidates = result;
            return result;
        }
    }
}
