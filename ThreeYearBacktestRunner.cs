using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [3년 백테스트] 현재 MajorCoinStrategy 로직을 1시간봉 기준으로 시뮬레이션
    ///
    /// 파라미터 (appsettings.json 기준):
    ///  - 레버리지: 20x
    ///  - 마진: 잔고의 13% (MajorMarginPercent)
    ///  - 본절 트리거: ROE +5% → SL을 진입가로 이동
    ///  - TP1: ROE +20% → 50% 부분청산
    ///  - TP2: ROE +40% → 나머지 전량 청산
    ///  - 트레일링: ROE +35% 시작, 4% 갭
    ///  - 손절: ROE -20% (= 가격 -1%)
    ///  - AI 점수 임계값: 67점 이상 진입 (지표 복합점수로 시뮬레이션)
    ///  - 수수료: 0.04% (taker) × 2 (진입+청산)
    /// </summary>
    public class ThreeYearBacktestRunner
    {
        // ═══ 실전 파라미터 (appsettings.json 기준) ═══
        private const decimal LEVERAGE            = 20m;
        private const decimal MARGIN_PERCENT      = 0.10m;   // 잔고의 10% (13% → 10%: 리스크 축소)
        private const decimal SL_ROE              = -0.18m;  // -18% ROE = 가격 -0.9% (20% → 18%: 손실 축소)
        private const decimal BREAKEVEN_ROE       = 0.05m;   // +5% ROE → SL → 진입가
        private const decimal TP1_ROE             = 0.20m;   // +20% ROE → 50% 청산
        private const decimal TP2_ROE             = 0.40m;   // +40% ROE → 전량 청산
        private const decimal TRAIL_START_ROE     = 0.35m;   // 트레일링 시작
        private const decimal TRAIL_GAP_ROE       = 0.04m;   // 4% 갭
        // ── 최적화 탐색 대상 (워크포워드 그리드서치에서 교체) ──
        private double _threshold = 70.0;    // 진입 점수 임계값 (v3: 추세추종 고품질만)
        private const decimal FEE_RATE            = 0.0004m; // 0.04% per side
        private const decimal MIN_RR_RATIO        = 1.50m;   // 최소 손익비 (1.10→1.50)
        private const int     MAX_CONCURRENT      = 2;       // 최대 동시 포지션
        private const int     COOLDOWN_HOURS      = 8;       // 거래 후 쿨다운 (시간)

        // 1시간봉 기준 지표 파라미터
        private const int RSI_PERIOD    = 14;
        private const int MACD_FAST     = 12;
        private const int MACD_SLOW     = 26;
        private const int MACD_SIGNAL   = 9;
        private const int BB_PERIOD     = 20;
        private const double BB_MULT    = 2.0;
        private const int EMA_SHORT     = 20;
        private const int EMA_LONG      = 50;
        private const int EMA_TREND     = 200; // [추가] 200EMA: 장기 추세 방향 판단
        private const int ATR_PERIOD    = 14;
        private const int VOL_MA_PERIOD = 20;

        // ═══ 데이터 구조 ═══
        public record TradeRecord(
            string   Symbol,
            DateTime EntryTime,
            DateTime ExitTime,
            string   Direction,     // LONG / SHORT
            decimal  EntryPrice,
            decimal  ExitPrice,
            decimal  MarginUsed,    // USDT
            decimal  RealizedPnl,   // USDT (수수료 차감 후)
            double   EntryScore,
            string   ExitReason     // SL / BE / TP1 / TP2 / TRAIL / END
        );

        public class SymbolReport
        {
            public string   Symbol      { get; set; } = "";
            public int      Trades      { get; set; }
            public int      Wins        { get; set; }
            public int      Losses      { get; set; }
            public decimal  TotalPnl    { get; set; }
            public decimal  WinRate     => Trades > 0 ? (decimal)Wins / Trades * 100m : 0m;
            public decimal  AvgWin      { get; set; }
            public decimal  AvgLoss     { get; set; }
            public decimal  MaxDrawdown { get; set; }
        }

        public class BacktestReport
        {
            public DateTime   StartDate       { get; set; }
            public DateTime   EndDate         { get; set; }
            public decimal    InitialBalance  { get; set; }
            public decimal    FinalBalance    { get; set; }
            public decimal    TotalPnl        => FinalBalance - InitialBalance;
            public decimal    TotalPnlPct     => InitialBalance > 0 ? TotalPnl / InitialBalance * 100m : 0m;
            public decimal    MaxDrawdown     { get; set; }
            public decimal    SharpeRatio     { get; set; }
            public int        TotalTrades     { get; set; }
            public int        WinCount        { get; set; }
            public decimal    WinRate         => TotalTrades > 0 ? (decimal)WinCount / TotalTrades * 100m : 0m;
            public List<TradeRecord>                Trades      { get; set; } = new();
            public List<SymbolReport>               BySymbol    { get; set; } = new();
            public Dictionary<int, decimal>         ByYear      { get; set; } = new();   // year → pnl
            public Dictionary<string, decimal>      ByMonth     { get; set; } = new();   // "2023-04" → pnl
            public Dictionary<string, decimal>      ByDay       { get; set; } = new();   // "2023-04-15" → pnl
            public Dictionary<int, (int trades, int wins)>    YearTrades  { get; set; } = new();
            public Dictionary<string, (int trades, int wins)> MonthTrades { get; set; } = new();
        }

        // ATR 기반 동적 SL/TP 배수 — 비대칭 R:R (TP >> SL)
        // 핵심: 40% 승률에서도 수익이 나려면 avg_win > 2.5 × avg_loss 필요
        private double _stopMult  = 1.8;  // SL  = ATR × 1.8 (적당한 여유)
        private double _tp1Mult   = 4.0;  // TP1 = ATR × 4.0 → R:R 2.2:1 (40% 청산으로 수익 확보)
        private double _tp2Mult   = 8.0;  // TP2 = ATR × 8.0 → R:R 4.4:1 (추세 지속시 대폭 수익)
        private double _beMult    = 2.0;  // 본절 = ATR × 2.0 (TP1 절반 도달시 BE)
        private double _trailMult = 5.0;  // 트레일 시작 = ATR × 5.0
        private double _gapMult   = 1.2;  // 트레일 갭 = ATR × 1.2 (추세에 여유)

        // ═══ 진입 분석 결과 ═══
        private record EntrySignal(double Score, string Direction, string Breakdown, double AtrValue);

        // ═══ 메인 실행 ═══
        public async Task<BacktestReport> RunAsync(
            decimal initialBalance = 2500m,
            int     years          = 3,
            Action<string>? onLog  = null)
        {
            var symbols  = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
            var endDate  = DateTime.UtcNow.Date;
            var startDate= endDate.AddYears(-years);

            onLog?.Invoke($"═══════════════════════════════════════════════════════════════");
            onLog?.Invoke($"[3년 백테스트] {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");
            onLog?.Invoke($"심볼: {string.Join(", ", symbols)}");
            onLog?.Invoke($"초기잔고: ${initialBalance:N0} | 레버리지: {LEVERAGE}x | 마진: {MARGIN_PERCENT*100:F0}%");
            onLog?.Invoke($"SL:{SL_ROE*100:F0}% ROE | BE:{BREAKEVEN_ROE*100:F0}% | TP1:{TP1_ROE*100:F0}% | TP2:{TP2_ROE*100:F0}%");
            onLog?.Invoke($"진입 점수 임계값: {_threshold}점 | ATR 배수: SL×{_stopMult} TP2×{_tp2Mult} / 수수료: {FEE_RATE*100:F2}%/side");
            onLog?.Invoke($"═══════════════════════════════════════════════════════════════");

            var allTrades   = new List<TradeRecord>();
            var symbolReports = new List<SymbolReport>();

            using var client = new BinanceRestClient();

            foreach (var symbol in symbols)
            {
                onLog?.Invoke($"\n📡 [{symbol}] 데이터 수집 중... ({startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd})");
                var klines = await FetchKlinesAsync(client, symbol, startDate, endDate, onLog);

                if (klines.Count < EMA_LONG + 10)
                {
                    onLog?.Invoke($"  ⚠️ [{symbol}] 데이터 부족 ({klines.Count}개), 건너뜀");
                    continue;
                }

                onLog?.Invoke($"  ✅ [{symbol}] {klines.Count}개 1시간봉 수집 완료");
                onLog?.Invoke($"  🔄 [{symbol}] 전략 시뮬레이션 중...");

                var (trades, report) = SimulateSymbol(symbol, klines, initialBalance / symbols.Length, onLog);
                allTrades.AddRange(trades);
                symbolReports.Add(report);

                onLog?.Invoke($"  📊 [{symbol}] {report.Trades}건 | 승률 {report.WinRate:F1}% | PnL ${report.TotalPnl:+#,##0.00;-#,##0.00}");
            }

            // ═══ 전체 집계 ═══
            var report2 = AggregateReport(initialBalance, allTrades, symbolReports, startDate, endDate);
            onLog?.Invoke("\n✅ 시뮬레이션 완료 — 집계 중...");
            return report2;
        }

        // ═══ 워크포워드 최적화 실행 ═══
        // 2/3 기간 훈련 데이터로 최적 파라미터 탐색 → 전체 기간 백테스트 적용
        public async Task<BacktestReport> RunOptimizedAsync(
            decimal initialBalance = 2500m,
            int     years          = 3,
            Action<string>? onLog  = null)
        {
            var symbols   = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
            var endDate   = DateTime.UtcNow.Date;
            var startDate = endDate.AddYears(-years);
            var splitDate = startDate.AddMonths((int)(years * 8));  // 2/3 훈련, 1/3 검증

            onLog?.Invoke("═══════════════════════════════════════════════════════════════");
            onLog?.Invoke($"[워크포워드 최적화] {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");
            onLog?.Invoke($"훈련: {startDate:yyyy-MM-dd} ~ {splitDate:yyyy-MM-dd}");
            onLog?.Invoke($"검증: {splitDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}");
            onLog?.Invoke($"심볼: {string.Join(", ", symbols)} | 초기잔고: ${initialBalance:N0}");
            onLog?.Invoke("═══════════════════════════════════════════════════════════════");

            // Step 1: 모든 심볼 klines 한 번만 수집
            onLog?.Invoke("\n📡 [Step 1] 전체 데이터 수집 중...");
            using var client = new BinanceRestClient();
            var allKlines = new Dictionary<string, List<IBinanceKline>>();
            foreach (var sym in symbols)
            {
                onLog?.Invoke($"  [{sym}] 수집 중...");
                var kl = await FetchKlinesAsync(client, sym, startDate, endDate, null);
                allKlines[sym] = kl;
                onLog?.Invoke($"  ✅ [{sym}] {kl.Count}개");
            }

            // Step 2: 그리드 서치 (훈련 구간)
            double[] thresholds = { 60.0, 65.0, 70.0, 75.0 };
            double[] stopMults  = { 1.0, 1.5, 2.0, 2.5 };
            double[] tp2Mults   = { 3.5, 5.0, 6.5, 8.0 };
            int totalCombos = thresholds.Length * stopMults.Length * tp2Mults.Length;

            onLog?.Invoke($"\n🔍 [Step 2] 그리드 서치: {totalCombos}개 조합 탐색 (훈련 구간)...");

            double bestFitness = double.MinValue;
            double bestThr = _threshold, bestStop = _stopMult, bestTp2 = _tp2Mult;
            int comboIdx = 0;

            foreach (var thr in thresholds)
            foreach (var stp in stopMults)
            foreach (var tp2 in tp2Mults)
            {
                comboIdx++;
                _threshold = thr;
                _stopMult  = stp;
                _tp1Mult   = tp2 / 2.0;
                _tp2Mult   = tp2;

                var trainTrades = new List<TradeRecord>();
                foreach (var sym in symbols)
                {
                    var trainKl = allKlines[sym]
                        .Where(k => k.OpenTime < splitDate).ToList();
                    if (trainKl.Count < EMA_TREND + 10) continue;
                    var (t, _) = SimulateSymbol(sym, trainKl, initialBalance / symbols.Length, null);
                    trainTrades.AddRange(t);
                }

                double fit = ComputeFitness(trainTrades, initialBalance);
                if (fit > bestFitness)
                {
                    bestFitness = fit;
                    bestThr = thr; bestStop = stp; bestTp2 = tp2;
                }

                if (comboIdx % 16 == 0)
                    onLog?.Invoke($"  {comboIdx}/{totalCombos}개 완료... 현재 최고 피트니스: {bestFitness:F3}");
            }

            // Step 3: 최적 파라미터 확정
            _threshold = bestThr;
            _stopMult  = bestStop;
            _tp1Mult   = bestTp2 / 2.0;
            _tp2Mult   = bestTp2;

            onLog?.Invoke($"\n🏆 [Step 3] 최적 파라미터 확정");
            onLog?.Invoke($"  진입 임계값: {_threshold:F0}점");
            onLog?.Invoke($"  ATR 배수: SL×{_stopMult:F1} | TP1×{_tp1Mult:F1} | TP2×{_tp2Mult:F1}");
            onLog?.Invoke($"  훈련 피트니스: {bestFitness:F3}");

            // Step 4: 검증 구간 성과 확인
            onLog?.Invoke($"\n📊 [Step 4] 검증 구간 ({splitDate:yyyy-MM} ~ {endDate:yyyy-MM})...");
            var valTrades = new List<TradeRecord>();
            foreach (var sym in symbols)
            {
                var valKl = allKlines[sym].Where(k => k.OpenTime >= splitDate).ToList();
                if (valKl.Count < EMA_TREND + 10) continue;
                var (t, r) = SimulateSymbol(sym, valKl, initialBalance / symbols.Length, null);
                valTrades.AddRange(t);
                onLog?.Invoke($"  [{sym}] {r.Trades}건 | 승률 {r.WinRate:F1}% | PnL ${r.TotalPnl:+#,##0.00;-#,##0.00}");
            }
            int valWins = valTrades.Count(t => t.RealizedPnl > 0);
            double valWr = valTrades.Count > 0 ? valWins * 100.0 / valTrades.Count : 0;
            onLog?.Invoke($"  검증 총계: {valTrades.Count}건 | 승률 {valWr:F1}% | 피트니스: {ComputeFitness(valTrades, initialBalance / 3):F3}");

            // Step 5: 최적 파라미터로 전체 최종 백테스트
            onLog?.Invoke($"\n🚀 [Step 5] 최적 파라미터로 전체 {years}년 백테스트...");
            var allTrades     = new List<TradeRecord>();
            var symbolReports = new List<SymbolReport>();
            foreach (var sym in symbols)
            {
                var (trades, report) = SimulateSymbol(sym, allKlines[sym], initialBalance / symbols.Length, null);
                allTrades.AddRange(trades);
                symbolReports.Add(report);
                onLog?.Invoke($"  [{sym}] {report.Trades}건 | 승률 {report.WinRate:F1}% | PnL ${report.TotalPnl:+#,##0.00;-#,##0.00}");
            }

            var finalReport = AggregateReport(initialBalance, allTrades, symbolReports, startDate, endDate);
            onLog?.Invoke("\n✅ 워크포워드 최적화 완료");
            return finalReport;
        }

        // ═══ 피트니스 함수: 승률 × 수익성 × 거래수 균형 ═══
        private double ComputeFitness(List<TradeRecord> trades, decimal initialBalance)
        {
            if (trades.Count < 5) return -99.0;

            int wins       = trades.Count(t => t.RealizedPnl > 0);
            double winRate = (double)wins / trades.Count;
            double pnlPct  = (double)trades.Sum(t => t.RealizedPnl) / (double)initialBalance;

            if (pnlPct < -0.7) return -99.0;  // 잔고 70% 이상 손실 페널티

            double tradeFactor = Math.Min(1.0, trades.Count / 20.0);  // 20건 이상이면 만점
            return winRate * (1.0 + pnlPct) * tradeFactor;
        }

        // ═══ 심볼별 시뮬레이션 ═══
        private (List<TradeRecord> trades, SymbolReport report) SimulateSymbol(
            string                       symbol,
            List<IBinanceKline>          klines,
            decimal                      allocatedBalance,
            Action<string>?              onLog)
        {
            var trades   = new List<TradeRecord>();
            decimal balance  = allocatedBalance;
            decimal peakBal  = balance;
            decimal maxDD    = 0m;

            int warmup = Math.Max(EMA_TREND, MACD_SLOW + MACD_SIGNAL) + 5;
            DateTime lastExitTime = DateTime.MinValue; // 쿨다운용

            bool   inPosition    = false;
            string direction     = "";
            decimal entryPrice   = 0m;
            decimal positionQty  = 0m;    // 총 수량 (전체)
            decimal remainQty    = 0m;    // 남은 수량 (TP1 이후)
            decimal marginUsed   = 0m;
            decimal slPrice      = 0m;
            decimal tp1Price     = 0m;
            decimal tp2Price     = 0m;
            decimal trailStart   = 0m;
            decimal trailStop    = 0m;
            decimal trailGapAmt  = 0m;    // ATR 기반 트레일 갭 (가격)
            decimal highestPrice = 0m;    // LONG용 최고가
            decimal lowestPrice  = 0m;    // SHORT용 최저가
            bool    beActivated  = false;
            bool    tp1Done      = false;
            bool    trailActive  = false;
            double  entryScore   = 0;
            DateTime entryTime   = DateTime.MinValue;

            for (int i = warmup; i < klines.Count; i++)
            {
                var candle  = klines[i];
                var candleOpen  = (decimal)candle.OpenPrice;
                var candleHigh  = (decimal)candle.HighPrice;
                var candleLow   = (decimal)candle.LowPrice;
                var candleClose = (decimal)candle.ClosePrice;
                var candleVol   = (decimal)candle.Volume;

                // ── 포지션 관리 (진입 중인 경우) ──
                if (inPosition)
                {
                    if (direction == "LONG")
                        highestPrice = Math.Max(highestPrice, candleHigh);
                    else
                        lowestPrice = Math.Min(lowestPrice, candleLow);

                    decimal currentRoeRaw = direction == "LONG"
                        ? (candleClose - entryPrice) / entryPrice * LEVERAGE
                        : (entryPrice - candleClose) / entryPrice * LEVERAGE;

                    // ── 트레일링 스탑 갱신 (ATR × 3.5 거리 도달 시 활성화) ──
                    if (!trailActive)
                    {
                        bool trailTrigger = direction == "LONG"
                            ? highestPrice >= trailStart
                            : lowestPrice  <= trailStart;
                        if (trailTrigger)
                        {
                            trailActive = true;
                        }
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

                    // ── 본절 활성화 (ATR × 0.8 거리만큼 유리해지면 → SL → 진입가) ──
                    if (!beActivated)
                    {
                        bool beTrigger = direction == "LONG"
                            ? candleClose >= entryPrice + trailGapAmt / (decimal)_gapMult * (decimal)_beMult
                            : candleClose <= entryPrice - trailGapAmt / (decimal)_gapMult * (decimal)_beMult;
                        if (beTrigger)
                        {
                            beActivated = true;
                            slPrice = entryPrice;
                        }
                    }

                    // ── TP1 청산 (50%) ──
                    bool tp1Triggered = !tp1Done &&
                        (direction == "LONG" ? candleHigh >= tp1Price : candleLow <= tp1Price);
                    if (tp1Triggered)
                    {
                        decimal exitPx  = tp1Price;
                        decimal halfQty = positionQty / 2m;
                        decimal rawPnl  = direction == "LONG"
                            ? (exitPx - entryPrice) * halfQty
                            : (entryPrice - exitPx) * halfQty;
                        decimal fee  = exitPx * halfQty * FEE_RATE;
                        decimal netPnl = rawPnl - fee;
                        balance  += netPnl;
                        peakBal   = Math.Max(peakBal, balance);
                        remainQty = positionQty / 2m;
                        tp1Done   = true;

                        trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                            entryPrice, exitPx, marginUsed / 2m, netPnl, entryScore, "TP1"));
                    }

                    // ── TP2 청산 ──
                    bool tp2Triggered = direction == "LONG" ? candleHigh >= tp2Price : candleLow <= tp2Price;
                    if (tp2Triggered)
                    {
                        decimal tp2ExitPx  = tp2Price;
                        decimal tp2Qty     = tp1Done ? remainQty : positionQty;
                        decimal tp2RawPnl  = direction == "LONG"
                            ? (tp2ExitPx - entryPrice) * tp2Qty
                            : (entryPrice - tp2ExitPx) * tp2Qty;
                        decimal tp2Fee     = tp2ExitPx * tp2Qty * FEE_RATE;
                        decimal tp2NetPnl  = tp2RawPnl - tp2Fee;
                        balance  += tp2NetPnl;
                        peakBal   = Math.Max(peakBal, balance);

                        trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                            entryPrice, tp2ExitPx, tp1Done ? marginUsed / 2m : marginUsed, tp2NetPnl, entryScore, "TP2"));

                        decimal tp2Dd = (peakBal - balance) / peakBal;
                        maxDD = Math.Max(maxDD, tp2Dd);
                        inPosition = false; lastExitTime = candle.OpenTime;
                        tp1Done = false;
                        continue;
                    }

                    // ── 트레일링 스탑 청산 ──
                    if (trailActive && trailStop > 0m)
                    {
                        bool trailHit = direction == "LONG" ? candleLow <= trailStop : candleHigh >= trailStop;
                        if (trailHit)
                        {
                            decimal trailExitPx  = trailStop;
                            decimal trailQty     = tp1Done ? remainQty : positionQty;
                            decimal trailRawPnl  = direction == "LONG"
                                ? (trailExitPx - entryPrice) * trailQty
                                : (entryPrice - trailExitPx) * trailQty;
                            decimal trailFee     = trailExitPx * trailQty * FEE_RATE;
                            decimal trailNetPnl  = trailRawPnl - trailFee;
                            balance  += trailNetPnl;
                            peakBal   = Math.Max(peakBal, balance);

                            trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                                entryPrice, trailExitPx, tp1Done ? marginUsed / 2m : marginUsed, trailNetPnl, entryScore, "TRAIL"));

                            decimal trailDd = (peakBal - balance) / peakBal;
                            maxDD = Math.Max(maxDD, trailDd);
                            inPosition = false; lastExitTime = candle.OpenTime;
                            tp1Done = false;
                            continue;
                        }
                    }

                    // ── 손절 / 본절 청산 ──
                    bool slHit = direction == "LONG" ? candleLow <= slPrice : candleHigh >= slPrice;
                    if (slHit)
                    {
                        decimal slExitPx  = slPrice;
                        decimal slQty     = tp1Done ? remainQty : positionQty;
                        decimal slRawPnl  = direction == "LONG"
                            ? (slExitPx - entryPrice) * slQty
                            : (entryPrice - slExitPx) * slQty;
                        decimal slFee     = slExitPx * slQty * FEE_RATE;
                        decimal slNetPnl  = slRawPnl - slFee;
                        balance  += slNetPnl;
                        peakBal   = Math.Max(peakBal, balance);

                        string reason = beActivated ? "BE" : "SL";
                        trades.Add(new TradeRecord(symbol, entryTime, candle.OpenTime, direction,
                            entryPrice, slExitPx, tp1Done ? marginUsed / 2m : marginUsed, slNetPnl, entryScore, reason));

                        decimal slDd = (peakBal - balance) / peakBal;
                        maxDD = Math.Max(maxDD, slDd);
                        inPosition = false; lastExitTime = candle.OpenTime;
                        tp1Done = false;
                        continue;
                    }

                    continue; // 포지션 중에는 진입 스캔 안 함
                }

                // ── 신규 진입 스캔 ──
                if (balance < 50m) continue; // 잔고 부족

                // 쿨다운: 마지막 청산 후 COOLDOWN_HOURS 이내 재진입 금지
                if (lastExitTime != DateTime.MinValue &&
                    (candle.OpenTime - lastExitTime).TotalHours < COOLDOWN_HOURS) continue;

                var window = klines.GetRange(Math.Max(0, i - 220), Math.Min(220, i + 1));
                var signal = EvaluateEntry(window, symbol);

                if (signal == null || signal.Score < _threshold)
                    continue;

                // ── ATR 기반 동적 SL/TP 계산 ──
                // ATR = signal.AtrValue (EvaluateEntry에서 계산된 1H ATR, 가격 단위)
                // SL = ATR × 1.5 | TP1 = ATR × 2.5 | TP2 = ATR × 5.0
                // min stop: 가격의 1.0%, max stop: 가격의 4.0% (과도한 손실 방지)
                decimal eprice   = candleClose;
                double  rawAtr   = signal.AtrValue > 0 ? signal.AtrValue : (double)eprice * 0.015;
                double  atrClamp = Math.Clamp(rawAtr, (double)eprice * 0.010, (double)eprice * 0.040);
                decimal atrD     = (decimal)atrClamp;

                decimal dynSl    = atrD * (decimal)_stopMult;   // stop 거리 (가격)
                decimal dynTp1   = atrD * (decimal)_tp1Mult;    // TP1 거리
                decimal dynTp2   = atrD * (decimal)_tp2Mult;    // TP2 거리
                decimal dynBe    = atrD * (decimal)_beMult;     // 본절 트리거 거리
                decimal dynTrail = atrD * (decimal)_trailMult;  // 트레일 시작 거리
                decimal dynGap   = atrD * (decimal)_gapMult;    // 트레일 갭

                // R:R 확인: TP2 / SL >= 1.5 (항상 만족, 5.0/1.5=3.33)
                if (dynSl <= 0m || dynTp2 / dynSl < 1.5m) continue;

                // 진입 실행
                decimal margin   = Math.Min(balance * MARGIN_PERCENT, balance * 0.95m);
                decimal notional = margin * LEVERAGE;
                decimal qty      = notional / eprice;
                decimal entryFee = notional * FEE_RATE;
                balance -= entryFee;

                direction    = signal.Direction;
                entryPrice   = eprice;
                positionQty  = qty;
                remainQty    = qty;
                marginUsed   = margin;
                entryScore   = signal.Score;
                entryTime    = candle.OpenTime;
                beActivated  = false;
                tp1Done      = false;
                trailActive  = false;
                trailStop    = 0m;
                trailGapAmt  = dynGap;

                if (direction == "LONG")
                {
                    slPrice      = eprice - dynSl;
                    tp1Price     = eprice + dynTp1;
                    tp2Price     = eprice + dynTp2;
                    trailStart   = eprice + dynTrail;
                    highestPrice = eprice;
                }
                else // SHORT
                {
                    slPrice      = eprice + dynSl;
                    tp1Price     = eprice - dynTp1;
                    tp2Price     = eprice - dynTp2;
                    trailStart   = eprice - dynTrail;
                    lowestPrice  = eprice;
                }

                inPosition = true;
            }

            // ── 기간 종료 시 미청산 포지션 강제 청산 ──
            if (inPosition && klines.Count > 0)
            {
                var last    = klines[klines.Count - 1];
                decimal exitPx = (decimal)last.ClosePrice;
                decimal qty    = tp1Done ? remainQty : positionQty;
                decimal rawPnl = direction == "LONG"
                    ? (exitPx - entryPrice) * qty
                    : (entryPrice - exitPx) * qty;
                decimal fee    = exitPx * qty * FEE_RATE;
                decimal netPnl = rawPnl - fee;
                balance += netPnl;

                trades.Add(new TradeRecord(symbol, entryTime, last.OpenTime, direction,
                    entryPrice, exitPx, tp1Done ? marginUsed / 2m : marginUsed, netPnl, entryScore, "END"));
            }

            // ── 심볼 리포트 ──
            var wins   = trades.Where(t => t.RealizedPnl > 0).ToList();
            var losses = trades.Where(t => t.RealizedPnl <= 0).ToList();
            var report = new SymbolReport
            {
                Symbol      = symbol,
                Trades      = trades.Count,
                Wins        = wins.Count,
                Losses      = losses.Count,
                TotalPnl    = balance - allocatedBalance,
                AvgWin      = wins.Count > 0 ? wins.Average(t => t.RealizedPnl) : 0m,
                AvgLoss     = losses.Count > 0 ? losses.Average(t => t.RealizedPnl) : 0m,
                MaxDrawdown = maxDD * 100m
            };

            return (trades, report);
        }

        // ═══ 진입 신호 평가 v3 (추세추종 풀백 전략) ═══
        //
        // 핵심 원칙: "추세 방향으로만 풀백 진입"
        //  v1: 하드필터 과다 → 3년 18건, 승률 33%
        //  v2: 소프트감점 → 3년 1664건 과매매, 승률 40%, -80%
        //  v3: 추세 정렬 필수 + 풀백 감지 + 반등 확인 + 쿨다운
        //
        // 구조:
        //  [Gate 1] 추세 방향 확인 (EMA20 > EMA50 = 상승추세 → LONG만 허용)
        //  [Gate 2] 풀백 구간 감지 (BB 하단 접근 or EMA20 터치)
        //  [Gate 3] 반등/반락 확인 (양봉 출현 + RSI 반등 + 볼륨 확인)
        //  [Gate 4] 점수 70+ 이상만 진입
        private EntrySignal? EvaluateEntry(List<IBinanceKline> window, string symbol)
        {
            if (window.Count < EMA_LONG + 5) return null;

            var closes  = window.Select(k => (double)k.ClosePrice).ToList();
            var highs   = window.Select(k => (double)k.HighPrice).ToList();
            var lows    = window.Select(k => (double)k.LowPrice).ToList();
            var opens   = window.Select(k => (double)k.OpenPrice).ToList();
            var volumes = window.Select(k => (double)k.Volume).ToList();

            int n = closes.Count;
            double price = closes[n - 1];

            // ── 지표 계산 ──
            double rsi     = ComputeRsi(closes, RSI_PERIOD);
            double rsiPrev = ComputeRsi(closes.Take(n - 1).ToList(), RSI_PERIOD);

            double emaFast = ComputeEma(closes, MACD_FAST);
            double emaSlow = ComputeEma(closes, MACD_SLOW);
            double macdNow = emaFast - emaSlow;
            double macdPrevVal = ComputeEma(closes.Take(n - 1).ToList(), MACD_FAST)
                               - ComputeEma(closes.Take(n - 1).ToList(), MACD_SLOW);
            bool macdRising = macdNow > macdPrevVal;
            bool macdPos    = macdNow > 0;

            var bbSlice = closes.TakeLast(BB_PERIOD).ToList();
            double bbMid   = bbSlice.Average();
            double bbStd   = Math.Sqrt(bbSlice.Average(c => (c - bbMid) * (c - bbMid)));
            double bbWidth = (bbMid + BB_MULT * bbStd) - (bbMid - BB_MULT * bbStd);
            double bbPos   = bbWidth > 0 ? (price - (bbMid - BB_MULT * bbStd)) / bbWidth : 0.5;

            double ema20     = ComputeEma(closes, EMA_SHORT);
            double ema50     = ComputeEma(closes, EMA_LONG);
            double ema20Prev = ComputeEma(closes.Take(n - 1).ToList(), EMA_SHORT);
            double ema50Prev = ComputeEma(closes.Take(n - 1).ToList(), EMA_LONG);

            double volAvg   = volumes.TakeLast(VOL_MA_PERIOD).Average();
            double volRatio = volAvg > 0 ? volumes[n - 1] / volAvg : 1.0;

            double atr    = ComputeAtr(closes, highs, lows, ATR_PERIOD);
            double atrPct = price > 0 ? atr / price * 100.0 : 0;
            if (atrPct > 4.0) return null;  // 극단 변동성 차단 (5→4%)

            // ── 캔들 분석 ──
            double lastOpen  = opens[n - 1], lastClose = closes[n - 1];
            double lastHigh  = highs[n - 1], lastLow   = lows[n - 1];
            double totalRange = lastHigh - lastLow;
            double body = Math.Abs(lastClose - lastOpen);
            double bodyRatio = totalRange > 0 ? body / totalRange : 0;
            bool isBullCandle = lastClose > lastOpen && bodyRatio >= 0.40; // 확신 양봉
            bool isBearCandle = lastClose < lastOpen && bodyRatio >= 0.40; // 확신 음봉

            // ── 추세 판단 ──
            bool ema20AboveEma50 = ema20 > ema50;       // 상승추세
            bool ema20BelowEma50 = ema20 < ema50;       // 하락추세
            bool ema20Rising     = ema20 > ema20Prev;
            bool ema20Falling    = ema20 < ema20Prev;
            bool ema50Rising     = ema50 > ema50Prev;
            bool ema50Falling    = ema50 < ema50Prev;

            // 풀백 감지: 가격이 EMA20 근처(±1.2%)로 내려왔는지
            double ema20Dist = ema20 > 0 ? (price - ema20) / ema20 : 0; // 양수=위, 음수=아래

            var bd = new StringBuilder();

            // ═══════════════════════════════════════════
            // [Gate 1] 추세 방향 확인 — 필수 조건
            // ═══════════════════════════════════════════
            bool longTrend  = ema20AboveEma50 && ema20Rising;   // LONG 허용: EMA20>50 + EMA20 상승
            bool shortTrend = ema20BelowEma50 && ema20Falling;  // SHORT 허용: EMA20<50 + EMA20 하락

            if (!longTrend && !shortTrend) return null; // 추세 불명확 → 진입 안함

            // ═══════════════════════════════════════════
            // LONG 진입 평가
            // ═══════════════════════════════════════════
            if (longTrend)
            {
                double score = 0;

                // [Gate 2] 풀백 구간: 가격이 EMA20 아래~근처로 내려왔어야 함 (0~30점)
                // 핵심: 추세 중 가격이 EMA20 근처로 눌림 = 최고의 진입점
                double pullback;
                if (ema20Dist <= -0.005)     pullback = 30;  // EMA20 아래까지 하락 = 최고 풀백
                else if (ema20Dist <= 0.003) pullback = 25;  // EMA20 근접 (±0.3%)
                else if (ema20Dist <= 0.008) pullback = 18;  // EMA20 살짝 위 (0.8%)
                else if (ema20Dist <= 0.015) pullback = 10;  // 1.5% 위
                else                         pullback = 0;   // 너무 멀리 = 풀백 아님
                score += pullback;
                bd.Append($"Pull={pullback:F0} ");

                // [Gate 3] 반등 확인 (0~30점)
                double bounce = 0;
                if (isBullCandle) bounce += 15;                       // 양봉 출현
                if (rsi > rsiPrev && rsi >= 30 && rsi <= 60) bounce += 10; // RSI 반등 중 (30~60)
                if (macdRising) bounce += 5;                          // MACD 반전 중
                score += bounce;
                bd.Append($"Bounce={bounce:F0} ");

                // [추가] 볼륨 확인 (0~20점)
                double vol = volRatio >= 2.0 ? 20 : volRatio >= 1.5 ? 15 : volRatio >= 1.0 ? 10 : 5;
                score += vol;
                bd.Append($"Vol={vol:F0} ");

                // [추가] BB 위치 보너스 (0~10점) — 하단일수록 반등 확률 높음
                double bbBonus = bbPos <= 0.25 ? 10 : bbPos <= 0.40 ? 7 : bbPos <= 0.55 ? 3 : 0;
                score += bbBonus;
                bd.Append($"BB={bbBonus:F0} ");

                // [추가] 추세 강도 보너스 (0~10점) — EMA50도 상승이면 추세 더 강함
                if (ema50Rising) { score += 5; bd.Append("E50^+5 "); }
                if (macdPos)     { score += 5; bd.Append("MACD++5 "); }

                bd.Append($"[LONG total={score:F0}]");

                if (score >= _threshold)
                    return new EntrySignal(score, "LONG", bd.ToString(), atr);
            }

            // ═══════════════════════════════════════════
            // SHORT 진입 평가
            // ═══════════════════════════════════════════
            if (shortTrend)
            {
                double score = 0;
                bd.Clear();

                // [Gate 2] 풀백 구간: 가격이 EMA20 위~근처로 올라왔어야 함
                double pullback;
                if (ema20Dist >= 0.005)       pullback = 30;
                else if (ema20Dist >= -0.003) pullback = 25;
                else if (ema20Dist >= -0.008) pullback = 18;
                else if (ema20Dist >= -0.015) pullback = 10;
                else                          pullback = 0;
                score += pullback;
                bd.Append($"Pull={pullback:F0} ");

                // [Gate 3] 반락 확인
                double bounce = 0;
                if (isBearCandle) bounce += 15;
                if (rsi < rsiPrev && rsi >= 40 && rsi <= 70) bounce += 10;
                if (!macdRising) bounce += 5;
                score += bounce;
                bd.Append($"Bounce={bounce:F0} ");

                // 볼륨
                double vol = volRatio >= 2.0 ? 20 : volRatio >= 1.5 ? 15 : volRatio >= 1.0 ? 10 : 5;
                score += vol;
                bd.Append($"Vol={vol:F0} ");

                // BB 상단 보너스
                double bbBonus = bbPos >= 0.75 ? 10 : bbPos >= 0.60 ? 7 : bbPos >= 0.45 ? 3 : 0;
                score += bbBonus;
                bd.Append($"BB={bbBonus:F0} ");

                // 추세 강도
                if (ema50Falling) { score += 5; bd.Append("E50v+5 "); }
                if (!macdPos)     { score += 5; bd.Append("MACD-+5 "); }

                bd.Append($"[SHORT total={score:F0}]");

                if (score >= _threshold)
                    return new EntrySignal(score, "SHORT", bd.ToString(), atr);
            }

            return null;
        }

        // ═══ 결과 집계 ═══
        private BacktestReport AggregateReport(
            decimal         initialBalance,
            List<TradeRecord> trades,
            List<SymbolReport> symbolReports,
            DateTime        startDate,
            DateTime        endDate)
        {
            decimal finalBalance = initialBalance + trades.Sum(t => t.RealizedPnl);

            // 연별
            var byYear = trades
                .GroupBy(t => t.ExitTime.Year)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));

            // 월별
            var byMonth = trades
                .GroupBy(t => t.ExitTime.ToString("yyyy-MM"))
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));

            // 일별
            var byDay = trades
                .GroupBy(t => t.ExitTime.ToString("yyyy-MM-dd"))
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));

            // 연별 거래 수
            var yearTrades = trades
                .GroupBy(t => t.ExitTime.Year)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Count(t => t.RealizedPnl > 0)));

            var monthTrades = trades
                .GroupBy(t => t.ExitTime.ToString("yyyy-MM"))
                .ToDictionary(g => g.Key, g => (g.Count(), g.Count(t => t.RealizedPnl > 0)));

            // MDD
            decimal balance  = initialBalance;
            decimal peak     = balance;
            decimal maxDD    = 0m;
            var     sortedT  = trades.OrderBy(t => t.ExitTime).ToList();
            foreach (var t in sortedT)
            {
                balance += t.RealizedPnl;
                peak     = Math.Max(peak, balance);
                maxDD    = Math.Max(maxDD, peak > 0 ? (peak - balance) / peak * 100m : 0m);
            }

            // 샤프 비율 (일별 수익 기준)
            var dailyReturns = byDay.Values.Select(v => (double)(v / initialBalance)).ToList();
            double sharpe = 0;
            if (dailyReturns.Count > 1)
            {
                double avg = dailyReturns.Average();
                double std = Math.Sqrt(dailyReturns.Average(r => (r - avg) * (r - avg)));
                if (std > 0) sharpe = avg / std * Math.Sqrt(365.0);
            }

            int wins = trades.Count(t => t.RealizedPnl > 0);

            return new BacktestReport
            {
                StartDate      = startDate,
                EndDate        = endDate,
                InitialBalance = initialBalance,
                FinalBalance   = finalBalance,
                MaxDrawdown    = maxDD,
                SharpeRatio    = (decimal)sharpe,
                TotalTrades    = trades.Count,
                WinCount       = wins,
                Trades         = trades,
                BySymbol       = symbolReports,
                ByYear         = byYear,
                ByMonth        = byMonth,
                ByDay          = byDay,
                YearTrades     = yearTrades,
                MonthTrades    = monthTrades
            };
        }

        // ═══ 리포트 출력 ═══
        public string FormatReport(BacktestReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════╗");
            sb.AppendLine($"║   3년 백테스트 결과   {r.StartDate:yyyy-MM-dd} ~ {r.EndDate:yyyy-MM-dd}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║  초기 잔고  : ${r.InitialBalance,10:N2}");
            sb.AppendLine($"║  최종 잔고  : ${r.FinalBalance,10:N2}");
            sb.AppendLine($"║  총 수 익   : ${r.TotalPnl,+10:N2}  ({r.TotalPnlPct:+0.00;-0.00}%)");
            sb.AppendLine($"║  총 거래수  : {r.TotalTrades,6}건   승률: {r.WinRate:F1}% ({r.WinCount}승 {r.TotalTrades-r.WinCount}패)");
            sb.AppendLine($"║  최대 낙폭  : {r.MaxDrawdown:F2}%");
            sb.AppendLine($"║  샤프 비율  : {r.SharpeRatio:F3}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");

            // 연별
            sb.AppendLine("║  [연별 수익]");
            foreach (var kv in r.ByYear.OrderBy(k => k.Key))
            {
                var (cnt, win) = r.YearTrades.GetValueOrDefault(kv.Key);
                decimal yrPct = r.InitialBalance > 0 ? kv.Value / r.InitialBalance * 100m : 0m;
                sb.AppendLine($"║   {kv.Key}년  ${kv.Value,+9:N2} ({yrPct:+0.0;-0.0}%)  거래:{cnt}건 승률:{(cnt > 0 ? (decimal)win/cnt*100m : 0m):F0}%");
            }

            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            // 월별
            sb.AppendLine("║  [월별 수익]");
            foreach (var kv in r.ByMonth.OrderBy(k => k.Key))
            {
                var (cnt, win) = r.MonthTrades.GetValueOrDefault(kv.Key);
                string bar = kv.Value >= 0
                    ? new string('█', Math.Min(20, (int)(kv.Value / 50)))
                    : new string('░', Math.Min(20, (int)(-kv.Value / 50)));
                string sign = kv.Value >= 0 ? "+" : "";
                sb.AppendLine($"║   {kv.Key}  {sign}${kv.Value:N0,8}  [{bar,-20}]  {cnt}건");
            }

            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            // 심볼별
            sb.AppendLine("║  [심볼별 성과]");
            foreach (var s in r.BySymbol.OrderByDescending(s => s.TotalPnl))
            {
                sb.AppendLine($"║   {s.Symbol,-10}  PnL:${s.TotalPnl,+9:N2}  거래:{s.Trades}건  승률:{s.WinRate:F0}%  MDD:{s.MaxDrawdown:F1}%");
                if (s.Wins > 0 || s.Losses > 0)
                    sb.AppendLine($"║              평균수익:${s.AvgWin:+N2}  평균손실:${s.AvgLoss:N2}");
            }

            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            // 일별 (최근 30일)
            var recentDays = r.ByDay.OrderByDescending(k => k.Key).Take(30).OrderBy(k => k.Key).ToList();
            sb.AppendLine($"║  [일별 수익 — 최근 {recentDays.Count}일]");
            decimal cumDay = 0;
            foreach (var kv in recentDays)
            {
                cumDay += kv.Value;
                string sign = kv.Value >= 0 ? "+" : "";
                sb.AppendLine($"║   {kv.Key}  {sign}${kv.Value:N2,8}  (누계 {sign}${cumDay:N2})");
            }

            // 일평균 수익
            int totalDays = (r.EndDate - r.StartDate).Days;
            if (totalDays > 0)
            {
                decimal dailyAvg = r.TotalPnl / totalDays;
                decimal monthAvg = dailyAvg * 30m;
                decimal yearAvg  = dailyAvg * 365m;
                sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
                sb.AppendLine($"║  [추정 평균 수익]");
                sb.AppendLine($"║   일 평균  : ${dailyAvg:+N2;-N2}  /일");
                sb.AppendLine($"║   월 평균  : ${monthAvg:+N2;-N2}  /월");
                sb.AppendLine($"║   연 평균  : ${yearAvg:+N2;-N2}  /년");
            }
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════╝");

            return sb.ToString();
        }

        // ═══ 리포트 파일 저장 ═══
        public string SaveReportToFile(BacktestReport report)
        {
            string fileName = $"backtest_3yr_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path     = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            var sb = new StringBuilder();
            sb.AppendLine(FormatReport(report));
            sb.AppendLine();
            sb.AppendLine("═══ 전체 거래 상세 ═══");
            sb.AppendLine($"{"시간",-20} {"심볼",-10} {"방향",-6} {"진입가",-12} {"청산가",-12} {"수익",-12} {"이유",-8} {"점수",-6}");
            sb.AppendLine(new string('-', 90));
            foreach (var t in report.Trades.OrderBy(t => t.ExitTime))
            {
                string sign = t.RealizedPnl >= 0 ? "+" : "";
                sb.AppendLine($"{t.ExitTime:yyyy-MM-dd HH:mm,-20} {t.Symbol,-10} {t.Direction,-6} {t.EntryPrice,-12:F4} {t.ExitPrice,-12:F4} {sign}${t.RealizedPnl,-10:N2} {t.ExitReason,-8} {t.EntryScore:F0}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        // ═══ Binance 데이터 수집 ═══
        private static async Task<List<IBinanceKline>> FetchKlinesAsync(
            BinanceRestClient client,
            string            symbol,
            DateTime          startUtc,
            DateTime          endUtc,
            Action<string>?   onLog = null)
        {
            var result = new List<IBinanceKline>();
            var cursor  = startUtc;
            int chunk   = 1000; // 1h × 1000 = ~41일/요청

            while (cursor < endUtc)
            {
                var to = cursor.AddHours(chunk);
                if (to > endUtc) to = endUtc;

                var resp = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.OneHour,
                    startTime: cursor, endTime: to,
                    limit: chunk);

                if (!resp.Success || resp.Data == null || !resp.Data.Any())
                    break;

                result.AddRange(resp.Data);
                cursor = resp.Data.Last().CloseTime.AddMilliseconds(1);

                await Task.Delay(120); // 속도 제한 방지
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
            double k   = 2.0 / (period + 1);
            double ema = values.Take(period).Average();
            for (int i = period; i < values.Count; i++)
                ema = values[i] * k + ema * (1 - k);
            return ema;
        }

        private static double ComputeEmaFromValues(List<double> values, int period)
        {
            if (values.Count < period) return values.LastOrDefault();
            double k   = 2.0 / (period + 1);
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
                                     Math.Abs(lows[i]  - closes[i - 1])));
                trs.Add(tr);
            }
            return trs.TakeLast(period).Average();
        }
    }
}
