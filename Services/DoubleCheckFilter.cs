using System;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Services.AI;

namespace TradingBot.Services
{
    /// <summary>
    /// DoubleCheck AI 필터
    /// ═══════════════════════════════════════════════════════
    /// 
    /// 기술적 분석 점수(AI Score)와 Transformer 숏 스퀴즈 확률을
    /// 동시 검증하여 20배 레버리지 진입을 더블 체크합니다.
    /// 
    /// [Engine 1] Technical Score: ML.NET 기반 기술적 지표 점수 (0~100)
    /// [Engine 2] Transformer Probability: 숏 스퀴즈/가격 상승 확률 (0~1)
    /// 
    /// 진입 승인 조건:
    /// ─ 일반 추세장: TechScore ≥ 65 AND Transformer > 0.60
    /// ─ 횡보장(지표 약함): TechScore ≥ 50 AND Transformer > 0.75 (AI가 높은 확률일 때 보완)
    /// ─ 강력 추세: TechScore ≥ 80 → Transformer 조건 완화 (> 0.50)
    /// 
    /// 이 설계로:
    /// - 한쪽 엔진의 실수(가짜 돌파)를 다른 엔진이 잡아줌
    /// - 횡보장(2파)에서 OI 축적 패턴을 포착한 Transformer가 진입 기회를 열어줌
    /// - 20배 레버리지 청산 위험 최소화
    /// </summary>
    public class DoubleCheckFilter
    {
        // 일반 추세장 임계값
        public float TechScoreThreshold { get; set; } = 65f;
        public float TransformerThreshold { get; set; } = 0.60f;

        // 횡보장 (기술 지표 약할 때) AI 보완 임계값
        public float SidewaysTechMin { get; set; } = 50f;
        public float SidewaysTransformerThreshold { get; set; } = 0.75f;

        // 강력 추세 (기술 지표 강할 때) Transformer 완화 임계값
        public float StrongTechThreshold { get; set; } = 80f;
        public float StrongTransformerMin { get; set; } = 0.50f;

        // 숏 진입 전용 임계값
        public float ShortTechThreshold { get; set; } = 70f;
        public float ShortTransformerThreshold { get; set; } = 0.65f;

        public event Action<string>? OnLog;

        /// <summary>
        /// 더블 체크 진입 검증.
        /// </summary>
        /// <param name="symbol">심볼</param>
        /// <param name="direction">LONG 또는 SHORT</param>
        /// <param name="techScore">기술적 분석 점수 (0~100)</param>
        /// <param name="transformerProbability">Transformer 예측 확률 (0~1, 상승 확률)</param>
        /// <param name="isSidewaysMarket">횡보장 여부 (ADX < 20 등)</param>
        /// <param name="oiChangePct">현재 OI 변화율 (%)</param>
        /// <returns>진입 승인 결과</returns>
        public DoubleCheckResult Evaluate(
            string symbol,
            string direction,
            float techScore,
            float transformerProbability,
            bool isSidewaysMarket,
            float oiChangePct = 0f)
        {
            var result = new DoubleCheckResult
            {
                Symbol = symbol,
                Direction = direction,
                TechScore = techScore,
                TransformerProbability = transformerProbability,
                IsSideways = isSidewaysMarket,
                OiChangePct = oiChangePct
            };

            // ═══════ LONG 진입 검증 ═══════
            if (direction == "LONG")
            {
                return EvaluateLong(result);
            }
            
            // ═══════ SHORT 진입 검증 ═══════
            if (direction == "SHORT")
            {
                return EvaluateShort(result);
            }

            result.IsApproved = false;
            result.Reason = "유효하지 않은 방향";
            return result;
        }

