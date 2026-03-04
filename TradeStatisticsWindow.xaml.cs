using LiveCharts;
using LiveCharts.Wpf;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TradingBot.Models;
using TradingBot.Shared.Models;

namespace TradingBot
{
    public partial class TradeStatisticsWindow : Window, INotifyPropertyChanged
    {
        public SeriesCollection WinRateSeries { get; set; } = new SeriesCollection();
        public SeriesCollection CumulativeProfitSeries { get; set; } = new SeriesCollection();
        public SeriesCollection HourlyProfitSeries { get; set; } = new SeriesCollection();

        public string[] HourlyLabels { get; set; } = Array.Empty<string>();
        public string[] TradeLabels { get; set; } = Array.Empty<string>();
        public Func<double, string> CurrencyFormatter { get; set; } = value => value.ToString("C0");

        private string _summaryText = string.Empty;
        public string SummaryText
        {
            get => _summaryText;
            set { _summaryText = value; OnPropertyChanged(); }
        }

        public TradeStatisticsWindow(IEnumerable<TradeLog> trades)
        {
            InitializeComponent();
            DataContext = this;
            CurrencyFormatter = value => value.ToString("C0");
            AnalyzeData(trades.ToList());
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnMaximize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void AnalyzeData(List<TradeLog> trades)
        {
            if (trades == null || trades.Count == 0)
            {
                SummaryText = "데이터가 없습니다.";
                return;
            }

            // 1. 기본 통계 및 승률 (Win Rate)
            int totalTrades = trades.Count;
            int wins = trades.Count(t => t.PnL > 0);
            int losses = trades.Count(t => t.PnL <= 0);
            decimal totalProfit = trades.Sum(t => t.PnL);
            double winRate = totalTrades > 0 ? (double)wins / totalTrades * 100 : 0;

            SummaryText = $"총 매매: {totalTrades}회 | 승: {wins} / 패: {losses} (승률 {winRate:F1}%) | 순수익: ${totalProfit:N2}";

            WinRateSeries = new SeriesCollection
            {
                new PieSeries
                {
                    Title = "Win",
                    Values = new ChartValues<int> { wins },
                    DataLabels = true,
                    Fill = Brushes.LimeGreen,
                    LabelPoint = chartPoint => string.Format("{0} ({1:P})", chartPoint.Y, chartPoint.Participation)
                },
                new PieSeries
                {
                    Title = "Loss",
                    Values = new ChartValues<int> { losses },
                    DataLabels = true,
                    Fill = Brushes.Tomato,
                    LabelPoint = chartPoint => string.Format("{0} ({1:P})", chartPoint.Y, chartPoint.Participation)
                }
            };

            // 2. 누적 수익 곡선 (Cumulative Profit)
            // 시간순 정렬
            var sortedTrades = trades.OrderBy(t => t.Time).ToList();
            var cumulativeValues = new ChartValues<decimal>();
            decimal currentSum = 0;
            var labels = new List<string>();

            foreach (var t in sortedTrades)
            {
                currentSum += t.PnL;
                cumulativeValues.Add(currentSum);
                labels.Add(t.Time.ToString("MM/dd HH:mm"));
            }

            CumulativeProfitSeries = new SeriesCollection
            {
                new LineSeries
                {
                    Title = "Cumulative PnL",
                    Values = cumulativeValues,
                    PointGeometry = null,
                    LineSmoothness = 0,
                    Stroke = Brushes.DeepSkyBlue,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0, 191, 255))
                }
            };
            TradeLabels = labels.ToArray();

            // 3. 시간대별 수익 (Hourly Profit)
            var hourlyProfit = new decimal[24];
            foreach (var t in trades)
            {
                hourlyProfit[t.Time.Hour] += t.PnL;
            }

            var hourlyValues = new ChartValues<decimal>(hourlyProfit);

            HourlyProfitSeries = new SeriesCollection
            {
                new ColumnSeries
                {
                    Title = "Hourly PnL",
                    Values = hourlyValues,
                    Fill = Brushes.Orange,
                    DataLabels = false
                }
            };

            HourlyLabels = Enumerable.Range(0, 24).Select(h => $"{h}시").ToArray();

            // UI 업데이트 알림
            OnPropertyChanged(nameof(WinRateSeries));
            OnPropertyChanged(nameof(CumulativeProfitSeries));
            OnPropertyChanged(nameof(HourlyProfitSeries));
            OnPropertyChanged(nameof(HourlyLabels));
            OnPropertyChanged(nameof(TradeLabels));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
