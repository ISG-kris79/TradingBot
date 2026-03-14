using Binance.Net.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 15분봉 볼린저 밴드 스퀴즈 → 중심선 상향 돌파 진입 전략
    /// ─────────────────────────────────────────────────────
    /// 조건 1) 스퀴즈:  현재 BB 폭 &lt; 최근 30캔들 평균 BB 폭 × SQUEEZE_RATIO(0.65)
    /// 조건 2) 중심선 돌파: 직전 종가 &lt; BB Mid, 현재 종가 &gt; BB Mid
    /// 조건 3) 양봉:    현재 종가 &gt; 현재 시가
    /// 조건 4) 거래량:  현재 거래량 &gt; 20캔들 평균 거래량 × VOLUME_RATIO(1.2)
    /// 조건 5) RSI:     40 ≤ RSI ≤ 65 (과열/과매도 제외)
    /// 조건 6) RR 비율: (TP - entry) / (entry - SL) ≥ MIN_RR_RATIO(1.5)
    ///
    /// TP  = 현재 BB Upper
    /// SL  = 현재 BB Lower (또는 진입가 - 1.5×ATR 중 더 먼 값)
    /// 재진입 쿨다운: 심볼당 4시간
    /// </summary>
    public class FifteenMinBBSqueezeBreakoutStrategy
    {
        // ── 파라미터 ────────────────────────────────────────────
        private const int    BB_PERIOD            = 20;
        private const double BB_MULTIPLIER        = 2.0;
        private const int    SQUEEZE_LOOKBACK     = 30;  // 평균 폭 계산 기준 캔들 수
        private const double SQUEEZE_RATIO        = 0.80; // currentBBW < avgBBW * 이 값 (메이저 고속도로: 0.65→0.80)
        private const double VOLUME_RATIO         = 1.20; // 거래량 배수 기준
        private const double RSI_MIN              = 40.0;
        private const double RSI_MAX              = 65.0;
        private const double MIN_RR_RATIO         = 1.5;
        private const int    COOLDOWN_HOURS       = 4;
        private const int    MIN_REQUIRED_CANDLES = 60;   // 최소 필요 캔들 수

        // ── 상태 ────────────────────────────────────────────────
        /// <summary>스퀴즈가 한 번 이상 감지된 심볼 (중심선 돌파 조건 전에 스퀴즈 선행 필요)</summary>
        private readonly ConcurrentDictionary<string, DateTime> _squeezeDetectedAt
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>마지막 신호 발생 시각 (쿨다운)</summary>
        private readonly ConcurrentDictionary<string, DateTime> _lastSignalAt
            = new(StringComparer.OrdinalIgnoreCase);

        // ── 공개 API ────────────────────────────────────────────

        /// <summary>
        /// 15분봉 캔들로 BB 스퀴즈 → 중심선 돌파 신호를 평가합니다.
        /// </summary>
        /// <param name="symbol">심볼</param>
        /// <param name="klines15m">15분봉 캔들 (최소 60개 권장)</param>
        /// <param name="result">신호 결과 (신호 없으면 null)</param>
        /// <returns>진입 신호 존재 여부</returns>
        public bool Evaluate(
            string symbol,
            List<IBinanceKline> klines15m,
            out BBSqueezeSignalResult? result)
        {
            result = null;

            if (klines15m == null || klines15m.Count < MIN_REQUIRED_CANDLES)
                return false;

            // 쿨다운 체크
            if (_lastSignalAt.TryGetValue(symbol, out var lastSignal)
                && (DateTime.UtcNow - lastSignal).TotalHours < COOLDOWN_HOURS)
                return false;

            // ── BB 시리즈 계산 ──
            var closes = klines15m.Select(k => (double)k.ClosePrice).ToList();
            var (bbUppers, bbMids, bbLowers) =
                IndicatorCalculator.CalculateBBSeries(closes, BB_PERIOD, BB_MULTIPLIER);

            if (bbMids == null || bbMids.Count < MIN_REQUIRED_CANDLES)
                return false;

            int lastIdx = bbMids.Count - 1;
            int prevIdx = lastIdx - 1;

            double curMid   = bbMids[lastIdx];
            double prevMid  = bbMids[prevIdx];
            double curUpper = bbUppers[lastIdx];
            double curLower = bbLowers[lastIdx];

            if (curMid <= 0 || curUpper <= curLower)
                return false;

            // ── BB 폭 시리즈 계산 ──
            double curBbWidth = (curUpper - curLower) / curMid * 100.0;

            // 스퀴즈 판단용 평균 BB 폭 (마지막 캔들 제외, SQUEEZE_LOOKBACK 범위)
            int startForAvg = Math.Max(0, prevIdx - SQUEEZE_LOOKBACK);
            double avgBbWidth = 0;
            int validCount = 0;
            for (int i = startForAvg; i < prevIdx; i++)
            {
                if (bbMids[i] > 0 && bbUppers[i] > bbLowers[i])
                {
                    avgBbWidth += (bbUppers[i] - bbLowers[i]) / bbMids[i] * 100.0;
                    validCount++;
                }
            }
            if (validCount < 10) return false;
            avgBbWidth /= validCount;

            // ── 조건 1: 스퀴즈 감지 & 상태 갱신 ──
            bool isSqueezed = curBbWidth < avgBbWidth * SQUEEZE_RATIO;
            if (isSqueezed)
            {
                _squeezeDetectedAt[symbol] = DateTime.UtcNow;
            }

            // 스퀴즈가 먼저 감지된 시간이 있어야 함 (최대 8시간 이내)
            bool hadPriorSqueeze = _squeezeDetectedAt.TryGetValue(symbol, out var squeezeAt)
                && (DateTime.UtcNow - squeezeAt).TotalHours <= 8;
            if (!hadPriorSqueeze) return false;

            // ── 조건 2: 중심선 상향 돌파 ──
            double prevClose = (double)klines15m[^2].ClosePrice;
            double curClose  = (double)klines15m[^1].ClosePrice;
            double curOpen   = (double)klines15m[^1].OpenPrice;

            bool crossedAboveMid = prevClose < prevMid && curClose > curMid;
            if (!crossedAboveMid) return false;

            // ── 조건 3: 양봉 ──
            if (curClose <= curOpen) return false;

            // ── 조건 4: 거래량 확인 ──
            double curVol = (double)klines15m[^1].Volume;
            var recentVols = klines15m
                .Skip(Math.Max(0, klines15m.Count - 1 - BB_PERIOD))
                .Take(BB_PERIOD)
                .Select(k => (double)k.Volume)
                .ToList();
            double avgVol = recentVols.Count > 0 ? recentVols.Average() : 0;
            if (avgVol <= 0 || curVol < avgVol * VOLUME_RATIO) return false;

            // ── 조건 5: RSI ──
            double rsi = IndicatorCalculator.CalculateRSI(klines15m, 14);
            if (rsi < RSI_MIN || rsi > RSI_MAX) return false;

            // ── ATR 기반 SL 보완 ──
            double atr = IndicatorCalculator.CalculateATR(klines15m, 14);

            decimal entryPrice = (decimal)curClose;
            decimal tp         = (decimal)curUpper;
            decimal slByBB     = (decimal)curLower;
            decimal slByAtr    = atr > 0 ? entryPrice - (decimal)(atr * 1.5) : slByBB;
            decimal sl         = Math.Min(slByBB, slByAtr); // 더 낮은(보수적인) SL

            if (entryPrice <= sl || tp <= entryPrice) return false;

            // ── 조건 6: RR 비율 ──
            double rrRatio = (double)(tp - entryPrice) / (double)(entryPrice - sl);
            if (rrRatio < MIN_RR_RATIO) return false;

            // ── 신호 발생 ──
            _lastSignalAt[symbol] = DateTime.UtcNow;
            _squeezeDetectedAt.TryRemove(symbol, out _); // 다음 스퀴즈 재감지 전까지 초기화

            result = new BBSqueezeSignalResult
            {
                Symbol         = symbol,
                Direction      = "LONG",
                EntryPrice     = entryPrice,
                TakeProfit     = tp,
                StopLoss       = sl,
                BbWidth        = curBbWidth,
                AvgBbWidth     = avgBbWidth,
                SqueezeRatio   = curBbWidth / avgBbWidth,
                VolumeMultiple = avgVol > 0 ? curVol / avgVol : 0,
                Rsi            = rsi,
                RrRatio        = rrRatio,
                SignalTime     = DateTime.UtcNow
            };
            return true;
        }

        /// <summary>스퀴즈 상태를 명시적으로 초기화합니다.</summary>
        public void ResetState(string symbol)
        {
            _squeezeDetectedAt.TryRemove(symbol, out _);
            _lastSignalAt.TryRemove(symbol, out _);
        }
    }

    /// <summary>BB 스퀴즈 돌파 신호 결과</summary>
    public sealed record BBSqueezeSignalResult
    {
        public required string  Symbol         { get; init; }
        public required string  Direction      { get; init; }
        public required decimal EntryPrice     { get; init; }
        public required decimal TakeProfit     { get; init; }
        public required decimal StopLoss       { get; init; }
        public          double  BbWidth        { get; init; }
        public          double  AvgBbWidth     { get; init; }
        public          double  SqueezeRatio   { get; init; }
        public          double  VolumeMultiple { get; init; }
        public          double  Rsi            { get; init; }
        public          double  RrRatio        { get; init; }
        public          DateTime SignalTime    { get; init; }
    }
}
