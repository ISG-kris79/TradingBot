using Dapper;
using Microsoft.Data.SqlClient;
using TradingBot.Models;
using TradingBot.Services.BacktestStrategies;
using TradingBot.Services.Optimization; // [Agent 1] 추가

namespace TradingBot.Services
{
    public enum BacktestStrategyType
    {
        RSI,
        MA_Cross,
        BollingerBand
    }

    public class BacktestService
    {
        private readonly string _connectionString;

        public BacktestService()
        {
            _connectionString = AppConfig.ConnectionString;
        }

        public async Task<BacktestResult> RunBacktestAsync(string symbol, DateTime startDate, DateTime endDate, IBacktestStrategy strategy, decimal initialBalance = 1000)
        {
            // 1. DB에서 데이터 조회
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT * FROM MarketCandles 
                WHERE Symbol = @symbol AND OpenTime >= @startDate AND OpenTime <= @endDate 
                ORDER BY OpenTime ASC";

            var candles = (await conn.QueryAsync<CandleData>(sql, new { symbol, startDate, endDate })).ToList();

            if (candles.Count == 0) return new BacktestResult { Symbol = symbol, InitialBalance = initialBalance, FinalBalance = initialBalance };

            // 2. 시뮬레이션 초기화
            var result = new BacktestResult { Symbol = symbol, InitialBalance = initialBalance, FinalBalance = initialBalance };
            result.Candles = candles;

            strategy.Execute(candles, result);
            
            // 성과 지표 계산
            CalculateMetrics(result);
            
            return result;
        }

        public async Task<BacktestResult> RunBacktestAsync(string symbol, DateTime startDate, DateTime endDate, BacktestStrategyType strategyType, decimal initialBalance = 1000)
        {
            IBacktestStrategy strategy = strategyType switch
            {
                BacktestStrategyType.RSI => new RsiBacktestStrategy(),
                BacktestStrategyType.MA_Cross => new MaCrossBacktestStrategy(),
                BacktestStrategyType.BollingerBand => new BollingerBandBacktestStrategy(),
                _ => throw new ArgumentException("Invalid strategy type")
            };

            return await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
        }

        private void CalculateMetrics(BacktestResult result)
        {
            if (result.EquityCurve.Count == 0) return;

            // 1. MDD (Max Drawdown)
            decimal peak = result.InitialBalance;
            decimal maxDrawdown = 0;

            foreach (var equity in result.EquityCurve)
            {
                if (equity > peak) peak = equity;
                decimal drawdown = (peak - equity) / peak * 100;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;
            }
            result.MaxDrawdown = maxDrawdown;

            // 2. Sharpe Ratio (간이 계산)
            // 수익률의 평균 / 수익률의 표준편차
            var returns = new List<double>();
            for (int i = 1; i < result.EquityCurve.Count; i++)
            {
                double r = (double)((result.EquityCurve[i] - result.EquityCurve[i - 1]) / result.EquityCurve[i - 1]);
                returns.Add(r);
            }

            if (returns.Count > 0)
            {
                double avgReturn = returns.Average();
                double sumSq = returns.Sum(r => Math.Pow(r - avgReturn, 2));
                double stdDev = Math.Sqrt(sumSq / returns.Count);

                if (stdDev > 0)
                    result.SharpeRatio = (avgReturn / stdDev) * Math.Sqrt(returns.Count); // 연율화 대신 전체 기간 기준
            }
        }

        public async Task<BacktestResult> OptimizeRsiStrategyAsync(string symbol, DateTime startDate, DateTime endDate, decimal initialBalance)
        {
            var bestResult = new BacktestResult { FinalBalance = 0 };
            var strategy = new RsiBacktestStrategy();

            // Grid Search: RSI Period (10~20), Buy (20~40), Sell (60~80)
            // 예시로 간단하게 Threshold만 조정
            for (double buy = 20; buy <= 40; buy += 5)
            {
                for (double sell = 60; sell <= 80; sell += 5)
                {
                    strategy.BuyThreshold = buy;
                    strategy.SellThreshold = sell;
                    var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
                    // 결과에 파라미터 정보 기록
                    result.StrategyConfiguration = $"RSI Period:14, Buy:{buy}, Sell:{sell}";

                    if (result.FinalBalance > bestResult.FinalBalance)
                    {
                        bestResult = result;
                    }
                }
            }
            return bestResult;
        }

        // [Agent 1] Optuna 스타일 최적화 메서드 추가
        public async Task<BacktestResult> OptimizeWithOptunaAsync(string symbol, DateTime startDate, DateTime endDate, decimal initialBalance, int nTrials = 20)
        {
            var tuner = new OptunaTuner();
            
            // 목적 함수 정의: Trial -> Profit
            var study = await tuner.OptimizeAsync(async (trial) =>
            {
                // 하이퍼파라미터 제안 (Suggest)
                var rsiBuy = trial.SuggestFloat("RsiBuy", 20, 40);
                var rsiSell = trial.SuggestFloat("RsiSell", 60, 80);
                
                var strategy = new RsiBacktestStrategy 
                { 
                    BuyThreshold = rsiBuy, 
                    SellThreshold = rsiSell 
                };

                var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
                return (double)result.FinalBalance; // 최대화할 값
            }, nTrials);

            // 최적 결과 반환
            var bestParams = study.BestTrial?.Params;
            var bestResult = new BacktestResult { FinalBalance = (decimal)study.BestValue };
            bestResult.StrategyConfiguration = $"[Optuna Best] Buy:{bestParams?["RsiBuy"]:F2}, Sell:{bestParams?["RsiSell"]:F2}";
            
            return bestResult;
        }

        public async Task<BacktestResult> OptimizeBollingerStrategyAsync(string symbol, DateTime startDate, DateTime endDate, decimal initialBalance)
        {
            var bestResult = new BacktestResult { FinalBalance = 0 };
            
            // Grid Search
            // Period: 10 ~ 30 (step 2)
            // Multiplier: 1.5 ~ 3.0 (step 0.1)
            for (int period = 10; period <= 30; period += 2)
            {
                for (int m = 15; m <= 30; m++) // 1.5 ~ 3.0
                {
                    double mult = m / 10.0;
                    var strategy = new BollingerBandBacktestStrategy { Period = period, Multiplier = mult };

                    var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
                    result.StrategyConfiguration = $"BB Period:{period}, Mult:{mult:F1}";

                    if (result.FinalBalance > bestResult.FinalBalance)
                    {
                        bestResult = result;
                    }
                }
            }
            return bestResult;
        }
    }
}