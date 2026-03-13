using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 멀티 전략 하이브리드 스코어링 시스템 (실전 튜닝 버전)
    /// ─────────────────────────────────────────
    /// 전략 A (AI 예측 - ML.NET/Transformer): 추세 방향 결정 (40점)
    /// 전략 B (지표 검증 - Technical):
    ///   - 엘리엇 3파동 단계 (25점) - 선취매 관점 재배분
    ///   - 거래량 모멘텀 (15점) - 1.3배부터 점진적 점수
    ///   - RSI & MACD "기울기(Slope)" (10점) *** v2.5 변경: 수치→변화율 ***
    ///   - 볼린저 밴드 위치 (10점)
    /// ─────────────────────────────────────────
    /// 
    /// 🆕 [v2.5] RSI/MACD 기울기 기반 점수 변경
    /// ────────────────────────────────────────
    /// 기존(v2.4): RSI 수치 자체 평가 (예: RSI>50 = 강세)
    /// 변경(v2.5): RSI 변화율(기울기) 평가 (예: RSI 20→30 급상승 = 강세 신호)
    /// 
    /// • GetRsiSlope() = 최근 4개 봉 간 누적 변화율
    ///   - slope ≥ +10: 급상승 (8점) → 추세 전환 신호, ~15초 앞당김
    ///   - slope 5~10: 중간 상승 (6점)
    ///   - slope 0~5:  약한 상승 (3점)
    ///   - slope -5~0: 약한 하강 (1점)
    ///   - slope < -5:  하락 (0점)
    /// 
    /// 효과: 절대값이 아닌 "방향의 전환"을 감지해 15~30초 조기 진입
    /// 예시:
    ///   - RSI 20→25→30→35 = slope +15 ✅ 강한 상승 (8점)
    ///   - RSI 50→50→51→49 = slope ~0 ❌ 중립 (1점)
    ///   - RSI 35→30→25→20 = slope -15 ❌ 급락 (0점)
    /// 
    /// FinalScore ≥ 65 (기본값, ATR 기반 동적 조정: 60~75)
    /// - 수렴장(ATR<0.15%): 60점 → 3파 선취매
    /// - 일반장(0.15~0.3%): 65점 → 표준 진입
    /// - 확장장(0.3~0.5%): 70점 → 조건 강화
    /// - 과열장(ATR>0.5%): 75점 → 보수적 진입
    /// </summary>
    public class HybridStrategyScorer
    {
        // ════════════════ 가중치 설정 ════════════════
        private const double W_AI_PREDICTION = 40.0;     // ML.NET Transformer 예측
        private const double W_ELLIOTT_WAVE = 25.0;      // 엘리엇 3파동 단계
        private const double W_VOLUME_MOMENTUM = 15.0;   // 거래량 모멘텀
        private const double W_RSI_MACD = 10.0;          // RSI & MACD 위치
        private const double W_BOLLINGER = 10.0;         // 볼린저 밴드 위치

        // ════════════════ LONG 진입 기준 ════════════════
        private const decimal LONG_PREDICTED_CHANGE_MIN = 0.0025m;   // +0.25% (20x → ROI 5%) - 완화
        private const decimal SHORT_PREDICTED_CHANGE_MIN = -0.0060m; // -0.60% (숏 조건 완화)

        // ════════════════ 최종 승인 임계값 (기본) ════════════════
        // LONG 진입 기본값: 80점 (스나이퍼 모드)
        // AI(27) + 기술(45) + 보너스(8) 등의 중첩 필요
        // 노이즈 진입 원천 차단 → 종목당 일일 1~2회 '결정적 순간'만 포착
        public const double LONG_APPROVAL_THRESHOLD = 80.0;  // 70→80 상향 (스나이퍼 모드 활성화)
        public const double SHORT_APPROVAL_THRESHOLD = 80.0; // LONG과 대칭 기준

        /// <summary>
        /// 하이브리드 스코어 평가 결과
        /// </summary>
        public class HybridScoreResult
        {
            public string Symbol { get; set; } = string.Empty;
            public string Direction { get; set; } = "WAIT";     // LONG, SHORT, WAIT
            public double FinalScore { get; set; }

            // 개별 항목 점수
            public double AiPredictionScore { get; set; }
            public double ElliottWaveScore { get; set; }
            public double VolumeMomentumScore { get; set; }
            public double RsiMacdScore { get; set; }
            public double BollingerScore { get; set; }

            // AI 예측값
            public decimal PredictedChange { get; set; }
            public decimal PredictedPrice { get; set; }
            public decimal CurrentPrice { get; set; }

            // 기술적 컨텍스트
            public double RSI { get; set; }
            public double MacdHist { get; set; }
            public string ElliottPhase { get; set; } = "Idle";
            public string BBPosition { get; set; } = "Mid";
            public double VolumeRatio { get; set; }
            public double RsiDivergence { get; set; }

            // 승인 여부
            public bool IsApproved => FinalScore >= (Direction == "LONG" ? LONG_APPROVAL_THRESHOLD : SHORT_APPROVAL_THRESHOLD);

            // ═══ 컴포넌트 최소점수 게이트 (Adaptive All-Gate) ═══
            // 핵심 원칙: 합산 총점(ATR 동적 임계값 60~80)이 1차 관문, 개별 게이트는 2차 안전망.
            // AI 극강(≥38) 시 EW/Vol/RSI/BB 게이트 전부 바이패스 — 모멘텀 우선.
            // AI 강(≥35) 시 모든 임계값 대폭 완화 — 추세 초입도 허용.
            // AI 미달(<35) 시 기본 임계값 적용 — 최소 안전망 유지.
            //
            // [Adaptive Gate Tiers]
            //   AI ≥ 38  → 전체 컴포넌트 게이트 바이패스 (극강 모멘텀)
            //   AI ≥ 35  → EW≥3, Vol≥2, RSI≥1, BB≥2 (완화)
            //   AI < 35  → EW≥5, Vol≥5, RSI≥3, BB≥4 (기본)
            public const double MIN_AI_SCORE = 30.0;
            public const double AI_SCORE_FULL_BYPASS = 36.0;        // AI≥36 → 전체 게이트 바이패스

            // 기본 임계값 (AI < 35)
            public const double MIN_ELLIOTT_SCORE_DEFAULT = 5.0;
            public const double MIN_VOLUME_SCORE_DEFAULT = 5.0;
            public const double MIN_RSI_MACD_SCORE_DEFAULT = 3.0;
            public const double MIN_BOLLINGER_SCORE_DEFAULT = 4.0;

            // AI 강신호 임계값 (AI ≥ 35 & < 38)
            public const double MIN_ELLIOTT_SCORE_RELAXED = 3.0;
            public const double MIN_VOLUME_SCORE_RELAXED = 2.0;
            public const double MIN_RSI_MACD_SCORE_RELAXED = 1.0;
            public const double MIN_BOLLINGER_SCORE_RELAXED = 2.0;

            /// <summary>
            /// Adaptive 컴포넌트 게이트 (AI 점수 기반 3단계 적응형).
            /// AI가 극강 신호(≥38)를 줄 때 → 개별 게이트 전부 바이패스 (총점만으로 판단)
            /// AI가 강신호(≥35)를 줄 때 → 모든 임계값 대폭 완화 (모멘텀 추종 허용)
            /// AI가 약신호(<35)일 때 → 기본 임계값 유지 (안전망)
            /// </summary>
            public bool PassesComponentGate(out string failReason)
            {
                failReason = "";

                // 1. AI 점수 최소 기준 미달 → 무조건 기각
                if (AiPredictionScore < MIN_AI_SCORE)
                {
                    failReason = $"AI:{AiPredictionScore:F0}<{MIN_AI_SCORE}";
                    return false;
                }

                // 2. AI 극강 → 전체 컴포넌트 게이트 바이패스 (총점이 이미 동적 임계값 통과)
                if (AiPredictionScore >= AI_SCORE_FULL_BYPASS)
                {
                    return true; // EW, Vol, RSI, BB 모두 무시
                }

                // 3. AI 강신호(≥35 & <38) → 완화된 임계값 적용
                var failures = new List<string>();

                if (ElliottWaveScore < MIN_ELLIOTT_SCORE_RELAXED)
                    failures.Add($"EW:{ElliottWaveScore:F0}<{MIN_ELLIOTT_SCORE_RELAXED}(R)");
                if (VolumeMomentumScore < MIN_VOLUME_SCORE_RELAXED)
                    failures.Add($"Vol:{VolumeMomentumScore:F0}<{MIN_VOLUME_SCORE_RELAXED}(R)");
                if (RsiMacdScore < MIN_RSI_MACD_SCORE_RELAXED)
                    failures.Add($"RSI/M:{RsiMacdScore:F0}<{MIN_RSI_MACD_SCORE_RELAXED}(R)");
                if (BollingerScore < MIN_BOLLINGER_SCORE_RELAXED)
                    failures.Add($"BB:{BollingerScore:F0}<{MIN_BOLLINGER_SCORE_RELAXED}(R)");

                if (failures.Count > 0)
                {
                    failReason = string.Join(", ", failures);
                    return false;
                }
                return true;
            }

            public override string ToString() =>
                $"[{Direction}] {Symbol} | Score: {FinalScore:F1}/100 | AI:{AiPredictionScore:F1} EW:{ElliottWaveScore:F1} Vol:{VolumeMomentumScore:F1} RSI/MACD:{RsiMacdScore:F1} BB:{BollingerScore:F1} | {(IsApproved ? "✅ APPROVED" : "❌ REJECTED")}";
        }

        /// <summary>
        /// 기술적 컨텍스트 입력
        /// </summary>
        public class TechnicalContext
        {
            // 가격
            public decimal CurrentPrice { get; set; }

            // 볼린저 밴드
            public double BbUpper { get; set; }
            public double BbMid { get; set; }
            public double BbLower { get; set; }
            public double BbWidth { get; set; }

            // 캔들 구조 (바닥/고점 가점 계산용)
            public double UpperWick { get; set; } = 0;  // 윗꼬리 (=High - max(Open,Close))
            public double LowerWick { get; set; } = 0;  // 아랫꼬리 (=min(Open,Close) - Low)
            public double Body { get; set; } = 0;       // 몸통 크기 (=|Close - Open|)

            // 거래량 (바닥 강기 가점 계산용)
            public double Volume { get; set; } = 0;     // 현재 캔들 거래량
            public double AvgVolume { get; set; } = 0;  // 20봉 평균 거래량

            // RSI - 현재값 + 최근 4개 봉 (기울기 계산용)
            public double RSI { get; set; }
            public double RSI_Prev1 { get; set; } = 0;  // 1봉 전
            public double RSI_Prev2 { get; set; } = 0;  // 2봉 전
            public double RSI_Prev3 { get; set; } = 0;  // 3봉 전
            public double RSI_Prev4 { get; set; } = 0;  // 4봉 전

            // MACD - 현재값 + 최근 4개 봉 (기울기 계산용)
            public double MacdHist { get; set; }
            public double MacdHist_Prev1 { get; set; } = 0;
            public double MacdHist_Prev2 { get; set; } = 0;
            public double MacdHist_Prev3 { get; set; } = 0;
            public double MacdHist_Prev4 { get; set; } = 0;
            public double MacdLine { get; set; }
            public double MacdSignal { get; set; }

            // SMA
            public double Sma20 { get; set; }
            public double Sma50 { get; set; }
            public double Sma200 { get; set; }

            // 엘리엇 파동
            public bool IsElliottUptrend { get; set; }
            public string ElliottPhase { get; set; } = "Idle"; // Idle, Wave3Setup, Wave3Entry, Wave3Active 등

            // 피보나치
            public decimal Fib382 { get; set; }
            public decimal Fib500 { get; set; }
            public decimal Fib618 { get; set; }

            // 거래량
            public double VolumeRatio { get; set; }       // 20봉 평균 대비
            public double VolumeMomentum { get; set; }    // 직전 봉 대비

            // RSI 다이버전스 (-1: 약세, 0: 없음, 1: 강세)
            public double RsiDivergence { get; set; }

            /// <summary>
            /// RSI 기울기(Slope) 계산: 최근 4개 봉 간의 변화율
            /// 양수 = 상승, 음수 = 하락, 절대값이 클수록 급변
            /// </summary>
            public double GetRsiSlope()
            {
                // 최근 4개 봉의 RSI 변화 합계 (급상승 감지)
                return (RSI - RSI_Prev1) + (RSI_Prev1 - RSI_Prev2) + (RSI_Prev2 - RSI_Prev3) + (RSI_Prev3 - RSI_Prev4);
                // 또는 선형 회귀로 더 정교하게: slope = (4*RSI + 3*RSI_Prev1 + 2*RSI_Prev2 + RSI_Prev3 - 10*RSI_Prev4) / 10
            }

            /// <summary>
            /// MACD 기울기(Slope) 계산: 최근 4개 봉 간의 변화율
            /// </summary>
            public double GetMacdSlope()
            {
                return (MacdHist - MacdHist_Prev1) + (MacdHist_Prev1 - MacdHist_Prev2) + (MacdHist_Prev2 - MacdHist_Prev3) + (MacdHist_Prev3 - MacdHist_Prev4);
            }
        }

        /// <summary>
        /// LONG 방향 하이브리드 스코어 산출
        /// AI의 예측이 긍정적이어도, 기술적으로 '고점'이면 진입하지 않음
        /// </summary>
        public HybridScoreResult EvaluateLong(
            string symbol,
            decimal predictedChange,
            decimal predictedPrice,
            TechnicalContext ctx)
        {
            var result = new HybridScoreResult
            {
                Symbol = symbol,
                Direction = "LONG",
                PredictedChange = predictedChange,
                PredictedPrice = predictedPrice,
                CurrentPrice = ctx.CurrentPrice,
                RSI = ctx.RSI,
                MacdHist = ctx.MacdHist,
                ElliottPhase = ctx.ElliottPhase,
                VolumeRatio = ctx.VolumeRatio,
                RsiDivergence = ctx.RsiDivergence
            };

            // ── 1. AI 예측 점수 (40점 만점) ──
            // 예측 변화율에 따라 0~40점 부여
            if (predictedChange >= LONG_PREDICTED_CHANGE_MIN)
            {
                // +0.35% 이상이면 기본 25점, +1.0% 이상이면 40점
                double aiRatio = Math.Min((double)(predictedChange / 0.010m), 1.0);
                result.AiPredictionScore = 25 + (15 * aiRatio);
            }
            else if (predictedChange > 0)
            {
                // 양수지만 임계값 미달: 부분 점수
                result.AiPredictionScore = (double)(predictedChange / LONG_PREDICTED_CHANGE_MIN) * 20;
            }
            else
            {
                // AI가 하락을 예측하면 LONG에 0점
                result.AiPredictionScore = 0;
            }

            // ── 2. 엘리엇 3파동 점수 (25점 만점) ──
            result.ElliottWaveScore = ScoreElliottForLong(ctx);

            // ── 3. 거래량 모멘텀 점수 (15점 만점) ──
            result.VolumeMomentumScore = ScoreVolumeForLong(ctx);

            // ── 4. RSI & MACD 점수 (10점 만점) ──
            result.RsiMacdScore = ScoreRsiMacdForLong(ctx);

            // ── 5. 볼린저 밴드 점수 (10점 만점) ──
            result.BollingerScore = ScoreBollingerForLong(ctx);
            result.BBPosition = GetBBPosition(ctx);

            // ── 6. 과매도 바닥 낚시 보너스 (새로운 신호) ──
            double overSoldBonus = GetOverSoldBonus(ctx);

            result.FinalScore = Math.Clamp(
                result.AiPredictionScore +
                result.ElliottWaveScore +
                result.VolumeMomentumScore +
                result.RsiMacdScore +
                result.BollingerScore +
                overSoldBonus,
                0, 100);

            return result;
        }

        /// <summary>
        /// SHORT 방향 하이브리드 스코어 산출
        /// AI가 하락 가속도를 예측 + 기술적으로 저항 확인 시에만 진입
        /// </summary>
        public HybridScoreResult EvaluateShort(
            string symbol,
            decimal predictedChange,
            decimal predictedPrice,
            TechnicalContext ctx)
        {
            var result = new HybridScoreResult
            {
                Symbol = symbol,
                Direction = "SHORT",
                PredictedChange = predictedChange,
                PredictedPrice = predictedPrice,
                CurrentPrice = ctx.CurrentPrice,
                RSI = ctx.RSI,
                MacdHist = ctx.MacdHist,
                ElliottPhase = ctx.ElliottPhase,
                VolumeRatio = ctx.VolumeRatio,
                RsiDivergence = ctx.RsiDivergence
            };

            // ── 1. AI 예측 점수 (40점 만점) ──
            if (predictedChange <= SHORT_PREDICTED_CHANGE_MIN)
            {
                // -0.80% 이하: 기본 25점, -2.0% 이하면 40점
                double aiRatio = Math.Min((double)(predictedChange / -0.020m), 1.0);
                result.AiPredictionScore = 25 + (15 * aiRatio);
            }
            else if (predictedChange < 0)
            {
                result.AiPredictionScore = (double)(predictedChange / SHORT_PREDICTED_CHANGE_MIN) * 20;
            }
            else
            {
                result.AiPredictionScore = 0;
            }

            // ── 2. 엘리엇 파동 점수 (25점 만점) ──
            result.ElliottWaveScore = ScoreElliottForShort(ctx);

            // ── 3. 거래량 모멘텀 점수 (15점 만점) ──
            result.VolumeMomentumScore = ScoreVolumeForShort(ctx);

            // ── 4. RSI & MACD 점수 (10점 만점) ──
            result.RsiMacdScore = ScoreRsiMacdForShort(ctx);

            // ── 5. 볼린저 밴드 점수 (10점 만점) ──
            result.BollingerScore = ScoreBollingerForShort(ctx);
            result.BBPosition = GetBBPosition(ctx);

            // ── 6. 과매수 고점 낚시 보너스 (새로운 신호) ──
            double overBoughtBonus = GetOverBoughtShortBonus(ctx);

            result.FinalScore = Math.Clamp(
                result.AiPredictionScore +
                result.ElliottWaveScore +
                result.VolumeMomentumScore +
                result.RsiMacdScore +
                result.BollingerScore +
                overBoughtBonus,
                0, 100);

            return result;
        }

        // ══════════════════════════════════════════════════════════
        //  LONG 개별 항목 점수 계산
        // ══════════════════════════════════════════════════════════

        private double ScoreElliottForLong(TechnicalContext ctx)
        {
            double score = 0;

            // ─── 변경 [v2.5]: 엘리엇 3파 "확인" → 역추세 "낚시" 가점 ───
            // 기존: Wave3Entry 확정 후 진입 (늦음)
            // 변경: 바닥 신호(RSI≤20 + BB하단) 감지 시 미리 25점 부여 (빠름)
            //
            // 역추세 가점: RSI 극도 과매도 + BB 하단 이탈
            // "지옥 구경하고 왔다"는 기술 지표 신호 → 3파동 점수를 당겨주기
            if (ctx.RSI <= 20 && (double)ctx.CurrentPrice <= ctx.BbLower)
            {
                score = 25; // ⭐ "바닥"이다! 즉시 반등 기대 → 풀 점수
            }
            // Wave3 단계별 보조 점수 (이제는 참고일만 함)
            else if (ctx.ElliottPhase == "Wave3Entry") score = 10;      
            else if (ctx.ElliottPhase == "Wave3Setup") score = 15; 
            else if (ctx.ElliottPhase == "Wave3Active") score = 10;
            else if (ctx.ElliottPhase == "Wave2Started") score = 15; // Wave2 눌림목은 여전히 우호
            else if (ctx.IsElliottUptrend) score = 5; // 상승 추세 기본점

            // RSI 강세 다이버전스 보너스
            if (ctx.RsiDivergence > 0 && (ctx.RSI <= 30 || ctx.ElliottPhase == "Wave2Started"))
                score += 5; // 갭 메꾸기 보너스

            // SMA 정배열 보너스
            if (ctx.Sma20 > ctx.Sma50 && ctx.Sma50 > ctx.Sma200) score = Math.Min(score + 3, 25);

            return score;
        }

        private double ScoreVolumeForLong(TechnicalContext ctx)
        {
            double score = 0;

            // [진입 빈도 증가] 거래량 조건 완화 - 1.3배부터 점진적 점수 부여
            if (ctx.VolumeMomentum >= 2.0) score = 15;       // 2배 이상 급증
            else if (ctx.VolumeMomentum >= 1.5) score = 10;  // 12→10 (1.5배도 충분)
            else if (ctx.VolumeMomentum >= 1.3) score = 5;   // 1.3배 신규 추가
            else if (ctx.VolumeRatio >= 1.2) score = 9;
            else if (ctx.VolumeRatio >= 1.0) score = 6;
            else if (ctx.VolumeRatio >= 0.8) score = 3;

            // 거래량이 평균 이하면 감점 (fake breakout 가능)
            if (ctx.VolumeRatio < 0.6) score = 0;

            return score;
        }

        private double ScoreRsiMacdForLong(TechnicalContext ctx)
        {
            double score = 0;

            // ═══ 변경: RSI 수치 → RSI 기울기(Slope) 기반 ═══
            // 기울기는 최근 4개 봉에서 RSI가 얼마나 급상승했는지를 나타냄
            // 예: RSI 20→25→30→35 = slope +15 (좋음!, 강한 상승 신호)
            // 예: RSI 50에 머물러있음 = slope ~0 (중립)
            // 예: RSI 35→30→25→20 = slope -15 (하락, 피함)
            
            double rsiSlope = ctx.GetRsiSlope();
            
            if (rsiSlope >= 10)  score += 8;    // 강한 상승 각도 (15초 앞당김 효과)
            else if (rsiSlope >= 5) score += 6; // 중간 상승
            else if (rsiSlope >= 0) score += 3; // 약한 상승
            else if (rsiSlope >= -5) score += 1; // 약간 하락
            else score = 0;                      // 강한 하락

            // ═══ MACD 기울기도 유사하게 평가 ═══
            double macdSlope = ctx.GetMacdSlope();
            
            if (macdSlope > 0.001) score += 2;     // MACD도 상승
            else if (macdSlope < -0.001) score -= 1; // MACD 하락

            return Math.Clamp(score, 0, 10);
        }

        private double ScoreBollingerForLong(TechnicalContext ctx)
        {
            double price = (double)ctx.CurrentPrice;
            double score = 0;

            // LONG 최적: 중단 ~ 하단 사이 (눌림목에서 진입)
            if (price <= ctx.BbMid && price >= ctx.BbLower)
            {
                score = 10; // 완벽한 눌림목
            }
            else if (price < ctx.BbLower)
            {
                score = 7; // 하단 이탈 → 과매도 반등 가능하나 위험
            }
            else if (price > ctx.BbMid && price < ctx.BbUpper)
            {
                score = 4; // 중단 위 → 진입 손익비 불리
            }
            else if (price >= ctx.BbUpper)
            {
                score = 0; // 상단 돌파 → 고점 추격 위험
            }

            // BB 폭이 너무 좁으면 감점 (횡보장 → 방향 불명)
            if (ctx.BbWidth < 0.5) score *= 0.5;

            return Math.Clamp(score, 0, 10);
        }

        // ══════════════════════════════════════════════════════════
        //  SHORT 개별 항목 점수 계산
        // ══════════════════════════════════════════════════════════

        private double ScoreElliottForShort(TechnicalContext ctx)
        {
            double score = 0;

            // 하락 추세 확인
            if (!ctx.IsElliottUptrend) score = 15;

            // 피보나치 0.618 하향 돌파 → 강한 하락 신호
            if (ctx.CurrentPrice < ctx.Fib618) score += 10;

            // SMA 역배열 보너스
            if (ctx.Sma20 < ctx.Sma50 && ctx.Sma50 < ctx.Sma200) score = Math.Min(score + 5, 25);

            return Math.Clamp(score, 0, 25);
        }

        private double ScoreVolumeForShort(TechnicalContext ctx)
        {
            double score = 0;

            // 매도 거래량 급증
            if (ctx.VolumeMomentum >= 2.0) score = 15;
            else if (ctx.VolumeMomentum >= 1.5) score = 12;
            else if (ctx.VolumeRatio >= 1.3) score = 9;
            else if (ctx.VolumeRatio >= 1.1) score = 6;

            return score;
        }

        private double ScoreRsiMacdForShort(TechnicalContext ctx)
        {
            double score = 0;

            // ═══ 변경: RSI 수치 → RSI 기울기(Slope) 기반 ═══
            // SHORT: RSI가 급하락할 때 ('70→60→50→40' 같은 가파른 하강)
            
            double rsiSlope = ctx.GetRsiSlope();
            
            if (rsiSlope <= -10)  score += 8;    // 강한 하락 각도 (SHORT 최적)
            else if (rsiSlope <= -5) score += 6; // 중간 하락
            else if (rsiSlope <= 0)  score += 3; // 약한 하락
            else if (rsiSlope <= 5)   score += 1; // 약간 상승
            else score = 0;                       // 강한 상승

            // ═══ MACD 기울기 ═══
            double macdSlope = ctx.GetMacdSlope();
            
            if (macdSlope < -0.001) score += 2;    // MACD도 하락
            else if (macdSlope > 0.001) score -= 1; // MACD 상승

            return Math.Clamp(score, 0, 10);
        }

        private double ScoreBollingerForShort(TechnicalContext ctx)
        {
            double price = (double)ctx.CurrentPrice;
            double score = 0;

            // SHORT 최적: 상단에서 저항 맞고 내려올 때
            if (price >= ctx.BbUpper)
            {
                score = 8; // 상단 터치 후 하락 가능
            }
            else if (price > ctx.BbMid && price < ctx.BbUpper)
            {
                score = 10; // 상단 근처에서 하락 시작 → 완벽한 숏 포지션
            }
            else if (price <= ctx.BbMid && price >= ctx.BbLower)
            {
                score = 4; // 중단 아래는 추격숏 위험
            }
            else if (price < ctx.BbLower)
            {
                score = 0; // 하단 이탈 → 반등 가능성
            }

            return Math.Clamp(score, 0, 10);
        }

        // ══════════════════════════════════════════════════════════
        //  과매도/과매수 "긴급 신호" 보너스 점수
        //  ("지옥 구경" 바닥/고점 낚시 전용)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 롱 진입용 바닥 포착 보너스
        /// 조건: RSI "투매" 25이하 AND 볼린저 밴드 하단 이탈 
        /// → 즉시 30점 부여 (지표 점수의 절반 점령)
        /// + 거래량이 평소의 1.5배 터지면 추가 5점
        /// </summary>
        public double GetOverSoldBonus(TechnicalContext ctx)
        {
            double bonusScore = 0;

            // [조건] RSI 25 이하 AND 볼린저 밴드 하단 이탈
            // 밈코인이 미쳐서 투매가 나올 때 봇이 눈을 뜨게 만듭니다.
            if (ctx.RSI <= 25 && (double)ctx.CurrentPrice <= ctx.BbLower) 
            {
                bonusScore = 30.0; // 즉시 30점 부여 (지표 점수의 절반 점령)

                // [추가 가속] 만약 여기서 거래량까지 평소보다 터지면? +5점 더!
                if (ctx.AvgVolume > 0 && ctx.Volume > ctx.AvgVolume * 1.5) 
                {
                    bonusScore += 5.0;
                }
            }

            return bonusScore;
        }

        /// <summary>
        /// 숏 진입용 고점 포착 보너스
        /// 조건: RSI "과매수" 75이상 AND 볼린저 밴드 상단 돌파
        /// → 즉시 30점 부여 (숏 진입의 결정적 근거)
        /// + 길게 달린 윗꼬리(Upper Wick)가 경고 신호면 추가 10점
        /// </summary>
        public double GetOverBoughtShortBonus(TechnicalContext ctx)
        {
            double shortBonusScore = 0;

            // [조건] RSI 75 이상 AND 볼린저 밴드 상단 돌파 (Overbought)
            // 밈코인이 미친듯이 쏴서 개미들이 달려들 때, 우리는 숏 칠 준비를 합니다.
            if (ctx.RSI >= 75 && (double)ctx.CurrentPrice >= ctx.BbUpper) 
            {
                shortBonusScore = 30.0; // 즉시 30점 부여 (숏 진입의 결정적 근거)

                // [추가 가속] 만약 여기서 윗꼬리(Upper Wick)가 길게 달리면? +10점 더!
                // 윗꼬리는 세력이 던지기 시작했다는 가장 강력한 신호입니다.
                if (ctx.Body > 0 && ctx.UpperWick > ctx.Body * 1.2) 
                {
                    shortBonusScore += 10.0;
                }
            }

            return shortBonusScore;
        }

        // ══════════════════════════════════════════════════════════
        //  메이저코인 특수 조건 (BTC, ETH, XRP, SOL)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 메이저코인(BTC/ETH/XRP/SOL) 전용 보정 점수
        /// 각 코인의 고유 변동성 패턴에 따라 점수를 보정합니다.
        /// </summary>
        public static double GetMajorCoinBonus(string symbol, string direction, TechnicalContext ctx)
        {
            string upperSymbol = symbol.ToUpperInvariant();
            double bonus = 0;

            // ─── BTC: 시장 리더, 변동성 낮음 → 추세 확인 후 진입 ───
            if (upperSymbol.StartsWith("BTC"))
            {
                bonus = GetBtcBonus(direction, ctx);
            }
            // ─── ETH: BTC 후행, 기술적 패턴 정확도 높음 ───
            else if (upperSymbol.StartsWith("ETH"))
            {
                bonus = GetEthBonus(direction, ctx);
            }
            // ─── XRP: 고유 변동성 (급등/급락 패턴), 거래량 의존도 높음 ───
            else if (upperSymbol.StartsWith("XRP"))
            {
                bonus = GetXrpBonus(direction, ctx);
            }
            // ─── SOL: 고베타, 추세 추종 유리 ───
            else if (upperSymbol.StartsWith("SOL"))
            {
                bonus = GetSolBonus(direction, ctx);
            }

            return bonus;
        }

        public static bool IsMajorCoin(string symbol)
        {
            var s = symbol.ToUpperInvariant();
            return s.StartsWith("BTC") || s.StartsWith("ETH") || s.StartsWith("XRP") || s.StartsWith("SOL");
        }

        // ─── BTC 특수 조건 ───
        private static double GetBtcBonus(string direction, TechnicalContext ctx)
        {
            double bonus = 0;

            if (direction == "LONG")
            {
                // BTC는 SMA200 위에서만 강한 상승 신호
                if (ctx.CurrentPrice > (decimal)ctx.Sma200) bonus += 3;
                // BTC 하락장에서 LONG은 위험 → 페널티
                if (ctx.Sma20 < ctx.Sma50 && ctx.Sma50 < ctx.Sma200) bonus -= 5;
                // BB 중단 이상에서 거래량 동반 시 신뢰도 상승
                if ((double)ctx.CurrentPrice > ctx.BbMid && ctx.VolumeRatio >= 1.3) bonus += 2;
            }
            else // SHORT
            {
                // BTC 숏은 매우 보수적으로 (시장 전체 하락일 때만)
                if (ctx.Sma20 < ctx.Sma50 && ctx.Sma50 < ctx.Sma200) bonus += 3;
                else bonus -= 3; // 상승장 BTC 숏은 감점
            }

            return bonus;
        }

        // ─── ETH 특수 조건 ───
        private static double GetEthBonus(string direction, TechnicalContext ctx)
        {
            double bonus = 0;

            if (direction == "LONG")
            {
                // ETH는 기술적 패턴이 잘 먹힘 → 엘리엇+RSI 조합 보너스
                if (ctx.IsElliottUptrend && ctx.RSI >= 40 && ctx.RSI <= 60) bonus += 3;
                // BB 하단 바운스 패턴
                if ((double)ctx.CurrentPrice <= ctx.BbLower * 1.01) bonus += 2;
            }
            else // SHORT
            {
                // ETH는 BTC 하락 시 더 큰 폭 하락 → BTC 연동 고려
                if (ctx.MacdHist < -0.001) bonus += 2;
                if (ctx.RSI > 75) bonus += 3; // 과매수 후 하락
            }

            return bonus;
        }

        // ─── XRP 특수 조건 (고유 변동성 패턴) ───
        private static double GetXrpBonus(string direction, TechnicalContext ctx)
        {
            double bonus = 0;

            if (direction == "LONG")
            {
                // XRP는 거래량 급증 시 급등 패턴 강함
                if (ctx.VolumeMomentum >= 2.5) bonus += 5;   // 직전 대비 2.5배 → 강한 신호
                else if (ctx.VolumeMomentum >= 1.8) bonus += 3;

                // XRP 피보나치 바운스 (0.382에서 반등 패턴 빈번)
                if (ctx.CurrentPrice >= ctx.Fib382 * 0.99m && ctx.CurrentPrice <= ctx.Fib382 * 1.02m)
                    bonus += 3; // Fib 0.382 근처 지지 확인

                // 볼린저 하단 + 거래량 급증 → XRP 특유 반등
                if ((double)ctx.CurrentPrice <= ctx.BbLower * 1.005 && ctx.VolumeRatio >= 1.5)
                    bonus += 3;
            }
            else // SHORT
            {
                // XRP 숏은 급락 시에만 (거래량 동반 하락)
                if (ctx.VolumeMomentum >= 2.0 && ctx.MacdHist < -0.002) bonus += 4;
                // RSI 80+ 후 하락 전환 → XRP 특유의 급락 패턴
                if (ctx.RSI >= 80) bonus += 3;
                // 피보나치 0.618 하향 이탈 + 거래량
                if (ctx.CurrentPrice < ctx.Fib618 && ctx.VolumeRatio >= 1.3) bonus += 3;
            }

            return bonus;
        }

        // ─── SOL 특수 조건 (고베타, 추세 추종) ───
        private static double GetSolBonus(string direction, TechnicalContext ctx)
        {
            double bonus = 0;

            if (direction == "LONG")
            {
                // SOL은 추세가 강할 때 가속도가 붙음
                if (ctx.Sma20 > ctx.Sma50 && ctx.Sma50 > ctx.Sma200) bonus += 3;
                // 거래량 + 추세 동시 확인 시 고베타 프리미엄
                if (ctx.VolumeMomentum >= 1.5 && ctx.IsElliottUptrend) bonus += 3;
                // MACD 골든크로스 부근
                if (ctx.MacdHist > 0 && ctx.MacdLine > ctx.MacdSignal) bonus += 2;
            }
            else // SHORT
            {
                // SOL 하락장에서도 고베타 → 숏 수익 극대화 가능
                if (ctx.Sma20 < ctx.Sma50 && ctx.Sma50 < ctx.Sma200) bonus += 3;
                // 거래량 동반 하락
                if (ctx.VolumeMomentum >= 1.5 && !ctx.IsElliottUptrend) bonus += 3;
            }

            return bonus;
        }

        // ── 유틸 ──
        private string GetBBPosition(TechnicalContext ctx)
        {
            double p = (double)ctx.CurrentPrice;
            if (p >= ctx.BbUpper) return "Upper";
            if (p <= ctx.BbLower) return "Lower";
            if (p >= ctx.BbMid) return "Above_Mid";
            return "Below_Mid";
        }
    }
}
