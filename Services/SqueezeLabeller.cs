using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 숏 스퀴즈 자동 라벨링 서비스
    /// ─────────────────────────────────
    /// 
    /// 과거 가격 + OI + 펀딩비 데이터를 결합하여
    /// "숏 스퀴즈" 이벤트를 자동으로 감지·라벨링합니다.
    /// 
    /// 라벨링 조건 (3중 확인):
    /// 1. OI 축적 → 감소: 최근 60분간 OI 증가 추세 → 직전 5분 OI 급감 (-0.8% 이상)
    /// 2. 가격 급등: 향후 30분 내 High가 현재 Close 대비 +1.5% 이상 상승
    /// 3. 거래량 급증: 현재 거래량이 20봉 평균 대비 1.5배 이상
    /// 
    /// 출력: CandleData 리스트에 SqueezeLabel 필드를 자동 설정 (0=Normal, 1=Squeeze)
    /// </summary>
    public class SqueezeLabeller
    {
        private readonly OiDataCollector _oiCollector;
        private readonly IBinanceRestClient _client;

        public event Action<string>? OnLog;

        // 라벨링 파라미터 (튜닝 가능)
        public double SqueezePriceThreshold { get; set; } = 0.015;      // 30분 내 +1.5% 이상 상승
        public double OiDropThreshold { get; set; } = -0.8;             // OI -0.8% 이상 감소
        public double VolumeRatioThreshold { get; set; } = 1.5;          // 거래량 1.5배 이상
        public int LookbackMinutes { get; set; } = 60;                   // OI 축적 확인 기간 (분)
        public int LookaheadMinutes { get; set; } = 30;                  // 가격 상승 확인 기간 (분)

        public SqueezeLabeller(OiDataCollector oiCollector, IBinanceRestClient client)
        {
            _oiCollector = oiCollector;
            _client = client;
        }

        /// <summary>
        /// 과거 데이터를 기반으로 숏 스퀴즈 라벨링된 학습 데이터를 생성합니다.
        /// 1시간봉 500개 + 5분봉 OI 히스토리를 결합합니다.
        /// </summary>
        public async Task<List<CandleData>> GenerateLabelledTrainingDataAsync(
            string symbol, CancellationToken token = default)
        {
            OnLog?.Invoke($"🏷️ [Labeller] {symbol} 숏 스퀴즈 라벨링 시작...");

            // 1. 5분봉 캔들 데이터 수집 (최근 500개 = ~41시간)
            var klines5m = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                symbol, KlineInterval.FiveMinutes, limit: 500, ct: token);
            if (!klines5m.Success || klines5m.Data == null || klines5m.Data.Length < 100)
            {
                OnLog?.Invoke($"⚠️ [Labeller] {symbol} 5분봉 데이터 부족");
                return new List<CandleData>();
            }

            var candles = klines5m.Data.ToList();

            // 2. OI 히스토리 수집 (5분 단위)
            var oiHistory = await _oiCollector.GetHistoricalOiAsync(symbol, "5m", 500, token);

            // 3. 펀딩비 조회
            decimal fundingRate = await _oiCollector.GetCurrentFundingRateAsync(symbol, token);

            // 4. CandleData로 변환 + 지표 계산 + OI/펀딩비 병합
            var result = ConvertWithIndicatorsAndOi(candles, oiHistory, fundingRate, symbol);

            // 5. 숏 스퀴즈 자동 라벨링
            int squeezeCount = ApplySqueezeLabels(result, candles, oiHistory);

            OnLog?.Invoke($"🏷️ [Labeller] {symbol} 라벨링 완료: {result.Count}건 중 {squeezeCount}건 스퀴즈 감지 ({(double)squeezeCount / Math.Max(result.Count, 1) * 100:F1}%)");

            return result;
        }

        /// <summary>
        /// 바이낸스 캔들 → CandleData + 지표 + OI/펀딩비 병합
        /// </summary>
        private List<CandleData> ConvertWithIndicatorsAndOi(
            List<Binance.Net.Interfaces.IBinanceKline> candles,
            List<OiSnapshot> oiHistory,
            decimal fundingRate,
            string symbol)
        {
            var result = new List<CandleData>();
            var volumes = candles.Select(k => (float)k.Volume).ToList();

            // 지표 계산에 최소 60봉 필요
            for (int i = 60; i < candles.Count; i++)
            {
                var subset = candles.GetRange(0, i + 1);
                var current = candles[i];

                // 기본 지표 계산
                var rsi = IndicatorCalculator.CalculateRSI(subset, 14);
                var bb = IndicatorCalculator.CalculateBB(subset, 20, 2);
                var atr = IndicatorCalculator.CalculateATR(subset, 14);
                var macd = IndicatorCalculator.CalculateMACD(subset);
                var fib = IndicatorCalculator.CalculateFibonacci(subset, 50);

                // 거래량 분석
                float vol20Avg = 0;
                if (i >= 20)
                {
                    for (int v = i - 19; v <= i; v++) vol20Avg += volumes[v];
                    vol20Avg /= 20f;
                }
                float volumeRatio = vol20Avg > 0 ? volumes[i] / vol20Avg : 1;

                // OI 데이터 매칭 (시간 기준)
                var candleTime = current.OpenTime;
                double oiValue = GetClosestOiValue(oiHistory, candleTime);
                double oiChangePct = GetClosestOiChange(oiHistory, candleTime);

                result.Add(new CandleData
                {
                    Symbol = symbol,
                    Open = current.OpenPrice,
                    High = current.HighPrice,
                    Low = current.LowPrice,
                    Close = current.ClosePrice,
                    Volume = (float)current.Volume,
                    OpenTime = current.OpenTime,
                    CloseTime = current.CloseTime,

                    // 기본 보조지표
                    RSI = (float)rsi,
                    BollingerUpper = (float)bb.Upper,
                    BollingerLower = (float)bb.Lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    Fib_236 = (float)fib.Level236,
                    Fib_382 = (float)fib.Level382,
                    Fib_500 = (float)fib.Level500,
                    Fib_618 = (float)fib.Level618,
                    BB_Upper = bb.Upper,
                    BB_Lower = bb.Lower,
                    SentimentScore = 0,
                    Volume_Ratio = volumeRatio,

                    // OI / 펀딩비 (확장 피처)
                    OpenInterest = (float)oiValue,
                    OI_Change_Pct = (float)oiChangePct,
                    FundingRate = (float)fundingRate,
                    SqueezeLabel = 0 // 기본값 (나중에 ApplySqueezeLabels에서 설정)
                });
            }

            return result;
        }

        /// <summary>
        /// 숏 스퀴즈 자동 라벨링
        /// ──────────────────────
        /// 
        /// 조건 (AND):
        /// 1. 향후 N분(LookaheadMinutes) 내 High가 현재 Close 대비 +1.5% 이상
        /// 2. 현 시점 OI가 감소 중 (OI_Change_Pct < -0.8%)
        /// 3. 거래량이 평균 대비 1.5배 이상
        /// 
        /// 추가 보강: OI 축적 후 급감 패턴 (60분 OI 상승 후 급락)
        /// </summary>
        private int ApplySqueezeLabels(
            List<CandleData> data,
            List<Binance.Net.Interfaces.IBinanceKline> rawCandles,
            List<OiSnapshot> oiHistory)
        {
            int squeezeCount = 0;
            int lookaheadCandles = LookaheadMinutes / 5; // 5분봉 기준

            // data는 rawCandles[60:]에 대응
            int dataOffset = 60;

            for (int i = 0; i < data.Count - lookaheadCandles; i++)
            {
                var current = data[i];
                int rawIdx = i + dataOffset;

                // 1. 향후 N분 내 최대 가격 확인
                decimal futureMaxHigh = 0;
                for (int j = rawIdx + 1; j <= rawIdx + lookaheadCandles && j < rawCandles.Count; j++)
                {
                    if (rawCandles[j].HighPrice > futureMaxHigh)
                        futureMaxHigh = rawCandles[j].HighPrice;
                }

                if (current.Close <= 0) continue;
                double priceJump = (double)(futureMaxHigh - current.Close) / (double)current.Close;

                // 2. OI 급감 확인
                bool oiDropping = current.OI_Change_Pct < OiDropThreshold;

                // 3. 거래량 급증 확인
                bool volumeSurge = current.Volume_Ratio > VolumeRatioThreshold;

                // 4. OI 축적 후 급감 패턴 (보강 조건)
                bool oiAccumulateThenDrop = false;
                if (oiHistory.Count > 0 && i >= 12) // 최소 60분 이전 데이터 필요 (12 * 5분)
                {
                    // 60분 전~10분 전까지 OI 증가 추세 확인
                    float avgOiChangeEarly = 0;
                    int count = 0;
                    for (int k = i - 12; k < i - 2; k++)
                    {
                        if (k >= 0 && k < data.Count)
                        {
                            avgOiChangeEarly += data[k].OI_Change_Pct;
                            count++;
                        }
                    }
                    if (count > 0)
                    {
                        avgOiChangeEarly /= count;
                        // 이전 OI 상승 → 현재 OI 급감 = 축적 후 청산
                        oiAccumulateThenDrop = avgOiChangeEarly > 0 && current.OI_Change_Pct < -0.5;
                    }
                }

                // 라벨링 결정
                // Primary: 가격 급등 + (OI 급감 || 거래량 급증)
                // Enhanced: OI 축적 후 급감 패턴 + 가격 급등
                bool isSqueeze = priceJump >= SqueezePriceThreshold &&
                                 (oiDropping || (volumeSurge && oiAccumulateThenDrop));

                if (isSqueeze)
                {
                    data[i].SqueezeLabel = 1;
                    squeezeCount++;
                }
            }

            return squeezeCount;
        }

        /// <summary>
        /// 라벨링된 데이터를 CSV로 내보냅니다 (디버깅/검증용).
        /// </summary>
        public void ExportToCsv(List<CandleData> data, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Symbol,Time,Open,High,Low,Close,Volume,RSI,MACD,ATR,VolumeRatio,OI,OI_Change,FundingRate,SqueezeLabel");

            foreach (var d in data)
            {
                sb.AppendLine($"{d.Symbol},{d.OpenTime:yyyy-MM-dd HH:mm},{d.Open},{d.High},{d.Low},{d.Close},{d.Volume},{d.RSI:F2},{d.MACD:F4},{d.ATR:F4},{d.Volume_Ratio:F2},{d.OpenInterest:F2},{d.OI_Change_Pct:F4},{d.FundingRate:F6},{d.SqueezeLabel}");
            }

            File.WriteAllText(filePath, sb.ToString());
            OnLog?.Invoke($"💾 [Labeller] CSV 내보내기 완료: {filePath} ({data.Count}행)");
        }

        #region [ 헬퍼 ]

        private double GetClosestOiValue(List<OiSnapshot> oiHistory, DateTime time)
        {
            if (oiHistory.Count == 0) return 0;
            return oiHistory
                .OrderBy(h => Math.Abs((h.Timestamp - time).TotalSeconds))
                .First().OpenInterest;
        }

        private double GetClosestOiChange(List<OiSnapshot> oiHistory, DateTime time)
        {
            if (oiHistory.Count == 0) return 0;
            return oiHistory
                .OrderBy(h => Math.Abs((h.Timestamp - time).TotalSeconds))
                .First().OiChangePct;
        }

        #endregion
    }
}
