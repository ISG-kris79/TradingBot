using Dapper;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
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
        BollingerBand,
        ElliottWave // [추가]
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
            var candles = await LoadBacktestCandlesAsync(conn, symbol, startDate, endDate);

            if (candles.Count == 0)
            {
                return new BacktestResult
                {
                    Symbol = symbol,
                    InitialBalance = initialBalance,
                    FinalBalance = initialBalance,
                    StrategyConfiguration = strategy.Name,
                    Message = $"데이터 없음: {symbol} | 구간 {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}"
                };
            }

            // 2. 시뮬레이션 초기화
            var result = new BacktestResult { Symbol = symbol, InitialBalance = initialBalance, FinalBalance = initialBalance };
            result.Candles = candles;

            strategy.Execute(candles, result);
            
            // 성과 지표 계산
            CalculateMetrics(result);

            if (string.IsNullOrWhiteSpace(result.StrategyConfiguration))
                result.StrategyConfiguration = strategy.Name;

            if (string.IsNullOrWhiteSpace(result.Message))
            {
                result.Message =
                    $"전략: {strategy.Name} | 기간: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} | " +
                    $"데이터: {candles.Count}개 | 초기자본: {initialBalance:N0} USDT";
            }
            
            return result;
        }

        private async Task<List<CandleData>> LoadBacktestCandlesAsync(SqlConnection conn, string symbol, DateTime startDate, DateTime endDate)
        {
            const string marketCandlesSql = @"
                SELECT
                    Symbol,
                    OpenTime,
                    OpenPrice AS [Open],
                    HighPrice AS [High],
                    LowPrice AS [Low],
                    ClosePrice AS [Close],
                    CAST(Volume AS real) AS Volume
                FROM MarketCandles
                WHERE Symbol = @symbol AND OpenTime >= @startDate AND OpenTime <= @endDate
                ORDER BY OpenTime ASC";

            try
            {
                var marketCandles = (await conn.QueryAsync<CandleData>(marketCandlesSql, new { symbol, startDate, endDate })).ToList();
                if (marketCandles.Count > 0)
                    return marketCandles;
            }
            catch
            {
                // MarketCandles 스키마가 다른 경우 CandleData로 폴백
            }

            const string candleDataSql = @"
                SELECT
                    Symbol,
                    OpenTime,
                    [Open],
                    [High],
                    [Low],
                    [Close],
                    CAST(Volume AS real) AS Volume
                FROM CandleData
                WHERE Symbol = @symbol AND OpenTime >= @startDate AND OpenTime <= @endDate
                ORDER BY OpenTime ASC";

            return (await conn.QueryAsync<CandleData>(candleDataSql, new { symbol, startDate, endDate })).ToList();
        }

        public async Task<int> RecollectRecentCandleDataAsync(string symbol, int days = 30, int maxChunks = 8)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol is required", nameof(symbol));

            int safeDays = Math.Max(1, days);
            int safeChunks = Math.Max(1, maxChunks);

            var endUtc = DateTime.UtcNow;
            var startUtc = endUtc.AddDays(-safeDays);

            using var client = new BinanceRestClient();
            var allKlines = new List<IBinanceKline>();
            var cursor = startUtc;

            for (int i = 0; i < safeChunks && cursor < endUtc; i++)
            {
                var response = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    startTime: cursor,
                    endTime: endUtc,
                    limit: 1500);

                if (!response.Success)
                    throw new InvalidOperationException(response.Error?.Message ?? "Kline 재수집 실패");

                var batch = response.Data?
                    .OrderBy(k => k.OpenTime)
                    .Cast<IBinanceKline>()
                    .ToList() ?? new List<IBinanceKline>();

                if (batch.Count == 0)
                    break;

                allKlines.AddRange(batch);

                var nextCursor = batch[^1].OpenTime.AddMinutes(5);
                if (nextCursor <= cursor || batch.Count < 1500)
                    break;

                cursor = nextCursor;
            }

            var deduped = allKlines
                .GroupBy(k => k.OpenTime)
                .Select(g => g.First())
                .OrderBy(k => k.OpenTime)
                .ToList();

            if (deduped.Count == 0)
                return 0;

            var db = new DatabaseService();
            await db.SaveCandlesAsync(symbol, deduped);

            return deduped.Count;
        }

        public async Task<BacktestResult> RunBacktestAsync(string symbol, DateTime startDate, DateTime endDate, BacktestStrategyType strategyType, decimal initialBalance = 1000)
        {
            IBacktestStrategy strategy = strategyType switch
            {
                BacktestStrategyType.RSI => new RsiBacktestStrategy(),
                BacktestStrategyType.MA_Cross => new MaCrossBacktestStrategy(),
                BacktestStrategyType.BollingerBand => new BollingerBandBacktestStrategy(),
                BacktestStrategyType.ElliottWave => new ElliottWaveBacktestStrategy(), // [추가]
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

                // 3. Sortino Ratio (하방 변동성 기준)
                var downsideReturns = returns.Where(r => r < 0).ToList();
                if (downsideReturns.Count > 0)
                {
                    double downsideVariance = downsideReturns.Sum(r => r * r) / downsideReturns.Count;
                    double downsideDeviation = Math.Sqrt(downsideVariance);
                    if (downsideDeviation > 0)
                        result.SortinoRatio = (avgReturn / downsideDeviation) * Math.Sqrt(returns.Count);
                }
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

            if (study.BestTrial?.Params == null)
            {
                return new BacktestResult
                {
                    Symbol = symbol,
                    InitialBalance = initialBalance,
                    FinalBalance = initialBalance,
                    StrategyConfiguration = $"Optuna RSI (Trials: {nTrials})",
                    Message = "최적화 결과를 찾지 못했습니다."
                };
            }

            var bestParams = study.BestTrial.Params;
            double bestBuy = bestParams.TryGetValue("RsiBuy", out var buyObj) ? Convert.ToDouble(buyObj) : 30;
            double bestSell = bestParams.TryGetValue("RsiSell", out var sellObj) ? Convert.ToDouble(sellObj) : 70;

            var bestStrategy = new RsiBacktestStrategy
            {
                BuyThreshold = bestBuy,
                SellThreshold = bestSell
            };

            var bestResult = await RunBacktestAsync(symbol, startDate, endDate, bestStrategy, initialBalance);
            bestResult.StrategyConfiguration =
                $"Optuna RSI 최적화 (Trials: {nTrials}) | Buy < {bestBuy:F2}, Sell > {bestSell:F2}";
            bestResult.Message =
                $"최적화 완료: {nTrials}회 탐색, 최고 최종자산 {study.BestValue:N2} USDT (Trial #{study.BestTrial.Id})";

            bestResult.TopTrials = study.Trials
                .OrderByDescending(t => t.ObjectiveValue)
                .Take(5)
                .Select((t, index) => new OptimizationTrialItem
                {
                    Rank = index + 1,
                    TrialId = t.Id,
                    FinalBalance = (decimal)t.ObjectiveValue,
                    ProfitPercent = initialBalance > 0
                        ? ((decimal)t.ObjectiveValue - initialBalance) / initialBalance * 100m
                        : 0m,
                    Parameters = FormatTrialParams(t.Params)
                })
                .ToList();

            return bestResult;
        }

        private static string FormatTrialParams(Dictionary<string, object> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return "-";

            return string.Join(", ", parameters.Select(kvp =>
            {
                if (kvp.Value is double d)
                    return $"{kvp.Key}={d:F2}";
                if (kvp.Value is float f)
                    return $"{kvp.Key}={f:F2}";
                return $"{kvp.Key}={kvp.Value}";
            }));
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
