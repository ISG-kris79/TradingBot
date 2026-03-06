using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Futures.Socket;
using System.Collections.Concurrent;
using TradingBot.Models;
using PositionInfo = TradingBot.Shared.Models.PositionInfo;
using TradeLog = TradingBot.Shared.Models.TradeLog;

namespace TradingBot.Services
{
    public class PositionMonitorService
    {
        private readonly IBinanceRestClient _client;
        private readonly IExchangeService _exchangeService; // [변경]
        private readonly RiskManager _riskManager;
        private readonly MarketDataManager _marketDataManager;
        private readonly DbManager _dbManager;
        private readonly Dictionary<string, PositionInfo> _activePositions;
        private readonly object _posLock;
        private readonly TradingSettings _settings;
        private readonly ConcurrentDictionary<string, DateTime> _blacklistedSymbols;
        private readonly ConcurrentDictionary<string, int> _closingPositions = new();
        private readonly AdvancedExitStopCalculator _advancedExitCalculator;  // [v2.1.18] 지표 결합 익절
        private AIPredictor? _aiPredictor;

        // Events
        public event Action<string>? OnLog = delegate { };
        public event Action<string>? OnAlert = delegate { };
        public event Action<string, decimal, double?>? OnTickerUpdate = delegate { };
        public event Action<string, bool, decimal>? OnPositionStatusUpdate = delegate { };
        public event Action<string, bool, string?>? OnCloseIncompleteStatusChanged = delegate { };
        public event Action? OnTradeHistoryUpdated = delegate { };

        public PositionMonitorService(
            IBinanceRestClient client,
            IExchangeService exchangeService, // [변경]
            RiskManager riskManager,
            MarketDataManager marketDataManager,
            DbManager dbManager,
            Dictionary<string, PositionInfo> activePositions,
            object posLock,
            ConcurrentDictionary<string, DateTime> blacklistedSymbols,
            TradingSettings settings,
            AIPredictor? aiPredictor = null,
            AdvancedExitStopCalculator? advancedExitCalculator = null)  // [v2.1.18] 선택적
        {
            _client = client;
            _exchangeService = exchangeService; // [변경]
            _riskManager = riskManager;
            _marketDataManager = marketDataManager;
            _dbManager = dbManager;
            _activePositions = activePositions;
            _posLock = posLock;
            _blacklistedSymbols = blacklistedSymbols;
            _settings = settings;
            _aiPredictor = aiPredictor;
            _advancedExitCalculator = advancedExitCalculator ?? new AdvancedExitStopCalculator();  // [v2.1.18] 기본값 생성
        }

        public void UpdateAiPredictor(AIPredictor? aiPredictor)
        {
            _aiPredictor = aiPredictor;
        }

        public bool IsCloseInProgress(string symbol)
        {
            return !string.IsNullOrWhiteSpace(symbol)
                && _closingPositions.TryGetValue(symbol, out var count)
                && count > 0;
        }

        private void MarkCloseStarted(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            _closingPositions.AddOrUpdate(symbol, 1, (_, current) => current + 1);
        }

        private void MarkCloseFinished(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            while (_closingPositions.TryGetValue(symbol, out var current))
            {
                if (current <= 1)
                {
                    _closingPositions.TryRemove(symbol, out _);
                    return;
                }

                if (_closingPositions.TryUpdate(symbol, current - 1, current))
                    return;
            }
        }

        public async Task MonitorPositionStandard(
            string symbol,
            decimal entryPrice,
            bool isLong,
            CancellationToken token,
            string mode = "TREND",
            decimal customTakeProfitPrice = 0m,
            decimal customStopLossPrice = 0m)
        {
            bool isSidewaysMode = string.Equals(mode, "SIDEWAYS", StringComparison.OrdinalIgnoreCase);
            bool partialTaken = false;
            bool hybridDcaTaken = false;
            bool hybridDcaDeferredLogged = false;
            decimal leverage = _settings.DefaultLeverage;
            decimal hybridDcaTriggerRoe = -5.0m;
            DateTime positionEntryTime = DateTime.Now;
            bool timeDecayBreakevenApplied = false;
            double timeDecayBreakevenMinutes = 60.0;
            double maxHoldingMinutes = 120.0;
            decimal timeoutExitRoeThreshold = 10.0m;
            DateTime nextAiRecheckTime = DateTime.Now.AddMinutes(60);
            bool squeezeDefenseReduced = false;
            double squeezeDefenseMinutes = 90.0;
            decimal squeezeDefenseMaxRoe = 8.0m;
            decimal squeezeDefenseBbWidthThreshold = 0.60m;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    entryPrice = p.EntryPrice;
                    isLong = p.IsLong;
                    partialTaken = p.TakeProfitStep >= 1;
                    hybridDcaTaken = p.IsAveragedDown;
                    leverage = p.Leverage > 0 ? p.Leverage : leverage;
                    positionEntryTime = p.EntryTime == default ? positionEntryTime : p.EntryTime;
                    squeezeDefenseReduced = p.TakeProfitStep >= 1;
                    nextAiRecheckTime = positionEntryTime.AddMinutes(60);

                    if (customTakeProfitPrice <= 0 && p.TakeProfit > 0)
                        customTakeProfitPrice = p.TakeProfit;

                    if (customStopLossPrice <= 0 && p.StopLoss > 0)
                        customStopLossPrice = p.StopLoss;

                    isSidewaysMode = isSidewaysMode || (p.TakeProfit > 0 && p.StopLoss > 0);
                }
            }

            OnLog?.Invoke($"🔍 {symbol} 감시 시작 (진입가: {entryPrice}) | mode={(isSidewaysMode ? "SIDEWAYS" : "TREND")}");

            // [3단계 본절 보호 & 수익 잠금] 스마트 방어 시스템
            decimal highestROE = -999m;            // 최고 ROE 추적
            decimal protectiveStopPrice = 0m;      // 방어적 스탑 가격
            bool breakEvenActivated = false;       // ROE 10% 본절 보호 활성화
            bool profitLockActivated = false;      // ROE 15% 수익 잠금 활성화
            bool tightTrailingActivated = false;   // ROE 20% 타이트 트레일링 활성화
            
            // 3단계 파라미터 (빠른 본절 방어: 20배 레버리지에서 5% ROE = 0.25% 가격 변동)
            decimal breakEvenROE = 5.0m;           // 1단계: 빠른 본절 확보 (5% ROE)
            decimal profitLockROE = 12.0m;         // 2단계: 수익 잠금
            decimal tightTrailingROE = 18.0m;      // 3단계: 타이트 트레일링
            decimal minLockROE = 15.0m;            // 3단계 최소 수익 (ROE 15%)
            decimal tightGapPercent = 0.0020m;     // 3단계 간격 (0.20%)
            bool hasCustomAbsoluteStop = customStopLossPrice > 0;

            if (hasCustomAbsoluteStop && !isSidewaysMode)
            {
                OnLog?.Invoke($"🛡️ {symbol} ATR 하이브리드 손절 활성화 | 절대손절가={customStopLossPrice:F8}");
            }

