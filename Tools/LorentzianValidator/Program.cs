using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using TradingBot.Services.LorentzianV2;

namespace LorentzianValidator;

internal sealed class SimpleKline : IBinanceKline
{
    public DateTime OpenTime { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice  { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Volume { get; set; }
    public DateTime CloseTime { get; set; }
    public decimal QuoteVolume { get; set; }
    public int TradeCount { get; set; }
    public decimal TakerBuyBaseVolume { get; set; }
    public decimal TakerBuyQuoteVolume { get; set; }
}

internal sealed class MiniLorentzianService
{
    private readonly ConcurrentDictionary<string, LorentzianAnnEngine> _engines = new();
    public int NeighborsCount { get; set; } = 8;
    public int MaxBarsBack { get; set; } = 2000;
    public int FeatureCount { get; set; } = 7;
    public LorentzianAnnEngine GetOrCreate(string s)
        => _engines.GetOrAdd(s, sym => new LorentzianAnnEngine(sym, NeighborsCount, MaxBarsBack, FeatureCount));
    public LorentzianAnnPrediction Predict(string s, List<IBinanceKline> klines)
    {
        var feat = LorentzianFeatures.Extract(klines);
        if (feat == null) return new LorentzianAnnPrediction { Symbol = s, IsReady = false, K = NeighborsCount };
        return GetOrCreate(s).Predict(feat);
    }
    public int BackfillFromCandles(string s, List<IBinanceKline> asc)
    {
        if (asc == null || asc.Count < 305) return 0;
        var engine = GetOrCreate(s);
        int added = 0;
        for (int i = 300; i < asc.Count - 4; i++)
        {
            var slice = asc.GetRange(0, i + 1);
            var feat = LorentzianFeatures.Extract(slice);
            if (feat == null) continue;
            decimal nowC = asc[i].ClosePrice;
            decimal fut = asc[i + 4].ClosePrice;
            int label = fut > nowC ? 1 : fut < nowC ? -1 : 0;
            engine.AddSample(feat, label);
            added++;
        }
        return added;
    }
}

internal static class Program
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>[v5.21.1] PUMP 전용 튜닝 — 76% WR + 1:3 TP/SL 비대칭 결함 해결</summary>
    private static async Task RunPumpTuneAsync(int pages = 18)
    {
        int days = pages * BARS_PER_REQ * 5 / (60 * 24);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  v5.21.1 PUMP 전용 TUNING ({days}일 / 30 syms)");
        Console.WriteLine("  현재 76.79% WR / -$9 → 흑자 전환 위한 TP/SL + 가드 강화 sweep");
        Console.WriteLine("================================================================");

        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                symData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch { Console.WriteLine("fail"); }
        }

        // PUMP 트리거: 1분 +1.5% + Vol 3x
        Func<List<IBinanceKline>, int, bool> pumpTrig = (kl, i) =>
            i >= 20 && PriceChange(kl, i, 1) >= 1.5 && VolMult(kl, i, 20) >= 3.0;

        // PUMP 전용 가드 7종 — 펌프코인 특성 반영
        var gateSets = new (string label, Func<List<IBinanceKline>, int, bool> ok)[]
        {
            ("baseline (v5.20.8)",          (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 70),
            ("RSI<65",                       (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 65),
            ("RSI<60",                       (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 60),
            ("RSI<55",                       (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 55),
            ("RSI<60 + Vol 5x",              (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 60 && VolMult(kl, i, 20) >= 5.0),
            ("RSI<60 + EMA5>EMA20",         (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 60 && Ema5GtEma20(kl, i)),
            ("RSI<60 + ATR 0.5-1.5%",       (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 60 && CalcAtrPct(kl, i) >= 0.5 && CalcAtrPct(kl, i) <= 1.5),
            ("RSI<55 + EMA5>EMA20 + Vol 5x", (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 55 && Ema5GtEma20(kl, i) && VolMult(kl, i, 20) >= 5.0),
        };

        // PUMP 코인 특성에 맞는 TP/SL — 펌프는 빠르게 익절, SL은 짧게
        var tpslSets = new (string label, decimal tp, decimal sl, int win)[]
        {
            ("Current 1.0/3.0/24 (BE 75%)",  1.0m, 3.0m, 24),
            ("PUMP 2.0/0.7/12 (BE 25.9%)",   2.0m, 0.7m, 12),  // 펌프 따라가기
            ("PUMP 1.5/0.7/12 (BE 31.8%)",   1.5m, 0.7m, 12),  // 빠른 익절
            ("PUMP 1.5/1.0/12 (BE 40%)",     1.5m, 1.0m, 12),  // 1.5:1
            ("PUMP 2.0/1.0/12 (BE 33.3%)",   2.0m, 1.0m, 12),
            ("PUMP 3.0/1.0/24 (BE 25%)",     3.0m, 1.0m, 24),  // 큰 익절
            ("PUMP 1.0/0.5/6 (BE 33.3%)",    1.0m, 0.5m, 6),   // 초고속 스캘핑
            ("PUMP 0.7/0.5/6 (BE 41.7%)",    0.7m, 0.5m, 6),
            ("PUMP 2.0/1.5/24 (BE 42.9%)",   2.0m, 1.5m, 24),
        };

        Console.WriteLine();
        Console.WriteLine($"{"Gate",-36} | {"TP/SL/WIN",-32} | {"Trades",7} {"WR%",7} {"PnL$",10} {"avg",7}  Status");
        Console.WriteLine(new string('-', 130));

        var results = new List<(string g, string t, int n, double wr, decimal pnl, decimal avg)>();
        foreach (var gs in gateSets)
        {
            foreach (var ts in tpslSets)
            {
                decimal tpUsd = Notional * ts.tp / 100m - RoundTripFee;
                decimal slUsd = Notional * ts.sl / 100m + RoundTripFee;

                int n = 0, w = 0; decimal pnl = 0m;
                foreach (var kv in symData)
                {
                    var kl = kv.Value;
                    int trainEnd = (int)(kl.Count * 0.7);
                    for (int i = trainEnd + 50; i < kl.Count - ts.win; i++)
                    {
                        if (!pumpTrig(kl, i)) continue;
                        if (!gs.ok(kl, i)) continue;
                        var (tp, sl) = OutcomeIn(kl, i, ts.tp, ts.sl, ts.win);
                        if (!(tp || sl)) continue;
                        n++;
                        if (tp) { w++; pnl += tpUsd; } else pnl -= slUsd;
                    }
                }
                double wr = n > 0 ? w * 100.0 / n : 0;
                decimal avg = n > 0 ? pnl / n : 0m;
                string status = pnl > 0 ? "✅ 흑자" : "";
                Console.WriteLine($"{gs.label,-36} | {ts.label,-32} | {n,7} {wr,6:F2} {pnl,9:F2} {avg,7:F2}  {status}");
                results.Add((gs.label, ts.label, n, wr, pnl, avg));
            }
            Console.WriteLine();
        }

        Console.WriteLine("=== PUMP 흑자 조합 TOP 10 (PnL DESC, n>=30) ===");
        foreach (var r in results.Where(r => r.pnl > 0 && r.n >= 30).OrderByDescending(r => r.pnl).Take(10))
            Console.WriteLine($"  ✅ n={r.n,5}  WR={r.wr,6:F2}%  PnL=${r.pnl,8:F2}  avg=${r.avg,5:F2} | {r.g} | {r.t}");

        Console.WriteLine();
        Console.WriteLine("=== PUMP 평균 PnL/trade TOP 10 (효율성, n>=30) ===");
        foreach (var r in results.Where(r => r.n >= 30).OrderByDescending(r => r.avg).Take(10))
            Console.WriteLine($"  n={r.n,5}  WR={r.wr,6:F2}%  PnL=${r.pnl,8:F2}  avg=${r.avg,5:F2} | {r.g} | {r.t}");
    }

    /// <summary>[v5.21.0] 봇 진입 로직별 30일 백테스트 — 각 트리거 시뮬 + 가드 적용 비교</summary>
    private static async Task RunLogicBreakdownAsync(int pages = 6)
    {
        int days = pages * BARS_PER_REQ * 5 / (60 * 24);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  v5.21.0 LOGIC BREAKDOWN BACKTEST ({days}일 / 30 syms)");
        Console.WriteLine("  봇 5종 진입 트리거 시뮬: PUMP / SPIKE / MAJOR / SQUEEZE / BB_WALK");
        Console.WriteLine("  3가지 가드 시나리오로 비교: NONE / v5.20.7 (기존) / v5.20.8 (재설계)");
        Console.WriteLine("================================================================");

        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                symData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch { Console.WriteLine("fail"); }
        }
        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };

        // 봇 5종 진입 트리거 시뮬레이터
        var triggers = new (string name, Func<List<IBinanceKline>, int, string, bool> ok)[]
        {
            // PUMP: 1분 +1.5% AND 거래량 3x avg(20)
            ("PUMP", (kl, i, sym) => i >= 20 && PriceChange(kl, i, 1) >= 1.5 && VolMult(kl, i, 20) >= 3.0),
            // SPIKE (TICK_SURGE): 5분 +2.0% AND 거래량 5x avg(20)
            ("SPIKE", (kl, i, sym) => i >= 20 && PriceChange(kl, i, 5) >= 2.0 && VolMult(kl, i, 20) >= 5.0),
            // MAJOR: BTC/ETH/SOL/XRP만, EMA20 추세, M15 30봉 위치 60-85%
            ("MAJOR", (kl, i, sym) => majors.Contains(sym) && i >= 30 && Ema20Rising(kl, i)
                       && M15RangePos(kl, i, 30) is >= 60 and <= 85),
            // SQUEEZE_BREAKOUT: BB width < 1.5% (조임) AND 종가 > BB upper (돌파)
            ("SQUEEZE", (kl, i, sym) => i >= 20 && BBWidth(kl, i) < 1.5 && BBWalkUpper(kl, i)),
            // BB_WALK: 직전 5봉 중 4봉 이상 종가 > BB upper
            ("BB_WALK", (kl, i, sym) => i >= 20 && BBWalkStreak(kl, i, 5) >= 4),
        };

        // 가드 시나리오
        var scenarios = new (string name, Func<List<IBinanceKline>, int, string, bool> guard)[]
        {
            // NONE: 트리거 그대로 진입
            ("NONE", (kl, i, sym) => true),
            // v5.20.7 (기존 게이트 묶음): MAJOR 외 RSI<30 차단 + Lorentzian Pred>3 (생략 가능, 효과 미미) + EMA20↑ + Vol>1.3x
            ("v5.20.7", (kl, i, sym) => {
                if (!majors.Contains(sym) && CalcRsi14(kl, i) < 30) return false;
                if (!Ema20Rising(kl, i)) return false;
                if (!VolSurge(kl, i, 1.3)) return false;
                return true;
            }),
            // v5.20.8 (재설계): EMA20↑ + RSI<70
            ("v5.20.8", (kl, i, sym) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 70),
            // v5.21.1 (PUMP 강화): EMA20↑ + RSI<65
            ("v5.21.1", (kl, i, sym) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 65),
        };

        // TP/SL 시나리오
        var tpslSets = new (string label, decimal tp, decimal sl, int win)[]
        {
            ("Bot기본 1.5/0.7/12",  1.5m, 0.7m, 12),  // 현재 봇 설정
            ("권장 1.0/3.0/24",     1.0m, 3.0m, 24),  // 87% WR target
            ("타이트 0.5/1.5/12",   0.5m, 1.5m, 12),
        };

        Console.WriteLine();
        Console.WriteLine($"{"Trigger",-9} {"Guard",-10} {"TP/SL/WIN",-22} | {"Trades",7} {"WR%",7} {"PnL$",10} {"avg",7}");
        Console.WriteLine(new string('-', 100));

        var results = new List<(string trig, string guard, string ts, int n, double wr, decimal pnl, decimal avg)>();

        foreach (var trig in triggers)
        {
            // [v5.21.3] 카테고리별 marg 적용
            decimal trigNotional = NotionalFor(trig.name);
            decimal trigFee = trigNotional * FEE_RATE * 2m;
            foreach (var sc in scenarios)
            {
                foreach (var ts in tpslSets)
                {
                    decimal tpUsd = trigNotional * ts.tp / 100m - trigFee;
                    decimal slUsd = trigNotional * ts.sl / 100m + trigFee;

                    int n = 0, w = 0; decimal pnl = 0m;
                    foreach (var kv in symData)
                    {
                        var kl = kv.Value; var sym = kv.Key;
                        int trainEnd = (int)(kl.Count * 0.7);
                        for (int i = trainEnd + 50; i < kl.Count - ts.win; i++)
                        {
                            if (!trig.ok(kl, i, sym)) continue;
                            if (!sc.guard(kl, i, sym)) continue;
                            var (tp, sl) = OutcomeIn(kl, i, ts.tp, ts.sl, ts.win);
                            if (!(tp || sl)) continue;
                            n++;
                            if (tp) { w++; pnl += tpUsd; } else pnl -= slUsd;
                        }
                    }
                    double wr = n > 0 ? w * 100.0 / n : 0;
                    decimal avg = n > 0 ? pnl / n : 0m;
                    Console.WriteLine($"{trig.name,-9} {sc.name,-10} {ts.label,-22} | {n,7} {wr,6:F2} {pnl,9:F2} {avg,7:F2}");
                    results.Add((trig.name, sc.name, ts.label, n, wr, pnl, avg));
                }
            }
            Console.WriteLine();
        }

        // 트리거별 BEST 조합
        Console.WriteLine("=== 트리거별 BEST PnL 조합 ===");
        foreach (var trigGroup in results.GroupBy(r => r.trig))
        {
            var best = trigGroup.OrderByDescending(r => r.pnl).First();
            string tag = best.pnl > 0 ? "✅" : "❌";
            Console.WriteLine($"  {tag} {best.trig,-9} | {best.guard,-10} | {best.ts,-22} | n={best.n}, WR={best.wr:F2}%, PnL=${best.pnl:F2}, avg=${best.avg:F2}");
        }

        // SCENARIO 비교 — 같은 트리거에서 가드 효과
        Console.WriteLine();
        Console.WriteLine("=== 가드 효과 비교 (TP/SL 동일, NONE vs v5.20.7 vs v5.20.8) ===");
        foreach (var trig in triggers)
        {
            foreach (var ts in tpslSets)
            {
                Console.WriteLine($"  [{trig.name} / {ts.label}]");
                foreach (var sc in scenarios)
                {
                    var r = results.First(x => x.trig == trig.name && x.guard == sc.name && x.ts == ts.label);
                    Console.WriteLine($"     {sc.name,-10} n={r.n,5}  WR={r.wr,6:F2}%  PnL=${r.pnl,9:F2}  avg=${r.avg,6:F2}");
                }
            }
        }

        // 추천: 트리거별 흑자 보장 가드+TP/SL
        Console.WriteLine();
        Console.WriteLine("=== 흑자 가능 조합만 (PnL > 0, n >= 30) ===");
        foreach (var r in results.Where(r => r.pnl > 0 && r.n >= 30).OrderByDescending(r => r.avg))
            Console.WriteLine($"  ✅ {r.trig,-9} | {r.guard,-10} | {r.ts,-22} | n={r.n}, WR={r.wr:F2}%, PnL=${r.pnl:F2}, avg=${r.avg:F2}/trade");

        // 손실 강한 트리거 — 차단 권고
        Console.WriteLine();
        Console.WriteLine("=== 손실 큰 조합 TOP 10 — 봇에서 차단 권고 ===");
        foreach (var r in results.Where(r => r.n >= 30).OrderBy(r => r.pnl).Take(10))
            Console.WriteLine($"  ❌ {r.trig,-9} | {r.guard,-10} | {r.ts,-22} | n={r.n}, WR={r.wr:F2}%, PnL=${r.pnl:F2}");
    }
    private static double PriceChange(List<IBinanceKline> kl, int i, int barsAgo)
    {
        if (i < barsAgo) return 0;
        decimal prev = kl[i - barsAgo].ClosePrice;
        decimal cur = kl[i].ClosePrice;
        return prev > 0m ? (double)((cur - prev) / prev * 100m) : 0;
    }
    private static double VolMult(List<IBinanceKline> kl, int i, int avgPeriod)
    {
        if (i < avgPeriod) return 0;
        double cur = (double)kl[i].Volume;
        double sum = 0;
        for (int j = i - avgPeriod; j < i; j++) sum += (double)kl[j].Volume;
        double avg = sum / avgPeriod;
        return avg < 1e-9 ? 0 : cur / avg;
    }
    private static double M15RangePos(List<IBinanceKline> kl, int i, int bars)
    {
        // 5m × 3 = 15m approximation, look at last `bars*3` 5m bars
        int win = bars * 3;
        if (i < win) return 50;
        decimal hi = decimal.MinValue, lo = decimal.MaxValue;
        for (int j = i - win + 1; j <= i; j++)
        {
            if (kl[j].HighPrice > hi) hi = kl[j].HighPrice;
            if (kl[j].LowPrice < lo) lo = kl[j].LowPrice;
        }
        decimal cur = kl[i].ClosePrice;
        return hi > lo ? (double)((cur - lo) / (hi - lo) * 100m) : 50;
    }
    private static double BBWidth(List<IBinanceKline> kl, int i)
    {
        if (i < 20) return 0;
        double sum = 0;
        for (int j = i - 19; j <= i; j++) sum += (double)kl[j].ClosePrice;
        double mean = sum / 20;
        double sq = 0;
        for (int j = i - 19; j <= i; j++) { double d = (double)kl[j].ClosePrice - mean; sq += d * d; }
        double sd = Math.Sqrt(sq / 20);
        return mean > 0 ? (sd * 4) / mean * 100 : 0;
    }
    private static int BBWalkStreak(List<IBinanceKline> kl, int i, int lookback)
    {
        if (i < 20) return 0;
        int cnt = 0;
        for (int q = i - lookback + 1; q <= i; q++)
        {
            if (q < 20) continue;
            double sum = 0;
            for (int j = q - 19; j <= q; j++) sum += (double)kl[j].ClosePrice;
            double mean = sum / 20;
            double sq = 0;
            for (int j = q - 19; j <= q; j++) { double d = (double)kl[j].ClosePrice - mean; sq += d * d; }
            double sd = Math.Sqrt(sq / 20);
            double upper = mean + 2 * sd;
            if ((double)kl[q].ClosePrice >= upper) cnt++;
        }
        return cnt;
    }

    /// <summary>[v5.20.9] 승률 70% 목표 — 작은 TP + 넓은 SL + 다중 필터 sweep</summary>
    private static async Task RunTarget70Async(int pages = PAGES)
    {
        int days = pages * BARS_PER_REQ * 5 / (60 * 24);
        Console.WriteLine("=================================================================");
        Console.WriteLine($"  v5.20.9 TARGET 70%+ WIN-RATE BACKTEST (chart 30 syms × {days} days)");
        Console.WriteLine("  전략: 작은 TP (쉽게 도달) + 넓은 SL (드물게 맞음) + 강한 필터");
        Console.WriteLine("=================================================================");

        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                symData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch { Console.WriteLine("fail"); }
        }

        // 70% WR 가능성 높은 TP/SL 조합 (작은 TP + 큰 SL)
        var tpslSets = new (string label, decimal tp, decimal sl, int win)[]
        {
            ("TP0.3/SL2.0/WIN24",  0.3m, 2.0m, 24),
            ("TP0.5/SL2.0/WIN24",  0.5m, 2.0m, 24),
            ("TP0.5/SL3.0/WIN24",  0.5m, 3.0m, 24),
            ("TP0.7/SL2.0/WIN24",  0.7m, 2.0m, 24),
            ("TP0.7/SL3.0/WIN24",  0.7m, 3.0m, 24),
            ("TP1.0/SL3.0/WIN24",  1.0m, 3.0m, 24),
            ("TP0.5/SL2.0/WIN48",  0.5m, 2.0m, 48),
            ("TP0.7/SL3.0/WIN48",  0.7m, 3.0m, 48),
            ("TP0.3/SL2.0/WIN12",  0.3m, 2.0m, 12),
            ("TP0.5/SL1.5/WIN12",  0.5m, 1.5m, 12),
        };

        // 강한 필터 조합 — 진단 양성 (EMA20↑, RSI<70, ATR sweet spot) + 추가 강력 필터
        var gateSets = new (string label, Func<List<IBinanceKline>, int, bool> ok)[]
        {
            ("none", (kl, i) => true),
            ("EMA20↑",
                (kl, i) => Ema20Rising(kl, i)),
            ("EMA20↑ + RSI<70",
                (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 70),
            ("EMA20↑ + RSI 30-65",
                (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) >= 30 && CalcRsi14(kl, i) < 65),
            ("EMA20↑ + RSI 40-60",
                (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) >= 40 && CalcRsi14(kl, i) < 60),
            ("EMA20↑ + RSI<70 + ATR 0.5-1.5%",
                (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 70 && CalcAtrPct(kl, i) >= 0.5 && CalcAtrPct(kl, i) <= 1.5),
            ("EMA5>EMA20 + RSI<70 + ATR 0.5-1.5%",
                (kl, i) => Ema5GtEma20(kl, i) && CalcRsi14(kl, i) < 70 && CalcAtrPct(kl, i) >= 0.5 && CalcAtrPct(kl, i) <= 1.5),
            ("EMA5>EMA20 + RSI 40-60 + ATR 0.5-1.5%",
                (kl, i) => Ema5GtEma20(kl, i) && CalcRsi14(kl, i) >= 40 && CalcRsi14(kl, i) < 60 && CalcAtrPct(kl, i) >= 0.5 && CalcAtrPct(kl, i) <= 1.5),
            ("ULTRA: EMA5>20 + RSI 45-58 + ATR 0.6-1.2 + Vol normal",
                (kl, i) => Ema5GtEma20(kl, i) && CalcRsi14(kl, i) >= 45 && CalcRsi14(kl, i) <= 58 && CalcAtrPct(kl, i) >= 0.6 && CalcAtrPct(kl, i) <= 1.2 && IsVolNormal(kl, i, 0.7, 1.5)),
            ("Pullback: RSI 35-50 + EMA20↑ + ATR 0.5-1.5",
                (kl, i) => CalcRsi14(kl, i) >= 35 && CalcRsi14(kl, i) <= 50 && Ema20Rising(kl, i) && CalcAtrPct(kl, i) >= 0.5 && CalcAtrPct(kl, i) <= 1.5),
        };

        Console.WriteLine();
        Console.WriteLine($"{"Gate",-50} | {"TP/SL/WIN",-22} | {"BE%",6} {"Trades",7} {"WR%",7} {"PnL$",10} {"avg",7}  Status");
        Console.WriteLine(new string('-', 140));

        var hits70 = new List<(string g, string t, int n, double wr, decimal pnl, decimal avg)>();

        foreach (var gs in gateSets)
        {
            foreach (var ts in tpslSets)
            {
                decimal tpUsd = Notional * ts.tp / 100m - RoundTripFee;
                decimal slUsd = Notional * ts.sl / 100m + RoundTripFee;
                decimal beWR = ts.sl / (ts.tp + ts.sl) * 100m;

                int n = 0, w = 0; decimal pnl = 0m;
                foreach (var kv in symData)
                {
                    var kl = kv.Value;
                    int trainEnd = (int)(kl.Count * 0.7);
                    for (int i = trainEnd + 50; i < kl.Count - ts.win; i++)
                    {
                        if (!gs.ok(kl, i)) continue;
                        var (tp, sl) = OutcomeIn(kl, i, ts.tp, ts.sl, ts.win);
                        if (!(tp || sl)) continue;
                        n++;
                        if (tp) { w++; pnl += tpUsd; } else pnl -= slUsd;
                    }
                }
                double wr = n > 0 ? w * 100.0 / n : 0;
                decimal avg = n > 0 ? pnl / n : 0m;
                string status = wr >= 70.0 ? (pnl > 0 ? "✅ 70%+ 흑자" : "⚠️ 70%+ 적자") : (pnl > 0 ? "흑자" : "");
                Console.WriteLine($"{gs.label,-50} | {ts.label,-22} | {beWR,5:F1}% {n,7} {wr,6:F2}% {pnl,9:F2} {avg,7:F2}  {status}");
                if (wr >= 70.0) hits70.Add((gs.label, ts.label, n, wr, pnl, avg));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 승률 70%+ 달성 조합 (PnL 양수만) ===");
        var winners = hits70.Where(h => h.pnl > 0 && h.n >= 30).OrderByDescending(h => h.pnl).ToList();
        if (winners.Count == 0)
        {
            Console.WriteLine("  ❌ 70%+ 흑자 조합 없음. BE 임계가 높아 적자 우세.");
            Console.WriteLine();
            Console.WriteLine("  최고 WR 조합 (PnL 음수 포함):");
            foreach (var h in hits70.Where(h => h.n >= 30).OrderByDescending(h => h.wr).Take(5))
                Console.WriteLine($"    WR={h.wr:F2}% n={h.n} PnL=${h.pnl:F2} | {h.g} | {h.t}");
        }
        else
        {
            foreach (var h in winners.Take(10))
                Console.WriteLine($"  ✅ WR={h.wr:F2}% n={h.n} PnL=${h.pnl:F2} avg=${h.avg:F2} | {h.g} | {h.t}");
        }
    }
    private static bool Ema5GtEma20(List<IBinanceKline> kl, int i)
    {
        if (i < 25) return false;
        decimal e5 = CalcEmaN(kl, i, 5);
        decimal e20 = CalcEmaN(kl, i, 20);
        return e5 > e20;
    }
    private static decimal CalcEmaN(List<IBinanceKline> kl, int idx, int period)
    {
        decimal k = 2m / (period + 1);
        int from = Math.Max(0, idx - period * 3);
        decimal e = kl[from].ClosePrice;
        for (int j = from + 1; j <= idx; j++) e = kl[j].ClosePrice * k + e * (1 - k);
        return e;
    }
    private static bool IsVolNormal(List<IBinanceKline> kl, int i, double low, double high)
    {
        if (i < 20) return false;
        double cur = (double)kl[i].Volume;
        double sum = 0;
        for (int j = i - 20; j < i; j++) sum += (double)kl[j].Volume;
        double avg = sum / 20.0;
        if (avg < 1e-9) return false;
        double r = cur / avg;
        return r >= low && r <= high;
    }

    /// <summary>[v5.20.8 REDESIGN] 진단 결과 기반 새 전략 검증 — Lorentzian 제거, EMA+RSI<70+ATR sweet spot</summary>
    private static async Task RunRedesignAsync()
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("  v5.20.8 REDESIGN BACKTEST (chart, 30 syms × 14 days)");
        Console.WriteLine("  진단 기반: Lorentzian 제거 / EMA20↑ + RSI<70 + ATR 0.7~1.5%");
        Console.WriteLine("=================================================================");

        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                symData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 4개 가드 조합 + 4개 TP/SL × WIN combo 검증
        var gateSets = new (string label, Func<List<IBinanceKline>, int, bool> ok)[]
        {
            ("none",                               (kl, i) => true),
            ("EMA20↑",                            (kl, i) => Ema20Rising(kl, i)),
            ("EMA20↑ + RSI<70",                  (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 70),
            ("EMA20↑ + RSI<70 + ATR 0.7-1.5%",   (kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) < 70 && CalcAtrPct(kl, i) >= 0.7 && CalcAtrPct(kl, i) <= 1.5),
            ("EMA20↑ + RSI 30-70 + ATR 0.7-1.5%",(kl, i) => Ema20Rising(kl, i) && CalcRsi14(kl, i) >= 30 && CalcRsi14(kl, i) < 70 && CalcAtrPct(kl, i) >= 0.7 && CalcAtrPct(kl, i) <= 1.5),
            ("RSI<70 단독",                       (kl, i) => CalcRsi14(kl, i) < 70),
            ("ATR 0.7-1.5% 단독",                (kl, i) => CalcAtrPct(kl, i) >= 0.7 && CalcAtrPct(kl, i) <= 1.5),
        };
        var tpslSets = new (string label, decimal tp, decimal sl, int win)[]
        {
            ("TP1.5/SL1.0/WIN24",  1.5m, 1.0m, 24),
            ("TP1.5/SL1.5/WIN24",  1.5m, 1.5m, 24),  // SL 더 넓게 — SL 1.4봉 빨리 맞는 문제 해결
            ("TP2.0/SL1.5/WIN24",  2.0m, 1.5m, 24),  // 2:1.5 = 손익비 1.33
            ("TP1.0/SL1.0/WIN24",  1.0m, 1.0m, 24),  // 1:1 단순
            ("TP1.5/SL1.0/WIN48",  1.5m, 1.0m, 48),  // 윈도우 더 넓게 (TP가 평균 8봉 도달)
        };

        Console.WriteLine();
        Console.WriteLine("Gate Combo                              | TP/SL/WIN              | Trades  Wins  WR%   PnL$       AvgPnL$  ROI%");
        Console.WriteLine(new string('-', 130));

        var results = new List<(string gate, string tp, int n, double wr, decimal pnl, decimal roi, decimal avg)>();

        foreach (var gs in gateSets)
        {
            foreach (var ts in tpslSets)
            {
                decimal tpUsd = Notional * ts.tp / 100m - RoundTripFee;
                decimal slUsd = Notional * ts.sl / 100m + RoundTripFee;
                decimal beWR = ts.sl / (ts.tp + ts.sl) * 100m;

                int n = 0, w = 0; decimal pnl = 0m;
                foreach (var kv in symData)
                {
                    var kl = kv.Value;
                    int trainEnd = (int)(kl.Count * 0.7);
                    for (int i = trainEnd + 50; i < kl.Count - ts.win; i++)
                    {
                        if (!gs.ok(kl, i)) continue;
                        var (tp, sl) = OutcomeIn(kl, i, ts.tp, ts.sl, ts.win);
                        if (!(tp || sl)) continue;
                        n++;
                        if (tp) { w++; pnl += tpUsd; } else pnl -= slUsd;
                    }
                }
                double wr = n > 0 ? w * 100.0 / n : 0;
                decimal avg = n > 0 ? pnl / n : 0m;
                decimal roi = pnl / 1000m * 100m;  // 초기자본 $1000
                Console.WriteLine($"{gs.label,-38} | {ts.label,-22} | {n,6}  {w,4}  {wr,5:F2} {pnl,9:F2}  {avg,7:F2}  {roi,6:F2}%");
                results.Add((gs.label, ts.label, n, wr, pnl, roi, avg));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== TOP 5 흑자 조합 (PnL 양수만) ===");
        var profitable = results.Where(r => r.pnl > 0).OrderByDescending(r => r.pnl).Take(5).ToList();
        if (profitable.Count == 0)
            Console.WriteLine("  ❌ 모든 조합 손실. 더 근본적인 재설계 필요 (다른 지표/엔진).");
        else
            foreach (var r in profitable)
                Console.WriteLine($"  ✅ {r.gate,-40} | {r.tp,-22} | n={r.n}, WR={r.wr:F2}%, PnL=${r.pnl:F2} ({r.roi:F2}%)");

        Console.WriteLine();
        Console.WriteLine("=== TOP 5 손실 조합 ===");
        foreach (var r in results.OrderBy(r => r.pnl).Take(5))
            Console.WriteLine($"  ❌ {r.gate,-40} | {r.tp,-22} | n={r.n}, WR={r.wr:F2}%, PnL=${r.pnl:F2}");

        Console.WriteLine();
        Console.WriteLine("=== AVG PnL/거래 TOP 5 (효율성) ===");
        foreach (var r in results.Where(r => r.n >= 50).OrderByDescending(r => r.avg).Take(5))
            Console.WriteLine($"  {r.gate,-40} | {r.tp,-22} | n={r.n}, avg=${r.avg:F2}/trade, WR={r.wr:F2}%");
    }

    /// <summary>[v5.20.7 DIAG] 로직 원인 분석 — 임계값 만으로 안 풀림. 왜 지는지 추적.</summary>
    private static async Task RunDiagnosisAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.20.7 LOGIC DIAGNOSIS — 왜 가드를 강화해도 손실인가?");
        Console.WriteLine("================================================================");

        var svc = new MiniLorentzianService();
        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                int trainEnd = (int)(kl.Count * 0.7);
                int added = svc.BackfillFromCandles(sym, kl.GetRange(0, trainEnd));
                symData[sym] = kl;
                Console.WriteLine($"trained={added}");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        decimal tpUsd = Notional * 2.0m / 100m - RoundTripFee;
        decimal slUsd = Notional * 1.0m / 100m + RoundTripFee;

        // === A. Lorentzian Prediction 분포 + per-bucket 승률 ===
        Console.WriteLine();
        Console.WriteLine("=== A. Lorentzian Prediction 분포 vs TP-first 승률 ===");
        Console.WriteLine("    예측 강도가 실제로 의미 있는지 측정");
        Console.WriteLine();
        var bucketStats = new Dictionary<int, (int n, int tpHits, int slHits)>();
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value;
            int trainEnd = (int)(kl.Count * 0.7);
            for (int i = trainEnd + 50; i < kl.Count - 24; i++)
            {
                var slice = kl.GetRange(0, i + 1);
                var pred = svc.Predict(sym, slice);
                if (!pred.IsReady) continue;
                int b = pred.Prediction;
                var (tp, sl) = OutcomeIn(kl, i, 2.0m, 1.0m, 24);
                if (!(tp || sl)) continue;
                if (!bucketStats.ContainsKey(b)) bucketStats[b] = (0, 0, 0);
                var s = bucketStats[b];
                s.n++; if (tp) s.tpHits++; else s.slHits++;
                bucketStats[b] = s;
            }
        }
        Console.WriteLine("  Pred  Trades  TP    SL    WinRate  Edge(BE 33.33%)");
        foreach (var kv in bucketStats.OrderBy(kv => kv.Key))
        {
            var s = kv.Value;
            double wr = s.n > 0 ? s.tpHits * 100.0 / s.n : 0;
            decimal pnl = s.tpHits * tpUsd - s.slHits * slUsd;
            Console.WriteLine($"  {kv.Key,4}  {s.n,6}  {s.tpHits,4}  {s.slHits,4}  {wr,6:F2}%  {(wr - 33.33),+6:F2}%p   PnL ${pnl,8:F2}");
        }

        // === B. Hit time 분포 — TP가 빨리 오나 SL이 빨리 오나? ===
        Console.WriteLine();
        Console.WriteLine("=== B. TP vs SL hit time 분포 ===");
        Console.WriteLine("    SL이 평균 더 일찍 맞으면 시장이 LONG에 비호의적");
        var tpTimes = new List<int>();
        var slTimes = new List<int>();
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value;
            int trainEnd = (int)(kl.Count * 0.7);
            for (int i = trainEnd + 50; i < kl.Count - 24; i++)
            {
                if (!Ema20Rising(kl, i)) continue;
                if (!VolSurge(kl, i, 1.3)) continue;
                var slice = kl.GetRange(0, i + 1);
                var pred = svc.Predict(sym, slice);
                if (!pred.IsReady || pred.Prediction <= 3) continue;

                decimal entry = kl[i].ClosePrice;
                decimal tpPx = entry * 1.020m, slPx = entry * 0.990m;
                for (int k = 1; k <= 24 && i + k < kl.Count; k++)
                {
                    var b = kl[i + k];
                    if (b.HighPrice >= tpPx && b.LowPrice <= slPx) { slTimes.Add(k); break; }
                    if (b.HighPrice >= tpPx) { tpTimes.Add(k); break; }
                    if (b.LowPrice <= slPx)  { slTimes.Add(k); break; }
                }
            }
        }
        if (tpTimes.Count > 0 && slTimes.Count > 0)
        {
            Console.WriteLine($"  TP hit: count={tpTimes.Count}, 평균 {tpTimes.Average():F1}봉, 중앙값 {Median(tpTimes)}봉, p25={Percentile(tpTimes,25)} p75={Percentile(tpTimes,75)}");
            Console.WriteLine($"  SL hit: count={slTimes.Count}, 평균 {slTimes.Average():F1}봉, 중앙값 {Median(slTimes)}봉, p25={Percentile(slTimes,25)} p75={Percentile(slTimes,75)}");
            Console.WriteLine($"  → SL이 평균 {slTimes.Average() - tpTimes.Average():+0.0;-0.0}봉 더 {(slTimes.Average() < tpTimes.Average() ? "빠르게" : "느리게")} 맞음");
        }

        // === C. 진입 시점 RSI 구간별 승률 — 늦은 진입(고RSI) vs 이른 진입 ===
        Console.WriteLine();
        Console.WriteLine("=== C. 진입 시점 RSI(14) 구간별 승률 ===");
        Console.WriteLine("    RSI 70+ = 과열, 50-70 = 상승 중, 30-50 = 약세, <30 = 떨어짐");
        var rsiBuckets = new Dictionary<string, (int n, int w)>
        {
            { "RSI<30",   (0, 0) }, { "RSI 30-50", (0, 0) },
            { "RSI 50-70", (0, 0) }, { "RSI 70+",   (0, 0) },
        };
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value;
            int trainEnd = (int)(kl.Count * 0.7);
            for (int i = trainEnd + 50; i < kl.Count - 24; i++)
            {
                if (!Ema20Rising(kl, i) || !VolSurge(kl, i, 1.3)) continue;
                var slice = kl.GetRange(0, i + 1);
                var pred = svc.Predict(sym, slice);
                if (!pred.IsReady || pred.Prediction <= 3) continue;
                double rsi = CalcRsi14(kl, i);
                var (tp, sl) = OutcomeIn(kl, i, 2.0m, 1.0m, 24);
                if (!(tp || sl)) continue;
                string bk = rsi < 30 ? "RSI<30" : rsi < 50 ? "RSI 30-50" : rsi < 70 ? "RSI 50-70" : "RSI 70+";
                var s = rsiBuckets[bk]; s.n++; if (tp) s.w++; rsiBuckets[bk] = s;
            }
        }
        Console.WriteLine("  Bucket       Trades  Wins  WinRate  Edge");
        foreach (var kv in rsiBuckets)
        {
            double wr = kv.Value.n > 0 ? kv.Value.w * 100.0 / kv.Value.n : 0;
            Console.WriteLine($"  {kv.Key,-10}  {kv.Value.n,6}  {kv.Value.w,4}  {wr,6:F2}%  {(wr - 33.33),+6:F2}%p");
        }

        // === D. ATR-based 변동성 구간별 승률 ===
        Console.WriteLine();
        Console.WriteLine("=== D. ATR/Close 변동성 구간별 승률 ===");
        Console.WriteLine("    저변동성(<0.3%)은 TP 도달 어렵, 고변동성(>3%)은 SL 빠르게");
        var atrBuckets = new Dictionary<string, (int n, int w)>
        {
            { "<0.3%",      (0, 0) }, { "0.3-0.7%", (0, 0) },
            { "0.7-1.5%",   (0, 0) }, { "1.5-3.0%", (0, 0) },
            { ">3.0%",      (0, 0) },
        };
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value;
            int trainEnd = (int)(kl.Count * 0.7);
            for (int i = trainEnd + 50; i < kl.Count - 24; i++)
            {
                if (!Ema20Rising(kl, i) || !VolSurge(kl, i, 1.3)) continue;
                var slice = kl.GetRange(0, i + 1);
                var pred = svc.Predict(sym, slice);
                if (!pred.IsReady || pred.Prediction <= 3) continue;
                double atrPct = CalcAtrPct(kl, i);
                var (tp, sl) = OutcomeIn(kl, i, 2.0m, 1.0m, 24);
                if (!(tp || sl)) continue;
                string bk = atrPct < 0.3 ? "<0.3%" : atrPct < 0.7 ? "0.3-0.7%" : atrPct < 1.5 ? "0.7-1.5%" : atrPct < 3.0 ? "1.5-3.0%" : ">3.0%";
                var s = atrBuckets[bk]; s.n++; if (tp) s.w++; atrBuckets[bk] = s;
            }
        }
        Console.WriteLine("  ATR/Close   Trades  Wins  WinRate  Edge");
        foreach (var kv in atrBuckets)
        {
            double wr = kv.Value.n > 0 ? kv.Value.w * 100.0 / kv.Value.n : 0;
            Console.WriteLine($"  {kv.Key,-10}  {kv.Value.n,6}  {kv.Value.w,4}  {wr,6:F2}%  {(wr - 33.33),+6:F2}%p");
        }

        // === E. 트리거별 단독 승률 (각 가드의 진짜 가치) ===
        Console.WriteLine();
        Console.WriteLine("=== E. 각 가드 단독 vs 무가드 비교 (TP=2.0/SL=1.0/WIN=24) ===");
        var (eN, eW) = MeasureNone(symData);
        var (lN, lW) = MeasureWith(symData, svc, useEma:false, useVol:false, lorThr:3);
        var (eaN, eaW) = MeasureWith(symData, svc, useEma:true, useVol:false, lorThr:-99);
        var (vaN, vaW) = MeasureWith(symData, svc, useEma:false, useVol:true, lorThr:-99);
        var (allN, allW) = MeasureWith(symData, svc, useEma:true, useVol:true, lorThr:3);
        Console.WriteLine("  Filter                      Trades  Wins  WinRate  Edge");
        Print("none                       ", eN, eW);
        Print("Lorentzian>3 only          ", lN, lW);
        Print("EMA20 rising only          ", eaN, eaW);
        Print("VolSurge>1.3x only         ", vaN, vaW);
        Print("ALL gates (Lor+EMA+Vol)    ", allN, allW);

        // === F. 결론 — 어느 가드가 정말 효과 있나, 어느 게 노이즈만 추가하나 ===
        Console.WriteLine();
        Console.WriteLine("=== F. 결론 / 권장 조치 ===");
        Console.WriteLine("  1. Pred 분포(A)에서 high-Pred일수록 win-rate가 높지 않으면 → Lorentzian 학습 부족");
        Console.WriteLine("  2. SL hit time(B)이 TP보다 짧으면 → 시장이 short bias, LONG 전략 자체 부적합");
        Console.WriteLine("  3. RSI 70+ 진입 승률이 낮으면 → '늦은 진입' 차단 가드 추가 필요");
        Console.WriteLine("  4. 단독 가드(E)에서 가장 효과 큰 것만 유지, 나머지 제거 → 진입 기회 확보");
    }

    private static double CalcRsi14(List<IBinanceKline> kl, int i)
    {
        if (i < 14) return 50.0;
        double g = 0, l = 0;
        for (int q = i - 13; q <= i; q++)
        {
            double d = (double)(kl[q].ClosePrice - kl[q - 1].ClosePrice);
            if (d > 0) g += d; else l -= d;
        }
        double avgG = g / 14.0, avgL = l / 14.0;
        return avgL < 1e-12 ? 100.0 : 100.0 - (100.0 / (1.0 + avgG / avgL));
    }
    private static double CalcAtrPct(List<IBinanceKline> kl, int i)
    {
        if (i < 14) return 0;
        double tr = 0;
        for (int q = i - 13; q <= i; q++)
        {
            double hl = (double)(kl[q].HighPrice - kl[q].LowPrice);
            double hc = Math.Abs((double)(kl[q].HighPrice - kl[q - 1].ClosePrice));
            double lc = Math.Abs((double)(kl[q].LowPrice - kl[q - 1].ClosePrice));
            tr += Math.Max(hl, Math.Max(hc, lc));
        }
        double atr = tr / 14.0;
        double close = (double)kl[i].ClosePrice;
        return close > 0 ? atr / close * 100.0 : 0;
    }
    private static double Median(List<int> a)
    {
        var s = a.OrderBy(x => x).ToList();
        return s.Count % 2 == 0 ? (s[s.Count/2 - 1] + s[s.Count/2]) / 2.0 : s[s.Count/2];
    }
    private static int Percentile(List<int> a, int p)
    {
        var s = a.OrderBy(x => x).ToList();
        int idx = (int)Math.Floor(p / 100.0 * s.Count);
        return s[Math.Min(idx, s.Count - 1)];
    }
    private static (int n, int w) MeasureNone(Dictionary<string, List<IBinanceKline>> symData)
    {
        int n = 0, w = 0;
        foreach (var kv in symData)
        {
            var kl = kv.Value; int te = (int)(kl.Count * 0.7);
            for (int i = te + 50; i < kl.Count - 24; i++)
            {
                var (tp, sl) = OutcomeIn(kl, i, 2.0m, 1.0m, 24);
                if (!(tp || sl)) continue;
                n++; if (tp) w++;
            }
        }
        return (n, w);
    }
    private static (int n, int w) MeasureWith(Dictionary<string, List<IBinanceKline>> symData, MiniLorentzianService svc, bool useEma, bool useVol, int lorThr)
    {
        int n = 0, w = 0;
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value; int te = (int)(kl.Count * 0.7);
            for (int i = te + 50; i < kl.Count - 24; i++)
            {
                if (useEma && !Ema20Rising(kl, i)) continue;
                if (useVol && !VolSurge(kl, i, 1.3)) continue;
                if (lorThr > -99)
                {
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= lorThr) continue;
                }
                var (tp, sl) = OutcomeIn(kl, i, 2.0m, 1.0m, 24);
                if (!(tp || sl)) continue;
                n++; if (tp) w++;
            }
        }
        return (n, w);
    }
    private static void Print(string label, int n, int w)
    {
        double wr = n > 0 ? w * 100.0 / n : 0;
        Console.WriteLine($"  {label} {n,6}  {w,4}  {wr,6:F2}%  {(wr - 33.33),+6:F2}%p");
    }

    /// <summary>[v5.20.7 FINAL] 실제 적용된 v5.20.7 로직으로 백테스트 — 수익률/수익금 보고</summary>
    private static async Task RunFinalBacktestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.20.7 FINAL LOGIC BACKTEST (chart data, 30 syms × 14 days 5m)");
        Console.WriteLine("  Gates: Lorentzian Pred>3 + EMA20 rising + Volume > 1.3x avg(20)");
        Console.WriteLine("  ALT_RSI<30 blocked (BTC/ETH/SOL/XRP exempt)");
        Console.WriteLine("================================================================");

        var svc = new MiniLorentzianService();
        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                int trainEnd = (int)(kl.Count * 0.7);
                int added = svc.BackfillFromCandles(sym, kl.GetRange(0, trainEnd));
                symData[sym] = kl;
                Console.WriteLine($"trained={added}");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };

        // 3 TP/SL configs to compare: 현재 코드 기본값 vs sweep 최적값 vs 보수
        var configs = new (string label, decimal tp, decimal sl, int win)[]
        {
            ("Current(1.5/0.7/12bar)",  1.5m, 0.7m, 12),
            ("Sweep-Best(2.0/1.0/24bar)", 2.0m, 1.0m, 24),
            ("Conservative(1.0/0.7/24bar)", 1.0m, 0.7m, 24),
        };

        Console.WriteLine();
        Console.WriteLine("=== v5.20.7 ALL GATES ON: Lorentzian>3 + EMA20↑ + Vol>1.3x + ALT_RSI block ===");
        Console.WriteLine();

        foreach (var cfg in configs)
        {
            decimal tpUsd = Notional * cfg.tp / 100m - RoundTripFee;
            decimal slUsd = Notional * cfg.sl / 100m + RoundTripFee;
            decimal beWR = cfg.sl / (cfg.tp + cfg.sl) * 100m;

            int trades = 0, wins = 0;
            decimal pnl = 0m;
            var perSym = new Dictionary<string, (int n, int w, decimal p)>();

            foreach (var kv in symData)
            {
                string sym = kv.Key; var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                int sN = 0, sW = 0; decimal sP = 0m;

                for (int i = trainEnd + 50; i < kl.Count - cfg.win; i++)
                {
                    // Gate 1: ALT_RSI_FALLING_KNIFE
                    if (!majors.Contains(sym))
                    {
                        if (i >= 14)
                        {
                            double g = 0, l = 0;
                            for (int q = i - 13; q <= i; q++)
                            {
                                double d = (double)(kl[q].ClosePrice - kl[q - 1].ClosePrice);
                                if (d > 0) g += d; else l -= d;
                            }
                            double avgG = g / 14.0, avgL = l / 14.0;
                            double rsi = avgL < 1e-12 ? 100.0 : 100.0 - (100.0 / (1.0 + avgG / avgL));
                            if (rsi < 30.0) continue;
                        }
                    }
                    // Gate 2: EMA20 rising
                    if (!Ema20Rising(kl, i)) continue;
                    // Gate 3: Vol surge >1.3x
                    if (!VolSurge(kl, i, 1.3)) continue;
                    // Gate 4: Lorentzian Pred > 3
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= 3) continue;
                    // TP/SL outcome
                    var (tp, sl) = OutcomeIn(kl, i, cfg.tp, cfg.sl, cfg.win);
                    if (!(tp || sl)) continue;
                    trades++; sN++;
                    if (tp) { wins++; sW++; pnl += tpUsd; sP += tpUsd; }
                    else    {                         pnl -= slUsd; sP -= slUsd; }
                }
                if (sN > 0) perSym[sym] = (sN, sW, sP);
            }

            double wr = trades > 0 ? wins * 100.0 / trades : 0;
            decimal avg = trades > 0 ? pnl / trades : 0m;
            // ROI: 마진 ${MARGIN_USD} 기준 누적 수익률
            decimal capitalUsed = MARGIN_USD * trades;  // 단순 누적 기준
            decimal roiPerTrade = trades > 0 ? pnl / capitalUsed * 100m : 0m;
            decimal roiOnInitial = pnl / 1000m * 100m;  // 초기 자본 $1000 기준

            Console.WriteLine($"┌─ Config: {cfg.label}  (BE win-rate {beWR:F2}%)");
            Console.WriteLine($"│   Trades:        {trades}");
            Console.WriteLine($"│   Wins / Losses: {wins} / {trades - wins}");
            Console.WriteLine($"│   Win-rate:      {wr:F2}%   (BE {beWR:F2}% → {(wr - (double)beWR):+0.00;-0.00}%p)");
            Console.WriteLine($"│   Total PnL:     ${pnl:F2}");
            Console.WriteLine($"│   Avg PnL/trade: ${avg:F2}");
            Console.WriteLine($"│   ROI/trade:     {roiPerTrade:F2}% (수익금/투입마진)");
            Console.WriteLine($"│   ROI vs $1000:  {roiOnInitial:F2}% (초기자본 $1000 기준)");
            Console.WriteLine($"│   $/14days:      ${pnl:F2}  →  ${(pnl/14m):F2}/day");
            Console.WriteLine($"└─ TOP 5 symbols:");
            foreach (var t in perSym.OrderByDescending(p => p.Value.p).Take(5))
                Console.WriteLine($"     {t.Key,-14} {t.Value.n,3} trades, {t.Value.w} wins, ${t.Value.p:F2}");
            Console.WriteLine($"   BOTTOM 5:");
            foreach (var t in perSym.OrderBy(p => p.Value.p).Take(5))
                Console.WriteLine($"     {t.Key,-14} {t.Value.n,3} trades, {t.Value.w} wins, ${t.Value.p:F2}");
            Console.WriteLine();
        }

        // Compare BASELINE (no gates) vs FINAL (all gates) for reference
        Console.WriteLine("=== REFERENCE: Baseline (no gates) vs FINAL (v5.20.7 all gates) ===");
        decimal tpRef = Notional * 1.5m / 100m - RoundTripFee;
        decimal slRef = Notional * 0.7m / 100m + RoundTripFee;
        int bN = 0, bW = 0; decimal bP = 0m;
        int fN = 0, fW = 0; decimal fP = 0m;
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value;
            int trainEnd = (int)(kl.Count * 0.7);
            for (int i = trainEnd + 50; i < kl.Count - 12; i++)
            {
                var (tp, sl) = OutcomeIn(kl, i, 1.5m, 0.7m, 12);
                if (!(tp || sl)) continue;
                bN++;
                if (tp) { bW++; bP += tpRef; } else bP -= slRef;
            }
        }
        foreach (var kv in symData)
        {
            string sym = kv.Key; var kl = kv.Value;
            int trainEnd = (int)(kl.Count * 0.7);
            for (int i = trainEnd + 50; i < kl.Count - 12; i++)
            {
                if (!majors.Contains(sym))
                {
                    if (i >= 14)
                    {
                        double g = 0, l = 0;
                        for (int q = i - 13; q <= i; q++)
                        { double d = (double)(kl[q].ClosePrice - kl[q-1].ClosePrice); if (d > 0) g += d; else l -= d; }
                        double avgG = g / 14.0, avgL = l / 14.0;
                        double rsi = avgL < 1e-12 ? 100.0 : 100.0 - (100.0 / (1.0 + avgG / avgL));
                        if (rsi < 30.0) continue;
                    }
                }
                if (!Ema20Rising(kl, i)) continue;
                if (!VolSurge(kl, i, 1.3)) continue;
                var slice = kl.GetRange(0, i + 1);
                var pred = svc.Predict(sym, slice);
                if (!pred.IsReady || pred.Prediction <= 3) continue;
                var (tp, sl) = OutcomeIn(kl, i, 1.5m, 0.7m, 12);
                if (!(tp || sl)) continue;
                fN++;
                if (tp) { fW++; fP += tpRef; } else fP -= slRef;
            }
        }
        Console.WriteLine($"  Baseline (no gates, 모든 캔들 진입):");
        Console.WriteLine($"    {bN} trades, win-rate {(bN>0?bW*100.0/bN:0):F2}%, PnL ${bP:F2}");
        Console.WriteLine($"  v5.20.7 FINAL (모든 가드 적용):");
        Console.WriteLine($"    {fN} trades, win-rate {(fN>0?fW*100.0/fN:0):F2}%, PnL ${fP:F2}");
        Console.WriteLine($"  → 진입 차단: {bN - fN}건 ({(bN>0?(bN-fN)*100.0/bN:0):F1}%)");
        Console.WriteLine($"  → PnL 개선: {(fP - bP):+$0.00;-$0.00} ({(bP!=0?(double)((fP-bP)/Math.Abs(bP)*100):0):+0.00;-0.00}%)");
    }

