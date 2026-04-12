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

        /// <summary>최대 드로다운: 0.8% (LabelCandleData 가 2배 완화 → 실효 1.6%)</summary>
        public override float MaxDrawdownPct => 0.8f;

        /// <summary>진입 최소 신뢰도 (메이저는 완화)</summary>
        public override float MinConfidence => 0.58f;
    }
}
