using System;
using System.Collections.Generic;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════
    //  [v5.33.0] 엘리엇 파동 엔진 — 사용자 스펙: 진입 15분봉 · 손익비 1:3(진입가 기준)
    //
    //  RAW LOGIC SHARED BY: TradingEngine.AnalyzeElliottWaveEntryAsync (라이브)
    //                       Tools/LorentzianValidator --elliott / --elliott-final (백테)
    //
    //  구조: ZigZag(ATR14 × 배수) 인과적 피벗 → 엘리엇 규칙 검증 → 돌파 진입.
    //    · 파동2는 파동1 시작점을 하회할 수 없음
    //    · 파동3은 파동1보다 짧을 수 없음
    //    · 파동4는 파동1 가격영역을 침범할 수 없음
    //    · 진입 = 직전 파동 극점 돌파 / 손절 = 되돌림 극점 / 익절 = 진입가 ± 3×손절폭
    //
    //  ※ 피벗은 "확정봉(ConfirmIndex)" 이후에만 사용 — 미래참조 없음.
    //  ※ 3년·30코인·80만 후보 검증 결과 채택 규칙(M): WC(C파) 전체 + 숏&ADX≥25,
    //     공통 필터 = ZZ배수≥5 · 되돌림≤0.5 · 거래량≥1.2× · MACD 방향일치 · 1h EMA20 방향일치.
    //     5폴드 전부 양수(기대 +0.139R · 승률 37.4% · PF 1.23) — 유일하게 구간 안정적.
    // ═══════════════════════════════════════════════════════════════════════════════
    public static class ElliottWaveEngine
    {
        /// <summary>ZigZag 피벗 — Type +1=고점, -1=저점. ConfirmIndex = 이 피벗이 확정된 봉(그 이전엔 알 수 없음).</summary>
        public sealed class Pivot
        {
            public int Index;
            public int ConfirmIndex;
            public double Price;
            public int Type;
        }

        public sealed class Setup
        {
            public string Kind = "";        // "W3"(파동3) · "W5"(파동5) · "WC"(C파)
            public bool IsLong;
            public double TriggerPrice;     // 돌파 진입선 (직전 파동 극점)
            public double StopPrice;        // 손절선 (되돌림 극점)
            public double Retrace;          // 되돌림 비율 (0~1)
            public double WaveLen;          // 기준 파동 길이 (가격)
            public double ZzMult;           // 이 셋업을 만든 ZigZag ATR 배수
            public int ConfirmIndex;        // 되돌림 극점이 확정된 봉
            public int BarsWaited;          // 확정 후 경과 봉수
        }

        /// <summary>Wilder ATR(14) 배열 — ZigZag 임계값 산출용.</summary>
        public static double[] BuildAtr(IList<IBinanceKline> k, int period = 14)
        {
            int n = k.Count;
            var atr = new double[n];
            double a = 0;
            for (int j = 1; j < n; j++)
            {
                double hi = (double)k[j].HighPrice, lo = (double)k[j].LowPrice, pc = (double)k[j - 1].ClosePrice;
                double tr = Math.Max(hi - lo, Math.Max(Math.Abs(hi - pc), Math.Abs(lo - pc)));
                if (j <= period) { a += tr; if (j == period) a /= period; }
                else a = (a * (period - 1) + tr) / period;
                atr[j] = a;
            }
            return atr;
        }

        /// <summary>인과적 ZigZag — 극점에서 (ATR×mult) 만큼 되돌리는 순간 그 봉에서 피벗 확정.</summary>
        public static List<Pivot> ZigZag(IList<IBinanceKline> k, double[] atr, double mult, int start = 20)
        {
            var piv = new List<Pivot>();
            int n = k.Count;
            if (start >= n) return piv;
            int dir = 1;
            double ext = (double)k[start].HighPrice;
            int extIdx = start;
            for (int j = start + 1; j < n; j++)
            {
                double thr = mult * atr[j];
                if (thr <= 0) continue;
                double hi = (double)k[j].HighPrice, lo = (double)k[j].LowPrice;
                if (dir > 0)
                {
                    if (hi >= ext) { ext = hi; extIdx = j; }
                    else if (ext - lo >= thr)
                    { piv.Add(new Pivot { Index = extIdx, ConfirmIndex = j, Price = ext, Type = 1 }); dir = -1; ext = lo; extIdx = j; }
                }
                else
                {
                    if (lo <= ext) { ext = lo; extIdx = j; }
                    else if (hi - ext >= thr)
                    { piv.Add(new Pivot { Index = extIdx, ConfirmIndex = j, Price = ext, Type = -1 }); dir = 1; ext = hi; extIdx = j; }
                }
            }
            return piv;
        }

        /// <summary>
        /// evalIdx(마지막 마감봉) 시점에서 "아직 살아있고 아직 돌파되지 않은" 최신 엘리엇 셋업 탐지.
        /// 우선순위 WC → W5 → W3 (검증 규칙 M이 WC를 주축으로 삼음).
        /// </summary>
        public static Setup? DetectSetup(IList<IBinanceKline> k, double[] atr, double mult, int evalIdx,
                                         double retraceMin = 0.15, double retraceMax = 0.50, int maxWaitBars = 96)
            => DetectSetupFromPivots(k, ZigZag(k, atr, mult), mult, evalIdx, retraceMin, retraceMax, maxWaitBars);

        /// <summary>
        /// 피벗을 외부에서 1회만 계산해 재사용하는 오버로드(백테 재생용). 판정 로직은 DetectSetup 과 완전 동일.
        /// ZigZag 은 전진 인과 패스라 "ConfirmIndex ≤ evalIdx 인 피벗 집합" = "evalIdx 까지의 데이터로 계산한 피벗 집합".
        /// </summary>
        public static Setup? DetectSetupFromPivots(IList<IBinanceKline> k, List<Pivot> piv, double mult, int evalIdx,
                                                   double retraceMin = 0.15, double retraceMax = 0.50, int maxWaitBars = 96)
        {
            if (piv.Count < 4) return null;

            // 되돌림 극점 = 가장 최근 피벗. 확정봉이 평가봉보다 뒤면 아직 모르는 정보.
            Pivot? p2 = null; int p2i = -1;
            for (int i = piv.Count - 1; i >= 0; i--)
            {
                if (piv[i].ConfirmIndex <= evalIdx) { p2 = piv[i]; p2i = i; break; }
            }
            if (p2 == null) return null;
            int waited = evalIdx - p2.ConfirmIndex;
            if (waited < 0 || waited > maxWaitBars) return null;

            bool isLong = p2.Type == -1;                 // 저점에서 끝난 되돌림 → 상방 진행
            double minWavePct = 0.004;                   // 파동1 최소 0.4% (노이즈 제거)

            // 후보 3종 (WC → W5 → W3)
            for (int kind = 0; kind < 3; kind++)
            {
                double trig, stop, waveLen, retr;
                string name;
                if (kind == 0)   // WC — 직전 추세레그의 조정(A-B-C) 중 C파
                {
                    if (p2i < 3) continue;
                    var pm = piv[p2i - 3]; var p0 = piv[p2i - 2]; var p1 = piv[p2i - 1];
                    if (isLong ? !(pm.Type == 1 && p0.Type == -1 && p1.Type == 1)
                               : !(pm.Type == -1 && p0.Type == 1 && p1.Type == -1)) continue;
                    double prior = Math.Abs(p0.Price - pm.Price);
                    waveLen = Math.Abs(p1.Price - p0.Price);
                    if (prior <= 0 || waveLen <= 0 || waveLen >= prior) continue;      // A파는 직전 레그보다 작아야 '조정'
                    if (waveLen / p1.Price < minWavePct) continue;
                    retr = Math.Abs(p1.Price - p2.Price) / waveLen;
                    if (isLong ? p2.Price <= p0.Price : p2.Price >= p0.Price) continue;
                    trig = p1.Price; stop = p2.Price; name = "WC";
                }
                else if (kind == 1)   // W5 — 1·2·3·4 완성 후 파동5
                {
                    if (p2i < 4) continue;
                    var p0 = piv[p2i - 4]; var p1 = piv[p2i - 3]; var pw2 = piv[p2i - 2]; var p3 = piv[p2i - 1];
                    if (isLong ? !(p0.Type == -1 && p1.Type == 1 && pw2.Type == -1 && p3.Type == 1)
                               : !(p0.Type == 1 && p1.Type == -1 && pw2.Type == 1 && p3.Type == -1)) continue;
                    double w1 = Math.Abs(p1.Price - p0.Price), w3 = Math.Abs(p3.Price - pw2.Price);
                    if (w1 <= 0 || w3 <= w1) continue;                                  // ★파동3은 파동1보다 짧을 수 없음
                    if (w1 / p1.Price < minWavePct) continue;
                    if (isLong ? pw2.Price <= p0.Price : pw2.Price >= p0.Price) continue; // ★파동2 미전량되돌림
                    if (isLong ? p2.Price <= p1.Price : p2.Price >= p1.Price) continue;   // ★파동4 비중첩
                    waveLen = w3; retr = Math.Abs(p3.Price - p2.Price) / w3;
                    trig = p3.Price; stop = p2.Price; name = "W5";
                }
                else               // W3 — 파동1 후 파동2 되돌림 완료 → 파동3
                {
                    if (p2i < 2) continue;
                    var p0 = piv[p2i - 2]; var p1 = piv[p2i - 1];
                    if (isLong ? !(p0.Type == -1 && p1.Type == 1)
                               : !(p0.Type == 1 && p1.Type == -1)) continue;
                    waveLen = Math.Abs(p1.Price - p0.Price);
                    if (waveLen <= 0 || waveLen / p1.Price < minWavePct) continue;
                    retr = Math.Abs(p1.Price - p2.Price) / waveLen;
                    if (isLong ? p2.Price <= p0.Price : p2.Price >= p0.Price) continue;   // ★파동2 미전량되돌림
                    trig = p1.Price; stop = p2.Price; name = "W3";
                }

                if (retr < retraceMin || retr > retraceMax) continue;

                // 확정봉 다음 봉부터 평가봉까지: ①손절선 이탈(무효) ②이미 돌파(늦은진입) 둘 다 배제
                bool dead = false, already = false;
                for (int e = p2.ConfirmIndex + 1; e <= evalIdx; e++)
                {
                    double hi = (double)k[e].HighPrice, lo = (double)k[e].LowPrice;
                    if (isLong) { if (lo <= stop) { dead = true; break; } if (hi >= trig) { already = true; break; } }
                    else { if (hi >= stop) { dead = true; break; } if (lo <= trig) { already = true; break; } }
                }
                if (dead || already) continue;

                return new Setup
                {
                    Kind = name, IsLong = isLong, TriggerPrice = trig, StopPrice = stop,
                    Retrace = retr, WaveLen = waveLen, ZzMult = mult,
                    ConfirmIndex = p2.ConfirmIndex, BarsWaited = waited
                };
            }
            return null;
        }
    }
}
