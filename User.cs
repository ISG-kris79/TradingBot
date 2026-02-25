namespace TradingBot.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public string BinanceApiKey { get; set; }
        public string BinanceApiSecret { get; set; }
        public string TelegramBotToken { get; set; }
        public string TelegramChatId { get; set; }
        public string BybitApiKey { get; set; }
        public string BybitApiSecret { get; set; }
        public string BitgetApiKey { get; set; }
        public string BitgetApiSecret { get; set; }
        public string BitgetPassphrase { get; set; }
    }
}