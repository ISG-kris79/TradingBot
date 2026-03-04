using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingBot.Services.Optimization
{
    public class Trial
    {
        public int Id { get; set; }
        public double ObjectiveValue { get; set; } = double.MinValue;
        public Dictionary<string, object> Params { get; } = new();
        private readonly Random _random = new();
        private readonly Study _study;

        public Trial(int id, Study study)
        {
            Id = id;
            _study = study;
        }

        public double SuggestFloat(string name, double min, double max)
        {
            double val;
            // [개선] 초기 5회는 랜덤 탐색, 이후 Best 값이 있으면 70% 확률로 Best 주변 탐색 (Exploitation)
            if (_study.BestTrial != null && _study.Trials.Count > 5 && _random.NextDouble() < 0.7)
            {
                if (_study.BestTrial.Params.TryGetValue(name, out object? bestValObj) && bestValObj is double bestVal)
                {
                    // Gaussian Sampling (Box-Muller Transform)
                    double u1 = 1.0 - _random.NextDouble();
                    double u2 = 1.0 - _random.NextDouble();
                    double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
                    
                    // 표준편차: 전체 범위의 10%로 설정 (지역 탐색)
                    double sigma = (max - min) * 0.1;
                    val = bestVal + (sigma * randStdNormal);
                    val = Math.Clamp(val, min, max);
                    
                    Params[name] = val;
                    return val;
                }
            }

            // 기본 Random Search (Exploration)
            val = _random.NextDouble() * (max - min) + min;
            Params[name] = val;
            return val;
        }

        public int SuggestInt(string name, int min, int max)
        {
            // SuggestFloat와 동일한 로직을 정수형에 적용
            double valDouble = SuggestFloat(name, min, max);
            int val = (int)Math.Round(valDouble);
            val = Math.Clamp(val, min, max);
            
            Params[name] = val;
            return val;
        }
    }

    public class Study
    {
        public Trial? BestTrial { get; private set; }
        public double BestValue { get; private set; } = double.MinValue;
        public List<Trial> Trials { get; } = new();

        public void Report(Trial trial, double value)
        {
            trial.ObjectiveValue = value;

            if (value > BestValue)
            {
                BestValue = value;
                BestTrial = trial;
            }
            Trials.Add(trial);
        }
    }

    /// <summary>
    /// Optuna 스타일의 하이퍼파라미터 튜너 (C# 구현)
    /// </summary>
    public class OptunaTuner
    {
        public async Task<Study> OptimizeAsync(Func<Trial, Task<double>> objective, int nTrials)
        {
            var study = new Study();
            var tasks = new List<Task>();
            var semaphore = new System.Threading.SemaphoreSlim(4); // 동시 실행 제한 (CPU 코어 수 고려)

            Console.WriteLine($"[Optuna] 최적화 시작: 총 {nTrials}회 시도");

            for (int i = 0; i < nTrials; i++)
            {
                await semaphore.WaitAsync();
                int trialId = i;
                
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var trial = new Trial(trialId, study);
                        double result = await objective(trial);
                        
                        lock (study)
                        {
                            study.Report(trial, result);
                            Console.WriteLine($"[Optuna] Trial {trialId}: {result:F4}");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            return study;
        }
    }
}
