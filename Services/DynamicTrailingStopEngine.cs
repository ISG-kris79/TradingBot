using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// AI 기반 동적 익절/손절 트레일링 엔진
    ///
    /// 고정 %가 아닌, 파동의 '에너지 강도'와 '변동성'에 따라
    /// 탈출 시점을 실시간으로 변경합니다.
    ///
    /// ┌─────────────────────────────────────────────────────┐
    /// │  ML.NET → LowerBound (신뢰구간 하한선) → 동적 손절가  │
    /// │  TF     → 추세 반전 확률 → 트레일링 폭 조절           │
    /// │  ATR    → 변동성 가중치 → 스탑 간격 보정              │
    /// └─────────────────────────────────────────────────────┘
    ///
    /// 워크플로우:
    ///   진입: ML ProgressBar 30% 이하 + TF '3파 초입' 감지
    ///   홀딩: 가격이 ML LowerBound 위 + TrendStrength 높음
    ///   탈출: TF '5파 완성/다이버전스' 또는 LowerBound 하향 돌파
    /// </summary>
    public class DynamicTrailingStopEngine
    {
        private readonly ConcurrentDictionary<string, AITrailingState> _states = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;

        /// <summary>
        /// Exit Score 업데이트 이벤트 (UI 게이지용)
        /// (symbol, exitScore 0~100, trendReversalProb 0~1, dynamicStopPrice)
        /// </summary>
        public event Action<string, double, float, decimal>? OnExitScoreUpdate;

        /// <summary>포지션 등록</summary>
        public void RegisterPosition(string symbol, string direction, decimal entryPrice, double atr)
        {
            _states[symbol] = new AITrailingState
            {
                Symbol = symbol,
                Direction = direction,
                EntryPrice = entryPrice,
                HighestPrice = entryPrice,
                LowestPrice = entryPrice,
                BaseATR = atr,
                CurrentATR = atr,
                DynamicStopPrice = 0m,
                TrendReversalProbability = 0f,
                ExitScore = 0,
                LastUpdateTime = DateTime.UtcNow
            };
        }

        /// <summary>포지션 제거</summary>
        public void RemovePosition(string symbol) => _states.TryRemove(symbol, out _);

        /// <summary>상태 존재 여부</summary>
        public bool HasPosition(string symbol) => _states.ContainsKey(symbol);

        /// <summary>현재 동적 손절가 조회</summary>
        public decimal GetDynamicStopPrice(string symbol)
            => _states.TryGetValue(symbol, out var s) ? s.DynamicStopPrice : 0m;

        /// <summary>Exit Score 조회 (0~100)</summary>
        public double GetExitScore(string symbol)
            => _states.TryGetValue(symbol, out var s) ? s.ExitScore : 0;

        /// <summary>
        /// AI 기반 동적 손절가 계산 (메인 메서드)
        ///
        /// ML.NET의 LowerBound(신뢰구간 하한)를 손절가 기준으로 삼고,
        /// TF의 추세 반전 확률에 따라 트레일링 폭을 조절합니다.
        /// </summary>
        public DynamicStopResult CalculateAIDynamicStop(
            string symbol,
            decimal currentPrice,
            double currentATR,
            double rsi,
            float mlConfidence,
            float tfTrendStrength,
            float upperShadowRatio,
            bool isWave5Pattern,
            bool isRsiDivergence,
            double bbWidth)
        {
            if (!_states.TryGetValue(symbol, out var state))
                return DynamicStopResult.NoAction;

            bool isLong = state.Direction == "LONG";

            // ── 1. 최고가/최저가 갱신 ─────────────────────────────
            if (isLong && currentPrice > state.HighestPrice)
                state.HighestPrice = currentPrice;
            if (!isLong && currentPrice < state.LowestPrice)
                state.LowestPrice = currentPrice;

            state.CurrentATR = currentATR;
            state.LastUpdateTime = DateTime.UtcNow;

            // ── 2. 추세 반전 확률 계산 (TF 기반) ───────────────────
            float reversalProb = CalculateTrendReversalProbability(
                tfTrendStrength, rsi, upperShadowRatio,
                isWave5Pattern, isRsiDivergence, bbWidth);
            state.TrendReversalProbability = reversalProb;

            // ── 3. ATR 기반 동적 손절가 계산 ──────────────────────
            decimal referencePrice = isLong ? state.HighestPrice : state.LowestPrice;
            decimal roe = CalculateROE(state.EntryPrice, referencePrice, isLong);

            // ATR 멀티플라이어: 반전 확률이 높을수록 타이트하게
            double atrMultiplier = CalculateAtrMultiplier(roe, rsi, reversalProb, currentATR, state.BaseATR);
            decimal atrStop = (decimal)(currentATR * atrMultiplier);

            decimal newStopPrice = isLong
                ? referencePrice - atrStop
                : referencePrice + atrStop;

            // ── 4. ML.NET LowerBound 기반 보정 ────────────────────
            // ML 확률이 높으면 LowerBound를 좀 더 신뢰 → 스탑을 올림
            if (mlConfidence >= 0.40f)
            {
                // ML이 강한 신호를 주면 스탑을 더 올림 (ML 신뢰만큼 보정)
                decimal mlBoost = atrStop * (decimal)(mlConfidence - 0.40f);
                if (isLong)
                    newStopPrice += mlBoost;
                else
                    newStopPrice -= mlBoost;
            }

            // ── 5. 반전 패턴 감지 시 극한 타이트 트레일링 ─────────
            if (reversalProb >= 0.80f)
            {
                // 5파 완성 + RSI 다이버전스: 0.1~0.2% 이내로 극한 타이트
                decimal tightStop = isLong
                    ? currentPrice * 0.998m   // 0.2%
                    : currentPrice * 1.002m;
                // 더 보수적인 쪽 선택 (이익 보존 우선)
                if (isLong)
                    newStopPrice = Math.Max(newStopPrice, tightStop);
                else
                    newStopPrice = Math.Min(newStopPrice, tightStop);
            }

            // ── 6. 상향 전용 트레일링 (절대 후퇴 불가) ─────────────
            bool stopChanged = false;
            if (state.DynamicStopPrice == 0m)
            {
                state.DynamicStopPrice = newStopPrice;
                stopChanged = true;
            }
            else if (isLong && newStopPrice > state.DynamicStopPrice)
            {
                state.DynamicStopPrice = newStopPrice;
                stopChanged = true;
            }
            else if (!isLong && newStopPrice < state.DynamicStopPrice)
            {
                state.DynamicStopPrice = newStopPrice;
                stopChanged = true;
            }

            // ── 7. Exit Score 계산 (UI 게이지용, 0~100) ──────────
            double exitScore = CalculateExitScore(roe, rsi, reversalProb, upperShadowRatio, bbWidth);
            state.ExitScore = exitScore;

            // ── 8. 이벤트 발행 (UI 업데이트) ─────────────────────
            OnExitScoreUpdate?.Invoke(symbol, exitScore, reversalProb, state.DynamicStopPrice);

            // ── 9. 손절 트리거 확인 ──────────────────────────────
            bool triggered = isLong
                ? currentPrice <= state.DynamicStopPrice
                : currentPrice >= state.DynamicStopPrice;

            if (triggered)
            {
                string reason = reversalProb >= 0.70f
                    ? $"AI 동적 손절 (반전확률={reversalProb:P0}, ATR×{atrMultiplier:F2})"
                    : $"ATR 트레일링 스탑 (ATR×{atrMultiplier:F2})";
                OnAlert?.Invoke($"🔴 [{symbol}] {reason} | Stop={state.DynamicStopPrice:F8} Price={currentPrice:F8}");
            }

            return new DynamicStopResult
            {
                Symbol = symbol,
                NewStopPrice = state.DynamicStopPrice,
                StopChanged = stopChanged,
                Triggered = triggered,
                ExitScore = exitScore,
                TrendReversalProbability = reversalProb,
                AtrMultiplier = atrMultiplier,
                ROE = (double)roe
            };
        }

        /// <summary>
        /// TF 기반 추세 반전 확률 계산 (0.0 ~ 1.0)
        ///
        /// 신호:
        /// - 엘리엇 5파 완성 패턴 (거래량 감소 + 고점 갱신)
        /// - RSI 하락 다이버전스 (가격 신고가 + RSI 하락)
        /// - 상위 꼬리 비율 높음 (매도 압력)
        /// - BB 폭 축소 후 확대 (변동성 폭발 직전)
        /// </summary>
        private static float CalculateTrendReversalProbability(
            float tfTrendStrength,
            double rsi,
            float upperShadowRatio,
            bool isWave5Pattern,
            bool isRsiDivergence,
            double bbWidth)
        {
            float prob = 0f;

            // (1) 5파 완성 패턴: +35%
            if (isWave5Pattern) prob += 0.35f;

            // (2) RSI 다이버전스: +25%
            if (isRsiDivergence) prob += 0.25f;

            // (3) 상위 꼬리 비율 70%+: +15%
            if (upperShadowRatio >= 0.70f) prob += 0.15f;
            else if (upperShadowRatio >= 0.50f) prob += 0.08f;

            // (4) RSI 과매수: +10~20%
            if (rsi >= 80) prob += 0.20f;
            else if (rsi >= 70) prob += 0.10f;

            // (5) TF 추세 강도 약화: +10%
            if (tfTrendStrength < 0.30f) prob += 0.10f;

            // (6) BB 폭 극한 축소 (수렴 후 발산 임박): +5%
            if (bbWidth < 1.5) prob += 0.05f;

            return Math.Clamp(prob, 0f, 1.0f);
        }

        /// <summary>
        /// ATR 멀티플라이어 계산
        ///
        /// 기본 로직 (HybridExitManager 호환):
        ///   ROE < 10%: ATR × 1.5 (휩소 방어)
        ///   ROE 10~20% & RSI < 70: ATR × 1.0 (추세 진행)
        ///   RSI 70~80: ATR × 0.5 (고점 근접)
        ///   RSI 80+: ATR × 0.2 (극단 트레일링)
        ///
        /// AI 보정:
        ///   반전 확률 높으면 → 멀티플라이어 하향 (타이트)
        ///   변동성 급변(ATR 급등) → 멀티플라이어 조정
        /// </summary>
        private static double CalculateAtrMultiplier(
            decimal roe, double rsi, float reversalProb,
            double currentATR, double baseATR)
        {
            // 기본 멀티플라이어 (HybridExitManager 호환)
            double baseMult;
            if (roe < 10)
                baseMult = 1.5;
            else if (roe >= 10 && rsi < 70)
                baseMult = 1.0;
            else if (rsi >= 70 && rsi < 80)
                baseMult = 0.5;
            else
                baseMult = 0.2;

            // AI 반전 확률 보정: 높을수록 타이트
            if (reversalProb >= 0.70f)
                baseMult *= 0.3;   // 70% 더 타이트
            else if (reversalProb >= 0.50f)
                baseMult *= 0.6;   // 40% 더 타이트
            else if (reversalProb >= 0.30f)
                baseMult *= 0.85;  // 15% 더 타이트

            // 변동성 급변 보정: ATR이 기준 대비 2배 이상 → 타이트
            if (baseATR > 0 && currentATR / baseATR >= 2.0)
            {
                baseMult *= 0.5;  // 변동성 폭발 시 스탑 바짝 붙임
            }
            // ATR 낮음 (계단식 상승): 스탑 여유
            else if (baseATR > 0 && currentATR / baseATR <= 0.5)
            {
                baseMult *= 1.3;  // 잔파동에 털리지 않도록
            }

            return Math.Max(0.05, baseMult); // 최소 0.05 ATR
        }

        /// <summary>
        /// Exit Score (UI 게이지용, 0~100)
        ///
        /// 0~30: 홀딩 유지 (추세 진행 중)
        /// 31~60: 익절 준비
        /// 61~80: 익절 권장
        /// 81~100: 즉시 탈출
        /// </summary>
        private static double CalculateExitScore(
            decimal roe, double rsi, float reversalProb,
            float upperShadowRatio, double bbWidth)
        {
            double score = 0;

            // 반전 확률 (최대 40점)
            score += reversalProb * 40;

            // RSI 과매수 (최대 20점)
            if (rsi >= 80) score += 20;
            else if (rsi >= 70) score += (rsi - 70) * 2;

            // 상위 꼬리 비율 (최대 15점)
            score += upperShadowRatio * 15;

            // ROE 높으면 수익 보존 유인 (최대 15점)
            if (roe >= 30) score += 15;
            else if (roe >= 20) score += 10;
            else if (roe >= 10) score += 5;

            // BB 축소 (최대 10점) — 변동성 폭발 임박
            if (bbWidth < 1.5) score += 10;
            else if (bbWidth < 2.5) score += 5;

            return Math.Clamp(score, 0, 100);
        }

        private static decimal CalculateROE(decimal entry, decimal reference, bool isLong)
        {
            if (entry == 0) return 0;
            decimal change = isLong
                ? (reference - entry) / entry
                : (entry - reference) / entry;
            return change * 20 * 100; // 20배 레버리지 ROE%
        }
    }

    // ─── 결과 모델 ───────────────────────────────────────────

    public class DynamicStopResult
    {
        public static readonly DynamicStopResult NoAction = new() { StopChanged = false };

        public string Symbol { get; set; } = "";
        public decimal NewStopPrice { get; set; }
        public bool StopChanged { get; set; }
        public bool Triggered { get; set; }
        public double ExitScore { get; set; }
        public float TrendReversalProbability { get; set; }
        public double AtrMultiplier { get; set; }
        public double ROE { get; set; }
    }

    internal class AITrailingState
    {
        public string Symbol { get; set; } = "";
        public string Direction { get; set; } = "LONG";
        public decimal EntryPrice { get; set; }
        public decimal HighestPrice { get; set; }
        public decimal LowestPrice { get; set; }
        public double BaseATR { get; set; }
        public double CurrentATR { get; set; }
        public decimal DynamicStopPrice { get; set; }
        public float TrendReversalProbability { get; set; }
        public double ExitScore { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }
}
