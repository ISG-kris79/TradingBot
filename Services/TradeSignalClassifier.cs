using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML;
using Microsoft.ML.Data;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// ML.NET 3분류 매매 신호 모델 (롱=1 / 숏=2 / 관망=0)
    /// - DB CandleData → 라벨링 → LightGBM MultiClass → 실시간 추론
    /// [v3.0.7] TensorFlow 제거 후 ML.NET 통합
    /// </summary>
    public class TradeSignalClassifier : IDisposable
    {
        private readonly MLContext _mlContext;
        private ITransformer? _model;
        private PredictionEngine<TradeSignalFeature, TradeSignalPrediction>? _predictionEngine;
        private readonly object _predictLock = new();
        private bool _disposed;

        private static readonly string DefaultModelDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "Models");
        private static readonly string ModelFileName = "trade_signal_model.zip";

        public bool IsModelLoaded => _model != null && _predictionEngine != null;

        public event Action<string>? OnLog;

        // ──────── 라벨링 설정 ────────
        /// <summary>롱 라벨 기준: N분 후 가격변동률 +X% 이상</summary>
        public float LongThresholdPct { get; set; } = 1.0f;
        /// <summary>숏 라벨 기준: N분 후 가격변동률 -X% 이상</summary>
        public float ShortThresholdPct { get; set; } = 1.0f;
        /// <summary>라벨링 기준 시간 (봉 개수, 5분봉 기준)</summary>
        public int LookAheadCandles { get; set; } = 6; // 30분 후

        public TradeSignalClassifier()
        {
            _mlContext = new MLContext(seed: 42);
            Directory.CreateDirectory(DefaultModelDir);
        }

        // ═══════════════════════════════════════════
        // Phase 2: 라벨링
        // ═══════════════════════════════════════════

        /// <summary>
        /// CandleData 리스트에서 학습용 피처+라벨 데이터셋 생성
        /// candles는 시간순 정렬(오래된→최신)이어야 함
        /// </summary>
        public List<TradeSignalFeature> LabelCandleData(List<CandleData> candles)
        {
            if (candles == null || candles.Count < LookAheadCandles + 10)
                return new List<TradeSignalFeature>();

            var dataset = new List<TradeSignalFeature>();

            for (int i = 0; i < candles.Count - LookAheadCandles; i++)
            {
                var current = candles[i];
                var future = candles[i + LookAheadCandles];

                if (current.Close <= 0 || future.Close <= 0)
                    continue;

                float pctChange = (float)((future.Close - current.Close) / current.Close * 100m);

                // 라벨링: 롱=1, 숏=2, 관망=0
                uint label;
                if (pctChange >= LongThresholdPct)
                    label = 1; // 롱
                else if (pctChange <= -ShortThresholdPct)
                    label = 2; // 숏
                else
                    label = 0; // 관망

                var feature = ExtractFeature(current);
                feature.Label = label;
                dataset.Add(feature);
            }

            OnLog?.Invoke($"[TradeSignal] 라벨링 완료: {dataset.Count}건 (Long={dataset.Count(d => d.Label == 1)}, Short={dataset.Count(d => d.Label == 2)}, Hold={dataset.Count(d => d.Label == 0)})");
            return dataset;
        }

        /// <summary>CandleData → TradeSignalFeature 피처 추출</summary>
        public static TradeSignalFeature ExtractFeature(CandleData candle)
        {
            return new TradeSignalFeature
            {
                RSI = Sanitize(candle.RSI),
                MACD = Sanitize(candle.MACD),
                MACD_Signal = Sanitize(candle.MACD_Signal),
                MACD_Hist = Sanitize(candle.MACD_Hist),
                ATR = Sanitize(candle.ATR),
                ADX = Sanitize(candle.ADX),
                PlusDI = Sanitize(candle.PlusDI),
                MinusDI = Sanitize(candle.MinusDI),
                BB_Width = Sanitize(candle.BB_Width),
                Price_To_BB_Mid = Sanitize(candle.Price_To_BB_Mid),
                Price_To_SMA20_Pct = Sanitize(candle.Price_To_SMA20_Pct),
                Price_Change_Pct = Sanitize(candle.Price_Change_Pct),
                Candle_Body_Ratio = Sanitize(candle.Candle_Body_Ratio),
                Upper_Shadow_Ratio = Sanitize(candle.Upper_Shadow_Ratio),
                Lower_Shadow_Ratio = Sanitize(candle.Lower_Shadow_Ratio),
                Volume_Ratio = Sanitize(candle.Volume_Ratio),
                Volume_Change_Pct = Sanitize(candle.Volume_Change_Pct),
                OI_Change_Pct = Sanitize(candle.OI_Change_Pct),
                Trend_Strength = Sanitize(candle.Trend_Strength),
                Fib_Position = Sanitize(candle.Fib_Position),
                Stoch_K = Sanitize(candle.Stoch_K),
                Stoch_D = Sanitize(candle.Stoch_D),
                RSI_Divergence = Sanitize(candle.RSI_Divergence),
                SentimentScore = Sanitize(candle.SentimentScore)
            };
        }

        // ═══════════════════════════════════════════
        // Phase 3: 학습 파이프라인
        // ═══════════════════════════════════════════

        /// <summary>
        /// 학습 데이터로 LightGBM MultiClass 모델 학습 + 저장
        /// </summary>
        public async Task<TradeSignalMetrics> TrainAndSaveAsync(
            List<TradeSignalFeature> trainingData,
            CancellationToken token = default)
        {
            if (trainingData == null || trainingData.Count < 50)
            {
                OnLog?.Invoke($"[TradeSignal] 학습 데이터 부족: {trainingData?.Count ?? 0}건 (최소 50건 필요)");
                return new TradeSignalMetrics();
            }

            return await Task.Run(() =>
            {
                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

                    // 80/20 분할
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);

                    // 파이프라인: 피처 연결 → 정규화 → LightGBM MultiClass
                    var featureColumns = typeof(TradeSignalFeature)
                        .GetProperties()
                        .Where(p => p.Name != "Label" && p.PropertyType == typeof(float))
                        .Select(p => p.Name)
                        .ToArray();

                    var pipeline = _mlContext.Transforms.Conversion
                        .MapValueToKey("Label")
                        .Append(_mlContext.Transforms.Concatenate("Features", featureColumns))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.MulticlassClassification.Trainers.LightGbm(
                            labelColumnName: "Label",
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 10,
                            learningRate: 0.1,
                            numberOfIterations: 300))
                        .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

                    OnLog?.Invoke($"[TradeSignal] LightGBM MultiClass 학습 시작 ({trainingData.Count}건, 피처 {featureColumns.Length}개)...");
                    _model = pipeline.Fit(split.TrainSet);

                    // 평가
                    var predictions = _model.Transform(split.TestSet);
                    var metrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");

                    var result = new TradeSignalMetrics
                    {
                        MicroAccuracy = metrics.MicroAccuracy,
                        MacroAccuracy = metrics.MacroAccuracy,
                        LogLoss = metrics.LogLoss,
                        SampleCount = trainingData.Count,
                        TrainedAt = DateTime.Now
                    };

                    OnLog?.Invoke($"[TradeSignal] 학습 완료 | MicroAcc={metrics.MicroAccuracy:P2}, MacroAcc={metrics.MacroAccuracy:P2}, LogLoss={metrics.LogLoss:F4}");

                    // 모델 저장
                    string modelPath = Path.Combine(DefaultModelDir, ModelFileName);
                    _mlContext.Model.Save(_model, dataView.Schema, modelPath);
                    OnLog?.Invoke($"[TradeSignal] 모델 저장: {modelPath}");

                    // PredictionEngine 갱신
                    lock (_predictLock)
                    {
                        _predictionEngine?.Dispose();
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<TradeSignalFeature, TradeSignalPrediction>(_model);
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[TradeSignal] 학습 실패: {ex.Message}");
                    return new TradeSignalMetrics();
                }
            }, token);
        }

        /// <summary>저장된 모델 로드</summary>
        public bool LoadModel()
        {
            try
            {
                string modelPath = Path.Combine(DefaultModelDir, ModelFileName);
                if (!File.Exists(modelPath))
                {
                    OnLog?.Invoke("[TradeSignal] 모델 파일 없음 — 학습 필요");
                    return false;
                }

                _model = _mlContext.Model.Load(modelPath, out _);
                lock (_predictLock)
                {
                    _predictionEngine?.Dispose();
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<TradeSignalFeature, TradeSignalPrediction>(_model);
                }

                OnLog?.Invoke($"[TradeSignal] 모델 로드 완료: {modelPath}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TradeSignal] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════
        // Phase 4: 실시간 추론
        // ═══════════════════════════════════════════

        /// <summary>
        /// 현재 CandleData를 기반으로 매매 신호 예측
        /// 반환: PredictedLabel(0=관망, 1=롱, 2=숏), Score 배열
        /// </summary>
        public TradeSignalPrediction? Predict(CandleData candle)
        {
            if (!IsModelLoaded || candle == null)
                return null;

            try
            {
                var feature = ExtractFeature(candle);
                lock (_predictLock)
                {
                    return _predictionEngine!.Predict(feature);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TradeSignal] 추론 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 예측 결과를 진입 방향과 신뢰도로 변환
        /// </summary>
        public static (string Direction, float Confidence) InterpretPrediction(TradeSignalPrediction prediction)
        {
            if (prediction?.Score == null || prediction.Score.Length < 3)
                return ("HOLD", 0f);

            float holdScore = prediction.Score[0];
            float longScore = prediction.Score[1];
            float shortScore = prediction.Score[2];
            float maxScore = Math.Max(holdScore, Math.Max(longScore, shortScore));

            if (prediction.PredictedLabel == 1 && longScore >= 0.50f)
                return ("LONG", longScore);
            if (prediction.PredictedLabel == 2 && shortScore >= 0.50f)
                return ("SHORT", shortScore);

            return ("HOLD", holdScore);
        }

        private static float Sanitize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0f;
            return value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _predictionEngine?.Dispose();
            _disposed = true;
        }
    }

    // ═══════════════════════════════════════════
    // 데이터 모델
    // ═══════════════════════════════════════════

    /// <summary>학습/추론용 피처 (24개)</summary>
    public class TradeSignalFeature
    {
        [ColumnName("Label")]
        public uint Label { get; set; } // 0=관망, 1=롱, 2=숏

        // 기본 지표
        public float RSI { get; set; }
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float MACD_Hist { get; set; }
        public float ATR { get; set; }
        public float ADX { get; set; }
        public float PlusDI { get; set; }
        public float MinusDI { get; set; }

        // 볼린저/이평선 파생
        public float BB_Width { get; set; }
        public float Price_To_BB_Mid { get; set; }
        public float Price_To_SMA20_Pct { get; set; }

        // 캔들 패턴
        public float Price_Change_Pct { get; set; }
        public float Candle_Body_Ratio { get; set; }
        public float Upper_Shadow_Ratio { get; set; }
        public float Lower_Shadow_Ratio { get; set; }

        // 거래량/OI
        public float Volume_Ratio { get; set; }
        public float Volume_Change_Pct { get; set; }
        public float OI_Change_Pct { get; set; }

        // 추세/피보나치
        public float Trend_Strength { get; set; }
        public float Fib_Position { get; set; }
        public float Stoch_K { get; set; }
        public float Stoch_D { get; set; }
        public float RSI_Divergence { get; set; }
        public float SentimentScore { get; set; }
    }

    /// <summary>3분류 예측 결과</summary>
    public class TradeSignalPrediction
    {
        [ColumnName("PredictedLabel")]
        public uint PredictedLabel { get; set; } // 0=관망, 1=롱, 2=숏

        [ColumnName("Score")]
        public float[] Score { get; set; } = Array.Empty<float>(); // [Hold, Long, Short] 확률
    }

    /// <summary>학습 결과 메트릭</summary>
    public class TradeSignalMetrics
    {
        public double MicroAccuracy { get; set; }
        public double MacroAccuracy { get; set; }
        public double LogLoss { get; set; }
        public int SampleCount { get; set; }
        public DateTime TrainedAt { get; set; }
    }
}
