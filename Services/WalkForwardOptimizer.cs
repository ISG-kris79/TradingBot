using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [워크포워드 최적화 엔진]
    ///
    /// 핵심 원리:
    ///   전체 데이터를 여러 롤링 윈도우로 분할하여,
    ///   각 윈도우마다 IS(In-Sample)에서 최적 파라미터를 탐색하고
    ///   OOS(Out-of-Sample)에서 검증한 뒤,
    ///   모든 OOS 결과를 이어 붙여 실전 기대 성과를 산출합니다.
    ///
    /// 구조:
    ///   ┌──────── Window 1 ────────┐
    ///   │  IS (12개월)  │ OOS (4개월) │
    ///   └──────────────┴───────────┘
    ///        ┌──────── Window 2 ────────┐
    ///        │  IS (12개월)  │ OOS (4개월) │
    ///        └──────────────┴───────────┘
    ///             ┌──────── Window 3 ────────┐
    ///             │  IS (12개월)  │ OOS (4개월) │
    ///             └──────────────┴───────────┘
    ///
    /// 탐색 파라미터 (6개, 1,296 조합):
    ///   - 진입 점수 임계값: 60, 65, 70, 75
    ///   - SL ATR 배수:     1.0, 1.5, 2.0, 2.5
    ///   - TP2 ATR 배수:    3.5, 5.0, 6.5, 8.0
    ///   - BE ATR 배수:     0.5, 0.8, 1.0
    ///   - Trail ATR 배수:  2.5, 3.5, 4.5
    ///   - Trail Gap 배수:  0.4, 0.6, 0.8
    ///
    /// 피트니스 함수:
    ///   SharpeAdjusted × (1 + PnL%) × ProfitFactor × TradeCountFactor × (1 - MDD_Penalty)
    /// </summary>
    public class WalkForwardOptimizer
    {
        // ═══ 워크포워드 설정 ═══
        private readonly int _isMonths;       // In-Sample 기간 (월)
        private readonly int _oosMonths;      // Out-of-Sample 기간 (월)
        private readonly int _stepMonths;     // 윈도우 이동 간격 (월)
        private readonly int _totalYears;     // 전체 데이터 기간 (년)
        private readonly decimal _initialBalance;
        private readonly string[] _symbols;

        // ═══ 고정 파라미터 (시뮬레이션용) ═══
        private const decimal LEVERAGE       = 20m;
        private const decimal MARGIN_PERCENT = 0.10m;
        private const decimal FEE_RATE       = 0.0004m;
        private const int MAX_CONCURRENT     = 2;

        // 지표 파라미터
        private const int RSI_PERIOD    = 14;
        private const int MACD_FAST     = 12;
        private const int MACD_SLOW     = 26;
        private const int MACD_SIGNAL   = 9;
        private const int BB_PERIOD     = 20;
        private const double BB_MULT    = 2.0;
        private const int EMA_SHORT     = 20;
        private const int EMA_LONG      = 50;
        private const int EMA_TREND     = 200;
        private const int ATR_PERIOD    = 14;
        private const int VOL_MA_PERIOD = 20;

        // ═══ 탐색 파라미터 세트 ═══
        public record ParamSet(
            double Threshold,   // 진입 점수 임계값
            double StopMult,    // SL = ATR × StopMult
            double Tp2Mult,     // TP2 = ATR × Tp2Mult (TP1 = Tp2Mult / 2)
            double BeMult,      // 본절 트리거 = ATR × BeMult
            double TrailMult,   // 트레일 시작 = ATR × TrailMult
            double GapMult      // 트레일 갭 = ATR × GapMult
        );

        // ═══ 윈도우별 결과 ═══
        public class WindowResult
        {
            public int WindowIndex { get; set; }
            public DateTime IsStart { get; set; }
            public DateTime IsEnd { get; set; }
            public DateTime OosStart { get; set; }
            public DateTime OosEnd { get; set; }
            public ParamSet BestParams { get; set; } = null!;
            public double IsFitness { get; set; }
            public double OosFitness { get; set; }

            // IS 성과
            public int IsTrades { get; set; }
            public double IsWinRate { get; set; }
            public decimal IsPnl { get; set; }
            public decimal IsMdd { get; set; }
            public decimal IsSharpe { get; set; }

            // OOS 성과
            public int OosTrades { get; set; }
            public double OosWinRate { get; set; }
            public decimal OosPnl { get; set; }
            public decimal OosMdd { get; set; }
            public decimal OosSharpe { get; set; }

            // 효율성 비율: OOS/IS 피트니스 (1.0에 가까울수록 과적합 없음)
            public double EfficiencyRatio => IsFitness > 0 ? OosFitness / IsFitness : 0;

            public List<TradeRecord> OosTrades_Detail { get; set; } = new();
        }

        // ═══ 최종 WFO 리포트 ═══
        public class WfoReport
        {
            public DateTime DataStart { get; set; }
            public DateTime DataEnd { get; set; }
            public int TotalWindows { get; set; }
            public decimal InitialBalance { get; set; }

            // OOS 합산 성과 (실전 기대치)
            public decimal OosFinalBalance { get; set; }
            public decimal OosTotalPnl => OosFinalBalance - InitialBalance;
            public decimal OosTotalPnlPct => InitialBalance > 0 ? OosTotalPnl / InitialBalance * 100m : 0;
            public int OosTotalTrades { get; set; }
            public int OosWinCount { get; set; }
            public decimal OosWinRate => OosTotalTrades > 0 ? (decimal)OosWinCount / OosTotalTrades * 100m : 0;
            public decimal OosMaxDrawdown { get; set; }
            public decimal OosSharpeRatio { get; set; }
            public decimal OosProfitFactor { get; set; }

            // 안정성 지표
            public double AvgEfficiencyRatio { get; set; }     // 평균 OOS/IS 효율성 (>0.5 양호)
            public double StabilityScore { get; set; }         // 수익 윈도우 비율 (>0.6 양호)
            public double ParamStabilityScore { get; set; }    // 파라미터 일관성 (낮을수록 안정)

            // 최적 글로벌 파라미터 (가장 빈번하게 선택된 조합)
            public ParamSet RecommendedParams { get; set; } = null!;

            // 윈도우별 상세
            public List<WindowResult> Windows { get; set; } = new();
            public List<TradeRecord> AllOosTrades { get; set; } = new();

            // 연/월별 OOS 수익
            public Dictionary<int, decimal> ByYear { get; set; } = new();
            public Dictionary<string, decimal> ByMonth { get; set; } = new();
        }

        // ═══ 거래 기록 ═══
        public record TradeRecord(
            string Symbol, DateTime EntryTime, DateTime ExitTime,
            string Direction, decimal EntryPrice, decimal ExitPrice,
            decimal MarginUsed, decimal RealizedPnl, double EntryScore,
            string ExitReason);

        // ═══ 생성자 ═══
        public WalkForwardOptimizer(
            int isMonths = 12, int oosMonths = 4, int stepMonths = 4,
            int totalYears = 3, decimal initialBalance = 2500m,
            string[]? symbols = null)
        {
            _isMonths = isMonths;
            _oosMonths = oosMonths;
            _stepMonths = stepMonths;
            _totalYears = totalYears;
            _initialBalance = initialBalance;
            _symbols = symbols ?? new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        }

        // ═══ 메인 실행 ═══
        public async Task<WfoReport> RunAsync(Action<string>? onLog = null, CancellationToken ct = default)
        {
            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddYears(-_totalYears);

            onLog?.Invoke("╔══════════════════════════════════════════════════════════════════╗");
            onLog?.Invoke("║          워크포워드 최적화 (Walk-Forward Optimization)          ║");
            onLog?.Invoke("╠══════════════════════════════════════════════════════════════════╣");
            onLog?.Invoke($"║  데이터 기간 : {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} ({_totalYears}년)");
            onLog?.Invoke($"║  IS 기간     : {_isMonths}개월  |  OOS 기간: {_oosMonths}개월  |  스텝: {_stepMonths}개월");
            onLog?.Invoke($"║  심볼        : {string.Join(", ", _symbols)}");
            onLog?.Invoke($"║  초기 잔고   : ${_initialBalance:N0}  |  레버리지: {LEVERAGE}x");
            onLog?.Invoke("╚══════════════════════════════════════════════════════════════════╝");

            // Step 1: 데이터 수집
            onLog?.Invoke("\n[Step 1/4] 전체 데이터 수집 중...");
            using var client = new BinanceRestClient();
            var allKlines = new Dictionary<string, List<IBinanceKline>>();

            foreach (var sym in _symbols)
            {
                ct.ThrowIfCancellationRequested();
                onLog?.Invoke($"  [{sym}] 수집 중...");
                var kl = await FetchKlinesAsync(client, sym, startDate, endDate);
                allKlines[sym] = kl;
                onLog?.Invoke($"  [{sym}] {kl.Count}개 1시간봉 수집 완료");
            }

            // Step 2: 윈도우 구성
            var windows = BuildWindows(startDate, endDate);
            onLog?.Invoke($"\n[Step 2/4] {windows.Count}개 롤링 윈도우 구성 완료");
            for (int i = 0; i < windows.Count; i++)
            {
                var (isS, isE, oosS, oosE) = windows[i];
                onLog?.Invoke($"  Window {i + 1}: IS [{isS:yyyy-MM} ~ {isE:yyyy-MM}] → OOS [{oosS:yyyy-MM} ~ {oosE:yyyy-MM}]");
            }

            // Step 3: 파라미터 그리드 구성
            var grid = BuildParamGrid();
            onLog?.Invoke($"\n[Step 3/4] 파라미터 그리드: {grid.Count}개 조합");
            onLog?.Invoke($"  Threshold: [60, 65, 70, 75]");
            onLog?.Invoke($"  StopMult:  [1.0, 1.5, 2.0, 2.5]");
            onLog?.Invoke($"  Tp2Mult:   [3.5, 5.0, 6.5, 8.0]");
            onLog?.Invoke($"  BeMult:    [0.5, 0.8, 1.0]");
            onLog?.Invoke($"  TrailMult: [2.5, 3.5, 4.5]");
            onLog?.Invoke($"  GapMult:   [0.4, 0.6, 0.8]");

            // Step 4: 각 윈도우별 최적화 실행
            onLog?.Invoke($"\n[Step 4/4] 윈도우별 최적화 실행...");
            var windowResults = new List<WindowResult>();
            var allOosTrades = new List<TradeRecord>();

            for (int wi = 0; wi < windows.Count; wi++)
            {
                ct.ThrowIfCancellationRequested();
                var (isStart, isEnd, oosStart, oosEnd) = windows[wi];
                onLog?.Invoke($"\n  ━━━ Window {wi + 1}/{windows.Count} ━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                onLog?.Invoke($"  IS: {isStart:yyyy-MM-dd} ~ {isEnd:yyyy-MM-dd} | OOS: {oosStart:yyyy-MM-dd} ~ {oosEnd:yyyy-MM-dd}");

                // IS 데이터 추출
                var isKlines = ExtractKlines(allKlines, isStart, isEnd);
                var oosKlines = ExtractKlines(allKlines, oosStart, oosEnd);

                // IS 그리드 서치 (병렬)
                onLog?.Invoke($"  IS 그리드 서치: {grid.Count}개 조합 탐색 중...");
                var bestParams = await FindBestParamsAsync(grid, isKlines, ct);
                onLog?.Invoke($"  IS 최적 파라미터: Thr={bestParams.param.Threshold:F0} SL×{bestParams.param.StopMult:F1} TP2×{bestParams.param.Tp2Mult:F1} BE×{bestParams.param.BeMult:F1} Trail×{bestParams.param.TrailMult:F1} Gap×{bestParams.param.GapMult:F1}");
                onLog?.Invoke($"  IS 피트니스: {bestParams.fitness:F4}");

                // IS 성과 상세
                var isDetail = RunSimulation(isKlines, bestParams.param);

                // OOS 검증
                var oosDetail = RunSimulation(oosKlines, bestParams.param);
                double oosFitness = ComputeFitness(oosDetail.trades, _initialBalance / _symbols.Length * _symbols.Length);

                var wr = new WindowResult
                {
                    WindowIndex = wi + 1,
                    IsStart = isStart, IsEnd = isEnd,
                    OosStart = oosStart, OosEnd = oosEnd,
                    BestParams = bestParams.param,
                    IsFitness = bestParams.fitness,
                    OosFitness = oosFitness,
                    IsTrades = isDetail.trades.Count,
                    IsWinRate = isDetail.trades.Count > 0 ? isDetail.trades.Count(t => t.RealizedPnl > 0) * 100.0 / isDetail.trades.Count : 0,
                    IsPnl = isDetail.trades.Sum(t => t.RealizedPnl),
                    IsMdd = isDetail.mdd,
                    IsSharpe = isDetail.sharpe,
                    OosTrades = oosDetail.trades.Count,
                    OosWinRate = oosDetail.trades.Count > 0 ? oosDetail.trades.Count(t => t.RealizedPnl > 0) * 100.0 / oosDetail.trades.Count : 0,
                    OosPnl = oosDetail.trades.Sum(t => t.RealizedPnl),
                    OosMdd = oosDetail.mdd,
                    OosSharpe = oosDetail.sharpe,
                    OosTrades_Detail = oosDetail.trades
                };

                windowResults.Add(wr);
                allOosTrades.AddRange(oosDetail.trades);

                onLog?.Invoke($"  OOS 결과: {wr.OosTrades}건 | 승률 {wr.OosWinRate:F1}% | PnL ${wr.OosPnl:+#,##0.00;-#,##0.00} | MDD {wr.OosMdd:F1}%");
                onLog?.Invoke($"  효율성 비율 (OOS/IS): {wr.EfficiencyRatio:F3} {(wr.EfficiencyRatio >= 0.5 ? "(양호)" : wr.EfficiencyRatio >= 0.3 ? "(보통)" : "(과적합 주의)")}");
            }

            // 최종 리포트 생성
            var report = BuildFinalReport(windowResults, allOosTrades, startDate, endDate, onLog);

            onLog?.Invoke("\n" + FormatReport(report));

            // 파일 저장
            string savedPath = SaveReportToFile(report);
            onLog?.Invoke($"\n결과 파일 저장: {savedPath}");

            return report;
        }

        // ═══ 롤링 윈도우 구성 ═══
        private List<(DateTime isStart, DateTime isEnd, DateTime oosStart, DateTime oosEnd)> BuildWindows(
            DateTime dataStart, DateTime dataEnd)
        {
            var windows = new List<(DateTime, DateTime, DateTime, DateTime)>();
            var cursor = dataStart;

            while (true)
            {
                var isStart = cursor;
                var isEnd = isStart.AddMonths(_isMonths);
                var oosStart = isEnd;
                var oosEnd = oosStart.AddMonths(_oosMonths);

                if (oosEnd > dataEnd)
                {
                    // 마지막 윈도우: OOS 끝을 데이터 끝으로 조정
                    oosEnd = dataEnd;
                    if (oosEnd <= oosStart.AddDays(14)) break; // OOS가 2주 미만이면 스킵
                    windows.Add((isStart, isEnd, oosStart, oosEnd));
                    break;
                }

                windows.Add((isStart, isEnd, oosStart, oosEnd));
                cursor = cursor.AddMonths(_stepMonths);
            }

            return windows;
        }

        // ═══ 파라미터 그리드 구성 ═══
        private List<ParamSet> BuildParamGrid()
        {
            double[] thresholds = { 60.0, 65.0, 70.0, 75.0 };
            double[] stopMults  = { 1.0, 1.5, 2.0, 2.5 };
            double[] tp2Mults   = { 3.5, 5.0, 6.5, 8.0 };
            double[] beMults    = { 0.5, 0.8, 1.0 };
            double[] trailMults = { 2.5, 3.5, 4.5 };
            double[] gapMults   = { 0.4, 0.6, 0.8 };

            var grid = new List<ParamSet>();
            foreach (var thr in thresholds)
            foreach (var stp in stopMults)
            foreach (var tp2 in tp2Mults)
            foreach (var be in beMults)
            foreach (var trail in trailMults)
            foreach (var gap in gapMults)
            {
                // 유효성 검증: TP2 > Trail > TP1 > BE > SL 순서 보장
                double tp1 = tp2 / 2.0;
                if (trail < tp1 || be >= tp1 || gap >= stp) continue;
                grid.Add(new ParamSet(thr, stp, tp2, be, trail, gap));
            }

            return grid;
        }

        // ═══ IS 그리드 서치 (병렬) ═══
        private async Task<(ParamSet param, double fitness)> FindBestParamsAsync(
            List<ParamSet> grid,
            Dictionary<string, List<IBinanceKline>> klines,
            CancellationToken ct)
        {
            var results = new ConcurrentBag<(ParamSet param, double fitness)>();

            await Task.Run(() =>
            {
                Parallel.ForEach(grid, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                },
                paramSet =>
                {
                    var sim = RunSimulation(klines, paramSet);
                    double fit = ComputeFitness(sim.trades, _initialBalance);
                    results.Add((paramSet, fit));
                });
            }, ct);

            return results.OrderByDescending(r => r.fitness).First();
        }

        // ═══ 시뮬레이션 실행 ═══
        private (List<TradeRecord> trades, decimal mdd, decimal sharpe) RunSimulation(
            Dictionary<string, List<IBinanceKline>> klinesBySymbol, ParamSet p)
        {
            var allTrades = new List<TradeRecord>();

            foreach (var (symbol, klines) in klinesBySymbol)
            {
                if (klines.Count < EMA_TREND + 10) continue;
                var trades = SimulateSymbol(symbol, klines, _initialBalance / _symbols.Length, p);
                allTrades.AddRange(trades);
            }

            // MDD 계산
            decimal balance = _initialBalance;
            decimal peak = balance;
            decimal maxDD = 0m;
            foreach (var t in allTrades.OrderBy(t => t.ExitTime))
            {
                balance += t.RealizedPnl;
                peak = Math.Max(peak, balance);
                if (peak > 0) maxDD = Math.Max(maxDD, (peak - balance) / peak * 100m);
            }

            // Sharpe 계산 (일별)
            decimal sharpe = 0m;
            var byDay = allTrades.GroupBy(t => t.ExitTime.Date)
                .ToDictionary(g => g.Key, g => (double)g.Sum(t => t.RealizedPnl) / (double)_initialBalance);
            if (byDay.Count > 1)
            {
                double avg = byDay.Values.Average();
                double std = Math.Sqrt(byDay.Values.Average(r => (r - avg) * (r - avg)));
                if (std > 0) sharpe = (decimal)(avg / std * Math.Sqrt(365.0));
            }

            return (allTrades, maxDD, sharpe);
        }

        // ═══ 심볼별 시뮬레이션 (ThreeYearBacktestRunner.SimulateSymbol과 동일 로직) ═══
        private List<TradeRecord> SimulateSymbol(
            string symbol, List<IBinanceKline> klines, decimal allocatedBalance, ParamSet p)
        {
            var trades = new List<TradeRecord>();
            decimal balance = allocatedBalance;
            int warmup = Math.Max(EMA_TREND, MACD_SLOW + MACD_SIGNAL) + 5;

            bool inPosition = false;
            string direction = "";
            decimal entryPrice = 0m, positionQty = 0m, remainQty = 0m, marginUsed = 0m;
            decimal slPrice = 0m, tp1Price = 0m, tp2Price = 0m;
            decimal trailStart = 0m, trailStop = 0m, trailGapAmt = 0m;
            decimal highestPrice = 0m, lowestPrice = 0m;
            bool beActivated = false, tp1Done = false, trailActive = false;
            double entryScore = 0;
            DateTime entryTime = DateTime.MinValue;

            for (int i = warmup; i < klines.Count; i++)
            {
                var candle = klines[i];
                decimal candleHigh  = (decimal)candle.HighPrice;
                decimal candleLow   = (decimal)candle.LowPrice;
                decimal candleClose = (decimal)candle.ClosePrice;

                if (inPosition)
                {
                    if (direction == "LONG") highestPrice = Math.Max(highestPrice, candleHigh);
                    else lowestPrice = Math.Min(lowestPrice, candleLow);

                    // 트레일링 스탑
                    if (!trailActive)
                    {
                        bool trailTrigger = direction == "LONG"
                            ? highestPrice >= trailStart
                            : lowestPrice <= trailStart;
                        if (trailTrigger) trailActive = true;
                    }
                    if (trailActive)
                    {
                        if (direction == "LONG")
                        {
                            decimal newTrail = highestPrice - trailGapAmt;
                            trailStop = Math.Max(trailStop, newTrail);
                        }
                        else
                        {
                            decimal newTrail = lowestPrice + trailGapAmt;
                            trailStop = trailStop == 0m ? newTrail : Math.Min(trailStop, newTrail);
                        }
                    }

                    // 본절 활성화
                    if (!beActivated)
                    {
                        decimal beDistance = trailGapAmt / (decimal)p.GapMult * (decimal)p.BeMult;
                        bool beTrigger = direction == "LONG"
                            ? candleClose >= entryPrice + beDistance
                            : candleClose <= entryPrice - beDistance;
                        if (beTrigger) { beActivated = true; slPrice = entryPrice; }
                    }

                    // TP1 (50% 청산)
                    bool tp1Triggered = !tp1Done &&
                        (direction == "LONG" ? candleHigh >= tp1Price : candleLow <= tp1Price);
                    if (tp1Triggered)
                    {
                        decimal halfQty = positionQty / 2m;
                        decimal rawPnl = direction == "LONG"
                            ? (tp1Price - entryPrice) * halfQty
                            : (entryPrice - tp1Price) * halfQty;
                        decimal fee = tp1Price * halfQty * FEE_RATE;
                        balance += rawPnl - fee;
                        remainQty = positionQty / 2m;
                        tp1Done = true;
                        trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                            entryPrice, tp1Price, marginUsed / 2m, rawPnl - fee, entryScore, "TP1"));
                    }

                    // TP2
                    bool tp2Triggered = direction == "LONG" ? candleHigh >= tp2Price : candleLow <= tp2Price;
                    if (tp2Triggered)
                    {
                        decimal qty = tp1Done ? remainQty : positionQty;
                        decimal rawPnl = direction == "LONG"
                            ? (tp2Price - entryPrice) * qty
                            : (entryPrice - tp2Price) * qty;
                        decimal fee = tp2Price * qty * FEE_RATE;
                        balance += rawPnl - fee;
                        trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                            entryPrice, tp2Price, tp1Done ? marginUsed / 2m : marginUsed, rawPnl - fee, entryScore, "TP2"));
                        inPosition = false; tp1Done = false; continue;
                    }

                    // 트레일링 스탑 청산
                    if (trailActive && trailStop > 0m)
                    {
                        bool trailHit = direction == "LONG" ? candleLow <= trailStop : candleHigh >= trailStop;
                        if (trailHit)
                        {
                            decimal qty = tp1Done ? remainQty : positionQty;
                            decimal rawPnl = direction == "LONG"
                                ? (trailStop - entryPrice) * qty
                                : (entryPrice - trailStop) * qty;
                            decimal fee = trailStop * qty * FEE_RATE;
                            balance += rawPnl - fee;
                            trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                                entryPrice, trailStop, tp1Done ? marginUsed / 2m : marginUsed, rawPnl - fee, entryScore, "TRAIL"));
                            inPosition = false; tp1Done = false; continue;
                        }
                    }

                    // SL/BE 청산
                    bool slHit = direction == "LONG" ? candleLow <= slPrice : candleHigh >= slPrice;
                    if (slHit)
                    {
                        decimal qty = tp1Done ? remainQty : positionQty;
                        decimal rawPnl = direction == "LONG"
                            ? (slPrice - entryPrice) * qty
                            : (entryPrice - slPrice) * qty;
                        decimal fee = slPrice * qty * FEE_RATE;
                        balance += rawPnl - fee;
                        trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                            entryPrice, slPrice, tp1Done ? marginUsed / 2m : marginUsed, rawPnl - fee, entryScore, beActivated ? "BE" : "SL"));
                        inPosition = false; tp1Done = false; continue;
                    }

                    continue;
                }

                // 신규 진입
                if (balance < 50m) continue;

                var window = klines.GetRange(Math.Max(0, i - 220), Math.Min(220, i + 1));
                var signal = EvaluateEntry(window, symbol, p.Threshold);
                if (signal == null || signal.Score < p.Threshold) continue;

                decimal eprice = candleClose;
                double rawAtr = signal.AtrValue > 0 ? signal.AtrValue : (double)eprice * 0.015;
                double atrClamp = Math.Clamp(rawAtr, (double)eprice * 0.010, (double)eprice * 0.040);
                decimal atrD = (decimal)atrClamp;

                decimal dynSl = atrD * (decimal)p.StopMult;
                decimal dynTp1 = atrD * (decimal)(p.Tp2Mult / 2.0);
                decimal dynTp2 = atrD * (decimal)p.Tp2Mult;
                decimal dynTrail = atrD * (decimal)p.TrailMult;
                decimal dynGap = atrD * (decimal)p.GapMult;

                if (dynSl <= 0m || dynTp2 / dynSl < 1.5m) continue;

                decimal margin = Math.Min(balance * MARGIN_PERCENT, balance * 0.95m);
                decimal notional = margin * LEVERAGE;
                decimal qty2 = notional / eprice;
                balance -= notional * FEE_RATE;

                direction = signal.Direction;
                entryPrice = eprice;
                positionQty = qty2; remainQty = qty2;
                marginUsed = margin;
                entryScore = signal.Score;
                entryTime = candle.OpenTime;
                beActivated = false; tp1Done = false; trailActive = false;
                trailStop = 0m; trailGapAmt = dynGap;

                if (direction == "LONG")
                {
                    slPrice = eprice - dynSl; tp1Price = eprice + dynTp1;
                    tp2Price = eprice + dynTp2; trailStart = eprice + dynTrail;
                    highestPrice = eprice;
                }
                else
                {
                    slPrice = eprice + dynSl; tp1Price = eprice - dynTp1;
                    tp2Price = eprice - dynTp2; trailStart = eprice - dynTrail;
                    lowestPrice = eprice;
                }
                inPosition = true;
            }

            // 기간 종료 시 미청산 포지션 강제 청산
            if (inPosition && klines.Count > 0)
            {
                var last = klines[^1];
                decimal exitPx = (decimal)last.ClosePrice;
                decimal qty = tp1Done ? remainQty : positionQty;
                decimal rawPnl = direction == "LONG"
                    ? (exitPx - entryPrice) * qty
                    : (entryPrice - exitPx) * qty;
                decimal fee = exitPx * qty * FEE_RATE;
                trades.Add(new TradeRecord(symbol, entryTime, last.OpenTime, direction,
                    entryPrice, exitPx, tp1Done ? marginUsed / 2m : marginUsed, rawPnl - fee, entryScore, "END"));
            }

            return trades;
        }

        // ═══ 진입 신호 평가 (기존 ThreeYearBacktestRunner와 동일) ═══
        private record EntrySignal(double Score, string Direction, double AtrValue);

        private EntrySignal? EvaluateEntry(List<IBinanceKline> window, string symbol, double threshold)
        {
            if (window.Count < EMA_LONG + 5) return null;

            var closes  = window.Select(k => (double)k.ClosePrice).ToList();
            var highs   = window.Select(k => (double)k.HighPrice).ToList();
            var lows    = window.Select(k => (double)k.LowPrice).ToList();
            var opens   = window.Select(k => (double)k.OpenPrice).ToList();
            var volumes = window.Select(k => (double)k.Volume).ToList();

            int n = closes.Count;
            double price = closes[n - 1];

            double rsi     = ComputeRsi(closes, RSI_PERIOD);
            double rsiPrev = ComputeRsi(closes.Take(n - 1).ToList(), RSI_PERIOD);

            double emaFast = ComputeEma(closes, MACD_FAST);
            double emaSlow = ComputeEma(closes, MACD_SLOW);
            double macdNow = emaFast - emaSlow;
            double emaFastPrev = ComputeEma(closes.Take(n - 1).ToList(), MACD_FAST);
            double emaSlowPrev = ComputeEma(closes.Take(n - 1).ToList(), MACD_SLOW);
            double macdPrev = emaFastPrev - emaSlowPrev;
            bool macdRising = macdNow > macdPrev;
            bool macdPos = macdNow > 0;

            var bbSlice = closes.TakeLast(BB_PERIOD).ToList();
            double bbMid = bbSlice.Average();
            double bbStd = Math.Sqrt(bbSlice.Average(c => (c - bbMid) * (c - bbMid)));
            double bbUpper = bbMid + BB_MULT * bbStd;
            double bbLower = bbMid - BB_MULT * bbStd;
            double bbWidth = bbUpper - bbLower;
            double bbPos = bbWidth > 0 ? (price - bbLower) / bbWidth : 0.5;

            double ema20 = ComputeEma(closes, EMA_SHORT);
            double ema50 = ComputeEma(closes, EMA_LONG);
            double ema200 = closes.Count >= EMA_TREND ? ComputeEma(closes, EMA_TREND) : 0;
            double ema20Prev = ComputeEma(closes.Take(n - 1).ToList(), EMA_SHORT);

            double volAvg = volumes.TakeLast(VOL_MA_PERIOD).Average();
            double volCur = volumes[n - 1];
            double volRatio = volAvg > 0 ? volCur / volAvg : 1.0;

            double atr = ComputeAtr(closes, highs, lows, ATR_PERIOD);
            double atrPct = price > 0 ? atr / price * 100.0 : 0;
            if (atrPct > 5.0) return null;
            if (volRatio < 0.6) return null;

            double ema20Dist = ema20 > 0 ? Math.Abs(price - ema20) / ema20 : 1;
            bool uptrend200  = ema200 > 0 && price > ema200;
            bool uptrend50   = price > ema50;
            bool downtrend50 = price < ema50;
            bool uptrend20   = price > ema20;

            // 캔들 패턴
            double lastOpen  = opens[n - 1], lastClose = closes[n - 1];
            double lastHigh  = highs[n - 1], lastLow = lows[n - 1];
            double totalRange = lastHigh - lastLow;
            double body       = Math.Abs(lastClose - lastOpen);
            double upperShadow = lastHigh - Math.Max(lastOpen, lastClose);
            double lowerShadow = Math.Min(lastOpen, lastClose) - lastLow;
            double upperShadowRatio = totalRange > 0 ? upperShadow / totalRange : 0;
            double lowerShadowRatio = totalRange > 0 ? lowerShadow / totalRange : 0;
            double bodyRatio        = totalRange > 0 ? body / totalRange : 0;

            bool isHammer = lowerShadowRatio >= 0.55 && bodyRatio <= 0.30 && bbPos <= 0.40;
            bool isBullEngulfing = n >= 3 && lastClose > lastOpen &&
                lastClose > highs[n - 2] && lastOpen <= lows[n - 2] && closes[n - 2] < opens[n - 2];
            bool isShootingStar = upperShadowRatio >= 0.55 && bodyRatio <= 0.30 && bbPos >= 0.60;
            bool isBearEngulfing = n >= 3 && lastClose < lastOpen &&
                lastClose < lows[n - 2] && lastOpen >= highs[n - 2] && closes[n - 2] > opens[n - 2];
            bool ema20Pullback = ema20Dist < 0.008;

            // LONG 점수 (수렴 기반)
            double longScore = 0;
            int longConfirms = 0;

            double rsiL;
            if (rsi <= 30)      { rsiL = 25; longConfirms++; }
            else if (rsi <= 40) { rsiL = 22; longConfirms++; }
            else if (rsi <= 50) { rsiL = 18; longConfirms++; }
            else if (rsi <= 60) { rsiL = 12; }
            else if (rsi <= 70) { rsiL = 6;  }
            else                { rsiL = 0;  }
            if (rsi > rsiPrev + 1.5) rsiL = Math.Min(25, rsiL + 3);
            longScore += rsiL;

            double momL;
            if (macdPos && macdRising)      { momL = 25; longConfirms++; }
            else if (macdPos)               { momL = 18; longConfirms++; }
            else if (macdRising)            { momL = 14; longConfirms++; }
            else if (ema20 > ema20Prev)     { momL = 8;  }
            else                            { momL = 0;  }
            if (ema20 > ema20Prev) momL = Math.Min(25, momL + 3);
            longScore += momL;

            double priceL;
            if (bbPos <= 0.15)      { priceL = 25; longConfirms++; }
            else if (bbPos <= 0.30) { priceL = 22; longConfirms++; }
            else if (bbPos <= 0.45) { priceL = 16; }
            else if (bbPos <= 0.60) { priceL = 10; }
            else if (bbPos <= 0.75) { priceL = 5;  }
            else                    { priceL = 0;  }
            if (ema20Pullback && uptrend20) { priceL = Math.Min(25, priceL + 5); longConfirms++; }
            if (uptrend50 && Math.Abs(price - ema50) / ema50 < 0.01) priceL = Math.Min(25, priceL + 3);
            longScore += priceL;

            double volL;
            if (volRatio >= 2.5)      { volL = 25; longConfirms++; }
            else if (volRatio >= 1.8) { volL = 20; longConfirms++; }
            else if (volRatio >= 1.2) { volL = 15; }
            else if (volRatio >= 0.8) { volL = 10; }
            else                      { volL = 5;  }
            longScore += volL;

            if (longConfirms >= 4) longScore += 20;
            else if (longConfirms >= 3) longScore += 12;
            if (isHammer) longScore += 12;
            if (isBullEngulfing) longScore += 15;

            // SHORT 점수 (수렴 기반)
            double shortScore = 0;
            int shortConfirms = 0;

            double rsiS;
            if (rsi >= 70)      { rsiS = 25; shortConfirms++; }
            else if (rsi >= 60) { rsiS = 22; shortConfirms++; }
            else if (rsi >= 50) { rsiS = 18; shortConfirms++; }
            else if (rsi >= 40) { rsiS = 12; }
            else if (rsi >= 30) { rsiS = 6;  }
            else                { rsiS = 0;  }
            if (rsi < rsiPrev - 1.5) rsiS = Math.Min(25, rsiS + 3);
            shortScore += rsiS;

            double momS;
            if (!macdPos && !macdRising)     { momS = 25; shortConfirms++; }
            else if (!macdPos)               { momS = 18; shortConfirms++; }
            else if (!macdRising)            { momS = 14; shortConfirms++; }
            else if (ema20 < ema20Prev)      { momS = 8;  }
            else                             { momS = 0;  }
            if (ema20 < ema20Prev) momS = Math.Min(25, momS + 3);
            shortScore += momS;

            double priceS;
            if (bbPos >= 0.85)      { priceS = 25; shortConfirms++; }
            else if (bbPos >= 0.70) { priceS = 22; shortConfirms++; }
            else if (bbPos >= 0.55) { priceS = 16; }
            else if (bbPos >= 0.40) { priceS = 10; }
            else if (bbPos >= 0.25) { priceS = 5;  }
            else                    { priceS = 0;  }
            if (ema20Pullback && !uptrend20) { priceS = Math.Min(25, priceS + 5); shortConfirms++; }
            if (downtrend50 && Math.Abs(price - ema50) / ema50 < 0.01) priceS = Math.Min(25, priceS + 3);
            shortScore += priceS;

            shortScore += volL;
            if (volRatio >= 1.8) shortConfirms++;

            if (shortConfirms >= 4) shortScore += 20;
            else if (shortConfirms >= 3) shortScore += 12;
            if (isShootingStar) shortScore += 12;
            if (isBearEngulfing) shortScore += 15;

            // 소프트 감점 (하드 차단 제거)
            if (ema200 > 0)
            {
                if (!uptrend200) longScore  -= 12;
                if (uptrend200)  shortScore -= 12;
            }
            if (downtrend50) longScore  -= 8;
            if (uptrend50)   shortScore -= 8;
            if (rsi > 75) longScore  -= 10;
            if (rsi < 25) shortScore -= 10;
            if (!macdPos && !macdRising) longScore  -= 8;
            if (macdPos && macdRising)   shortScore -= 8;
            if (!uptrend20) longScore  -= 4;
            if (uptrend20)  shortScore -= 4;

            longScore  = Math.Max(0, longScore);
            shortScore = Math.Max(0, shortScore);

            if (longScore >= shortScore && longScore >= threshold)
                return new EntrySignal(longScore, "LONG", atr);
            if (shortScore > longScore && shortScore >= threshold)
                return new EntrySignal(shortScore, "SHORT", atr);

            return null;
        }

        // ═══ 피트니스 함수 (개선): Sharpe보정 × PnL × ProfitFactor × 거래수 × MDD페널티 ═══
        private double ComputeFitness(List<TradeRecord> trades, decimal initialBalance)
        {
            if (trades.Count < 5) return -99.0;

            int wins = trades.Count(t => t.RealizedPnl > 0);
            double winRate = (double)wins / trades.Count;
            decimal totalPnl = trades.Sum(t => t.RealizedPnl);
            double pnlPct = (double)(totalPnl / initialBalance);

            if (pnlPct < -0.7) return -99.0;

            // Profit Factor (총이익 / 총손실)
            decimal grossProfit = trades.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
            decimal grossLoss = Math.Abs(trades.Where(t => t.RealizedPnl <= 0).Sum(t => t.RealizedPnl));
            double profitFactor = grossLoss > 0 ? Math.Min(5.0, (double)(grossProfit / grossLoss)) : 3.0;

            // MDD 계산
            decimal balance = initialBalance;
            decimal peak = balance;
            decimal maxDD = 0m;
            foreach (var t in trades.OrderBy(t => t.ExitTime))
            {
                balance += t.RealizedPnl;
                peak = Math.Max(peak, balance);
                if (peak > 0) maxDD = Math.Max(maxDD, (peak - balance) / peak);
            }
            double mddPenalty = 1.0 - Math.Min(0.8, (double)maxDD); // MDD 80% 이상이면 최소 보상

            // Sharpe (일별)
            var byDay = trades.GroupBy(t => t.ExitTime.Date)
                .Select(g => (double)g.Sum(t => t.RealizedPnl) / (double)initialBalance).ToList();
            double sharpeBonus = 1.0;
            if (byDay.Count > 1)
            {
                double avg = byDay.Average();
                double std = Math.Sqrt(byDay.Average(r => (r - avg) * (r - avg)));
                if (std > 0)
                {
                    double sharpe = avg / std * Math.Sqrt(365.0);
                    sharpeBonus = 1.0 + Math.Clamp(sharpe * 0.1, -0.3, 0.5);
                }
            }

            double tradeFactor = Math.Min(1.0, trades.Count / 20.0);

            return winRate * (1.0 + pnlPct) * profitFactor * tradeFactor * mddPenalty * sharpeBonus;
        }

        // ═══ Kline 기간 추출 ═══
        private Dictionary<string, List<IBinanceKline>> ExtractKlines(
            Dictionary<string, List<IBinanceKline>> allKlines, DateTime start, DateTime end)
        {
            var result = new Dictionary<string, List<IBinanceKline>>();
            foreach (var (sym, klines) in allKlines)
            {
                result[sym] = klines.Where(k => k.OpenTime >= start && k.OpenTime < end).ToList();
            }
            return result;
        }

        // ═══ 최종 리포트 구성 ═══
        private WfoReport BuildFinalReport(
            List<WindowResult> windows, List<TradeRecord> allOosTrades,
            DateTime startDate, DateTime endDate, Action<string>? onLog)
        {
            // OOS 합산 잔고 추적
            decimal balance = _initialBalance;
            decimal peak = _initialBalance;
            decimal maxDD = 0m;
            foreach (var t in allOosTrades.OrderBy(t => t.ExitTime))
            {
                balance += t.RealizedPnl;
                peak = Math.Max(peak, balance);
                if (peak > 0) maxDD = Math.Max(maxDD, (peak - balance) / peak * 100m);
            }

            // OOS Sharpe
            var byDay = allOosTrades.GroupBy(t => t.ExitTime.Date)
                .ToDictionary(g => g.Key, g => (double)g.Sum(t => t.RealizedPnl) / (double)_initialBalance);
            decimal sharpe = 0m;
            if (byDay.Count > 1)
            {
                double avg = byDay.Values.Average();
                double std = Math.Sqrt(byDay.Values.Average(r => (r - avg) * (r - avg)));
                if (std > 0) sharpe = (decimal)(avg / std * Math.Sqrt(365.0));
            }

            // Profit Factor
            decimal grossProfit = allOosTrades.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
            decimal grossLoss = Math.Abs(allOosTrades.Where(t => t.RealizedPnl <= 0).Sum(t => t.RealizedPnl));
            decimal pf = grossLoss > 0 ? grossProfit / grossLoss : 0m;

            // 안정성 지표
            double avgEfficiency = windows.Count > 0 ? windows.Average(w => w.EfficiencyRatio) : 0;
            double stabilityScore = windows.Count > 0 ? (double)windows.Count(w => w.OosPnl > 0) / windows.Count : 0;

            // 파라미터 안정성 (표준편차 기반)
            double paramStability = 0;
            if (windows.Count > 1)
            {
                var thresholds = windows.Select(w => w.BestParams.Threshold).ToList();
                var stops = windows.Select(w => w.BestParams.StopMult).ToList();
                var tp2s = windows.Select(w => w.BestParams.Tp2Mult).ToList();
                double thrStd = StdDev(thresholds);
                double stpStd = StdDev(stops);
                double tp2Std = StdDev(tp2s);
                paramStability = (thrStd / 10.0 + stpStd + tp2Std) / 3.0; // 정규화된 복합 지표
            }

            // 가장 빈번한 파라미터 조합 (추천)
            var recommended = windows
                .GroupBy(w => $"{w.BestParams.Threshold:F0}_{w.BestParams.StopMult:F1}_{w.BestParams.Tp2Mult:F1}")
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Average(w => w.OosFitness))
                .First().First().BestParams;

            // 연/월별
            var byYear = allOosTrades.GroupBy(t => t.ExitTime.Year)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));
            var byMonth = allOosTrades.GroupBy(t => t.ExitTime.ToString("yyyy-MM"))
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));

            int oosWins = allOosTrades.Count(t => t.RealizedPnl > 0);

            return new WfoReport
            {
                DataStart = startDate,
                DataEnd = endDate,
                TotalWindows = windows.Count,
                InitialBalance = _initialBalance,
                OosFinalBalance = balance,
                OosTotalTrades = allOosTrades.Count,
                OosWinCount = oosWins,
                OosMaxDrawdown = maxDD,
                OosSharpeRatio = sharpe,
                OosProfitFactor = pf,
                AvgEfficiencyRatio = avgEfficiency,
                StabilityScore = stabilityScore,
                ParamStabilityScore = paramStability,
                RecommendedParams = recommended,
                Windows = windows,
                AllOosTrades = allOosTrades,
                ByYear = byYear,
                ByMonth = byMonth
            };
        }

        // ═══ 리포트 포맷 ═══
        public string FormatReport(WfoReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║          워크포워드 최적화 (WFO) 최종 결과                         ║");
            sb.AppendLine($"║  기간: {r.DataStart:yyyy-MM-dd} ~ {r.DataEnd:yyyy-MM-dd}  |  윈도우: {r.TotalWindows}개");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [OOS 합산 성과] (실전 기대치)");
            sb.AppendLine($"║  초기 잔고   : ${r.InitialBalance,10:N2}");
            sb.AppendLine($"║  최종 잔고   : ${r.OosFinalBalance,10:N2}");
            sb.AppendLine($"║  총 수익     : ${r.OosTotalPnl,+10:N2}  ({r.OosTotalPnlPct:+0.00;-0.00}%)");
            sb.AppendLine($"║  총 거래수   : {r.OosTotalTrades,6}건   승률: {r.OosWinRate:F1}% ({r.OosWinCount}승 {r.OosTotalTrades - r.OosWinCount}패)");
            sb.AppendLine($"║  최대 낙폭   : {r.OosMaxDrawdown:F2}%");
            sb.AppendLine($"║  샤프 비율   : {r.OosSharpeRatio:F3}");
            sb.AppendLine($"║  프로핏 팩터 : {r.OosProfitFactor:F3}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [안정성 지표]");
            sb.AppendLine($"║  효율성 비율 (OOS/IS 평균): {r.AvgEfficiencyRatio:F3}  {RateEfficiency(r.AvgEfficiencyRatio)}");
            sb.AppendLine($"║  수익 윈도우 비율          : {r.StabilityScore:P0}  {RateStability(r.StabilityScore)}");
            sb.AppendLine($"║  파라미터 안정성           : {r.ParamStabilityScore:F3}  {RateParamStability(r.ParamStabilityScore)}");

            // 과적합 진단
            string overfit = DiagnoseOverfit(r);
            sb.AppendLine($"║  과적합 진단               : {overfit}");

            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [추천 파라미터] (가장 빈번하게 최적 선택된 조합)");
            sb.AppendLine($"║  진입 임계값 : {r.RecommendedParams.Threshold:F0}점");
            sb.AppendLine($"║  SL  배수    : ATR × {r.RecommendedParams.StopMult:F1}");
            sb.AppendLine($"║  TP1 배수    : ATR × {r.RecommendedParams.Tp2Mult / 2.0:F1}");
            sb.AppendLine($"║  TP2 배수    : ATR × {r.RecommendedParams.Tp2Mult:F1}");
            sb.AppendLine($"║  BE  배수    : ATR × {r.RecommendedParams.BeMult:F1}");
            sb.AppendLine($"║  Trail 배수  : ATR × {r.RecommendedParams.TrailMult:F1}");
            sb.AppendLine($"║  Gap  배수   : ATR × {r.RecommendedParams.GapMult:F1}");

            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [윈도우별 상세]");
            sb.AppendLine("║  # │  IS 기간           │ OOS 기간          │ OOS건수│ OOS승률│  OOS PnL  │효율비│파라미터");
            sb.AppendLine("║  ──┼────────────────────┼───────────────────┼────────┼────────┼───────────┼──────┼──────────");
            foreach (var w in r.Windows)
            {
                sb.AppendLine($"║  {w.WindowIndex} │ {w.IsStart:yy-MM}~{w.IsEnd:yy-MM} ({_isMonths}mo) │ {w.OosStart:yy-MM}~{w.OosEnd:yy-MM} ({_oosMonths}mo) │ {w.OosTrades,6} │ {w.OosWinRate,5:F1}% │ ${w.OosPnl,+8:N0} │ {w.EfficiencyRatio:F2} │ T{w.BestParams.Threshold:F0} S{w.BestParams.StopMult:F1} P{w.BestParams.Tp2Mult:F1}");
            }

            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
            // 연별
            sb.AppendLine("║  [OOS 연별 수익]");
            foreach (var kv in r.ByYear.OrderBy(k => k.Key))
            {
                decimal pct = r.InitialBalance > 0 ? kv.Value / r.InitialBalance * 100m : 0;
                sb.AppendLine($"║   {kv.Key}년  ${kv.Value,+9:N2} ({pct:+0.0;-0.0}%)");
            }

            // 월별
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [OOS 월별 수익]");
            foreach (var kv in r.ByMonth.OrderBy(k => k.Key))
            {
                string bar = kv.Value >= 0
                    ? new string('#', Math.Min(20, (int)(kv.Value / 30)))
                    : new string('-', Math.Min(20, (int)(-kv.Value / 30)));
                sb.AppendLine($"║   {kv.Key}  ${kv.Value,+8:N0}  [{bar,-20}]");
            }

            // 추정 수익
            int totalDays = (r.DataEnd - r.DataStart).Days;
            // OOS 일수만 계산 (IS 기간 제외)
            int oosDays = r.Windows.Sum(w => (w.OosEnd - w.OosStart).Days);
            if (oosDays > 0)
            {
                decimal dailyAvg = r.OosTotalPnl / oosDays;
                sb.AppendLine("╠══════════════════════════════════════════════════════════════════════╣");
                sb.AppendLine("║  [OOS 기반 추정 수익] (실전 기대치)");
                sb.AppendLine($"║   OOS 총 일수 : {oosDays}일");
                sb.AppendLine($"║   일 평균     : ${dailyAvg:+N2;-N2}  /일");
                sb.AppendLine($"║   월 평균     : ${dailyAvg * 30:+N2;-N2}  /월");
                sb.AppendLine($"║   연 평균     : ${dailyAvg * 365:+N2;-N2}  /년");
            }

            sb.AppendLine("╚══════════════════════════════════════════════════════════════════════╝");
            return sb.ToString();
        }

        // ═══ 파일 저장 ═══
        public string SaveReportToFile(WfoReport report)
        {
            string fileName = $"wfo_result_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            var sb = new StringBuilder();
            sb.AppendLine(FormatReport(report));
            sb.AppendLine();
            sb.AppendLine("═══ OOS 전체 거래 상세 ═══");
            sb.AppendLine($"{"시간",-20} {"심볼",-10} {"방향",-6} {"진입가",-12} {"청산가",-12} {"수익",-12} {"이유",-8} {"점수",-6}");
            sb.AppendLine(new string('-', 90));
            foreach (var t in report.AllOosTrades.OrderBy(t => t.ExitTime))
            {
                string sign = t.RealizedPnl >= 0 ? "+" : "";
                sb.AppendLine($"{t.ExitTime:yyyy-MM-dd HH:mm,-20} {t.Symbol,-10} {t.Direction,-6} {t.EntryPrice,-12:F4} {t.ExitPrice,-12:F4} {sign}${t.RealizedPnl,-10:N2} {t.ExitReason,-8} {t.EntryScore:F0}");
            }

            // 윈도우별 IS 최적 파라미터 히스토리
            sb.AppendLine();
            sb.AppendLine("═══ 윈도우별 최적 파라미터 히스토리 ═══");
            sb.AppendLine($"{"Window",-8} {"Threshold",-10} {"StopMult",-10} {"Tp2Mult",-10} {"BeMult",-8} {"TrailMult",-10} {"GapMult",-8} {"IS Fit",-10} {"OOS Fit",-10} {"효율비",-8}");
            sb.AppendLine(new string('-', 100));
            foreach (var w in report.Windows)
            {
                sb.AppendLine($"{"W" + w.WindowIndex,-8} {w.BestParams.Threshold,-10:F0} {w.BestParams.StopMult,-10:F1} {w.BestParams.Tp2Mult,-10:F1} {w.BestParams.BeMult,-8:F1} {w.BestParams.TrailMult,-10:F1} {w.BestParams.GapMult,-8:F1} {w.IsFitness,-10:F4} {w.OosFitness,-10:F4} {w.EfficiencyRatio,-8:F3}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        // ═══ 진단 헬퍼 ═══
        private static string RateEfficiency(double r) =>
            r >= 0.7 ? "(우수 - 과적합 최소)" :
            r >= 0.5 ? "(양호)" :
            r >= 0.3 ? "(보통 - 주의 필요)" : "(불량 - 과적합 가능성 높음)";

        private static string RateStability(double s) =>
            s >= 0.8 ? "(우수)" :
            s >= 0.6 ? "(양호)" :
            s >= 0.4 ? "(보통)" : "(불량)";

        private static string RateParamStability(double p) =>
            p <= 0.3 ? "(안정 - 일관된 파라미터)" :
            p <= 0.5 ? "(보통)" : "(불안정 - 시장 레짐 변동 큼)";

        private static string DiagnoseOverfit(WfoReport r)
        {
            int score = 0;
            if (r.AvgEfficiencyRatio >= 0.5) score++;
            if (r.StabilityScore >= 0.6) score++;
            if (r.ParamStabilityScore <= 0.4) score++;
            if (r.OosSharpeRatio > 0) score++;
            if (r.OosTotalPnl > 0) score++;

            return score switch
            {
                5 => "과적합 위험 낮음 (5/5)",
                4 => "양호 (4/5)",
                3 => "보통 (3/5) - 실전 적용 시 주의",
                2 => "과적합 의심 (2/5) - 파라미터 축소 권장",
                _ => "과적합 위험 높음 (≤1/5) - 전략 재설계 필요"
            };
        }

        private static double StdDev(List<double> values)
        {
            if (values.Count < 2) return 0;
            double avg = values.Average();
            return Math.Sqrt(values.Average(v => (v - avg) * (v - avg)));
        }

        // ═══ Binance 데이터 수집 ═══
        private static async Task<List<IBinanceKline>> FetchKlinesAsync(
            BinanceRestClient client, string symbol, DateTime startUtc, DateTime endUtc)
        {
            var result = new List<IBinanceKline>();
            var cursor = startUtc;
            int chunk = 1000;

            while (cursor < endUtc)
            {
                var to = cursor.AddHours(chunk);
                if (to > endUtc) to = endUtc;

                var resp = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.OneHour,
                    startTime: cursor, endTime: to, limit: chunk);

                if (!resp.Success || resp.Data == null || !resp.Data.Any()) break;

                result.AddRange(resp.Data);
                cursor = resp.Data.Last().CloseTime.AddMilliseconds(1);
                await Task.Delay(120);
            }

            return result.OrderBy(k => k.OpenTime).ToList();
        }

        // ═══ 지표 계산 헬퍼 ═══
        private static double ComputeRsi(List<double> closes, int period)
        {
            if (closes.Count < period + 1) return 50;
            double gain = 0, loss = 0;
            for (int i = closes.Count - period; i < closes.Count; i++)
            {
                double diff = closes[i] - closes[i - 1];
                if (diff > 0) gain += diff; else loss -= diff;
            }
            if (loss == 0) return 100;
            double rs = (gain / period) / (loss / period);
            return 100.0 - 100.0 / (1.0 + rs);
        }

        private static double ComputeEma(List<double> values, int period)
        {
            if (values.Count < period) return values.LastOrDefault();
            double k = 2.0 / (period + 1);
            double ema = values.Take(period).Average();
            for (int i = period; i < values.Count; i++)
                ema = values[i] * k + ema * (1 - k);
            return ema;
        }

        private static double ComputeAtr(List<double> closes, List<double> highs, List<double> lows, int period)
        {
            if (closes.Count < 2) return 0;
            var trs = new List<double>();
            for (int i = 1; i < closes.Count; i++)
            {
                double tr = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i] - closes[i - 1])));
                trs.Add(tr);
            }
            return trs.TakeLast(period).Average();
        }
    }
}
