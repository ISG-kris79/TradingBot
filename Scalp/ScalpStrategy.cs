// 단타 진입 전략 엔진 — CoinFF TradingCheckBot에서 이식 (namespace TradingBot.Scalp 로 격리)
// 트리거 기반 진입/대기/회피 + 진입계획 상태유지(PlanManager) + 상위TF 역추세 경고
using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Scalp;

public sealed record Candle(DateTime OpenTime, double Open, double High, double Low, double Close, double Volume);

public enum Bias { Bull = 1, Neutral = 0, Bear = -1 }
public enum TradeSide { Long, Short }
public enum ScalpDecision { Enter, Wait, Avoid }

/// <summary>보조지표 계산 (필요한 것만 이식)</summary>
public static class Ind
{
    public static double[] Ema(IReadOnlyList<double> src, int period)
    {
        var outp = new double[src.Count];
        double k = 2.0 / (period + 1);
        double ema = 0; bool seeded = false; double seedSum = 0;
        for (int i = 0; i < src.Count; i++)
        {
            if (!seeded)
            {
                seedSum += src[i];
                if (i == period - 1) { ema = seedSum / period; seeded = true; outp[i] = ema; }
                else outp[i] = double.NaN;
            }
            else { ema = src[i] * k + ema * (1 - k); outp[i] = ema; }
        }
        return outp;
    }

    public static double[] Rsi(IReadOnlyList<double> close, int period = 14)
    {
        int n = close.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        if (n <= period) return outp;
        double gain = 0, loss = 0;
        for (int i = 1; i <= period; i++) { double ch = close[i] - close[i - 1]; if (ch >= 0) gain += ch; else loss -= ch; }
        double avgGain = gain / period, avgLoss = loss / period;
        outp[period] = Rs(avgGain, avgLoss);
        for (int i = period + 1; i < n; i++)
        {
            double ch = close[i] - close[i - 1];
            double g = ch > 0 ? ch : 0, l = ch < 0 ? -ch : 0;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
            outp[i] = Rs(avgGain, avgLoss);
        }
        return outp;
        static double Rs(double ag, double al) { if (al == 0) return 100; double rs = ag / al; return 100 - 100 / (1 + rs); }
    }

    public sealed record StochResult(double[] K, double[] D);

    public static StochResult Stochastic(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close,
        int kPeriod = 14, int kSmooth = 3, int dPeriod = 3)
    {
        int n = close.Count;
        var rawK = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (i < kPeriod - 1) { rawK[i] = double.NaN; continue; }
            double hh = double.MinValue, ll = double.MaxValue;
            for (int j = i - kPeriod + 1; j <= i; j++) { if (high[j] > hh) hh = high[j]; if (low[j] < ll) ll = low[j]; }
            double range = hh - ll;
            rawK[i] = range == 0 ? 50 : (close[i] - ll) / range * 100;
        }
        var k = SmaSkipNaN(rawK, kSmooth);
        var d = SmaSkipNaN(k, dPeriod);
        return new StochResult(k, d);
    }

    public static double[] Adx(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close, int period = 14)
    {
        int n = close.Count;
        var adx = new double[n];
        for (int i = 0; i < n; i++) adx[i] = double.NaN;
        if (n <= period * 2) return adx;
        var tr = new double[n]; var plusDm = new double[n]; var minusDm = new double[n];
        for (int i = 1; i < n; i++)
        {
            double upMove = high[i] - high[i - 1], downMove = low[i - 1] - low[i];
            plusDm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
            minusDm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
            double hl = high[i] - low[i], hc = Math.Abs(high[i] - close[i - 1]), lc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }
        double trS = 0, pS = 0, mS = 0;
        for (int i = 1; i <= period; i++) { trS += tr[i]; pS += plusDm[i]; mS += minusDm[i]; }
        var dx = new double[n];
        for (int i = 0; i < n; i++) dx[i] = double.NaN;
        for (int i = period + 1; i < n; i++)
        {
            trS = trS - trS / period + tr[i]; pS = pS - pS / period + plusDm[i]; mS = mS - mS / period + minusDm[i];
            double plusDi = trS == 0 ? 0 : 100 * pS / trS, minusDi = trS == 0 ? 0 : 100 * mS / trS;
            double sum = plusDi + minusDi;
            dx[i] = sum == 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / sum;
        }
        int start = period + 1; double adxVal = 0; int cnt = 0;
        for (int i = start; i < start + period && i < n; i++) { adxVal += dx[i]; cnt++; }
        if (cnt == 0) return adx;
        adxVal /= cnt;
        int adxStart = start + period - 1;
        if (adxStart < n) adx[adxStart] = adxVal;
        for (int i = adxStart + 1; i < n; i++) { adxVal = (adxVal * (period - 1) + dx[i]) / period; adx[i] = adxVal; }
        return adx;
    }

    public static double[] Atr(IReadOnlyList<double> high, IReadOnlyList<double> low, IReadOnlyList<double> close, int period = 14)
    {
        int n = close.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        if (n <= period) return outp;
        var tr = new double[n];
        tr[0] = high[0] - low[0];
        for (int i = 1; i < n; i++)
        {
            double hl = high[i] - low[i], hc = Math.Abs(high[i] - close[i - 1]), lc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }
        double sum = 0;
        for (int i = 1; i <= period; i++) sum += tr[i];
        double atr = sum / period;
        outp[period] = atr;
        for (int i = period + 1; i < n; i++) { atr = (atr * (period - 1) + tr[i]) / period; outp[i] = atr; }
        return outp;
    }

    private static double[] SmaSkipNaN(IReadOnlyList<double> src, int period)
    {
        int n = src.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        var buf = new Queue<double>(); double sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(src[i])) continue;
            buf.Enqueue(src[i]); sum += src[i];
            if (buf.Count > period) sum -= buf.Dequeue();
            if (buf.Count == period) outp[i] = sum / period;
        }
        return outp;
    }
}

