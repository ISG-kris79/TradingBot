using Newtonsoft.Json;
using System.IO;
using System.Windows;
using TradingBot.Services;

namespace TradingBot
{
    public partial class LoginWindow : Window
    {
        private readonly DatabaseService _dbService;

        public LoginWindow()
        {
            InitializeComponent();
            _dbService = new DatabaseService();

            // [추가] 저장된 아이디 불러오기
            if (File.Exists("last_user.txt"))
            {
                txtUsername.Text = File.ReadAllText("last_user.txt");
                chkAutoLogin.IsChecked = true; // 아이디가 저장되어 있으면 체크박스 활성화
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            string username = txtUsername.Text;
            string password = txtPassword.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("아이디와 비밀번호를 입력해주세요.");
                return;
            }

            btnLogin.IsEnabled = false;
            string hash = SecurityService.HashPassword(password);

            try
            {
                var user = await _dbService.LoginUserAsync(username, hash);

                if (user != null)
                {
                    // 자동 로그인 설정 저장
                    // [추가] 아이디 저장 (Remember Me)
                    if (chkAutoLogin.IsChecked == true) File.WriteAllText("last_user.txt", username);
                    else if (File.Exists("last_user.txt")) File.Delete("last_user.txt");

                    if (chkAutoLogin.IsChecked == true)
                    {
                        var config = new { Username = username, Token = hash }; // 실제로는 토큰 사용 권장
                        File.WriteAllText("login.config", JsonConvert.SerializeObject(config));
                    }

                    // 앱 설정에 사용자 키 로드
                    AppConfig.SetUserCredentials(
                        SecurityService.DecryptString(user.BinanceApiKey),
                        SecurityService.DecryptString(user.BinanceApiSecret),
                        SecurityService.DecryptString(user.TelegramBotToken),
                        SecurityService.DecryptString(user.TelegramChatId),
                        user.Username,
                        SecurityService.DecryptString(user.BitgetApiKey),
                        SecurityService.DecryptString(user.BitgetApiSecret),
                        SecurityService.DecryptString(user.BitgetPassphrase)
                    );

                    // 메인 윈도우 실행
                    new MainWindow().Show();
                    this.Close();
                }
                else
                {
                    MessageBox.Show("로그인 실패: 아이디 또는 비밀번호가 틀립니다.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"오류 발생: {ex.Message}");
            }
            finally
            {
                btnLogin.IsEnabled = true;
            }
        }

        private void btnSignUp_Click(object sender, RoutedEventArgs e)
        {
            new SignUpWindow().ShowDialog();
        }

        private void btnForgotPassword_Click(object sender, RoutedEventArgs e)
        {
            new ForgotPasswordWindow().ShowDialog();
        }
    }
}