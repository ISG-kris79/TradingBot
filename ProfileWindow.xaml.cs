using System.Windows;
using System.Windows.Input;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Shared.Services;

namespace TradingBot
{
    public partial class ProfileWindow : Window
    {
        private readonly DatabaseService _dbService;

        public ProfileWindow()
        {
            InitializeComponent();
            _dbService = new DatabaseService();
            LoadUserData();
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

        private async void LoadUserData()
        {
            try
            {
                // DB에서 최신 정보 조회
                var user = await _dbService.GetUserByUsernameAsync(AppConfig.CurrentUsername);
                if (user != null)
                {
                    // 복호화 실패 시 평문으로 간주
                    string telegramToken = SecurityService.DecryptString(user.TelegramBotToken);
                    txtBotToken.Text = !string.IsNullOrEmpty(telegramToken) ? telegramToken : user.TelegramBotToken;

                    string telegramChat = SecurityService.DecryptString(user.TelegramChatId);
                    txtChatId.Text = !string.IsNullOrEmpty(telegramChat) ? telegramChat : user.TelegramChatId;

                    string binanceKey = SecurityService.DecryptString(user.BinanceApiKey);
                    txtBinanceKey.Password = !string.IsNullOrEmpty(binanceKey) ? binanceKey : user.BinanceApiKey;

                    string binanceSecret = SecurityService.DecryptString(user.BinanceApiSecret);
                    txtBinanceSecret.Password = !string.IsNullOrEmpty(binanceSecret) ? binanceSecret : user.BinanceApiSecret;

                    string bybitKey = SecurityService.DecryptString(user.BybitApiKey);
                    txtBybitKey.Password = !string.IsNullOrEmpty(bybitKey) ? bybitKey : user.BybitApiKey;

                    string bybitSecret = SecurityService.DecryptString(user.BybitApiSecret);
                    txtBybitSecret.Password = !string.IsNullOrEmpty(bybitSecret) ? bybitSecret : user.BybitApiSecret;
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"정보 로드 실패: {ex.Message}");
            }
        }

        private async void btnSave_Click(object sender, RoutedEventArgs e)
        {
            // AES256 암호화로 저장 (모든 PC에서 복호화 가능)
            var user = new User
            {
                Username = AppConfig.CurrentUsername,
                BinanceApiKey = SecurityService.EncryptString(txtBinanceKey.Password),
                BinanceApiSecret = SecurityService.EncryptString(txtBinanceSecret.Password),
                BybitApiKey = SecurityService.EncryptString(txtBybitKey.Password),
                BybitApiSecret = SecurityService.EncryptString(txtBybitSecret.Password),
                TelegramBotToken = SecurityService.EncryptString(txtBotToken.Text),
                TelegramChatId = SecurityService.EncryptString(txtChatId.Text)
            };

            bool success = await _dbService.UpdateUserAsync(user);
            if (success)
            {
                MessageBox.Show("정보가 수정되었습니다.\n변경 사항을 적용하려면 앱을 재시작해주세요.");
                // 메모리 상의 설정도 업데이트
                AppConfig.SetUserCredentials(txtBinanceKey.Password, txtBinanceSecret.Password, txtBotToken.Text, txtChatId.Text, AppConfig.CurrentUsername);
                this.Close();
            }
            else
            {
                MessageBox.Show("정보 수정 실패.");
            }
        }
    }
}
