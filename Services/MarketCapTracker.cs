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

        // [v5.23.74] 실효 알트 N 보정 — CoinGecko 시총 Top N 에 섞인 스테이블/랩드/스테이킹 토큰이
        //   슬롯을 잡아먹어 실제 트레이드 가능 알트 컷오프가 훨씬 빡빡했던 문제 fix.
        //   (예: WLDUSDT 시총 50위인데 죽은 슬롯 ~12개로 사실상 밖 → 추적 안 됨)
        //   → per_page 를 넉넉히 받아 아래 심볼 제외 후 _topN 개의 "실제 알트" 로 채움.
        private static readonly HashSet<string> _excludedSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            // 스테이블코인 (가격 ~$1, 트레이드 대상 아님)
            "USDT","USDC","DAI","USDE","FDUSD","TUSD","USDD","PYUSD","USDS","BUSD","GUSD",
            "FRAX","LUSD","USD1","SUSDE","USDP","RLUSD","USR","SUSDS","USDX","USD0","USDF","USDG","AUSD",
            // 랩드/스테이킹/LST (원자산 중복 — 별도 트레이드 의미 없음)
            "WBTC","WETH","WEETH","WSTETH","STETH","RETH","CBETH","WBETH","METH","EZETH",
            "RSETH","SOLVBTC","LBTC","WBNB","CLBTC","BUIDL","WSOL","WHYPE","BSC-USD"
        };

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
                // [v5.23.74] 죽은 슬롯(스테이블/랩드) 보정용 여유 fetch — _topN×2 받아 제외 후 _topN 개 알트 확보
                int fetchCount = Math.Min(250, _topN * 2 + 20);
                string url = $"https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page={fetchCount}&page=1&sparkline=false";
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
                    if (newSet.Count >= _topN) break;   // [v5.23.74] 실제 알트 _topN 개 채우면 종료
                    if (!el.TryGetProperty("symbol", out var symEl)) continue;
                    string? raw = symEl.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string up = raw.ToUpperInvariant();
                    // [v5.23.74] 스테이블/랩드/스테이킹 제외 → 죽은 슬롯이 알트 컷오프를 잡아먹지 않게
                    if (_excludedSymbols.Contains(up)) continue;
                    // CoinGecko: "btc" → Binance "BTCUSDT"
                    newSet.Add(up + "USDT");
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
