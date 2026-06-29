// 단타 진입 판정 엔진 + 진입계획 상태유지 — TradingCheckBot에서 이식
using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Scalp;

public sealed class ScalpResult
{
    public required string Symbol { get; init; }
    public required string Interval { get; init; }
    public required double Price { get; init; }
    public required DateTime LastTime { get; init; }
    public required ScalpDecision Decision { get; init; }
    public TradeSide Side { get; init; } = TradeSide.Long;
    public required int Quality { get; init; }
    public required string Trigger { get; init; }
    public required List<string> Reasons { get; init; }
    public required double Atr { get; init; }
    public required double Entry { get; init; }
    public required double Target { get; init; }
    public required double Stop { get; init; }
    public string? Warning { get; init; }

    public double RiskReward
    {
        get { double risk = Entry - Stop, reward = Target - Entry; return risk <= 0 ? 0 : reward / risk; }
    }
    public string DecisionText => Decision switch
    {
        ScalpDecision.Enter => Side == TradeSide.Long ? "롱 진입" : "숏 진입",
        ScalpDecision.Wait => "대기",
        _ => "회피"
    };
}

/// <summary>트리거 기반 단타 진입 판정 엔진</summary>
public static class ScalpEngine
{
    public static ScalpResult Evaluate(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        if (candles.Count > 80) candles = candles.Take(candles.Count - 1).ToList();

        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        int last = candles.Count - 1;
        double price = close[last];
        var c0 = candles[last];
        double rng0 = c0.High - c0.Low;
        bool bull0 = c0.Close > c0.Open;

        var ema9 = Ind.Ema(close, 9);
        var ema21 = Ind.Ema(close, 21);
        var ema50 = Ind.Ema(close, 50);
        var rsi = Ind.Rsi(close, 14);
        var stoch = Ind.Stochastic(high, low, close);
        var adxArr = Ind.Adx(high, low, close);
        var atrArr = Ind.Atr(high, low, close);
        double atr = Get(atrArr, last);
        if (double.IsNaN(atr) || atr <= 0) atr = Math.Max(price * 0.002, 1e-9);

        var ema20 = Ind.Ema(close, 20);
        double e9 = Get(ema9, last), e21 = Get(ema21, last), e50 = Get(ema50, last), e20 = Get(ema20, last);
        double e50Prev = Get(ema50, last - 3);

        bool aboveEma20 = !double.IsNaN(e20) && price > e20;
        double extAbove = (!double.IsNaN(e20) && atr > 0) ? (price - e20) / atr : 0;
        bool notExtended = extAbove < 2.5;

        int gcBar = -1;
        for (int i = last; i >= Math.Max(1, last - 20); i--)
        {
            double a = Get(ema20, i), b = Get(ema50, i), ap = Get(ema20, i - 1), bp = Get(ema50, i - 1);
            if (!double.IsNaN(a) && !double.IsNaN(ap) && a > b && ap <= bp) { gcBar = i; break; }
        }
        bool recentGoldenCross = gcBar >= 0 && (last - gcBar) <= 18 && !double.IsNaN(e20) && e20 > e50;
        double r = Get(rsi, last), rPrev = Get(rsi, last - 1);
        double k = Get(stoch.K, last), d = Get(stoch.D, last), kPrev = Get(stoch.K, last - 1), dPrev = Get(stoch.D, last - 1);
        double adx = Get(adxArr, last);

        bool ctxUp = !double.IsNaN(e50) && e9 > e21 && price > e50;
        bool ctxDown = !double.IsNaN(e50) && e9 < e21 && e21 < e50 && price < e50;
        bool e50Rising = !double.IsNaN(e50Prev) && e50 > e50Prev;

        var reasons = new List<string>();
        if (ctxUp) reasons.Add(e50Rising ? "상승추세(EMA정배열·50상승)" : "상승추세(EMA정배열)");

        if (ctxDown)
            return EvaluateShort(symbol, interval, candles, last, price, c0, rng0, bull0, ema20, ema50, rsi, stoch, atr, e20, e50);

        double bearBody = (!bull0 && rng0 > 0) ? (c0.Open - c0.Close) / rng0 : 0;
        bool knife = (!bull0 && bearBody >= 0.55) || (last >= 1 && c0.Close < candles[last - 1].Low);

        double divg = 0;
        int recPk = ArgMaxHigh(high, last - 4, last), prePk = ArgMaxHigh(high, last - 18, last - 6);
        if (recPk >= 0 && prePk >= 0 && high[recPk] > high[prePk])
        {
            double rA = Get(rsi, recPk), rB = Get(rsi, prePk);
            if (!double.IsNaN(rA) && !double.IsNaN(rB) && rA < rB - 2) divg = Math.Clamp((rB - rA) / 12.0, 0, 1);
        }
        double upper0 = c0.High - Math.Max(c0.Open, c0.Close);
        double wick = rng0 > 0 ? Math.Clamp((upper0 / rng0 - 0.45) / 0.4, 0, 1) : 0;
        double roll = (!double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev >= 72 && r < rPrev) ? 1 : 0;
        bool exhaustion = (divg >= 0.4 && (wick >= 0.3 || roll > 0)) || (wick >= 0.6 && r >= 65);

        bool ctxOk = ctxUp || (!ctxDown);
        string trigger = ""; double trigStrength = 0;
        double swingLow = MinLow(low, last - 5, last);
        double rangeLo = MinLow(low, last - 20, last);
        double rangeHi = MaxHigh(high, last - 20, last - 2);

        if (ctxOk && !knife)
        {
            bool recentDip = false;
            for (int i = last - 1; i >= Math.Max(0, last - 4); i--) if (candles[i].Close < candles[i].Open) recentDip = true;
            if (recentGoldenCross && aboveEma20 && notExtended && bull0 && c0.Close > e20 && recentDip && r < 68)
            { trigger = "골든크로스 초입 눌림 진입(EMA20)"; trigStrength = 0.95; reasons.Add($"EMA20>50 골든크로스 {last - gcBar}봉 전 + 눌림 후 양봉"); }

            bool dippedToEma = !double.IsNaN(e21) && MinLow(low, last - 3, last) <= e21 * 1.004;
            if (trigger.Length == 0 && ctxUp && dippedToEma && bull0 && c0.Close > e21)
            { trigger = "눌림목 반등(EMA21 지지)"; trigStrength = 0.9; reasons.Add("EMA21 지지 후 양봉 반등"); }

            if (trigger.Length == 0)
            {
                bool rsiTurn = !double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev <= 38 && r > rPrev && bull0;
                bool stochTurn = !double.IsNaN(k) && !double.IsNaN(d) && kPrev <= dPrev && k > d && k < 35;
                if (rsiTurn || stochTurn)
                {
                    trigger = "과매도 반등"; trigStrength = 0.8;
                    if (rsiTurn) reasons.Add($"RSI 과매도 반등 {rPrev:F0}→{r:F0}");
                    if (stochTurn) reasons.Add("스토캐스틱 과매도 상향교차");
                }
            }
            if (trigger.Length == 0 && !double.IsNaN(rangeHi))
            {
                bool retest = price >= rangeHi * 0.999 && MinLow(low, last - 2, last) <= rangeHi * 1.004 && bull0;
                if (ctxUp && retest) { trigger = "돌파 후 지지 재테스트"; trigStrength = 0.75; reasons.Add("저항 돌파 후 지지 확인"); }
            }
            if (trigger.Length == 0)
            {
                bool nearSupport = c0.Low <= rangeLo * 1.01;
                bool bounce = bull0 && c0.Close > (c0.High + c0.Low) / 2;
                if (nearSupport && bounce) { trigger = "지지선 반등"; trigStrength = 0.6; reasons.Add("박스 저점 지지 반등"); }
            }
        }

        double avgVol = AvgVolume(vol, last, 20);
        bool volOk = avgVol > 0 && vol[last] >= avgVol * 1.2;
        if (volOk) reasons.Add($"거래량 증가({vol[last] / avgVol * 100:F0}%)");

        var bullPatterns = PatternEngine.Detect(candles).Where(p => p.Detected && p.Direction == Bias.Bull).Select(p => p.Name).ToList();
        foreach (var pn in bullPatterns) reasons.Add($"📐{pn}");

        double stop = Math.Min(swingLow, price - atr * 0.8) - atr * 0.1;
        double risk = price - stop;
        double target = price + Math.Max(risk * 1.8, atr * 1.5);
        double rr = risk > 0 ? (target - price) / risk : 0;
        bool rrOk = rr >= 1.3 && risk > atr * 0.15 && risk <= atr * 2.5;

        ScalpDecision decision; string note;
        bool overbought = !double.IsNaN(r) && r >= 72;
        if (ctxDown) { decision = ScalpDecision.Avoid; note = "하락추세 — 롱 회피"; }
        else if (knife) { decision = ScalpDecision.Avoid; note = "급락/저가이탈 진행 — 칼받기 회피"; }
        else if (exhaustion) { decision = ScalpDecision.Avoid; note = "고점 반전 증거 — 회피"; }
        else if (!aboveEma20) { decision = ScalpDecision.Wait; note = "EMA20 아래 — 20일선 회복 대기"; }
        else if (!notExtended || overbought) { decision = ScalpDecision.Wait; note = $"EMA20 +{extAbove:F1}ATR 과확장/과매수 — 추격 금지"; }
        else if (trigger.Length > 0 && rrOk) { decision = ScalpDecision.Enter; note = trigger; }
        else if (trigger.Length > 0) { decision = ScalpDecision.Wait; note = "트리거 있으나 손익비/거리 부적합 — 대기"; }
        else { decision = ScalpDecision.Wait; note = ctxUp ? "상승추세지만 진입 트리거 없음 — 눌림 대기" : "방향 불명확 — 대기"; }

        int quality;
        if (decision == ScalpDecision.Enter)
        {
            double q = 55 + trigStrength * 25;
            if (ctxUp) q += 6;
            if (e50Rising) q += 3;
            if (volOk) q += 6;
            if (bullPatterns.Count > 0) q += 5;
            if (!double.IsNaN(adx) && adx >= 20) q += 3;
            q += Math.Min(6, (rr - 1.3) * 4);
            quality = (int)Math.Clamp(Math.Round(q), 0, 100);
        }
        else if (decision == ScalpDecision.Wait) quality = Math.Clamp(30 + (ctxUp ? 12 : 0) + (e50Rising ? 4 : 0), 0, 55);
        else quality = Math.Max(0, 18 - (knife ? 10 : 0));

        if (reasons.Count == 0) reasons.Add(note);

        int consecDown = 0;
        for (int i = last; i >= 0 && candles[i].Close < candles[i].Open; i--) consecDown++;

        double planEntry;
        if (decision == ScalpDecision.Enter) planEntry = price;
        else if (consecDown >= 3) { planEntry = price; note = $"하락 진행 중({consecDown}연속 음봉) — 진정 후 진입 판단"; }
        else planEntry = (!double.IsNaN(e20) && aboveEma20) ? e20 : price;

        double planStop = Math.Min(swingLow, planEntry - atr * 0.8) - atr * 0.1;
        double planRisk = planEntry - planStop;
        double planTarget = planEntry + Math.Max(planRisk * 1.8, atr * 1.5);

        string? warning = null;
        var (hLabel, hRatio) = Higher(interval);
        if (hRatio > 1 && HigherTrend(candles, interval) == Bias.Bear && decision != ScalpDecision.Avoid)
        { warning = $"⚠ {hLabel} 하락추세 역행 — 반등 짧을 수 있음"; reasons.Add(warning); }

        return new ScalpResult
        {
            Symbol = symbol, Interval = interval, Price = price, LastTime = c0.OpenTime,
            Decision = decision, Quality = quality, Trigger = note, Reasons = reasons,
            Atr = atr, Entry = planEntry, Target = planTarget, Stop = planStop, Warning = warning
        };
    }

