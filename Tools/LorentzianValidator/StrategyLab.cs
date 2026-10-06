// ===============================================================================
//  --lab : [v5.34.17] 라이브 조건 강제 전략 검증 하네스 (2026-10-06)
//    "+106R 흑자" 착시 재발 방지 — 모든 후보를 같은 잣대로만 잰다.
//    · 1건 명목 $3,000 고정 · 슬롯 메이저 2 / 알트 3 · 심볼당 1포지션
//    · 왕복 수수료+슬리피지 0.12% · 펀딩 0.01%/8h (방향 무관 차감, 보수적)
//    · 데이터 = 최신 15m 캐시(_15m_71) → 1h/4h/1d 집계, 체결·손절은 15m 봉 단위
//    · 워크포워드: 기간 6등분, 1~5구간은 직전까지 성적 1등 설정을 고정 적용한 OOS 만 합산
// ===============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

internal static class StrategyLab
{
    const double Notional = 3000, FeeRT = 0.0012, FundPer8h = 0.0001;
    const int MajSlots = 2, AltSlots = 3, Segs = 6;
    static readonly string[] Universe = { "BTCUSDT","ETHUSDT","XRPUSDT","BNBUSDT","SOLUSDT","DOGEUSDT","ADAUSDT","TRXUSDT","AVAXUSDT","LINKUSDT",
        "DOTUSDT","LTCUSDT","BCHUSDT","NEARUSDT","UNIUSDT","APTUSDT","ICPUSDT","ETCUSDT","FILUSDT","ARBUSDT",
        "OPUSDT","ATOMUSDT","SUIUSDT","AAVEUSDT","XLMUSDT","INJUSDT","ALGOUSDT","HBARUSDT","SEIUSDT","VETUSDT" };
    static readonly HashSet<string> Majors = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };

    sealed class Bars { public long[] t = Array.Empty<long>(); public double[] o = Array.Empty<double>(), h = Array.Empty<double>(), l = Array.Empty<double>(), c = Array.Empty<double>(); public int n; }
    sealed class TfData { public Bars b = new(); public int[] end15 = Array.Empty<int>(); public double[] atr = Array.Empty<double>(); }
    sealed class Sym
    {
        public string name = ""; public bool major; public Bars m = new();
        public Dictionary<int, TfData> tf = new();
        public Dictionary<int, int[]> lastClosed = new();   // 15m j → 그 봉 시가 이전에 마감된 마지막 TF 봉 index
    }
    sealed class Trade { public int sym; public bool major; public int dir; public long tIn, tOut; public double pnl; public string how = "", name = "", status = ""; public double px0, px1; }
    sealed class Exit { public double stopAtr = 2, trailAtr, tpR; public int maxHoldTf; public long forceExitT; }
    sealed class Cfg { public string fam = "", name = ""; public Func<List<Sym>, List<Trade>> gen = _ => new(); }
    sealed class Res { public Cfg cfg = null!; public List<Trade> taken = new(), rejected = new(); public double[] seg = new double[Segs]; public double total, pf, mdd, win; }

    static long seg0, segLen;
    static bool Neighborhood; static string Pick = "";
    static int Base = 15; static string CacheSuffix = "_15m_71"; static long UntilMs = long.MaxValue, FromMs = 0;

    public static void Run(string[] args)
    {
        for (int a = 0; a < args.Length - 1; a++)
        {
            // --lab-base 60 : 1h 캐시(_1h_40, 2019~) 기반 — 미사용 과거구간 검증용
            if (args[a] == "--lab-pick") Pick = args[a + 1];
            if (args[a] == "--lab-base" && args[a + 1] == "60") { Base = 60; CacheSuffix = "_1h_40"; }
            if (args[a] == "--lab-until") UntilMs = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(args[a + 1]), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            if (args[a] == "--lab-from") FromMs = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(args[a + 1]), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }
        Console.WriteLine($"=== --lab : 라이브 조건 강제 전략 검증 (명목 $3,000 · 슬롯 2/3 · 비용 0.12%+펀딩 · 워크포워드) · 기본봉 {Base}m ===\n");
        var syms = Load();
        long tMin = syms.Min(s => s.m.t[0]), tMax = syms.Max(s => s.m.t[s.m.n - 1]);
        seg0 = tMin; segLen = (tMax - tMin) / Segs + 1;
        Console.WriteLine($"데이터 {D(tMin):yyyy-MM-dd} ~ {D(tMax):yyyy-MM-dd} · {syms.Count}코인 · 구간 {segLen / 86400000}일 × {Segs}\n");

        Neighborhood = args.Contains("--lab-donchian");
        var cfgs = BuildConfigs();
        var results = new List<Res>();
        foreach (var cfg in cfgs)
        {
            var cands = cfg.gen(syms);
            var r = Portfolio(cfg, cands);
            results.Add(r);
            Console.WriteLine($"  {cfg.name,-44} 체결{r.taken.Count,5} 승률{r.win,5:F1}% PF{r.pf,5:F2} 총{r.total,9:F0}$ 낙폭{r.mdd,8:F0}$ | {string.Join(" ", r.seg.Select(x => x >= 0 ? "+" : "-"))}");
        }

        // 벤치마크: BTC 매수 보유 $3,000
        var btc = syms.First(s => s.name == "BTCUSDT").m;
        Console.WriteLine($"\n(참고) BTC 매수후보유 $3,000: {Notional * (btc.c[btc.n - 1] / btc.o[0] - 1):F0}$");

        Console.WriteLine("\n=== 전 구간 순위 (총손익) — 6구간 부호 ===");
        foreach (var r in results.OrderByDescending(x => x.total).Take(15))
            Console.WriteLine($"  {r.cfg.name,-44} 총{r.total,9:F0}$ PF{r.pf,5:F2} 낙폭{r.mdd,8:F0}$ 흑자구간 {r.seg.Count(x => x > 0)}/{Segs} [{string.Join(" ", r.seg.Select(x => $"{x,6:F0}"))}]");

        Console.WriteLine("\n=== 워크포워드 OOS (직전까지 누적 1등 설정을 다음 구간에 적용) ===");
        double oos = 0; var oosTrades = new List<Trade>(); int posF = 0;
        for (int f = 1; f < Segs; f++)
        {
            var pick = results.Where(r => r.taken.Count(t => SegOf(t.tIn) < f) >= 15)
                              .OrderByDescending(r => r.seg.Take(f).Sum()).FirstOrDefault();
            if (pick == null) { Console.WriteLine($"  구간{f}: 후보 없음"); continue; }
            double v = pick.seg[f]; oos += v; if (v > 0) posF++;
            oosTrades.AddRange(pick.taken.Where(t => SegOf(t.tIn) == f));
            Console.WriteLine($"  구간{f} {D(seg0 + f * segLen):yyyy-MM-dd}~ 선택={pick.cfg.name,-44} 학습{pick.seg.Take(f).Sum(),8:F0}$ → OOS {v,8:F0}$");
        }
        Console.WriteLine($"  ▶ OOS 합계 {oos:F0}$ · 흑자구간 {posF}/{Segs - 1} · 판정: {(oos > 0 && posF >= 4 ? "★합격" : "불합격")}");

        // 견고 후보: 6구간 중 5+ 흑자 & 총손익>0
        var robust = results.Where(r => r.total > 0 && r.seg.Count(x => x > 0) >= 5).OrderByDescending(r => r.total).ToList();
        Console.WriteLine($"\n=== 견고 후보 (6구간 중 5개 이상 흑자) : {robust.Count}개 ===");
        foreach (var r in robust) Console.WriteLine($"  {r.cfg.name,-44} 총{r.total,9:F0}$ PF{r.pf,5:F2} 낙폭{r.mdd,8:F0}$ 체결{r.taken.Count}");

        // CSV: 견고 후보 상위 + 워크포워드 경로의 일별 손익
        var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
        WriteDaily(Path.Combine(outDir, "lab-wf-daily.csv"), oosTrades);
        foreach (var r in results.Where(r => Pick.Length > 0 && r.cfg.name.Contains(Pick)))
        {
            WriteDaily(Path.Combine(outDir, $"lab-pick-{Safe(r.cfg.name)}-{Base}m.csv"), r.taken);
            File.WriteAllLines(Path.Combine(outDir, $"lab-trades-{Safe(r.cfg.name)}-{Base}m.csv"), new[] { "status,symbol,dir,inUTC,outUTC,entry,exit,how,pnlUSD" }
                .Concat(r.taken.Concat(r.rejected).OrderBy(t => t.tIn).Select(t => $"{t.status},{t.name},{(t.dir > 0 ? "L" : "S")},{D(t.tIn):yyyy-MM-dd HH:mm},{D(t.tOut):yyyy-MM-dd HH:mm},{t.px0.ToString(CultureInfo.InvariantCulture)},{t.px1.ToString(CultureInfo.InvariantCulture)},{t.how},{t.pnl.ToString("F2", CultureInfo.InvariantCulture)}")));
        }
        foreach (var r in robust.Take(3))
            WriteDaily(Path.Combine(outDir, $"lab-daily-{Safe(r.cfg.name)}.csv"), r.taken);
        File.WriteAllLines(Path.Combine(outDir, "lab-summary.csv"), new[] { "fam,name,trades,winPct,pf,totalUSD,mddUSD," + string.Join(",", Enumerable.Range(0, Segs).Select(i => "seg" + i)) }
            .Concat(results.Select(r => $"{r.cfg.fam},\"{r.cfg.name}\",{r.taken.Count},{r.win:F1},{r.pf:F2},{r.total:F0},{r.mdd:F0},{string.Join(",", r.seg.Select(x => x.ToString("F0")))}")));
        Console.WriteLine("\nCSV: lab-summary.csv · lab-wf-daily.csv · lab-daily-*.csv");
    }

    // ───────────────────────── 후보 정의 ─────────────────────────
    static List<Cfg> BuildConfigs()
    {
        var L = new List<Cfg>();
        if (Neighborhood)
        {
            // 파라미터 이웃 검증 — 한 점만 흑자인지, 지형 전체가 흑자인지
            foreach (var tf in new[] { 240, 1440 })
                foreach (var N in new[] { 20, 30, 40, 55, 70, 90 })
                    foreach (var m in new[] { 4.0, 5.0, 6.0 })
                    {
                        int tf_ = tf, N_ = N; double m_ = m;
                        L.Add(new Cfg
                        {
                            fam = "DONCHIAN", name = $"돈치안 {TfName(tf)} N{N} 트레일{m}ATR 롱숏",
                            gen = S => SignalTrades(S, tf_, (s, T, i) =>
                            {
                                if (i < N_ + 2) return 0;
                                double hi = Max(T.b.h, i - N_, i - 1), lo = Min(T.b.l, i - N_, i - 1);
                                double hiP = Max(T.b.h, i - N_ - 1, i - 2), loP = Min(T.b.l, i - N_ - 1, i - 2);
                                if (T.b.c[i] > hi && T.b.c[i - 1] <= hiP) return 1;
                                if (T.b.c[i] < lo && T.b.c[i - 1] >= loP) return -1;
                                return 0;
                            }, new Exit { stopAtr = 2, trailAtr = m_ }, null)
                        });
                    }
            // 신고가 필터 — 롱은 돌파 시점 종가가 최근 H봉 최고가(=장기 신고가)일 때만, 숏은 장기 신저가일 때만
            //   (사용자 지적 2026-10-06: "전고점 돌파한 코인이면 수익이 나야 한다")
            foreach (var hd in new[] { 90, 180, 365 })
                foreach (var shortToo in new[] { false, true })
                    foreach (var N in new[] { 20, 55 })
                    {
                        int N_ = N, H_ = hd * 6; bool st_ = shortToo;
                        L.Add(new Cfg
                        {
                            fam = "DONCHIAN_HI", name = $"돈치안 4h N{N} 트레일5ATR 롱숏 +{hd}일신고가{(shortToo ? "(숏도신저가)" : "(롱만)")}",
                            gen = S => SignalTrades(S, 240, (s, T, i) =>
                            {
                                if (i < N_ + 2) return 0;
                                double hi = Max(T.b.h, i - N_, i - 1), lo = Min(T.b.l, i - N_, i - 1);
                                double hiP = Max(T.b.h, i - N_ - 1, i - 2), loP = Min(T.b.l, i - N_ - 1, i - 2);
                                if (T.b.c[i] > hi && T.b.c[i - 1] <= hiP)
                                    return i >= H_ && T.b.c[i] > Max(T.b.h, i - H_, i - 1) ? 1 : 0;
                                if (T.b.c[i] < lo && T.b.c[i - 1] >= loP)
                                    return !st_ || (i >= H_ && T.b.c[i] < Min(T.b.l, i - H_, i - 1)) ? -1 : 0;
                                return 0;
                            }, new Exit { stopAtr = 2, trailAtr = 5 }, null)
                        });
                    }
            return L;
        }
        // ① 돈치안 채널 돌파 + ATR 트레일 (신규 돌파 봉에서만)
        foreach (var tf in new[] { 240, 1440 })
            foreach (var N in new[] { 20, 55 })
                foreach (var m in new[] { 3.0, 5.0 })
                    foreach (var ls in new[] { false, true })
                    {
                        int tf_ = tf, N_ = N; double m_ = m; bool ls_ = ls;
                        L.Add(new Cfg
                        {
                            fam = "DONCHIAN", name = $"돈치안 {TfName(tf)} N{N} 트레일{m}ATR {(ls ? "롱숏" : "롱")}",
                            gen = S => SignalTrades(S, tf_, (s, T, i) =>
                            {
                                if (i < N_ + 2) return 0;
                                double hi = Max(T.b.h, i - N_, i - 1), lo = Min(T.b.l, i - N_, i - 1);
                                double hiP = Max(T.b.h, i - N_ - 1, i - 2), loP = Min(T.b.l, i - N_ - 1, i - 2);
                                if (T.b.c[i] > hi && T.b.c[i - 1] <= hiP) return 1;
                                if (ls_ && T.b.c[i] < lo && T.b.c[i - 1] >= loP) return -1;
                                return 0;
                            }, new Exit { stopAtr = 2, trailAtr = m_ }, null)
                        });
                    }
        // ② EMA 크로스 추세추종 — 반대 크로스 청산 + 재난손절 3ATR
        foreach (var tf in new[] { 240, 1440 })
            foreach (var (f, sl) in new[] { (10, 50), (20, 100) })
                foreach (var ls in new[] { false, true })
                {
                    int tf_ = tf, f_ = f, s_ = sl; bool ls_ = ls;
                    var cache = new Dictionary<Sym, (double[] a, double[] b)>();
                    (double[] a, double[] b) E(Sym s, TfData T) { if (!cache.TryGetValue(s, out var v)) { v = (Ema(T.b.c, f_), Ema(T.b.c, s_)); cache[s] = v; } return v; }
                    L.Add(new Cfg
                    {
                        fam = "EMA_X", name = $"EMA{f}/{sl} 크로스 {TfName(tf)} {(ls ? "롱숏" : "롱")}",
                        gen = S => SignalTrades(S, tf_, (s, T, i) =>
                        {
                            if (i < s_ + 2) return 0; var (a, b) = E(s, T);
                            if (a[i] > b[i] && a[i - 1] <= b[i - 1]) return 1;
                            if (ls_ && a[i] < b[i] && a[i - 1] >= b[i - 1]) return -1;
                            return 0;
                        }, new Exit { stopAtr = 3 }, (s, T, k, dir) => { var (a, b) = E(s, T); return dir > 0 ? a[k] < b[k] : a[k] > b[k]; })
                    });
                }
        // ③ 단기 평균회귀 — RSI(2) 극단 + SMA200 추세 쪽, SMA5 회복 청산
        foreach (var tf in new[] { 60, 240 })
            foreach (var th in new[] { 5.0, 10.0 })
                foreach (var k in new[] { 2.0, 3.0 })
                    foreach (var ls in new[] { false, true })
                    {
                        int tf_ = tf; double th_ = th, k_ = k; bool ls_ = ls;
                        var cache = new Dictionary<Sym, (double[] r, double[] s200, double[] s5)>();
                        (double[] r, double[] s200, double[] s5) I(Sym s, TfData T) { if (!cache.TryGetValue(s, out var v)) { v = (Rsi(T.b.c, 2), Sma(T.b.c, 200), Sma(T.b.c, 5)); cache[s] = v; } return v; }
                        L.Add(new Cfg
                        {
                            fam = "MEANREV", name = $"RSI2<{th} 평균회귀 {TfName(tf)} 손절{k}ATR {(ls ? "롱숏" : "롱")}",
                            gen = S => SignalTrades(S, tf_, (s, T, i) =>
                            {
                                if (i < 205) return 0; var (r, s200, _) = I(s, T);
                                if (r[i] < th_ && T.b.c[i] > s200[i]) return 1;
                                if (ls_ && r[i] > 100 - th_ && T.b.c[i] < s200[i]) return -1;
                                return 0;
                            }, new Exit { stopAtr = k_, maxHoldTf = tf_ == 60 ? 48 : 18 }, (s, T, kk, dir) => { var (_, _, s5) = I(s, T); return dir > 0 ? T.b.c[kk] > s5[kk] : T.b.c[kk] < s5[kk]; })
                        });
                    }
        // ④ 변동성 돌파 (일봉: 당일시가 + k×전일레인지 돌파 진입, 다음날 00시 청산, 손절=당일시가)
        foreach (var kk in new[] { 0.5, 0.7 })
            foreach (var ls in new[] { false, true })
            {
                double k_ = kk; bool ls_ = ls;
                L.Add(new Cfg { fam = "VOLBRK", name = $"변동성돌파 k{kk} 일봉 {(ls ? "롱숏" : "롱")}", gen = S => VolBreakout(S, k_, ls_) });
            }
        // ⑤ 코인 간 순위 — 모멘텀(상위 매수)/반전(하위 매수), 리밸런싱 R일, 손절 3ATR(일봉)
        foreach (var lb in new[] { 1, 7, 14, 30 })
            foreach (var rb in new[] { 1, 7 })
                foreach (var rev in new[] { false, true })
                    foreach (var ls in new[] { false, true })
                    {
                        int lb_ = lb, rb_ = rb; bool rev_ = rev, ls_ = ls;
                        L.Add(new Cfg { fam = "XSEC", name = $"순위{(rev ? "반전" : "모멘텀")} {lb}일 리밸{rb}일 {(ls ? "롱숏" : "롱")}", gen = S => CrossSection(S, lb_, rb_, rev_, ls_) });
                    }
        return L;
    }

    // ───────────────────────── 거래 생성 ─────────────────────────
    static List<Trade> SignalTrades(List<Sym> S, int tf, Func<Sym, TfData, int, int> sig, Exit x, Func<Sym, TfData, int, int, bool>? exitCond)
    {
        var res = new List<Trade>();
        for (int si = 0; si < S.Count; si++)
        {
            var s = S[si]; var T = s.tf[tf];
            for (int i = 1; i < T.b.n - 1; i++)
            {
                int d = sig(s, T, i); if (d == 0) continue;
                int e0 = T.end15[i] + 1; if (e0 >= s.m.n) continue;
                var tr = Sim(s, si, tf, i, e0, s.m.o[e0], d, x, exitCond == null ? null : (k, dir) => exitCond(s, T, k, dir));
                if (tr != null) res.Add(tr);
            }
        }
        return res;
    }

    static Trade? Sim(Sym s, int si, int tf, int sigTf, int e0, double entry, int dir, Exit x, Func<int, int, bool>? exitCond, double fixedStop = double.NaN)
    {
        var m = s.m; var T = s.tf[tf]; var lc = s.lastClosed[tf];
        double atr = T.atr[sigTf]; if (atr <= 0 || entry <= 0) return null;
        double stop = double.IsNaN(fixedStop) ? entry - dir * x.stopAtr * atr : fixedStop;
        double risk = Math.Abs(entry - stop); if (risk <= 0) return null;
        double tp = x.tpR > 0 ? entry + dir * x.tpR * risk : double.NaN;
        int lastTf = sigTf; double px = 0; string how = ""; int xj = -1;
        for (int j = e0; j < m.n; j++)
        {
            int k = lc[j];
            while (lastTf < k)
            {
                lastTf++;
                if (x.trailAtr > 0)
                {
                    double ns = T.b.c[lastTf] - dir * x.trailAtr * T.atr[lastTf];
                    if (dir > 0 ? ns > stop : ns < stop) stop = ns;
                }
                if (exitCond != null && exitCond(lastTf, dir)) { px = m.o[j]; how = "SIG"; xj = j; goto done; }
                if (x.maxHoldTf > 0 && lastTf - sigTf >= x.maxHoldTf) { px = m.o[j]; how = "TIME"; xj = j; goto done; }
            }
            if (x.forceExitT > 0 && m.t[j] >= x.forceExitT) { px = m.o[j]; how = "TIME"; xj = j; goto done; }
            if (dir > 0)
            {
                if (m.o[j] <= stop) { px = m.o[j]; how = "SL"; xj = j; goto done; }
                if (m.l[j] <= stop) { px = stop; how = "SL"; xj = j; goto done; }
                if (!double.IsNaN(tp) && m.h[j] >= tp) { px = tp; how = "TP"; xj = j; goto done; }
            }
            else
            {
                if (m.o[j] >= stop) { px = m.o[j]; how = "SL"; xj = j; goto done; }
                if (m.h[j] >= stop) { px = stop; how = "SL"; xj = j; goto done; }
                if (!double.IsNaN(tp) && m.l[j] <= tp) { px = tp; how = "TP"; xj = j; goto done; }
            }
        }
        xj = m.n - 1; px = m.c[xj]; how = "END";
    done:
        double hours = (m.t[xj] - m.t[e0]) / 3600000.0;
        double pnl = dir * (px - entry) / entry - FeeRT - FundPer8h * Math.Max(0, hours) / 8;
        return new Trade { sym = si, major = s.major, dir = dir, tIn = m.t[e0], tOut = m.t[xj] + Base * 60000L, pnl = pnl * Notional, how = how, name = s.name, px0 = entry, px1 = px };
    }

    static List<Trade> VolBreakout(List<Sym> S, double k, bool ls)
    {
        var res = new List<Trade>();
        for (int si = 0; si < S.Count; si++)
        {
            var s = S[si]; var T = s.tf[1440];
            for (int i = 20; i < T.b.n - 1; i++)
            {
                int d0 = T.end15[i] + 1; int d1 = T.end15[i + 1]; if (d1 <= d0) continue;
                double open = s.m.o[d0], rng = T.b.h[i] - T.b.l[i]; if (rng <= 0) continue;
                double up = open + k * rng, dn = open - k * rng;
                for (int j = d0; j <= d1; j++)
                {
                    int dir = 0; double ent = 0;
                    if (s.m.h[j] >= up) { dir = 1; ent = Math.Max(up, s.m.o[j]); }
                    else if (ls && s.m.l[j] <= dn) { dir = -1; ent = Math.Min(dn, s.m.o[j]); }
                    if (dir == 0) continue;
                    // 진입봉 내 손절(시가) 판정은 다음 봉부터 — 같은 봉은 순서 불명이라 손절 우선 가정
                    if (dir > 0 ? s.m.l[j] <= open : s.m.h[j] >= open)
                    {
                        double pl = -Math.Abs(ent - open) / ent - FeeRT;
                        res.Add(new Trade { sym = si, major = s.major, dir = dir, tIn = s.m.t[j], tOut = s.m.t[j] + Base * 60000L, pnl = pl * Notional, how = "SL" });
                        break;
                    }
                    if (j + 1 >= s.m.n) break;
                    var tr = Sim(s, si, 1440, i, j + 1, ent, dir, new Exit { forceExitT = s.m.t[d1] + Base * 60000L }, null, open);
                    if (tr != null) { tr.tIn = s.m.t[j]; res.Add(tr); }
                    break;
                }
            }
        }
        return res;
    }

    static List<Trade> CrossSection(List<Sym> S, int lb, int rb, bool rev, bool ls)
    {
        var res = new List<Trade>();
        // 일봉 day-number → index
        var idx = S.Select(s => { var T = s.tf[1440]; var d = new Dictionary<long, int>(); for (int i = 0; i < T.b.n; i++) d[T.b.t[i] / 86400000] = i; return d; }).ToList();
        long dMin = S.Min(s => s.tf[1440].b.t[0] / 86400000) + lb + 20, dMax = S.Max(s => s.tf[1440].b.t[s.tf[1440].b.n - 1] / 86400000);
        for (long day = dMin; day + rb <= dMax; day += rb)
        {
            var sc = new List<(int si, double r, int i)>();
            for (int si = 0; si < S.Count; si++)
            {
                if (!idx[si].TryGetValue(day - 1, out int i) || !idx[si].TryGetValue(day - 1 - lb, out int i0)) continue;
                var c = S[si].tf[1440].b.c; sc.Add((si, c[i] / c[i0] - 1, i));
            }
            if (sc.Count < 10) continue;
            var ord = sc.OrderByDescending(x => x.r).ToList();
            var longs = rev ? ord.Skip(ord.Count - 3) : ord.Take(3);
            var shorts = rev ? ord.Take(2) : ord.Skip(ord.Count - 2);
            long exitT = (day + rb) * 86400000;
            void Go((int si, double r, int i) p, int dir)
            {
                var s = S[p.si]; int e0 = s.tf[1440].end15[p.i] + 1; if (e0 >= s.m.n) return;
                var tr = Sim(s, p.si, 1440, p.i, e0, s.m.o[e0], dir, new Exit { stopAtr = 3, forceExitT = exitT }, null);
                if (tr != null) res.Add(tr);
            }
            foreach (var p in longs) Go(p, 1);
            if (ls) foreach (var p in shorts) Go(p, -1);
        }
        return res;
    }

    // ───────────────────────── 포트폴리오 재생 ─────────────────────────
    static Res Portfolio(Cfg cfg, List<Trade> cands)
    {
        var r = new Res { cfg = cfg };
        var open = new List<Trade>(); var busy = new Dictionary<int, long>();
        foreach (var c in cands.OrderBy(x => x.tIn).ThenBy(x => x.sym))
        {
            open.RemoveAll(o => o.tOut <= c.tIn);
            if (busy.TryGetValue(c.sym, out var bt) && c.tIn < bt) { c.status = "SYM_BUSY"; r.rejected.Add(c); continue; }
            if (open.Count(o => o.major == c.major) >= (c.major ? MajSlots : AltSlots)) { c.status = "SLOT_FULL:" + string.Join("/", open.Where(o => o.major == c.major).Select(o => o.name.Replace("USDT", ""))); r.rejected.Add(c); continue; }
            c.status = "TAKEN"; open.Add(c); r.taken.Add(c); busy[c.sym] = c.tOut;
        }
        foreach (var t in r.taken) { int sg = SegOf(t.tIn); if (sg >= 0 && sg < Segs) r.seg[sg] += t.pnl; }
        r.total = r.taken.Sum(t => t.pnl);
        double gp = r.taken.Where(t => t.pnl > 0).Sum(t => t.pnl), gl = -r.taken.Where(t => t.pnl <= 0).Sum(t => t.pnl);
        r.pf = gl > 0 ? gp / gl : 99; r.win = r.taken.Count > 0 ? 100.0 * r.taken.Count(t => t.pnl > 0) / r.taken.Count : 0;
        double cum = 0, pk = 0; foreach (var t in r.taken.OrderBy(t => t.tOut)) { cum += t.pnl; pk = Math.Max(pk, cum); r.mdd = Math.Min(r.mdd, cum - pk); }
        return r;
    }

    static void WriteDaily(string path, List<Trade> trades)
    {
        if (trades.Count == 0) return;
        var byDay = trades.GroupBy(t => D(t.tOut).AddHours(9).Date).ToDictionary(g => g.Key, g => g.ToList());
        var d0 = D(seg0).AddHours(9).Date; var d1 = byDay.Keys.Max();
        var lines = new List<string> { "dateKST,trades,wins,pnlUSD,cumUSD" }; double cum = 0;
        for (var d = d0; d <= d1; d = d.AddDays(1))
        {
            byDay.TryGetValue(d, out var l); double p = l?.Sum(x => x.pnl) ?? 0; cum += p;
            lines.Add($"{d:yyyy-MM-dd},{l?.Count ?? 0},{l?.Count(x => x.pnl > 0) ?? 0},{p.ToString("F2", CultureInfo.InvariantCulture)},{cum.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        File.WriteAllLines(path, lines);
    }

    // ───────────────────────── 데이터 ─────────────────────────
    static List<Sym> Load()
    {
        var list = new List<Sym>();
        foreach (var name in Universe)
        {
            var f = Path.Combine("cache", name + CacheSuffix + ".csv");
            if (!File.Exists(f)) { Console.WriteLine($"  [{name}] 캐시 없음 — --elliott-daily --ew-pages 71 먼저 실행"); continue; }
            var rows = File.ReadAllLines(f).Select(x => x.Split(',')).Where(p => p.Length >= 6)
                .Where(p => { long t0 = long.Parse(p[0]); return t0 >= FromMs && t0 < UntilMs; }).ToList();
            if (rows.Count < 24 * 60 * 60 / Base) { Console.WriteLine($"  [{name}] 기간내 데이터 부족 — 제외"); continue; }
            var b = new Bars { n = rows.Count, t = new long[rows.Count], o = new double[rows.Count], h = new double[rows.Count], l = new double[rows.Count], c = new double[rows.Count] };
            for (int i = 0; i < rows.Count; i++)
            {
                b.t[i] = long.Parse(rows[i][0]); b.o[i] = P(rows[i][1]); b.h[i] = P(rows[i][2]); b.l[i] = P(rows[i][3]); b.c[i] = P(rows[i][4]);
            }
            var s = new Sym { name = name, major = Majors.Contains(name), m = b };
            foreach (var tf in new[] { 60, 240, 1440 }) Aggregate(s, tf);
            list.Add(s);
        }
        return list;
    }

    static void Aggregate(Sym s, int tfMin)
    {
        var m = s.m; long w = tfMin * 60000L; int need = tfMin / Base;
        var t = new List<long>(); var o = new List<double>(); var h = new List<double>(); var l = new List<double>(); var c = new List<double>(); var e = new List<int>();
        int i = 0;
        while (i < m.n)
        {
            long bk = m.t[i] / w; int j = i; double hi = m.h[i], lo = m.l[i];
            while (j + 1 < m.n && m.t[j + 1] / w == bk) { j++; hi = Math.Max(hi, m.h[j]); lo = Math.Min(lo, m.l[j]); }
            if (j - i + 1 == need) { t.Add(bk * w); o.Add(m.o[i]); h.Add(hi); l.Add(lo); c.Add(m.c[j]); e.Add(j); }
            i = j + 1;
        }
        var T = new TfData { b = new Bars { n = t.Count, t = t.ToArray(), o = o.ToArray(), h = h.ToArray(), l = l.ToArray(), c = c.ToArray() }, end15 = e.ToArray() };
        T.atr = Atr(T.b, 14);
        s.tf[tfMin] = T;
        var lc = new int[m.n]; int k = -1;
        for (int j = 0; j < m.n; j++) { while (k + 1 < T.b.n && T.end15[k + 1] < j) k++; lc[j] = k; }
        s.lastClosed[tfMin] = lc;
    }

    // ───────────────────────── 지표 ─────────────────────────
    static double[] Ema(double[] x, int p) { var r = new double[x.Length]; double a = 2.0 / (p + 1); for (int i = 0; i < x.Length; i++) r[i] = i == 0 ? x[0] : a * x[i] + (1 - a) * r[i - 1]; return r; }
    static double[] Sma(double[] x, int p) { var r = new double[x.Length]; double s = 0; for (int i = 0; i < x.Length; i++) { s += x[i]; if (i >= p) s -= x[i - p]; r[i] = i >= p - 1 ? s / p : double.NaN; } return r; }
    static double[] Rsi(double[] x, int p)
    {
        var r = new double[x.Length]; double g = 0, l = 0;
        for (int i = 1; i < x.Length; i++)
        {
            double d = x[i] - x[i - 1], up = Math.Max(d, 0), dn = Math.Max(-d, 0);
            if (i <= p) { g += up / p; l += dn / p; } else { g = (g * (p - 1) + up) / p; l = (l * (p - 1) + dn) / p; }
            r[i] = i < p ? 50 : (l == 0 ? 100 : 100 - 100 / (1 + g / l));
        }
        return r;
    }
    static double[] Atr(Bars b, int p)
    {
        var r = new double[b.n]; double a = 0;
        for (int i = 1; i < b.n; i++)
        {
            double tr = Math.Max(b.h[i] - b.l[i], Math.Max(Math.Abs(b.h[i] - b.c[i - 1]), Math.Abs(b.l[i] - b.c[i - 1])));
            a = i <= p ? a + tr / p : (a * (p - 1) + tr) / p; r[i] = i >= p ? a : 0;
        }
        return r;
    }
    static double Max(double[] x, int a, int b) { double m = double.MinValue; for (int i = Math.Max(0, a); i <= b; i++) m = Math.Max(m, x[i]); return m; }
    static double Min(double[] x, int a, int b) { double m = double.MaxValue; for (int i = Math.Max(0, a); i <= b; i++) m = Math.Min(m, x[i]); return m; }

    static double P(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    static DateTime D(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    static int SegOf(long t) => (int)Math.Min(Segs - 1, Math.Max(0, (t - seg0) / segLen));
    static string TfName(int m) => m == 60 ? "1h" : m == 240 ? "4h" : "1d";
    static string Safe(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
}
