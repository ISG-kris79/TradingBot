// ===============================================================================
//  --donchian-parity : [v5.36.0] 라이브 규칙 코드(Services/DonchianTrend.cs, 링크 컴파일)가
//    백테스트(research.py · dump_parity.py 산출 parity_A.csv / parity_B.csv)와 같은 신호·같은 손절가를 내는지 슬리브별 건별 대조.
//    라이브와 똑같이 창을 자른다: 진입 판단·트레일 재계산 = 해당 TF 500봉 창.
//    대상 구간: 15m_71 캐시(2023-09~) — 신호는 과열추격 필터 포함, 쿨다운 제외(거래 결과 의존이라 양쪽 모두 미적용).
// ===============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Binance.Net.Interfaces;
using TradingBot.Services;
using LorentzianValidator;

internal static class DonchianParity
{
    public static void Run(string[] args)
    {
        Console.WriteLine("=== --donchian-parity : 라이브 규칙 코드 vs 백테스트(research.py) 슬리브별 건별 대조 ===\n");
        bool allOk = true;
        foreach (var sl in DonchianTrend.Sleeves) allOk &= RunSleeve(sl);
        Console.WriteLine($"\n최종 판정: {(allOk ? "★라이브 규칙 = 백테스트 규칙 (A·B 신호·손절가 일치)" : "불일치 있음 — 위 목록 확인")}");
    }

