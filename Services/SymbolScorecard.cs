using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Dapper;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.23.64] 심볼 자가학습 스코어카드.
    /// 30일 실거래 데이터로 심볼별 승률·PnL 계산해서 진입 사이즈 multiplier 결정.
    /// - WR ≥ 70% + n ≥ 5 + PnL ≥ +$20 → multiplier 1.5 (확신도 부스트)
    /// - WR ≤ 30% + n ≥ 5 + PnL ≤ -$30 → multiplier 0.0 (차단)
    /// - 그 외 → multiplier 1.0
    /// 1시간 주기 갱신. 첫 부팅 / fetch 실패 시 캐시 비어있음 → multiplier 1.0 폴백 (진입 막지 않음).
    /// 차단 아닌 사이즈 조절 → 진입 빈도 유지하면서 손실 최소화.
    /// </summary>
    public sealed class SymbolScorecard
    {
        public static SymbolScorecard Instance { get; } = new SymbolScorecard();

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

        private readonly Dictionary<string, decimal> _multipliers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private Timer? _timer;
        private string? _connectionString;
        private int _userId;

        public event Action<string>? OnLog;
        public bool IsReady { get; private set; }
        public DateTime LastFetchUtc { get; private set; } = DateTime.MinValue;

        private SymbolScorecard() { }

        public void Start(string connectionString, int userId)
        {
            _connectionString = connectionString;
            _userId = userId;
            // [FIX] 유저 전환 대비 — Start(재로그인/엔진 재시작) 시 이전 유저 캐시를 즉시 폐기해
            //   새 유저가 옛 유저의 심볼 배수를 이어받지 않도록 함. RefreshAsync가 채우기 전까지 1.0 폴백.
            lock (_lock) { _multipliers.Clear(); }
            IsReady = false;
            _ = Task.Run(async () => await RefreshAsync(CancellationToken.None));
            _timer ??= new Timer(_ => _ = RefreshAsync(CancellationToken.None),
                                 null, RefreshInterval, RefreshInterval);
        }

        /// <summary>심볼별 사이즈 배수. 캐시 미준비 / 표본 부족 → 1.0 (중립).</summary>
        public decimal GetMultiplier(string symbol)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(symbol)) return 1.0m;
            lock (_lock)
            {
                return _multipliers.TryGetValue(symbol, out var m) ? m : 1.0m;
            }
        }

        public IReadOnlyDictionary<string, decimal> SnapshotMultipliers()
        {
            lock (_lock) return new Dictionary<string, decimal>(_multipliers, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<bool> RefreshAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_connectionString)) return false;
            // [FIX] 동결된 _userId 대신 현재 로그인 유저를 매 갱신마다 fresh 조회 —
            //   프로세스 수명 내내 도는 1h 타이머가 옛 유저 데이터로 배수를 계산하던 버그 차단.
            int userId = TradingBot.AppConfig.CurrentUser?.Id ?? _userId;
            if (userId <= 0) return false;
            try
            {
                const string sql = @"
SELECT Symbol,
       COUNT(*) AS N,
       SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
       SUM(PnL) AS NetPnL
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=@UserId AND IsClosed=1 AND PnL<>0
  AND EntryTime >= DATEADD(DAY,-30,GETDATE())
GROUP BY Symbol
HAVING COUNT(*) >= 5";

                using var db = new SqlConnection(_connectionString);
                var rows = await db.QueryAsync(sql, new { UserId = userId });

                var newMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                int boosted = 0, blocked = 0;
                foreach (var r in rows)
                {
                    string sym = (string)r.Symbol;
                    int n = (int)r.N;
                    int wins = (int)r.Wins;
                    decimal pnl = (decimal)r.NetPnL;
                    decimal wr = n > 0 ? (decimal)wins / n * 100m : 0m;

                    decimal m;
                    if (wr <= 30m && pnl <= -30m) { m = 0.0m; blocked++; }
                    else if (wr >= 70m && pnl >= 20m) { m = 1.5m; boosted++; }
                    else m = 1.0m;
                    newMap[sym] = m;
                }

                lock (_lock)
                {
                    _multipliers.Clear();
                    foreach (var kv in newMap) _multipliers[kv.Key] = kv.Value;
                    LastFetchUtc = DateTime.UtcNow;
                }
                IsReady = true;
                OnLog?.Invoke($"📊 [Scorecard] 30d 갱신: {newMap.Count}개 심볼 ({boosted} 부스트 1.5x, {blocked} 차단 0x, 나머지 1.0x)");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [Scorecard] 갱신 실패 (캐시 유지, IsReady={IsReady}): {ex.Message}");
                return false;
            }
        }
    }
}
