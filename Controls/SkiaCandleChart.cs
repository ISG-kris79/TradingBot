using SkiaSharp;
using SkiaSharp.Views.WPF;
using SkiaSharp.Views.Desktop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TradingBot.Controls
{
    /// <summary>
    /// [WPF최적화 3] SkiaSharp 기반 고성능 캔들 차트 렌더러
    ///
    /// LiveCharts 대비 장점:
    ///   - Shape 객체 생성 없음 → GC 부하 제거
    ///   - WriteableBitmap 직접 렌더 → UI 스레드 부하 최소화
    ///   - 수백 개 캔들도 1ms 이내 렌더링
    ///   - DispatcherTimer로 프레임 제한 (초당 2~5회)
    ///
    /// 사용법 (XAML):
    ///   xmlns:ctrl="clr-namespace:TradingBot.Controls"
    ///   &lt;ctrl:SkiaCandleChart x:Name="CandleChart" /&gt;
    ///
    /// 코드비하인드:
    ///   CandleChart.UpdateCandles(candleList);
    /// </summary>
    public class SkiaCandleChart : SKElement
    {
        // ─── 테마 색상 ──────────────────────────────────────
        private static readonly SKColor BgColor = new(0x0F, 0x11, 0x1A);      // #0F111A
        private static readonly SKColor GridColor = new(0x2D, 0x31, 0x42);     // #2D3142
        private static readonly SKColor TextColor = new(0x94, 0xA3, 0xB8);     // #94A3B8
        private static readonly SKColor BullColor = new(0x22, 0xC5, 0x5E);     // 양봉 (초록)
        private static readonly SKColor BearColor = new(0xEF, 0x44, 0x44);     // 음봉 (빨강)
        private static readonly SKColor VolumeColor = new(0x38, 0x4B, 0x70);   // 거래량
        private static readonly SKColor EntryMarkerColor = new(0xFF, 0xD7, 0x00); // 진입 마커 (금)
        private static readonly SKColor SLColor = new(0xEF, 0x44, 0x44, 0x80); // 손절선
        private static readonly SKColor TPColor = new(0x22, 0xC5, 0x5E, 0x80); // 익절선
        private static readonly SKColor ForecastColor = new(0x60, 0xA5, 0xFA); // 예측선

        // ─── AI 오버레이 색상 ──────────────────────────────
        private static readonly SKColor AIStopColor = new(0xFF, 0x53, 0x70);         // AI 손절선 (빨강)
        private static readonly SKColor AIStopHistoryColor = new(0xFF, 0x53, 0x70, 0x40); // 손절 히스토리 (반투명)
        private static readonly SKColor PredBandColor = new(0x60, 0xA5, 0xFA, 0x20); // 예측 밴드 (연파랑)
        private static readonly SKColor PredBandBorderColor = new(0x60, 0xA5, 0xFA, 0x60);
        private static readonly SKColor CurrentPriceColor = new(0xFF, 0xFF, 0xFF);   // 현재가 마커
        private static readonly SKColor StopExitAlertColor = new(0xFF, 0x00, 0x00);  // STOP-EXIT 경고

        // ─── 데이터 ─────────────────────────────────────────
        private List<CandleRenderData> _candles = new();
        private readonly object _dataLock = new();
        private double _entryPrice;
        private double _stopLossPrice;
        private double _targetPrice;
        private List<double> _forecastPrices = new();
        private bool _needsRedraw = true;

        // ─── AI 동적 트레일링 레이어 데이터 ──────────────────
        private double _aiDynamicStopPrice;       // 현재 AI 손절가
        private double _currentLivePrice;         // 현재 실시간 가격
        private double _predictionUpperBound;     // ML 예측 상한
        private double _predictionLowerBound;     // ML 예측 하한
        private double _exitScore;                // Exit Score (0~100)
        private List<TrailingStopPoint> _trailingHistory = new(); // 손절가 변동 히스토리
        private readonly object _trailingLock = new();
        private bool _stopExitAlert;              // STOP-EXIT 경고 상태
        private int _alertFlashFrame;             // 경고 깜빡임 프레임 카운터

        // ─── 렌더링 설정 ────────────────────────────────────
        private const float CandleMarginRatio = 0.15f;   // 캔들 간 여백 비율
        private const float VolumeAreaRatio = 0.2f;       // 하단 거래량 영역 비율
        private const float PaddingLeft = 8f;
        private const float PaddingRight = 60f;           // Y축 레이블 공간
        private const float PaddingTop = 10f;
        private const float PaddingBottom = 24f;          // X축 레이블 공간

        // ─── 프레임 제한 타이머 ─────────────────────────────
        private DispatcherTimer? _renderTimer;
        private int _targetFps = 4;  // 초당 4프레임 (250ms)

        // ─── Paint 객체 캐싱 (GC 부하 제거) ──────────────
        // 매 프레임 new SKPaint 대신 필드로 재사용
        private readonly SKPaint _bullPaint = new() { Color = BullColor, IsAntialias = true };
        private readonly SKPaint _bearPaint = new() { Color = BearColor, IsAntialias = true };
        private readonly SKPaint _wickPaint = new() { Color = SKColors.White, StrokeWidth = 1f, IsAntialias = true };
        private readonly SKPaint _volPaint = new() { Color = VolumeColor, IsAntialias = true };
        private readonly SKPaint _gridPaint = new() { Color = GridColor, StrokeWidth = 0.5f, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) };
        private readonly SKPaint _entryLinePaint = new() { Color = EntryMarkerColor, StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 3f }, 0) };
        private readonly SKPaint _slLinePaint = new() { Color = SLColor, StrokeWidth = 1f, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) };
        private readonly SKPaint _tpLinePaint = new() { Color = TPColor, StrokeWidth = 1f, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) };
        private readonly SKPaint _aiStopPaint = new() { Color = AIStopColor, StrokeWidth = 2.5f, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 10f, 5f }, 0) };
        private readonly SKPaint _aiStopHistPaint = new() { Color = AIStopHistoryColor, StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        private readonly SKPaint _predBandPaint = new() { Color = PredBandColor, Style = SKPaintStyle.Fill };
        private readonly SKPaint _predBorderPaint = new() { Color = PredBandBorderColor, StrokeWidth = 1f, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 3f }, 0) };
        private readonly SKPaint _currentPriceMarkerPaint = new() { Color = CurrentPriceColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _stopAlertOverlayPaint = new() { Color = new SKColor(0xFF, 0x00, 0x00, 0x18), Style = SKPaintStyle.Fill };
        private readonly SKPaint _textPaint = new() { Color = TextColor, IsAntialias = true };
        private readonly SKPaint _aiStopLabelBgPaint = new() { Color = new SKColor(0xFF, 0x53, 0x70, 0xCC), Style = SKPaintStyle.Fill };
        private readonly SKPaint _whitePaint = new() { Color = SKColors.White, IsAntialias = true };
        private readonly SKPaint _alertTextPaint = new() { Color = StopExitAlertColor, IsAntialias = true };
        private readonly SKFont _defaultFont = new() { Size = 10f };
        private readonly SKFont _smallFont = new() { Size = 9f };
        private readonly SKFont _alertFont = new() { Size = 24f, Embolden = true };

        // ─── 기술적 지표 오버레이 Paint ──────────────────
        private static readonly SKColor SMA20Color = new(0xFF, 0xD7, 0x00, 0xCC);  // 금색
        private static readonly SKColor SMA50Color = new(0x60, 0xA5, 0xFA, 0xCC);  // 파랑
        private static readonly SKColor BBFillColor = new(0x7C, 0x4D, 0xFF, 0x15); // 보라 투명
        private static readonly SKColor BBBorderColor = new(0x7C, 0x4D, 0xFF, 0x40);
        private readonly SKPaint _sma20Paint = new() { Color = SMA20Color, StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        private readonly SKPaint _sma50Paint = new() { Color = SMA50Color, StrokeWidth = 1.5f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        private readonly SKPaint _bbFillPaint = new() { Color = BBFillColor, Style = SKPaintStyle.Fill };
        private readonly SKPaint _bbBorderPaint = new() { Color = BBBorderColor, StrokeWidth = 0.8f, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };

        // ─── Interlocked 데이터 무결성 ────────────────────
        // AI 스레드와 UI 스레드 간 데이터 공유 시 원자적 접근 보장
        private long _aiStopPriceBits;   // double → long (Interlocked 호환)
        private long _livePriceBits;     // double → long
        private long _exitScoreBits;     // double → long

        public SkiaCandleChart()
        {
            // 렌더 타이머 초기화
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / _targetFps)
            };
            _renderTimer.Tick += (_, _) =>
            {
                if (_needsRedraw)
                {
                    InvalidateVisual();
                    _needsRedraw = false;
                }
            };
            _renderTimer.Start();
        }

        /// <summary>캔들 데이터 업데이트 (Thread-safe)</summary>
        public void UpdateCandles(List<CandleRenderData> candles)
        {
            lock (_dataLock)
            {
                _candles = candles ?? new();
            }
            _needsRedraw = true;
        }

        /// <summary>포지션 라인 설정</summary>
        public void SetPositionLines(double entry, double stopLoss, double target)
        {
            _entryPrice = entry;
            _stopLossPrice = stopLoss;
            _targetPrice = target;
            _needsRedraw = true;
        }

        /// <summary>예측 가격 설정</summary>
        public void SetForecastPrices(List<double> forecast)
        {
            _forecastPrices = forecast ?? new();
            _needsRedraw = true;
        }

        /// <summary>FPS 설정 (1~10)</summary>
        public void SetTargetFps(int fps)
        {
            _targetFps = Math.Clamp(fps, 1, 10);
            if (_renderTimer != null)
                _renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
        }

        /// <summary>
        /// AI 동적 손절가 실시간 업데이트 (Thread-safe, Interlocked)
        /// AI 워커 스레드에서 직접 호출 가능 — Dispatcher 불필요
        /// </summary>
        public void UpdateAIDynamicStop(double stopPrice, double currentPrice, double exitScore)
        {
            // Interlocked로 원자적 데이터 교환 (AI스레드↔렌더스레드 무결성)
            Interlocked.Exchange(ref _aiStopPriceBits, BitConverter.DoubleToInt64Bits(stopPrice));
            Interlocked.Exchange(ref _livePriceBits, BitConverter.DoubleToInt64Bits(currentPrice));
            Interlocked.Exchange(ref _exitScoreBits, BitConverter.DoubleToInt64Bits(exitScore));

            // 필드도 업데이트 (렌더 스레드에서 읽기)
            _aiDynamicStopPrice = stopPrice;
            _currentLivePrice = currentPrice;
            _exitScore = exitScore;

            // STOP-EXIT 경고: 현재가가 손절가의 0.15% 이내 접근
            if (stopPrice > 0 && currentPrice > 0)
            {
                double distance = Math.Abs(currentPrice - stopPrice) / currentPrice;
                _stopExitAlert = distance < 0.0015; // 0.15% 이내
            }

            // 히스토리에 추가 (최대 200포인트)
            if (stopPrice > 0)
            {
                lock (_trailingLock)
                {
                    _trailingHistory.Add(new TrailingStopPoint
                    {
                        Time = DateTime.Now,
                        StopPrice = stopPrice,
                        LivePrice = currentPrice
                    });
                    if (_trailingHistory.Count > 200)
                        _trailingHistory.RemoveAt(0);
                }
            }

            _needsRedraw = true;
        }

        /// <summary>ML.NET 예측 밴드 설정 (Upper/Lower Bound)</summary>
        public void SetPredictionBand(double upperBound, double lowerBound)
        {
            _predictionUpperBound = upperBound;
            _predictionLowerBound = lowerBound;
            _needsRedraw = true;
        }

        /// <summary>트레일링 히스토리 초기화 (포지션 종료 시)</summary>
        public void ClearTrailingHistory()
        {
            lock (_trailingLock)
            {
                _trailingHistory.Clear();
            }
            _aiDynamicStopPrice = 0;
            _stopExitAlert = false;
            _needsRedraw = true;
        }

        protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            var info = e.Info;
            canvas.Clear(BgColor);

            List<CandleRenderData> candles;
            lock (_dataLock)
            {
                candles = _candles.ToList();
            }

            if (candles.Count == 0)
            {
                DrawEmptyState(canvas, info);
                return;
            }

            // 차트 영역 계산
            float chartLeft = PaddingLeft;
            float chartRight = info.Width - PaddingRight;
            float chartTop = PaddingTop;
            float chartBottom = info.Height - PaddingBottom;
            float volumeTop = chartBottom - (chartBottom - chartTop) * VolumeAreaRatio;

            // 가격 범위 계산
            double priceMin = candles.Min(c => c.Low);
            double priceMax = candles.Max(c => c.High);
            double pricePadding = (priceMax - priceMin) * 0.05;
            priceMin -= pricePadding;
            priceMax += pricePadding;

            // 거래량 범위
            double volMax = candles.Max(c => c.Volume);
            if (volMax <= 0) volMax = 1;

            // 캔들 너비 계산
            float totalWidth = chartRight - chartLeft;
            float candleWidth = totalWidth / candles.Count;
            float bodyWidth = candleWidth * (1f - CandleMarginRatio);

            // 그리드 라인 (캐싱된 Paint 사용)
            DrawGrid(canvas, chartLeft, chartRight, chartTop, volumeTop, priceMin, priceMax);

            // 포지션 라인 (진입/손절/익절)
            DrawPositionLines(canvas, chartLeft, chartRight, chartTop, volumeTop, priceMin, priceMax);

            // 캔들 렌더링 (캐싱된 Paint 객체 재사용 — GC 부하 제거)
            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                float x = chartLeft + i * candleWidth + candleWidth / 2;
                bool isBull = c.Close >= c.Open;

                // 꼬리 (Wick)
                float highY = PriceToY(c.High, chartTop, volumeTop, priceMin, priceMax);
                float lowY = PriceToY(c.Low, chartTop, volumeTop, priceMin, priceMax);
                _wickPaint.Color = isBull ? BullColor : BearColor;
                canvas.DrawLine(x, highY, x, lowY, _wickPaint);

                // 몸통 (Body)
                float openY = PriceToY(c.Open, chartTop, volumeTop, priceMin, priceMax);
                float closeY = PriceToY(c.Close, chartTop, volumeTop, priceMin, priceMax);
                float bodyTop = Math.Min(openY, closeY);
                float bodyBottom = Math.Max(openY, closeY);
                float bodyH = Math.Max(bodyBottom - bodyTop, 1f); // 최소 1px

                var paint = isBull ? _bullPaint : _bearPaint;
                canvas.DrawRect(x - bodyWidth / 2, bodyTop, bodyWidth, bodyH, paint);

                // 거래량 바
                float volH = (float)(c.Volume / volMax) * (chartBottom - volumeTop - 4f);
                _volPaint.Color = isBull ? BullColor.WithAlpha(0x60) : BearColor.WithAlpha(0x60);
                canvas.DrawRect(x - bodyWidth / 2, chartBottom - volH, bodyWidth, volH, _volPaint);
            }

            // ═══ 기술적 지표 오버레이 (SMA + BB) ═══
            DrawIndicatorOverlays(canvas, candles, chartLeft, candleWidth, chartTop, volumeTop, priceMin, priceMax);

            // 예측선 (Forecast)
            if (_forecastPrices.Count > 0)
            {
                DrawForecastLine(canvas, candles.Count, candleWidth, chartLeft, chartTop, volumeTop, priceMin, priceMax);
            }

            // ═══ AI 오버레이 레이어 (캔들 위에 그림) ═══

            // ML.NET 예측 밴드 (UpperBound ~ LowerBound 영역)
            DrawPredictionBand(canvas, chartLeft, chartRight, chartTop, volumeTop, priceMin, priceMax);

            // AI 트레일링 스탑 히스토리 라인 (과거 손절가 변동 궤적)
            DrawTrailingStopHistory(canvas, candles, chartLeft, candleWidth, chartTop, volumeTop, priceMin, priceMax);

            // AI 동적 손절선 (실시간 수평선)
            DrawAIDynamicStopLine(canvas, chartLeft, chartRight, chartTop, volumeTop, priceMin, priceMax);

            // 현재가 마커 (우측에 가격 표시)
            DrawCurrentPriceMarker(canvas, chartRight, chartTop, volumeTop, priceMin, priceMax);

            // STOP-EXIT 경고 오버레이
            if (_stopExitAlert)
                DrawStopExitAlert(canvas, info);

            // Y축 레이블
            DrawYAxisLabels(canvas, chartRight, chartTop, volumeTop, priceMin, priceMax);

            // X축 레이블
            DrawXAxisLabels(canvas, candles, chartLeft, candleWidth, chartBottom);
        }

        private float PriceToY(double price, float top, float bottom, double min, double max)
        {
            if (max <= min) return top;
            return top + (float)((max - price) / (max - min)) * (bottom - top);
        }

        private void DrawGrid(SKCanvas canvas, float left, float right, float top, float bottom, double priceMin, double priceMax)
        {
            int gridLines = 5;
            for (int i = 0; i <= gridLines; i++)
            {
                float y = top + (bottom - top) * i / gridLines;
                canvas.DrawLine(left, y, right, y, _gridPaint);
            }
        }

        private void DrawPositionLines(SKCanvas canvas, float left, float right, float top, float bottom, double priceMin, double priceMax)
        {
            if (_entryPrice > 0)
            {
                float y = PriceToY(_entryPrice, top, bottom, priceMin, priceMax);
                canvas.DrawLine(left, y, right, y, _entryLinePaint);
            }
            if (_stopLossPrice > 0)
            {
                float y = PriceToY(_stopLossPrice, top, bottom, priceMin, priceMax);
                canvas.DrawLine(left, y, right, y, _slLinePaint);
            }
            if (_targetPrice > 0)
            {
                float y = PriceToY(_targetPrice, top, bottom, priceMin, priceMax);
                canvas.DrawLine(left, y, right, y, _tpLinePaint);
            }
        }

        private void DrawForecastLine(SKCanvas canvas, int startIndex, float candleWidth, float chartLeft, float chartTop, float volumeTop, double priceMin, double priceMax)
        {
            using var paint = new SKPaint
            {
                Color = ForecastColor,
                StrokeWidth = 2f,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                PathEffect = SKPathEffect.CreateDash(new[] { 8f, 4f }, 0)
            };

            using var path = new SKPath();
            bool started = false;

            for (int i = 0; i < _forecastPrices.Count; i++)
            {
                float x = chartLeft + (startIndex + i) * candleWidth + candleWidth / 2;
                float y = PriceToY(_forecastPrices[i], chartTop, volumeTop, priceMin, priceMax);

                if (!started)
                {
                    path.MoveTo(x, y);
                    started = true;
                }
                else
                {
                    path.LineTo(x, y);
                }
            }

            canvas.DrawPath(path, paint);
        }

        private void DrawYAxisLabels(SKCanvas canvas, float right, float top, float bottom, double priceMin, double priceMax)
        {
            int labels = 5;
            for (int i = 0; i <= labels; i++)
            {
                float y = top + (bottom - top) * i / labels;
                double price = priceMax - (priceMax - priceMin) * i / labels;
                string fmt = price >= 100 ? "N2" : price >= 1 ? "N4" : "N6";
                canvas.DrawText(price.ToString(fmt), right + 4, y + 4, SKTextAlign.Left, _defaultFont, _textPaint);
            }
        }

        private void DrawXAxisLabels(SKCanvas canvas, List<CandleRenderData> candles, float chartLeft, float candleWidth, float bottom)
        {
            int step = Math.Max(1, candles.Count / 6);
            for (int i = 0; i < candles.Count; i += step)
            {
                float x = chartLeft + i * candleWidth + candleWidth / 2;
                canvas.DrawText(candles[i].Time.ToString("HH:mm"), x - 14, bottom + 14, SKTextAlign.Left, _smallFont, _textPaint);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // AI 오버레이 렌더링 메서드
        // ═══════════════════════════════════════════════════════════

        /// <summary>ML.NET 예측 밴드: Upper~Lower 사이를 연한 파랑 영역으로 칠함</summary>
        private void DrawPredictionBand(SKCanvas canvas, float left, float right, float top, float bottom, double priceMin, double priceMax)
        {
            if (_predictionUpperBound <= 0 || _predictionLowerBound <= 0) return;
            if (_predictionUpperBound <= _predictionLowerBound) return;

            float upperY = PriceToY(_predictionUpperBound, top, bottom, priceMin, priceMax);
            float lowerY = PriceToY(_predictionLowerBound, top, bottom, priceMin, priceMax);

            canvas.DrawRect(left, upperY, right - left, lowerY - upperY, _predBandPaint);
            canvas.DrawLine(left, upperY, right, upperY, _predBorderPaint);
            canvas.DrawLine(left, lowerY, right, lowerY, _predBorderPaint);
        }

        /// <summary>AI 동적 손절선 (살아 움직이는 수평선)</summary>
        private void DrawAIDynamicStopLine(SKCanvas canvas, float left, float right, float top, float bottom, double priceMin, double priceMax)
        {
            // Interlocked에서 최신 값 읽기
            double stopPrice = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _aiStopPriceBits));
            if (stopPrice <= 0) return;

            float y = PriceToY(stopPrice, top, bottom, priceMin, priceMax);
            canvas.DrawLine(left, y, right, y, _aiStopPaint);

            // 우측 가격 라벨 배경
            string fmt = stopPrice >= 100 ? "N2" : stopPrice >= 1 ? "N4" : "N6";
            canvas.DrawRect(right - 56, y - 8, 56, 16, _aiStopLabelBgPaint);
            canvas.DrawText($"SL {stopPrice.ToString(fmt)}", right - 54, y + 3, SKTextAlign.Left, _smallFont, _whitePaint);
        }

        /// <summary>AI 트레일링 스탑 히스토리 (손절가 변동 궤적)</summary>
        private void DrawTrailingStopHistory(SKCanvas canvas, List<CandleRenderData> candles, float chartLeft, float candleWidth, float top, float bottom, double priceMin, double priceMax)
        {
            List<TrailingStopPoint> history;
            lock (_trailingLock)
            {
                if (_trailingHistory.Count < 2) return;
                history = _trailingHistory.ToList();
            }
            if (candles.Count == 0) return;

            var chartStart = candles[0].Time;
            var chartEnd = candles[^1].Time;
            double chartSpanMs = (chartEnd - chartStart).TotalMilliseconds;
            if (chartSpanMs <= 0) return;

            float chartWidth = candles.Count * candleWidth;

            using var path = new SKPath();
            bool started = false;
            foreach (var pt in history)
            {
                float ratio = (float)((pt.Time - chartStart).TotalMilliseconds / chartSpanMs);
                if (ratio < 0 || ratio > 1.1f) continue;

                float x = chartLeft + ratio * chartWidth;
                float y = PriceToY(pt.StopPrice, top, bottom, priceMin, priceMax);
                if (!started) { path.MoveTo(x, y); started = true; }
                else path.LineTo(x, y);
            }
            if (started) canvas.DrawPath(path, _aiStopHistPaint);
        }

        /// <summary>현재가 실시간 마커 (우측 삼각형)</summary>
        private void DrawCurrentPriceMarker(SKCanvas canvas, float right, float top, float bottom, double priceMin, double priceMax)
        {
            double livePrice = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _livePriceBits));
            if (livePrice <= 0) return;

            float y = PriceToY(livePrice, top, bottom, priceMin, priceMax);
            using var markerPath = new SKPath();
            markerPath.MoveTo(right + 1, y);
            markerPath.LineTo(right + 8, y - 5);
            markerPath.LineTo(right + 8, y + 5);
            markerPath.Close();
            canvas.DrawPath(markerPath, _currentPriceMarkerPaint);
        }

        /// <summary>STOP-EXIT 경고 오버레이 (화면 전체 빨간 깜빡임)</summary>
        private void DrawStopExitAlert(SKCanvas canvas, SKImageInfo info)
        {
            _alertFlashFrame++;
            if (_alertFlashFrame % 2 == 0) return;

            canvas.DrawRect(0, 0, info.Width, info.Height, _stopAlertOverlayPaint);
            canvas.DrawText("⚠ STOP-EXIT", info.Width / 2f, 36f, SKTextAlign.Center, _alertFont, _alertTextPaint);
        }

        /// <summary>기술적 지표 오버레이: SMA20/50 + Bollinger Bands</summary>
        private void DrawIndicatorOverlays(SKCanvas canvas, List<CandleRenderData> candles, float chartLeft, float candleWidth, float top, float bottom, double priceMin, double priceMax)
        {
            if (candles.Count < 2) return;

            // BB 밴드 영역 (Upper~Lower 사이 채우기)
            bool hasBB = candles.Any(c => c.BollingerUpper > 0);
            if (hasBB)
            {
                using var bbPath = new SKPath();
                bool started = false;
                var lowerPoints = new List<SKPoint>();
                for (int i = 0; i < candles.Count; i++)
                {
                    if (candles[i].BollingerUpper <= 0) continue;
                    float x = chartLeft + i * candleWidth + candleWidth / 2;
                    float upperY = PriceToY(candles[i].BollingerUpper, top, bottom, priceMin, priceMax);
                    float lowerY = PriceToY(candles[i].BollingerLower, top, bottom, priceMin, priceMax);
                    if (!started) { bbPath.MoveTo(x, upperY); started = true; }
                    else bbPath.LineTo(x, upperY);
                    lowerPoints.Add(new SKPoint(x, lowerY));
                }
                for (int i = lowerPoints.Count - 1; i >= 0; i--)
                    bbPath.LineTo(lowerPoints[i].X, lowerPoints[i].Y);
                bbPath.Close();
                canvas.DrawPath(bbPath, _bbFillPaint);

                // BB 상/하 경계선
                DrawLineSeries(canvas, candles, c => c.BollingerUpper, chartLeft, candleWidth, top, bottom, priceMin, priceMax, _bbBorderPaint);
                DrawLineSeries(canvas, candles, c => c.BollingerLower, chartLeft, candleWidth, top, bottom, priceMin, priceMax, _bbBorderPaint);
            }

            // SMA20 (금색)
            if (candles.Any(c => c.SMA20 > 0))
                DrawLineSeries(canvas, candles, c => c.SMA20, chartLeft, candleWidth, top, bottom, priceMin, priceMax, _sma20Paint);

            // SMA50 (파랑)
            if (candles.Any(c => c.SMA50 > 0))
                DrawLineSeries(canvas, candles, c => c.SMA50, chartLeft, candleWidth, top, bottom, priceMin, priceMax, _sma50Paint);
        }

        /// <summary>데이터 시리즈를 연결선으로 그리기</summary>
        private void DrawLineSeries(SKCanvas canvas, List<CandleRenderData> candles, Func<CandleRenderData, double> selector, float chartLeft, float candleWidth, float top, float bottom, double priceMin, double priceMax, SKPaint paint)
        {
            using var path = new SKPath();
            bool started = false;
            for (int i = 0; i < candles.Count; i++)
            {
                double val = selector(candles[i]);
                if (val <= 0) continue;
                float x = chartLeft + i * candleWidth + candleWidth / 2;
                float y = PriceToY(val, top, bottom, priceMin, priceMax);
                if (!started) { path.MoveTo(x, y); started = true; }
                else path.LineTo(x, y);
            }
            if (started) canvas.DrawPath(path, paint);
        }

        private void DrawEmptyState(SKCanvas canvas, SKImageInfo info)
        {
            using var paint = new SKPaint { Color = TextColor, IsAntialias = true };
            using var font = new SKFont { Size = 14f };
            canvas.DrawText("심볼을 선택하면 캔들 차트가 표시됩니다.", info.Width / 2f, info.Height / 2f, SKTextAlign.Center, font, paint);
        }
    }

    /// <summary>캔들 렌더링 데이터</summary>
    public class CandleRenderData
    {
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }

        // ─── 기술적 지표 (차트 오버레이용) ───
        public double SMA20 { get; set; }
        public double SMA50 { get; set; }
        public double BollingerUpper { get; set; }
        public double BollingerMid { get; set; }
        public double BollingerLower { get; set; }
    }

    /// <summary>트레일링 스탑 히스토리 포인트</summary>
    public class TrailingStopPoint
    {
        public DateTime Time { get; set; }
        public double StopPrice { get; set; }
        public double LivePrice { get; set; }
    }
}
