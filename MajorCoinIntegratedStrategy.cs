using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Serilog;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 메이저 코인(BTC, ETH, SOL, XRP) 통합 전략 예제
    /// 
    /// 기존 MajorCoinStrategy + MajorCoinRetestStrategy 통합
    /// - 기존: 5분봉 기반 AI 스코어 계산
    /// - 신규: EMA 20 눌림목 + 숏 스퀴즈 감지
    /// 
    /// 이 클래스는 두 전략을 동시에 활용하는 방법을 보여줍니다.
    /// </summary>
    public class MajorCoinIntegratedStrategy
    {
        private readonly ILogger _logger;
        private readonly MajorCoinStrategy _majorStrategy;
        private readonly MajorCoinRetestStrategy _retestStrategy;
        private readonly MarketDataManager _marketData;

        public event Action<string>? OnLog;
        public event Action<string, string, decimal, double>? OnTradeSignal;  // symbol, decision, price, multiplier

        public MajorCoinIntegratedStrategy(
            MarketDataManager marketData,
            Func<TradingSettings?>? settingsAccessor = null)
        {
            _logger = Log.ForContext<MajorCoinIntegratedStrategy>();
            _marketData = marketData;
            
            _majorStrategy = new MajorCoinStrategy(marketData, settingsAccessor);
            _retestStrategy = new MajorCoinRetestStrategy();

            // 이벤트 구독
            _majorStrategy.OnLog += (msg) => OnLog?.Invoke(msg);
            _retestStrategy.OnLog += (msg) => OnLog?.Invoke(msg);
        }

        /// <summary>
        /// 통합 분석 (기존 + 신규 전략)
        /// </summary>
        public async Task AnalyzeAsync(
            string symbol,
            decimal currentPrice,
            CancellationToken token)
        {
            // 메이저 코인이 아니면 기존 전략 사용
            if (!MajorCoinRetestStrategy.IsMajorCoin(symbol))
            {
                await _majorStrategy.AnalyzeAsync(symbol, currentPrice, token);
                return;
            }

            // ========== 메이저 코인 통합 분석 ==========
            
            LogInfo($"🔍 [{symbol}] 메이저 코인 통합 분석 시작...");

            // [1] 기술 지표 수집
            var tech = await CollectTechnicalDataAsync(symbol, currentPrice);
            
            // [2] 시장 데이터 수집 (OI, 청산, 펀딩비)
            var market = await CollectMarketDataAsync(symbol);

            // [3] 신규 전략 평가: EMA 20 눌림목 + 숏 스퀴즈
            var decision = _retestStrategy.EvaluateEntry(symbol, tech, market);

            if (decision.ShouldEnter)
            {
                // 신규 전략 진입 신호 발생
                string entryTypeName = decision.EntryType switch
                {
                    EntryType.EMA20_Retest => "EMA 20 눌림목",
                    EntryType.ShortSqueeze => "숏 스퀴즈",
                    _ => "알 수 없음"
                };

                LogInfo($"✅ [{symbol}] 신규 전략 진입 신호: {entryTypeName}");
                LogInfo($"   사유: {decision.Reason}");
                LogInfo($"   비중: {decision.PositionSizeMultiplier}x");
                LogInfo($"   손절: {decision.StopLossType}");
                LogInfo($"   익절: {decision.TakeProfitTarget}");

                if (decision.SqueezeSignals.Count > 0)
                {
                    LogInfo($"   스퀴즈 신호: {string.Join(", ", decision.SqueezeSignals)}");
                }

                // 트레이드 신호 발생
                OnTradeSignal?.Invoke(
                    symbol,
                    "LONG",  // 메이저는 기본적으로 롱 전략
                    currentPrice,
                    decision.PositionSizeMultiplier
                );

                return;
            }

            // [4] 신규 전략 미감지 시 기존 전략 실행 (폴백)
            LogInfo($"ℹ️ [{symbol}] 신규 전략 미감지. 기존 전략으로 폴백...");
            await _majorStrategy.AnalyzeAsync(symbol, currentPrice, token);
        }

        /// <summary>
        /// 기술 지표 수집
        /// </summary>
        private async Task<MajorTechnicalData> CollectTechnicalDataAsync(string symbol, decimal currentPrice)
        {
            // 실제 구현에서는 IndicatorCalculator를 사용하여 계산
            // 여기서는 더미 데이터 반환
            
            if (!_marketData.KlineCache.TryGetValue(symbol, out var cache))
            {
                return new MajorTechnicalData { CurrentPrice = currentPrice };
            }

            var list = cache.ToList();
            if (list.Count < 120)
            {
                return new MajorTechnicalData { CurrentPrice = currentPrice };
            }

            // 지표 계산
            double rsi = IndicatorCalculator.CalculateRSI(list, 14);
            var bb = IndicatorCalculator.CalculateBB(list, 20, 2);
            double ema20 = IndicatorCalculator.CalculateEMA(list, 20);
            double ema50 = IndicatorCalculator.CalculateEMA(list, 50);

            // RSI 추세 확인
            var recent5Rsi = list.TakeLast(5).Select(k => IndicatorCalculator.CalculateRSI(list.Take(list.IndexOf(k) + 1).ToList(), 14)).ToList();
            bool isRsiUptrend = recent5Rsi.Count >= 2 && recent5Rsi.Last() > recent5Rsi.First();

            // 거래량 비율
            var recent20 = list.TakeLast(20).ToList();
            double avgVolume = recent20.Any() ? recent20.Average(k => (double)k.Volume) : 0;
            double currentVolume = recent20.LastOrDefault() != null ? (double)recent20.Last().Volume : 0;
            double volumeRatio = avgVolume > 0 ? currentVolume / avgVolume : 1;

            // 저점 상승 패턴
            bool isMakingHigherLows = IsMakingHigherLows(list);

            // TODO: 1시간봉 EMA 데이터 수집 (실제로는 별도 KlineCache 필요)
            decimal ema20_1h = (decimal)ema20;  // 임시
            decimal ema50_1h = (decimal)ema50;  // 임시

            await Task.CompletedTask;

            return new MajorTechnicalData
            {
                CurrentPrice = currentPrice,
                Ema20 = (decimal)ema20,
                Ema50 = (decimal)ema50,
                Ema20_1h = ema20_1h,
                Ema50_1h = ema50_1h,
                Rsi = rsi,
                IsRsiUptrend = isRsiUptrend,
                UpperBand = (decimal)bb.Upper,
                LowerBand = (decimal)bb.Lower,
                VolumeRatio = volumeRatio,
                IsMakingHigherLows = isMakingHigherLows
            };
        }

        /// <summary>
        /// 시장 데이터 수집 (OI, 청산, 펀딩비)
        /// </summary>
        private async Task<MajorMarketData> CollectMarketDataAsync(string symbol)
        {
            // 실제 구현에서는 바이낸스 API 호출
            // - GetOpenInterest(): 미결제약정
            // - GetLiquidationOrders(): 청산 데이터
            // - GetFundingRate(): 펀딩비
            
            // TODO: API 구현
            
            await Task.CompletedTask;

            // 더미 데이터 반환
            return new MajorMarketData
            {
                PriceChange_5m = 0.5m,  // 5분 가격 변화율
                OiChange_5m = -0.5m,    // 5분 OI 변화율
                RecentShortLiquidationUsdt = 100000,  // 최근 1분 숏 청산액
                AvgLiquidation = 50000,  // 평균 청산액
                FundingRate = 0.005m,    // 펀딩비
                OrderBookRatio = 1.3     // 호가창 비율
            };
        }

        /// <summary>
        /// 저점 상승 패턴 확인
        /// </summary>
        private bool IsMakingHigherLows(List<IBinanceKline> candles)
        {
            const int segmentSize = 5;
            const int requiredSegments = 3;
            const decimal minRiseRatio = 1.001m;

            int requiredCandles = segmentSize * requiredSegments;
            if (candles.Count < requiredCandles) return false;

            var window = candles.TakeLast(requiredCandles).ToList();

            decimal low1 = window.Take(segmentSize).Min(c => c.LowPrice);
            decimal low2 = window.Skip(segmentSize).Take(segmentSize).Min(c => c.LowPrice);
            decimal low3 = window.Skip(segmentSize * 2).Take(segmentSize).Min(c => c.LowPrice);

            return low2 >= low1 * minRiseRatio && low3 >= low2 * minRiseRatio;
        }

        private void LogInfo(string message)
        {
            _logger.Information(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}
