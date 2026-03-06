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
        private static TelegramService? _instance;
        public static TelegramService Instance => _instance ??= new TelegramService();

        private string BotToken = string.Empty;
        private string ChatId = string.Empty;
        private ITelegramBotClient? _botClient;
        private CancellationTokenSource? _recvCts;
        public Func<string>? OnRequestStatus { get; set; } // 상태 요청 시 호출될 콜백
        public Action? OnRequestStop { get; set; } // [추가] 정지 요청 시 호출될 콜백
        private Dictionary<string, ITelegramCommand> _commands = new Dictionary<string, ITelegramCommand>();

        public void Initialize()
        {
            BotToken = AppConfig.TelegramBotToken;
            ChatId = AppConfig.TelegramChatId;

            try
            {
                if (string.IsNullOrEmpty(BotToken))
                {
                    _botClient = null;
                    MainWindow.Instance?.AddLog("⚠️ [Telegram] 토큰이 설정되지 않았습니다. 사용자 설정에서 텔레그램 봇 토큰을 입력하세요.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(ChatId))
                {
                    _botClient = null;
                    MainWindow.Instance?.AddLog("⚠️ [Telegram] ChatId가 설정되지 않았습니다. 사용자 설정에서 ChatId를 입력하세요.");
                    return;
                }

                _botClient = new TelegramBotClient(BotToken);
                MainWindow.Instance?.AddLog($"✅ [Telegram] 봇 클라이언트 초기화 성공 (토큰 길이: {BotToken.Length})");
            }
            catch (Exception ex)
            {
                _botClient = null;
                MainWindow.Instance?.AddLog($"❌ [Telegram] 토큰 형식 오류: {ex.Message}");
                MainWindow.Instance?.AddLog("💡 [Telegram] BotFather로부터 받은 올바른 토큰을 입력하세요 (예: 123456789:ABCdefGHIjklMNOpqrsTUVwxyz)");
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
            // [수정] 초기화가 누락되었거나, 로그인 후 토큰이 갱신된 경우 자동 재초기화
            if (_botClient == null || BotToken != AppConfig.TelegramBotToken)
            {
                Initialize();
            }

            if (_botClient == null)
            {
                string errorMsg = string.IsNullOrEmpty(BotToken)
                    ? "Telegram: 토큰 미설정"
                    : "Telegram: 토큰 형식 오류";
                MainWindow.Instance?.UpdateTelegramStatus(false, errorMsg);
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
                // [수정] 전송 시점에 초기화 누락/설정 변경이 있어도 자동 복구
                if (_botClient == null || BotToken != AppConfig.TelegramBotToken || ChatId != AppConfig.TelegramChatId)
                {
                    Initialize();
                }

                if (_botClient == null)
                {
                    MainWindow.Instance?.AddLog("⚠️ [Telegram] 봇 클라이언트 미초기화 상태로 전송이 건너뛰어졌습니다.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(ChatId))
                {
                    MainWindow.Instance?.AddLog("⚠️ [Telegram] ChatId가 비어 있어 메시지를 보낼 수 없습니다.");
                    return;
                }

                // 최신 버전에서는 첫 번째 인자로 ChatId 객체를 넘깁니다.
                // ParseMode를 마크다운으로 설정하면 *굵게*, _기울임_ 등이 적용됩니다.
                await _botClient.SendMessage(
                    chatId: ChatId,
                    text: $"[TradingBot]\n{message}",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
                );
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 401)
            {
                MainWindow.Instance?.AddLog("❌ 텔레그램 전송 실패: 봇 토큰이 유효하지 않습니다.");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("parse entities"))
            {
                // [수정] 마크다운 파싱 오류 시 plain text로 재시도
                try
                {
                    if (_botClient == null)
                    {
                        MainWindow.Instance?.AddLog("⚠️ [Telegram] 재전송 실패: 봇 클라이언트가 초기화되지 않았습니다.");
                        return;
                    }

                    await _botClient.SendMessage(
                        chatId: ChatId,
                        text: $"[TradingBot]\n{message}"
                    );
                    MainWindow.Instance?.AddLog("ℹ️ [Telegram] Markdown 파싱 오류로 plain text 전송으로 대체했습니다.");
                }
                catch (Exception retryEx)
                {
                    MainWindow.Instance?.AddLog($"⚠️ 텔레그램 재전송 실패: {retryEx.Message}");
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400)
            {
                string maskedChatId = ChatId.Length > 4 ? $"***{ChatId[^4..]}" : "***";
                MainWindow.Instance?.AddLog($"⚠️ 텔레그램 전송 실패(400): {ex.Message} | ChatId: {maskedChatId}");
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

        // [Phase 14] 차익거래 기회 알림
        public async Task SendArbitrageOpportunityAsync(string symbol, string buyExchange, string sellExchange,
            decimal buyPrice, decimal sellPrice, decimal profitPercent, decimal estimatedProfit)
        {
            string icon = profitPercent >= 3m ? "🔥" : profitPercent >= 2m ? "💎" : "💰";
            string message = $"{icon} *[차익거래 기회 감지]*\n\n" +
                           $"🪙 *종목*: {symbol}\n" +
                           $"📊 *매수*: {buyExchange} @ ${buyPrice:N4}\n" +
                           $"📊 *매도*: {sellExchange} @ ${sellPrice:N4}\n" +
                           $"💵 *수익률*: {profitPercent:F2}%\n" +
                           $"💰 *예상 수익*: ${estimatedProfit:N2}\n\n" +
                           $"⏰ 감지 시간: {DateTime.Now:HH:mm:ss}";
            await SendMessageAsync(message);
        }

        // [Phase 14] 차익거래 실행 결과 알림
        public async Task SendArbitrageExecutionResultAsync(string symbol, bool success, decimal actualProfit, string? errorMessage = null)
        {
            string icon = success ? "✅" : "❌";
            string message = success
                ? $"{icon} *[차익거래 실행 완료]*\n\n" +
                  $"🪙 *종목*: {symbol}\n" +
                  $"💰 *실현 수익*: ${actualProfit:N2}\n" +
                  $"⏰ 완료 시간: {DateTime.Now:HH:mm:ss}"
                : $"{icon} *[차익거래 실행 실패]*\n\n" +
                  $"🪙 *종목*: {symbol}\n" +
                  $"⚠️ *에러*: {errorMessage}\n" +
                  $"⏰ 실패 시간: {DateTime.Now:HH:mm:ss}";
            await SendMessageAsync(message);
        }

        // [Phase 14] 자금 이동 알림
        public async Task SendFundTransferNotificationAsync(string fromExchange, string toExchange,
            string asset, decimal amount, bool success, string? errorMessage = null)
        {
            string icon = success ? "✅" : "❌";
            string status = success ? "완료" : "실패";
            string message = $"{icon} *[자금 이동 {status}]*\n\n" +
                           $"💱 *자산*: {asset}\n" +
                           $"💵 *수량*: {amount:N8}\n" +
                           $"🏦 *출발*: {fromExchange}\n" +
                           $"🏦 *도착*: {toExchange}\n";

            if (!success && !string.IsNullOrEmpty(errorMessage))
            {
                message += $"⚠️ *에러*: {errorMessage}\n";
            }

            message += $"⏰ 시간: {DateTime.Now:HH:mm:ss}";
            await SendMessageAsync(message);
        }

        // [Phase 14] 포트폴리오 리밸런싱 알림
        public async Task SendRebalancingNotificationAsync(decimal totalPortfolioValue, int actionCount,
            decimal totalCost, bool success, List<string>? actions = null)
        {
            string icon = success ? "✅" : "❌";
            string status = success ? "완료" : "실패";
            string message = $"{icon} *[포트폴리오 리밸런싱 {status}]*\n\n" +
                           $"💰 *포트폴리오 가치*: ${totalPortfolioValue:N2}\n" +
                           $"📊 *실행 액션*: {actionCount}개\n" +
                           $"💵 *총 비용*: ${totalCost:N2}\n";

            if (success && actions != null && actions.Any())
            {
                message += $"\n📋 *실행된 액션*:\n";
                foreach (var action in actions.Take(5)) // 최대 5개만 표시
                {
                    message += $"  • {action}\n";
                }
                if (actions.Count > 5)
                {
                    message += $"  ... 외 {actions.Count - 5}개\n";
                }
            }

            message += $"\n⏰ 완료 시간: {DateTime.Now:HH:mm:ss}";
            await SendMessageAsync(message);
        }
    }
}
