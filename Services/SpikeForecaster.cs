namespace TradingBot.Services
{
    /// <summary>
    /// [v5.0] 1분 급등 Forecaster — 로켓 발사 예측
    /// 학습/추론 모두 1분봉 기준 (B1 버그 수정: 기존 Spike 모델은 5m 학습 + 1m 추론으로 불일치)
    ///
    /// 1분봉 특성상 예측 window 짧고, 목표 수익 높게, 드로다운 타이트
    /// </summary>
    public class SpikeForecaster : OpportunityForecaster
    {
        public override string ModelPrefix => "forecast_spike";

        /// <summary>예측 window: 5봉 × 1분 = 5분</summary>
        public override int FutureWindowBars => 5;

        /// <summary>최소 수익 목표: +4% (1분 스파이크는 크게)</summary>
        public override float TargetProfitPct => 4.0f;

        /// <summary>최대 드로다운: 1.0% (타이트)</summary>
        public override float MaxDrawdownPct => 1.0f;

        /// <summary>진입 최소 신뢰도 (1분 스파이크는 더 확실해야)</summary>
        public override float MinConfidence => 0.65f;
    }
}
