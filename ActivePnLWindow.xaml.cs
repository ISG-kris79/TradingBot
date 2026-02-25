using System.Windows;
using TradingBot.ViewModels;

namespace TradingBot
{
    public partial class ActivePnLWindow : Window
    {
        public ActivePnLWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            this.DataContext = viewModel;
        }
    }
}