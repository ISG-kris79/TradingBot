using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// 더블 체크 진입 엔진 - 엘리엇 파동 + AI 더블 검증
    /// 
    /// 아키텍처:
    /// [1차 체크] Transformer (네비게이터 - 우뇌): "지금은 3파 진입 시간대인가?"
    /// [2차 체크] ML.NET (스나이퍼 - 좌뇌): "이 조건에서 방아쇠를 당겨도 되는가?"
    /// 
    /// 두 AI의 교집합에서만 진입 승인
    /// </summary>
    public class DoubleCheckEntryEngine
    {
        private readonly ElliottWaveDetector _waveDetector;
        private readonly WaveSniper _waveSniper;
        private readonly TransformerWaveNavigator _transformerNavigator;
        private readonly MLNetWaveSniper _mlNetSniper;

        // 임계값
        private const float TransformerConfidenceThreshold = 0.70f; // Transformer 최소 신뢰도 70%
        private const float MLNetProbabilityThreshold = 0.75f;      // ML.NET 최소 확률 75%
        private const int MinSniperConditions = 3;                  // 스나이퍼 최소 조건 3/5

        public bool IsReady => _transformerNavigator.IsReady && _mlNetSniper.IsReady;

        public DoubleCheckEntryEngine(
            string transformerModelPath = "transformer_wave_navigator.dat",
            string mlnetModelPath = "mlnet_wave_sniper.zip")
        {
            _waveDetector = new ElliottWaveDetector();
            _waveSniper = new WaveSniper();
            _transformerNavigator = new TransformerWaveNavigator(transformerModelPath);
            _mlNetSniper = new MLNetWaveSniper(mlnetModelPath);
        }

        /// <summary>
        /// 진입 결정 결과
        /// </summary>
        public class EntryDecision
        {
            public bool AllowEntry { get; set; }
            public string Reason { get; set; } = string.Empty;
            
            // 파동 정보
            public ElliottWaveDetector.WavePhase WavePhase { get; set; }
            public decimal FibonacciLevel { get; set; }
            
            // 1차 체크 (Transformer)
            public bool TransformerApproved { get; set; }
            public float TransformerConfidence { get; set; }
            
            // 2차 체크 (ML.NET)
            public bool MLNetApproved { get; set; }
            public float MLNetProbability { get; set; }
            
            // 스나이퍼 조건
            public int SniperConditionsMet { get; set; }
            public WaveSniper.SniperSignal? SniperSignal { get; set; }
            
            // 추천 가격
            public decimal? RecommendedEntry { get; set; }
            public decimal? RecommendedStopLoss { get; set; }
            public decimal? RecommendedTakeProfit { get; set; }
            
            public DateTime DecisionTime { get; set; }
        }

        /// <summary>
        /// 실시간 진입 검증 (메인 로직)
        /// </summary>
        public EntryDecision EvaluateEntry(
            string symbol,
            CandleData currentCandle,
            List<CandleData> recentCandles15m,  // 최근 100개 15분봉
            List<float[]> transformerSequence,  // Transformer 입력 시퀀스
            decimal currentPrice,
            int trend1H,
            int trend4H)
        {
            var decision = new EntryDecision
            {
                DecisionTime = DateTime.UtcNow
            };

            // Step 1: 파동 상태 업데이트
            var waveState = _waveDetector.UpdateWaveDetection(
                symbol, 
                currentCandle, 
                recentCandles15m, 
                currentPrice);

            decision.WavePhase = waveState.Phase;
            decision.FibonacciLevel = waveState.Wave2RetracementRatio;

            // 파동이 준비되지 않았으면 즉시 거부
            if (waveState.Phase != ElliottWaveDetector.WavePhase.Wave2Retracing &&
                waveState.Phase != ElliottWaveDetector.WavePhase.Wave2Complete)
            {
                decision.Reason = $"파동 미준비 (현재: {waveState.Phase})";
                return decision;
            }

            // Step 2: 스나이퍼 조건 검증
            var sniperSignal = _waveSniper.EvaluateTrigger(
                waveState, 
                currentCandle, 
                recentCandles15m, 
                currentPrice);

            decision.SniperSignal = sniperSignal;
            decision.SniperConditionsMet = sniperSignal.ConfirmedConditions;

            // 스나이퍼 최소 조건 미달
            if (!sniperSignal.IsTriggerReady || sniperSignal.ConfirmedConditions < MinSniperConditions)
            {
                decision.Reason = $"스나이퍼 조건 미달 ({sniperSignal.ConfirmedConditions}/{MinSniperConditions}) | {sniperSignal.Reason}";
                return decision;
            }

            // ============================================
            // 🔑 핵심: 더블 체크 (핵 발사 버튼 2개)
            // ============================================

            // [1차 체크] Transformer Navigator (시간대 승인)
            float transformerConfidence = 0.5f;
            if (_transformerNavigator.IsReady && transformerSequence.Count >= _transformerNavigator.SeqLen)
            {
                transformerConfidence = _transformerNavigator.PredictWave3EntryProbability(transformerSequence);
                decision.TransformerConfidence = transformerConfidence;
                decision.TransformerApproved = transformerConfidence >= TransformerConfidenceThreshold;
            }
            else
            {
                decision.Reason = "Transformer 모델 미준비 또는 시퀀스 부족";
                return decision;
            }

            // [2차 체크] ML.NET Sniper (조건 승인)
            float mlnetProbability = 0.5f;
            if (_mlNetSniper.IsReady)
            {
                var sniperInput = MLNetWaveSniper.ConvertFromSniperSignal(
                    sniperSignal, 
                    waveState, 
                    currentCandle, 
                    trend1H, 
                    trend4H);

                var prediction = _mlNetSniper.Predict(sniperInput);
                if (prediction != null)
                {
                    mlnetProbability = prediction.Probability;
                    decision.MLNetProbability = mlnetProbability;
                    decision.MLNetApproved = mlnetProbability >= MLNetProbabilityThreshold;
                }
                else
                {
                    decision.Reason = "ML.NET 예측 실패";
                    return decision;
                }
            }
            else
            {
                decision.Reason = "ML.NET 모델 미준비";
                return decision;
            }

            // ============================================
            // 최종 판정: 두 AI 모두 승인해야 진입
            // ============================================
            decision.AllowEntry = decision.TransformerApproved && decision.MLNetApproved;

            if (decision.AllowEntry)
            {
                decision.Reason = $"✅ 더블 체크 통과 | " +
                    $"TF={transformerConfidence:P0} ML={mlnetProbability:P0} | " +
                    $"Fib={decision.FibonacciLevel:P1} 조건={decision.SniperConditionsMet}/5";

                // 추천 가격 계산
                decision.RecommendedEntry = currentPrice;
                decision.RecommendedStopLoss = waveState.Wave1StartPrice * 0.998m; // 1파 시작점 약간 아래
                decision.RecommendedTakeProfit = waveState.Wave1PeakPrice * 1.02m; // 1파 고점 2% 위
            }
            else
            {
                var reasons = new List<string>();
                if (!decision.TransformerApproved)
                    reasons.Add($"Transformer={transformerConfidence:P0}<{TransformerConfidenceThreshold:P0}");
                if (!decision.MLNetApproved)
                    reasons.Add($"MLNet={mlnetProbability:P0}<{MLNetProbabilityThreshold:P0}");

                decision.Reason = $"❌ 더블 체크 실패 | {string.Join(", ", reasons)}";
            }

            return decision;
        }

        /// <summary>
        /// 특정 심볼의 파동 상태 조회
        /// </summary>
        public ElliottWaveDetector.WaveState? GetWaveState(string symbol)
        {
            return _waveDetector.GetWaveState(symbol);
        }

        /// <summary>
        /// 파동 상태 리셋
        /// </summary>
        public void ResetWave(string symbol)
        {
            _waveDetector.ResetWave(symbol);
        }

        /// <summary>
        /// 현재 피보나치 구간 정보 (디버깅용)
        /// </summary>
        public string GetFibonacciInfo(string symbol)
        {
            return _waveDetector.GetFibonacciZoneInfo(symbol);
        }

        /// <summary>
        /// 진입 결정 로그 포맷
        /// </summary>
        public string FormatDecisionLog(EntryDecision decision)
        {
            return $"🎯 [DOUBLE_CHECK] {decision.Reason}\n" +
                   $"   파동: {decision.WavePhase} (Fib {decision.FibonacciLevel:P1})\n" +
                   $"   1차체크: TF={decision.TransformerConfidence:P0} (승인={decision.TransformerApproved})\n" +
                   $"   2차체크: ML={decision.MLNetProbability:P0} (승인={decision.MLNetApproved})\n" +
                   $"   스나이퍼: {decision.SniperConditionsMet}/5 조건\n" +
                   (decision.AllowEntry 
                       ? $"   추천: Entry={decision.RecommendedEntry:F8} SL={decision.RecommendedStopLoss:F8} TP={decision.RecommendedTakeProfit:F8}"
                       : "");
        }

        /// <summary>
        /// Transformer 학습 실행
        /// </summary>
        public async Task<bool> TrainTransformerAsync(
            List<WaveTrainingData> trainingData, 
            CancellationToken token)
        {
            try
            {
                var (accuracy, loss) = await _transformerNavigator.TrainAsync(trainingData, token);
                Console.WriteLine($"[DoubleCheck] Transformer 학습 완료 | Accuracy={accuracy:P1} Loss={loss:F4}");
                return accuracy > 0.6f; // 최소 60% 정확도 요구
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DoubleCheck] Transformer 학습 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ML.NET 학습 실행
        /// </summary>
        public bool TrainMLNet(List<MLNetWaveSniper.SniperInput> trainingData)
        {
            try
            {
                var (accuracy, auc) = _mlNetSniper.Train(trainingData);
                Console.WriteLine($"[DoubleCheck] ML.NET 학습 완료 | Accuracy={accuracy:P1} AUC={auc:F3}");
                return accuracy > 0.65f; // 최소 65% 정확도 요구
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DoubleCheck] ML.NET 학습 실패: {ex.Message}");
                return false;
            }
        }
    }
}
