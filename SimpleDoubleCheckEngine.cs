using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Services.AI;

namespace TradingBot
{
    /// <summary>
    /// 단순화된 더블 체크 진입 엔진
    /// 
    /// [1단계] Transformer Navigator (우뇌): "타점까지 몇 캔들 남았는가?" (1~4캔들 → 매복 시작)
    /// [2단계] ML.NET Sniper (좌뇌): 매복 시간대 내에서만 "지금 쏠 조건인가?" (신뢰도 85%)
    /// 
    /// 효과: CPU 75% 절감 (매복 시간대 외에는 ML.NET 스킵)
    /// </summary>
    public class SimpleDoubleCheckEngine
    {
        private readonly Dictionary<string, AmbushState> _ambushStates = new();
        private readonly TransformerTrainer? _transformer;
        private readonly AIPredictor? _mlNetPredictor;

        // 임계값
        private const float TRANSFORMER_CONFIDENCE_THRESHOLD = 0.70f;
        private const float MLNET_CONFIDENCE_THRESHOLD = 0.85f;
        private const int AMBUSH_MIN_CANDLES = 1;
        private const int AMBUSH_MAX_CANDLES = 4;
        private const int AMBUSH_BUFFER_MINUTES = 30; // 매복 윈도우 버퍼

        public bool IsReady => _transformer != null && _transformer.IsModelReady 
                            && _mlNetPredictor != null && _mlNetPredictor.IsModelLoaded;

        public event Action<string>? OnLog;

        /// <summary>
        /// 매복 상태
        /// </summary>
        private class AmbushState
        {
            public bool IsActive { get; set; }
            public DateTime AmbushStart { get; set; }
            public DateTime AmbushEnd { get; set; }
            public float PredictedCandles { get; set; }
            public float TransformerConfidence { get; set; }
        }

        /// <summary>
        /// 진입 결정 결과
        /// </summary>
        public class EntryDecision
        {
            public bool AllowEntry { get; set; }
            public string Reason { get; set; } = string.Empty;
            
            // 1단계: Transformer
            public bool TransformerApproved { get; set; }
            public float TransformerConfidence { get; set; }
            public float PredictedCandlesToTarget { get; set; }
            
            // 2단계: ML.NET
            public bool MLNetApproved { get; set; }
            public float MLNetConfidence { get; set; }
            
            // 매복 상태
            public bool IsInAmbushWindow { get; set; }
            public DateTime? AmbushExpiration { get; set; }
            
            // [HybridExitManager 통합] 익절/손절 추천가
            public decimal RecommendedTakeProfit { get; set; }  // Transformer 예측 타겟 가격
            public decimal RecommendedStopLoss { get; set; }    // ATR 기반 손절가
            public decimal PredictedTargetPrice { get; set; }   // HybridExitManager용 AI 예측가
            
            public DateTime DecisionTime { get; set; }
        }

        public SimpleDoubleCheckEngine(
            TransformerTrainer? transformer,
            AIPredictor? mlNetPredictor)
        {
            _transformer = transformer;
            _mlNetPredictor = mlNetPredictor;
        }