    private static ScalpResult EvaluateShort(string symbol, string interval, IReadOnlyList<Candle> candles,
        int last, double price, Candle c0, double rng0, bool bull0,
        double[] ema20, double[] ema50, double[] rsi, Ind.StochResult stoch, double atr, double e20, double e50)
    {
        var close = candles.Select(c => c.Close).ToArray();
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var vol = candles.Select(c => c.Volume).ToArray();
        double r = Get(rsi, last), rPrev = Get(rsi, last - 1);
        double k = Get(stoch.K, last), d = Get(stoch.D, last), kPrev = Get(stoch.K, last - 1), dPrev = Get(stoch.D, last - 1);

        var reasons = new List<string> { "하락추세(EMA 역배열)" };
        bool belowEma20 = !double.IsNaN(e20) && price < e20;
        double extBelow = (!double.IsNaN(e20) && atr > 0) ? (e20 - price) / atr : 0;
        bool notExtended = extBelow < 2.5;
        bool oversold = !double.IsNaN(r) && r <= 28;

        int dcBar = -1;
        for (int i = last; i >= Math.Max(1, last - 20); i--)
        {
            double a = Get(ema20, i), b = Get(ema50, i), ap = Get(ema20, i - 1), bp = Get(ema50, i - 1);
            if (!double.IsNaN(a) && !double.IsNaN(ap) && a < b && ap >= bp) { dcBar = i; break; }
        }
        bool recentDeadCross = dcBar >= 0 && (last - dcBar) <= 18 && !double.IsNaN(e20) && e20 < e50;

        double bullBody = (bull0 && rng0 > 0) ? (c0.Close - c0.Open) / rng0 : 0;
        bool squeeze = (bull0 && bullBody >= 0.55) || (last >= 1 && c0.Close > candles[last - 1].High);

        double bdiv = 0;
        int recTr = ArgMinLow(low, last - 4, last), preTr = ArgMinLow(low, last - 18, last - 6);
        if (recTr >= 0 && preTr >= 0 && low[recTr] < low[preTr])
        {
            double rA = Get(rsi, recTr), rB = Get(rsi, preTr);
            if (!double.IsNaN(rA) && !double.IsNaN(rB) && rA > rB + 2) bdiv = Math.Clamp((rA - rB) / 12.0, 0, 1);
        }
        double lowerWick = rng0 > 0 ? Math.Clamp(((Math.Min(c0.Open, c0.Close) - c0.Low) / rng0 - 0.45) / 0.4, 0, 1) : 0;
        double rollUp = (!double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev <= 28 && r > rPrev) ? 1 : 0;
        bool bottomReversal = (bdiv >= 0.4 && (lowerWick >= 0.3 || rollUp > 0)) || (lowerWick >= 0.6 && r <= 35);

        string trigger = ""; double trigStrength = 0;
        double swingHigh = MaxHigh(high, last - 5, last);
        double rangeHi = MaxHigh(high, last - 20, last);
        double rangeLo = MinLow(low, last - 20, last - 2);

        if (belowEma20 && !squeeze)
        {
            bool recentPop = false;
            for (int i = last - 1; i >= Math.Max(0, last - 4); i--) if (candles[i].Close > candles[i].Open) recentPop = true;
            if (recentDeadCross && notExtended && !bull0 && c0.Close < e20 && recentPop && r > 32)
            { trigger = "데드크로스 초입 반락 숏(EMA20)"; trigStrength = 0.95; reasons.Add($"EMA20<50 데드크로스 {last - dcBar}봉 전 + 되돌림 후 음봉"); }

            if (trigger.Length == 0 && !double.IsNaN(e20) && MaxHigh(high, last - 3, last) >= e20 * 0.996 && !bull0 && c0.Close < e20)
            { trigger = "EMA20 저항 반락 숏"; trigStrength = 0.85; reasons.Add("EMA20 저항 확인 후 음봉"); }

            if (trigger.Length == 0)
            {
                bool rsiTurn = !double.IsNaN(r) && !double.IsNaN(rPrev) && rPrev >= 58 && r < rPrev && !bull0;
                bool stochTurn = !double.IsNaN(k) && !double.IsNaN(d) && kPrev >= dPrev && k < d && k > 65;
                if (rsiTurn || stochTurn) { trigger = "과매수 반락 숏"; trigStrength = 0.8; reasons.Add("되돌림 과매수 반락"); }
            }
            if (trigger.Length == 0 && !double.IsNaN(rangeLo))
            {
                bool retest = price <= rangeLo * 1.001 && MaxHigh(high, last - 2, last) >= rangeLo * 0.996 && !bull0;
                if (retest) { trigger = "지지 붕괴 후 재테스트 숏"; trigStrength = 0.75; reasons.Add("지지 붕괴 후 저항 확인"); }
            }
            if (trigger.Length == 0)
            {
                bool nearRes = c0.High >= rangeHi * 0.99;
                bool reject = !bull0 && c0.Close < (c0.High + c0.Low) / 2;
                if (nearRes && reject) { trigger = "저항선 반락 숏"; trigStrength = 0.6; reasons.Add("박스 고점 저항 반락"); }
            }
        }

        double avgVol = AvgVolume(vol, last, 20);
        bool volOk = avgVol > 0 && vol[last] >= avgVol * 1.2;
        if (volOk) reasons.Add($"거래량 증가({vol[last] / avgVol * 100:F0}%)");
        var bearPatterns = PatternEngine.Detect(candles).Where(p => p.Detected && p.Direction == Bias.Bear).Select(p => p.Name).ToList();
        foreach (var pn in bearPatterns) reasons.Add($"📐{pn}");

        double stop = Math.Max(swingHigh, price + atr * 0.8) + atr * 0.1;
        double risk = stop - price;
        double target = price - Math.Max(risk * 1.8, atr * 1.5);
        double rr = risk > 0 ? (price - target) / risk : 0;
        bool rrOk = rr >= 1.3 && risk > atr * 0.15 && risk <= atr * 2.5;

        ScalpDecision decision; string note;
        if (squeeze) { decision = ScalpDecision.Avoid; note = "급등/신고가 진행 — 숏 회피"; }
        else if (bottomReversal) { decision = ScalpDecision.Avoid; note = "바닥 반등 증거 — 숏 회피"; }
        else if (!belowEma20) { decision = ScalpDecision.Wait; note = "EMA20 위 — 숏은 20일선 아래에서"; }
        else if (!notExtended || oversold) { decision = ScalpDecision.Wait; note = $"EMA20 -{extBelow:F1}ATR 과확장/과매도 — 추격 금지"; }
        else if (trigger.Length > 0 && rrOk) { decision = ScalpDecision.Enter; note = trigger; }
        else if (trigger.Length > 0) { decision = ScalpDecision.Wait; note = "숏 트리거 있으나 손익비 부적합 — 대기"; }
        else { decision = ScalpDecision.Wait; note = "하락추세지만 숏 트리거 없음 — 반등 후 대기"; }

        int quality;
        if (decision == ScalpDecision.Enter)
        {
            double q = 55 + trigStrength * 25;
            if (volOk) q += 6;
            if (bearPatterns.Count > 0) q += 5;
            q += Math.Min(6, (rr - 1.3) * 4);
            quality = (int)Math.Clamp(Math.Round(q), 0, 100);
        }
        else if (decision == ScalpDecision.Wait) quality = 35;
        else quality = Math.Max(0, 18 - (squeeze ? 10 : 0));

        if (reasons.Count == 0) reasons.Add(note);

        double planEntry = decision == ScalpDecision.Enter ? price : (!double.IsNaN(e20) && belowEma20 ? e20 : price);
        double planStop = Math.Max(swingHigh, planEntry + atr * 0.8) + atr * 0.1;
        double planRisk = planStop - planEntry;
        double planTarget = planEntry - Math.Max(planRisk * 1.8, atr * 1.5);

        string? warning = null;
        var (hLabel, hRatio) = Higher(interval);
        if (hRatio > 1 && HigherTrend(candles, interval) == Bias.Bull && decision != ScalpDecision.Avoid)
        { warning = $"⚠ {hLabel} 상승추세 역행 — 반락 짧을 수 있음"; reasons.Add(warning); }

        return new ScalpResult
        {
            Symbol = symbol, Interval = interval, Price = price, LastTime = c0.OpenTime,
            Decision = decision, Side = TradeSide.Short, Quality = quality, Trigger = note, Reasons = reasons,
            Atr = atr, Entry = planEntry, Target = planTarget, Stop = planStop, Warning = warning
        };
    }

