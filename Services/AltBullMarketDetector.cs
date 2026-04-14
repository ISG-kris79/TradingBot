using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.3.9] 알트 불장(Alt Season) 감지기 — 바이낸스 API 기반
    ///
    /// 3가지 조건 동시 충족 시 알트불장 판정:
    /// 1) BTC 도미넌스 50% 이하 (BTCDOMUSDT 선물 지수)
    /// 2) 알트시즌 지수 75 이상 (상위 50개 중 BTC 대비 아웃퍼폼 비율)
    /// 3) ETH/BTC 페어 0.05 이상 (ETHBTC 현물)
    /// </summary>
    public class AltBullMarketDetector
    {
        // ─── 임계값 ─────────────────────────────────────────
        public double BtcDominanceThreshold { get; set; } = 50.0;   // BTC 도미넌스 50% 이하
        public int AltSeasonIndexThreshold { get; set; } = 75;       // 알트시즌 지수 75 이상
        public double EthBtcThreshold { get; set; } = 0.05;          // ETH/BTC 0.05 이상
        public int CheckIntervalMinutes { get; set; } = 10;          // 10분 주기 검사
        public int ConsecutiveConfirmRequired { get; set; } = 2;     // 2회 연속 충족 시 활성화

        // ─── 활성화 상태 ────────────────────────────────────
        public bool IsActive { get; private set; }
        public DateTime? ActivatedAt { get; private set; }
        public DateTime? DeactivatedAt { get; private set; }

        // ─── 최근 측정값 (로그/디버그용) ──────────────────────
        public double LastBtcDominance { get; private set; }
        public int LastAltSeasonIndex { get; private set; }
        public double LastEthBtc { get; private set; }

        // ─── 내부 상태 ──────────────────────────────────────
        private DateTime _lastCheckTime = DateTime.MinValue;
        private int _consecutiveConfirmCount;
        private int _consecutiveMissCount;
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        // ─── 이벤트 ────────────────────────────────────────
        public event Action<string>? OnLog;
        public event Action<bool>? OnAltBullStateChanged;

        /// <summary>
        /// TickerCache 기반 주기적 검사 (기존 호출부 호환)
        /// </summary>
        public void Check(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCheckTime).TotalMinutes < CheckIntervalMinutes)
                return;
            _lastCheckTime = now;

            _ = CheckAsync(tickerCache);
        }

        private async Task CheckAsync(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            try
            {
                // ── 조건 1: BTC 도미넌스 (BTCDOMUSDT 선물)
                double btcDom = await GetBtcDominanceAsync();

                // ── 조건 2: ETH/BTC 페어 (ETHBTC 현물)
                double ethBtc = await GetEthBtcAsync();

                // ── 조건 3: 알트시즌 지수 (TickerCache에서 24h 변동률 비교)
                int altSeasonIdx = CalculateAltSeasonIndex(tickerCache);

                LastBtcDominance = btcDom;
                LastEthBtc = ethBtc;
                LastAltSeasonIndex = altSeasonIdx;

                bool allMet = btcDom > 0 && btcDom <= BtcDominanceThreshold
                           && altSeasonIdx >= AltSeasonIndexThreshold
                           && ethBtc >= EthBtcThreshold;

                if (allMet)
                {
                    _consecutiveConfirmCount++;
                    _consecutiveMissCount = 0;

                    if (!IsActive && _consecutiveConfirmCount >= ConsecutiveConfirmRequired)
                    {
                        IsActive = true;
                        ActivatedAt = DateTime.UtcNow;
                        OnLog?.Invoke($"🔥 [알트불장] 활성화 | BTC.D={btcDom:F1}% ETH/BTC={ethBtc:F4} AltIdx={altSeasonIdx}");
                        OnAltBullStateChanged?.Invoke(true);
                    }
                }
                else
                {
                    _consecutiveMissCount++;
                    _consecutiveConfirmCount = 0;

                    if (IsActive && _consecutiveMissCount >= 2)
                    {
                        IsActive = false;
                        DeactivatedAt = DateTime.UtcNow;
                        ActivatedAt = null;
                        OnLog?.Invoke($"💧 [알트불장] 해제 | BTC.D={btcDom:F1}% ETH/BTC={ethBtc:F4} AltIdx={altSeasonIdx}");
                        OnAltBullStateChanged?.Invoke(false);
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [알트불장] 검사 오류: {ex.Message}");
            }
        }

        /// <summary>BTCDOMUSDT 선물 지수에서 BTC 도미넌스 조회</summary>
        private async Task<double> GetBtcDominanceAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("https://fapi.binance.com/fapi/v1/ticker/price?symbol=BTCDOMUSDT");
                using var doc = JsonDocument.Parse(json);
                var price = doc.RootElement.GetProperty("price").GetString();
                if (double.TryParse(price, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double val))
                    return val / 100.0; // 5204.5 → 52.045%
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [BTCDOM] 조회 실패: {ex.Message}");
            }
            return 0;
        }

        /// <summary>ETHBTC 현물 페어 가격 조회</summary>
        private async Task<double> GetEthBtcAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("https://api.binance.com/api/v3/ticker/price?symbol=ETHBTC");
                using var doc = JsonDocument.Parse(json);
                var price = doc.RootElement.GetProperty("price").GetString();
                if (double.TryParse(price, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double val))
                    return val;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [ETHBTC] 조회 실패: {ex.Message}");
            }
            return 0;
        }

        /// <summary>TickerCache에서 알트시즌 지수 자체 계산 (상위 50개 중 BTC 대비 24h 아웃퍼폼 비율)</summary>
        private int CalculateAltSeasonIndex(ConcurrentDictionary<string, TickerCacheItem> tickerCache)
        {
            try
            {
                var stables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "USDCUSDT", "DAIUSDT", "TUSDUSDT", "BUSDUSDT", "FDUSDUSDT", "USDSUSDT", "EURUSDT" };

                double btcChange = 0;
                if (tickerCache.TryGetValue("BTCUSDT", out var btc) && btc.OpenPrice > 0)
                    btcChange = (double)((btc.LastPrice - btc.OpenPrice) / btc.OpenPrice * 100m);

                var alts = tickerCache.Values
                    .Where(t => t.LastPrice > 0 && t.OpenPrice > 0
                        && !string.IsNullOrEmpty(t.Symbol)
                        && t.Symbol!.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                        && t.Symbol != "BTCUSDT"
                        && !stables.Contains(t.Symbol!))
                    .OrderByDescending(t => t.QuoteVolume)
                    .Take(50)
                    .ToList();

                if (alts.Count == 0) return 0;

                int outperform = alts.Count(t =>
                {
                    double change = (double)((t.LastPrice - t.OpenPrice) / t.OpenPrice * 100m);
                    return change > btcChange;
                });

                return (int)(outperform * 100.0 / alts.Count);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>알트불장 모드 시 레버리지 조정 (절반, 최소 5x)</summary>
        public int AdjustLeverage(int originalLeverage)
        {
            if (!IsActive) return originalLeverage;
            return Math.Max(5, originalLeverage / 2);
        }

        /// <summary>알트불장 모드 시 포지션 사이즈 배수 (70%)</summary>
        public decimal AdjustSizeMultiplier(decimal originalMultiplier)
        {
            if (!IsActive) return originalMultiplier;
            return originalMultiplier * 0.7m;
        }

        /// <summary>현재 상태 요약</summary>
        public string GetStatusSummary()
        {
            return IsActive
                ? $"🔥 알트불장 ON | BTC.D={LastBtcDominance:F1}% ETH/BTC={LastEthBtc:F4} AltIdx={LastAltSeasonIndex}"
                : $"💤 알트불장 OFF | BTC.D={LastBtcDominance:F1}% ETH/BTC={LastEthBtc:F4} AltIdx={LastAltSeasonIndex}";
        }
    }
}
