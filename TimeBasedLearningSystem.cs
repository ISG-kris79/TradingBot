using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static TradingBot.Services.UnifiedLogger;

namespace TradingBot.AI
{
    /// <summary>
    /// 시간대별 학습 시스템
    /// 미국 장(13:30~20:00 UTC) vs 아시아 장(00:00~08:00 UTC) 패턴 분리
    /// </summary>
    public class TimeBasedLearningSystem : IDisposable
    {
        private readonly Dictionary<TradingSession, AdaptiveOnlineLearningService> _sessionSpecialists = new();
        
        public enum TradingSession
        {
            Asian,      // 00:00~08:00 UTC (서울 9시~17시)
            European,   // 08:00~13:30 UTC (런던 9시~14:30)
            US,         // 13:30~20:00 UTC (뉴욕 8:30~15:00)
            AfterHours  // 20:00~00:00 UTC (시간외)
        }
        
        public TimeBasedLearningSystem()
        {
            InitializeSessionSpecialists();
        }
        
        private void InitializeSessionSpecialists()
        {
            // 아시아 장: 낮은 변동성, 횡보 많음
            var asianTrainer = new EntryTimingMLTrainer("Asian_Session.zip");
            var asianTransformer = new EntryTimingTransformerTrainer("Asian_Transformer.pt");
            _sessionSpecialists[TradingSession.Asian] = new AdaptiveOnlineLearningService(
                asianTrainer,
                asianTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 600,
                    MinSamplesForTraining = 120,
                    TriggerEveryNSamples = 60,
                    RetrainingIntervalHours = 2.0,  // 패턴 변화 느림
                    TransformerFastEpochs = 3
                });
            
            // 유럽 장: 중간 변동성
            var europeanTrainer = new EntryTimingMLTrainer("European_Session.zip");
            var europeanTransformer = new EntryTimingTransformerTrainer("European_Transformer.pt");
            _sessionSpecialists[TradingSession.European] = new AdaptiveOnlineLearningService(
                europeanTrainer,
                europeanTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 700,
                    MinSamplesForTraining = 140,
                    TriggerEveryNSamples = 70,
                    RetrainingIntervalHours = 1.5,
                    TransformerFastEpochs = 4
                });
            
