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

        public async Task<BacktestResult> RunBacktestAsync(
            string symbol,
            DateTime startDate,
            DateTime endDate,
            IBacktestStrategy strategy,
            decimal initialBalance = 1000,
            BacktestMetricOptions? metricOptions = null)
        {
            // 1. DB에서 데이터 조회
            using var conn = new SqlConnection(_connectionString);
            var candles = await LoadBacktestCandlesAsync(conn, symbol, startDate, endDate);
            bool loadedFromApiFallback = false;

            if (candles.Count == 0)
            {
                candles = await FetchCandlesFromBinanceRangeAsync(symbol, startDate, endDate);
                loadedFromApiFallback = candles.Count > 0;

                if (loadedFromApiFallback)
                {
                    var db = new DatabaseService();
                    await db.SaveCandleDataBulkAsync(candles);
                }
            }

            if (candles.Count == 0)
            {
                return new BacktestResult
                {
                    Symbol = symbol,
                    InitialBalance = initialBalance,
                    FinalBalance = initialBalance,
                    StrategyConfiguration = strategy.Name,
                    MetricsComputationNote = "데이터가 없어 Sharpe/Sortino를 계산하지 않았습니다.",
                    Message = $"데이터 없음: {symbol} | 구간 {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}"
                };
            }

            // 2. 시뮬레이션 초기화
            var result = new BacktestResult { Symbol = symbol, InitialBalance = initialBalance, FinalBalance = initialBalance };
            result.Candles = candles;

            strategy.Execute(candles, result);
            
            // 성과 지표 계산
            CalculateMetrics(result, metricOptions);

            if (string.IsNullOrWhiteSpace(result.StrategyConfiguration))
                result.StrategyConfiguration = strategy.Name;

            if (string.IsNullOrWhiteSpace(result.Message))
            {
                result.Message =
                    $"전략: {strategy.Name} | 기간: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} | " +
                    $"데이터: {candles.Count}개({(loadedFromApiFallback ? "Binance API" : "DB")}) | 초기자본: {initialBalance:N0} USDT";
            }

            if (result.TotalTrades == 0)
            {
                result.Message += " | 체결 0회: 기간 확대 또는 다른 전략(RSI/MA/Bollinger) 비교를 권장";
            }
            
            return result;
        }

        private async Task<List<CandleData>> FetchCandlesFromBinanceRangeAsync(
            string symbol,
            DateTime startDate,
            DateTime endDate,
            int maxChunks = 80)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return new List<CandleData>();

            var startUtc = startDate.Kind == DateTimeKind.Utc ? startDate : startDate.ToUniversalTime();
            var endUtc = endDate.Kind == DateTimeKind.Utc ? endDate : endDate.ToUniversalTime();

            if (endUtc <= startUtc)
                return new List<CandleData>();

            using var client = new BinanceRestClient();
            var all = new List<CandleData>();
            var cursor = startUtc;

            for (int i = 0; i < Math.Max(1, maxChunks) && cursor < endUtc; i++)
            {
                var response = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    startTime: cursor,
                    endTime: endUtc,
                    limit: 1500);

                if (!response.Success)
                    break;

                var batch = response.Data?
                    .OrderBy(k => k.OpenTime)
                    .ToList() ?? new List<IBinanceKline>();

                if (batch.Count == 0)
                    break;

                all.AddRange(batch.Select(k => new CandleData
                {
                    Symbol = symbol,
                    Interval = "5m",
                    OpenTime = k.OpenTime,
                    CloseTime = k.CloseTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = (float)k.Volume
                }));

                var nextCursor = batch[^1].OpenTime.AddMinutes(5);
                if (nextCursor <= cursor || batch.Count < 1500)
                    break;

                cursor = nextCursor;
            }

            return all
                .GroupBy(c => c.OpenTime)
                .Select(g => g.First())
                .OrderBy(c => c.OpenTime)
                .ToList();
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

        public async Task<BacktestResult> RunBacktestAsync(
            string symbol,
            DateTime startDate,
            DateTime endDate,
            BacktestStrategyType strategyType,
            decimal initialBalance = 1000,
            BacktestMetricOptions? metricOptions = null)
        {
            IBacktestStrategy strategy = strategyType switch
            {
                BacktestStrategyType.RSI => new RsiBacktestStrategy(),
                BacktestStrategyType.MA_Cross => new MaCrossBacktestStrategy(),
                BacktestStrategyType.BollingerBand => new BollingerBandBacktestStrategy(),
                BacktestStrategyType.ElliottWave => new ElliottWaveBacktestStrategy(), // [추가]
                _ => throw new ArgumentException("Invalid strategy type")
            };

            return await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance, metricOptions);
        }

        private void CalculateMetrics(BacktestResult result, BacktestMetricOptions? metricOptions)
        {
            metricOptions ??= new BacktestMetricOptions();

            if (result.EquityCurve.Count == 0)
            {
                result.MetricsComputationNote = result.TotalTrades == 0
                    ? "체결(청산) 거래가 없어 Sharpe/Sortino가 0으로 표시됩니다. 전략 조건이 엄격하거나 기간이 짧을 수 있습니다."
                    : "손익 곡선 데이터가 없어 Sharpe/Sortino를 계산하지 못했습니다.";
                return;
            }

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

            // 2. Sharpe/Sortino 계산용 수익률 시계열
            var returns = new List<double>();
            for (int i = 1; i < result.EquityCurve.Count; i++)
            {
                if (result.EquityCurve[i - 1] == 0)
                    continue;

                double r = (double)((result.EquityCurve[i] - result.EquityCurve[i - 1]) / result.EquityCurve[i - 1]);
                returns.Add(r);
            }

            if (returns.Count == 0)
            {
                result.MetricsComputationNote = "수익률 표본이 부족하여 Sharpe/Sortino가 0으로 표시됩니다.";
                return;
            }

            double periodsPerYear = DetermineAnnualizationPeriods(result, metricOptions.AnnualizationMode);
            double annualizationFactor = metricOptions.AnnualizationMode == BacktestAnnualizationMode.None
                ? 1.0
                : Math.Sqrt(periodsPerYear);

            double rfAnnual = metricOptions.RiskFreeRateAnnualPct / 100.0;
            double rfPerPeriod = metricOptions.AnnualizationMode == BacktestAnnualizationMode.None
                ? 0.0
                : Math.Pow(1.0 + rfAnnual, 1.0 / periodsPerYear) - 1.0;

            var excessReturns = returns.Select(r => r - rfPerPeriod).ToList();
            double avgExcess = excessReturns.Average();
            double variance = excessReturns.Sum(r => Math.Pow(r - avgExcess, 2)) / excessReturns.Count;
            double stdDev = Math.Sqrt(variance);
            bool isFlatEquityCurve = result.EquityCurve.All(e => e == result.EquityCurve[0]);

            if (stdDev > 0)
                result.SharpeRatio = (avgExcess / stdDev) * annualizationFactor;

            var downsideExcess = excessReturns.Where(r => r < 0).ToList();
            if (downsideExcess.Count > 0)
            {
                double downsideVariance = downsideExcess.Sum(r => r * r) / downsideExcess.Count;
                double downsideDeviation = Math.Sqrt(downsideVariance);
                if (downsideDeviation > 0)
                    result.SortinoRatio = (avgExcess / downsideDeviation) * annualizationFactor;
            }

            string annualizationText = metricOptions.AnnualizationMode switch
            {
                BacktestAnnualizationMode.None => "None",
                BacktestAnnualizationMode.TradingDays252 => "252",
                BacktestAnnualizationMode.CalendarDays365 => "365",
                BacktestAnnualizationMode.Crypto5m => "Crypto5m(105120)",
                _ => $"Auto({periodsPerYear:F0})"
            };

            result.MetricsComputationNote =
                $"지표 기준: RF={metricOptions.RiskFreeRateAnnualPct:F2}%/year, Annualization={annualizationText}, 표본={returns.Count}";

            if (result.TotalTrades == 0)
            {
                result.MetricsComputationNote += " | 체결 거래가 없어 곡선이 평탄하면 지표가 0에 수렴합니다.";
            }
            else if (isFlatEquityCurve)
            {
                result.MetricsComputationNote += " | EquityCurve가 평탄하여 지표가 0에 가깝습니다.";
            }
            else if (stdDev == 0)
            {
                result.MetricsComputationNote += " | 초과수익률 변동성(표준편차)이 0이라 Sharpe가 0으로 표시됩니다.";
            }

            if (downsideExcess.Count == 0)
            {
                result.MetricsComputationNote += " | 하방 수익률 표본이 없어 Sortino가 0으로 표시될 수 있습니다.";
            }
        }

        private static double DetermineAnnualizationPeriods(BacktestResult result, BacktestAnnualizationMode mode)
        {
            return mode switch
            {
                BacktestAnnualizationMode.None => 1.0,
                BacktestAnnualizationMode.TradingDays252 => 252.0,
                BacktestAnnualizationMode.CalendarDays365 => 365.0,
                BacktestAnnualizationMode.Crypto5m => 365.0 * 24.0 * 12.0,
                _ => InferPeriodsPerYearFromCandles(result)
            };
        }

        private static double InferPeriodsPerYearFromCandles(BacktestResult result)
        {
            if (result.Candles == null || result.Candles.Count < 2)
                return 365.0;

            var sorted = result.Candles
                .Select(c => c.OpenTime)
                .OrderBy(t => t)
                .ToList();

            var minuteDiffs = new List<double>();
            for (int i = 1; i < sorted.Count; i++)
            {
                var diff = (sorted[i] - sorted[i - 1]).TotalMinutes;
                if (diff > 0)
                    minuteDiffs.Add(diff);
            }

            if (minuteDiffs.Count == 0)
                return 365.0;

            minuteDiffs.Sort();
            double medianMinutes = minuteDiffs[minuteDiffs.Count / 2];
            if (medianMinutes <= 0)
                return 365.0;

            double periods = (365.0 * 24.0 * 60.0) / medianMinutes;
            if (double.IsNaN(periods) || double.IsInfinity(periods) || periods <= 0)
                return 365.0;

            return Math.Max(1.0, periods);
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