    static bool RunSleeve(DonchianTrend.Sleeve sl)
    {
        var csv = $"parity_{sl.Id}.csv";
        if (!File.Exists(csv)) { Console.WriteLine($"[{sl.Id}] {csv} 없음 — python dump_parity.py 먼저 실행"); return false; }
        var py = File.ReadAllLines(csv).Skip(1).Select(l => l.Split(',')).Select(p => new
        {
            sym = p[0], dir = int.Parse(p[1]), sigOpen = long.Parse(p[2]), entryMs = long.Parse(p[3]), exitMs = long.Parse(p[4]),
            entry = decimal.Parse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture), exitPx = decimal.Parse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture)
        }).ToList();
        var pySig = new HashSet<string>(py.Select(x => $"{x.sym}|{x.dir}|{x.sigOpen}"));

        int liveN = 0, matched = 0; var liveOnly = new List<string>(); var liveAll = new HashSet<string>();
        var kBySym = new Dictionary<string, List<IBinanceKline>>();
        foreach (var sym in DonchianTrend.Universe)
        {
            var f = Path.Combine("cache", sym + "_15m_71.csv");
            if (!File.Exists(f)) continue;
            var k = Aggregate(f, sl.TfMinutes); kBySym[sym] = k;
            for (int i = 500; i < k.Count; i++)
            {
                var win = k.GetRange(i - 499, 500);
                int d = DonchianTrend.Signal(win, win.Count - 1, sl.Lookback);
                if (d == 0) continue;
                var wAtr = DonchianTrend.Atr(win); var wEma = DonchianTrend.Ema(win, DonchianTrend.EmaLen);
                if (wAtr[^1] <= 0 || DonchianTrend.IsChasing(win, win.Count - 1, wAtr, wEma, d, out _)) continue;
                liveN++;
                string key = $"{sym}|{d}|{new DateTimeOffset(k[i].OpenTime).ToUnixTimeMilliseconds()}";
                liveAll.Add(key);
                if (pySig.Contains(key)) matched++; else liveOnly.Add(key);
            }
        }
        // 백테스트 덤프는 '청산된' 거래만 담는다 → 데이터 끝까지 안 끝난 신호는 라이브만으로 남는 게 정상.
        var pyInRange = py.Where(x => kBySym.TryGetValue(x.sym, out var k) && k.Count > 500 && x.sigOpen >= new DateTimeOffset(k[500].OpenTime).ToUnixTimeMilliseconds()).ToList();
        var pyOnly = pyInRange.Select(x => $"{x.sym}|{x.dir}|{x.sigOpen}").Where(key => !liveAll.Contains(key)).ToList();
        int liveOnlyUnexplained = liveOnly.Count(key => !IsRecent(key, kBySym));
        Console.WriteLine($"[{sl.Id}] 신호: 백테스트 {pyInRange.Count}건(청산분) · 라이브코드 {liveN}건 · 일치 {matched} · 백테만 {pyOnly.Count} · 라이브만 {liveOnly.Count} (그중 최근 120일 미청산 가능 {liveOnly.Count - liveOnlyUnexplained})");
        foreach (var s in pyOnly.Take(3)) Console.WriteLine($"   백테만: {s}");
        foreach (var s in liveOnly.Where(k2 => !IsRecent(k2, kBySym)).Take(3)) Console.WriteLine($"   라이브만: {s}");

        // 손절가: 백테스트 청산가 vs 청산 시점 라이브 RecomputeStop (부분익절은 손절선 경로에 영향 없음)
        int n = 0, exact = 0, gap = 0, bad = 0;
        foreach (var t in pyInRange)
        {
            var k = kBySym[t.sym];
            var exitBarOpen = DateTimeOffset.FromUnixTimeMilliseconds(t.exitMs - 900_000).UtcDateTime;
            var closed = k.Where(b => b.CloseTime <= exitBarOpen).ToList();
            if (closed.Count > 500) closed = closed.GetRange(closed.Count - 500, 500);
            var atr = DonchianTrend.Atr(closed);
            var entryUtc = DateTimeOffset.FromUnixTimeMilliseconds(t.entryMs).UtcDateTime;
            decimal stop = DonchianTrend.RecomputeStop(closed, atr, entryUtc, t.dir > 0, t.entry, sl);
            if (stop <= 0) continue;
            n++;
            double rel = (double)Math.Abs(stop - t.exitPx) / (double)t.exitPx;
            bool gapExit = t.dir > 0 ? t.exitPx < stop : t.exitPx > stop;
            if (rel < 0.002) exact++; else if (gapExit) gap++; else { bad++; if (bad <= 3) Console.WriteLine($"   손절 불일치: {t.sym} {t.dir} 라이브 {stop} vs 백테 {t.exitPx} ({rel:P2})"); }
        }
        Console.WriteLine($"[{sl.Id}] 손절가: {n}건 · 일치(0.2%) {exact} · 갭 청산 {gap} · 불일치 {bad}");
        return pyOnly.Count == 0 && liveOnlyUnexplained == 0 && bad == 0;
    }

    static bool IsRecent(string key, Dictionary<string, List<IBinanceKline>> kBySym)
    {
        var p = key.Split('|'); var k = kBySym[p[0]]; long sig = long.Parse(p[2]);
        long end = new DateTimeOffset(k[^1].CloseTime).ToUnixTimeMilliseconds();
        return end - sig < 120L * 86_400_000;
    }

    static List<IBinanceKline> Aggregate(string file, int tfMin)
    {
        long W = tfMin * 60_000L; int need = tfMin / 15;
        var rows = File.ReadAllLines(file).Select(l => l.Split(',')).Where(p => p.Length >= 6).ToList();
        var res = new List<IBinanceKline>();
        int i = 0;
        while (i < rows.Count)
        {
            long bk = long.Parse(rows[i][0]) / W; int j = i;
            decimal o = D(rows[i][1]), h = D(rows[i][2]), l = D(rows[i][3]);
            while (j + 1 < rows.Count && long.Parse(rows[j + 1][0]) / W == bk) { j++; h = Math.Max(h, D(rows[j][2])); l = Math.Min(l, D(rows[j][3])); }
            if (j - i + 1 == need)
                res.Add(new SimpleKline
                {
                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(bk * W).UtcDateTime,
                    OpenPrice = o, HighPrice = h, LowPrice = l, ClosePrice = D(rows[j][4]),
                    CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(bk * W + W - 1).UtcDateTime
                });
            i = j + 1;
        }
        return res;
    }
    static decimal D(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
