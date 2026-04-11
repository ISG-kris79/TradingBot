using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.8.0] Breakout Price Classifier (접근 C — 급등 코인 대상)
    ///
    /// 목적:
    ///   - Consolidation(횡보 수축) → Breakout(돌파) 패턴을 AI로 인식
    ///   - 돌파 트리거 가격을 예측해서 선제 LIMIT 주문 배치
    ///
    /// 특화:
    ///   - 급등 후보 코인의 "숨고르기" 구간 식별에 최적화
    ///   - 볼린저 밴드 수축 + 거래량 저조 + 짧은 캔들 실체가 consolidation 시그널
    ///   - 직후 볼륨 spike + 상단 이탈 = breakout 확정
    ///
    /// 학습 타겟:
    ///   - Classification: 최근 consolidation → +2% 돌파 여부 (binary)
    ///   - Regression (2차): 돌파 후 예측 가격 수준
    /// </summary>
    public class BreakoutPriceClassifier
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _model;
        private PredictionEngine<BreakoutFeature, BreakoutPrediction>? _engine;
        private readonly object _lock = new();

        private readonly ConcurrentQueue<BreakoutFeature> _trainingBuffer = new();
        private const int MinTrainingSamples = 100;
        private const int MaxTrainingSamples = 20_000;

        private static readonly string ModelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "Models");
        private static readonly string ModelPath = Path.Combine(ModelDir, "breakout_classifier.zip");

        public event Action<string>? OnLog;
        public bool IsModelReady => _engine != null;

        public BreakoutPriceClassifier()
        {
            Directory.CreateDirectory(ModelDir);
            TryLoadModel();
        }

        /// <summary>과거 CandleData로 consolidation → breakout 패턴 라벨링</summary>
        public int GenerateTrainingDataFromCandles(string symbol, List<CandleData> candles)
        {
            if (candles == null || candles.Count < 80) return 0;

            const int consolidationLookback = 20;  // 직전 20봉 consolidation 분석
            const int breakoutLookAhead = 12;      // 향후 12봉(1h) 내 돌파 여부 관찰
            const float breakoutThreshold = 2.0f;  // +2% 돌파 기준

            int added = 0;
            for (int t = consolidationLookback; t < candles.Count - breakoutLookAhead; t++)
            {
                // Consolidation 구간 체크 — 최근 20봉 가격 range, 볼륨 평균
                var consolidationWindow = candles.GetRange(t - consolidationLookback, consolidationLookback);
                float highMax = (float)consolidationWindow.Max(c => c.High);
                float lowMin = (float)consolidationWindow.Min(c => c.Low);
                float range = highMax - lowMin;
                if (range <= 0) continue;

                float baseClose = (float)candles[t].Close;
                float relativeRange = range / baseClose * 100f;

                // Consolidation 조건: range < 5% (과한 변동 제외)
                if (relativeRange > 5.0f) continue;

                // 볼륨 수축 확인: 최근 5봉 평균 < 직전 15봉 평균
                var recent5 = consolidationWindow.TakeLast(5).ToList();
                var prior15 = consolidationWindow.Take(consolidationWindow.Count - 5).ToList();
                double recentVol = recent5.Average(c => (double)c.Volume);
                double priorVol = prior15.Average(c => (double)c.Volume);
                float volContraction = priorVol > 0 ? (float)(recentVol / priorVol) : 1f;

                // 캔들 실체 평균 (작을수록 consolidation 강함)
                float avgBody = (float)consolidationWindow.Average(c => (double)Math.Abs(c.Close - c.Open));
                float bodyRatio = avgBody / baseClose * 100f;

                // 향후 breakoutLookAhead 봉 중 consolidation high 대비 +2% 초과 여부
                var futureWindow = candles.GetRange(t, breakoutLookAhead);
                float futureMaxHigh = (float)futureWindow.Max(c => c.High);
                float breakoutRisePct = (futureMaxHigh - highMax) / highMax * 100f;
                bool didBreakout = breakoutRisePct >= breakoutThreshold;

                // 돌파 시 실제 트리거 가격 (consolidation high 대비)
                float predictedBreakoutPrice = highMax * (1f + breakoutThreshold / 100f);

                var feature = new BreakoutFeature
                {
                    RelativeRange = relativeRange,
                    VolContraction = volContraction,
                    BodyRatio = bodyRatio,
                    BasePrice = baseClose,
                    ConsolidationHigh = highMax,
                    ConsolidationLow = lowMin,
                    DidBreakout = didBreakout
                };

                _trainingBuffer.Enqueue(feature);
                added++;

                while (_trainingBuffer.Count > MaxTrainingSamples)
                    _trainingBuffer.TryDequeue(out _);
            }

            OnLog?.Invoke($"[BreakoutClassifier] {symbol} → {added}개 샘플 추가 (총 버퍼 {_trainingBuffer.Count})");
            return added;
        }

        /// <summary>학습 (Binary classification)</summary>
        public async Task<bool> TrainAsync()
        {
            if (_trainingBuffer.Count < MinTrainingSamples)
            {
                OnLog?.Invoke($"[BreakoutClassifier] 학습 데이터 부족: {_trainingBuffer.Count}/{MinTrainingSamples}");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var data = _trainingBuffer.ToArray().ToList();
                    OnLog?.Invoke($"[BreakoutClassifier] 학습 시작: {data.Count}개 샘플");

                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                    var pipeline = _mlContext.Transforms.Concatenate("Features",
                            nameof(BreakoutFeature.RelativeRange),
                            nameof(BreakoutFeature.VolContraction),
                            nameof(BreakoutFeature.BodyRatio))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.BinaryClassification.Trainers.LightGbm(
                            labelColumnName: nameof(BreakoutFeature.DidBreakout),
                            featureColumnName: "Features"));

                    var model = pipeline.Fit(split.TrainSet);
                    var predictions = model.Transform(split.TestSet);
                    var metrics = _mlContext.BinaryClassification.Evaluate(predictions,
                        labelColumnName: nameof(BreakoutFeature.DidBreakout));

                    lock (_lock)
                    {
                        _model = model;
                        _engine?.Dispose();
                        _engine = _mlContext.Model.CreatePredictionEngine<BreakoutFeature, BreakoutPrediction>(model);
                        _mlContext.Model.Save(model, dataView.Schema, ModelPath);
                    }

                    OnLog?.Invoke($"[BreakoutClassifier] 학습 완료 | Acc={metrics.Accuracy:P2} AUC={metrics.AreaUnderRocCurve:F3} F1={metrics.F1Score:F3}");
                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[BreakoutClassifier] 학습 실패: {ex.Message}");
                    return false;
                }
            });
        }

        public bool TryLoadModel()
        {
            try
            {
                if (!File.Exists(ModelPath)) return false;
                lock (_lock)
                {
                    _model = _mlContext.Model.Load(ModelPath, out _);
                    _engine?.Dispose();
                    _engine = _mlContext.Model.CreatePredictionEngine<BreakoutFeature, BreakoutPrediction>(_model);
                }
                OnLog?.Invoke("[BreakoutClassifier] 기존 모델 로드 완료");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[BreakoutClassifier] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 실시간 consolidation 감지 + 돌파 확률 예측.
        /// 반환:
        ///   (probability, breakoutPrice) — probability > 0.6 이면 breakout 임박
        ///   null → 예측 불가
        /// </summary>
        public (float Probability, decimal BreakoutPrice)? PredictBreakout(List<CandleData> recent20)
        {
            lock (_lock) { if (_engine == null) return null; }
            if (recent20 == null || recent20.Count < 20) return null;

            try
            {
                float highMax = (float)recent20.Max(c => c.High);
                float lowMin = (float)recent20.Min(c => c.Low);
                float range = highMax - lowMin;
                if (range <= 0) return null;

                float baseClose = (float)recent20[^1].Close;
                float relativeRange = range / baseClose * 100f;
                if (relativeRange > 5.0f) return null;  // consolidation 아님

                var recent5 = recent20.TakeLast(5).ToList();
                var prior15 = recent20.Take(recent20.Count - 5).ToList();
                double recentVol = recent5.Average(c => (double)c.Volume);
                double priorVol = prior15.Average(c => (double)c.Volume);
                float volContraction = priorVol > 0 ? (float)(recentVol / priorVol) : 1f;

                float avgBody = (float)recent20.Average(c => (double)Math.Abs(c.Close - c.Open));
                float bodyRatio = avgBody / baseClose * 100f;

                var feature = new BreakoutFeature
                {
                    RelativeRange = relativeRange,
                    VolContraction = volContraction,
                    BodyRatio = bodyRatio
                };

                BreakoutPrediction pred;
                lock (_lock) { pred = _engine!.Predict(feature); }

                decimal breakoutPrice = (decimal)highMax * 1.02m; // +2% 트리거
                return (pred.Probability, breakoutPrice);
            }
            catch
            {
                return null;
            }
        }

        public int BufferedSampleCount => _trainingBuffer.Count;
    }

    public class BreakoutFeature
    {
        public float RelativeRange { get; set; }        // (high-low)/close %
        public float VolContraction { get; set; }       // recent5/prior15
        public float BodyRatio { get; set; }            // avg body / close %

        // 참고용 (학습 입력 아님)
        public float BasePrice { get; set; }
        public float ConsolidationHigh { get; set; }
        public float ConsolidationLow { get; set; }

        // 레이블
        public bool DidBreakout { get; set; }
    }

    public class BreakoutPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }

        [ColumnName("Probability")]
        public float Probability { get; set; }
    }
}
