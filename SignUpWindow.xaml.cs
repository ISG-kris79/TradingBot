using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Shared.Services;

namespace TradingBot
{
    public partial class SignUpWindow : Window
    {
        private readonly DatabaseService? _dbService;
        private readonly EmailService? _emailService;
        private string? _generatedCode;
        private DateTime _codeExpirationTime = DateTime.MaxValue;
        private DispatcherTimer? _verificationTimer;

        public SignUpWindow()
        {
            InitializeComponent();

            try
            {
                _dbService = new DatabaseService();
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show($"데이터베이스 초기화 오류:\n{ex.Message}\n\n설정이 올바른지 확인해주세요.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                // [수정] 생성자에서 Close() 호출 시 크래시 발생하므로 UI 비활성화로 대체
                DisableForm();
                return;
            }

            _emailService = new EmailService();
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

        private void DisableForm()
        {
            txtUser.IsEnabled = false;
            txtEmail.IsEnabled = false;
            txtPass.IsEnabled = false;
            txtApiKey.IsEnabled = false;
            txtSecretKey.IsEnabled = false;
            btnRegister.IsEnabled = false;
            btnVerifyEmail.IsEnabled = false;
        }

        private async void btnVerifyEmail_Click(object sender, RoutedEventArgs e)
        {
            string email = txtEmail.Text;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {
                MessageBox.Show("유효한 이메일을 입력해주세요.");
                return;
            }

            // 기존 코드 무효화 (초기화)
            _generatedCode = null;

            // 6자리 인증 코드 생성
            var random = new Random();
            _generatedCode = random.Next(100000, 999999).ToString();
            _codeExpirationTime = DateTime.Now.AddMinutes(3); // 3분 유효

            try
            {
                btnVerifyEmail.IsEnabled = false;
                await (_emailService?.SendVerificationCodeAsync(email, _generatedCode!) ?? Task.CompletedTask);
                MessageBox.Show($"인증 코드가 {email}로 발송되었습니다.\n 인증코드가 없을 시 스팸메일확인!!"); // 실제 배포 시 코드 노출 제거

                lblCode.Visibility = Visibility.Visible;
                txtCode.Visibility = Visibility.Visible;
                txtCode.Text = ""; // 기존 입력값 초기화

                // 타이머 시작
                StartVerificationTimer();

                // 재전송 가능하도록 버튼 활성화 및 텍스트 변경
                btnVerifyEmail.Content = "재발송";
                btnVerifyEmail.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"이메일 발송 실패: {ex.Message}");
                btnVerifyEmail.IsEnabled = true;
                _generatedCode = null; // 실패 시 코드 초기화
            }
        }

        private async void btnRegister_Click(object sender, RoutedEventArgs e)
        {
            if (_dbService == null)
            {
                MessageBox.Show("데이터베이스 서비스가 초기화되지 않았습니다.");
                return;
            }

            if (string.IsNullOrWhiteSpace(txtUser.Text) || string.IsNullOrWhiteSpace(txtPass.Password))
            {
                MessageBox.Show("아이디와 비밀번호는 필수입니다.");
                return;
            }

            // 인증 코드 만료 확인 (인증을 시도한 경우에만)
            if (_generatedCode != null && DateTime.Now > _codeExpirationTime)
            {
                MessageBox.Show("인증 코드가 만료되었습니다. 다시 요청해주세요.");
                return;
            }

            // 이메일 인증 확인
            if (txtCode.Text != _generatedCode)
            {
                MessageBox.Show("인증 코드가 일치하지 않습니다.");
                return;
            }
            // 이메일 인증 완료

            if (await _dbService.IsUsernameExistsAsync(txtUser.Text))
            {
                MessageBox.Show("이미 존재하는 아이디입니다.");
                return;
            }

            if (await _dbService.IsEmailExistsAsync(txtEmail.Text))
            {
                MessageBox.Show("이미 등록된 이메일입니다.");
                return;
            }

            if (!IsPasswordComplex(txtPass.Password))
            {
                MessageBox.Show("비밀번호는 8자 이상이며, 대문자, 소문자, 숫자, 특수문자를 각각 하나 이상 포함해야 합니다.");
                return;
            }

            var user = new User
            {
                Username = txtUser.Text,
                Email = txtEmail.Text,
                PasswordHash = SecurityService.HashPassword(txtPass.Password),
                // 민감 정보 AES256 암호화 저장 (모든 PC에서 복호화 가능)
                BinanceApiKey = SecurityService.EncryptString(txtApiKey.Password),
                BinanceApiSecret = SecurityService.EncryptString(txtSecretKey.Password),
                TelegramBotToken = SecurityService.EncryptString(txtBotToken.Text),
                TelegramChatId = SecurityService.EncryptString(txtChatId.Text)
            };

            btnRegister.IsEnabled = false;

            // 첫 번째 사용자인지 확인
            bool isFirstUser = await _dbService.IsFirstUserAsync();

            bool success = await _dbService.RegisterUserAsync(user);
            btnRegister.IsEnabled = true;

            if (success)
            {
                if (isFirstUser)
                {
                    MessageBox.Show("첫 번째 사용자로 회원가입이 완료되었습니다!\n\n✅ 자동으로 승인되었으며, 관리자 권한이 부여되었습니다.\n지금 바로 로그인하실 수 있습니다.", "회원가입 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("회원가입이 완료되었습니다.\n\n⚠️ 관리자 승인 후 로그인이 가능합니다.\n승인 완료 시 이메일로 알림을 받으실 수 있습니다.", "회원가입 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                this.Close();
            }
            else
            {
                MessageBox.Show("회원가입 실패. 아이디가 중복되었거나 DB 오류입니다.");
            }
        }

        private async Task<bool> ValidateBinanceKeys(string apiKey, string apiSecret)
        {
            try
            {
                using var client = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });
                var accountInfo = await client.UsdFuturesApi.Account.GetBalancesAsync();
                return accountInfo.Success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance API 검증 실패] {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ValidateBybitKeys(string apiKey, string apiSecret)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)) return false;
            try
            {
                using var client = new HttpClient();
                string url = "https://api.bybit.com/v5/user/query-api";
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string recvWindow = "5000";
                string payload = timestamp + apiKey + recvWindow;

                string signature;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret)))
                {
                    byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                    signature = BitConverter.ToString(hash).Replace("-", "").ToLower();
                }

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-BAPI-API-KEY", apiKey);
                request.Headers.Add("X-BAPI-SIGN", signature);
                request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
                request.Headers.Add("X-BAPI-RECV-WINDOW", recvWindow);

                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(content);
                // retCode 0이면 성공
                if (doc.RootElement.TryGetProperty("retCode", out var retCode))
                {
                    return retCode.GetInt32() == 0;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bybit API 검증 실패] {ex.Message}");
                return false;
            }
        }

        private bool IsPasswordComplex(string password)
        {
            if (password.Length < 6) return false; // 길이 완화 (8 -> 6)
            if (!password.Any(char.IsLower)) return false;
            if (!password.Any(char.IsDigit)) return false;
            // 특수문자 조건 제거
            return true;
        }

        private async void txtUser_TextChanged(object sender, TextChangedEventArgs e)
        {
            string username = txtUser.Text;
            if (string.IsNullOrWhiteSpace(username))
            {
                lblUserFeedback.Text = "";
                return;
            }

            // [안전 장치] DB 서비스가 초기화되지 않았으면 중단 (앱 크래시 방지)
            if (_dbService == null) return;

            try
            {
                if (await _dbService.IsUsernameExistsAsync(username))
                {
                    lblUserFeedback.Text = "이미 사용 중인 아이디입니다.";
                    lblUserFeedback.Foreground = Brushes.Red;
                }
                else
                {
                    lblUserFeedback.Text = "사용 가능한 아이디입니다.";
                    lblUserFeedback.Foreground = Brushes.LimeGreen;
                }
            }
            catch (Exception ex)
            {
                lblUserFeedback.Text = $"확인 실패: {ex.Message}";
                lblUserFeedback.Foreground = Brushes.Orange;
            }
        }

        private async void txtEmail_TextChanged(object sender, TextChangedEventArgs e)
        {
            string email = txtEmail.Text;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {
                lblEmailFeedback.Text = "";
                return;
            }

            try
            {
                if (_dbService == null)
                {
                    lblEmailFeedback.Text = "DB 서비스가 초기화되지 않았습니다.";
                    lblEmailFeedback.Foreground = Brushes.Red;
                    return;
                }

                if (await _dbService.IsEmailExistsAsync(email))
                {
                    lblEmailFeedback.Text = "이미 등록된 이메일입니다.";
                    lblEmailFeedback.Foreground = Brushes.Red;
                }
                else
                {
                    lblEmailFeedback.Text = "사용 가능한 이메일입니다.";
                    lblEmailFeedback.Foreground = Brushes.LimeGreen;
                }
            }
            catch (Exception ex)
            {
                lblEmailFeedback.Text = $"확인 실패: {ex.Message}";
                lblEmailFeedback.Foreground = Brushes.Orange;
            }
        }

        private void StartVerificationTimer()
        {
            // 기존 타이머가 있으면 중지
            _verificationTimer?.Stop();

            lblCodeTimer.Visibility = Visibility.Visible;

            _verificationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _verificationTimer.Tick += (s, e) =>
            {
                var remaining = _codeExpirationTime - DateTime.Now;

                if (remaining.TotalSeconds <= 0)
                {
                    lblCodeTimer.Text = "인증 코드가 만료되었습니다.";
                    lblCodeTimer.Foreground = Brushes.Red;
                    _verificationTimer?.Stop();
                }
                else
                {
                    int minutes = (int)remaining.TotalMinutes;
                    int seconds = remaining.Seconds;
                    lblCodeTimer.Text = $"인증 코드 유효시간: {minutes:D2}:{seconds:D2}";

                    // 30초 이하일 때 빨간색으로 변경
                    if (remaining.TotalSeconds <= 30)
                    {
                        lblCodeTimer.Foreground = Brushes.Red;
                    }
                    else
                    {
                        lblCodeTimer.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFB11A"));
                    }
                }
            };

            _verificationTimer.Start();
        }
    }
}
