using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    // [동기화 2026-07-15] 라이브(Services/LorentzianV2/LorentzianFeatures.cs)와 1:1 동일 — 백테=라이브 보장.
    //   f1 n_rsi(14) f2 n_wt(10,11) f3 n_cci(20) f4 n_adx(14) f5 n_rsi(9) f6 MACD히스토 기울기(ATR정규화+Tanh)
    public static class LorentzianFeatures
    {
        // [v5.28.5] TradingView jdehorty 원본과 동일 5특징 복원 (f6 제거) — 라이브와 1:1.
        public const int FeatureCount = 5;

        public static float[]? Extract(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 60) return null;

            double rsi14 = CalcRSI(klines, 14);
            float f1 = (float)(rsi14 / 100.0);

            var wtSeries = CalcWaveTrendSeries(klines, 10, 11);
            float f2 = NormalizeExpanding(wtSeries);

            var cciSeries = CalcCCISeries(klines, 20);
            float f3 = NormalizeExpanding(cciSeries);

            double adx14 = CalcADX(klines, 14);
            float f4 = (float)(adx14 / 100.0);

            double rsi9 = CalcRSI(klines, 9);
            float f5 = (float)(rsi9 / 100.0);

            return new[] { Clamp01(f1), Clamp01(f2), Clamp01(f3), Clamp01(f4), Clamp01(f5) };
        }

        // MACD(12,26,9) 히스토그램 기울기 → ATR정규화 + Tanh → 0-1 (0.5=평탄, →1=상승가속/세력팽창, →0=하락가속/숏)
        private static float MacdHistSlope(List<IBinanceKline> kl)
        {
            int n = kl.Count; if (n < 35) return 0.5f;
            double e12 = (double)kl[0].ClosePrice, e26 = e12, sig = 0; bool si = false;
            double histPrev = 0, histNow = 0;
            for (int i = 1; i < n; i++)
            {
                double c = (double)kl[i].ClosePrice;
                e12 += (c - e12) * (2.0 / 13); e26 += (c - e26) * (2.0 / 27);
                double macd = e12 - e26;
                if (!si) { sig = macd; si = true; } else sig += (macd - sig) * (2.0 / 10);
                histPrev = histNow; histNow = macd - sig;
            }
            double atr = AtrForSlope(kl, 14);
            if (atr <= 1e-12) return 0.5f;
            double scaleFactor = (histNow - histPrev) / atr;
            return (float)(0.5 * (1.0 + Math.Tanh(scaleFactor * 2.0)));
        }
        private static double AtrForSlope(List<IBinanceKline> kl, int period)
        {
            int n = kl.Count; if (n < period + 1) return 0;
            double sum = 0;
            for (int i = n - period; i < n; i++)
            {
                double h = (double)kl[i].HighPrice, l = (double)kl[i].LowPrice, pc = (double)kl[i - 1].ClosePrice;
                sum += Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            }
            return sum / period;
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0.5f;
            return Math.Max(0f, Math.Min(1f, v));
        }

        private static float NormalizeExpanding(List<double> series)
        {
            if (series == null || series.Count == 0) return 0.5f;
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < series.Count; i++)
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
            if (n < period * 2 + 1) return 0.0;
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
            double atr = 0, pdmS = 0, ndmS = 0;
            for (int i = 1; i <= period; i++) { atr += tr[i]; pdmS += pdm[i]; ndmS += ndm[i]; }
            double adx = 0; bool adxInit = false; int dxCount = 0;
            for (int i = period + 1; i < n; i++)
            {
                atr  = atr  - (atr  / period) + tr[i];
                pdmS = pdmS - (pdmS / period) + pdm[i];
                ndmS = ndmS - (ndmS / period) + ndm[i];
                if (atr < 1e-12) continue;
                double pdi = 100.0 * pdmS / atr;
                double ndi = 100.0 * ndmS / atr;
                double dx = (pdi + ndi) > 1e-12 ? 100.0 * Math.Abs(pdi - ndi) / (pdi + ndi) : 0;
                dxCount++;
                if (!adxInit) { adx += dx; if (dxCount == period) { adx /= period; adxInit = true; } }
                else adx = (adx * (period - 1) + dx) / period;
            }
            if (!adxInit && dxCount > 0) adx /= dxCount;
            return adx;
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
