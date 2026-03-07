using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Strategies;

namespace TradingBot.Services.Backtest
{
    /// <summary>
    /// 현재 TransformerStrategy 주문 로직을 과거 데이터에 대해 시뮬레이션합니다.
    ///
    /// 시뮬레이션 항목:
    ///  · HybridStrategyScorer (AI 예측 → 지표 검증 5개 항목)
    ///  · 컴포넌트 최소점수 게이트 (Adaptive EW: AI≥38→EW무시, AI≥35→EW≥3, 기본→EW≥5, Vol≥5, RSI/M≥3, BB≥4)
    ///  · 상위 타임프레임 페널티 (15분봉 상단 저항 -20, 1시간봉 역추세 -15, 횡보 스퀴즈 -10)
    ///  · ATR 기반 동적 임계값 (60/65/70/80)
    ///  · ADX 기반 횡보/추세 모드 구분
    ///
    /// AI 예측값은 실제 미래 가격변화율로 대체합니다 (백테스트 특성상 "완벽한 AI"를 가정).
    /// 이렇게 하면 전략 로직 자체의 진입 기각/수락 판단력을 순수하게 평가할 수 있습니다.
    ///
    /// 옵션: PerfectAI=false 설정 시 AI 예측을 랜덤 노이즈로 설정하여
    /// "AI 예측이 부정확할 때" 게이트/페널티가 얼마나 보호하는지 평가합니다.
    /// </summary>
    public class HybridStrategyBacktester
    {
        // ═══════════ 설정 ═══════════
        public decimal InitialBalance { get; set; } = 1000m;
        public decimal Leverage { get; set; } = 20m;
        public decimal PositionSizePercent { get; set; } = 0.10m; // 잔고의 10%
        public int FutureLookAhead { get; set; } = 10;            // 미래 10봉(50분) 확인
        public decimal TakeProfitPct { get; set; } = 0.025m;      // +2.5% (ROE +50%)
        public decimal StopLossPct { get; set; } = 0.010m;        // -1.0% (ROE -20%)
        public decimal FeeRate { get; set; } = 0.0004m;           // 0.04% taker fee
        public bool PerfectAI { get; set; } = true;               // true=완벽한 AI, false=노이즈
        public bool EnableComponentGate { get; set; } = true;      // 컴포넌트 최소점수 게이트 ON/OFF
        public int AdxPeriod { get; set; } = 14;
        public double AdxSidewaysThreshold { get; set; } = 20.0;

        // ═══════════ 결과 ═══════════
        public class BacktestTrade
        {
            public string Symbol { get; set; } = "";
            public DateTime EntryTime { get; set; }
            public DateTime ExitTime { get; set; }
            public string Direction { get; set; } = ""; // LONG/SHORT
            public decimal EntryPrice { get; set; }
            public decimal ExitPrice { get; set; }
            public string ExitReason { get; set; } = ""; // TP/SL/TIMEOUT
            public decimal PnL { get; set; }              // USDT
            public decimal PnLPercent { get; set; }       // ROE%
            public double FinalScore { get; set; }
            public string ScoreBreakdown { get; set; } = "";
            public double HtfPenalty { get; set; }
            public string Mode { get; set; } = ""; // TREND/SIDEWAYS
        }

        public class BacktestSummary
        {
            public string Symbol { get; set; } = "";
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public int TotalCandles { get; set; }
            public int TotalSignals { get; set; }
            public int GateRejections { get; set; }
            public int HtfRejections { get; set; }
            public int TotalTrades { get; set; }
            public int WinCount { get; set; }
            public int LossCount { get; set; }
            public decimal WinRate => TotalTrades > 0 ? (decimal)WinCount / TotalTrades * 100 : 0;
            public decimal InitialBalance { get; set; }
            public decimal FinalBalance { get; set; }
            public decimal TotalPnL => FinalBalance - InitialBalance;
            public decimal TotalPnLPercent => InitialBalance > 0 ? TotalPnL / InitialBalance * 100 : 0;
            public decimal MaxDrawdown { get; set; }
            public decimal MaxDrawdownPercent { get; set; }
            public decimal AvgWin { get; set; }
            public decimal AvgLoss { get; set; }
            public decimal ProfitFactor { get; set; }
            public List<BacktestTrade> Trades { get; set; } = new();
            public List<decimal> EquityCurve { get; set; } = new();
        }

