using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    // [v5.23.24] 밈코인 전용 5-feature: 자금 유입/거래량 중심
    //   사용자: "밈코인은 차트 모양보다 돈이 얼마나 빠르게 들어오는가가 승률 90%"
    //   기존 (RSI/WT/CCI/ADX/RSI9) → MFI/VolumeDelta/OBV/RSI/ADX
    public static class LorentzianMemeFeatures
    {
        public const int FeatureCount = 5;

        public static float[]? Extract(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 30) return null;
            int normWindow = Math.Min(200, klines.Count);

            // f1: MFI(14) — 자금 유입 RSI 유사
            double mfi14 = CalcMFI(klines, 14);
            float f1 = (float)(mfi14 / 100.0);

            // f2: Volume Delta proxy — 양봉 vol vs 음봉 vol ratio (sliding 14 bars)
            var volDeltaSeries = CalcVolumeDeltaSeries(klines);
            float f2 = NormalizeSliding(volDeltaSeries, normWindow);

            // f3: OBV slope — On-Balance Volume 14봉 변화율
            var obvSeries = CalcOBVSeries(klines);
            float f3 = NormalizeSliding(obvSeries, normWindow);

            // f4: RSI(14) — 보조 (모멘텀)
            double rsi14 = CalcRSI(klines, 14);
            float f4 = (float)(rsi14 / 100.0);

            // f5: ADX(14) — 추세 강도
            double adx14 = CalcADX(klines, 14);
            float f5 = (float)(adx14 / 100.0);

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
            return (float)((series[^1] - min) / (max - min));
        }

        // MFI(14) — 자금 유입 RSI 변형
        private static double CalcMFI(List<IBinanceKline> kl, int period)
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
                if (tp > tpPrev) posMF += mf;
                else if (tp < tpPrev) negMF += mf;
            }
            if (negMF < 1e-12) return 100.0;
            double mr = posMF / negMF;
            return 100.0 - 100.0 / (1.0 + mr);
        }

        // Volume Delta series — 양봉 vol - 음봉 vol (14봉 cumulative)
        private static List<double> CalcVolumeDeltaSeries(List<IBinanceKline> kl)
        {
            var result = new List<double>(new double[kl.Count]);
            double cum = 0;
            for (int i = 0; i < kl.Count; i++)
            {
                double v = (double)kl[i].Volume;
                if (kl[i].ClosePrice > kl[i].OpenPrice) cum += v;
                else if (kl[i].ClosePrice < kl[i].OpenPrice) cum -= v;
                result[i] = cum;
            }
            return result;
        }

        // OBV series — On-Balance Volume
        private static List<double> CalcOBVSeries(List<IBinanceKline> kl)
        {
            var result = new List<double>(new double[kl.Count]);
            double obv = 0;
            for (int i = 1; i < kl.Count; i++)
            {
                double v = (double)kl[i].Volume;
                if (kl[i].ClosePrice > kl[i - 1].ClosePrice) obv += v;
                else if (kl[i].ClosePrice < kl[i - 1].ClosePrice) obv -= v;
                result[i] = obv;
            }
            return result;
        }

        private static double CalcRSI(List<IBinanceKline> kl, int period)
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

        private static double CalcADX(List<IBinanceKline> kl, int period)
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
    }
}
