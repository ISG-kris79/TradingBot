using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Dapper;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.25.18] 전략×레짐 자가학습 스코어카드 (SymbolScorecard 자매 서비스).
    /// 최근 30일 봇 청산결과(StrategyRegimeOutcome)로 전략(MEANREV/RSI2/LORENTZIAN) × BTC레짐(UP/DOWN/RANGE)
    /// 셀별 승률·PnL 계산 → 진입 사이즈 multiplier 결정.
    /// - WR ≥ 65% + n ≥ 5 + PnL ≥ 0 → 1.5x (레짐-전략 궁합 좋음, 비중↑)
    /// - WR ≤ 35% + n ≥ 5 + PnL &lt; 0 → 0.5x (궁합 나쁨, 비중↓ — 사용자 결정: 완전차단 아닌 가중만)
    /// - 그 외 / 표본부족 / 미준비 → 1.0x (중립, 진입 막지 않음)
    /// 1시간 주기 갱신. 데이터는 v5.25.18 배포 이후 축적 → 1~2주 후 유효.
    /// </summary>
    public sealed class StrategyRegimeScorecard
    {
        public static StrategyRegimeScorecard Instance { get; } = new StrategyRegimeScorecard();

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
        private const int MinSamples = 5;

        private readonly Dictionary<string, decimal> _multipliers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private Timer? _timer;
        private string? _connectionString;
        private int _userId;

        public event Action<string>? OnLog;
        public bool IsReady { get; private set; }
        public DateTime LastFetchUtc { get; private set; } = DateTime.MinValue;

        private StrategyRegimeScorecard() { }

        private static string Key(string strategy, string regime) => $"{strategy}|{regime}".ToUpperInvariant();

        public void Start(string connectionString, int userId)
        {
            _connectionString = connectionString;
            _userId = userId;
            lock (_lock) { _multipliers.Clear(); }   // 유저 전환 시 이전 캐시 폐기 (1.0 폴백)
            IsReady = false;
            _ = Task.Run(async () => await RefreshAsync(CancellationToken.None));
            _timer ??= new Timer(_ => _ = RefreshAsync(CancellationToken.None),
                                 null, RefreshInterval, RefreshInterval);
        }

        /// <summary>전략×레짐 사이즈 배수. 미준비/표본부족/레짐미상 → 1.0 (중립).</summary>
        public decimal GetMultiplier(string strategy, string regime)
        {
            if (!IsReady || string.IsNullOrWhiteSpace(strategy) || string.IsNullOrWhiteSpace(regime)) return 1.0m;
            lock (_lock)
            {
                return _multipliers.TryGetValue(Key(strategy, regime), out var m) ? m : 1.0m;
            }
        }

        public IReadOnlyDictionary<string, decimal> SnapshotMultipliers()
        {
            lock (_lock) return new Dictionary<string, decimal>(_multipliers, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<bool> RefreshAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_connectionString)) return false;
            int userId = TradingBot.AppConfig.CurrentUser?.Id ?? _userId;   // 현재 로그인 유저 fresh 조회
            if (userId <= 0) return false;
            try
            {
                const string sql = @"
SELECT Strategy, Regime,
       COUNT(*) AS N,
       SUM(CASE WHEN IsWin=1 THEN 1 ELSE 0 END) AS Wins,
       SUM(NetPnl) AS NetPnL
FROM dbo.StrategyRegimeOutcome WITH (NOLOCK)
WHERE UserId=@UserId
  AND ExitTime >= DATEADD(DAY,-30,SYSUTCDATETIME())
GROUP BY Strategy, Regime
HAVING COUNT(*) >= @MinN";

                await using var db = new SqlConnection(_connectionString);
                var rows = await db.QueryAsync(sql, new { UserId = userId, MinN = MinSamples });

                var newMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                int up = 0, down = 0;
                foreach (var r in rows)
                {
                    string strat = (string)r.Strategy;
                    string regime = (string)r.Regime;
                    int n = (int)r.N;
                    int wins = (int)r.Wins;
                    decimal pnl = (decimal)r.NetPnL;
                    decimal wr = n > 0 ? (decimal)wins / n * 100m : 0m;

                    decimal m;
                    if (wr <= 35m && pnl < 0m) { m = 0.5m; down++; }
                    else if (wr >= 65m && pnl >= 0m) { m = 1.5m; up++; }
                    else m = 1.0m;
                    newMap[Key(strat, regime)] = m;
                }

                lock (_lock)
                {
                    _multipliers.Clear();
                    foreach (var kv in newMap) _multipliers[kv.Key] = kv.Value;
                    LastFetchUtc = DateTime.UtcNow;
                }
                IsReady = true;
                OnLog?.Invoke($"📊 [StratRegime] 30d 갱신: {newMap.Count}셀 ({up} 부스트 1.5x, {down} 축소 0.5x, 나머지 1.0x)");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [StratRegime] 갱신 실패 (캐시 유지, IsReady={IsReady}): {ex.Message}");
                return false;
            }
        }
    }
}
