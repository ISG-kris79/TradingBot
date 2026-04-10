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
    /// MACD 골든크로스/데드크로스 감지 서비스
    /// - 상위봉(15m, 1H) 정배열 확인 + 1분봉 MACD 골든크로스 → 진입 신호
    /// - Case A: 0선 아래 골크 (과매도 반등)
    /// - Case B: 0선 위 골크 (추세 가속, 숏 스퀴징)
    /// - 데드크로스 → 트레일링 스탑 조임 / 익절 신호
    /// </summary>
    public class MacdCrossSignalService
    {
        private readonly IExchangeService _exchangeService;
        public event Action<string>? OnLog;

        // [v4.5.2] 크로스 이력 추적 — 휩소(whipsaw) ML 피처용
        private readonly ConcurrentDictionary<string, List<CrossHistoryEntry>> _crossHistory = new();
        private const int CrossHistoryMaxAge = 600; // 10분 보관

        public MacdCrossSignalService(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        /// <summary>
        /// [v4.5.15] WebSocket 캐시 우선 조회, 없으면 REST fallback
        /// 반환: List (호출 측이 .Count 프로퍼티 사용)
        /// </summary>
        private async Task<List<IBinanceKline>?> GetKlinesCachedAsync(
            string symbol, KlineInterval interval, int limit, CancellationToken token)
        {
            var cached = MarketDataManager.Instance?.GetCachedKlines(symbol, interval, Math.Min(limit, 30));
            if (cached != null) return cached;
            var rest = await _exchangeService.GetKlinesAsync(symbol, interval, limit, token);
            return rest?.ToList();
        }

        /// <summary>
        /// 크로스 이벤트 기록 (DetectGoldenCrossAsync 내부에서 자동 호출)
        /// </summary>
        private void RecordCrossEvent(string symbol, MacdCrossType crossType, double macdLine, double signalLine, double rsi)
        {
            var history = _crossHistory.GetOrAdd(symbol, _ => new List<CrossHistoryEntry>());
            lock (history)
            {
                history.Add(new CrossHistoryEntry
                {
                    Time = DateTime.UtcNow,
                    CrossType = crossType,
                    MacdLine = macdLine,
                    SignalLine = signalLine,
                    RSI = rsi
                });
                // 오래된 항목 제거
                var cutoff = DateTime.UtcNow.AddSeconds(-CrossHistoryMaxAge);
                history.RemoveAll(h => h.Time < cutoff);
            }
        }

        /// <summary>
        /// ML 피처용: 심볼의 최근 크로스 이력 통계
        /// </summary>
        public MacdWhipsawFeatures GetWhipsawFeatures(string symbol, MacdCrossType currentCrossType)
        {
            var result = new MacdWhipsawFeatures();

            if (!_crossHistory.TryGetValue(symbol, out var history))
                return result;

            var now = DateTime.UtcNow;
            List<CrossHistoryEntry> recent;
            lock (history)
            {
                recent = history.Where(h => (now - h.Time).TotalSeconds <= 300).ToList(); // 5분 이내
            }

            if (recent.Count == 0)
                return result;

            // 1) 5분 내 크로스 방향 전환 횟수
            int flipCount = 0;
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i].CrossType != recent[i - 1].CrossType)
                    flipCount++;
            }
            result.CrossFlipCount5m = flipCount;

            // 2) 직전 반대 크로스 이후 경과 초
            var oppositeCross = currentCrossType == MacdCrossType.Golden
                ? MacdCrossType.Dead : MacdCrossType.Golden;
            var lastOpposite = recent.LastOrDefault(h => h.CrossType == oppositeCross);
            if (lastOpposite != null)
                result.SecondsSinceOppositeCross = (float)(now - lastOpposite.Time).TotalSeconds;
            else
                result.SecondsSinceOppositeCross = 600f; // 반대 크로스 없음 = 안전

            return result;
        }

        /// <summary>
        /// [v4.5.17] 상위봉 정배열 확인 (D1 SMA20 > SMA60)
        /// - MACD 크로스가 4시간봉 기준이므로, 상위 TF는 D1로 변경
        /// </summary>
        public async Task<(bool isBullish, string detail)> CheckHigherTimeframeBullishAsync(
            string symbol, CancellationToken token)
        {
            try
            {
                var kD1 = await GetKlinesCachedAsync(symbol, KlineInterval.OneDay, 70, token);
                if (kD1 == null || kD1.Count < 60)
                    return (false, "D1 데이터 부족");

                var listD1 = kD1.ToList();
                double sma20 = listD1.TakeLast(20).Average(k => (double)k.ClosePrice);
                double sma60 = listD1.TakeLast(60).Average(k => (double)k.ClosePrice);
                bool bullish = sma20 > sma60;

                return (bullish, $"D1 sma20={sma20:F4},sma60={sma60:F4},bullish={bullish}");
            }
            catch (Exception ex)
            {
                return (false, $"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// [v4.6.0] PUMP 알트 전용 HTF 강세 체크 — 더 관대함
        /// - MACD 크로스(메이저)는 D1 기준이지만, PUMP 알트는 D1 대부분 하향 추세라 차단 과함
        /// - H1 SMA20>SMA60 또는 M15 SMA20>SMA60 중 하나만 충족하면 통과
        /// - 단기 상승 모멘텀 포착 우선
        /// </summary>
        public async Task<(bool isBullish, string detail)> CheckPumpHtfBullishAsync(
            string symbol, CancellationToken token)
        {
            try
            {
                // H1 체크
                var k1h = await GetKlinesCachedAsync(symbol, KlineInterval.OneHour, 70, token);
                bool h1Bullish = false;
                if (k1h != null && k1h.Count >= 60)
                {
                    var list1h = k1h.ToList();
                    double sma20 = list1h.TakeLast(20).Average(k => (double)k.ClosePrice);
                    double sma60 = list1h.TakeLast(60).Average(k => (double)k.ClosePrice);
                    h1Bullish = sma20 > sma60;
                }

                // M15 체크
                var k15m = await GetKlinesCachedAsync(symbol, KlineInterval.FifteenMinutes, 70, token);
                bool m15Bullish = false;
                if (k15m != null && k15m.Count >= 60)
                {
                    var list15m = k15m.ToList();
                    double sma20 = list15m.TakeLast(20).Average(k => (double)k.ClosePrice);
                    double sma60 = list15m.TakeLast(60).Average(k => (double)k.ClosePrice);
                    m15Bullish = sma20 > sma60;
                }

                // 둘 다 데이터 없으면 false
                if (k1h == null && k15m == null)
                    return (false, "H1/M15 데이터 모두 부족");

                // OR 조건: 하나만 충족해도 통과 (관대)
                bool pass = h1Bullish || m15Bullish;
                return (pass, $"PUMP-HTF H1={h1Bullish} M15={m15Bullish} → {(pass ? "통과" : "차단")}");
            }
            catch (Exception ex)
            {
                return (false, $"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// [v4.5.17] 상위봉 하락세 확인 (D1 SMA20 < SMA60 또는 RSI 과매수 꺾임)
        /// </summary>
        public async Task<(bool isBearish, string detail)> CheckHigherTimeframeBearishAsync(
            string symbol, CancellationToken token)
        {
            try
            {
                var kD1 = await GetKlinesCachedAsync(symbol, KlineInterval.OneDay, 70, token);
                if (kD1 == null || kD1.Count < 60)
                    return (false, "D1 데이터 부족");

                var listD1 = kD1.ToList();
                double sma20 = listD1.TakeLast(20).Average(k => (double)k.ClosePrice);
                double sma60 = listD1.TakeLast(60).Average(k => (double)k.ClosePrice);
                bool bearish = sma20 < sma60;

                // RSI 과매수 꺾임 (D1 RSI 60~70 구간에서 역전 신호)
                double rsiD1 = CalculateRSI(listD1, 14);
                bool overboughtReversal = rsiD1 > 60 && rsiD1 < 70 && sma20 > sma60;

                bool isBearish = bearish || overboughtReversal;
                return (isBearish, $"D1 bearish={bearish},RSI={rsiD1:F1},overboughtReversal={overboughtReversal}");
            }
            catch (Exception ex)
            {
                return (false, $"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// [v4.5.17] 4시간봉 MACD 골든크로스/데드크로스 감지 (기존 1분봉 → 4시간봉)
        /// - 4시간봉은 노이즈 적고 추세 전환을 확실하게 포착
        /// - 마지막 미완성 봉 제외 → 직전 완성봉으로 판단
        /// - 노이즈 필터: SignalGapRatio, Angle, 휩소, RSI 중립 구간 차단
        /// </summary>
        /// <returns>(detected, caseType, macdLine, signalLine, histogram, rsi)</returns>
        public async Task<MacdCrossResult> DetectGoldenCrossAsync(string symbol, CancellationToken token)
        {
            try
            {
                // [v4.5.17] 4시간봉 100개 (약 17일 분량) — MACD EMA(26) + Signal(9) 안정 계산 가능
                var klines = await GetKlinesCachedAsync(symbol, KlineInterval.FourHour, 100, token);
                if (klines == null || klines.Count < 40)
                    return MacdCrossResult.None("4h 데이터 부족");

                // [v4.5.17] 미완성 마지막 봉 제외 (현재 진행 중인 봉은 값이 계속 바뀜)
                var list = klines.ToList();
                if (list.Count > 0) list.RemoveAt(list.Count - 1);
                if (list.Count < 35)
                    return MacdCrossResult.None("4h 완성봉 부족");

                var (macd, signal, hist) = CalculateMACD(list);
                var (prevMacd, prevSignal, prevHist) = CalculateMACD(list.Take(list.Count - 1).ToList());

                // 골든크로스: 이전봉 MACD < Signal, 현재봉 MACD >= Signal
                bool goldenCross = prevMacd < prevSignal && macd >= signal;

                // 데드크로스: 이전봉 MACD > Signal, 현재봉 MACD <= Signal
                bool deadCross = prevMacd > prevSignal && macd <= signal;

                // RSI
                double rsi = CalculateRSI(list, 14);

                // 히스토그램 변화율 (ML 피처용)
                double histChangeRate = prevHist != 0 ? (hist - prevHist) / Math.Abs(prevHist) : 0;

                // DeadCrossAngle: (MACD-Signal)[t] - (MACD-Signal)[t-1]  음수일수록 급하락
                double deadCrossAngle = (macd - signal) - (prevMacd - prevSignal);

                // [v4.5.2] ML 피처용: MACD-Signal 갭 / ATR 비율, 히스토그램 강도
                double atr14 = CalculateATR(list, 14);
                double signalGapRatio = atr14 > 0 ? Math.Abs(macd - signal) / atr14 : 0;
                double avgAbsHist = CalculateAvgAbsHistogram(list, 14);
                double histStrength = avgAbsHist > 0 ? Math.Abs(hist) / avgAbsHist : 0;

                // [v4.5.2] 4시간봉 최근 10봉 크로스 횟수
                int recentCrossCount = CountRecentCrosses(list, 10);

                // ═══════════════════════════════════════════════════════════════
                // [v4.5.17] 노이즈 필터 — 의미 없는 크로스를 텔레그램 알림 전에 차단
                // ═══════════════════════════════════════════════════════════════
                // 필터 1: SignalGapRatio 너무 작음 — |MACD-Signal| < ATR×0.02 = 가격 변동의 2% 미만
                //        = MACD 선과 Signal 선이 거의 겹쳐 있음 (횡보 노이즈)
                bool noiseGap = signalGapRatio < 0.02;

                // 필터 2: Angle(교차 기울기) 너무 약함
                //        DeadCrossAngle의 절대값이 평균 히스토그램의 5% 미만 = 거의 수평 교차
                bool noiseAngle = avgAbsHist > 0 && Math.Abs(deadCrossAngle) < avgAbsHist * 0.05;

                // 필터 3: 휩소 구간 (최근 10봉 내 크로스 3회 이상)
                bool whipsawZone = recentCrossCount >= 3;

                // 필터 4: RSI 중립 구간 — 방향성 약함
                //        Golden은 RSI < 55, Dead는 RSI > 45 이면 확신 부족
                bool rsiNeutralForGolden = goldenCross && rsi < 55;
                bool rsiNeutralForDead = deadCross && rsi > 45;

                if ((goldenCross || deadCross) && (noiseGap || noiseAngle || whipsawZone || rsiNeutralForGolden || rsiNeutralForDead))
                {
                    string reason = noiseGap ? "GapRatio부족"
                        : noiseAngle ? "Angle약함"
                        : whipsawZone ? $"휩소{recentCrossCount}회"
                        : rsiNeutralForGolden ? $"RSI중립({rsi:F0})"
                        : $"RSI중립({rsi:F0})";
                    return MacdCrossResult.None($"NoiseFiltered[{reason}] MACD={macd:F6} Gap={signalGapRatio:F3} Angle={deadCrossAngle:F6}");
                }

                if (goldenCross)
                {
                    RecordCrossEvent(symbol, MacdCrossType.Golden, macd, signal, rsi);
                    string caseType = macd > 0 ? "B" : "A";
                    var whipsaw = GetWhipsawFeatures(symbol, MacdCrossType.Golden);
                    return new MacdCrossResult
                    {
                        Detected = true,
                        CrossType = MacdCrossType.Golden,
                        CaseType = caseType,
                        MacdLine = macd,
                        SignalLine = signal,
                        Histogram = hist,
                        HistChangeRate = histChangeRate,
                        DeadCrossAngle = deadCrossAngle,
                        RSI = rsi,
                        SignalGapRatio = signalGapRatio,
                        HistogramStrength = histStrength,
                        RecentCrossCount = recentCrossCount,
                        WhipsawFeatures = whipsaw,
                        Detail = $"4h GoldenCross Case{caseType} MACD={macd:F6} Sig={signal:F6} Hist={hist:F6} RSI={rsi:F1} GapRatio={signalGapRatio:F3} Flips={whipsaw.CrossFlipCount5m}"
                    };
                }

                if (deadCross)
                {
                    RecordCrossEvent(symbol, MacdCrossType.Dead, macd, signal, rsi);
                    // 숏 유형 A: 0선 근처/위에서 데드크로스 (추세 추종, 가장 안전)
                    // 숏 유형 B: 0선 아래에서 히스토그램 급감 (변곡점 포착, 하이리스크)
                    string shortCase = macd >= 0 || (macd > -0.0001 && prevMacd > 0) ? "A" : "B";
                    var whipsaw = GetWhipsawFeatures(symbol, MacdCrossType.Dead);
                    return new MacdCrossResult
                    {
                        Detected = true,
                        CrossType = MacdCrossType.Dead,
                        CaseType = shortCase,
                        MacdLine = macd,
                        SignalLine = signal,
                        Histogram = hist,
                        HistChangeRate = histChangeRate,
                        DeadCrossAngle = deadCrossAngle,
                        RSI = rsi,
                        SignalGapRatio = signalGapRatio,
                        HistogramStrength = histStrength,
                        RecentCrossCount = recentCrossCount,
                        WhipsawFeatures = whipsaw,
                        Detail = $"4h DeadCross Case{shortCase} MACD={macd:F6} Sig={signal:F6} Angle={deadCrossAngle:F6} RSI={rsi:F1} GapRatio={signalGapRatio:F3} Flips={whipsaw.CrossFlipCount5m}"
                    };
                }

                // 히스토그램 Peak Out 감지 (롱 익절 신호)
                bool histPeakOut = prevHist > 0 && hist > 0 && hist < prevHist && prevHist > 0.00001;

                // 히스토그램 Bottom Out 감지 (숏 익절 신호: 음수 막대가 짧아지기 시작)
                bool histBottomOut = prevHist < 0 && hist < 0 && hist > prevHist && prevHist < -0.00001;

                MacdCrossType noCrossType = MacdCrossType.None;
                string noCrossDetail = "NoCross";
                if (histPeakOut)
                {
                    noCrossType = MacdCrossType.HistPeakOut;
                    noCrossDetail = $"HistPeakOut hist={hist:F6} prev={prevHist:F6}";
                }
                else if (histBottomOut)
                {
                    noCrossType = MacdCrossType.HistBottomOut;
                    noCrossDetail = $"HistBottomOut hist={hist:F6} prev={prevHist:F6}";
                }

                return new MacdCrossResult
                {
                    Detected = false,
                    CrossType = noCrossType,
                    MacdLine = macd,
                    SignalLine = signal,
                    Histogram = hist,
                    HistChangeRate = histChangeRate,
                    DeadCrossAngle = deadCrossAngle,
                    RSI = rsi,
                    Detail = noCrossDetail
                };
            }
            catch (Exception ex)
            {
                return MacdCrossResult.None($"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// [v4.5.17] 1분봉 MACD 단타 감지 (익절 타이밍 / 꼬리 리테스트용)
        /// - `DetectGoldenCrossAsync`는 4시간봉이라 익절/단타에 느림
        /// - 이 메서드는 기존 1분봉 로직 유지 (PositionMonitor 실시간 익절 + 15분 꼬리 리테스트 전용)
        /// - 노이즈 필터는 적용하지 않음 (빠른 반응 우선)
        /// </summary>
        public async Task<MacdCrossResult> DetectShortTermCrossAsync(string symbol, CancellationToken token)
        {
            try
            {
                var klines = await GetKlinesCachedAsync(symbol, KlineInterval.OneMinute, 40, token);
                if (klines == null || klines.Count < 30)
                    return MacdCrossResult.None("1m 데이터 부족");

                var list = klines.ToList();
                var (macd, signal, hist) = CalculateMACD(list);
                var (prevMacd, prevSignal, prevHist) = CalculateMACD(list.Take(list.Count - 1).ToList());

                bool goldenCross = prevMacd < prevSignal && macd >= signal;
                bool deadCross = prevMacd > prevSignal && macd <= signal;

                double rsi = CalculateRSI(list, 14);
                double histChangeRate = prevHist != 0 ? (hist - prevHist) / Math.Abs(prevHist) : 0;
                double deadCrossAngle = (macd - signal) - (prevMacd - prevSignal);

                if (goldenCross)
                {
                    string caseType = macd > 0 ? "B" : "A";
                    return new MacdCrossResult
                    {
                        Detected = true,
                        CrossType = MacdCrossType.Golden,
                        CaseType = caseType,
                        MacdLine = macd,
                        SignalLine = signal,
                        Histogram = hist,
                        HistChangeRate = histChangeRate,
                        DeadCrossAngle = deadCrossAngle,
                        RSI = rsi,
                        Detail = $"1m GoldenCross Case{caseType} MACD={macd:F6}"
                    };
                }
                if (deadCross)
                {
                    string shortCase = macd >= 0 || (macd > -0.0001 && prevMacd > 0) ? "A" : "B";
                    return new MacdCrossResult
                    {
                        Detected = true,
                        CrossType = MacdCrossType.Dead,
                        CaseType = shortCase,
                        MacdLine = macd,
                        SignalLine = signal,
                        Histogram = hist,
                        HistChangeRate = histChangeRate,
                        DeadCrossAngle = deadCrossAngle,
                        RSI = rsi,
                        Detail = $"1m DeadCross Case{shortCase} MACD={macd:F6}"
                    };
                }

                // 히스토그램 Peak/Bottom Out 감지 (익절 신호)
                bool histPeakOut = prevHist > 0 && hist > 0 && hist < prevHist && prevHist > 0.00001;
                bool histBottomOut = prevHist < 0 && hist < 0 && hist > prevHist && prevHist < -0.00001;
                MacdCrossType noCrossType = MacdCrossType.None;
                if (histPeakOut) noCrossType = MacdCrossType.HistPeakOut;
                else if (histBottomOut) noCrossType = MacdCrossType.HistBottomOut;

                return new MacdCrossResult
                {
                    Detected = false,
                    CrossType = noCrossType,
                    MacdLine = macd,
                    SignalLine = signal,
                    Histogram = hist,
                    HistChangeRate = histChangeRate,
                    DeadCrossAngle = deadCrossAngle,
                    RSI = rsi,
                    Detail = "1m NoCross"
                };
            }
            catch (Exception ex)
            {
                return MacdCrossResult.None($"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// 15분봉 위꼬리 음봉 감지 → 1분봉 리테스트 대기 → MACD 데크 진입 복합 신호
        /// </summary>
        public async Task<BearishTailResult> Detect15mBearishTailAsync(string symbol, CancellationToken token)
        {
            try
            {
                // 15분봉 최근 3봉 조회
                var k15m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 30, token);
                if (k15m == null || k15m.Count < 20)
                    return BearishTailResult.None("15m 데이터 부족");

                var list15m = k15m.ToList();
                var lastCandle = list15m[^2]; // 직전 완성봉 (현재봉은 미완성)

                decimal high = lastCandle.HighPrice;
                decimal low = lastCandle.LowPrice;
                decimal open = lastCandle.OpenPrice;
                decimal close = lastCandle.ClosePrice;
                decimal totalRange = high - low;

                if (totalRange <= 0)
                    return BearishTailResult.None("범위 0");

                // 음봉 여부
                bool isBearish = close < open;
                if (!isBearish)
                    return BearishTailResult.None("양봉");

                // 위꼬리 비율 계산: UpperShadowRatio = (High - Max(Open,Close)) / (High - Low)
                decimal bodyMax = Math.Max(open, close);
                decimal upperShadow = high - bodyMax;
                float upperShadowRatio = (float)(upperShadow / totalRange);

                if (upperShadowRatio < 0.50f) // 최소 50% 이상 꼬리
                    return BearishTailResult.None($"꼬리 부족 ({upperShadowRatio:P0})");

                // 거래량 비교 (직전 10봉 평균 대비)
                var prev10 = list15m.Skip(Math.Max(0, list15m.Count - 12)).Take(10).ToList();
                double avgVol = prev10.Any() ? prev10.Average(k => (double)k.Volume) : 0;
                double lastVol = (double)lastCandle.Volume;
                float relativeVolume = avgVol > 0 ? (float)(lastVol / avgVol) : 1f;

                // 15분봉 MACD
                var (macd15, signal15, hist15) = CalculateMACD(list15m);
                bool macd15mBearish = macd15 < signal15 || hist15 < 0;

                // 리테스트 목표가 (꼬리의 0.5~0.618 지점)
                decimal retestTarget50 = close + (upperShadow * 0.50m);
                decimal retestTarget618 = close + (upperShadow * 0.618m);

                OnLog?.Invoke($"🕯️ [15m 꼬리] {symbol} UpperShadow={upperShadowRatio:P0} Vol={relativeVolume:F1}x MACD15m={(macd15mBearish ? "약세" : "강세")} | 리테스트 목표: {retestTarget50:F4}~{retestTarget618:F4}");

                return new BearishTailResult
                {
                    Detected = true,
                    UpperShadowRatio = upperShadowRatio,
                    RelativeVolume = relativeVolume,
                    Is15mMacdBearish = macd15mBearish,
                    CandleHigh = high,
                    CandleClose = close,
                    RetestTarget50 = retestTarget50,
                    RetestTarget618 = retestTarget618,
                    Detail = $"15m꼬리 {upperShadowRatio:P0} Vol={relativeVolume:F1}x MACD={(macd15mBearish ? "약세" : "강세")}"
                };
            }
            catch (Exception ex)
            {
                return BearishTailResult.None($"에러: {ex.Message}");
            }
        }

        /// <summary>
        /// 1분봉 리테스트 대기: 가격이 15분봉 꼬리의 0.5~0.618 지점까지 올라왔을 때 MACD 데크 확인
        /// </summary>
        public async Task<(bool triggered, string reason)> WaitForRetestShortTriggerAsync(
            string symbol, decimal retestLow, decimal retestHigh, decimal stopLoss,
            int maxWaitSeconds = 300, CancellationToken token = default)
        {
            var deadline = DateTime.Now.AddSeconds(maxWaitSeconds);
            bool retestZoneReached = false;

            OnLog?.Invoke($"⏱️ [꼬리 리테스트] {symbol} SHORT 대기 | 리테스트 구간: {retestLow:F4}~{retestHigh:F4} | SL: {stopLoss:F4} | 최대 {maxWaitSeconds}s");

            while (DateTime.Now < deadline && !token.IsCancellationRequested)
            {
                try
                {
                    var klines1m = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.OneMinute, 5, token);
                    if (klines1m == null || klines1m.Count < 3)
                    {
                        await Task.Delay(3000, token);
                        continue;
                    }

                    var list1m = klines1m.ToList();
                    decimal currentPrice = list1m[^1].ClosePrice;

                    // 리테스트 구간 도달 확인
                    if (currentPrice >= retestLow && currentPrice <= retestHigh)
                    {
                        retestZoneReached = true;

                        // [v4.5.17] 1분봉 단타 MACD 데크 확인 — DetectShortTermCrossAsync 사용
                        var crossResult = await DetectShortTermCrossAsync(symbol, token);
                        if (crossResult.CrossType == MacdCrossType.Dead)
                        {
                            OnLog?.Invoke($"✅ [꼬리 리테스트] {symbol} 리테스트 구간({currentPrice:F4}) + MACD 데크 → SHORT 트리거!");
                            return (true, $"RETEST_DEAD_CROSS price={currentPrice:F4} angle={crossResult.DeadCrossAngle:F6}");
                        }

                        // RSI가 반등 후 다시 꺾이는지
                        if (crossResult.RSI < 45 && crossResult.HistChangeRate < -0.3)
                        {
                            OnLog?.Invoke($"✅ [꼬리 리테스트] {symbol} RSI꺾임({crossResult.RSI:F1}) + Hist감소 → SHORT 트리거!");
                            return (true, $"RETEST_RSI_TURN price={currentPrice:F4} rsi={crossResult.RSI:F1}");
                        }
                    }

                    // 리테스트 구간 지나서 SL 돌파하면 포기
                    if (currentPrice > stopLoss)
                    {
                        OnLog?.Invoke($"❌ [꼬리 리테스트] {symbol} SL({stopLoss:F4}) 돌파 → SHORT 포기");
                        return (false, "SL_BREACHED");
                    }

                    // 리테스트 없이 바로 하락 시작하면 진입
                    if (retestZoneReached && currentPrice < retestLow * 0.998m)
                    {
                        OnLog?.Invoke($"✅ [꼬리 리테스트] {symbol} 리테스트 후 하락 재개({currentPrice:F4}) → SHORT 트리거!");
                        return (true, $"RETEST_DROP price={currentPrice:F4}");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [꼬리 리테스트] {symbol} 오류: {ex.Message}");
                }

                await Task.Delay(5000, token);
            }

            OnLog?.Invoke($"⏰ [꼬리 리테스트] {symbol} 시간 초과 ({maxWaitSeconds}s)");
            return (false, "TIMEOUT");
        }

        private static (double macd, double signal, double hist) CalculateMACD(
            List<IBinanceKline> candles, int fast = 12, int slow = 26, int signalPeriod = 9)
        {
            if (candles.Count < slow + signalPeriod) return (0, 0, 0);

            var closes = candles.Select(k => (double)k.ClosePrice).ToArray();
            double[] emaFast = EMA(closes, fast);
            double[] emaSlow = EMA(closes, slow);

            double[] macdLine = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++)
                macdLine[i] = emaFast[i] - emaSlow[i];

            double[] signalLine = EMA(macdLine, signalPeriod);

            double m = macdLine[^1];
            double s = signalLine[^1];
            return (m, s, m - s);
        }

        private static double[] EMA(double[] data, int period)
        {
            double[] ema = new double[data.Length];
            double multiplier = 2.0 / (period + 1);
            ema[0] = data[0];
            for (int i = 1; i < data.Length; i++)
                ema[i] = (data[i] - ema[i - 1]) * multiplier + ema[i - 1];
            return ema;
        }

        private static double CalculateRSI(List<IBinanceKline> candles, int period = 14)
        {
            if (candles.Count < period + 1) return 50;

            double gainSum = 0, lossSum = 0;
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                double diff = (double)(candles[i].ClosePrice - candles[i - 1].ClosePrice);
                if (diff > 0) gainSum += diff; else lossSum += Math.Abs(diff);
            }
            double avgGain = gainSum / period;
            double avgLoss = lossSum / period;
            if (avgLoss == 0) return 100;
            double rs = avgGain / avgLoss;
            return 100 - (100 / (1 + rs));
        }

        /// <summary>
        /// [v4.5.2] 1분봉 ATR(14) 계산
        /// </summary>
        private static double CalculateATR(List<IBinanceKline> candles, int period = 14)
        {
            if (candles.Count < period + 1) return 0;
            double sum = 0;
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                double h = (double)candles[i].HighPrice;
                double l = (double)candles[i].LowPrice;
                double pc = (double)candles[i - 1].ClosePrice;
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                sum += tr;
            }
            return sum / period;
        }

        /// <summary>
        /// [v4.5.2] 최근 N봉 평균 |히스토그램| (크로스 강도 정규화용)
        /// </summary>
        private static double CalculateAvgAbsHistogram(List<IBinanceKline> candles, int lookback)
        {
            if (candles.Count < 26 + 9 + lookback) return 0;
            var closes = candles.Select(k => (double)k.ClosePrice).ToArray();
            double[] emaFast = EMA(closes, 12);
            double[] emaSlow = EMA(closes, 26);
            double[] macdLine = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++)
                macdLine[i] = emaFast[i] - emaSlow[i];
            double[] signalLine = EMA(macdLine, 9);

            double sum = 0;
            int start = Math.Max(0, closes.Length - lookback);
            int count = 0;
            for (int i = start; i < closes.Length; i++)
            {
                sum += Math.Abs(macdLine[i] - signalLine[i]);
                count++;
            }
            return count > 0 ? sum / count : 0;
        }

        /// <summary>
        /// [v4.5.2] 최근 N봉 내 MACD 크로스 횟수 (kline에서 직접 계산)
        /// </summary>
        private static int CountRecentCrosses(List<IBinanceKline> candles, int lookback)
        {
            if (candles.Count < 26 + 9 + lookback + 1) return 0;
            var closes = candles.Select(k => (double)k.ClosePrice).ToArray();
            double[] emaFast = EMA(closes, 12);
            double[] emaSlow = EMA(closes, 26);
            double[] macdLine = new double[closes.Length];
            for (int i = 0; i < closes.Length; i++)
                macdLine[i] = emaFast[i] - emaSlow[i];
            double[] signalLine = EMA(macdLine, 9);

            int crossCount = 0;
            int start = Math.Max(1, closes.Length - lookback);
            for (int i = start; i < closes.Length; i++)
            {
                double prevDiff = macdLine[i - 1] - signalLine[i - 1];
                double currDiff = macdLine[i] - signalLine[i];
                if ((prevDiff > 0 && currDiff <= 0) || (prevDiff < 0 && currDiff >= 0))
                    crossCount++;
            }
            return crossCount;
        }
    }

    public enum MacdCrossType { None, Golden, Dead, HistPeakOut, HistBottomOut }

    /// <summary>
    /// [v4.5.2] 크로스 이력 항목
    /// </summary>
    public class CrossHistoryEntry
    {
        public DateTime Time { get; set; }
        public MacdCrossType CrossType { get; set; }
        public double MacdLine { get; set; }
        public double SignalLine { get; set; }
        public double RSI { get; set; }
    }

    /// <summary>
    /// [v4.5.2] 휩소 ML 피처
    /// </summary>
    public class MacdWhipsawFeatures
    {
        public int CrossFlipCount5m { get; set; }           // 5분 내 방향 전환 횟수
        public float SecondsSinceOppositeCross { get; set; } = 600f; // 직전 반대 크로스 경과초 (600=없음)
    }

    public class MacdCrossResult
    {
        public bool Detected { get; set; }
        public MacdCrossType CrossType { get; set; }
        public string CaseType { get; set; } = "";  // "A" or "B"
        public double MacdLine { get; set; }
        public double SignalLine { get; set; }
        public double Histogram { get; set; }
        public double HistChangeRate { get; set; }
        public double DeadCrossAngle { get; set; }  // (MACD-Sig)[t] - (MACD-Sig)[t-1] — 음수일수록 급하락
        public double RSI { get; set; }
        // [v4.5.2] ML 피처용 필드
        public double SignalGapRatio { get; set; }     // |MACD-Signal| / ATR(14)
        public double HistogramStrength { get; set; }  // |hist| / avg|hist|
        public int RecentCrossCount { get; set; }      // 최근 10봉 내 크로스 횟수
        public MacdWhipsawFeatures WhipsawFeatures { get; set; } = new();
        public string Detail { get; set; } = "";

        public static MacdCrossResult None(string detail) => new() { Detail = detail };
    }

    public class BearishTailResult
    {
        public bool Detected { get; set; }
        public float UpperShadowRatio { get; set; }   // 0~1, 0.6+ = 강한 꼬리
        public float RelativeVolume { get; set; }      // 평균 대비 거래량 배수
        public bool Is15mMacdBearish { get; set; }
        public decimal CandleHigh { get; set; }        // 손절선 = 이 값 바로 위
        public decimal CandleClose { get; set; }
        public decimal RetestTarget50 { get; set; }    // 꼬리 50% 리테스트 가격
        public decimal RetestTarget618 { get; set; }   // 꼬리 61.8% 리테스트 가격
        public string Detail { get; set; } = "";

        public static BearishTailResult None(string detail) => new() { Detail = detail };
    }
}
