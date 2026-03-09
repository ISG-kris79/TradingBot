using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// ML.NET Wave Sniper (스나이퍼 - 좌뇌)
    /// 역할: 타점 순간의 조건 검증 - "이 조건에서 방아쇠를 당기면 성공하는가?"
    /// 학습 목표: 피보나치 구간 도달 시점의 스냅샷 데이터로 진입 성공률 예측
    /// </summary>
    public class MLNetWaveSniper
    {
        private readonly MLContext _mlContext;
        private readonly string _modelPath;
        private ITransformer? _model;
        private PredictionEngine<SniperInput, SniperOutput>? _predictionEngine;
        private bool _isReady;

        public bool IsReady => _isReady;

        public MLNetWaveSniper(string modelPath = "mlnet_wave_sniper.zip")
        {
            _mlContext = new MLContext(seed: 42);
            _modelPath = modelPath;

            try
            {
                LoadModel();
            }
            catch
            {
                _isReady = false;
            }
        }

        /// <summary>
        /// ML.NET 입력 스키마 - 타점 순간의 스냅샷
        /// </summary>
        public class SniperInput
        {
            // 엘리엇 파동 관련
            [LoadColumn(0)] public float Wave1HeightPercent { get; set; }      // 1파 상승폭 (%)
            [LoadColumn(1)] public float Wave2RetracementRatio { get; set; }   // 2파 되돌림 비율 (0.5~0.618)
            [LoadColumn(2)] public float CandlesSinceWave1Peak { get; set; }   // 1파 고점 이후 경과 캔들 수
            
            // 피보나치 구간
            [LoadColumn(3)] public float DistanceFromFib618 { get; set; }      // 0.618 레벨과의 거리 (%)
            [LoadColumn(4)] public float IsInGoldenZone { get; set; }          // 황금 구간 여부 (0/1)
            
            // RSI 다이버전스
            [LoadColumn(5)] public float HasRsiDivergence { get; set; }        // 다이버전스 발생 (0/1)
            [LoadColumn(6)] public float RsiDivergenceStrength { get; set; }   // 다이버전스 강도 (0~1)
            [LoadColumn(7)] public float CurrentRsi { get; set; }              // 현재 RSI
            
            // 볼린저 밴드
            [LoadColumn(8)] public float BollingerPosition { get; set; }       // 볼린저 밴드 내 위치 (0=하단, 1=상단)
            [LoadColumn(9)] public float LowerTailRatio { get; set; }          // 아랫꼬리 비율
            [LoadColumn(10)] public float HasBollingerTail { get; set; }       // 볼린저 터치 + 꼬리 (0/1)
            
            // 거래량
            [LoadColumn(11)] public float VolumeMultiplier { get; set; }       // 평균 대비 거래량 배율
            [LoadColumn(12)] public float HasVolumeExplosion { get; set; }     // 거래량 폭발 (0/1)
            
            // 추세 확인
            [LoadColumn(13)] public float Trend1H { get; set; }                // 1시간 추세 (-1/0/1)
            [LoadColumn(14)] public float Trend4H { get; set; }                // 4시간 추세 (-1/0/1)
            [LoadColumn(15)] public float MacdHistogram { get; set; }          // MACD 히스토그램
            
            // 가격 행동
            [LoadColumn(16)] public float PriceReversalStrength { get; set; }  // 반등 강도 (%)
            [LoadColumn(17)] public float CandleBodyRatio { get; set; }        // 캔들 몸통 비율
            
            // 라벨
            [LoadColumn(18)] public bool LabelSuccess { get; set; }            // 진입 성공 여부
        }

        /// <summary>
        /// ML.NET 출력 스키마
        /// </summary>
        public class SniperOutput
        {
            [ColumnName("PredictedLabel")]
            public bool Prediction { get; set; }                               // 성공 예측
            
            [ColumnName("Probability")]
            public float Probability { get; set; }                             // 성공 확률
            
            [ColumnName("Score")]
            public float Score { get; set; }                                   // 로짓 스코어
        }

        /// <summary>
        /// 모델 학습
        /// </summary>
        public (float accuracy, float auc) Train(List<SniperInput> trainingData)
        {
            if (trainingData.Count < 100)
                throw new ArgumentException("최소 100개 이상의 학습 데이터 필요");

            // 데이터 로드
            var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

            // 80-20 분할
            var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);

            // 파이프라인 구성
            var pipeline = _mlContext.Transforms.Concatenate(
                    "Features",
                    nameof(SniperInput.Wave1HeightPercent),
                    nameof(SniperInput.Wave2RetracementRatio),
                    nameof(SniperInput.CandlesSinceWave1Peak),
                    nameof(SniperInput.DistanceFromFib618),
                    nameof(SniperInput.IsInGoldenZone),
                    nameof(SniperInput.HasRsiDivergence),
                    nameof(SniperInput.RsiDivergenceStrength),
                    nameof(SniperInput.CurrentRsi),
                    nameof(SniperInput.BollingerPosition),
                    nameof(SniperInput.LowerTailRatio),
                    nameof(SniperInput.HasBollingerTail),
                    nameof(SniperInput.VolumeMultiplier),
                    nameof(SniperInput.HasVolumeExplosion),
                    nameof(SniperInput.Trend1H),
                    nameof(SniperInput.Trend4H),
                    nameof(SniperInput.MacdHistogram),
                    nameof(SniperInput.PriceReversalStrength),
                    nameof(SniperInput.CandleBodyRatio))
                .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                    labelColumnName: nameof(SniperInput.LabelSuccess),
                    featureColumnName: "Features",
                    numberOfIterations: 200,
                    learningRate: 0.05,
                    numberOfLeaves: 50));

            // 학습
            Console.WriteLine("[MLNetSniper] 학습 시작...");
            _model = pipeline.Fit(split.TrainSet);

            // 평가
            var predictions = _model.Transform(split.TestSet);
            var metrics = _mlContext.BinaryClassification.Evaluate(predictions, nameof(SniperInput.LabelSuccess));

            Console.WriteLine($"[MLNetSniper] 정확도={metrics.Accuracy:P1} AUC={metrics.AreaUnderRocCurve:F3} F1={metrics.F1Score:F3}");

            // 저장
            _mlContext.Model.Save(_model, dataView.Schema, _modelPath);
            _isReady = true;

            // PredictionEngine 재생성
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<SniperInput, SniperOutput>(_model);

            return ((float)metrics.Accuracy, (float)metrics.AreaUnderRocCurve);
        }

        /// <summary>
        /// 실시간 예측
        /// </summary>
        public SniperOutput? Predict(SniperInput input)
        {
            if (!_isReady || _predictionEngine == null)
                return null;

            return _predictionEngine.Predict(input);
        }

        /// <summary>
        /// 모델 로드
        /// </summary>
        private void LoadModel()
        {
            if (!System.IO.File.Exists(_modelPath))
            {
                _isReady = false;
                return;
            }

            _model = _mlContext.Model.Load(_modelPath, out var schema);
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<SniperInput, SniperOutput>(_model);
            _isReady = true;
        }

        /// <summary>
        /// WaveSniper 신호를 ML.NET 입력으로 변환
        /// </summary>
        public static SniperInput ConvertFromSniperSignal(
            WaveSniper.SniperSignal signal,
            ElliottWaveDetector.WaveState waveState,
            CandleData currentCandle,
            int trend1H,
            int trend4H)
        {
            return new SniperInput
            {
                Wave1HeightPercent = (float)(waveState.Wave1Height / waveState.Wave1StartPrice * 100m),
                Wave2RetracementRatio = (float)waveState.Wave2RetracementRatio,
                CandlesSinceWave1Peak = waveState.CandlesSinceWave1Peak,
                DistanceFromFib618 = (float)Math.Abs((signal.CurrentPrice - waveState.Fib_0618) / waveState.Fib_0618 * 100m),
                IsInGoldenZone = signal.IsInGoldenZone ? 1f : 0f,
                HasRsiDivergence = signal.HasRsiDivergence ? 1f : 0f,
                RsiDivergenceStrength = signal.RsiDivergenceStrength,
                CurrentRsi = currentCandle.RSI,
                BollingerPosition = CalculateBollingerPosition(currentCandle),
                LowerTailRatio = (float)signal.TailRatio,
                HasBollingerTail = signal.HasBollingerTail ? 1f : 0f,
                VolumeMultiplier = signal.VolumeMultiplier,
                HasVolumeExplosion = signal.HasVolumeExplosion ? 1f : 0f,
                Trend1H = trend1H,
                Trend4H = trend4H,
                MacdHistogram = currentCandle.MACD_Hist,
                PriceReversalStrength = (float)((signal.CurrentPrice - waveState.Wave2LowPrice) / waveState.Wave2LowPrice * 100m),
                CandleBodyRatio = CalculateCandleBodyRatio(currentCandle),
                LabelSuccess = false // 예측 시에는 사용 안 함
            };
        }

        private static float CalculateBollingerPosition(CandleData candle)
        {
            float upper = candle.BollingerUpper;
            float lower = candle.BollingerLower;
            float range = upper - lower;

            if (range <= 0)
                return 0.5f;

            return Math.Clamp(((float)candle.Close - lower) / range, 0f, 1f);
        }

        private static float CalculateCandleBodyRatio(CandleData candle)
        {
            float high = (float)candle.High;
            float low = (float)candle.Low;
            float bodySize = Math.Abs((float)candle.Close - (float)candle.Open);
            float totalSize = high - low;

            if (totalSize <= 0)
                return 0f;

            return bodySize / totalSize;
        }
    }
}