public readonly record struct Swing(int Index, double Price, bool IsHigh);

public sealed class PatternResult
{
    public required string Name { get; init; }
    public required bool Detected { get; init; }
    public required Bias Direction { get; init; }
    public required string Detail { get; init; }
    public required double Confidence { get; init; }
}

/// <summary>차트 패턴 인식 (이중바닥/천장·헤드앤숄더·삼각수렴·V반등·피보나치·엘리엇)</summary>
public static class PatternEngine
{
    public static List<Swing> FindSwings(IReadOnlyList<double> high, IReadOnlyList<double> low, double pct = 0.02)
    {
        int n = high.Count;
        var swings = new List<Swing>();
        if (n < 3) return swings;
        int dir = 0; double extreme = (high[0] + low[0]) / 2.0; int extremeIdx = 0;
        for (int i = 1; i < n; i++)
        {
            if (dir >= 0)
            {
                if (high[i] > extreme) { extreme = high[i]; extremeIdx = i; }
                if (low[i] <= extreme * (1 - pct)) { swings.Add(new Swing(extremeIdx, extreme, true)); dir = -1; extreme = low[i]; extremeIdx = i; }
            }
            if (dir <= 0)
            {
                if (low[i] < extreme) { extreme = low[i]; extremeIdx = i; }
                if (high[i] >= extreme * (1 + pct)) { swings.Add(new Swing(extremeIdx, extreme, false)); dir = 1; extreme = high[i]; extremeIdx = i; }
            }
        }
        swings.Add(new Swing(extremeIdx, extreme, dir >= 0));
        return Clean(swings);
    }

    private static List<Swing> Clean(List<Swing> s)
    {
        var outp = new List<Swing>();
        foreach (var sw in s)
        {
            if (outp.Count > 0 && outp[^1].IsHigh == sw.IsHigh)
            {
                var prev = outp[^1];
                bool replace = sw.IsHigh ? sw.Price > prev.Price : sw.Price < prev.Price;
                if (replace) outp[^1] = sw;
            }
            else outp.Add(sw);
        }
        return outp;
    }

    public static List<PatternResult> Detect(IReadOnlyList<Candle> candles)
    {
        var results = new List<PatternResult>();
        int n = candles.Count;
        if (n < 20) return results;
        var high = candles.Select(c => c.High).ToArray();
        var low = candles.Select(c => c.Low).ToArray();
        var close = candles.Select(c => c.Close).ToArray();
        double price = close[n - 1];
        double atr = Avg(TrueRanges(high, low, close), 14);
        double pct = Math.Clamp(atr / price * 1.5, 0.008, 0.05);
        var sw = FindSwings(high, low, pct);
        results.Add(DoubleBottom(sw, price, atr));
        results.Add(DoubleTop(sw, price, atr));
        results.Add(InverseHeadShoulders(sw, price, atr));
        results.Add(HeadShoulders(sw, price, atr));
        results.Add(Triangle(sw, price));
        results.Add(VBounce(candles, atr));
        results.Add(Fibonacci(sw, price));
        results.Add(ElliottWave(sw));
        return results;
    }

