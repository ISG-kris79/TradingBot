using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    /// <summary>
    /// [v5.20.0] Pine Script jdehorty/MLExtensions 의 n_rsi/n_wt/n_cci/n_adx 정규화 포팅
    ///
    /// 핵심: KNN 거리 계산 시 모든 feature 가 0-1 동일 스케일이어야 outlier 영향 최소화
    /// Pine 정규화 공식: (raw - min(history)) / (max(history) - min(history))
    ///
    /// Pine 기본 5 feature:
    ///   f1: RSI(14, 1)        — 현재 모멘텀
    ///   f2: WT(10, 11)        — Wave Trend (LazyBear)
    ///   f3: CCI(20, 1)        — 평균 가격 편차
    ///   f4: ADX(20, 2)        — 추세 강도
    ///   f5: RSI(9, 1)         — 단기 모멘텀 (다른 기간)
    /// </summary>
    public static class LorentzianFeatures
    {
        public const int FeatureCount = 7;  // [v5.20.0] 사용자 요구로 5 → 7 확장 (f6: 최대상승폭, f7: H1 기울기)

        /// <summary>
        /// 5-피처 벡터 추출 (모두 0-1 정규화)
        /// klines: 시간 오름차순(ASC), 마지막이 현재봉. 최소 60봉 필요
        /// </summary>
        public static float[]? Extract(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 60) return null;

            // 정규화는 직전 N봉 기준 min/max 사용
            int normWindow = Math.Min(200, klines.Count);

            // ── f1: RSI(14, 1) → n_rsi (raw RSI 자체가 0-100 → /100)
            double rsi14 = CalcRSI(klines, 14);
            float f1 = (float)(rsi14 / 100.0);

            // ── f2: WT(10, 11) → n_wt (sliding min-max)
            var wtSeries = CalcWaveTrendSeries(klines, 10, 11);
            float f2 = NormalizeSliding(wtSeries, normWindow);

            // ── f3: CCI(20, 1) → n_cci (sliding min-max)
            var cciSeries = CalcCCISeries(klines, 20);
            float f3 = NormalizeSliding(cciSeries, normWindow);

            // ── f4: ADX(20, 2) → /100
            double adx20 = CalcADX(klines, 14);  // ADX 계산은 14가 표준, Pine 의 20 도 동작은 유사
            float f4 = (float)(adx20 / 100.0);

            // ── f5: RSI(9, 1) → /100
            double rsi9 = CalcRSI(klines, 9);
            float f5 = (float)(rsi9 / 100.0);

            // ── [v5.20.0] f6: 직전 30봉(5m × 30 = 2.5h) 최대 상승폭 % → /20 정규화
            //   사용자 요구: "이미 너무 많이 올랐는지 AI 가 판단"
            int look6 = Math.Min(30, klines.Count - 1);
            decimal nowClose = klines[^1].ClosePrice;
            decimal maxRise6 = 0m;
            for (int i = klines.Count - look6; i < klines.Count; i++)
            {
                decimal high = klines[i].HighPrice;
                if (high > nowClose && nowClose > 0m)
                {
                    decimal risePct = (high - nowClose) / nowClose * 100m;
                    if (risePct > maxRise6) maxRise6 = risePct;
                }
            }
            // 최저점 대비 최대 상승폭 (현재가가 정점인지 확인)
            decimal minLow6 = decimal.MaxValue;
            for (int i = klines.Count - look6; i < klines.Count; i++)
                if (klines[i].LowPrice < minLow6) minLow6 = klines[i].LowPrice;
            decimal totalRise = minLow6 > 0 ? (nowClose - minLow6) / minLow6 * 100m : 0m;
            float f6 = (float)Math.Min(1.0, (double)totalRise / 20.0);  // 20% 상승 = 1.0 (만점)

            // ── [v5.20.0] f7: H1 EMA(20) 5봉 기울기 % → 시그모이드 정규화
            //   사용자 요구: "큰 흐름이 하락일 때 작은 매수 신호 필터링"
            //   5m 12개 → 1H 1개 aggregate (close 만 사용)
            float f7 = 0.5f;  // 기본값 = 중립
            if (klines.Count >= 12 * 25)  // 25 H1봉 필요 (EMA20 + 5봉 lookback)
            {
                var h1Closes = new List<double>();
                for (int g = 0; g + 12 <= klines.Count; g += 12)
                {
                    decimal sumClose = 0; int cnt = 0;
                    for (int j = g; j < g + 12; j++) { sumClose += klines[j].ClosePrice; cnt++; }
                    h1Closes.Add(cnt > 0 ? (double)(sumClose / cnt) : 0);
                }
                var emaH1 = EMA(h1Closes, 20);
                if (emaH1.Count >= 6 && emaH1[^6] > 0)
                {
                    double slopePct = (emaH1[^1] - emaH1[^6]) / emaH1[^6] * 100.0;
                    // 시그모이드: -2% = 0, 0% = 0.5, +2% = 1
                    f7 = (float)(1.0 / (1.0 + Math.Exp(-slopePct)));
                }
            }

            return new[] { Clamp01(f1), Clamp01(f2), Clamp01(f3), Clamp01(f4), Clamp01(f5), Clamp01(f6), Clamp01(f7) };
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0.5f;
            return Math.Max(0f, Math.Min(1f, v));
        }

        /// <summary>마지막 값을 직전 window 봉 min/max 기준 정규화</summary>
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

        // ── RSI (Wilder's smoothing) — 마지막 값 반환
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

        // ── Wave Trend (LazyBear) series
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

        // ── CCI series
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

        // ── ADX (Wilder's, 마지막 값)
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
