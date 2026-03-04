using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 통합 포트폴리오 리밸런싱 서비스 (Phase 13)
    /// 여러 거래소에 분산된 자산을 목표 비율에 맞춰 자동으로 재조정합니다.
    /// </summary>
    public class PortfolioRebalancingService
    {
        private readonly Dictionary<ExchangeType, IExchangeService> _exchanges = new();
        private readonly PortfolioRebalancingSettings _settings;
        private readonly DbManager? _dbManager;  // [Phase 14] DB 로깅
        private readonly TelegramService? _telegram;  // [Phase 14] Telegram 알림
        private bool _isMonitoring = false;
        private CancellationTokenSource? _cts;
        
        public event Action<string>? OnLog;
        public event Action<RebalancingReport>? OnRebalancingCompleted;
        
        public PortfolioRebalancingService(PortfolioRebalancingSettings settings, DbManager? dbManager = null, TelegramService? telegram = null)
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
            OnLog?.Invoke($"[Rebalancing] 거래소 추가: {type}");
        }
        
        /// <summary>
        /// 자동 리밸런싱 모니터링 시작
        /// </summary>
        public async Task StartMonitoringAsync(CancellationToken ct = default)
        {
            if (_isMonitoring)
            {
                OnLog?.Invoke("[Rebalancing] ⚠️ 이미 모니터링 중입니다.");
                return;
            }
            
            _isMonitoring = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            
            OnLog?.Invoke($"[Rebalancing] 🚀 리밸런싱 모니터링 시작 (체크 간격: {_settings.CheckIntervalHours}시간)");
            
            _ = Task.Run(async () => await MonitoringLoopAsync(token), token);
        }
        
        /// <summary>
        /// 모니터링 중지
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _isMonitoring = false;
            OnLog?.Invoke("[Rebalancing] 🛑 리밸런싱 모니터링 중지");
        }
        
        /// <summary>
        /// 주기적으로 포트폴리오를 확인하고 리밸런싱
        /// </summary>
        private async Task MonitoringLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isMonitoring)
            {
                try
                {
                    await CheckAndRebalanceAsync(ct);
                    
                    // 다음 체크까지 대기
                    await Task.Delay(TimeSpan.FromHours(_settings.CheckIntervalHours), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Rebalancing] ⚠️ 모니터링 오류: {ex.Message}");
                    await Task.Delay(TimeSpan.FromHours(1), ct);
                }
            }
        }
        
        /// <summary>
        /// 포트폴리오 확인 및 리밸런싱
        /// </summary>
        private async Task CheckAndRebalanceAsync(CancellationToken ct)
        {
            try
            {
                OnLog?.Invoke("[Rebalancing] 🔍 통합 포트폴리오 분석 중...");
                
                var portfolio = await GetConsolidatedPortfolioAsync(ct);
                
                if (!portfolio.Any())
                {
                    OnLog?.Invoke("[Rebalancing] ⚠️ 포트폴리오가 비어있습니다.");
                    return;
                }
                
                // 현재 비율 계산
                decimal totalValue = portfolio.Sum(p => p.Value);
                var currentAllocation = portfolio.ToDictionary(
                    p => p.Key,
                    p => (p.Value / totalValue) * 100m
                );
                
                OnLog?.Invoke($"[Rebalancing] 💰 총 포트폴리오 가치: ${totalValue:F2}");
                OnLog?.Invoke($"[Rebalancing] 📊 현재 자산 배분:");
                foreach (var (asset, percentage) in currentAllocation.OrderByDescending(a => a.Value))
                {
                    OnLog?.Invoke($"  - {asset}: {percentage:F2}%");
                }
                
                // 목표 비율과 비교
                var rebalanceNeeded = false;
                var actions = new List<RebalancingAction>();
                
                foreach (var (asset, targetPercentage) in _settings.TargetAllocation)
                {
                    if (!currentAllocation.ContainsKey(asset))
                    {
                        currentAllocation[asset] = 0m;
                    }
                    
                    decimal currentPercentage = currentAllocation[asset];
                    decimal deviation = Math.Abs(currentPercentage - targetPercentage);
                    
                    if (deviation > _settings.RebalanceThreshold)
                    {
                        rebalanceNeeded = true;
                        
                        var action = new RebalancingAction
                        {
                            Asset = asset,
                            CurrentPercentage = currentPercentage,
                            TargetPercentage = targetPercentage,
                            Deviation = deviation,
                            Action = currentPercentage < targetPercentage ? "매수" : "매도",
                            TargetValue = (targetPercentage / 100m) * totalValue
                        };
                        
                        actions.Add(action);
                        
                        OnLog?.Invoke($"[Rebalancing] ⚠️ {asset}: {currentPercentage:F2}% → 목표 {targetPercentage:F2}% (편차: {deviation:F2}%)");
                    }
                }
                
                if (!rebalanceNeeded)
                {
                    OnLog?.Invoke("[Rebalancing] ✅ 포트폴리오가 목표 범위 내에 있습니다.");
                    return;
                }
                
                // 리밸런싱 실행
                if (_settings.AutoRebalance)
                {
                    OnLog?.Invoke("[Rebalancing] 🔄 자동 리밸런싱 시작...");
                    await ExecuteRebalancingAsync(actions, totalValue, ct);
                }
                else
                {
                    OnLog?.Invoke("[Rebalancing] 💡 리밸런싱 권장:");
                    foreach (var action in actions.OrderByDescending(a => a.Deviation))
                    {
                        OnLog?.Invoke($"  - {action.Action} {action.Asset}: ${action.TargetValue:F2} (목표 {action.TargetPercentage:F2}%)");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Rebalancing] ❌ 리밸런싱 분석 오류: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 수동 리밸런싱 요청
        /// </summary>
        public async Task<RebalancingReport> RequestRebalancingAsync(CancellationToken ct = default)
        {
            OnLog?.Invoke("[Rebalancing] 📤 수동 리밸런싱 요청");
            await CheckAndRebalanceAsync(ct);
            return new RebalancingReport { Success = true };
        }
        
        /// <summary>
        /// 거래소 전체의 통합 포트폴리오 조회
        /// </summary>
        private async Task<Dictionary<string, decimal>> GetConsolidatedPortfolioAsync(CancellationToken ct)
        {
            var consolidatedPortfolio = new Dictionary<string, decimal>();
            
            foreach (var (exchangeType, service) in _exchanges)
            {
                try
                {
                    // 각 거래소의 잔고 조회 (USDT, BTC, ETH, BNB 등)
                    var assets = new[] { "USDT", "BTC", "ETH", "BNB", "SOL", "XRP" };
                    
                    foreach (var asset in assets)
                    {
                        try
                        {
                            var balance = await service.GetBalanceAsync(asset, ct);
                            if (balance > 0)
                            {
                                // 자산 가치를 USDT로 환산 (실제로는 현재가 조회 필요)
                                decimal valueInUsdt = balance;
                                
                                if (asset != "USDT")
                                {
                                    // TODO: 실제 가격 조회하여 USDT로 환산
                                    // var price = await service.GetPriceAsync($"{asset}USDT", ct);
                                    // valueInUsdt = balance * price;
                                    
                                    // 임시: BTC는 50000, ETH는 3000 등으로 가정
                                    valueInUsdt = asset switch
                                    {
                                        "BTC" => balance * 50000m,
                                        "ETH" => balance * 3000m,
                                        "BNB" => balance * 500m,
                                        "SOL" => balance * 100m,
                                        "XRP" => balance * 0.5m,
                                        _ => 0m
                                    };
                                }
                                
                                if (!consolidatedPortfolio.ContainsKey(asset))
                                {
                                    consolidatedPortfolio[asset] = 0m;
                                }
                                consolidatedPortfolio[asset] += valueInUsdt;
                            }
                        }
                        catch
                        {
                            // 개별 자산 조회 실패는 무시
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Rebalancing] ⚠️ {exchangeType} 잔고 조회 실패: {ex.Message}");
                }
            }
            
            return consolidatedPortfolio;
        }
        
        /// <summary>
        /// 리밸런싱 실행
        /// </summary>
        private async Task ExecuteRebalancingAsync(
            List<RebalancingAction> actions,
            decimal totalValue,
            CancellationToken ct)
        {
            var report = new RebalancingReport
            {
                StartTime = DateTime.UtcNow,
                TotalValue = totalValue,
                Actions = actions
            };
            
            try
            {
                // [Phase 14] 시뮬레이션 모드 체크
                if (_settings.SimulationMode)
                {
                    OnLog?.Invoke("[Rebalancing] 🔵 시뮬레이션 모드: 실제 주문 없이 시뮬레이션만 수행");
                    
                    // 시뮬레이션: 매도 먼저 실행 (현금 마련)
                    var sellActions = actions.Where(a => a.Action == "매도").OrderByDescending(a => a.Deviation);
                    foreach (var action in sellActions)
                    {
                        OnLog?.Invoke($"[Rebalancing] 📉 [시뮬] {action.Asset} 매도 ({action.CurrentValue:F2} USDT → 0)");
                        report.ExecutedActions.Add(action);
                        await Task.Delay(100, ct); // 시뮬레이션 지연
                    }
                    
                    // 시뮬레이션: 매수 실행
                    var buyActions = actions.Where(a => a.Action == "매수").OrderByDescending(a => a.Deviation);
                    foreach (var action in buyActions)
                    {
                        OnLog?.Invoke($"[Rebalancing] 📈 [시뮬] {action.Asset} 매수 (목표: {action.TargetValue:F2} USDT)");
                        report.ExecutedActions.Add(action);
                        await Task.Delay(100, ct); // 시뮬레이션 지연
                    }
                    
                    report.Success = true;
                    OnLog?.Invoke($"[Rebalancing] ✅ [시뮬] 리밸런싱 완료 (실행된 액션: {report.ExecutedActions.Count}개)");
                }
                else
                {
                    // [Phase 14] 실제 주문 모드
                    OnLog?.Invoke("[Rebalancing] ⚠️ 실제 주문 모드: 실제 자금이 사용됩니다!");
                    
                    // 매도 먼저 실행 (현금 마련)
                    var sellActions = actions.Where(a => a.Action == "매도").OrderByDescending(a => a.Deviation);
                    foreach (var action in sellActions)
                    {
                        OnLog?.Invoke($"[Rebalancing] 📉 {action.Asset} 매도 준비 (수량: {action.CurrentQuantity:F8})...");
                        
                        // TODO: 실제 매도 주문 실행
                        // 여러 거래소에서 가장 유리한 가격의 거래소 선택
                        // var bestExchange = _exchanges.OrderByDescending(e => e.GetBidPrice(action.Asset)).First();
                        // var orderResult = await bestExchange.PlaceOrderAsync(
                        //     action.Asset,
                        //     OrderSide.Sell,
                        //     OrderType.Market,
                        //     action.CurrentQuantity
                        // );
                        
                        // 현재는 NotImplementedException 던지기
                        throw new NotImplementedException(
                            $"실제 매도 주문은 아직 구현되지 않았습니다. " +
                            $"IExchangeService.PlaceOrderAsync 메서드를 사용하여 {action.Asset} {action.CurrentQuantity:F8} 매도를 구현해야 합니다. " +
                            $"시뮬레이션 모드(SimulationMode: true)를 사용하세요."
                        );
                    }
                    
                    // 매수 실행
                    var buyActions = actions.Where(a => a.Action == "매수").OrderByDescending(a => a.Deviation);
                    foreach (var action in buyActions)
                    {
                        OnLog?.Invoke($"[Rebalancing] 📈 {action.Asset} 매수 준비 (금액: {action.TargetValue:F2} USDT)...");
                        
                        // TODO: 실제 매수 주문 실행
                        // var bestExchange = _exchanges.OrderBy(e => e.GetAskPrice(action.Asset)).First();
                        // var currentPrice = await bestExchange.GetCurrentPriceAsync(action.Asset);
                        // var quantity = action.TargetValue / currentPrice;
                        // var orderResult = await bestExchange.PlaceOrderAsync(
                        //     action.Asset,
                        //     OrderSide.Buy,
                        //     OrderType.Market,
                        //     quantity
                        // );
                        
                        throw new NotImplementedException(
                            $"실제 매수 주문은 아직 구현되지 않았습니다. " +
                            $"IExchangeService.PlaceOrderAsync 및 GetCurrentPriceAsync 메서드를 사용하여 {action.Asset} {action.TargetValue:F2} USDT 매수를 구현해야 합니다. " +
                            $"시뮬레이션 모드(SimulationMode: true)를 사용하세요."
                        );
                    }
                    
                    report.Success = true;
                    OnLog?.Invoke($"[Rebalancing] ✅ 리밸런싱 완료 (실행된 액션: {report.ExecutedActions.Count}개)");
                }
            }
            catch (Exception ex)
            {
                report.Success = false;
                report.ErrorMessage = ex.Message;
                OnLog?.Invoke($"[Rebalancing] ❌ 리밸런싱 실행 오류: {ex.Message}");
            }
            finally
            {
                report.EndTime = DateTime.UtcNow;
                
                // [Phase 14] DB 로그 저장
                if (_dbManager != null)
                {
                    _ = Task.Run(async () => await _dbManager.SaveRebalancingLogAsync(report));
                }
                
                OnRebalancingCompleted?.Invoke(report);
                
                // [Phase 14] Telegram 알림 - 리밸런싱 완료
                if (_telegram != null)
                {
                    var actionDescriptions = report.ExecutedActions.Select(a => 
                        $"{a.Action} {a.Asset} (Target: {a.TargetPercentage:F1}%)"
                    ).ToList();
                    
                    _ = Task.Run(async () => await _telegram.SendRebalancingNotificationAsync(
                        report.TotalValue,
                        report.ExecutedActions.Count,
                        report.ExecutedActions.Sum(a => a.TargetValue),
                        report.Success,
                        actionDescriptions
                    ));
                }
            }
        }
    }
    
    /// <summary>
    /// 리밸런싱 액션
    /// </summary>
    public class RebalancingAction
    {
        public string Asset { get; set; } = "";
        public decimal CurrentPercentage { get; set; }
        public decimal TargetPercentage { get; set; }
        public decimal Deviation { get; set; }
        public string Action { get; set; } = ""; // 매수/매도
        public decimal TargetValue { get; set; }
        public decimal CurrentValue { get; set; } = 0m; // [Phase 14] 현재 보유 가치 (USDT)
        public decimal CurrentQuantity { get; set; } = 0m; // [Phase 14] 현재 보유 수량
    }
    
    /// <summary>
    /// 리밸런싱 보고서
    /// </summary>
    public class RebalancingReport
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public decimal TotalValue { get; set; }
        public List<RebalancingAction> Actions { get; set; } = new();
        public List<RebalancingAction> ExecutedActions { get; set; } = new();
    }
}
