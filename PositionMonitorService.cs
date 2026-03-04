using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
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
        private readonly Dictionary<string, DateTime> _blacklistedSymbols;

        // Events
        public event Action<string>? OnLog = delegate { };
        public event Action<string>? OnAlert = delegate { };
        public event Action<string, decimal, double?>? OnTickerUpdate = delegate { };
        public event Action<string, bool, decimal>? OnPositionStatusUpdate = delegate { };

        public PositionMonitorService(
            IBinanceRestClient client,
            IExchangeService exchangeService, // [변경]
            RiskManager riskManager,
            MarketDataManager marketDataManager,
            DbManager dbManager,
            Dictionary<string, PositionInfo> activePositions,
            object posLock,
            Dictionary<string, DateTime> blacklistedSymbols,
            TradingSettings settings)
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

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    entryPrice = p.EntryPrice;
                    isLong = p.IsLong;
                    partialTaken = p.TakeProfitStep >= 1;

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
            
            // 3단계 파라미터
            decimal breakEvenROE = 10.0m;          // 1단계: 본절 확보
            decimal profitLockROE = 15.0m;         // 2단계: 수익 잠금
            decimal tightTrailingROE = 20.0m;      // 3단계: 타이트 트레일링
            decimal minLockROE = 18.0m;            // 3단계 최소 수익 (ROE 18%)
            decimal tightGapPercent = 0.0015m;     // 3단계 간격 (0.15%)

            // [추가] 안전장치: 서버사이드 손절 주문 설정 (Stop Market)
            try
            {
                decimal stopPrice = (isSidewaysMode && customStopLossPrice > 0)
                    ? customStopLossPrice
                    : (isLong
                        ? entryPrice * (1 - _settings.StopLossRoe / _settings.DefaultLeverage / 100)
                        : entryPrice * (1 + _settings.StopLossRoe / _settings.DefaultLeverage / 100));

                // 수량 조회 (락 필요)
                decimal qty = 0;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) qty = Math.Abs(p.Quantity); }

                if (qty > 0)
                {
                    bool success = await _exchangeService.PlaceStopOrderAsync(symbol, isLong ? "SELL" : "BUY", qty, stopPrice, token);
                    if (success)
                    {
                        // lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) p.StopOrderId = ...; } // ID 추적은 거래소별 구현 필요
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
                    if (currentPrice == 0) { await Task.Delay(2000, token); continue; }

                    decimal priceChangePercent = isLong
                        ? (currentPrice - entryPrice) / entryPrice * 100
                        : (entryPrice - currentPrice) / entryPrice * 100;

                    decimal currentROE = priceChangePercent * _settings.DefaultLeverage;

                    OnTickerUpdate?.Invoke(symbol, 0m, (double)currentROE);

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
                    // 1단계: ROE 10% 도달 → 본절 보호 (Break-even)
                    // ═══════════════════════════════════════════════
                    if (!breakEvenActivated && highestROE >= breakEvenROE)
                    {
                        breakEvenActivated = true;
                        
                        // 진입가 + 0.1% (수수료 방어 + 본절 확보)
                        protectiveStopPrice = isLong 
                            ? entryPrice * 1.001m
                            : entryPrice * 0.999m;
                        
                        OnLog?.Invoke($"🛡️ {symbol} [1단계] 본절 보호 활성화! ROE {highestROE:F1}% 도달 | 스탑={protectiveStopPrice:F8} (본절가 + 0.1%)");
                    }

                    // ═══════════════════════════════════════════════
                    // 2단계: ROE 15% 도달 → 수익 잠금 (최소 ROE 7%)
                    // ═══════════════════════════════════════════════
                    if (breakEvenActivated && !profitLockActivated && highestROE >= profitLockROE)
                    {
                        profitLockActivated = true;
                        
                        // 진입가 + 0.35% (ROE 약 7% 지점)
                        protectiveStopPrice = isLong 
                            ? entryPrice * 1.0035m
                            : entryPrice * 0.9965m;
                        
                        OnLog?.Invoke($"💰 {symbol} [2단계] 수익 잠금 활성화! ROE {highestROE:F1}% 도달 | 스탑={protectiveStopPrice:F8} (최소 ROE 7% 확보)");
                    }

                    // ═══════════════════════════════════════════════
                    // 3단계: ROE 20% 도달 → 타이트한 트레일링 (최소 ROE 18%)
                    // ═══════════════════════════════════════════════
                    if (profitLockActivated && !tightTrailingActivated && highestROE >= tightTrailingROE)
                    {
                        tightTrailingActivated = true;
                        
                        // ROE 18% = 0.9% 가격 변동 (20배 레버리지)
                        decimal minLockPriceChange = minLockROE / _settings.DefaultLeverage / 100;
                        protectiveStopPrice = isLong 
                            ? entryPrice * (1 + minLockPriceChange)
                            : entryPrice * (1 - minLockPriceChange);
                        
                        OnLog?.Invoke($"🎯 {symbol} [3단계] 타이트 트레일링 활성화! ROE {highestROE:F1}% 돌파 | 스탑={protectiveStopPrice:F8} (최소 ROE 18%)");
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
                            decimal minLockPrice = entryPrice * (1 + minLockROE / _settings.DefaultLeverage / 100);
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
                            decimal minLockPrice = entryPrice * (1 - minLockROE / _settings.DefaultLeverage / 100);
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
                                ? ((currentPrice - entryPrice) / entryPrice) * _settings.DefaultLeverage * 100
                                : ((entryPrice - currentPrice) / entryPrice) * _settings.DefaultLeverage * 100;
                            
                            string stage = tightTrailingActivated ? "3단계 타이트 트레일링" 
                                         : profitLockActivated ? "2단계 수익 잠금" 
                                         : "1단계 본절 보호";
                            
                            OnLog?.Invoke($"[청산 트리거] {symbol} {stage} 스톱 발동! | 현재가={currentPrice:F8}, 스탑={protectiveStopPrice:F8} | 최종ROE={finalROE:F1}%");
                            await ExecuteMarketClose(symbol, $"Smart Protective Stop [{stage}] (ROE {finalROE:F1}%)", token);
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

                    if (currentROE >= _settings.TargetRoe)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 메이저 익절 조건 충족 | 방향={(isLong ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 목표ROE={_settings.TargetRoe:F2}%");
                        await ExecuteMarketClose(symbol, $"메이저 익절 달성 ({currentROE:F2}%)", token);
                        break;
                    }
                    else if (currentROE <= -_settings.StopLossRoe)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 메이저 손절 조건 충족 | 방향={(isLong ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 손절ROE=-{_settings.StopLossRoe:F2}%");
                        await ExecuteMarketClose(symbol, $"메이저 손절 실행 ({currentROE:F2}%)", token);
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
                            var last = recentCandles.Last();
                            var bb = IndicatorCalculator.CalculateBB(recentCandles.TakeLast(20).ToList(), 20, 2);
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
                                var rsiNow = IndicatorCalculator.CalculateRSI(recentCandles.TakeLast(20).ToList(), 14);
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
                                var bbAnalysis = IndicatorCalculator.CalculateBB(recentCandles.TakeLast(20).ToList(), 20, 2);
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
                            var lastCandle = recentCandles.Last();
                            decimal lastClose = (decimal)lastCandle.ClosePrice;

                            var bbAnalysis = IndicatorCalculator.CalculateBB(recentCandles.TakeLast(20).ToList(), 20, 2);
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
                                var candles = recentCandles.TakeLast(30).ToList();
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

        public async Task ExecuteMarketClose(string symbol, string reason, CancellationToken token)
        {
            try
            {
                var positions = await _exchangeService.GetPositionsAsync(ct: token);
                var position = positions.FirstOrDefault(p => p.Symbol == symbol && p.Quantity != 0);
                if (position == null)
                {
                    CleanupPositionData(symbol);
                    return;
                }

                // [추가] 포지션 종료 전, 걸어둔 서버사이드 손절 주문 취소
                long stopOrderId = 0;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) stopOrderId = p.StopOrderId; }

                if (stopOrderId > 0)
                {
                    await _exchangeService.CancelOrderAsync(symbol, stopOrderId.ToString(), token);
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

                if (absQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ [청산 스킵] {symbol} 수량이 0입니다. 포지션 데이터 정리.");
                    CleanupPositionData(symbol);
                    return;
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
                var afterClosePositions = await _exchangeService.GetPositionsAsync(ct: token);
                var stillOpen = afterClosePositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);
                if (stillOpen != null)
                {
                    string stillOpenDirection = stillOpen.IsLong ? "LONG" : "SHORT";

                    // [중요] 반대 방향 포지션 감지 및 재청산
                    bool isOppositeDirection = (isLongPosition && !stillOpen.IsLong) || (!isLongPosition && stillOpen.IsLong);

                    if (isOppositeDirection)
                    {
                        OnAlert?.Invoke($"🚨🚨 {symbol} 반대 방향 포지션 생성됨! 원래={positionDirection}, 현재={stillOpenDirection}, 수량={Math.Abs(stillOpen.Quantity)} - 즉시 재청산 시도");
                        OnLog?.Invoke($"[긴급] {symbol} 반대 방향 포지션 감지! 원래 청산 방향={expectedCloseDirection}, 실제 포지션={stillOpenDirection}");
                        await LogExchangePositionSnapshotAsync("반대방향포지션감지");

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

                    // 같은 방향이면 기존 로직 (잔량 있음)
                    OnAlert?.Invoke($"🚨 {symbol} 청산 미완료! 주문은 성공했지만 포지션 잔존 ({stillOpenDirection} {Math.Abs(stillOpen.Quantity)})");
                    await LogExchangePositionSnapshotAsync("청산주문성공_잔존포지션확인");
                    return; // 잔존 포지션이 있으므로 내부 상태/DB 정리 금지
                }

                OnLog?.Invoke($"[청산 검증완료] {symbol} 청산 후 거래소 오픈 포지션 0 확인");

                // [수정] 청산가 정확한 조회 (캐시 우선, 폴백: 현재가 API)
                decimal exitPrice = 0;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached))
                {
                    exitPrice = cached.LastPrice;
                }

                if (exitPrice == 0)
                {
                    exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                }

                decimal pnl = isLongPosition
                    ? (exitPrice - position.EntryPrice) * absQty
                    : (position.EntryPrice - exitPrice) * absQty;
                _riskManager.UpdatePnlAndCheck(pnl);

                // [수정] 수익률 계산 및 DB 저장
                decimal pnlPercent = 0;
                if (position.EntryPrice > 0)
                {
                    pnlPercent = (pnl / (position.EntryPrice * absQty)) * 100 * position.Leverage;
                }

                // DB에 매매 이력 저장
                var log = new TradeLog(symbol, side, "MarketClose", exitPrice, position.AiScore, DateTime.Now, pnl, pnlPercent);
                try
                {
                    await _dbManager.SaveTradeLogAsync(log); // [수정] fire-and-forget에서 await로 변경
                }
                catch (Exception dbEx)
                {
                    OnLog?.Invoke($"⚠️ {symbol} DB 저장 중 오류: {dbEx.Message}");
                }

                // 텔레그램, 디스코드, 푸시 알림 통합 전송
                _ = NotificationService.Instance.NotifyProfitAsync(symbol, pnl, pnlPercent);

                OnAlert?.Invoke($"✅ {symbol} 청산 완료(검증됨): {reason}");
                OnLog?.Invoke($"[청산 확인] {symbol} 포지션={positionDirection} -> 주문={side}, 종료가={exitPrice:F4}, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                OnLog?.Invoke($"[종료] {symbol} | 수량: {absQty} | 사유: {reason}");

                if (reason.Contains("지루함") || reason.Contains("Boredom"))
                {
                    lock (_blacklistedSymbols) _blacklistedSymbols[symbol] = DateTime.Now.AddMinutes(30);
                    OnLog?.Invoke($"🚫 {symbol} 30분간 블랙리스트 등록 (재진입 금지)");
                }

                CleanupPositionData(symbol);
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ {symbol} 종료 로직 에러: {ex.Message}"); }
        }

        public async Task ExecutePartialClose(string symbol, decimal ratio, CancellationToken token)
        {
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

                var remainingQty = Math.Max(0, currentQty - closeQty);
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                    {
                        p.Quantity = p.IsLong ? remainingQty : -remainingQty;
                    }
                }

                OnLog?.Invoke($"✅ {symbol} 부분청산 완료: 청산={closeQty}, 잔여={remainingQty}");
                OnPositionStatusUpdate?.Invoke(symbol, remainingQty > 0, 0);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 부분청산 예외: {ex.Message}");
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
                    if (pos.StopOrderId == data.OrderId && data.Status == OrderStatus.Filled)
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
            OnPositionStatusUpdate?.Invoke(symbol, false, 0);
        }
    }
}