    private static (string label, int ratio) Higher(string interval) => interval switch
    {
        "1m" => ("15m", 15), "3m" => ("1h", 20), "5m" => ("1h", 12), "15m" => ("1h", 4),
        "1h" => ("4h", 4), "4h" => ("1d", 6), _ => ("", 1)
    };

    public static Bias HigherTrend(IReadOnlyList<Candle> candles, string interval)
    {
        int ratio = Higher(interval).ratio;
        if (ratio <= 1) return Bias.Neutral;
        int n = candles.Count;
        var hc = new List<double>();
        for (int end = n; end > 0; end -= ratio) hc.Add(candles[end - 1].Close);
        hc.Reverse();
        if (hc.Count < 25) return Bias.Neutral;
        var ema20 = Ind.Ema(hc, 20);
        int last = hc.Count - 1;
        double e = ema20[last], ep = ema20[Math.Max(0, last - 2)];
        if (double.IsNaN(e) || double.IsNaN(ep)) return Bias.Neutral;
        double p = hc[last];
        if (p > e && e >= ep) return Bias.Bull;
        if (p < e && e <= ep) return Bias.Bear;
        return Bias.Neutral;
    }

    private static double Get(double[] arr, int i) => i >= 0 && i < arr.Length ? arr[i] : double.NaN;
    private static int ArgMinLow(double[] low, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(low.Length - 1, to);
        if (from > to) return -1;
        int idx = from; double mn = low[from];
        for (int i = from + 1; i <= to; i++) if (low[i] < mn) { mn = low[i]; idx = i; }
        return idx;
    }
    private static double AvgVolume(double[] vol, int last, int period)
    {
        int start = Math.Max(0, last - period); int cnt = last - start;
        if (cnt <= 0) return 0;
        double sum = 0; for (int i = start; i < last; i++) sum += vol[i];
        return sum / cnt;
    }
    private static int ArgMaxHigh(double[] high, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(high.Length - 1, to);
        if (from > to) return -1;
        int idx = from; double mx = high[from];
        for (int i = from + 1; i <= to; i++) if (high[i] > mx) { mx = high[i]; idx = i; }
        return idx;
    }
    private static double MaxHigh(double[] high, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(high.Length - 1, to);
        if (from > to) return double.NaN;
        double mx = double.MinValue; for (int i = from; i <= to; i++) mx = Math.Max(mx, high[i]);
        return mx;
    }
    private static double MinLow(double[] low, int from, int to)
    {
        from = Math.Max(0, from); to = Math.Min(low.Length - 1, to);
        if (from > to) return double.NaN;
        double mn = double.MaxValue; for (int i = from; i <= to; i++) mn = Math.Min(mn, low[i]);
        return mn;
    }
}

