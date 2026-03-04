using System;
using System.Globalization;
using System.Windows.Data;
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
}
