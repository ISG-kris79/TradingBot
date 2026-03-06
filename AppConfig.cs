using Microsoft.Extensions.Configuration;
using TradingBot.Models;
using TradingBot.Shared.Services;
using SecurityService = TradingBot.Shared.Services.SecurityService;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

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

        public static string BybitApiKey => Current?.Bybit?.ApiKey ?? string.Empty;
        public static string BybitApiSecret => Current?.Bybit?.ApiSecret ?? string.Empty;

        public static string CurrentUsername { get; private set; } = string.Empty;

        /// <summary>
        /// 현재 로그인한 사용자 정보 (관리자 여부 확인용)
        /// </summary>
        public static User? CurrentUser { get; private set; }

        public BinanceSettings Binance { get; set; } = new();
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

            // ConnectionString 복호화 처리
            if (config.ConnectionStrings?.IsEncrypted == true &&
                !string.IsNullOrEmpty(config.ConnectionStrings.DefaultConnection))
            {
                try
                {
                    string decrypted = SecurityService.DecryptString(config.ConnectionStrings.DefaultConnection);
                    if (!string.IsNullOrEmpty(decrypted))
                    {
                        config.ConnectionStrings.DefaultConnection = decrypted;
                        System.Diagnostics.Debug.WriteLine("[AppConfig] ConnectionString 복호화 성공");
                    }
                    else
                    {
                        // 복호화 실패
                        System.Diagnostics.Debug.WriteLine("[AppConfig] ConnectionString 복호화 실패 - 잘못된 암호화 형식이거나 키가 다릅니다.");
                        System.Windows.MessageBox.Show(
                            "데이터베이스 연결 설정을 복호화할 수 없습니다.\n\n" +
                            "암호화 형식이 잘못되었거나 암호화 키가 다릅니다.\n\n" +
                            "해결 방법:\n" +
                            "1. appsettings.json에서 IsEncrypted를 false로 변경\n" +
                            "2. DefaultConnection에 평문 연결 문자열 입력\n" +
                            "3. 또는 다시 암호화 (TradingBot.exe --encrypt-connection)",
                            "설정 오류",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        config.ConnectionStrings.DefaultConnection = string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppConfig] ConnectionString 복호화 오류: {ex.Message}");
                    System.Windows.MessageBox.Show(
                        $"데이터베이스 연결 설정 복호화 중 오류 발생:\n{ex.Message}\n\n" +
                        "appsettings.json에서 IsEncrypted를 false로 변경하고 평문으로 설정하세요.",
                        "설정 오류",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                    config.ConnectionStrings.DefaultConnection = string.Empty;
                }
            }

            // [디버깅용] 설정 로드 여부 확인 (Visual Studio 출력 창에서 확인)
            System.Diagnostics.Debug.WriteLine($"[AppConfig] Binance Key Loaded: {!string.IsNullOrEmpty(BinanceApiKey)}");
            System.Diagnostics.Debug.WriteLine($"[AppConfig] Telegram Token Loaded: {!string.IsNullOrEmpty(TelegramBotToken)}");
            System.Diagnostics.Debug.WriteLine($"[AppConfig] ConnectionString Loaded: {!string.IsNullOrEmpty(ConnectionString)}");
        }

        public static void SetUserCredentials(string apiKey, string apiSecret, string botToken, string chatId, string username)
        {
            if (Current != null)
            {
                Current.Binance.ApiKey = apiKey;
                Current.Binance.ApiSecret = apiSecret;
                Current.Telegram.BotToken = botToken;
                Current.Telegram.ChatId = chatId;
            }
            CurrentUsername = username;
        }

        /// <summary>
        /// 로그인 성공 후 User 객체로부터 자격 증명을 설정합니다.
        /// DB에 암호화된 키를 복호화하여 메모리에 로드합니다.
        /// 복호화 실패 시 평문으로 간주하고 사용합니다.
        /// </summary>
        /// <param name="user">DB에서 조회한 User 객체</param>
        /// <returns>설정 성공 여부</returns>
        public static bool SetUserCredentials(User user)
        {
            if (Current == null || user == null) return false;

            try
            {
                // 암호화된 키들을 복호화 (실패 시 평문으로 간주)
                string binanceKey = DecryptOrUseRaw(user.BinanceApiKey);
                string binanceSecret = DecryptOrUseRaw(user.BinanceApiSecret);
                string bybitKey = DecryptOrUseRaw(user.BybitApiKey);
                string bybitSecret = DecryptOrUseRaw(user.BybitApiSecret);
                string telegramToken = DecryptOrUseRaw(user.TelegramBotToken);
                string telegramChatId = DecryptOrUseRaw(user.TelegramChatId);

                Current.Binance.ApiKey = binanceKey;
                Current.Binance.ApiSecret = binanceSecret;
                Current.Bybit.ApiKey = bybitKey;
                Current.Bybit.ApiSecret = bybitSecret;
                Current.Telegram.BotToken = telegramToken;
                Current.Telegram.ChatId = telegramChatId;

                CurrentUsername = user.Username;
                CurrentUser = user; // 현재 사용자 정보 저장

                System.Diagnostics.Debug.WriteLine($"[AppConfig] 사용자 자격 증명 로드 성공: {user.Username}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppConfig] 사용자 자격 증명 로드 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 복호화를 시도하고, 실패 시 원본 값을 반환합니다.
        /// DPAPI로 암호화된 값이나 AES256으로 암호화된 값 모두 처리합니다.
        /// </summary>
        private static string DecryptOrUseRaw(string encryptedValue)
        {
            if (string.IsNullOrEmpty(encryptedValue))
                return string.Empty;

            // 복호화 시도
            string decrypted = TradingBot.Shared.Services.SecurityService.DecryptString(encryptedValue);

            // 복호화 성공 또는 원본값이 평문인 경우
            if (!string.IsNullOrEmpty(decrypted))
                return decrypted;

            // 복호화 실패 -> 평문으로 간주
            return encryptedValue;
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
            CurrentUser = null; // 사용자 정보 초기화
        }
    }

    public class BinanceSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
    }

    public class BybitSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
    }

    public class TelegramSettings
    {
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
    }

    public class ConnectionStringsSettings
    {
        public string DefaultConnection { get; set; } = string.Empty;
        public bool IsEncrypted { get; set; } = false;
    }

    public class TradingConfig
    {
        public List<string> Symbols { get; set; } = new();
        public ExchangeType SelectedExchange { get; set; } = ExchangeType.Binance;
        public PumpScanSettings PumpSettings { get; set; } = new();
        public TradingSettings GeneralSettings { get; set; } = new();
        public GridStrategySettings GridSettings { get; set; } = new();
        public ArbitrageSettings ArbitrageSettings { get; set; } = new();
        public TransformerSettings TransformerSettings { get; set; } = new();
        public DeFiSettings DeFiSettings { get; set; } = new();
        public DcaSettings DcaSettings { get; set; } = new(); // [Agent 2] 추가
        public FundTransferSettings FundTransferSettings { get; set; } = new(); // [Phase 13] 추가
        public PortfolioRebalancingSettings PortfolioRebalancingSettings { get; set; } = new(); // [Phase 13] 추가
        public bool IsSimulationMode { get; set; } = false;
        public decimal SimulationInitialBalance { get; set; } = 10000m;
    }

    public class ExternalApiSettings
    {
        public string CryptoPanicApiKey { get; set; } = string.Empty;
    }

    public class TransformerSettings
    {
        public int InputDim { get; set; } = 21;
        public int DModel { get; set; } = 128;
        public int NHeads { get; set; } = 8;
        public int NLayers { get; set; } = 4;
        public int OutputDim { get; set; } = 1;
        public int SeqLen { get; set; } = 60;
        public int BatchSize { get; set; } = 32;
        public int Epochs { get; set; } = 10;
        public double LearningRate { get; set; } = 0.001;

        public int AdxPeriod { get; set; } = 14;
        public double AdxSidewaysThreshold { get; set; } = 20.0;

        public double SidewaysRsiLongMax { get; set; } = 35.0;
        public double SidewaysRsiShortMin { get; set; } = 65.0;
        public double SidewaysVolumeRatioMax { get; set; } = 1.5;

        public decimal SidewaysLongLowerBandTouchMultiplier { get; set; } = 1.001m;
        public decimal SidewaysShortUpperBandTouchMultiplier { get; set; } = 0.999m;
        public decimal SidewaysLongStopLossMultiplier { get; set; } = 0.9975m;
        public decimal SidewaysShortStopLossMultiplier { get; set; } = 1.0025m;
    }

    public class DeFiSettings
    {
        public string RpcUrl { get; set; } = "https://mainnet.infura.io/v3/YOUR_KEY";
        public string WalletPrivateKey { get; set; } = ""; // 암호화 저장 권장
        public string EtherscanApiKey { get; set; } = "";
        public decimal WhaleThresholdUsd { get; set; } = 1000000; // 100만불 이상 감지
    }

    public class DcaSettings
    {
        public bool Enabled { get; set; } = false;
        public decimal AmountPerOrder { get; set; } = 10m; // USDT per order
        public int IntervalMinutes { get; set; } = 60; // 1 hour default
        public List<string> TargetSymbols { get; set; } = new();
    }

    /// <summary>
    /// 자금 이동 설정 (Phase 13)
    /// </summary>
    public class FundTransferSettings
    {
        public int CheckIntervalMinutes { get; set; } = 60; // 1시간마다 체크
        public decimal MinTransferAmount { get; set; } = 100m; // 최소 100 USDT
        public bool AutoTransfer { get; set; } = false; // 자동 이동 여부 (기본: 수동)
        public bool SimulationMode { get; set; } = true; // [Phase 14] 시뮬레이션 모드 (안전)
    }

    /// <summary>
    /// 리밸런싱 설정 (Phase 13)
    /// </summary>
    public class PortfolioRebalancingSettings
    {
        public int CheckIntervalHours { get; set; } = 24; // 24시간마다 체크
        public decimal RebalanceThreshold { get; set; } = 5.0m; // 5% 이상 편차 시 리밸런싱
        public bool AutoRebalance { get; set; } = false; // 자동 리밸런싱 여부 (기본: 수동)
        public bool SimulationMode { get; set; } = true; // [Phase 14] 시뮬레이션 모드 (안전)
        
        // 목표 자산 배분 (%)
        public Dictionary<string, decimal> TargetAllocation { get; set; } = new()
        {
            { "USDT", 40m },  // 40% 현금
            { "BTC", 30m },   // 30% 비트코인
            { "ETH", 20m },   // 20% 이더리움
            { "BNB", 10m }    // 10% 바이낸스코인
        };
    }
}
