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
    ///   - RSI & MACD 위치 (10점)
    ///   - 볼린저 밴드 위치 (10점)
    /// ─────────────────────────────────────────
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
        public const double LONG_APPROVAL_THRESHOLD = 65.0;  // 75→65 하향 (진입 빈도 증가)
        public const double SHORT_APPROVAL_THRESHOLD = 65.0;

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

            // RSI
            public double RSI { get; set; }

            // MACD
            public double MacdHist { get; set; }
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

            result.FinalScore = Math.Clamp(
                result.AiPredictionScore +
                result.ElliottWaveScore +
                result.VolumeMomentumScore +
                result.RsiMacdScore +
                result.BollingerScore,
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

            result.FinalScore = Math.Clamp(
                result.AiPredictionScore +
                result.ElliottWaveScore +
                result.VolumeMomentumScore +
                result.RsiMacdScore +
                result.BollingerScore,
                0, 100);

            return result;
        }

        // ══════════════════════════════════════════════════════════
        //  LONG 개별 항목 점수 계산
        // ══════════════════════════════════════════════════════════

        private double ScoreElliottForLong(TechnicalContext ctx)
        {
            double score = 0;

            // [선취매 관점] Wave3Entry는 이미 가격 상승 후 → 점수 하향
            // Setup/Wave2 단계에서 미리 잡는 것이 20배 레버리지에 유리
            if (ctx.ElliottPhase == "Wave3Entry") score = 15;      // 25→15 하향 (늦은 진입)
            else if (ctx.ElliottPhase == "Wave3Setup") score = 20; // 18→20 상향 (준비 단계)
            else if (ctx.ElliottPhase == "Wave3Active") score = 15;
            else if (ctx.ElliottPhase == "Wave2Started") score = 15; // 10→15 상향 (눌림목)
            else if (ctx.IsElliottUptrend) score = 8;

            // RSI 다이버전스 보너스 (Wave2 눌림목에서 강세 다이버전스)
            if (ctx.RsiDivergence > 0 && ctx.ElliottPhase == "Wave2Started") score += 10;

            // SMA 정배열 보너스 (엘리엇 방향 확인)
            if (ctx.Sma20 > ctx.Sma50 && ctx.Sma50 > ctx.Sma200) score = Math.Min(score + 5, 25);

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

            // RSI: 40~70 (과열 아닌 구간) → 정상 진입
            if (ctx.RSI >= 40 && ctx.RSI <= 65) score += 5;
            else if (ctx.RSI > 70) score -= 3;  // 과매수 → 감점
            else if (ctx.RSI < 30) score += 2;  // 극도 과매도에서 반등 가능

            // MACD 히스토그램: 0선 위이거나, 음수 폭이 줄어드는 중
            if (ctx.MacdHist > 0) score += 5;
            else if (ctx.MacdHist >= -0.0005) score += 3; // 0선 근접 (반전 직전)

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

            // RSI 하락 다이버전스 발생 → 최대 점수
            if (ctx.RsiDivergence < 0) score += 5;

            // RSI > 70 이후 하락 전환 → 숏 기회
            if (ctx.RSI >= 70) score += 3;
            else if (ctx.RSI >= 55 && ctx.RSI <= 70) score += 2;
            // RSI < 35 → 이미 과매도, 숏 추격 위험
            if (ctx.RSI < 35) score -= 5;

            // MACD 하락
            if (ctx.MacdHist < 0) score += 5;

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
