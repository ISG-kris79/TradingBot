namespace TradingBot.Services
{
    /// <summary>
    /// [v5.0] PUMP 코인 전용 Forecaster — 알트 LONG 진입 기회 예측
    /// 학습/추론 모두 5분봉 기준
    /// </summary>
    public class PumpForecaster : OpportunityForecaster
    {
        public override string ModelPrefix => "forecast_pump";

        /// <summary>예측 window: 24봉 × 5분 = 120분 (v5.0.7: 긴 window 로 포지티브 샘플 확보)</summary>
        public override int FutureWindowBars => 24;

        /// <summary>최소 수익 목표: +2.0% (v5.0.7: 2.5% 너무 엄격, 완화)</summary>
        public override float TargetProfitPct => 2.0f;

        /// <summary>최대 드로다운: 1.5% (LabelCandleData 가 2배 완화 → 실효 3.0%)</summary>
        public override float MaxDrawdownPct => 1.5f;

        /// <summary>진입 최소 신뢰도</summary>
        public override float MinConfidence => 0.60f;
    }
}
