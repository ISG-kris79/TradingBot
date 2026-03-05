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
            
            // [추가] RSI 분할매도 추적
            bool rsiPartial1Taken = false;  // RSI ≥ 80 → 50% 분할매도
            bool rsiPartial2Taken = false;  // RSI ≥ 85 또는 RSI 80+ 지속 → 잔량 청산
            int rsiAbove80Count = 0;        // RSI 80 이상 연속 캔들 수
            
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
                        result.EquityCurve.Add(currentBalance + (positionQuantity * currentPrice));
                    }

                    // 2. RSI ≥ 80 단계별 분할매도 (전량 청산 → 50%+잔량 분할로 개선)
                    if (partialTaken && rsiList[i] >= 80)
                    {
                        rsiAbove80Count++;

                        // 2-1. RSI ≥ 85 극단 과매수 → 즉시 전량 청산
                        if (rsiList[i] >= 85)
                        {
                            shouldExit = true;
                            exitReason = $"RSI_극단과매수_{rsiList[i]:F1}";
                        }
                        // 2-2. RSI 80~85 최초 도달 → 50% 분할매도
                        else if (!rsiPartial1Taken)
                        {
                            rsiPartial1Taken = true;
                            decimal closeQty = positionQuantity * 0.5m;
                            if (closeQty > 0 && positionQuantity > closeQty)
                            {
                                decimal revenue = closeQty * currentPrice;
                                decimal profit = revenue - (closeQty * entryPrice);
                                currentBalance += revenue;
                                positionQuantity -= closeQty;

                                result.TradeHistory.Add(new TradeLog(symbol, "SELL", $"RSI_분할매도_50%_RSI{rsiList[i]:F1}", currentPrice, 0, currentCandle.OpenTime, profit, (profit / revenue) * 100));
                                result.EquityCurve.Add(currentBalance + (positionQuantity * currentPrice));

                                // ATR 트레일링을 더 타이트하게 조정 (0.3x 멀티플라이어)
                                decimal tightStop = highestPriceSinceEntry - (decimal)(atrList[i] * 0.3);
                                if (tightStop > currentTrailingStopPrice)
                                    currentTrailingStopPrice = tightStop;
                            }
                        }
                        // 2-3. RSI 80+ 3캔들 이상 지속 → 잔량 전량 청산
                        else if (rsiPartial1Taken && rsiAbove80Count >= 3)
                        {
                            shouldExit = true;
                            exitReason = $"RSI_과매수_지속_{rsiAbove80Count}캔들_RSI{rsiList[i]:F1}";
                        }
                    }
                    else
                    {
                        rsiAbove80Count = 0; // RSI 80 미만이면 카운터 리셋
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
                        result.EquityCurve.Add(currentBalance);
                        result.TradeDates.Add(currentCandle.OpenTime.ToString("MM/dd HH:mm"));

                        inPosition = false;
                        positionQuantity = 0;
                        partialTaken = false;
                        highestPriceSinceEntry = 0;
                        currentTrailingStopPrice = 0;
                        highestROE = 0;
                        previousHitBBUpper = false;
                        rsiPartial1Taken = false;
                        rsiPartial2Taken = false;
                        rsiAbove80Count = 0;
                        strategy.ResetState(symbol);
                    }
                }
            }
            result.FinalBalance = currentBalance + (inPosition ? positionQuantity * (decimal)candles.Last().Close : 0);
        }

        /// <summary>
        /// ATR 기반 동적 트레일링 스톱 계산 (강화 버전)
        /// ─────────────────────────────────────
        /// ROE 단계별 멀티플라이어 + RSI 과열 반영 + 수익 보장선
        /// </summary>
        private decimal CalculateDynamicTrailingStop(
            decimal entryPrice,
            decimal highestPrice,
            double currentAtr,
            double currentRsi)
        {
            decimal priceChangeRate = (highestPrice - entryPrice) / entryPrice;
            decimal roe = priceChangeRate * 20 * 100;

            // [강화] ROE 단계별 + RSI 결합 ATR 멀티플라이어
            double atrMultiplier;
            if (roe < 5)
                atrMultiplier = 2.0;  // 진입 초기: 넓은 방어 (변동성 흡수)
            else if (roe < 10)
                atrMultiplier = 1.5;  // 초반 수익: 기본 방어
            else if (roe >= 10 && roe < 20 && currentRsi < 70)
                atrMultiplier = 1.0;  // 추세 진행 중
            else if (roe >= 20 && currentRsi < 70)
                atrMultiplier = 0.7;  // 고수익 구간: 점진적 타이트닝
            else if (currentRsi >= 70 && currentRsi < 80)
                atrMultiplier = 0.4;  // 과열 접근: 강한 타이트닝
            else if (currentRsi >= 80 && currentRsi < 85)
                atrMultiplier = 0.2;  // 극단 과열: 밀착 트레일링
            else
                atrMultiplier = 0.1;  // RSI 85+: 초밀착 (거의 피크)

            decimal trailingDistance = (decimal)(currentAtr * atrMultiplier);
            decimal calculatedStopPrice = highestPrice - trailingDistance;

            // [강화] ROE 기반 단계별 수익 보장선
            if (roe >= 10)
            {
                // ROE 10%+ → 최소 ROE 5% 보장
                decimal minGuaranteePrice = entryPrice * (1 + 5m / 20m / 100m);  // ROE 5% 지점
                if (calculatedStopPrice < minGuaranteePrice)
                    calculatedStopPrice = minGuaranteePrice;
            }
            if (roe >= 15)
            {
                // ROE 15%+ → 최소 ROE 10% 보장
                decimal minGuaranteePrice = entryPrice * (1 + 10m / 20m / 100m);
                if (calculatedStopPrice < minGuaranteePrice)
                    calculatedStopPrice = minGuaranteePrice;
            }
            if (roe >= 25)
            {
                // ROE 25%+ → 최소 ROE 18% 보장
                decimal minGuaranteePrice = entryPrice * (1 + 18m / 20m / 100m);
                if (calculatedStopPrice < minGuaranteePrice)
                    calculatedStopPrice = minGuaranteePrice;
            }

            return calculatedStopPrice;
        }
    }
}
