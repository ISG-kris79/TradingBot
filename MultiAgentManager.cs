using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TradingBot.Services.AI.RL
{
    public class MultiAgentManager
    {
        public event Action<string, float, float>? OnAgentTrainingStats;

        private readonly Dictionary<string, PPOAgent> _agents;
        private readonly bool _torchAvailable;

        public bool IsTorchAvailable => _torchAvailable;

        public MultiAgentManager(int stateDim, int actionDim)
        {
            _agents = new Dictionary<string, PPOAgent>();

            bool torchFeaturesEnabled = AppConfig.Current?.Trading?.TransformerSettings?.Enabled ?? false;
            if (!torchFeaturesEnabled)
            {
                _torchAvailable = false;
                Debug.WriteLine("[MultiAgentManager] Torch/Transformer 설정 비활성화 - PPO 에이전트 비활성화");
                return;
            }

            // TorchSharp 사용 가능 여부 확인
            _torchAvailable = Services.TorchInitializer.IsAvailable;
            
            if (_torchAvailable)
            {
                try
                {
                    _agents.Add("Scalping", new PPOAgent(stateDim, actionDim, lr: 0.0005, gamma: 0.95));
                    _agents.Add("Swing", new PPOAgent(stateDim, actionDim, lr: 0.0003, gamma: 0.99));
                    Debug.WriteLine("[MultiAgentManager] PPO 에이전트 초기화 성공");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MultiAgentManager] PPO 에이전트 생성 실패: {ex.Message}");
                    _agents.Clear();
                    _torchAvailable = false;
                }
            }
            else
            {
                Debug.WriteLine("[MultiAgentManager] TorchSharp 사용 불가 - PPO 에이전트 비활성화");
            }
        }

        public int GetAction(string strategyType, float[] state)
        {
            if (!_torchAvailable)
            {
                Debug.WriteLine("[MultiAgentManager] TorchSharp 비활성화 상태 - 기본 행동 반환");
                return 0; // Hold
            }
            
            if (_agents.TryGetValue(strategyType, out var agent))
            {
                return agent.GetAction(state);
            }
            return 0; // Default: Hold
        }

        public void UpdateAgent(string strategyType, List<float[]> states, List<int> actions, List<float> rewards, List<float> nextStates, List<bool> dones)
        {
            if (!_torchAvailable)
                return;
            
            if (_agents.TryGetValue(strategyType, out var agent))
            {
                var (loss, avgReward) = agent.Update(states, actions, rewards, nextStates, dones);
                OnAgentTrainingStats?.Invoke(strategyType, loss, avgReward);
            }
        }

        public PPOAgent? GetAgent(string strategyType)
        {
            if (!_torchAvailable)
                return null;
            
            if (_agents.TryGetValue(strategyType, out var agent))
                return agent;
            return null;
        }
    }
}