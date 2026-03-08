using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Services.AI;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 멀티 전략 하이브리드 Transformer 전략
    /// ─────────────────────────────────────────────
    /// AI(Transformer)가 "어디로 갈지" 결정 → 지표 로직이 "지금 들어가도 안전한지" 최종 승인
    /// 이중 검증(Double-Check) 구조로 20배 레버리지 진입
    /// </summary>
    public class TransformerStrategy
    {
        private readonly IBinanceRestClient _client;
        private readonly TransformerTrainer _trainer;
        private readonly NewsSentimentService _newsService;
        private readonly ElliottWave3WaveStrategy? _elliotWave3Strategy;
        private readonly PatternMemoryService? _patternMemoryService;
        private readonly TransformerSettings _settings;
        private readonly HybridStrategyScorer _hybridScorer = new();
        private bool _modelNotReadyLogged;
        private readonly object _signalHistoryLock = new object();
        private readonly List<string> _recentSignalDirections = new List<string>();
        private readonly ConcurrentDictionary<string, DateTime> _lastAnalyzeTimes = new(StringComparer.OrdinalIgnoreCase);

        // 이벤트 정의
        public event Action<string, string, decimal, decimal, string, decimal, decimal, PatternSnapshotInput?>? OnTradeSignal;
        public event Action<string, decimal, decimal>? OnPredictionUpdated;
        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string>? OnLog;

        // [설정값 튜닝]
        private const int RequiredHistory = 240;
        private const int SignalHistoryLimit = 100;
        private const double TargetShortRatio = 0.30;
        private static readonly TimeSpan AnalyzeMinInterval = TimeSpan.FromSeconds(3);
        private const int IndicatorLookbackWindow = 80;

        // [상위 타임프레임 페널티 캐시] — 15분봉/1시간봉 조회 빈도 제한
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime timestamp, double penalty, string reason)> _htfPenaltyCache
            = new();
        private static readonly TimeSpan HtfCacheDuration = TimeSpan.FromMinutes(3);

        public TransformerStrategy(
            IBinanceRestClient client,
            TransformerTrainer trainer,
            NewsSentimentService newsService,
            ElliottWave3WaveStrategy? elliotWave3Strategy = null,
            TransformerSettings? settings = null,
            PatternMemoryService? patternMemoryService = null)
        {
            _client = client;
            _trainer = trainer;
            _newsService = newsService;
            _elliotWave3Strategy = elliotWave3Strategy;
            _settings = settings ?? new TransformerSettings();
            _patternMemoryService = patternMemoryService;
        }

        public async Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var shouldAnalyze = false;
                _lastAnalyzeTimes.AddOrUpdate(
                    symbol,
                    _ =>
                    {
                        shouldAnalyze = true;
                        return nowUtc;
                    },
                    (_, previousUtc) =>
                    {
                        if ((nowUtc - previousUtc) >= AnalyzeMinInterval)
                        {
                            shouldAnalyze = true;
                            return nowUtc;
                        }

                        return previousUtc;
                    });

                if (!shouldAnalyze)
                    return;

                // 1. ═══ [메인 분석] 15분봉 기반으로 전체 방향성/AI 예측 수행 ═══
                // 15분봉은 노이즈가 적고 추세가 견고하며, 20배 레버리지에서 수수료 대비 수익 극대화
                var klinesResult = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, limit: RequiredHistory, ct: token);
                if (!klinesResult.Success || klinesResult.Data.Count() < RequiredHistory) return;

                var klines = klinesResult.Data.ToList();

                // 2. 데이터 전처리
                var candleDataList = await ConvertToCandleDataAsync(klines, symbol, token);
                if (candleDataList.Count == 0) return;

                // 2.5. 변동성 체크 (15분봉 ATR 기반 동적 임계값)
                var lastCandle = candleDataList[candleDataList.Count - 1];
                double currentAtr = lastCandle.ATR;
                double dynamicThreshold = CalculateDynamicThreshold(currentPrice, currentAtr);
                OnLog?.Invoke($"📊 [15분봉 변동성] {symbol} ATR%: {(currentAtr / (double)currentPrice * 100):F2}% | 진입 임계: {dynamicThreshold}점");

                // 3. Transformer 시퀀스 준비
                var requiredSeqLen = _trainer.SeqLen;
                var recentSequence = candleDataList.Skip(Math.Max(0, candleDataList.Count - requiredSeqLen)).Take(requiredSeqLen).ToList();

                if (recentSequence.Count != requiredSeqLen) return;

                if (!_trainer.IsModelReady)
                {
                    _trainer.LoadModel();
                    if (!_trainer.IsModelReady)
                    {
                        if (!_modelNotReadyLogged) { OnLog?.Invoke("⚠️ 모델 준비 대기 중..."); _modelNotReadyLogged = true; }
                        return;
                    }
                }
                _modelNotReadyLogged = false;

                // 4. AI 예측 수행
                float predictedPriceFloat;
                try
                {
                    predictedPriceFloat = _trainer.Predict(recentSequence);
                }
                catch (InvalidOperationException)
                {
                    // 모델 미준비 상태 — 이미 위에서 IsModelReady 체크했으나 경합 상태 가능
                    if (!_modelNotReadyLogged) { OnLog?.Invoke("⚠️ 모델 준비 대기 중..."); _modelNotReadyLogged = true; }
                    return;
                }
                catch (ArgumentException argEx)
                {
                    // 시퀀스 길이 불일치 등 데이터 문제 — 반복 로깅 방지
                    OnLog?.Invoke($"ℹ️ [{symbol}] 예측 입력 데이터 부적합: {argEx.Message}");
                    return;
                }

                decimal predictedPrice = (decimal)predictedPriceFloat;
                try
                {
                    OnPredictionUpdated?.Invoke(symbol, currentPrice, predictedPrice);
                }
                catch (Exception eventEx)
                {
                    OnLog?.Invoke($"⚠️ [TransformerStrategy] {symbol} 예측 이벤트 오류: {eventEx.Message}");
                }

                if (predictedPrice <= 0) return;

                decimal predictedChange = currentPrice > 0 ? (predictedPrice - currentPrice) / currentPrice : 0;

                // 5. 기술적 컨텍스트 구성
                var technicalCtx = BuildTechnicalContext(symbol, klines, currentPrice);

                // 6. ADX 기반 모드 판별 (횡보/추세)
                var (adx, plusDi, minusDi) = IndicatorCalculator.CalculateADX(klines, _settings.AdxPeriod);
                bool isSidewaysMarket = adx < _settings.AdxSidewaysThreshold;

                // 6.5. ═══════ 하이브리드 멀티 전략 스코어링 ═══════
                var longResult = _hybridScorer.EvaluateLong(symbol, predictedChange, predictedPrice, technicalCtx);
                var shortResult = _hybridScorer.EvaluateShort(symbol, predictedChange, predictedPrice, technicalCtx);

                // 메이저코인 특수 보정 (BTC/ETH/XRP/SOL)
                if (HybridStrategyScorer.IsMajorCoin(symbol))
                {
                    double longBonus = HybridStrategyScorer.GetMajorCoinBonus(symbol, "LONG", technicalCtx);
                    double shortBonus = HybridStrategyScorer.GetMajorCoinBonus(symbol, "SHORT", technicalCtx);
                    longResult.FinalScore = Math.Clamp(longResult.FinalScore + longBonus, 0, 100);
                    shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + shortBonus, 0, 100);
                }

                // 6.6. ═══════ 상위 타임프레임 페널티 (15분봉/1시간봉 저항) ═══════
                double htfPenalty = await CalculateHigherTimeframePenaltyAsync(symbol, currentPrice, predictedChange > 0 ? "LONG" : "SHORT", token);
                if (htfPenalty != 0)
                {
                    longResult.FinalScore = Math.Clamp(longResult.FinalScore + htfPenalty, 0, 100);
                    shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + htfPenalty, 0, 100);
                }

                bool longCondition = false;
                bool shortCondition = false;
                string mode = isSidewaysMarket ? "SIDEWAYS" : "TREND";
                string decisionForUi = "WAIT";
                decimal customTakeProfitPrice = 0m;
                decimal customStopLossPrice = 0m;
                bool isXrpSymbol = symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);
                var longPatternInput = BuildPatternSnapshotInput(symbol, "LONG", mode, currentPrice, predictedPrice, predictedChange, longResult, shortResult, technicalCtx, adx, plusDi, minusDi, currentAtr, htfPenalty);
                var shortPatternInput = BuildPatternSnapshotInput(symbol, "SHORT", mode, currentPrice, predictedPrice, predictedChange, shortResult, longResult, technicalCtx, adx, plusDi, minusDi, currentAtr, htfPenalty);

                PatternMatchDecision longPatternDecision = PatternMatchDecision.None;
                PatternMatchDecision shortPatternDecision = PatternMatchDecision.None;
                if (_patternMemoryService != null && _settings.PatternMatchingEnabled)
                {
                    longPatternDecision = await _patternMemoryService.EvaluateEntryAsync(longPatternInput, _settings, token);
                    shortPatternDecision = await _patternMemoryService.EvaluateEntryAsync(shortPatternInput, _settings, token);
                    longPatternInput.Match = longPatternDecision;
                    shortPatternInput.Match = shortPatternDecision;

                    if (longPatternDecision.ScoreBoost > 0)
                    {
                        longResult.FinalScore = Math.Clamp(longResult.FinalScore + longPatternDecision.ScoreBoost, 0, 100);
                        longPatternInput.FinalScore = longResult.FinalScore;
                    }

                    if (shortPatternDecision.ScoreBoost > 0)
                    {
                        shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + shortPatternDecision.ScoreBoost, 0, 100);
                        shortPatternInput.FinalScore = shortResult.FinalScore;
                    }

                    if (longPatternDecision.TopSimilarity >= _settings.PatternSimilarityThreshold)
                    {
                        OnLog?.Invoke($"🧠 [Pattern LONG] {symbol} sim:{longPatternDecision.TopSimilarity:P1} prob:{longPatternDecision.MatchProbability:P1} super:{longPatternDecision.IsSuperEntry}");
                    }
                    if (shortPatternDecision.TopSimilarity >= _settings.PatternSimilarityThreshold)
                    {
                        OnLog?.Invoke($"🧠 [Pattern SHORT] {symbol} sim:{shortPatternDecision.TopSimilarity:P1} prob:{shortPatternDecision.MatchProbability:P1} super:{shortPatternDecision.IsSuperEntry}");
                    }

                    if (longPatternDecision.ShouldDeferEntry)
                    {
                        OnLog?.Invoke($"⏸️ [Pattern HOLD LONG] {symbol} 손절 유사 패턴 감지로 진입 보류 | {longPatternDecision.DeferReason}");
                    }
                    if (shortPatternDecision.ShouldDeferEntry)
                    {
                        OnLog?.Invoke($"⏸️ [Pattern HOLD SHORT] {symbol} 손절 유사 패턴 감지로 진입 보류 | {shortPatternDecision.DeferReason}");
                    }
                }

                bool longPatternBypass = longPatternDecision.GateBypass && !longPatternDecision.ShouldDeferEntry;
                bool shortPatternBypass = shortPatternDecision.GateBypass && !shortPatternDecision.ShouldDeferEntry;

                if (isSidewaysMarket)
                {
                    OnLog?.Invoke($"⚖️ [횡보장 모드] {symbol} ADX:{adx:F1} (+DI:{plusDi:F1}/-DI:{minusDi:F1}) | 박스권 핑퐁 대기");

                    double bbPos = 0.5;
                    double bbRangeForEntry = technicalCtx.BbUpper - technicalCtx.BbLower;
                    if (bbRangeForEntry > 0)
                    {
                        bbPos = ((double)currentPrice - technicalCtx.BbLower) / bbRangeForEntry;
                        bbPos = Math.Clamp(bbPos, 0.0, 1.0);
                    }

                    longCondition =
                        currentPrice <= (decimal)technicalCtx.BbLower * _settings.SidewaysLongLowerBandTouchMultiplier &&
                        technicalCtx.RSI <= _settings.SidewaysRsiLongMax &&
                        predictedChange > 0 &&
                        technicalCtx.VolumeRatio < _settings.SidewaysVolumeRatioMax;

                    shortCondition =
                        currentPrice >= (decimal)technicalCtx.BbUpper * _settings.SidewaysShortUpperBandTouchMultiplier &&
                        technicalCtx.RSI >= _settings.SidewaysRsiShortMin &&
                        predictedChange < 0 &&
                        technicalCtx.VolumeRatio < _settings.SidewaysVolumeRatioMax;

                    if (isXrpSymbol)
                    {
                        bool xrpLongRelaxed =
                            predictedChange >= 0.001m &&
                            technicalCtx.RSI <= 55 &&
                            currentPrice <= (decimal)technicalCtx.BbMid * 1.003m &&
                            bbPos <= 0.45 &&
                            technicalCtx.VolumeRatio < 1.8;

                        bool xrpShortRelaxed =
                            predictedChange <= -0.001m &&
                            technicalCtx.RSI >= 35 &&
                            currentPrice >= (decimal)technicalCtx.BbMid * 0.997m &&
                            bbPos >= 0.55 &&
                            technicalCtx.VolumeRatio < 1.8;

                        if (xrpLongRelaxed && !longCondition)
                        {
                            OnLog?.Invoke($"⚡ [XRP SIDEWAYS LONG 완화] {symbol} Pred:{predictedChange * 100:F2}% RSI:{technicalCtx.RSI:F1} Vol:{technicalCtx.VolumeRatio:F2}x");
                        }
                        if (xrpShortRelaxed && !shortCondition)
                        {
                            OnLog?.Invoke($"⚡ [XRP SIDEWAYS SHORT 완화] {symbol} Pred:{predictedChange * 100:F2}% RSI:{technicalCtx.RSI:F1} Vol:{technicalCtx.VolumeRatio:F2}x");
                        }

                        longCondition = longCondition || xrpLongRelaxed;
                        shortCondition = shortCondition || xrpShortRelaxed;
                    }

                    if (longCondition && longPatternDecision.ShouldDeferEntry)
                    {
                        longCondition = false;
                        OnLog?.Invoke($"⛔ [LONG 보류] {symbol} 손절 유사 패턴으로 횡보 LONG 진입 취소");
                    }

                    if (shortCondition && shortPatternDecision.ShouldDeferEntry)
                    {
                        shortCondition = false;
                        OnLog?.Invoke($"⛔ [SHORT 보류] {symbol} 손절 유사 패턴으로 횡보 SHORT 진입 취소");
                    }

                    if (!longCondition && longPatternBypass && predictedChange > 0)
                    {
                        longCondition = true;
                        OnLog?.Invoke($"⚡ [Pattern SUPER LONG] {symbol} 횡보 게이트 우회 진입 허용");
                    }

                    if (!shortCondition && shortPatternBypass && predictedChange < 0)
                    {
                        shortCondition = true;
                        OnLog?.Invoke($"⚡ [Pattern SUPER SHORT] {symbol} 횡보 게이트 우회 진입 허용");
                    }

                    if (longCondition)
                    {
                        decisionForUi = "LONG";
                        customTakeProfitPrice = (decimal)technicalCtx.BbMid;
                        customStopLossPrice = (decimal)technicalCtx.BbLower * _settings.SidewaysLongStopLossMultiplier;
                    }
                    else if (shortCondition)
                    {
                        decisionForUi = "SHORT";
                        customTakeProfitPrice = (decimal)technicalCtx.BbMid;
                        customStopLossPrice = (decimal)technicalCtx.BbUpper * _settings.SidewaysShortStopLossMultiplier;
                    }
                }
                else
                {
                    OnLog?.Invoke($"🔥 [추세장 모드] {symbol} ADX:{adx:F1} (+DI:{plusDi:F1}/-DI:{minusDi:F1}) | 엘리엇+Transformer 가동");

                    bool longStrongOverride = longResult.FinalScore >= (dynamicThreshold + 3.0) && predictedChange >= 0.0035m;
                    bool shortStrongOverride = shortResult.FinalScore >= (dynamicThreshold + 3.0) && predictedChange <= -0.0035m;
                    bool xrpTrendLongOverride =
                        isXrpSymbol &&
                        predictedChange >= 0.0035m &&
                        longResult.AiPredictionScore >= 30.0 &&
                        longResult.BollingerScore >= 8.0 &&
                        longResult.RsiMacdScore >= 5.0 &&
                        longResult.FinalScore >= 45.0 &&
                        (longResult.FinalScore - shortResult.FinalScore) >= 12.0;

                    bool safeToLong = plusDi >= minusDi || longStrongOverride || xrpTrendLongOverride || longPatternBypass;
                    bool safeToShort = minusDi > plusDi || shortStrongOverride || shortPatternBypass;

                    if (longStrongOverride && plusDi < minusDi)
                    {
                        OnLog?.Invoke($"⚠️ [LONG 예외허용] {symbol} 강신호로 ADX 방향 불일치 1회 허용 | Score:{longResult.FinalScore:F1} Pred:{predictedChange * 100:F2}%");
                    }
                    if (xrpTrendLongOverride)
                    {
                        OnLog?.Invoke($"⚡ [XRP TREND LONG 완화] {symbol} Score:{longResult.FinalScore:F1}/{dynamicThreshold:F0} Pred:{predictedChange * 100:F2}% | AI:{longResult.AiPredictionScore:F0} RSI:{longResult.RsiMacdScore:F0} BB:{longResult.BollingerScore:F0}");
                    }
                    if (shortStrongOverride && minusDi <= plusDi)
                    {
                        OnLog?.Invoke($"⚠️ [SHORT 예외허용] {symbol} 강신호로 ADX 방향 불일치 1회 허용 | Score:{shortResult.FinalScore:F1} Pred:{predictedChange * 100:F2}%");
                    }

                    bool longThresholdPassed = longResult.FinalScore >= dynamicThreshold || xrpTrendLongOverride || longPatternBypass;
                    if (longThresholdPassed && longResult.FinalScore > shortResult.FinalScore && safeToLong && !longPatternDecision.ShouldDeferEntry)
                    {
                        // ── 컴포넌트 최소점수 게이트 검증 ──
                        bool longGatePassed = longResult.PassesComponentGate(out var longGateFail);
                        if (longGatePassed || xrpTrendLongOverride || longPatternBypass)
                        {
                            if (!longGatePassed && xrpTrendLongOverride)
                            {
                                OnLog?.Invoke($"⚡ [XRP LONG 게이트 우회] {symbol} | {longGateFail}");
                            }
                            else if (!longGatePassed && longPatternBypass)
                            {
                                OnLog?.Invoke($"⚡ [Pattern LONG 게이트 우회] {symbol} | sim:{longPatternDecision.TopSimilarity:P1} prob:{longPatternDecision.MatchProbability:P1}");
                            }
                            longCondition = true;
                            decisionForUi = "LONG";
                        }
                        else
                        {
                            OnLog?.Invoke($"🚫 [LONG 게이트 FAIL] {symbol} | Score:{longResult.FinalScore:F1} 통과했으나 컴포넌트 미달: {longGateFail}");
                        }
                    }
                    else
                    {
                        // ── LONG 진입 조건 실패 상세 진단 ──
                        string failReason = "";
                        if (longPatternDecision.ShouldDeferEntry)
                        {
                            failReason = $"패턴 보류: {longPatternDecision.DeferReason}";
                        }
                        else if (!xrpTrendLongOverride && !longPatternBypass && longResult.FinalScore < dynamicThreshold)
                        {
                            failReason = $"Score부족: {longResult.FinalScore:F1}<{dynamicThreshold:F0} (부족:{dynamicThreshold - longResult.FinalScore:F1})";
                        }
                        else if (longResult.FinalScore <= shortResult.FinalScore)
                        {
                            failReason = $"SHORT우위: {longResult.FinalScore:F1} vs SHORT {shortResult.FinalScore:F1}";
                        }
                        else if (!safeToLong)
                        {
                            failReason = $"ADX방향: +DI({plusDi:F1}) < -DI({minusDi:F1}) (약세추세)";
                        }

                        // 로그 최소화: 같은 심볼 반복 로그 방지 (60초마다)
                        string logKey = $"{symbol}_LONG_FAIL_{DateTime.UtcNow:HHmm}";
                        OnLog?.Invoke($"⚠️ [LONG 진입 거부] {symbol} | 이유: {failReason} | 현재가:{currentPrice:F2}");
                    }

                    bool shortThresholdPassed = shortResult.FinalScore >= dynamicThreshold || shortPatternBypass;
                    if (shortThresholdPassed && shortResult.FinalScore > longResult.FinalScore && safeToShort && !shortPatternDecision.ShouldDeferEntry)
                    {
                        if (CanEmitShortSignal(shortResult.FinalScore, predictedChange))
                        {
                            // ── 컴포넌트 최소점수 게이트 검증 ──
                            bool shortGatePassed = shortResult.PassesComponentGate(out var shortGateFail);
                            if (shortGatePassed || shortPatternBypass)
                            {
                                if (!shortGatePassed && shortPatternBypass)
                                {
                                    OnLog?.Invoke($"⚡ [Pattern SHORT 게이트 우회] {symbol} | sim:{shortPatternDecision.TopSimilarity:P1} prob:{shortPatternDecision.MatchProbability:P1}");
                                }
                                shortCondition = true;
                                decisionForUi = "SHORT";
                            }
                            else
                            {
                                OnLog?.Invoke($"🚫 [SHORT 게이트 FAIL] {symbol} | Score:{shortResult.FinalScore:F1} 통과했으나 컴포넌트 미달: {shortGateFail}");
                            }
                        }
                    }
                    else
                    {
                        // ── SHORT 진입 조건 실패 상세 진단 ──
                        string failReason = "";
                        if (shortPatternDecision.ShouldDeferEntry)
                        {
                            failReason = $"패턴 보류: {shortPatternDecision.DeferReason}";
                        }
                        else if (!shortPatternBypass && shortResult.FinalScore < dynamicThreshold)
                        {
                            failReason = $"Score부족: {shortResult.FinalScore:F1}<{dynamicThreshold:F0} (부족:{dynamicThreshold - shortResult.FinalScore:F1})";
                        }
                        else if (shortResult.FinalScore <= longResult.FinalScore)
                        {
                            failReason = $"LONG우위: {shortResult.FinalScore:F1} vs LONG {longResult.FinalScore:F1}";
                        }
                        else if (!safeToShort)
                        {
                            failReason = $"ADX방향: -DI({minusDi:F1}) <= +DI({plusDi:F1}) (강세추세)";
                        }
                        else if (!CanEmitShortSignal(shortResult.FinalScore, predictedChange))
                        {
                            failReason = $"단기매도제약: {shortResult.FinalScore:F1}점 (매매 안전장치)";
                        }

                        OnLog?.Invoke($"⚠️ [SHORT 진입 거부] {symbol} | 이유: {failReason} | 현재가:{currentPrice:F2}");
                    }

                    if (decisionForUi == "WAIT")
                    {
                        decisionForUi = GetUiDecision(longResult, shortResult);
                    }
                }

                // 분석 데이터 UI 전송
                try
                {
                    OnSignalAnalyzed?.Invoke(new MultiTimeframeViewModel
                    {
                        Symbol = symbol,
                        LastPrice = currentPrice,
                        RSI_1H = technicalCtx.RSI,
                        AIScore = (float)Math.Max(longResult.FinalScore, shortResult.FinalScore),
                        Decision = decisionForUi,
                        StrategyName = isSidewaysMarket ? "Hybrid AI Sideways(15m+5m)" : "Hybrid AI Trend(15m+5m)",
                        SignalSource = "TRANSFORMER",
                        ShortLongScore = longResult.FinalScore,
                        ShortShortScore = shortResult.FinalScore,
                        MacdHist = technicalCtx.MacdHist,
                        ElliottTrend = technicalCtx.IsElliottUptrend ? "UP" : "DOWN",
                        MAState = technicalCtx.Sma20 > technicalCtx.Sma50 && technicalCtx.Sma50 > technicalCtx.Sma200 ? "BULL" :
                                  (technicalCtx.Sma20 < technicalCtx.Sma50 && technicalCtx.Sma50 < technicalCtx.Sma200 ? "BEAR" : "MIX"),
                        FibPosition = currentPrice < technicalCtx.Fib618 ? "BELOW618" : "ABOVE618",
                        VolumeRatioValue = technicalCtx.VolumeRatio,
                        VolumeRatio = $"{technicalCtx.VolumeRatio:F2}x",
                        BBPosition = longResult.BBPosition,
                        TransformerPrice = predictedPrice,
                        TransformerChange = (double)(predictedChange * 100)
                    });
                }
                catch (Exception eventEx)
                {
                    OnLog?.Invoke($"⚠️ [TransformerStrategy] {symbol} 시그널 이벤트 오류: {eventEx.Message}");
                }

                // 7. ═══════ 최종 신호 발생 ═══════
                if (longCondition)
                {
                    // ══ [하이브리드 타임프레임] 15분봉 방향 확정 후 5분봉 타이밍 체크 ══
                    var (isGoodTiming, timingReason, bbPos5m) = await CheckFiveMinuteEntryTimingAsync(symbol, "LONG", token);
                    if (!isGoodTiming)
                    {
                        OnLog?.Invoke($"⏸️ [LONG 타이밍 대기] {symbol} | 15분봉 신호는 유효하나 5분봉 눌림목 미형성 | {timingReason}");
                        // 신호는 유효하지만 진입 타이밍이 아니므로 다음 루프까지 대기
                        return;
                    }

                    RegisterSignalDirection("LONG");
                    string majorTag = HybridStrategyScorer.IsMajorCoin(symbol) ? " [MAJOR]" : "";
                    if (isSidewaysMarket)
                    {
                        OnLog?.Invoke($"⚖️{majorTag} [SIDEWAYS LONG] {symbol} | ADX:{adx:F1} RSI:{technicalCtx.RSI:F1} Vol:{technicalCtx.VolumeRatio:F2}x | TP(mid):{customTakeProfitPrice:F8} SL:{customStopLossPrice:F8} | 5m타이밍: {timingReason}");
                    }
                    else
                    {
                        OnLog?.Invoke($"🚀{majorTag} [TREND LONG] {symbol} | 15분봉 Score: {longResult.FinalScore:F1}/{dynamicThreshold:F0} | 예측: {predictedChange * 100:F2}% | AI:{longResult.AiPredictionScore:F0} EW:{longResult.ElliottWaveScore:F0} Vol:{longResult.VolumeMomentumScore:F0} RSI/M:{longResult.RsiMacdScore:F0} BB:{longResult.BollingerScore:F0} | 5m타이밍: {timingReason}");
                    }
                    try
                    {
                        OnTradeSignal?.Invoke(symbol, "LONG", currentPrice, predictedPrice, mode, customTakeProfitPrice, customStopLossPrice, longPatternInput);
                    }
                    catch (Exception eventEx)
                    {
                        OnLog?.Invoke($"⚠️ [TransformerStrategy] {symbol} LONG 주문 이벤트 오류: {eventEx.Message}");
                    }
                }
                else if (shortCondition)
                {
                    // ══ [하이브리드 타임프레임] 15분봉 방향 확정 후 5분봉 타이밍 체크 ══
                    var (isGoodTiming, timingReason, bbPos5m) = await CheckFiveMinuteEntryTimingAsync(symbol, "SHORT", token);
                    if (!isGoodTiming)
                    {
                        OnLog?.Invoke($"⏸️ [SHORT 타이밍 대기] {symbol} | 15분봉 신호는 유효하나 5분봉 저항 미도달 | {timingReason}");
                        // 신호는 유효하지만 진입 타이밍이 아니므로 다음 루프까지 대기
                        return;
                    }

                    RegisterSignalDirection("SHORT");
                    string majorTag = HybridStrategyScorer.IsMajorCoin(symbol) ? " [MAJOR]" : "";
                    if (isSidewaysMarket)
                    {
                        OnLog?.Invoke($"⚖️{majorTag} [SIDEWAYS SHORT] {symbol} | ADX:{adx:F1} RSI:{technicalCtx.RSI:F1} Vol:{technicalCtx.VolumeRatio:F2}x | TP(mid):{customTakeProfitPrice:F8} SL:{customStopLossPrice:F8} | 5m타이밍: {timingReason}");
                    }
                    else
                    {
                        OnLog?.Invoke($"📉{majorTag} [TREND SHORT] {symbol} | 15분봉 Score: {shortResult.FinalScore:F1}/{dynamicThreshold:F0} | 예측: {predictedChange * 100:F2}% | AI:{shortResult.AiPredictionScore:F0} EW:{shortResult.ElliottWaveScore:F0} Vol:{shortResult.VolumeMomentumScore:F0} RSI/M:{shortResult.RsiMacdScore:F0} BB:{shortResult.BollingerScore:F0} | 5m타이밍: {timingReason}");
                    }
                    try
                    {
                        OnTradeSignal?.Invoke(symbol, "SHORT", currentPrice, predictedPrice, mode, customTakeProfitPrice, customStopLossPrice, shortPatternInput);
                    }
                    catch (Exception eventEx)
                    {
                        OnLog?.Invoke($"⚠️ [TransformerStrategy] {symbol} SHORT 주문 이벤트 오류: {eventEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ 오류 ({symbol}): {ex.Message}");
            }
        }

        /// <summary>
        /// [동적 임계값] 변동성(ATR)에 따라 60~75점 범위에서 진입 캻LINE 자동 조정
        /// </summary>
        private double CalculateDynamicThreshold(decimal currentPrice, double atr)
        {
            // 1. 현재 가격 대비 ATR 비율 계산 (5분봉 캠들 하나의 평균 변동폭)
            double atrPercentage = (atr / (double)currentPrice) * 100;

            // 2. 변동성 구간별 임계값 매핑
            if (atrPercentage < 0.15)
            {
                // [수렵 구간] 변동성 극도로 낮음 → 조만간 터질 3파를 잡기 위해 문턱 낮춤
                return 58.0;
            }
            else if (atrPercentage >= 0.15 && atrPercentage < 0.30)
            {
                // [일반 구간] 평이한 변동성 → 표준 임계값 적용
                return 63.0;
            }
            else if (atrPercentage >= 0.30 && atrPercentage < 0.50)
            {
                // [확장 구간] 변동성 증가 → 추격 매수 리스크가 있으므로 조건 강화
                return 68.0;
            }
            else
            {
                // [과열 구간] 0.5% 이상. 5분봉 하나가 위아래로 미친듯이 흔드는 구간
                // 20배 레버리지 보호를 위해 거의 모든 조건이 맞을 때만 진입 (가장 보수적)
                return 75.0;
            }
        }

        /// <summary>
        /// 기술적 컨텍스트 빌드 (HybridStrategyScorer 입력 형식)
        /// </summary>
        private HybridStrategyScorer.TechnicalContext BuildTechnicalContext(
            string symbol,
            List<Binance.Net.Interfaces.IBinanceKline> klines,
            decimal currentPrice)
        {
            // 지표 계산
            double rsi = IndicatorCalculator.CalculateRSI(klines, 14);
            var bb = IndicatorCalculator.CalculateBB(klines, 20, 2);
            var macd = IndicatorCalculator.CalculateMACD(klines);
            var fib = IndicatorCalculator.CalculateFibonacci(klines, 120);
            double sma20 = IndicatorCalculator.CalculateSMA(klines, 20);
            double sma50 = IndicatorCalculator.CalculateSMA(klines, 50);
            double sma200 = IndicatorCalculator.CalculateSMA(klines, 200);
            bool elliottUptrend = IndicatorCalculator.AnalyzeElliottWave(klines);

            // 거래량 모멘텀
            var recent20 = klines.TakeLast(20).ToList();
            if (recent20.Count == 0)
            {
                OnLog?.Invoke($"[TransformerStrategy] {symbol} 거래량 데이터 부족 (recent20 empty)");
                // 기본 TechnicalContext 반환
                return new HybridStrategyScorer.TechnicalContext
                {
                    CurrentPrice = currentPrice,
                    BbUpper = 0,
                    BbMid = 0,
                    BbLower = 0,
                    BbWidth = 0,
                    RSI = 50,
                    MacdHist = 0,
                    MacdLine = 0,
                    MacdSignal = 0,
                    Sma20 = 0,
                    Sma50 = 0,
                    Sma200 = 0,
                    IsElliottUptrend = false,
                    ElliottPhase = "Idle",
                    Fib382 = currentPrice,
                    Fib500 = currentPrice,
                    Fib618 = currentPrice,
                    VolumeRatio = 1,
                    VolumeMomentum = 1,
                    RsiDivergence = 0
                };
            }
            double avgVolume = recent20.Average(k => (double)k.Volume);
            double currentVolume = (double)recent20.Last().Volume;
            double prevVolume = recent20.Count >= 2 ? (double)recent20[^2].Volume : currentVolume;
            double volumeRatio = avgVolume > 0 ? currentVolume / avgVolume : 1;
            double volumeMomentum = prevVolume > 0 ? currentVolume / prevVolume : 1;

            // RSI 다이버전스 (5봉 전과 비교)
            double rsiDivergence = 0;
            if (klines.Count >= 6)
            {
                var prevSubset = klines.GetRange(0, klines.Count - 5);
                var prevRsi = IndicatorCalculator.CalculateRSI(prevSubset, 14);
                decimal priceDelta = currentPrice - klines[klines.Count - 6].ClosePrice;
                double rsiDelta = rsi - prevRsi;
                if (priceDelta > 0 && rsiDelta < 0) rsiDivergence = -1;  // 약세 다이버전스
                else if (priceDelta < 0 && rsiDelta > 0) rsiDivergence = 1; // 강세 다이버전스
            }

            // 엘리엇 3파 상태 확인
            string elliottPhase = "Idle";
            if (_elliotWave3Strategy != null)
            {
                var waveState = _elliotWave3Strategy.GetCurrentState(symbol);
                if (waveState != null)
                {
                    elliottPhase = waveState.CurrentPhase.ToString();
                }
            }

            double bbMid = (bb.Upper + bb.Lower) / 2.0;
            double bbWidth = bbMid > 0 ? (bb.Upper - bb.Lower) / bbMid * 100 : 0;

            return new HybridStrategyScorer.TechnicalContext
            {
                CurrentPrice = currentPrice,
                BbUpper = bb.Upper,
                BbMid = bbMid,
                BbLower = bb.Lower,
                BbWidth = bbWidth,
                RSI = rsi,
                MacdHist = macd.Hist,
                MacdLine = macd.Macd,
                MacdSignal = macd.Signal,
                Sma20 = sma20,
                Sma50 = sma50,
                Sma200 = sma200,
                IsElliottUptrend = elliottUptrend,
                ElliottPhase = elliottPhase,
                Fib382 = (decimal)fib.Level382,
                Fib500 = (decimal)fib.Level500,
                Fib618 = (decimal)fib.Level618,
                VolumeRatio = volumeRatio,
                VolumeMomentum = volumeMomentum,
                RsiDivergence = rsiDivergence,
            };
        }

        private PatternSnapshotInput BuildPatternSnapshotInput(
            string symbol,
            string side,
            string mode,
            decimal currentPrice,
            decimal predictedPrice,
            decimal predictedChange,
            HybridStrategyScorer.HybridScoreResult primary,
            HybridStrategyScorer.HybridScoreResult opposite,
            HybridStrategyScorer.TechnicalContext technicalCtx,
            double adx,
            double plusDi,
            double minusDi,
            double atr,
            double htfPenalty)
        {
            double bbPosition = 0.5;
            double bbRange = technicalCtx.BbUpper - technicalCtx.BbLower;
            if (bbRange > 0)
            {
                bbPosition = ((double)currentPrice - technicalCtx.BbLower) / bbRange;
                bbPosition = Math.Clamp(bbPosition, 0.0, 1.0);
            }

            string componentMix =
                $"AI({primary.AiPredictionScore:F1}),EW({primary.ElliottWaveScore:F1}),Vol({primary.VolumeMomentumScore:F1}),RSI/M({primary.RsiMacdScore:F1}),BB({primary.BollingerScore:F1})";

            string contextJson =
                $"{{\"atrPct\":{(currentPrice > 0 ? (atr / (double)currentPrice * 100) : 0):F4},\"htfPenalty\":{htfPenalty:F2},\"adx\":{adx:F2},\"plusDi\":{plusDi:F2},\"minusDi\":{minusDi:F2},\"bbPos\":{bbPosition:F4},\"rsi\":{technicalCtx.RSI:F2},\"volumeRatio\":{technicalCtx.VolumeRatio:F3}}}";

            return new PatternSnapshotInput
            {
                Symbol = symbol,
                Side = side,
                Mode = mode,
                Strategy = "TRANSFORMER_PATTERN",
                SignalTime = DateTime.UtcNow,
                CurrentPrice = currentPrice,
                PredictedPrice = predictedPrice,
                PredictedChange = predictedChange,
                FinalScore = primary.FinalScore,
                AiScore = primary.AiPredictionScore,
                ElliottScore = primary.ElliottWaveScore,
                VolumeScore = primary.VolumeMomentumScore,
                RsiMacdScore = primary.RsiMacdScore,
                BollingerScore = primary.BollingerScore,
                ScoreGap = primary.FinalScore - opposite.FinalScore,
                AtrPercent = currentPrice > 0 ? atr / (double)currentPrice * 100 : 0,
                HtfPenalty = htfPenalty,
                Adx = adx,
                PlusDi = plusDi,
                MinusDi = minusDi,
                Rsi = technicalCtx.RSI,
                MacdHist = technicalCtx.MacdHist,
                BbPosition = bbPosition,
                VolumeRatio = technicalCtx.VolumeRatio,
                ComponentMix = componentMix,
                ContextJson = contextJson
            };
        }

        private string GetUiDecision(HybridStrategyScorer.HybridScoreResult longResult, HybridStrategyScorer.HybridScoreResult shortResult)
        {
            if (longResult.IsApproved && longResult.FinalScore > shortResult.FinalScore) return "LONG";
            if (shortResult.IsApproved && shortResult.FinalScore > longResult.FinalScore) return "SHORT";
            if (longResult.FinalScore >= 55) return "LONG_CAND";
            if (shortResult.FinalScore >= 55) return "SHORT_CAND";
            return "WAIT";
        }

        // [비중 제어 로직 유지]
        private bool CanEmitShortSignal(double shortScore, decimal predictedChange)
        {
            lock (_signalHistoryLock)
            {
                int total = _recentSignalDirections.Count;
                int shortCount = _recentSignalDirections.Count(s => s == "SHORT");
                double projectedRatio = total > 0 ? (double)(shortCount + 1) / (total + 1) : 1.0;
                if (projectedRatio <= TargetShortRatio) return true;
                return shortScore >= 92 && predictedChange <= -0.015m;
            }
        }

        private void RegisterSignalDirection(string direction)
        {
            lock (_signalHistoryLock)
            {
                _recentSignalDirections.Add(direction);
                if (_recentSignalDirections.Count > SignalHistoryLimit) _recentSignalDirections.RemoveAt(0);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  상위 타임프레임 페널티 (15분봉/1시간봉 저항 감지)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 15분봉/1시간봉 데이터를 조회하여 상위 타임프레임 저항 페널티를 계산합니다.
        /// - 15분봉 상단 저항 (-20점): %B > 0.85 + 음봉
        /// - 1시간봉 역추세 (-15점): 가격이 1H SMA20(중심선) 아래
        /// - 횡보장 스퀴즈 (-10점): BB폭 극도로 좁은 상태 2시간(=24개 5분봉) 이상
        /// 결과는 3분간 캐시하여 API 호출 횟수를 제한합니다.
        /// </summary>
        private async Task<double> CalculateHigherTimeframePenaltyAsync(
            string symbol, decimal currentPrice, string direction, CancellationToken token)
        {
            // 캐시 확인 — 3분 이내 재계산 방지
            if (_htfPenaltyCache.TryGetValue(symbol, out var cached) &&
                (DateTime.UtcNow - cached.timestamp) < HtfCacheDuration)
            {
                return cached.penalty;
            }

            double totalPenalty = 0;
            var reasons = new List<string>();

            try
            {
                // ── 15분봉 조회 (최근 30개 — 저항 체크 + 스퀴즈 체크 통합) ──
                var klines15m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FifteenMinutes, limit: 30, ct: token);

                if (klines15m.Success && klines15m.Data.Count() >= 20)
                {
                    var data15m = klines15m.Data.ToList();
                    var bb15m = IndicatorCalculator.CalculateBB(data15m, 20, 2);
                    var lastCandle15m = data15m.Last();

                    // %B 계산: (현재가 - 하단) / (상단 - 하단)
                    double bbRange15m = bb15m.Upper - bb15m.Lower;
                    double percentB15m = bbRange15m > 0 ? ((double)currentPrice - bb15m.Lower) / bbRange15m : 0.5;

                    // 15분봉 상단 저항: %B > 0.85 이고 직전 15분봉이 음봉
                    bool isBearish15m = lastCandle15m.ClosePrice < lastCandle15m.OpenPrice;

                    if (direction == "LONG" && percentB15m > 0.85 && isBearish15m)
                    {
                        totalPenalty -= 12;
                        reasons.Add($"15m상단저항(%B:{percentB15m:F2},음봉):-12");
                    }
                    else if (direction == "SHORT" && percentB15m < 0.15 && !isBearish15m)
                    {
                        // SHORT일 때는 15분봉 하단 지지가 저항
                        totalPenalty -= 12;
                        reasons.Add($"15m하단지지(%B:{percentB15m:F2},양봉):-12");
                    }

                    // ── 15분봉 횡보 스퀴즈 체크 (동일 데이터 재사용) ──
                    // 최근 8개 15분봉(=2시간) BB폭 확인
                    if (data15m.Count >= 8)
                    {
                        int squeezeCount = 0;
                        for (int i = Math.Max(0, data15m.Count - 8); i < data15m.Count; i++)
                        {
                            var subset = data15m.GetRange(0, i + 1);
                            if (subset.Count >= 20)
                            {
                                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                                double mid = (bb.Upper + bb.Lower) / 2.0;
                                double width = mid > 0 ? (bb.Upper - bb.Lower) / mid * 100 : 0;

                                // 15분봉은 5분봉보다 변동이 크므로 스퀴즈 임계값도 상향 (0.5% → 0.8%)
                                if (width < 0.8)
                                    squeezeCount++;
                            }
                        }

                        // 8개 중 6개 이상(≈75%)이 스퀴즈 상태면 횡보장 판단
                        if (squeezeCount >= 6)
                        {
                            totalPenalty -= 6;
                            reasons.Add($"횡보스퀴즈(15m,2h+,{squeezeCount}/8봉):-6");
                        }
                    }
                }

                // ── 1시간봉 조회 (최근 20개) ──
                var klines1h = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.OneHour, limit: 20, ct: token);

                if (klines1h.Success && klines1h.Data.Count() >= 20)
                {
                    var data1h = klines1h.Data.ToList();
                    double sma20_1h = IndicatorCalculator.CalculateSMA(data1h, 20);

                    // 1시간봉 역추세: LONG인데 가격이 1H SMA20 아래
                    if (direction == "LONG" && (double)currentPrice < sma20_1h)
                    {
                        totalPenalty -= 8;
                        reasons.Add($"1h역추세(가격<SMA20:{sma20_1h:F2}):-8");
                    }
                    else if (direction == "SHORT" && (double)currentPrice > sma20_1h)
                    {
                        // SHORT인데 1H SMA20 위에 있으면 역추세
                        totalPenalty -= 8;
                        reasons.Add($"1h역추세(가격>SMA20:{sma20_1h:F2}):-8");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [HTF 페널티] {symbol} 조회 실패: {ex.Message}");
            }

            // 캐시 저장
            string reasonStr = reasons.Count > 0 ? string.Join(" | ", reasons) : "없음";
            _htfPenaltyCache[symbol] = (DateTime.UtcNow, totalPenalty, reasonStr);

            if (totalPenalty < 0)
            {
                OnLog?.Invoke($"⛔ [HTF 페널티] {symbol} {direction} | 감점: {totalPenalty:F0}점 | {reasonStr}");
            }

            return totalPenalty;
        }

        /// <summary>
        /// [진입 타이밍 정밀 체크] 15분봉에서 방향이 결정되었다면, 5분봉 눌림목에서 실제 진입
        /// 20배 레버리지에서 수수료/슬리피지를 최소화하기 위해 정밀 타이밍만 5분봉 사용
        /// </summary>
        public async Task<(bool IsGoodEntry, string Reason, decimal BbPosition5m)> CheckFiveMinuteEntryTimingAsync(
            string symbol, 
            string direction, 
            CancellationToken token)
        {
            try
            {
                // 5분봉 최근 20개 조회 (BB 계산용)
                var klines5m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, KlineInterval.FiveMinutes, limit: 20, ct: token);

                if (!klines5m.Success || klines5m.Data.Count() < 20)
                    return (false, "5분봉 데이터 부족", 0m);

                var data5m = klines5m.Data.ToList();
                var bb5m = IndicatorCalculator.CalculateBB(data5m, 20, 2);
                decimal currentPrice = data5m[^1].ClosePrice;
                
                decimal bbRange = (decimal)(bb5m.Upper - bb5m.Lower);
                if (bbRange <= 0)
                    return (false, "5분봉 BB 계산 실패", 0m);

                decimal bbPosition = (currentPrice - (decimal)bb5m.Lower) / bbRange;

                if (direction == "LONG")
                {
                    // LONG: 중단 눌림목 (45~55% 구간)에서 진입
                    if (bbPosition >= 0.45m && bbPosition <= 0.55m)
                    {
                        return (true, $"5분봉 중단 눌림목 진입 타이밍 ({bbPosition:P0})", bbPosition);
                    }
                    else if (bbPosition < 0.45m)
                    {
                        return (false, $"5분봉 하단 과매도 대기 ({bbPosition:P0})", bbPosition);
                    }
                    else
                    {
                        return (false, $"5분봉 상단 추격 금지 ({bbPosition:P0})", bbPosition);
                    }
                }
                else if (direction == "SHORT")
                {
                    // SHORT: 상단 저항 (80~90% 구간)에서 진입
                    if (bbPosition >= 0.80m && bbPosition <= 0.90m)
                    {
                        return (true, $"5분봉 상단 저항 진입 타이밍 ({bbPosition:P0})", bbPosition);
                    }
                    else
                    {
                        return (false, $"5분봉 SHORT 타이밍 미충족 ({bbPosition:P0})", bbPosition);
                    }
                }

                return (false, "방향 미지정", bbPosition);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [5분봉 타이밍 체크] {symbol} 오류: {ex.Message}");
                return (false, $"조회 실패: {ex.Message}", 0m);
            }
        }

        private async Task<List<CandleData>> ConvertToCandleDataAsync(List<Binance.Net.Interfaces.IBinanceKline> klines, string symbol, CancellationToken token)
        {
            var result = new List<CandleData>();
            double sentiment = await _newsService.GetMarketSentimentAsync();

            for (int i = 50; i < klines.Count; i++)
            {
                // 대량 데이터 처리 시 주기적으로 양보 + 취소 반응성 확보
                if ((i - 50) % 32 == 0)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                int startIndex = Math.Max(0, i - IndicatorLookbackWindow);
                var subset = klines.GetRange(startIndex, i - startIndex + 1);
                var current = klines[i];
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);
                var atr = IndicatorCalculator.CalculateATR(subset, 14); // ATR 추가

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = (decimal)current.OpenPrice,
                    High = (decimal)current.HighPrice,
                    Low = (decimal)current.LowPrice,
                    Close = (decimal)current.ClosePrice,
                    Volume = (float)current.Volume,
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD_Hist = (float)macd.Hist,
                    Fib_618 = (float)fib.Level618,
                    SentimentScore = (float)sentiment,
                    ATR = (float)atr  // ATR 필드 추가 (float 형변환)
                });
            }
            return result;
        }
    }
}
