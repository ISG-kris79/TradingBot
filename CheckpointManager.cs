using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Services;
using static TradingBot.Services.UnifiedLogger;

namespace TradingBot.AI
{
    /// <summary>
    /// AI 모델 체크포인트 관리자
    /// 서버 재시작 시에도 학습 상태 유지
    /// </summary>
    public class CheckpointManager
    {
        private readonly string _checkpointDir;
        private readonly Timer _autoSaveTimer;
        private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);
        
        public CheckpointManager(string checkpointDir = "Checkpoints")
        {
            _checkpointDir = checkpointDir;
            
            // 디렉터리 생성
            if (!Directory.Exists(_checkpointDir))
            {
                Directory.CreateDirectory(_checkpointDir);
                Info(LogCategory.AI, $"[Checkpoint] 디렉터리 생성: {_checkpointDir}");
            }
            
            // 자동 저장 타이머 (1시간마다)
            _autoSaveTimer = new Timer(
                callback: async _ => await AutoSaveAsync(),
                state: null,
                dueTime: TimeSpan.FromHours(1),
                period: TimeSpan.FromHours(1));
            
            Info(LogCategory.System, "[Checkpoint] 자동 저장 활성화 (1시간 간격)");
        }
        
        /// <summary>
        /// 코인 전문가 체크포인트 저장
        /// </summary>
        public async Task SaveCoinSpecialistAsync(
            CoinSpecialistLearningSystem.CoinGroup group,
            CoinSpecialistCheckpoint checkpoint)
        {
            await _saveLock.WaitAsync();
            try
            {
                string fileName = Path.Combine(_checkpointDir, $"{group}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                
                var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                await File.WriteAllTextAsync(fileName, json);
                Info(LogCategory.AI, $"[Checkpoint] {group} 저장 완료: {Path.GetFileName(fileName)}");
                
                // 오래된 체크포인트 정리 (최근 5개만 유지)
                CleanOldCheckpoints(group.ToString());
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, $"[Checkpoint] {group} 저장 실패", ex);
            }
            finally
            {
                _saveLock.Release();
            }
        }
        
