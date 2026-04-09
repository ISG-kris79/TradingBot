using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 시장 급변(CRASH/PUMP) 실시간 감지기
    /// BTC/ETH/SOL 등 주요 코인의 1분 가격 변동률을 추적하여
    /// 매크로 급락/급등 이벤트를 감지하고 이벤트 발화
    /// </summary>
    public class MarketCrashDetector
    {
        // ─── 설정 ───────────────────────────────────────────
        public decimal CrashThresholdPct { get; set; } = -1.5m;   // 1분 -1.5% → CRASH
        public decimal PumpThresholdPct { get; set; } = 1.5m;     // 1분 +1.5% → PUMP
        public int MinCoinCount { get; set; } = 2;                // 최소 N개 코인 동시 급변
        public decimal ReverseEntrySizeRatio { get; set; } = 0.5m; // 리버스 진입 사이즈 (기본 50%)
        public bool Enabled { get; set; } = true;
        public int CooldownSeconds { get; set; } = 120;           // 발동 후 쿨다운 (중복 방지)

        // ─── 감시 대상 ──────────────────────────────────────
        private static readonly string[] WatchSymbols = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        private static readonly HashSet<string> MajorWatchSymbols = new(WatchSymbols, StringComparer.OrdinalIgnoreCase);

        // ─── 가격 히스토리 (1분 스냅샷) ─────────────────────
        private readonly ConcurrentDictionary<string, decimal> _priceSnapshot = new();
        private DateTime _lastSnapshotTime = DateTime.MinValue;
        private DateTime _lastTriggerTime = DateTime.MinValue;

        // ─── 이벤트 ────────────────────────────────────────
        /// <summary>CRASH 감지: (crashCoins, avgDropPct)</summary>
        public event Action<List<string>, decimal>? OnCrashDetected;
        /// <summary>PUMP 감지: (pumpCoins, avgRisePct)</summary>
        public event Action<List<string>, decimal>? OnPumpDetected;
        public event Action<string>? OnLog;

        /// <summary>
        /// TickerCache 업데이트마다 호출 — 1분 간격으로 스냅샷 비교
        /// </summary>
        public void CheckPriceVelocity(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            if (!Enabled) return;

            var now = DateTime.Now;

            // 첫 스냅샷 저장
            if (_lastSnapshotTime == DateTime.MinValue)
            {
                TakeSnapshot(tickerCache);
                return;
            }

            // 1분 미만이면 스킵
            if ((now - _lastSnapshotTime).TotalSeconds < 60)
                return;

            // 쿨다운 체크
            if ((now - _lastTriggerTime).TotalSeconds < CooldownSeconds)
            {
                TakeSnapshot(tickerCache);
                return;
            }

            // 1분 가격 변동률 계산
            var crashCoins = new List<(string symbol, decimal changePct)>();
            var pumpCoins = new List<(string symbol, decimal changePct)>();

            foreach (var sym in WatchSymbols)
            {
                if (!_priceSnapshot.TryGetValue(sym, out var prevPrice) || prevPrice <= 0)
                    continue;

                if (!tickerCache.TryGetValue(sym, out var current) || current.LastPrice <= 0)
                    continue;

                decimal changePct = (current.LastPrice - prevPrice) / prevPrice * 100m;

                if (changePct <= CrashThresholdPct)
                    crashCoins.Add((sym, changePct));

                if (changePct >= PumpThresholdPct)
                    pumpCoins.Add((sym, changePct));
            }

            // 스냅샷 갱신
            TakeSnapshot(tickerCache);

            // CRASH 판정: N개 이상 동시 급락
            if (crashCoins.Count >= MinCoinCount)
            {
                decimal avgDrop = crashCoins.Average(c => c.changePct);
                string coinList = string.Join(", ", crashCoins.Select(c => $"{c.symbol}({c.changePct:+0.00;-0.00}%)"));
                OnLog?.Invoke($"🔴 [CRASH 감지] {coinList} | 평균 {avgDrop:+0.00;-0.00}%");
                _lastTriggerTime = now;
                OnCrashDetected?.Invoke(crashCoins.Select(c => c.symbol).ToList(), avgDrop);
            }

            // PUMP 판정: N개 이상 동시 급등
            if (pumpCoins.Count >= MinCoinCount)
            {
                decimal avgRise = pumpCoins.Average(c => c.changePct);
                string coinList = string.Join(", ", pumpCoins.Select(c => $"{c.symbol}({c.changePct:+0.00;-0.00}%)"));
                OnLog?.Invoke($"🟢 [PUMP 감지] {coinList} | 평균 {avgRise:+0.00;-0.00}%");
                _lastTriggerTime = now;
                OnPumpDetected?.Invoke(pumpCoins.Select(c => c.symbol).ToList(), avgRise);
            }
        }

        private void TakeSnapshot(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            foreach (var sym in WatchSymbols)
            {
                if (tickerCache.TryGetValue(sym, out var item) && item.LastPrice > 0)
                    _priceSnapshot[sym] = item.LastPrice;
            }
            _lastSnapshotTime = DateTime.Now;
        }

        // ═══════════════════════════════════════════════════════════════
        // [개별 코인 급등 감지] 전 종목 5분 가격 변동률 스캔
        // ═══════════════════════════════════════════════════════════════

        public decimal SpikeThresholdPct { get; set; } = 3.0m;    // [v3.2.19] 30초 +3% → 진짜 급등만
        public decimal SpikeVolumeMinRatio { get; set; } = 2.0m;

        private readonly ConcurrentDictionary<string, decimal> _allPriceSnapshot = new();
        private readonly ConcurrentDictionary<string, decimal> _allVolumeSnapshot = new(); // [v3.4.1] 거래량 비교용
        private DateTime _lastAllSnapshotTime = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _spikeCooldown = new();

        /// <summary>급등/급락 코인 발견 이벤트: (symbol, changePct, currentPrice)</summary>
        public event Action<string, decimal, decimal>? OnSpikeDetected;

        /// <summary>[v3.6.5] 거래량 급증 감지 (가격 변동 전): (symbol, volumeRatio, currentPrice)</summary>
        public event Action<string, decimal, decimal>? OnVolumeSurgeDetected;
        private readonly ConcurrentDictionary<string, DateTime> _volumeSurgeCooldown = new();

        /// <summary>[v3.2.7] 전 종목(메이저 포함) 1분 가격 변동률 스캔</summary>
        public void CheckSpikeDetection(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            if (!Enabled) return;

            var now = DateTime.Now;

            if (_lastAllSnapshotTime == DateTime.MinValue)
            {
                TakeAllSnapshot(tickerCache);
                return;
            }

            // [v3.2.15] 1분 → 30초 간격 (급등 빠른 감지)
            if ((now - _lastAllSnapshotTime).TotalSeconds < 30)
                return;

            foreach (var kvp in tickerCache)
            {
                string sym = kvp.Value.Symbol ?? kvp.Key;
                if (string.IsNullOrWhiteSpace(sym) || !sym.EndsWith("USDT")) continue;
                // [v3.2.7] 메이저 제외 제거 — 전 종목 대상

                if (_spikeCooldown.TryGetValue(sym, out var cd) && now < cd) continue;
                if (!_allPriceSnapshot.TryGetValue(sym, out var prevPrice) || prevPrice <= 0) continue;
                if (kvp.Value.LastPrice <= 0) continue;

                decimal changePct = (kvp.Value.LastPrice - prevPrice) / prevPrice * 100m;

                if (kvp.Value.QuoteVolume < 1_000_000m) continue; // [v3.2.19] $500K → $1M 복원
                if (kvp.Value.LastPrice < 0.001m) continue; // [v3.2.40] 초저가 밈코인 제외

                // [v3.4.1] 거래량 비율 체크 — 이전 30초 대비 거래량 급증 확인
                if (_allVolumeSnapshot.TryGetValue(sym, out var prevVolume) && prevVolume > 0)
                {
                    decimal volumeRatio = kvp.Value.QuoteVolume / prevVolume;
                    if (volumeRatio < SpikeVolumeMinRatio) continue; // 거래량 미달 → 가짜 스파이크
                }

                bool isMajorCoin = MajorWatchSymbols.Contains(sym);

                // [v3.2.19] PUMP 코인은 급등(+3%)만, 메이저는 급등/급락 둘 다
                bool spikeDetected = false;
                if (changePct >= SpikeThresholdPct)
                    spikeDetected = true; // 급등 → 모든 코인
                else if (changePct <= -SpikeThresholdPct && isMajorCoin)
                    spikeDetected = true; // 급락 → 메이저만

                if (spikeDetected)
                {
                    _spikeCooldown[sym] = now.AddMinutes(15);
                    string direction = changePct > 0 ? "급등" : "급락";
                    OnLog?.Invoke($"⚡ [{direction} 감지] {sym} {changePct:+0.00;-0.00}% (30초) | 가격: {kvp.Value.LastPrice}");
                    OnSpikeDetected?.Invoke(sym, changePct, kvp.Value.LastPrice);
                }
            }

            // [v3.8.0] 거래량 증분 급증 선행 감지 — 30초간 증분이 평소 30초 증분의 3배+
            foreach (var kvp in tickerCache)
            {
                string vSym = kvp.Value.Symbol ?? kvp.Key;
                if (string.IsNullOrWhiteSpace(vSym) || !vSym.EndsWith("USDT")) continue;
                if (MajorWatchSymbols.Contains(vSym)) continue;
                if (kvp.Value.QuoteVolume < 500_000m) continue;
                if (kvp.Value.LastPrice < 0.001m) continue;

                if (_volumeSurgeCooldown.TryGetValue(vSym, out var vcd) && now < vcd) continue;
                if (!_allVolumeSnapshot.TryGetValue(vSym, out var prevVol) || prevVol <= 0) continue;
                if (!_allPriceSnapshot.TryGetValue(vSym, out var prevPx) || prevPx <= 0) continue;

                // 증분 계산: 현재 24h누적 - 이전 24h누적 = 30초간 거래량
                decimal volIncrement = kvp.Value.QuoteVolume - prevVol;
                if (volIncrement <= 0) continue;

                // 평소 30초 증분 추정: 24h 누적 / (24*120) = 30초 평균
                decimal avgIncrement30s = kvp.Value.QuoteVolume / (24m * 120m);
                if (avgIncrement30s <= 0) continue;

                decimal volIncrRatio = volIncrement / avgIncrement30s;
                decimal pxChange = Math.Abs((kvp.Value.LastPrice - prevPx) / prevPx * 100m);

                // 30초 증분이 평소의 5배+ && 가격 1.5% 미만 변동
                if (volIncrRatio >= 5.0m && pxChange < 1.5m)
                {
                    _volumeSurgeCooldown[vSym] = now.AddMinutes(10);
                    OnLog?.Invoke($"🔥 [거래량 급증] {vSym} 30초 증분 {volIncrRatio:F1}x (가격 {pxChange:+0.0;-0.0}%) → 감시 풀 등록");
                    OnVolumeSurgeDetected?.Invoke(vSym, volIncrRatio, kvp.Value.LastPrice);
                }
            }

            TakeAllSnapshot(tickerCache);
        }

        private void TakeAllSnapshot(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            foreach (var kvp in tickerCache)
            {
                string sym = kvp.Value.Symbol ?? kvp.Key;
                if (!string.IsNullOrWhiteSpace(sym) && kvp.Value.LastPrice > 0)
                {
                    _allPriceSnapshot[sym] = kvp.Value.LastPrice;
                    if (kvp.Value.QuoteVolume > 0)
                        _allVolumeSnapshot[sym] = kvp.Value.QuoteVolume;
                }
            }
            _lastAllSnapshotTime = DateTime.Now;
        }
    }
}