/// <summary>진입 계획을 상태로 유지 (진입대기→체결→익절/손절/무효)</summary>
public static class PlanManager
{
    private const int MaxWaitBars = 12;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, Plan> _book = new();

    private sealed class Plan
    {
        public required TradeSide Side;
        public required double Entry;
        public required double Target;
        public required double Stop;
        public required DateTime IssuedTime;
        public required bool ImmediateEntry;
        public required int Quality;
        public required string Trigger;
        public required List<string> Reasons;
    }

    public static ScalpResult Evaluate(string symbol, string interval, IReadOnlyList<Candle> candles)
    {
        var s = ScalpEngine.Evaluate(symbol, interval, candles);
        int lastClosed = candles.Count - 2;
        if (lastClosed < 1) return s;
        string key = symbol + "|" + interval;
        lock (_lock)
        {
            if (_book.TryGetValue(key, out var p))
            {
                var view = Simulate(p, candles, lastClosed, s, out bool terminal);
                if (terminal) _book.Remove(key);
                return view;
            }
            var np = TryCreate(s, candles[lastClosed].OpenTime);
            if (np != null) { _book[key] = np; return Simulate(np, candles, lastClosed, s, out _); }
            return s;
        }
    }

    private static Plan? TryCreate(ScalpResult s, DateTime issueTime)
    {
        if (s.Decision == ScalpDecision.Enter)
            return new Plan { Side = s.Side, Entry = s.Entry, Target = s.Target, Stop = s.Stop, IssuedTime = issueTime, ImmediateEntry = true, Quality = s.Quality, Trigger = s.Trigger, Reasons = s.Reasons };
        if (s.Decision == ScalpDecision.Wait)
        {
            bool concrete = (s.Side == TradeSide.Long && s.Entry < s.Price * 0.999) || (s.Side == TradeSide.Short && s.Entry > s.Price * 1.001);
            if (concrete)
                return new Plan { Side = s.Side, Entry = s.Entry, Target = s.Target, Stop = s.Stop, IssuedTime = issueTime, ImmediateEntry = false, Quality = s.Quality, Trigger = s.Trigger, Reasons = s.Reasons };
        }
        return null;
    }

