using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static TradingBot.Services.UnifiedLogger;

namespace TradingBot.AI
{
    /// <summary>
    /// 강화학습(RL) 프레임워크
    /// PPO (Proximal Policy Optimization) 기반
    /// Reward = 실제 PnL × (1 - MDD penalty) × (1 + Sharpe bonus)
    /// </summary>
    public class ReinforcementLearningFramework
    {
        // RL 상태 공간
        public class RLState
        {
            // 시장 상태
            public float CurrentPrice { get; set; }
            public float[] PriceHistory { get; set; } = Array.Empty<float>();  // 최근 20개
            public float RSI { get; set; }
            public float MACD { get; set; }
            public float ATR { get; set; }
            public float VolumeRatio { get; set; }
            
            // 포지션 상태
            public bool HasPosition { get; set; }
            public float CurrentPnL { get; set; }
            public float CurrentDrawdown { get; set; }
            public int PositionHoldingBars { get; set; }
            
            // 계정 상태
            public float AccountEquity { get; set; }
            public float AvailableMargin { get; set; }
            public int ConsecutiveWins { get; set; }
            public int ConsecutiveLosses { get; set; }
            
            // 시간 컨텍스트
            public int HourOfDay { get; set; }
            public TimeBasedLearningSystem.TradingSession Session { get; set; }
            
            public float[] ToVector()
            {
                var vec = new List<float>
                {
                    // 정규화된 시장 상태 (0~1 범위)
                    (RSI - 30f) / 40f,  // RSI 30~70 → 0~1
                    (MACD + 100f) / 200f,  // MACD -100~100 → 0~1
                    ATR / CurrentPrice,  // ATR%
                    Math.Min(VolumeRatio / 5f, 1f),  // Volume 비율 (최대 5배)
                    
                    // 포지션 상태
                    HasPosition ? 1f : 0f,
                    Math.Max(Math.Min(CurrentPnL / 100f + 0.5f, 1f), 0f),  // PnL% -50~50 → 0~1
                    Math.Min(CurrentDrawdown / 50f, 1f),  // DD% 0~50 → 0~1
                    Math.Min(PositionHoldingBars / 100f, 1f),  // 보유 시간
                    
                    // 계정 상태
                    AvailableMargin / AccountEquity,  // 여유 마진 비율
                    Math.Min((float)ConsecutiveWins / 5f, 1f),  // 연승 (최대 5)
                    Math.Min((float)ConsecutiveLosses / 5f, 1f),  // 연패 (최대 5)
                    
                    // 시간 컨텍스트
                    HourOfDay / 24f,  // 0~1
                    (int)Session / 4f  // 0~1
                };
                
                // 가격 히스토리 추가
                vec.AddRange(PriceHistory.Take(20));
                
                return vec.ToArray();
            }
        }
        
        // RL 액션 공간
        public enum RLAction
        {
            Hold = 0,        // 대기
            LongEntry = 1,   // 롱 진입
            ShortEntry = 2,  // 숏 진입
            ClosePosition = 3,  // 포지션 청산
            AdjustLeverage = 4  // 레버리지 조정 (향후)
        }
        
        // 보상 함수
        public class RewardCalculator
        {
            private Queue<float> _recentPnLs = new(100);
            private float _peak = 0f;
            
            public float CalculateReward(
                RLAction action,
                RLState prevState,
                RLState currentState,
                float tradePnL)
            {
                float reward = 0f;
                
                // 1. 기본 PnL 보상
                reward += tradePnL;
                
                // 2. MDD 페널티 (드로다운 억제)
                float drawdownPenalty = currentState.CurrentDrawdown * -0.5f;  // DD 1% → -0.5 페널티
                reward += drawdownPenalty;
                
                // 3. Sharpe 보너스 (수익 안정성)
                if (_recentPnLs.Count >= 20)
                {
                    float avgPnL = _recentPnLs.Average();
                    float stdPnL = CalculateStdDev(_recentPnLs);
                    
                    if (stdPnL > 0)
                    {
                        float sharpe = avgPnL / stdPnL;
                        reward += sharpe * 0.1f;  // Sharpe 보너스
                    }
                }
                
                // 4. 연승/연패 보정
                if (currentState.ConsecutiveWins >= 3)
                {
                    reward += 0.5f;  // 연승 보너스
                }
                if (currentState.ConsecutiveLosses >= 3)
                {
                    reward -= 1.0f;  // 연패 페널티 (리스크 감소 유도)
                }
                
                // 5. 보유 시간 페널티 (장기 보유 억제)
                if (prevState.HasPosition && currentState.PositionHoldingBars > 50)
                {
                    reward -= 0.1f * (currentState.PositionHoldingBars - 50) / 50f;
                }
                
                // 6. 잘못된 액션 페널티
                if (action == RLAction.LongEntry && prevState.HasPosition)
                {
                    reward -= 5f;  // 이미 포지션 있는데 진입 시도
                }
                
                _recentPnLs.Enqueue(tradePnL);
                while (_recentPnLs.Count > 100)
                {
                    _recentPnLs.Dequeue();
                }
                
                return reward;
            }
            
