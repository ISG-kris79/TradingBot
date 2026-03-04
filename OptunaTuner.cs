using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingBot.Services.Optimization
{
    public class Trial
    {
        public int Id { get; set; }
        public Dictionary<string, object> Params { get; } = new();
        private readonly Random _random = new();

        public double SuggestFloat(string name, double min, double max)
        {
            double val = _random.NextDouble() * (max - min) + min;
            Params[name] = val;
            return val;
        }

        public int SuggestInt(string name, int min, int max)
        {
            int val = _random.Next(min, max + 1);
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
                        var trial = new Trial { Id = trialId };
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