        /// <summary>
        /// 코인 전문가 체크포인트 복원
        /// </summary>
        public async Task<CoinSpecialistCheckpoint?> RestoreCoinSpecialistAsync(
            CoinSpecialistLearningSystem.CoinGroup group)
        {
            try
            {
                // 최신 체크포인트 찾기
                var files = Directory.GetFiles(_checkpointDir, $"{group}_*.json")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .ToList();
                
                if (files.Count == 0)
                {
                    Info(LogCategory.AI, $"[Checkpoint] {group} 복원 대상 없음 (신규 시작)");
                    return null;
                }
                
                string latestFile = files.First();
                string json = await File.ReadAllTextAsync(latestFile);
                
                var checkpoint = JsonSerializer.Deserialize<CoinSpecialistCheckpoint>(json);
                
                Info(LogCategory.AI, $"[Checkpoint] {group} 복원 완료: {Path.GetFileName(latestFile)}");
                return checkpoint;
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, $"[Checkpoint] {group} 복원 실패", ex);
                return null;
            }
        }
        
        /// <summary>
        /// 시간대별 학습 체크포인트 저장
        /// </summary>
        public async Task SaveSessionSpecialistAsync(
            TimeBasedLearningSystem.TradingSession session,
            SessionCheckpoint checkpoint)
        {
            await _saveLock.WaitAsync();
            try
            {
                string fileName = Path.Combine(_checkpointDir, $"Session_{session}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                
                var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                await File.WriteAllTextAsync(fileName, json);
                Info(LogCategory.AI, $"[Checkpoint] {session} 세션 저장 완료");
                
                CleanOldCheckpoints($"Session_{session}");
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, $"[Checkpoint] {session} 세션 저장 실패", ex);
            }
            finally
            {
                _saveLock.Release();
            }
        }
        
        /// <summary>
        /// 시간대별 학습 체크포인트 복원
        /// </summary>
        public async Task<SessionCheckpoint?> RestoreSessionSpecialistAsync(
            TimeBasedLearningSystem.TradingSession session)
        {
            try
            {
                var files = Directory.GetFiles(_checkpointDir, $"Session_{session}_*.json")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .ToList();
                
                if (files.Count == 0)
                {
                    return null;
                }
                
                string latestFile = files.First();
                string json = await File.ReadAllTextAsync(latestFile);
                
                var checkpoint = JsonSerializer.Deserialize<SessionCheckpoint>(json);
                
                Info(LogCategory.AI, $"[Checkpoint] {session} 세션 복원 완료");
                return checkpoint;
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, $"[Checkpoint] {session} 세션 복원 실패", ex);
                return null;
            }
        }
        
        /// <summary>
        /// RL 에이전트 체크포인트 저장
        /// </summary>
        public async Task SaveRLAgentAsync(RLCheckpoint checkpoint)
        {
            await _saveLock.WaitAsync();
            try
            {
                string fileName = Path.Combine(_checkpointDir, $"RL_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                
                var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                await File.WriteAllTextAsync(fileName, json);
                Info(LogCategory.AI, $"[Checkpoint] RL 에이전트 저장 완료 (에피소드 {checkpoint.EpisodeCount})");
                
                CleanOldCheckpoints("RL_");
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, "[Checkpoint] RL 에이전트 저장 실패", ex);
            }
            finally
            {
                _saveLock.Release();
            }
        }
        
        /// <summary>
        /// RL 에이전트 체크포인트 복원
        /// </summary>
        public async Task<RLCheckpoint?> RestoreRLAgentAsync()
        {
            try
            {
                var files = Directory.GetFiles(_checkpointDir, "RL_*.json")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .ToList();
                
                if (files.Count == 0)
                {
                    return null;
                }
                
                string latestFile = files.First();
                string json = await File.ReadAllTextAsync(latestFile);
                
                var checkpoint = JsonSerializer.Deserialize<RLCheckpoint>(json);
                
                Info(LogCategory.AI, $"[Checkpoint] RL 에이전트 복원 완료 (에피소드 {checkpoint?.EpisodeCount})");
                return checkpoint;
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, "[Checkpoint] RL 에이전트 복원 실패", ex);
                return null;
            }
        }
        
        /// <summary>
        /// 오래된 체크포인트 정리 (최근 5개만 유지)
        /// </summary>
        private void CleanOldCheckpoints(string prefix)
        {
            try
            {
                var files = Directory.GetFiles(_checkpointDir, $"{prefix}*.json")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .ToList();
                
                if (files.Count > 5)
                {
                    foreach (var oldFile in files.Skip(5))
                    {
                        File.Delete(oldFile);
                        Trace(LogCategory.AI, $"[Checkpoint] 오래된 파일 삭제: {Path.GetFileName(oldFile)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Warn(LogCategory.AI, $"[Checkpoint] 정리 실패: {prefix} - {ex.Message}");
            }
        }
        
        /// <summary>
        /// 자동 저장 (주기적 호출)
        /// </summary>
        private async Task AutoSaveAsync()
        {
            Info(LogCategory.AI, "[Checkpoint] 자동 저장 시작...");
            
            // 실제 저장 로직은 CoinSpecialistLearningSystem/TimeBasedLearningSystem에서 호출해야 함
            // 여기서는 트리거만 제공
            OnAutoSaveRequested?.Invoke(this, EventArgs.Empty);
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// 자동 저장 요청 이벤트
        /// </summary>
        public event EventHandler? OnAutoSaveRequested;
        
        /// <summary>
        /// 즉시 저장 (수동 호출)
        /// </summary>
        public void TriggerManualSave()
        {
            Info(LogCategory.AI, "[Checkpoint] 수동 저장 트리거");
            OnAutoSaveRequested?.Invoke(this, EventArgs.Empty);
        }
        
        /// <summary>
        /// 정리 (종료 시 호출)
        /// </summary>
        public void Dispose()
        {
            _autoSaveTimer?.Dispose();
            _saveLock?.Dispose();
        }
    }
    
    #region Checkpoint 데이터 구조
    
    /// <summary>
    /// 코인 전문가 체크포인트
    /// </summary>
    public class CoinSpecialistCheckpoint
    {
        public string CoinGroup { get; set; } = "";
        public DateTime SavedAt { get; set; }
        
        // 학습 상태
        public double CurrentAccuracy { get; set; }
        public double BaselineAccuracy { get; set; }
        public int TotalSampleCount { get; set; }
        public int WindowSize { get; set; }
        
        // 최근 예측 기록 (최대 100개)
        public List<PredictionRecord> RecentPredictions { get; set; } = new();
        
        // 통계
        public double WinRate { get; set; }
        public double AvgProfitPercent { get; set; }
        public DateTime LastRetrainingTime { get; set; }
    }
    
    /// <summary>
    /// 시간대별 학습 체크포인트
    /// </summary>
    public class SessionCheckpoint
    {
        public string Session { get; set; } = "";
        public DateTime SavedAt { get; set; }
        
        public double CurrentAccuracy { get; set; }
        public int TotalSampleCount { get; set; }
        public int WindowSize { get; set; }
        
        public List<PredictionRecord> RecentPredictions { get; set; } = new();
        
        public double SessionWinRate { get; set; }
        public DateTime LastRetrainingTime { get; set; }
    }
    
    /// <summary>
    /// RL 에이전트 체크포인트
    /// </summary>
    public class RLCheckpoint
    {
        public DateTime SavedAt { get; set; }
        
        // 학습 진행도
        public int EpisodeCount { get; set; }
        public int TotalSteps { get; set; }
        
        // 성능 메트릭
        public double AverageReward { get; set; }
        public double BestReward { get; set; }
        public double CurrentSharpeRatio { get; set; }
        public double MaxDrawdownPercent { get; set; }
        
        // 경험 재생 버퍼 (최근 1000개만 저장)
        public List<ExperienceSummary> RecentExperiences { get; set; } = new();
        
        // 모델 가중치 파일 경로 (TorchSharp .pt 파일)
        public string? ModelWeightsPath { get; set; }
    }
    
    /// <summary>
    /// 예측 기록
    /// </summary>
    public class PredictionRecord
    {
        public DateTime Timestamp { get; set; }
        public string Symbol { get; set; } = "";
        public float PredictedScore { get; set; }
        public bool ActualOutcome { get; set; }  // 실제 수익 여부
        public double ProfitPercent { get; set; }
    }
    
    /// <summary>
    /// 경험 요약 (전체 RLExperience는 너무 크므로)
    /// </summary>
    public class ExperienceSummary
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        public float Reward { get; set; }
        public bool Done { get; set; }
    }
    
    #endregion
}
