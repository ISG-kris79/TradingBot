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
using TradingBot.Tools.LorentzianValidator;

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
    public int FeatureCount { get; set; } = LorentzianFeatures.FeatureCount;
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

        // === D2. BB(20,2) 위치 구간별 PnL (5m) — "BB 상단 매수 = 손실" 검증 (production TP/SL) ===
        //   승률이 아닌 실제 손익으로 확인. TP +1.0% / SL -3.0% / WIN 24 (production ROE 15/45 @ 15x = 가격 1%/3%)
        Console.WriteLine();
        Console.WriteLine("=== D2. BB(20,2) %B 위치 구간별 PnL (5m, TP+1.0%/SL-3.0%/WIN24 = production) ===");
        Console.WriteLine("    %B: 0=하단밴드, 0.5=중심선(SMA20), 1.0=상단밴드, >1.0=밴드 위 돌파");
        decimal bbTpUsd = Notional * 1.0m / 100m - RoundTripFee;
        decimal bbSlUsd = Notional * 3.0m / 100m + RoundTripFee;
        var bbZones = new (string label, double lo, double hi)[]
        {
            ("하단 <0.2",     -99, 0.2),
            ("0.2-0.4",       0.2, 0.4),
            ("중단 0.4-0.6",  0.4, 0.6),
            ("0.6-0.8",       0.6, 0.8),
            ("상단 0.8-1.0",  0.8, 1.0),
            ("밴드위 >1.0",   1.0, 99),
        };
        var bbStat = new Dictionary<string, (int n, int w, decimal pnl)>();
        foreach (var z in bbZones) bbStat[z.label] = (0, 0, 0m);
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
                double bbPos = CalcBbPos(kl, i);
                var (tp, sl) = OutcomeIn(kl, i, 1.0m, 3.0m, 24);
                if (!(tp || sl)) continue;
                string bk = bbZones.First(z => bbPos >= z.lo && bbPos < z.hi).label;
                var s = bbStat[bk];
                s.n++;
                if (tp) { s.w++; s.pnl += bbTpUsd; } else s.pnl -= bbSlUsd;
                bbStat[bk] = s;
            }
        }
        Console.WriteLine("  BB위치(%B)    Trades  WinRate     PnL     avg/trade");
        foreach (var z in bbZones)
        {
            var v = bbStat[z.label];
            double wr = v.n > 0 ? v.w * 100.0 / v.n : 0;
            decimal avg = v.n > 0 ? v.pnl / v.n : 0m;
            string mark = v.pnl > 0 ? "✅" : "❌";
            Console.WriteLine($"  {z.label,-12}  {v.n,6}  {wr,6:F2}%  {v.pnl,9:F2}  {avg,7:F2}  {mark}");
        }

        // === D3. BB 게이트 임계 스윕 (EMA20↑ 유니버스, 큰 n) — 게이트를 0.5→0.8 올리면? ===
        //   D2는 Lorentzian 유니버스라 n이 작음. D3는 EMA20↑만으로 n을 키워 BB 게이트 임계의 PnL 효과를 직접 측정.
        //   각 임계: bbPos >= 임계인 진입만 취함. 현재 활성 게이트=0.5, 제안=0.8.
        Console.WriteLine();
        Console.WriteLine("=== D3. BB 게이트 임계 스윕 (EMA20↑ 진입, TP+1.0%/SL-3.0%/WIN24, 큰 n) ===");
        double[] bbThr = { -99, 0.5, 0.6, 0.7, 0.8, 0.9 };
        foreach (double thr in bbThr)
        {
            int gn = 0, gw = 0; decimal gpnl = 0m;
            foreach (var kv in symData)
            {
                string sym = kv.Key; var kl = kv.Value;
                int trainEnd = (int)(kl.Count * 0.7);
                for (int i = trainEnd + 50; i < kl.Count - 24; i++)
                {
                    if (!Ema20Rising(kl, i)) continue;
                    double bbPos = CalcBbPos(kl, i);
                    if (bbPos < thr) continue;
                    var (tp, sl) = OutcomeIn(kl, i, 1.0m, 3.0m, 24);
                    if (!(tp || sl)) continue;
                    gn++;
                    if (tp) { gw++; gpnl += bbTpUsd; } else gpnl -= bbSlUsd;
                }
            }
            double gwr = gn > 0 ? gw * 100.0 / gn : 0;
            decimal gavg = gn > 0 ? gpnl / gn : 0m;
            string lbl = thr < 0 ? "전체(게이트X)" : $"bbPos≥{thr:F1}";
            string mark = gpnl > 0 ? "✅" : "❌";
            Console.WriteLine($"  {lbl,-14}  n={gn,6}  WR={gwr,6:F2}%  PnL={gpnl,10:F2}  avg={gavg,7:F2}  {mark}");
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
    // BB(20,2) %B 위치: 0=하단밴드, 0.5=중심선(SMA20), 1.0=상단밴드, >1.0=밴드 위 돌파
    private static double CalcBbPos(List<IBinanceKline> kl, int i)
    {
        if (i < 20) return 0.5;
        double sma = 0;
        for (int q = i - 19; q <= i; q++) sma += (double)kl[q].ClosePrice;
        sma /= 20.0;
        double var2 = 0;
        for (int q = i - 19; q <= i; q++) { double d = (double)kl[q].ClosePrice - sma; var2 += d * d; }
        double sd = Math.Sqrt(var2 / 20.0);
        if (sd < 1e-12) return 0.5;
        double upper = sma + 2.0 * sd, lower = sma - 2.0 * sd;
        double last = (double)kl[i].ClosePrice;
        return (last - lower) / (upper - lower);
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

    // [v5.23.74] "급등 코인 진입이 흑자인가?" 검증 — 최근 급등 중소형 알트, 수직스파이크 vs 지속추세 구분
    private static async Task RunPumpRecentAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  PUMP-RECENT — 최근 급등 중소형 알트, 급등 시 진입이 흑자인가?");
        Console.WriteLine("  진입: 30분(6×5m) 누적 +4%↑ = 급등 진행 → 진입");
        Console.WriteLine("  분류: 수직스파이크(단일 5m봉 +3%↑) vs 지속추세(점진)");
        Console.WriteLine("  청산: production TP+1.0% / SL-3.0% / WIN 24 (1:3, 손익분기 WR 75%)");
        Console.WriteLine("================================================================");

        string[] pumpSyms = {
            "WLDUSDT","ENAUSDT","PENGUUSDT","ONDOUSDT","JUPUSDT","WIFUSDT","BONKUSDT","FLOKIUSDT",
            "SEIUSDT","TIAUSDT","STRKUSDT","ZKUSDT","WUSDT","PYTHUSDT","JTOUSDT","AEVOUSDT",
            "ETHFIUSDT","SAGAUSDT","VIRTUALUSDT","GRASSUSDT","AI16ZUSDT","MOODENGUSDT","POPCATUSDT",
            "PNUTUSDT","GOATUSDT","FARTCOINUSDT","ZROUSDT","NOTUSDT","ARKMUSDT","REZUSDT"
        };

        decimal tpUsd = Notional * 1.0m / 100m - RoundTripFee;
        decimal slUsd = Notional * 3.0m / 100m + RoundTripFee;
        const double PUMP_RISE = 4.0;   // 30분 누적 % 이상
        const double SPIKE_BAR = 3.0;   // 단일 5m봉 % 이상 = 수직 스파이크
        const int COOLDOWN = 12;        // 진입 후 봉 쿨다운 (같은 펌프 중복 진입 방지)

        int allN=0, allW=0; decimal allPnl=0m;
        int spkN=0, spkW=0; decimal spkPnl=0m;
        int trdN=0, trdW=0; decimal trdPnl=0m;

        int idx=0;
        foreach (var sym in pumpSyms)
        {
            idx++;
            Console.Write($"[fetch {idx}/{pumpSyms.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, 8); }   // 8페이지 ~40일
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); continue; }
            if (kl.Count < 200) { Console.WriteLine("skip(데이터부족)"); continue; }
            Console.WriteLine($"bars={kl.Count}");

            int lastEntry = -COOLDOWN;
            for (int i=6; i < kl.Count - 24; i++)
            {
                if (i - lastEntry < COOLDOWN) continue;
                double c0 = (double)kl[i-6].ClosePrice;
                double cn = (double)kl[i].ClosePrice;
                if (c0 <= 0) continue;
                double rise6 = (cn - c0)/c0*100.0;
                if (rise6 < PUMP_RISE) continue;
                double maxBar = 0;
                for (int q=i-5; q<=i; q++){ double o=(double)kl[q].OpenPrice; double cc=(double)kl[q].ClosePrice; if(o>0){ double m=(cc-o)/o*100.0; if(m>maxBar)maxBar=m; } }
                bool vertical = maxBar >= SPIKE_BAR;

                var (tp, sl) = OutcomeIn(kl, i, 1.0m, 3.0m, 24);
                if (!(tp || sl)) continue;   // timeout 제외 (기존 컨벤션)
                lastEntry = i;
                allN++; if(tp){allW++; allPnl+=tpUsd;} else allPnl-=slUsd;
                if (vertical){ spkN++; if(tp){spkW++; spkPnl+=tpUsd;} else spkPnl-=slUsd; }
                else { trdN++; if(tp){trdW++; trdPnl+=tpUsd;} else trdPnl-=slUsd; }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 결과 (급등 진입, production TP1%/SL3%, 손익분기 WR 75%) ===");
        void Row(string lbl, int n, int w, decimal pnl){ double wr=n>0?w*100.0/n:0; decimal avg=n>0?pnl/n:0m; string mk=pnl>0?"✅":"❌"; Console.WriteLine($"  {lbl,-16}  n={n,5}  WR={wr,6:F2}%  PnL={pnl,10:F2}  avg={avg,7:F2}  {mk}"); }
        Row("전체 급등진입", allN, allW, allPnl);
        Row("수직스파이크", spkN, spkW, spkPnl);
        Row("지속추세", trdN, trdW, trdPnl);
        Console.WriteLine();
        Console.WriteLine("  해석: 수직스파이크 적자 / 지속추세 흑자면 → '스파이크 제외 + 추세형 급등만' 진입이 답");
    }

    // [v5.23.74] 견고성 스윕 — StochRSI 과매도 임계 × MACD 조건, 더 많은 심볼/긴 기간 + 전후반 분할
    private static async Task RunUserSignalSweepAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  USER-SIGNAL-SWEEP — StochRSI 과매도골든 + MACD 견고성 검증");
        Console.WriteLine("  과매도 임계 {20,30,40} × MACD {line>sig, hist상승, macd>0}");
        Console.WriteLine("  45심볼 ~62일, production TP1%/SL3%/WIN24(1h), 손익분기 ~77%");
        Console.WriteLine("================================================================");

        string[] syms = {
            "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT","ADAUSDT","AVAXUSDT","LINKUSDT","DOTUSDT",
            "LTCUSDT","BCHUSDT","NEARUSDT","APTUSDT","ARBUSDT","OPUSDT","SUIUSDT","TIAUSDT","SEIUSDT","INJUSDT",
            "FETUSDT","WLDUSDT","ENAUSDT","ONDOUSDT","JUPUSDT","PEPEUSDT","WIFUSDT","ALGOUSDT","ATOMUSDT","FILUSDT",
            "UNIUSDT","AAVEUSDT","ICPUSDT","ETCUSDT","XLMUSDT","HBARUSDT","VETUSDT","RENDERUSDT","IMXUSDT","STXUSDT",
            "GALAUSDT","SANDUSDT","GRTUSDT","CRVUSDT","TAOUSDT"
        };
        decimal tpUsd = Notional * 1.0m / 100m - RoundTripFee;
        decimal slUsd = Notional * 3.0m / 100m + RoundTripFee;
        const int WIN1H = 24, COOLDOWN = 6;
        double[] osThrs = { 20, 30, 40 };
        string[] macdLbl = { "line>sig", "hist상승", "macd>0" };

        var acc = new Dictionary<string,(int n,int w,decimal p)>();
        foreach (var t in osThrs) foreach (var m in new[]{0,1,2}) acc[$"OS<{t}|{macdLbl[m]}"] = (0,0,0m);
        // 헤드라인(OS<30, line>sig) 전후반 분할
        int h0N=0,h0W=0; decimal h0P=0m; int h1N=0,h1W=0; decimal h1P=0m;

        int idx=0;
        foreach (var sym in syms)
        {
            idx++;
            Console.Write($"[{idx}/{syms.Length}] {sym} ");
            List<IBinanceKline> k5;
            try { k5 = await FetchKlinesAsync(sym, 12); }   // ~62일
            catch (Exception ex) { Console.WriteLine("fail:"+ex.Message); continue; }
            if (k5.Count < 600) { Console.WriteLine("skip"); continue; }
            var k1 = Aggregate1h(k5);
            if (k1.Count < 80) { Console.WriteLine("skip"); continue; }
            Console.WriteLine($"1h={k1.Count}");
            var closes = k1.Select(x=>(double)x.ClosePrice).ToArray();
            var (kk, dd) = StochRsiKD(closes, 14, 14, 3, 3);
            var (macd, sig) = MacdSeries(closes);
            int half = k1.Count/2;
            int last = -COOLDOWN;
            for (int i=2; i<k1.Count-WIN1H; i++)
            {
                bool golden = kk[i-1] <= dd[i-1] && kk[i] > dd[i];
                if (!golden) continue;
                if (i-last < COOLDOWN) continue;
                var (tp, sl) = OutcomeIn(k1, i, 1.0m, 3.0m, WIN1H);
                if (!(tp||sl)) continue;
                last = i;
                double hist = macd[i]-sig[i], histPrev = macd[i-1]-sig[i-1];
                bool[] macdOk = { macd[i]>sig[i], (hist>histPrev && hist>0), macd[i]>0 };
                foreach (var t in osThrs)
                {
                    if (kk[i] >= t) continue;
                    for (int m=0;m<3;m++)
                    {
                        if (!macdOk[m]) continue;
                        var s = acc[$"OS<{t}|{macdLbl[m]}"]; s.n++; if(tp){s.w++; s.p+=tpUsd;} else s.p-=slUsd; acc[$"OS<{t}|{macdLbl[m]}"]=s;
                    }
                }
                if (kk[i] < 30 && macd[i] > sig[i])
                {
                    if (i < half) { h0N++; if(tp){h0W++; h0P+=tpUsd;} else h0P-=slUsd; }
                    else          { h1N++; if(tp){h1W++; h1P+=tpUsd;} else h1P-=slUsd; }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 스윕 결과 (production TP1%/SL3%, 손익분기 ~77%) ===");
        Console.WriteLine("  config                 n      WR        PnL      avg");
        foreach (var kv in acc.OrderByDescending(x=>x.Value.n>0?x.Value.p/x.Value.n:-999m))
        {
            var v = kv.Value; double wr=v.n>0?v.w*100.0/v.n:0; decimal avg=v.n>0?v.p/v.n:0m; string mk=v.p>0?"✅":"❌";
            Console.WriteLine($"  {kv.Key,-20} {v.n,5}  {wr,6:F2}%  {v.p,9:F2}  {avg,7:F2}  {mk}");
        }
        Console.WriteLine();
        Console.WriteLine("=== 헤드라인(OS<30, line>sig) 전후반 안정성 ===");
        void HRow(string l,int n,int w,decimal p){ double wr=n>0?w*100.0/n:0; decimal a=n>0?p/n:0m; string mk=p>0?"✅":"❌"; Console.WriteLine($"  {l,-10}  n={n,5}  WR={wr,6:F2}%  PnL={p,9:F2}  avg={a,7:F2}  {mk}"); }
        HRow("전반기", h0N,h0W,h0P);
        HRow("후반기", h1N,h1W,h1P);
        Console.WriteLine();
        Console.WriteLine("  판정: 흑자 config가 여러 개 + 전후반 모두 흑자면 → 엣지 견고. 한쪽만 흑자면 우연 의심.");
    }

    // [v5.23.74] 사용자 지표 검증 — 1h StochRSI 골든크로스 + MACD + 체결강도(Volume Power)
    private static async Task RunUserSignalAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  USER-SIGNAL — 1h StochRSI 골든크로스 + MACD + 체결강도 진입 검증");
        Console.WriteLine("  StochRSI(14,14,3,3) %K>%D 교차 / MACD(12,26,9) line>signal / 체결강도 taker_buy/vol");
        Console.WriteLine("  청산: production TP+1.0% / SL-3.0% / WIN 24봉(1h) — 1:3, 손익분기 WR 75%");
        Console.WriteLine("================================================================");

        string[] syms = {
            "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT","ADAUSDT","AVAXUSDT","LINKUSDT","DOTUSDT",
            "LTCUSDT","BCHUSDT","NEARUSDT","APTUSDT","ARBUSDT","OPUSDT","SUIUSDT","TIAUSDT","SEIUSDT","INJUSDT",
            "FETUSDT","WLDUSDT","ENAUSDT","ONDOUSDT","JUPUSDT","PEPEUSDT","WIFUSDT","ALGOUSDT","ATOMUSDT","FILUSDT"
        };
        decimal tpUsd = Notional * 1.0m / 100m - RoundTripFee;
        decimal slUsd = Notional * 3.0m / 100m + RoundTripFee;
        const double VP_MIN = 0.55;   // 체결강도 최소 (taker buy 비율)
        const int WIN1H = 24;
        const int COOLDOWN = 6;

        int aN=0,aW=0; decimal aP=0m;   // A: StochRSI 골든 단독
        int bN=0,bW=0; decimal bP=0m;   // B: + MACD
        int cN=0,cW=0; decimal cP=0m;   // C: + 체결강도 (풀조합)
        int dN=0,dW=0; decimal dP=0m;   // D: 상승 다이버전스(RSI) 단독
        int eN=0,eW=0; decimal eP=0m;   // E: 다이버전스 + 체결강도

        int idx=0;
        foreach (var sym in syms)
        {
            idx++;
            Console.Write($"[fetch {idx}/{syms.Length}] {sym} ");
            List<IBinanceKline> k5;
            try { k5 = await FetchKlinesAsync(sym, 10); }   // 10페이지 ~52일 5m
            catch (Exception ex) { Console.WriteLine("fail: "+ex.Message); continue; }
            if (k5.Count < 600) { Console.WriteLine("skip"); continue; }
            var k1 = Aggregate1h(k5);
            if (k1.Count < 60) { Console.WriteLine("skip(1h부족)"); continue; }
            Console.WriteLine($"1h봉={k1.Count}");

            var closes = k1.Select(x => (double)x.ClosePrice).ToArray();
            var lows = k1.Select(x => (double)x.LowPrice).ToArray();
            var (kk, dd) = StochRsiKD(closes, 14, 14, 3, 3);
            var (macd, sig) = MacdSeries(closes);
            var divSig = BullishDivergence(closes, lows, 3, 30);   // 상승 다이버전스(LL price / HL RSI)

            int last = -COOLDOWN;
            for (int i=2; i < k1.Count - WIN1H; i++)
            {
                if (i - last < COOLDOWN) continue;
                bool golden = kk[i-1] <= dd[i-1] && kk[i] > dd[i] && kk[i] < 30;   // StochRSI 과매도권(<30) 골든크로스 = 강력 매수
                if (!golden) continue;
                bool macdBull = macd[i] > sig[i];
                double vol = (double)k1[i].Volume;
                double vp = vol > 0 ? (double)k1[i].TakerBuyBaseVolume / vol : 0;
                bool vpHigh = vp >= VP_MIN;

                var (tp, sl) = OutcomeIn(k1, i, 1.0m, 3.0m, WIN1H);
                if (!(tp || sl)) continue;
                last = i;
                aN++; if(tp){aW++; aP+=tpUsd;} else aP-=slUsd;
                if (macdBull) { bN++; if(tp){bW++; bP+=tpUsd;} else bP-=slUsd; }
                if (macdBull && vpHigh) { cN++; if(tp){cW++; cP+=tpUsd;} else cP-=slUsd; }
            }

            // 상승 다이버전스 진입 루프 (별도 신호 — 골든크로스와 진입 시점 다름)
            int lastD = -COOLDOWN;
            for (int i=0; i < k1.Count - WIN1H; i++)
            {
                if (!divSig[i]) continue;
                if (i - lastD < COOLDOWN) continue;
                var (tp, sl) = OutcomeIn(k1, i, 1.0m, 3.0m, WIN1H);
                if (!(tp || sl)) continue;
                lastD = i;
                double vol2 = (double)k1[i].Volume;
                double vp2 = vol2 > 0 ? (double)k1[i].TakerBuyBaseVolume / vol2 : 0;
                dN++; if(tp){dW++; dP+=tpUsd;} else dP-=slUsd;
                if (vp2 >= VP_MIN) { eN++; if(tp){eW++; eP+=tpUsd;} else eP-=slUsd; }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 결과 (production TP1%/SL3%, 손익분기 WR 75%) ===");
        void Row(string lbl, int n, int w, decimal pnl){ double wr=n>0?w*100.0/n:0; decimal avg=n>0?pnl/n:0m; string mk=pnl>0?"✅":"❌"; Console.WriteLine($"  {lbl,-30}  n={n,5}  WR={wr,6:F2}%  PnL={pnl,10:F2}  avg={avg,7:F2}  {mk}"); }
        Row("A. StochRSI 과매도골든 단독", aN, aW, aP);
        Row("B. + MACD line>signal", bN, bW, bP);
        Row("C. + 체결강도>=0.55 (풀조합)", cN, cW, cP);
        Row("D. 상승 다이버전스(RSI) 단독", dN, dW, dP);
        Row("E. 다이버전스 + 체결강도", eN, eW, eP);
        Console.WriteLine();
        Console.WriteLine("  비교: 현재 봇 실거래 건당 +$2.17 (30일). 풀조합 avg 가 이보다 높고 흑자면 → 도입 가치 있음");
    }

    private static List<IBinanceKline> Aggregate1h(List<IBinanceKline> k5)
    {
        var outp = new List<IBinanceKline>();
        SimpleKline? cur = null;
        foreach (var b in k5)
        {
            bool newHour = b.OpenTime.Minute == 0;   // UTC 정시 정렬
            if (cur == null || newHour)
            {
                if (cur != null) outp.Add(cur);
                cur = new SimpleKline {
                    OpenTime = b.OpenTime, OpenPrice = b.OpenPrice, HighPrice = b.HighPrice,
                    LowPrice = b.LowPrice, ClosePrice = b.ClosePrice, Volume = b.Volume,
                    CloseTime = b.CloseTime, TakerBuyBaseVolume = b.TakerBuyBaseVolume
                };
            }
            else
            {
                if (b.HighPrice > cur.HighPrice) cur.HighPrice = b.HighPrice;
                if (b.LowPrice < cur.LowPrice) cur.LowPrice = b.LowPrice;
                cur.ClosePrice = b.ClosePrice;
                cur.Volume += b.Volume;
                cur.TakerBuyBaseVolume += b.TakerBuyBaseVolume;
                cur.CloseTime = b.CloseTime;
            }
        }
        if (cur != null) outp.Add(cur);
        return outp;
    }

    private static double[] EmaArr(double[] d, int p)
    {
        var e = new double[d.Length]; if (d.Length==0) return e;
        double k = 2.0/(p+1); e[0]=d[0];
        for (int i=1;i<d.Length;i++) e[i]=d[i]*k + e[i-1]*(1-k);
        return e;
    }
    private static (double[] macd, double[] signal) MacdSeries(double[] closes)
    {
        var ef = EmaArr(closes, 12); var es = EmaArr(closes, 26);
        var m = new double[closes.Length];
        for (int i=0;i<closes.Length;i++) m[i]=ef[i]-es[i];
        return (m, EmaArr(m, 9));
    }
    private static double[] RsiArr(double[] closes, int period)
    {
        int n=closes.Length; var r=new double[n];
        for (int i=0;i<n;i++){
            if (i<period){ r[i]=50; continue; }
            double g=0,l=0;
            for (int q=i-period+1;q<=i;q++){ double dd=closes[q]-closes[q-1]; if(dd>0)g+=dd; else l-=dd; }
            double ag=g/period, al=l/period;
            r[i] = al<1e-12 ? 100 : 100 - 100/(1+ag/al);
        }
        return r;
    }
    private static (double[] k, double[] d) StochRsiKD(double[] closes, int rsiLen, int stochLen, int kSmooth, int dSmooth)
    {
        var rsi = RsiArr(closes, rsiLen);
        int n = closes.Length;
        var stoch = new double[n];
        for (int i=0;i<n;i++){
            if (i < rsiLen + stochLen){ stoch[i]=50; continue; }
            double mn=double.MaxValue, mx=double.MinValue;
            for (int q=i-stochLen+1;q<=i;q++){ if(rsi[q]<mn)mn=rsi[q]; if(rsi[q]>mx)mx=rsi[q]; }
            stoch[i] = (mx-mn)<1e-12 ? 50 : (rsi[i]-mn)/(mx-mn)*100.0;
        }
        var k = SmaArr(stoch, kSmooth);
        return (k, SmaArr(k, dSmooth));
    }
    private static double[] SmaArr(double[] x, int p)
    {
        int n=x.Length; var o=new double[n];
        for (int i=0;i<n;i++){
            if (i<p-1){ o[i]=x[i]; continue; }
            double s=0; for(int q=i-p+1;q<=i;q++) s+=x[q]; o[i]=s/p;
        }
        return o;
    }
    // 상승 다이버전스: 가격은 직전 피벗저점보다 낮은 저점(LL), RSI는 높은 저점(HL) → 반전. 피벗 확인 시점에 진입 신호.
    private static bool[] BullishDivergence(double[] closes, double[] lows, int pivLen, int maxGap)
    {
        int n = closes.Length;
        var rsi = RsiArr(closes, 14);
        var sig = new bool[n];
        int prevIdx = -1; double prevLow = 0, prevRsi = 0;
        for (int i = pivLen; i < n - pivLen; i++)
        {
            bool piv = true;   // i 가 피벗저점인가 (좌우 pivLen 봉 중 최저)
            for (int q = i-pivLen; q <= i+pivLen; q++) { if (lows[q] < lows[i]) { piv = false; break; } }
            if (!piv) continue;
            if (prevIdx >= 0 && (i - prevIdx) <= maxGap)
            {
                bool priceLL = lows[i] < prevLow;       // 가격 저점 낮아짐
                bool rsiHL   = rsi[i]  > prevRsi;        // RSI 저점 높아짐
                if (priceLL && rsiHL)
                {
                    int entry = i + pivLen;              // 피벗 확정 시점 진입 (look-ahead 없음)
                    if (entry < n) sig[entry] = true;
                }
            }
            prevIdx = i; prevLow = lows[i]; prevRsi = rsi[i];
        }
        return sig;
    }

    // [v5.23.74] 3년치 일별/월별 수익 보고서 — StochRSI 과매도골든 + MACD 트리거 로직 점검 (기존 FetchKlines1hAsync 재사용)
    private static async Task RunReport3yrAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  3년 보고서 — StochRSI(1h) 과매도<30 골든크로스 + MACD line>signal");
        Console.WriteLine("  청산 TP+1.0%/SL-3.0%/WIN24(1h) · 마진 $100 × 10x · 수수료 0.04%×2");
        Console.WriteLine("================================================================");

        string[] syms = {
            "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT","ADAUSDT","AVAXUSDT","LINKUSDT","DOTUSDT",
            "LTCUSDT","BCHUSDT","ATOMUSDT","ETCUSDT","XLMUSDT","NEARUSDT","FILUSDT","UNIUSDT","AAVEUSDT","ICPUSDT",
            "ALGOUSDT","SANDUSDT","GALAUSDT","GRTUSDT","VETUSDT"
        };
        decimal tpUsd = Notional * 1.0m / 100m - RoundTripFee;
        decimal slUsd = Notional * 3.0m / 100m + RoundTripFee;
        const int WIN1H = 24, COOLDOWN = 6;

        var trades = new List<(DateTime t, decimal pnl, bool win)>();
        int idx=0;
        foreach (var sym in syms)
        {
            idx++;
            Console.Write($"[{idx}/{syms.Length}] {sym} ");
            List<IBinanceKline> k1;
            try { k1 = await FetchKlines1hAsync(sym, 28); }   // ~3.2년
            catch (Exception ex) { Console.WriteLine("fail:"+ex.Message); continue; }
            if (k1.Count < 200) { Console.WriteLine("skip"); continue; }
            Console.WriteLine($"1h={k1.Count} ({k1[0].OpenTime:yyyy-MM-dd}~{k1[^1].OpenTime:yyyy-MM-dd})");
            var closes = k1.Select(x=>(double)x.ClosePrice).ToArray();
            var (kk, dd) = StochRsiKD(closes, 14, 14, 3, 3);
            var (macd, sig) = MacdSeries(closes);
            int last=-COOLDOWN;
            for (int i=2;i<k1.Count-WIN1H;i++)
            {
                bool golden = kk[i-1] <= dd[i-1] && kk[i] > dd[i] && kk[i] < 30;
                if (!golden) continue;
                if (macd[i] <= sig[i]) continue;   // MACD bullish
                if (i-last < COOLDOWN) continue;
                var (tp, sl) = OutcomeIn(k1, i, 1.0m, 3.0m, WIN1H);
                if (!(tp||sl)) continue;
                last=i;
                trades.Add((k1[i].OpenTime, tp ? tpUsd : -slUsd, tp));
            }
        }

        var months = new SortedDictionary<string,(int n,int w,decimal p)>(StringComparer.Ordinal);
        var days = new SortedDictionary<string,(int n,int w,decimal p)>(StringComparer.Ordinal);
        foreach (var t in trades)
        {
            string mk = t.t.ToString("yyyy-MM"), dk = t.t.ToString("yyyy-MM-dd");
            var mv = months.TryGetValue(mk, out var m0) ? m0 : (n:0,w:0,p:0m); mv.n++; if(t.win)mv.w++; mv.p+=t.pnl; months[mk]=mv;
            var dv = days.TryGetValue(dk, out var d0) ? d0 : (n:0,w:0,p:0m); dv.n++; if(t.win)dv.w++; dv.p+=t.pnl; days[dk]=dv;
        }

        Console.WriteLine();
        Console.WriteLine("=== 월별 보고서 (수익률 = 마진 $100 기준) ===");
        Console.WriteLine("  월        거래   승률     수익금($)    수익률(%)    누적($)");
        decimal cum=0m;
        foreach (var kv in months)
        {
            var v=kv.Value; double wr=v.n>0?v.w*100.0/v.n:0; decimal roi=v.p/MARGIN_USD*100m; cum+=v.p;
            string mk=v.p>0?"✅":"❌";
            Console.WriteLine($"  {kv.Key}   {v.n,4}  {wr,6:F2}%  {v.p,10:F2}  {roi,8:F1}%  {cum,10:F2}  {mk}");
        }

        int totN=trades.Count, totW=trades.Count(x=>x.win); decimal totP=trades.Sum(x=>x.pnl);
        int profM=months.Count(x=>x.Value.p>0), profD=days.Count(x=>x.Value.p>0);
        Console.WriteLine();
        Console.WriteLine("=== 요약 ===");
        Console.WriteLine($"  총 거래: {totN}  승률: {(totN>0?totW*100.0/totN:0):F2}%  총 수익금: ${totP:F2}  총 수익률(마진): {totP/MARGIN_USD*100m:F0}%");
        Console.WriteLine($"  흑자 월: {profM}/{months.Count}  흑자 일: {profD}/{days.Count}");
        if (months.Count>0){ var best=months.OrderByDescending(x=>x.Value.p).First(); var worst=months.OrderBy(x=>x.Value.p).First();
            Console.WriteLine($"  최고 월: {best.Key} ${best.Value.p:F2}  /  최악 월: {worst.Key} ${worst.Value.p:F2}"); }

        var sb = new System.Text.StringBuilder("Date,Trades,Wins,PnL_USD\n");
        foreach (var kv in days) sb.Append($"{kv.Key},{kv.Value.n},{kv.Value.w},{kv.Value.p:F2}\n");
        System.IO.File.WriteAllText("report-3yr-daily.csv", sb.ToString());
        Console.WriteLine($"\n  일별 상세 → report-3yr-daily.csv ({days.Count}일)");
    }

    // [v5.23.74] StochRSI 과매도골든+MACD 신호 × TP/SL 손익비 스윕 — "흑자 되는 손익비" 탐색 + 견고성
    private static async Task RunUserSignalTpslAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  TP/SL 손익비 스윕 — StochRSI(1h)과매도<30 골든 + MACD line>signal 고정");
        Console.WriteLine("  손익비 1:3 ~ 3:1, ~3년 1h, WIN48 · 마진$100×10x · 전후반 견고성");
        Console.WriteLine("================================================================");

        string[] syms = {
            "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT","ADAUSDT","AVAXUSDT","LINKUSDT","DOTUSDT",
            "LTCUSDT","BCHUSDT","ATOMUSDT","ETCUSDT","XLMUSDT","NEARUSDT","FILUSDT","UNIUSDT","AAVEUSDT","ICPUSDT",
            "ALGOUSDT","SANDUSDT","GALAUSDT","GRTUSDT","VETUSDT"
        };
        var cfgs = new (decimal tp, decimal sl)[] {
            (3.0m,1.0m),(2.0m,1.0m),(1.5m,1.0m),(2.0m,2.0m),(1.0m,1.0m),(1.5m,1.5m),
            (1.0m,1.5m),(2.0m,3.0m),(1.5m,3.0m),(1.0m,2.0m),(1.0m,3.0m),(0.5m,1.5m)
        };
        const int WIN1H = 48, COOLDOWN = 6;
        int K = cfgs.Length;
        var n=new int[K]; var w=new int[K]; var pnl=new decimal[K];
        var h0=new decimal[K]; var h1=new decimal[K];
        var tpU=new decimal[K]; var slU=new decimal[K];
        for (int c=0;c<K;c++){ tpU[c]=Notional*cfgs[c].tp/100m-RoundTripFee; slU[c]=Notional*cfgs[c].sl/100m+RoundTripFee; }

        int idx=0;
        foreach (var sym in syms)
        {
            idx++; Console.Write($"[{idx}/{syms.Length}] {sym} ");
            List<IBinanceKline> k1;
            try { k1 = await FetchKlines1hAsync(sym, 24); } catch (Exception ex) { Console.WriteLine("fail:"+ex.Message); continue; }
            if (k1.Count < 300) { Console.WriteLine("skip"); continue; }
            Console.WriteLine($"1h={k1.Count}");
            var closes = k1.Select(x=>(double)x.ClosePrice).ToArray();
            var (kk, dd) = StochRsiKD(closes, 14, 14, 3, 3);
            var (macd, sig) = MacdSeries(closes);
            int half = k1.Count/2; int last=-COOLDOWN;
            for (int i=2;i<k1.Count-WIN1H;i++)
            {
                bool golden = kk[i-1] <= dd[i-1] && kk[i] > dd[i] && kk[i] < 30;
                if (!golden) continue;
                if (macd[i] <= sig[i]) continue;
                if (i-last < COOLDOWN) continue;
                last=i;
                for (int c=0;c<K;c++)
                {
                    var (t, s) = OutcomeIn(k1, i, cfgs[c].tp, cfgs[c].sl, WIN1H);
                    if (!(t||s)) continue;
                    decimal pl = t ? tpU[c] : -slU[c];
                    n[c]++; if(t)w[c]++; pnl[c]+=pl;
                    if (i < half) h0[c]+=pl; else h1[c]+=pl;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== StochRSI 과매도골든+MACD × TP/SL 손익비 (1h, ~3년) ===");
        Console.WriteLine("  TP/SL    손익분기   거래   승률       PnL      avg    전후반        ");
        foreach (var c in Enumerable.Range(0,K).OrderByDescending(c=>pnl[c]))
        {
            double wr = n[c]>0?w[c]*100.0/n[c]:0; decimal avg = n[c]>0?pnl[c]/n[c]:0m;
            decimal be = cfgs[c].sl/(cfgs[c].tp+cfgs[c].sl)*100m;
            string rob = (h0[c]>0&&h1[c]>0)?"✅견고":(h0[c]>0||h1[c]>0)?"⚠️한쪽만":"❌둘다적자";
            string mk = pnl[c]>0?"✅":"❌";
            Console.WriteLine($"  {cfgs[c].tp:F1}/{cfgs[c].sl:F1}    {be,5:F0}%   {n[c],5}  {wr,6:F2}%  {pnl[c],9:F2}  {avg,6:F2}  {rob}  {mk}");
        }
        Console.WriteLine();
        Console.WriteLine("  판정: 흑자 + 전후반 모두 흑자(✅견고) 손익비가 있으면 → StochRSI 신호 도입 가능.");
        Console.WriteLine("  ※ 현재 production 은 0.5/1.5(1:3). TP/SL 실제 변경은 사용자 승인 후 (메모리 규칙).");
    }

    // [v5.23.74] BB_WALK/SQUEEZE 확장 — WR 유지하며 진입 늘리는 파라미터 탐색 (유일한 진짜 엣지 넓히기)
    private static async Task RunBbExpandAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine($"  BB-EXPAND — BB_WALK/SQUEEZE 트리거 파라미터 스윕 (5m, {BbExpandPages}p=~{BbExpandPages*1500*5/60/24}일)");
        Console.WriteLine("  목표: WR 88%+ 유지하며 진입 ↑. production TP1.0%/SL3.0%/WIN24, 전후반 견고성");
        Console.WriteLine("================================================================");

        string[] syms = {
            "DOGEUSDT","ADAUSDT","AVAXUSDT","LINKUSDT","DOTUSDT","LTCUSDT","BCHUSDT","NEARUSDT","APTUSDT","ARBUSDT",
            "OPUSDT","SUIUSDT","TIAUSDT","SEIUSDT","INJUSDT","FETUSDT","WLDUSDT","ENAUSDT","ONDOUSDT","JUPUSDT",
            "PEPEUSDT","WIFUSDT","ALGOUSDT","ATOMUSDT","FILUSDT","UNIUSDT","AAVEUSDT","ICPUSDT","ETCUSDT","XLMUSDT",
            "GALAUSDT","SANDUSDT","GRTUSDT","CRVUSDT","RENDERUSDT"
        };
        var cfgs = new (string label, Func<List<IBinanceKline>,int,bool> trig)[] {
            ("SQZ w<1.5(현재)",   (kl,i)=> i>=20 && BBWidth(kl,i)<1.5 && BBWalkUpper(kl,i)),
            ("SQZ w<2.0",        (kl,i)=> i>=20 && BBWidth(kl,i)<2.0 && BBWalkUpper(kl,i)),
            ("SQZ w<2.5",        (kl,i)=> i>=20 && BBWidth(kl,i)<2.5 && BBWalkUpper(kl,i)),
            ("SQZ w<1.0",        (kl,i)=> i>=20 && BBWidth(kl,i)<1.0 && BBWalkUpper(kl,i)),
            ("WALK 4/5(현재)",    (kl,i)=> i>=20 && BBWalkStreak(kl,i,5)>=4),
            ("WALK 3/5",         (kl,i)=> i>=20 && BBWalkStreak(kl,i,5)>=3),
            ("WALK 4/6",         (kl,i)=> i>=20 && BBWalkStreak(kl,i,6)>=4),
            ("WALK 5/7",         (kl,i)=> i>=20 && BBWalkStreak(kl,i,7)>=5),
            ("WALK 3/4",         (kl,i)=> i>=20 && BBWalkStreak(kl,i,4)>=3),
            ("SQZ<2.0||WALK4/5", (kl,i)=> i>=20 && ((BBWidth(kl,i)<2.0 && BBWalkUpper(kl,i)) || BBWalkStreak(kl,i,5)>=4)),
        };
        decimal tpUsd = Notional*1.0m/100m - RoundTripFee;
        decimal slUsd = Notional*3.0m/100m + RoundTripFee;
        const int WIN=24, COOLDOWN=12;
        int K=cfgs.Length;
        var n=new int[K]; var w=new int[K]; var pnl=new decimal[K]; var h0=new decimal[K]; var h1=new decimal[K]; var last=new int[K];
        int idx=0;
        foreach (var sym in syms)
        {
            idx++; Console.Write($"[{idx}/{syms.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, BbExpandPages); } catch (Exception ex) { Console.WriteLine("fail:"+ex.Message); continue; }
            if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
            double days = (kl[^1].OpenTime - kl[0].OpenTime).TotalDays;
            Console.WriteLine($"5m={kl.Count} ({days:F0}일, {kl[0].OpenTime:yyyy-MM-dd}~)");
            int half = kl.Count/2;
            for (int c=0;c<K;c++) last[c]=-COOLDOWN;
            for (int i=20;i<kl.Count-WIN;i++)
                for (int c=0;c<K;c++)
                {
                    if (i-last[c] < COOLDOWN) continue;
                    if (!cfgs[c].trig(kl,i)) continue;
                    var (tp, sl) = OutcomeIn(kl, i, 1.0m, 3.0m, WIN);
                    if (!(tp||sl)) continue;
                    last[c]=i;
                    decimal pl = tp ? tpUsd : -slUsd;
                    n[c]++; if(tp)w[c]++; pnl[c]+=pl;
                    if (i<half) h0[c]+=pl; else h1[c]+=pl;
                }
        }
        Console.WriteLine();
        Console.WriteLine("=== BB 트리거 확장 스윕 (TP1%/SL3%, 손익분기 75%) ===");
        Console.WriteLine("  config              거래    승률       PnL      avg    전후반");
        foreach (var c in Enumerable.Range(0,K).OrderByDescending(c=>pnl[c]))
        {
            double wr = n[c]>0?w[c]*100.0/n[c]:0; decimal avg=n[c]>0?pnl[c]/n[c]:0m;
            string rob = (h0[c]>0&&h1[c]>0)?"✅견고":(h0[c]>0||h1[c]>0)?"⚠️한쪽":"❌둘다적자";
            string mk = pnl[c]>0?"✅":"❌";
            Console.WriteLine($"  {cfgs[c].label,-18} {n[c],6}  {wr,6:F2}%  {pnl[c],9:F2}  {avg,6:F2}  {rob}  {mk}");
        }
        Console.WriteLine();
        Console.WriteLine("  찾는 것: '현재'보다 거래 많고 + WR 88%+ + ✅견고 인 config → 그걸로 확장 도입.");
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

    // [v5.23.67] BB mid/upper 동시 반환 (리테스트 판정용)
    private static void BbMidUpper(List<IBinanceKline> kl, int i, out double mid, out double upper)
    {
        double sum = 0; for (int j = i - 19; j <= i; j++) sum += (double)kl[j].ClosePrice;
        mid = sum / 20.0;
        double sq = 0;
        for (int j = i - 19; j <= i; j++) { double d = (double)kl[j].ClosePrice - mid; sq += d * d; }
        double sd = Math.Sqrt(sq / 20.0);
        upper = mid + 2 * sd;
    }

    // [v5.23.67] RETEST: 직전 6봉 내 BB 상단 돌파 발생 + 현재봉이 mid 로 되돌림 터치 후 회복(양봉)
    //   = 돌파 추격이 아니라 돌파 후 눌림에서 진입 → 진입가 낮음 + 눌림에 안 털림
    private static bool RetestSetup(List<IBinanceKline> kl, int i)
    {
        if (i < 26) return false;
        bool breakoutRecent = false;
        for (int q = i - 6; q < i; q++)
        {
            if (q < 20) continue;
            BbMidUpper(kl, q, out _, out double up);
            if ((double)kl[q].ClosePrice >= up) { breakoutRecent = true; break; }
        }
        if (!breakoutRecent) return false;
        BbMidUpper(kl, i, out double mid, out _);
        double low = (double)kl[i].LowPrice, close = (double)kl[i].ClosePrice, open = (double)kl[i].OpenPrice;
        return low <= mid && close > mid && close > open;   // 되돌림 터치 후 회복 양봉
    }

    // [v5.23.67] PULLBACK: EMA20 상승 추세 + 직전 5봉 고점 대비 1~4% 되돌림 + 반등 양봉
    private static bool PullbackSetup(List<IBinanceKline> kl, int i)
    {
        if (i < 26) return false;
        if (!Ema20Rising(kl, i)) return false;
        double hi5 = double.MinValue;
        for (int q = i - 5; q < i; q++) hi5 = Math.Max(hi5, (double)kl[q].HighPrice);
        double low = (double)kl[i].LowPrice, close = (double)kl[i].ClosePrice, open = (double)kl[i].OpenPrice;
        double pullPct = hi5 > 0 ? (hi5 - low) / hi5 * 100.0 : 0;
        return pullPct >= 1.0 && pullPct <= 4.0 && close > open;   // 눌림 후 반등 양봉
    }

    // [v5.23.67] 진입 타이밍 비교 — BREAKOUT(현재) vs RETEST vs PULLBACK
    //   사용자 지적: BB_WALK/SQUEEZE 가 고점 추격 → 진입 직후 눌림에 손절. 눌림에서 진입하면?
    //   같은 알트셋·같은 TP/SL(1.0/3.0/24)·같은 추세가드(EMA20↑)로 진입 타이밍만 교체 비교.
    private static async Task RunEntryTimingCompareAsync(int pages)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  진입 타이밍 비교: BREAKOUT(현재) vs RETEST vs PULLBACK");
        Console.WriteLine("  알트만 / TP 1.0% SL 3.0% win 24 / EMA20↑ 공통 가드");
        Console.WriteLine("================================================================");
        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            if (majors.Contains(sym)) continue;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count})");
            }
            catch (Exception ex) { Console.WriteLine("fail " + ex.Message); }
        }

        var methods = new (string name, Func<List<IBinanceKline>, int, bool> ok, int rsiMax)[]
        {
            ("BREAKOUT_현재", (kl, i) => i >= 25 && (BBWalkStreak(kl, i, 5) >= 4 || (BBWidth(kl, i) < 1.5 && BBWalkUpper(kl, i))), 65),
            ("RETEST",        (kl, i) => RetestSetup(kl, i), 70),
            ("PULLBACK",      (kl, i) => PullbackSetup(kl, i), 70),
        };

        // TP/SL/레버리지 조합 — 검증값 vs 사용자 실제 설정 비교
        //   사용자 설정: PumpTp1Roe 30/PumpStopLossRoe 75 @15x = 가격 TP 2% / SL 5%
        //   win 은 TP 폭 비례로 (1%→24봉=2h, 2%→48봉=4h) 도달 기회 동등화
        var tpslSets = new (decimal tp, decimal sl, decimal lev, int win, string label)[]
        {
            (1.0m, 3.0m, 5m, 24, "검증값 TP1%/SL3% @5x (2h)"),
            (2.0m, 5.0m, 15m, 48, "사용자 TP2%/SL5% @15x (4h)"),
        };

        foreach (var ts in tpslSets)
        {
            decimal notional = 100m * ts.lev;
            decimal fee = notional * 0.0004m * 2m;
            decimal tpUsd = notional * ts.tp / 100m - fee;
            decimal slUsd = notional * ts.sl / 100m + fee;
            Console.WriteLine();
            Console.WriteLine($"━━━ {ts.label} (notional ${notional:F0}, TP +${tpUsd:F2} / SL -${slUsd:F2}) ━━━");
            Console.WriteLine($"{"방식",-16} {"진입",6} {"승",5} {"승률",8} {"순PnL",12} {"건당",8}");
            Console.WriteLine(new string('-', 62));
            foreach (var m in methods)
            {
                int n = 0, w = 0; decimal pnl = 0m;
                foreach (var kv in fullData)
                {
                    var kl = kv.Value;
                    for (int i = 50; i < kl.Count - ts.win; i++)
                    {
                        if (!m.ok(kl, i)) continue;
                        if (!Ema20Rising(kl, i)) continue;
                        if (CalcRsi14(kl, i) >= m.rsiMax) continue;
                        var (tp, sl) = OutcomeIn(kl, i, ts.tp, ts.sl, ts.win);
                        if (!(tp || sl)) continue;
                        n++;
                        if (tp) { w++; pnl += tpUsd; } else { pnl -= slUsd; }
                    }
                }
                double wr = n > 0 ? w * 100.0 / n : 0;
                decimal per = n > 0 ? pnl / n : 0m;
                Console.WriteLine($"{m.name,-16} {n,6} {w,5} {wr,7:F2}% {pnl,11:F2} {per,7:F3}");
            }
            Console.WriteLine(new string('-', 62));
        }
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
    private static int BbExpandPages = 12;   // --pages N CLI 로 override (12=~62일, 211=~3년)
    private static bool SkipKnn = false;     // --no-knn: KNN precompute 생략(장기탐색 가속, 승리조합에 KNN 없음)
    private static bool UseMajors = false;    // --majors: 배터리를 대형주 universe로
    private static bool UseRegime = false;    // --regime: BTC 상승장(종가>EMA200)일 때만 진입 카운트
    private static readonly string[] LargeCaps = {
        "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT","ADAUSDT","AVAXUSDT","LINKUSDT","TRXUSDT",
        "LTCUSDT","BCHUSDT","DOTUSDT","UNIUSDT","ATOMUSDT","ETCUSDT","XLMUSDT","FILUSDT","NEARUSDT","APTUSDT",
        "ARBUSDT","OPUSDT","INJUSDT","SUIUSDT","AAVEUSDT" };
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
            (label: "6시간", pages: 1),    // 5분봉 72봉 — sliceLen 별도 처리
            (label: "12시간",pages: 1),    // 5분봉 144봉
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
            // [v5.22.7] 6h/12h 짧은 기간은 별도 sliceLen (5분봉 기준 72/144봉)
            //   기본 컨텍스트 위해 최소 400봉 필요 → 400봉 슬라이스 후 마지막 N봉만 시뮬
            int sliceLen;
            int simBars; // 실제 시뮬할 봉 수 (가드 평가 윈도)
            if (per.label == "6시간") { sliceLen = 500; simBars = 72; }
            else if (per.label == "12시간") { sliceLen = 500; simBars = 144; }
            else { sliceLen = per.pages * BARS_PER_REQ; simBars = sliceLen; }

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
                    // 시뮬 시작점: 6h/12h 는 마지막 simBars 만, 일 단위는 50번부터 전체
                    int startI = Math.Max(50, kl.Count - simBars - win);
                    for (int i = startI; i < kl.Count - win; i++)
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

    // [v5.22.36] 180일 일별 PnL — RunDaily60Async 와 동일 로직, pages=36 으로 확장
    private static async Task RunDaily180Async()
    {
        await RunDailyAsync(pages: 36, label: "180일", altOnly: false);
    }

    private static async Task RunDaily60Async()
    {
        await RunDailyAsync(pages: 12, label: "60일", altOnly: false);
    }

    // [v5.22.37] 알트 26개만 — 메이저 (BTC/ETH/SOL/XRP/BNB) 제외, SQUEEZE + BB_WALK 트리거만 평가
    private static async Task RunAlt180Async() => await RunDailyAsync(pages: 36, label: "알트180일", altOnly: true);
    private static async Task RunAlt60Async() => await RunDailyAsync(pages: 12, label: "알트60일", altOnly: true);

    // [v5.22.49] Golden Time v2 — Pump-and-Dump 필터 2종 추가 (캔들 몸통 + 거래량 지속성)
    //   v1 + 추가:
    //     - 캔들 몸통 비율 ≥ 60% (윗꼬리 40% 이하 = 가짜돌파 차단)
    //     - 거래량 지속성: 현재 봉 vol > 직전 봉 vol (Velocity > 0)
    //   * 호가창 잔량 (매수:매도 < 3:1) 은 백테스트 데이터 한계로 불가, 라이브만 적용
    private static async Task RunGoldenTimeV2Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.49 Golden Time v2 — Pump&Dump 필터 (캔들 몸통 60% + Vol Velocity)");
        Console.WriteLine("  진입: 시가 +1.5% + Vol 3배 + BTC가드 + 몸통60% + Vol증가");
        Console.WriteLine("  청산: TP+2.5% SL-1.0% + 강제20분");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;
        const decimal goldenLeverage = 5m;
        const decimal goldenMargin = 50m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 5m — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool IsGoldenTime(DateTime utc, out int sessionMinute, out DateTime sessionStartUtc)
        {
            DateTime kst = utc.AddHours(9);
            int h = kst.Hour, m = kst.Minute;
            if (h == 9 && m < 10)
            {
                sessionMinute = m;
                DateTime kstStart = new DateTime(kst.Year, kst.Month, kst.Day, 9, 0, 0);
                sessionStartUtc = kstStart.AddHours(-9);
                return true;
            }
            if (h == 0 && m < 10)
            {
                sessionMinute = m;
                DateTime kstStart = new DateTime(kst.Year, kst.Month, kst.Day, 0, 0, 0);
                sessionStartUtc = kstStart.AddHours(-9);
                return true;
            }
            sessionMinute = -1; sessionStartUtc = DateTime.MinValue;
            return false;
        }

        decimal SessionOpenPrice(List<IBinanceKline> kl, DateTime sessionStartUtc)
        {
            int idx2 = kl.FindIndex(k => k.OpenTime == sessionStartUtc);
            if (idx2 < 0)
            {
                idx2 = kl.FindIndex(k => k.OpenTime > sessionStartUtc);
                if (idx2 > 0) idx2--; else idx2 = 0;
            }
            return idx2 >= 0 && idx2 < kl.Count ? kl[idx2].OpenPrice : 0m;
        }

        decimal Btc1mChange(DateTime t)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return 0m;
            int idx2 = btc.FindIndex(k => k.OpenTime > t);
            if (idx2 < 0) idx2 = btc.Count - 1; else idx2--;
            if (idx2 < 1) return 0m;
            return (btc[idx2].ClosePrice - btc[idx2 - 1].ClosePrice) / btc[idx2 - 1].ClosePrice * 100m;
        }

        bool ShouldEnterGoldenV2(List<IBinanceKline> kl, int i)
        {
            if (!IsGoldenTime(kl[i].OpenTime, out int sessionMin, out DateTime sessionStart)) return false;
            if (Btc1mChange(kl[i].OpenTime) < -0.3m) return false;
            decimal openPx = SessionOpenPrice(kl, sessionStart);
            if (openPx <= 0) return false;
            decimal jumpPct = (kl[i].ClosePrice - openPx) / openPx * 100m;
            if (jumpPct < 1.5m) return false;
            // 거래량 ≥ 평균 × 3
            if (i < 5) return false;
            decimal avgVol = 0m;
            for (int j = i - 5; j < i; j++) avgVol += (decimal)(double)kl[j].Volume;
            avgVol /= 5m;
            if (avgVol <= 0) return false;
            decimal curVol = (decimal)(double)kl[i].Volume;
            if (curVol < avgVol * 3m) return false;

            // [신규 1] 캔들 몸통 비율 ≥ 60% (윗꼬리 40% 이하)
            decimal range = kl[i].HighPrice - kl[i].LowPrice;
            if (range <= 0) return false;
            decimal body = kl[i].ClosePrice - kl[i].OpenPrice;
            if (body <= 0) return false; // 음봉 차단
            decimal bodyRatio = body / range;
            if (bodyRatio < 0.6m) return false;

            // [신규 2] 거래량 지속성 (Velocity) — 현재 봉 vol > 직전 봉 vol
            decimal prevVol = (decimal)(double)kl[i - 1].Volume;
            if (curVol <= prevVol) return false;

            return true;
        }

        (string kind, decimal pct) GoldenSimulate(List<IBinanceKline> kl, int i, DateTime sessionStartUtc)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * 0.99m;
            decimal openPx = SessionOpenPrice(kl, sessionStartUtc);
            if (openPx > 0 && openPx > slPx && openPx < entry) slPx = openPx;
            decimal tp1Px = entry * 1.025m;

            int sessionElapsed = (int)((kl[i].OpenTime - sessionStartUtc).TotalMinutes);
            int forceLeft = (20 - sessionElapsed) / 5;
            if (forceLeft < 1) forceLeft = 1;
            int win = Math.Min(4, forceLeft);

            bool tp1Hit = false; decimal beSl = entry; decimal highSinceTp1 = 0m;
            for (int k = 1; k <= win && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", (slPx - entry) / entry * 100m);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; highSinceTp1 = tp1Px; }
                }
                else
                {
                    if (b.HighPrice > highSinceTp1) highSinceTp1 = b.HighPrice;
                    decimal trail = highSinceTp1 * 0.995m;
                    if (trail < beSl) trail = beSl;
                    if (b.LowPrice <= trail)
                    {
                        decimal half2 = (trail - entry) / entry * 100m;
                        return ("TP1+Trail", 2.5m * 0.5m + half2 * 0.5m);
                    }
                }
            }
            decimal lastClose = kl[Math.Min(i + win, kl.Count - 1)].ClosePrice;
            if (tp1Hit)
            {
                decimal half2 = (lastClose - entry) / entry * 100m;
                return ("TP1+Force20m", 2.5m * 0.5m + half2 * 0.5m);
            }
            return ("Force20m", (lastClose - entry) / entry * 100m);
        }

        (decimal pnl, int n, int sl, int tp, int force) Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            decimal notional = goldenMargin * goldenLeverage;
            int n = 0, sl_ = 0, tp_ = 0, force = 0;
            decimal totalPnl = 0m;
            DateTime nextSlotFreeUtc = DateTime.MinValue;

            foreach (var kv in fullData)
            {
                var kl = kv.Value;
                for (int i = 50; i < kl.Count - 4; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (kl[i].OpenTime < nextSlotFreeUtc) continue;
                    if (!ShouldEnterGoldenV2(kl, i)) continue;
                    IsGoldenTime(kl[i].OpenTime, out _, out DateTime sessionStart);
                    var (kind, pctRaw) = GoldenSimulate(kl, i, sessionStart);
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "SL") sl_++;
                    else if (kind.StartsWith("TP1")) tp_++;
                    else force++;
                    nextSlotFreeUtc = sessionStart.AddMinutes(20);
                }
            }
            return (totalPnl, n, sl_, tp_, force);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"SL",5} {"TP1",5} {"강제",5} {"PnL",10} {"ROI(시드$400)",16}");
        Console.WriteLine(new string('-', 75));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.sl,5} {r.tp,5} {r.force,5} {r.pnl,9:F2} {roi,15:F2}%");
        }
        Console.WriteLine(new string('-', 75));
        Console.WriteLine();
        Console.WriteLine("[해석] 진입 가드 7개: KST 00/09시 10분 + BTC 1m≥-0.3% + 시가+1.5% + Vol×3 + 양봉몸통≥60% + Vol증가");
        Console.WriteLine("       청산: SL-1.0% (또는 시가) / TP+2.5% (50%) + 트레일고점-0.5% / 강제20분");
        Console.WriteLine("       호가창 잔량 필터 (매수:매도<3:1) 는 라이브에서만 적용 (백테스트 데이터 한계)");
    }

    // [v5.22.48] Golden Time Scouter — 00:00 / 09:00 KST 광기 구간 10분 진입
    //   진입 (00:00~00:10 OR 09:00~09:10 KST):
    //     - BTC 1m 변동 ≥ -0.3%
    //     - 1m 종가 vs 09:00/00:00 시가 ≥ +1.5%
    //     - 1m 거래량 ≥ 직전 5분 평균 × 3
    //   청산:
    //     - SL: -1.0% OR 시가 이탈
    //     - TP1 +2.5% (50% 매도) → 트레일 고점 -0.5%
    //     - 강제 종료 09:20 / 00:20 (수익 무관 전량)
    //   레버리지: 5x (평소 15x → 1/3)
    //   시드 마진: $50 (평소 $100~150 → 절반)
    //   주의: 5m 봉 데이터로 시뮬 (1m 정밀도 한계 있음 — 5m 첫 봉을 1m × 5 근사)
    private static async Task RunGoldenTimeAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.48 Golden Time Scouter — 00시/09시 광기 구간 10분 단타");
        Console.WriteLine("  진입: 시가 +1.5% + Vol 3배 + BTC 가드  /  청산: TP+2.5% SL-1.0% 강제20분");
        Console.WriteLine("  시드 \\$400, 마진 \\$50/슬롯 1, 레버리지 5x");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;
        const decimal goldenLeverage = 5m;
        const decimal goldenMargin = 50m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 5m — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // 5분봉 → KST 시간 변환 (Binance UTC + 9시간)
        bool IsGoldenTime(DateTime utc, out int sessionMinute, out DateTime sessionStartUtc)
        {
            DateTime kst = utc.AddHours(9);
            // 09:00~09:10 KST = UTC 00:00~00:10
            // 00:00~00:10 KST = UTC 15:00~15:10 (전일)
            int h = kst.Hour, m = kst.Minute;
            if (h == 9 && m < 10)
            {
                sessionMinute = m;
                DateTime kstStart = new DateTime(kst.Year, kst.Month, kst.Day, 9, 0, 0);
                sessionStartUtc = kstStart.AddHours(-9);
                return true;
            }
            if (h == 0 && m < 10)
            {
                sessionMinute = m;
                DateTime kstStart = new DateTime(kst.Year, kst.Month, kst.Day, 0, 0, 0);
                sessionStartUtc = kstStart.AddHours(-9);
                return true;
            }
            sessionMinute = -1;
            sessionStartUtc = DateTime.MinValue;
            return false;
        }

        // 세션 시가 — sessionStartUtc 시점 5m 봉의 OpenPrice
        decimal SessionOpenPrice(List<IBinanceKline> kl, DateTime sessionStartUtc)
        {
            int idx2 = kl.FindIndex(k => k.OpenTime == sessionStartUtc);
            if (idx2 < 0)
            {
                idx2 = kl.FindIndex(k => k.OpenTime > sessionStartUtc);
                if (idx2 > 0) idx2--;
                else idx2 = 0;
            }
            return idx2 >= 0 && idx2 < kl.Count ? kl[idx2].OpenPrice : 0m;
        }

        // BTC 1m 변동률 — 5분봉이라 5분봉 변동률 사용 (1m 정밀도 없음)
        decimal Btc1mChange(DateTime t)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return 0m;
            int idx2 = btc.FindIndex(k => k.OpenTime > t);
            if (idx2 < 0) idx2 = btc.Count - 1; else idx2--;
            if (idx2 < 1) return 0m;
            decimal now = btc[idx2].ClosePrice;
            decimal prev = btc[idx2 - 1].ClosePrice;
            return prev > 0 ? (now - prev) / prev * 100m : 0m;
        }

        bool ShouldEnterGolden(List<IBinanceKline> kl, int i)
        {
            if (!IsGoldenTime(kl[i].OpenTime, out int sessionMin, out DateTime sessionStart)) return false;
            // BTC 가드
            if (Btc1mChange(kl[i].OpenTime) < -0.3m) return false;
            // 시가 대비 ≥ +1.5%
            decimal openPx = SessionOpenPrice(kl, sessionStart);
            if (openPx <= 0) return false;
            decimal jumpPct = (kl[i].ClosePrice - openPx) / openPx * 100m;
            if (jumpPct < 1.5m) return false;
            // 거래량 ≥ 직전 5분 평균 × 3
            // 5분봉 1봉 vs 직전 5봉 평균 (= 25분 평균)
            if (i < 5) return false;
            decimal avgVol = 0m;
            for (int j = i - 5; j < i; j++) avgVol += (decimal)(double)kl[j].Volume;
            avgVol /= 5m;
            if (avgVol <= 0) return false;
            decimal curVol = (decimal)(double)kl[i].Volume;
            if (curVol < avgVol * 3m) return false;
            return true;
        }

        // Golden Time 청산 — 강제 20분 종료 (= 4봉)
        // win 봉 한도 = 4 (00:00 시작 시 00:20 = 4봉, 진입 시점부터 max 4봉)
        (string kind, decimal pct) GoldenSimulate(List<IBinanceKline> kl, int i, DateTime sessionStartUtc)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - 0.01m); // -1.0%
            decimal openPx = SessionOpenPrice(kl, sessionStartUtc);
            // 시가 이탈 = openPx 도 SL 후보 (둘 중 가까운 것)
            if (openPx > 0 && openPx > slPx && openPx < entry) slPx = openPx;
            decimal tp1Px = entry * 1.025m; // +2.5%

            // 진입 봉 OpenTime 기준 강제 종료 시각 (sessionStart + 20분)
            DateTime forceExit = sessionStartUtc.AddMinutes(20);
            int maxBar = 4; // 절대 한계
            // 진입 봉이 sessionStart + N분이면 강제 종료까지 (20-N)/5 봉 남음
            int sessionElapsed = (int)((kl[i].OpenTime - sessionStartUtc).TotalMinutes);
            int forceLeft = (20 - sessionElapsed) / 5;
            if (forceLeft < 1) forceLeft = 1;
            int win = Math.Min(maxBar, forceLeft);

            bool tp1Hit = false; decimal beSl = entry; decimal highSinceTp1 = 0m;
            for (int k = 1; k <= win && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", (slPx - entry) / entry * 100m);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; highSinceTp1 = tp1Px; }
                }
                else
                {
                    if (b.HighPrice > highSinceTp1) highSinceTp1 = b.HighPrice;
                    decimal trail = highSinceTp1 * (1 - 0.005m); // 고점 -0.5%
                    if (trail < beSl) trail = beSl;
                    if (b.LowPrice <= trail)
                    {
                        decimal half2 = (trail - entry) / entry * 100m;
                        return ("TP1+Trail", 2.5m * 0.5m + half2 * 0.5m);
                    }
                }
            }
            // 강제 종료 (20분 도달)
            decimal lastClose = kl[Math.Min(i + win, kl.Count - 1)].ClosePrice;
            if (tp1Hit)
            {
                decimal half2 = (lastClose - entry) / entry * 100m;
                return ("TP1+Force20m", 2.5m * 0.5m + half2 * 0.5m);
            }
            decimal pctClose = (lastClose - entry) / entry * 100m;
            return ("Force20m", pctClose);
        }

        (decimal pnl, int n, int sl, int tp, int force) Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            decimal notional = goldenMargin * goldenLeverage;
            int n = 0, sl_ = 0, tp_ = 0, force = 0;
            decimal totalPnl = 0m;

            // Golden time 슬롯 = 1 (한 번에 하나만, 강제 20분 후 자동 해제)
            DateTime nextSlotFreeUtc = DateTime.MinValue;

            foreach (var kv in fullData)
            {
                var kl = kv.Value;
                for (int i = 50; i < kl.Count - 4; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (kl[i].OpenTime < nextSlotFreeUtc) continue;
                    if (!ShouldEnterGolden(kl, i)) continue;

                    IsGoldenTime(kl[i].OpenTime, out _, out DateTime sessionStart);
                    var (kind, pctRaw) = GoldenSimulate(kl, i, sessionStart);
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "SL") sl_++;
                    else if (kind.StartsWith("TP1")) tp_++;
                    else force++;
                    // 슬롯 해제: 진입 봉 + win 봉 후
                    nextSlotFreeUtc = sessionStart.AddMinutes(20);
                }
            }
            return (totalPnl, n, sl_, tp_, force);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"SL",5} {"TP1",5} {"강제",5} {"PnL",10} {"ROI(시드$400)",16}");
        Console.WriteLine(new string('-', 75));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.sl,5} {r.tp,5} {r.force,5} {r.pnl,9:F2} {roi,15:F2}%");
        }
        Console.WriteLine(new string('-', 75));
        Console.WriteLine();
        Console.WriteLine("[해석] 00시/09시 KST 골든타임 10분간 진입 (BTC 가드 + 시가 +1.5% + Vol 3x)");
        Console.WriteLine("       SL -1.0% / TP +2.5% (50%) + Trail 고점-0.5% / 강제20분 종료");
        Console.WriteLine("       레버리지 5x, 마진 \\$50 (평소의 1/3)");
    }

    // [v5.22.47] 사용자 v4 — WatchList 60분 갱신 + 횡보 회피 (ADX/BB Squeeze + Time-Cut + BE-Brake + Vol Dry-up)
    //   사양 변경:
    //     1. 60분마다 알트 1차 필터링 5단계 통과한 종목만 WatchList → WatchList 종목만 매매
    //     2. 진입 전: ADX(14) ≥ 20 + BB Squeeze→Expansion (BBW 평균 0.7배 후 종가 상단 돌파)
    //     3. 진입 후: Time-Cut (6봉 +0.5% 미달 → 시장가 탈출)
    //                Break-even Brake (+0.3% 후 진입가 복귀 → 본절 청산)
    //                Vol Dry-up (진입봉 vol 대비 ≤30% 가 3봉 연속 → 시장가 탈출)
    private static async Task RunUserStrategyV4Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.47 사용자 v4 — WatchList + 횡보 회피 (ADX/BBSqz + TimeCut + BE-Brake + VolDry)");
        Console.WriteLine("  메이저 TP+1.2%/SL-0.7% | 알트 TP+2.5%/SL-1.2% | 시드 \\$400");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 5m — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // 헬퍼들 (v3 동일)
        decimal SelfEma(List<IBinanceKline> kl, int upTo, int period)
        {
            if (upTo + 1 < period) return 0m;
            decimal alpha = 2m / (period + 1);
            int from = Math.Max(0, upTo - period * 5);
            decimal e = kl[from].ClosePrice;
            for (int j = from + 1; j <= upTo; j++) e = kl[j].ClosePrice * alpha + e * (1 - alpha);
            return e;
        }

        decimal Ema15m50(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 50 * 3) return 0m;
            var list = new List<decimal>(50);
            for (int q = 49; q >= 0; q--)
            {
                int j = upTo - q * 3;
                if (j < 0) return 0m;
                list.Add(kl[j].ClosePrice);
            }
            decimal alpha = 2m / 51m;
            decimal e = list[0];
            for (int j = 1; j < list.Count; j++) e = list[j] * alpha + e * (1 - alpha);
            return e;
        }

        double Rsi15m14(List<IBinanceKline> kl, int upTo)
        {
            int needed = 14 * 3 + 3;
            if (upTo + 1 < needed) return 0;
            var closes = new List<double>();
            for (int q = 28; q >= 0; q--)
            {
                int j = upTo - q * 3;
                if (j < 0) return 0;
                closes.Add((double)kl[j].ClosePrice);
            }
            double gain = 0, loss = 0;
            for (int j = 1; j < closes.Count; j++)
            {
                double diff = closes[j] - closes[j - 1];
                if (diff > 0) gain += diff; else loss -= diff;
            }
            int n = closes.Count - 1;
            double avgGain = gain / n, avgLoss = loss / n;
            if (avgLoss == 0) return 100;
            double rs = avgGain / avgLoss;
            return 100 - 100 / (1 + rs);
        }

        decimal Btc1mChange(DateTime t)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return 0m;
            int idx2 = btc.FindIndex(k => k.OpenTime > t);
            if (idx2 < 0) idx2 = btc.Count - 1; else idx2--;
            if (idx2 < 1) return 0m;
            decimal now = btc[idx2].ClosePrice;
            decimal prev = btc[idx2 - 1].ClosePrice;
            return prev > 0 ? (now - prev) / prev * 100m : 0m;
        }

        decimal QuoteVolume24h(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 288) return 0m;
            decimal sum = 0m;
            for (int j = upTo - 287; j <= upTo; j++)
                sum += (decimal)(double)kl[j].Volume * kl[j].ClosePrice;
            return sum;
        }

        decimal Change24h(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 288) return 0m;
            return (kl[upTo].ClosePrice - kl[upTo - 287].OpenPrice) / kl[upTo - 287].OpenPrice * 100m;
        }

        decimal Volume1hRatio(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 288) return 0m;
            decimal vol1h = 0m, vol24h = 0m;
            for (int j = upTo - 11; j <= upTo; j++) vol1h += (decimal)(double)kl[j].Volume;
            for (int j = upTo - 287; j <= upTo; j++) vol24h += (decimal)(double)kl[j].Volume;
            decimal avg1h = vol24h / 24m;
            return avg1h > 0 ? vol1h / avg1h : 0m;
        }

        // ADX(14) 계산 — Wilder's smoothing
        double Adx14(List<IBinanceKline> kl, int upTo)
        {
            int period = 14;
            if (upTo + 1 < period * 3) return 0;
            int from = upTo - period * 2;
            double prevTr = 0, prevPdm = 0, prevNdm = 0, prevAdx = 0;
            double atr = 0, pdi = 0, ndi = 0, adx = 0;
            for (int i = from + 1; i <= upTo; i++)
            {
                double th = (double)kl[i].HighPrice;
                double tl = (double)kl[i].LowPrice;
                double tc = (double)kl[i].ClosePrice;
                double yh = (double)kl[i - 1].HighPrice;
                double yl = (double)kl[i - 1].LowPrice;
                double yc = (double)kl[i - 1].ClosePrice;
                double tr = Math.Max(th - tl, Math.Max(Math.Abs(th - yc), Math.Abs(tl - yc)));
                double upMove = th - yh;
                double dnMove = yl - tl;
                double pdm = (upMove > dnMove && upMove > 0) ? upMove : 0;
                double ndm = (dnMove > upMove && dnMove > 0) ? dnMove : 0;
                if (i == from + 1)
                {
                    atr = tr; prevPdm = pdm; prevNdm = ndm;
                }
                else
                {
                    atr = (atr * (period - 1) + tr) / period;
                    prevPdm = (prevPdm * (period - 1) + pdm) / period;
                    prevNdm = (prevNdm * (period - 1) + ndm) / period;
                }
                if (atr > 0)
                {
                    pdi = 100 * prevPdm / atr;
                    ndi = 100 * prevNdm / atr;
                    double dx = (pdi + ndi) > 0 ? Math.Abs(pdi - ndi) / (pdi + ndi) * 100 : 0;
                    if (i - from < period) adx = dx;
                    else adx = (adx * (period - 1) + dx) / period;
                }
            }
            return adx;
        }

        // BBW 평균 (직전 100봉)
        decimal BbwAvg100(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 120) return 0m;
            decimal sum = 0m; int cnt = 0;
            for (int q = upTo - 99; q <= upTo - 1; q++)
            {
                var b = LiveMajorEvaluator.Bb(kl, q, 20, 2);
                if (b.Mid > 0)
                {
                    sum += ((decimal)b.Upper - (decimal)b.Lower) / (decimal)b.Mid * 100m;
                    cnt++;
                }
            }
            return cnt > 0 ? sum / cnt : 0m;
        }

        // 알트 1차 필터링 (v3 동일)
        bool AltPassFilter1(List<IBinanceKline> kl, int i)
        {
            if (QuoteVolume24h(kl, i) < 50_000_000m) return false;
            if (Change24h(kl, i) < 2m) return false;
            if (Volume1hRatio(kl, i) < 2m) return false;
            decimal ema15 = Ema15m50(kl, i);
            if (ema15 == 0 || kl[i].ClosePrice <= ema15) return false;
            if (Rsi15m14(kl, i) <= 50) return false;
            if (Math.Abs(Btc1mChange(kl[i].OpenTime)) > 0.3m) return false;
            return true;
        }

        bool MajorPassFilter(List<IBinanceKline> kl, int i)
        {
            decimal ema15 = Ema15m50(kl, i);
            return ema15 != 0 && kl[i].ClosePrice > ema15;
        }

        // 진입 가드 (5m): RSI 50~70 + MACD>0 + 이격도<1% + ADX≥20 + BB Squeeze→Expansion
        bool Entry5m(List<IBinanceKline> kl, int i)
        {
            double rsi = LiveMajorEvaluator.Rsi(kl, i, 14);
            if (rsi < 50 || rsi > 70) return false;
            var macd = LiveMajorEvaluator.Macd(kl, i);
            if (macd.Hist <= 0) return false;
            decimal ema5_20 = SelfEma(kl, i, 20);
            if (ema5_20 == 0) return false;
            decimal divPct = Math.Abs(kl[i].ClosePrice - ema5_20) / ema5_20 * 100m;
            if (divPct > 1.0m) return false;
            // ADX(14) ≥ 20
            if (Adx14(kl, i) < 20) return false;
            // BB Squeeze→Expansion: 직전 100봉 BBW 평균 × 0.7 이하 후 종가 상단 돌파
            var bb = LiveMajorEvaluator.Bb(kl, i, 20, 2);
            if (bb.Mid <= 0) return false;
            decimal nowBbw = ((decimal)bb.Upper - (decimal)bb.Lower) / (decimal)bb.Mid * 100m;
            decimal avgBbw = BbwAvg100(kl, i);
            if (avgBbw == 0) return false;
            // 직전 5봉 동안 squeeze (BBW < avg × 0.7) 였다가 현재 종가 상단 돌파
            bool wasSqueezed = false;
            for (int q = Math.Max(0, i - 5); q < i; q++)
            {
                var bp = LiveMajorEvaluator.Bb(kl, q, 20, 2);
                if (bp.Mid > 0)
                {
                    decimal bbwP = ((decimal)bp.Upper - (decimal)bp.Lower) / (decimal)bp.Mid * 100m;
                    if (bbwP < avgBbw * 0.7m) { wasSqueezed = true; break; }
                }
            }
            if (!wasSqueezed) return false;
            if (kl[i].ClosePrice <= (decimal)bb.Upper) return false; // 상단 돌파
            return true;
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i, bool isMajor)
        {
            if (i < 290) return false;
            if (isMajor) { if (!MajorPassFilter(kl, i)) return false; }
            else { if (!AltPassFilter1(kl, i)) return false; }
            return Entry5m(kl, i);
        }

        // 청산 시뮬 — Time-Cut + BE-Brake + Vol Dry-up + 분할 익절 + 트레일링
        const int hardWin = 24; // 절대 한계 2시간 (Time Stop)
        (string kind, decimal pct) Simulate(List<IBinanceKline> kl, int i, decimal tpPct, decimal slPct)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - slPct / 100m);
            if (i > 0)
            {
                decimal prevLow = kl[i - 1].LowPrice;
                if (prevLow > slPx && prevLow < entry) slPx = prevLow;
            }
            decimal tp1Px = entry * (1 + tpPct / 100m);
            decimal entryVol = (decimal)(double)kl[i].Volume;

            bool tp1Hit = false;
            decimal beSl = entry;
            decimal highSinceTp1 = 0m;
            decimal peakBeforeTp1 = entry;
            int volDryStreak = 0;

            for (int k = 1; k <= hardWin && i + k < kl.Count; k++)
            {
                var b = kl[i + k];

                // 1. SL/TP 우선
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", (slPx - entry) / entry * 100m);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; highSinceTp1 = tp1Px; }
                }
                else
                {
                    if (b.HighPrice > highSinceTp1) highSinceTp1 = b.HighPrice;
                    decimal trailStop = highSinceTp1 * (1 - 0.003m);
                    if (trailStop < beSl) trailStop = beSl;
                    if (b.LowPrice <= trailStop)
                    {
                        decimal half2Pct = (trailStop - entry) / entry * 100m;
                        return ("TP1+Trail", tpPct * 0.5m + half2Pct * 0.5m);
                    }
                    continue;
                }

                // 2. Time-Cut: 6봉(30분) 내 +0.5% 미달
                if (k == 6)
                {
                    if (b.HighPrice < entry * 1.005m)
                    {
                        // 본절 또는 시장가 탈출 — 종가 기준
                        decimal exitPct = (b.ClosePrice - entry) / entry * 100m;
                        return ("TimeCut", exitPct);
                    }
                }

                // 3. BE-Brake: +0.3% 도달 후 진입가 복귀
                if (b.HighPrice > peakBeforeTp1) peakBeforeTp1 = b.HighPrice;
                if (peakBeforeTp1 >= entry * 1.003m && b.LowPrice <= entry)
                {
                    return ("BE-Brake", 0m);
                }

                // 4. Vol Dry-up: 진입봉 대비 30% 이하가 3봉 연속
                decimal curVol = (decimal)(double)b.Volume;
                if (entryVol > 0 && curVol <= entryVol * 0.3m) volDryStreak++;
                else volDryStreak = 0;
                if (volDryStreak >= 3)
                {
                    decimal exitPct = (b.ClosePrice - entry) / entry * 100m;
                    return ("VolDry", exitPct);
                }
            }

            // 윈도우 종료
            decimal lastClose = kl[Math.Min(i + hardWin, kl.Count - 1)].ClosePrice;
            if (tp1Hit)
            {
                decimal half2Pct = (lastClose - entry) / entry * 100m;
                return ("TP1+Timeout", tpPct * 0.5m + half2Pct * 0.5m);
            }
            decimal pctClose = (lastClose - entry) / entry * 100m;
            return (Math.Abs(pctClose) < 0.5m ? "Neutral" : "Timeout", pctClose);
        }

        (decimal pnl, int n, int sl, int tp1tr, int tcut, int beBr, int volDry, int neu, int tout) Eval(int days, bool majorOnly = false, bool altOnly = false)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal feeRate = FEE_RATE;
            int n = 0, sl_ = 0, tp1tr = 0, tcut = 0, beBr = 0, volDry = 0, neu = 0, tout = 0;
            decimal totalPnl = 0m;

            // WatchList 갱신: 60분(=12봉)마다 알트 1차 필터링 통과 종목만 유지
            // 시뮬에서 i 봉이 12의 배수인 시점에 Eval. 단순화: 매 5분봉마다 필터 조건 체크해도 결과 동일 (어차피 진입 시 같은 조건 검증)
            // 메이저는 WatchList 무관 (항상 후보)

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorOnly && !isMajor) continue;
                if (altOnly && isMajor) continue;
                decimal margin = isMajor ? 150m : 100m;
                decimal notional = margin * LEVERAGE;
                decimal tpPct = isMajor ? 1.2m : 2.5m;
                decimal slPct = isMajor ? 0.7m : 1.2m;
                int lastFire = -1000;
                for (int i = 290; i < kl.Count - hardWin; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i, isMajor)) continue;
                    var (kind, pctRaw) = Simulate(kl, i, tpPct, slPct);
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "SL") sl_++;
                    else if (kind.StartsWith("TP1")) tp1tr++;
                    else if (kind == "TimeCut") tcut++;
                    else if (kind == "BE-Brake") beBr++;
                    else if (kind == "VolDry") volDry++;
                    else if (kind == "Neutral") neu++;
                    else tout++;
                    lastFire = i;
                }
            }
            return (totalPnl, n, sl_, tp1tr, tcut, beBr, volDry, neu, tout);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"카테",-6} {"진입",6} {"SL",4} {"TP1",4} {"TC",4} {"BE",4} {"VD",4} {"중립",4} {"타임",4} {"PnL",10} {"ROI",10}");
        Console.WriteLine(new string('-', 95));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var maj = Eval(days, majorOnly: true);
            var alt = Eval(days, altOnly: true);
            decimal allPnl = maj.pnl + alt.pnl;
            int allN = maj.n + alt.n;
            Console.WriteLine($"{days,-7}일 {"메이저",-6} {maj.n,6} {maj.sl,4} {maj.tp1tr,4} {maj.tcut,4} {maj.beBr,4} {maj.volDry,4} {maj.neu,4} {maj.tout,4} {maj.pnl,9:F2} {maj.pnl / seed * 100m,9:F2}%");
            Console.WriteLine($"{days,-7}일 {"알트",-6} {alt.n,6} {alt.sl,4} {alt.tp1tr,4} {alt.tcut,4} {alt.beBr,4} {alt.volDry,4} {alt.neu,4} {alt.tout,4} {alt.pnl,9:F2} {alt.pnl / seed * 100m,9:F2}%");
            Console.WriteLine($"{days,-7}일 {"합계",-6} {allN,6} {maj.sl + alt.sl,4} {maj.tp1tr + alt.tp1tr,4} {maj.tcut + alt.tcut,4} {maj.beBr + alt.beBr,4} {maj.volDry + alt.volDry,4} {maj.neu + alt.neu,4} {maj.tout + alt.tout,4} {allPnl,9:F2} {allPnl / seed * 100m,9:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 95));
        Console.WriteLine("[해석] SL=손절 TP1=1차익절+트레일 TC=TimeCut(30분) BE=Break-evenBrake VD=VolDryup");
    }

    // [v5.22.46] 사용자 v3 — 알트 1차 필터링 3단계 추가 + 메이저/알트 차별화
    //   1차 필터링 (알트만):
    //     - 거래대금 24h ≥ $50M (백테스트는 quoteVolume 누적 추정)
    //     - 24h 변동률 ≥ +2%
    //     - 1시간 거래량 폭증 ≥ 평소 × 2~3배
    //     - 15m: 가격 > EMA50 AND RSI > 50
    //     - BTC 1m 변동 ±0.3% 초과 시 모든 알트 진입 차단
    //   진입 (양 카테고리 공통 추가): RSI 50~70 + MACD>0 + 이격도<1%
    //   TP/SL: 메이저 +1.2%/-0.7%, 알트 +2.5%/-1.2%
    private static async Task RunUserStrategyV3Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.46 사용자 차별화 v3 — 알트 1차 필터링 3단계 추가");
        Console.WriteLine("  거래대금 ≥ \\$50M / 24h ≥ +2% / Vol 1h ≥ 평균×2 / BTC 1m \\|\\±0.3%\\| 차단");
        Console.WriteLine("  메이저 TP+1.2%/SL-0.7% | 알트 TP+2.5%/SL-1.2%");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 5m — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        decimal SelfEma(List<IBinanceKline> kl, int upTo, int period)
        {
            if (upTo + 1 < period) return 0m;
            decimal alpha = 2m / (period + 1);
            int from = Math.Max(0, upTo - period * 5);
            decimal e = kl[from].ClosePrice;
            for (int j = from + 1; j <= upTo; j++) e = kl[j].ClosePrice * alpha + e * (1 - alpha);
            return e;
        }

        decimal Ema15m50(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 50 * 3) return 0m;
            var list = new List<decimal>(50);
            for (int q = 49; q >= 0; q--)
            {
                int j = upTo - q * 3;
                if (j < 0) return 0m;
                list.Add(kl[j].ClosePrice);
            }
            decimal alpha = 2m / 51m;
            decimal e = list[0];
            for (int j = 1; j < list.Count; j++) e = list[j] * alpha + e * (1 - alpha);
            return e;
        }

        // 15m RSI14 — 매 3봉 종가로 RSI 계산
        double Rsi15m14(List<IBinanceKline> kl, int upTo)
        {
            int needed = 14 * 3 + 3;
            if (upTo + 1 < needed) return 0;
            var closes = new List<double>();
            for (int q = 28; q >= 0; q--)
            {
                int j = upTo - q * 3;
                if (j < 0) return 0;
                closes.Add((double)kl[j].ClosePrice);
            }
            double gain = 0, loss = 0;
            for (int j = 1; j < closes.Count; j++)
            {
                double diff = closes[j] - closes[j - 1];
                if (diff > 0) gain += diff; else loss -= diff;
            }
            int n = closes.Count - 1;
            double avgGain = gain / n, avgLoss = loss / n;
            if (avgLoss == 0) return 100;
            double rs = avgGain / avgLoss;
            return 100 - 100 / (1 + rs);
        }

        // BTC 1m 변동률 — 5분봉이라 1분봉 정밀도 없음 → 5분봉 1봉 변동률 사용
        decimal Btc1mChange(DateTime t)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return 0m;
            int idx2 = btc.FindIndex(k => k.OpenTime > t);
            if (idx2 < 0) idx2 = btc.Count - 1; else idx2--;
            if (idx2 < 1) return 0m;
            decimal now = btc[idx2].ClosePrice;
            decimal prev = btc[idx2 - 1].ClosePrice;
            return prev > 0 ? (now - prev) / prev * 100m : 0m;
        }

        // 24h 거래대금 (quoteVolume) — 5m × 288봉 합계
        decimal QuoteVolume24h(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 288) return 0m;
            decimal sum = 0m;
            for (int j = upTo - 287; j <= upTo; j++)
                sum += (decimal)(double)kl[j].Volume * kl[j].ClosePrice;
            return sum;
        }

        // 24h 변동률
        decimal Change24h(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 288) return 0m;
            decimal now = kl[upTo].ClosePrice;
            decimal prev = kl[upTo - 287].OpenPrice;
            return prev > 0 ? (now - prev) / prev * 100m : 0m;
        }

        // 1시간 거래량 vs 24시간 평균 (배수)
        decimal Volume1hRatio(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 288) return 0m;
            decimal vol1h = 0m;
            for (int j = upTo - 11; j <= upTo; j++) vol1h += (decimal)(double)kl[j].Volume;
            decimal vol24h = 0m;
            for (int j = upTo - 287; j <= upTo; j++) vol24h += (decimal)(double)kl[j].Volume;
            decimal avg1h = vol24h / 24m;
            return avg1h > 0 ? vol1h / avg1h : 0m;
        }

        // 알트 1차 필터링 — 5단계 모두 통과해야 진입 후보
        bool AltPassFilter1(List<IBinanceKline> kl, int i)
        {
            // 1. 거래대금 ≥ $50M
            if (QuoteVolume24h(kl, i) < 50_000_000m) return false;
            // 2. 24h 변동률 ≥ +2%
            if (Change24h(kl, i) < 2m) return false;
            // 3. 1h 거래량 폭증 ≥ 평소 × 2
            if (Volume1hRatio(kl, i) < 2m) return false;
            // 4. 15m: 가격 > EMA50 AND RSI > 50
            decimal ema15 = Ema15m50(kl, i);
            if (ema15 == 0 || kl[i].ClosePrice <= ema15) return false;
            double rsi15 = Rsi15m14(kl, i);
            if (rsi15 <= 50) return false;
            // 5. BTC 1m 변동 ±0.3% 초과 시 차단
            decimal btcCh = Btc1mChange(kl[i].OpenTime);
            if (Math.Abs(btcCh) > 0.3m) return false;
            return true;
        }

        bool MajorPassFilter(List<IBinanceKline> kl, int i)
        {
            decimal ema15 = Ema15m50(kl, i);
            if (ema15 == 0 || kl[i].ClosePrice <= ema15) return false;
            return true;
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i, bool isMajor)
        {
            if (i < 290) return false;
            if (isMajor)
            {
                if (!MajorPassFilter(kl, i)) return false;
            }
            else
            {
                if (!AltPassFilter1(kl, i)) return false;
            }
            // 공통 5m 진입: RSI 50~70 + MACD>0 + 이격도 < 1%
            double rsi = LiveMajorEvaluator.Rsi(kl, i, 14);
            if (rsi < 50 || rsi > 70) return false;
            var macd = LiveMajorEvaluator.Macd(kl, i);
            if (macd.Hist <= 0) return false;
            decimal ema5_20 = SelfEma(kl, i, 20);
            if (ema5_20 == 0) return false;
            decimal divPct = Math.Abs(kl[i].ClosePrice - ema5_20) / ema5_20 * 100m;
            if (divPct > 1.0m) return false;
            return true;
        }

        const int win = 24;
        (string kind, decimal pct) Simulate(List<IBinanceKline> kl, int i, decimal tpPct, decimal slPct)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - slPct / 100m);
            if (i > 0)
            {
                decimal prevLow = kl[i - 1].LowPrice;
                if (prevLow > slPx && prevLow < entry) slPx = prevLow;
            }
            decimal tp1Px = entry * (1 + tpPct / 100m);
            bool tp1Hit = false; decimal beSl = entry; decimal highSinceTp1 = 0m;
            for (int k = 1; k <= win && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", (slPx - entry) / entry * 100m);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; highSinceTp1 = tp1Px; }
                }
                else
                {
                    if (b.HighPrice > highSinceTp1) highSinceTp1 = b.HighPrice;
                    decimal trailStop = highSinceTp1 * (1 - 0.003m);
                    if (trailStop < beSl) trailStop = beSl;
                    if (b.LowPrice <= trailStop)
                    {
                        decimal half2Pct = (trailStop - entry) / entry * 100m;
                        decimal totalPct = tpPct * 0.5m + half2Pct * 0.5m;
                        return ("TP1+Trail", totalPct);
                    }
                }
            }
            decimal lastClose = kl[Math.Min(i + win, kl.Count - 1)].ClosePrice;
            if (tp1Hit)
            {
                decimal half2Pct = (lastClose - entry) / entry * 100m;
                decimal totalPct = tpPct * 0.5m + half2Pct * 0.5m;
                string kindClose = (Math.Abs(half2Pct) < 0.5m) ? "TP1+Neutral" : "TP1+Timeout";
                return (kindClose, totalPct);
            }
            decimal pctClose = (lastClose - entry) / entry * 100m;
            string k2 = (pctClose > -0.5m && pctClose < 0.5m) ? "Neutral" : "Timeout";
            return (k2, pctClose);
        }

        (decimal pnl, int n, int sl, int tp1tr, int neu, int tout) Eval(int days, bool majorOnly = false, bool altOnly = false)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal feeRate = FEE_RATE;
            int n = 0, sl_ = 0, tp1tr = 0, neu = 0, tout = 0;
            decimal totalPnl = 0m;

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorOnly && !isMajor) continue;
                if (altOnly && isMajor) continue;
                decimal margin = isMajor ? 150m : 100m;
                decimal notional = margin * LEVERAGE;
                decimal tpPct = isMajor ? 1.2m : 2.5m;
                decimal slPct = isMajor ? 0.7m : 1.2m;
                int lastFire = -1000;
                for (int i = 290; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i, isMajor)) continue;
                    var (kind, pctRaw) = Simulate(kl, i, tpPct, slPct);
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "SL") sl_++;
                    else if (kind.StartsWith("TP1")) tp1tr++;
                    else if (kind == "Neutral") neu++;
                    else tout++;
                    lastFire = i;
                }
            }
            return (totalPnl, n, sl_, tp1tr, neu, tout);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"카테",-6} {"진입",6} {"SL",5} {"TP1+Tr",7} {"중립",5} {"타임",5} {"PnL",10} {"ROI(시드$400)",16}");
        Console.WriteLine(new string('-', 90));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var maj = Eval(days, majorOnly: true);
            var alt = Eval(days, altOnly: true);
            decimal allPnl = maj.pnl + alt.pnl;
            int allN = maj.n + alt.n;
            int allSl = maj.sl + alt.sl, allTp = maj.tp1tr + alt.tp1tr, allNeu = maj.neu + alt.neu, allTout = maj.tout + alt.tout;
            Console.WriteLine($"{days,-7}일 {"메이저",-6} {maj.n,6} {maj.sl,5} {maj.tp1tr,7} {maj.neu,5} {maj.tout,5} {maj.pnl,9:F2} {maj.pnl / seed * 100m,15:F2}%");
            Console.WriteLine($"{days,-7}일 {"알트",-6} {alt.n,6} {alt.sl,5} {alt.tp1tr,7} {alt.neu,5} {alt.tout,5} {alt.pnl,9:F2} {alt.pnl / seed * 100m,15:F2}%");
            Console.WriteLine($"{days,-7}일 {"합계",-6} {allN,6} {allSl,5} {allTp,7} {allNeu,5} {allTout,5} {allPnl,9:F2} {allPnl / seed * 100m,15:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 90));
        Console.WriteLine("[해석] 알트 1차 필터링: 거래대금≥\\$50M + 24h≥+2% + 1h Vol≥평균×2 + 15m EMA50/RSI50 + BTC 1m\\|±0.3%\\| 차단");
    }

    // [v5.22.45] 사용자 차별화 전략 v2 — 메이저/알트 분리 가드 + TP/SL + BTC 가드
    //   메이저: TP+1.2%/SL-0.7%, 거래량 가드 없음, BTC 가드 없음
    //   알트:   TP+2.5%/SL-1.2%, 거래량 ≥ 전봉 × 3, BTC 1h 변동 -0.5% 초과 하락 시 진입 차단
    //   공통:   15m EMA50 위 + 5m RSI 50~70 + MACD>0 + 이격도 < 1%
    //   청산:   1차 TP → 50% 매도 + 본절 SL, 2차 trailing 고점 -0.3%
    private static async Task RunUserStrategyV2Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.45 사용자 차별화 전략 v2 (메이저/알트 분리)");
        Console.WriteLine("  메이저 TP+1.2%/SL-0.7% | 알트 TP+2.5%/SL-1.2% + Vol 3x + BTC 가드");
        Console.WriteLine("  시드 $400, 메이저 $150/슬롯 1, 알트 $100/슬롯 2");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 5m — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        decimal SelfEma(List<IBinanceKline> kl, int upTo, int period)
        {
            if (upTo + 1 < period) return 0m;
            decimal alpha = 2m / (period + 1);
            int from = Math.Max(0, upTo - period * 5);
            decimal e = kl[from].ClosePrice;
            for (int j = from + 1; j <= upTo; j++) e = kl[j].ClosePrice * alpha + e * (1 - alpha);
            return e;
        }

        decimal Ema15m50(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 50 * 3) return 0m;
            var list = new List<decimal>(50);
            for (int q = 49; q >= 0; q--)
            {
                int j = upTo - q * 3;
                if (j < 0) return 0m;
                list.Add(kl[j].ClosePrice);
            }
            decimal alpha = 2m / 51m;
            decimal e = list[0];
            for (int j = 1; j < list.Count; j++) e = list[j] * alpha + e * (1 - alpha);
            return e;
        }

        // BTC 1h 변동률 (5분봉 인덱스 기준 직전 12봉 = 1h 변화율)
        decimal Btc1hChangePct(DateTime t)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return 0m;
            int idx2 = btc.FindIndex(k => k.OpenTime > t);
            if (idx2 < 0) idx2 = btc.Count - 1; else idx2--;
            if (idx2 < 12) return 0m;
            decimal now = btc[idx2].ClosePrice;
            decimal then = btc[idx2 - 12].ClosePrice;
            return then > 0 ? (now - then) / then * 100m : 0m;
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i, string sym, bool isMajor)
        {
            if (i < 160) return false;
            // STEP 1: 15m EMA50 위
            decimal ema15 = Ema15m50(kl, i);
            if (ema15 == 0) return false;
            decimal price = kl[i].ClosePrice;
            if (price <= ema15) return false;

            // 알트만: 거래량 가드 (현재 봉 ≥ 전봉 × 3)
            if (!isMajor)
            {
                if (i == 0) return false;
                decimal vNow = (decimal)(double)kl[i].Volume;
                decimal vPrev = (decimal)(double)kl[i - 1].Volume;
                if (vPrev <= 0 || vNow < vPrev * 3m) return false;
                // BTC 가드: 1h -0.5% 이상 하락 시 차단
                if (Btc1hChangePct(kl[i].OpenTime) <= -0.5m) return false;
            }

            // STEP 2: RSI 50~70 + MACD>0 + 이격도 < 1%
            double rsi = LiveMajorEvaluator.Rsi(kl, i, 14);
            if (rsi < 50 || rsi > 70) return false;
            var macd = LiveMajorEvaluator.Macd(kl, i);
            if (macd.Hist <= 0) return false;
            decimal ema5_20 = SelfEma(kl, i, 20);
            if (ema5_20 == 0) return false;
            decimal divPct = Math.Abs(price - ema5_20) / ema5_20 * 100m;
            if (divPct > 1.0m) return false;
            return true;
        }

        const int win = 24;
        // (kind, totalPnlPctRaw)
        (string kind, decimal pct) Simulate(List<IBinanceKline> kl, int i, decimal tpPct, decimal slPct)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - slPct / 100m);
            // 직전 5m 저가 (더 가까우면 우선)
            if (i > 0)
            {
                decimal prevLow = kl[i - 1].LowPrice;
                if (prevLow > slPx && prevLow < entry) slPx = prevLow;
            }
            decimal tp1Px = entry * (1 + tpPct / 100m);

            bool tp1Hit = false;
            decimal beSl = entry;
            decimal highSinceTp1 = 0m;
            for (int k = 1; k <= win && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx)
                        return ("SL", (slPx - entry) / entry * 100m);
                    if (b.HighPrice >= tp1Px)
                    {
                        tp1Hit = true; highSinceTp1 = tp1Px;
                    }
                }
                else
                {
                    if (b.HighPrice > highSinceTp1) highSinceTp1 = b.HighPrice;
                    decimal trailStop = highSinceTp1 * (1 - 0.003m);
                    if (trailStop < beSl) trailStop = beSl;
                    if (b.LowPrice <= trailStop)
                    {
                        decimal exit = trailStop;
                        decimal half2Pct = (exit - entry) / entry * 100m;
                        decimal totalPct = tpPct * 0.5m + half2Pct * 0.5m;
                        return ("TP1+Trail", totalPct);
                    }
                }
            }
            decimal lastClose = kl[Math.Min(i + win, kl.Count - 1)].ClosePrice;
            if (tp1Hit)
            {
                decimal half2Pct = (lastClose - entry) / entry * 100m;
                decimal totalPct = tpPct * 0.5m + half2Pct * 0.5m;
                string kindClose = (Math.Abs(half2Pct) < 0.5m) ? "TP1+Neutral" : "TP1+Timeout";
                return (kindClose, totalPct);
            }
            decimal pctClose = (lastClose - entry) / entry * 100m;
            string k2 = (pctClose > -0.5m && pctClose < 0.5m) ? "Neutral" : "Timeout";
            return (k2, pctClose);
        }

        (decimal pnl, int n, int sl, int tp1tr, int neu, int tout) Eval(int days, bool majorOnly = false, bool altOnly = false)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal feeRate = FEE_RATE;
            int n = 0, sl_ = 0, tp1tr = 0, neu = 0, tout = 0;
            decimal totalPnl = 0m;

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorOnly && !isMajor) continue;
                if (altOnly && isMajor) continue;
                decimal margin = isMajor ? 150m : 100m;
                decimal notional = margin * LEVERAGE;
                decimal tpPct = isMajor ? 1.2m : 2.5m;
                decimal slPct = isMajor ? 0.7m : 1.2m;
                int lastFire = -1000;
                for (int i = 160; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i, sym, isMajor)) continue;
                    var (kind, pctRaw) = Simulate(kl, i, tpPct, slPct);
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "SL") sl_++;
                    else if (kind.StartsWith("TP1")) tp1tr++;
                    else if (kind == "Neutral") neu++;
                    else tout++;
                    lastFire = i;
                }
            }
            return (totalPnl, n, sl_, tp1tr, neu, tout);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"카테",-6} {"진입",6} {"SL",5} {"TP1+Tr",7} {"중립",5} {"타임",5} {"PnL",10} {"ROI(시드$400)",16}");
        Console.WriteLine(new string('-', 90));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var maj = Eval(days, majorOnly: true);
            var alt = Eval(days, altOnly: true);
            var all = (pnl: maj.pnl + alt.pnl, n: maj.n + alt.n,
                       sl: maj.sl + alt.sl, tp: maj.tp1tr + alt.tp1tr,
                       neu: maj.neu + alt.neu, tout: maj.tout + alt.tout);
            decimal majRoi = maj.pnl / seed * 100m;
            decimal altRoi = alt.pnl / seed * 100m;
            decimal allRoi = all.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {"메이저",-6} {maj.n,6} {maj.sl,5} {maj.tp1tr,7} {maj.neu,5} {maj.tout,5} {maj.pnl,9:F2} {majRoi,15:F2}%");
            Console.WriteLine($"{days,-7}일 {"알트",-6} {alt.n,6} {alt.sl,5} {alt.tp1tr,7} {alt.neu,5} {alt.tout,5} {alt.pnl,9:F2} {altRoi,15:F2}%");
            Console.WriteLine($"{days,-7}일 {"합계",-6} {all.n,6} {all.sl,5} {all.tp,7} {all.neu,5} {all.tout,5} {all.pnl,9:F2} {allRoi,15:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 90));
        Console.WriteLine("[해석] 메이저: 15m EMA50↑ + RSI 50~70 + MACD>0 + 이격도<1% / TP+1.2% SL-0.7%");
        Console.WriteLine("       알트:   메이저조건 + 거래량 ≥ 전봉×3 + BTC 1h ≥ -0.5% / TP+2.5% SL-1.2%");
        Console.WriteLine("       청산:   1차 TP 시 50% + 본절 / 2차 트레일링 고점 -0.3%");
    }

    // [v5.22.44] 사용자 신규 전략 — 15m EMA50 필터 + 5m RSI/MACD/이격도 진입 + 분할 익절 + 트레일링
    //   진입: 15m EMA50 위 + 5m RSI 50~70 + MACD>0 + 이격도 < 1%
    //   청산: SL min(-0.7%, 직전 5m저가) / TP1 +1.3% (50% 매도) / TP2 본절 + 고점-0.3% 트레일
    private static async Task RunUserStrategyAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.44 사용자 신규 전략 — 15m EMA50 필터 + 5m 진입 + 분할 익절");
        Console.WriteLine("  시드 $400, 메이저 $150/슬롯 1, 알트 $100/슬롯 2");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 5m — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // 5m EMA20 (Self)
        decimal Ema(List<IBinanceKline> kl, int upTo, int period)
        {
            if (upTo + 1 < period) return 0m;
            decimal alpha = 2m / (period + 1);
            int from = Math.Max(0, upTo - period * 5);
            decimal ema = kl[from].ClosePrice;
            for (int j = from + 1; j <= upTo; j++)
                ema = kl[j].ClosePrice * alpha + ema * (1 - alpha);
            return ema;
        }

        // 15m EMA50 — 5m 봉 인덱스 i 시점에서 직전 50개 15m 봉 종가의 EMA50
        //   15m 종가 = 매 3번째 5m 봉의 종가 (i 가 15m 마감점에 정렬되지 않을 수도 있으므로 가까운 15m 봉 사용)
        decimal Ema15m50(List<IBinanceKline> kl, int upTo)
        {
            if (upTo + 1 < 50 * 3) return 0m;
            // i 시점 기준 직전 50개 15m (각 3봉) 종가 = kl[upTo-2], kl[upTo-5], ..., kl[upTo-149]
            var list = new List<decimal>(50);
            for (int q = 49; q >= 0; q--)
            {
                int j = upTo - q * 3;
                if (j < 0) return 0m;
                list.Add(kl[j].ClosePrice);
            }
            decimal alpha = 2m / 51m;
            decimal ema = list[0];
            for (int j = 1; j < list.Count; j++)
                ema = list[j] * alpha + ema * (1 - alpha);
            return ema;
        }

        // MACD (Skender)
        bool MacdBullish(List<IBinanceKline> kl, int upTo)
        {
            var macd = LiveMajorEvaluator.Macd(kl, upTo);
            return macd.Hist > 0;
        }

        bool ShouldEnterLong(List<IBinanceKline> kl, int i)
        {
            if (i < 160) return false;
            // STEP 1: 15m EMA50 위
            decimal ema15 = Ema15m50(kl, i);
            if (ema15 == 0) return false;
            decimal price = kl[i].ClosePrice;
            if (price <= ema15) return false;
            // STEP 2: 5m RSI 50~70 + MACD Hist > 0 + 이격도
            double rsi = LiveMajorEvaluator.Rsi(kl, i, 14);
            if (rsi < 50 || rsi > 70) return false;
            if (!MacdBullish(kl, i)) return false;
            decimal ema5_20 = Ema(kl, i, 20);
            if (ema5_20 == 0) return false;
            decimal divPct = Math.Abs(price - ema5_20) / ema5_20 * 100m;
            if (divPct > 1.0m) return false; // 이격도 1% 초과 시 진입 금지
            return true;
        }

        // 청산 시뮬 — 분할 익절 + 트레일링
        // 반환: (kind, totalPnlPct) — pctNet 포함 (수수료/슬리피지 차감 전)
        // 5m 봉 단위 진입 후 윈도우 내 가격 시뮬
        const int win = 24; // 2시간
        (string kind, decimal pnlPct) Simulate(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - 0.007m); // -0.7% 고정 SL
            // 직전 5m 저가 (LONG 기준 더 가까우면 우선)
            if (i > 0)
            {
                decimal prevLow = kl[i - 1].LowPrice;
                if (prevLow > slPx && prevLow < entry) slPx = prevLow;
            }
            decimal tp1Px = entry * (1 + 0.013m); // +1.3% (1.2~1.5 중간)

            bool tp1Hit = false;
            decimal beSl = entry; // 본절선
            decimal highSinceTp1 = 0m;
            for (int k = 1; k <= win && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) // SL 도달 (전체 손실)
                    {
                        decimal pct = (slPx - entry) / entry * 100m;
                        return ("SL", pct);
                    }
                    if (b.HighPrice >= tp1Px) // TP1 도달 (50% 익절)
                    {
                        tp1Hit = true;
                        highSinceTp1 = tp1Px;
                        // 50% PnL 확정: +1.3% × 0.5 = +0.65%
                        // 나머지 50% trailing 시작
                    }
                }
                else
                {
                    // TP1 후 trailing
                    if (b.HighPrice > highSinceTp1) highSinceTp1 = b.HighPrice;
                    decimal trailStop = highSinceTp1 * (1 - 0.003m); // 고점 -0.3%
                    if (trailStop < beSl) trailStop = beSl;
                    if (b.LowPrice <= trailStop)
                    {
                        decimal exit = trailStop;
                        decimal half2Pct = (exit - entry) / entry * 100m;
                        decimal totalPct = 0.013m * 0.5m * 100m + half2Pct * 0.5m;
                        return ("TP1+Trail", totalPct);
                    }
                }
            }
            // 윈도우 종료
            decimal lastClose = kl[Math.Min(i + win, kl.Count - 1)].ClosePrice;
            if (tp1Hit)
            {
                decimal half2Pct = (lastClose - entry) / entry * 100m;
                decimal totalPct = 0.013m * 0.5m * 100m + half2Pct * 0.5m;
                string kindClose = (Math.Abs(half2Pct) < 0.5m) ? "TP1+Neutral" : "TP1+Timeout";
                return (kindClose, totalPct);
            }
            decimal pctClose = (lastClose - entry) / entry * 100m;
            string k2 = (pctClose > -0.5m && pctClose < 0.5m) ? "Neutral" : "Timeout";
            return (k2, pctClose);
        }

        (decimal pnl, int n, int sl, int tp1trail, int neu, int tout) Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal feeRate = FEE_RATE;
            int n = 0, sl_ = 0, tp1tr = 0, neu = 0, tout = 0;
            decimal totalPnl = 0m;

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                decimal margin = isMajor ? 150m : 100m;
                decimal notional = margin * LEVERAGE;
                int lastFire = -1000;
                for (int i = 160; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnterLong(kl, i)) continue;
                    var (kind, pctRaw) = Simulate(kl, i);
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "SL") sl_++;
                    else if (kind.StartsWith("TP1")) tp1tr++;
                    else if (kind == "Neutral") neu++;
                    else tout++;
                    lastFire = i;
                }
            }
            return (totalPnl, n, sl_, tp1tr, neu, tout);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"SL",5} {"TP1+Tr",7} {"중립",5} {"타임",5} {"PnL",10} {"ROI(시드$400)",16}");
        Console.WriteLine(new string('-', 80));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.sl,5} {r.tp1trail,7} {r.neu,5} {r.tout,5} {r.pnl,9:F2} {roi,15:F2}%");
        }
        Console.WriteLine(new string('-', 80));
        Console.WriteLine();
        Console.WriteLine("[해석] SL=손절 / TP1+Trail=1차익절 후 트레일링 (본절+ 또는 본절-) / 중립=±0.5% / 타임=Time Stop");
    }

    // [v5.22.43] 후보 전략 4개 진짜 검증 (수수료 + 슬리피지 + 미체결 PnL)
    private static async Task RunExploreStrategiesAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.43 전략 탐색 — 4개 후보 진짜 모델 (시드 $400, 90일/180일)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const int fetchPages = 36;
        const decimal slippagePct = 0.05m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // BTC 1H EMA20 추세 캐시 (전략 C 용)
        bool BtcUptrendAt(DateTime t)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return true;
            // 5분봉 인덱스에서 1H EMA20 = 12봉 EMA20
            int idx2 = btc.FindIndex(k => k.OpenTime > t);
            if (idx2 < 0) idx2 = btc.Count - 1; else idx2--;
            if (idx2 < 12 * 21) return true;
            decimal alpha = 2m / 13m;
            decimal ema = btc[idx2 - 12 * 20].ClosePrice;
            for (int j = idx2 - 12 * 20 + 12; j <= idx2; j += 12)
                ema = btc[j].ClosePrice * alpha + ema * (1 - alpha);
            int back = idx2 - 12 * 5;
            if (back < 12 * 21) return true;
            decimal emaPrev = btc[back - 12 * 20].ClosePrice;
            for (int j = back - 12 * 20 + 12; j <= back; j += 12)
                emaPrev = btc[j].ClosePrice * alpha + emaPrev * (1 - alpha);
            return ema > emaPrev;
        }

        // 전략 함수 시그니처: (kl, i) → bool
        // A: 강한 추세 + 거래량 (BB Walk 5/5 + Vol 2x + RSI 50~70)
        bool StratA(List<IBinanceKline> kl, int i)
        {
            if (i < 25) return false;
            var bb = LiveMajorEvaluator.Bb(kl, i, 20, 2);
            decimal upper = (decimal)bb.Upper;
            int wc = 0;
            for (int j = Math.Max(0, i - 4); j <= i; j++)
                if (kl[j].ClosePrice > upper) wc++;
            if (wc < 5) return false;
            int from20 = Math.Max(0, i - 19);
            decimal avgVol = 0;
            for (int j = from20; j < i; j++) avgVol += (decimal)(double)kl[j].Volume;
            avgVol /= 19;
            if ((decimal)(double)kl[i].Volume < avgVol * 2m) return false;
            double rsi = LiveMajorEvaluator.Rsi(kl, i, 14);
            return rsi >= 50 && rsi <= 70;
        }

        // B: 24h 고점 돌파 + 양봉 (24h = 5분봉 288개)
        bool StratB(List<IBinanceKline> kl, int i)
        {
            if (i < 290) return false;
            decimal high24h = kl[i - 288].HighPrice;
            for (int j = i - 287; j < i; j++) if (kl[j].HighPrice > high24h) high24h = kl[j].HighPrice;
            if (kl[i].ClosePrice <= high24h) return false; // 돌파
            if (kl[i].ClosePrice / high24h > 1.01m) return false; // 1% 이내만
            return kl[i].ClosePrice > kl[i].OpenPrice; // 양봉
        }

        // C: BTC 1h EMA20↑ 일 때만 알트 LONG (메이저는 단순 트리거)
        bool StratC(List<IBinanceKline> kl, int i, string sym)
        {
            bool isMajor = majors.Contains(sym);
            if (isMajor)
                return LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice);
            // 알트
            if (!BtcUptrendAt(kl[i].OpenTime)) return false;
            return LiveAltEvaluator.ShouldEnterLong(kl, i);
        }

        // D: 같은 트리거 + R:R 역전 (TP 1%/SL 0.5%) — Eval 시 별도 처리

        (decimal pnl, int n, int tp, int sl, int neu, int tout) Eval(int days, Func<List<IBinanceKline>, int, string, bool> stratFn,
            decimal tpPct, decimal slPct)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal feeRate = FEE_RATE;

            int n = 0, tp_ = 0, sl_ = 0, neu = 0, tout = 0;
            decimal totalPnl = 0m;
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                int win = isMajor ? 12 : 24;
                decimal margin = isMajor ? 150m : 100m;
                decimal notional = margin * LEVERAGE;
                int lastFire = -1000;
                for (int i = 290; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!stratFn(kl, i, sym)) continue;

                    decimal entry = kl[i].ClosePrice;
                    decimal tpPx = entry * (1 + tpPct / 100m);
                    decimal slPx = entry * (1 - slPct / 100m);
                    string kind = "TIMEOUT"; decimal pctRaw = 0m;
                    bool done = false;
                    for (int k = 1; k <= win && i + k < kl.Count && !done; k++)
                    {
                        var b = kl[i + k];
                        if (b.HighPrice >= tpPx && b.LowPrice <= slPx) { kind = "SL"; pctRaw = -slPct; done = true; }
                        else if (b.HighPrice >= tpPx) { kind = "TP"; pctRaw = tpPct; done = true; }
                        else if (b.LowPrice <= slPx) { kind = "SL"; pctRaw = -slPct; done = true; }
                    }
                    if (!done)
                    {
                        int idx2 = Math.Min(i + win, kl.Count - 1);
                        pctRaw = (kl[idx2].ClosePrice - entry) / entry * 100m;
                        kind = (pctRaw > -0.5m && pctRaw < 0.5m) ? "WIN_NEUTRAL" : "TIMEOUT";
                    }
                    decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                    totalPnl += notional * pctNet / 100m;
                    n++;
                    if (kind == "TP") tp_++;
                    else if (kind == "SL") sl_++;
                    else if (kind == "WIN_NEUTRAL") neu++;
                    else tout++;
                    lastFire = i;
                }
            }
            return (totalPnl, n, tp_, sl_, neu, tout);
        }

        var strategies = new List<(string name, Func<List<IBinanceKline>, int, string, bool> fn, decimal tp, decimal sl)>
        {
            ("기존(메이저+알트 단순)",  (kl,i,sym) => majors.Contains(sym) ? LiveMajorEvaluator.ShouldEnterLong(kl,i,kl[i].ClosePrice) : LiveAltEvaluator.ShouldEnterLong(kl,i),  0.7m, 1.5m),
            ("A: BB워킹5+Vol2x+RSI50-70",   (kl,i,sym) => StratA(kl,i),  0.7m, 1.5m),
            ("B: 24h고점돌파+양봉",         (kl,i,sym) => StratB(kl,i),  1.0m, 0.5m),
            ("C: BTC상승+기존알트",         (kl,i,sym) => StratC(kl,i,sym), 0.7m, 1.5m),
            ("D: 기존+R:R역전(TP1/SL0.5)", (kl,i,sym) => majors.Contains(sym) ? LiveMajorEvaluator.ShouldEnterLong(kl,i,kl[i].ClosePrice) : LiveAltEvaluator.ShouldEnterLong(kl,i), 1.0m, 0.5m),
        };

        Console.WriteLine();
        foreach (int days in new[]{ 90, 180 })
        {
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━ {days}일 ━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"{"전략",-30} {"진입",6} {"TP",5} {"SL",5} {"중립",5} {"타임",5} {"PnL($)",10} {"ROI",10}");
            Console.WriteLine(new string('-', 90));
            foreach (var s in strategies)
            {
                var r = Eval(days, s.fn, s.tp, s.sl);
                decimal roi = r.pnl / seed * 100m;
                Console.WriteLine($"{s.name,-30} {r.n,6} {r.tp,5} {r.sl,5} {r.neu,5} {r.tout,5} {r.pnl,9:F2} {roi,9:F2}%");
            }
            Console.WriteLine();
        }
        Console.WriteLine("[해석] +수익 + 중립 비율 낮은 전략이 라이브에서 유리");
    }

    // [v5.22.42] 진짜 검증 — 미체결 PnL 반영 + 수수료 + 슬리피지 + PnL 분포 + MDD
    //   기존 RunLiveStatsV2Async 의 가짜 ROI (+3,019%) 와 비교용
    private static async Task RunValidateRealisticAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.42 진짜 검증 — 미체결 PnL + 수수료 + 슬리피지 + 분포 + MDD");
        Console.WriteLine("  시드 $400, 메이저 마진 $150/슬롯 1, 알트 $100/슬롯 2");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal majorMargin = 150m;
        const decimal altMargin = 100m;
        const int majorSlot = 1;
        const int altSlot = 2;
        const decimal slippagePct = 0.05m; // 0.05% 시장가 슬리피지 (양방향 = 0.10%)
        const int fetchPages = 36;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // 진짜 OutcomeIn — TP/SL/미체결 모두 PnL 반환
        // 반환: (kind, pnlPct) — kind: TP / SL / TIMEOUT / WIN_NEUTRAL
        (string kind, decimal pnlPct) RealOutcome(List<IBinanceKline> kl, int i, decimal tpPct, decimal slPct, int win)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= win && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct);
                if (b.HighPrice >= tpPx) return ("TP", tpPct);
                if (b.LowPrice <= slPx) return ("SL", -slPct);
            }
            // win 시점 종가 - 진입가 (Time Stop 청산)
            int idxClose = Math.Min(i + win, kl.Count - 1);
            decimal exitPx = kl[idxClose].ClosePrice;
            decimal pct = (exitPx - entry) / entry * 100m;
            string k2 = (pct > -0.5m && pct < 0.5m) ? "WIN_NEUTRAL" : "TIMEOUT";
            return (k2, pct);
        }

        // 슬롯 시뮬 + 진짜 PnL
        (decimal totalPnl, int n, int tpN, int slN, int neuN, int toutN, decimal mdd, decimal mddPct, List<decimal> dailyPnl) Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal feeRate = FEE_RATE;

            var candidates = new List<(DateTime time, string sym, bool isMajor, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                int win = isMajor ? 12 : 24;
                int lastFire = -1000;
                for (int i = 50; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    bool fire = isMajor
                        ? LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice)
                        : LiveAltEvaluator.ShouldEnterLong(kl, i);
                    if (!fire) continue;
                    candidates.Add((kl[i].OpenTime, sym, isMajor, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var majorActive = new List<DateTime>();
            var altActive = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, neuN = 0, toutN = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                majorActive.RemoveAll(t => t <= c.time);
                altActive.RemoveAll(t => t <= c.time);
                bool slotOk = c.isMajor ? majorActive.Count < majorSlot : altActive.Count < altSlot;
                if (!slotOk) continue;

                decimal tpPct = c.isMajor ? 0.5m : 1.0m;
                decimal slPct = c.isMajor ? 1.5m : 3.0m;
                int win = c.isMajor ? 12 : 24;
                decimal margin = c.isMajor ? majorMargin : altMargin;
                decimal notional = margin * LEVERAGE;

                var (kind, pctRaw) = RealOutcome(fullData[c.sym], c.barIdx, tpPct, slPct, win);
                // 수수료 + 슬리피지 차감 — 라운드트립 0.08% + 0.10% = 0.18%
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;

                totalPnl += pnlUsd;
                n++;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "WIN_NEUTRAL") neuN++;
                else toutN++;

                var kl = fullData[c.sym];
                int endBar = Math.Min(c.barIdx + win / 2, kl.Count - 1);
                DateTime endTime = kl[endBar].OpenTime;
                if (c.isMajor) majorActive.Add(endTime); else altActive.Add(endTime);

                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            // MDD 계산
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            return (totalPnl, n, tpN, slN, neuN, toutN, mdd, mddPct, byDay.Values.ToList());
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"중립",5} {"타임",5} {"PnL",10} {"ROI(시드$400)",16} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 95));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.totalPnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.neuN,5} {r.toutN,5} {r.totalPnl,9:F2} {roi,15:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 95));
        Console.WriteLine();
        Console.WriteLine("[해석] TP=익절 / SL=손절 / 중립=±0.5% 이내 종료 (수수료만 손실) / 타임=Time Stop 청산 (±0.5% 초과)");
        Console.WriteLine("       수수료 0.08% + 슬리피지 0.10% = 라운드트립 0.18% 차감 적용");
        Console.WriteLine("       MDD = 누적 PnL 최대 낙폭, MDD% = 최대 낙폭 / (시드+최고점) × 100");
    }

    // [v5.22.53 라이브 백테스트] 15m 봉 마감 1회 발화 — 라이브 봇 100% 시뮬
    //   라이브 v5.22.52 동작:
    //     1. 15m kline 사용 (5m 아님)
    //     2. 진입은 봉 마감 1회만 (같은 봉 재발화 금지)
    //     3. 5중 가드: EMA20 위 + 이격도≤2.5% + BBW<5% + 돌파+vol×2 + RSI<75
    //     4. 메이저 60분(4봉) / 알트 120분(8봉) Time Stop
    //     5. RSI≥80 도달 후 꺾임 + ROE>0.3%×lev → 즉시 청산
    //   진짜 모델: 수수료 0.08% + 슬리피지 0.10% + 미체결 PnL + MDD
    private static async Task<List<IBinanceKline>?> FetchKlines15mPageAsync(string sym, long endMs, int limit)
    {
        for (int t = 1; t <= 4; t++)
        {
            try
            {
                await Task.Delay(800);
                var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={sym}&interval=15m&limit={limit}&endTime={endMs}";
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

    private static async Task<List<IBinanceKline>> FetchKlines15mAsync(string sym, int pages = 12)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < pages; p++)
        {
            var page = await FetchKlines15mPageAsync(sym, endMs, BARS_PER_REQ);
            if (page == null || page.Count == 0) break;
            all.Insert(0, page);
            endMs = ((DateTimeOffset)page[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            if (page.Count < BARS_PER_REQ) break;
        }
        return all.SelectMany(c => c).ToList();
    }

    private static async Task RunLiveRealisticAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.53 라이브 봇 100% 시뮬 — 15m 봉 마감 1회 발화");
        Console.WriteLine("  5중가드 + RSI80꺾임익절 + Time Stop (메이저 60분/알트 120분)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal majorMargin = 150m;
        const decimal altMargin = 100m;
        const int majorSlot = 1;
        const int altSlot = 2;
        const decimal tpPct = 1.0m;
        const decimal slPct = 3.0m;
        const decimal slippagePct = 0.05m;
        const int majorWinBars = 4;        // 60분 = 15m × 4
        const int altWinBars = 8;          // 120분 = 15m × 8
        const int fetchPages = 12;          // 180일 (15m × 17280 = 12 페이지)

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (15m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, fetchPages);
                if (kl.Count < 200) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // 라이브 5중 가드 진입 판정 (15m kline, i = 마감 봉)
        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 21) return false;
            // 1. EMA20: 현재 봉까지의 종가로 EMA20 계산
            decimal ema20 = Ema(kl, i, 20);
            if (kl[i].ClosePrice <= ema20) return false;
            // 2. BB(20,2)
            double sum = 0;
            for (int q = i - 19; q <= i; q++) sum += (double)kl[q].ClosePrice;
            double mean = sum / 20.0;
            double sq = 0;
            for (int q = i - 19; q <= i; q++) { double d = (double)kl[q].ClosePrice - mean; sq += d * d; }
            double sd = Math.Sqrt(sq / 20.0);
            decimal mid = (decimal)mean;
            decimal upper = (decimal)(mean + 2 * sd);
            decimal lower = (decimal)(mean - 2 * sd);
            if (mid <= 0) return false;
            decimal distMid = (kl[i].ClosePrice - mid) / mid * 100m;
            if (distMid > 2.5m) return false;
            decimal bbw = (upper - lower) / mid * 100m;
            if (bbw >= 5.0m) return false;
            if (kl[i].ClosePrice <= upper) return false;
            // 3. 거래량 > 직전 5봉 평균 × 2
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 2m) return false;
            // 4. RSI < 75
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 75) return false;
            return true;
        }

        // 라이브 청산 시뮬: TP/SL 우선 → 매 봉 RSI80꺾임 체크 → Time Stop
        // 반환: (kind, pctRaw, holdBars)
        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i, int winBars, decimal lev)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= winBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                // TP/SL 봉 내 동시 → 보수적 SL
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                // RSI80 꺾임 익절 — k≥2 부터 가능 (직전 RSI 필요)
                if (k >= 2)
                {
                    double rsiCurr = CalcRsi14(kl, i + k);
                    double rsiPrev = CalcRsi14(kl, i + k - 1);
                    if (rsiPrev >= 80.0 && rsiCurr < rsiPrev)
                    {
                        decimal pct = (b.ClosePrice - entry) / entry * 100m;
                        if (pct * lev > 0.3m * lev)   // ROE>0.3%×lev (= 0.3% 가격 변동)
                            return ("RSI_EXIT", pct, k);
                    }
                }
            }
            // Time Stop
            int idxClose = Math.Min(i + winBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, winBars);
        }

        (decimal pnl, int n, int tpN, int slN, int rsiN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;

            var candidates = new List<(DateTime time, string sym, bool isMajor, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                int winBars = isMajor ? majorWinBars : altWinBars;
                DateTime lastFireTime = DateTime.MinValue;
                for (int i = 25; i < kl.Count - winBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    // 봉 마감 1회 — 같은 봉 재발화 금지 (자동 보장: 한 i 당 한 번만 평가)
                    // 30분 cooldown (= 2 × 15m 봉)
                    if ((kl[i].OpenTime - lastFireTime).TotalMinutes < 30) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, isMajor, i));
                    lastFireTime = kl[i].OpenTime;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var majorActive = new List<DateTime>();
            var altActive = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, rsiN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                majorActive.RemoveAll(t => t <= c.time);
                altActive.RemoveAll(t => t <= c.time);
                bool slotOk = c.isMajor ? majorActive.Count < majorSlot : altActive.Count < altSlot;
                if (!slotOk) continue;

                int winBars = c.isMajor ? majorWinBars : altWinBars;
                decimal margin = c.isMajor ? majorMargin : altMargin;
                decimal notional = margin * LEVERAGE;

                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx, winBars, LEVERAGE);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "RSI_EXIT") rsiN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                DateTime endTime = fullData[c.sym][endBar].OpenTime;
                if (c.isMajor) majorActive.Add(endTime); else altActive.Add(endTime);

                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, rsiN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  시드 ${seed} | 메이저 ${majorMargin}/슬롯 1 | 알트 ${altMargin}/슬롯 2 | {LEVERAGE:F0}x");
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% (15x → ROE +15% / -45%)");
        Console.WriteLine($"  메이저 {majorWinBars}봉(60분) / 알트 {altWinBars}봉(120분) Time Stop");
        Console.WriteLine($"  RSI80꺾임익절 ON / 5중가드 라이브 동일 / 30분 cooldown");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"RSI",5} {"BE",5} {"타임",5} {"평균보유",10} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 110));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.rsiN,5} {r.beN,5} {r.toN,5} {r.avgHold,9:F1}봉 {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 110));
        Console.WriteLine();
        Console.WriteLine("[해석] TP=익절 / SL=손절 / RSI=RSI80꺾임 익절 / BE=Time Stop ±0.3% 이내 / 타임=Time Stop ±0.3% 초과");
        Console.WriteLine("       수수료 0.08% + 슬리피지 0.10% = 라운드트립 0.18% 차감");
    }

    // [전략 #11b] Daily Swing 파라미터 sweep — TP 5/8/10/15% × SL 3/5/7% × 보유 7/14/21일
    private static async Task RunDailySwingSweepAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #11b — Daily Swing 파라미터 SWEEP (TP×SL×보유 36조합)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;
        const int maxSlots = 2;
        const decimal slippagePct = 0.05m;
        const decimal swingLeverage = 5m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch — {symbols.Length}개 심볼 (1D)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1dAsync(sym, 1);
                if (kl.Count < 60) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 51) return false;
            decimal sumS = 0m;
            for (int q = i - 19; q <= i; q++) sumS += kl[q].ClosePrice;
            decimal sma20 = sumS / 20m;
            decimal sumL = 0m;
            for (int q = i - 49; q <= i; q++) sumL += kl[q].ClosePrice;
            decimal sma50 = sumL / 50m;
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 65.0) return false;
            decimal volAvg = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume;
            volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i, decimal tpPct, decimal slPct, int maxHoldBars)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 1.0m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, decimal mdd, decimal mddPct) Eval(int days, decimal tpPct, decimal slPct, int maxHoldBars)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                for (int i = 51; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * swingLeverage;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx, tpPct, slPct, maxHoldBars);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            return (totalPnl, n, tpN, slN, mdd, mddPct);
        }

        decimal[] tps = { 5m, 8m, 10m, 15m };
        decimal[] sls = { 3m, 5m, 7m };
        int[] holds = { 7, 14, 21 };
        int[] periods = { 180, 365 };

        Console.WriteLine();
        foreach (int days in periods)
        {
            Console.WriteLine($"==== {days}일 결과 (정렬: PnL desc) ====");
            Console.WriteLine($"{"TP%",4} {"SL%",4} {"보유",4} {"진입",6} {"TP",4} {"SL",4} {"PnL",10} {"ROI",10} {"MDD%",8}");
            Console.WriteLine(new string('-', 75));
            var rows = new List<(decimal tp, decimal sl, int hold, decimal pnl, int n, int tpN, int slN, decimal mddPct, decimal roi)>();
            foreach (var tp in tps)
            foreach (var sl in sls)
            foreach (var h in holds)
            {
                var r = Eval(days, tp, sl, h);
                decimal roi = r.pnl / seed * 100m;
                rows.Add((tp, sl, h, r.pnl, r.n, r.tpN, r.slN, r.mddPct, roi));
            }
            foreach (var r in rows.OrderByDescending(r => r.pnl))
                Console.WriteLine($"{r.tp,4:F1} {r.sl,4:F1} {r.hold,4} {r.n,6} {r.tpN,4} {r.slN,4} {r.pnl,9:F2} {r.roi,9:F2}% {r.mddPct,7:F2}%");
            Console.WriteLine();
        }
    }

    // [전략 #11] Daily Swing — 1D 봉 추세 + 큰 TP + 긴 보유
    //   원칙: 거래 빈도 ↓ + 거래당 EV ↑ (수수료 비율 최소화)
    //   진입: 1D 종가 > 20SMA + 20SMA>50SMA(추세) + RSI 50~65 + vol > 5봉평균×1.5
    //   청산: TP +10%, SL -5%, max 14일 보유
    //   레버리지 5x: TP=+50% ROE, SL=-25% ROE
    private static async Task<List<IBinanceKline>?> FetchKlines1dPageAsync(string sym, long endMs, int limit)
    {
        for (int t = 1; t <= 4; t++)
        {
            try
            {
                await Task.Delay(800);
                var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={sym}&interval=1d&limit={limit}&endTime={endMs}";
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
            { await Task.Delay(t * 5000); }
            catch { return null; }
        }
        return null;
    }
    private static async Task<List<IBinanceKline>> FetchKlines1dAsync(string sym, int pages = 1)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < pages; p++)
        {
            var page = await FetchKlines1dPageAsync(sym, endMs, BARS_PER_REQ);
            if (page == null || page.Count == 0) break;
            all.Insert(0, page);
            endMs = ((DateTimeOffset)page[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            if (page.Count < BARS_PER_REQ) break;
        }
        return all.SelectMany(c => c).ToList();
    }

    private static async Task RunDailySwingAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #11 — Daily Swing (저빈도 + 큰 TP + 긴 보유)");
        Console.WriteLine("  1D close>20SMA + 20SMA>50SMA + RSI 50~65 + vol×1.5 / TP+10% SL-5% max 14일");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;          // 슬롯당 마진 ↑
        const int maxSlots = 2;                // 슬롯 ↓
        const decimal tpPct = 10.0m;
        const decimal slPct = 5.0m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 1;             // 1D × 1500봉 = 4년치
        const int maxHoldBars = 14;           // 14일 max
        const decimal swingLeverage = 5m;     // 일봉 = 변동성 큼 → 레버리지 ↓

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch — {symbols.Length}개 심볼 (1D)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1dAsync(sym, fetchPages);
                if (kl.Count < 60) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 51) return false;
            // 20 SMA
            decimal sumS = 0m;
            for (int q = i - 19; q <= i; q++) sumS += kl[q].ClosePrice;
            decimal sma20 = sumS / 20m;
            // 50 SMA
            decimal sumL = 0m;
            for (int q = i - 49; q <= i; q++) sumL += kl[q].ClosePrice;
            decimal sma50 = sumL / 50m;
            // 추세 확인
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            // RSI 50~65
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 65.0) return false;
            // 거래량 > 직전 5봉 평균 × 1.5
            decimal volAvg = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume;
            volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            // 양봉
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 1.0m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                for (int i = 51; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * swingLeverage;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {swingLeverage}x | TP+{tpPct}% / SL-{slPct}% / max {maxHoldBars}일");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유일",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180, 365 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [v5.22.54 검증] 새 동적 풀 선정 (양수 변동률 × log(거래대금)) + 5중 가드 진입
    //   라이브의 200+ 풀과 다르지만, 30개 풀 내에서도 선정 로직 차이로 PnL 검증 가능
    private static async Task RunDynPoolV5_22_54Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.54 검증 — 새 동적 풀 (양수 변동률×log(거래대금)) + 5중 가드");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tpPct = 1.0m;
        const decimal slPct = 1.5m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 12;
        const int maxHoldBars = 24;
        const int rescanBars = 36;     // 15분 갱신 = 5m × 3 = 9봉, but 정확히 15min/5m = 3봉. 여기 단위 confused.
                                        // 5m 봉 기준 9봉 = 45분. 15분 갱신 = 3봉.
        const int rescanBars5m = 3;    // 15min / 5m = 3봉

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (5m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 24h price change percent + 24h quote volume — 시점별
        var change24hAt = new Dictionary<string, decimal[]>();
        var qVol24hAt = new Dictionary<string, decimal[]>();
        foreach (var kv in fullData)
        {
            var kl = kv.Value;
            int n = kl.Count;
            var ch = new decimal[n];
            var qv = new decimal[n];
            decimal sum = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal q = kl[i].QuoteVolume > 0 ? kl[i].QuoteVolume : kl[i].ClosePrice * kl[i].Volume;
                sum += q;
                if (i >= 288)
                {
                    decimal qO = kl[i - 288].QuoteVolume > 0
                        ? kl[i - 288].QuoteVolume
                        : kl[i - 288].ClosePrice * kl[i - 288].Volume;
                    sum -= qO;
                    decimal p0 = kl[i - 288].ClosePrice;
                    if (p0 > 0) ch[i] = (kl[i].ClosePrice - p0) / p0 * 100m;
                }
                qv[i] = sum;
            }
            change24hAt[kv.Key] = ch;
            qVol24hAt[kv.Key] = qv;
        }

        // 15분 단위 동적 풀 — 양수 변동률만, score = ch × log10(qv)
        DateTime tStart = fullData.Values.Max(v => v[0].OpenTime);
        DateTime tEnd = fullData.Values.Min(v => v[^1].OpenTime);
        var timeIdx = new Dictionary<string, Dictionary<DateTime, int>>();
        foreach (var kv in fullData)
        {
            var d = new Dictionary<DateTime, int>(kv.Value.Count);
            for (int i = 0; i < kv.Value.Count; i++) d[kv.Value[i].OpenTime] = i;
            timeIdx[kv.Key] = d;
        }
        var poolCache = new SortedDictionary<DateTime, HashSet<string>>();
        var symList = fullData.Keys.ToList();
        for (DateTime t = tStart; t <= tEnd; t = t.AddMinutes(5 * rescanBars5m))
        {
            var ranked = new List<(string s, double score)>();
            foreach (var s in symList)
            {
                if (!timeIdx[s].TryGetValue(t, out int i)) continue;
                if (i < 288) continue;
                decimal ch = change24hAt[s][i];
                if (ch <= 0m) continue;     // 양수 변동률만
                double qv = (double)qVol24hAt[s][i];
                double score = (double)ch * Math.Log10(Math.Max(1.0, qv));
                ranked.Add((s, score));
            }
            poolCache[t] = ranked.OrderByDescending(r => r.score).Take(50).Select(r => r.s).ToHashSet();
        }
        HashSet<string> PoolAt(DateTime t)
        {
            int totalMin = (int)(t - tStart).TotalMinutes;
            int bucket = totalMin / (5 * rescanBars5m);
            DateTime k = tStart.AddMinutes(bucket * 5 * rescanBars5m);
            return poolCache.TryGetValue(k, out var set) ? set : new HashSet<string>();
        }

        // 5중 가드 진입 — v5.22.52 라이브 동일 (5m kline 근사)
        bool ShouldEnter(string sym, int i)
        {
            var kl = fullData[sym];
            if (i < 25) return false;
            if (!PoolAt(kl[i].OpenTime).Contains(sym)) return false;
            decimal e1 = Ema(kl, i, 20);
            decimal e0 = Ema(kl, i - 5, 20);
            if (e1 <= e0) return false;
            if (kl[i].ClosePrice <= e1) return false;
            double sum = 0;
            for (int q = i - 19; q <= i; q++) sum += (double)kl[q].ClosePrice;
            double mean = sum / 20.0;
            double sq = 0;
            for (int q = i - 19; q <= i; q++) { double d = (double)kl[q].ClosePrice - mean; sq += d * d; }
            double sd = Math.Sqrt(sq / 20.0);
            decimal mid = (decimal)mean;
            decimal upper = (decimal)(mean + 2 * sd);
            decimal lower = (decimal)(mean - 2 * sd);
            if (mid <= 0) return false;
            decimal distMid = (kl[i].ClosePrice - mid) / mid * 100m;
            if (distMid > 2.5m) return false;
            decimal bbw = (upper - lower) / mid * 100m;
            if (bbw >= 5.0m) return false;
            if (kl[i].ClosePrice <= upper) return false;
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 2m) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 75.0) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 6;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 290; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(sym, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% / 동적풀 50개 15분갱신 / 5중가드");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [전략 #10] True Scalping — 1m봉 단타 (max 30분)
    //   진입 (하락 추세 절대 금지):
    //     1. BTC 5분봉 60봉(1h) 누적 변화율 > 0 (시장 전체 상승)
    //     2. 종목 5분봉 12봉(1h) 누적 변화율 > 0 (개별 상승)
    //     3. 24h 변화율 ≥ +5% (펌핑 중)
    //     4. 1m 종가 > 직전 5봉 고점 (브레이크아웃)
    //     5. 1m 거래량 > 직전 10봉 평균 × 3 (큰 자본)
    //     6. 1m RSI < 75 + 양봉
    //   청산: TP +0.7%, SL -0.4%, max 30봉(30분)
    private static async Task<List<IBinanceKline>?> FetchKlines1mPageAsync(string sym, long endMs, int limit)
    {
        for (int t = 1; t <= 4; t++)
        {
            try
            {
                await Task.Delay(800);
                var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={sym}&interval=1m&limit={limit}&endTime={endMs}";
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
            { await Task.Delay(t * 5000); }
            catch { return null; }
        }
        return null;
    }
    private static async Task<List<IBinanceKline>> FetchKlines1mAsync(string sym, int pages = 3)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < pages; p++)
        {
            var page = await FetchKlines1mPageAsync(sym, endMs, BARS_PER_REQ);
            if (page == null || page.Count == 0) break;
            all.Insert(0, page);
            endMs = ((DateTimeOffset)page[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            if (page.Count < BARS_PER_REQ) break;
        }
        return all.SelectMany(c => c).ToList();
    }

    private static async Task RunTrueScalpingAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #10 — True Scalping (1분봉 단타 max 30분 보유)");
        Console.WriteLine("  하락추세 진입금지: BTC 1h↑ + 종목 1h↑ + 24h+5% + 1m 5봉돌파 + vol×3");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tpPct = 1.5m;       // v3: 비대칭 1:3
        const decimal slPct = 0.5m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 3;            // 1m × 4500봉 = 3.1일 (실 단타 검증)
        const int maxHoldBars = 30;          // 30분 max

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch ~3일치 — {symbols.Length}개 심볼 (1m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1mAsync(sym, fetchPages);
                if (kl.Count < 1500) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // BTC 1h 추세 가드 — BTC 1m 데이터로 60봉(1h) 누적 변화율
        bool BtcUp(int i)
        {
            if (!fullData.TryGetValue("BTCUSDT", out var btc)) return true;   // BTC 없으면 패스
            // 동시간 BTC i 인덱스 (1m 동기 가정)
            if (i < 60 || i >= btc.Count) return true;
            decimal p0 = btc[i - 60].ClosePrice;
            if (p0 <= 0) return true;
            decimal pct = (btc[i].ClosePrice - p0) / p0 * 100m;
            return pct > 0m;
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 1500) return false;          // 24h = 1440봉
            // 0. 하락 추세 절대 금지 — BTC 1h ↑
            if (!BtcUp(i)) return false;
            // 1. 종목 1h 누적 변화율 > 0
            decimal price1h = kl[i - 60].ClosePrice;
            if (price1h <= 0) return false;
            if (kl[i].ClosePrice <= price1h) return false;
            // 2. 24h 변화율 > +5%
            decimal price24h = kl[i - 1440].ClosePrice;
            if (price24h <= 0) return false;
            decimal change24h = (kl[i].ClosePrice - price24h) / price24h * 100m;
            if (change24h < 5m) return false;
            // v3: 1m EMA20 풀백 + 양봉 반등 진입
            // 3. 직전 봉 저가가 EMA20 근처 (-0.3% ~ +0.5% 범위) — 풀백 발생
            decimal ema20 = Ema(kl, i, 20);
            decimal prevLow = kl[i - 1].LowPrice;
            decimal lowOffset = (prevLow - ema20) / ema20 * 100m;
            if (lowOffset < -0.3m || lowOffset > 0.5m) return false;
            // 4. 현재 봉 양봉 + 종가 > EMA20 (반등 시작)
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            if (kl[i].ClosePrice <= ema20) return false;
            // 5. 현재 봉 거래량 > 직전 10봉 평균 × 1.5 (반등 확인)
            decimal volAvg = 0m;
            for (int q = i - 10; q <= i - 1; q++) volAvg += kl[q].Volume;
            volAvg /= 10m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            // 6. RSI 35~65 (과열/과매도 회피)
            double rsi = CalcRsi14(kl, i);
            if (rsi < 35.0 || rsi > 65.0) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.2m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 30;       // 30분 cooldown

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 1500; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% / max {maxHoldBars}분(30봉) / cooldown 30분 / 24h+5% 펌핑종목만");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유분",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 1, 2, 3 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [전략 #9] Hot Mover — 거래대금 상위 종목 + 단순 추세 진입
    //   동적 풀: 매 1시간, 직전 24h quoteVolume 상위 15개만 진입 대상
    //   진입: 5m 종가 > EMA20 + 양봉 + 직전 5봉 vol×2 + RSI 40~70
    //   청산: TP1 +1.5% 50% + 본전 / TP2 +4% / SL -1.5%
    private static async Task RunHotMoverAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #9 — Hot Mover (동적 거래대금 상위 15)");
        Console.WriteLine("  핵심: 봇이 항상 지금 가장 활발한 종목만 본다");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tp1Pct = 1.5m;
        const decimal tp2Pct = 4.0m;
        const decimal slPct = 1.5m;
        const decimal bePadPct = 0.2m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;
        const int maxHoldBars = 96;          // 8h max
        const int rescanBars = 12;           // 1시간마다 풀 갱신

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (5m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 24h quoteVolume 누적 (288봉 슬라이딩)
        var qVolAt = new Dictionary<string, decimal[]>();
        foreach (var kv in fullData)
        {
            var kl = kv.Value;
            int n = kl.Count;
            var qv = new decimal[n];
            decimal sum = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal q = kl[i].QuoteVolume > 0 ? kl[i].QuoteVolume : kl[i].ClosePrice * kl[i].Volume;
                sum += q;
                if (i >= 288)
                {
                    decimal qO = kl[i - 288].QuoteVolume > 0
                        ? kl[i - 288].QuoteVolume
                        : kl[i - 288].ClosePrice * kl[i - 288].Volume;
                    sum -= qO;
                }
                qv[i] = sum;
            }
            qVolAt[kv.Key] = qv;
        }

        // 시간 그리드 — 모든 심볼 공통 시작
        DateTime tStart = fullData.Values.Max(v => v[0].OpenTime);
        DateTime tEnd = fullData.Values.Min(v => v[^1].OpenTime);
        var timeIdx = new Dictionary<string, Dictionary<DateTime, int>>();
        foreach (var kv in fullData)
        {
            var d = new Dictionary<DateTime, int>(kv.Value.Count);
            for (int i = 0; i < kv.Value.Count; i++) d[kv.Value[i].OpenTime] = i;
            timeIdx[kv.Key] = d;
        }
        // 1시간 단위 top15 캐시
        var topCache = new SortedDictionary<DateTime, HashSet<string>>();
        var symList = fullData.Keys.ToList();
        for (DateTime t = tStart; t <= tEnd; t = t.AddMinutes(5 * rescanBars))
        {
            var ranked = new List<(string s, decimal qv)>();
            foreach (var s in symList)
            {
                if (!timeIdx[s].TryGetValue(t, out int i)) continue;
                ranked.Add((s, qVolAt[s][i]));
            }
            topCache[t] = ranked.OrderByDescending(r => r.qv).Take(15).Select(r => r.s).ToHashSet();
        }
        HashSet<string> TopAt(DateTime t)
        {
            int totalMin = (int)(t - tStart).TotalMinutes;
            int bucket = totalMin / (5 * rescanBars);
            DateTime k = tStart.AddMinutes(bucket * 5 * rescanBars);
            return topCache.TryGetValue(k, out var set) ? set : new HashSet<string>();
        }

        bool ShouldEnter(string sym, int i)
        {
            var kl = fullData[sym];
            if (i < 25) return false;
            if (!TopAt(kl[i].OpenTime).Contains(sym)) return false;     // top15 가드
            decimal ema20 = Ema(kl, i, 20);
            if (kl[i].ClosePrice <= ema20) return false;                // 추세 위
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;      // 양봉
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 2m) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 40.0 || rsi > 70.0) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tp1Px = entry * (1 + tp1Pct / 100m);
            decimal tp2Px = entry * (1 + tp2Pct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal bePx = entry * (1 + bePadPct / 100m);
            bool tp1Hit = false; decimal halfPnl = 0m; int holdK = maxHoldBars;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; halfPnl = tp1Pct / 2m; }
                }
                else
                {
                    if (b.LowPrice <= bePx) return ("BE_LOCK", halfPnl + bePadPct / 2m, k);
                    if (b.HighPrice >= tp2Px) return ("TP2", halfPnl + tp2Pct / 2m, k);
                }
                holdK = k;
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            if (tp1Hit) { decimal p = halfPnl + pctTo / 2m; return (p > 0 ? "TIMEOUT_TP" : "TIMEOUT_SL", p, holdK); }
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, holdK);
        }

        (decimal pnl, int n, int tp2N, int beLockN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 12;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 50; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(sym, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tp2N = 0, beLockN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP2" || kind == "TIMEOUT_TP") tp2N++;
                else if (kind == "BE_LOCK") beLockN++;
                else if (kind == "SL" || kind == "TIMEOUT_SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tp2N, beLockN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP1+{tp1Pct}% 50% / TP2+{tp2Pct}% 50% / 본전+{bePadPct}% / SL-{slPct}% / max {maxHoldBars}봉(8h) / 동적 top15 1h갱신");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP2",5} {"BELK",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tp2N,5} {r.beLockN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [전략 #8] Pump Pullback Entry — 강한 펌핑 종목의 첫 눌림 매수
    //   진입:
    //     1. 24h 변화율 ≥ +10% (강한 펌핑 진행 중)
    //     2. 1H 직전 -3% 이상 눌림 (FOMO 매도)
    //     3. 5m 양봉 + 거래량 > 직전 5봉 평균 × 2 (반등 시작)
    //     4. RSI(14) on 5m: 30~50 (과매도 회복 구간)
    //   청산: TP1 +2% 절반 + 본전, TP2 +5%, SL -2%
    private static async Task RunPumpPullbackAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #8 — Pump Pullback Entry (펌핑 종목 첫 눌림)");
        Console.WriteLine("  24h+10% + 1H-3% 눌림 + 5m 양봉 vol×2 + RSI 30~50");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tp1Pct = 2.0m;
        const decimal tp2Pct = 5.0m;
        const decimal slPct = 2.0m;
        const decimal bePadPct = 0.2m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;
        const int maxHoldBars = 144;          // 12h max (5m × 144)

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (5m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 300) return false;
            // 1. 24h 변화율 ≥ +10% (= 288봉 5m 전 종가 대비)
            decimal price24hAgo = kl[i - 288].ClosePrice;
            if (price24hAgo <= 0) return false;
            decimal change24h = (kl[i].ClosePrice - price24hAgo) / price24hAgo * 100m;
            if (change24h < 10m) return false;
            // 2. 1H 직전 -3% 이상 눌림 (12봉 5m 내 최고가 → 현재가 -3%)
            decimal hi12 = kl[i - 11].HighPrice;
            for (int q = i - 11; q <= i; q++) if (kl[q].HighPrice > hi12) hi12 = kl[q].HighPrice;
            decimal pullback = (hi12 - kl[i].ClosePrice) / hi12 * 100m;
            if (pullback < 3m) return false;
            // 3. 5m 양봉 + 거래량 > 직전 5봉 평균 × 2
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 2m) return false;
            // 4. RSI 30~50 (과매도 회복 구간)
            double rsi = CalcRsi14(kl, i);
            if (rsi < 30.0 || rsi > 50.0) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tp1Px = entry * (1 + tp1Pct / 100m);
            decimal tp2Px = entry * (1 + tp2Pct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal bePx = entry * (1 + bePadPct / 100m);
            bool tp1Hit = false;
            decimal halfPnl = 0m;
            int holdK = maxHoldBars;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; halfPnl = tp1Pct / 2m; }
                }
                else
                {
                    if (b.LowPrice <= bePx) { return ("BE_LOCK", halfPnl + bePadPct / 2m, k); }
                    if (b.HighPrice >= tp2Px) { return ("TP2", halfPnl + tp2Pct / 2m, k); }
                }
                holdK = k;
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            if (tp1Hit) { decimal p = halfPnl + pctTo / 2m; return (p > 0 ? "TIMEOUT_TP" : "TIMEOUT_SL", p, holdK); }
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, holdK);
        }

        (decimal pnl, int n, int tp2N, int beLockN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 24;       // 2h cooldown

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 300; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tp2N = 0, beLockN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP2" || kind == "TIMEOUT_TP") tp2N++;
                else if (kind == "BE_LOCK") beLockN++;
                else if (kind == "SL" || kind == "TIMEOUT_SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tp2N, beLockN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP1+{tp1Pct}% 50% / TP2+{tp2Pct}% 50% / 본전+{bePadPct}% / SL-{slPct}% / max {maxHoldBars}봉(12h)");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP2",5} {"BELK",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tp2N,5} {r.beLockN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [전략 #7] Selective Bull — 4중 강세장 필터 + 종목별 강세 + 분할 익절
    //   진입 조건 모두 충족:
    //     1. BTC 4H EMA50 > EMA200 (장기 강세장)
    //     2. BTC 직전 24h 변화율 > 0 (오늘도 강세)
    //     3. 종목 4H EMA50 > EMA200 (개별 강세)
    //     4. 종목 1H 종가 > EMA20 + RSI<70
    //   청산: TP1 +1% 절반 청산 + 본전 이동, TP2 +2.5%, SL -1%
    private static async Task RunSelectiveBullAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #7 — Selective Bull (4중 강세장 필터 + 분할 익절)");
        Console.WriteLine("  BTC 4H EMA50>200 + BTC 24h ↑ + 종목 4H EMA50>200 + 1H EMA20↑");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tp1Pct = 1.0m;
        const decimal tp2Pct = 2.5m;
        const decimal slPct = 1.0m;
        const decimal bePadPct = 0.2m;
        const decimal slippagePct = 0.05m;
        const int fetchPages1h = 3;
        const int maxHoldBars = 48;          // 48h max

        Console.WriteLine("\n[fetch BTC 4H — regime 판정]");
        var btc4h = await FetchKlines4hAsync("BTCUSDT", 1);
        Console.WriteLine($"BTCUSDT 4H ok ({btc4h.Count} bars)");
        var btcRegime = new bool[btc4h.Count];
        for (int i = 200; i < btc4h.Count; i++)
        {
            decimal e50 = Ema(btc4h, i, 50);
            decimal e200 = Ema(btc4h, i, 200);
            btcRegime[i] = e50 > e200;
        }
        bool BtcBullAt(DateTime t)
        {
            int lo = 0, hi = btc4h.Count - 1, r = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (btc4h[mid].OpenTime <= t) { r = mid; lo = mid + 1; } else hi = mid - 1; }
            return r >= 0 && btcRegime[r];
        }
        bool Btc24hUp(DateTime t)
        {
            int lo = 0, hi = btc4h.Count - 1, r = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (btc4h[mid].OpenTime <= t) { r = mid; lo = mid + 1; } else hi = mid - 1; }
            if (r < 6) return false;
            return btc4h[r].ClosePrice > btc4h[r - 6].ClosePrice;   // 24h = 6 4H봉
        }

        var fullData1h = new Dictionary<string, List<IBinanceKline>>();
        var fullData4h = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (1H + 4H)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl1h = await FetchKlines1hAsync(sym, fetchPages1h);
                var kl4h = await FetchKlines4hAsync(sym, 1);
                if (kl1h.Count < 100 || kl4h.Count < 50) { Console.WriteLine("skip"); continue; }
                fullData1h[sym] = kl1h;
                fullData4h[sym] = kl4h;
                Console.WriteLine($"ok (1H {kl1h.Count} / 4H {kl4h.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 종목 4H regime: e50 > e200
        var symRegime4h = new Dictionary<string, bool[]>();
        foreach (var kv in fullData4h)
        {
            var kl = kv.Value;
            var arr = new bool[kl.Count];
            for (int i = Math.Min(200, kl.Count - 1); i < kl.Count; i++)
            {
                decimal e50 = Ema(kl, i, 50);
                decimal e200 = Ema(kl, i, Math.Min(200, i));
                arr[i] = e50 > e200;
            }
            symRegime4h[kv.Key] = arr;
        }
        bool SymBull4hAt(string sym, DateTime t)
        {
            if (!fullData4h.TryGetValue(sym, out var kl)) return false;
            int lo = 0, hi = kl.Count - 1, r = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (kl[mid].OpenTime <= t) { r = mid; lo = mid + 1; } else hi = mid - 1; }
            return r >= 0 && symRegime4h[sym][r];
        }

        bool ShouldEnter(string sym, int i)
        {
            var kl = fullData1h[sym];
            if (i < 22) return false;
            DateTime t = kl[i].OpenTime;
            if (!BtcBullAt(t)) return false;
            if (!Btc24hUp(t)) return false;
            if (!SymBull4hAt(sym, t)) return false;
            // 1H: 종가 > EMA20 + RSI<70
            decimal ema20 = Ema(kl, i, 20);
            if (kl[i].ClosePrice <= ema20) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 70.0) return false;
            // 직전 봉에서 EMA20 돌파 직후만 (이미 한참 위로 간 거 차단)
            decimal ema20Prev = Ema(kl, i - 1, 20);
            if (kl[i - 1].ClosePrice > ema20Prev * 1.005m) return false;   // 0.5% 초과 위면 추격
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tp1Px = entry * (1 + tp1Pct / 100m);
            decimal tp2Px = entry * (1 + tp2Pct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal bePx = entry * (1 + bePadPct / 100m);
            bool tp1Hit = false;
            decimal halfPnl = 0m;
            int holdK = maxHoldBars;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                    if (b.HighPrice >= tp1Px) { tp1Hit = true; halfPnl = tp1Pct / 2m; }
                }
                else
                {
                    if (b.LowPrice <= bePx) { decimal p = halfPnl + bePadPct / 2m; return ("BE_LOCK", p, k); }
                    if (b.HighPrice >= tp2Px) { decimal p = halfPnl + tp2Pct / 2m; return ("TP2", p, k); }
                }
                holdK = k;
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            if (tp1Hit) { decimal p = halfPnl + pctTo / 2m; return (p > 0 ? "TIMEOUT_TP" : "TIMEOUT_SL", p, holdK); }
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, holdK);
        }

        (decimal pnl, int n, int tp2N, int beLockN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 6;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData1h)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 25; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(sym, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tp2N = 0, beLockN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData1h[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP2" || kind == "TIMEOUT_TP") tp2N++;
                else if (kind == "BE_LOCK") beLockN++;
                else if (kind == "SL" || kind == "TIMEOUT_SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData1h[c.sym].Count - 1);
                active.Add(fullData1h[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tp2N, beLockN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP1+{tp1Pct}% 50% / TP2+{tp2Pct}% 50% / 본전+{bePadPct}% / SL-{slPct}% / max {maxHoldBars}h");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP2",5} {"BELK",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tp2N,5} {r.beLockN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [전략 #6] SQZMOM + 분할 익절 + 본전 이동
    //   진입: SQZMOM 1H 동일 (squeeze release + momentum>0)
    //   TP1: +0.8% 도달 → 50% 청산 + SL 본전(+0.2% 수수료 흡수)으로 이동
    //   TP2: +3.0% (나머지 50%)
    //   SL:  -1.5% 또는 본전(TP1 후)
    //   max hold 24h
    private static async Task RunSplitTpSqzMomAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  전략 #6 — SQZMOM 1H + 분할 익절 + 본전 이동");
        Console.WriteLine("  TP1 +0.8% 50% 청산 + SL 본전 / TP2 +3.0% 나머지 / SL -1.5%");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tp1Pct = 0.8m;
        const decimal tp2Pct = 3.0m;
        const decimal slPct = 1.5m;
        const decimal bePadPct = 0.2m;        // 본전+수수료
        const decimal slippagePct = 0.05m;
        const int fetchPages = 3;
        const int maxHoldBars = 24;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (1H)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1hAsync(sym, fetchPages);
                if (kl.Count < 100) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 22) return false;
            var (sOnPrev, _, _) = SqzMomAt(kl, i - 1);
            var (_, sOffNow, momNow) = SqzMomAt(kl, i);
            return sOnPrev && sOffNow && momNow > 0;
        }

        // 분할 익절 시뮬: 1건 진입을 2개 청산으로 추적
        // 반환: pnlPct (가중평균), kind, holdBars
        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tp1Px = entry * (1 + tp1Pct / 100m);
            decimal tp2Px = entry * (1 + tp2Pct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal bePx = entry * (1 + bePadPct / 100m);
            bool tp1Hit = false;
            decimal halfPnl = 0m;
            int holdK = maxHoldBars;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (!tp1Hit)
                {
                    // SL 먼저
                    if (b.LowPrice <= slPx) { return ("SL", -slPct, k); }
                    if (b.HighPrice >= tp1Px)
                    {
                        tp1Hit = true;
                        halfPnl = tp1Pct / 2m;        // 50% × +0.8% = +0.4%
                    }
                }
                else
                {
                    // 본전 SL 먼저
                    if (b.LowPrice <= bePx)
                    {
                        decimal pnl = halfPnl + bePadPct / 2m;     // 나머지 50% × +0.2% = +0.1%
                        return ("BE_LOCK", pnl, k);
                    }
                    if (b.HighPrice >= tp2Px)
                    {
                        decimal pnl = halfPnl + tp2Pct / 2m;       // 나머지 50% × +3% = +1.5%
                        return ("TP2", pnl, k);
                    }
                }
                holdK = k;
            }
            // Time stop: 청산 시점 가격
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            if (tp1Hit)
            {
                decimal pnl = halfPnl + pctTo / 2m;
                return (pnl > 0 ? "TIMEOUT_TP" : "TIMEOUT_SL", pnl, holdK);
            }
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, holdK);
        }

        (decimal pnl, int n, int tp1ExitN, int tp2N, int slN, int beLockN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 4;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 25; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tp1N = 0, tp2N = 0, slN = 0, beLockN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                // 분할 익절 = 2회 청산이지만 fee×2로 모델링 (notional 절반씩 두 번 = 동일)
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP2" || kind == "TIMEOUT_TP") tp2N++;
                else if (kind == "BE_LOCK") beLockN++;
                else if (kind == "SL" || kind == "TIMEOUT_SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tp1N, tp2N, slN, beLockN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP1+{tp1Pct}% 50% / TP2+{tp2Pct}% 50% / 본전+{bePadPct}% / SL-{slPct}% / max {maxHoldBars}h");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP2",5} {"BELK",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tp2N,5} {r.beLockN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
        Console.WriteLine("[해석] TP2=분할익절+큰TP / BELK=TP1후 본전탈출(소폭익) / SL=손절 / BE=±0.3% / TO=Timeout");
    }

    // [트뷰 인기전략 #5] Regime Adaptive — BTC 4H EMA50/200 골든크로스 시에만 LONG 진입
    //   강세장(BTC 4H EMA50>EMA200)에서만 SQZMOM 1H 진입
    //   횡보/약세장에서는 진입 OFF
    private static async Task<List<IBinanceKline>?> FetchKlines4hPageAsync(string sym, long endMs, int limit)
    {
        for (int t = 1; t <= 4; t++)
        {
            try
            {
                await Task.Delay(800);
                var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={sym}&interval=4h&limit={limit}&endTime={endMs}";
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
            { await Task.Delay(t * 5000); }
            catch { return null; }
        }
        return null;
    }

    private static async Task<List<IBinanceKline>> FetchKlines4hAsync(string sym, int pages = 1)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < pages; p++)
        {
            var page = await FetchKlines4hPageAsync(sym, endMs, BARS_PER_REQ);
            if (page == null || page.Count == 0) break;
            all.Insert(0, page);
            endMs = ((DateTimeOffset)page[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            if (page.Count < BARS_PER_REQ) break;
        }
        return all.SelectMany(c => c).ToList();
    }

    private static async Task RunRegimeAdaptiveAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  트뷰 인기전략 #5 — Regime Adaptive (BTC 4H EMA50/200)");
        Console.WriteLine("  강세장(EMA50>EMA200) + SQZMOM 1H → LONG / 약세장에서는 진입 OFF");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 3;
        const int maxHoldBars = 24;

        // BTC 4H EMA50/200 — regime 판정
        Console.WriteLine("\n[fetch BTC 4H — regime 판정용]");
        var btc4h = await FetchKlines4hAsync("BTCUSDT", 1);
        Console.WriteLine($"BTCUSDT 4H ok ({btc4h.Count} bars)");
        // regime 시리즈: bull(true) / bear(false)
        var regimeAt = new bool[btc4h.Count];
        for (int i = 200; i < btc4h.Count; i++)
        {
            decimal e50 = Ema(btc4h, i, 50);
            decimal e200 = Ema(btc4h, i, 200);
            regimeAt[i] = e50 > e200;
        }
        bool BtcBullAt(DateTime t)
        {
            // 가장 가까운 직전 4H 봉 regime 사용
            int lo = 0, hi = btc4h.Count - 1, r = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (btc4h[mid].OpenTime <= t) { r = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return r >= 0 && regimeAt[r];
        }

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (1H)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1hAsync(sym, fetchPages);
                if (kl.Count < 100) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 22) return false;
            if (!BtcBullAt(kl[i].OpenTime)) return false;   // regime 가드
            var (sOnPrev, _, _) = SqzMomAt(kl, i - 1);
            var (_, sOffNow, momNow) = SqzMomAt(kl, i);
            if (!sOnPrev) return false;
            if (!sOffNow) return false;
            if (momNow <= 0) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal atr = AtrAt(kl, i, 14);
            if (atr <= 0) return ("BE", 0m, 0);
            decimal tpPx = entry + 2m * atr;
            decimal slPx = entry - 1m * atr;
            decimal tpPct = (tpPx - entry) / entry * 100m;
            decimal slPct = (entry - slPx) / entry * 100m;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                var (_, _, momK) = SqzMomAt(kl, i + k);
                if (momK < 0)
                {
                    decimal pct = (b.ClosePrice - entry) / entry * 100m;
                    return (pct > 0.5m ? "MOM_TP" : "MOM_EXIT", pct, k);
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 4;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 25; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP" || kind == "MOM_TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x | TP=2*ATR / SL=1*ATR / max {maxHoldBars}h");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [트뷰 인기전략 #4] Ichimoku Cloud Breakout on 1H
    //   가격이 구름(Span A/B) 위로 돌파 + Tenkan > Kijun + Chikou(=close 26봉전) > 26봉전 가격 → LONG
    //   청산: Tenkan < Kijun OR 가격이 구름 아래
    private static (decimal tenkan, decimal kijun, decimal spanA, decimal spanB, decimal chikou)
        IchimokuAt(List<IBinanceKline> kl, int i)
    {
        if (i < 52) return (0, 0, 0, 0, 0);
        decimal HighestN(int p, int idx) { decimal m = kl[idx - p + 1].HighPrice; for (int q = idx - p + 2; q <= idx; q++) if (kl[q].HighPrice > m) m = kl[q].HighPrice; return m; }
        decimal LowestN(int p, int idx) { decimal m = kl[idx - p + 1].LowPrice; for (int q = idx - p + 2; q <= idx; q++) if (kl[q].LowPrice < m) m = kl[q].LowPrice; return m; }
        decimal tenkan = (HighestN(9, i) + LowestN(9, i)) / 2m;
        decimal kijun = (HighestN(26, i) + LowestN(26, i)) / 2m;
        decimal spanA = (tenkan + kijun) / 2m;
        decimal spanB = (HighestN(52, i) + LowestN(52, i)) / 2m;
        decimal chikou = kl[i].ClosePrice;   // 26봉 후로 displaced 되지만 비교용
        return (tenkan, kijun, spanA, spanB, chikou);
    }

    private static async Task RunIchimoku1hAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  트뷰 인기전략 #4 — Ichimoku Cloud Breakout on 1H");
        Console.WriteLine("  가격>구름 + Tenkan>Kijun + Chikou>26봉전가격 → LONG");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal slPct = 2.0m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 3;
        const int maxHoldBars = 48;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (1H)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1hAsync(sym, fetchPages);
                if (kl.Count < 100) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 60) return false;
            var (tenkan, kijun, spanA, spanB, _) = IchimokuAt(kl, i);
            var (tenkanP, kijunP, _, _, _) = IchimokuAt(kl, i - 1);
            decimal cloudTop = Math.Max(spanA, spanB);
            decimal cloudBot = Math.Min(spanA, spanB);
            if (kl[i].ClosePrice <= cloudTop) return false;     // 구름 위
            if (tenkan <= kijun) return false;                  // Tenkan>Kijun
            // Tenkan/Kijun 골든크로스 직후 (직전 봉에서는 Tenkan<=Kijun 였거나 가격이 구름 안)
            if (tenkanP > kijunP && kl[i - 1].ClosePrice > cloudTop) return false;  // 이미 진입 시그널 지난 후
            // Chikou(=현재가) > 26봉 전 가격
            if (kl[i].ClosePrice <= kl[i - 26].HighPrice) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                var (tenkanK, kijunK, spanAK, spanBK, _) = IchimokuAt(kl, i + k);
                decimal cloudBot = Math.Min(spanAK, spanBK);
                if (tenkanK < kijunK)
                {
                    decimal pct = (b.ClosePrice - entry) / entry * 100m;
                    return (pct > 0 ? "TK_TP" : "TK_SL", pct, k);
                }
                if (b.ClosePrice < cloudBot)
                {
                    decimal pct = (b.ClosePrice - entry) / entry * 100m;
                    return ("CLOUD_EXIT", pct, k);
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 4;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 60; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TK_TP" || kind == "CLOUD_EXIT" && pctRaw > 0) tpN++;
                else if (kind == "SL" || kind == "TK_SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x | SL=-{slPct}% / max {maxHoldBars}봉(48h)");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [트뷰 인기전략 #3] VWAP Mean Reversion on 5m
    //   세션 VWAP 계산 (24h rolling)
    //   가격 < VWAP - 2σ (deep discount) + RSI<30 → LONG (반등 노림)
    //   청산: VWAP 터치 OR SL -1.5%
    private static decimal[] ComputeRollingVwap(List<IBinanceKline> kl, int win = 288)  // 24h on 5m
    {
        var vwap = new decimal[kl.Count];
        decimal sumPV = 0m, sumV = 0m;
        var queue = new Queue<(decimal pv, decimal v)>();
        for (int i = 0; i < kl.Count; i++)
        {
            decimal tp = (kl[i].HighPrice + kl[i].LowPrice + kl[i].ClosePrice) / 3m;
            decimal v = kl[i].Volume;
            decimal pv = tp * v;
            queue.Enqueue((pv, v));
            sumPV += pv; sumV += v;
            if (queue.Count > win)
            {
                var (oldPv, oldV) = queue.Dequeue();
                sumPV -= oldPv; sumV -= oldV;
            }
            vwap[i] = sumV > 0 ? sumPV / sumV : kl[i].ClosePrice;
        }
        return vwap;
    }

    private static async Task RunVwapMeanRevAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  트뷰 인기전략 #3 — VWAP Mean Reversion on 5m");
        Console.WriteLine("  가격 < VWAP - 2σ + RSI<30 → LONG (반등 노림)");
        Console.WriteLine("  청산: VWAP 터치 OR SL -1.5%");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal slPct = 1.5m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;
        const int maxHoldBars = 48;          // 4h
        const int vwapWin = 288;
        const int sigmaWin = 96;             // 8h std

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (5m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var vwapCache = new Dictionary<string, decimal[]>();
        foreach (var kv in fullData) vwapCache[kv.Key] = ComputeRollingVwap(kv.Value, vwapWin);

        bool ShouldEnter(string sym, int i)
        {
            var kl = fullData[sym];
            if (i < sigmaWin) return false;
            decimal vwap = vwapCache[sym][i];
            if (vwap <= 0) return false;
            // sigma = std of (close - vwap) over last sigmaWin bars
            decimal mean = 0m;
            for (int q = i - sigmaWin + 1; q <= i; q++) mean += kl[q].ClosePrice - vwapCache[sym][q];
            mean /= sigmaWin;
            decimal sumSq = 0m;
            for (int q = i - sigmaWin + 1; q <= i; q++)
            {
                decimal d = (kl[q].ClosePrice - vwapCache[sym][q]) - mean;
                sumSq += d * d;
            }
            decimal sigma = (decimal)Math.Sqrt((double)(sumSq / sigmaWin));
            decimal lowerBand = vwap + mean - 2m * sigma;
            if (kl[i].ClosePrice >= lowerBand) return false;
            // RSI<30
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 30.0) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, string sym, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                decimal vwap = vwapCache[sym][i + k];
                if (b.HighPrice >= vwap)
                {
                    decimal pct = (vwap - entry) / entry * 100m;
                    return ("VWAP_TP", pct, k);
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 12;   // 1h cooldown

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = sigmaWin + 5; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(sym, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.sym, c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "VWAP_TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x | TP=VWAP터치 / SL=-{slPct}% / max {maxHoldBars}봉(4h)");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [트뷰 인기전략 #2] Triple SuperTrend on 15m
    //   3개 SuperTrend (ATR mult 1, 2, 3) 모두 LONG 시그널 → 진입
    //   2개 이상 SHORT 전환 → 청산
    //   TP 없음 (트레일링 SL = SuperTrend1 추종), Max hold 24봉 (6h)
    private static (decimal upper, decimal lower, int dir) SuperTrendAt(List<IBinanceKline> kl, int i, int atrPeriod, decimal mult, ref decimal prevUpper, ref decimal prevLower, ref int prevDir)
    {
        decimal hl2 = (kl[i].HighPrice + kl[i].LowPrice) / 2m;
        decimal atr = AtrAt(kl, i, atrPeriod);
        decimal up = hl2 + mult * atr;
        decimal dn = hl2 - mult * atr;
        // SuperTrend logic
        decimal upper = (up < prevUpper || kl[i - 1].ClosePrice > prevUpper) ? up : prevUpper;
        decimal lower = (dn > prevLower || kl[i - 1].ClosePrice < prevLower) ? dn : prevLower;
        int dir = prevDir;
        if (prevDir == 1 && kl[i].ClosePrice < lower) dir = -1;
        else if (prevDir == -1 && kl[i].ClosePrice > upper) dir = 1;
        prevUpper = upper; prevLower = lower; prevDir = dir;
        return (upper, lower, dir);
    }

    // 미리 SuperTrend 시리즈 계산 (3개)
    private static List<int[]> ComputeTripleSuperTrendDirs(List<IBinanceKline> kl, int atrPeriod = 10)
    {
        decimal[] mults = { 1.0m, 2.0m, 3.0m };
        var dirsAll = new List<int[]>();
        foreach (var m in mults)
        {
            var dirs = new int[kl.Count];
            decimal pUp = 0, pLow = 0;
            int pDir = 1;
            for (int i = atrPeriod; i < kl.Count; i++)
            {
                if (i == atrPeriod) { pUp = kl[i].HighPrice; pLow = kl[i].LowPrice; pDir = 1; }
                var (_, _, d) = SuperTrendAt(kl, i, atrPeriod, m, ref pUp, ref pLow, ref pDir);
                dirs[i] = d;
            }
            dirsAll.Add(dirs);
        }
        return dirsAll;
    }

    private static async Task RunTripleSuperTrendAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  트뷰 인기전략 #2 — Triple SuperTrend (ATR mult 1/2/3) on 15m");
        Console.WriteLine("  3개 모두 LONG → 진입 / 2개 이상 SHORT → 청산");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 12;
        const int maxHoldBars = 24;          // 6h max

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (15m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, fetchPages);
                if (kl.Count < 200) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 사전 계산
        var stCache = new Dictionary<string, List<int[]>>();
        foreach (var kv in fullData) stCache[kv.Key] = ComputeTripleSuperTrendDirs(kv.Value);

        bool ShouldEnter(string sym, int i)
        {
            var dirs = stCache[sym];
            if (i < 12) return false;
            // 직전 봉에서는 모두 LONG 아니었음 (진입 순간 신호)
            int allLongPrev = (dirs[0][i - 1] == 1 && dirs[1][i - 1] == 1 && dirs[2][i - 1] == 1) ? 1 : 0;
            int allLongNow = (dirs[0][i] == 1 && dirs[1][i] == 1 && dirs[2][i] == 1) ? 1 : 0;
            return allLongNow == 1 && allLongPrev == 0;   // 막 모두 LONG 전환
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, string sym, int i)
        {
            var dirs = stCache[sym];
            decimal entry = kl[i].ClosePrice;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                // 청산: 2개 이상 SHORT
                int shortCnt = (dirs[0][i + k] == -1 ? 1 : 0) + (dirs[1][i + k] == -1 ? 1 : 0) + (dirs[2][i + k] == -1 ? 1 : 0);
                if (shortCnt >= 2)
                {
                    decimal pct = (kl[i + k].ClosePrice - entry) / entry * 100m;
                    return (pct > 0 ? "ST_TP" : "ST_SL", pct, k);
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 8;   // 2h cooldown

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 25; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(sym, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.sym, c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "ST_TP") tpN++;
                else if (kind == "ST_SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x | max {maxHoldBars}봉(6h)");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [트뷰 인기전략 #1] Squeeze Momentum (LazyBear) on 1H
    //   BB(20,2) inside KC(20,1.5) = 스퀴즈 / 밖 = 해제
    //   해제 + momentum > 0 → LONG
    //   청산: momentum < 0 OR ATR 기반 TP/SL
    private static async Task<List<IBinanceKline>?> FetchKlines1hPageAsync(string sym, long endMs, int limit)
    {
        for (int t = 1; t <= 4; t++)
        {
            try
            {
                await Task.Delay(800);
                var url = $"https://fapi.binance.com/fapi/v1/klines?symbol={sym}&interval=1h&limit={limit}&endTime={endMs}";
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
            { await Task.Delay(t * 5000); }
            catch { return null; }
        }
        return null;
    }
    private static async Task<List<IBinanceKline>> FetchKlines1hAsync(string sym, int pages = 3)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < pages; p++)
        {
            var page = await FetchKlines1hPageAsync(sym, endMs, BARS_PER_REQ);
            if (page == null || page.Count == 0) break;
            all.Insert(0, page);
            endMs = ((DateTimeOffset)page[0].OpenTime).ToUnixTimeMilliseconds() - 1;
            if (page.Count < BARS_PER_REQ) break;
        }
        return all.SelectMany(c => c).ToList();
    }

    // ATR(14)
    private static decimal AtrAt(List<IBinanceKline> kl, int i, int p = 14)
    {
        if (i < p) return 0m;
        decimal sum = 0m;
        for (int q = i - p + 1; q <= i; q++)
        {
            decimal h = kl[q].HighPrice, l = kl[q].LowPrice, pc = kl[q - 1].ClosePrice;
            decimal tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            sum += tr;
        }
        return sum / p;
    }

    // SQZMOM components
    private static (bool sqzOn, bool sqzOff, decimal momentum) SqzMomAt(List<IBinanceKline> kl, int i, int len = 20, decimal bbMult = 2.0m, decimal kcMult = 1.5m)
    {
        if (i < len) return (false, false, 0m);
        // BB
        decimal sumC = 0m;
        for (int q = i - len + 1; q <= i; q++) sumC += kl[q].ClosePrice;
        decimal basis = sumC / len;
        decimal sumSq = 0m;
        for (int q = i - len + 1; q <= i; q++) { decimal d = kl[q].ClosePrice - basis; sumSq += d * d; }
        decimal stdev = (decimal)Math.Sqrt((double)(sumSq / len));
        decimal upperBB = basis + bbMult * stdev;
        decimal lowerBB = basis - bbMult * stdev;
        // KC (use TR avg as range)
        decimal sumTr = 0m;
        for (int q = i - len + 1; q <= i; q++)
        {
            if (q == 0) { sumTr += kl[q].HighPrice - kl[q].LowPrice; continue; }
            decimal h = kl[q].HighPrice, l = kl[q].LowPrice, pc = kl[q - 1].ClosePrice;
            decimal tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            sumTr += tr;
        }
        decimal rangeMa = sumTr / len;
        decimal upperKC = basis + rangeMa * kcMult;
        decimal lowerKC = basis - rangeMa * kcMult;
        bool sqzOn = (lowerBB > lowerKC) && (upperBB < upperKC);
        bool sqzOff = (lowerBB < lowerKC) && (upperBB > upperKC);
        // Momentum (linreg of close - midpoint over len, simplified to value diff)
        // LazyBear uses linreg(source - avg(avg(highest_high, lowest_low), sma_close), length, 0)
        decimal hh = kl[i - len + 1].HighPrice, ll = kl[i - len + 1].LowPrice;
        for (int q = i - len + 2; q <= i; q++) { if (kl[q].HighPrice > hh) hh = kl[q].HighPrice; if (kl[q].LowPrice < ll) ll = kl[q].LowPrice; }
        decimal mid = ((hh + ll) / 2m + basis) / 2m;
        // linreg simplified: slope of (close - mid) over len
        // y = close[q] - mid, x = q index 0..len-1
        decimal sumX = 0m, sumY = 0m, sumXY = 0m, sumXX = 0m;
        for (int q = 0; q < len; q++)
        {
            decimal x = q;
            decimal y = kl[i - len + 1 + q].ClosePrice - mid;
            sumX += x; sumY += y; sumXY += x * y; sumXX += x * x;
        }
        decimal n = len;
        decimal denom = n * sumXX - sumX * sumX;
        decimal slope = denom != 0 ? (n * sumXY - sumX * sumY) / denom : 0m;
        decimal intercept = (sumY - slope * sumX) / n;
        decimal val = intercept + slope * (len - 1);  // value at last bar
        return (sqzOn, sqzOff, val);
    }

    private static async Task RunSqzMom1hAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  트뷰 인기전략 #1 — Squeeze Momentum (LazyBear) on 1H");
        Console.WriteLine("  BB(20,2) inside KC(20,1.5) → 해제 + momentum>0 → LONG");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 3;            // 180일 1H = 4320봉 = 3페이지
        const int maxHoldBars = 24;          // 1H × 24 = 24h max

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (1H)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1hAsync(sym, fetchPages);
                if (kl.Count < 100) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 진입: 직전 봉 sqzOn=true, 현재 봉 sqzOff=true (squeeze release 순간), momentum > 0
        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 22) return false;
            var (sOnPrev, _, _) = SqzMomAt(kl, i - 1);
            var (_, sOffNow, momNow) = SqzMomAt(kl, i);
            if (!sOnPrev) return false;          // 직전 봉 squeeze 중
            if (!sOffNow) return false;          // 현재 봉 squeeze 해제
            if (momNow <= 0) return false;       // 양수 모멘텀
            return true;
        }

        // 청산: ATR 기반 TP=2*ATR, SL=1*ATR, 모멘텀 음수 전환 시 청산, max hold
        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal atr = AtrAt(kl, i, 14);
            if (atr <= 0) return ("BE", 0m, 0);
            decimal tpPx = entry + 2m * atr;
            decimal slPx = entry - 1m * atr;
            decimal tpPct = (tpPx - entry) / entry * 100m;
            decimal slPct = (entry - slPx) / entry * 100m;

            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                // 모멘텀 음수 전환 시 청산 (signal exit)
                var (_, _, momK) = SqzMomAt(kl, i + k);
                if (momK < 0)
                {
                    decimal pct = (b.ClosePrice - entry) / entry * 100m;
                    return (pct > 0.5m ? "MOM_TP" : "MOM_EXIT", pct, k);
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int momTpN, int momExN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 4;   // 4시간 cooldown

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 25; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, momTpN = 0, momExN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "MOM_TP") momTpN++;
                else if (kind == "MOM_EXIT") momExN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, momTpN, momExN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x | TP=2*ATR / SL=1*ATR / max {maxHoldBars}h");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"MTP",5} {"MEX",5} {"BE",5} {"TO",5} {"보유h",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 110));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.momTpN,5} {r.momExN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 110));
        Console.WriteLine("[해석] TP=ATR익절 / SL=ATR손절 / MTP=모멘텀음수전환 익절(>0.5%) / MEX=모멘텀청산(이외) / BE=±0.3% / TO=Timeout");
    }

    // [v5.22.73] Swing 4H/1H 빈도 향상 — 사용자 "월 진입 많아야"
    //   동일 로직 (close>SMA20 + SMA20>SMA50 + RSI50~70 + vol×1.5 + 양봉) 을 4H/1H 봉에 적용
    //   결과 비교: 1D vs 4H vs 1H 빈도 + 수익성
    private static async Task RunSwingMultiTfAsync(string label, int barMinutes, int fetchPages, decimal tpPct, decimal slPct, int maxHoldBars)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine($"  Swing {label} 빈도/수익 검증 (lev 20x, TP+{tpPct}% SL-{slPct}%, max {maxHoldBars}봉)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;
        const int maxSlots = 2;
        const decimal slippagePct = 0.05m;
        const decimal swingLeverage = 20m;
        const decimal trailingTriggerRoe = 3m;
        const decimal trailingMinRetrace = 5m;
        const decimal trailingRatio = 0.33m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch {label} — {symbols.Length}개 심볼]");
        int idx = 0;
        Func<string, int, Task<List<IBinanceKline>>> fetcher = barMinutes switch
        {
            240 => (s, p) => FetchKlines4hAsync(s, p),
            60 => (s, p) => FetchKlines1hAsync(s, p),
            1440 => (s, p) => FetchKlines1dAsync(s, p),
            _ => (s, p) => FetchKlines15mAsync(s, p)
        };
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await fetcher(sym, fetchPages);
                if (kl.Count < 60) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 51) return false;
            decimal sma20 = 0m; for (int q = i - 19; q <= i; q++) sma20 += kl[q].ClosePrice; sma20 /= 20m;
            decimal sma50 = 0m; for (int q = i - 49; q <= i; q++) sma50 += kl[q].ClosePrice; sma50 /= 50m;
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 70.0) return false;
            decimal volAvg = 0m; for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume; volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal highestRoe = 0m;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                decimal highPriceRoe = (b.HighPrice - entry) / entry * 100m * swingLeverage;
                if (highPriceRoe > highestRoe) highestRoe = highPriceRoe;
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (highestRoe >= trailingTriggerRoe)
                {
                    decimal closeRoe = (b.ClosePrice - entry) / entry * 100m * swingLeverage;
                    decimal limit = Math.Max(trailingMinRetrace, highestRoe * trailingRatio);
                    if (highestRoe - closeRoe >= limit)
                        return ("TRAIL", (b.ClosePrice - entry) / entry * 100m, k);
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 1m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        DateTime since = DateTime.UtcNow.AddYears(-3);
        var candidates = new List<(DateTime time, string sym, int barIdx)>();
        foreach (var kv in fullData)
        {
            var kl = kv.Value; var sym = kv.Key;
            for (int i = 51; i < kl.Count - maxHoldBars; i++)
            {
                if (kl[i].OpenTime < since) continue;
                if (!ShouldEnter(kl, i)) continue;
                candidates.Add((kl[i].OpenTime, sym, i));
            }
        }
        candidates.Sort((a, b) => a.time.CompareTo(b.time));

        var active = new List<DateTime>();
        var monthly = new SortedDictionary<string, (int n, int tp, int sl, int trail, decimal pnl)>();
        decimal totalPnl = 0m;
        int totalN = 0, totalTp = 0, totalSl = 0, totalTrail = 0;
        foreach (var c in candidates)
        {
            active.RemoveAll(t => t <= c.time);
            if (active.Count >= maxSlots) continue;
            decimal notional = margin * swingLeverage;
            var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
            decimal pctNet = pctRaw - (decimal)(FEE_RATE * 2m * 100m) - (slippagePct * 2m);
            decimal pnlUsd = notional * pctNet / 100m;
            totalPnl += pnlUsd;
            totalN++;
            if (kind == "TP") totalTp++;
            else if (kind == "SL") totalSl++;
            else if (kind == "TRAIL") totalTrail++;
            string monthKey = c.time.ToString("yyyy-MM");
            if (!monthly.ContainsKey(monthKey)) monthly[monthKey] = (0, 0, 0, 0, 0m);
            var m = monthly[monthKey];
            m.n++;
            if (kind == "TP") m.tp++;
            else if (kind == "SL") m.sl++;
            else if (kind == "TRAIL") m.trail++;
            m.pnl += pnlUsd;
            monthly[monthKey] = m;
            int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
            active.Add(fullData[c.sym][endBar].OpenTime);
        }

        Console.WriteLine();
        Console.WriteLine($"==== {label} 월별 통계 ====");
        Console.WriteLine($"{"월",-9} {"진입",4} {"TP",3} {"SL",3} {"TRAIL",6} {"PnL($)",10} {"ROI%",8}");
        Console.WriteLine(new string('-', 60));
        foreach (var kv in monthly)
        {
            decimal monthRoi = kv.Value.pnl / seed * 100m;
            Console.WriteLine($"{kv.Key,-9} {kv.Value.n,4} {kv.Value.tp,3} {kv.Value.sl,3} {kv.Value.trail,6} {kv.Value.pnl,9:F2} {monthRoi,7:F1}");
        }
        Console.WriteLine(new string('-', 60));
        decimal totalRoi = totalPnl / seed * 100m;
        Console.WriteLine($"{"합계",-9} {totalN,4} {totalTp,3} {totalSl,3} {totalTrail,6} {totalPnl,9:F2} {totalRoi,7:F1}");
        var profitMonths = monthly.Values.Count(m => m.pnl > 0);
        Console.WriteLine($"수익월 {profitMonths}/{monthly.Count} | 월평균 진입 {totalN / Math.Max(1, monthly.Count)}건");
    }

    // [v5.23.4] 3y KNN sweep — DOGE 검증 + meme alts + 두 라벨 모드 동시 비교
    //   심볼: BTC (control), DOGE (이전 best), 1000PEPE, 1000SHIB, 1000BONK, WIF
    //   라벨 모드:
    //     A) TP-first: TP 가 SL 보다 먼저 도달 → 1 (현실적 거래 라벨)
    //     B) max-hit:  close[+5..+10] 중 한 번이라도 entry 위 → 1 (사용자 제안)
    //   TP/SL combos: 사용자 새 영역 (작은 TP + 넓은 SL) + 이전 best (3:1)
    //   Output: 라벨 모드별 별도 표 + 흑자 셀 ✅ 표시
    private static async Task RunKnnSweep3yAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  3y KNN Sweep — 6 symbol × 9 TP/SL × 2 WR × 2 라벨 모드");
        Console.WriteLine("  분류기: Simple Lorentzian KNN (5-feat, K=10) — 사용자 reference");
        Console.WriteLine("  데이터: 15m × 70 page (~3년)");
        Console.WriteLine("================================================================");

        var syms = new[] {
            "BTCUSDT",
            "DOGEUSDT",
            "1000PEPEUSDT",
            "1000SHIBUSDT",
            "1000BONKUSDT",
            "WIFUSDT"
        };
        var combos = new (decimal tp, decimal sl, string label)[]
        {
            // 사용자 새 제안 영역 (작은 TP + 넓은 SL, max-hit 신호 활용)
            (0.5m, 2.0m, "1:4 작TP넓SL"),
            (0.5m, 3.0m, "1:6 작TP초넓"),
            (1.0m, 2.0m, "1:2 넓SL"),
            (1.0m, 3.0m, "1:3 초넓SL"),
            (0.7m, 2.0m, "1:3"),
            // 기존 best 영역 (3:1 R:R, TP-first 우세)
            (1.0m, 0.5m, "2:1"),
            (1.2m, 0.6m, "2:1 (이전)"),
            (1.5m, 0.5m, "3:1 ⭐DOGE"),
            (2.0m, 0.5m, "4:1"),
        };
        var wrThresholds = new[] { 0.70, 0.85 };
        const int K = 10;
        const int maxHoldBars = 10;
        const double feeSlipPct = 0.18;

        foreach (var sym in syms)
        {
            Console.Write($"\n[{sym}] fetch ");
            var kl = await FetchKlines15mAsync(sym, 70);
            if (kl.Count < 1000) { Console.WriteLine($"skip ({kl.Count})"); continue; }
            int yearsApprox = kl.Count * 15 / 60 / 24 / 365;
            Console.Write($"ok ({kl.Count}봉 ~{yearsApprox}y) | features... ");

            var feats = new double[kl.Count][];
            for (int j = 60; j < kl.Count; j++)
            {
                int wStart = Math.Max(0, j - 499);
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = KnnFeatures5.Extract(win);
            }
            Console.Write("ok | KNN cache... ");

            // KNN top-K 인덱스 cache
            var knnIdx = new int[kl.Count][];
            for (int j = 71; j < kl.Count - maxHoldBars; j++)
            {
                if (feats[j] == null) continue;
                int trainEnd = j - 11;
                if (trainEnd < 60 + K) continue;
                var dists = new (double dist, int idx)[trainEnd - 60 + 1];
                int cnt = 0;
                for (int i = 60; i <= trainEnd; i++)
                {
                    if (feats[i] == null) continue;
                    double d = LorentzianDistanceLocal(feats[j], feats[i]);
                    dists[cnt++] = (d, i);
                }
                Array.Sort(dists, 0, cnt, Comparer<(double, int)>.Create((a, b) => a.Item1.CompareTo(b.Item1)));
                knnIdx[j] = new int[K];
                for (int x = 0; x < K && x < cnt; x++) knnIdx[j][x] = dists[x].idx;
            }
            Console.WriteLine("ok");

            // max-hit 라벨 (TP/SL 무관, 한 번만 계산)
            var labels_max = new int[kl.Count];
            for (int j = 0; j < kl.Count - maxHoldBars; j++)
            {
                decimal entry = kl[j].ClosePrice;
                decimal maxC = entry;
                for (int k = 5; k <= maxHoldBars; k++)
                    if (kl[j + k].ClosePrice > maxC) maxC = kl[j + k].ClosePrice;
                labels_max[j] = maxC > entry ? 1 : -1;
            }

            // 두 라벨 모드 출력
            foreach (var labelMode in new[] { "TP-first", "max-hit" })
            {
                Console.WriteLine();
                Console.WriteLine($"  ★ Label = {labelMode} — KNN 학습/예측 신호 기준");
                Console.WriteLine($"    PnL outcome 은 항상 TP-first (실거래 시뮬레이션)");
                Console.WriteLine($"    {"TP",4} {"SL",4} {"R:R",-13} {"WRthr",6} {"신호",6} {"적중률",8} {"BE WR",7} {"Net%/trade",12} {"3y%",10}");
                Console.WriteLine("    " + new string('-', 90));

                foreach (var (tp, sl, comboLabel) in combos)
                {
                    // TP-first 라벨 + outcome (항상 PnL 기준)
                    var labels_tpsl = new int[kl.Count];
                    for (int j = 0; j < kl.Count - maxHoldBars; j++)
                    {
                        decimal entry = kl[j].ClosePrice;
                        decimal tpPx = entry * (1 + tp / 100m);
                        decimal slPx = entry * (1 - sl / 100m);
                        int outcome = -1;
                        for (int k = 1; k <= maxHoldBars; k++)
                        {
                            var b = kl[j + k];
                            bool tpHit = b.HighPrice >= tpPx;
                            bool slHit = b.LowPrice <= slPx;
                            if (tpHit && slHit) { outcome = -1; break; }
                            if (tpHit) { outcome = 1; break; }
                            if (slHit) { outcome = -1; break; }
                        }
                        labels_tpsl[j] = outcome;
                    }

                    int[] training_labels = labelMode == "TP-first" ? labels_tpsl : labels_max;
                    int[] outcome_labels  = labels_tpsl;

                    decimal beWR = sl / (tp + sl);

                    foreach (var wrThr in wrThresholds)
                    {
                        int sig = 0, hit = 0;
                        for (int j = 71; j < kl.Count - maxHoldBars; j++)
                        {
                            if (knnIdx[j] == null) continue;
                            int wins = 0;
                            foreach (int idx in knnIdx[j]) if (training_labels[idx] == 1) wins++;
                            double wr = (double)wins / K;
                            if (wr >= wrThr)
                            {
                                sig++;
                                if (outcome_labels[j] == 1) hit++;
                            }
                        }
                        double actualWR = sig > 0 ? (double)hit / sig : 0;
                        double netPct = actualWR * (double)tp - (1 - actualWR) * (double)sl - feeSlipPct;
                        // 3년 sample: 그대로 합계 (sig × netPct = 누적%)
                        double total3y = sig * netPct;
                        string flag = netPct > 0 ? "✅" : "  ";
                        Console.WriteLine($"    {tp,4:F1} {sl,4:F1} {comboLabel,-13} {wrThr * 100,5:F0}% {sig,6} {actualWR * 100,7:F2}% {beWR * 100,6:F1}% {netPct,+11:F4}% {total3y,+9:F1}% {flag}");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("✅ = 흑자 (Net%/trade > 0)");
        Console.WriteLine("3y%: 3년 누적 PnL% (signals × Net%/trade) — 한 번에 1슬롯 가정");
    }

    // [v5.23.3] KNN sweep — 4 symbol × 8 TP/SL × 2 WR threshold (사용자 셋다 진행)
    //   목적: 어느 (symbol, TP/SL, WR) 조합이 실거래 흑자 가능한지 측정
    //   - 분류기: Simple Lorentzian KNN (5-feat, K=10) — 사용자 reference 코드
    //   - KNN top-K 인덱스 cache (label-independent) → 8 combo eval 빠름
    //   - 출력: 신호수, 실측승률, BE WR, Net%/trade, 연간 예상 PnL
    private static async Task RunKnnSweepAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  KNN Sweep: 4 symbol × 8 TP/SL × 2 WR threshold");
        Console.WriteLine("  분류기: Simple Lorentzian KNN (5-feat: RSI, MFI, ADX, CCI, Mom, K=10)");
        Console.WriteLine("================================================================");

        var syms = new[] { "BTCUSDT", "XRPUSDT", "DOGEUSDT", "PEPEUSDT" };
        var combos = new (decimal tp, decimal sl, string label)[]
        {
            (0.3m, 0.6m, "1:2 작은TP"),
            (0.5m, 1.0m, "1:2"),
            (0.5m, 1.5m, "1:3 매우 작은TP"),
            (1.0m, 1.0m, "1:1"),
            (1.0m, 0.5m, "2:1"),
            (1.2m, 0.6m, "2:1 (현재)"),
            (1.5m, 0.5m, "3:1"),
            (2.0m, 1.0m, "2:1 wider"),
        };
        var wrThresholds = new[] { 0.70, 0.85 };
        const int K = 10;
        const int maxHoldBars = 10;
        const double feeSlipPct = 0.18;   // 0.08% fee × 2 + 0.05% slip × 2 = ~0.26% (보수적 0.18%)

        foreach (var sym in syms)
        {
            Console.Write($"\n[{sym}] fetch ");
            var kl = await FetchKlines15mAsync(sym, 12);
            if (kl.Count < 1000) { Console.WriteLine($"skip ({kl.Count})"); continue; }
            Console.Write($"ok ({kl.Count}) | features... ");

            var feats = new double[kl.Count][];
            for (int j = 60; j < kl.Count; j++)
            {
                int wStart = Math.Max(0, j - 499);
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = KnnFeatures5.Extract(win);
            }
            Console.Write("ok | KNN cache... ");

            // KNN top-K 인덱스 cache (label-independent, 한 번만 계산)
            var knnIdx = new int[kl.Count][];
            for (int j = 71; j < kl.Count - maxHoldBars; j++)
            {
                if (feats[j] == null) continue;
                int trainEnd = j - 11;
                if (trainEnd < 60 + K) continue;
                var dists = new (double dist, int idx)[trainEnd - 60 + 1];
                int cnt = 0;
                for (int i = 60; i <= trainEnd; i++)
                {
                    if (feats[i] == null) continue;
                    double d = LorentzianDistanceLocal(feats[j], feats[i]);
                    dists[cnt++] = (d, i);
                }
                Array.Sort(dists, 0, cnt, Comparer<(double, int)>.Create((a, b) => a.Item1.CompareTo(b.Item1)));
                knnIdx[j] = new int[K];
                for (int x = 0; x < K && x < cnt; x++) knnIdx[j][x] = dists[x].idx;
            }
            Console.WriteLine("ok");

            Console.WriteLine();
            Console.WriteLine($"  {"TP",4} {"SL",4} {"R:R",-12} {"WRthr",6} {"신호",6} {"적중률",8} {"BE WR",7} {"Net%/trade",12} {"연간%",10}");
            Console.WriteLine("  " + new string('-', 90));

            foreach (var (tp, sl, label) in combos)
            {
                // Compute labels for this combo
                var labels = new int[kl.Count];
                for (int j = 60; j < kl.Count - maxHoldBars; j++)
                {
                    decimal entry = kl[j].ClosePrice;
                    decimal tpPx = entry * (1 + tp / 100m);
                    decimal slPx = entry * (1 - sl / 100m);
                    int outcome = -1;
                    for (int k = 1; k <= maxHoldBars; k++)
                    {
                        var b = kl[j + k];
                        bool tpHit = b.HighPrice >= tpPx;
                        bool slHit = b.LowPrice <= slPx;
                        if (tpHit && slHit) { outcome = -1; break; }
                        if (tpHit) { outcome = 1; break; }
                        if (slHit) { outcome = -1; break; }
                    }
                    labels[j] = outcome;
                }

                decimal beWR = sl / (tp + sl);

                foreach (var wrThr in wrThresholds)
                {
                    int sig = 0, hit = 0;
                    for (int j = 71; j < kl.Count - maxHoldBars; j++)
                    {
                        if (knnIdx[j] == null) continue;
                        int wins = 0;
                        foreach (int idx in knnIdx[j]) if (labels[idx] == 1) wins++;
                        double wr = (double)wins / K;
                        if (wr >= wrThr)
                        {
                            sig++;
                            if (labels[j] == 1) hit++;
                        }
                    }
                    double actualWR = sig > 0 ? (double)hit / sig : 0;
                    double netPct = actualWR * (double)tp - (1 - actualWR) * (double)sl - feeSlipPct;
                    double annualPnl = sig * (365.0 / 180.0) * netPct;   // 6mo → 1y 환산
                    string flag = netPct > 0 ? "✅" : "  ";
                    Console.WriteLine($"  {tp,4:F1} {sl,4:F1} {label,-12} {wrThr * 100,5:F0}% {sig,6} {actualWR * 100,7:F2}% {beWR * 100,6:F1}% {netPct,+11:F4}% {annualPnl,+9:F1}% {flag}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("✅ = 흑자 (Net%/trade > 0)");
        Console.WriteLine("BE WR (Break-even Win Rate) = SL / (TP + SL) — fee 미포함");
        Console.WriteLine("Net%/trade = WR × TP − (1−WR) × SL − 0.18% (fee+slip)");
        Console.WriteLine("연간%: 6개월 sample × 365/180 환산 (월 진입수 일정 가정)");
    }

    private static double LorentzianDistanceLocal(double[] a, double[] b)
    {
        double sum = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++) sum += Math.Log(1.0 + Math.Abs(a[i] - b[i]));
        return sum;
    }

    // [v5.23.2] KNN 변형 비교 — 사용자 의심 검증
    //   3가지 알고리즘 동일 데이터 / 동일 5-feature / 동일 라벨 (5~10봉 내 수익 도달)
    //   → 어떤 알고리즘이 67.7% 에 가장 가까운지 직접 측정
    //   변형:
    //     a) Pine ANN Lorentzian (LorentzianAnnEngine — 7-feature, 4봉 subsampling)
    //     b) Simple Euclidean KNN (사용자 reference 코드 그대로 5-feature K=10)
    //     c) Simple Lorentzian KNN (b 와 같은 구조, 거리만 Lorentzian)
    //     d) c + BTC 듀얼 가드 (BTC 1H EMA20↑ + BTC 24H 변화 > 5%)
    private static async Task RunKnnVariantsCompareAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  KNN 변형 4종 비교 검증 (사용자 정확한 TP/SL 라벨링)");
        Console.WriteLine("  데이터: BTCUSDT 15m × 12 page (~6 month)");
        Console.WriteLine("  라벨: 진입 후 10봉 내 TP +1.2% 도달 → 1, SL -0.6% 도달 또는 timeout → -1");
        Console.WriteLine("================================================================");

        Console.Write("\n[fetch BTC 15m] ");
        var kl = await FetchKlines15mAsync("BTCUSDT", 12);
        Console.WriteLine($"ok ({kl.Count})");
        if (kl.Count < 500) { Console.WriteLine("데이터 부족"); return; }

        // 사전 계산: 5-feature + 7-feature + 라벨 (5~10봉 내 수익 도달)
        Console.WriteLine("[pre-compute 5/7 feature + labels]");
        var feats5 = new double[kl.Count][];
        var feats7 = new float[kl.Count][];
        var labels = new int[kl.Count];
        for (int j = 60; j < kl.Count; j++)
        {
            int wStart = Math.Max(0, j - 499);
            var win = kl.GetRange(wStart, j - wStart + 1);
            feats5[j] = KnnFeatures5.Extract(win);
            feats7[j] = TradingBot.Services.LorentzianV2.LorentzianFeatures.Extract(win);
        }
        // 사용자 스펙: 10봉 내 TP +1.2% 도달 → 1, SL -0.6% 도달 또는 timeout → -1
        const decimal labelTpPct = 1.2m;
        const decimal labelSlPct = 0.6m;
        const int labelMaxBars = 10;
        for (int j = 0; j < kl.Count - labelMaxBars; j++)
        {
            decimal entry = kl[j].ClosePrice;
            decimal tpPx = entry * (1 + labelTpPct / 100m);
            decimal slPx = entry * (1 - labelSlPct / 100m);
            int outcome = -1; // default = timeout
            for (int k = 1; k <= labelMaxBars; k++)
            {
                var b = kl[j + k];
                bool tpHit = b.HighPrice >= tpPx;
                bool slHit = b.LowPrice <= slPx;
                if (tpHit && slHit) { outcome = -1; break; } // 둘 다 = 보수적 SL
                if (tpHit) { outcome = 1; break; }
                if (slHit) { outcome = -1; break; }
            }
            labels[j] = outcome;
        }

        // 4가지 분류기
        var euclid = new SimpleEuclideanKnn(10);
        var lorenz = new SimpleLorentzianKnn(10);
        var pine = new TradingBot.Services.LorentzianV2.LorentzianAnnEngine("BTCUSDT", neighborsCount: 8, maxBarsBack: 2000, featureCount: 7);

        int sigEuclid = 0, hitEuclid = 0;
        int sigLorenz = 0, hitLorenz = 0;
        int sigPine = 0, hitPine = 0;
        int sigBtcGuard = 0, hitBtcGuard = 0;

        // BTC 듀얼 가드 사전계산
        var btcEma1h = new double[kl.Count];
        var btc24hChg = new double[kl.Count];
        for (int j = 100; j < kl.Count; j++)
        {
            // 1H EMA20 = aggregate 4 × 15m → SMA proxy on 80 15m bars
            double sum = 0;
            for (int q = j - 79; q <= j; q++) sum += (double)kl[q].ClosePrice;
            btcEma1h[j] = sum / 80.0;
            // 24H change: now vs 96 bars ago
            int ref24 = Math.Max(0, j - 96);
            double prev24 = (double)kl[ref24].ClosePrice;
            btc24hChg[j] = prev24 > 0 ? ((double)kl[j].ClosePrice - prev24) / prev24 * 100.0 : 0;
        }

        Console.WriteLine("[walk-forward 4 변형 동시 실행]");
        for (int j = 70; j < kl.Count - 11; j++)
        {
            // 학습: 라벨 완전 관측 가능한 sIdx = j-11
            int sIdx = j - 11;
            if (sIdx >= 60)
            {
                if (feats5[sIdx] != null)
                {
                    var fv = new FeatureVector { Features = feats5[sIdx], Label = labels[sIdx] };
                    euclid.AddSample(fv);
                    lorenz.AddSample(fv);
                }
                if (feats7[sIdx] != null) pine.AddSample(feats7[sIdx], labels[sIdx]);
            }
            if (j < 270) continue;   // 워밍업 (200 샘플 이상)
            if (feats5[j] == null) continue;

            // a) Euclidean
            double wrE = euclid.PredictWinRate(feats5[j]);
            if (wrE >= 0.70)
            {
                sigEuclid++;
                if (labels[j] == 1) hitEuclid++;
            }

            // b) Simple Lorentzian
            double wrL = lorenz.PredictWinRate(feats5[j]);
            if (wrL >= 0.70)
            {
                sigLorenz++;
                if (labels[j] == 1) hitLorenz++;
            }

            // c) Pine ANN Lorentzian
            if (feats7[j] != null)
            {
                var p = pine.Predict(feats7[j]);
                double wrP = p.K > 0 ? (double)p.PositiveVotes / p.K : 0;
                if (p.IsReady && p.Prediction > 0 && wrP >= 0.70)
                {
                    sigPine++;
                    if (labels[j] == 1) hitPine++;
                }
            }

            // d) Lorentzian + BTC 듀얼 가드 (BTC 1H EMA20↑ + 24H > 5%)
            //   1H EMA 상승 = btcEma1h[j] > btcEma1h[j-4]
            bool btcEmaUp = j >= 4 && btcEma1h[j] > btcEma1h[j - 4];
            bool btc24Bull = btc24hChg[j] > 5.0;
            if (wrL >= 0.70 && btcEmaUp && btc24Bull)
            {
                sigBtcGuard++;
                if (labels[j] == 1) hitBtcGuard++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"==== 4가지 KNN 변형 walk-forward 결과 (사용자 TP/SL 라벨) ====");
        Console.WriteLine($"  데이터: BTCUSDT 15m {kl.Count}봉");
        Console.WriteLine($"  라벨: 10봉 내 TP +{labelTpPct}% 도달 → 1, SL -{labelSlPct}% 도달 또는 timeout → -1 (2:1 R:R)");
        Console.WriteLine($"  진입 조건: 분류기가 winRate ≥ 70% 신호 발화");
        Console.WriteLine($"  실제 승률: 진입 시 TP 가 SL 보다 먼저 도달했는지");
        Console.WriteLine();
        Console.WriteLine($"  {"변형",-50} {"신호 수",8} {"적중",6} {"실측 승률",10}");
        Console.WriteLine("  " + new string('-', 80));
        PrintRow("a) Pine ANN Lorentzian (7-feat, 4봉 subsample)", sigPine, hitPine);
        PrintRow("b) Simple Euclidean KNN (5-feat K=10) [사용자 reference]", sigEuclid, hitEuclid);
        PrintRow("c) Simple Lorentzian KNN (5-feat K=10)", sigLorenz, hitLorenz);
        PrintRow("d) c + BTC 듀얼 가드 (1H EMA20↑ + 24H > 5%)", sigBtcGuard, hitBtcGuard);
        Console.WriteLine();
        Console.WriteLine($"  TradingView 표시 winRate ≈ 67.7% — 위 변형 중 어느 것도 그 수치에 가까우면 알고리즘이 맞은 것");
        Console.WriteLine($"  모두 ~50% 면 신호 자체가 미래 예측 못 하는 것");

        static void PrintRow(string label, int sig, int hit)
        {
            double wr = sig > 0 ? 100.0 * hit / sig : 0;
            Console.WriteLine($"  {label,-50} {sig,8} {hit,6} {wr,9:F2}%");
        }
    }

    // [v5.23.0] jdehorty Lorentzian Classification 공식 필터 풀세트 (15m TF)
    //   진입 가드 (모두 통과 필요):
    //     1) KNN: Prediction > 0 + PositiveVotes/K ≥ 0.70
    //     2) Volatility: ATR(1) > ATR(10) (변동성 확장)
    //     3) Regime: Kalman-like KLMF trend slope > -0.1 (하락추세 차단)
    //     4) ADX(14) > 20 (추세장만)
    //     5) EMA(200) > close 거부 (가격이 EMA200 위)
    //     6) SMA(200) > close 거부 (가격이 SMA200 위)
    //     7) Nadaraya-Watson Rational Quadratic kernel 방향 = up
    //   청산: 동적 (kernel direction flip 또는 KNN signal flip) — TP/SL 아님
    //   Walk-forward: 바 j 에서 바 j-5 샘플 추가 (4봉 후 라벨 완전 관측)

    private static double CalcEMA(List<IBinanceKline> kl, int idx, int period)
    {
        if (idx < period - 1) return (double)kl[idx].ClosePrice;
        double k = 2.0 / (period + 1);
        double ema = (double)kl[idx - period + 1].ClosePrice;
        for (int q = idx - period + 2; q <= idx; q++)
            ema = (double)kl[q].ClosePrice * k + ema * (1 - k);
        return ema;
    }

    private static double CalcSMA(List<IBinanceKline> kl, int idx, int period)
    {
        if (idx < period - 1) return (double)kl[idx].ClosePrice;
        double sum = 0;
        for (int q = idx - period + 1; q <= idx; q++) sum += (double)kl[q].ClosePrice;
        return sum / period;
    }

    // True Range
    private static double CalcTR(List<IBinanceKline> kl, int idx)
    {
        if (idx < 1) return (double)(kl[idx].HighPrice - kl[idx].LowPrice);
        double h = (double)kl[idx].HighPrice, l = (double)kl[idx].LowPrice, pc = (double)kl[idx - 1].ClosePrice;
        return Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
    }

    // ATR(period) at idx (Wilder smoothing)
    private static double CalcATR(List<IBinanceKline> kl, int idx, int period)
    {
        if (idx < period) return CalcTR(kl, idx);
        double sum = 0;
        for (int q = idx - period + 1; q <= idx; q++) sum += CalcTR(kl, q);
        return sum / period;
    }

    // ADX(14) — Wilder 정통: 마지막 봉의 smoothed ADX 반환
    private static double CalcADX_idx(List<IBinanceKline> kl, int idx, int period)
    {
        if (idx < period * 2) return 0.0;
        double[] tr = new double[idx + 1], pdm = new double[idx + 1], ndm = new double[idx + 1];
        for (int i = 1; i <= idx; i++)
        {
            double high = (double)kl[i].HighPrice, low = (double)kl[i].LowPrice, prevClose = (double)kl[i - 1].ClosePrice;
            tr[i] = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            double upMove = high - (double)kl[i - 1].HighPrice;
            double downMove = (double)kl[i - 1].LowPrice - low;
            pdm[i] = upMove > downMove && upMove > 0 ? upMove : 0;
            ndm[i] = downMove > upMove && downMove > 0 ? downMove : 0;
        }
        double atr = 0, pdmS = 0, ndmS = 0;
        for (int i = 1; i <= period; i++) { atr += tr[i]; pdmS += pdm[i]; ndmS += ndm[i]; }
        double adx = 0;
        bool adxInit = false;
        for (int i = period + 1; i <= idx; i++)
        {
            atr  = atr  - (atr  / period) + tr[i];
            pdmS = pdmS - (pdmS / period) + pdm[i];
            ndmS = ndmS - (ndmS / period) + ndm[i];
            if (atr < 1e-12) continue;
            double pdi = 100.0 * pdmS / atr;
            double ndi = 100.0 * ndmS / atr;
            double dx = (pdi + ndi) > 1e-12 ? 100.0 * Math.Abs(pdi - ndi) / (pdi + ndi) : 0;
            if (!adxInit) { adx = dx; adxInit = true; }
            else adx = (adx * (period - 1) + dx) / period;
        }
        return adx;
    }

    // Kalman-like Modified Filter (KLMF) trend slope approximation
    //   jdehorty regime filter 로 사용. close 의 EMA 5봉 차이 / atr 비교
    private static double CalcRegimeSlope(List<IBinanceKline> kl, int idx)
    {
        if (idx < 50) return 0.0;
        double ema = CalcEMA(kl, idx, 50);
        double emaPrev = CalcEMA(kl, idx - 5, 50);
        double atr = CalcATR(kl, idx, 14);
        if (atr < 1e-12) return 0.0;
        return (ema - emaPrev) / atr;
    }

    // Nadaraya-Watson Rational Quadratic kernel estimator
    //   h=8, r=8, x=25, lookback=barIdx
    //   결과: 현재 봉의 kernel 추정값
    private static double CalcNWKernel(List<IBinanceKline> kl, int idx, int h = 8, double r = 8.0, int x = 25)
    {
        int look = Math.Min(idx, 200);
        double num = 0, den = 0;
        for (int i = 0; i < look; i++)
        {
            int srcIdx = idx - i;
            if (srcIdx < 0) break;
            double w = Math.Pow(1.0 + (i * i) / (h * h * 2.0 * r), -r);
            num += (double)kl[srcIdx].ClosePrice * w;
            den += w;
        }
        return den > 1e-12 ? num / den : (double)kl[idx].ClosePrice;
    }

    // ===== [v5.23.54] 손실 코인의 흑자 구간 vs 손실 구간 패턴 분석 =====
    //   같은 코인 (MANA/AVAX) 내에서 진입이 흑자/손실 나는 차이를 만드는 시장 상태 찾기
    private static async Task RunLossPatternAnalysisAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  손실 코인 — 흑자 진입 vs 손실 진입의 시장 상태 비교");
        Console.WriteLine("  각 진입 시점에 1h ADX/EMA dev/recent volatility/시간대 등 측정");
        Console.WriteLine("================================================================");

        var lossCoins = new[] { "MANAUSDT", "AVAXUSDT", "AXSUSDT", "AAVEUSDT" };
        const decimal tpPct = 2.0m;
        const decimal slPct = 5.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars = 96;
        const decimal slippagePct = 0.05m;

        foreach (var sym in lossCoins)
        {
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);
                var k15 = await FetchKlines15mAsync(sym, 6);
                if (k1h.Count < 250 || k15.Count < 500) continue;

                int n1h = k1h.Count;
                int n = k15.Count;
                var ema20_1h = new double[n1h];
                var adx_1h = new double[n1h];
                for (int q = 50; q < n1h; q++)
                {
                    ema20_1h[q] = CalcEMA(k1h, q, 20);
                    adx_1h[q] = CalcADX_idx(k1h, q, 14);
                }

                var trades = new List<(bool win, decimal pnl, double adx, double emaDev, double atrPct, int hour)>();

                for (int j = 22; j < n - maxHoldBars - 1; j++)
                {
                    var prev15 = k15[j - 1];
                    if (prev15.ClosePrice <= prev15.OpenPrice) continue;

                    DateTime t15 = k15[j].OpenTime;
                    int q1h = -1;
                    for (int qq = n1h - 1; qq >= 50; qq--) { if (k1h[qq].CloseTime <= t15) { q1h = qq; break; } }
                    if (q1h < 50) continue;
                    if ((double)k1h[q1h].ClosePrice <= ema20_1h[q1h]) continue;

                    decimal body = prev15.ClosePrice - prev15.OpenPrice;
                    decimal upperWick = prev15.HighPrice - prev15.ClosePrice;
                    if (upperWick > 0 && body / upperWick < 0.3m) continue;

                    decimal entryPrice = k15[j].OpenPrice;
                    decimal tpPx = entryPrice * (1m + tpPct / 100m);
                    decimal slPx = entryPrice * (1m - slPct / 100m);
                    decimal exitPrice = 0m;
                    bool win_ = false;
                    bool exited = false;
                    for (int q = j; q <= Math.Min(n - 1, j + maxHoldBars); q++)
                    {
                        if (k15[q].LowPrice <= slPx) { exitPrice = slPx; win_ = false; exited = true; break; }
                        if (k15[q].HighPrice >= tpPx) { exitPrice = tpPx; win_ = true; exited = true; break; }
                    }
                    if (!exited) { int last = Math.Min(n - 1, j + maxHoldBars); exitPrice = k15[last].ClosePrice; win_ = exitPrice > entryPrice; }

                    decimal pmove = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                    decimal pnl = margin * lev * pmove / 100m;

                    double currentAdx = adx_1h[q1h];
                    double currentEmaDev = ((double)k1h[q1h].ClosePrice - ema20_1h[q1h]) / ema20_1h[q1h] * 100.0;
                    double atrPctLocal = q1h >= 14 ? CalcATR(k1h, q1h, 14) / (double)k1h[q1h].ClosePrice * 100.0 : 0;
                    int hourUtc = k15[j].OpenTime.Hour;

                    trades.Add((win_, pnl, currentAdx, currentEmaDev, atrPctLocal, hourUtc));
                }

                if (trades.Count == 0) continue;

                var wins = trades.Where(t => t.win).ToList();
                var losses = trades.Where(t => !t.win).ToList();
                Console.WriteLine();
                Console.WriteLine($"=== {sym} (Total {trades.Count}건) ===");
                Console.WriteLine($"               WINS ({wins.Count})         LOSSES ({losses.Count})");
                Console.WriteLine($"  Avg PnL:     ${wins.Average(t => t.pnl),7:F2}        ${losses.Average(t => t.pnl),7:F2}");
                Console.WriteLine($"  Avg 1h ADX:  {wins.Average(t => t.adx),6:F1}            {losses.Average(t => t.adx),6:F1}");
                Console.WriteLine($"  Avg EMA dev: {wins.Average(t => t.emaDev),6:F2}%           {losses.Average(t => t.emaDev),6:F2}%");
                Console.WriteLine($"  Avg ATR%:    {wins.Average(t => t.atrPct),6:F2}%           {losses.Average(t => t.atrPct),6:F2}%");

                // ADX 버킷별 WR/PnL
                var adxBuckets = new[] {
                    ("ADX<20", trades.Where(t => t.adx < 20).ToList()),
                    ("20-25", trades.Where(t => t.adx >= 20 && t.adx < 25).ToList()),
                    ("25-30", trades.Where(t => t.adx >= 25 && t.adx < 30).ToList()),
                    ("30-40", trades.Where(t => t.adx >= 30 && t.adx < 40).ToList()),
                    ("40+",   trades.Where(t => t.adx >= 40).ToList()),
                };
                Console.WriteLine($"  ADX 버킷별:");
                foreach (var (label, list) in adxBuckets)
                {
                    if (list.Count == 0) continue;
                    double wr = list.Count(x => x.win) * 100.0 / list.Count;
                    decimal pnl = list.Sum(x => x.pnl);
                    Console.WriteLine($"    {label,-7} N={list.Count,4}  WR={wr,5:F2}%  PnL=${pnl,8:F2}");
                }

                // EMA dev 버킷별
                var emaBuckets = new[] {
                    ("dev<1%",   trades.Where(t => t.emaDev < 1).ToList()),
                    ("1-3%",     trades.Where(t => t.emaDev >= 1 && t.emaDev < 3).ToList()),
                    ("3-5%",     trades.Where(t => t.emaDev >= 3 && t.emaDev < 5).ToList()),
                    ("5%+",      trades.Where(t => t.emaDev >= 5).ToList()),
                };
                Console.WriteLine($"  EMA20 괴리율 버킷별:");
                foreach (var (label, list) in emaBuckets)
                {
                    if (list.Count == 0) continue;
                    double wr = list.Count(x => x.win) * 100.0 / list.Count;
                    decimal pnl = list.Sum(x => x.pnl);
                    Console.WriteLine($"    {label,-7} N={list.Count,4}  WR={wr,5:F2}%  PnL=${pnl,8:F2}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {sym} fail: {ex.Message}");
            }
        }
    }

    // ===== [v5.23.53] 손실 코인 차트 분석 — 왜 MANA/AVAX/AXS 등은 적자? =====
    private static async Task RunLossCoinAnalysisAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  손실 코인 차트 분석 — 패턴 찾기");
        Console.WriteLine("  대상: MANA, AVAX, AXS, AAVE, NEAR, CRV (TOP 손실)");
        Console.WriteLine("  비교: TRX, DOGE, DYDX, INJ (TOP 흑자)");
        Console.WriteLine("================================================================");

        var lossCoins = new[] { "MANAUSDT", "AVAXUSDT", "AXSUSDT", "AAVEUSDT", "NEARUSDT", "CRVUSDT" };
        var winCoins = new[] { "TRXUSDT", "DOGEUSDT", "DYDXUSDT", "INJUSDT", "SNXUSDT", "ZECUSDT" };
        var allCoins = lossCoins.Concat(winCoins).ToArray();

        const decimal tpPct = 2.0m;
        const decimal slPct = 5.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars = 96;
        const decimal slippagePct = 0.05m;

        Console.WriteLine($"\n[fetch 90d 1h + 15m × {allCoins.Length}]");

        foreach (var sym in allCoins)
        {
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);
                if (k1h.Count < 250) continue;
                var k15 = await FetchKlines15mAsync(sym, 6);
                if (k15.Count < 500) continue;

                // 1h indicators
                int n1h = k1h.Count;
                var ema20_1h = new double[n1h];
                var ema50_1h = new double[n1h];
                for (int q = 50; q < n1h; q++)
                {
                    ema20_1h[q] = CalcEMA(k1h, q, 20);
                    ema50_1h[q] = CalcEMA(k1h, q, 50);
                }

                // 1h 통계
                int barsAboveEma20 = 0, barsBelowEma20 = 0;
                int barsUptrend = 0, barsDowntrend = 0;   // ema20 > ema50
                for (int q = 50; q < n1h; q++)
                {
                    double c = (double)k1h[q].ClosePrice;
                    if (c > ema20_1h[q]) barsAboveEma20++; else barsBelowEma20++;
                    if (ema20_1h[q] > ema50_1h[q]) barsUptrend++; else barsDowntrend++;
                }
                int totalBars = barsAboveEma20 + barsBelowEma20;
                double pctAbove = totalBars > 0 ? barsAboveEma20 * 100.0 / totalBars : 0;
                double pctUptrend = (barsUptrend + barsDowntrend) > 0 ? barsUptrend * 100.0 / (barsUptrend + barsDowntrend) : 0;

                // 변동성 (1h ATR/Price)
                double avgAtrPct = 0;
                int atrCount = 0;
                for (int q = 14; q < n1h; q++)
                {
                    double atr = CalcATR(k1h, q, 14);
                    double px = (double)k1h[q].ClosePrice;
                    if (px > 0)
                    {
                        avgAtrPct += atr / px * 100.0;
                        atrCount++;
                    }
                }
                avgAtrPct = atrCount > 0 ? avgAtrPct / atrCount : 0;

                // 현재 가드 진입 시뮬레이션
                int n = k15.Count;
                int entries = 0, wins = 0;
                decimal sumPnl = 0m;

                for (int j = 22; j < n - maxHoldBars - 1; j++)
                {
                    var prev15 = k15[j - 1];
                    if (prev15.ClosePrice <= prev15.OpenPrice) continue;

                    DateTime t15 = k15[j].OpenTime;
                    int q1h = -1;
                    for (int qq = n1h - 1; qq >= 50; qq--) { if (k1h[qq].CloseTime <= t15) { q1h = qq; break; } }
                    if (q1h < 50) continue;
                    if ((double)k1h[q1h].ClosePrice <= ema20_1h[q1h]) continue;

                    decimal body = prev15.ClosePrice - prev15.OpenPrice;
                    decimal upperWick = prev15.HighPrice - prev15.ClosePrice;
                    if (upperWick > 0 && body / upperWick < 0.3m) continue;

                    decimal entryPrice = k15[j].OpenPrice;
                    decimal tpPx = entryPrice * (1m + tpPct / 100m);
                    decimal slPx = entryPrice * (1m - slPct / 100m);
                    decimal exitPrice = 0m;
                    bool win_ = false;
                    bool exited = false;
                    for (int q = j; q <= Math.Min(n - 1, j + maxHoldBars); q++)
                    {
                        if (k15[q].LowPrice <= slPx) { exitPrice = slPx; win_ = false; exited = true; break; }
                        if (k15[q].HighPrice >= tpPx) { exitPrice = tpPx; win_ = true; exited = true; break; }
                    }
                    if (!exited) { int last = Math.Min(n - 1, j + maxHoldBars); exitPrice = k15[last].ClosePrice; win_ = exitPrice > entryPrice; }

                    decimal pmove = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                    sumPnl += margin * lev * pmove / 100m;
                    entries++;
                    if (win_) wins++;
                }

                double wr = entries > 0 ? wins * 100.0 / entries : 0;
                string cat = lossCoins.Contains(sym) ? "LOSS" : "WIN ";
                Console.WriteLine($"[{cat}] {sym,-12}  1h>EMA20={pctAbove,5:F1}%  UpTrend={pctUptrend,5:F1}%  ATR={avgAtrPct,4:F2}%  Entries={entries,4}  WR={wr,5:F2}%  PnL=${sumPnl,8:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {sym} fail: {ex.Message}");
            }
        }
    }

    // ===== [v5.23.58] LORENTZIAN 1h prototype — 사용자 지시 "1시간 기준 데이터로 로직" =====
    //   기존 LORENTZIAN: 15m × 1500봉 (~16일) KNN
    //   신규 LORENTZIAN_1H: 1h × 1500봉 (~62일) KNN
    //   목적: 1h 봉 단위로 학습/예측해서 단기 노이즈 줄이고 큰 흐름 잡기
    // [v5.23.59] LORENTZIAN 1h — fetch-once + (threshold × TP/SL) sweep, 1-position-at-a-time.
    //   v5.23.58 단일 config(2%/5%, pred>0)는 -$30k 손실. KNN 예측은 TP/SL/threshold 와 무관하므로
    //   심볼당 walk-forward 1회로 봉별 (pred, emaOk) 캐싱 후 in-memory sweep.
    //   심볼당 동시 1포지션만(slot 가정) → v5.23.58 의 1611건 과매매 제거, 실제 edge 측정.
    private static async Task RunLorentzian1hTestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  [v5.23.59] LORENTZIAN 1h sweep — 30개 알트 × 1500봉");
        Console.WriteLine("  학습: 1h 처음 500봉 / 검증: 501~ walk-forward (online learning)");
        Console.WriteLine("  심볼당 동시 1포지션 · 1h EMA20 방향가드 · lev=15x margin=$30");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT",
            "DOGEUSDT","AVAXUSDT","LINKUSDT","SUIUSDT","NEARUSDT",
            "ARBUSDT","OPUSDT","INJUSDT","SEIUSDT","ICPUSDT",
            "DYDXUSDT","ZECUSDT","TAOUSDT","ATOMUSDT","AAVEUSDT",
            "ADAUSDT","DOTUSDT","UNIUSDT","LTCUSDT","TRXUSDT",
            "FILUSDT","ETCUSDT","ALGOUSDT","VETUSDT","HBARUSDT"
        };

        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars1h = 48;   // 48시간 = 2일 최대 보유
        const decimal slippagePct = 0.05m;

        // ── 1) 심볼별 fetch + walk-forward 1회 → 봉별 시그널 캐시 ─────────────
        //    bars: (entryIdx = j+1, pred.Prediction, emaOk)
        var cache = new Dictionary<string, (List<IBinanceKline> k, List<(int ei, int pred, bool emaOk)> bars)>();
        foreach (var sym in symbols)
        {
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);   // ~1500봉
                if (k1h.Count < 600) { Console.WriteLine($"  {sym} skip ({k1h.Count} bars)"); continue; }

                var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);

                // 처음 500봉 학습 (j=60..499)
                for (int j = 60; j < 500 && j < k1h.Count - 4; j++)
                {
                    var w = k1h.GetRange(0, j + 1);
                    var f = LorentzianFeatures.Extract(w);
                    if (f == null) continue;
                    int lbl = k1h[j + 4].ClosePrice > k1h[j].ClosePrice ? 1 : k1h[j + 4].ClosePrice < k1h[j].ClosePrice ? -1 : 0;
                    engine.AddSample(f, lbl);
                }

                var bars = new List<(int ei, int pred, bool emaOk)>();
                int ready = 0, posCnt = 0;
                for (int j = 500; j < k1h.Count - maxHoldBars1h - 1; j++)
                {
                    var w = k1h.GetRange(0, j + 1);
                    var f = LorentzianFeatures.Extract(w);
                    if (f == null) continue;
                    var pred = engine.Predict(f);
                    if (!pred.IsReady) continue;
                    ready++;
                    bool emaOk = (double)k1h[j].ClosePrice >= CalcEMA(k1h, j, 20);
                    if (pred.Prediction > 0) posCnt++;
                    if (k1h[j + 1].OpenPrice > 0)
                        bars.Add((j + 1, pred.Prediction, emaOk));
                    // online learning (TP/SL/threshold 와 무관 → 1회만)
                    if (j + 4 < k1h.Count)
                    {
                        int lbl = k1h[j + 4].ClosePrice > k1h[j].ClosePrice ? 1 : k1h[j + 4].ClosePrice < k1h[j].ClosePrice ? -1 : 0;
                        engine.AddSample(f, lbl);
                    }
                }
                cache[sym] = (k1h, bars);
                Console.WriteLine($"  {sym,-12} 학습={engine.SampleCount,4} Ready={ready,4} Pos(>0)={posCnt,4} 캐시봉={bars.Count,4}");
            }
            catch (Exception ex) { Console.WriteLine($"  {sym} fail: {ex.Message}"); }
        }

        // ── 2) (threshold × TP/SL) sweep — in-memory, 심볼당 1포지션 ──────────
        // pred.Prediction ∈ [-K, +K], K=neighborsCount=8 → thr 7 이상은 사실상 진입 0
        int[] thresholds = { 0, 2, 3, 4, 5, 6 };
        var tpsl = new (decimal tp, decimal sl)[]
        {
            (2.0m, 5.0m),   // v5.23.58 baseline
            (1.0m, 3.0m),   // 프로젝트 컨벤션 (TargetRoe15/StopLoss45 @15x)
            (1.5m, 1.5m),   // 1:1
            (2.0m, 1.0m),   // 2:1 (펌프 따라가기)
            (3.0m, 1.5m),   // 2:1 wide
            (1.5m, 0.7m),   // 빠른 익절
        };

        // 한 진입의 결과 시뮬레이션 → (pnl, win, exitIdx)
        (decimal pnl, bool win, int exitIdx) Sim(List<IBinanceKline> k, int ei, decimal tp, decimal sl)
        {
            decimal ep = k[ei].OpenPrice;
            decimal tpPx = ep * (1m + tp / 100m), slPx = ep * (1m - sl / 100m);
            int lastQ = Math.Min(k.Count - 1, ei + maxHoldBars1h);
            for (int q = ei; q <= lastQ; q++)
            {
                if (k[q].LowPrice <= slPx) return (margin * lev * (-sl - slippagePct) / 100m, false, q);
                if (k[q].HighPrice >= tpPx) return (margin * lev * (tp - slippagePct) / 100m, true, q);
            }
            decimal mv = (k[lastQ].ClosePrice - ep) / ep * 100m - slippagePct;
            return (margin * lev * mv / 100m, k[lastQ].ClosePrice > ep, lastQ);
        }

        var results = new List<(int thr, decimal tp, decimal sl, int n, int w, decimal pnl, decimal aw, decimal al)>();
        foreach (int thr in thresholds)
        foreach (var (tp, sl) in tpsl)
        {
            int n = 0, w = 0; decimal pnl = 0m; decimal sumW = 0m, sumL = 0m; int wc = 0, lc = 0;
            foreach (var (sym, (k, bars)) in cache)
            {
                int freeFrom = 0;   // 심볼당 1포지션: 이전 거래 종료 봉 이후만 진입
                foreach (var (ei, pred, emaOk) in bars)
                {
                    if (ei < freeFrom) continue;
                    if (pred <= thr || !emaOk) continue;
                    var (tpnl, twin, exitIdx) = Sim(k, ei, tp, sl);
                    n++; pnl += tpnl;
                    if (twin) { w++; sumW += tpnl; wc++; } else { sumL += tpnl; lc++; }
                    freeFrom = exitIdx + 1;
                }
            }
            results.Add((thr, tp, sl, n, w, pnl, wc > 0 ? sumW / wc : 0m, lc > 0 ? sumL / lc : 0m));
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  SWEEP 결과 (PnL 내림차순)");
        Console.WriteLine($"  {"thr",4} {"TP/SL",-9} {"진입",6} {"WR%",7} {"PnL$",11} {"평균$",8} {"AvgW",8} {"AvgL",8}");
        Console.WriteLine("  " + new string('-', 70));
        foreach (var r in results.OrderByDescending(r => r.pnl))
        {
            double wr = r.n > 0 ? 100.0 * r.w / r.n : 0;
            decimal avg = r.n > 0 ? r.pnl / r.n : 0m;
            string flag = r.pnl > 0 ? " ✅" : "";
            Console.WriteLine($"  {r.thr,4} {r.tp:F1}/{r.sl:F1}{"",-3} {r.n,6} {wr,6:F1}% {r.pnl,10:F2} {avg,7:F2} {r.aw,7:F2} {r.al,7:F2}{flag}");
        }

        var withTrades = results.Where(r => r.n > 0).ToList();
        Console.WriteLine();
        if (withTrades.Count == 0)
        {
            Console.WriteLine("  진입 0건 — 모든 config 무효");
        }
        else
        {
            var best = withTrades.OrderByDescending(r => r.pnl).First();
            Console.WriteLine($"  BEST(진입>0): thr>{best.thr} TP{best.tp:F1}/SL{best.sl:F1} → 진입 {best.n}건 WR {100.0 * best.w / best.n:F1}% PnL ${best.pnl:F2}");
            if (best.pnl <= 0)
                Console.WriteLine("  ⚠ 모든 config 손실 — 1h LORENTZIAN 단독 진입은 edge 없음. 방향게이트 용도로만 검토 권장.");
        }
    }

    // ===== [v5.23.59] 1h LORENTZIAN = 방향게이트 A/B (사용자 결정: 단독진입X, 방향확인 필터로만) =====
    //   BASELINE = v5.23.53 프로덕션 스택 (15m 양봉 + 1h EMA20 + body/wick≥0.3)
    //   +LOR(thr) = BASELINE 통과 후 추가로 "그 시점 1h Lorentzian pred > thr" 요구
    //   동일 후보 집합으로 A/B → 게이트가 손실거래를 걸러내는지(가치 있음) 거래량만 줄이는지 판정
    private static async Task RunLorentzian1hGateTestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  [v5.23.59] 1h LORENTZIAN 방향게이트 A/B");
        Console.WriteLine("  BASELINE: 15m 양봉 + 1h EMA20 + body/wick≥0.3 (v5.23.53)");
        Console.WriteLine("  +LOR(thr): 위 통과 + 해당시점 1h Lorentzian pred>thr (방향확인)");
        Console.WriteLine("  TP=2%/SL=5% lev=15x margin=$30 · 동일 후보집합 A/B");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "DOGEUSDT","AVAXUSDT","ARBUSDT","OPUSDT","SUIUSDT","INJUSDT","LINKUSDT","SEIUSDT","NEARUSDT","ICPUSDT",
            "DYDXUSDT","ZECUSDT","TAOUSDT","ATOMUSDT","AAVEUSDT","ADAUSDT","DOTUSDT","UNIUSDT","LTCUSDT","TRXUSDT",
            "FILUSDT","ETCUSDT","ALGOUSDT","VETUSDT","HBARUSDT","XLMUSDT","SANDUSDT","MANAUSDT","AXSUSDT","GALAUSDT",
            "CHZUSDT","ENJUSDT","GRTUSDT","COMPUSDT","MKRUSDT","CRVUSDT","SNXUSDT","SUSHIUSDT","RUNEUSDT","FETUSDT"
        };

        const decimal tpPct = 2.0m;
        const decimal slPct = 5.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars15m = 96;
        const decimal slippagePct = 0.05m;
        int[] thresholds = { 0, 2, 3, 4, 5 };

        Console.WriteLine($"\n[fetch 1h(3p) + 15m(6p) × {symbols.Length} alts]");

        // 후보: (sym, entryTime, pnl, win, lorPred)  — BASELINE 통과 + 1h pred ready 인 것만
        var cand = new List<(string sym, decimal pnl, bool win, int lorPred)>();
        int baseEntriesNoLor = 0;   // BASELINE 통과했지만 1h pred not-ready 라 A/B 제외된 건수

        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);
                if (k1h.Count < 600) continue;
                var k15 = await FetchKlines15mAsync(sym, 6);
                if (k15.Count < 500) continue;

                int n1h = k1h.Count, n15 = k15.Count;

                // ── 1h Lorentzian: 처음 500봉 학습 + walk-forward 봉별 pred 캐시 ──
                var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
                for (int j = 60; j < 500 && j < n1h - 4; j++)
                {
                    var w = k1h.GetRange(0, j + 1);
                    var f = LorentzianFeatures.Extract(w);
                    if (f == null) continue;
                    int lbl = k1h[j + 4].ClosePrice > k1h[j].ClosePrice ? 1 : k1h[j + 4].ClosePrice < k1h[j].ClosePrice ? -1 : 0;
                    engine.AddSample(f, lbl);
                }
                var lorPredAt = new int[n1h];
                var lorReady = new bool[n1h];
                for (int j = 500; j < n1h - 1; j++)
                {
                    var w = k1h.GetRange(0, j + 1);
                    var f = LorentzianFeatures.Extract(w);
                    if (f == null) continue;
                    var pr = engine.Predict(f);
                    if (pr.IsReady) { lorReady[j] = true; lorPredAt[j] = pr.Prediction; }
                    if (j + 4 < n1h)
                    {
                        int lbl = k1h[j + 4].ClosePrice > k1h[j].ClosePrice ? 1 : k1h[j + 4].ClosePrice < k1h[j].ClosePrice ? -1 : 0;
                        engine.AddSample(f, lbl);
                    }
                }

                // 1h EMA20 pre-compute
                var ema20_1h = new double[n1h];
                for (int q = 20; q < n1h; q++) ema20_1h[q] = CalcEMA(k1h, q, 20);

                for (int j = 22; j < n15 - maxHoldBars15m - 1; j++)
                {
                    var prev15 = k15[j - 1];
                    if (prev15.ClosePrice <= prev15.OpenPrice) continue;   // 15m 양봉

                    // 해당 15m 시점의 마지막 *마감* 1h 봉
                    DateTime t15 = k15[j].OpenTime;
                    int q1h = -1;
                    for (int qq = n1h - 1; qq >= 20; qq--) { if (k1h[qq].CloseTime <= t15) { q1h = qq; break; } }
                    if (q1h < 20) continue;

                    // BASELINE: 1h EMA20 방향
                    if ((double)k1h[q1h].ClosePrice <= ema20_1h[q1h]) continue;
                    // BASELINE: 15m body/wick
                    decimal upperWick = prev15.HighPrice - prev15.ClosePrice;
                    if (upperWick > 0 && (prev15.ClosePrice - prev15.OpenPrice) / upperWick < 0.3m) continue;

                    decimal entryPrice = k15[j].OpenPrice;
                    if (entryPrice <= 0) continue;

                    // A/B 공정성: 1h pred ready 인 후보만 비교집합에 포함
                    if (!lorReady[q1h]) { baseEntriesNoLor++; continue; }

                    decimal tpPx = entryPrice * (1m + tpPct / 100m);
                    decimal slPx = entryPrice * (1m - slPct / 100m);
                    decimal exitPrice = 0m; bool win_ = false; bool exited = false;
                    for (int q = j; q <= Math.Min(n15 - 1, j + maxHoldBars15m); q++)
                    {
                        if (k15[q].LowPrice <= slPx) { exitPrice = slPx; win_ = false; exited = true; break; }
                        if (k15[q].HighPrice >= tpPx) { exitPrice = tpPx; win_ = true; exited = true; break; }
                    }
                    if (!exited) { int last = Math.Min(n15 - 1, j + maxHoldBars15m); exitPrice = k15[last].ClosePrice; win_ = exitPrice > entryPrice; }

                    decimal pmove = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                    cand.Add((sym, margin * lev * pmove / 100m, win_, lorPredAt[q1h]));
                }
                if (fIdx % 10 == 0) Console.Write($"[{fIdx}/{symbols.Length}] ");
            }
            catch (Exception ex) { Console.WriteLine($"  {sym} fail: {ex.Message}"); }
        }

        Console.WriteLine($"\n비교 후보(BASELINE 통과 + 1h pred ready): {cand.Count}건  (1h pred 미준비로 제외: {baseEntriesNoLor}건)");
        if (cand.Count == 0) { Console.WriteLine("후보 0건 — 종료"); return; }

        void Print(string label, List<(string sym, decimal pnl, bool win, int lorPred)> ts)
        {
            int n = ts.Count, w = ts.Count(t => t.win);
            decimal pnl = ts.Sum(t => t.pnl);
            decimal aw = ts.Where(t => t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
            decimal al = ts.Where(t => !t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
            Console.WriteLine($"  {label,-14} 진입 {n,5} WR {(n > 0 ? 100.0 * w / n : 0),5:F1}% PnL ${pnl,9:F2} 평균 ${(n > 0 ? pnl / n : 0),6:F2}  AvgW ${aw,6:F2} AvgL ${al,6:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  A/B 결과");
        Console.WriteLine("================================================================");
        Print("BASELINE", cand);
        foreach (int thr in thresholds)
        {
            var kept = cand.Where(t => t.lorPred > thr).ToList();
            var removed = cand.Where(t => t.lorPred <= thr).ToList();
            Print($"+LOR(>{thr})", kept);
            // 게이트 가치 = 걸러낸 거래가 *남긴* 거래보다 거래당 더 나쁜가? (전체가 적자라 절대부호로는 판단 불가)
            int rw = removed.Count(t => t.win);
            decimal rper = removed.Count > 0 ? removed.Sum(t => t.pnl) / removed.Count : 0m;
            decimal kper = kept.Count > 0 ? kept.Sum(t => t.pnl) / kept.Count : 0m;
            Console.WriteLine($"    └ 걸러낸 {removed.Count,5}건 WR {(removed.Count > 0 ? 100.0 * rw / removed.Count : 0),5:F1}% 거래당 ${rper,6:F3} vs 남긴 ${kper,6:F3}  → {(rper < kper ? "게이트 유효(나쁜거래 제거)" : "게이트 역효과(좋은거래 제거)")}");
        }

        // 판정: 게이트 적용 시 평균 거래당 PnL 이 개선되는 thr 가 있는가
        decimal basePerTrade = cand.Sum(t => t.pnl) / cand.Count;
        var bestThr = thresholds
            .Select(thr => { var k = cand.Where(t => t.lorPred > thr).ToList(); return (thr, n: k.Count, per: k.Count > 0 ? k.Sum(t => t.pnl) / k.Count : decimal.MinValue); })
            .Where(x => x.n >= 30)
            .OrderByDescending(x => x.per)
            .FirstOrDefault();
        Console.WriteLine();
        Console.WriteLine($"  BASELINE 거래당 ${basePerTrade:F3}");
        if (bestThr.n > 0)
        {
            string verdict = bestThr.per > basePerTrade ? "✅ 방향게이트가 거래품질 개선" : "❌ 방향게이트가 거래품질 개선 못함 (거래량만 감소)";
            Console.WriteLine($"  BEST 게이트 thr>{bestThr.thr}: 거래당 ${bestThr.per:F3} ({bestThr.n}건) → {verdict}");
        }
    }

    // ===== [v5.23.59] BASELINE(v5.23.53) TP/SL/maxHold sweep — 현 프로덕션 흑자전환 탐색 =====
    //   고정 후보집합(15m 양봉+1h EMA20+body/wick≥0.3) 위에서 TP×SL×maxHold 만 sweep.
    //   라이브 봇은 TP/SL 을 유저 DB GeneralSettings 에서 읽음 → 결과는 UI 설정 권고로 전달.
    private static async Task RunBaselineTpSlSweepAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  [v5.23.59] BASELINE TP/SL/maxHold SWEEP");
        Console.WriteLine("  후보: 15m 양봉 + 1h EMA20 + body/wick≥0.3 (v5.23.53, Lorentzian 제거)");
        Console.WriteLine("  lev=15x margin=$30 slippage=0.05% · 흑자 config 탐색");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "DOGEUSDT","AVAXUSDT","ARBUSDT","OPUSDT","SUIUSDT","INJUSDT","LINKUSDT","SEIUSDT","NEARUSDT","ICPUSDT",
            "DYDXUSDT","ZECUSDT","TAOUSDT","ATOMUSDT","AAVEUSDT","ADAUSDT","DOTUSDT","UNIUSDT","LTCUSDT","TRXUSDT",
            "FILUSDT","ETCUSDT","ALGOUSDT","VETUSDT","HBARUSDT","XLMUSDT","SANDUSDT","MANAUSDT","AXSUSDT","GALAUSDT",
            "CHZUSDT","ENJUSDT","GRTUSDT","COMPUSDT","MKRUSDT","CRVUSDT","SNXUSDT","SUSHIUSDT","RUNEUSDT","FETUSDT"
        };

        const decimal lev = 15m;
        const decimal margin = 30m;
        const decimal slippagePct = 0.05m;
        const int maxHoldCap = 96;   // 후보 수집 시 가장 긴 maxHold 기준 여유 확보

        Console.WriteLine($"\n[fetch 1h(3p) + 15m(6p) × {symbols.Length} alts]");

        // 후보 = (해당 심볼 k15 참조, 진입봉 j). exit 은 sweep 에서 재시뮬.
        var cand = new List<(List<IBinanceKline> k15, int j)>();
        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);
                if (k1h.Count < 250) continue;
                var k15 = await FetchKlines15mAsync(sym, 6);
                if (k15.Count < 500) continue;
                int n1h = k1h.Count, n15 = k15.Count;

                var ema20_1h = new double[n1h];
                for (int q = 20; q < n1h; q++) ema20_1h[q] = CalcEMA(k1h, q, 20);

                for (int j = 22; j < n15 - maxHoldCap - 1; j++)
                {
                    var prev15 = k15[j - 1];
                    if (prev15.ClosePrice <= prev15.OpenPrice) continue;        // 15m 양봉
                    DateTime t15 = k15[j].OpenTime;
                    int q1h = -1;
                    for (int qq = n1h - 1; qq >= 20; qq--) { if (k1h[qq].CloseTime <= t15) { q1h = qq; break; } }
                    if (q1h < 20) continue;
                    if ((double)k1h[q1h].ClosePrice <= ema20_1h[q1h]) continue;  // 1h EMA20 방향
                    decimal upperWick = prev15.HighPrice - prev15.ClosePrice;
                    if (upperWick > 0 && (prev15.ClosePrice - prev15.OpenPrice) / upperWick < 0.3m) continue;  // body/wick
                    if (k15[j].OpenPrice <= 0) continue;
                    cand.Add((k15, j));
                }
                if (fIdx % 10 == 0) Console.Write($"[{fIdx}/{symbols.Length}] ");
            }
            catch (Exception ex) { Console.WriteLine($"  {sym} fail: {ex.Message}"); }
        }

        Console.WriteLine($"\nBASELINE 후보: {cand.Count}건");
        if (cand.Count == 0) { Console.WriteLine("후보 0건 — 종료"); return; }

        decimal[] tps = { 1.0m, 1.5m, 2.0m, 2.5m, 3.0m };
        decimal[] sls = { 1.0m, 1.5m, 2.0m, 3.0m, 4.0m, 5.0m };
        int[] holds = { 48, 96 };   // 15m봉 → 12h / 24h

        var rows = new List<(decimal tp, decimal sl, int hold, int n, int w, decimal pnl)>();
        foreach (decimal tp in tps)
        foreach (decimal sl in sls)
        foreach (int hold in holds)
        {
            int n = 0, w = 0; decimal pnl = 0m;
            foreach (var (k15, j) in cand)
            {
                decimal ep = k15[j].OpenPrice;
                decimal tpPx = ep * (1m + tp / 100m), slPx = ep * (1m - sl / 100m);
                int last = Math.Min(k15.Count - 1, j + hold);
                decimal mv; bool win_;
                int q = j;
                for (; q <= last; q++)
                {
                    if (k15[q].LowPrice <= slPx) { mv = -sl - slippagePct; win_ = false; goto done; }
                    if (k15[q].HighPrice >= tpPx) { mv = tp - slippagePct; win_ = true; goto done; }
                }
                mv = (k15[last].ClosePrice - ep) / ep * 100m - slippagePct;
                win_ = k15[last].ClosePrice > ep;
                done:
                n++; if (win_) w++;
                pnl += margin * lev * mv / 100m;
            }
            rows.Add((tp, sl, hold, n, w, pnl));
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  SWEEP 결과 (PnL 내림차순) — 동일 후보 " + cand.Count + "건");
        Console.WriteLine($"  {"TP%",5} {"SL%",5} {"hold",5} {"WR%",7} {"BE_WR",7} {"PnL$",12} {"평균$",8}");
        Console.WriteLine("  " + new string('-', 62));
        foreach (var r in rows.OrderByDescending(r => r.pnl))
        {
            double wr = 100.0 * r.w / r.n;
            double beWr = (double)(r.sl + slippagePct) / (double)(r.tp + r.sl) * 100.0;  // 근사 BE WR
            decimal avg = r.pnl / r.n;
            string flag = r.pnl > 0 ? " ✅흑자" : "";
            Console.WriteLine($"  {r.tp,5:F1} {r.sl,5:F1} {r.hold,5} {wr,6:F1}% {beWr,6:F1}% {r.pnl,11:F2} {avg,7:F3}{flag}");
        }

        var best = rows.OrderByDescending(r => r.pnl).First();
        Console.WriteLine();
        Console.WriteLine("================================================================");
        if (best.pnl > 0)
        {
            // 라이브 UI 환산: ROE = price% × leverage
            decimal targetRoe = best.tp * lev;
            decimal slRoe = best.sl * lev;
            Console.WriteLine($"  ✅ 흑자 config 발견: TP {best.tp:F1}% / SL {best.sl:F1}% / hold {best.hold}봉({best.hold / 4}h)");
            Console.WriteLine($"     WR {100.0 * best.w / best.n:F1}%  PnL ${best.pnl:F2}  거래당 ${best.pnl / best.n:F3}");
            Console.WriteLine($"  ── 라이브 UI 설정 권고 (lev {lev:F0}x 기준) ──");
            Console.WriteLine($"     TargetRoe   = {targetRoe:F0}   (price TP {best.tp:F1}%)");
            Console.WriteLine($"     StopLossRoe = {slRoe:F0}   (price SL {best.sl:F1}%)");
            Console.WriteLine($"     ※ 코드 default 변경은 기존 유저 DB GeneralSettings 에 반영 안 됨 — UI 에서 직접 변경 필요");
        }
        else
        {
            Console.WriteLine($"  ❌ 전 config 손실. 최소손실: TP{best.tp:F1}/SL{best.sl:F1}/{best.hold}봉 거래당 ${best.pnl / best.n:F3}");
            Console.WriteLine("     → TP/SL 만으로 흑자전환 불가. 진입 트리거 자체 재설계 필요.");
        }
        Console.WriteLine("================================================================");
    }

    // ===== [v5.23.53] 현재 가드 스택 검증 — 사용자 지적 "진입 너무 적음" =====
    //   가드: 1h EMA20 + 15m 양봉+body/wick≥30% (v5.23.52)
    //   목적: 일 진입 빈도 측정, 사용자 기대 (5% 알트 40~50개 중 잡혀야)
    private static async Task RunCurrentGateTestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.23.53 현재 가드 스택 90일 검증");
        Console.WriteLine("  Gates: 1h EMA20 (방향) + 15m 양봉+body/wick≥30% (캔들 품질)");
        Console.WriteLine("  TP=2%/SL=5% lev=15x margin=$30 (옵션 A 추세 추종)");
        Console.WriteLine("================================================================");

        // 다양한 알트 50개 (mid-cap + 활동 큰 알트 + 일부 밈)
        var symbols = new[] {
            "DOGEUSDT","AVAXUSDT","ARBUSDT","OPUSDT","SUIUSDT","INJUSDT","LINKUSDT","SEIUSDT","NEARUSDT","ICPUSDT",
            "DYDXUSDT","ZECUSDT","TAOUSDT","ATOMUSDT","AAVEUSDT","ADAUSDT","MATICUSDT","DOTUSDT","UNIUSDT","LTCUSDT",
            "TRXUSDT","FILUSDT","ETCUSDT","ALGOUSDT","VETUSDT","ICXUSDT","FLOWUSDT","HBARUSDT","XLMUSDT","SANDUSDT",
            "MANAUSDT","AXSUSDT","GALAUSDT","CHZUSDT","ENJUSDT","BATUSDT","ZRXUSDT","GRTUSDT","COMPUSDT","MKRUSDT",
            "1INCHUSDT","CRVUSDT","SNXUSDT","SUSHIUSDT","YFIUSDT","RUNEUSDT","KSMUSDT","DYDXUSDT","FETUSDT","RNDRUSDT"
        };

        const decimal tpPct = 2.0m;
        const decimal slPct = 5.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars15m = 96;
        const decimal slippagePct = 0.05m;

        Console.WriteLine($"\n[fetch 90d 1h + 15m × {symbols.Length} alts]");

        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        var k1hMap = new Dictionary<string, List<IBinanceKline>>();
        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);
                if (k1h.Count < 250) continue;
                k1hMap[sym] = k1h;
                var k15 = await FetchKlines15mAsync(sym, 6);
                if (k15.Count < 500) continue;
                k15Map[sym] = k15;
                if (fIdx % 10 == 0) Console.Write($"[{fIdx}/{symbols.Length}] ");
            }
            catch { }
        }
        Console.WriteLine($"\nfetched: {k15Map.Count}/{symbols.Length} symbols");

        var allTrades = new List<(string sym, DateTime entryTime, decimal pnl, bool win)>();
        int totalCandidates = 0;   // 15m 봉 후보 (양봉)
        int blocked1h = 0;
        int blockedBodyWick = 0;
        int entries = 0;

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var k15 = kv.Value;
            var k1h = k1hMap[sym];
            int n = k15.Count;
            int n1h = k1h.Count;

            // 1h EMA20 pre-compute
            var ema20_1h = new double[n1h];
            for (int q = 20; q < n1h; q++) ema20_1h[q] = CalcEMA(k1h, q, 20);

            for (int j = 22; j < n - maxHoldBars15m - 1; j++)
            {
                var prev15 = k15[j - 1];   // 직전 마감 15m 봉
                bool isBullish = prev15.ClosePrice > prev15.OpenPrice;
                if (!isBullish) continue;
                totalCandidates++;

                // === 1h EMA20 ===
                DateTime t15 = k15[j].OpenTime;
                int q1h = -1;
                for (int qq = n1h - 1; qq >= 20; qq--) { if (k1h[qq].CloseTime <= t15) { q1h = qq; break; } }
                if (q1h < 20) continue;
                bool h1Up = (double)k1h[q1h].ClosePrice > ema20_1h[q1h];
                if (!h1Up) { blocked1h++; continue; }

                // === 15m body/wick ===
                decimal body = prev15.ClosePrice - prev15.OpenPrice;
                decimal upperWick = prev15.HighPrice - prev15.ClosePrice;
                if (upperWick > 0)
                {
                    decimal ratio = body / upperWick;
                    if (ratio < 0.3m) { blockedBodyWick++; continue; }
                }

                // 진입가: 다음 봉 시가
                decimal entryPrice = k15[j].OpenPrice;
                if (entryPrice <= 0) continue;

                // Exit simulate
                decimal tpPx = entryPrice * (1m + tpPct / 100m);
                decimal slPx = entryPrice * (1m - slPct / 100m);
                decimal exitPrice = 0m;
                bool win_ = false;
                bool exited = false;
                for (int q = j; q <= Math.Min(n - 1, j + maxHoldBars15m); q++)
                {
                    if (k15[q].LowPrice <= slPx) { exitPrice = slPx; win_ = false; exited = true; break; }
                    if (k15[q].HighPrice >= tpPx) { exitPrice = tpPx; win_ = true; exited = true; break; }
                }
                if (!exited) { int last = Math.Min(n - 1, j + maxHoldBars15m); exitPrice = k15[last].ClosePrice; win_ = exitPrice > entryPrice; }

                decimal pmove = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                decimal pnl = margin * lev * pmove / 100m;

                allTrades.Add((sym, k15[j].OpenTime, pnl, win_));
                entries++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  결과 ({k15Map.Count} 알트 × 90일)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"  15m 양봉 후보: {totalCandidates}건");
        Console.WriteLine($"  1h EMA20 차단:     {blocked1h,8}건  ({blocked1h * 100.0 / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  15m body/wick 차단:{blockedBodyWick,8}건  ({blockedBodyWick * 100.0 / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ==> 진입: {entries}건 ({entries * 100.0 / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  일평균 진입: {entries / 90.0:F1}건 (90일)");
        Console.WriteLine();

        int wins = allTrades.Count(t => t.win);
        decimal sumPnl = allTrades.Sum(t => t.pnl);
        decimal avgWin = allTrades.Where(t => t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
        decimal avgLoss = allTrades.Where(t => !t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
        Console.WriteLine($"  WR:     {wins * 100.0 / Math.Max(1, entries):F2}% ({wins}/{entries})");
        Console.WriteLine($"  Sum PnL: ${sumPnl:F2} (일평균 ${sumPnl / 90m:F2})");
        Console.WriteLine($"  AvgWin: ${avgWin:F2}  AvgLoss: ${avgLoss:F2}");

        Console.WriteLine();
        Console.WriteLine("심볼별 진입 (TOP 20):");
        var perSym = allTrades.GroupBy(t => t.sym)
            .Select(g => new { sym = g.Key, n = g.Count(), wr = g.Count(x => x.win) * 100.0 / g.Count(), pnl = g.Sum(x => x.pnl) })
            .OrderByDescending(x => x.pnl)
            .Take(20);
        foreach (var s in perSym)
            Console.WriteLine($"  {s.sym,-12} N={s.n,4} WR={s.wr,6:F2}% PnL=${s.pnl,8:F2}");
    }

    // ===== [my-strategy] 「흐름 따라잡기」 전략 백테스트 =====
    //   4h regime (BTC) + 1h trend (alt) + 15m pullback trigger + tight SL / wide TP
    //   현재 봇의 7-가드 + KNN 와 비교
    private static async Task RunMyStrategyTestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  「흐름 따라잡기」 전략 백테스트 — 90일");
        Console.WriteLine("  필터: BTC 4h regime → alt 1h trend → 15m pullback trigger");
        Console.WriteLine("  TP/SL 4가지 조합 비교 (lev=10, margin=$30)");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "DOGEUSDT", "AVAXUSDT", "ARBUSDT", "OPUSDT", "SUIUSDT",
            "INJUSDT", "LINKUSDT", "SEIUSDT", "NEARUSDT", "ICPUSDT",
            "DYDXUSDT", "ZECUSDT", "TAOUSDT", "ATOMUSDT", "AAVEUSDT"
        };

        const decimal lev = 10m;
        const decimal margin = 30m;
        const int maxHoldBars15m = 16;     // 4시간 = 15m × 16 (타임 스톱)
        const decimal slippagePct = 0.05m;

        // TP/SL 조합 (가격 %)
        var combos = new (decimal tp, decimal sl, string label)[] {
            (1.5m, 1.0m, "TP1.5/SL1.0 (R:R 1.5)"),
            (3.0m, 1.0m, "TP3.0/SL1.0 (R:R 3.0)"),
            (2.0m, 1.5m, "TP2.0/SL1.5 (R:R 1.33)"),
            (2.0m, 3.0m, "TP2.0/SL3.0 (R:R 0.67 - 현재)")
        };

        Console.WriteLine($"\n[fetch BTC 4h + alt 1h + 15m × {symbols.Length}]");

        // BTC 4h regime
        Console.Write("  BTC 4h ");
        var btc4h = await FetchKlines4hAsync("BTCUSDT", 2);
        Console.WriteLine($"ok ({btc4h.Count} bars)");
        var btcEma50_4h = new double[btc4h.Count];
        for (int j = 50; j < btc4h.Count; j++) btcEma50_4h[j] = CalcEMA(btc4h, j, 50);

        // alt 1h + 15m
        var k1hMap = new Dictionary<string, List<IBinanceKline>>();
        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            Console.Write($"  [{fIdx}/{symbols.Length}] {sym} 1h ");
            try
            {
                var k1h = await FetchKlines1hAsync(sym, 3);
                if (k1h.Count < 250) { Console.WriteLine($"skip"); continue; }
                k1hMap[sym] = k1h;
                Console.Write($"({k1h.Count}) | 15m ");
                var k15 = await FetchKlines15mAsync(sym, 6);
                if (k15.Count < 500) { Console.WriteLine($"skip"); continue; }
                k15Map[sym] = k15;
                Console.WriteLine($"({k15.Count})");
            }
            catch (Exception ex) { Console.WriteLine($"fail: {ex.Message}"); }
        }

        // 진입 후보 추출 (TP/SL 무관)
        var allEntries = new List<(string sym, int j15, decimal entryPrice)>();
        int regimeBlocks = 0, trendBlocks = 0, triggerBlocks = 0;

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var kl = kv.Value;
            int n = kl.Count;
            if (!k1hMap.TryGetValue(sym, out var k1h)) continue;
            int n1h = k1h.Count;

            // Pre-compute 1h indicators
            var ema50_1h = new double[n1h];
            var ema200_1h = new double[n1h];
            var adx_1h = new double[n1h];
            for (int q = 200; q < n1h; q++)
            {
                ema50_1h[q] = CalcEMA(k1h, q, 50);
                ema200_1h[q] = CalcEMA(k1h, q, 200);
                adx_1h[q] = CalcADX_idx(k1h, q, 14);
            }

            // Pre-compute 15m indicators
            var ema20_15m = new double[n];
            var rsi14_15m = new double[n];
            var volMa20_15m = new double[n];
            for (int j = 20; j < n; j++)
            {
                ema20_15m[j] = CalcEMA(kl, j, 20);
                // RSI(14)
                double g = 0, l = 0;
                for (int q = j - 13; q <= j; q++)
                {
                    double d = (double)(kl[q].ClosePrice - kl[q - 1].ClosePrice);
                    if (d > 0) g += d; else l -= d;
                }
                rsi14_15m[j] = l < 1e-12 ? 100.0 : 100.0 - (100.0 / (1.0 + (g / 14.0) / (l / 14.0)));
                // Volume MA20
                decimal vsum = 0;
                for (int q = j - 19; q <= j; q++) vsum += kl[q].Volume;
                volMa20_15m[j] = (double)(vsum / 20m);
            }

            int symEntries = 0;
            for (int j = 250; j < n - maxHoldBars15m - 1; j++)
            {
                DateTime t15 = kl[j].OpenTime;

                // === 1. BTC 4h Regime 게이트 ===
                int q4h = -1;
                for (int qq = btc4h.Count - 1; qq >= 50; qq--)
                {
                    if (btc4h[qq].CloseTime <= t15) { q4h = qq; break; }
                }
                if (q4h < 50) continue;
                bool btcRegimeOk = (double)btc4h[q4h].ClosePrice > btcEma50_4h[q4h];
                if (!btcRegimeOk) { regimeBlocks++; continue; }

                // === 2. 1h Trend 게이트 ===
                int q1h = -1;
                for (int qq = n1h - 1; qq >= 200; qq--)
                {
                    if (k1h[qq].CloseTime <= t15) { q1h = qq; break; }
                }
                if (q1h < 200) continue;
                bool trendOk = (double)k1h[q1h].ClosePrice > ema50_1h[q1h]
                            && ema50_1h[q1h] > ema200_1h[q1h]
                            && adx_1h[q1h] > 25.0;
                if (!trendOk) { trendBlocks++; continue; }

                // === 3. 15m Pullback Trigger ===
                // 조건: 5봉 내 EMA20 터치 + 직전 RSI dip (≤45) + 현재 RSI 회복 (>45) + 양봉 + 거래량 ≥ MA20 × 1.2
                bool emaTouched = false;
                for (int q = j - 5; q < j; q++)
                {
                    if (q < 20) continue;
                    decimal e = (decimal)ema20_15m[q];
                    decimal touchTol = e * 0.002m;   // ±0.2%
                    if (kl[q].LowPrice <= e + touchTol && kl[q].HighPrice >= e - touchTol)
                    {
                        emaTouched = true; break;
                    }
                }
                if (!emaTouched) { triggerBlocks++; continue; }

                // 직전 RSI 가 45 이하였다가 현재 45 이상 복귀
                bool rsiRecovered = false;
                for (int q = j - 5; q < j; q++)
                {
                    if (rsi14_15m[q] <= 45.0 && rsi14_15m[j] > 45.0)
                    {
                        rsiRecovered = true; break;
                    }
                }
                if (!rsiRecovered) { triggerBlocks++; continue; }

                // 현재 봉 양봉
                if (kl[j].ClosePrice <= kl[j].OpenPrice) { triggerBlocks++; continue; }

                // 거래량 ≥ MA20 × 1.2
                if ((double)kl[j].Volume < volMa20_15m[j] * 1.2) { triggerBlocks++; continue; }

                // 진입가: 다음 15m 봉 시가
                decimal entryPrice = kl[j + 1].OpenPrice;
                allEntries.Add((sym, j, entryPrice));
                symEntries++;
            }
            Console.WriteLine($"  {sym,-12} 진입 후보 {symEntries,4}건");
        }

        Console.WriteLine();
        Console.WriteLine($"  Total 진입: {allEntries.Count}건");
        Console.WriteLine($"  Regime 차단: {regimeBlocks}, Trend 차단: {trendBlocks}, Trigger 차단: {triggerBlocks}");

        // TP/SL 4가지 시뮬레이션
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  TP/SL 비교 (진입 {allEntries.Count}건 × 4조합)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"Combo",-30} {"N",6} {"Win",6} {"WR%",7} {"PnL($)",10} {"AvgWin",8} {"AvgLoss",8} {"AvgHold",8}");
        Console.WriteLine(new string('-', 90));

        foreach (var (tp, sl, label) in combos)
        {
            int n = 0, wins = 0;
            decimal pnlSum = 0m, winSum = 0m, lossSum = 0m;
            int winCnt = 0, lossCnt = 0, holdSum = 0;

            foreach (var (sym, j, entryPrice) in allEntries)
            {
                var kl = k15Map[sym];
                int nKl = kl.Count;
                decimal tpPx = entryPrice * (1m + tp / 100m);
                decimal slPx = entryPrice * (1m - sl / 100m);

                decimal exitPrice = 0m;
                int holdBars = 0;
                bool win_ = false;
                bool exited = false;
                for (int q = j + 1; q <= Math.Min(nKl - 1, j + maxHoldBars15m); q++)
                {
                    if (kl[q].LowPrice <= slPx) { exitPrice = slPx; holdBars = q - j; win_ = false; exited = true; break; }
                    if (kl[q].HighPrice >= tpPx) { exitPrice = tpPx; holdBars = q - j; win_ = true; exited = true; break; }
                }
                if (!exited)
                {
                    int last = Math.Min(nKl - 1, j + maxHoldBars15m);
                    exitPrice = kl[last].ClosePrice;
                    holdBars = last - j;
                    win_ = exitPrice > entryPrice;
                }

                decimal pmove = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                decimal pnl = margin * lev * pmove / 100m;

                n++;
                if (win_) { wins++; winSum += pnl; winCnt++; }
                else { lossSum += pnl; lossCnt++; }
                pnlSum += pnl;
                holdSum += holdBars;
            }

            double wr = n > 0 ? wins * 100.0 / n : 0;
            decimal avgWin = winCnt > 0 ? winSum / winCnt : 0m;
            decimal avgLoss = lossCnt > 0 ? lossSum / lossCnt : 0m;
            double avgHold = n > 0 ? holdSum * 1.0 / n : 0;
            Console.WriteLine($"{label,-30} {n,6} {wins,6} {wr,6:F2}% {pnlSum,10:F2} {avgWin,8:F2} {avgLoss,8:F2} {avgHold,8:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("[참고] 현재 봇 (Lorentzian + 7가드 + TP2%/SL3%) 같은 90일 데이터: +$413 (426건, WR 64%)");
    }

    // ===== [v5.23.37 검증] 1h 방향 필터 효과 — "방향 1h, 속도 5/15m" 원칙 =====
    //   1h close > 1h EMA200 + 1h ADX > 20 + 1h regime > 0 모두 통과 시만 진입
    //   가드 ON/OFF 90일 비교
    private static async Task RunHourlyDirectionTestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.23.37 1h 방향 필터 효과 검증 — 90일 백테스트");
        Console.WriteLine("  원칙: 방향 1h, 속도 15m");
        Console.WriteLine("  TP=2%/SL=3%, v5.23.32 PULLBACK_QUALITY 적용, 가드 ON/OFF 비교");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "DOGEUSDT", "AVAXUSDT", "ARBUSDT", "OPUSDT", "SUIUSDT",
            "INJUSDT", "LINKUSDT", "SEIUSDT", "NEARUSDT", "ICPUSDT",
            "DYDXUSDT", "ZECUSDT", "TAOUSDT", "ATOMUSDT", "AAVEUSDT"
        };

        const float guardWinRate = 0.70f;
        const decimal tpPct = 2.0m;
        const decimal slPct = 3.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars = 96;
        const decimal slippagePct = 0.05m;

        Console.WriteLine($"\n[fetch 90d 15m + 1h × {symbols.Length} alts]");
        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        var k1hMap = new Dictionary<string, List<IBinanceKline>>();
        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            Console.Write($"[{fIdx}/{symbols.Length}] {sym} 15m ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, 6);
                if (kl.Count < 500) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                k15Map[sym] = kl;
                Console.Write($"ok ({kl.Count}) | 1h ");
                var k1h = await FetchKlines1hAsync(sym, 3);   // 90d × 24h = 2160 bars, 3 pages = 4500
                k1hMap[sym] = k1h;
                Console.WriteLine($"ok ({k1h.Count})");
            }
            catch (Exception ex) { Console.WriteLine($"fail: {ex.Message}"); }
        }

        var trades_offGate = new List<(string sym, decimal pnl, bool win)>();
        var trades_onGate = new List<(string sym, decimal pnl, bool win)>();
        int hourlyBlocks = 0;

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var kl = kv.Value;
            int n = kl.Count;
            if (!k1hMap.TryGetValue(sym, out var k1h) || k1h.Count < 250) continue;
            int n1h = k1h.Count;

            var feats = new float[n][];
            var labels = new int[n];
            for (int j = 60; j < n; j++)
            {
                int wStart = Math.Max(0, j - 499);
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = LorentzianFeatures.Extract(win)!;
            }
            for (int j = 0; j < n - 4; j++)
            {
                decimal fut = kl[j + 4].ClosePrice;
                decimal nowC = kl[j].ClosePrice;
                labels[j] = fut > nowC ? 1 : (fut < nowC ? -1 : 0);
            }

            var atr1 = new double[n];
            var atr10 = new double[n];
            var adx = new double[n];
            var ema200 = new double[n];
            var sma200 = new double[n];
            var regime = new double[n];
            var nwk = new double[n];
            for (int j = 200; j < n; j++)
            {
                atr1[j] = CalcTR(kl, j);
                atr10[j] = CalcATR(kl, j, 10);
                adx[j] = CalcADX_idx(kl, j, 14);
                ema200[j] = CalcEMA(kl, j, 200);
                sma200[j] = CalcSMA(kl, j, 200);
                regime[j] = CalcRegimeSlope(kl, j);
                nwk[j] = CalcNWKernel(kl, j);
            }

            // Pre-compute 1h indicators per 1h bar
            var ema200_1h = new double[n1h];
            var adx_1h = new double[n1h];
            var regime_1h = new double[n1h];
            for (int q = 200; q < n1h; q++)
            {
                ema200_1h[q] = CalcEMA(k1h, q, 200);
                adx_1h[q] = CalcADX_idx(k1h, q, 14);
                regime_1h[q] = CalcRegimeSlope(k1h, q);
            }

            var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            var sig = new int[n];
            var sigWr = new float[n];
            for (int j = 65; j < n; j++)
            {
                int sIdx = j - 5;
                if (feats[sIdx] != null) engine.AddSample(feats[sIdx], labels[sIdx]);
                if (feats[j] == null) continue;
                var p = engine.Predict(feats[j]);
                if (!p.IsReady || p.K == 0) continue;
                sig[j] = p.Prediction > 0 ? 1 : (p.Prediction < 0 ? -1 : 0);
                sigWr[j] = p.K > 0 ? (float)p.PositiveVotes / p.K : 0f;
            }

            int symEntries = 0, symHourlyBlocked = 0;

            for (int j = 250; j < n - maxHoldBars - 1; j++)
            {
                if (sig[j] != 1 || sigWr[j] < guardWinRate) continue;
                if (atr1[j] <= atr10[j]) continue;
                if (regime[j] <= -0.1) continue;
                if (adx[j] <= 20.0) continue;
                if ((double)kl[j].ClosePrice <= ema200[j]) continue;
                if ((double)kl[j].ClosePrice <= sma200[j]) continue;
                if (j < 2 || nwk[j] <= nwk[j - 2]) continue;

                decimal entryPrice = kl[j + 1].OpenPrice;

                // PULLBACK_QUALITY (v5.23.32)
                int scanStart = Math.Max(0, j - 22);
                int scanEnd = j - 2;
                bool pbqAllow = false;
                if (scanEnd - scanStart >= 5)
                {
                    int hiIdx = scanStart;
                    decimal hiPrice = kl[scanStart].HighPrice;
                    for (int i = scanStart + 1; i <= scanEnd; i++) if (kl[i].HighPrice > hiPrice) { hiPrice = kl[i].HighPrice; hiIdx = i; }
                    int loIdx = hiIdx; decimal loPrice = hiPrice;
                    for (int i = hiIdx; i <= scanEnd; i++) if (kl[i].LowPrice < loPrice) { loPrice = kl[i].LowPrice; loIdx = i; }
                    decimal pullbackPct = hiPrice > 0 ? (hiPrice - loPrice) / hiPrice * 100m : 0m;
                    bool c1 = pullbackPct >= 1.5m && loIdx > hiIdx;
                    if (c1)
                    {
                        decimal mid = (hiPrice + loPrice) / 2m;
                        bool c2 = entryPrice >= mid;
                        decimal e20 = (decimal)CalcEMA(kl, j, 20);
                        decimal dev = e20 > 0 ? Math.Abs(entryPrice - e20) / e20 * 100m : 0m;
                        bool c3 = e20 > 0 && dev <= 2.5m;
                        bool c4 = false;
                        if (loIdx > hiIdx)
                        {
                            decimal rv = 0; int rc = 0;
                            for (int i = scanEnd - 2; i <= scanEnd; i++) if (i >= 0) { rv += kl[i].Volume; rc++; }
                            decimal pv = 0; int pc = 0;
                            for (int i = hiIdx; i <= loIdx; i++) { pv += kl[i].Volume; pc++; }
                            if (rc > 0 && pc > 0) { c4 = pv <= 0 || rv / rc >= pv / pc * 0.8m; }
                        }
                        int sub = (c2 ? 1 : 0) + (c3 ? 1 : 0) + (c4 ? 1 : 0);
                        pbqAllow = sub >= 2;
                    }
                }
                if (!pbqAllow) continue;

                // === 1h 방향 필터 ===
                // 15m 봉 j 의 OpenTime 으로 매칭되는 1h 봉 인덱스 찾기
                DateTime t15 = kl[j].OpenTime;
                int q1h = -1;
                for (int qq = n1h - 1; qq >= 200; qq--)
                {
                    if (k1h[qq].CloseTime <= t15) { q1h = qq; break; }
                }
                bool hourlyAllow = false;
                if (q1h >= 200)
                {
                    bool h_ema = (double)k1h[q1h].ClosePrice > ema200_1h[q1h];
                    bool h_adx = adx_1h[q1h] > 20.0;
                    bool h_reg = regime_1h[q1h] > 0.0;
                    hourlyAllow = h_ema && h_adx && h_reg;
                }

                // Simulate exit
                decimal tpPx = entryPrice * (1m + tpPct / 100m);
                decimal slPx = entryPrice * (1m - slPct / 100m);
                decimal exitPrice = 0m;
                bool win_ = false;
                bool exited = false;
                for (int q = j + 1; q <= Math.Min(n - 1, j + maxHoldBars); q++)
                {
                    if (kl[q].LowPrice <= slPx) { exitPrice = slPx; win_ = false; exited = true; break; }
                    if (kl[q].HighPrice >= tpPx) { exitPrice = tpPx; win_ = true; exited = true; break; }
                }
                if (!exited) { int last = Math.Min(n - 1, j + maxHoldBars); exitPrice = kl[last].ClosePrice; win_ = exitPrice > entryPrice; }
                decimal pmove = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                decimal pnl = margin * lev * pmove / 100m;

                trades_offGate.Add((sym, pnl, win_));
                symEntries++;
                if (hourlyAllow) trades_onGate.Add((sym, pnl, win_));
                else symHourlyBlocked++;
            }
            hourlyBlocks += symHourlyBlocked;
            Console.WriteLine($"  {sym,-12} 진입 {symEntries,4}건  1h차단 {symHourlyBlocked,4}건");
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  90일 결과 — 1h 방향 필터 ON/OFF 비교");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"Mode",-30} {"N",6} {"Wins",6} {"WR%",7} {"PnL($)",10} {"AvgWin",8} {"AvgLoss",8}");
        Console.WriteLine(new string('-', 80));
        PrintMicroAltMode("Gate OFF (15m only)", trades_offGate);
        PrintMicroAltMode("Gate ON (1h direction)", trades_onGate);

        Console.WriteLine();
        Console.WriteLine($"1h 차단: {hourlyBlocks}건 ({hourlyBlocks * 100.0 / Math.Max(1, trades_offGate.Count):F1}%)");
        decimal blockedPnl = trades_offGate.Sum(t => t.pnl) - trades_onGate.Sum(t => t.pnl);
        int blockedWin = trades_offGate.Count(t => t.win) - trades_onGate.Count(t => t.win);
        int blockedN = trades_offGate.Count - trades_onGate.Count;
        if (blockedN > 0)
        {
            Console.WriteLine($"막힌 진입 {blockedN}건 — win {blockedWin}건 (WR {blockedWin * 100.0 / blockedN:F2}%) PnL ${blockedPnl:F2}");
            Console.WriteLine("  → PnL 음수 = 가드 효과 ↑ / 양수 = 가드가 흑자 진입까지 차단");
        }
    }

    // ===== [v5.23.34] MICRO_ALT_VOLUME 가드 검증 — 마이너 알트 + 미드캡 비교 =====
    //   라이브 손실 패턴 (SANTOS/GOAT/INX 0% 승률) 재현
    //   가드 ON/OFF 결과 비교 — 24h vol < $20M 차단 효과
    private static async Task RunMicroAltGateTestAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.23.34 MICRO_ALT_VOLUME 가드 검증 — 90일 백테스트");
        Console.WriteLine("  $20M 24h 임계 ON/OFF 비교, TP=2%/SL=3%, v5.23.32 PULLBACK_QUALITY 적용");
        Console.WriteLine("================================================================");

        var midCapAlts = new[] {
            "DOGEUSDT", "AVAXUSDT", "ARBUSDT", "OPUSDT", "SUIUSDT",
            "LINKUSDT", "NEARUSDT", "ICPUSDT", "ZECUSDT", "AAVEUSDT"
        };
        var microCapAlts = new[] {
            "1000FLOKIUSDT", "VIRTUALUSDT", "GOATUSDT", "SANTOSUSDT",
            "INXUSDT", "FLOCKUSDT", "DOGSUSDT", "NIGHTUSDT"
        };
        var allSymbols = midCapAlts.Concat(microCapAlts).ToArray();

        const float guardWinRate = 0.70f;
        const decimal tpPct = 2.0m;
        const decimal slPct = 3.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars = 96;
        const decimal slippagePct = 0.05m;
        const decimal microAltThresholdUsdt = 20_000_000m;   // $20M

        Console.WriteLine($"\n[fetch 90d 15m × {allSymbols.Length} symbols (mid:{midCapAlts.Length} + micro:{microCapAlts.Length})]");
        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        var avg24hQuoteVol = new Dictionary<string, decimal>();
        int fIdx = 0;
        foreach (var sym in allSymbols)
        {
            fIdx++;
            Console.Write($"[{fIdx}/{allSymbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, 6);
                if (kl.Count < 500) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                k15Map[sym] = kl;

                // 90일치 평균 24h quote volume (96봉 = 24h × 15m)
                int barsPerDay = 96;
                decimal totalVol = 0m;
                int days = 0;
                for (int d = 0; d * barsPerDay + barsPerDay <= kl.Count; d++)
                {
                    decimal dayVol = 0m;
                    for (int q = d * barsPerDay; q < (d + 1) * barsPerDay; q++)
                    {
                        // QuoteVolume = price × Volume 근사
                        dayVol += kl[q].Volume * kl[q].ClosePrice;
                    }
                    totalVol += dayVol;
                    days++;
                }
                decimal avgDailyQuoteVol = days > 0 ? totalVol / days : 0m;
                avg24hQuoteVol[sym] = avgDailyQuoteVol;
                Console.WriteLine($"ok ({kl.Count}) | 평균 24h vol ${avgDailyQuoteVol / 1_000_000m:F1}M");
            }
            catch (Exception ex) { Console.WriteLine($"fail: {ex.Message}"); }
        }

        Console.WriteLine();
        Console.WriteLine("==== 심볼별 평균 24h 거래량 ====");
        foreach (var (sym, vol) in avg24hQuoteVol.OrderBy(kv => kv.Value))
        {
            string mark = vol < microAltThresholdUsdt ? "❌ 차단" : "✅ 통과";
            string cat = midCapAlts.Contains(sym) ? "mid" : "micro";
            Console.WriteLine($"  [{cat,-5}] {sym,-16} ${vol / 1_000_000m,7:F1}M  {mark}");
        }

        // For each symbol, run Lorentzian + PULLBACK_QUALITY + simulate trades
        // Track entries with/without MICRO_ALT_VOLUME gate
        var trades_offGate = new List<(string sym, decimal pnl, bool win)>();
        var trades_onGate = new List<(string sym, decimal pnl, bool win)>();
        int microBlocks = 0;

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var kl = kv.Value;
            int n = kl.Count;
            decimal symVol24h = avg24hQuoteVol.GetValueOrDefault(sym, 0m);
            bool isMicro = symVol24h > 0 && symVol24h < microAltThresholdUsdt;

            var feats = new float[n][];
            var labels = new int[n];
            for (int j = 60; j < n; j++)
            {
                int wStart = Math.Max(0, j - 499);
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = LorentzianFeatures.Extract(win)!;
            }
            for (int j = 0; j < n - 4; j++)
            {
                decimal fut = kl[j + 4].ClosePrice;
                decimal nowC = kl[j].ClosePrice;
                labels[j] = fut > nowC ? 1 : (fut < nowC ? -1 : 0);
            }

            var atr1 = new double[n];
            var atr10 = new double[n];
            var adx = new double[n];
            var ema200 = new double[n];
            var sma200 = new double[n];
            var regime = new double[n];
            var nwk = new double[n];
            for (int j = 200; j < n; j++)
            {
                atr1[j] = CalcTR(kl, j);
                atr10[j] = CalcATR(kl, j, 10);
                adx[j] = CalcADX_idx(kl, j, 14);
                ema200[j] = CalcEMA(kl, j, 200);
                sma200[j] = CalcSMA(kl, j, 200);
                regime[j] = CalcRegimeSlope(kl, j);
                nwk[j] = CalcNWKernel(kl, j);
            }

            var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            var sig = new int[n];
            var sigWr = new float[n];
            for (int j = 65; j < n; j++)
            {
                int sIdx = j - 5;
                if (feats[sIdx] != null) engine.AddSample(feats[sIdx], labels[sIdx]);
                if (feats[j] == null) continue;
                var p = engine.Predict(feats[j]);
                if (!p.IsReady || p.K == 0) continue;
                sig[j] = p.Prediction > 0 ? 1 : (p.Prediction < 0 ? -1 : 0);
                sigWr[j] = p.K > 0 ? (float)p.PositiveVotes / p.K : 0f;
            }

            int symEntries = 0;
            int symMicroBlocked = 0;

            for (int j = 250; j < n - maxHoldBars - 1; j++)
            {
                if (sig[j] != 1 || sigWr[j] < guardWinRate) continue;
                if (atr1[j] <= atr10[j]) continue;
                if (regime[j] <= -0.1) continue;
                if (adx[j] <= 20.0) continue;
                if ((double)kl[j].ClosePrice <= ema200[j]) continue;
                if ((double)kl[j].ClosePrice <= sma200[j]) continue;
                if (j < 2 || nwk[j] <= nwk[j - 2]) continue;

                decimal entryPrice = kl[j + 1].OpenPrice;

                // === v5.23.32 PULLBACK_QUALITY ===
                int scanStart = Math.Max(0, j - 22);
                int scanEnd = j - 2;
                bool gateAllow = false;
                if (scanEnd - scanStart >= 5)
                {
                    int hiIdx = scanStart;
                    decimal hiPrice = kl[scanStart].HighPrice;
                    for (int i = scanStart + 1; i <= scanEnd; i++)
                    {
                        if (kl[i].HighPrice > hiPrice) { hiPrice = kl[i].HighPrice; hiIdx = i; }
                    }
                    int loIdx = hiIdx;
                    decimal loPrice = hiPrice;
                    for (int i = hiIdx; i <= scanEnd; i++)
                    {
                        if (kl[i].LowPrice < loPrice) { loPrice = kl[i].LowPrice; loIdx = i; }
                    }
                    decimal pullbackPct = hiPrice > 0 ? (hiPrice - loPrice) / hiPrice * 100m : 0m;
                    bool c1Pullback = pullbackPct >= 1.5m && loIdx > hiIdx;
                    if (c1Pullback)
                    {
                        decimal midPoint = (hiPrice + loPrice) / 2m;
                        bool c2Recovery = entryPrice >= midPoint;
                        decimal ema20_15m = (decimal)CalcEMA(kl, j, 20);
                        decimal emaDevPct = ema20_15m > 0 ? Math.Abs(entryPrice - ema20_15m) / ema20_15m * 100m : 0m;
                        bool c3EmaOk = ema20_15m > 0 && emaDevPct <= 2.5m;
                        bool c4VolOk = false;
                        decimal recentVolSum = 0m; int recentCnt = 0;
                        for (int i = scanEnd - 2; i <= scanEnd; i++)
                        {
                            if (i >= 0) { recentVolSum += kl[i].Volume; recentCnt++; }
                        }
                        decimal pullVolSum = 0m; int pullCnt = 0;
                        for (int i = hiIdx; i <= loIdx; i++)
                        {
                            pullVolSum += kl[i].Volume; pullCnt++;
                        }
                        if (recentCnt > 0 && pullCnt > 0)
                        {
                            decimal recentAvg = recentVolSum / recentCnt;
                            decimal pullAvg = pullVolSum / pullCnt;
                            c4VolOk = pullAvg <= 0m || recentAvg >= pullAvg * 0.8m;
                        }
                        int subPassCnt = (c2Recovery ? 1 : 0) + (c3EmaOk ? 1 : 0) + (c4VolOk ? 1 : 0);
                        gateAllow = subPassCnt >= 2;
                    }
                }
                if (!gateAllow) continue;

                // Simulate exit (TP=2%, SL=3%)
                decimal tpPx = entryPrice * (1m + tpPct / 100m);
                decimal slPx = entryPrice * (1m - slPct / 100m);
                decimal exitPrice = 0m;
                bool win_ = false;
                bool exited = false;
                for (int q = j + 1; q <= Math.Min(n - 1, j + maxHoldBars); q++)
                {
                    if (kl[q].LowPrice <= slPx) { exitPrice = slPx; win_ = false; exited = true; break; }
                    if (kl[q].HighPrice >= tpPx) { exitPrice = tpPx; win_ = true; exited = true; break; }
                }
                if (!exited)
                {
                    int lastQ = Math.Min(n - 1, j + maxHoldBars);
                    exitPrice = kl[lastQ].ClosePrice;
                    win_ = exitPrice > entryPrice;
                }
                decimal priceMovePct = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                decimal pnl = margin * lev * priceMovePct / 100m;

                // OFF gate: 모든 진입 포함
                trades_offGate.Add((sym, pnl, win_));
                symEntries++;

                // ON gate: micro-alt 차단
                if (isMicro)
                {
                    symMicroBlocked++;
                }
                else
                {
                    trades_onGate.Add((sym, pnl, win_));
                }
            }

            microBlocks += symMicroBlocked;
            string symVolMark = isMicro ? "MICRO" : "MID  ";
            Console.WriteLine($"  [{symVolMark}] {sym,-16} 진입 {symEntries,4}건  micro차단 {symMicroBlocked,4}건");
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  90일 결과 — MICRO_ALT_VOLUME 가드 ON/OFF 비교");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"Mode",-30} {"N",6} {"Wins",6} {"WR%",7} {"PnL($)",10} {"AvgWin",8} {"AvgLoss",8}");
        Console.WriteLine(new string('-', 80));
        PrintMicroAltMode("Gate OFF (전부 포함)", trades_offGate);
        PrintMicroAltMode("Gate ON (micro 차단)", trades_onGate);

        // micro-only PnL 분리
        var trades_microOnly = trades_offGate.Where(t => avg24hQuoteVol.GetValueOrDefault(t.sym, 0m) < microAltThresholdUsdt).ToList();
        Console.WriteLine();
        if (trades_microOnly.Count > 0)
        {
            Console.WriteLine($"==== micro-alt 만 분리 (가드가 막은 진입) ====");
            PrintMicroAltMode("micro-alt only", trades_microOnly);
            Console.WriteLine();
            Console.WriteLine($"  → 가드가 정확히 손실 케이스 차단했는지: PnL이 음수면 가드 효과 ↑");
        }
        else
        {
            Console.WriteLine("micro-alt 진입 신호 0건 — 백테스트 셋이 너무 클린합니다");
        }
    }

    private static void PrintMicroAltMode(string name, List<(string sym, decimal pnl, bool win)> trades)
    {
        int n = trades.Count;
        int wins = trades.Count(t => t.win);
        decimal pnl = trades.Sum(t => t.pnl);
        decimal avgWin = trades.Where(t => t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
        decimal avgLoss = trades.Where(t => !t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
        double wr = n > 0 ? wins * 100.0 / n : 0;
        Console.WriteLine($"{name,-30} {n,6} {wins,6} {wr,6:F2}% {pnl,10:F2} {avgWin,8:F2} {avgLoss,8:F2}");
    }

    // ===== [v5.23.32+] TP/SL 스윕 — v5.23.32 가드 + R:R 변경 90일 비교 =====
    //   prior 결과: WR 72%인데 -$212 (TP1%/SL3% R:R 1:3 → break-even WR 75% 필요)
    //   목표: 흑자 전환되는 TP/SL 조합 탐색
    private static async Task RunLorentzianAltSweepAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  LORENTZIAN Alt 90일 TP/SL 스윕 — v5.23.32 가드 적용");
        Console.WriteLine("  목표: WR 72% 환경에서 흑자 전환되는 R:R 탐색");
        Console.WriteLine("  15알트, 15× lev, margin $30/trade, maxHold 96(24h)");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "DOGEUSDT", "AVAXUSDT", "ARBUSDT", "OPUSDT", "SUIUSDT",
            "INJUSDT", "LINKUSDT", "SEIUSDT", "NEARUSDT", "ICPUSDT",
            "DYDXUSDT", "ZECUSDT", "TAOUSDT", "ATOMUSDT", "AAVEUSDT"
        };

        // (TP%, SL%) 조합
        var combos = new[] {
            (1.0m, 1.0m),    // R:R 1:1, BE-WR 50%
            (1.0m, 1.5m),    // R:R 1:1.5, BE-WR 60% ← 옵션1
            (1.0m, 2.0m),    // R:R 1:2, BE-WR 67%
            (1.5m, 2.0m),    // R:R 1:1.33, BE-WR 57%
            (1.5m, 2.5m),    // R:R 1:1.67, BE-WR 63%
            (2.0m, 2.0m),    // R:R 1:1, BE-WR 50%, 더 큰 TP
            (2.0m, 3.0m),    // R:R 1:1.5, BE-WR 60% ← 옵션2
            (1.0m, 3.0m),    // 현재 production setting (baseline)
        };

        const float guardWinRate = 0.70f;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars = 96;
        const decimal slippagePct = 0.05m;

        Console.WriteLine($"\n[fetch 90d 15m × {symbols.Length} alts]");
        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            Console.Write($"[{fIdx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, 6);
                if (kl.Count < 500) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                k15Map[sym] = kl;
                Console.WriteLine($"ok ({kl.Count})");
            }
            catch (Exception ex) { Console.WriteLine($"fail: {ex.Message}"); }
        }

        // Pre-compute KNN signals + filter pass + PULLBACK_QUALITY pass per (sym, j)
        // 그리고 j → "이 봉 진입 시 어떻게 되었는지" — entryPrice 만 (TP/SL 별도 계산)
        var allSignals = new List<(string sym, int j, decimal entryPrice, List<IBinanceKline> kl)>();

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var kl = kv.Value;
            int n = kl.Count;

            var feats = new float[n][];
            var labels = new int[n];
            for (int j = 60; j < n; j++)
            {
                int wStart = Math.Max(0, j - 499);
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = LorentzianFeatures.Extract(win)!;
            }
            for (int j = 0; j < n - 4; j++)
            {
                decimal fut = kl[j + 4].ClosePrice;
                decimal nowC = kl[j].ClosePrice;
                labels[j] = fut > nowC ? 1 : (fut < nowC ? -1 : 0);
            }

            var atr1 = new double[n];
            var atr10 = new double[n];
            var adx = new double[n];
            var ema200 = new double[n];
            var sma200 = new double[n];
            var regime = new double[n];
            var nwk = new double[n];
            for (int j = 200; j < n; j++)
            {
                atr1[j] = CalcTR(kl, j);
                atr10[j] = CalcATR(kl, j, 10);
                adx[j] = CalcADX_idx(kl, j, 14);
                ema200[j] = CalcEMA(kl, j, 200);
                sma200[j] = CalcSMA(kl, j, 200);
                regime[j] = CalcRegimeSlope(kl, j);
                nwk[j] = CalcNWKernel(kl, j);
            }

            var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            var sig = new int[n];
            var sigWr = new float[n];
            for (int j = 65; j < n; j++)
            {
                int sIdx = j - 5;
                if (feats[sIdx] != null) engine.AddSample(feats[sIdx], labels[sIdx]);
                if (feats[j] == null) continue;
                var p = engine.Predict(feats[j]);
                if (!p.IsReady || p.K == 0) continue;
                sig[j] = p.Prediction > 0 ? 1 : (p.Prediction < 0 ? -1 : 0);
                sigWr[j] = p.K > 0 ? (float)p.PositiveVotes / p.K : 0f;
            }

            int symPassed = 0;
            for (int j = 250; j < n - maxHoldBars - 1; j++)
            {
                if (sig[j] != 1 || sigWr[j] < guardWinRate) continue;
                if (atr1[j] <= atr10[j]) continue;
                if (regime[j] <= -0.1) continue;
                if (adx[j] <= 20.0) continue;
                if ((double)kl[j].ClosePrice <= ema200[j]) continue;
                if ((double)kl[j].ClosePrice <= sma200[j]) continue;
                if (j < 2 || nwk[j] <= nwk[j - 2]) continue;

                decimal entryPrice = kl[j + 1].OpenPrice;

                // === v5.23.32 PULLBACK_QUALITY ===
                int scanStart = Math.Max(0, j - 22);
                int scanEnd = j - 2;
                bool gateAllow = false;

                if (scanEnd - scanStart >= 5)
                {
                    int hiIdx = scanStart;
                    decimal hiPrice = kl[scanStart].HighPrice;
                    for (int i = scanStart + 1; i <= scanEnd; i++)
                    {
                        if (kl[i].HighPrice > hiPrice) { hiPrice = kl[i].HighPrice; hiIdx = i; }
                    }
                    int loIdx = hiIdx;
                    decimal loPrice = hiPrice;
                    for (int i = hiIdx; i <= scanEnd; i++)
                    {
                        if (kl[i].LowPrice < loPrice) { loPrice = kl[i].LowPrice; loIdx = i; }
                    }

                    decimal pullbackPct = hiPrice > 0 ? (hiPrice - loPrice) / hiPrice * 100m : 0m;
                    bool c1Pullback = pullbackPct >= 1.5m && loIdx > hiIdx;

                    if (c1Pullback)
                    {
                        decimal midPoint = (hiPrice + loPrice) / 2m;
                        bool c2Recovery = entryPrice >= midPoint;
                        decimal ema20_15m = (decimal)CalcEMA(kl, j, 20);
                        decimal emaDevPct = ema20_15m > 0 ? Math.Abs(entryPrice - ema20_15m) / ema20_15m * 100m : 0m;
                        bool c3EmaOk = ema20_15m > 0 && emaDevPct <= 2.5m;

                        bool c4VolOk = false;
                        decimal recentVolSum = 0m; int recentCnt = 0;
                        for (int i = scanEnd - 2; i <= scanEnd; i++)
                        {
                            if (i >= 0) { recentVolSum += kl[i].Volume; recentCnt++; }
                        }
                        decimal pullVolSum = 0m; int pullCnt = 0;
                        for (int i = hiIdx; i <= loIdx; i++)
                        {
                            pullVolSum += kl[i].Volume; pullCnt++;
                        }
                        if (recentCnt > 0 && pullCnt > 0)
                        {
                            decimal recentAvg = recentVolSum / recentCnt;
                            decimal pullAvg = pullVolSum / pullCnt;
                            c4VolOk = pullAvg <= 0m || recentAvg >= pullAvg * 0.8m;
                        }

                        int subPassCnt = (c2Recovery ? 1 : 0) + (c3EmaOk ? 1 : 0) + (c4VolOk ? 1 : 0);
                        gateAllow = subPassCnt >= 2;
                    }
                }

                if (!gateAllow) continue;
                allSignals.Add((sym, j, entryPrice, kl));
                symPassed++;
            }
            Console.WriteLine($"  {sym,-12} v5.23.32 통과 {symPassed,4}건");
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  TP/SL 스윕 — v5.23.32 통과 진입 {allSignals.Count}건 × {combos.Length}가지 조합");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"TP",6} {"SL",6} {"R:R",6} {"BE-WR",7} {"N",6} {"Win",6} {"WR%",7} {"PnL($)",12} {"AvgWin",8} {"AvgLoss",8} {"AvgHold",8}");
        Console.WriteLine(new string('-', 100));

        foreach (var (tp, sl) in combos)
        {
            int n = 0, wins = 0;
            decimal pnlSum = 0m;
            decimal winSum = 0m, lossSum = 0m;
            int winCnt = 0, lossCnt = 0;
            int holdSum = 0;

            foreach (var (sym, j, entryPrice, kl) in allSignals)
            {
                int nKl = kl.Count;
                decimal tpPx = entryPrice * (1m + tp / 100m);
                decimal slPx = entryPrice * (1m - sl / 100m);

                decimal exitPrice = 0m;
                int holdBars = 0;
                bool win_ = false;
                bool exited = false;

                for (int q = j + 1; q <= Math.Min(nKl - 1, j + maxHoldBars); q++)
                {
                    if (kl[q].LowPrice <= slPx) { exitPrice = slPx; holdBars = q - j; win_ = false; exited = true; break; }
                    if (kl[q].HighPrice >= tpPx) { exitPrice = tpPx; holdBars = q - j; win_ = true; exited = true; break; }
                }
                if (!exited)
                {
                    int lastQ = Math.Min(nKl - 1, j + maxHoldBars);
                    exitPrice = kl[lastQ].ClosePrice;
                    holdBars = lastQ - j;
                    win_ = exitPrice > entryPrice;
                }

                decimal priceMovePct = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                decimal pnl = margin * lev * priceMovePct / 100m;

                n++;
                if (win_) { wins++; winSum += pnl; winCnt++; }
                else { lossSum += pnl; lossCnt++; }
                pnlSum += pnl;
                holdSum += holdBars;
            }

            double wr = n > 0 ? wins * 100.0 / n : 0;
            decimal avgWin = winCnt > 0 ? winSum / winCnt : 0m;
            decimal avgLoss = lossCnt > 0 ? lossSum / lossCnt : 0m;
            double avgHold = n > 0 ? holdSum * 1.0 / n : 0;
            decimal rr = sl > 0 ? Math.Round(sl / tp, 2) : 0m;
            double beWr = (double)(sl / (tp + sl)) * 100.0;

            Console.WriteLine($"{tp,5:F1}% {sl,5:F1}% 1:{rr,-4:F2} {beWr,6:F1}% {n,6} {wins,6} {wr,6:F2}% {pnlSum,12:F2} {avgWin,8:F2} {avgLoss,8:F2} {avgHold,8:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("[해석] R:R = SL/TP 비율. BE-WR = 손익분기 승률. WR > BE-WR 이어야 흑자.");
    }

    // ===== [v5.23.32] LORENTZIAN Alt 90d — PULLBACK_QUALITY 가드 효과 검증 =====
    //   라이브 LORENTZIAN 진입 경로 재현 (알트, 15m KNN 7-필터 + 진입가는 j+1 시가)
    //   3가지 모드 동시 측정:
    //     A. NONE         — v5.23.30 baseline (PULLBACK_QUALITY 없음)
    //     B. v5.23.31     — 4-of-3 (눌림 0% 시 c2/c4 trivially pass 버그)
    //     C. v5.23.32     — c1(눌림≥1.5%) 필수 + c2~c4 중 2/3
    private static async Task RunLorentzianAlt90dAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.23.32 LORENTZIAN Alt 90일 백테스트 — PULLBACK_QUALITY 효과 검증");
        Console.WriteLine("  TP=1%, SL=3%, maxHold=96(24h), 15× lev, margin $30/trade");
        Console.WriteLine("================================================================");

        var symbols = new[] {
            "DOGEUSDT", "AVAXUSDT", "ARBUSDT", "OPUSDT", "SUIUSDT",
            "INJUSDT", "LINKUSDT", "SEIUSDT", "NEARUSDT", "ICPUSDT",
            "DYDXUSDT", "ZECUSDT", "TAOUSDT", "ATOMUSDT", "AAVEUSDT"
        };

        const float guardWinRate = 0.70f;
        const decimal tpPct = 1.0m;
        const decimal slPct = 3.0m;
        const decimal lev = 15m;
        const decimal margin = 30m;
        const int maxHoldBars = 96;
        const decimal slippagePct = 0.05m;

        Console.WriteLine($"\n[fetch 90d 15m × {symbols.Length} alts]");
        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        int fIdx = 0;
        foreach (var sym in symbols)
        {
            fIdx++;
            Console.Write($"[{fIdx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, 6);
                if (kl.Count < 500) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                k15Map[sym] = kl;
                Console.WriteLine($"ok ({kl.Count})");
            }
            catch (Exception ex) { Console.WriteLine($"fail: {ex.Message}"); }
        }

        var trades_A = new List<(string sym, decimal pnl, int hold, bool win, decimal pullbackPct)>();
        var trades_B = new List<(string sym, decimal pnl, int hold, bool win, decimal pullbackPct)>();
        var trades_C = new List<(string sym, decimal pnl, int hold, bool win, decimal pullbackPct)>();
        int blocks_B_total = 0, blocks_C_total = 0;
        int signals_total = 0;

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var kl = kv.Value;
            int n = kl.Count;

            var feats = new float[n][];
            var labels = new int[n];
            for (int j = 60; j < n; j++)
            {
                int wStart = Math.Max(0, j - 499);
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = LorentzianFeatures.Extract(win)!;
            }
            for (int j = 0; j < n - 4; j++)
            {
                decimal fut = kl[j + 4].ClosePrice;
                decimal nowC = kl[j].ClosePrice;
                labels[j] = fut > nowC ? 1 : (fut < nowC ? -1 : 0);
            }

            var atr1 = new double[n];
            var atr10 = new double[n];
            var adx = new double[n];
            var ema200 = new double[n];
            var sma200 = new double[n];
            var regime = new double[n];
            var nwk = new double[n];
            for (int j = 200; j < n; j++)
            {
                atr1[j] = CalcTR(kl, j);
                atr10[j] = CalcATR(kl, j, 10);
                adx[j] = CalcADX_idx(kl, j, 14);
                ema200[j] = CalcEMA(kl, j, 200);
                sma200[j] = CalcSMA(kl, j, 200);
                regime[j] = CalcRegimeSlope(kl, j);
                nwk[j] = CalcNWKernel(kl, j);
            }

            var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            var sig = new int[n];
            var sigWr = new float[n];
            for (int j = 65; j < n; j++)
            {
                int sIdx = j - 5;
                if (feats[sIdx] != null) engine.AddSample(feats[sIdx], labels[sIdx]);
                if (feats[j] == null) continue;
                var p = engine.Predict(feats[j]);
                if (!p.IsReady || p.K == 0) continue;
                sig[j] = p.Prediction > 0 ? 1 : (p.Prediction < 0 ? -1 : 0);
                sigWr[j] = p.K > 0 ? (float)p.PositiveVotes / p.K : 0f;
            }

            int symSignals = 0;
            int symBlocks_B = 0, symBlocks_C = 0;

            for (int j = 250; j < n - maxHoldBars - 1; j++)
            {
                if (sig[j] != 1 || sigWr[j] < guardWinRate) continue;
                if (atr1[j] <= atr10[j]) continue;
                if (regime[j] <= -0.1) continue;
                if (adx[j] <= 20.0) continue;
                if ((double)kl[j].ClosePrice <= ema200[j]) continue;
                if ((double)kl[j].ClosePrice <= sma200[j]) continue;
                if (j < 2 || nwk[j] <= nwk[j - 2]) continue;

                symSignals++;
                decimal entryPrice = kl[j + 1].OpenPrice;

                int scanStart = Math.Max(0, j - 22);
                int scanEnd = j - 2;
                bool gateAllow_A = true;
                bool gateAllow_B = true;
                bool gateAllow_C = true;
                decimal pullbackPctOut = 0m;

                if (scanEnd - scanStart >= 5)
                {
                    int hiIdx = scanStart;
                    decimal hiPrice = kl[scanStart].HighPrice;
                    for (int i = scanStart + 1; i <= scanEnd; i++)
                    {
                        if (kl[i].HighPrice > hiPrice) { hiPrice = kl[i].HighPrice; hiIdx = i; }
                    }
                    int loIdx = hiIdx;
                    decimal loPrice = hiPrice;
                    for (int i = hiIdx; i <= scanEnd; i++)
                    {
                        if (kl[i].LowPrice < loPrice) { loPrice = kl[i].LowPrice; loIdx = i; }
                    }

                    decimal pullbackPct = hiPrice > 0 ? (hiPrice - loPrice) / hiPrice * 100m : 0m;
                    pullbackPctOut = pullbackPct;
                    bool c1Pullback_v32 = pullbackPct >= 1.5m && loIdx > hiIdx;
                    bool c1Pullback_v31 = pullbackPct >= 1.5m;

                    decimal midPoint = (hiPrice + loPrice) / 2m;
                    decimal cur = entryPrice;
                    bool c2Recovery = cur >= midPoint;

                    decimal ema20_15m = (decimal)CalcEMA(kl, j, 20);
                    decimal emaDevPct = ema20_15m > 0 ? Math.Abs(cur - ema20_15m) / ema20_15m * 100m : 0m;
                    bool c3EmaOk = ema20_15m > 0 && emaDevPct <= 2.5m;

                    bool c4VolOk_v31 = true;
                    bool c4VolOk_v32 = false;
                    if (loIdx > hiIdx)
                    {
                        decimal recentVolSum = 0m; int recentCnt = 0;
                        for (int i = scanEnd - 2; i <= scanEnd; i++)
                        {
                            if (i >= 0) { recentVolSum += kl[i].Volume; recentCnt++; }
                        }
                        decimal pullVolSum = 0m; int pullCnt = 0;
                        for (int i = hiIdx; i <= loIdx; i++)
                        {
                            pullVolSum += kl[i].Volume; pullCnt++;
                        }
                        if (recentCnt > 0 && pullCnt > 0)
                        {
                            decimal recentAvg = recentVolSum / recentCnt;
                            decimal pullAvg = pullVolSum / pullCnt;
                            bool ok = pullAvg <= 0m || recentAvg >= pullAvg * 0.8m;
                            c4VolOk_v31 = ok;
                            c4VolOk_v32 = ok;
                        }
                    }

                    int passCnt_v31 = (c1Pullback_v31 ? 1 : 0) + (c2Recovery ? 1 : 0) + (c3EmaOk ? 1 : 0) + (c4VolOk_v31 ? 1 : 0);
                    gateAllow_B = passCnt_v31 >= 3;

                    if (c1Pullback_v32)
                    {
                        int subPassCnt_v32 = (c2Recovery ? 1 : 0) + (c3EmaOk ? 1 : 0) + (c4VolOk_v32 ? 1 : 0);
                        gateAllow_C = subPassCnt_v32 >= 2;
                    }
                    else gateAllow_C = false;
                }

                if (!gateAllow_B) symBlocks_B++;
                if (!gateAllow_C) symBlocks_C++;

                decimal exitPrice = 0m;
                int holdBars = 0;
                bool win_ = false;
                bool exited = false;
                decimal tpPx = entryPrice * (1m + tpPct / 100m);
                decimal slPx = entryPrice * (1m - slPct / 100m);

                for (int q = j + 1; q <= Math.Min(n - 1, j + maxHoldBars); q++)
                {
                    if (kl[q].LowPrice <= slPx)
                    {
                        exitPrice = slPx; holdBars = q - j; win_ = false; exited = true; break;
                    }
                    if (kl[q].HighPrice >= tpPx)
                    {
                        exitPrice = tpPx; holdBars = q - j; win_ = true; exited = true; break;
                    }
                }
                if (!exited)
                {
                    int lastQ = Math.Min(n - 1, j + maxHoldBars);
                    exitPrice = kl[lastQ].ClosePrice;
                    holdBars = lastQ - j;
                    win_ = exitPrice > entryPrice;
                }

                decimal priceMovePct = (exitPrice - entryPrice) / entryPrice * 100m - slippagePct;
                decimal pnl = margin * lev * priceMovePct / 100m;

                if (gateAllow_A) trades_A.Add((sym, pnl, holdBars, win_, pullbackPctOut));
                if (gateAllow_B) trades_B.Add((sym, pnl, holdBars, win_, pullbackPctOut));
                if (gateAllow_C) trades_C.Add((sym, pnl, holdBars, win_, pullbackPctOut));
            }

            signals_total += symSignals;
            blocks_B_total += symBlocks_B;
            blocks_C_total += symBlocks_C;
            Console.WriteLine($"  {sym,-12} sig={symSignals,4} blkB={symBlocks_B,4} blkC={symBlocks_C,4}");
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  90일 결과 비교 — 총 KNN 신호 통과: {signals_total}건");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"Mode",-22} {"N",6} {"Wins",6} {"WR%",7} {"PnL($)",10} {"AvgWin",8} {"AvgLoss",8} {"AvgHold",8}");
        Console.WriteLine(new string('-', 90));
        PrintLorAltMode("A. NONE(baseline)", trades_A);
        PrintLorAltMode("B. v5.23.31(buggy)", trades_B);
        PrintLorAltMode("C. v5.23.32(fixed)", trades_C);

        Console.WriteLine();
        Console.WriteLine($"가드 차단:");
        Console.WriteLine($"  v5.23.31 차단: {blocks_B_total,4}건 ({blocks_B_total * 100.0 / Math.Max(1, signals_total):F1}%)");
        Console.WriteLine($"  v5.23.32 차단: {blocks_C_total,4}건 ({blocks_C_total * 100.0 / Math.Max(1, signals_total):F1}%)");
        Console.WriteLine($"  v5.23.32 추가 차단: {blocks_C_total - blocks_B_total}건 (눌림 없는 일직선 상승)");

        Console.WriteLine();
        Console.WriteLine("==== 차단된 진입의 실제 결과 (가드가 막은 진입의 가상 PnL) ====");
        var blocked_by_C_only = trades_A.Where((t, i) =>
        {
            // signal-by-signal alignment requires re-walk; simplified: trades_A includes everything,
            // trades_C includes only those passing v5.23.32. Difference = blocked by either v31 or v32.
            return false;
        }).ToList();
        // Simpler: A - C diff via index → not aligned. Compute aggregate stats of "would-be entries"
        decimal pnl_blocked_by_C = trades_A.Sum(t => t.pnl) - trades_C.Sum(t => t.pnl);
        int wins_blocked_by_C = trades_A.Count(t => t.win) - trades_C.Count(t => t.win);
        int n_blocked_by_C = trades_A.Count - trades_C.Count;
        if (n_blocked_by_C > 0)
        {
            Console.WriteLine($"  v5.23.32가 막은 진입: {n_blocked_by_C}건, 그 중 win {wins_blocked_by_C}건 (WR {wins_blocked_by_C * 100.0 / n_blocked_by_C:F2}%)");
            Console.WriteLine($"  막힌 진입의 합산 PnL: ${pnl_blocked_by_C:F2}  (음수면 가드 효과 ↑, 양수면 손실)");
        }
    }

    private static void PrintLorAltMode(string name, List<(string sym, decimal pnl, int hold, bool win, decimal pullbackPct)> trades)
    {
        int n = trades.Count;
        int wins = trades.Count(t => t.win);
        decimal pnl = trades.Sum(t => t.pnl);
        decimal avgWin = trades.Where(t => t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
        decimal avgLoss = trades.Where(t => !t.win).Select(t => t.pnl).DefaultIfEmpty(0m).Average();
        decimal avgHold = trades.Count > 0 ? (decimal)trades.Average(t => t.hold) : 0m;
        double wr = n > 0 ? wins * 100.0 / n : 0;
        Console.WriteLine($"{name,-22} {n,6} {wins,6} {wr,6:F2}% {pnl,10:F2} {avgWin,8:F2} {avgLoss,8:F2} {avgHold,8:F1}");
    }

    private static async Task RunLorentzian15m5mAsync()
    {
        // [v5.23.59] --canon: jdehorty 원본 기본값 (ADX/EMA200/SMA200 OFF, kernel rate 1봉)
        //   미지정(STRICT)=현행 v5.23.x (ADX>20+EMA200+SMA200 강제 ON, kernel 2봉, darkgreen 2연속)
        bool canon = Environment.GetCommandLineArgs().Any(a => a.Equals("--canon", StringComparison.OrdinalIgnoreCase));
        string cfgName = canon ? "CANONICAL(jdehorty 원본)" : "STRICT(현행 v5.23.x)";
        Console.WriteLine("================================================================");
        Console.WriteLine($"  3년치 Lorentzian + SMC (FVG 눌림) 백테스트 (메이저 4종) — [{cfgName}]");
        Console.WriteLine(canon
            ? "  15m 가드: KNN(WR≥70%) + Vol + Regime + NWKernel(1봉상승)  [ADX/EMA200/SMA200 OFF = 원본]"
            : "  15m 가드: KNN(WR≥70%) + Vol + Regime + ADX + EMA200 + SMA200 + NWKernel + Kernel진한초록");
        Console.WriteLine("  5m 진입: SMC Bullish FVG 감지 → 가격이 FVG 로 되돌아와 mid 위에서 종가 마감 시 LONG");
        Console.WriteLine("  청산: SL = FVG 하단×0.998, TP = entry + (entry-SL)×3.0 (3:1 R:R), maxHold 96봉");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;
        const int maxSlots = 2;
        const decimal swingLeverage = 20m;
        const decimal slippagePct = 0.05m;
        const float guardWinRate = 0.70f;
        const int maxHoldBars15m = 96;     // 24h hard-cap
        const int neighborsK = 8;
        const int featureWindow = 500;             // 정규화 윈도

        // [v5.23.0] 메이저 4종 + 3년치 (15m × 70 page + 5m × 210 page) — fetch 약 15분
        var testSymbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        Console.WriteLine($"\n[fetch 3y 15m + 5m × {testSymbols.Length} 메이저]  (예상 ~15분)");
        var k15Map = new Dictionary<string, List<IBinanceKline>>();
        var k5Map = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var sym in testSymbols)
        {
            idx++;
            Console.Write($"[{idx}/{testSymbols.Length}] {sym} 15m ");
            try
            {
                // [v5.23.18] 90일치 (15m=6 page=8640봉, 5m=18 page=25920봉)
                var kl15 = await FetchKlines15mAsync(sym, 6);
                if (kl15.Count < 500) { Console.WriteLine($"skip ({kl15.Count})"); continue; }
                k15Map[sym] = kl15;
                Console.Write($"ok ({kl15.Count}) | 5m ");
                var kl5 = await FetchKlinesAsync(sym, 18);
                k5Map[sym] = kl5;
                Console.WriteLine($"ok ({kl5.Count})");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var monthly = new SortedDictionary<string, (int n, int kFlip, int kernelFlip, int hardCap, decimal pnl)>();
        decimal totalPnl = 0m;
        int totalN = 0;
        int blkKnn = 0, blkVol = 0, blkRegime = 0, blkAdx = 0, blkEma = 0, blkSma = 0, blkKernel = 0;
        var active = new List<DateTime>();

        // [v5.23.1] active 슬롯을 per-symbol 로 변경 (이전 버그: BTC 가 모든 슬롯 점유 → 다른 심볼 진입 0)
        // ── DIAGNOSTIC ──
        // 1) KNN predictive accuracy: for every bar where sig=+1 & WR≥70%,
        //    record actual close change at +4, +12, +24, +96 bars
        var knnSigBars = new List<(string sym, DateTime t, float wr, double r4, double r12, double r24, double r96)>();
        // 2) per-trade detail: entry, exit, MAE, MFE, exit reason
        var trades = new List<(string sym, DateTime entryT, DateTime exitT, string kind, decimal pctRaw, decimal pctNet, decimal mae, decimal mfe, float wr, int holdBars)>();
        // 3) per-symbol summary
        var perSymbol = new Dictionary<string, (int n, int wins, decimal pnl)>();

        foreach (var kv in k15Map)
        {
            var sym = kv.Key;
            var kl = kv.Value;
            active.Clear();   // [v5.23.1] 심볼별 슬롯 독립 — 이전 버그 fix

            // Pre-compute features + labels
            var feats = new float[kl.Count][];
            var labels = new int[kl.Count];
            for (int j = 60; j < kl.Count; j++)
            {
                int wStart = Math.Max(0, j - (featureWindow - 1));
                var win = kl.GetRange(wStart, j - wStart + 1);
                feats[j] = LorentzianFeatures.Extract(win)!;
            }
            // [v5.23.59] jdehorty trailing 라벨: src[4]<src[0] (close[j] vs close[j-4]) — LorentzianGuard.LabelForBar 와 동일
            for (int j = 4; j < kl.Count; j++)
            {
                decimal nowC  = kl[j].ClosePrice;
                decimal prevC = kl[j - 4].ClosePrice;
                labels[j] = nowC > prevC ? 1 : (nowC < prevC ? -1 : 0);
            }

            // Pre-compute filters per bar
            var atr1 = new double[kl.Count];
            var atr10 = new double[kl.Count];
            var adx = new double[kl.Count];
            var ema200 = new double[kl.Count];
            var sma200 = new double[kl.Count];
            var regime = new double[kl.Count];
            var nwk = new double[kl.Count];
            for (int j = 200; j < kl.Count; j++)
            {
                atr1[j] = CalcTR(kl, j);
                atr10[j] = CalcATR(kl, j, 10);
                adx[j] = CalcADX_idx(kl, j, 14);
                ema200[j] = CalcEMA(kl, j, 200);
                sma200[j] = CalcSMA(kl, j, 200);
                regime[j] = CalcRegimeSlope(kl, j);
                nwk[j] = CalcNWKernel(kl, j);
            }

            // Walk-forward training + signal generation
            var engine = new LorentzianAnnEngine(sym, neighborsCount: neighborsK, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            var sig = new int[kl.Count]; // +1 LONG, -1 SHORT, 0 NEUTRAL (per bar predicted)
            var sigWr = new float[kl.Count];

            for (int j = 65; j < kl.Count; j++)
            {
                int sIdx = j - 5;
                if (feats[sIdx] != null) engine.AddSample(feats[sIdx], labels[sIdx]);
                if (feats[j] == null) continue;
                var p = engine.Predict(feats[j]);
                if (!p.IsReady || p.K == 0) continue;
                sig[j] = p.Prediction > 0 ? 1 : (p.Prediction < 0 ? -1 : 0);
                sigWr[j] = p.K > 0 ? (float)p.PositiveVotes / p.K : 0f;
            }

            // 15m 가드 통과 여부 pre-compute (각 15m 봉 마감 기준) — LorentzianGuard 와 동일 로직 inline
            //   주의: LorentzianGuard.EvaluateEntry 직접 호출은 ADX 가 매 호출 O(n) → 너무 느림.
            //         같은 임계값 (ATR1>ATR10, regime>-0.1, ADX>20, EMA200, SMA200, NWKernel up) inline 적용.
            var guardPass = new bool[kl.Count];
            // Kernel 진한 초록색 = 직전 2봉 연속 상승 (kernel[j]>kernel[j-1] AND kernel[j-1]>kernel[j-2])
            var kernelDarkGreen = new bool[kl.Count];
            for (int j = 250; j < kl.Count; j++)
            {
                if (sig[j] != 1 || sigWr[j] < guardWinRate) continue;
                if (atr1[j] <= atr10[j]) continue;             // volatility (jdehorty 기본 ON)
                if (regime[j] <= -0.1) continue;               // regime (jdehorty 기본 ON)
                if (!canon)
                {
                    // STRICT: 현행 v5.23.x 강제 ON 필터
                    if (adx[j] <= 20.0) continue;
                    if ((double)kl[j].ClosePrice <= ema200[j]) continue;
                    if ((double)kl[j].ClosePrice <= sma200[j]) continue;
                    if (j < 2 || nwk[j] <= nwk[j - 2]) continue;             // kernel rate 2봉
                    guardPass[j] = true;
                    if (j >= 2 && nwk[j] > nwk[j - 1] && nwk[j - 1] > nwk[j - 2]) kernelDarkGreen[j] = true;  // 2연속 상승
                }
                else
                {
                    // CANONICAL: jdehorty 원본 — ADX/EMA200/SMA200 OFF, isBullishRate = kernel 1봉 상승
                    if (j < 1 || nwk[j] <= nwk[j - 1]) continue;             // kernel rate 1봉 (yhat1[1]<yhat1)
                    guardPass[j] = true;
                    kernelDarkGreen[j] = true;                               // canonical isBullish = 1봉 상승 (위에서 이미 enforce)
                }
            }

            // DIAG: KNN 신호 자체 예측 정확도 (sig=+1 & WR≥70% 통과 모든 봉)
            for (int j = 250; j < kl.Count - 96; j++)
            {
                if (sig[j] != 1 || sigWr[j] < guardWinRate) continue;
                double c0 = (double)kl[j].ClosePrice;
                double r4 = ((double)kl[j + 4].ClosePrice - c0) / c0 * 100.0;
                double r12 = ((double)kl[j + 12].ClosePrice - c0) / c0 * 100.0;
                double r24 = ((double)kl[j + 24].ClosePrice - c0) / c0 * 100.0;
                double r96 = ((double)kl[j + 96].ClosePrice - c0) / c0 * 100.0;
                knnSigBars.Add((sym, kl[j].OpenTime, sigWr[j], r4, r12, r24, r96));
            }

            // 5m walk — Lorentzian 15m 가드 통과 + SMC Bullish FVG 눌림 트리거
            var kl5 = k5Map[sym];
            int j15Cursor = 0;
            // Active FVG 목록: (생성 idx, 상단, 하단). 50봉 보존, 침범 시 무효화.
            var activeFvgs = new List<(int birth, double top, double bot)>();
            const int fvgLifeBars = 50;
            const int maxHoldBars5m = 288;     // 24h × 12 5m bars
            const decimal rrTarget = 3.0m;     // 3:1 R:R

            for (int i = 30; i < kl5.Count - 1; i++)
            {
                var t5 = kl5[i].OpenTime;

                // (1) 새 bullish FVG 감지 — bar[i-2].high < bar[i].low (3봉 갭)
                if (i >= 2 && (double)kl5[i - 2].HighPrice < (double)kl5[i].LowPrice)
                    activeFvgs.Add((i, (double)kl5[i].LowPrice, (double)kl5[i - 2].HighPrice));
                // (2) 만료/침범된 FVG 제거
                activeFvgs.RemoveAll(f => i - f.birth > fvgLifeBars
                                       || (double)kl5[i].LowPrice <= f.bot * 0.999);

                // (3) 마지막 마감된 15m 봉
                while (j15Cursor < kl.Count - 1 && kl[j15Cursor + 1].CloseTime <= t5) j15Cursor++;
                int j15 = j15Cursor;
                if (j15 < 250) continue;

                // (4) 15m 가드
                if (!guardPass[j15]) { blkKnn++; continue; }
                if (!kernelDarkGreen[j15]) { blkKernel++; continue; }

                // (5) FVG 눌림 트리거: 활성 FVG 중 가장 최근 것이
                //     - 현재 5m 봉 low <= mid (FVG 안으로 들어옴)
                //     - 현재 5m 봉 close > mid (rejection 마감)
                //     - 현재 5m 봉 양봉
                if (activeFvgs.Count == 0) continue;
                var fvg = activeFvgs[activeFvgs.Count - 1];
                double mid = (fvg.top + fvg.bot) / 2.0;
                if ((double)kl5[i].LowPrice > mid) continue;       // 아직 FVG 안 닿음
                if ((double)kl5[i].ClosePrice <= mid) continue;    // mid 위 종가 마감 실패
                if (kl5[i].ClosePrice <= kl5[i].OpenPrice) continue;

                active.RemoveAll(t => t <= t5);
                if (active.Count >= maxSlots) continue;

                // 진입 — SL = FVG 하단×0.998, TP = entry + (entry-SL)×3.0
                decimal entry = kl5[i].ClosePrice;
                decimal slPx = (decimal)(fvg.bot * 0.998);
                decimal risk = entry - slPx;
                if (risk <= 0m) continue;
                decimal tpPx = entry + risk * rrTarget;
                decimal slPctRaw = -risk / entry * 100m;
                decimal tpPctRaw = (tpPx - entry) / entry * 100m;

                int jStart = j15;
                string kind = "HARDCAP";
                decimal pctRaw = 0m;
                int holdBars5m = maxHoldBars5m;
                decimal mae = 0m, mfe = 0m;

                for (int k = 1; k <= maxHoldBars5m && i + k < kl5.Count; k++)
                {
                    var b = kl5[i + k];
                    decimal pctH = (b.HighPrice - entry) / entry * 100m;
                    decimal pctL = (b.LowPrice - entry) / entry * 100m;
                    if (pctH > mfe) mfe = pctH;
                    if (pctL < mae) mae = pctL;

                    bool tpHit = b.HighPrice >= tpPx;
                    bool slHit = b.LowPrice <= slPx;
                    if (tpHit && slHit) { kind = "SL"; pctRaw = slPctRaw; holdBars5m = k; break; }
                    if (tpHit) { kind = "TP"; pctRaw = tpPctRaw; holdBars5m = k; break; }
                    if (slHit) { kind = "SL"; pctRaw = slPctRaw; holdBars5m = k; break; }
                }
                if (kind == "HARDCAP")
                {
                    int idxClose5 = Math.Min(i + maxHoldBars5m, kl5.Count - 1);
                    pctRaw = (kl5[idxClose5].ClosePrice - entry) / entry * 100m;
                }
                int holdBars = holdBars5m;   // 호환성

                decimal notional = margin * swingLeverage;
                decimal pctNet = pctRaw - (decimal)(FEE_RATE * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                totalN++;

                int endBarTr = Math.Min(i + holdBars5m, kl5.Count - 1);
                trades.Add((sym, t5, kl5[endBarTr].OpenTime, kind, pctRaw, pctNet, mae, mfe, sigWr[jStart], holdBars5m));
                if (!perSymbol.ContainsKey(sym)) perSymbol[sym] = (0, 0, 0m);
                var ps = perSymbol[sym];
                ps.n++;
                if (pnlUsd > 0) ps.wins++;
                ps.pnl += pnlUsd;
                perSymbol[sym] = ps;

                string monthKey = t5.ToString("yyyy-MM");
                if (!monthly.ContainsKey(monthKey)) monthly[monthKey] = (0, 0, 0, 0, 0m);
                var m = monthly[monthKey];
                m.n++;
                if (kind == "TP") m.kFlip++;
                else if (kind == "SL") m.kernelFlip++;
                else m.hardCap++;
                m.pnl += pnlUsd;
                monthly[monthKey] = m;

                active.Add(kl5[endBarTr].OpenTime);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"==== Lorentzian + SMC FVG 결과 ====");
        Console.WriteLine($"  설정: 시드 ${seed} 마진 ${margin}×{maxSlots}슬롯×{swingLeverage}x | 3:1 R:R | maxHold 24h");
        Console.WriteLine();
        Console.WriteLine($"{"월",-9} {"진입",5} {"TP",6} {"SL",5} {"HardCap",8} {"PnL($)",10} {"ROI",10}");
        Console.WriteLine(new string('-', 75));
        foreach (var kvm in monthly)
        {
            decimal monthRoi = kvm.Value.pnl / seed * 100m;
            Console.WriteLine($"{kvm.Key,-9} {kvm.Value.n,5} {kvm.Value.kFlip,9} {kvm.Value.kernelFlip,7} {kvm.Value.hardCap,8} {kvm.Value.pnl,9:F2} {monthRoi,8:F2}%");
        }
        Console.WriteLine(new string('-', 75));
        decimal totalRoi = totalPnl / seed * 100m;
        int totalKnnFlip = monthly.Values.Sum(m => m.kFlip);
        int totalKernelFlip = monthly.Values.Sum(m => m.kernelFlip);
        int totalHardCap = monthly.Values.Sum(m => m.hardCap);
        Console.WriteLine($"{"합계",-9} {totalN,5} {totalKnnFlip,9} {totalKernelFlip,7} {totalHardCap,8} {totalPnl,9:F2} {totalRoi,8:F2}%");
        Console.WriteLine();
        int profitMonths = monthly.Values.Count(m => m.pnl > 0);
        int lossMonths = monthly.Values.Count(m => m.pnl < 0);
        Console.WriteLine($"수익월 {profitMonths} / 손실월 {lossMonths}");
        Console.WriteLine();
        Console.WriteLine($"==== 가드 차단 깔때기 (funnel) ====");
        long totalCandidates = (long)blkKnn + blkVol + blkRegime + blkAdx + blkEma + blkSma + blkKernel + totalN;
        Console.WriteLine($"  Total 캔들 후보              {totalCandidates,10}");
        Console.WriteLine($"  ─ KNN (winRate<70% or pred≤0)  차단 {blkKnn,9}  ({100.0 * blkKnn / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ Volatility (ATR1≤ATR10)      차단 {blkVol,9}  ({100.0 * blkVol / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ Regime (slope≤-0.1)          차단 {blkRegime,9}  ({100.0 * blkRegime / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ ADX (≤20)                    차단 {blkAdx,9}  ({100.0 * blkAdx / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ EMA200 (close≤ema200)        차단 {blkEma,9}  ({100.0 * blkEma / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ SMA200 (close≤sma200)        차단 {blkSma,9}  ({100.0 * blkSma / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ NW Kernel (방향 down)        차단 {blkKernel,9}  ({100.0 * blkKernel / Math.Max(1, totalCandidates):F1}%)");
        Console.WriteLine($"  ─ 진입 통과                          {totalN,9}  ({100.0 * totalN / Math.Max(1, totalCandidates):F2}%)");
        Console.WriteLine();

        // ── KNN 모델 자체 예측 정확도 (smoking gun) ──
        Console.WriteLine($"==== [핵심] KNN 모델 자체 예측 정확도 (sig=+1 & WR≥70% 조건만 통과한 신호) ====");
        Console.WriteLine($"  총 신호 수: {knnSigBars.Count}");
        Console.WriteLine();
        Console.WriteLine($"  TradingView jdehorty 가 표시하는 'winRate 70%' 가 in-sample 평가가 아니라면,");
        Console.WriteLine($"  walk-forward 로 평가했을 때 아래 horizon 별 양수율(winRate)이 70%+ 나와야 함.");
        Console.WriteLine();
        if (knnSigBars.Count > 0)
        {
            int win4 = knnSigBars.Count(s => s.r4 > 0);
            int win12 = knnSigBars.Count(s => s.r12 > 0);
            int win24 = knnSigBars.Count(s => s.r24 > 0);
            int win96 = knnSigBars.Count(s => s.r96 > 0);
            double avg4 = knnSigBars.Average(s => s.r4);
            double avg12 = knnSigBars.Average(s => s.r12);
            double avg24 = knnSigBars.Average(s => s.r24);
            double avg96 = knnSigBars.Average(s => s.r96);
            Console.WriteLine($"  Horizon  WR (양수율)         평균 가격변화%   기대값");
            Console.WriteLine($"  ─────────────────────────────────────────────────────");
            Console.WriteLine($"  +4 봉    {100.0 * win4 / knnSigBars.Count,5:F2}% ({win4}/{knnSigBars.Count})  {avg4,+8:F4}%        {(avg4 > 0.18 ? "✅ 흑자가능" : "❌ 적자(수수료+슬립이 0.18%)")}");
            Console.WriteLine($"  +12 봉   {100.0 * win12 / knnSigBars.Count,5:F2}% ({win12}/{knnSigBars.Count})  {avg12,+8:F4}%        {(avg12 > 0.18 ? "✅ 흑자가능" : "❌ 적자")}");
            Console.WriteLine($"  +24 봉   {100.0 * win24 / knnSigBars.Count,5:F2}% ({win24}/{knnSigBars.Count})  {avg24,+8:F4}%        {(avg24 > 0.18 ? "✅ 흑자가능" : "❌ 적자")}");
            Console.WriteLine($"  +96 봉   {100.0 * win96 / knnSigBars.Count,5:F2}% ({win96}/{knnSigBars.Count})  {avg96,+8:F4}%        {(avg96 > 0.18 ? "✅ 흑자가능" : "❌ 적자")}");
            Console.WriteLine();
            Console.WriteLine($"  ▶ 결론: KNN 신호의 실제 walk-forward winRate 가 표시된 70% 와 일치하면 흑자, 50%/random 이면 적자");
        }
        Console.WriteLine();

        // ── 심볼별 ──
        Console.WriteLine($"==== 심볼별 PnL (정렬: PnL 오름차순) ====");
        Console.WriteLine($"  {"심볼",-12} {"진입",6} {"승",5} {"승률",8} {"PnL($)",10}");
        Console.WriteLine($"  " + new string('-', 50));
        foreach (var kvs in perSymbol.OrderBy(x => x.Value.pnl))
        {
            double wr = kvs.Value.n > 0 ? 100.0 * kvs.Value.wins / kvs.Value.n : 0;
            Console.WriteLine($"  {kvs.Key,-12} {kvs.Value.n,6} {kvs.Value.wins,5} {wr,7:F2}% {kvs.Value.pnl,9:F2}");
        }
        Console.WriteLine();

        // ── 종료 사유별 ──
        Console.WriteLine($"==== 종료 사유별 평균 PnL ====");
        var byKind = trades.GroupBy(t => t.kind).Select(g => new
        {
            Kind = g.Key,
            N = g.Count(),
            AvgPctRaw = g.Average(t => (double)t.pctRaw),
            AvgPctNet = g.Average(t => (double)t.pctNet),
            WinRate = g.Count() > 0 ? 100.0 * g.Count(t => t.pctNet > 0) / g.Count() : 0,
            AvgHold = g.Average(t => t.holdBars),
            AvgMae = g.Average(t => (double)t.mae),
            AvgMfe = g.Average(t => (double)t.mfe),
        });
        Console.WriteLine($"  {"사유",-13} {"N",5} {"승률",8} {"평균%(net)",11} {"평균보유",9} {"평균MAE",9} {"평균MFE",9}");
        Console.WriteLine($"  " + new string('-', 75));
        foreach (var x in byKind.OrderByDescending(x => x.N))
        {
            Console.WriteLine($"  {x.Kind,-13} {x.N,5} {x.WinRate,7:F2}% {x.AvgPctNet,+10:F4}% {x.AvgHold,8:F1} {x.AvgMae,+8:F2}% {x.AvgMfe,+8:F2}%");
        }
        Console.WriteLine();

        // ── 최악 10건 / 최고 10건 ──
        Console.WriteLine($"==== 최악 손실 10건 ====");
        Console.WriteLine($"  {"심볼",-10} {"진입시각",-17} {"종료",-12} {"보유",5} {"WR",6} {"종료%(net)",11} {"MAE",7} {"MFE",7}");
        foreach (var t in trades.OrderBy(x => x.pctNet).Take(10))
            Console.WriteLine($"  {t.sym,-10} {t.entryT:MM-dd HH:mm} {t.kind,-12} {t.holdBars,5} {t.wr,5:F2} {t.pctNet,+10:F4}% {t.mae,+6:F2}% {t.mfe,+6:F2}%");
        Console.WriteLine();
        Console.WriteLine($"==== 최고 수익 10건 ====");
        Console.WriteLine($"  {"심볼",-10} {"진입시각",-17} {"종료",-12} {"보유",5} {"WR",6} {"종료%(net)",11} {"MAE",7} {"MFE",7}");
        foreach (var t in trades.OrderByDescending(x => x.pctNet).Take(10))
            Console.WriteLine($"  {t.sym,-10} {t.entryT:MM-dd HH:mm} {t.kind,-12} {t.holdBars,5} {t.wr,5:F2} {t.pctNet,+10:F4}% {t.mae,+6:F2}% {t.mfe,+6:F2}%");
        Console.WriteLine();

        // ── PnL 분포 ──
        Console.WriteLine($"==== PnL%(net) 분포 (히스토그램) ====");
        var buckets = new[] { -10.0, -5.0, -3.0, -2.0, -1.0, -0.5, -0.18, 0.0, 0.18, 0.5, 1.0, 2.0, 3.0, 5.0, 10.0 };
        for (int b = 0; b < buckets.Length - 1; b++)
        {
            int count = trades.Count(t => (double)t.pctNet >= buckets[b] && (double)t.pctNet < buckets[b + 1]);
            string bar = new string('█', Math.Min(80, count / 2));
            Console.WriteLine($"  [{buckets[b],+6:F2}% ~ {buckets[b + 1],+6:F2}%]  {count,5} {bar}");
        }
        Console.WriteLine();

        Console.WriteLine($"월평균 진입 {totalN / Math.Max(1, monthly.Count)}건");
    }

    // [v5.22.72] Daily Swing + ProfitTrailing 3년 월별 통계
    private static async Task RunDailySwingMonthly3yAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  Daily Swing + ProfitTrailing 3년 월별 통계 (lev 20x)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;
        const int maxSlots = 2;
        const decimal tpPct = 15m;
        const decimal slPct = 5m;
        const decimal slippagePct = 0.05m;
        const int maxHoldBars = 14;
        const decimal swingLeverage = 20m;
        const decimal trailingTriggerRoe = 3m;
        const decimal trailingMinRetrace = 5m;
        const decimal trailingRatio = 0.33m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 3년 1D — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1dAsync(sym, 1);
                if (kl.Count < 60) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 51) return false;
            decimal sma20 = 0m; for (int q = i - 19; q <= i; q++) sma20 += kl[q].ClosePrice; sma20 /= 20m;
            decimal sma50 = 0m; for (int q = i - 49; q <= i; q++) sma50 += kl[q].ClosePrice; sma50 /= 50m;
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 70.0) return false;
            decimal volAvg = 0m; for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume; volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal highestRoe = 0m;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                decimal highPriceRoe = (b.HighPrice - entry) / entry * 100m * swingLeverage;
                if (highPriceRoe > highestRoe) highestRoe = highPriceRoe;

                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);

                if (highestRoe >= trailingTriggerRoe)
                {
                    decimal closeRoe = (b.ClosePrice - entry) / entry * 100m * swingLeverage;
                    decimal limit = Math.Max(trailingMinRetrace, highestRoe * trailingRatio);
                    if (highestRoe - closeRoe >= limit)
                    {
                        decimal exitPct = (b.ClosePrice - entry) / entry * 100m;
                        return ("TRAIL", exitPct, k);
                    }
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 1m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        // 3년 시뮬
        DateTime since = DateTime.UtcNow.AddYears(-3);
        var candidates = new List<(DateTime time, string sym, int barIdx)>();
        foreach (var kv in fullData)
        {
            var kl = kv.Value; var sym = kv.Key;
            for (int i = 51; i < kl.Count - maxHoldBars; i++)
            {
                if (kl[i].OpenTime < since) continue;
                if (!ShouldEnter(kl, i)) continue;
                candidates.Add((kl[i].OpenTime, sym, i));
            }
        }
        candidates.Sort((a, b) => a.time.CompareTo(b.time));

        var active = new List<DateTime>();
        // 월별 집계: yyyy-MM → (n, tpN, slN, trailN, pnl)
        var monthly = new SortedDictionary<string, (int n, int tp, int sl, int trail, decimal pnl)>();
        decimal totalPnl = 0m;
        int totalN = 0, totalTp = 0, totalSl = 0, totalTrail = 0;

        foreach (var c in candidates)
        {
            active.RemoveAll(t => t <= c.time);
            if (active.Count >= maxSlots) continue;
            decimal notional = margin * swingLeverage;
            var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
            decimal pctNet = pctRaw - (decimal)(FEE_RATE * 2m * 100m) - (slippagePct * 2m);
            decimal pnlUsd = notional * pctNet / 100m;
            totalPnl += pnlUsd;
            totalN++;
            if (kind == "TP") totalTp++;
            else if (kind == "SL") totalSl++;
            else if (kind == "TRAIL") totalTrail++;

            string monthKey = c.time.ToString("yyyy-MM");
            if (!monthly.ContainsKey(monthKey)) monthly[monthKey] = (0, 0, 0, 0, 0m);
            var m = monthly[monthKey];
            m.n++;
            if (kind == "TP") m.tp++;
            else if (kind == "SL") m.sl++;
            else if (kind == "TRAIL") m.trail++;
            m.pnl += pnlUsd;
            monthly[monthKey] = m;

            int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
            active.Add(fullData[c.sym][endBar].OpenTime);
        }

        Console.WriteLine();
        Console.WriteLine($"  설정: 시드 ${seed} 마진 ${margin}×{maxSlots}슬롯×{swingLeverage}x | TP+{tpPct}% SL-{slPct}% / Trailing v5.22.68");
        Console.WriteLine();
        Console.WriteLine($"==== 월별 통계 (3년) ====");
        Console.WriteLine($"{"월",-9} {"진입",4} {"TP",3} {"SL",3} {"TRAIL",6} {"PnL($)",10} {"ROI(시드기준)",14}");
        Console.WriteLine(new string('-', 65));
        foreach (var kv in monthly)
        {
            decimal monthRoi = kv.Value.pnl / seed * 100m;
            Console.WriteLine($"{kv.Key,-9} {kv.Value.n,4} {kv.Value.tp,3} {kv.Value.sl,3} {kv.Value.trail,6} {kv.Value.pnl,9:F2} {monthRoi,12:F2}%");
        }
        Console.WriteLine(new string('-', 65));
        decimal totalRoi = totalPnl / seed * 100m;
        Console.WriteLine($"{"합계",-9} {totalN,4} {totalTp,3} {totalSl,3} {totalTrail,6} {totalPnl,9:F2} {totalRoi,12:F2}%");
        Console.WriteLine();
        // 월 통계
        var profitMonths = monthly.Values.Count(m => m.pnl > 0);
        var lossMonths = monthly.Values.Count(m => m.pnl < 0);
        Console.WriteLine($"수익 월 {profitMonths}개월 / 손실 월 {lossMonths}개월 / 중립 월 {monthly.Count - profitMonths - lossMonths}개월");
    }

    // [v5.22.72] Daily Swing + v5.22.68 ProfitTrailing 통합 백테스트
    //   사용자 의문: "SL ROE -40%인데 정말 흑자 가능?"
    //   Daily Swing 단독 백테스트(+330%)는 ProfitTrailing 미적용. 실 라이브 동작 시뮬 필요.
    //   진입: 1D close>20SMA + 20SMA>50SMA + RSI 50~70 + vol×1.5 + 양봉
    //   청산: TP+15% / SL-7% (price) / ProfitTrailing (highest>=+3% → retrace>=max(5,highest×33))
    //   leverage 5x 가정
    private static async Task RunDailySwingWithTrailingAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  Daily Swing + ProfitTrailing v5.22.68 통합 백테스트");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;
        const int maxSlots = 2;
        const decimal tpPct = 15m;        // 가격 +15%
        const decimal slPct = 5m;         // 가격 -5% (20x 레버리지 강제청산 부근, SL 우선)
        const decimal slippagePct = 0.05m;
        const int maxHoldBars = 14;       // 14일
        const decimal swingLeverage = 20m;   // 20x 사용자 환경
        const decimal trailingTriggerRoe = 3m;     // ROE +3% 이상 도달 시 활성
        const decimal trailingMinRetrace = 5m;     // 최소 retrace %p
        const decimal trailingRatio = 0.33m;       // highest × 33%

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch — {symbols.Length}개 심볼 (1D)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1dAsync(sym, 1);
                if (kl.Count < 60) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 51) return false;
            decimal sma20 = 0m; for (int q = i - 19; q <= i; q++) sma20 += kl[q].ClosePrice; sma20 /= 20m;
            decimal sma50 = 0m; for (int q = i - 49; q <= i; q++) sma50 += kl[q].ClosePrice; sma50 /= 50m;
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 70.0) return false;
            decimal volAvg = 0m; for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume; volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }

        // 시뮬: 매 봉 ROE 추적 + ProfitTrailing 적용
        // 라이브는 5분 tick 검사하지만 1D 봉만 있으므로 봉 내 high/low 로 근사
        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            decimal highestRoe = 0m;
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                // 봉 내 최고 가격 ROE
                decimal highPriceRoe = (b.HighPrice - entry) / entry * 100m * swingLeverage;
                decimal lowPriceRoe  = (b.LowPrice  - entry) / entry * 100m * swingLeverage;
                if (highPriceRoe > highestRoe) highestRoe = highPriceRoe;

                // TP/SL 도달 우선
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);

                // ProfitTrailing 시뮬: highest ROE 도달 후 봉 종가 ROE 가 trailing limit 넘게 retrace 시 청산
                //   봉 종가 기준 (라이브는 매 5분 tick 이므로 더 빠르게 트리거되지만 봉 종가로 근사)
                if (highestRoe >= trailingTriggerRoe)
                {
                    decimal closeRoe = (b.ClosePrice - entry) / entry * 100m * swingLeverage;
                    decimal limit = Math.Max(trailingMinRetrace, highestRoe * trailingRatio);
                    decimal retrace = highestRoe - closeRoe;
                    if (retrace >= limit)
                    {
                        decimal exitPct = (b.ClosePrice - entry) / entry * 100m;
                        return ("TRAIL", exitPct, k);
                    }
                }
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 1m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int trailN, int beN, int toN, decimal mddPct) Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                for (int i = 51; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));
            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, trailN = 0, beN = 0, toN = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * swingLeverage;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "TRAIL") trailN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            return (totalPnl, n, tpN, slN, trailN, beN, toN, mddPct);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% SL-{slPct}% (price) / lev {swingLeverage}x");
        Console.WriteLine($"  ProfitTrailing: highest≥+{trailingTriggerRoe}% ROE 활성, retrace≥max({trailingMinRetrace}%, highest×{trailingRatio:P0}) → 청산");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"TRAIL",6} {"BE",5} {"TO",5} {"PnL",10} {"ROI",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 90));
        int[] periods = { 30, 60, 90, 180, 365 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.trailN,6} {r.beN,5} {r.toN,5} {r.pnl,9:F2} {roi,9:F2}% {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 90));
    }

    // [v5.22.72] Pullback Long 전략 백테스트 — 눌림목 진입
    //   1H EMA20 위 + 15m 직전 봉 BEAR + 현재 봉 BULL + Higher High + EMA20 ±1% + RSI 35~60 + vol×1.2
    //   180일 30심볼 시뮬
    private static async Task RunPullbackLongAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.72 Pullback Long — 눌림목 진입 백테스트 (180일)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tpPct = 2.0m;
        const decimal slPct = 1.5m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 12;
        const int maxHoldBars = 24;        // 6h max

        var fullData15m = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (15m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, fetchPages);
                if (kl.Count < 200) { Console.WriteLine("skip"); continue; }
                fullData15m[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 1H EMA20 = 15m EMA80 근사 (4배). 15m 봉 i 시점에서 80봉 EMA = 1H 20봉 EMA 근사
        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 85) return false;

            // 1. 1H EMA20 위 (15m 80봉 EMA 근사)
            decimal ema80 = Ema(kl, i, 80);
            if (kl[i].ClosePrice <= ema80) return false;

            // 2. 직전 봉 BEAR
            if (kl[i - 1].ClosePrice >= kl[i - 1].OpenPrice) return false;

            // 3. 현재 봉 BULL + 종가 > 직전 봉 high
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            if (kl[i].ClosePrice <= kl[i - 1].HighPrice) return false;

            // 4. 15m EMA20 이격도 ≤ ±1%
            decimal ema20 = Ema(kl, i, 20);
            decimal dist = (kl[i].ClosePrice - ema20) / ema20 * 100m;
            if (dist > 1m || dist < -1m) return false;

            // 5. RSI 35~60
            double rsi = CalcRsi14(kl, i);
            if (rsi < 35.0 || rsi > 60.0) return false;

            // 6. 거래량 > 직전 5봉 평균 × 1.2
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 1.2m) return false;

            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 2;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData15m)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 90; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData15m[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData15m[c.sym].Count - 1);
                active.Add(fullData15m[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% / max {maxHoldBars}봉(6h) / 1H EMA20위 + 눌림+반등 + EMA20±1%");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유",8} {"PnL",10} {"ROI",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 95));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 95));
    }

    // [v5.22.69] Top30 24h 상승률 백테스트 — AltMomentum 검증
    //   사용자 보고: "BUSDT/TST 폭등 코인 진입 없음. 손절만 나옴"
    //   1. Binance /fapi/v1/ticker/24hr 호출 → priceChangePercent 상위 30개 추출
    //   2. 각 심볼 1m 1500봉 fetch (~25h)
    //   3. AltMomentum 진입 시뮬 (24h +5~30% + EMA20 갓 돌파 + vol×2 + RSI<70)
    //   4. TP +3% / SL -2% / max 30분 hold
    private static async Task RunTop30Last24hAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.69 검증 — top 30 상승률 알트 (24h) AltMomentum 시뮬");
        Console.WriteLine("================================================================");

        // 1. ticker/24hr fetch top 30
        Console.WriteLine("\n[1] Binance /fapi/v1/ticker/24hr — top 30 추출");
        List<(string sym, decimal change24h, decimal qVol)> top30;
        try
        {
            var json = await http.GetStringAsync("https://fapi.binance.com/fapi/v1/ticker/24hr");
            var arr = JsonDocument.Parse(json).RootElement;
            var all = new List<(string, decimal, decimal)>();
            foreach (var e in arr.EnumerateArray())
            {
                string sym = e.GetProperty("symbol").GetString() ?? "";
                if (!sym.EndsWith("USDT")) continue;
                if (!decimal.TryParse(e.GetProperty("priceChangePercent").GetString(), CultureInfo.InvariantCulture, out var ch)) continue;
                if (!decimal.TryParse(e.GetProperty("quoteVolume").GetString(), CultureInfo.InvariantCulture, out var qv)) continue;
                if (qv < 5_000_000m) continue;     // 거래대금 500만 USDT 이상
                all.Add((sym, ch, qv));
            }
            top30 = all.OrderByDescending(x => x.Item2).Take(30).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ticker fetch 실패: {ex.Message}");
            return;
        }

        Console.WriteLine($"  TOP 30 추출 완료. 1~5위:");
        for (int k = 0; k < Math.Min(5, top30.Count); k++)
            Console.WriteLine($"    {k + 1}. {top30[k].sym,-15} +{top30[k].change24h,6:F2}%  qVol=${top30[k].qVol / 1_000_000m,8:F1}M");

        // 2. 각 심볼 1m fetch
        Console.WriteLine($"\n[2] fetch 1m 1500봉 (~25h) × {top30.Count}");
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        foreach (var (sym, _, _) in top30)
        {
            idx++;
            Console.Write($"[{idx}/{top30.Count}] {sym} ");
            try
            {
                var kl = await FetchKlines1mAsync(sym, 1);
                if (kl.Count < 1440) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 3. AltMomentum 시뮬 (1m → 15m 환산)
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tpPct = 3.0m;
        const decimal slPct = 2.0m;
        const decimal slippagePct = 0.05m;
        const int maxHoldBars = 30;       // 30분
        const int cooldownBars = 30;

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            // 1m봉. 15봉 = 15분.
            if (i < 1440) return false;       // 24h 백필 필요
            // 24h 변화율 5~30%
            decimal price24h = kl[i - 1440].ClosePrice;
            if (price24h <= 0) return false;
            decimal change24h = (kl[i].ClosePrice - price24h) / price24h * 100m;
            if (change24h < 5m || change24h > 30m) return false;
            // 15m EMA20 — 1m × 300봉 → EMA20 환산
            decimal ema20 = Ema(kl, i, 300);     // 1m EMA300 ≈ 15m EMA20
            decimal ema20Prev = Ema(kl, i - 15, 300);
            if (kl[i].ClosePrice <= ema20) return false;
            if (kl[i - 15].ClosePrice > ema20Prev) return false;     // 갓 돌파
            // 거래량 (직전 5×15봉 = 75봉 평균 × 2)
            decimal volAvg = 0m;
            for (int q = i - 75; q < i; q++) volAvg += kl[q].Volume;
            volAvg /= 75m;
            if (volAvg <= 0m || kl[i].Volume * 15m < volAvg * 75m * 2m) return false;
            // RSI < 70
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 70) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        // 시뮬
        var candidates = new List<(DateTime time, string sym, int barIdx)>();
        foreach (var kv in fullData)
        {
            var kl = kv.Value; var sym = kv.Key;
            int lastFire = -1000;
            for (int i = 1450; i < kl.Count - maxHoldBars; i++)
            {
                if (i - lastFire < cooldownBars) continue;
                if (!ShouldEnter(kl, i)) continue;
                candidates.Add((kl[i].OpenTime, sym, i));
                lastFire = i;
            }
        }
        candidates.Sort((a, b) => a.time.CompareTo(b.time));

        var active = new List<DateTime>();
        decimal totalPnl = 0m;
        int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
        var perSym = new Dictionary<string, (int n, int tp, int sl, decimal pnl)>();
        foreach (var c in candidates)
        {
            active.RemoveAll(t => t <= c.time);
            if (active.Count >= maxSlots) continue;
            decimal notional = margin * LEVERAGE;
            var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
            decimal pctNet = pctRaw - (decimal)(FEE_RATE * 2m * 100m) - (slippagePct * 2m);
            decimal pnlUsd = notional * pctNet / 100m;
            totalPnl += pnlUsd;
            n++;
            if (kind == "TP") tpN++;
            else if (kind == "SL") slN++;
            else if (kind == "BE") beN++;
            else toN++;
            int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
            active.Add(fullData[c.sym][endBar].OpenTime);
            if (!perSym.ContainsKey(c.sym)) perSym[c.sym] = (0, 0, 0, 0m);
            var p = perSym[c.sym];
            perSym[c.sym] = (p.n + 1, p.tp + (kind == "TP" ? 1 : 0), p.sl + (kind == "SL" ? 1 : 0), p.pnl + pnlUsd);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% / max {maxHoldBars}분 / 마진 ${margin}×{maxSlots}슬롯×{LEVERAGE}x / cooldown {cooldownBars}분");
        Console.WriteLine();
        Console.WriteLine($"==== 24h 종합 결과 ====");
        Console.WriteLine($"  진입: {n}건  TP: {tpN}  SL: {slN}  BE: {beN}  TIMEOUT: {toN}");
        Console.WriteLine($"  PnL: ${totalPnl:F2}  ROI(시드 $400): {totalPnl / 400m * 100m:F2}%");
        Console.WriteLine();
        Console.WriteLine($"==== 심볼별 결과 (PnL desc) ====");
        Console.WriteLine($"{"심볼",-15} {"진입",4} {"TP",3} {"SL",3} {"PnL",10}");
        Console.WriteLine(new string('-', 50));
        foreach (var kv in perSym.OrderByDescending(k => k.Value.pnl))
        {
            Console.WriteLine($"{kv.Key,-15} {kv.Value.n,4} {kv.Value.tp,3} {kv.Value.sl,3} {kv.Value.pnl,9:F2}");
        }
    }

    // [v5.22.65] Daily Swing 변형 4종 — 수익성 더 높은 변형 탐색
    //   #A 베이스: 1D close>20SMA + 20SMA>50SMA + RSI 50~65 + vol×1.5 + 양봉 (현재 라이브)
    //   #B 강화: + 24h 변화율 ≥ +5% (펌프 종목 우선)
    //   #C RSI 완화: RSI 50~70 (놓치는 강한 추세 잡기)
    //   #D 거래대금 가중: 24h quoteVolume 상위 50% 만 진입 (유동성 ↑)
    //   #E B+C+D 조합 (모두 적용)
    private static async Task RunDailySwingVariantsAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.65 Daily Swing 변형 4종 — 수익성 비교");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 200m;
        const int maxSlots = 2;
        const decimal tpPct = 15m;
        const decimal slPct = 7m;
        const decimal slippagePct = 0.05m;
        const int maxHoldBars = 7;
        const decimal swingLeverage = 5m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch — {symbols.Length}개 심볼 (1D)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines1dAsync(sym, 1);
                if (kl.Count < 60) { Console.WriteLine($"skip ({kl.Count})"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 24h quoteVolume 누적 (1D 기준 = 1봉 = 24h)
        // 상위 50% 종목 동적 선정
        var dailyQVolAvg = new Dictionary<string, decimal>();
        foreach (var kv in fullData)
        {
            decimal sumQv = 0m; int cnt = 0;
            foreach (var k in kv.Value)
            {
                decimal q = k.QuoteVolume > 0 ? k.QuoteVolume : k.ClosePrice * k.Volume;
                sumQv += q; cnt++;
            }
            dailyQVolAvg[kv.Key] = cnt > 0 ? sumQv / cnt : 0m;
        }
        decimal medianQVol = dailyQVolAvg.Values.OrderByDescending(v => v).Skip(dailyQVolAvg.Count / 2).FirstOrDefault();

        // 변형 진입 조건들
        bool BaseEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 51) return false;
            decimal sma20 = 0m; for (int q = i - 19; q <= i; q++) sma20 += kl[q].ClosePrice; sma20 /= 20m;
            decimal sma50 = 0m; for (int q = i - 49; q <= i; q++) sma50 += kl[q].ClosePrice; sma50 /= 50m;
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 65.0) return false;
            decimal volAvg = 0m; for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume; volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }
        bool BEnter(List<IBinanceKline> kl, int i)   // + 24h +5%
        {
            if (!BaseEnter(kl, i)) return false;
            if (i < 1) return false;
            decimal change24h = (kl[i].ClosePrice - kl[i - 1].OpenPrice) / kl[i - 1].OpenPrice * 100m;
            return change24h >= 5m;
        }
        bool CEnter(List<IBinanceKline> kl, int i)   // RSI 50~70
        {
            if (i < 51) return false;
            decimal sma20 = 0m; for (int q = i - 19; q <= i; q++) sma20 += kl[q].ClosePrice; sma20 /= 20m;
            decimal sma50 = 0m; for (int q = i - 49; q <= i; q++) sma50 += kl[q].ClosePrice; sma50 /= 50m;
            if (kl[i].ClosePrice <= sma20) return false;
            if (sma20 <= sma50) return false;
            double rsi = CalcRsi14(kl, i);
            if (rsi < 50.0 || rsi > 70.0) return false;
            decimal volAvg = 0m; for (int q = i - 5; q <= i - 1; q++) volAvg += kl[q].Volume; volAvg /= 5m;
            if (volAvg <= 0m || kl[i].Volume < volAvg * 1.5m) return false;
            if (kl[i].ClosePrice <= kl[i].OpenPrice) return false;
            return true;
        }
        bool DEnter(string sym, List<IBinanceKline> kl, int i)   // 거래대금 가중 + base
        {
            if (!BaseEnter(kl, i)) return false;
            return dailyQVolAvg.TryGetValue(sym, out var qv) && qv >= medianQVol;
        }
        bool EEnter(string sym, List<IBinanceKline> kl, int i)   // B+C+D 조합
        {
            return CEnter(kl, i)
                && (i >= 1 && (kl[i].ClosePrice - kl[i - 1].OpenPrice) / kl[i - 1].OpenPrice * 100m >= 5m)
                && dailyQVolAvg.TryGetValue(sym, out var qv) && qv >= medianQVol;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 1.0m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, decimal mddPct) Eval(int days, Func<string, List<IBinanceKline>, int, bool> shouldEnter)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                for (int i = 51; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (!shouldEnter(sym, kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));
            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * swingLeverage;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++;
                if (kind == "TP") tpN++; else if (kind == "SL") slN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }
            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            return (totalPnl, n, tpN, slN, mddPct);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% / 마진 ${margin}×{maxSlots}슬롯×{swingLeverage}x");
        Console.WriteLine();
        int[] periods = { 180, 365 };
        foreach (int days in periods)
        {
            Console.WriteLine($"==== {days}일 결과 ====");
            Console.WriteLine($"{"변형",-30} {"진입",6} {"TP",4} {"SL",4} {"PnL",10} {"ROI",10} {"MDD%",8}");
            Console.WriteLine(new string('-', 80));
            var rA = Eval(days, (s, k, i) => BaseEnter(k, i));
            var rB = Eval(days, (s, k, i) => BEnter(k, i));
            var rC = Eval(days, (s, k, i) => CEnter(k, i));
            var rD = Eval(days, (s, k, i) => DEnter(s, k, i));
            var rE = Eval(days, (s, k, i) => EEnter(s, k, i));
            void Show(string label, (decimal pnl, int n, int tpN, int slN, decimal mddPct) r)
            {
                decimal roi = r.pnl / seed * 100m;
                Console.WriteLine($"{label,-30} {r.n,6} {r.tpN,4} {r.slN,4} {r.pnl,9:F2} {roi,9:F2}% {r.mddPct,7:F2}%");
            }
            Show("#A 베이스 (현재 라이브)", rA);
            Show("#B + 24h+5% 펌프", rB);
            Show("#C RSI 50~70 완화", rC);
            Show("#D 거래대금 상위 50%", rD);
            Show("#E B+C+D 조합", rE);
            Console.WriteLine();
        }
    }

    // [v5.22.65] B+D 조합 — EMA20 갓 돌파 + 이격도 ≤ 1% (조기 진입)
    //   사용자 보고: "WLFI 13:45 고점 진입, 12:45/13:00 진입했어야" → 5중 가드 추격 매수 구조 변경
    //   진입: 1) 15m 종가 > EMA20  2) 직전 봉 ≤ 직전 EMA20 (갓 돌파)  3) 이격도 ≤ 1%  4) vol > 5봉 평균 × 2  5) RSI < 75
    //   진짜 모델 (수수료 0.08% + 슬리피지 0.10% + 미체결 PnL + MDD)
    private static async Task RunEma20BreakTightAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.65 검증 — EMA20 갓 돌파 + 이격도 ≤ 1% + vol×2 + RSI<75");
        Console.WriteLine("  WLFI 같은 추격 매수 회피 → 조기 진입 효과 검증");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tpPct = 1.0m;
        const decimal slPct = 1.5m;
        const decimal slippagePct = 0.05m;
        const int fetchPages = 12;             // 180일 15m
        const int maxHoldBars = 24;            // 6h max

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (15m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, fetchPages);
                if (kl.Count < 200) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 22) return false;
            decimal ema20Now = Ema(kl, i, 20);
            decimal ema20Prev = Ema(kl, i - 1, 20);
            // 1. 종가 > EMA20
            if (kl[i].ClosePrice <= ema20Now) return false;
            // 2. 직전 봉 ≤ 직전 EMA20 (갓 돌파)
            if (kl[i - 1].ClosePrice > ema20Prev) return false;
            // 3. 이격도 ≤ 1%
            decimal distToEma = (kl[i].ClosePrice - ema20Now) / ema20Now * 100m;
            if (distToEma > 1.0m) return false;
            // 4. 거래량 > 직전 5봉 평균 × 2
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 2m) return false;
            // 5. RSI < 75
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 75) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 2;       // 30분 (15m × 2)

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 25; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();
            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay) { cum += kv.Value; if (cum > peak) peak = cum; decimal dd = peak - cum; if (dd > mdd) mdd = dd; }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% / max {maxHoldBars}봉(6h) / EMA20 갓 돌파 + 이격도≤1% + vol×2");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"TO",5} {"보유봉",8} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 105));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,7:F1} {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 105));
    }

    // [v5.22.53 라이브 v2] 처방 1+2+3 동시 적용
    //   1. SL 3%→1.5% (1:1 손익비) + Time Stop 단축 (메이저 4→2봉, 알트 8→4봉)
    //   2. 거래량 가드 ×2.0→×3.0 (진입 -40% / 수수료 -40% 기대)
    //   3. TP 1%→1.5% (TP 1건당 +$60)
    private static async Task RunLiveRealisticV2Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.54 후보 — 처방 1+2+3 동시 적용");
        Console.WriteLine("  TP+1.5% / SL-1.5% / vol×3.0 / Time Stop 메이저 2봉 / 알트 4봉");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal majorMargin = 150m;
        const decimal altMargin = 100m;
        const int majorSlot = 1;
        const int altSlot = 2;
        const decimal tpPct = 1.5m;
        const decimal slPct = 1.5m;
        const decimal slippagePct = 0.05m;
        const int majorWinBars = 2;        // 30분
        const int altWinBars = 4;          // 60분
        const int fetchPages = 12;
        const decimal volMult = 3.0m;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼 (15m)]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlines15mAsync(sym, fetchPages);
                if (kl.Count < 200) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 21) return false;
            decimal ema20 = Ema(kl, i, 20);
            if (kl[i].ClosePrice <= ema20) return false;
            double sum = 0;
            for (int q = i - 19; q <= i; q++) sum += (double)kl[q].ClosePrice;
            double mean = sum / 20.0;
            double sq = 0;
            for (int q = i - 19; q <= i; q++) { double d = (double)kl[q].ClosePrice - mean; sq += d * d; }
            double sd = Math.Sqrt(sq / 20.0);
            decimal mid = (decimal)mean;
            decimal upper = (decimal)(mean + 2 * sd);
            decimal lower = (decimal)(mean - 2 * sd);
            if (mid <= 0) return false;
            decimal distMid = (kl[i].ClosePrice - mid) / mid * 100m;
            if (distMid > 2.5m) return false;
            decimal bbw = (upper - lower) / mid * 100m;
            if (bbw >= 5.0m) return false;
            if (kl[i].ClosePrice <= upper) return false;
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * volMult) return false;   // ×3.0
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 75) return false;
            return true;
        }

        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i, int winBars, decimal lev)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= winBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (k >= 2)
                {
                    double rsiCurr = CalcRsi14(kl, i + k);
                    double rsiPrev = CalcRsi14(kl, i + k - 1);
                    if (rsiPrev >= 80.0 && rsiCurr < rsiPrev)
                    {
                        decimal pct = (b.ClosePrice - entry) / entry * 100m;
                        if (pct * lev > 0.3m * lev) return ("RSI_EXIT", pct, k);
                    }
                }
            }
            int idxClose = Math.Min(i + winBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pctTo) < 0.3m ? "BE" : "TIMEOUT", pctTo, winBars);
        }

        (decimal pnl, int n, int tpN, int slN, int rsiN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;

            var candidates = new List<(DateTime time, string sym, bool isMajor, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                int winBars = isMajor ? majorWinBars : altWinBars;
                DateTime lastFireTime = DateTime.MinValue;
                for (int i = 25; i < kl.Count - winBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if ((kl[i].OpenTime - lastFireTime).TotalMinutes < 30) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, isMajor, i));
                    lastFireTime = kl[i].OpenTime;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var majorActive = new List<DateTime>();
            var altActive = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, rsiN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                majorActive.RemoveAll(t => t <= c.time);
                altActive.RemoveAll(t => t <= c.time);
                bool slotOk = c.isMajor ? majorActive.Count < majorSlot : altActive.Count < altSlot;
                if (!slotOk) continue;

                int winBars = c.isMajor ? majorWinBars : altWinBars;
                decimal margin = c.isMajor ? majorMargin : altMargin;
                decimal notional = margin * LEVERAGE;

                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx, winBars, LEVERAGE);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "RSI_EXIT") rsiN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                DateTime endTime = fullData[c.sym][endBar].OpenTime;
                if (c.isMajor) majorActive.Add(endTime); else altActive.Add(endTime);

                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, rsiN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  TP+{tpPct}% / SL-{slPct}% (1:1) / vol×{volMult} / 메이저 {majorWinBars}봉(30분) / 알트 {altWinBars}봉(60분)");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"RSI",5} {"BE",5} {"타임",5} {"평균보유",10} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 110));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.rsiN,5} {r.beN,5} {r.toN,5} {r.avgHold,9:F1}봉 {r.pnl,9:F2} {roi,9:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 110));
    }

    // [v5.22.53 SMC 1단계] FVG 가드 백테스트 — 현 v5.22.52 5중 가드 + FVG 가드
    //   FVG (Fair Value Gap) 정의:
    //     Bearish FVG = 봉 i-2의 low > 봉 i의 high  (impulse 캔들 i-1로 갭다운)
    //                   → 가격이 갭 메우러 다시 올라올 가능성 = 저항대
    //   가드 룰: 현재가 위 N봉 이내 미충족 Bearish FVG 존재 → LONG 진입 차단
    //   비교: 가드 있음 vs 없음 — 180일 진짜 모델 (수수료+슬리피지+미체결PnL)
    private static async Task RunSmcFvgAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  SMC 1단계 — FVG 가드 백테스트 (Bearish FVG 위 진입 차단)");
        Console.WriteLine("  베이스: v5.22.52 5중 가드 (15m EMA20 + 이격도 + BBW + 돌파+vol×2 + RSI<75)");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;
        const int maxSlots = 3;
        const decimal tpPct = 2.0m;
        const decimal slPct = 1.5m;
        const int winBars = 24;            // 2h 강제 청산 (5m × 24)
        const decimal slippagePct = 0.05m;
        const int fetchPages = 36;         // 180일
        const int fvgLookback = 20;        // FVG 탐색 윈도우

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 5중 가드 진입 조건 (v5.22.52 라이브 동일) — 5m kline 기준
        bool ShouldEnter(List<IBinanceKline> kl, int i)
        {
            if (i < 25) return false;
            // 1. EMA20 5봉 상승 (백테스트 표현 — 라이브는 15m close > 15m EMA20 인데 5m로 근사)
            decimal e1 = Ema(kl, i, 20);
            decimal e0 = Ema(kl, i - 5, 20);
            if (e1 <= e0) return false;
            // 종가 > EMA20
            if (kl[i].ClosePrice <= e1) return false;
            // 2. BB(20,2) 이격도 / BBW / 상단 돌파
            double sum = 0;
            for (int q = i - 19; q <= i; q++) sum += (double)kl[q].ClosePrice;
            double mean = sum / 20.0;
            double sq = 0;
            for (int q = i - 19; q <= i; q++) { double d = (double)kl[q].ClosePrice - mean; sq += d * d; }
            double sd = Math.Sqrt(sq / 20.0);
            decimal mid = (decimal)mean;
            decimal upper = (decimal)(mean + 2 * sd);
            decimal lower = (decimal)(mean - 2 * sd);
            if (mid <= 0) return false;
            decimal distMid = (kl[i].ClosePrice - mid) / mid * 100m;
            if (distMid > 2.5m) return false;
            decimal bbw = (upper - lower) / mid * 100m;
            if (bbw >= 5.0m) return false;
            if (kl[i].ClosePrice <= upper) return false;
            // 3. 돌파 봉 거래량 > 직전 5봉 평균 × 2
            decimal volAvg5 = 0m;
            for (int q = i - 5; q <= i - 1; q++) volAvg5 += kl[q].Volume;
            volAvg5 /= 5m;
            if (volAvg5 <= 0m || kl[i].Volume < volAvg5 * 2m) return false;
            // 4. RSI < 75
            double rsi = CalcRsi14(kl, i);
            if (rsi >= 75) return false;
            return true;
        }

        // FVG 가드 — i 시점 기준 직전 lookback 봉 내 미충족 Bearish FVG 가 현재가 위에 존재?
        //   Bearish FVG: kl[k-2].LowPrice > kl[k].HighPrice (k-2..k 3봉으로 형성)
        //   현재가 이상에 있어야 저항으로 작용 (가격이 메우러 다시 올라가야 함)
        //   미충족 = i 시점까지 가격이 갭 영역에 진입한 적 없음
        bool HasBearishFvgAbove(List<IBinanceKline> kl, int i)
        {
            decimal curPx = kl[i].ClosePrice;
            int from = Math.Max(2, i - fvgLookback);
            for (int k = from; k <= i - 1; k++)
            {
                if (k - 2 < 0) continue;
                decimal gapTop = kl[k - 2].LowPrice;
                decimal gapBot = kl[k].HighPrice;
                if (gapTop <= gapBot) continue;          // FVG 없음
                if (gapBot <= curPx) continue;           // 갭이 현재가 위에 있어야 저항
                // 미충족 검사: k+1 ~ i 봉 중 갭 영역(gapBot..gapTop) 진입했나?
                bool filled = false;
                for (int q = k + 1; q <= i; q++)
                {
                    if (kl[q].HighPrice >= gapBot)        // 갭 하단 터치 = 메움 시작
                    {
                        filled = true;
                        break;
                    }
                }
                if (!filled) return true;                 // 미충족 + 위에 있음 = 저항
            }
            return false;
        }

        (string kind, decimal pctRaw) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= winBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct);
                if (b.HighPrice >= tpPx) return ("TP", tpPct);
                if (b.LowPrice <= slPx) return ("SL", -slPct);
            }
            int idxClose = Math.Min(i + winBars, kl.Count - 1);
            decimal pct = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return (Math.Abs(pct) < 0.5m ? "BE" : "TIMEOUT", pct);
        }

        // 두 모드 동시 시뮬: with/without FVG 가드
        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct) Eval(int days, bool useFvg)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 6;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 50; i < kl.Count - winBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(kl, i)) continue;
                    if (useFvg && HasBearishFvgAbove(kl, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + winBars / 2, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x | TP+{tpPct}% / SL-{slPct}% / win{winBars}봉 / FVG lookback {fvgLookback}봉");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"모드",-12} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"타임",5} {"PnL",10} {"ROI",10} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 110));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var rA = Eval(days, false);   // 가드 없음
            var rB = Eval(days, true);    // FVG 가드 ON
            decimal roiA = rA.pnl / seed * 100m;
            decimal roiB = rB.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {"baseline",-12} {rA.n,6} {rA.tpN,5} {rA.slN,5} {rA.beN,5} {rA.toN,5} {rA.pnl,9:F2} {roiA,9:F2}% {rA.mdd,9:F2} {rA.mddPct,7:F2}%");
            Console.WriteLine($"{days,-7}일 {"+FVG가드",-12} {rB.n,6} {rB.tpN,5} {rB.slN,5} {rB.beN,5} {rB.toN,5} {rB.pnl,9:F2} {roiB,9:F2}% {rB.mdd,9:F2} {rB.mddPct,7:F2}%");
            decimal deltaPct = (rB.pnl - rA.pnl);
            Console.WriteLine($"        {"Δ",-12} {rB.n - rA.n,+6} {"",5} {"",5} {"",5} {"",5} {deltaPct,+9:F2}");
            Console.WriteLine();
        }
        Console.WriteLine("[해석] FVG 가드가 PnL 개선 / 진입 감소 비율 확인 — 흑자 전환 시 라이브 적용 후보");
    }

    // [v5.22.51] 사용자 청정 로직 — "단순함이 복잡함을 이긴다"
    //   1. 스캐너:  최근 24h quoteVolume 상위 15개 종목만 (시점별 동적)
    //   2. 추세 가드: 15m EMA50 위에 있을 때만 LONG
    //   3. 진입:   5m vol[i] >= vol[i-1] × 1.5  AND  high[i] > 직전 5봉 고점
    //   4. 강제 퇴출: 6봉(30분) 내 수익 없으면 본절 탈출
    //   진짜 모델 (수수료 0.08% + 슬리피지 0.10% + 미체결 PnL + MDD)
    private static async Task RunCleanLogicAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.51 청정 로직 — \"단순함이 복잡함을 이긴다\"");
        Console.WriteLine("  스캐너(top15 거래대금) → 15m EMA50 위 → 5m Vol×1.5 + 5봉고점돌파");
        Console.WriteLine("  → 30분 내 수익 없으면 본절 탈출");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal margin = 100m;        // 슬롯당 마진
        const int maxSlots = 3;             // 동시 진입 3개
        const decimal tpPct = 2.0m;         // 익절 +2%
        const decimal slPct = 1.5m;         // 손절 -1.5%
        const int timeCutBars = 6;          // 30분 (5m × 6)
        const int maxHoldBars = 24;         // 2시간 강제 청산
        const int breakoutLook = 5;         // 직전 N봉 고점 돌파
        const decimal slippagePct = 0.05m;  // 0.05% × 2 = 0.10%
        const int fetchPages = 36;          // 180일

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        // 15m EMA50 시리즈 사전 계산 — 5m 3개 → 15m 1개로 묶고 종가 EMA50
        // emaAt[symbol][5m_index] = 해당 시점 직전 완성된 15m 캔들의 EMA50 값
        var emaAt = new Dictionary<string, decimal[]>();
        var qVolAt = new Dictionary<string, decimal[]>();   // 24h 누적 거래대금 (288봉 합)
        foreach (var kv in fullData)
        {
            var kl = kv.Value;
            int n = kl.Count;
            var ema = new decimal[n];
            var qv = new decimal[n];

            // 15m 종가 시리즈 만들기 (3개씩 묶음)
            var closes15m = new List<decimal>(n / 3 + 1);
            var idx15m = new List<int>(n / 3 + 1);   // 해당 15m 캔들이 닫힌 5m 인덱스
            for (int i = 2; i < n; i += 3)
            {
                closes15m.Add(kl[i].ClosePrice);
                idx15m.Add(i);
            }
            // EMA50 (15m)
            var ema15 = new decimal[closes15m.Count];
            decimal kEma = 2m / (50m + 1m);
            decimal prev = closes15m.Count > 0 ? closes15m[0] : 0m;
            for (int j = 0; j < closes15m.Count; j++)
            {
                prev = closes15m[j] * kEma + prev * (1 - kEma);
                ema15[j] = prev;
            }
            // 각 5m 인덱스 i에 대해, i보다 같거나 이전에 닫힌 마지막 15m 의 ema 값 매핑
            int j15 = -1;
            for (int i = 0; i < n; i++)
            {
                while (j15 + 1 < idx15m.Count && idx15m[j15 + 1] <= i) j15++;
                ema[i] = j15 >= 0 ? ema15[j15] : 0m;
            }
            // 24h quoteVolume 슬라이딩 합 — quoteVolume 미존재 시 close*volume 으로 근사
            decimal sum = 0m;
            const int win24h = 288;
            for (int i = 0; i < n; i++)
            {
                decimal q = kl[i].QuoteVolume > 0 ? kl[i].QuoteVolume : kl[i].ClosePrice * kl[i].Volume;
                sum += q;
                if (i >= win24h)
                {
                    decimal qOld = kl[i - win24h].QuoteVolume > 0
                        ? kl[i - win24h].QuoteVolume
                        : kl[i - win24h].ClosePrice * kl[i - win24h].Volume;
                    sum -= qOld;
                }
                qv[i] = sum;
            }
            emaAt[kv.Key] = ema;
            qVolAt[kv.Key] = qv;
        }

        // 시점별 top-15 거래대금 — 매 5m 마다 다시 계산하면 느림. 1시간(12봉) 단위로 갱신.
        // 갱신 시점에 모든 심볼의 qVolAt[i] 기준으로 상위 15 추출.
        // 결과: barIdxBucket → HashSet<string>
        // 단순화: 각 심볼 캔들 시간을 기준으로 동기화하기 어려우므로 시간 기반 버킷 사용
        const int rescanBars = 12;   // 1시간마다 스캐너 갱신
        var symList = fullData.Keys.ToList();
        // 모든 심볼 공통 시간축 (UTC 5m 그리드) — 첫/마지막 시각 교집합
        DateTime tStart = fullData.Values.Max(v => v[0].OpenTime);
        DateTime tEnd = fullData.Values.Min(v => v[^1].OpenTime);
        // 심볼별 OpenTime → 인덱스 dict
        var timeIdx = new Dictionary<string, Dictionary<DateTime, int>>();
        foreach (var kv in fullData)
        {
            var d = new Dictionary<DateTime, int>(kv.Value.Count);
            for (int i = 0; i < kv.Value.Count; i++) d[kv.Value[i].OpenTime] = i;
            timeIdx[kv.Key] = d;
        }
        // top15 캐시: 갱신 시각 → 심볼 셋
        var topCache = new SortedDictionary<DateTime, HashSet<string>>();
        for (DateTime t = tStart; t <= tEnd; t = t.AddMinutes(5 * rescanBars))
        {
            var ranked = new List<(string sym, decimal qv)>();
            foreach (var s in symList)
            {
                if (!timeIdx[s].TryGetValue(t, out int i)) continue;
                ranked.Add((s, qVolAt[s][i]));
            }
            var top = ranked.OrderByDescending(r => r.qv).Take(15).Select(r => r.sym).ToHashSet();
            topCache[t] = top;
        }
        HashSet<string> TopAt(DateTime t)
        {
            // 가장 최근(이전) 갱신 시각의 캐시 반환
            DateTime k = t;
            // 5분 단위 + rescanBars 정렬
            int totalMin = (int)(t - tStart).TotalMinutes;
            int bucket = totalMin / (5 * rescanBars);
            DateTime kk = tStart.AddMinutes(bucket * 5 * rescanBars);
            return topCache.TryGetValue(kk, out var set) ? set : new HashSet<string>();
        }

        bool ShouldEnter(string sym, int i)
        {
            var kl = fullData[sym];
            if (i < breakoutLook + 1) return false;
            // 추세 가드: 15m EMA50 위
            if (emaAt[sym][i] <= 0m) return false;
            if (kl[i].ClosePrice <= emaAt[sym][i]) return false;
            // 거래량 폭증
            if (kl[i - 1].Volume <= 0m) return false;
            if (kl[i].Volume < kl[i - 1].Volume * 1.5m) return false;
            // 직전 5봉 고점 돌파
            decimal prevHigh = 0m;
            for (int j = i - breakoutLook; j <= i - 1; j++)
                if (kl[j].HighPrice > prevHigh) prevHigh = kl[j].HighPrice;
            if (kl[i].HighPrice <= prevHigh) return false;
            // 스캐너 가드
            if (!TopAt(kl[i].OpenTime).Contains(sym)) return false;
            return true;
        }

        // 결과: TP / SL / BE(본절) / TIMEOUT
        (string kind, decimal pctRaw, int holdBars) Outcome(List<IBinanceKline> kl, int i)
        {
            decimal entry = kl[i].ClosePrice;
            decimal tpPx = entry * (1 + tpPct / 100m);
            decimal slPx = entry * (1 - slPct / 100m);
            for (int k = 1; k <= maxHoldBars && i + k < kl.Count; k++)
            {
                var b = kl[i + k];
                // 30분 본절 탈출 — 도달 시 현재가 <= entry 면 즉시 종가로 탈출
                if (k == timeCutBars)
                {
                    if (b.ClosePrice <= entry)
                    {
                        decimal pct = (b.ClosePrice - entry) / entry * 100m;
                        return ("BE", pct, k);
                    }
                }
                if (b.HighPrice >= tpPx && b.LowPrice <= slPx) return ("SL", -slPct, k);
                if (b.HighPrice >= tpPx) return ("TP", tpPct, k);
                if (b.LowPrice <= slPx) return ("SL", -slPct, k);
            }
            int idxClose = Math.Min(i + maxHoldBars, kl.Count - 1);
            decimal pctTo = (kl[idxClose].ClosePrice - entry) / entry * 100m;
            return ("TIMEOUT", pctTo, maxHoldBars);
        }

        (decimal pnl, int n, int tpN, int slN, int beN, int toN, decimal mdd, decimal mddPct, decimal avgHold)
            Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal feeRate = FEE_RATE;
            const int cooldownBars = 6;

            var candidates = new List<(DateTime time, string sym, int barIdx)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                int lastFire = -1000;
                for (int i = 50; i < kl.Count - maxHoldBars; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    if (!ShouldEnter(sym, i)) continue;
                    candidates.Add((kl[i].OpenTime, sym, i));
                    lastFire = i;
                }
            }
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            var active = new List<DateTime>();
            decimal totalPnl = 0m;
            int n = 0, tpN = 0, slN = 0, beN = 0, toN = 0;
            int holdSum = 0;
            var byDay = new SortedDictionary<DateTime, decimal>();

            foreach (var c in candidates)
            {
                active.RemoveAll(t => t <= c.time);
                if (active.Count >= maxSlots) continue;
                decimal notional = margin * LEVERAGE;
                var (kind, pctRaw, hold) = Outcome(fullData[c.sym], c.barIdx);
                decimal pctNet = pctRaw - (decimal)(feeRate * 2m * 100m) - (slippagePct * 2m);
                decimal pnlUsd = notional * pctNet / 100m;
                totalPnl += pnlUsd;
                n++; holdSum += hold;
                if (kind == "TP") tpN++;
                else if (kind == "SL") slN++;
                else if (kind == "BE") beN++;
                else toN++;
                int endBar = Math.Min(c.barIdx + hold, fullData[c.sym].Count - 1);
                active.Add(fullData[c.sym][endBar].OpenTime);
                DateTime day = c.time.Date;
                byDay[day] = byDay.TryGetValue(day, out var v) ? v + pnlUsd : pnlUsd;
            }

            decimal cum = 0m, peak = 0m, mdd = 0m;
            foreach (var kv in byDay)
            {
                cum += kv.Value;
                if (cum > peak) peak = cum;
                decimal dd = peak - cum;
                if (dd > mdd) mdd = dd;
            }
            decimal mddPct = peak > 0 ? mdd / (seed + peak) * 100m : 0m;
            decimal avgHoldBars = n > 0 ? (decimal)holdSum / n : 0m;
            return (totalPnl, n, tpN, slN, beN, toN, mdd, mddPct, avgHoldBars);
        }

        Console.WriteLine();
        Console.WriteLine($"  마진 ${margin}/슬롯 × {maxSlots}슬롯 × {LEVERAGE:F0}x = max notional ${margin * maxSlots * LEVERAGE:F0}");
        Console.WriteLine($"  TP +{tpPct}% / SL -{slPct}% / 본절컷 {timeCutBars}봉(30분) / 강제청산 {maxHoldBars}봉(2h)");
        Console.WriteLine();
        Console.WriteLine($"{"기간",-7} {"진입",6} {"TP",5} {"SL",5} {"BE",5} {"타임",5} {"평균보유",10} {"PnL",10} {"ROI(시드$400)",16} {"MDD$",10} {"MDD%",8}");
        Console.WriteLine(new string('-', 110));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var r = Eval(days);
            decimal roi = r.pnl / seed * 100m;
            Console.WriteLine($"{days,-7}일 {r.n,6} {r.tpN,5} {r.slN,5} {r.beN,5} {r.toN,5} {r.avgHold,9:F1}봉 {r.pnl,9:F2} {roi,15:F2}% {r.mdd,9:F2} {r.mddPct,7:F2}%");
        }
        Console.WriteLine(new string('-', 110));
        Console.WriteLine();
        Console.WriteLine("[해석] TP=익절도달 / SL=손절도달 / BE=30분 본절컷 / 타임=2h 강제청산");
        Console.WriteLine("       수수료 0.08% + 슬리피지 0.10% = 라운드트립 0.18% 차감");
        Console.WriteLine("       스캐너 갱신 1시간(12봉) / 동시 진입 3슬롯 / 쿨다운 6봉");
    }

    // [v5.22.41] 사용자 실제 설정 시뮬 — 시드 $400, 마진 메이저 $150/슬롯 1, 알트 $100/슬롯 2
    //   슬롯 제한: 메이저 활성 1 초과 / 알트 활성 2 초과 시 진입 차단
    //   활성 = 진입 후 win 봉 (TP/SL 도달 시점) 동안 점유
    private static async Task RunLiveStatsV2Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.40 라이브 시뮬 — 시드 \\$400, 메이저 마진 \\$150/슬롯 1, 알트 \\$100/슬롯 2");
        Console.WriteLine("================================================================");

        const decimal seed = 400m;
        const decimal majorMargin = 150m;
        const decimal altMargin = 100m;
        const int majorSlot = 1;
        const int altSlot = 2;
        const int fetchPages = 36;

        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        // 시간 정렬된 모든 진입 후보 한꺼번에 시뮬 (슬롯 제한 시뮬)
        (int n, int w, decimal pnl) Eval(int days)
        {
            DateTime since = DateTime.UtcNow.AddDays(-days);
            // 슬롯 추적 — Major/Alt 분리
            // 활성 슬롯 [심볼별 종료 시각 (kline 인덱스)]
            // 시뮬 흐름: 모든 (sym, i) 후보 시간순 → 슬롯 통과 → win 봉까지 점유
            const int cooldownBars = 6; // 30분
            decimal feeRate = FEE_RATE;

            // 모든 후보 모음 (timeIndex, sym, isMajor)
            var candidates = new List<(DateTime time, string sym, bool isMajor, int barIdx, decimal entryPrice, decimal high, decimal low)>();
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                int win = isMajor ? 12 : 24;
                int lastFire = -1000;
                for (int i = 50; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    bool fire = isMajor
                        ? LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice)
                        : LiveAltEvaluator.ShouldEnterLong(kl, i);
                    if (!fire) continue;
                    candidates.Add((kl[i].OpenTime, sym, isMajor, i, kl[i].ClosePrice, 0m, 0m));
                    lastFire = i;
                }
            }
            // 시간 정렬
            candidates.Sort((a, b) => a.time.CompareTo(b.time));

            // 슬롯 시뮬 — Major/Alt 별 활성 종료 시각 풀
            var majorActive = new List<DateTime>();  // 각 활성 포지션 종료 시각
            var altActive = new List<DateTime>();
            int totalN = 0, totalW = 0; decimal totalPnl = 0m;

            foreach (var c in candidates)
            {
                // 활성 슬롯 정리 (현재 시각 ≥ 종료시각인 거 제거)
                majorActive.RemoveAll(t => t <= c.time);
                altActive.RemoveAll(t => t <= c.time);

                bool slotOk = c.isMajor ? majorActive.Count < majorSlot : altActive.Count < altSlot;
                if (!slotOk) continue;

                // TP/SL 평가
                decimal tpPct = c.isMajor ? 0.5m : 1.0m;
                decimal slPct = c.isMajor ? 1.5m : 3.0m;
                int win = c.isMajor ? 12 : 24;
                decimal margin = c.isMajor ? majorMargin : altMargin;
                decimal lev = LEVERAGE;
                decimal notional = margin * lev;
                decimal trigFee = notional * feeRate * 2m;
                decimal tpUsd = notional * tpPct / 100m - trigFee;
                decimal slUsd = notional * slPct / 100m + trigFee;

                var kl = fullData[c.sym];
                var (tp, sl) = OutcomeIn(kl, c.barIdx, tpPct, slPct, win);
                if (!(tp || sl))
                {
                    // 미체결 — win 봉까지 점유 후 청산 (PnL 0)
                    DateTime closeT = kl[Math.Min(c.barIdx + win, kl.Count - 1)].OpenTime;
                    if (c.isMajor) majorActive.Add(closeT); else altActive.Add(closeT);
                    continue;
                }

                totalN++;
                if (tp) { totalW++; totalPnl += tpUsd; } else { totalPnl -= slUsd; }
                // 종료 시각 = TP/SL 도달 봉 — 단순화: c.barIdx + win/2 (평균 절반)
                int endBar = Math.Min(c.barIdx + win / 2, kl.Count - 1);
                DateTime endTime = kl[endBar].OpenTime;
                if (c.isMajor) majorActive.Add(endTime); else altActive.Add(endTime);
            }
            return (totalN, totalW, totalPnl);
        }

        // 메이저 / 알트 분리 통계는 슬롯 시뮬과 별개 — 독립 평가도 추가로
        (int n, int w, decimal pnl) EvalCategory(int days, bool majorMode)
        {
            int totalN = 0, totalW = 0; decimal totalPnl = 0m;
            DateTime since = DateTime.UtcNow.AddDays(-days);
            const int cooldownBars = 6;
            decimal margin = majorMode ? majorMargin : altMargin;
            decimal lev = LEVERAGE;
            decimal notional = margin * lev;
            decimal trigFee = notional * FEE_RATE * 2m;
            decimal tpPct = majorMode ? 0.5m : 1.0m;
            decimal slPct = majorMode ? 1.5m : 3.0m;
            int win = majorMode ? 12 : 24;
            decimal tpUsd = notional * tpPct / 100m - trigFee;
            decimal slUsd = notional * slPct / 100m + trigFee;

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorMode != isMajor) continue;
                int lastFire = -1000;
                for (int i = 50; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    bool fire = majorMode
                        ? LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice)
                        : LiveAltEvaluator.ShouldEnterLong(kl, i);
                    if (!fire) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                    if (!(tp || sl)) continue;
                    totalN++;
                    if (tp) { totalW++; totalPnl += tpUsd; } else { totalPnl -= slUsd; }
                    lastFire = i;
                }
            }
            return (totalN, totalW, totalPnl);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-6} {"카테",-7} {"진입",6} {"승",6} {"승률",8} {"PnL($)",10} {"ROI%(시드$400)",16}");
        Console.WriteLine(new string('-', 80));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            // 슬롯 미적용 (raw — 모든 신호 진입)
            var maj = EvalCategory(days, true);
            var alt = EvalCategory(days, false);
            // 슬롯 적용 (실제 봇 시뮬)
            var slot = Eval(days);

            double majWr = maj.n > 0 ? maj.w * 100.0 / maj.n : 0;
            double altWr = alt.n > 0 ? alt.w * 100.0 / alt.n : 0;
            double slotWr = slot.n > 0 ? slot.w * 100.0 / slot.n : 0;
            decimal majRoi = maj.pnl / seed * 100m;
            decimal altRoi = alt.pnl / seed * 100m;
            decimal slotRoi = slot.pnl / seed * 100m;

            Console.WriteLine($"{days,-6}일 {"메이저",-7} {maj.n,6} {maj.w,6} {majWr,7:F2}% {maj.pnl,9:F2} {majRoi,15:F2}%");
            Console.WriteLine($"{days,-6}일 {"알트",-7} {alt.n,6} {alt.w,6} {altWr,7:F2}% {alt.pnl,9:F2} {altRoi,15:F2}%");
            Console.WriteLine($"{days,-6}일 {"★슬롯적용",-7} {slot.n,6} {slot.w,6} {slotWr,7:F2}% {slot.pnl,9:F2} {slotRoi,15:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 80));
        Console.WriteLine("[해석] '슬롯적용' = 메이저 1 / 알트 2 동시 활성 제한 시뮬 (실제 봇 결과)");
    }

    // [v5.22.40] 라이브 v5.22.40 진입 로직 100% 동일 — 1/10/30/60/90/180일
    //   메이저: AnalyzeMajorSimpleAsync (EMA20 5봉↑ + RSI<65 + M15RangePos 60~85%)
    //   알트:   AnalyzeAltSimpleTriggersAsync (EMA20 5봉↑ + RSI<65 + (BBW<1.5% 상단돌파 OR 5봉중 4봉워킹))
    //   TP/SL:  메이저 0.5%/1.5%/win 12, 알트 1%/3%/win 24
    //   30분 cooldown (5분봉 6개)
    private static async Task RunLiveStatsAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.40 라이브 100% 동일 — 1/10/30/60/90/180일 × 메이저/알트");
        Console.WriteLine("================================================================");

        const decimal seed = 1000m;
        const int fetchPages = 36;
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        (int n, int w, decimal pnl) Eval(int days, bool majorMode)
        {
            int totalN = 0, totalW = 0; decimal totalPnl = 0m;
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal tpPct = majorMode ? 0.5m : 1.0m;
            decimal slPct = majorMode ? 1.5m : 3.0m;
            int win = majorMode ? 12 : 24;
            decimal trigNotional = NotionalFor(majorMode ? "MAJOR" : "SQUEEZE");
            decimal trigFee = trigNotional * FEE_RATE * 2m;
            decimal tpUsd = trigNotional * tpPct / 100m - trigFee;
            decimal slUsd = trigNotional * slPct / 100m + trigFee;
            const int cooldownBars = 6; // 30분 = 5분봉 6개

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorMode && !isMajor) continue;
                if (!majorMode && isMajor) continue;
                int lastFire = -1000;

                for (int i = 50; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    bool fire = majorMode
                        ? LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice)
                        : LiveAltEvaluator.ShouldEnterLong(kl, i);
                    if (!fire) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                    if (!(tp || sl)) continue;
                    totalN++;
                    if (tp) { totalW++; totalPnl += tpUsd; } else { totalPnl -= slUsd; }
                    lastFire = i;
                }
            }
            return (totalN, totalW, totalPnl);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-6} {"카테",-6} {"진입",6} {"승",6} {"승률",8} {"PnL($)",10} {"ROI%",10}");
        Console.WriteLine(new string('-', 72));
        int[] periods = { 1, 10, 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var maj = Eval(days, true);
            double majWr = maj.n > 0 ? maj.w * 100.0 / maj.n : 0;
            decimal majRoi = maj.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"메이저",-6} {maj.n,6} {maj.w,6} {majWr,7:F2}% {maj.pnl,9:F2} {majRoi,9:F2}%");

            var alt = Eval(days, false);
            double altWr = alt.n > 0 ? alt.w * 100.0 / alt.n : 0;
            decimal altRoi = alt.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"알트",-6} {alt.n,6} {alt.w,6} {altWr,7:F2}% {alt.pnl,9:F2} {altRoi,9:F2}%");

            int totN = maj.n + alt.n; int totW = maj.w + alt.w;
            decimal totPnl = maj.pnl + alt.pnl;
            double totWr = totN > 0 ? totW * 100.0 / totN : 0;
            decimal totRoi = totPnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"합계",-6} {totN,6} {totW,6} {totWr,7:F2}% {totPnl,9:F2} {totRoi,9:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 72));
        Console.WriteLine("[해석] 라이브 v5.22.40 진입 로직 (메이저 단순 + 알트 단순) 100% 동일");
    }

    // [v5.22.39] 진짜 라이브 백테스트 — MajorCoinStrategy + AnalyzeAltSimpleTriggers 코드 그대로 이식
    //   메이저: 3 Tier OR (aiScore≥70 / aiScore≥62+모멘텀 / 반등+HigherLows+RSI>52) — 횡보필터 + 우회로직 모두 포함
    //   알트:   SQUEEZE BBW<3% + 상단돌파 / BB_WALK 5봉 중 3봉 / MID_BREAK (v5.22.38 완화)
    //   가드:   EMA20↑ + RSI<65 (알트만) — 메이저는 RSI 가드 없음 (라이브 동일)
    //   TP/SL:  메이저 0.5%/1.5%/win 12, 알트 1%/3%/win 24
    private static async Task RunLive180Async()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.39 진짜 라이브 백테스트 — Major(3 Tier+우회) + Alt(v5.22.38)");
        Console.WriteLine("  30/60/90/180일 × 메이저/알트 분리 통계");
        Console.WriteLine("================================================================");

        const decimal seed = 1000m;
        const int fetchPages = 36;
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };

        (int n, int w, decimal pnl) Eval(int days, bool majorMode)
        {
            int totalN = 0, totalW = 0; decimal totalPnl = 0m;
            DateTime since = DateTime.UtcNow.AddDays(-days);
            decimal tpPct = majorMode ? 0.5m : 1.0m;
            decimal slPct = majorMode ? 1.5m : 3.0m;
            int win = majorMode ? 12 : 24;
            decimal trigNotional = NotionalFor(majorMode ? "MAJOR" : "SQUEEZE");
            decimal trigFee = trigNotional * FEE_RATE * 2m;
            decimal tpUsd = trigNotional * tpPct / 100m - trigFee;
            decimal slUsd = trigNotional * slPct / 100m + trigFee;

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorMode && !isMajor) continue;
                if (!majorMode && isMajor) continue;
                // 30분 cooldown 시뮬 (알트 트리거에 적용. 메이저는 라이브가 5초 캐시이지만 여기선 봉 단위 1회로 단순화)
                int lastFire = -1000;
                int cooldownBars = majorMode ? 0 : 6; // 30분 = 5분봉 6개

                for (int i = 120; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (i - lastFire < cooldownBars) continue;
                    bool fire = majorMode
                        ? LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice)
                        : LiveAltEvaluator.ShouldEnterLong(kl, i);
                    if (!fire) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                    if (!(tp || sl)) continue;
                    totalN++;
                    if (tp) { totalW++; totalPnl += tpUsd; } else { totalPnl -= slUsd; }
                    lastFire = i;
                }
            }
            return (totalN, totalW, totalPnl);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-6} {"카테",-6} {"진입",6} {"승",6} {"승률",8} {"PnL($)",10} {"ROI%",10}");
        Console.WriteLine(new string('-', 72));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            var maj = Eval(days, true);
            double majWr = maj.n > 0 ? maj.w * 100.0 / maj.n : 0;
            decimal majRoi = maj.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"메이저",-6} {maj.n,6} {maj.w,6} {majWr,7:F2}% {maj.pnl,9:F2} {majRoi,9:F2}%");

            var alt = Eval(days, false);
            double altWr = alt.n > 0 ? alt.w * 100.0 / alt.n : 0;
            decimal altRoi = alt.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"알트",-6} {alt.n,6} {alt.w,6} {altWr,7:F2}% {alt.pnl,9:F2} {altRoi,9:F2}%");

            int totN = maj.n + alt.n; int totW = maj.w + alt.w;
            decimal totPnl = maj.pnl + alt.pnl;
            double totWr = totN > 0 ? totW * 100.0 / totN : 0;
            decimal totRoi = totPnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"합계",-6} {totN,6} {totW,6} {totWr,7:F2}% {totPnl,9:F2} {totRoi,9:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 72));
        Console.WriteLine("[해석] 라이브 코드 그대로 시뮬 — 메이저 3 Tier + aiScore + 우회 / 알트 v5.22.38 완화");
    }

    // [v5.22.38] 종합 통계 — 180일 fetch 후 슬라이스, 30/60/90/180일 × 메이저/알트 × 기존/완화 임계 16조합
    private static async Task RunStatsAllAsync()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  v5.22.38 종합 통계 — 30/60/90/180일 × 메이저/알트 × 기존/완화 임계");
        Console.WriteLine("================================================================");

        const decimal seed = 1000m;
        const int fetchPages = 36; // 180일치 한번 fetch
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        Console.WriteLine($"\n[fetch 180일 — {symbols.Length}개 심볼]");
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, fetchPages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }

        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };
        // 평가 함수: 기간 (마지막 N일), 메이저모드, BBW 임계, 워킹 임계, MID_BREAK 사용
        (int n, int w, decimal pnl) Eval(int days, bool majorMode, double bbwThr, int walkThr, bool useMidBreak)
        {
            int totalN = 0, totalW = 0; decimal totalPnl = 0m;
            DateTime since = DateTime.UtcNow.AddDays(-days);
            // 메이저 (TP 0.5/SL 1.5/win 12) vs 알트 (TP 1.0/SL 3.0/win 24)
            decimal tpPct = majorMode ? 0.5m : 1.0m;
            decimal slPct = majorMode ? 1.5m : 3.0m;
            int win = majorMode ? 12 : 24;
            decimal trigNotional = NotionalFor(majorMode ? "MAJOR" : "SQUEEZE");
            decimal trigFee = trigNotional * FEE_RATE * 2m;
            decimal tpUsd = trigNotional * tpPct / 100m - trigFee;
            decimal slUsd = trigNotional * slPct / 100m + trigFee;

            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                bool isMajor = majors.Contains(sym);
                if (majorMode && !isMajor) continue;
                if (!majorMode && isMajor) continue;
                for (int i = 50; i < kl.Count - win; i++)
                {
                    if (kl[i].OpenTime < since) continue;
                    if (!Ema20Rising(kl, i)) continue;
                    if (CalcRsi14(kl, i) >= 65) continue;
                    bool fire;
                    if (majorMode)
                    {
                        // 메이저: M15 30봉 위치 60~85% (백테스트 동일)
                        var pos = M15RangePos(kl, i, 30);
                        fire = i >= 30 && pos >= 60 && pos <= 85;
                    }
                    else
                    {
                        // 알트: SQUEEZE + BB_WALK + (옵션) MID_BREAK
                        bool sqz = i >= 20 && BBWidth(kl, i) < bbwThr && BBWalkUpper(kl, i);
                        bool walk = i >= 20 && BBWalkStreak(kl, i, 5) >= walkThr;
                        bool midBr = useMidBreak && i >= 22 && BbMidBreak(kl, i);
                        fire = sqz || walk || midBr;
                    }
                    if (!fire) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                    if (!(tp || sl)) continue;
                    totalN++;
                    if (tp) { totalW++; totalPnl += tpUsd; } else { totalPnl -= slUsd; }
                }
            }
            return (totalN, totalW, totalPnl);
        }

        Console.WriteLine();
        Console.WriteLine($"{"기간",-6} {"카테",-6} {"임계",-12} {"진입",6} {"승",6} {"승률",8} {"PnL($)",10} {"ROI%",10}");
        Console.WriteLine(new string('-', 78));
        int[] periods = { 30, 60, 90, 180 };
        foreach (int days in periods)
        {
            // 메이저 (임계 무관 — 메이저 트리거는 1개)
            var maj = Eval(days, true, 0, 0, false);
            double majWr = maj.n > 0 ? maj.w * 100.0 / maj.n : 0;
            decimal majRoi = maj.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"메이저",-6} {"-",-12} {maj.n,6} {maj.w,6} {majWr,7:F2}% {maj.pnl,9:F2} {majRoi,9:F2}%");

            // 알트 기존 임계 (BBW<1.5%, 워킹 4/5)
            var altOld = Eval(days, false, 1.5, 4, false);
            double altOldWr = altOld.n > 0 ? altOld.w * 100.0 / altOld.n : 0;
            decimal altOldRoi = altOld.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"알트",-6} {"기존(1.5/4)",-12} {altOld.n,6} {altOld.w,6} {altOldWr,7:F2}% {altOld.pnl,9:F2} {altOldRoi,9:F2}%");

            // 알트 완화 임계 (BBW<3.0%, 워킹 3/5, MID_BREAK 포함)
            var altNew = Eval(days, false, 3.0, 3, true);
            double altNewWr = altNew.n > 0 ? altNew.w * 100.0 / altNew.n : 0;
            decimal altNewRoi = altNew.pnl / seed * 100m;
            Console.WriteLine($"{days,-6}일 {"알트",-6} {"완화(3.0/3)",-12} {altNew.n,6} {altNew.w,6} {altNewWr,7:F2}% {altNew.pnl,9:F2} {altNewRoi,9:F2}%");
            Console.WriteLine();
        }
        Console.WriteLine(new string('-', 78));
        Console.WriteLine("[해석] 완화 임계 PnL/ROI 가 기존보다 크면 v5.22.38 배포 정당, 작거나 마이너스면 롤백");
    }

    // BB 중심선 아래→위 돌파 + 양봉 (v5.22.38 MID_BREAK 트리거)
    private static bool BbMidBreak(List<IBinanceKline> kl, int i)
    {
        if (i < 21) return false;
        // i-1 BB
        double s1 = 0;
        for (int j = i - 20; j <= i - 1; j++) s1 += (double)kl[j].ClosePrice;
        double mid1 = s1 / 20;
        // i BB
        double s2 = 0;
        for (int j = i - 19; j <= i; j++) s2 += (double)kl[j].ClosePrice;
        double mid2 = s2 / 20;
        bool wasBelow = (double)kl[i - 1].ClosePrice < mid1;
        bool nowAbove = (double)kl[i].ClosePrice > mid2;
        bool bullCandle = kl[i].ClosePrice > kl[i].OpenPrice;
        return wasBelow && nowAbove && bullCandle;
    }

    private static async Task RunDailyAsync(int pages, string label, bool altOnly = false)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine($"  v5.22.5+ {label} 일별 PnL (PUMP/SPIKE 차단, MAJOR+SQZ+BBW)");
        Console.WriteLine("================================================================");
        const decimal seed = 1000m;
        var fullData = new Dictionary<string, List<IBinanceKline>>();
        int idx = 0;
        Console.WriteLine($"\n[fetch 60일 — {symbols.Length}개 심볼]");
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
                fullData[sym] = kl;
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        var majors = new HashSet<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };
        var triggers = altOnly
            ? new (string name, Func<List<IBinanceKline>, int, string, bool> ok)[]
            {
                // 알트만 — SQUEEZE + BB_WALK 만 평가, 메이저 심볼 제외
                ("SQUEEZE", (kl, i, sym) => !majors.Contains(sym) && i >= 20 && BBWidth(kl, i) < 1.5 && BBWalkUpper(kl, i)),
                ("BB_WALK", (kl, i, sym) => !majors.Contains(sym) && i >= 20 && BBWalkStreak(kl, i, 5) >= 4),
            }
            : new (string name, Func<List<IBinanceKline>, int, string, bool> ok)[]
            {
                ("MAJOR",   (kl, i, sym) => majors.Contains(sym) && i >= 30 && Ema20Rising(kl, i)
                              && M15RangePos(kl, i, 30) is >= 60 and <= 85),
                ("SQUEEZE", (kl, i, sym) => i >= 20 && BBWidth(kl, i) < 1.5 && BBWalkUpper(kl, i)),
                ("BB_WALK", (kl, i, sym) => i >= 20 && BBWalkStreak(kl, i, 5) >= 4),
            };
        var dailyPnl = new SortedDictionary<DateTime, (int n, int w, decimal pnl)>();
        foreach (var trig in triggers)
        {
            decimal trigNotional = NotionalFor(trig.name);
            decimal trigFee = trigNotional * FEE_RATE * 2m;
            decimal tpPct, slPct; int win;
            if (trig.name == "MAJOR") { tpPct = 0.5m; slPct = 1.5m; win = 12; }
            else { tpPct = 1.0m; slPct = 3.0m; win = 24; }
            decimal tpUsd = trigNotional * tpPct / 100m - trigFee;
            decimal slUsd = trigNotional * slPct / 100m + trigFee;
            foreach (var kv in fullData)
            {
                var kl = kv.Value; var sym = kv.Key;
                for (int i = 50; i < kl.Count - win; i++)
                {
                    if (!trig.ok(kl, i, sym)) continue;
                    if (!Ema20Rising(kl, i)) continue;
                    if (CalcRsi14(kl, i) >= 65) continue;
                    var (tp, sl) = OutcomeIn(kl, i, tpPct, slPct, win);
                    if (!(tp || sl)) continue;
                    DateTime day = kl[i].OpenTime.Date;
                    var (n, w, p) = dailyPnl.TryGetValue(day, out var v) ? v : (0, 0, 0m);
                    n++;
                    if (tp) { w++; p += tpUsd; } else { p -= slUsd; }
                    dailyPnl[day] = (n, w, p);
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"날짜",-12} {"진입",6} {"승",5} {"승률",8} {"일PnL",11} {"누적PnL",12} {"누적ROI",9}");
        Console.WriteLine(new string('-', 78));
        decimal cum = 0m;
        foreach (var kv in dailyPnl)
        {
            cum += kv.Value.pnl;
            double wr = kv.Value.n > 0 ? kv.Value.w * 100.0 / kv.Value.n : 0;
            decimal roi = cum / seed * 100m;
            Console.WriteLine($"{kv.Key:yyyy-MM-dd}   {kv.Value.n,6} {kv.Value.w,5} {wr,7:F2}% {kv.Value.pnl,10:F2} {cum,11:F2} {roi,8:F2}%");
        }
        Console.WriteLine(new string('-', 78));
        int tn = dailyPnl.Sum(x => x.Value.n);
        int tw = dailyPnl.Sum(x => x.Value.w);
        Console.WriteLine($"합계: {tn}건 / 승률 {(tn > 0 ? tw * 100.0 / tn : 0):F2}% / 누적 ${cum:F2} / 시드 $1000 → ${1000m + cum:F2} ({cum / seed * 100m:F2}%)");
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
                        CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(k[6].GetInt64()).UtcDateTime,
                        // [v5.23.74] 체결강도(Volume Power) 계산용 — taker buy base volume (배열 index 9)
                        TakerBuyBaseVolume = k.GetArrayLength() > 9 ? decimal.Parse(k[9].GetString()!, CultureInfo.InvariantCulture) : 0m
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

    // ===================================================================
    // [v5.23.73] 다중지표 합의 진입 모델 — BB 단독 의존 폐기
    //   6 카테고리 (Trend / Timing / Position / Volatility / Volume / AI)
    //   진입: 점수 기반 (Full 6/6, Score≥5)
    //   AB: 각 카테고리 1개씩 제거하여 기여도 측정
    // ===================================================================
    private static void BbAtMI(List<IBinanceKline> kl, int i, out double mid, out double upper, out double lower)
    {
        mid = upper = lower = 0;
        if (i < 19) return;
        double sum = 0; for (int j = i - 19; j <= i; j++) sum += (double)kl[j].ClosePrice;
        mid = sum / 20.0;
        double sq = 0;
        for (int j = i - 19; j <= i; j++) { double d = (double)kl[j].ClosePrice - mid; sq += d * d; }
        double sd = Math.Sqrt(sq / 20.0);
        upper = mid + 2 * sd;
        lower = mid - 2 * sd;
    }

    private static (double macd, double signal, double hist) MacdAt(List<IBinanceKline> kl, int i)
    {
        if (i < 34) return (0, 0, 0);
        // EMA12, EMA26
        double mult12 = 2.0 / 13.0, mult26 = 2.0 / 27.0;
        double ema12 = (double)kl[0].ClosePrice, ema26 = (double)kl[0].ClosePrice;
        for (int q = 1; q <= i; q++)
        {
            double c = (double)kl[q].ClosePrice;
            ema12 = c * mult12 + ema12 * (1 - mult12);
            ema26 = c * mult26 + ema26 * (1 - mult26);
        }
        double macd = ema12 - ema26;
        // EMA9 of MACD (approximation: build last 30 MACD values, then EMA9)
        int seedStart = Math.Max(26, i - 30);
        double e12 = (double)kl[0].ClosePrice, e26 = (double)kl[0].ClosePrice;
        for (int q = 1; q < seedStart; q++)
        {
            double c = (double)kl[q].ClosePrice;
            e12 = c * mult12 + e12 * (1 - mult12);
            e26 = c * mult26 + e26 * (1 - mult26);
        }
        double sig = e12 - e26;
        double multSig = 2.0 / 10.0;
        for (int q = seedStart; q <= i; q++)
        {
            double c = (double)kl[q].ClosePrice;
            e12 = c * mult12 + e12 * (1 - mult12);
            e26 = c * mult26 + e26 * (1 - mult26);
            double m = e12 - e26;
            sig = m * multSig + sig * (1 - multSig);
        }
        return (macd, sig, macd - sig);
    }

    private static (double k, double d) StochAt(List<IBinanceKline> kl, int i, int period = 14)
    {
        if (i < period + 5) return (50, 50);
        double Raw(int idx)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (int p = idx - period + 1; p <= idx; p++)
            {
                if ((double)kl[p].LowPrice < low) low = (double)kl[p].LowPrice;
                if ((double)kl[p].HighPrice > high) high = (double)kl[p].HighPrice;
            }
            return high - low > 0 ? ((double)kl[idx].ClosePrice - low) / (high - low) * 100.0 : 50.0;
        }
        // Smoothed %K = SMA3 of raw K
        double[] smoothK = new double[3];
        for (int q = 0; q < 3; q++)
        {
            int idx = i - 2 + q;
            smoothK[q] = (Raw(idx) + Raw(idx - 1) + Raw(idx - 2)) / 3.0;
        }
        double finalK = smoothK[2];
        double finalD = (smoothK[0] + smoothK[1] + smoothK[2]) / 3.0;
        return (finalK, finalD);
    }

    private static double WilliamsRAt(List<IBinanceKline> kl, int i, int period = 14)
    {
        if (i < period) return -50;
        double low = double.MaxValue, high = double.MinValue;
        for (int q = i - period + 1; q <= i; q++)
        {
            if ((double)kl[q].LowPrice < low) low = (double)kl[q].LowPrice;
            if ((double)kl[q].HighPrice > high) high = (double)kl[q].HighPrice;
        }
        if (high - low <= 0) return -50;
        return (high - (double)kl[i].ClosePrice) / (high - low) * -100.0;
    }

    private static double CciAt(List<IBinanceKline> kl, int i, int period = 20)
    {
        if (i < period) return 0;
        double sum = 0;
        double[] tp = new double[period];
        for (int q = 0; q < period; q++)
        {
            int idx = i - period + 1 + q;
            tp[q] = ((double)kl[idx].HighPrice + (double)kl[idx].LowPrice + (double)kl[idx].ClosePrice) / 3.0;
            sum += tp[q];
        }
        double mean = sum / period;
        double mad = 0;
        for (int q = 0; q < period; q++) mad += Math.Abs(tp[q] - mean);
        mad /= period;
        if (mad < 1e-12) return 0;
        double currentTp = ((double)kl[i].HighPrice + (double)kl[i].LowPrice + (double)kl[i].ClosePrice) / 3.0;
        return (currentTp - mean) / (0.015 * mad);
    }

    private static bool ObvRising(List<IBinanceKline> kl, int i, int lookback = 20)
    {
        if (i < lookback + 1) return false;
        double obvStart = 0, obv = 0;
        for (int q = i - lookback; q <= i; q++)
        {
            if (q == 0) continue;
            double v = (double)kl[q].Volume;
            if (kl[q].ClosePrice > kl[q - 1].ClosePrice) obv += v;
            else if (kl[q].ClosePrice < kl[q - 1].ClosePrice) obv -= v;
            if (q == i - lookback) obvStart = obv;
        }
        return obv > obvStart;
    }

    private static (decimal high, decimal low) SwingAtMI(List<IBinanceKline> kl, int i, int lookback = 20)
    {
        decimal high = decimal.MinValue, low = decimal.MaxValue;
        int start = Math.Max(0, i - lookback + 1);
        for (int q = start; q <= i; q++)
        {
            if (kl[q].HighPrice > high) high = kl[q].HighPrice;
            if (kl[q].LowPrice < low) low = kl[q].LowPrice;
        }
        return (high, low);
    }

    private static async Task RunMultiIndicatorAsync(int days = 90)
    {
        int pages = Math.Max(1, days * 24 * 60 / 5 / BARS_PER_REQ + 1);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  [v5.23.73] 다중지표 합의 진입 모델 ({days}일 / 5m / {symbols.Length}개 심볼)");
        Console.WriteLine("  6 카테고리: Trend / Timing / Position / Volatility / Volume / AI");
        Console.WriteLine("  TP: BB 상단 도달  SL: swing low -0.3%  max hold 24봉(2h)");
        Console.WriteLine("================================================================");

        var lor = new MiniLorentzianService();
        var data = new Dictionary<string, List<IBinanceKline>>();

        Console.WriteLine($"\n[fetch {days}일 — {symbols.Length}개 심볼 (5m)]");
        int fidx = 0;
        foreach (var sym in symbols)
        {
            fidx++;
            Console.Write($"[{fidx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 500) { Console.WriteLine("skip"); continue; }
                data[sym] = kl;
                int trainEnd = (int)(kl.Count * 0.7);
                var trainSlice = kl.GetRange(0, trainEnd);
                int added = lor.BackfillFromCandles(sym, trainSlice);
                Console.WriteLine($"ok ({kl.Count} bars, KNN train {added})");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        if (data.Count == 0) { Console.WriteLine("데이터 없음"); return; }

        // 6 카테고리 정의
        bool Cat1Trend(List<IBinanceKline> kl, int i)
        {
            if (i < 200) return false;
            double e20 = (double)CalcEmaN(kl, i, 20);
            double e50 = (double)CalcEmaN(kl, i, 50);
            double e200 = (double)CalcEmaN(kl, i, 200);
            if (!(e20 > e50 && e50 > e200)) return false;
            var (macd, sig, _) = MacdAt(kl, i);
            if (macd < sig) return false;
            if (CalcADX_idx(kl, i, 14) < 20) return false;
            return true;
        }
        bool Cat2Timing(List<IBinanceKline> kl, int i)
        {
            double rsi = CalcRsi14(kl, i);
            if (rsi < 30 || rsi > 55) return false;
            var (sK, _) = StochAt(kl, i);
            var (sKp, _) = StochAt(kl, i - 1);
            if (sK < 20 || sK <= sKp) return false;
            if (WilliamsRAt(kl, i) < -80) return false;
            if (CciAt(kl, i) < -100) return false;
            return true;
        }
        bool Cat3Position(List<IBinanceKline> kl, int i)
        {
            BbAtMI(kl, i, out double mid, out _, out double low);
            double c = (double)kl[i].ClosePrice;
            if (c < low || c > mid) return false;
            for (int q = i - 3; q <= i; q++)
            {
                if (q < 19) continue;
                BbAtMI(kl, q, out _, out _, out double lQ);
                if ((double)kl[q].LowPrice <= lQ) return true;
            }
            return false;
        }
        bool Cat4Volatility(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            decimal atrNow = AtrAt(kl, i);
            decimal atrAvg = 0; int cnt = 0;
            for (int q = i - 30; q < i; q++) { atrAvg += AtrAt(kl, q); cnt++; }
            if (cnt > 0) atrAvg /= cnt;
            if (atrNow < atrAvg) return false;
            BbAtMI(kl, i, out double mid, out double up, out double low);
            if (mid <= 0) return false;
            double bw = (up - low) / mid * 100.0;
            return bw >= 0.5 && bw <= 3.0;
        }
        bool Cat5Volume(List<IBinanceKline> kl, int i)
        {
            if (VolMult(kl, i, 20) < 1.2) return false;
            if (!ObvRising(kl, i, 20)) return false;
            return true;
        }
        bool Cat6AI(string sym, List<IBinanceKline> kl, int i)
        {
            var slice = kl.GetRange(0, i + 1);
            var pred = lor.Predict(sym, slice);
            return pred.IsReady && pred.Prediction >= 1;
        }

        // Variants: Full 6/6, Score≥5, drop each cat
        var keys = new[] { "Full_6of6", "Score>=5", "noTrend", "noTiming", "noPosition", "noVolatility", "noVolume", "noAI" };
        var stats = new Dictionary<string, (int dec, int tp, decimal pnl, int holdSum)>();
        foreach (var k in keys) stats[k] = (0, 0, 0m, 0);
        var catHits = new int[6];  // 각 카테고리 단독 통과 카운트
        int barsTested = 0;

        foreach (var kv in data)
        {
            var kl = kv.Value; var sym = kv.Key;
            int trainEnd = (int)(kl.Count * 0.7);
            int lastFire = -1000;
            const int cooldown = 12;

            for (int i = trainEnd + 50; i < kl.Count - 24; i++)
            {
                if (i < 200) continue;
                if (i - lastFire < cooldown) continue;
                barsTested++;

                bool c1 = Cat1Trend(kl, i);
                bool c2 = Cat2Timing(kl, i);
                bool c3 = Cat3Position(kl, i);
                bool c4 = Cat4Volatility(kl, i);
                bool c5 = Cat5Volume(kl, i);
                bool c6 = Cat6AI(sym, kl, i);
                if (c1) catHits[0]++; if (c2) catHits[1]++; if (c3) catHits[2]++;
                if (c4) catHits[3]++; if (c5) catHits[4]++; if (c6) catHits[5]++;
                int score = (c1?1:0)+(c2?1:0)+(c3?1:0)+(c4?1:0)+(c5?1:0)+(c6?1:0);

                bool full = score == 6;
                bool s5 = score >= 5;
                bool noT = c2 && c3 && c4 && c5 && c6;
                bool noTm = c1 && c3 && c4 && c5 && c6;
                bool noP = c1 && c2 && c4 && c5 && c6;
                bool noV = c1 && c2 && c3 && c5 && c6;
                bool noVol = c1 && c2 && c3 && c4 && c6;
                bool noAI = c1 && c2 && c3 && c4 && c5;

                if (!(full || s5 || noT || noTm || noP || noV || noVol || noAI)) continue;

                BbAtMI(kl, i, out _, out double bbUp, out _);
                var (swH, swL) = SwingAtMI(kl, i, 20);
                decimal entry = kl[i].ClosePrice;
                decimal tpPx = (decimal)bbUp;
                if (tpPx <= entry) continue;
                decimal slPx = swL * 0.997m;
                if (slPx >= entry) continue;

                decimal pnlPct = 0m; bool tpHit = false;
                int hold = 0;
                for (int k = 1; k <= 24; k++)
                {
                    hold = k;
                    var b = kl[i + k];
                    if (b.LowPrice <= slPx) { pnlPct = (slPx - entry) / entry * 100m; break; }
                    if (b.HighPrice >= tpPx) { pnlPct = (tpPx - entry) / entry * 100m; tpHit = true; break; }
                    if (k == 24) pnlPct = (b.ClosePrice - entry) / entry * 100m;
                }
                decimal pnlNet = pnlPct - (decimal)(FEE_RATE * 2m * 100m);
                decimal pnlUsd = MARGIN_USD * LEVERAGE * pnlNet / 100m;

                void Add(string key, bool enter)
                {
                    if (!enter) return;
                    var s = stats[key];
                    stats[key] = (s.dec + 1, s.tp + (tpHit ? 1 : 0), s.pnl + pnlUsd, s.holdSum + hold);
                }
                Add("Full_6of6", full);
                Add("Score>=5", s5);
                Add("noTrend", noT);
                Add("noTiming", noTm);
                Add("noPosition", noP);
                Add("noVolatility", noV);
                Add("noVolume", noVol);
                Add("noAI", noAI);

                if (full || s5) lastFire = i;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  검토봉 {barsTested:N0}개  |  notional ${MARGIN_USD * LEVERAGE:F0}");
        Console.WriteLine();
        Console.WriteLine("  [카테고리 단독 시그널 발생률]");
        string[] catNames = { "Trend", "Timing", "Position", "Volatility", "Volume", "AI(KNN)" };
        for (int c = 0; c < 6; c++)
        {
            double pct = barsTested > 0 ? catHits[c] * 100.0 / barsTested : 0;
            Console.WriteLine($"    {catNames[c],-12} {catHits[c],8}건  ({pct,5:F2}%)");
        }
        Console.WriteLine();
        Console.WriteLine("  [Variant 백테스트 결과]");
        Console.WriteLine($"  {"Variant",-15} {"Entries",10} {"WR%",7} {"PnL($)",12} {"avgHold",8} {"avgPnL/trd",12}");
        Console.WriteLine("  " + new string('-', 70));
        foreach (var key in keys)
        {
            var s = stats[key];
            double wr = s.dec > 0 ? s.tp * 100.0 / s.dec : 0;
            decimal avg = s.dec > 0 ? s.pnl / s.dec : 0m;
            double avgHold = s.dec > 0 ? s.holdSum * 1.0 / s.dec : 0;
            Console.WriteLine($"  {key,-15} {s.dec,10:N0} {wr,6:F1}% {s.pnl,11:F2} {avgHold,7:F1} {avg,11:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("  [기여도 분석: Full_6of6 대비 noX 변종 ΔPnL — 큰 양수일수록 그 지표가 손실 유발]");
        var fullPnL = stats["Full_6of6"].pnl;
        var fullDec = stats["Full_6of6"].dec;
        for (int c = 0; c < 6; c++)
        {
            string key = c switch { 0 => "noTrend", 1 => "noTiming", 2 => "noPosition", 3 => "noVolatility", 4 => "noVolume", _ => "noAI" };
            var s = stats[key];
            double wr = s.dec > 0 ? s.tp * 100.0 / s.dec : 0;
            double wrFull = fullDec > 0 ? stats["Full_6of6"].tp * 100.0 / fullDec : 0;
            decimal dPnL = s.pnl - fullPnL;
            double dWR = wr - wrFull;
            string verdict = dPnL > 5m ? "필터링 도움 안 됨" : dPnL < -5m ? "필터링 효과" : "중립";
            Console.WriteLine($"    {catNames[c],-12} ΔPnL={dPnL,+9:F2}$  ΔWR={dWR,+6:F1}%  → {verdict}");
        }
    }

    // ===================================================================
    // [v5.23.74] 다중지표 FULL — VWAP + Ichimoku + HA + PSAR 추가
    //   시나리오 A (Pullback): 완화된 Trend + BB 하단/중단 + 눌림 회복
    //   시나리오 B (Trend Follow): 엄격한 Trend + BB 중단/상단 + Ichimoku/PSAR
    // ===================================================================
    private static (decimal vwap, double upper1Sigma, double lower1Sigma)
        VwapMI(List<IBinanceKline> kl, int i, int win = 288)
    {
        if (i < 20) return (kl[i].ClosePrice, (double)kl[i].ClosePrice, (double)kl[i].ClosePrice);
        int start = Math.Max(0, i - win + 1);
        decimal sumPV = 0m, sumV = 0m;
        for (int q = start; q <= i; q++)
        {
            decimal tp = (kl[q].HighPrice + kl[q].LowPrice + kl[q].ClosePrice) / 3m;
            sumPV += tp * kl[q].Volume;
            sumV += kl[q].Volume;
        }
        decimal vwap = sumV > 0 ? sumPV / sumV : kl[i].ClosePrice;
        double mean = (double)vwap;
        double sq = 0; int n = 0;
        for (int q = start; q <= i; q++)
        {
            double d = (double)kl[q].ClosePrice - mean;
            sq += d * d; n++;
        }
        double sigma = n > 0 ? Math.Sqrt(sq / n) : 0;
        return (vwap, mean + sigma, mean - sigma);
    }

    private static (decimal haOpen, decimal haClose, decimal haHigh, decimal haLow, int greenStreak)
        HeikenAshiAt(List<IBinanceKline> kl, int i)
    {
        if (i < 2) return (kl[i].OpenPrice, kl[i].ClosePrice, kl[i].HighPrice, kl[i].LowPrice, 0);
        // 처음부터 빌드 (lookback 50)
        int start = Math.Max(0, i - 50);
        decimal prevHaOpen = (kl[start].OpenPrice + kl[start].ClosePrice) / 2m;
        decimal prevHaClose = (kl[start].OpenPrice + kl[start].HighPrice + kl[start].LowPrice + kl[start].ClosePrice) / 4m;
        decimal haOpen = prevHaOpen, haClose = prevHaClose, haHigh = kl[start].HighPrice, haLow = kl[start].LowPrice;
        int streak = haClose > haOpen ? 1 : 0;
        for (int q = start + 1; q <= i; q++)
        {
            haClose = (kl[q].OpenPrice + kl[q].HighPrice + kl[q].LowPrice + kl[q].ClosePrice) / 4m;
            haOpen = (prevHaOpen + prevHaClose) / 2m;
            haHigh = Math.Max(kl[q].HighPrice, Math.Max(haOpen, haClose));
            haLow = Math.Min(kl[q].LowPrice, Math.Min(haOpen, haClose));
            if (haClose > haOpen) streak = streak >= 0 ? streak + 1 : 1;
            else streak = streak <= 0 ? streak - 1 : -1;
            prevHaOpen = haOpen; prevHaClose = haClose;
        }
        return (haOpen, haClose, haHigh, haLow, streak);
    }

    private static (decimal sar, bool bullish) PsarAt(List<IBinanceKline> kl, int i, decimal step = 0.02m, decimal maxAcc = 0.2m)
    {
        if (i < 2) return (kl[i].LowPrice, true);
        int start = Math.Max(0, i - 100);
        bool bull = kl[start + 1].ClosePrice > kl[start].ClosePrice;
        decimal sar = bull ? kl[start].LowPrice : kl[start].HighPrice;
        decimal ep = bull ? kl[start].HighPrice : kl[start].LowPrice;
        decimal af = step;
        for (int q = start + 1; q <= i; q++)
        {
            sar = sar + af * (ep - sar);
            if (bull)
            {
                if (kl[q].LowPrice < sar)
                {
                    bull = false; sar = ep; ep = kl[q].LowPrice; af = step;
                }
                else
                {
                    if (kl[q].HighPrice > ep) { ep = kl[q].HighPrice; af = Math.Min(af + step, maxAcc); }
                    sar = Math.Min(sar, Math.Min(kl[Math.Max(0, q - 1)].LowPrice, kl[Math.Max(0, q - 2)].LowPrice));
                }
            }
            else
            {
                if (kl[q].HighPrice > sar)
                {
                    bull = true; sar = ep; ep = kl[q].HighPrice; af = step;
                }
                else
                {
                    if (kl[q].LowPrice < ep) { ep = kl[q].LowPrice; af = Math.Min(af + step, maxAcc); }
                    sar = Math.Max(sar, Math.Max(kl[Math.Max(0, q - 1)].HighPrice, kl[Math.Max(0, q - 2)].HighPrice));
                }
            }
        }
        return (sar, bull);
    }

    private static async Task RunMultiIndicatorFullAsync(int days = 90)
    {
        int pages = Math.Max(1, days * 24 * 60 / 5 / BARS_PER_REQ + 1);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  [v5.23.74] 다중지표 FULL ({days}일 / 5m / {symbols.Length}개 심볼)");
        Console.WriteLine("  시나리오 A: 눌림 진입(mean-rev) — 완화 Trend + BB 하단~중단");
        Console.WriteLine("  시나리오 B: 추세 동승(trend)    — 엄격 Trend + Ichimoku + PSAR");
        Console.WriteLine("  지표: EMA/MACD/ADX/RSI/Stoch/W%R/CCI/BB/ATR/Vol/OBV/KNN/VWAP/Ichi/HA/PSAR");
        Console.WriteLine("================================================================");

        var lor = new MiniLorentzianService();
        var data = new Dictionary<string, List<IBinanceKline>>();

        Console.WriteLine($"\n[fetch {days}일 — {symbols.Length}개 심볼 (5m)]");
        int fidx = 0;
        foreach (var sym in symbols)
        {
            fidx++;
            Console.Write($"[{fidx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 500) { Console.WriteLine("skip"); continue; }
                data[sym] = kl;
                int trainEnd = (int)(kl.Count * 0.7);
                var trainSlice = kl.GetRange(0, trainEnd);
                int added = lor.BackfillFromCandles(sym, trainSlice);
                Console.WriteLine($"ok ({kl.Count} bars, KNN train {added})");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        if (data.Count == 0) { Console.WriteLine("데이터 없음"); return; }

        // === 시나리오 A: 눌림 진입 (mean reversion) ===
        bool A_Trend(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            double e20 = (double)CalcEmaN(kl, i, 20);
            double e50 = (double)CalcEmaN(kl, i, 50);
            return e20 > e50;  // 완화: 정배열만, MACD/ADX 안 봄
        }
        bool A_Timing(List<IBinanceKline> kl, int i)
        {
            double rsi = CalcRsi14(kl, i);
            if (rsi < 25 || rsi > 50) return false;
            var (sK, _) = StochAt(kl, i);
            var (sKp, _) = StochAt(kl, i - 1);
            if (sK >= 30 || sK <= sKp) return false;  // oversold 회복 시작
            if (WilliamsRAt(kl, i) > -70) return false;  // 아직 oversold
            return true;
        }
        bool A_Position(List<IBinanceKline> kl, int i)
        {
            BbAtMI(kl, i, out double mid, out _, out double low);
            double c = (double)kl[i].ClosePrice;
            if (c < low * 0.995 || c > mid) return false;  // BB 하단~중단
            var (_, _, vwapLow) = VwapMI(kl, i);
            return c <= vwapLow * 1.01;  // VWAP -1σ 근처/아래
        }
        bool A_Volatility(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            BbAtMI(kl, i, out double mid, out double up, out double low);
            if (mid <= 0) return false;
            double bw = (up - low) / mid * 100.0;
            return bw >= 0.5 && bw <= 4.0;
        }
        bool A_Volume(List<IBinanceKline> kl, int i)
        {
            return VolMult(kl, i, 20) >= 1.2 && ObvRising(kl, i, 20);
        }
        bool A_AI(string sym, List<IBinanceKline> kl, int i)
        {
            var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
            return pred.IsReady && pred.Prediction >= 1;
        }
        bool A_HA(List<IBinanceKline> kl, int i)
        {
            var (haO, haC, _, _, streak) = HeikenAshiAt(kl, i);
            return haC > haO && streak >= 1;  // 반전 양봉 1개+
        }

        // === 시나리오 B: 추세 동승 (trend follow) ===
        bool B_Trend(List<IBinanceKline> kl, int i)
        {
            if (i < 200) return false;
            double e20 = (double)CalcEmaN(kl, i, 20);
            double e50 = (double)CalcEmaN(kl, i, 50);
            double e200 = (double)CalcEmaN(kl, i, 200);
            if (!(e20 > e50 && e50 > e200)) return false;
            var (macd, sig, _) = MacdAt(kl, i);
            if (macd < sig) return false;
            if (CalcADX_idx(kl, i, 14) < 20) return false;
            return true;
        }
        bool B_Timing(List<IBinanceKline> kl, int i)
        {
            double rsi = CalcRsi14(kl, i);
            if (rsi < 45 || rsi > 65) return false;
            var (sK, sD) = StochAt(kl, i);
            return sK > 50 && sK > sD;
        }
        bool B_Position(List<IBinanceKline> kl, int i)
        {
            BbAtMI(kl, i, out double mid, out double up, out _);
            double c = (double)kl[i].ClosePrice;
            if (c <= mid || c >= up) return false;  // 중단~상단 구간
            var (vwap, _, _) = VwapMI(kl, i);
            return c > (double)vwap;
        }
        bool B_Volatility(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            decimal atrNow = AtrAt(kl, i);
            decimal atrAvg = 0; int cnt = 0;
            for (int q = i - 30; q < i; q++) { atrAvg += AtrAt(kl, q); cnt++; }
            if (cnt > 0) atrAvg /= cnt;
            if (atrNow < atrAvg) return false;
            BbAtMI(kl, i, out double mid, out double up, out double low);
            if (mid <= 0) return false;
            double bw = (up - low) / mid * 100.0;
            return bw >= 1.0;
        }
        bool B_Volume(List<IBinanceKline> kl, int i)
        {
            return VolMult(kl, i, 20) >= 1.5 && ObvRising(kl, i, 20);
        }
        bool B_AI(string sym, List<IBinanceKline> kl, int i)
        {
            var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
            return pred.IsReady && pred.Prediction >= 2;  // 더 엄격
        }
        bool B_Ichimoku(List<IBinanceKline> kl, int i)
        {
            if (i < 52) return false;
            var (tk, kj, sA, sB, _) = IchimokuAt(kl, i);
            decimal top = Math.Max(sA, sB);
            return kl[i].ClosePrice > top && tk > kj;
        }
        bool B_PSAR(List<IBinanceKline> kl, int i)
        {
            var (sar, bull) = PsarAt(kl, i);
            return bull && kl[i].ClosePrice > sar;
        }

        // 시나리오 A: 7 카테고리 (Trend/Timing/Position/Vol/Volume/AI/HA)
        // 시나리오 B: 8 카테고리 (Trend/Timing/Position/Vol/Volume/AI/Ichimoku/PSAR)
        // Variant: Full, Score>=N-1, drop each
        var aKeys = new[] { "A_Full", "A_Score-1",
            "A_noTrend", "A_noTiming", "A_noPosition", "A_noVolatility", "A_noVolume", "A_noAI", "A_noHA" };
        var bKeys = new[] { "B_Full", "B_Score-1",
            "B_noTrend", "B_noTiming", "B_noPosition", "B_noVolatility", "B_noVolume", "B_noAI", "B_noIchi", "B_noPSAR" };

        var stats = new Dictionary<string, (int dec, int tp, decimal pnl, int hold)>();
        foreach (var k in aKeys) stats[k] = (0, 0, 0m, 0);
        foreach (var k in bKeys) stats[k] = (0, 0, 0m, 0);
        var aCat = new int[7]; var bCat = new int[8];
        int barsTested = 0;

        foreach (var kv in data)
        {
            var kl = kv.Value; var sym = kv.Key;
            int trainEnd = (int)(kl.Count * 0.7);
            int lastFireA = -1000, lastFireB = -1000;
            const int cooldown = 12;

            for (int i = trainEnd + 50; i < kl.Count - 36; i++)
            {
                if (i < 200) continue;
                barsTested++;

                // === Scenario A ===
                if (i - lastFireA >= cooldown)
                {
                    bool a1 = A_Trend(kl, i), a2 = A_Timing(kl, i), a3 = A_Position(kl, i);
                    bool a4 = A_Volatility(kl, i), a5 = A_Volume(kl, i), a6 = A_AI(sym, kl, i), a7 = A_HA(kl, i);
                    if (a1) aCat[0]++; if (a2) aCat[1]++; if (a3) aCat[2]++;
                    if (a4) aCat[3]++; if (a5) aCat[4]++; if (a6) aCat[5]++; if (a7) aCat[6]++;
                    int sA = (a1?1:0)+(a2?1:0)+(a3?1:0)+(a4?1:0)+(a5?1:0)+(a6?1:0)+(a7?1:0);

                    bool aFull = sA == 7;
                    bool aS6 = sA >= 6;
                    bool an1 = a2 && a3 && a4 && a5 && a6 && a7;  // noTrend (6 of remaining)
                    bool an2 = a1 && a3 && a4 && a5 && a6 && a7;
                    bool an3 = a1 && a2 && a4 && a5 && a6 && a7;
                    bool an4 = a1 && a2 && a3 && a5 && a6 && a7;
                    bool an5 = a1 && a2 && a3 && a4 && a6 && a7;
                    bool an6 = a1 && a2 && a3 && a4 && a5 && a7;
                    bool an7 = a1 && a2 && a3 && a4 && a5 && a6;

                    if (aFull || aS6 || an1 || an2 || an3 || an4 || an5 || an6 || an7)
                    {
                        // TP/SL — A: 평균회귀
                        BbAtMI(kl, i, out double mid, out double up, out _);
                        var (swH, swL) = SwingAtMI(kl, i, 20);
                        decimal entry = kl[i].ClosePrice;
                        decimal tpPx = (decimal)up;  // BB 상단
                        decimal slPx = swL * 0.997m;
                        if (tpPx > entry && slPx < entry)
                        {
                            decimal pnlPct = 0m; bool tpHit = false; int hold = 0;
                            for (int k = 1; k <= 24; k++)
                            {
                                hold = k; var b = kl[i + k];
                                if (b.LowPrice <= slPx) { pnlPct = (slPx - entry) / entry * 100m; break; }
                                if (b.HighPrice >= tpPx) { pnlPct = (tpPx - entry) / entry * 100m; tpHit = true; break; }
                                if (k == 24) pnlPct = (b.ClosePrice - entry) / entry * 100m;
                            }
                            decimal pnlNet = pnlPct - (decimal)(FEE_RATE * 2m * 100m);
                            decimal pnlUsd = MARGIN_USD * LEVERAGE * pnlNet / 100m;
                            void Add(string key, bool enter) { if (!enter) return; var s = stats[key]; stats[key] = (s.dec + 1, s.tp + (tpHit ? 1 : 0), s.pnl + pnlUsd, s.hold + hold); }
                            Add("A_Full", aFull);
                            Add("A_Score-1", aS6);
                            Add("A_noTrend", an1);
                            Add("A_noTiming", an2);
                            Add("A_noPosition", an3);
                            Add("A_noVolatility", an4);
                            Add("A_noVolume", an5);
                            Add("A_noAI", an6);
                            Add("A_noHA", an7);
                            if (aFull || aS6) lastFireA = i;
                        }
                    }
                }

                // === Scenario B ===
                if (i - lastFireB >= cooldown)
                {
                    bool b1 = B_Trend(kl, i), b2 = B_Timing(kl, i), b3 = B_Position(kl, i);
                    bool b4 = B_Volatility(kl, i), b5 = B_Volume(kl, i), b6 = B_AI(sym, kl, i);
                    bool b7 = B_Ichimoku(kl, i), b8 = B_PSAR(kl, i);
                    if (b1) bCat[0]++; if (b2) bCat[1]++; if (b3) bCat[2]++;
                    if (b4) bCat[3]++; if (b5) bCat[4]++; if (b6) bCat[5]++;
                    if (b7) bCat[6]++; if (b8) bCat[7]++;
                    int sB = (b1?1:0)+(b2?1:0)+(b3?1:0)+(b4?1:0)+(b5?1:0)+(b6?1:0)+(b7?1:0)+(b8?1:0);

                    bool bFull = sB == 8;
                    bool bS7 = sB >= 7;
                    bool bn1 = b2 && b3 && b4 && b5 && b6 && b7 && b8;
                    bool bn2 = b1 && b3 && b4 && b5 && b6 && b7 && b8;
                    bool bn3 = b1 && b2 && b4 && b5 && b6 && b7 && b8;
                    bool bn4 = b1 && b2 && b3 && b5 && b6 && b7 && b8;
                    bool bn5 = b1 && b2 && b3 && b4 && b6 && b7 && b8;
                    bool bn6 = b1 && b2 && b3 && b4 && b5 && b7 && b8;
                    bool bn7 = b1 && b2 && b3 && b4 && b5 && b6 && b8;
                    bool bn8 = b1 && b2 && b3 && b4 && b5 && b6 && b7;

                    if (bFull || bS7 || bn1 || bn2 || bn3 || bn4 || bn5 || bn6 || bn7 || bn8)
                    {
                        // TP/SL — B: 추세 동승
                        decimal atrNow = AtrAt(kl, i);
                        var (swH, swL) = SwingAtMI(kl, i, 20);
                        decimal entry = kl[i].ClosePrice;
                        decimal tpPx = entry + atrNow * 3m;  // ATR×3
                        decimal slPx = Math.Min(swL * 0.995m, entry - atrNow * 1.5m);
                        if (tpPx > entry && slPx < entry)
                        {
                            decimal pnlPct = 0m; bool tpHit = false; int hold = 0;
                            for (int k = 1; k <= 36; k++)
                            {
                                hold = k; var b = kl[i + k];
                                if (b.LowPrice <= slPx) { pnlPct = (slPx - entry) / entry * 100m; break; }
                                if (b.HighPrice >= tpPx) { pnlPct = (tpPx - entry) / entry * 100m; tpHit = true; break; }
                                if (k == 36) pnlPct = (b.ClosePrice - entry) / entry * 100m;
                            }
                            decimal pnlNet = pnlPct - (decimal)(FEE_RATE * 2m * 100m);
                            decimal pnlUsd = MARGIN_USD * LEVERAGE * pnlNet / 100m;
                            void AddB(string key, bool enter) { if (!enter) return; var s = stats[key]; stats[key] = (s.dec + 1, s.tp + (tpHit ? 1 : 0), s.pnl + pnlUsd, s.hold + hold); }
                            AddB("B_Full", bFull);
                            AddB("B_Score-1", bS7);
                            AddB("B_noTrend", bn1);
                            AddB("B_noTiming", bn2);
                            AddB("B_noPosition", bn3);
                            AddB("B_noVolatility", bn4);
                            AddB("B_noVolume", bn5);
                            AddB("B_noAI", bn6);
                            AddB("B_noIchi", bn7);
                            AddB("B_noPSAR", bn8);
                            if (bFull || bS7) lastFireB = i;
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  검토봉 {barsTested:N0}개  |  notional ${MARGIN_USD * LEVERAGE:F0}");
        Console.WriteLine();
        Console.WriteLine("  ===== 시나리오 A: 눌림 진입 (BB 하단~중단 → BB 상단 익절) =====");
        Console.WriteLine();
        Console.WriteLine("  [카테고리 단독 발생률]");
        string[] aNames = { "Trend(EMA)", "Timing", "Position(VWAP+BB)", "Volatility", "Volume", "AI(KNN)", "HeikenAshi" };
        for (int c = 0; c < 7; c++)
        {
            double pct = barsTested > 0 ? aCat[c] * 100.0 / barsTested : 0;
            Console.WriteLine($"    {aNames[c],-22} {aCat[c],8}건  ({pct,5:F2}%)");
        }
        PrintVariantTable(aKeys, stats);
        AnalyzeContribution(aKeys, aNames, stats, "A");

        Console.WriteLine();
        Console.WriteLine("  ===== 시나리오 B: 추세 동승 (Ichimoku+PSAR+MACD → ATR×3 TP) =====");
        Console.WriteLine();
        Console.WriteLine("  [카테고리 단독 발생률]");
        string[] bNames = { "Trend(strict)", "Timing", "Position(VWAP+BB)", "Volatility", "Volume", "AI(KNN≥2)", "Ichimoku", "PSAR" };
        for (int c = 0; c < 8; c++)
        {
            double pct = barsTested > 0 ? bCat[c] * 100.0 / barsTested : 0;
            Console.WriteLine($"    {bNames[c],-22} {bCat[c],8}건  ({pct,5:F2}%)");
        }
        PrintVariantTable(bKeys, stats);
        AnalyzeContribution(bKeys, bNames, stats, "B");
    }

    private static void PrintVariantTable(string[] keys, Dictionary<string, (int dec, int tp, decimal pnl, int hold)> stats)
    {
        Console.WriteLine();
        Console.WriteLine("  [Variant 백테스트]");
        Console.WriteLine($"  {"Variant",-18} {"Entries",10} {"WR%",7} {"PnL($)",12} {"avgHold",8} {"avgPnL",10}");
        Console.WriteLine("  " + new string('-', 75));
        foreach (var key in keys)
        {
            var s = stats[key];
            double wr = s.dec > 0 ? s.tp * 100.0 / s.dec : 0;
            decimal avg = s.dec > 0 ? s.pnl / s.dec : 0m;
            double avgHold = s.dec > 0 ? s.hold * 1.0 / s.dec : 0;
            Console.WriteLine($"  {key,-18} {s.dec,10:N0} {wr,6:F1}% {s.pnl,11:F2} {avgHold,7:F1} {avg,9:F2}");
        }
    }

    private static void AnalyzeContribution(string[] keys, string[] catNames, Dictionary<string, (int dec, int tp, decimal pnl, int hold)> stats, string label)
    {
        Console.WriteLine();
        Console.WriteLine($"  [기여도: {label}_Full 대비 noX ΔPnL — 음수 클수록 그 지표가 손익에 기여]");
        string fullKey = $"{label}_Full";
        decimal fullPnL = stats[fullKey].pnl;
        int fullDec = stats[fullKey].dec;
        double wrFull = fullDec > 0 ? stats[fullKey].tp * 100.0 / fullDec : 0;
        int idx = 0;
        foreach (var k in keys)
        {
            if (!k.StartsWith($"{label}_no")) continue;
            var s = stats[k];
            decimal dPnL = s.pnl - fullPnL;
            double wr = s.dec > 0 ? s.tp * 100.0 / s.dec : 0;
            double dWR = wr - wrFull;
            string verdict = dPnL > 5m ? "필터링 역효과" : dPnL < -5m ? "필터링 효과" : (Math.Abs(dPnL) < 1m ? "영향 없음" : "약한 효과");
            string name = idx < catNames.Length ? catNames[idx] : k;
            Console.WriteLine($"    {name,-22} {k,-15} n={s.dec,4} WR={wr,5:F1}% ΔPnL={dPnL,+9:F2}$ ΔWR={dWR,+6:F1}%  → {verdict}");
            idx++;
        }
    }

    // ===================================================================
    // [v5.23.75] BB 를 진입 게이트에서 제거 — BB 는 추세 인식 (방향/폭) 만 사용
    //   진입은 EMA20/VWAP/Fib 풀백 + 모멘텀 + 캔들 + AI 합의
    //   TP 는 BB 상단 (위치 참조만)
    // ===================================================================
    private static (decimal r382, decimal r500, decimal r618, decimal swH, decimal swL)
        FibAt(List<IBinanceKline> kl, int i, int lookback = 30)
    {
        decimal high = decimal.MinValue, low = decimal.MaxValue;
        int start = Math.Max(0, i - lookback + 1);
        for (int q = start; q <= i; q++)
        {
            if (kl[q].HighPrice > high) high = kl[q].HighPrice;
            if (kl[q].LowPrice < low) low = kl[q].LowPrice;
        }
        decimal range = high - low;
        return (high - range * 0.382m, high - range * 0.500m, high - range * 0.618m, high, low);
    }

    private static async Task RunMultiPureAsync(int days = 90)
    {
        int pages = Math.Max(1, days * 24 * 60 / 5 / BARS_PER_REQ + 1);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  [v5.23.75] BB 게이트 제거 / 다중지표 PURE ({days}일 / 5m / {symbols.Length}개 심볼)");
        Console.WriteLine("  BB → 추세 인식 (방향/폭) 만, 진입 게이트 X");
        Console.WriteLine("  진입: EMA20/VWAP/Fib 풀백 + 모멘텀 + 캔들 + 다중확인");
        Console.WriteLine("  TP: BB 상단 (참조), SL: swing low -0.3%");
        Console.WriteLine("================================================================");

        var lor = new MiniLorentzianService();
        var data = new Dictionary<string, List<IBinanceKline>>();

        Console.WriteLine($"\n[fetch {days}일 — {symbols.Length}개 심볼 (5m)]");
        int fidx = 0;
        foreach (var sym in symbols)
        {
            fidx++;
            Console.Write($"[{fidx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 500) { Console.WriteLine("skip"); continue; }
                data[sym] = kl;
                int trainEnd = (int)(kl.Count * 0.7);
                int added = lor.BackfillFromCandles(sym, kl.GetRange(0, trainEnd));
                Console.WriteLine($"ok ({kl.Count} bars, KNN train {added})");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        if (data.Count == 0) { Console.WriteLine("데이터 없음"); return; }

        // === 6 카테고리 — BB 게이트 X ===

        // 1. 추세 — EMA stack + MACD + BB middle 기울기 (추세 정보로만)
        bool Cat1Trend(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            double e20 = (double)CalcEmaN(kl, i, 20);
            double e50 = (double)CalcEmaN(kl, i, 50);
            if (e20 <= e50) return false;
            var (macd, sig, _) = MacdAt(kl, i);
            if (macd < sig) return false;
            // BB 중단 기울기 = 추세 방향 (정보 사용, 차단 아님)
            BbAtMI(kl, i, out double midNow, out _, out _);
            BbAtMI(kl, i - 5, out double midPrev, out _, out _);
            if (midNow <= midPrev) return false;  // BB 중단 상승 = 추세 상승 (방향 확인)
            return true;
        }

        // 2. 타이밍 — 모멘텀 반전 (RSI/Stoch/W%R/CCI 합의)
        bool Cat2Timing(List<IBinanceKline> kl, int i)
        {
            double rsi = CalcRsi14(kl, i);
            if (rsi < 30 || rsi > 60) return false;
            var (sK, _) = StochAt(kl, i);
            var (sKp, _) = StochAt(kl, i - 1);
            if (sK <= sKp) return false;  // 상승 중
            if (WilliamsRAt(kl, i) < -80) return false;
            return true;
        }

        // 3. 풀백 — BB 가 아닌 EMA20/VWAP/Fib 중 ANY 근처 (OR)
        bool Cat3Pullback(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            double c = (double)kl[i].ClosePrice;
            // EMA20 근처 ±0.5%
            double e20 = (double)CalcEmaN(kl, i, 20);
            bool nearEma = Math.Abs(c - e20) / e20 < 0.005;
            // VWAP 근처 ±0.5%
            var (vwap, _, _) = VwapMI(kl, i);
            bool nearVwap = Math.Abs(c - (double)vwap) / (double)vwap < 0.005;
            // Fib 0.382/0.5/0.618 ±0.3%
            var (r382, r500, r618, _, _) = FibAt(kl, i, 30);
            bool nearFib =
                Math.Abs(c - (double)r382) / c < 0.003 ||
                Math.Abs(c - (double)r500) / c < 0.003 ||
                Math.Abs(c - (double)r618) / c < 0.003;
            return nearEma || nearVwap || nearFib;
        }

        // 4. 거래량 — Vol + OBV
        bool Cat4Volume(List<IBinanceKline> kl, int i)
        {
            return VolMult(kl, i, 20) >= 1.2 && ObvRising(kl, i, 20);
        }

        // 5. 캔들 — Heiken Ashi 반전 양봉
        bool Cat5Candle(List<IBinanceKline> kl, int i)
        {
            var (haO, haC, haH, haL, streak) = HeikenAshiAt(kl, i);
            if (haC <= haO) return false;  // 양봉 필수
            // 윗꼬리 < 몸통 50%
            decimal body = Math.Abs(haC - haO);
            decimal upperWick = haH - Math.Max(haO, haC);
            if (body <= 0) return false;
            return upperWick < body * 0.5m;
        }

        // 6. 다중확인 — Ichimoku/PSAR/AI 중 ≥2 통과
        bool Cat6Confirm(string sym, List<IBinanceKline> kl, int i)
        {
            int votes = 0;
            // Ichimoku: 가격 > kijun
            if (i >= 52)
            {
                var (tk, kj, _, _, _) = IchimokuAt(kl, i);
                if (kl[i].ClosePrice > kj && tk > kj) votes++;
            }
            // PSAR: bullish + 가격 > sar
            var (sar, bull) = PsarAt(kl, i);
            if (bull && kl[i].ClosePrice > sar) votes++;
            // AI: KNN ≥ 1
            var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
            if (pred.IsReady && pred.Prediction >= 1) votes++;
            return votes >= 2;
        }

        var keys = new[] { "Full_6of6", "Score>=5", "noTrend", "noTiming", "noPullback", "noVolume", "noCandle", "noConfirm" };
        var stats = new Dictionary<string, (int dec, int tp, decimal pnl, int hold)>();
        foreach (var k in keys) stats[k] = (0, 0, 0m, 0);
        var cat = new int[6];
        int barsTested = 0;

        foreach (var kv in data)
        {
            var kl = kv.Value; var sym = kv.Key;
            int trainEnd = (int)(kl.Count * 0.7);
            int lastFire = -1000;
            const int cooldown = 12;

            for (int i = trainEnd + 50; i < kl.Count - 36; i++)
            {
                if (i < 200) continue;
                if (i - lastFire < cooldown) continue;
                barsTested++;

                bool c1 = Cat1Trend(kl, i);
                bool c2 = Cat2Timing(kl, i);
                bool c3 = Cat3Pullback(kl, i);
                bool c4 = Cat4Volume(kl, i);
                bool c5 = Cat5Candle(kl, i);
                bool c6 = Cat6Confirm(sym, kl, i);
                if (c1) cat[0]++; if (c2) cat[1]++; if (c3) cat[2]++;
                if (c4) cat[3]++; if (c5) cat[4]++; if (c6) cat[5]++;
                int sc = (c1?1:0)+(c2?1:0)+(c3?1:0)+(c4?1:0)+(c5?1:0)+(c6?1:0);

                bool full = sc == 6;
                bool s5 = sc >= 5;
                bool n1 = c2 && c3 && c4 && c5 && c6;
                bool n2 = c1 && c3 && c4 && c5 && c6;
                bool n3 = c1 && c2 && c4 && c5 && c6;
                bool n4 = c1 && c2 && c3 && c5 && c6;
                bool n5 = c1 && c2 && c3 && c4 && c6;
                bool n6 = c1 && c2 && c3 && c4 && c5;

                if (!(full || s5 || n1 || n2 || n3 || n4 || n5 || n6)) continue;

                // TP/SL — TP=BB 상단 (참조), SL=swing low -0.3%, max hold 36
                BbAtMI(kl, i, out _, out double bbUp, out _);
                var (swH, swL) = SwingAtMI(kl, i, 20);
                decimal entry = kl[i].ClosePrice;
                decimal tpPx = (decimal)bbUp;
                decimal slPx = swL * 0.997m;
                if (tpPx <= entry || slPx >= entry) continue;

                decimal pnlPct = 0m; bool tpHit = false; int hold = 0;
                for (int k = 1; k <= 36; k++)
                {
                    hold = k; var b = kl[i + k];
                    if (b.LowPrice <= slPx) { pnlPct = (slPx - entry) / entry * 100m; break; }
                    if (b.HighPrice >= tpPx) { pnlPct = (tpPx - entry) / entry * 100m; tpHit = true; break; }
                    if (k == 36) pnlPct = (b.ClosePrice - entry) / entry * 100m;
                }
                decimal pnlNet = pnlPct - (decimal)(FEE_RATE * 2m * 100m);
                decimal pnlUsd = MARGIN_USD * LEVERAGE * pnlNet / 100m;
                void Add(string key, bool enter) { if (!enter) return; var s = stats[key]; stats[key] = (s.dec + 1, s.tp + (tpHit ? 1 : 0), s.pnl + pnlUsd, s.hold + hold); }
                Add("Full_6of6", full);
                Add("Score>=5", s5);
                Add("noTrend", n1);
                Add("noTiming", n2);
                Add("noPullback", n3);
                Add("noVolume", n4);
                Add("noCandle", n5);
                Add("noConfirm", n6);
                if (full || s5) lastFire = i;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  검토봉 {barsTested:N0}개  |  notional ${MARGIN_USD * LEVERAGE:F0}");
        Console.WriteLine();
        Console.WriteLine("  [카테고리 단독 발생률]");
        string[] catNames = { "Trend(EMA+MACD+BB방향)", "Timing(RSI/Stoch/W%R)", "Pullback(EMA20/VWAP/Fib)", "Volume(vol+OBV)", "Candle(HA양봉)", "Confirm(Ichi/PSAR/AI≥2)" };
        for (int c = 0; c < 6; c++)
        {
            double pct = barsTested > 0 ? cat[c] * 100.0 / barsTested : 0;
            Console.WriteLine($"    {catNames[c],-28} {cat[c],8}건  ({pct,5:F2}%)");
        }
        PrintVariantTable(keys, stats);
        AnalyzeContributionMI(keys, catNames, stats);
    }

    private static void AnalyzeContributionMI(string[] keys, string[] catNames, Dictionary<string, (int dec, int tp, decimal pnl, int hold)> stats)
    {
        Console.WriteLine();
        Console.WriteLine("  [기여도: Full 대비 noX ΔPnL — 음수 클수록 그 지표가 손익에 기여]");
        decimal fullPnL = stats["Full_6of6"].pnl;
        int fullDec = stats["Full_6of6"].dec;
        double wrFull = fullDec > 0 ? stats["Full_6of6"].tp * 100.0 / fullDec : 0;
        int idx = 0;
        foreach (var k in keys)
        {
            if (!k.StartsWith("no")) continue;
            var s = stats[k];
            decimal dPnL = s.pnl - fullPnL;
            double wr = s.dec > 0 ? s.tp * 100.0 / s.dec : 0;
            double dWR = wr - wrFull;
            string verdict = dPnL > 10m ? "필터링 역효과" : dPnL < -10m ? "필터링 효과" : (Math.Abs(dPnL) < 2m ? "영향 없음" : "약한 효과");
            string name = idx < catNames.Length ? catNames[idx] : k;
            Console.WriteLine($"    {name,-28} {k,-12} n={s.dec,4} WR={wr,5:F1}% ΔPnL={dPnL,+9:F2}$ ΔWR={dWR,+6:F1}%  → {verdict}");
            idx++;
        }
    }

    // ===================================================================
    // [v5.23.76] TP/SL 스윕 — multi-pure Full 6/6 진입 조건 고정, TP/SL 만 변경
    //   95.7% WR 인데 PnL=0 인 문제 = TP 너무 멀음. 다양한 TP/SL 조합 비교
    // ===================================================================
    private static async Task RunMultiPureTpslSweepAsync(int days = 90)
    {
        int pages = Math.Max(1, days * 24 * 60 / 5 / BARS_PER_REQ + 1);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  [v5.23.76] TP/SL 스윕 — Full 6/6 진입 ({days}일 / 5m / {symbols.Length}개)");
        Console.WriteLine("  진입 조건 고정, TP/SL 14개 조합 비교 → 최적 손익비 탐색");
        Console.WriteLine("================================================================");

        var lor = new MiniLorentzianService();
        var data = new Dictionary<string, List<IBinanceKline>>();

        Console.WriteLine($"\n[fetch {days}일]");
        int fidx = 0;
        foreach (var sym in symbols)
        {
            fidx++;
            Console.Write($"[{fidx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 500) { Console.WriteLine("skip"); continue; }
                data[sym] = kl;
                int trainEnd = (int)(kl.Count * 0.7);
                int added = lor.BackfillFromCandles(sym, kl.GetRange(0, trainEnd));
                Console.WriteLine($"ok ({kl.Count} bars, KNN {added})");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        if (data.Count == 0) { Console.WriteLine("데이터 없음"); return; }

        // === multi-pure Full 6/6 진입 조건 (BB 게이트 X) ===
        bool C1Trend(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            double e20 = (double)CalcEmaN(kl, i, 20);
            double e50 = (double)CalcEmaN(kl, i, 50);
            if (e20 <= e50) return false;
            var (macd, sig, _) = MacdAt(kl, i);
            if (macd < sig) return false;
            BbAtMI(kl, i, out double midNow, out _, out _);
            BbAtMI(kl, i - 5, out double midPrev, out _, out _);
            return midNow > midPrev;
        }
        bool C2Timing(List<IBinanceKline> kl, int i)
        {
            double rsi = CalcRsi14(kl, i);
            if (rsi < 30 || rsi > 60) return false;
            var (sK, _) = StochAt(kl, i);
            var (sKp, _) = StochAt(kl, i - 1);
            if (sK <= sKp) return false;
            return WilliamsRAt(kl, i) >= -80;
        }
        bool C3Pullback(List<IBinanceKline> kl, int i)
        {
            if (i < 50) return false;
            double c = (double)kl[i].ClosePrice;
            double e20 = (double)CalcEmaN(kl, i, 20);
            bool nearEma = Math.Abs(c - e20) / e20 < 0.005;
            var (vwap, _, _) = VwapMI(kl, i);
            bool nearVwap = Math.Abs(c - (double)vwap) / (double)vwap < 0.005;
            var (r382, r500, r618, _, _) = FibAt(kl, i, 30);
            bool nearFib = Math.Abs(c - (double)r382) / c < 0.003 || Math.Abs(c - (double)r500) / c < 0.003 || Math.Abs(c - (double)r618) / c < 0.003;
            return nearEma || nearVwap || nearFib;
        }
        bool C4Volume(List<IBinanceKline> kl, int i) => VolMult(kl, i, 20) >= 1.2 && ObvRising(kl, i, 20);
        bool C5Candle(List<IBinanceKline> kl, int i)
        {
            var (haO, haC, haH, _, _) = HeikenAshiAt(kl, i);
            if (haC <= haO) return false;
            decimal body = Math.Abs(haC - haO);
            decimal upperWick = haH - Math.Max(haO, haC);
            return body > 0 && upperWick < body * 0.5m;
        }
        bool C6Confirm(string sym, List<IBinanceKline> kl, int i)
        {
            int votes = 0;
            if (i >= 52)
            {
                var (tk, kj, _, _, _) = IchimokuAt(kl, i);
                if (kl[i].ClosePrice > kj && tk > kj) votes++;
            }
            var (sar, bull) = PsarAt(kl, i);
            if (bull && kl[i].ClosePrice > sar) votes++;
            var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
            if (pred.IsReady && pred.Prediction >= 1) votes++;
            return votes >= 2;
        }

        // === TP/SL 14개 조합 ===
        // 각 조합: (label, tpFunc, slFunc)
        // tpFunc/slFunc → (entry, kl, i, atr) → price
        var combos = new (string label, Func<decimal, List<IBinanceKline>, int, decimal, decimal> tpFn, Func<decimal, List<IBinanceKline>, int, decimal, decimal> slFn)[]
        {
            ("BBupper / swingL-0.3%",  (e, kl, i, atr) => { BbAtMI(kl, i, out _, out double up, out _); return (decimal)up; },
                                       (e, kl, i, atr) => { var (_, swL) = SwingAtMI(kl, i, 20); return swL * 0.997m; }),
            ("BBmid / swingL-0.3%",    (e, kl, i, atr) => { BbAtMI(kl, i, out double mid, out _, out _); return (decimal)mid; },
                                       (e, kl, i, atr) => { var (_, swL) = SwingAtMI(kl, i, 20); return swL * 0.997m; }),
            ("ATR×1.0 / ATR×0.7",      (e, kl, i, atr) => e + atr,
                                       (e, kl, i, atr) => e - atr * 0.7m),
            ("ATR×1.0 / ATR×1.0",      (e, kl, i, atr) => e + atr,
                                       (e, kl, i, atr) => e - atr),
            ("ATR×1.5 / ATR×1.0",      (e, kl, i, atr) => e + atr * 1.5m,
                                       (e, kl, i, atr) => e - atr),
            ("ATR×2.0 / ATR×1.0",      (e, kl, i, atr) => e + atr * 2m,
                                       (e, kl, i, atr) => e - atr),
            ("Fix 0.3% / Fix 0.2%",    (e, kl, i, atr) => e * 1.003m,
                                       (e, kl, i, atr) => e * 0.998m),
            ("Fix 0.5% / Fix 0.3%",    (e, kl, i, atr) => e * 1.005m,
                                       (e, kl, i, atr) => e * 0.997m),
            ("Fix 0.5% / Fix 0.5%",    (e, kl, i, atr) => e * 1.005m,
                                       (e, kl, i, atr) => e * 0.995m),
            ("Fix 1.0% / Fix 0.5%",    (e, kl, i, atr) => e * 1.010m,
                                       (e, kl, i, atr) => e * 0.995m),
            ("Fix 1.0% / Fix 0.7%",    (e, kl, i, atr) => e * 1.010m,
                                       (e, kl, i, atr) => e * 0.993m),
            ("Fix 1.5% / Fix 0.5%",    (e, kl, i, atr) => e * 1.015m,
                                       (e, kl, i, atr) => e * 0.995m),
            ("Fix 1.5% / Fix 1.0%",    (e, kl, i, atr) => e * 1.015m,
                                       (e, kl, i, atr) => e * 0.990m),
            ("Fix 2.0% / Fix 1.0%",    (e, kl, i, atr) => e * 1.020m,
                                       (e, kl, i, atr) => e * 0.990m),
        };

        var stats = new Dictionary<string, (int dec, int tp, int sl, int to, decimal pnl, int hold, decimal mdd, decimal cum, decimal peak)>();
        foreach (var c in combos) stats[c.label] = (0, 0, 0, 0, 0m, 0, 0m, 0m, 0m);
        int totalEntries = 0;

        foreach (var kv in data)
        {
            var kl = kv.Value; var sym = kv.Key;
            int trainEnd = (int)(kl.Count * 0.7);
            int lastFire = -1000;
            const int cooldown = 12;
            const int maxHold = 36;

            for (int i = trainEnd + 50; i < kl.Count - maxHold; i++)
            {
                if (i < 200) continue;
                if (i - lastFire < cooldown) continue;

                if (!(C1Trend(kl, i) && C2Timing(kl, i) && C3Pullback(kl, i)
                   && C4Volume(kl, i) && C5Candle(kl, i) && C6Confirm(sym, kl, i))) continue;

                totalEntries++;
                lastFire = i;
                decimal entry = kl[i].ClosePrice;
                decimal atr = AtrAt(kl, i);
                if (atr <= 0) continue;

                foreach (var combo in combos)
                {
                    decimal tpPx = combo.tpFn(entry, kl, i, atr);
                    decimal slPx = combo.slFn(entry, kl, i, atr);
                    if (tpPx <= entry || slPx >= entry) continue;

                    decimal pnlPct = 0m; int outcome = 0; int hold = 0;  // 0=TO, 1=TP, 2=SL
                    for (int k = 1; k <= maxHold; k++)
                    {
                        hold = k; var b = kl[i + k];
                        if (b.LowPrice <= slPx) { pnlPct = (slPx - entry) / entry * 100m; outcome = 2; break; }
                        if (b.HighPrice >= tpPx) { pnlPct = (tpPx - entry) / entry * 100m; outcome = 1; break; }
                        if (k == maxHold) pnlPct = (b.ClosePrice - entry) / entry * 100m;
                    }
                    decimal pnlNet = pnlPct - (decimal)(FEE_RATE * 2m * 100m);
                    decimal pnlUsd = MARGIN_USD * LEVERAGE * pnlNet / 100m;
                    var s = stats[combo.label];
                    decimal cum = s.cum + pnlUsd;
                    decimal peak = Math.Max(s.peak, cum);
                    decimal mdd = Math.Max(s.mdd, peak - cum);
                    stats[combo.label] = (s.dec + 1, s.tp + (outcome == 1 ? 1 : 0), s.sl + (outcome == 2 ? 1 : 0), s.to + (outcome == 0 ? 1 : 0), s.pnl + pnlUsd, s.hold + hold, mdd, cum, peak);
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  총 진입 트리거: {totalEntries:N0}건 (Full 6/6) | notional ${MARGIN_USD * LEVERAGE:F0}");
        Console.WriteLine();
        Console.WriteLine($"  {"TP/SL Combo",-26} {"Entries",8} {"WR%",7} {"TP",6} {"SL",6} {"TO",6} {"PnL($)",10} {"avgPnL",10} {"avgHold",8} {"MDD$",9}");
        Console.WriteLine("  " + new string('-', 110));
        var sorted = combos.Select(c => (label: c.label, s: stats[c.label])).OrderByDescending(x => x.s.pnl).ToList();
        foreach (var (label, s) in sorted)
        {
            double wr = s.dec > 0 ? s.tp * 100.0 / s.dec : 0;
            decimal avg = s.dec > 0 ? s.pnl / s.dec : 0m;
            double avgHold = s.dec > 0 ? s.hold * 1.0 / s.dec : 0;
            Console.WriteLine($"  {label,-26} {s.dec,8:N0} {wr,6:F1}% {s.tp,5} {s.sl,5} {s.to,5} {s.pnl,9:F2} {avg,9:F2} {avgHold,7:F1} {s.mdd,8:F2}");
        }
        Console.WriteLine();
        Console.WriteLine("  ※ 정렬: PnL 내림차순. WR 높아도 PnL 낮으면 TP 너무 멀음. WR 낮아도 PnL 높으면 손익비 우월.");
    }

    // ===================================================================
    // [v5.23.77] Multi-pure Full 6/6 세부 분석 — 심볼/시간대/요일별 PnL
    //   TP=1.0% / SL=0.7% 고정. 180일 데이터로 어느 코인/시간이 가장 기여하는지 측정
    // ===================================================================
    private static async Task RunMultiPureDetailAsync(int days = 180)
    {
        int pages = Math.Max(1, days * 24 * 60 / 5 / BARS_PER_REQ + 1);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  [v5.23.77] Multi-pure 세부 분석 ({days}일 / TP=1% / SL=0.7%)");
        Console.WriteLine("  심볼별 / 시간대별 / 요일별 PnL");
        Console.WriteLine("================================================================");

        var lor = new MiniLorentzianService();
        var data = new Dictionary<string, List<IBinanceKline>>();

        Console.WriteLine($"\n[fetch {days}일]");
        int fidx = 0;
        foreach (var sym in symbols)
        {
            fidx++;
            Console.Write($"[{fidx}/{symbols.Length}] {sym} ");
            try
            {
                var kl = await FetchKlinesAsync(sym, pages);
                if (kl.Count < 500) { Console.WriteLine("skip"); continue; }
                data[sym] = kl;
                int trainEnd = (int)(kl.Count * 0.7);
                int added = lor.BackfillFromCandles(sym, kl.GetRange(0, trainEnd));
                Console.WriteLine($"ok ({kl.Count} bars)");
            }
            catch (Exception ex) { Console.WriteLine("fail: " + ex.Message); }
        }
        if (data.Count == 0) { Console.WriteLine("데이터 없음"); return; }

        // 진입 조건 (Full 6/6, BB 게이트 X)
        bool Entry(string sym, List<IBinanceKline> kl, int i)
        {
            if (i < 200) return false;
            // 1. Trend
            double e20 = (double)CalcEmaN(kl, i, 20);
            double e50 = (double)CalcEmaN(kl, i, 50);
            if (e20 <= e50) return false;
            var (macd, sig, _) = MacdAt(kl, i);
            if (macd < sig) return false;
            BbAtMI(kl, i, out double midNow, out _, out _);
            BbAtMI(kl, i - 5, out double midPrev, out _, out _);
            if (midNow <= midPrev) return false;
            // 2. Timing
            double rsi = CalcRsi14(kl, i);
            if (rsi < 30 || rsi > 60) return false;
            var (sK, _) = StochAt(kl, i);
            var (sKp, _) = StochAt(kl, i - 1);
            if (sK <= sKp) return false;
            if (WilliamsRAt(kl, i) < -80) return false;
            // 3. Pullback
            double c = (double)kl[i].ClosePrice;
            double e20p = (double)CalcEmaN(kl, i, 20);
            bool nearEma = Math.Abs(c - e20p) / e20p < 0.005;
            var (vwap, _, _) = VwapMI(kl, i);
            bool nearVwap = Math.Abs(c - (double)vwap) / (double)vwap < 0.005;
            var (r382, r500, r618, _, _) = FibAt(kl, i, 30);
            bool nearFib = Math.Abs(c - (double)r382) / c < 0.003 || Math.Abs(c - (double)r500) / c < 0.003 || Math.Abs(c - (double)r618) / c < 0.003;
            if (!(nearEma || nearVwap || nearFib)) return false;
            // 4. Volume
            if (VolMult(kl, i, 20) < 1.2 || !ObvRising(kl, i, 20)) return false;
            // 5. Candle
            var (haO, haC, haH, _, _) = HeikenAshiAt(kl, i);
            if (haC <= haO) return false;
            decimal body = Math.Abs(haC - haO);
            decimal upperWick = haH - Math.Max(haO, haC);
            if (body <= 0 || upperWick >= body * 0.5m) return false;
            // 6. Confirm (Ichi/PSAR/AI ≥ 2)
            int votes = 0;
            if (i >= 52) { var (tk, kj, _, _, _) = IchimokuAt(kl, i); if (kl[i].ClosePrice > kj && tk > kj) votes++; }
            var (sar, bull) = PsarAt(kl, i);
            if (bull && kl[i].ClosePrice > sar) votes++;
            var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
            if (pred.IsReady && pred.Prediction >= 1) votes++;
            return votes >= 2;
        }

        var perSym = new Dictionary<string, (int dec, int tp, int sl, int to, decimal pnl, int hold)>();
        var perHourUtc = new Dictionary<int, (int dec, int tp, int sl, int to, decimal pnl)>();
        var perHourKst = new Dictionary<int, (int dec, int tp, int sl, int to, decimal pnl)>();
        var perDow = new Dictionary<DayOfWeek, (int dec, int tp, int sl, int to, decimal pnl)>();
        var perMonth = new Dictionary<string, (int dec, int tp, int sl, int to, decimal pnl)>();

        foreach (var s in data.Keys) perSym[s] = (0, 0, 0, 0, 0m, 0);
        for (int h = 0; h < 24; h++) { perHourUtc[h] = (0, 0, 0, 0, 0m); perHourKst[h] = (0, 0, 0, 0, 0m); }
        foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek))) perDow[d] = (0, 0, 0, 0, 0m);

        int totalEntries = 0;
        decimal totalPnl = 0m;
        int totalTp = 0, totalSl = 0, totalTo = 0;

        foreach (var kv in data)
        {
            var kl = kv.Value; var sym = kv.Key;
            int trainEnd = (int)(kl.Count * 0.7);
            int lastFire = -1000;
            const int cooldown = 12;
            const int maxHold = 36;

            for (int i = trainEnd + 50; i < kl.Count - maxHold; i++)
            {
                if (i - lastFire < cooldown) continue;
                if (!Entry(sym, kl, i)) continue;
                lastFire = i;
                totalEntries++;

                decimal entry = kl[i].ClosePrice;
                decimal tpPx = entry * 1.010m;
                decimal slPx = entry * 0.993m;
                decimal pnlPct = 0m; int outcome = 0; int hold = 0;
                for (int k = 1; k <= maxHold; k++)
                {
                    hold = k; var b = kl[i + k];
                    if (b.LowPrice <= slPx) { pnlPct = (slPx - entry) / entry * 100m; outcome = 2; break; }
                    if (b.HighPrice >= tpPx) { pnlPct = (tpPx - entry) / entry * 100m; outcome = 1; break; }
                    if (k == maxHold) pnlPct = (b.ClosePrice - entry) / entry * 100m;
                }
                decimal pnlNet = pnlPct - (decimal)(FEE_RATE * 2m * 100m);
                decimal pnlUsd = MARGIN_USD * LEVERAGE * pnlNet / 100m;
                totalPnl += pnlUsd;
                if (outcome == 1) totalTp++; else if (outcome == 2) totalSl++; else totalTo++;

                var ot = kl[i].OpenTime;  // UTC
                int hourUtc = ot.Hour;
                int hourKst = (hourUtc + 9) % 24;
                var dow = ot.DayOfWeek;
                string monthKey = ot.ToString("yyyy-MM");

                var sS = perSym[sym]; perSym[sym] = (sS.dec + 1, sS.tp + (outcome == 1 ? 1 : 0), sS.sl + (outcome == 2 ? 1 : 0), sS.to + (outcome == 0 ? 1 : 0), sS.pnl + pnlUsd, sS.hold + hold);
                var sU = perHourUtc[hourUtc]; perHourUtc[hourUtc] = (sU.dec + 1, sU.tp + (outcome == 1 ? 1 : 0), sU.sl + (outcome == 2 ? 1 : 0), sU.to + (outcome == 0 ? 1 : 0), sU.pnl + pnlUsd);
                var sK2 = perHourKst[hourKst]; perHourKst[hourKst] = (sK2.dec + 1, sK2.tp + (outcome == 1 ? 1 : 0), sK2.sl + (outcome == 2 ? 1 : 0), sK2.to + (outcome == 0 ? 1 : 0), sK2.pnl + pnlUsd);
                var sD = perDow[dow]; perDow[dow] = (sD.dec + 1, sD.tp + (outcome == 1 ? 1 : 0), sD.sl + (outcome == 2 ? 1 : 0), sD.to + (outcome == 0 ? 1 : 0), sD.pnl + pnlUsd);
                if (!perMonth.ContainsKey(monthKey)) perMonth[monthKey] = (0, 0, 0, 0, 0m);
                var sM = perMonth[monthKey]; perMonth[monthKey] = (sM.dec + 1, sM.tp + (outcome == 1 ? 1 : 0), sM.sl + (outcome == 2 ? 1 : 0), sM.to + (outcome == 0 ? 1 : 0), sM.pnl + pnlUsd);
            }
        }

        double overallWr = totalEntries > 0 ? totalTp * 100.0 / totalEntries : 0;
        Console.WriteLine();
        Console.WriteLine($"  총 {totalEntries}건 | WR {overallWr:F1}% (TP={totalTp}, SL={totalSl}, TO={totalTo}) | PnL=${totalPnl:F2}");
        Console.WriteLine();

        // 심볼별
        Console.WriteLine("  ===== 심볼별 PnL (높은 순) =====");
        Console.WriteLine($"  {"Symbol",-14} {"Entries",8} {"WR%",6} {"TP",4} {"SL",4} {"TO",4} {"PnL$",9} {"avgHold",8}");
        foreach (var (s, st) in perSym.OrderByDescending(x => x.Value.pnl))
        {
            if (st.dec == 0) continue;
            double wr = st.tp * 100.0 / st.dec;
            double avgH = st.hold * 1.0 / st.dec;
            Console.WriteLine($"  {s,-14} {st.dec,8} {wr,5:F1}% {st.tp,3} {st.sl,3} {st.to,3} {st.pnl,8:F2} {avgH,7:F1}");
        }

        // 시간대별 KST
        Console.WriteLine();
        Console.WriteLine("  ===== 시간대별 PnL (KST) =====");
        Console.WriteLine($"  {"Hour",5} {"Entries",8} {"WR%",6} {"PnL$",10}");
        for (int h = 0; h < 24; h++)
        {
            var st = perHourKst[h];
            if (st.dec == 0) continue;
            double wr = st.tp * 100.0 / st.dec;
            string bar = new string('█', Math.Min(20, (int)Math.Abs((double)st.pnl) / 5));
            string sign = st.pnl >= 0 ? "+" : "-";
            Console.WriteLine($"  {h,3}시 {st.dec,8} {wr,5:F1}% {st.pnl,9:F2}  {sign}{bar}");
        }

        // 시간대별 UTC (참고용)
        Console.WriteLine();
        Console.WriteLine("  ===== TOP 5 / BOTTOM 5 시간대 (KST) =====");
        var hoursSorted = perHourKst.Where(kv => kv.Value.dec > 0).OrderByDescending(kv => kv.Value.pnl).ToList();
        Console.WriteLine("  TOP 5:");
        foreach (var (h, st) in hoursSorted.Take(5))
            Console.WriteLine($"    {h:D2}시  진입 {st.dec}건  WR {st.tp * 100.0 / st.dec:F1}%  PnL ${st.pnl:F2}");
        Console.WriteLine("  BOTTOM 5:");
        foreach (var (h, st) in hoursSorted.OrderBy(kv => kv.Value.pnl).Take(5))
            Console.WriteLine($"    {h:D2}시  진입 {st.dec}건  WR {st.tp * 100.0 / st.dec:F1}%  PnL ${st.pnl:F2}");

        // 요일별
        Console.WriteLine();
        Console.WriteLine("  ===== 요일별 PnL =====");
        string[] dowKr = { "일", "월", "화", "수", "목", "금", "토" };
        for (int d = 0; d < 7; d++)
        {
            var st = perDow[(DayOfWeek)d];
            if (st.dec == 0) continue;
            double wr = st.tp * 100.0 / st.dec;
            Console.WriteLine($"  {dowKr[d]}요일  진입 {st.dec,4}건  WR {wr,5:F1}%  PnL ${st.pnl,8:F2}");
        }

        // 월별
        Console.WriteLine();
        Console.WriteLine("  ===== 월별 PnL =====");
        foreach (var (m, st) in perMonth.OrderBy(x => x.Key))
        {
            double wr = st.dec > 0 ? st.tp * 100.0 / st.dec : 0;
            Console.WriteLine($"  {m}  진입 {st.dec,4}건  WR {wr,5:F1}%  PnL ${st.pnl,8:F2}");
        }

        // 핵심 인사이트
        Console.WriteLine();
        Console.WriteLine("  ===== 핵심 인사이트 =====");
        var topSym = perSym.Where(x => x.Value.dec > 0).OrderByDescending(x => x.Value.pnl).Take(5).ToList();
        var botSym = perSym.Where(x => x.Value.dec > 0).OrderBy(x => x.Value.pnl).Take(5).ToList();
        decimal topPnl = topSym.Sum(x => x.Value.pnl);
        decimal botPnl = botSym.Sum(x => x.Value.pnl);
        Console.WriteLine($"  TOP 5 심볼이 총 PnL의 {(totalPnl != 0 ? topPnl / totalPnl * 100m : 0):F1}% 차지 (${topPnl:F2})");
        Console.WriteLine($"  BOTTOM 5 심볼이 총 PnL의 {(totalPnl != 0 ? botPnl / totalPnl * 100m : 0):F1}% 차지 (${botPnl:F2})");
        int profitableHours = perHourKst.Count(kv => kv.Value.pnl > 0);
        int unprofitableHours = perHourKst.Count(kv => kv.Value.pnl < 0);
        Console.WriteLine($"  흑자 시간대 {profitableHours}/24, 손실 시간대 {unprofitableHours}/24");
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.80] --rsidip-verify : 사용자 진입전략(눌림+RSI보조+가짜반등필터) 검증.
    //   가짜반등 필터 ON vs OFF 비교 → 필터가 승률을 올리는지 확인. K폴드.
    //   진입조건은 라이브 AnalyzeMeanReversionEntry 와 동일. 청산은 SimulateRunner(근사).
    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.80] --master : 사용자 마스터 전략 검증 — 1h추세 필터 + 1m 눌림목끝 진입.
    //   1h(거인): EMA20>EMA50 & 종가>EMA20 상승추세. 1m(스나이퍼): 과매도(RSI<35) 눌림 후 반등시작.
    //   1h필터 ON vs OFF, 청산무관 전방수익률(+30m/+2h/+6h). 1m 데이터.
    private static async Task RunMasterAsync()
    {
        int pages1m = BbExpandPages >= 12 ? BbExpandPages : 16;   // 1m: 16p ≈ 16.6일
        var uni = UseMajors ? LargeCaps : symbols;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  MASTER 전략 검증 — {uni.Length}심볼 / 1m {pages1m}p(~{pages1m * 1500 / 60 / 24}일)");
        Console.WriteLine("  1h(거인): EMA20>EMA50 & 종가>EMA20  |  1m(스나이퍼): RSI<35 눌림후 양봉+직전고가돌파+RSI상승");
        Console.WriteLine("================================================================");
        Console.WriteLine("  1h필터 항상 ON. 1m 트리거 4변형 비교 + 세분 호라이즌(15/30/60/120분).");
        int[] H = { 15, 30, 60, 120 };
        string[] hl = { "15m", "30m", "1h", "2h" };
        int VAR = 4;
        string[] vn = { "검증본RSI<35반등", "라이브RSI43~55", "라이브+거래량스퍼트", "RSI<40+거래량" };
        var N = new int[VAR]; var cnt = new int[VAR, 4]; var up = new int[VAR, 4]; var sum = new double[VAR, 4];
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> h1, m1;
            try { h1 = await FetchKlines1hAsync(sym, 1); m1 = await FetchKlines1mAsync(sym, pages1m); }
            catch { Console.WriteLine("fail"); continue; }
            if (h1.Count < 60 || m1.Count < 500) { Console.WriteLine("skip"); continue; }
            var hc = new double[h1.Count]; for (int t = 0; t < h1.Count; t++) hc[t] = (double)h1[t].ClosePrice;
            var he20 = EmaC(hc, 20); var he50 = EmaC(hc, 50);
            var hourUp = new Dictionary<long, bool>();
            for (int t = 0; t < h1.Count; t++) hourUp[((DateTimeOffset)h1[t].OpenTime).ToUnixTimeMilliseconds()] = (hc[t] > he20[t] && he20[t] > he50[t]);
            int n = m1.Count; var C = new double[n]; var O = new double[n]; var Hi = new double[n]; var Lo = new double[n]; var Vol = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)m1[t].ClosePrice; O[t] = (double)m1[t].OpenPrice; Hi[t] = (double)m1[t].HighPrice; Lo[t] = (double)m1[t].LowPrice; Vol[t] = (double)m1[t].Volume; }
            var rsi = BtRsiArr(C, 14); var ema20m = EmaC(C, 20);
            var busy = new int[VAR]; for (int v = 0; v < VAR; v++) busy[v] = -1;
            for (int i = 30; i < n - 121; i++)
            {
                long hourMs = (((DateTimeOffset)m1[i].OpenTime).ToUnixTimeMilliseconds() / 3600000L) * 3600000L;
                if (!(hourUp.TryGetValue(hourMs, out var u) && u)) continue;   // 1h 상승추세만 (항상 ON)
                bool pulled35 = false, pulled40 = false;
                for (int q = i - 10; q <= i; q++) { if (rsi[q] < 35) pulled35 = true; if (rsi[q] < 40) pulled40 = true; }
                bool green = C[i] > O[i], reclaim = C[i] > Hi[i - 1], rsiUp = rsi[i] > rsi[i - 1];
                double avgV = 0; for (int q = i - 20; q < i; q++) avgV += Vol[q]; avgV /= 20.0;
                double range = Hi[i] - Lo[i], body = Math.Abs(C[i] - O[i]);
                double volR = avgV > 0 ? Vol[i] / avgV : 0, buyR = range > 0 ? (C[i] - Lo[i]) / range : 0, peff = range > 0 ? body / range : 0;
                bool spurt = volR >= 2.5 && buyR > 0.6 && peff >= 0.5 && green;
                var sig = new bool[VAR];
                sig[0] = pulled35 && green && reclaim && rsiUp;
                sig[1] = rsi[i] > 43 && rsi[i] < 55 && C[i] > ema20m[i];
                sig[2] = sig[1] && spurt;
                sig[3] = pulled40 && spurt;
                for (int v = 0; v < VAR; v++)
                {
                    if (!sig[v] || i <= busy[v]) continue;
                    busy[v] = i + 30; double e = C[i]; N[v]++;
                    for (int h = 0; h < 4; h++) { int j = i + H[h]; if (j < n) { cnt[v, h]++; double r = C[j] / e - 1; sum[v, h] += r; if (r > 0) up[v, h]++; } }
                }
            }
            Console.WriteLine("ok");
        }
        Console.WriteLine();
        double lev = 20, margin = 100, costPct = 0.0018;  // 20x, 마진$100, 왕복비용 0.18%(수수료0.08+슬립0.10)
        Console.WriteLine($"  (1h 상추 필터 ON. {lev}x·마진${margin}·왕복비용{costPct * 100:F2}% 반영. 호라이즌별 상승%/평균%/건당순익$/총순익$)");
        for (int v = 0; v < VAR; v++)
        {
            Console.WriteLine($"── {vn[v]} — 진입 {N[v]}건 ──");
            for (int h = 0; h < 4; h++)
            {
                int cc = cnt[v, h]; if (cc == 0) continue;
                double up_ = 100.0 * up[v, h] / cc, m = sum[v, h] / cc * 100;
                double netPerTrade = margin * (sum[v, h] / cc - costPct) * lev;
                double totalNet = margin * lev * (sum[v, h] - costPct * cc);
                Console.WriteLine($"    +{hl[h],3}: 상승 {up_,5:F1}%  평균 {m,7:F3}%  건당 {netPerTrade,7:F2}$  총 {totalNet,9:F0}$");
            }
        }
        Console.WriteLine();
        Console.WriteLine("  [판정] 승률 아닌 *총순익$* 최대인 (변형×익절시점) 채택. 20x는 수수료가 커 단기익절이 손해일 수 있음.");
    }

    // [v5.23.83] --h1m1-3yr : 라이브 H1M1 새 구조(1h판단)를 3년 1h 데이터로 검증.
    //   진입판단 = 직전 닫힌 1h봉 종가>EMA20 + MACD 골든크로스(macd[t]>sig[t] & macd[t-1]<=sig[t-1]).
    //   진입가 = 그 1h 종가(1m 눌림타이밍은 ±소수점이라 1h종가 근사). 단일포지션(비중복). LONG only.
    //   청산 = TP/SL 매트릭스 시뮬 + 청산무관 전방수익률. 20x·마진$100·왕복0.18%. 연도별 분해.
    private static async Task RunH1M1ThreeYearAsync()
    {
        var uni = UseMajors ? LargeCaps : symbols;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  H1M1 3년 검증 — {uni.Length}심볼 / 1h 28p(~3.2년)");
        Console.WriteLine("  진입판단: 직전 1h봉 종가>EMA20 + MACD 골든크로스 | 진입가=1h종가 | 단일포지션");
        Console.WriteLine("================================================================");
        double lev = 20, margin = 100, costPct = 0.0018;
        var cfgs = new (double tp, double sl, string name)[] { (0.010, 0.030, "1:3(1%/3%)"), (0.010, 0.020, "1:2(1%/2%)"), (0.010, 0.010, "1:1(1%/1%)") };
        int CFG = cfgs.Length;
        int[] H = { 1, 4, 12, 24 }; string[] hl = { "+1h", "+4h", "+12h", "+24h" };
        var trades = new int[CFG]; var wins = new int[CFG]; var net = new double[CFG]; var heldSum = new long[CFG];
        var fcnt = new int[4]; var fup = new int[4]; var fsum = new double[4];
        var bcnt = new int[4]; var bup = new int[4]; var bsum = new double[4];   // 베이스라인(전 봉 무조건 진입)
        int totalSignals = 0;
        var perYearTr = new Dictionary<int, int>(); var perYearNet = new Dictionary<int, double>(); var perYearWin = new Dictionary<int, int>();
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlines1hAsync(sym, 28); } catch { Console.WriteLine("fail"); continue; }
            int n = kl.Count;
            if (n < 200) { Console.WriteLine("skip"); continue; }
            var C = new double[n]; var Hi = new double[n]; var Lo = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)kl[t].ClosePrice; Hi[t] = (double)kl[t].HighPrice; Lo[t] = (double)kl[t].LowPrice; }
            var ema20 = EmaC(C, 20); var (macd, sig) = MacdSeries(C);
            // 베이스라인: 모든 봉에서 무조건 LONG 진입했다면(신호 무관) 전방수익률 — 시장 자체의 상방편향 기준선
            for (int t = 30; t < n - 1; t++)
                for (int h = 0; h < 4; h++) { int j = t + H[h]; if (j < n) { bcnt[h]++; double r = C[j] / C[t] - 1; bsum[h] += r; if (r > 0) bup[h]++; } }
            int busyUntil = -1;   // 단일포지션(대표 1:3 보유기간 기준 비중복)
            for (int t = 30; t < n - 1; t++)
            {
                if (t <= busyUntil) continue;
                bool up = C[t] > ema20[t];
                bool golden = macd[t] > sig[t] && macd[t - 1] <= sig[t - 1];
                if (!(up && golden)) continue;
                totalSignals++;
                double e = C[t]; int yr = kl[t].OpenTime.Year;
                for (int h = 0; h < 4; h++) { int j = t + H[h]; if (j < n) { fcnt[h]++; double r = C[j] / e - 1; fsum[h] += r; if (r > 0) fup[h]++; } }
                int heldCfg0 = 1;
                for (int ci = 0; ci < CFG; ci++)
                {
                    double tp = e * (1 + cfgs[ci].tp), sl = e * (1 - cfgs[ci].sl);
                    int outcome = 0, held = 1;
                    for (int j = t + 1; j < n && j <= t + 480; j++)   // 최대 20일(480h) 보유
                    {
                        held = j - t;
                        if (Lo[j] <= sl) { outcome = -1; break; }     // SL 먼저(보수적)
                        if (Hi[j] >= tp) { outcome = 1; break; }
                    }
                    double r = outcome == 1 ? cfgs[ci].tp : outcome == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, t + 480)] / e - 1);
                    double pnl = margin * lev * (r - costPct);
                    trades[ci]++; net[ci] += pnl; heldSum[ci] += held; if (r > 0) wins[ci]++;
                    if (ci == 0)
                    {
                        heldCfg0 = held;
                        perYearTr.TryGetValue(yr, out var a); perYearTr[yr] = a + 1;
                        perYearNet.TryGetValue(yr, out var b); perYearNet[yr] = b + pnl;
                        if (r > 0) { perYearWin.TryGetValue(yr, out var w); perYearWin[yr] = w + 1; }
                    }
                }
                busyUntil = t + heldCfg0;
            }
            Console.WriteLine($"ok ({n}h)");
        }
        Console.WriteLine();
        Console.WriteLine($"  총 신호 {totalSignals}건 (20x·마진${margin}·왕복{costPct * 100:F2}%)");
        Console.WriteLine("  [청산무관 전방수익률] 신호 vs 베이스라인(전 봉 무조건진입=시장 상방편향)");
        for (int h = 0; h < 4; h++)
        {
            int cc = fcnt[h], bc = bcnt[h]; if (cc == 0 || bc == 0) continue;
            double sUp = 100.0 * fup[h] / cc, bUp = 100.0 * bup[h] / bc;
            double sAvg = fsum[h] / cc * 100, bAvg = bsum[h] / bc * 100;
            Console.WriteLine($"    {hl[h],4}: 신호 상승 {sUp,5:F1}% 평균 {sAvg,7:F3}%  |  베이스 상승 {bUp,5:F1}% 평균 {bAvg,7:F3}%  |  엣지 {sUp - bUp,6:+0.0;-0.0}%p");
        }
        Console.WriteLine("  [TP/SL 시뮬 — 전량 1:N]");
        for (int ci = 0; ci < CFG; ci++) { int tr = trades[ci]; if (tr == 0) continue; double wr = 100.0 * wins[ci] / tr; double be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100; Console.WriteLine($"    {cfgs[ci].name,-12}: {tr}건  WR {wr,5:F1}%(손익분기 {be:F0}%)  평균보유 {(double)heldSum[ci] / tr,5:F1}h  총순익 {net[ci],10:F0}$  건당 {net[ci] / tr,7:F2}$"); }
        Console.WriteLine("  [연도별 — 대표 1:3]");
        foreach (var yr in perYearTr.Keys.OrderBy(x => x)) { int tr = perYearTr[yr]; perYearWin.TryGetValue(yr, out var w); perYearNet.TryGetValue(yr, out var nt); Console.WriteLine($"    {yr}: {tr,5}건  WR {100.0 * w / Math.Max(1, tr),5:F1}%  순익 {nt,10:F0}$"); }
        Console.WriteLine();
        Console.WriteLine("  [주의] 1m 진입타이밍 생략(1h종가 근사) + SL동봉 보수가정. 백테는 라이브와 괴리 있음(실거래 DB가 최종잣대).");
    }

    // [v5.23.83] --h1m1-trend : 사용자 지적 반영 — "여러 1h봉 상승추세 확인 후, 추세 안 눌림자리에서 선행 진입".
    //   후행 MACD골든크로스(꼭대기 매수) 대신 (a)여러봉 추세필터 (b)EMA20까지 눌렸다 되돌림(눌림매수) 검증.
    //   3변형 비교: V0 추세만+즉시 / V1 추세+EMA20눌림 / V2 강추세+EMA20눌림. 신호 vs 베이스라인 + TP/SL.
    private static async Task RunH1M1TrendAsync()
    {
        var uni = UseMajors ? LargeCaps : symbols;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  H1M1 추세+눌림 3년 검증 — {uni.Length}심볼 / 1h 28p(~3.2년)");
        Console.WriteLine("  추세=EMA20>EMA50 & 둘다 우상향 & 종가>EMA50(여러봉) | 진입=추세속 EMA20 눌림되돌림");
        Console.WriteLine("================================================================");
        double lev = 20, margin = 100, costPct = 0.0018;
        var cfgs = new (double tp, double sl, string name)[] { (0.020, 0.010, "TP2/SL1"), (0.015, 0.010, "TP1.5/SL1"), (0.010, 0.010, "TP1/SL1"), (0.010, 0.030, "TP1/SL3") };
        int CFG = cfgs.Length, VAR = 4;
        string[] vn = { "추세만+즉시", "추세+EMA20눌림", "강추세+EMA20눌림", "강추세+EMA20지정가(선행)" };
        int[] H = { 1, 4, 12, 24 }; string[] hl = { "+1h", "+4h", "+12h", "+24h" };
        var N = new int[VAR];
        var trades = new int[VAR, CFG]; var wins = new int[VAR, CFG]; var net = new double[VAR, CFG]; var heldSum = new long[VAR, CFG];
        var fcnt = new int[VAR, 4]; var fup = new int[VAR, 4]; var fsum = new double[VAR, 4];
        var bcnt = new int[4]; var bup = new int[4]; var bsum = new double[4];
        var pyTr = new Dictionary<int, int>(); var pyWin = new Dictionary<int, int>(); var pyNet = new Dictionary<int, double>();   // 선행변형(v3) TP2/SL1 연도별
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlines1hAsync(sym, 28); } catch { Console.WriteLine("fail"); continue; }
            int n = kl.Count;
            if (n < 200) { Console.WriteLine("skip"); continue; }
            var C = new double[n]; var O = new double[n]; var Hi = new double[n]; var Lo = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)kl[t].ClosePrice; O[t] = (double)kl[t].OpenPrice; Hi[t] = (double)kl[t].HighPrice; Lo[t] = (double)kl[t].LowPrice; }
            var ema20 = EmaC(C, 20); var ema50 = EmaC(C, 50);
            for (int t = 60; t < n - 1; t++)
                for (int h = 0; h < 4; h++) { int j = t + H[h]; if (j < n) { bcnt[h]++; double r = C[j] / C[t] - 1; bsum[h] += r; if (r > 0) bup[h]++; } }
            var busyUntil = new int[VAR]; for (int v = 0; v < VAR; v++) busyUntil[v] = -1;
            for (int t = 60; t < n - 1; t++)
            {
                // ── 여러 1h봉 상승추세 확인 ──
                bool upBasic = ema20[t] > ema50[t] && ema50[t] > ema50[t - 12] && ema20[t] > ema20[t - 6] && C[t] > ema50[t];
                bool upStrong = upBasic && ema50[t] > ema50[t - 24] && C[t] > ema20[t];
                // ── 추세 속 EMA20 눌림되돌림(선행 눌림매수) ──
                bool pulled = false; for (int q = t - 5; q <= t; q++) if (Lo[q] <= ema20[q] * 1.002) { pulled = true; break; }
                bool resume = C[t] > O[t] && C[t] > ema20[t] && C[t] < ema20[t] * 1.015;   // MA 위로 복귀 양봉, 과확장 아님
                var sig = new bool[VAR];
                sig[0] = upBasic && C[t] > O[t] && C[t] > ema20[t] && C[t] < ema20[t] * 1.015;   // 추세속 즉시(눌림무관)
                sig[1] = upBasic && pulled && resume;
                sig[2] = upStrong && pulled && resume;
                sig[3] = upStrong && Lo[t] <= ema20[t] * 1.001 && Hi[t] >= ema20[t];   // 강추세속 EMA20 지정가 눌림체결(선행)
                for (int v = 0; v < VAR; v++)
                {
                    if (!sig[v] || t <= busyUntil[v]) continue;
                    N[v]++; double e = (v == 3) ? ema20[t] : C[t];   // 선행변형은 EMA20 지정가 체결 가정
                    for (int h = 0; h < 4; h++) { int j = t + H[h]; if (j < n) { fcnt[v, h]++; double r = C[j] / e - 1; fsum[v, h] += r; if (r > 0) fup[v, h]++; } }
                    int held0 = 1;
                    for (int ci = 0; ci < CFG; ci++)
                    {
                        double tp = e * (1 + cfgs[ci].tp), sl = e * (1 - cfgs[ci].sl);
                        int outcome = 0, held = 1;
                        for (int j = t + 1; j < n && j <= t + 480; j++) { held = j - t; if (Lo[j] <= sl) { outcome = -1; break; } if (Hi[j] >= tp) { outcome = 1; break; } }
                        double r = outcome == 1 ? cfgs[ci].tp : outcome == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, t + 480)] / e - 1);
                        double pnl = margin * lev * (r - costPct);
                        trades[v, ci]++; net[v, ci] += pnl; heldSum[v, ci] += held; if (r > 0) wins[v, ci]++;
                        if (ci == 0) held0 = held;
                        if (v == 3 && ci == 0)   // 선행변형 TP2/SL1 연도별
                        {
                            int yr = kl[t].OpenTime.Year;
                            pyTr.TryGetValue(yr, out var a); pyTr[yr] = a + 1;
                            pyNet.TryGetValue(yr, out var b); pyNet[yr] = b + pnl;
                            if (r > 0) { pyWin.TryGetValue(yr, out var w); pyWin[yr] = w + 1; }
                        }
                    }
                    busyUntil[v] = t + held0;
                }
            }
            Console.WriteLine($"ok ({n}h)");
        }
        Console.WriteLine();
        Console.WriteLine($"  (20x·마진${margin}·왕복{costPct * 100:F2}%. 베이스라인=전 봉 무조건진입)");
        for (int h = 0; h < 4; h++) { int bc = bcnt[h]; if (bc == 0) continue; Console.WriteLine($"  [베이스 {hl[h]}] 상승 {100.0 * bup[h] / bc,5:F1}%  평균 {bsum[h] / bc * 100,7:F3}%"); }
        for (int v = 0; v < VAR; v++)
        {
            Console.WriteLine($"── {vn[v]} — 진입 {N[v]}건 ──");
            for (int h = 0; h < 4; h++)
            {
                int cc = fcnt[v, h], bc = bcnt[h]; if (cc == 0 || bc == 0) continue;
                double sUp = 100.0 * fup[v, h] / cc, bUp = 100.0 * bup[h] / bc;
                Console.WriteLine($"    {hl[h],4}: 신호 상승 {sUp,5:F1}% 평균 {fsum[v, h] / cc * 100,7:F3}%  | 엣지(vs베이스) {sUp - bUp,6:+0.0;-0.0}%p");
            }
            for (int ci = 0; ci < CFG; ci++)
            {
                int tr = trades[v, ci]; if (tr == 0) continue;
                double wr = 100.0 * wins[v, ci] / tr, be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100;
                Console.WriteLine($"      {cfgs[ci].name,-10}: WR {wr,5:F1}%(분기 {be:F0}%) 보유 {(double)heldSum[v, ci] / tr,4:F1}h  총 {net[v, ci],9:F0}$  건당 {net[v, ci] / tr,6:F2}$");
            }
        }
        Console.WriteLine();
        Console.WriteLine("  [연도별 — 선행변형(EMA20지정가) TP2/SL1]");
        foreach (var yr in pyTr.Keys.OrderBy(x => x)) { int tr = pyTr[yr]; pyWin.TryGetValue(yr, out var w); pyNet.TryGetValue(yr, out var nt); Console.WriteLine($"    {yr}: {tr,5}건  WR {100.0 * w / Math.Max(1, tr),5:F1}%  순익 {nt,9:F0}$"); }
        Console.WriteLine();
        Console.WriteLine("  [판정] 베이스 대비 엣지(+%p) 양수 & TP/SL 순익 양수만 유효. 단 EMA20 지정가체결은 낙관(슬리피지·미체결 라이브 할인).");
    }

    // [v5.23.83] --sqzlor : 새 엔진 검증 — [1h Squeeze Momentum 대세필터] + [1m Lorentzian 진입타점].
    //   1h: LazyBear Squeeze Momentum mom>0 & 상승(직전봉 대비) = 상승추세 ON (거인).
    //   1m: Lorentzian KNN LONG 신호(sig=+1) = 진입타점 (스나이퍼). 둘 다 충족 시 시장가 LONG(라이브 동일).
    //   신호 vs 베이스라인 엣지 + TP/SL. 단일포지션. 20x·마진$100·왕복0.18%.
    private static async Task RunSqueezeLorentzianAsync()
    {
        var uni = UseMajors ? LargeCaps : symbols;
        int pages1m = BbExpandPages >= 8 ? BbExpandPages : 12;   // 1m ~12일
        Console.WriteLine("================================================================");
        Console.WriteLine($"  SQZ+LOR 엔진 검증 — {uni.Length}심볼 / 1m {pages1m}p(~{pages1m * 1500 / 60 / 24}일) + 1h");
        Console.WriteLine("  1h: Squeeze Momentum mom>0 & 상승 | 1m: Lorentzian LONG | 둘 다 → 시장가 LONG");
        Console.WriteLine("================================================================");
        double lev = 20, margin = 100, costPct = 0.0018;
        var cfgs = new (double tp, double sl, string name)[] { (0.005, 0.005, "TP0.5/SL0.5"), (0.010, 0.010, "TP1/SL1"), (0.020, 0.010, "TP2/SL1"), (0.010, 0.030, "TP1/SL3") };
        int CFG = cfgs.Length;
        int[] H = { 5, 15, 30, 60 }; string[] hl = { "+5m", "+15m", "+30m", "+1h" };
        int N = 0, filtBull = 0;
        var trades = new int[CFG]; var wins = new int[CFG]; var net = new double[CFG]; var heldSum = new long[CFG];
        var fcnt = new int[4]; var fup = new int[4]; var fsum = new double[4];
        var bcnt = new int[4]; var bup = new int[4]; var bsum = new double[4];
        // 지정가 눌림 체결(0.15% 아래, 3분 내) — 시장가와 동일 신호 스트림에 병렬 회계
        const double dipPct = 0.0015; const int fillWin = 3;
        var ltr = new int[CFG]; var lwin = new int[CFG]; var lnet = new double[CFG]; long lfilled = 0, lmissed = 0;
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> m1, h1;
            try { m1 = await FetchKlines1mAsync(sym, pages1m); h1 = await FetchKlines1hAsync(sym, 1); }
            catch { Console.WriteLine("fail"); continue; }
            int n = m1.Count;
            if (n < 1500 || h1.Count < 60) { Console.WriteLine("skip"); continue; }
            // 1h Squeeze Momentum 상승추세 맵 (hour openMs → bullish)
            var Hh = new double[h1.Count]; var Hl = new double[h1.Count]; var Hc = new double[h1.Count];
            for (int t = 0; t < h1.Count; t++) { Hh[t] = (double)h1[t].HighPrice; Hl[t] = (double)h1[t].LowPrice; Hc[t] = (double)h1[t].ClosePrice; }
            var (mom, _on, _off) = SqueezeMom(Hh, Hl, Hc, 20, 2.0, 1.5);
            var hourBull = new Dictionary<long, bool>();
            for (int t = 1; t < h1.Count; t++)
            {
                long ms = (((DateTimeOffset)h1[t].OpenTime).ToUnixTimeMilliseconds() / 3600000L) * 3600000L;
                hourBull[ms] = mom[t] > 0 && mom[t] > mom[t - 1];
            }
            // 1m 배열 + Lorentzian 엔진
            var C = new double[n]; var Hi = new double[n]; var Lo = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)m1[t].ClosePrice; Hi[t] = (double)m1[t].HighPrice; Lo[t] = (double)m1[t].LowPrice; }
            // 베이스라인: 모든 1m봉 전방수익
            for (int t = 520; t < n - 1; t++)
                for (int hh = 0; hh < 4; hh++) { int j = t + H[hh]; if (j < n) { bcnt[hh]++; double r = C[j] / C[t] - 1; bsum[hh] += r; if (r > 0) bup[hh]++; } }
            var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            int trained = 0, busyUntil = -1;
            for (int i = 520; i < n - 1; i++)
            {
                // walk-forward 학습 (i 이전 봉까지, 500봉 윈도우 — PredictAtBar와 정합)
                while (trained < i)
                {
                    int ws = Math.Max(0, trained - 499);
                    var fs = LorentzianFeatures.Extract(m1.GetRange(ws, trained - ws + 1));
                    if (fs != null) engine.AddSample(fs, LorentzianGuard.LabelForBar(m1, trained));
                    trained++;
                }
                if (i <= busyUntil) continue;
                // 1h 대세필터: 직전 닫힌 1h봉(=현재 forming hour의 이전 시간) 상승추세만
                long prevHourMs = ((((DateTimeOffset)m1[i].OpenTime).ToUnixTimeMilliseconds() / 3600000L) - 1) * 3600000L;
                if (!(hourBull.TryGetValue(prevHourMs, out var bull) && bull)) continue;
                filtBull++;
                // 1m Lorentzian 진입타점
                var (sig, wr, ready) = LorentzianGuard.PredictAtBar(m1, i, engine, 500);
                if (!ready || sig != 1) continue;
                N++; double e = C[i];
                for (int hh = 0; hh < 4; hh++) { int j = i + H[hh]; if (j < n) { fcnt[hh]++; double r = C[j] / e - 1; fsum[hh] += r; if (r > 0) fup[hh]++; } }
                int held0 = 1;
                for (int ci = 0; ci < CFG; ci++)
                {
                    double tp = e * (1 + cfgs[ci].tp), sl = e * (1 - cfgs[ci].sl);
                    int outc = 0, held = 1;
                    for (int j = i + 1; j < n && j <= i + 480; j++) { held = j - i; if (Lo[j] <= sl) { outc = -1; break; } if (Hi[j] >= tp) { outc = 1; break; } }
                    double r = outc == 1 ? cfgs[ci].tp : outc == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, i + 480)] / e - 1);
                    double pnl = margin * lev * (r - costPct);
                    trades[ci]++; net[ci] += pnl; heldSum[ci] += held; if (r > 0) wins[ci]++;
                    if (ci == 0) held0 = held;
                }
                // 지정가 눌림 체결 회계 (0.15% 아래 3분 내 터치 시 그 가격 체결)
                double dipLimit = e * (1 - dipPct); int fillBar = -1;
                for (int j = i + 1; j < n && j <= i + fillWin; j++) { if (Lo[j] <= dipLimit) { fillBar = j; break; } }
                if (fillBar < 0) lmissed++;
                else
                {
                    lfilled++; double le = dipLimit;
                    for (int ci = 0; ci < CFG; ci++)
                    {
                        double tp = le * (1 + cfgs[ci].tp), sl = le * (1 - cfgs[ci].sl);
                        int outc = 0; for (int j = fillBar + 1; j < n && j <= fillBar + 480; j++) { if (Lo[j] <= sl) { outc = -1; break; } if (Hi[j] >= tp) { outc = 1; break; } }
                        double r = outc == 1 ? cfgs[ci].tp : outc == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, fillBar + 480)] / le - 1);
                        ltr[ci]++; lnet[ci] += margin * lev * (r - costPct); if (r > 0) lwin[ci]++;
                    }
                }
                busyUntil = i + held0;
            }
            Console.WriteLine($"ok (1h상승봉 {filtBull}, 진입 {N})");
        }
        Console.WriteLine();
        Console.WriteLine($"  총 진입 {N}건 (20x·마진${margin}·왕복{costPct * 100:F2}%. 베이스=전 1m봉 무조건진입)");
        Console.WriteLine("  [청산무관 전방수익률] 신호 vs 베이스라인");
        for (int hh = 0; hh < 4; hh++)
        {
            int cc = fcnt[hh], bc = bcnt[hh]; if (cc == 0 || bc == 0) continue;
            double sUp = 100.0 * fup[hh] / cc, bUp = 100.0 * bup[hh] / bc;
            Console.WriteLine($"    {hl[hh],4}: 신호 상승 {sUp,5:F1}% 평균 {fsum[hh] / cc * 100,7:F3}%  | 베이스 상승 {bUp,5:F1}% 평균 {bsum[hh] / bc * 100,7:F3}%  | 엣지 {sUp - bUp,6:+0.0;-0.0}%p");
        }
        Console.WriteLine("  [TP/SL 시뮬]");
        for (int ci = 0; ci < CFG; ci++)
        {
            int tr = trades[ci]; if (tr == 0) continue;
            double wrr = 100.0 * wins[ci] / tr, be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100;
            Console.WriteLine($"    {cfgs[ci].name,-12}: {tr}건 WR {wrr,5:F1}%(분기 {be:F0}%) 보유 {(double)heldSum[ci] / tr,5:F1}m 총 {net[ci],9:F0}$ 건당 {net[ci] / tr,6:F2}$");
        }
        Console.WriteLine($"  [지정가 눌림 체결 {dipPct * 100:F2}%↓/{fillWin}분 — 체결 {lfilled}건 / 미체결 {lmissed}건, 체결률 {(lfilled + lmissed > 0 ? 100.0 * lfilled / (lfilled + lmissed) : 0):F0}%]");
        for (int ci = 0; ci < CFG; ci++)
        {
            int tr = ltr[ci]; if (tr == 0) continue;
            double wrr = 100.0 * lwin[ci] / tr, be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100;
            Console.WriteLine($"    {cfgs[ci].name,-12}: {tr}건 WR {wrr,5:F1}%(분기 {be:F0}%) 총 {lnet[ci],9:F0}$ 건당 {lnet[ci] / tr,6:F2}$");
        }
        Console.WriteLine();
        Console.WriteLine("  [판정] 시장가 vs 지정가눌림 비교. 지정가 순익 양수면 라이브는 지정가 진입으로 구현. (지정가 체결가정은 다소 낙관)");
    }

    // [v5.23.84] --bbbounce : 사용자 지정 진입 — 1h BB 스퀴즈(폭 최소) + 하단밴드 지지 반등에서 LONG.
    //   "상단 돌파 추격 금지, 하단 지지 바닥 매수". 상승추세(종가>SMA50) 컨텍스트 + 폭이 최근 최소 + 하단터치후 양봉반등.
    private static async Task RunBbBounceAsync()
    {
        var uni = UseMajors ? LargeCaps : symbols;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  BB 스퀴즈+하단반등 진입 검증 — {uni.Length}심볼 / 1h 28p(~3.2년)");
        Console.WriteLine("  진입: 종가>SMA50 & BB폭 최근20봉 최소권 & 최근3봉 하단밴드 터치 & 현재 양봉+종가>하단&>직전종가");
        Console.WriteLine("================================================================");
        double lev = 20, margin = 100, costPct = 0.0018;
        var cfgs = new (double tp, double sl, string name)[] { (0.020, 0.010, "TP2/SL1"), (0.030, 0.015, "TP3/SL1.5"), (0.030, 0.020, "TP3/SL2"), (0.020, 0.020, "TP2/SL2"), (0.010, 0.030, "TP1/SL3") };
        int CFG = cfgs.Length;
        int[] H = { 1, 4, 12, 24 }; string[] hl = { "+1h", "+4h", "+12h", "+24h" };
        int N = 0;
        var trades = new int[CFG]; var wins = new int[CFG]; var net = new double[CFG]; var heldSum = new long[CFG];
        var fcnt = new int[4]; var fup = new int[4]; var fsum = new double[4];
        var bcnt = new int[4]; var bup = new int[4]; var bsum = new double[4];
        var pyTr = new Dictionary<int, int>(); var pyWin = new Dictionary<int, int>(); var pyNet = new Dictionary<int, double>();
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlines1hAsync(sym, 28); } catch { Console.WriteLine("fail"); continue; }
            int n = kl.Count;
            if (n < 200) { Console.WriteLine("skip"); continue; }
            var C = new double[n]; var O = new double[n]; var Hi = new double[n]; var Lo = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)kl[t].ClosePrice; O[t] = (double)kl[t].OpenPrice; Hi[t] = (double)kl[t].HighPrice; Lo[t] = (double)kl[t].LowPrice; }
            var sma50 = SmaArr(C, 50);
            var (mid, up, lo) = BbArr(C, 20, 2.0);
            var width = new double[n]; for (int t = 0; t < n; t++) width[t] = mid[t] > 0 ? (up[t] - lo[t]) / mid[t] : 1;
            for (int t = 60; t < n - 1; t++)
                for (int hh = 0; hh < 4; hh++) { int j = t + H[hh]; if (j < n) { bcnt[hh]++; double r = C[j] / C[t] - 1; bsum[hh] += r; if (r > 0) bup[hh]++; } }
            int busyUntil = -1;
            for (int t = 60; t < n - 1; t++)
            {
                if (t <= busyUntil) continue;
                if (!(C[t] > sma50[t])) continue;                                   // 상승추세 컨텍스트
                double wmin = width[t]; for (int q = t - 20; q <= t; q++) if (width[q] < wmin) wmin = width[q];
                bool squeeze = width[t] <= wmin * 1.10;                              // BB폭 최근20봉 최소권(스퀴즈)
                bool lowerTouch = false; for (int q = t - 2; q <= t; q++) if (Lo[q] <= lo[q] * 1.001) { lowerTouch = true; break; }  // 하단밴드 터치(지지)
                bool bounce = C[t] > O[t] && C[t] > lo[t] && C[t] > C[t - 1];        // 양봉+하단위 복귀+직전종가 돌파(반등)
                if (!(squeeze && lowerTouch && bounce)) continue;
                N++; double e = C[t]; int yr = kl[t].OpenTime.Year;
                for (int hh = 0; hh < 4; hh++) { int j = t + H[hh]; if (j < n) { fcnt[hh]++; double r = C[j] / e - 1; fsum[hh] += r; if (r > 0) fup[hh]++; } }
                int held0 = 1;
                for (int ci = 0; ci < CFG; ci++)
                {
                    double tp = e * (1 + cfgs[ci].tp), sl = e * (1 - cfgs[ci].sl);
                    int outc = 0, held = 1;
                    for (int j = t + 1; j < n && j <= t + 480; j++) { held = j - t; if (Lo[j] <= sl) { outc = -1; break; } if (Hi[j] >= tp) { outc = 1; break; } }
                    double r = outc == 1 ? cfgs[ci].tp : outc == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, t + 480)] / e - 1);
                    double pnl = margin * lev * (r - costPct);
                    trades[ci]++; net[ci] += pnl; heldSum[ci] += held; if (r > 0) wins[ci]++;
                    if (ci == 0) held0 = held;
                    if (ci == 0) { pyTr.TryGetValue(yr, out var a); pyTr[yr] = a + 1; pyNet.TryGetValue(yr, out var b); pyNet[yr] = b + pnl; if (r > 0) { pyWin.TryGetValue(yr, out var w); pyWin[yr] = w + 1; } }
                }
                busyUntil = t + held0;
            }
            Console.WriteLine($"ok (진입 {N})");
        }
        Console.WriteLine();
        Console.WriteLine($"  총 진입 {N}건 (20x·마진${margin}·왕복{costPct * 100:F2}%. 베이스=전 1h봉)");
        Console.WriteLine("  [청산무관 전방수익률] 신호 vs 베이스라인");
        for (int hh = 0; hh < 4; hh++) { int cc = fcnt[hh], bc = bcnt[hh]; if (cc == 0 || bc == 0) continue; double sUp = 100.0 * fup[hh] / cc, bUp = 100.0 * bup[hh] / bc; Console.WriteLine($"    {hl[hh],4}: 신호 상승 {sUp,5:F1}% 평균 {fsum[hh] / cc * 100,7:F3}%  | 베이스 상승 {bUp,5:F1}% 평균 {bsum[hh] / bc * 100,7:F3}%  | 엣지 {sUp - bUp,6:+0.0;-0.0}%p"); }
        Console.WriteLine("  [TP/SL 시뮬]");
        for (int ci = 0; ci < CFG; ci++) { int tr = trades[ci]; if (tr == 0) continue; double wrr = 100.0 * wins[ci] / tr, be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100; Console.WriteLine($"    {cfgs[ci].name,-11}: {tr}건 WR {wrr,5:F1}%(분기 {be:F0}%) 보유 {(double)heldSum[ci] / tr,5:F1}h 총 {net[ci],9:F0}$ 건당 {net[ci] / tr,6:F2}$"); }
        Console.WriteLine("  [연도별 — TP2/SL1]");
        foreach (var yr in pyTr.Keys.OrderBy(x => x)) { int tr = pyTr[yr]; pyWin.TryGetValue(yr, out var w); pyNet.TryGetValue(yr, out var nt); Console.WriteLine($"    {yr}: {tr,5}건 WR {100.0 * w / Math.Max(1, tr),5:F1}% 순익 {nt,9:F0}$"); }
        Console.WriteLine();
        Console.WriteLine("  [판정] 엣지(+%p) 양수 & TP/SL 순익 양수 & 연도별 견고면 라이브 채택(하단 지지 매수).");
    }

    // [v5.23.83] --sqzlor15 : 1h Squeeze Momentum 대세필터 + 15m Lorentzian 진입(1m 수수료함정 회피).
    //   1m 대비 15m은 봉당 움직임 ~10배 → 왕복비용 대비 엣지 여유. 시장가 vs 지정가눌림 + 연도별.
    private static async Task RunSqueezeLor15Async()
    {
        var uni = UseMajors ? LargeCaps : symbols;
        int pages15 = BbExpandPages >= 8 ? BbExpandPages : 40;   // 15m 40p ≈ 1.7년
        Console.WriteLine("================================================================");
        Console.WriteLine($"  SQZ+LOR(15m) 엔진 검증 — {uni.Length}심볼 / 15m {pages15}p(~{pages15 * 1500 * 15 / 60 / 24 / 365.0:F1}년) + 1h");
        Console.WriteLine("  1h: Squeeze Momentum mom>0 & 상승 | 15m: Lorentzian LONG | 둘 다 → LONG");
        Console.WriteLine("================================================================");
        double lev = 20, margin = 100, costPct = 0.0018;
        var cfgs = new (double tp, double sl, string name)[] { (0.010, 0.010, "TP1/SL1"), (0.015, 0.010, "TP1.5/SL1"), (0.020, 0.010, "TP2/SL1"), (0.020, 0.020, "TP2/SL2"), (0.010, 0.030, "TP1/SL3") };
        int CFG = cfgs.Length;
        int[] H = { 1, 2, 4, 8 }; string[] hl = { "+15m", "+30m", "+1h", "+2h" };
        int N = 0, filtBull = 0;
        var trades = new int[CFG]; var wins = new int[CFG]; var net = new double[CFG]; var heldSum = new long[CFG];
        var fcnt = new int[4]; var fup = new int[4]; var fsum = new double[4];
        var bcnt = new int[4]; var bup = new int[4]; var bsum = new double[4];
        const double dipPct = 0.003; const int fillWin = 3;   // 15m 0.3%↓, 3봉(45분) 내
        var ltr = new int[CFG]; var lwin = new int[CFG]; var lnet = new double[CFG]; long lfilled = 0, lmissed = 0;
        var pyTr = new Dictionary<int, int>(); var pyWin = new Dictionary<int, int>(); var pyNet = new Dictionary<int, double>();   // 지정가 TP1.5/SL1 연도별
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> k15, h1;
            try { k15 = await FetchKlines15mAsync(sym, pages15); h1 = await FetchKlines1hAsync(sym, 16); }
            catch { Console.WriteLine("fail"); continue; }
            int n = k15.Count;
            if (n < 1000 || h1.Count < 60) { Console.WriteLine("skip"); continue; }
            var Hh = new double[h1.Count]; var Hl = new double[h1.Count]; var Hc = new double[h1.Count];
            for (int t = 0; t < h1.Count; t++) { Hh[t] = (double)h1[t].HighPrice; Hl[t] = (double)h1[t].LowPrice; Hc[t] = (double)h1[t].ClosePrice; }
            var (mom, _on, _off) = SqueezeMom(Hh, Hl, Hc, 20, 2.0, 1.5);
            var hourBull = new Dictionary<long, bool>();
            for (int t = 1; t < h1.Count; t++) { long ms = (((DateTimeOffset)h1[t].OpenTime).ToUnixTimeMilliseconds() / 3600000L) * 3600000L; hourBull[ms] = mom[t] > 0 && mom[t] > mom[t - 1]; }
            var C = new double[n]; var Hi = new double[n]; var Lo = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)k15[t].ClosePrice; Hi[t] = (double)k15[t].HighPrice; Lo[t] = (double)k15[t].LowPrice; }
            for (int t = 520; t < n - 1; t++)
                for (int hh = 0; hh < 4; hh++) { int j = t + H[hh]; if (j < n) { bcnt[hh]++; double r = C[j] / C[t] - 1; bsum[hh] += r; if (r > 0) bup[hh]++; } }
            var engine = new LorentzianAnnEngine(sym, neighborsCount: 8, maxBarsBack: 2000, featureCount: LorentzianFeatures.FeatureCount);
            int trained = 0, busyUntil = -1;
            for (int i = 520; i < n - 1; i++)
            {
                while (trained < i)
                {
                    int ws = Math.Max(0, trained - 499);
                    var fs = LorentzianFeatures.Extract(k15.GetRange(ws, trained - ws + 1));
                    if (fs != null) engine.AddSample(fs, LorentzianGuard.LabelForBar(k15, trained));
                    trained++;
                }
                if (i <= busyUntil) continue;
                long prevHourMs = ((((DateTimeOffset)k15[i].OpenTime).ToUnixTimeMilliseconds() / 3600000L) - 1) * 3600000L;
                if (!(hourBull.TryGetValue(prevHourMs, out var bull) && bull)) continue;
                filtBull++;
                var (sig, wr, ready) = LorentzianGuard.PredictAtBar(k15, i, engine, 500);
                if (!ready || sig != 1) continue;
                N++; double e = C[i]; int yr = k15[i].OpenTime.Year;
                for (int hh = 0; hh < 4; hh++) { int j = i + H[hh]; if (j < n) { fcnt[hh]++; double r = C[j] / e - 1; fsum[hh] += r; if (r > 0) fup[hh]++; } }
                int held0 = 1;
                for (int ci = 0; ci < CFG; ci++)
                {
                    double tp = e * (1 + cfgs[ci].tp), sl = e * (1 - cfgs[ci].sl);
                    int outc = 0, held = 1;
                    for (int j = i + 1; j < n && j <= i + 192; j++) { held = j - i; if (Lo[j] <= sl) { outc = -1; break; } if (Hi[j] >= tp) { outc = 1; break; } }   // 최대 192봉(2일)
                    double r = outc == 1 ? cfgs[ci].tp : outc == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, i + 192)] / e - 1);
                    double pnl = margin * lev * (r - costPct);
                    trades[ci]++; net[ci] += pnl; heldSum[ci] += held; if (r > 0) wins[ci]++;
                    if (ci == 0) held0 = held;
                }
                // 지정가 눌림 (0.3%↓, 3봉 내) — maker 가정으로 비용 절반(0.09%)
                double dipLimit = e * (1 - dipPct); int fillBar = -1;
                for (int j = i + 1; j < n && j <= i + fillWin; j++) { if (Lo[j] <= dipLimit) { fillBar = j; break; } }
                if (fillBar < 0) lmissed++;
                else
                {
                    lfilled++; double le = dipLimit;
                    for (int ci = 0; ci < CFG; ci++)
                    {
                        double tp = le * (1 + cfgs[ci].tp), sl = le * (1 - cfgs[ci].sl);
                        int outc = 0; for (int j = fillBar + 1; j < n && j <= fillBar + 192; j++) { if (Lo[j] <= sl) { outc = -1; break; } if (Hi[j] >= tp) { outc = 1; break; } }
                        double r = outc == 1 ? cfgs[ci].tp : outc == -1 ? -cfgs[ci].sl : (C[Math.Min(n - 1, fillBar + 192)] / le - 1);
                        double pnl = margin * lev * (r - 0.0009);   // maker entry+taker exit ≈ 0.09%
                        ltr[ci]++; lnet[ci] += pnl; if (r > 0) lwin[ci]++;
                        if (ci == 1) { pyTr.TryGetValue(yr, out var a); pyTr[yr] = a + 1; pyNet.TryGetValue(yr, out var b); pyNet[yr] = b + pnl; if (r > 0) { pyWin.TryGetValue(yr, out var w); pyWin[yr] = w + 1; } }
                    }
                }
                busyUntil = i + held0;
            }
            Console.WriteLine($"ok (1h상승봉 {filtBull}, 진입 {N})");
        }
        Console.WriteLine();
        Console.WriteLine($"  총 진입 {N}건 (20x·마진${margin}·시장가왕복0.18%/지정가0.09%. 베이스=전 15m봉)");
        Console.WriteLine("  [청산무관 전방수익률] 신호 vs 베이스라인");
        for (int hh = 0; hh < 4; hh++) { int cc = fcnt[hh], bc = bcnt[hh]; if (cc == 0 || bc == 0) continue; double sUp = 100.0 * fup[hh] / cc, bUp = 100.0 * bup[hh] / bc; Console.WriteLine($"    {hl[hh],4}: 신호 상승 {sUp,5:F1}% 평균 {fsum[hh] / cc * 100,7:F3}%  | 베이스 상승 {bUp,5:F1}% 평균 {bsum[hh] / bc * 100,7:F3}%  | 엣지 {sUp - bUp,6:+0.0;-0.0}%p"); }
        Console.WriteLine("  [TP/SL 시뮬 — 시장가(0.18%)]");
        for (int ci = 0; ci < CFG; ci++) { int tr = trades[ci]; if (tr == 0) continue; double wrr = 100.0 * wins[ci] / tr, be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100; Console.WriteLine($"    {cfgs[ci].name,-11}: {tr}건 WR {wrr,5:F1}%(분기 {be:F0}%) 보유 {(double)heldSum[ci] / tr * 15,5:F0}m 총 {net[ci],9:F0}$ 건당 {net[ci] / tr,6:F2}$"); }
        Console.WriteLine($"  [TP/SL 시뮬 — 지정가눌림 maker(0.09%), 체결 {lfilled}/미체결 {lmissed}, 체결률 {(lfilled + lmissed > 0 ? 100.0 * lfilled / (lfilled + lmissed) : 0):F0}%]");
        for (int ci = 0; ci < CFG; ci++) { int tr = ltr[ci]; if (tr == 0) continue; double wrr = 100.0 * lwin[ci] / tr, be = cfgs[ci].sl / (cfgs[ci].sl + cfgs[ci].tp) * 100; Console.WriteLine($"    {cfgs[ci].name,-11}: {tr}건 WR {wrr,5:F1}%(분기 {be:F0}%) 총 {lnet[ci],9:F0}$ 건당 {lnet[ci] / tr,6:F2}$"); }
        Console.WriteLine("  [연도별 — 지정가 TP1.5/SL1]");
        foreach (var yr in pyTr.Keys.OrderBy(x => x)) { int tr = pyTr[yr]; pyWin.TryGetValue(yr, out var w); pyNet.TryGetValue(yr, out var nt); Console.WriteLine($"    {yr}: {tr,5}건 WR {100.0 * w / Math.Max(1, tr),5:F1}% 순익 {nt,9:F0}$"); }
        Console.WriteLine();
        Console.WriteLine("  [판정] 지정가 TP/SL 순익 양수 & 연도별 견고면 라이브(1h Squeeze + 15m Lorentzian + 지정가)로 구현.");
    }

    // [v5.23.80] --user4 : 사용자 지정 4전략 검증 (Lorentzian / VolSupertrend / RSI / 선형회귀채널)
    //   각 독립, BTC 상승장 레짐(--regime), 청산무관 전방수익률. Top? 는 symbols 사용.
    private static async Task RunUser4Async()
    {
        int pages = BbExpandPages >= 12 ? BbExpandPages : 24;
        var uni = UseMajors ? LargeCaps : symbols;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  USER4 전략 검증 — {uni.Length}심볼 / ~{pages * 1500 * 5 / 60 / 24}일 / 레짐:{(UseRegime ? "ON(BTC상승장)" : "OFF")}");
        Console.WriteLine("  전략: ①Lorentzian(KNN) ②VolSuperTrend ③RSI반전 ④선형회귀채널 하단반등");
        Console.WriteLine("================================================================");
        string[] names = { "Lorentzian", "VolSuperTrend", "RSI반전", "선형회귀채널", "Lor+거래량20선위" };
        int S = 5;
        var N = new int[S]; var up6 = new int[S]; var up24 = new int[S]; var c6 = new int[S]; var c24 = new int[S];
        var sum6 = new double[S]; var sum24 = new double[S];

        var btcUp = new Dictionary<long, bool>();
        if (UseRegime)
        {
            Console.Write("BTC 레짐 로딩... ");
            var bk = await FetchKlinesAsync("BTCUSDT", pages);
            var bc2 = new double[bk.Count]; for (int t = 0; t < bk.Count; t++) bc2[t] = (double)bk[t].ClosePrice;
            var be200 = EmaC(bc2, 200); var be50 = EmaC(bc2, 50);
            for (int t = 0; t < bk.Count; t++) btcUp[((DateTimeOffset)bk[t].OpenTime).ToUnixTimeMilliseconds()] = (bc2[t] > be200[t] && be50[t] > be200[t]);
            Console.WriteLine($"ok ({btcUp.Values.Count(v => v)}/{btcUp.Count} 상승)");
        }
        bool RegimeOk(IBinanceKline bar) => !UseRegime || (btcUp.TryGetValue(((DateTimeOffset)bar.OpenTime).ToUnixTimeMilliseconds(), out var u) && u);

        var lor = new MiniLorentzianService();
        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, pages); } catch { Console.WriteLine("fail"); continue; }
            int n = kl.Count; if (n < 600) { Console.WriteLine("skip"); continue; }
            var C = new double[n]; var H = new double[n]; var L = new double[n]; var O = new double[n]; var V = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)kl[t].ClosePrice; H[t] = (double)kl[t].HighPrice; L[t] = (double)kl[t].LowPrice; O[t] = (double)kl[t].OpenPrice; V[t] = (double)kl[t].Volume; }
            var rsi = BtRsiArr(C, 14); var atr = AtrArr(H, L, C, 14); var stDir = SupertrendDir(H, L, C, atr, 3.0);
            var (reg, lrLo, lrUp, slope) = LinRegChannel(C, 100, 2.0);
            var engine = lor.GetOrCreate(sym); int trained = 300;

            var busy = new int[S]; for (int s = 0; s < S; s++) busy[s] = -1;
            for (int i = 305; i < n - 2; i++)
            {
                while (trained <= i - 4) { var fs = LorentzianFeatures.Extract(kl.GetRange(0, trained + 1)); if (fs != null) { decimal a0 = kl[trained].ClosePrice, fu = kl[trained + 4].ClosePrice; engine.AddSample(fs, fu > a0 ? 1 : fu < a0 ? -1 : 0); } trained++; }
                bool rOk = RegimeOk(kl[i]);
                double volAvg = 0; for (int q = i - 20; q < i; q++) volAvg += V[q]; volAvg /= 20;
                var sig = new bool[S];
                // ① Lorentzian
                var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
                sig[0] = pred.IsReady && pred.Prediction > 0 && pred.PositiveRate >= 0.66f;
                // ② Volume SuperTrend AI: 슈퍼트렌드 상향전환 + 거래량 확인
                sig[1] = stDir[i] == 1 && stDir[i - 1] == -1 && volAvg > 0 && V[i] > volAvg * 1.2;
                // ③ RSI 반전: 30 아래→위 돌파
                sig[2] = rsi[i - 1] < 30 && rsi[i] >= 30;
                // ④ 선형회귀채널: 상승채널(slope>0) + 저가가 하단밴드 터치 + 양봉 반등
                sig[3] = slope[i] > 0 && lrLo[i] > 0 && L[i] <= lrLo[i] && C[i] > O[i];
                // ⑤ Lorentzian + 거래량 20선 위 (사용자 지정): 시그널 + V > 20봉평균
                sig[4] = sig[0] && volAvg > 0 && V[i] > volAvg;
                for (int s = 0; s < S; s++)
                {
                    if (!sig[s] || i <= busy[s] || !rOk) continue;
                    busy[s] = i + 24; double e = C[i]; N[s]++;
                    int j6 = i + 72, j24 = i + 288;
                    if (j6 < n) { c6[s]++; double r = C[j6] / e - 1; sum6[s] += r; if (r > 0) up6[s]++; }
                    if (j24 < n) { c24[s]++; double r = C[j24] / e - 1; sum24[s] += r; if (r > 0) up24[s]++; }
                }
            }
            Console.WriteLine("ok");
        }
        Console.WriteLine();
        Console.WriteLine($"{"전략",-16} {"진입",7} {"6h상승%",8} {"24h상승%",9} {"6h평균%",8} {"24h평균%",9}");
        for (int s = 0; s < S; s++)
        {
            double u6 = c6[s] > 0 ? 100.0 * up6[s] / c6[s] : 0, u24 = c24[s] > 0 ? 100.0 * up24[s] / c24[s] : 0;
            double m6 = c6[s] > 0 ? sum6[s] / c6[s] * 100 : 0, m24 = c24[s] > 0 ? sum24[s] / c24[s] * 100 : 0;
            Console.WriteLine($"{names[s],-16} {N[s],7} {u6,7:F1}% {u24,8:F1}% {m6,7:F2}% {m24,8:F2}%");
        }
        Console.WriteLine();
        Console.WriteLine("  [판정] 6h/24h 상승% 55%+ & 평균% 양수면 그 전략을 독립 진입으로 채택. 50% 부근=제외.");
    }

    private static async Task RunBatteryAsync()
    {
        int pages = BbExpandPages >= 12 ? BbExpandPages : 24;
        var uni = UseMajors ? LargeCaps : symbols;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  TA 전략 배터리 — {uni.Length}심볼({(UseMajors ? "대형주" : "잡알트")}) / ~{pages * 1500 * 5 / 60 / 24}일 / 청산무관 전방수익률");
        Console.WriteLine("================================================================");

        string[] names = {
            "RSI과매도반전","Stoch과매도반전","StochRSI반전","MACD골든크로스","MACD히스토전환",
            "BB하단반등","EMA9>21크로스","골든크로스50/200","Williams%R반전","CCI반전",
            "MFI과매도","돈치안20돌파","하이킨아시전환","RSI상승다이버전스","슈퍼트렌드전환",
            "거래량스파이크","EMA200회복","EMA20눌림반등" };
        int S = names.Length;
        var N = new int[S]; var up6 = new int[S]; var up24 = new int[S]; var c6 = new int[S]; var c24 = new int[S];
        var sum6 = new double[S]; var sum24 = new double[S]; var fade = new int[S]; var fadeC = new int[S];

        // [v5.23.80] BTC 상승장 레짐 — 종가>EMA200 & EMA50>EMA200 인 시각만 진입 허용
        var btcUp = new Dictionary<long, bool>();
        if (UseRegime)
        {
            Console.Write("BTC 레짐 로딩... ");
            var bk = await FetchKlinesAsync("BTCUSDT", pages);
            var bc = new double[bk.Count]; for (int t = 0; t < bk.Count; t++) bc[t] = (double)bk[t].ClosePrice;
            var be200 = EmaC(bc, 200); var be50 = EmaC(bc, 50);
            for (int t = 0; t < bk.Count; t++)
                btcUp[((DateTimeOffset)bk[t].OpenTime).ToUnixTimeMilliseconds()] = (bc[t] > be200[t] && be50[t] > be200[t]);
            Console.WriteLine($"ok ({btcUp.Values.Count(v => v)}/{btcUp.Count} 상승봉)");
        }
        bool RegimeOk(IBinanceKline bar)
        {
            if (!UseRegime) return true;
            return btcUp.TryGetValue(((DateTimeOffset)bar.OpenTime).ToUnixTimeMilliseconds(), out var u) && u;
        }

        int bidx = 0;
        foreach (var sym in uni)
        {
            bidx++; Console.Write($"[{bidx}/{uni.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, pages); } catch { Console.WriteLine("fail"); continue; }
            int n = kl.Count; if (n < 500) { Console.WriteLine("skip"); continue; }
            var C = new double[n]; var H = new double[n]; var L = new double[n]; var O = new double[n]; var V = new double[n];
            for (int t = 0; t < n; t++) { C[t] = (double)kl[t].ClosePrice; H[t] = (double)kl[t].HighPrice; L[t] = (double)kl[t].LowPrice; O[t] = (double)kl[t].OpenPrice; V[t] = (double)kl[t].Volume; }
            var rsi = BtRsiArr(C, 14); var ema9 = EmaC(C, 9); var ema21 = EmaC(C, 21); var ema50 = EmaC(C, 50); var ema200 = EmaC(C, 200);
            var atr = AtrArr(H, L, C, 14); var (bbMid, bbUp, bbLo) = BbArr(C, 20, 2);
            var stoK = StochArr(H, L, C, 14); var srsi = StochOfArr(rsi, 14);
            var macdH = MacdHistArr(C); var wr = WilliamsArr(H, L, C, 14); var cci = CciArr(H, L, C, 20); var mfi = MfiArr(H, L, C, V, 14);
            var (haO, haC) = HeikinArr(O, H, L, C); var stDir = SupertrendDir(H, L, C, atr, 3.0);
            var don20 = DonchianHigh(H, 20);

            var sig = new bool[S]; var busy = new int[S]; for (int s = 0; s < S; s++) busy[s] = -1;
            for (int i = 205; i < n - 2; i++)
            {
                EvalSignals(sig, i, C, H, L, O, V, rsi, ema9, ema21, ema50, ema200, bbMid, bbUp, bbLo, stoK, srsi, macdH, wr, cci, mfi, haO, haC, stDir, don20);
                bool regimeOk = RegimeOk(kl[i]);
                for (int s = 0; s < S; s++)
                {
                    if (!sig[s] || i <= busy[s]) continue;
                    if (!regimeOk) continue;        // BTC 상승장만 진입
                    busy[s] = i + 24; double e = C[i]; N[s]++;
                    int j6 = i + 72, j24 = i + 288;
                    if (j6 < n) { c6[s]++; double r = C[j6] / e - 1; sum6[s] += r; if (r > 0) up6[s]++; fadeC[s]++; if (C[j6] < e) fade[s]++; }
                    if (j24 < n) { c24[s]++; double r = C[j24] / e - 1; sum24[s] += r; if (r > 0) up24[s]++; }
                }
            }
            Console.WriteLine("ok");
        }

        var rows = new List<(string name, int n, double u6, double u24, double m6, double m24, double fd)>();
        for (int s = 0; s < S; s++)
            rows.Add((names[s], N[s],
                c6[s] > 0 ? 100.0 * up6[s] / c6[s] : 0, c24[s] > 0 ? 100.0 * up24[s] / c24[s] : 0,
                c6[s] > 0 ? sum6[s] / c6[s] * 100 : 0, c24[s] > 0 ? sum24[s] / c24[s] * 100 : 0,
                fadeC[s] > 0 ? 100.0 * fade[s] / fadeC[s] : 0));
        Console.WriteLine();
        Console.WriteLine($"{"전략",-20} {"진입",7} {"6h상승%",8} {"24h상승%",9} {"6h평균%",8} {"24h평균%",9} {"가짜반등%",9}");
        foreach (var r in rows.OrderByDescending(x => x.u6))
            Console.WriteLine($"{r.name,-20} {r.n,7} {r.u6,7:F1}% {r.u24,8:F1}% {r.m6,7:F2}% {r.m24,8:F2}% {r.fd,8:F1}%");
        Console.WriteLine();
        Console.WriteLine("  [판정] 6h/24h 상승% > 55~60% & 평균% 양수면 진짜 엣지 후보. 50% 부근=동전던지기.");
        Console.WriteLine("  [중요] 청산 가정 없는 실제 가격경로 — 부풀린 가짜 승률 아님.");
    }

    private static double[] EmaC(double[] c, int p) { var e = new double[c.Length]; double k = 2.0 / (p + 1); e[0] = c[0]; for (int i = 1; i < c.Length; i++) e[i] = c[i] * k + e[i - 1] * (1 - k); return e; }
    private static double[] BtRsiArr(double[] c, int p) { var r = new double[c.Length]; double ag = 0, al = 0; for (int i = 1; i < c.Length; i++) { double d = c[i] - c[i - 1]; double g = d > 0 ? d : 0, l = d < 0 ? -d : 0; if (i <= p) { ag += g; al += l; if (i == p) { ag /= p; al /= p; r[i] = al < 1e-12 ? 100 : 100 - 100 / (1 + ag / al); } } else { ag = (ag * (p - 1) + g) / p; al = (al * (p - 1) + l) / p; r[i] = al < 1e-12 ? 100 : 100 - 100 / (1 + ag / al); } } return r; }
    private static double[] AtrArr(double[] h, double[] l, double[] c, int p) { var a = new double[c.Length]; double s = 0; for (int i = 1; i < c.Length; i++) { double tr = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1]))); if (i <= p) { s += tr; if (i == p) a[i] = s / p; } else a[i] = (a[i - 1] * (p - 1) + tr) / p; } return a; }
    private static (double[] mid, double[] up, double[] lo) BbArr(double[] c, int p, double mult) { int n = c.Length; var mid = new double[n]; var up = new double[n]; var lo = new double[n]; for (int i = p - 1; i < n; i++) { double s = 0; for (int q = i - p + 1; q <= i; q++) s += c[q]; double m = s / p; double v = 0; for (int q = i - p + 1; q <= i; q++) v += (c[q] - m) * (c[q] - m); double sd = Math.Sqrt(v / p); mid[i] = m; up[i] = m + mult * sd; lo[i] = m - mult * sd; } return (mid, up, lo); }
    private static double[] StochArr(double[] h, double[] l, double[] c, int p) { int n = c.Length; var k = new double[n]; for (int i = p - 1; i < n; i++) { double hh = h[i], ll = l[i]; for (int q = i - p + 1; q <= i; q++) { if (h[q] > hh) hh = h[q]; if (l[q] < ll) ll = l[q]; } k[i] = hh > ll ? (c[i] - ll) / (hh - ll) * 100 : 50; } return k; }
    private static double[] StochOfArr(double[] src, int p) { int n = src.Length; var k = new double[n]; for (int i = p; i < n; i++) { double hh = src[i], ll = src[i]; for (int q = i - p + 1; q <= i; q++) { if (src[q] > hh) hh = src[q]; if (src[q] < ll) ll = src[q]; } k[i] = hh > ll ? (src[i] - ll) / (hh - ll) * 100 : 50; } return k; }
    private static double[] MacdHistArr(double[] c) { var e12 = EmaC(c, 12); var e26 = EmaC(c, 26); var line = new double[c.Length]; for (int i = 0; i < c.Length; i++) line[i] = e12[i] - e26[i]; var sig = EmaC(line, 9); var h = new double[c.Length]; for (int i = 0; i < c.Length; i++) h[i] = line[i] - sig[i]; return h; }
    private static double[] WilliamsArr(double[] h, double[] l, double[] c, int p) { int n = c.Length; var w = new double[n]; for (int i = p - 1; i < n; i++) { double hh = h[i], ll = l[i]; for (int q = i - p + 1; q <= i; q++) { if (h[q] > hh) hh = h[q]; if (l[q] < ll) ll = l[q]; } w[i] = hh > ll ? -100 * (hh - c[i]) / (hh - ll) : -50; } return w; }
    private static double[] CciArr(double[] h, double[] l, double[] c, int p) { int n = c.Length; var cc = new double[n]; for (int i = p - 1; i < n; i++) { double s = 0; for (int q = i - p + 1; q <= i; q++) s += (h[q] + l[q] + c[q]) / 3; double m = s / p; double tp = (h[i] + l[i] + c[i]) / 3; double md = 0; for (int q = i - p + 1; q <= i; q++) md += Math.Abs((h[q] + l[q] + c[q]) / 3 - m); md /= p; cc[i] = md > 1e-12 ? (tp - m) / (0.015 * md) : 0; } return cc; }
    private static double[] MfiArr(double[] h, double[] l, double[] c, double[] v, int p) { int n = c.Length; var m = new double[n]; for (int i = p; i < n; i++) { double pos = 0, neg = 0; for (int q = i - p + 1; q <= i; q++) { double tp = (h[q] + l[q] + c[q]) / 3, tpPrev = (h[q - 1] + l[q - 1] + c[q - 1]) / 3; double mf = tp * v[q]; if (tp > tpPrev) pos += mf; else if (tp < tpPrev) neg += mf; } m[i] = neg < 1e-9 ? 100 : 100 - 100 / (1 + pos / neg); } return m; }
    private static (double[] o, double[] c) HeikinArr(double[] o, double[] h, double[] l, double[] c) { int n = c.Length; var ho = new double[n]; var hc = new double[n]; ho[0] = o[0]; hc[0] = (o[0] + h[0] + l[0] + c[0]) / 4; for (int i = 1; i < n; i++) { hc[i] = (o[i] + h[i] + l[i] + c[i]) / 4; ho[i] = (ho[i - 1] + hc[i - 1]) / 2; } return (ho, hc); }
    private static int[] SupertrendDir(double[] h, double[] l, double[] c, double[] atr, double mult) { int n = c.Length; var dir = new int[n]; double prevUp = 0, prevDn = 0; int prevDir = 1; for (int i = 1; i < n; i++) { double mid = (h[i] + l[i]) / 2; double up = mid + mult * atr[i], dn = mid - mult * atr[i]; double fUp = (c[i - 1] <= prevUp) ? Math.Min(up, prevUp == 0 ? up : prevUp) : up; double fDn = (c[i - 1] >= prevDn) ? Math.Max(dn, prevDn == 0 ? dn : prevDn) : dn; int d = prevDir; if (prevDir == 1 && c[i] < fDn) d = -1; else if (prevDir == -1 && c[i] > fUp) d = 1; dir[i] = d; prevUp = fUp; prevDn = fDn; prevDir = d; } return dir; }
    private static double[] DonchianHigh(double[] h, int p) { int n = h.Length; var d = new double[n]; for (int i = p; i < n; i++) { double hh = h[i - 1]; for (int q = i - p; q < i; q++) if (h[q] > hh) hh = h[q]; d[i] = hh; } return d; }

    // [v5.23.83] LazyBear Squeeze Momentum (TradingView #10). mom>0=상승모멘텀, sqzOn=BB⊂KC(눌림/수축), sqzOff=BB⊃KC(발산).
    //   mom = linreg(close - avg((HH+LL)/2, SMA(close)), len). KC range = SMA(TR).
    private static (double[] mom, bool[] sqzOn, bool[] sqzOff) SqueezeMom(double[] h, double[] l, double[] c, int len = 20, double bbM = 2.0, double kcM = 1.5)
    {
        int n = c.Length;
        var mom = new double[n]; var on = new bool[n]; var off = new bool[n];
        var tr = new double[n];
        for (int i = 1; i < n; i++) tr[i] = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
        var src = new double[n];
        for (int i = len - 1; i < n; i++)
        {
            double s = 0; for (int q = i - len + 1; q <= i; q++) s += c[q]; double basis = s / len;          // SMA(close)
            double v = 0; for (int q = i - len + 1; q <= i; q++) v += (c[q] - basis) * (c[q] - basis); double sd = Math.Sqrt(v / len);
            double upBB = basis + bbM * sd, loBB = basis - bbM * sd;
            double st = 0; for (int q = i - len + 1; q <= i; q++) st += tr[q]; double rangema = st / len;      // SMA(TR)
            double upKC = basis + kcM * rangema, loKC = basis - kcM * rangema;
            on[i] = loBB > loKC && upBB < upKC;
            off[i] = loBB < loKC && upBB > upKC;
            double hh = h[i], ll = l[i]; for (int q = i - len + 1; q <= i; q++) { if (h[q] > hh) hh = h[q]; if (l[q] < ll) ll = l[q]; }
            src[i] = c[i] - (((hh + ll) / 2.0 + basis) / 2.0);
        }
        // linreg(src, len, 0) — OLS 적합선의 현재봉 끝값
        double sx = 0, sxx = 0; for (int x = 0; x < len; x++) { sx += x; sxx += (double)x * x; }
        double den = len * sxx - sx * sx;
        for (int i = (len - 1) + (len - 1); i < n; i++)
        {
            double sy = 0, sxy = 0;
            for (int x = 0; x < len; x++) { double y = src[i - (len - 1) + x]; sy += y; sxy += x * y; }
            double b = (len * sxy - sx * sy) / den;
            double a = (sy - b * sx) / len;
            mom[i] = a + b * (len - 1);
        }
        return (mom, on, off);
    }

    // 선형회귀 채널: 윗/아랫 밴드 + 기울기 (윈도우 W, ±mult·resid_std)
    private static (double[] reg, double[] lo, double[] up, double[] slope) LinRegChannel(double[] c, int W, double mult)
    {
        int n = c.Length; var reg = new double[n]; var lo = new double[n]; var up = new double[n]; var sl = new double[n];
        double sx = 0, sxx = 0; for (int x = 0; x < W; x++) { sx += x; sxx += (double)x * x; }
        double denom = W * sxx - sx * sx;
        for (int i = W - 1; i < n; i++)
        {
            double sy = 0, sxy = 0; int b = i - W + 1;
            for (int x = 0; x < W; x++) { double y = c[b + x]; sy += y; sxy += x * y; }
            double slope = denom != 0 ? (W * sxy - sx * sy) / denom : 0;
            double intercept = (sy - slope * sx) / W;
            double rv = intercept + slope * (W - 1);   // i 지점 회귀값
            double ss = 0; for (int x = 0; x < W; x++) { double pred = intercept + slope * x; double d = c[b + x] - pred; ss += d * d; }
            double sd = Math.Sqrt(ss / W);
            reg[i] = rv; lo[i] = rv - mult * sd; up[i] = rv + mult * sd; sl[i] = slope;
        }
        return (reg, lo, up, sl);
    }
    private static void EvalSignals(bool[] sig, int i, double[] C, double[] H, double[] L, double[] O, double[] V,
        double[] rsi, double[] e9, double[] e21, double[] e50, double[] e200, double[] bbMid, double[] bbUp, double[] bbLo,
        double[] stoK, double[] srsi, double[] macdH, double[] wr, double[] cci, double[] mfi, double[] haO, double[] haC, int[] stDir, double[] don20)
    {
        bool green = C[i] > O[i];
        sig[0] = rsi[i - 1] < 30 && rsi[i] >= 30;
        sig[1] = stoK[i - 1] < 20 && stoK[i] >= 20;
        sig[2] = srsi[i - 1] < 20 && srsi[i] >= 20;
        sig[3] = macdH[i - 1] < 0 && macdH[i] > 0;
        sig[4] = macdH[i] < 0 && macdH[i] > macdH[i - 1] && macdH[i - 1] > macdH[i - 2];
        sig[5] = C[i - 1] < bbLo[i - 1] && C[i] > bbLo[i] && bbLo[i] > 0;
        sig[6] = e9[i - 1] <= e21[i - 1] && e9[i] > e21[i];
        sig[7] = e50[i - 1] <= e200[i - 1] && e50[i] > e200[i];
        sig[8] = wr[i - 1] < -80 && wr[i] >= -80;
        sig[9] = cci[i - 1] < -100 && cci[i] >= -100;
        sig[10] = mfi[i - 1] < 20 && mfi[i] >= 20;
        sig[11] = don20[i] > 0 && C[i] > don20[i] && C[i - 1] <= don20[i - 1];
        sig[12] = haC[i - 1] < haO[i - 1] && haC[i] > haO[i];
        sig[13] = false;
        { int lb = 20; if (i > lb) { int pl2 = i - 1; for (int q = i - lb; q < i; q++) if (L[q] < L[pl2]) pl2 = q; if (L[i] < L[pl2] && rsi[i] > rsi[pl2] && green) sig[13] = true; } }
        sig[14] = stDir[i] == 1 && stDir[i - 1] == -1;
        { double va = 0; for (int q = i - 20; q < i; q++) va += V[q]; va /= 20; sig[15] = va > 0 && V[i] > va * 2.0 && green && C[i] > e21[i]; }
        sig[16] = C[i - 1] <= e200[i - 1] && C[i] > e200[i];
        sig[17] = e9[i] > e21[i] && e21[i] > e50[i] && L[i] <= e21[i] && green;
    }

    private static async Task RunRsiDipVerifyAsync()
    {
        decimal lev = (LEVERAGE == 10m) ? 15m : LEVERAGE;
        int pages = BbExpandPages >= 12 ? BbExpandPages : 24;
        decimal slAtr = 1.5m, tp1Atr = 1.0m, tp1Pct = 0.5m, trailAtr = 3.0m;
        const int K = 5;
        Console.WriteLine("================================================================");
        Console.WriteLine($"  RSI-DIP 검증 (청산무관·순수 전방가격) — {symbols.Length}심볼 / ~{pages * 1500 * 5 / 60 / 24}일");
        Console.WriteLine("  진입: BB상단/RSI60+ 금지, (RSI≤30 과매도 | 종가≤BB중심+RSI≤50 눌림)");
        Console.WriteLine("  비교: [필터OFF]단순양봉  vs  [필터ON]강몸통+작은윗꼬리+거래량1.2x+직전고가돌파");
        Console.WriteLine("  지표: 진입 후 2h/6h/24h 가격 상승비율 + 평균수익 + MFE/MAE + 가짜반등율(6h뒤 하락)");
        Console.WriteLine("================================================================");

        int[] H = { 24, 72, 288 };  // 2h, 6h, 24h (5m봉)
        // [variant], 누적
        var N = new int[2]; var cntH = new int[2, 3]; var upH = new int[2, 3]; var sumH = new double[2, 3];
        var sumMfe = new double[2]; var sumMae = new double[2]; var fadeN = new int[2]; var fade6cnt = new int[2];
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++; Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, pages); } catch { Console.WriteLine("fail"); continue; }
            if (kl.Count < 400) { Console.WriteLine("skip"); continue; }
            int n = kl.Count, start = 40, end = n - 2;
            for (int v = 0; v < 2; v++)
            {
                int busy = -1;
                for (int i = start; i < end; i++)
                {
                    if (i <= busy) continue;
                    if (!RsiDipEntry(kl, i, v == 1)) continue;
                    double entryPx = (double)kl[i + 1].OpenPrice;
                    if (entryPx <= 0) continue;
                    N[v]++; busy = i + 24;   // 2h 쿨다운(중복신호 방지)
                    for (int h = 0; h < 3; h++)
                    {
                        int j = i + 1 + H[h];
                        if (j < n) { double r = (double)kl[j].ClosePrice / entryPx - 1; cntH[v, h]++; if (r > 0) upH[v, h]++; sumH[v, h] += r; }
                    }
                    // MFE/MAE 24h
                    int last = Math.Min(n - 1, i + 1 + 288); double mx = entryPx, mn = entryPx;
                    for (int j = i + 1; j <= last; j++) { if ((double)kl[j].HighPrice > mx) mx = (double)kl[j].HighPrice; if ((double)kl[j].LowPrice < mn) mn = (double)kl[j].LowPrice; }
                    sumMfe[v] += (mx / entryPx - 1); sumMae[v] += (mn / entryPx - 1);
                    int j6 = i + 1 + 72; if (j6 < n) { fade6cnt[v]++; if ((double)kl[j6].ClosePrice < entryPx) fadeN[v]++; }
                }
            }
            Console.WriteLine("ok");
        }
        Console.WriteLine();
        string[] hl = { "2h", "6h", "24h" };
        for (int v = 0; v < 2; v++)
        {
            Console.WriteLine($"── {(v == 0 ? "필터OFF (단순양봉)" : "필터ON (가짜반등 필터)")} — 진입 {N[v]}건 ──");
            for (int h = 0; h < 3; h++)
            {
                int cc = cntH[v, h];
                double up = cc > 0 ? 100.0 * upH[v, h] / cc : 0, mean = cc > 0 ? sumH[v, h] / cc * 100 : 0;
                Console.WriteLine($"    {hl[h],3} 후: 상승비율 {up,5:F1}%   평균수익 {mean,6:F2}%");
            }
            double mfe = N[v] > 0 ? sumMfe[v] / N[v] * 100 : 0, mae = N[v] > 0 ? sumMae[v] / N[v] * 100 : 0;
            double fade = fade6cnt[v] > 0 ? 100.0 * fadeN[v] / fade6cnt[v] : 0;
            Console.WriteLine($"    평균 최대상승(MFE) {mfe:F2}%  평균 최대하락(MAE) {mae:F2}%  가짜반등율(6h뒤 하락) {fade:F1}%");
            Console.WriteLine();
        }
        Console.WriteLine("  [판정] 필터ON이 OFF보다 상승비율↑·평균수익↑·가짜반등율↓ 이면 필터가 진짜 효과.");
        Console.WriteLine("  [중요] 이 수치는 청산 가정이 전혀 없는 실제 가격경로 — 청산모델 오류가 끼어들 여지 없음.");
    }

    // 라이브 AnalyzeMeanReversionEntry 와 동일 조건 (useFilter=가짜반등 필터)
    private static bool RsiDipEntry(List<IBinanceKline> k, int i, bool useFilter)
    {
        if (i < 30) return false;
        double c = (double)k[i].ClosePrice, o = (double)k[i].OpenPrice, pc = (double)k[i - 1].ClosePrice;
        double hi = (double)k[i].HighPrice, lo = (double)k[i].LowPrice, prevHi = (double)k[i - 1].HighPrice;
        var bb = LiveMajorEvaluator.Bb(k, i, 20, 2);
        if (bb.Mid <= 0) return false;
        double mid = bb.Mid, upper = bb.Upper;
        if (c >= upper * 0.999) return false;                  // 천장 금지
        double rsi = LiveMajorEvaluator.Rsi(k, i, 14);
        if (rsi >= 60) return false;
        bool oversold = rsi <= 30, pullback = c <= mid && rsi <= 50;
        if (!(oversold || pullback)) return false;
        if (!useFilter) return c > o && c > pc;                // 필터OFF: 단순 양봉
        // 필터ON: 진짜반등
        double range = hi - lo, body = Math.Abs(c - o), upWick = hi - Math.Max(c, o);
        double volAvg = 0; for (int q = i - 20; q < i; q++) volAvg += (double)k[q].Volume; volAvg /= 20.0;
        bool strongBody = range > 0 && body >= range * 0.5;
        bool smallUpWick = body > 0 && upWick <= body * 0.6;
        bool volConfirm = volAvg > 0 && (double)k[i].Volume >= volAvg * 1.2;
        bool reclaim = c > prevHi;
        return c > o && strongBody && smallUpWick && volConfirm && reclaim;
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.79] --now : 현재 실시간 시장 스캔 (바이낸스=트뷰 데이터). 지금 어떤 코인이
    //   깨끗한 1h 상승추세인지, 과열 아닌 진입자리인지 실제 차트로 읽음.
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunNowScanAsync()
    {
        var scan = new[] { "BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT" }.Concat(symbols).Distinct().ToArray();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  실시간 시장 스캔 (바이낸스 1h) — {scan.Length}심볼");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"심볼",-13} {"현재가",12} {"24h%",7} {"추세",-10} {"vsEMA20",8} {"RSI",5} {"ADX",5} {"판정",-16}");

        var rows = new List<(string sym, string verdict, double score, string line)>();
        foreach (var sym in scan)
        {
            List<IBinanceKline> k;
            try { k = await FetchKlines1hAsync(sym, 1); } catch { continue; }
            if (k.Count < 210) continue;
            int i = k.Count - 1;
            double[] e20 = EmaArr(k, 20), e50 = EmaArr(k, 50), e200 = EmaArr(k, 200);
            double c = (double)k[i].ClosePrice;
            double ch24 = i >= 24 ? (c / (double)k[i - 24].ClosePrice - 1) * 100 : 0;
            double rsi = LiveMajorEvaluator.Rsi(k, i, 14);
            double adx = LiveSim.Adx(k, i, 14);
            double vsE20 = (c / e20[i] - 1) * 100;

            string trend; double score;
            bool fullUp = c > e20[i] && e20[i] > e50[i] && e50[i] > e200[i];
            bool up = e20[i] > e50[i] && c > e50[i];
            if (fullUp) { trend = "정배열↑"; score = 3; }
            else if (up) { trend = "상승"; score = 2; }
            else if (c > e200[i]) { trend = "중립↑"; score = 1; }
            else { trend = "하락/약세"; score = 0; }

            // 판정: 정배열 + 과열아님(vsEMA20 0~4% & RSI<70) + 추세강도(ADX>20) = 진입후보
            string verdict;
            if (fullUp && vsE20 >= -1 && vsE20 <= 4 && rsi < 70 && adx > 20) { verdict = "★진입후보"; score += 2; }
            else if (fullUp && rsi >= 75) verdict = "과열(되돌림대기)";
            else if (fullUp && vsE20 > 4) verdict = "확장(추격금지)";
            else if (up) verdict = "상승中(관망)";
            else verdict = "회피";

            string line = $"{sym,-13} {c,12:G6} {ch24,6:F1}% {trend,-10} {vsE20,7:F1}% {rsi,5:F0} {adx,5:F0} {verdict,-16}";
            rows.Add((sym, verdict, score, line));
        }
        foreach (var r in rows.OrderByDescending(x => x.score).ThenByDescending(x => x.sym.StartsWith("BTC") || x.sym.StartsWith("ETH")))
            Console.WriteLine(r.line);
        Console.WriteLine();
        Console.WriteLine("=== ★ 지금 깨끗한 진입후보 (정배열+과열아님+ADX>20) ===");
        var cand = rows.Where(x => x.verdict == "★진입후보").ToList();
        if (cand.Count == 0) Console.WriteLine("  현재 깨끗한 진입후보 없음 (다 과열이거나 추세 약함)");
        foreach (var r in cand) Console.WriteLine("  " + r.line);
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.24.4] --near-entry : 실제 라이브 LorentzianGuard 기준 '진입 임박' 스캐너.
    //   라이브 AnalyzeLorentzianEntryAsync 와 동일하게 1500봉 15m fetch → walk-forward 학습 →
    //   마지막 마감봉(count-2)에서 가드 평가. "무엇이 막나"가 아니라 "각 코인이 진입에 얼마나
    //   가깝고, 어느 값/가격이면 진입되는가"를 보여준다. (가드블록 카운트 모니터 대체)
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunNearEntryScanAsync()
    {
        var scan = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" }
            .Concat(symbols).Distinct().ToArray();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  진입 임박 스캐너 — 실제 라이브 LorentzianGuard 기준 ({scan.Length}심볼, 15m)");
        Console.WriteLine($"  활성 게이트: KNN신호(net≥4/8) · NW커널 상승 · DBB 과열아님(close≤+1σ)");
        Console.WriteLine($"  (REGIME/VOLATILITY 는 v5.24.4 OFF — 라이브와 동일)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"심볼",-13}{"현재가",12}  {"KNN",-9}{"NW커널",-9}{"DBB여유",-9}{"진입상단(+1σ)",14}  판정");

        var rows = new List<(int rank, string sym, string verdict, string line, string need)>();
        int done = 0;
        foreach (var sym in scan)
        {
            done++;
            Console.Error.Write($"\r[fetch {done}/{scan.Length}] {sym}          ");
            List<IBinanceKline> k15;
            try { k15 = await FetchKlines15mAsync(sym, 1); } catch { continue; }
            if (k15 == null || k15.Count < 350) continue;

            // 라이브와 동일 학습: walk-forward bulk fill
            var engine = new LorentzianAnnEngine(sym, 8, 2000, LorentzianFeatures.FeatureCount);
            for (int j = 60; j <= k15.Count - 6; j++)
            {
                int wS = Math.Max(0, j - 499);
                var win = k15.GetRange(wS, j - wS + 1);
                var feats = LorentzianFeatures.Extract(win);
                if (feats == null) continue;
                engine.AddSample(feats, LorentzianGuard.LabelForBar(k15, j));
            }

            // 마지막 마감봉 평가 (라이브 evalIdx = count-2)
            int evalIdx = k15.Count - 2;
            int wStartE = Math.Max(0, evalIdx - 499);
            var winE = k15.GetRange(wStartE, evalIdx - wStartE + 1);
            int ei = winE.Count - 1;

            var guard = LorentzianGuard.EvaluateEntry(winE, engine);

            // 게이트별 지표 직접 계산 (가드 단락과 무관하게 전부 표시)
            var feE = LorentzianFeatures.Extract(winE);
            var pr = feE != null ? engine.Predict(feE) : new LorentzianAnnPrediction { K = 8, IsReady = false };
            int net = pr.Prediction, pos = pr.PositiveVotes, K = pr.K > 0 ? pr.K : 8;
            double nwNow = LorentzianGuard.CalcNWKernel(winE, ei);
            double nwPrev = ei >= 1 ? LorentzianGuard.CalcNWKernel(winE, ei - 1) : nwNow;
            double nwGap = nwNow - nwPrev;
            LorentzianGuard.CalcBB(winE, ei, 20, 1.0, out double dbbMid, out double dbbUp1, out _);
            double close = (double)winE[ei].ClosePrice;
            double dbbRoomPct = dbbUp1 > 0 ? (dbbUp1 - close) / close * 100.0 : 0.0;

            // 전환 여부 (라이브는 직전봉도 통과면 '지속신호'로 스킵 → 음→양 전환 첫봉만 진입)
            bool prevPassed = false;
            if (evalIdx - 1 >= 60)
            {
                int pIdx = evalIdx - 1, wsP = Math.Max(0, pIdx - 499);
                var winP = k15.GetRange(wsP, pIdx - wsP + 1);
                prevPassed = LorentzianGuard.EvaluateEntry(winP, engine).Passed;
            }

            // 게이트 통과 표시
            bool knnOk = net >= 4;
            bool nwOk = nwNow >= nwPrev;
            bool dbbOk = !(dbbMid > 0 && close > dbbUp1);

            string knnCell = $"{net,2}/{K}{(knnOk ? "✓" : "✗")}";
            string nwCell = $"{(nwGap >= 0 ? "↑+" : "↓")}{nwGap,5:F3}{(nwOk ? "✓" : "✗")}".Replace("↓-", "↓-");
            string dbbCell = $"{dbbRoomPct,+5:F1}%{(dbbOk ? "✓" : "✗")}";

            // 판정 + 거리점수 (낮을수록 진입 임박)
            string verdict; int rank;
            if (guard.Passed && !prevPassed) { verdict = "★진입가능(신규전환)"; rank = 0; }
            else if (guard.Passed && prevPassed) { verdict = "진입조건충족(지속신호—라이브스킵)"; rank = 1; }
            else
            {
                // 거리: KNN 부족표 ×10 + NW 미상승 ×5 + DBB 과열 ×(초과%)
                int dist = 0;
                var miss = new List<string>();
                if (!knnOk) { dist += (4 - net) * 10; miss.Add($"KNN {net}→4 ({4 - net}표↑)"); }
                if (!nwOk) { dist += 5; miss.Add($"NW커널 상승전환 필요({nwGap:F3})"); }
                if (!dbbOk) { dist += (int)Math.Ceiling(-dbbRoomPct) + 1; miss.Add($"DBB 과열(+1σ {dbbUp1:G6} ≤ 진입, 현재 {close:G6}, {dbbRoomPct:F1}%)"); }
                verdict = miss.Count == 0 ? "(가드기타차단)" : "근접: " + string.Join(", ", miss);
                rank = 2_000 + dist;
            }

            string line = $"{sym,-13}{close,12:G6}  {knnCell,-9}{nwCell,-9}{dbbCell,-9}{dbbUp1,14:G6}  {(rank < 2 ? verdict : "근접도 " + (rank - 2000))}";
            rows.Add((rank, sym, verdict, line, BuildNeedLine(sym, guard.Passed, prevPassed, net, K, nwNow, nwPrev, close, dbbUp1, dbbOk)));
        }
        Console.Error.Write("\r                                        \r");

        foreach (var r in rows.OrderBy(x => x.rank).ThenByDescending(x => x.sym.StartsWith("BTC") || x.sym.StartsWith("ETH")))
            Console.WriteLine(r.line);

        Console.WriteLine();
        Console.WriteLine("=== ★ 지금 진입 가능 (가드 통과 + 신규 전환) ===");
        var ready = rows.Where(x => x.rank == 0).ToList();
        if (ready.Count == 0) Console.WriteLine("  현재 진입 가능 코인 없음.");
        foreach (var r in ready) Console.WriteLine("  " + r.sym + " — " + r.verdict);

        Console.WriteLine();
        Console.WriteLine("=== 진입 임박 TOP (어느 값이면 진입되는가) ===");
        foreach (var r in rows.OrderBy(x => x.rank).Take(8))
            Console.WriteLine("  " + r.need);
    }

    private static string BuildNeedLine(string sym, bool passed, bool prevPassed,
        int net, int K, double nwNow, double nwPrev, double close, double dbbUp1, bool dbbOk)
    {
        if (passed && !prevPassed) return $"{sym}: ✅ 지금 진입 조건 충족 (신규 전환).";
        if (passed && prevPassed) return $"{sym}: 가드는 통과지만 직전봉도 통과(지속신호) → 라이브는 전환 첫봉만 진입하므로 스킵.";
        var parts = new List<string>();
        if (net < 4) parts.Add($"KNN {net}/{K} → net≥4 필요 ({4 - net}표 더 LONG 쪽으로)");
        if (nwNow < nwPrev) parts.Add($"NW커널 하락중({nwNow:G6}<{nwPrev:G6}) → 상승전환 1봉 필요");
        if (!dbbOk) parts.Add($"과열: 가격이 +1σ({dbbUp1:G6}) 이하로 눌려야 진입 (현재 {close:G6})");
        if (parts.Count == 0) return $"{sym}: 기타 가드 차단.";
        return $"{sym}: " + string.Join(" / ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.79] --trend1h-folds : 1시간봉 추세추종 전략을 3년 차트 다중폴드로 검증.
    //   사용자 원칙 "방향은 1h" — 상승추세 안에서 진입(고점추격 아님). 5개 변형 × K폴드.
    //   청산 = 부분TP1 + 넓은 ATR 추적 (큰 추세 끝까지).
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunTrend1hFoldsAsync()
    {
        decimal lev = (LEVERAGE == 10m) ? 15m : LEVERAGE;
        int pages = BbExpandPages >= 12 ? BbExpandPages : 18;   // 1h: 18p ≈ 3년
        decimal slAtr = 1.5m, tp1Atr = 1.5m, tp1Pct = 0.5m, trailAtr = 3.0m;
        const int K = 5;

        Console.WriteLine("================================================================");
        Console.WriteLine($"  1H 추세추종 검증  —  {symbols.Length}심볼 / 1h {pages}p / {K}폴드 / {lev}x");
        Console.WriteLine($"  청산: 부분TP1({tp1Atr}ATR,{tp1Pct:P0}) + 잔량 {trailAtr}ATR 추적 (추세 끝까지)");
        Console.WriteLine("  변형: T1추세지속 T2눌림반등 T3RSI눌림 T4골든크로스 T5EMA20근접");
        Console.WriteLine("================================================================");

        string[] vname = { "T1추세지속", "T2눌림반등", "T3RSI눌림", "T4골든크로스", "T5EMA근접" };
        int V = vname.Length;
        var foldN = new int[V, K]; var foldW = new int[V, K]; var foldRet = new decimal[V, K];

        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlines1hAsync(sym, pages); }
            catch (Exception ex) { Console.WriteLine("fail:" + ex.Message); continue; }
            if (kl.Count < 300) { Console.WriteLine($"skip ({kl.Count})"); continue; }
            int n = kl.Count, start = 60, end = n - 2;
            int foldLen = (end - start) / K;

            // EMA20/50 precompute
            double[] e20 = EmaArr(kl, 20), e50 = EmaArr(kl, 50);

            for (int v = 0; v < V; v++)
            {
                int busy = -1;
                for (int i = start; i < end; i++)
                {
                    if (i <= busy) continue;
                    if (!Trend1hCond(v, kl, i, e20, e50)) continue;
                    var rr = LiveSim.SimulateRunner(kl, i + 1, slAtr, tp1Atr, tp1Pct, trailAtr, 240);
                    if (!rr.Entered) continue;
                    int fold = Math.Min(K - 1, (i - start) / Math.Max(1, foldLen));
                    foldN[v, fold]++; if (rr.HitTp1) foldW[v, fold]++; foldRet[v, fold] += rr.RetPct;
                    busy = rr.ExitIdx;
                }
            }
            Console.WriteLine("ok");
        }

        Console.WriteLine();
        Console.WriteLine("=== 변형별 × 폴드별 WR (각 폴드 독립구간 ~7개월 — 전부 60%+ & 흑자면 진짜) ===");
        Console.WriteLine($"{"변형",-12} " + string.Join(" ", Enumerable.Range(1, K).Select(f => $"F{f}(N/WR)".PadLeft(12))) + "   전체WR  건당ROE%");
        for (int v = 0; v < V; v++)
        {
            int tN = 0, tW = 0; decimal tR = 0m; var cells = new List<string>();
            for (int f = 0; f < K; f++)
            {
                int nn = foldN[v, f], ww = foldW[v, f]; tN += nn; tW += ww; tR += foldRet[v, f];
                decimal wr = nn > 0 ? 100m * ww / nn : 0m;
                cells.Add($"{nn}/{wr:F0}%".PadLeft(12));
            }
            decimal twr = tN > 0 ? 100m * tW / tN : 0m;
            decimal avgRoe = tN > 0 ? tR / tN * lev * 100m : 0m;
            Console.WriteLine($"{vname[v],-12} " + string.Join(" ", cells) + $"   {twr,5:F1}% {avgRoe,8:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine("  [판정] 5폴드 전부 WR 60%+ & 건당ROE 양수 = 1h 추세추종 실엣지. 이게 새 전략 후보.");
    }

    private static double[] EmaArr(List<IBinanceKline> kl, int period)
    {
        var e = new double[kl.Count]; double k = 2.0 / (period + 1); double prev = (double)kl[0].ClosePrice;
        e[0] = prev;
        for (int i = 1; i < kl.Count; i++) { double c = (double)kl[i].ClosePrice; prev = c * k + prev * (1 - k); e[i] = prev; }
        return e;
    }

    // 1h 추세추종 진입조건 변형 (LONG, 모두 상승추세 EMA20>EMA50 전제)
    private static bool Trend1hCond(int v, List<IBinanceKline> kl, int i, double[] e20, double[] e50)
    {
        if (i < 51) return false;
        double c = (double)kl[i].ClosePrice, pc = (double)kl[i - 1].ClosePrice;
        bool up = e20[i] > e50[i];                       // 상승추세
        switch (v)
        {
            case 0: // T1 추세지속: 상승추세 + 종가>EMA20 + 오르는 중
                return up && c > e20[i] && c > pc;
            case 1: // T2 눌림반등: 상승추세 + 직전봉 저가 EMA20 터치(눌림) + 현재 종가>직전 고가(반등)
                return up && (double)kl[i - 1].LowPrice <= e20[i - 1] && c > (double)kl[i - 1].HighPrice;
            case 2: // T3 RSI눌림: 상승추세 + 종가>EMA50 + RSI 40~60 (추세 중 눌림)
            {
                if (!(up && c > e50[i])) return false;
                double r = LiveMajorEvaluator.Rsi(kl, i, 14); return r >= 40 && r <= 60;
            }
            case 3: // T4 골든크로스: EMA20이 EMA50 갓 상향돌파
                return e20[i] > e50[i] && e20[i - 1] <= e50[i - 1];
            case 4: // T5 EMA근접: 상승추세 + 종가가 EMA20 위 0~2% (과열 아닌 추세 진입)
                return up && c >= e20[i] && c <= e20[i] * 1.02 && c > pc;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.79] --meanrev-folds : MEANREV 규칙을 다중 폴드(K구간)로 검증 (며칠 라이브 대신 차트로 즉시).
    //   규칙: Drop2%(직전1h −2%↓) + BBroom(종가>BB중심) + ADX>20 + RSI 45~68.
    //   K개 비중첩 구간 각각의 WR/기대값 + 변형(노RSI/노ADX/Drop임계) 민감도 + 심볼별.
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunMeanRevFoldsAsync()
    {
        decimal lev = (LEVERAGE == 10m) ? 15m : LEVERAGE;
        int pages = BbExpandPages;             // 기본 12, --pages 48 권장(~250일)
        int days = pages * BARS_PER_REQ * 5 / (60 * 24);
        decimal slAtr = 1.5m, tp1Atr = 1.0m, tp1Pct = 0.5m, trailAtr = 3.0m;
        const int K = 5;

        Console.WriteLine("================================================================");
        Console.WriteLine($"  MEANREV 다중폴드 검증  —  {symbols.Length}심볼 / ~{days}일 / {K}폴드 / {lev}x");
        Console.WriteLine($"  규칙: Drop2% + BBroom + ADX>20 + RSI45~68 | 청산: 부분TP1+넓은ATR추적");
        Console.WriteLine("================================================================");

        // 변형 정의: (이름, 판정함수)
        var variants = new (string name, Func<List<IBinanceKline>, int, bool> f)[]
        {
            ("MEANREV(기본)", (k,i) => CondMeanRev(k,i,-2.0,true,true)),
            ("노RSI",        (k,i) => CondMeanRev(k,i,-2.0,false,true)),
            ("노ADX",        (k,i) => CondMeanRev(k,i,-2.0,true,false)),
            ("Drop3%",       (k,i) => CondMeanRev(k,i,-3.0,true,true)),
            ("Drop1%",       (k,i) => CondMeanRev(k,i,-1.0,true,true)),
        };

        // 폴드별·변형별 집계
        var foldN = new int[variants.Length, K];
        var foldW = new int[variants.Length, K];
        var foldRet = new decimal[variants.Length, K];
        var symAgg = new Dictionary<string, (int n, int w)>();

        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, pages); }
            catch (Exception ex) { Console.WriteLine("fail:" + ex.Message); continue; }
            if (kl.Count < 1000) { Console.WriteLine($"skip ({kl.Count})"); continue; }
            int n = kl.Count, start = 50, end = n - 2;
            int foldLen = (end - start) / K;

            for (int v = 0; v < variants.Length; v++)
            {
                int busy = -1;
                for (int i = start; i < end; i++)
                {
                    if (i <= busy) continue;
                    if (!variants[v].f(kl, i)) continue;
                    var rr = LiveSim.SimulateRunner(kl, i + 1, slAtr, tp1Atr, tp1Pct, trailAtr, 288);
                    if (!rr.Entered) continue;
                    int fold = Math.Min(K - 1, (i - start) / Math.Max(1, foldLen));
                    foldN[v, fold]++; if (rr.HitTp1) foldW[v, fold]++; foldRet[v, fold] += rr.RetPct;
                    busy = rr.ExitIdx;
                    if (v == 0) { var cur = symAgg.TryGetValue(sym, out var x) ? x : (0, 0); symAgg[sym] = (cur.Item1 + 1, cur.Item2 + (rr.HitTp1 ? 1 : 0)); }
                }
            }
            Console.WriteLine("ok");
        }

        Console.WriteLine();
        Console.WriteLine("=== 변형별 × 폴드별 WR (각 폴드가 독립 구간 — 전부 60%+면 진짜) ===");
        Console.WriteLine($"{"변형",-14} " + string.Join(" ", Enumerable.Range(1, K).Select(f => $"F{f}(N/WR)".PadLeft(13))) + "   전체WR  건당ROE%");
        for (int v = 0; v < variants.Length; v++)
        {
            int tN = 0, tW = 0; decimal tR = 0m;
            var cells = new List<string>();
            for (int f = 0; f < K; f++)
            {
                int nn = foldN[v, f], ww = foldW[v, f];
                tN += nn; tW += ww; tR += foldRet[v, f];
                decimal wr = nn > 0 ? 100m * ww / nn : 0m;
                cells.Add($"{nn}/{wr:F0}%".PadLeft(13));
            }
            decimal twr = tN > 0 ? 100m * tW / tN : 0m;
            decimal avgRoe = tN > 0 ? tR / tN * lev * 100m : 0m;
            Console.WriteLine($"{variants[v].name,-14} " + string.Join(" ", cells) + $"   {twr,5:F1}% {avgRoe,8:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine("=== 기본규칙 심볼별 WR (10건+, 최고/최저 8) ===");
        foreach (var kv in symAgg.Where(x => x.Value.n >= 10).OrderByDescending(x => (double)x.Value.w / x.Value.n).Take(8))
            Console.WriteLine($"  +{kv.Key,-12} N={kv.Value.n,4}  WR={100.0*kv.Value.w/kv.Value.n:F1}%");
        foreach (var kv in symAgg.Where(x => x.Value.n >= 10).OrderBy(x => (double)x.Value.w / x.Value.n).Take(8))
            Console.WriteLine($"  -{kv.Key,-12} N={kv.Value.n,4}  WR={100.0*kv.Value.w/kv.Value.n:F1}%");
        Console.WriteLine();
        Console.WriteLine("  [판정] 5개 폴드 전부 WR 60%+ & 건당ROE 양수 = 구간 무관 실엣지. 일부 폴드만 좋으면 우연.");
    }

    // MEANREV 조건 (dropPct=하락임계 음수, useRsi/useAdx 토글)
    private static bool CondMeanRev(List<IBinanceKline> kl, int i, double dropPct, bool useRsi, bool useAdx)
    {
        if (i < 45 || i < 12) return false;
        double c = (double)kl[i].ClosePrice, c1h = (double)kl[i - 12].ClosePrice;
        if (c1h <= 0) return false;
        if ((c / c1h - 1.0) * 100.0 > dropPct) return false;            // Drop
        var bb = LiveMajorEvaluator.Bb(kl, i, 20, 2);
        if (bb.Mid <= 0 || c <= bb.Mid) return false;                   // BBroom
        if (useAdx && LiveSim.Adx(kl, i, 14) <= 20.0) return false;     // ADX>20
        if (useRsi) { double r = LiveMajorEvaluator.Rsi(kl, i, 14); if (r < 45 || r > 68) return false; } // RSI
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.79] --replay-entries : 실거래 진입기록(live-entries.csv)을 차트에 되감아
    //   "왜 실패했나" 분석. 각 진입을 진입가로 봉 앵커링 → 직전 1h 움직임(모멘텀 추격 vs 눌림)
    //   + MEANREV 규칙(Drop2%+BBroom+ADX20) 충족 여부 + 실제 승패 대조.
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunReplayEntriesAsync()
    {
        string csv = System.IO.Path.Combine(AppContext.BaseDirectory, "live-entries.csv");
        if (!System.IO.File.Exists(csv)) csv = "live-entries.csv";
        if (!System.IO.File.Exists(csv)) { Console.WriteLine($"CSV 없음: {csv}"); return; }
        var lines = System.IO.File.ReadAllLines(csv).Skip(1).ToList();
        Console.WriteLine($"진입기록 {lines.Count}건 로드");

        // 파싱
        var entries = new List<(string sym, long ms, decimal entry, decimal exit, string cat, int win)>();
        foreach (var ln in lines)
        {
            var p = ln.Split(',');
            if (p.Length < 6) continue;
            if (!long.TryParse(p[1], out var ms)) continue;
            if (!decimal.TryParse(p[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var en)) continue;
            if (!decimal.TryParse(p[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ex)) continue;
            if (!int.TryParse(p[5], out var w)) continue;
            entries.Add((p[0], ms, en, ex, p[4], w));
        }
        // 상위 40 심볼만 (fetch 비용)
        var topSyms = entries.GroupBy(e => e.sym).OrderByDescending(g => g.Count()).Take(40).Select(g => g.Key).ToHashSet();
        Console.WriteLine($"상위 {topSyms.Count} 심볼 분석 (전체 {entries.Select(e=>e.sym).Distinct().Count()} 중)");

        int momWin = 0, momN = 0, dipWin = 0, dipN = 0, flatWin = 0, flatN = 0;
        int meanrevN = 0, meanrevWin = 0, restN = 0, restWin = 0;
        decimal sumPriorWin = 0m, sumPriorLoss = 0m; int nWin = 0, nLoss = 0;
        int anchored = 0, skipped = 0;

        int si = 0;
        foreach (var sym in topSyms)
        {
            si++;
            Console.Write($"[{si}/{topSyms.Count}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, 14); } // ~72일
            catch { Console.WriteLine("fail"); continue; }
            if (kl.Count < 320) { Console.WriteLine("skip"); continue; }
            long firstMs = ((DateTimeOffset)kl[0].OpenTime).ToUnixTimeMilliseconds();
            long lastMs = ((DateTimeOffset)kl[^1].OpenTime).ToUnixTimeMilliseconds();
            int used = 0;
            foreach (var e in entries.Where(x => x.sym == sym))
            {
                if (e.ms < firstMs || e.ms > lastMs) { skipped++; continue; }
                // 시간 컨테이너 봉 + 진입가 매칭으로 앵커 (±8봉 내 가격 포함 봉)
                int guess = (int)((e.ms - firstMs) / (5L * 60 * 1000));
                int bi = -1;
                for (int off = 0; off <= 8 && bi < 0; off++)
                {
                    foreach (int cand in new[] { guess - off, guess + off })
                    {
                        if (cand < 300 || cand >= kl.Count - 1) continue;
                        if (kl[cand].LowPrice <= e.entry && e.entry <= kl[cand].HighPrice) { bi = cand; break; }
                    }
                }
                if (bi < 0) { skipped++; continue; }
                anchored++; used++;

                decimal prior = bi >= 12 ? (kl[bi].ClosePrice / kl[bi - 12].ClosePrice - 1m) * 100m : 0m;
                bool win = e.win == 1;
                if (win) { sumPriorWin += prior; nWin++; } else { sumPriorLoss += prior; nLoss++; }
                if (prior > 0.5m) { momN++; if (win) momWin++; }
                else if (prior < -0.5m) { dipN++; if (win) dipWin++; }
                else { flatN++; if (win) flatWin++; }

                // MEANREV 규칙 충족?
                double rsi = LiveMajorEvaluator.Rsi(kl, bi, 14);
                double adx = LiveSim.Adx(kl, bi, 14);
                var bb = LiveMajorEvaluator.Bb(kl, bi, 20, 2);
                bool drop2 = bi >= 12 && kl[bi].ClosePrice < kl[bi - 12].ClosePrice * 0.98m;
                bool bbroom = bb.Mid > 0 && (double)kl[bi].ClosePrice > bb.Mid;
                bool adx20 = adx > 20;
                bool meanrev = drop2 && bbroom && adx20;
                if (meanrev) { meanrevN++; if (win) meanrevWin++; } else { restN++; if (win) restWin++; }
            }
            Console.WriteLine($"ok ({used} anchored)");
        }

        decimal WR(int w, int n) => n > 0 ? 100m * w / n : 0m;
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  실거래 진입 되감기 — 앵커성공 {anchored} / 스킵 {skipped}");
        Console.WriteLine("================================================================");
        Console.WriteLine($"  승리거래 평균 직전1h움직임: {(nWin>0?sumPriorWin/nWin:0):F2}%   손실거래: {(nLoss>0?sumPriorLoss/nLoss:0):F2}%");
        Console.WriteLine($"  (양수=상승 후 추격진입 / 음수=하락 후 눌림진입)");
        Console.WriteLine();
        Console.WriteLine("  진입 직전 움직임별 실제 승률:");
        Console.WriteLine($"    상승추격(+0.5%↑)  N={momN,5}  WR={WR(momWin,momN):F1}%");
        Console.WriteLine($"    눌림진입(-0.5%↓)  N={dipN,5}  WR={WR(dipWin,dipN):F1}%");
        Console.WriteLine($"    횡보(±0.5%)       N={flatN,5}  WR={WR(flatWin,flatN):F1}%");
        Console.WriteLine();
        Console.WriteLine("  실제 진입 중 MEANREV(Drop2%+BBroom+ADX20) 규칙 충족분 vs 나머지:");
        Console.WriteLine($"    MEANREV 충족   N={meanrevN,5}  WR={WR(meanrevWin,meanrevN):F1}%");
        Console.WriteLine($"    나머지         N={restN,5}  WR={WR(restWin,restN):F1}%");
        Console.WriteLine();
        Console.WriteLine("  [해석] 손실거래가 상승추격(양수)에 몰리고 MEANREV 충족분 WR이 높으면 → 새 규칙이 방향 맞음.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.79] --entry-search : 과거 차트에서 "승률 60%+ 흑자" 진입조건 조합 탐색.
    //   7개 조건의 모든 조합(128) × train(과거60%)/test(최근40%) 분할.
    //   청산 = LiveSim.SimulateRunner (부분TP1 승확정 + 잔량 3×ATR 추적 = 큰수익 런).
    //   승 정의 = TP1 도달(이익확정). test 60%+ & 흑자만 채택 → 과최적화 거름.
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunEntrySearchAsync()
    {
        decimal lev = (LEVERAGE == 10m) ? 15m : LEVERAGE;
        int pages = BbExpandPages;
        int days = pages * BARS_PER_REQ * 5 / (60 * 24);
        // 청산 파라미터 (큰수익 끝까지)
        decimal slAtr = 1.5m, tp1Atr = 1.0m, tp1Pct = 0.5m, trailAtr = 3.0m;
        string[] condName = { "TREND", "RSIband", "MACDup", "ADX20", "VolSurge", "BBroom", "KNN",
                              "Oversold", "LowBB", "Drop2%", "BullRev" };  // 7~10 = 역추세(평균회귀)
        int NC = condName.Length;

        Console.WriteLine("================================================================");
        Console.WriteLine($"  ENTRY-SEARCH  —  {symbols.Length}심볼 / ~{days}일 / {lev}x");
        Console.WriteLine($"  청산: SL {slAtr}×ATR / TP1 {tp1Atr}×ATR {tp1Pct:P0}청산 / 잔량 {trailAtr}×ATR 추적");
        Console.WriteLine($"  조건({NC}): {string.Join(" ", condName)}  → 모든 조합 128개, train60/test40");
        Console.WriteLine($"  승=TP1도달(이익확정). 진입가=다음봉시가+슬립, 비용 전구간 반영.");
        Console.WriteLine("================================================================");

        // 심볼별 precompute: 조건비트마스크[bar], 청산결과[bar]
        var symMasks = new List<int[]>();
        var symEntered = new List<bool[]>();
        var symWin = new List<bool[]>();
        var symRet = new List<decimal[]>();
        var symExit = new List<int[]>();
        var symStart = new List<int>();
        var symTrainCut = new List<int>();

        var lor = new MiniLorentzianService();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, pages); }
            catch (Exception ex) { Console.WriteLine("fail:" + ex.Message); continue; }
            if (kl.Count < 800) { Console.WriteLine($"skip ({kl.Count})"); continue; }

            int n = kl.Count;
            var mask = new int[n];
            var entered = new bool[n];
            var win = new bool[n];
            var ret = new decimal[n];
            var exit = new int[n];
            var engine = lor.GetOrCreate(sym);
            int trained = 300;
            int start = 305;

            for (int i = start; i < n - 2; i++)
            {
                while (!SkipKnn && trained <= i - 4)
                {
                    var fs = LorentzianFeatures.Extract(kl.GetRange(0, trained + 1));
                    if (fs != null) { decimal c0 = kl[trained].ClosePrice, fu = kl[trained + 4].ClosePrice; engine.AddSample(fs, fu > c0 ? 1 : fu < c0 ? -1 : 0); }
                    trained++;
                }

                double sma50 = LiveMajorEvaluator.Sma(kl, i, 50);
                double sma200 = LiveMajorEvaluator.Sma(kl, i, 200);
                double rsi = LiveMajorEvaluator.Rsi(kl, i, 14);
                var macd = LiveMajorEvaluator.Macd(kl, i);
                double adx = LiveSim.Adx(kl, i, 14);
                var bb = LiveMajorEvaluator.Bb(kl, i, 20, 2);
                double close = (double)kl[i].ClosePrice;
                decimal vol = kl[i].Volume; decimal volAvg = 0m;
                for (int q = i - 20; q < i; q++) volAvg += kl[q].Volume;
                volAvg /= 20m;

                int m = 0;
                if (sma200 > 0 && close > sma200 && sma50 > sma200) m |= 1 << 0; // TREND
                if (rsi >= 45 && rsi <= 68) m |= 1 << 1;                          // RSIband
                if (macd.Hist > 0) m |= 1 << 2;                                   // MACDup
                if (adx > 20) m |= 1 << 3;                                        // ADX20
                if (volAvg > 0m && vol > volAvg * 1.3m) m |= 1 << 4;              // VolSurge
                if (bb.Mid > 0 && close > bb.Mid) m |= 1 << 5;                    // BBroom
                if (!SkipKnn)
                {
                    var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
                    if (pred.IsReady && pred.Prediction > 0 && pred.PositiveRate >= 0.70f) m |= 1 << 6; // KNN
                }
                // ── 역추세(평균회귀) 조건 ──
                if (rsi < 35) m |= 1 << 7;                                                 // Oversold
                if (bb.Lower > 0 && close < bb.Lower * 1.005) m |= 1 << 8;                 // LowBB (하단밴드 근처/이하)
                if (i >= 12 && kl[i].ClosePrice < kl[i - 12].ClosePrice * 0.98m) m |= 1 << 9; // Drop2% (1h내 -2%+)
                if (kl[i].ClosePrice > kl[i].OpenPrice && kl[i - 1].ClosePrice < kl[i - 1].OpenPrice) m |= 1 << 10; // BullRev (음봉 뒤 양봉)
                mask[i] = m;

                var rr = LiveSim.SimulateRunner(kl, i + 1, slAtr, tp1Atr, tp1Pct, trailAtr, 288);
                entered[i] = rr.Entered; win[i] = rr.HitTp1; ret[i] = rr.RetPct; exit[i] = rr.ExitIdx;
            }

            symMasks.Add(mask); symEntered.Add(entered); symWin.Add(win); symRet.Add(ret); symExit.Add(exit);
            symStart.Add(start); symTrainCut.Add(start + (int)((n - 2 - start) * 0.6));
            Console.WriteLine($"ok ({n} bars)");
        }

        // 조합 스윕 (128) — 비트마스크 AND, 심볼별 한 번에 한 포지션
        var rows = new List<(int mask, int trN, int trW, decimal trRet, int teN, int teW, decimal teRet)>();
        for (int combo = 0; combo < (1 << NC); combo++)
        {
            int trN = 0, trW = 0, teN = 0, teW = 0; decimal trRet = 0m, teRet = 0m;
            for (int s = 0; s < symMasks.Count; s++)
            {
                var mask = symMasks[s]; var ent = symEntered[s]; var win = symWin[s]; var ret = symRet[s]; var exit = symExit[s];
                int cut = symTrainCut[s]; int busy = -1;
                for (int i = symStart[s]; i < mask.Length; i++)
                {
                    if (i <= busy) continue;
                    if ((mask[i] & combo) != combo) continue;
                    if (!ent[i]) continue;
                    if (i < cut) { trN++; if (win[i]) trW++; trRet += ret[i]; }
                    else { teN++; if (win[i]) teW++; teRet += ret[i]; }
                    busy = exit[i];
                }
            }
            rows.Add((combo, trN, trW, trRet, teN, teW, teRet));
        }

        string Name(int m) { if (m == 0) return "(전체)"; var p = new List<string>(); for (int b = 0; b < NC; b++) if ((m & (1 << b)) != 0) p.Add(condName[b]); return string.Join("+", p); }

        Console.WriteLine();
        Console.WriteLine("=== TOP 20 — test 평균수익(ROE) 순 (test 진입 30+ , test승률 표시) ===");
        Console.WriteLine($"{"조건조합",-34} {"trN",5} {"trWR",6} {"teN",5} {"teWR",6} {"te건당ROE%",10} {"te총ROE%",9}");
        foreach (var r in rows.Where(x => x.teN >= 30).OrderByDescending(x => x.teRet / Math.Max(1, x.teN)).Take(20))
        {
            decimal trWR = r.trN > 0 ? 100m * r.trW / r.trN : 0m;
            decimal teWR = r.teN > 0 ? 100m * r.teW / r.teN : 0m;
            decimal teAvgRoe = (r.teRet / r.teN) * lev * 100m;
            decimal teTotRoe = r.teRet * lev * 100m;
            Console.WriteLine($"{Name(r.mask),-34} {r.trN,5} {trWR,5:F1}% {r.teN,5} {teWR,5:F1}% {teAvgRoe,9:F1}% {teTotRoe,8:F0}%");
        }
        Console.WriteLine();
        Console.WriteLine("=== 승률 우선 — test승률 60%+ & test흑자 & 30건+ (실전 후보) ===");
        Console.WriteLine($"{"조건조합",-34} {"trWR",6} {"teN",5} {"teWR",6} {"te건당ROE%",10}");
        var cands = rows.Where(x => x.teN >= 30 && (100m * x.teW / x.teN) >= 60m && x.teRet > 0m)
                        .OrderByDescending(x => 100m * x.teW / x.teN).ToList();
        if (cands.Count == 0) Console.WriteLine("  (test 60%+ & 흑자 조합 없음 — 더 탐색 필요)");
        foreach (var r in cands.Take(20))
        {
            decimal trWR = r.trN > 0 ? 100m * r.trW / r.trN : 0m;
            decimal teWR = 100m * r.teW / r.teN;
            decimal teAvgRoe = (r.teRet / r.teN) * lev * 100m;
            Console.WriteLine($"{Name(r.mask),-34} {trWR,5:F1}% {r.teN,5} {teWR,5:F1}% {teAvgRoe,9:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine("  [신뢰] train에서 좋고 test에서도 60%+ & 흑자여야 진짜. test만 좋으면 우연.");
        Console.WriteLine("  [다음] 후보 조합 → 라이브 카나리 소액 검증 후 채택.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // [v5.23.77] --live-sim : 단일 권위 "실용 충실본" 백테스트 (LiveSim.cs 사용)
    //   5대 괴리 닫음 + Lorentzian 워크포워드 증분학습(룩어헤드 제거).
    //   이 모드만 배포 방향성 판단에 사용. 최종 판정은 라이브 카나리(실거래 N건).
    // ─────────────────────────────────────────────────────────────────────
    private static async Task RunLiveSimAsync()
    {
        decimal lev = (LEVERAGE == 10m) ? 15m : LEVERAGE;   // 라이브 기본 15x (미override 시)
        int pages = BbExpandPages;
        int days = pages * BARS_PER_REQ * 5 / (60 * 24);
        Console.WriteLine("================================================================");
        Console.WriteLine($"  LIVE-SIM (실용 충실본)  —  {symbols.Length}심볼 / ~{days}일 / {lev}x");
        Console.WriteLine("  진입가=다음봉시가+슬립 | 수수료0.04%×2+슬립0.05%×2 | 다단청산(BE+TP1+트레일+SL)");
        Console.WriteLine("  진입: LORENTZIAN(KNN 워크포워드) / SQUEEZE(BBW<1.5+상단,가드) / MAJOR(EMA20↑+rangePos)");
        Console.WriteLine("  게이트 근사: 당일상승 동적풀 + RSI<50 낙하나이프. (미모사: BTC1h추세/시총Top30/풀랭킹)");
        Console.WriteLine("  ※ 청산은 1,000줄 상태기의 근사 — 배포 단독기준 금지, 라이브 카나리로 최종확정");
        Console.WriteLine("================================================================");

        var trigs = new[] { "LORENTZIAN", "SQUEEZE", "MAJOR" };
        var n = new Dictionary<string, int>();
        var w = new Dictionary<string, int>();
        var pnl = new Dictionary<string, decimal>();
        var winSum = new Dictionary<string, decimal>();
        var lossSum = new Dictionary<string, decimal>();
        foreach (var tname in trigs) { n[tname] = 0; w[tname] = 0; pnl[tname] = 0m; winSum[tname] = 0m; lossSum[tname] = 0m; }

        var lor = new MiniLorentzianService();
        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            Console.Write($"[{idx}/{symbols.Length}] {sym} ");
            List<IBinanceKline> kl;
            try { kl = await FetchKlinesAsync(sym, pages); }
            catch (Exception ex) { Console.WriteLine("fail:" + ex.Message); continue; }
            if (kl.Count < 600) { Console.WriteLine($"skip ({kl.Count})"); continue; }

            bool isMajor = LiveSim.Majors.Contains(sym);
            var tier = LiveSim.TierFor(sym);
            decimal margin = MARGIN_USD;
            var engine = lor.GetOrCreate(sym);
            int trained = 300;            // 다음에 표본으로 추가할 봉 (워크포워드)
            int symN = 0, busyUntil = -1; // busyUntil: 포지션 보유 중인 봉 (중복진입 방지)

            for (int i = 305; i < kl.Count - 2; i++)
            {
                // ── 워크포워드 증분학습: 라벨 확정된(i-4 이하) 봉만 표본 추가 ──
                while (trained <= i - 4)
                {
                    var fSlice = kl.GetRange(0, trained + 1);
                    var feat = LorentzianFeatures.Extract(fSlice);
                    if (feat != null)
                    {
                        decimal nowC = kl[trained].ClosePrice, fut = kl[trained + 4].ClosePrice;
                        engine.AddSample(feat, fut > nowC ? 1 : fut < nowC ? -1 : 0);
                    }
                    trained++;
                }
                if (i <= busyUntil) continue;   // 이미 포지션 보유 — 신규진입 스킵

                string? src = null;

                // ── 트리거 평가 (i = 마감 확인된 봉) ──
                if (isMajor)
                {
                    if (LiveMajorEvaluator.ShouldEnterLong(kl, i, kl[i].ClosePrice)) src = "MAJOR";
                }
                else
                {
                    // 알트: SQUEEZE (production v5.23.76 — BB_WALK 폐지) + 당일상승 동적풀 게이트
                    if (LiveSim.SqueezeTrigger(kl, i) && LiveSim.DailyUpFilter(kl, i)) src = "SQUEEZE";
                }

                // ── LORENTZIAN (모든 심볼) — 충돌 시 트리거가 비어있을 때만 ──
                if (src == null)
                {
                    var pred = lor.Predict(sym, kl.GetRange(0, i + 1));
                    if (pred.IsReady && pred.Prediction > 0 && pred.PositiveRate >= 0.70f)
                    {
                        double sma200 = LiveMajorEvaluator.Sma(kl, i, 200);
                        if (sma200 > 0 && (double)kl[i].ClosePrice > sma200)   // 상승추세 확인 (라이브 close>EMA200 근사)
                            src = "LORENTZIAN";
                    }
                }

                if (src == null) continue;

                // ── 게이트: RSI 낙하나이프 (전 트리거 공통) ──
                if (LiveSim.RsiFallingKnife(kl, i)) continue;

                // ── 진입가 = 다음 봉 시가 (i+1) → 청산 시뮬 ──
                var res = LiveSim.SimulateExit(kl, i + 1, margin, lev, tier, 288);
                if (!res.Entered) continue;

                n[src]++; symN++;
                if (res.Win) { w[src]++; winSum[src] += res.PnlUsd; } else { lossSum[src] += res.PnlUsd; }
                pnl[src] += res.PnlUsd;
                busyUntil = res.ExitBarIndex;   // 청산 봉까지 신규진입 차단
            }
            Console.WriteLine($"ok ({kl.Count} bars, {symN} trades)");
        }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($"  결과 — 마진 ${MARGIN_USD}/건, {lev}x, 비용 전구간 반영");
        Console.WriteLine("================================================================");
        Console.WriteLine($"{"트리거",-12} {"진입",6} {"승",5} {"WR",7} {"순손익$",11} {"건당$",8} {"평균익",8} {"평균손",8}");
        decimal totPnl = 0m; int totN = 0, totW = 0;
        foreach (var tname in trigs)
        {
            int nn = n[tname]; if (nn == 0) { Console.WriteLine($"{tname,-12} {0,6}   (진입 없음)"); continue; }
            decimal wr = 100m * w[tname] / nn;
            decimal avgWin = w[tname] > 0 ? winSum[tname] / w[tname] : 0m;
            int losses = nn - w[tname];
            decimal avgLoss = losses > 0 ? lossSum[tname] / losses : 0m;
            Console.WriteLine($"{tname,-12} {nn,6} {w[tname],5} {wr,6:F1}% {pnl[tname],11:F2} {pnl[tname] / nn,8:F3} {avgWin,8:F2} {avgLoss,8:F2}");
            totPnl += pnl[tname]; totN += nn; totW += w[tname];
        }
        Console.WriteLine("----------------------------------------------------------------");
        if (totN > 0)
            Console.WriteLine($"{"합계",-12} {totN,6} {totW,5} {100m * totW / totN,6:F1}% {totPnl,12:F2} {totPnl / totN,9:F3}");
        Console.WriteLine();
        Console.WriteLine("  [해석] 손익분기 WR ≈ 청산구조에 따라 가변. 건당$ > 0 이어야 흑자.");
        Console.WriteLine("  [신뢰] 진입 트리거·진입가·비용은 충실. 청산은 근사 → 라이브 카나리로 최종확정.");
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
            if (args[a] == "--pages" && a + 1 < args.Length && int.TryParse(args[a + 1], out var pg) && pg > 0)
            {
                BbExpandPages = pg;
                Console.WriteLine($"[CONFIG] BbExpandPages = {pg} (~{pg * 1500 * 5 / 60 / 24}일)");
            }
        }
        // [v5.22.17] mode flag 인식 — args[0] 고정 검사 → 어느 위치에 있어도 인식
        //   기존: --lev 10 --daily-60d 호출 시 args[0]=="--lev" 라 default 분기로 떨어져
        //   real-lorentzian C# engine 경로가 실행되며 daily-60d 절대 안 돌았음.
        bool HasArg(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (HasArg("--no-knn")) { SkipKnn = true; Console.WriteLine("[CONFIG] KNN precompute 생략 (--no-knn)"); }
        if (HasArg("--majors")) { UseMajors = true; Console.WriteLine("[CONFIG] 대형주 universe (--majors)"); }
        if (HasArg("--regime")) { UseRegime = true; Console.WriteLine("[CONFIG] BTC 상승장 레짐 필터 (--regime)"); }
        if (HasArg("--sweep"))
        {
            await RunSweepAsync();
            return;
        }
        if (HasArg("--sweep-all"))
        {
            await RunAllSweepsAsync();
            return;
        }
        if (HasArg("--master"))
        {
            await RunMasterAsync();
            return;
        }
        if (HasArg("--h1m1-3yr"))
        {
            await RunH1M1ThreeYearAsync();
            return;
        }
        if (HasArg("--h1m1-trend"))
        {
            await RunH1M1TrendAsync();
            return;
        }
        if (HasArg("--sqzlor"))
        {
            await RunSqueezeLorentzianAsync();
            return;
        }
        if (HasArg("--sqzlor15"))
        {
            await RunSqueezeLor15Async();
            return;
        }
        if (HasArg("--bbbounce"))
        {
            await RunBbBounceAsync();
            return;
        }
        if (HasArg("--user4"))
        {
            await RunUser4Async();
            return;
        }
        if (HasArg("--battery"))
        {
            await RunBatteryAsync();
            return;
        }
        if (HasArg("--rsidip-verify"))
        {
            await RunRsiDipVerifyAsync();
            return;
        }
        if (HasArg("--now"))
        {
            await RunNowScanAsync();
            return;
        }
        if (HasArg("--near-entry"))
        {
            await RunNearEntryScanAsync();
            return;
        }
        if (HasArg("--trend1h-folds"))
        {
            await RunTrend1hFoldsAsync();
            return;
        }
        if (HasArg("--meanrev-folds"))
        {
            await RunMeanRevFoldsAsync();
            return;
        }
        if (HasArg("--replay-entries"))
        {
            await RunReplayEntriesAsync();
            return;
        }
        if (HasArg("--entry-search"))
        {
            await RunEntrySearchAsync();
            return;
        }
        if (HasArg("--live-sim"))
        {
            await RunLiveSimAsync();
            return;
        }
        if (HasArg("--final"))
        {
            await RunFinalBacktestAsync();
            return;
        }
        if (HasArg("--diagnose"))
        {
            await RunDiagnosisAsync();
            return;
        }
        if (HasArg("--pump-recent"))
        {
            await RunPumpRecentAsync();
            return;
        }
        if (HasArg("--user-signal-sweep"))
        {
            await RunUserSignalSweepAsync();
            return;
        }
        if (HasArg("--report-3yr"))
        {
            await RunReport3yrAsync();
            return;
        }
        if (HasArg("--user-signal-tpsl"))
        {
            await RunUserSignalTpslAsync();
            return;
        }
        if (HasArg("--bb-expand"))
        {
            await RunBbExpandAsync();
            return;
        }
        if (HasArg("--user-signal"))
        {
            await RunUserSignalAsync();
            return;
        }
        if (HasArg("--redesign"))
        {
            await RunRedesignAsync();
            return;
        }
        if (HasArg("--golden-time-v2"))
        {
            await RunGoldenTimeV2Async();
            return;
        }
        if (HasArg("--golden-time"))
        {
            await RunGoldenTimeAsync();
            return;
        }
        if (HasArg("--user-strategy-v4"))
        {
            await RunUserStrategyV4Async();
            return;
        }
        if (HasArg("--user-strategy-v3"))
        {
            await RunUserStrategyV3Async();
            return;
        }
        if (HasArg("--user-strategy-v2"))
        {
            await RunUserStrategyV2Async();
            return;
        }
        if (HasArg("--user-strategy"))
        {
            await RunUserStrategyAsync();
            return;
        }
        if (HasArg("--explore-strategies"))
        {
            await RunExploreStrategiesAsync();
            return;
        }
        if (HasArg("--validate-realistic"))
        {
            await RunValidateRealisticAsync();
            return;
        }
        if (HasArg("--clean-logic"))
        {
            await RunCleanLogicAsync();
            return;
        }
        if (HasArg("--smc-fvg"))
        {
            await RunSmcFvgAsync();
            return;
        }
        if (HasArg("--live-realistic-v2"))
        {
            await RunLiveRealisticV2Async();
            return;
        }
        if (HasArg("--sqzmom-1h"))
        {
            await RunSqzMom1hAsync();
            return;
        }
        if (HasArg("--triple-st"))
        {
            await RunTripleSuperTrendAsync();
            return;
        }
        if (HasArg("--vwap-mr"))
        {
            await RunVwapMeanRevAsync();
            return;
        }
        if (HasArg("--ichimoku-1h"))
        {
            await RunIchimoku1hAsync();
            return;
        }
        if (HasArg("--multi-indicator-30d"))
        {
            await RunMultiIndicatorAsync(30);
            return;
        }
        if (HasArg("--multi-indicator-90d"))
        {
            await RunMultiIndicatorAsync(90);
            return;
        }
        if (HasArg("--multi-indicator-180d"))
        {
            await RunMultiIndicatorAsync(180);
            return;
        }
        if (HasArg("--multi-indicator"))
        {
            await RunMultiIndicatorAsync(90);
            return;
        }
        if (HasArg("--multi-full-30d"))
        {
            await RunMultiIndicatorFullAsync(30);
            return;
        }
        if (HasArg("--multi-full-90d"))
        {
            await RunMultiIndicatorFullAsync(90);
            return;
        }
        if (HasArg("--multi-full-180d"))
        {
            await RunMultiIndicatorFullAsync(180);
            return;
        }
        if (HasArg("--multi-full"))
        {
            await RunMultiIndicatorFullAsync(90);
            return;
        }
        if (HasArg("--multi-pure-30d")) { await RunMultiPureAsync(30); return; }
        if (HasArg("--multi-pure-90d")) { await RunMultiPureAsync(90); return; }
        if (HasArg("--multi-pure")) { await RunMultiPureAsync(90); return; }
        if (HasArg("--multi-tpsl-30d")) { await RunMultiPureTpslSweepAsync(30); return; }
        if (HasArg("--multi-tpsl-90d")) { await RunMultiPureTpslSweepAsync(90); return; }
        if (HasArg("--multi-tpsl-180d")) { await RunMultiPureTpslSweepAsync(180); return; }
        if (HasArg("--multi-detail-180d")) { await RunMultiPureDetailAsync(180); return; }
        if (HasArg("--multi-detail-90d")) { await RunMultiPureDetailAsync(90); return; }
        if (HasArg("--multi-detail")) { await RunMultiPureDetailAsync(180); return; }
        if (HasArg("--multi-tpsl")) { await RunMultiPureTpslSweepAsync(90); return; }
        if (HasArg("--regime-adaptive"))
        {
            await RunRegimeAdaptiveAsync();
            return;
        }
        if (HasArg("--split-tp"))
        {
            await RunSplitTpSqzMomAsync();
            return;
        }
        if (HasArg("--selective-bull"))
        {
            await RunSelectiveBullAsync();
            return;
        }
        if (HasArg("--pump-pullback"))
        {
            await RunPumpPullbackAsync();
            return;
        }
        if (HasArg("--hot-mover"))
        {
            await RunHotMoverAsync();
            return;
        }
        if (HasArg("--scalping"))
        {
            await RunTrueScalpingAsync();
            return;
        }
        if (HasArg("--dynpool-v54"))
        {
            await RunDynPoolV5_22_54Async();
            return;
        }
        if (HasArg("--daily-swing-sweep"))
        {
            await RunDailySwingSweepAsync();
            return;
        }
        if (HasArg("--ema20-break-tight"))
        {
            await RunEma20BreakTightAsync();
            return;
        }
        if (HasArg("--daily-swing-variants"))
        {
            await RunDailySwingVariantsAsync();
            return;
        }
        if (HasArg("--top30-24h"))
        {
            await RunTop30Last24hAsync();
            return;
        }
        if (HasArg("--pullback"))
        {
            await RunPullbackLongAsync();
            return;
        }
        if (HasArg("--daily-swing-trail"))
        {
            await RunDailySwingWithTrailingAsync();
            return;
        }
        if (HasArg("--daily-swing-monthly-3y"))
        {
            await RunDailySwingMonthly3yAsync();
            return;
        }
        if (HasArg("--lorentzian-15m-5m"))
        {
            await RunLorentzian15m5mAsync();
            return;
        }
        if (HasArg("--knn-compare"))
        {
            await RunKnnVariantsCompareAsync();
            return;
        }
        if (HasArg("--knn-sweep"))
        {
            await RunKnnSweepAsync();
            return;
        }
        if (HasArg("--knn-sweep-3y"))
        {
            await RunKnnSweep3yAsync();
            return;
        }
        if (HasArg("--swing-4h"))
        {
            // 4H: TP+5% SL-3% (1봉 평균 변동 큼) max 30봉(5일)
            await RunSwingMultiTfAsync("4H", 240, 5, 5m, 3m, 30);
            return;
        }
        if (HasArg("--swing-1h"))
        {
            // 1H: TP+3% SL-2% max 24봉(1일)
            await RunSwingMultiTfAsync("1H", 60, 12, 3m, 2m, 24);
            return;
        }
        if (HasArg("--swing-15m"))
        {
            // 15m: TP+1.5% SL-1% max 16봉(4시간)
            await RunSwingMultiTfAsync("15m", 15, 36, 1.5m, 1m, 16);
            return;
        }
        if (HasArg("--daily-swing"))
        {
            await RunDailySwingAsync();
            return;
        }
        if (HasArg("--live-realistic"))
        {
            await RunLiveRealisticAsync();
            return;
        }
        if (HasArg("--live-stats-v2"))
        {
            await RunLiveStatsV2Async();
            return;
        }
        if (HasArg("--live-stats"))
        {
            await RunLiveStatsAsync();
            return;
        }
        if (HasArg("--live-180d"))
        {
            await RunLive180Async();
            return;
        }
        if (HasArg("--stats-all"))
        {
            await RunStatsAllAsync();
            return;
        }
        if (HasArg("--alt-180d"))
        {
            await RunAlt180Async();
            return;
        }
        if (HasArg("--alt-60d"))
        {
            await RunAlt60Async();
            return;
        }
        if (HasArg("--daily-180d"))
        {
            await RunDaily180Async();
            return;
        }
        if (HasArg("--daily-60d"))
        {
            await RunDaily60Async();
            return;
        }
        if (HasArg("--target70-90d"))
        {
            await RunTarget70Async(pages: 18);  // ~90일 (먼저 검사 — --target70 와 substring 충돌 방지)
            return;
        }
        if (HasArg("--target70"))
        {
            await RunTarget70Async();
            return;
        }
        if (HasArg("--live-all"))
        {
            // [v5.22.1] 라이브 로직 백테스트 — AI 게이트 제거, 가드만으로 진입
            //   1/10/30/60/90/180/360일 7개 기간 카테고리별 합산
            await RunLiveAllPeriodsAsync();
            return;
        }
        if (HasArg("--ai-all"))
        {
            // [v5.21.13] AI 게이트 포함 백테스트 — 라이브 봇 시뮬과 동일
            //   기존 RunLogicBreakdownAsync = 가드만 시뮬 (AI 미포함)
            //   본 모드 = 가드 + Lorentzian KNN (라이브 봇의 ML.NET 모델 근사) 게이트
            //   AI 임계: pred.Prediction > 0 통과 (v5.21.12 임계 0.005 와 동급 수준)
            await RunAiAllPeriodsAsync();
            return;
        }
        if (HasArg("--logic-1d"))
        {
            await RunLogicBreakdownAsync(pages: 1);  // 1일 (실제 5일치 데이터, 페이징 최소 단위)
            return;
        }
        if (HasArg("--logic-10d"))
        {
            await RunLogicBreakdownAsync(pages: 2);  // 10일
            return;
        }
        if (HasArg("--logic-30d"))
        {
            await RunLogicBreakdownAsync(pages: 6);  // 30일
            return;
        }
        if (HasArg("--logic-60d"))
        {
            await RunLogicBreakdownAsync(pages: 12);  // 60일
            return;
        }
        if (HasArg("--logic-90d"))
        {
            await RunLogicBreakdownAsync(pages: 18);  // 90일
            return;
        }
        if (HasArg("--lorentzian-alt-90d"))
        {
            await RunLorentzianAlt90dAsync();
            return;
        }
        if (HasArg("--lorentzian-alt-sweep"))
        {
            await RunLorentzianAltSweepAsync();
            return;
        }
        if (HasArg("--micro-alt-gate"))
        {
            await RunMicroAltGateTestAsync();
            return;
        }
        if (HasArg("--hourly-direction"))
        {
            await RunHourlyDirectionTestAsync();
            return;
        }
        if (HasArg("--my-strategy"))
        {
            await RunMyStrategyTestAsync();
            return;
        }
        if (HasArg("--current-gate"))
        {
            await RunCurrentGateTestAsync();
            return;
        }
        if (HasArg("--baseline-tpsl"))
        {
            await RunBaselineTpSlSweepAsync();
            return;
        }
        if (HasArg("--lorentzian-1h-gate"))
        {
            await RunLorentzian1hGateTestAsync();
            return;
        }
        if (HasArg("--lorentzian-1h"))
        {
            await RunLorentzian1hTestAsync();
            return;
        }
        if (HasArg("--loss-analysis"))
        {
            await RunLossCoinAnalysisAsync();
            return;
        }
        if (HasArg("--loss-pattern"))
        {
            await RunLossPatternAnalysisAsync();
            return;
        }
        if (HasArg("--logic-180d"))
        {
            await RunLogicBreakdownAsync(pages: 36);  // 180일 (6개월)
            return;
        }
        if (HasArg("--logic-365d"))
        {
            await RunLogicBreakdownAsync(pages: 70);  // 365일 (1년)
            return;
        }
        if (HasArg("--pump-tune"))
        {
            await RunPumpTuneAsync(pages: 18);  // 90일 PUMP 전용
            return;
        }
        if (HasArg("--entry-compare"))
        {
            await RunEntryTimingCompareAsync(pages: 36);  // 180일 — 진입 타이밍 비교
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
