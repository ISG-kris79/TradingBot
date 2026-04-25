using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.18.0] AI 모델 zip 파일 주기 헬스체크 + 자동 reload
    ///
    /// 검증 항목:
    ///   1) 파일 존재
    ///   2) 파일 크기 > MinHealthyBytes
    ///   3) 마지막 수정시각 < StaleThresholdHours 시간 전
    ///   4) 변경 감지 시 자동 reload (mtime 변경된 모델 재로드)
    ///
    /// 실행 주기:
    ///   - File 존재/크기 체크: 1분마다
    ///   - 마지막 수정 모니터링 (학습 stuck 감지): 매번 함께
    ///   - 자동 reload (mtime 변경): 5분마다
    ///
    /// 결과:
    ///   - 이상 감지 시 OnAlert 호출 (Telegram + UI)
    ///   - 자동 reload 후 OnLog
    /// </summary>
    public class ModelHealthMonitor : IDisposable
    {
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;

        private readonly Dictionary<string, ModelEntry> _models = new();
        private readonly object _lock = new();
        private Timer? _checkTimer;
        private Timer? _reloadTimer;
        private bool _disposed;

        public long MinHealthyBytes { get; set; } = 30 * 1024;       // 30KB 미만이면 손상 의심
        public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromHours(6);  // 6시간 학습 안 됨 = 의심
        // [v5.19.1] grace period — 등록 직후 학습 중일 가능성 높으므로 알람 억제
        public TimeSpan InitialGracePeriod { get; set; } = TimeSpan.FromMinutes(30);
        // [v5.19.1] 동일 variant + 동일 사유 알람 cooldown — 분당 4건 폭주 방지
        public TimeSpan AlertCooldown { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>학습 모델 등록 (variant 별로 한 번씩 호출)</summary>
        public void Register(string variantTag, string zipPath, Action reloadCallback)
        {
            lock (_lock)
            {
                _models[variantTag] = new ModelEntry
                {
                    Tag = variantTag,
                    Path = zipPath,
                    Reload = reloadCallback,
                    LastSeenWriteTime = File.Exists(zipPath) ? new FileInfo(zipPath).LastWriteTimeUtc : DateTime.MinValue,
                    RegisteredAt = DateTime.UtcNow
                };
            }
            OnLog?.Invoke($"📋 [HEALTH] {variantTag} 등록: {zipPath}");
        }

        public void Start()
        {
            if (_checkTimer != null) return;
            // 1분마다 헬스체크
            _checkTimer = new Timer(_ => SafeCheck(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
            // 5분마다 변경 감지 + reload
            _reloadTimer = new Timer(_ => SafeReload(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
            OnLog?.Invoke($"🩺 [HEALTH] ModelHealthMonitor 시작 (1분 체크 / 5분 reload, threshold={StaleThreshold.TotalHours:F0}h)");
        }

        private void SafeCheck()
        {
            try { CheckAll(); }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ [HEALTH] check 예외: {ex.Message}"); }
        }

        private void SafeReload()
        {
            try { ReloadIfChanged(); }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ [HEALTH] reload 예외: {ex.Message}"); }
        }

        private void CheckAll()
        {
            ModelEntry[] snapshot;
            lock (_lock) snapshot = _models.Values.ToArray();
            DateTime nowUtc = DateTime.UtcNow;
            foreach (var m in snapshot)
            {
                // [v5.19.1] 등록 직후 grace period 내 알람 억제 (학습 중일 가능성)
                bool inGrace = (nowUtc - m.RegisteredAt) < InitialGracePeriod;

                if (!File.Exists(m.Path))
                {
                    if (inGrace)
                    {
                        // grace 중에는 1회만 info 로그
                        if (!m.GraceMissingLogged)
                        {
                            m.GraceMissingLogged = true;
                            OnLog?.Invoke($"⏳ [HEALTH][{m.Tag}] zip 미존재 — grace {InitialGracePeriod.TotalMinutes:F0}분 학습 대기");
                        }
                        continue;
                    }
                    if (TryAlert(m, "MISSING", nowUtc))
                        OnAlert?.Invoke($"🚨 [HEALTH][{m.Tag}] zip 파일 없음! {m.Path}");
                    continue;
                }
                var fi = new FileInfo(m.Path);
                if (fi.Length < MinHealthyBytes)
                {
                    if (TryAlert(m, "CORRUPT", nowUtc))
                        OnAlert?.Invoke($"🚨 [HEALTH][{m.Tag}] zip 파일 손상 의심! 크기 {fi.Length}B < {MinHealthyBytes}B");
                    continue;
                }
                var age = nowUtc - fi.LastWriteTimeUtc;
                if (age > StaleThreshold)
                {
                    if (TryAlert(m, "STALE", nowUtc))
                        OnAlert?.Invoke($"⚠️ [HEALTH][{m.Tag}] 학습 stale: 마지막 갱신 {age.TotalHours:F1}h 전 (threshold {StaleThreshold.TotalHours:F0}h)");
                }
            }
        }

        /// <summary>[v5.19.1] 동일 variant + 동일 사유 알람 cooldown 체크</summary>
        private bool TryAlert(ModelEntry m, string reason, DateTime nowUtc)
        {
            if (m.LastAlerts.TryGetValue(reason, out var lastAt) && (nowUtc - lastAt) < AlertCooldown)
                return false;
            m.LastAlerts[reason] = nowUtc;
            return true;
        }

        private void ReloadIfChanged()
        {
            ModelEntry[] snapshot;
            lock (_lock) snapshot = _models.Values.ToArray();
            foreach (var m in snapshot)
            {
                if (!File.Exists(m.Path)) continue;
                var fi = new FileInfo(m.Path);
                if (fi.LastWriteTimeUtc > m.LastSeenWriteTime)
                {
                    var prevTime = m.LastSeenWriteTime;
                    m.LastSeenWriteTime = fi.LastWriteTimeUtc;
                    try
                    {
                        m.Reload?.Invoke();
                        OnLog?.Invoke($"🔁 [HEALTH][{m.Tag}] zip 갱신 감지 → reload 완료 (이전={prevTime:HH:mm:ss}, 신규={fi.LastWriteTimeUtc:HH:mm:ss})");
                    }
                    catch (Exception ex)
                    {
                        if (TryAlert(m, "RELOAD_FAIL", DateTime.UtcNow))
                            OnAlert?.Invoke($"🚨 [HEALTH][{m.Tag}] reload 실패: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>현재 모든 모델 상태 보고 (UI/외부 호출용)</summary>
        public List<ModelStatus> GetStatusSnapshot()
        {
            var result = new List<ModelStatus>();
            ModelEntry[] snapshot;
            lock (_lock) snapshot = _models.Values.ToArray();
            foreach (var m in snapshot)
            {
                bool exists = File.Exists(m.Path);
                long size = exists ? new FileInfo(m.Path).Length : 0;
                DateTime? lastWrite = exists ? new FileInfo(m.Path).LastWriteTimeUtc : (DateTime?)null;
                result.Add(new ModelStatus
                {
                    Tag = m.Tag,
                    Path = m.Path,
                    Exists = exists,
                    SizeBytes = size,
                    LastWriteUtc = lastWrite,
                    StaleHours = lastWrite.HasValue ? (DateTime.UtcNow - lastWrite.Value).TotalHours : -1
                });
            }
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _checkTimer?.Dispose();
            _reloadTimer?.Dispose();
        }

        private class ModelEntry
        {
            public string Tag = "";
            public string Path = "";
            public Action? Reload;
            public DateTime LastSeenWriteTime;
            // [v5.19.1] grace + alert cooldown
            public DateTime RegisteredAt;
            public bool GraceMissingLogged;
            public Dictionary<string, DateTime> LastAlerts = new();
        }

        public class ModelStatus
        {
            public string Tag { get; set; } = "";
            public string Path { get; set; } = "";
            public bool Exists { get; set; }
            public long SizeBytes { get; set; }
            public DateTime? LastWriteUtc { get; set; }
            public double StaleHours { get; set; }
        }
    }
}