    private static PatternResult DoubleBottom(List<Swing> sw, double price, double atr)
    {
        const string name = "이중바닥(W)";
        if (sw.Count >= 3)
            for (int i = sw.Count - 1; i >= 2; i--)
            {
                var low2 = sw[i]; var mid = sw[i - 1]; var low1 = sw[i - 2];
                if (!low2.IsHigh && mid.IsHigh && !low1.IsHigh)
                {
                    bool similarLows = Math.Abs(low2.Price - low1.Price) < atr * 1.5;
                    bool neckline = mid.Price > low1.Price + atr;
                    bool breaking = price >= mid.Price * 0.998;
                    if (similarLows && neckline)
                        return Hit(name, Bias.Bull, breaking ? 0.8 : 0.5, breaking ? "두 저점 후 넥라인 돌파 — 상승 반전" : "두 저점 형성 — 넥라인 돌파 시 상승");
                }
            }
        return Miss(name);
    }

    private static PatternResult DoubleTop(List<Swing> sw, double price, double atr)
    {
        const string name = "이중천장(M)";
        if (sw.Count >= 3)
            for (int i = sw.Count - 1; i >= 2; i--)
            {
                var high2 = sw[i]; var mid = sw[i - 1]; var high1 = sw[i - 2];
                if (high2.IsHigh && !mid.IsHigh && high1.IsHigh)
                {
                    bool similar = Math.Abs(high2.Price - high1.Price) < atr * 1.5;
                    bool valley = mid.Price < high1.Price - atr;
                    bool breaking = price <= mid.Price * 1.002;
                    if (similar && valley)
                        return Hit(name, Bias.Bear, breaking ? 0.8 : 0.5, breaking ? "두 고점 후 넥라인 이탈 — 하락 반전" : "두 고점 형성 — 넥라인 이탈 시 하락");
                }
            }
        return Miss(name);
    }

    private static PatternResult InverseHeadShoulders(List<Swing> sw, double price, double atr)
    {
        const string name = "역헤드앤숄더";
        if (sw.Count >= 5)
            for (int i = sw.Count - 1; i >= 4; i--)
            {
                var rs = sw[i]; var h2 = sw[i - 1]; var head = sw[i - 2]; var h1 = sw[i - 3]; var ls = sw[i - 4];
                if (!rs.IsHigh && h2.IsHigh && !head.IsHigh && h1.IsHigh && !ls.IsHigh)
                {
                    bool headLowest = head.Price < ls.Price - atr * 0.3 && head.Price < rs.Price - atr * 0.3;
                    bool shouldersSimilar = Math.Abs(ls.Price - rs.Price) < atr * 2.0;
                    double neck = Math.Max(h1.Price, h2.Price);
                    bool breaking = price >= neck * 0.997;
                    if (headLowest && shouldersSimilar)
                        return Hit(name, Bias.Bull, breaking ? 0.85 : 0.55, breaking ? "넥라인 돌파 — 강한 상승 반전" : "역H&S 형성 — 넥라인 돌파 대기");
                }
            }
        return Miss(name);
    }

    private static PatternResult HeadShoulders(List<Swing> sw, double price, double atr)
    {
        const string name = "헤드앤숄더";
        if (sw.Count >= 5)
            for (int i = sw.Count - 1; i >= 4; i--)
            {
                var rs = sw[i]; var l2 = sw[i - 1]; var head = sw[i - 2]; var l1 = sw[i - 3]; var ls = sw[i - 4];
                if (rs.IsHigh && !l2.IsHigh && head.IsHigh && !l1.IsHigh && ls.IsHigh)
                {
                    bool headHighest = head.Price > ls.Price + atr * 0.3 && head.Price > rs.Price + atr * 0.3;
                    bool shouldersSimilar = Math.Abs(ls.Price - rs.Price) < atr * 2.0;
                    double neck = Math.Min(l1.Price, l2.Price);
                    bool breaking = price <= neck * 1.003;
                    if (headHighest && shouldersSimilar)
                        return Hit(name, Bias.Bear, breaking ? 0.85 : 0.55, breaking ? "넥라인 이탈 — 강한 하락 반전" : "H&S 형성 — 넥라인 이탈 대기");
                }
            }
        return Miss(name);
    }

    private static PatternResult Triangle(List<Swing> sw, double price)
    {
        const string name = "삼각수렴";
        if (sw.Count >= 4)
        {
            var highs = sw.Where(s => s.IsHigh).TakeLast(3).ToList();
            var lows = sw.Where(s => !s.IsHigh).TakeLast(3).ToList();
            if (highs.Count >= 2 && lows.Count >= 2)
            {
                double highSlope = highs[^1].Price - highs[0].Price, lowSlope = lows[^1].Price - lows[0].Price;
                double hTol = highs[0].Price * 0.004, lTol = lows[0].Price * 0.004;
                bool flatHighs = Math.Abs(highSlope) < hTol, risingLows = lowSlope > lTol;
                bool fallingHighs = highSlope < -hTol, flatLows = Math.Abs(lowSlope) < lTol;
                if (flatHighs && risingLows) return Hit(name, Bias.Bull, 0.6, "상승삼각형 — 저점 상승, 상단 돌파 시 상승");
                if (fallingHighs && flatLows) return Hit(name, Bias.Bear, 0.6, "하락삼각형 — 고점 하락, 하단 이탈 시 하락");
                if (fallingHighs && risingLows) return Hit(name, Bias.Neutral, 0.4, "대칭삼각형 — 변동성 수축");
            }
        }
        return Miss(name);
    }