        // ═══════════ 실행 ═══════════

        public async Task<BacktestSummary> RunAsync(string symbol, int days = 3, Action<string>? onLog = null)
        {
            onLog?.Invoke($"═══════════ 하이브리드 전략 백테스트 시작 ═══════════");
            onLog?.Invoke($"심볼: {symbol} | 기간: 최근 {days}일 | 초기자본: {InitialBalance} USDT");
            onLog?.Invoke($"레버리지: {Leverage}x | TP: {TakeProfitPct * 100:F1}% | SL: {StopLossPct * 100:F1}%");
            onLog?.Invoke($"AI 모드: {(PerfectAI ? "완벽한 AI (실제 미래값)" : "노이즈 AI")} | 게이트: {(EnableComponentGate ? "ON" : "OFF")}");
            onLog?.Invoke("");

            var endUtc = DateTime.UtcNow;
            var startUtc = endUtc.AddDays(-days);

            // 1. 데이터 수집 (5분봉, 15분봉, 1시간봉)
            onLog?.Invoke("📡 데이터 수집 중...");
            using var client = new BinanceRestClient();

            var klines5m = await FetchKlinesAsync(client, symbol, KlineInterval.FiveMinutes, startUtc, endUtc);
            var klines15m = await FetchKlinesAsync(client, symbol, KlineInterval.FifteenMinutes, startUtc, endUtc);
            var klines1h = await FetchKlinesAsync(client, symbol, KlineInterval.OneHour, startUtc, endUtc);

            onLog?.Invoke($"  5분봉: {klines5m.Count}개 | 15분봉: {klines15m.Count}개 | 1시간봉: {klines1h.Count}개");

            if (klines5m.Count < 240)
            {
                onLog?.Invoke("❌ 5분봉 데이터 부족 (최소 240개 필요)");
                return new BacktestSummary { Symbol = symbol };
            }

            // 2. 시뮬레이션 루프
            var scorer = new HybridStrategyScorer();
            var summary = new BacktestSummary
            {
                Symbol = symbol,
                StartDate = klines5m.First().OpenTime,
                EndDate = klines5m.Last().OpenTime,
                TotalCandles = klines5m.Count,
                InitialBalance = InitialBalance
            };

            decimal balance = InitialBalance;
            decimal peakBalance = balance;
            decimal maxDrawdown = 0;
            bool inPosition = false;

            // 240봉 워밍업 후 시작
            int startIdx = 240;
            int totalSignals = 0;
            int gateRejections = 0;
            int htfRejections = 0;

            onLog?.Invoke($"📊 시뮬레이션 시작 (인덱스 {startIdx}~{klines5m.Count - FutureLookAhead - 1})");
            onLog?.Invoke("");

            for (int i = startIdx; i < klines5m.Count - FutureLookAhead; i++)
            {
                if (inPosition) continue; // 포지션 중이면 스킵 (아래 포지션 관리에서 i 전진)

                var currentKlines = klines5m.GetRange(0, i + 1);
                var currentCandle = klines5m[i];
                decimal currentPrice = currentCandle.ClosePrice;

                // 2.1. ATR 동적 임계값
                double atr = IndicatorCalculator.CalculateATR(currentKlines, 14);
                double dynamicThreshold = CalculateDynamicThreshold(currentPrice, atr);

                // 2.2. ADX 모드 판별
                var (adx, plusDi, minusDi) = IndicatorCalculator.CalculateADX(currentKlines, AdxPeriod);
                bool isSidewaysMarket = adx < AdxSidewaysThreshold;
                bool isXrpSymbol = symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);

                // 2.3. 기술적 컨텍스트 구성
                var ctx = BuildContext(currentKlines, currentPrice);

                // 2.4. AI 예측값 결정 (미래 N봉 가격변화율 사용)
                decimal futurePrice = klines5m[i + FutureLookAhead].ClosePrice;
                decimal predictedChange;
                if (PerfectAI)
                {
                    predictedChange = currentPrice > 0 ? (futurePrice - currentPrice) / currentPrice : 0;
                }
                else
                {
                    // 노이즈 AI: 실제 방향에 ±랜덤 오프셋
                    var rng = new Random(i); // 재현성 보장
                    double noise = (rng.NextDouble() - 0.5) * 0.02; // ±1%
                    predictedChange = currentPrice > 0 ? (futurePrice - currentPrice) / currentPrice + (decimal)noise : 0;
                }
                decimal predictedPrice = currentPrice * (1 + predictedChange);

                // 2.5. 하이브리드 스코어링
                var longResult = scorer.EvaluateLong(symbol, predictedChange, predictedPrice, ctx);
                var shortResult = scorer.EvaluateShort(symbol, predictedChange, predictedPrice, ctx);

                // 메이저코인 보정
                if (HybridStrategyScorer.IsMajorCoin(symbol))
                {
                    double longBonus = HybridStrategyScorer.GetMajorCoinBonus(symbol, "LONG", ctx);
                    double shortBonus = HybridStrategyScorer.GetMajorCoinBonus(symbol, "SHORT", ctx);
                    longResult.FinalScore = Math.Clamp(longResult.FinalScore + longBonus, 0, 100);
                    shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + shortBonus, 0, 100);
                }

