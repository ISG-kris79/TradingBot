using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TradingBot.Models;

namespace TradingBot
{
    public class LessThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double val && double.TryParse(parameter?.ToString(), out double limit))
                return val < limit;
            return false;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class StatusToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PositionStatus status)
            {
                return status switch
                {
                    PositionStatus.Monitoring => "🔍",
                    PositionStatus.TakeProfitReady => "💰",
                    PositionStatus.Danger => "⚠️",
                    _ => ""
                };
            }
            return "";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class GreaterThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return false;

            if (double.TryParse(value.ToString(), out double val) &&
                double.TryParse(parameter.ToString(), out double limit))
            {
                return val > limit;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// null 또는 빈 문자열이면 Collapsed, 값이 있으면 Visible 반환
    /// </summary>
    public class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InvertBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return !boolValue;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return !boolValue;
            return true;
        }
    }

    /// <summary>
    /// [v4.9.0] bool == false → Visible, bool == true → Collapsed
    /// AI Insight Panel의 Idle 상태 표시용
    /// </summary>
    public class InvertBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 심볼별 동적 가격 포맷팅 Converter
    /// XRP, DOGE, SHIB, PEPE 등 저가 코인: 4자리
    /// 기타 (BTC, ETH, SOL 등): 2자리
    /// </summary>
    public class PriceFormattingConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return "0";

            // values[0] = Symbol (문자열), values[1] = Price (decimal/double)
            string symbol = values[0]?.ToString()?.ToUpper() ?? "";
            object priceObj = values[1];

            if (priceObj == null) return "0";

            if (!decimal.TryParse(priceObj.ToString(), out decimal price))
            {
                if (!double.TryParse(priceObj.ToString(), out double dPrice))
                    return "0";
                price = (decimal)dPrice;
            }

            // [v3.2.39] 가격 기반 자동 소수점 결정 (PUMP 코인 8자리 대응)
            int decimalPlaces;
            if (price >= 100m)       decimalPlaces = 2;  // BTC 등
            else if (price >= 1m)    decimalPlaces = 4;  // ETH, SOL, XRP 등
            else if (price >= 0.01m) decimalPlaces = 6;  // 중저가
            else                     decimalPlaces = 8;  // 초저가 밈코인

            return price.ToString($"F{decimalPlaces}", culture);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 로그 텍스트의 색상 접두사(🔴, 🟢, 🟡, ⭐, 💎)를 파싱하여 적절한 색상 반환
    /// </summary>
    public class LogTextToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return Brushes.White;

            string text = value.ToString()!;
            
            if (string.IsNullOrWhiteSpace(text))
                return Brushes.White;

            // 상태 색상 접두사
            if (text.Contains("🔴"))
                return new SolidColorBrush(Color.FromRgb(255, 83, 112)); // #FF5370 - 빨간색 (차단/오류)
            
            if (text.Contains("🟢"))
                return new SolidColorBrush(Color.FromRgb(0, 230, 118)); // #00E676 - 초록색 (통과)
            
            if (text.Contains("🟡"))
                return new SolidColorBrush(Color.FromRgb(255, 179, 0)); // #FFB300 - 노란색 (대기)

            // 심볼 강조 색상
            if (text.Contains("⭐"))
                return new SolidColorBrush(Color.FromRgb(255, 215, 0)); // #FFD700 - 금색 (메이저 코인)
            
            if (text.Contains("💎"))
                return new SolidColorBrush(Color.FromRgb(0, 229, 255)); // #00E5FF - 청록색 (알트코인)

            // 특정 키워드 색상
            if (text.Contains("진입실행") || text.Contains("🚀"))
                return new SolidColorBrush(Color.FromRgb(124, 77, 255)); // #7C4DFF - 보라색
            
            if (text.Contains("손절실행") || text.Contains("🛑"))
                return new SolidColorBrush(Color.FromRgb(244, 67, 54)); // #F44336 - 진한 빨강
            
            if (text.Contains("익절실행") || text.Contains("💰"))
                return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // #4CAF50 - 진한 초록

            // 기본 색상
            return new SolidColorBrush(Color.FromRgb(224, 231, 255)); // #E0E7FF - 밝은 청백색
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class ScoreToArcGeometryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!double.TryParse(value?.ToString(), out var score))
                score = 0;

            score = Math.Clamp(score, 0d, 100d);
            if (score <= 0d)
                return Geometry.Empty;

            double centerX = 85d;
            double centerY = 85d;
            double radius = 62d;

            if (parameter is string paramText && !string.IsNullOrWhiteSpace(paramText))
            {
                var parts = paramText.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCenterX) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCenterY) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRadius))
                {
                    centerX = parsedCenterX;
                    centerY = parsedCenterY;
                    radius = Math.Max(1d, parsedRadius);
                }
            }

            var sweepAngle = Math.Min(359.99d, (score / 100d) * 360d);

            Point PointAt(double angleDeg)
            {
                double rad = angleDeg * Math.PI / 180d;
                return new Point(
                    centerX + radius * Math.Cos(rad),
                    centerY + radius * Math.Sin(rad));
            }

            const double startAngle = -90d;
            var startPoint = PointAt(startAngle);
            var endPoint = PointAt(startAngle + sweepAngle);

            var figure = new PathFigure
            {
                StartPoint = startPoint,
                IsClosed = false,
                IsFilled = false
            };

            figure.Segments.Add(new ArcSegment
            {
                Point = endPoint,
                Size = new Size(radius, radius),
                IsLargeArc = sweepAngle >= 180d,
                SweepDirection = SweepDirection.Clockwise
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Bull/Bear 파워 게이지 바 너비 계산: percent(0~100) × containerWidth / 100
    /// </summary>
    public class PercentageWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return 0d;
            if (!double.TryParse(values[0]?.ToString(), out double pct)) pct = 0;
            if (!double.TryParse(values[1]?.ToString(), out double totalWidth)) totalWidth = 0;
            return Math.Max(0, totalWidth * pct / 100.0);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
