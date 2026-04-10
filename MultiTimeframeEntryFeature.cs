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
        // [v3.4.2] 확장 피처: Stochastic + MACD Cross + ADX + 방향성
        // ═══════════════════════════════════════════════════════════════
        // D1 확장
        public float D1_Stoch_K { get; set; }             // Stochastic %K (0~1)
        public float D1_Stoch_D { get; set; }             // Stochastic %D (0~1)
        public float D1_MACD_Cross { get; set; }          // 1=골든, -1=데드, 0=없음
        public float D1_ADX { get; set; }                 // 추세 강도 (0~1)
        public float D1_PlusDI { get; set; }
        public float D1_MinusDI { get; set; }

        // H4 확장
        public float H4_Stoch_K { get; set; }
        public float H4_Stoch_D { get; set; }
        public float H4_MACD_Cross { get; set; }
        public float H4_ADX { get; set; }
        public float H4_PlusDI { get; set; }
        public float H4_MinusDI { get; set; }
        public float H4_MomentumStrength { get; set; }    // H4 모멘텀 (0~1)

        // H1 확장
        public float H1_Stoch_K { get; set; }
        public float H1_Stoch_D { get; set; }
        public float H1_MACD_Cross { get; set; }

        // M15 확장
        public float M15_Stoch_K { get; set; }
        public float M15_Stoch_D { get; set; }

        // 방향성 합산 (D1+H4 기준)
        public float DirectionBias { get; set; }           // -2~+2: D1+H4 방향 합산

        // ═══════════════════════════════════════════════════════════════
        // [v4.5.2] 1분봉 MACD 휩소(Whipsaw) 품질 피처
        // ═══════════════════════════════════════════════════════════════
        public float M1_MACD_CrossFlipCount { get; set; }       // 5분 내 크로스 방향 전환 횟수 (0=안전, 3+=노이즈)
        public float M1_MACD_SecsSinceOppCross { get; set; }   // 반대 크로스 경과 초 (600=없음, 30=위험)
        public float M1_MACD_SignalGapRatio { get; set; }       // |MACD-Signal| / ATR(14) (작을수록 노이즈)
        public float M1_RSI_ExtremeZone { get; set; }           // 1=과매수/과매도 극단(방향 반대), 0=정상
        public float M1_MACD_HistStrength { get; set; }         // |hist| / avg|hist| (1 미만=약한 크로스)

        // ═══════════════════════════════════════════════════════════════
        // [v4.5.6] PUMP 진입 방어 — 다중 TF 하락추세 감지 피처
        // WETUSDT 같은 "1시간봉/15분봉 하락추세 + 거래량 급증" 케이스 차단
        // ═══════════════════════════════════════════════════════════════
        public float M15_IsDowntrend { get; set; }              // 1=15분봉 SMA20<SMA60 하락추세, 0=상승/중립
        public float H1_IsDowntrend { get; set; }               // 1=1시간봉 SMA20<SMA60 하락추세, 0=상승/중립
        public float M15_ConsecBearishCount { get; set; }       // 15분봉 최근 연속 음봉 개수 (0~5)
        public float H1_PriceBelowSma60 { get; set; }           // 1=현재가 < H1 SMA60 (중기 하락), 0=위
        public float M15_RSI_BelowNeutral { get; set; }         // 1=M15 RSI<45 (약세), 0=위

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

        /// <summary>
        /// Transformer 회귀 라벨: 현재 시점부터 목표가(피보나치 0.618) 도달까지의 캔들 개수
        /// - 예: 12.0 → 12개 캔들(3시간) 후 목표가 도달 예상
        /// - 예측 범위(32캔들=8시간) 내 도달 안 하면 학습 샘플에서 제외
        /// - ML.NET은 ShouldEnter 사용, Transformer는 CandlesToTarget 사용
        /// </summary>
        [NoColumn]
        public float CandlesToTarget { get; set; } = -1f;                  // -1=미계산 또는 범위 외

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
