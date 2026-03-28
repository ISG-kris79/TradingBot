using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Services
{
    /// <summary>
    /// ML.NET SSA (Singular Spectrum Analysis) 기반 시계열 가격 예측
    ///
    /// 역할: 향후 5~10분간의 예상 가격 변동 폭(Confidence Interval) 산출
    /// 출력: 예측 가격 + UpperBound + LowerBound → SkiaSharp 차트에 반투명 영역 렌더링
    ///
    /// 파라미터 (1분봉 단타 최적화):
    ///   WindowSize=20, SeriesLength=100, Horizon=5, ConfidenceLevel=0.95
    /// </summary>
    public class SsaPriceForecastService
    {
        private readonly MLContext _mlContext;
        private ITransformer? _model;
        private PredictionEngine<SsaInput, SsaOutput>? _engine;
        private readonly object _lock = new();

        // SSA 하이퍼파라미터
        private const int WindowSize = 20;       // 패턴 인식 윈도우
        private const int SeriesLength = 100;     // 학습 시리즈 길이
        private const int Horizon = 5;            // 미래 예측 범위 (5분봉 = 25분)
        private const float ConfidenceLevel = 0.95f;

        public event Action<string>? OnLog;

        public SsaPriceForecastService()
        {
            _mlContext = new MLContext(seed: 42);
        }

        /// <summary>
        /// 과거 가격 데이터로 SSA 모델 학습
        /// 최소 SeriesLength(100)개 이상의 데이터 필요
        /// </summary>
        public bool Train(IEnumerable<float> closePrices)
        {
            try
            {
                var priceList = closePrices.ToList();
                if (priceList.Count < SeriesLength)
                {
                    OnLog?.Invoke($"[SSA] 데이터 부족: {priceList.Count}/{SeriesLength}개");
                    return false;
                }

                // 데이터 정규화: 기준가(첫 번째 가격) 대비 변화율로 변환
                // 소수점 정밀도 문제 방지 + 모델 학습 안정성 향상
                float basePrice = priceList[0];
                var normalizedData = priceList.Select(p => new SsaInput
                {
                    Value = (p - basePrice) / basePrice * 10000f // 기준가 대비 변화량 (bps)
                }).ToList();

                var dataView = _mlContext.Data.LoadFromEnumerable(normalizedData);

                var pipeline = _mlContext.Forecasting.ForecastBySsa(
                    outputColumnName: nameof(SsaOutput.Forecast),
                    inputColumnName: nameof(SsaInput.Value),
                    windowSize: WindowSize,
                    seriesLength: SeriesLength,
                    trainSize: priceList.Count,
                    horizon: Horizon,
                    confidenceLevel: ConfidenceLevel,
                    confidenceLowerBoundColumn: nameof(SsaOutput.LowerBound),
                    confidenceUpperBoundColumn: nameof(SsaOutput.UpperBound));

                lock (_lock)
                {
                    _model = pipeline.Fit(dataView);
                    _engine = _mlContext.Model.CreatePredictionEngine<SsaInput, SsaOutput>(_model);
                }

                OnLog?.Invoke($"[SSA] 학습 완료: {priceList.Count}개 데이터, WindowSize={WindowSize}, Horizon={Horizon}");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[SSA] 학습 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 예측 실행: 향후 Horizon개의 예측값 + 상한/하한 반환
        /// 반환값의 가격은 원래 스케일로 복원됨
        /// </summary>
        public SsaForecastResult? Predict(float currentBasePrice)
        {
            lock (_lock)
            {
                if (_engine == null) return null;
            }

            try
            {
                SsaOutput prediction;
                lock (_lock)
                {
                    prediction = _engine!.Predict(new SsaInput { Value = 0f });
                }

                if (prediction.Forecast == null || prediction.Forecast.Length == 0)
                    return null;

                // bps → 실제 가격으로 복원
                var result = new SsaForecastResult
                {
                    Horizon = prediction.Forecast.Length,
                    ForecastPrices = new float[prediction.Forecast.Length],
                    UpperBounds = new float[prediction.Forecast.Length],
                    LowerBounds = new float[prediction.Forecast.Length]
                };

                for (int i = 0; i < prediction.Forecast.Length; i++)
                {
                    float bpsToPrice(float bps) => currentBasePrice * (1f + bps / 10000f);

                    result.ForecastPrices[i] = bpsToPrice(prediction.Forecast[i]);
                    result.UpperBounds[i] = prediction.UpperBound != null && i < prediction.UpperBound.Length
                        ? bpsToPrice(prediction.UpperBound[i]) : result.ForecastPrices[i];
                    result.LowerBounds[i] = prediction.LowerBound != null && i < prediction.LowerBound.Length
                        ? bpsToPrice(prediction.LowerBound[i]) : result.ForecastPrices[i];

                    // NaN/Infinity 방어
                    if (float.IsNaN(result.ForecastPrices[i]) || float.IsInfinity(result.ForecastPrices[i]))
                        result.ForecastPrices[i] = currentBasePrice;
                    if (float.IsNaN(result.UpperBounds[i]) || float.IsInfinity(result.UpperBounds[i]))
                        result.UpperBounds[i] = currentBasePrice;
                    if (float.IsNaN(result.LowerBounds[i]) || float.IsInfinity(result.LowerBounds[i]))
                        result.LowerBounds[i] = currentBasePrice;
                }

                return result;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[SSA] 예측 실패: {ex.Message}");
                return null;
            }
        }
    }

    // ─── SSA 입출력 모델 ──────────────────────────────────

    public class SsaInput
    {
        public float Value { get; set; }
    }

    public class SsaOutput
    {
        public float[] Forecast { get; set; } = Array.Empty<float>();
        public float[] LowerBound { get; set; } = Array.Empty<float>();
        public float[] UpperBound { get; set; } = Array.Empty<float>();
    }

    public class SsaForecastResult
    {
        public int Horizon { get; set; }
        public float[] ForecastPrices { get; set; } = Array.Empty<float>();
        public float[] UpperBounds { get; set; } = Array.Empty<float>();
        public float[] LowerBounds { get; set; } = Array.Empty<float>();

        /// <summary>마지막 예측값의 Upper/Lower 밴드 (차트 표시용)</summary>
        public float LastUpperBound => UpperBounds.Length > 0 ? UpperBounds[^1] : 0f;
        public float LastLowerBound => LowerBounds.Length > 0 ? LowerBounds[^1] : 0f;
        public float LastForecast => ForecastPrices.Length > 0 ? ForecastPrices[^1] : 0f;
    }
}
