using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.17.0 REDESIGN] "15-5-1" 급등/추세 진입 엔진
    ///
    /// 설계 철학:
    ///   밈코인/알트 20배 단타의 표준 구조 — 타임프레임별 역할 분리
    ///
    ///   Layer 1 (15분봉, 필터): "시장의 판을 읽음"
    ///     - 15m EMA(50) 위/아래 → 상승장/하락장 판독
    ///     - 하락장에서 LONG 차단 (가짜 신호 제거)
    ///
    ///   Layer 2 (5분봉, 전략): "진입 대기 자리 찾기"
    ///     - [v5.23.73] MACD 골든크로스 = 필수 1차 트리거 (없으면 진입 없음)
    ///     - RSI = 보조: 과매수(ceiling 이상)면 거부, 상승이면 확인 가산 (단독 진입 불가)
    ///     - EMA 12/26 정배열 = 보조: 단기 추세 동조 확인 가산
    ///     - MACD 골든크로스 + 보조확인(RSI상승/EMA정배열) 최소 1개 → "진입 대기" 등록
    ///
    ///   Layer 3 (1분봉, 실행): "정확한 방아쇠"
    ///     - "진입 대기" 심볼 중 1m 첫 양봉 + 볼륨 spike 시 즉시 시장가
    ///     - Major 1.3x volume, PUMP 알트 1.5x volume
    ///
    /// 만료 조건:
    ///   - 15분 경과 시 1m trigger 없으면 자동 취소
    ///   - 15m 필터가 재평가에서 isUptrend=false 전환 시 즉시 취소
    ///   - 5m 재평가에서 신호 소멸 시 취소
    /// </summary>
    public class FifteenFiveOneEngine
    {
        public event Action<string>? OnLog;
        public event Action<EntryTrigger>? OnEntryFire;

        /// <summary>Layer 1 결과 캐시 (심볼별 15분 체크 주기)</summary>
        private readonly ConcurrentDictionary<string, RegimeSnapshot> _regime = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Layer 2 결과 — 진입 대기 중인 심볼 (1m trigger 대기)</summary>
        private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Layer 3 체결된 직후 쿨다운 (중복 방지)</summary>
        private readonly ConcurrentDictionary<string, DateTime> _recentTriggers = new(StringComparer.OrdinalIgnoreCase);

        // ═══════════════════════════════════════════════════════════════
        // 설정값 — 모두 ctor 주입 (하드코딩 금지)
        // ═══════════════════════════════════════════════════════════════
        private readonly int _emaPeriod15m;
        private readonly int _rsiPeriod5m;
        private readonly decimal _rsiBounceFloor;          // RSI 이 아래에서 반등 시작 판정
        private readonly decimal _rsiEntryCeiling;         // 현재 RSI 이 값 이상이면 과매수 거부
        private readonly decimal _majorVolSpikeMultiplier; // Major 1m 거래량 spike 배수
        private readonly decimal _altVolSpikeMultiplier;   // PUMP 알트 1m 거래량 spike 배수
        private readonly TimeSpan _pendingExpiry;          // 진입 대기 만료 시간
        private readonly TimeSpan _triggerCooldown;        // 체결 후 재진입 쿨다운
        private readonly int _minSignalsRequired;          // 5m strategy 최소 일치 신호 수 (기본 2)

        public FifteenFiveOneEngine(
            int emaPeriod15m = 50,
            int rsiPeriod5m = 14,
            decimal rsiBounceFloor = 40m,
            decimal rsiEntryCeiling = 72m,
            decimal majorVolSpikeMultiplier = 1.3m,
            decimal altVolSpikeMultiplier = 1.5m,
            TimeSpan? pendingExpiry = null,
            TimeSpan? triggerCooldown = null,
            int minSignalsRequired = 2)
        {
            _emaPeriod15m = emaPeriod15m;
            _rsiPeriod5m = rsiPeriod5m;
            _rsiBounceFloor = rsiBounceFloor;
            _rsiEntryCeiling = rsiEntryCeiling;
            _majorVolSpikeMultiplier = majorVolSpikeMultiplier;
            _altVolSpikeMultiplier = altVolSpikeMultiplier;
            _pendingExpiry = pendingExpiry ?? TimeSpan.FromMinutes(15);
            _triggerCooldown = triggerCooldown ?? TimeSpan.FromMinutes(10);
            _minSignalsRequired = minSignalsRequired;
        }

        // ═══════════════════════════════════════════════════════════════
        // LAYER 1: 1h FILTER (사용자 원칙: 방향 1h, 속도 5/15m)
        // ═══════════════════════════════════════════════════════════════
        /// <summary>
        /// 1시간봉 EMA(20) 필터 — 상승장 여부 판독. 1h 종가 확정 시 호출.
        /// [v5.23.48] 15m EMA50 → 1h EMA20 변경 (사용자 원칙: "방향은 1시간봉")
        /// </summary>
        public bool EvaluateRegime(string symbol, IReadOnlyList<IBinanceKline> candles1h)
        {
            if (candles1h == null || candles1h.Count < 21)
            {
                _regime[symbol] = new RegimeSnapshot(false, double.NaN, double.NaN, DateTime.Now, "insufficient_1h_data");
                return false;
            }

            double ema20 = IndicatorCalculator.CalculateEMA(candles1h.ToList(), 20);
            double close = (double)candles1h[^1].ClosePrice;
            bool isUptrend = close > ema20;

            _regime[symbol] = new RegimeSnapshot(isUptrend, close, ema20, DateTime.Now, isUptrend ? "1h_uptrend" : "1h_downtrend");

            // 하락장 전환 시 진입 대기 자동 취소
            if (!isUptrend && _pending.TryRemove(symbol, out var cancelled))
            {
                OnLog?.Invoke($"🛑 [L1][{symbol}] 1h EMA20 하락 전환 → 진입 대기 취소 (등록={cancelled.RegisteredAt:HH:mm:ss})");
            }
            return isUptrend;
        }

        public bool IsUptrend(string symbol) =>
            _regime.TryGetValue(symbol, out var r) && r.IsUptrend && (DateTime.Now - r.EvaluatedAt).TotalMinutes < 90;   // 1h 기준 90분 캐시

        // ═══════════════════════════════════════════════════════════════
        // LAYER 2: 5m STRATEGY
        // ═══════════════════════════════════════════════════════════════
        /// <summary>5분봉 종가 확정 시 호출 — 3개 지표 중 minSignalsRequired 이상 일치 시 진입 대기 등록.</summary>
        public bool TryGenerateSignal(string symbol, IReadOnlyList<IBinanceKline> candles5m, out PendingEntry? pending)
        {
            pending = null;
            if (!IsUptrend(symbol))
            {
                return false; // 15m 필터 미통과
            }
            if (candles5m == null || candles5m.Count < 30)
            {
                return false;
            }

            // 쿨다운 체크
            if (_recentTriggers.TryGetValue(symbol, out var lastTrigger) && DateTime.Now - lastTrigger < _triggerCooldown)
            {
                return false;
            }

            var candleList = candles5m.ToList();
            var closes = candleList.Select(c => (double)c.ClosePrice).ToList();

            // [v5.23.73] RSI 단독 진입 폐지 — 사용자 원칙: "RSI로 진입하지 마. RSI는 보조 개념. 진입은 MACD 골든크로스로."
            //   기존: MACD/RSI/EMA 3개 중 2개 → 진입 (RSI 반등 + EMA 정배열 만으로도 MACD 없이 진입 = 고점 추격 손실 주범)
            //         실거래 손실 데이터: ENGINE_151/LORENTZIAN 손절이 RSI 70~91 고점 진입에 집중 (ZEC RSI91, BEAT RSI86.8)
            //   변경: MACD 골든크로스 = 필수 1차 트리거. RSI는 보조(① 과매수면 거부 ② 상승이면 확인 가산)로만, 단독 진입 불가.
            var hits = new List<string>();
            float strength = 0f;
            int confirms = 0;

            // ── [1차 트리거 — 필수] MACD 골든크로스 (최근 2봉 내 hist 음→양 전환)
            var (macdSeries, signalSeries, _) = IndicatorCalculator.CalculateMACDSeries(closes);
            int n = macdSeries.Count;
            bool macdGolden = false;
            if (n >= 3)
            {
                double hist0 = macdSeries[n - 2] - signalSeries[n - 2];
                double hist1 = macdSeries[n - 1] - signalSeries[n - 1];
                macdGolden = hist0 <= 0 && hist1 > 0;
            }
            if (!macdGolden)
            {
                // MACD 골든크로스 없으면 진입 근거 없음 (RSI/EMA 단독 진입 폐지)
                if (_pending.TryRemove(symbol, out var prevNoMacd))
                    OnLog?.Invoke($"🛑 [L2][{symbol}] MACD 골든크로스 없음 → 대기 취소 (RSI/EMA 단독 진입 폐지)");
                return false;
            }
            hits.Add("MACD_golden_cross");
            strength += 0.50f;

            // ── [보조] RSI — 단독 진입 불가, 차단도 안 함. 상승 중이면 확인 가산만 (순수 보조).
            //   [v5.23.73] RSI≥ceiling 과매수 거부 가드 제거 — 백테스트(--diagnose PnL, production TP1%/SL3%)
            //     결과 고RSI/BB상단이 오히려 최고 흑자 구간이었음 (상단 0.8-1.0 +$42, 밴드돌파 +$147 /
            //     하단·중단은 전부 적자, 1:3 구조 손익분기 WR 75%를 상단만 돌파). 고RSI 차단 = 수익 구간 제거 역효과.
            var rsiSeries = IndicatorCalculator.CalculateRSISeries(closes, _rsiPeriod5m);
            if (rsiSeries.Count >= 2)
            {
                double rsiPrev = rsiSeries[^2];
                double rsiNow = rsiSeries[^1];
                if (rsiNow > rsiPrev)
                {
                    hits.Add($"RSI_confirm({rsiNow:F0})");
                    strength += 0.15f;
                    confirms++;
                }
            }

            // ── [보조] EMA 12/26 정배열 (단기 추세 동조 — 확인 가산)
            double ema12 = IndicatorCalculator.CalculateEMA(candleList, 12);
            double ema26 = IndicatorCalculator.CalculateEMA(candleList, 26);
            if (ema12 > ema26)
            {
                hits.Add("EMA_aligned");
                strength += 0.25f;
                confirms++;
            }

            // 필수: MACD 골든크로스(통과) + 보조 확인 최소 (_minSignalsRequired-1)개 (RSI 상승 또는 EMA 정배열)
            int confirmsNeeded = Math.Max(1, _minSignalsRequired - 1);
            if (confirms < confirmsNeeded)
            {
                if (_pending.TryRemove(symbol, out var prev))
                    OnLog?.Invoke($"🛑 [L2][{symbol}] MACD 골든크로스이나 보조확인 {confirms}/{confirmsNeeded} 부족 → 대기 취소");
                return false;
            }

            var reg = _regime[symbol];
            var newPending = new PendingEntry(
                Symbol: symbol,
                Direction: "LONG",
                RegisteredAt: DateTime.Now,
                SignalPrice: candleList[^1].ClosePrice,
                Strength: strength,
                Reason: string.Join("+", hits),
                RegimeClose: (decimal)reg.Close1h,
                RegimeEma50: (decimal)reg.Ema20_1h
            );
            _pending[symbol] = newPending;
            pending = newPending;
            OnLog?.Invoke($"🎯 [L2][{symbol}] 진입 대기 등록 | MACD골든크로스+보조{confirms} [{string.Join(",", hits)}] strength={strength:F2}");
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        // LAYER 3: 1m EXECUTION
        // ═══════════════════════════════════════════════════════════════
        /// <summary>1분봉 tick/종가 확정 시 호출 — 대기 심볼에 대해 trigger 조건 체크.</summary>
        public bool TryTriggerEntry(
            string symbol,
            IBinanceKline currentM1,
            IReadOnlyList<IBinanceKline> recentM1s,
            bool isMajorSymbol,
            out EntryTrigger? trigger)
        {
            trigger = null;
            if (!_pending.TryGetValue(symbol, out var p))
                return false;

            // 만료 체크
            if (DateTime.Now - p.RegisteredAt > _pendingExpiry)
            {
                _pending.TryRemove(symbol, out _);
                OnLog?.Invoke($"⏰ [L3][{symbol}] 진입 대기 만료 ({_pendingExpiry.TotalMinutes:F0}분 경과)");
                return false;
            }

            // 15m 필터 전환 체크
            if (!IsUptrend(symbol))
            {
                _pending.TryRemove(symbol, out _);
                OnLog?.Invoke($"🛑 [L3][{symbol}] 15m 하락 전환 → 대기 취소");
                return false;
            }

            // 1m 양봉 조건
            bool isBullish = currentM1.ClosePrice > currentM1.OpenPrice;
            if (!isBullish) return false;

            // [v5.23.57] v5.23.50 눌림+반등 필수 폐기 — 사용자 지시
            //   v5.23.50 (직전 3봉 음봉 + 현재가 > 직전 high) 가 일직선 상승 알트 차단
            //   사용자: "고점 도장 회피"를 "눌림 강제"로 잘못 변환 — 둘은 다른 문제
            //   "고점 회피"는 universal IsEntryAllowedCore 단기봉 가드 (5m RSI 65~75 + 15m BB pos<0.5) 로 처리
            //   여기서는 1m 양봉 + 볼륨 spike 만 유지
            if (recentM1s.Count < 1) return false;

            decimal prevHigh = recentM1s[^1].HighPrice;   // 디버깅 로그용 (가드 X)

            // 1m 거래량 spike 조건
            decimal avgVol = 0m;
            int volLookback = Math.Min(10, recentM1s.Count - 1);
            for (int i = recentM1s.Count - 1 - volLookback; i < recentM1s.Count - 1 && i >= 0; i++)
                avgVol += recentM1s[i].Volume;
            if (volLookback > 0) avgVol /= volLookback;

            decimal mult = isMajorSymbol ? _majorVolSpikeMultiplier : _altVolSpikeMultiplier;
            bool volSpike = avgVol > 0 && currentM1.Volume >= avgVol * mult;
            if (!volSpike) return false;

            // 발사
            _pending.TryRemove(symbol, out _);
            _recentTriggers[symbol] = DateTime.Now;

            decimal volRatio = avgVol > 0 ? currentM1.Volume / avgVol : 0m;
            trigger = new EntryTrigger(
                Symbol: symbol,
                Direction: p.Direction,
                TriggerPrice: currentM1.ClosePrice,
                PendingAgeSec: (decimal)(DateTime.Now - p.RegisteredAt).TotalSeconds,
                VolRatio: volRatio,
                Strength: p.Strength,
                Reason: $"L1+L2({p.Reason})+L3(bull+vol×{volRatio:F1})"
            );

            OnLog?.Invoke($"🚀 [L3][{symbol}] TRIGGER | {p.Direction} px={currentM1.ClosePrice} prevHi={prevHigh:F6} vol×{volRatio:F1} pendingAge={trigger.PendingAgeSec:F0}s");
            OnEntryFire?.Invoke(trigger);
            return true;
        }

        /// <summary>수동 진입 대기 취소 (예: 슬롯 포화 시)</summary>
        public bool CancelPending(string symbol, string reason)
        {
            if (_pending.TryRemove(symbol, out _))
            {
                OnLog?.Invoke($"🚫 [Cancel][{symbol}] 진입 대기 취소: {reason}");
                return true;
            }
            return false;
        }

        /// <summary>현재 대기 중인 심볼 목록 (UI 표시용)</summary>
        public IReadOnlyList<PendingEntry> GetPendingSnapshot()
            => _pending.Values.ToList();

        public RegimeSnapshot? GetRegime(string symbol)
            => _regime.TryGetValue(symbol, out var r) ? r : null;

        // ═══════════════════════════════════════════════════════════════
        // 데이터 타입
        // ═══════════════════════════════════════════════════════════════
        public record RegimeSnapshot(bool IsUptrend, double Close1h, double Ema20_1h, DateTime EvaluatedAt, string Reason);

        public record PendingEntry(
            string Symbol,
            string Direction,
            DateTime RegisteredAt,
            decimal SignalPrice,
            float Strength,
            string Reason,
            decimal RegimeClose,
            decimal RegimeEma50);

        public record EntryTrigger(
            string Symbol,
            string Direction,
            decimal TriggerPrice,
            decimal PendingAgeSec,
            decimal VolRatio,
            float Strength,
            string Reason);
    }
}
