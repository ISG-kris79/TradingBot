// ─────────────────────────────────────────────────────────────────────────
// LiveSim.cs  [v5.23.77]
//   백테스트 신뢰 복구 — "실용 충실본(pragmatic faithful)" 단일 시뮬레이터.
//
//   배경: Program.cs 의 ~30개 모드는 제각각 진입·청산을 재구현해 충실도가 달랐다.
//   특히 --bb-expand/--final 은 (1)고정 35알트 무차별, (2)전량 고정 TP/SL,
//   (3)돌파봉 종가 즉시체결(룩어헤드), (4)게이트 부재, (5)슬리피지 누락 으로
//   실거래와 64%p 까지 벌어졌다(BB_WALK 백테 91.5% vs 실거래 27.6%).
//
//   LiveSim 은 그 5대 괴리를 닫는다:
//     1) 진입가 = 트리거 봉 *마감 후 다음 봉 시가* + 슬리피지 (룩어헤드 제거)
//     2) 수수료(0.04%×2) + 슬리피지(0.05%×2) 전 구간 반영
//     3) 청산 = 실거래 다단 모사: 본절 시프트 + TP1 부분익절 + ATR/고점 트레일링 + 전량 SL
//        (PositionMonitorService.cs:330-381 등급별 파라미터 그대로)
//     4) 게이트 = 계산 가능한 것 이식(RSI 낙하나이프 + 당일상승 동적풀 근사)
//     5) 진입 트리거 = 충실 평가기(LiveMajorEvaluator/LiveAltEvaluator/Lorentzian KNN)
//
//   ※ 한계(반드시 인지): 실거래 청산은 ~1,000줄 상태기(Fractal/V-Stop/듀얼스탑/
//      회복모드/공격형배수)라 100% 재현 불가. LiveSim 은 "지배적 경로"의 근사다.
//      => 배포 단독 기준으로 쓰지 말 것. 최종 판정은 라이브 카나리(실거래 N건).
// ─────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Interfaces;
using Skender.Stock.Indicators;
using TradingBot.Services.LorentzianV2;

namespace TradingBot.Tools.LorentzianValidator
{
    /// <summary>등급별 실거래 청산 파라미터 (ROE% 단위, PositionMonitorService.cs 그대로).</summary>
    public readonly struct ExitTier
    {
        public readonly decimal BreakEvenRoe;   // 본절 이동 발동 ROE
        public readonly decimal Tp1Roe;         // 1차 부분익절 ROE
        public readonly decimal Tp1ClosePct;    // 1차에 청산하는 비중 (0~1)
        public readonly decimal TrailStartRoe;  // 트레일링 시작 ROE
        public readonly decimal TrailGapRoe;    // 트레일링 간격 ROE (고점 대비)
        public readonly decimal StopLossRoe;    // 최종 손절 ROE
        public readonly decimal Tp1SafetyRoe;   // TP1 후 스탑을 올리는 ROE
        public ExitTier(decimal be, decimal tp1, decimal tp1Pct, decimal trailStart, decimal trailGap, decimal sl, decimal tp1Safety)
        { BreakEvenRoe = be; Tp1Roe = tp1; Tp1ClosePct = tp1Pct; TrailStartRoe = trailStart; TrailGapRoe = trailGap; StopLossRoe = sl; Tp1SafetyRoe = tp1Safety; }
    }

    public struct LiveTradeResult
    {
        public bool Entered;
        public decimal PnlUsd;
        public bool Win;
        public int BarsHeld;
        public int ExitBarIndex;
        public string ExitReason;
    }

    public static class LiveSim
    {
        // 전 구간 비용 (실거래 estimatedRoundTripCostPct=0.0013 와 정합)
        public const decimal FeeRate = 0.0004m;       // 0.04% taker, 편도
        public const decimal SlippagePct = 0.0005m;   // 0.05% 편도

        public static readonly string[] Majors = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT" };
        private static readonly string[] Atr20Majors = { "ETHUSDT", "XRPUSDT", "SOLUSDT" };

