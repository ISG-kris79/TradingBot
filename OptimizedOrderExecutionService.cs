using System;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// [8단계] 주문 실행 최적화
    /// 
    /// 지정가 3초 대기 후 미체결 잔량 취소
    /// 부분 체결 시 즉시 손절/익절 감시 로직 활성화
    /// 
    /// 20배 레버리지에서는 3초도 길 수 있으므로
    /// 부분 체결 상태에서도 리스크 관리가 즉시 작동합니다.
    /// </summary>
    public class OptimizedOrderExecutionService
    {
        private readonly ILogger _logger;
        
        public const int LIMIT_ORDER_TIMEOUT_SECONDS = 3;
        
        public event Action<string>? OnLog;
        public event Action<string, decimal, bool>? OnPartialFillDetected;  // symbol, filledQty, isLong

        public OptimizedOrderExecutionService()
        {
            _logger = Log.ForContext<OptimizedOrderExecutionService>();
        }

        /// <summary>
        /// 최적화된 주문 실행 (지정가 3초 → 잔량 시장가)
        /// </summary>
        public async Task<OrderExecutionResult> ExecuteOptimizedOrderAsync(
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal limitPrice,
            Func<string, OrderSide, decimal, decimal, Task<string>> placeLimitOrderFunc,
            Func<string, Task<(decimal FilledQty, decimal RemainingQty, OrderStatus Status)>> checkOrderStatusFunc,
            Func<string, Task<bool>> cancelOrderFunc,
            Func<string, OrderSide, decimal, Task<bool>> placeMarketOrderFunc,
            CancellationToken cancellationToken = default)
        {
            var result = new OrderExecutionResult
            {
                Symbol = symbol,
                Side = side,
                RequestedQuantity = quantity,
                LimitPrice = limitPrice
            };

            try
            {
                // [1단계] 지정가 주문 실행
                LogInfo($"📤 [{symbol}] 지정가 주문 실행: {side} {quantity} @ ${limitPrice:F2}");
                string orderId = await placeLimitOrderFunc(symbol, side, quantity, limitPrice);
                
                if (string.IsNullOrEmpty(orderId))
                {
                    result.Success = false;
                    result.ErrorMessage = "지정가 주문 실패";
                    return result;
                }

                result.OrderId = orderId;

                // [2단계] 3초 대기
                LogInfo($"⏳ [{symbol}] 지정가 체결 대기 (3초)...");
                await Task.Delay(TimeSpan.FromSeconds(LIMIT_ORDER_TIMEOUT_SECONDS), cancellationToken);

                // [3단계] 주문 상태 확인
                var (filledQty, remainingQty, status) = await checkOrderStatusFunc(orderId);
                
                result.FilledQuantity = filledQty;
                result.RemainingQuantity = remainingQty;
                result.OrderStatus = status;

                // [4단계] 부분 체결 또는 미체결 처리
                if (status == OrderStatus.Filled)
                {
                    // 완전 체결
                    LogInfo($"✅ [{symbol}] 전량 체결 완료: {filledQty}");
                    result.Success = true;
                    result.ExecutionType = ExecutionType.FullyFilled;
                }
                else if (filledQty > 0 && remainingQty > 0)
                {
                    // 부분 체결
                    LogWarning($"⚠️ [{symbol}] 부분 체결 감지: {filledQty}/{quantity} (잔량: {remainingQty})");
                    
                    // 잔량 취소
                    bool canceled = await cancelOrderFunc(orderId);
                    if (canceled)
                    {
                        LogInfo($"❌ [{symbol}] 잔량 주문 취소 완료. 체결된 {filledQty}에 대해 손절/익절 감시 활성화.");
                        
                        // 부분 체결 이벤트 발생 (손절/익절 감시 트리거)
                        bool isLong = side == OrderSide.Buy;
                        OnPartialFillDetected?.Invoke(symbol, filledQty, isLong);
                        
                        result.Success = true;
                        result.ExecutionType = ExecutionType.PartiallyFilled;
                    }
                    else
                    {
                        LogError($"❗ [{symbol}] 잔량 취소 실패. 수동 확인 필요.");
                        result.Success = false;
                        result.ErrorMessage = "잔량 취소 실패";
                    }
                }
                else if (filledQty == 0)
                {
                    // 미체결
                    LogWarning($"❌ [{symbol}] 3초 내 미체결. 주문 취소 후 시장가 재진입 시도...");
                    
                    // 주문 취소
                    await cancelOrderFunc(orderId);
                    
                    // 시장가 재진입 (선택적)
                    bool marketSuccess = await placeMarketOrderFunc(symbol, side, quantity);
                    
                    if (marketSuccess)
                    {
                        LogInfo($"🚀 [{symbol}] 시장가 주문 성공: {quantity}");
                        result.Success = true;
                        result.ExecutionType = ExecutionType.MarketFallback;
                        result.FilledQuantity = quantity;  // 시장가는 즉시 체결 가정
                    }
                    else
                    {
                        LogError($"❗ [{symbol}] 시장가 주문 실패. 진입 포기.");
                        result.Success = false;
                        result.ErrorMessage = "시장가 주문 실패";
                        result.ExecutionType = ExecutionType.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"❗ [{symbol}] 주문 실행 중 예외 발생: {ex.Message}");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.ExecutionType = ExecutionType.Failed;
            }

            return result;
        }

        /// <summary>
        /// 부분 체결 후 손절/익절 감시 활성화 헬퍼
        /// </summary>
        public void ActivateStopLossForPartialFill(
            string symbol,
            decimal filledQuantity,
            decimal entryPrice,
            bool isLong,
            Action<string, decimal, decimal, bool> startMonitoringFunc)
        {
            LogInfo($"🛡️ [{symbol}] 부분 체결 수량 {filledQuantity}에 대한 손절/익절 감시 시작.");
            LogInfo($"   진입가: ${entryPrice:F2} | 방향: {(isLong ? "LONG" : "SHORT")}");
            
            // 손절/익절 감시 시작 (외부 함수 호출)
            startMonitoringFunc(symbol, filledQuantity, entryPrice, isLong);
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
    /// 주문 실행 결과
    /// </summary>
    public class OrderExecutionResult
    {
        public string Symbol { get; set; } = "";
        public OrderSide Side { get; set; }
        public decimal RequestedQuantity { get; set; }
        public decimal LimitPrice { get; set; }
        
        public string OrderId { get; set; } = "";
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
        
        public decimal FilledQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }
        public OrderStatus OrderStatus { get; set; }
        public ExecutionType ExecutionType { get; set; }
    }

    /// <summary>
    /// 실행 유형
    /// </summary>
    public enum ExecutionType
    {
        FullyFilled,      // 전량 체결
        PartiallyFilled,  // 부분 체결
        MarketFallback,   // 시장가 대체
        Failed            // 실패
    }
}