    private static ScalpResult Simulate(Plan p, IReadOnlyList<Candle> candles, int lastClosed, ScalpResult s, out bool terminal)
    {
        int issueIdx = -1;
        for (int i = lastClosed; i >= Math.Max(0, lastClosed - 60); i--)
            if (candles[i].OpenTime == p.IssuedTime) { issueIdx = i; break; }
        if (issueIdx < 0) issueIdx = lastClosed;

        bool isLong = p.Side == TradeSide.Long;
        string state = p.ImmediateEntry ? "Entered" : "Waiting";
        int barsWaited = 0;
        for (int i = issueIdx + 1; i <= lastClosed; i++)
        {
            var c = candles[i];
            if (state == "Waiting")
            {
                barsWaited++;
                if (isLong ? c.Low <= p.Stop : c.High >= p.Stop) { state = "Invalid"; break; }
                if (isLong ? c.Low <= p.Entry : c.High >= p.Entry) { state = "Entered"; continue; }
                if (barsWaited > MaxWaitBars) { state = "Expired"; break; }
            }
            else if (state == "Entered")
            {
                if (isLong ? c.High >= p.Target : c.Low <= p.Target) { state = "TargetHit"; break; }
                if (isLong ? c.Low <= p.Stop : c.High >= p.Stop) { state = "StoppedOut"; break; }
            }
        }
        if (state == "Waiting" && (lastClosed - issueIdx) > MaxWaitBars) state = "Expired";
        terminal = state is "TargetHit" or "StoppedOut" or "Invalid" or "Expired";

        ScalpDecision decision; string note; var reasons = new List<string>(p.Reasons);
        string sideTxt = isLong ? "롱" : "숏";
        switch (state)
        {
            case "Waiting": decision = ScalpDecision.Wait; note = $"{sideTxt} 진입대기 @ {Fmt(p.Entry)} (대기 {lastClosed - issueIdx}/{MaxWaitBars}봉)"; break;
            case "Entered": decision = ScalpDecision.Enter; note = $"{sideTxt} 진입 체결 @ {Fmt(p.Entry)} — 목표/손절 유지"; break;
            case "TargetHit": decision = ScalpDecision.Wait; note = $"🎯 목표 도달 — 익절 구간 ({Fmt(p.Target)})"; reasons.Insert(0, "계획 완료: 목표 도달"); break;
            case "StoppedOut": decision = ScalpDecision.Avoid; note = $"손절 도달 — 계획 종료 ({Fmt(p.Stop)})"; reasons.Insert(0, "계획 종료: 손절"); break;
            case "Invalid": decision = ScalpDecision.Avoid; note = "진입 전 손절선 이탈 — 무효"; break;
            default: decision = ScalpDecision.Wait; note = "진입가 미도달 — 대기 만료(무효)"; break;
        }

        return new ScalpResult
        {
            Symbol = s.Symbol, Interval = s.Interval, Price = s.Price, LastTime = s.LastTime,
            Decision = decision, Side = p.Side, Quality = p.Quality, Trigger = note, Reasons = reasons,
            Atr = s.Atr, Entry = p.Entry, Target = p.Target, Stop = p.Stop, Warning = s.Warning
        };
    }

    private static string Fmt(double v) => v >= 1000 ? v.ToString("N1") : v >= 1 ? v.ToString("N3") : v.ToString("0.######");
}
