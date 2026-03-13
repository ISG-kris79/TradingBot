using System.Globalization;
using System.Windows;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    public partial class BacktestOptionsDialog : Window
    {
        public BacktestStrategyType SelectedStrategy { get; private set; }
        public int SelectedDays { get; private set; }
        public BacktestMetricOptions SelectedMetricOptions { get; private set; }
        public bool IsConfirmed { get; private set; }

        public BacktestOptionsDialog()
        {
            InitializeComponent();
            
            // 기본값
            SelectedStrategy = BacktestStrategyType.LiveEntryParity;
            SelectedDays = 30;
            SelectedMetricOptions = new BacktestMetricOptions
            {
                RiskFreeRateAnnualPct = 3.0,
                AnnualizationMode = BacktestAnnualizationMode.Auto
            };
            IsConfirmed = false;
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            // 전략 선택
            if (rbLiveEntryParity.IsChecked == true)
                SelectedStrategy = BacktestStrategyType.LiveEntryParity;
            else if (rbElliottWave.IsChecked == true)
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

            var riskFreeText = txtRiskFreeRate.Text?.Trim();
            if (!double.TryParse(riskFreeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var riskFreeRate))
            {
                if (!double.TryParse(riskFreeText, NumberStyles.Float, CultureInfo.CurrentCulture, out riskFreeRate))
                {
                    MessageBox.Show("무위험수익률은 숫자로 입력해 주세요. 예: 3.0", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (riskFreeRate < -100 || riskFreeRate > 100)
            {
                MessageBox.Show("무위험수익률은 -100 ~ 100 범위로 입력해 주세요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var annualizationMode = BacktestAnnualizationMode.Auto;
            if (cmbAnnualization.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                var tag = selectedItem.Tag?.ToString();
                annualizationMode = tag switch
                {
                    "None" => BacktestAnnualizationMode.None,
                    "TradingDays252" => BacktestAnnualizationMode.TradingDays252,
                    "CalendarDays365" => BacktestAnnualizationMode.CalendarDays365,
                    "Crypto5m" => BacktestAnnualizationMode.Crypto5m,
                    _ => BacktestAnnualizationMode.Auto
                };
            }

            SelectedMetricOptions = new BacktestMetricOptions
            {
                RiskFreeRateAnnualPct = riskFreeRate,
                AnnualizationMode = annualizationMode
            };

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
