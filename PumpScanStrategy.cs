using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using System.Collections.Concurrent;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    public class PumpScanStrategy : ITradingStrategy
    {
        private readonly IBinanceRestClient _client;
        private readonly List<string> _majorSymbols;
        private readonly PumpScanSettings _settings;

        // 이벤트: 분석 결과 알림
        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string, decimal, string, double, double>? OnPumpDetected; // symbol, price, decision, rsi, atr
        public event Action<string>? OnLog; // [추가] 로그 이벤트

        public PumpScanStrategy(IBinanceRestClient client, List<string> majorSymbols, PumpScanSettings settings)
        {
            _client = client;
            _majorSymbols = majorSymbols;
            _settings = settings ?? new PumpScanSettings();
        }

        // [Agent 1] ITradingStrategy 구현: 단일 심볼 분석 인터페이스
        public async Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            // 단일 분석 요청 시 블랙리스트 체크 없이(또는 빈 목록으로) 즉시 분석 수행
            await AnalyzeSymbolAsync(symbol, new Dictionary<string, DateTime>(), token);
        }

        public async Task ExecuteScanAsync(
            ConcurrentDictionary<string, TickerCacheItem> tickerCache,
            Dictionary<string, DateTime> blacklistedSymbols,
            CancellationToken token)
        {
            // 1. 스캔 대상 필터링 (로컬 캐시 사용)
            var allTickers = tickerCache.Values
                .Where(t => !string.IsNullOrEmpty(t.Symbol) && t.Symbol.EndsWith("USDT") && !_majorSymbols.Contains(t.Symbol))
                .ToList();

            var afterHighPriceFilter = allTickers
                .Where(t => t.HighPrice > 0 && t.LastPrice >= t.HighPrice * 0.7m) // 고점 대비 30% 이상 하락 제외
                .ToList();

            var afterCandleFilter = afterHighPriceFilter
                .Where(t => t.LastPrice >= t.OpenPrice) // 음봉 제외
                .ToList();

            var topTickers = afterCandleFilter
                .OrderByDescending(t => t.QuoteVolume)
                .Take(10)
                .ToList();

            // 디버깅 로그
            OnLog?.Invoke($"[급등주 스캔] 전체: {allTickers.Count} → 고점필터: {afterHighPriceFilter.Count} → 양봉필터: {afterCandleFilter.Count} → 상위10: {topTickers.Count}");

            if (topTickers.Count > 0)
            {
                var symbols = string.Join(", ", topTickers.Take(5).Select(t => t.Symbol));
                OnLog?.Invoke($"[급등주 후보] {symbols}...");
            }

            // 2. 병렬 분석 실행
            var tasks = topTickers.Select(async ticker =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(ticker.Symbol))
                        await AnalyzeSymbolAsync(ticker.Symbol, blacklistedSymbols, token);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[스캔 오류] {ticker.Symbol}: {ex.Message}");
                }
            });

            await Task.WhenAll(tasks);
        }

        private async Task AnalyzeSymbolAsync(string symbol, Dictionary<string, DateTime> blacklist, CancellationToken token)
        {
            // 블랙리스트 확인
            lock (blacklist)
            {
                if (blacklist.TryGetValue(symbol, out var expiry))
                {
                    if (DateTime.Now < expiry) return;
                    blacklist.Remove(symbol);
                }
            }

            // [1단계] 1분봉 조회 (빠른 필터링)
            var k1mRes = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneMinute, limit: 30, ct: token);
            if (!k1mRes.Success) return;
            var data1m = k1mRes.Data.ToList();
            if (data1m.Count < 20) return;

            var last1m = data1m.Last();
            decimal rangePercent = (last1m.HighPrice - last1m.LowPrice) / last1m.OpenPrice * 100;
            double avgVol1m = data1m.Take(20).Average(c => (double)c.Volume);
            double volRatio = avgVol1m > 0 ? (double)last1m.Volume / avgVol1m : 0;

            // 디버깅 로그
            OnLog?.Invoke($"[1분봉 체크] {symbol} | 변동률: {rangePercent:F2}% | 거래량비: {volRatio:F2}x");

            if (rangePercent < _settings.MinPriceChangePercentage || volRatio < _settings.MinVolumeRatio)
            {
                OnLog?.Invoke($"[스캔 제외] {symbol} | 조건 미달 (변동률 {rangePercent:F2}% < {_settings.MinPriceChangePercentage}% 또는 거래량비 {volRatio:F2}x < {_settings.MinVolumeRatio}x)");
                return;
            }

            // [2단계] 정밀 데이터 조회
            var k5mTask = _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, limit: 30, ct: token);
            var k15mTask = _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, limit: 30, ct: token);
            var k1hTask = _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 100, ct: token);
            var depthTask = _client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, limit: 20, ct: token);

            await Task.WhenAll(k5mTask, k15mTask, k1hTask, depthTask);
            if (!k5mTask.Result.Success || !k15mTask.Result.Success || !k1hTask.Result.Success || !depthTask.Result.Success) return;

            var data5m = k5mTask.Result.Data.ToList();
            var data15m = k15mTask.Result.Data.ToList();
            var data1h = k1hTask.Result.Data.ToList();
            decimal currentPrice = last1m.ClosePrice;

            // 지표 계산
            double ma99 = IndicatorCalculator.CalculateSMA(data1h, 99);
            bool isAboveMA99 = (double)currentPrice > ma99;
            bool isElliottUptrend = IndicatorCalculator.AnalyzeElliottWave(data1m);
            var bb15m = IndicatorCalculator.CalculateBB(data15m, 20, 2);
            double rsi15m = IndicatorCalculator.CalculateRSI(data15m, 14);
            double atr15m = IndicatorCalculator.CalculateATR(data15m, 14);

            // 호가창 분석
            var depth = depthTask.Result.Data;
            decimal totalBids = depth.Bids.Sum(b => b.Quantity);
            decimal totalAsks = depth.Asks.Sum(a => a.Quantity);
            bool isOrderBookBullish = totalAsks > 0 && (double)(totalBids / totalAsks) >= _settings.MinOrderBookRatio;

            // 점수 산출
            int aiScore = CalculateScore(rsi15m, bb15m, currentPrice, isElliottUptrend);
            if ((double)currentPrice > bb15m.Upper) aiScore += 15;
            if (rsi15m >= 85) aiScore -= 40;

            // Decision Logic
            string decision = "WAIT";
            double volRatio5m = data5m.Take(20).Average(c => (double)c.Volume) > 0 ? (double)data5m.Last().Volume / data5m.Take(20).Average(c => (double)c.Volume) : 0;
            bool is5mBullish = data5m.Last().ClosePrice >= data5m.Last().OpenPrice;

            // 체결강도
            var recentCandles = data1m.TakeLast(3).ToList();
            decimal sumBuyVol = recentCandles.Sum(k => k.TakerBuyBaseVolume);
            decimal sumSellVol = recentCandles.Sum(k => k.Volume) - sumBuyVol;
            bool isTakerStrong = sumSellVol > 0 && (double)(sumBuyVol / sumSellVol) >= _settings.MinTakerBuyRatio;

            if (aiScore >= 85 && volRatio >= 3.5 && volRatio5m >= _settings.MinVolumeRatio5m && is5mBullish && isOrderBookBullish && isTakerStrong && isAboveMA99) decision = "🚀 PUMP";
            else if (aiScore >= 70) decision = "MOMENTUM";

            // 결과 알림 (의미있는 결과만 UI에 전송)
            if (decision != "WAIT" || aiScore >= 60)
            {
                OnSignalAnalyzed?.Invoke(new MultiTimeframeViewModel
                {
                    Symbol = symbol,
                    LastPrice = currentPrice,
                    RSI_1H = rsi15m,
                    AIScore = aiScore,
                    Decision = decision,
                    StrategyName = "Pump Scan"
                });
            }

            if (decision == "🚀 PUMP")
            {
                OnPumpDetected?.Invoke(symbol, currentPrice, decision, rsi15m, atr15m);
            }

            // [추가] 분석 결과 로그 출력 (동작 확인용)
            // 너무 빈번한 로그를 방지하기 위해 점수가 높거나 특정 조건일 때만 출력
            if (aiScore >= 60 || decision != "WAIT")
            {
                OnLog?.Invoke($"[스캔] {symbol} | Score: {aiScore} | R: {rsi15m:F1} | Dec: {decision}");
            }
        }

        private int CalculateScore(double rsi, BBResult bb, decimal currentPrice, bool isElliottUptrend)
        {
            int score = 50;
            if (isElliottUptrend) score += 20; else score -= 10;
            if (rsi >= 70) score += 15;
            else if (rsi <= 30) score -= 20;

            double price = (double)currentPrice;
            if (price >= bb.Upper) score += 15;
            else if (price <= bb.Lower) score -= 30;

            return Math.Clamp(score, 0, 100);
        }
    }
}
