using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 양방향 AI 공략 시나리오 엔진
    ///
    /// LONG (엘리엇 3파 &amp; V-Turn):
    ///   4H/1H 정배열 + 15M 2파 조정(0.618 Fib) 완료 시
    ///   → 1M에서 저점 높이며 거래량 터지는 순간 LONG 진입
    ///
    /// SHORT (헤드앤숄더 &amp; 데드캣):
    ///   4H H&amp;S 오른쪽 어깨 완성 + 1H 0.618 저항 거절 시
    ///   → 1M에서 넥라인 음봉 이탈 순간 SHORT 진입
    /// </summary>
    public class BiDirectionalScenarioEngine
    {
        private readonly IExchangeService _exchangeService;
        private readonly ElliottWaveDetector _waveDetector;

        public Action<string>? OnLog { get; set; }

        public BiDirectionalScenarioEngine(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
            _waveDetector = new ElliottWaveDetector();
        }

        // ────────────────────────────────────────────────
        // LONG 시나리오: 엘리엇 3파 진입
        // ────────────────────────────────────────────────

        /// <summary>
        /// LONG 시나리오 평가
        /// 조건: 4H/1H 정배열 + 15M Wave2Complete(0.618 피보나치 근접)
        /// </summary>
        public async Task<ScenarioResult> EvaluateLongScenarioAsync(
            string symbol,
            CancellationToken token = default)
        {
            try
            {
                var h4Task  = _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour,   60, token);
                var h1Task  = _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneHour,    60, token);
                var m15Task = _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 60, token);

                await Task.WhenAll(h4Task, h1Task, m15Task);

                var h4Klines  = h4Task.Result;
                var h1Klines  = h1Task.Result;
                var m15Klines = m15Task.Result;

                if (h4Klines == null || h1Klines == null || m15Klines == null ||
                    h4Klines.Count < 30 || h1Klines.Count < 30 || m15Klines.Count < 30)
                    return ScenarioResult.NoSignal("데이터 부족");

                // 1. 4H 정배열 확인
                bool h4Bullish = IsBullishAlignment(h4Klines);
                if (!h4Bullish)
                    return ScenarioResult.NoSignal("4H 역배열");

                // 2. 1H 정배열 확인
                bool h1Bullish = IsBullishAlignment(h1Klines);
                if (!h1Bullish)
                    return ScenarioResult.NoSignal("1H 역배열");

                // 3. 15M 엘리엇 2파 완료 (0.618 피보나치 근접) 확인
                var waveState = GetWave2CompleteState(symbol, m15Klines);
                if (waveState == null)
                    return ScenarioResult.NoSignal("15M 2파 미완료");

                decimal fib618 = waveState.Fib_0618;
                decimal currentPrice = (decimal)m15Klines.Last().ClosePrice;
                decimal distancePct  = Math.Abs(currentPrice - fib618) / fib618 * 100m;

                // 0.618 피보나치 레벨 ±1.5% 이내
                if (distancePct > 1.5m)
                    return ScenarioResult.NoSignal($"0.618 Fib 거리 {distancePct:F2}% > 1.5%");

                decimal wave1Height = waveState.Wave1Height;
                decimal estTarget   = currentPrice + wave1Height * 1.618m; // 3파 목표 (1.618 확장)

                OnLog?.Invoke($"🎯 [Scenario] {symbol} LONG 시나리오 활성화 | " +
                              $"4H/1H 정배열 + 15M Wave2@{fib618:F4} (±{distancePct:F2}%) | " +
                              $"예상 3파 목표={estTarget:F4}");

                return new ScenarioResult
                {
                    IsActive      = true,
                    Direction     = "LONG",
                    Reason        = $"ELLIOTT_WAVE3_SETUP | 4H+1H_BULL | Wave2@Fib618",
                    NecklinePrice = 0m,                 // LONG엔 불필요
                    WaveTarget    = estTarget,
                    Fib618Level   = fib618,
                    WaveState     = waveState
                };
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [Scenario] {symbol} LONG 평가 오류: {ex.Message}");
                return ScenarioResult.NoSignal("예외: " + ex.Message);
            }
        }

        // ────────────────────────────────────────────────
        // SHORT 시나리오: H&S 넥라인 이탈
        // ────────────────────────────────────────────────

        /// <summary>
        /// SHORT 시나리오 평가
        /// 조건: 4H H&S 오른쪽 어깨 완성 + 1H 0.618 저항 거절
        /// </summary>
        public async Task<ScenarioResult> EvaluateShortScenarioAsync(
            string symbol,
            CancellationToken token = default)
        {
            try
            {
                var h4Task = _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour,  70, token);
                var h1Task = _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneHour,   60, token);

                await Task.WhenAll(h4Task, h1Task);

                var h4Klines = h4Task.Result;
                var h1Klines = h1Task.Result;

                if (h4Klines == null || h1Klines == null ||
                    h4Klines.Count < 50 || h1Klines.Count < 30)
                    return ScenarioResult.NoSignal("데이터 부족");

                // 1. 4H H&S 패턴 감지
                var hsResult = HeadAndShouldersDetector.DetectPattern(h4Klines, 70);
                if (!hsResult.IsDetected || hsResult.PatternType != "H&S")
                    return ScenarioResult.NoSignal("4H H&S 미감지");

                decimal neckline = hsResult.Neckline;

                // 2. 오른쪽 어깨 완성 확인: 현재가가 넥라인 위 3% 이내 (어깨 형성 완료 근접)
                decimal lastPrice = (decimal)h4Klines.Last().ClosePrice;
                decimal aboveNeck = (lastPrice - neckline) / neckline * 100m;
                if (aboveNeck > 3.0m || aboveNeck < 0m)
                    return ScenarioResult.NoSignal($"오른쪽어깨 위치 부적합 (넥라인 대비 {aboveNeck:F2}%)");

                // 3. 1H 0.618 저항 거절 확인 (반락 캔들 확인)
                bool h1FibRejected = IsH1FibRejection(h1Klines);
                if (!h1FibRejected)
                    return ScenarioResult.NoSignal("1H Fib618 저항 거절 미확인");

                // H&S 목표: 넥라인 - (헤드 - 넥라인) 높이만큼 하락
                decimal hsHeight  = hsResult.Head - neckline;
                decimal estTarget = neckline - hsHeight;

                OnLog?.Invoke($"🎯 [Scenario] {symbol} SHORT 시나리오 활성화 | " +
                              $"4H H&S 넥라인={neckline:F4} | 현재={lastPrice:F4} (+{aboveNeck:F2}%) | " +
                              $"1H Fib거절 확인 | 예상 목표={estTarget:F4}");

                return new ScenarioResult
                {
                    IsActive      = true,
                    Direction     = "SHORT",
                    Reason        = $"HNS_SHORT_SETUP | H&S@{neckline:F4} | 1H_FIB618_REJECT",
                    NecklinePrice = neckline,
                    WaveTarget    = estTarget,
                    Fib618Level   = 0m,
                    WaveState     = null
                };
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [Scenario] {symbol} SHORT 평가 오류: {ex.Message}");
                return ScenarioResult.NoSignal("예외: " + ex.Message);
            }
        }

        // ────────────────────────────────────────────────
        // 정배열: SMA20 > SMA50 > SMA200
        // ────────────────────────────────────────────────
        private static bool IsBullishAlignment(List<IBinanceKline> klines)
        {
            if (klines.Count < 200) return false;
            var closes = klines.Select(k => (double)k.ClosePrice).ToList();
            double sma20  = closes.TakeLast(20).Average();
            double sma50  = closes.TakeLast(50).Average();
            double sma200 = closes.TakeLast(200).Average();
            return sma20 > sma50 && sma50 > sma200;
        }

        // ────────────────────────────────────────────────
        // 15M Wave2Complete 상태 반환 (피보나치 0.618 근접)
        // ────────────────────────────────────────────────
        private ElliottWaveDetector.WaveState? GetWave2CompleteState(
            string symbol, List<IBinanceKline> m15Klines)
        {
            // 15M 캔들을 CandleData로 변환하여 ElliottWaveDetector에 피드
            for (int i = 1; i < m15Klines.Count; i++)
            {
                var k = m15Klines[i];
                var cd = new CandleData
                {
                    OpenTime  = k.OpenTime,
                    Open      = k.OpenPrice,
                    High      = k.HighPrice,
                    Low       = k.LowPrice,
                    Close     = k.ClosePrice,
                    Volume    = (float)k.Volume
                };
                var recent = m15Klines.Take(i).Select(x => new CandleData
                {
                    OpenTime = x.OpenTime,
                    Open = x.OpenPrice,
                    High = x.HighPrice,
                    Low  = x.LowPrice,
                    Close = x.ClosePrice,
                    Volume = (float)x.Volume
                }).ToList();

                _waveDetector.UpdateWaveDetection(symbol, cd, recent, cd.Close);
            }

            var state = _waveDetector.GetWaveState(symbol);
            if (state?.Phase == ElliottWaveDetector.WavePhase.Wave2Complete)
                return state;

            return null;
        }

        // ────────────────────────────────────────────────
        // 1H Fib 0.618 저항 거절: 최근 반락 캔들 존재 확인
        // 최근 5캔들 내에 위꼬리 음봉(윗꼬리 비율 30%+) 존재
        // ────────────────────────────────────────────────
        private static bool IsH1FibRejection(List<IBinanceKline> h1Klines)
        {
            if (h1Klines.Count < 10) return false;

            var recent = h1Klines.TakeLast(5).ToList();
            foreach (var k in recent)
            {
                double range  = (double)(k.HighPrice - k.LowPrice);
                if (range <= 0) continue;
                double upperWick = (double)(k.HighPrice - Math.Max(k.OpenPrice, k.ClosePrice));
                double wickRatio = upperWick / range;

                // 위꼬리 비율 30% 이상 + 음봉 (저항 거절 캔들)
                if (wickRatio >= 0.30 && k.ClosePrice < k.OpenPrice)
                    return true;
            }
            return false;
        }
    }

    // ────────────────────────────────────────────────
    // 시나리오 결과 모델
    // ────────────────────────────────────────────────

    public class ScenarioResult
    {
        public bool IsActive { get; set; }
        public string Direction { get; set; } = string.Empty;  // "LONG" / "SHORT"
        public string Reason { get; set; } = string.Empty;

        /// <summary>SHORT 시나리오의 넥라인 가격 (1M 허브에서 이탈 감지용)</summary>
        public decimal NecklinePrice { get; set; }

        /// <summary>LONG 시나리오의 3파 목표 (TP 확장용)</summary>
        public decimal WaveTarget { get; set; }

        /// <summary>0.618 피보나치 레벨 (LONG 진입 참조)</summary>
        public decimal Fib618Level { get; set; }

        public ElliottWaveDetector.WaveState? WaveState { get; set; }

        public static ScenarioResult NoSignal(string reason) =>
            new() { IsActive = false, Reason = reason };
    }

    // ────────────────────────────────────────────────
    // 1분봉 허브에 전달하는 시나리오 컨텍스트
    // ────────────────────────────────────────────────

    public class ScenarioContext
    {
        /// <summary>SHORT: 1M 넥라인 이탈 트리거 기준가 (0이면 기본 트리거 사용)</summary>
        public decimal NecklinePrice { get; set; }

        /// <summary>LONG: 3파 목표 (허브가 TP 확장 로그에 사용)</summary>
        public decimal WaveTarget { get; set; }

        /// <summary>시나리오 방향 ("LONG" / "SHORT" / "")</summary>
        public string Direction { get; set; } = string.Empty;

        public static readonly ScenarioContext Empty = new();
    }
}
