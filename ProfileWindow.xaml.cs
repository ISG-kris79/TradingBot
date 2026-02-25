using System.Windows;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    public partial class ProfileWindow : Window
    {
        private readonly DatabaseService _dbService;
        private User _currentUser;

        public ProfileWindow()
        {
            InitializeComponent();
            _dbService = new DatabaseService();
            LoadUserData();
        }

        private async void LoadUserData()
        {
            try
            {
                // DB에서 최신 정보 조회
                var user = await _dbService.GetUserByUsernameAsync(AppConfig.CurrentUsername);
                if (user != null)
                {
                    txtBotToken.Text = SecurityService.DecryptString(user.TelegramBotToken);
                    txtChatId.Text = SecurityService.DecryptString(user.TelegramChatId);
                    
                    txtBinanceKey.Password = SecurityService.DecryptString(user.BinanceApiKey);
                    txtBinanceSecret.Password = SecurityService.DecryptString(user.BinanceApiSecret);
                    txtBybitKey.Password = SecurityService.DecryptString(user.BybitApiKey);
                    txtBybitSecret.Password = SecurityService.DecryptString(user.BybitApiSecret);
                    txtBitgetKey.Password = SecurityService.DecryptString(user.BitgetApiKey);
                    txtBitgetSecret.Password = SecurityService.DecryptString(user.BitgetApiSecret);
                    txtBitgetPassphrase.Password = SecurityService.DecryptString(user.BitgetPassphrase);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"정보 로드 실패: {ex.Message}");
            }
        }

        private async void btnSave_Click(object sender, RoutedEventArgs e)
        {
            var user = new User
            {
                Username = AppConfig.CurrentUsername,
                BinanceApiKey = SecurityService.EncryptString(txtBinanceKey.Password),
                BinanceApiSecret = SecurityService.EncryptString(txtBinanceSecret.Password),
                BybitApiKey = SecurityService.EncryptString(txtBybitKey.Password),
                BybitApiSecret = SecurityService.EncryptString(txtBybitSecret.Password),
                BitgetApiKey = SecurityService.EncryptString(txtBitgetKey.Password),
                BitgetApiSecret = SecurityService.EncryptString(txtBitgetSecret.Password),
                BitgetPassphrase = SecurityService.EncryptString(txtBitgetPassphrase.Password),
                TelegramBotToken = SecurityService.EncryptString(txtBotToken.Text),
                TelegramChatId = SecurityService.EncryptString(txtChatId.Text)
            };

            bool success = await _dbService.UpdateUserAsync(user);
            if (success)
            {
                MessageBox.Show("정보가 수정되었습니다.\n변경 사항을 적용하려면 앱을 재시작해주세요.");
                // 메모리 상의 설정도 업데이트
                AppConfig.SetUserCredentials(txtBinanceKey.Password, txtBinanceSecret.Password, txtBotToken.Text, txtChatId.Text, AppConfig.CurrentUsername,
                    txtBitgetKey.Password, txtBitgetSecret.Password, txtBitgetPassphrase.Password);
                this.Close();
            }
            else
            {
                MessageBox.Show("정보 수정 실패.");
            }
        }
    }
}