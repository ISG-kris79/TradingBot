using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 거래소 간 자동 자금 이동 서비스 (Phase 13)
    /// 거래소 간 잔고를 모니터링하고 필요 시 자동으로 자금을 이동합니다.
    /// </summary>
    public class FundTransferService
    {
        private readonly Dictionary<ExchangeType, IExchangeService> _exchanges = new();
        private readonly FundTransferSettings _settings;
        private readonly DbManager? _dbManager;  // [Phase 14] DB 로깅
        private readonly TelegramService? _telegram;  // [Phase 14] Telegram 알림
        private bool _isMonitoring = false;
        private CancellationTokenSource? _cts;
        
        public event Action<string>? OnLog;
        public event Action<FundTransferRequest>? OnTransferInitiated;
        public event Action<FundTransferResult>? OnTransferCompleted;
        
        public FundTransferService(FundTransferSettings settings, DbManager? dbManager = null, TelegramService? telegram = null)
        {
            _settings = settings;
            _dbManager = dbManager;
            _telegram = telegram;
        }
        
        /// <summary>
        /// 거래소 추가
        /// </summary>
        public void AddExchange(ExchangeType type, IExchangeService service)
        {
            _exchanges[type] = service;
            OnLog?.Invoke($"[FundTransfer] 거래소 추가: {type}");
        }
        
        /// <summary>
        /// 자동 자금 이동 모니터링 시작
        /// </summary>
        public Task StartMonitoringAsync(CancellationToken ct = default)
        {
            if (_isMonitoring)
            {
                OnLog?.Invoke("[FundTransfer] ⚠️ 이미 모니터링 중입니다.");
                return Task.CompletedTask;
            }
            
            _isMonitoring = true;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            
            OnLog?.Invoke($"[FundTransfer] 🚀 자금 이동 모니터링 시작 (체크 간격: {_settings.CheckIntervalMinutes}분)");
            
            _ = Task.Run(() => MonitoringLoopAsync(token), token);
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// 모니터링 중지
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _isMonitoring = false;
            OnLog?.Invoke("[FundTransfer] 🛑 자금 이동 모니터링 중지");
        }
        
        /// <summary>
        /// 주기적으로 거래소 간 잔고를 확인하고 필요 시 이동
        /// </summary>
        private async Task MonitoringLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isMonitoring)
            {
                try
                {
                    await CheckAndRebalanceAsync(ct);
                    
                    // 다음 체크까지 대기
                    await Task.Delay(TimeSpan.FromMinutes(_settings.CheckIntervalMinutes), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[FundTransfer] ⚠️ 모니터링 오류: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                }
            }
        }
        
        /// <summary>
        /// 거래소 간 잔고 확인 및 리밸런싱
        /// </summary>
        private async Task CheckAndRebalanceAsync(CancellationToken ct)
        {
            try
            {
                OnLog?.Invoke("[FundTransfer] 🔍 거래소 잔고 확인 중...");
                
                // 각 거래소의 USDT 잔고 조회
                var balances = new Dictionary<ExchangeType, decimal>();
                
                foreach (var (exchangeType, service) in _exchanges)
                {
                    var balance = await service.GetBalanceAsync("USDT", ct);
                    balances[exchangeType] = balance;
                    OnLog?.Invoke($"[FundTransfer] {exchangeType}: {balance:F2} USDT");
                }
                
                if (balances.Count < 2) return;
                
                // 최소 잔고 거래소 확인
                var minBalance = balances.OrderBy(b => b.Value).First();
                var maxBalance = balances.OrderBy(b => b.Value).Last();
                
                decimal balanceDiff = maxBalance.Value - minBalance.Value;
                decimal totalBalance = balances.Values.Sum();
                decimal targetBalance = totalBalance / balances.Count;
                
                // 최소 잔고가 목표의 80% 미만이면 자금 이동
                if (minBalance.Value < targetBalance * 0.8m && balanceDiff > _settings.MinTransferAmount)
                {
                    decimal transferAmount = Math.Min(
                        balanceDiff / 2, // 절반만 이동
                        maxBalance.Value - targetBalance // 또는 목표까지만
                    );
                    
                    transferAmount = Math.Max(transferAmount, _settings.MinTransferAmount);
                    
                    OnLog?.Invoke($"[FundTransfer] ⚠️ 잔고 불균형 감지!");
                    OnLog?.Invoke($"[FundTransfer] {maxBalance.Key}: {maxBalance.Value:F2} → {minBalance.Key}: {minBalance.Value:F2}");
                    
                    if (_settings.AutoTransfer)
                    {
                        await InitiateTransferAsync(maxBalance.Key, minBalance.Key, transferAmount, ct);
                    }
                    else
                    {
                        OnLog?.Invoke($"[FundTransfer] 💡 권장: {maxBalance.Key}에서 {minBalance.Key}로 {transferAmount:F2} USDT 이동");
                    }
                }
                else
                {
                    OnLog?.Invoke("[FundTransfer] ✅ 거래소 간 잔고 균형 유지 중");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[FundTransfer] ❌ 잔고 확인 오류: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 수동 자금 이동 요청
        /// </summary>
        public Task<FundTransferResult> RequestTransferAsync(
            ExchangeType fromExchange,
            ExchangeType toExchange,
            decimal amount,
            CancellationToken ct = default)
        {
            OnLog?.Invoke($"[FundTransfer] 📤 수동 자금 이동 요청: {fromExchange} → {toExchange}, {amount:F2} USDT");
            return InitiateTransferAsync(fromExchange, toExchange, amount, ct);
        }
        
        /// <summary>
        /// 자금 이동 실행 (출금 → 입금)
        /// </summary>
        private Task<FundTransferResult> InitiateTransferAsync(
            ExchangeType fromExchange,
            ExchangeType toExchange,
            decimal amount,
            CancellationToken ct)
        {
            var request = new FundTransferRequest
            {
                FromExchange = fromExchange,
                ToExchange = toExchange,
                Amount = amount,
                Asset = "USDT",
                Timestamp = DateTime.UtcNow
            };
            
            var result = new FundTransferResult
            {
                Request = request,
                StartTime = DateTime.UtcNow
            };
            
            OnTransferInitiated?.Invoke(request);
            
            try
            {
                OnLog?.Invoke($"[FundTransfer] 🔄 자금 이동 시작: {fromExchange} → {toExchange} ({amount:F2} {request.Asset})");
                
                // [Phase 14] 시뮬레이션 모드 체크
                if (_settings.SimulationMode)
                {
                    OnLog?.Invoke($"[FundTransfer] 🔵 시뮬레이션 모드: 실제 출금/입금 없이 시뮬레이션만 수행");
                    
                    // 시뮬레이션: 항상 성공으로 처리
                    result.WithdrawSuccess = true;
                    result.DepositSuccess = true;
                    result.Success = true;
                    result.EndTime = DateTime.UtcNow;
                    
                    OnLog?.Invoke($"[FundTransfer] ✅ 시뮬레이션 완료: {amount:F2} {request.Asset} 이동");
                    
                    // [Phase 14] DB 로그 저장
                    if (_dbManager != null)
                    {
                        _ = Task.Run(() => _dbManager.SaveFundTransferLogAsync(result));
                    }
                    
                    OnTransferCompleted?.Invoke(result);
                    
                    // [Phase 14] Telegram 알림
                    if (_telegram != null)
                    {
                        _ = Task.Run(() => _telegram.SendFundTransferNotificationAsync(
                            request.FromExchange.ToString(),
                            request.ToExchange.ToString(),
                            request.Asset,
                            request.Amount,
                            true,
                            null
                        ));
                    }
                    
                    return Task.FromResult(result);
                }
                
                OnLog?.Invoke($"[FundTransfer] ⚠️ 실제 출금/입금 모드: 실제 자금이 이동됩니다!");
                
                // 실제 출금/입금 API 구현
                var fromService = _exchanges[fromExchange];
                
                // 1단계: 목적지 거래소의 입금 주소 조회
                // 주의: IExchangeService에 GetDepositAddressAsync 메서드가 있어야 함
                OnLog?.Invoke($"[FundTransfer] 📍 {toExchange} 입금 주소 조회 중...");
                // TODO: 실제 구현 필요
                // var depositAddress = await toService.GetDepositAddressAsync(request.Asset, ct);
                // if (string.IsNullOrEmpty(depositAddress))
                // {
                //     throw new Exception($"{toExchange}의 {request.Asset} 입금 주소를 가져올 수 없습니다.");
                // }
                
                // 2단계: 출발 거래소에서 출금 요청
                OnLog?.Invoke($"[FundTransfer] 💸 {fromExchange}에서 출금 요청 중...");
                // TODO: 실제 구현 필요
                // var withdrawResult = await fromService.WithdrawAsync(request.Asset, amount, depositAddress, ct);
                // result.WithdrawSuccess = withdrawResult != null;
                // if (!result.WithdrawSuccess)
                // {
                //     throw new Exception($"{fromExchange}에서 출금 실패");
                // }
                
                // 3단계: 입금 확인 (폴링 또는 WebSocket)
                OnLog?.Invoke($"[FundTransfer] ⏳ {toExchange} 입금 대기 중...");
                // TODO: 실제 구현 필요
                // 주의: 입금은 블록체인 확인 시간(수 분~수십 분) 소요
                // var depositConfirmed = await WaitForDepositConfirmationAsync(toService, request.Asset, amount, ct);
                // result.DepositSuccess = depositConfirmed;
                
                // [미구현] 실제 출금/입금 API는 아직 구현되지 않음
                result.Success = false;
                result.ErrorMessage = "실제 출금/입금 API는 아직 구현되지 않았습니다. " +
                    "IExchangeService에 GetDepositAddressAsync, WithdrawAsync 메서드를 추가해야 합니다. " +
                    "시뮬레이션 모드(SimulationMode: true)를 사용하세요.";
                result.EndTime = DateTime.UtcNow;
                
                OnLog?.Invoke($"[FundTransfer] ⚠️ 미구현 기능: {result.ErrorMessage}");
                OnTransferCompleted?.Invoke(result);
                
                return Task.FromResult(result);
                
                // result.Success = result.WithdrawSuccess && result.DepositSuccess;
                // result.EndTime = DateTime.UtcNow;
                // 
                // OnLog?.Invoke($"[FundTransfer] ✅ 자금 이동 완료");
                // 
                // // [Phase 14] DB 로그 저장
                // if (_dbManager != null)
                // {
                //     _ = Task.Run(async () => await _dbManager.SaveFundTransferLogAsync(result));
                // }
                // 
                // OnTransferCompleted?.Invoke(result);
                // 
                // // [Phase 14] Telegram 알림
                // if (_telegram != null)
                // {
                //     _ = Task.Run(async () => await _telegram.SendFundTransferNotificationAsync(
                //         request.FromExchange.ToString(),
                //         request.ToExchange.ToString(),
                //         request.Asset,
                //         request.Amount,
                //         true,
                //         null
                //     ));
                // }
                
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                
                OnLog?.Invoke($"[FundTransfer] ❌ 자금 이동 실패: {ex.Message}");
                
                // [Phase 14] DB 로그 저장 (실패도 기록)
                if (_dbManager != null)
                {
                    _ = Task.Run(() => _dbManager.SaveFundTransferLogAsync(result));
                }
                
                OnTransferCompleted?.Invoke(result);
                
                // [Phase 14] Telegram 알림 - 자금 이동 실패
                if (_telegram != null)
                {
                    _ = Task.Run(() => _telegram.SendFundTransferNotificationAsync(
                        request.FromExchange.ToString(),
                        request.ToExchange.ToString(),
                        request.Asset,
                        request.Amount,
                        false,
                        ex.Message
                    ));
                }
                
                return Task.FromResult(result);
            }
        }
    }
    
    /// <summary>
    /// 자금 이동 요청
    /// </summary>
    public class FundTransferRequest
    {
        public ExchangeType FromExchange { get; set; }
        public ExchangeType ToExchange { get; set; }
        public string Asset { get; set; } = "USDT";
        public decimal Amount { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    /// <summary>
    /// 자금 이동 결과
    /// </summary>
    public class FundTransferResult
    {
        public FundTransferRequest Request { get; set; } = null!;
        public bool WithdrawSuccess { get; set; }
        public bool DepositSuccess { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }
}