            // 미국 장: 높은 변동성, 뉴스 영향 큼
            var usTrainer = new EntryTimingMLTrainer("US_Session.zip");
            var usTransformer = new EntryTimingTransformerTrainer("US_Transformer.pt");
            _sessionSpecialists[TradingSession.US] = new AdaptiveOnlineLearningService(
                usTrainer,
                usTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 800,
                    MinSamplesForTraining = 150,
                    TriggerEveryNSamples = 80,
                    RetrainingIntervalHours = 1.0,  // 빠른 적응
                    TransformerFastEpochs = 5
                });
            
            // 시간외: 낮은 거래량
            var afterHoursTrainer = new EntryTimingMLTrainer("AfterHours_Session.zip");
            var afterHoursTransformer = new EntryTimingTransformerTrainer("AfterHours_Transformer.pt");
            _sessionSpecialists[TradingSession.AfterHours] = new AdaptiveOnlineLearningService(
                afterHoursTrainer,
                afterHoursTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 500,
                    MinSamplesForTraining = 100,
                    TriggerEveryNSamples = 50,
                    RetrainingIntervalHours = 2.0,
                    TransformerFastEpochs = 3
                });
            
            // 로그 이벤트
            foreach (var kvp in _sessionSpecialists)
            {
                kvp.Value.OnLog += msg => Info(LogCategory.AI, $"[{kvp.Key}Session] {msg}");
            }
            
            Info(LogCategory.AI, "[TimeBasedLearning] 초기화 완료: 4개 세션 전문가");
        }
        
        /// <summary>
        /// 현재 시간의 세션 판별
        /// </summary>
        public static TradingSession GetCurrentSession(DateTime? utcTime = null)
        {
            var time = utcTime ?? DateTime.UtcNow;
            int hour = time.Hour;
            
            if (hour >= 0 && hour < 8)
                return TradingSession.Asian;
            else if (hour >= 8 && hour < 14) // 13:30 근사
                return TradingSession.European;
            else if (hour >= 14 && hour < 20)
                return TradingSession.US;
            else
                return TradingSession.AfterHours;
        }
        
        /// <summary>
        /// 세션별 특성 정보
        /// </summary>
        public static SessionCharacteristics GetSessionCharacteristics(TradingSession session)
        {
            return session switch
            {
                TradingSession.Asian => new SessionCharacteristics
                {
                    VolatilityLevel = VolatilityLevel.Low,
                    LiquidityLevel = LiquidityLevel.Medium,
                    TrendStrength = TrendStrength.Weak,
                    NewsImpact = NewsImpact.Low,
                    PreferredStrategy = "Sideways Trading, Range Bound",
                    TypicalSpread = "낮음 (0.02~0.05%)"
                },
                TradingSession.European => new SessionCharacteristics
                {
                    VolatilityLevel = VolatilityLevel.Medium,
                    LiquidityLevel = LiquidityLevel.High,
                    TrendStrength = TrendStrength.Medium,
                    NewsImpact = NewsImpact.Medium,
                    PreferredStrategy = "Breakout, Short-term Trend",
                    TypicalSpread = "중간 (0.03~0.08%)"
                },
                TradingSession.US => new SessionCharacteristics
                {
                    VolatilityLevel = VolatilityLevel.High,
                    LiquidityLevel = LiquidityLevel.VeryHigh,
                    TrendStrength = TrendStrength.Strong,
                    NewsImpact = NewsImpact.High,
                    PreferredStrategy = "Trend Following, News Trading",
                    TypicalSpread = "높음 (0.05~0.15%)"
                },
                TradingSession.AfterHours => new SessionCharacteristics
                {
                    VolatilityLevel = VolatilityLevel.Low,
                    LiquidityLevel = LiquidityLevel.Low,
                    TrendStrength = TrendStrength.Weak,
                    NewsImpact = NewsImpact.VeryLow,
                    PreferredStrategy = "Avoid Trading, Consolidation",
                    TypicalSpread = "매우 낮음 (0.01~0.03%)"
                },
                _ => throw new ArgumentException("Invalid session")
            };
        }
        
        /// <summary>
        /// 라벨링된 샘플 추가 (현재 세션에 맞춤)
        /// </summary>
        public async Task AddLabeledSampleAsync(MultiTimeframeEntryFeature sample, DateTime? timestamp = null)
        {
            var session = GetCurrentSession(timestamp);
            
            if (_sessionSpecialists.TryGetValue(session, out var specialist))
            {
                await specialist.AddLabeledSampleAsync(sample);
                Trace(LogCategory.AI, $"[{session}Session] 샘플 추가: 윈도우={specialist.WindowSize}");
            }
        }
        
        /// <summary>
        /// 세션별 Threshold 조회
        /// </summary>
        public (float mlThreshold, float tfThreshold) GetSessionThresholds(TradingSession? session = null)
        {
            var currentSession = session ?? GetCurrentSession();
            
            if (_sessionSpecialists.TryGetValue(currentSession, out var specialist))
            {
                return (specialist.CurrentMLThreshold, specialist.CurrentTFThreshold);
            }
            
            return (0.65f, 0.60f);  // 기본값
        }
        
        /// <summary>
        /// 세션 전환 알림 (UI용)
        /// </summary>
        public static event Action<TradingSession, SessionCharacteristics>? OnSessionChanged;
        
        private static TradingSession _lastSession = GetCurrentSession();
        
        /// <summary>
        /// 주기적 세션 체크 (1분마다 호출 권장)
        /// </summary>
        public static void CheckSessionTransition()
        {
            var currentSession = GetCurrentSession();
            
            if (currentSession != _lastSession)
            {
                _lastSession = currentSession;
                var chars = GetSessionCharacteristics(currentSession);
                
                Info(LogCategory.Trading, 
                    $"🌍 세션 전환: {currentSession} | 변동성={chars.VolatilityLevel}, 유동성={chars.LiquidityLevel}");
                
                OnSessionChanged?.Invoke(currentSession, chars);
            }
        }
        
        /// <summary>
        /// 모든 세션 성능 요약
        /// </summary>
        public Dictionary<TradingSession, (double accuracy, int windowSize)> GetPerformanceSummary()
        {
            return _sessionSpecialists.ToDictionary(
                kvp => kvp.Key,
                kvp => (kvp.Value.CurrentAccuracy, kvp.Value.WindowSize)
            );
        }
        
        public void Dispose()
        {
            foreach (var specialist in _sessionSpecialists.Values)
            {
                specialist?.Dispose();
            }
        }
    }
    
    /// <summary>
    /// 세션 특성
    /// </summary>
    public class SessionCharacteristics
    {
        public VolatilityLevel VolatilityLevel { get; set; }
        public LiquidityLevel LiquidityLevel { get; set; }
        public TrendStrength TrendStrength { get; set; }
        public NewsImpact NewsImpact { get; set; }
        public string PreferredStrategy { get; set; } = string.Empty;
        public string TypicalSpread { get; set; } = string.Empty;
    }
    
    public enum VolatilityLevel { VeryLow, Low, Medium, High, VeryHigh }
    public enum LiquidityLevel { VeryLow, Low, Medium, High, VeryHigh }
    public enum TrendStrength { VeryWeak, Weak, Medium, Strong, VeryStrong }
    public enum NewsImpact { VeryLow, Low, Medium, High, VeryHigh }
}
