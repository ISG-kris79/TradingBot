using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace LorentzianValidator
{
    public sealed class FeatureVector
    {
        public double[] Features = Array.Empty<double>();
        public int Label;   // 1=수익 도달, -1=손실 도달, 0=neutral
    }

    // 사용자 스펙: 5-feature {RSI(14), MFI(14), ADX(14), CCI(20), Momentum(10)}
    public static class KnnFeatures5
    {
        public const int FeatureCount = 5;

        public static double[] Extract(List<IBinanceKline> kl)
        {
            double rsi = CalcRSI(kl, 14);
            double mfi = CalcMFI(kl, 14);
            double adx = CalcADX(kl, 14);
            double cci = CalcCCI(kl, 20);
            double mom = CalcMomentum(kl, 10);
            return new[] { rsi / 100.0, mfi / 100.0, adx / 100.0, (cci + 200.0) / 400.0, (mom + 10.0) / 20.0 };
        }

        public static double CalcRSI(List<IBinanceKline> kl, int period)
        {
            if (kl.Count < period + 1) return 50.0;
            double avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                double change = (double)(kl[i].ClosePrice - kl[i - 1].ClosePrice);
                if (change > 0) avgGain += change; else avgLoss -= change;
            }
            avgGain /= period; avgLoss /= period;
            for (int i = period + 1; i < kl.Count; i++)
            {
                double change = (double)(kl[i].ClosePrice - kl[i - 1].ClosePrice);
                double gain = change > 0 ? change : 0;
                double loss = change < 0 ? -change : 0;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }
            if (avgLoss < 1e-12) return 100.0;
            double rs = avgGain / avgLoss;
            return 100.0 - 100.0 / (1.0 + rs);
        }

        public static double CalcMFI(List<IBinanceKline> kl, int period)
        {
            int n = kl.Count;
            if (n < period + 1) return 50.0;
            double posMF = 0, negMF = 0;
            for (int i = n - period; i < n; i++)
            {
                if (i < 1) continue;
                double tp = ((double)kl[i].HighPrice + (double)kl[i].LowPrice + (double)kl[i].ClosePrice) / 3.0;
                double tpPrev = ((double)kl[i - 1].HighPrice + (double)kl[i - 1].LowPrice + (double)kl[i - 1].ClosePrice) / 3.0;
                double mf = tp * (double)kl[i].Volume;
                if (tp > tpPrev) posMF += mf; else if (tp < tpPrev) negMF += mf;
            }
            if (negMF < 1e-12) return 100.0;
            double mr = posMF / negMF;
            return 100.0 - 100.0 / (1.0 + mr);
        }

        public static double CalcADX(List<IBinanceKline> kl, int period)
        {
            int n = kl.Count;
            if (n < period * 2 + 1) return 25.0;
            double[] tr = new double[n], pdm = new double[n], ndm = new double[n];
            for (int i = 1; i < n; i++)
            {
                double high = (double)kl[i].HighPrice, low = (double)kl[i].LowPrice;
                double prevClose = (double)kl[i - 1].ClosePrice;
                tr[i] = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                double upMove = high - (double)kl[i - 1].HighPrice;
                double downMove = (double)kl[i - 1].LowPrice - low;
                pdm[i] = upMove > downMove && upMove > 0 ? upMove : 0;
                ndm[i] = downMove > upMove && downMove > 0 ? downMove : 0;
            }
            double atr = tr.Skip(1).Take(period).Sum();
            double pdmS = pdm.Skip(1).Take(period).Sum();
            double ndmS = ndm.Skip(1).Take(period).Sum();
            double adx = 0;
            bool init = false;
            for (int i = period + 1; i < n; i++)
            {
                atr  = atr  - (atr  / period) + tr[i];
                pdmS = pdmS - (pdmS / period) + pdm[i];
                ndmS = ndmS - (ndmS / period) + ndm[i];
                if (atr < 1e-12) continue;
                double pdi = 100.0 * pdmS / atr;
                double ndi = 100.0 * ndmS / atr;
                double dx = (pdi + ndi) > 1e-12 ? 100.0 * Math.Abs(pdi - ndi) / (pdi + ndi) : 0;
                if (!init) { adx = dx; init = true; }
                else adx = (adx * (period - 1) + dx) / period;
            }
            return adx;
        }

        public static double CalcCCI(List<IBinanceKline> kl, int period)
        {
            int n = kl.Count;
            if (n < period) return 0.0;
            double sum = 0;
            for (int i = n - period; i < n; i++)
                sum += ((double)kl[i].HighPrice + (double)kl[i].LowPrice + (double)kl[i].ClosePrice) / 3.0;
            double sma = sum / period;
            double mad = 0;
            for (int i = n - period; i < n; i++)
            {
                double tp = ((double)kl[i].HighPrice + (double)kl[i].LowPrice + (double)kl[i].ClosePrice) / 3.0;
                mad += Math.Abs(tp - sma);
            }
            mad /= period;
            if (mad < 1e-12) return 0.0;
            double tpNow = ((double)kl[n - 1].HighPrice + (double)kl[n - 1].LowPrice + (double)kl[n - 1].ClosePrice) / 3.0;
            return Math.Max(-200, Math.Min(200, (tpNow - sma) / (0.015 * mad)));
        }

        public static double CalcMomentum(List<IBinanceKline> kl, int period)
        {
            int n = kl.Count;
            if (n < period + 1) return 0.0;
            double now = (double)kl[n - 1].ClosePrice;
            double then = (double)kl[n - 1 - period].ClosePrice;
            if (then < 1e-12) return 0.0;
            return Math.Max(-10, Math.Min(10, (now - then) / then * 100.0));
        }
    }

    // 변형: Simple Euclidean KNN (사용자 reference 코드)
    public sealed class SimpleEuclideanKnn
    {
        private readonly List<FeatureVector> _train = new();
        private readonly int _k;
        public SimpleEuclideanKnn(int k = 10) { _k = k; }
        public void AddSample(FeatureVector v) => _train.Add(v);
        public int Count => _train.Count;
        public double PredictWinRate(double[] q)
        {
            if (_train.Count == 0) return 0;
            var top = _train
                .Select(v => (Distance: Euclidean(q, v.Features), Label: v.Label))
                .OrderBy(x => x.Distance)
                .Take(_k)
                .ToList();
            int wins = top.Count(x => x.Label == 1);
            return (double)wins / _k;
        }
        public int PredictDirection(double[] q)
        {
            if (_train.Count == 0) return 0;
            return _train
                .Select(v => (Distance: Euclidean(q, v.Features), Label: v.Label))
                .OrderBy(x => x.Distance)
                .Take(_k)
                .Sum(x => x.Label);
        }
        private static double Euclidean(double[] a, double[] b)
        {
            double sum = 0;
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++) { double d = a[i] - b[i]; sum += d * d; }
            return Math.Sqrt(sum);
        }
    }

    // 변형: Simple Lorentzian KNN (같은 구조, 거리만 Lorentzian)
    public sealed class SimpleLorentzianKnn
    {
        private readonly List<FeatureVector> _train = new();
        private readonly int _k;
        public SimpleLorentzianKnn(int k = 10) { _k = k; }
        public void AddSample(FeatureVector v) => _train.Add(v);
        public int Count => _train.Count;
        public double PredictWinRate(double[] q)
        {
            if (_train.Count == 0) return 0;
            var top = _train
                .Select(v => (Distance: Lorentzian(q, v.Features), Label: v.Label))
                .OrderBy(x => x.Distance)
                .Take(_k)
                .ToList();
            int wins = top.Count(x => x.Label == 1);
            return (double)wins / _k;
        }
        public int PredictDirection(double[] q)
        {
            if (_train.Count == 0) return 0;
            return _train
                .Select(v => (Distance: Lorentzian(q, v.Features), Label: v.Label))
                .OrderBy(x => x.Distance)
                .Take(_k)
                .Sum(x => x.Label);
        }
        private static double Lorentzian(double[] a, double[] b)
        {
            double sum = 0;
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++) sum += Math.Log(1.0 + Math.Abs(a[i] - b[i]));
            return sum;
        }
    }
}
