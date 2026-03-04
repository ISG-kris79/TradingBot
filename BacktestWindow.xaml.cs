using System.Windows;
using System.Windows.Input;
using TradingBot.Services;
using TradingBot.ViewModels;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// BacktestWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class BacktestWindow : Window
    {
        public BacktestWindow()
        {
            InitializeComponent();
            // 기본 생성자: 빈 결과로 초기화
            var emptyResult = new BacktestResult
            {
                Symbol = "N/A",
                InitialBalance = 1000,
                FinalBalance = 1000,
                TotalTrades = 0,
                WinCount = 0,
                LossCount = 0,
                MaxDrawdown = 0,
                SharpeRatio = 0,
                StrategyConfiguration = "Optimize Mode",
                Message = "Backtest & Optimize Window"
            };
            DataContext = new BacktestViewModel(emptyResult, "📊 Backtest & Optimize");
        }

        public BacktestWindow(BacktestResult result, string title = "Backtest Result")
        {
            InitializeComponent();
            DataContext = new BacktestViewModel(result, title);
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
    }
}
