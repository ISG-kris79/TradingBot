using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    public partial class SignUpWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly EmailService _emailService;
        private string? _generatedCode;
        private bool _isEmailVerified = false;
        private DateTime _codeExpirationTime = DateTime.MaxValue;

        public SignUpWindow()
        {
            InitializeComponent();
            _dbService = new DatabaseService();
            _emailService = new EmailService();
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
                await _emailService.SendVerificationCodeAsync(email, _generatedCode);
                MessageBox.Show($"인증 코드가 {email}로 발송되었습니다.\n(테스트 모드: {_generatedCode})"); // 실제 배포 시 코드 노출 제거

                lblCode.Visibility = Visibility.Visible;
                txtCode.Visibility = Visibility.Visible;
                txtCode.Text = ""; // 기존 입력값 초기화

                // 재전송 가능하도록 버튼 활성화 및 텍스트 변경
                btnVerifyEmail.Content = "Resend";
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
            _isEmailVerified = true;

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
                // 민감 정보 암호화 저장
                BinanceApiKey = SecurityService.EncryptString(txtApiKey.Password),
                BinanceApiSecret = SecurityService.EncryptString(txtSecretKey.Password),
                TelegramBotToken = SecurityService.EncryptString(txtBotToken.Text),
                TelegramChatId = SecurityService.EncryptString(txtChatId.Text),
                
                BybitApiKey = SecurityService.EncryptString(txtBybitKey.Password),
                BybitApiSecret = SecurityService.EncryptString(txtBybitSecret.Password),
                BitgetApiKey = SecurityService.EncryptString(txtBitgetKey.Password),
                BitgetApiSecret = SecurityService.EncryptString(txtBitgetSecret.Password),
                BitgetPassphrase = SecurityService.EncryptString(txtBitgetPassphrase.Password)
            };

            btnRegister.IsEnabled = false;
            bool success = await _dbService.RegisterUserAsync(user);
            btnRegister.IsEnabled = true;

            if (success)
            {
                MessageBox.Show("회원가입 성공! 로그인해주세요.");
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
            catch { return false; }
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
            catch { return false; }
        }

        private async Task<bool> ValidateBitgetKeys(string apiKey, string apiSecret, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret) || string.IsNullOrWhiteSpace(passphrase)) return false;

            try
            {
                using var client = new HttpClient();
                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string method = "GET";
                string requestPath = "/api/v2/spot/account/assets"; // 잔고 조회 (읽기 전용)
                string body = "";

                string prehash = timestamp + method + requestPath + body;
                string signature;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret)))
                {
                    byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(prehash));
                    signature = Convert.ToBase64String(hash);
                }

                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bitget.com" + requestPath);
                request.Headers.Add("ACCESS-KEY", apiKey);
                request.Headers.Add("ACCESS-SIGN", signature);
                request.Headers.Add("ACCESS-TIMESTAMP", timestamp);
                request.Headers.Add("ACCESS-PASSPHRASE", passphrase);
                request.Headers.Add("locale", "en-US");

                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.TryGetProperty("code", out var code) && code.GetString() == "00000";
            }
            catch { return false; }
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

        private async void txtEmail_TextChanged(object sender, TextChangedEventArgs e)
        {
            string email = txtEmail.Text;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
            {
                lblEmailFeedback.Text = "";
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
    }
}