using System.Windows;
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
        public BacktestWindow(BacktestResult result)
        {
            InitializeComponent();
            DataContext = new BacktestViewModel(result);
        }
    }
}