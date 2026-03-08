namespace TradingBot.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string BinanceApiKey { get; set; } = string.Empty;
        public string BinanceApiSecret { get; set; } = string.Empty;
        public string TelegramBotToken { get; set; } = string.Empty;
        public string TelegramChatId { get; set; } = string.Empty;

        /// <summary>
        /// 관리자 승인 여부 (false: 승인대기, true: 승인완료)
        /// </summary>
        public bool IsApproved { get; set; } = false;

        /// <summary>
        /// 승인한 관리자 사용자명
        /// </summary>
        public string? ApprovedBy { get; set; }

        /// <summary>
        /// 승인 일시
        /// </summary>
        public DateTime? ApprovedAt { get; set; }

        /// <summary>
        /// 관리자 권한 여부
        /// </summary>
        public bool IsAdmin { get; set; } = false;
    }
}
