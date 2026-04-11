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
    /// [v4.8.0] 최적 진입 가격 예측 — Pullback Depth Regression (접근 A)
    ///
    /// 아이디어:
    ///   - 거래량 급증 감지 시 즉시 시장가 진입하는 대신,
    ///     ML이 향후 몇 % 눌림이 일어날지 예측 → 그 지점에 LIMIT 주문 배치
    ///   - 예측 실패 시 기존 로직으로 fallback (+0.5% 확인 후 시장가)
    ///
    /// 타겟 라벨링:
    ///   - 각 캔들 t 기준으로 향후 24봉(2h) 관찰
    ///   - 그 구간에서 최고가가 현재 대비 +2% 이상 → positive 샘플
    ///   - positive 샘플의 pullback% = (현재가 - 최저가) / 현재가 × 100
    ///   - negative 샘플은 학습에서 제외 (noise 방지)
    /// </summary>
    public class OptimalEntryPriceRegressor
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _model;
        private PredictionEngine<EntryPriceFeature, EntryPricePrediction>? _engine;
        private readonly object _lock = new();

        private readonly ConcurrentQueue<EntryPriceFeature> _trainingBuffer = new();
        private const int MinTrainingSamples = 100;
        private const int MaxTrainingSamples = 20_000;
        private DateTime _lastTrainTime = DateTime.MinValue;

        // 모델 파일 경로 (%LOCALAPPDATA%\TradingBot\Models\)
        private static readonly string ModelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "Models");
        private static readonly string ModelPath = Path.Combine(ModelDir, "optimal_entry_price.zip");

        public event Action<string>? OnLog;
        public bool IsModelReady => _engine != null;

        public OptimalEntryPriceRegressor()
        {
            Directory.CreateDirectory(ModelDir);
            TryLoadModel();
        }

        /// <summary>
        /// 과거 CandleData로부터 학습 데이터 일괄 생성.
        /// 각 심볼별로 candleData를 받아서 라벨링된 샘플을 버퍼에 추가.
        /// </summary>
        public int GenerateTrainingDataFromCandles(string symbol, List<CandleData> candles)
        {
            if (candles == null || candles.Count < 100) return 0;

            // 라벨링 파라미터 — 하드코딩 아님, 과거 분포 기반
            const int lookAheadBars = 24;      // 2h = 24봉 × 5분
            const float minRallyPct = 2.0f;    // +2% 이상 랠리 발생한 경우만 positive
            const int featureLookback = 30;    // 피처 추출용 과거 30봉

            int added = 0;
            for (int t = featureLookback; t < candles.Count - lookAheadBars; t++)
            {
                var baseCandle = candles[t];
                float basePrice = (float)baseCandle.Close;
                if (basePrice <= 0) continue;

                // 향후 관찰 구간에서 최고가/최저가 찾기
                float futureHigh = basePrice;
                float futureLow = basePrice;
                for (int k = 1; k <= lookAheadBars; k++)
                {
                    var futureCandle = candles[t + k];
                    if ((float)futureCandle.High > futureHigh) futureHigh = (float)futureCandle.High;
                    if ((float)futureCandle.Low < futureLow) futureLow = (float)futureCandle.Low;
                }

                float maxRisePct = (futureHigh - basePrice) / basePrice * 100f;
                if (maxRisePct < minRallyPct) continue;  // 랠리 없음 → 제외

                float pullbackPct = (basePrice - futureLow) / basePrice * 100f;
                if (pullbackPct < 0) pullbackPct = 0;   // 음수 방어 (전진만 한 경우)

                // 피처 추출 (간소화 버전, ProfitFeature와 유사 구성)
                var window = candles.GetRange(t - featureLookback + 1, featureLookback);
                var feature = BuildFeatureFromWindow(window, baseCandle, pullbackPct);
                if (feature == null) continue;

                _trainingBuffer.Enqueue(feature);
                added++;

                // 버퍼 상한
                while (_trainingBuffer.Count > MaxTrainingSamples)
                    _trainingBuffer.TryDequeue(out _);
            }

            OnLog?.Invoke($"[EntryPriceRegressor] {symbol} → {added}개 샘플 추가 (총 버퍼 {_trainingBuffer.Count})");
            return added;
        }

        private EntryPriceFeature? BuildFeatureFromWindow(List<CandleData> window, CandleData baseCandle, float targetPullbackPct)
        {
            if (window.Count < 14) return null;

            // RSI 14
            double gain = 0, loss = 0;
            for (int i = window.Count - 14; i < window.Count; i++)
            {
                if (i <= 0) continue;
                double diff = (double)(window[i].Close - window[i - 1].Close);
                if (diff > 0) gain += diff; else loss += -diff;
            }
            double avgGain = gain / 14.0, avgLoss = loss / 14.0;
            float rsi = (avgLoss == 0) ? 70f : (float)(100.0 - (100.0 / (1.0 + avgGain / avgLoss)));

            // ATR 14
            double tr = 0;
            for (int i = window.Count - 14; i < window.Count; i++)
            {
                if (i <= 0) continue;
                double h = (double)window[i].High;
                double l = (double)window[i].Low;
                double pc = (double)window[i - 1].Close;
                tr += Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            }
            float atr = (float)(tr / 14.0);

            // 볼륨 비율 (최근 / 평균)
            double avgVol = window.Take(window.Count - 1).Average(c => (double)c.Volume);
            float volRatio = avgVol > 0 ? (float)((double)baseCandle.Volume / avgVol) : 1f;

            // Momentum (최근 5봉 수익률)
            float momentum = 0f;
            if (window.Count >= 6 && window[^6].Close > 0)
                momentum = (float)(((double)baseCandle.Close / (double)window[^6].Close - 1.0) * 100.0);

            // BB Position (최근 20봉 min/max 내 상대 위치 — 0~1)
            var last20 = window.TakeLast(20).ToList();
            double minC = (double)last20.Min(c => c.Close);
            double maxC = (double)last20.Max(c => c.Close);
            float bbPos = (maxC - minC) > 0
                ? (float)(((double)baseCandle.Close - minC) / (maxC - minC))
                : 0.5f;

            // 변동성 — 최근 10봉 캔들 실체 크기 표준편차
            var bodySizes = window.TakeLast(10).Select(c => (double)Math.Abs((double)(c.Close - c.Open))).ToList();
            double meanBodySize = bodySizes.Average();
            float volatility = (float)Math.Sqrt(bodySizes.Average(b => (b - meanBodySize) * (b - meanBodySize)));

            return new EntryPriceFeature
            {
                RSI = rsi,
                BBPosition = bbPos,
                ATR = atr,
                VolumeRatio = volRatio,
                Momentum = momentum,
                Volatility = volatility,
                HourOfDay = baseCandle.OpenTime.Hour,
                PullbackPct = targetPullbackPct
            };
        }

        /// <summary>버퍼에 쌓인 샘플로 모델 학습</summary>
        public async Task<bool> TrainAsync()
        {
            if (_trainingBuffer.Count < MinTrainingSamples)
            {
                OnLog?.Invoke($"[EntryPriceRegressor] 학습 데이터 부족: {_trainingBuffer.Count}/{MinTrainingSamples}");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var data = _trainingBuffer.ToArray().ToList();
                    OnLog?.Invoke($"[EntryPriceRegressor] 학습 시작: {data.Count}개 샘플");

                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                    var pipeline = _mlContext.Transforms.Concatenate("Features",
                            nameof(EntryPriceFeature.RSI),
                            nameof(EntryPriceFeature.BBPosition),
                            nameof(EntryPriceFeature.ATR),
                            nameof(EntryPriceFeature.VolumeRatio),
                            nameof(EntryPriceFeature.Momentum),
                            nameof(EntryPriceFeature.Volatility),
                            nameof(EntryPriceFeature.HourOfDay))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            labelColumnName: nameof(EntryPriceFeature.PullbackPct),
                            featureColumnName: "Features",
                            numberOfLeaves: 31,
                            minimumExampleCountPerLeaf: 10,
                            numberOfIterations: 200,
                            learningRate: 0.05));

                    var model = pipeline.Fit(split.TrainSet);
                    var predictions = model.Transform(split.TestSet);
                    var metrics = _mlContext.Regression.Evaluate(predictions,
                        labelColumnName: nameof(EntryPriceFeature.PullbackPct));

                    lock (_lock)
                    {
                        _model = model;
                        _engine?.Dispose();
                        _engine = _mlContext.Model.CreatePredictionEngine<EntryPriceFeature, EntryPricePrediction>(model);
                        // 모델 저장
                        _mlContext.Model.Save(model, dataView.Schema, ModelPath);
                    }

                    _lastTrainTime = DateTime.Now;
                    OnLog?.Invoke($"[EntryPriceRegressor] 학습 완료 | R²={metrics.RSquared:F3} MAE={metrics.MeanAbsoluteError:F3}% RMSE={metrics.RootMeanSquaredError:F3}%");
                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[EntryPriceRegressor] 학습 실패: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>저장된 모델 로드</summary>
        public bool TryLoadModel()
        {
            try
            {
                if (!File.Exists(ModelPath)) return false;
                lock (_lock)
                {
                    _model = _mlContext.Model.Load(ModelPath, out _);
                    _engine?.Dispose();
                    _engine = _mlContext.Model.CreatePredictionEngine<EntryPriceFeature, EntryPricePrediction>(_model);
                }
                OnLog?.Invoke("[EntryPriceRegressor] 기존 모델 로드 완료");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[EntryPriceRegressor] 모델 로드 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 실시간 진입 시 pullback % 예측.
        /// 반환값:
        ///   null → 예측 불가 (모델 미준비)
        ///   0 이하 → pullback 없음 예상 → 즉시 진입 권장
        ///   양수 → 예측된 pullback % (현재가 × (1 - p/100) 에 LIMIT 주문 권장)
        /// </summary>
        public float? PredictPullbackPct(
            float rsi, float bbPosition, float atr, float volumeRatio,
            float momentum, float volatility, int hourOfDay)
        {
            lock (_lock)
            {
                if (_engine == null) return null;
            }

            try
            {
                var feature = new EntryPriceFeature
                {
                    RSI = rsi,
                    BBPosition = bbPosition,
                    ATR = atr,
                    VolumeRatio = volumeRatio,
                    Momentum = momentum,
                    Volatility = volatility,
                    HourOfDay = hourOfDay
                };

                EntryPricePrediction result;
                lock (_lock) { result = _engine!.Predict(feature); }

                if (float.IsNaN(result.Score) || float.IsInfinity(result.Score))
                    return null;

                // 합리적 범위로 clamp (0~3%)
                return Math.Clamp(result.Score, 0f, 3f);
            }
            catch
            {
                return null;
            }
        }

        public int BufferedSampleCount => _trainingBuffer.Count;
    }

    // ─── 피처/예측 입출력 ──────────────────────────────
    public class EntryPriceFeature
    {
        public float RSI { get; set; }
        public float BBPosition { get; set; }       // 0~1
        public float ATR { get; set; }
        public float VolumeRatio { get; set; }
        public float Momentum { get; set; }         // 최근 5봉 %
        public float Volatility { get; set; }       // 캔들 실체 std
        public float HourOfDay { get; set; }

        // 레이블 (예측 대상)
        public float PullbackPct { get; set; }
    }

    public class EntryPricePrediction
    {
        [ColumnName("Score")]
        public float Score { get; set; }
    }
}
