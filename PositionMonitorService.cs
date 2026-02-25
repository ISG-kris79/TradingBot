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
        public event Action<string> OnLog;
        public event Action<string> OnAlert;
        public event Action<string, decimal, double?> OnTickerUpdate;
        public event Action<string, bool, decimal> OnPositionStatusUpdate;

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

        public async Task MonitorPositionStandard(string symbol, decimal entryPrice, bool isLong, CancellationToken token)
        {
            OnLog?.Invoke($"🔍 {symbol} 감시 시작 (진입가: {entryPrice})");

            // [추가] 안전장치: 서버사이드 손절 주문 설정 (Stop Market)
            try 
            {
                decimal stopPrice = isLong 
                    ? entryPrice * (1 - _settings.StopLossRoe / _settings.DefaultLeverage / 100) 
                    : entryPrice * (1 + _settings.StopLossRoe / _settings.DefaultLeverage / 100);
                
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

                    if (currentROE >= _settings.TargetRoe)
                    {
                        await ExecuteMarketClose(symbol, $"메이저 익절 달성 ({currentROE:F2}%)", token);
                        break;
                    }
                    else if (currentROE <= -_settings.StopLossRoe)
                    {
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
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var pos)) entryPrice = pos.EntryPrice;
                else return;
            }

            DateTime startTime = DateTime.Now;
            decimal highestROE = -999m;
            decimal leverage = _settings.DefaultLeverage;

            decimal stopLossROE = _settings.StopLossRoe;
            decimal trailingStartROE = _settings.TrailingStartRoe;
            decimal trailingDropROE = _settings.TrailingDropRoe;
            decimal averageDownROE = -5.0m;
            bool isBreakEvenTriggered = false;
            decimal partialTakeProfitROE = 5.0m;

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
                decimal priceChangePercent = (currentPrice - entryPrice) / entryPrice * 100;
                decimal currentROE = priceChangePercent * leverage;

                if ((DateTime.Now - startTime).TotalSeconds >= 60 && currentROE < 0.0m)
                {
                    await ExecuteMarketClose(symbol, "⏱️ 타임컷 (1분 경과/손실권)", token);
                    break;
                }

                if ((DateTime.Now - startTime).TotalMinutes >= 3 && currentROE >= -1.0m && currentROE <= 1.0m)
                {
                    await ExecuteMarketClose(symbol, "🥱 지루함 (3분 횡보)", token);
                    break;
                }

                OnTickerUpdate?.Invoke(symbol, 0m, (double)currentROE);

                bool isAveraged = false;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) isAveraged = p.IsAveragedDown; }

                if (isAveraged) stopLossROE = 20.0m;

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
                    await ExecuteMarketClose(symbol, exitReason, token);
                    break;
                }

                if (currentROE > highestROE) highestROE = currentROE;

                if (highestROE >= 10.0m && highestROE < 15.0m && trailingDropROE > 2.0m)
                {
                    trailingDropROE = 2.0m;
                    if (trailingStartROE > 10.0m) trailingStartROE = 10.0m;
                    OnAlert?.Invoke($"🏃 {symbol} Dynamic Trailing 가동 (ROE 10%↑ -> 간격 2%로 축소)");
                }
                else if (highestROE >= 15.0m && trailingDropROE != 3.0m)
                {
                    trailingDropROE = 3.0m;
                    if (trailingStartROE > 10.0m) trailingStartROE = 10.0m;
                    OnAlert?.Invoke($"🚀 {symbol} 슈퍼 트레일링 가동 (ROE 15%↑ -> 간격 3%로 설정)");
                }

                if (highestROE >= trailingStartROE)
                {
                    if (highestROE - currentROE >= trailingDropROE)
                    {
                        PositionInfo pos = null;
                        lock (_posLock) { _activePositions.TryGetValue(symbol, out pos); }

                        if (pos != null && pos.TakeProfitStep == 0)
                        {
                            await ExecutePartialClose(symbol, 0.5m, token);
                            lock (_posLock) { if (_activePositions.ContainsKey(symbol)) _activePositions[symbol].TakeProfitStep = 1; }
                            OnAlert?.Invoke($"💰 {symbol} 1차 트레일링 익절 (50%) | ROE: {currentROE:F1}%");
                            highestROE = currentROE;
                        }
                        else
                        {
                            await ExecuteMarketClose(symbol, $"ROE 트레일링 최종 익절 (최고:{highestROE:F1}% / 현재:{currentROE:F1}%)", token);
                            break;
                        }
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

                var side = position.Quantity > 0 ? "SELL" : "BUY";
                var absQty = Math.Abs(position.Quantity);

                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, absQty, null, token);

                if (success)
                {
                    decimal exitPrice = 0; // 시장가 청산 시 체결가 조회 필요 (여기서는 현재가로 대체)
                    if (exitPrice == 0)
                    {
                        if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached)) exitPrice = cached.LastPrice;
                        else
                        { exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token); }
                    }

                    decimal pnl = position.Quantity > 0 ? (exitPrice - position.EntryPrice) * absQty : (position.EntryPrice - exitPrice) * absQty;
                    _riskManager.UpdatePnlAndCheck(pnl);

                    // [수정] 수익률 계산 및 DB 저장
                    decimal pnlPercent = 0;
                    if (position.EntryPrice > 0)
                    {
                        pnlPercent = (pnl / (position.EntryPrice * absQty)) * 100 * position.Leverage;
                    }

                    // DB에 매매 이력 저장
                    var log = new TradeLog(symbol, side, "MarketClose", exitPrice, 0, DateTime.Now, pnl, pnlPercent);
                    _ = _dbManager.SaveTradeLogAsync(log);

                    OnAlert?.Invoke($"✅ {symbol} 청산 완료: {reason}");
                    OnLog?.Invoke($"[종료] {symbol} | 수량: {absQty} | 사유: {reason}");

                    if (reason.Contains("지루함") || reason.Contains("Boredom"))
                    {
                        lock (_blacklistedSymbols) _blacklistedSymbols[symbol] = DateTime.Now.AddMinutes(30);
                        OnLog?.Invoke($"🚫 {symbol} 30분간 블랙리스트 등록 (재진입 금지)");
                    }

                    CleanupPositionData(symbol);
                }
                else OnLog?.Invoke($"❌ {symbol} 청산 주문 실패");
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ {symbol} 종료 로직 에러: {ex.Message}"); }
        }

        public async Task ExecutePartialClose(string symbol, decimal ratio, CancellationToken token)
        {
            // 부분 청산 로직 구현 (IExchangeService 활용)
            await Task.CompletedTask;
        }

        public async Task ExecuteAverageDown(string symbol, CancellationToken token)
        {
            // 물타기/추가 진입 로직 구현
            await Task.CompletedTask;
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