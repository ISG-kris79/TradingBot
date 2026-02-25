using System.Net;
using System.Net.Mail;

namespace TradingBot.Services
{
    public class EmailService
    {
        // [주의] 실제 운영 시에는 AppConfig 등에서 설정을 불러와야 합니다.
        private const string SmtpHost = "smtp.gmail.com";
        private const int SmtpPort = 587;
        private const string SmtpUser = "cyberoto79@gmail.com"; // 발신자 이메일
        private const string SmtpPass = "zkcuipbajgtmgcel";    // 앱 비밀번호

        public async Task SendVerificationCodeAsync(string toEmail, string code)
        {
            // SMTP 설정이 없는 경우 테스트를 위해 콘솔 출력만 수행 (개발 모드)
            if (SmtpUser == "your_email@gmail.com")
            {
                System.Diagnostics.Debug.WriteLine($"[Email Mock] To: {toEmail}, Code: {code}");
                return;
            }

            using var client = new SmtpClient(SmtpHost, SmtpPort)
            {
                Credentials = new NetworkCredential(SmtpUser, SmtpPass),
                EnableSsl = true
            };

            var mailMessage = new MailMessage(SmtpUser, toEmail, "TradingBot Verification Code", $"Your verification code is: {code}");
            await client.SendMailAsync(mailMessage);
        }
    }
}