using System;
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
        private readonly TransformerSettings _settings;
        private readonly HybridStrategyScorer _hybridScorer = new();
        private bool _modelNotReadyLogged;
        private readonly object _signalHistoryLock = new object();
        private readonly List<string> _recentSignalDirections = new List<string>();

        // 이벤트 정의
        public event Action<string, string, decimal, decimal, string, decimal, decimal>? OnTradeSignal;
        public event Action<string, decimal, decimal>? OnPredictionUpdated;
        public event Action<MultiTimeframeViewModel>? OnSignalAnalyzed;
        public event Action<string>? OnLog;

        // [설정값 튜닝]
        private const int RequiredHistory = 240;
        private const int SignalHistoryLimit = 100;
        private const double TargetShortRatio = 0.30;

        public TransformerStrategy(
            IBinanceRestClient client,
            TransformerTrainer trainer,
            NewsSentimentService newsService,
            ElliottWave3WaveStrategy? elliotWave3Strategy = null,
            TransformerSettings? settings = null)
        {
            _client = client;
            _trainer = trainer;
            _newsService = newsService;
            _elliotWave3Strategy = elliotWave3Strategy;
            _settings = settings ?? new TransformerSettings();
        }

        public async Task AnalyzeAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // 1. 과거 데이터 조회 (5분봉)
                var klinesResult = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, limit: RequiredHistory, ct: token);
                if (!klinesResult.Success || klinesResult.Data.Count() < RequiredHistory) return;

                var klines = klinesResult.Data.ToList();

                // 2. 데이터 전처리
                var candleDataList = await ConvertToCandleDataAsync(klines, symbol);
                if (candleDataList.Count == 0) return;

                // 2.5. 변동성 체크 (ATR 기반 동적 임계값)
                var lastCandle = candleDataList[candleDataList.Count - 1];
                double currentAtr = lastCandle.ATR;
                double dynamicThreshold = CalculateDynamicThreshold(currentPrice, currentAtr);
                OnLog?.Invoke($"📊 [변동성 체크] {symbol} ATR%: {(currentAtr / (double)currentPrice * 100):F2}% | 진입 캻LINE: {dynamicThreshold}점");

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

                bool longCondition = false;
                bool shortCondition = false;
                string mode = isSidewaysMarket ? "SIDEWAYS" : "TREND";
                string decisionForUi = "WAIT";
                decimal customTakeProfitPrice = 0m;
                decimal customStopLossPrice = 0m;

                if (isSidewaysMarket)
                {
                    OnLog?.Invoke($"⚖️ [횡보장 모드] {symbol} ADX:{adx:F1} (+DI:{plusDi:F1}/-DI:{minusDi:F1}) | 박스권 핑퐁 대기");

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

                    bool safeToLong = plusDi >= minusDi;
                    bool safeToShort = minusDi > plusDi;

                    if (longResult.FinalScore >= dynamicThreshold && longResult.FinalScore > shortResult.FinalScore && safeToLong)
                    {
                        longCondition = true;
                        decisionForUi = "LONG";
                    }
                    else if (shortResult.FinalScore >= dynamicThreshold && shortResult.FinalScore > longResult.FinalScore && safeToShort)
                    {
                        if (CanEmitShortSignal(shortResult.FinalScore, predictedChange))
                        {
                            shortCondition = true;
                            decisionForUi = "SHORT";
                        }
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
                        StrategyName = isSidewaysMarket ? "Hybrid AI Sideways(5m)" : "Hybrid AI Trend(5m)",
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
                    RegisterSignalDirection("LONG");
                    string majorTag = HybridStrategyScorer.IsMajorCoin(symbol) ? " [MAJOR]" : "";
                    if (isSidewaysMarket)
                    {
                        OnLog?.Invoke($"⚖️{majorTag} [SIDEWAYS LONG] {symbol} | ADX:{adx:F1} RSI:{technicalCtx.RSI:F1} Vol:{technicalCtx.VolumeRatio:F2}x | TP(mid):{customTakeProfitPrice:F8} SL:{customStopLossPrice:F8}");
                    }
                    else
                    {
                        OnLog?.Invoke($"🚀{majorTag} [TREND LONG] {symbol} | Score: {longResult.FinalScore:F1}/{dynamicThreshold:F0} | 예측: {predictedChange * 100:F2}% | AI:{longResult.AiPredictionScore:F0} EW:{longResult.ElliottWaveScore:F0} Vol:{longResult.VolumeMomentumScore:F0} RSI/M:{longResult.RsiMacdScore:F0} BB:{longResult.BollingerScore:F0}");
                    }
                    try
                    {
                        OnTradeSignal?.Invoke(symbol, "LONG", currentPrice, predictedPrice, mode, customTakeProfitPrice, customStopLossPrice);
                    }
                    catch (Exception eventEx)
                    {
                        OnLog?.Invoke($"⚠️ [TransformerStrategy] {symbol} LONG 주문 이벤트 오류: {eventEx.Message}");
                    }
                }
                else if (shortCondition)
                {
                    RegisterSignalDirection("SHORT");
                    string majorTag = HybridStrategyScorer.IsMajorCoin(symbol) ? " [MAJOR]" : "";
                    if (isSidewaysMarket)
                    {
                        OnLog?.Invoke($"⚖️{majorTag} [SIDEWAYS SHORT] {symbol} | ADX:{adx:F1} RSI:{technicalCtx.RSI:F1} Vol:{technicalCtx.VolumeRatio:F2}x | TP(mid):{customTakeProfitPrice:F8} SL:{customStopLossPrice:F8}");
                    }
                    else
                    {
                        OnLog?.Invoke($"📉{majorTag} [TREND SHORT] {symbol} | Score: {shortResult.FinalScore:F1}/{dynamicThreshold:F0} | 예측: {predictedChange * 100:F2}% | AI:{shortResult.AiPredictionScore:F0} EW:{shortResult.ElliottWaveScore:F0} Vol:{shortResult.VolumeMomentumScore:F0} RSI/M:{shortResult.RsiMacdScore:F0} BB:{shortResult.BollingerScore:F0}");
                    }
                    try
                    {
                        OnTradeSignal?.Invoke(symbol, "SHORT", currentPrice, predictedPrice, mode, customTakeProfitPrice, customStopLossPrice);
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
                return 60.0;
            }
            else if (atrPercentage >= 0.15 && atrPercentage < 0.30)
            {
                // [일반 구간] 평이한 변동성 → 표준 임계값 적용
                return 65.0;
            }
            else if (atrPercentage >= 0.30 && atrPercentage < 0.50)
            {
                // [확장 구간] 변동성 증가 → 추격 매수 리스크가 있으므로 조건 강화
                return 70.0;
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

        private async Task<List<CandleData>> ConvertToCandleDataAsync(List<Binance.Net.Interfaces.IBinanceKline> klines, string symbol)
        {
            var result = new List<CandleData>();
            double sentiment = await _newsService.GetMarketSentimentAsync();

            for (int i = 50; i < klines.Count; i++)
            {
                // 대량 데이터 처리 시 100건마다 Task.Yield로 봐주기
                if ((i - 50) % 100 == 0)
                    await Task.Yield();
                var subset = klines.GetRange(0, i + 1);
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
