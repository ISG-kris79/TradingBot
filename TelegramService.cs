using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TradingBot.TelegramCommands;

namespace TradingBot
{
    public class TelegramService
    {
        // Singleton Instance
        private static TelegramService _instance;
        public static TelegramService Instance => _instance ??= new TelegramService();

        private string BotToken;
        private string ChatId;
        private ITelegramBotClient _botClient;
        private CancellationTokenSource _recvCts;
        public Func<string> OnRequestStatus { get; set; } // 상태 요청 시 호출될 콜백
        public Action OnRequestStop { get; set; } // [추가] 정지 요청 시 호출될 콜백
        private Dictionary<string, ITelegramCommand> _commands;

        public void Initialize()
        {
            BotToken = AppConfig.TelegramBotToken;
            ChatId = AppConfig.TelegramChatId;

            try
            {
                if (!string.IsNullOrEmpty(BotToken))
                    _botClient = new TelegramBotClient(BotToken);
            }
            catch (Exception)
            {
                _botClient = null; // 토큰 형식이 잘못된 경우 등 초기화 실패 처리
            }

            // 명령어 등록
            _commands = new Dictionary<string, ITelegramCommand>();
            var statusCmd = new StatusCommand();
            var stopCmd = new StopCommand();
            _commands[statusCmd.Name] = statusCmd;
            _commands[stopCmd.Name] = stopCmd;
            _commands["/help"] = new HelpCommand(_commands.Values);
        }

        public void StartReceiving()
        {
            if (_botClient == null)
            {
                MainWindow.Instance?.UpdateTelegramStatus(false, "Telegram: Invalid Token");
                return;
            }
            if (_recvCts != null) return; // 이미 수신 중이면 패스

            _recvCts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = new[] { UpdateType.Message } // 메시지만 수신
            };

            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandlePollingErrorAsync,
                receiverOptions,
                _recvCts.Token
            );

            // [수정] 연결 상태 확인 및 UI 업데이트
            Task.Run(async () =>
            {
                try
                {
                    bool isConnected = await _botClient.TestApi(_recvCts.Token);
                    if (isConnected)
                    {
                        MainWindow.Instance?.UpdateTelegramStatus(true, "Telegram: 연결 성공 (ON)");
                    }
                    else
                    {
                        MainWindow.Instance?.UpdateTelegramStatus(false, "Telegram: 연결 실패");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.UpdateTelegramStatus(false, $"Telegram: Connect Failed ({ex.Message})");
                }
            });
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // 텍스트 메시지가 아니면 무시
            if (update.Message is not { } message) return;
            if (message.Text is not { } messageText) return;

            // 커맨드 패턴 적용
            var commandName = messageText.Split(' ')[0].ToLower();
            if (_commands.TryGetValue(commandName, out var command))
            {
                await command.ExecuteAsync(botClient, message, cancellationToken);
            }
            else if (messageText.StartsWith("/"))
            {
                // 알 수 없는 명령어 처리 (선택 사항)
            }
        }

        private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is Telegram.Bot.Exceptions.ApiRequestException apiEx && apiEx.ErrorCode == 401)
            {
                MainWindow.Instance?.AddLog("❌ 텔레그램 봇 토큰이 유효하지 않습니다. (401 Unauthorized)");
                MainWindow.Instance?.UpdateTelegramStatus(false, "Telegram: Unauthorized");
            }
            else
            {
                MainWindow.Instance?.AddLog($"⚠️ 텔레그램 폴링 에러: {exception.Message}");
            }
            return Task.CompletedTask;
        }

        public async Task SendMessageAsync(string message)
        {
            try
            {
                if (_botClient == null) return;

                // 최신 버전에서는 첫 번째 인자로 ChatId 객체를 넘깁니다.
                // ParseMode를 마크다운으로 설정하면 *굵게*, _기울임_ 등이 적용됩니다.
                await _botClient.SendMessage(
                    chatId: ChatId,
                    text: $"[AI QUANTUM]\n{message}",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
                );
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 401)
            {
                MainWindow.Instance?.AddLog("❌ 텔레그램 전송 실패: 봇 토큰이 유효하지 않습니다.");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ 텔레그램 발송 실패: {ex.Message}");
            }
        }
        // TradingEngine 내에서 호출 예시
        private async Task ExecutePumpTradeDetailed(string symbol, decimal quantity, decimal entryPrice)
        {
            // ... 주문 로직 ...
            string body = $"🔹 종목: {symbol}\n" +
                          $"🔹 수량: {quantity:N3}\n" +
                          $"🔹 진입가: ${entryPrice:N4}\n" +
                          $"🔹 예상 손절: -1.5%\n" +
                          $"🔹 전략: 급등주 스캔(PUMP)";

            await Instance.SendFormattedMessage("급등주 신규 진입", body);
        }
        public async Task SendFormattedMessage(string title, string body)
        {
            string emoji = title.Contains("진입") ? "🚀" : title.Contains("종료") ? "💰" : "📊";
            string message = $"{emoji} *[{title}]*\n\n{body}\n\n⏰ 시각: {DateTime.Now:HH:mm:ss}";

            await SendMessageAsync(message);
            await Task.Delay(10);
        }

        public async Task SendReportAsync(decimal totalBalance, decimal dailyPnl, double dailyPnlPercent, decimal tradePnl, int activeTrades)
        {
            string icon = tradePnl >= 0 ? "📈" : "📉";
            string report = $"{icon} *실시간 수익 보고서*\n\n" +
                           $"⚡ *이번 매매*: ${tradePnl:N2}\n" +
                           $"💰 *총 자산*: ${totalBalance:N2}\n" +
                           $"💵 *당일 손익*: ${dailyPnl:N2} ({dailyPnlPercent:F2}%)\n" +
                           $"📊 *운영 중인 포지션*: {activeTrades}개\n\n" +
                           $"🕒 _기준 시간: {DateTime.Now:HH:mm:ss}_";

            await SendMessageAsync(report);
        }

        // [추가] 봇 시작 알림
        public async Task NotifyBotStartedAsync(decimal initialBalance)
        {
            string message = $"🤖 *[시스템 알림]*\n\n✅ **트레이딩 봇이 시작되었습니다.**\n" +
                             $"💰 초기 자산: `${initialBalance:N2}`\n" +
                             $"⏰ 시작 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            await SendMessageAsync(message);
        }
    }
}