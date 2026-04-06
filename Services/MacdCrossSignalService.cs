using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// MACD 골든크로스/데드크로스 감지 서비스
    /// - 상위봉(15m, 1H) 정배열 확인 + 1분봉 MACD 골든크로스 → 진입 신호
    /// - Case A: 0선 아래 골크 (과매도 반등)
    /// - Case B: 0선 위 골크 (추세 가속, 숏 스퀴징)
    /// - 데드크로스 → 트레일링 스탑 조임 / 익절 신호
    /// </summary>
    public class MacdCrossSignalService
    {
        private readonly IExchangeService _exchangeService;
        public event Action<string>? OnLog;

        public MacdCrossSignalService(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        /// <summary>
        /// 상위봉 정배열 확인 (15m + 1H SMA20 > SMA60)
        /// </summary>
        public async Task<(bool isBullish, string detail)> CheckHigherTimeframeBullishAsync(
            string symbol, CancellationToken token)
        {
            try
            {
                // 15분봉
                var k15m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 70, token);
                if (k15m == null || k15m.Count < 60)
                    return (false, "15m 데이터 부족");

                var list15m = k15m.ToList();
                double sma20_15m = list15m.TakeLast(20).Average(k => (double)k.ClosePrice);
                double sma60_15m = list15m.TakeLast(60).Average(k => (double)k.ClosePrice);
                bool bullish15m = sma20_15m > sma60_15m;

                // 1시간봉
                var k1h = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneHour, 70, token);
                if (k1h == null || k1h.Count < 60)
                    return (bullish15m, $"15m={bullish15m}, 1H=데이터부족");

                var list1h = k1h.ToList();
                double sma20_1h = list1h.TakeLast(20).Average(k => (double)k.ClosePrice);
                double sma60_1h = list1h.TakeLast(60).Average(k => (double)k.ClosePrice);
                bool bullish1h = sma20_1h > sma60_1h;

                bool ultraBullish = bullish15m && bullish1h;
                return (ultraBullish, $"15m={bullish15m}(sma20={sma20_15m:F4},sma60={sma60_15m:F4}), 1H={bullish1h}(sma20={sma20_1h:F4},sma60={sma60_1h:F4})");
            }
            catch (Exception ex)
            {
                return (false, $"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// 1분봉 MACD 골든크로스 감지
        /// </summary>
        /// <returns>(detected, caseType, macdLine, signalLine, histogram, rsi)</returns>
        public async Task<MacdCrossResult> DetectGoldenCrossAsync(string symbol, CancellationToken token)
        {
            try
            {
                var klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneMinute, 40, token);
                if (klines == null || klines.Count < 30)
                    return MacdCrossResult.None("1m 데이터 부족");

                var list = klines.ToList();
                var (macd, signal, hist) = CalculateMACD(list);
                var (prevMacd, prevSignal, prevHist) = CalculateMACD(list.Take(list.Count - 1).ToList());

                // 골든크로스: 이전봉 MACD < Signal, 현재봉 MACD >= Signal
                bool goldenCross = prevMacd < prevSignal && macd >= signal;

                // 데드크로스: 이전봉 MACD > Signal, 현재봉 MACD <= Signal
                bool deadCross = prevMacd > prevSignal && macd <= signal;

                // RSI
                double rsi = CalculateRSI(list, 14);

                // 히스토그램 변화율 (ML 피처용)
                double histChangeRate = prevHist != 0 ? (hist - prevHist) / Math.Abs(prevHist) : 0;

                if (goldenCross)
                {
                    string caseType = macd > 0 ? "B" : "A";
                    // Case B: 0선 위 골크 — 추세 가속 (RSI 65+ 무시하고 진입)
                    // Case A: 0선 아래 골크 — 과매도 반등
                    return new MacdCrossResult
                    {
                        Detected = true,
                        CrossType = MacdCrossType.Golden,
                        CaseType = caseType,
                        MacdLine = macd,
                        SignalLine = signal,
                        Histogram = hist,
                        HistChangeRate = histChangeRate,
                        RSI = rsi,
                        Detail = $"GoldenCross Case{caseType} MACD={macd:F6} Sig={signal:F6} Hist={hist:F6} RSI={rsi:F1}"
                    };
                }

                if (deadCross)
                {
                    return new MacdCrossResult
                    {
                        Detected = true,
                        CrossType = MacdCrossType.Dead,
                        CaseType = "",
                        MacdLine = macd,
                        SignalLine = signal,
                        Histogram = hist,
                        HistChangeRate = histChangeRate,
                        RSI = rsi,
                        Detail = $"DeadCross MACD={macd:F6} Sig={signal:F6} Hist={hist:F6} RSI={rsi:F1}"
                    };
                }

                // 히스토그램 Peak Out 감지 (익절 신호)
                bool histPeakOut = prevHist > 0 && hist > 0 && hist < prevHist && prevHist > 0.00001;

                return new MacdCrossResult
                {
                    Detected = false,
                    CrossType = histPeakOut ? MacdCrossType.HistPeakOut : MacdCrossType.None,
                    MacdLine = macd,
                    SignalLine = signal,
                    Histogram = hist,
                    HistChangeRate = histChangeRate,
                    RSI = rsi,
                    Detail = histPeakOut ? $"HistPeakOut hist={hist:F6} prev={prevHist:F6}" : "NoCross"
                };
            }
            catch (Exception ex)
            {
                return MacdCrossResult.None($"에러: {ex.Message}");
            }
        }

        private static (double macd, double signal, double hist) CalculateMACD(
            List<IBinanceKline> candles, int fast = 12, int slow = 26, int signalPeriod = 9)
        {
            if (candles.Count < slow + signalPeriod) return (0, 0, 0);

            var closes = candles.Select(k => (double)k.ClosePrice).ToArray();
            double[] emaFast = EMA(closes, fast);
            double[] emaSlow = EMA(closes, slow);

            double[] macdLine = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++)
                macdLine[i] = emaFast[i] - emaSlow[i];

            double[] signalLine = EMA(macdLine, signalPeriod);

            double m = macdLine[^1];
            double s = signalLine[^1];
            return (m, s, m - s);
        }

        private static double[] EMA(double[] data, int period)
        {
            double[] ema = new double[data.Length];
            double multiplier = 2.0 / (period + 1);
            ema[0] = data[0];
            for (int i = 1; i < data.Length; i++)
                ema[i] = (data[i] - ema[i - 1]) * multiplier + ema[i - 1];
            return ema;
        }

        private static double CalculateRSI(List<IBinanceKline> candles, int period = 14)
        {
            if (candles.Count < period + 1) return 50;

            double gainSum = 0, lossSum = 0;
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                double diff = (double)(candles[i].ClosePrice - candles[i - 1].ClosePrice);
                if (diff > 0) gainSum += diff; else lossSum += Math.Abs(diff);
            }
            double avgGain = gainSum / period;
            double avgLoss = lossSum / period;
            if (avgLoss == 0) return 100;
            double rs = avgGain / avgLoss;
            return 100 - (100 / (1 + rs));
        }
    }

    public enum MacdCrossType { None, Golden, Dead, HistPeakOut }

    public class MacdCrossResult
    {
        public bool Detected { get; set; }
        public MacdCrossType CrossType { get; set; }
        public string CaseType { get; set; } = "";  // "A" or "B"
        public double MacdLine { get; set; }
        public double SignalLine { get; set; }
        public double Histogram { get; set; }
        public double HistChangeRate { get; set; }
        public double RSI { get; set; }
        public string Detail { get; set; } = "";

        public static MacdCrossResult None(string detail) => new() { Detail = detail };
    }
}
