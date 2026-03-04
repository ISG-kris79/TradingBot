using System.IO;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using TradingBot.Services;
using TradingBot.Shared.Services;

namespace TradingBot
{
    public partial class LoginWindow : Window
    {
        private readonly DatabaseService _dbService;

        public LoginWindow()
        {
            InitializeComponent();
            _dbService = new DatabaseService();

            // 버전 정보 동적 표시 (어셈블리 버전 자동)
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            txtVersion.Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}";

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
            pnlLoading.Visibility = Visibility.Visible;
            pgLoginProgress.Value = 0;
            txtProgressPercent.Text = "0%";
            txtLoadingMessage.Text = "로그인 중...";
            File.AppendAllText("login_debug.log", $"[{DateTime.Now}] UI set to loading state.\n");
            await System.Threading.Tasks.Task.Delay(300);
            pgLoginProgress.Value = 30;
            txtProgressPercent.Text = "30%";

            string hash = SecurityService.HashPassword(password);
            await System.Threading.Tasks.Task.Delay(200);
            pgLoginProgress.Value = 60;
            txtProgressPercent.Text = "60%";

            try
            {
                var user = await _dbService.LoginUserAsync(username, hash);
                await System.Threading.Tasks.Task.Delay(200);
                pgLoginProgress.Value = 90;
                txtProgressPercent.Text = "90%";

                if (user == null)
                {
                    await System.Threading.Tasks.Task.Delay(200);
                    pgLoginProgress.Value = 100;
                    txtProgressPercent.Text = "100%";
                    pnlLoading.Visibility = Visibility.Collapsed;
                    btnLogin.IsEnabled = true;
                    MessageBox.Show("아이디 또는 비밀번호가 일치하지 않거나\n관리자 승인 대기 중인 계정입니다.", "로그인 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                    File.AppendAllText("login_debug.log", $"[{DateTime.Now}] Authentication failed for user '{username}'.\n");
                    return;
                }

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
                // [수정] DecryptString 단독 사용 시 복호화 실패 시 빈 문자열이 되어 토큰 누락될 수 있음
                // AppConfig.SetUserCredentials(User) 경로는 DecryptOrUseRaw를 사용해 평문/암호문 모두 안전 처리
                if (!AppConfig.SetUserCredentials(user))
                {
                    throw new InvalidOperationException("사용자 자격 증명 로드에 실패했습니다.");
                }

                pgLoginProgress.Value = 100;
                txtProgressPercent.Text = "100%";
                await System.Threading.Tasks.Task.Delay(200);
                // 메인 윈도우 실행
                var mainWindow = new MainWindow();
                Application.Current.MainWindow = mainWindow;
                mainWindow.Show();
                this.Close();
            }
            catch (Exception ex)
            {
                await System.Threading.Tasks.Task.Delay(200);
                pgLoginProgress.Value = 100;
                txtProgressPercent.Text = "100%";
                pnlLoading.Visibility = Visibility.Collapsed;
                MessageBox.Show($"오류 발생: {ex.Message}");
                File.AppendAllText("login_debug.log", $"[{DateTime.Now}] Exception caught: {ex.ToString()}\n");
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

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnClose_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
