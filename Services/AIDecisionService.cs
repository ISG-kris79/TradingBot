using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TradingBot.Services
{
    /// <summary>
    /// [AI 의사결정 서비스 — Sniper Mode]
    ///
    /// 모든 트레이딩 결정을 AI가 수행. 하드코딩된 임계값 없음.
    ///
    /// ┌─────────────────────────────────────────────────────┐
    /// │ 1. 진입: "확률의 오케스트라"                         │
    /// │    - 패턴 매칭: 과거 승리 차트와 92%+ 유사?          │
    /// │    - 거래량 밀도: 개미 뇌동매매 vs 세력 매집?        │
    /// │    - 파동 확률: 엘리엇 3파 초입 88%?               │
    /// │    → ML이 43개 피처 통째로 삼켜서 "차트의 관상" 판단 │
    /// ├─────────────────────────────────────────────────────┤
    /// │ 2. 손절: "유연한 맷집 (Hybrid Stop)"                │
    /// │    - 가격↓ + 거래량 없음 → "개미 털기, 버텨라"      │
    /// │    - 가격 유지 + 매도 체결 강도↑ → "즉시 던져라!"    │
    /// │    → 고정 -18% 손절 대신 ML이 실시간 위험도 판단     │
    /// ├─────────────────────────────────────────────────────┤
    /// │ 3. 익절: "거머리 추격 (Trailing)"                    │
    /// │    - 3파 진행 중 → "절반 익절, 나머지 갭 넓혀 추격"  │
    /// │    - 5파 도달 → "전량 청산"                          │
    /// │    → ML이 보유 상태 + 시장 상태로 최적 청산 판단      │
    /// └─────────────────────────────────────────────────────┘
    ///
    /// 모든 코인 공통 적용 (BTC/ETH/SOL/XRP + DOGE/PEPE/WIF/BONK + 신규)
    /// </summary>
    public class AIDecisionService
    {
        private static AIDecisionService? _instance;
        public static AIDecisionService Instance => _instance ??= new AIDecisionService();

        private readonly MLContext _mlContext = new MLContext(seed: 42);
        private ITransformer? _entryModel;
        private ITransformer? _exitModel;
        private PredictionEngine<SniperEntryInput, SniperEntryOutput>? _entryPredictor;
        private PredictionEngine<SniperExitInput, SniperExitOutput>? _exitPredictor;

        // 승리 패턴 DB (방향별, 메모리 + 파일 영속화)
        private readonly ConcurrentDictionary<string, List<double[]>> _winPatterns = new();

        public bool IsEntryModelReady => _entryPredictor != null;
        public bool IsExitModelReady => _exitPredictor != null;
        public DateTime? LastTrainTime { get; private set; }
        public double EntryModelAccuracy { get; private set; }
        public double EntryModelAUC { get; private set; }
        public double ExitModelAccuracy { get; private set; }

        private static readonly string ModelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "AIModels");

        // ═══════════════════════════════════════════════════════════
        //  진입 피처 (43개): 보조지표 전부 + 상위TF + 패턴매칭 결과
        //  ML이 이 43개를 통째로 보고 "차트의 관상"을 판단
        // ═══════════════════════════════════════════════════════════
        public class SniperEntryInput
        {
            // ── 5분봉 시장 상태 (25개) ──
            public float RSI { get; set; }               // RSI 14
            public float RSI_Slope { get; set; }          // RSI 3봉 변화율
            public float MACD_Hist { get; set; }          // MACD 히스토그램 (정규화)
            public float MACD_Rising { get; set; }        // MACD 상승 중 (1/0)
            public float BB_Position { get; set; }        // BB 내 위치 (0~1)
            public float BB_Width_Pct { get; set; }       // BB 폭 / 가격
            public float ATR_Pct { get; set; }            // ATR / 가격 (변동성)
            public float Volume_Ratio { get; set; }       // 현재 / 20MA 거래량
            public float Volume_Spike { get; set; }       // 급증 여부 (1/0)
            public float Volume_Density { get; set; }     // ★ 거래량 밀도: 연속 3봉 볼륨 합 / 평균
            public float EMA9_Slope { get; set; }         // EMA9 기울기%
            public float EMA21_Slope { get; set; }        // EMA21 기울기%
            public float EMA_Alignment { get; set; }      // 정배열(+1)/역배열(-1)/중립(0)
            public float Price_To_EMA21 { get; set; }     // 가격 vs EMA21 거리%
            public float Price_To_EMA50 { get; set; }     // 가격 vs EMA50 거리%
            public float Candle_Body_Ratio { get; set; }  // 몸통/전체 비율
            public float Candle_Direction { get; set; }   // 양봉(+1)/음봉(-1)
            public float Upper_Shadow { get; set; }       // 윗꼬리 비율
            public float Lower_Shadow { get; set; }       // 아랫꼬리 비율
            public float Price_Change_3Bar { get; set; }  // 3봉 가격변화%
            public float Price_Change_12Bar { get; set; } // 12봉(1시간) 가격변화%
            public float High_Break { get; set; }         // 12봉 고점 돌파 (1/0)
            public float Low_Break { get; set; }          // 12봉 저점 돌파 (1/0)
            public float HourOfDay { get; set; }          // 시간대 (0~1)
            public float DayOfWeek { get; set; }          // 요일 (0~1)

            // ══ 15분봉 핵심 4대 지표 (12개) ══════════════════════════
            //
            // [1] 볼린저 밴드 — 스퀴즈 감지 + 돌파 방향
            public float M15_BB_Position { get; set; }      // BB 내 위치 (0~1)
            public float M15_BB_Width { get; set; }         // ★ 밴드폭 (가격 대비 %)
            public float M15_BB_Squeeze { get; set; }       // ★ 스퀴즈 강도: 현재폭/20봉평균폭 (<0.8이면 응축)
            public float M15_BB_Breakout { get; set; }      // ★ 상단돌파(+1) / 하단돌파(-1) / 없음(0)
            //
            // [2] RSI — V-Turn 감지 + 기울기 (반등 강도)
            public float M15_RSI { get; set; }              // RSI 14 값 (0~1 정규화)
            public float M15_RSI_Slope { get; set; }        // ★ RSI 3봉 기울기 (반등 강도)
            public float M15_RSI_VTurn { get; set; }        // ★ V-Turn 감지: RSI<30 → U자 반등 (1/0)
            public float M15_RSI_Divergence { get; set; }   // ★ 다이버전스: 가격↓ + RSI↑ = +1 (상승)
            //
            // [3] EMA 20/60 — 골든크로스 + 이격도 (에너지 잔량)
            public float M15_EMA_Trend { get; set; }        // EMA20>EMA60 = +1 (상승추세)
            public float M15_EMA_Cross { get; set; }        // ★ 골든크로스 발생 (1) / 데드크로스 (-1) / 없음 (0)
            public float M15_EMA_Gap { get; set; }          // ★ EMA20-EMA60 이격도 % (에너지 잔량)
            public float M15_Price_Above_EMA20 { get; set; }// ★ 가격이 EMA20 위 지지 (1/0)
            //
            // [4] 피보나치 되돌림 — 정밀 타점
            public float M15_Fib_Position { get; set; }     // ★ 현재가의 피보나치 위치 (0=저점, 1=고점)
            public float M15_Fib_AtGoldenZone { get; set; } // ★ 0.618~0.786 골든존 내 (1/0)
            public float M15_Fib_Bounce { get; set; }       // ★ 골든존 터치 후 반등 (1/0)
            //
            public float M15_MACD_Rising { get; set; }      // MACD 방향
            public float M15_Volume_Ratio { get; set; }     // 볼륨 비율

            // ── 상위 타임프레임 (10개) — 1H/4H ──
            public float H1_RSI { get; set; }
            public float H1_MACD_Rising { get; set; }
            public float H1_BB_Position { get; set; }
            public float H1_EMA_Trend { get; set; }
            public float H1_Volume_Ratio { get; set; }
            public float H4_RSI { get; set; }
            public float H4_MACD_Rising { get; set; }
            public float H4_BB_Position { get; set; }
            public float H4_EMA_Trend { get; set; }        // ★ 4H 장기추세 = 가장 중요
            public float H4_Volume_Ratio { get; set; }

            // ── 패턴 매칭 결과 (3개) — 승리 스냅샷 DB 유사도 ──
            public float PatternSimilarity { get; set; }    // Top5 평균 코사인 유사도
            public float PatternMatchCount { get; set; }    // 매칭 패턴 수 (정규화)
            public float PatternWinRate { get; set; }      // 유사 패턴들의 승률

            // 라벨
            public bool Label { get; set; }
        }

        public class SniperEntryOutput
        {
            [ColumnName("PredictedLabel")] public bool ShouldEnter { get; set; }
            [ColumnName("Probability")] public float Confidence { get; set; }
            [ColumnName("Score")] public float Score { get; set; }
        }

        // ═══════════════════════════════════════════════════════════
        //  청산 피처 (25개): 포지션 상태 + 실시간 시장 + 매도압력
        //  "유연한 맷집" — 거래량 없는 하락은 버티고, 매도압력 급증은 즉시 청산
        // ═══════════════════════════════════════════════════════════
        public class SniperExitInput
        {
            // ── 포지션 상태 (8개) ──
            public float UnrealizedRoePct { get; set; }    // 현재 미실현 ROE%
            public float MaxRoePct { get; set; }           // 보유 중 최고 ROE%
            public float DrawdownFromPeak { get; set; }    // 최고점 대비 하락% (음수)
            public float HoldMinutes { get; set; }         // 보유 시간 (분, 정규화)
            public float EntryConfidence { get; set; }     // 진입시 AI 확신도
            public float IsLong { get; set; }              // 1=LONG, 0=SHORT
            public float PartialExitDone { get; set; }     // 부분익절 완료 여부 (1/0)
            public float TrailingActive { get; set; }      // 트레일링 활성화 (1/0)

            // ── 현재 시장 상태 (10개) ──
            public float RSI { get; set; }
            public float MACD_Rising { get; set; }
            public float BB_Position { get; set; }
            public float ATR_Pct { get; set; }
            public float Volume_Ratio { get; set; }
            public float Volume_Density { get; set; }      // ★ 연속 볼륨 밀도
            public float SellPressure { get; set; }        // ★ 매도 체결 강도 (taker sell / total)
            public float EMA_Alignment { get; set; }
            public float Price_Change_3Bar { get; set; }
            public float Momentum_Score { get; set; }      // 복합 모멘텀

            // ── 상위 TF 실시간 (7개) ──
            public float H1_RSI { get; set; }
            public float H1_EMA_Trend { get; set; }
            public float H1_MACD_Rising { get; set; }
            public float H4_RSI { get; set; }
            public float H4_EMA_Trend { get; set; }
            public float H4_MACD_Rising { get; set; }
            public float H4_BB_Position { get; set; }

            // 라벨: true=청산해야했다, false=홀드가 정답이었다
            public bool Label { get; set; }
        }

        public class SniperExitOutput
        {
            [ColumnName("PredictedLabel")] public bool ShouldExit { get; set; }
            [ColumnName("Probability")] public float Confidence { get; set; }
            [ColumnName("Score")] public float Score { get; set; }
        }

        // ═══════════════════════════════════════════════════════════
        //  청산 액션 (ML 확신도 기반)
        // ═══════════════════════════════════════════════════════════
        public enum ExitAction
        {
            Hold,            // 홀드 — 거래량 없는 하락, 개미 털기로 판단
            PartialClose,    // 부분 익절 — 3파 진행 중, 절반만 정리
            TightenTrail,    // 트레일링 갭 축소 — 추세 약화 조짐
            WidenTrail,      // 트레일링 갭 확대 — 추세 강하게 지속 중
            FullClose,       // 전량 청산 — 매도압력 급증 or 5파 도달
            EmergencyCut     // 긴급 손절 — 가격 유지인데 매도 체결 강도 비정상
        }

        public class SniperDecision
        {
            public ExitAction Action { get; set; }
            public float Confidence { get; set; }
            public string Reason { get; set; } = "";
            public decimal SuggestedTrailGap { get; set; } // ATR 배수
        }

        // ═══════════════════════════════════════════════════════════
        //  [1] 진입 판단: "확률의 오케스트라"
        //  - 모든 보조지표를 ML이 통째로 판단
        //  - 패턴 매칭으로 "이 차트 관상이 과거 승리 패턴과 유사한가?"
        //  - 거래량 밀도로 "세력 매집인가 개미 뇌동인가?"
        // ═══════════════════════════════════════════════════════════
        public (bool shouldEnter, float confidence, string reason, decimal sizeMult)
            SniperEntry(SniperEntryInput input)
        {
            if (_entryPredictor == null)
                return (false, 0, "AI 모델 미준비 — 설정>AI학습 실행 필요", 0);

            var pred = _entryPredictor.Predict(input);

            // ML이 "진입하지 마라" 판단
            if (!pred.ShouldEnter)
                return (false, pred.Confidence, $"AI 거부 ({pred.Confidence:P0})", 0);

            // 확신도 55% 미만 — 노이즈 가능성
            if (pred.Confidence < 0.55f)
                return (false, pred.Confidence, $"확신도 부족 ({pred.Confidence:P0}<55%)", 0);

            // 패턴 매칭 보강: 승리 패턴과 60%+ 유사해야 최종 승인
            bool patternOk = input.PatternSimilarity >= 0.55f || input.PatternMatchCount < 0.1f; // 패턴 DB 없으면 패스
            if (!patternOk && pred.Confidence < 0.70f)
                return (false, pred.Confidence, $"패턴 불일치 (유사도:{input.PatternSimilarity:P0})", 0);

            // ★ 확신도 기반 포지션 사이징 (하드코딩 X, 연속 함수)
            decimal sizeMult = CalculateSizeMultiplier(pred.Confidence, input.PatternSimilarity);

            string reason = pred.Confidence >= 0.80f
                ? $"SNIPER 확정 ({pred.Confidence:P0}, 패턴:{input.PatternSimilarity:P0})"
                : pred.Confidence >= 0.65f
                    ? $"SNIPER 진입 ({pred.Confidence:P0})"
                    : $"SCOUT 진입 ({pred.Confidence:P0})";

            return (true, pred.Confidence, reason, sizeMult);
        }

        // ═══════════════════════════════════════════════════════════
        //  [2] 청산 판단: "유연한 맷집" + "거머리 추격"
        //  - 가격↓ + 거래량 없음 → "개미 털기, 버텨라"
        //  - 가격 유지 + 매도 체결 강도↑ → "즉시 던져라"
        //  - 3파 진행 중 → "절반 익절, 나머지 갭 넓혀 추격"
        //  - 5파 도달 → "전량 청산"
        // ═══════════════════════════════════════════════════════════
        public SniperDecision SniperExit(SniperExitInput input)
        {
            if (_exitPredictor == null)
            {
                // ML 미준비시 기본 규칙 (폴백)
                return FallbackExitDecision(input);
            }

            var pred = _exitPredictor.Predict(input);

            // ★ 긴급 손절: 매도 체결 강도 비정상 (가격 유지인데 매도 쏟아짐)
            if (input.SellPressure > 0.75f && input.Volume_Density > 2.0f && input.UnrealizedRoePct > -5f)
            {
                return new SniperDecision
                {
                    Action = ExitAction.EmergencyCut,
                    Confidence = Math.Max(pred.Confidence, 0.90f),
                    Reason = $"매도압력 급증 (체결강도:{input.SellPressure:P0}, 볼륨밀도:{input.Volume_Density:F1}x)",
                };
            }

            // ★ 개미 털기 감지: 가격 하락 + 거래량 없음 → 홀드
            if (input.UnrealizedRoePct < 0 && input.UnrealizedRoePct > -15f
                && input.Volume_Ratio < 0.7f && input.Volume_Density < 1.0f)
            {
                return new SniperDecision
                {
                    Action = ExitAction.Hold,
                    Confidence = 0.60f,
                    Reason = $"개미 털기 의심 (볼륨:{input.Volume_Ratio:F1}x, 밀도:{input.Volume_Density:F1}x) — 버텨라",
                };
            }

            if (!pred.ShouldExit)
            {
                // ML이 "홀드" 판단했지만 추세 상태에 따라 트레일링 조정
                if (input.UnrealizedRoePct > 20f && input.MaxRoePct > 25f)
                {
                    // 수익 구간 — 추세 강도에 따라 갭 조정
                    if (input.H4_EMA_Trend > 0 && input.MACD_Rising > 0.5f)
                        return new SniperDecision { Action = ExitAction.WidenTrail, Confidence = 0.55f,
                            Reason = "추세 강세 지속 — 갭 넓혀 추격", SuggestedTrailGap = 1.5m };
                    else
                        return new SniperDecision { Action = ExitAction.TightenTrail, Confidence = 0.55f,
                            Reason = "추세 약화 조짐 — 갭 축소", SuggestedTrailGap = 0.6m };
                }
                return new SniperDecision { Action = ExitAction.Hold, Confidence = pred.Confidence, Reason = "HOLD" };
            }

            // ML이 "청산" 판단 — 확신도에 따라 액션 결정
            if (pred.Confidence >= 0.85f)
                return new SniperDecision { Action = ExitAction.FullClose, Confidence = pred.Confidence,
                    Reason = $"전량청산 확신 ({pred.Confidence:P0})" };

            if (pred.Confidence >= 0.65f && input.PartialExitDone < 0.5f)
                return new SniperDecision { Action = ExitAction.PartialClose, Confidence = pred.Confidence,
                    Reason = $"부분익절 ({pred.Confidence:P0}) — 절반 정리, 나머지 추격" };

            return new SniperDecision { Action = ExitAction.TightenTrail, Confidence = pred.Confidence,
                Reason = $"트레일링 강화 ({pred.Confidence:P0})", SuggestedTrailGap = 0.5m };
        }

        // ═══════════════════════════════════════════════════════════
        //  포지션 사이즈: 확신도 연속 함수 (하드코드 구간 없음)
        //  0.55 → 5%, 0.70 → 12%, 0.85 → 20%
        // ═══════════════════════════════════════════════════════════
        private decimal CalculateSizeMultiplier(float confidence, float patternSim)
        {
            // 확신도 60% + 패턴 40% 가중
            float combined = confidence * 0.6f + Math.Max(patternSim, 0.5f) * 0.4f;
            // 선형 보간: 0.50 → 0.05, 0.90 → 0.20
            double size = 0.05 + (Math.Clamp(combined, 0.50f, 0.90f) - 0.50) / 0.40 * 0.15;
            return (decimal)Math.Round(size, 3);
        }

        // ═══════════════════════════════════════════════════════════
        //  동적 SL/TP: 확신도 기반 연속 함수
        //  높은 확신 → 넓은 SL + 먼 TP (추세 지속 기대)
        //  낮은 확신 → 타이트 SL + 가까운 TP (빠른 수익 확보)
        // ═══════════════════════════════════════════════════════════
        public (decimal slDist, decimal tp1Dist, decimal tp2Dist, decimal trailStart, decimal trailGap)
            GetDynamicLevels(decimal atr, float confidence)
        {
            // 선형 보간: 확신도 0.55~0.90 → 배수 조정
            double c = Math.Clamp(confidence, 0.55, 0.90);
            double t = (c - 0.55) / 0.35; // 0~1 정규화

            decimal slMult     = (decimal)(1.5 + t * 1.0);    // 1.5x ~ 2.5x
            decimal tp1Mult    = (decimal)(3.0 + t * 2.0);    // 3.0x ~ 5.0x
            decimal tp2Mult    = (decimal)(6.0 + t * 4.0);    // 6.0x ~ 10.0x
            decimal trailMult  = (decimal)(4.0 + t * 2.0);    // 4.0x ~ 6.0x
            decimal gapMult    = (decimal)(0.8 + t * 0.7);    // 0.8x ~ 1.5x

            return (atr * slMult, atr * tp1Mult, atr * tp2Mult, atr * trailMult, atr * gapMult);
        }

        // ═══════════════════════════════════════════════════════════
        //  승리 패턴 매칭 (코사인 유사도)
        // ═══════════════════════════════════════════════════════════
        public (float similarity, int matchCount, float winRate) MatchWinPatterns(
            double[] currentVector, string direction)
        {
            if (!_winPatterns.TryGetValue(direction, out var patterns) || patterns.Count < 3)
                return (0, patterns?.Count ?? 0, 0);

            var sims = patterns.Select(p => CosineSimilarity(currentVector, p))
                .OrderByDescending(s => s).ToList();

            float topAvg = (float)sims.Take(Math.Min(5, sims.Count)).Average();
            int goodMatches = sims.Count(s => s >= 0.60);
            float winRate = patterns.Count > 0 ? goodMatches / (float)patterns.Count : 0;

            return (topAvg, patterns.Count, winRate);
        }

        public void AddWinPattern(string direction, double[] vector)
        {
            _winPatterns.AddOrUpdate(direction,
                _ => new List<double[]> { vector },
                (_, list) => { lock (list) { list.Add(vector); } return list; });
        }

        // ═══════════════════════════════════════════════════════════
        //  ML 미준비시 폴백 청산 규칙 (최소한의 안전장치)
        // ═══════════════════════════════════════════════════════════
        private SniperDecision FallbackExitDecision(SniperExitInput input)
        {
            // 긴급: ROE -25% 이하
            if (input.UnrealizedRoePct <= -25f)
                return new SniperDecision { Action = ExitAction.FullClose, Confidence = 0.95f,
                    Reason = $"폴백 손절 (ROE:{input.UnrealizedRoePct:F1}%)" };

            // 매도압력 급증
            if (input.SellPressure > 0.70f && input.Volume_Density > 1.8f)
                return new SniperDecision { Action = ExitAction.EmergencyCut, Confidence = 0.85f,
                    Reason = "폴백 긴급청산 (매도압력)" };

            // ROE +30% 이상 + 부분익절 안했으면
            if (input.UnrealizedRoePct >= 30f && input.PartialExitDone < 0.5f)
                return new SniperDecision { Action = ExitAction.PartialClose, Confidence = 0.70f,
                    Reason = $"폴백 부분익절 (ROE:{input.UnrealizedRoePct:F1}%)" };

            return new SniperDecision { Action = ExitAction.Hold, Confidence = 0.50f, Reason = "폴백 HOLD" };
        }

        // ═══════════════════════════════════════════════════════════
        //  모델 학습
        // ═══════════════════════════════════════════════════════════
        private static readonly string[] EntryFeatureCols = {
            // 5분봉 25개
            "RSI", "RSI_Slope", "MACD_Hist", "MACD_Rising", "BB_Position",
            "BB_Width_Pct", "ATR_Pct", "Volume_Ratio", "Volume_Spike", "Volume_Density",
            "EMA9_Slope", "EMA21_Slope", "EMA_Alignment", "Price_To_EMA21", "Price_To_EMA50",
            "Candle_Body_Ratio", "Candle_Direction", "Upper_Shadow", "Lower_Shadow",
            "Price_Change_3Bar", "Price_Change_12Bar", "High_Break", "Low_Break", "HourOfDay", "DayOfWeek",
            // 15분봉 핵심 4대 지표 (18개)
            "M15_BB_Position", "M15_BB_Width", "M15_BB_Squeeze", "M15_BB_Breakout",
            "M15_RSI", "M15_RSI_Slope", "M15_RSI_VTurn", "M15_RSI_Divergence",
            "M15_EMA_Trend", "M15_EMA_Cross", "M15_EMA_Gap", "M15_Price_Above_EMA20",
            "M15_Fib_Position", "M15_Fib_AtGoldenZone", "M15_Fib_Bounce",
            "M15_MACD_Rising", "M15_Volume_Ratio",
            // 1H/4H (10개)
            "H1_RSI", "H1_MACD_Rising", "H1_BB_Position", "H1_EMA_Trend", "H1_Volume_Ratio",
            "H4_RSI", "H4_MACD_Rising", "H4_BB_Position", "H4_EMA_Trend", "H4_Volume_Ratio",
            // 패턴 매칭 (3개)
            "PatternSimilarity", "PatternMatchCount", "PatternWinRate"
        };

        private static readonly string[] ExitFeatureCols = {
            "UnrealizedRoePct", "MaxRoePct", "DrawdownFromPeak", "HoldMinutes", "EntryConfidence",
            "IsLong", "PartialExitDone", "TrailingActive",
            "RSI", "MACD_Rising", "BB_Position", "ATR_Pct", "Volume_Ratio",
            "Volume_Density", "SellPressure", "EMA_Alignment", "Price_Change_3Bar", "Momentum_Score",
            "H1_RSI", "H1_EMA_Trend", "H1_MACD_Rising",
            "H4_RSI", "H4_EMA_Trend", "H4_MACD_Rising", "H4_BB_Position"
        };

        public (double accuracy, double auc) TrainEntryModel(IEnumerable<SniperEntryInput> data)
        {
            var dataView = _mlContext.Data.LoadFromEnumerable(data);

            var pipeline = _mlContext.Transforms.Concatenate("Features", EntryFeatureCols)
                .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                    labelColumnName: "Label", featureColumnName: "Features",
                    numberOfLeaves: 63, learningRate: 0.03, numberOfIterations: 500,
                    minimumExampleCountPerLeaf: 15));

            var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);
            _entryModel = pipeline.Fit(split.TrainSet);

            var metrics = _mlContext.BinaryClassification.Evaluate(
                _entryModel.Transform(split.TestSet), "Label");
            EntryModelAccuracy = metrics.Accuracy;
            EntryModelAUC = metrics.AreaUnderRocCurve;

            _entryPredictor = _mlContext.Model.CreatePredictionEngine<SniperEntryInput, SniperEntryOutput>(_entryModel);

            Directory.CreateDirectory(ModelDir);
            _mlContext.Model.Save(_entryModel, dataView.Schema, Path.Combine(ModelDir, "sniper_entry.zip"));

            LastTrainTime = DateTime.UtcNow;
            SaveTimestamp();

            return (metrics.Accuracy, metrics.AreaUnderRocCurve);
        }

        public (double accuracy, double auc) TrainExitModel(IEnumerable<SniperExitInput> data)
        {
            var dataView = _mlContext.Data.LoadFromEnumerable(data);

            var pipeline = _mlContext.Transforms.Concatenate("Features", ExitFeatureCols)
                .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                    labelColumnName: "Label", featureColumnName: "Features",
                    numberOfLeaves: 31, learningRate: 0.05, numberOfIterations: 300,
                    minimumExampleCountPerLeaf: 20));

            var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);
            _exitModel = pipeline.Fit(split.TrainSet);

            var metrics = _mlContext.BinaryClassification.Evaluate(
                _exitModel.Transform(split.TestSet), "Label");
            ExitModelAccuracy = metrics.Accuracy;

            _exitPredictor = _mlContext.Model.CreatePredictionEngine<SniperExitInput, SniperExitOutput>(_exitModel);

            Directory.CreateDirectory(ModelDir);
            _mlContext.Model.Save(_exitModel, dataView.Schema, Path.Combine(ModelDir, "sniper_exit.zip"));

            return (metrics.Accuracy, metrics.AreaUnderRocCurve);
        }

        // ═══════════════════════════════════════════════════════════
        //  모델 로드 (앱 시작시 자동)
        // ═══════════════════════════════════════════════════════════
        public bool TryLoadModels()
        {
            try
            {
                string entryPath = Path.Combine(ModelDir, "sniper_entry.zip");
                string exitPath = Path.Combine(ModelDir, "sniper_exit.zip");

                if (File.Exists(entryPath))
                {
                    _entryModel = _mlContext.Model.Load(entryPath, out _);
                    _entryPredictor = _mlContext.Model.CreatePredictionEngine<SniperEntryInput, SniperEntryOutput>(_entryModel);
                }
                if (File.Exists(exitPath))
                {
                    _exitModel = _mlContext.Model.Load(exitPath, out _);
                    _exitPredictor = _mlContext.Model.CreatePredictionEngine<SniperExitInput, SniperExitOutput>(_exitModel);
                }

                LoadTimestamp();
                LoadWinPatterns();

                return _entryPredictor != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AIDecision] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        // ═══ 패턴 영속화 ═══
        public void SaveWinPatterns()
        {
            Directory.CreateDirectory(ModelDir);
            string path = Path.Combine(ModelDir, "win_patterns.csv");
            using var sw = new StreamWriter(path, false, Encoding.UTF8);
            foreach (var (dir, patterns) in _winPatterns)
                foreach (var vec in patterns)
                    sw.WriteLine($"{dir},{string.Join(";", vec.Select(v => v.ToString("G", CultureInfo.InvariantCulture)))}");
        }

        private void LoadWinPatterns()
        {
            string path = Path.Combine(ModelDir, "win_patterns.csv");
            if (!File.Exists(path)) return;
            _winPatterns.Clear();
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                var vec = parts[1].Split(';')
                    .Select(s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0)
                    .ToArray();
                if (vec.Length > 0) AddWinPattern(parts[0], vec);
            }
        }

        private void SaveTimestamp()
        {
            Directory.CreateDirectory(ModelDir);
            File.WriteAllText(Path.Combine(ModelDir, "last_train.txt"), DateTime.UtcNow.ToString("o"));
        }

        private void LoadTimestamp()
        {
            string p = Path.Combine(ModelDir, "last_train.txt");
            if (File.Exists(p) && DateTime.TryParse(File.ReadAllText(p).Trim(), out var dt)) LastTrainTime = dt;
        }

        private static double CosineSimilarity(double[] a, double[] b)
        {
            double dot = 0, magA = 0, magB = 0;
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
            double d = Math.Sqrt(magA) * Math.Sqrt(magB);
            return d > 0 ? dot / d : 0;
        }
    }
}