                // 2.6. 상위 타임프레임 페널티 (로컬 계산)
                string candidateDir = longResult.FinalScore > shortResult.FinalScore ? "LONG" : "SHORT";
                double htfPenalty = CalculateHTFPenaltyLocal(symbol, currentPrice, candidateDir,
                    currentCandle.OpenTime, klines15m, klines1h, klines5m.GetRange(Math.Max(0, i - 29), Math.Min(30, i + 1)));

                longResult.FinalScore = Math.Clamp(longResult.FinalScore + htfPenalty, 0, 100);
                shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + htfPenalty, 0, 100);

                // 2.7. 진입 조건 확인
                string direction = "";
                double finalScore = 0;
                string scoreBreakdown = "";
                HybridStrategyScorer.HybridScoreResult? winningResult = null;

                if (isSidewaysMarket)
                {
                    bool sideLong =
                        currentPrice <= (decimal)ctx.BbLower * 1.001m &&
                        ctx.RSI <= 35.0 &&
                        predictedChange > 0 &&
                        ctx.VolumeRatio < 1.5;

                    bool sideShort =
                        currentPrice >= (decimal)ctx.BbUpper * 0.999m &&
                        ctx.RSI >= 65.0 &&
                        predictedChange < 0 &&
                        ctx.VolumeRatio < 1.5;

                    if (isXrpSymbol)
                    {
                        bool xrpLongRelaxed =
                            predictedChange >= 0.001m &&
                            ctx.RSI <= 55.0 &&
                            currentPrice <= (decimal)ctx.BbMid * 1.003m &&
                            ctx.VolumeRatio < 1.8;

                        bool xrpShortRelaxed =
                            predictedChange <= -0.001m &&
                            ctx.RSI >= 35.0 &&
                            currentPrice >= (decimal)ctx.BbMid * 0.997m &&
                            ctx.VolumeRatio < 1.8;

                        sideLong = sideLong || xrpLongRelaxed;
                        sideShort = sideShort || xrpShortRelaxed;
                    }

                    if (sideLong)
                    {
                        totalSignals++;
                        direction = "LONG";
                        finalScore = longResult.FinalScore;
                        winningResult = longResult;
                        scoreBreakdown = $"SIDEWAYS AI:{longResult.AiPredictionScore:F0} EW:{longResult.ElliottWaveScore:F0} Vol:{longResult.VolumeMomentumScore:F0} RSI:{longResult.RsiMacdScore:F0} BB:{longResult.BollingerScore:F0}";
                    }
                    else if (sideShort)
                    {
                        totalSignals++;
                        direction = "SHORT";
                        finalScore = shortResult.FinalScore;
                        winningResult = shortResult;
                        scoreBreakdown = $"SIDEWAYS AI:{shortResult.AiPredictionScore:F0} EW:{shortResult.ElliottWaveScore:F0} Vol:{shortResult.VolumeMomentumScore:F0} RSI:{shortResult.RsiMacdScore:F0} BB:{shortResult.BollingerScore:F0}";
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    bool longStrongOverride = longResult.FinalScore >= (dynamicThreshold + 3.0) && predictedChange >= 0.0035m;
                    bool shortStrongOverride = shortResult.FinalScore >= (dynamicThreshold + 3.0) && predictedChange <= -0.0035m;
                    bool xrpTrendLongOverride =
                        isXrpSymbol &&
                        predictedChange >= 0.0035m &&
                        longResult.AiPredictionScore >= 30.0 &&
                        longResult.BollingerScore >= 8.0 &&
                        longResult.RsiMacdScore >= 5.0 &&
                        longResult.FinalScore >= 45.0 &&
                        (longResult.FinalScore - shortResult.FinalScore) >= 12.0;

                    bool safeToLong = plusDi >= minusDi || longStrongOverride || xrpTrendLongOverride;
                    bool safeToShort = minusDi > plusDi || shortStrongOverride;
                    bool longThresholdPassed = longResult.FinalScore >= dynamicThreshold || xrpTrendLongOverride;

                    if (longThresholdPassed && longResult.FinalScore > shortResult.FinalScore && safeToLong)
                    {
                        totalSignals++;
                        bool longGatePassed = longResult.PassesComponentGate(out var failReason);
                        if (EnableComponentGate && !longGatePassed && !xrpTrendLongOverride)
                        {
                            gateRejections++;
                            onLog?.Invoke($"  🚫 LONG 게이트기각 {currentCandle.OpenTime:MM/dd HH:mm} | Score:{longResult.FinalScore:F1} | {failReason} | AI:{longResult.AiPredictionScore:F0} EW:{longResult.ElliottWaveScore:F0} Vol:{longResult.VolumeMomentumScore:F0} RSI:{longResult.RsiMacdScore:F0} BB:{longResult.BollingerScore:F0}");
                            continue;
                        }
                        if (EnableComponentGate && !longGatePassed && xrpTrendLongOverride)
                        {
                            onLog?.Invoke($"  ⚡ XRP LONG 게이트우회 {currentCandle.OpenTime:MM/dd HH:mm} | Score:{longResult.FinalScore:F1} | {failReason}");
                        }
                        direction = "LONG";
                        finalScore = longResult.FinalScore;
                        winningResult = longResult;
                        scoreBreakdown = $"AI:{longResult.AiPredictionScore:F0} EW:{longResult.ElliottWaveScore:F0} Vol:{longResult.VolumeMomentumScore:F0} RSI:{longResult.RsiMacdScore:F0} BB:{longResult.BollingerScore:F0}";
                    }
                    else if (shortResult.FinalScore >= dynamicThreshold && shortResult.FinalScore > longResult.FinalScore && safeToShort)
                    {
                        totalSignals++;
                        if (EnableComponentGate && !shortResult.PassesComponentGate(out var failReason))
                        {
                            gateRejections++;
                            onLog?.Invoke($"  🚫 SHORT 게이트기각 {currentCandle.OpenTime:MM/dd HH:mm} | Score:{shortResult.FinalScore:F1} | {failReason} | AI:{shortResult.AiPredictionScore:F0} EW:{shortResult.ElliottWaveScore:F0} Vol:{shortResult.VolumeMomentumScore:F0} RSI:{shortResult.RsiMacdScore:F0} BB:{shortResult.BollingerScore:F0}");
                            continue;
                        }
                        direction = "SHORT";
                        finalScore = shortResult.FinalScore;
                        winningResult = shortResult;
                        scoreBreakdown = $"AI:{shortResult.AiPredictionScore:F0} EW:{shortResult.ElliottWaveScore:F0} Vol:{shortResult.VolumeMomentumScore:F0} RSI:{shortResult.RsiMacdScore:F0} BB:{shortResult.BollingerScore:F0}";
                    }
                    else
                    {
                        continue; // 조건 미충족
                    }
                }

                // 2.8. 포지션 진입 → 미래 봉에서 TP/SL 확인
                decimal entryPrice = currentPrice;
                decimal tp, sl;
                if (direction == "LONG")
                {
                    tp = entryPrice * (1 + TakeProfitPct);
                    sl = entryPrice * (1 - StopLossPct);
                }
                else
                {
                    tp = entryPrice * (1 - TakeProfitPct);
                    sl = entryPrice * (1 + StopLossPct);
                }

                string exitReason = "TIMEOUT";
                decimal exitPrice = klines5m[i + FutureLookAhead].ClosePrice; // 기본: 타임아웃 시 종가
                DateTime exitTime = klines5m[i + FutureLookAhead].OpenTime;

                for (int j = i + 1; j <= i + FutureLookAhead && j < klines5m.Count; j++)
                {
                    var futureCandle = klines5m[j];

                    if (direction == "LONG")
                    {
                        if (futureCandle.LowPrice <= sl)
                        {
                            exitReason = "SL";
                            exitPrice = sl;
                            exitTime = futureCandle.OpenTime;
                            break;
                        }
                        if (futureCandle.HighPrice >= tp)
                        {
                            exitReason = "TP";
                            exitPrice = tp;
                            exitTime = futureCandle.OpenTime;
                            break;
                        }
                    }
                    else // SHORT
                    {
                        if (futureCandle.HighPrice >= sl)
                        {
                            exitReason = "SL";
                            exitPrice = sl;
                            exitTime = futureCandle.OpenTime;
                            break;
                        }
                        if (futureCandle.LowPrice <= tp)
                        {
                            exitReason = "TP";
                            exitPrice = tp;
                            exitTime = futureCandle.OpenTime;
                            break;
                        }
                    }
                }

                // 2.9. PnL 계산
                decimal priceChange;
                if (direction == "LONG")
                    priceChange = (exitPrice - entryPrice) / entryPrice;
                else
                    priceChange = (entryPrice - exitPrice) / entryPrice;

                decimal positionSize = balance * PositionSizePercent;
                decimal pnl = positionSize * Leverage * priceChange - positionSize * Leverage * FeeRate * 2; // 진입+청산 수수료
                decimal pnlPercent = Leverage * priceChange * 100 - Leverage * FeeRate * 2 * 100;

                balance += pnl;
                if (balance > peakBalance) peakBalance = balance;
                decimal dd = peakBalance > 0 ? (peakBalance - balance) / peakBalance * 100 : 0;
                if (dd > maxDrawdown) maxDrawdown = dd;

                var trade = new BacktestTrade
                {
                    Symbol = symbol,
                    EntryTime = currentCandle.OpenTime,
                    ExitTime = exitTime,
                    Direction = direction,
                    EntryPrice = entryPrice,
                    ExitPrice = exitPrice,
                    ExitReason = exitReason,
                    PnL = pnl,
                    PnLPercent = pnlPercent,
                    FinalScore = finalScore,
                    ScoreBreakdown = scoreBreakdown,
                    HtfPenalty = htfPenalty,
                    Mode = "TREND"
                };

                summary.Trades.Add(trade);
                summary.EquityCurve.Add(balance);

                bool isWin = pnl > 0;
                string emoji = isWin ? "✅" : "❌";
                onLog?.Invoke($"  {emoji} #{summary.Trades.Count} {direction} {currentCandle.OpenTime:MM/dd HH:mm} | " +
                    $"Score:{finalScore:F1} HTF:{htfPenalty:F0} | {entryPrice:F2}→{exitPrice:F2} ({exitReason}) | " +
                    $"PnL:{pnl:F2} ({pnlPercent:F1}%) | 잔고:{balance:F2}");

                // 포지션 종료 후 FutureLookAhead 봉만큼 건너뜀 (중복 진입 방지)
                i += FutureLookAhead;
            }

