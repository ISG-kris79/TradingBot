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
    }
}
