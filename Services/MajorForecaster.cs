using System.Collections.Generic;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.0] 메이저 코인 Forecaster (BTC/ETH/SOL 등) — LONG+SHORT 양방향 예측
    /// 학습/추론 5분봉 기준
    ///
    /// 메이저는 변동폭이 작아서 수익 목표 낮게, window 길게 설정
    /// LONG/SHORT 구분은 호출측에서 Forecast(candles, "LONG") / Forecast(candles, "SHORT") 두 번 호출
    /// </summary>
    public class MajorForecaster : OpportunityForecaster
    {
        public override string ModelPrefix => "forecast_major";

        /// <summary>예측 window: 24봉 × 5분 = 120분 (v5.0.7: 긴 window 로 포지티브 샘플 확보)</summary>
        public override int FutureWindowBars => 24;

        /// <summary>최소 수익 목표: +0.8% (v5.0.7: 메이저 실제 변동폭 맞춤, 기존 1.5% 너무 엄격)</summary>
        public override float TargetProfitPct => 0.8f;

        /// <summary>최대 드로다운: 0.8% (LabelCandleData 1.2배 완화 → 실효 0.96%)</summary>
        public override float MaxDrawdownPct => 0.8f;

        /// <summary>진입 최소 신뢰도 (메이저는 완화)</summary>
        public override float MinConfidence => 0.58f;

        /// <summary>
        /// [v5.10] 장기 추세 피처 추가 — MajorForecaster 전용 override
        /// 기존 36개 피처 + Price_Change_60 / Price_Change_240 / EmaSlope_50
        /// → 5시간/20시간 추세 방향을 학습해 상승장 SHORT 오진 방지
        /// </summary>
        public override ForecastFeature? ExtractFeature(List<IBinanceKline> candles, int index = -1)
        {
            var feature = base.ExtractFeature(candles, index);
            if (feature == null) return null;

            int idx = index >= 0 ? index : candles.Count - 1;
            if (idx < 20) return feature;

            decimal close = candles[idx].ClosePrice;
            if (close <= 0) return feature;

            // [v5.10] 5시간(60봉) 수익률 — 지금 장기 상승 중인가?
            if (idx >= 60)
            {
                decimal close60Ago = candles[idx - 60].ClosePrice;
                feature.Price_Change_60 = close60Ago > 0
                    ? Sanitize((float)((close - close60Ago) / close60Ago * 100))
                    : 0f;
            }

            // [v5.10] 20시간(240봉) 수익률 — 거시 추세 방향
            if (idx >= 240)
            {
                decimal close240Ago = candles[idx - 240].ClosePrice;
                feature.Price_Change_240 = close240Ago > 0
                    ? Sanitize((float)((close - close240Ago) / close240Ago * 100))
                    : 0f;
            }

            // [v5.10] SMA50 기울기 (5봉 전 대비 현재 변화율) — 추세 가속/감속
            if (idx >= 55)
            {
                double sma50Now = CalcSma(candles, idx, 50);
                double sma50Prev5 = CalcSma(candles, idx - 5, 50);
                feature.EmaSlope_50 = sma50Prev5 > 0
                    ? Sanitize((float)((sma50Now - sma50Prev5) / sma50Prev5 * 100))
                    : 0f;
            }

            return feature;
        }

        private static double CalcSma(List<IBinanceKline> candles, int endIdx, int period)
        {
            if (endIdx < period - 1) return (double)candles[endIdx].ClosePrice;
            double sum = 0;
            for (int i = endIdx - period + 1; i <= endIdx; i++)
                sum += (double)candles[i].ClosePrice;
            return sum / period;
        }

        private static float Sanitize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return value;
        }
    }
}
