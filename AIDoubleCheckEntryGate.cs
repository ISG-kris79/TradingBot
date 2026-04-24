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
    /// ML.NET 단독 게이트 + 실시간 데이터 수집 (지속적 학습용)
    /// + 온라인 학습 (적응형 학습 및 Concept Drift 감지)
    /// [v3.0.7] TensorFlow.NET 제거, ML.NET 통합
    /// </summary>
    public class AIDoubleCheckEntryGate
    {
        private readonly IExchangeService _exchangeService;
        private readonly EntryTimingMLTrainer _mlTrainer;                   // [Default/기본] — Fallback
        private readonly EntryTimingMLTrainer _mlTrainerMajor;              // [v5.10.76 Phase 3] Major 전용
        private readonly EntryTimingMLTrainer _mlTrainerPump;               // [v5.10.76 Phase 3] Pump 전용
        private readonly EntryTimingMLTrainer _mlTrainerSpike;              // [v5.10.76 Phase 3] Spike 전용
        private readonly MultiTimeframeFeatureExtractor _featureExtractor;
        // [v4.3.1] MultiTF 피처 캐시 (30초 TTL) — 5개 API 호출 제거
        private readonly ConcurrentDictionary<string, (MultiTimeframeEntryFeature feature, DateTime time)> _featureCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly BacktestEntryLabeler _labeler;
        private readonly EntryRuleValidator _ruleValidator;
        private readonly ConcurrentDictionary<string, Queue<MultiTimeframeEntryFeature>> _recentFeatureBuffers
            = new(StringComparer.OrdinalIgnoreCase);
        
        // 실시간 데이터 수집
        private readonly ConcurrentQueue<EntryDecisionRecord> _pendingRecords = new();
        private readonly string _dataCollectionPath = "TrainingData/EntryDecisions";
        private readonly Timer? _dataFlushTimer;
        
        // [v5.16.0 CRITICAL FIX] 온라인 학습 서비스 — 4개 variant 별 독립 인스턴스
        //   기존 (v5.15.0까지): 1개 인스턴스(_mlTrainer=Default)만 존재 → Major/Pump/Spike 모델은 봇 시작 시 1회 로드 후 영원히 동결
        //   근본 원인: AddLabeledSampleAsync()가 Default trainer 에만 라벨 전달, 3-모델 분리는 인프라만 있고 학습은 가짜
        //   수정: variant 별 독립 sliding window + 독립 retrain timer + 심볼 기반 라우팅
        private readonly AdaptiveOnlineLearningService? _onlineLearning;         // Default (fallback)
        private readonly AdaptiveOnlineLearningService? _onlineLearningMajor;    // BTC/ETH/SOL/XRP
        private readonly AdaptiveOnlineLearningService? _onlineLearningPump;     // 일반 알트
        private readonly AdaptiveOnlineLearningService? _onlineLearningSpike;    // SPIKE_FAST/TICK_SURGE
        private readonly DbManager? _dbManager;

        // [Lorentzian Phase 1] KNN 사이드카 게이트 — 진입 신호 추가 검증 (soft mode)
        private readonly Services.LorentzianClassifier _lorentzian;
        private readonly string _lorentzianDataPath = "TrainingData/Lorentzian";
        
        // 설정
        private readonly DoubleCheckConfig _config;
        
        // 로깅 이벤트
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;
        public event Action<AiLabeledSample>? OnLabeledSample;
        
        // [v3.0.7] ML.NET 모델 로드 시 게이트 활성화 (Default 또는 variant 중 하나만 로드돼도 Ready)
        public bool IsReady => _mlTrainer.IsModelLoaded || _mlTrainerMajor.IsModelLoaded
                            || _mlTrainerPump.IsModelLoaded || _mlTrainerSpike.IsModelLoaded;

        /// <summary>
        /// [v5.10.76 Phase 3] 심볼 + signalSource로 적절한 ML trainer 선택
        /// 우선순위: 해당 variant 모델 로드됨 → 그 모델 사용, 아니면 Default fallback
        /// </summary>
        private EntryTimingMLTrainer SelectTrainerForSymbol(string symbol, string? signalSource)
        {
            bool isMajor = IsMajorSymbol(symbol);
            bool isSpike = !string.IsNullOrEmpty(signalSource)
                && (signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Equals("TICK_SURGE", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Equals("M1_FAST_PUMP", StringComparison.OrdinalIgnoreCase));

            EntryTimingMLTrainer selected;
            if (isMajor && _mlTrainerMajor.IsModelLoaded) selected = _mlTrainerMajor;
            else if (isSpike && _mlTrainerSpike.IsModelLoaded) selected = _mlTrainerSpike;
            else if (!isMajor && _mlTrainerPump.IsModelLoaded) selected = _mlTrainerPump;
            else if (_mlTrainer.IsModelLoaded) selected = _mlTrainer;
            else
            {
                // 모든 variant 미로드 → Default (빈 모델) 반환. IsReady=false로 gate 차단됨
                selected = _mlTrainer;
            }
            return selected;
        }

        /// <summary>
        /// [v5.16.0 CRITICAL] 심볼/signalSource 로 해당 variant 의 online learning 인스턴스 반환
        /// 라벨된 샘플을 올바른 variant 의 sliding window 에 라우팅하기 위해 사용
        /// </summary>
        private AdaptiveOnlineLearningService? SelectLearningServiceForSymbol(string symbol, string? signalSource)
        {
            bool isMajor = IsMajorSymbol(symbol);
            bool isSpike = !string.IsNullOrEmpty(signalSource)
                && (signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Equals("TICK_SURGE", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Equals("M1_FAST_PUMP", StringComparison.OrdinalIgnoreCase));

            if (isMajor) return _onlineLearningMajor ?? _onlineLearning;
            if (isSpike) return _onlineLearningSpike ?? _onlineLearning;
            return _onlineLearningPump ?? _onlineLearning;
        }

        /// <summary>
        /// [v5.16.0] variant 태그 문자열 (로그/DB 기록용)
        /// </summary>
        private string GetVariantTagForSymbol(string symbol, string? signalSource)
        {
            bool isMajor = IsMajorSymbol(symbol);
            bool isSpike = !string.IsNullOrEmpty(signalSource)
                && (signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Equals("TICK_SURGE", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Equals("M1_FAST_PUMP", StringComparison.OrdinalIgnoreCase));
            if (isMajor) return "Major";
            if (isSpike) return "Spike";
            return "Pump";
        }

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
            
            // [v5.10.76 Phase 3] 3 모델 분리 — 메이저/PUMP/SPIKE 각각 별도 학습/추론
            _mlTrainer       = new EntryTimingMLTrainer(EntryTimingMLTrainer.ModelVariant.Default);
            _mlTrainerMajor  = new EntryTimingMLTrainer(EntryTimingMLTrainer.ModelVariant.Major);
            _mlTrainerPump   = new EntryTimingMLTrainer(EntryTimingMLTrainer.ModelVariant.Pump);
            _mlTrainerSpike  = new EntryTimingMLTrainer(EntryTimingMLTrainer.ModelVariant.Spike);
            _featureExtractor = new MultiTimeframeFeatureExtractor(exchangeService);
            // [v5.10.94] Feature 추출 실패 로그를 AIDoubleCheckEntryGate.OnLog로 전달 → MainWindow FooterLogs 표시
            _featureExtractor.OnLog += msg => OnLog?.Invoke(msg);
            _labeler = new BacktestEntryLabeler();
            _ruleValidator = new EntryRuleValidator(_config);

            // 각 모델 로드 (없으면 fallback으로 Default 사용)
            _mlTrainer.LoadModel();
            _mlTrainerMajor.LoadModel();
            _mlTrainerPump.LoadModel();
            _mlTrainerSpike.LoadModel();

            // [v3.0.7] ML.NET 단독 모드 (TensorFlow.NET 제거됨)

            // 데이터 수집 폴더 생성
            Directory.CreateDirectory(_dataCollectionPath);
            Directory.CreateDirectory(_lorentzianDataPath);

            // [Lorentzian Phase 1] KNN 분류기 초기화 + 과거 샘플 로드 (백그라운드)
            _lorentzian = new Services.LorentzianClassifier(
                k: _config.LorentzianK,
                maxSamples: _config.LorentzianMaxSamples,
                minSamplesForPrediction: _config.LorentzianMinSamples);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _lorentzian.LoadSamplesFromFolderAsync(_lorentzianDataPath);
                    OnLog?.Invoke($"[Lorentzian] 초기 샘플 로드 완료: {_lorentzian.SampleCount}건 (K={_lorentzian.K}, ready={_lorentzian.IsReady})");
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [Lorentzian] 샘플 로드 실패: {ex.Message}");
                }
            });

            // [v5.16.0] 온라인 학습 서비스 초기화 — 4개 variant 별 독립 인스턴스
            if (enableOnlineLearning)
            {
                // [v5.17.0 SMART LEARNING ②] 재학습 가속 — 트리거 5건마다 + 주기 15분
                //   기존(v5.16.0): 200/100/1h → 2달간 학습 0회
                //   v5.17.0: 50/5/15min → 능동 학습 + 시장 급변 대응
                //   콜드 스타트는 BootstrapFromTradeHistoryAsync 가 별도 처리 (DB 기반 즉시 학습)
                OnlineLearningConfig MakeConfig() => new OnlineLearningConfig
                {
                    SlidingWindowSize = 1000,
                    MinSamplesForTraining = 50,        // 200 → 50 (현실적 임계)
                    TriggerEveryNSamples = 5,          // 100 → 5 (능동 업데이트)
                    RetrainingIntervalHours = 0.25,    // 1.0 → 0.25 (15분)
                    EnablePeriodicRetraining = true,
                    EnableConceptDriftDetection = true,
                    TransformerFastEpochs = 5
                };

                _onlineLearning      = new AdaptiveOnlineLearningService(_mlTrainer,      MakeConfig());
                _onlineLearningMajor = new AdaptiveOnlineLearningService(_mlTrainerMajor, MakeConfig());
                _onlineLearningPump  = new AdaptiveOnlineLearningService(_mlTrainerPump,  MakeConfig());
                _onlineLearningSpike = new AdaptiveOnlineLearningService(_mlTrainerSpike, MakeConfig());

                // 공통 로그/이벤트 핸들러 연결
                void AttachHandlers(AdaptiveOnlineLearningService svc, string variantTag)
                {
                    svc.OnLog += msg => OnLog?.Invoke($"[{variantTag}] {msg}");
                    svc.OnPerformanceUpdate += (reason, acc, mlThresh, tfThresh) =>
                    {
                        OnAlert?.Invoke($"🧠 온라인 학습[{variantTag}]: {reason} | 정확도={acc:P1}, ML={mlThresh:P0}");
                    };
                    svc.OnRetrainCompleted += (reason, sampleCount, accuracy, f1, success) =>
                    {
                        string status = success ? "✅" : "❌";
                        OnLog?.Invoke($"🧠 [ONLINE_ML][{variantTag}][{status}] reason={reason} samples={sampleCount} acc={accuracy:P1} f1={f1:P1}");
                        if (_dbManager == null) return;
                        string runId = $"ONLINE_ML_{variantTag}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                        _ = _dbManager.UpsertAiTrainingRunAsync(
                            projectName: $"ML.NET_{variantTag}",
                            runId: runId,
                            stage: $"Online_Retrain_{variantTag}",
                            success: success,
                            sampleCount: sampleCount,
                            epochs: 0,
                            accuracy: accuracy,
                            f1Score: f1,
                            auc: 0,
                            bestValidationLoss: 0f,
                            finalTrainLoss: 0f,
                            detail: $"variant={variantTag} reason={reason}");
                    };
                }
                AttachHandlers(_onlineLearning,      "Default");
                AttachHandlers(_onlineLearningMajor, "Major");
                AttachHandlers(_onlineLearningPump,  "Pump");
                AttachHandlers(_onlineLearningSpike, "Spike");

                // 각 variant 별 초기 윈도우 로드 (비동기 실행) — variant 별 서브디렉터리 사용
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string basePath = _dataCollectionPath;
                        await _onlineLearning.LoadInitialWindowAsync(Path.Combine(basePath, "Default"));
                        await _onlineLearningMajor.LoadInitialWindowAsync(Path.Combine(basePath, "Major"));
                        await _onlineLearningPump.LoadInitialWindowAsync(Path.Combine(basePath, "Pump"));
                        await _onlineLearningSpike.LoadInitialWindowAsync(Path.Combine(basePath, "Spike"));
                        OnLog?.Invoke($"[OnlineLearning] 4-variant 초기화 완료: Default={_onlineLearning.WindowSize}, Major={_onlineLearningMajor.WindowSize}, Pump={_onlineLearningPump.WindowSize}, Spike={_onlineLearningSpike.WindowSize}");

                        // [v5.17.0 SMART LEARNING ①] Initial Batch Learning — DB TradeHistory 일괄 학습
                        //   목적: 콜드 스타트 1-2주 → 30분 단축. 봇 시작시 즉시 4 zip 모델 생성
                        await BootstrapFromTradeHistoryAsync(daysBack: 30);
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [OnlineLearning] 초기화 실패: {ex.Message}");
                    }
                });
            }

            // 데이터 자동 저장 타이머 (5분마다)
            // [v5.10.72 Phase 1-A] 5분 → 1분 단축 (빠른 외부 청산 케이스에도 디스크 반영 보장 + _pendingRecords 큐 선행 검색과 이중 안전장치)
            _dataFlushTimer = new Timer(_ => FlushCollectedData(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

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
        /// [v5.11.0] AI 진입 게이트 — 검증(Validation) → 예측(Prediction) → 결정(Decision) 3단계 파이프라인
        ///
        /// 설계 원칙:
        ///  1) 검증 통과해야만 예측 수행 (리소스 절약 + 무효 예측 차단)
        ///  2) 예측 결과와 독립된 raw feature 값으로 추세 교차검증 (동일 소스 중복 평가 금지)
        ///  3) 바이패스/소프트패스 전면 제거 (하드코딩 예외 조항 금지)
        ///
        /// 폐기된 레거시:
        ///  - Fibonacci bonus score 가산 (주관적 바이어스)
        ///  - Elliott Rule TF>=80% 바이패스 (객관적 규칙 회피)
        ///  - Sanity_Filter_UpperWick/RSI 하드코딩 (AI 학습으로 대체)
        ///  - Lorentzian soft warn (샘플 0/100 상태로 무의미)
        ///  - KST9 시간대 threshold 완화 (시간대 바이어스)
        ///  - ML=0 경고 후 진입 계속 (ML 무력화)
        /// </summary>
        public async Task<(bool allowEntry, string reason, AIEntryDetail detail)> EvaluateEntryAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CancellationToken token = default)
        {
            var detail = new AIEntryDetail();

            try
            {
                // ═══════════════════════════════════════════════════════════════
                // PHASE 1: 검증 (Validation) — 예측 전 자격심사
                // 목적: 예측을 돌려도 의미 없는 상태를 선제 차단
                // ═══════════════════════════════════════════════════════════════

                if (!IsReady)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [VALIDATE] AI 모델 미준비");
                    return (false, "VALIDATE_Models_Not_Ready", detail);
                }

                // Major 전용 모델 미학습 시 메이저 차단 (학습 데이터 격리 유지)
                if (_config.BlockMajorEntries && IsMajorSymbol(symbol) && !_mlTrainerMajor.IsModelLoaded)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [VALIDATE] Major 모델 미학습 → 메이저 진입 차단");
                    return (false, "VALIDATE_Major_Model_Not_Loaded", detail);
                }

                // Feature 추출 (30초 캐시)
                MultiTimeframeEntryFeature? feature = null;
                if (_featureCache.TryGetValue(symbol, out var cached) && (DateTime.Now - cached.time).TotalSeconds < 30)
                {
                    feature = cached.feature;
                }
                else
                {
                    feature = await _featureExtractor.ExtractRealtimeFeatureAsync(symbol, DateTime.UtcNow, token);
                    if (feature != null)
                        _featureCache[symbol] = (feature, DateTime.Now);
                }

                if (feature == null)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [VALIDATE] Feature 추출 실패 (데이터 부족)");
                    return (false, "VALIDATE_Feature_Extraction_Failed", detail);
                }

                // M1 데이터 silent fallback 차단 — 1분봉 수집 실패 시 진입 금지
                if (feature.M1_Data_Valid < 0.5f)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [VALIDATE] M1 데이터 무효 (silent fallback 감지)");
                    return (false, "VALIDATE_M1_Data_Invalid", detail);
                }

                // 해당 coinType 전용 모델 로드 여부 확인
                var selectedTrainer = SelectTrainerForSymbol(symbol, null);
                if (!selectedTrainer.IsModelLoaded)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [VALIDATE] {selectedTrainer.Variant} 모델 미로드");
                    return (false, $"VALIDATE_Model_Not_Loaded_{selectedTrainer.Variant}", detail);
                }

                // ═══════════════════════════════════════════════════════════════
                // PHASE 2: 예측 (Prediction) — 검증 통과 시에만 실행
                // 목적: 선택된 모델로 진입 확률 산출 (단일 소스, 사본/복제 금지)
                // ═══════════════════════════════════════════════════════════════

                var mlPrediction = selectedTrainer.Predict(feature);
                if (mlPrediction == null)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [PREDICT] 예측 결과 null");
                    return (false, "PREDICT_Null", detail);
                }

                float mlConfidence = mlPrediction.Probability;
                bool mlApprove = mlPrediction.ShouldEnter;

                detail.ML_Approve = mlApprove;
                detail.ML_Confidence = mlConfidence;
                // UI 바인딩 호환을 위한 TF 별칭 (값 동일하지만 Decision 단계에서는 별개 소스로 재평가됨)
                detail.TF_Approve = mlApprove;
                detail.TF_Confidence = mlConfidence;
                detail.TrendScore = mlConfidence;
                detail.M15_RSI = feature.M15_RSI;
                detail.M15_BBPosition = feature.M15_BBPosition;

                // 데이터 수집 (실시간 학습용)
                string decisionId = RecordEntryDecision(feature, mlPrediction, mlApprove, mlConfidence, false);
                detail.DecisionId = decisionId;

                // ═══════════════════════════════════════════════════════════════
                // PHASE 3: 결정 (Decision) — 독립 3소스 교차검증
                // 목적: ML 확률 + ML 방향 + raw feature 추세 — 세 소스 모두 일치해야 진입
                // ═══════════════════════════════════════════════════════════════

                bool isLong = decision.Contains("LONG", StringComparison.OrdinalIgnoreCase)
                           || decision.Equals("BUY", StringComparison.OrdinalIgnoreCase);

                // [v5.16.0] variant 별 threshold 조회 — variant service 가 승률 기반 auto-calibrate 한 값 사용
                var variantLearningService = SelectLearningServiceForSymbol(symbol, null);
                string activeVariantTag = GetVariantTagForSymbol(symbol, null);
                float effectiveThreshold = variantLearningService?.CurrentMLThreshold
                                         ?? _onlineLearning?.CurrentMLThreshold
                                         ?? _config.MinMLConfidence;

                if (mlConfidence < effectiveThreshold)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [DECIDE][{activeVariantTag}][prob] ML={mlConfidence:P1} < threshold={effectiveThreshold:P0}");
                    return (false, $"DECIDE_Prob_Low_{activeVariantTag}_ML={mlConfidence:P1}_TH={effectiveThreshold:P0}", detail);
                }

                // [D-2] ML 방향(ShouldEnter) 승인 검증
                if (!mlApprove)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [DECIDE][approve] ML ShouldEnter=false (prob={mlConfidence:P1})");
                    return (false, $"DECIDE_ML_Not_Approve_Prob={mlConfidence:P1}", detail);
                }

                // [D-3] Raw Feature 추세 교차검증 (ML 출력 아닌 원본 피처값)
                //  LONG: 15m 하락추세 / H1 하락추세 / SuperTrend 하락 / D1+H4 강한 약세 → 거부
                //  SHORT: 15m 상승 + SuperTrend 상승 / D1+H4 강한 강세 → 거부
                if (isLong)
                {
                    if (feature.M15_IsDowntrend >= 0.5f)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] LONG 거부: M15 하락추세 (SMA20<SMA60)");
                        return (false, "DECIDE_M15_Downtrend_Long_Block", detail);
                    }
                    // H1 하락추세이면서 아직 돌파 못했으면 거부
                    if (feature.H1_IsDowntrend >= 0.5f && feature.H1_BreakoutFromDowntrend < 0.5f)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] LONG 거부: H1 하락추세 지속 (돌파 미검출)");
                        return (false, "DECIDE_H1_Downtrend_No_Breakout", detail);
                    }
                    if (feature.M15_SuperTrend_Direction < 0)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] LONG 거부: M15 SuperTrend=하락");
                        return (false, "DECIDE_M15_SuperTrend_Down", detail);
                    }
                    // 3 연속 음봉 이상 + M15_RSI<45 = 약세 지속 → LONG 차단
                    if (feature.M15_ConsecBearishCount >= 3 && feature.M15_RSI_BelowNeutral >= 0.5f)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] LONG 거부: 연속음봉 {feature.M15_ConsecBearishCount}개 + RSI 약세");
                        return (false, $"DECIDE_Consec_Bearish_{feature.M15_ConsecBearishCount}", detail);
                    }
                    if (feature.DirectionBias <= -1.5f)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] LONG 거부: D1+H4 방향 bias={feature.DirectionBias:F1} 강한약세");
                        return (false, $"DECIDE_DirectionBias_Bearish_{feature.DirectionBias:F1}", detail);
                    }
                }
                else // SHORT
                {
                    if (feature.M15_IsDowntrend < 0.5f
                        && feature.M15_SuperTrend_Direction > 0
                        && feature.M15_ConsecBullishCount >= 3)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] SHORT 거부: 상승추세 + 연속양봉 {feature.M15_ConsecBullishCount}개");
                        return (false, $"DECIDE_Uptrend_Short_Block_{feature.M15_ConsecBullishCount}", detail);
                    }
                    if (feature.DirectionBias >= 1.5f)
                    {
                        OnLog?.Invoke($"❌ [{symbol}] [DECIDE][slope] SHORT 거부: D1+H4 방향 bias={feature.DirectionBias:F1} 강한강세");
                        return (false, $"DECIDE_DirectionBias_Bullish_{feature.DirectionBias:F1}", detail);
                    }
                }

                // 3 단계 모두 통과 → 진입 승인
                detail.DoubleCheckPassed = true;
                detail.ElliottValid = true;
                OnLog?.Invoke($"✅ [{symbol}] [VPD_PASS] ML={mlConfidence:P1} dir={decision} M15_IsDown={feature.M15_IsDowntrend:F0} ST={feature.M15_SuperTrend_Direction:F0} bias={feature.DirectionBias:F1}");
                return (true, $"VPD_PASS_ML={mlConfidence:P1}_Dir={decision}", detail);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [{symbol}] [GATE_EXCEPTION] {ex.Message}");
                return (false, $"Exception_{ex.Message}", detail);
            }
        }

        /// <summary>
        /// [v5.11.0] CoinType별 추가 threshold 검증 (Major/Pumping 차등)
        /// 기본 게이트 통과 후 CoinType 특화 임계값만 overlay (Fib 보너스 등 가산 제거)
        /// </summary>
        public async Task<(bool allowEntry, string reason, AIEntryDetail detail)> EvaluateEntryWithCoinTypeAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CoinType coinType,
            CancellationToken token = default)
        {
            var (allow, reason, detail) = await EvaluateEntryAsync(symbol, decision, currentPrice, token);
            if (!allow) return (allow, reason, detail);

            var symbolThreshold = _config.GetThresholdBySymbol(symbol);
            bool isLong = decision.Equals("LONG", StringComparison.OrdinalIgnoreCase)
                       || decision.Equals("BUY", StringComparison.OrdinalIgnoreCase);

            if (coinType == CoinType.Major)
            {
                // Major LONG 75%+, SHORT 65%+ (학습 데이터 편향 보정)
                float majorMin = isLong
                    ? Math.Max(_config.MinMLConfidenceMajor + 0.05f, 0.75f)
                    : Math.Max(_config.MinMLConfidenceMajor - 0.05f, 0.65f);
                majorMin = Math.Max(majorMin, symbolThreshold.AiConfidenceMin);

                if (detail.ML_Confidence < majorMin)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [DECIDE][major] ML={detail.ML_Confidence:P1} < Major 임계 {majorMin:P0}");
                    return (false, $"Major_{decision}_Below_{majorMin:P0}_ML={detail.ML_Confidence:P1}", detail);
                }
            }
            else if (coinType == CoinType.Pumping)
            {
                // Pumping 변동성 리스크 → 강화 threshold
                float pumpMin = Math.Max(
                    Math.Max(_config.MinMLConfidencePumping, _config.MinMLConfidence),
                    symbolThreshold.AiConfidenceMin);

                if (detail.ML_Confidence < pumpMin)
                {
                    OnLog?.Invoke($"❌ [{symbol}] [DECIDE][pump] ML={detail.ML_Confidence:P1} < Pump 임계 {pumpMin:P0}");
                    return (false, $"Pumping_Below_{pumpMin:P0}_ML={detail.ML_Confidence:P1}", detail);
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

                return await Task.Run(() =>
                {
                    var lastFeature = recentFeatures.Last();
                    var pred = _mlTrainer.Predict(lastFeature);
                    if (pred == null) return (-1f, 0f);
                    return (pred.ShouldEnter ? 8f : -1f, pred.Probability);
                });
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ ML 예측 오류: {ex.Message}");
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

            // [v5.10.72 Phase 1-A] _pendingRecords 큐 선행 검색 — 빠른 외부 청산(<5분)이 flush 전이라 디스크 파일에 없는 경우 매칭 실패 버그 수정
            // v5.10.66 이후 라벨 N=0 현상의 직접 원인. AAVE 등 3분 청산 케이스 커버.
            foreach (var pending in _pendingRecords)
            {
                bool pendingMatch = false;
                if (!string.IsNullOrWhiteSpace(decisionId)
                    && string.Equals(pending.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)
                    && (overwriteExisting || !pending.Labeled))
                {
                    pendingMatch = true;
                }
                else if (string.IsNullOrWhiteSpace(decisionId)
                    && string.Equals(pending.Symbol, symbolUpper, StringComparison.OrdinalIgnoreCase)
                    && (overwriteExisting || !pending.Labeled))
                {
                    // symbol+time+price fallback (1분 이내 + 가격 3% 이내)
                    DateTime recUtc = pending.Timestamp.Kind == DateTimeKind.Utc ? pending.Timestamp : DateTime.SpecifyKind(pending.Timestamp, DateTimeKind.Utc);
                    double timeDiff = Math.Abs((recUtc - entryUtc).TotalMinutes);
                    double priceDiffPct = entryPrice > 0m ? (double)(Math.Abs(pending.EntryPrice - entryPrice) / entryPrice) : 1.0;
                    if (timeDiff <= 1.0 && priceDiffPct <= 0.03)
                        pendingMatch = true;
                }

                if (pendingMatch)
                {
                    pending.ActualProfitPct = actualProfitPct;
                    pending.Labeled = true;
                    pending.LabelSource = labelSource;
                    pending.LabeledAt = DateTime.UtcNow;
                    // 큐 아이템 업데이트됨 (참조 타입). 다음 flush 주기에 디스크 반영.
                    OnLog?.Invoke($"📝 [Label][Pending] {symbol} decisionId={decisionId} pnl={actualProfitPct:F2}% → 큐 내부 레코드 업데이트 (flush 전)");
                    return true;
                }
            }

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
        /// [v5.17.0 SMART LEARNING ①] DB TradeHistory 일괄 학습 — 콜드 스타트 즉시 해결
        ///
        /// 동작:
        ///  1) DB 에서 최근 N일 trades 조회 (PnL 있는 closed trade만)
        ///  2) 각 trade 의 EntryTime 시점 feature 추출 (MultiTimeframeFeatureExtractor 활용)
        ///  3) 라벨: PnL > 0 → ShouldEnter=true
        ///  4) 심볼별 variant 인스턴스에 샘플 추가
        ///  5) 50건 이상 모인 variant → 즉시 RetrainModelsAsync 강제 호출
        ///  6) 결과: EntryTimingModel_*.zip 4개 즉시 생성 → AI Gate 즉시 활성
        ///
        /// 주의: feature 추출은 historical kline 필요 (현재 캐시 없으면 API 폴링)
        ///       ~수백 trade 처리에 1-3분 소요 예상 → 백그라운드 실행 권장
        /// </summary>
        public async Task<(int processed, int loaded, string report)> BootstrapFromTradeHistoryAsync(int daysBack = 30)
        {
            if (_dbManager == null)
                return (0, 0, "DB_NOT_AVAILABLE");

            int userId = AppConfig.CurrentUser?.Id ?? 0;
            if (userId <= 0)
                return (0, 0, "USER_NOT_LOGGED_IN");

            try
            {
                OnLog?.Invoke($"🎓 [BOOTSTRAP] DB TradeHistory 일괄 학습 시작 (최근 {daysBack}일)");

                var since = DateTime.UtcNow.AddDays(-daysBack);
                var trades = await _dbManager.GetTradeHistoryAsync(userId, since, DateTime.UtcNow, 5000);
                if (trades == null || trades.Count == 0)
                {
                    OnLog?.Invoke("ℹ️ [BOOTSTRAP] 학습 가능한 과거 trade 없음 → 스킵");
                    return (0, 0, "NO_TRADES");
                }

                int processed = 0, loaded = 0;
                int defaultAdded = 0, majorAdded = 0, pumpAdded = 0, spikeAdded = 0;

                foreach (var t in trades)
                {
                    processed++;
                    if (string.IsNullOrEmpty(t.Symbol)) continue;
                    if (t.Time == default || t.Price <= 0) continue;
                    if (t.PnL == 0m && t.PnLPercent == 0m) continue; // 미체결/0-PnL 스킵

                    try
                    {
                        // EntryTime 시점 feature 재추출
                        var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(t.Symbol, t.Time, CancellationToken.None);
                        if (feature == null) continue;

                        // 라벨: PnL > 0 = WIN
                        feature.ShouldEnter = t.PnL > 0m;
                        feature.ActualProfitPct = (float)t.PnLPercent;
                        feature.Symbol = t.Symbol;
                        feature.Timestamp = t.Time;
                        feature.EntryPrice = t.Price;

                        // variant 라우팅
                        var svc = SelectLearningServiceForSymbol(t.Symbol, t.Strategy);
                        var tag = GetVariantTagForSymbol(t.Symbol, t.Strategy);
                        if (svc != null)
                        {
                            await svc.AddLabeledSampleAsync(feature);
                            loaded++;
                            if (tag == "Major") majorAdded++;
                            else if (tag == "Spike") spikeAdded++;
                            else pumpAdded++;
                        }
                        // Default 도 모든 샘플 받기
                        if (_onlineLearning != null && svc != _onlineLearning)
                        {
                            await _onlineLearning.AddLabeledSampleAsync(feature);
                            defaultAdded++;
                        }
                    }
                    catch { /* per-trade skip */ }
                }

                OnLog?.Invoke($"📊 [BOOTSTRAP] 처리={processed} 로딩={loaded} | Default+={defaultAdded} Major+={majorAdded} Pump+={pumpAdded} Spike+={spikeAdded}");

                // 즉시 강제 재학습 — 최소 샘플 미달이어도 시도 (window>=10이면 train 가능하도록 향후 수정 가능)
                int trained = 0;
                async Task TryTrain(AdaptiveOnlineLearningService? svc, string tag)
                {
                    if (svc == null) return;
                    try
                    {
                        if (svc.WindowSize >= 10)
                        {
                            await svc.ForceRetrainAsync($"BOOTSTRAP_{tag}");
                            trained++;
                            OnLog?.Invoke($"✅ [BOOTSTRAP][{tag}] 강제 재학습 완료 (window={svc.WindowSize})");
                        }
                        else
                        {
                            OnLog?.Invoke($"⏸️ [BOOTSTRAP][{tag}] 재학습 skip (window={svc.WindowSize} < 10)");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [BOOTSTRAP][{tag}] 재학습 실패: {ex.Message}");
                    }
                }
                // [v5.17.0 SMART LEARNING ③] Cross-Variant Transfer
                //   Default 먼저 학습 → 다른 variant 들이 Default 의 학습된 모델을 즉시 활용 가능
                //   각 variant 도 자기 데이터로 추가 학습 시도 (있을 경우)
                await TryTrain(_onlineLearning, "Default");

                // Default 가 학습 성공했으면 zip 파일을 다른 variant 경로로 복사 (warm start)
                try
                {
                    string defaultPath = _mlTrainer.GetModelPath();
                    if (System.IO.File.Exists(defaultPath))
                    {
                        void CopyIfMissing(EntryTimingMLTrainer t, string tag)
                        {
                            try
                            {
                                string targetPath = t.GetModelPath();
                                if (!System.IO.File.Exists(targetPath))
                                {
                                    System.IO.File.Copy(defaultPath, targetPath, overwrite: false);
                                    t.LoadModel();
                                    OnLog?.Invoke($"🔁 [BOOTSTRAP][{tag}] Default 모델 복사 (warm start) → {targetPath}");
                                }
                            }
                            catch (Exception ex) { OnLog?.Invoke($"⚠️ [BOOTSTRAP][{tag}] warm start 실패: {ex.Message}"); }
                        }
                        CopyIfMissing(_mlTrainerMajor, "Major");
                        CopyIfMissing(_mlTrainerPump,  "Pump");
                        CopyIfMissing(_mlTrainerSpike, "Spike");
                    }
                }
                catch (Exception ex) { OnLog?.Invoke($"⚠️ [BOOTSTRAP][warmstart] {ex.Message}"); }

                // 각 variant 자체 데이터로 fine-tune (있을 경우)
                await TryTrain(_onlineLearningMajor, "Major");
                await TryTrain(_onlineLearningPump, "Pump");
                await TryTrain(_onlineLearningSpike, "Spike");

                string report = $"processed={processed} loaded={loaded} trained_variants={trained}/4";
                OnLog?.Invoke($"🎉 [BOOTSTRAP] 완료: {report}");
                return (processed, loaded, report);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [BOOTSTRAP] 예외: {ex.Message}");
                return (0, 0, $"EXCEPTION: {ex.Message}");
            }
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

                // [v5.15.0] 라벨 기준 완화: +1.0% → +0.8% (EntryLabelConfig.TargetProfitPct 와 일치)
                //   사유: MOVRUSDT 같은 +1.2% (4hr) steady uptrend 가 WIN으로 분류되게
                bool shouldEnter = actualProfitPct >= 0.8f;
                feature.ShouldEnter = shouldEnter;
                feature.ActualProfitPct = actualProfitPct;
                feature.Timestamp = entryTime;
                feature.EntryPrice = entryPrice;

                // [v5.16.0 CRITICAL FIX] variant 별 independent sliding window 에 라우팅
                //   기존: _onlineLearning (Default) 1개만 → Major/Pump/Spike 모델은 영원히 동결
                //   수정: 심볼에 따라 해당 variant 의 online learning service 로 전달
                //         ALSO 라벨 샘플을 variant 전용 디렉터리에 저장하여 봇 재시작 시 복구
                var variantService = SelectLearningServiceForSymbol(symbol, labelSource);
                string variantTag = GetVariantTagForSymbol(symbol, labelSource);
                if (variantService != null)
                {
                    await variantService.AddLabeledSampleAsync(feature);
                    OnLog?.Invoke($"[OnlineLearning][{variantTag}] 샘플 추가: {symbol} PnL={actualProfitPct:F2}% → Label={shouldEnter} | window={variantService.WindowSize}");
                }
                // Default 도 병행 유지 (안전장치 — fallback 모델이 필요한 경우)
                if (_onlineLearning != null && variantService != _onlineLearning)
                {
                    await _onlineLearning.AddLabeledSampleAsync(feature);
                }

                // [Lorentzian Phase 1] KNN 분류기에도 동일 샘플 추가 (메모리 + 파일 영구화)
                if (_config.EnableLorentzianGate)
                {
                    _lorentzian.AddSample(feature, shouldEnter, symbol);
                    var lorSample = new Services.LorentzianSample
                    {
                        Features = Services.LorentzianFeatureMapperPublic.Extract(feature),
                        WasSuccessful = shouldEnter,
                        Symbol = symbol,
                        TimestampUtc = entryTime.Kind == DateTimeKind.Utc ? entryTime : entryTime.ToUniversalTime()
                    };
                    _ = _lorentzian.AppendSampleToFileAsync(_lorentzianDataPath, lorSample);
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
            int requiredSeqLen = 8; // 시퀀스 기본 길이

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
            var mlPred = _mlTrainer.Predict(feature);
            float candlesToTarget = mlPred?.ShouldEnter == true ? 8f : -1f;
            float tfConfidence = mlPred?.Probability ?? 0f;

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
            CancellationToken token = default,
            bool forceRetrain = false)
        {
            if (IsReady && !forceRetrain)
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

                // 2. ML.NET 모델 학습 — [v5.10.78 Phase 5-B] Default + 3 Variant 모두 학습
                OnAlert?.Invoke($"🧠 [AI 학습] ML.NET 모델 학습 중... (샘플: {trainingFeatures.Count}개)");

                // 심볼 카테고리별 feature 분리
                var majorFeatures = trainingFeatures.Where(f => IsMajorSymbol(f.Symbol)).ToList();
                var pumpFeatures  = trainingFeatures.Where(f => !IsMajorSymbol(f.Symbol)).ToList();

                // [v5.10.92 ROOT FIX] Pump/Spike variant 학습 데이터 부족 fallback
                if (pumpFeatures.Count < 10 && trainingFeatures.Count >= 10)
                {
                    OnLog?.Invoke($"[AI 학습] ⚠️ pumpFeatures={pumpFeatures.Count}개 부족 → trainingFeatures({trainingFeatures.Count}개) fallback");
                    pumpFeatures = trainingFeatures;
                }
                // [v5.10.99 P2-4] Major variant도 동일 fallback (Major 학습 데이터 부족 대비)
                //   현재 Major 440 samples vs Pump 1540 — 메이저 4개만 있어 데이터 적음
                //   majorFeatures<50이면 trainingFeatures 전체로 보강 (메이저 추론 정확도↑)
                if (majorFeatures.Count < 50 && trainingFeatures.Count >= 50)
                {
                    OnLog?.Invoke($"[AI 학습] ⚠️ majorFeatures={majorFeatures.Count}개 부족 → trainingFeatures({trainingFeatures.Count}개) fallback");
                    majorFeatures = trainingFeatures;
                }
                // Spike는 1분봉 초단타 — pumpFeatures 동일 사용 (향후 분리)
                var spikeFeatures = pumpFeatures;

                async Task TrainVariantAsync(EntryTimingMLTrainer trainer, List<MultiTimeframeEntryFeature> data, string label)
                {
                    if (data.Count < 10)
                    {
                        OnLog?.Invoke($"[AI 학습][{label}] 데이터 부족 ({data.Count}개) — 학습 스킵");
                        return;
                    }
                    string runId = $"INIT_ML_{label}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    try
                    {
                        var metrics = await trainer.TrainAndSaveAsync(data);
                        OnLog?.Invoke($"[AI 학습][{label}] 완료 - Acc: {metrics.Accuracy:P2}, F1: {metrics.F1Score:P2}, N: {data.Count}");
                        await PersistTrainingRunAsync(
                            projectName: "ML.NET",
                            runId: runId,
                            stage: $"Initial_{label}",
                            success: true,
                            sampleCount: data.Count,
                            epochs: 0,
                            accuracy: metrics.Accuracy,
                            f1Score: metrics.F1Score,
                            auc: metrics.AUC,
                            detail: $"{label} variant 초기 학습");
                    }
                    catch (Exception ex)
                    {
                        OnAlert?.Invoke($"❌ [AI 학습][{label}] 실패: {ex.Message}");
                        await PersistTrainingRunAsync(
                            projectName: "ML.NET",
                            runId: runId,
                            stage: $"Initial_{label}",
                            success: false,
                            sampleCount: data.Count,
                            epochs: 0,
                            detail: ex.Message);
                    }
                }

                // Default (전체 통합) + 3 variant 모두 학습
                await TrainVariantAsync(_mlTrainer,      trainingFeatures, "Default");
                await TrainVariantAsync(_mlTrainerMajor, majorFeatures,    "Major");
                await TrainVariantAsync(_mlTrainerPump,  pumpFeatures,     "Pump");
                await TrainVariantAsync(_mlTrainerSpike, spikeFeatures,    "Spike");

                RaiseCriticalTrainingAlert(
                    projectName: "ML.NET",
                    stage: "초기학습",
                    success: true,
                    detail: $"4 모델 학습 완료 (Default={trainingFeatures.Count}, Major={majorFeatures.Count}, Pump={pumpFeatures.Count}, Spike={spikeFeatures.Count})");

                // 3. 모델 리로드 및 상태 확인
                _mlTrainer.LoadModel();
                _mlTrainerMajor.LoadModel();
                _mlTrainerPump.LoadModel();
                _mlTrainerSpike.LoadModel();
                bool tfReady = _mlTrainer.IsModelLoaded; // UI 호환용

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

                string msg = $"✅ [AI 재학습] 완료 - ML: {mlMetrics.Accuracy:P1} (샘플: {labeledData.Count}개)";
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

        // [v3.7.1] AI Gate 임계값 대폭 상향 — 56%는 동전 던지기 수준
        // DB 분석: 승률 40% → 70% 목표, 확신 있는 진입만 허용
        public float MinMLConfidence { get; set; } = 0.65f;           // 56→65%
        public float MinTransformerConfidence { get; set; } = 0.60f;  // 52→60%
        public float MinMLConfidenceMajor { get; set; } = 0.70f;     // 60→70%
        public float MinTransformerConfidenceMajor { get; set; } = 0.65f; // 55→65%
        public float MinMLConfidencePumping { get; set; } = 0.65f;   // 56→65%
        public float MinTransformerConfidencePumping { get; set; } = 0.60f; // 54→60%

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

        // ═══════════════════════════════════════════════════════════════
        // [v5.10.66] 메이저 진입 임시 차단 (AI 학습 정상화 후 false 복원 예정)
        // 7일 분석: MAJOR 365건, 승률 38%, AvgPnL -$8.59/건, Total -$3,133
        // 라벨링 6% 성공률 → ML 모델 비관적 학습 → 차단 못한 메이저는 큰 손실
        // ═══════════════════════════════════════════════════════════════
        public bool BlockMajorEntries { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // [Lorentzian Phase 1] KNN 사이드카 게이트 설정 (soft mode)
        // Phase 1: 경고만 출력, 진입 차단은 안 함. Phase 2부터 차단 활성화 예정.
        // ═══════════════════════════════════════════════════════════════
        public bool EnableLorentzianGate { get; set; } = true;
        public int LorentzianK { get; set; } = 10;
        public int LorentzianMinSamples { get; set; } = 100;
        public int LorentzianMaxSamples { get; set; } = 5000;
        public int MinLorentzianScore { get; set; } = 2;        // -K~+K 범위 (K=10이면 ±10)
        public float MinLorentzianPassRate { get; set; } = 0.55f; // 60% 이상 양성 투표

        // [Lorentzian Phase 2 v5.10.80] Hard mode: 샘플 충분 시 KNN 약세 진입 차단
        public bool LorentzianHardMode { get; set; } = false;     // 기본 비활성 (안전)
        public int LorentzianHardModeMinSamples { get; set; } = 200; // 최소 샘플 수

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

        // [Lorentzian Phase 1] KNN 사이드카 검증 결과
        public bool LorentzianReady { get; set; }
        public int LorentzianScore { get; set; }       // -K ~ +K (K=10이면 ±10)
        public float LorentzianPassRate { get; set; }  // 0~1
        public int LorentzianSampleCount { get; set; }
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

