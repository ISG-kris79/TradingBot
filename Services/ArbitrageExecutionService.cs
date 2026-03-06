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
    /// 복수 거래소 차익거래 자동화 서비스 (Phase 13)
    /// 거래소 간 가격 차이를 감지하고 자동으로 차익거래를 실행합니다.
    /// </summary>
    public class ArbitrageExecutionService
    {
        private readonly Dictionary<ExchangeType, IExchangeService> _exchanges = new();
        private readonly SemaphoreSlim _executionLock = new SemaphoreSlim(1, 1);
        private readonly DbManager? _dbManager;  // [Phase 14] DB 로깅
        private readonly TelegramService? _telegram;  // [Phase 14] Telegram 알림
        
        public ArbitrageSettings Settings { get; }  // [Phase 14] 공개 속성
        
        private bool _isRunning = false;
        private CancellationTokenSource? _cts;
        
        public event Action<string>? OnLog;
        public event Action<ArbitrageOpportunity>? OnOpportunityDetected;
        public event Action<ArbitrageExecution>? OnExecutionCompleted;
        
        public ArbitrageExecutionService(ArbitrageSettings settings, DbManager? dbManager = null, TelegramService? telegram = null)
        {
            Settings = settings;
            _dbManager = dbManager;
            _telegram = telegram;
        }
        
        /// <summary>
        /// 거래소 추가 (Binance, Bybit 등)
        /// </summary>
        public void AddExchange(ExchangeType type, IExchangeService service)
        {
            _exchanges[type] = service;
            OnLog?.Invoke($"[Arbitrage] 거래소 추가: {type}");
        }
        
        /// <summary>
        /// 차익거래 스캔 시작
        /// </summary>
        public Task StartAsync(List<string> symbols, CancellationToken ct = default)
        {
            if (_isRunning)
            {
                OnLog?.Invoke("[Arbitrage] ⚠️ 이미 실행 중입니다.");
                return Task.CompletedTask;
            }
            
            if (_exchanges.Count < 2)
            {
                OnLog?.Invoke("[Arbitrage] ⚠️ 최소 2개 이상의 거래소가 필요합니다.");
                return Task.CompletedTask;
            }
            
            _isRunning = true;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            
            OnLog?.Invoke($"[Arbitrage] 🚀 차익거래 스캔 시작 (심볼: {symbols.Count}개, 최소 수익률: {Settings.MinProfitPercent}%)");
            
            _ = Task.Run(() => ScanLoopAsync(symbols, token), token);
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// 차익거래 스캔 중지
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _isRunning = false;
            OnLog?.Invoke("[Arbitrage] 🛑 차익거래 스캔 중지");
        }
        
        /// <summary>
        /// 주기적으로 차익거래 기회 스캔
        /// </summary>
        private async Task ScanLoopAsync(List<string> symbols, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    foreach (var symbol in symbols)
                    {
                        if (ct.IsCancellationRequested) break;
                        
                        var opportunity = await DetectOpportunityAsync(symbol, ct);
                        if (opportunity != null && opportunity.ProfitPercent >= Settings.MinProfitPercent)
                        {
                            OnOpportunityDetected?.Invoke(opportunity);
                            
                            // [Phase 14] Telegram 알림 - 차익거래 기회 감지
                            if (_telegram != null)
                            {
                                decimal estimatedProfit = (opportunity.ProfitPercent / 100) * Settings.DefaultQuantity * opportunity.BuyPrice;
                                _ = Task.Run(async () => await _telegram.SendArbitrageOpportunityAsync(
                                    opportunity.Symbol,
                                    opportunity.BuyExchange.ToString(),
                                    opportunity.SellExchange.ToString(),
                                    opportunity.BuyPrice,
                                    opportunity.SellPrice,
                                    opportunity.ProfitPercent,
                                    estimatedProfit
                                ));
                            }
                            
                            // 자동 실행 설정이 켜져 있으면 실행
                            if (Settings.AutoExecute)
                            {
                                await ExecuteArbitrageAsync(opportunity, ct);
                            }
                        }
                    }
                    
                    // 스캔 간격 대기 (기본 5초)
                    await Task.Delay(TimeSpan.FromSeconds(Settings.ScanIntervalSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Arbitrage] ⚠️ 스캔 오류: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
            }
        }
        
        /// <summary>
        /// 차익거래 기회 탐지
        /// </summary>
        private async Task<ArbitrageOpportunity?> DetectOpportunityAsync(string symbol, CancellationToken ct)
        {
            try
            {
                // 모든 거래소에서 가격 조회
                var prices = new Dictionary<ExchangeType, decimal>();
                
                foreach (var (exchangeType, service) in _exchanges)
                {
                    var price = await service.GetPriceAsync(symbol, ct);
                    if (price > 0)
                    {
                        prices[exchangeType] = price;
                    }
                }
                
                if (prices.Count < 2) return null;
                
                // 최저가와 최고가 찾기
                var minExchange = prices.OrderBy(p => p.Value).First();
                var maxExchange = prices.OrderBy(p => p.Value).Last();
                
                decimal priceDiff = maxExchange.Value - minExchange.Value;
                decimal profitPercent = (priceDiff / minExchange.Value) * 100;
                
                // 거래 수수료 고려 (각 거래소 0.1% * 2)
                decimal totalFeePercent = 0.2m;
                decimal netProfitPercent = profitPercent - totalFeePercent;
                
                if (netProfitPercent < Settings.MinProfitPercent)
                    return null;
                
                return new ArbitrageOpportunity
                {
                    Symbol = symbol,
                    BuyExchange = minExchange.Key,
                    SellExchange = maxExchange.Key,
                    BuyPrice = minExchange.Value,
                    SellPrice = maxExchange.Value,
                    ProfitPercent = netProfitPercent,
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Arbitrage] ⚠️ 기회 탐지 오류 ({symbol}): {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 차익거래 실행 (매수 + 매도)
        /// </summary>
        private async Task ExecuteArbitrageAsync(ArbitrageOpportunity opportunity, CancellationToken ct)
        {
            await _executionLock.WaitAsync(ct);
            try
            {
                OnLog?.Invoke($"[Arbitrage] 🔄 차익거래 실행 시작: {opportunity.Symbol} ({opportunity.ProfitPercent:F2}%)");
                
                var execution = new ArbitrageExecution
                {
                    Opportunity = opportunity,
                    StartTime = DateTime.UtcNow,
                    Quantity = Settings.DefaultQuantity
                };
                
                // [Phase 14] 시뮬레이션 모드 체크
                if (Settings.SimulationMode)
                {
                    OnLog?.Invoke($"[Arbitrage] 🔵 시뮬레이션 모드: 실제 주문 없이 시뮬레이션만 수행");
                    
                    // 시뮬레이션: 항상 성공으로 처리
                    execution.BuySuccess = true;
                    execution.SellSuccess = true;
                    execution.Success = true;
                    execution.BuyOrderId = $"SIM-BUY-{DateTime.UtcNow.Ticks}";
                    execution.SellOrderId = $"SIM-SELL-{DateTime.UtcNow.Ticks}";
                    execution.EndTime = DateTime.UtcNow;
                    
                    OnLog?.Invoke($"[Arbitrage] ✅ 시뮬레이션 완료: {opportunity.Symbol} (예상 수익: {opportunity.ProfitPercent:F2}%)");
                    
                    // [Phase 14] DB 로그 저장
                    if (_dbManager != null)
                    {
                        _ = Task.Run(async () => await _dbManager.SaveArbitrageExecutionLogAsync(execution));
                    }
                    
                    OnExecutionCompleted?.Invoke(execution);
                    
                    // [Phase 14] Telegram 알림
                    if (_telegram != null)
                    {
                        decimal actualProfit = (opportunity.ProfitPercent / 100) * execution.Quantity * opportunity.BuyPrice;
                        _ = Task.Run(async () => await _telegram.SendArbitrageExecutionResultAsync(
                            opportunity.Symbol,
                            true,
                            actualProfit,
                            null
                        ));
                    }
                    
                    return;
                }
                
                OnLog?.Invoke($"[Arbitrage] ⚠️ 실제 주문 모드: 실제 자금이 사용됩니다!");
                
                // 1. 저가 거래소에서 구매
                var buyService = _exchanges[opportunity.BuyExchange];
                bool buySuccess = await buyService.PlaceOrderAsync(
                    opportunity.Symbol,
                    "BUY",
                    execution.Quantity,
                    null, // Market order
                    ct
                );
                
                execution.BuySuccess = buySuccess;
                
                if (!buySuccess)
                {
                    OnLog?.Invoke($"[Arbitrage] ❌ 매수 실패: {opportunity.BuyExchange}");
                    execution.Success = false;
                    execution.ErrorMessage = "매수 주문 실패";
                    execution.EndTime = DateTime.UtcNow;
                    
                    // [Phase 14] DB 로그 저장 (실패도 기록)
                    if (_dbManager != null)
                    {
                        _ = Task.Run(async () => await _dbManager.SaveArbitrageExecutionLogAsync(execution));
                    }
                    
                    OnExecutionCompleted?.Invoke(execution);
                    return;
                }
                
                OnLog?.Invoke($"[Arbitrage] ✅ 매수 성공: {opportunity.BuyExchange} @ {opportunity.BuyPrice}");
                
                // 2. 고가 거래소에서 판매
                var sellService = _exchanges[opportunity.SellExchange];
                bool sellSuccess = await sellService.PlaceOrderAsync(
                    opportunity.Symbol,
                    "SELL",
                    execution.Quantity,
                    null, // Market order
                    ct
                );
                
                execution.SellSuccess = sellSuccess;
                execution.Success = buySuccess && sellSuccess;
                execution.EndTime = DateTime.UtcNow;
                
                if (sellSuccess)
                {
                    OnLog?.Invoke($"[Arbitrage] ✅ 매도 성공: {opportunity.SellExchange} @ {opportunity.SellPrice}");
                    OnLog?.Invoke($"[Arbitrage] 🎉 차익거래 완료! 예상 수익: {opportunity.ProfitPercent:F2}%");
                }
                else
                {
                    OnLog?.Invoke($"[Arbitrage] ❌ 매도 실패: {opportunity.SellExchange} (매수 포지션 보유 중)");
                    execution.ErrorMessage = "매도 주문 실패";
                }
                
                // [Phase 14] DB 로그 저장 (실제 거래)
                if (_dbManager != null)
                {
                    _ = Task.Run(async () => await _dbManager.SaveArbitrageExecutionLogAsync(execution));
                }
                
                OnExecutionCompleted?.Invoke(execution);
                
                // [Phase 14] Telegram 알림 - 차익거래 실행 결과
                if (_telegram != null)
                {
                    decimal actualProfit = execution.Success ? (opportunity.ProfitPercent / 100) * execution.Quantity * opportunity.BuyPrice : 0;
                    _ = Task.Run(async () => await _telegram.SendArbitrageExecutionResultAsync(
                        opportunity.Symbol,
                        execution.Success,
                        actualProfit,
                        execution.ErrorMessage
                    ));
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Arbitrage] ❌ 실행 오류: {ex.Message}");
            }
            finally
            {
                _executionLock.Release();
            }
        }
    }
    
    /// <summary>
    /// 차익거래 기회 정보
    /// </summary>
    public class ArbitrageOpportunity
    {
        public string Symbol { get; set; } = "";
        public ExchangeType BuyExchange { get; set; }
        public ExchangeType SellExchange { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal SellPrice { get; set; }
        public decimal ProfitPercent { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    /// <summary>
    /// 차익거래 실행 결과
    /// </summary>
    public class ArbitrageExecution
    {
        public ArbitrageOpportunity Opportunity { get; set; } = null!;
        public decimal Quantity { get; set; }
        public string? BuyOrderId { get; set; }      // [Phase 14] DB 로깅용
        public string? SellOrderId { get; set; }     // [Phase 14] DB 로깅용
        public bool BuySuccess { get; set; }
        public bool SellSuccess { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }    // [Phase 14] DB 로깅용
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }
}
