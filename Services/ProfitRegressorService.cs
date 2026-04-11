using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [미래지향 AI] 수익률 회귀 모델 — "지금 진입하면 5분 뒤 수익률이 얼마인가"
    ///
    /// 기존 시스템(이진 분류: 진입/비진입)과 차이점:
    ///   - 이진 분류: "진입할까 말까" → YES/NO
    ///   - 회귀 모델: "진입하면 얼마를 벌까" → +2.3% 예측 → 크게 진입 / -0.5% 예측 → 패스
    ///
    /// 학습 데이터: 실제 거래 결과 (EntryPrice, ExitPrice, P&amp;L, 보유시간)
    /// 입력 피처: RSI, BB위치, ATR, 거래량비율, 모멘텀, AI확률 등
    /// 출력: 향후 5분/15분/1시간 예상 수익률 (%)
    /// </summary>
    public class ProfitRegressorService
    {
        private readonly MLContext _mlContext = new(seed: 42);
        private ITransformer? _model;
        private PredictionEngine<ProfitFeature, ProfitPrediction>? _engine;
        private readonly object _lock = new();

        // 학습 데이터 수집 버퍼 (실제 거래 결과)
        private readonly ConcurrentQueue<ProfitFeature> _trainingBuffer = new();
        private const int MinTrainingSamples = 50;
        private const int MaxTrainingSamples = 5000;
        private DateTime _lastTrainTime = DateTime.MinValue;

        public event Action<string>? OnLog;

        public bool IsModelReady => _engine != null;

        /// <summary>실제 거래 결과를 학습 데이터로 추가</summary>
        public void RecordTradeOutcome(
            float rsi, float bbPosition, float atr, float volumeRatio,
            float momentum, float mlConfidence,
            float spreadPct, float fundingRate,
            float actualProfitPct, float holdingMinutes)
        {
            var feature = new ProfitFeature
            {
                RSI = rsi,
                BBPosition = bbPosition,
                ATR = atr,
                VolumeRatio = volumeRatio,
                Momentum = momentum,
                MLConfidence = mlConfidence,
                SpreadPct = spreadPct,
                FundingRate = fundingRate,
                HourOfDay = DateTime.Now.Hour,
                DayOfWeek = (int)DateTime.Now.DayOfWeek,
                // 레이블
                ProfitPct = actualProfitPct,
                HoldingMinutes = holdingMinutes
            };

            _trainingBuffer.Enqueue(feature);

            // 버퍼 크기 제한
            while (_trainingBuffer.Count > MaxTrainingSamples)
                _trainingBuffer.TryDequeue(out _);
        }

        /// <summary>DB TradeHistory에서 과거 거래 내역을 학습 데이터로 로드</summary>
        public async Task<int> LoadFromTradeHistoryAsync(DbManager dbManager, int userId, int days = 30)
        {
            try
            {
                var startDate = DateTime.Now.AddDays(-days);
                var endDate = DateTime.Now;
                var trades = await dbManager.GetTradeHistoryAsync(userId, startDate, endDate, limit: 2000);

                int loaded = 0;
                foreach (var trade in trades)
                {
                    // 청산 완료된 거래만 (PnL 있음)
                    if (trade.EntryPrice <= 0 || trade.ExitPrice <= 0 || trade.ExitTime == default)
                        continue;

                    float holdingMin = (float)(trade.ExitTime - trade.EntryTime).TotalMinutes;
                    if (holdingMin <= 0) continue;

                    float pnlPct = (float)trade.PnLPercent;

                    // 진입 시점 캔들 지표 조회 시도
                    float rsi = 50f, bbPos = 0.5f, atr = 0f, volRatio = 1f, momentum = 0f;
                    try
                    {
                        var candles = await dbManager.GetRecentCandleDataAsync(trade.Symbol, 30);
                        if (candles != null && candles.Count > 0)
                        {
                            // 진입 시점에 가장 가까운 캔들 찾기
                            var nearest = candles.OrderBy(c => Math.Abs((c.OpenTime - trade.EntryTime).TotalMinutes)).FirstOrDefault();
                            if (nearest != null)
                            {
                                rsi = nearest.RSI > 0 ? nearest.RSI : 50f;
                                bbPos = nearest.BB_Width > 0 ? Math.Clamp(nearest.BB_Width / 100f, 0f, 1f) : 0.5f;
                                atr = nearest.ATR;
                                volRatio = nearest.Volume_Ratio > 0 ? nearest.Volume_Ratio : 1f;
                                momentum = nearest.Price_Change_Pct;
                            }
                        }
                    }
                    catch { /* 캔들 데이터 없으면 기본값 사용 */ }

                    RecordTradeOutcome(
                        rsi, bbPos, atr, volRatio, momentum,
                        trade.AiScore > 0 && trade.AiScore <= 1 ? trade.AiScore : trade.AiScore / 100f,
                        0f, 0f,
                        pnlPct, holdingMin);
                    loaded++;
                }

                OnLog?.Invoke($"[ProfitRegressor] DB에서 {loaded}건 거래 내역 로드 완료 (최근 {days}일, 총 버퍼: {_trainingBuffer.Count}건)");
                return loaded;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[ProfitRegressor] DB 로드 실패: {ex.Message}");
                return 0;
            }
        }

        /// <summary>축적된 거래 결과로 모델 학습 (비동기)</summary>
        public async Task<bool> TrainAsync()
        {
            if (_trainingBuffer.Count < MinTrainingSamples)
            {
                OnLog?.Invoke($"[ProfitRegressor] 학습 데이터 부족: {_trainingBuffer.Count}/{MinTrainingSamples}");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    var data = _trainingBuffer.ToArray().ToList();
                    OnLog?.Invoke($"[ProfitRegressor] 학습 시작: {data.Count}개 거래 결과");

                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var split = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                    var pipeline = _mlContext.Transforms.Concatenate("Features",
                            nameof(ProfitFeature.RSI),
                            nameof(ProfitFeature.BBPosition),
                            nameof(ProfitFeature.ATR),
                            nameof(ProfitFeature.VolumeRatio),
                            nameof(ProfitFeature.Momentum),
                            nameof(ProfitFeature.MLConfidence),
                            nameof(ProfitFeature.SpreadPct),
                            nameof(ProfitFeature.FundingRate),
                            nameof(ProfitFeature.HourOfDay),
                            nameof(ProfitFeature.DayOfWeek))
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.FastTree(
                            labelColumnName: nameof(ProfitFeature.ProfitPct),
                            featureColumnName: "Features",
                            numberOfLeaves: 20,
                            minimumExampleCountPerLeaf: 5,
                            numberOfTrees: 100,
                            learningRate: 0.05));

                    var model = pipeline.Fit(split.TrainSet);

                    // 검증
                    var predictions = model.Transform(split.TestSet);
                    var metrics = _mlContext.Regression.Evaluate(predictions, labelColumnName: nameof(ProfitFeature.ProfitPct));

                    lock (_lock)
                    {
                        _model = model;
                        _engine?.Dispose();
                        _engine = _mlContext.Model.CreatePredictionEngine<ProfitFeature, ProfitPrediction>(model);
                    }

                    _lastTrainTime = DateTime.Now;
                    OnLog?.Invoke($"[ProfitRegressor] 학습 완료 | R²={metrics.RSquared:F3} MAE={metrics.MeanAbsoluteError:F4} RMSE={metrics.RootMeanSquaredError:F4}");
                    return true;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[ProfitRegressor] 학습 실패: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// 현재 시장 상태에서 예상 수익률 예측
        /// 반환: 예상 수익률 (%, 양수=이익, 음수=손실)
        /// </summary>
        public float? PredictProfit(
            float rsi, float bbPosition, float atr, float volumeRatio,
            float momentum, float mlConfidence,
            float spreadPct = 0f, float fundingRate = 0f)
        {
            lock (_lock)
            {
                if (_engine == null) return null;
            }

            try
            {
                var feature = new ProfitFeature
                {
                    RSI = rsi,
                    BBPosition = bbPosition,
                    ATR = atr,
                    VolumeRatio = volumeRatio,
                    Momentum = momentum,
                    MLConfidence = mlConfidence,
                    SpreadPct = spreadPct,
                    FundingRate = fundingRate,
                    HourOfDay = DateTime.Now.Hour,
                    DayOfWeek = (int)DateTime.Now.DayOfWeek
                };

                ProfitPrediction result;
                lock (_lock) { result = _engine!.Predict(feature); }

                if (float.IsNaN(result.Score) || float.IsInfinity(result.Score))
                    return null;

                return result.Score;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 예상 수익률 기반 포지션 사이즈 배수 계산
        /// 높은 수익 예측 → 크게 진입, 낮은/음수 → 작게/패스
        /// </summary>
        public decimal GetPositionMultiplier(float? predictedProfitPct)
        {
            if (!predictedProfitPct.HasValue) return 1.0m; // 모델 없으면 기본

            float p = predictedProfitPct.Value;
            return p switch
            {
                >= 3.0f => 1.5m,   // 강한 수익 예측 → 150%
                >= 2.0f => 1.3m,   // 좋은 수익 → 130%
                >= 1.0f => 1.0m,   // 보통 → 100%
                >= 0.5f => 0.7m,   // 약한 수익 → 70%
                >= 0f   => 0.5m,   // 미약 → 50%
                _       => 0m      // 손실 예측 → 진입 금지
            };
        }
    }

    // ─── 수익률 예측 입출력 ──────────────────────────────────

    public class ProfitFeature
    {
        // 시장 상태 피처
        public float RSI { get; set; }
        public float BBPosition { get; set; }     // 0.0~1.0
        public float ATR { get; set; }
        public float VolumeRatio { get; set; }    // 현재/평균
        public float Momentum { get; set; }       // 가격 변화율
        public float MLConfidence { get; set; }   // ML.NET 확률
        public float SpreadPct { get; set; }      // 호가 스프레드
        public float FundingRate { get; set; }    // 펀딩비

        // 시간 피처
        public float HourOfDay { get; set; }
        public float DayOfWeek { get; set; }

        // 레이블 (실제 결과)
        [ColumnName("Label")]
        public float ProfitPct { get; set; }      // 실제 수익률 (%)
        public float HoldingMinutes { get; set; }  // 보유 시간 (분)
    }

    public class ProfitPrediction
    {
        [ColumnName("Score")]
        public float Score { get; set; }  // 예측 수익률 (%)
    }
}
