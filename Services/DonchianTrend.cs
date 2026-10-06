using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.35.0] ★돈치안 추세추종 단일 진입 (사용자 승인 2026-10-06 "1번으로 해")
    ///   4h 종가가 직전 55봉 최고가를 처음 돌파 → 롱 / 최저가를 처음 이탈 → 숏.
    ///   초기손절 = 진입가 ∓ 2×ATR(14,4h) · 청산 = 4h 종가 ∓ 5×ATR 트레일(유리한 쪽으로만) · 익절 없음.
    ///   검증(Tools/LorentzianValidator --lab, 명목 $3,000·슬롯 메이저2/알트3·비용 0.12%+펀딩):
    ///     미사용 기간 2019-09~2023-09 +$58,810 · 2023-09~2026-10 +$26,332 · 이웃 파라미터 36/36 흑자.
    ///   ★이 규칙은 승자를 끝까지 들고 가야 성립한다 — 부분익절·본절·반전캔들·EMA컷 등 레거시 청산 금지.
    ///     레거시 감시 모듈은 Owns(symbol) 이면 붙지 않는다(TradingEngine/PositionMonitorService 가드).
    /// </summary>
    public static class DonchianTrend
    {
        public const int Lookback = 55;
        public const int AtrLen = 14;
        public const decimal InitStopAtr = 2m;
        public const decimal TrailAtr = 5m;
        // [v5.35.2] 과열 추격 제외 — 돌파 시점 종가가 EMA50(4h) 에서 진입방향으로 4.5×ATR 이상 떨어져 있으면 진입 안 함.
        //   손실 해부(두 기간 공통): EMA50+4~6ATR 추격 진입이 적자. 이웃 4/4.5/5 전부 개선, 3/3.5 는 정상 돌파까지 잘라 악화.
        //   2023~26 +$26,332→+$30,727 · 2019~23 +$56,023→+$59,143 (1h데이터 +$58,810→+$60,691).
        public const decimal MaxChaseAtr = 4.5m;
        public const int EmaLen = 50;
        public static readonly TimeSpan BarSpan = TimeSpan.FromHours(4);

        public const string LongSource = "LORENTZIAN_DONCHIAN";
        public const string ShortSource = "LORENTZIAN_SCALP5M_SHORT_DONCHIAN";   // SHORT 화이트리스트 prefix 준수

        // 백테스트(StrategyLab.Universe)와 동일한 30종목 — 추적풀/시총 순위와 무관하게 고정
        public static readonly string[] Universe = {
            "BTCUSDT","ETHUSDT","XRPUSDT","BNBUSDT","SOLUSDT","DOGEUSDT","ADAUSDT","TRXUSDT","AVAXUSDT","LINKUSDT",
            "DOTUSDT","LTCUSDT","BCHUSDT","NEARUSDT","UNIUSDT","APTUSDT","ICPUSDT","ETCUSDT","FILUSDT","ARBUSDT",
            "OPUSDT","ATOMUSDT","SUIUSDT","AAVEUSDT","XLMUSDT","INJUSDT","ALGOUSDT","HBARUSDT","SEIUSDT","VETUSDT" };

        // 메이저 = BTC/ETH/SOL/XRP 4개만 (사용자 규칙 · 백테스트 슬롯 분류와 동일)
        private static readonly HashSet<string> MajorSet = new(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        public static bool IsMajor(string symbol) => MajorSet.Contains(symbol);

        public static bool IsDonchianSource(string? source) =>
            !string.IsNullOrEmpty(source) && source.IndexOf("DONCHIAN", StringComparison.OrdinalIgnoreCase) >= 0;

        public sealed class Holding
        {
            public bool IsLong;
            public decimal EntryPrice;
            public DateTime EntryUtc;
            public decimal CurrentStop;      // 거래소에 걸린(걸어야 할) 손절가 — 유리한 쪽으로만 이동
            public bool StopSynced;          // 이번 세션에서 거래소 SL 을 이 값으로 등록했는가
            public int MissingChecks;        // 메모리 포지션 부재 연속 횟수(정리용)
            public bool NoStopAlerted;       // 손절가 계산 불가 경고 1회만
        }

        /// <summary>돈치안이 소유한 포지션. 여기 있으면 레거시 감시·청산 모듈이 손대지 않는다.</summary>
        public static readonly ConcurrentDictionary<string, Holding> Active = new(StringComparer.OrdinalIgnoreCase);
        public static bool Owns(string symbol) => !string.IsNullOrEmpty(symbol) && Active.ContainsKey(symbol);

        /// <summary>마감된 봉만 남긴다(진행 중인 마지막 봉 제거).</summary>
        public static List<IBinanceKline> ClosedOnly(IList<IBinanceKline> k, DateTime nowUtc)
        {
            var r = new List<IBinanceKline>(k.Count);
            foreach (var b in k) if (b.CloseTime <= nowUtc) r.Add(b);
            return r;
        }

        /// <summary>Wilder ATR — 백테스트(StrategyLab.Atr)와 같은 식.</summary>
        public static decimal[] Atr(IList<IBinanceKline> k)
        {
            var r = new decimal[k.Count]; decimal a = 0;
            for (int i = 1; i < k.Count; i++)
            {
                decimal tr = Math.Max(k[i].HighPrice - k[i].LowPrice,
                             Math.Max(Math.Abs(k[i].HighPrice - k[i - 1].ClosePrice), Math.Abs(k[i].LowPrice - k[i - 1].ClosePrice)));
                a = i <= AtrLen ? a + tr / AtrLen : (a * (AtrLen - 1) + tr) / AtrLen;
                r[i] = i >= AtrLen ? a : 0;
            }
            return r;
        }

        /// <summary>마감봉 i 에서의 신호: +1 신규 상단돌파 · -1 신규 하단이탈 · 0 없음.</summary>
        public static int Signal(IList<IBinanceKline> k, int i)
        {
            if (i < Lookback + 2) return 0;
            decimal hi = decimal.MinValue, lo = decimal.MaxValue, hiP = decimal.MinValue, loP = decimal.MaxValue;
            for (int j = i - Lookback; j <= i - 1; j++) { hi = Math.Max(hi, k[j].HighPrice); lo = Math.Min(lo, k[j].LowPrice); }
            for (int j = i - Lookback - 1; j <= i - 2; j++) { hiP = Math.Max(hiP, k[j].HighPrice); loP = Math.Min(loP, k[j].LowPrice); }
            if (k[i].ClosePrice > hi && k[i - 1].ClosePrice <= hiP) return 1;
            if (k[i].ClosePrice < lo && k[i - 1].ClosePrice >= loP) return -1;
            return 0;
        }

        /// <summary>EMA(50) — 백테스트(StrategyLab.Ema)와 같은 식(첫 값 시드). 창이 길수록(500봉) 시드 영향 소멸.</summary>
        public static decimal[] Ema(IList<IBinanceKline> k, int p)
        {
            var r = new decimal[k.Count]; decimal a = 2m / (p + 1);
            for (int i = 0; i < k.Count; i++) r[i] = i == 0 ? k[0].ClosePrice : a * k[i].ClosePrice + (1 - a) * r[i - 1];
            return r;
        }

        /// <summary>과열 추격이면 true — 진입방향 기준 (종가 − EMA50)/ATR ≥ 4.5.</summary>
        public static bool IsChasing(IList<IBinanceKline> k, int i, decimal[] atr, decimal[] ema50, int dir, out decimal ext)
        {
            ext = atr[i] > 0 ? dir * (k[i].ClosePrice - ema50[i]) / atr[i] : 0m;
            return ext >= MaxChaseAtr;
        }

        /// <summary>
        /// 진입 이후 마감된 4h 봉들로 트레일 손절가를 처음부터 다시 계산한다(재시작 안전 — 상태 저장 불필요).
        ///   초기 = 진입가 ∓ 2×ATR(진입 직전 마감봉) · 이후 각 마감봉 종가 ∓ 5×ATR 중 유리한 값으로만 이동.
        /// </summary>
        public static decimal RecomputeStop(IList<IBinanceKline> closed, decimal[] atr, DateTime entryUtc, bool isLong, decimal entryPrice)
        {
            int sig = -1;
            for (int i = 0; i < closed.Count; i++) if (closed[i].CloseTime <= entryUtc) sig = i; else break;
            if (sig < AtrLen || atr[sig] <= 0) return 0m;
            decimal stop = isLong ? entryPrice - InitStopAtr * atr[sig] : entryPrice + InitStopAtr * atr[sig];
            for (int i = sig + 1; i < closed.Count; i++)
            {
                if (atr[i] <= 0) continue;
                decimal cand = isLong ? closed[i].ClosePrice - TrailAtr * atr[i] : closed[i].ClosePrice + TrailAtr * atr[i];
                if (isLong ? cand > stop : cand < stop) stop = cand;
            }
            return stop;
        }
    }
}
