using Microsoft.ML.Data;
using System;

namespace TradingBot
{
    /// <summary>
    /// Multi-Timeframe Entry Timing 예측을 위한 Feature Set
    /// 목표: 지금 진입하면 수익날까? (Binary Classification: 1=진입, 0=대기)
    /// 주의: LoadFromEnumerable 사용 시 [LoadColumn] 불필요 (프로퍼티 이름 기반)
    /// </summary>
    public class MultiTimeframeEntryFeature
    {
        // ═══════════════════════════════════════════════════════════════
        // 1일봉 (Daily) - 대세 추세
        // ═══════════════════════════════════════════════════════════════
        public float D1_Trend { get; set; }              // 1=상승, -1=하락, 0=중립
        public float D1_RSI { get; set; }
        public float D1_MACD { get; set; }
        public float D1_Signal { get; set; }
        public float D1_BBPosition { get; set; }         // 볼린저밴드 상 위치 (0~1)
        public float D1_Volume_Ratio { get; set; }       // 거래량 비율

        // ═══════════════════════════════════════════════════════════════
        // 4시간봉 (4H) - 중기 추세 + 주요 지지/저항
        // ═══════════════════════════════════════════════════════════════
        public float H4_Trend { get; set; }
        public float H4_RSI { get; set; }
        public float H4_MACD { get; set; }
        public float H4_Signal { get; set; }
        public float H4_BBPosition { get; set; }
        public float H4_Volume_Ratio { get; set; }
        public float H4_DistanceToSupport { get; set; }  // 지지선까지 거리 (%)
        public float H4_DistanceToResist { get; set; }   // 저항선까지 거리 (%)

        // ═══════════════════════════════════════════════════════════════
        // 2시간봉 (2H) - 파동 단계
        // ═══════════════════════════════════════════════════════════════
        public float H2_Trend { get; set; }
        public float H2_RSI { get; set; }
        public float H2_MACD { get; set; }
        public float H2_Signal { get; set; }
        public float H2_BBPosition { get; set; }
        public float H2_Volume_Ratio { get; set; }
        public float H2_WavePosition { get; set; }       // 파동 위치 (1~5)

        // ═══════════════════════════════════════════════════════════════
        // 1시간봉 (1H) - 단기 모멘텀
        // ═══════════════════════════════════════════════════════════════
        public float H1_Trend { get; set; }
        public float H1_RSI { get; set; }
        public float H1_MACD { get; set; }
        public float H1_Signal { get; set; }
        public float H1_BBPosition { get; set; }
        public float H1_Volume_Ratio { get; set; }
        public float H1_MomentumStrength { get; set; }   // 모멘텀 강도 (0~1)

        // ═══════════════════════════════════════════════════════════════
        // 15분봉 (15M) - 현재 진입 시점 상태
        // ═══════════════════════════════════════════════════════════════
        public float M15_RSI { get; set; }
        public float M15_MACD { get; set; }
        public float M15_Signal { get; set; }
        public float M15_BBPosition { get; set; }
        public float M15_Volume_Ratio { get; set; }
        public float M15_PriceVsSMA20 { get; set; }      // 현재가 vs SMA20 (%)
        public float M15_PriceVsSMA60 { get; set; }      // 현재가 vs SMA60 (%)
        public float M15_ADX { get; set; }                // 추세 강도
        public float M15_PlusDI { get; set; }
        public float M15_MinusDI { get; set; }
        public float M15_ATR { get; set; }                // 변동성
        public float M15_OI_Change_Pct { get; set; }     // 미결제약정 변화

        // ═══════════════════════════════════════════════════════════════
        // 시간 컨텍스트 (시간대별 패턴)
        // ═══════════════════════════════════════════════════════════════
        public float HourOfDay { get; set; }             // 0~23
        public float DayOfWeek { get; set; }             // 0~6 (월~일)
        public float IsAsianSession { get; set; }        // 1=아시아, 0=기타
        public float IsEuropeSession { get; set; }       // 1=유럽, 0=기타
        public float IsUSSession { get; set; }           // 1=미국, 0=기타

        // ═══════════════════════════════════════════════════════════════
        // 피보나치 되돌림 레벨 (객관적 수치로 AI 특징 사용)
        // ═══════════════════════════════════════════════════════════════
        public float Fib_DistanceTo0382_Pct { get; set; }    // 현재가에서 0.382 레벨까지 거리 (%)
        public float Fib_DistanceTo0618_Pct { get; set; }    // 현재가에서 0.618 레벨까지 거리 (%)
        public float Fib_DistanceTo0786_Pct { get; set; }    // 현재가에서 0.786 레벨까지 거리 (%)
        public float Fib_InEntryZone { get; set; }           // 진입 구간(0.382~0.618) 내 여부 (1=예, 0=아니오)

        // ═══════════════════════════════════════════════════════════════
        // 타겟 레이블 (학습용)
        // ═══════════════════════════════════════════════════════════════
        [ColumnName("Label")]
        public bool ShouldEnter { get; set; }                             // true=진입, false=대기

        // ═══════════════════════════════════════════════════════════════
        // 메타 정보 (예측 시 참고용, 학습에는 미사용)
        // ML.NET IDataView에서 제외 ([NoColumn])
        // ═══════════════════════════════════════════════════════════════
        [NoColumn] public string Symbol { get; set; } = string.Empty;
        [NoColumn] public DateTime Timestamp { get; set; }
        [NoColumn] public decimal EntryPrice { get; set; }
        [NoColumn] public float ActualProfitPct { get; set; }             // 실제 수익률 (백테스트 결과)

        /// <summary>
        /// 현재 Feature를 복제하고 미래 시각 기준의 시간 컨텍스트만 갱신합니다.
        /// 시장 상태값은 유지하고, AI가 학습한 시간대 패턴으로 다음 진입 시점을 추정할 때 사용합니다.
        /// </summary>
        public MultiTimeframeEntryFeature CloneWithTimestamp(DateTime timestamp)
        {
            var clone = (MultiTimeframeEntryFeature)MemberwiseClone();
            clone.Timestamp = timestamp;
            clone.HourOfDay = timestamp.Hour;
            clone.DayOfWeek = (float)timestamp.DayOfWeek;
            clone.IsAsianSession = (timestamp.Hour >= 0 && timestamp.Hour < 9) ? 1f : 0f;
            clone.IsEuropeSession = (timestamp.Hour >= 7 && timestamp.Hour < 16) ? 1f : 0f;
            clone.IsUSSession = (timestamp.Hour >= 13 && timestamp.Hour < 22) ? 1f : 0f;
            return clone;
        }
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
    /// AI ENTRY 컬럼 표시용 미래 진입 예측 결과
    /// </summary>
    public class AIEntryForecastResult
    {
        public string Symbol { get; set; } = string.Empty;
        public float MLProbability { get; set; }
        public float TFProbability { get; set; }
        public float AverageProbability { get; set; }
        public DateTime ForecastTimeUtc { get; set; }
        public DateTime ForecastTimeLocal { get; set; }
        public int ForecastOffsetMinutes { get; set; }
        public DateTime GeneratedAtLocal { get; set; }
        public bool IsImmediate => ForecastOffsetMinutes <= 1;
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
