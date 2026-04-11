using System;

namespace TradingBot.Models
{
    /// <summary>
    /// [v5.0] Forecaster 예측 결과 — 진입 기회 + 시점 + 가격
    /// </summary>
    public class ForecastResult
    {
        /// <summary>기회 있음 여부 (Classifier 결과)</summary>
        public bool HasOpportunity { get; set; }

        /// <summary>기회 확률 (0~1)</summary>
        public float Probability { get; set; }

        /// <summary>방향 (LONG / SHORT)</summary>
        public string Direction { get; set; } = "LONG";

        /// <summary>몇 봉 후 최적 진입 (0 = 즉시)</summary>
        public int OffsetBars { get; set; }

        /// <summary>현재가 대비 진입가 offset (%) — 음수=눌림, 양수=돌파</summary>
        public float PriceOffsetPct { get; set; }

        /// <summary>예상 수익률 (%)</summary>
        public float ExpectedProfitPct { get; set; }

        /// <summary>이 예측을 생성한 시점</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        /// <summary>심볼 구분 (디버그용)</summary>
        public string SymbolType { get; set; } = "";

        public static ForecastResult NoOpportunity => new() { HasOpportunity = false, Probability = 0f };
    }

    /// <summary>
    /// [v5.0] 예약 진입 (Scheduler 관리)
    /// </summary>
    public class PendingEntry
    {
        public string Symbol { get; set; } = "";
        public string Direction { get; set; } = "LONG"; // LONG / SHORT
        public decimal TargetPrice { get; set; }
        public decimal Quantity { get; set; }
        public int Leverage { get; set; }

        /// <summary>예측된 진입 시점</summary>
        public DateTime PredictedEntryTime { get; set; }

        /// <summary>예약 만료 시각 (이후 취소)</summary>
        public DateTime Expiry { get; set; }

        /// <summary>Forecaster 신뢰도 (0~1)</summary>
        public float Confidence { get; set; }

        /// <summary>어느 Forecaster가 만들었는지 (MAJOR/PUMP/SPIKE)</summary>
        public string Source { get; set; } = "";

        /// <summary>거래소 LIMIT 주문 ID</summary>
        public string? LimitOrderId { get; set; }

        /// <summary>등록 시각</summary>
        public DateTime RegisteredAt { get; set; } = DateTime.Now;

        /// <summary>원본 Forecast (디버그용)</summary>
        public ForecastResult? OriginalForecast { get; set; }
    }
}
