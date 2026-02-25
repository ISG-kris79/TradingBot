using Microsoft.Extensions.Configuration;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    public class AppConfig
    {
        // 전역에서 접근 가능한 싱글톤 인스턴스
        public static AppConfig? Current { get; private set; }

        // 기존 코드와의 호환성을 위한 정적 속성 (다른 클래스에서 참조 중)
        public static string BinanceApiKey => Current?.Binance?.ApiKey ?? string.Empty;
        public static string BinanceApiSecret => Current?.Binance?.ApiSecret ?? string.Empty;
        public static string TelegramBotToken => Current?.Telegram?.BotToken ?? string.Empty;
        public static string TelegramChatId => Current?.Telegram?.ChatId ?? string.Empty;
        public static string ConnectionString => Current?.ConnectionStrings?.DefaultConnection ?? string.Empty;
        public static string CryptoPanicApiKey => Current?.ExternalApi?.CryptoPanicApiKey ?? string.Empty;
        
        public static string BitgetApiKey => Current?.Bitget?.ApiKey ?? string.Empty;
        public static string BitgetApiSecret => Current?.Bitget?.ApiSecret ?? string.Empty;
        public static string BitgetPassphrase => Current?.Bitget?.Passphrase ?? string.Empty;

        public static string BybitApiKey => Current?.Bybit?.ApiKey ?? string.Empty;
        public static string BybitApiSecret => Current?.Bybit?.ApiSecret ?? string.Empty;

        public static string CurrentUsername { get; private set; } = string.Empty;

        public BinanceSettings Binance { get; set; } = new();
        public BitgetSettings Bitget { get; set; } = new();
        public BybitSettings Bybit { get; set; } = new();
        public TelegramSettings Telegram { get; set; } = new();
        public ConnectionStringsSettings ConnectionStrings { get; set; } = new();
        public TradingConfig Trading { get; set; } = new();
        public ExternalApiSettings ExternalApi { get; set; } = new();

        /// <summary>
        /// 설정 로드 메서드. 프로그램 시작 시(App.xaml.cs 등) 호출해야 합니다.
        /// </summary>
        public static void Load()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            try
            {
                // User Secrets 로드 (개발 환경 보안)
                // T에는 프로젝트 내의 아무 클래스나 지정 (여기서는 AppConfig 자신)
                builder.AddUserSecrets<AppConfig>();
            }
            catch (Exception ex)
            {
                // User Secrets가 초기화되지 않았거나 패키지가 없는 경우 무시
                System.Diagnostics.Debug.WriteLine($"[AppConfig] User Secrets Load Failed: {ex.Message}");
            }

            IConfigurationRoot configuration = builder.Build();

            var config = new AppConfig();
            configuration.Bind(config);

            Current = config;

            // [디버깅용] 설정 로드 여부 확인 (Visual Studio 출력 창에서 확인)
            System.Diagnostics.Debug.WriteLine($"[AppConfig] Binance Key Loaded: {!string.IsNullOrEmpty(BinanceApiKey)}");
            System.Diagnostics.Debug.WriteLine($"[AppConfig] Telegram Token Loaded: {!string.IsNullOrEmpty(TelegramBotToken)}");
        }

        public static void SetUserCredentials(string apiKey, string apiSecret, string botToken, string chatId, string username, 
            string bitgetKey = "", string bitgetSecret = "", string bitgetPassphrase = "")
        {
            if (Current != null)
            {
                Current.Binance.ApiKey = apiKey;
                Current.Binance.ApiSecret = apiSecret;
                Current.Telegram.BotToken = botToken;
                Current.Telegram.ChatId = chatId;
                
                Current.Bitget.ApiKey = bitgetKey;
                Current.Bitget.ApiSecret = bitgetSecret;
                Current.Bitget.Passphrase = bitgetPassphrase;
            }
            CurrentUsername = username;
        }

        /// <summary>
        /// 로그인 성공 후 User 객체로부터 자격 증명을 설정합니다.
        /// DB에 암호화된 키를 복호화하여 메모리에 로드합니다.
        /// </summary>
        /// <param name="user">DB에서 조회한 User 객체</param>
        /// <returns>복호화 및 설정 성공 여부. 키 복호화 실패 시 false를 반환합니다.</returns>
        public static bool SetUserCredentials(User user)
        {
            if (Current == null || user == null) return false;

            try
            {
                // DPAPI로 암호화된 키들을 복호화합니다.
                string binanceKey = SecurityService.DecryptString(user.BinanceApiKey);
                string binanceSecret = SecurityService.DecryptString(user.BinanceApiSecret);
                string bitgetKey = SecurityService.DecryptString(user.BitgetApiKey);
                string bitgetSecret = SecurityService.DecryptString(user.BitgetApiSecret);
                string bitgetPassphrase = SecurityService.DecryptString(user.BitgetPassphrase);
                string bybitKey = SecurityService.DecryptString(user.BybitApiKey);
                string bybitSecret = SecurityService.DecryptString(user.BybitApiSecret);
                string telegramToken = SecurityService.DecryptString(user.TelegramBotToken);
                string telegramChatId = SecurityService.DecryptString(user.TelegramChatId);

                // 주요 키가 DB에 있었는데 복호화 후 비어있다면, 다른 환경에서 실행한 것으로 간주하고 실패 처리
                if ((!string.IsNullOrEmpty(user.BinanceApiKey) && string.IsNullOrEmpty(binanceKey)) ||
                    (!string.IsNullOrEmpty(user.BitgetApiKey) && string.IsNullOrEmpty(bitgetKey)))
                {
                    System.Diagnostics.Debug.WriteLine("[AppConfig] API 키 복호화 실패. 다른 PC 또는 다른 사용자 계정으로 실행했을 수 있습니다.");
                    return false;
                }

                Current.Binance.ApiKey = binanceKey;
                Current.Binance.ApiSecret = binanceSecret;
                Current.Bitget.ApiKey = bitgetKey;
                Current.Bitget.ApiSecret = bitgetSecret;
                Current.Bitget.Passphrase = bitgetPassphrase;
                Current.Bybit.ApiKey = bybitKey;
                Current.Bybit.ApiSecret = bybitSecret;
                Current.Telegram.BotToken = telegramToken;
                Current.Telegram.ChatId = telegramChatId;

                CurrentUsername = user.Username;
                return true;
            }
            catch { return false; }
        }

        public static void ClearCredentials()
        {
            if (Current != null)
            {
                Current.Binance.ApiKey = string.Empty;
                Current.Binance.ApiSecret = string.Empty;
                Current.Telegram.BotToken = string.Empty;
                Current.Telegram.ChatId = string.Empty;
            }
            CurrentUsername = string.Empty;
        }
    }

    public class BinanceSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
    }

    public class BitgetSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public string Passphrase { get; set; } = string.Empty;
    }

    public class TelegramSettings
    {
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
    }

    public class ConnectionStringsSettings
    {
        public string DefaultConnection { get; set; } = string.Empty;
    }

    public class TradingConfig
    {
        public List<string> Symbols { get; set; } = new();
        public ExchangeType SelectedExchange { get; set; } = ExchangeType.Binance;
        public PumpScanSettings PumpSettings { get; set; } = new();
        public TradingSettings GeneralSettings { get; set; } = new();
        public GridStrategySettings GridSettings { get; set; } = new();
        public ArbitrageSettings ArbitrageSettings { get; set; } = new();
        public bool IsSimulationMode { get; set; } = false;
    }

    public class ExternalApiSettings
    {
        public string CryptoPanicApiKey { get; set; } = string.Empty;
    }

    public class BybitSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
    }
}