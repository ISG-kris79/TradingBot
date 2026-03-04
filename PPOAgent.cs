using System;
using System.Collections.Generic;
using System.Linq;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;
using static TorchSharp.torch.optim;

namespace TradingBot.Services.AI.RL
{
    /// <summary>
    /// PPO (Proximal Policy Optimization) 에이전트
    /// Actor-Critic 구조를 사용하여 안정적인 정책 학습을 수행합니다.
    /// </summary>
    public class PPOAgent
    {
        private readonly ActorCritic _model;
        private readonly Optimizer _optimizer;
        private readonly Device _device;
        
        private readonly double _gamma; // 할인율
        private readonly double _epsClip; // PPO 클리핑 파라미터
        private readonly int _kEpochs; // 업데이트 시 반복 횟수
        private readonly List<float> _rewardHistory = new List<float>();

        public IReadOnlyList<float> RewardHistory => _rewardHistory;

        public PPOAgent(int stateDim, int actionDim, double lr = 0.0003, double gamma = 0.99, double epsClip = 0.2, int kEpochs = 4)
        {
            _gamma = gamma;
            _epsClip = epsClip;
            _kEpochs = kEpochs;
            _device = torch.cuda.is_available() ? torch.CUDA : torch.CPU;

            _model = new ActorCritic(stateDim, actionDim);
            _model.to(_device);
            _optimizer = Adam(_model.parameters(), lr);
        }

        public int GetAction(float[] state)
        {
            using var stateTensor = torch.tensor(state, device: _device).unsqueeze(0);
            var output = _model.forward(stateTensor);
            
            // Actor의 출력(확률)을 기반으로 행동 샘플링
            var actionProbs = output.actionProbs;
            var dist = torch.distributions.Categorical(actionProbs);
            var action = dist.sample();
            
            return (int)action.item<long>();
        }

        public (float loss, float avgReward) Update(List<float[]> states, List<int> actions, List<float> rewards, List<float> nextStates, List<bool> dones)
        {
            // 데이터 텐서 변환
            var tStates = torch.tensor(states.SelectMany(x => x).ToArray(), new long[] { states.Count, states[0].Length }, device: _device);
            var tActions = torch.tensor(actions.ToArray(), device: _device).unsqueeze(1);
            var tRewards = torch.tensor(rewards.ToArray(), device: _device).unsqueeze(1);

            // Monte Carlo Estimate of State Rewards (간소화된 버전)
            // 실제 구현에서는 GAE (Generalized Advantage Estimation)를 사용하는 것이 좋음
            var discountedRewards = new List<float>();
            float runningAdd = 0;
            for (int i = rewards.Count - 1; i >= 0; i--)
            {
                runningAdd = runningAdd * (float)_gamma + rewards[i];
                discountedRewards.Insert(0, runningAdd);
            }
            var tDiscountedRewards = torch.tensor(discountedRewards.ToArray(), device: _device).unsqueeze(1);

            // 정규화
            tDiscountedRewards = (tDiscountedRewards - tDiscountedRewards.mean()) / (tDiscountedRewards.std() + 1e-5f);

            float lastLoss = 0f;
            float avgReward = rewards.Count > 0 ? rewards.Average() : 0f;

            // PPO Update Loop
            for (int i = 0; i < _kEpochs; i++)
            {
                var output = _model.forward(tStates);
                var actionProbs = output.actionProbs;
                var stateValues = output.stateValues;

                var dist = torch.distributions.Categorical(actionProbs);
                var logProbs = dist.log_prob(tActions.squeeze());
                var distEntropy = dist.entropy();

                // Advantage 계산
                var advantages = tDiscountedRewards - stateValues.detach();

                // PPO Loss (여기서는 단순화하여 Policy Gradient 형태만 예시로 구현)
                // 실제 PPO는 OldPolicy와의 Ratio 계산 필요 (메모리 관리상 생략된 부분 보강 필요)

                // Critic Loss (MSE)
                var criticLoss = functional.mse_loss(stateValues, tDiscountedRewards);

                // Actor Loss (간소화)
                var actorLoss = -(logProbs * advantages).mean();

                var loss = actorLoss + 0.5f * criticLoss - 0.01f * distEntropy.mean();
                lastLoss = loss.item<float>();

                _optimizer.zero_grad();
                loss.backward();
                _optimizer.step();
            }

            _rewardHistory.Add(avgReward);
            if (_rewardHistory.Count > 500) _rewardHistory.RemoveAt(0);

            return (lastLoss, avgReward);
        }

        private class ActorCritic : Module<Tensor, (Tensor actionProbs, Tensor stateValues)>
        {
            private readonly Module<Tensor, Tensor> _actor;
            private readonly Module<Tensor, Tensor> _critic;

            public ActorCritic(int stateDim, int actionDim) : base("ActorCritic")
            {
                // Actor: 상태 -> 행동 확률
                _actor = Sequential(
                    Linear(stateDim, 64),
                    Tanh(),
                    Linear(64, 64),
                    Tanh(),
                    Linear(64, actionDim),
                    Softmax(1)
                );

                // Critic: 상태 -> 가치(Value)
                _critic = Sequential(
                    Linear(stateDim, 64),
                    Tanh(),
                    Linear(64, 64),
                    Tanh(),
                    Linear(64, 1)
                );

                RegisterComponents();
            }

            public override (Tensor actionProbs, Tensor stateValues) forward(Tensor input)
            {
                return (_actor.forward(input), _critic.forward(input));
            }
        }
    }
}