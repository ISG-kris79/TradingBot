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
}