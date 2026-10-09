using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.36.0] ★돈치안 추세추종 2슬리브 (사용자 승인 2026-10-09 "1번")
    ///   사용자 기준(2026-10-07~09): 연도 적자 금지 · 큰 손실 방어 → 7년(2019-09~2026-10) 격자탐색(research.py exp8/9/12)으로 채택.
    ///     A = 4h · 100봉 돌파 · 초기손절 2ATR · 4h 종가∓3ATR 트레일 · +5ATR 에서 50% 익절 · 손절 후 7일 재진입금지
    ///     B = 1d · 55봉 돌파  · 초기손절 2ATR · 1d 종가∓5ATR 트레일 · +2ATR 에서 50% 익절 · 손절 후 7일 재진입금지
    ///     공통: 과열추격 제외(EMA50 대비 4.5ATR↑) · 슬리브별 슬롯 메이저2/알트3 · 코인당 1포지션 · 1건 = 사용자 설정 명목의 절반
    ///   검증(실제 펀딩비·동시신호 순서 무작위 20회·코인당 1포지션): 7년 +$37,896 · 적자연도 0.2 · 최대낙폭 −$3,109 · 2026 +$2.8k
    ///     (현행 v5.35: 2026 −$2.8k · 낙폭 −$9.3k). 연속 손실일 기준(≤1일)은 어떤 구조도 미달 — 사용자 고지 후 채택.
    ///   ★레거시 청산 금지 — Owns(symbol) 이면 레거시 감시·브래킷이 붙지 않는다. 거래소엔 SL + (미체결 시) 50% TP 만.
    /// </summary>
    public static class DonchianTrend
    {
        public sealed class Sleeve
        {
            public string Id = "";
            public KlineInterval Interval;
            public int TfMinutes;
            public int Lookback;
            public decimal InitStopAtr = 2m;
            public decimal TrailAtr;
            public decimal PartialTpAtr;       // 진입가 ± x·ATR(신호봉) 에서 50% 익절
            public int CooldownHours = 168;    // 손실 청산 후 같은 코인(이 슬리브) 재진입 금지
            public int MajorSlots = 2, AltSlots = 3;
            public string LongSource => $"LORENTZIAN_DONCHIAN_{Id}";
            public string ShortSource => $"LORENTZIAN_SCALP5M_SHORT_DONCHIAN_{Id}";   // SHORT 화이트리스트 prefix 준수
        }

        public static readonly Sleeve A = new() { Id = "A", Interval = KlineInterval.FourHour, TfMinutes = 240, Lookback = 100, TrailAtr = 3m, PartialTpAtr = 5m };
        public static readonly Sleeve B = new() { Id = "B", Interval = KlineInterval.OneDay, TfMinutes = 1440, Lookback = 55, TrailAtr = 5m, PartialTpAtr = 2m };
        public static readonly Sleeve[] Sleeves = { A, B };
        public static Sleeve? SleeveOf(string? source)
        {
            if (!IsDonchianSource(source)) return null;
            return source!.EndsWith("_B", StringComparison.OrdinalIgnoreCase) ? B : A;   // 접미사 없는 v5.35 구 소스는 A 로 관리
        }

        public const int AtrLen = 14;
        public const decimal MaxChaseAtr = 4.5m;
        public const int EmaLen = 50;
        public const decimal SizeFraction = 0.5m;      // 1건 = 사용자 설정(증거금×레버리지)의 절반 — 검증 구성 "각 $1,500"(UserId10 기준)

        // 백테스트(research.py UNIV)와 동일한 30종목 — 추적풀/시총 순위와 무관하게 고정
        public static readonly string[] Universe = {
            "BTCUSDT","ETHUSDT","XRPUSDT","BNBUSDT","SOLUSDT","DOGEUSDT","ADAUSDT","TRXUSDT","AVAXUSDT","LINKUSDT",
            "DOTUSDT","LTCUSDT","BCHUSDT","NEARUSDT","UNIUSDT","APTUSDT","ICPUSDT","ETCUSDT","FILUSDT","ARBUSDT",
            "OPUSDT","ATOMUSDT","SUIUSDT","AAVEUSDT","XLMUSDT","INJUSDT","ALGOUSDT","HBARUSDT","SEIUSDT","VETUSDT" };

        private static readonly HashSet<string> MajorSet = new(StringComparer.OrdinalIgnoreCase) { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };
        public static bool IsMajor(string symbol) => MajorSet.Contains(symbol);

        public static bool IsDonchianSource(string? source) =>
            !string.IsNullOrEmpty(source) && source.IndexOf("DONCHIAN", StringComparison.OrdinalIgnoreCase) >= 0;

        public sealed class Holding
        {
            public string SleeveId { get; set; } = "A";
            public bool IsLong { get; set; }
            public decimal EntryPrice { get; set; }
            public DateTime EntryUtc { get; set; }
            public decimal InitQty { get; set; }
            public decimal SignalAtr { get; set; }
            public bool HalfDone { get; set; }
            public decimal CurrentStop { get; set; }
            [System.Text.Json.Serialization.JsonIgnore] public bool StopSynced;
            [System.Text.Json.Serialization.JsonIgnore] public int MissingChecks;
            [System.Text.Json.Serialization.JsonIgnore] public bool NoStopAlerted;
            [System.Text.Json.Serialization.JsonIgnore] public Sleeve Sleeve => SleeveId == "B" ? B : A;
            public decimal TpPrice => SignalAtr > 0 ? (IsLong ? EntryPrice + Sleeve.PartialTpAtr * SignalAtr : EntryPrice - Sleeve.PartialTpAtr * SignalAtr) : 0m;
        }

        /// <summary>돈치안이 소유한 포지션. 여기 있으면 레거시 감시·청산 모듈이 손대지 않는다.</summary>
        public static readonly ConcurrentDictionary<string, Holding> Active = new(StringComparer.OrdinalIgnoreCase);
        public static bool Owns(string symbol) => !string.IsNullOrEmpty(symbol) && Active.ContainsKey(symbol);

        /// <summary>손실 청산 후 재진입 금지 기한 (key = "SYM|A").</summary>
        public static readonly ConcurrentDictionary<string, DateTime> CooldownUntil = new(StringComparer.OrdinalIgnoreCase);
        public static bool InCooldown(string symbol, Sleeve s, DateTime signalBarOpenUtc) =>
            CooldownUntil.TryGetValue($"{symbol}|{s.Id}", out var until) && signalBarOpenUtc < until;

        // ── 상태 저장 (재시작 시 50% 익절 여부·처음 수량·쿨다운 복원) ──
        private sealed class StateFile { public Dictionary<string, Holding> Holdings { get; set; } = new(); public Dictionary<string, DateTime> Cooldowns { get; set; } = new(); }
        private static readonly object _fileLock = new();
        public static string StatePath(int userId) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", $"donchian_state_u{userId}.json");
        public static void Save(int userId)
        {
            try
            {
                var st = new StateFile { Holdings = Active.ToDictionary(k => k.Key, v => v.Value), Cooldowns = CooldownUntil.ToDictionary(k => k.Key, v => v.Value) };
                lock (_fileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StatePath(userId))!);
                    File.WriteAllText(StatePath(userId), JsonSerializer.Serialize(st));
                }
            }
            catch { /* 상태 저장 실패는 매매를 막지 않는다 — 다음 저장에서 재시도 */ }
        }
        public static (Dictionary<string, Holding> holdings, Dictionary<string, DateTime> cooldowns) Load(int userId)
        {
            try
            {
                lock (_fileLock)
                {
                    if (!File.Exists(StatePath(userId))) return (new(), new());
                    var st = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath(userId)));
                    return (st?.Holdings ?? new(), st?.Cooldowns ?? new());
                }
            }
            catch { return (new(), new()); }
        }

        /// <summary>마감된 봉만 남긴다(진행 중인 마지막 봉 제거).</summary>
        public static List<IBinanceKline> ClosedOnly(IList<IBinanceKline> k, DateTime nowUtc)
        {
            var r = new List<IBinanceKline>(k.Count);
            foreach (var b in k) if (b.CloseTime <= nowUtc) r.Add(b);
            return r;
        }

        /// <summary>Wilder ATR — 백테스트(research.atr_s)와 같은 식.</summary>
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
        public static int Signal(IList<IBinanceKline> k, int i, int lookback)
        {
            if (i < lookback + 2) return 0;
            decimal hi = decimal.MinValue, lo = decimal.MaxValue, hiP = decimal.MinValue, loP = decimal.MaxValue;
            for (int j = i - lookback; j <= i - 1; j++) { hi = Math.Max(hi, k[j].HighPrice); lo = Math.Min(lo, k[j].LowPrice); }
            for (int j = i - lookback - 1; j <= i - 2; j++) { hiP = Math.Max(hiP, k[j].HighPrice); loP = Math.Min(loP, k[j].LowPrice); }
            if (k[i].ClosePrice > hi && k[i - 1].ClosePrice <= hiP) return 1;
            if (k[i].ClosePrice < lo && k[i - 1].ClosePrice >= loP) return -1;
            return 0;
        }

        /// <summary>EMA — 백테스트(research.ema_s)와 같은 식(첫 값 시드). 창이 길수록 시드 영향 소멸.</summary>
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

        /// <summary>진입 직전 마감봉(신호봉) index — 진입 시각 이전에 마감된 마지막 봉.</summary>
        public static int SignalBarIndex(IList<IBinanceKline> closed, DateTime entryUtc)
        {
            int sig = -1;
            for (int i = 0; i < closed.Count; i++) if (closed[i].CloseTime <= entryUtc) sig = i; else break;
            return sig;
        }

        /// <summary>
        /// 진입 이후 마감된 봉들로 트레일 손절가를 처음부터 다시 계산(재시작 안전).
        ///   초기 = 진입가 ∓ InitStopAtr×ATR(신호봉) · 이후 각 마감봉 종가 ∓ TrailAtr×ATR 중 유리한 값으로만 이동.
        /// </summary>
        public static decimal RecomputeStop(IList<IBinanceKline> closed, decimal[] atr, DateTime entryUtc, bool isLong, decimal entryPrice, Sleeve s)
        {
            int sig = SignalBarIndex(closed, entryUtc);
            if (sig < AtrLen || atr[sig] <= 0) return 0m;
            decimal stop = isLong ? entryPrice - s.InitStopAtr * atr[sig] : entryPrice + s.InitStopAtr * atr[sig];
            for (int i = sig + 1; i < closed.Count; i++)
            {
                if (atr[i] <= 0) continue;
                decimal cand = isLong ? closed[i].ClosePrice - s.TrailAtr * atr[i] : closed[i].ClosePrice + s.TrailAtr * atr[i];
                if (isLong ? cand > stop : cand < stop) stop = cand;
            }
            return stop;
        }
    }
}