    private static PatternResult VBounce(IReadOnlyList<Candle> c, double atr)
    {
        const string name = "V자 반등";
        int n = c.Count;
        if (n >= 10)
        {
            int k = 5;
            double dropStart = c[n - 1 - 2 * k].High;
            double bottom = c[n - 1 - k].Low;
            for (int i = n - 1 - 2 * k; i <= n - 1 - k; i++) bottom = Math.Min(bottom, c[i].Low);
            double now = c[n - 1].Close;
            double drop = dropStart - bottom, rebound = now - bottom;
            bool sharpDrop = drop > atr * 3, sharpRebound = rebound > drop * 0.5;
            bool bullishNow = c[n - 1].Close > c[n - 1].Open && c[n - 2].Close > c[n - 2].Open;
            if (sharpDrop && sharpRebound && bullishNow) return Hit(name, Bias.Bull, 0.65, "급락 후 급반등 — V자 회복");
        }
        return Miss(name);
    }

    private static PatternResult Fibonacci(List<Swing> sw, double price)
    {
        const string name = "피보나치 되돌림";
        if (sw.Count >= 2)
        {
            var last = sw[^1]; var prev = sw[^2];
            double a = prev.Price, b = last.Price;
            if (!prev.IsHigh && last.IsHigh)
            {
                double range = b - a;
                if (range > 0)
                {
                    double r382 = b - range * 0.382, r618 = b - range * 0.618;
                    if (price <= r382 && price >= r618 * 0.995) return Hit(name, Bias.Bull, 0.6, "상승파동 0.382~0.618 되돌림 지지 — 반등 매수 구간");
                }
            }
            else if (prev.IsHigh && !last.IsHigh)
            {
                double range = a - b;
                if (range > 0)
                {
                    double r382 = b + range * 0.382, r618 = b + range * 0.618;
                    if (price >= r382 && price <= r618 * 1.005) return Hit(name, Bias.Bear, 0.55, "하락파동 0.382~0.618 되돌림 저항 — 반락 주의");
                }
            }
        }
        return Miss(name);
    }

    private static PatternResult ElliottWave(List<Swing> sw)
    {
        const string name = "엘리엇 파동";
        if (sw.Count >= 6)
        {
            var s = sw.TakeLast(6).ToList();
            if (!s[0].IsHigh && s[1].IsHigh && !s[2].IsHigh && s[3].IsHigh && !s[4].IsHigh && s[5].IsHigh)
            {
                bool higherHighs = s[3].Price > s[1].Price && s[5].Price > s[3].Price;
                bool higherLows = s[2].Price > s[0].Price && s[4].Price > s[2].Price;
                if (higherHighs && higherLows) return Hit(name, Bias.Bull, 0.55, "상승 5파 임펄스 — 추세 상승");
            }
            if (s[0].IsHigh && !s[1].IsHigh && s[2].IsHigh && !s[3].IsHigh && s[4].IsHigh && !s[5].IsHigh)
            {
                bool lowerLows = s[3].Price < s[1].Price && s[5].Price < s[3].Price;
                bool lowerHighs = s[2].Price < s[0].Price && s[4].Price < s[2].Price;
                if (lowerLows && lowerHighs) return Hit(name, Bias.Bear, 0.55, "하락 5파 — 추세 하락");
            }
        }
        return Miss(name);
    }

    private static PatternResult Hit(string name, Bias dir, double conf, string detail) =>
        new() { Name = name, Detected = true, Direction = dir, Confidence = conf, Detail = detail };
    private static PatternResult Miss(string name) =>
        new() { Name = name, Detected = false, Direction = Bias.Neutral, Confidence = 0, Detail = "미형성" };

    private static double[] TrueRanges(double[] h, double[] l, double[] c)
    {
        int n = c.Length; var tr = new double[n];
        tr[0] = h[0] - l[0];
        for (int i = 1; i < n; i++) tr[i] = Math.Max(h[i] - l[i], Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
        return tr;
    }
    private static double Avg(double[] v, int period)
    {
        int start = Math.Max(0, v.Length - period); double sum = 0; int cnt = 0;
        for (int i = start; i < v.Length; i++) { sum += v[i]; cnt++; }
        return cnt == 0 ? 0 : sum / cnt;
    }
}