            private float CalculateStdDev(IEnumerable<float> values)
            {
                float avg = values.Average();
                float sumSqDiffs = values.Sum(v => (v - avg) * (v - avg));
                return (float)Math.Sqrt(sumSqDiffs / values.Count());
            }
        }
        
        // PPO 에이전트 (플레이스홀더 - 실제 구현은 TorchSharp 필요)
        public class PPOAgent
        {
            private readonly int _stateDim;
            private readonly int _actionDim;
            
            public PPOAgent(int stateDim, int actionDim = 5)
            {
                _stateDim = stateDim;
                _actionDim = actionDim;
                
                Info(LogCategory.AI, $"[PPO] 에이전트 초기화: StateDim={stateDim}, ActionDim={actionDim}");
            }
            
            /// <summary>
            /// 액션 선택 (확률 분포)
            /// </summary>
            public (RLAction action, float[] actionProbs) SelectAction(RLState state)
            {
                // TODO: 실제 신경망 추론
                // 현재는 랜덤 플레이스홀더
                
                var probs = new float[_actionDim];
                var rand = new Random();
                
                // Hold 선호
                probs[0] = 0.6f;
                probs[1] = 0.15f;  // Long
                probs[2] = 0.15f;  // Short
                probs[3] = 0.08f;  // Close
                probs[4] = 0.02f;  // Adjust
                
                int actionIdx = SampleFromDistribution(probs);
                
                return ((RLAction)actionIdx, probs);
            }
            
            private int SampleFromDistribution(float[] probs)
            {
                float sum = probs.Sum();
                float rand = (float)new Random().NextDouble() * sum;
                
                float cumulative = 0f;
                for (int i = 0; i < probs.Length; i++)
                {
                    cumulative += probs[i];
                    if (rand < cumulative)
                        return i;
                }
                
                return 0;
            }
            
            /// <summary>
            /// 학습 (Experience Replay)
            /// </summary>
            public async Task<bool> TrainAsync(List<RLExperience> experiences)
            {
                // TODO: PPO 학습 로직
                // - Advantage 계산
                // - Policy Gradient 업데이트
                // - Value Function 학습
                
                await Task.Delay(100);  // 플레이스홀더
                
                Info(LogCategory.AI, $"[PPO] 학습 완료: {experiences.Count}개 경험");
                return true;
            }
        }
        
        // RL 경험 (Experience Replay용)
        public class RLExperience
        {
            public RLState State { get; set; } = new();
            public RLAction Action { get; set; }
            public float Reward { get; set; }
            public RLState NextState { get; set; } = new();
            public bool Done { get; set; }  // Episode 종료
        }
        
        // RL 통합 관리자
        public class RLIntegrationManager
        {
            private readonly PPOAgent _agent;
            private readonly RewardCalculator _rewardCalc = new();
            private readonly List<RLExperience> _replayBuffer = new();
            private const int BufferSize = 10000;
            
            public RLIntegrationManager()
            {
                // 상태 벡터 크기 = 13 (기본) + 20 (가격 히스토리)
                _agent = new PPOAgent(stateDim: 33, actionDim: 5);
            }
            
            /// <summary>
            /// 액션 추천
            /// </summary>
            public (RLAction action, float confidence) RecommendAction(RLState currentState)
            {
                var (action, probs) = _agent.SelectAction(currentState);
                float confidence = probs[(int)action];
                
                return (action, confidence);
            }
            
            /// <summary>
            /// 경험 기록
            /// </summary>
            public void RecordExperience(
                RLState state,
                RLAction action,
                RLState nextState,
                float tradePnL,
                bool done)
            {
                float reward = _rewardCalc.CalculateReward(action, state, nextState, tradePnL);
                
                var exp = new RLExperience
                {
                    State = state,
                    Action = action,
                    Reward = reward,
                    NextState = nextState,
                    Done = done
                };
                
                _replayBuffer.Add(exp);
                
                // 버퍼 크기 제한
                if (_replayBuffer.Count > BufferSize)
                {
                    _replayBuffer.RemoveAt(0);
                }
                
                // 일정 경험 누적 시 학습
                if (_replayBuffer.Count % 100 == 0)
                {
                    _ = Task.Run(async () => await _agent.TrainAsync(_replayBuffer.TakeLast(100).ToList()));
                }
            }
        }
    }
}