            // 3. 최종 집계
            summary.TotalSignals = totalSignals;
            summary.GateRejections = gateRejections;
            summary.HtfRejections = htfRejections;
            summary.TotalTrades = summary.Trades.Count;
            summary.WinCount = summary.Trades.Count(t => t.PnL > 0);
            summary.LossCount = summary.Trades.Count(t => t.PnL <= 0);
            summary.FinalBalance = balance;
            summary.MaxDrawdown = maxDrawdown;
            summary.MaxDrawdownPercent = maxDrawdown;

            var wins = summary.Trades.Where(t => t.PnL > 0).ToList();
            var losses = summary.Trades.Where(t => t.PnL <= 0).ToList();
            summary.AvgWin = wins.Count > 0 ? wins.Average(t => t.PnL) : 0;
            summary.AvgLoss = losses.Count > 0 ? losses.Average(t => t.PnL) : 0;
            decimal grossProfit = wins.Sum(t => t.PnL);
            decimal grossLoss = Math.Abs(losses.Sum(t => t.PnL));
            summary.ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 999 : 0;

            // 4. 결과 출력
            onLog?.Invoke("");
            onLog?.Invoke($"═══════════ 백테스트 결과 ═══════════");
            onLog?.Invoke($"기간: {summary.StartDate:yyyy-MM-dd HH:mm} ~ {summary.EndDate:yyyy-MM-dd HH:mm}");
            onLog?.Invoke($"총 캔들: {summary.TotalCandles}개 | 총 신호: {summary.TotalSignals}개");
            onLog?.Invoke($"게이트 기각: {summary.GateRejections}건 | HTF 기각: {summary.HtfRejections}건");
            onLog?.Invoke($"─────────────────────────────");
            onLog?.Invoke($"총 거래: {summary.TotalTrades}회");
            onLog?.Invoke($"승: {summary.WinCount}회 | 패: {summary.LossCount}회 | 승률: {summary.WinRate:F1}%");
            onLog?.Invoke($"─────────────────────────────");
            onLog?.Invoke($"초기 자본: {summary.InitialBalance:F2} USDT");
            onLog?.Invoke($"최종 잔고: {summary.FinalBalance:F2} USDT");
            onLog?.Invoke($"총 수익: {summary.TotalPnL:F2} USDT ({summary.TotalPnLPercent:F2}%)");
            onLog?.Invoke($"─────────────────────────────");
            onLog?.Invoke($"평균 수익(승): {summary.AvgWin:F2} USDT");
            onLog?.Invoke($"평균 손실(패): {summary.AvgLoss:F2} USDT");
            onLog?.Invoke($"수익 팩터: {summary.ProfitFactor:F2}");
            onLog?.Invoke($"최대 낙폭(MDD): {summary.MaxDrawdownPercent:F2}%");
            onLog?.Invoke($"═══════════════════════════════");

