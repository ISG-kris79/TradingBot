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
    // 수익금 시뮬: 마진 $100 × 레버리지 10x = notional $1000, 수수료 0.04% 양방향
    private const decimal MARGIN_USD = 100m;
    private const decimal LEVERAGE   = 10m;
    private const decimal FEE_RATE   = 0.0004m;
    private static decimal Notional => MARGIN_USD * LEVERAGE;
    private static decimal RoundTripFee => Notional * FEE_RATE * 2m;
    private static decimal TpProfit => Notional * TP_PCT / 100m - RoundTripFee;
    private static decimal SlLoss   => Notional * SL_PCT / 100m + RoundTripFee;

    private static async Task<List<IBinanceKline>> FetchKlinesAsync(string sym)
    {
        var all = new List<List<IBinanceKline>>();
        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int p = 0; p < PAGES; p++)
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
