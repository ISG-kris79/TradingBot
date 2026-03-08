using Microsoft.ML.Data;
using System;

namespace TradingBot
{
    /// <summary>
    /// Multi-Timeframe Entry Timing 예측을 위한 Feature Set
    /// 목표: 지금 진입하면 수익날까? (Binary Classification: 1=진입, 0=대기)
    /// </summary>
    public class MultiTimeframeEntryFeature
    {
        // ═══════════════════════════════════════════════════════════════
        // 1일봉 (Daily) - 대세 추세
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(0)] public float D1_Trend { get; set; }              // 1=상승, -1=하락, 0=중립
        [LoadColumn(1)] public float D1_RSI { get; set; }
        [LoadColumn(2)] public float D1_MACD { get; set; }
        [LoadColumn(3)] public float D1_Signal { get; set; }
        [LoadColumn(4)] public float D1_BBPosition { get; set; }         // 볼린저밴드 상 위치 (0~1)
        [LoadColumn(5)] public float D1_Volume_Ratio { get; set; }       // 거래량 비율

        // ═══════════════════════════════════════════════════════════════
        // 4시간봉 (4H) - 중기 추세 + 주요 지지/저항
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(6)] public float H4_Trend { get; set; }
        [LoadColumn(7)] public float H4_RSI { get; set; }
        [LoadColumn(8)] public float H4_MACD { get; set; }
        [LoadColumn(9)] public float H4_Signal { get; set; }
        [LoadColumn(10)] public float H4_BBPosition { get; set; }
        [LoadColumn(11)] public float H4_Volume_Ratio { get; set; }
        [LoadColumn(12)] public float H4_DistanceToSupport { get; set; }  // 지지선까지 거리 (%)
        [LoadColumn(13)] public float H4_DistanceToResist { get; set; }   // 저항선까지 거리 (%)

        // ═══════════════════════════════════════════════════════════════
        // 2시간봉 (2H) - 파동 단계
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(14)] public float H2_Trend { get; set; }
        [LoadColumn(15)] public float H2_RSI { get; set; }
        [LoadColumn(16)] public float H2_MACD { get; set; }
        [LoadColumn(17)] public float H2_Signal { get; set; }
        [LoadColumn(18)] public float H2_BBPosition { get; set; }
        [LoadColumn(19)] public float H2_Volume_Ratio { get; set; }
        [LoadColumn(20)] public float H2_WavePosition { get; set; }       // 파동 위치 (1~5)

        // ═══════════════════════════════════════════════════════════════
        // 1시간봉 (1H) - 단기 모멘텀
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(21)] public float H1_Trend { get; set; }
        [LoadColumn(22)] public float H1_RSI { get; set; }
        [LoadColumn(23)] public float H1_MACD { get; set; }
        [LoadColumn(24)] public float H1_Signal { get; set; }
        [LoadColumn(25)] public float H1_BBPosition { get; set; }
        [LoadColumn(26)] public float H1_Volume_Ratio { get; set; }
        [LoadColumn(27)] public float H1_MomentumStrength { get; set; }   // 모멘텀 강도 (0~1)

        // ═══════════════════════════════════════════════════════════════
        // 15분봉 (15M) - 현재 진입 시점 상태
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(28)] public float M15_RSI { get; set; }
        [LoadColumn(29)] public float M15_MACD { get; set; }
        [LoadColumn(30)] public float M15_Signal { get; set; }
        [LoadColumn(31)] public float M15_BBPosition { get; set; }
        [LoadColumn(32)] public float M15_Volume_Ratio { get; set; }
        [LoadColumn(33)] public float M15_PriceVsSMA20 { get; set; }      // 현재가 vs SMA20 (%)
        [LoadColumn(34)] public float M15_PriceVsSMA60 { get; set; }      // 현재가 vs SMA60 (%)
        [LoadColumn(35)] public float M15_ADX { get; set; }                // 추세 강도
        [LoadColumn(36)] public float M15_PlusDI { get; set; }
        [LoadColumn(37)] public float M15_MinusDI { get; set; }
        [LoadColumn(38)] public float M15_ATR { get; set; }                // 변동성
        [LoadColumn(39)] public float M15_OI_Change_Pct { get; set; }     // 미결제약정 변화

        // ═══════════════════════════════════════════════════════════════
        // 시간 컨텍스트 (시간대별 패턴)
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(40)] public float HourOfDay { get; set; }             // 0~23
        [LoadColumn(41)] public float DayOfWeek { get; set; }             // 0~6 (월~일)
        [LoadColumn(42)] public float IsAsianSession { get; set; }        // 1=아시아, 0=기타
        [LoadColumn(43)] public float IsEuropeSession { get; set; }       // 1=유럽, 0=기타
        [LoadColumn(44)] public float IsUSSession { get; set; }           // 1=미국, 0=기타

        // ═══════════════════════════════════════════════════════════════
        // 피보나치 되돌림 레벨 (객관적 수치로 AI 특징 사용)
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(45)] public float Fib_DistanceTo0382_Pct { get; set; }    // 현재가에서 0.382 레벨까지 거리 (%)
        [LoadColumn(46)] public float Fib_DistanceTo0618_Pct { get; set; }    // 현재가에서 0.618 레벨까지 거리 (%)
        [LoadColumn(47)] public float Fib_DistanceTo0786_Pct { get; set; }    // 현재가에서 0.786 레벨까지 거리 (%)
        [LoadColumn(48)] public float Fib_InEntryZone { get; set; }           // 진입 구간(0.382~0.618) 내 여부 (1=예, 0=아니오)

        // ═══════════════════════════════════════════════════════════════
        // 타겟 레이블 (학습용)
        // ═══════════════════════════════════════════════════════════════
        [LoadColumn(49)]
        [ColumnName("Label")]
        public bool ShouldEnter { get; set; }                             // true=진입, false=대기

        // ═══════════════════════════════════════════════════════════════
        // 메타 정보 (예측 시 참고용, 학습에는 미사용)
        // ═══════════════════════════════════════════════════════════════
        public string Symbol { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public decimal EntryPrice { get; set; }
        public float ActualProfitPct { get; set; }                        // 실제 수익률 (백테스트 결과)
    }

    /// <summary>
    /// Entry Timing 예측 결과
    /// </summary>
    public class EntryTimingPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool ShouldEnter { get; set; }                             // 진입 여부 예측

        [ColumnName("Probability")]
        public float Probability { get; set; }                            // 진입 확률 (0~1)

        [ColumnName("Score")]
        public float Score { get; set; }                                  // 원시 점수
    }

    /// <summary>
    /// 백테스팅 기반 레이블 생성 설정
    /// </summary>
    public class EntryLabelConfig
    {
        /// <summary>
        /// 목표 수익률 (예: 2.0 = 2%)
        /// </summary>
        public decimal TargetProfitPct { get; set; } = 2.0m;

        /// <summary>
        /// 손절 기준 (예: -1.0 = -1%)
        /// </summary>
        public decimal StopLossPct { get; set; } = -1.0m;

        /// <summary>
        /// 평가 기간 (캔들 수, 예: 16 = 15분봉 16개 = 4시간)
        /// </summary>
        public int EvaluationPeriodCandles { get; set; } = 16;

        /// <summary>
        /// 최소 리스크/리워드 비율
        /// </summary>
        public decimal MinRiskRewardRatio { get; set; } = 1.5m;
    }
}