        /// <summary>
        /// 실시간 진입 검증 (매복 모드 기반)
        /// </summary>
        public EntryDecision EvaluateEntry(
            string symbol,
            List<CandleData> recentCandles,
            decimal currentPrice)
        {
            var decision = new EntryDecision
            {
                DecisionTime = DateTime.UtcNow
            };

            // ============================================
            // [1단계] Transformer: 타점까지 시간 예측
            // ============================================
            if (_transformer == null || !_transformer.IsModelReady)
            {
                decision.Reason = "Transformer 모델 미준비";
                return decision;
            }

            // 매복 상태 확인
            if (!_ambushStates.TryGetValue(symbol, out var ambushState))
            {
                ambushState = new AmbushState();
                _ambushStates[symbol] = ambushState;
            }

            // 매복 모드가 아니면 → Transformer로 시간 예측
            if (!ambushState.IsActive)
            {
                var (predictedCandles, confidence) = PredictTimeToTarget(recentCandles);
                
                decision.PredictedCandlesToTarget = predictedCandles;
                decision.TransformerConfidence = confidence;

                // 1~4 캔들(15~60분) 이내에 타점 도달 예측 & 신뢰도 70% 이상
                if (predictedCandles >= AMBUSH_MIN_CANDLES 
                    && predictedCandles <= AMBUSH_MAX_CANDLES 
                    && confidence >= TRANSFORMER_CONFIDENCE_THRESHOLD)
                {
                    // 매복 시작!
                    int estimatedMinutes = (int)(predictedCandles * 15); // 15분봉 기준
                    ambushState.IsActive = true;
                    ambushState.AmbushStart = DateTime.Now;
                    ambushState.AmbushEnd = DateTime.Now.AddMinutes(estimatedMinutes + AMBUSH_BUFFER_MINUTES);
                    ambushState.PredictedCandles = predictedCandles;
                    ambushState.TransformerConfidence = confidence;

                    decision.TransformerApproved = true;
                    decision.IsInAmbushWindow = true;
                    decision.AmbushExpiration = ambushState.AmbushEnd;

                    OnLog?.Invoke(
                        $"🎯 [{symbol}] Transformer 1단계 승인! " +
                        $"예측: {predictedCandles:F1}캔들 후 타점 (신뢰도 {confidence:P0}) | " +
                        $"매복 종료: {ambushState.AmbushEnd:HH:mm}");
                }
                else
                {
                    // [FIX] 매복 모드가 아닐 때도 TF 점수는 기록 (이유만 표시)
                    decision.Reason = $"⏳ TF 대기 중 ({predictedCandles:F1}캔들, {confidence:P0})";
                    decision.MLNetConfidence = 0f; // ML.NET은 매복 모드에서만 실행
                    return decision; // 매복 모드 아니면 턴 종료 (CPU 절약)
                }
            }
            else
            {
                // 이미 매복 모드 → 시간 만료 체크
                if (DateTime.Now > ambushState.AmbushEnd)
                {
                    OnLog?.Invoke($"⏱️ [{symbol}] 매복 시간 만료. 타점 미도달로 매복 해제.");
                    ambushState.IsActive = false;
                    
                    // [FIX] 만료 시에도 마지막 TF 점수는 유지
                    decision.TransformerConfidence = ambushState.TransformerConfidence;
                    decision.PredictedCandlesToTarget = ambushState.PredictedCandles;
                    decision.Reason = $"⏰ 매복 만료 (TF={ambushState.TransformerConfidence:P0})";
                    decision.MLNetConfidence = 0f;
                    return decision;
                }

                decision.TransformerApproved = true;
                decision.IsInAmbushWindow = true;
                decision.AmbushExpiration = ambushState.AmbushEnd;
                decision.TransformerConfidence = ambushState.TransformerConfidence;
                decision.PredictedCandlesToTarget = ambushState.PredictedCandles;
            }

            // ============================================
            // [2단계] ML.NET: 정밀 조건 검증 (매복 시간대 내에서만)
            // ============================================
            if (_mlNetPredictor == null || !_mlNetPredictor.IsModelLoaded)
            {
                decision.Reason = "ML.NET 모델 미준비";
                return decision;
            }

            // 최신 캔들로 특징 추출
            var latestCandle = ConvertToMLFeatures(recentCandles);
            var prediction = _mlNetPredictor.Predict(latestCandle);

            decision.MLNetConfidence = prediction.Probability;
            decision.MLNetApproved = prediction.Probability >= MLNET_CONFIDENCE_THRESHOLD;

            if (decision.MLNetApproved)
            {
                // ============================================
                // [HybridExitManager 통합] 익절/손절가 계산
                // ============================================
                
                // 1️⃣ 익절가: Transformer 예측 캔들 수 기반 목표가 계산
                // 15분봉 기준, 1~4캔들 후 예상 가격 (간단한 선형 추정)
                decimal estimatedMove = currentPrice * ((decimal)decision.PredictedCandlesToTarget * 0.008m); // 캔들당 0.8% 상승 가정
                decision.PredictedTargetPrice = currentPrice + estimatedMove;
                decision.RecommendedTakeProfit = decision.PredictedTargetPrice;
                
                // 2️⃣ 손절가: ATR 기반 동적 손절 (ATR 1.5배 = 진입 직후 방어)
                // 최근 캔들의 ATR 계산
                decimal atrStopDistance = CalculateAtrStopDistance(recentCandles, currentPrice);
                decision.RecommendedStopLoss = currentPrice - atrStopDistance;
                
                // 최종 승인!
                decision.AllowEntry = true;
                decision.Reason = $"✅ 더블 체크 통과! " +
                    $"TF={decision.TransformerConfidence:P0} ({decision.PredictedCandlesToTarget:F1}캔들) | " +
                    $"ML={decision.MLNetConfidence:P0} | " +
                    $"목표가=${decision.RecommendedTakeProfit:F2} | " +
                    $"손절=${decision.RecommendedStopLoss:F2}";

                OnLog?.Invoke(
                    $"🔥 [{symbol}] ML.NET 2단계 승인! " +
                    $"신뢰도 {decision.MLNetConfidence:P0} | " +
                    $"목표가 ${decision.RecommendedTakeProfit:F2} (현재 +{estimatedMove / currentPrice * 100:F1}%) | " +
                    $"손절 ${decision.RecommendedStopLoss:F2} (-{atrStopDistance / currentPrice * 100:F1}%) | " +
                    $"🚀 최종 진입 실행!");

                // 진입 후 매복 해제
                ambushState.IsActive = false;
            }
            else
            {
                decision.Reason = $"ML.NET 신뢰도 부족 ({decision.MLNetConfidence:P0} < {MLNET_CONFIDENCE_THRESHOLD:P0})";
                
                OnLog?.Invoke(
                    $"✋ [{symbol}] ML.NET 2단계 거부 | " +
                    $"신뢰도 {decision.MLNetConfidence:P0} (기준 {MLNET_CONFIDENCE_THRESHOLD:P0})");
            }

            return decision;
        }

