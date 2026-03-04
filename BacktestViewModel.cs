using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TradingBot.Services;
using TradingBot.Models;

namespace TradingBot.ViewModels
{
    public class BacktestViewModel : INotifyPropertyChanged
    {
        public BacktestResult Result { get; }
        public SeriesCollection ChartSeries { get; set; } = new SeriesCollection();
        public string[] Labels { get; set; } = Array.Empty<string>();
        public Func<double, string> Formatter { get; set; }
        public Brush ProfitColor => (Result?.TotalProfit ?? 0) >= 0 ? Brushes.LimeGreen : Brushes.Tomato;
        public string WindowTitle { get; set; } = string.Empty;

        public BacktestViewModel(BacktestResult result, string title = "Backtest Result")
        {
            Result = result;
            WindowTitle = title;
            Formatter = value => value.ToString("N4");

            if (result.Candles == null || !result.Candles.Any()) return;

            // 1. 캔들 차트 데이터 생성
            var candleValues = new ChartValues<OhlcPoint>();
            foreach (var c in result.Candles)
            {
                candleValues.Add(new OhlcPoint((double)c.Open, (double)c.High, (double)c.Low, (double)c.Close));
            }

            // 2. 매매 시점 마커 데이터 생성
            var buyPoints = new ChartValues<ScatterPoint>();
            var sellPoints = new ChartValues<ScatterPoint>();

            Labels = result.Candles.Select(c => c.OpenTime.ToString("MM/dd HH:mm")).ToArray();

            if (result.TradeHistory == null) return;

            foreach (var trade in result.TradeHistory)
            {
                // 거래가 발생한 캔들의 인덱스를 찾습니다.
                var candleIndex = result.Candles.FindIndex(c => c.OpenTime <= trade.Time && c.OpenTime.AddMinutes(15) > trade.Time); // 15분봉 가정
                if (candleIndex != -1)
                {
                    var point = new ScatterPoint(candleIndex, (double)trade.Price);
                    if (string.Equals(trade.Side, "BUY", StringComparison.OrdinalIgnoreCase))
                    {
                        buyPoints.Add(point);
                    }
                    else
                    {
                        sellPoints.Add(point);
                    }
                }
            }

            // 3. 차트 시리즈 컬렉션 조립
            ChartSeries = new SeriesCollection
            {
                new OhlcSeries
                {
                    Title = result.Symbol,
                    Values = candleValues
                },
                new ScatterSeries
                {
                    Title = "Buy",
                    Values = buyPoints,
                    PointGeometry = DefaultGeometries.Triangle,
                    Fill = Brushes.LimeGreen,
                    MinPointShapeDiameter = 15,
                    DataLabels = false
                },
                new ScatterSeries
                {
                    Title = "Sell",
                    Values = sellPoints,
                    PointGeometry = DefaultGeometries.Triangle,
                    Fill = Brushes.Red,
                    MinPointShapeDiameter = 15,
                    DataLabels = false
                }
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