            // [추가] 안전장치: 서버사이드 손절 주문 설정 (Stop Market)
            try
            {
                decimal stopPrice = customStopLossPrice > 0
                    ? customStopLossPrice
                    : (isLong
                        ? entryPrice * (1 - _settings.StopLossRoe / leverage / 100)
                        : entryPrice * (1 + _settings.StopLossRoe / leverage / 100));

                // 수량 조회 (락 필요)
                decimal qty = 0;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) qty = Math.Abs(p.Quantity); }

                if (qty > 0)
                {
                    var (success, orderId) = await _exchangeService.PlaceStopOrderAsync(symbol, isLong ? "SELL" : "BUY", qty, stopPrice, token);
                    if (success)
                    {
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                                p.StopOrderId = orderId;
                        }
                        OnLog?.Invoke($"🛡️ {symbol} 서버 손절 주문 설정 완료 (가: {stopPrice:F4})");
                    }
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ 손절 주문 설정 실패: {ex.Message}"); }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                    if (currentPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                        currentPrice = ticker.LastPrice;

                    if (currentPrice == 0) { await Task.Delay(2000, token); continue; }

                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var livePos) && livePos.Leverage > 0)
                            leverage = livePos.Leverage;
                    }

                    decimal priceChangePercent = isLong
                        ? (currentPrice - entryPrice) / entryPrice * 100
                        : (entryPrice - currentPrice) / entryPrice * 100;

                    decimal currentROE = priceChangePercent * leverage;
                    TimeSpan holdingTime = DateTime.Now - positionEntryTime;

                    OnTickerUpdate?.Invoke(symbol, 0m, (double)currentROE);

                    if (_aiPredictor != null && DateTime.Now >= nextAiRecheckTime)
                    {
                        if (TryEvaluateAiReversalExit(symbol, currentPrice, isLong, out string aiRecheckReason))
                        {
                            OnLog?.Invoke($"🧠 [AI 재검증 청산] {symbol} {aiRecheckReason}");
                            await ExecuteMarketClose(symbol, $"AI Reversal Exit ({aiRecheckReason})", token);
                            break;
                        }

                        OnLog?.Invoke($"🧠 [AI 재검증 유지] {symbol} {aiRecheckReason}");
                        nextAiRecheckTime = DateTime.Now.AddMinutes(60);
                    }

                    if (!timeDecayBreakevenApplied && holdingTime.TotalMinutes >= timeDecayBreakevenMinutes)
                    {
                        decimal breakevenStopPrice = entryPrice;
                        bool stopTightened = false;
                        decimal previousStop = 0m;

                        if (hasCustomAbsoluteStop)
                        {
                            previousStop = customStopLossPrice;
                            if ((isLong && customStopLossPrice < breakevenStopPrice) || (!isLong && customStopLossPrice > breakevenStopPrice))
                            {
                                customStopLossPrice = breakevenStopPrice;
                                stopTightened = true;
                            }
                        }
                        else
                        {
                            previousStop = protectiveStopPrice;
                            if (protectiveStopPrice <= 0m || (isLong && protectiveStopPrice < breakevenStopPrice) || (!isLong && protectiveStopPrice > breakevenStopPrice))
                            {
                                protectiveStopPrice = breakevenStopPrice;
                                stopTightened = true;
                            }
                        }

                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                decimal storedStop = p.StopLoss;
                                if (storedStop <= 0m || (isLong && storedStop < breakevenStopPrice) || (!isLong && storedStop > breakevenStopPrice))
                                {
                                    p.StopLoss = breakevenStopPrice;
                                }
                            }
                        }

                        OnLog?.Invoke(stopTightened
                            ? $"🛡️ [시간 기반 보호] {symbol} {holdingTime.TotalMinutes:F0}분 경과 → 손절가를 본절({breakevenStopPrice:F8})로 상향 조정 (이전={previousStop:F8})"
                            : $"🛡️ [시간 기반 보호] {symbol} {holdingTime.TotalMinutes:F0}분 경과 → 기존 스탑이 본절 이상으로 유지 중입니다.");

                        timeDecayBreakevenApplied = true;
                    }

                    // [3단계 스마트 방어 시스템] 수익 보존 로직
                    
                    // 최고 ROE 추적
                    if (currentROE > highestROE)
                    {
                        highestROE = currentROE;
                        
                        // 포지션에 최고 ROE 기록
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                p.HighestROEForTrailing = highestROE;
                            }
                        }
                    }

                    // ═══════════════════════════════════════════════
                    // 1단계: ROE 5% 도달 → 빠른 본절 보호 (20x에서 0.25% 가격변동)
                    // ═══════════════════════════════════════════════
                    if (!breakEvenActivated && highestROE >= breakEvenROE)
                    {
                        breakEvenActivated = true;
                        
                        // 진입가 + 0.05% (수수료 방어 + 본절 확보)
                        protectiveStopPrice = isLong 
                            ? entryPrice * 1.0005m
                            : entryPrice * 0.9995m;
                        
                        OnLog?.Invoke($"🛡️ {symbol} [1단계] 빠른 본절 보호 활성화! ROE {highestROE:F1}% 도달 | 스탑={protectiveStopPrice:F8} (본절가 + 0.05%)");
                    }

                    // ═══════════════════════════════════════════════
                    // 2단계: ROE 12% 도달 → 수익 잠금 (최소 ROE ~5%)
                    // ═══════════════════════════════════════════════
                    if (breakEvenActivated && !profitLockActivated && highestROE >= profitLockROE)
                    {
                        profitLockActivated = true;
                        
                        // 진입가 + 0.25% (ROE 약 5% 지점)
                        protectiveStopPrice = isLong 
                            ? entryPrice * 1.0025m
                            : entryPrice * 0.9975m;
                        
                        OnLog?.Invoke($"💰 {symbol} [2단계] 수익 잠금 활성화! ROE {highestROE:F1}% 도달 | 스탑={protectiveStopPrice:F8} (최소 ROE 5% 확보)");
                    }

                    // ═══════════════════════════════════════════════
                    // 3단계: ROE 18% 도달 → 타이트한 트레일링 (최소 ROE 15%)
                    // ═══════════════════════════════════════════════
                    if (profitLockActivated && !tightTrailingActivated && highestROE >= tightTrailingROE)
                    {
                        tightTrailingActivated = true;
                        
                        // ROE 15% = 0.75% 가격 변동 (20배 레버리지)
                        decimal minLockPriceChange = minLockROE / leverage / 100;
                        protectiveStopPrice = isLong 
                            ? entryPrice * (1 + minLockPriceChange)
                            : entryPrice * (1 - minLockPriceChange);
                        
                        OnLog?.Invoke($"🎯 {symbol} [3단계] 타이트 트레일링 활성화! ROE {highestROE:F1}% 돌파 | 스탑={protectiveStopPrice:F8} (최소 ROE 15%)");

                        // [v2.1.18] 지표 기반 익절 준비: ROE 20% 도달 시 지표 모니터링 시작
                        OnLog?.Invoke($"🔍 {symbol} 지표 기반 익절 시스템 활성화 [3단계 타이트 트레일링과 병행]");
                    }

                    // ═══════════════════════════════════════════════
                    // 3단계 활성화 후: 최고가 추적 + 0.15% 타이트 트레일링
                    // ═══════════════════════════════════════════════
                    if (tightTrailingActivated)
                    {
                        decimal currentPriceForTrailing = currentPrice;
                        
                        if (isLong)
                        {
                            // 롱: 최고가 - 0.15% 간격
                            decimal newStopPrice = currentPriceForTrailing * (1 - tightGapPercent);
                            
                            // ROE 18% 아래로는 절대 내려가지 않음
                            decimal minLockPrice = entryPrice * (1 + minLockROE / leverage / 100);
                            if (newStopPrice < minLockPrice)
                                newStopPrice = minLockPrice;
                            
                            // 스탑 가격 상승만 허용 (절대 뒤로 물러나지 않음)
                            if (newStopPrice > protectiveStopPrice)
                            {
                                decimal oldStop = protectiveStopPrice;
                                protectiveStopPrice = newStopPrice;
                                OnLog?.Invoke($"📈 {symbol} 트레일링 스톱 갱신: {oldStop:F8} → {protectiveStopPrice:F8} (현재가: {currentPriceForTrailing:F8})");
                            }
                        }
                        else // 숏 포지션
                        {
                            // 숏: 최저가 + 0.15% 간격
                            decimal newStopPrice = currentPriceForTrailing * (1 + tightGapPercent);
                            
                            // ROE 18% 위로는 절대 올라가지 않음
                            decimal minLockPrice = entryPrice * (1 - minLockROE / leverage / 100);
                            if (newStopPrice > minLockPrice)
                                newStopPrice = minLockPrice;
                            
                            // 스탑 가격 하락만 허용 (절대 뒤로 물러나지 않음)
                            if (protectiveStopPrice == 0m || newStopPrice < protectiveStopPrice)
                            {
                                decimal oldStop = protectiveStopPrice;
                                protectiveStopPrice = newStopPrice;
                                if (oldStop > 0)
                                    OnLog?.Invoke($"📉 {symbol} 트레일링 스톱 갱신: {oldStop:F8} → {protectiveStopPrice:F8} (현재가: {currentPriceForTrailing:F8})");
                            }
                        }

                        // [v2.1.18] 지표 기반 익절 신호 모니터링 (3단계 타이트 트레일링 중)
                        if (highestROE >= tightTrailingROE)
                        {
                            // 지표 데이터 수집 (AdvancedIndicators 등에서 제공)
                            var tech = BuildTechnicalDataForExitSignal(symbol, currentPrice, entryPrice, isLong);
                            
                            if (tech != null)
                            {
                                // 지표 기반 익절 신호 계산
                                var exitSignal = _advancedExitCalculator.CalculateAdvancedExitStop(
                                    protectiveStopPrice,
                                    tech,
                                    isLong);

                                // [즉시 익절 1] BB 회귀 신호
                                if (exitSignal.ShouldTakeProfitNow)
                                {
                                    OnLog?.Invoke($"⚠️ {symbol} [지표 익절] {exitSignal.SignalSummary}");
                                    decimal exitROE = isLong
                                        ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                        : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                                    
                                    await ExecuteMarketClose(symbol, 
                                        $"Advanced Exit Signal: {exitSignal.SignalSummary} (ROE {exitROE:F1}%)", 
                                        token);
                                    break;
                                }

                                // [즉시 익절 2] 여러 신호 동시 발생 (3개 이상)
                                if (_advancedExitCalculator.ShouldExecuteImmediateExit(tech, exitSignal))
                                {
                                    OnLog?.Invoke($"🚨 {symbol} [다중 지표 신호] {exitSignal.SignalSummary} - 즉시 익절 실행!");
                                    decimal exitROE = isLong
                                        ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                        : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                                    
                                    await ExecuteMarketClose(symbol, 
                                        $"Multi-Signal Exit: {exitSignal.SignalSummary} (ROE {exitROE:F1}%)", 
                                        token);
                                    break;
                                }

                                // [지표 기반 스탑 갱신] 추천 스탑이 더 타이트하면 적용
                                if (isLong && exitSignal.RecommendedStopPrice > protectiveStopPrice)
                                {
                                    protectiveStopPrice = exitSignal.RecommendedStopPrice;
                                    OnLog?.Invoke($"🔧 {symbol} 지표 기반 스탑 갱신: {exitSignal.SignalSummary} | 새 스탑={protectiveStopPrice:F8}");
                                }
                                else if (!isLong && exitSignal.RecommendedStopPrice < protectiveStopPrice)
                                {
                                    protectiveStopPrice = exitSignal.RecommendedStopPrice;
                                    OnLog?.Invoke($"🔧 {symbol} 지표 기반 스탑 갱신: {exitSignal.SignalSummary} | 새 스탑={protectiveStopPrice:F8}");
                                }
                            }
                        }
                    }

                    // ═══════════════════════════════════════════════
                    // 방어적 스탑 체크: 현재가가 스탑 가격 터치 시 청산
                    // ═══════════════════════════════════════════════
                    if (protectiveStopPrice > 0)
                    {
                        bool stopHit = isLong 
                            ? (currentPrice <= protectiveStopPrice)
                            : (currentPrice >= protectiveStopPrice);
                        
                        if (stopHit)
                        {
                            decimal finalROE = isLong
                                ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                            
                            string stage = tightTrailingActivated ? "3단계 타이트 트레일링" 
                                         : profitLockActivated ? "2단계 수익 잠금" 
                                         : "1단계 본절 보호";
                            
                            OnLog?.Invoke($"[청산 트리거] {symbol} {stage} 스톱 발동! | 현재가={currentPrice:F8}, 스탑={protectiveStopPrice:F8} | 최종ROE={finalROE:F1}%");
                            await ExecuteMarketClose(symbol, $"Smart Protective Stop [{stage}] (ROE {finalROE:F1}%)", token);
                            break;
                        }
                    }

                    if (hasCustomAbsoluteStop && !isSidewaysMode)
                    {
                        bool customStopHit = (isLong && currentPrice <= customStopLossPrice) || (!isLong && currentPrice >= customStopLossPrice);
                        if (customStopHit)
                        {
                            OnLog?.Invoke($"[청산 트리거] {symbol} ATR 하이브리드 손절 | 현재가={currentPrice:F8}, SL={customStopLossPrice:F8}");
                            await ExecuteMarketClose(symbol, $"ATR 2.5x Hybrid Stop ({currentPrice:F8})", token);
                            break;
                        }
                    }

                    if (isSidewaysMode)
                    {
                        bool customStopHit = customStopLossPrice > 0 &&
                            ((isLong && currentPrice <= customStopLossPrice) || (!isLong && currentPrice >= customStopLossPrice));

                        if (customStopHit)
                        {
                            OnLog?.Invoke($"[청산 트리거] {symbol} SIDEWAYS 커스텀 손절 | 현재가={currentPrice:F8}, SL={customStopLossPrice:F8}");
                            await ExecuteMarketClose(symbol, $"SIDEWAYS 커스텀 손절 ({currentPrice:F8})", token);
                            break;
                        }

                        bool customTpHit = customTakeProfitPrice > 0 &&
                            ((isLong && currentPrice >= customTakeProfitPrice) || (!isLong && currentPrice <= customTakeProfitPrice));

                        if (!partialTaken && customTpHit)
                        {
                            await ExecutePartialClose(symbol, 0.5m, token);
                            partialTaken = true;

                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    p.TakeProfitStep = 1;
                                    p.BreakevenPrice = entryPrice;
                                    p.StopLoss = entryPrice;
                                }
                            }

                            OnLog?.Invoke($"💰 {symbol} SIDEWAYS 중단선 도달: 50% 부분익절, 잔여는 본절가({entryPrice:F8}) 보호");
                        }

                        if (partialTaken)
                        {
                            bool breakEvenHit = (isLong && currentPrice <= entryPrice) || (!isLong && currentPrice >= entryPrice);
                            if (breakEvenHit)
                            {
                                OnLog?.Invoke($"[청산 트리거] {symbol} SIDEWAYS 잔여 본절 청산 | 현재가={currentPrice:F8}, 본절={entryPrice:F8}");
                                await ExecuteMarketClose(symbol, "SIDEWAYS 본절가 청산", token);
                                break;
                            }
                        }
                    }

                    if (!isSidewaysMode && isLong && !hybridDcaTaken && currentROE <= hybridDcaTriggerRoe)
                    {
                        if (TryShouldExecuteHybridLongDca(symbol, currentPrice, out string dcaReason))
                        {
                            OnAlert?.Invoke($"💧 {symbol} 하이브리드 DCA 시도 (ROE: {currentROE:F2}%) | {dcaReason}");
                            await ExecuteAverageDown(symbol, token);
                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    entryPrice = p.EntryPrice;
                                    hybridDcaTaken = p.IsAveragedDown;
                                }
                            }
                            hybridDcaDeferredLogged = false;
                            continue;
                        }

                        if (!hybridDcaDeferredLogged)
                        {
                            OnLog?.Invoke($"⏸️ {symbol} 하이브리드 DCA 보류 | {dcaReason}");
                            hybridDcaDeferredLogged = true;
                        }
                    }
                    else
                    {
                        hybridDcaDeferredLogged = false;
                    }

                    if (!squeezeDefenseReduced && holdingTime.TotalMinutes >= squeezeDefenseMinutes && currentROE < squeezeDefenseMaxRoe)
                    {
                        if (TryGetCurrentBbWidthPct(symbol, out decimal currentBbWidthPct) && currentBbWidthPct > 0m && currentBbWidthPct <= squeezeDefenseBbWidthThreshold)
                        {
                            await ExecutePartialClose(symbol, 0.5m, token);
                            partialTaken = true;
                            squeezeDefenseReduced = true;

                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    p.TakeProfitStep = Math.Max(p.TakeProfitStep, 1);
                                    p.BreakevenPrice = entryPrice;
                                    p.StopLoss = entryPrice;
                                }
                            }

                            OnLog?.Invoke($"📦 [스퀴즈 방어 축소] {symbol} {holdingTime.TotalMinutes:F0}분 경과 + BB폭 {currentBbWidthPct:F2}% → 50% 축소 후 본절 보호 전환");
                            continue;
                        }
                    }

                    if (holdingTime.TotalMinutes >= maxHoldingMinutes && currentROE < timeoutExitRoeThreshold)
                    {
                        OnLog?.Invoke($"⏳ [시간 초과 종료] {symbol} {holdingTime.TotalMinutes:F0}분 경과 | 현재ROE={currentROE:F2}% < {timeoutExitRoeThreshold:F2}% → 추세 소멸로 포지션 정리");
                        await ExecuteMarketClose(symbol, $"TimeOut Exit ({holdingTime.TotalMinutes:F0}m, ROE {currentROE:F2}%)", token);
                        break;
                    }

                    if (currentROE >= _settings.TargetRoe)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 메이저 익절 조건 충족 | 방향={(isLong ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 목표ROE={_settings.TargetRoe:F2}%");
                        await ExecuteMarketClose(symbol, $"메이저 익절 달성 ({currentROE:F2}%)", token);
                        break;
                    }
                    else if (!hasCustomAbsoluteStop && currentROE <= -_settings.StopLossRoe)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 메이저 손절 조건 충족 | 방향={(isLong ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 손절ROE=-{_settings.StopLossRoe:F2}%");
                        await ExecuteMarketClose(symbol, $"메이저 손절 실행 (현재 {currentROE:F2}%, 기준 -{_settings.StopLossRoe:F2}%)", token);
                        break;
                    }

                    lock (_posLock)
                    {
                        if (!_activePositions.ContainsKey(symbol))
                        {
                            OnLog?.Invoke($"ℹ️ {symbol} 포지션이 외부에서 종료됨.");
                            break;
                        }
                    }

                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 모니터링 에러: {ex.Message}");
                    await Task.Delay(5000, token);
                }
            }
        }

        public async Task MonitorPumpPositionShortTerm(string symbol, decimal entryPrice, string strategyName, double atr, CancellationToken token)
        {
            bool isLongPosition;
            bool isPumpPosition;
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var pos))
                {
                    entryPrice = pos.EntryPrice;
                    isLongPosition = pos.IsLong;
                    isPumpPosition = pos.IsPumpStrategy;
                }
                else return;
            }

            OnLog?.Invoke($"🔍 {symbol} Pump 감시 시작 | 방향: {(isLongPosition ? "LONG" : "SHORT")} | 진입가: {entryPrice:F4}");

            DateTime startTime = DateTime.Now;
            decimal highestROE = -999m;
            decimal leverage = _settings.PumpLeverage;

            // [ElliottWave 3파 기반 익절/손절] 
            PositionInfo? elliotWavePos = null;
            decimal fib0618Level = 0;
            decimal wave1LowPrice = 0;
            decimal wave1HighPrice = 0;
            decimal fib0786Level = 0;
            decimal fib1618Target = 0;
            decimal fib2618Target = 0;
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    elliotWavePos = p;
                    leverage = p.Leverage > 0 ? p.Leverage : leverage;
                    fib0618Level = p.Fib0618Level;
                    wave1LowPrice = p.Wave1LowPrice;
                    wave1HighPrice = p.Wave1HighPrice;
                    fib0786Level = p.Fib0786Level;
                    fib1618Target = p.Fib1618Target;
                    fib2618Target = p.TakeProfit;
                }
            }

            decimal stopLossROE = _settings.StopLossRoe;
            decimal trailingStartROE = _settings.TrailingStartRoe;
            decimal trailingDropROE = _settings.TrailingDropRoe;
            decimal averageDownROE = -5.0m;
            bool isBreakEvenTriggered = false;
            decimal partialTakeProfitROE = 10.0m;
            decimal pumpTp1Roe = _settings.PumpTp1Roe > 0 ? _settings.PumpTp1Roe : 20.0m;
            decimal pumpTp2Roe = _settings.PumpTp2Roe > 0 ? _settings.PumpTp2Roe : 50.0m;
            decimal pumpTimeStopMinutes = _settings.PumpTimeStopMinutes > 0 ? _settings.PumpTimeStopMinutes : 15.0m;

            if (atr > 0)
            {
                decimal targetPriceMove = (decimal)atr * 3.0m;
                decimal dynamicROE = (targetPriceMove / entryPrice) * leverage * 100;
                trailingStartROE = Math.Clamp(dynamicROE, 15.0m, 60.0m);
                OnLog?.Invoke($"🎯 {symbol} 목표가 동적 설정 (ATR:{atr:F2}): ROE {trailingStartROE:F1}%");
            }

            if (strategyName == "📈 RSI REBOUND" || strategyName == "🔄 BB RETURN")
            {
                trailingStartROE = Math.Min(trailingStartROE, 15.0m);
                stopLossROE = 10.0m;
            }

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token);

                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                if (currentPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                    currentPrice = ticker.LastPrice;
                if (currentPrice == 0) continue;
                decimal priceChangePercent = isLongPosition
                    ? (currentPrice - entryPrice) / entryPrice * 100
                    : (entryPrice - currentPrice) / entryPrice * 100;
                decimal currentROE = priceChangePercent * leverage;

                if ((DateTime.Now - startTime).TotalMinutes >= (double)pumpTimeStopMinutes && currentROE >= -2.0m && currentROE <= 2.0m)
                {
                    OnLog?.Invoke($"[청산 트리거] {symbol} PUMP 타임스탑 발동 | 기준={pumpTimeStopMinutes:F1}분, 경과={(DateTime.Now - startTime).TotalMinutes:F1}분, 현재ROE={currentROE:F2}%");
                    await ExecuteMarketClose(symbol, $"⏱️ 타임스탑 ({pumpTimeStopMinutes:F1}분 횡보)", token);
                    break;
                }

                OnTickerUpdate?.Invoke(symbol, 0m, (double)currentROE);

                bool isAveraged = false;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) isAveraged = p.IsAveragedDown; }

                // [20배 PUMP 즉시 손절] 진입 후 다음 5분봉이 BB중단을 음봉으로 하향 돌파 마감 시 즉시 청산
                if (isPumpPosition && (DateTime.Now - startTime).TotalMinutes >= 5)
                {
                    try
                    {
                        if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                        {
                            List<IBinanceKline> snapshot;
                            lock (recentCandles)
                            {
                                snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전] 스냅샷 복사
                            }
                            
                            if (snapshot.Count < 20) continue;
                            var last = snapshot[snapshot.Count - 1];
                            var bb = IndicatorCalculator.CalculateBB(snapshot, 20, 2);
                            decimal mid = (decimal)bb.Mid;
                            bool bearishCloseBelowMid = (decimal)last.ClosePrice < (decimal)last.OpenPrice && (decimal)last.ClosePrice < mid;
                            if (bearishCloseBelowMid)
                            {
                                OnLog?.Invoke($"[청산 트리거] {symbol} 급등 추세 실패(BB중단 하향 음봉마감)");
                                await ExecuteMarketClose(symbol, "🛑 BB중단 하향 음봉마감 (즉시손절)", token);
                                break;
                            }
                        }
                    }
                    catch { }
                }

                // ============================================================
                // [ElliottWave 3단계 부분익절] (Wave1High → Fib1618 → 잔량)
                // ============================================================
                if (elliotWavePos != null && isLongPosition &&
                    wave1HighPrice > 0 && fib1618Target > 0)
                {
                    // 1차 익절: 전고점(1.0) 도달 OR 설정 ROE 달성 시 50% 매도
                    bool tp1Condition = currentPrice >= wave1HighPrice || currentROE >= pumpTp1Roe;
                    if (elliotWavePos.PartialProfitStage == 0 && tp1Condition)
                    {
                        await ExecutePartialClose(symbol, 0.50m, token); // 50% 매도
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                p.PartialProfitStage = 1;
                                p.BreakevenPrice = entryPrice; // 본절가 설정
                            }
                        }
                        string tp1Trigger = currentPrice >= wave1HighPrice ? $"Fib1.0(전고점) {wave1HighPrice:F8}" : $"ROE {pumpTp1Roe:F1}% 달성 ({currentROE:F1}%)";
                        OnAlert?.Invoke($"💰 {symbol} 1차 익절 (50%) | 트리거: {tp1Trigger} | 본절가 설정: {entryPrice:F8}");
                        OnLog?.Invoke($"✅ {symbol} 1차 익절 완료(Stage=1), 다음 목표: Fib1.618 {fib1618Target:F8} 또는 ROE {pumpTp2Roe:F1}%");
                    }

                    // 2차 익절: Fib1.618 도달 OR 설정 ROE 달성 시 30% 매도
                    bool tp2Condition = currentPrice >= fib1618Target || currentROE >= pumpTp2Roe;
                    if (elliotWavePos.PartialProfitStage == 1 && tp2Condition)
                    {
                        decimal secondRatio = 0.30m;
                        try
                        {
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                            {
                                List<IBinanceKline> snapshot;
                                lock (recentCandles)
                                {
                                    snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전]
                                }
                                if (snapshot.Count < 20) continue;
                                var rsiNow = IndicatorCalculator.CalculateRSI(snapshot, 14);
                                if (rsiNow >= 80) secondRatio = 0.40m; // 초과매수면 익절 강도 강화
                            }
                        }
                        catch { }

                        await ExecutePartialClose(symbol, secondRatio, token);
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                p.PartialProfitStage = 2;
                                p.BreakevenPrice = wave1HighPrice > 0 ? wave1HighPrice : p.BreakevenPrice; // 마디가 추격 (Fib1.0로 상향)
                            }
                        }
                        string tp2Trigger = currentPrice >= fib1618Target ? $"Fib1.618 {fib1618Target:F8}" : $"ROE {pumpTp2Roe:F1}% 달성 ({currentROE:F1}%)";
                        OnAlert?.Invoke($"💰 {symbol} 2차 익절 ({secondRatio * 100m:F0}%) | 트리거: {tp2Trigger} | 스탑 상향: Fib1.0");
                        OnLog?.Invoke($"✅ {symbol} 2차 익절 완료(Stage=2), 잔량은 밴드라이딩/다이버전스 추격 관리");
                    }

                    // 최종 익절: Fib2.618 도달 시 잔량 정리
                    if (elliotWavePos.PartialProfitStage >= 2 && fib2618Target > 0 && currentPrice >= fib2618Target)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} Fib2.618({fib2618Target:F8}) 도달, 잔량 최종 정리");
                        await ExecuteMarketClose(symbol, "🎯 Fib2.618 최종 익절", token);
                        break;
                    }
                }

                // ============================================================
                // [ElliottWave 절대 손절선] (4가지)
                // ============================================================
                if (elliotWavePos != null && isLongPosition)
                {
                    bool shouldAbsoluteStop = false;
                    string stopReason = "";

                    // 1. Wave1LowPrice 이탈 (절대 손절선 1)
                    if (wave1LowPrice > 0 && currentPrice <= wave1LowPrice)
                    {
                        shouldAbsoluteStop = true;
                        stopReason = $"Wave1Low {wave1LowPrice:F8} 이탈";
                    }

                    // 2. Fib0.618/논리손절 이탈 (20배 핵심 손절선)
                    if (!shouldAbsoluteStop && fib0618Level > 0 && currentPrice <= fib0618Level)
                    {
                        shouldAbsoluteStop = true;
                        stopReason = $"Fib0.618 {fib0618Level:F8} 이탈";
                    }

                    if (!shouldAbsoluteStop && fib0786Level > 0 && currentPrice <= fib0786Level)
                    {
                        shouldAbsoluteStop = true;
                        stopReason = $"논리손절 {fib0786Level:F8} 이탈";
                    }

                    // 3. BB 하단 이탈 (절대 손절선 3) - 마켓 데이터 캐시에서 최신 캔들 조회
                    if (!shouldAbsoluteStop)
                    {
                        try
                        {
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                            {
                                List<IBinanceKline> snapshot;
                                lock (recentCandles)
                                {
                                    snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전]
                                }
                                if (snapshot.Count < 20) continue;
                                var bbAnalysis = IndicatorCalculator.CalculateBB(snapshot, 20, 2);
                                decimal bbLower = (decimal)bbAnalysis.Lower;
                                if (currentPrice < bbLower)
                                {
                                    shouldAbsoluteStop = true;
                                    stopReason = $"BB하단 {bbLower:F8} 이탈";
                                }
                            }
                        }
                        catch { /* BB 계산 실패 무시 */ }
                    }

                    // 4. 타임스탑: 15~25분 횡보 시 정리 (절대 손절선 4)
                    if (!shouldAbsoluteStop)
                    {
                        double timeCutMinutes = 20; // 20분 횡보
                        if ((DateTime.Now - startTime).TotalMinutes >= timeCutMinutes &&
                            Math.Abs(currentROE) <= 2.0m) // 수익/손실이 ±2% 이내
                        {
                            shouldAbsoluteStop = true;
                            stopReason = $"타임스탑 ({timeCutMinutes}분 횡보, ROE={currentROE:F2}%)";
                        }
                    }

                    if (shouldAbsoluteStop && elliotWavePos.PartialProfitStage < 2)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 절대 손절선 발동! | 이유: {stopReason} | 현재ROE: {currentROE:F2}%");
                        await ExecuteMarketClose(symbol, $"🛑 {stopReason}", token);
                        break;
                    }
                }

                // [수정] 물타기 포지션의 손절폭 완화: 20% → 12% (과도한 손로 방지)

                // ============================================================
                // [레벨업 스탑 + BB 중단 추격] 2차 익절 이후 (PartialProfitStage=2)
                // ============================================================
                if (elliotWavePos?.PartialProfitStage == 2 && wave1HighPrice > 0)
                {
                    bool shouldLevelUpStop = false;
                    string levelUpReason = "";

                    // 1. BB 중단(20EMA) 추격 스탑: 종가가 BB 중단 아래로 내려오면 즉시 청산
                    try
                    {
                        if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                        {
                            List<IBinanceKline> snapshot;
                            lock (recentCandles)
                            {
                                snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전] 스냅샷 복사
                            }
                            
                            if (snapshot.Count < 20) continue;
                            var lastCandle = snapshot[snapshot.Count - 1];
                            decimal lastClose = (decimal)lastCandle.ClosePrice;

                            var bbAnalysis = IndicatorCalculator.CalculateBB(snapshot, 20, 2);
                            decimal bbMiddle = (decimal)bbAnalysis.Mid; // 20EMA

                            // 종가가 BB 중단 아래: 추세가 죽었다고 판단 → 즉시 청산
                            if (lastClose < bbMiddle)
                            {
                                shouldLevelUpStop = true;
                                levelUpReason = $"BB 중단({bbMiddle:F8}) 이탈 (종가: {lastClose:F8})";
                            }
                        }
                    }
                    catch { /* BB 계산 실패 무시 */ }

                    // 2. 레벨업 스탑: Wave1HighPrice(1차 익절가) 아래로 내려오면 청산
                    if (!shouldLevelUpStop && currentPrice < wave1HighPrice)
                    {
                        shouldLevelUpStop = true;
                        levelUpReason = $"레벨업 손절 (Wave1High {wave1HighPrice:F8} 이탈)";
                    }

                    // 3. 밴드라이딩 종료 + RSI 하락 다이버전스: 상단선 안쪽 복귀 + RSI 하락 시 전량 정리
                    if (!shouldLevelUpStop)
                    {
                        try
                        {
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 30)
                            {
                                List<IBinanceKline> snapshot;
                                lock (recentCandles)
                                {
                                    snapshot = recentCandles.TakeLast(30).ToList(); // [동시성 안전]
                                }
                                
                                if (snapshot.Count < 30) continue;
                                var candles = snapshot;
                                if (candles.Count < 2)
                                {
                                    OnLog?.Invoke($"⚠️ {symbol} candles 부족 (BB 분석, Count: {candles.Count})");
                                    continue;
                                }
                                var bbNow = IndicatorCalculator.CalculateBB(candles, 20, 2);
                                var prevCandles = candles.Take(candles.Count - 1).ToList();
                                var bbPrev = IndicatorCalculator.CalculateBB(prevCandles, 20, 2);

                                decimal lastClose = (decimal)candles[^1].ClosePrice;
                                decimal prevClose = (decimal)candles[^2].ClosePrice;
                                bool upperBandBackInside = prevClose >= (decimal)bbPrev.Upper && lastClose < (decimal)bbNow.Upper;

                                double rsiNow = IndicatorCalculator.CalculateRSI(candles, 14);
                                double rsiPrev = IndicatorCalculator.CalculateRSI(prevCandles, 14);
                                bool bearishRsiDivergenceHint = lastClose > prevClose && rsiNow < rsiPrev;

                                if (upperBandBackInside && bearishRsiDivergenceHint)
                                {
                                    shouldLevelUpStop = true;
                                    levelUpReason = $"밴드라이딩 종료+RSI다이버전스 (RSI {rsiPrev:F1}->{rsiNow:F1})";
                                }
                            }
                        }
                        catch { }
                    }

                    if (shouldLevelUpStop)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 레벨업/추격 스탑 발동! | 이유: {levelUpReason} | 현재ROE: {currentROE:F2}%");
                        await ExecuteMarketClose(symbol, $"🚀 {levelUpReason}", token);
                        break;
                    }
                }

                if (isAveraged) stopLossROE = 12.0m;

                // ============================================================
                // [본절가 스탑] ElliottWave 1차 익절 후 진입가로 손절 상향
                // ============================================================
                if (elliotWavePos?.BreakevenPrice > 0 && isLongPosition)
                {
                    if (currentPrice <= elliotWavePos.BreakevenPrice)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 본절가 손절 발동 | BreakevenPrice: {elliotWavePos.BreakevenPrice:F8}, 현재가: {currentPrice:F8}");
                        await ExecuteMarketClose(symbol, "🛡️ 본절가 손절 (진입가 손절 상향)", token);
                        break;
                    }
                }

                if (!isBreakEvenTriggered && currentROE >= 3.0m)
                {
                    isBreakEvenTriggered = true;
                    OnAlert?.Invoke($"🛡️ {symbol} Break Even 발동! (ROE 3% 도달 -> 손절라인 본절 이동)");
                }

                if (isBreakEvenTriggered) stopLossROE = 0.0m;

                int currentTpStep = 0;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) currentTpStep = p.TakeProfitStep; }

                if (currentTpStep == 0 && currentROE >= partialTakeProfitROE)
                {
                    await ExecutePartialClose(symbol, 0.5m, token);
                    lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) p.TakeProfitStep = 1; }
                    if (!isBreakEvenTriggered) { isBreakEvenTriggered = true; stopLossROE = 0.0m; }
                    OnAlert?.Invoke($"💰 {symbol} 1차 익절 (50%) & 본절 확정 (ROE: {currentROE:F2}%)");
                }

                if (!isAveraged && !isBreakEvenTriggered && currentROE <= averageDownROE)
                {
                    OnAlert?.Invoke($"💧 {symbol} 물타기 시도 (ROE: {currentROE:F2}%)");
                    await ExecuteAverageDown(symbol, token);
                    lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) entryPrice = p.EntryPrice; }
                    continue;
                }

                if (currentROE <= -stopLossROE)
                {
                    string exitReason = isBreakEvenTriggered ? "🛡️ Break Even (본절)" : $"ROE 손절 (-{stopLossROE}%)";
                    OnLog?.Invoke($"[청산 트리거] {symbol} 손절 조건 충족 | 방향={(isLongPosition ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 기준ROE=-{stopLossROE:F2}%, BreakEven={(isBreakEvenTriggered ? "ON" : "OFF")}");
                    await ExecuteMarketClose(symbol, exitReason, token);
                    break;
                }

                if (currentROE > highestROE) highestROE = currentROE;

                if (highestROE >= 10.0m && highestROE < 15.0m && trailingDropROE > 4.0m)
                {
                    trailingDropROE = 4.0m;
                    if (trailingStartROE > 10.0m) trailingStartROE = 10.0m;
                    OnAlert?.Invoke($"🏃 {symbol} Dynamic Trailing 가동 (ROE 10%↑ -> 간격 4%로 설정)");
                }
                else if (highestROE >= 15.0m && trailingDropROE != 5.0m)
                {
                    trailingDropROE = 5.0m;
                    if (trailingStartROE > 10.0m) trailingStartROE = 10.0m;
                    OnAlert?.Invoke($"🚀 {symbol} 슈퍼 트레일링 가동 (ROE 15%↑ -> 간격 5%로 설정)");
                }

                if (highestROE >= trailingStartROE)
                {
                    if (highestROE - currentROE >= trailingDropROE)
                    {
                        PositionInfo? pos = null;
                        lock (_posLock) { _activePositions.TryGetValue(symbol, out pos); }

                        if (pos != null && pos.TakeProfitStep == 0)
                        {
                            await ExecutePartialClose(symbol, 0.5m, token);
                            lock (_posLock) { if (_activePositions.ContainsKey(symbol)) _activePositions[symbol].TakeProfitStep = 1; }
                            OnAlert?.Invoke($"💰 {symbol} 1차 트레일링 익절 (50%) | ROE: {currentROE:F1}%");
                            // [수정] highestROE 리셋 제거 - 남은 50%도 원래 최고가 기준으로 추적
                        }
                        else
                        {
                            OnLog?.Invoke($"[청산 트리거] {symbol} 트레일링 최종청산 조건 충족 | 방향={(isLongPosition ? "LONG" : "SHORT")}, 최고ROE={highestROE:F1}%, 현재ROE={currentROE:F1}%, 간격={trailingDropROE:F1}%");
                            await ExecuteMarketClose(symbol, $"ROE 트레일링 최종 익절 (최고:{highestROE:F1}% / 현재:{currentROE:F1}%)", token);
                            break;
                        }
                    }
                }

                lock (_posLock)
                {
                    if (!_activePositions.ContainsKey(symbol))
                    {
                        OnLog?.Invoke($"ℹ️ {symbol} Pump 감시 중 포지션이 외부에서 종료됨.");
                        break;
                    }
                }
            }
        }

        private bool TryShouldExecuteHybridLongDca(string symbol, decimal currentPrice, out string reason)
        {
            reason = string.Empty;

            PositionInfo? localPosition;
            lock (_posLock)
            {
                _activePositions.TryGetValue(symbol, out localPosition);
            }

            if (localPosition == null)
            {
                reason = "내부 포지션 정보 없음";
                return false;
            }

            if (!localPosition.IsLong)
            {
                reason = "SHORT 포지션은 하이브리드 DCA 비활성";
                return false;
            }

            if (!localPosition.IsHybridMidBandLongEntry)
            {
                reason = $"중단 롱 진입 포지션 아님 (EntryZone={localPosition.EntryZoneTag ?? string.Empty})";
                return false;
            }

            if (localPosition.AiConfidencePercent < 70f)
            {
                reason = $"AI 확률 {localPosition.AiConfidencePercent:F1}% < 70%";
                return false;
            }

            if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) || recentCandles.Count < 20)
            {
                reason = "5분봉 데이터 부족";
                return false;
            }

            List<IBinanceKline> recent20;
            lock (recentCandles)
            {
                recent20 = recentCandles.TakeLast(20).ToList(); // [동시성 안전]
            }
            if (recent20.Count < 20)
            {
                reason = "5분봉 스냅샷 부족";
                return false;
            }
            var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
            decimal bbLower = (decimal)bb.Lower;
            decimal bbUpper = (decimal)bb.Upper;
            decimal bbRange = bbUpper - bbLower;
            if (bbRange <= 0m)
            {
                reason = "BB 계산 불가";
                return false;
            }

            decimal bbPosition = (currentPrice - bbLower) / bbRange;
            if (bbPosition <= 0.10m)
            {
                reason = $"%B {bbPosition:P0} 하단 붕괴 구간에서는 DCA 금지";
                return false;
            }

            if (bbPosition > 0.15m)
            {
                reason = $"하단 DCA 대기 구간 아님 (%B {bbPosition:P0})";
                return false;
            }

            reason = $"하단 DCA 구간 도달 (%B {bbPosition:P0}, AI {localPosition.AiConfidencePercent:F1}%)";
            return true;
        }

        private bool TryEvaluateAiReversalExit(string symbol, decimal currentPrice, bool isLong, out string reason)
        {
            reason = "AI 재검증 데이터 부족";

            if (_aiPredictor == null)
                return false;

            var candle = BuildAiRecheckCandle(symbol, currentPrice);
            if (candle == null)
                return false;

            var prediction = _aiPredictor.Predict(candle);
            float confidencePct = prediction.Probability * 100f;
            bool oppositeSignal = isLong ? !prediction.Prediction : prediction.Prediction;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    p.AiScore = prediction.Score;
                    p.AiConfidencePercent = confidencePct;
                }
            }

            if (oppositeSignal && prediction.Probability >= 0.58f)
            {
                reason = $"반대 예측 감지 | 방향={(prediction.Prediction ? "LONG" : "SHORT")}, 확률={confidencePct:F1}%";
                return true;
            }

            reason = $"현재 방향 유지 | 예측={(prediction.Prediction ? "LONG" : "SHORT")}, 확률={confidencePct:F1}%";
            return false;
        }

        private CandleData? BuildAiRecheckCandle(string symbol, decimal currentPrice)
        {
            try
            {
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var candles) || candles.Count < 30)
                    return null;

                List<IBinanceKline> recent;
                lock (candles)
                {
                    recent = candles.TakeLast(120).ToList(); // [동시성 안전]
                }
                if (recent.Count < 20)
                {
                    OnLog?.Invoke($"⚠️ {symbol} recent 데이터 부족 (MarketStatus, Count: {recent.Count})");
                    return null;
                }
                var recent20 = recent.TakeLast(20).ToList();
                var latest = recent[^1];
                var previous = recent.Count >= 2 ? recent[^2] : latest;

                var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(recent);
                double rsi = IndicatorCalculator.CalculateRSI(recent20, 14);
                double atr = IndicatorCalculator.CalculateATR(recent20, 14);
                double sma20 = IndicatorCalculator.CalculateSMA(recent, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(recent, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(recent, 120);

                decimal mid = (decimal)bb.Mid;
                decimal upper = (decimal)bb.Upper;
                decimal lower = (decimal)bb.Lower;
                decimal high = latest.HighPrice;
                decimal low = latest.LowPrice;
                decimal open = latest.OpenPrice;
                decimal close = currentPrice > 0 ? currentPrice : latest.ClosePrice;
                decimal range = Math.Max(high - low, 0.00000001m);
                float volumeRatio = 0f;
                var avgVolume = recent20.Take(recent20.Count - 1).Select(k => (double)k.Volume).DefaultIfEmpty((double)latest.Volume).Average();
                if (avgVolume > 0)
                    volumeRatio = (float)((double)latest.Volume / avgVolume);

                float volumeChangePct = 0f;
                if (previous.Volume > 0)
                    volumeChangePct = (float)(((double)latest.Volume - (double)previous.Volume) / (double)previous.Volume * 100.0);

                decimal bbWidthPct = mid > 0 ? ((upper - lower) / mid) * 100m : 0m;
                decimal priceToMidPct = mid > 0 ? ((close - mid) / mid) * 100m : 0m;
                decimal sma20Dec = (decimal)sma20;
                decimal priceToSma20Pct = sma20Dec > 0 ? ((close - sma20Dec) / sma20Dec) * 100m : 0m;
                decimal body = Math.Abs(close - open);
                decimal upperShadow = high - Math.Max(open, close);
                decimal lowerShadow = Math.Min(open, close) - low;

                return new CandleData
                {
                    Symbol = symbol,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = (float)latest.Volume,
                    OpenTime = latest.OpenTime,
                    CloseTime = latest.CloseTime,
                    RSI = (float)rsi,
                    BollingerUpper = (float)upper,
                    BollingerLower = (float)lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,
                    Price_Change_Pct = open > 0 ? (float)(((close - open) / open) * 100m) : 0f,
                    Price_To_BB_Mid = (float)priceToMidPct,
                    BB_Width = (float)bbWidthPct,
                    Price_To_SMA20_Pct = (float)priceToSma20Pct,
                    Candle_Body_Ratio = (float)(body / range),
                    Upper_Shadow_Ratio = (float)(Math.Max(upperShadow, 0m) / range),
                    Lower_Shadow_Ratio = (float)(Math.Max(lowerShadow, 0m) / range),
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct
                };
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} AI 재검증 캔들 생성 실패: {ex.Message}");
                return null;
            }
        }

        private bool TryGetCurrentBbWidthPct(string symbol, out decimal bbWidthPct)
        {
            bbWidthPct = 0m;

            try
            {
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var candles) || candles.Count < 20)
                    return false;

                List<IBinanceKline> recent20;
                lock (candles)
                {
                    recent20 = candles.TakeLast(20).ToList(); // [동시성 안전]
                }
                if (recent20.Count < 20)
                    return false;
                var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
                decimal mid = (decimal)bb.Mid;
                decimal upper = (decimal)bb.Upper;
                decimal lower = (decimal)bb.Lower;
                if (mid <= 0m || upper <= lower)
                    return false;

                bbWidthPct = ((upper - lower) / mid) * 100m;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task ExecuteMarketClose(string symbol, string reason, CancellationToken token)
        {
            MarkCloseStarted(symbol);
            try
            {
                OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);

                PositionInfo? localTrackedPosition = null;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var localPos) && Math.Abs(localPos.Quantity) > 0)
                    {
                        localTrackedPosition = new PositionInfo
                        {
                            Symbol = symbol,
                            IsLong = localPos.IsLong,
                            Quantity = Math.Abs(localPos.Quantity),
                            EntryPrice = localPos.EntryPrice,
                            Leverage = localPos.Leverage,
                            UnrealizedPnL = localPos.UnrealizedPnL,
                            Side = localPos.Side,
                            EntryTime = localPos.EntryTime,
                            AiScore = localPos.AiScore
                        };
                    }
                }

                var positions = await _exchangeService.GetPositionsAsync(ct: token);
                var position = positions.FirstOrDefault(p => p.Symbol == symbol && p.Quantity != 0);
                if (position == null)
                {
                    if (localTrackedPosition != null)
                    {
                        decimal fallbackExitPrice = 0m;
                        if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cachedTicker))
                            fallbackExitPrice = cachedTicker.LastPrice;

                        if (fallbackExitPrice <= 0)
                        {
                            try
                            {
                                fallbackExitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                            }
                            catch (Exception priceEx)
                            {
                                OnLog?.Invoke($"⚠️ {symbol} 이미 종료된 포지션의 종료가 재조회 실패: {priceEx.Message}");
                            }
                        }

                        if (fallbackExitPrice <= 0)
                            fallbackExitPrice = localTrackedPosition.EntryPrice;

                        decimal fallbackAbsQty = Math.Abs(localTrackedPosition.Quantity);
                        
                        // 순수 가격 차이
                        decimal fallbackRawPnl = localTrackedPosition.IsLong
                            ? (fallbackExitPrice - localTrackedPosition.EntryPrice) * fallbackAbsQty
                            : (localTrackedPosition.EntryPrice - fallbackExitPrice) * fallbackAbsQty;

                        // 거래 수수료 및 슬리피지 차감
                        decimal fallbackEntryFee = localTrackedPosition.EntryPrice * fallbackAbsQty * 0.0004m;
                        decimal fallbackExitFee = fallbackExitPrice * fallbackAbsQty * 0.0004m;
                        decimal fallbackSlippage = fallbackExitPrice * fallbackAbsQty * 0.0005m;
                        decimal fallbackPnl = fallbackRawPnl - fallbackEntryFee - fallbackExitFee - fallbackSlippage;

                        decimal fallbackPnlPercent = 0m;
                        if (localTrackedPosition.EntryPrice > 0 && fallbackAbsQty > 0)
                        {
                            fallbackPnlPercent = (fallbackPnl / (localTrackedPosition.EntryPrice * fallbackAbsQty)) * 100m * localTrackedPosition.Leverage;
                        }

                        var alreadyClosedLog = new TradeLog(
                            symbol,
                            localTrackedPosition.IsLong ? "SELL" : "BUY",
                            "ALREADY_CLOSED_SYNC",
                            fallbackExitPrice,
                            localTrackedPosition.AiScore,
                            DateTime.Now,
                            fallbackPnl,
                            fallbackPnlPercent)
                        {
                            EntryPrice = localTrackedPosition.EntryPrice,
                            ExitPrice = fallbackExitPrice,
                            Quantity = fallbackAbsQty,
                            EntryTime = localTrackedPosition.EntryTime == default ? DateTime.Now : localTrackedPosition.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = string.IsNullOrWhiteSpace(reason) ? "ALREADY_CLOSED_SYNC" : reason
                        };

                        bool dbSaved = await _dbManager.CompleteTradeAsync(alreadyClosedLog);
                        OnLog?.Invoke(dbSaved
                            ? $"📝 {symbol} 거래소상 이미 종료된 포지션을 TradeHistory에 보정 반영했습니다."
                            : $"⚠️ {symbol} 거래소상 이미 종료된 포지션이었지만 TradeHistory 보정 반영에 실패했습니다.");

                        // [FIX] 청산 이력 갱신 이벤트
                        if (dbSaved) OnTradeHistoryUpdated?.Invoke();
                    }

                    CleanupPositionData(symbol);
                    return;
                }

                // [추가] 포지션 종료 전, 걸어둔 서버사이드 손절 주문 취소
                string stopOrderId = string.Empty;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) stopOrderId = p.StopOrderId; }

                if (!string.IsNullOrWhiteSpace(stopOrderId))
                {
                    await _exchangeService.CancelOrderAsync(symbol, stopOrderId, token);
                }

                bool isLongPosition = position.IsLong;
                var side = isLongPosition ? "SELL" : "BUY";
                var absQty = Math.Abs(position.Quantity);
                var positionDirection = isLongPosition ? "LONG" : "SHORT";
                var expectedCloseDirection = isLongPosition ? "SELL(롱 청산)" : "BUY(숏 청산)";

                async Task LogExchangePositionSnapshotAsync(string phase)
                {
                    try
                    {
                        var latestPositions = await _exchangeService.GetPositionsAsync(ct: token);
                        var latest = latestPositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);

                        if (latest == null)
                        {
                            OnLog?.Invoke($"[{phase}] {symbol} 거래소 재조회 결과: 오픈 포지션 없음");
                            return;
                        }

                        string latestDirection = latest.IsLong ? "LONG" : "SHORT";
                        OnLog?.Invoke($"[{phase}] {symbol} 거래소 재조회 결과: {latestDirection}, 수량={Math.Abs(latest.Quantity)}, 진입가={latest.EntryPrice:F4}, 미실현PnL={latest.UnrealizedPnL:F2}");
                    }
                    catch (Exception snapshotEx)
                    {
                        OnLog?.Invoke($"[{phase}] {symbol} 거래소 재조회 실패: {snapshotEx.Message}");
                    }
                }

                bool IsLocalPositionClosed()
                {
                    lock (_posLock)
                    {
                        return !_activePositions.TryGetValue(symbol, out var localPos) || Math.Abs(localPos.Quantity) <= 0;
                    }
                }

                PositionInfo? GetLocalTrackedPositionSnapshot()
                {
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var localPos) && Math.Abs(localPos.Quantity) > 0)
                        {
                            return new PositionInfo
                            {
                                Symbol = symbol,
                                IsLong = localPos.IsLong,
                                Quantity = Math.Abs(localPos.Quantity),
                                EntryPrice = localPos.EntryPrice,
                                Leverage = localPos.Leverage,
                                UnrealizedPnL = localPos.UnrealizedPnL,
                                Side = localPos.Side,
                                EntryTime = localPos.EntryTime,
                                AiScore = localPos.AiScore
                            };
                        }

                        return null;
                    }
                }

                async Task<PositionInfo?> ConfirmRemainingPositionAsync()
                {
                    PositionInfo? lastSeen = null;
                    const int maxAttempts = 8;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (attempt == 1)
                            await Task.Delay(200, token);
                        else
                            await Task.Delay(300, token);

                        if (IsLocalPositionClosed())
                        {
                            OnLog?.Invoke($"[청산 재확인] {symbol} 웹소켓/내부 상태에서 포지션 종료 확인 ({attempt}/{maxAttempts})");
                            return null;
                        }

                        PositionInfo? open = null;
                        try
                        {
                            var latestPositions = await _exchangeService.GetPositionsAsync(ct: token);
                            open = latestPositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);
                        }
                        catch (Exception restEx)
                        {
                            OnLog?.Invoke($"⚠️ [청산 재확인] {symbol} REST 재조회 실패 ({attempt}/{maxAttempts}): {restEx.Message}");
                        }

                        if (open == null)
                        {
                            OnLog?.Invoke($"[청산 재확인] {symbol} REST 재조회에서 포지션 0 확인 ({attempt}/{maxAttempts})");

                            return null;
                        }

                        lastSeen = open;

                        if (attempt < maxAttempts)
                        {
                            string openDirection = open.IsLong ? "LONG" : "SHORT";
                            OnLog?.Invoke($"⏳ [청산 재확인] {symbol} {attempt}/{maxAttempts} | 잔존감지 {openDirection} {Math.Abs(open.Quantity)} -> 재조회 대기");
                        }
                    }

                    if (IsLocalPositionClosed())
                    {
                        OnLog?.Invoke($"[청산 재확인] {symbol} 최종 확인에서 웹소켓/내부 상태 기준 포지션 종료 확인");
                        return null;
                    }

                    return lastSeen ?? GetLocalTrackedPositionSnapshot();
                }

                if (absQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ [청산 스킵] {symbol} 수량이 0입니다. 포지션 데이터 정리.");
                    CleanupPositionData(symbol);
                    return;
                }

                // [FIX] 진입시간을 주문 전에 미리 가져옴 (DB 저장에 필요)
                DateTime entryTimeForHistory = DateTime.Now;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var localPosForTime) && localPosForTime.EntryTime != default)
                        entryTimeForHistory = localPosForTime.EntryTime;
                }

                OnLog?.Invoke($"[청산 검증] {symbol} 포지션={positionDirection}, 진입가={position.EntryPrice:F4}, 수량={absQty}, 청산주문={expectedCloseDirection}");
                OnLog?.Invoke($"[청산 시도] {symbol} {side} {absQty} - 사유: {reason}");
                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, absQty, null, token, reduceOnly: true);

                if (!success)
                {
                    OnAlert?.Invoke($"❌ {symbol} 청산 실패! 거래소 API 오류 - 수동 확인 필요");
                    OnLog?.Invoke($"❌ [청산 실패] {symbol} - 거래소 주문 실행 실패. 실제 포지션 확인 필요!");
                    await LogExchangePositionSnapshotAsync("청산실패후재조회");
                    return; // 실패 시 포지션 데이터 유지
                }

                // [엄격모드] 주문 성공만으로는 청산 완료로 간주하지 않음 (거래소 포지션 0 확인 필수)
                PositionInfo? stillOpen = null;
                try
                {
                    stillOpen = await ConfirmRemainingPositionAsync();
                }
                catch (OperationCanceledException)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 청산 확인 중 취소됨 - DB 저장 시도 계속");
                    // CancellationToken 취소 시에도 DB 저장은 진행
                }

                if (stillOpen != null)
                {
                    string stillOpenDirection = stillOpen.IsLong ? "LONG" : "SHORT";

                    // [중요] 반대 방향 포지션 감지 및 재청산
                    bool isOppositeDirection = (isLongPosition && !stillOpen.IsLong) || (!isLongPosition && stillOpen.IsLong);

                    if (isOppositeDirection)
                    {
                        OnCloseIncompleteStatusChanged?.Invoke(symbol, true, $"반대 방향 포지션 잔존: {stillOpenDirection} {Math.Abs(stillOpen.Quantity)}");
                        OnAlert?.Invoke($"🚨🚨 {symbol} 반대 방향 포지션 생성됨! 원래={positionDirection}, 현재={stillOpenDirection}, 수량={Math.Abs(stillOpen.Quantity)} - 즉시 재청산 시도");
                        OnLog?.Invoke($"[긴급] {symbol} 반대 방향 포지션 감지! 원래 청산 방향={expectedCloseDirection}, 실제 포지션={stillOpenDirection}");
                        await LogExchangePositionSnapshotAsync("반대방향포지션감지");

                        // [FIX] 반대방향이라도 원래 포지션은 청산됐으므로 DB에 원래 청산 기록 저장
                        await SaveCloseTradeToDbAsync(symbol, side, position, isLongPosition, absQty, reason, entryTimeForHistory, CancellationToken.None);

                        // 재귀 호출로 반대 포지션 청산 (무한 루프 방지: reason에 "반대방향" 포함 시 1회만)
                        if (!reason.Contains("반대방향"))
                        {
                            await ExecuteMarketClose(symbol, $"반대방향 포지션 긴급 청산 (원인: {reason})", token);
                        }
                        else
                        {
                            OnAlert?.Invoke($"⚠️ {symbol} 반대방향 재청산 실패 - 무한루프 방지로 중단");
                        }
                        return;
                    }

                    // 같은 방향이면 기존 로직 (잔량 있음) - 하지만 부분 체결 가능성 → DB 저장
                    decimal closedQty = absQty - Math.Abs(stillOpen.Quantity);
                    if (closedQty > 0)
                    {
                        OnLog?.Invoke($"📝 {symbol} 부분 체결 감지: 원래={absQty}, 잔존={Math.Abs(stillOpen.Quantity)}, 체결={closedQty}");
                        await SaveCloseTradeToDbAsync(symbol, side, position, isLongPosition, closedQty, reason + " (부분체결)", entryTimeForHistory, CancellationToken.None);
                    }

                    OnCloseIncompleteStatusChanged?.Invoke(symbol, true, $"청산 주문 후 잔존 포지션: {stillOpenDirection} {Math.Abs(stillOpen.Quantity)}");
                    OnAlert?.Invoke($"🚨 {symbol} 청산 미완료! 주문은 성공했지만 포지션 잔존 ({stillOpenDirection} {Math.Abs(stillOpen.Quantity)})");
                    await LogExchangePositionSnapshotAsync("청산주문성공_잔존포지션확인");
                    return; // 잔존 포지션이 있으므로 내부 상태/DB 정리 금지
                }

                OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);
                OnLog?.Invoke($"[청산 검증완료] {symbol} 청산 후 거래소 오픈 포지션 0 확인");

                // [FIX] 헬퍼 메서드로 DB 저장 통합 (청산 완료 확인 후)
                await SaveCloseTradeToDbAsync(symbol, side, position, isLongPosition, absQty, reason, entryTimeForHistory, CancellationToken.None);

                OnAlert?.Invoke($"✅ {symbol} 청산 완료(검증됨): {reason}");

                if (reason.Contains("지루함") || reason.Contains("Boredom"))
                {
                    _blacklistedSymbols[symbol] = DateTime.Now.AddMinutes(30);
                    OnLog?.Invoke($"🚫 {symbol} 30분간 블랙리스트 등록 (재진입 금지)");
                }

                CleanupPositionData(symbol);
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ {symbol} 종료 로직 에러: {ex.Message}"); }
            finally
            {
                MarkCloseFinished(symbol);
            }
        }

        /// <summary>
        /// [FIX] 청산 시 DB TradeHistory 저장을 일원화하는 헬퍼 메서드.
        /// 모든 청산 경로(정상 청산, 반대방향 청산, 부분 체결 등)에서 호출.
        /// </summary>
        private async Task SaveCloseTradeToDbAsync(
            string symbol, string side, PositionInfo position,
            bool isLongPosition, decimal quantity, string reason,
            DateTime entryTimeForHistory, CancellationToken token)
        {
            try
            {
                // 청산가 조회 (캐시 우선, 폴백: 현재가 API)
                decimal exitPrice = 0;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached))
                {
                    exitPrice = cached.LastPrice;
                }

                if (exitPrice == 0)
                {
                    try
                    {
                        exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                    }
                    catch (Exception priceEx)
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 청산가 조회 실패, 진입가로 대체: {priceEx.Message}");
                        exitPrice = position.EntryPrice;
                    }
                }

                if (exitPrice == 0)
                    exitPrice = position.EntryPrice;

                decimal absQty = Math.Abs(quantity);
                
                // [수정] 순수 가격 차이로 PnL 계산
                decimal rawPnl = isLongPosition
                    ? (exitPrice - position.EntryPrice) * absQty
                    : (position.EntryPrice - exitPrice) * absQty;

                // [추가] 거래 수수료 차감 (Binance Taker 기준: 진입 0.04% + 청산 0.04%)
                decimal entryFee = position.EntryPrice * absQty * 0.0004m;
                decimal exitFee = exitPrice * absQty * 0.0004m;
                decimal totalFee = entryFee + exitFee;

                // [추가] 슬리피지 예상치 차감 (시장가 주문: 약 0.05% 예상)
                decimal estimatedSlippage = exitPrice * absQty * 0.0005m;

                // [최종] 실제 PnL = 순수 가격 차이 - 수수료 - 슬리피지
                decimal pnl = rawPnl - totalFee - estimatedSlippage;
                _riskManager.UpdatePnlAndCheck(pnl);

                // 수익률 계산
                decimal pnlPercent = 0;
                decimal positionLeverage = position.Leverage;
                lock (_posLock)
                {
                    if (positionLeverage <= 0 && _activePositions.TryGetValue(symbol, out var localPos) && localPos.Leverage > 0)
                        positionLeverage = localPos.Leverage;
                }

                if (position.EntryPrice > 0 && absQty > 0)
                {
                    pnlPercent = (pnl / (position.EntryPrice * absQty)) * 100 * positionLeverage;
                }

                // [디버깅] 상세 내역 로그
                OnLog?.Invoke($"[PnL 계산] {symbol} | 순수: {rawPnl:F2} | 수수료: -{totalFee:F2} | 슬리피지: -{estimatedSlippage:F2} | 최종: {pnl:F2} ({pnlPercent:F2}%)");

                // DB에 매매 이력 저장
                var log = new TradeLog(symbol, side, "MarketClose", exitPrice, position.AiScore, DateTime.Now, pnl, pnlPercent)
                {
                    EntryPrice = position.EntryPrice,
                    ExitPrice = exitPrice,
                    Quantity = absQty,
                    ExitReason = reason,
                    EntryTime = entryTimeForHistory,
                    ExitTime = DateTime.Now
                };

                bool dbSaved = await _dbManager.CompleteTradeAsync(log);
                OnLog?.Invoke(dbSaved
                    ? $"✅ [DB 확인] {symbol} 청산 이력이 TradeHistory에 반영되었습니다. (PnL={pnl:F2}, ROE={pnlPercent:F2}%)"
                    : $"⚠️ [DB 확인] {symbol} 청산은 완료됐지만 TradeHistory 반영에 실패했습니다. (userId 확인 필요)");

                OnLog?.Invoke($"[청산 확인] {symbol} 종료가={exitPrice:F4}, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                OnLog?.Invoke($"[종료] {symbol} | 수량: {absQty} | 사유: {reason}");

                // 텔레그램, 디스코드, 푸시 알림 통합 전송 (총 수익금 포함)
                decimal totalPnl = _riskManager?.DailyRealizedPnl ?? 0m;
                _ = NotificationService.Instance.NotifyProfitAsync(symbol, pnl, pnlPercent, totalPnl);

                // 청산 완료 이벤트 발생 (UI 자동 갱신 트리거)
                OnTradeHistoryUpdated?.Invoke();
            }
            catch (Exception dbEx)
            {
                OnLog?.Invoke($"❌ {symbol} DB 저장 중 치명적 오류: {dbEx.Message}");
            }
        }

        public async Task ExecutePartialClose(string symbol, decimal ratio, CancellationToken token)
        {
            MarkCloseStarted(symbol);
            try
            {
                if (ratio <= 0 || ratio >= 1)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 비율이 유효하지 않습니다: {ratio}");
                    return;
                }

                PositionInfo? localPosition;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out localPosition);
                }

                if (localPosition == null)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 실패: 내부 포지션 없음");
                    return;
                }

                var side = localPosition.IsLong ? "SELL" : "BUY";
                var currentQty = Math.Abs(localPosition.Quantity);
                var closeQty = Math.Round(currentQty * ratio, 6, MidpointRounding.AwayFromZero);

                if (closeQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 수량 계산 실패: 현재={currentQty}, 비율={ratio}");
                    return;
                }

                OnLog?.Invoke($"[부분청산 시도] {symbol} {side} {closeQty} ({ratio:P0})");
                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, closeQty, null, token, reduceOnly: true);

                if (!success)
                {
                    OnAlert?.Invoke($"❌ {symbol} 부분청산 실패 (거래소 주문 실패)");
                    return;
                }

                decimal remainingQty = Math.Max(0, currentQty - closeQty);
                try
                {
                    PositionInfo? confirmedPosition = null;
                    for (int attempt = 1; attempt <= 4; attempt++)
                    {
                        await Task.Delay(attempt == 1 ? 200 : 300, token);

                        var latestPositions = await _exchangeService.GetPositionsAsync(ct: token);
                        confirmedPosition = latestPositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);

                        if (confirmedPosition == null)
                        {
                            remainingQty = 0;
                            break;
                        }

                        decimal exchangeQty = Math.Abs(confirmedPosition.Quantity);
                        if (exchangeQty < currentQty - 0.000001m)
                        {
                            remainingQty = exchangeQty;
                            break;
                        }
                    }
                }
                catch (Exception confirmEx)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 재확인 실패, 계산 수량으로 반영: {confirmEx.Message}");
                }

                decimal actualClosedQty = Math.Max(0, currentQty - remainingQty);
                if (actualClosedQty <= 0.000001m)
                {
                    actualClosedQty = closeQty;
                    remainingQty = Math.Max(0, currentQty - actualClosedQty);
                }

                decimal exitPrice = 0;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached))
                {
                    exitPrice = cached.LastPrice;
                }

                if (exitPrice == 0)
                {
                    exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                }

                // 순수 가격 차이
                decimal rawPnl = localPosition.IsLong
                    ? (exitPrice - localPosition.EntryPrice) * actualClosedQty
                    : (localPosition.EntryPrice - exitPrice) * actualClosedQty;

                // 거래 수수료 및 슬리피지 차감
                decimal entryFee = localPosition.EntryPrice * actualClosedQty * 0.0004m;
                decimal exitFee = exitPrice * actualClosedQty * 0.0004m;
                decimal estimatedSlippage = exitPrice * actualClosedQty * 0.0005m;
                decimal pnl = rawPnl - entryFee - exitFee - estimatedSlippage;

                _riskManager.UpdatePnlAndCheck(pnl);

                decimal pnlPercent = 0;
                if (localPosition.EntryPrice > 0 && actualClosedQty > 0)
                {
                    pnlPercent = (pnl / (localPosition.EntryPrice * actualClosedQty)) * 100 * localPosition.Leverage;
                }

                DateTime now = DateTime.Now;
                if (remainingQty <= 0.000001m)
                {
                    var closeLog = new TradeLog(
                        symbol,
                        side,
                        "PartialCloseFullFill",
                        exitPrice,
                        localPosition.AiScore,
                        now,
                        pnl,
                        pnlPercent)
                    {
                        EntryPrice = localPosition.EntryPrice,
                        ExitPrice = exitPrice,
                        Quantity = currentQty,
                        EntryTime = localPosition.EntryTime,
                        ExitTime = now,
                        ExitReason = "PartialClose_FullClose"
                    };

                    try
                    {
                        await _dbManager.CompleteTradeAsync(closeLog);
                        OnTradeHistoryUpdated?.Invoke();
                    }
                    catch (Exception dbEx)
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 부분청산→전량청산 DB 저장 실패: {dbEx.Message}");
                    }

                    OnAlert?.Invoke($"ℹ️ {symbol} 부분청산 주문이 전량 체결되어 포지션 종료 처리됨");
                    CleanupPositionData(symbol);
                    OnLog?.Invoke($"✅ {symbol} 부분청산 주문 전량 체결: 청산={currentQty}, 잔여=0, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                    return;
                }

                var partialLog = new TradeLog(
                    symbol,
                    side,
                    "PartialClose",
                    exitPrice,
                    localPosition.AiScore,
                    now,
                    pnl,
                    pnlPercent)
                {
                    EntryPrice = localPosition.EntryPrice,
                    ExitPrice = exitPrice,
                    Quantity = actualClosedQty,
                    EntryTime = localPosition.EntryTime,
                    ExitTime = now,
                    ExitReason = "PartialClose"
                };

                try
                {
                    await _dbManager.RecordPartialCloseAsync(partialLog);
                    OnTradeHistoryUpdated?.Invoke();
                }
                catch (Exception dbEx)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 DB 저장 실패: {dbEx.Message}");
                }

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                    {
                        p.Quantity = p.IsLong ? remainingQty : -remainingQty;
                    }
                }

                OnLog?.Invoke($"✅ {symbol} 부분청산 완료: 청산={actualClosedQty}, 잔여={remainingQty}, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                OnPositionStatusUpdate?.Invoke(symbol, remainingQty > 0, 0);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 부분청산 예외: {ex.Message}");
            }
            finally
            {
                MarkCloseFinished(symbol);
            }
        }

        /// <summary>
        /// [v2.1.18] 지표 기반 익절 신호를 위한 기술적 데이터 구축
        /// MarketDataManager와 AdvancedIndicators에서 필요한 모든 지표를 수집
        /// </summary>
        private TechnicalData? BuildTechnicalDataForExitSignal(string symbol, decimal currentPrice, decimal entryPrice, bool isLong)
        {
            try
            {
                var tech = new TechnicalData
                {
                    CurrentPrice = currentPrice,
                    EntryPrice = entryPrice,
                    HighestPrice = currentPrice,  // 실제로는 MarketDataManager에서 조회
                    LowestPrice = currentPrice,   // 실제로는 MarketDataManager에서 조회
                    Atr = 0.001m, // 기본값 (실제 구현 시 지표 계산기에서 제공)
                    AtrMultiplier = 1.5m,
                };

                // [TODO] 다음 정보는 AdvancedIndicators 클래스에서 읽어야 함:
                // - tech.IsWave5 (엘리엇 파동 분석)
                // - tech.Rsi (RSI 계산)
                // - tech.MacdLine, SignalLine, MacdHistogram (MACD 계산)
                // - tech.UpperBand, MidBand, LowerBand (볼린저 밴드 계산)
                // - tech.Fibo1618, Fibo2618 (피보나치 계산)

                // 현재는 MarketDataManager를 통해 기본 데이터만 수집
                // (추후 AdvancedIndicators와의 통합 시 완성)

                return tech;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 지표 데이터 수집 실패: {ex.Message}");
                return null;
            }
        }

        public async Task ExecuteAverageDown(string symbol, CancellationToken token)
        {
            try
            {
                PositionInfo? localPosition;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out localPosition);
                }

                if (localPosition == null)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 물타기 실패: 내부 포지션 없음");
                    return;
                }

                if (localPosition.IsAveragedDown)
                {
                    OnLog?.Invoke($"ℹ️ {symbol} 물타기는 이미 1회 수행됨");
                    return;
                }

                var addQty = Math.Round(Math.Abs(localPosition.Quantity) * 0.5m, 6, MidpointRounding.AwayFromZero);
                if (addQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 물타기 수량 계산 실패");
                    return;
                }

                var side = localPosition.IsLong ? "BUY" : "SELL";
                OnLog?.Invoke($"[물타기 시도] {symbol} {side} {addQty} (기존의 50%)");

                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, addQty, null, token, reduceOnly: false);
                if (!success)
                {
                    OnAlert?.Invoke($"❌ {symbol} 물타기 실패 (거래소 주문 실패)");
                    return;
                }

                var marketPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                if (marketPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                    marketPrice = ticker.LastPrice;
                if (marketPrice <= 0)
                {
                    marketPrice = localPosition.EntryPrice;
                }

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                    {
                        var oldQtyAbs = Math.Abs(p.Quantity);
                        var totalQty = oldQtyAbs + addQty;
                        if (totalQty > 0)
                        {
                            p.EntryPrice = ((p.EntryPrice * oldQtyAbs) + (marketPrice * addQty)) / totalQty;
                        }
                        p.Quantity = p.IsLong ? totalQty : -totalQty;
                        p.IsAveragedDown = true;

                        var averagedLog = new TradeLog(
                            symbol,
                            p.IsLong ? "BUY" : "SELL",
                            "AverageDown",
                            p.EntryPrice,
                            p.AiScore,
                            DateTime.Now,
                            0,
                            0)
                        {
                            EntryPrice = p.EntryPrice,
                            Quantity = Math.Abs(p.Quantity),
                            EntryTime = p.EntryTime == default ? DateTime.Now : p.EntryTime
                        };

                        _ = _dbManager.UpsertTradeEntryAsync(averagedLog);
                    }
                }

                OnAlert?.Invoke($"💧 {symbol} 물타기 완료: +{addQty} @ {marketPrice:F4}");
                OnLog?.Invoke($"✅ {symbol} 물타기 반영 완료");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 물타기 예외: {ex.Message}");
            }
        }

        public void HandleOrderUpdate(BinanceFuturesStreamOrderUpdate orderUpdate)
        {
            var data = orderUpdate.UpdateData;
            string symbol = data.Symbol;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var pos))
                {
                    // [수정] data.Id -> data.OrderId
                    if (pos.StopOrderId == data.OrderId.ToString() && data.Status == OrderStatus.Filled)
                    {
                        OnAlert?.Invoke($"🛑 {symbol} 서버사이드 손절 주문 체결 완료! (체결가: {data.AveragePrice})");
                        OnLog?.Invoke($"[체결] {symbol} STOP_MARKET Filled at {data.AveragePrice}");
                    }
                }
            }
        }

        private void CleanupPositionData(string symbol)
        {
            lock (_posLock) _activePositions.Remove(symbol);
            OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);
            OnPositionStatusUpdate?.Invoke(symbol, false, 0);
            
            // [중요] 포지션 청산 후 ROI를 명시적으로 0으로 리셋
            // (감시 루프의 마지막 ROE 값이 UI를 덮어씌우는 것을 방지)
            OnTickerUpdate?.Invoke(symbol, 0m, 0d);
        }
    }
}