    /// <summary>[v5.20.7 B-plan] 통합 스윕 — fetch+train 1회 후 P1/P2/P3 모두 실행</summary>
    private static async Task RunAllSweepsAsync()
    {
        Console.WriteLine("=========================================================");
        Console.WriteLine("  ALL SWEEPS — P1 Lorentzian threshold / P2 Trigger / P3 Window");
        Console.WriteLine("  Base: TP=2.0% / SL=1.0% (이전 스윕 1위, 손실 최소)");
        Console.WriteLine("=========================================================");

        var svc = new MiniLorentzianService();
        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                int trainEnd = (int)(kl.Count * 0.7);
                int added = svc.BackfillFromCandles(sym, kl.GetRange(0, trainEnd));
                symData[sym] = kl;
                Console.WriteLine($"trained={added}");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        decimal tpPct = 2.0m, slPct = 1.0m;
        decimal tpUsd = Notional * tpPct / 100m - RoundTripFee;
        decimal slUsd = Notional * slPct / 100m + RoundTripFee;

        Func<List<IBinanceKline>, int, bool, int> compute = (kl, trainEnd, _) => 0;

        // === P1: Lorentzian threshold sweep ===
        Console.WriteLine();
        Console.WriteLine("=== P1: Lorentzian Prediction threshold (TP=2.0% / SL=1.0% / WIN=12) ===");
        Console.WriteLine("  Threshold  Trades  WinRate  PnL$         AvgPnL$");
        int[] thresholds = { 0, 2, 3, 4, 5, 6 };
        foreach (var thr in thresholds)
        {
            int dec = 0, tpHit = 0; decimal pnl = 0m;
            foreach (var kv in symData)
            {
                string sym = kv.Key; var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                for (int i = trainEnd + 50; i < kl.Count - 12; i++)
                {
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, 12);
                    if (!(tp || sl)) continue;
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= thr) continue;
                    dec++;
                    if (tp) { tpHit++; pnl += tpUsd; } else pnl -= slUsd;
                }
            }
            double wr = dec > 0 ? tpHit * 100.0 / dec : 0;
            decimal avg = dec > 0 ? pnl / dec : 0m;
            Console.WriteLine($"  > {thr,-7}  {dec,6}  {wr,6:F2}%  {pnl,11:F2}  {avg,7:F2}");
        }

        // === P2: Entry trigger filter ===
        Console.WriteLine();
        Console.WriteLine("=== P2: 진입 트리거 필터 (Lorentzian Prediction>0 + 트리거) ===");
        Console.WriteLine("  Trigger             Trades  WinRate  PnL$         AvgPnL$");
        var triggers = new (string name, Func<List<IBinanceKline>, int, bool> ok)[]
        {
            ("none",         (kl,i) => true),
            ("EMA20_rising", (kl,i) => Ema20Rising(kl, i)),
            ("VolSurge>1.5", (kl,i) => VolSurge(kl, i, 1.5)),
            ("BBWalk_upper", (kl,i) => BBWalkUpper(kl, i)),
            ("EMA+Vol+BB",   (kl,i) => Ema20Rising(kl,i) && VolSurge(kl,i,1.3) && BBWalkUpper(kl,i)),
            ("EMA+Vol",      (kl,i) => Ema20Rising(kl,i) && VolSurge(kl,i,1.3)),
        };
        foreach (var (name, ok) in triggers)
        {
            int dec = 0, tpHit = 0; decimal pnl = 0m;
            foreach (var kv in symData)
            {
                string sym = kv.Key; var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                for (int i = trainEnd + 50; i < kl.Count - 12; i++)
                {
                    if (!ok(kl, i)) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, 12);
                    if (!(tp || sl)) continue;
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= 0) continue;
                    dec++;
                    if (tp) { tpHit++; pnl += tpUsd; } else pnl -= slUsd;
                }
            }
            double wr = dec > 0 ? tpHit * 100.0 / dec : 0;
            decimal avg = dec > 0 ? pnl / dec : 0m;
            Console.WriteLine($"  {name,-18}  {dec,6}  {wr,6:F2}%  {pnl,11:F2}  {avg,7:F2}");
        }

        // === P3: Holding window ===
        Console.WriteLine();
        Console.WriteLine("=== P3: Holding window (TP=2.0% / SL=1.0% / Lorentzian Prediction>0) ===");
        Console.WriteLine("  Window  Trades  WinRate  PnL$         AvgPnL$");
        int[] windows = { 6, 12, 24, 36, 48, 72 };
        foreach (var w in windows)
        {
            int dec = 0, tpHit = 0; decimal pnl = 0m;
            foreach (var kv in symData)
            {
                string sym = kv.Key; var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                for (int i = trainEnd + 50; i < kl.Count - w; i++)
                {
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, w);
                    if (!(tp || sl)) continue;
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= 0) continue;
                    dec++;
                    if (tp) { tpHit++; pnl += tpUsd; } else pnl -= slUsd;
                }
            }
            double wr = dec > 0 ? tpHit * 100.0 / dec : 0;
            decimal avg = dec > 0 ? pnl / dec : 0m;
            Console.WriteLine($"  {w,3} bars {dec,6}  {wr,6:F2}%  {pnl,11:F2}  {avg,7:F2}");
        }

        // === P4 (보너스): 최선 조합 — P1/P2/P3 베스트 합성 ===
        Console.WriteLine();
        Console.WriteLine("=== P4: 합성 (Lorentzian>3 + EMA+Vol 트리거 + WIN=24 + TP2.0/SL1.0) ===");
        {
            int dec = 0, tpHit = 0; decimal pnl = 0m;
            foreach (var kv in symData)
            {
                string sym = kv.Key; var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                int W = 24;
                for (int i = trainEnd + 50; i < kl.Count - W; i++)
                {
                    if (!(Ema20Rising(kl, i) && VolSurge(kl, i, 1.3))) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, W);
                    if (!(tp || sl)) continue;
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= 3) continue;
                    dec++;
                    if (tp) { tpHit++; pnl += tpUsd; } else pnl -= slUsd;
                }
            }
            double wr = dec > 0 ? tpHit * 100.0 / dec : 0;
            decimal avg = dec > 0 ? pnl / dec : 0m;
            Console.WriteLine($"  Combo: {dec} 진입, win-rate {wr:F2}%, PnL ${pnl:F2}, 평균 ${avg:F2}/거래");
            Console.WriteLine(pnl > 0
                ? $"  ✅ 흑자 가능! ${pnl:F2}"
                : $"  ❌ 여전히 손실 ${pnl:F2}");
        }
    }

    private static (bool tp, bool sl) OutcomeIn(List<IBinanceKline> kl, int i, decimal tpPct, decimal slPct, int win)
    {
        decimal entry = kl[i].ClosePrice;
        decimal tpPx = entry * (1 + tpPct/100m);
        decimal slPx = entry * (1 - slPct/100m);
        for (int k = 1; k <= win && i + k < kl.Count; k++)
        {
            var b = kl[i + k];
            if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return (false, true);
            if (b.HighPrice >= tpPx) return (true, false);
            if (b.LowPrice <= slPx) return (false, true);
        }
        return (false, false);
    }
    private static bool Ema20Rising(List<IBinanceKline> kl, int i)
    {
        if (i < 25) return false;
        decimal e1 = Ema(kl, i, 20);
        decimal e0 = Ema(kl, i - 5, 20);
        return e1 > e0;
    }
    private static decimal Ema(List<IBinanceKline> kl, int idx, int period)
    {
        decimal k = 2m / (period + 1);
        decimal ema = kl[Math.Max(0, idx - period * 2)].ClosePrice;
        int from = Math.Max(0, idx - period * 2);
        for (int j = from + 1; j <= idx; j++) ema = kl[j].ClosePrice * k + ema * (1 - k);
        return ema;
    }
    private static bool VolSurge(List<IBinanceKline> kl, int i, double mult)
    {
        if (i < 20) return false;
        double cur = (double)kl[i].Volume;
        double sum = 0;
        for (int j = i - 20; j < i; j++) sum += (double)kl[j].Volume;
        double avg = sum / 20.0;
        if (avg < 1e-9) return false;
        return cur > avg * mult;
    }
    private static bool BBWalkUpper(List<IBinanceKline> kl, int i)
    {
        if (i < 20) return false;
        double sum = 0; for (int j = i - 19; j <= i; j++) sum += (double)kl[j].ClosePrice;
        double mean = sum / 20.0;
        double sq = 0;
        for (int j = i - 19; j <= i; j++) { double d = (double)kl[j].ClosePrice - mean; sq += d * d; }
        double sd = Math.Sqrt(sq / 20.0);
        double upper = mean + 2 * sd;
        return (double)kl[i].ClosePrice >= upper;
    }

    /// <summary>[v5.20.7 B-plan] TP/SL 조합 스윕 — 차트데이터로 흑자 전환 가능 손익비 탐색</summary>
    private static async Task RunSweepAsync()
    {
        Console.WriteLine("=========================================================");
        Console.WriteLine("  TP/SL 스윕 — 30 심볼 × 14일 5m / Lorentzian gate ON");
        Console.WriteLine("  (마진 $100 × 10x = notional $1,000, fee 0.04%×2)");
        Console.WriteLine("=========================================================");

        // 모든 캔들을 한 번에 fetch + train (재사용)
        var svc = new MiniLorentzianService();
        var symData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[fetch {idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                var trainSlice = kl.GetRange(0, (int)(kl.Count * 0.7));
                int added = svc.BackfillFromCandles(sym, trainSlice);
                symData[sym] = kl;
                Console.WriteLine($"trained={added}");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        Console.WriteLine();
        Console.WriteLine("  TP%   SL%   BE%      Trades  WinRate  PnL$         AvgPnL$");
        Console.WriteLine("  ----  ----  ------   ------  -------  -----------  -------");

        foreach (var combo in Sweep)
        {
            decimal tpPct = combo.tp;
            decimal slPct = combo.sl;
            decimal tpUsd = Notional * tpPct / 100m - RoundTripFee;
            decimal slUsd = Notional * slPct / 100m + RoundTripFee;
            decimal beWR = slPct / (tpPct + slPct) * 100m;

            int dec = 0, tpHit = 0;
            decimal pnl = 0m;
            foreach (var kv in symData)
            {
                string sym = kv.Key;
                var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                for (int i = trainEnd + 50; i < kl.Count - WIN; i++)
                {
                    decimal entry = kl[i].ClosePrice;
                    decimal tpPx = entry * (1 + tpPct/100m);
                    decimal slPx = entry * (1 - slPct/100m);
                    bool tp = false, sl = false;
                    for (int k = 1; k <= WIN; k++)
                    {
                        var b = kl[i + k];
                        if (b.HighPrice >= tpPx && b.LowPrice <= slPx) { sl = true; break; }
                        if (b.HighPrice >= tpPx) { tp = true; break; }
                        if (b.LowPrice <= slPx) { sl = true; break; }
                    }
                    if (!(tp || sl)) continue;
                    var slice = kl.GetRange(0, i + 1);
                    var pred = svc.Predict(sym, slice);
                    if (!pred.IsReady || pred.Prediction <= 0) continue;
                    dec++;
                    if (tp) { tpHit++; pnl += tpUsd; } else { pnl -= slUsd; }
                }
            }
            double wr = dec > 0 ? tpHit * 100.0 / dec : 0;
            decimal avg = dec > 0 ? pnl / dec : 0m;
            Console.WriteLine($"  {tpPct,4:F1}  {slPct,4:F1}  {beWR,5:F2}%   {dec,6}  {wr,6:F2}%  {pnl,11:F2}  {avg,7:F2}");
        }
    }

    private static readonly string[] symbols =
    {
        "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT","ADAUSDT","TRXUSDT","AVAXUSDT","LINKUSDT",
        "APEUSDT","API3USDT","DUSDT","DYMUSDT","DYDXUSDT","ESPORTSUSDT","SPORTFUNUSDT","KGENUSDT","PLAYUSDT","MAGMAUSDT",
        "GRIFFAINUSDT","WUSDT","PUMPBTCUSDT","ZBTUSDT","GALAUSDT","SOONUSDT","OPNUSDT","ZKPUSDT","BSBUSDT","KATUSDT"
    };
    private const decimal TP_PCT = 1.5m, SL_PCT = 0.7m;
    private const int WIN = 12;
    // [v5.20.7] TP/SL 스윕 — 차트 기반 최적 손익비 탐색
    private static readonly (decimal tp, decimal sl)[] Sweep =
    {
        (1.0m, 0.5m), (1.5m, 0.5m), (2.0m, 0.5m), (3.0m, 0.5m),
        (1.0m, 0.7m), (1.5m, 0.7m), (2.0m, 0.7m), (3.0m, 0.7m),
        (2.0m, 1.0m), (3.0m, 1.0m), (4.0m, 1.0m),
    };
    private const int BARS_PER_REQ = 1500;
    private const int PAGES = 3; // ~14 days
    // 수익금 시뮬: 마진 $100 × 레버리지 (CLI override 가능), 수수료 0.04% 양방향
    private const decimal MARGIN_USD = 100m;
    private static decimal LEVERAGE = 10m;  // --lev N CLI 로 override
    private const decimal FEE_RATE   = 0.0004m;
    private static decimal Notional => MARGIN_USD * LEVERAGE;
    private static decimal RoundTripFee => Notional * FEE_RATE * 2m;
    private static decimal TpProfit => Notional * TP_PCT / 100m - RoundTripFee;
    private static decimal SlLoss   => Notional * SL_PCT / 100m + RoundTripFee;
    // [v5.21.3] 카테고리별 마진 (CLI: --margin-major / --margin-pump 등)
    private static decimal MarginMajor = 100m;
    private static decimal MarginPump  = 100m;
    private static decimal MarginSqueeze = 100m;
    private static decimal MarginBBWalk = 100m;
    private static decimal MarginSpike = 100m;
    private static decimal NotionalFor(string trig) => trig switch
    {
        "MAJOR" => MarginMajor * LEVERAGE,
        "PUMP" => MarginPump * LEVERAGE,
        "SQUEEZE" => MarginSqueeze * LEVERAGE,
        "BB_WALK" => MarginBBWalk * LEVERAGE,
        "SPIKE" => MarginSpike * LEVERAGE,
        _ => MARGIN_USD * LEVERAGE
    };

    // ═══════════════════════════════════════════════════════════════════════════════════
    // [v5.21.13] AI 게이트 포함 백테스트 — 라이브 봇 시뮬과 동일
    //   가드(v5.21.1: EMA20↑ + RSI<65) + AI(Lorentzian KNN pred>0) → 진입 시뮬
    //   카테고리별 TP/SL: MAJOR 타이트(0.5/1.5/12) / 알트 권장(1.0/3.0/24)
    //   기간: 10/30/60/90/180일
    // ═══════════════════════════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════════════════════════
    // [v5.22.1] 라이브 로직 백테스트 — AI 게이트 제거, 가드만으로 진입
    //   가드: v5.21.1 (EMA20↑ + RSI<65)
    //   TP/SL: MAJOR 0.5/1.5/12  /  알트 1.0/3.0/24
    //   기간: 1/10/30/60/90/180/360일 (7개)
    // ═══════════════════════════════════════════════════════════════════════════════════
    private static async Task RunLiveAllPeriodsAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.1 라이브 로직 백테스트 (AI 게이트 제거, 가드만)");
        Console.WriteLine("  가드: v5.21.1 (EMA20↑ + RSI<65)");
        Console.WriteLine("  TP/SL: MAJOR 0.5/1.5/12  /  알트 1.0/3.0/24");
        Console.WriteLine("================================================================");

        var periods = new[] {
            (label: "1일",   pages: 1),
            (label: "10일",  pages: 2),
            (label: "30일",  pages: 6),
            (label: "60일",  pages: 12),
            (label: "90일",  pages: 18),
            (label: "180일", pages: 36),
            (label: "360일", pages: 70),
        };

        Console.WriteLine();
        Console.WriteLine($"[fetch 360일치 캔들 — {symbols.Length}개 심볼]");
        var maxPages = periods.Max(p => p.pages);
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, maxPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        var triggers = new (string name, Func<List<IBinanceKline>, int, string, bool> ok)[]
        {
            ("PUMP",    (kl, i, sym) => i >= 20 && PriceChange(kl, i, 1) >= 1.5 && VolMult(kl, i, 20) >= 3.0),
            ("MAJOR",   (kl, i, sym) => majors.Contains(sym) && i >= 30 && Ema20Rising(kl, i)
                          && M15RangePos(kl, i, 30) is >= 60 and <= 85),
            ("SQUEEZE", (kl, i, sym) => i >= 20 && BBWidth(kl, i) < 1.5 && BBWalkUpper(kl, i)),
            ("BB_WALK", (kl, i, sym) => i >= 20 && BBWalkStreak(kl, i, 5) >= 4),
        };

        var summary = new List<(string period, int n, int w, decimal pnl, decimal majorPnl, decimal pumpPnl, decimal sqzPnl, decimal bbwPnl)>();

        foreach (var per in periods)
        {
            int sliceLen = per.pages * BARS_PER_REQ;
            var slicedData = new Dictionary<string, List<IBinanceKline>>();
            foreach (var kv in fullData)
            {
                int start = Math.Max(0, kv.Value.Count - sliceLen);
                var slice = kv.Value.GetRange(start, kv.Value.Count - start);
                if (slice.Count < 400) continue;
                slicedData[kv.Key] = slice;
            }

            int totalN = 0, totalW = 0;
            decimal totalPnl = 0m, majorPnl = 0m, pumpPnl = 0m, sqzPnl = 0m, bbwPnl = 0m;

            foreach (var trig in triggers)
            {
                decimal trigNotional = NotionalFor(trig.name);
                decimal trigFee = trigNotional * FEE_RATE * 2m;
                decimal tpPct, slPct; int win;
                if (trig.name == "MAJOR") { tpPct = 0.5m; slPct = 1.5m; win = 12; }
                else { tpPct = 1.0m; slPct = 3.0m; win = 24; }
                decimal tpUsd = trigNotional * tpPct / 100m - trigFee;
                decimal slUsd = trigNotional * slPct / 100m + trigFee;

                int catN = 0, catW = 0; decimal catPnl = 0m;
                foreach (var kv in slicedData)
                {
                    var kl = kv.Value; var sym = kv.Key;
                    for (int i = 50; i < kl.Count - win; i++)
                    {
                        if (!trig.ok(kl, i, sym)) continue;
                        // v5.21.1 가드 — AI 게이트 없음
                        if (!Ema20Rising(kl, i)) continue;
                        if (CalcRsi14(kl, i) >= 65) continue;
                        var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                        if (!(tp || sl)) continue;
                        catN++;
                        if (tp) { catW++; catPnl += tpUsd; } else catPnl -= slUsd;
                    }
                }
                totalN += catN; totalW += catW; totalPnl += catPnl;
                if (trig.name == "MAJOR") majorPnl = catPnl;
                else if (trig.name == "PUMP") pumpPnl = catPnl;
                else if (trig.name == "SQUEEZE") sqzPnl = catPnl;
                else if (trig.name == "BB_WALK") bbwPnl = catPnl;
            }

            summary.Add((per.label, totalN, totalW, totalPnl, majorPnl, pumpPnl, sqzPnl, bbwPnl));
        }

        // 출력
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.1 라이브 로직 백테스트 결과 (가드만, AI 없음)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"기간",-7} {"진입수",7} {"승률",8} {"총PnL",11} {"avg",8} {"MAJOR",10} {"PUMP",10} {"SQZ",10} {"BBW",10}");
        Console.WriteLine(new string('-', 100));
        foreach (var s in summary)
        {
            double wr = s.n > 0 ? s.w * 100.0 / s.n : 0;
            decimal avg = s.n > 0 ? s.pnl / s.n : 0m;
            Console.WriteLine($"{s.period,-7} {s.n,7} {wr,7:F2}% {s.pnl,10:F2} {avg,7:F2} {s.majorPnl,9:F2} {s.pumpPnl,9:F2} {s.sqzPnl,9:F2} {s.bbwPnl,9:F2}");
        }
    }

    private static async Task RunAiAllPeriodsAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  AI 게이트 포함 백테스트 (라이브 봇 시뮬)");
        Console.WriteLine("  가드: v5.21.1 (EMA20↑ + RSI<65)  AI: Lorentzian KNN pred>0");
        Console.WriteLine("  TP/SL: MAJOR 0.5/1.5/12  /  알트 1.0/3.0/24");
        Console.WriteLine("================================================================");

        var periods = new[] {
            (label: "1일",   pages: 1),   // 페이징 최소 단위 (실제 5일치)
            (label: "10일",  pages: 2),
            (label: "30일",  pages: 6),
            (label: "60일",  pages: 12),
            (label: "90일",  pages: 18),
            (label: "180일", pages: 36),
        };

        // 한 번에 가장 긴 기간(180일) fetch — 짧은 기간은 슬라이스로 사용
        Console.WriteLine();
        Console.WriteLine($"[fetch 180일치 캔들 — {symbols.Length}개 심볼]");
        var maxPages = periods.Max(p => p.pages);
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, maxPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        var triggers = new (string name, Func<List<IBinanceKline>, int, string, bool> ok)[]
        {
            ("PUMP",    (kl, i, sym) => i >= 20 && PriceChange(kl, i, 1) >= 1.5 && VolMult(kl, i, 20) >= 3.0),
            ("MAJOR",   (kl, i, sym) => majors.Contains(sym) && i >= 30 && Ema20Rising(kl, i)
                          && M15RangePos(kl, i, 30) is >= 60 and <= 85),
            ("SQUEEZE", (kl, i, sym) => i >= 20 && BBWidth(kl, i) < 1.5 && BBWalkUpper(kl, i)),
            ("BB_WALK", (kl, i, sym) => i >= 20 && BBWalkStreak(kl, i, 5) >= 4),
        };

        // 각 기간별 결과 누적
        var summary = new List<(string period, int n, int w, decimal pnl, decimal majorPnl, decimal pumpPnl, decimal sqzPnl, decimal bbwPnl)>();

        foreach (var per in periods)
        {
            // 기간 슬라이스: 마지막 (per.pages * BARS_PER_REQ) 개 캔들
            int sliceLen = per.pages * BARS_PER_REQ;

            // AI 학습: 슬라이스 시작 전 KNN 백필 (학습 = 슬라이스 이전 70% / 테스트 = 슬라이스 30% 후반부 — 미래 데이터 누설 방지)
            var svc = new MiniLorentzianService();
            var slicedData = new Dictionary<string, List<IBinanceKline>>();
            foreach (var kv in fullData)
            {
                int start = Math.Max(0, kv.Value.Count - sliceLen);
                var slice = kv.Value.GetRange(start, kv.Value.Count - start);
                if (slice.Count < 400) continue;
                slicedData[kv.Key] = slice;
                int trainEnd = (int)(slice.Count * 0.5);  // 슬라이스 앞 50% 학습
                svc.BackfillFromCandles(kv.Key, slice.GetRange(0, trainEnd));
            }

            int totalN = 0, totalW = 0;
            decimal totalPnl = 0m, majorPnl = 0m, pumpPnl = 0m, sqzPnl = 0m, bbwPnl = 0m;

            foreach (var trig in triggers)
            {
                decimal trigNotional = NotionalFor(trig.name);
                decimal trigFee = trigNotional * FEE_RATE * 2m;
                // TP/SL: MAJOR 만 타이트, 나머지는 권장
                decimal tpPct, slPct; int win;
                if (trig.name == "MAJOR") { tpPct = 0.5m; slPct = 1.5m; win = 12; }
                else { tpPct = 1.0m; slPct = 3.0m; win = 24; }
                decimal tpUsd = trigNotional * tpPct / 100m - trigFee;
                decimal slUsd = trigNotional * slPct / 100m + trigFee;

                int catN = 0, catW = 0; decimal catPnl = 0m;
                foreach (var kv in slicedData)
                {
                    var kl = kv.Value; var sym = kv.Key;
                    int trainEnd = (int)(kl.Count * 0.5);
                    for (int i = trainEnd + 50; i < kl.Count - win; i++)
                    {
                        if (!trig.ok(kl, i, sym)) continue;
                        // v5.21.1 가드
                        if (!Ema20Rising(kl, i)) continue;
                        if (CalcRsi14(kl, i) >= 65) continue;
                        // [핵심] AI 게이트: Lorentzian KNN pred > 0
                        var aiSlice = kl.GetRange(0, i + 1);
                        var pred = svc.Predict(sym, aiSlice);
                        if (!pred.IsReady || pred.Prediction <= 0) continue;
                        // TP/SL 시뮬
                        var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                        if (!(tp || sl)) continue;
                        catN++;
                        if (tp) { catW++; catPnl += tpUsd; } else catPnl -= slUsd;
                    }
                }
                totalN += catN; totalW += catW; totalPnl += catPnl;
                if (trig.name == "MAJOR") majorPnl = catPnl;
                else if (trig.name == "PUMP") pumpPnl = catPnl;
                else if (trig.name == "SQUEEZE") sqzPnl = catPnl;
                else if (trig.name == "BB_WALK") bbwPnl = catPnl;
            }

            summary.Add((per.label, totalN, totalW, totalPnl, majorPnl, pumpPnl, sqzPnl, bbwPnl));
        }

        // 최종 표 출력
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  AI 게이트 포함 백테스트 결과 (라이브 봇 시뮬)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"기간",-7} {"진입수",7} {"승률",8} {"총PnL",11} {"avg",8} {"MAJOR",10} {"PUMP",10} {"SQZ",10} {"BBW",10}");
        Console.WriteLine(new string('-', 100));
        foreach (var s in summary)
        {
            double wr = s.n > 0 ? s.w * 100.0 / s.n : 0;
            decimal avg = s.n > 0 ? s.pnl / s.n : 0m;
            Console.WriteLine($"{s.period,-7} {s.n,7} {wr,7:F2}% {s.pnl,10:F2} {avg,7:F2} {s.majorPnl,9:F2} {s.pumpPnl,9:F2} {s.sqzPnl,9:F2} {s.bbwPnl,9:F2}");
        }
    }

    private static async Task<List<IBinanceKline>> FetchKlinesAsync(string sym, int pages = PAGES)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < pages; p++)
        {
            var page = await FetchPageAsync(sym, endMs, BARS_PER_REQ);
            if (page == null || page.Count == 0) break;
            all.Insert(0, page);
            endMs = ((DateTimeOffset)page[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            if (page.Count < BARS_PER_REQ) break;
        }
        return all.SelectMany(c => c).ToList();
    }
    private static async Task<List<IBinanceKline>?> FetchPageAsync(string sym, long endMs, int limit)
    {
        for (int t = 1; t <= 4; t++)
        {
            try
            {
                await Task.Delay(800);
                var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={sym}&interval=5m&limit={limit}&endTime={endMs}";
                var json = await http.GetStringAsync(url);
                var arr = JsonDocument.Parse(json).RootElement;
                var list = new List<IBinanceKline>(arr.GetArrayLength());
                foreach (var k in arr.EnumerateArray())
                {
                    list.Add(new SimpleKline
                    {
                        OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()).UtcDateTime,
                        OpenPrice = decimal.Parse(k[1].GetString()!, CultureInfo.InvariantCulture),
                        HighPrice = decimal.Parse(k[2].GetString()!, CultureInfo.InvariantCulture),
                        LowPrice  = decimal.Parse(k[3].GetString()!, CultureInfo.InvariantCulture),
                        ClosePrice = decimal.Parse(k[4].GetString()!, CultureInfo.InvariantCulture),
                        Volume = decimal.Parse(k[5].GetString()!, CultureInfo.InvariantCulture),
                        CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(k[6].GetInt64()).UtcDateTime
                    });
                }
                return list;
            }
            catch (Exception ex) when (ex.Message.Contains("429") || ex.Message.Contains("1003"))
            {
                await Task.Delay(t * 5000);
            }
            catch { return null; }
        }
        return null;
    }

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        for (int a = 0; a < args.Length; a++)
        {
            if (args[a] == "--lev" && a + 1 < args.Length && decimal.TryParse(args[a + 1], out var lev))
            {
                LEVERAGE = lev;
                Console.WriteLine($"[CONFIG] LEVERAGE override = {LEVERAGE}x → notional ${Notional:F0}");
            }
            if (args[a] == "--margin-major" && a + 1 < args.Length && decimal.TryParse(args[a + 1], out var mm))
            {
                MarginMajor = MarginSqueeze = MarginBBWalk = mm;  // major-tier triggers
                Console.WriteLine($"[CONFIG] MarginMajor/Squeeze/BBWalk = ${mm}");
            }
            if (args[a] == "--margin-pump" && a + 1 < args.Length && decimal.TryParse(args[a + 1], out var mp))
            {
                MarginPump = MarginSpike = mp;
                Console.WriteLine($"[CONFIG] MarginPump/Spike = ${mp}");
            }
        }
        if (args.Length > 0 && args[0] == "--sweep")
        {
            await RunSweepAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--sweep-all")
        {
            await RunAllSweepsAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--final")
        {
            await RunFinalBacktestAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--diagnose")
        {
            await RunDiagnosisAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--redesign")
        {
            await RunRedesignAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--target70")
        {
            await RunTarget70Async();
            return;
        }
        if (args.Length > 0 && args[0] == "--target70-90d")
        {
            await RunTarget70Async(pages: 18);  // ~90일
            return;
        }
        if (args.Length > 0 && args[0] == "--live-all")
        {
            // [v5.22.1] 라이브 로직 백테스트 — AI 게이트 제거, 가드만으로 진입
            //   1/10/30/60/90/180/360일 7개 기간 카테고리별 합산
            await RunLiveAllPeriodsAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--ai-all")
        {
            // [v5.21.13] AI 게이트 포함 백테스트 — 라이브 봇 시뮬과 동일
            //   기존 RunLogicBreakdownAsync = 가드만 시뮬 (AI 미포함)
            //   본 모드 = 가드 + Lorentzian KNN (라이브 봇의 ML.NET 모델 근사) 게이트
            //   AI 임계: pred.Prediction > 0 통과 (v5.21.12 임계 0.005 와 동급 수준)
            await RunAiAllPeriodsAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-1d")
        {
            await RunLogicBreakdownAsync(pages: 1);  // 1일 (실제 5일치 데이터, 페이징 최소 단위)
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-10d")
        {
            await RunLogicBreakdownAsync(pages: 2);  // 10일
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-30d")
        {
            await RunLogicBreakdownAsync(pages: 6);  // 30일
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-60d")
        {
            await RunLogicBreakdownAsync(pages: 12);  // 60일
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-90d")
        {
            await RunLogicBreakdownAsync(pages: 18);  // 90일
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-180d")
        {
            await RunLogicBreakdownAsync(pages: 36);  // 180일 (6개월)
            return;
        }
        if (args.Length > 0 && args[0] == "--logic-365d")
        {
            await RunLogicBreakdownAsync(pages: 70);  // 365일 (1년)
            return;
        }
        if (args.Length > 0 && args[0] == "--pump-tune")
        {
            await RunPumpTuneAsync(pages: 18);  // 90일 PUMP 전용
            return;
        }
        var svc = new MiniLorentzianService();
        Console.WriteLine($"[REAL Lorentzian C# engine] K={svc.NeighborsCount} feat={svc.FeatureCount}");

        int totBaseDec = 0, totBaseTP = 0;
        int totGateDec = 0, totGateTP = 0, totGated = 0;
        decimal totBasePnL = 0m, totGatePnL = 0m;
        var perSym = new List<(string sym, int bDec, double bWR, decimal bPnL, int gDec, double gWR, decimal gPnL, int gated)>();

        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym); }
            catch (Exception ex) { Console.WriteLine("fetch fail: " + ex.Message); continue; }
            if (kl.Count < 400) { Console.WriteLine($"skip ({kl.Count} bars)"); continue; }

            int trainEnd = (int)(kl.Count * 0.7);
            var trainSlice = kl.GetRange(0, trainEnd);
            int added = svc.BackfillFromCandles(sym, trainSlice);

            int bDec = 0, bTP = 0, gDec = 0, gTP = 0, gated = 0;
            decimal bPnL = 0m, gPnL = 0m;
            for (int i = trainEnd + 50; i < kl.Count - WIN; i++)
            {
                decimal entry = kl[i].ClosePrice;
                decimal tpPx = entry * (1 + TP_PCT/100m);
                decimal slPx = entry * (1 - SL_PCT/100m);
                bool tp = false, sl = false;
                for (int k = 1; k <= WIN; k++)
                {
                    var b = kl[i + k];
                    if (b.HighPrice >= tpPx && b.LowPrice <= slPx) { sl = true; break; }
                    if (b.HighPrice >= tpPx) { tp = true; break; }
                    if (b.LowPrice <= slPx) { sl = true; break; }
                }
                if (!(tp || sl)) continue;
                bDec++;
                decimal pnl = tp ? TpProfit : -SlLoss;
                bPnL += pnl;
                if (tp) bTP++;

                var slice = kl.GetRange(0, i + 1);
                var pred = svc.Predict(sym, slice);
                if (!pred.IsReady) { gated++; continue; }
                if (pred.Prediction <= 0) { gated++; continue; }
                gDec++; gPnL += pnl;
                if (tp) gTP++;
            }
            double bWR = bDec > 0 ? bTP * 100.0 / bDec : 0;
            double gWR = gDec > 0 ? gTP * 100.0 / gDec : 0;
            Console.WriteLine($"trained={added} | base[{bDec}, {bWR:F1}%, ${bPnL:F0}] gate[{gDec}, {gWR:F1}%, ${gPnL:F0}, gated {gated}]");
            totBaseDec += bDec; totBaseTP += bTP; totBasePnL += bPnL;
            totGateDec += gDec; totGateTP += gTP; totGatePnL += gPnL; totGated += gated;
            perSym.Add((sym, bDec, bWR, bPnL, gDec, gWR, gPnL, gated));
        }

        double bWRAll = totBaseDec > 0 ? totBaseTP * 100.0 / totBaseDec : 0;
        double gWRAll = totGateDec > 0 ? totGateTP * 100.0 / totGateDec : 0;
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine("  REAL Lorentzian C# Engine — chart-data validation");
        Console.WriteLine($"  마진 ${MARGIN_USD:F0} × 레버리지 {LEVERAGE:F0}x = notional ${Notional:F0}");
        Console.WriteLine($"  TP +{TP_PCT}% (=${TpProfit:F2}/trade)  SL -{SL_PCT}% (=-${SlLoss:F2}/trade)  fee 0.04%×2");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"  Baseline (no gate): {totBaseDec} 진입, win-rate {bWRAll:F2}%, 누적PnL = ${totBasePnL:F2}");
        Console.WriteLine($"  + Lorentzian gate:  {totGateDec} 진입, win-rate {gWRAll:F2}%, 누적PnL = ${totGatePnL:F2}  (gated {totGated})");
        Console.WriteLine($"  Δ win-rate = {(gWRAll - bWRAll):+0.00;-0.00}%");
        Console.WriteLine($"  Δ PnL      = {(totGatePnL - totBasePnL):+$0.00;-$0.00}");
        if (totBaseDec > 0)
            Console.WriteLine($"  Baseline 평균PnL/진입 = ${(totBasePnL / totBaseDec):F2}");
        if (totGateDec > 0)
            Console.WriteLine($"  Gated  평균PnL/진입 = ${(totGatePnL / totGateDec):F2}");
        Console.WriteLine();
        Console.WriteLine("  [per-symbol — sorted by ΔPnL]");
        Console.WriteLine("  Symbol         bDec  bWR%    bPnL$    gDec  gWR%    gPnL$    gated   ΔPnL$");
        foreach (var p in perSym.OrderByDescending(p => p.gPnL - p.bPnL))
            Console.WriteLine($"  {p.sym,-14} {p.bDec,5} {p.bWR,6:F2} {p.bPnL,8:F2}  {p.gDec,5} {p.gWR,6:F2} {p.gPnL,8:F2}  {p.gated,5}   {(p.gPnL - p.bPnL),+8:F2}");
    }
}
