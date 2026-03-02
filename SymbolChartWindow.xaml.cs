using System.Windows;
using TradingBot.ViewModels;

namespace TradingBot
{
    public partial class SymbolChartWindow : Window
    {
        public SymbolChartWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
