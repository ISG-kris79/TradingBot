using System;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// 신호 파이프라인 통합 서비스
    /// 
    /// 전체 파이프라인 단계:
    /// [1단계] 신호 생성 (MajorCoinStrategy 등)
    /// [2단계] AI 필터 (기존 시스템)
    /// [3단계] 워밍업 (120초 + 휩소 감지) ← SignalWarmupService
    /// [4단계] 슬롯 제한 (최대 2개) ← SignalWarmupService
    /// [5단계] 리스크 계산 (기존 시스템)
    /// [6단계] 5분봉 필터 (필수 3 + 선택 2/3) ← FiveMinuteFilterService
    /// [7단계] 최종 승인
    /// [8단계] 주문 실행 (지정가 3초 + 부분 체결 처리) ← OptimizedOrderExecutionService
    /// 
    /// 최종 진입율: 약 0.5% (수천 번의 기회 중 가장 확실한 5~10번만 선택)
    /// </summary>
    public class SignalPipelineService
    {
        private readonly ILogger _logger;
        private readonly SignalWarmupService _warmupService;
        private readonly FiveMinuteFilterService _filterService;
        private readonly OptimizedOrderExecutionService _executionService;

        public event Action<string>? OnLog;
        public event Action<string, PipelineStage, string>? OnPipelineStageComplete;  // symbol, stage, result

        public SignalPipelineService(
            SignalWarmupService warmupService,
            FiveMinuteFilterService filterService,
            OptimizedOrderExecutionService executionService)
        {
            _logger = Log.ForContext<SignalPipelineService>();
            _warmupService = warmupService;
            _filterService = filterService;
            _executionService = executionService;
        }

        /// <summary>
        /// [전체 파이프라인] 신호 → 주문 실행
        /// </summary>
        public async Task<PipelineResult> ProcessSignalAsync(
            SignalData signal,
            Func<SignalData, Task<bool>> aiFilterFunc,
            Func<string, OrderSide, decimal, decimal, Task<string>> placeLimitOrderFunc,
            Func<string, Task<(decimal, decimal, OrderStatus)>> checkOrderStatusFunc,
            Func<string, Task<bool>> cancelOrderFunc,
            Func<string, OrderSide, decimal, Task<bool>> placeMarketOrderFunc,
            Action<string, decimal, decimal, bool> startMonitoringFunc,
            CancellationToken cancellationToken = default)
        {
            var result = new PipelineResult { Symbol = signal.Symbol };

            try
            {
                // ============ [2단계] AI 필터 ============
                LogInfo($"🔹 [{signal.Symbol}] [2단계] AI 필터 검증 중...");
                bool aiPass = await aiFilterFunc(signal);
                
                if (!aiPass)
                {
                    result.FailedStage = PipelineStage.AiFilter;
                    result.FailReason = "AI 필터 차단";
                    LogWarning($"❌ [{signal.Symbol}] AI 필터 차단. 파이프라인 종료.");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.AiFilter, "차단");
                    return result;
                }
                
                LogInfo($"✅ [{signal.Symbol}] AI 필터 통과.");
                OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.AiFilter, "통과");

                // ============ [3단계] 워밍업 등록 ============
                LogInfo($"🔹 [{signal.Symbol}] [3단계] 워밍업 등록 중...");
                bool registered = _warmupService.RegisterSignal(signal.Symbol, signal.CurrentPrice);
                
                if (!registered)
                {
                    result.FailedStage = PipelineStage.Warmup;
                    result.FailReason = "워밍업 등록 실패 (슬롯 부족 가능성)";
                    LogWarning($"❌ [{signal.Symbol}] 워밍업 등록 실패.");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.Warmup, "등록 실패");
                    return result;
                }

                // 120초 워밍업 기간 동안 가격 추적
                var startTime = DateTime.UtcNow;
                while ((DateTime.UtcNow - startTime).TotalSeconds < SignalWarmupService.WARMUP_DURATION_SECONDS)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.FailedStage = PipelineStage.Warmup;
                        result.FailReason = "워밍업 취소됨";
                        return result;
                    }

                    // 가격 업데이트 (실제로는 외부에서 주기적으로 호출해야 함)
                    _warmupService.UpdatePrice(signal.Symbol, signal.CurrentPrice);
                    
                    await Task.Delay(1000, cancellationToken);  // 1초마다 체크
                }

                // 워밍업 완료 확인
                var (isReady, isWhipsaw, reason) = _warmupService.CheckWarmupStatus(signal.Symbol);
                
                if (isWhipsaw)
                {
                    result.FailedStage = PipelineStage.Warmup;
                    result.FailReason = "휩소 감지: " + reason;
                    LogWarning($"❌ [{signal.Symbol}] 휩소 감지. 신호 드랍.");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.Warmup, "휩소 감지");
                    return result;
                }
                
                LogInfo($"✅ [{signal.Symbol}] 워밍업 통과: {reason}");
                OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.Warmup, "통과");

                // ============ [4단계] 슬롯 점유 ============
                LogInfo($"🔹 [{signal.Symbol}] [4단계] 슬롯 점유 시도...");
                bool slotOccupied = _warmupService.OccupySlot(signal.Symbol);
                
                if (!slotOccupied)
                {
                    result.FailedStage = PipelineStage.SlotLimit;
                    result.FailReason = "슬롯 제한 (최대 2개)";
                    LogWarning($"❌ [{signal.Symbol}] 슬롯 점유 실패.");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.SlotLimit, "점유 실패");
                    return result;
                }
                
                LogInfo($"✅ [{signal.Symbol}] 슬롯 점유 성공.");
                OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.SlotLimit, "점유 성공");

                // ============ [5단계] 리스크 계산 생략 (기존 시스템 연동) ============
                // 실제로는 포지션 크기, 레버리지 등 계산

                // ============ [6단계] 5분봉 필터 ============
                LogInfo($"🔹 [{signal.Symbol}] [6단계] 5분봉 필터 검증 중...");
                var filterResult = _filterService.EvaluateFilter(signal.FilterInput);
                
                if (!filterResult.Passed)
                {
                    result.FailedStage = PipelineStage.FiveMinuteFilter;
                    result.FailReason = filterResult.Reason;
                    LogWarning($"❌ [{signal.Symbol}] 5분봉 필터 미달: {filterResult.Reason}");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.FiveMinuteFilter, "미달");
                    
                    // 슬롯 해제
                    _warmupService.ReleaseSlot(signal.Symbol);
                    return result;
                }
                
                LogInfo($"✅ [{signal.Symbol}] 5분봉 필터 통과.");
                OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.FiveMinuteFilter, "통과");

                // ============ [7단계] 최종 승인 ============
                LogInfo($"🔹 [{signal.Symbol}] [7단계] 최종 승인 완료. 주문 실행 시작.");
                OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.FinalApproval, "승인");

                // ============ [8단계] 주문 실행 ============
                LogInfo($"🔹 [{signal.Symbol}] [8단계] 최적화 주문 실행 중...");
                var executionResult = await _executionService.ExecuteOptimizedOrderAsync(
                    signal.Symbol,
                    signal.IsLong ? OrderSide.Buy : OrderSide.Sell,
                    signal.Quantity,
                    signal.LimitPrice,
                    placeLimitOrderFunc,
                    checkOrderStatusFunc,
                    cancelOrderFunc,
                    placeMarketOrderFunc,
                    cancellationToken);

                if (executionResult.Success)
                {
                    result.Success = true;
                    result.ExecutionResult = executionResult;
                    
                    LogInfo($"🎉 [{signal.Symbol}] 파이프라인 완료! 체결: {executionResult.ExecutionType}");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.OrderExecution, "성공");

                    // 부분 체결 시 손절/익절 감시 활성화
                    if (executionResult.ExecutionType == ExecutionType.PartiallyFilled)
                    {
                        _executionService.ActivateStopLossForPartialFill(
                            signal.Symbol,
                            executionResult.FilledQuantity,
                            signal.LimitPrice,
                            signal.IsLong,
                            startMonitoringFunc);
                    }
                }
                else
                {
                    result.FailedStage = PipelineStage.OrderExecution;
                    result.FailReason = executionResult.ErrorMessage;
                    LogError($"❌ [{signal.Symbol}] 주문 실행 실패: {executionResult.ErrorMessage}");
                    OnPipelineStageComplete?.Invoke(signal.Symbol, PipelineStage.OrderExecution, "실패");
                    
                    // 슬롯 해제
                    _warmupService.ReleaseSlot(signal.Symbol);
                }
            }
            catch (Exception ex)
            {
                LogError($"❗ [{signal.Symbol}] 파이프라인 예외: {ex.Message}");
                result.FailedStage = PipelineStage.Unknown;
                result.FailReason = ex.Message;
                
                // 슬롯 해제
                _warmupService.ReleaseSlot(signal.Symbol);
            }

            return result;
        }

        /// <summary>
        /// 포지션 청산 시 슬롯 해제
        /// </summary>
        public void OnPositionClosed(string symbol)
        {
            _warmupService.ReleaseSlot(symbol);
            LogInfo($"🔓 [{symbol}] 포지션 청산. 슬롯 해제 완료.");
        }

        private void LogInfo(string message)
        {
            _logger.Information(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void LogWarning(string message)
        {
            _logger.Warning(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void LogError(string message)
        {
            _logger.Error(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    /// <summary>
    /// 신호 데이터
    /// </summary>
    public class SignalData
    {
        public string Symbol { get; set; } = "";
        public decimal CurrentPrice { get; set; }
        public bool IsLong { get; set; }
        public decimal Quantity { get; set; }
        public decimal LimitPrice { get; set; }
        public FilterInput FilterInput { get; set; } = new();
    }

    /// <summary>
    /// 파이프라인 결과
    /// </summary>
    public class PipelineResult
    {
        public string Symbol { get; set; } = "";
        public bool Success { get; set; }
        public PipelineStage FailedStage { get; set; } = PipelineStage.None;
        public string FailReason { get; set; } = "";
        public OrderExecutionResult? ExecutionResult { get; set; }
    }

    /// <summary>
    /// 파이프라인 단계
    /// </summary>
    public enum PipelineStage
    {
        None,
        SignalGeneration,   // 1단계
        AiFilter,           // 2단계
        Warmup,             // 3단계
        SlotLimit,          // 4단계
        RiskCalculation,    // 5단계
        FiveMinuteFilter,   // 6단계
        FinalApproval,      // 7단계
        OrderExecution,     // 8단계
        Unknown
    }
}
