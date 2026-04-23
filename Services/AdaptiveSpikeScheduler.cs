using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.14.0 AI #5] SPIKE_FAST 진입 타이밍 적응형 스케줄러 — 경량 강화학습 (Nearest-Neighbor Bandit)
    ///
    /// 설계 철학:
    ///   Deep RL / Q-Network 대신 tabular bandit. 이유:
    ///    1) 학습 안정성: 심볼당 데이터 몇십건 수준이라 Deep RL 수렴 어려움
    ///    2) 해석 가능성: "왜 wait 했나" 를 bucket hit-rate 로 즉시 설명 가능
    ///    3) Fail-safe: 데이터 부족 시 rule-based 로 자동 폴백
    ///
    /// 상태 공간 (State):
    ///   - cumGainPct:   진입 감지 이후 누적 상승률 (0~5% 범위, 0.5% bucket)
    ///   - pullbackPct:  고점 대비 현재 하락률 (0~2% 범위, 0.25% bucket)
    ///   - elapsedSec:   감지 이후 경과 초 (0~30s, 5s bucket)
    ///
    /// 액션 (Action):
    ///   - Wait:   계속 관찰
    ///   - Enter:  즉시 진입
    ///   - Cancel: 진입 포기 (30초 만료 또는 unfavorable)
    ///
    /// 보상 (Reward):
    ///   - 진입 후 5분 PnL% (레버리지 미적용 기준)
    ///   - Cancel 의 보상은 0 (기회비용 무시, 손실 회피 우선)
    /// </summary>
    public class AdaptiveSpikeScheduler
    {
        public enum SpikeAction { Wait, Enter, Cancel }

        public record SpikeState(int CumGainBucket, int PullbackBucket, int ElapsedBucket)
        {
            public string Key => $"{CumGainBucket}:{PullbackBucket}:{ElapsedBucket}";
        }

        private record BucketStat
        {
            public int EnterCount { get; set; }
            public int WinCount { get; set; }
            public double SumPnL { get; set; }
            public double WinRate => EnterCount > 0 ? (double)WinCount / EnterCount : 0.0;
            public double AvgPnL => EnterCount > 0 ? SumPnL / EnterCount : 0.0;
        }

        private readonly ConcurrentDictionary<string, BucketStat> _stats = new();
        private readonly string _dataPath;
        private readonly object _saveLock = new();
        private DateTime _lastSaveTime = DateTime.MinValue;

        // 탐색/활용 비율 (epsilon-greedy). 0.10 = 10% 무작위 탐색
        public double ExplorationRate { get; set; } = 0.10;

        // 최소 샘플 수 (이 미만이면 rule-based 폴백)
        public int MinSamplesPerBucket { get; set; } = 10;

        // 진입 승인 최소 승률
        public double MinWinRateToEnter { get; set; } = 0.55;

        // 취소 결정 최대 승률 (이하 + 20초 경과 시 포기)
        public double MaxWinRateToCancel { get; set; } = 0.35;

        public event Action<string>? OnLog;

        public AdaptiveSpikeScheduler(string? dataPath = null)
        {
            _dataPath = dataPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TradingBot", "Models", "spike_scheduler_stats.json");

            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            Load();
        }

        public static SpikeState Discretize(double cumGainPct, double pullbackPct, double elapsedSec)
        {
            int cumBucket = Math.Max(0, Math.Min(10, (int)Math.Floor(cumGainPct / 0.5)));
            int pullBucket = Math.Max(0, Math.Min(8, (int)Math.Floor(Math.Abs(pullbackPct) / 0.25)));
            int elapsedBucket = Math.Max(0, Math.Min(6, (int)Math.Floor(elapsedSec / 5.0)));
            return new SpikeState(cumBucket, pullBucket, elapsedBucket);
        }

        /// <summary>
        /// 현재 상태에서 다음 액션 결정.
        /// Fail-safe: 샘플 부족 시 rule-based 로 폴백 (enter).
        /// </summary>
        public (SpikeAction action, double expectedPnL, string reason) Decide(SpikeState state, double elapsedSec)
        {
            // Epsilon-greedy 탐색
            var rng = new Random();
            if (rng.NextDouble() < ExplorationRate)
            {
                return (SpikeAction.Enter, 0.0, "explore_random");
            }

            var stat = _stats.GetValueOrDefault(state.Key);
            if (stat == null || stat.EnterCount < MinSamplesPerBucket)
            {
                // 샘플 부족 → rule-based 폴백 (기존 30초 window 로직)
                return (SpikeAction.Enter, 0.0, $"insufficient_samples_{stat?.EnterCount ?? 0}");
            }

            double winRate = stat.WinRate;
            double avgPnL = stat.AvgPnL;

            // 승률 55%+ 이면 즉시 진입
            if (winRate >= MinWinRateToEnter)
            {
                return (SpikeAction.Enter, avgPnL, $"exploit_win={winRate:P0}_pnl={avgPnL:F2}%");
            }

            // 승률 35% 이하 + 20초 경과 → 취소
            if (winRate <= MaxWinRateToCancel && elapsedSec >= 20.0)
            {
                return (SpikeAction.Cancel, avgPnL, $"exploit_low_win={winRate:P0}_late");
            }

            // 애매 구간 → 계속 대기
            return (SpikeAction.Wait, avgPnL, $"wait_win={winRate:P0}");
        }

        /// <summary>
        /// 실제 진입이 이루어진 상태 + 5분 후 PnL 로 학습.
        /// </summary>
        public void RecordOutcome(SpikeState stateAtEntry, double pnlPct5min)
        {
            _stats.AddOrUpdate(stateAtEntry.Key,
                _ => new BucketStat
                {
                    EnterCount = 1,
                    WinCount = pnlPct5min > 0 ? 1 : 0,
                    SumPnL = pnlPct5min
                },
                (_, existing) =>
                {
                    existing.EnterCount++;
                    if (pnlPct5min > 0) existing.WinCount++;
                    existing.SumPnL += pnlPct5min;
                    return existing;
                });

            OnLog?.Invoke($"[SpikeScheduler] 학습 state={stateAtEntry.Key} pnl={pnlPct5min:+0.00;-0.00;0.00}% n={_stats[stateAtEntry.Key].EnterCount} win={_stats[stateAtEntry.Key].WinRate:P0}");

            // 60초 디바운스 저장
            if ((DateTime.Now - _lastSaveTime).TotalSeconds >= 60)
            {
                Save();
            }
        }

        public void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    var snapshot = _stats.ToDictionary(kv => kv.Key, kv => new
                    {
                        kv.Value.EnterCount,
                        kv.Value.WinCount,
                        kv.Value.SumPnL
                    });
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
                    File.WriteAllText(_dataPath, json);
                    _lastSaveTime = DateTime.Now;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [SpikeScheduler] 저장 실패: {ex.Message}");
                }
            }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_dataPath)) return;
                var json = File.ReadAllText(_dataPath);
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (raw == null) return;

                foreach (var (key, el) in raw)
                {
                    _stats[key] = new BucketStat
                    {
                        EnterCount = el.GetProperty("EnterCount").GetInt32(),
                        WinCount = el.GetProperty("WinCount").GetInt32(),
                        SumPnL = el.GetProperty("SumPnL").GetDouble()
                    };
                }
                OnLog?.Invoke($"[SpikeScheduler] 히스토리 로드: {_stats.Count} buckets");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [SpikeScheduler] 로드 실패: {ex.Message}");
            }
        }

        public int TotalBuckets => _stats.Count;
        public int TotalSamples => _stats.Values.Sum(s => s.EnterCount);
    }
}
