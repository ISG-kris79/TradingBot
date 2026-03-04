using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace TradingBot
{
    public partial class UpdateDialogWindow : Window
    {
        private bool _allowClose;

        public bool IsProgressMode { get; }

        public UpdateDialogWindow(
            string title,
            string header,
            string message,
            string primaryButtonText = "확인",
            string secondaryButtonText = "취소",
            bool showSecondaryButton = true,
            bool isProgressMode = false)
        {
            InitializeComponent();

            IsProgressMode = isProgressMode;
            _allowClose = !isProgressMode;

            Title = title;
            txtWindowTitle.Text = title;
            txtHeader.Text = header;
            txtMessage.Text = message;
            txtProgressHint.Text = message;

            btnPrimary.Content = primaryButtonText;
            btnSecondary.Content = secondaryButtonText;
            btnSecondary.Visibility = showSecondaryButton ? Visibility.Visible : Visibility.Collapsed;

            progressPanel.Visibility = isProgressMode ? Visibility.Visible : Visibility.Collapsed;
            buttonPanel.Visibility = isProgressMode ? Visibility.Collapsed : Visibility.Visible;
            btnClose.Visibility = isProgressMode ? Visibility.Collapsed : Visibility.Visible;
        }

        public void SetProgressMessage(string message)
        {
            txtMessage.Text = message;
            txtProgressHint.Text = message;
        }

        public void AllowClose()
        {
            _allowClose = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (IsProgressMode && !_allowClose)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Secondary_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
