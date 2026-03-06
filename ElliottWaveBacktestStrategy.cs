using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;
using TradingBot.Strategies;
using TradingBot.Shared.Models;
using TradeLog = TradingBot.Shared.Models.TradeLog;
using CandleData = TradingBot.Models.CandleData;

namespace TradingBot.Services.BacktestStrategies
{
    public class ElliottWaveBacktestStrategy : IBacktestStrategy
    {
        public string Name => "Elliott Wave 3-Wave Strategy (Hybrid Exit)";

        public void Execute(List<CandleData> candles, BacktestResult result)
        {
            var strategy = new ElliottWave3WaveStrategy();
            var symbol = result.Symbol;
            decimal currentBalance = result.InitialBalance;
            decimal positionQuantity = 0;
            decimal entryPrice = 0;
            bool inPosition = false;
            
            // [HybridExitManager 로직 복제]
            bool partialTaken = false; // 1차 익절 여부
            decimal highestPriceSinceEntry = 0;
            decimal currentTrailingStopPrice = 0;
            double highestROE = 0;
            bool previousHitBBUpper = false;

            result.EquityCurve.Clear();
            result.TradeDates.Clear();

            if (candles.Count > 0)
            {
                result.EquityCurve.Add(currentBalance);
                result.TradeDates.Add(candles[0].OpenTime.ToString("MM/dd HH:mm"));
            }
            
            // 지표 미리 계산 (성능 최적화)
            var closes = candles.Select(c => (double)c.Close).ToList();
            var rsiList = IndicatorCalculator.CalculateRSISeries(closes, 14);
            var (macd, signal, hist) = IndicatorCalculator.CalculateMACDSeries(closes);
            var (bbUpper, bbMid, bbLower) = IndicatorCalculator.CalculateBBSeries(closes, 20, 2.0);
            var atrList = IndicatorCalculator.CalculateATRSeries(candles, 14);

            // ElliottWave 전략 목표가/손절가 (참고용)
            decimal takeProfit1 = 0;
            decimal takeProfit2 = 0;
            decimal stopLoss = 0;

            // 데이터 순회
            for (int i = 50; i < candles.Count; i++)
            {
                var currentCandle = candles[i];
                decimal currentPrice = currentCandle.Close;
                
                // 전략 상태 업데이트를 위해 부분 리스트 전달 (실제 봇과 동일 환경 시뮬레이션)
                // 주의: DetectWave 메서드들은 전체 리스트와 인덱스를 받으므로 그대로 사용 가능
                
                var state = strategy.GetOrCreateState(symbol);

                // 1. 파동 감지 로직 실행
                if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Idle)
                {
                    strategy.DetectWave1(symbol, candles, i);
                }
                else if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave1Started)
                {
                    strategy.DetectWave2AndSetFibonacci(symbol, candles, i);
                }
                else if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave2Started)
                {
                    // [수정] RSI 다이버전스 감지 시 미래 데이터 참조 방지
                    // DetectRSIDivergence는 리스트의 마지막 요소를 현재 값으로 사용하므로,
                    // 현재 시점(i)까지의 데이터를 잘라서 전달해야 함.
                    int startIndex = Math.Max(0, i - 9);
                    int count = i - startIndex + 1;
                    
                    var recentCandles = candles.GetRange(startIndex, count);
                    var recentRsi = rsiList.GetRange(startIndex, count).Select(r => (decimal)r).ToList();
                    
                    strategy.DetectRSIDivergence(symbol, recentCandles, recentRsi);
                }
                else if (state.CurrentPhase == ElliottWave3WaveStrategy.WavePhaseType.Wave3Setup)
                {
                    bool entrySignal = strategy.ConfirmEntry(
                        symbol,
                        currentCandle,
                        (decimal)rsiList[i],
                        (decimal)macd[i],
                        (decimal)signal[i],
                        (decimal)bbMid[i],
                        (decimal)bbLower[i],
                        (decimal)bbUpper[i]
                    );

                    if (entrySignal && !inPosition)
                    {
                        // 매수 진입
                        decimal amountToInvest = currentBalance * 0.98m; // 98% 투자
                        positionQuantity = amountToInvest / currentPrice;
                        currentBalance -= amountToInvest;
                        inPosition = true;
                        entryPrice = currentPrice;
                        partialTaken = false;
                        highestPriceSinceEntry = currentPrice;
                        currentTrailingStopPrice = 0;
                        highestROE = 0;
                        previousHitBBUpper = false;

                        (takeProfit1, takeProfit2) = strategy.GetTakeProfits(symbol);
                        stopLoss = strategy.GetStopLoss(symbol);

                        result.TradeHistory.Add(new TradeLog(symbol, "BUY", "ElliottWave", currentPrice, 0, currentCandle.OpenTime));
                    }
                }
                