        public static ExitTier TierFor(string sym)
        {
            // PositionMonitorService.cs:330-353 (Tp1ClosePct = 0.40: 주석 328 "ROE40% → 40% 청산")
            if (sym == "BTCUSDT")             return new ExitTier(15m, 30m, 0.40m, 40m, 20m, 50m, 5m);
            if (Atr20Majors.Contains(sym))    return new ExitTier(20m, 40m, 0.40m, 60m, 30m, 50m, 5m);
            return new ExitTier(20m, 40m, 0.40m, 50m, 30m, 50m, 5m); // 알트/기타 (BNB·전체 알트)
        }

        /// <summary>ROE% → 가격 변동율 (priceMove = roe / lev / 100).</summary>
        private static decimal Px(decimal roe, decimal lev) => roe / lev / 100m;

        /// <summary>ATR(14) at index (Wilder 근사 — 직전 14봉 True Range 평균).</summary>
        private static decimal Atr14(List<IBinanceKline> kl, int idx)
        {
            int start = Math.Max(1, idx - 14);
            decimal sum = 0m; int cnt = 0;
            for (int j = start; j <= idx && j < kl.Count; j++)
            {
                decimal tr = Math.Max(kl[j].HighPrice - kl[j].LowPrice,
                              Math.Max(Math.Abs(kl[j].HighPrice - kl[j - 1].ClosePrice),
                                       Math.Abs(kl[j].LowPrice - kl[j - 1].ClosePrice)));
                sum += tr; cnt++;
            }
            return cnt > 0 ? sum / cnt : 0m;
        }

        // 실거래 하이브리드 듀얼스탑(ATR+Fractal+V-Stop) 근사: 진입 직후부터 ATR 배수 트레일.
        //   실거래 LORENTZIAN 평균손절 -$10 / 평균보유 54분에 맞춘 보정값. (2.5×ATR)
        public const decimal AtrStopMult = 2.5m;

        // ───────────────────────────────────────────────────────────────────
        // 청산 시뮬 — 실거래 다단 경로 근사 (LONG 전용. 봇은 LONG 전용)
        //   진입가 = kl[entryIdx].OpenPrice (트리거 봉 마감 후 다음 봉 시가) × (1+슬립)
        //   봉내 순서는 *비관적*: 매 봉 저가(스탑) 먼저 검사 후 고가(승급) — 과대평가 방지
        // ───────────────────────────────────────────────────────────────────
        public static LiveTradeResult SimulateExit(
            List<IBinanceKline> kl, int entryIdx, decimal margin, decimal lev, ExitTier t, int maxBars = 288)
        {
            var r = new LiveTradeResult { Entered = false, ExitReason = "NO_FILL" };
            if (entryIdx < 1 || entryIdx >= kl.Count) return r;

            decimal notional = margin * lev;
            decimal entryFill = kl[entryIdx].OpenPrice * (1m + SlippagePct);
            if (entryFill <= 0) return r;
            r.Entered = true;

            decimal slPx       = entryFill * (1m - Px(t.StopLossRoe, lev));
            decimal bePx       = entryFill * (1m + 0.0014m);                 // 본절+버퍼 (수수료+슬립 0.13% 흡수)
            decimal tp1Px      = entryFill * (1m + Px(t.Tp1Roe, lev));
            decimal tp1SafePx  = entryFill * (1m + Px(t.Tp1SafetyRoe, lev));
            decimal beTrigPx   = entryFill * (1m + Px(t.BreakEvenRoe, lev));
            decimal trailTrigPx= entryFill * (1m + Px(t.TrailStartRoe, lev));
            decimal gapFrac    = Px(t.TrailGapRoe, lev);

            // 실거래 ATR 듀얼스탑 근사 — 진입 직후부터 작동 (지는 거래 조기 절단)
            decimal atr = Atr14(kl, entryIdx - 1);
            decimal atrGap = atr * AtrStopMult;
            decimal stop = Math.Max(slPx, entryFill - atrGap);  // 둘 중 타이트한(높은) 쪽
            decimal hiWater = entryFill;
            bool beDone = false, tp1Done = false, trailOn = false;
            decimal weightedReturn = 0m;   // Σ 비중 × (청산가/진입가 - 1)
            decimal remaining = 1.0m;
            string reason = "WINDOW_END";
            int last = Math.Min(kl.Count - 1, entryIdx + maxBars);
            int j = entryIdx;

            for (; j <= last; j++)
            {
                decimal hi = kl[j].HighPrice, lo = kl[j].LowPrice;

                // 1) 비관적: 스탑/SL 먼저 (저가가 현재 스탑 이하면 잔량 청산)
                if (lo <= stop)
                {
                    decimal exitFill = stop * (1m - SlippagePct);
                    weightedReturn += remaining * (exitFill / entryFill - 1m);
                    remaining = 0m;
                    reason = tp1Done ? (trailOn ? "TRAIL_STOP" : "BE_STOP") : (beDone ? "BE_STOP" : "SL");
                    break;
                }

                // 2) 승급 (고가 기준)
                hiWater = Math.Max(hiWater, hi);

                if (!tp1Done && hi >= tp1Px)
                {
                    decimal exitFill = tp1Px * (1m - SlippagePct);
                    weightedReturn += t.Tp1ClosePct * (exitFill / entryFill - 1m);
                    remaining -= t.Tp1ClosePct;
                    tp1Done = true; beDone = true;
                    stop = Math.Max(stop, tp1SafePx);   // 잔량 스탑 +SafetyRoe
                }
                else if (!beDone && hi >= beTrigPx)
                {
                    beDone = true;
                    stop = Math.Max(stop, bePx);        // 본절 이동
                }

                // ATR 트레일 (진입부터 상시) — 고점 대비 2.5×ATR
                decimal atrStop = hiWater - atrGap;
                if (atrStop > stop) stop = atrStop;

                // ROE 기반 타이트 트레일 (고ROE 도달 후 추가)
                if (hi >= trailTrigPx) trailOn = true;
                if (trailOn)
                {
                    decimal trailStop = hiWater * (1m - gapFrac);
                    if (trailStop > stop) stop = trailStop;
                }

                if (remaining <= 0.0001m) { reason = "TP_ALL"; break; }
            }

            if (remaining > 0.0001m)
            {
                // 윈도우 종료 — 마지막 종가로 잔량 청산
                decimal exitFill = kl[Math.Min(j, last)].ClosePrice * (1m - SlippagePct);
                weightedReturn += remaining * (exitFill / entryFill - 1m);
            }

            decimal pnl = notional * weightedReturn - notional * FeeRate * 2m;
            r.PnlUsd = pnl;
            r.Win = pnl > 0m;
            r.BarsHeld = Math.Min(j, last) - entryIdx;
            r.ExitBarIndex = Math.Min(j, last);
            r.ExitReason = reason;
            return r;
        }

