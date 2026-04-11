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

        /// <summary>예측 window: 12봉 × 5분 = 60분</summary>
        public override int FutureWindowBars => 12;

        /// <summary>최소 수익 목표: +1.5% (메이저 변동폭 小)</summary>
        public override float TargetProfitPct => 1.5f;

        /// <summary>최대 드로다운: 1.0%</summary>
        public override float MaxDrawdownPct => 1.0f;

        /// <summary>진입 최소 신뢰도 (메이저는 완화)</summary>
        public override float MinConfidence => 0.58f;
    }
}
