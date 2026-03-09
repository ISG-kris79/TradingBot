using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// 파동 데이터셋 생성기
    /// 1년 치 역사적 데이터에서 엘리엇 파동 패턴을 찾아
    /// Transformer용 시계열 데이터셋과 ML.NET용 스냅샷 데이터셋을 분리 추출
    /// </summary>
    public class WaveDatasetGenerator
    {
        private readonly IExchangeService _exchangeService;
        private readonly ElliottWaveDetector _waveDetector;
        private readonly WaveSniper _waveSniper;

        // 학습 파라미터
        private const decimal TakeProfitTarget = 2.5m;  // 목표 수익 2.5%
        private const decimal StopLossTarget = 1.0m;    // 손절 1.0%
        private const int LookaheadCandles = 30;        // 향후 30개 캔들 관찰 (15분봉 기준 7.5시간)

        public WaveDatasetGenerator(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
            _waveDetector = new ElliottWaveDetector();
            _waveSniper = new WaveSniper();
        }

        /// <summary>
        /// 전체 데이터셋 생성 (메인 진입점)
        /// </summary>
        public async Task<(List<WaveTrainingData> transformerData, List<MLNetWaveSniper.SniperInput> mlnetData)>
            GenerateDatasetAsync(
                List<string> symbols,
                DateTime startDate,
                DateTime endDate,
                CancellationToken token)
        {
            var transformerDataset = new List<WaveTrainingData>();
            var mlnetDataset = new List<MLNetWaveSniper.SniperInput>();

            Console.WriteLine($"[DatasetGenerator] 데이터셋 생성 시작 | 심볼 수={symbols.Count} | 기간={startDate:yyyy-MM-dd}~{endDate:yyyy-MM-dd}");

            int processedSymbols = 0;
            foreach (var symbol in symbols)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var (tfData, mlData) = await GenerateSymbolDatasetAsync(symbol, startDate, endDate, token);
                    transformerDataset.AddRange(tfData);
                    mlnetDataset.AddRange(mlData);

                    processedSymbols++;
                    Console.WriteLine($"[DatasetGenerator] {symbol} 완료 ({processedSymbols}/{symbols.Count}) | TF샘플={tfData.Count} ML샘플={mlData.Count}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DatasetGenerator] {symbol} 실패: {ex.Message}");
                }

                await Task.Delay(500, token); // API 레이트 리밋
            }

            Console.WriteLine($"[DatasetGenerator] 전체 완료 | TF={transformerDataset.Count} ML={mlnetDataset.Count}");
            return (transformerDataset, mlnetDataset);
        }

        /// <summary>
        /// 단일 심볼의 데이터셋 생성
        /// </summary>
        private async Task<(List<WaveTrainingData> tfData, List<MLNetWaveSniper.SniperInput> mlData)>
            GenerateSymbolDatasetAsync(
                string symbol,
                DateTime startDate,
                DateTime endDate,
                CancellationToken token)
        {
            var transformerData = new List<WaveTrainingData>();
            var mlnetData = new List<MLNetWaveSniper.SniperInput>();

            // 15분봉 데이터 가져오기 (최대 1500개 = 15.6일)
            // 1년 치를 처리하려면 여러 번 나눠서 가져와야 함
            var allCandles = await FetchHistoricalCandles(symbol, startDate, endDate, token);
            
            if (allCandles.Count < 200)
                return (transformerData, mlnetData);

            // CandleData로 변환 및 지표 계산
            var candleDataList = new List<CandleData>();
            foreach (var kline in allCandles)
            {
                var candle = new CandleData
                {
                    Symbol = symbol,
                    OpenTime = kline.OpenTime,
                    CloseTime = kline.CloseTime,
                    Open = kline.OpenPrice,
                    High = kline.HighPrice,
                    Low = kline.LowPrice,
                    Close = kline.ClosePrice,
                    Volume = (float)kline.Volume
                };
                candleDataList.Add(candle);
            }

            // 지표 계산 (TODO: 실제 환경에서는 TradingEngine에서 계산된 데이터 사용)
            // IndicatorCalculator.CalculateIndicators(candleDataList);

            // 슬라이딩 윈도우로 파동 패턴 탐색
            for (int i = 100; i < candleDataList.Count - LookaheadCandles; i++)
            {
                if (token.IsCancellationRequested)
                    break;

                var currentCandle = candleDataList[i];
                var recentCandles = candleDataList.Skip(i - 100).Take(100).ToList();
                var currentPrice = currentCandle.Close;

                // 파동 상태 업데이트
                var waveState = _waveDetector.UpdateWaveDetection(
                    symbol, 
                    currentCandle, 
                    recentCandles, 
                    currentPrice);

                // 2파 조정 구간이면 샘플 추출
                if (waveState.Phase == ElliottWaveDetector.WavePhase.Wave2Retracing ||
                    waveState.Phase == ElliottWaveDetector.WavePhase.Wave2Complete)
                {
                    // 향후 가격 확인 (라벨링)
                    var futureCandles = candleDataList.Skip(i + 1).Take(LookaheadCandles).ToList();
                    bool isWave3Entry = CheckIfWave3Started(currentPrice, futureCandles, out bool wasSuccessful);

                    // Transformer 샘플 생성
                    if (isWave3Entry || waveState.Phase == ElliottWaveDetector.WavePhase.Wave2Complete)
                    {
                        var sequence = ExtractFeatureSequence(recentCandles);
                        transformerData.Add(new WaveTrainingData
                        {
                            Sequence = sequence,
                            IsWave3Entry = isWave3Entry,
                            Symbol = symbol,
                            Timestamp = currentCandle.CloseTime
                        });
                    }

                    // ML.NET 샘플 생성 (피보나치 황금 구간에서만)
                    if (waveState.Wave2RetracementRatio >= 0.5m && waveState.Wave2RetracementRatio <= 0.786m)
                    {
                        var sniperSignal = _waveSniper.EvaluateTrigger(
                            waveState, 
                            currentCandle, 
                            recentCandles, 
                            currentPrice);

                        // 1시간, 4시간 추세 계산 (간단화)
                        int trend1H = currentCandle.SMA_20 > 0 && currentCandle.Close > (decimal)currentCandle.SMA_20 ? 1 : -1;
                        int trend4H = trend1H; // 단순화

                        var mlInput = MLNetWaveSniper.ConvertFromSniperSignal(
                            sniperSignal, 
                            waveState, 
                            currentCandle, 
                            trend1H, 
                            trend4H);

                        mlInput.LabelSuccess = wasSuccessful;
                        mlnetData.Add(mlInput);
                    }
                }
            }

            return (transformerData, mlnetData);
        }

        /// <summary>
        /// 역사적 캔들 데이터 가져오기 (간소화 버전 - 최근 1500개만)
        /// </summary>
        private async Task<List<IBinanceKline>> FetchHistoricalCandles(
            string symbol,
            DateTime startDate,
            DateTime endDate,
            CancellationToken token)
        {
            // 간소화: 최근 1500개 15분봉만 가져오기 (약 15.6일치)
            var candles = await _exchangeService.GetKlinesAsync(
                symbol, 
                KlineInterval.FifteenMinutes, 
                1500, 
                token);

            return candles?.ToList() ?? new List<IBinanceKline>();
        }

        /// <summary>
        /// 3파가 실제로 시작되었는지 확인 (라벨링)
        /// </summary>
        private bool CheckIfWave3Started(
            decimal entryPrice,
            List<CandleData> futureCandles,
            out bool wasSuccessful)
        {
            wasSuccessful = false;

            if (futureCandles.Count < 10)
                return false;

            decimal highestPrice = futureCandles.Max(c => c.High);
            decimal lowestPrice = futureCandles.Min(c => c.Low);

            decimal gainPercent = (highestPrice - entryPrice) / entryPrice * 100m;
            decimal lossPercent = (entryPrice - lowestPrice) / entryPrice * 100m;

            // 3파 시작 조건: 목표가 달성 전에 손절에 안 걸림
            if (gainPercent >= TakeProfitTarget && lossPercent < StopLossTarget)
            {
                wasSuccessful = true;
                return true;
            }

            // 손실 먼저 발생
            if (lossPercent >= StopLossTarget)
            {
                wasSuccessful = false;
                return false;
            }

            // 강한 상승 시작 (5개 캔들 내 1.5% 이상 상승)
            var first5 = futureCandles.Take(5).ToList();
            decimal quickGain = (first5.Max(c => c.High) - entryPrice) / entryPrice * 100m;
            if (quickGain >= 1.5m)
            {
                wasSuccessful = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Transformer 입력용 특징 시퀀스 추출
        /// </summary>
        private List<float[]> ExtractFeatureSequence(List<CandleData> candles)
        {
            var sequence = new List<float[]>();

            foreach (var candle in candles)
            {
                var features = new float[]
                {
                    (float)candle.Close,
                    (float)candle.Volume,
                    candle.RSI,
                    candle.MACD,
                    candle.MACD_Signal,
                    candle.MACD_Hist,
                    candle.SMA_20,
                    (candle.BollingerUpper - candle.BollingerLower) / 2f, // 밴드 폭 (표준편차 대체)
                    candle.Volume_Ratio,
                    (float)((candle.Close - (decimal)candle.SMA_20) / (decimal)candle.SMA_20), // SMA 거리
                    (float)((candle.High - candle.Low) / candle.Close), // 변동성
                    (float)((candle.Close - (decimal)candle.Open) / candle.Close), // 캔들 방향
                    candle.Volume_Ratio > 1.5f ? 1f : 0f, // 거래량 폭발 플래그
                    candle.RSI > 70f ? 1f : (candle.RSI < 30f ? -1f : 0f), // RSI 과매수/과매도
                    (float)(candle.High - Math.Max((decimal)candle.Open, candle.Close)), // 위꼬리
                    (float)(Math.Min((decimal)candle.Open, candle.Close) - candle.Low), // 아래꼬리
                    (float)(Math.Abs(candle.Close - (decimal)candle.Open)), // 몸통 크기
                    0f // Reserved (추가 특징용)
                };
                sequence.Add(features);
            }

            return sequence;
        }

        /// <summary>
        /// 데이터셋을 CSV로 저장
        /// </summary>
        public void SaveToCSV(
            List<WaveTrainingData> transformerData,
            List<MLNetWaveSniper.SniperInput> mlnetData,
            string outputDir = "TrainingData/WaveDatasets")
        {
            System.IO.Directory.CreateDirectory(outputDir);

            // Transformer CSV (간단 버전 - 실제로는 binary 저장 권장)
            var tfPath = System.IO.Path.Combine(outputDir, $"transformer_data_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using (var writer = new System.IO.StreamWriter(tfPath))
            {
                writer.WriteLine("Symbol,Timestamp,IsWave3Entry,SequenceLength");
                foreach (var data in transformerData)
                {
                    writer.WriteLine($"{data.Symbol},{data.Timestamp:yyyy-MM-dd HH:mm:ss},{data.IsWave3Entry},{data.Sequence.Count}");
                }
            }
            Console.WriteLine($"[DatasetGenerator] Transformer CSV 저장: {tfPath}");

            // ML.NET CSV
            var mlPath = System.IO.Path.Combine(outputDir, $"mlnet_data_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using (var writer = new System.IO.StreamWriter(mlPath))
            {
                writer.WriteLine("Wave1HeightPercent,Wave2RetracementRatio,CandlesSinceWave1Peak,DistanceFromFib618," +
                    "IsInGoldenZone,HasRsiDivergence,RsiDivergenceStrength,CurrentRsi," +
                    "BollingerPosition,LowerTailRatio,HasBollingerTail,VolumeMultiplier,HasVolumeExplosion," +
                    "Trend1H,Trend4H,MacdHistogram,PriceReversalStrength,CandleBodyRatio,LabelSuccess");

                foreach (var data in mlnetData)
                {
                    writer.WriteLine($"{data.Wave1HeightPercent},{data.Wave2RetracementRatio},{data.CandlesSinceWave1Peak}," +
                        $"{data.DistanceFromFib618},{data.IsInGoldenZone},{data.HasRsiDivergence}," +
                        $"{data.RsiDivergenceStrength},{data.CurrentRsi},{data.BollingerPosition}," +
                        $"{data.LowerTailRatio},{data.HasBollingerTail},{data.VolumeMultiplier}," +
                        $"{data.HasVolumeExplosion},{data.Trend1H},{data.Trend4H},{data.MacdHistogram}," +
                        $"{data.PriceReversalStrength},{data.CandleBodyRatio},{data.LabelSuccess}");
                }
            }
            Console.WriteLine($"[DatasetGenerator] ML.NET CSV 저장: {mlPath}");
        }
    }
}