        // ───────────────────────────────────────────────────────────────────
        // 게이트 근사 (계산 가능분만). 미모사: BTC_1h하락추세, 시총Top30, 추적풀 점수랭킹.
        // ───────────────────────────────────────────────────────────────────

        /// <summary>당일 상승 동적풀 근사 — 직전 24h(288×5m) 수익률>0 인 알트만 진입.
        /// 실거래는 "당일 펌핑 Top30 점수상위"만 추적 → 고점추격 편향을 방향만 재현.</summary>
        public static bool DailyUpFilter(List<IBinanceKline> kl, int i)
        {
            if (i < 288) return kl[i].ClosePrice >= kl[0].ClosePrice;
            return kl[i].ClosePrice > kl[i - 288].ClosePrice;
        }

        /// <summary>ALT_RSI_FALLING_KNIFE 근사 — 5m RSI(14) &lt; 50 이면 차단.</summary>
        public static bool RsiFallingKnife(List<IBinanceKline> kl, int i)
            => LiveMajorEvaluator.Rsi(kl, i, 14) < 50.0;

        /// <summary>SQUEEZE 트리거 (production v5.23.76: BBW&lt;1.5% + 종가>상단, BB_WALK 폐지).
        /// 가드 EMA20상승 + RSI&lt;65 는 LiveAltEvaluator 와 동일하게 자체 검사.</summary>
        public static bool SqueezeTrigger(List<IBinanceKline> kl, int i)
        {
            if (i < 26) return false;
            decimal alpha = 2m / 21m;
            decimal ema = kl[i - 25].ClosePrice;
            for (int j = i - 24; j <= i; j++) ema = kl[j].ClosePrice * alpha + ema * (1 - alpha);
            int from5 = Math.Max(0, i - 30);
            decimal e5 = kl[from5].ClosePrice;
            for (int j = from5 + 1; j <= i - 5; j++) e5 = kl[j].ClosePrice * alpha + e5 * (1 - alpha);
            if (ema <= e5) return false;                       // EMA20 상승
            if (LiveMajorEvaluator.Rsi(kl, i, 14) >= 65) return false; // RSI<65
            var bb = LiveMajorEvaluator.Bb(kl, i, 20, 2);
            if (bb.Mid <= 0) return false;
            decimal widthPct = ((decimal)bb.Upper - (decimal)bb.Lower) / (decimal)bb.Mid * 100m;
            return widthPct < 1.5m && kl[i].ClosePrice > (decimal)bb.Upper;
        }

