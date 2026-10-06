// ===============================================================================
//  --donchian-parity : [v5.35.0] 라이브 규칙 코드(Services/DonchianTrend.cs, 링크 컴파일)가
//    백테스트(--lab 돈치안 4h N55 트레일5ATR 롱숏)와 같은 신호·같은 손절가를 내는지 건별 대조.
//    라이브와 똑같이 창을 자른다: 진입 판단 = 4h 120봉 창 · 트레일 재계산 = 4h 500봉 창.
//    선행 조건: --lab --lab-donchian --lab-pick "4h N55 트레일5ATR 롱숏" 로 후보 CSV 생성.
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
    public static void Run()
    {
        var csv = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "lab-trades-돈치안_4h_N55_트레일5ATR_롱숏-15m.csv");
        if (!File.Exists(csv)) { Console.WriteLine("후보 CSV 없음 — 먼저 --lab --lab-donchian --lab-pick \"4h N55 트레일5ATR 롱숏\" 실행"); return; }
        var lab = File.ReadAllLines(csv).Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= 9).Select(p => new
        {
            status = p[0], sym = p[1], isLong = p[2] == "L",
            tIn = DateTime.SpecifyKind(DateTime.ParseExact(p[3], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc),
            tOut = DateTime.SpecifyKind(DateTime.ParseExact(p[4], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Utc),
            px0 = decimal.Parse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture),
            px1 = decimal.Parse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture),
            how = p[7]
        }).ToList();
        var labSig = new HashSet<string>(lab.Select(x => $"{x.sym}|{(x.isLong ? "L" : "S")}|{x.tIn:yyyy-MM-dd HH:mm}"));

        Console.WriteLine("=== --donchian-parity : 라이브 규칙 코드 vs 백테스트 건별 대조 ===\n");
        int liveN = 0, matched = 0; var liveOnly = new List<string>(); var liveAll = new HashSet<string>();
        var k4BySym = new Dictionary<string, List<IBinanceKline>>();
        foreach (var sym in DonchianTrend.Universe)
        {
            var f = Path.Combine("cache", sym + "_15m_71.csv");
            if (!File.Exists(f)) continue;
            var k4 = Aggregate4h(f); k4BySym[sym] = k4;
            for (int i = 120; i < k4.Count; i++)
            {
                // 라이브: GetKlinesAsync(4h,120) 에서 진행봉 제외 → 마지막 마감봉이 i
                var win = k4.GetRange(i - 119, 120);
                int d = DonchianTrend.Signal(win, win.Count - 1);
                if (d == 0) continue;
                liveN++;
                string key = $"{sym}|{(d > 0 ? "L" : "S")}|{k4[i].CloseTime.AddMilliseconds(1):yyyy-MM-dd HH:mm}";
                liveAll.Add(key);
                if (labSig.Contains(key)) matched++; else liveOnly.Add(key);
            }
        }
        // 백테스트는 데이터 시작부터 신호를 내므로, 라이브 창(120봉)이 성립하는 구간만 비교
        var labInRange = lab.Where(x => k4BySym.TryGetValue(x.sym, out var k) && k.Count > 120 && x.tIn > k[119].CloseTime.AddMilliseconds(1)).ToList();
        var labOnlyList = labInRange.Select(x => $"{x.sym}|{(x.isLong ? "L" : "S")}|{x.tIn:yyyy-MM-dd HH:mm}").Where(k => !liveAll.Contains(k)).ToList();
        int labOnly = labOnlyList.Count;
        Console.WriteLine($"[신호] 백테스트 {labInRange.Count}건 · 라이브코드 {liveN}건 · 일치 {matched}건 · 라이브만 {liveOnly.Count}건 · 백테만 {Math.Max(0, labOnly)}건");
        foreach (var s in liveOnly.Take(5)) Console.WriteLine($"   라이브만: {s}");
        foreach (var s in labOnlyList.Take(5)) Console.WriteLine($"   백테만: {s}");

        // 손절가 대조 — 백테스트에서 손절(SL)로 끝난 체결 건: 청산 시점에 라이브가 재계산한 손절가 = 백테 청산가?
        int nSl = 0, exact = 0, gap = 0, bad = 0; double worst = 0;
        foreach (var t in lab.Where(x => x.status == "TAKEN" && x.how == "SL"))
        {
            if (!k4BySym.TryGetValue(t.sym, out var k4)) continue;
            var exitBarOpen = t.tOut.AddMinutes(-15);
            var closed = k4.Where(b => b.CloseTime <= exitBarOpen).ToList();
            if (closed.Count > 500) closed = closed.GetRange(closed.Count - 500, 500);
            var atr = DonchianTrend.Atr(closed);
            decimal stop = DonchianTrend.RecomputeStop(closed, atr, t.tIn, t.isLong, t.px0);
            if (stop <= 0) continue;
            nSl++;
            double rel = (double)Math.Abs(stop - t.px1) / (double)t.px1;
            bool gapExit = t.isLong ? t.px1 < stop : t.px1 > stop;   // 봉 시가가 손절선 너머 → 시가 청산
            if (rel < 0.002) exact++;
            else if (gapExit) gap++;
            else { bad++; worst = Math.Max(worst, rel); if (bad <= 5) Console.WriteLine($"   불일치: {t.sym} {(t.isLong ? "L" : "S")} {t.tIn:MM-dd HH:mm}→{t.tOut:MM-dd HH:mm} 라이브SL {stop} vs 백테청산 {t.px1} ({rel:P2})"); }
        }
        Console.WriteLine($"[손절가] 손절 청산 {nSl}건 · 일치(0.2% 이내) {exact}건 · 갭 청산(시가가 손절선 너머) {gap}건 · 불일치 {bad}건{(bad > 0 ? $" (최대 {worst:P2})" : "")}");
        Console.WriteLine($"\n판정: {(liveOnly.Count == 0 && labOnly <= 0 && bad == 0 ? "★라이브 규칙 = 백테스트 규칙 (신호·손절가 일치)" : "불일치 있음 — 위 목록 확인")}");
    }

    static List<IBinanceKline> Aggregate4h(string file)
    {
        const long W = 4 * 3600_000L;
        var rows = File.ReadAllLines(file).Select(l => l.Split(',')).Where(p => p.Length >= 6).ToList();
        var res = new List<IBinanceKline>();
        int i = 0;
        while (i < rows.Count)
        {
            long t0 = long.Parse(rows[i][0]); long bk = t0 / W; int j = i;
            decimal o = D(rows[i][1]), h = D(rows[i][2]), l = D(rows[i][3]);
            while (j + 1 < rows.Count && long.Parse(rows[j + 1][0]) / W == bk) { j++; h = Math.Max(h, D(rows[j][2])); l = Math.Min(l, D(rows[j][3])); }
            if (j - i + 1 == 16)
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
