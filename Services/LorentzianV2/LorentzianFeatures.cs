using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    // [v5.23.59] jdehorty MLExtensions 충실 재포팅 (advanced-ta 레퍼런스 1:1 대조)
    //   f1 n_rsi(14,1)  = rescale(EMA(RSI(close,14),1), 0,100)            → /100
    //   f2 n_wt(10,11)  = normalize(WT)                — 전체 히스토리 min/max (expanding)
    //   f3 n_cci(20,1)  = normalize(EMA(CCI(20),1))    — 전체 히스토리 min/max (expanding)
    //   f4 n_adx(14)    = rescale(ADX(14), 0,100)      — Wilder smoothed ADX *마지막값* /100
    //   f5 n_rsi(9,1)   = rescale(EMA(RSI(close,9),1), 0,100)             → /100
    //   변경 핵심(v5.23.58 → v5.23.59):
    //     · ADX: 전구간 DX 평균 → Wilder smoothed ADX 마지막값 (포팅 버그 fix)
    //     · WT/CCI 정규화: 200봉 sliding → 전체 히스토리 expanding min/max (jdehorty normalize())
    //     · 정규화 causal 보장 (Pine var 누적 min/max 와 동일, 미래 미사용)
    public static class LorentzianFeatures
    {
        // [v5.28.5] TradingView jdehorty 원본과 동일하게 5특징으로 복원 (f6 MACD기울기 제거).
        //   사유: 봇이 6특징이라 TradingView 5특징 KNN과 예측이 달라 상승장서 매수신호 누락(07-20 3회 미발화). 원본 정렬.
        public const int FeatureCount = 5;

        public static float[]? Extract(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 60) return null;

            // f1: n_rsi(14, 1) — RSI 후 EMA(1)=무평활, rescale(0,100)=/100
            double rsi14 = CalcRSI(klines, 14);
            float f1 = (float)(rsi14 / 100.0);

            // f2: n_wt(10, 11) — WaveTrend → normalize() (전체 히스토리 min/max)
            var wtSeries = CalcWaveTrendSeries(klines, 10, 11);
            float f2 = NormalizeExpanding(wtSeries);

            // f3: n_cci(20, 1) — CCI → EMA(1)=무평활 → normalize() (전체 히스토리)
            var cciSeries = CalcCCISeries(klines, 20);
            float f3 = NormalizeExpanding(cciSeries);

            // f4: n_adx(14) — Wilder smoothed ADX 마지막값, rescale(0,100)=/100
            double adx14 = CalcADX(klines, 14);
            float f4 = (float)(adx14 / 100.0);

            // f5: n_rsi(9, 1)
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

        // jdehorty normalize(): 전체 히스토리(현재 봉까지) min/max 로 0-1 스케일.
        //   Pine 의 var historicMin/historicMax 누적과 동일 — causal (미래 미사용).
        //   series 는 시간 오름차순, 마지막 원소가 현재 봉.
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

        // [v5.23.59 fix] jdehorty n_adx = rescale(ADX(n1),0,100).
        //   ADX = Wilder smoothed DX 의 *마지막 값* (이전: 전구간 DX 산술평균 — 명백한 포팅 버그).
        //   표준 Wilder: TR/+DM/-DM 스무딩 → +DI/-DI → DX → ADX = Wilder smooth(DX), last 반환.
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

            // 초기 평활값 = 첫 period 합
            double atr = 0, pdmS = 0, ndmS = 0;
            for (int i = 1; i <= period; i++) { atr += tr[i]; pdmS += pdm[i]; ndmS += ndm[i]; }

            double adx = 0;
            bool adxInit = false;
            int dxCount = 0;
            for (int i = period + 1; i < n; i++)
            {
                // Wilder 평활 (이전값 - 이전값/period + 신규)
                atr  = atr  - (atr  / period) + tr[i];
                pdmS = pdmS - (pdmS / period) + pdm[i];
                ndmS = ndmS - (ndmS / period) + ndm[i];
                if (atr < 1e-12) continue;
                double pdi = 100.0 * pdmS / atr;
                double ndi = 100.0 * ndmS / atr;
                double dx = (pdi + ndi) > 1e-12 ? 100.0 * Math.Abs(pdi - ndi) / (pdi + ndi) : 0;

                dxCount++;
                if (!adxInit)
                {
                    // 첫 period 개 DX 누적 → 평균으로 ADX 시드
                    adx += dx;
                    if (dxCount == period) { adx /= period; adxInit = true; }
                }
                else
                {
                    // ADX = (이전ADX*(period-1) + DX) / period — Wilder
                    adx = (adx * (period - 1) + dx) / period;
                }
            }
            // 시드 누적 완료 못 했으면(데이터 부족) 부분평균 반환
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