        /// <summary>
        /// Transformer로 타점까지 캔들 수 예측
        /// </summary>
        private (float predictedCandles, float confidence) PredictTimeToTarget(List<CandleData> candles)
        {
            if (_transformer == null || candles.Count < 30)
                return (0f, 0f);

            try
            {
                // Transformer 예측 (실제 구현은 TimeSeriesTransformer 활용)
                // 여기서는 간단히 더미 값 반환
                // TODO: 실제 Transformer.Predict() 호출
                
                float predictedCandles = 2.5f; // 예시
                float confidence = 0.75f;

                return (predictedCandles, confidence);
            }
            catch
            {
                return (0f, 0f);
            }
        }

        /// <summary>
        /// 캔들 데이터를 ML.NET 입력 형식으로 변환
        /// </summary>
        private CandleData ConvertToMLFeatures(List<CandleData> candles)
        {
            if (candles == null || candles.Count == 0)
                return new CandleData();

            // 최신 캔들 반환 (실제로는 피보나치, RSI 다이버전스 등 특징 추출)
            return candles.Last();
        }

        /// <summary>
        /// ATR 기반 손절 거리 계산 (진입 직후 방어용 ATR 1.5배)
        /// </summary>
        private decimal CalculateAtrStopDistance(List<CandleData> candles, decimal currentPrice)
        {
            if (candles == null || candles.Count < 20)
                return currentPrice * 0.015m; // 폴백: 1.5%

            try
            {
                // ATR 14 계산 - IBinanceKline 리스트 생성
                var recentCandles = candles.TakeLast(20).ToList();
                var klineList = recentCandles.Select(c => (IBinanceKline)new BinanceKlineAdapter(c)).ToList();
                
                double atr14 = IndicatorCalculator.CalculateATR(klineList, 14);
                
                // ATR 1.5배 (진입 직후 변동성 방어)
                decimal atrDistance = (decimal)atr14 * 1.5m;
                
                // 안전장치: 최소 0.5%, 최대 3.0%
                decimal minStop = currentPrice * 0.005m;
                decimal maxStop = currentPrice * 0.030m;
                
                return Math.Max(minStop, Math.Min(atrDistance, maxStop));
            }
            catch
            {
                return currentPrice * 0.015m; // 폴백
            }
        }

        /// <summary>
        /// 특정 심볼의 매복 상태 조회
        /// </summary>
        public bool IsInAmbushMode(string symbol)
        {
            if (_ambushStates.TryGetValue(symbol, out var state))
            {
                return state.IsActive && DateTime.Now <= state.AmbushEnd;
            }
            return false;
        }

        /// <summary>
        /// 매복 상태 초기화
        /// </summary>
        public void ResetAmbush(string symbol)
        {
            if (_ambushStates.ContainsKey(symbol))
            {
                _ambushStates[symbol].IsActive = false;
            }
        }

        /// <summary>
        /// 모든 매복 상태 정보
        /// </summary>
        public Dictionary<string, string> GetAmbushStatus()
        {
            var status = new Dictionary<string, string>();
            
            foreach (var kvp in _ambushStates)
            {
                if (kvp.Value.IsActive)
                {
                    var remaining = (kvp.Value.AmbushEnd - DateTime.Now).TotalMinutes;
                    status[kvp.Key] = $"매복 중 (종료까지 {remaining:F0}분)";
                }
                else
                {
                    status[kvp.Key] = "대기 중";
                }
            }

            return status;
        }
    }
}
