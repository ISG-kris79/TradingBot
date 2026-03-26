using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// AI 더블체크 진입 시스템
    /// ML.NET + TensorFlow Transformer 중 하나 이상 통과 시 진입 후보 허가
    /// + 실시간 데이터 수집 (지속적 학습용)
    /// + 온라인 학습 (적응형 학습 및 Concept Drift 감지)
    /// [v2.4.27] TorchSharp 완전 제거, TensorFlow.NET으로 전환
    /// </summary>
    public class AIDoubleCheckEntryGate
    {
        private readonly IExchangeService _exchangeService;
        private readonly EntryTimingMLTrainer _mlTrainer;
        private readonly TensorFlowEntryTimingTrainer _transformerTrainer;
        private readonly MultiTimeframeFeatureExtractor _featureExtractor;
        private readonly BacktestEntryLabeler _labeler;
        private readonly EntryRuleValidator _ruleValidator;
        private readonly ConcurrentDictionary<string, Queue<MultiTimeframeEntryFeature>> _recentFeatureBuffers
            = new(StringComparer.OrdinalIgnoreCase);
        
        // 실시간 데이터 수집
        private readonly ConcurrentQueue<EntryDecisionRecord> _pendingRecords = new();
        private readonly string _dataCollectionPath = "TrainingData/EntryDecisions";
        private readonly Timer? _dataFlushTimer;
        
        // 온라인 학습 서비스
        private readonly AdaptiveOnlineLearningService? _onlineLearning;
        private readonly DbManager? _dbManager;
        
        // 설정
        private readonly DoubleCheckConfig _config;
        
        // 로깅 이벤트
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;
        public event Action<AiLabeledSample>? OnLabeledSample;
        
        // [핫픽스] ML 모델 없어도 TF만으로 게이트 통과 가능하도록 변경
        // ML 모델은 첫 학습 전까지 없을 수 있음 → TF만 준비되면 IsReady = true
        // ML 0%일 때는 EntryRuleValidator에서 TF 점수로 FinalScore 보정
        public bool IsReady => _transformerTrainer.IsModelReady;

        public AIDoubleCheckEntryGate(
            IExchangeService exchangeService,
            DoubleCheckConfig? config = null,
            bool enableOnlineLearning = true,
            bool preferExternalTorchService = false) // 파라미터 유지하되 더 이상 사용 안 함
        {
            _config = config ?? new DoubleCheckConfig();
            _exchangeService = exchangeService;
            if (!string.IsNullOrWhiteSpace(AppConfig.ConnectionString))
            {
                _dbManager = new DbManager(AppConfig.ConnectionString);
            }
            
            _mlTrainer = new EntryTimingMLTrainer();
            _featureExtractor = new MultiTimeframeFeatureExtractor(exchangeService);
            _labeler = new BacktestEntryLabeler();
            _ruleValidator = new EntryRuleValidator(_config);

            // ML 모델 로드
            _mlTrainer.LoadModel();

            // TensorFlow Transformer 초기화 (통합 모델, 외부 프로세스 불필요)
            OnLog?.Invoke("[AIDoubleCheck] TensorFlow.NET Transformer 초기화 중...");
            _transformerTrainer = new TensorFlowEntryTimingTrainer();
            _transformerTrainer.OnLog += msg => OnLog?.Invoke(msg);
            _transformerTrainer.LoadModel();

            // 데이터 수집 폴더 생성
            Directory.CreateDirectory(_dataCollectionPath);

            // 온라인 학습 서비스 초기화
            if (enableOnlineLearning)
            {
                _onlineLearning = new AdaptiveOnlineLearningService(
                    _mlTrainer,
                    _transformerTrainer,
                    new OnlineLearningConfig
                    {
                        SlidingWindowSize = 1000,
                        MinSamplesForTraining = 200,
                        TriggerEveryNSamples = 100,
                        RetrainingIntervalHours = 1.0,
                        EnablePeriodicRetraining = true,
                        EnableConceptDriftDetection = true,
                        TransformerFastEpochs = 5
                    });

                _onlineLearning.OnLog += msg => OnLog?.Invoke(msg);
                _onlineLearning.OnPerformanceUpdate += (reason, acc, mlThresh, tfThresh) =>
                {
                    OnAlert?.Invoke($"🧠 온라인 학습: {reason} | 정확도={acc:P1}, ML={mlThresh:P0}, TF={tfThresh:P0}");
                };

                // 초기 윈도우 로드 (비동기 실행)
                _ = Task.Run(async () =>
                {
                    await _onlineLearning.LoadInitialWindowAsync(_dataCollectionPath);
                    OnLog?.Invoke($"[OnlineLearning] 초기화 완료: 윈도우 크기={_onlineLearning.WindowSize}");
                });
            }

            // 데이터 자동 저장 타이머 (5분마다)
            _dataFlushTimer = new Timer(_ => FlushCollectedData(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // [Look-ahead Shield] 기동 시 기존 EntryDecisions 오염 데이터 1회 정화
            _ = Task.Run(async () =>
            {
                try
                {
                    var stats = await SanitizeEntryDecisionFilesAsync(CancellationToken.None);
                    if (stats.removedRecords > 0)
                    {
                        OnAlert?.Invoke($"🧹 [LookAhead Shield] 초기 정화 완료: {stats.filesScanned}개 파일, {stats.removedRecords}/{stats.totalRecords}건 제거");
                    }
                    else
                    {
                        OnLog?.Invoke($"🛡️ [LookAhead Shield] 초기 점검 완료: {stats.filesScanned}개 파일, 제거 0건");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [LookAhead Shield] 초기 정화 실패: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 더블체크 진입 심사
        /// </summary>
        public async Task<(bool allowEntry, string reason, AIEntryDetail detail)> EvaluateEntryAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CancellationToken token = default)
        {
            if (!IsReady)
                return (false, "AI_Models_Not_Ready", new AIEntryDetail());

            try
            {
                // 1. Multi-Timeframe Feature 추출
                var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(symbol, DateTime.UtcNow, token);
                if (feature == null)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] Feature 추출 실패 (데이터 부족)");
                    return (false, "Feature_Extraction_Failed", new AIEntryDetail());
                }

                // 2. [The Brain] Transformer 흐름/타점 평가
                var recentFeatures = BuildTransformerSequence(symbol, feature);
                var (candlesToTarget, tfConfidence) = _transformerTrainer.Predict(recentFeatures);

                // Transformer 승인 기준: 유효한 Time-to-Target 예측 (1~32캔들 범위)
                bool tfApprove = candlesToTarget >= 1f && candlesToTarget <= 32f;
                float effectiveTFThreshold = _onlineLearning?.CurrentTFThreshold ?? _config.MinTransformerConfidence;

                if (tfApprove)
                {
                    int minutesToTarget = (int)Math.Round(candlesToTarget * 15);
                    DateTime eta = DateTime.Now.AddMinutes(minutesToTarget);
                    OnLog?.Invoke($"🧠 [{symbol}] [BRAIN] TF 타점: {candlesToTarget:F1}캔들 ({minutesToTarget}분) 후, ETA {eta:HH:mm} | TrendScore={tfConfidence:P0}");
                }

                // 3. [The Filter] ML.NET 최종 판정
                // [핫픽스] ML 모델 미로드 시 TF 단독 진행 (하드 블록 제거)
                var mlPrediction = _mlTrainer.IsModelLoaded ? _mlTrainer.Predict(feature) : null;

                bool mlApprove;
                float mlConfidence;
                float effectiveMLThreshold;

                if (mlPrediction != null)
                {
                    mlApprove = mlPrediction.ShouldEnter;
                    mlConfidence = mlPrediction.Probability;
                    effectiveMLThreshold = _onlineLearning?.CurrentMLThreshold ?? _config.MinMLConfidence;
                }
                else
                {
                    // ML 미가용: TF 단독 모드 — ML 조건 자동 통과
                    mlApprove = tfApprove;  // TF 판단에 따름
                    mlConfidence = 0f;
                    effectiveMLThreshold = 0f;
                    OnLog?.Invoke($"⚠️ [{symbol}] ML.NET 미가용 → TF 단독 모드 (TF={tfConfidence:P0})");
                }

                // 4. M15/M1 캔들 수집 (Fib 보너스 + 리스크/규칙 필터 공용)
                var m15List = (await _exchangeService
                    .GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes, 100, token))?
                    .ToList() ?? new List<IBinanceKline>();

                List<IBinanceKline> m1List = new();
                if (IsMajorSymbol(symbol) && decision.Contains("LONG", StringComparison.OrdinalIgnoreCase))
                {
                    m1List = (await _exchangeService
                        .GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneMinute, 5, token))?
                        .ToList() ?? new List<IBinanceKline>();
                }

                var fibSignal = EvaluateFibonacciSupportSignal(symbol, decision, currentPrice, m15List, m1List);
                float fibConfidenceBonus = (float)(fibSignal.BonusScore / 100.0);

                bool tfPass = tfApprove && (tfConfidence + fibConfidenceBonus) >= effectiveTFThreshold;
                bool mlPass = mlApprove && (mlConfidence + fibConfidenceBonus) >= effectiveMLThreshold;

                var detail = new AIEntryDetail
                {
                    ML_Approve = mlApprove,
                    ML_Confidence = mlConfidence,
                    TF_Approve = tfApprove,
                    TF_Confidence = tfConfidence,
                    TrendScore = Math.Min(1f, tfConfidence + fibConfidenceBonus),
                    DoubleCheckPassed = mlPass || tfPass,
                    M15_RSI = feature?.M15_RSI ?? 0f,
                    M15_BBPosition = feature?.M15_BBPosition ?? 0f,
                    FibonacciBonusScore = (float)fibSignal.BonusScore,
                    Fib618 = (double)fibSignal.Fib618,
                    Fib786 = (double)fibSignal.Fib786,
                    FibReversalConfirmed = fibSignal.ReversalConfirmed,
                    FibDeadCatBlocked = fibSignal.DeadCatBlocked
                };

                // 5. 데이터 수집 (실시간 학습용)
                string decisionId = RecordEntryDecision(feature, mlPrediction, tfApprove, tfConfidence, detail.DoubleCheckPassed);
                detail.DecisionId = decisionId;

                if (fibSignal.DeadCatBlocked)
                {
                    detail.DoubleCheckPassed = false;
                    OnLog?.Invoke($"❌ [{symbol}] [DEADCAT_BLOCK] {fibSignal.Reason} | Trend={detail.TrendScore:P0}");
                    return (false, $"DeadCat_Block_{fibSignal.Reason}_Trend={detail.TrendScore:P1}_FibBonus={fibSignal.BonusScore:F0}", detail);
                }

                if (fibSignal.BonusScore > 0)
                {
                    OnLog?.Invoke($"🎯 [{symbol}] [FIB_SUPPORT_BONUS] 0.618~0.786 지지+1m 리버설 확인, +{fibSignal.BonusScore:F0}점");
                }

                if (!tfPass && !mlPass)
                {
                    string tfReason = tfApprove
                        ? $"TF 신뢰도 부족 ({tfConfidence + fibConfidenceBonus:P0} < {effectiveTFThreshold:P0})"
                        : $"TF 타점 범위 외 ({candlesToTarget:F1}캔들, 유효 범위 1-32)";
                    string mlReason = mlApprove
                        ? $"ML 신뢰도 부족 ({mlConfidence + fibConfidenceBonus:P0} < {effectiveMLThreshold:P0})"
                        : $"ML 진입 비승인 ({mlConfidence:P0})";
                    OnLog?.Invoke($"❌ [{symbol}] [DUAL_BLOCK] ML/TF 동시 미달: {mlReason}, {tfReason}");
                    return (false, $"Dual_Reject_ML={mlConfidence:P1}_TF={tfConfidence:P1}_Trend={detail.TrendScore:P1}_FibBonus={fibSignal.BonusScore:F0}", detail);
                }

                if (!tfPass)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] [BRAIN_SOFT_PASS] TF 미달이지만 ML 통과로 진입 유지 | ML={mlConfidence:P0}, TF={tfConfidence:P0}, Fib+={fibSignal.BonusScore:F0}");
                }

                if (!mlPass)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] [FILTER_SOFT_PASS] ML 미달이지만 TF 통과로 진입 유지 | ML={mlConfidence:P0}, TF={tfConfidence:P0}, Fib+={fibSignal.BonusScore:F0}");
                }

                // 6. ML 필터 보강: 과열/꼬리/추격 리스크 차단
                if (m15List.Count > 0)
                {
                    var sanityFilter = EvaluateDualGateRiskFilter(symbol, decision, currentPrice, feature!, tfConfidence, m15List);
                    if (!sanityFilter.passed)
                    {
                        detail.DoubleCheckPassed = false;
                        OnLog?.Invoke($"❌ [{symbol}] [FILTER_BLOCK] {sanityFilter.reason}");
                        return (false, $"Sanity_Filter_{sanityFilter.reason}", detail);
                    }
                }

                // 7. 엘리엇 파동 + 피보나치 규칙 검증 (거부 필터)
                if (m15List.Count > 0)
                {
                    PositionSide side = decision.Contains("SHORT", StringComparison.OrdinalIgnoreCase)
                        ? PositionSide.Short
                        : PositionSide.Long;

                    var ruleCheck = _ruleValidator.ValidateEntryRules(
                        m15List,
                        symbol,
                        currentPrice,
                        side,
                        mlConfidence,
                        tfConfidence,
                        detail.M15_RSI,
                        detail.M15_BBPosition,
                        out var waveState,
                        out var fibLevels);

                    if (!ruleCheck.passed)
                    {
                        // [핫픽스] TF 80%+ 고신뢰 시 엘리엇 규칙 1/2 위반을 경고로 다운그레이드
                        // 엘리엇 파동은 주관적 해석이므로 TF가 강하게 확신하면 진입 허용
                        bool isElliottRuleBlock = ruleCheck.reason.Contains("Elliott_Rule1") || ruleCheck.reason.Contains("Elliott_Rule2");
                        bool tfHighConfidence = tfConfidence >= 0.80f;

                        if (isElliottRuleBlock && tfHighConfidence)
                        {
                            OnLog?.Invoke($"⚠️ [{symbol}] 엘리엇 규칙 위반이나 TF={tfConfidence:P0} 고신뢰로 바이패스: {ruleCheck.reason}");
                            // 바이패스: 진입은 허용하되 기록 남김
                        }
                        else
                        {
                            detail.DoubleCheckPassed = false;
                            OnLog?.Invoke($"❌ [{symbol}] 규칙 위반 거부: {ruleCheck.reason}");
                            return (false, $"Rule_Violation_{ruleCheck.reason}", detail);
                        }
                    }

                    detail.ElliottValid = waveState.IsValid;
                    detail.FibInEntryZone = fibLevels.InEntryZone;
                }

                // 8. 최종 승인 (ML 또는 TF + Fib 보너스 + 리스크 필터 + 규칙 필터)
                OnLog?.Invoke($"✅ [{symbol}] AI 더블체크 승인! Trend={detail.TrendScore:P0}, ML={mlConfidence:P0}, TF={tfConfidence:P0}, Fib+={fibSignal.BonusScore:F0}");
                return (true, $"DoubleCheck_PASS_Trend={detail.TrendScore:P1}_ML={mlConfidence:P1}_TF={tfConfidence:P1}_FibBonus={fibSignal.BonusScore:F0}", detail);
            }
            catch (Exception ex)
            {
                return (false, $"Exception_{ex.Message}", new AIEntryDetail());
            }
        }

        /// <summary>
        /// 메이저 코인 vs 펌핑 코인 별도 처리
        /// </summary>
        public async Task<(bool allowEntry, string reason, AIEntryDetail detail)> EvaluateEntryWithCoinTypeAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CoinType coinType,
            CancellationToken token = default)
        {
            var (allow, reason, detail) = await EvaluateEntryAsync(symbol, decision, currentPrice, token);
            var symbolThreshold = _config.GetThresholdBySymbol(symbol);

            if (!allow)
                return (allow, reason, detail);

            float fibBonusConfidence = detail.FibonacciBonusScore / 100f;

            // 메이저 코인: 고신뢰 임계값 적용 (ML/TF 중 하나만 충족해도 통과)
            if (coinType == CoinType.Major)
            {
                float majorMinConf = Math.Max(_config.MinMLConfidenceMajor, symbolThreshold.AiConfidenceMin);
                bool majorMlPass = detail.ML_Approve && (detail.ML_Confidence + fibBonusConfidence) >= majorMinConf;
                bool majorTfPass = detail.TF_Approve && (detail.TF_Confidence + fibBonusConfidence) >= Math.Max(_config.MinTransformerConfidenceMajor, symbolThreshold.AiConfidenceMin);
                if (!majorMlPass && !majorTfPass)
                {
                    return (false, $"Major_Threshold_Not_Met_ML={detail.ML_Confidence:P1}_TF={detail.TF_Confidence:P1}", detail);
                }
            }
            // 펌핑 코인: 별도 모델 (TODO: 펌핑 전용 모델 학습)
            else if (coinType == CoinType.Pumping)
            {
                // 펌핑 코인은 변동성 리스크가 높아 별도 강화 threshold 적용
                float pumpMlThreshold = Math.Max(Math.Max(_config.MinMLConfidencePumping, _config.MinMLConfidence), symbolThreshold.AiConfidenceMin);
                float pumpTfThreshold = Math.Max(Math.Max(_config.MinTransformerConfidencePumping, _config.MinTransformerConfidence), symbolThreshold.AiConfidenceMin);

                bool pumpMlPass = detail.ML_Approve && (detail.ML_Confidence + fibBonusConfidence) >= pumpMlThreshold;
                bool pumpTfPass = detail.TF_Approve && (detail.TF_Confidence + fibBonusConfidence) >= pumpTfThreshold;
                if (!pumpMlPass && !pumpTfPass)
                {
                    return (false, $"Pumping_Threshold_Not_Met_ML={detail.ML_Confidence:P1}_TF={detail.TF_Confidence:P1}", detail);
                }
            }

            return (allow, reason, detail);
        }

        private (bool passed, string reason) EvaluateDualGateRiskFilter(
            string symbol,
            string decision,
            decimal currentPrice,
            MultiTimeframeEntryFeature feature,
            float trendScore,
            List<IBinanceKline> m15Candles)
        {
            if (!decision.Contains("LONG", StringComparison.OrdinalIgnoreCase))
                return (true, "Not_Long_Direction");

            float rsi = feature.M15_RSI;
            float bbPosition = feature.M15_BBPosition;
            var symbolThreshold = _config.GetThresholdBySymbol(symbol);
            float rsiHardCap = Math.Min(_config.RsiOverheatHardCap, symbolThreshold.MaxRsiLimit);
            float upperWickRatio = CalculateUpperWickRatio(m15Candles[^1]);
            decimal pullbackFromRecentHighPct = CalculatePullbackFromRecentHighPct(currentPrice, m15Candles, 20);

            if (rsi >= rsiHardCap)
                return (false, $"RSI_Overheat_{rsi:F1}_GE_{rsiHardCap:F1}");

            if (upperWickRatio >= _config.UpperWickRiskThreshold)
                return (false, $"UpperWick_Risk_{upperWickRatio:P0}_GE_{_config.UpperWickRiskThreshold:P0}");

            bool strongTrend = trendScore >= _config.StrongTrendBypassThreshold;

            // ── [Staircase Pursuit] 연속 Higher Lows + BB 중단 지지 감지 ────────────────────────────
            // 벼린저 중단 위 + 3연속 저점 상승(계단식) + TF 기준치 측 충족 시 추격 리스크 필터 무시
            bool isStaircaseUptrend = trendScore >= _config.StaircaseTfMinThreshold
                && DetectStaircaseUptrend(currentPrice, feature.M15_BBPosition, m15Candles);

            if (isStaircaseUptrend)
            {
                OnLog?.Invoke($"🪜 [{symbol}] [STAIRCASE_PURSUIT] 계단식 상승 감지: HigherLows + BB중단 지지 → Chasing 필터 바이패스 (TF={trendScore:P0})");
                return (true, $"Staircase_Pursuit_Bypass_TF={trendScore:P0}_BB={feature.M15_BBPosition:P0}");
            }

            if (!strongTrend &&
                bbPosition >= _config.BbUpperRiskThreshold &&
                rsi >= _config.RsiCautionThreshold)
            {
                return (false, $"UpperBand_Overheat_BB={bbPosition:P0}_RSI={rsi:F1}");
            }

            if (!strongTrend &&
                pullbackFromRecentHighPct >= 0m &&
                pullbackFromRecentHighPct <= (decimal)_config.RecentHighChaseThresholdPct)
            {
                // [Staircase Pursuit] 계단식 상승의 경우 pullback 조건도 스통 (위에서 이미 처리되지만 안전 문구)
                return (false, $"Chasing_Risk_Pullback={pullbackFromRecentHighPct:F2}%");
            }

            return (true, "Risk_Filter_Passed");
        }

        /// <summary>[Staircase Pursuit] 연속 Higher Lows(3봉) + 가격이 BB 중단 위인지 판단</summary>
        private static bool DetectStaircaseUptrend(
            decimal currentPrice,
            float bbPosition,
            List<IBinanceKline> m15Candles)
        {
            if (m15Candles == null || m15Candles.Count < 4) return false;
            // BB 중단 위(좌표 50% 초과)
            if (bbPosition <= 0.50f) return false;
            // 연속 3봉 Higher Lows
            var recent = m15Candles.TakeLast(4).ToList();
            for (int i = 1; i < recent.Count; i++)
                if (recent[i].LowPrice <= recent[i - 1].LowPrice) return false;
            return true;
        }

        private (double BonusScore, bool DeadCatBlocked, string Reason, decimal Fib618, decimal Fib786, bool ReversalConfirmed)
            EvaluateFibonacciSupportSignal(
                string symbol,
                string decision,
                decimal currentPrice,
                List<IBinanceKline> m15Candles,
                List<IBinanceKline> m1Candles)
        {
            if (!IsMajorSymbol(symbol) ||
                !decision.Contains("LONG", StringComparison.OrdinalIgnoreCase))
            {
                return (0d, false, "Fib_Not_Applicable", 0m, 0m, false);
            }

            if (m15Candles == null || m15Candles.Count < Math.Max(8, _config.FibonacciWaveLookbackCandles / 2))
            {
                return (0d, false, "Fib_M15_Data_Insufficient", 0m, 0m, false);
            }

            var waveCandles = m15Candles.TakeLast(Math.Max(8, _config.FibonacciWaveLookbackCandles)).ToList();
            decimal waveHigh = waveCandles.Max(c => c.HighPrice);
            decimal waveLow = waveCandles.Min(c => c.LowPrice);
            decimal range = waveHigh - waveLow;
            if (range <= 0m)
            {
                return (0d, false, "Fib_Range_Invalid", 0m, 0m, false);
            }

            decimal fib618 = waveHigh - (range * (decimal)_config.FibonacciSupportUpper);
            decimal fib786 = waveHigh - (range * (decimal)_config.FibonacciSupportLower);

            if (m1Candles == null || m1Candles.Count < 2)
            {
                return (0d, false, "Fib_M1_Data_Insufficient", fib618, fib786, false);
            }

            var prev = m1Candles[^2];
            var last = m1Candles[^1];

            if (IsStrongDeadCatBreakdown(last, fib786, _config.DeadCatBodyBreakRatio))
            {
                return (0d, true,
                    $"Fib786_BodyBreak_Close={last.ClosePrice:F6}_Fib786={fib786:F6}",
                    fib618, fib786, false);
            }

            bool inSupportZone = currentPrice <= fib618 && currentPrice >= fib786;
            bool bullishReversal = IsBullishReversal(prev, last, _config.ReversalBodyRatioThreshold);

            if (inSupportZone && bullishReversal)
            {
                return (_config.FibonacciSupportBonusScore, false, "Fib_Support_Reversal", fib618, fib786, true);
            }

            return (0d, false, "Fib_No_Bonus", fib618, fib786, bullishReversal);
        }

        private static bool IsBullishReversal(IBinanceKline previous, IBinanceKline current, float minBodyRatio)
        {
            bool prevBearish = previous.ClosePrice < previous.OpenPrice;
            bool currentBullish = current.ClosePrice > current.OpenPrice;
            if (!prevBearish || !currentBullish)
                return false;

            decimal currentRange = current.HighPrice - current.LowPrice;
            if (currentRange <= 0m)
                return false;

            decimal currentBody = Math.Abs(current.ClosePrice - current.OpenPrice);
            decimal bodyRatio = currentBody / currentRange;
            bool bodyStrong = bodyRatio >= (decimal)minBodyRatio;

            bool closeRecovered = current.ClosePrice >= previous.OpenPrice
                                 || current.ClosePrice > previous.ClosePrice;

            return bodyStrong && closeRecovered;
        }

        private static bool IsStrongDeadCatBreakdown(IBinanceKline candle, decimal fib786, float minBodyRatio)
        {
            bool bearish = candle.ClosePrice < candle.OpenPrice;
            if (!bearish)
                return false;

            bool bodyBreak = candle.OpenPrice >= fib786 && candle.ClosePrice < fib786;
            if (!bodyBreak)
                return false;

            decimal range = candle.HighPrice - candle.LowPrice;
            if (range <= 0m)
                return false;

            decimal body = Math.Abs(candle.OpenPrice - candle.ClosePrice);
            decimal bodyRatio = body / range;
            return bodyRatio >= (decimal)minBodyRatio;
        }

        private static bool IsMajorSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            return symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("SOL", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);
        }

        private static float CalculateUpperWickRatio(IBinanceKline candle)
        {
            decimal range = candle.HighPrice - candle.LowPrice;
            if (range <= 0m)
                return 0f;

            decimal bodyTop = Math.Max(candle.OpenPrice, candle.ClosePrice);
            decimal upperWick = candle.HighPrice - bodyTop;
            if (upperWick <= 0m)
                return 0f;

            decimal ratio = upperWick / range;
            return (float)Math.Clamp(ratio, 0m, 1m);
        }

        private static decimal CalculatePullbackFromRecentHighPct(
            decimal currentPrice,
            List<IBinanceKline> candles,
            int lookback)
        {
            if (candles == null || candles.Count == 0)
                return -1m;

            var recent = candles.TakeLast(Math.Max(1, lookback)).ToList();
            decimal recentHigh = recent.Max(c => c.HighPrice);
            if (recentHigh <= 0m || currentPrice <= 0m)
                return -1m;

            decimal pullback = (recentHigh - currentPrice) / recentHigh * 100m;
            return pullback < 0m ? 0m : pullback;
        }

        /// <summary>
        /// 진입 결정 기록 (학습 데이터 수집)
        /// </summary>
        /// <summary>
        /// Navigator 전용: Transformer Time-to-Target 예측만 수행
        /// (ML.NET 없이 경량 계산)
        /// </summary>
        public async Task<(float candlesToTarget, float confidence)> GetTransformerPredictionAsync(
            List<MultiTimeframeEntryFeature> recentFeatures)
        {
            try
            {
                if (!IsReady || recentFeatures == null || recentFeatures.Count == 0)
                    return (-1f, 0f);

                return await Task.Run(() => _transformerTrainer.Predict(recentFeatures));
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ Transformer 예측 오류: {ex.Message}");
                return (-1f, 0f);
            }
        }

        private string RecordEntryDecision(
            MultiTimeframeEntryFeature? feature,
            EntryTimingPrediction mlPred,
            bool tfApprove,
            float tfConf,
            bool finalDecision)
        {
            string decisionId = Guid.NewGuid().ToString("N");

            var safeFeature = feature ?? new MultiTimeframeEntryFeature
            {
                Symbol = string.Empty,
                EntryPrice = 0m,
                Timestamp = DateTime.UtcNow
            };

            var record = new EntryDecisionRecord
            {
                DecisionId = decisionId,
                Timestamp = DateTime.UtcNow,
                Symbol = safeFeature.Symbol,
                EntryPrice = safeFeature.EntryPrice,
                ML_Approve = mlPred.ShouldEnter,
                ML_Confidence = mlPred.Probability,
                TF_Approve = tfApprove,
                TF_Confidence = tfConf,
                FinalDecision = finalDecision,
                Feature = safeFeature,
                // ActualProfit는 15분 후 별도 업데이트
                ActualProfitPct = null,
                Labeled = false
            };

            _pendingRecords.Enqueue(record);

            // 큐 오버플로우 방지
            if (_pendingRecords.Count > 10000)
            {
                FlushCollectedData();
            }

            return decisionId;
        }

        /// <summary>
        /// 수집된 데이터를 파일로 저장
        /// </summary>
        private void FlushCollectedData()
        {
            if (_pendingRecords.IsEmpty)
                return;

            try
            {
                var records = new List<EntryDecisionRecord>();
                while (_pendingRecords.TryDequeue(out var record))
                {
                    records.Add(record);
                }

                if (records.Count == 0)
                    return;

                string filename = $"EntryDecisions_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                string filepath = Path.Combine(_dataCollectionPath, filename);

                var json = JsonSerializer.Serialize(records, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(filepath, json);

                Console.WriteLine($"[AIDoubleCheck] 데이터 저장: {records.Count}개 레코드 → {filepath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 데이터 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 진입 후 실제 수익률 레이블링 (15분 후 호출)
        /// </summary>
        public async Task LabelActualProfitAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            bool isLong,
            string? decisionId = null,
            CancellationToken token = default)
        {
            try
            {
                decimal currentPrice = entryPrice;
                try
                {
                    currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                }
                catch
                {
                    currentPrice = entryPrice;
                }

                float markToMarketPct = 0f;
                if (entryPrice > 0)
                {
                    markToMarketPct = isLong
                        ? (float)((currentPrice - entryPrice) / entryPrice * 100m)
                        : (float)((entryPrice - currentPrice) / entryPrice * 100m);
                }

                bool updated = await UpsertLabelInRecentFilesAsync(
                    symbol,
                    entryTime,
                    entryPrice,
                    markToMarketPct,
                    decisionId,
                    labelSource: "mark_to_market_15m",
                    overwriteExisting: false,
                    token: token);

                if (updated)
                {
                    Console.WriteLine($"[AIDoubleCheck] 15분 라벨 업데이트: {symbol} pnl={markToMarketPct:F2}%");
                    
                    // 온라인 학습 + 외부 저장용 샘플 이벤트
                    await AddLabeledSampleToOnlineLearningAsync(symbol, entryTime, entryPrice, markToMarketPct, "mark_to_market_15m", token);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 레이블링 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 포지션 청산 확정 결과로 실제 수익률 라벨을 덮어씁니다.
        /// </summary>
        public async Task LabelActualProfitByCloseAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            float actualProfitPct,
            string? closeReason = null,
            string? decisionId = null,
            CancellationToken token = default)
        {
            try
            {
                bool updated = await UpsertLabelInRecentFilesAsync(
                    symbol,
                    entryTime,
                    entryPrice,
                    actualProfitPct,
                    decisionId,
                    labelSource: string.IsNullOrWhiteSpace(closeReason) ? "trade_close" : $"trade_close:{closeReason}",
                    overwriteExisting: true,
                    token: token);

                if (updated)
                {
                    Console.WriteLine($"[AIDoubleCheck] 청산 라벨 반영: {symbol} pnl={actualProfitPct:F2}%");
                    
                    // 온라인 학습 + 외부 저장용 샘플 이벤트
                    await AddLabeledSampleToOnlineLearningAsync(
                        symbol,
                        entryTime,
                        entryPrice,
                        actualProfitPct,
                        string.IsNullOrWhiteSpace(closeReason) ? "trade_close" : $"trade_close:{closeReason}",
                        token);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 청산 라벨 반영 실패: {ex.Message}");
            }
        }

        private async Task<bool> UpsertLabelInRecentFilesAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            float actualProfitPct,
            string? decisionId,
            string labelSource,
            bool overwriteExisting,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            string symbolUpper = symbol.ToUpperInvariant();
            DateTime entryUtc = entryTime.Kind switch
            {
                DateTimeKind.Utc => entryTime,
                DateTimeKind.Local => entryTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(entryTime, DateTimeKind.Local).ToUniversalTime()
            };

            var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json")
                .OrderByDescending(f => f)
                .Take(30);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                var json = await File.ReadAllTextAsync(file, token);
                var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);

                if (records == null || records.Count == 0)
                    continue;

                EntryDecisionRecord? target = null;

                if (!string.IsNullOrWhiteSpace(decisionId))
                {
                    target = records.FirstOrDefault(r =>
                        string.Equals(r.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)
                        && (overwriteExisting || !r.Labeled));
                }

                target ??= records
                    .Where(r => string.Equals(r.Symbol, symbolUpper, StringComparison.OrdinalIgnoreCase))
                    .Where(r => overwriteExisting || !r.Labeled)
                    .OrderBy(r =>
                    {
                        DateTime recordUtc = r.Timestamp.Kind == DateTimeKind.Utc
                            ? r.Timestamp
                            : DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc);

                        double timeDiff = Math.Abs((recordUtc - entryUtc).TotalMinutes);
                        double priceDiffPct = entryPrice > 0m
                            ? (double)(Math.Abs(r.EntryPrice - entryPrice) / entryPrice)
                            : 1.0;

                        return priceDiffPct * 100.0 + timeDiff * 0.01;
                    })
                    .FirstOrDefault();

                if (target == null)
                    continue;

                target.ActualProfitPct = actualProfitPct;
                target.Labeled = true;
                target.LabelSource = labelSource;
                target.LabeledAt = DateTime.UtcNow;

                var updatedJson = JsonSerializer.Serialize(records, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(file, updatedJson, token);
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            _dataFlushTimer?.Dispose();
            _onlineLearning?.Dispose();
            FlushCollectedData(); // 종료 시 마지막 저장
        }

        /// <summary>
        /// 온라인 학습에 라벨링된 샘플 추가
        /// </summary>
        private async Task AddLabeledSampleToOnlineLearningAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            float actualProfitPct,
            string labelSource,
            CancellationToken token = default)
        {
            try
            {
                // Feature 재추출 (진입 시점 기준)
                var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(symbol, entryTime, token);
                if (feature == null)
                    return;

                // 라벨 설정 (목표 +2%, 손절 -1% 기준)
                bool shouldEnter = actualProfitPct >= 2.0f;
                feature.ShouldEnter = shouldEnter;
                feature.ActualProfitPct = actualProfitPct;
                feature.Timestamp = entryTime;
                feature.EntryPrice = entryPrice;

                // 온라인 학습 서비스에 추가
                if (_onlineLearning != null)
                {
                    await _onlineLearning.AddLabeledSampleAsync(feature);
                    OnLog?.Invoke($"[OnlineLearning] 샘플 추가: {symbol} PnL={actualProfitPct:F2}% → Label={shouldEnter} | 윈도우={_onlineLearning.WindowSize}");
                }

                // 외부(DB) 적재용 이벤트 발행
                OnLabeledSample?.Invoke(new AiLabeledSample
                {
                    Symbol = symbol,
                    EntryTimeUtc = entryTime.Kind == DateTimeKind.Utc ? entryTime : entryTime.ToUniversalTime(),
                    EntryPrice = entryPrice,
                    ActualProfitPct = actualProfitPct,
                    IsSuccess = shouldEnter,
                    LabelSource = string.IsNullOrWhiteSpace(labelSource) ? "unknown" : labelSource,
                    Feature = feature
                });
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OnlineLearning] 샘플 추가 실패: {ex.Message}");
            }
        }

        public (int total, int labeled, int markToMarket, int tradeClose) GetRecentLabelStats(int maxFiles = 10)
        {
            try
            {
                int safeMaxFiles = Math.Max(1, maxFiles);
                var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json")
                    .OrderByDescending(f => f)
                    .Take(safeMaxFiles);

                int total = 0;
                int labeled = 0;
                int markToMarket = 0;
                int tradeClose = 0;

                foreach (var file in files)
                {
                    var json = File.ReadAllText(file);
                    var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);
                    if (records == null)
                        continue;

                    total += records.Count;

                    foreach (var record in records)
                    {
                        if (!record.Labeled)
                            continue;

                        labeled++;

                        if (string.Equals(record.LabelSource, "mark_to_market_15m", StringComparison.OrdinalIgnoreCase))
                            markToMarket++;

                        if (!string.IsNullOrWhiteSpace(record.LabelSource)
                            && record.LabelSource.StartsWith("trade_close", StringComparison.OrdinalIgnoreCase))
                        {
                            tradeClose++;
                        }
                    }
                }

                return (total, labeled, markToMarket, tradeClose);
            }
            catch
            {
                return (0, 0, 0, 0);
            }
        }

        private List<MultiTimeframeEntryFeature> BuildTransformerSequence(string symbol, MultiTimeframeEntryFeature latestFeature)
        {
            string key = string.IsNullOrWhiteSpace(symbol) ? "UNKNOWN" : symbol;
            int requiredSeqLen = _transformerTrainer?.SeqLen ?? 8;

            var buffer = _recentFeatureBuffers.GetOrAdd(key, _ => new Queue<MultiTimeframeEntryFeature>(requiredSeqLen));

            lock (buffer)
            {
                buffer.Enqueue(latestFeature);
                while (buffer.Count > requiredSeqLen)
                {
                    buffer.Dequeue();
                }

                var sequence = buffer.ToList();
                if (sequence.Count == 0)
                {
                    sequence.Add(latestFeature);
                }

                // 워밍업 구간: 길이가 부족하면 가장 오래된 feature로 앞쪽 패딩
                if (sequence.Count < requiredSeqLen)
                {
                    var padFeature = sequence[0];
                    int padCount = requiredSeqLen - sequence.Count;
                    for (int i = 0; i < padCount; i++)
                    {
                        sequence.Insert(0, padFeature);
                    }
                }

                return sequence;
            }
        }

        /// <summary>
        /// 심볼 목록에 대해 AI 진입 확률과 미래 진입 ETA를 배치 스캔합니다.
        /// 현재 시장 상태를 기준으로 다음 15분 슬롯들의 시간 컨텍스트를 시뮬레이션하여
        /// "언제가 가장 유리한 진입 시점인지"를 반환합니다.
        /// </summary>
        public async Task<Dictionary<string, AIEntryForecastResult>> ScanEntryProbabilitiesAsync(
            List<string> symbols,
            CancellationToken token = default)
        {
            var results = new Dictionary<string, AIEntryForecastResult>(StringComparer.OrdinalIgnoreCase);

            if (!IsReady)
                return results;

            DateTime referenceUtc = DateTime.UtcNow;
            DateTime referenceLocal = referenceUtc.ToLocalTime();

            foreach (var symbol in symbols)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var forecast = await ForecastEntryTimingAsync(symbol, referenceUtc, referenceLocal, token);
                    if (forecast != null)
                        results[symbol] = forecast;
                }
                catch
                {
                    // 개별 심볼 실패 무시
                }

                await Task.Delay(100, token); // API 부하 방지
            }

            return results;
        }

        private async Task<AIEntryForecastResult?> ForecastEntryTimingAsync(
            string symbol,
            DateTime referenceUtc,
            DateTime referenceLocal,
            CancellationToken token)
        {
            var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(symbol, referenceUtc, token);
            if (feature == null)
                return null;

            var baseSequence = BuildTransformerSequence(symbol, feature);
            var candidates = new List<AIEntryForecastResult>(_config.EntryForecastSteps + 1);

            // [Time-to-Target 회귀 기반 ETA 예측]
            // Transformer가 "목표가 도달까지 몇 캔들 후인지" 직접 예측
            var (candlesToTarget, tfConfidence) = _transformerTrainer?.Predict(baseSequence) ?? (-1f, 0f);

            // 현재 시점 ML 확률 계산
            float currentMlProb = 0f;
            var mlPrediction = _mlTrainer.Predict(feature);
            if (mlPrediction != null)
                currentMlProb = mlPrediction.Probability;

            AIEntryForecastResult best;

            // Time-to-Target이 유효한 경우 (1~32캔들 범위)
            if (candlesToTarget >= 1f && candlesToTarget <= 32f)
            {
                // 예측된 시간 계산
                int minutesToTarget = (int)Math.Round(candlesToTarget * 15); // 15분봉 기준
                var forecastUtc = referenceUtc.AddMinutes(minutesToTarget);
                
                // 해당 시점의 ML 확률 예측 (미래 시점 피처 생성)
                var futureFeature = feature.CloneWithTimestamp(forecastUtc);
                float futureMlProb = 0f;
                var futureMlPrediction = _mlTrainer.Predict(futureFeature);
                if (futureMlPrediction != null)
                    futureMlProb = futureMlPrediction.Probability;

                // 평균 확률 계산 (ML 스나이퍼 + TF 네비게이터)
                float avgProb = (futureMlProb + tfConfidence) / 2f;

                best = new AIEntryForecastResult
                {
                    Symbol = symbol,
                    MLProbability = futureMlProb,
                    TFProbability = tfConfidence,
                    AverageProbability = avgProb,
                    ForecastTimeUtc = forecastUtc,
                    ForecastTimeLocal = forecastUtc.ToLocalTime(),
                    ForecastOffsetMinutes = Math.Max(1, minutesToTarget),
                    GeneratedAtLocal = referenceLocal
                };
            }
            else
            {
                // Time-to-Target 예측 실패 또는 범위 외 → 현재 시점 기준 반환
                float avgProb = (currentMlProb + tfConfidence) / 2f;
                best = new AIEntryForecastResult
                {
                    Symbol = symbol,
                    MLProbability = currentMlProb,
                    TFProbability = tfConfidence,
                    AverageProbability = avgProb,
                    ForecastTimeUtc = referenceUtc,
                    ForecastTimeLocal = referenceLocal,
                    ForecastOffsetMinutes = 0,
                    GeneratedAtLocal = referenceLocal
                };
            }

            return best;
        }

        /// <summary>
        /// [DEPRECATED] 이진 분류 기반 확률 예측 (회귀 모델 전환으로 미사용)
        /// </summary>
        private (float mlProb, float tfProb, float avgProb) PredictEntryProbabilities(
            MultiTimeframeEntryFeature feature,
            List<MultiTimeframeEntryFeature> transformerSequence)
        {
            float mlProb = 0f;
            var mlPrediction = _mlTrainer.Predict(feature);
            if (mlPrediction != null)
                mlProb = mlPrediction.Probability;

            // Transformer는 이제 Time-to-Target 회귀 모델이므로 확률 반환 불가
            // 호환성을 위해 0 반환
            float tfProb = 0f;
            float avgProb = (mlProb + tfProb) / 2f;

            return (mlProb, tfProb, avgProb);
        }

        private List<MultiTimeframeEntryFeature> ReplaceLastSequenceFeature(
            List<MultiTimeframeEntryFeature> baseSequence,
            MultiTimeframeEntryFeature latestFeature)
        {
            var sequence = new List<MultiTimeframeEntryFeature>(baseSequence.Count);
            for (int i = 0; i < baseSequence.Count; i++)
            {
                sequence.Add(i == baseSequence.Count - 1 ? latestFeature : baseSequence[i]);
            }

            return sequence;
        }

        private AIEntryForecastResult SelectBestForecast(List<AIEntryForecastResult> candidates)
        {
            if (candidates.Count == 0)
                return new AIEntryForecastResult();

            var current = candidates[0];
            var best = current;
            double bestScore = GetForecastScore(best);

            foreach (var candidate in candidates.Skip(1))
            {
                double score = GetForecastScore(candidate);
                if (score > bestScore ||
                    (Math.Abs(score - bestScore) < 0.0001 && candidate.ForecastOffsetMinutes < best.ForecastOffsetMinutes))
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (current.AverageProbability >= _config.EntryForecastImmediateThreshold &&
                current.AverageProbability + _config.EntryForecastImmediateTolerance >= best.AverageProbability)
            {
                return current;
            }

            return best;
        }

        private double GetForecastScore(AIEntryForecastResult candidate)
        {
            double stepPenalty = (candidate.ForecastOffsetMinutes / 15.0) * _config.EntryForecastTimePenaltyPerStep;
            return candidate.AverageProbability - stepPenalty;
        }

        private static DateTime AlignToNextQuarterHourUtc(DateTime utcTime)
        {
            int remainder = utcTime.Minute % 15;
            if (remainder == 0 && utcTime.Second == 0 && utcTime.Millisecond == 0)
                return utcTime;

            int addMinutes = remainder == 0 ? 15 : 15 - remainder;
            var truncated = new DateTime(utcTime.Year, utcTime.Month, utcTime.Day, utcTime.Hour, utcTime.Minute, 0, DateTimeKind.Utc);
            return truncated.AddMinutes(addMinutes);
        }

        /// <summary>
        /// 초기 학습 트리거 (모델 파일이 없을 때 기본 데이터로 빠르게 학습)
        /// </summary>
        public async Task<(bool success, string message)> TriggerInitialTrainingAsync(
            IExchangeService exchangeService,
            List<string> symbols,
            CancellationToken token = default)
        {
            if (IsReady)
                return (true, "모델이 이미 준비되었습니다.");

            bool signalUiSuspended = false;
            try
            {
                // [병목 해결] AI 초기 학습 중 UI 시그널 업데이트 일시 중단
                UISuspensionManager.SuspendSignalUpdates(true);
                signalUiSuspended = true;
                OnAlert?.Invoke("🔄 [AI 학습] 초기 학습 시작: 히스토리컬 데이터 수집 중... (UI 업데이트 일시 중단)");

                // 1. 히스토리컬 데이터로 대량 Feature 생성 (심볼당 수십 개)
                var trainingFeatures = new List<MultiTimeframeEntryFeature>();
                int maxSymbols = Math.Min(symbols.Count, 20);
                
                foreach (var symbol in symbols.Take(maxSymbols))
                {
                    try
                    {
                        OnLog?.Invoke($"  - {symbol} 히스토리컬 데이터 수집 중...");
                        
                        // 각 타임프레임 캔들 수집
                        var d1Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneDay, 50, token);
                        var h4Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.FourHour, 120, token);
                        var h2Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.TwoHour, 120, token);
                        var h1Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneHour, 200, token);
                        var m15Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes, 260, token);
                        
                        await Task.WhenAll(d1Task, h4Task, h2Task, h1Task, m15Task);
                        
                        var d1 = d1Task.Result?.ToList();
                        var h4 = h4Task.Result?.ToList();
                        var h2 = h2Task.Result?.ToList();
                        var h1 = h1Task.Result?.ToList();
                        var m15 = m15Task.Result?.ToList();
                        
                        if (d1 != null && d1.Count >= 20 &&
                            h4 != null && h4.Count >= 40 &&
                            h2 != null && h2.Count >= 40 &&
                            h1 != null && h1.Count >= 50 &&
                            m15 != null && m15.Count >= 100)
                        {
                            // 히스토리컬 Feature 대량 생성 (슬라이딩 윈도우)
                            var historicalFeatures = _featureExtractor.ExtractHistoricalFeatures(
                                symbol, d1, h4, h2, h1, m15, _labeler, isLongStrategy: true);
                            
                            trainingFeatures.AddRange(historicalFeatures);
                            OnLog?.Invoke($"  ✓ {symbol} 완료 ({historicalFeatures.Count}개 Feature, 총 {trainingFeatures.Count}개)");
                        }
                        else
                        {
                            OnLog?.Invoke($"  ⚠ {symbol} 데이터 부족 스킵");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"  ⚠ {symbol} 실패: {ex.Message}");
                    }
                    
                    await Task.Delay(300, token); // API 제한 방지
                }

                OnAlert?.Invoke($"📊 [AI 학습] 데이터 수집 완료: {trainingFeatures.Count}개 Feature");

                if (trainingFeatures.Count < 10)
                {
                    string errMsg = $"❌ [AI 학습] 히스토리컬 데이터 부족 ({trainingFeatures.Count}개) - 기존 WaveGate로 동작합니다";
                    OnAlert?.Invoke(errMsg);
                    return (false, errMsg);
                }

                // 2. ML.NET 모델 학습
                OnAlert?.Invoke($"🧠 [AI 학습] ML.NET 모델 학습 중... (샘플: {trainingFeatures.Count}개)");
                string mlInitRunId = $"INIT_ML_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                try
                {
                    var mlMetrics = await _mlTrainer.TrainAndSaveAsync(trainingFeatures);
                    OnLog?.Invoke($"[AIDoubleCheck] ML.NET 학습 완료 - Accuracy: {mlMetrics.Accuracy:P2}, F1: {mlMetrics.F1Score:P2}");

                    await PersistTrainingRunAsync(
                        projectName: "ML.NET",
                        runId: mlInitRunId,
                        stage: "Initial",
                        success: true,
                        sampleCount: trainingFeatures.Count,
                        epochs: 0,
                        accuracy: mlMetrics.Accuracy,
                        f1Score: mlMetrics.F1Score,
                        auc: mlMetrics.AUC,
                        detail: "AIDoubleCheck 초기 학습 완료");

                    RaiseCriticalTrainingAlert(
                        projectName: "ML.NET",
                        stage: "초기학습",
                        success: true,
                        detail: $"Acc={mlMetrics.Accuracy:P2}, F1={mlMetrics.F1Score:P2}, 샘플={trainingFeatures.Count}");
                }
                catch (Exception mlEx)
                {
                    OnAlert?.Invoke($"❌ [AI 학습] ML.NET 학습 실패: {mlEx.Message}");
                    OnLog?.Invoke($"[AIDoubleCheck] ML.NET 학습 상세 오류:\n{mlEx}");

                    await PersistTrainingRunAsync(
                        projectName: "ML.NET",
                        runId: mlInitRunId,
                        stage: "Initial",
                        success: false,
                        sampleCount: trainingFeatures.Count,
                        epochs: 0,
                        detail: mlEx.Message);

                    RaiseCriticalTrainingAlert(
                        projectName: "ML.NET",
                        stage: "초기학습",
                        success: false,
                        detail: mlEx.Message);
                }

                // 3. Transformer 모델 학습 (빠른 초기화: 1 epoch)
                OnAlert?.Invoke("🧠 [AI 학습] Transformer 모델 학습 중... (1-2분 소요)");
                string tfInitRunId = $"INIT_TF_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                try
                {
                    OnLog?.Invoke($"[AIDoubleCheck] TensorFlow 초기학습 시작");

                    using var tfTrainTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    tfTrainTimeout.CancelAfter(TimeSpan.FromMinutes(4));

                    var tfMetrics = await _transformerTrainer.TrainAsync(trainingFeatures, epochs: 1, batchSize: 16, token: tfTrainTimeout.Token);
                    OnLog?.Invoke($"[AIDoubleCheck] TensorFlow Transformer 학습 완료 - Val Loss: {tfMetrics.BestValidationLoss:F4}");

                    await PersistTrainingRunAsync(
                        projectName: "TensorFlow",
                        runId: tfInitRunId,
                        stage: "Initial",
                        success: true,
                        sampleCount: trainingFeatures.Count,
                        epochs: 1,
                        bestValidationLoss: tfMetrics.BestValidationLoss,
                        finalTrainLoss: tfMetrics.FinalTrainLoss,
                        detail: "TensorFlow 초기 학습 완료");

                    RaiseCriticalTrainingAlert(
                        projectName: "TensorFlow",
                        stage: "초기학습",
                        success: true,
                        detail: $"BestLoss={tfMetrics.BestValidationLoss:F4}, 샘플={trainingFeatures.Count}");

                    OnAlert?.Invoke($"✅ [AI 학습] Transformer 모델 학습 완료 (BestLoss={tfMetrics.BestValidationLoss:F4})");
                }
                catch (Exception tfEx)
                {
                    OnAlert?.Invoke($"❌ [AI 학습] Transformer 학습 실패: {tfEx.Message}");
                    OnLog?.Invoke($"[AIDoubleCheck] Transformer 학습 상세 오류:\n{tfEx}");

                    await PersistTrainingRunAsync(
                        projectName: "TensorFlow",
                        runId: tfInitRunId,
                        stage: "Initial",
                        success: false,
                        sampleCount: trainingFeatures.Count,
                        epochs: 1,
                        detail: tfEx.Message);

                    RaiseCriticalTrainingAlert(
                        projectName: "TensorFlow",
                        stage: "초기학습",
                        success: false,
                        detail: tfEx.Message);
                }

                // 4. 모델 리로드 및 상태 확인
                _mlTrainer.LoadModel();
                _transformerTrainer?.LoadModel();

                bool tfReady = _transformerTrainer?.IsModelReady ?? false;

                if (IsReady)
                {
                    string msg = $"✅ [AI 학습] 초기 학습 완료! ML Ready: {_mlTrainer.IsModelLoaded}, TF Ready: {tfReady}";
                    OnAlert?.Invoke(msg);
                    OnLog?.Invoke(msg);
                    return (true, msg);
                }
                else
                {
                    string errMsg = $"⚠️ [AI 학습] 학습 후 모델 상태 - ML: {(_mlTrainer.IsModelLoaded ? "OK" : "FAIL")}, TF: {(tfReady ? "OK" : "FAIL")}";
                    OnAlert?.Invoke(errMsg);
                    return (false, errMsg);
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"❌ [AI 학습] 초기 학습 실패: {ex.Message}";
                OnAlert?.Invoke(errorMsg);
                OnLog?.Invoke($"[AIDoubleCheck] 초기 학습 오류 스택:\n{ex}");
                return (false, errorMsg);
            }
            finally
            {
                if (signalUiSuspended)
                {
                    UISuspensionManager.SuspendSignalUpdates(false);
                }
            }
        }

        /// <summary>
        /// 정기 재학습 (수집된 라벨링 데이터 활용)
        /// </summary>
        public async Task<(bool success, string message)> RetrainModelsAsync(CancellationToken token = default)
        {
            try
            {
                // 0. 재학습 전 기존 파일 전면 재검수/정화
                var sanitizeStats = await SanitizeEntryDecisionFilesAsync(token);
                OnLog?.Invoke($"🧹 [LookAhead Shield] 재학습 전 정화: 파일 {sanitizeStats.filesScanned}개, 제거 {sanitizeStats.removedRecords}/{sanitizeStats.totalRecords}건");

                // 1. 저장된 라벨링 데이터 로드
                var labeledData = LoadLabeledDataFromFiles();
                if (labeledData.Count < 100)
                {
                    return (false, $"재학습 데이터 부족 ({labeledData.Count}/100)");
                }

                OnAlert?.Invoke($"🔄 [AI 재학습] 시작: {labeledData.Count}개 라벨링 데이터");

                // 2. ML.NET 재학습
                string mlRetrainRunId = $"RETRAIN_ML_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                ModelMetrics mlMetrics;
                try
                {
                    mlMetrics = await _mlTrainer.TrainAndSaveAsync(labeledData);
                    _mlTrainer.LoadModel();

                    await PersistTrainingRunAsync(
                        projectName: "ML.NET",
                        runId: mlRetrainRunId,
                        stage: "Retrain",
                        success: true,
                        sampleCount: labeledData.Count,
                        epochs: 0,
                        accuracy: mlMetrics.Accuracy,
                        f1Score: mlMetrics.F1Score,
                        auc: mlMetrics.AUC,
                        detail: "AIDoubleCheck 재학습 완료");

                    RaiseCriticalTrainingAlert(
                        projectName: "ML.NET",
                        stage: "재학습",
                        success: true,
                        detail: $"Acc={mlMetrics.Accuracy:P2}, F1={mlMetrics.F1Score:P2}, 샘플={labeledData.Count}");
                }
                catch (Exception mlEx)
                {
                    await PersistTrainingRunAsync(
                        projectName: "ML.NET",
                        runId: mlRetrainRunId,
                        stage: "Retrain",
                        success: false,
                        sampleCount: labeledData.Count,
                        epochs: 0,
                        detail: mlEx.Message);

                    RaiseCriticalTrainingAlert(
                        projectName: "ML.NET",
                        stage: "재학습",
                        success: false,
                        detail: mlEx.Message);

                    throw;
                }

                // 3. Transformer 재학습 (2 epochs)
                float tfLoss;
                string tfRetrainRunId = $"RETRAIN_TF_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                
                OnLog?.Invoke($"[AIDoubleCheck] TensorFlow 재학습 시작");

                using var tfRetrainTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                tfRetrainTimeout.CancelAfter(TimeSpan.FromMinutes(4));

                var tfMetrics = await _transformerTrainer.TrainAsync(labeledData, epochs: 2, batchSize: 32, token: tfRetrainTimeout.Token);
                tfLoss = tfMetrics.BestValidationLoss;

                OnAlert?.Invoke($"✅ [AI 재학습] Transformer 재학습 완료 (BestLoss={tfMetrics.BestValidationLoss:F4})");

                await PersistTrainingRunAsync(
                    projectName: "TensorFlow",
                    runId: tfRetrainRunId,
                    stage: "Retrain",
                    success: true,
                    sampleCount: labeledData.Count,
                    epochs: 2,
                    bestValidationLoss: tfMetrics.BestValidationLoss,
                    finalTrainLoss: tfMetrics.FinalTrainLoss,
                    detail: "TensorFlow 재학습 완료");

                RaiseCriticalTrainingAlert(
                    projectName: "TensorFlow",
                    stage: "재학습",
                    success: true,
                    detail: $"BestLoss={tfMetrics.BestValidationLoss:F4}, 샘플={labeledData.Count}");

                string msg = $"✅ [AI 재학습] 완료 - ML: {mlMetrics.Accuracy:P1}, TF Loss: {tfLoss:F3} (샘플: {labeledData.Count}개)";
                OnAlert?.Invoke(msg);
                OnLog?.Invoke(msg);
                return (true, msg);
            }
            catch (Exception ex)
            {
                /* TensorFlow 전환 중 비활성화
                _torchServiceClient?.Dispose();
                */
                string errorMsg = $"❌ [AI 재학습] 실패: {ex.Message}";
                OnAlert?.Invoke(errorMsg);
                OnLog?.Invoke($"[AIDoubleCheck] {errorMsg}");
                return (false, errorMsg);
            }
        }

        private async Task PersistTrainingRunAsync(
            string projectName,
            string runId,
            string stage,
            bool success,
            int sampleCount,
            int epochs,
            double? accuracy = null,
            double? f1Score = null,
            double? auc = null,
            float? bestValidationLoss = null,
            float? finalTrainLoss = null,
            string? detail = null)
        {
            if (_dbManager == null)
                return;

            OnLog?.Invoke($"ℹ️ [AI][DB] 학습 이력 저장 시작: {projectName}/{stage}");

            var saveTask = _dbManager.UpsertAiTrainingRunAsync(
                projectName: projectName,
                runId: runId,
                stage: stage,
                success: success,
                sampleCount: sampleCount,
                epochs: epochs,
                accuracy: accuracy,
                f1Score: f1Score,
                auc: auc,
                bestValidationLoss: bestValidationLoss,
                finalTrainLoss: finalTrainLoss,
                detail: detail);

            var completed = await Task.WhenAny(saveTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != saveTask)
            {
                _ = saveTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        OnLog?.Invoke($"⚠️ [AI][DB] 백그라운드 저장 예외: {t.Exception?.GetBaseException().Message}");
                    }
                }, TaskScheduler.Default);

                OnLog?.Invoke($"⚠️ [AI][DB] 학습 이력 저장 타임아웃(5s): {projectName}/{stage} — 학습 흐름은 계속 진행합니다.");
                return;
            }

            bool saved = await saveTask;

            if (!saved)
            {
                OnLog?.Invoke($"⚠️ [AI][DB] 학습 이력 저장 실패: {projectName}/{stage}");
                return;
            }

            OnLog?.Invoke($"✅ [AI][DB] 학습 이력 저장 완료: {projectName}/{stage}");
        }

        private static string NormalizeCriticalProjectName(string projectName)
        {
            if (string.Equals(projectName, "TorchService", StringComparison.OrdinalIgnoreCase)
                || string.Equals(projectName, "TransformerLocal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(projectName, "Transformer", StringComparison.OrdinalIgnoreCase))
            {
                return "Transformer";
            }

            return projectName;
        }

        /// <summary>
        /// 학습 실행 중 주기적으로 진행상황을 footer 알람으로 표시.
        /// trainFunc 완료 또는 취소 시 자동 중단.
        /// </summary>
        private async Task<TransformerTrainingMetrics?> RunTrainWithProgressAlertsAsync(
            Func<Task<TransformerTrainingMetrics?>> trainFunc,
            string label,
            int intervalSeconds = 20)
        {
            var startTime = DateTime.Now;
            using var progressCts = new CancellationTokenSource();

            var progressTask = Task.Run(async () =>
            {
                try
                {
                    while (!progressCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), progressCts.Token);
                        if (!progressCts.Token.IsCancellationRequested)
                        {
                            int elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                            OnLog?.Invoke($"⏳ [AI 학습] {label} 진행중... ({elapsed}초 경과, 최대 4분)");
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });

            try
            {
                return await trainFunc();
            }
            finally
            {
                progressCts.Cancel();
                try { await progressTask; } catch { }
            }
        }

        private void RaiseCriticalTrainingAlert(string projectName, string stage, bool success, string detail)
        {
            projectName = NormalizeCriticalProjectName(projectName);
            string status = success ? "완료" : "실패";
            string message = $"🚨 [CRITICAL][AI][{projectName}] {stage} {status} | {detail}";
            
            // 이벤트 경로로 전달 (MainViewModel에서 AddAlert 처리)
            OnAlert?.Invoke(message);
        }

        private void HandleTorchServiceLog(string msg)
        {
            string uiMessage = $"[TorchService] {msg}";

            if (ShouldSuppressTorchServiceUiLog(msg))
            {
                LoggerService.Info(uiMessage);
                return;
            }

            OnLog?.Invoke(uiMessage);
        }

        private static bool ShouldSuppressTorchServiceUiLog(string? msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return true;

            // 에러/타임아웃/실패는 UI에 그대로 노출
            if (msg.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("❌", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("실패", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("오류", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return msg.Contains("[NamedPipeClient]", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("[NamedPipeServer]", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Starting process", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Process started", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Running startup health check", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Service is healthy and ready", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Background health monitoring disabled", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Process already running - skip startup health probe", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("ModelLoaded=", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Terminating stale process", StringComparison.OrdinalIgnoreCase);
        }

        private List<MultiTimeframeEntryFeature> LoadLabeledDataFromFiles()
        {
            var result = new List<MultiTimeframeEntryFeature>();
            int skippedContaminated = 0;

            try
            {
                if (!Directory.Exists(_dataCollectionPath))
                    return result;

                var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);
                        if (records != null)
                        {
                            foreach (var record in records.Where(r => r.Labeled && r.Feature != null))
                            {
                                if (IsRecordCausalitySafe(record, out _))
                                {
                                    result.Add(record.Feature!);
                                }
                                else
                                {
                                    skippedContaminated++;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 개별 파일 오류 무시
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 라벨링 데이터 로드 실패: {ex.Message}");
            }

            if (skippedContaminated > 0)
            {
                OnLog?.Invoke($"🛡️ [LookAhead Shield] 오염 의심 라벨 {skippedContaminated}건을 학습에서 제외했습니다.");
            }

            return result;
        }

        public async Task<(int filesScanned, int totalRecords, int wouldRemoveRecords)> PreviewSanitizeEntryDecisionFilesAsync(CancellationToken token = default)
        {
            var stats = await SanitizeEntryDecisionFilesAsync(token, previewOnly: true);
            OnLog?.Invoke($"🔎 [LookAhead Shield][Preview] 파일 {stats.filesScanned}개, 제거예정 {stats.removedRecords}/{stats.totalRecords}건");
            return (stats.filesScanned, stats.totalRecords, stats.removedRecords);
        }

        private async Task<(int filesScanned, int totalRecords, int removedRecords)> SanitizeEntryDecisionFilesAsync(CancellationToken token = default, bool previewOnly = false)
        {
            int filesScanned = 0;
            int totalRecords = 0;
            int removedRecords = 0;

            try
            {
                if (!Directory.Exists(_dataCollectionPath))
                    return (0, 0, 0);

                FlushCollectedData(); // 큐 적재분 반영 후 검사

                var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json")
                    .OrderBy(f => f)
                    .ToList();

                if (files.Count == 0)
                    return (0, 0, 0);

                string backupDir = Path.Combine(_dataCollectionPath, "SanitizedBackup");

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    filesScanned++;

                    List<EntryDecisionRecord>? records;
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, token);
                        records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);
                    }
                    catch
                    {
                        continue;
                    }

                    if (records == null || records.Count == 0)
                        continue;

                    totalRecords += records.Count;

                    var valid = new List<EntryDecisionRecord>(records.Count);
                    int removedInFile = 0;

                    foreach (var record in records)
                    {
                        if (IsRecordCausalitySafe(record, out _))
                        {
                            valid.Add(record);
                        }
                        else
                        {
                            removedInFile++;
                        }
                    }

                    if (removedInFile <= 0)
                        continue;

                    removedRecords += removedInFile;

                    if (previewOnly)
                        continue;

                    Directory.CreateDirectory(backupDir);
                    string backupName = $"{Path.GetFileNameWithoutExtension(file)}_pre_sanitize_{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
                    string backupPath = Path.Combine(backupDir, backupName);
                    File.Copy(file, backupPath, overwrite: true);

                    string updatedJson = JsonSerializer.Serialize(valid, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    await File.WriteAllTextAsync(file, updatedJson, token);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [LookAhead Shield] 정화 중 오류: {ex.Message}");
            }

            return (filesScanned, totalRecords, removedRecords);
        }

        private bool IsRecordCausalitySafe(EntryDecisionRecord record, out string reason)
        {
            if (record == null)
            {
                reason = "record_null";
                return false;
            }

            if (record.Feature == null)
            {
                reason = "feature_null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(record.Symbol))
            {
                reason = "symbol_empty";
                return false;
            }

            if (record.EntryPrice <= 0m || record.Feature.EntryPrice <= 0m)
            {
                reason = "entry_price_invalid";
                return false;
            }

            if (!float.IsFinite(record.ML_Confidence) || !float.IsFinite(record.TF_Confidence))
            {
                reason = "confidence_not_finite";
                return false;
            }

            // 피처-기록 심볼/가격 불일치 검사
            if (!string.IsNullOrWhiteSpace(record.Feature.Symbol) &&
                !string.Equals(record.Feature.Symbol, record.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                reason = "feature_symbol_mismatch";
                return false;
            }

            decimal priceDiffPct = Math.Abs(record.Feature.EntryPrice - record.EntryPrice) / Math.Max(record.EntryPrice, 0.00000001m);
            if (priceDiffPct > 0.03m) // 3% 이상 차이면 시점 불일치 의심
            {
                reason = "feature_entry_price_mismatch";
                return false;
            }

            DateTime recordTs = ToUtcSafe(record.Timestamp);
            DateTime featureTs = ToUtcSafe(record.Feature.Timestamp);

            // 핵심: feature timestamp가 record timestamp보다 미래면 look-ahead 오염
            if (featureTs > recordTs.AddMinutes(1))
            {
                reason = "feature_timestamp_in_future";
                return false;
            }

            if (recordTs > DateTime.UtcNow.AddMinutes(5))
            {
                reason = "record_timestamp_future";
                return false;
            }

            if (record.Labeled)
            {
                if (!record.ActualProfitPct.HasValue || !float.IsFinite(record.ActualProfitPct.Value))
                {
                    reason = "labeled_without_valid_profit";
                    return false;
                }

                if (record.LabeledAt.HasValue && ToUtcSafe(record.LabeledAt.Value) < recordTs)
                {
                    reason = "labeled_before_entry";
                    return false;
                }
            }

            if (!float.IsFinite(record.Feature.Fib_DistanceTo0382_Pct)
                || !float.IsFinite(record.Feature.Fib_DistanceTo0618_Pct)
                || !float.IsFinite(record.Feature.Fib_DistanceTo0786_Pct))
            {
                reason = "fib_feature_not_finite";
                return false;
            }

            if (record.Feature.CandlesToTarget < -1f || record.Feature.CandlesToTarget > 256f)
            {
                reason = "candles_to_target_out_of_range";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static DateTime ToUtcSafe(DateTime dt)
        {
            return dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            };
        }
    }
    /// 더블체크 설정
    /// </summary>
    public class DoubleCheckConfig
    {
        public readonly record struct SymbolThreshold(double EntryScoreCut, float MaxRsiLimit, float AiConfidenceMin);

        public float MinMLConfidence { get; set; } = 0.56f;
        public float MinTransformerConfidence { get; set; } = 0.52f;
        public float MinMLConfidenceMajor { get; set; } = 0.75f; // 메이저 코인은 더 보수적
        public float MinTransformerConfidenceMajor { get; set; } = 0.68f;
        public float MinMLConfidencePumping { get; set; } = 0.56f; // 펌핑 손실 구간 대응: 보수 강화
        public float MinTransformerConfidencePumping { get; set; } = 0.54f;

        public float StrongTrendBypassThreshold { get; set; } = 0.80f;
        public float ElliottRule3Penalty { get; set; } = 0.15f;
        public float RuleFilterFinalScoreThreshold { get; set; } = 0.65f;
        public float SuperTrendTfThreshold { get; set; } = 0.84f;
        public float SuperTrendMlThreshold { get; set; } = 0.85f;
        public float LongFibExtremeLevel { get; set; } = 0.786f;
        public float LongFibBlockRsi { get; set; } = 25f;
        public float LongFibBlockBbPosition { get; set; } = 0.05f;
        public float ShortFibExtremeLevel { get; set; } = 0.236f;
        public float ShortFibBlockRsi { get; set; } = 75f;
        public float ShortFibBlockBbPosition { get; set; } = 0.95f;
        public float RsiOverheatHardCap { get; set; } = 80f;
        public float RsiCautionThreshold { get; set; } = 70f;

        /// <summary>[Staircase Pursuit] 계단식 상승 감지 시 Chasing 필터 바이패스에 필요한 최소 TF 점수 (0~1)</summary>
        public float StaircaseTfMinThreshold { get; set; } = 0.65f;
        public float StaircaseHigherLowsCount { get; set; } = 3f;
        public float BbUpperRiskThreshold { get; set; } = 0.90f;
        public float UpperWickRiskThreshold { get; set; } = 0.70f;
        public float RecentHighChaseThresholdPct { get; set; } = 0.20f;
        public float LowVolumeRejectRatio { get; set; } = 0.70f;
        public float LowVolumeBypassMinRatio { get; set; } = 0.30f;
        public float LowVolumeBypassTfThreshold { get; set; } = 0.90f;
        public float LowVolumeBypassBbLower { get; set; } = 0.40f;
        public float LowVolumeBypassBbUpper { get; set; } = 0.60f;
        public float LowVolumeBypassLowerWickBodyRatio { get; set; } = 1.20f;

        public int FibonacciWaveLookbackCandles { get; set; } = 32;
        public float FibonacciSupportUpper { get; set; } = 0.618f;
        public float FibonacciSupportLower { get; set; } = 0.786f;
        public double FibonacciSupportBonusScore { get; set; } = 20.0;
        public float ReversalBodyRatioThreshold { get; set; } = 0.35f;
        public float DeadCatBodyBreakRatio { get; set; } = 0.55f;

        public int EntryForecastSteps { get; set; } = 8; // 다음 2시간(15분 x 8)
        public int EntryForecastWatchSteps { get; set; } = 16; // 관망 시 4시간(15분 x 16)
        public float EntryForecastImmediateThreshold { get; set; } = 0.62f;
        public float EntryForecastImmediateTolerance { get; set; } = 0.03f;
        public float EntryForecastWatchThreshold { get; set; } = 0.35f;
        public float EntryForecastMinCandidateProbability { get; set; } = 0.35f;
        public float EntryForecastTimePenaltyPerStep { get; set; } = 0.01f;

        public SymbolThreshold GetThresholdBySymbol(string symbol)
        {
            bool isMajor = IsMajorCoin(symbol); // BTC, ETH, SOL, XRP

            if (isMajor)
            {
                // [메이저 전용 완화 세팅]
                return new SymbolThreshold(
                    EntryScoreCut: 70.0,
                    MaxRsiLimit: 75f,
                    AiConfidenceMin: 0.7f);
            }

            // [밈코인용 2차 완화 세팅]
            return new SymbolThreshold(
                EntryScoreCut: 72.0,
                MaxRsiLimit: 70f,
                AiConfidenceMin: 0.72f);
        }

        private static bool IsMajorCoin(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            return symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("SOL", StringComparison.OrdinalIgnoreCase)
                || symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// AI 진입 평가 상세
    /// </summary>
    public class AIEntryDetail
    {
        public string DecisionId { get; set; } = string.Empty;
        public bool ML_Approve { get; set; }
        public float ML_Confidence { get; set; }
        public bool TF_Approve { get; set; }
        public float TF_Confidence { get; set; }
        public float TrendScore { get; set; }
        public bool DoubleCheckPassed { get; set; }

        // 진입 시점 M15 핵심 지표 (텔레그램·DB 로그용)
        public float M15_RSI { get; set; }
        public float M15_BBPosition { get; set; }

        // 엘리엇 파동 & 피보나치 규칙 검증 결과
        public bool ElliottValid { get; set; }
        public bool FibInEntryZone { get; set; }

        // 메이저 코인 Fib 전술 신호
        public float FibonacciBonusScore { get; set; }
        public double Fib618 { get; set; }
        public double Fib786 { get; set; }
        public bool FibReversalConfirmed { get; set; }
        public bool FibDeadCatBlocked { get; set; }
    }

    /// <summary>
    /// 진입 결정 기록 (학습 데이터)
    /// </summary>
    public class EntryDecisionRecord
    {
        public string DecisionId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public bool ML_Approve { get; set; }
        public float ML_Confidence { get; set; }
        public bool TF_Approve { get; set; }
        public float TF_Confidence { get; set; }
        public bool FinalDecision { get; set; }
        public MultiTimeframeEntryFeature? Feature { get; set; }
        public float? ActualProfitPct { get; set; } // 15분 후 업데이트
        public bool Labeled { get; set; }
        public string? LabelSource { get; set; }
        public DateTime? LabeledAt { get; set; }
    }

    public class AiLabeledSample
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime EntryTimeUtc { get; set; }
        public decimal EntryPrice { get; set; }
        public float ActualProfitPct { get; set; }
        public bool IsSuccess { get; set; }
        public string LabelSource { get; set; } = "unknown";
        public MultiTimeframeEntryFeature Feature { get; set; } = new MultiTimeframeEntryFeature();
    }

    public enum CoinType
    {
        Major,      // BTC, ETH, SOL, XRP
        Pumping,    // 급등주 (STEEM, MANTRA 등)
        Normal      // 일반 알트코인
    }
    
    /// <summary>
    /// Transformer 학습 평가 지표 (TensorFlow 전환 중 더미 타입)
    /// </summary>
    public record TransformerTrainingMetrics(float BestValidationLoss, float FinalTrainLoss, int TrainedEpochs);
}

