using System.Windows;
using TradingBot.Services;

namespace TradingBot
{
    public partial class BacktestOptionsDialog : Window
    {
        public BacktestStrategyType SelectedStrategy { get; private set; }
        public int SelectedDays { get; private set; }
        public bool IsConfirmed { get; private set; }

        public BacktestOptionsDialog()
        {
            InitializeComponent();
            
            // 기본값
            SelectedStrategy = BacktestStrategyType.ElliottWave;
            SelectedDays = 30;
            IsConfirmed = false;
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            // 전략 선택
            if (rbElliottWave.IsChecked == true)
                SelectedStrategy = BacktestStrategyType.ElliottWave;
            else if (rbBollingerBand.IsChecked == true)
                SelectedStrategy = BacktestStrategyType.BollingerBand;
            else if (rbMACross.IsChecked == true)
                SelectedStrategy = BacktestStrategyType.MA_Cross;
            else if (rbRSI.IsChecked == true)
                SelectedStrategy = BacktestStrategyType.RSI;

            // 기간 선택
            if (rb30Days.IsChecked == true)
                SelectedDays = 30;
            else if (rb60Days.IsChecked == true)
                SelectedDays = 60;
            else if (rb90Days.IsChecked == true)
                SelectedDays = 90;
            else if (rb180Days.IsChecked == true)
                SelectedDays = 180;

            IsConfirmed = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