            return summary;
        }

        /// <summary>
        /// 여러 심볼을 동시에 백테스트하여 종합 결과를 반환합니다.
        /// </summary>
        public async Task<List<BacktestSummary>> RunMultiAsync(string[] symbols, int days = 3, Action<string>? onLog = null)
        {
            var results = new List<BacktestSummary>();

            foreach (var symbol in symbols)
            {
                try
                {
                    var result = await RunAsync(symbol, days, onLog);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"⚠️ {symbol} 백테스트 실패: {ex.Message}");
                }

                // API 레이트 리밋 방지
                await Task.Delay(500);
            }

            // 종합 결과
            if (results.Count > 1)
            {
                onLog?.Invoke("");
                onLog?.Invoke($"═══════════ 종합 결과 ({results.Count}개 심볼) ═══════════");
                int totalTrades = results.Sum(r => r.TotalTrades);
                int totalWins = results.Sum(r => r.WinCount);
                decimal totalPnL = results.Sum(r => r.TotalPnL);
                decimal overallWinRate = totalTrades > 0 ? (decimal)totalWins / totalTrades * 100 : 0;

                onLog?.Invoke($"총 거래: {totalTrades}회 | 승률: {overallWinRate:F1}%");
                onLog?.Invoke($"총 수익: {totalPnL:F2} USDT");

                foreach (var r in results.OrderByDescending(r => r.TotalPnL))
                {
                    string emoji = r.TotalPnL >= 0 ? "📈" : "📉";
                    onLog?.Invoke($"  {emoji} {r.Symbol}: {r.TotalTrades}거래 | 승률:{r.WinRate:F0}% | PnL:{r.TotalPnL:F2}");
                }
            }

            return results;
        }

        // ═══════════ 헬퍼 메서드 ═══════════

        private async Task<List<IBinanceKline>> FetchKlinesAsync(
            BinanceRestClient client, string symbol, KlineInterval interval,
            DateTime startUtc, DateTime endUtc)
        {
            var all = new List<IBinanceKline>();
            var cursor = startUtc;

            for (int chunk = 0; chunk < 20 && cursor < endUtc; chunk++)
            {
                var response = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, interval, startTime: cursor, endTime: endUtc, limit: 1500);

                if (!response.Success) break;
                var batch = response.Data?.OrderBy(k => k.OpenTime).ToList();
                if (batch == null || batch.Count == 0) break;

                all.AddRange(batch);

                int minutes = interval switch
                {
                    KlineInterval.FiveMinutes => 5,
                    KlineInterval.FifteenMinutes => 15,
                    KlineInterval.OneHour => 60,
                    _ => 5
                };

                var nextCursor = batch.Last().OpenTime.AddMinutes(minutes);
                if (nextCursor <= cursor || batch.Count < 1500) break;
                cursor = nextCursor;
            }

            return all
                .GroupBy(k => k.OpenTime)
                .Select(g => g.First())
                .OrderBy(k => k.OpenTime)
                .ToList();
        }

        private HybridStrategyScorer.TechnicalContext BuildContext(
            List<IBinanceKline> klines, decimal currentPrice)
        {
            double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
            var bb = IndicatorCalculator.CalculateBB(klines, 20, 2);
            var macd = IndicatorCalculator.CalculateMACD(klines);
            var fib = IndicatorCalculator.CalculateFibonacci(klines, 120);
            double sma20 = IndicatorCalculator.CalculateSMA(klines, 20);
            double sma50 = IndicatorCalculator.CalculateSMA(klines, 50);
            double sma200 = IndicatorCalculator.CalculateSMA(klines, 200);
            bool elliottUptrend = IndicatorCalculator.AnalyzeElliottWave(klines);

            var recent20 = klines.TakeLast(20).ToList();
            double avgVolume = recent20.Average(k => (double)k.Volume);
            double currentVolume = (double)recent20.Last().Volume;
            double prevVolume = recent20.Count >= 2 ? (double)recent20[^2].Volume : currentVolume;
            double volumeRatio = avgVolume > 0 ? currentVolume / avgVolume : 1;
            double volumeMomentum = prevVolume > 0 ? currentVolume / prevVolume : 1;

            double rsiDivergence = 0;
            if (klines.Count >= 6)
            {
                var prevSubset = klines.GetRange(0, klines.Count - 5);
                var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                decimal priceDelta = currentPrice - klines[klines.Count - 6].ClosePrice;
                double rsiDelta = rsi - prevRsi;
                if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;
                else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1;
            }

            double bbMid = (bb.Upper + bb.Lower) / 2.0;
            double bbWidth = bbMid > 0 ? (bb.Upper - bb.Lower) / bbMid * 100 : 0;

            return new HybridStrategyScorer.TechnicalContext
            {
                CurrentPrice = currentPrice,
                BbUpper = bb.Upper,
                BbMid = bbMid,
                BbLower = bb.Lower,
                BbWidth = bbWidth,
                RSI = rsi,
                MacdHist = macd.Hist,
                MacdLine = macd.Macd,
                MacdSignal = macd.Signal,
                Sma20 = sma20,
                Sma50 = sma50,
                Sma200 = sma200,
                IsElliottUptrend = elliottUptrend,
                ElliottPhase = "Idle", // 백테스트에서는 Wave3Strategy 없이 Idle 사용
                Fib382 = (decimal)fib.Level382,
                Fib500 = (decimal)fib.Level500,
                Fib618 = (decimal)fib.Level618,
                VolumeRatio = volumeRatio,
                VolumeMomentum = volumeMomentum,
                RsiDivergence = rsiDivergence
            };
        }

        /// <summary>
        /// 상위 타임프레임 페널티를 로컬 캔들 데이터에서 계산합니다 (API 호출 없음).
        /// </summary>
        private double CalculateHTFPenaltyLocal(
            string symbol, decimal currentPrice, string direction,
            DateTime currentTime,
            List<IBinanceKline> klines15m, List<IBinanceKline> klines1h,
            List<IBinanceKline> recent5m)
        {
            double totalPenalty = 0;

            // ── 15분봉 상단 저항 ──
            var relevant15m = klines15m.Where(k => k.OpenTime <= currentTime).TakeLast(20).ToList();
            if (relevant15m.Count >= 20)
            {
                var bb15m = IndicatorCalculator.CalculateBB(relevant15m, 20, 2);
                double bbRange = bb15m.Upper - bb15m.Lower;
                double percentB = bbRange > 0 ? ((double)currentPrice - bb15m.Lower) / bbRange : 0.5;
                var lastCandle15m = relevant15m.Last();
                bool isBearish = lastCandle15m.ClosePrice < lastCandle15m.OpenPrice;

                if (direction == "LONG" && percentB > 0.85 && isBearish)
                    totalPenalty -= 12;
                else if (direction == "SHORT" && percentB < 0.15 && !isBearish)
                    totalPenalty -= 12;
            }

            // ── 1시간봉 역추세 ──
            var relevant1h = klines1h.Where(k => k.OpenTime <= currentTime).TakeLast(20).ToList();
            if (relevant1h.Count >= 20)
            {
                double sma20_1h = IndicatorCalculator.CalculateSMA(relevant1h, 20);

                if (direction == "LONG" && (double)currentPrice < sma20_1h)
                    totalPenalty -= 8;
                else if (direction == "SHORT" && (double)currentPrice > sma20_1h)
                    totalPenalty -= 8;
            }

            // ── 횡보장 스퀴즈 필터 ──
            if (recent5m.Count >= 24)
            {
                int squeezeCount = 0;
                for (int k = Math.Max(0, recent5m.Count - 24); k < recent5m.Count; k++)
                {
                    var subset = recent5m.GetRange(0, k + 1);
                    if (subset.Count >= 20)
                    {
                        var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                        double mid = (bb.Upper + bb.Lower) / 2.0;
                        double width = mid > 0 ? (bb.Upper - bb.Lower) / mid * 100 : 0;
                        if (width < 0.5) squeezeCount++;
                    }
                }
                if (squeezeCount >= 20)
                    totalPenalty -= 6;
            }

            return totalPenalty;
        }

        private double CalculateDynamicThreshold(decimal currentPrice, double atr)
        {
            double atrPercentage = (atr / (double)currentPrice) * 100;

            if (atrPercentage < 0.15) return 58.0;
            else if (atrPercentage < 0.30) return 63.0;
            else if (atrPercentage < 0.50) return 68.0;
            else return 75.0;
        }
    }
}
