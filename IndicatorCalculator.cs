using Binance.Net.Interfaces;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Services
{
    public static class IndicatorCalculator
    {
        public static double CalculateRSI(List<IBinanceKline> candles, int period)
        {
            if (candles.Count < period + 1) return 50;
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var rsi = quotes.GetRsi(period).LastOrDefault();
            return rsi?.Rsi ?? 50;
        }

        public static BBResult CalculateBB(List<IBinanceKline> candles, int period, double multiplier)
        {
            if (candles.Count < period) return new BBResult();
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var bb = quotes.GetBollingerBands(period, multiplier).LastOrDefault();
            return new BBResult
            {
                Upper = bb?.UpperBand ?? 0,
                Mid = bb?.Sma ?? 0,
                Lower = bb?.LowerBand ?? 0
            };
        }

        public static double CalculateATR(List<IBinanceKline> candles, int period)
        {
            if (candles.Count < period + 1) return 0;
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var atr = quotes.GetAtr(period).LastOrDefault();
            return atr?.Atr ?? 0;
        }

        public static (double Adx, double PlusDi, double MinusDi) CalculateADX(List<IBinanceKline> klines, int period = 14)
        {
            if (klines == null || klines.Count <= period * 2)
                return (0, 0, 0);

            double[] tr = new double[klines.Count];
            double[] plusDm = new double[klines.Count];
            double[] minusDm = new double[klines.Count];

            for (int i = 1; i < klines.Count; i++)
            {
                double high = (double)klines[i].HighPrice;
                double low = (double)klines[i].LowPrice;
                double prevHigh = (double)klines[i - 1].HighPrice;
                double prevLow = (double)klines[i - 1].LowPrice;
                double prevClose = (double)klines[i - 1].ClosePrice;

                double tr1 = high - low;
                double tr2 = Math.Abs(high - prevClose);
                double tr3 = Math.Abs(low - prevClose);
                tr[i] = Math.Max(tr1, Math.Max(tr2, tr3));

                double upMove = high - prevHigh;
                double downMove = prevLow - low;

                plusDm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
                minusDm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
            }

            double smoothedTr = 0;
            double smoothedPlusDm = 0;
            double smoothedMinusDm = 0;

            for (int i = 1; i <= period; i++)
            {
                smoothedTr += tr[i];
                smoothedPlusDm += plusDm[i];
                smoothedMinusDm += minusDm[i];
            }

            double[] dx = new double[klines.Count];
            double currentPlusDi = 0;
            double currentMinusDi = 0;

            for (int i = period; i < klines.Count; i++)
            {
                if (i > period)
                {
                    smoothedTr = smoothedTr - (smoothedTr / period) + tr[i];
                    smoothedPlusDm = smoothedPlusDm - (smoothedPlusDm / period) + plusDm[i];
                    smoothedMinusDm = smoothedMinusDm - (smoothedMinusDm / period) + minusDm[i];
                }

                currentPlusDi = smoothedTr == 0 ? 0 : 100 * (smoothedPlusDm / smoothedTr);
                currentMinusDi = smoothedTr == 0 ? 0 : 100 * (smoothedMinusDm / smoothedTr);

                double diDiff = Math.Abs(currentPlusDi - currentMinusDi);
                double diSum = currentPlusDi + currentMinusDi;
                dx[i] = diSum == 0 ? 0 : 100 * (diDiff / diSum);
            }

            double adx = 0;
            for (int i = period; i < period * 2; i++)
            {
                adx += dx[i];
            }
            adx /= period;

            for (int i = period * 2; i < klines.Count; i++)
            {
                adx = ((adx * (period - 1)) + dx[i]) / period;
            }

            return (adx, currentPlusDi, currentMinusDi);
        }

        public static (double Macd, double Signal, double Hist) CalculateMACD(List<IBinanceKline> candles)
        {
            if (candles.Count < 26) return (0, 0, 0);
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var macd = quotes.GetMacd().LastOrDefault();
            return (macd?.Macd ?? 0, macd?.Signal ?? 0, macd?.Histogram ?? 0);
        }

        public static (double Level236, double Level382, double Level500, double Level618) CalculateFibonacci(List<IBinanceKline> candles, int lookback)
        {
            if (candles.Count < lookback) return (0, 0, 0, 0);
            var subset = candles.TakeLast(lookback).ToList();
            decimal high = subset.Max(c => c.HighPrice);
            decimal low = subset.Min(c => c.LowPrice);
            decimal diff = high - low;

            return (
                (double)(low + diff * 0.236m),
                (double)(low + diff * 0.382m),
                (double)(low + diff * 0.500m),
                (double)(low + diff * 0.618m)
            );
        }

        public static (double K, double D) CalculateStochastic(List<IBinanceKline> candles, int lookback, int signal, int smooth)
        {
            if (candles.Count < lookback) return (0, 0);
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var stoch = quotes.GetStoch(lookback, signal, smooth).LastOrDefault();
            return (stoch?.K ?? 0, stoch?.D ?? 0);
        }

        public static double CalculateSMA(List<IBinanceKline> candles, int period)
        {
            if (candles.Count < period) return 0;
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var sma = quotes.GetSma(period).LastOrDefault();
            return sma?.Sma ?? 0;
        }

        public static double CalculateEMA(List<IBinanceKline> candles, int period)
        {
            if (candles.Count < period) return 0;
            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            var ema = quotes.GetEma(period).LastOrDefault();
            return ema?.Ema ?? 0;
        }

        public static bool AnalyzeElliottWave(List<IBinanceKline> candles)
        {
            // Simplified Elliott Wave logic: Check for Higher Highs and Higher Lows in recent swing points
            if (candles.Count < 20) return false;
            
            // This is a placeholder for complex wave analysis.
            // For now, we check if SMA 20 > SMA 50 as a trend proxy.
            double sma20 = CalculateSMA(candles, 20);
            double sma50 = CalculateSMA(candles, 50);

            return sma20 > sma50;
        }

        // [추가] 백테스트용: 시리즈를 반환하는 헬퍼 메서드들
        public static List<double> CalculateRSISeries(List<double> closePrices, int period)
        {
            if (closePrices.Count < period + 1) 
                return Enumerable.Repeat(50.0, closePrices.Count).ToList();
            
            var quotes = closePrices.Select((price, i) => new Quote
            {
                Date = DateTime.Now.AddMinutes(-closePrices.Count + i),
                Close = (decimal)price
            }).ToList();

            var rsiResults = quotes.GetRsi(period).ToList();
            return rsiResults.Select(r => r?.Rsi ?? 50).ToList();
        }

        public static List<double> CalculateSMASeries(List<double> closePrices, int period)
        {
            if (closePrices.Count < period) 
                return Enumerable.Repeat(0.0, closePrices.Count).ToList();
            
            var quotes = closePrices.Select((price, i) => new Quote
            {
                Date = DateTime.Now.AddMinutes(-closePrices.Count + i),
                Close = (decimal)price
            }).ToList();

            var smaResults = quotes.GetSma(period).ToList();
            return smaResults.Select(s => s?.Sma ?? 0).ToList();
        }

        public static List<double> CalculateEMASeries(List<double> values, int period)
        {
            if (values.Count < period) 
                return Enumerable.Repeat(0.0, values.Count).ToList();
            
            var quotes = values.Select((val, i) => new Quote
            {
                Date = DateTime.Now.AddMinutes(-values.Count + i),
                Close = (decimal)val
            }).ToList();

            var emaResults = quotes.GetEma(period).ToList();
            return emaResults.Select(e => e?.Ema ?? 0).ToList();
        }

        public static (List<double> Macd, List<double> Signal, List<double> Hist) CalculateMACDSeries(List<double> closePrices)
        {
            if (closePrices.Count < 26)
                return (new List<double>(), new List<double>(), new List<double>());

            var quotes = closePrices.Select((price, i) => new Quote
            {
                Date = DateTime.Now.AddMinutes(-closePrices.Count + i),
                Close = (decimal)price
            }).ToList();

            var results = quotes.GetMacd().ToList();
            
            return (
                results.Select(r => r?.Macd ?? 0).ToList(),
                results.Select(r => r?.Signal ?? 0).ToList(),
                results.Select(r => r?.Histogram ?? 0).ToList()
            );
        }

        public static (List<double> Upper, List<double> Mid, List<double> Lower) CalculateBBSeries(List<double> closePrices, int period = 20, double multiplier = 2.0)
        {
            if (closePrices.Count < period)
                return (new List<double>(), new List<double>(), new List<double>());

            var quotes = closePrices.Select((price, i) => new Quote
            {
                Date = DateTime.Now.AddMinutes(-closePrices.Count + i),
                Close = (decimal)price
            }).ToList();

            var results = quotes.GetBollingerBands(period, multiplier).ToList();
            return (
                results.Select(r => r?.UpperBand ?? 0).ToList(),
                results.Select(r => r?.Sma ?? 0).ToList(),
                results.Select(r => r?.LowerBand ?? 0).ToList()
            );
        }

        public static List<double> CalculateATRSeries(List<CandleData> candles, int period = 14)
        {
            if (candles.Count < period + 1)
                return Enumerable.Repeat(0.0, candles.Count).ToList();

            var quotes = candles.Select(k => new Quote
            {
                Date = k.OpenTime,
                Open = k.Open,
                High = k.High,
                Low = k.Low,
                Close = k.Close,
                Volume = (decimal)k.Volume
            }).ToList();

            var results = quotes.GetAtr(period).ToList();
            return results.Select(r => r?.Atr ?? 0).ToList();
        }
    }
}
