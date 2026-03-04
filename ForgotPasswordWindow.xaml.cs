using System;
using System.Windows;
using System.Windows.Input;
using TradingBot.Services;
using TradingBot.Shared.Services;

namespace TradingBot
{
    public partial class ForgotPasswordWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly EmailService _emailService;

        public ForgotPasswordWindow()
        {
            InitializeComponent();
            _dbService = new DatabaseService();
            _emailService = new EmailService();
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnMaximize_Click(object sender, RoutedEventArgs e)
        {
            // This is a fixed size window, so maximize doesn't make much sense.
            // Keeping it for consistency, but it could be disabled.
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

        private async void btnSendReset_Click(object sender, RoutedEventArgs e)
        {
            string email = txtEmail.Text;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {
                MessageBox.Show("유효한 이메일을 입력해주세요.");
                return;
            }

            btnSendReset.IsEnabled = false;

            try
            {
                // 1. 이메일 존재 여부 확인
                if (!await _dbService.IsEmailExistsAsync(email))
                {
                    MessageBox.Show("등록되지 않은 이메일입니다.");
                    return;
                }

                // 2. 임시 비밀번호 생성 및 DB 업데이트
                string tempPassword = Guid.NewGuid().ToString("N").Substring(0, 8) + "A1!"; // 간단한 임시 비번 생성 규칙
                string hash = SecurityService.HashPassword(tempPassword);
                await _dbService.UpdatePasswordByEmailAsync(email, hash);

                // 3. 이메일 발송
                await _emailService.SendVerificationCodeAsync(email, $"임시 비밀번호: {tempPassword}\n로그인 후 반드시 변경해주세요.");
                
                MessageBox.Show($"임시 비밀번호가 {email}로 발송되었습니다.");
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"오류 발생: {ex.Message}");
            }
            finally
            {
                btnSendReset.IsEnabled = true;
            }
        }
    }
}