                // 포지션 보유 중일 때 HybridExitManager 로직 적용
                if (inPosition)
                {
                    // 최고가 갱신
                    if (currentPrice > highestPriceSinceEntry)
                        highestPriceSinceEntry = currentPrice;

                    // ROE 계산 (20배 레버리지)
                    decimal priceChangeRatio = (currentPrice - entryPrice) / entryPrice;
                    double currentROE = (double)(priceChangeRatio * 20 * 100);
                    highestROE = Math.Max(highestROE, currentROE);

                    // ATR 동적 트레일링 스톱 계산
                    decimal newTrailingStopPrice = CalculateDynamicTrailingStop(
                        entryPrice, highestPriceSinceEntry, atrList[i], rsiList[i]);

                    if (currentTrailingStopPrice == 0)
                        currentTrailingStopPrice = newTrailingStopPrice;
                    else if (newTrailingStopPrice > currentTrailingStopPrice)
                        currentTrailingStopPrice = newTrailingStopPrice;

                    bool shouldExit = false;
                    string exitReason = "";

                    // 1. 1차 익절 (ElliottWave TP1 도달 시 50% 청산)
                    if (!partialTaken && currentPrice >= takeProfit1)
                    {
                        decimal closeQty = positionQuantity * 0.5m;
                        decimal revenue = closeQty * currentPrice;
                        decimal profit = revenue - (closeQty * entryPrice);
                        
                        currentBalance += revenue;
                        positionQuantity -= closeQty;
                        partialTaken = true;

                        result.TradeHistory.Add(new TradeLog(symbol, "SELL", "ElliottWave_TP1_50%", currentPrice, 0, currentCandle.OpenTime, profit, (profit/revenue)*100));
                    }

                    // 2. RSI 80+ 과매수 전량 청산
                    if (partialTaken && rsiList[i] >= 80)
                    {
                        shouldExit = true;
                        exitReason = $"RSI_과매수_{rsiList[i]:F1}";
                    }

                    // 3. BB 상단 이탈 후 재진입 청산
                    if (partialTaken && currentPrice >= (decimal)bbUpper[i])
                        previousHitBBUpper = true;
                    
                    if (partialTaken && previousHitBBUpper && currentPrice < (decimal)bbUpper[i])
                    {
                        shouldExit = true;
                        exitReason = "BB_상단_이탈_재진입";
                    }

                    // 4. ATR 동적 트레일링 스톱
                    if (currentTrailingStopPrice > 0 && currentPrice <= currentTrailingStopPrice)
                    {
                        shouldExit = true;
                        exitReason = $"ATR_트레일링_스톱_ROE{currentROE:F1}%";
                    }

                    // 5. 절대 손절 ROE -20%
                    if (currentROE <= -20)
                    {
                        shouldExit = true;
                        exitReason = $"절대손절_ROE_{currentROE:F1}%";
                    }

                    // 6. ElliottWave 전략 조기 익절 신호
                    if (i > 0 && strategy.ShouldTakeProfitEarly(symbol, (decimal)rsiList[i], (decimal)rsiList[i-1], currentPrice, (decimal)bbUpper[i]))
                    {
                        shouldExit = true;
                        exitReason = "ElliottWave_조기익절";
                    }

                    // 7. ElliottWave TP2 도달
                    if (currentPrice >= takeProfit2)
                    {
                        shouldExit = true;
                        exitReason = "ElliottWave_TP2";
                    }

                    // 전량 청산 실행
                    if (shouldExit)
                    {
                        decimal revenue = positionQuantity * currentPrice;
                        decimal profit = revenue - (positionQuantity * entryPrice);
                        
                        currentBalance += revenue;
                        result.TotalTrades++;
                        if (profit > 0) result.WinCount++; else result.LossCount++;

                        result.TradeHistory.Add(new TradeLog(symbol, "SELL", exitReason, currentPrice, 0, currentCandle.OpenTime, profit, (profit/revenue)*100));

                        inPosition = false;
                        positionQuantity = 0;
                        partialTaken = false;
                        highestPriceSinceEntry = 0;
                        currentTrailingStopPrice = 0;
                        highestROE = 0;
                        previousHitBBUpper = false;
                        strategy.ResetState(symbol);
                    }
                }

                decimal markToMarketEquity = currentBalance + (inPosition ? positionQuantity * currentPrice : 0m);
                
                // 추가 안전장치: 음수나 0 방지
                if (markToMarketEquity <= 0)
                {
                    markToMarketEquity = result.EquityCurve.Count > 0 
                        ? result.EquityCurve[^1] 
                        : result.InitialBalance;
                }
                
                result.EquityCurve.Add(markToMarketEquity);
                result.TradeDates.Add(currentCandle.OpenTime.ToString("MM/dd HH:mm"));
            }
            
            // FinalBalance 계산 시에도 검증
            decimal finalEquity = currentBalance + (inPosition ? positionQuantity * (decimal)candles.Last().Close : 0);
            if (finalEquity <= 0)
            {
                finalEquity = result.EquityCurve.LastOrDefault(result.InitialBalance);
            }
            result.FinalBalance = finalEquity;
        }

        /// <summary>
        /// ATR 기반 동적 트레일링 스톱 계산 (HybridExitManager 로직 복제)
        /// </summary>
        private decimal CalculateDynamicTrailingStop(
            decimal entryPrice,
            decimal highestPrice,
            double currentAtr,
            double currentRsi)
        {
            decimal priceChangeRate = (highestPrice - entryPrice) / entryPrice;
            decimal roe = priceChangeRate * 20 * 100;

            // ATR 멀티플라이어 결정
            double atrMultiplier;
            if (roe < 10)
                atrMultiplier = 1.5; // 진입 직후 방어
            else if (roe >= 10 && currentRsi < 70)
                atrMultiplier = 1.0; // 추세 진행
            else if (currentRsi >= 70 && currentRsi < 80)
                atrMultiplier = 0.5; // 과열 진입
            else
                atrMultiplier = 0.2; // 극단적 과열

            decimal trailingDistance = (decimal)(currentAtr * atrMultiplier);
            decimal calculatedStopPrice = highestPrice - trailingDistance;

            // ROE 15% 이상이면 본절가 이하로 내려가지 않음
            if (roe >= 15 && calculatedStopPrice < entryPrice)
                calculatedStopPrice = entryPrice + (entryPrice * 0.001m);

            return calculatedStopPrice;
        }
    }
}
