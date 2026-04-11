namespace TradingBot.Services
{
    /// <summary>
    /// [v5.0] PUMP 코인 전용 Forecaster — 알트 LONG 진입 기회 예측
    /// 학습/추론 모두 5분봉 기준
    /// </summary>
    public class PumpForecaster : OpportunityForecaster
    {
        public override string ModelPrefix => "forecast_pump";

        /// <summary>예측 window: 12봉 × 5분 = 60분</summary>
        public override int FutureWindowBars => 12;

        /// <summary>최소 수익 목표: +2.5%</summary>
        public override float TargetProfitPct => 2.5f;

        /// <summary>최대 드로다운: 1.5%</summary>
        public override float MaxDrawdownPct => 1.5f;

        /// <summary>진입 최소 신뢰도</summary>
        public override float MinConfidence => 0.60f;
    }
}
