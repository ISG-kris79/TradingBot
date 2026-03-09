using System;
using System.Globalization;
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

            // 저가 코인 목록 (4자리)
            var lowPriceSymbols = new[] { "XRP", "DOGE", "SHIB", "PEPE", "DENT", "SHFT", "ALGO", "AVAX", "ADA" };

            // 심볼에서 "USDT" 제거 (예: "XRPUSDT" → "XRP")
            string baseSymbol = symbol.Replace("USDT", "").Replace("BUSD", "").Replace("USDC", "");

            int decimalPlaces = lowPriceSymbols.Contains(baseSymbol) ? 4 : 2;

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
}