        private DoubleCheckResult EvaluateLong(DoubleCheckResult result)
        {
            float tech = result.TechScore;
            float tfProb = result.TransformerProbability;

            // [케이스 1] 강력 추세: 기술 점수 80+ → Transformer 완화
            if (tech >= StrongTechThreshold)
            {
                if (tfProb >= StrongTransformerMin)
                {
                    result.IsApproved = true;
                    result.Reason = $"🎯 [DOUBLE ✅] 강력 추세 | Tech={tech:F1} ≥ {StrongTechThreshold} + AI={tfProb:P1} ≥ {StrongTransformerMin:P0}";
                    result.ApprovedBy = "STRONG_TREND";
                    OnLog?.Invoke(result.Reason);
                    return result;
                }
                // Tech 높지만 AI 너무 낮으면 경고
                result.IsApproved = false;
                result.Reason = $"⚠️ Tech={tech:F1} 강하지만 AI={tfProb:P1} < {StrongTransformerMin:P0} 경고";
                OnLog?.Invoke(result.Reason);
                return result;
            }

            // [케이스 2] 횡보장: 기술 점수 약해도 AI가 높으면 승인
            if (result.IsSideways)
            {
                if (tech >= SidewaysTechMin && tfProb >= SidewaysTransformerThreshold)
                {
                    result.IsApproved = true;
                    result.Reason = $"🤖 [AI 보완] 횡보장 스퀴즈 감지 | Tech={tech:F1} + AI={tfProb:P1} ≥ {SidewaysTransformerThreshold:P0}";
                    result.ApprovedBy = "SIDEWAYS_AI_OVERRIDE";

                    // OI 축적 패턴 추가 확인
                    if (result.OiChangePct < -0.5f)
                    {
                        result.Reason += $" + OI 급감({result.OiChangePct:F2}%) → 숏 스퀴즈 확률 높음";
                    }

                    OnLog?.Invoke(result.Reason);
                    return result;
                }

                result.IsApproved = false;
                result.Reason = $"❌ 횡보장 미달 | Tech={tech:F1} (min {SidewaysTechMin}) + AI={tfProb:P1} (min {SidewaysTransformerThreshold:P0})";
                OnLog?.Invoke(result.Reason);
                return result;
            }

            // [케이스 3] 일반 추세장: 더블 체크 (AND 조건)
            bool isTechOk = tech >= TechScoreThreshold;
            bool isAiOk = tfProb >= TransformerThreshold;

            if (isTechOk && isAiOk)
            {
                result.IsApproved = true;
                result.Reason = $"✅ [DOUBLE ✅] Tech={tech:F1} ≥ {TechScoreThreshold} AND AI={tfProb:P1} ≥ {TransformerThreshold:P0}";
                result.ApprovedBy = "DOUBLE_CHECK";
                OnLog?.Invoke(result.Reason);
                return result;
            }

            // 실패 사유 상세
            if (isTechOk && !isAiOk)
            {
                result.Reason = $"⚠️ 지표 OK({tech:F1}) but AI 낮음({tfProb:P1} < {TransformerThreshold:P0})";
            }
            else if (!isTechOk && isAiOk)
            {
                result.Reason = $"⚠️ AI OK({tfProb:P1}) but 지표 낮음({tech:F1} < {TechScoreThreshold})";
            }
            else
            {
                result.Reason = $"❌ 모두 미달 | Tech={tech:F1} < {TechScoreThreshold}, AI={tfProb:P1} < {TransformerThreshold:P0}";
            }

            result.IsApproved = false;
            OnLog?.Invoke(result.Reason);
            return result;
        }

        private DoubleCheckResult EvaluateShort(DoubleCheckResult result)
        {
            float tech = result.TechScore;
            // SHORT의 경우 transformerProbability가 낮을수록(하락 예측) 좋음
            // 따라서 (1 - probability)를 사용
            float downProb = 1f - result.TransformerProbability;

            bool isTechOk = tech >= ShortTechThreshold;
            bool isAiOk = downProb >= ShortTransformerThreshold;

            if (isTechOk && isAiOk)
            {
                result.IsApproved = true;
                result.Reason = $"✅ [SHORT ✅] Tech={tech:F1} ≥ {ShortTechThreshold} + 하락확률={downProb:P1} ≥ {ShortTransformerThreshold:P0}";
                result.ApprovedBy = "DOUBLE_CHECK_SHORT";
                OnLog?.Invoke(result.Reason);
                return result;
            }

            result.IsApproved = false;
            result.Reason = $"❌ SHORT 미달 | Tech={tech:F1} (min {ShortTechThreshold}) + 하락확률={downProb:P1} (min {ShortTransformerThreshold:P0})";
            OnLog?.Invoke(result.Reason);
            return result;
        }
    }

    /// <summary>
    /// Double Check 결과
    /// </summary>
    public class DoubleCheckResult
    {
        public string Symbol { get; set; } = "";
        public string Direction { get; set; } = "";
        public bool IsApproved { get; set; }
        public string Reason { get; set; } = "";
        public string ApprovedBy { get; set; } = "";

        // 상세 점수
        public float TechScore { get; set; }
        public float TransformerProbability { get; set; }
        public bool IsSideways { get; set; }
        public float OiChangePct { get; set; }
    }
}
