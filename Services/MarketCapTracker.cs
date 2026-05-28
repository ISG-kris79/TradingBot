using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.23.63] CoinGecko 시가총액 Top N 캐시.
    /// 알트 진입 가드: 시총 Top 30 안의 심볼만 LONG 진입 허용 (메이저는 entryCat=MAJOR 별도 통과).
    /// - 갱신: 1시간 주기 자동
    /// - 캐시 미준비 / fetch 실패 → IsReady=false → 가드는 안전 차단
    /// - 매핑: CoinGecko symbol(lowercase) → Binance "{SYMBOL}USDT"
    ///   USDT/USDC/DAI 같은 스테이블 + wstETH/stETH 등 LST는 Binance USDT 페어 없거나
    ///   _stablecoinSymbols 가드에서 별도 차단되므로 set 에 그대로 포함해도 무해.
    /// </summary>
    public sealed class MarketCapTracker
    {
        public static MarketCapTracker Instance { get; } = new MarketCapTracker();

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

        private HashSet<string> _topSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFetchUtc = DateTime.MinValue;
        private readonly object _lock = new object();
        private Timer? _timer;
        private int _topN = 50;   // [v5.23.66] 30 → 50 (사용자 지시 — 추적풀/진입 후보 확대)

        public event Action<string>? OnLog;

        /// <summary>첫 fetch 완료 여부. false 시 가드는 안전 차단.</summary>
        public bool IsReady { get; private set; }

        public DateTime LastFetchUtc
        {
            get { lock (_lock) return _lastFetchUtc; }
        }

        public int TopN
        {
            get => _topN;
            set => _topN = Math.Max(1, Math.Min(value, 250));  // CoinGecko per_page 상한
        }

        private MarketCapTracker()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingBot/5.23.63");
        }

        /// <summary>봇 부팅 시 1회 호출 — 첫 fetch 동기 시도 + 1시간 주기 타이머 시작.</summary>
        public void Start()
        {
            // 첫 fetch 는 background 로 (UI 부팅 블록 방지). 가드는 IsReady 체크하므로 안전.
            _ = Task.Run(async () => await RefreshAsync(CancellationToken.None));
            _timer ??= new Timer(_ => _ = RefreshAsync(CancellationToken.None),
                                 null, RefreshInterval, RefreshInterval);
        }

        public bool IsTopN(string symbol)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(symbol)) return false;
            lock (_lock) return _topSymbols.Contains(symbol);
        }

        public IReadOnlyCollection<string> Snapshot()
        {
            lock (_lock) return new List<string>(_topSymbols);
        }

        public async Task<bool> RefreshAsync(CancellationToken ct)
        {
            try
            {
                string url = $"https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page={_topN}&page=1&sparkline=false";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    OnLog?.Invoke($"⚠️ [MarketCap] CoinGecko HTTP {(int)resp.StatusCode} — 캐시 유지 (IsReady={IsReady})");
                    return false;
                }
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

                var newSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("symbol", out var symEl)) continue;
                    string? raw = symEl.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    // CoinGecko: "btc" → Binance "BTCUSDT"
                    newSet.Add(raw.ToUpperInvariant() + "USDT");
                }

                if (newSet.Count == 0)
                {
                    OnLog?.Invoke("⚠️ [MarketCap] CoinGecko 응답 비어있음 — 캐시 유지");
                    return false;
                }

                lock (_lock)
                {
                    _topSymbols = newSet;
                    _lastFetchUtc = DateTime.UtcNow;
                }
                IsReady = true;
                OnLog?.Invoke($"📊 [MarketCap] Top {_topN} 캐시 갱신 ({newSet.Count}개): {string.Join(", ", newSet)}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [MarketCap] fetch 실패 — 캐시 유지 (IsReady={IsReady}): {ex.Message}");
                return false;
            }
        }
    }
}
