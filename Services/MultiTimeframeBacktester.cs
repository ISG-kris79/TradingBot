using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [다중 타임프레임 백테스터 v1]
    ///
    /// 핵심 전략: 5분봉 기반 모멘텀 스캘핑 + 상위 TF 추세 필터
    ///
    /// 구조:
    ///   4H봉 → 추세 방향 결정 (EMA20/50 정렬)
    ///   1H봉 → 중기 모멘텀 확인 (MACD, RSI)
    ///   5분봉 → 진입 타이밍 (볼륨 급증 + 가격 돌파 + 캔들 패턴)
    ///
    /// 목표:
    ///   - 일 평균 5% 계좌 수익 ($125 on $2,500)
    ///   - 승률 50%+ with R:R 2:1+
    ///   - 심볼: 메이저 4종 + 펌프 코인
    /// </summary>
    public class MultiTimeframeBacktester
    {
        private const decimal LEVERAGE       = 20m;
        private const decimal MARGIN_PERCENT = 0.12m;  // 잔고의 12%
        private const decimal FEE_RATE       = 0.0004m;
        private const int     COOLDOWN_BARS  = 12;     // 5분봉 12개 = 1시간 쿨다운

        // 지표 파라미터
        private const int RSI_PERIOD = 14;
        private const int EMA_FAST   = 9;
        private const int EMA_SLOW   = 21;
        private const int EMA_TREND  = 50;
        private const int ATR_PERIOD = 14;
        private const int VOL_PERIOD = 20;
        private const int BB_PERIOD  = 20;
        private const double BB_MULT = 2.0;

        // 진입/청산 파라미터 (최적화 대상)
        private double _entryThreshold  = 65.0;
        private double _slAtrMult       = 1.5;   // SL = ATR × 1.5
        private double _tp1AtrMult      = 3.0;   // TP1 = ATR × 3.0 (R:R 2:1)
        private double _tp2AtrMult      = 6.0;   // TP2 = ATR × 6.0 (R:R 4:1)
        private double _beAtrMult       = 1.8;   // BE 트리거
        private double _trailAtrMult    = 4.0;   // 트레일 시작
        private double _trailGapMult    = 0.8;   // 트레일 갭
        private double _tp1ExitRatio    = 0.40;   // TP1에서 40% 청산

        // ═══ 데이터 구조 ═══
        public record TradeRecord(
            string Symbol, DateTime EntryTime, DateTime ExitTime,
            string Direction, decimal EntryPrice, decimal ExitPrice,
            decimal MarginUsed, decimal RealizedPnl, double EntryScore,
            string ExitReason, int HoldBars);

        public class BacktestResult
        {
            public decimal InitialBalance { get; set; }
            public decimal FinalBalance { get; set; }
            public decimal TotalPnl => FinalBalance - InitialBalance;
            public decimal TotalPnlPct => InitialBalance > 0 ? TotalPnl / InitialBalance * 100m : 0;
            public int TotalTrades { get; set; }
            public int Wins { get; set; }
            public decimal WinRate => TotalTrades > 0 ? (decimal)Wins / TotalTrades * 100m : 0;
            public decimal MaxDrawdown { get; set; }
            public decimal AvgWinPnl { get; set; }
            public decimal AvgLossPnl { get; set; }
            public decimal ProfitFactor { get; set; }
            public decimal SharpeRatio { get; set; }
            public decimal AvgDailyPct { get; set; }
            public List<TradeRecord> Trades { get; set; } = new();
            public Dictionary<string, decimal> ByDay { get; set; } = new();
            public Dictionary<string, decimal> ByMonth { get; set; } = new();
            public Dictionary<string, (int t, int w, decimal pnl)> BySymbol { get; set; } = new();
            public string ParamDesc { get; set; } = "";
        }

        // 내부 캔들 구조 (지표 포함)
        private class Candle
        {
            public DateTime Time;
            public double Open, High, Low, Close, Volume;
            public double RSI, EMA9, EMA21, EMA50, MACD, MACDPrev;
            public double ATR, BBUpper, BBLower, BBMid, BBPos;
            public double VolRatio;
            public bool IsBullish => Close > Open;
            public double BodyRatio => (High - Low) > 0 ? Math.Abs(Close - Open) / (High - Low) : 0;
        }

        // ═══ 메인 실행 ═══
        public async Task<BacktestResult> RunAsync(
            decimal initialBalance = 2500m,
            int months = 6,
            Action<string>? onLog = null)
        {
            var symbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT",
                                  "DOGEUSDT", "PEPEUSDT", "WIFUSDT", "BONKUSDT" };
            var endDate   = DateTime.UtcNow.Date;
            var startDate = endDate.AddMonths(-months);

            onLog?.Invoke("╔══════════════════════════════════════════════════════════════════╗");
            onLog?.Invoke("║     다중 타임프레임 백테스터 (5분봉 모멘텀 스캘핑)              ║");
            onLog?.Invoke("╠══════════════════════════════════════════════════════════════════╣");
            onLog?.Invoke($"║  기간: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} ({months}개월)");
            onLog?.Invoke($"║  심볼: {string.Join(", ", symbols)}");
            onLog?.Invoke($"║  초기잔고: ${initialBalance:N0} | 레버리지: {LEVERAGE}x | 마진: {MARGIN_PERCENT*100:F0}%");
            onLog?.Invoke($"║  SL: ATR×{_slAtrMult} | TP1: ATR×{_tp1AtrMult} | TP2: ATR×{_tp2AtrMult}");
            onLog?.Invoke("╚══════════════════════════════════════════════════════════════════╝");

            using var client = new BinanceRestClient();
            var allTrades = new List<TradeRecord>();
            decimal balance = initialBalance;

            foreach (var symbol in symbols)
            {
                onLog?.Invoke($"\n[{symbol}] 5분봉 데이터 수집 중...");
                var klines5m = await FetchKlinesAsync(client, symbol, KlineInterval.FiveMinutes, startDate, endDate);
                if (klines5m.Count < 200)
                {
                    onLog?.Invoke($"  [{symbol}] 데이터 부족 ({klines5m.Count}개), 건너뜀");
                    continue;
                }
                onLog?.Invoke($"  [{symbol}] {klines5m.Count}개 5분봉 수집 완료");

                // 5분봉에서 지표 계산
                var candles = BuildCandles(klines5m);
                onLog?.Invoke($"  [{symbol}] 지표 계산 완료, 시뮬레이션 중...");

                var symbolAlloc = initialBalance / symbols.Length;
                var trades = SimulateSymbol(symbol, candles, symbolAlloc);
                allTrades.AddRange(trades);

                int w = trades.Count(t => t.RealizedPnl > 0);
                decimal pnl = trades.Sum(t => t.RealizedPnl);
                onLog?.Invoke($"  [{symbol}] {trades.Count}건 | 승률 {(trades.Count > 0 ? w * 100.0 / trades.Count : 0):F1}% | PnL ${pnl:+#,##0.00;-#,##0.00}");
            }

            var result = BuildResult(initialBalance, allTrades, startDate, endDate);
            onLog?.Invoke("\n" + FormatResult(result));

            string path = SaveResult(result);
            onLog?.Invoke($"\n결과 저장: {path}");

            return result;
        }

        // ═══ 자동 최적화: 파라미터 조합을 반복하며 일 5% 목표 탐색 ═══
        public async Task<BacktestResult> RunOptimizeAsync(
            decimal initialBalance = 2500m,
            int months = 6,
            Action<string>? onLog = null)
        {
            var symbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT",
                                  "DOGEUSDT", "PEPEUSDT", "WIFUSDT", "BONKUSDT" };
            var endDate   = DateTime.UtcNow.Date;
            var startDate = endDate.AddMonths(-months);

            onLog?.Invoke("╔══════════════════════════════════════════════════════════════════╗");
            onLog?.Invoke("║     다중 TF 자동 최적화 (일 5% 목표)                            ║");
            onLog?.Invoke("╚══════════════════════════════════════════════════════════════════╝");

            // 데이터 수집 (1회)
            using var client = new BinanceRestClient();
            var allKlines = new Dictionary<string, List<Candle>>();
            foreach (var sym in symbols)
            {
                onLog?.Invoke($"[{sym}] 수집 중...");
                var kl = await FetchKlinesAsync(client, sym, KlineInterval.FiveMinutes, startDate, endDate);
                if (kl.Count >= 200)
                {
                    allKlines[sym] = BuildCandles(kl);
                    onLog?.Invoke($"  [{sym}] {kl.Count}개 → {allKlines[sym].Count}개 캔들");
                }
            }

            // 파라미터 그리드
            double[] thresholds = { 55, 60, 65, 70 };
            double[] slMults    = { 1.2, 1.5, 1.8, 2.2 };
            double[] tp1Mults   = { 2.5, 3.0, 3.5, 4.5 };
            double[] tp2Mults   = { 5.0, 6.0, 8.0, 10.0 };
            int total = thresholds.Length * slMults.Length * tp1Mults.Length * tp2Mults.Length;

            onLog?.Invoke($"\n그리드 서치: {total}개 조합 탐색 중...");

            BacktestResult? bestResult = null;
            double bestScore = double.MinValue;
            int idx = 0;

            foreach (var thr in thresholds)
            foreach (var sl in slMults)
            foreach (var tp1 in tp1Mults)
            foreach (var tp2 in tp2Mults)
            {
                idx++;
                if (tp1 <= sl * 1.3 || tp2 <= tp1 * 1.3) continue; // 유효하지 않은 조합 스킵

                _entryThreshold = thr;
                _slAtrMult = sl;
                _tp1AtrMult = tp1;
                _tp2AtrMult = tp2;
                _beAtrMult = sl * 1.2;
                _trailAtrMult = tp1 * 1.3;
                _trailGapMult = sl * 0.5;

                var trades = new List<TradeRecord>();
                foreach (var (sym, candles) in allKlines)
                {
                    var t = SimulateSymbol(sym, candles, initialBalance / allKlines.Count);
                    trades.AddRange(t);
                }

                if (trades.Count < 10) continue;

                var result = BuildResult(initialBalance, trades, startDate, endDate);

                // 피트니스: 일 수익% × 승률 × ProfitFactor × (1 - MDD페널티)
                double dailyPct = (double)result.AvgDailyPct;
                double winRate = (double)result.WinRate / 100.0;
                double pf = Math.Min(5.0, (double)result.ProfitFactor);
                double mddPenalty = Math.Max(0.2, 1.0 - (double)result.MaxDrawdown / 100.0);
                double score = dailyPct * winRate * pf * mddPenalty;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestResult = result;
                    bestResult.ParamDesc = $"Thr={thr:F0} SL×{sl:F1} TP1×{tp1:F1} TP2×{tp2:F1}";

                    if (idx % 32 == 0 || dailyPct > 3.0)
                        onLog?.Invoke($"  [{idx}/{total}] 일{dailyPct:F2}% 승률{winRate*100:F0}% PF{pf:F2} → score={score:F4} ★ {bestResult.ParamDesc}");
                }

                if (idx % 64 == 0)
                    onLog?.Invoke($"  [{idx}/{total}] 탐색 중...");
            }

            if (bestResult == null)
            {
                onLog?.Invoke("유효한 조합을 찾지 못했습니다.");
                return new BacktestResult { InitialBalance = initialBalance, FinalBalance = initialBalance };
            }

            onLog?.Invoke($"\n최적 파라미터: {bestResult.ParamDesc}");
            onLog?.Invoke(FormatResult(bestResult));
            SaveResult(bestResult);

            return bestResult;
        }

        // ═══ 캔들 데이터 + 지표 계산 ═══
        private List<Candle> BuildCandles(List<IBinanceKline> klines)
        {
            var candles = klines.Select(k => new Candle
            {
                Time = k.OpenTime,
                Open = (double)k.OpenPrice,
                High = (double)k.HighPrice,
                Low  = (double)k.LowPrice,
                Close = (double)k.ClosePrice,
                Volume = (double)k.Volume
            }).ToList();

            // EMA 계산
            ComputeEmaArray(candles, EMA_FAST, c => c.Close, (c, v) => c.EMA9 = v);
            ComputeEmaArray(candles, EMA_SLOW, c => c.Close, (c, v) => c.EMA21 = v);
            ComputeEmaArray(candles, EMA_TREND, c => c.Close, (c, v) => c.EMA50 = v);

            // RSI
            for (int i = RSI_PERIOD + 1; i < candles.Count; i++)
            {
                double gain = 0, loss = 0;
                for (int j = i - RSI_PERIOD; j <= i; j++)
                {
                    double diff = candles[j].Close - candles[j - 1].Close;
                    if (diff > 0) gain += diff; else loss -= diff;
                }
                double rs = loss > 0 ? (gain / RSI_PERIOD) / (loss / RSI_PERIOD) : 100;
                candles[i].RSI = 100.0 - 100.0 / (1.0 + rs);
            }

            // ATR
            for (int i = 1; i < candles.Count; i++)
            {
                double tr = Math.Max(candles[i].High - candles[i].Low,
                    Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close),
                             Math.Abs(candles[i].Low - candles[i - 1].Close)));
                if (i < ATR_PERIOD + 1)
                    candles[i].ATR = tr;
                else
                    candles[i].ATR = candles[i - 1].ATR * (ATR_PERIOD - 1.0) / ATR_PERIOD + tr / ATR_PERIOD;
            }

            // BB
            for (int i = BB_PERIOD; i < candles.Count; i++)
            {
                var slice = candles.Skip(i - BB_PERIOD + 1).Take(BB_PERIOD).Select(c => c.Close).ToList();
                double mid = slice.Average();
                double std = Math.Sqrt(slice.Average(c => (c - mid) * (c - mid)));
                candles[i].BBMid = mid;
                candles[i].BBUpper = mid + BB_MULT * std;
                candles[i].BBLower = mid - BB_MULT * std;
                double w = candles[i].BBUpper - candles[i].BBLower;
                candles[i].BBPos = w > 0 ? (candles[i].Close - candles[i].BBLower) / w : 0.5;
            }

            // Volume ratio
            for (int i = VOL_PERIOD; i < candles.Count; i++)
            {
                double avg = candles.Skip(i - VOL_PERIOD).Take(VOL_PERIOD).Average(c => c.Volume);
                candles[i].VolRatio = avg > 0 ? candles[i].Volume / avg : 1.0;
            }

            // MACD (EMA9 - EMA21 기반, 이미 계산됨)
            for (int i = 1; i < candles.Count; i++)
            {
                candles[i].MACD = candles[i].EMA9 - candles[i].EMA21;
                candles[i].MACDPrev = candles[i - 1].EMA9 - candles[i - 1].EMA21;
            }

            return candles;
        }

        // ═══ 심볼별 시뮬레이션 ═══
        private List<TradeRecord> SimulateSymbol(string symbol, List<Candle> candles, decimal allocBalance)
        {
            var trades = new List<TradeRecord>();
            decimal balance = allocBalance;
            int warmup = Math.Max(EMA_TREND, BB_PERIOD) + 5;
            int cooldownUntil = 0;

            bool inPos = false;
            string dir = "";
            decimal entryPx = 0, qty = 0, remainQty = 0, margin = 0;
            decimal slPx = 0, tp1Px = 0, tp2Px = 0, trailStart = 0, trailStop = 0, trailGap = 0;
            decimal highPx = 0, lowPx = 0;
            bool beActive = false, tp1Done = false, trailActive = false;
            double entryScore = 0;
            DateTime entryTime = DateTime.MinValue;
            int entryBar = 0;

            for (int i = warmup; i < candles.Count; i++)
            {
                var c = candles[i];
                decimal cHigh  = (decimal)c.High;
                decimal cLow   = (decimal)c.Low;
                decimal cClose = (decimal)c.Close;

                if (inPos)
                {
                    if (dir == "LONG") highPx = Math.Max(highPx, cHigh);
                    else lowPx = Math.Min(lowPx, cLow);

                    // 트레일링 스탑
                    if (!trailActive)
                    {
                        if (dir == "LONG" ? highPx >= trailStart : lowPx <= trailStart)
                            trailActive = true;
                    }
                    if (trailActive)
                    {
                        if (dir == "LONG")
                            trailStop = Math.Max(trailStop, highPx - trailGap);
                        else
                            trailStop = trailStop == 0 ? lowPx + trailGap : Math.Min(trailStop, lowPx + trailGap);
                    }

                    // BE
                    if (!beActive)
                    {
                        decimal beDist = (decimal)c.ATR * (decimal)_beAtrMult;
                        if (dir == "LONG" ? cClose >= entryPx + beDist : cClose <= entryPx - beDist)
                        { beActive = true; slPx = entryPx; }
                    }

                    // TP1
                    if (!tp1Done && (dir == "LONG" ? cHigh >= tp1Px : cLow <= tp1Px))
                    {
                        decimal exitQty = qty * (decimal)_tp1ExitRatio;
                        decimal pnl = (dir == "LONG" ? tp1Px - entryPx : entryPx - tp1Px) * exitQty;
                        pnl -= tp1Px * exitQty * FEE_RATE;
                        balance += pnl;
                        remainQty = qty - exitQty;
                        tp1Done = true;
                        trades.Add(new TradeRecord(symbol, entryTime, c.Time, dir, entryPx, tp1Px,
                            margin * (decimal)_tp1ExitRatio, pnl, entryScore, "TP1", i - entryBar));
                    }

                    // TP2
                    if (dir == "LONG" ? cHigh >= tp2Px : cLow <= tp2Px)
                    {
                        decimal eq = tp1Done ? remainQty : qty;
                        decimal pnl = (dir == "LONG" ? tp2Px - entryPx : entryPx - tp2Px) * eq;
                        pnl -= tp2Px * eq * FEE_RATE;
                        balance += pnl;
                        trades.Add(new TradeRecord(symbol, entryTime, c.Time, dir, entryPx, tp2Px,
                            tp1Done ? margin * (1m - (decimal)_tp1ExitRatio) : margin, pnl, entryScore, "TP2", i - entryBar));
                        inPos = false; tp1Done = false; cooldownUntil = i + COOLDOWN_BARS; continue;
                    }

                    // Trail
                    if (trailActive && trailStop > 0 && (dir == "LONG" ? cLow <= trailStop : cHigh >= trailStop))
                    {
                        decimal eq = tp1Done ? remainQty : qty;
                        decimal pnl = (dir == "LONG" ? trailStop - entryPx : entryPx - trailStop) * eq;
                        pnl -= trailStop * eq * FEE_RATE;
                        balance += pnl;
                        trades.Add(new TradeRecord(symbol, entryTime, c.Time, dir, entryPx, trailStop,
                            tp1Done ? margin * (1m - (decimal)_tp1ExitRatio) : margin, pnl, entryScore, "TRAIL", i - entryBar));
                        inPos = false; tp1Done = false; cooldownUntil = i + COOLDOWN_BARS; continue;
                    }

                    // SL
                    if (dir == "LONG" ? cLow <= slPx : cHigh >= slPx)
                    {
                        decimal eq = tp1Done ? remainQty : qty;
                        decimal pnl = (dir == "LONG" ? slPx - entryPx : entryPx - slPx) * eq;
                        pnl -= slPx * eq * FEE_RATE;
                        balance += pnl;
                        trades.Add(new TradeRecord(symbol, entryTime, c.Time, dir, entryPx, slPx,
                            tp1Done ? margin * (1m - (decimal)_tp1ExitRatio) : margin, pnl, entryScore,
                            beActive ? "BE" : "SL", i - entryBar));
                        inPos = false; tp1Done = false; cooldownUntil = i + COOLDOWN_BARS; continue;
                    }

                    // 시간 손절: 288봉 (24시간) 이상 보유시 강제 청산
                    if (i - entryBar > 288)
                    {
                        decimal eq = tp1Done ? remainQty : qty;
                        decimal pnl = (dir == "LONG" ? cClose - entryPx : entryPx - cClose) * eq;
                        pnl -= cClose * eq * FEE_RATE;
                        balance += pnl;
                        trades.Add(new TradeRecord(symbol, entryTime, c.Time, dir, entryPx, cClose,
                            tp1Done ? margin * (1m - (decimal)_tp1ExitRatio) : margin, pnl, entryScore, "TIME", i - entryBar));
                        inPos = false; tp1Done = false; cooldownUntil = i + COOLDOWN_BARS; continue;
                    }

                    continue;
                }

                // ── 신규 진입 ──
                if (balance < 30m || i < cooldownUntil) continue;

                var signal = EvaluateEntry(candles, i);
                if (signal == null) continue;

                decimal ep = cClose;
                decimal atrD = (decimal)c.ATR;
                if (atrD <= 0) continue;

                // ATR clamp: 가격의 0.3~3%
                atrD = Math.Clamp(atrD, ep * 0.003m, ep * 0.03m);

                decimal dynSl   = atrD * (decimal)_slAtrMult;
                decimal dynTp1  = atrD * (decimal)_tp1AtrMult;
                decimal dynTp2  = atrD * (decimal)_tp2AtrMult;
                decimal dynBe   = atrD * (decimal)_beAtrMult;
                decimal dynTrail = atrD * (decimal)_trailAtrMult;
                decimal dynGap  = atrD * (decimal)_trailGapMult;

                margin = Math.Min(balance * MARGIN_PERCENT, balance * 0.95m);
                decimal notional = margin * LEVERAGE;
                qty = notional / ep;
                balance -= notional * FEE_RATE;

                dir = signal.Value.dir;
                entryPx = ep;
                remainQty = qty;
                entryScore = signal.Value.score;
                entryTime = c.Time;
                entryBar = i;
                beActive = false; tp1Done = false; trailActive = false;
                trailStop = 0; trailGap = dynGap;

                if (dir == "LONG")
                {
                    slPx = ep - dynSl; tp1Px = ep + dynTp1; tp2Px = ep + dynTp2;
                    trailStart = ep + dynTrail; highPx = ep;
                }
                else
                {
                    slPx = ep + dynSl; tp1Px = ep - dynTp1; tp2Px = ep - dynTp2;
                    trailStart = ep - dynTrail; lowPx = ep;
                }
                inPos = true;
            }

            // 미청산 강제 청산
            if (inPos && candles.Count > 0)
            {
                var last = candles[^1];
                decimal ep2 = (decimal)last.Close;
                decimal eq = tp1Done ? remainQty : qty;
                decimal pnl = (dir == "LONG" ? ep2 - entryPx : entryPx - ep2) * eq - ep2 * eq * FEE_RATE;
                balance += pnl;
                trades.Add(new TradeRecord(symbol, entryTime, last.Time, dir, entryPx, ep2,
                    tp1Done ? margin * (1m - (decimal)_tp1ExitRatio) : margin, pnl, entryScore, "END", 0));
            }

            return trades;
        }

        // ═══ 진입 평가 (5분봉 모멘텀 + 상위 TF 추세 필터) ═══
        //
        // 5분봉 기반이므로 상위 TF 추세는 EMA 정렬로 시뮬레이션:
        //   - EMA9 > EMA21 > EMA50 = 강한 상승추세 (LONG)
        //   - EMA9 < EMA21 < EMA50 = 강한 하락추세 (SHORT)
        //
        // 진입 조건 (모두 충족):
        //   1. 추세 정렬 (필수)
        //   2. 풀백 또는 모멘텀 돌파
        //   3. 볼륨 확인
        //   4. 캔들 확인
        private (string dir, double score)? EvaluateEntry(List<Candle> candles, int idx)
        {
            var c  = candles[idx];
            var c1 = candles[idx - 1]; // 이전 캔들
            var c2 = candles[idx - 2]; // 2개전

            // ── 추세 정렬 확인 (필수) ──
            bool longTrend  = c.EMA9 > c.EMA21 && c.EMA21 > c.EMA50 && c.EMA9 > candles[idx - 1].EMA9;
            bool shortTrend = c.EMA9 < c.EMA21 && c.EMA21 < c.EMA50 && c.EMA9 < candles[idx - 1].EMA9;

            if (!longTrend && !shortTrend) return null;

            if (longTrend)
            {
                double score = 0;

                // [1] 풀백 감지: 가격이 EMA21 근처로 내려왔다가 반등 (0~30)
                double distToEma21 = c.EMA21 > 0 ? (c.Close - c.EMA21) / c.EMA21 : 0;
                bool pullbackZone = distToEma21 >= -0.005 && distToEma21 <= 0.008;
                // 또는 모멘텀 돌파: 볼륨 급증 + 이전 고점 돌파
                bool momentumBreak = c.VolRatio >= 2.0 && c.Close > c1.High && c1.Close > c2.High;

                if (pullbackZone)
                {
                    score += 25;
                    // 반등 캔들 확인
                    if (c.IsBullish && c.BodyRatio >= 0.50) score += 10;
                    if (c.Close > c1.High) score += 5;  // 이전 고점 돌파
                }
                else if (momentumBreak)
                {
                    score += 20;
                    if (c.IsBullish && c.BodyRatio >= 0.60) score += 10;
                }
                else
                {
                    return null; // 풀백도 아니고 돌파도 아님 → 진입 안함
                }

                // [2] RSI 적합성 (0~15)
                if (c.RSI >= 30 && c.RSI <= 55) score += 15;      // 풀백 후 반등 구간
                else if (c.RSI > 55 && c.RSI <= 65) score += 8;   // 강세 지속
                else if (c.RSI > 65) score += 3;                    // 과매수 접근

                // [3] 볼륨 (0~15)
                if (c.VolRatio >= 2.5) score += 15;
                else if (c.VolRatio >= 1.5) score += 10;
                else if (c.VolRatio >= 1.0) score += 5;

                // [4] BB 위치 (0~10) — 하단일수록 풀백 진입에 유리
                if (c.BBPos <= 0.30) score += 10;
                else if (c.BBPos <= 0.50) score += 5;

                // [5] MACD 반전 확인 (0~10)
                if (c.MACD > c.MACDPrev) score += 5;
                if (c.MACD > 0) score += 5;

                if (score >= _entryThreshold)
                    return ("LONG", score);
            }

            if (shortTrend)
            {
                double score = 0;

                double distToEma21 = c.EMA21 > 0 ? (c.Close - c.EMA21) / c.EMA21 : 0;
                bool pullbackZone = distToEma21 >= -0.008 && distToEma21 <= 0.005;
                bool momentumBreak = c.VolRatio >= 2.0 && c.Close < c1.Low && c1.Close < c2.Low;

                if (pullbackZone)
                {
                    score += 25;
                    if (!c.IsBullish && c.BodyRatio >= 0.50) score += 10;
                    if (c.Close < c1.Low) score += 5;
                }
                else if (momentumBreak)
                {
                    score += 20;
                    if (!c.IsBullish && c.BodyRatio >= 0.60) score += 10;
                }
                else
                {
                    return null;
                }

                if (c.RSI >= 45 && c.RSI <= 70) score += 15;
                else if (c.RSI < 45 && c.RSI >= 35) score += 8;
                else if (c.RSI < 35) score += 3;

                if (c.VolRatio >= 2.5) score += 15;
                else if (c.VolRatio >= 1.5) score += 10;
                else if (c.VolRatio >= 1.0) score += 5;

                if (c.BBPos >= 0.70) score += 10;
                else if (c.BBPos >= 0.50) score += 5;

                if (c.MACD < c.MACDPrev) score += 5;
                if (c.MACD < 0) score += 5;

                if (score >= _entryThreshold)
                    return ("SHORT", score);
            }

            return null;
        }

        // ═══ 결과 집계 ═══
        private BacktestResult BuildResult(decimal initBal, List<TradeRecord> trades, DateTime start, DateTime end)
        {
            decimal balance = initBal;
            decimal peak = initBal, maxDD = 0;
            foreach (var t in trades.OrderBy(t => t.ExitTime))
            {
                balance += t.RealizedPnl;
                peak = Math.Max(peak, balance);
                if (peak > 0) maxDD = Math.Max(maxDD, (peak - balance) / peak * 100m);
            }

            var wins = trades.Where(t => t.RealizedPnl > 0).ToList();
            var losses = trades.Where(t => t.RealizedPnl <= 0).ToList();
            decimal grossProfit = wins.Sum(t => t.RealizedPnl);
            decimal grossLoss = Math.Abs(losses.Sum(t => t.RealizedPnl));

            var byDay = trades.GroupBy(t => t.ExitTime.ToString("yyyy-MM-dd"))
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));
            var byMonth = trades.GroupBy(t => t.ExitTime.ToString("yyyy-MM"))
                .ToDictionary(g => g.Key, g => g.Sum(t => t.RealizedPnl));
            var bySymbol = trades.GroupBy(t => t.Symbol)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Count(t => t.RealizedPnl > 0), g.Sum(t => t.RealizedPnl)));

            int totalDays = byDay.Count;
            decimal avgDailyPnl = totalDays > 0 ? trades.Sum(t => t.RealizedPnl) / totalDays : 0;
            decimal avgDailyPct = initBal > 0 ? avgDailyPnl / initBal * 100m : 0;

            // Sharpe
            decimal sharpe = 0;
            if (byDay.Count > 1)
            {
                var rets = byDay.Values.Select(v => (double)(v / initBal)).ToList();
                double avg = rets.Average();
                double std = Math.Sqrt(rets.Average(r => (r - avg) * (r - avg)));
                if (std > 0) sharpe = (decimal)(avg / std * Math.Sqrt(365));
            }

            return new BacktestResult
            {
                InitialBalance = initBal,
                FinalBalance = balance,
                TotalTrades = trades.Count,
                Wins = wins.Count,
                MaxDrawdown = maxDD,
                AvgWinPnl = wins.Count > 0 ? wins.Average(t => t.RealizedPnl) : 0,
                AvgLossPnl = losses.Count > 0 ? losses.Average(t => t.RealizedPnl) : 0,
                ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : 0,
                SharpeRatio = sharpe,
                AvgDailyPct = avgDailyPct,
                Trades = trades,
                ByDay = byDay,
                ByMonth = byMonth,
                BySymbol = bySymbol
            };
        }

        // ═══ 결과 출력 ═══
        public string FormatResult(BacktestResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n╔══════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║          다중 타임프레임 백테스트 결과                           ║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            if (!string.IsNullOrEmpty(r.ParamDesc))
                sb.AppendLine($"║  파라미터: {r.ParamDesc}");
            sb.AppendLine($"║  초기 잔고  : ${r.InitialBalance,10:N2}");
            sb.AppendLine($"║  최종 잔고  : ${r.FinalBalance,10:N2}");
            sb.AppendLine($"║  총 수익    : ${r.TotalPnl,+10:N2}  ({r.TotalPnlPct:+0.00;-0.00}%)");
            sb.AppendLine($"║  거래수     : {r.TotalTrades,6}건  승률: {r.WinRate:F1}% ({r.Wins}승 {r.TotalTrades - r.Wins}패)");
            sb.AppendLine($"║  평균 승리  : ${r.AvgWinPnl:+N2}  |  평균 손실: ${r.AvgLossPnl:N2}");
            sb.AppendLine($"║  프로핏팩터 : {r.ProfitFactor:F3}  |  샤프: {r.SharpeRatio:F3}");
            sb.AppendLine($"║  최대 낙폭  : {r.MaxDrawdown:F2}%");
            sb.AppendLine($"║  ★ 일평균   : {r.AvgDailyPct:+0.00;-0.00}%  (${r.InitialBalance * r.AvgDailyPct / 100:+N2;-N2}/일)");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");

            // 심볼별
            sb.AppendLine("║  [심볼별 성과]");
            foreach (var (sym, (t, w, pnl)) in r.BySymbol.OrderByDescending(kv => kv.Value.pnl))
            {
                double wr = t > 0 ? w * 100.0 / t : 0;
                sb.AppendLine($"║   {sym,-10} {t,4}건 승률{wr,5:F0}% PnL ${pnl,+9:N2}");
            }

            // 월별
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [월별 수익]");
            foreach (var (m, pnl) in r.ByMonth.OrderBy(kv => kv.Key))
            {
                decimal pct = r.InitialBalance > 0 ? pnl / r.InitialBalance * 100m : 0;
                string bar = pnl >= 0
                    ? new string('#', Math.Min(30, (int)(pnl / 20)))
                    : new string('-', Math.Min(30, (int)(-pnl / 20)));
                sb.AppendLine($"║   {m}  ${pnl,+8:N0} ({pct:+0.0;-0.0}%)  [{bar}]");
            }

            sb.AppendLine("╚══════════════════════════════════════════════════════════════════╝");
            return sb.ToString();
        }

        // ═══ 파일 저장 ═══
        public string SaveResult(BacktestResult r)
        {
            string name = $"mtf_backtest_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);

            var sb = new StringBuilder();
            sb.AppendLine(FormatResult(r));
            sb.AppendLine("\n═══ 거래 상세 ═══");
            sb.AppendLine($"{"시간",-20} {"심볼",-10} {"방향",-6} {"진입가",-14} {"청산가",-14} {"수익",-12} {"이유",-6} {"보유",-6} {"점수"}");
            sb.AppendLine(new string('-', 100));
            foreach (var t in r.Trades.OrderBy(t => t.ExitTime))
            {
                sb.AppendLine($"{t.ExitTime:MM-dd HH:mm,-20} {t.Symbol,-10} {t.Direction,-6} {t.EntryPrice,-14:F4} {t.ExitPrice,-14:F4} ${t.RealizedPnl,+10:N2} {t.ExitReason,-6} {t.HoldBars * 5,4}m {t.EntryScore:F0}");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        // ═══ 헬퍼 ═══
        private void ComputeEmaArray(List<Candle> candles, int period, Func<Candle, double> getValue, Action<Candle, double> setValue)
        {
            if (candles.Count < period) return;
            double k = 2.0 / (period + 1);
            double ema = candles.Take(period).Average(c => getValue(c));
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < period)
                    setValue(candles[i], candles.Take(i + 1).Average(c => getValue(c)));
                else
                {
                    ema = getValue(candles[i]) * k + ema * (1 - k);
                    setValue(candles[i], ema);
                }
            }
        }

        private static async Task<List<IBinanceKline>> FetchKlinesAsync(
            BinanceRestClient client, string symbol, KlineInterval interval,
            DateTime startUtc, DateTime endUtc)
        {
            var result = new List<IBinanceKline>();
            var cursor = startUtc;
            int chunk = 1500;

            while (cursor < endUtc)
            {
                var resp = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, interval, startTime: cursor, endTime: endUtc, limit: chunk);

                if (!resp.Success || resp.Data == null || !resp.Data.Any()) break;

                result.AddRange(resp.Data);
                cursor = resp.Data.Last().CloseTime.AddMilliseconds(1);
                await Task.Delay(100);
            }

            return result.OrderBy(k => k.OpenTime).ToList();
        }
    }
}