        // ───────────────────────────────────────────────────────────────────
        // [v5.23.79] 진입조건 탐색용 — "큰 수익 끝까지" 하이브리드 청산 + 지표 헬퍼
        // ───────────────────────────────────────────────────────────────────
        public static decimal AtrPub(List<IBinanceKline> kl, int idx) => Atr14(kl, idx);

        public static double Adx(List<IBinanceKline> kl, int upTo, int period = 14)
        {
            if (upTo + 1 < period * 3) return 0;
            var q = LiveMajorEvaluator.ToQuotesWindow(kl, upTo, period * 4);
            return q.GetAdx(period).LastOrDefault()?.Adx ?? 0;
        }

        public struct RunnerResult
        {
            public bool Entered;
            public bool HitTp1;     // 승 정의 = 부분익절 TP1 도달(이익 확정)
            public decimal RetPct;  // 가격수익률 가중합 (비용 차감), 마진 대비 ROE = ×leverage
            public int ExitIdx;
            public int BarsHeld;
        }

        /// <summary>"큰 수익 끝까지" 하이브리드 청산: 구조적 SL + 부분TP1(승확정) + 잔량 넓은 ATR 추적.
        ///   진입가=다음봉 시가+슬립. 봉내 비관적(저가 먼저). TP1 후 본절스탑→3×ATR 샹들리에로 잔량 런.
        ///   HitTp1=true ⟹ 잔량 스탑이 본절 이상이라 손실 불가 → 승=이익확정.</summary>
        public static RunnerResult SimulateRunner(List<IBinanceKline> kl, int entryIdx,
            decimal slAtr, decimal tp1Atr, decimal tp1Pct, decimal trailAtr, int maxBars = 288)
        {
            var r = new RunnerResult();
            if (entryIdx < 1 || entryIdx >= kl.Count) return r;
            decimal atr = Atr14(kl, entryIdx - 1);
            decimal entry = kl[entryIdx].OpenPrice * (1m + SlippagePct);
            if (atr <= 0m || entry <= 0m) return r;
            r.Entered = true;

            decimal stop = entry - slAtr * atr;
            decimal tp1 = entry + tp1Atr * atr;
            decimal hi = entry, ret = 0m, rem = 1.0m;
            bool tp1done = false;
            int last = Math.Min(kl.Count - 1, entryIdx + maxBars);
            int j = entryIdx;
            for (; j <= last; j++)
            {
                decimal h = kl[j].HighPrice, lo = kl[j].LowPrice;
                if (lo <= stop) { ret += rem * (stop * (1m - SlippagePct) / entry - 1m); rem = 0m; break; }  // 비관적: 스탑 먼저
                if (h > hi) hi = h;
                if (!tp1done && h >= tp1) { ret += tp1Pct * (tp1 * (1m - SlippagePct) / entry - 1m); rem -= tp1Pct; tp1done = true; if (entry > stop) stop = entry; }
                if (tp1done) { decimal ts = hi - trailAtr * atr; if (ts > stop) stop = ts; }
                if (rem <= 0.0001m) break;
            }
            if (rem > 0.0001m) ret += rem * (kl[Math.Min(j, last)].ClosePrice * (1m - SlippagePct) / entry - 1m);
            ret -= FeeRate * 2m;
            r.HitTp1 = tp1done;
            r.RetPct = ret;
            r.ExitIdx = Math.Min(j, last);
            r.BarsHeld = r.ExitIdx - entryIdx;
            return r;
        }
    }
}
