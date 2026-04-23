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
        // [v4.5.11] 일일 수익 상태 피처 — 목표 달성 후 보수적 판단 학습
        // ═══════════════════════════════════════════════════════════════
        public float DailyPnlRatio { get; set; }                // 일일 PnL / $250 목표 (예: 0.8=80% 달성, 2.0=500달러)
        public float IsAboveDailyTarget { get; set; }           // 1=$250 초과, 0=미달
        public float DailyTradeCount { get; set; }              // 오늘 거래 수 (피로도/과매매 감지)

        // ═══════════════════════════════════════════════════════════════
        // [v4.6.2] 단타 보조지표 — 트레이딩뷰 검증된 단타 핵심
        // M15 기준 EMA 9/21/50, VWAP, StochRSI
        // ═══════════════════════════════════════════════════════════════
        public float M15_EMA_CrossState { get; set; }           // 1=정배열(9>21>50), -1=역배열, 0=중립
        public float M15_Price_VWAP_Distance_Pct { get; set; }  // (Close - VWAP) / VWAP * 100
        public float M15_StochRSI_K { get; set; }
        public float M15_StochRSI_D { get; set; }
        public float M15_StochRSI_Cross { get; set; }           // 1=K>D 골든, -1=K<D 데드

        // ═══════════════════════════════════════════════════════════════
        // [v4.6.3] 단타 보조지표 추가 — BB 스퀴즈, SuperTrend, Daily Pivot
        // ═══════════════════════════════════════════════════════════════
        public float M15_BB_Width_Pct { get; set; }              // BB 밴드 폭 % (낮을수록 스퀴즈/폭발 직전)
        public float M15_SuperTrend_Direction { get; set; }      // 1=상승 추세, -1=하락 추세 (ATR10, mult3)
        public float M15_DailyPivot_R1_Dist_Pct { get; set; }   // R1까지 거리 % (양수=R1이 위)
        public float M15_DailyPivot_S1_Dist_Pct { get; set; }   // S1까지 거리 % (음수=S1이 아해)

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.75 Phase 2] 고점 진입 학습 + 여유도 + 심볼 성과 feature (6개)
        // 하드코딩 차단 대신 ML이 "이게 고점 진입인가 / 여력이 남았나 / 이 심볼 수익 나는가"를 스스로 학습
        // ═══════════════════════════════════════════════════════════════
        public float Price_Position_In_Prev5m_Range { get; set; }  // 직전 5분봉 범위 내 현재가 위치 (0=Low, 1=High) — 1에 가까우면 고점
        public float M1_Rise_From_Low_Pct { get; set; }             // 최근 1분봉 저점 대비 현재가 상승폭 (%) — 클수록 꼭대기
        public float M1_Pullback_From_High_Pct { get; set; }        // 최근 1분봉 고점 대비 현재가 하락폭 (%) — 클수록 pullback
        public float Prev_5m_Rise_From_Low_Pct { get; set; }        // 직전 5분봉 저점→현재가 상승폭 (%) — 여유도
        public float Symbol_Recent_WinRate_30d { get; set; }        // 해당 심볼 30일 승률 (0~1) — 낮을수록 기피 학습
        public float Symbol_Recent_AvgPnLPct_30d { get; set; }      // 해당 심볼 30일 건당 평균 PnLPct (정규화된 -1~+1)

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.75 Phase 2b] 다중 TF 고점 confluence + 캔들 특성 (5개)
        // ChOP/M/AAVE 등 "1분/5분/15분 모두 고점 + 윗꼬리 빨간봉" 진입 차단을 ML이 학습
        // ═══════════════════════════════════════════════════════════════
        public float M15_Position_In_Range { get; set; }           // 직전 15분봉 범위 내 현재가 위치 (0~1)
        public float M15_Upper_Shadow_Ratio { get; set; }          // 15분봉 윗꼬리 / 전체 범위 (0~1) — 0.4+ = 반전 위험
        public float M15_Is_Red_Candle { get; set; }                // 1=음봉(Close<Open), 0=양봉 — 상승 중 음봉 = 약세
        public float M15_Rise_From_Low_Pct { get; set; }           // 15분봉 저점 대비 현재가 (%) — 여유도
        public float MultiTF_Top_Confluence_Score { get; set; }     // (M1_pos + M5_pos + M15_pos)/3 (0~1) — 여러 TF 동시 고점 = 위험

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.77 Phase 5-A] 호가창 (BookTicker) 선행 지표 (4개) — WebSocket 실시간
        // 매수/매도 호가 균형, 스프레드 = 펌프 직전 시그널
        // ═══════════════════════════════════════════════════════════════
        public float BidAskImbalanceRatio { get; set; }      // BidQty / (BidQty+AskQty) (0~1) — 0.5 균형, 0.7+ = 매수우세 펌프임박
        public float SpreadPct { get; set; }                  // (Ask-Bid)/Mid × 100 (%) — 낮을수록 유동성 풍부
        public float BidQtyToAskQtyRatio { get; set; }       // BidQty / AskQty (0~10 클램프) — 1.0 균형, 3.0+ = 매수폭발
        public float MidPriceVsLastPct { get; set; }          // (MidPrice - LastPrice)/LastPrice × 100 — 호가 중심 vs 마지막 체결가 차이

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.79 Phase 5-C] aggTrade + Funding Rate 선행 지표 (5개)
        // 체결 방향성 + 펀딩비 극단 = 펌프/스퀴즈 임박 학습
        // ═══════════════════════════════════════════════════════════════
        public float AggTrade_Buy_Ratio_1m { get; set; }      // BuyVol / (BuyVol+SellVol) (0~1) — 0.5 균형, 0.7+ = 매수우세
        public float AggTrade_Buy_Volume_1m { get; set; }     // 1분 누적 매수 볼륨 (정규화: log scale)
        public float AggTrade_Sell_Volume_1m { get; set; }    // 1분 누적 매도 볼륨 (정규화: log scale)
        public float Funding_Rate { get; set; }                // 8h funding rate (예: 0.0001 = 0.01%)
        public float Funding_Rate_Extreme { get; set; }        // 1=절댓값 0.05% 초과 (롱/숏 스퀴즈 임박), 0=정상

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.80 Phase 5-D] OrderBook depth5 + Open Interest (5개)
        // 더 깊은 호가 + OI 변화 = 레버리지 축적/포지션 빌드 감지
        // ═══════════════════════════════════════════════════════════════
        public float Depth5_BidAskImbalanceRatio { get; set; }   // Top5 BidVol / (BidVol+AskVol) (0~1)
        public float Depth5_BidValueToAskValueRatio { get; set; } // Top5 BidValue / AskValue (0~10 클램프)
        public float OpenInterest_Normalized { get; set; }        // log10(OI+1) 정규화
        public float OpenInterest_Change_15m_Pct { get; set; }    // 15분 OI 변화율 (%) — 양수 = 신규 레버리지 유입
        public float OpenInterest_Surge { get; set; }             // 1 = 15분 변화 ≥3% (급격한 레버리지 폭발)

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.84 Phase 6] H1 추세전환 + M15 상승전환 + M1 신뢰성 (7개)
        // 사용자 요구: "1H 하락추세 뚫었는지" + "15m 상승전환" + "1m fetch 실패 silent fallback 차단"
        // 하드코딩 차단 대신 ML이 추세전환 패턴을 자기 학습 (AI-only 원칙 유지)
        // ═══════════════════════════════════════════════════════════════
        public float M1_Data_Valid { get; set; }                   // 1=M1 fetch 성공(정상), 0=fetch 실패(나머지 M1 feature 신뢰 X)
        public float H1_BreakoutFromDowntrend { get; set; }        // 1=최근 5봉 내 SMA20<SMA60 → SMA20>SMA60 골든크로스 발생, 0=아님
        public float H1_MACD_Hist_Turning_Up { get; set; }         // 1=MACD 히스토그램 음→양 또는 회복 중 (현재>이전), 0=하락 중
        public float H1_TrendChange_Count_Recent5 { get; set; }    // 최근 5봉 내 SMA20-SMA60 부호 전환 횟수 (0~5) — 잦으면 횡보
        public float M15_ConsecBullishCount { get; set; }          // 15분봉 최근 연속 양봉 개수 (0~5) — 상승전환
        public float M15_Hammer_Pattern { get; set; }              // 1=해머 캔들 (lower_shadow > 2×body, body 작음) — 반등 신호
        public float M15_Bullish_Engulfing { get; set; }           // 1=직전 음봉을 장악하는 양봉 (open<prev_close, close>prev_open) — 강한 반전

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
        /// 목표 수익률 (예: 0.8 = 0.8%)
        /// [v5.15.0 ROOT FIX] 2.0% → 0.8% 완화
        ///   사유: MOVRUSDT 같이 8-12시간 걸쳐 +3% 오르는 steady uptrend가 전부 NEGATIVE 라벨됨
        ///         (4시간 내 +2% 못 넘으면 fail) → 모델이 "모든 상승 = fail" 학습
        ///   수정: +0.8% 도달 시 WIN → 중간 크기 수익도 positive 학습. 학습 데이터 3-4배 증가
        /// </summary>
        public decimal TargetProfitPct { get; set; } = 0.8m;

        /// <summary>
        /// 손절 기준 (예: -1.0 = -1%)
        /// </summary>
        public decimal StopLossPct { get; set; } = -1.0m;

        /// <summary>
        /// 평가 기간 (캔들 수, 예: 48 = 15분봉 48개 = 12시간)
        /// [v5.15.0 ROOT FIX] 16 (4hr) → 48 (12hr) 확장
        ///   사유: 메이저/steady 알트는 8-24시간 시간 지평으로 수익 실현
        ///         4시간 창으로는 느린 상승을 못 잡음
        /// </summary>
        public int EvaluationPeriodCandles { get; set; } = 48;

        /// <summary>
        /// 최소 리스크/리워드 비율
        /// </summary>
        public decimal MinRiskRewardRatio { get; set; } = 1.5m;

        /// <summary>
        /// [v5.15.0 ROOT FIX] 조기 실패 드로다운 임계값 (%)
        ///   기존 -0.3% → -1.5% 완화
        ///   사유: 정상 진입도 intra-candle -0.3% 자주 찍음 → 거의 모든 sample을 FAIL 라벨
        ///         실질 손절 수준인 -1.5% 미만 drawdown 만 early-fail로 분류
        /// </summary>
        public decimal EarlyFailDrawdownPct { get; set; } = -1.5m;

        /// <summary>
        /// [v5.13.0] 조기 실패 관측 캔들 수 (15분봉 기준 2개 = 30분)
        /// </summary>
        public int EarlyFailWithinCandles { get; set; } = 2;

        /// <summary>
        /// [v5.13.0] 조기 실패 라벨링 활성 여부 (false면 기존 TP/SL only 로직)
        /// [v5.15.0] 기본값 true 유지하되 threshold를 -1.5%로 완화 (완전 비활성화 하면 flash drop 케이스 못 걸러냄)
        /// </summary>
        public bool EnableEarlyFailLabeling { get; set; } = true;
    }
}
