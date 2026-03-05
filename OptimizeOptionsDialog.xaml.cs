using System.Windows;
using TradingBot.Services;

namespace TradingBot
{
    public partial class OptimizeOptionsDialog : Window
    {
        public BacktestStrategyType SelectedStrategy { get; private set; }
        public int SelectedDays { get; private set; }
        public int SelectedTrials { get; private set; }
        public bool IsConfirmed { get; private set; }

        public OptimizeOptionsDialog()
        {
            InitializeComponent();
            
            // 기본값
            SelectedStrategy = BacktestStrategyType.ElliottWave;
            SelectedDays = 30;
            SelectedTrials = 20;
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

            // 시도 횟수 선택
            if (rb10Trials.IsChecked == true)
                SelectedTrials = 10;
            else if (rb20Trials.IsChecked == true)
                SelectedTrials = 20;
            else if (rb50Trials.IsChecked == true)
                SelectedTrials = 50;
            else if (rb100Trials.IsChecked == true)
                SelectedTrials = 100;

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
