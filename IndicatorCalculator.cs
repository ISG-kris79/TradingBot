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
    }
}