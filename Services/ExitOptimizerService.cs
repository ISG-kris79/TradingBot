using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TradingBot.Services
{
    /// <summary>
    /// 최적 익절 타이밍 예측 모델 (EXIT_NOW / HOLD)
    /// 보유 중 포지션의 현재 상태(ROE, 최고ROE, 시장상태 등)를 입력받아
    /// 지금 익절해야 하는지 판단
    /// </summary>
    public class ExitOptimizerService : IDisposable
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _model;
        private PredictionEngine<ExitFeature, ExitPrediction>? _predictionEngine;
        private readonly object _predictLock = new();
        private bool _disposed;

        private static readonly string DefaultModelDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradingBot", "Models");
        private const string ModelFileName = "exit_optimizer_model.zip";

        public bool IsModelLoaded => _model != null && _predictionEngine != null;
        public event Action<string>? OnLog;

        /// <summary>
        /// 과거 트레이드 + 캔들 데이터에서 학습 데이터 생성
        /// 좋은 EXIT: 최고ROE 대비 50% 이상 지킨 청산 → Label=true
        /// 나쁜 EXIT: 최고ROE 대비 80% 이상 까먹은 청산 → Label=false (= 더 일찍 나왔어야)
        /// </summary>
        public static List<ExitFeature> BuildTrainingDataFromTrades(
            List<(decimal entryPrice, decimal exitPrice, decimal highestPrice, decimal lowestPrice,
                  bool isLong, int leverage, float bbWidth, float adx, float rsi,
                  float macdSlope, float volumeChange, double holdingMinutes, int regimeLabel)> trades)
        {
            var data = new List<ExitFeature>();
            if (trades == null) return data;

            foreach (var t in trades)
            {
                if (t.entryPrice <= 0 || t.highestPrice <= 0) continue;

                decimal priceChange = t.isLong
                    ? (t.exitPrice - t.entryPrice) / t.entryPrice
                    : (t.entryPrice - t.exitPrice) / t.entryPrice;
                decimal peakChange = t.isLong
                    ? (t.highestPrice - t.entryPrice) / t.entryPrice
                    : (t.entryPrice - t.lowestPrice) / t.entryPrice;

                float exitROE = (float)(priceChange * t.leverage * 100);
                float peakROE = (float)(peakChange * t.leverage * 100);

                if (peakROE <= 2f) continue; // 최고 ROE가 너무 낮으면 스킵

                float retainedRatio = peakROE > 0 ? exitROE / peakROE : 0;

                // 라벨: 최고ROE 대비 50% 이상 지킨 EXIT → 좋은 타이밍(true)
                bool goodExit = retainedRatio >= 0.5f;

                // 시뮬레이션: 다양한 "보유 중" 시점을 생성 (peak 근처, 중간, 하락 중)
                // Peak 시점 (EXIT_NOW가 정답)
                data.Add(new ExitFeature
                {
                    CurrentROE = peakROE,
                    HighestROE = peakROE,
                    ROE_Drawdown = 0f,
                    BB_Width = t.bbWidth,
                    ADX = t.adx,
                    RSI = t.rsi,
                    MACD_Slope = t.macdSlope,
                    Volume_Change_Pct = t.volumeChange,
                    HoldingMinutes = (float)t.holdingMinutes * 0.7f,
                    RegimeLabel = t.regimeLabel,
                    Label = true // peak에서 나가는 게 최선
                });

                // Exit 시점
                data.Add(new ExitFeature
                {
                    CurrentROE = exitROE,
                    HighestROE = peakROE,
                    ROE_Drawdown = peakROE - exitROE,
                    BB_Width = t.bbWidth,
                    ADX = t.adx,
                    RSI = t.rsi,
                    MACD_Slope = t.macdSlope,
                    Volume_Change_Pct = t.volumeChange,
                    HoldingMinutes = (float)t.holdingMinutes,
                    RegimeLabel = t.regimeLabel,
                    Label = goodExit
                });

                // 중간 시점 (peak의 60% ROE)
                float midROE = peakROE * 0.6f;
                data.Add(new ExitFeature
                {
                    CurrentROE = midROE,
                    HighestROE = peakROE,
                    ROE_Drawdown = peakROE - midROE,
                    BB_Width = t.bbWidth,
                    ADX = t.adx,
                    RSI = Math.Min(t.rsi + 5f, 90f), // peak 근처 RSI 약간 높게
                    MACD_Slope = t.macdSlope,
                    Volume_Change_Pct = t.volumeChange,
                    HoldingMinutes = (float)t.holdingMinutes * 0.5f,
                    RegimeLabel = t.regimeLabel,
                    Label = t.regimeLabel == 1 // 횡보면 여기서 나가는 게 맞음, 추세면 홀딩
                });
            }

            return data;
        }

        public async Task<bool> TrainAndSaveAsync(List<ExitFeature> data, CancellationToken token = default)
        {
            if (data == null || data.Count < 50)
            {
                OnLog?.Invoke($"[ExitOpt] 학습 데이터 부족: {data?.Count ?? 0}건 (최소 50건 필요)");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2, seed: 42);

                    string[] featureCols = { "CurrentROE", "HighestROE", "ROE_Drawdown", "BB_Width", "ADX",
                        "RSI", "MACD_Slope", "Volume_Change_Pct", "HoldingMinutes", "RegimeLabelFloat" };

                    var pipeline = _mlContext.Transforms.CustomMapping<ExitFeature, RegimeLabelMapped>(
                            (input, output) => output.RegimeLabelFloat = (float)input.RegimeLabel,
                            contractName: "RegimeToFloat")
                        .Append(_mlContext.Transforms.Concatenate("Features", featureCols))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                            labelColumnName: "Label",
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 10,
                            learningRate: 0.05,
                            numberOfIterations: 300));

                    OnLog?.Invoke($"[ExitOpt] LightGBM 이진분류 학습 시작 ({data.Count}건)...");
                    _model = pipeline.Fit(split.TrainSet);

                    var predictions = _model.Transform(split.TestSet);
                    var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");
                    OnLog?.Invoke($"[ExitOpt] 학습 완료 | Acc={metrics.Accuracy:P2}, F1={metrics.F1Score:P2}, AUC={metrics.AreaUnderRocCurve:P2}");

                    Directory.CreateDirectory(DefaultModelDir);
                    string path = Path.Combine(DefaultModelDir, ModelFileName);
                    _mlContext.Model.Save(_model, dataView.Schema, path);

                    lock (_predictLock)
                    {
                        _predictionEngine?.Dispose();
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<ExitFeature, ExitPrediction>(_model);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[ExitOpt] 학습 실패: {ex.Message}");
                    return false;
                }
            }, token);
        }

        public bool TryLoadModel()
        {
            try
            {
                string path = Path.Combine(DefaultModelDir, ModelFileName);
                if (!File.Exists(path)) return false;

                _model = _mlContext.Model.Load(path, out _);
                lock (_predictLock)
                {
                    _predictionEngine?.Dispose();
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<ExitFeature, ExitPrediction>(_model);
                }
                OnLog?.Invoke($"[ExitOpt] 모델 로드 완료: {path}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[ExitOpt] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>EXIT_NOW=true, HOLD=false + 확률</summary>
        public (bool exitNow, float probability) Predict(ExitFeature feature)
        {
            lock (_predictLock)
            {
                if (_predictionEngine == null)
                    return (false, 0f);

                var pred = _predictionEngine.Predict(feature);
                float prob = pred.Probability;
                if (float.IsNaN(prob) || float.IsInfinity(prob))
                    prob = 0.5f;

                return (pred.ExitNow, prob);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_predictLock) { _predictionEngine?.Dispose(); }
        }
    }

    public class ExitFeature
    {
        public bool Label { get; set; }           // true=EXIT_NOW (좋은 타이밍), false=HOLD

        public float CurrentROE { get; set; }     // 현재 ROE%
        public float HighestROE { get; set; }     // 보유 기간 최고 ROE%
        public float ROE_Drawdown { get; set; }   // HighestROE - CurrentROE (되돌림 크기)
        public float BB_Width { get; set; }       // BB폭 (횡보 지표)
        public float ADX { get; set; }            // 추세 강도
        public float RSI { get; set; }
        public float MACD_Slope { get; set; }     // MACD 히스토그램 기울기
        public float Volume_Change_Pct { get; set; }
        public float HoldingMinutes { get; set; } // 보유 시간(분)
        public int RegimeLabel { get; set; }      // 0=Trending, 1=Sideways, 2=Volatile
    }

    // CustomMapping 출력용
    public class RegimeLabelMapped
    {
        public float RegimeLabelFloat { get; set; }
    }

    public class ExitPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool ExitNow { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }
    }
}
