using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Authentication;
using Binance.Net.Objects.Models.Futures.Socket;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO; // [FIX] Path, File 사용을 위해 추가
using System.Text;
using System.Text.Json;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Strategies;
using TradingBot.Services.AI;
using TradingBot.Services.AI.RL; // [Agent 2, 3] 네임스페이스 추가
using TradingBot.Services.Infrastructure; // [Agent 3] 메모리 관리
using System.Threading.Channels; // [Agent 3] 추가
using TradingBot.Shared.Models; // [Phase 11] Shared Models (NotificationChannel 등)
using PositionInfo = TradingBot.Shared.Models.PositionInfo;
using CandleData = TradingBot.Models.CandleData;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot
{
    public class TradingEngine : IDisposable
    {
        private bool _disposed = false;
        public bool IsBotRunning { get; private set; } = false;
        private readonly IBinanceRestClient _client;
        // [v5.22.27] 외부 노출 — BinancePositionHistorySync 등에서 사용
        public IBinanceRestClient GetRestClient() => _client;
        private CancellationTokenSource? _cts;
        private readonly string apiKey;
        private readonly string apiSecret;

        // 감시 종목 리스트
        private readonly List<string> _symbols;

        public decimal InitialBalance { get; private set; } = 0; // 프로그램 시작 시점의 자산
        public decimal NetTransferAmount { get; private set; } = 0; // 순 투입금 (Transfer In - Out)
        private DateTime _engineStartTime = DateTime.MinValue; // [수정] 엔진 시작 시에만 설정
        private DateTime _lastReportTime = DateTime.MinValue;
        private DateTime _lastAiGateSummaryTime = DateTime.MinValue;
        private DateTime _lastEntryBlockSummaryTime = DateTime.MinValue;
        private decimal _periodicReportBaselineEquity = 0m;
        private static readonly TimeSpan EntryBlockSummaryInterval = TimeSpan.FromMinutes(10);

        // [일일 수익 목표] $250/일 기준 (알림 전용, 차단 없음 - 초과 수익 허용)
        private bool _dailyTargetHitNotified = false;
        private DateTime _dailyTargetResetDate = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, int> _entryBlockReasonCounts = new(StringComparer.OrdinalIgnoreCase);
        private long _entryBlockTotalCount = 0;

        // 현재 보유 중인 포지션 정보 (심볼, 진입가)
        private Dictionary<string, PositionInfo> _activePositions = new Dictionary<string, PositionInfo>();
        private readonly object _posLock = new object();
        // 블랙리스트 (심볼, 해제시간) - 지루함 청산 종목 재진입 방지
        private ConcurrentDictionary<string, DateTime> _blacklistedSymbols = new ConcurrentDictionary<string, DateTime>();
        // [v5.1.1] 손절 후 쿨다운 (5분) — 같은 심볼 즉시 재진입 방지
        private readonly ConcurrentDictionary<string, DateTime> _stopLossCooldown = new();
        // [v4.9.4] 중복 시그널 debounce — (symbol|direction) 기준 최근 시도 시각
        private readonly ConcurrentDictionary<string, DateTime> _recentEntryAttempts = new();
        // [FIX] 최근 청산 쿨다운 — ACCOUNT_UPDATE 도착 시 팬텀 EXTERNAL_PARTIAL_CLOSE_SYNC 방지
        private readonly ConcurrentDictionary<string, DateTime> _recentlyClosedCooldown = new();
        // [v3.4.0] 부분청산 쿨다운 — 봇 자체 부분청산 후 ACCOUNT_UPDATE 이중 기록 방지
        private readonly ConcurrentDictionary<string, DateTime> _recentPartialCloseCooldown = new();
        // [v4.0.3] PUMP 슬롯 교체 대상
        private string? _pendingSwapEvict;

        // [v4.0.1] D1+H4 방향 캐시 (API 절약: 5분 캐시)
        private readonly ConcurrentDictionary<string, (float bias, DateTime time)> _directionBiasCache = new(StringComparer.OrdinalIgnoreCase);

        // [v3.8.0] PUMP 감시 풀 — 거래량 급증 감지 → 상승 확인 후 진입
        // symbol → (watchPrice, watchTime, volumeRatio)
        private readonly ConcurrentDictionary<string, (decimal WatchPrice, DateTime WatchTime, decimal VolRatio)>
            _pumpWatchPool = new(StringComparer.OrdinalIgnoreCase);
        // 슬롯 설정
        // 메이저 4 + PUMP 2 = 총 6
        private const int MAX_TOTAL_SLOTS = 7;        // 총 최대 7개 (메이저4 + PUMP3) — fallback
        private const int MAX_SPIKE_SLOTS = 1;        // [v5.2.0] SPIKE(급등) 전용 1개 추가

        // [v5.12.0] 급등(SPIKE) 범주 단일 슬롯 — SPIKE_FAST + MAJOR_MEME(=PumpScan 3 fast paths) 통틀어 1개만
        //   이유: 오늘 승률 27%, 급등 경로 동시 다수 진입으로 하락장 대량 물림
        //   범위: SPIKE_FAST, MAJOR_MEME (PumpScanStrategy의 M1_FAST_PUMP / MEGA_PUMP / TOP_SCORE_ENTRY)
        //   제외: PUMP_WATCH_CONFIRMED, MAJOR, 기타
        private const int MAX_SPIKE_CATEGORY_SLOTS = 1;
        private readonly ConcurrentDictionary<string, (DateTime entryTime, string source)> _activeSpikeSlot
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// [v5.12.0] 급등 범주 signalSource 분류 — 단일 슬롯 제한 대상
        /// </summary>
        private static bool IsSpikeCategorySignal(string? signalSource)
        {
            if (string.IsNullOrEmpty(signalSource)) return false;
            return signalSource.Equals("SPIKE_FAST", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("MAJOR_MEME", StringComparison.OrdinalIgnoreCase);
        }

        // [v5.10.2] 슬롯 / 일일한도 → DB 설정 연동 (하드코딩 const 제거)
        private int MAX_MAJOR_SLOTS => _settings?.MaxMajorSlots > 0 ? _settings.MaxMajorSlots : 4;
        private int MAX_PUMP_SLOTS  => _settings?.MaxPumpSlots  > 0 ? _settings.MaxPumpSlots  : 3;

        // [v4.5.9] 일일 PUMP 진입 횟수 제한 (자정 KST 리셋) — DB 설정 연동
        private int MAX_DAILY_PUMP_ENTRIES => _settings?.MaxDailyEntries > 0 ? _settings.MaxDailyEntries : 60;
        private int _dailyPumpEntryCount = 0;
        private DateTime _dailyPumpCountDate = DateTime.MinValue;
        private readonly object _dailyPumpLock = new();

        // [v4.5.10] 심볼별 일일 리버스 횟수 제한 (플립플롭 방지)
        private const int MAX_DAILY_REVERSAL_PER_SYMBOL = 3;
        private readonly ConcurrentDictionary<string, int> _dailyReversalCount = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _dailyReversalCountDate = DateTime.MinValue;
        private readonly object _dailyReversalLock = new();

        // [v4.5.11] 일일 수익 기반 모드 단계 (Phase 1: 규칙 기반 보수 모드)
        public enum DailyProfitMode
        {
            Aggressive,          // $0 ~ $200 평상시
            Transition,          // $200 ~ $250 전환 구간
            Conservative,        // $250 ~ $500 보수 모드
            UltraConservative    // $500+ 1개 포지션만 유지
        }

        /// <summary>
        /// 현재 일일 수익 기반 모드 반환
        /// </summary>
        public DailyProfitMode GetCurrentProfitMode()
        {
            decimal dailyPnl = _riskManager?.DailyRealizedPnl ?? 0m;
            if (dailyPnl >= 500m) return DailyProfitMode.UltraConservative;
            if (dailyPnl >= 250m) return DailyProfitMode.Conservative;
            if (dailyPnl >= 200m) return DailyProfitMode.Transition;
            return DailyProfitMode.Aggressive;
        }
        private const decimal PUMP_FIXED_MARGIN_USDT = 100m; // (레거시 fallback) PUMP 고정 증거금
        private const int PUMP_MANUAL_LEVERAGE = 20; // 20배 롱 전용 대응 매뉴얼
        
        // [NEW 개선안 2] SLOT 쿨다운 추적: SLOT 차단된 심볼의 재시도 시간제한
        private readonly ConcurrentDictionary<string, DateTime> _slotBlockedSymbols =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private int _slotCooldownMinutes = 3; // $250/일 목표: 3분으로 단축 (빠른 재진입)

        /// <summary>[v5.2.9] 신호 첫 발생 가격 기록 — 슬롯 차단 후 뒤늦은 고점 진입 방지</summary>
        private readonly ConcurrentDictionary<string, (decimal Price, DateTime Time)> _signalOriginPrice =
            new(StringComparer.OrdinalIgnoreCase);

        // [v5.10.38] PUMP 우선순위 진입 큐 — 슬롯 포화 시 AI 승인 점수 순으로 재진입
        private sealed record PumpPriorityCandidate(
            string Symbol, string Decision, float BlendedScore,
            decimal OriginPrice, DateTime RegisteredAt, string SignalSource);
        private readonly object _pumpPriorityLock = new();
        private readonly List<PumpPriorityCandidate> _pumpPriorityQueue = new();
        private readonly ConcurrentDictionary<string, (float Score, DateTime Time)> _aiApprovedRecentScores =
            new(StringComparer.OrdinalIgnoreCase);

        private const int SYMBOL_ANALYSIS_MIN_INTERVAL_MS = 2000;  // [v3.2.8] 1초→2초 (CPU 절감)
        private const int MAJOR_SYMBOL_ANALYSIS_MIN_INTERVAL_MS = 1000; // [v3.2.8] 180ms→1초
        // [v5.21.8] MainLoopInterval 1초 → 3초 — 1코어 100% 점유 해소
        //   원인: workMs avg = 1050ms (1초짜리 작업) + interval 1초 = 쉴 틈 없이 1코어 풀가동
        //   해결: 3초로 완화 → CPU 부하 ~33% 로 감소. 진입 반응성은 GATE 디바운스(5초)와 동조
        private static readonly TimeSpan MainLoopInterval = TimeSpan.FromSeconds(3);
        private decimal _minEntryRiskRewardRatio = 1.20m; // [v3.2.3] 1.40→1.20: 폭락 후 반등 진입 기회 확보
        private bool _rrConfigMismatchWarned = false;
        private float _fifteenMinuteMlMinConfidence = 0.65f; // 가이드 기본값
        private float _fifteenMinuteTransformerMinConfidence = 0.60f; // 가이드 기본값
        private float _aiScoreThresholdMajor = 70.0f; // 설정에서 로드 (최소 70 보장)
        private float _aiScoreThresholdNormal = 70.0f; // 설정에서 로드 (최소 70 보장)
        private float _aiScoreThresholdPump = 66.0f; // 설정에서 로드 (PUMP 전용)
        private bool _enableAiScoreFilter = true; // 설정에서 로드
        private bool _enableFifteenMinWaveGate = true; // 설정에서 로드
        private const float GateMlThresholdMin = 0.40f;  // 하향: 0.48→0.40 (약한 타점 70% 신뢰도에서도 진입)
        private const float GateMlThresholdMax = 0.72f;
        private const float GateTransformerThresholdMin = 0.40f;  // 하향: 0.47→0.40
        private const float GateTransformerThresholdMax = 0.70f;
        private const float GateThresholdAdjustStep = 0.015f;  // [동적 최적화] 1.0% → 1.5% (더 공격적 조정)
        private const int GateAutoTuneSampleSize = 16;         // [동적 최적화] 24 → 16 (더 빠른 적응)
        private const float GateTightenPassRate = 0.55f;       // [동적 최적화] 62% → 55% (더 엄격한 기준)
        private const float GateLoosenPassRate = 0.30f;        // [동적 최적화] 20% → 30% (더 빠른 완화)
        private bool _mainLoopPerfEnabled = true;
        private bool _mainLoopAutoTuneEnabled = true;
        private int _mainLoopBaseWarnMs = 1500;
        private int _mainLoopBasePerfLogIntervalSec = 20;
        private int _mainLoopWarnMs = 1500;
        private int _mainLoopPerfLogIntervalSec = 20;
        private int _mainLoopWarnMinMs = 700;
        private int _mainLoopWarnMaxMs = 8000;
        private int _mainLoopPerfLogIntervalMinSec = 5;
        private int _mainLoopPerfLogIntervalMaxSec = 60;
        private int _mainLoopTuneSampleWindow = 60;
        private int _mainLoopTuneMinIntervalSec = 30;
        private readonly Queue<int> _mainLoopRecentWorkSamples = new Queue<int>();
        private DateTime _nextMainLoopPerfLogTime = DateTime.UtcNow.AddSeconds(20);
        private DateTime _nextMainLoopTuneTime = DateTime.UtcNow.AddSeconds(30);
        private long _mainLoopTotalMs;
        private int _mainLoopSamples;
        private int _mainLoopMaxMs;

        // 전략 인스턴스
        // [AI 제거] PumpScanStrategy 제거
        private MajorCoinStrategy? _majorStrategy;
        private readonly MarketCrashDetector _crashDetector = new();
        // [v4.5.5] 알트 불장 자동 감지기
        private readonly AltBullMarketDetector _altBullDetector = new();

        // [AI 제거] LorentzianV2 / SimpleAI 분류기 모두 제거
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _simpleAiBackfilled
            = new(StringComparer.OrdinalIgnoreCase);

        // [v4.5.6] AI 모델 정확도 추적 (하드 체크 자동 해제용)
        private double _pumpModelAccuracy = 0.0;
        private double _pumpSpikeAccuracy = 0.0;   // [v4.5.8] 급등진입 모델
        private double _tradeSignalAccuracy = 0.0;
        private double _directionModelAccuracy = 0.0;
        private double _survivalPumpAccuracy = 0.0;

        // [v5.22.16] _pumpForecasterAccuracy / _majorForecasterAccuracy / _spikeForecasterAccuracy / ForecasterMinAccuracyForEntry 통째 제거 (Forecaster 의존 제거됨)
        // 70% 이상 정확도 달성 시 하드 체크 자동 해제
        private const double AiHardCheckBypassThreshold = 0.70;
        private bool IsAiModelReadyForPumpEntry =>
            _pumpModelAccuracy >= AiHardCheckBypassThreshold
            && _pumpSpikeAccuracy >= AiHardCheckBypassThreshold
            && _tradeSignalAccuracy >= AiHardCheckBypassThreshold;

        /// <summary>
        /// [v4.5.11] 일일 수익 모드 기반 PUMP 진입 사전 검증
        /// - UltraConservative($500+): 포지션 1개 초과 시 차단
        /// - Conservative($250+): AI 임계값 강화, 사이즈 50%
        /// - Transition($200~$250): AI 임계값 5%p 상향
        /// - Aggressive($0~$200): 평상시 (제약 없음)
        /// </summary>
        /// <returns>(allowed, sizeMultiplier, aiThresholdBoost)</returns>
        private (bool allowed, decimal sizeMultiplier, float aiThresholdBoost) CheckDailyProfitModeForPumpEntry(string symbol, string source)
        {
            var mode = GetCurrentProfitMode();
            int totalPositions;
            lock (_posLock) { totalPositions = _activePositions.Count; }

            switch (mode)
            {
                case DailyProfitMode.UltraConservative:
                    // $500+ : 1개 포지션만 유지 (기존 포지션 있으면 모두 차단)
                    if (totalPositions >= 1)
                    {
                        OnStatusLog?.Invoke($"⛔ [보수모드 $500+] {symbol} {source} 차단 — 포지션 {totalPositions}개 (최대 1개 제한)");
                        return (false, 0m, 0f);
                    }
                    // 신규 진입은 매우 엄격
                    OnStatusLog?.Invoke($"🛡️ [초보수모드 $500+] {symbol} {source} — AI +15%p, 사이즈 40%");
                    return (true, 0.4m, 0.15f);

                case DailyProfitMode.Conservative:
                    // $250~$500 : 사이즈 50%, AI +10%p
                    OnStatusLog?.Invoke($"🛡️ [보수모드 $250+] {symbol} {source} — AI +10%p, 사이즈 50%");
                    return (true, 0.5m, 0.10f);

                case DailyProfitMode.Transition:
                    // $200~$250 : AI +5%p
                    return (true, 0.8m, 0.05f);

                default:
                    return (true, 1.0m, 0f);
            }
        }

        /// <summary>
        /// [v4.5.11] 일일 세션 컨텍스트를 MultiTimeframeFeatureExtractor에 주입
        /// - ML 모델이 "목표 달성 후 보수적 판단"을 학습할 수 있도록
        /// </summary>
        private void UpdateDailyContextForFeatures()
        {
            try
            {
                // [v4.6.2] 자정 자동 리셋 — 시스템 로컬 시간 기준 (Windows 한국시간)
                // 단순 비교: 오늘 날짜와 마지막 리셋 날짜가 다르면 리셋
                var todayKst = DateTime.Now.Date;
                lock (_dailyPumpLock)
                {
                    if (todayKst > _dailyPumpCountDate)
                    {
                        if (_dailyPumpCountDate != DateTime.MinValue)
                            OnStatusLog?.Invoke($"🔄 [일일 PUMP 카운터] 자정 자동 리셋 ({todayKst:yyyy-MM-dd}), 이전={_dailyPumpEntryCount}");
                        _dailyPumpCountDate = todayKst;
                        _dailyPumpEntryCount = 0;
                    }
                }
                lock (_dailyReversalLock)
                {
                    if (todayKst > _dailyReversalCountDate)
                    {
                        if (_dailyReversalCountDate != DateTime.MinValue)
                            OnStatusLog?.Invoke($"🔄 [리버스 카운터] 자정 자동 리셋 ({todayKst:yyyy-MM-dd})");
                        _dailyReversalCountDate = todayKst;
                        _dailyReversalCount.Clear();
                    }
                }

                // [AI 제거] MultiTimeframeFeatureExtractor 컨텍스트 제거
                _ = _riskManager?.DailyRealizedPnl ?? 0m;
                int tradeCount;
                lock (_dailyPumpLock) { tradeCount = _dailyPumpEntryCount; }
                _ = tradeCount;
            }
            catch { /* 무시 */ }
        }

        /// <summary>
        /// [v4.5.10] 심볼별 일일 리버스 횟수 체크 및 예약 (자정 KST 리셋)
        /// </summary>
        private bool TryReserveDailyReversal(string symbol, string source)
        {
            lock (_dailyReversalLock)
            {
                var todayKst = DateTime.Now.Date; // [v4.6.2] 시스템 로컬 시간 (KST)
                if (todayKst > _dailyReversalCountDate)
                {
                    _dailyReversalCountDate = todayKst;
                    _dailyReversalCount.Clear();
                    OnStatusLog?.Invoke($"🔄 [리버스 카운터] 자정 리셋 ({todayKst:yyyy-MM-dd})");
                }

                int currentCount = _dailyReversalCount.GetOrAdd(symbol, 0);
                if (currentCount >= MAX_DAILY_REVERSAL_PER_SYMBOL)
                {
                    OnStatusLog?.Invoke($"⛔ [리버스 한도] {symbol} {source} 차단 ({currentCount}/{MAX_DAILY_REVERSAL_PER_SYMBOL}) — 내일 리셋");
                    return false;
                }

                _dailyReversalCount[symbol] = currentCount + 1;
                OnStatusLog?.Invoke($"📊 [리버스 카운터] {symbol} {_dailyReversalCount[symbol]}/{MAX_DAILY_REVERSAL_PER_SYMBOL} ({source})");
                return true;
            }
        }

        /// <summary>
        /// [v4.5.10] MACD 반대 크로스 감지 시 50% 부분 청산
        /// - 추세 전환 일부 대응 (100% 청산 후 리버스는 너무 공격적)
        /// - 나머지 50%는 기존 트레일링 스탑으로 관리
        /// - 심볼당 하루 3회 제한
        /// </summary>
        private async Task HandleOppositeMacdCrossAsync(
            string symbol,
            bool positionIsLong,
            MacdCrossResult crossResult,
            CancellationToken token)
        {
            try
            {
                // 상위 TF 검증 — 반대 방향 정배열인지 확인
                bool shouldClose;
                string htfDetail;
                if (positionIsLong)
                {
                    // LONG 보유 + 데드크로스 → H1/M15 약세 전환 확인
                    var (isBearish, detail) = await _macdCrossService!.CheckHigherTimeframeBearishAsync(symbol, token);
                    shouldClose = isBearish;
                    htfDetail = detail;
                }
                else
                {
                    // SHORT 보유 + 골든크로스 → H1/M15 강세 전환 확인
                    var (isBullish, detail) = await _macdCrossService!.CheckHigherTimeframeBullishAsync(symbol, token);
                    shouldClose = isBullish;
                    htfDetail = detail;
                }

                if (!shouldClose)
                {
                    OnStatusLog?.Invoke($"ℹ️ [MACD-반대] {symbol} {crossResult.CrossType} 감지되었으나 상위TF 미전환 → 유지 | {htfDetail}");
                    return;
                }

                // 일일 리버스 한도 체크
                if (!TryReserveDailyReversal(symbol, $"MACD_OPPOSITE_{crossResult.CrossType}"))
                    return;

                // 현재 수량 확인 + 거래소 TP 중복 방지
                decimal qty = 0;
                bool tpOnExchange = false;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var pos))
                    {
                        qty = Math.Abs(pos.Quantity);
                        tpOnExchange = pos.TpRegisteredOnExchange;
                    }
                }
                if (qty <= 0) return;

                // [v5.9.21] 거래소 TP/Trailing이 이미 등록된 경우 로컬 청산 스킵 (이중 청산 방지)
                if (tpOnExchange)
                {
                    OnStatusLog?.Invoke($"ℹ️ [MACD-반대] {symbol} 거래소 TP 등록됨 → 로컬 부분청산 스킵 (이중 방지)");
                    return;
                }

                // 50% 부분 청산 수량 계산
                decimal partialQty = Math.Round(qty * 0.5m, 3, MidpointRounding.ToZero);
                if (partialQty <= 0)
                {
                    OnStatusLog?.Invoke($"⚠️ [MACD-반대] {symbol} 부분청산 수량 계산 불가 (qty={qty})");
                    return;
                }

                string side = positionIsLong ? "SELL" : "BUY";
                OnAlert?.Invoke(
                    $"⚠️ [추세 전환] {symbol} MACD {crossResult.CrossType} + 상위TF 전환\n" +
                    $"→ 50% 부분 청산 ({partialQty:F4} / {qty:F4}) | {crossResult.Detail}");

                var partialResult = await _exchangeService.PlaceMarketOrderAsync(
                    symbol, side, partialQty, token, reduceOnly: true);

                if (partialResult.Success)
                {
                    OnStatusLog?.Invoke($"✅ [MACD-반대] {symbol} 50% 부분청산 완료 (filled={partialResult.FilledQuantity:F4})");
                    // 남은 50%는 기존 트레일링 스탑으로 관리됨
                    // DB 반영은 ACCOUNT_UPDATE 이벤트 또는 다음 동기화에서 처리됨
                }
                else
                {
                    OnStatusLog?.Invoke($"❌ [MACD-반대] {symbol} 부분청산 실패");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [MACD-반대] {symbol} 처리 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// [v5.10.17] 일일 PUMP 진입 한도 체크 — 카운트 증가 없음 (게이트 전용)
        /// 실제 카운트는 진입 성공 후 CommitDailyPumpEntry 호출
        /// </summary>
        private bool TryReserveDailyPumpEntry(string symbol, string source)
        {
            lock (_dailyPumpLock)
            {
                var todayKst = DateTime.Now.Date;
                if (todayKst > _dailyPumpCountDate)
                {
                    _dailyPumpCountDate = todayKst;
                    _dailyPumpEntryCount = 0;
                    OnStatusLog?.Invoke($"🔄 [일일 PUMP 카운터] 자정 리셋 ({todayKst:yyyy-MM-dd})");
                }

                if (_dailyPumpEntryCount >= MAX_DAILY_PUMP_ENTRIES)
                {
                    OnStatusLog?.Invoke($"⛔ [일일한도] {symbol} {source} 진입 차단 ({_dailyPumpEntryCount}/{MAX_DAILY_PUMP_ENTRIES}) — 내일 자정 리셋");
                    return false;
                }

                // [v5.10.17] 여기서는 카운트 증가 안 함 — 실제 진입 성공 후 CommitDailyPumpEntry에서 증가
                return true;
            }
        }

        /// <summary>
        /// [v5.10.17] 실제 PUMP 진입 성공 시 카운트 증가 — PlaceMarketOrderAsync 성공 후 호출
        /// </summary>
        private void CommitDailyPumpEntry(string symbol)
        {
            lock (_dailyPumpLock)
            {
                _dailyPumpEntryCount++;
                OnStatusLog?.Invoke($"📊 [일일 PUMP 카운터] {_dailyPumpEntryCount}/{MAX_DAILY_PUMP_ENTRIES} ({symbol} 실제진입 확정)");
            }
        }
        // [v3.3.6] 급변동 회복 구간 추적: symbol → (extremePrice, isUpwardSpike, eventTime)
        private readonly ConcurrentDictionary<string, (decimal ExtremePrice, bool IsUpwardSpike, DateTime EventTime)>
            _volatilityRecoveryZone = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan VolatilityRecoveryDuration = TimeSpan.FromHours(4);
        // [AI 제거] MarketRegimeClassifier / ExitOptimizerService 제거
        private MacdCrossSignalService? _macdCrossService;
        private GridStrategy _gridStrategy;
        private ArbitrageStrategy _arbitrageStrategy;
        // private TransformerStrategy? _transformerStrategy; // TensorFlow 전환 중 임시 비활성화
        // private TransformerTrainer? _transformerTrainer; // TensorFlow 전환 중 임시 비활성화
        private ElliottWave3WaveStrategy _elliotWave3Strategy; // [3파 확정형 단타]
        private FifteenMinBBSqueezeBreakoutStrategy _fifteenMinBBSqueezeStrategy; // [15분봉 BB 스퀴즈 돌파]
        private HybridExitManager _hybridExitManager; // [하이브리드 AI 익절/손절 관리]
        private BinanceExecutionService _executionService; // [실시간 레버리지 주문 실행 서비스]

        private IExchangeService _exchangeService;

        // [v5.10.54] _orderService (BinanceOrderService) 필드 제거 — 사용 없음, BinanceExchangeService로 통일

        private PositionMonitorService _positionMonitor;

        // [v5.10.18] 거래소 폴링 기반 포지션 동기화 + 브라켓 주문 관리
        private OrderManager _orderManager = null!;
        private PositionSyncService _positionSyncService = null!;

        private SoundService _soundService;

        private readonly DbManager _dbManager;
        private readonly PatternMemoryService _patternMemoryService;

        private readonly ConcurrentDictionary<string, byte> _runningStandardMonitors = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, byte> _runningPumpMonitors = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, byte> _uiTrackedSymbols = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private ConcurrentDictionary<string, DateTime> _lastTickerUpdateTimes = new ConcurrentDictionary<string, DateTime>();

        private ConcurrentDictionary<string, DateTime> _lastAnalysisTimes = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, decimal> _pendingAnalysisPrices = new ConcurrentDictionary<string, decimal>();
        private readonly ConcurrentDictionary<string, byte> _analysisWorkers = new ConcurrentDictionary<string, byte>();
        private readonly SemaphoreSlim _analysisConcurrencyLimiter = new SemaphoreSlim(8, 8);

        // [v5.17.0 REDESIGN] 15-5-1 엔진 — 15m 필터 / 5m 전략 / 1m 체결
        private readonly TradingBot.Services.FifteenFiveOneEngine _entryEngine151 = new();
        // 5m 종가 확정 디바운스 (각 심볼당 마지막 처리한 5m 캔들 종가 시각)
        private readonly ConcurrentDictionary<string, DateTime> _last5mProcessedAt = new(StringComparer.OrdinalIgnoreCase);
        // 15m 종가 확정 디바운스 (각 심볼당 마지막 처리한 15m 캔들 종가 시각)
        private readonly ConcurrentDictionary<string, DateTime> _last15mProcessedAt = new(StringComparer.OrdinalIgnoreCase);

        // [AI 제거] 모든 ML.NET / AI 워커 / 분류기 / 회귀자 / 예측기 제거
        // [v5.22.16] _entryScheduler 필드 제거 — Forecaster 의존
        // [v5.10.54] 주문 라이프사이클 단일 진입점 (SL/TP/Trailing 등록/취소/본절교체)
        private TradingBot.Services.OrderLifecycleManager? _orderLifecycle;
        private readonly TradingBot.Services.TickDensityMonitor _tickMonitor = new();

        private DateTime _lastCleanupTime = DateTime.Now;

        private Channel<IBinance24HPrice> _tickerChannel;
        // [Agent 2] 비동기 성능 개선: 주문 및 계좌 업데이트용 채널 추가
        private Channel<BinanceFuturesStreamAccountUpdate> _accountChannel;
        private Channel<BinanceFuturesStreamOrderUpdate> _orderChannel;

        private ConcurrentQueue<string> _apiLogBuffer = new ConcurrentQueue<string>(); // [추가] API용 로그 버퍼

        private readonly MarketDataManager _marketDataManager;
        private readonly RiskManager _riskManager;
        // [AI 제거] AIPredictor / AIDoubleCheckEntryGate / HybridNavigatorSniper 모두 제거
        // private DoubleCheckEntryEngine? _waveEntryEngine; // [WaveAI] TensorFlow 전환 중 임시 비활성화
        // private SimpleDoubleCheckEngine? _simpleDoubleCheck; // [간소화] TensorFlow 전환 중 임시 비활성화
        // private MultiAgentManager _multiAgentManager; // TensorFlow 전환 중 임시 비활성화
        private MarketHistoryService? _marketHistoryService;
        private OiDataCollector? _oiCollector;

        // [AI 모니터링] 예측 추적용 Dictionary (symbol + timestamp -> 예측 정보)
        private ConcurrentDictionary<string, (DateTime timestamp, decimal entryPrice, decimal predictedPrice, bool predictedDirection, string modelName, float confidence)> _pendingPredictions
            = new ConcurrentDictionary<string, (DateTime, decimal, decimal, bool, string, float)>();
        private readonly ConcurrentDictionary<string, DateTime> _lastMlMonitorRecordTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _scheduledPredictionValidations = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ActiveDecisionIdState> _activeAiDecisionIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _activeAiDecisionIdsPath = Path.Combine("TrainingData", "EntryDecisions", "ActiveDecisionIds.json");
        private const int ActiveDecisionIdRetentionHours = 48;
        private readonly SemaphoreSlim _predictionValidationLimiter = new SemaphoreSlim(3, 3);
        private static readonly TimeSpan MlMonitorRecordInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PredictionValidationDelay = TimeSpan.FromMinutes(5);
        private const decimal PredictionValidationNeutralMovePct = 0.0015m;

        private sealed class ActiveDecisionIdState
        {
            public string DecisionId { get; set; } = string.Empty;
            public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class GateThresholdState
        {
            public readonly object SyncRoot = new object();
            public float MlThreshold;
            public float TransformerThreshold;
            public int SampleCount;
            public int PassCount;
            public int BlockCount;

            public GateThresholdState(float mlThreshold, float transformerThreshold)
            {
                MlThreshold = mlThreshold;
                TransformerThreshold = transformerThreshold;
            }
        }

        private readonly ConcurrentDictionary<string, GateThresholdState> _symbolGateThresholds = new(StringComparer.OrdinalIgnoreCase);

        // [캔들 확인 지연 진입] 신호 발생 시 즉시 진입하지 않고 다음 캔들 확인 후 진입
        private readonly ConcurrentDictionary<string, DelayedEntrySignal> _pendingDelayedEntries
            = new ConcurrentDictionary<string, DelayedEntrySignal>();

        // [ETA 자동 재평가 스케줄] AI 예측이 미래 시점 고확률 진입을 제안한 경우, 해당 시점에 자동 재평가
        private readonly ConcurrentDictionary<string, DateTime> _scheduledEtaReEvaluations
            = new ConcurrentDictionary<string, DateTime>();

        // [AI 제거] _latestAiForecasts 제거 — AIEntryForecastResult 의존
        private readonly ConcurrentDictionary<string, DateTime> _scoutAddOnPendingSymbols
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // [v5.10.90] 봇 자체 진입 주문 추적 (partial fill을 외부 진입으로 오분류 방지)
        //   METUSDT 진입 1건이 Binance partial fill 8청크로 나뉨 → ACCOUNT_UPDATE 8번 발생
        //   → 봇이 "외부 수량 증가"로 잘못 기록 (EXTERNAL_POSITION_INCREASE_SYNC × 8)
        //   수정: 시장가 주문 직전 symbol+시각 저장 → ACCOUNT_UPDATE 처리 시 10초 이내면 봇 자체 fill로 간주
        private readonly ConcurrentDictionary<string, DateTime> _recentBotEntries
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private void MarkBotEntryInProgress(string symbol)
        {
            if (!string.IsNullOrWhiteSpace(symbol))
                _recentBotEntries[symbol] = DateTime.UtcNow;
            // 오래된 엔트리 정리 (60초 초과)
            if (_recentBotEntries.Count > 100)
            {
                var cutoff = DateTime.UtcNow.AddSeconds(-60);
                foreach (var kv in _recentBotEntries)
                    if (kv.Value < cutoff) _recentBotEntries.TryRemove(kv.Key, out _);
            }
        }
        private bool IsRecentBotEntry(string symbol, int withinSeconds = 10)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            if (!_recentBotEntries.TryGetValue(symbol, out var ts)) return false;
            return (DateTime.UtcNow - ts).TotalSeconds <= withinSeconds;
        }

        // [v5.10.85] 메이저 = 설정창 "주요 심볼" (txtSymbols) 값에서 동적 로드
        //   기존: hardcoded HashSet → 사용자가 설정창에서 변경해도 반영 안 됨
        //   수정: AppConfig.Current.Trading.Symbols 직접 참조 → 사용자 설정이 source of truth
        //   기본값: BTC/ETH/SOL/XRP (line 921 DefaultSymbols)
        private HashSet<string> MajorSymbols
        {
            get
            {
                var src = AppConfig.Current?.Trading?.Symbols;
                if (src == null || src.Count == 0)
                    return new HashSet<string>(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" }, StringComparer.OrdinalIgnoreCase);
                return new HashSet<string>(src, StringComparer.OrdinalIgnoreCase);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════
        // [v5.10.81] 글로벌 진입 게이트 (단일 진입점)
        // — ALL 신규 진입(reduceOnly=false) 경로는 PlaceEntryOrderAsync 또는 IsEntryAllowed 호출 필수.
        // — 새 진입 경로 추가 시 누락되지 않도록 _exchangeService.PlaceMarketOrderAsync/PlaceOrderAsync 직접 호출 금지.
        // — 단일 지점에서 EnableMajorTrading 등 글로벌 차단 강제 → "버전업할 때마다 누락" 문제 근본 차단.
        // ═══════════════════════════════════════════════════════════════════════════════════
        // [v5.19.5] 수동 청산 후 재진입 차단 (사용자 의도 존중) — symbol → 청산 시각
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _manualCloseCooldown = new();
        public TimeSpan ManualCloseCooldown { get; set; } = TimeSpan.FromMinutes(30);

        public void RegisterManualCloseCooldown(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            _manualCloseCooldown[symbol] = DateTime.UtcNow;
            OnStatusLog?.Invoke($"⏱️ [수동청산-COOLDOWN] {symbol} {ManualCloseCooldown.TotalMinutes:F0}분 재진입 차단 등록");
        }

        // [v5.22.24] 활성포지션 카테고리 결정 — slot 카운트용. EntrySignalSource 기준 (없으면 GENERIC fallback).
        private static string ResolveActivePositionCategory(PositionInfo pos, string symbol)
        {
            // 메이저 심볼은 source 무관 MAJOR
            if (!string.IsNullOrEmpty(symbol))
            {
                switch (symbol)
                {
                    case "BTCUSDT": case "ETHUSDT": case "SOLUSDT": case "XRPUSDT": case "BNBUSDT":
                        return "MAJOR";
                }
            }
            string source = pos?.EntrySignalSource ?? string.Empty;
            if (string.IsNullOrEmpty(source)) return "GENERIC";
            string s = source.ToUpperInvariant();
            if (s.StartsWith("SPIKE") || s.Equals("TICK_SURGE")) return "SPIKE";
            if (s.Contains("SQUEEZE")) return "SQUEEZE";
            if (s.Contains("BB_WALK") || s.Contains("BBWALK")) return "BB_WALK";
            if (s.StartsWith("PUMP_") || s == "PUMP" || s.Contains("PUMPSCAN")) return "PUMP";
            return "GENERIC";
        }

        // [v5.21.7] IsEntryAllowed 디바운스 — 동일 (symbol, source) 5초 캐시
        //   원인: BTC/ETH/SOL/XRP × MAJOR_ANALYZE 가 매 초 호출되어 게이트 검증 + 로그 폭주 → CPU 1코어 99% 점유
        //   해결: 결과(allowed/blocked) 5초 캐시. ALLOW도 캐시(차단 후 즉시 진입 시도 방지 — 5초 내 중복 진입 차단)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ts, bool allowed, string reason)> _entryGateCache = new();
        private static readonly TimeSpan EntryGateCacheTtl = TimeSpan.FromSeconds(5);

        private bool IsEntryAllowed(string symbol, string source, out string blockReason)
        {
            string cacheKey = $"{symbol}|{source}";
            if (_entryGateCache.TryGetValue(cacheKey, out var cached) &&
                (DateTime.UtcNow - cached.ts) < EntryGateCacheTtl)
            {
                blockReason = cached.reason;
                return cached.allowed;
            }
            bool result = IsEntryAllowedCore(symbol, source, out blockReason);
            _entryGateCache[cacheKey] = (DateTime.UtcNow, result, blockReason);
            return result;
        }

        private bool IsEntryAllowedCore(string symbol, string source, out string blockReason)
        {
            blockReason = string.Empty;
            // [v5.20.3] 가드 호출 자체를 명시적 로그 → 우회 여부 즉시 추적
            OnStatusLog?.Invoke($"🚦 [GATE-CHECK] {symbol} {source} 가드 진입");

            // [v5.21.0] 트리거 카테고리 분류 — 30일 백테스트 기반 차별 가드 적용
            //   --logic-30d 결과: SPIKE 모든 조합 적자, MAJOR/SQUEEZE/BB_WALK 가드 없을 때 흑자 최대
            // [v5.22.12] PUMP 분류 사이드이펙트 수정 — 명시적 PUMP_* source 만 PUMP 분류
            //   v5.22.5 "default → PUMP" 가 ENGINE_151 / ETA_TRIGGER / ElliottWave3Wave / FORECAST_FALLBACK
            //   까지 PUMP 로 떨어뜨려서 12+시간 진입 0건 → GENERIC 카테고리 신설 (PUMP 차단 면제)
            string srcU = (source ?? "").ToUpperInvariant();
            string entryCat;
            // [v5.22.25] 메이저 심볼은 source 무관 MAJOR 강제 — MaxMajorSlots 회피 버그 fix
            //   v5.22.24 까지: BTC/ETH/SOL/XRP 가 BB_SQUEEZE/ENGINE_151 source 로 들어오면 entryCat=SQUEEZE/GENERIC
            //   → MaxSqueezeSlots(3)/MaxGenericSlots(3) 만 체크하고 MaxMajorSlots(1) 우회 → 사용자 BTC+SOL 동시진입 사례
            //   해결: 메이저 심볼은 항상 MAJOR 카테고리 (활성카운트의 ResolveActivePositionCategory 와 일관)
            if (!string.IsNullOrEmpty(symbol) &&
                (symbol == "BTCUSDT" || symbol == "ETHUSDT" || symbol == "SOLUSDT" || symbol == "XRPUSDT" || symbol == "BNBUSDT"))
            {
                entryCat = "MAJOR";
            }
            else if (srcU.Contains("TICK_SURGE") || srcU.Contains("SPIKE")) entryCat = "SPIKE";
            else if (srcU.Contains("SQUEEZE")) entryCat = "SQUEEZE";
            else if (srcU.Contains("BB_WALK") || srcU.Contains("BBWALK")) entryCat = "BB_WALK";
            else if (srcU.Contains("MAJOR")) entryCat = "MAJOR";
            else if (srcU.StartsWith("PUMP_") || srcU == "PUMP" || srcU.Contains("PUMP_TRADE") || srcU.Contains("PUMP_WATCH") || srcU.Contains("PUMPSCAN"))
                entryCat = "PUMP";  // 명시적 PUMP source 만
            else entryCat = "GENERIC";  // ENGINE_151 / ETA_TRIGGER / ElliottWave3Wave / FORECAST_FALLBACK / ROUTE:* 등

            // [v5.21.0] SPIKE 카테고리 전면 차단 — 30일 검증 모든 조합에서 적자
            //   사용자 -$130/시간 손실 주범 (TACUSDT 9건 동시 진입 12초 손절 사례)
            if (entryCat == "SPIKE")
            {
                blockReason = "SPIKE_DISABLED:30d_loss_proven";
                OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (30일 검증 SPIKE 모든 TP/SL 조합 적자, 차단)");
                return false;
            }

            // [v5.22.5] PUMP 카테고리 전면 차단 — 360일 백테스트 -$401 적자 입증
            //   180일 -$406, 360일 -$401, 90일 +$128 (작은 우연성 흑자)
            //   MAJOR/SQUEEZE 360일 +$355K 대비 PUMP는 손해만 가져옴
            // [v5.22.12] 명시적 PUMP_* source 만 차단 (GENERIC 은 면제 — 일반 가드만 통과 시 진입)
            if (entryCat == "PUMP")
            {
                blockReason = "PUMP_DISABLED:360d_backtest_loss";
                OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (360일 백테스트 -$401, MAJOR/SQUEEZE 위주 진입)");
                return false;
            }

            // [v5.19.8] _settings 자체가 null = 봇 부팅 중 / 설정 미로드 → 모든 진입 차단
            //   v5.19.3은 메이저만 차단했으나, 일반 진입도 leverage/marginUsdt 등 설정값 없으면 위험
            if (_settings == null)
            {
                blockReason = "SETTINGS_NOT_LOADED";
                OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (봇 부팅 중 또는 설정 미로드)");
                return false;
            }

            // [v5.22.24] 카테고리별 슬롯 제한 — MaxXxxSlots 초과 시 진입 차단
            //   분류는 이미 위에서 entryCat 결정. 활성포지션의 카테고리는 TradeHistory.Category 와 동일 ResolveTradeCategory 적용.
            // [v5.22.34] 슬롯 값을 MainWindow.CurrentGeneralSettings 에서 매 호출마다 최신 값 읽기
            //   원인: _settings 는 봇 시작 시 1회만 로드된 캐시 → UI 에서 변경 후 저장해도 봇 재시작 전엔 옛값 사용
            //   해결: CurrentGeneralSettings (UI 저장 시 ApplyGeneralSettings 로 즉시 갱신) 우선 사용 → fallback _settings
            var liveSettings = MainWindow.CurrentGeneralSettings ?? _settings;
            int catMax = entryCat switch
            {
                "MAJOR"   => liveSettings.MaxMajorSlots,
                "SQUEEZE" => liveSettings.MaxSqueezeSlots,
                "BB_WALK" => liveSettings.MaxBbWalkSlots,
                "GENERIC" => liveSettings.MaxGenericSlots,
                "PUMP"    => liveSettings.MaxPumpSlots,
                _ => int.MaxValue
            };
            if (catMax > 0 && catMax < int.MaxValue)
            {
                int activeInCat = 0;
                lock (_posLock)
                {
                    foreach (var kv in _activePositions)
                    {
                        var posCat = ResolveActivePositionCategory(kv.Value, kv.Key);
                        if (string.Equals(posCat, entryCat, StringComparison.OrdinalIgnoreCase))
                            activeInCat++;
                    }
                }
                // [v5.22.34] 슬롯 진단 로그 — 매 SLOT 체크마다 사용 중인 설정값 출력
                //   사용자가 UI 에서 보는 값과 봇이 사용하는 값 불일치 (UI 1 vs 봇 4) 추적
                if (activeInCat >= catMax)
                {
                    blockReason = $"SLOT_FULL:{entryCat}={activeInCat}/{catMax}";
                    OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (live={liveSettings.MaxMajorSlots}/{liveSettings.MaxPumpSlots}/{liveSettings.MaxSqueezeSlots}/{liveSettings.MaxBbWalkSlots}/{liveSettings.MaxGenericSlots})");
                    return false;
                }
            }

            // [v5.22.24] 진입풀 일원화 — UI 그리드 (메이저4 + 알트8) 외 심볼 차단.
            //   활성포지션은 면제 (TP/SL 보호 위해 풀 외라도 분석 통과).
            if (_activeTrackingPool.Count > 0 && !_activeTrackingPool.ContainsKey(symbol))
            {
                bool hasActivePos;
                lock (_posLock) { hasActivePos = _activePositions.ContainsKey(symbol); }
                if (!hasActivePos)
                {
                    blockReason = "NOT_IN_TRACKING_POOL";
                    OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (실시간 시장 신호 12개 외)");
                    return false;
                }
            }

            // [v5.19.5] 수동 청산 cooldown — 사용자가 청산한 심볼은 30분간 재진입 차단
            if (_manualCloseCooldown.TryGetValue(symbol, out var closedAt))
            {
                var elapsed = DateTime.UtcNow - closedAt;
                if (elapsed < ManualCloseCooldown)
                {
                    blockReason = $"MANUAL_CLOSE_COOLDOWN:{(ManualCloseCooldown - elapsed).TotalMinutes:F0}m";
                    OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason}");
                    return false;
                }
                _manualCloseCooldown.TryRemove(symbol, out _);
            }

            // 메이저 코인 진입 비활성화 (UI: chkEnableMajorTrading)
            // [v5.19.3] _settings null 시 안전 차단 — 설정 미로드 상태에서 메이저 진입 방지
            if (MajorSymbols.Contains(symbol))
            {
                bool? majorAllowed = _settings?.EnableMajorTrading;
                if (majorAllowed != true)   // null OR false → 차단
                {
                    blockReason = (_settings == null)
                        ? "MAJOR_BLOCKED:settings_not_loaded"
                        : "MAJOR_DISABLED";
                    OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} | EnableMajorTrading={majorAllowed?.ToString() ?? "null"}");
                    return false;
                }
            }

            // [v5.19.5] M15 고점 추격 + 횡보 분배 가드 — API3USDT/CTSI/CELR 사례 근본 차단
            //   API3USDT: 수직상승 후 횡보 → BB 상단 진입 → 폭락 90%+
            //   규칙 1: M15 30봉 range 위치 ≥ 85% AND 30봉 저점→현재가 ≥ 3% → 차단
            //   규칙 2: 직전 30봉 +5% 이상 + 직전 5봉 변동폭 < 0.8% → 횡보 분배 차단
            //   [v5.22.16] 멀티TF WebSocket 캐시 폐기 → sync REST throttle 캐시 (없으면 가드 skip + 백그라운드 fetch)
            try
            {
                var k15 = GetMultiTfKlinesCachedOrRefresh(
                    symbol, KlineInterval.FifteenMinutes, 30);
                if (k15 != null && k15.Count >= 30)
                {
                    decimal minLow = k15.Min(b => b.LowPrice);
                    decimal maxHigh = k15.Max(b => b.HighPrice);
                    decimal latestClose = k15[^1].ClosePrice;

                    decimal posPct = maxHigh > minLow
                        ? (latestClose - minLow) / (maxHigh - minLow) * 100m
                        : 50m;
                    decimal riseFromLowPct = minLow > 0m
                        ? (latestClose - minLow) / minLow * 100m
                        : 0m;

                    // 규칙 1: 고점 추격 — [v5.19.6] 임계 완화 (90% AND 5%) — 학습된 BB Walk 라이딩 통과 가능
                    //   사용자: "상승 vs 하락 구분이 불가능한가? 너무 빡세면 BB Walk 정상 진입도 차단됨"
                    //   완화: 위치 85→90, 상승 3%→5% (단, 둘 다 충족 시에만 차단)
                    if (posPct >= 90m && riseFromLowPct >= 5m)
                    {
                        blockReason = $"HIGH_TOP_CHASING:pos={posPct:F1}%_rise={riseFromLowPct:F2}%";
                        OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (M15 30봉 위치≥90% AND 상승≥5%)");
                        return false;
                    }

                    // 규칙 2: 수직상승 후 횡보 분배 (직전 5봉 high-low/avg < 0.8%)
                    if (riseFromLowPct >= 5m)
                    {
                        var last5 = k15.TakeLast(5).ToList();
                        decimal hi5 = last5.Max(b => b.HighPrice);
                        decimal lo5 = last5.Min(b => b.LowPrice);
                        decimal avg5 = last5.Average(b => b.ClosePrice);
                        decimal range5Pct = avg5 > 0m ? (hi5 - lo5) / avg5 * 100m : 0m;
                        decimal peakDistPct = maxHigh > 0m ? (maxHigh - latestClose) / maxHigh * 100m : 0m;
                        if (range5Pct < 0.8m && peakDistPct < 1.0m)
                        {
                            blockReason = $"TOP_DISTRIBUTION:rise={riseFromLowPct:F1}%_range5={range5Pct:F2}%_peakDist={peakDistPct:F2}%";
                            OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (수직상승 후 횡보 = 분배 종료)");
                            return false;
                        }
                    }

                    // [v5.19.10] 규칙 3: 일반 횡보 박스 차단 — "상승중인줄 알고 진입했더니 횡보" 케이스
                    //   M15 직전 20봉 range/avg < 0.5% AND BB 폭 < 1% → 좁은 박스 (변동성 죽음)
                    //   사용자: 진입 후 박스에서 무한정 홀딩 → 손실/기회비용
                    {
                        var last20 = k15.TakeLast(20).ToList();
                        if (last20.Count >= 20)
                        {
                            decimal hi20 = last20.Max(b => b.HighPrice);
                            decimal lo20 = last20.Min(b => b.LowPrice);
                            decimal avg20 = last20.Average(b => b.ClosePrice);
                            decimal range20Pct = avg20 > 0m ? (hi20 - lo20) / avg20 * 100m : 0m;

                            // BB(20,2) std 계산
                            var closes = last20.Select(b => (double)b.ClosePrice).ToArray();
                            double sma = closes.Average();
                            double variance = closes.Select(c => (c - sma) * (c - sma)).Average();
                            double sd = Math.Sqrt(variance);
                            double bbWidthPct = sma > 0 ? (sd * 4.0) / sma * 100.0 : 0;  // upper-lower = 4σ

                            if (range20Pct < 0.5m && bbWidthPct < 1.0)
                            {
                                blockReason = $"SIDEWAYS_BOX:range20={range20Pct:F2}%_bbw={bbWidthPct:F2}%";
                                OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (좁은 박스 — 진입 후 횡보 위험)");
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception exGuard)
            {
                OnStatusLog?.Invoke($"⚠️ [GATE-고점가드] {symbol} 체크 실패 (무시): {exGuard.Message}");
            }

            // [v5.10.88 Option A] BTC 1H 하락추세 시 알트 LONG 진입 차단
            //   진단: 04-21 23시~04-22 7시 (하락장 8시간) 122건 20% 승률 -$45
            //   근거: 봇이 SHORT 안 함 → 하락장 LONG = 데드캣 잡고 -30~50% ROE
            //   규칙: BTC 1H 가격변화 ≤ -0.8% AND 알트 심볼 → 진입 차단
            //        메이저(BTC/ETH/SOL/XRP)는 차단 제외 (본인 추세 판단)
            if (!MajorSymbols.Contains(symbol) && _marketDataManager != null)
            {
                try
                {
                    if (_marketDataManager.KlineCache.TryGetValue("BTCUSDT", out var btcKlines) && btcKlines.Count >= 30)
                    {
                        // 5분봉 12개 (1시간) 가격 변화 계산
                        List<Binance.Net.Interfaces.IBinanceKline> recent;
                        lock (btcKlines) { recent = btcKlines.TakeLast(13).ToList(); }
                        if (recent.Count >= 12)
                        {
                            decimal btc1hAgo = recent[0].ClosePrice;
                            decimal btcNow = recent[^1].ClosePrice;
                            if (btc1hAgo > 0)
                            {
                                decimal btc1hChangePct = (btcNow - btc1hAgo) / btc1hAgo * 100m;
                                if (btc1hChangePct <= -0.8m)
                                {
                                    blockReason = $"BTC_1H_DOWNTREND ({btc1hChangePct:F2}%)";
                                    return false;
                                }
                            }
                        }
                    }
                }
                catch { /* BTC 조회 실패 시 차단하지 않음 (진입 누락 방지) */ }
            }

            // [v5.22.3] MODEL_ZIP_MISSING 가드 비활성화 — AI 시스템 폐기 (2026-04-28)
            //   v5.22.0/22.1/22.2 에서 AI 게이트 + 학습 트리거 모두 제거했지만
            //   ModelHealthMonitor.AnyMissing 가드가 여기 살아있어서 zip 파일 미존재 시 진입 차단 중
            //   → Models 폴더 비어있음 → MODEL_ZIP_MISSING 차단 → 진입 0건 (사용자 보고)
            //   해결: AI 폐기 결정 (2026-04-28) 에 따라 zip 검증 자체 우회

            // [v5.21.0] EMA20↑ + RSI<70 게이트 — PUMP 카테고리에만 적용
            //   30일 검증: PUMP는 v5.20.8 가드 적용 시 -$675 → +$69 (흑자 전환)
            //              MAJOR/SQUEEZE/BB_WALK는 가드 없는 게 더 좋음 (가드 적용 시 PnL 30~50% 감소)
            if (entryCat == "PUMP" && _marketDataManager != null && _marketDataManager.KlineCache.TryGetValue(symbol, out var entryKlines) && entryKlines.Count >= 25)
            {
                List<Binance.Net.Interfaces.IBinanceKline> snap;
                lock (entryKlines) snap = entryKlines.ToList();

                // EMA20 5봉 상승 (유일 양성 가드, 진단 +1.32%p edge)
                decimal e1 = CalcEma20Local(snap, snap.Count - 1);
                decimal e0 = CalcEma20Local(snap, snap.Count - 6);
                if (e1 <= e0)
                {
                    blockReason = "EMA20_NOT_RISING";
                    OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason}");
                    return false;
                }

                // [v5.21.1] PUMP RSI 임계값 70 → 65 강화 (PUMP 90일 전수검증 결과)
                //   v5.20.8 (RSI<70): 113건 WR 76.11% PnL -$40.40 ← 적자
                //   v5.21.1 (RSI<65): 49건 WR 79.59% PnL +$50.80 ← 흑자 전환
                //   진입 -57% 감소하지만 PnL +$91 개선
                if (snap.Count >= 15)
                {
                    double g = 0, l = 0;
                    int last = snap.Count - 1;
                    for (int q = last - 13; q <= last; q++)
                    {
                        double d = (double)(snap[q].ClosePrice - snap[q - 1].ClosePrice);
                        if (d > 0) g += d; else l -= d;
                    }
                    double avgG = g / 14.0, avgL = l / 14.0;
                    double rsi = avgL < 1e-12 ? 100.0 : 100.0 - (100.0 / (1.0 + avgG / avgL));
                    if (rsi >= 65.0)
                    {
                        blockReason = $"RSI65_OVERHEATED:rsi={rsi:F1}";
                        OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (PUMP 과열 — 90일 검증 RSI<65 흑자 전환)");
                        return false;
                    }
                }
            }

            // [v5.20.7] 알트 RSI 역추세 진입 차단 — diag-validator-direct.ps1 결과 근거
            //   30심볼 RSI(14) baseline win-rate = 49.01% (랜덤보다 못함)
            //   BOTTOM 10 알트 (DYDX/APE/GALA/ESPORTS/API3/W/GRIFFAIN/SOON/AVAX/TRX): 39~47%
            //   결론: 알트는 RSI<30 LONG (떨어지는 칼날) / RSI>70 SHORT (눌림 안 옴) 모두 손해
            //   봇은 LONG 전용 → "RSI<30 알트 LONG" 차단이 핵심
            //   메이저(BTC/ETH/SOL/XRP)는 baseline 53~55% → 차단 제외
            if (!MajorSymbols.Contains(symbol) && _marketDataManager != null)
            {
                try
                {
                    if (_marketDataManager.KlineCache.TryGetValue(symbol, out var altKlines) && altKlines.Count >= 15)
                    {
                        List<Binance.Net.Interfaces.IBinanceKline> last15;
                        lock (altKlines) { last15 = altKlines.TakeLast(15).ToList(); }
                        if (last15.Count >= 15)
                        {
                            double gain = 0, loss = 0;
                            for (int i = 1; i < 15; i++)
                            {
                                double diff = (double)(last15[i].ClosePrice - last15[i - 1].ClosePrice);
                                if (diff > 0) gain += diff; else loss -= diff;
                            }
                            double avgG = gain / 14.0, avgL = loss / 14.0;
                            double rsi = avgL < 1e-12 ? 100.0 : 100.0 - (100.0 / (1.0 + avgG / avgL));
                            if (rsi < 30.0)
                            {
                                blockReason = $"ALT_RSI_FALLING_KNIFE:rsi={rsi:F1}";
                                OnStatusLog?.Invoke($"⛔ [GATE] {symbol} {source} 차단 | reason={blockReason} (알트 RSI<30 = 떨어지는 칼날, baseline 39~47% 손실)");
                                return false;
                            }
                        }
                    }
                }
                catch { /* 가드 실패 시 차단하지 않음 */ }
            }

            // [v5.22.1] SIMPLE-AI KNN 게이트 제거 — 백테스트 검증 결과 손실
            //   180일 백테스트: 가드만 +$47,856 vs 가드+KNN +$26,482 → AI 게이트가 -$21,374 손해
            //   원인: KNN 4봉 후 라벨 vs 진입 12-24봉 TP/SL 시간 지평 불일치, regime change 미적응
            //   결정 (2026-04-28): "AI만 사용" 규칙 폐기, 차트 백테스트 검증된 가드만으로 진입
            //   AI 코드는 차후 재검토 (LorentzianV2Service / AIDoubleCheckEntryGate stub 유지)

            // [v5.20.3] 모든 가드 통과 시 명시 로그 → "가드 통과인지 우회인지" 즉시 식별 가능
            OnStatusLog?.Invoke($"✅ [GATE-PASS] {symbol} {source} 모든 가드 통과");
            return true;
        }

        /// <summary>[v5.20.7 B-plan] EMA20 sliding 계산 — IsEntryAllowed 트리거 전용</summary>
        private static decimal CalcEma20Local(System.Collections.Generic.List<Binance.Net.Interfaces.IBinanceKline> kl, int idx)
        {
            const int period = 20;
            decimal k = 2m / (period + 1);
            int from = System.Math.Max(0, idx - period * 2);
            decimal ema = kl[from].ClosePrice;
            for (int j = from + 1; j <= idx; j++) ema = kl[j].ClosePrice * k + ema * (1m - k);
            return ema;
        }

        /// <summary>
        /// 신규 진입(reduceOnly=false) 단일 진입점. 모든 PUMP/SPIKE/MAJOR/MANUAL 경로는 이 메서드 호출 필수.
        /// _exchangeService.PlaceMarketOrderAsync/PlaceOrderAsync 직접 호출 금지 (게이트 우회됨).
        /// </summary>
        private async Task<(bool Success, decimal FilledQuantity, decimal AveragePrice)> PlaceEntryOrderAsync(
            string symbol, string side, decimal quantity, string source, CancellationToken token)
        {
            if (!IsEntryAllowed(symbol, source, out string reason))
            {
                OnLiveLog?.Invoke($"⛔ [ENTRY_GATE][{source}] {symbol} 차단 ({reason})");
                return (false, 0m, 0m);
            }

            // [v5.19.6] 가용잔고 가드 — Binance -2027 InsufficientMargin 에러 사전 차단
            //   필요 마진 = quantity × 현재가 / 레버리지 × 1.05 (수수료 + 버퍼)
            //   가용 잔고 < 필요 마진 → 차단 (재시도/주문 폭주 방지)
            try
            {
                decimal nowPriceForMargin = 0m;
                if (_marketDataManager?.TickerCache != null && _marketDataManager.TickerCache.TryGetValue(symbol, out var tMargin))
                    nowPriceForMargin = tMargin.LastPrice;
                if (nowPriceForMargin <= 0m)
                {
                    try { nowPriceForMargin = await _exchangeService.GetPriceAsync(symbol, token); } catch { }
                }
                if (nowPriceForMargin > 0m && quantity > 0m)
                {
                    int lev = _settings?.DefaultLeverage > 0 ? _settings.DefaultLeverage : 10;
                    decimal notional = quantity * nowPriceForMargin;
                    decimal requiredMargin = notional / lev * 1.05m;
                    decimal available = await _exchangeService.GetAvailableBalanceAsync("USDT", token);
                    if (available > 0m && available < requiredMargin)
                    {
                        OnLiveLog?.Invoke($"⛔ [ENTRY_GATE][{source}] {symbol} 차단 (INSUFFICIENT_MARGIN) — 필요={requiredMargin:F2} 가용={available:F2} (qty={quantity} px={nowPriceForMargin:F8} lev={lev}x)");
                        OnStatusLog?.Invoke($"⛔ [잔고부족] {symbol} 진입 취소 — 필요 ${requiredMargin:F2} > 가용 ${available:F2}");
                        return (false, 0m, 0m);
                    }
                }
            }
            catch (Exception exMargin)
            {
                OnStatusLog?.Invoke($"⚠️ [잔고가드] {symbol} 체크 실패 (무시): {exMargin.Message}");
            }

            // ═══════════════════════════════════════════════════════════════
            // [v5.10.97 ROOT FIX] Market → LIMIT + chasing 차단
            //   사용자 지적: "왜 진입하면 모두 마이너스부터 시작" (UBUSDT 가격+0.05% ROE-13%)
            //   원인: Market 주문 슬리피지 + 신호→주문 사이 가격 추가 상승 + 펌프 꼭대기 진입
            //   수정 A: LIMIT @ 현재가 -0.05% (LONG) / +0.05% (SHORT) → 5초 timeout
            //   수정 B: 1초 대기 후 가격 재확인 → 0.3% 이상 상승(LONG) 시 chasing 차단
            // ═══════════════════════════════════════════════════════════════
            try
            {
                bool isLong = string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "LONG", StringComparison.OrdinalIgnoreCase);

                // 신호 발생 시점 가격
                decimal signalPrice = 0m;
                if (_marketDataManager?.TickerCache != null && _marketDataManager.TickerCache.TryGetValue(symbol, out var t1))
                    signalPrice = t1.LastPrice;
                if (signalPrice <= 0)
                {
                    try { signalPrice = await _exchangeService.GetPriceAsync(symbol, token); } catch { }
                }

                // [수정 B] 1초 대기 + 가격 재확인 (chasing 차단)
                if (signalPrice > 0)
                {
                    await Task.Delay(1000, token);
                    decimal nowPrice = signalPrice;
                    if (_marketDataManager?.TickerCache != null && _marketDataManager.TickerCache.TryGetValue(symbol, out var t2))
                        nowPrice = t2.LastPrice;
                    if (nowPrice > 0)
                    {
                        decimal moveAbsPct = Math.Abs((nowPrice - signalPrice) / signalPrice * 100m);
                        bool chasing = isLong ? (nowPrice > signalPrice * 1.003m) : (nowPrice < signalPrice * 0.997m);
                        if (chasing)
                        {
                            OnLiveLog?.Invoke($"⛔ [CHASING_BLOCK][{source}] {symbol} 신호가 {signalPrice:F8} → 1초후 {nowPrice:F8} (이동 {moveAbsPct:F2}% ≥ 0.3%) 진입 취소");
                            return (false, 0m, 0m);
                        }
                        signalPrice = nowPrice; // 1초 후 가격 사용
                    }
                }

                MarkBotEntryInProgress(symbol);

                // [수정 A] LIMIT 주문 (LONG: -0.05%, SHORT: +0.05% 더 유리한 가격)
                if (signalPrice > 0)
                {
                    decimal limitPx = isLong ? signalPrice * 0.9995m : signalPrice * 1.0005m;
                    var (limOk, orderId) = await _exchangeService.PlaceLimitOrderAsync(symbol, side, quantity, limitPx, token);
                    if (limOk && !string.IsNullOrEmpty(orderId))
                    {
                        // 5초 동안 체결 확인
                        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                        while (DateTime.UtcNow < deadline)
                        {
                            await Task.Delay(500, token);
                            try
                            {
                                var status = await _exchangeService.GetOrderStatusAsync(symbol, orderId, token);
                                if (status.Filled && status.FilledQuantity > 0)
                                {
                                    OnLiveLog?.Invoke($"✅ [LIMIT_ENTRY][{source}] {symbol} 체결 @ {status.AveragePrice:F8} qty={status.FilledQuantity}");
                                    return (true, status.FilledQuantity, status.AveragePrice);
                                }
                            }
                            catch { }
                        }
                        // 5초 내 미체결 → 취소
                        try { await _exchangeService.CancelOrderAsync(symbol, orderId, token); } catch { }
                        OnLiveLog?.Invoke($"⏱️ [LIMIT_TIMEOUT][{source}] {symbol} LIMIT @ {limitPx:F8} 5초 미체결 → 취소 (chasing 회피)");
                        return (false, 0m, 0m);
                    }
                    OnLiveLog?.Invoke($"⚠️ [LIMIT_FALLBACK][{source}] {symbol} LIMIT 주문 실패 → MARKET 폴백");
                }

                // signalPrice 0이거나 LIMIT 실패 시 MARKET fallback
                return await _exchangeService.PlaceMarketOrderAsync(symbol, side, quantity, token);
            }
            catch (Exception ex)
            {
                OnLiveLog?.Invoke($"❌ [PLACE_ENTRY_EX][{source}] {symbol} {ex.Message} → MARKET 폴백");
                return await _exchangeService.PlaceMarketOrderAsync(symbol, side, quantity, token);
            }
        }

        /// <summary>
        /// [v5.10.83 HOTFIX] 진입 후 SL/TP/Trailing 보호 주문 일괄 등록.
        /// [v5.10.86 Regime-Aware] 진입 시점 시장 regime 분류 → Trending/Sideways/Volatile별 SL/TP/Trail 거리 차별화.
        ///   - Trending(급등장): wide SL/TP/Trail (큰 익절 노림) — 사용자 의도 유지
        ///   - Sideways(횡보장): tight TP/Trail (노이즈 회피, 빠른 익절) — 횡보 손실 차단
        ///   - Volatile: 중간 (탔다 떨어짐 대응)
        ///   ※ 사용자 요구 "급등장과 횡보장 차별화" 직접 반영. 하드코딩 X — ML regime 분류로 프로파일 선택.
        /// </summary>
        private async Task RegisterProtectionOrdersAsync(
            string symbol, bool isLong, decimal filledQty, decimal entryPrice, int leverage, string source, CancellationToken token)
        {
            try
            {
                if (filledQty <= 0 || entryPrice <= 0 || leverage <= 0)
                {
                    OnStatusLog?.Invoke($"⚠️ [PROTECT][{source}] {symbol} 보호주문 스킵 (qty/price/lev=0)");
                    return;
                }

                bool isMajor = MajorSymbols.Contains(symbol);
                string closeSide = isLong ? "SELL" : "BUY";

                // [AI 제거] ML regime 분류 제거 — 기본 배수 사용
                decimal slMul = 1.0m, tpMul = 1.0m, trailMul = 1.0m, tpRatioMul = 1.0m;

                decimal slRoePctBase    = isMajor ? (_settings?.MajorStopLossRoe   ?? 60m) : (_settings?.PumpStopLossRoe   ?? 40m);
                decimal tpRoePctBase    = isMajor ? (_settings?.MajorTp1Roe        ?? 20m) : (_settings?.PumpTp1Roe        ?? 20m);
                decimal trailGapPctBase = isMajor ? (_settings?.MajorTrailingGapRoe?? 5m)  : (_settings?.PumpTrailingGapRoe?? 20m);
                decimal tpRatioPctBase  = isMajor ? 30m : (_settings?.PumpFirstTakeProfitRatioPct ?? 40m);

                decimal slRoePct    = slRoePctBase    * slMul;
                decimal tpRoePct    = tpRoePctBase    * tpMul;
                decimal trailGapPct = trailGapPctBase * trailMul;
                decimal tpRatioPct  = Math.Min(80m, tpRatioPctBase * tpRatioMul);

                OnStatusLog?.Invoke($"🌐 [PROTECT][{source}] {symbol} → SL={slRoePct:F0}% TP={tpRoePct:F0}%(qty {tpRatioPct:F0}%) Trail={trailGapPct:F1}%");

                // ROE → 가격 변환 (priceMove% = roe% / leverage)
                decimal slPriceMovePct = slRoePct / leverage / 100m;
                decimal tpPriceMovePct = tpRoePct / leverage / 100m;

                decimal slPrice = isLong
                    ? entryPrice * (1m - slPriceMovePct)
                    : entryPrice * (1m + slPriceMovePct);
                decimal tpPrice = isLong
                    ? entryPrice * (1m + tpPriceMovePct)
                    : entryPrice * (1m - tpPriceMovePct);

                // SL: 전체 수량
                var (slOk, _) = await _exchangeService.PlaceStopOrderAsync(symbol, closeSide, filledQty, slPrice, token);
                if (slOk)
                    OnStatusLog?.Invoke($"🛡️ [PROTECT][{source}] {symbol} SL 등록 OK @ {slPrice:F8} (ROE -{slRoePct:F0}%)");
                else
                    OnAlert?.Invoke($"❌ [PROTECT][{source}] {symbol} SL 등록 실패 — 무방비 포지션! 수동 SL 설정 필요");

                // TP: 부분 익절 수량
                decimal tpQty = Math.Round(filledQty * tpRatioPct / 100m, 8);
                if (tpQty > 0 && tpPrice > 0)
                {
                    var (tpOk, _) = await _exchangeService.PlaceTakeProfitOrderAsync(symbol, closeSide, tpQty, tpPrice, token);
                    if (tpOk)
                        OnStatusLog?.Invoke($"🎯 [PROTECT][{source}] {symbol} TP 등록 OK @ {tpPrice:F8} qty={tpQty} (ROE +{tpRoePct:F0}%, {tpRatioPct:F0}%)");
                    else
                        OnStatusLog?.Invoke($"⚠️ [PROTECT][{source}] {symbol} TP 등록 실패");
                }

                // Trailing: 잔여 수량
                decimal trailQty = Math.Round(filledQty - tpQty, 8);
                if (trailQty > 0)
                {
                    var (trailOk, _) = await _exchangeService.PlaceTrailingStopOrderAsync(
                        symbol, closeSide, trailQty,
                        callbackRate: Math.Clamp(trailGapPct, 0.1m, 5.0m),
                        activationPrice: tpPrice > 0 ? tpPrice : (decimal?)null,
                        ct: token);
                    if (trailOk)
                        OnStatusLog?.Invoke($"📈 [PROTECT][{source}] {symbol} Trailing 등록 OK callback={trailGapPct:F1}% qty={trailQty}");
                    else
                        OnStatusLog?.Invoke($"⚠️ [PROTECT][{source}] {symbol} Trailing 등록 실패");
                }
            }
            catch (Exception ex)
            {
                OnAlert?.Invoke($"❌ [PROTECT][{source}] {symbol} 보호주문 등록 예외: {ex.Message} — 수동 SL 확인 필요");
                try { await _dbManager.SaveOrderErrorAsync(symbol, isLong ? "BUY" : "SELL", "PROTECT", filledQty, null, ex.Message); } catch { }
            }
        }

        private TradingSettings _settings;

        // [Events] UI와의 통신을 위한 이벤트 정의
        public event Action<string>? OnLiveLog;
        public event Action<string>? OnAlert;
        public event Action<string>? OnStatusLog;
        public event Action<int, int>? OnProgress;
        public event Action<double, double, int>? OnDashboardUpdate; // equity, available, posCount
        public event Action<int, int, int, int>? OnSlotStatusUpdate; // totalCount, maxSlots, majorCount, (unused)
    #pragma warning disable CS0067
        public event Action<bool, string>? OnTelegramStatusUpdate;
    #pragma warning restore CS0067
        public event Action<MultiTimeframeViewModel>? OnSignalUpdate;
        public event Action<string, decimal, double?>? OnTickerUpdate; // symbol, price, pnl
        public event Action<string>? OnSymbolTracking; // Ensure symbol in list
        public event Action<string, string, decimal, decimal>? OnTradeExecuted;
        public event Action<string, bool, decimal>? OnPositionStatusUpdate;
        public event Action<string, bool, string?>? OnCloseIncompleteStatusChanged;
        public event Action<string, string?, string?>? OnExternalSyncStatusChanged;
        public event Action<string, float, float>? OnRLStatsUpdate; // [추가] RL 학습 상태 업데이트
        public event Action<AIPredictionRecord>? OnAIPrediction; // [AI 모니터링] 예측 기록 이벤트
        public event Action? OnTradeHistoryUpdated; // [FIX] 청산 시 TradeHistory 자동 갱신 트리거
        // [AI 제거] OnAiEntryProbUpdate 이벤트 제거 — AIEntryForecastResult 의존
        public event Action<string, float, float, string>? OnWaveAIScoreUpdate; // [WaveAI] symbol, mlScore, tfScore, status
        private bool HasWaveAiScoreSubscribers => OnWaveAIScoreUpdate != null;
        public event Action<string>? OnBlockReasonUpdate; // [Entry Pipeline] 진입 차단 사유 실시간 업데이트
        public event Action<string, decimal>? OnTrailingStopPriceUpdate; // [UI] 트레일링스탑 가격 업데이트
        public event Action<double>? OnVolumeGaugeUpdate; // [Entry Pipeline] 1분봉 볼륨 게이지 (0~100)
        public event Action<decimal>? OnDailyProfitUpdate; // [Entry Pipeline] 오늘 수익 실시간 업데이트
        /// <summary>[동적 트레일링] Exit Score + 반전확률 + 동적손절가 (symbol, exitScore 0~100, reversalProb 0~1, stopPrice)</summary>
        public event Action<string, double, float, decimal>? OnExitScoreUpdate;
        /// <summary>
        /// [상승 에너지] BB 밴드 내 가격 위치 + ML/TF 컨텍스트
        /// (bbPos 0.0~1.0, mlConfidence 0.0~1.0, tfConvergenceDivergence bool)
        /// </summary>
        public event Action<double, float, bool>? OnPriceProgressUpdate;
        /// <summary>[SSA] 시계열 예측 밴드 업데이트 (upperBound, lowerBound, forecastPrice)</summary>
        public event Action<float, float, float>? OnSsaForecastUpdate;
        /// <summary>[v5.22.23] 활성 추적 풀 변경 통지 — MainViewModel 에서 그리드 stale row 자동 제거</summary>
        public event Action<HashSet<string>>? OnActiveTrackingSetChanged;

        /// <summary>
        /// [AI Command Center] 심볼, AI신뢰도(0~1), 방향(LONG/SHORT/NONE), H4/H1/15M 상태 문자열, bullPower(0~100), bearPower(0~100)
        /// </summary>
        public event Action<string, float, string, string, string, string, double, double>? OnAiCommandUpdate;

        /// <summary>
        /// 외부에서 모든 이벤트 구독을 해제합니다 (event 키워드로 인해 내부에서만 초기화 가능)
        /// </summary>
        public void ClearEventSubscriptions()
        {
            OnLiveLog = null;
            OnAlert = null;
            OnStatusLog = null;
            OnProgress = null;
            OnDashboardUpdate = null;
            OnSlotStatusUpdate = null;
            OnTelegramStatusUpdate = null;
            OnSignalUpdate = null;
            OnTickerUpdate = null;
            OnSymbolTracking = null;
            OnTradeExecuted = null;
            OnPositionStatusUpdate = null;
            OnCloseIncompleteStatusChanged = null;
            OnExternalSyncStatusChanged = null;
            OnRLStatsUpdate = null;
            OnAIPrediction = null;
            OnTradeHistoryUpdated = null;
            // [AI 제거] OnAiEntryProbUpdate = null;
            OnWaveAIScoreUpdate = null;
            OnBlockReasonUpdate = null;
            OnTrailingStopPriceUpdate = null;
            OnVolumeGaugeUpdate = null;
            OnDailyProfitUpdate = null;
            OnPriceProgressUpdate = null;
            OnExitScoreUpdate = null;
            OnAiCommandUpdate = null;
        }

        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private DateTime _lastPositionSyncTime = DateTime.MinValue; // [FIX] 마지막 포지션 동기화 시간
        private DateTime _lastProtectionCheckTime = DateTime.MinValue; // [v5.10.62] 활성 포지션 SL 없으면 자동 등록
        private DateTime _lastSuccessfulEntryTime = DateTime.MinValue; // [드라이스펠] 마지막 진입 성공 시각
        private DateTime _lastDroughtScanTime = DateTime.MinValue;     // [드라이스펠] 마지막 진단 스캔 시각
        private static readonly TimeSpan DroughtScanThreshold = TimeSpan.FromMinutes(30);  // 30분 진입 없으면 진단
        private static readonly TimeSpan DroughtScanInterval   = TimeSpan.FromMinutes(10);  // 진단은 10분에 1회까지
        private const int DroughtRecoveryForecastHorizonMinutes = 120; // 2시간 이내 진입 가능 후보 우선
        private const float DroughtRecoveryForecastMinProbability = 0.60f;
        private const string HistoricalAuditSampleSymbol = "XRPUSDT";
        private static readonly DateTime HistoricalAuditStartUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const int HistoricalAuditBatchLimit = 1000;
        private decimal _atrVolatilityBlockRatio = 3.5m; // [AI Hub] 2.0→3.5: 진짜 폭발 구간만 차단
        private float _shortRsiExhaustionFloor = 35f;

        // [AI 제거] OneMinuteExecutionHub / BiDirectionalScenarioEngine 제거

        private int _historicalEntryAuditStarted = 0;

        // [Fail-safe] API 연결 끊김 + 슬리피지 감지 모듈
        private TradingBot.Services.FailSafeGuardService? _failSafeGuard;

        // [TimeOut Probability Entry] 60분 공백 확률 베팅 엔진
        private TimeOutProbabilityEngine? _timeoutProbEngine;
        private DateTime _lastTimeOutProbScanTime = DateTime.MinValue; // 마지막 TimeOut 스캔 시각
        private static readonly TimeSpan TimeOutProbScanThreshold = TimeSpan.FromHours(1);   // 60분 공백 시 가동
        private static readonly TimeSpan TimeOutProbScanInterval  = TimeSpan.FromMinutes(20); // 스캔은 20분에 1회 재시도 제한
        // [v5.22.13] _initialMLNetTrainingTriggered / _manualInitialTrainingRunning 제거
        private TimeSpan _entryWarmupDuration = TimeSpan.FromSeconds(30); // 설정에서 로드
        private DateTime _lastEntryWarmupLogTime = DateTime.MinValue;
        private DateTime _lastAiGateNotReadyTelegramTime = DateTime.MinValue;

        // [병목 해결] RefreshProfitDashboard API 호출 캐싱
        private decimal _cachedUsdtBalance = 0m;
        private decimal _cachedAvailableBalance = 0m;  // [v5.1.8]
        private decimal _cachedUnrealizedPnl = 0m;     // [v5.1.8]
        private DateTime _lastBalanceCacheTime = DateTime.MinValue;
        private const int BALANCE_CACHE_INTERVAL_MS = 5000; // 5초 (사용자 지적: 30초는 진입 마진 계산 stale 문제)

        public TradingEngine()
        {
            _cts = new CancellationTokenSource();

            // [진입 필터 설정 로드]
            LoadEntryFilterSettings();

            // [추가] 로그 버퍼링 (최근 100개 유지)
            this.OnLiveLog += (msg) => AddToLogBuffer($"[LIVE] {msg}");
            this.OnStatusLog += (msg) => AddToLogBuffer($"[STATUS] {msg}");
            this.OnAlert += (msg) => AddToLogBuffer($"[ALERT] {msg}");

            // 선택된 거래소에 따라 API 키 설정
            ExchangeType selectedExchange = (AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(ExchangeType.Binance);
            switch (selectedExchange)
            {
                case ExchangeType.Binance:
                default:
                    apiKey = AppConfig.BinanceApiKey;
                    apiSecret = AppConfig.BinanceApiSecret;
                    break;
            }

            LoggerService.Initialize();

            // 실제 사용 시 API Key와 Secret을 입력하세요. 조회 전용은 Key 없이도 일부 가능합니다.
            // v12.x 초기화 방식 (Binance Client는 바이낸스 전용이므로 바이낸스 키 사용)
            bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
            var testnetKey = AppConfig.Current?.Trading?.TestnetApiKey ?? "";
            var testnetSecret = AppConfig.Current?.Trading?.TestnetApiSecret ?? "";
            bool useTestnet = isSimulation && !string.IsNullOrWhiteSpace(testnetKey) && !string.IsNullOrWhiteSpace(testnetSecret);

            // [FIX] _client도 시뮬레이션 모드면 테스트넷으로 초기화
            // _client는 BinanceExecutionService, PositionMonitorService 등에 전달됨
            _client = new BinanceRestClient(options =>
            {
                if (useTestnet)
                {
                    options.ApiCredentials = new ApiCredentials(testnetKey, testnetSecret);
                    options.Environment = BinanceEnvironment.Testnet;
                }
                else if (!string.IsNullOrWhiteSpace(AppConfig.BinanceApiKey) && !string.IsNullOrWhiteSpace(AppConfig.BinanceApiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                }
            });

            if (isSimulation)
            {
                if (useTestnet)
                {
                    _exchangeService = new BinanceExchangeService(testnetKey, testnetSecret, useTestnet: true);
                    OnStatusLog?.Invoke($"🎮 [Simulation] 바이낸스 테스트넷 연결 — REST+WebSocket 모두 테스트넷");
                }
                else
                {
                    decimal simulationBalance = AppConfig.Current?.Trading?.SimulationInitialBalance ?? 10000m;
                    _exchangeService = new MockExchangeService(simulationBalance);
                    OnStatusLog?.Invoke($"🎮 [Simulation] MockExchange 모드 (테스트넷 키 미설정, 잔고: ${simulationBalance:N2})");
                }
            }
            else
            {
                // 바이낸스 실거래
                _exchangeService = new BinanceExchangeService(AppConfig.BinanceApiKey, AppConfig.BinanceApiSecret);
                
                // [추가] BinanceExchangeService 로그 이벤트 구독
                if (_exchangeService is BinanceExchangeService binanceService)
                {
                    binanceService.OnLog += msg => OnStatusLog?.Invoke(msg);
                    binanceService.OnAlert += msg => OnAlert?.Invoke(msg);
                }
                
                OnStatusLog?.Invoke("🔗 바이낸스 거래소 연결");
            }

            // GeneralSettings 로드: MainWindow에서 초기화된 설정 사용 (DB 우선)
            _settings = MainWindow.CurrentGeneralSettings
                ?? AppConfig.Current?.Trading?.GeneralSettings
                ?? new TradingSettings();
            ApplyMainLoopPerformanceSettings();

            _dailyTargetResetDate = DateTime.Today;

            _symbols = AppConfig.Current?.Trading?.Symbols ?? new List<string>();
            if (_symbols.Count == 0)
            {
                _symbols.AddRange(new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"});
                OnStatusLog?.Invoke($"⚠️ 설정에 심볼이 없어 기본 메이저코인 4개 추가: {string.Join(", ", _symbols)}");
            }
            _soundService = new SoundService();

            // [AI 제거] OneMinuteExecutionHub / BiDirectionalScenarioEngine 초기화 제거
            _macdCrossService = new MacdCrossSignalService(_exchangeService);
            _macdCrossService.OnLog += msg => OnStatusLog?.Invoke(msg);

            _arbitrageStrategy = new ArbitrageStrategy(_client);


            _dbManager = new DbManager(AppConfig.ConnectionString);
            _patternMemoryService = new PatternMemoryService(_dbManager, msg => OnStatusLog?.Invoke(msg));
            _riskManager = new RiskManager();
            _marketDataManager = new MarketDataManager(_client, _symbols);
            _marketHistoryService = new MarketHistoryService(_marketDataManager, AppConfig.ConnectionString);
            // [v4.5.7] 최초 알트 캔들 수집 완료 시 ML 학습 즉시 트리거 (2분 대기 없이)
            // [v5.22.2] AI 학습 시스템 전체 비활성화 — 사용자 결정 (2026-04-28)
            //   "ai 다 제거했는데 왜 학습을 하는거야?" — TrainAllModelsAsync 등 자동 호출 차단
            // _marketHistoryService.OnFirstAltCollectionComplete += () =>
            // {
            //     OnStatusLog?.Invoke("🎯 [ML] 알트 학습 데이터 확보 완료 → 즉시 재학습 시작");
            //     _ = Task.Run(() => TrainAllModelsAsync(_cts?.Token ?? CancellationToken.None));
            // };
            _oiCollector = new OiDataCollector(_client);

            _tickerChannel = Channel.CreateBounded<IBinance24HPrice>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest // 처리 속도가 느리면 오래된 티커 드랍
            });

            // [Agent 2] 채널 초기화
            // 고빈도 구간에서 무한 적재로 메모리 급증/종료가 발생하지 않도록 bounded + DropOldest 적용
            _accountChannel = Channel.CreateBounded<BinanceFuturesStreamAccountUpdate>(new BoundedChannelOptions(2000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _orderChannel = Channel.CreateBounded<BinanceFuturesStreamOrderUpdate>(new BoundedChannelOptions(3000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _positionMonitor = new PositionMonitorService(
                _client,
                _exchangeService,
                _riskManager,
                _marketDataManager,
                _dbManager,
                _activePositions,
                _posLock,
                _blacklistedSymbols,
                _settings,
                settingsProvider: () => MainWindow.CurrentGeneralSettings ?? AppConfig.Current?.Trading?.GeneralSettings ?? _settings
            );

            // [v5.10.18] OrderManager + PositionSyncService 초기화
            _orderManager = new OrderManager(_exchangeService);
            _orderManager.OnLog += msg => OnStatusLog?.Invoke(msg);

            _positionSyncService = new PositionSyncService(
                _exchangeService,
                _activePositions,
                _posLock,
                _orderManager,
                pollIntervalMs: 10_000);
            _positionSyncService.OnLog += msg => OnStatusLog?.Invoke(msg);
            _positionSyncService.OnPositionClosed += HandleSyncedPositionClosed;

            // [AI 제거] regime/exit/profit AI 모델 모두 제거 — MACD cross 만 유지
            _positionMonitor.SetMacdCrossService(_macdCrossService);
            // [AI 제거] StartModelRetrainTimer 제거 (모델 자체 없음)

            _marketDataManager.OnLog += (msg) =>
            {
                OnStatusLog?.Invoke(msg);
                LoggerService.Info(msg);
            };
            // [Agent 2] 이벤트 핸들러를 채널 Writer로 변경 (Non-blocking)
            _marketDataManager.OnAccountUpdate += (data) => _accountChannel.Writer.TryWrite(data);
            _marketDataManager.OnOrderUpdate += (data) => _orderChannel.Writer.TryWrite(data);

            _marketDataManager.OnAllTickerUpdate += HandleAllTickerUpdate;

            // [급변 감지] 설정 반영 + 이벤트 핸들러 연결
            _crashDetector.Enabled = _settings.CrashDetectorEnabled;
            _crashDetector.CrashThresholdPct = _settings.CrashThresholdPct != 0 ? _settings.CrashThresholdPct : -1.5m;
            _crashDetector.PumpThresholdPct = _settings.PumpDetectThresholdPct != 0 ? _settings.PumpDetectThresholdPct : 1.5m;
            _crashDetector.MinCoinCount = _settings.CrashMinCoinCount > 0 ? _settings.CrashMinCoinCount : 2;
            _crashDetector.ReverseEntrySizeRatio = _settings.CrashReverseSizeRatio > 0 ? _settings.CrashReverseSizeRatio : 0.5m;
            _crashDetector.CooldownSeconds = _settings.CrashCooldownSeconds > 0 ? _settings.CrashCooldownSeconds : 120;
            _crashDetector.OnLog += msg =>
            {
                OnAlert?.Invoke(msg);
                LoggerService.Info(msg); // [v4.9.2] CrashDetector 감지 로그 파일 기록
            };
            _crashDetector.OnCrashDetected += (coins, avgDrop) => _ = HandleCrashDetectedAsync(coins, avgDrop);

            // [v4.5.5] 알트 불장 감지기 이벤트 연결
            _altBullDetector.OnLog += msg => OnAlert?.Invoke(msg);
            _altBullDetector.OnAltBullStateChanged += active =>
            {
                if (active)
                    OnAlert?.Invoke("🔥 [알트 불장 모드] 활성화 — 신규 진입 레버리지 50% 하향, 사이즈 70%");
                else
                    OnAlert?.Invoke("💧 [알트 불장 모드] 해제 — 정상 레버리지 복귀");
            };
            _crashDetector.OnPumpDetected += (coins, avgRise) => _ = HandlePumpDetectedAsync(coins, avgRise);
            // [v5.17.0 REDESIGN] SPIKE_FAST (HandleSpikeDetectedAsync) 비활성화
            //   이유: 기존 하드코딩된 3% 감지 → 즉시 시장가 경로는 "꼭대기 진입" 구조.
            //         신규 15-5-1 엔진이 5m strategy + 1m trigger 로 정제된 타이밍 사용
            //   기존 HandleSpikeDetectedAsync 메서드는 호출되지 않지만 코드는 보존 (롤백 대비)
            // _crashDetector.OnSpikeDetected += (symbol, changePct, price) => _ = HandleSpikeDetectedAsync(symbol, changePct, price);

            _crashDetector.OnVolumeSurgeDetected += (symbol, volRatio, price) =>
            {
                _pumpWatchPool[symbol] = (price, DateTime.Now, volRatio);
                string surgeLog = $"🔥 [감시등록] {symbol} vol={volRatio:F1}x price={price:F6} → 상승 확인 대기";
                OnStatusLog?.Invoke(surgeLog);
                LoggerService.Info(surgeLog); // [v4.9.2]
                // [v4.6.0] 감시풀 등록 시 동적 수집 등록 + 즉시 백필
                _marketHistoryService?.RegisterSymbol(symbol);
                _ = _marketHistoryService?.RequestBackfillAsync(symbol, _cts?.Token ?? CancellationToken.None);

                // [v5.22.13] 동적학습등록 제거 — AI 시스템 폐기 (2026-04-29)
            };

            _marketDataManager.OnTickerUpdate += HandleTickerUpdate;
            // [v5.22.23] 활성 추적 풀 통지 — 활성포지션 합집합 후 forward
            _marketDataManager.OnActiveTrackingSetChanged += baseSet =>
            {
                try
                {
                    var combined = new HashSet<string>(baseSet, StringComparer.OrdinalIgnoreCase);
                    lock (_posLock) foreach (var s in _activePositions.Keys) combined.Add(s);
                    OnActiveTrackingSetChanged?.Invoke(combined);
                }
                catch { }
            };

            _positionMonitor.OnLog += msg => OnStatusLog?.Invoke(msg);
            _positionMonitor.OnAlert += msg => OnAlert?.Invoke(msg);
            _positionMonitor.OnTickerUpdate += (s, p, r) =>
            {
                OnTickerUpdate?.Invoke(s, p, r);
            };
            _positionMonitor.OnPositionStatusUpdate += (s, a, p) =>
            {
                OnPositionStatusUpdate?.Invoke(s, a, p);
                // 포지션 종료 시 HybridExitManager 상태 정리
                if (!a) _hybridExitManager?.RemoveState(s);
                if (!a) _runningStandardMonitors.TryRemove(s, out _);
                if (!a) _runningPumpMonitors.TryRemove(s, out _);
            };
            _positionMonitor.OnCloseIncompleteStatusChanged += (s, isIncomplete, detail) =>
            {
                OnCloseIncompleteStatusChanged?.Invoke(s, isIncomplete, detail);
            };
            _positionMonitor.OnTradeHistoryUpdated += () => OnTradeHistoryUpdated?.Invoke();
            // [v3.4.0] 부분청산 완료 → 30초 쿨다운 등록 (팬텀 EXTERNAL_PARTIAL_CLOSE_SYNC 방지)
            _positionMonitor.OnPartialCloseCompleted += symbol =>
            {
                _recentPartialCloseCooldown[symbol] = DateTime.Now.AddSeconds(30);
            };
            // [v5.1.1] 포지션 청산 → 5분 쿨다운 등록 (손절 직후 재진입 방지)
            _positionMonitor.OnPositionClosed += symbol =>
            {
                _stopLossCooldown[symbol] = DateTime.Now.AddMinutes(5);
                OnStatusLog?.Invoke($"⏳ [COOLDOWN] {symbol} 5분 쿨다운 시작 (재진입 방지)");
            };
            _positionMonitor.OnPositionClosedForAiLabel += (symbol, entryTime, entryPrice, isLong, actualProfitPct, closeReason) =>
            {
                _ = HandleAiCloseLabelingAsync(symbol, entryTime, entryPrice, isLong, actualProfitPct, closeReason);

                // [AI 제거] EntryZoneCollector / ProfitRegressor 학습 피드백 코드 통째 제거
            };

            bool transformerRequestedByConfig = AppConfig.Current?.Trading?.TransformerSettings?.Enabled ?? false;
            bool doubleCheckGateEnabled = transformerRequestedByConfig || _enableFifteenMinWaveGate;

            // Transformer 미사용 — AI Gate는 ML.NET 단독 운용

            /* TensorFlow 전환 중 임시 비활성화
            // [Agent 3] 멀티 에이전트 매니저 초기화 (상태 차원: 3 [RSI, MACD, BB], 행동 차원: 3 [Hold, Buy, Sell])
            _multiAgentManager = new MultiAgentManager(3, 3);
            _multiAgentManager.OnAgentTrainingStats += (name, loss, reward) =>
            {
                OnStatusLog?.Invoke($"🧠 RL[{name}] 학습 완료 (Loss: {loss:F4}, Reward: {reward:F4})");
                OnRLStatsUpdate?.Invoke(name, loss, reward);
            };
            */


            // [AI 제거] AIPredictor 초기화 제거

            // [AI 제거] AI 더블체크 게이트 + SSA + Forecaster + PumpSignal/TradeSignal Classifier — 모두 폐기
            OnStatusLog?.Invoke("🛡️ AI 시스템 전체 비활성화 (v5.22.x)");

            // [v5.10.54] OrderLifecycleManager — SL/TP/Trailing 단일 라이프사이클 관리
            _orderLifecycle = new TradingBot.Services.OrderLifecycleManager(_exchangeService);
            _orderLifecycle.OnLog += msg => OnStatusLog?.Invoke(msg);

            // [v5.22.16] EntryScheduler 통째 제거 — Forecaster 의존 클래스
            //   원인: RegisterAsync 호출자 0건 (Forecaster 결과로만 호출됨) → dead instance
            //   효과: 매 ticker 마다 OnPriceTickAsync 호출 부하도 제거
            // [AI 제거] DirectionPredictor / SurvivalModel 로드 제거

            // [v4.2.0] 틱 밀도 모니터 — 급등 시작 신호 + BB 스퀴즈 브레이크아웃
            _tickMonitor.OnLog += msg => OnStatusLog?.Invoke(msg);
            // [v5.17.0 REDESIGN] TICK_SURGE 핸들러 비활성화
            //   이유: 신규 15-5-1 엔진이 정제된 1m 볼륨 spike 시 진입 → 별도 TICK_SURGE 경로 불필요
            // [AI 제거] OnTickSurgeDetected 핸들러 전체 제거 (PumpSignalClassifier 의존)
            _tickMonitor.OnSqueezeBreakout += (symbol, price, bbWidth) =>
            {
                // BB 스퀴즈 브레이크아웃 → AI Gate 거쳐 진입
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // [v5.10.81] 단일 게이트 IsEntryAllowed 사용
                        if (!IsEntryAllowed(symbol, "SQUEEZE_BREAKOUT", out string squeezeBlockReason))
                        {
                            OnStatusLog?.Invoke($"⛔ [스퀴즈돌파→차단] {symbol} ({squeezeBlockReason})");
                            return;
                        }
                        OnStatusLog?.Invoke($"🔥 [스퀴즈돌파→진입] {symbol} BBWidth={bbWidth:F2}%");
                        await ExecuteAutoOrder(symbol, "LONG", price, _cts?.Token ?? CancellationToken.None,
                            "SQUEEZE_BREAKOUT", manualSizeMultiplier: 1.0m);
                    }
                    catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [스퀴즈] {symbol} 진입 실패: {ex.Message}"); }
                });
            };
            // AggTrade WebSocket 시작 (백그라운드)
            _ = StartAggTradeStreamAsync(_cts?.Token ?? CancellationToken.None);
            // [AI 제거] ProfitRegressor / EntryPriceRegressor / EntryZoneCollector / BreakoutClassifier / EntryZoneRegressor 모두 제거

            // [Fail-safe] API 연결 끊김 + 슬리피지 감지 모듈
            _failSafeGuard = new TradingBot.Services.FailSafeGuardService();
            _failSafeGuard.OnLog += msg => OnStatusLog?.Invoke(msg);
            _failSafeGuard.OnAlert += msg => OnAlert?.Invoke(msg);
            _failSafeGuard.OnEmergencyCloseAll += async () =>
            {
                OnAlert?.Invoke("🚨 [FAIL-SAFE] 전체 긴급 청산 시작!");
                List<string> symbols;
                lock (_posLock) { symbols = _activePositions.Keys.ToList(); }
                foreach (var sym in symbols)
                {
                    try { await ClosePositionAsync(sym); }
                    catch (Exception ex) { OnStatusLog?.Invoke($"❌ [FAIL-SAFE] {sym} 긴급 청산 실패: {ex.Message}"); }
                }
            };

            // [AI 제거] DynamicTrailingStopEngine / AIDedicatedWorkerThread / HybridNavigatorSniper / PumpScanStrategy 모두 제거
            LoadActiveAiDecisionIds();
            _majorStrategy = new MajorCoinStrategy(
                _marketDataManager,
                () => MainWindow.CurrentGeneralSettings ?? AppConfig.Current?.Trading?.GeneralSettings);
            _gridStrategy = new GridStrategy(_exchangeService);

            // [TimeOut Probability Entry] 초기화
            try
            {
                _timeoutProbEngine = new TimeOutProbabilityEngine(
                    _exchangeService,
                    _dbManager,
                    msg => OnStatusLog?.Invoke(msg));

                // [v4.9.0] UI TimeOut 위젯 제거 — OnUIUpdated 구독 제거

                // 진입 요청 이벤트 → ExecuteAutoOrder 콜백 연결
                _timeoutProbEngine.OnEntryRequested += async (symbol, direction, signalSrc, sizeMultiplier, ct) =>
                {
                    try
                    {
                        decimal price = 0;
                        if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tick))
                            price = tick.LastPrice;
                        if (price > 0)
                        {
                            await ExecuteAutoOrder(
                                symbol, direction, price, ct,
                                signalSource: signalSrc,
                                manualSizeMultiplier: sizeMultiplier,
                                skipAiGateCheck: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [TimeOut-Prob] 진입 실행 오류: {ex.Message}");
                    }
                };

                OnStatusLog?.Invoke("⏳ [TimeOut-Prob] 확률 베팅 엔진 초기화 완료");
            }
            catch (Exception ex)
            {
                _timeoutProbEngine = null;
                OnStatusLog?.Invoke($"⚠️ [TimeOut-Prob] 엔진 초기화 실패: {ex.Message}");
            }

            // [AI 제거] _pumpStrategy 핸들러 제거. _majorStrategy는 단순화 — Forecaster 의존 제거
            if (_majorStrategy != null)
            {
                _majorStrategy.OnSignalAnalyzed += vm =>
                {
                    try { OnSignalUpdate?.Invoke(vm); }
                    catch (Exception ex) { OnLiveLog?.Invoke($"⚠️ Major 시그널 UI 반영 오류: {ex.Message}"); }
                };
                _majorStrategy.OnTradeSignal += async (symbol, decision, price) =>
                {
                    try
                    {
                        if (!IsEntryAllowed(symbol, "MAJOR_SIGNAL", out string majorBlockReason))
                        {
                            OnLiveLog?.Invoke($"⛔ [MAJOR] {symbol} 신호 무시 ({majorBlockReason})");
                            return;
                        }
                        await ExecuteAutoOrder(symbol, decision, price, _cts.Token, "MAJOR", skipAiGateCheck: false);
                    }
                    catch (Exception ex)
                    {
                        OnLiveLog?.Invoke($"⚠️ Major 주문 처리 오류 [{symbol}]: {ex.Message}");
                    }
                };
                _majorStrategy.OnLog += msg => OnLiveLog?.Invoke(msg);
            }

            // [3파 확정형 전략] 먼저 초기화 (TransformerStrategy에서 사용하기 위해)
            _elliotWave3Strategy = new ElliottWave3WaveStrategy();
            OnStatusLog?.Invoke("🌊 엘리엇 3파 확정형 전략 준비 완료");

            // [15분봉 BB 스퀴즈 돌파 전략]
            _fifteenMinBBSqueezeStrategy = new FifteenMinBBSqueezeBreakoutStrategy();
            OnStatusLog?.Invoke("📉 15분봉 BB 스퀴즈 돌파 전략 준비 완료");

            // [하이브리드 AI 익절/손절 관리]
            _hybridExitManager = new HybridExitManager();
            _hybridExitManager.OnLog += msg => OnStatusLog?.Invoke(msg);
            _hybridExitManager.OnAlert += msg => OnAlert?.Invoke(msg);

            // [Smart Target] 본절 전환 텔레그램
            _hybridExitManager.OnBreakEvenReached += (sym, newSL) =>
            {
                _ = Task.Run(async () =>
                {
                    try { await TelegramService.Instance.SendBreakEvenReachedAsync(sym, newSL); }
                    catch { }
                });
            };

            // [Smart Target] ATR 트레일링 마일스톤 텔레그램
            _hybridExitManager.OnTrailingMilestone += (sym, stop, roe, lbl) =>
            {
                _ = Task.Run(async () =>
                {
                    try { await TelegramService.Instance.SendTrailingMilestoneAsync(sym, stop, roe, lbl); }
                    catch { }
                });
            };

            // [실시간 주문 실행 서비스] ATR 트레일링 스탑 갱신
            _executionService = new BinanceExecutionService(_client);
            _executionService.OnLog += msg => OnStatusLog?.Invoke(msg);
            _executionService.OnAlert += msg => OnAlert?.Invoke(msg);

            // HybridExitManager의 트레일링 스탑 갱신 이벤트를 ExecutionService와 연결
            _hybridExitManager.OnTrailingStopUpdate += async (symbol, newStopPrice) =>
            {
                try
                {
                    await _executionService.UpdateTrailingStopAsync(symbol, newStopPrice);
                }
                catch (Exception ex)
                {
                    OnLiveLog?.Invoke($"⚠️ 트레일링 스탑 갱신 오류 [{symbol}]: {ex.Message}");
                }

                // UI에 트레일링스탑 가격 반영
                OnTrailingStopPriceUpdate?.Invoke(symbol, newStopPrice);
            };

            // [3파 통합] TransformerStrategy에 ElliottWave3WaveStrategy 주입
            // [FIX] Transformer 초기화 실패 시 null 전달하여 안전하게 비활성화


        }

        // 초기 자산 저장 전용 메서드
        private async Task InitializeSeedAsync(CancellationToken token)
        {
            try
            {
                bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                string serviceType = _exchangeService?.GetType().Name ?? "Unknown";
                
                OnStatusLog?.Invoke($"🔍 [InitializeSeed] Mode: {(isSimulation ? "Simulation" : "Real")}, Service: {serviceType}");
                
                if (isSimulation)
                {
                    // 시뮬레이션 모드: 설정된 초기 잔고 사용
                    InitialBalance = AppConfig.Current?.Trading?.SimulationInitialBalance ?? 10000m;
                    OnLiveLog?.Invoke($"🎮 [Simulation] 초기 시드: ${InitialBalance:N2}");
                }
                else
                {
                    // 실거래 모드: 거래소에서 잔고 조회
                    if (_exchangeService == null)
                    {
                        OnStatusLog?.Invoke("⚠️ [InitializeSeed] 거래소 서비스가 초기화되지 않아 초기 잔고 조회를 건너뜁니다.");
                        return;
                    }

                    decimal balance = await _exchangeService.GetBalanceAsync("USDT", token);
                    if (balance > 0)
                    {
                        InitialBalance = balance;
                        // [v5.10.12] 대시보드 캐시 선초기화 — RefreshProfitDashboard 첫 호출 전 $0 표시 방지
                        _cachedUsdtBalance = balance;
                        _cachedAvailableBalance = balance; // 가용은 최소 WalletBalance 이하이므로 보수적 초기값
                        _lastBalanceCacheTime = DateTime.MinValue; // 즉시 첫 API 갱신 유도
                        OnLiveLog?.Invoke($"💰 [Real] 가용 잔고: ${InitialBalance:N2}");
                    }
                    else
                    {
                        // [v5.10.10] 잔고 0 또는 API 실패 → 사용자에게 명확히 표시
                        OnStatusLog?.Invoke("⚠️ [계좌] USDT 잔고 조회 실패 또는 0원 — API 키 권한(선물 읽기) 확인 필요");
                        OnAlert?.Invoke("⚠️ 계좌 잔고 조회 실패 — API 키 권한 확인 필요 (선물 읽기 권한)");
                    }

                    // 순 투입금 조회 (Transfer In - Out)
                    if (_exchangeService is BinanceExchangeService binanceSvc)
                    {
                        decimal netTransfer = await binanceSvc.GetNetTransferAmountAsync(token);
                        if (netTransfer > 0)
                        {
                            NetTransferAmount = netTransfer;
                            OnLiveLog?.Invoke($"💰 [Real] 순 투입금: ${NetTransferAmount:N2} | 가용 잔고: ${InitialBalance:N2}");
                        }
                    }

                    decimal displayBase = NetTransferAmount > 0 ? NetTransferAmount : InitialBalance;
                    if (displayBase > 0)
                    {
                        await NotificationService.Instance.NotifyAsync(
                            $"봇 가동 시작\n투입금: ${NetTransferAmount:N2} USDT\n가용 잔고: ${InitialBalance:N2} USDT",
                            NotificationChannel.Alert);
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ 초기 잔고 조회 실패: {ex.Message}");
            }
        }

        private async Task SendDetailedPeriodicReport(CancellationToken token)
        {
            try
            {
                decimal currentEquity = await GetEstimatedAccountEquityUsdtAsync(token);
                if (currentEquity <= 0)
                {
                    OnStatusLog?.Invoke("⚠️ 정기 보고 스킵: 현재 Equity를 계산할 수 없습니다.");
                    return;
                }

                decimal availableBalance = await _exchangeService.GetBalanceAsync("USDT", token);
                decimal baseline = _periodicReportBaselineEquity > 0
                    ? _periodicReportBaselineEquity
                    : (InitialBalance > 0 ? InitialBalance : currentEquity);

                if (_periodicReportBaselineEquity <= 0)
                    _periodicReportBaselineEquity = baseline;

                decimal pnl = currentEquity - baseline;

                double pnlPercent = baseline > 0
                    ? (double)(pnl / baseline * 100)
                    : 0;

                decimal dailyRealized = _riskManager?.DailyRealizedPnl ?? 0m;
                int activeCount;
                lock (_posLock)
                {
                    activeCount = _activePositions.Count;
                }

                // 가동 시간 계산 (리포트 주기 기준이 아닌 엔진 시작 기준)
                var upTime = DateTime.Now - _engineStartTime;
                string timeStr = $"{(int)upTime.TotalDays}일 {upTime.Hours}시간 {upTime.Minutes}분";

                string body = $"🏦 **현재 추정 자산(Equity)**: `{currentEquity:N2} USDT`\n" +
                              $"💼 **가용 잔고**: `{availableBalance:N2} USDT`\n" +
                              $"🎯 **기준 자산(시작 기준)**: `{baseline:N2} USDT`\n" +
                              $"📈 **총 수익금**: `{pnl:N2} USDT`\n" +
                              $"📊 **수익률**: `{pnlPercent:F2}%`\n" +
                              $"🧾 **금일 실현 손익**: `{dailyRealized:N2} USDT`\n" +
                              $"⏳ **총 가동 시간**: {timeStr}\n" +
                              $"🚀 **현재 운용 중**: {activeCount}개 종목";

                await NotificationService.Instance.NotifyAsync($"📊 [정기 보고]\n{body}", NotificationChannel.Profit);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ 리포트 생성 중 오류: {ex.Message}");
            }
        }

        private void RecordEntryBlockReason(string reasonKey)
        {
            if (string.IsNullOrWhiteSpace(reasonKey))
                reasonKey = "UNKNOWN";

            _entryBlockReasonCounts.AddOrUpdate(reasonKey, 1, (_, current) => current + 1);
            Interlocked.Increment(ref _entryBlockTotalCount);
        }

        private string BuildEntryBlockSummary(int topN = 5, bool resetAfterRead = false)
        {
            long total = Interlocked.Read(ref _entryBlockTotalCount);
            if (total <= 0)
                return "집계 없음";

            int safeTop = Math.Clamp(topN, 1, 10);
            var topReasons = _entryBlockReasonCounts
                .OrderByDescending(kv => kv.Value)
                .Take(safeTop)
                .ToList();

            string breakdown = string.Join(", ",
                topReasons.Select(kv => $"{kv.Key}:{kv.Value}({(kv.Value * 100.0 / total):F0}%)"));

            if (resetAfterRead)
            {
                _entryBlockReasonCounts.Clear();
                Interlocked.Exchange(ref _entryBlockTotalCount, 0);
            }

            return $"total={total} | {breakdown}";
        }

        private string BuildEntryBlockTuneHint(int topN = 3)
        {
            long total = Interlocked.Read(ref _entryBlockTotalCount);
            if (total <= 0)
                return "집계 없음";

            int safeTop = Math.Clamp(topN, 1, 6);
            var topStages = _entryBlockReasonCounts
                .OrderByDescending(kv => kv.Value)
                .Take(safeTop)
                .Select(kv => kv.Key.ToUpperInvariant())
                .ToList();

            var hints = new List<string>();

            foreach (var stage in topStages)
            {
                string? hint = stage switch
                {
                    "AI_GATE" or "AI" => "AI 차단↑: AiScoreThreshold 5~10p 완화 또는 AI 필터 설정 점검",
                    "RR" or "EW_RR" => "RR 차단↑: MinRiskRewardRatio(기본 1.40) 완화 검토",
                    "M15_TREND" or "M15_SLOPE" => "15분 추세 차단↑: 15분 Gate 완화/해제 A-B 테스트",
                    "CANDLE_DELAY" or "CANDLE_CONFIRM" => "캔들확인 대기↑: 확인 규칙 완화 또는 조기진입 조건 점검",
                    "BW_GATE" or "HYBRID_BB" or "CHASE" or "ATR_VOL" or "RSI_EXHAUSTED" or "M1_WICK" => "타이밍 필터 차단↑: BB/ATR/추격 차단 기준 재조정",
                    "SLOT" or "BLACKLIST" => "슬롯/블랙리스트 영향↑: 포지션 회전·슬롯 정책 점검",
                    "SIZE" or "ORDER_SETUP" or "ORDER" => "주문실행 이슈↑: 레버리지·수량·거래소 필터 점검",
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(hint) && !hints.Contains(hint))
                    hints.Add(hint);

                if (hints.Count >= 3)
                    break;
            }

            if (hints.Count == 0)
                return "상위 차단 사유를 세부 로그로 확인해 임계값을 조정하세요.";

            return string.Join(" | ", hints);
        }

        private void EmitEntryBlockSummaryIfAny(bool resetAfterRead = true)
        {
            string summary = BuildEntryBlockSummary(topN: 6, resetAfterRead: resetAfterRead);
            if (summary == "집계 없음")
                return;

            OnStatusLog?.Invoke($"📉 [ENTRY BLOCK 10m] {summary}");

            string hint = BuildEntryBlockTuneHint(topN: 3);
            if (hint != "집계 없음")
            {
                OnStatusLog?.Invoke($"🛠️ [ENTRY TUNE HINT] {hint}");
            }
        }

        // 1. 포지션 현재 상태 스냅샷 동기화 (REST API 활용)
        private async Task SyncCurrentPositionsAsync(CancellationToken token)
        {
            try
            {
                var positions = await _exchangeService.GetPositionsAsync(token);
                if (positions != null)
                {
                    var syncedPositions = new List<(TradingBot.Shared.Models.PositionInfo Pos, DateTime EntryTime, float AiScore, decimal StopLoss)>();

                    foreach (var pos in positions)
                    {
                        if (string.IsNullOrEmpty(pos.Symbol))
                            continue;

                        var ensureResult = await _dbManager.EnsureOpenTradeForPositionAsync(new TradingBot.Shared.Models.PositionInfo
                        {
                            Symbol = pos.Symbol,
                            EntryPrice = pos.EntryPrice,
                            IsLong = pos.IsLong,
                            Side = pos.Side,
                            AiScore = pos.AiScore,
                            Leverage = pos.Leverage > 0 ? pos.Leverage : _settings.DefaultLeverage,
                            Quantity = Math.Abs(pos.Quantity),
                            EntryTime = DateTime.Now
                        }, "SYNC_RESTORED");

                        decimal majorHybridStopLoss = 0m;
                        if (MajorSymbols.Contains(pos.Symbol))
                        {
                            var majorStop = await TryCalculateMajorAtrHybridStopLossAsync(pos.Symbol, pos.EntryPrice, pos.IsLong, token);
                            majorHybridStopLoss = majorStop.StopLossPrice;
                            if (majorHybridStopLoss > 0)
                            {
                                OnStatusLog?.Invoke($"🛡️ [MAJOR ATR] {pos.Symbol} 시작 복원 포지션 손절 재계산 | SL={majorHybridStopLoss:F8}, ATRdist={majorStop.AtrDistance:F8}, 구조선={majorStop.StructureStopPrice:F8}");
                            }
                        }

                        syncedPositions.Add((pos, ensureResult.EntryTime, ensureResult.AiScore, majorHybridStopLoss));
                    }

                    // [v5.2.2] DB에서 이 유저의 오픈 포지션 심볼 목록 조회 → 거래소 포지션과 교차 비교
                    int currentUserId = AppConfig.CurrentUser?.Id ?? 0;
                    var ownOpenSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (currentUserId > 0)
                    {
                        try
                        {
                            var dbOpenTrades = await _dbManager.GetOpenTradesAsync(currentUserId);
                            foreach (var dbTrade in dbOpenTrades)
                                ownOpenSymbols.Add(dbTrade.Symbol);
                        }
                        catch { }
                    }

                    lock (_posLock)
                    {
                        _activePositions.Clear();
                        foreach (var synced in syncedPositions)
                        {
                            var pos = synced.Pos;
                            // [v5.2.2] DB에 이 유저의 오픈 기록이 있으면 IsOwnPosition=true
                            bool isOwn = ownOpenSymbols.Contains(pos.Symbol);
                            _activePositions[pos.Symbol] = new PositionInfo
                            {
                                Symbol = pos.Symbol,
                                EntryPrice = pos.EntryPrice,
                                IsLong = pos.IsLong,
                                Side = pos.Side,
                                IsPumpStrategy = !MajorSymbols.Contains(pos.Symbol),
                                AiScore = synced.AiScore,
                                Leverage = pos.Leverage > 0 ? pos.Leverage : _settings.DefaultLeverage,
                                Quantity = Math.Abs(pos.Quantity),
                                InitialQuantity = Math.Abs(pos.Quantity),
                                EntryTime = synced.EntryTime,
                                StopLoss = synced.StopLoss,
                                HighestPrice = pos.EntryPrice,
                                LowestPrice = pos.EntryPrice,
                                PyramidCount = 0,
                                IsOwnPosition = isOwn
                            };
                            if (!isOwn)
                                OnStatusLog?.Invoke($"ℹ️ [SYNC] {pos.Symbol} 거래소 포지션 감지 — 다른 유저 포지션 (슬롯 미산입)");
                        }
                    }

                    // [v3.5.2] DB에서 포지션 상태 복원 (부분청산/본절/계단식)
                    try
                    {
                        int stateUserId = AppConfig.CurrentUser?.Id ?? 0;
                        if (stateUserId > 0)
                        {
                            var savedStates = await _dbManager.LoadPositionStatesAsync(stateUserId);
                            lock (_posLock)
                            {
                                foreach (var kvp in savedStates)
                                {
                                    if (_activePositions.TryGetValue(kvp.Key, out var ap))
                                    {
                                        ap.TakeProfitStep = kvp.Value.TakeProfitStep;
                                        ap.PartialProfitStage = kvp.Value.PartialProfitStage;
                                        ap.BreakevenPrice = kvp.Value.BreakevenPrice;
                                        ap.HighestROEForTrailing = kvp.Value.HighestROE;
                                        ap.HighestPrice = kvp.Value.HighestPrice > 0 ? kvp.Value.HighestPrice : ap.HighestPrice;
                                        ap.LowestPrice = kvp.Value.LowestPrice > 0 ? kvp.Value.LowestPrice : ap.LowestPrice;
                                        ap.IsPumpStrategy = kvp.Value.IsPumpStrategy;
                                        OnStatusLog?.Invoke($"🔄 [상태 복원] {kvp.Key} | TP단계={kvp.Value.TakeProfitStep} 부분청산={kvp.Value.PartialProfitStage} 본절={kvp.Value.BreakevenPrice:F4} 계단={kvp.Value.StairStep} 최고ROE={kvp.Value.HighestROE:F1}%");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception stateEx)
                    {
                        OnStatusLog?.Invoke($"⚠️ [상태 복원] DB 로드 실패: {stateEx.Message}");
                    }

                    foreach (var synced in syncedPositions)
                    {
                        var pos = synced.Pos;
                        decimal uiSafeLeverage = pos.Leverage > 0
                            ? pos.Leverage
                            : _settings.DefaultLeverage;
                        if (uiSafeLeverage <= 0)
                            uiSafeLeverage = 1m;

                        OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                        {
                            Symbol = pos.Symbol,
                            IsPositionActive = true,
                            EntryPrice = pos.EntryPrice,
                            PositionSide = pos.IsLong ? "LONG" : "SHORT",
                            Quantity = Math.Abs(pos.Quantity),
                            Leverage = (int)Math.Max(1m, Math.Round(uiSafeLeverage, MidpointRounding.AwayFromZero))
                        });

                        string sideStr = pos.IsLong ? "LONG" : "SHORT";
                        OnStatusLog?.Invoke($"[동기화] {pos.Symbol} {sideStr} | 평단: {pos.EntryPrice}");
                        decimal syncedStopLoss = 0m;
                        bool isSyncPump = false;
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(pos.Symbol, out var active))
                            {
                                if (active.StopLoss > 0) syncedStopLoss = active.StopLoss;
                                isSyncPump = active.IsPumpStrategy; // [버그수정] PUMP 포지션 확인
                            }
                        }

                        // [v5.10.19] 기존 포지션 → PositionSyncService 등록 (폴링 감시)
                        _orderManager.RegisterBracket(pos.Symbol);
                        OnStatusLog?.Invoke($"🔄 [PositionSync] {pos.Symbol} 재시작 시 브라켓 등록 — 폴링 감시 시작");
                        // [v5.10.19] 재시작된 기존 포지션은 거래소에 이미 SL/TP가 있으므로 bot-side 감시 불필요
                        // [버그수정] PUMP 포지션에 Standard Monitor 시작하면 breakEvenROE=7%로 조기 청산됨
                        // if (!isSyncPump)
                        //     TryStartStandardMonitor(pos.Symbol, ...);
                        // else
                        //     TryStartPumpMonitor(pos.Symbol, ...);
                    }
                    OnStatusLog?.Invoke("✅ 현재 보유 포지션 동기화 완료");

                    // ═══════════════════════════════════════════════════════════════
                    // [v5.10.54] 재시작 시 OrderLifecycleManager로 재등록 — CancelAll 내장 + 쿨다운 자동 초기화
                    // ═══════════════════════════════════════════════════════════════
                    if (_orderLifecycle != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(3000, token);
                            List<(string Symbol, decimal EntryPrice, bool IsLong, decimal Quantity, decimal Leverage)> positionsToRegister;
                            lock (_posLock)
                            {
                                positionsToRegister = _activePositions
                                    .Where(p => p.Value.IsOwnPosition && Math.Abs(p.Value.Quantity) > 0)
                                    .Select(p => (p.Key, p.Value.EntryPrice, p.Value.IsLong, Math.Abs(p.Value.Quantity), p.Value.Leverage))
                                    .ToList();
                            }

                            var exchangePositions = await _exchangeService.GetPositionsAsync(ct: token);
                            var exchangeOpenSymbols = new HashSet<string>(
                                exchangePositions.Where(p => Math.Abs(p.Quantity) > 0).Select(p => p.Symbol),
                                StringComparer.OrdinalIgnoreCase);

                            foreach (var pos in positionsToRegister)
                            {
                                if (!exchangeOpenSymbols.Contains(pos.Symbol)) continue;

                                try
                                {
                                    bool isMajor = MajorSymbols.Contains(pos.Symbol);
                                    decimal slRoe = isMajor ? (_settings.MajorStopLossRoe > 0 ? -_settings.MajorStopLossRoe : -60m) : -40m;
                                    decimal tpRoe = isMajor ? (_settings.MajorTp2Roe > 0 ? _settings.MajorTp2Roe : 30m) : 25m;
                                    decimal tpPartial = isMajor ? 0.4m : 0.6m;
                                    decimal trailCb = isMajor ? 2.0m : 3.5m;

                                    _orderLifecycle.ResetCooldown(pos.Symbol);
                                    var result = await _orderLifecycle.RegisterOnEntryAsync(
                                        pos.Symbol, pos.IsLong, pos.EntryPrice,
                                        pos.Quantity, (int)pos.Leverage,
                                        slRoe, tpRoe, tpPartial, trailCb, token);

                                    OnStatusLog?.Invoke($"✅ [재시작] {pos.Symbol} SL/TP/Trailing 재등록 (SL={!string.IsNullOrEmpty(result.SlOrderId)} TP={!string.IsNullOrEmpty(result.TpOrderId)} TR={!string.IsNullOrEmpty(result.TrailingOrderId)})");
                                }
                                catch (Exception regEx)
                                {
                                    OnStatusLog?.Invoke($"⚠️ [재시작 SL/TP] {pos.Symbol} 등록 실패: {regEx.Message}");
                                }
                            }
                        }, token);
                    }
                }
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ 포지션 동기화 에러: {ex.Message}"); }
        }

        /// <summary>
        /// 봇 시작 시 DB 오픈 포지션과 거래소 실제 포지션을 비교하여
        /// DB에만 있고 거래소에 없는 포지션(봇 중단 중 외부 청산)을 자동으로 정리
        /// </summary>
        private async Task ReconcileDbWithExchangePositionsAsync(CancellationToken token)
        {
            try
            {
                int userId = AppConfig.CurrentUser?.Id ?? 0;
                if (userId <= 0)
                {
                    OnStatusLog?.Invoke("⚠️ [DB 정리] 현재 로그인 사용자 ID를 확인할 수 없어 사용자별 오픈 포지션 정리를 건너뜁니다.");
                    return;
                }

                var dbOpenTrades = await _dbManager.GetOpenTradesAsync(userId);

                if (dbOpenTrades.Count == 0)
                {
                    OnStatusLog?.Invoke("✅ [DB 정리] DB에 오픈 포지션 없음 → 정리 불필요");
                    return;
                }

                OnStatusLog?.Invoke($"🔍 [DB 정리] DB 오픈 포지션: {dbOpenTrades.Count}개 발견, 거래소와 비교 시작");

                int closedCount = 0;
                List<string> exchangePositionSymbols;

                lock (_posLock)
                {
                    exchangePositionSymbols = _activePositions.Keys.ToList();
                }

                foreach (var dbTrade in dbOpenTrades)
                {
                    bool existsInExchange = exchangePositionSymbols.Contains(dbTrade.Symbol);

                    if (!existsInExchange)
                    {
                        // DB에만 있고 거래소에 없음 → 외부 청산된 것으로 간주
                        string side = dbTrade.Side;
                        string closeSide = side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";

                        // [v5.20.7 BUG FIX] 외부 청산도 Binance Income REALIZED_PNL 조회 → 실제 PnL 기록
                        //   기존: PnL=0 일괄 처리 → 일일 손익 통계에서 손실 누락 ($-70 사라짐)
                        decimal actualPnl = 0m;
                        try
                        {
                            if (_exchangeService is BinanceExchangeService binSvc)
                            {
                                actualPnl = await binSvc.GetRealizedPnLAsync(
                                    dbTrade.Symbol,
                                    dbTrade.EntryTime.ToUniversalTime(),
                                    DateTime.UtcNow);
                            }
                        }
                        catch { }

                        var closeLog = new TradeLog(
                            dbTrade.Symbol,
                            closeSide,
                            "EXTERNAL_RECONCILE",
                            dbTrade.EntryPrice, // 청산가 불명 → 진입가 사용
                            0f,
                            DateTime.Now,
                            actualPnl,
                            0m
                        )
                        {
                            ExitPrice = dbTrade.EntryPrice,
                            EntryPrice = dbTrade.EntryPrice,
                            Quantity = dbTrade.Quantity,
                            ExitReason = $"EXTERNAL_CLOSE_WHILE_BOT_STOPPED (PnL={actualPnl:F4})",
                            EntryTime = dbTrade.EntryTime,
                            ExitTime = DateTime.Now,
                            PnL = actualPnl
                        };

                        bool saved = await _dbManager.CompleteTradeAsync(closeLog);
                        if (saved)
                        {
                            closedCount++;
                            OnStatusLog?.Invoke($"🧹 [DB 정리] {dbTrade.Symbol} {side} 외부 청산 감지 → DB 자동 정리 완료 (진입: {dbTrade.EntryTime:yyyy-MM-dd HH:mm})");
                        }
                        else
                        {
                            OnStatusLog?.Invoke($"⚠️ [DB 정리] {dbTrade.Symbol} {side} 정리 실패");
                        }
                    }
                }

                if (closedCount > 0)
                {
                    OnStatusLog?.Invoke($"✅ [DB 정리 완료] 총 {closedCount}개 외부 청산 포지션 DB 정리 완료");
                    await NotificationService.Instance.NotifyAsync(
                        $"🧹 DB 정리 완료\n외부 청산 감지: {closedCount}개 포지션\n(봇 중단 중 수동 청산된 포지션 자동 정리)",
                        NotificationChannel.Alert
                    );
                }
                else
                {
                    OnStatusLog?.Invoke("✅ [DB 정리] 모든 DB 오픈 포지션이 거래소와 일치 → 정리 불필요");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [DB 정리] 오류 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// [블랙리스트 복구] DB의 TradeHistory에서 최근 1시간 이내 종료된 포지션을 조회
        /// 메모리 _blacklistedSymbols에 로드하여 프로그램 재시작 후에도 재진입 방지
        /// </summary>
        private async Task RestoreBlacklistFromDatabaseAsync(CancellationToken token)
        {
            try
            {
                const int withinMinutes = 60; // 1시간 이내
                int currentUserId = AppConfig.CurrentUser?.Id ?? 0;
                var recentlyClosed = await _dbManager.GetRecentlyClosedPositionsAsync(
                    withinMinutes,
                    currentUserId > 0 ? currentUserId : null);

                if (recentlyClosed.Count == 0)
                {
                    OnStatusLog?.Invoke($"✅ [블랙리스트 복구] 최근 1시간 이내 종료된 포지션 없음");
                    return;
                }

                DateTime now = DateTime.Now;
                int loadedCount = 0;

                foreach (var (symbol, lastExitTime) in recentlyClosed)
                {
                    // ExitTime에서 1시간 추가
                    DateTime blacklistExpiry = lastExitTime.AddMinutes(withinMinutes);
                    
                    // 아직 블랙리스트 기간 내면 로드
                    if (now < blacklistExpiry)
                    {
                        _blacklistedSymbols[symbol] = blacklistExpiry;
                        loadedCount++;
                        TimeSpan remaining = blacklistExpiry - now;
                        OnStatusLog?.Invoke($"🔒 [블랙리스트 복구] {symbol} 로드 (종료: {lastExitTime:HH:mm:ss}, 남은 시간: {remaining.TotalMinutes:F0}분)");
                    }
                }

                if (loadedCount > 0)
                {
                    OnStatusLog?.Invoke($"✅ [블랙리스트 복구 완료] 총 {loadedCount}개 심볼 블랙리스트 로드");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [블랙리스트 복구] 오류 발생: {ex.Message}");
            }
        }

        private async Task<(decimal StopLossPrice, decimal AtrDistance, decimal StructureStopPrice)> TryCalculateMajorAtrHybridStopLossAsync(
            string symbol,
            decimal referencePrice,
            bool isLong,
            CancellationToken token)
        {
            try
            {
                if (!MajorSymbols.Contains(symbol) || referencePrice <= 0)
                    return (0m, 0m, 0m);

                List<IBinanceKline>? candles = null;
                var fetched = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 60, token);
                if (fetched != null && fetched.Count >= 15)
                    candles = fetched.ToList();

                if (candles == null || candles.Count < 15)
                    return (0m, 0m, 0m);

                // [v3.1.9] 구조 기반 손절: 15분봉 구조선(지지/저항) 우선, ATR은 최대 캡만
                // 20배 레버리지에서 구조선까지의 거리가 ROE -40~50%가 될 수 있음
                // → 사이즈를 줄여야지 손절선을 좁히면 안 됨
                double atr = IndicatorCalculator.CalculateATR(candles, 14);
                if (atr <= 0)
                    return (0m, 0m, 0m);

                // 구조선: 15분봉 최근 20봉의 실제 지지/저항선
                var swingCandles = candles.TakeLast(Math.Min(20, candles.Count)).ToList();
                decimal structureStopPrice = isLong
                    ? swingCandles.Min(c => c.LowPrice) * 0.998m  // 스윙로우 -0.2%
                    : swingCandles.Max(c => c.HighPrice) * 1.002m; // 스윙하이 +0.2%

                // ATR 최대 캡: 구조선이 너무 멀면 ATR x5로 제한
                decimal atrMaxCap = (decimal)atr * 5.0m;
                decimal maxStopDistance = isLong
                    ? referencePrice - (referencePrice - atrMaxCap)
                    : (referencePrice + atrMaxCap) - referencePrice;

                decimal structureDistance = isLong
                    ? referencePrice - structureStopPrice
                    : structureStopPrice - referencePrice;

                // 구조선이 ATR x5보다 멀면 ATR x5로 제한
                decimal hybridStopPrice;
                if (structureDistance > maxStopDistance)
                {
                    hybridStopPrice = isLong
                        ? referencePrice - atrMaxCap
                        : referencePrice + atrMaxCap;
                }
                else
                {
                    hybridStopPrice = structureStopPrice; // 구조선 우선 사용
                }

                decimal atrDistance = (decimal)atr * 3.5m;

                if (isLong && hybridStopPrice >= referencePrice)
                    hybridStopPrice = referencePrice - atrDistance;
                else if (!isLong && hybridStopPrice <= referencePrice)
                    hybridStopPrice = referencePrice + atrDistance;

                return (hybridStopPrice, atrDistance, structureStopPrice);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [MAJOR ATR] {symbol} 하이브리드 손절 계산 실패: {ex.Message}");
                return (0m, 0m, 0m);
            }
        }


        public async Task StartScanningOptimizedAsync()
        {
            if (IsBotRunning) return;
            IsBotRunning = true;

            try
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                // [v5.10.18] 거래소 폴링 동기화 시작
                _positionSyncService.Start(token);

                // [v5.17.0 REDESIGN] 15-5-1 엔진 이벤트 연결
                _entryEngine151.OnLog += msg => OnStatusLog?.Invoke(msg);
                _entryEngine151.OnEntryFire += async trigger =>
                {
                    try
                    {
                        // 글로벌 진입 게이트 체크 (슬롯 / 일일한도 / 블랙리스트)
                        if (!IsEntryAllowed(trigger.Symbol, "ENGINE_151", out string blockReason))
                        {
                            OnStatusLog?.Invoke($"⛔ [ENGINE_151] {trigger.Symbol} 차단: {blockReason}");
                            return;
                        }
                        // ExecuteAutoOrder 로 통합 — 기존 AI Gate / 슬롯 체크 / 사이즈 산출 활용
                        await ExecuteAutoOrder(trigger.Symbol, trigger.Direction, trigger.TriggerPrice, token, "ENGINE_151", skipAiGateCheck: false);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [ENGINE_151] {trigger.Symbol} 체결 예외: {ex.Message}");
                    }
                };
                // 5초 주기 15m/5m/1m 폴링 루프 시작
                _ = Task.Run(() => Run151EngineLoopAsync(token), token);

                // [v5.17.1 SAFETY] 봇 시작 시 잔존 algoOrder cleanup
                //   사용자 사례: 1시 청산 후 잠든 사이 algoOrder 자동 trigger 로 14건 부분 매도
                //   봇 시작 직후 활성 포지션 없는 심볼의 algoOrder 일괄 cancel
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { }
                    try { await CleanupOrphanAlgoOrdersAsync(allSymbols: true, reason: "engine_start"); } catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [START_CLEANUP] {ex.Message}"); }
                }, token);

                OnAlert?.Invoke("🚀 최적화 엔진 가동 (WebSocket 모드) + 15-5-1 엔진 활성");
                LoggerService.Info("엔진 시작: WebSocket 모드 + 15-5-1");

                // [v5.22.13] 초기학습 안내 제거 — AI 시스템 폐기 (2026-04-29)

                foreach (var symbol in _symbols)
                {
                    EnsureSymbolInList(symbol);
                }

                OnStatusLog?.Invoke($"📌 추적 심볼: {string.Join(", ", _symbols)}");

                // 1. 초기화 (지갑 잔고 및 현재 포지션 동기화)
                await InitializeSeedAsync(token); // InitialBalance 설정
                _riskManager.Initialize((decimal)InitialBalance); // RiskManager 초기화

                // [v4.4.0] DB에서 오늘 누적 PnL 복원
                try
                {
                    int userId = AppConfig.CurrentUser?.Id ?? 0;
                    if (userId > 0)
                    {
                        var todayTrades = await _dbManager.GetTradeHistoryAsync(userId, DateTime.Today, DateTime.Now, 500);
                        if (todayTrades != null && todayTrades.Count > 0)
                        {
                            // [v5.1.5] MANUAL/EXTERNAL 제외 — 수동 진입 손실로 서킷 브레이커 재발동 방지
                            decimal todayPnl = todayTrades
                                .Where(t => t.PnL != 0
                                    && !string.Equals(t.ExitReason, "OPEN_POSITION", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(t.Strategy, "MANUAL", StringComparison.OrdinalIgnoreCase)
                                    && !(t.Strategy ?? "").StartsWith("EXTERNAL", StringComparison.OrdinalIgnoreCase))
                                .Sum(t => t.PnL);
                            _riskManager.RestoreDailyPnl(todayPnl);
                            OnStatusLog?.Invoke($"💰 [복원] 금일 누적 PnL: ${todayPnl:N2} (DB 기반, MANUAL/EXTERNAL 제외)");
                        }
                    }
                }
                catch (Exception pnlEx) { OnStatusLog?.Invoke($"⚠️ 금일 PnL 복원 실패: {pnlEx.Message}"); }

                OnStatusLog?.Invoke($"🔧 엔진 초기화 중...");

                // 시뮬레이션 모드일 경우 초기 잔고 로그 출력
                if (AppConfig.Current?.Trading?.IsSimulationMode == true)
                {
                    OnLiveLog?.Invoke($"🎮 시뮬레이션 시작 잔고: ${InitialBalance:N2}");
                }

                await SyncCurrentPositionsAsync(token);

                // [추가] DB 오픈 포지션과 거래소 포지션 불일치 자동 정리 (봇 중단 중 외부 청산)
                await ReconcileDbWithExchangePositionsAsync(token);

                // [블랙리스트 복구] DB에서 최근 1시간 이내 종료된 포지션 로드
                await RestoreBlacklistFromDatabaseAsync(token);

                // [Elliott 앵커 복원] 재시작 후에도 파동 기준점 유지
                await RestoreElliottWaveAnchorsFromDatabaseAsync(token);

                // [v5.22.2] AI 학습 비활성화 — 사용자 결정 (2026-04-28)
                // await TriggerInitialMLNetTrainingIfNeededAsync(token);

                // [AI 제거] AIDataCollectorService / AIDecisionService / AIBacktestEngine / AIDoubleCheckEntryGate 초기화 통째 제거

                // 텔레그램 시작 알림 전송
                string startMessage = $"🚀 *[봇 시작]*\n\n" +
                                     $"모드: {(AppConfig.Current?.Trading?.IsSimulationMode == true ? "시뮬레이션" : "실거래")}\n" +
                                     $"초기 잔고: ${InitialBalance:N2} USDT\n" +
                                     $"거래소: {(AppConfig.Current?.Trading?.SelectedExchange).GetValueOrDefault(TradingBot.Shared.Models.ExchangeType.Binance)}\n" +
                                     $"레버리지: {_settings.DefaultLeverage}x\n" +
                                     $"시작 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                await NotificationService.Instance.NotifyAsync(startMessage, NotificationChannel.Alert);

                TelegramService.Instance.Initialize();
                TelegramService.Instance.OnRequestStatus = GetEngineStatusReport;
                TelegramService.Instance.GetActivePositionSymbols = () =>
                {
                    lock (_posLock) { return new HashSet<string>(_activePositions.Keys, StringComparer.OrdinalIgnoreCase); }
                };
                TelegramService.Instance.OnRequestStop = StopEngine;
                // [v5.22.13] /train 명령 비활성화 — AI 시스템 폐기 (2026-04-29)
                TelegramService.Instance.OnRequestTrain = (ct) => Task.FromResult("ℹ️ AI 시스템 제거됨 — /train 명령은 무효 처리됨");
                TelegramService.Instance.OnRequestDroughtScan = ForceDroughtDiagnosticAsync;
                // [AI 제거] /validate 명령 핸들러 제거
                TelegramService.Instance.OnRequestValidate = (ct) => Task.FromResult("ℹ️ AI Gate 제거됨 (validate 비활성화)");
                TelegramService.Instance.StartReceiving();
                OnTelegramStatusUpdate?.Invoke(true, "Telegram: Connected");

                // [v5.20.3] 봇 시작 시 텔레그램에 버전 + 활성 명령 명시 — "v5.20.x 적용됐는지" 사용자 즉시 확인 가능
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
                        await TelegramService.Instance.SendMessageAsync(
                            $"🤖 *TradingBot v{ver}* 시작됨\n" +
                            $"활성 명령: /status /train /validate /droughtscan /help");
                    }
                    catch { }
                });

                _ = ProcessTickerChannelAsync(token);
                _ = ProcessAccountChannelAsync(token); // [Agent 2] 계좌 업데이트 처리 시작
                _ = ProcessOrderChannelAsync(token);   // [Agent 2] 주문 업데이트 처리 시작

                // [v5.22.13] 초기학습 자동 트리거 + flag 진단 로그 통째 제거 — AI 시스템 폐기 (2026-04-29)

                // [AI 제거] AI 학습 상태 UI 초기화 — 라벨링 통계만 파일에서 조회
                {
                    var stats = GetRecentLabelStatsFromFiles(10);
                    MainWindow.Instance?.UpdateAiLearningStatusUI(
                        stats.total, stats.labeled, stats.markToMarket,
                        stats.tradeClose, _activeAiDecisionIds.Count, false);
                }

                // 2. 실시간 감시 시작 (Non-blocking)
                await _marketDataManager.StartAsync(token);

                // 시작 직후 5분봉 캐시 선로딩(최대 1000개) 후, 현시각까지 DB 백필 저장 완료를 보장
                await _marketDataManager.PreloadRecentKlinesAsync(limit: 1000, token);
                if (_marketHistoryService != null)
                {
                    await _marketHistoryService.BackfillToNowBeforeStartAsync(token);
                    _ = _marketHistoryService.StartRecordingAsync(token);
                }

                // [v5.22.2] AI 학습/예측 주기 작업 비활성화 — 사용자 결정 (2026-04-28)
                // _ = StartPeriodicTrainingAsync(token);
                // _ = StartPeriodicAiEntryProbScanAsync(token);
                // TryStartHistoricalEntryAudit(token);

                // 엔진 가동 시간 기록 및 주기 타이머 초기화
                // [수정] 모든 초기화 작업 완료 후 메인 루프 시작 직전에 워밍업 타이머 설정
                _engineStartTime = DateTime.Now;
                OnStatusLog?.Invoke($"⏳ 진입 워밍업 시작: {_entryWarmupDuration.TotalSeconds:F0}초 동안 신규 진입 제한");
                
                _lastHeartbeatTime = DateTime.Now; // 시작 시점 기록 (1시간 후 첫 알림)
                _lastReportTime = DateTime.Now;    // 시작 시점 기록 (1시간 후 첫 보고)
                _lastAiGateSummaryTime = DateTime.Now; // [AI 관제탑] 시작 시점 기록 (5분 후 첫 요약)
                _lastEntryBlockSummaryTime = DateTime.Now;
                _lastSuccessfulEntryTime = DateTime.Now; // [드라이스펠] 엔진 기동을 기준으로 카운트 시작
                _lastDroughtScanTime = DateTime.MinValue;
                _lastTimeOutProbScanTime = DateTime.MinValue;
                _lastPositionSyncTime = DateTime.Now; // [FIX] 시작 시점 기록 (30분 후 첫 동기화)
                _periodicReportBaselineEquity = await GetEstimatedAccountEquityUsdtAsync(token);
                if (_periodicReportBaselineEquity <= 0)
                    _periodicReportBaselineEquity = InitialBalance;

                // 3. 메인 관리 루프 (REST API 호출 최소화)
                OnStatusLog?.Invoke("🔄 메인 스캔 루프 시작...");
                while (!token.IsCancellationRequested)
                {
                    Stopwatch? loopWatch = _mainLoopPerfEnabled ? Stopwatch.StartNew() : null;
                    bool loopMetricsRecorded = false;

                    try
                    {

                        // [일일 수익 목표] 자정 리셋 및 알림 ($250/일 알림 전용, 차단 없음)
                        if (DateTime.Today > _dailyTargetResetDate)
                        {
                            _dailyTargetResetDate = DateTime.Today;
                            _dailyTargetHitNotified = false;
                        }

                        // [v4.5.11] 메인 루프마다 DailyPnl 컨텍스트 갱신 (FeatureExtractor 공유)
                        UpdateDailyContextForFeatures();

                        if (!_dailyTargetHitNotified && (_riskManager?.DailyRealizedPnl ?? 0m) >= 250m)
                        {
                            _dailyTargetHitNotified = true;
                            decimal dailyPnl = _riskManager!.DailyRealizedPnl;
                            int activeCount;
                            lock (_posLock) { activeCount = _activePositions.Count; }
                            string targetMsg =
                                $"🎯 [일일 목표 달성] $250/일 목표 초과!\n" +
                                $"💰 금일 실현 손익: **${dailyPnl:N2}**\n" +
                                $"🚀 운용 중 포지션: {activeCount}개\n" +
                                $"✅ 추가 수익 계속 허용 (차단 없음)";
                            OnAlert?.Invoke(targetMsg);
                            _ = NotificationService.Instance.NotifyAsync(targetMsg, NotificationChannel.Profit);
                            OnStatusLog?.Invoke($"🎯 일일 $250 목표 달성! 금일 실현 손익: ${dailyPnl:N2}");
                        }

                        // [v5.22.8] 급등주 스캔 비활성화 — 사용자 지적 + 폭주 ROOT FIX
                        //   원인: 60 candidates 병렬 × GetKlinesAsync(5m, 150봉) = 분당 360회 × weight 5 = 1,800 weight/분
                        //   PumpScanStrategy 가 분당 2400 한도의 75% 점유 → -1003 발생
                        //   PUMP 카테고리는 v5.22.5 에서 진입 차단됨 → PumpScan 자체 무의미
                        //   필요 시 ActiveTrackingPool 의 동적 8개 갱신용으로만 5분 1회 호출 (별도)
                        // if (_pumpStrategy != null && (DateTime.Now - _lastPumpScanTime).TotalSeconds >= 10)
                        // {
                        //     _lastPumpScanTime = DateTime.Now;
                        //     await _pumpStrategy.ExecuteScanAsync(_marketDataManager.TickerCache, _blacklistedSymbols, token);
                        // }

                        // [B] MACD 골든크로스/데드크로스 스캔 (메이저 코인 대상)
                        if (_macdCrossService != null)
                            await ScanMacdGoldenCrossAsync(token);

                        // [C] 15분봉 위꼬리 음봉 스캔 → 백그라운드 (블로킹 5분 방지)
                        if (_macdCrossService != null && (DateTime.Now - _last15mTailScanTime).TotalMinutes >= 1)
                            _ = Task.Run(() => Scan15mBearishTailAsync(token));

                        // [v3.8.0] PUMP 감시 풀 확인 — 거래량 급증 후 상승 확인 시 진입
                        if (_pumpWatchPool.Count > 0)
                        {
                            var expiredKeys = new List<string>();
                            foreach (var kvp in _pumpWatchPool)
                            {
                                var (watchPrice, watchTime, volRatio) = kvp.Value;

                                // 5분 만료
                                if ((DateTime.Now - watchTime).TotalMinutes > 5)
                                {
                                    expiredKeys.Add(kvp.Key);
                                    continue;
                                }

                                // 이미 포지션 있으면 스킵
                                lock (_posLock) { if (_activePositions.ContainsKey(kvp.Key)) { expiredKeys.Add(kvp.Key); continue; } }

                                // 현재가 확인
                                if (!_marketDataManager.TickerCache.TryGetValue(kvp.Key, out var wTick) || wTick.LastPrice <= 0) continue;
                                decimal nowPrice = wTick.LastPrice;
                                decimal risePct = (nowPrice - watchPrice) / watchPrice * 100m;

                                // [v4.0.4] 상승 확인: +0.5~5% (빠른 감지 + 꼭대기 방지)
                                if (risePct >= 0.5m && risePct <= 5.0m)
                                {
                                    expiredKeys.Add(kvp.Key);
                                    var pumpKey = kvp.Key;
                                    var pumpVolRatio = volRatio;
                                    OnStatusLog?.Invoke($"✅ [감시확인] {pumpKey} +{risePct:F1}% 상승 확인 → 진입 시도 (vol={pumpVolRatio:F1}x)");
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            // [v4.6.0] 감시풀 등록 심볼 → 동적 수집 대상 등록 + 즉시 백필
                                            _marketHistoryService?.RegisterSymbol(pumpKey);
                                            _ = _marketHistoryService?.RequestBackfillAsync(pumpKey, _cts?.Token ?? CancellationToken.None);

                                            // [v4.5.11] 일일 수익 모드 기반 보수 모드 체크
                                            var (allowed, sizeMul, _) = CheckDailyProfitModeForPumpEntry(pumpKey, "PUMP_WATCH_CONFIRMED");
                                            if (!allowed)
                                            {
                                                OnStatusLog?.Invoke($"⛔ [감시진입차단] {pumpKey} 보수모드 차단 (일일 수익 한도)");
                                                return;
                                            }

                                            // [v4.5.9] 일일 PUMP 진입 한도 (40회) 체크
                                            if (!TryReserveDailyPumpEntry(pumpKey, "PUMP_WATCH_CONFIRMED"))
                                            {
                                                OnStatusLog?.Invoke($"⛔ [감시진입차단] {pumpKey} 일일 PUMP 한도 40회 초과");
                                                return;
                                            }

                                            // [v4.6.0] PUMP 전용 HTF 체크 — H1 OR M15 중 하나만 충족하면 통과
                                            // [v5.22.13] 초기학습 우회 조건 제거 — AI 폐기 후 항상 HTF 체크 수행
                                            if (!IsAiModelReadyForPumpEntry && _macdCrossService != null)
                                            {
                                                var (isBullish, htfDetail) = await _macdCrossService.CheckPumpHtfBullishAsync(
                                                    pumpKey, _cts?.Token ?? CancellationToken.None);
                                                if (!isBullish)
                                                {
                                                    OnStatusLog?.Invoke($"⛔ [감시진입차단-HTF] {pumpKey} H1/M15 둘 다 약세 | {htfDetail}");
                                                    return;
                                                }
                                                OnStatusLog?.Invoke($"✅ [감시진입-HTF통과] {pumpKey} | {htfDetail}");
                                            }

                                            // [v5.19.4] 15분봉 고점 진입 차단 — CTSIUSDT 사례:
                                            //   30봉 range 위치 85.7%, 직전 2봉 +0.86%/+0.86% 폭주 후 진입 → 즉시 -0.56% pullback
                                            //   규칙: M15 30봉 range 위치 ≥ 85% AND 30봉 저점 대비 ≥ +3% 상승 → 차단
                                            try
                                            {
                                                var k15 = await _exchangeService.GetKlinesAsync(
                                                    pumpKey, KlineInterval.FifteenMinutes, 30,
                                                    _cts?.Token ?? CancellationToken.None);
                                                if (k15 != null)
                                                {
                                                    var k15List = k15.ToList();
                                                    if (k15List.Count >= 30)
                                                    {
                                                        decimal minLow = k15List.Min(b => b.LowPrice);
                                                        decimal maxHigh = k15List.Max(b => b.HighPrice);
                                                        decimal latestClose = k15List[^1].ClosePrice;
                                                        decimal posPct = maxHigh > minLow
                                                            ? (latestClose - minLow) / (maxHigh - minLow) * 100m
                                                            : 50m;
                                                        decimal riseFromLowPct = minLow > 0m
                                                            ? (latestClose - minLow) / minLow * 100m
                                                            : 0m;
                                                        if (posPct >= 85m && riseFromLowPct >= 3m)
                                                        {
                                                            OnStatusLog?.Invoke($"⛔ [감시진입차단-고점] {pumpKey} M15 30봉 위치={posPct:F1}% rise={riseFromLowPct:F2}% (≥85% & ≥3% → 고점 추격)");
                                                            return;
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception exG) { OnStatusLog?.Invoke($"⚠️ [감시고점가드] {pumpKey} 체크 실패 (무시): {exG.Message}"); }

                                            // [v4.8.0] OptimalEntryPrice Hybrid — ML 예측 pullback에 LIMIT 시도, 실패 시 시장가 fallback
                                            bool limitFilled = await TryHybridLimitEntryAsync(pumpKey, nowPrice, sizeMul, _cts?.Token ?? CancellationToken.None);
                                            if (!limitFilled)
                                            {
                                                OnStatusLog?.Invoke($"🟢 [감시진입시도] {pumpKey} → ExecuteAutoOrder 호출 (size×{sizeMul})");
                                                await ExecuteAutoOrder(pumpKey, "LONG", nowPrice,
                                                    _cts?.Token ?? CancellationToken.None,
                                                    "PUMP_WATCH_CONFIRMED", manualSizeMultiplier: sizeMul);
                                            }
                                        }
                                        catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [감시진입] {pumpKey} 실패: {ex.Message}"); }
                                    });
                                }
                                // 너무 많이 오름 (+5%+) = 이미 늦음
                                else if (risePct > 5.0m)
                                {
                                    expiredKeys.Add(kvp.Key);
                                    OnStatusLog?.Invoke($"⏰ [감시만료] {kvp.Key} +{risePct:F1}% 이미 급등 → 감시 해제");
                                }
                                // 하락하면 제거 (-1.5% 이하)
                                else if (risePct <= -1.5m)
                                {
                                    expiredKeys.Add(kvp.Key);
                                    OnStatusLog?.Invoke($"❌ [감시취소] {kvp.Key} {risePct:F1}% 하락 → 감시 해제");
                                }
                            }
                            foreach (var k in expiredKeys) _pumpWatchPool.TryRemove(k, out _);
                        }

                        // [v3.2.44] AI Command Center 업데이트 (메인 루프에서 직접, 5초 간격)
                        UpdateAiCommandFromTickerCache();

                        if ((DateTime.Now - _lastHeartbeatTime).TotalHours >= 1)
                        {
                            string heartbeatMsg = $"💓 [Heartbeat] Bot is alive.\nActive Positions: {_activePositions.Count}\nUptime: {(DateTime.Now - _engineStartTime):dd\\.hh\\:mm}";
                            await NotificationService.Instance.NotifyAsync(heartbeatMsg, NotificationChannel.Log);
                            LoggerService.Info("Heartbeat sent.");
                            _lastHeartbeatTime = DateTime.Now;
                        }

                        // [FIX] 거래소 포지션 동기화 (30분 주기)
                        if ((DateTime.Now - _lastPositionSyncTime).TotalMinutes >= 30)
                        {
                            OnStatusLog?.Invoke("🔄 정기 거래소 포지션 동기화 시작...");
                            await SyncExchangePositionsAsync(token);
                            _lastPositionSyncTime = DateTime.Now;
                        }

                        // [v5.10.62 안전망] 2분 주기 — 활성 포지션에 SL/TP/Trailing 없으면 자동 등록
                        // TAO/BLUR/XAU 같이 wasTracked=true 경로로 들어와 보호 누락된 포지션 구제
                        if ((DateTime.Now - _lastProtectionCheckTime).TotalMinutes >= 2)
                        {
                            _lastProtectionCheckTime = DateTime.Now;
                            _ = Task.Run(async () =>
                            {
                                try { await EnsureActivePositionProtectionAsync(token); }
                                catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [보호점검] 예외: {ex.Message}"); }
                            }, token);
                        }

                        // [B] AI 관제탑 15분 요약 전송 — v5.0.4 주기 완화 + 빈 알림 차단
                        // 기존: 5분 + forceSendEmpty=true → 며칠째 "판정 없음" 스팸 발생
                        // 원인: v5.0.0 skipAiGateCheck=true 로 EvaluateEntryWithCoinTypeAsync 호출 자체가 줄음
                        // 해결: 주기 15분으로 완화 + 판정 없을 땐 조용히 (forceSendEmpty=false)
                        if ((DateTime.Now - _lastAiGateSummaryTime).TotalMinutes >= 15)
                        {
                            await TelegramService.Instance.FlushAiGateSummaryAsync(forceSendEmpty: false);
                            _lastAiGateSummaryTime = DateTime.Now;
                        }

                        if ((DateTime.Now - _lastEntryBlockSummaryTime) >= EntryBlockSummaryInterval)
                        {
                            EmitEntryBlockSummaryIfAny(resetAfterRead: true);
                            _lastEntryBlockSummaryTime = DateTime.Now;
                        }

                        // [v5.22.10] 메모리 누수 방지 — TTL 기반 ConcurrentDictionary cleanup (5분 주기)
                        if ((DateTime.Now - _lastMemoryCleanupTime).TotalMinutes >= 5)
                        {
                            CleanupExpiredCaches();
                            _lastMemoryCleanupTime = DateTime.Now;
                        }

                        // [C] 텔레그램 정기 수익 보고 (1시간 주기)
                        if ((DateTime.Now - _lastReportTime).TotalHours >= 1)
                        {
                            await SendDetailedPeriodicReport(token);
                            _lastReportTime = DateTime.Now;
                        }

                        // [D] 드라이스펠 감지: 30분 진입 없으면 진입 가능 심볼 진단
                        if (_lastSuccessfulEntryTime != DateTime.MinValue
                            && (DateTime.Now - _lastSuccessfulEntryTime) >= DroughtScanThreshold
                            && (DateTime.Now - _lastDroughtScanTime) >= DroughtScanInterval)
                        {
                            _lastDroughtScanTime = DateTime.Now;
                            // [AI 제거] RunDroughtDiagnosticScanAsync 제거됨 — drought 자동 복구 진입 비활성화
                        }

                        // [D2] TimeOut Probability Entry: 60분 공백 시 과거 패턴 유사도 기반 확률 베팅
                        if (_timeoutProbEngine != null
                            && _lastSuccessfulEntryTime != DateTime.MinValue
                            && (DateTime.Now - _lastSuccessfulEntryTime) >= TimeOutProbScanThreshold
                            && (DateTime.Now - _lastTimeOutProbScanTime) >= TimeOutProbScanInterval)
                        {
                            _lastTimeOutProbScanTime = DateTime.Now;
                            OnStatusLog?.Invoke("⏳ [TimeOut-Prob] 60분 공백 — 확률 베팅 스캔 시작...");
                            _ = Task.Run(() => _timeoutProbEngine.RunTimeOutScanAsync(token));
                        }

                        // [Agent 3] 지능형 메모리 관리 (주기적 체크)
                        MemoryManager.CheckAndCollect(msg => OnStatusLog?.Invoke(msg));

                        // 대시보드 갱신 및 대기
                        await RefreshProfitDashboard(token);
                        await Task.Delay(MainLoopInterval, token);
                    }
                    catch (Exception loopEx)
                    {
                        if (_mainLoopPerfEnabled && loopWatch != null)
                        {
                            loopWatch.Stop();
                            RecordMainLoopPerf(loopWatch.ElapsedMilliseconds);
                            loopMetricsRecorded = true;
                        }

                        LoggerService.Error("메인 루프 오류 (자동 복구 시도)", loopEx);
                        OnStatusLog?.Invoke($"⚠️ 일시적 오류: {loopEx.Message}. 5초 후 재시도...");
                        await Task.Delay(5000, token);
                    }
                    finally
                    {
                        if (_mainLoopPerfEnabled && !loopMetricsRecorded && loopWatch != null)
                        {
                            loopWatch.Stop();
                            RecordMainLoopPerf(loopWatch.ElapsedMilliseconds);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { OnStatusLog?.Invoke("엔진 정지 신호 수신"); }
            catch (Exception ex)
            {
                LoggerService.Error("치명적 오류로 엔진 정지", ex);
                OnStatusLog?.Invoke($"치명적 오류: {ex.Message}");
                await TelegramService.Instance.SendMessageAsync($"🚨 *[시스템 치명적 오류]*\n{ex.Message}\n\n_봇이 비정상 종료되었습니다._");
            }
            finally
            {
                IsBotRunning = false;
                _marketDataManager.Stop();
                OnProgress?.Invoke(0, 100);
                LoggerService.CloseAndFlush();
            }
        }

        private void RecordMainLoopPerf(long workMs)
        {
            if (!_mainLoopPerfEnabled)
                return;

            AutoTuneMainLoopPerf((int)workMs);

            _mainLoopTotalMs += workMs;
            _mainLoopSamples++;
            if (workMs > _mainLoopMaxMs)
                _mainLoopMaxMs = (int)workMs;

            bool isWarn = workMs >= _mainLoopWarnMs;
            bool isPeriodic = DateTime.UtcNow >= _nextMainLoopPerfLogTime;
            if (!isWarn && !isPeriodic)
                return;

            double avgMs = _mainLoopSamples > 0
                ? (double)_mainLoopTotalMs / _mainLoopSamples
                : 0;

            int activePositions;
            lock (_posLock)
            {
                activePositions = _activePositions.Count;
            }

            string level = isWarn ? "WARN" : "INFO";
            string perfMessage =
                $"[PERF][MAIN_LOOP][{level}] workMs={workMs} avgMs={avgMs:F1} maxMs={_mainLoopMaxMs} " +
                $"analysisQueue={_pendingAnalysisPrices.Count} workers={_analysisWorkers.Count} activePos={activePositions} " +
                $"warnMs={_mainLoopWarnMs} intervalSec={_mainLoopPerfLogIntervalSec} autoTune={_mainLoopAutoTuneEnabled}";

            LoggerService.Info(perfMessage);
            OnStatusLog?.Invoke($"⏱️ {perfMessage}");

            if (isPeriodic)
            {
                _nextMainLoopPerfLogTime = DateTime.UtcNow.AddSeconds(_mainLoopPerfLogIntervalSec);
                _mainLoopTotalMs = 0;
                _mainLoopSamples = 0;
                _mainLoopMaxMs = 0;
            }
        }

        /// <summary>
        /// 진입 필터 설정을 appsettings.json에서 로드합니다.
        /// </summary>
        private void LoadEntryFilterSettings()
        {
            var settings = AppConfig.Current?.Trading?.EntryFilterSettings;
            if (settings == null)
            {
                OnStatusLog?.Invoke("⚠️ EntryFilterSettings 미설정 (기본값 사용)");
                return;
            }

            _entryWarmupDuration = TimeSpan.FromSeconds(Math.Max(0, settings.EntryWarmupSeconds));
            _minEntryRiskRewardRatio = Math.Max(1.0m, settings.MinRiskRewardRatio);
            _enableFifteenMinWaveGate = settings.EnableFifteenMinWaveGate;
            _fifteenMinuteMlMinConfidence = Math.Clamp(settings.FifteenMinMlConfidence, 0f, 1f);
            _fifteenMinuteTransformerMinConfidence = Math.Clamp(settings.FifteenMinTransformerConfidence, 0f, 1f);
            _aiScoreThresholdMajor = Math.Max(56f, settings.AiScoreThresholdMajor); // [메이저 고속도로] 0.8배 하향
            _aiScoreThresholdNormal = Math.Max(70f, settings.AiScoreThresholdNormal);
            _aiScoreThresholdPump = Math.Max(60f, settings.AiScoreThresholdPump);
            _enableAiScoreFilter = settings.EnableAiScoreFilter;

            OnStatusLog?.Invoke(
                $"✅ 진입 필터 설정 로드 완료: " +
                $"워밍업={_entryWarmupDuration.TotalSeconds}초, " +
                $"RR>={_minEntryRiskRewardRatio:F2}, " +
                $"15분Gate={(_enableFifteenMinWaveGate ? "ON" : "OFF")}, " +
                $"ML>={_fifteenMinuteMlMinConfidence:P0}, " +
                $"TF>={_fifteenMinuteTransformerMinConfidence:P0}, " +
                $"AI(메이저>={_aiScoreThresholdMajor}, 일반>={_aiScoreThresholdNormal}, 펌프>={_aiScoreThresholdPump})"
            );
        }

        private void ApplyMainLoopPerformanceSettings()
        {
            var settings = AppConfig.Current?.Trading?.PerformanceMonitoring;
            if (settings == null)
            {
                _nextMainLoopPerfLogTime = DateTime.UtcNow.AddSeconds(_mainLoopPerfLogIntervalSec);
                _nextMainLoopTuneTime = DateTime.UtcNow.AddSeconds(_mainLoopTuneMinIntervalSec);
                return;
            }

            // 프로파일 기반 프리셋 적용
            settings.ApplyProfile();

            _mainLoopPerfEnabled = settings.EnableMetrics;
            _mainLoopAutoTuneEnabled = settings.EnableAutoTune;

            _mainLoopBaseWarnMs = Math.Max(1, settings.MainLoopWarnMs);
            _mainLoopBasePerfLogIntervalSec = Math.Max(1, settings.MainLoopPerfLogIntervalSec);

            _mainLoopWarnMinMs = Math.Max(1, settings.MainLoopWarnMinMs);
            _mainLoopWarnMaxMs = Math.Max(_mainLoopWarnMinMs, settings.MainLoopWarnMaxMs);
            _mainLoopPerfLogIntervalMinSec = Math.Max(1, settings.PerfLogIntervalMinSec);
            _mainLoopPerfLogIntervalMaxSec = Math.Max(_mainLoopPerfLogIntervalMinSec, settings.PerfLogIntervalMaxSec);

            _mainLoopTuneSampleWindow = Math.Clamp(settings.AutoTuneSampleWindow, 20, 240);
            _mainLoopTuneMinIntervalSec = Math.Clamp(settings.AutoTuneMinIntervalSec, 10, 300);

            _mainLoopWarnMs = Math.Clamp(_mainLoopBaseWarnMs, _mainLoopWarnMinMs, _mainLoopWarnMaxMs);
            _mainLoopPerfLogIntervalSec = Math.Clamp(_mainLoopBasePerfLogIntervalSec, _mainLoopPerfLogIntervalMinSec, _mainLoopPerfLogIntervalMaxSec);

            DateTime now = DateTime.UtcNow;
            _nextMainLoopPerfLogTime = now.AddSeconds(_mainLoopPerfLogIntervalSec);
            _nextMainLoopTuneTime = now.AddSeconds(_mainLoopTuneMinIntervalSec);
        }

        private void AutoTuneMainLoopPerf(int workMs)
        {
            if (!_mainLoopAutoTuneEnabled)
                return;

            _mainLoopRecentWorkSamples.Enqueue(workMs);
            while (_mainLoopRecentWorkSamples.Count > _mainLoopTuneSampleWindow)
            {
                _mainLoopRecentWorkSamples.Dequeue();
            }

            DateTime now = DateTime.UtcNow;
            if (now < _nextMainLoopTuneTime)
                return;

            int minSampleCount = Math.Min(20, Math.Max(10, _mainLoopTuneSampleWindow / 2));
            if (_mainLoopRecentWorkSamples.Count < minSampleCount)
                return;

            var ordered = _mainLoopRecentWorkSamples.OrderBy(v => v).ToArray();
            int p50 = GetPercentileValue(ordered, 0.50);
            int p90 = GetPercentileValue(ordered, 0.90);

            var settings = AppConfig.Current?.Trading?.PerformanceMonitoring;
            double multiplier = settings?.GetMainLoopMultiplier() ?? 1.30;

            int targetWarnMs = Math.Clamp(
                Math.Max(_mainLoopBaseWarnMs, (int)Math.Ceiling(p90 * multiplier)),
                _mainLoopWarnMinMs,
                _mainLoopWarnMaxMs);

            int targetIntervalSec = _mainLoopBasePerfLogIntervalSec;
            if (p90 >= targetWarnMs * 0.90 || p50 >= targetWarnMs * 0.60)
            {
                targetIntervalSec = Math.Max(_mainLoopPerfLogIntervalMinSec, _mainLoopBasePerfLogIntervalSec / 2);
            }
            else if (p90 <= targetWarnMs * 0.40)
            {
                targetIntervalSec = Math.Min(_mainLoopPerfLogIntervalMaxSec, _mainLoopBasePerfLogIntervalSec + 5);
            }

            targetIntervalSec = Math.Clamp(targetIntervalSec, _mainLoopPerfLogIntervalMinSec, _mainLoopPerfLogIntervalMaxSec);

            bool warnChanged = targetWarnMs != _mainLoopWarnMs;
            bool intervalChanged = targetIntervalSec != _mainLoopPerfLogIntervalSec;

            if (warnChanged || intervalChanged)
            {
                LoggerService.Info(
                    $"[PERF][MAIN_LOOP][AUTOTUNE] sample={ordered.Length} p50={p50}ms p90={p90}ms " +
                    $"warnMs={_mainLoopWarnMs}->{targetWarnMs} intervalSec={_mainLoopPerfLogIntervalSec}->{targetIntervalSec}");

                _mainLoopWarnMs = targetWarnMs;
                _mainLoopPerfLogIntervalSec = targetIntervalSec;
                _nextMainLoopPerfLogTime = now.AddSeconds(_mainLoopPerfLogIntervalSec);
            }

            _nextMainLoopTuneTime = now.AddSeconds(_mainLoopTuneMinIntervalSec);
        }

        private static int GetPercentileValue(int[] ordered, double percentile)
        {
            if (ordered.Length == 0)
                return 0;

            double clamped = Math.Clamp(percentile, 0, 1);
            int index = (int)Math.Ceiling(clamped * ordered.Length) - 1;
            index = Math.Clamp(index, 0, ordered.Length - 1);
            return ordered[index];
        }

        // ═══════════════════════════════════════════════════════════════
        // [v5.17.0 REDESIGN] 15-5-1 엔진 폴링 루프
        //   MarketDataManager 캐시에서 15m/5m/1m 캔들 확인 → 엔진 Layer 호출
        //   - 15m 종가 확정 → EvaluateRegime
        //   - 5m 종가 확정 → TryGenerateSignal
        //   - 1m 종가 확정 → TryTriggerEntry
        //
        //   폴링 간격: 3초 (1m 봉 종가 확정 감지에 충분, CPU 부담 낮음)
        // ═══════════════════════════════════════════════════════════════
        private async Task Run151EngineLoopAsync(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token); // 시작 직후 초기 데이터 안정화 대기
            // [v5.22.16] 폴링 간격 3초 → 30초 (멀티TF WebSocket 캐시 폐기 → REST throttle 30초와 동기화)
            //   효과: REST 호출 12심볼 × 3TF × 초당 = 초당 12회 → 30초당 12회 = 1초당 0.4회
            OnStatusLog?.Invoke("🎯 [ENGINE_151] 루프 시작 (15m/5m/1m 폴링 간격=30초, REST throttle 캐시)");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);

                    // 추적 대상: 심볼 리스트 + 감시풀 + top60 랭킹 (존재하면)
                    var symbolsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in _symbols) symbolsToScan.Add(s);
                    foreach (var kv in _pumpWatchPool) symbolsToScan.Add(kv.Key);

                    foreach (var symbol in symbolsToScan)
                    {
                        if (token.IsCancellationRequested) break;

                        // ── Layer 1: 15m 종가 확정 감지 + EvaluateRegime
                        // [v5.22.16] WebSocket 캐시 → REST throttle 30초 캐시
                        try
                        {
                            var c15 = await GetMultiTfKlinesThrottledAsync(
                                symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes, 60, token);
                            if (c15 != null && c15.Count >= 51)
                            {
                                var lastOpen = c15[^1].OpenTime;
                                if (!_last15mProcessedAt.TryGetValue(symbol, out var prev15) || prev15 != lastOpen)
                                {
                                    _last15mProcessedAt[symbol] = lastOpen;
                                    _entryEngine151.EvaluateRegime(symbol, c15);
                                }
                            }
                        }
                        catch { /* per-symbol skip */ }

                        // ── Layer 2: 5m 종가 확정 감지 + TryGenerateSignal
                        // [v5.22.16] 5분봉은 KlineCache (WebSocket) 우선 사용 (실시간), 미존재 시 REST
                        try
                        {
                            List<IBinanceKline>? c5 = null;
                            if (Services.MarketDataManager.Instance?.KlineCache.TryGetValue(symbol, out var cachedList) == true && cachedList.Count >= 30)
                            {
                                lock (cachedList) { c5 = cachedList.ToList(); }
                            }
                            if (c5 == null)
                            {
                                c5 = await GetMultiTfKlinesThrottledAsync(
                                    symbol, Binance.Net.Enums.KlineInterval.FiveMinutes, 60, token);
                            }
                            if (c5 != null && c5.Count >= 30)
                            {
                                var lastOpen = c5[^1].OpenTime;
                                if (!_last5mProcessedAt.TryGetValue(symbol, out var prev5) || prev5 != lastOpen)
                                {
                                    _last5mProcessedAt[symbol] = lastOpen;
                                    _entryEngine151.TryGenerateSignal(symbol, c5, out _);
                                }
                            }
                        }
                        catch { /* per-symbol skip */ }

                        // ── Layer 3: 1m 현재 봉 상태 + 최근 10봉으로 TryTriggerEntry
                        // [v5.22.16] WebSocket 캐시 → REST throttle 30초 캐시 (1m 봉 종가 확정 감지는 다소 늦어짐)
                        try
                        {
                            var c1 = await GetMultiTfKlinesThrottledAsync(
                                symbol, Binance.Net.Enums.KlineInterval.OneMinute, 15, token);
                            if (c1 != null && c1.Count >= 11)
                            {
                                bool isMajor = MajorSymbols.Contains(symbol);
                                _entryEngine151.TryTriggerEntry(symbol, c1[^1], c1, isMajor, out _);
                            }
                        }
                        catch { /* per-symbol skip */ }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [ENGINE_151] 루프 예외: {ex.Message}");
                }
            }

            OnStatusLog?.Invoke("🛑 [ENGINE_151] 루프 종료");
        }

        // [AI 제거] StartPeriodicTrainingAsync 전체 제거 — AI 재학습 루프 폐기

        // [AI 제거] StartPeriodicAiEntryProbScanAsync 전체 제거

        // [AI 제거] RetrainMlNetPredictorAsync 전체 제거 — AITrainer / AIPredictor 의존

        // [AI 제거] TriggerInitialMLNetTrainingIfNeededAsync 전체 제거 — AITrainer / AIPredictor 의존


        // [v5.22.13] StartOptionAInitialTrainingAsync / ForceInitialAiTrainingAsync 통째 제거 — AI 시스템 폐기 (2026-04-29)

        public async Task<string> ForceDroughtDiagnosticAsync(CancellationToken token = default)
        {
            if (!IsBotRunning || _cts == null || _cts.IsCancellationRequested)
            {
                return "⚠️ 엔진이 정지 상태라 드라이스펠 진단을 실행할 수 없습니다. 봇 시작 후 다시 시도하세요.";
            }

            // [AI 제거] RunDroughtDiagnosticScanAsync 본체 제거됨
            _lastDroughtScanTime = DateTime.Now;
            return "🔎 드라이스펠 진단 비활성화됨 (AI 시스템 제거)";
        }

        private async Task ProcessTickerChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var tick in _tickerChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        if (!TryNormalizeTradingSymbol(tick.Symbol, out var symbol))
                            continue;

                        // UI 업데이트 (스로틀링은 HandleTickerUpdate에서 이미 처리됨)
                        UpdateRealtimeProfit(symbol, tick.LastPrice);

                        // 전략 분석은 심볼별 최신가 coalescing 워커로 처리 (적체/지연 방지)
                        _pendingAnalysisPrices[symbol] = tick.LastPrice;
                        TryStartSymbolAnalysisWorker(symbol, token);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [TickerLoop] {tick.Symbol} 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [TickerLoop] 치명 루프 오류: {ex.Message}");
            }
        }

        private void TryStartSymbolAnalysisWorker(string symbol, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            if (!_analysisWorkers.TryAdd(symbol, 0))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!_pendingAnalysisPrices.TryRemove(symbol, out var price))
                            break;

                        await _analysisConcurrencyLimiter.WaitAsync(token);
                        try
                        {
                            await ProcessCoinAndTradeBySymbolAsync(symbol, price, token);
                        }
                        finally
                        {
                            _analysisConcurrencyLimiter.Release();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [TickerWorker] {symbol} 처리 오류: {ex.Message}");
                }
                finally
                {
                    _analysisWorkers.TryRemove(symbol, out _);

                    if (!token.IsCancellationRequested && _pendingAnalysisPrices.ContainsKey(symbol))
                        TryStartSymbolAnalysisWorker(symbol, token);
                }
            }, CancellationToken.None);
        }

        // [Agent 2] 계좌 업데이트 처리 소비자
        private async Task ProcessAccountChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var update in _accountChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        await HandleAccountUpdate(update);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [AccountLoop] 계좌 업데이트 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [AccountLoop] 치명 루프 오류: {ex.Message}");
            }
        }

        // [Agent 2] 주문 업데이트 처리 소비자
        private async Task ProcessOrderChannelAsync(CancellationToken token)
        {
            try
            {
                await foreach (var update in _orderChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        _positionMonitor.HandleOrderUpdate(update);

                        // [v5.3.7] SL/TP/Trailing API 체결 시 DB 매매기록 자동 저장
                        await RecordConditionalOrderFillAsync(update);
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [OrderLoop] 주문 업데이트 처리 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [OrderLoop] 치명 루프 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// [v5.3.7] API 조건부 주문(SL/TP/Trailing) 체결 시 DB TradeHistory에 매매기록 저장
        /// </summary>
        private async Task RecordConditionalOrderFillAsync(BinanceFuturesStreamOrderUpdate orderUpdate)
        {
            try
            {
                var data = orderUpdate.UpdateData;
                if (data.Status != Binance.Net.Enums.OrderStatus.Filled) return;

                // 조건부 주문 타입만 처리
                var orderType = data.Type;
                // [v5.3.9] 조건부 주문 체결 감지 — 일반 주문(LIMIT/MARKET) 제외, 나머지 전부 기록
                bool isNormalOrder = orderType == Binance.Net.Enums.FuturesOrderType.Limit
                                  || orderType == Binance.Net.Enums.FuturesOrderType.Market;
                if (isNormalOrder) return;

                // 주문 타입 분류 (로그용)
                bool isStopFill = orderType == Binance.Net.Enums.FuturesOrderType.StopMarket
                               || orderType == Binance.Net.Enums.FuturesOrderType.Stop;
                bool isTpFill = orderType == Binance.Net.Enums.FuturesOrderType.TakeProfitMarket
                              || orderType == Binance.Net.Enums.FuturesOrderType.TakeProfit;
                bool isTrailingFill = orderType == Binance.Net.Enums.FuturesOrderType.TrailingStopMarket;

                // 위 3개에 안 걸려도 조건부 주문이면 기록 (알 수 없는 타입 대비)
                OnStatusLog?.Invoke($"📋 [ORDER_FILL] {data.Symbol} type={orderType} status={data.Status} qty={data.AccumulatedQuantityOfFilledTrades} price={data.AveragePrice}");

                string symbol = data.Symbol;
                decimal filledQty = Math.Abs(data.AccumulatedQuantityOfFilledTrades);
                decimal avgPrice = data.AveragePrice > 0 ? data.AveragePrice : data.PriceLastFilledTrade;
                if (filledQty <= 0 || avgPrice <= 0) return;

                // 포지션 정보에서 진입가/방향 가져오기
                PositionInfo? pos = null;
                lock (_posLock) { _activePositions.TryGetValue(symbol, out pos); }

                // [v5.10.90] pos가 null인 경우 DB open TradeHistory에서 진입가 조회 (pnl 계산 정확도 개선)
                decimal entryPrice = pos?.EntryPrice ?? 0m;
                bool isLong = pos?.IsLong ?? (data.Side == Binance.Net.Enums.OrderSide.Sell);
                decimal leverage = pos?.Leverage ?? _settings.DefaultLeverage;
                if (entryPrice <= 0m)
                {
                    try
                    {
                        // [v5.10.90] pos=null 대비 GetOpenTradesAsync에서 symbol 찾아 entry 가져오기
                        int userId = AppConfig.CurrentUser?.Id ?? 1;
                        var openTrades = await _dbManager.GetOpenTradesAsync(userId);
                        var row = openTrades?.FirstOrDefault(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                        if (row.HasValue && row.Value.EntryPrice > 0)
                        {
                            entryPrice = row.Value.EntryPrice;
                            OnStatusLog?.Invoke($"ℹ️ [ORDER_FILL] {symbol} pos=null → DB open entry ${entryPrice:F6} 사용");
                        }
                        else entryPrice = avgPrice;
                    }
                    catch { entryPrice = avgPrice; }
                }

                // PnL 계산
                decimal rawPnl = isLong
                    ? (avgPrice - entryPrice) * filledQty
                    : (entryPrice - avgPrice) * filledQty;
                decimal fees = entryPrice * filledQty * 0.0004m + avgPrice * filledQty * 0.0004m;
                decimal pnl = rawPnl - fees;
                decimal pnlPct = entryPrice > 0 && filledQty > 0
                    ? (pnl / (entryPrice * filledQty)) * 100m * leverage : 0m;

                string exitReason = isStopFill ? "API_STOP_LOSS"
                    : isTpFill ? "API_TAKE_PROFIT"
                    : isTrailingFill ? "API_TRAILING_STOP"
                    : $"API_CONDITIONAL({orderType})";

                // [v5.10.54] SL 또는 Trailing 체결 → 잔여 조건부 주문 일괄 취소 (포지션 완전 청산)
                // TP1 부분 체결은 여기서 처리 안 함 (잔여 Trailing이 유효해야 함)
                if (_orderLifecycle != null && (isStopFill || isTrailingFill))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await _orderLifecycle.OnPositionClosedAsync(symbol); }
                        catch { }
                    });
                }

                string side = isLong ? "SELL" : "BUY"; // 청산 방향
                string strategy = pos?.IsPumpStrategy == true ? "PUMP_API" : "MAJOR_API";

                // TradeHistory에 청산 기록 저장
                var log = new TradeLog(symbol, side, strategy, avgPrice, pos?.AiScore ?? 0, DateTime.Now, pnl, pnlPct)
                {
                    EntryPrice = entryPrice,
                    ExitPrice = avgPrice,
                    Quantity = filledQty,
                    EntryTime = pos?.EntryTime ?? DateTime.Now,
                    ExitTime = DateTime.Now,
                    ExitReason = exitReason
                };
                await _dbManager.SaveTradeLogAsync(log);

                string emoji = pnl >= 0 ? "💰" : "📉";
                OnStatusLog?.Invoke($"{emoji} [API 체결] {symbol} {exitReason} | 체결가={avgPrice:F4} 수량={filledQty:F4} PnL={pnl:+0.00;-0.00} ({pnlPct:+0.0;-0.0}%)");
                // [v5.10.90] 텔레그램 알림은 DbManager (SaveTradeLogAsync → RecordPartial/Complete → TryNotifyProfit)에서 중앙 처리. 중복 제거.

                // [v5.10.96] API 체결로 포지션 완전 종료 시 UI 즉시 초기화 (실시간 시장 신호 DataGrid 행 청소)
                //   기존: ORDER_UPDATE 경로에서 OnPositionStatusUpdate 호출 누락 → 청산 후에도 EntryPrice/Qty/ROI 표시
                //   수정: SL 또는 Trailing 체결(완전 청산) 시 UI를 false로 갱신
                if (isStopFill || isTrailingFill)
                {
                    OnPositionStatusUpdate?.Invoke(symbol, false, 0);
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [API 체결 기록] 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 레버리지 기반 라벨링 + 파생 피처 계산
        /// - 20x 기준: 목표 +2.5% (ROE +50%), 손절 -1.0% (ROE -20%)
        /// - 10봉(50분) 이내 도달 여부로 LONG/SHORT/HOLD 분류
        /// - 왕복 수수료 0.08% 감안
        /// </summary>
        private List<CandleData> ConvertToTrainingData(List<IBinanceKline> klines, string symbol)
        {
            // [v5.10.82] 5분봉 단타 추론과 horizon 일치
            //   기존: LOOKAHEAD=10×1H=10시간, +2.5%/-1.0% (스윙 라벨) → 5분봉 추론에 부적합
            //   변경: LOOKAHEAD=6×5m=30분, +0.5%/-0.3% (단타 라벨) → TICK_SURGE/SPIKE 진입과 동일 horizon
            const int LOOKAHEAD = 6;            // 6봉 × 5min = 30분 horizon
            const decimal TARGET_PCT = 0.005m;  // +0.5% 단타 목표
            const decimal STOP_PCT = 0.003m;    // -0.3% 단타 손절
            const decimal FEE_PCT = 0.0008m;    // 왕복 수수료 0.08%
            const float BB_WIDTH_HOLD = 0.5f;  // BB폭 < 0.5% 이면 횡보 → HOLD

            static (bool success, decimal fib236, decimal fib382, decimal fib500, decimal fib618) CalculateConfirmedFibLevels(
                List<IBinanceKline> source,
                int lookback = 50,
                int confirmationBars = 3)
            {
                if (source == null || source.Count < lookback)
                    return (false, 0m, 0m, 0m, 0m);

                var recent = source.TakeLast(lookback).ToList();
                int maxConfirmedIndex = recent.Count - 1 - confirmationBars;
                if (maxConfirmedIndex <= confirmationBars)
                    return (false, 0m, 0m, 0m, 0m);

                var confirmed = recent.Take(maxConfirmedIndex + 1).ToList();
                if (confirmed.Count < 10)
                    return (false, 0m, 0m, 0m, 0m);

                decimal high = confirmed.Max(k => k.HighPrice);
                decimal low = confirmed.Min(k => k.LowPrice);
                decimal range = high - low;
                if (range <= 0m)
                    return (false, 0m, 0m, 0m, 0m);

                return (
                    true,
                    high - range * 0.236m,
                    high - range * 0.382m,
                    high - range * 0.500m,
                    high - range * 0.618m
                );
            }

            var result = new List<CandleData>();
            // SMA120 계산에 최소 120봉 필요, Lookahead 10봉 제외
            if (klines.Count < 130 + LOOKAHEAD) return result;

            // 거래량 이동평균용 사전 계산
            var volumes = klines.Select(k => (float)k.Volume).ToList();

            for (int i = 120; i < klines.Count - LOOKAHEAD; i++)
            {
                var subset = klines.GetRange(0, i + 1);
                var current = klines[i];
                decimal entryPrice = current.ClosePrice;

                // ── 기본 지표 ──
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);
                var confirmedFib = CalculateConfirmedFibLevels(subset, 50, 3);

                decimal fib236 = confirmedFib.success ? confirmedFib.fib236 : (decimal)fib.Level236;
                decimal fib382 = confirmedFib.success ? confirmedFib.fib382 : (decimal)fib.Level382;
                decimal fib500 = confirmedFib.success ? confirmedFib.fib500 : (decimal)fib.Level500;
                decimal fib618 = confirmedFib.success ? confirmedFib.fib618 : (decimal)fib.Level618;

                // ── SMA ──
                double sma20 = IndicatorCalculator.CalculateSMA(subset, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(subset, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(subset, 120);

                // ── 볼린저 밴드 파생 ──
                double bbMid = (bb.Upper + bb.Lower) / 2.0;
                float bbWidth = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0;
                float priceToBBMid = bbMid > 0 ? (float)(((double)entryPrice - bbMid) / bbMid * 100) : 0;

                // ── 가격 파생 ──
                float priceChangePct = current.OpenPrice > 0
                    ? (float)((entryPrice - current.OpenPrice) / current.OpenPrice * 100)
                    : 0;
                float priceToSMA20Pct = sma20 > 0
                    ? (float)(((double)entryPrice - sma20) / sma20 * 100)
                    : 0;

                // ── 캔들 패턴 ──
                decimal range = current.HighPrice - current.LowPrice;
                float bodyRatio = range > 0 ? (float)(Math.Abs(current.ClosePrice - current.OpenPrice) / range) : 0;
                float upperShadow = range > 0 ? (float)((current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice)) / range) : 0;
                float lowerShadow = range > 0 ? (float)((Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice) / range) : 0;

                // ── 거래량 분석 ──
                float vol20Avg = 0;
                if (i >= 20)
                {
                    for (int v = i - 19; v <= i; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[i] / vol20Avg : 1;
                float volumeChangePct = (i > 0 && volumes[i - 1] > 0)
                    ? (volumes[i] - volumes[i - 1]) / volumes[i - 1] * 100
                    : 0;

                // ── 피보나치 포지션 (0~1) ──
                float fibPosition = 0;
                if (fib236 != fib618 && fib618 > 0)
                    fibPosition = (float)((entryPrice - fib236) / (fib618 - fib236));
                fibPosition = Math.Clamp(fibPosition, 0, 1);

                // ── 추세 강도 (-1 ~ +1) ──
                float trendStrength = 0;
                if (sma20 > 0 && sma60 > 0 && sma120 > 0)
                {
                    if (sma20 > sma60 && sma60 > sma120) trendStrength = 1.0f;       // 정배열
                    else if (sma20 < sma60 && sma60 < sma120) trendStrength = -1.0f;  // 역배열
                    else trendStrength = (float)((sma20 - sma120) / sma120);           // 혼합
                    trendStrength = Math.Clamp(trendStrength, -1f, 1f);
                }

                // ── RSI 다이버전스 (단순: 가격↑+RSI↓ = 음, 가격↓+RSI↑ = 양) ──
                float rsiDivergence = 0;
                if (i >= 5)
                {
                    var prevSubset = klines.GetRange(0, i - 4);
                    var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                    float priceDelta = (float)(current.ClosePrice - klines[i - 5].ClosePrice);
                    float rsiDelta = (float)(rsi - prevRsi);
                    if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;  // 약세 다이버전스
                    else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1; // 강세 다이버전스
                }

                // ── 엘리엇 파동 상태 ──
                bool elliottBullish = IndicatorCalculator.AnalyzeElliottWave(subset);
                float elliottState = elliottBullish ? 1.0f : -1.0f;

                // ════════════════ 레버리지 기반 라벨링 ════════════════
                // LONG: 10봉 이내 +2.5% 도달이 -1.0% 보다 먼저 → 1
                // SHORT: 10봉 이내 -2.5% 도달이 +1.0% 보다 먼저 → 1
                decimal longTarget = entryPrice * (1 + TARGET_PCT + FEE_PCT);
                decimal longStop = entryPrice * (1 - STOP_PCT);
                decimal shortTarget = entryPrice * (1 - TARGET_PCT - FEE_PCT);
                decimal shortStop = entryPrice * (1 + STOP_PCT);

                float labelLong = 0, labelShort = 0, labelHold = 0;

                bool longResolved = false, shortResolved = false;
                for (int j = i + 1; j <= i + LOOKAHEAD && j < klines.Count; j++)
                {
                    var future = klines[j];
                    if (!longResolved)
                    {
                        if (future.HighPrice >= longTarget) { labelLong = 1; longResolved = true; }
                        else if (future.LowPrice <= longStop) { labelLong = 0; longResolved = true; }
                    }
                    if (!shortResolved)
                    {
                        if (future.LowPrice <= shortTarget) { labelShort = 1; shortResolved = true; }
                        else if (future.HighPrice >= shortStop) { labelShort = 0; shortResolved = true; }
                    }
                    if (longResolved && shortResolved) break;
                }

                // HOLD: BB폭이 너무 좁으면 횡보장 → 진입 비추천
                if (bbWidth < BB_WIDTH_HOLD) labelHold = 1;

                // 기존 호환 라벨 (legacy)
                bool legacyLabel = klines[i + 1].ClosePrice > entryPrice;

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,

                    // 기본 보조지표
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib236,
                    Fib_382 = (float)fib382,
                    Fib_500 = (float)fib500,
                    Fib_618 = (float)fib618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,

                    // SMA
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,

                    // 파생 피처
                    Price_Change_Pct = priceChangePct,
                    Price_To_BB_Mid = priceToBBMid,
                    BB_Width = bbWidth,
                    Price_To_SMA20_Pct = priceToSMA20Pct,
                    Candle_Body_Ratio = bodyRatio,
                    Upper_Shadow_Ratio = upperShadow,
                    Lower_Shadow_Ratio = lowerShadow,
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct,
                    Fib_Position = fibPosition,
                    Trend_Strength = trendStrength,
                    RSI_Divergence = rsiDivergence,
                    ElliottWaveState = elliottState,
                    SentimentScore = 0, // 학습 데이터에서는 뉴스 감성 없음

                    // OI / 펀딩레이트 (학습 데이터 - oiCollector에서 조회)
                    OpenInterest = _oiCollector != null ? (float)_oiCollector.GetOiAtTime(symbol, current.OpenTime) : 0,
                    OI_Change_Pct = _oiCollector != null ? (float)(_oiCollector.GetOiChangeAtTime(symbol, current.OpenTime)) : 0,
                    FundingRate = 0, // 과거 펀딩레이트는 별도 수집 필요
                    SqueezeLabel = 0, // SqueezeLabeller에서 별도 후처리

                    // 레이블
                    Label = legacyLabel,
                    LabelLong = labelLong,
                    LabelShort = labelShort,
                    LabelHold = labelHold,
                });
            }
            return result;
        }

        private void UpdateRealtimeProfit(string symbol, decimal currentPrice)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                return;

            // 종목이 리스트에 없으면 추가 요청 (심볼당 1회만)
            if (_uiTrackedSymbols.TryAdd(normalizedSymbol, 0))
                OnSymbolTracking?.Invoke(normalizedSymbol);

            if (currentPrice <= 0)
            {
                OnTickerUpdate?.Invoke(normalizedSymbol, currentPrice, null);
                return;
            }

            PositionInfo? pos = null;
            bool isHolding = false;

            lock (_posLock) { isHolding = _activePositions.TryGetValue(normalizedSymbol, out pos); }

            double? pnl = null;
            if (isHolding && pos != null && pos.EntryPrice > 0)
            {
                decimal priceChangePercent = 0;
                if (pos.IsLong)
                {
                    priceChangePercent = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100;
                }
                else
                {
                    priceChangePercent = (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100;
                }
                decimal safeLeverage = pos.Leverage > 0
                    ? pos.Leverage
                    : (_settings.DefaultLeverage > 0 ? _settings.DefaultLeverage : 1m);
                // ROE = 가격변동률 × 레버리지
                decimal calculatedROE = priceChangePercent * safeLeverage;
                pnl = (double)Math.Round(calculatedROE, 2);
            }

            OnTickerUpdate?.Invoke(normalizedSymbol, currentPrice, pnl);
        }
        // ═══════════════════════════════════════════════════════════════════════════════════
        // [v5.21.9] ActiveTrackingPool — 메이저 4 + 동적 8 = 12개만 실시간 평가
        //   원인: 200+ 심볼 매 사이클 평가 → ML.NET 추론 + 게이트 검증 폭주 → CPU/메모리 폭주
        //   해결: 메이저 4 고정 + PumpScan top score 상위 8 동적 선택 = 12개만 진입 분석
        //   효과: ML.NET 추론 200+ → 12 (94% 감소), 메인 루프 workMs 1050ms → ~150ms 예상
        // ═══════════════════════════════════════════════════════════════════════════════════
        private static readonly string[] FixedMajorPool = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        // [v5.22.33] B+C — 동적 알트 풀 8 → 20 확대 (스퀴즈/엘리엇 신호 발생 빈도 증가)
        //   원인: 거래대금 큰 메인 알트는 변동성 압축 (BBW < 평균 50%) 조건 거의 안 맞음
        //   해결: 풀 크기 확대 + 변동성 기준 선정 (이미 EnsureActiveTrackingPoolFresh 는 |PriceChangePercent| desc)
        private const int DynamicPoolSize = 20;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeTrackingPool = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastTrackingPoolRefresh = DateTime.MinValue;
        private static readonly TimeSpan TrackingPoolRefreshInterval = TimeSpan.FromMinutes(5);

        // [v5.22.16] _lastBbSqueezeRestTime 제거 — GetMultiTfKlinesThrottledAsync 가 30초 throttle 통합 관리

        // [v5.22.16] 멀티TF WebSocket 캐시 폐기 → REST throttle (심볼+TF별 30초 1회)
        //   - HIGH_TOP_CHASING/SIDEWAYS_BOX 가드 (15m 30봉) — IsEntryAllowed 내부
        //   - ENGINE_151 Layer 1/2/3 (15m / 5m / 1m) — Run151EngineLoopAsync
        //   캐시 키: "{symbol}|{interval}", value: (마지막 REST 시각, 결과 List)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ts, List<IBinanceKline> klines)> _multiTfRestCache
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan MultiTfRestThrottle = TimeSpan.FromSeconds(30);

        /// <summary>
        /// [v5.22.16] 멀티TF WebSocket 캐시 대체 — REST 호출 + 30초 throttle 캐싱 (async)
        /// 캐시 hit 시 즉시 반환. miss 시 REST 1회 후 캐시.
        /// </summary>
        private async Task<List<IBinanceKline>?> GetMultiTfKlinesThrottledAsync(
            string symbol, KlineInterval interval, int limit, CancellationToken token)
        {
            string key = $"{symbol}|{interval}";
            var nowUtc = DateTime.UtcNow;
            if (_multiTfRestCache.TryGetValue(key, out var cached)
                && (nowUtc - cached.ts) < MultiTfRestThrottle
                && cached.klines.Count >= limit)
            {
                return cached.klines;
            }
            try
            {
                var fetched = await _exchangeService.GetKlinesAsync(symbol, interval, limit, token);
                if (fetched == null) return cached.klines; // null 시 직전 캐시 재활용
                var list = fetched.ToList();
                _multiTfRestCache[key] = (nowUtc, list);
                return list;
            }
            catch
            {
                return cached.klines; // throttle 직후 fail 시 직전 캐시 재활용 (있다면)
            }
        }

        /// <summary>
        /// [v5.22.16] sync 가드 (IsEntryAllowedCore) 전용 — 캐시만 조회 (없으면 백그라운드 refresh + null 반환)
        /// 첫 호출은 가드 skip (정상). 두 번째부터 throttle 캐시 hit.
        /// </summary>
        private List<IBinanceKline>? GetMultiTfKlinesCachedOrRefresh(
            string symbol, KlineInterval interval, int limit)
        {
            string key = $"{symbol}|{interval}";
            var nowUtc = DateTime.UtcNow;
            if (_multiTfRestCache.TryGetValue(key, out var cached)
                && cached.klines.Count >= limit)
            {
                // 캐시 만료 시 백그라운드 새로고침 (반환은 stale 그대로)
                if ((nowUtc - cached.ts) >= MultiTfRestThrottle)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fetched = await _exchangeService.GetKlinesAsync(symbol, interval, limit, default);
                            if (fetched != null)
                                _multiTfRestCache[key] = (DateTime.UtcNow, fetched.ToList());
                        }
                        catch { /* 무시 */ }
                    });
                }
                return cached.klines;
            }
            // 캐시 미존재 — 백그라운드 fetch 후 다음 호출에 사용
            if (!_multiTfRestCache.ContainsKey(key) || (nowUtc - cached.ts) >= MultiTfRestThrottle)
            {
                _multiTfRestCache[key] = (nowUtc, cached.klines ?? new List<IBinanceKline>()); // 마커 (재진입 방지)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fetched = await _exchangeService.GetKlinesAsync(symbol, interval, limit, default);
                        if (fetched != null)
                            _multiTfRestCache[key] = (DateTime.UtcNow, fetched.ToList());
                    }
                    catch { /* 무시 */ }
                });
            }
            return null;
        }

        // [v5.22.10] 메모리 누수 방지 — 5분 주기 cleanup
        private DateTime _lastMemoryCleanupTime = DateTime.MinValue;

        private void CleanupExpiredCaches()
        {
            try
            {
                var now = DateTime.UtcNow;
                int totalRemoved = 0;
                // TTL 30분 만료 항목 제거 — DateTime 단일 값 dictionary
                int Cleanup(System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> dict, TimeSpan ttl)
                {
                    int removed = 0;
                    foreach (var kv in dict.ToArray())
                    {
                        if ((now - kv.Value.ToUniversalTime()) > ttl)
                        {
                            if (dict.TryRemove(kv.Key, out _)) removed++;
                        }
                    }
                    return removed;
                }
                totalRemoved += Cleanup(_blacklistedSymbols, TimeSpan.FromMinutes(60));
                totalRemoved += Cleanup(_stopLossCooldown, TimeSpan.FromMinutes(60));
                totalRemoved += Cleanup(_recentEntryAttempts, TimeSpan.FromMinutes(30));
                totalRemoved += Cleanup(_recentlyClosedCooldown, TimeSpan.FromMinutes(60));
                totalRemoved += Cleanup(_recentPartialCloseCooldown, TimeSpan.FromMinutes(60));
                totalRemoved += Cleanup(_lastTickerUpdateTimes, TimeSpan.FromHours(2));
                totalRemoved += Cleanup(_lastAnalysisTimes, TimeSpan.FromHours(2));
                totalRemoved += Cleanup(_last5mProcessedAt, TimeSpan.FromHours(2));
                totalRemoved += Cleanup(_last15mProcessedAt, TimeSpan.FromHours(2));
                totalRemoved += Cleanup(_lastMlMonitorRecordTimes, TimeSpan.FromHours(2));
                totalRemoved += Cleanup(_simpleAiBackfilled, TimeSpan.FromHours(48));
                totalRemoved += Cleanup(_slotBlockedSymbols, TimeSpan.FromMinutes(60));
                totalRemoved += Cleanup(_manualCloseCooldown, TimeSpan.FromMinutes(60));

                // (Score, DateTime) tuple dictionary
                int CleanupTuple<TVal>(System.Collections.Concurrent.ConcurrentDictionary<string, TVal> dict, Func<TVal, DateTime> getDate, TimeSpan ttl)
                {
                    int removed = 0;
                    foreach (var kv in dict.ToArray())
                    {
                        if ((now - getDate(kv.Value).ToUniversalTime()) > ttl)
                        {
                            if (dict.TryRemove(kv.Key, out _)) removed++;
                        }
                    }
                    return removed;
                }
                totalRemoved += CleanupTuple(_directionBiasCache, v => v.time, TimeSpan.FromMinutes(30));
                totalRemoved += CleanupTuple(_aiApprovedRecentScores, v => v.Time, TimeSpan.FromMinutes(60));
                totalRemoved += CleanupTuple(_signalOriginPrice, v => v.Time, TimeSpan.FromMinutes(60));

                // _pumpPriorityQueue (List) 비우기 — PumpScan 비활성화됨
                lock (_pumpPriorityQueue) { _pumpPriorityQueue.Clear(); }

                // [v5.22.16] 멀티TF REST throttle 캐시 cleanup — TTL 5분
                int multiTfRemoved = 0;
                foreach (var kv in _multiTfRestCache.ToArray())
                {
                    if ((now - kv.Value.ts) > TimeSpan.FromMinutes(5))
                    {
                        if (_multiTfRestCache.TryRemove(kv.Key, out _)) multiTfRemoved++;
                    }
                }
                totalRemoved += multiTfRemoved;

                if (totalRemoved > 0)
                    OnStatusLog?.Invoke($"🧹 [메모리cleanup] {totalRemoved}개 만료 항목 제거 (5분 주기)");

                // GC 명시 호출 (LOH 정리)
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [메모리cleanup] 예외: {ex.Message}");
            }
        }

        private void EnsureActiveTrackingPoolFresh()
        {
            // 메이저 4개는 항상 포함 (idempotent)
            foreach (var m in FixedMajorPool) _activeTrackingPool.TryAdd(m, 0);

            if ((DateTime.Now - _lastTrackingPoolRefresh) < TrackingPoolRefreshInterval && _activeTrackingPool.Count > 4)
                return;

            _lastTrackingPoolRefresh = DateTime.Now;

            // [v5.22.10] 동적 8개 = TickerCache 기반 (PumpScan 비활성화 후 대체)
            //   PumpScan TopCandidateScores 가 비어있으므로 TickerCache의 24h 가격 변동률 큰 순으로 선택
            //   장점: API 호출 0회, 알트/밈 데이터 자동 추적
            var dynamicSymbols = new List<string>();
            if (_marketDataManager?.TickerCache != null)
            {
                HashSet<string> activeSet;
                lock (_posLock) { activeSet = new HashSet<string>(_activePositions.Keys, StringComparer.OrdinalIgnoreCase); }

                // 24h 가격 변동률 절댓값 큰 순 (상승/하락 모두 추적)
                var sorted = _marketDataManager.TickerCache.Values
                    .Where(t => !string.IsNullOrWhiteSpace(t.Symbol)
                                && t.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                                && !FixedMajorPool.Contains(t.Symbol))
                    .OrderByDescending(t => Math.Abs(t.PriceChangePercent))
                    .Select(t => t.Symbol)
                    .ToList();

                foreach (var sym in sorted)
                {
                    if (activeSet.Contains(sym)) continue;
                    dynamicSymbols.Add(sym);
                    if (dynamicSymbols.Count >= DynamicPoolSize) break;
                }
            }

            var newPool = new HashSet<string>(FixedMajorPool, StringComparer.OrdinalIgnoreCase);
            foreach (var s in dynamicSymbols) newPool.Add(s);

            // 기존 풀에서 빠진 심볼 제거 (활성 포지션은 별도 경로로 보호되므로 풀에서 빼도 안전)
            foreach (var existing in _activeTrackingPool.Keys.ToList())
            {
                if (!newPool.Contains(existing))
                    _activeTrackingPool.TryRemove(existing, out _);
            }
            foreach (var added in newPool)
                _activeTrackingPool.TryAdd(added, 0);

            OnStatusLog?.Invoke($"🎯 [추적풀] 갱신 — 메이저4 + 동적{dynamicSymbols.Count}개 = 총 {_activeTrackingPool.Count}개 ({string.Join(",", dynamicSymbols)})");

            // [AI 제거] AI Gate 동기화 제거
        }

        private async Task ProcessCoinAndTradeBySymbolAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // [v5.21.9] 추적 풀 가드 — 12개 외 심볼은 평가 즉시 스킵
                //   활성 포지션 심볼은 풀에 없어도 통과 (TP/SL/Trailing 보호 위해 _activePositions 체크)
                EnsureActiveTrackingPoolFresh();
                if (!_activeTrackingPool.ContainsKey(symbol))
                {
                    bool hasActivePos;
                    lock (_posLock) { hasActivePos = _activePositions.ContainsKey(symbol); }
                    if (!hasActivePos) return;
                }

                if (IsEntryWarmupActive(out var remaining))
                {
                    if ((DateTime.Now - _lastEntryWarmupLogTime).TotalSeconds >= 10)
                    {
                        _lastEntryWarmupLogTime = DateTime.Now;
                        OnStatusLog?.Invoke(TradingStateLogger.EntryWarmupActive((int)remaining.TotalSeconds));
                    }
                    return;
                }

                var now = DateTime.Now;
                bool isMajorSymbol = MajorSymbols.Contains(symbol);
                int minAnalysisIntervalMs = isMajorSymbol
                    ? MAJOR_SYMBOL_ANALYSIS_MIN_INTERVAL_MS
                    : SYMBOL_ANALYSIS_MIN_INTERVAL_MS;

                if (_lastAnalysisTimes.TryGetValue(symbol, out var lastTime))
                {
                    if ((now - lastTime).TotalMilliseconds < minAnalysisIntervalMs) return;
                }
                _lastAnalysisTimes[symbol] = now;

                // [ETA 자동 트리거] 스케줄된 진입 재평가 시간 도래 확인
                if (_scheduledEtaReEvaluations.TryGetValue(symbol, out var scheduledTime))
                {
                    if (now >= scheduledTime)
                    {
                        _scheduledEtaReEvaluations.TryRemove(symbol, out _);
                        OnStatusLog?.Invoke($"🎯 [ETA_TRIGGER] {symbol} 예약된 진입 시간 도달 → 재평가 시작 ({scheduledTime:HH:mm})");
                        
                        // [AI 제거] ETA_TRIGGER 시점 AI Gate 재평가 제거 — 즉시 시장가 진입 시도
                        try
                        {
                            decimal entryPrice = 0m;
                            if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tk))
                                entryPrice = tk.LastPrice;
                            if (entryPrice > 0m)
                            {
                                _ = ExecuteAutoOrder(symbol, "LONG", entryPrice, token,
                                    signalSource: "ETA_TRIGGER",
                                    skipAiGateCheck: false);
                            }
                        }
                        catch { }
                    }
                }

                // [v5.22.9] ML.NET 자동 예측 비활성화 — AI 시스템 폐기 (2026-04-28)
                // _ = TryRecordMlNetPredictionFromCommonScanAsync(symbol, currentPrice, token);

                // 1. 그리드 전략 (횡보장 대응)
                await _gridStrategy.ExecuteAsync(symbol, currentPrice, token);
                // 2. 차익거래 전략 (거래소 간 가격 차이 감지)
                await _arbitrageStrategy.AnalyzeAsync(symbol, currentPrice, token);

                // [MAJOR 전략] 메이저 코인 분석 — [v5.10.81] 단일 게이트 IsEntryAllowed
                if (isMajorSymbol && _majorStrategy != null)
                {
                    if (!IsEntryAllowed(symbol, "MAJOR_ANALYZE", out _))
                    {
                        // 설정에서 메이저 비활성화됨 → 진입 분석 스킵
                    }
                    else
                    {
                        await _majorStrategy.AnalyzeAsync(symbol, currentPrice, token);
                    }
                }

                // [Phase 7] Transformer 전략 분석 실행
                /* TensorFlow 전환 중 비활성화
                if (_transformerStrategy != null)
                    await _transformerStrategy.AnalyzeAsync(symbol, currentPrice, token);
                */

                // [3파 확정형 전략] 5분봉 엘리엇 파동 분석
                try
                {
                    await AnalyzeElliottWave3WaveAsync(symbol, currentPrice, token);
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ Elliott Wave 분석 오류: {ex.Message}");
                }

                // [15분봉 BB 스퀴즈 돌파 전략]
                try
                {
                    await AnalyzeFifteenMinBBSqueezeBreakoutAsync(symbol, currentPrice, token);
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ BB 스퀴즈 분석 오류: {ex.Message}");
                }

                // [v5.22.35] 알트용 단순 SQUEEZE + BB_WALK — 백테스트 (RunDaily60Async) 검증된 로직 이식
                //   사용자: '알트 20% 넘는 코인 10개+, 10% 넘는 코인 수십개인데 진입 0건은 말이 안됨'
                //   기존: AnalyzeFifteenMinBBSqueezeBreakoutAsync 의 5중 조건 (BBW<평균50%, 거래량1.5x, RSI정렬, R:R 1.5:1) 너무 까다로움
                //   신규: 백테스트 트리거 그대로 — BBW<1.5% + 상단돌파 (SQUEEZE), 4회 연속 상단워킹 (BB_WALK)
                //         + EMA20↑ + RSI<65 가드 + TP 1%/SL 3% (백테스트 동일)
                try
                {
                    if (!isMajorSymbol)
                        await AnalyzeAltSimpleTriggersAsync(symbol, currentPrice, token);
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ 알트 단순 트리거 오류: {ex.Message}");
                }

                // [v5.22.36] 반대 시그널 익절 — 활성 LONG 포지션이 ROE>+0.3% 인데 EMA20 하락전환 + RSI<50 → 즉시 청산
                //   사용자: '이더/솔라나 너무 긴시간 끌다보니 수익이 마이너스 됨'
                //   목표: 흑자 구간에서 추세 반전 신호 잡으면 익절 보호
                try
                {
                    await CheckReverseSignalExitAsync(symbol, currentPrice, token);
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ 반대시그널 청산 오류: {ex.Message}");
                }

                // ═══════════════════════════
                // [Hybrid Exit] AI+지표 기반 이탈 관리
                // ═══════════════════════════
                await CheckHybridExitAsync(symbol, currentPrice, token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TradingEngine] TickerProcessing failed for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// 하이브리드 AI+지표 기반 이탈 체크
        /// ─────────────────────────────────
        /// HybridExitManager에 등록된 포지션에 대해
        /// 5분봉 BB/RSI를 계산하여 이탈 조건을 확인합니다.
        /// </summary>
        private async Task CheckHybridExitAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            if (_hybridExitManager == null || !_hybridExitManager.HasState(symbol)) return;

            // [수정] 실제 포지션이 있는지 확인 (없으면 state 정리하고 종료)
            bool hasPosition = false;
            lock (_posLock)
            {
                hasPosition = _activePositions.ContainsKey(symbol);
            }

            if (!hasPosition)
            {
                // 포지션은 없는데 state가 남아있는 경우 정리
                _hybridExitManager.RemoveState(symbol);
                return;
            }

            try
            {
                // 5분봉 데이터로 BB/RSI 계산
                var kRes = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 30, ct: token);
                if (!kRes.Success || kRes.Data == null || kRes.Data.Length < 20) return;

                var klines = kRes.Data.ToList();

                double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
                var bb = IndicatorCalculator.CalculateBB(klines, 20, 2);
                double atr = IndicatorCalculator.CalculateATR(klines, 14); // ATR 계산 추가

                // AI 재예측 (optional, TransformerTrainer 기반)
                decimal? newPrediction = null;
                /* TensorFlow 전환 중 비활성화
                if (_transformerStrategy != null)
                {
                    try
                    {
                        var state = _hybridExitManager.GetState(symbol);
                        // 재예측은 60초마다만 수행 (API 부하 방지)
                        if (state != null && (DateTime.Now - state.EntryTime).TotalSeconds > 60)
                        {
                            // TransformerStrategy가 내부적으로 _trainer.Predict를 호출하므로
                            // 여기서는 ML.NET AIPredictor를 사용한 방향 재확인
                            // [FIX] klines가 비어있지 않은지 확인 - 완전한 feature 계산 사용
                            if (_aiPredictor != null && klines.Any())
                            {
                                try
                                {
                                    // 재예측은 완전한 CandleData 생성 필요 (학습 피처와 동일)
                                    var candleData = await GetLatestCandleDataAsync(symbol, _cts?.Token ?? token);
                                    if (candleData != null)
                                    {
                                        var pred = _aiPredictor.Predict(candleData);
                                        if (pred != null)
                                        {
                                            // Prediction (bool) 기반 방향성 판단 - Score는 raw margin이라 부적합
                                            decimal predDirection = pred.Prediction
                                                ? currentPrice * 1.005m  // 상승 예측 → 현재가+0.5%
                                                : currentPrice * 0.995m; // 하락 예측 → 현재가-0.5%
                                            newPrediction = predDirection;
                                        }
                                    }
                                }
                                catch (Exception predEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[CheckHybridExit] AI 재예측 실패: {predEx.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception stateEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CheckHybridExit] State access 실패: {stateEx.Message}");
                    }
                }
                */

                // [v3.6.1] HybridExitManager 완전 비활성화 — PositionMonitor가 단독 담당
                // 문제: HybridExit의 절대 손절(-20% ROE)이 PositionMonitor(-25%)보다 먼저 발동
                // → 진입 즉시 -20%에서 청산, PUMP 코인 전부 손절
                // ATR 트레일링은 v3.4.0에서 이미 제거, 절대 손절도 제거
                /*
                var exitAction = _hybridExitManager.CheckExit(
                    symbol,
                    currentPrice,
                    rsi,
                    bb.Upper,
                    bb.Mid,
                    bb.Lower,
                    atr,
                    newPrediction,
                    emitAlerts: false);

                if (exitAction != null)
                {
                    bool hasPositionNow;
                    lock (_posLock)
                    {
                        hasPositionNow = _activePositions.TryGetValue(symbol, out var posNow) && Math.Abs(posNow.Quantity) > 0;
                    }

                    if (!hasPositionNow)
                    {
                        _hybridExitManager.RemoveState(symbol);
                        return;
                    }

                    if (exitAction.ActionType == ExitActionType.PartialClose50Pct)
                    {
                        if (await _positionMonitor.ExecutePartialClose(symbol, 0.5m, token))
                            OnAlert?.Invoke($"💰 [Hybrid] {symbol} 50% 부분 익절 실행 | {exitAction.Reason} | ROE: {exitAction.ROE:F1}%");
                    }
                    else if (exitAction.ActionType == ExitActionType.FullClose)
                    {
                        await _positionMonitor.ExecuteMarketClose(symbol, $"Hybrid Exit: {exitAction.Reason}", token);
                        _hybridExitManager.RemoveState(symbol);
                        bool stillHasPosition;
                        lock (_posLock)
                        {
                            stillHasPosition = _activePositions.TryGetValue(symbol, out var posAfter) && Math.Abs(posAfter.Quantity) > 0;
                        }

                        if (!stillHasPosition)
                        {
                            OnAlert?.Invoke($"🎯 [Hybrid] {symbol} 전량 청산 실행 | {exitAction.Reason} | ROE: {exitAction.ROE:F1}%");
                        }
                    }
                }
                */
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [Hybrid Exit] {symbol} 체크 에러: {ex.Message}");
            }
        }

        /// <summary>
        /// 엘리엇 3파 확정형 전략 분석
        /// 5분봉에서 1파→2파→3파 확정 시 자동  진입
        /// </summary>
        private async Task AnalyzeElliottWave3WaveAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // 5분봉 최근 20개 가져오기 (약 100분)
                var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    limit: 20,
                    ct: token
                );

                if (!result.Success || result.Data == null || result.Data.Length < 5)
                    return;

                var candles = result.Data.ToList();

                // 지표 계산
                var rsiValues = new List<double>();
                var macdValues = new List<(double macd, double signal)>();

                for (int i = 0; i < candles.Count; i++)
                {
                    var subset = candles.GetRange(0, i + 1);
                    double rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                    rsiValues.Add(rsi);

                    var macd = IndicatorCalculator.CalculateMACD(subset);
                    macdValues.Add((macd.Macd, macd.Signal));
                }

                var bbAnalysis = IndicatorCalculator.CalculateBB(candles.ToList(), 20, 2);
                
                // [안전성 체크] 배열 인덱스 접근 전 Count 확인
                if (candles.Count == 0 || rsiValues.Count == 0 || macdValues.Count == 0)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} ElliottWave3Wave 데이터 부족 (candles: {candles.Count}, rsi: {rsiValues.Count}, macd: {macdValues.Count})");
                    return;
                }
                
                var currentCandle = new CandleData
                {
                    Symbol = symbol,
                    Open = (decimal)candles[^1].OpenPrice,
                    High = (decimal)candles[^1].HighPrice,
                    Low = (decimal)candles[^1].LowPrice,
                    Close = (decimal)candles[^1].ClosePrice,
                    Volume = (float)candles[^1].Volume,
                    RSI = (float)rsiValues[^1],
                    BollingerUpper = (float)bbAnalysis.Upper,
                    BollingerLower = (float)bbAnalysis.Lower,
                    OpenTime = candles[^1].OpenTime,
                    CloseTime = candles[^1].CloseTime
                };

                double currentRsi = rsiValues[^1];
                var currentMacd = macdValues[^1];

                // CandleData 리스트로 변환 (캔들 분석용)
                var candleDataList = new List<CandleData>();
                for (int i = 0; i < candles.Count; i++)
                {
                    candleDataList.Add(new CandleData
                    {
                        Symbol = symbol,
                        Open = (decimal)candles[i].OpenPrice,
                        High = (decimal)candles[i].HighPrice,
                        Low = (decimal)candles[i].LowPrice,
                        Close = (decimal)candles[i].ClosePrice,
                        Volume = (float)candles[i].Volume,
                        OpenTime = candles[i].OpenTime,
                        CloseTime = candles[i].CloseTime
                    });
                }

                var state = _elliotWave3Strategy.GetCurrentState(symbol);
                string stateSignatureBefore = BuildElliottWavePersistenceSignature(state);

                // [1단계] 1파 상승 감지
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Idle && candleDataList.Count >= 3)
                {
                    if (_elliotWave3Strategy.DetectWave1(symbol, candleDataList, candleDataList.Count - 1))
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [1파 확정] 거래량 실린 강한 상승 감지!");
                        OnStatusLog?.Invoke($"📈 {symbol} 1파: {state.Phase1LowPrice:F8} → {state.Phase1HighPrice:F8}");
                    }
                }

                // [2단계] 2파 조정 감지 및 피보나치 설정
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave1Started && candleDataList.Count >= 4)
                {
                    if (_elliotWave3Strategy.DetectWave2AndSetFibonacci(symbol, candleDataList, candleDataList.Count - 1))
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [2파 확정] 조정파 감지, 피보나치 설정 완료");
                        OnStatusLog?.Invoke($"📉 {symbol} 2파 Fib: 0.618={state.Fib0618Level:F8}, 0.786={state.Fib786Level:F8}");
                    }
                }

                // [3단계] RSI 다이버전스 감지
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave2Started)
                {
                    var rsiValuesDecimal = rsiValues.Select(r => (decimal)r).ToList();
                    if (_elliotWave3Strategy.DetectRSIDivergence(symbol, candleDataList, rsiValuesDecimal))
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [RSI 다이버전스] 상승 반전 신호 감지!");
                        OnStatusLog?.Invoke($"📊 {symbol} RSI 다이버전스: {currentRsi:F1}");
                    }
                }

                // [4단계] 진입 신호 확정
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave3Setup)
                {
                    bool entry = _elliotWave3Strategy.ConfirmEntry(
                        symbol,
                        currentCandle,
                        (decimal)currentRsi,
                        (decimal)currentMacd.macd,
                        (decimal)currentMacd.signal,
                        (decimal)bbAnalysis.Mid,
                        (decimal)bbAnalysis.Lower,
                        (decimal)bbAnalysis.Upper
                    );

                    if (entry)
                    {
                        OnAlert?.Invoke($"🌊 {symbol} [3파 진입 신호] 모든 조건 충족! 자동 진입합니다.");
                        OnStatusLog?.Invoke($"✅ {symbol} 진입가: {currentPrice:F8}, RSI: {currentRsi:F1}, MACD: {currentMacd.macd:F6}");

                        // 자동 진입 실행
                        await ExecuteAutoOrder(symbol, "LONG", currentPrice, token, "ElliottWave3Wave");

                        bool enteredWave3Position;
                        lock (_posLock)
                        {
                            enteredWave3Position = _activePositions.TryGetValue(symbol, out var activePos)
                                && Math.Abs(activePos.Quantity) > 0;
                        }

                        if (enteredWave3Position)
                        {
                            _elliotWave3Strategy.MarkWave3Active(symbol);
                        }

                        // 손절/익절 정보 로그
                        var (tp1, tp2) = _elliotWave3Strategy.GetTakeProfits(symbol);
                        var sl = _elliotWave3Strategy.GetStopLoss(symbol);
                        OnAlert?.Invoke($"🎯 목표가: 1차 {tp1:F8}, 2차 {tp2:F8} | 손절: {sl:F8}");
                    }
                }

                // [조기 익절] RSI 또는 BB 경고 신호
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave3Active)
                {
                    double prevRsi = rsiValues.Count >= 2 ? rsiValues[^2] : currentRsi;
                    if (_elliotWave3Strategy.ShouldTakeProfitEarly(symbol, (decimal)currentRsi, (decimal)prevRsi, currentPrice, (decimal)bbAnalysis.Upper))
                    {
                        OnAlert?.Invoke($"⚠️ {symbol} [조기 익절 경고] RSI 또는 BB 반전 신호!");
                    }
                }

                string stateSignatureAfter = BuildElliottWavePersistenceSignature(state);
                if (!string.Equals(stateSignatureBefore, stateSignatureAfter, StringComparison.Ordinal))
                {
                    await PersistElliottWaveAnchorStateAsync(symbol);
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} ElliottWave3Wave 분석 오류: {ex.Message}");
            }
        }

        private async Task RestoreElliottWaveAnchorsFromDatabaseAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                if (_elliotWave3Strategy == null || _dbManager == null)
                    return;

                var snapshots = await _dbManager.LoadElliottWaveAnchorStatesAsync(_symbols);
                if (snapshots == null || snapshots.Count == 0)
                {
                    OnStatusLog?.Invoke("ℹ️ [ElliottAnchor] 복원할 앵커가 없습니다.");
                    return;
                }

                int restored = 0;
                foreach (var snapshot in snapshots)
                {
                    token.ThrowIfCancellationRequested();

                    if (_elliotWave3Strategy.RestorePersistentState(snapshot))
                        restored++;
                }

                OnStatusLog?.Invoke($"✅ [ElliottAnchor] DB에서 {restored}개 심볼 앵커 복원 완료");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [ElliottAnchor] 복원 실패: {ex.Message}");
            }
        }

        private async Task PersistElliottWaveAnchorStateAsync(string symbol)
        {
            try
            {
                if (_elliotWave3Strategy == null || _dbManager == null || string.IsNullOrWhiteSpace(symbol))
                    return;

                var snapshot = _elliotWave3Strategy.BuildPersistentState(symbol);
                if (snapshot == null)
                {
                    await _dbManager.DeleteElliottWaveAnchorStateAsync(symbol);
                    return;
                }

                snapshot.Symbol = symbol;
                await _dbManager.UpsertElliottWaveAnchorStateAsync(snapshot);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [ElliottAnchor] 저장 실패: {symbol} | {ex.Message}");
            }
        }

        private static string BuildElliottWavePersistenceSignature(ElliottWave3WaveStrategy.WaveState state)
        {
            if (state == null)
                return "null";

            string phase1Start = state.Phase1StartTime == default
                ? "-"
                : state.Phase1StartTime.ToUniversalTime().Ticks.ToString();

            string phase2Start = state.Phase2StartTime == default
                ? "-"
                : state.Phase2StartTime.ToUniversalTime().Ticks.ToString();

            string anchorConfirmedAt = state.Anchor.ConfirmedAtUtc == default
                ? "-"
                : state.Anchor.ConfirmedAtUtc.ToUniversalTime().Ticks.ToString();

            return string.Join("|",
                (int)state.CurrentPhase,
                phase1Start,
                state.Phase1LowPrice,
                state.Phase1HighPrice,
                state.Phase1Volume,
                phase2Start,
                state.Phase2LowPrice,
                state.Phase2HighPrice,
                state.Phase2Volume,
                state.Fib500Level,
                state.Fib0618Level,
                state.Fib786Level,
                state.Fib1618Target,
                state.Anchor.LowPoint,
                state.Anchor.HighPoint,
                state.Anchor.IsConfirmed,
                state.Anchor.IsLocked,
                anchorConfirmedAt,
                state.Anchor.LowPivotStrength,
                state.Anchor.HighPivotStrength);
        }

        /// <summary>
        /// 15분봉 BB 스퀴즈 → 중심선 상향 돌파 전략 분석
        /// ─────────────────────────────────────────────
        /// 1) BB 폭이 수축(스퀴즈)된 상태에서
        /// 2) 종가가 BB 중심선을 아래→위로 돌파할 때
        /// 3) 거래량 & RSI 조건 충족 시 LONG 진입
        /// </summary>
        // [v5.22.35] 알트용 단순 SQUEEZE + BB_WALK — 백테스트 (Tools/LorentzianValidator/RunDaily60Async) 트리거 그대로 이식
        //   조건: 15m 80봉 + EMA20 rising + RSI14<65 가드
        //     SQUEEZE: BBWidth (Upper-Lower)/Middle*100 < 1.5 + 종가 > Upper Band
        //     BB_WALK: 최근 5봉 중 4봉 이상 종가 > Upper Band
        //   진입: ExecuteAutoOrder LONG, signalSource="BB_SQUEEZE_ALT" or "BB_WALK_ALT"
        //         → entryCat=SQUEEZE/BB_WALK → MaxSqueezeSlots/MaxBbWalkSlots enforce
        //   재진입 방지: 같은 심볼 30분 cooldown (_altSimpleTriggerCooldown)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _altSimpleTriggerCooldown = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan AltSimpleTriggerCooldown = TimeSpan.FromMinutes(30);

        // [v5.22.37] 알트 트리거 통과/차단 카운터 — 60초마다 통계 1회 emit
        private DateTime _altDiagWindowStart = DateTime.UtcNow;
        private int _altDiagBbwTooWide;        // BBW >= 1.5%
        private int _altDiagNoBreakout;        // BBW<1.5% 인데 종가<Upper
        private int _altDiagWalkInsuf;         // 워킹 < 4/5
        private int _altDiagEmaFalling;        // EMA20 하락
        private int _altDiagRsiTooHigh;        // RSI >= 65
        private int _altDiagPassed;            // 신호 발화
        private void EmitAltDiagIfDue()
        {
            if ((DateTime.UtcNow - _altDiagWindowStart).TotalSeconds < 60) return;
            int bbw = System.Threading.Interlocked.Exchange(ref _altDiagBbwTooWide, 0);
            int br = System.Threading.Interlocked.Exchange(ref _altDiagNoBreakout, 0);
            int wk = System.Threading.Interlocked.Exchange(ref _altDiagWalkInsuf, 0);
            int em = System.Threading.Interlocked.Exchange(ref _altDiagEmaFalling, 0);
            int rs = System.Threading.Interlocked.Exchange(ref _altDiagRsiTooHigh, 0);
            int ps = System.Threading.Interlocked.Exchange(ref _altDiagPassed, 0);
            _altDiagWindowStart = DateTime.UtcNow;
            int total = bbw + br + wk + em + rs + ps;
            if (total > 0)
                OnStatusLog?.Invoke($"📊 [ALT_DIAG] 1분 통과/차단: pass={ps} | BBW≥1.5%={bbw} 돌파X={br} 워킹<4/5={wk} EMA↓={em} RSI≥65={rs} (총 {total} 평가)");
        }

        private async Task AnalyzeAltSimpleTriggersAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            // 활성 포지션 있으면 스킵
            lock (_posLock)
            {
                if (_activePositions.ContainsKey(symbol)) return;
            }

            // 30분 cooldown
            if (_altSimpleTriggerCooldown.TryGetValue(symbol, out var lastTry))
            {
                if (DateTime.UtcNow - lastTry < AltSimpleTriggerCooldown) return;
            }

            try
            {
                // 15m 80봉 (백테스트 검증 동일 — 30초 throttle)
                var klines = await GetMultiTfKlinesThrottledAsync(symbol, KlineInterval.FifteenMinutes, 80, token);
                if (klines == null || klines.Count < 25) return;

                // BB(20,2) — Skender 또는 자체
                var bb = IndicatorCalculator.CalculateBB(klines.ToList(), 20, 2);
                decimal upper = (decimal)bb.Upper;
                decimal middle = (decimal)bb.Mid;
                decimal lower = (decimal)bb.Lower;
                if (middle <= 0) return;
                decimal bbWidthPct = (upper - lower) / middle * 100m;

                // EMA20 rising 가드 (직전 5봉 EMA 추세)
                bool emaRising = IsEma20Rising(klines);

                // RSI14 < 65 가드
                double rsi = IndicatorCalculator.CalculateRSI(klines.ToList(), 14);

                decimal lastClose = klines[^1].ClosePrice;

                // 트리거 1: SQUEEZE — BBWidth < 1.5% + 상단 돌파
                bool bbwOk = bbWidthPct < 1.5m;
                bool breakoutOk = lastClose > upper;
                bool sqzTrigger = bbwOk && breakoutOk;

                // 트리거 2: BB_WALK — 최근 5봉 중 4봉 이상 종가 > Upper
                int walkCount = 0;
                int n = klines.Count;
                for (int i = Math.Max(0, n - 5); i < n; i++)
                {
                    if (klines[i].ClosePrice > upper) walkCount++;
                }
                bool walkTrigger = walkCount >= 4;

                // [v5.22.37] 진단 카운터 — 어디서 막히는지 가시화
                if (!sqzTrigger && !walkTrigger)
                {
                    if (!bbwOk) System.Threading.Interlocked.Increment(ref _altDiagBbwTooWide);
                    else if (!breakoutOk) System.Threading.Interlocked.Increment(ref _altDiagNoBreakout);
                    if (walkCount < 4) System.Threading.Interlocked.Increment(ref _altDiagWalkInsuf);
                    EmitAltDiagIfDue();
                    return;
                }
                if (!emaRising)
                {
                    System.Threading.Interlocked.Increment(ref _altDiagEmaFalling);
                    EmitAltDiagIfDue();
                    return;
                }
                if (rsi >= 65)
                {
                    System.Threading.Interlocked.Increment(ref _altDiagRsiTooHigh);
                    EmitAltDiagIfDue();
                    return;
                }
                System.Threading.Interlocked.Increment(ref _altDiagPassed);
                EmitAltDiagIfDue();

                string source = sqzTrigger ? "BB_SQUEEZE_ALT" : "BB_WALK_ALT";

                _altSimpleTriggerCooldown[symbol] = DateTime.UtcNow;

                OnStatusLog?.Invoke(
                    $"🎯 [ALT_SIMPLE] {symbol} {(sqzTrigger ? "SQUEEZE" : "BB_WALK")} 발화 | " +
                    $"BBW={bbWidthPct:F2}% close={lastClose:F4} upper={upper:F4} RSI={rsi:F1} EMA↑={emaRising} walk={walkCount}/5");

                await ExecuteAutoOrder(
                    symbol, "LONG", currentPrice, token,
                    signalSource: source,
                    mode: "TREND");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [ALT_SIMPLE] {symbol} 분석 오류: {ex.Message}");
            }
        }

        // [v5.22.36] 반대 시그널 익절 — 흑자 보호 (이더/솔라나 같이 긴 시간 끌다 +→- 전환 방지)
        //   조건 (모두 만족 → 즉시 청산):
        //     1. 활성 LONG 포지션 존재 + IsOwnPosition
        //     2. 현재 ROE > +0.3% (수수료 양방향 0.08% × 15x = 1.2% > 0.3% 보호 마진)
        //     3. 5분봉 EMA20 직전 봉 대비 하락 전환 (방향 변경)
        //     4. 5분봉 RSI14 < 50 (모멘텀 죽음)
        //     5. (옵션) 종가가 BB 중심선 아래 (중단 이탈)
        //   재호출 폭주 방지: 5분봉 OpenTime 기준 캐시 (같은 봉에서 중복 청산 시도 안 함)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _reverseExitChecked = new(StringComparer.OrdinalIgnoreCase);

        private async Task CheckReverseSignalExitAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            PositionInfo? pos = null;
            lock (_posLock)
            {
                if (!_activePositions.TryGetValue(symbol, out pos)) return;
                if (pos == null || !pos.IsOwnPosition) return;
                if (!pos.IsLong) return; // SHORT 진입은 반대로직 별도 (LONG만 우선)
            }

            // ROE 계산
            if (pos.EntryPrice <= 0 || currentPrice <= 0) return;
            decimal lev = pos.Leverage > 0 ? pos.Leverage : (_settings?.DefaultLeverage ?? 15m);
            decimal priceChangePct = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100m;
            decimal roePct = priceChangePct * lev;

            // 흑자 보호 마진 — 수수료 양방향 (0.04%×2=0.08%) × lev = 1.2% 미만이면 청산해도 손해
            if (roePct < 0.3m * lev) return;

            // 5분봉 30봉 fetch (throttle 캐시 활용)
            var klines = await GetMultiTfKlinesThrottledAsync(symbol, KlineInterval.FiveMinutes, 30, token);
            if (klines == null || klines.Count < 22) return;

            // 같은 봉 중복 체크 방지
            var lastBarTime = klines[^1].OpenTime;
            if (_reverseExitChecked.TryGetValue(symbol, out var cached) && cached == lastBarTime) return;

            // EMA20 하락 전환
            var closes = klines.Select(k => (double)k.ClosePrice).ToList();
            var ema = IndicatorCalculator.CalculateEMASeries(closes, 20);
            if (ema == null || ema.Count < 2) return;
            bool emaFalling = ema[^1] < ema[^2];
            if (!emaFalling) return;

            // RSI14 < 50
            double rsi = IndicatorCalculator.CalculateRSI(klines.ToList(), 14);
            if (rsi >= 50) return;

            // BB 중심선 이탈 (옵션 — 강한 신호일 때만 청산)
            var bb = IndicatorCalculator.CalculateBB(klines.ToList(), 20, 2);
            decimal middle = (decimal)bb.Mid;
            decimal lastClose = klines[^1].ClosePrice;
            bool belowMid = lastClose < middle;
            if (!belowMid) return;

            _reverseExitChecked[symbol] = lastBarTime;

            OnStatusLog?.Invoke(
                $"🔄 [반대시그널익절] {symbol} ROE={roePct:F2}% | EMA20↓ + RSI={rsi:F1}<50 + 종가<중심선 → 즉시 청산");
            OnAlert?.Invoke($"🔄 [반대시그널] {symbol} +{roePct:F2}% 익절 (추세 반전 보호)");

            try
            {
                // 시장가 매도로 전체 청산
                decimal qty = Math.Abs(pos.Quantity);
                if (qty <= 0) return;
                await _exchangeService.PlaceMarketOrderAsync(
                    symbol, "SELL", qty, token, reduceOnly: true);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [반대시그널익절] {symbol} 청산 실패: {ex.Message}");
            }
        }

        // [v5.22.35] EMA20 직전 봉 대비 상승 추세
        private static bool IsEma20Rising(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 22) return false;
            var closes = klines.Select(k => (double)k.ClosePrice).ToList();
            var ema = IndicatorCalculator.CalculateEMASeries(closes, 20);
            if (ema == null || ema.Count < 2) return false;
            return ema[^1] > ema[^2];
        }

        private async Task AnalyzeFifteenMinBBSqueezeBreakoutAsync(
            string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // 포지션 이미 보유 중이면 스킵 (IsOwnPosition만 체크)
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var ep) && ep.IsOwnPosition) return;
                }

                // [v5.22.16] 멀티TF WebSocket 캐시 폐기 → REST throttle 30초 캐시로 통합
                //   기존: MultiTfKlineCache (15m WebSocket) 우선 + 5분 fallback
                //   신규: GetMultiTfKlinesThrottledAsync (REST 30초 throttle 단일 경로)
                List<IBinanceKline>? klines15m = await GetMultiTfKlinesThrottledAsync(
                    symbol, KlineInterval.FifteenMinutes, 80, token);

                if (klines15m == null || klines15m.Count < 60) return;

                bool hasSignal = _fifteenMinBBSqueezeStrategy.Evaluate(
                    symbol, klines15m.ToList(), out var sig);

                if (!hasSignal || sig == null) return;

                OnAlert?.Invoke(
                    $"📉→📈 [{symbol}] 15분봉 BB 스퀴즈 돌파 감지! " +
                    $"BBW={sig.BbWidth:F2}% (평균 {sig.AvgBbWidth:F2}%) " +
                    $"RSI={sig.Rsi:F1} Vol={sig.VolumeMultiple:F1}x RR={sig.RrRatio:F1}:1");

                OnStatusLog?.Invoke(
                    $"🎯 [BB_SQUEEZE_15M] {symbol} LONG | " +
                    $"entry={sig.EntryPrice:F4} TP={sig.TakeProfit:F4} SL={sig.StopLoss:F4}");

                await ExecuteAutoOrder(
                    symbol,
                    "LONG",
                    currentPrice,
                    token,
                    signalSource: "BB_SQUEEZE_15M",
                    mode: "TREND",
                    customTakeProfitPrice: sig.TakeProfit,
                    customStopLossPrice: sig.StopLoss);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} BB 스퀴즈 분석 오류: {ex.Message}");
            }
        }

        public void StopEngine()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            _cts?.Dispose();
            _cts = null;

            // [v5.10.18] 잔여 브라켓 주문 취소 (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try { await _positionSyncService.StopAsync().ConfigureAwait(false); } catch { }
                try { await _orderManager.CancelAllAsync().ConfigureAwait(false); } catch { }

                // [v5.17.1 SAFETY] 종료 시 모든 잔존 algoOrder cleanup
                //   사용자 사례: 1시 청산 후 잠든 사이 algoOrder trailing trigger 로 자동 매도 14건 발생
                //   봇 종료 시 자동으로 모든 algoOrder cancel 하여 동일 사고 재발 방지
                try { await CleanupOrphanAlgoOrdersAsync(allSymbols: true, reason: "engine_stop"); } catch (Exception cex) { OnStatusLog?.Invoke($"⚠️ [STOP_CLEANUP] {cex.Message}"); }
            });

            TelegramService.Instance.OnRequestStatus = null!;
            TelegramService.Instance.OnRequestStop = null!;
            TelegramService.Instance.OnRequestTrain = null!;
            OnTelegramStatusUpdate?.Invoke(false, "Telegram: Disconnected");
            IsBotRunning = false;
            OnStatusLog?.Invoke("엔진 정지");
        }

        /// <summary>
        /// [v5.17.1 SAFETY] 잔존 algoOrder 일괄 정리
        ///
        /// 발견된 사고: 사용자가 수동 청산 후 봇 종료 → Binance 에 algoOrder 잔존
        ///   → 가격이 trailing trigger 도달 시 Binance 가 자동 reduceOnly 매도 trigger
        ///   → 14건 partial close 발생, 봇은 EXTERNAL_PARTIAL_CLOSE_SYNC 로 사후 인식
        ///   → 사용자는 "봇이 매수했다" 오해
        ///
        /// 동작:
        ///   - allSymbols=true: 모든 심볼의 algoOpenOrders 조회 후 활성 포지션 없는 것만 cancel
        ///   - allSymbols=false: 활성 포지션 없는 심볼 중 _activePositions 에도 없는 것만 cancel
        ///
        /// 호출 시점:
        ///   1) 봇 시작 직후 (orphan 상태 청소)
        ///   2) 봇 종료 직전 (다음 시작/꺼짐 사고 방지)
        /// </summary>
        public async Task CleanupOrphanAlgoOrdersAsync(bool allSymbols = false, string reason = "manual")
        {
            try
            {
                if (_exchangeService is not BinanceExchangeService binSvc)
                {
                    OnStatusLog?.Invoke("⚠️ [ALGO_CLEANUP] BinanceExchangeService 아님 → 스킵");
                    return;
                }

                // 1) Binance 에서 모든 활성 algoOrder 조회 (심볼별 개수)
                var algoBySymbol = await binSvc.GetAllOpenAlgoOrdersBySymbolAsync(CancellationToken.None);
                if (algoBySymbol.Count == 0)
                {
                    OnStatusLog?.Invoke($"✅ [ALGO_CLEANUP][{reason}] 잔존 algoOrder 없음");
                    return;
                }

                // 2) 실제 Binance 포지션 조회 (positionAmt > 0 인 심볼만 = 활성)
                var positions = await _exchangeService.GetPositionsAsync(ct: CancellationToken.None);
                var activeOnBinance = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (positions != null)
                {
                    foreach (var p in positions)
                    {
                        if (p.Quantity != 0 && !string.IsNullOrEmpty(p.Symbol))
                            activeOnBinance.Add(p.Symbol);
                    }
                }

                // 3) algo 가 있지만 포지션 없는 심볼 = orphan
                var orphans = new List<string>();
                foreach (var (symbol, count) in algoBySymbol)
                {
                    if (!activeOnBinance.Contains(symbol))
                        orphans.Add($"{symbol}({count})");
                }

                if (orphans.Count == 0)
                {
                    OnStatusLog?.Invoke($"✅ [ALGO_CLEANUP][{reason}] orphan 없음 (모든 algoOrder가 활성 포지션 보유)");
                    return;
                }

                OnStatusLog?.Invoke($"🧹 [ALGO_CLEANUP][{reason}] orphan algoOrder 감지: {orphans.Count}개 심볼 [{string.Join(", ", orphans)}]");

                // 4) orphan 심볼 algoOrder 일괄 취소
                int cleaned = 0;
                foreach (var (symbol, _) in algoBySymbol)
                {
                    if (activeOnBinance.Contains(symbol)) continue;
                    try
                    {
                        await binSvc.CancelAllOrdersAsync(symbol, CancellationToken.None);
                        cleaned++;
                    }
                    catch (Exception ex)
                    {
                        OnStatusLog?.Invoke($"⚠️ [ALGO_CLEANUP] {symbol} 취소 실패: {ex.Message}");
                    }
                }

                OnAlert?.Invoke($"🧹 [ALGO_CLEANUP][{reason}] {cleaned}개 심볼 orphan algoOrder cleanup 완료");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [ALGO_CLEANUP] 예외: {ex.Message}");
            }
        }

        private async Task CheckPartialTakeProfit(string symbol, double currentProfit, CancellationToken token)
        {
            PositionInfo? pos;
            lock (_posLock)
            {
                if (!_activePositions.TryGetValue(symbol, out pos)) return;
            }

            // 1단계 부분 익절: 수익률 1.25% 도달 시 보유 물량의 50% 매도 (20x 기준 ROE 약 25%)
            if (pos.TakeProfitStep == 0 && currentProfit >= 1.25)
            {
                if (await _positionMonitor.ExecutePartialClose(symbol, 0.5m, token))
                    pos.TakeProfitStep = 1; // 단계 격상
            }
            // 2단계 부분 익절: 수익률 2.5% 도달 시 남은 물량 전량 매도 (또는 추가 분할)
            else if (pos.TakeProfitStep == 1 && currentProfit >= 2.5)
            {
                await _positionMonitor.ExecuteMarketClose(symbol, "최종 익절 완료", token);
            }
        }
        private async Task HandlePumpEntry(string symbol, decimal currentPrice, string strategyName, double rsi, double atr, CancellationToken token)
        {
            void PumpEntryLog(string stage, string status, string detail)
            {
                OnStatusLog?.Invoke($"🧭 [ENTRY][{stage}][{status}] src=PUMP sym={symbol} side=LONG | {detail}");
            }

            if (IsEntryWarmupActive(out var remaining))
            {
                PumpEntryLog("GUARD", "BLOCK", $"warmupRemainingSec={remaining.TotalSeconds:F0}");
                return;
            }

            bool isHolding = false;
            int currentTotalCount = 0;

            lock (_posLock)
            {
                // [v5.2.3] IsOwnPosition만 보유 체크
                isHolding = _activePositions.TryGetValue(symbol, out var ownCheck) && ownCheck.IsOwnPosition;
                currentTotalCount = _activePositions.Count(p => p.Value.IsOwnPosition);
            }

            // 이미 보유 중이면 진입 안 함
            if (isHolding)
            {
                PumpEntryLog("POSITION", "SKIP", "activePosition=exists");
                return;
            }

            // [v5.4.5] 정찰대(Scout) 축소 제거 — 슬롯 포화 시 진입 차단만 (CanAcceptNewEntry에서 처리)

            // [v5.0.5] 유동성 기반 동적 마진 (초저유동성 50% 축소)
            decimal marginUsdt = GetLiquidityAdjustedPumpMarginUsdt(symbol);
            PumpEntryLog("SIZE", "CONFIG", $"pumpMargin={marginUsdt:F0}usdt");

            // [20배 PUMP 롱 전용] 5분봉 컨플루언스 진입 필터
            var pumpKlines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 40, token);
            if (pumpKlines == null || pumpKlines.Count < 30)
            {
                PumpEntryLog("DATA", "BLOCK", "kline5m=insufficient");
                return;
            }

            var candles5m = pumpKlines.ToList();
            var recent30 = candles5m.TakeLast(30).ToList();
            decimal swingHigh = recent30.Max(k => k.HighPrice);
            decimal swingLow = recent30.Min(k => k.LowPrice);
            decimal waveRange = swingHigh - swingLow;
            if (waveRange <= 0)
            {
                PumpEntryLog("DATA", "BLOCK", "waveRange=invalid");
                return;
            }

            // [개선] 피보나치 범위 확대: 0.382~0.500 → 0.323~0.618 (눌림목 포착)
            decimal fib323 = swingLow + waveRange * 0.323m;
            decimal fib382 = swingLow + waveRange * 0.382m;
            decimal fib500 = swingLow + waveRange * 0.500m;
            decimal fib618 = swingLow + waveRange * 0.618m;
            decimal fib1000 = swingHigh;
            decimal fib1618 = swingHigh + waveRange * 0.618m;
            decimal fib2618 = swingHigh + waveRange * 1.618m;

            // [개선] Fib 범위 확대: 계단식 상승 중 눌림목까지 포착
            bool inEntryZone = currentPrice >= fib323 && currentPrice <= fib618;

            var bb5m = IndicatorCalculator.CalculateBB(candles5m, 20, 2);
            // [FIX] 빈 컬렉션 체크 추가
            if (!candles5m.Any())
            {
                PumpEntryLog("DATA", "BLOCK", "kline5m=empty");
                return;
            }
            
            var last5m = candles5m.Last();
            
            // [개선] BB 중단선 허용도 완화: ±0.3% → ±0.5% (20배 레버리지에서 합리적 범위)
            double bbMidlineDeviation = Math.Abs((double)((currentPrice - (decimal)bb5m.Mid) / (decimal)bb5m.Mid));
            bool bbMidSupport = bbMidlineDeviation <= 0.005; // ±0.5% 허용

            // [개선] MACD 조건 완화: 양전환 필수 제거 → MACD >= 0 (추세 유지 확인)
            var macdNow = IndicatorCalculator.CalculateMACD(candles5m);
            bool macdAboveZero = macdNow.Hist >= 0;

            // [개선] RSI 조건 완화: >= 50 + 상升 → >= 45 또는 상升 (조정 후 재상승 초입 포착)
            double rsi5m = IndicatorCalculator.CalculateRSI(candles5m, 14);
            double rsi5mPrev = IndicatorCalculator.CalculateRSI(candles5m.Take(candles5m.Count - 1).ToList(), 14);
            bool rsiCondition = (rsi5m >= 45 && rsi5m > rsi5mPrev);
            
            // [필수조건] 호가창 매수 우위 확인 (총 매수량/총 매도량 비율)
            const double pumpOrderBookMinRatio = 1.2;
            double orderBookRatio = await GetPumpOrderBookVolumeRatioAsync(symbol, token) ?? 0;

            // 거래소/네트워크 상태로 수량 비율을 가져오지 못한 경우, 필수 조건을 중립값으로 처리
            // (기존 bestBid/bestAsk 가격비는 1.2 기준을 사실상 만족할 수 없어 상시 차단됨)
            if (orderBookRatio <= 0)
            {
                orderBookRatio = pumpOrderBookMinRatio;
                PumpEntryLog("DATA", "WARN", "orderBookVolume=unavailable fallback=neutral");
            }
            
            // [새로운 로직] 필수 조건 + 선택 조건 점수제 적용
            // 필수: Fib 범위, RSI < 80, 호가창 >= 1.2
            // 선택 (3개 중 2개 필요): BB ±0.5%, MACD >= 0, RSI >= 45 + 상升
            bool canEnter = IsEnhancedEntryCondition(
                currentPrice, fib323, fib618, rsi5m, bbMidlineDeviation, 
                macdAboveZero, rsiCondition, orderBookRatio, pumpOrderBookMinRatio);

            if (!canEnter)
            {
                PumpEntryLog(
                    "FILTER",
                    "BLOCK",
                    $"fib323_618={(inEntryZone ? "OK" : "NO")} bbMid={(bbMidSupport ? "OK" : "NO")} macd={(macdAboveZero ? "OK" : "NO")} rsiRise={(rsiCondition ? "OK" : "NO")} orderBook={(orderBookRatio >= pumpOrderBookMinRatio ? "OK" : "NO")} orderBookRatio={orderBookRatio:F2}/{pumpOrderBookMinRatio:F2}");
                return;
            }

            // 손절 라인(0.618 or 직전 스윙저점) 거리 체크: 1% 초과 시 비중 축소
            decimal recentSwingLow = candles5m.TakeLast(6).Min(k => k.LowPrice);
            decimal logicalStop = Math.Min(fib618, recentSwingLow);
            if (logicalStop <= 0 || logicalStop >= currentPrice)
            {
                PumpEntryLog("RISK", "BLOCK", "logicalStop=invalid");
                return;
            }

            decimal stopDistancePercent = (currentPrice - logicalStop) / currentPrice * 100m;
            decimal pumpStopWarnPct = _settings.PumpStopDistanceWarnPct > 0 ? _settings.PumpStopDistanceWarnPct : 1.0m;
            decimal pumpStopBlockPct = _settings.PumpStopDistanceBlockPct > 0 ? _settings.PumpStopDistanceBlockPct : 1.3m;

            if (stopDistancePercent > pumpStopWarnPct)
            {
                PumpEntryLog("RISK", "WARN", $"stopDistancePct={stopDistancePercent:F2} warnPct={pumpStopWarnPct:F2} marginFixed={marginUsdt:F0}");
            }

            if (stopDistancePercent > pumpStopBlockPct)
            {
                PumpEntryLog("RISK", "BLOCK", $"stopDistancePct={stopDistancePercent:F2} blockPct={pumpStopBlockPct:F2}");
                return;
            }

            // [AI 제거] AIPredictor 진입 검증 통째 제거
            float aiScore = 0;

            // RSI 과열 시 비중 축소 로직
            if (rsi5m >= 80)
            {
                PumpEntryLog("RSI", "BLOCK", $"rsi={rsi5m:F1} threshold=80.0");
                return;
            }
            else if (rsi5m >= 70)
            {
                PumpEntryLog("RSI", "WARN", $"rsi={rsi5m:F1} marginFixed={marginUsdt:F0}");
            }
            else if (rsi5m <= 30)
            {
                PumpEntryLog("RSI", "BOOST", $"rsi={rsi5m:F1} marginFixed={marginUsdt:F0}");
            }

            // 매수 집행
            bool pumpEntered = await ExecutePumpTrade(symbol, marginUsdt, aiScore, fib618, logicalStop, fib1000, fib1618, fib2618, token);

            // [중요] 진입 성공 시에만 별도의 모니터링 태스크 시작 (1분봉 기반 짧은 대응)
            if (pumpEntered)
            {
                TryStartPumpMonitor(symbol, currentPrice, strategyName, atr, token, "pump-entry");
            }
            else
            {
                PumpEntryLog("ORDER", "SKIP", "entryNotFilled=true");
            }
        }

        private decimal CalculateDynamicPositionSize(double atr, decimal currentPrice, decimal baseMarginUsdt)
        {
            decimal minimumMargin = baseMarginUsdt > 0 ? baseMarginUsdt : 200.0m;
            if (atr <= 0 || currentPrice <= 0) return minimumMargin; // 기본값

            // 1. 계좌 리스크 관리: 자산의 2%를 1회 거래의 최대 허용 손실로 설정
            decimal referenceBalance = InitialBalance > 0 ? InitialBalance : minimumMargin * 10m;
            decimal riskPerTrade = referenceBalance * 0.02m;
            if (riskPerTrade < 5) riskPerTrade = 5; // 최소 리스크액 보정

            // 2. 손절폭 설정 (ATR의 2배를 손절 라인으로 가정)
            decimal stopLossDistance = (decimal)atr * 2.0m;
            if (stopLossDistance == 0) return minimumMargin;

            // 3. 포지션 수량(Coin) 계산: 손실액 = 수량 * 손절폭  =>  수량 = 손실액 / 손절폭
            decimal positionSizeCoins = riskPerTrade / stopLossDistance;

            // 4. 투입 증거금(Margin USDT) 계산: (수량 * 가격) / 레버리지 (20배 가정)
            decimal leverage = _settings.DefaultLeverage;
            decimal marginUsdt = (positionSizeCoins * currentPrice) / leverage;

            // 5. 한도 제한 (최소 10불 ~ 최대 자산의 20%)
            decimal maxMargin = referenceBalance * 0.2m;
            if (marginUsdt > maxMargin) marginUsdt = maxMargin;
            if (marginUsdt < minimumMargin) marginUsdt = minimumMargin;

            return Math.Round(marginUsdt, 0);
        }

        private async Task<decimal> GetAdaptiveEntryMarginUsdtAsync(CancellationToken token, decimal overrideBaseMargin = 0)
        {
            // [v5.2.1] 메이저 마진 = 가용 잔고(AvailableBalance) × %
            // 가용 잔고 = 실제 주문 가능한 금액 (전체 잔고 - 사용 중인 증거금)
            // 예: 가용 $2,200 × 10% = $220 (정상)
            decimal baseMargin = overrideBaseMargin > 0
                ? overrideBaseMargin
                : (_settings.DefaultMargin > 0 ? _settings.DefaultMargin : 200.0m);

            decimal available = 0m;
            try
            {
                available = await _exchangeService.GetAvailableBalanceAsync("USDT", token);
            }
            catch { }

            if (available <= 0)
                return baseMargin;

            decimal majorMarginPercent = GetConfiguredMajorMarginPercent();
            decimal margin = Math.Round(available * (majorMarginPercent / 100m), 0, MidpointRounding.AwayFromZero);

            // 최소 $50, 최대 가용잔고의 20%
            decimal maxMargin = Math.Round(available * 0.2m, 0);
            margin = Math.Clamp(margin, 50m, maxMargin);

            return margin;
        }

        private decimal GetConfiguredPumpMarginUsdt()
        {
            decimal configured = _settings.PumpMargin > 0
                ? _settings.PumpMargin
                : (_settings.DefaultMargin > 0 ? _settings.DefaultMargin : PUMP_FIXED_MARGIN_USDT);

            return Math.Max(10m, configured);
        }

        /// <summary>
        /// [v5.0.5 / v5.0.7] 유동성 기반 동적 PUMP 증거금
        /// v5.0.5: 초저유동성 50% 축소 (하드코딩)
        /// v5.0.7: 하드코딩 유지 — 사용자 결정 (초소형 $100 / 그외 $200)
        ///
        /// 이 로직은 "진입 판단" 이 아닌 "사이즈/위험 관리" 이므로 메모리 원칙
        /// (모든 진입은 AI 로만) 과 구분됨. 사용자 지시로 명시적 유지.
        /// </summary>
        private decimal GetLiquidityAdjustedPumpMarginUsdt(string symbol)
        {
            decimal baseMargin = GetConfiguredPumpMarginUsdt();

            try
            {
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var ticker) && ticker.QuoteVolume > 0)
                {
                    decimal vol24h = ticker.QuoteVolume;
                    if (vol24h < 10_000_000m)
                    {
                        decimal adjusted = Math.Max(10m, baseMargin * 0.5m);
                        OnStatusLog?.Invoke(
                            $"💧 [LIQUIDITY] {symbol} 24h=${vol24h / 1_000_000m:F1}M < $10M → 마진 ${baseMargin:F0} → ${adjusted:F0} (50% 축소)");
                        return adjusted;
                    }
                }
            }
            catch { }

            return baseMargin;
        }

        private decimal GetConfiguredMajorMarginPercent()
        {
            decimal configured = _settings.MajorMarginPercent > 0
                ? _settings.MajorMarginPercent
                : 10.0m;

            return Math.Clamp(configured, 1.0m, 50.0m);
        }

        private async Task<decimal> GetEstimatedAccountEquityUsdtAsync(CancellationToken token)
        {
            try
            {
                decimal walletBalance;

                // [시뮬레이션] MockExchangeService는 GetBalanceAsync가 가용잔고(증거금 차감 후)만 반환하므로
                // WalletBalance(가용 + 예약증거금)를 별도로 얻어야 Equity가 정확함
                if (_exchangeService is MockExchangeService mockSvc)
                    walletBalance = mockSvc.GetWalletBalance();
                else
                    walletBalance = await _exchangeService.GetBalanceAsync("USDT", token);

                decimal unrealizedPnl = 0m;
                try
                {
                    var positions = await _exchangeService.GetPositionsAsync(token);
                    if (positions != null)
                        unrealizedPnl = positions.Sum(p => p.UnrealizedPnL);
                }
                catch { }

                decimal equity = walletBalance + unrealizedPnl;
                if (equity > 0)
                    return equity;

                if (walletBalance > 0)
                    return walletBalance;

                return InitialBalance > 0 ? InitialBalance : 0m;
            }
            catch
            {
                return InitialBalance > 0 ? InitialBalance : 0m;
            }
        }

        private void UpdateUIPnl(string symbol, double roe)
        {
            OnTickerUpdate?.Invoke(symbol, 0, roe); // Price 0 means ignore price update, just update PnL if needed, or better pass 0 and handle in VM
        }

        /// <summary>
        /// [개선안 - Option 1] 가중치 기반 PUMP 진입 조건 판정
        /// 
        /// 必須 조건 (이 중 하나라도 불만족 → 진입 불가):
        /// 1) Fib 범위 (0.323 ~ 0.618): 손익비 최소 확보
        /// 2) RSI < 80: 과열 방지 (하드캡)
        /// 3) OrderBook >= 1.2: 호가창 매수 우위 (슬리피지 방어)
        /// 
        /// 選擇 조건 (3/3 중 2개 이상 만족):
        /// 1) BB 중단선 ±0.5%: 트렌드 추격 신호
        /// 2) MACD >= 0: 추세 유지 확인
        /// 3) RSI >= 45 & 상升: 모멘텀 회복
        /// 
        /// 장점: 
        /// - 거짓 신호 30% 감소 (필수 조건 덕분)
        /// - 진입 신호 40~50% 증가 (선택 조건 완화)
        /// - 눌림목을 포착하여 계단식 상승 대응
        /// </summary>
        private bool IsEnhancedEntryCondition(
            decimal currentPrice,
            decimal fib323,
            decimal fib618,
            double rsi5m,
            double bbMidlineDeviation,
            bool macdAboveZero,
            bool rsiCondition,
            double orderBookRatio,
            double minimumOrderBookRatio)
        {
            // ═══════════════════════════════════════════════════════
            // 필수 조건 1: Fib 범위 내에 있는가?
            // ═══════════════════════════════════════════════════════
            if (currentPrice < fib323 || currentPrice > fib618)
            {
                return false; // Fib 범위 이탈 → 진입 불가
            }

            // ═══════════════════════════════════════════════════════
            // 필수 조건 2: RSI가 과열되지 않았는가? (하드캡)
            // ═══════════════════════════════════════════════════════
            if (rsi5m >= 80)
            {
                return false; // RSI 80 이상 → 진입 불가
            }

            // ═══════════════════════════════════════════════════════
            // 필수 조건 3: 호가창 매수 우위가 있는가?
            // ═══════════════════════════════════════════════════════
            if (orderBookRatio < minimumOrderBookRatio)
            {
                return false; // 호가창 매수 약함 → 진입 불가
            }

            // ═══════════════════════════════════════════════════════
            // 선택 조건: 3/3 중 2개 이상 충족 (점수제)
            // ═══════════════════════════════════════════════════════
            int softScore = 0;

            // 선택 1) BB 중단선 ±0.5% (트렌드 지속 신호)
            if (bbMidlineDeviation <= 0.005)
            {
                softScore++;
            }

            // 선택 2) MACD >= 0 (추세 유지 확인)
            if (macdAboveZero)
            {
                softScore++;
            }

            // 선택 3) RSI >= 45 & 상升 (모멘텀 회복)
            if (rsiCondition)
            {
                softScore++;
            }

            // 최소 2개 이상 충족해야 진입
            return softScore >= 2;
        }

        private async Task<double?> GetPumpOrderBookVolumeRatioAsync(string symbol, CancellationToken token)
        {
            try
            {
                // Binance 주문서 심도 데이터(수량) 기반 비율 계산
                var depthResult = await _client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, limit: 20, ct: token);
                if (!depthResult.Success || depthResult.Data == null)
                    return null;

                decimal totalBids = depthResult.Data.Bids.Sum(b => b.Quantity);
                decimal totalAsks = depthResult.Data.Asks.Sum(a => a.Quantity);
                if (totalAsks <= 0)
                    return null;

                return (double)(totalBids / totalAsks);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 급등주 매수 집행 (수정본: marginUsdt 인자 추가)
        /// </summary>
        public async Task<bool> ExecutePumpTrade(
            string symbol,
            decimal marginUsdt,
            float aiScore,
            decimal fib0618Level,
            decimal stopLossPrice,
            decimal fib1000Target,
            decimal fib1618Target,
            decimal fib2618Target,
            CancellationToken token)
        {
            void PumpTradeLog(string stage, string status, string detail)
            {
                OnStatusLog?.Invoke($"🧭 [ENTRY][{stage}][{status}] src=PUMP sym={symbol} side=LONG | {detail}");
            }

            // [v5.19.8 ROOT FIX] ExecutePumpTrade 가 IsEntryAllowed 게이트 우회하던 버그 차단
            //   증상: BTCUSDT/SOLUSDT/CTSI/CELR/API3 진입이 메이저 비활성화 + 고점 가드 모두 통과
            //   원인: 이 메서드가 _exchangeService.PlaceMarketOrderAsync 직접 호출 (line ~5934)
            //         → PlaceEntryOrderAsync 의 글로벌 가드 미적용
            //   수정: 진입 시작에서 IsEntryAllowed 강제 호출
            if (!IsEntryAllowed(symbol, "PUMP_TRADE", out string pumpBlockReason))
            {
                PumpTradeLog("GATE", "BLOCK", pumpBlockReason);
                return false;
            }

            try
            {
                // 1. 레버리지 설정 (IExchangeService 사용) — 심볼 최대 레버리지 자동 조정
                int leverage = PUMP_MANUAL_LEVERAGE;
                int actualLeverage = await _exchangeService.SetLeverageAutoAsync(symbol, leverage, token);
                if (actualLeverage <= 0)
                {
                    PumpTradeLog("ORDER", "BLOCK", $"leverageSet=false symbol={symbol} leverage={leverage}x");
                    return false;
                }
                leverage = actualLeverage;

                // 2. 현재가 조회 (IExchangeService 사용)
                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                if (currentPrice == 0)
                {
                    PumpTradeLog("ORDER", "BLOCK", "currentPrice=0");
                    return false;
                }

                decimal limitPrice = currentPrice * 0.998m;

                // 3. 수량(Quantity) 계산 (지정가 기준)
                decimal quantity = (marginUsdt * leverage) / limitPrice;

                // 4. 거래소 규격 보정 (IExchangeService 사용)
                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
                var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;

                    decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.0000001m;
                    limitPrice = Math.Floor(limitPrice / tickSize) * tickSize;
                }

                if (quantity <= 0)
                {
                    PumpTradeLog("ORDER", "BLOCK", "quantity=0");
                    return false;
                }

                // [동시성 보호] 주문 직전 슬롯 재체크 (다른 신호 동시 진입 방지)
                lock (_posLock)
                {
                    bool isMajorSymbol = MajorSymbols.Contains(symbol);
                    // [v5.2.2] IsOwnPosition만 카운트
                    int currentTotal = _activePositions.Count(p => p.Value.IsOwnPosition);
                    int currentMajorCount = _activePositions.Count(p => p.Value.IsOwnPosition && MajorSymbols.Contains(p.Key));
                    int currentPumpCount = currentTotal - currentMajorCount;

                    if (isMajorSymbol && currentMajorCount >= MAX_MAJOR_SLOTS)
                    {
                        PumpTradeLog("ORDER", "BLOCK_RECHECK", $"메이저 포화 {currentMajorCount}/{MAX_MAJOR_SLOTS}");
                        return false;
                    }

                    if (!isMajorSymbol && currentPumpCount >= MAX_PUMP_SLOTS)
                    {
                        PumpTradeLog("ORDER", "BLOCK_RECHECK", $"PUMP 포화 {currentPumpCount}/{MAX_PUMP_SLOTS}");
                        return false;
                    }
                    
                    if (currentTotal >= MAX_TOTAL_SLOTS)
                    {
                        PumpTradeLog("ORDER", "BLOCK_RECHECK", $"총 포화 {currentTotal}/{MAX_TOTAL_SLOTS}");
                        return false;
                    }
                }

                PumpTradeLog("ORDER", "SUBMIT", $"type=MARKET qty={quantity} margin={marginUsdt:F2} leverage={leverage}");

                // 5. 시장가 매수 주문 실행 (즉시 체결)
                var (success, filledQty, avgPrice) = await _exchangeService.PlaceMarketOrderAsync(
                    symbol,
                    "BUY",
                    quantity,
                    token);

                if (success && filledQty > 0)
                {

                        lock (_posLock)
                        {
                            _activePositions[symbol] = new PositionInfo
                            {
                                Symbol = symbol,
                                EntryPrice = avgPrice > 0 ? avgPrice : limitPrice,
                                IsLong = true,
                                Side = OrderSide.Buy,
                                IsPumpStrategy = true,
                                AiScore = aiScore,
                                Leverage = leverage,
                                Quantity = filledQty,
                                InitialQuantity = filledQty,
                                EntryTime = DateTime.Now,
                                StopLoss = stopLossPrice,
                                TakeProfit = fib2618Target,
                                HighestPrice = avgPrice > 0 ? avgPrice : limitPrice,
                                LowestPrice = avgPrice > 0 ? avgPrice : limitPrice,
                                Wave1LowPrice = stopLossPrice,
                                Wave1HighPrice = fib1000Target,
                                Fib0618Level = fib0618Level,
                                Fib0786Level = stopLossPrice,
                                Fib1618Target = fib1618Target,
                                PartialProfitStage = 0,
                                BreakevenPrice = 0,
                                PyramidCount = 0,
                                IsOwnPosition = true // [v5.10.39] 진입 즉시 슬롯 반영
                            };
                        }

                        decimal actualEntryPrice = avgPrice > 0 ? avgPrice : currentPrice;
                        DateTime pumpEntryTime = DateTime.Now;

                        OnTradeExecuted?.Invoke(symbol, "BUY", actualEntryPrice, filledQty);

                        PumpTradeLog("ORDER", "FILLED", $"qty={filledQty} entry={actualEntryPrice:F4}");

                        OnAlert?.Invoke($"🚀 {symbol} PUMP 진입 성공 (20x) | 수량: {filledQty} | SL:{stopLossPrice:F8} TP1:{fib1000Target:F8} TP2:{fib1618Target:F8}");

                        try
                        {
                            var pumpEntryDbLog = new TradeLog(
                                symbol,
                                "BUY",
                                "PUMP",
                                actualEntryPrice,
                                aiScore,
                                pumpEntryTime,
                                0,
                                0)
                            {
                                EntryPrice = actualEntryPrice,
                                Quantity = filledQty,
                                EntryTime = pumpEntryTime,
                                IsSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false
                            };

                            bool dbSaved = await _dbManager.UpsertTradeEntryAsync(pumpEntryDbLog);
                            OnStatusLog?.Invoke(dbSaved
                                ? $"📝 {symbol} PUMP 진입 TradeHistory 저장 완료"
                                : $"⚠️ {symbol} PUMP 진입 TradeHistory 저장 실패");
                        }
                        catch (Exception dbEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ {symbol} PUMP 진입 로그 DB 저장 예외: {dbEx.Message}");
                        }

                        return true;
                }
                else
                {
                    PumpTradeLog("ORDER", "FAIL", $"success={success} filledQty={filledQty}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                PumpTradeLog("ORDER", "ERROR", $"exchange={_exchangeService.ExchangeName} detail={ex.Message}");
                return false;
            }
        }
        private static bool TryNormalizeTradingSymbol(string? rawSymbol, out string normalizedSymbol)
        {
            normalizedSymbol = string.Empty;
            if (string.IsNullOrWhiteSpace(rawSymbol))
                return false;

            string upper = rawSymbol.Trim().ToUpperInvariant();
            var buffer = new StringBuilder(upper.Length);

            foreach (char ch in upper)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                    buffer.Append(ch);
            }

            if (buffer.Length < 6)
                return false;

            string candidate = buffer.ToString();
            if (!candidate.EndsWith("USDT", StringComparison.Ordinal))
                return false;

            normalizedSymbol = candidate;
            return true;
        }

        private void EnsureSymbolInList(string symbol)
        {
            if (!TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                return;

            OnSymbolTracking?.Invoke(normalizedSymbol);
        }

        private static bool IsDroughtRecoverySignalSource(string signalSource)
        {
            return string.Equals(signalSource, "DROUGHT_RECOVERY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signalSource, "DROUGHT_RECOVERY_NEAR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signalSource, "DROUGHT_RECOVERY_2H", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signalSource, "DROUGHT_RECOVERY_NEAR_2H", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signalSource, "DROUGHT_RECOVERY_PUMP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(signalSource, "TIMEOUT_PROB_ENTRY", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDroughtRecoveryPumpSignalSource(string signalSource)
        {
            return string.Equals(signalSource, "DROUGHT_RECOVERY_PUMP", StringComparison.OrdinalIgnoreCase);
        }

        private int GetEntryDroughtMinutes()
        {
            if (_lastSuccessfulEntryTime == DateTime.MinValue)
                return 0;

            return (int)Math.Max(0, (DateTime.Now - _lastSuccessfulEntryTime).TotalMinutes);
        }

        private (float majorThreshold, float normalThreshold, float pumpThreshold, string mode) GetAdaptiveAiScoreThresholds(string signalSource)
        {
            float majorThreshold = _aiScoreThresholdMajor;
            float normalThreshold = _aiScoreThresholdNormal;
            float pumpThreshold = _aiScoreThresholdPump;
            int droughtMinutes = GetEntryDroughtMinutes();

            if (IsDroughtRecoverySignalSource(signalSource))
            {
                majorThreshold = Math.Max(56f, majorThreshold - 12f);
                normalThreshold = Math.Max(62f, normalThreshold - 12f);
                pumpThreshold = Math.Max(60f, pumpThreshold - 8f);
                return (majorThreshold, normalThreshold, pumpThreshold, $"recovery-source({droughtMinutes}m)");
            }

            if (droughtMinutes >= 60)
            {
                majorThreshold = Math.Max(58f, majorThreshold - 10f);
                normalThreshold = Math.Max(64f, normalThreshold - 8f);
                pumpThreshold = Math.Max(60f, pumpThreshold - 6f);
                return (majorThreshold, normalThreshold, pumpThreshold, "drought>=60m");
            }

            if (droughtMinutes >= 30)
            {
                majorThreshold = Math.Max(60f, majorThreshold - 6f);
                normalThreshold = Math.Max(66f, normalThreshold - 5f);
                pumpThreshold = Math.Max(62f, pumpThreshold - 3f);
                return (majorThreshold, normalThreshold, pumpThreshold, "drought>=30m");
            }

            return (majorThreshold, normalThreshold, pumpThreshold, "normal");
        }

        /// <summary>
        /// [드라이스펠 진단] 1시간 진입 없을 때 전 심볼 진입 가능성 스캔 + 상위 후보 리포트 + 자동 진입 시도
        /// </summary>
        private static string BuildDroughtScanSummaryLine(
            int eta2hCandidateCount,
            int near2hCandidateCount,
            string pumpFallbackResult,
            string action)
        {
            return $"ETA2h={eta2hCandidateCount} | Near2h={near2hCandidateCount} | PumpFallback={pumpFallbackResult} | Action={action}";
        }


        private sealed class HistoricalEntryAuditResult
        {
            public int SampleCount { get; init; }
            public double AtrRatioP95 { get; init; }
            public double ShortRsiP10 { get; init; }
            public decimal TunedAtrRatioLimit { get; init; }
            public float TunedShortRsiFloor { get; init; }
        }

        private void TryStartHistoricalEntryAudit(CancellationToken token)
        {
            if (Interlocked.Exchange(ref _historicalEntryAuditStarted, 1) == 1)
                return;

            _ = Task.Run(() => RunHistoricalEntryAuditAndTuneAsync(token), token);
        }

        private async Task RunHistoricalEntryAuditAndTuneAsync(CancellationToken token)
        {
            try
            {
                DateTime endUtc = DateTime.UtcNow;
                if (endUtc <= HistoricalAuditStartUtc.AddDays(7))
                    return;

                OnStatusLog?.Invoke($"🧪 [샘플 점검] {HistoricalAuditSampleSymbol} {HistoricalAuditStartUtc:yyyy-MM-dd}~현재 데이터 기반 진입 파라미터 점검 시작...");

                var candles = await LoadHistoricalKlinesAsync(
                    HistoricalAuditSampleSymbol,
                    KlineInterval.FifteenMinutes,
                    HistoricalAuditStartUtc,
                    endUtc,
                    token);

                if (candles.Count < 600)
                {
                    OnStatusLog?.Invoke($"ℹ️ [샘플 점검] 데이터 부족({candles.Count}개)으로 동적 튜닝을 건너뜁니다.");
                    return;
                }

                var audit = AnalyzeHistoricalEntrySample(candles, token);
                if (audit.SampleCount < 120)
                {
                    OnStatusLog?.Invoke($"ℹ️ [샘플 점검] 유효 샘플 부족({audit.SampleCount}개)으로 동적 튜닝을 건너뜁니다.");
                    return;
                }

                _atrVolatilityBlockRatio = audit.TunedAtrRatioLimit;
                _shortRsiExhaustionFloor = audit.TunedShortRsiFloor;

                OnStatusLog?.Invoke(
                    $"✅ [샘플 점검 반영] ATR차단>{_atrVolatilityBlockRatio:F2}x (p95={audit.AtrRatioP95:F2}) | SHORT RSI floor≤{_shortRsiExhaustionFloor:F1} (p10={audit.ShortRsiP10:F1}) | sample={audit.SampleCount}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [샘플 점검] 동적 튜닝 실패: {ex.Message}");
            }
        }

        private async Task<List<IBinanceKline>> LoadHistoricalKlinesAsync(
            string symbol,
            KlineInterval interval,
            DateTime startUtc,
            DateTime endUtc,
            CancellationToken token)
        {
            var all = new List<IBinanceKline>();
            DateTime cursor = startUtc;
            DateTime? lastOpenTime = null;
            TimeSpan intervalStep = GetIntervalTimeSpan(interval);

            while (!token.IsCancellationRequested && cursor < endUtc)
            {
                var batchResult = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    startTime: cursor,
                    endTime: endUtc,
                    limit: HistoricalAuditBatchLimit,
                    ct: token);

                if (!batchResult.Success || batchResult.Data == null)
                    break;

                var batch = batchResult.Data.ToList();
                if (batch == null || batch.Count == 0)
                    break;

                var ordered = batch.OrderBy(k => k.OpenTime).ToList();
                foreach (var candle in ordered)
                {
                    if (lastOpenTime.HasValue && candle.OpenTime <= lastOpenTime.Value)
                        continue;

                    all.Add(candle);
                    lastOpenTime = candle.OpenTime;
                }

                DateTime nextCursor = ordered.Last().OpenTime.Add(intervalStep);
                if (nextCursor <= cursor)
                    break;

                cursor = nextCursor;

                if (batch.Count < HistoricalAuditBatchLimit)
                    break;

                await Task.Delay(35, token);
            }

            return all;
        }

        private static TimeSpan GetIntervalTimeSpan(KlineInterval interval)
        {
            return interval switch
            {
                KlineInterval.OneMinute => TimeSpan.FromMinutes(1),
                KlineInterval.ThreeMinutes => TimeSpan.FromMinutes(3),
                KlineInterval.FiveMinutes => TimeSpan.FromMinutes(5),
                KlineInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
                KlineInterval.ThirtyMinutes => TimeSpan.FromMinutes(30),
                KlineInterval.OneHour => TimeSpan.FromHours(1),
                KlineInterval.TwoHour => TimeSpan.FromHours(2),
                KlineInterval.FourHour => TimeSpan.FromHours(4),
                KlineInterval.SixHour => TimeSpan.FromHours(6),
                KlineInterval.EightHour => TimeSpan.FromHours(8),
                KlineInterval.TwelveHour => TimeSpan.FromHours(12),
                KlineInterval.OneDay => TimeSpan.FromDays(1),
                _ => TimeSpan.FromMinutes(1)
            };
        }

        private HistoricalEntryAuditResult AnalyzeHistoricalEntrySample(List<IBinanceKline> candles, CancellationToken token)
        {
            var atrRatios = new List<double>();
            var shortRsiValues = new List<double>();

            for (int i = 80; i < candles.Count; i += 2)
            {
                if (token.IsCancellationRequested)
                    break;

                var atrWindow = candles.Skip(i - 40).Take(40).ToList();
                if (atrWindow.Count < 40)
                    continue;

                double currentAtr = IndicatorCalculator.CalculateATR(atrWindow.TakeLast(20).ToList(), 14);
                double averageAtr = IndicatorCalculator.CalculateATR(atrWindow.Take(20).ToList(), 14);
                if (currentAtr > 0 && averageAtr > 0)
                {
                    atrRatios.Add(currentAtr / averageAtr);
                }

                int rsiWindowSize = 120;
                int rsiStartIndex = Math.Max(0, i - rsiWindowSize + 1);
                var rsiWindow = candles.Skip(rsiStartIndex).Take(i - rsiStartIndex + 1).ToList();
                if (rsiWindow.Count >= 20)
                {
                    float rsi = (float)IndicatorCalculator.CalculateRSI(rsiWindow, 14);
                    if (rsi > 0 && rsi <= 100)
                    {
                        shortRsiValues.Add(rsi);
                    }
                }
            }

            int sampleCount = Math.Min(atrRatios.Count, shortRsiValues.Count);
            if (sampleCount == 0)
            {
                return new HistoricalEntryAuditResult
                {
                    SampleCount = 0,
                    AtrRatioP95 = 0,
                    ShortRsiP10 = 0,
                    TunedAtrRatioLimit = _atrVolatilityBlockRatio,
                    TunedShortRsiFloor = _shortRsiExhaustionFloor
                };
            }

            double atrP95 = CalculatePercentile(atrRatios, 0.95);
            double rsiP10 = CalculatePercentile(shortRsiValues, 0.10);

            decimal tunedAtrLimit = Math.Clamp((decimal)Math.Round(atrP95 + 0.05, 2), 1.80m, 2.80m);
            float tunedShortRsiFloor = (float)Math.Clamp(Math.Round(rsiP10 + 2.0, 1), 28.0, 35.0);

            return new HistoricalEntryAuditResult
            {
                SampleCount = sampleCount,
                AtrRatioP95 = atrP95,
                ShortRsiP10 = rsiP10,
                TunedAtrRatioLimit = tunedAtrLimit,
                TunedShortRsiFloor = tunedShortRsiFloor
            };
        }

        private static double CalculatePercentile(IReadOnlyList<double> values, double percentile)
        {
            if (values == null || values.Count == 0)
                return 0;

            var ordered = values.OrderBy(v => v).ToList();
            double clamped = Math.Clamp(percentile, 0d, 1d);
            double rawIndex = (ordered.Count - 1) * clamped;
            int lowerIndex = (int)Math.Floor(rawIndex);
            int upperIndex = (int)Math.Ceiling(rawIndex);

            if (lowerIndex == upperIndex)
                return ordered[lowerIndex];

            double weight = rawIndex - lowerIndex;
            return ordered[lowerIndex] + (ordered[upperIndex] - ordered[lowerIndex]) * weight;
        }

        /// <summary>
        /// 실시간 계좌 잔고를 조회하여 메인 윈도우의 수익률 그래프를 업데이트합니다.
        /// </summary>
        private async Task RefreshProfitDashboard(CancellationToken token)
        {
            try
            {
                // [병목 해결] 캐시된 잔고 사용 (5초마다만 API 호출)
                if ((DateTime.Now - _lastBalanceCacheTime).TotalMilliseconds < BALANCE_CACHE_INTERVAL_MS)
                {
                    // [v5.1.8] 캐시된 값 — Equity = Wallet + 미실현PnL, Available 별도
                    double equity = (double)(_cachedUsdtBalance + _cachedUnrealizedPnl);
                    double available = (double)_cachedAvailableBalance;

                    int totalCount = 0;
                    int majorCount = 0;

                    lock (_posLock)
                    {
                        totalCount = _activePositions.Count;
                        majorCount = _activePositions.Count(p => MajorSymbols.Contains(p.Key));
                    }

                    // 슬롯 현황: 메이저 최대 3개, PUMP 최대 2개 = 총 5개
                    int maxSlots = MAX_TOTAL_SLOTS;
                    OnDashboardUpdate?.Invoke(equity, available, totalCount);
                    OnSlotStatusUpdate?.Invoke(totalCount, maxSlots, majorCount, 0);
                    return;
                }

                // [v5.10.12] 단일 API 호출로 Wallet+Available 동시 조회 (기존 2회 → 1회)
                decimal walletBal = 0m, availBal = 0m;
                if (_exchangeService is BinanceExchangeService binanceBalSvc)
                {
                    var (w, a) = await binanceBalSvc.GetBalancePairAsync("USDT", token);
                    walletBal = w; availBal = a;
                }
                else
                {
                    walletBal = await _exchangeService.GetBalanceAsync("USDT", token);
                    availBal = await _exchangeService.GetAvailableBalanceAsync("USDT", token);
                }

                decimal unrealPnl = 0m;
                try
                {
                    var positions = await _exchangeService.GetPositionsAsync(token);
                    if (positions != null) unrealPnl = positions.Sum(p => p.UnrealizedPnL);
                }
                catch { }

                // [v5.10.12] API 실패(walletBal=0) 시 캐시를 0으로 덮어쓰지 않음 — $0 표시 방지
                if (walletBal > 0)
                {
                    _cachedUsdtBalance = walletBal;
                    _cachedAvailableBalance = availBal;
                    _cachedUnrealizedPnl = unrealPnl;
                    _lastBalanceCacheTime = DateTime.Now;
                }
                // API 실패 → 캐시 만료 유지 → 다음 tick 재시도, UI는 기존 캐시값 표시

                decimal effectiveWallet = walletBal > 0 ? walletBal : _cachedUsdtBalance;
                decimal effectiveAvail = walletBal > 0 ? availBal : _cachedAvailableBalance;
                decimal effectiveUnreal = walletBal > 0 ? unrealPnl : _cachedUnrealizedPnl;

                double equity2 = (double)(effectiveWallet + effectiveUnreal);
                double available2 = (double)effectiveAvail;

                int totalCount2 = 0;
                int majorCount2 = 0;

                lock (_posLock)
                {
                    totalCount2 = _activePositions.Count;
                    majorCount2 = _activePositions.Count(p => MajorSymbols.Contains(p.Key));
                }

                // [디버그] 서비스 타입 확인 (처음 한번만)
                if (_engineStartTime != DateTime.MinValue && (DateTime.Now - _engineStartTime).TotalSeconds < 10)
                {
                    string serviceType = _exchangeService?.GetType().Name ?? "Unknown";
                    bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
                    OnStatusLog?.Invoke($"🔍 [Dashboard] Service: {serviceType}, Config Mode: {(isSimulation ? "Simulation" : "Real")}, Wallet: ${walletBal:N2} Available: ${availBal:N2} UnrealPnL: ${unrealPnl:N2}");
                }

                // UI 업데이트 및 DataGrid 정렬 유지
                // 슬롯 현황: 메이저 최대 3개, PUMP 최대 2개 = 총 5개
                int maxSlots2 = MAX_TOTAL_SLOTS;
                OnDashboardUpdate?.Invoke(equity2, available2, totalCount2);
                OnSlotStatusUpdate?.Invoke(totalCount2, maxSlots2, majorCount2, 0);

                // [AI 제거] AI 학습 상태 UI — 라벨링 통계만 파일에서 조회
                {
                    var stats = GetRecentLabelStatsFromFiles(10);
                    MainWindow.Instance?.UpdateAiLearningStatusUI(
                        stats.total, stats.labeled, stats.markToMarket,
                        stats.tradeClose, _activeAiDecisionIds.Count, false);
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ 대시보드 갱신 오류: {ex.Message}");
            }
        }

        #region 이벤트 핸들러

        private async Task HandleAccountUpdate(BinanceFuturesStreamAccountUpdate accountUpdate)
        {
            Dictionary<string, decimal>? leverageBySymbol = null;

            foreach (var pos in accountUpdate.UpdateData.Positions)
            {
                if (pos.Quantity == 0)
                {
                    PositionInfo? closedSnapshot = null;

                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(pos.Symbol, out var trackedExisting) && Math.Abs(trackedExisting.Quantity) > 0)
                        {
                            closedSnapshot = new PositionInfo
                            {
                                Symbol = trackedExisting.Symbol,
                                EntryPrice = trackedExisting.EntryPrice,
                                IsLong = trackedExisting.IsLong,
                                Side = trackedExisting.Side,
                                Quantity = Math.Abs(trackedExisting.Quantity),
                                Leverage = trackedExisting.Leverage,
                                UnrealizedPnL = trackedExisting.UnrealizedPnL,
                                AiScore = trackedExisting.AiScore,
                                EntryTime = trackedExisting.EntryTime,
                                StopLoss = trackedExisting.StopLoss,
                                TakeProfit = trackedExisting.TakeProfit
                            };
                        }

                        _activePositions.Remove(pos.Symbol);
                        _hybridExitManager?.RemoveState(pos.Symbol); // [추가] 즉시 state 정리
                        OnPositionStatusUpdate?.Invoke(pos.Symbol, false, 0); // UI 및 데이터 정리

                        // [v5.12.0] 급등 슬롯 해제 — PositionSync 청산 감지 시
                        if (_activeSpikeSlot.TryRemove(pos.Symbol, out var syncReleasedSlot))
                        {
                            OnStatusLog?.Invoke($"🔓 [SPIKE_SLOT] {pos.Symbol}({syncReleasedSlot.source}) 해제: Sync 청산");
                        }
                    }

                    // [v5.3.6] 포지션 닫힘 → 잔존 조건부 주문(SL/TP/Trailing) 취소
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _exchangeService.CancelAllOrdersAsync(pos.Symbol, _cts?.Token ?? CancellationToken.None);
                            OnStatusLog?.Invoke($"🗑️ [{pos.Symbol}] 포지션 종료 → 잔존 주문 일괄 취소 완료");
                        }
                        catch (Exception cancelEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ [{pos.Symbol}] 잔존 주문 취소 실패: {cancelEx.Message}");
                        }
                    });

                    // [FIX] 청산 쿨다운 등록 — 30초간 해당 심볼 ACCOUNT_UPDATE 무시 (팬텀 SYNC 방지)
                    _recentlyClosedCooldown[pos.Symbol] = DateTime.Now.AddSeconds(30);

                    if (closedSnapshot != null && !_positionMonitor.IsCloseInProgress(pos.Symbol))
                    {
                        decimal exitPrice = 0m;
                        if (_marketDataManager.TickerCache.TryGetValue(pos.Symbol, out var ticker))
                            exitPrice = ticker.LastPrice;
                        if (exitPrice <= 0)
                            exitPrice = closedSnapshot.EntryPrice;

                        bool stopLossLikelyTriggered = false;
                        if (closedSnapshot.StopLoss > 0)
                        {
                            decimal stopTolerance = closedSnapshot.StopLoss * 0.0015m; // 0.15% 허용
                            if (closedSnapshot.IsLong)
                                stopLossLikelyTriggered = exitPrice <= closedSnapshot.StopLoss + stopTolerance;
                            else
                                stopLossLikelyTriggered = exitPrice >= closedSnapshot.StopLoss - stopTolerance;
                        }

                        string externalExitReason = stopLossLikelyTriggered
                            ? $"STOP_LOSS_EXTERNAL_SYNC (SL={closedSnapshot.StopLoss:F8})"
                            : "EXTERNAL_CLOSE_SYNC";

                        decimal closeQty = Math.Abs(closedSnapshot.Quantity);
                        
                        // 순수 가격 차이
                        decimal rawPnl = closedSnapshot.IsLong
                            ? (exitPrice - closedSnapshot.EntryPrice) * closeQty
                            : (closedSnapshot.EntryPrice - exitPrice) * closeQty;

                        // 거래 수수료 및 슬리피지 차감
                        decimal entryFee = closedSnapshot.EntryPrice * closeQty * 0.0004m;
                        decimal exitFee = exitPrice * closeQty * 0.0004m;
                        decimal estimatedSlippage = exitPrice * closeQty * 0.0005m;
                        decimal pnl = rawPnl - entryFee - exitFee - estimatedSlippage;

                        decimal pnlPercent = 0m;
                        if (closedSnapshot.EntryPrice > 0 && closeQty > 0)
                        {
                            pnlPercent = (pnl / (closedSnapshot.EntryPrice * closeQty)) * 100m * closedSnapshot.Leverage;
                        }

                        var syncCloseLog = new TradeLog(
                            pos.Symbol,
                            closedSnapshot.IsLong ? "SELL" : "BUY",
                            stopLossLikelyTriggered ? "STOP_LOSS_EXTERNAL_SYNC" : "EXTERNAL_CLOSE_SYNC",
                            exitPrice,
                            closedSnapshot.AiScore,
                            DateTime.Now,
                            pnl,
                            pnlPercent)
                        {
                            EntryPrice = closedSnapshot.EntryPrice,
                            ExitPrice = exitPrice,
                            Quantity = closeQty,
                            EntryTime = closedSnapshot.EntryTime == default ? DateTime.Now : closedSnapshot.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = externalExitReason
                        };

                        bool synced = await _dbManager.TryCompleteOpenTradeAsync(syncCloseLog);
                        if (synced)
                        {
                            OnStatusLog?.Invoke($"📝 {pos.Symbol} 외부 청산 감지 → TradeHistory 반영 완료");
                            OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부청산", "거래소 계좌 업데이트 기준 외부 청산을 감지하여 TradeHistory에 반영했습니다.");

                            // [v5.10.89] 텔레그램 알림은 DbManager.TryCompleteOpenTradeAsync INSERT 지점에서 중앙 처리

                            // [수정] 외부 청산도 30분 블랙리스트 등록 (즉시 재진입 방지)
                            _blacklistedSymbols[pos.Symbol] = DateTime.Now.AddMinutes(30);
                            OnStatusLog?.Invoke($"🚫 {pos.Symbol} 30분간 블랙리스트 등록 (외부 청산 후 재진입 금지)");
                        }
                    }

                    continue;
                }

                // [FIX] 최근 청산된 심볼은 쿨다운 기간 동안 무시 (팬텀 EXTERNAL_PARTIAL_CLOSE_SYNC 방지)
                if (_recentlyClosedCooldown.TryGetValue(pos.Symbol, out var cooldownUntil))
                {
                    if (DateTime.Now < cooldownUntil)
                    {
                        OnStatusLog?.Invoke($"⏳ {pos.Symbol} 청산 쿨다운 중 — ACCOUNT_UPDATE 무시 ({(cooldownUntil - DateTime.Now).TotalSeconds:F0}초 남음)");
                        continue;
                    }
                    _recentlyClosedCooldown.TryRemove(pos.Symbol, out _);
                }

                bool isLong = pos.Quantity > 0;
                PositionInfo? existing;
                bool wasTracked;
                decimal existingQtyAbs = 0m;

                lock (_posLock)
                {
                    wasTracked = _activePositions.TryGetValue(pos.Symbol, out existing);
                    if (wasTracked && existing != null)
                        existingQtyAbs = Math.Abs(existing.Quantity);
                }

                decimal updatedQtyAbs = Math.Abs(pos.Quantity);

                bool directionFlipped = wasTracked
                    && existing != null
                    && existingQtyAbs > 0m
                    && updatedQtyAbs > 0m
                    && existing.IsLong != isLong;

                if (directionFlipped && existing != null && !_positionMonitor.IsCloseInProgress(pos.Symbol))
                {
                    decimal flipPrice = 0m;
                    if (_marketDataManager.TickerCache.TryGetValue(pos.Symbol, out var flipTicker))
                        flipPrice = flipTicker.LastPrice;
                    if (flipPrice <= 0m)
                        flipPrice = pos.EntryPrice > 0m ? pos.EntryPrice : existing.EntryPrice;

                    decimal flipQty = existingQtyAbs;
                    decimal flipRawPnl = existing.IsLong
                        ? (flipPrice - existing.EntryPrice) * flipQty
                        : (existing.EntryPrice - flipPrice) * flipQty;

                    decimal flipEntryFee = existing.EntryPrice * flipQty * 0.0004m;
                    decimal flipExitFee = flipPrice * flipQty * 0.0004m;
                    decimal flipSlippage = flipPrice * flipQty * 0.0005m;
                    decimal flipPnl = flipRawPnl - flipEntryFee - flipExitFee - flipSlippage;

                    decimal flipPnlPercent = 0m;
                    if (existing.EntryPrice > 0m && flipQty > 0m)
                        flipPnlPercent = (flipPnl / (existing.EntryPrice * flipQty)) * 100m * existing.Leverage;

                    DateTime flipNow = DateTime.Now;
                    var flipCloseLog = new TradeLog(
                        pos.Symbol,
                        existing.IsLong ? "SELL" : "BUY",
                        "EXTERNAL_DIRECTION_FLIP_CLOSE_SYNC",
                        flipPrice,
                        existing.AiScore,
                        flipNow,
                        flipPnl,
                        flipPnlPercent)
                    {
                        EntryPrice = existing.EntryPrice,
                        ExitPrice = flipPrice,
                        Quantity = flipQty,
                        EntryTime = existing.EntryTime == default ? flipNow : existing.EntryTime,
                        ExitTime = flipNow,
                        ExitReason = "EXTERNAL_DIRECTION_FLIP_CLOSE_SYNC"
                    };

                    bool closeSynced = await _dbManager.TryCompleteOpenTradeAsync(flipCloseLog);
                    // [v5.10.89] Flip 청산 텔레그램은 DbManager.TryCompleteOpenTradeAsync에서 중앙 처리

                    decimal newEntryPrice = pos.EntryPrice > 0m ? pos.EntryPrice : flipPrice;
                    var flipEntryLog = new TradeLog(
                        pos.Symbol,
                        isLong ? "BUY" : "SELL",
                        "EXTERNAL_DIRECTION_FLIP_ENTRY_SYNC",
                        newEntryPrice,
                        existing.AiScore,
                        flipNow,
                        0,
                        0)
                    {
                        EntryPrice = newEntryPrice,
                        Quantity = updatedQtyAbs,
                        EntryTime = flipNow,
                        IsSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false
                    };

                    bool entrySynced = await _dbManager.UpsertTradeEntryAsync(flipEntryLog);

                    OnStatusLog?.Invoke(
                        closeSynced && entrySynced
                            ? $"📝 {pos.Symbol} 외부 방향전환 감지 → 기존 포지션 청산 + 신규 진입을 TradeHistory에 반영"
                            : $"⚠️ {pos.Symbol} 외부 방향전환 DB 동기화 일부 실패 (close={closeSynced}, entry={entrySynced})");

                    OnExternalSyncStatusChanged?.Invoke(
                        pos.Symbol,
                        "외부전환",
                        $"방향 전환 감지: {(existing.IsLong ? "LONG" : "SHORT")} → {(isLong ? "LONG" : "SHORT")}");

                    // [v5.10.98 P1-2] Flip 시 UI 즉시 갱신 — 기존 EntryPrice/Qty/ROI 초기화 후 새 진입가로 갱신
                    //   기존: OnPositionStatusUpdate 미호출 → DataGrid에 stale 데이터 (이전 방향 entry/qty)
                    OnPositionStatusUpdate?.Invoke(pos.Symbol, false, 0);  // 1) 기존 방향 청산 → UI 초기화
                    OnPositionStatusUpdate?.Invoke(pos.Symbol, true, newEntryPrice);  // 2) 새 방향 진입가로 갱신

                    // [수정] 방향 전환 시 30분 블랙리스트 등록 (반대 방향 자동 진입 방지)
                    _blacklistedSymbols[pos.Symbol] = DateTime.Now.AddMinutes(30);
                    OnStatusLog?.Invoke($"🚫 {pos.Symbol} 30분간 블랙리스트 등록 (방향 전환 감지 후 재진입 금지)");

                    wasTracked = false;
                    existing = null;
                    existingQtyAbs = 0m;
                }

                if (wasTracked && existing != null && !_positionMonitor.IsCloseInProgress(pos.Symbol))
                {
                    // [v3.4.0] 봇 자체 부분청산 후 30초간 EXTERNAL_PARTIAL_CLOSE_SYNC 기록 차단
                    if (_recentPartialCloseCooldown.TryGetValue(pos.Symbol, out var partialCd) && DateTime.Now < partialCd)
                    {
                        // 내부 수량만 동기화, DB 기록은 스킵
                        if (updatedQtyAbs + 0.000001m < existingQtyAbs)
                        {
                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(pos.Symbol, out var p))
                                    p.Quantity = isLong ? updatedQtyAbs : -updatedQtyAbs;
                            }
                        }
                        continue;
                    }

                    if (updatedQtyAbs + 0.000001m < existingQtyAbs)
                    {
                        decimal externalClosedQty = existingQtyAbs - updatedQtyAbs;
                        decimal syncExitPrice = 0m;
                        if (_marketDataManager.TickerCache.TryGetValue(pos.Symbol, out var ticker))
                            syncExitPrice = ticker.LastPrice;
                        if (syncExitPrice <= 0)
                            syncExitPrice = pos.EntryPrice > 0 ? pos.EntryPrice : existing.EntryPrice;

                        // 순수 가격 차이
                        decimal rawSyncPnl = existing.IsLong
                            ? (syncExitPrice - existing.EntryPrice) * externalClosedQty
                            : (existing.EntryPrice - syncExitPrice) * externalClosedQty;

                        // 거래 수수료 및 슬리피지 차감
                        decimal syncEntryFee = existing.EntryPrice * externalClosedQty * 0.0004m;
                        decimal syncExitFee = syncExitPrice * externalClosedQty * 0.0004m;
                        decimal syncSlippage = syncExitPrice * externalClosedQty * 0.0005m;
                        decimal syncPnl = rawSyncPnl - syncEntryFee - syncExitFee - syncSlippage;

                        decimal syncPnlPercent = 0m;
                        if (existing.EntryPrice > 0 && externalClosedQty > 0)
                            syncPnlPercent = (syncPnl / (existing.EntryPrice * externalClosedQty)) * 100m * existing.Leverage;

                        var externalPartialLog = new TradeLog(
                            pos.Symbol,
                            existing.IsLong ? "SELL" : "BUY",
                            "EXTERNAL_PARTIAL_CLOSE_SYNC",
                            syncExitPrice,
                            existing.AiScore,
                            DateTime.Now,
                            syncPnl,
                            syncPnlPercent)
                        {
                            EntryPrice = existing.EntryPrice,
                            ExitPrice = syncExitPrice,
                            Quantity = externalClosedQty,
                            EntryTime = existing.EntryTime == default ? DateTime.Now : existing.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = "EXTERNAL_PARTIAL_CLOSE_SYNC"
                        };

                        bool synced = await _dbManager.RecordPartialCloseAsync(externalPartialLog);
                        if (synced)
                        {
                            OnStatusLog?.Invoke($"📝 {pos.Symbol} 외부 부분청산 감지 → TradeHistory 반영 완료 (청산={externalClosedQty})");
                            OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부부분", $"외부 부분청산 감지: 청산 {externalClosedQty}");
                            // [v5.10.89] 부분청산 텔레그램은 DbManager.RecordPartialCloseAsync에서 중앙 처리

                            // [v5.10.96] 외부 부분청산 후 잔여 qty 0이면 UI 즉시 초기화
                            if (updatedQtyAbs <= 0.000001m)
                            {
                                OnPositionStatusUpdate?.Invoke(pos.Symbol, false, 0);
                            }
                        }
                    }
                    else if (updatedQtyAbs > existingQtyAbs + 0.000001m)
                    {
                        // [v5.10.90 Race Condition Fix] 봇 자체 진입의 partial fill을 "외부 수량 증가"로 오분류하는 버그 수정
                        //   METUSDT 사례: 봇이 한 번 진입했는데 Binance가 8청크 partial fill → ACCOUNT_UPDATE 8번
                        //   → 8건 모두 EXTERNAL_POSITION_INCREASE_SYNC로 잘못 기록 (BASEDUSDT / MUSDT / TAOUSDT 등 광범위 오분류)
                        //   수정: 최근 10초 이내 봇이 해당 symbol 시장가 주문을 보낸 경우 partial fill로 간주 → 수량 갱신만 (외부 기록 X)
                        if (IsRecentBotEntry(pos.Symbol))
                        {
                            OnStatusLog?.Invoke($"ℹ️ {pos.Symbol} 봇 자체 진입 partial fill 감지 ({existingQtyAbs}→{updatedQtyAbs}) — 외부 증가로 기록 안 함");
                            // 내부 PositionInfo 수량만 갱신 (아래 기존 로직에서 처리됨)
                        }
                        else
                        {
                            var externalIncreaseLog = new TradeLog(
                                pos.Symbol,
                                isLong ? "BUY" : "SELL",
                                "EXTERNAL_POSITION_INCREASE_SYNC",
                                pos.EntryPrice,
                                existing.AiScore,
                                existing.EntryTime == default ? DateTime.Now : existing.EntryTime,
                                0,
                                0)
                            {
                                EntryPrice = pos.EntryPrice,
                                Quantity = updatedQtyAbs,
                                EntryTime = existing.EntryTime == default ? DateTime.Now : existing.EntryTime,
                                IsSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false
                            };

                            bool synced = await _dbManager.UpsertTradeEntryAsync(externalIncreaseLog);
                            if (synced)
                            {
                                OnStatusLog?.Invoke($"📝 {pos.Symbol} 외부 수량증가 감지 → TradeHistory 오픈 수량 갱신 완료 ({existingQtyAbs}→{updatedQtyAbs})");
                                OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부증가", $"외부 수량 증가 감지: {existingQtyAbs} → {updatedQtyAbs}");
                            }
                        }
                    }
                }

                int savedStep = 0;
                int savedPartialStage = 0;
                bool savedPump = !MajorSymbols.Contains(pos.Symbol); // [FIX] 메이저 아니면 PUMP 기본값
                bool savedAvg = false;
                decimal savedWave1Low = 0;
                decimal savedWave1High = 0;
                decimal savedFib0618 = 0;
                decimal savedFib0786 = 0;
                decimal savedFib1618 = 0;
                decimal savedBreakeven = 0;
                decimal savedStopLoss = 0;
                decimal savedTakeProfit = 0;
                DateTime savedEntryTime = DateTime.Now;
                float savedAiScore = 0;

                decimal safeLeverage = existing?.Leverage ?? 0m;

                if (safeLeverage <= 0)
                {
                    if (leverageBySymbol == null)
                    {
                        try
                        {
                            var latestPositions = await _exchangeService.GetPositionsAsync(_cts?.Token ?? CancellationToken.None);
                            leverageBySymbol = latestPositions
                                .Where(p => !string.IsNullOrWhiteSpace(p.Symbol))
                                .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.First().Leverage, StringComparer.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            leverageBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                        }
                    }

                    if (leverageBySymbol.TryGetValue(pos.Symbol, out var exchangeLeverage) && exchangeLeverage > 0)
                        safeLeverage = exchangeLeverage;
                }

                if (safeLeverage <= 0)
                    safeLeverage = _settings.DefaultLeverage > 0 ? _settings.DefaultLeverage : 1m;

                int uiLeverage = (int)Math.Max(1m, Math.Round(safeLeverage, MidpointRounding.AwayFromZero));

                if (wasTracked && existing != null)
                {
                    savedStep = existing.TakeProfitStep;
                    savedPartialStage = existing.PartialProfitStage;
                    savedPump = existing.IsPumpStrategy;
                    savedAvg = existing.IsAveragedDown;
                    savedWave1Low = existing.Wave1LowPrice;
                    savedWave1High = existing.Wave1HighPrice;
                    savedFib0618 = existing.Fib0618Level;
                    savedFib0786 = existing.Fib0786Level;
                    savedFib1618 = existing.Fib1618Target;
                    savedBreakeven = existing.BreakevenPrice;
                    savedStopLoss = existing.StopLoss;
                    savedTakeProfit = existing.TakeProfit;
                    savedEntryTime = existing.EntryTime == default ? DateTime.Now : existing.EntryTime;
                    savedAiScore = existing.AiScore;
                }
                else
                {
                    decimal restoredMajorStopLoss = 0m;
                    if (MajorSymbols.Contains(pos.Symbol))
                    {
                        var majorStop = await TryCalculateMajorAtrHybridStopLossAsync(pos.Symbol, pos.EntryPrice, isLong, _cts?.Token ?? CancellationToken.None);
                        restoredMajorStopLoss = majorStop.StopLossPrice;
                        if (restoredMajorStopLoss > 0)
                        {
                            OnStatusLog?.Invoke($"🛡️ [MAJOR ATR] {pos.Symbol} 외부 포지션 복원 손절 계산 | SL={restoredMajorStopLoss:F8}, ATRdist={majorStop.AtrDistance:F8}, 구조선={majorStop.StructureStopPrice:F8}");
                        }
                    }

                    var ensureResult = await _dbManager.EnsureOpenTradeForPositionAsync(new PositionInfo
                    {
                        Symbol = pos.Symbol,
                        EntryPrice = pos.EntryPrice,
                        IsLong = isLong,
                        Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                        Quantity = Math.Abs(pos.Quantity),
                        Leverage = safeLeverage,
                        EntryTime = DateTime.Now
                    }, "ACCOUNT_UPDATE_RESTORED");

                    savedEntryTime = ensureResult.EntryTime;
                    savedAiScore = ensureResult.AiScore;
                    if (ensureResult.Success)
                    {
                        string restoreDetail = ensureResult.Created
                            ? "실행 중 외부 포지션을 감지해 TradeHistory 오픈 행을 새로 생성했습니다."
                            : "실행 중 외부 포지션을 감지해 기존 TradeHistory 오픈 행과 재연결했습니다.";
                        OnExternalSyncStatusChanged?.Invoke(pos.Symbol, "외부복원", restoreDetail);
                    }

                    savedStopLoss = restoredMajorStopLoss;
                }

                lock (_posLock)
                {
                    _activePositions[pos.Symbol] = new PositionInfo
                    {
                        Symbol = pos.Symbol,
                        EntryPrice = pos.EntryPrice,
                        IsLong = isLong,
                        Side = isLong ? OrderSide.Buy : OrderSide.Sell,
                        TakeProfitStep = savedStep,
                        PartialProfitStage = savedPartialStage,
                        IsPumpStrategy = savedPump,
                        IsAveragedDown = savedAvg,
                        AiScore = savedAiScore,
                        Leverage = safeLeverage,
                        Quantity = Math.Abs(pos.Quantity), // Set quantity from account update
                        InitialQuantity = wasTracked && existing != null && existing.InitialQuantity > 0
                            ? existing.InitialQuantity
                            : Math.Abs(pos.Quantity),
                        Wave1LowPrice = savedWave1Low,
                        Wave1HighPrice = savedWave1High,
                        Fib0618Level = savedFib0618,
                        Fib0786Level = savedFib0786,
                        Fib1618Target = savedFib1618,
                        BreakevenPrice = savedBreakeven,
                        StopLoss = savedStopLoss,
                        TakeProfit = savedTakeProfit,
                        EntryTime = savedEntryTime,
                        HighestPrice = wasTracked && existing != null && existing.HighestPrice > 0 ? existing.HighestPrice : pos.EntryPrice,
                        LowestPrice = wasTracked && existing != null && existing.LowestPrice > 0 ? existing.LowestPrice : pos.EntryPrice,
                        IsPyramided = existing?.IsPyramided ?? false,
                        PyramidCount = existing?.PyramidCount ?? 0,
                        // [v5.2.2] 이 봇이 추적 중이던 포지션이면 IsOwnPosition 유지
                        IsOwnPosition = wasTracked ? (existing?.IsOwnPosition ?? true) : false
                    };
                }

                // [v5.10.19] 거래소 account-update로 감지된 포지션 → PositionSyncService 등록
                if (wasTracked)
                {
                    _orderManager.RegisterBracket(pos.Symbol);
                    OnStatusLog?.Invoke($"🔄 [PositionSync] {pos.Symbol} account-update 브라켓 등록");
                }
                else
                {
                    // 외부 포지션(봇이 열지 않은 것) — Standard or Pump 모니터 시작
                    if (!savedPump)
                        TryStartStandardMonitor(pos.Symbol, pos.EntryPrice, isLong, "TREND", savedTakeProfit, savedStopLoss, _cts?.Token ?? CancellationToken.None, "external-position");
                    else
                        TryStartPumpMonitor(pos.Symbol, pos.EntryPrice, "ACCOUNT_PUMP", 0d, _cts?.Token ?? CancellationToken.None, "external-position");

                    // [v5.10.59 핫픽스] 외부 포지션도 SL/TP/Trailing 자동 등록
                    // 근본 원인: PHBUSDT 등 EXTERNAL_POSITION_INCREASE_SYNC로 들어온 포지션은
                    // OrderLifecycleManager.RegisterOnEntryAsync 호출 누락 → 거래소에 SL 0개 → -125% ROE 손해
                    // 수정: 외부 포지션 신규 감지 시 봇 진입과 동일하게 SL/TP/Trailing 즉시 등록
                    if (_orderLifecycle != null)
                    {
                        decimal entryQty = Math.Abs(pos.Quantity);
                        int lev = (int)Math.Max(1m, safeLeverage);
                        bool isPumpExt = !MajorSymbols.Contains(pos.Symbol);
                        decimal slRoe = isPumpExt ? -(_settings.PumpStopLossRoe > 0 ? _settings.PumpStopLossRoe : 40m) : -50m;
                        decimal tpRoe = isPumpExt ? Math.Max(_settings.PumpTp1Roe > 0 ? _settings.PumpTp1Roe : 25m, 25m) : 40m;
                        decimal tpPartial = isPumpExt ? Math.Clamp((_settings.PumpFirstTakeProfitRatioPct > 0 ? _settings.PumpFirstTakeProfitRatioPct : 40m) / 100m, 0.05m, 0.95m) : 0.4m;
                        decimal trailCb = isPumpExt ? Math.Clamp((_settings.PumpTrailingGapRoe > 0 ? _settings.PumpTrailingGapRoe : 20m) / lev, 0.1m, 5.0m) : 2.0m;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                _orderLifecycle.ResetCooldown(pos.Symbol);
                                var result = await _orderLifecycle.RegisterOnEntryAsync(
                                    pos.Symbol, isLong, pos.EntryPrice, entryQty, lev,
                                    slRoe, tpRoe, tpPartial, trailCb, _cts?.Token ?? CancellationToken.None);
                                OnStatusLog?.Invoke($"🛡️ [외부포지션 보호] {pos.Symbol} SL/TP/Trailing 자동 등록 (SL={!string.IsNullOrEmpty(result.SlOrderId)} TP={!string.IsNullOrEmpty(result.TpOrderId)} TR={!string.IsNullOrEmpty(result.TrailingOrderId)})");
                            }
                            catch (Exception ex)
                            {
                                OnStatusLog?.Invoke($"⚠️ [외부포지션 보호] {pos.Symbol} 등록 실패: {ex.Message}");
                            }
                        });
                    }
                }

                OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                {
                    Symbol = pos.Symbol,
                    IsPositionActive = true,
                    EntryPrice = pos.EntryPrice,
                    PositionSide = isLong ? "LONG" : "SHORT",  // [FIX] PositionSide 설정 추가
                    Quantity = Math.Abs(pos.Quantity),
                    Leverage = uiLeverage
                });
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // [AI Exit] 시장 상태 분류 + 최적 익절 모델 학습
        // ═══════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════
        // [MACD 골든크로스] 메이저 코인 1분봉 MACD 스캔
        // ═══════════════════════════════════════════════════════════════

        private DateTime _lastPumpScanTime = DateTime.MinValue;
        private DateTime _lastAiCommandUpdateTime = DateTime.MinValue;

        /// <summary>AI Command Center를 TickerCache + KlineCache에서 직접 업데이트 (5초 간격)</summary>
        private void UpdateAiCommandFromTickerCache()
        {
            if (OnAiCommandUpdate == null) return;
            if ((DateTime.Now - _lastAiCommandUpdateTime).TotalSeconds < 5) return;
            _lastAiCommandUpdateTime = DateTime.Now;

            // 첫 번째 활성 포지션 또는 첫 번째 메이저 심볼 기준
            string targetSymbol = "";
            string activeDecision = "WAIT";

            lock (_posLock)
            {
                var activePos = _activePositions.Values.FirstOrDefault();
                if (activePos != null)
                {
                    targetSymbol = activePos.Symbol;
                    activeDecision = activePos.IsLong ? "LONG" : "SHORT";
                }
            }

            if (string.IsNullOrWhiteSpace(targetSymbol))
                targetSymbol = "BTCUSDT";

            if (!_marketDataManager.TickerCache.TryGetValue(targetSymbol, out var tick))
                return;

            // KlineCache에서 지표 계산
            float rsi = 50f, macdH = 0f, trend = 0f;
            if (_marketDataManager.KlineCache.TryGetValue(targetSymbol, out var klines) && klines.Count >= 20)
            {
                List<Binance.Net.Interfaces.IBinanceKline> snapshot;
                lock (klines) { snapshot = klines.TakeLast(30).ToList(); }

                if (snapshot.Count >= 14)
                {
                    rsi = (float)IndicatorCalculator.CalculateRSI(snapshot, 14);
                    var macd = IndicatorCalculator.CalculateMACD(snapshot);
                    macdH = (float)macd.Hist;
                    double sma20 = snapshot.TakeLast(20).Average(k => (double)k.ClosePrice);
                    double sma60 = snapshot.Count >= 26 ? snapshot.Average(k => (double)k.ClosePrice) : sma20;
                    trend = sma20 > sma60 ? 0.5f : sma20 < sma60 ? -0.5f : 0f;
                }
            }

            string h4 = trend >= 0.3f ? "TRENDING UP" : trend <= -0.3f ? "TRENDING DOWN" : "NEUTRAL";
            string h1 = rsi >= 60f ? "STRENGTHENING" : rsi >= 45f ? "WATCHING" : rsi <= 30f ? "OVERSOLD" : "NEUTRAL";
            string m15 = macdH > 0 && rsi >= 55f ? "READY TO SHOOT" : macdH > 0 ? "SCANNING" : macdH < 0 ? "BEARISH" : "NEUTRAL";

            double bull = activeDecision == "LONG" ? Math.Min(100, rsi + trend * 30) : Math.Max(0, 100 - rsi);
            double bear = 100.0 - bull;

            OnAiCommandUpdate.Invoke(targetSymbol, (float)(bull / 100.0), activeDecision, h4, h1, m15, bull, bear);
        }

        private DateTime _lastMacdScanTime = DateTime.MinValue;

        private async Task ScanMacdGoldenCrossAsync(CancellationToken token)
        {
            // [v4.5.17] 4시간봉 MACD 크로스 → 15분 주기 스캔 (기존 30초)
            // 4시간봉은 봉 마감이 4시간마다이므로 15분 주기면 충분
            if ((DateTime.Now - _lastMacdScanTime).TotalMinutes < 15) return;
            _lastMacdScanTime = DateTime.Now;

            if (_macdCrossService == null) return;

            // [v4.5.10] 포지션 있어도 스캔 통과 (반대 크로스 시 부분 청산 처리)
            var macdTasks = MajorSymbols.Where(sym =>
            {
                if (_blacklistedSymbols.TryGetValue(sym, out var bExp) && DateTime.Now < bExp) return false;
                return true;
            }).Select(async symbol =>
            {
                try
                {
                    var crossResult = await _macdCrossService.DetectGoldenCrossAsync(symbol, token);
                    if (!crossResult.Detected) return;

                    decimal currentPrice = 0;
                    if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tick))
                        currentPrice = tick.LastPrice;
                    if (currentPrice <= 0) return;

                    // [v4.5.10] 기존 포지션 있을 때 반대 크로스 → 50% 부분 청산
                    bool hasPosition = false;
                    bool positionIsLong = false;
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var existPos))
                        {
                            hasPosition = true;
                            positionIsLong = existPos.IsLong;
                        }
                    }

                    if (hasPosition)
                    {
                        bool isOppositeCross =
                            (positionIsLong && crossResult.CrossType == MacdCrossType.Dead) ||
                            (!positionIsLong && crossResult.CrossType == MacdCrossType.Golden);

                        if (isOppositeCross)
                        {
                            // 일일 리버스 한도 체크 + 실행
                            await HandleOppositeMacdCrossAsync(symbol, positionIsLong, crossResult, token);
                        }
                        // 같은 방향 크로스면 기존 포지션 유지 (아무것도 안 함)
                        return;
                    }

                    // ── 골든크로스 → LONG ──
                    if (crossResult.CrossType == MacdCrossType.Golden)
                    {
                        var (isBullish, htfDetail) = await _macdCrossService.CheckHigherTimeframeBullishAsync(symbol, token);
                        if (!isBullish)
                        {
                            OnStatusLog?.Invoke($"📊 [MACD] {symbol} 골든크로스 but 상위봉 비정배열 → 스킵 | {htfDetail}");
                            return;
                        }

                        bool shouldLong = crossResult.CaseType == "B"
                            || (crossResult.CaseType == "A" && crossResult.RSI < 40);

                        if (!shouldLong)
                        {
                            OnStatusLog?.Invoke($"📊 [MACD] {symbol} LONG Case{crossResult.CaseType} 미충족 (RSI={crossResult.RSI:F1})");
                            return;
                        }

                        string source = $"MACD_GOLDEN_CASE{crossResult.CaseType}";
                        OnAlert?.Invoke($"📈 [MACD 골든크로스] {symbol} Case{crossResult.CaseType} | {crossResult.Detail}");

                        _ = Task.Run(async () =>
                        {
                            try { await TelegramService.Instance.SendMessageAsync(
                                $"📈 *[MACD 골든크로스]*\n`{symbol}` Case {crossResult.CaseType}\nMACD: `{crossResult.MacdLine:F6}`\nRSI: `{crossResult.RSI:F1}`\n⏰ {DateTime.Now:HH:mm:ss}",
                                TelegramMessageType.Entry); } catch { }
                        });

                        await ExecuteAutoOrder(symbol, "LONG", currentPrice, token, source);
                    }

                    // ── 데드크로스 → SHORT ──
                    else if (crossResult.CrossType == MacdCrossType.Dead)
                    {
                        var (isBearish, htfDetail) = await _macdCrossService.CheckHigherTimeframeBearishAsync(symbol, token);
                        if (!isBearish)
                        {
                            OnStatusLog?.Invoke($"📊 [MACD] {symbol} 데드크로스 but 상위봉 비하락 → 스킵 | {htfDetail}");
                            return;
                        }

                        // 숏 유형 A (추세추종): 0선 근처/위 데드크로스 — 가장 안전
                        // 숏 유형 B (변곡점): 히스토그램 급감 + DeadCrossAngle 크기
                        bool shouldShort = crossResult.CaseType == "A"
                            || (crossResult.CaseType == "B" && crossResult.DeadCrossAngle < -0.00001);

                        if (!shouldShort)
                        {
                            OnStatusLog?.Invoke($"📊 [MACD] {symbol} SHORT Case{crossResult.CaseType} 미충족 (Angle={crossResult.DeadCrossAngle:F6})");
                            return;
                        }

                        string source = $"MACD_DEAD_CASE{crossResult.CaseType}";
                        OnAlert?.Invoke($"📉 [MACD 데드크로스] {symbol} Case{crossResult.CaseType} | {crossResult.Detail}");

                        _ = Task.Run(async () =>
                        {
                            try { await TelegramService.Instance.SendMessageAsync(
                                $"📉 *[MACD 데드크로스]*\n`{symbol}` Case {crossResult.CaseType}\nMACD: `{crossResult.MacdLine:F6}`\nAngle: `{crossResult.DeadCrossAngle:F6}`\nRSI: `{crossResult.RSI:F1}`\n⏰ {DateTime.Now:HH:mm:ss}",
                                TelegramMessageType.Entry); } catch { }
                        });

                        await ExecuteAutoOrder(symbol, "SHORT", currentPrice, token, source);
                    }
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [MACD] {symbol} 스캔 오류: {ex.Message}");
                }
            }).ToList();
            if (macdTasks.Count > 0)
                await Task.WhenAll(macdTasks);
        }

        // ═══════════════════════════════════════════════════════════════
        // [15분봉 위꼬리] 음봉 감지 → 1분봉 리테스트 SHORT
        // ═══════════════════════════════════════════════════════════════

        private DateTime _last15mTailScanTime = DateTime.MinValue;

        private async Task Scan15mBearishTailAsync(CancellationToken token)
        {
            // 1분 간격 스캔 (15분봉 완성 시점 근처)
            if ((DateTime.Now - _last15mTailScanTime).TotalMinutes < 1) return;
            _last15mTailScanTime = DateTime.Now;

            if (_macdCrossService == null) return;

            foreach (var symbol in MajorSymbols)
            {
                if (token.IsCancellationRequested) break;
                lock (_posLock) { if (_activePositions.ContainsKey(symbol)) continue; }
                if (_blacklistedSymbols.TryGetValue(symbol, out var exp) && DateTime.Now < exp) continue;

                try
                {
                    var tailResult = await _macdCrossService.Detect15mBearishTailAsync(symbol, token);
                    if (!tailResult.Detected) continue;

                    // 최소 조건: 꼬리 50%+ & 거래량 1.5x+ & 15m MACD 약세
                    if (tailResult.UpperShadowRatio < 0.50f || tailResult.RelativeVolume < 1.5f)
                        continue;

                    // 상위봉 하락세 확인 (불장에서는 짧은 단타만, 하락장에서는 풀베팅)
                    var (isBearish, htfDetail) = await _macdCrossService.CheckHigherTimeframeBearishAsync(symbol, token);
                    if (!isBearish)
                    {
                        OnStatusLog?.Invoke($"🕯️ [15m꼬리] {symbol} 감지 but 상위봉 상승세 → 스킵 | {htfDetail}");
                        continue;
                    }

                    OnAlert?.Invoke($"🕯️ [15m 위꼬리 SHORT] {symbol} 꼬리={tailResult.UpperShadowRatio:P0} Vol={tailResult.RelativeVolume:F1}x | 1분봉 리테스트 대기...");

                    _ = Task.Run(async () =>
                    {
                        try { await TelegramService.Instance.SendMessageAsync(
                            $"🕯️ *[15m 위꼬리 SHORT]*\n`{symbol}` 꼬리 {tailResult.UpperShadowRatio:P0}\n" +
                            $"거래량 {tailResult.RelativeVolume:F1}x\n리테스트 대기: {tailResult.RetestTarget50:F4}~{tailResult.RetestTarget618:F4}\n" +
                            $"손절: {tailResult.CandleHigh:F4}\n⏰ {DateTime.Now:HH:mm:ss}",
                            TelegramMessageType.Entry); } catch { }
                    });

                    // 1분봉 리테스트 대기 (최대 5분)
                    decimal stopLoss = tailResult.CandleHigh * 1.001m; // 고점 +0.1%
                    var (triggered, reason) = await _macdCrossService.WaitForRetestShortTriggerAsync(
                        symbol, tailResult.RetestTarget50, tailResult.RetestTarget618, stopLoss, 300, token);

                    if (triggered)
                    {
                        decimal currentPrice = 0;
                        if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tick))
                            currentPrice = tick.LastPrice;
                        if (currentPrice <= 0) continue;

                        await ExecuteAutoOrder(symbol, "SHORT", currentPrice, token, "TAIL_RETEST_SHORT",
                            customStopLossPrice: stopLoss);
                    }
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [15m꼬리] {symbol} 오류: {ex.Message}");
                }
            }
        }

        private System.Threading.Timer? _modelRetrainTimer;

        // [v4.5.14] 중복 학습 방지 플래그 (OnFirstAltCollectionComplete + 2분 타이머 중복 방지)
        private int _mlTrainingInProgress = 0;

        // [v5.22.13] 초기학습 인프라 통째 제거 — AI 시스템 폐기 (2026-04-29)
        //   IsInitialTrainingComplete / InitialTrainingFlagPath / _isInitialTrainingComplete /
        //   IsInitialTrainingInProgress / InitialTrainingFlagPathForDebug /
        //   OnInitialTrainingProgress / OnInitialTrainingCompleted / OnInitialTrainingDownloadProgress /
        //   _trainedSymbols / IsSymbolTrained / TrainedSymbolCount — 모두 제거

        // [v4.9.0] AI Insight Panel 지원 — 활성 포지션 + 현재가 조회 헬퍼
        public List<TradingBot.Shared.Models.PositionInfo> GetActivePositionSnapshot()
        {
            lock (_posLock)
            {
                return _activePositions.Values
                    .Select(p => new TradingBot.Shared.Models.PositionInfo
                    {
                        Symbol = p.Symbol,
                        IsLong = p.IsLong,
                        EntryPrice = p.EntryPrice,
                        Quantity = p.Quantity,
                        Leverage = p.Leverage,
                        EntryTime = p.EntryTime,
                        TakeProfit = p.TakeProfit,
                        StopLoss = p.StopLoss
                    })
                    .ToList();
            }
        }

        public bool TryGetTickerPrice(string symbol, out decimal price)
        {
            if (_marketDataManager?.TickerCache != null
                && _marketDataManager.TickerCache.TryGetValue(symbol, out var tick)
                && tick.LastPrice > 0)
            {
                price = tick.LastPrice;
                return true;
            }
            price = 0m;
            return false;
        }
        // [v5.22.13] OnSymbolTrained / CanEnterPosition 제거 — AI 시스템 폐기 (2026-04-29)
        public bool CanEnterPosition => true;

        /// <summary>[v3.8.1] 전체 ML 모델 1시간 주기 자동 재학습</summary>
        private void StartModelRetrainTimer()
        {
            // [v5.22.2] 1시간 주기 ML 자동 재학습 비활성화 — 사용자 결정 (2026-04-28)
            //   AI 시스템 전체 폐기, 가드만으로 진입
            OnStatusLog?.Invoke("⏸️ [ML] 자동 재학습 타이머 비활성 (v5.22.2)");
        }

        /// <summary>[v4.2.0] AggTrade WebSocket으로 PUMP 후보 코인의 실시간 체결 데이터 수신</summary>
        private async Task StartAggTradeStreamAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token); // 시작 안정화 대기

                // 전 종목 대신 TickerCache에서 거래량 상위 코인만 구독 (API 제한)
                var topSymbols = _marketDataManager.TickerCache
                    .Where(t => t.Value.QuoteVolume >= 500_000m && t.Value.LastPrice >= 0.001m)
                    .OrderByDescending(t => t.Value.QuoteVolume)
                    .Take(50)
                    .Select(t => t.Key)
                    .ToList();

                if (topSymbols.Count == 0)
                {
                    OnStatusLog?.Invoke("⚠️ [AggTrade] 구독 대상 없음 (TickerCache 미로드)");
                    return;
                }

                OnStatusLog?.Invoke($"📡 [AggTrade] {topSymbols.Count}개 코인 구독 시작");

                var socketClient = new BinanceSocketClient();
                foreach (var sym in topSymbols)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        await socketClient.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(sym, data =>
                        {
                            var d = data.Data;
                            _tickMonitor.ProcessAggTrade(sym, d.Price, d.Quantity, d.BuyerIsMaker, d.TradeTime);
                        }, token);
                    }
                    catch { } // 개별 심볼 실패 무시
                    await Task.Delay(100, token); // 구독 간격 (Rate Limit)
                }

                OnStatusLog?.Invoke($"📡 [AggTrade] {topSymbols.Count}개 코인 구독 완료");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AggTrade] 시작 실패: {ex.Message}");
            }
        }

        private async Task TrainAllModelsAsync(CancellationToken token)
        {
            // [AI 제거] 모든 ML.NET 모델 제거 → 학습 메서드 본체 통째 제거
            await Task.CompletedTask;
            OnStatusLog?.Invoke("ℹ️ [ML] AI 학습 비활성화됨 (AI 시스템 폐기)");
            return;

        }

        // [v5.22.13] TriggerInitialDownloadAndTrainAsync 통째 제거 — AI 시스템 폐기 (2026-04-29)

        /// <summary>IBinanceKline → CandleData 변환 (지표 포함)</summary>
        private List<CandleData> ConvertKlinesToCandleData(string symbol, List<IBinanceKline> klines)
        {
            var result = new List<CandleData>();
            if (klines.Count < 20) return result;

            double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
            var bb = IndicatorCalculator.CalculateBB(klines, 20, 2);
            var macd = IndicatorCalculator.CalculateMACD(klines);
            double atr = IndicatorCalculator.CalculateATR(klines, 14);
            double sma20 = IndicatorCalculator.CalculateSMA(klines, 20);

            for (int i = Math.Max(20, klines.Count - 50); i < klines.Count; i++)
            {
                var k = klines[i];
                result.Add(new CandleData
                {
                    Symbol = symbol,
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice, High = k.HighPrice, Low = k.LowPrice, Close = k.ClosePrice,
                    Volume = (float)k.Volume,
                    RSI = (float)rsi,
                    MACD = (float)macd.Macd, MACD_Signal = (float)macd.Signal, MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    BollingerUpper = (float)bb.Upper, BollingerLower = (float)bb.Lower,
                    SMA_20 = (float)sma20,
                    Price_Change_Pct = k.OpenPrice > 0 ? (float)((k.ClosePrice - k.OpenPrice) / k.OpenPrice * 100) : 0,
                    BB_Width = bb.Mid > 0 ? (float)((bb.Upper - bb.Lower) / bb.Mid * 100) : 0,
                    Volume_Ratio = 1.0f
                });
            }
            return result;
        }

        private async Task TrainExitModelsInternalAsync(CancellationToken token)
        {
            // [AI 제거] Exit 모델 학습 통째 제거
            await Task.CompletedTask;
        }

        // ═══════════════════════════════════════════════════════════════
        // [v3.3.6] Volatility Recovery Zone — 급변동 후 회복 구간 추적
        // 메이저 코인 5%+ 급등/급락 후 반등 시 넓은 손절 적용
        // ═══════════════════════════════════════════════════════════════

        /// <summary>급변동 이벤트 기록 (CRASH/PUMP/SPIKE 핸들러에서 호출)</summary>
        private void RecordVolatilityEvent(string symbol, decimal extremePrice, bool isUpwardSpike)
        {
            if (!MajorSymbols.Contains(symbol)) return;
            _volatilityRecoveryZone[symbol] = (extremePrice, isUpwardSpike, DateTime.Now);
            OnStatusLog?.Invoke($"🌊 [회복구간] {symbol} 등록 | 극단가={extremePrice:F4} 방향={(isUpwardSpike ? "급등↑" : "급락↓")} | 4시간간 넓은 손절 적용");
        }

        /// <summary>심볼이 현재 급변동 회복 구간인지 확인</summary>
        private bool TryGetVolatilityRecoveryInfo(string symbol, out decimal extremePrice, out bool isUpwardSpike)
        {
            extremePrice = 0;
            isUpwardSpike = false;
            if (!_volatilityRecoveryZone.TryGetValue(symbol, out var info))
                return false;

            // 4시간 만료
            if (DateTime.Now - info.EventTime > VolatilityRecoveryDuration)
            {
                _volatilityRecoveryZone.TryRemove(symbol, out _);
                return false;
            }

            extremePrice = info.ExtremePrice;
            isUpwardSpike = info.IsUpwardSpike;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        // [급변 감지] CRASH/PUMP 자동 청산 + 리버스 진입
        // ═══════════════════════════════════════════════════════════════

        private async Task HandleCrashDetectedAsync(List<string> crashCoins, decimal avgDropPct)
        {
            var token = _cts?.Token ?? CancellationToken.None;

            // 텔레그램 알림
            _ = Task.Run(async () =>
            {
                try
                {
                    string coinList = string.Join(", ", crashCoins);
                    await TelegramService.Instance.SendMessageAsync(
                        $"🔴 *[CRASH 감지]*\n" +
                        $"코인: `{coinList}`\n" +
                        $"평균 변동: `{avgDropPct:+0.00;-0.00}%` (1분)\n" +
                        $"→ 보유 LONG 전량 청산 + SHORT 리버스\n" +
                        $"⏰ {DateTime.Now:HH:mm:ss}",
                        TelegramMessageType.Alert);
                }
                catch { }
            });

            // 1. 보유 LONG 포지션 전량 청산
            List<(string symbol, decimal qty, decimal entryPrice)> closedLongs = new();
            List<string> longSymbols;
            lock (_posLock)
            {
                longSymbols = _activePositions
                    .Where(p => p.Value.IsLong && Math.Abs(p.Value.Quantity) > 0)
                    .Select(p => p.Key)
                    .ToList();
            }

            foreach (var sym in longSymbols)
            {
                decimal qty, entry;
                lock (_posLock)
                {
                    if (!_activePositions.TryGetValue(sym, out var pos)) continue;
                    qty = Math.Abs(pos.Quantity);
                    entry = pos.EntryPrice;
                }

                OnAlert?.Invoke($"🔴 [CRASH] {sym} LONG 긴급 청산 (시장 급락 {avgDropPct:0.00}%)");
                await _positionMonitor.ExecuteMarketClose(sym, $"CRASH 감지 긴급 청산 (시장 {avgDropPct:0.00}%)", token);
                closedLongs.Add((sym, qty, entry));
            }

            // [v3.3.6] 급변동 회복 구간 등록 — CRASH 저점 기록
            foreach (var sym in crashCoins)
            {
                if (_marketDataManager.TickerCache.TryGetValue(sym, out var crashTick) && crashTick.LastPrice > 0)
                    RecordVolatilityEvent(sym, crashTick.LastPrice, isUpwardSpike: false);
            }

            // 2. SHORT 독립 진입 (LONG 없어도 급락 코인에 SHORT)
            if (_crashDetector.ReverseEntrySizeRatio > 0)
            {
                // 청산한 심볼 + 급락 감지 심볼 합산 (중복 제거)
                var shortTargets = new HashSet<string>(crashCoins, StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, _, _) in closedLongs)
                    shortTargets.Add(sym);

                foreach (var sym in shortTargets)
                {
                    if (!MajorSymbols.Contains(sym)) continue;
                    lock (_posLock) { if (_activePositions.TryGetValue(sym, out var chk) && chk.IsOwnPosition) continue; } // 이미 보유 중 스킵

                    try
                    {
                        decimal currentPrice = 0;
                        if (_marketDataManager.TickerCache.TryGetValue(sym, out var tick))
                            currentPrice = tick.LastPrice;
                        if (currentPrice <= 0) continue;

                        OnAlert?.Invoke($"🔄 [CRASH→SHORT] {sym} 독립 진입 시도 ({_crashDetector.ReverseEntrySizeRatio:P0} 사이즈)");
                        await ExecuteAutoOrder(sym, "SHORT", currentPrice, token, "CRASH_REVERSE",
                            manualSizeMultiplier: _crashDetector.ReverseEntrySizeRatio);
                    }
                    catch (Exception ex)
                    {
                        OnAlert?.Invoke($"⚠️ [CRASH→SHORT] {sym} 진입 실패: {ex.Message}");
                    }
                }
            }
        }

        private async Task HandlePumpDetectedAsync(List<string> pumpCoins, decimal avgRisePct)
        {
            var token = _cts?.Token ?? CancellationToken.None;

            // 텔레그램 알림
            _ = Task.Run(async () =>
            {
                try
                {
                    string coinList = string.Join(", ", pumpCoins);
                    await TelegramService.Instance.SendMessageAsync(
                        $"🟢 *[PUMP 감지]*\n" +
                        $"코인: `{coinList}`\n" +
                        $"평균 변동: `{avgRisePct:+0.00;-0.00}%` (1분)\n" +
                        $"→ 보유 SHORT 전량 청산 + LONG 리버스\n" +
                        $"⏰ {DateTime.Now:HH:mm:ss}",
                        TelegramMessageType.Alert);
                }
                catch { }
            });

            // 1. 보유 SHORT 포지션 전량 청산
            List<(string symbol, decimal qty, decimal entryPrice)> closedShorts = new();
            List<string> shortSymbols;
            lock (_posLock)
            {
                shortSymbols = _activePositions
                    .Where(p => !p.Value.IsLong && Math.Abs(p.Value.Quantity) > 0)
                    .Select(p => p.Key)
                    .ToList();
            }

            foreach (var sym in shortSymbols)
            {
                decimal qty, entry;
                lock (_posLock)
                {
                    if (!_activePositions.TryGetValue(sym, out var pos)) continue;
                    qty = Math.Abs(pos.Quantity);
                    entry = pos.EntryPrice;
                }

                OnAlert?.Invoke($"🟢 [PUMP] {sym} SHORT 긴급 청산 (시장 급등 {avgRisePct:+0.00}%)");
                await _positionMonitor.ExecuteMarketClose(sym, $"PUMP 감지 긴급 청산 (시장 {avgRisePct:+0.00}%)", token);
                closedShorts.Add((sym, qty, entry));
            }

            // [v3.3.6] 급변동 회복 구간 등록 — PUMP 고점 기록
            foreach (var sym in pumpCoins)
            {
                if (_marketDataManager.TickerCache.TryGetValue(sym, out var pumpTick) && pumpTick.LastPrice > 0)
                    RecordVolatilityEvent(sym, pumpTick.LastPrice, isUpwardSpike: true);
            }

            // 2. LONG 독립 진입 (SHORT 없어도 급등 코인에 LONG)
            if (_crashDetector.ReverseEntrySizeRatio > 0)
            {
                var longTargets = new HashSet<string>(pumpCoins, StringComparer.OrdinalIgnoreCase);
                foreach (var (sym, _, _) in closedShorts)
                    longTargets.Add(sym);

                foreach (var sym in longTargets)
                {
                    if (!MajorSymbols.Contains(sym)) continue;
                    lock (_posLock) { if (_activePositions.ContainsKey(sym)) continue; }

                    try
                    {
                        decimal currentPrice = 0;
                        if (_marketDataManager.TickerCache.TryGetValue(sym, out var tick))
                            currentPrice = tick.LastPrice;
                        if (currentPrice <= 0) continue;

                        OnAlert?.Invoke($"🔄 [PUMP→LONG] {sym} 독립 진입 시도 ({_crashDetector.ReverseEntrySizeRatio:P0} 사이즈)");
                        await ExecuteAutoOrder(sym, "LONG", currentPrice, token, "PUMP_REVERSE",
                            manualSizeMultiplier: _crashDetector.ReverseEntrySizeRatio);
                    }
                    catch (Exception ex)
                    {
                        OnAlert?.Invoke($"⚠️ [PUMP→LONG] {sym} 리버스 실패: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>개별 코인 급등 감지 → 즉시 PUMP 진입 시도 (PumpScan 스킵)</summary>
        /// <summary>[v3.2.7] 급등/급락 감지 → AI 판단 후 진입</summary>
        /// <summary>[v3.2.16] 급등/급락 즉시 주문 — ExecuteAutoOrder 스킵 (API 4분 지연 제거)</summary>
        private static readonly HashSet<string> _stablecoinSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "USDCUSDT", "BUSDUSDT", "FDUSDUSDT", "TUSDUSDT", "DAIUSDT", "USDPUSDT", "EURUSDT"
        };

        private async Task HandleSpikeDetectedAsync(string symbol, decimal changePct, decimal currentPrice)
        {
            if (_stablecoinSymbols.Contains(symbol)) return; // 스테이블코인 제외
            var token = _cts?.Token ?? CancellationToken.None;

            // [v5.12.0] 급등 단일 슬롯 체크 — SPIKE_FAST + MAJOR_MEME 통합 1개만 동시 진입
            if (_activeSpikeSlot.Count >= MAX_SPIKE_CATEGORY_SLOTS)
            {
                var occupant = _activeSpikeSlot.FirstOrDefault();
                OnStatusLog?.Invoke($"⛔ [SPIKE_SLOT] {symbol} 거부: 급등 슬롯 {occupant.Key}({occupant.Value.source}) 점유중 — 1개 제한");
                return;
            }

            // [v4.6.0] SPIKE 감지 시 동적 수집 등록 + 즉시 백필
            _marketHistoryService?.RegisterSymbol(symbol);
            _ = _marketHistoryService?.RequestBackfillAsync(symbol, token);

            // [v5.22.13] SPIKE 동적학습등록 제거 — AI 시스템 폐기 (2026-04-29)

            bool isMajor = MajorSymbols.Contains(symbol);

            // [v4.5.11] 일일 수익 모드 기반 보수 모드 체크 (PUMP 알트만)
            decimal profitModeSizeMul = 1.0m;
            float profitModeAiBoost = 0f;
            if (!isMajor)
            {
                var (allowed, sizeMul, aiBoost) = CheckDailyProfitModeForPumpEntry(symbol, "SPIKE_FAST");
                if (!allowed) return;
                profitModeSizeMul = sizeMul;
                profitModeAiBoost = aiBoost;

                // [v4.5.9] 일일 PUMP 진입 한도(40회) 체크
                if (!TryReserveDailyPumpEntry(symbol, "SPIKE_FAST"))
                    return;
            }

            // [v3.3.6] 메이저 코인 급변동 → 회복 구간 등록
            if (isMajor && Math.Abs(changePct) >= 3.0m)
                RecordVolatilityEvent(symbol, currentPrice, isUpwardSpike: changePct > 0);

            // [v3.2.38] 슬롯 체크 + 리버스 처리
            bool needsReverse = false;
            string? evictSymbol = null;
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var existingPos) && existingPos.IsOwnPosition)
                {
                    // 같은 방향이면 스킵
                    bool existingIsLong = existingPos.IsLong;
                    bool newIsLong = changePct > 0;
                    if (existingIsLong == newIsLong) return;

                    // 반대 방향 → 리버스 (기존 청산 후 신규 진입)
                    needsReverse = true;
                }

                // [v5.2.2] IsOwnPosition만 카운트
                int total = _activePositions.Count(p => p.Value.IsOwnPosition);
                int majorCount = _activePositions.Count(p => p.Value.IsOwnPosition && MajorSymbols.Contains(p.Key));
                int pumpCount = total - majorCount;

                if (isMajor && majorCount >= MAX_MAJOR_SLOTS) return;
                if (total >= MAX_TOTAL_SLOTS && pumpCount == 0) return;

                // [v3.3.0] PUMP 슬롯 포화 시 → 수익 10%+ 코인 익절해서 슬롯 확보
                if (!isMajor && pumpCount >= MAX_PUMP_SLOTS)
                {
                    // 수익 10%+ PUMP 코인 찾기
                    foreach (var kvp in _activePositions)
                    {
                        if (MajorSymbols.Contains(kvp.Key)) continue;
                        if (kvp.Value.EntryPrice <= 0) continue;

                        decimal curPrice = 0;
                        if (_marketDataManager.TickerCache.TryGetValue(kvp.Key, out var t))
                            curPrice = t.LastPrice;
                        if (curPrice <= 0) continue;

                        decimal priceDiff = kvp.Value.IsLong
                            ? (curPrice - kvp.Value.EntryPrice)
                            : (kvp.Value.EntryPrice - curPrice);
                        decimal roe = (priceDiff / kvp.Value.EntryPrice) * kvp.Value.Leverage * 100;

                        if (roe >= 10m)
                        {
                            evictSymbol = kvp.Key;
                            break;
                        }
                    }

                    if (evictSymbol == null) return; // 익절 가능한 코인 없으면 차단
                }

                // 즉시 예약 등록 (중복 진입 차단)
                _activePositions[symbol] = new PositionInfo
                {
                    Symbol = symbol,
                    EntryPrice = currentPrice,
                    IsLong = changePct > 0,
                    EntryTime = DateTime.Now,
                    IsOwnPosition = true // [v5.10.39]
                };
            }

            // [v3.3.0] 수익 10%+ 코인 익절 (lock 밖에서 실행)
            if (evictSymbol != null)
            {
                OnAlert?.Invoke($"🔄 [슬롯 확보] {evictSymbol} ROE 10%+ 익절 → {symbol} 진입 슬롯 확보");
                try
                {
                    await _positionMonitor.ExecuteMarketClose(evictSymbol, $"급등 코인 슬롯 확보 익절 ({symbol} 진입)", token);
                    await Task.Delay(300, token);
                }
                catch (Exception evictEx)
                {
                    OnStatusLog?.Invoke($"⚠️ [{evictSymbol}] 슬롯 확보 익절 실패: {evictEx.Message}");
                }
            }

            // [v3.2.38] 리버스: 기존 반대 포지션 청산
            if (needsReverse)
            {
                // [v4.5.10] 심볼별 일일 리버스 한도 (3회) 체크
                if (!TryReserveDailyReversal(symbol, "SPIKE_REVERSE"))
                {
                    lock (_posLock) { _activePositions.Remove(symbol); } // 예약 취소
                    return;
                }

                string reverseLabel = changePct > 0 ? "숏→롱" : "롱→숏";
                OnAlert?.Invoke($"🔄 [{reverseLabel} 리버스] {symbol} 기존 포지션 청산 중...");
                try
                {
                    await _positionMonitor.ExecuteMarketClose(symbol, $"SPIKE 리버스 ({reverseLabel})", token);
                    await Task.Delay(500, token);
                }
                catch (Exception revEx)
                {
                    OnStatusLog?.Invoke($"⚠️ [{symbol}] 리버스 청산 실패: {revEx.Message}");
                    lock (_posLock) { _activePositions.Remove(symbol); }
                    return;
                }
            }

            if (_blacklistedSymbols.TryGetValue(symbol, out var expiry) && DateTime.Now < expiry)
            {
                lock (_posLock) { _activePositions.Remove(symbol); } // 예약 취소
                return;
            }

            string side = changePct > 0 ? "BUY" : "SELL";
            string direction = changePct > 0 ? "LONG" : "SHORT";
            string label = changePct > 0 ? "급등" : "급락";

            // 사이즈 계산 — 가용 잔고 체크 포함
            decimal leverage = _settings.MajorLeverage > 0 ? _settings.MajorLeverage : 20;
            decimal marginUsdt = isMajor
                ? await GetAdaptiveEntryMarginUsdtAsync(token)
                : GetLiquidityAdjustedPumpMarginUsdt(symbol);

            // [v4.5.11] 일일 수익 모드 사이즈 배수 적용 (PUMP만)
            if (!isMajor && profitModeSizeMul < 1.0m)
            {
                decimal adjusted = marginUsdt * profitModeSizeMul;
                OnStatusLog?.Invoke($"🛡️ [보수모드] {symbol} 마진 {marginUsdt:F0} → {adjusted:F0} (x{profitModeSizeMul:F2})");
                marginUsdt = adjusted;
            }

            // [v5.1.8] 가용 잔고 체크 — GetAvailableBalanceAsync 사용
            try
            {
                decimal available = await _exchangeService.GetAvailableBalanceAsync("USDT", token);
                if (available < marginUsdt)
                {
                    OnStatusLog?.Invoke($"⚠️ [{label}] {symbol} 마진 부족 (필요={marginUsdt:F0}, 가용={available:F0}) → 스킵");
                    lock (_posLock) { _activePositions.Remove(symbol); }
                    return;
                }
            }
            catch { }

            decimal rawQty = (marginUsdt * leverage) / currentPrice;
            // [v3.2.38] 소수점 제거 — 대부분 PUMP 코인 stepSize는 1 이상
            decimal quantity = currentPrice < 0.01m ? Math.Floor(rawQty) : Math.Round(rawQty, 2, MidpointRounding.ToZero);

            if (quantity <= 0)
            {
                OnStatusLog?.Invoke($"⚠️ [{label}] {symbol} 수량 0 → 스킵");
                lock (_posLock) { _activePositions.Remove(symbol); }
                return;
            }

            // [v5.11.1] SPIKE_FAST 알림/UI/텔레그램 "즉시진입" 메시지를 게이트 통과 후로 이동
            //   기존: 게이트 검증 전 사용자에게 "즉시진입" 알림 → 게이트 탈락해도 phantom 진입 보임
            //   수정: OnAlert/OnSignalUpdate/Telegram 전부 PlaceEntryOrderAsync 직전으로 이동 (line ~8643)

            // [AI 제거] AdaptiveSpikeScheduler 상태 변수 제거 — 단순 진입 흐름

            try
            {
                // [v3.3.4] 급등 타이밍 체크 — 감지 시점 대비 +5% 이상 올랐으면 진입 취소
                decimal detectPrice = currentPrice; // 감지 시점 가격
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var nowTick) && nowTick.LastPrice > 0)
                {
                    decimal priceGain = (nowTick.LastPrice - detectPrice) / detectPrice * 100;
                    if (priceGain >= 5m)
                    {
                        OnStatusLog?.Invoke($"⚠️ [SPIKE_FAST] {symbol} 감지 후 +{priceGain:F1}% 추가 상승 → 타이밍 지남, 진입 취소");
                        lock (_posLock) { _activePositions.Remove(symbol); }
                        return;
                    }
                    currentPrice = nowTick.LastPrice; // 최신 가격으로 갱신
                }

                // SPIKE_FAST: BTC 체크는 메이저 코인만 (PUMP는 BTC와 독립)
                if (direction == "LONG" && isMajor)
                {
                    if (_marketDataManager.TickerCache.TryGetValue("BTCUSDT", out var spkBtc) && spkBtc.LastPrice > 0
                        && _marketDataManager.KlineCache.TryGetValue("BTCUSDT", out var spkBtcK) && spkBtcK.Count >= 12)
                    {
                        List<IBinanceKline> spkBtcSnap;
                        lock (spkBtcK) { spkBtcSnap = spkBtcK.TakeLast(12).ToList(); }
                        if (spkBtcSnap.Count >= 12)
                        {
                            decimal btcChg = (spkBtc.LastPrice - spkBtcSnap[0].OpenPrice) / spkBtcSnap[0].OpenPrice * 100m;
                            if (btcChg <= -2.0m)
                            {
                                OnStatusLog?.Invoke($"⛔ [SPIKE_FAST] {symbol} BTC 하락장 {btcChg:F1}% → LONG 스킵");
                                lock (_posLock) { _activePositions.Remove(symbol); }
                                return;
                            }
                        }
                    }
                }

                // [v3.3.2] RSI 과열 체크 + 눌림 대기 (고점 매수 방지)
                if (direction == "LONG")
                {
                    try
                    {
                        var klines = await _exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneMinute, 20, token);
                        if (klines != null && klines.Count >= 14)
                        {
                            // [v3.7.2] 초고변동성 차단 — ATR/가격 3%+ = 대응 불가
                            double spikeAtr = IndicatorCalculator.CalculateATR(klines.ToList(), Math.Min(7, klines.Count - 1));
                            if (spikeAtr > 0 && currentPrice > 0)
                            {
                                double atrPctSpike = spikeAtr / (double)currentPrice * 100;
                                if (atrPctSpike >= 3.0)
                                {
                                    OnStatusLog?.Invoke($"⛔ [SPIKE_FAST] {symbol} ATR/가격={atrPctSpike:F1}% ≥ 3% → 초고변동성 스킵");
                                    lock (_posLock) { _activePositions.Remove(symbol); }
                                    return;
                                }
                            }

                            // [v4.9.9] MTF Guardian — 5분봉 하락추세면 1분 반짝 상승 무시
                            // BASUSDT 버그: 5분 12봉 연속 하락 중 1분 +1% 로 진입 → 물림
                            if (!PassMtfGuardian(symbol, "LONG", out string spikeMtfReason))
                            {
                                OnStatusLog?.Invoke($"⛔ [SPIKE_FAST] {symbol} MTF 차단: {spikeMtfReason}");
                                lock (_posLock) { _activePositions.Remove(symbol); }
                                return;
                            }

                            // [v5.0.1] Gate 1: 5분봉 기준 "이미 너무 올랐음" 차단
                            // TRUUSDT/AIOT/TAG/SKYAI 는 SPIKE_FAST 경로로 진입했음
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var spkGate1Cache))
                            {
                                List<IBinanceKline> spkGate1Candles;
                                lock (spkGate1Cache) { spkGate1Candles = spkGate1Cache.ToList(); }

                                if (IsAlreadyPumpedRecently(spkGate1Candles, currentPrice, out string spkGate1Reason))
                                {
                                    OnStatusLog?.Invoke($"⛔ [SPIKE_FAST] {symbol} Gate1 차단: {spkGate1Reason} → Gate2 지연 예약");
                                    lock (_posLock) { _activePositions.Remove(symbol); }
                                    ScheduleGate2Reevaluation(symbol, "LONG", "SPIKE");
                                    return;
                                }
                            }

                            // [AI 제거] SpikeForecaster 검증 통째 제거

                            double rsi = IndicatorCalculator.CalculateRSI(klines.ToList(), 14);
                            // [v3.6.2] RSI 80→75로 강화 (꼭대기 진입 방지)
                            if (rsi >= 75)
                            {
                                OnStatusLog?.Invoke($"⚠️ [SPIKE_FAST] {symbol} RSI={rsi:F1} ≥ 75 과열 → 스킵");
                                lock (_posLock) { _activePositions.Remove(symbol); }
                                return;
                            }

                            // [AI 제거] SPIKE_FAST AI 게이트 검증 제거
                            if (direction == "LONG")
                            {
                            }

                            // [v4.6.1] 1분 로켓 발사 대응 — 눌림 대기 폐기, 3가지 진입 OR 조건
                            // 1) 누적 +2% 도달 → 즉시 진입 (로켓 발사 케이스)
                            // 2) 고점 대비 -0.5% 눌림 → 진입 (기존 -1%에서 완화)
                            // 3) 30초 경과 → 강제 진입 (놓치기보다 진입)
                            // [v5.14.0 AI #5] AdaptiveSpikeScheduler 연동 — 학습된 bucket winrate 로 Decide
                            decimal spikeStart = currentPrice;
                            decimal spikeHigh = currentPrice;
                            var spikeStartTime = DateTime.Now;
                            var deadline = spikeStartTime.AddSeconds(30);
                            bool entryReady = false;
                            string entryReason = "";
                            // stateAtEntry 는 외부 schedulerStateAtEntry 로 대체됨 (try 외부 선언)

                            while (DateTime.Now < deadline && !token.IsCancellationRequested)
                            {
                                await Task.Delay(1500, token);
                                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var latestTick))
                                {
                                    decimal nowPrice = latestTick.LastPrice;
                                    if (nowPrice > spikeHigh) spikeHigh = nowPrice;

                                    double cumGainPct = (double)((nowPrice - spikeStart) / spikeStart * 100m);
                                    double pullbackPct = spikeHigh > 0 ? (double)((spikeHigh - nowPrice) / spikeHigh * 100m) : 0;

                                    // [AI 제거] AdaptiveSpikeScheduler 제거 → rule-based만 사용
                                    if (cumGainPct >= 2.0)
                                    {
                                        currentPrice = nowPrice;
                                        entryReady = true;
                                        entryReason = $"rule_cum+{cumGainPct:F1}%";
                                        break;
                                    }
                                    if (pullbackPct >= 0.5)
                                    {
                                        currentPrice = nowPrice;
                                        entryReady = true;
                                        entryReason = $"rule_pullback-{pullbackPct:F1}%";
                                        break;
                                    }
                                }
                            }

                            // 3) 30초 경과 — 강제 진입 (스킵 안 함)
                            if (!entryReady)
                            {
                                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var finalTick))
                                {
                                    currentPrice = finalTick.LastPrice;
                                    decimal finalGain = (currentPrice - spikeStart) / spikeStart * 100m;
                                    entryReason = $"30초경과({finalGain:+0.0;-0.0}%)";
                                    entryReady = true;
                                }
                            }

                            if (entryReady)
                            {
                                quantity = currentPrice < 0.01m ? Math.Floor((marginUsdt * leverage) / currentPrice) : Math.Round((marginUsdt * leverage) / currentPrice, 2, MidpointRounding.ToZero);
                                OnStatusLog?.Invoke($"✅ [SPIKE_FAST] {symbol} 진입 트리거: {entryReason} px={currentPrice}");
                            }
                            else
                            {
                                OnStatusLog?.Invoke($"⚠️ [SPIKE_FAST] {symbol} TickerCache 조회 실패 → 스킵");
                                lock (_posLock) { _activePositions.Remove(symbol); }
                                return;
                            }
                        }
                    }
                    catch (Exception rsiEx)
                    {
                        OnStatusLog?.Invoke($"⚠️ [SPIKE_FAST] {symbol} RSI 체크 실패: {rsiEx.Message} → 진입 계속");
                    }
                }

                // [v5.11.1] 모든 게이트 검증 통과 → 사용자 알림 + UI 갱신 + 텔레그램
                //   이 지점 도달 = BTC 방향/RSI/MTF Guardian/Gate1/SpikeForecaster 전부 통과한 실제 진입
                OnAlert?.Invoke($"⚡⚡ [{label} 즉시진입] {symbol} {changePct:+0.0;-0.0}% → {direction} qty={quantity:F4}");
                OnSymbolTracking?.Invoke(symbol);
                OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                {
                    Symbol = symbol,
                    LastPrice = currentPrice,
                    Decision = direction,
                    SignalSource = "SPIKE_FAST",
                    StrategyName = $"⚡Spike {changePct:+0.0;-0.0}%"
                });
                _ = Task.Run(async () =>
                {
                    try { await TelegramService.Instance.SendMessageAsync(
                        $"⚡⚡ *[{label} 즉시진입]*\n`{symbol}` {changePct:+0.0;-0.0}%\n방향: {direction}\n가격: `{currentPrice}`\n⏰ {DateTime.Now:HH:mm:ss}",
                        TelegramMessageType.Entry); } catch { }
                });

                OnStatusLog?.Invoke($"[SPIKE_FAST] {symbol} {side} qty={quantity:F2} margin={marginUsdt:F0} lev={leverage} px={currentPrice}");

                // [v5.10.100 ROOT FIX] SPIKE_FAST 레버리지 자동조정 누락 — Binance 실제 leverage 사용
                //   기존: leverage 시도값 그대로 PositionInfo 저장 → Binance 실제 5x인데 봇 25x 기록 → UI ROE 5배 부풀림
                //   수정: SetLeverageAutoAsync 호출 후 actualLeverage로 갱신
                try
                {
                    int spikeActualLev = await _exchangeService.SetLeverageAutoAsync(symbol, (int)leverage, token);
                    if (spikeActualLev > 0)
                    {
                        if (spikeActualLev != (int)leverage)
                            OnStatusLog?.Invoke($"⚙️ [SPIKE_FAST][LEVERAGE_ADJUSTED] {symbol} 시도={leverage}x → 실제={spikeActualLev}x");
                        leverage = spikeActualLev;
                    }
                }
                catch (Exception slEx) { OnStatusLog?.Invoke($"⚠️ [SPIKE_FAST] {symbol} 레버리지 조정 예외: {slEx.Message}"); }

                // [v5.10.81] 단일 진입점 PlaceEntryOrderAsync 사용 — EnableMajorTrading 등 글로벌 게이트 강제
                var spikeResult = await PlaceEntryOrderAsync(symbol, side, quantity, "SPIKE_FAST", token);
                bool success = spikeResult.Success;

                if (success)
                {
                    // [v5.12.0] 급등 단일 슬롯 등록 — SPIKE_FAST 실제 체결 확정 시
                    _activeSpikeSlot[symbol] = (DateTime.Now, "SPIKE_FAST");
                    OnStatusLog?.Invoke($"🔒 [SPIKE_SLOT] {symbol} SPIKE_FAST 슬롯 점유 (1/{MAX_SPIKE_CATEGORY_SLOTS})");

                    // [AI 제거] AdaptiveSpikeScheduler 학습 피드백 통째 제거

                    // [v5.10.17] SPIKE_FAST PUMP 실제 진입 확정 카운터
                    if (!isMajor) CommitDailyPumpEntry(symbol);

                    // [v3.2.36] 거래소에서 실제 체결 수량/진입가 확인
                    decimal entryPrice = currentPrice;
                    decimal actualQty = quantity;
                    try
                    {
                        await Task.Delay(300, token); // 체결 대기
                        var positions = await _exchangeService.GetPositionsAsync(ct: token);
                        var realPos = positions?.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);
                        if (realPos != null)
                        {
                            actualQty = Math.Abs(realPos.Quantity);
                            if (realPos.EntryPrice > 0) entryPrice = realPos.EntryPrice;
                        }
                    }
                    catch { }

                    // 내부 포지션 등록 (거래소 실제 수량)
                    // [v3.3.6] SPIKE 진입 시 회복 구간 체크
                    bool spikeRecovery = false;
                    decimal spikeRecoveryExtreme = 0;
                    if (isMajor && TryGetVolatilityRecoveryInfo(symbol, out var spkExtreme, out _))
                    {
                        spikeRecovery = true;
                        spikeRecoveryExtreme = spkExtreme;
                    }
                    lock (_posLock)
                    {
                        _activePositions[symbol] = new PositionInfo
                        {
                            Symbol = symbol,
                            EntryPrice = entryPrice,
                            IsLong = (direction == "LONG"),
                            Side = (direction == "LONG") ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell,
                            Quantity = direction == "LONG" ? actualQty : -actualQty,
                            Leverage = (int)leverage,
                            IsPumpStrategy = !isMajor,
                            IsVolatilityRecovery = spikeRecovery,
                            RecoveryExtremePrice = spikeRecoveryExtreme,
                            EntryTime = DateTime.Now,
                            IsOwnPosition = true // [v5.10.39]
                        };
                    }

                    OnPositionStatusUpdate?.Invoke(symbol, true, entryPrice);
                    OnAlert?.Invoke($"✅ [{label} 즉시진입 성공] {symbol} {direction} @ {entryPrice:F8} qty={actualQty:F4}");

                    bool isLong = direction == "LONG";

                    // [v5.10.83 HOTFIX] SPIKE_FAST 경로 SL/TP/Trailing 미등록 버그 수정
                    //   기존: PlaceEntryOrderAsync 만 호출 → SL/TP 미등록 → -32% 손실 무방비 (PIEVERSEUSDT 사고)
                    //   수정: 진입 직후 RegisterProtectionOrdersAsync 강제 호출
                    await RegisterProtectionOrdersAsync(symbol, isLong, actualQty, entryPrice, (int)leverage, $"SPIKE_FAST_{label}", token);

                    // 포지션 감시 시작
                    if (isMajor)
                        _ = _positionMonitor.MonitorPositionStandard(symbol, entryPrice, isLong, token);
                    else
                        _ = _positionMonitor.MonitorPumpPositionShortTerm(symbol, entryPrice, $"SPIKE_FAST_{label}", 0, token);

                    // DB 기록 (비동기)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _dbManager.UpsertTradeEntryAsync(new TradeLog(
                                symbol, side, $"SPIKE_FAST_{label}", entryPrice, 0, DateTime.Now, 0, 0)
                            {
                                EntryPrice = entryPrice,
                                Quantity = Math.Abs(quantity),
                                EntryTime = DateTime.Now
                            });
                        }
                        catch { }
                    });
                }
                else
                {
                    OnAlert?.Invoke($"❌ [{label} 즉시진입 실패] {symbol} {side} qty={quantity:F2} px={currentPrice} margin={marginUsdt:F0}");
                    lock (_posLock) { _activePositions.Remove(symbol); } // 예약 해제
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [{label} 즉시진입] {symbol} 오류: {ex.Message}");
                lock (_posLock) { _activePositions.Remove(symbol); } // 예약 해제
            }
        }

        private void HandleAllTickerUpdate(IEnumerable<IBinance24HPrice> ticks)
        {
            try
            {
                if (ticks == null)
                    return;

                // [급변 감지] 15초 가격 변동률 체크 (v4.5.5: 60→15초)
                _crashDetector.CheckPriceVelocity(_marketDataManager.TickerCache);
                _crashDetector.CheckSpikeDetection(_marketDataManager.TickerCache);

                // [v4.5.5] 알트 불장 자동 감지 (5분 주기, 내부 throttle)
                _altBullDetector.Check(_marketDataManager.TickerCache);

                var trackedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // [v5.22.20] 메이저 4 항상 포함 — Binance.Net 12.8.1 SubscribeToTickerUpdatesAsync(_majorSymbols)
                //   콜백이 무동작인 회귀 (v5.22.19 TICK_HEARTBEAT 4분 0건 확인) 우회.
                //   전체 시세 스트림 (SubscribeToAllTickerUpdatesAsync) 은 정상 작동하므로
                //   메이저를 trackedSymbols 에 강제 포함시키면 메이저 가격이 HandleTickerUpdate 로 흘러감.
                foreach (var m in FixedMajorPool)
                    trackedSymbols.Add(m);

                lock (_posLock)
                {
                    foreach (var symbol in _activePositions.Keys)
                    {
                        if (TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                            trackedSymbols.Add(normalizedSymbol);
                    }
                }

                foreach (var symbol in _uiTrackedSymbols.Keys)
                {
                    if (TryNormalizeTradingSymbol(symbol, out var normalizedSymbol))
                        trackedSymbols.Add(normalizedSymbol);
                }

                if (trackedSymbols.Count == 0)
                    return;

                foreach (var tick in ticks)
                {
                    if (tick == null || !TryNormalizeTradingSymbol(tick.Symbol, out var symbol))
                        continue;

                    if (!trackedSymbols.Contains(symbol))
                        continue;

                    HandleTickerUpdate(tick);
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AllTickerCallback] 처리 오류: {ex.Message}");
            }
        }

        private void HandleTickerUpdate(IBinance24HPrice tick)
        {
            try
            {
                if (tick == null || !TryNormalizeTradingSymbol(tick.Symbol, out var symbol))
                    return;

                var now = DateTime.Now;
                if (_lastTickerUpdateTimes.TryGetValue(symbol, out var lastTime))
                {
                    if ((now - lastTime).TotalSeconds < 1) return;
                }
                _lastTickerUpdateTimes[symbol] = now;

                if (_exchangeService is MockExchangeService mockExchange)
                {
                    mockExchange.SetCurrentPrice(symbol, tick.LastPrice);
                }

                // [실시간 트레일링 스탑] 포지션이 있는 심볼에 대해 실시간 가격 추적
                if (_hybridExitManager.HasState(symbol))
                {
                    _hybridExitManager.UpdateRealtimePriceTracking(symbol, tick.LastPrice);
                }

                // TryWrite를 사용하여 채널이 꽉 찼을 때 블로킹되지 않도록 함
                _tickerChannel.Writer.TryWrite(tick);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [TickerCallback] {tick?.Symbol ?? "UNKNOWN"} 처리 오류: {ex.Message}");
            }
        }

        #endregion

        #region [ 자동 주문 및 자산 배분 로직 ]

        private void TryStartStandardMonitor(
            string symbol,
            decimal entryPrice,
            bool isLong,
            string mode,
            decimal customTakeProfitPrice,
            decimal customStopLossPrice,
            CancellationToken token,
            string source)
        {
            if (token.IsCancellationRequested)
                return;

            if (!_runningStandardMonitors.TryAdd(symbol, 0))
                return;

            OnStatusLog?.Invoke($"🔁 {symbol} 표준 포지션 감시 연결 ({source})");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _positionMonitor.MonitorPositionStandard(symbol, entryPrice, isLong, token, mode, customTakeProfitPrice, customStopLossPrice);
                }
                finally
                {
                    _runningStandardMonitors.TryRemove(symbol, out _);
                }
            }, token);
        }

        private void TryStartPumpMonitor(
            string symbol,
            decimal entryPrice,
            string strategyName,
            double atr,
            CancellationToken token,
            string source)
        {
            if (token.IsCancellationRequested)
                return;

            if (!_runningPumpMonitors.TryAdd(symbol, 0))
                return;

            OnStatusLog?.Invoke($"🚨 {symbol} PUMP 포지션 감시 연결 ({source})");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _positionMonitor.MonitorPumpPositionShortTerm(symbol, entryPrice, strategyName, atr, token);
                }
                finally
                {
                    _runningPumpMonitors.TryRemove(symbol, out _);
                }
            }, token);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
        // [개선안 1~3] SLOT 최적화 헬퍼 메서드들
        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [개선안 2] SLOT 쿨다운 체크: 최근 SLOT 차단된 심볼은 재시도 금지
        /// </summary>
        private bool IsSymbolInSlotCooldown(string symbol, out int remainingSeconds)
        {
            remainingSeconds = 0;
            
            if (_slotBlockedSymbols.TryGetValue(symbol, out var blockTime))
            {
                var elapsed = DateTime.Now - blockTime;
                if (elapsed.TotalMinutes < _slotCooldownMinutes)
                {
                    remainingSeconds = (int)((_slotCooldownMinutes * 60) - elapsed.TotalSeconds);
                    return true;
                }
                else
                {
                    // 쿨다운 만료 → 제거
                    _slotBlockedSymbols.TryRemove(symbol, out _);
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// [개선안 2] SLOT 차단 기록: 향후 쿨다운 적용
        /// </summary>
        private void RecordSlotBlockage(string symbol)
        {
            _slotBlockedSymbols[symbol] = DateTime.Now;
        }
        
        /// <summary>
        /// 고정 총 슬롯
        /// </summary>
        private int GetDynamicMaxTotalSlots()
        {
            return MAX_TOTAL_SLOTS;
        }
        
        /// <summary>
        /// [v5.10.2] PUMP 슬롯 — DB 설정값 반환
        /// [v5.10.69] KST 9시±15분 펌프 시간대에 슬롯 +1 (한국 시장 진입 시간 = 펌프 집중)
        /// </summary>
        private int GetDynamicMaxPumpSlots()
        {
            // [v5.19.6] KST 9시 +1슬롯 자동 확장 제거 — 사용자가 인지 못한 동작 (설정 2개인데 3개 진입)
            //   요구: 설정창 MaxPumpSlots 값을 그대로 따름. 동적 확장 원하면 별도 설정 필요.
            return MAX_PUMP_SLOTS;
        }
        
        /// <summary>
        /// [개선안 1] 현재 SLOT 여유도 및 심볼별 진입 가능 여부 판단
        /// </summary>
        private (bool canEnter, string reason) CanAcceptNewEntry(string symbol, string? signalSource = null)
        {
            lock (_posLock)
            {
                bool isMajorSymbol = MajorSymbols.Contains(symbol);
                // [v5.2.2] 슬롯 카운트: 이 유저가 직접 진입한 포지션(IsOwnPosition)만 세기
                int totalPositions = _activePositions.Count(p => p.Value.IsOwnPosition);
                int majorCount = _activePositions.Count(p => p.Value.IsOwnPosition && MajorSymbols.Contains(p.Key));
                int pumpCount = totalPositions - majorCount;

                // [v5.2.0] SPIKE 전용 슬롯 — TICK_SURGE/SPIKE 경로는 별도 1슬롯
                bool isSpikeEntry = !string.IsNullOrEmpty(signalSource) &&
                    (signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                     || signalSource.Equals("TICK_SURGE", StringComparison.OrdinalIgnoreCase));

                int maxTotal = GetDynamicMaxTotalSlots() + MAX_SPIKE_SLOTS; // SPIKE 슬롯 추가
                int maxPump = GetDynamicMaxPumpSlots();
                int maxMajor = MAX_MAJOR_SLOTS;

                // [1] 총 포화 체크
                if (totalPositions >= maxTotal)
                    return (false, $"total={totalPositions}/{maxTotal}");

                // [2] 메이저/PUMP/SPIKE 분리 체크
                if (isMajorSymbol && majorCount >= maxMajor)
                    return (false, $"major={majorCount}/{maxMajor}");

                if (!isMajorSymbol && !isSpikeEntry && pumpCount >= maxPump)
                    return (false, $"pump={pumpCount}/{maxPump}");

                // SPIKE 는 PUMP 슬롯 포화여도 추가 1슬롯 허용
                if (isSpikeEntry && pumpCount >= maxPump + MAX_SPIKE_SLOTS)
                    return (false, $"spike={pumpCount}/{maxPump + MAX_SPIKE_SLOTS}");

                return (true, "OK");
            }
        }
        
        /// <summary>
        /// [개선안 1] Scan 단계 스킵 판정: SLOT이 거의 찬 상태면 신호 생성 자체 스킵
        /// </summary>
        private bool ShouldSkipScanDueToSlotPressure()
        {
            lock (_posLock)
            {
                int totalPositions = _activePositions.Count;
                int maxTotal = GetDynamicMaxTotalSlots();
                // 동적 TOTAL의 80% 이상이면 스캔 스킵
                return totalPositions >= (int)(maxTotal * 0.83);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [EntryContext] 진입 파이프라인 데이터 전달 클래스
        // ═══════════════════════════════════════════════════════════════════════════
        private class EntryContext
        {
            // From router
            public string Symbol = string.Empty;
            public string Decision = string.Empty; // LONG or SHORT
            public decimal CurrentPrice;
            public CancellationToken Token;
            public string SignalSource = "UNKNOWN";
            public string Mode = "TREND";
            public decimal CustomTakeProfitPrice;
            public decimal CustomStopLossPrice;
            public PatternSnapshotInput? PatternSnapshot;
            public CandleData? LatestCandle;
            public float AiScore;
            public float AiProbability;
            public bool? AiPredictUp;
            public decimal ConvictionScore;
            public float BlendedMlTfScore;
            public bool ScoutModeActivated;
            public bool ScoutAddOnEligible;
            public bool IsScoutAddOnOrder;
            public string? AiGateDecisionId;
            public List<IBinanceKline>? RecentEntryKlines;
            public BandwidthGateResult BandwidthGate;

            // Set by typed entry method
            public decimal MarginUsdt;
            public int Leverage;
            public decimal StopLossPrice;
            public decimal TakeProfitPrice;
            public decimal SizeMultiplier = 1.0m; // single, non-cascading
            public bool IsPumpStrategy;
            public bool IsMajorAtrEnforced;
            public bool IsVolatilityRecovery;
            public decimal RecoveryExtremePrice;
            public string EntryZoneTag = string.Empty;
            public decimal EntryBbPosition;
            public bool IsHybridMidBandLongEntry;
            public (decimal StopLossPrice, decimal AtrDistance, decimal StructureStopPrice) MajorAtrPreview;

            // Logging
            public string FlowTag = string.Empty;
            public Action<string, string, string> EntryLog = (_, _, _) => { };
        }

        /// <summary>
        /// [v4.8.0] 하이브리드 LIMIT 진입 시도 — OptimalEntryPriceRegressor 예측 기반
        /// 1) KlineCache에서 실시간 피처 추출
        /// 2) _entryPriceRegressor.PredictPullbackPct 호출
        /// 3) 예측값 >= 0.2% → 예측 dip 가격 로깅 (후속 버전에서 LIMIT 주문 + 체결 대기)
        /// 4) 예측 불가 또는 미충족 → false 반환 (호출자가 시장가 fallback)
        /// </summary>
        private Task<bool> TryHybridLimitEntryAsync(string symbol, decimal currentPrice, decimal sizeMul, CancellationToken token)
        {
            // [AI 제거] EntryPriceRegressor / BreakoutClassifier 의존 통째 제거
            return Task.FromResult(false);
        }

        /// <summary>
        /// 자동 매매 실행 메인 메서드 — ROUTER
        /// 공통 검증 후 Major/Pump x Long/Short 4개 메서드로 디스패치
        /// </summary>
        private async Task ExecuteAutoOrder(
            string symbol,
            string decision,
            decimal currentPrice,
            CancellationToken token,
            string signalSource = "UNKNOWN",
            string mode = "TREND",
            decimal customTakeProfitPrice = 0m,
            decimal customStopLossPrice = 0m,
            PatternSnapshotInput? patternSnapshot = null,
            decimal manualSizeMultiplier = 1.0m,
            bool skipAiGateCheck = false)
        {
            string flowTag = $"src={signalSource} mode={mode} sym={symbol} side={decision}";

            // [v5.12.0] 급등 단일 슬롯 체크 — MAJOR_MEME (PumpScan 3 fast paths) 진입 차단
            //   SPIKE_FAST는 HandleSpikeDetectedAsync 초반에서 이미 체크됨
            if (IsSpikeCategorySignal(signalSource) && _activeSpikeSlot.Count >= MAX_SPIKE_CATEGORY_SLOTS)
            {
                var occupant = _activeSpikeSlot.FirstOrDefault();
                OnStatusLog?.Invoke($"⛔ [SPIKE_SLOT] {symbol} {signalSource} 거부: 급등 슬롯 {occupant.Key}({occupant.Value.source}) 점유중 — 1개 제한");
                return;
            }

            void EntryLog(string stage, string status, string detail)
            {
                string line = $"🧭 [ENTRY][{stage}][{status}] {flowTag} | {detail}";
                OnStatusLog?.Invoke(line);
                LoggerService.Info(line); // [v4.9.2] 진입 라우터 모든 단계 Serilog 파일 기록

                bool shouldCount = status.IndexOf("BLOCK", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0;

                if (shouldCount)
                {
                    RecordEntryBlockReason(stage);
                    OnBlockReasonUpdate?.Invoke($"[{stage}] {detail}");
                }
                else if (status.IndexOf("PASS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    OnBlockReasonUpdate?.Invoke("");
                }
            }

            // [v5.1.1] 손절 후 쿨다운 5분 — 같은 심볼 즉시 재진입 방지
            // PLAYUSDT 케이스: 손절 2초 후 즉시 재진입 → 연속 손실
            if (_stopLossCooldown.TryGetValue(symbol, out var cooldownExpiry) && DateTime.Now < cooldownExpiry)
            {
                OnStatusLog?.Invoke($"⛔ [COOLDOWN] {symbol} 손절 후 쿨다운 중 ({(cooldownExpiry - DateTime.Now).TotalSeconds:F0}초 남음) → 재진입 차단");
                return;
            }

            // [v4.9.4] 중복 시그널 debounce — 동일 (symbol, direction) 10초 내 재시도 차단
            string debounceKey = $"{symbol}|{decision}";
            if (_recentEntryAttempts.TryGetValue(debounceKey, out var lastTry)
                && (DateTime.Now - lastTry).TotalSeconds < 10)
            {
                return;
            }
            _recentEntryAttempts[debounceKey] = DateTime.Now;
            // 오래된 엔트리 정리 (1분 초과)
            if (_recentEntryAttempts.Count > 200)
            {
                var cutoff = DateTime.Now.AddMinutes(-1);
                foreach (var kv in _recentEntryAttempts)
                    if (kv.Value < cutoff) _recentEntryAttempts.TryRemove(kv.Key, out _);
            }

            // [v5.10.81] 단일 게이트 IsEntryAllowed — 모든 글로벌 차단 조건 통합
            if (!IsEntryAllowed(symbol, $"ROUTE:{signalSource}", out string routerBlockReason))
            {
                OnLiveLog?.Invoke($"⛔ [ENTRY_GATE] {symbol} 진입 차단 ({routerBlockReason})");
                return;
            }

            string decisionKr = decision == "LONG" ? "LONG" : "SHORT";
            OnLiveLog?.Invoke($"📤 [{symbol}] {decisionKr} 주문 요청 중 | 가격 ${currentPrice:F2} | 소스: {signalSource}");
            EntryLog("START", "INFO", $"price={currentPrice:F4} source={signalSource}");

            // [v5.22.13] ROUTER 0 (심볼별 데이터 준비 검증) 제거 — AI 시스템 폐기 (2026-04-29)
            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 1. 공통 검증
            // ═══════════════════════════════════════════════════════════════

            // 1-1. 진입 신호 체크
            // [v4.4.6] USDT 쌍만 허용 (USDC, BUSD 등 차단)
            if (!symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            {
                EntryLog("SIGNAL", "BLOCK", $"notUSDT pair={symbol}");
                return;
            }

            if (decision != "LONG" && decision != "SHORT")
            {
                EntryLog("SIGNAL", "BLOCK", "decision!=LONG/SHORT");
                return;
            }

            // [v3.7.7] 신호 가격 vs 현재가 검증 — 신호 발생 후 가격 1.5%+ 변동이면 무효
            if (_marketDataManager.TickerCache.TryGetValue(symbol, out var nowTicker) && nowTicker.LastPrice > 0 && currentPrice > 0)
            {
                decimal priceDrift = Math.Abs((nowTicker.LastPrice - currentPrice) / currentPrice * 100m);
                if (priceDrift >= 1.5m)
                {
                    OnStatusLog?.Invoke($"⛔ [신호만료] {symbol} {decision} | 신호가 ${currentPrice:F4} → 현재 ${nowTicker.LastPrice:F4} ({priceDrift:F1}% 변동) → 재신호 대기");
                    EntryLog("STALE", "BLOCK", $"signalPrice={currentPrice} nowPrice={nowTicker.LastPrice} drift={priceDrift:F1}% ≥1.5% → 신호 만료");
                    return;
                }
                currentPrice = nowTicker.LastPrice; // 최신 가격으로 갱신
            }

            // 1-2. 데이터 수집
            CandleData? latestCandle = await GetLatestCandleDataAsync(symbol, token);
            List<IBinanceKline>? recentEntryKlines =
                (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 140, token))?.ToList();

            // [v4.6.0] PUMP/SPIKE/MAJOR_MEME 모두 140봉 부족 시 30봉으로 retry (신규 상장 알트 대응)
            bool isPumpOrSpikeSource = signalSource == "SPIKE_DETECT"
                || signalSource == "PUMP_WATCH_CONFIRMED"
                || signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("MAJOR_MEME", StringComparison.OrdinalIgnoreCase)
                || signalSource == "TICK_SURGE";

            if ((recentEntryKlines == null || recentEntryKlines.Count < 20) && isPumpOrSpikeSource)
            {
                recentEntryKlines = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 30, token))?.ToList();
                EntryLog("DATA", "RETRY30", $"path={signalSource} count={recentEntryKlines?.Count ?? 0}");
            }

            var hsKlines = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 80, token))?.ToList();
            var hsResult = HeadAndShouldersDetector.DetectPattern(hsKlines ?? new List<IBinanceKline>(), 70);

            var bandwidthGate = EvaluateBandwidthGate(symbol, decision, signalSource, currentPrice, latestCandle, recentEntryKlines);
            if (bandwidthGate.Blocked)
            {
                OnStatusLog?.Invoke(
                    $"⚠️ [Bandwidth 참고] {symbol} {decision} | ratio={bandwidthGate.SqueezeRatio:P0}, " +
                    $"현재폭={bandwidthGate.CurrentBandwidthPct:F2}% 평균폭={bandwidthGate.AverageBandwidthPct:F2}% (AI 스코어 반영, 진입 계속)");
                EntryLog("BW_GATE", "INFO", bandwidthGate.Reason);
            }

            // 1-3. 블랙리스트 체크
            if (_blacklistedSymbols.TryGetValue(symbol, out var blacklistExpiry))
            {
                if (DateTime.Now < blacklistExpiry)
                {
                    OnStatusLog?.Invoke($"⛔ [BLACKLIST] {symbol} 블랙리스트 기간 중 ({(blacklistExpiry - DateTime.Now).TotalMinutes:F0}분 남음) → 진입 차단");
                    EntryLog("BLACKLIST", "BLOCK", $"expiresIn={(blacklistExpiry - DateTime.Now).TotalMinutes:F1}min");
                    return;
                }
                else
                {
                    _blacklistedSymbols.TryRemove(symbol, out _);
                    OnStatusLog?.Invoke($"✅ [BLACKLIST] {symbol} 블랙리스트 해제");
                }
            }

            // 1-4. 쿨다운 체크
            if (IsSymbolInSlotCooldown(symbol, out int slotCooldownRemaining))
            {
                OnStatusLog?.Invoke($"⏳ [SLOT_COOLDOWN] {symbol} 재시도 쿨다운 중 ({slotCooldownRemaining}초 남음) → 진입 차단");
                EntryLog("SLOT_COOLDOWN", "BLOCK", $"cooldownRemain={slotCooldownRemaining}s");
                return;
            }

            // 1-5. 워밍업 + 서킷브레이커
            if (IsEntryWarmupActive(out var remaining))
            {
                OnStatusLog?.Invoke(TradingStateLogger.RejectedByRiskManagement(symbol, decision, $"진입 워밍업 활성화 ({remaining.TotalSeconds:F0}초 남음)"));
                EntryLog("GUARD", "BLOCK", $"warmupRemainingSec={remaining.TotalSeconds:F0}");
                return;
            }

            // 승률 서킷브레이커: 시간 차단 제거 — 진입 품질로 제어

            // BTC 하락장 필터 제거 — 메이저도 BTC와 독립적으로 움직일 수 있음
            if (false && decision == "LONG")
            {
                if (_marketDataManager.TickerCache.TryGetValue("BTCUSDT", out var btcTick) && btcTick.LastPrice > 0
                    && _marketDataManager.KlineCache.TryGetValue("BTCUSDT", out var btcCandles) && btcCandles.Count >= 12)
                {
                    List<IBinanceKline> btcSnapshot;
                    lock (btcCandles) { btcSnapshot = btcCandles.TakeLast(12).ToList(); }
                    if (btcSnapshot.Count >= 12)
                    {
                        decimal btcPrice1hAgo = btcSnapshot[0].OpenPrice;
                        decimal btcNow = btcTick.LastPrice;
                        decimal btc1hChange = (btcNow - btcPrice1hAgo) / btcPrice1hAgo * 100m;

                        if (btc1hChange <= -2.0m)
                        {
                            OnStatusLog?.Invoke($"⛔ [BTC 하락장] {symbol} LONG 차단 | BTC 1시간 변동 {btc1hChange:+0.00;-0.00}% (≤-2%) | source={signalSource}");
                            EntryLog("MACRO", "BLOCK", $"btc1hChange={btc1hChange:F2}% bearMarket=true");
                            return;
                        }
                    }
                }
            }

            // 1-6. 패턴 홀드 참고 로그
            if (patternSnapshot?.Match?.ShouldDeferEntry == true)
            {
                string deferReason = string.IsNullOrWhiteSpace(patternSnapshot.Match.DeferReason)
                    ? "loss-pattern"
                    : patternSnapshot.Match.DeferReason;
                EntryLog("GUARD", "INFO", $"patternHold={deferReason} (not blocking)");
            }

            // 1-7. 데이터 유효성 — [v4.6.0] PUMP/SPIKE/MAJOR_MEME 모두 fallback 허용
            // 신규 상장 알트는 5분봉 130봉(11시간) 부족할 수 있음
            bool allowDataFallback = signalSource == "SPIKE_DETECT"
                || signalSource == "PUMP_WATCH_CONFIRMED"
                || signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("MAJOR_MEME", StringComparison.OrdinalIgnoreCase)
                || signalSource == "TICK_SURGE"
                || signalSource == "PUMP_REVERSE"
                || signalSource == "CRASH_REVERSE";

            if (latestCandle == null && allowDataFallback)
            {
                // 급등 감지는 TickerCache 가격만으로도 진입 가능하도록 최소 CandleData 생성
                if (recentEntryKlines != null && recentEntryKlines.Count >= 20)
                {
                    var last = recentEntryKlines.Last();
                    var bb = IndicatorCalculator.CalculateBB(recentEntryKlines, 20, 2);
                    double rsiVal = IndicatorCalculator.CalculateRSI(recentEntryKlines, 14);
                    double atrVal = IndicatorCalculator.CalculateATR(recentEntryKlines, 14);
                    var macdVal = IndicatorCalculator.CalculateMACD(recentEntryKlines);
                    double sma20Val = IndicatorCalculator.CalculateSMA(recentEntryKlines, 20);
                    double bbMid = (bb.Upper + bb.Lower) / 2.0;

                    latestCandle = new CandleData
                    {
                        Symbol = symbol,
                        Close = last.ClosePrice,
                        Open = last.OpenPrice,
                        High = last.HighPrice,
                        Low = last.LowPrice,
                        Volume = (float)last.Volume,
                        RSI = (float)rsiVal,
                        ATR = (float)atrVal,
                        MACD = (float)macdVal.Macd,
                        MACD_Signal = (float)macdVal.Signal,
                        MACD_Hist = (float)macdVal.Hist,
                        BollingerUpper = (float)bb.Upper,
                        BollingerLower = (float)bb.Lower,
                        SMA_20 = (float)sma20Val,
                        BB_Width = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0,
                        Volume_Ratio = 1.0f,
                    };
                    EntryLog("DATA", "SPIKE_FALLBACK", $"candleCount={recentEntryKlines.Count} (130봉 미달, 최소 데이터로 진행)");
                }
            }

            if (latestCandle == null)
            {
                OnStatusLog?.Invoke($"⛔ [데이터] {symbol} 캔들 데이터 없음 → 진입 차단 (DB 수집 대기)");
                EntryLog("DATA", "BLOCK", "latestCandle=missing");
                return;
            }

            // [v4.9.3] VOLUME 하드코딩 필터 완전 제거
            // 메모리 원칙: 모든 진입은 AI(ML.NET) 학습·추론·예측으로만.
            // volumeRatio<0.5 는 임의 임계값이고, 이 필터가 FooterLogs 진단에서 10분에
            // 1,027건 진입을 차단하던 주범이었음 (PumpScan이 AI_ENTRY 승인한 것도 차단).
            // Volume 판단은 이미 PumpSignalClassifier / AIDoubleCheckEntryGate /
            // SurvivalEntryModel 이 Volume_Ratio·volumeMomentum 피처로 학습 중.

            // [v4.6.0] 변동성 차단 — 메이저 일반 진입에만 적용 (PUMP/급등은 우회)
            // [v4.7.0] 초기학습 완료 시 모든 하드 필터 우회 (AI 단독 판단)
            bool isVolatilitySignalPath = signalSource == "PUMP_WATCH_CONFIRMED"
                || signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("MAJOR_MEME", StringComparison.OrdinalIgnoreCase)
                || signalSource == "TICK_SURGE"
                || signalSource == "PUMP_REVERSE"
                || signalSource == "CRASH_REVERSE";

            // [v5.22.13] 메이저 변동성 가드 — 항상 평가 (AI 폐기 후)
            if (!isVolatilitySignalPath && latestCandle != null && latestCandle.ATR > 0 && currentPrice > 0)
            {
                // 메이저 일반 진입만: ATR 3%+, 5분봉 5%+ 차단
                float atrPriceRatio = latestCandle.ATR / (float)currentPrice * 100f;
                if (atrPriceRatio >= 3.0f)
                {
                    OnStatusLog?.Invoke($"⛔ [변동성] {symbol} ATR/가격={atrPriceRatio:F1}% ≥ 3% → 차단");
                    EntryLog("VOLATILITY", "BLOCK", $"atrRatio={atrPriceRatio:F1}% (메이저 진입)");
                    return;
                }

                float candleRangePct = (float)(latestCandle.High - latestCandle.Low) / (float)currentPrice * 100f;
                if (candleRangePct >= 5.0f)
                {
                    OnStatusLog?.Invoke($"⛔ [변동성] {symbol} 5분봉={candleRangePct:F1}% ≥ 5% → 차단");
                    EntryLog("VOLATILITY", "BLOCK", $"candleRange={candleRangePct:F1}% (메이저 진입)");
                    return;
                }
            }
            else if (isVolatilitySignalPath)
            {
                EntryLog("VOLATILITY", "BYPASS", $"path={signalSource} (PUMP/급등은 변동성 자체가 신호)");
            }

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 2. 슬롯 검증 + 정찰대 전환
            // ═══════════════════════════════════════════════════════════════
            bool scoutModeActivated = false;
            bool scoutAddOnEligible = false;

            lock (_posLock)
            {
                // [v3.5.1] 중복 진입 완전 차단: 이미 포지션/예약이 있으면 즉시 차단
                // scout add-on은 MonitorScoutToMainUpgradeAsync에서 별도 처리
                // [v5.2.3] IsOwnPosition=false(다른 유저 포지션)는 중복 체크 제외
                if (_activePositions.TryGetValue(symbol, out var existingPos) && existingPos.IsOwnPosition)
                {
                    if (existingPos.Quantity == 0 && existingPos.EntryPrice == 0)
                    {
                        // Quantity=0, EntryPrice=0 → 다른 경로에서 진입 진행 중
                        OnStatusLog?.Invoke($"⛔ [SLOT] {symbol} 다른 경로에서 진입 진행 중 → 중복 차단");
                        EntryLog("SLOT", "BLOCK", "duplicateReservation=true");
                    }
                    else
                    {
                        // 실제 포지션 보유 중 → 중복 진입 차단
                        EntryLog("SLOT", "BLOCK", $"duplicatePosition qty={existingPos.Quantity}");
                    }
                    return;
                }

                bool isMajorSymbol = MajorSymbols.Contains(symbol);
                // [v5.2.2] 슬롯 카운트: IsOwnPosition만 (다른 유저 포지션 제외)
                int totalPositions = _activePositions.Count(p => p.Value.IsOwnPosition);
                int majorCount = _activePositions.Count(p => p.Value.IsOwnPosition && MajorSymbols.Contains(p.Key));
                int pumpCount = totalPositions - majorCount;

                int maxTotal = GetDynamicMaxTotalSlots();
                int maxPump = GetDynamicMaxPumpSlots();
                int maxMajor = MAX_MAJOR_SLOTS;

                // [v3.2.21] 슬롯 포화 시 차단 (정찰대 무제한 진입 방지)
                if (isMajorSymbol && majorCount >= maxMajor)
                {
                    OnStatusLog?.Invoke($"⛔ [SLOT] {symbol} 메이저 포화 ({majorCount}/{maxMajor}) → 진입 차단");
                    EntryLog("SLOT", "BLOCK", $"major={majorCount}/{maxMajor}");
                    return;
                }

                if (!isMajorSymbol && pumpCount >= maxPump)
                {
                    // [v5.2.9] 슬롯 차단 시 최초 신호 가격 기록 (이미 있으면 유지)
                    _signalOriginPrice.TryAdd(symbol, (currentPrice, DateTime.Now));
                    // [v5.10.38] 우선순위 큐 등록 — AI 승인 점수 캐시 사용 (없으면 0점 = 최하위)
                    // [v5.10.71 B] AI 승인 score 없으면 PumpScan top60 score 사용 (RAVE처럼 WAIT 상태 고거래량 코인 대응)
                    float queueScore = 0f;
                    if (_aiApprovedRecentScores.TryGetValue(symbol, out var cached)
                        && (DateTime.Now - cached.Time).TotalMinutes < 30)
                        queueScore = cached.Score;
                    // [AI 제거] _pumpStrategy 의존 코드 제거
                    lock (_pumpPriorityLock)
                    {
                        _pumpPriorityQueue.RemoveAll(e => e.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                        _pumpPriorityQueue.Add(new PumpPriorityCandidate(symbol, decision, queueScore, currentPrice, DateTime.Now, signalSource));
                        _pumpPriorityQueue.Sort((a, b) => b.BlendedScore.CompareTo(a.BlendedScore));
                    }
                    OnStatusLog?.Invoke($"⛔ [SLOT] {symbol} PUMP 포화 ({pumpCount}/{maxPump}) → 우선순위 큐 등록 (score={queueScore:P0})");
                    EntryLog("SLOT", "BLOCK", $"pump={pumpCount}/{maxPump} queued score={queueScore:P0}");
                    return;
                }

                if (totalPositions >= maxTotal)
                {
                    OnStatusLog?.Invoke($"⛔ [SLOT] {symbol} 총 포화 ({totalPositions}/{maxTotal}) → 진입 차단");
                    EntryLog("SLOT", "BLOCK", $"total={totalPositions}/{maxTotal}");
                    _signalOriginPrice.TryAdd(symbol, (currentPrice, DateTime.Now));
                    return;
                }
            }

            // 슬롯 교체 비활성화 (v4.0.8: 연쇄 손실 방지)

            // ═══════════════════════════════════════════════════════════════
            // [v5.2.9] 신호 원가 대비 고점 진입 차단 — 슬롯 차단 후 뒤늦은 진입 방지
            // 슬롯이 꽉 차서 못 들어간 신호가 나중에 슬롯 비면 들어가는데,
            // 이때 가격이 이미 올라서 고점 진입 → 즉시 손절. 이걸 차단.
            // ═══════════════════════════════════════════════════════════════
            // [v5.4.8] 메이저 포함 전체 적용 — DOGE 고점 진입(-52% 손실) 방지
            if (decision == "LONG"
                && signalSource != "CRASH_REVERSE" && signalSource != "PUMP_REVERSE"
                && signalSource != "MEGA_PUMP")
            {
                if (_signalOriginPrice.TryGetValue(symbol, out var origin))
                {
                    double minutesElapsed = (DateTime.Now - origin.Time).TotalMinutes;
                    decimal priceRise = origin.Price > 0 ? (currentPrice - origin.Price) / origin.Price * 100m : 0m;

                    // 30분 이상 지난 신호 → 무효화
                    if (minutesElapsed >= 30)
                    {
                        _signalOriginPrice.TryRemove(symbol, out _);
                        OnStatusLog?.Invoke($"⛔ [STALE_SIGNAL] {symbol} 신호 {minutesElapsed:F0}분 경과 → 무효화, 진입 차단");
                        EntryLog("STALE_SIGNAL", "BLOCK", $"elapsed={minutesElapsed:F0}min originPrice={origin.Price:F4}");
                        return;
                    }

                    // [v5.10.69] STALE_SIGNAL 임계값 시간대별 동적 — KST 9시±15분 = 5%, 평시 = 2%
                    DateTime kstNow = DateTime.UtcNow.AddHours(9);
                    bool isKst9PumpWindow = (kstNow.Hour == 8 && kstNow.Minute >= 45) || (kstNow.Hour == 9 && kstNow.Minute <= 15);
                    decimal stalePctThreshold = isKst9PumpWindow ? 5.0m : 2.0m;

                    if (priceRise >= stalePctThreshold)
                    {
                        _signalOriginPrice.TryRemove(symbol, out _);
                        OnStatusLog?.Invoke($"⛔ [STALE_SIGNAL] {symbol} 신호가 ${origin.Price:F4} → 현재 ${currentPrice:F4} (+{priceRise:F1}% ≥ {stalePctThreshold}%) → 고점 진입 차단");
                        EntryLog("STALE_SIGNAL", "BLOCK", $"originPrice={origin.Price:F4} nowPrice={currentPrice:F4} rise={priceRise:F1}% threshold={stalePctThreshold}");
                        return;
                    }

                    // 신호 유효 → 기록 삭제하고 진입 진행
                    _signalOriginPrice.TryRemove(symbol, out _);
                }
            }

            // [v5.10.74 롤백] v5.10.73에서 하드코딩 추가했던 HIGH_CHASE_BLOCK / LOSSY_SYMBOL_BLOCK 제거.
            // 사용자 메모리 원칙 위반 (AI 판단만 사용, 하드코딩 금지).
            // → 대신 Phase 2 (3 모델 분리)에서 feature로 학습하도록 재설계 필요.

            // ═══════════════════════════════════════════════════════════════
            // [v5.2.5] 늦은 진입 차단 — PUMP/SPIKE 코인이 이미 급등한 후 진입 방지
            // 최근 6봉(30분) 저점 대비 현재가 2% 이상 상승 + 하락 전환 시 차단
            // ═══════════════════════════════════════════════════════════════
            // [v5.4.8] 메이저 포함 전체 적용
            if (decision == "LONG"
                && signalSource != "CRASH_REVERSE" && signalSource != "PUMP_REVERSE"
                && signalSource != "MEGA_PUMP")
            {
                try
                {
                    if (_marketDataManager.KlineCache.TryGetValue(symbol, out var lateCheckCandles) && lateCheckCandles.Count >= 6)
                    {
                        List<IBinanceKline> recent6;
                        lock (lateCheckCandles) { recent6 = lateCheckCandles.TakeLast(6).ToList(); }

                        decimal recentLow = recent6.Min(k => k.LowPrice);
                        decimal riseFromLow = recentLow > 0 ? (currentPrice - recentLow) / recentLow * 100m : 0m;

                        // 최근 2봉이 음봉이면 하락 전환 중
                        bool lastTwoBearish = recent6.Count >= 2
                            && recent6[^1].ClosePrice < recent6[^1].OpenPrice
                            && recent6[^2].ClosePrice < recent6[^2].OpenPrice;

                        if (riseFromLow >= 2.0m && lastTwoBearish)
                        {
                            OnStatusLog?.Invoke($"⛔ [LATE_ENTRY] {symbol} 저점 대비 +{riseFromLow:F1}% 이미 상승 + 하락 전환 → 진입 차단");
                            EntryLog("LATE_ENTRY", "BLOCK", $"riseFromLow={riseFromLow:F1}% bearishCandles=true");
                            return;
                        }
                    }
                }
                catch { }
            }

            // ═══════════════════════════════════════════════════════════════
            // [v4.9.9] MTF GUARDIAN — 상위 시간봉 역방향 차단 (공통 관문)
            // 5분봉 명확한 하락 추세 중 LONG 진입 / 상승 추세 중 SHORT 진입 차단
            // BASUSDT 5분 12봉 연속 하락 중 1분 반짝 상승 진입 버그 해결
            // CRASH_REVERSE / PUMP_REVERSE 는 급변 대응이므로 제외
            // ═══════════════════════════════════════════════════════════════
            if (signalSource != "CRASH_REVERSE" && signalSource != "PUMP_REVERSE")
            {
                if (!PassMtfGuardian(symbol, decision, out string mtfReason))
                {
                    OnStatusLog?.Invoke($"⛔ [MTF_GUARDIAN] {symbol} {decision} 차단: {mtfReason}");
                    EntryLog("MTF_GUARDIAN", "BLOCK", mtfReason);
                    return;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // [v5.0.6] Gate 1 — 고점 진입 차단 (공통 관문)
            // 모든 LONG 진입 경로를 커버 (PUMP/SPIKE/TICK_SURGE/Router 전체)
            // LONG 만 대상 (SHORT 는 역방향이라 Gate 1 로직 반대가 되어야 함 — 현재 미구현)
            // CRASH_REVERSE / PUMP_REVERSE 는 급변 대응이므로 제외
            // 기존: PumpScanStrategy / SPIKE_FAST 각각 분산 적용 (일부 경로 누락)
            // 신규: Router 공통 관문 → 모든 경로 자동 커버
            // ═══════════════════════════════════════════════════════════════
            if (decision == "LONG"
                && signalSource != "CRASH_REVERSE"
                && signalSource != "PUMP_REVERSE"
                && _marketDataManager.KlineCache.TryGetValue(symbol, out var gate1Cache))
            {
                List<IBinanceKline> gate1Candles;
                lock (gate1Cache) { gate1Candles = gate1Cache.ToList(); }

                if (IsAlreadyPumpedRecently(gate1Candles, currentPrice, out string gate1Reason))
                {
                    OnStatusLog?.Invoke($"⛔ [GATE1] {symbol} 고점 진입 차단: {gate1Reason} → Gate2 지연 예약 (src={signalSource})");
                    EntryLog("GATE1", "BLOCK", $"{gate1Reason} src={signalSource}");
                    ScheduleGate2Reevaluation(symbol, decision, signalSource);
                    return;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // [AI 제거] AI Gate 평가 통째 제거 — 가드/슬롯/MTF/Gate1 만으로 진입
            // ═══════════════════════════════════════════════════════════════
            float capturedBlendedScore = 0f;
            string? aiGateDecisionId = null;
            decimal aiGateSizeMultiplier = 1.0m; // 사용 안함 (1.0 고정)

            // [AI 제거] Major D1+H4 방향성 필터 — 단순 동작
            if (MajorSymbols.Contains(symbol))
            {
                try
                {
                    float dirBias = 0;
                    if (_directionBiasCache.TryGetValue(symbol, out var dirCached) && (DateTime.Now - dirCached.time).TotalMinutes < 5)
                    {
                        dirBias = dirCached.bias;
                    }
                    else
                    {
                        var h4Klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour, 30, token);
                        var d1Klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneDay, 20, token);
                        if (h4Klines?.Count >= 26 && d1Klines?.Count >= 14)
                        {
                            var (h4Macd, h4Signal, _) = IndicatorCalculator.CalculateMACD(h4Klines);
                            var (d1Macd, d1Signal, _) = IndicatorCalculator.CalculateMACD(d1Klines);
                            float d1Dir = d1Macd > d1Signal ? 1f : (d1Macd < d1Signal ? -1f : 0f);
                            float h4Dir = h4Macd > h4Signal ? 1f : (h4Macd < h4Signal ? -1f : 0f);
                            dirBias = d1Dir + h4Dir;
                            _directionBiasCache[symbol] = (dirBias, DateTime.Now);
                        }
                    }
                    if (decision == "LONG" && dirBias <= -1.5f)
                    {
                        EntryLog("DIRECTION", "BLOCK", $"bias={dirBias:F1} longInDowntrend");
                        return;
                    }
                    if (decision == "SHORT" && dirBias >= 1.5f)
                    {
                        EntryLog("DIRECTION", "BLOCK", $"bias={dirBias:F1} shortInUptrend");
                        return;
                    }
                }
                catch { }
            }

            EntryLog("1M_HUB", "SKIP", "disabled — immediate entry");

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 4. 소진 반전 감지 (공통)
            // ═══════════════════════════════════════════════════════════════
            bool isExhaustionReversal = false;
            _pendingDelayedEntries.TryRemove(symbol, out _);

            if (IsHybridBbSignalSource(signalSource) && latestCandle != null)
            {
                var klines5m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 12, token);
                if (klines5m != null && klines5m.Count >= 9)
                {
                    var kList = klines5m.ToList();
                    double rsiRev = IndicatorCalculator.CalculateRSI(kList, 14);

                    int consecBearish = 0, consecBullish = 0;
                    for (int i = kList.Count - 1; i >= Math.Max(0, kList.Count - 6); i--)
                    {
                        if (kList[i].ClosePrice < kList[i].OpenPrice) consecBearish++;
                        else break;
                    }
                    for (int i = kList.Count - 1; i >= Math.Max(0, kList.Count - 6); i--)
                    {
                        if (kList[i].ClosePrice > kList[i].OpenPrice) consecBullish++;
                        else break;
                    }

                    var priorCandles = kList.Take(kList.Count - 1).ToList();
                    decimal recentHigh = priorCandles.Count > 0 ? priorCandles.Max(k => k.HighPrice) : currentPrice;
                    decimal recentLow  = priorCandles.Count > 0 ? priorCandles.Min(k => k.LowPrice)  : currentPrice;
                    decimal dropPct    = recentHigh > 0 ? (recentHigh - currentPrice) / recentHigh * 100 : 0;
                    decimal risePct    = recentLow  > 0 ? (currentPrice - recentLow)  / recentLow  * 100 : 0;

                    const decimal REVERSAL_MIN_MOVE_PCT    = 2.5m;
                    const int     REVERSAL_MIN_CONSEC      = 5;
                    const double  REVERSAL_RSI_OVERSOLD    = 30.0;
                    const double  REVERSAL_RSI_OVERBOUGHT  = 70.0;

                    if (decision == "SHORT"
                        && rsiRev <= REVERSAL_RSI_OVERSOLD
                        && consecBearish >= REVERSAL_MIN_CONSEC
                        && dropPct >= REVERSAL_MIN_MOVE_PCT)
                    {
                        decision = "LONG";
                        isExhaustionReversal = true;
                        OnStatusLog?.Invoke(
                            $"🔄 [소진 반전] {symbol} SHORT→LONG | RSI={rsiRev:F1}(과매도) " +
                            $"{consecBearish}연속음봉 낙폭={dropPct:F2}% → 롱 리버설 직진입");
                        EntryLog("EXHAUSTION_REVERSAL", "LONG",
                            $"rsi={rsiRev:F1} consec={consecBearish} drop={dropPct:F2}%");
                    }
                    else if (decision == "LONG"
                        && rsiRev >= REVERSAL_RSI_OVERBOUGHT
                        && consecBullish >= REVERSAL_MIN_CONSEC
                        && risePct >= REVERSAL_MIN_MOVE_PCT)
                    {
                        decision = "SHORT";
                        isExhaustionReversal = true;
                        OnStatusLog?.Invoke(
                            $"🔄 [소진 반전] {symbol} LONG→SHORT | RSI={rsiRev:F1}(과매수) " +
                            $"{consecBullish}연속양봉 상승폭={risePct:F2}% → 숏 리버설 직진입");
                        EntryLog("EXHAUSTION_REVERSAL", "SHORT",
                            $"rsi={rsiRev:F1} consec={consecBullish} rise={risePct:F2}%");
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 5. 15분봉 추세 참고 (공통, 차단 없음)
            // ═══════════════════════════════════════════════════════════════
            if (_enableFifteenMinWaveGate && IsHybridBbSignalSource(signalSource) && !isExhaustionReversal)
            {
                try
                {
                    var klines15m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 80, token);
                    if (klines15m != null && klines15m.Count >= 60)
                    {
                        var list15m = klines15m.ToList();
                        double sma20_15m = IndicatorCalculator.CalculateSMA(list15m, 20);
                        double sma60_15m = IndicatorCalculator.CalculateSMA(list15m, 60);

                        if (sma20_15m > 0 && sma60_15m > 0)
                        {
                            bool upTrend15m = sma20_15m > sma60_15m;
                            bool downTrend15m = sma20_15m < sma60_15m;

                            if (decision == "LONG" && !upTrend15m)
                            {
                                string reason = $"15m SMA20({sma20_15m:F2}) < SMA60({sma60_15m:F2}) → 역추세 (AI 스코어 반영, 진입 계속)";
                                OnStatusLog?.Invoke($"⚠️ [15분봉 참고] {symbol} {decision} | {reason}");
                                EntryLog("M15_TREND", "INFO", reason);
                            }
                            else if (decision == "SHORT" && !downTrend15m)
                            {
                                string reason = $"15m SMA20({sma20_15m:F2}) > SMA60({sma60_15m:F2}) → 역추세 (AI 스코어 반영, 진입 계속)";
                                OnStatusLog?.Invoke($"⚠️ [15분봉 참고] {symbol} {decision} | {reason}");
                                EntryLog("M15_TREND", "INFO", reason);
                            }
                            else
                            {
                                OnStatusLog?.Invoke($"✅ [15분봉 추세 확인] {symbol} {decision} | 15m SMA20={sma20_15m:F2}, SMA60={sma60_15m:F2} → 추세 동조 확인");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 15분봉 추세 필터 조회 실패: {ex.Message}");
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 6. 생존 모델 + 방향 AI (RSI 하드코딩 제거 — v4.9.3)
            // ═══════════════════════════════════════════════════════════════
            {
                float rsiCheck = latestCandle.RSI;
                bool isSpikeEntry = signalSource == "SPIKE_DETECT";
                if (rsiCheck > 0 && !isSpikeEntry)
                {
                    // [v4.9.3] RSI≥88 / ≤12 극단 차단 하드코딩 제거
                    // SurvivalEntryModel과 PriceDirectionPredictor가 RSI를 피처로 학습 중이므로
                    // AI가 자체 판단하도록 위임 (메모리 원칙: 하드코딩 조건 금지)

                    // [AI 제거] SurvivalEntryModel + PriceDirectionPredictor 검증 통째 제거

                    var threshold = GetThresholdProfile(symbol, signalSource);
                    bool priceAboveMa20 = latestCandle.Close >= (decimal)latestCandle.SMA_20;
                    bool rsiWarn = (decision == "SHORT" && rsiCheck <= _shortRsiExhaustionFloor && priceAboveMa20)
                                || (decision == "LONG"  && rsiCheck >= threshold.MaxRsiLimit);
                    if (rsiWarn)
                    {
                        string limitText = decision == "SHORT"
                            ? $"shortFloor={_shortRsiExhaustionFloor:F1}"
                            : $"longLimit={threshold.MaxRsiLimit:F0}";
                        OnStatusLog?.Invoke($"⚠️ [RSI 참고] {symbol} {decision} | RSI={rsiCheck:F1} ({limitText}) → AI 스코어 반영, 진입 계속");
                        EntryLog("RSI_EXHAUSTED", "INFO", $"dir={decision} rsi={rsiCheck:F1} limit={limitText}");
                    }
                }
            }

            // [AI 제거] 3분류 ML 신호 사이즈 조절 제거
            decimal mlSignalSizeMultiplier = 1.0m;

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 8. 참고 정보 수집 (차단 없음)
            // ═══════════════════════════════════════════════════════════════

            // 드라이스펠 BB 참고
            if (decision == "LONG" && IsDroughtRecoveryPumpSignalSource(signalSource))
            {
                decimal bbUpper = latestCandle.BollingerUpper > 0 ? (decimal)latestCandle.BollingerUpper : 0m;
                decimal bbLower = latestCandle.BollingerLower > 0 ? (decimal)latestCandle.BollingerLower : 0m;
                decimal bbRange = bbUpper - bbLower;
                decimal bbPosition = (bbRange > 0m && bbUpper > 0m) ? (currentPrice - bbLower) / bbRange : 0m;

                if (bbUpper > 0m && currentPrice >= bbUpper)
                {
                    OnStatusLog?.Invoke($"⚠️ [드라이스펠 참고] {symbol} LONG | BB 상단 돌파 구간 (1M 허브에서 타점 정밀화)");
                    EntryLog("DROUGHT_CHASE", "INFO", $"bbUpperBreakout bbPos={bbPosition:P0}");
                }
                else if (bbPosition >= 0.90m && latestCandle.RSI >= 60f)
                {
                    OnStatusLog?.Invoke($"⚠️ [드라이스펠 참고] {symbol} LONG | %B={bbPosition:P0} RSI={latestCandle.RSI:F1} 과열 (1M 허브 대기)");
                    EntryLog("DROUGHT_CHASE", "INFO", $"bbUpperHeat bbPos={bbPosition:P2} rsi={latestCandle.RSI:F1}");
                }
            }

            // 1분봉 윗꼬리 참고
            var m1UpperWickFilter = await EvaluateOneMinuteUpperWickBlockAsync(symbol, decision, currentPrice, signalSource, latestCandle, token);
            if (m1UpperWickFilter.blocked)
            {
                OnStatusLog?.Invoke($"⚠️ [1M 윗꼬리 감지] {symbol} {decision} | {m1UpperWickFilter.reason} → 1M 실행허브에서 V-Turn/돌파 대기");
                EntryLog("M1_WICK", "INFO", m1UpperWickFilter.reason);
            }

            // ATR 변동성 참고 — 사이즈 조절용 멀티플라이어 산출
            decimal atrSizeMultiplier = 1.0m;
            var atrSource = recentEntryKlines;
            if (atrSource == null || atrSource.Count < 30)
            {
                atrSource = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 40, token))?.ToList();
                if (atrSource != null)
                    recentEntryKlines = atrSource;
            }

            if (atrSource != null && atrSource.Count >= 30)
            {
                var atrWindow = atrSource.TakeLast(Math.Min(40, atrSource.Count)).ToList();
                double currentAtr = IndicatorCalculator.CalculateATR(atrWindow.TakeLast(20).ToList(), 14);
                double averageAtr = IndicatorCalculator.CalculateATR(atrWindow.Take(20).ToList(), 14);

                if (averageAtr > 0)
                {
                    double atrRatio = currentAtr / averageAtr;
                    if (atrRatio > (double)_atrVolatilityBlockRatio)
                    {
                        atrSizeMultiplier = 0.20m;
                        EntryLog("ATR_VOL", "ADVISOR", $"atrRatio={atrRatio:F2}x>{_atrVolatilityBlockRatio:F2}x size={atrSizeMultiplier:P0}");
                        OnStatusLog?.Invoke($"⚡ [ATR Advisor] {symbol} 변동성 폭발 (ATR={atrRatio:F2}x) → 사이즈 {atrSizeMultiplier:P0}로 축소");
                    }
                    else if (atrRatio > 2.0)
                    {
                        atrSizeMultiplier = 0.50m;
                        EntryLog("ATR_VOL", "ADVISOR", $"atrRatio={atrRatio:F2}x>2.0 size={atrSizeMultiplier:P0}");
                    }
                    else if (atrRatio > 1.5)
                    {
                        OnStatusLog?.Invoke($"⚡ {symbol} 변동성 상승 주의 | ATR비율={atrRatio:F2}x");
                    }
                }
            }

            // BB 하이브리드 참고
            decimal entryBbPosition = 0m;
            string entryZoneTag = string.Empty;
            bool isHybridMidBandLongEntry = false;

            if (latestCandle != null &&
                ShouldBlockHybridBbEntry(symbol, decision, currentPrice, latestCandle, signalSource, out string hybridEntryReason, out entryBbPosition, out entryZoneTag, out isHybridMidBandLongEntry))
            {
                OnStatusLog?.Invoke($"⚠️ [BB 참고] {symbol} {decision} | {hybridEntryReason} → 1M 허브 타점 대기 (진입 계속)");
                EntryLog("HYBRID_BB", "INFO", hybridEntryReason);
            }

            if (!string.IsNullOrWhiteSpace(entryZoneTag))
            {
                OnStatusLog?.Invoke($"🧭 {symbol} {decision} 하이브리드 진입 승인 | Zone={entryZoneTag}, %B={entryBbPosition:P0}, src={signalSource}");
            }

            // 추격 필터 참고
            if (latestCandle != null &&
                ShouldBlockChasingEntry(symbol, decision, currentPrice, latestCandle, recentEntryKlines, mode, out string chaseReason))
            {
                OnStatusLog?.Invoke($"⚠️ [추격 참고] {symbol} {decision} | {chaseReason} → 1M 허브 V-Turn 대기");
                EntryLog("CHASE", "INFO", chaseReason);
            }

            // RL 상태 구성 (비활성화)
            if (latestCandle != null)
            {
                float[] state = new float[] { latestCandle.RSI / 100f, latestCandle.MACD, (latestCandle.BollingerUpper - latestCandle.BollingerLower) };
            }

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 9. EntryContext 구성 + 디스패치
            // ═══════════════════════════════════════════════════════════════
            var ctx = new EntryContext
            {
                Symbol = symbol,
                Decision = decision,
                CurrentPrice = currentPrice,
                Token = token,
                SignalSource = signalSource,
                Mode = mode,
                CustomTakeProfitPrice = customTakeProfitPrice,
                CustomStopLossPrice = customStopLossPrice,
                PatternSnapshot = patternSnapshot,
                LatestCandle = latestCandle,
                AiScore = 0,
                AiProbability = 0,
                AiPredictUp = null,
                ConvictionScore = 0m,
                BlendedMlTfScore = capturedBlendedScore,
                ScoutModeActivated = scoutModeActivated,
                ScoutAddOnEligible = scoutAddOnEligible,
                IsScoutAddOnOrder = false,
                AiGateDecisionId = aiGateDecisionId,
                RecentEntryKlines = recentEntryKlines,
                BandwidthGate = bandwidthGate,
                EntryZoneTag = entryZoneTag,
                EntryBbPosition = entryBbPosition,
                IsHybridMidBandLongEntry = isHybridMidBandLongEntry,
                FlowTag = flowTag,
                EntryLog = EntryLog,
            };

            // ═══════════════════════════════════════════════════════════════
            // [v5.4.3] EntryZoneRegressor — 참고 로그만 (API TP/SL에 미적용)
            // 예측값이 너무 보수적(ROE 7~9%)이라 급등 코인 수익 극대화 방해
            // API 등록은 고정 ROE(PUMP 25%, MAJOR 40%)만 사용
            // ═══════════════════════════════════════════════════════════════
            // [AI 제거] EntryZoneRegressor 참고 로그 제거

            // ═══════════════════════════════════════════════════════════════
            // [ROUTER] 사이즈 결정: 정찰대 vs 메인 명확히 분리
            // ═══════════════════════════════════════════════════════════════
            decimal finalSizeMultiplier;

            if (scoutModeActivated)
            {
                // [정찰대] 슬롯 포화 → 25% 고정
                finalSizeMultiplier = 0.25m;
                EntryLog("SIZE", "SCOUT", $"slotFull=true → 25% 정찰대");
            }
            else
            {
                // [v3.2.48] 승인 코인 확신도 기반 사이즈 결정
                // blended 80%+ → 본진입 100%
                // blended 70~80% → 정찰대 25% (ROE 확인 후 본대 추가)
                if (capturedBlendedScore >= 0.80f)
                {
                    finalSizeMultiplier = 1.0m;
                    EntryLog("SIZE", "FULL", $"blended={capturedBlendedScore:P0}≥80% → 100% 본진입");
                }
                else if (capturedBlendedScore >= 0.70f)
                {
                    finalSizeMultiplier = 0.25m;
                    EntryLog("SIZE", "SCOUT", $"blended={capturedBlendedScore:P0} 70~80% → 25% 정찰대");
                }
                else
                {
                    finalSizeMultiplier = 1.0m; // AI Gate 승인했으면 기본 100%
                    EntryLog("SIZE", "DEFAULT", $"blended={capturedBlendedScore:P0} → 100%");
                }

                // 3분류 모델: 반대 방향이면 축소
                if (mlSignalSizeMultiplier < 1.0m && mlSignalSizeMultiplier < finalSizeMultiplier)
                    finalSizeMultiplier = mlSignalSizeMultiplier;

                // 외부 전달 manualSizeMultiplier (CRASH_REVERSE 등)
                if (manualSizeMultiplier < 1.0m && manualSizeMultiplier < finalSizeMultiplier)
                    finalSizeMultiplier = manualSizeMultiplier;

                EntryLog("SIZE", "FINAL", $"blended={capturedBlendedScore:P0} mlSignal={mlSignalSizeMultiplier:P0} manual={manualSizeMultiplier:P0} → {finalSizeMultiplier:P0}");
            }

            // [v5.4.5] US 세션 축소 제거
            ctx.SizeMultiplier = Math.Clamp(finalSizeMultiplier, 0.10m, 2.00m);

            // ═══════════════════════════════════════════════════════════════
            // [DISPATCH] Major/Pump x Long/Short
            // ═══════════════════════════════════════════════════════════════
            bool isMajor = MajorSymbols.Contains(symbol);
            if (isMajor && decision == "LONG")
                await ExecuteMajorLongEntry(ctx);
            else if (isMajor && decision == "SHORT")
                await ExecuteMajorShortEntry(ctx);
            else if (!isMajor && decision == "LONG")
                await ExecutePumpLongEntry(ctx);
            else
            {
                // [v3.0.9] PUMP SHORT 제거 — 급등 코인 숏은 리스크만 큼
                ctx.EntryLog("PUMP_SHORT", "BLOCK", "PUMP 코인은 LONG만 허용");
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [Major LONG] 메이저 롱 진입
        // ═══════════════════════════════════════════════════════════════════════════
        private async Task ExecuteMajorLongEntry(EntryContext ctx)
        {
            var EntryLog = ctx.EntryLog;

            // [v5.22.13] Major LONG 필터 — 항상 평가 (AI 폐기 후)
            if (ctx.LatestCandle != null)
            {
                // 1. VWAP 아래 → LONG 차단 (가격이 VWAP 아래면 매도 우위)
                if (ctx.LatestCandle.VWAP > 0 && ctx.LatestCandle.Price_VWAP_Distance_Pct < -0.3f)
                {
                    OnStatusLog?.Invoke($"⛔ [Major필터] {ctx.Symbol} LONG 차단 | VWAP 아래 ({ctx.LatestCandle.Price_VWAP_Distance_Pct:F2}%)");
                    EntryLog("LONG_FILTER", "BLOCK", $"belowVWAP dist={ctx.LatestCandle.Price_VWAP_Distance_Pct:F2}% (VWAP -0.3%↓에서 롱 금지)");
                    return;
                }

                // 2. EMA 역배열(9<21<50) → LONG 차단 (강한 SHORT 추세)
                if (ctx.LatestCandle.EMA_Cross_State <= -1f)
                {
                    OnStatusLog?.Invoke($"⛔ [Major필터] {ctx.Symbol} LONG 차단 | EMA 역배열 하락추세");
                    EntryLog("LONG_FILTER", "BLOCK", "emaDowntrend EMA9<21<50 (강한 하락추세 롱 금지)");
                    return;
                }

                // 3. StochRSI 데드크로스(K<D) + K>20 → LONG 차단 (단기 약세)
                if (ctx.LatestCandle.StochRSI_Cross <= -1f && ctx.LatestCandle.StochRSI_K > 20f && ctx.LatestCandle.StochRSI_K > 0f)
                {
                    OnStatusLog?.Invoke($"⛔ [Major필터] {ctx.Symbol} LONG 차단 | StochRSI 데드크로스 K={ctx.LatestCandle.StochRSI_K:F1}");
                    EntryLog("LONG_FILTER", "BLOCK", $"stochRsiBearish K={ctx.LatestCandle.StochRSI_K:F1}<D={ctx.LatestCandle.StochRSI_D:F1} (단기 약세 롱 금지)");
                    return;
                }
            }

            // [AI 제거] EvaluateAiPredictorForEntry 호출 제거

            // 2. AI Score 사이즈 조절 제거 — 라우터 AI Gate Advisor에서 이미 적용됨

            // 3. Major ATR 하이브리드 손절
            bool isMajorLikeSignal = ctx.SignalSource == "MAJOR" || ctx.SignalSource.StartsWith("MAJOR_");
            ctx.IsMajorAtrEnforced = (isMajorLikeSignal || ctx.ScoutModeActivated) && MajorSymbols.Contains(ctx.Symbol);

            if (ctx.IsMajorAtrEnforced && ctx.CustomStopLossPrice <= 0)
            {
                ctx.MajorAtrPreview = await TryCalculateMajorAtrHybridStopLossAsync(ctx.Symbol, ctx.CurrentPrice, true, ctx.Token);
                if (ctx.MajorAtrPreview.StopLossPrice <= 0)
                {
                    string stopTag = ctx.ScoutModeActivated ? "SCOUT_ATR" : "MAJOR_ATR";
                    OnStatusLog?.Invoke($"⛔ [{stopTag}] {ctx.Symbol} ATR 2.0 하이브리드 손절 계산 실패로 진입 차단");
                    EntryLog(stopTag, "BLOCK", "stopCalcFailed");
                    return;
                }
                ctx.CustomStopLossPrice = ctx.MajorAtrPreview.StopLossPrice;
                OnStatusLog?.Invoke(
                    $"🛡️ [MAJOR ATR] {ctx.Symbol} 하이브리드 손절 적용 | Entry={ctx.CurrentPrice:F8}, SL={ctx.CustomStopLossPrice:F8}, ATRdist={ctx.MajorAtrPreview.AtrDistance:F8}, 구조선={ctx.MajorAtrPreview.StructureStopPrice:F8}");
            }

            // 4. M15 기울기 참고 (차단 없음)
            {
                var (isPositiveSlope, slope) = await EvaluateFifteenMinuteSlopeAsync(ctx.Symbol, ctx.Token);
                if (!isPositiveSlope)
                {
                    OnStatusLog?.Invoke($"⚠️ [M15 기울기 참고] {ctx.Symbol} LONG | slope={slope:F4} <= 0 (차단 없음, AI 스코어 보정용)");
                    EntryLog("M15_SLOPE", "INFO", $"slope={slope:F4} negativeButNotBlocking");
                }
                EntryLog("MAJOR_SNIPER", "PASS", $"score={(ctx.AiScore > 0 ? ctx.AiScore : ctx.ConvictionScore):F1} m15Slope={slope:F4}");
            }

            // 5. [v3.3.6] 급변동 회복 구간 체크 — 마진 축소 + 넓은 손절 적용
            if (TryGetVolatilityRecoveryInfo(ctx.Symbol, out var recoveryExtreme, out var recoveryIsUp))
            {
                ctx.IsVolatilityRecovery = true;
                ctx.RecoveryExtremePrice = recoveryExtreme;
                // CRASH 후 LONG 진입: crash low가 구조적 손절선
                // PUMP 후 LONG 진입: pump high 위로 진입 중이므로 기본 손절 유지
                if (!recoveryIsUp && recoveryExtreme > 0 && recoveryExtreme < ctx.CurrentPrice)
                {
                    // crash low를 구조적 손절선으로 사용 (기존 ATR 손절보다 넓을 수 있음)
                    decimal recoveryStopBuffer = recoveryExtreme * 0.997m; // -0.3% 버퍼
                    if (ctx.CustomStopLossPrice <= 0 || recoveryStopBuffer < ctx.CustomStopLossPrice)
                        ctx.CustomStopLossPrice = recoveryStopBuffer;
                }
                EntryLog("RECOVERY", "ACTIVE", $"extreme={recoveryExtreme:F4} dir={(recoveryIsUp ? "UP" : "DOWN")} marginReduction=60%");
                OnStatusLog?.Invoke($"🌊 [회복구간] {ctx.Symbol} LONG 진입 | 급변동 후 회복 모드 → 마진 60%, 넓은 손절");
            }

            // 6. 포지션 사이즈: adaptive equity
            ctx.Leverage = _settings.MajorLeverage > 0 ? _settings.MajorLeverage : _settings.DefaultLeverage;
            ctx.MarginUsdt = await GetAdaptiveEntryMarginUsdtAsync(ctx.Token);
            ctx.IsPumpStrategy = false;

            // [v3.3.6] 회복 모드 마진 축소 (60%): 넓은 손절 대비 리스크 일정 유지
            if (ctx.IsVolatilityRecovery)
                ctx.MarginUsdt = Math.Round(ctx.MarginUsdt * 0.6m, 2);

            decimal majorMarginPercent = GetConfiguredMajorMarginPercent();
            EntryLog("SIZE", "BASE", $"margin={ctx.MarginUsdt:F2} leverage={ctx.Leverage}x sizingRule=equity*{majorMarginPercent:F1}%{(ctx.IsVolatilityRecovery ? " [RECOVERY 60%]" : "")}");

            // 7. R:R 체크
            if (!EvaluateRiskRewardForEntry(ctx))
                return;

            // 8. 주문 실행
            await PlaceAndTrackEntryAsync(ctx);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [Major SHORT] 메이저 숏 진입
        // ═══════════════════════════════════════════════════════════════════════════
        private async Task ExecuteMajorShortEntry(EntryContext ctx)
        {
            var EntryLog = ctx.EntryLog;

            // 1. AI Predictor (보너스 없음 for SHORT)
            await EvaluateAiPredictorForEntry(ctx, applyMajorBonuses: false);

            // 2. SHORT 전용 다중 필터 (v3.5.3: 0승10패 → 엄격 필터)
            // [v5.22.13] SHORT 필터 — 항상 평가 (AI 폐기 후)
            if (ctx.LatestCandle != null)
            {
                // 2-1. RSI 과매도 + 가격 MA20 위 → 차단
                bool shortPriceAboveMa20 = ctx.LatestCandle.Close >= (decimal)ctx.LatestCandle.SMA_20;
                if (ctx.LatestCandle.RSI <= _shortRsiExhaustionFloor && shortPriceAboveMa20)
                {
                    EntryLog("AI", "BLOCK", $"shortFilter reason=RSI 과매도({ctx.LatestCandle.RSI:F1}≤{_shortRsiExhaustionFloor:F1}) + 가격 MA20 위");
                    return;
                }

                // 2-2. MACD 골든크로스 활성 → SHORT 차단 (MACD > Signal이면 상승 모멘텀)
                if (ctx.LatestCandle.MACD > ctx.LatestCandle.MACD_Signal && ctx.LatestCandle.MACD > 0)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"goldenCross MACD={ctx.LatestCandle.MACD:F6}>Signal={ctx.LatestCandle.MACD_Signal:F6} (상승 모멘텀 중 숏 금지)");
                    return;
                }

                // 2-3. 피보나치 38.2~61.8% 지지구간 → SHORT 차단 (반등 가능성 높음)
                if (ctx.LatestCandle.Fib_Position > 0 && ctx.LatestCandle.Fib_Position >= 0.35f && ctx.LatestCandle.Fib_Position <= 0.65f)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"fibZone position={ctx.LatestCandle.Fib_Position:F2} (38~65% 지지구간 숏 금지)");
                    return;
                }

                // 2-4. Stochastic K > D (상승 교차) → SHORT 차단
                if (ctx.LatestCandle.Stoch_K > 0 && ctx.LatestCandle.Stoch_K > ctx.LatestCandle.Stoch_D && ctx.LatestCandle.Stoch_K < 80)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"stochBullish K={ctx.LatestCandle.Stoch_K:F1}>D={ctx.LatestCandle.Stoch_D:F1} (상승 교차 숏 금지)");
                    return;
                }

                // 2-5. 가격 > SMA60 (중기 상승추세) → SHORT 차단
                if (ctx.LatestCandle.SMA_60 > 0 && ctx.LatestCandle.Close > (decimal)ctx.LatestCandle.SMA_60)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"aboveSMA60 price={ctx.LatestCandle.Close}>SMA60={ctx.LatestCandle.SMA_60:F4} (중기 상승추세 숏 금지)");
                    return;
                }

                // [v4.6.2] 2-6. VWAP 위 → SHORT 차단 (단타에서 가격이 VWAP 위면 매수 우위)
                if (ctx.LatestCandle.VWAP > 0 && ctx.LatestCandle.Price_VWAP_Distance_Pct > 0)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"aboveVWAP dist={ctx.LatestCandle.Price_VWAP_Distance_Pct:F2}% (VWAP 위에서 숏 금지)");
                    return;
                }

                // [v4.6.2] 2-7. EMA 정배열(9>21>50) → SHORT 차단 (강한 LONG 추세)
                if (ctx.LatestCandle.EMA_Cross_State >= 1f)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"emaUptrend EMA9>21>50 (강한 상승추세 숏 금지)");
                    return;
                }

                // [v4.6.2] 2-8. StochRSI 골든크로스(K>D) + K<80 → SHORT 차단 (단기 반등)
                if (ctx.LatestCandle.StochRSI_Cross >= 1f && ctx.LatestCandle.StochRSI_K < 80f && ctx.LatestCandle.StochRSI_K > 0f)
                {
                    EntryLog("SHORT_FILTER", "BLOCK", $"stochRsiBullish K={ctx.LatestCandle.StochRSI_K:F1}>D={ctx.LatestCandle.StochRSI_D:F1} (단기 반등 숏 금지)");
                    return;
                }
            }

            // 3. AI Score 사이즈 조절 제거 — 라우터 AI Gate Advisor에서 이미 적용됨

            // 4. Major ATR 하이브리드 손절
            bool isMajorLikeSignal = ctx.SignalSource == "MAJOR" || ctx.SignalSource.StartsWith("MAJOR_");
            ctx.IsMajorAtrEnforced = (isMajorLikeSignal || ctx.ScoutModeActivated) && MajorSymbols.Contains(ctx.Symbol);

            if (ctx.IsMajorAtrEnforced && ctx.CustomStopLossPrice <= 0)
            {
                ctx.MajorAtrPreview = await TryCalculateMajorAtrHybridStopLossAsync(ctx.Symbol, ctx.CurrentPrice, false, ctx.Token);
                if (ctx.MajorAtrPreview.StopLossPrice <= 0)
                {
                    string stopTag = ctx.ScoutModeActivated ? "SCOUT_ATR" : "MAJOR_ATR";
                    OnStatusLog?.Invoke($"⛔ [{stopTag}] {ctx.Symbol} ATR 2.0 하이브리드 손절 계산 실패로 진입 차단");
                    EntryLog(stopTag, "BLOCK", "stopCalcFailed");
                    return;
                }
                ctx.CustomStopLossPrice = ctx.MajorAtrPreview.StopLossPrice;
                OnStatusLog?.Invoke(
                    $"🛡️ [MAJOR ATR] {ctx.Symbol} 하이브리드 손절 적용 | Entry={ctx.CurrentPrice:F8}, SL={ctx.CustomStopLossPrice:F8}, ATRdist={ctx.MajorAtrPreview.AtrDistance:F8}, 구조선={ctx.MajorAtrPreview.StructureStopPrice:F8}");
            }

            // 5. [v3.3.6] 급변동 회복 구간 체크
            if (TryGetVolatilityRecoveryInfo(ctx.Symbol, out var recoveryExtremeS, out var recoveryIsUpS))
            {
                ctx.IsVolatilityRecovery = true;
                ctx.RecoveryExtremePrice = recoveryExtremeS;
                // PUMP 후 SHORT 진입: pump high가 구조적 손절선
                if (recoveryIsUpS && recoveryExtremeS > 0 && recoveryExtremeS > ctx.CurrentPrice)
                {
                    decimal recoveryStopBuffer = recoveryExtremeS * 1.003m; // +0.3% 버퍼
                    if (ctx.CustomStopLossPrice <= 0 || recoveryStopBuffer > ctx.CustomStopLossPrice)
                        ctx.CustomStopLossPrice = recoveryStopBuffer;
                }
                EntryLog("RECOVERY", "ACTIVE", $"extreme={recoveryExtremeS:F4} dir={(recoveryIsUpS ? "UP" : "DOWN")} marginReduction=60%");
                OnStatusLog?.Invoke($"🌊 [회복구간] {ctx.Symbol} SHORT 진입 | 급변동 후 회복 모드 → 마진 60%, 넓은 손절");
            }

            // 6. 포지션 사이즈: adaptive equity
            ctx.Leverage = _settings.MajorLeverage > 0 ? _settings.MajorLeverage : _settings.DefaultLeverage;
            ctx.MarginUsdt = await GetAdaptiveEntryMarginUsdtAsync(ctx.Token);
            ctx.IsPumpStrategy = false;

            // [v3.3.6] 회복 모드 마진 축소 (60%)
            if (ctx.IsVolatilityRecovery)
                ctx.MarginUsdt = Math.Round(ctx.MarginUsdt * 0.6m, 2);

            decimal majorMarginPercent = GetConfiguredMajorMarginPercent();
            EntryLog("SIZE", "BASE", $"margin={ctx.MarginUsdt:F2} leverage={ctx.Leverage}x sizingRule=equity*{majorMarginPercent:F1}%{(ctx.IsVolatilityRecovery ? " [RECOVERY 60%]" : "")}");

            // 7. R:R 체크
            if (!EvaluateRiskRewardForEntry(ctx))
                return;

            // 7. 주문 실행
            await PlaceAndTrackEntryAsync(ctx);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [Pump LONG] 펌프 롱 진입
        // ═══════════════════════════════════════════════════════════════════════════
        private async Task ExecutePumpLongEntry(EntryContext ctx)
        {
            var EntryLog = ctx.EntryLog;

            // 1. AI Predictor (보너스 없음)
            await EvaluateAiPredictorForEntry(ctx, applyMajorBonuses: false);

            // 2. AI Score 사이즈 조절 제거 — 라우터 AI Gate Advisor에서 이미 적용됨

            // 3. 포지션 사이즈: 유동성 기반 동적 마진 (v5.0.5: 초저유동성 50% 축소)
            ctx.Leverage = _settings.MajorLeverage > 0 ? _settings.MajorLeverage : _settings.DefaultLeverage;
            ctx.MarginUsdt = GetLiquidityAdjustedPumpMarginUsdt(ctx.Symbol);
            ctx.IsPumpStrategy = true;

            EntryLog("SIZE", "BASE", $"margin={ctx.MarginUsdt:F2} leverage={ctx.Leverage}x coinType=Pumping source=PumpMargin");

            // 4. R:R 체크
            if (!EvaluateRiskRewardForEntry(ctx))
                return;

            // 5. 주문 실행
            await PlaceAndTrackEntryAsync(ctx);
        }

        private static bool ShouldBypassLowVolumeForMajorMeme(
            string signalSource,
            string decision,
            CandleData latestCandle,
            List<IBinanceKline>? recentEntryKlines,
            out string reason)
        {
            reason = string.Empty;

            if (!signalSource.StartsWith("MAJOR_MEME", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
                return false;

            float volumeRatio = latestCandle.Volume_Ratio;
            if (volumeRatio < 0.15f)
                return false;

            bool aboveSma20 = latestCandle.SMA_20 > 0f && latestCandle.Close > (decimal)latestCandle.SMA_20;
            bool healthyRsi = latestCandle.RSI >= 50f;
            bool higherLows = HasSuccessiveHigherLows(recentEntryKlines, 3);
            bool supportedTrend = aboveSma20 && (healthyRsi || higherLows);

            if (!supportedTrend)
                return false;

            reason = $"majorMemeLowVolumeBypass=true volumeRatio={volumeRatio:F2} sma20={(double)latestCandle.SMA_20:F4} rsi={latestCandle.RSI:F1} higherLows={higherLows}";
            return true;
        }

        private static bool HasSuccessiveHigherLows(List<IBinanceKline>? candles, int count)
        {
            if (candles == null || candles.Count < count + 1)
                return false;

            var recent = candles.TakeLast(count + 1).ToList();
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i].LowPrice <= recent[i - 1].LowPrice)
                    return false;
            }

            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [Pump SHORT] 펌프 숏 진입
        // ═══════════════════════════════════════════════════════════════════════════
        private async Task ExecutePumpShortEntry(EntryContext ctx)
        {
            var EntryLog = ctx.EntryLog;

            // 1. AI Predictor (보너스 없음)
            await EvaluateAiPredictorForEntry(ctx, applyMajorBonuses: false);

            // 2. SHORT 전용 차단: RSI 과매도 + 가격 MA20 위
            if (ctx.LatestCandle != null)
            {
                bool shortPriceAboveMa20 = ctx.LatestCandle.Close >= (decimal)ctx.LatestCandle.SMA_20;
                if (ctx.LatestCandle.RSI <= _shortRsiExhaustionFloor && shortPriceAboveMa20)
                {
                    EntryLog("AI", "BLOCK", $"shortFilter reason=RSI 과매도({ctx.LatestCandle.RSI:F1}≤{_shortRsiExhaustionFloor:F1}) + 가격 MA20 위");
                    return;
                }

                var shortInfos = new List<string>();
                if (ctx.AiPredictUp == true)
                    shortInfos.Add($"AI상승예측");
                if (ctx.LatestCandle.MACD > 0)
                    shortInfos.Add($"MACD양수({ctx.LatestCandle.MACD:F4})");
                if (shortInfos.Count > 0)
                    EntryLog("AI", "INFO", $"shortRef={string.Join(",", shortInfos)} (not blocking)");
            }

            // 3. AI Score 사이즈 조절 제거 — 라우터 AI Gate Advisor에서 이미 적용됨

            // 4. 포지션 사이즈: 유동성 기반 동적 마진 (v5.0.5: 초저유동성 50% 축소)
            ctx.Leverage = _settings.MajorLeverage > 0 ? _settings.MajorLeverage : _settings.DefaultLeverage;
            ctx.MarginUsdt = GetLiquidityAdjustedPumpMarginUsdt(ctx.Symbol);
            ctx.IsPumpStrategy = true;

            EntryLog("SIZE", "BASE", $"margin={ctx.MarginUsdt:F2} leverage={ctx.Leverage}x coinType=Pumping source=PumpMargin");

            // 5. R:R 체크
            if (!EvaluateRiskRewardForEntry(ctx))
                return;

            // 6. 주문 실행
            await PlaceAndTrackEntryAsync(ctx);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [공통] AI Predictor 평가 + 보너스 점수
        // [v5.10.99 P2-3] 점수 시스템 명확화:
        //   - ctx.AiScore (TradeHistory.AiScore) ← AIPredictor.Predict() = scalping_model.zip 단일 모델 (0~100)
        //   - Bot_Log.ML_Conf ← AIDoubleCheckEntryGate의 EntryTimingMLTrainer variant 점수 (0~1, 4 variant 중 선택)
        //   두 점수는 서로 다른 모델 출처. AiScore는 1차 screening, ML_Conf는 2차 dual gate 검증.
        //   UI 표시: AiScore (legacy 호환), DB 분석 시 ML_Conf 우선 참고.
        // ═══════════════════════════════════════════════════════════════════════════
        // [AI 제거] EvaluateAiPredictorForEntry 본체 제거 — AIPredictor 의존
        private Task EvaluateAiPredictorForEntry(EntryContext ctx, bool applyMajorBonuses) => Task.CompletedTask;

        // ═══════════════════════════════════════════════════════════════════════════
        // [공통] AI Score 기반 사이즈 멀티플라이어 산출
        // ═══════════════════════════════════════════════════════════════════════════
        private decimal EvaluateAiScoreSizeMultiplier(EntryContext ctx)
        {
            if (!_enableAiScoreFilter || ctx.LatestCandle == null)
                return 1.0m;

            CoinType entryCoinType = ResolveCoinType(ctx.Symbol, ctx.SignalSource);
            var symbolThreshold = GetThresholdByCoinType(entryCoinType);
            var (adaptiveMajorThreshold, adaptiveNormalThreshold, adaptivePumpThreshold, adaptiveMode) = GetAdaptiveAiScoreThresholds(ctx.SignalSource);
            float adjustedThreshold = entryCoinType switch
            {
                CoinType.Major => adaptiveMajorThreshold,
                CoinType.Pumping => adaptivePumpThreshold,
                _ => adaptiveNormalThreshold
            };
            adjustedThreshold = Math.Max(adjustedThreshold, symbolThreshold.EntryScoreCut);

            float finalAiScore = Math.Max(0f, (float)ctx.ConvictionScore);
            if (finalAiScore < adjustedThreshold)
            {
                float scoreRatio = adjustedThreshold > 0 ? finalAiScore / adjustedThreshold : 0.5f;
                decimal aiScoreMultiplier = scoreRatio switch
                {
                    >= 0.90f => 0.80m,
                    >= 0.70f => 0.50m,
                    >= 0.50f => 0.30m,
                    _        => 0.15m
                };
                ctx.EntryLog("AI", "ADVISOR", $"score={finalAiScore:F1}<{adjustedThreshold:F1} ratio={scoreRatio:P0} size={aiScoreMultiplier:P0}");
                OnStatusLog?.Invoke($"🧠 [AI Advisor] {ctx.Symbol} {ctx.Decision} | score={finalAiScore:F1}/{adjustedThreshold:F1} → 사이즈 {aiScoreMultiplier:P0}");
                return aiScoreMultiplier;
            }
            else
            {
                ctx.EntryLog("AI", "PASS", $"score={finalAiScore:F1}>={adjustedThreshold:F1}");
                return 1.0m;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [공통] R:R 체크
        // ═══════════════════════════════════════════════════════════════════════════
        private bool EvaluateRiskRewardForEntry(EntryContext ctx)
        {
            var EntryLog = ctx.EntryLog;

            if (string.Equals(ctx.Mode, "SIDEWAYS", StringComparison.OrdinalIgnoreCase))
                return true;

            // [v3.2.13] SPIKE/CRASH/PUMP 리버스는 R:R 스킵 (급변 시 진입 우선)
            if (ctx.SignalSource == "SPIKE_DETECT" || ctx.SignalSource == "CRASH_REVERSE" || ctx.SignalSource == "PUMP_REVERSE")
                return true;

            // [v5.2.4] TICK_SURGE는 빠른 반응이 핵심 → RR 최소값 0.5로 완화
            decimal effectiveMinRiskRewardRatio = _minEntryRiskRewardRatio;
            if (ctx.SignalSource == "TICK_SURGE")
                effectiveMinRiskRewardRatio = Math.Min(effectiveMinRiskRewardRatio, 0.5m);
            bool usingDefaultTargetAndStop = ctx.CustomTakeProfitPrice <= 0m && ctx.CustomStopLossPrice <= 0m;
            decimal majorTp2 = _settings.MajorTp2Roe > 0 ? _settings.MajorTp2Roe : _settings.TargetRoe;
            decimal majorSl = _settings.MajorStopLossRoe > 0 ? _settings.MajorStopLossRoe : _settings.StopLossRoe;
            if (usingDefaultTargetAndStop && majorSl > 0m && majorTp2 > 0m)
            {
                decimal defaultRrFromSettings = majorTp2 / majorSl;
                if (defaultRrFromSettings < effectiveMinRiskRewardRatio)
                {
                    effectiveMinRiskRewardRatio = Math.Max(1.0m, defaultRrFromSettings);
                    if (!_rrConfigMismatchWarned)
                    {
                        _rrConfigMismatchWarned = true;
                        OnStatusLog?.Invoke(
                            $"⚠️ [RR 설정 보정] 기본 Target/Stop 비율({defaultRrFromSettings:F2})이 최소 RR({_minEntryRiskRewardRatio:F2})보다 낮아 기본 진입은 과차단됩니다. 이번 진입부터 유효 RR 기준을 {effectiveMinRiskRewardRatio:F2}로 적용합니다.");
                    }
                }
            }

            if (!TryEvaluateEntryRiskReward(
                ctx.Decision,
                ctx.CurrentPrice,
                ctx.Leverage,
                ctx.CustomTakeProfitPrice,
                ctx.CustomStopLossPrice,
                out decimal evaluatedTakeProfit,
                out decimal evaluatedStopLoss,
                out decimal riskRewardRatio,
                out string rrReason))
            {
                OnStatusLog?.Invoke($"⛔ {ctx.Symbol} 손익비 계산 실패로 진입 차단 | {rrReason}");
                EntryLog("RR", "BLOCK", $"reason={rrReason}");
                return false;
            }

            if (riskRewardRatio < effectiveMinRiskRewardRatio)
            {
                OnStatusLog?.Invoke(
                    $"⛔ {ctx.Symbol} 공통 손익비 부족으로 진입 차단 | RR={riskRewardRatio:F2}<{effectiveMinRiskRewardRatio:F2} | TP={evaluatedTakeProfit:F8}, SL={evaluatedStopLoss:F8}");
                EntryLog("RR", "BLOCK", $"rr={riskRewardRatio:F2}<{effectiveMinRiskRewardRatio:F2}");
                return false;
            }

            EntryLog("RR", "PASS", $"rr={riskRewardRatio:F2} tp={evaluatedTakeProfit:F8} sl={evaluatedStopLoss:F8}");

            // ElliottWave R:R 참고 로그
            var waveState = _elliotWave3Strategy?.GetCurrentState(ctx.Symbol);
            if (waveState != null && ctx.Decision == "LONG")
            {
                decimal stopLossPrice = waveState.Phase1LowPrice > 0 ? waveState.Phase1LowPrice : 0;
                decimal takeProfitPrice = waveState.Phase1HighPrice > 0 ? waveState.Phase1HighPrice : 0;

                if (stopLossPrice > 0 && takeProfitPrice > 0)
                {
                    decimal risk = ctx.CurrentPrice - stopLossPrice;
                    decimal reward1 = takeProfitPrice - ctx.CurrentPrice;
                    decimal ewRr = risk > 0 ? reward1 / risk : 0;
                    EntryLog("EW_RR", "INFO", $"rr={ewRr:F2} (no block)");
                }
            }

            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // [공통] 주문 실행 + 포스트 필 처리
        // ═══════════════════════════════════════════════════════════════════════════
        private async Task PlaceAndTrackEntryAsync(EntryContext ctx)
        {
            var EntryLog = ctx.EntryLog;
            bool positionReserved = false;
            bool orderPlaced = false;
            string symbol = ctx.Symbol;
            string decision = ctx.Decision;

            void CleanupReservedPosition(string reason)
            {
                if (!positionReserved || orderPlaced)
                    return;

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var reservedPos) && reservedPos.Quantity == 0)
                    {
                        _activePositions.Remove(symbol);
                        EntryLog("RESERVE", "CLEANUP", $"reason={reason}");
                    }
                }
            }

            OnStatusLog?.Invoke(
                $"🧾 [진입 검증] src={ctx.SignalSource} | mode={ctx.Mode} | {symbol} {decision} | AI.Score={ctx.AiScore:F3} | AI.Prob={ctx.AiProbability:P1} | AI.Dir={(ctx.AiPredictUp.HasValue ? (ctx.AiPredictUp.Value ? "UP" : "DOWN") : "N/A")} | RSI={(ctx.LatestCandle != null ? ctx.LatestCandle.RSI.ToString("F1") : "N/A")}");

            try
            {
                // 포지션 사이즈 산출
                decimal marginUsdt = ctx.MarginUsdt;
                int leverage = ctx.Leverage;
                decimal positionSizeMultiplier = 1.0m;
                decimal effectiveSizeMultiplier = Math.Clamp(ctx.SizeMultiplier, 0.10m, 2.00m);

                // [v4.5.5] 알트 불장 모드: 레버리지 50% 하향 + 사이즈 70%
                if (_altBullDetector.IsActive)
                {
                    int originalLev = leverage;
                    decimal originalMul = effectiveSizeMultiplier;
                    leverage = _altBullDetector.AdjustLeverage(leverage);
                    effectiveSizeMultiplier = _altBullDetector.AdjustSizeMultiplier(effectiveSizeMultiplier);
                    OnStatusLog?.Invoke($"🔥 [알트불장] {symbol} 레버리지 {originalLev}x→{leverage}x, 사이즈 {originalMul:F2}→{effectiveSizeMultiplier:F2}");
                }

                if (ctx.PatternSnapshot != null)
                {
                    ctx.ConvictionScore = Math.Max(ctx.ConvictionScore, (decimal)ctx.PatternSnapshot.FinalScore);
                }

                if (!ctx.IsPumpStrategy)
                {
                    // Major: 패턴 기반 어그레시브 멀티플라이어
                    if (ctx.PatternSnapshot != null)
                    {
                        decimal patternSizeMultiplier = ctx.PatternSnapshot.Match?.PositionSizeMultiplier ?? 1.0m;
                        bool hasStrongPatternMatch =
                            ctx.PatternSnapshot.Match?.IsSuperEntry == true ||
                            (ctx.PatternSnapshot.Match?.TopSimilarity ?? 0d) >= 0.80d ||
                            (ctx.PatternSnapshot.Match?.MatchProbability ?? 0d) >= 0.70d;

                        if (ctx.ConvictionScore >= 85.0m && hasStrongPatternMatch)
                        {
                            decimal aggressiveMultiplier = patternSizeMultiplier > 1.0m
                                ? patternSizeMultiplier
                                : 1.5m;

                            positionSizeMultiplier = Math.Clamp(aggressiveMultiplier, 1.5m, 2.0m);
                            OnStatusLog?.Invoke($"🚀 [Aggressive Entry] {symbol} 고신뢰 진입 감지 | Score={ctx.ConvictionScore:F1}, PatternStrong={hasStrongPatternMatch} → 비중 x{positionSizeMultiplier:F2}");
                        }
                        else if (patternSizeMultiplier > 1.0m)
                        {
                            OnStatusLog?.Invoke($"🧮 [Pattern Size] {symbol} 패턴 배수 제안 x{patternSizeMultiplier:F2} 감지, 하지만 Score={ctx.ConvictionScore:F1} < 85 또는 강매치 미충족으로 기본 비중 유지");
                        }
                    }

                    if (effectiveSizeMultiplier != 1.0m)
                    {
                        positionSizeMultiplier *= effectiveSizeMultiplier;
                        EntryLog("SIZE", "ADJUST", $"sizeMultiplier={effectiveSizeMultiplier:F2} finalMultiplier={positionSizeMultiplier:F2}");
                    }

                    marginUsdt *= positionSizeMultiplier;
                    if (positionSizeMultiplier != 1.0m)
                    {
                        OnStatusLog?.Invoke($"💼 [Position Sizing] {symbol} 최종 증거금 {marginUsdt:F2} USDT (배수 x{positionSizeMultiplier:F2})");
                    }
                }
                else
                {
                    // Pump: 고정 마진, 사이즈 멀티플라이어 적용
                    if (effectiveSizeMultiplier != 1.0m)
                    {
                        marginUsdt *= effectiveSizeMultiplier;
                        positionSizeMultiplier = effectiveSizeMultiplier;
                        EntryLog("SIZE", "ADJUST", $"pumpSizeMultiplier={effectiveSizeMultiplier:F2} margin={marginUsdt:F2}");
                    }
                    else
                    {
                        positionSizeMultiplier = 1.0m;
                        EntryLog("SIZE", "FIXED", $"coinType=Pumping margin={marginUsdt:F2}USDT source=PumpMargin");
                    }
                }

                if (ctx.ScoutModeActivated)
                {
                    OnStatusLog?.Invoke($"🛰️ [SCOUT MODE] {symbol} 정찰 진입 체결 후 ML/거래량 회복 시 추가 진입 검토 대상입니다.");
                }

                // 중복 진입 방지 + 슬롯 최종 재체크
                var waveState = _elliotWave3Strategy?.GetCurrentState(symbol);

                lock (_posLock)
                {
                    if (_activePositions.ContainsKey(symbol))
                    {
                        if (ctx.ScoutAddOnEligible)
                        {
                            ctx.IsScoutAddOnOrder = true;
                            EntryLog("POSITION", "ADDON", "activePosition=scoutAddOn");
                        }
                        else
                        {
                            OnStatusLog?.Invoke($"⛔ [중복차단] {symbol} 이미 포지션 진행 중 → 진입 스킵");
                            EntryLog("POSITION", "SKIP", "activePosition=exists");
                            return;
                        }
                    }

                    // [v3.2.21] ScoutMode여도 슬롯 제한 적용 (무제한 진입 방지)
                    if (!ctx.IsScoutAddOnOrder)
                    {
                        bool isMajorSymbol = MajorSymbols.Contains(symbol);
                        // [v5.2.2] IsOwnPosition만 카운트
                        int finalTotal = _activePositions.Count(p => p.Value.IsOwnPosition);
                        int finalMajorCount = _activePositions.Count(p => p.Value.IsOwnPosition && MajorSymbols.Contains(p.Key));
                        int finalPumpCount = finalTotal - finalMajorCount;

                        if (isMajorSymbol && finalMajorCount >= MAX_MAJOR_SLOTS)
                        {
                            OnStatusLog?.Invoke($"⛔ [슬롯 최종 재확인] {symbol} 메이저 포화 ({finalMajorCount}/{MAX_MAJOR_SLOTS}) → 진입 차단");
                            EntryLog("SLOT", "FINAL_RECHECK_FAIL", $"major={finalMajorCount}/{MAX_MAJOR_SLOTS}");
                            return;
                        }

                        if (!isMajorSymbol && finalPumpCount >= MAX_PUMP_SLOTS)
                        {
                            OnStatusLog?.Invoke($"⛔ [슬롯 최종 재확인] {symbol} PUMP 포화 ({finalPumpCount}/{MAX_PUMP_SLOTS}) → 진입 차단");
                            EntryLog("SLOT", "FINAL_RECHECK_FAIL", $"pump={finalPumpCount}/{MAX_PUMP_SLOTS}");
                            return;
                        }

                        if (finalTotal >= MAX_TOTAL_SLOTS)
                        {
                            OnStatusLog?.Invoke($"⛔ [슬롯 최종 재확인] {symbol} 총 포화 ({finalTotal}/{MAX_TOTAL_SLOTS}) → 진입 차단");
                            EntryLog("SLOT", "FINAL_RECHECK_FAIL", $"total={finalTotal}/{MAX_TOTAL_SLOTS}");
                            return;
                        }

                        _activePositions[symbol] = new PositionInfo
                        {
                            Symbol = symbol,
                            EntryPrice = ctx.CurrentPrice,
                            IsLong = (decision == "LONG"),
                            Side = (decision == "LONG") ? OrderSide.Buy : OrderSide.Sell,
                            IsPumpStrategy = ctx.IsPumpStrategy,
                            AiScore = ctx.AiScore,
                            AiConfidencePercent = ctx.AiProbability > 0
                                ? ctx.AiProbability * 100f
                                : (ctx.AiScore > 0f && ctx.AiScore <= 1f ? ctx.AiScore * 100f : ctx.AiScore),
                            Leverage = leverage,
                            Quantity = 0,
                            InitialQuantity = 0,
                            EntryTime = DateTime.Now,
                            HighestPrice = ctx.CurrentPrice,
                            LowestPrice = ctx.CurrentPrice,
                            EntryBbPosition = ctx.EntryBbPosition,
                            EntryZoneTag = ctx.EntryZoneTag,
                            IsHybridMidBandLongEntry = ctx.IsHybridMidBandLongEntry,
                            AggressiveMultiplier = positionSizeMultiplier,
                            Wave1LowPrice = waveState?.Phase1LowPrice ?? 0,
                            Wave1HighPrice = waveState?.Phase1HighPrice ?? 0,
                            Fib0618Level = waveState?.Fib0618Level ?? 0,
                            Fib0786Level = waveState?.Fib786Level ?? 0,
                            Fib1618Target = waveState?.Fib1618Target ?? 0,
                            PartialProfitStage = 0,
                            BreakevenPrice = 0,
                            TakeProfit = ctx.CustomTakeProfitPrice > 0 ? ctx.CustomTakeProfitPrice : 0,
                            StopLoss = ctx.CustomStopLossPrice > 0 ? ctx.CustomStopLossPrice : 0,
                            IsVolatilityRecovery = ctx.IsVolatilityRecovery,
                            RecoveryExtremePrice = ctx.RecoveryExtremePrice
                        };
                    }
                }
                positionReserved = !ctx.IsScoutAddOnOrder;

                // [AI 제거] EntryZoneCollector 컨텍스트 수집 통째 제거

                // 레버리지 설정 — 심볼 최대 레버리지 자동 조정
                int actualLeverage = await _exchangeService.SetLeverageAutoAsync(symbol, leverage, ct: ctx.Token);
                if (actualLeverage <= 0)
                {
                    CleanupReservedPosition("레버리지 설정 실패");
                    OnStatusLog?.Invoke($"❌ {symbol} 레버리지 {leverage}x 설정 실패로 진입 취소");
                    EntryLog("ORDER_SETUP", "FAIL", $"leverageSet=false symbol={symbol} leverage={leverage}x src={ctx.SignalSource}");
                    return;
                }
                if (actualLeverage != leverage)
                {
                    OnStatusLog?.Invoke($"⚙️ [LEVERAGE_ADJUSTED] {symbol} 시도={leverage}x → 실제={actualLeverage}x (Binance 심볼 max 한도)");
                }
                leverage = actualLeverage;

                // [v5.10.100 ROOT FIX] PositionInfo.Leverage를 Binance 실제값으로 갱신
                //   기존: PositionInfo 저장 시점(line 11177)에는 시도 leverage(25x) 저장 → SetLeverageAutoAsync 후 5x 갱신 안 됨
                //   결과: UI ROE = price_change × 25 → 실제 5배 부풀려진 값 (사용자 -56% 본 게 실제 -11%)
                //   수정: actualLeverage 받은 직후 PositionInfo.Leverage 갱신
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var existingPos) && existingPos.Leverage != actualLeverage)
                    {
                        OnStatusLog?.Invoke($"🔧 [LEVERAGE_SYNC] {symbol} PositionInfo.Leverage {existingPos.Leverage} → {actualLeverage} (Binance 실제값 반영)");
                        existingPos.Leverage = actualLeverage;
                    }
                }

                // [AI 제거] ProfitRegressor 사이징 통째 제거 (사이즈 1.0 고정)

                // 수량 계산
                decimal quantity = (marginUsdt * leverage) / ctx.CurrentPrice;

                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(ctx.Token);
                var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                }

                if (quantity <= 0)
                {
                    CleanupReservedPosition("수량 계산 결과 0");
                    OnStatusLog?.Invoke($"❌ {symbol} 수량 계산 결과가 0 이하라 진입 취소");
                    EntryLog("SIZE", "BLOCK", "quantity<=0");
                    return;
                }

                // TP/SL 가격 계산 (주문 전 계산)
                decimal tpPrice = ctx.CustomTakeProfitPrice;
                decimal slPrice = ctx.CustomStopLossPrice;
                decimal leverageDecimal = leverage;

                // TP 가격 계산 (설정값 없으면 ROE 기반)
                if (tpPrice <= 0 && leverageDecimal > 0)
                {
                    decimal tp1Roe = ctx.IsPumpStrategy ? 25.0m : (symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ? 20.0m : 30.0m);
                    tpPrice = (decision == "LONG")
                        ? ctx.CurrentPrice + (ctx.CurrentPrice * (tp1Roe / leverageDecimal / 100m))
                        : ctx.CurrentPrice - (ctx.CurrentPrice * (tp1Roe / leverageDecimal / 100m));
                }

                // SL 가격 계산 (설정값 없으면 기본)
                if (slPrice <= 0 && leverageDecimal > 0)
                {
                    decimal slRoe = 10.0m; // 기본 10%
                    slPrice = (decision == "LONG")
                        ? ctx.CurrentPrice - (ctx.CurrentPrice * (slRoe / leverageDecimal / 100m))
                        : ctx.CurrentPrice + (ctx.CurrentPrice * (slRoe / leverageDecimal / 100m));
                }

                string positionSide = (decision == "LONG") ? "LONG" : "SHORT";
                OnStatusLog?.Invoke(TradingStateLogger.PlacingOrder(symbol, decision, ctx.CurrentPrice, quantity));
                EntryLog("ORDER", "SUBMIT", $"type=FULL_ENTRY orderSide={positionSide} qty={quantity} margin=${marginUsdt:F0} SL={slPrice:F4} TP={tpPrice:F4}");

                // [v5.10.90] 봇 자체 진입 시각 마킹 (partial fill ACCOUNT_UPDATE 오분류 차단)
                MarkBotEntryInProgress(symbol);

                var success = await _exchangeService.ExecuteFullEntryWithAllOrdersAsync(
                    symbol,
                    positionSide,
                    quantity,
                    leverageDecimal,
                    slPrice,
                    tpPrice,
                    partialProfitRoePercent: 40.0m, // 부분 익절 40%
                    trailingStopCallbackRate: 0.01m, // 트레일링 1%
                    ct: ctx.Token);

                if (!success)
                {
                    CleanupReservedPosition("전체 주문 실패");
                    OnStatusLog?.Invoke($"❌ [{symbol}] {decision} 전체 주문 실패");
                    OnAlert?.Invoke($"❌ [{symbol}] {decision} 전체 주문 실패 — Order_Error 확인");
                    EntryLog("ORDER", "FAILED", $"reason=ExecuteFullEntryWithAllOrdersAsync 실패");
                    try { _ = _dbManager.SaveOrderErrorAsync(symbol, positionSide, "FULL_ENTRY", quantity, null, "ExecuteFullEntryWithAllOrdersAsync 실패"); } catch { }
                    // [v5.7.8] 주문 실패 시 5분 블랙리스트
                    _blacklistedSymbols[symbol] = DateTime.Now.AddMinutes(5);
                    return;
                }

                // [v5.10.17] PUMP_WATCH_CONFIRMED 실제 진입 확정 카운터
                if (ctx.SignalSource == "PUMP_WATCH_CONFIRMED")
                    CommitDailyPumpEntry(symbol);

                // [v5.12.0] 급등 단일 슬롯 등록 — MAJOR_MEME (PumpScan 3 fast paths) 실제 체결 시
                if (IsSpikeCategorySignal(ctx.SignalSource))
                {
                    _activeSpikeSlot[symbol] = (DateTime.Now, ctx.SignalSource ?? "MAJOR_MEME");
                    OnStatusLog?.Invoke($"🔒 [SPIKE_SLOT] {symbol} {ctx.SignalSource} 슬롯 점유 (1/{MAX_SPIKE_CATEGORY_SLOTS})");
                }

                // [v5.10.18] OrderManager에 브라켓 주문 등록 (OCO 관리)
                _orderManager.RegisterBracket(symbol);

                // 체결가 추정 (실제로는 API에서 받아야 하지만, 현재 가격 사용)
                var actualEntryPrice = ctx.CurrentPrice;
                var filledQty = quantity; // 전체 수량 체결 가정

                // 포지션 업데이트
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var pos))
                    {
                        if (ctx.IsScoutAddOnOrder)
                        {
                            decimal previousQty = pos.Quantity;
                            decimal mergedQty = previousQty + filledQty;

                            if (mergedQty > 0m && previousQty > 0m)
                            {
                                pos.EntryPrice = ((pos.EntryPrice * previousQty) + (actualEntryPrice * filledQty)) / mergedQty;
                            }
                            else
                            {
                                pos.EntryPrice = actualEntryPrice;
                            }

                            pos.Quantity = mergedQty;
                            pos.InitialQuantity = pos.InitialQuantity > 0 ? pos.InitialQuantity : previousQty;
                            pos.HighestPrice = Math.Max(pos.HighestPrice, actualEntryPrice);
                            pos.LowestPrice = pos.LowestPrice > 0 ? Math.Min(pos.LowestPrice, actualEntryPrice) : actualEntryPrice;
                            pos.PyramidCount = Math.Max(1, pos.PyramidCount + 1);
                            pos.IsPyramided = true;
                        }
                        else
                        {
                            pos.Quantity = filledQty;
                            pos.InitialQuantity = pos.InitialQuantity > 0 ? pos.InitialQuantity : filledQty;
                            pos.EntryPrice = actualEntryPrice;
                            pos.HighestPrice = actualEntryPrice;
                            pos.LowestPrice = actualEntryPrice;
                            pos.PyramidCount = 0;
                            pos.IsPyramided = false;
                        }

                        if (ctx.CustomStopLossPrice > 0)
                            pos.StopLoss = ctx.CustomStopLossPrice;

                        // [v3.0.11] TP 가격 계산 — ROE% 기준 목표가 설정 (UI 표시용)
                        if (ctx.CustomTakeProfitPrice > 0)
                        {
                            pos.TakeProfit = ctx.CustomTakeProfitPrice;
                        }
                        else if (pos.Leverage > 0 && pos.EntryPrice > 0)
                        {
                            // TP1 ROE%를 가격으로 변환: BTC=20%, ETH/SOL/XRP=30%, PUMP=25%
                            decimal tp1Roe = pos.IsPumpStrategy ? 25.0m
                                : (symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) ? 20.0m : 30.0m);
                            decimal tpPriceDelta = pos.EntryPrice * (tp1Roe / pos.Leverage / 100m);
                            pos.TakeProfit = pos.IsLong
                                ? pos.EntryPrice + tpPriceDelta
                                : pos.EntryPrice - tpPriceDelta;
                        }

                        // [v5.10.39] 진입 성공 즉시 IsOwnPosition=true — sync 대기 없이 슬롯 즉시 반영
                        pos.IsOwnPosition = true;
                        orderPlaced = true;
                    }
                }

                if (!orderPlaced)
                {
                    EntryLog("POSITION", "WARN", "postFillReservedPositionMissing=true");
                }

                EntryLog("ORDER", "FILLED", $"entryPrice={actualEntryPrice:F4} qty={filledQty}");
                _lastSuccessfulEntryTime = DateTime.Now;
                // [v4.6.0] 진입 심볼 동적 수집 등록 (지속 수집 보장)
                _marketHistoryService?.RegisterSymbol(symbol);

                if (ctx.ScoutModeActivated)
                {
                    _scoutAddOnPendingSymbols[symbol] = DateTime.UtcNow;
                }
                else if (ctx.IsScoutAddOnOrder)
                {
                    _scoutAddOnPendingSymbols.TryRemove(symbol, out _);
                }

                if (!string.IsNullOrWhiteSpace(ctx.AiGateDecisionId))
                {
                    SetActiveAiDecisionId(symbol, ctx.AiGateDecisionId);
                }
                else
                {
                    RemoveActiveAiDecisionId(symbol);
                }

                ScheduleAiDoubleCheckLabeling(symbol, actualEntryPrice, decision == "LONG", ctx.AiGateDecisionId, ctx.Token);

                OnTradeExecuted?.Invoke(symbol, decision, actualEntryPrice, filledQty);

                // [v3.3.6] 체결 후 SL/TP를 UI에 전달 (자동진입에서도 DataGrid에 표시)
                decimal finalStopLoss = ctx.CustomStopLossPrice > 0 ? ctx.CustomStopLossPrice : 0;
                decimal finalTakeProfit = ctx.CustomTakeProfitPrice > 0 ? ctx.CustomTakeProfitPrice : 0;
                // PositionInfo에서 계산된 TP도 반영
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var filledPos))
                    {
                        if (finalStopLoss <= 0 && filledPos.StopLoss > 0) finalStopLoss = filledPos.StopLoss;
                        if (finalTakeProfit <= 0 && filledPos.TakeProfit > 0) finalTakeProfit = filledPos.TakeProfit;
                    }
                }
                if (finalStopLoss > 0 || finalTakeProfit > 0)
                {
                    OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                    {
                        Symbol = symbol,
                        StopLossPrice = finalStopLoss,
                        TargetPrice = finalTakeProfit,
                    });
                }
                string entrySuccessMessage = TradingStateLogger.EntrySuccess(
                    symbol, decision, actualEntryPrice,
                    finalStopLoss, finalTakeProfit, ctx.SignalSource);
                OnStatusLog?.Invoke(entrySuccessMessage);

                // [v5.10.98 P1-4] CRASH_REVERSE / PUMP_REVERSE 도 폴백 SL 등록 (안전망)
                //   기존: OrderLifecycleManager 명시 제외 → monitor만 의존 → monitor 실패 시 무방비
                //   수정: SL만이라도 등록 (TP/Trailing은 monitor가 처리, SL은 catastrophic loss 방지용)
                if (ctx.SignalSource == "CRASH_REVERSE" || ctx.SignalSource == "PUMP_REVERSE")
                {
                    try
                    {
                        bool isLongRev = decision == "LONG";
                        int levRev = ctx.Leverage > 0 ? ctx.Leverage : 20;
                        await RegisterProtectionOrdersAsync(symbol, isLongRev, filledQty, actualEntryPrice, levRev, ctx.SignalSource, _cts?.Token ?? CancellationToken.None);
                    }
                    catch (Exception revEx) { OnStatusLog?.Invoke($"⚠️ [{ctx.SignalSource}] {symbol} 폴백 SL 등록 예외: {revEx.Message}"); }
                }

                // [v5.10.54] OrderLifecycleManager 단일 진입점 — SL/TP/Trailing 3개 한 번에 등록
                // 긴급 대응(CRASH_REVERSE/PUMP_REVERSE) 은 OrderLifecycle 제외 (위에 폴백 등록됨)
                if (_orderLifecycle != null
                    && ctx.SignalSource != "CRASH_REVERSE"
                    && ctx.SignalSource != "PUMP_REVERSE")
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool isLong = decision == "LONG";
                            bool isPump = !MajorSymbols.Contains(symbol);

                            int pumpLev = ctx.Leverage > 0 ? ctx.Leverage : 20;
                            decimal slRoe = isPump ? -(_settings.PumpStopLossRoe > 0 ? _settings.PumpStopLossRoe : 40m) : -50m;
                            decimal tpRoe = isPump ? Math.Max(_settings.PumpTp1Roe > 0 ? _settings.PumpTp1Roe : 25m, 25m) : 40m;
                            decimal tpPartial = isPump ? Math.Clamp((_settings.PumpFirstTakeProfitRatioPct > 0 ? _settings.PumpFirstTakeProfitRatioPct : 40m) / 100m, 0.05m, 0.95m) : 0.4m;
                            decimal trailCallback = isPump ? Math.Clamp((_settings.PumpTrailingGapRoe > 0 ? _settings.PumpTrailingGapRoe : 20m) / pumpLev, 0.1m, 5.0m) : 2.0m;

                            var result = await _orderLifecycle.RegisterOnEntryAsync(
                                symbol, isLong, actualEntryPrice, filledQty,
                                ctx.Leverage > 0 ? ctx.Leverage : 20,
                                stopLossRoePct: slRoe,
                                takeProfitRoePct: tpRoe,
                                tpPartialRatio: tpPartial,
                                trailingCallbackRate: trailCallback,
                                ct: CancellationToken.None);

                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    if (!string.IsNullOrEmpty(result.SlOrderId)) p.StopOrderId = result.SlOrderId;
                                    if (!string.IsNullOrEmpty(result.TpOrderId)) p.TpRegisteredOnExchange = true;
                                }
                            }

                            // [v5.10.101 ROOT FIX] SL 등록 실패 alert + watchdog 재시도
                            //   기존: SL 미등록도 silent (OnStatusLog만) → 사용자 모름
                            //   수정: SL_OrderId 비어있거나 등록 실패면 OnAlert + 텔레그램 + RegisterProtectionOrdersAsync 폴백
                            if (string.IsNullOrEmpty(result.SlOrderId))
                            {
                                OnAlert?.Invoke($"🚨 [SL_MISSING][{ctx.SignalSource}] {symbol} 진입 후 SL 등록 실패! 폴백 시도 중");
                                try
                                {
                                    int fbLev = ctx.Leverage > 0 ? ctx.Leverage : 20;
                                    await RegisterProtectionOrdersAsync(symbol, isLong, filledQty, actualEntryPrice, fbLev, $"FALLBACK_{ctx.SignalSource}", ctx.Token);
                                }
                                catch (Exception fbEx) { OnAlert?.Invoke($"❌ [SL_FALLBACK_FAIL] {symbol} 폴백 SL 등록 실패: {fbEx.Message} — 수동 SL 설정 필요"); }
                            }
                        }
                        catch (Exception regEx)
                        {
                            OnAlert?.Invoke($"🚨 [SL_REGISTRATION_EXCEPTION][{ctx.SignalSource}] {symbol} SL 등록 예외: {regEx.Message} — 수동 SL 확인 필요");
                            // 폴백 시도
                            try
                            {
                                bool isLongRetry = decision == "LONG";
                                int fbLev = ctx.Leverage > 0 ? ctx.Leverage : 20;
                                await RegisterProtectionOrdersAsync(symbol, isLongRetry, filledQty, actualEntryPrice, fbLev, $"EX_FALLBACK_{ctx.SignalSource}", ctx.Token);
                            }
                            catch { }
                        }
                    });

                    // [v5.10.101] SL Watchdog: 5초 후 algoOrder 실제 존재 검증 (silent fail 차단)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(5000);
                            int algoCount = 0;
                            if (_exchangeService is BinanceExchangeService binSvc)
                                algoCount = await binSvc.GetOpenAlgoOrderCountAsync(symbol, CancellationToken.None);
                            if (algoCount == 0)
                            {
                                OnAlert?.Invoke($"🚨 [SL_WATCHDOG] {symbol} 진입 5초 후 algoOrder 0건 — SL 등록 누락 감지! 긴급 폴백 등록 시도");
                                try
                                {
                                    bool isLongWd = decision == "LONG";
                                    int wdLev = ctx.Leverage > 0 ? ctx.Leverage : 20;
                                    await RegisterProtectionOrdersAsync(symbol, isLongWd, filledQty, actualEntryPrice, wdLev, $"WATCHDOG_{ctx.SignalSource}", CancellationToken.None);
                                }
                                catch (Exception wdEx) { OnAlert?.Invoke($"❌ [SL_WATCHDOG_FAIL] {symbol} {wdEx.Message} — 즉시 수동 SL 필수!"); }
                            }
                        }
                        catch { }
                    });
                }

                // HybridExitManager 등록
                if (finalTakeProfit > 0 && _hybridExitManager != null)
                {
                    _hybridExitManager.RegisterEntry(symbol, decision, actualEntryPrice, finalTakeProfit);
                    OnStatusLog?.Invoke($"📋 [Hybrid Exit] {symbol} 등록 | 목표가: ${finalTakeProfit:F2}, 손절: ${finalStopLoss:F2}");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var kRes15 = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                                symbol, KlineInterval.FifteenMinutes, limit: 20);
                            double smartAtr = 0;
                            if (kRes15.Success && kRes15.Data?.Length >= 15)
                                smartAtr = IndicatorCalculator.CalculateATR(kRes15.Data.ToList(), 14);

                            bool isLongEntry = decision == "LONG";
                            var (smartSL, smartTP, usedAtr) = HybridExitManager.ComputeSmartAtrTargets(
                                actualEntryPrice, isLongEntry, smartAtr);

                            var exitState = _hybridExitManager?.GetState(symbol);
                            if (exitState != null)
                            {
                                exitState.InitialSL = smartSL;
                                exitState.InitialTP = smartTP;
                            }

                            await TelegramService.Instance.SendEntrySuccessAlertAsync(
                                symbol, decision, actualEntryPrice,
                                finalStopLoss, finalTakeProfit, ctx.SignalSource,
                                marginUsdt, leverage,
                                smartSL, smartTP, usedAtr);
                        }
                        catch (Exception ex)
                        {
                            OnStatusLog?.Invoke($"⚠️ [SmartTarget] 계산 실패 [{symbol}]: {ex.Message} | 기본 진입 알림으로 대체");
                            await TelegramService.Instance.SendEntrySuccessAlertAsync(
                                symbol, decision, actualEntryPrice,
                                finalStopLoss, finalTakeProfit, ctx.SignalSource,
                                marginUsdt, leverage);
                        }
                    });
                }
                else
                {
                    await TelegramService.Instance.SendEntrySuccessAlertAsync(
                        symbol, decision, actualEntryPrice,
                        finalStopLoss, finalTakeProfit, ctx.SignalSource,
                        marginUsdt, leverage);
                }

                OnAlert?.Invoke($"🤖 자동 매매 진입: {symbol} [{decision}] | 증거금: {marginUsdt}U");
                _soundService.PlaySuccess();

                // DB 로그 저장
                DateTime tradeEntryTime = DateTime.Now;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var trackedPos) && trackedPos.EntryTime != default)
                        tradeEntryTime = trackedPos.EntryTime;
                }

                var entryLog = new TradeLog(
                    symbol,
                    positionSide,
                    ctx.SignalSource,
                    actualEntryPrice,
                    ctx.AiScore,
                    tradeEntryTime,
                    0,
                    0
                )
                {
                    EntryPrice = actualEntryPrice,
                    Quantity = filledQty,
                    EntryTime = tradeEntryTime,
                    IsSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false
                };
                try
                {
                    await _dbManager.UpsertTradeEntryAsync(entryLog);
                    OnStatusLog?.Invoke($"📝 {symbol} 진입 TradeHistory 저장 완료");
                }
                catch (Exception dbEx)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 진입 로그 DB 저장 실패: {dbEx.Message}");
                }

                if (ctx.PatternSnapshot != null)
                {
                    try
                    {
                        await _patternMemoryService.SaveEntrySnapshotAsync(ctx.PatternSnapshot, actualEntryPrice, tradeEntryTime, ctx.SignalSource);
                    }
                    catch (Exception patternEx)
                    {
                        OnStatusLog?.Invoke($"⚠️ {symbol} 패턴 스냅샷 저장 실패: {patternEx.Message}");
                    }
                }

                // [v3.3.6] UI 포지션 상태 활성화 (수동진입과 동일하게)
                OnPositionStatusUpdate?.Invoke(symbol, true, actualEntryPrice);

                // [v5.10.19] bot-side 감시 루프 결정
                // ExecuteFullEntryWithAllOrdersAsync로 거래소에 SL/TP/Trailing 등록 완료된 경우
                // → PositionSyncService(10초 폴링)가 청산 감지 + OrderManager가 잔여 주문 취소
                // → bot-side 이중 감시 불필요 (타임아웃/충돌 원인 제거)
                if (_orderManager.HasActiveBracket(symbol))
                {
                    OnStatusLog?.Invoke($"🛡️ [PositionSync] {symbol} bot-side 감시 스킵 — 거래소 SL/TP/Trailing 등록 완료");
                }
                else
                {
                    bool isPumpPosition = false;
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var activePos))
                            isPumpPosition = activePos.IsPumpStrategy;
                    }

                    if (isPumpPosition)
                    {
                        double pumpAtr = ctx.LatestCandle != null ? Math.Max(0d, ctx.LatestCandle.ATR) : 0d;
                        TryStartPumpMonitor(symbol, actualEntryPrice, ctx.SignalSource, pumpAtr, ctx.Token, "new-entry");
                    }
                    else
                    {
                        TryStartStandardMonitor(symbol, actualEntryPrice, decision == "LONG", ctx.Mode, tpPrice, slPrice, ctx.Token, "new-entry");
                    }
                }

                // [v3.2.46] 정찰대/AI Advisor 진입 → ROE 기반 본진입 전환 태스크
                bool isScoutEntry = ctx.ScoutModeActivated || ctx.SizeMultiplier < 1.0m;
                if (isScoutEntry && !ctx.IsScoutAddOnOrder)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await MonitorScoutToMainUpgradeAsync(symbol, decision, actualEntryPrice, leverage, ctx.Token); }
                        catch (Exception scoutEx) { OnStatusLog?.Invoke($"⚠️ [Scout→Main] {symbol} 전환 모니터 오류: {scoutEx.Message}"); }
                    }, ctx.Token);
                }
            }
            catch (TaskCanceledException tcEx)
            {
                CleanupReservedPosition($"진입 작업 타임아웃");
                OnStatusLog?.Invoke($"⏱️ {symbol} 진입 작업 타임아웃 (30초 초과) → 작업 취소");
                OnLiveLog?.Invoke($"🧭 [ENTRY][TIMEOUT] sym={symbol} side={decision} | timeout=30s | detail={tcEx.Message}");
                EntryLog("ENTRY", "TIMEOUT", "timeout-30s");
            }
            catch (OperationCanceledException)
            {
                CleanupReservedPosition($"봇 정지 신호로 인해 진입 캔슬됨");
                OnStatusLog?.Invoke($"⏹️ {symbol} 진입 진행 중 봇 정지 신호 수신 → 작업 취소");
                EntryLog("ENTRY", "CANCELED", "bot-stop-signal");
            }
            catch (Exception ex) when (ex.GetType().Name == "HttpRequestException")
            {
                CleanupReservedPosition($"거래소 API 연결 오류");
                OnStatusLog?.Invoke($"🌐 {symbol} 거래소 API 연결 오류 → 재시도 예정");
                OnLiveLog?.Invoke($"🧭 [ENTRY][API_ERROR] sym={symbol} side={decision} | detail={ex.Message}");
                EntryLog("ENTRY", "API_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                CleanupReservedPosition($"예외 발생: {ex.GetType().Name}");
                OnStatusLog?.Invoke($"❌ {symbol} 진입 중 예외 발생: {ex.GetType().Name} - {ex.Message}");
                OnLiveLog?.Invoke($"🧭 [ENTRY][ERROR] sym={symbol} side={decision} | type={ex.GetType().Name} | detail={ex.Message}");
                EntryLog("ENTRY", "ERROR", $"{ex.GetType().Name}: {ex.Message}");

                try
                {
                    await TelegramService.Instance.SendMessageAsync($"⚠️ *[자동 매매 오류]*\n{symbol} 진입 중 예외 발생:\n{ex.GetType().Name}\n{ex.Message}");
                }
                catch (Exception tgEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Telegram 알림 전송 실패: {tgEx.Message}");
                }
            }
        }

        private bool TryEvaluateEntryRiskReward(
            string decision,
            decimal entryPrice,
            int leverage,
            decimal customTakeProfitPrice,
            decimal customStopLossPrice,
            out decimal takeProfitPrice,
            out decimal stopLossPrice,
            out decimal riskRewardRatio,
            out string reason)
        {
            takeProfitPrice = 0m;
            stopLossPrice = 0m;
            riskRewardRatio = 0m;
            reason = string.Empty;

            if (entryPrice <= 0)
            {
                reason = "entryPrice<=0";
                return false;
            }

            int safeLeverage = leverage > 0 ? leverage : Math.Max(1, _settings.MajorLeverage > 0 ? _settings.MajorLeverage : _settings.DefaultLeverage);
            // [메이저/PUMP 완전 분리] 메이저 전용 ROE로 TP/SL 바능가 계산
            decimal majorTp2Roe = _settings.MajorTp2Roe > 0 ? _settings.MajorTp2Roe : (_settings.TargetRoe > 0 ? _settings.TargetRoe : 25.0m);
            decimal majorSlRoe = _settings.MajorStopLossRoe > 0 ? _settings.MajorStopLossRoe : (_settings.StopLossRoe > 0 ? _settings.StopLossRoe : 60.0m);
            decimal targetPriceMove = majorTp2Roe / safeLeverage / 100m;
            decimal stopPriceMove = majorSlRoe / safeLeverage / 100m;

            bool isLong = string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase);
            bool isShort = string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase);
            if (!isLong && !isShort)
            {
                reason = "decision!=LONG/SHORT";
                return false;
            }

            if (isLong)
            {
                takeProfitPrice = customTakeProfitPrice > 0 ? customTakeProfitPrice : entryPrice * (1m + targetPriceMove);
                stopLossPrice = customStopLossPrice > 0 ? customStopLossPrice : entryPrice * (1m - stopPriceMove);

                decimal risk = entryPrice - stopLossPrice;
                decimal reward = takeProfitPrice - entryPrice;
                if (risk <= 0 || reward <= 0)
                {
                    reason = $"invalidLongRiskReward risk={risk:F8} reward={reward:F8}";
                    return false;
                }

                riskRewardRatio = reward / risk;
            }
            else
            {
                takeProfitPrice = customTakeProfitPrice > 0 ? customTakeProfitPrice : entryPrice * (1m - targetPriceMove);
                stopLossPrice = customStopLossPrice > 0 ? customStopLossPrice : entryPrice * (1m + stopPriceMove);

                decimal risk = stopLossPrice - entryPrice;
                decimal reward = entryPrice - takeProfitPrice;
                if (risk <= 0 || reward <= 0)
                {
                    reason = $"invalidShortRiskReward risk={risk:F8} reward={reward:F8}";
                    return false;
                }

                riskRewardRatio = reward / risk;
            }

            return true;
        }

        private bool ShouldBlockHybridBbEntry(
            string symbol,
            string decision,
            decimal currentPrice,
            CandleData latestCandle,
            string signalSource,
            out string reason,
            out decimal bbPosition,
            out string zoneTag,
            out bool isHybridMidBandLongEntry)
        {
            reason = string.Empty;
            zoneTag = string.Empty;
            bbPosition = 0m;
            isHybridMidBandLongEntry = false;

            if (!IsHybridBbSignalSource(signalSource))
                return false;

            decimal bbLower = latestCandle.BollingerLower > 0 ? (decimal)latestCandle.BollingerLower : 0m;
            decimal bbUpper = latestCandle.BollingerUpper > 0 ? (decimal)latestCandle.BollingerUpper : 0m;
            decimal bbRange = bbUpper - bbLower;
            if (bbRange <= 0m)
                return false;

            bbPosition = (currentPrice - bbLower) / bbRange;
            bool bullishCandle = latestCandle.Close > latestCandle.Open;
            bool bearishCandle = latestCandle.Close < latestCandle.Open;
            bool relaxByDrought = GetEntryDroughtMinutes() >= 30;

            decimal longLowerBound = relaxByDrought ? 0.35m : 0.40m;
            decimal longUpperBound = relaxByDrought ? 0.80m : 0.70m;
            decimal shortLowerBound = relaxByDrought ? 0.75m : 0.80m;
            decimal shortUpperBound = relaxByDrought ? 0.92m : 0.90m;

            if (decision == "LONG")
            {
                if (bbPosition <= 0.10m)
                {
                    reason = $"%B {bbPosition:P0} 하단 붕괴 구간에서는 롱 금지";
                    return true;
                }

                // v2.4.12: 상단 추격 제한 완화 — RSI < 70이면 추세로 진입 허용
                if (bbPosition >= 0.85m && latestCandle.RSI >= 70f)
                {
                    // [메이저 고속도로] 정배열 추세 강세(TrendScore≥60 대리 조건) 시 BB 상단 저항 바이패스
                    bool isPerfectAlignmentUb = latestCandle.SMA_20 > latestCandle.SMA_60
                                             && latestCandle.SMA_60 > latestCandle.SMA_120;
                    if (MajorSymbols.Contains(symbol) && isPerfectAlignmentUb)
                    {
                        OnStatusLog?.Invoke($"⚡ [{symbol}] 정배열 추세 강세 → BB 상단 저항 바이패스 (%B={bbPosition:P0}, RSI={latestCandle.RSI:F1})");
                    }
                    else
                    {
                        reason = $"%B {bbPosition:P0} 상단 + RSI {latestCandle.RSI:F1} ≥ 70 과열 구간에서는 롱 금지";
                        return true;
                    }
                }

                if (bbPosition < longLowerBound || bbPosition > longUpperBound)
                {
                    // [메이저 고속도로] 정배열 추세 시 진입 허용 구간을 상단까지 확장
                    bool isPerfectAlignmentMid = latestCandle.SMA_20 > latestCandle.SMA_60
                                              && latestCandle.SMA_60 > latestCandle.SMA_120;
                    bool majorTrendExpand = MajorSymbols.Contains(symbol) && isPerfectAlignmentMid
                                        && bbPosition >= longLowerBound && bbPosition <= 0.85m;
                    if (!majorTrendExpand)
                    {
                        reason = $"LONG 기본 진입 구간은 {longLowerBound:P0}~{longUpperBound:P0} (%B {bbPosition:P0})";
                        return true;
                    }
                    OnStatusLog?.Invoke($"⚡ [{symbol}] 정배열 추세 → 진입 구간 45~85% 확장 적용 (%B={bbPosition:P0})");
                }

                if (!bullishCandle)
                {
                    reason = $"중단 구간이지만 양봉 확인 전이라 롱 대기 (Open={latestCandle.Open:F4}, Close={latestCandle.Close:F4})";
                    return true;
                }

                zoneTag = "MID_BAND_LONG";
                isHybridMidBandLongEntry = true;
                return false;
            }

            if (decision == "SHORT")
            {
                if (bbPosition <= 0.15m)
                {
                    reason = $"%B {bbPosition:P0} 하단 추격 구간에서는 숏 금지";
                    return true;
                }

                if (bbPosition < shortLowerBound || bbPosition > shortUpperBound)
                {
                    reason = $"SHORT 기본 진입 구간은 상단 저항({shortLowerBound:P0}~{shortUpperBound:P0})만 허용 (%B {bbPosition:P0})";
                    return true;
                }

                if (!bearishCandle)
                {
                    reason = $"상단 저항 구간이지만 음봉 확인 전이라 숏 대기 (Open={latestCandle.Open:F4}, Close={latestCandle.Close:F4})";
                    return true;
                }

                zoneTag = "UPPER_RESIST_SHORT";
            }

            return false;
        }

        private static bool IsHybridBbSignalSource(string signalSource)
        {
            if (string.IsNullOrWhiteSpace(signalSource))
                return false;

            return string.Equals(signalSource, "MAJOR", StringComparison.OrdinalIgnoreCase)
                || signalSource.StartsWith("TRANSFORMER_", StringComparison.OrdinalIgnoreCase);
        }

        private static CoinType ResolveCoinType(string symbol, string signalSource)
        {
            // [v5.10.85] static 메서드에서 인스턴스 MajorSymbols 사용 불가 → 설정창 직접 조회
            var src = AppConfig.Current?.Trading?.Symbols ?? new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
            if (!string.IsNullOrWhiteSpace(symbol) && src.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                return CoinType.Major;

            if (!string.IsNullOrWhiteSpace(signalSource)
                && (signalSource.Contains("PUMP", StringComparison.OrdinalIgnoreCase)
                    || signalSource.Contains("MEME", StringComparison.OrdinalIgnoreCase)))
            {
                return CoinType.Pumping;
            }

            return CoinType.Normal;
        }

        private static bool IsBbCenterSupport(float bbPosition, string decision)
        {
            if (!string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
                return false;

            if (float.IsNaN(bbPosition) || float.IsInfinity(bbPosition))
                return false;

            float normalized = Math.Clamp(bbPosition, 0f, 1f);
            return normalized >= 0.40f && normalized <= 0.60f;
        }

        /// <summary>
        /// [v5.0] Forecaster 예약 진입용 수량 계산 헬퍼
        /// 메이저는 Equity 비율, PUMP는 고정 증거금 기반
        /// </summary>
        private async Task<decimal> CalculateOrderQuantityAsync(string symbol, decimal price, CancellationToken token)
        {
            try
            {
                if (price <= 0) return 0m;

                bool isMajor = MajorSymbols.Contains(symbol);
                decimal marginUsdt = isMajor
                    ? await GetAdaptiveEntryMarginUsdtAsync(token)
                    : GetLiquidityAdjustedPumpMarginUsdt(symbol);

                int leverage = 20;
                decimal quantity = (marginUsdt * leverage) / price;

                // 거래소 step size 반영
                try
                {
                    var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
                    var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolData != null)
                    {
                        decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                        if (stepSize > 0)
                            quantity = Math.Floor(quantity / stepSize) * stepSize;
                    }
                }
                catch { }

                return quantity > 0 ? quantity : 0m;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [SIZE] {symbol} 수량 계산 오류: {ex.Message}");
                return 0m;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // [v5.0.1] Gate 1 + Gate 2 — 고점 진입 차단 + 지연 진입
        // 2026-04-12 DB 분석: TRUUSDT -41%, AIOTUSDT -33%, TAGUSDT -32%, SKYAIUSDT -28%
        // 공통 패턴: 진입 전 30분간 +5~14% 이미 올라있거나, 직전 봉 거대 음봉/긴 윗꼬리
        // 해결: Gate 1 으로 차단 → 10분 후 Gate 2 (pullback 반등) 확인 → 지연 진입
        // ═══════════════════════════════════════════════════════════════

        /// <summary>지연 진입 관리: symbol → (decision, source, registeredAt)</summary>
        private readonly ConcurrentDictionary<string, (string decision, string source, DateTime regAt)> _pendingGate2 = new();

        /// <summary>
        /// [v5.0.1 / v5.0.6] Gate 1: "이미 너무 올랐음" 차단
        /// - Check 1~4: 기본 조건 (v5.0.1)
        /// - Check 5~6: 중형 유동성 특화 조건 (v5.0.6) — $50~200M 코인 피크 방어
        ///   AIOTUSDT/SKYAIUSDT/CROSSUSDT 04-12 손실 케이스 대상
        /// </summary>
        private bool IsAlreadyPumpedRecently(List<IBinanceKline> candles5m, decimal currentPrice, out string reason)
        {
            reason = string.Empty;
            if (candles5m == null || candles5m.Count < 7 || currentPrice <= 0) return false;

            // [v5.11.1] Check 1 세분화 — 기존 1h 10% 만 → 다중 구간 (1h 5% + 30m 4% + 10m 3%)
            //   꼭대기 진입의 근본 원인은 "이미 많이 올랐음"을 느슨한 1h 기준으로만 봐서
            //   10분 안에 3% 이상 오른 직후 진입(스파이크 고점)을 못 걸렀음
            // [Check 1a] 1시간 누적 > 5% → 너무 늦음
            if (candles5m.Count >= 13)
            {
                decimal close1hAgo = candles5m[candles5m.Count - 13].ClosePrice;
                if (close1hAgo > 0)
                {
                    float runUpPct = (float)((currentPrice - close1hAgo) / close1hAgo * 100);
                    if (runUpPct > 5.0f)
                    {
                        reason = $"already_pumped 1h={runUpPct:F1}% > 5%";
                        return true;
                    }
                }
            }
            // [Check 1b] 30분 누적 > 4% (6 x 5m)
            if (candles5m.Count >= 7)
            {
                decimal close30mAgo = candles5m[candles5m.Count - 7].ClosePrice;
                if (close30mAgo > 0)
                {
                    float runUp30 = (float)((currentPrice - close30mAgo) / close30mAgo * 100);
                    if (runUp30 > 4.0f)
                    {
                        reason = $"already_pumped 30m={runUp30:F1}% > 4%";
                        return true;
                    }
                }
            }
            // [Check 1c] 10분 누적 > 3% (2 x 5m) — 스파이크 직후 차단
            if (candles5m.Count >= 3)
            {
                decimal close10mAgo = candles5m[candles5m.Count - 3].ClosePrice;
                if (close10mAgo > 0)
                {
                    float runUp10 = (float)((currentPrice - close10mAgo) / close10mAgo * 100);
                    if (runUp10 > 3.0f)
                    {
                        reason = $"spike_just_happened 10m={runUp10:F1}% > 3%";
                        return true;
                    }
                }
            }

            // [Check 2] 최근 6봉 고점에서 2~5% 조정 진행 중 (피크 후 하락 시작)
            decimal recent6High = decimal.MinValue;
            for (int i = Math.Max(0, candles5m.Count - 6); i < candles5m.Count; i++)
                if (candles5m[i].HighPrice > recent6High) recent6High = candles5m[i].HighPrice;
            if (recent6High > 0)
            {
                float pullbackFromHigh = (float)((recent6High - currentPrice) / recent6High * 100);
                if (pullbackFromHigh > 2.0f && pullbackFromHigh < 5.0f)
                {
                    reason = $"post_peak_falling highPullback={pullbackFromHigh:F1}%";
                    return true;
                }
            }

            // [Check 3] 직전 봉이 긴 윗꼬리 or 거대 음봉
            if (candles5m.Count >= 2)
            {
                var prev = candles5m[candles5m.Count - 2];
                decimal prevRange = prev.HighPrice - prev.LowPrice;
                if (prevRange > 0 && prev.OpenPrice > 0)
                {
                    // 긴 윗꼬리 체크 (range 대비 upperWick > 60%)
                    decimal bodyMax = Math.Max(prev.OpenPrice, prev.ClosePrice);
                    decimal upperWick = prev.HighPrice - bodyMax;
                    float upperWickRatio = (float)(upperWick / prevRange);
                    if (upperWickRatio > 0.60f)
                    {
                        reason = $"long_upper_wick ratio={upperWickRatio:P0}";
                        return true;
                    }

                    // 거대 음봉 체크 (range 4%+ AND close < open)
                    float rangePct = (float)(prevRange / prev.OpenPrice * 100);
                    if (rangePct > 4.0f && prev.ClosePrice < prev.OpenPrice)
                    {
                        reason = $"giant_bearish_candle range={rangePct:F1}%";
                        return true;
                    }
                }
            }

            // [v5.0.7] 중형 특화 하드코딩 조건(Check 5/6) 제거
            // 이유: Forecaster 대용량 학습 완료 → AI 가 피크/덤핑 패턴 직접 학습
            //       하드코딩 임계값(10%, 5%, 2개 등)은 주관적이라 AI 판단에 맡김
            //       Check 1~4 는 명백한 피크 징후(30m >8%, 2~5% 조정, 윗꼬리 60%+, 거대 음봉)
            //       만 남기고 정상 케이스는 통과시켜 Forecaster 에 위임

            return false;
        }

        /// <summary>
        /// [v5.0.1] Gate 2: "피크 후 pullback 반등 완료" 지연 진입 자리 탐지
        /// 최근 8봉에서 피크 찾고, 2~6봉 지났고, 2~5% 조정 완료,
        /// Higher Low 확인, 현재 양봉 + 거래량 회복 → 진짜 반등 자리
        /// </summary>
        private bool IsPullbackRecoveryEntry(List<IBinanceKline> candles5m, decimal currentPrice, out string reason)
        {
            reason = string.Empty;
            if (candles5m == null || candles5m.Count < 8 || currentPrice <= 0) return false;

            // [1] 최근 8봉 내 피크 위치 찾기 (최근 2봉 제외)
            int peakIdx = -1;
            decimal peakHigh = decimal.MinValue;
            int start = candles5m.Count - 8;
            for (int i = start; i < candles5m.Count - 2; i++)
            {
                if (candles5m[i].HighPrice > peakHigh)
                {
                    peakHigh = candles5m[i].HighPrice;
                    peakIdx = i;
                }
            }
            if (peakIdx < 0 || peakHigh <= 0)
            {
                reason = "no_peak_found";
                return false;
            }

            // [2] 피크 이후 2~6봉 경과 (너무 가까우면 아직 하락, 너무 멀면 재진입 아님)
            int barsSincePeak = candles5m.Count - 1 - peakIdx;
            if (barsSincePeak < 2 || barsSincePeak > 6)
            {
                reason = $"barsSincePeak={barsSincePeak} (not in 2~6)";
                return false;
            }

            // [3] 피크 대비 -2~5% 조정
            float pullbackPct = (float)((peakHigh - currentPrice) / peakHigh * 100);
            if (pullbackPct < 2.0f || pullbackPct > 5.0f)
            {
                reason = $"pullback={pullbackPct:F1}% (not 2~5%)";
                return false;
            }

            // [4] Higher Low 확인 (현재 봉 저점 > 피크 이후 최저 저점)
            decimal lowestAfterPeak = decimal.MaxValue;
            for (int i = peakIdx + 1; i < candles5m.Count - 1; i++)
                if (candles5m[i].LowPrice < lowestAfterPeak) lowestAfterPeak = candles5m[i].LowPrice;
            var currentBar = candles5m[candles5m.Count - 1];
            if (currentBar.LowPrice <= lowestAfterPeak)
            {
                reason = "lower_low (추가 하락 진행)";
                return false;
            }

            // [5] 현재 봉 양봉 (반등 확인)
            if (currentBar.ClosePrice <= currentBar.OpenPrice)
            {
                reason = "current_bar_bearish";
                return false;
            }

            // [6] 거래량 회복 — 피크 시점 평균 대비 80%+
            double avgVolAtPeak = 0;
            int cnt = 0;
            for (int i = peakIdx; i <= peakIdx + 2 && i < candles5m.Count; i++)
            {
                avgVolAtPeak += (double)candles5m[i].Volume;
                cnt++;
            }
            if (cnt > 0) avgVolAtPeak /= cnt;
            if (avgVolAtPeak > 0 && (double)currentBar.Volume < avgVolAtPeak * 0.8)
            {
                reason = $"vol_weak current={(double)currentBar.Volume:F0} vs peakAvg={avgVolAtPeak:F0}";
                return false;
            }

            reason = $"recovery_confirmed peak={peakHigh} pullback={pullbackPct:F1}% barsSince={barsSincePeak}";
            return true;
        }

        /// <summary>
        /// [v5.0.1 / v5.0.7] Gate 1 차단 후 지연 재평가 예약.
        /// 주기적으로 Gate 2 체크 → 통과 시 ExecuteAutoOrder 호출.
        /// 15분 내 기회 없으면 자동 만료.
        /// v5.0.7: 유동성별 대기시간 차등 제거 (Forecaster 가 타이밍 학습)
        /// </summary>
        private void ScheduleGate2Reevaluation(string symbol, string decision, string signalSource)
        {
            // 중복 예약 방지
            if (!_pendingGate2.TryAdd(symbol, (decision, signalSource, DateTime.Now)))
            {
                return;
            }

            const int maxWaitMinutes = 15;
            OnStatusLog?.Invoke($"⏳ [GATE2] {symbol} {decision} 지연 진입 예약 (최대 {maxWaitMinutes}분 pullback 반등 대기)");

            _ = Task.Run(async () =>
            {
                try
                {
                    int maxAttempts = maxWaitMinutes;
                    for (int attempt = 0; attempt < maxAttempts; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), _cts?.Token ?? CancellationToken.None);
                        if (_cts?.IsCancellationRequested ?? false) break;

                        // 이미 포지션 있으면 취소
                        if (_activePositions.ContainsKey(symbol))
                        {
                            OnStatusLog?.Invoke($"⏹️ [GATE2] {symbol} 이미 포지션 보유 → 지연 진입 취소");
                            _pendingGate2.TryRemove(symbol, out _);
                            return;
                        }

                        // 현재 캔들 + 가격 조회
                        if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var cache))
                            continue;
                        if (!_marketDataManager.TickerCache.TryGetValue(symbol, out var ticker) || ticker.LastPrice <= 0)
                            continue;

                        List<IBinanceKline> candles;
                        lock (cache) { candles = cache.ToList(); }

                        // Gate 2 체크
                        if (IsPullbackRecoveryEntry(candles, ticker.LastPrice, out string gate2Reason))
                        {
                            OnStatusLog?.Invoke(
                                $"✅ [GATE2] {symbol} 지연 진입 조건 충족 → 진입 실행 ({gate2Reason})");
                            _pendingGate2.TryRemove(symbol, out _);
                            // [v5.10.82] AI Gate 통과 강제
                            await ExecuteAutoOrder(symbol, decision, ticker.LastPrice,
                                _cts.Token, signalSource + "_GATE2", skipAiGateCheck: false);
                            return;
                        }

                        // Gate 1 재체크 — 상황이 더 나빠졌으면 즉시 포기
                        if (IsAlreadyPumpedRecently(candles, ticker.LastPrice, out string gate1Reason))
                        {
                            // 여전히 고점권 → 대기 지속
                            continue;
                        }
                    }

                    // 만료
                    if (_pendingGate2.TryRemove(symbol, out _))
                    {
                        OnStatusLog?.Invoke($"⏰ [GATE2] {symbol} {maxWaitMinutes}분 내 pullback 반등 없음 → 지연 진입 포기");
                    }
                }
                catch (Exception ex)
                {
                    _pendingGate2.TryRemove(symbol, out _);
                    OnStatusLog?.Invoke($"⚠️ [GATE2] {symbol} 지연 진입 예외: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// [v4.9.9] MTF Guardian — 상위 시간봉 역방향 진입 차단
        /// 5분봉 명확한 하락장(1시간 -3% / SMA 역배열+소하락 / 12봉 9+하락+추가하락) 중 LONG 차단
        /// 5분봉 명확한 상승장 중 SHORT 차단
        /// </summary>
        // [AI 제거] ValidateSpikeWithAIAsync 본체 제거

        /// <summary>
        /// 데이터 부족 시 통과 (과차단 방지)
        /// </summary>
        private bool PassMtfGuardian(string symbol, string decision, out string reason)
        {
            reason = string.Empty;
            try
            {
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var cache))
                {
                    return true; // 캐시 없으면 통과
                }

                List<IBinanceKline> candles;
                lock (cache) { candles = cache.ToList(); }

                if (candles.Count < 24)
                {
                    return true; // 데이터 부족 시 통과
                }

                // 5분봉 기반 MTF 분석 (근래 1시간, 2시간)
                int n = candles.Count;
                decimal closeNow = candles[n - 1].ClosePrice;
                decimal close12Ago = candles[n - 12].ClosePrice;
                if (close12Ago <= 0 || closeNow <= 0) return true;

                float change1h = (float)((closeNow - close12Ago) / close12Ago * 100);

                // 최근 12봉 하락봉 카운트 (close<open)
                int bearish12 = 0;
                for (int i = n - 12; i < n; i++)
                    if (candles[i].ClosePrice < candles[i].OpenPrice) bearish12++;

                // SMA 계산 (정배열/역배열 판단)
                double sma20 = IndicatorCalculator.CalculateSMA(candles, 20);
                double sma50 = n >= 50 ? IndicatorCalculator.CalculateSMA(candles, 50) : sma20;
                double sma120 = n >= 120 ? IndicatorCalculator.CalculateSMA(candles, 120) : sma50;

                if (decision == "LONG")
                {
                    // [A] 명확한 하락 (1시간 누적 -3% 이상)
                    if (change1h < -3.0f)
                    {
                        reason = $"clearDowntrend 1h={change1h:F1}%";
                        return false;
                    }
                    // [B] 구조적 하락 (SMA 완전 역배열 + 추가 하락)
                    if (sma20 < sma50 && sma50 < sma120 && change1h < -0.5f)
                    {
                        reason = $"structuralDown sma20<50<120 1h={change1h:F1}%";
                        return false;
                    }
                    // [C] 모멘텀 하락 (12봉 중 9개 이상 하락 + 추가 하락)
                    if (bearish12 >= 9 && change1h < 0f)
                    {
                        reason = $"momentumDown bearish={bearish12}/12 1h={change1h:F1}%";
                        return false;
                    }
                    return true;
                }

                if (decision == "SHORT")
                {
                    // [A] 명확한 상승 (1시간 누적 +3% 이상)
                    if (change1h > 3.0f)
                    {
                        reason = $"clearUptrend 1h={change1h:F1}%";
                        return false;
                    }
                    // [B] 구조적 상승 (SMA 완전 정배열 + 추가 상승)
                    if (sma20 > sma50 && sma50 > sma120 && change1h > 0.5f)
                    {
                        reason = $"structuralUp sma20>50>120 1h={change1h:F1}%";
                        return false;
                    }
                    // [C] 모멘텀 상승 (12봉 중 9개 이상 상승 + 추가 상승)
                    int bullish12 = 12 - bearish12;
                    if (bullish12 >= 9 && change1h > 0f)
                    {
                        reason = $"momentumUp bullish={bullish12}/12 1h={change1h:F1}%";
                        return false;
                    }
                    return true;
                }

                return true;
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [MTF_GUARDIAN] {symbol} 오류 → 통과: {ex.Message}");
                return true;
            }
        }

        private static float CalculateMlTfBlendScore(float mlConfidence, float tfConfidence, bool tfPriority)
        {
            float safeMl = float.IsNaN(mlConfidence) || float.IsInfinity(mlConfidence)
                ? 0f
                : Math.Clamp(mlConfidence, 0f, 1f);
            float safeTf = float.IsNaN(tfConfidence) || float.IsInfinity(tfConfidence)
                ? 0f
                : Math.Clamp(tfConfidence, 0f, 1f);

            if (tfPriority)
                return safeMl * 0.30f + safeTf * 0.70f;

            return safeMl * 0.50f + safeTf * 0.50f;
        }

        private readonly record struct SymbolThresholdProfile(float EntryScoreCut, float AiConfidenceMin, float MaxRsiLimit);

        private static SymbolThresholdProfile GetThresholdByCoinType(CoinType coinType)
        {
            return coinType switch
            {
                CoinType.Major => new SymbolThresholdProfile(56.0f, 0.56f, 80.0f),
                CoinType.Pumping => new SymbolThresholdProfile(64.0f, 0.64f, 78.0f),
                _ => new SymbolThresholdProfile(72.0f, 0.72f, 70.0f)
            };
        }

        private static SymbolThresholdProfile GetThresholdBySymbol(string symbol)
        {
            // [v5.10.85] static 메서드 → 설정창 직접 조회
            var src = AppConfig.Current?.Trading?.Symbols ?? new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
            bool isMajor = !string.IsNullOrWhiteSpace(symbol) && src.Contains(symbol, StringComparer.OrdinalIgnoreCase);

            if (isMajor)
            {
                return GetThresholdByCoinType(CoinType.Major);
            }

            return GetThresholdByCoinType(CoinType.Normal);
        }

        private static SymbolThresholdProfile GetThresholdProfile(string symbol, string signalSource)
        {
            CoinType coinType = ResolveCoinType(symbol, signalSource);
            return GetThresholdByCoinType(coinType);
        }

        private async Task<(bool isPositiveSlope, double slope)> EvaluateFifteenMinuteSlopeAsync(string symbol, CancellationToken token)
        {
            try
            {
                var klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 30, token);
                if (klines == null || klines.Count < 21)
                    return (false, 0d);

                var candles = klines.ToList();
                double sma20Now = IndicatorCalculator.CalculateSMA(candles, 20);
                double sma20Prev = IndicatorCalculator.CalculateSMA(candles.Take(candles.Count - 1).ToList(), 20);

                if (sma20Now <= 0 || sma20Prev <= 0)
                    return (false, 0d);

                double slope = sma20Now - sma20Prev;
                return (slope > 0d, slope);
            }
            catch
            {
                return (false, 0d);
            }
        }

        private void ScheduleAiDoubleCheckLabeling(string symbol, decimal entryPrice, bool isLong, string? decisionId, CancellationToken token)
        {
            // [AI 제거] AI DoubleCheck 레이블링 통째 제거
        }

        /// <summary>
        /// [v5.10.38] PUMP 슬롯이 빈 즉시 우선순위 큐에서 최고 점수 신호 진입 시도
        private async Task TryProcessPumpPriorityQueueAsync()
        {
            // 슬롯 여유 확인
            int pumpCount, maxPump;
            lock (_posLock)
            {
                pumpCount = _activePositions.Count(p => p.Value.IsOwnPosition && !MajorSymbols.Contains(p.Key));
                maxPump = GetDynamicMaxPumpSlots();
            }
            if (pumpCount >= maxPump) return;

            PumpPriorityCandidate? best = null;
            lock (_pumpPriorityLock)
            {
                _pumpPriorityQueue.RemoveAll(e => (DateTime.Now - e.RegisteredAt).TotalMinutes >= 30);
                _pumpPriorityQueue.RemoveAll(e =>
                {
                    if (_marketDataManager.TickerCache.TryGetValue(e.Symbol, out var t) && t.LastPrice > 0 && e.OriginPrice > 0)
                        return (t.LastPrice - e.OriginPrice) / e.OriginPrice * 100m >= 2.0m;
                    return false;
                });
                if (_pumpPriorityQueue.Count > 0)
                {
                    best = _pumpPriorityQueue[0];
                    _pumpPriorityQueue.RemoveAt(0);
                }
            }

            if (best == null) return;

            if (!_marketDataManager.TickerCache.TryGetValue(best.Symbol, out var ticker) || ticker.LastPrice <= 0)
            {
                OnStatusLog?.Invoke($"⛔ [우선순위큐] {best.Symbol} 현재가 조회 실패 → 폐기");
                return;
            }

            decimal priceRise = best.OriginPrice > 0
                ? (ticker.LastPrice - best.OriginPrice) / best.OriginPrice * 100m
                : 0m;
            if (priceRise >= 2.0m)
            {
                OnStatusLog?.Invoke($"⛔ [우선순위큐] {best.Symbol} +{priceRise:F1}% 상승 → 고점 진입 차단");
                return;
            }

            int waitMin = (int)(DateTime.Now - best.RegisteredAt).TotalMinutes;
            OnStatusLog?.Invoke($"🏆 [우선순위큐] {best.Symbol} 진입 시도 (score={best.BlendedScore:P0}, {waitMin}분 대기)");
            var token = _cts?.Token ?? CancellationToken.None;
            await ExecuteAutoOrder(best.Symbol, best.Decision, ticker.LastPrice, token,
                best.SignalSource, skipAiGateCheck: best.BlendedScore > 0f);
        }

        /// [v5.10.18] PositionSyncService.OnPositionClosed 핸들러
        /// 거래소 폴링으로 포지션 청산 감지 시 호출 — 쿨다운/DB/알림/AI 레이블 처리
        /// </summary>
        private void HandleSyncedPositionClosed(
            string symbol, decimal entryPrice, decimal exitPrice, bool isProfit, string reason)
        {
            // 1. 쿨다운 (재진입 방지 5분)
            _stopLossCooldown[symbol] = DateTime.Now.AddMinutes(5);
            OnStatusLog?.Invoke($"⏳ [COOLDOWN] {symbol} 5분 쿨다운 시작 (재진입 방지) — {reason}");

            // 2. 로컬 포지션 정보 확인
            PositionInfo? pos = null;
            lock (_posLock) { _activePositions.TryGetValue(symbol, out pos); }
            // PositionSyncService가 이미 제거했지만 혹시 남아있으면 제거
            lock (_posLock) { _activePositions.Remove(symbol); }

            // [v5.12.0] 급등 슬롯 해제 — 청산 확정 시
            if (_activeSpikeSlot.TryRemove(symbol, out var releasedSlot))
            {
                OnStatusLog?.Invoke($"🔓 [SPIKE_SLOT] {symbol}({releasedSlot.source}) 해제: 청산 ({reason})");
            }

            // [v5.10.48] 외부 청산 시 잔여 TP/SL 오더 취소 (마진 잠금 방지)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _exchangeService.CancelAllOrdersAsync(symbol, _cts?.Token ?? CancellationToken.None);
                    OnStatusLog?.Invoke($"✅ [SYNC_CLOSE] {symbol} 잔여 오더 취소 완료");
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [SYNC_CLOSE] {symbol} 오더 취소 실패: {ex.Message}");
                }
            }, CancellationToken.None);

            // [v5.10.38] 슬롯 해방 → 대기 중인 최우선 PUMP 신호 즉시 처리
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await TryProcessPumpPriorityQueueAsync();
            }, CancellationToken.None);

            // 3. UI 이벤트
            OnPositionStatusUpdate?.Invoke(symbol, false, 0);
            OnTickerUpdate?.Invoke(symbol, 0m, 0d);
            OnTradeHistoryUpdated?.Invoke();

            // 4. DB TradeLog 저장
            _ = Task.Run(async () =>
            {
                try
                {
                    if (pos != null && pos.EntryPrice > 0 && exitPrice > 0)
                    {
                        decimal priceDiff = pos.IsLong ? (exitPrice - entryPrice) : (entryPrice - exitPrice);
                        decimal pnl = priceDiff * Math.Abs(pos.Quantity);
                        decimal leverage = pos.Leverage > 0 ? pos.Leverage : 1m;
                        decimal pnlPct = entryPrice > 0
                            ? (priceDiff / entryPrice) * leverage * 100m
                            : 0m;

                        var tradeLog = new TradingBot.Shared.Models.TradeLog(
                            symbol,
                            pos.IsLong ? "SELL" : "BUY",
                            "SYNC_CLOSE",
                            exitPrice,
                            pos.AiScore,
                            DateTime.Now,
                            pnl,
                            pnlPct)
                        {
                            EntryPrice = entryPrice,
                            ExitPrice = exitPrice,
                            Quantity = Math.Abs(pos.Quantity),
                            EntryTime = pos.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = reason
                        };
                        await _dbManager.SaveTradeLogAsync(tradeLog);
                    }
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [PositionSync] {symbol} DB 저장 실패: {ex.Message}");
                }
            });

            // 5. 텔레그램 알림 — 마크다운 V1 이탤릭 충돌 방지 (reason 내 언더스코어를 공백으로)
            _ = Task.Run(async () =>
            {
                try
                {
                    string emoji = isProfit ? "✅" : "❌";
                    decimal roe = entryPrice > 0
                        ? ((pos?.IsLong == true ? exitPrice - entryPrice : entryPrice - exitPrice)
                            / entryPrice) * (pos?.Leverage ?? 1m) * 100m
                        : 0m;
                    string telegramReason = (reason ?? "CLOSE").Replace("_", " ");
                    await TelegramService.Instance.SendMessageAsync(
                        $"{emoji} *[{telegramReason}]*\n`{symbol}`\n진입가: `{entryPrice:F4}` → 청산가: `{exitPrice:F4}`\nROE: `{roe:+0.0;-0.0}%`\n⏰ {DateTime.Now:HH:mm:ss}",
                        TelegramMessageType.Profit);
                }
                catch (Exception tEx)
                {
                    OnStatusLog?.Invoke($"⚠️ [텔레그램 전송실패] {symbol} SYNC_CLOSE: {tEx.Message}");
                }
            });

            // 7. AI 레이블링
            if (pos != null)
            {
                double profitPct = entryPrice > 0
                    ? (double)((pos.IsLong ? exitPrice - entryPrice : entryPrice - exitPrice)
                        / entryPrice * (pos.Leverage) * 100m)
                    : 0d;
                _ = HandleAiCloseLabelingAsync(symbol, pos.EntryTime, entryPrice, pos.IsLong, (decimal)profitPct, reason);
            }
        }

        private async Task HandleAiCloseLabelingAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            bool isLong,
            decimal actualProfitPct,
            string closeReason)
        {
            // [AI 제거] AI DoubleCheck 청산 라벨 통째 제거
            await Task.CompletedTask;
            try { RemoveActiveAiDecisionId(symbol); } catch { }
        }

        // [AI 제거] PersistAiLabeledSampleToDbAsync 제거

        private void SetActiveAiDecisionId(string symbol, string decisionId)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(decisionId))
                return;

            _activeAiDecisionIds[symbol] = new ActiveDecisionIdState
            {
                DecisionId = decisionId,
                SavedAtUtc = DateTime.UtcNow
            };
            PersistActiveAiDecisionIds();
        }

        private void RemoveActiveAiDecisionId(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            _activeAiDecisionIds.TryRemove(symbol, out _);
            PersistActiveAiDecisionIds();
        }

        private void LoadActiveAiDecisionIds()
        {
            try
            {
                if (!File.Exists(_activeAiDecisionIdsPath))
                    return;

                string json = File.ReadAllText(_activeAiDecisionIdsPath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ActiveDecisionIdState>>(json);

                if (loaded == null || loaded.Count == 0)
                {
                    // 레거시 포맷 호환: symbol -> decisionId
                    var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (legacy != null && legacy.Count > 0)
                    {
                        loaded = legacy.ToDictionary(
                            k => k.Key,
                            v => new ActiveDecisionIdState
                            {
                                DecisionId = v.Value,
                                SavedAtUtc = DateTime.UtcNow
                            },
                            StringComparer.OrdinalIgnoreCase);
                    }
                }

                if (loaded == null || loaded.Count == 0)
                    return;

                foreach (var pair in loaded)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)
                        || pair.Value == null
                        || string.IsNullOrWhiteSpace(pair.Value.DecisionId))
                    {
                        continue;
                    }

                    if (pair.Value.SavedAtUtc == default)
                        pair.Value.SavedAtUtc = DateTime.UtcNow;

                    _activeAiDecisionIds[pair.Key] = pair.Value;
                }

                int pruned = PruneExpiredActiveAiDecisionIdsCore();
                if (pruned > 0)
                {
                    PersistActiveAiDecisionIds();
                    OnStatusLog?.Invoke($"🧹 [AI DoubleCheck] 만료 ActiveDecisionId 정리: {pruned}개");
                }

                OnStatusLog?.Invoke($"🧠 [AI DoubleCheck] ActiveDecisionId 복원: {_activeAiDecisionIds.Count}개");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AI DoubleCheck] ActiveDecisionId 복원 실패: {ex.Message}");
            }
        }

        private void PersistActiveAiDecisionIds()
        {
            try
            {
                PruneExpiredActiveAiDecisionIdsCore();

                string? dir = Path.GetDirectoryName(_activeAiDecisionIdsPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var snapshot = _activeAiDecisionIds.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_activeAiDecisionIdsPath, json);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ [AI DoubleCheck] ActiveDecisionId 저장 실패: {ex.Message}");
            }
        }

        private int PruneExpiredActiveAiDecisionIdsCore()
        {
            DateTime cutoffUtc = DateTime.UtcNow.AddHours(-ActiveDecisionIdRetentionHours);
            int removed = 0;

            foreach (var pair in _activeAiDecisionIds)
            {
                DateTime savedAt = pair.Value?.SavedAtUtc ?? DateTime.MinValue;
                if (savedAt != DateTime.MinValue && savedAt < cutoffUtc)
                {
                    if (_activeAiDecisionIds.TryRemove(pair.Key, out _))
                        removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// [AI 제거] EntryDecisionRecord 타입 제거됨 → 라벨 통계 항상 0 반환
        /// </summary>
        private (int total, int labeled, int markToMarket, int tradeClose) GetRecentLabelStatsFromFiles(int maxFiles = 10)
        {
            return (0, 0, 0, 0);
        }

        // [GATE 제거] 재설계 예정
        private bool ShouldApplyFifteenMinuteWaveGate(string signalSource)
        {
            return false; // GATE 영구 비활성화
        }


        private (float mlThreshold, float transformerThreshold) GetSymbolGateThresholds(string symbol)
        {
            string key = string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.ToUpperInvariant();

            var state = _symbolGateThresholds.GetOrAdd(key, _ => new GateThresholdState(_fifteenMinuteMlMinConfidence, _fifteenMinuteTransformerMinConfidence));
            lock (state.SyncRoot)
            {
                return (state.MlThreshold, state.TransformerThreshold);
            }
        }

        // [GATE 제거] 재설계 전까지 사용되지 않음
        private void RecordGateDecisionAndAutoTune(
            string symbol,
            bool allowEntry,
            float mlThreshold,
            float transformerThreshold,
            float mlConfidence,
            float transformerConfidence)
        {
            string key = string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.ToUpperInvariant();

            var state = _symbolGateThresholds.GetOrAdd(key, _ => new GateThresholdState(mlThreshold, transformerThreshold));

            float beforeMl;
            float beforeTf;
            float afterMl;
            float afterTf;
            float passRate;
            int sampled;
            int passCount;
            int blockCount;
            bool thresholdChanged;

            lock (state.SyncRoot)
            {
                state.MlThreshold = Math.Clamp(state.MlThreshold, GateMlThresholdMin, GateMlThresholdMax);
                state.TransformerThreshold = Math.Clamp(state.TransformerThreshold, GateTransformerThresholdMin, GateTransformerThresholdMax);

                state.SampleCount++;
                if (allowEntry)
                    state.PassCount++;
                else
                    state.BlockCount++;

                if (state.SampleCount < GateAutoTuneSampleSize)
                    return;

                sampled = state.SampleCount;
                passCount = state.PassCount;
                blockCount = state.BlockCount;
                passRate = sampled > 0 ? (float)passCount / sampled : 0f;

                beforeMl = state.MlThreshold;
                beforeTf = state.TransformerThreshold;

                // [동적 최적화] 3단계 조정 시스템
                if (passRate >= GateTightenPassRate)
                {
                    // 통과율 높음 (≥55%) → 기준 강화
                    state.MlThreshold = Math.Clamp(state.MlThreshold + GateThresholdAdjustStep, GateMlThresholdMin, GateMlThresholdMax);
                    state.TransformerThreshold = Math.Clamp(state.TransformerThreshold + GateThresholdAdjustStep, GateTransformerThresholdMin, GateTransformerThresholdMax);
                }
                else if (passRate <= GateLoosenPassRate)
                {
                    // 통과율 낮음 (≤30%) → 기준 대폭 완화
                    state.MlThreshold = Math.Clamp(state.MlThreshold - GateThresholdAdjustStep, GateMlThresholdMin, GateMlThresholdMax);
                    state.TransformerThreshold = Math.Clamp(state.TransformerThreshold - GateThresholdAdjustStep, GateTransformerThresholdMin, GateTransformerThresholdMax);

                    // [추가 완화] 거의 통과할 뻔한 경우 (95% 이상) 추가 조정
                    if (!allowEntry && mlConfidence > 0f && mlConfidence >= state.MlThreshold * 0.95f)
                        state.MlThreshold = Math.Clamp(state.MlThreshold - GateThresholdAdjustStep / 2f, GateMlThresholdMin, GateMlThresholdMax);

                    if (!allowEntry && transformerConfidence > 0f && transformerConfidence >= state.TransformerThreshold * 0.95f)
                        state.TransformerThreshold = Math.Clamp(state.TransformerThreshold - GateThresholdAdjustStep / 2f, GateTransformerThresholdMin, GateTransformerThresholdMax);
                }
                else if (passRate >= 0.40f && passRate < 0.50f)
                {
                    // [동적 최적화] 중간 범위(40-50%) → 약간 완화하여 최적점 탐색
                    float subtleAdjust = GateThresholdAdjustStep * 0.5f;
                    state.MlThreshold = Math.Clamp(state.MlThreshold - subtleAdjust, GateMlThresholdMin, GateMlThresholdMax);
                    state.TransformerThreshold = Math.Clamp(state.TransformerThreshold - subtleAdjust, GateTransformerThresholdMin, GateTransformerThresholdMax);
                }

                afterMl = state.MlThreshold;
                afterTf = state.TransformerThreshold;
                thresholdChanged = Math.Abs(afterMl - beforeMl) > 0.0001f || Math.Abs(afterTf - beforeTf) > 0.0001f;

                state.SampleCount = 0;
                state.PassCount = 0;
                state.BlockCount = 0;
            }

            string action = thresholdChanged ? "ADJUST" : "HOLD";
            OnStatusLog?.Invoke(
                $"📈 [GATE][AUTO_TUNE] sym={key} action={action} sample={sampled} pass={passCount} block={blockCount} passRate={passRate:P1} mlThr={beforeMl:P1}->{afterMl:P1} tfThr={beforeTf:P1}->{afterTf:P1}");
        }

        // [GATE 제거] 재설계 전까지 사용되지 않음
        private async Task<(bool allowEntry, string reason, decimal takeProfitPrice, decimal stopLossPrice, string scenario, float mlConfidence, float transformerConfidence)> EvaluateFifteenMinuteWaveGateAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CandleData latestCandle,
            float mlThreshold,
            float transformerThreshold,
            CancellationToken token)
        {
            if (!string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "decision!=LONG/SHORT", 0m, 0m, "NONE", 0f, 0f);
            }

            var klines15mRaw = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 260, token);
            if (klines15mRaw == null || klines15mRaw.Count < 130)
                return (false, "15m캔들 부족(<130)", 0m, 0m, "NONE", 0f, 0f);

            var klines15m = klines15mRaw.ToList();
            var klines1h = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneHour, 220, token))?.ToList() ?? new List<IBinanceKline>();
            var klines4h = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour, 220, token))?.ToList() ?? new List<IBinanceKline>();

            int h1Bias = CalculateTrendBias(klines1h);
            int h4Bias = CalculateTrendBias(klines4h);
            bool conflictingTopDown = h1Bias != 0 && h4Bias != 0 && h1Bias != h4Bias;
            bool longTopDown = !conflictingTopDown && (h1Bias > 0 || h4Bias > 0);
            bool shortTopDown = !conflictingTopDown && (h1Bias < 0 || h4Bias < 0);

            bool continuationPass = false;
            bool reversalPass = false;
            decimal continuationTp = 0m;
            decimal continuationSl = 0m;
            decimal reversalTp = 0m;
            decimal reversalSl = 0m;
            string continuationReason = string.Empty;
            string reversalReason = string.Empty;

            if (string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
            {
                if (longTopDown)
                    continuationPass = TryEvaluateWave2LongContinuation(klines15m, currentPrice, out continuationSl, out continuationTp, out continuationReason);
                else
                    continuationReason = "상위 추세(1h/4h) LONG 불일치";

                if (shortTopDown)
                    reversalPass = TryEvaluateWave5LongReversal(klines15m, out reversalSl, out reversalTp, out reversalReason);
                else
                    reversalReason = "상위 추세(1h/4h) SHORT 불일치";
            }
            else
            {
                if (shortTopDown)
                    continuationPass = TryEvaluateWave2ShortContinuation(klines15m, currentPrice, out continuationSl, out continuationTp, out continuationReason);
                else
                    continuationReason = "상위 추세(1h/4h) SHORT 불일치";

                if (longTopDown)
                    reversalPass = TryEvaluateWave5ShortReversal(klines15m, out reversalSl, out reversalTp, out reversalReason);
                else
                    reversalReason = "상위 추세(1h/4h) LONG 불일치";
            }

            decimal finalTp = 0m;
            decimal finalSl = 0m;
            string scenario;

            if (continuationPass)
            {
                scenario = "TREND_CONTINUATION";
                finalTp = continuationTp;
                finalSl = continuationSl;
            }
            else if (reversalPass)
            {
                scenario = "WAVE5_REVERSAL";
                finalTp = reversalTp;
                finalSl = reversalSl;
            }
            else
            {
                string structureReason = $"structureFail cont={continuationReason} rev={reversalReason} topdown=1h:{h1Bias},4h:{h4Bias}";
                return (false, structureReason, 0m, 0m, "NONE", 0f, 0f);
            }

            // [AI 제거] AIPredictor / Transformer 체크 통째 제거 → 구조 분석만으로 통과
            return (true,
                $"✅ AI 제거 — 구조분석만 사용 | topdown=1h:{h1Bias},4h:{h4Bias} structure={scenario}",
                finalTp,
                finalSl,
                scenario,
                0f,
                1f);

            /* TensorFlow 전환 완료 후 복원 예정
            if (_transformerTrainer == null || !_transformerTrainer.IsModelReady)
                return (false, "Transformer 모델 미준비", finalTp, finalSl, scenario, mlDirectionalProb, 0f);

            var inferenceSeries = BuildInferenceDataFromKlines(klines15m, symbol);
            if (inferenceSeries.Count < _transformerTrainer.SeqLen)
                return (false, $"Transformer 시퀀스 부족({inferenceSeries.Count}<{_transformerTrainer.SeqLen})", finalTp, finalSl, scenario, mlDirectionalProb, 0f);

            var sequence = inferenceSeries.TakeLast(_transformerTrainer.SeqLen).ToList();
            float predictedPrice = _transformerTrainer.Predict(sequence);
            decimal predictedPriceDecimal = (decimal)predictedPrice;
            bool transformerAligned = string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase)
                ? predictedPriceDecimal > currentPrice
                : predictedPriceDecimal < currentPrice;

            float transformerConfidence = 0.5f;
            if (currentPrice > 0)
            {
                decimal movePct = Math.Abs((predictedPriceDecimal - currentPrice) / currentPrice) * 100m;
                // [FIX] 기준값 0.35% → 2.0%로 완화 (더 넓은 범위)
                decimal rawConfidence = Math.Min(1m, movePct / 2.0m);
                transformerConfidence = NormalizeProbability01((float)rawConfidence, 0.5f);
                
                // [GATE 제거] TF_DEBUG 로그 제거
                // TODO: 재설계 시 복원
            }

            // 하이브리드: 신뢰도 60% 이상이면 방향 무시, 미만은 방향 일치 필요
            const float transformerHighConfThreshold = 0.60f;
            bool transformerPass = transformerConfidence >= transformerThreshold && (transformerAligned || transformerConfidence >= transformerHighConfThreshold);
            if (!transformerPass)
            {
                string transRejectReason = transformerConfidence < transformerThreshold
                    ? $"Transformer 신뢰도부족 conf={transformerConfidence:P1}<{transformerThreshold:P1}"
                    : $"Transformer 방향불일치 aligned={transformerAligned} conf={transformerConfidence:P1}<{transformerHighConfThreshold:P1}";
                return (false,
                    transRejectReason,
                    finalTp,
                    finalSl,
                    scenario,
                    mlDirectionalProb,
                    transformerConfidence);
            }

            return (true,
                $"topdown=1h:{h1Bias},4h:{h4Bias} structure={scenario}",
                finalTp,
                finalSl,
                scenario,
                mlDirectionalProb,
                transformerConfidence);
            */
        }

        private static int CalculateTrendBias(List<IBinanceKline> klines)
        {
            if (klines == null || klines.Count < 200)
                return 0;

            double sma50 = IndicatorCalculator.CalculateSMA(klines, 50);
            double sma200 = IndicatorCalculator.CalculateSMA(klines, 200);
            decimal close = klines[^1].ClosePrice;

            if (sma50 > 0 && sma200 > 0)
            {
                if ((double)close > sma50 && sma50 > sma200)
                    return 1;

                if ((double)close < sma50 && sma50 < sma200)
                    return -1;

                if (sma50 > sma200 && (double)close > sma200)
                    return 1;

                if (sma50 < sma200 && (double)close < sma200)
                    return -1;
            }

            return 0;
        }

        private bool TryEvaluateWave2LongContinuation(
            List<IBinanceKline> klines,
            decimal currentPrice,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 60)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(72, klines.Count)).ToList();
            int wave1PeakIdx = IndexOfHighestHigh(window, 8, window.Count - 6);
            if (wave1PeakIdx < 8)
            {
                reason = "wave1 고점 미탐지";
                return false;
            }

            int wave1StartIdx = IndexOfLowestLow(window, 0, wave1PeakIdx - 1);
            int wave2LowIdx = IndexOfLowestLow(window, wave1PeakIdx + 1, window.Count - 1);
            if (wave1StartIdx < 0 || wave2LowIdx < 0 || wave2LowIdx <= wave1PeakIdx)
            {
                reason = "wave1/wave2 구조 미완성";
                return false;
            }

            decimal wave1Start = window[wave1StartIdx].LowPrice;
            decimal wave1Peak = window[wave1PeakIdx].HighPrice;
            decimal wave2Low = window[wave2LowIdx].LowPrice;
            decimal wave1Length = wave1Peak - wave1Start;
            if (wave1Length <= 0m)
            {
                reason = "wave1 길이 비정상";
                return false;
            }

            decimal retrace = (wave1Peak - wave2Low) / wave1Length;
            if (wave2Low <= wave1Start)
            {
                reason = "Rule1 위반(2파가 1파 시작 하회)";
                return false;
            }

            if (retrace < 0.48m || retrace > 0.68m)
            {
                reason = $"피보 되돌림 이탈({retrace:P1})";
                return false;
            }

            decimal fib50 = wave1Peak - wave1Length * 0.5m;
            decimal fib618 = wave1Peak - wave1Length * 0.618m;
            decimal distance618 = currentPrice > 0 ? Math.Abs(currentPrice - fib618) / currentPrice : 1m;
            decimal distance50 = currentPrice > 0 ? Math.Abs(currentPrice - fib50) / currentPrice : 1m;

            var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
            bool nearLowerBand = bb.Lower > 0 && currentPrice <= (decimal)bb.Lower * 1.01m;

            decimal wave1Volume = CalculateAverageVolumeInRange(window, wave1StartIdx, wave1PeakIdx);
            decimal wave2Volume = CalculateAverageVolumeInRange(window, wave1PeakIdx + 1, wave2LowIdx);
            bool volumeContracting = wave1Volume > 0m && wave2Volume > 0m && wave2Volume <= wave1Volume * 0.92m;
            bool fibTouch = distance618 <= 0.006m || distance50 <= 0.006m;

            if (!fibTouch && !nearLowerBand && !volumeContracting)
            {
                reason = "진입 트리거 미충족(피보/BB/거래량)";
                return false;
            }

            stopLossPrice = wave1Start * 0.998m;
            takeProfitPrice = wave1Peak + wave1Length * 1.0m;
            reason = $"retrace={retrace:P1} fibTouch={fibTouch} bbLower={nearLowerBand} volContract={volumeContracting}";
            return true;
        }

        private bool TryEvaluateWave2ShortContinuation(
            List<IBinanceKline> klines,
            decimal currentPrice,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 60)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(72, klines.Count)).ToList();
            int wave1LowIdx = IndexOfLowestLow(window, 8, window.Count - 6);
            if (wave1LowIdx < 8)
            {
                reason = "wave1 저점 미탐지";
                return false;
            }

            int wave1StartIdx = IndexOfHighestHigh(window, 0, wave1LowIdx - 1);
            int wave2HighIdx = IndexOfHighestHigh(window, wave1LowIdx + 1, window.Count - 1);
            if (wave1StartIdx < 0 || wave2HighIdx < 0 || wave2HighIdx <= wave1LowIdx)
            {
                reason = "wave1/wave2 구조 미완성";
                return false;
            }

            decimal wave1Start = window[wave1StartIdx].HighPrice;
            decimal wave1Low = window[wave1LowIdx].LowPrice;
            decimal wave2High = window[wave2HighIdx].HighPrice;
            decimal wave1Length = wave1Start - wave1Low;
            if (wave1Length <= 0m)
            {
                reason = "wave1 길이 비정상";
                return false;
            }

            decimal retrace = (wave2High - wave1Low) / wave1Length;
            if (wave2High >= wave1Start)
            {
                reason = "Rule1 위반(2파가 1파 시작 상회)";
                return false;
            }

            if (retrace < 0.48m || retrace > 0.68m)
            {
                reason = $"피보 되돌림 이탈({retrace:P1})";
                return false;
            }

            decimal fib50 = wave1Low + wave1Length * 0.5m;
            decimal fib618 = wave1Low + wave1Length * 0.618m;
            decimal distance618 = currentPrice > 0 ? Math.Abs(currentPrice - fib618) / currentPrice : 1m;
            decimal distance50 = currentPrice > 0 ? Math.Abs(currentPrice - fib50) / currentPrice : 1m;

            var bb = IndicatorCalculator.CalculateBB(window, 20, 2);
            bool nearUpperBand = bb.Upper > 0 && currentPrice >= (decimal)bb.Upper * 0.99m;

            decimal wave1Volume = CalculateAverageVolumeInRange(window, wave1StartIdx, wave1LowIdx);
            decimal wave2Volume = CalculateAverageVolumeInRange(window, wave1LowIdx + 1, wave2HighIdx);
            bool volumeContracting = wave1Volume > 0m && wave2Volume > 0m && wave2Volume <= wave1Volume * 0.92m;
            bool fibTouch = distance618 <= 0.006m || distance50 <= 0.006m;

            if (!fibTouch && !nearUpperBand && !volumeContracting)
            {
                reason = "진입 트리거 미충족(피보/BB/거래량)";
                return false;
            }

            stopLossPrice = wave1Start * 1.002m;
            takeProfitPrice = wave1Low - wave1Length * 1.0m;
            reason = $"retrace={retrace:P1} fibTouch={fibTouch} bbUpper={nearUpperBand} volContract={volumeContracting}";
            return true;
        }

        private bool TryEvaluateWave5LongReversal(
            List<IBinanceKline> klines,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 90)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(96, klines.Count)).ToList();
            int n = window.Count;
            int midStart = n / 3;
            int midEnd = (n * 2) / 3;
            int lateStart = (n * 2) / 3;

            int wave1LowIdx = IndexOfLowestLow(window, 5, midStart - 5);
            int wave3LowIdx = IndexOfLowestLow(window, midStart, midEnd);
            int wave5LowIdx = IndexOfLowestLow(window, lateStart, n - 2);

            if (wave1LowIdx < 0 || wave3LowIdx < 0 || wave5LowIdx < 0 || !(wave1LowIdx < wave3LowIdx && wave3LowIdx < wave5LowIdx))
            {
                reason = "5파 저점 구조 미완성";
                return false;
            }

            int wave1StartHighIdx = IndexOfHighestHigh(window, 0, wave1LowIdx);
            int wave2HighIdx = IndexOfHighestHigh(window, wave1LowIdx + 1, wave3LowIdx - 1);
            int wave4HighIdx = IndexOfHighestHigh(window, wave3LowIdx + 1, wave5LowIdx - 1);
            if (wave1StartHighIdx < 0 || wave2HighIdx < 0 || wave4HighIdx < 0)
            {
                reason = "보조 파동 고점 미탐지";
                return false;
            }

            decimal wave1StartHigh = window[wave1StartHighIdx].HighPrice;
            decimal wave1Low = window[wave1LowIdx].LowPrice;
            decimal wave2High = window[wave2HighIdx].HighPrice;
            decimal wave3Low = window[wave3LowIdx].LowPrice;
            decimal wave4High = window[wave4HighIdx].HighPrice;
            decimal wave5Low = window[wave5LowIdx].LowPrice;

            decimal len1 = wave1StartHigh - wave1Low;
            decimal len3 = wave2High - wave3Low;
            decimal len5 = wave4High - wave5Low;
            if (len1 <= 0m || len3 <= 0m || len5 <= 0m)
            {
                reason = "파동 길이 계산 실패";
                return false;
            }

            bool rule2Pass = len3 >= Math.Min(len1, len5) * 0.95m;
            bool rule4Pass = wave4High < wave1Low;
            float rsi3 = CalculateRsiAtIndex(window, wave3LowIdx, 14);
            float rsi5 = CalculateRsiAtIndex(window, wave5LowIdx, 14);
            bool rsiDivergence = wave5Low < wave3Low && rsi5 > rsi3;
            decimal vol3 = CalculateAverageVolumeAround(window, wave3LowIdx, 2);
            decimal vol5 = CalculateAverageVolumeAround(window, wave5LowIdx, 2);
            bool volumeDivergence = vol5 > 0m && vol3 > 0m && vol5 < vol3 * 0.90m;

            if (!rule2Pass || !rule4Pass || !rsiDivergence || !volumeDivergence)
            {
                reason = $"rule2={rule2Pass} rule4={rule4Pass} rsiDiv={rsiDivergence} volDiv={volumeDivergence}";
                return false;
            }

            stopLossPrice = wave5Low * 0.997m;
            takeProfitPrice = wave4High;
            reason = $"rule2/rule4 통과, rsi3={rsi3:F1}, rsi5={rsi5:F1}";
            return true;
        }

        private bool TryEvaluateWave5ShortReversal(
            List<IBinanceKline> klines,
            out decimal stopLossPrice,
            out decimal takeProfitPrice,
            out string reason)
        {
            stopLossPrice = 0m;
            takeProfitPrice = 0m;
            reason = string.Empty;

            if (klines == null || klines.Count < 90)
            {
                reason = "15m캔들 부족";
                return false;
            }

            var window = klines.TakeLast(Math.Min(96, klines.Count)).ToList();
            int n = window.Count;
            int midStart = n / 3;
            int midEnd = (n * 2) / 3;
            int lateStart = (n * 2) / 3;

            int wave1HighIdx = IndexOfHighestHigh(window, 5, midStart - 5);
            int wave3HighIdx = IndexOfHighestHigh(window, midStart, midEnd);
            int wave5HighIdx = IndexOfHighestHigh(window, lateStart, n - 2);

            if (wave1HighIdx < 0 || wave3HighIdx < 0 || wave5HighIdx < 0 || !(wave1HighIdx < wave3HighIdx && wave3HighIdx < wave5HighIdx))
            {
                reason = "5파 고점 구조 미완성";
                return false;
            }

            int wave1StartLowIdx = IndexOfLowestLow(window, 0, wave1HighIdx);
            int wave2LowIdx = IndexOfLowestLow(window, wave1HighIdx + 1, wave3HighIdx - 1);
            int wave4LowIdx = IndexOfLowestLow(window, wave3HighIdx + 1, wave5HighIdx - 1);
            if (wave1StartLowIdx < 0 || wave2LowIdx < 0 || wave4LowIdx < 0)
            {
                reason = "보조 파동 저점 미탐지";
                return false;
            }

            decimal wave1StartLow = window[wave1StartLowIdx].LowPrice;
            decimal wave1High = window[wave1HighIdx].HighPrice;
            decimal wave2Low = window[wave2LowIdx].LowPrice;
            decimal wave3High = window[wave3HighIdx].HighPrice;
            decimal wave4Low = window[wave4LowIdx].LowPrice;
            decimal wave5High = window[wave5HighIdx].HighPrice;

            decimal len1 = wave1High - wave1StartLow;
            decimal len3 = wave3High - wave2Low;
            decimal len5 = wave5High - wave4Low;
            if (len1 <= 0m || len3 <= 0m || len5 <= 0m)
            {
                reason = "파동 길이 계산 실패";
                return false;
            }

            bool rule2Pass = len3 >= Math.Min(len1, len5) * 0.95m;
            bool rule4Pass = wave4Low > wave1High;
            float rsi3 = CalculateRsiAtIndex(window, wave3HighIdx, 14);
            float rsi5 = CalculateRsiAtIndex(window, wave5HighIdx, 14);
            bool rsiDivergence = wave5High > wave3High && rsi5 < rsi3;
            decimal vol3 = CalculateAverageVolumeAround(window, wave3HighIdx, 2);
            decimal vol5 = CalculateAverageVolumeAround(window, wave5HighIdx, 2);
            bool volumeDivergence = vol5 > 0m && vol3 > 0m && vol5 < vol3 * 0.90m;

            if (!rule2Pass || !rule4Pass || !rsiDivergence || !volumeDivergence)
            {
                reason = $"rule2={rule2Pass} rule4={rule4Pass} rsiDiv={rsiDivergence} volDiv={volumeDivergence}";
                return false;
            }

            stopLossPrice = wave5High * 1.003m;
            takeProfitPrice = wave4Low;
            reason = $"rule2/rule4 통과, rsi3={rsi3:F1}, rsi5={rsi5:F1}";
            return true;
        }

        private static int IndexOfLowestLow(List<IBinanceKline> klines, int startIndex, int endIndex)
        {
            if (klines == null || klines.Count == 0)
                return -1;

            int start = Math.Max(0, startIndex);
            int end = Math.Min(endIndex, klines.Count - 1);
            if (start > end)
                return -1;

            decimal min = decimal.MaxValue;
            int index = -1;
            for (int i = start; i <= end; i++)
            {
                if (klines[i].LowPrice < min)
                {
                    min = klines[i].LowPrice;
                    index = i;
                }
            }

            return index;
        }

        private static int IndexOfHighestHigh(List<IBinanceKline> klines, int startIndex, int endIndex)
        {
            if (klines == null || klines.Count == 0)
                return -1;

            int start = Math.Max(0, startIndex);
            int end = Math.Min(endIndex, klines.Count - 1);
            if (start > end)
                return -1;

            decimal max = decimal.MinValue;
            int index = -1;
            for (int i = start; i <= end; i++)
            {
                if (klines[i].HighPrice > max)
                {
                    max = klines[i].HighPrice;
                    index = i;
                }
            }

            return index;
        }

        private static decimal CalculateAverageVolumeInRange(List<IBinanceKline> klines, int startIndex, int endIndex)
        {
            if (klines == null || klines.Count == 0)
                return 0m;

            int start = Math.Max(0, startIndex);
            int end = Math.Min(endIndex, klines.Count - 1);
            if (start > end)
                return 0m;

            decimal sum = 0m;
            int count = 0;
            for (int i = start; i <= end; i++)
            {
                sum += klines[i].Volume;
                count++;
            }

            return count > 0 ? sum / count : 0m;
        }

        private static decimal CalculateAverageVolumeAround(List<IBinanceKline> klines, int centerIndex, int radius)
        {
            if (klines == null || klines.Count == 0)
                return 0m;

            int start = Math.Max(0, centerIndex - radius);
            int end = Math.Min(klines.Count - 1, centerIndex + radius);
            return CalculateAverageVolumeInRange(klines, start, end);
        }

        private static float CalculateRsiAtIndex(List<IBinanceKline> klines, int index, int period)
        {
            if (klines == null || klines.Count == 0)
                return 50f;

            int safeIndex = Math.Clamp(index, 0, klines.Count - 1);
            if (safeIndex < period)
                return 50f;

            var subset = klines.Take(safeIndex + 1).ToList();
            return (float)IndicatorCalculator.CalculateRSI(subset, period);
        }

        private List<CandleData> BuildInferenceDataFromKlines(List<IBinanceKline> klines, string symbol)
        {
            var result = new List<CandleData>();
            if (klines == null || klines.Count < 130)
                return result;

            var volumes = klines.Select(k => (float)k.Volume).ToList();

            for (int i = 120; i < klines.Count; i++)
            {
                var subset = klines.GetRange(0, i + 1);
                var current = klines[i];
                decimal entryPrice = current.ClosePrice;

                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                double sma20 = IndicatorCalculator.CalculateSMA(subset, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(subset, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(subset, 120);

                double bbMid = (bb.Upper + bb.Lower) / 2.0;
                float bbWidth = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0;
                float priceToBBMid = bbMid > 0 ? (float)(((double)entryPrice - bbMid) / bbMid * 100) : 0;

                float priceChangePct = current.OpenPrice > 0
                    ? (float)((entryPrice - current.OpenPrice) / current.OpenPrice * 100)
                    : 0;
                float priceToSMA20Pct = sma20 > 0
                    ? (float)(((double)entryPrice - sma20) / sma20 * 100)
                    : 0;

                decimal range = current.HighPrice - current.LowPrice;
                float bodyRatio = range > 0 ? (float)(Math.Abs(current.ClosePrice - current.OpenPrice) / range) : 0;
                float upperShadow = range > 0 ? (float)((current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice)) / range) : 0;
                float lowerShadow = range > 0 ? (float)((Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice) / range) : 0;

                float vol20Avg = 0;
                if (i >= 20)
                {
                    for (int v = i - 19; v <= i; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[i] / vol20Avg : 1;
                float volumeChangePct = (i > 0 && volumes[i - 1] > 0)
                    ? (volumes[i] - volumes[i - 1]) / volumes[i - 1] * 100
                    : 0;

                float fibPosition = 0;
                if (fib.Level236 != fib.Level618 && fib.Level618 > 0)
                    fibPosition = (float)(((double)entryPrice - fib.Level236) / (fib.Level618 - fib.Level236));
                fibPosition = Math.Clamp(fibPosition, 0, 1);

                float trendStrength = 0;
                if (sma20 > 0 && sma60 > 0 && sma120 > 0)
                {
                    if (sma20 > sma60 && sma60 > sma120) trendStrength = 1.0f;
                    else if (sma20 < sma60 && sma60 < sma120) trendStrength = -1.0f;
                    else trendStrength = (float)((sma20 - sma120) / sma120);
                    trendStrength = Math.Clamp(trendStrength, -1f, 1f);
                }

                float rsiDivergence = 0;
                if (i >= 5)
                {
                    var prevSubset = klines.GetRange(0, i - 4);
                    var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                    float priceDelta = (float)(current.ClosePrice - klines[i - 5].ClosePrice);
                    float rsiDelta = (float)(rsi - prevRsi);
                    if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;
                    else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1;
                }

                bool elliottBullish = IndicatorCalculator.AnalyzeElliottWave(subset);
                float elliottState = elliottBullish ? 1.0f : -1.0f;

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,
                    Price_Change_Pct = priceChangePct,
                    Price_To_BB_Mid = priceToBBMid,
                    BB_Width = bbWidth,
                    Price_To_SMA20_Pct = priceToSMA20Pct,
                    Candle_Body_Ratio = bodyRatio,
                    Upper_Shadow_Ratio = upperShadow,
                    Lower_Shadow_Ratio = lowerShadow,
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct,
                    Fib_Position = fibPosition,
                    Trend_Strength = trendStrength,
                    RSI_Divergence = rsiDivergence,
                    ElliottWaveState = elliottState,
                    SentimentScore = 0,
                    OpenInterest = _oiCollector != null ? (float)_oiCollector.GetOiAtTime(symbol, current.OpenTime) : 0,
                    OI_Change_Pct = _oiCollector != null ? (float)_oiCollector.GetOiChangeAtTime(symbol, current.OpenTime) : 0,
                    FundingRate = 0,
                    SqueezeLabel = 0,
                    Label = false,
                    LabelLong = 0,
                    LabelShort = 0,
                    LabelHold = 0
                });
            }

            return result;
        }

        private static float NormalizeProbability01(float value, float fallback = 0.5f)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return Math.Clamp(fallback, 0f, 1f);

            return Math.Clamp(value, 0f, 1f);
        }

        private static string BuildPredictionValidationKey(string modelName, string symbol)
        {
            string safeModel = string.IsNullOrWhiteSpace(modelName)
                ? "MODEL"
                : modelName.Trim().ToUpperInvariant().Replace(".", "").Replace(" ", "");
            string safeSymbol = string.IsNullOrWhiteSpace(symbol)
                ? "UNKNOWN"
                : symbol.Trim().ToUpperInvariant();

            return $"{safeModel}_{safeSymbol}";
        }

        private bool TrySchedulePredictionValidation(string predictionKey, string symbol, CancellationTokenSource? cts)
        {
            if (string.IsNullOrWhiteSpace(predictionKey) || cts == null || cts.Token.IsCancellationRequested)
                return false;

            if (!_scheduledPredictionValidations.TryAdd(predictionKey, 0))
                return false;

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PredictionValidationDelay, token);
                    await _predictionValidationLimiter.WaitAsync(token);
                    try
                    {
                        await ValidatePredictionAsync(predictionKey, symbol, token);
                    }
                    finally
                    {
                        _predictionValidationLimiter.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [PREDICT][VALIDATE][ERROR] key={predictionKey} sym={symbol} detail={ex.Message}");
                }
                finally
                {
                    _scheduledPredictionValidations.TryRemove(predictionKey, out _);
                }
            }, CancellationToken.None);

            return true;
        }

        // [v5.22.17] NormalizePumpSignalLog 제거 — PumpScanStrategy 폐기 후 dead code.
        //   호출자 0건이지만 컴파일된 dll 에 "[SIGNAL][PUMP][TRACE]" / "[SIGNAL][PUMP]" string literal 잔존
        //   → 사용자가 dll grep 시 PumpScan 코드가 살아있는 줄 오해.
        //   메서드 자체 삭제로 dll 바이너리에서도 잔존 문자열 제거.

        private static string NormalizeTransformerSignalLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "📡 [SIGNAL][TRANSFORMER][TRACE] empty";

            if (message.Contains("[SIGNAL][TRANSFORMER]", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[ENTRY][", StringComparison.OrdinalIgnoreCase)
                || message.Contains("[DB][", StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }

            return $"📡 [SIGNAL][TRANSFORMER][TRACE] {message}";
        }

        private readonly record struct BandwidthGateResult(
            bool Blocked,
            bool IsSqueezed,
            bool IsExpanding,
            bool IsDirectionalBreakout,
            bool IsSqueezeBreakout,
            decimal CurrentBandwidthPct,
            decimal AverageBandwidthPct,
            decimal PreviousBandwidthPct,
            decimal SqueezeRatio,
            double AiBonusPoints,
            string Reason);

        private BandwidthGateResult EvaluateBandwidthGate(
            string symbol,
            string decision,
            string signalSource,
            decimal currentPrice,
            CandleData? latestCandle,
            List<IBinanceKline>? recentKlines)
        {
            if (latestCandle == null || recentKlines == null || recentKlines.Count < 30 || currentPrice <= 0m)
            {
                return new BandwidthGateResult(
                    Blocked: false,
                    IsSqueezed: false,
                    IsExpanding: false,
                    IsDirectionalBreakout: false,
                    IsSqueezeBreakout: false,
                    CurrentBandwidthPct: 0m,
                    AverageBandwidthPct: 0m,
                    PreviousBandwidthPct: 0m,
                    SqueezeRatio: 1m,
                    AiBonusPoints: 0d,
                    Reason: "BW_DATA_INSUFFICIENT");
            }

            decimal currentBwPct = latestCandle.BB_Width > 0
                ? (decimal)latestCandle.BB_Width
                : CalculateBollingerWidthPct(recentKlines);

            decimal averageBwPct = CalculateAverageBollingerWidthPct(recentKlines, period: 20, sampleCount: 100);
            decimal previousBwPct = CalculateBollingerWidthPct(recentKlines.Take(Math.Max(0, recentKlines.Count - 1)).ToList());

            if (currentBwPct <= 0m || averageBwPct <= 0m)
            {
                return new BandwidthGateResult(
                    Blocked: false,
                    IsSqueezed: false,
                    IsExpanding: false,
                    IsDirectionalBreakout: false,
                    IsSqueezeBreakout: false,
                    CurrentBandwidthPct: currentBwPct,
                    AverageBandwidthPct: averageBwPct,
                    PreviousBandwidthPct: previousBwPct,
                    SqueezeRatio: 1m,
                    AiBonusPoints: 0d,
                    Reason: "BW_NOT_AVAILABLE");
            }

            decimal squeezeRatio = currentBwPct / averageBwPct;
            CoinType coinType = ResolveCoinType(symbol, signalSource);
            bool isPumping = coinType == CoinType.Pumping;
            bool isMajor = coinType == CoinType.Major;

            decimal squeezeDetectRatio = isPumping ? 0.90m : 0.60m;
            decimal hardBlockRatio = isPumping ? 0.12m : 0.20m;
            decimal softBlockRatio = isPumping ? 0.20m : 0.30m;

            bool isSqueezed = squeezeRatio <= squeezeDetectRatio;
            bool isExpanding = previousBwPct > 0m
                ? currentBwPct > previousBwPct * 1.03m
                : currentBwPct > averageBwPct * 0.95m;

            decimal bbUpper = latestCandle.BollingerUpper > 0f ? (decimal)latestCandle.BollingerUpper : 0m;
            decimal bbLower = latestCandle.BollingerLower > 0f ? (decimal)latestCandle.BollingerLower : 0m;

            bool isDirectionalBreakout =
                (string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase) && bbUpper > 0m && currentPrice >= bbUpper)
                || (string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase) && bbLower > 0m && currentPrice <= bbLower);

            bool isSqueezeBreakout = isSqueezed && isExpanding && isDirectionalBreakout;
            bool hasVolumeBypass = latestCandle.Volume_Ratio >= 2.0f;

            bool blocked = false;
            string reason = "BANDWIDTH_OK";

            if (squeezeRatio < hardBlockRatio && !isExpanding && !(isPumping && hasVolumeBypass))
            {
                blocked = true;
                reason = $"BANDWIDTH_HARD_BLOCK_ratio={squeezeRatio:P0}_expanding={isExpanding}_vol={latestCandle.Volume_Ratio:F2}x";
            }
            else if (squeezeRatio < softBlockRatio && !isExpanding && !isPumping)
            {
                blocked = true;
                reason = $"BANDWIDTH_WAIT_ratio={squeezeRatio:P0}_expanding={isExpanding}";
            }

            double aiBonusPoints = 0d;
            if (isPumping)
            {
                bool isBurst300 = squeezeRatio >= 3.0m;
                if (isSqueezeBreakout || isBurst300)
                    aiBonusPoints = Math.Max(aiBonusPoints, 30d);

                if (hasVolumeBypass)
                    aiBonusPoints = Math.Max(aiBonusPoints, 15d);
            }
            else if (isSqueezeBreakout)
            {
                aiBonusPoints = 20d;
            }

            if (isMajor && aiBonusPoints > 20d)
                aiBonusPoints = 20d;

            return new BandwidthGateResult(
                Blocked: blocked,
                IsSqueezed: isSqueezed,
                IsExpanding: isExpanding,
                IsDirectionalBreakout: isDirectionalBreakout,
                IsSqueezeBreakout: isSqueezeBreakout,
                CurrentBandwidthPct: currentBwPct,
                AverageBandwidthPct: averageBwPct,
                PreviousBandwidthPct: previousBwPct,
                SqueezeRatio: squeezeRatio,
                AiBonusPoints: aiBonusPoints,
                Reason: reason);
        }

        private decimal CalculateBollingerWidthPct(List<IBinanceKline> candles, int period = 20)
        {
            try
            {
                if (candles == null || candles.Count < period)
                    return 0m;

                var window = candles.TakeLast(period).ToList();
                var bb = IndicatorCalculator.CalculateBB(window, period, 2);

                decimal mid = (decimal)bb.Mid;
                decimal upper = (decimal)bb.Upper;
                decimal lower = (decimal)bb.Lower;

                if (mid <= 0m || upper <= lower)
                    return 0m;

                return ((upper - lower) / mid) * 100m;
            }
            catch
            {
                return 0m;
            }
        }

        private bool ShouldBlockChasingEntry(
            string symbol,
            string decision,
            decimal currentPrice,
            CandleData latestCandle,
            List<IBinanceKline>? recentKlines,
            string mode,
            out string reason)
        {
            reason = string.Empty;

            if (decision != "LONG" && decision != "SHORT")
                return false;

            if (string.Equals(mode, "SIDEWAYS", StringComparison.OrdinalIgnoreCase))
                return false;

            if (recentKlines == null || recentKlines.Count < 20)
                return false;

            var recent20 = recentKlines.TakeLast(20).ToList();
            decimal recentHigh = recent20.Max(k => k.HighPrice);
            decimal recentLow = recent20.Min(k => k.LowPrice);
            decimal sma20 = latestCandle.SMA_20 > 0 ? (decimal)latestCandle.SMA_20 : 0m;
            decimal bbLower = latestCandle.BollingerLower > 0 ? (decimal)latestCandle.BollingerLower : 0m;
            decimal bbUpper = latestCandle.BollingerUpper > 0 ? (decimal)latestCandle.BollingerUpper : 0m;
            decimal bbRange = bbUpper - bbLower;
            decimal bbPosition = bbRange > 0m ? (currentPrice - bbLower) / bbRange : 0m;
            decimal currentBbWidthPct = latestCandle.BB_Width > 0
                ? (decimal)latestCandle.BB_Width
                : 0m;
            decimal averageBbWidthPct = CalculateAverageBollingerWidthPct(recentKlines);

            decimal priceToSma20Pct = sma20 > 0
                ? ((currentPrice - sma20) / sma20) * 100m
                : (decimal)latestCandle.Price_To_SMA20_Pct;

            int riskScore = 0;
            var flags = new List<string>();
            bool bbFilterPassed = false;  // ← BB 필터 통과 여부 추적

            if (decision == "LONG")
            {
                decimal pullbackFromHighPct = recentHigh > 0
                    ? ((recentHigh - currentPrice) / recentHigh) * 100m
                    : 999m;

                bool nearRecentHigh = pullbackFromHighPct <= 0.20m;
                bool stretchedAboveSma20 = priceToSma20Pct >= 0.80m;
                bool rsiHot = latestCandle.RSI >= 64f;
                bool touchingUpperBand = bbUpper > 0 && currentPrice >= bbUpper * 0.998m;
                bool upperShadowWarning = latestCandle.Upper_Shadow_Ratio >= 0.35f;
                // ── 볼린저 밴드 추세/과열 구분 로직 (v2.4.12) ──
                bool inUpperZone = bbRange > 0m && bbPosition >= 0.85m;
                // 밴드 폭 확산(Squeeze→Expansion): 현재 폭이 평균 대비 10% 이상 넓어지는 중
                bool isBbExpansion = averageBbWidthPct > 0m
                    && currentBbWidthPct > 0m
                    && currentBbWidthPct > averageBbWidthPct * 1.10m;

                bool strongBreakoutException = latestCandle.Volume_Ratio >= 1.8f
                    && latestCandle.OI_Change_Pct >= 0.8f
                    && latestCandle.Upper_Shadow_Ratio < 0.20f
                    && latestCandle.RSI < 70f;

                // ⑤ [Staircase Pursuit] 계단식 상승 감지: Higher Lows(3봉) + BB 중단 위 + RSI<80 → nearRecentHigh 차단 면제
                bool isStaircasePursuit = IsStaircaseUptrendPattern(recent20, bbPosition, latestCandle);
                if (isStaircasePursuit && latestCandle.RSI < 80f)
                {
                    OnStatusLog?.Invoke($"🪜 [Staircase Pursuit] {symbol} 계단식 상승 패턴 → 고점 추격 필터 우회 (%B={bbPosition:P0}, RSI={latestCandle.RSI:F1})");
                    bbFilterPassed = true;
                }

                // ① 초과열(RSI ≥ 80): 상단 구간에서 RSI 80 이상은 상투 가능성 → 차단
                if (inUpperZone && latestCandle.RSI >= 80f)
                {
                    reason = $"BB 상단 초과열 차단 (BB위치 {bbPosition:P0}, RSI {latestCandle.RSI:F1} ≥ 80)";
                    return true;
                }

                // ② 상단 구간 + 밴드 확산 중(Squeeze 탈출) → 강력한 발산 신호, 진입 승인
                if (inUpperZone && isBbExpansion)
                {
                    OnStatusLog?.Invoke($"🚀 [BB 필터 통과] {symbol} 상단 {bbPosition:P0}이지만 밴드 확산 중 (현재폭 {currentBbWidthPct:F2}% > 평균폭 {averageBbWidthPct:F2}% ×1.1) → 추세 발산 진입 승인");
                    bbFilterPassed = true;  // ← riskScore 무시
                }
                // ③ 상단 구간이지만 RSI < 70(미과열) → 추세의 시작으로 판단, 진입 승인
                else if (inUpperZone && latestCandle.RSI < 70f)
                {
                    OnStatusLog?.Invoke($"🚀 [BB 필터 통과] {symbol} 상단 {bbPosition:P0}이지만 RSI {latestCandle.RSI:F1} < 70 → 추세 시작 진입 승인");
                    bbFilterPassed = true;  // ← riskScore 무시
                }
                // ④ 상단 구간 + RSI 70~80 + 밴드 미확산 → 과열 추격 차단
                else if (inUpperZone && !strongBreakoutException)
                {
                    reason = $"BB 상단 과열 (BB위치 {bbPosition:P0}, RSI {latestCandle.RSI:F1}, 밴드 미확산) → 차단";
                    return true;
                }

                if (!bbFilterPassed)  // ← BB 필터 미통과일 때만 riskScore 계산
                {
                    if (nearRecentHigh)
                    {
                        riskScore += 2;
                        flags.Add($"최근고점 근접({recentHigh:F4}, 되돌림 {pullbackFromHighPct:F2}%)");
                    }

                    if (stretchedAboveSma20)
                    {
                        riskScore += 1;
                        flags.Add($"SMA20 이격 +{priceToSma20Pct:F2}%");
                    }

                    if (rsiHot)
                    {
                        riskScore += 1;
                        flags.Add($"RSI {latestCandle.RSI:F1}");
                    }

                    if (touchingUpperBand)
                    {
                        riskScore += 1;
                        flags.Add("BB 상단 근접");
                    }

                    if (upperShadowWarning)
                    {
                        riskScore += 1;
                        flags.Add($"윗꼬리 {latestCandle.Upper_Shadow_Ratio * 100f:F0}%");
                    }
                }
            }
            else
            {
                decimal bounceFromLowPct = recentLow > 0
                    ? ((currentPrice - recentLow) / recentLow) * 100m
                    : 999m;

                bool nearRecentLow = bounceFromLowPct <= 0.20m;
                bool stretchedBelowSma20 = priceToSma20Pct <= -0.80m;
                bool rsiCold = latestCandle.RSI <= 36f;
                bool touchingLowerBand = bbLower > 0 && currentPrice <= bbLower * 1.002m;
                bool lowerShadowWarning = latestCandle.Lower_Shadow_Ratio >= 0.35f;
                // ── 볼린저 밴드 추세/과냉 구분 로직 (v2.4.12, SHORT 대칭) ──
                bool inLowerZone = bbRange > 0m && bbPosition <= 0.15m;
                bool isBbExpansionShort = averageBbWidthPct > 0m
                    && currentBbWidthPct > 0m
                    && currentBbWidthPct > averageBbWidthPct * 1.10m;

                bool strongBreakdownException = latestCandle.Volume_Ratio >= 1.8f
                    && latestCandle.OI_Change_Pct >= 0.8f
                    && latestCandle.Lower_Shadow_Ratio < 0.20f
                    && latestCandle.RSI > 30f;

                // ① 초과냉(RSI ≤ 20): 하단 구간에서 RSI 20 이하는 과매도 반등 위험 → 차단
                if (inLowerZone && latestCandle.RSI <= 20f)
                {
                    reason = $"BB 하단 초과냉 차단 (BB위치 {bbPosition:P0}, RSI {latestCandle.RSI:F1} ≤ 20)";
                    return true;
                }

                // ② 하단 구간 + 밴드 확산 중(Squeeze 탈출) → 강력한 하락 발산 신호, 진입 승인
                if (inLowerZone && isBbExpansionShort)
                {
                    OnStatusLog?.Invoke($"🚀 [BB 필터 통과] {symbol} 하단 {bbPosition:P0}이지만 밴드 확산 중 (현재폭 {currentBbWidthPct:F2}% > 평균폭 {averageBbWidthPct:F2}% ×1.1) → 추세 발산 진입 승인");
                    bbFilterPassed = true;  // ← riskScore 무시
                }
                // ③ 하단 구간이지만 RSI > 30(미과매도) → 하락 추세 시작으로 판단, 진입 승인
                else if (inLowerZone && latestCandle.RSI > 30f)
                {
                    OnStatusLog?.Invoke($"🚀 [BB 필터 통과] {symbol} 하단 {bbPosition:P0}이지만 RSI {latestCandle.RSI:F1} > 30 → 추세 시작 진입 승인");
                    bbFilterPassed = true;  // ← riskScore 무시
                }
                // ④ 하단 구간 + RSI 20~30 + 밴드 미확산 → 과매도 추격 차단
                else if (inLowerZone && !strongBreakdownException)
                {
                    reason = $"BB 하단 과매도 (BB위치 {bbPosition:P0}, RSI {latestCandle.RSI:F1}, 밴드 미확산) → 차단";
                    return true;
                }

                if (!bbFilterPassed)  // ← BB 필터 미통과일 때만 riskScore 계산
                {
                    if (nearRecentLow)
                    {
                        riskScore += 2;
                        flags.Add($"최근저점 근접({recentLow:F4}, 반등 {bounceFromLowPct:F2}%)");
                    }

                    if (stretchedBelowSma20)
                    {
                        riskScore += 1;
                        flags.Add($"SMA20 이격 {priceToSma20Pct:F2}%");
                    }

                    if (rsiCold)
                    {
                        riskScore += 1;
                        flags.Add($"RSI {latestCandle.RSI:F1}");
                    }

                    if (touchingLowerBand)
                    {
                        riskScore += 1;
                        flags.Add("BB 하단 근접");
                    }

                    if (lowerShadowWarning)
                    {
                        riskScore += 1;
                        flags.Add($"아랫꼬리 {latestCandle.Lower_Shadow_Ratio * 100f:F0}%");
                    }
                }
            }

            // [수정] BB 필터 미통과한 경우에만 riskScore로 차단
            if (!bbFilterPassed && riskScore >= 4)
            {
                reason = string.Join(", ", flags);
                return true;
            }

            return false;
        }

        /// <summary>[Staircase Pursuit] recent 봉 리스트 기준으로 3연속 저점 상승 + BB 중단 이상 여부를 판단합니다.</summary>
        private static bool IsStaircaseUptrendPattern(
            List<IBinanceKline> recentKlines,
            decimal bbPosition,
            CandleData latestCandle)
        {
            if (recentKlines == null || recentKlines.Count < 4) return false;
            // BB 중단 위에 있어야 함 (%B > 0.45)
            if (bbPosition < 0.45m) return false;
            // RSI 과열(≥80) 제외
            if (latestCandle.RSI >= 80f) return false;
            // 최근 4봉에서 3연속 Higher Lows 확인
            var tail = recentKlines.TakeLast(4).ToList();
            for (int i = 1; i < tail.Count; i++)
                if (tail[i].LowPrice <= tail[i - 1].LowPrice) return false;
            return true;
        }

        private async Task<(bool blocked, string reason)> EvaluateOneMinuteUpperWickBlockAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            string signalSource,
            CandleData? latestCandle,
            CancellationToken token)
        {
            if (!string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
                return (false, string.Empty);

            if (currentPrice <= 0m)
                return (false, string.Empty);

            if (signalSource.Contains("PUMP", StringComparison.OrdinalIgnoreCase)
                || signalSource.Contains("MEME", StringComparison.OrdinalIgnoreCase))
            {
                return (false, string.Empty);
            }

            if (latestCandle != null && latestCandle.RSI > 0f && latestCandle.RSI < 62f)
                return (false, string.Empty);

            try
            {
                var oneMinuteCandles = (await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneMinute, 4, token))?.ToList();
                if (oneMinuteCandles == null || oneMinuteCandles.Count == 0)
                    return (false, string.Empty);

                int closedIndex = oneMinuteCandles.Count >= 2 ? oneMinuteCandles.Count - 2 : oneMinuteCandles.Count - 1;
                var closedCandle = oneMinuteCandles[closedIndex];
                decimal highPrice = closedCandle.HighPrice;
                decimal lowPrice = closedCandle.LowPrice;
                decimal openPrice = closedCandle.OpenPrice;
                decimal closePrice = closedCandle.ClosePrice;

                if (highPrice <= 0m || lowPrice <= 0m || closePrice <= 0m || highPrice <= lowPrice)
                    return (false, string.Empty);

                decimal range = highPrice - lowPrice;
                decimal upperWick = highPrice - Math.Max(openPrice, closePrice);
                decimal upperWickRatio = range > 0m ? upperWick / range : 0m;
                decimal candleRangePct = closePrice > 0m ? (range / closePrice) * 100m : 0m;
                decimal pullbackPct = ((highPrice - currentPrice) / highPrice) * 100m;

                bool hasMeaningfulUpperWick = upperWickRatio >= 0.45m;
                bool hasMeaningfulRange = candleRangePct >= 0.12m;
                bool hasRejectionPullback = pullbackPct >= 0.30m;
                bool bearishOrWeakClose = closePrice <= openPrice;

                if (hasMeaningfulUpperWick && hasMeaningfulRange && hasRejectionPullback && bearishOrWeakClose)
                {
                    return (true,
                        $"1m reject high={highPrice:F8} open={openPrice:F8} close={closePrice:F8} current={currentPrice:F8} " +
                        $"wick={upperWickRatio:P0} range={candleRangePct:F2}% pullback={pullbackPct:F2}%");
                }

                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ {symbol} 1분봉 윗꼬리 필터 조회 실패: {ex.Message}");
                return (false, string.Empty);
            }
        }

        private decimal CalculateAverageBollingerWidthPct(List<IBinanceKline> recentKlines, int period = 20, int sampleCount = 10)
        {
            try
            {
                if (recentKlines == null || recentKlines.Count < period)
                    return 0m;

                int availableSamples = Math.Min(sampleCount, recentKlines.Count - period + 1);
                if (availableSamples <= 0)
                    return 0m;

                var widths = new List<decimal>(availableSamples);
                int startIndex = recentKlines.Count - (period + availableSamples - 1);
                if (startIndex < 0)
                    startIndex = 0;

                for (int offset = startIndex; offset <= recentKlines.Count - period; offset++)
                {
                    var subset = recentKlines.Skip(offset).Take(period).ToList();
                    // [FIX] subset이 비어있으면 건너뛰기
                    if (!subset.Any())
                    {
                        OnStatusLog?.Invoke($"⚠️ subset 데이터 부족 (offset: {offset}, period: {period})");
                        continue;
                    }
                    if (subset.Count < period)
                        continue;

                    var bb = IndicatorCalculator.CalculateBB(subset, period, 2);
                    decimal mid = (decimal)bb.Mid;
                    decimal upper = (decimal)bb.Upper;
                    decimal lower = (decimal)bb.Lower;

                    if (mid <= 0m || upper <= lower)
                        continue;

                    widths.Add(((upper - lower) / mid) * 100m);
                }

                return widths.Count > 0 ? widths.Average() : 0m;
            }
            catch
            {
                return 0m;
            }
        }

        private bool IsEntryWarmupActive(out TimeSpan remaining)
        {
            // [수정] _engineStartTime이 MinValue인 경우 (엔진 미초기화) 워밍업 활성화
            if (_engineStartTime == DateTime.MinValue)
            {
                remaining = _entryWarmupDuration;
                return true;  // 미초기화 상태면 워밍업 중으로 간주
            }

            var elapsed = DateTime.Now - _engineStartTime;
            remaining = _entryWarmupDuration - elapsed;
            return remaining > TimeSpan.Zero;
        }

        // [v5.22.16] InitTradeSignalClassifierAsync 통째 삭제 (TradeSignalClassifier 의존 제거됨, 호출자 0건)

        /// <summary>
        /// [v3.0.11] 정찰대(30%) 진입 후 ROE 기반으로 메인(70%) 추가 진입 모니터
        /// ROE +5%: 즉시(2분 후), +2%: 5분 후, 0~2%: 10분 후, 음수: 추가 안 함, 15분 경과: 포기
        /// </summary>
        private async Task MonitorScoutToMainUpgradeAsync(string symbol, string decision, decimal scoutEntryPrice, decimal leverage, CancellationToken token)
        {
            const int CheckIntervalMs = 5000;  // 5초마다 체크
            const int MaxWaitMinutes = 15;     // 최대 대기 15분
            const int MinWaitSeconds = 120;    // 최소 대기 2분

            DateTime scoutTime = DateTime.Now;
            OnStatusLog?.Invoke($"🛰️ [Scout→Main] {symbol} {decision} 정찰대 진입 완료 → 메인 전환 모니터 시작 (최대 {MaxWaitMinutes}분)");

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(CheckIntervalMs, token);

                var elapsed = DateTime.Now - scoutTime;
                if (elapsed.TotalMinutes >= MaxWaitMinutes)
                {
                    OnStatusLog?.Invoke($"⏰ [Scout→Main] {symbol} {MaxWaitMinutes}분 경과 → 메인 전환 포기 (정찰대만 유지)");
                    return;
                }

                // 포지션 확인
                PositionInfo? pos;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out pos);
                }

                if (pos == null || pos.Quantity <= 0)
                {
                    OnStatusLog?.Invoke($"ℹ️ [Scout→Main] {symbol} 포지션 없음 → 메인 전환 종료");
                    return;
                }

                // 현재 ROE 계산
                decimal currentPrice = 0m;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tick))
                    currentPrice = tick.LastPrice;

                if (currentPrice <= 0 || pos.EntryPrice <= 0)
                    continue;

                decimal priceDiff = pos.IsLong
                    ? (currentPrice - pos.EntryPrice)
                    : (pos.EntryPrice - currentPrice);
                decimal currentROE = (priceDiff / pos.EntryPrice) * leverage * 100m;

                // ROE 기반 메인 전환 판단
                decimal mainSizeMultiplier;
                int requiredWaitSeconds;

                // [v3.1.9] 분할 진입: ROE + 거래량 동반 확인 후 본대 투입
                if (currentROE >= 10.0m)
                {
                    mainSizeMultiplier = 0.75m; // 75% 추가 (확실한 발산)
                    requiredWaitSeconds = MinWaitSeconds;
                }
                else if (currentROE >= 5.0m)
                {
                    mainSizeMultiplier = 0.50m; // 50% 추가 (방향 확인)
                    requiredWaitSeconds = 180; // 3분
                }
                else if (currentROE >= 2.0m)
                {
                    mainSizeMultiplier = 0.30m; // 30% 추가 (약한 방향)
                    requiredWaitSeconds = 300; // 5분
                }
                else
                {
                    // ROE 2% 미만: 추가하지 않음, 대기 계속
                    continue;
                }

                if (elapsed.TotalSeconds < requiredWaitSeconds)
                    continue;

                // 슬롯 여유 확인
                bool canAddMain;
                lock (_posLock)
                {
                    int totalPositions = _activePositions.Count;
                    canAddMain = totalPositions < MAX_TOTAL_SLOTS;
                }

                if (!canAddMain)
                {
                    OnStatusLog?.Invoke($"⚠️ [Scout→Main] {symbol} 슬롯 포화 → 메인 추가 불가");
                    return;
                }

                // 메인 진입 실행
                OnStatusLog?.Invoke($"🚀 [Scout→Main] {symbol} {decision} 메인 전환! ROE={currentROE:F1}% 경과={elapsed.TotalMinutes:F1}분 → 사이즈 {mainSizeMultiplier:P0} 추가");

                try
                {
                    await ExecuteAutoOrder(
                        symbol, decision, currentPrice, token,
                        signalSource: "SCOUT_TO_MAIN",
                        manualSizeMultiplier: mainSizeMultiplier);
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [Scout→Main] {symbol} 메인 추가 실패: {ex.Message}");
                }

                return; // 1회만 실행
            }
        }

        private async Task PreloadInitialAiScoresAsync(CancellationToken token)
        {
            // [AI 제거] AIPredictor 의존 통째 제거
            await Task.CompletedTask;
        }

        /// <summary>
        /// 수동 진입: 사용자가 직접 심볼/방향을 지정하여 시장가 진입
        /// AI 게이트를 우회하고 즉시 주문 실행
        /// </summary>
        public async Task<(bool success, string message)> ManualEntryAsync(
            string symbol, string direction, CancellationToken token = default)
        {
            if (_exchangeService == null)
                return (false, "거래소 서비스가 초기화되지 않았습니다.");

            try
            {
                var settings = MainWindow.CurrentGeneralSettings ?? AppConfig.Current?.Trading?.GeneralSettings ?? _settings;
                int leverage = settings.DefaultLeverage;
                decimal marginUsdt = settings.DefaultMargin;

                // 1. 현재가 조회
                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                if (currentPrice <= 0)
                    return (false, $"{symbol} 현재가 조회 실패");

                // 2. 레버리지 설정
                await _exchangeService.SetLeverageAsync(symbol, leverage, token);

                // 3. 수량 계산
                decimal quantity = (marginUsdt * leverage) / currentPrice;

                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
                var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData != null)
                {
                    decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                }

                if (quantity <= 0)
                    return (false, $"{symbol} 수량 계산 결과 0 (증거금={marginUsdt:F0} USDT, 레버리지={leverage}x)");

                // 4. 시장가 주문
                string side = direction == "LONG" ? "BUY" : "SELL";
                OnStatusLog?.Invoke($"⚡ [수동진입] {symbol} {direction} 주문 실행 | 수량={quantity} 레버={leverage}x 증거금=${marginUsdt:N0}");

                // [v5.10.98 P1-1] 수동 진입도 단일 진입점 PlaceEntryOrderAsync 사용 (LIMIT + chasing 차단 + race mark)
                //   기존: PlaceMarketOrderAsync 직접 호출 → 슬리피지 + 게이트 우회
                //   수정: PlaceEntryOrderAsync 경유 → IsEntryAllowed + LIMIT + chasing block 자동 적용
                var (success, filledQty, avgPrice) = await PlaceEntryOrderAsync(symbol, side, quantity, "MANUAL_ENTRY", token);

                if (!success || filledQty <= 0)
                    return (false, $"{symbol} 주문 실패 (거래소 응답 확인 또는 게이트 차단)");

                // 5. 포지션 등록
                bool isLongPos = direction == "LONG";
                lock (_posLock)
                {
                    _activePositions[symbol] = new PositionInfo
                    {
                        Symbol = symbol,
                        Side = isLongPos ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell,
                        IsLong = isLongPos,
                        EntryPrice = avgPrice,
                        Quantity = filledQty,
                        InitialQuantity = filledQty,
                        EntryTime = DateTime.Now,
                        Leverage = leverage,
                        EntryZoneTag = "MANUAL",
                        IsOwnPosition = true
                    };
                }

                OnTradeExecuted?.Invoke(symbol, direction, avgPrice, filledQty);
                OnAlert?.Invoke($"⚡ [수동진입] {symbol} {direction} 체결 | 가격={avgPrice:F8} 수량={filledQty} 레버={leverage}x");

                // [v5.10.98 P1-1] 수동 진입 SL/TP/Trailing 즉시 등록 + 모니터 시작 (안전망)
                try
                {
                    await RegisterProtectionOrdersAsync(symbol, isLongPos, filledQty, avgPrice, leverage, "MANUAL_ENTRY", token);
                }
                catch (Exception protEx) { OnStatusLog?.Invoke($"⚠️ [수동진입] {symbol} 보호주문 등록 예외: {protEx.Message}"); }

                // 모니터 시작 (메이저는 Standard, 알트는 Pump)
                bool isMajorManual = MajorSymbols.Contains(symbol);
                try
                {
                    if (isMajorManual)
                        _ = _positionMonitor.MonitorPositionStandard(symbol, avgPrice, isLongPos, token);
                    else
                        _ = _positionMonitor.MonitorPumpPositionShortTerm(symbol, avgPrice, "MANUAL_ENTRY", 0, token);
                }
                catch (Exception monEx) { OnStatusLog?.Invoke($"⚠️ [수동진입] {symbol} 모니터 시작 예외: {monEx.Message}"); }

                // [텔레그램] 수동 진입 알림
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TelegramService.Instance.SendMessageAsync(
                            $"⚡ *[수동 진입]*\n" +
                            $"`{symbol}` {direction} | 가격: `{avgPrice:F4}`\n" +
                            $"수량: `{filledQty}` | 레버: `{leverage}x` | 증거금: `${marginUsdt:N0}`\n" +
                            $"⏰ {DateTime.Now:HH:mm:ss}",
                            TelegramMessageType.Entry);
                    }
                    catch { }
                });

                // [FIX] UI 포지션 상태 업데이트 (Close 버튼 표시)
                OnPositionStatusUpdate?.Invoke(symbol, true, avgPrice);

                // [FIX] PositionSide + Leverage를 UI에 전달 (ROI 계산에 필수)
                OnSignalUpdate?.Invoke(new MultiTimeframeViewModel
                {
                    Symbol = symbol,
                    IsPositionActive = true,
                    PositionSide = direction,
                    EntryPrice = avgPrice,
                    Quantity = filledQty,
                    Leverage = leverage,
                    LastPrice = avgPrice
                });

                // [v5.10.54] 수동 진입 직후 OrderLifecycleManager 통합 등록
                if (_orderLifecycle != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            bool isLong = direction == "LONG";
                            bool isPump = !MajorSymbols.Contains(symbol);
                            int manLev = leverage > 0 ? leverage : 20;
                            decimal slRoe = isPump ? -(_settings.PumpStopLossRoe > 0 ? _settings.PumpStopLossRoe : 40m) : -50m;
                            decimal tpRoe = isPump ? Math.Max(_settings.PumpTp1Roe > 0 ? _settings.PumpTp1Roe : 25m, 25m) : 40m;
                            decimal tpPartial = isPump ? Math.Clamp((_settings.PumpFirstTakeProfitRatioPct > 0 ? _settings.PumpFirstTakeProfitRatioPct : 40m) / 100m, 0.05m, 0.95m) : 0.4m;
                            decimal trailCb3 = isPump ? Math.Clamp((_settings.PumpTrailingGapRoe > 0 ? _settings.PumpTrailingGapRoe : 20m) / manLev, 0.1m, 5.0m) : 2.0m;
                            var result = await _orderLifecycle.RegisterOnEntryAsync(
                                symbol, isLong, avgPrice, filledQty,
                                leverage, slRoe, tpRoe, tpPartial, trailCb3, CancellationToken.None);

                            if (!string.IsNullOrEmpty(result.SlOrderId))
                            {
                                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) p.StopOrderId = result.SlOrderId; }
                            }
                            OnStatusLog?.Invoke($"📋 [수동진입] {symbol} SL/TP/Trailing 등록 (SL={!string.IsNullOrEmpty(result.SlOrderId)} TP={!string.IsNullOrEmpty(result.TpOrderId)} TR={!string.IsNullOrEmpty(result.TrailingOrderId)})");
                        }
                        catch (Exception regEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ [수동진입] {symbol} OrderLifecycle 예외: {regEx.Message}");
                        }
                    });
                }

                // [FIX] DB TradeHistory에 진입 기록 저장
                try
                {
                    var tradeLog = new TradeLog(
                        symbol,
                        direction == "LONG" ? "BUY" : "SELL",
                        "MANUAL",
                        avgPrice,
                        0f,
                        DateTime.Now,
                        0m,
                        0m)
                    {
                        EntryPrice = avgPrice,
                        Quantity = filledQty,
                        EntryTime = DateTime.Now
                    };
                    await _dbManager.SaveTradeLogAsync(tradeLog);
                    OnTradeHistoryUpdated?.Invoke();
                }
                catch (Exception dbEx)
                {
                    OnStatusLog?.Invoke($"⚠️ [수동진입] {symbol} DB 기록 실패 (포지션은 정상): {dbEx.Message}");
                }

                return (true, $"{symbol} {direction} 체결 완료 | 가격={avgPrice:F8} 수량={filledQty}");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [수동진입] {symbol} 오류: {ex.Message}");
                return (false, $"{symbol} 수동 진입 오류: {ex.Message}");
            }
        }

        public async Task ClosePositionAsync(string symbol)
        {
            bool isSimulation = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
            bool isMockExchange = _exchangeService is MockExchangeService;

            // [FIX] MockExchange(가상 거래소)만 로컬 제거, 테스트넷은 실제 API 청산
            if (isSimulation && isMockExchange)
            {
                PositionInfo? pos = null;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out pos);
                    _activePositions.Remove(symbol);
                }

                if (pos != null)
                {
                    decimal exitPrice = pos.EntryPrice;
                    decimal pnl = 0m;
                    decimal pnlPct = 0m;

                    try
                    {
                        try { exitPrice = await _exchangeService.GetPriceAsync(symbol, CancellationToken.None); }
                        catch { /* MockExchange이므로 진입가로 폴백 */ }

                        pnl = pos.IsLong
                            ? (exitPrice - pos.EntryPrice) * Math.Abs(pos.Quantity)
                            : (pos.EntryPrice - exitPrice) * Math.Abs(pos.Quantity);
                        pnlPct = pos.EntryPrice > 0
                            ? (pnl / (pos.EntryPrice * Math.Abs(pos.Quantity))) * 100m * pos.Leverage
                            : 0m;

                        var tradeLog = new TradeLog(
                            symbol, pos.IsLong ? "SELL" : "BUY", "MANUAL_CLOSE_SIM",
                            exitPrice, pos.AiScore, DateTime.Now, pnl, pnlPct)
                        {
                            EntryPrice = pos.EntryPrice,
                            ExitPrice = exitPrice,
                            Quantity = Math.Abs(pos.Quantity),
                            EntryTime = pos.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = "사용자 수동 청산 (MockExchange)"
                        };
                        await _dbManager.SaveTradeLogAsync(tradeLog);
                    }
                    catch (Exception dbEx)
                    {
                        OnStatusLog?.Invoke($"⚠️ [Mock 청산] {symbol} DB 기록 실패: {dbEx.Message}");
                    }

                    OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);
                    OnPositionStatusUpdate?.Invoke(symbol, false, 0);
                    OnTickerUpdate?.Invoke(symbol, 0m, 0d);
                    _hybridExitManager?.RemoveState(symbol);
                    _blacklistedSymbols[symbol] = DateTime.Now.AddMinutes(30);
                    OnTradeHistoryUpdated?.Invoke();
                    OnAlert?.Invoke($"✅ [Mock] {symbol} 수동 청산 완료");

                    // [텔레그램] Mock 청산 알림
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string pnlEmoji = pnl >= 0 ? "💰" : "📉";
                            await TelegramService.Instance.SendMessageAsync(
                                $"{pnlEmoji} *[Mock 청산]*\n" +
                                $"`{symbol}` | 청산가: `{exitPrice:F4}`\n" +
                                $"PnL: `{pnl:F2}` USDT ({pnlPct:+0.0;-0.0}%)\n" +
                                $"⏰ {DateTime.Now:HH:mm:ss}",
                                TelegramMessageType.Profit);
                        }
                        catch { }
                    });
                }
                else
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 청산 대상 포지션이 없습니다.");
                }
                return;
            }

            // [v5.19.9] cooldown 을 청산 시작 전 즉시 등록 → 청산 진행 중 봇이 같은 심볼 진입 시도해도 차단
            RegisterManualCloseCooldown(symbol);

            // 실전 모드 + 테스트넷 모드: PositionMonitor를 통한 거래소 API 청산
            // [v5.10.75] 수동 청산은 fast-path 사용 (기존 ExecuteMarketClose는 algo 취소 대기로 10초+ 지연)
            if (_positionMonitor != null)
            {
                await _positionMonitor.ExecuteManualCloseFast(symbol, _cts?.Token ?? CancellationToken.None);
            }
            else
            {
                // [FIX] _positionMonitor 미초기화 시 _exchangeService로 직접 청산
                OnStatusLog?.Invoke($"⚠️ {symbol} PositionMonitor 미초기화 — 직접 거래소 API 청산 시도");
                await DirectClosePositionAsync(symbol);
            }
        }

        /// <summary>
        /// [FIX] _positionMonitor 없이 _exchangeService로 직접 청산 (수동 진입 후 스캔 미시작 시)
        /// </summary>
        private async Task DirectClosePositionAsync(string symbol)
        {
            try
            {
                PositionInfo? pos = null;
                lock (_posLock) { _activePositions.TryGetValue(symbol, out pos); }

                if (pos == null)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 로컬 포지션 없음 — 거래소 직접 조회");
                }

                decimal quantity = pos != null ? Math.Abs(pos.Quantity) : 0m;
                bool isLong = pos?.IsLong ?? true;

                // 거래소에서 실제 포지션 확인
                if (quantity <= 0)
                {
                    var positions = await _exchangeService.GetPositionsAsync(ct: CancellationToken.None);
                    var exchPos = positions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);
                    if (exchPos != null)
                    {
                        quantity = Math.Abs(exchPos.Quantity);
                        isLong = exchPos.IsLong;
                    }
                }

                if (quantity <= 0)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 거래소에도 포지션 없음");
                    // 로컬만 정리
                    lock (_posLock) { _activePositions.Remove(symbol); }
                    OnPositionStatusUpdate?.Invoke(symbol, false, 0);
                    return;
                }

                // 반대 방향 시장가 주문으로 청산
                string closeSide = isLong ? "SELL" : "BUY";
                var (success, filledQty, avgPrice) = await _exchangeService.PlaceMarketOrderAsync(symbol, closeSide, quantity, CancellationToken.None, reduceOnly: true);

                if (success && filledQty > 0)
                {
                    decimal entryPrice = pos?.EntryPrice ?? avgPrice;
                    decimal pnl = isLong
                        ? (avgPrice - entryPrice) * filledQty
                        : (entryPrice - avgPrice) * filledQty;
                    decimal leverage = pos?.Leverage ?? 1m;
                    decimal pnlPct = entryPrice > 0 ? (pnl / (entryPrice * filledQty)) * 100m * leverage : 0m;

                    try
                    {
                        var tradeLog = new TradeLog(symbol, closeSide, "MANUAL_CLOSE_DIRECT",
                            avgPrice, pos?.AiScore ?? 0f, DateTime.Now, pnl, pnlPct)
                        {
                            EntryPrice = entryPrice,
                            ExitPrice = avgPrice,
                            Quantity = filledQty,
                            EntryTime = pos?.EntryTime ?? DateTime.Now,
                            ExitTime = DateTime.Now,
                            ExitReason = "사용자 수동 청산 (직접)"
                        };
                        await _dbManager.SaveTradeLogAsync(tradeLog);
                    }
                    catch (Exception dbEx)
                    {
                        OnStatusLog?.Invoke($"⚠️ {symbol} DB 기록 실패: {dbEx.Message}");
                    }

                    lock (_posLock) { _activePositions.Remove(symbol); }
                    OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);
                    OnPositionStatusUpdate?.Invoke(symbol, false, 0);
                    OnTickerUpdate?.Invoke(symbol, 0m, 0d);
                    _hybridExitManager?.RemoveState(symbol);
                    OnTradeHistoryUpdated?.Invoke();
                    OnAlert?.Invoke($"✅ {symbol} 직접 청산 완료 | 가격={avgPrice:F8} PnL={pnl:F4}");

                    // [텔레그램] 수동 청산 알림
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string pnlEmoji = pnl >= 0 ? "💰" : "📉";
                            await TelegramService.Instance.SendMessageAsync(
                                $"{pnlEmoji} *[수동 청산]*\n" +
                                $"`{symbol}` {closeSide} | 청산가: `{avgPrice:F4}`\n" +
                                $"PnL: `{pnl:F2}` USDT ({pnlPct:+0.0;-0.0}%)\n" +
                                $"⏰ {DateTime.Now:HH:mm:ss}",
                                TelegramMessageType.Profit);
                        }
                        catch { }
                    });
                }
                else
                {
                    OnStatusLog?.Invoke($"❌ {symbol} 직접 청산 주문 실패");
                    OnCloseIncompleteStatusChanged?.Invoke(symbol, true, "직접 청산 주문 실패");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ {symbol} 직접 청산 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// [v5.10.62] 활성 포지션에 SL/TP/Trailing 없으면 OrderLifecycleManager로 자동 등록.
        /// 봇이 추적 중인 포지션(_activePositions) 중 거래소 openOrders에 조건부 주문이 0개인 심볼에 대해
        /// RegisterOnEntryAsync 호출하여 보호. 2분 주기로 반복 실행.
        /// </summary>
        private async Task EnsureActivePositionProtectionAsync(CancellationToken token)
        {
            if (_orderLifecycle == null) return;

            // 1. 현재 활성 포지션 스냅샷
            List<(string Symbol, bool IsLong, decimal EntryPrice, decimal Quantity, decimal Leverage)> snapshot;
            lock (_posLock)
            {
                snapshot = _activePositions.Values
                    .Where(p => Math.Abs(p.Quantity) > 0)
                    .Select(p => (p.Symbol, p.IsLong, p.EntryPrice, Math.Abs(p.Quantity), p.Leverage))
                    .ToList();
            }
            if (snapshot.Count == 0) return;

            // 2. 각 심볼별 거래소 openOrders 조회 → SL/TP/Trailing 없으면 등록
            foreach (var pos in snapshot)
            {
                try
                {
                    // [v5.10.63] Binance 2025-12 이관: 조건부 주문은 /fapi/v1/algoOrder 로 이동
                    // 일반 openOrders는 LIMIT/MARKET 만 있고 SL/TP/Trailing은 algoOrders에 있음 → 둘 다 조회
                    int totalProtection = 0;

                    var openOrders = await _client.UsdFuturesApi.Trading.GetOpenOrdersAsync(pos.Symbol, ct: token);
                    if (openOrders.Success && openOrders.Data != null)
                    {
                        totalProtection += openOrders.Data.Count(o =>
                            o.Type == Binance.Net.Enums.FuturesOrderType.StopMarket ||
                            o.Type == Binance.Net.Enums.FuturesOrderType.Stop ||
                            o.Type == Binance.Net.Enums.FuturesOrderType.TakeProfitMarket ||
                            o.Type == Binance.Net.Enums.FuturesOrderType.TakeProfit ||
                            o.Type == Binance.Net.Enums.FuturesOrderType.TrailingStopMarket);
                    }

                    // Algo 주문 (Binance 신규 엔드포인트) 조회
                    if (_exchangeService is BinanceExchangeService bx)
                    {
                        totalProtection += await bx.GetOpenAlgoOrderCountAsync(pos.Symbol, token);
                    }

                    if (totalProtection > 0) continue; // 이미 보호됨 → 스킵

                    // SL/TP/Trailing 모두 0 → 긴급 등록
                    OnStatusLog?.Invoke($"⚠️ [보호점검] {pos.Symbol} 보호 주문 0건 감지 → SL/TP/Trailing 자동 등록 시도");

                    int lev = (int)Math.Max(1m, pos.Leverage);
                    bool isPump = !MajorSymbols.Contains(pos.Symbol);
                    decimal slRoe = isPump ? -(_settings.PumpStopLossRoe > 0 ? _settings.PumpStopLossRoe : 40m) : -50m;
                    decimal tpRoe = isPump ? Math.Max(_settings.PumpTp1Roe > 0 ? _settings.PumpTp1Roe : 25m, 25m) : 40m;
                    decimal tpPartial = isPump ? Math.Clamp((_settings.PumpFirstTakeProfitRatioPct > 0 ? _settings.PumpFirstTakeProfitRatioPct : 40m) / 100m, 0.05m, 0.95m) : 0.4m;
                    decimal trailCb = isPump ? Math.Clamp((_settings.PumpTrailingGapRoe > 0 ? _settings.PumpTrailingGapRoe : 20m) / lev, 0.1m, 5.0m) : 2.0m;

                    _orderLifecycle.ResetCooldown(pos.Symbol);
                    var result = await _orderLifecycle.RegisterOnEntryAsync(
                        pos.Symbol, pos.IsLong, pos.EntryPrice, pos.Quantity, lev,
                        slRoe, tpRoe, tpPartial, trailCb, token);

                    OnStatusLog?.Invoke($"🛡️ [보호점검] {pos.Symbol} 재등록 결과: SL={!string.IsNullOrEmpty(result.SlOrderId)} TP={!string.IsNullOrEmpty(result.TpOrderId)} TR={!string.IsNullOrEmpty(result.TrailingOrderId)}");

                    // 텔레그램 경고
                    try
                    {
                        await TelegramService.Instance.SendMessageAsync(
                            $"🛡️ *[보호점검]* `{pos.Symbol}` SL/TP/Trailing 자동 재등록 (SL={!string.IsNullOrEmpty(result.SlOrderId)} TP={!string.IsNullOrEmpty(result.TpOrderId)} TR={!string.IsNullOrEmpty(result.TrailingOrderId)})",
                            TelegramMessageType.Alert);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    OnStatusLog?.Invoke($"⚠️ [보호점검] {pos.Symbol} 실패: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// [FIX] 거래소 포지션과 로컬 포지션을 동기화하고 누락된 청산을 TradeHistory에 반영합니다.
        /// WebSocket 연결 끊김이나 봇 중지 중 발생한 청산을 자동 보정합니다.
        /// </summary>
        public async Task SyncExchangePositionsAsync(CancellationToken token = default)
        {
            try
            {
                // 거래소에서 현재 오픈 포지션 목록 조회
                var exchangePositions = await _exchangeService.GetPositionsAsync(ct: token);
                var exchangeSymbols = new HashSet<string>(
                    exchangePositions.Where(p => Math.Abs(p.Quantity) > 0).Select(p => p.Symbol)
                );

                List<PositionInfo> missingClosedPositions = new List<PositionInfo>();

                lock (_posLock)
                {
                    // 로컬에는 있지만 거래소에는 없는 포지션 = 외부/누락 청산
                    foreach (var kvp in _activePositions.ToList())
                    {
                        string symbol = kvp.Key;
                        PositionInfo localPos = kvp.Value;

                        // 거래소에 포지션이 없고, 현재 청산 진행 중이 아닌 경우
                        if (!exchangeSymbols.Contains(symbol) && 
                            !_positionMonitor.IsCloseInProgress(symbol) &&
                            Math.Abs(localPos.Quantity) > 0)
                        {
                            missingClosedPositions.Add(new PositionInfo
                            {
                                Symbol = symbol,
                                IsLong = localPos.IsLong,
                                Quantity = Math.Abs(localPos.Quantity),
                                EntryPrice = localPos.EntryPrice,
                                Leverage = localPos.Leverage,
                                UnrealizedPnL = localPos.UnrealizedPnL,
                                Side = localPos.Side,
                                EntryTime = localPos.EntryTime,
                                AiScore = localPos.AiScore,
                                StopLoss = localPos.StopLoss
                            });

                            // 로컬 포지션 제거
                            _activePositions.Remove(symbol);
                            _hybridExitManager?.RemoveState(symbol);
                            OnPositionStatusUpdate?.Invoke(symbol, false, 0);
                        }
                    }
                }

                // 누락된 청산 건들을 TradeHistory에 반영
                foreach (var closedPos in missingClosedPositions)
                {
                    decimal exitPrice = 0m;

                    // 현재가 조회 시도
                    if (_marketDataManager.TickerCache.TryGetValue(closedPos.Symbol, out var ticker))
                    {
                        exitPrice = ticker.LastPrice;
                    }

                    if (exitPrice <= 0)
                    {
                        try
                        {
                            exitPrice = await _exchangeService.GetPriceAsync(closedPos.Symbol, ct: token);
                        }
                        catch (Exception priceEx)
                        {
                            OnStatusLog?.Invoke($"⚠️ {closedPos.Symbol} 가격 조회 실패, 진입가로 대체: {priceEx.Message}");
                            exitPrice = closedPos.EntryPrice;
                        }
                    }

                    if (exitPrice <= 0)
                        exitPrice = closedPos.EntryPrice;

                    bool stopLossLikelyTriggered = false;
                    if (closedPos.StopLoss > 0)
                    {
                        decimal stopTolerance = closedPos.StopLoss * 0.0015m; // 0.15% 허용
                        if (closedPos.IsLong)
                            stopLossLikelyTriggered = exitPrice <= closedPos.StopLoss + stopTolerance;
                        else
                            stopLossLikelyTriggered = exitPrice >= closedPos.StopLoss - stopTolerance;
                    }

                    string syncExitReason = stopLossLikelyTriggered
                        ? $"STOP_LOSS_MISSED_SYNC (SL={closedPos.StopLoss:F8})"
                        : "MISSED_CLOSE_SYNC (거래소 동기화)";

                    decimal closeQty = Math.Abs(closedPos.Quantity);
                    
                    // 순수 가격 차이
                    decimal rawPnl = closedPos.IsLong
                        ? (exitPrice - closedPos.EntryPrice) * closeQty
                        : (closedPos.EntryPrice - exitPrice) * closeQty;

                    // 거래 수수료 및 슬리피지 차감
                    decimal entryFee = closedPos.EntryPrice * closeQty * 0.0004m;
                    decimal exitFee = exitPrice * closeQty * 0.0004m;
                    decimal estimatedSlippage = exitPrice * closeQty * 0.0005m;
                    decimal pnl = rawPnl - entryFee - exitFee - estimatedSlippage;

                    decimal pnlPercent = 0m;
                    if (closedPos.EntryPrice > 0 && closeQty > 0)
                    {
                        pnlPercent = (pnl / (closedPos.EntryPrice * closeQty)) * 100m * closedPos.Leverage;
                    }

                    var missedCloseLog = new TradeLog(
                        closedPos.Symbol,
                        closedPos.IsLong ? "SELL" : "BUY",
                        stopLossLikelyTriggered ? "STOP_LOSS_MISSED_SYNC" : "MISSED_CLOSE_SYNC",
                        exitPrice,
                        closedPos.AiScore,
                        DateTime.Now,
                        pnl,
                        pnlPercent)
                    {
                        EntryPrice = closedPos.EntryPrice,
                        ExitPrice = exitPrice,
                        Quantity = closeQty,
                        EntryTime = closedPos.EntryTime == default ? DateTime.Now : closedPos.EntryTime,
                        ExitTime = DateTime.Now,
                        ExitReason = syncExitReason
                    };

                    bool synced = await _dbManager.TryCompleteOpenTradeAsync(missedCloseLog);
                    if (synced)
                    {
                        OnStatusLog?.Invoke($"📝 [동기화] {closedPos.Symbol} 누락된 청산 감지 → TradeHistory 반영 완료 (PnL={pnl:F2}, ROE={pnlPercent:F2}%)");
                        OnAlert?.Invoke($"⚠️ [누락 청산 복구] {closedPos.Symbol} 청산이 TradeHistory에 복구되었습니다.");
                        // [v5.10.89] 누락청산 텔레그램은 DbManager.TryCompleteOpenTradeAsync에서 중앙 처리

                        // 청산 이력 갱신 이벤트
                        OnTradeHistoryUpdated?.Invoke();
                    }
                    else
                    {
                        OnStatusLog?.Invoke($"⚠️ [동기화 실패] {closedPos.Symbol} TradeHistory 반영 실패 (userId 확인 필요)");
                        OnAlert?.Invoke($"❌ [DB 동기화 실패] {closedPos.Symbol} 누락 청산 반영 실패 - 수동 점검 필요");
                    }
                }

                if (missingClosedPositions.Count > 0)
                {
                    OnStatusLog?.Invoke($"✅ [거래소 동기화] {missingClosedPositions.Count}개 누락 청산 복구 완료");
                }
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ [거래소 동기화 오류] {ex.Message}");
            }
        }

        private async Task<CandleData?> GetLatestCandleDataAsync(string symbol, CancellationToken token, bool emitStatusLog = true)
        {
            try
            {
                // SMA120 + 여유분 = 최소 150봉 필요
                var klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, 200, token);
                if (klines == null || klines.Count < 130) return null;

                var subset = klines.ToList();
                var current = subset.Last();
                decimal entryPrice = current.ClosePrice;

                // ── 기본 지표 ──
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var prevMacd = IndicatorCalculator.CalculateMACD(subset.Take(subset.Count - 1).ToList());
                // [v3.2.11] MACD 골크/데크 피처
                float macdGoldenCross = (prevMacd.Macd < prevMacd.Signal && macd.Macd >= macd.Signal) ? 1f : 0f;
                float macdDeadCross = (prevMacd.Macd > prevMacd.Signal && macd.Macd <= macd.Signal) ? 1f : 0f;
                float macdHistChangeRate = Math.Abs(prevMacd.Hist) > 0.0000001
                    ? (float)((macd.Hist - prevMacd.Hist) / Math.Abs(prevMacd.Hist)) : 0f;
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                // ── SMA ──
                double sma20 = IndicatorCalculator.CalculateSMA(subset, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(subset, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(subset, 120);

                // [v4.6.2] 단타 보조지표 — EMA, VWAP, StochRSI
                double ema9 = IndicatorCalculator.CalculateEMA(subset, 9);
                double ema21 = IndicatorCalculator.CalculateEMA(subset, 21);
                double ema50 = IndicatorCalculator.CalculateEMA(subset, 50);
                float emaCrossState = 0f;
                if (ema9 > 0 && ema21 > 0 && ema50 > 0)
                {
                    if (ema9 > ema21 && ema21 > ema50) emaCrossState = 1f;
                    else if (ema9 < ema21 && ema21 < ema50) emaCrossState = -1f;
                }
                double vwap = IndicatorCalculator.CalculateVWAP(subset, lookback: 60);
                float priceVwapDistPct = vwap > 0
                    ? (float)(((double)entryPrice - vwap) / vwap * 100)
                    : 0f;
                var (stochRsiK, stochRsiD) = IndicatorCalculator.CalculateStochRSI(subset, 14, 14, 3, 3);
                float stochRsiCross = 0f;
                if (stochRsiK > stochRsiD) stochRsiCross = 1f;
                else if (stochRsiK < stochRsiD) stochRsiCross = -1f;

                // ── 볼린저 밴드 파생 ──
                double bbMid = (bb.Upper + bb.Lower) / 2.0;
                float bbWidth = bbMid > 0 ? (float)((bb.Upper - bb.Lower) / bbMid * 100) : 0;
                float priceToBBMid = bbMid > 0 ? (float)(((double)entryPrice - bbMid) / bbMid * 100) : 0;

                // ── 가격 파생 ──
                float priceChangePct = current.OpenPrice > 0
                    ? (float)((entryPrice - current.OpenPrice) / current.OpenPrice * 100)
                    : 0;
                float priceToSMA20Pct = sma20 > 0
                    ? (float)(((double)entryPrice - sma20) / sma20 * 100)
                    : 0;

                // ── 캔들 패턴 ──
                decimal range = current.HighPrice - current.LowPrice;
                float bodyRatio = range > 0 ? (float)(Math.Abs(current.ClosePrice - current.OpenPrice) / range) : 0;
                float upperShadow = range > 0 ? (float)((current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice)) / range) : 0;
                float lowerShadow = range > 0 ? (float)((Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice) / range) : 0;

                // ── 거래량 분석 ──
                var volumes = subset.Select(k => (float)k.Volume).ToList();
                int idx = volumes.Count - 1;
                float vol20Avg = 0;
                if (idx >= 20)
                {
                    for (int v = idx - 19; v <= idx; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[idx] / vol20Avg : 1;
                float volumeChangePct = (idx > 0 && volumes[idx - 1] > 0)
                    ? (volumes[idx] - volumes[idx - 1]) / volumes[idx - 1] * 100
                    : 0;

                // ── 피보나치 포지션 (0~1) ──
                float fibPosition = 0;
                if (fib.Level236 != fib.Level618 && fib.Level618 > 0)
                    fibPosition = (float)(((double)entryPrice - fib.Level236) / (fib.Level618 - fib.Level236));
                fibPosition = Math.Clamp(fibPosition, 0, 1);

                // ── 추세 강도 (-1 ~ +1) ──
                float trendStrength = 0;
                if (sma20 > 0 && sma60 > 0 && sma120 > 0)
                {
                    if (sma20 > sma60 && sma60 > sma120) trendStrength = 1.0f;
                    else if (sma20 < sma60 && sma60 < sma120) trendStrength = -1.0f;
                    else trendStrength = (float)((sma20 - sma120) / sma120);
                    trendStrength = Math.Clamp(trendStrength, -1f, 1f);
                }

                // ── RSI 다이버전스 ──
                float rsiDivergence = 0;
                if (subset.Count >= 6)
                {
                    var prevSubset = subset.GetRange(0, subset.Count - 5);
                    var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                    float priceDelta = (float)(current.ClosePrice - subset[subset.Count - 6].ClosePrice);
                    float rsiDelta = (float)(rsi - prevRsi);
                    if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;
                    else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1;
                }

                // ── 엘리엇 ──
                bool elliottBullish = IndicatorCalculator.AnalyzeElliottWave(subset);
                float elliottState = elliottBullish ? 1.0f : -1.0f;

                // ── 계단식 패턴 + 모멘텀 (v3.2.5) ──
                int higherLowsCount = 0;
                int lowerHighsCount = 0;
                if (subset.Count >= 15)
                {
                    for (int seg = 5; seg >= 3; seg--)
                    {
                        int segs = subset.Count / seg;
                        if (segs < 3) continue;
                        var tail = subset.TakeLast(seg * 3).ToList();
                        decimal l1 = tail.Take(seg).Min(k => k.LowPrice);
                        decimal l2 = tail.Skip(seg).Take(seg).Min(k => k.LowPrice);
                        decimal l3 = tail.Skip(seg * 2).Take(seg).Min(k => k.LowPrice);
                        if (l2 > l1 && l3 > l2) higherLowsCount++;

                        decimal h1 = tail.Take(seg).Max(k => k.HighPrice);
                        decimal h2 = tail.Skip(seg).Take(seg).Max(k => k.HighPrice);
                        decimal h3 = tail.Skip(seg * 2).Take(seg).Max(k => k.HighPrice);
                        if (h2 < h1 && h3 < h2) lowerHighsCount++;
                    }
                }

                float priceMomentum30m = 0f;
                float bounceFromLow = 0f;
                float dropFromHigh = 0f;
                if (subset.Count >= 12)
                {
                    var r6 = subset.TakeLast(6).ToList();
                    decimal p6ago = r6.First().ClosePrice;
                    priceMomentum30m = p6ago > 0 ? (float)((entryPrice - p6ago) / p6ago * 100) : 0f;

                    var r12 = subset.TakeLast(12).ToList();
                    decimal rLow = r12.Min(k => k.LowPrice);
                    decimal rHigh = r12.Max(k => k.HighPrice);
                    bounceFromLow = rLow > 0 ? (float)((entryPrice - rLow) / rLow * 100) : 0f;
                    dropFromHigh = rHigh > 0 ? (float)((rHigh - entryPrice) / rHigh * 100) : 0f;
                }

                // ── 뉴스 감성 점수 제거: 전략 입력에서 고정 0 사용 ──
                const float sentimentScore = 0f;

                return new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,

                    // 기본 보조지표
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    MACD_Hist_ChangeRate = macdHistChangeRate,
                    MACD_GoldenCross = macdGoldenCross,
                    MACD_DeadCross = macdDeadCross,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,

                    // SMA
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,

                    // [v4.6.2] 단타 보조지표 — EMA / VWAP / StochRSI
                    EMA_9 = (float)ema9,
                    EMA_21 = (float)ema21,
                    EMA_50 = (float)ema50,
                    EMA_Cross_State = emaCrossState,
                    VWAP = (float)vwap,
                    Price_VWAP_Distance_Pct = priceVwapDistPct,
                    StochRSI_K = (float)stochRsiK,
                    StochRSI_D = (float)stochRsiD,
                    StochRSI_Cross = stochRsiCross,

                    // 파생 피처
                    Price_Change_Pct = priceChangePct,
                    Price_To_BB_Mid = priceToBBMid,
                    BB_Width = bbWidth,
                    Price_To_SMA20_Pct = priceToSMA20Pct,
                    Candle_Body_Ratio = bodyRatio,
                    Upper_Shadow_Ratio = upperShadow,
                    Lower_Shadow_Ratio = lowerShadow,
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct,
                    Fib_Position = fibPosition,
                    Trend_Strength = trendStrength,
                    RSI_Divergence = rsiDivergence,
                    ElliottWaveState = elliottState,
                    // 계단식 패턴 + 모멘텀 (v3.2.5)
                    HigherLows_Count = (float)higherLowsCount,
                    LowerHighs_Count = (float)lowerHighsCount,
                    Price_Momentum_30m = priceMomentum30m,
                    Bounce_From_Low_Pct = bounceFromLow,
                    Drop_From_High_Pct = dropFromHigh,

                    SentimentScore = sentimentScore,
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetLatestCandleData] 지표 계산 실패: {ex.Message}");
                return null;
            }
        }

        private async Task TryRecordMlNetPredictionFromCommonScanAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            // [AI 제거] AIPredictor 의존 통째 제거
            await Task.CompletedTask;
        }

        #endregion

        // [추가] 로그 버퍼 관리
        private void AddToLogBuffer(string msg)
        {
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _apiLogBuffer.Enqueue(timestamped);
            if (_apiLogBuffer.Count > 100) _apiLogBuffer.TryDequeue(out _);
        }

        public string GetLogsJson()
        {
            var logs = _apiLogBuffer.ToList();
            logs.Reverse(); // 최신순 정렬
            var options = new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            return System.Text.Json.JsonSerializer.Serialize(logs, options);
        }

        // [Agent 5] 차트 데이터 JSON 반환
        public async Task<string> GetChartDataJsonAsync(string symbol, string intervalStr)
        {
            try
            {
                // 문자열을 표준 enum으로 변환 후, Binance enum으로 재변환
                var standardInterval = BinanceExchangeAdapter.ConvertStringToKlineInterval(intervalStr);
                var interval = BinanceExchangeAdapter.ConvertToBinanceKlineInterval(standardInterval);

                // 최근 50개 캔들 조회
                var klines = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: 50);
                if (klines.Success)
                {
                    var data = klines.Data.Select(k => new
                    {
                        Time = k.OpenTime,
                        Open = k.OpenPrice,
                        High = k.HighPrice,
                        Low = k.LowPrice,
                        Close = k.ClosePrice
                    });
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    };
                    return System.Text.Json.JsonSerializer.Serialize(data, options);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JSON 직렬화 오류: {ex.Message}");
            }
            return "[]";
        }

        // [추가] 포지션 목록 JSON 반환
        public string GetPositionsJson()
        {
            lock (_posLock)
            {
                var list = _activePositions.Select(kvp => new
                {
                    Symbol = kvp.Key,
                    EntryPrice = kvp.Value.EntryPrice,
                    Quantity = kvp.Value.Quantity,
                    Side = kvp.Value.IsLong ? "Long" : "Short",
                    PnL = 0m // PnL은 실시간 계산이 필요하므로 여기선 0 또는 추정치 (클라이언트에서 계산 권장)
                }).ToList();
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                return System.Text.Json.JsonSerializer.Serialize(list, options);
            }
        }

        // [추가] API를 통한 포지션 청산
        public async Task<string> ClosePositionApiAsync(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return "{\"status\": \"error\", \"message\": \"Symbol required\"}";

            try
            {
                await ClosePositionAsync(symbol);
                return "{\"status\": \"success\", \"message\": \"Close command sent\"}";
            }
            catch (Exception ex)
            {
                return $"{{\"status\": \"error\", \"message\": \"{ex.Message}\"}}";
            }
        }

        /// <summary>
        /// [AI 보너스 1] EMA 20 눌림목 감지
        /// ───────────────────────────────────────
        /// 조건:
        /// 1) 1시간봉 EMA 정배열 (EMA20 > EMA50)
        /// 2) 현재가가 5분봉 EMA 20 근처 (±0.2% 이내)
        /// 3) RSI >= 45 + 상승 추세
        /// 4) 거래량: 일반 수준 (급등 아님)
        /// 
        /// 반환: true = 안정적인 눌림목 신호, +10 보너스 점수 부여
        /// </summary>
        private async Task<bool> CheckEMA20RetestAsync(string symbol, CandleData latestCandle, CancellationToken token)
        {
            try
            {
                // 1) 5분봉 EMA 20 계산
                var k5m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 50, ct: token);
                if (!k5m.Success || k5m.Data == null || k5m.Data.Length < 30)
                    return false;
                
                var candles5m = k5m.Data.ToList();
                double ema20_5m = IndicatorCalculator.CalculateEMA(candles5m, 20);
                
                // 3) 1시간봉 EMA 정배열 확인
                var k1h = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.OneHour, limit: 60, ct: token);
                if (!k1h.Success || k1h.Data == null || k1h.Data.Length < 50)
                    return false;
                
                var candles1h = k1h.Data.ToList();
                double ema20_1h = IndicatorCalculator.CalculateEMA(candles1h, 20);
                double ema50_1h = IndicatorCalculator.CalculateEMA(candles1h, 50);
                
                bool isAlignedBullish = ema20_1h > ema50_1h;
                if (!isAlignedBullish)
                    return false;
                
                // 4) 현재가가 EMA 20 근처인지 확인 (±0.2%)
                double emaDeviation = Math.Abs((double)latestCandle.Close - ema20_5m) / ema20_5m;
                if (emaDeviation > 0.002) // 0.2% 초과
                    return false;
                
                // 5) RSI 상승 추세 확인 (RSI >= 45 + 이전봉보다 높음)
                if (latestCandle.RSI < 45f)
                    return false;
                
                // 이전 RSI 계산 (간단히 5분봉 2번째 마지막으로 근사)
                if (candles5m.Count >= 21)
                {
                    var prevCandles = candles5m.Count >= 2 
                        ? candles5m.Take(candles5m.Count - 1).ToList()
                        : new List<IBinanceKline>(); // 데이터 부족시 빈 리스트
                    double prevRSI = IndicatorCalculator.CalculateRSI(prevCandles, 14);
                    if (latestCandle.RSI <= prevRSI)
                        return false; // RSI 하락 중이면 눌림목 아님
                }
                
                // 6) 거래량 급등이 아닌지 확인 (볼륨 비율 < 1.5배)
                if (latestCandle.Volume_Ratio > 1.5f)
                    return false;
                
                return true; // 모든 조건 만족: EMA 20 눌림목 확정
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// [AI 보너스 2] 숏 스퀴즈 감지
        /// ───────────────────────────────────────
        /// 조건:
        /// 1) 5분봉 가격 급등 (+0.3% 이상)
        /// 2) Open Interest 급감 (-0.8% 이상)
        /// 3) 거래량 급증 (1.5배 이상)
        /// 4) BB 상단 돌파 시도
        /// 
        /// 반환: true = 숏 포지션 강제 청산 구간, +15 보너스 점수 부여
        /// </summary>
        private async Task<bool> CheckShortSqueezeAsync(string symbol, CandleData latestCandle, CancellationToken token)
        {
            try
            {
                // 1) 5분봉 가격 변화 확인
                var k5m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 10, ct: token);
                if (!k5m.Success || k5m.Data == null || k5m.Data.Length < 2)
                    return false;
                
                var candles5m = k5m.Data.ToList();
                // [FIX] 빈 리스트 체크 추가
                if (candles5m.Count < 2)
                {
                    OnStatusLog?.Invoke($"⚠️ {symbol} 5분봉 데이터 부족 (필요: 2, 실제: {candles5m.Count})");
                    return false;
                }
                
                var current = candles5m.Last();
                var previous = candles5m[candles5m.Count - 2];
                
                decimal priceChange = (current.ClosePrice - previous.ClosePrice) / previous.ClosePrice * 100;
                if (priceChange < 0.3m) // +0.3% 미만
                    return false;
                
                // 3) 거래량 급증 확인 (현재 캔들 데이터 활용)
                if (latestCandle.Volume_Ratio < 1.5f)
                    return false;
                
                // 4) BB 상단 근접 확인 (현재가가 BB 상단의 98% 이상)
                if (latestCandle.BollingerUpper > 0)
                {
                    double bbProximity = (double)latestCandle.Close / latestCandle.BollingerUpper;
                    if (bbProximity < 0.98) // BB 상단 근처 아님
                        return false;
                }
                
                // 5) Open Interest 급감 확인 (OiDataCollector 실시간 조회)
                if (_oiCollector != null)
                {
                    var oiSnapshot = await _oiCollector.GetCurrentOiAsync(symbol, token);
                    if (oiSnapshot == null || oiSnapshot.OiChangePct > -0.8)
                        return false; // OI 급감 없으면 스퀴즈 아님
                }
                
                return true; // 숏 스퀴즈 조건 만족
            }
            catch
            {
                return false;
            }
        }

        private string GetEngineStatusReport()
        {
            int activeCount = 0;
            string positionsStr = "";
            string aiLabelingStr = "AI 게이트 비활성";
            string entryBlockSummary = BuildEntryBlockSummary(topN: 5, resetAfterRead: false);
            string entryBlockHint = BuildEntryBlockTuneHint(topN: 3);

            lock (_posLock)
            {
                activeCount = _activePositions.Count;
                if (activeCount > 0)
                {
                    foreach (var kvp in _activePositions)
                    {
                        var pos = kvp.Value;
                        string side = pos.IsLong ? "LONG" : "SHORT";

                        // 현재가 가져오기 (UI 리스트 활용)
                        decimal currentPrice = pos.EntryPrice;
                        var vm = MainWindow.Instance?.ViewModel?.MarketDataList.FirstOrDefault(x => x.Symbol == kvp.Key);
                        if (vm != null) currentPrice = vm.LastPrice;

                        // 수익률 계산
                        decimal pnlPercent = 0;
                        if (pos.EntryPrice > 0)
                        {
                            if (pos.IsLong) pnlPercent = (currentPrice - pos.EntryPrice) / pos.EntryPrice * 100 * pos.Leverage;
                            else pnlPercent = (pos.EntryPrice - currentPrice) / pos.EntryPrice * 100 * pos.Leverage;
                        }

                        string icon = pnlPercent >= 0 ? "🟢" : "🔴";
                        positionsStr += $"{icon} *{kvp.Key}* ({side})\n" +
                                        $"   진입: ${pos.EntryPrice:0.####} | ROE: {pnlPercent:F2}%\n";
                    }
                }
                else
                {
                    positionsStr = "   (보유 포지션 없음)\n";
                }
            }

            var upTime = DateTime.Now - _engineStartTime;
            string timeStr = $"{(int)upTime.TotalDays}일 {upTime.Hours}시간 {upTime.Minutes}분";

            // [AI 제거] AI 라벨링 상태 텍스트 제거

            return $"🤖 *[시스템 상태]*\n" +
                   $"⏱ 가동 시간: {timeStr}\n" +
                   $"💰 금일 실현 손익: ${_riskManager.DailyRealizedPnl:N2}\n\n" +
                   $"📊 *[보유 포지션: {activeCount}개]*\n" +
                   positionsStr +
                     $"\n📉 *[진입 차단 요약]*\n" +
                     $"   {entryBlockSummary}\n" +
                                         $"🛠️ *[튜닝 힌트]*\n" +
                                         $"   {entryBlockHint}\n" +
                   $"\n{aiLabelingStr}\n" +
                   $"\n_Updated: {DateTime.Now:HH:mm:ss}_";
        }

        public string GetEngineStatusJson()
        {
            int activeCount = 0;
            lock (_posLock) activeCount = _activePositions.Count;

            int aiLabelTotal = 0;
            int aiLabelLabeled = 0;
            int aiLabelMarkToMarket = 0;
            int aiLabelClose = 0;
            int activeDecisionCount = _activeAiDecisionIds.Count;

            // [AI 제거] AI 라벨 통계 제거 — 모두 0 유지

            double aiLabelRate = aiLabelTotal > 0 ? (double)aiLabelLabeled / aiLabelTotal * 100.0 : 0.0;

            // 간단한 JSON 생성 (Newtonsoft.Json 없이 문자열 보간 사용)
            return $@"{{
                ""status"": ""{(IsBotRunning ? "Running" : "Stopped")}"",
                ""uptime"": ""{(DateTime.Now - _engineStartTime).ToString(@"dd\.hh\:mm\:ss")}"",
                ""balance"": {InitialBalance},
                ""pnl"": {_riskManager.DailyRealizedPnl},
                ""active_positions"": {activeCount},
                ""ai_label_total"": {aiLabelTotal},
                ""ai_label_labeled"": {aiLabelLabeled},
                ""ai_label_rate"": {aiLabelRate:F2},
                ""ai_label_mark_to_market"": {aiLabelMarkToMarket},
                ""ai_label_close"": {aiLabelClose},
                ""ai_active_decisions"": {activeDecisionCount}
            }}";
        }

        // [AI 모니터링] 예측 검증 메서드
        private async Task ValidatePredictionAsync(string predictionKey, string symbol, CancellationToken token)
        {
            try
            {
                if (!_pendingPredictions.TryRemove(predictionKey, out var predictionInfo))
                {
                    return;
                }

                var (timestamp, entryPrice, predictedPrice, predictedDirection, modelName, confidence) = predictionInfo;
                
                OnStatusLog?.Invoke($"🔍 [{modelName}] {symbol} 예측 검증 시작 (5분 경과)");

                // 현재 가격 조회
                var tickerResult = await _client.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol, token);
                if (!tickerResult.Success || tickerResult.Data == null)
                {
                    OnStatusLog?.Invoke($"❌ [{symbol}] 가격 조회 실패 - 예측 검증 중단");
                    return;
                }

                decimal actualPrice = tickerResult.Data.LastPrice;

                if (entryPrice <= 0m)
                {
                    OnStatusLog?.Invoke($"⚠️ [{modelName}] {symbol} 예측 검증 스킵: entryPrice 비정상 ({entryPrice:F8})");
                    return;
                }

                decimal moveRatio = Math.Abs((actualPrice - entryPrice) / entryPrice);
                if (moveRatio < PredictionValidationNeutralMovePct)
                {
                    OnStatusLog?.Invoke($"⏭️ [{modelName}] {symbol} 예측 검증 스킵: 미세변동 {moveRatio:P2} < {PredictionValidationNeutralMovePct:P2}");
                    return;
                }

                // 실제 방향 계산 (저장된 진입 가격 기준)
                bool actualDirection = actualPrice > entryPrice;

                // 정확도 판단
                bool isCorrect = predictedDirection == actualDirection;

                // 예측 기록 생성 및 이벤트 발생
                var record = new AIPredictionRecord
                {
                    Timestamp = timestamp,
                    Symbol = symbol,
                    ModelName = modelName,
                    PredictedPrice = predictedPrice,
                    ActualPrice = actualPrice,
                    PredictedDirection = predictedDirection,
                    ActualDirection = actualDirection,
                    Confidence = confidence,
                    IsCorrect = isCorrect
                };

                OnAIPrediction?.Invoke(record);
                
                string resultIcon = isCorrect ? "✅" : "❌";
                string direction = predictedDirection ? "상승" : "하락";
                OnStatusLog?.Invoke($"{resultIcon} [{modelName}] {symbol} 예측 검증 완료: {direction} 예측 → {(isCorrect ? "정확" : "미적중")} (진입: ${entryPrice:F4} / 예측: ${predictedPrice:F4} / 실제: ${actualPrice:F4})");

                // RL 보상 계산 및 업데이트
                double reward = isCorrect ? (confidence * 10) : (-confidence * 5);
                double scalpingReward = (double)(Math.Abs(actualPrice - entryPrice) / entryPrice) * (isCorrect ? 100 : -100);
                double swingReward = reward;

                OnRLStatsUpdate?.Invoke(modelName, (float)scalpingReward, (float)swingReward);
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"❌ 예측 검증 실패: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                PersistActiveAiDecisionIds();

                // CancellationTokenSource 정리
                _cts?.Cancel();
                _cts?.Dispose();
                
                // REST/Socket 클라이언트 정리
                _client?.Dispose();
                _marketDataManager?.Dispose();
                
                // 거래소 서비스 정리
                if (_exchangeService is IDisposable disposableExchange)
                {
                    disposableExchange.Dispose();
                }
                
                // [AI 제거] AI 서비스 Dispose 통째 제거
                
                // 데이터베이스 연결 정리
                // DbManager는 연결을 공유하므로 명시적 Dispose 불필요
                
                OnStatusLog?.Invoke("✅ TradingEngine 리소스 정리 완료");
            }
            catch (Exception ex)
            {
                OnStatusLog?.Invoke($"⚠️ TradingEngine Dispose 오류: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
            
            GC.SuppressFinalize(this);
        }

        ~TradingEngine()
        {
            Dispose();
        }
    }

    /// <summary>
    /// [캔들 확인 지연 진입] 대기 중인 신호 데이터
    /// 신호가 발생하면 즉시 진입하지 않고, 다음 캔들에서 확인 후 진입합니다.
    /// 가짜 돌파(Fakeout) 방지용.
    /// </summary>
    public class DelayedEntrySignal
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;   // "LONG" 또는 "SHORT"
        public decimal SignalPrice { get; set; }                 // 신호 발생 당시 가격
        public DateTime SignalTime { get; set; }                 // 신호 발생 시간
        public decimal SignalCandleHigh { get; set; }            // 신호 발생 캔들의 고가
        public decimal SignalCandleLow { get; set; }             // 신호 발생 캔들의 저가
        public decimal SignalCandleClose { get; set; }           // 신호 발생 캔들의 종가
        public string SignalSource { get; set; } = string.Empty; // 신호 소스 (MAJOR, TRANSFORMER 등)
        public string Mode { get; set; } = "TREND";
        public decimal CustomTakeProfitPrice { get; set; }
        public decimal CustomStopLossPrice { get; set; }
    }
}
