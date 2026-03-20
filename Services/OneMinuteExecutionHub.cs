using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [AI Intelligence + 1min Execution]
    /// 상위 타임프레임 AI가 "진입 대기" 신호를 내리면,
    /// 1분봉 캔들 모양 + 거래량만 보고 정밀 타점에서 즉시 진입합니다.
    ///
    /// 트리거 조건 (LONG):
    ///   1. Breakout  — 현재 1분봉 종가가 직전 1분봉 고가를 돌파
    ///   2. V-Turn    — 직전 봉 음봉, 현재 봉 양봉 + 몸통비율 60%+ + 거래량 1.3배+
    ///
    /// 트리거 조건 (SHORT): 역방향 대칭
    ///
    /// 대기 한도: maxWaitSeconds (기본 180초)
    /// AI 확신도가 매우 높으면(≥ threshold) 대기 없이 즉시 진입
    /// </summary>
    public class OneMinuteExecutionHub
    {
        private readonly IExchangeService _exchangeService;

        // 1분봉 조회 주기 (ms)
        private const int PollIntervalMs = 5_000;

        // 최소 거래량 배수 (V-Turn)
        private const double VolumeSpikeMinRatio = 1.3;

        // V-Turn 최소 몸통 비율
        private const double BodyRatioMin = 0.55;

        // 이 신뢰도 이상이면 1분봉 대기 없이 즉시 진입
        private const float ImmediateEntryConfidence = 0.82f;

        public Action<string>? OnLog { get; set; }

        public OneMinuteExecutionHub(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        /// <summary>
        /// 1분봉 트리거를 기다립니다.
        /// </summary>
        /// <param name="symbol">심볼</param>
        /// <param name="direction">LONG / SHORT</param>
        /// <param name="aiConfidence">상위 TF 블렌디드 AI 신뢰도 (0~1)</param>
        /// <param name="maxWaitSeconds">최대 대기 초 (기본 180)</param>
        /// <param name="scenarioCtx">시나리오 컨텍스트 (넥라인 기반 SHORT 트리거 등)</param>
        /// <param name="token">취소 토큰</param>
        /// <returns>(triggered, trigger reason)</returns>
        public async Task<(bool triggered, string reason)> WaitForTriggerAsync(
            string symbol,
            string direction,
            float aiConfidence,
            int maxWaitSeconds = 180,
            ScenarioContext? scenarioCtx = null,
            CancellationToken token = default)
        {
            // 높은 확신도: 대기 없이 즉시 진입
            if (aiConfidence >= ImmediateEntryConfidence)
            {
                OnLog?.Invoke($"🚀 [1M Hub] {symbol} {direction} | 즉시 진입 (AI신뢰도={aiConfidence:P0} ≥ {ImmediateEntryConfidence:P0})");
                return (true, $"IMMEDIATE_HIGH_CONF_{aiConfidence:P0}");
            }

            var deadline = DateTime.Now.AddSeconds(maxWaitSeconds);
            int polls = 0;
            bool hasScenario = scenarioCtx != null && scenarioCtx.Direction == direction;

            string scenarioTag = hasScenario
                ? (direction == "SHORT" ? $" [HNS넥라인={scenarioCtx!.NecklinePrice:F4}]"
                                        : $" [Wave3목표={scenarioCtx!.WaveTarget:F4}]")
                : string.Empty;

            OnLog?.Invoke($"⏱️ [1M Hub] {symbol} {direction}{scenarioTag} | 1분봉 진입 트리거 대기 시작 (최대 {maxWaitSeconds}s, AI={aiConfidence:P0})");

            while (DateTime.Now < deadline && !token.IsCancellationRequested)
            {
                polls++;
                try
                {
                    var klines = await _exchangeService.GetKlinesAsync(
                        symbol, KlineInterval.OneMinute, 4, token);

                    if (klines != null && klines.Count >= 2)
                    {
                        var list  = klines.ToList();
                        var prev  = list[^2]; // 직전 완성 봉
                        var curr  = list[^1]; // 현재 진행 봉

                        if (direction == "LONG")
                        {
                            // 시나리오 컨텍스트: Wave3 — 저점 상승 + 거래량 확인 우선
                            if (hasScenario)
                            {
                                var (hit, why) = CheckLongScenarioTrigger(prev, curr, list);
                                if (hit)
                                {
                                    OnLog?.Invoke($"✅ [1M Hub] {symbol} LONG 시나리오 트리거 [{why}] | " +
                                                  $"Wave3목표={scenarioCtx!.WaveTarget:F4} curr C={curr.ClosePrice:F4}");
                                    return (true, "SCENARIO_" + why);
                                }
                            }

                            var (stdHit, stdWhy) = CheckLongTrigger(prev, curr);
                            if (stdHit)
                            {
                                OnLog?.Invoke($"✅ [1M Hub] {symbol} LONG 트리거 [{stdWhy}] | " +
                                              $"prev H={prev.HighPrice:F4} curr C={curr.ClosePrice:F4} V={curr.Volume:F0}");
                                return (true, stdWhy);
                            }
                        }
                        else
                        {
                            // 시나리오 컨텍스트: H&S — 넥라인 이탈 트리거 우선
                            if (hasScenario && scenarioCtx!.NecklinePrice > 0m)
                            {
                                var (hit, why) = CheckNecklineBreakdown(curr, scenarioCtx.NecklinePrice);
                                if (hit)
                                {
                                    OnLog?.Invoke($"✅ [1M Hub] {symbol} SHORT 넥라인 이탈 [{why}] | " +
                                                  $"넥라인={scenarioCtx.NecklinePrice:F4} curr C={curr.ClosePrice:F4}");
                                    return (true, "SCENARIO_" + why);
                                }
                            }

                            var (stdHit, stdWhy) = CheckShortTrigger(prev, curr);
                            if (stdHit)
                            {
                                OnLog?.Invoke($"✅ [1M Hub] {symbol} SHORT 트리거 [{stdWhy}] | " +
                                              $"prev L={prev.LowPrice:F4} curr C={curr.ClosePrice:F4} V={curr.Volume:F0}");
                                return (true, stdWhy);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [1M Hub] {symbol} 1분봉 조회 오류: {ex.Message}");
                }

                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }

            // 대기 시간 초과 → 그냥 시장가 진입 (기회 자체를 날리지 않음)
            string timeoutReason = token.IsCancellationRequested
                ? "HUB_CANCELLED"
                : $"HUB_TIMEOUT_{polls}polls_{maxWaitSeconds}s";

            OnLog?.Invoke($"⌛ [1M Hub] {symbol} {direction} | 타임아웃 → 시장가 진입 ({polls}회 확인, {maxWaitSeconds}s 경과)");
            return (true, timeoutReason);
        }

        // ──────────────────────────────────────────────────
        // 시나리오 LONG: 저점 상승 + 거래량 확인 (Wave3 진입)
        //   최근 3봉 저점이 상승 추세 + 현재 봉 양봉 + 거래량 1.5배
        // ──────────────────────────────────────────────────
        private static (bool hit, string why) CheckLongScenarioTrigger(
            IBinanceKline prev, IBinanceKline curr, List<IBinanceKline> recent)
        {
            if (recent.Count < 4) return (false, string.Empty);

            // 저점 상승: 최근 3봉(recent[^4], [^3], [^2]) 저점이 순서대로 상승
            var l1 = recent[^4].LowPrice;
            var l2 = recent[^3].LowPrice;
            var l3 = recent[^2].LowPrice; // prev
            bool risingLows = l2 > l1 && l3 > l2;

            // 현재 양봉 + 거래량 1.5배
            bool currBullish = curr.ClosePrice > curr.OpenPrice;
            double volRatio  = (double)prev.Volume > 0 ? (double)curr.Volume / (double)prev.Volume : 0;

            if (risingLows && currBullish && volRatio >= 1.5)
                return (true, $"1M_RISING_LOWS_vol={volRatio:F1}x");

            return (false, string.Empty);
        }

        // ──────────────────────────────────────────────────
        // 시나리오 SHORT: 넥라인 이탈 (H&S 진입)
        //   현재 봉 종가가 넥라인 아래 + 음봉
        // ──────────────────────────────────────────────────
        private static (bool hit, string why) CheckNecklineBreakdown(
            IBinanceKline curr, decimal neckline)
        {
            bool bearish = curr.ClosePrice < curr.OpenPrice;
            bool belowNeckline = curr.ClosePrice < neckline;

            if (bearish && belowNeckline)
                return (true, $"1M_NECKLINE_BREAK@{neckline:F4}");

            return (false, string.Empty);
        }

        // ──────────────────────────────────────────────────
        // LONG 트리거
        // ──────────────────────────────────────────────────
        private static (bool hit, string why) CheckLongTrigger(IBinanceKline prev, IBinanceKline curr)
        {
            // 1. Breakout: 현재 봉 종가 > 직전 봉 고가
            if (curr.ClosePrice > prev.HighPrice && curr.ClosePrice > curr.OpenPrice)
                return (true, "1M_BREAKOUT");

            // 2. V-Turn: 직전 음봉 + 현재 양봉 + 몸통비율 + 거래량
            bool prevBearish = prev.ClosePrice < prev.OpenPrice;
            bool currBullish = curr.ClosePrice > curr.OpenPrice;

            if (prevBearish && currBullish)
            {
                double range    = (double)(curr.HighPrice - curr.LowPrice);
                double body     = (double)(curr.ClosePrice - curr.OpenPrice);
                double bodyRatio = range > 0 ? body / range : 0;
                double volRatio  = (double)prev.Volume > 0 ? (double)curr.Volume / (double)prev.Volume : 0;

                if (bodyRatio >= BodyRatioMin && volRatio >= VolumeSpikeMinRatio)
                    return (true, $"1M_VTURN_body={bodyRatio:P0}_vol={volRatio:F1}x");
            }

            return (false, string.Empty);
        }

        // ──────────────────────────────────────────────────
        // SHORT 트리거
        // ──────────────────────────────────────────────────
        private static (bool hit, string why) CheckShortTrigger(IBinanceKline prev, IBinanceKline curr)
        {
            // 1. Breakdown: 현재 봉 종가 < 직전 봉 저가
            if (curr.ClosePrice < prev.LowPrice && curr.ClosePrice < curr.OpenPrice)
                return (true, "1M_BREAKDOWN");

            // 2. Inverted V-Turn: 직전 양봉 + 현재 음봉 + 몸통비율 + 거래량
            bool prevBullish = prev.ClosePrice > prev.OpenPrice;
            bool currBearish = curr.ClosePrice < curr.OpenPrice;

            if (prevBullish && currBearish)
            {
                double range    = (double)(curr.HighPrice - curr.LowPrice);
                double body     = (double)(curr.OpenPrice - curr.ClosePrice);
                double bodyRatio = range > 0 ? body / range : 0;
                double volRatio  = (double)prev.Volume > 0 ? (double)curr.Volume / (double)prev.Volume : 0;

                if (bodyRatio >= BodyRatioMin && volRatio >= VolumeSpikeMinRatio)
                    return (true, $"1M_VTURN_DOWN_body={bodyRatio:P0}_vol={volRatio:F1}x");
            }

            return (false, string.Empty);
        }
    }
}
