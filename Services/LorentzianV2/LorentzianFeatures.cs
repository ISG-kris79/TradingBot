using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    // jdehorty Pine Script 정확 일치 — 5 feature (v5.23.1 7→5 축소)
    //   f1 RSI(14)  /100
    //   f2 WT(10,11) sliding min-max (200봉 윈도)
    //   f3 CCI(20)   sliding min-max
    //   f4 ADX(14)   /100
    //   f5 RSI(9)    /100
    //   (이전 f6 max-rise / f7 H1 slope 제거 — Pine 원본에 없음, KNN 거리 왜곡 원인)
    public static class LorentzianFeatures
    {
        public const int FeatureCount = 5;

        public static float[]? Extract(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 60) return null;
            int normWindow = Math.Min(200, klines.Count);

            double rsi14 = CalcRSI(klines, 14);
            float f1 = (float)(rsi14 / 100.0);

            var wtSeries = CalcWaveTrendSeries(klines, 10, 11);
            float f2 = NormalizeSliding(wtSeries, normWindow);

            var cciSeries = CalcCCISeries(klines, 20);
            float f3 = NormalizeSliding(cciSeries, normWindow);

            double adx20 = CalcADX(klines, 14);
            float f4 = (float)(adx20 / 100.0);

            double rsi9 = CalcRSI(klines, 9);
            float f5 = (float)(rsi9 / 100.0);

            return new[] { Clamp01(f1), Clamp01(f2), Clamp01(f3), Clamp01(f4), Clamp01(f5) };
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0.5f;
            return Math.Max(0f, Math.Min(1f, v));
        }

        private static float NormalizeSliding(List<double> series, int window)
        {
            if (series == null || series.Count == 0) return 0.5f;
            int n = series.Count;
            int start = Math.Max(0, n - window);
            double min = double.MaxValue, max = double.MinValue;
            for (int i = start; i < n; i++)
            {
                double v = series[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max <= min) return 0.5f;
            double last = series[^1];
            return (float)((last - min) / (max - min));
        }

        private static double CalcRSI(List<IBinanceKline> klines, int period)
        {
            if (klines.Count < period + 1) return 50.0;
            double avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                double change = (double)(klines[i].ClosePrice - klines[i - 1].ClosePrice);
                if (change > 0) avgGain += change; else avgLoss -= change;
            }
            avgGain /= period; avgLoss /= period;
            for (int i = period + 1; i < klines.Count; i++)
            {
                double change = (double)(klines[i].ClosePrice - klines[i - 1].ClosePrice);
                double gain = change > 0 ? change : 0;
                double loss = change < 0 ? -change : 0;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }
            if (avgLoss < 1e-12) return 100.0;
            double rs = avgGain / avgLoss;
            return 100.0 - (100.0 / (1.0 + rs));
        }

        private static List<double> CalcWaveTrendSeries(List<IBinanceKline> klines, int n1, int n2)
        {
            var hlc3 = klines.Select(k => (double)((k.HighPrice + k.LowPrice + k.ClosePrice) / 3m)).ToList();
            var esa = EMA(hlc3, n1);
            var d = new List<double>();
            for (int i = 0; i < hlc3.Count; i++) d.Add(Math.Abs(hlc3[i] - esa[i]));
            var dEma = EMA(d, n1);
            var ci = new List<double>();
            for (int i = 0; i < hlc3.Count; i++)
            {
                double denom = 0.015 * dEma[i];
                ci.Add(denom > 1e-12 ? (hlc3[i] - esa[i]) / denom : 0);
            }
            var wt1 = EMA(ci, n2);
            var wt2 = SMA(wt1, 4);
            var wt = new List<double>();
            for (int i = 0; i < wt1.Count; i++) wt.Add(wt1[i] - wt2[i]);
            return wt;
        }

        private static List<double> CalcCCISeries(List<IBinanceKline> klines, int period)
        {
            var tp = klines.Select(k => (double)((k.HighPrice + k.LowPrice + k.ClosePrice) / 3m)).ToList();
            var sma = SMA(tp, period);
            var result = new List<double>(new double[tp.Count]);
            for (int i = period - 1; i < tp.Count; i++)
            {
                double mean = sma[i];
                double mad = 0;
                for (int j = i - period + 1; j <= i; j++) mad += Math.Abs(tp[j] - mean);
                mad /= period;
                result[i] = mad > 1e-12 ? (tp[i] - mean) / (0.015 * mad) : 0;
            }
            return result;
        }

        private static double CalcADX(List<IBinanceKline> klines, int period)
        {
            int n = klines.Count;
            if (n < period * 2 + 1) return 25.0;
            double[] tr = new double[n], pdm = new double[n], ndm = new double[n];
            for (int i = 1; i < n; i++)
            {
                double high = (double)klines[i].HighPrice, low = (double)klines[i].LowPrice;
                double prevClose = (double)klines[i - 1].ClosePrice;
                tr[i] = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                double upMove = high - (double)klines[i - 1].HighPrice;
                double downMove = (double)klines[i - 1].LowPrice - low;
                pdm[i] = upMove > downMove && upMove > 0 ? upMove : 0;
                ndm[i] = downMove > upMove && downMove > 0 ? downMove : 0;
            }
            double atr = tr.Skip(1).Take(period).Sum();
            double pdmS = pdm.Skip(1).Take(period).Sum();
            double ndmS = ndm.Skip(1).Take(period).Sum();
            double dxSum = 0; int dxCount = 0;
            for (int i = period + 1; i < n; i++)
            {
                atr  = atr  - (atr  / period) + tr[i];
                pdmS = pdmS - (pdmS / period) + pdm[i];
                ndmS = ndmS - (ndmS / period) + ndm[i];
                if (atr < 1e-12) continue;
                double pdi = 100.0 * pdmS / atr;
                double ndi = 100.0 * ndmS / atr;
                double dx = (pdi + ndi) > 1e-12 ? 100.0 * Math.Abs(pdi - ndi) / (pdi + ndi) : 0;
                dxSum += dx; dxCount++;
            }
            return dxCount > 0 ? dxSum / dxCount : 25.0;
        }

        private static List<double> EMA(List<double> src, int period)
        {
            var result = new List<double>(new double[src.Count]);
            if (src.Count == 0) return result;
            double k = 2.0 / (period + 1);
            result[0] = src[0];
            for (int i = 1; i < src.Count; i++) result[i] = src[i] * k + result[i - 1] * (1 - k);
            return result;
        }

        private static List<double> SMA(List<double> src, int period)
        {
            var result = new List<double>(new double[src.Count]);
            if (src.Count == 0 || period <= 0) return result;
            double sum = 0;
            for (int i = 0; i < src.Count; i++)
            {
                sum += src[i];
                if (i >= period) sum -= src[i - period];
                result[i] = i >= period - 1 ? sum / period : src[i];
            }
            return result;
        }
    }
}
