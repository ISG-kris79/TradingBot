using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Shared.Models;
using TradingBot.Services;
using CandleData = TradingBot.Models.CandleData;

namespace TradingBot.Services
{
    public class MarketHistoryService
    {
        private readonly MarketDataManager _marketDataManager;
        private readonly DatabaseService _databaseService;

        public MarketHistoryService(MarketDataManager marketDataManager, string connectionString)
        {
            _marketDataManager = marketDataManager;
            _databaseService = new DatabaseService((msg) => MainWindow.Instance?.AddLog(msg));

            // [실시간 저장] 새 봉 추가 이벤트 구독
            _marketDataManager.OnNewKlineAdded += OnNewCandleReceived;
        }

        // [실시간 저장] 새 봉이 완성되면 즉시 DB에 저장
        private void OnNewCandleReceived(string symbol, IBinanceKline newKline)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SaveSingleCandleAsync(symbol, newKline);
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.AddLog($"❌ [실시간 저장] {symbol} 캔들 저장 실패: {ex.Message}");
                }
            });
        }

        // 단일 캔들 즉시 저장 (지표 계산 포함)
        private async Task SaveSingleCandleAsync(string symbol, IBinanceKline newKline)
        {
            try
            {
                // KlineCache에서 최신 데이터 가져오기
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var klines)) return;

                List<IBinanceKline> klineSnapshot;
                lock (klines)
                {
                    klineSnapshot = klines.ToList();
                }

                // 최소 데이터가 너무 적으면 저장 생략
                if (klineSnapshot.Count < 2) return;

                int fibLookback = Math.Min(60, klineSnapshot.Count);

                // 보조지표 계산 (전체 캐시 기반)
                double rsi = IndicatorCalculator.CalculateRSI(klineSnapshot, 14);
                var bb = IndicatorCalculator.CalculateBB(klineSnapshot, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(klineSnapshot);
                double atr = IndicatorCalculator.CalculateATR(klineSnapshot, 14);
                var fib = IndicatorCalculator.CalculateFibonacci(klineSnapshot, fibLookback);
                bool elliottUptrend = klineSnapshot.Count >= 20 && IndicatorCalculator.AnalyzeElliottWave(klineSnapshot);

                var candleData = new CandleData
                {
                    Symbol = symbol,
                    Interval = "5m",
                    OpenTime = newKline.OpenTime,
                    Open = newKline.OpenPrice,
                    High = newKline.HighPrice,
                    Low = newKline.LowPrice,
                    Close = newKline.ClosePrice,
                    Volume = (float)newKline.Volume,
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    ElliottWaveState = elliottUptrend ? 3.0f : 1.0f,
                    Trend_Strength = elliottUptrend ? 1.0f : -1.0f,
                };

                // 4개 테이블에 모두 저장 (병렬 실행)
                var tasks = new List<Task>
                {
                    _databaseService.BulkInsertMarketDataAsync(new[] { candleData }),  // MarketCandles
                    _databaseService.SaveCandleDataBulkAsync(new[] { candleData }),    // CandleData
                    _databaseService.SaveCandleHistoryBulkAsync(new[] { candleData }), // CandleHistory
                    _databaseService.SaveMarketDataBulkAsync(new[] { candleData })     // MarketData
                };
                await Task.WhenAll(tasks);

                MainWindow.Instance?.AddLog($"💾 [실시간 저장] {symbol} {newKline.OpenTime:HH:mm:ss} 캔들 저장 완료");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [실시간 저장] {symbol} 저장 중 예외: {ex.Message}");
            }
        }

        public async Task StartRecordingAsync(CancellationToken token)
        {
            MainWindow.Instance?.AddLog("📊 [MarketHistory] 캔들 데이터 자동 저장 시작 (5분 주기)");

            try
            {
                // 첫 저장은 즉시 실행
                await SaveAllSymbolsAsync();

                while (!token.IsCancellationRequested)
                {
                    // 5분마다 KlineCache의 데이터를 DB에 저장
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    await SaveAllSymbolsAsync();
                }
            }
            catch (OperationCanceledException)
            {
                MainWindow.Instance?.AddLog("📊 [MarketHistory] 캔들 데이터 저장 중지");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [MarketHistory] 오류: {ex.Message}");
            }
        }

        private async Task SaveAllSymbolsAsync()
        {
            try
            {
                int totalSaved = 0;

                // 모든 KlineCache 심볼에 대해 저장
                foreach (var kvp in _marketDataManager.KlineCache)
                {
                    string symbol = kvp.Key;
                    var klines = kvp.Value;

                    try
                    {
                        var candleDataList = new List<CandleData>();
                        List<IBinanceKline> klineSnapshot;

                        lock (klines)
                        {
                            klineSnapshot = klines.ToList();
                        }

                        // 최소 데이터가 너무 적으면 저장 생략
                        if (klineSnapshot.Count < 2) continue;

                        int fibLookback = Math.Min(60, klineSnapshot.Count);

                        // 보조지표 계산 (전체 캐시 기반)
                        double rsi = IndicatorCalculator.CalculateRSI(klineSnapshot, 14);
                        var bb = IndicatorCalculator.CalculateBB(klineSnapshot, 20, 2);
                        var macd = IndicatorCalculator.CalculateMACD(klineSnapshot);
                        double atr = IndicatorCalculator.CalculateATR(klineSnapshot, 14);
                        var fib = IndicatorCalculator.CalculateFibonacci(klineSnapshot, fibLookback);
                        bool elliottUptrend = klineSnapshot.Count >= 20 && IndicatorCalculator.AnalyzeElliottWave(klineSnapshot);

                        // 최근 20개만 저장
                        foreach (var k in klineSnapshot.TakeLast(20))
                        {
                            candleDataList.Add(new CandleData
                            {
                                Symbol = symbol,
                                Interval = "5m",
                                OpenTime = k.OpenTime,
                                Open = k.OpenPrice,
                                High = k.HighPrice,
                                Low = k.LowPrice,
                                Close = k.ClosePrice,
                                Volume = (float)k.Volume,
                                RSI = (float)rsi,
                                BollingerUpper = (float)bb.Upper,
                                BollingerLower = (float)bb.Lower,
                                MACD = (float)macd.Macd,
                                MACD_Signal = (float)macd.Signal,
                                MACD_Hist = (float)macd.Hist,
                                ATR = (float)atr,
                                Fib_236 = (float)fib.Level236,
                                Fib_382 = (float)fib.Level382,
                                Fib_500 = (float)fib.Level500,
                                Fib_618 = (float)fib.Level618,
                                ElliottWaveState = elliottUptrend ? 3.0f : 1.0f,
                                Trend_Strength = elliottUptrend ? 1.0f : -1.0f,
                            });
                        }

                        if (candleDataList.Any())
                        {
                            // 4개 테이블에 모두 저장 (병렬 실행)
                            var tasks = new List<Task>
                            {
                                _databaseService.BulkInsertMarketDataAsync(candleDataList),  // MarketCandles
                                _databaseService.SaveCandleDataBulkAsync(candleDataList),    // CandleData
                                _databaseService.SaveCandleHistoryBulkAsync(candleDataList), // CandleHistory
                                _databaseService.SaveMarketDataBulkAsync(candleDataList)     // MarketData
                            };
                            await Task.WhenAll(tasks);
                            totalSaved += candleDataList.Count;
                        }
                    }
                    catch (Exception symbolEx)
                    {
                        MainWindow.Instance?.AddLog($"❌ [MarketHistory] {symbol} 저장 실패: {symbolEx.Message}");
                    }
                }

                if (totalSaved > 0)
                {
                    MainWindow.Instance?.AddLog($"✅ [MarketHistory] {totalSaved}건 × 4개 테이블 저장 완료 (MarketCandles, CandleData, CandleHistory, MarketData)");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [MarketHistory] 저장 실패: {ex.Message}");
            }
        }
    }
}
