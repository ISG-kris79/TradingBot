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
    ///     - MACD 골든크로스
    ///     - RSI 반등 (과매도 40 이하에서 상승 전환)
    ///     - EMA 12/26 정배열
    ///     - 3개 중 2개 이상 True → "진입 대기" 상태로 등록
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
        // LAYER 1: 15m FILTER
        // ═══════════════════════════════════════════════════════════════
        /// <summary>15분봉 EMA(50) 필터 — 상승장 여부 판독. 15m 종가 확정 시 호출 권장.</summary>
        public bool EvaluateRegime(string symbol, IReadOnlyList<IBinanceKline> candles15m)
        {
            if (candles15m == null || candles15m.Count < _emaPeriod15m + 1)
            {
                _regime[symbol] = new RegimeSnapshot(false, double.NaN, double.NaN, DateTime.Now, "insufficient_15m_data");
                return false;
            }

            double ema50 = IndicatorCalculator.CalculateEMA(candles15m.ToList(), _emaPeriod15m);
            double close = (double)candles15m[^1].ClosePrice;
            bool isUptrend = close > ema50;

            _regime[symbol] = new RegimeSnapshot(isUptrend, close, ema50, DateTime.Now, isUptrend ? "uptrend" : "downtrend");

            // 하락장 전환 시 진입 대기 자동 취소
            if (!isUptrend && _pending.TryRemove(symbol, out var cancelled))
            {
                OnLog?.Invoke($"🛑 [L1][{symbol}] 15m 하락 전환 → 진입 대기 취소 (등록={cancelled.RegisteredAt:HH:mm:ss})");
            }
            return isUptrend;
        }

        public bool IsUptrend(string symbol) =>
            _regime.TryGetValue(symbol, out var r) && r.IsUptrend && (DateTime.Now - r.EvaluatedAt).TotalMinutes < 20;

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

            int signals = 0;
            var hits = new List<string>();
            float strength = 0f;

            // ── [Signal 1] MACD 골든크로스 (최근 2봉 내 hist 음→양 전환)
            var (macdSeries, signalSeries, _) = IndicatorCalculator.CalculateMACDSeries(closes);
            int n = macdSeries.Count;
            if (n >= 3)
            {
                double hist0 = macdSeries[n - 2] - signalSeries[n - 2];
                double hist1 = macdSeries[n - 1] - signalSeries[n - 1];
                if (hist0 <= 0 && hist1 > 0)
                {
                    signals++;
                    hits.Add("MACD_golden_cross");
                    strength += 0.35f;
                }
            }

            // ── [Signal 2] RSI 반등 (직전 rsiBounceFloor 아래 + 현재 상승 + ceiling 미만)
            var rsiSeries = IndicatorCalculator.CalculateRSISeries(closes, _rsiPeriod5m);
            if (rsiSeries.Count >= 3)
            {
                double rsi0 = rsiSeries[^3];
                double rsi1 = rsiSeries[^2];
                double rsi2 = rsiSeries[^1];
                bool recentlyOversold = rsi0 < (double)_rsiBounceFloor || rsi1 < (double)_rsiBounceFloor;
                bool rising = rsi2 > rsi1;
                bool belowCeiling = rsi2 < (double)_rsiEntryCeiling;
                if (recentlyOversold && rising && belowCeiling)
                {
                    signals++;
                    hits.Add($"RSI_bounce({rsi2:F0})");
                    strength += 0.30f;
                }
            }

            // ── [Signal 3] EMA 12/26 정배열 (단기 추세 동조)
            double ema12 = IndicatorCalculator.CalculateEMA(candleList, 12);
            double ema26 = IndicatorCalculator.CalculateEMA(candleList, 26);
            if (ema12 > ema26)
            {
                signals++;
                hits.Add("EMA_aligned");
                strength += 0.20f;
            }

            if (signals < _minSignalsRequired)
            {
                // 이미 대기중이던 신호 소멸 → 취소
                if (_pending.TryRemove(symbol, out var prev))
                {
                    OnLog?.Invoke($"🛑 [L2][{symbol}] 5m 재평가 실패 → 대기 취소 (signals={signals}/{_minSignalsRequired})");
                }
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
                RegimeClose: (decimal)reg.Close15m,
                RegimeEma50: (decimal)reg.Ema50_15m
            );
            _pending[symbol] = newPending;
            pending = newPending;
            OnLog?.Invoke($"🎯 [L2][{symbol}] 진입 대기 등록 | signals={signals}/{_minSignalsRequired} [{string.Join(",", hits)}] strength={strength:F2}");
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

            OnLog?.Invoke($"🚀 [L3][{symbol}] TRIGGER | {p.Direction} price={currentM1.ClosePrice} vol×{volRatio:F1} (min {mult:F1}) pendingAge={trigger.PendingAgeSec:F0}s");
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
        public record RegimeSnapshot(bool IsUptrend, double Close15m, double Ema50_15m, DateTime EvaluatedAt, string Reason);

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
