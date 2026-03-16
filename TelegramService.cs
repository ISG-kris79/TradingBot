using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.IO;
using System.Globalization;
using System.Collections.Concurrent;
using TradingBot.TelegramCommands;

namespace TradingBot
{
    public enum TelegramMessageType
    {
        Alert,
        Profit,
        Entry,
        AiGate,
        Log
    }

    public class TelegramService
    {
        // Singleton Instance
        private static TelegramService? _instance;
        public static TelegramService Instance => _instance ??= new TelegramService();

        private string BotToken = string.Empty;
        private string ChatId = string.Empty;
        private ITelegramBotClient? _botClient;
        private CancellationTokenSource? _recvCts;
        private readonly ConcurrentDictionary<string, DateTime> _aiGateLastSentAtUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan AiGatePerSymbolCooldown = TimeSpan.FromMinutes(1);
        private readonly object _aiGateSummaryLock = new();
        private AiGateSummaryWindow _aiGateSummaryWindow = new();
        public Func<string>? OnRequestStatus { get; set; } // 상태 요청 시 호출될 콜백
        public Action? OnRequestStop { get; set; } // [추가] 정지 요청 시 호출될 콜백
        public Func<CancellationToken, Task<string>>? OnRequestTrain { get; set; } // [추가] 수동 학습 요청 콜백
        public Func<CancellationToken, Task<string>>? OnRequestDroughtScan { get; set; } // [추가] 드라이스펠 진단 수동 실행 콜백
        private Dictionary<string, ITelegramCommand> _commands = new Dictionary<string, ITelegramCommand>();

        private sealed class AiGateSummaryWindow
        {
            public DateTime WindowStartLocal { get; set; } = DateTime.Now;
            public int TotalCount { get; set; }
            public int AllowedCount { get; set; }
            public int BlockedCount { get; set; }
            public int StrongAllowedCount { get; set; }
            public int LongCount { get; set; }
            public int ShortCount { get; set; }
            public int MajorCount { get; set; }
            public int PumpingCount { get; set; }
            public int NormalCount { get; set; }
            public float MlConfidenceSum { get; set; }
            public float TfConfidenceSum { get; set; }
            public float TrendScoreSum { get; set; }
            public Dictionary<string, int> BlockReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> BlockReasonsLong { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> BlockReasonsShort { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> BlockReasonsOther { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> AllowedLongSymbolReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> AllowedShortSymbolReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> AllowedOtherSymbolReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> BlockedLongSymbolReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> BlockedShortSymbolReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> BlockedOtherSymbolReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

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
            var trainCmd = new TrainCommand();
            var droughtCmd = new DroughtScanCommand();
            _commands[statusCmd.Name] = statusCmd;
            _commands[stopCmd.Name] = stopCmd;
            _commands[trainCmd.Name] = trainCmd;
            _commands[droughtCmd.Name] = droughtCmd;
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

            // [FIX] 공백 제거 및 빈 메시지 체크
            messageText = messageText.Trim();
            if (string.IsNullOrEmpty(messageText)) return;

            // 커맨드 패턴 적용
            var parts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var commandName = parts[0].ToLower();
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

        private void LogTelegramFailure(string scope, string message, Exception? ex = null)
        {
            string logMessage = string.IsNullOrWhiteSpace(scope)
                ? $"⚠️ [Telegram] {message}"
                : $"⚠️ [Telegram][{scope}] {message}";

            MainWindow.Instance?.AddLog(logMessage);

            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "telegram_error.log");
                string detail = ex == null ? string.Empty : $" | {ex.GetType().Name}: {ex.Message}";
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {logMessage}{detail}{Environment.NewLine}");
            }
            catch
            {
                // 파일 로깅 실패는 무시
            }
        }

        private void LogTelegramInfo(string scope, string message)
        {
            string logMessage = string.IsNullOrWhiteSpace(scope)
                ? $"ℹ️ [Telegram] {message}"
                : $"ℹ️ [Telegram][{scope}] {message}";

            MainWindow.Instance?.AddLog(logMessage);
        }

        private bool EnsureTelegramClientReady(string scope)
        {
            if (_botClient == null || BotToken != AppConfig.TelegramBotToken || ChatId != AppConfig.TelegramChatId)
            {
                Initialize();
            }

            if (_botClient == null)
            {
                LogTelegramFailure(scope, "봇 클라이언트 미초기화 상태로 전송이 건너뛰어졌습니다.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(ChatId))
            {
                LogTelegramFailure(scope, "ChatId가 비어 있어 메시지를 보낼 수 없습니다.");
                return false;
            }

            return true;
        }

        private static bool IsMessageTypeEnabled(TelegramMessageType messageType)
        {
            var telegram = AppConfig.Current?.Telegram;
            if (telegram == null)
                return true;

            return messageType switch
            {
                TelegramMessageType.Alert => telegram.EnableAlertMessages,
                TelegramMessageType.Profit => telegram.EnableProfitMessages,
                TelegramMessageType.Entry => telegram.EnableEntryMessages,
                TelegramMessageType.AiGate => telegram.EnableAiGateMessages,
                TelegramMessageType.Log => telegram.EnableLogMessages,
                _ => true
            };
        }

        private async Task SendInternalAsync(
            string text,
            bool disableNotification = false,
            string scope = "General",
            TelegramMessageType messageType = TelegramMessageType.Alert)
        {
            try
            {
                if (!IsMessageTypeEnabled(messageType))
                {
                    return;
                }

                if (!EnsureTelegramClientReady(scope))
                {
                    return;
                }

                await _botClient!.SendMessage(
                    chatId: ChatId,
                    text: text,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    disableNotification: disableNotification
                );
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 401)
            {
                LogTelegramFailure(scope, "봇 토큰이 유효하지 않습니다.", ex);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("parse entities"))
            {
                try
                {
                    if (!EnsureTelegramClientReady(scope))
                    {
                        return;
                    }

                    await _botClient!.SendMessage(
                        chatId: ChatId,
                        text: text,
                        disableNotification: disableNotification
                    );

                    LogTelegramInfo(scope, "Markdown 파싱 오류로 plain text 전송으로 대체했습니다.");
                }
                catch (Exception retryEx)
                {
                    LogTelegramFailure(scope, "plain text 재전송도 실패했습니다.", retryEx);
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400)
            {
                string maskedChatId = ChatId.Length > 4 ? $"***{ChatId[^4..]}" : "***";
                LogTelegramFailure(scope, $"전송 실패(400): {ex.Message} | ChatId: {maskedChatId}", ex);
            }
            catch (Exception ex)
            {
                LogTelegramFailure(scope, "발송 실패", ex);
            }
        }

        public async Task SendMessageAsync(string message, TelegramMessageType messageType = TelegramMessageType.Alert)
        {
            await SendInternalAsync($"[TradingBot]\n{message}", false, "General", messageType);
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

            await SendMessageAsync(message, TelegramMessageType.Alert);
            await Task.Delay(10);
        }

        public async Task SendReportAsync(
            decimal totalBalance,
            decimal dailyPnl,
            double dailyPnlPercent,
            decimal tradePnl,
            int activeTrades,
            int? aiLabelTotal = null,
            int? aiLabelLabeled = null,
            double? aiLabelRate = null,
            int? aiLabelMarkToMarket = null,
            int? aiLabelClose = null,
            int? aiActiveDecision = null)
        {
            string icon = tradePnl >= 0 ? "📈" : "📉";
            string report = $"{icon} *실시간 수익 보고서*\n\n" +
                           $"⚡ *이번 매매*: ${tradePnl:N2}\n" +
                           $"💰 *총 자산*: ${totalBalance:N2}\n" +
                           $"💵 *당일 손익*: ${dailyPnl:N2} ({dailyPnlPercent:F2}%)\n" +
                           $"📊 *운영 중인 포지션*: {activeTrades}개\n";

            if (aiLabelTotal.HasValue)
            {
                string rateText = aiLabelRate.HasValue ? $"{aiLabelRate.Value:F1}%" : "0.0%";
                int labeled = aiLabelLabeled ?? 0;
                int markToMarket = aiLabelMarkToMarket ?? 0;
                int close = aiLabelClose ?? 0;
                int activeDecision = aiActiveDecision ?? 0;

                report += $"🧠 *AI 라벨링*: total={aiLabelTotal.Value}, labeled={labeled} ({rateText}), m2m={markToMarket}, close={close}, activeDecision={activeDecision}\n";
            }

            report += "\n" +
                           $"🕒 _기준 시간: {DateTime.Now:HH:mm:ss}_";

            await SendMessageAsync(report, TelegramMessageType.Profit);
        }

        // [추가] 봇 시작 알림
        public async Task NotifyBotStartedAsync(decimal initialBalance)
        {
            string message = $"🤖 *[시스템 알림]*\n\n✅ **트레이딩 봇이 시작되었습니다.**\n" +
                             $"💰 초기 자산: `${initialBalance:N2}`\n" +
                             $"⏰ 시작 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            await SendMessageAsync(message, TelegramMessageType.Alert);
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
            await SendMessageAsync(message, TelegramMessageType.Alert);
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
            await SendMessageAsync(message, TelegramMessageType.Profit);
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
            await SendMessageAsync(message, TelegramMessageType.Alert);
        }

        // ─────────────────────────────────────────────────────────────────────
        // [AI 관제탑] AI Gate PASS/BLOCK 5분 집계 텔레그램 알림
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AI 이중 게이트(TF Brain + ML Filter) 결과를 5분 요약용으로 집계합니다.
        /// - BLOCK + ML 0.50 미만은 기존과 동일하게 집계 생략
        /// - 같은 코인은 1분 내 중복 집계를 막아 요약 스팸을 줄임
        /// </summary>
        public Task SendAiGateResultAsync(
            string symbol, string decision, bool allowed,
            string coinType, string reason,
            float mlConf, float tfConf, float trendScore,
            float rsi = 0f, float bbPos = 0f)
        {
            if (!allowed && mlConf < 0.50f)
                return Task.CompletedTask;

            string symbolKey = string.IsNullOrWhiteSpace(symbol)
                ? string.Empty
                : symbol.Trim().ToUpperInvariant();

            if (!string.IsNullOrEmpty(symbolKey))
            {
                DateTime nowUtc = DateTime.UtcNow;
                DateTime applied = _aiGateLastSentAtUtc.AddOrUpdate(
                    symbolKey,
                    nowUtc,
                    (_, previousUtc) =>
                        (nowUtc - previousUtc) >= AiGatePerSymbolCooldown ? nowUtc : previousUtc);

                if (applied != nowUtc)
                    return Task.CompletedTask;
            }

            lock (_aiGateSummaryLock)
            {
                var summary = _aiGateSummaryWindow;
                summary.TotalCount++;
                summary.MlConfidenceSum += SanitizeFinite(mlConf);
                summary.TfConfidenceSum += SanitizeFinite(tfConf);
                summary.TrendScoreSum += SanitizeFinite(trendScore);

                if (string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
                    summary.LongCount++;
                else if (string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase))
                    summary.ShortCount++;

                switch (coinType)
                {
                    case "Major":
                        summary.MajorCount++;
                        break;
                    case "Pumping":
                        summary.PumpingCount++;
                        break;
                    default:
                        summary.NormalCount++;
                        break;
                }

                if (allowed)
                {
                    summary.AllowedCount++;
                    if (mlConf >= 0.80f)
                        summary.StrongAllowedCount++;

                    if (!string.IsNullOrWhiteSpace(symbolKey))
                    {
                        string allowReason = NormalizeAiGateAllowedReason(reason);
                        string bucket = NormalizeDecisionBucket(decision);
                        switch (bucket)
                        {
                            case "LONG":
                                summary.AllowedLongSymbolReasons[symbolKey] = allowReason;
                                break;
                            case "SHORT":
                                summary.AllowedShortSymbolReasons[symbolKey] = allowReason;
                                break;
                            default:
                                summary.AllowedOtherSymbolReasons[symbolKey] = allowReason;
                                break;
                        }
                    }
                }
                else
                {
                    summary.BlockedCount++;
                    string blockReason = NormalizeAiGateBlockReason(reason);
                    string bucket = NormalizeDecisionBucket(decision);

                    if (!string.IsNullOrWhiteSpace(symbolKey))
                    {
                        switch (bucket)
                        {
                            case "LONG":
                                summary.BlockedLongSymbolReasons[symbolKey] = blockReason;
                                break;
                            case "SHORT":
                                summary.BlockedShortSymbolReasons[symbolKey] = blockReason;
                                break;
                            default:
                                summary.BlockedOtherSymbolReasons[symbolKey] = blockReason;
                                break;
                        }
                    }

                    if (summary.BlockReasons.TryGetValue(blockReason, out int currentCount))
                        summary.BlockReasons[blockReason] = currentCount + 1;
                    else
                        summary.BlockReasons[blockReason] = 1;

                    var directionalReasons = bucket switch
                    {
                        "LONG" => summary.BlockReasonsLong,
                        "SHORT" => summary.BlockReasonsShort,
                        _ => summary.BlockReasonsOther
                    };

                    if (directionalReasons.TryGetValue(blockReason, out int directionalCount))
                        directionalReasons[blockReason] = directionalCount + 1;
                    else
                        directionalReasons[blockReason] = 1;
                }
            }

            return Task.CompletedTask;
        }

        public async Task FlushAiGateSummaryAsync(bool forceSendEmpty = false)
        {
            AiGateSummaryWindow snapshot;
            DateTime windowEndLocal = DateTime.Now;

            lock (_aiGateSummaryLock)
            {
                if (_aiGateSummaryWindow.TotalCount == 0 && !forceSendEmpty)
                    return;

                snapshot = _aiGateSummaryWindow;
                _aiGateSummaryWindow = new AiGateSummaryWindow
                {
                    WindowStartLocal = windowEndLocal
                };
            }

            string body;
            string approvedOnlyBody;
            if (snapshot.TotalCount == 0)
            {
                body = $"🕒 구간: {snapshot.WindowStartLocal:HH:mm} ~ {windowEndLocal:HH:mm}\n" +
                       "📭 최근 5분 동안 AI 게이트 판정이 없었습니다.";

                approvedOnlyBody = $"🕒 구간: {snapshot.WindowStartLocal:HH:mm} ~ {windowEndLocal:HH:mm}\n" +
                                   "📭 최근 5분 동안 승인된 코인이 없습니다.";
            }
            else
            {
                float avgMl = snapshot.TotalCount > 0 ? snapshot.MlConfidenceSum / snapshot.TotalCount : 0f;
                float avgTf = snapshot.TotalCount > 0 ? snapshot.TfConfidenceSum / snapshot.TotalCount : 0f;
                float avgTrend = snapshot.TotalCount > 0 ? snapshot.TrendScoreSum / snapshot.TotalCount : 0f;
                string topReasons = snapshot.BlockReasons.Count == 0
                    ? "없음"
                    : string.Join("\n", snapshot.BlockReasons
                        .OrderByDescending(kv => kv.Value)
                        .Take(3)
                        .Select((kv, index) => $"{index + 1}. {kv.Key}: {kv.Value}건"));
                string topReasonsLong = FormatTopReasons(snapshot.BlockReasonsLong, 3);
                string topReasonsShort = FormatTopReasons(snapshot.BlockReasonsShort, 3);
                string topReasonsOther = FormatTopReasons(snapshot.BlockReasonsOther, 2);

                string allowedLongCoins = FormatCoinReasonList(snapshot.AllowedLongSymbolReasons, 8);
                string allowedShortCoins = FormatCoinReasonList(snapshot.AllowedShortSymbolReasons, 8);
                string blockedLongCoins = FormatCoinReasonList(snapshot.BlockedLongSymbolReasons, 8);
                string blockedShortCoins = FormatCoinReasonList(snapshot.BlockedShortSymbolReasons, 8);

                string allowedOtherCoins = snapshot.AllowedOtherSymbolReasons.Count > 0
                    ? FormatCoinReasonList(snapshot.AllowedOtherSymbolReasons, 8)
                    : "없음";

                string blockedOtherCoins = snapshot.BlockedOtherSymbolReasons.Count > 0
                    ? FormatCoinReasonList(snapshot.BlockedOtherSymbolReasons, 8)
                    : "없음";

                string allowedSection = $"• LONG: {allowedLongCoins}\n" +
                                       $"• SHORT: {allowedShortCoins}";
                if (snapshot.AllowedOtherSymbolReasons.Count > 0)
                {
                    allowedSection += $"\n• 기타: {allowedOtherCoins}";
                }

                string blockedSection = $"• LONG: {blockedLongCoins}\n" +
                                       $"• SHORT: {blockedShortCoins}";
                if (snapshot.BlockedOtherSymbolReasons.Count > 0)
                {
                    blockedSection += $"\n• 기타: {blockedOtherCoins}";
                }

                approvedOnlyBody = $"🕒 구간: {snapshot.WindowStartLocal:HH:mm} ~ {windowEndLocal:HH:mm}\n" +
                                   $"✅ 승인: {snapshot.AllowedCount}건 (확신 {snapshot.StrongAllowedCount}건)\n" +
                                   $"🟢 LONG 승인: {snapshot.AllowedLongSymbolReasons.Count}개 │ 🔴 SHORT 승인: {snapshot.AllowedShortSymbolReasons.Count}개\n" +
                                   $"🤖 평균 ML: {avgMl:P1} │ 🧠 평균 TF: {avgTf:P1} │ 📈 평균 Trend(피보반영): {avgTrend:P1}\n\n" +
                                   $"✅ 승인 코인(사유)\n" +
                                   $"• LONG: {allowedLongCoins}\n" +
                                   $"• SHORT: {allowedShortCoins}\n" +
                                   $"• 기타: {allowedOtherCoins}";

                body = $"🕒 구간: {snapshot.WindowStartLocal:HH:mm} ~ {windowEndLocal:HH:mm}\n" +
                       $"📊 총 판정: {snapshot.TotalCount}건\n" +
                       $"✅ 승인: {snapshot.AllowedCount}건 (확신 {snapshot.StrongAllowedCount}건)\n" +
                       $"⛔ 차단: {snapshot.BlockedCount}건\n" +
                       $"🟢 LONG: {snapshot.LongCount}건 │ 🔴 SHORT: {snapshot.ShortCount}건\n" +
                       $"🏆 메이저: {snapshot.MajorCount} │ 🚀 펌핑: {snapshot.PumpingCount} │ 📊 일반: {snapshot.NormalCount}\n" +
                      $"🤖 평균 ML: {avgMl:P1} │ 🧠 평균 TF: {avgTf:P1} │ 📈 평균 Trend(피보반영): {avgTrend:P1}\n\n" +
                       $"✅ 승인 코인(사유)\n{allowedSection}\n\n" +
                       $"⛔ 차단 코인(사유)\n{blockedSection}\n\n" +
                      $"📌 차단 TOP(전체)\n{topReasons}\n\n" +
                      $"📌 차단 TOP(LONG)\n{topReasonsLong}\n\n" +
                      $"📌 차단 TOP(SHORT)\n{topReasonsShort}" +
                      (snapshot.BlockReasonsOther.Count > 0 ? $"\n\n📌 차단 TOP(기타)\n{topReasonsOther}" : string.Empty);
            }

                 await SendInternalAsync($"[TradingBot]\n*[AI 관제탑 5분 요약]*\n\n{body}", true, "AI관제탑", TelegramMessageType.AiGate);

                 bool hasApprovedTargets = snapshot.AllowedLongSymbolReasons.Count > 0
                    || snapshot.AllowedShortSymbolReasons.Count > 0
                    || snapshot.AllowedOtherSymbolReasons.Count > 0;

                 if (hasApprovedTargets)
                 {
                     await SendInternalAsync($"[TradingBot]\n*[AI 관제탑 승인코인 5분 브리핑]*\n\n{approvedOnlyBody}", true, "AI관제탑-승인코인", TelegramMessageType.AiGate);
                 }
        }

        private static float SanitizeFinite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private static string NormalizeAiGateBlockReason(string reason)
        {
            string text = reason ?? string.Empty;

            if (HasPositiveFibBonus(text) && text.Contains("Dual_Reject", StringComparison.OrdinalIgnoreCase))
                return "피보 가점 반영 후 ML/TF 미달";

            return text switch
            {
                var r when r.Contains("DeadCat_Block", StringComparison.OrdinalIgnoreCase) => "데드캣 붕괴 차단",
                var r when r.Contains("RSI_Overheat", StringComparison.OrdinalIgnoreCase) => "RSI 과열 하드차단",
                var r when r.Contains("UpperWick", StringComparison.OrdinalIgnoreCase) => "윗꼬리 위험",
                var r when r.Contains("UpperBand", StringComparison.OrdinalIgnoreCase) => "BB 상단 과열",
                var r when r.Contains("Chasing", StringComparison.OrdinalIgnoreCase) => "추격 진입 차단",
                var r when r.Contains("MLNET", StringComparison.OrdinalIgnoreCase) || r.Contains("MLNET_Reject", StringComparison.OrdinalIgnoreCase) => "ML 신뢰도 미달",
                var r when r.Contains("Transformer", StringComparison.OrdinalIgnoreCase) => "TF 흐름 미달",
                var r when r.Contains("Pumping_Threshold", StringComparison.OrdinalIgnoreCase) => "PUMP 임계치 미달",
                var r when r.Contains("Major_Threshold", StringComparison.OrdinalIgnoreCase) => "메이저 임계치 미달",
                var r when r.Contains("Elliott", StringComparison.OrdinalIgnoreCase) || r.Contains("Rule_Violation", StringComparison.OrdinalIgnoreCase) => "규칙 위반(엘리엇/피보)",
                _ => string.IsNullOrWhiteSpace(text) ? "기타" : text[..Math.Min(text.Length, 40)]
            };
        }

        private static string NormalizeAiGateAllowedReason(string reason)
        {
            string text = reason ?? string.Empty;

            if (HasPositiveFibBonus(text))
                return "피보 지지+리버설 가점 통과";

            return text switch
            {
                var r when r.Contains("SuperTrend", StringComparison.OrdinalIgnoreCase) => "강추세 통과",
                var r when r.Contains("Major", StringComparison.OrdinalIgnoreCase) => "메이저 임계 통과",
                var r when r.Contains("Pumping", StringComparison.OrdinalIgnoreCase) => "펌핑 임계 통과",
                var r when r.Contains("Transformer", StringComparison.OrdinalIgnoreCase) => "TF 흐름 통과",
                var r when r.Contains("MLNET", StringComparison.OrdinalIgnoreCase) => "ML 신뢰 통과",
                _ => "게이트 통과"
            };
        }

        private static bool HasPositiveFibBonus(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            const string token = "FibBonus=";
            int start = reason.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return false;

            start += token.Length;
            if (start >= reason.Length)
                return false;

            int end = start;
            while (end < reason.Length)
            {
                char ch = reason[end];
                if (!char.IsDigit(ch) && ch != '.' && ch != '-')
                    break;
                end++;
            }

            if (end <= start)
                return false;

            string num = reason[start..end];
            return float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0f;
        }

        private static string NormalizeDecisionBucket(string decision)
        {
            if (string.Equals(decision, "LONG", StringComparison.OrdinalIgnoreCase))
                return "LONG";

            if (string.Equals(decision, "SHORT", StringComparison.OrdinalIgnoreCase))
                return "SHORT";

            return "OTHER";
        }

        private static string FormatCoinReasonList(IReadOnlyDictionary<string, string> symbolReasons, int maxCount)
        {
            var normalized = symbolReasons
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .Select(kv => new
                {
                    Symbol = kv.Key.Trim().ToUpperInvariant(),
                    Reason = string.IsNullOrWhiteSpace(kv.Value) ? "기타" : kv.Value.Trim()
                })
                .OrderBy(x => x.Symbol)
                .ToList();

            if (normalized.Count == 0)
                return "없음";

            int takeCount = Math.Max(1, maxCount);
            string listText = string.Join(", ", normalized
                .Take(takeCount)
                .Select(x => $"{x.Symbol}({x.Reason})"));
            int remain = normalized.Count - takeCount;

            return remain > 0
                ? $"{listText} 외 {remain}개"
                : listText;
        }

        private static string FormatTopReasons(IReadOnlyDictionary<string, int> reasons, int takeCount)
        {
            if (reasons.Count == 0)
                return "없음";

            return string.Join("\n", reasons
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, takeCount))
                .Select((kv, index) => $"{index + 1}. {kv.Key}: {kv.Value}건"));
        }

        // 
        // [Smart Target] 진입 / 본절 / 트레일링 마일스톤 텔레그램 알림
        // 

        /// <summary>
        /// 진입 성공 시 공통 텔레그램 1건 발송 (Smart TP/SL 포함 가능)
        /// </summary>
        public async Task SendEntrySuccessAlertAsync(
            string symbol, string direction, decimal entryPrice,
            decimal stopLoss, decimal takeProfit, string source,
            decimal marginUsdt, int leverage,
            decimal? smartSl = null, decimal? smartTp = null, double? atr = null)
        {
            try
            {
                string dir = string.Equals(direction, "LONG", StringComparison.OrdinalIgnoreCase) ? "LONG" : "SHORT";
                var lines = new List<string>
                {
                    $"✅ *[진입 완료]* `{symbol}` {dir}",
                    string.Empty,
                    $"진입가: `{entryPrice:F4}`",
                    $"전략: `{source}`",
                    $"증거금: `{marginUsdt:F2} USDT` | 레버리지: `{leverage}x`"
                };

                if (stopLoss > 0)
                    lines.Add($"기본 SL: `{stopLoss:F4}`");

                if (takeProfit > 0)
                    lines.Add($"기본 TP: `{takeProfit:F4}`");

                if (smartSl.HasValue && smartTp.HasValue && smartSl.Value > 0 && smartTp.Value > 0)
                {
                    double slPct = entryPrice > 0 ? (double)Math.Abs(smartSl.Value - entryPrice) / (double)entryPrice * 100 : 0;
                    double tpPct = entryPrice > 0 ? (double)Math.Abs(smartTp.Value - entryPrice) / (double)entryPrice * 100 : 0;
                    double slRoe = slPct * leverage;
                    double tpRoe = tpPct * leverage;

                    lines.Add(string.Empty);
                    lines.Add("📐 *Smart Target*");
                    lines.Add($"SL: `{smartSl.Value:F4}` ({slPct:F2}% | ROE -{slRoe:F0}%)");
                    lines.Add($"TP: `{smartTp.Value:F4}` ({tpPct:F2}% | ROE +{tpRoe:F0}%)");
                    if (atr.HasValue && atr.Value > 0)
                        lines.Add($"ATR(14): `{atr.Value:F4}`");
                }

                lines.Add(string.Empty);
                lines.Add($"시각: {DateTime.Now:HH:mm:ss}");

                await SendMessageAsync(string.Join("\n", lines), TelegramMessageType.Entry);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($" [Telegram][Entry] 발송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// ROE 10% 본절 전환 발생 시 알림 (무음)
        /// </summary>
        public async Task SendBreakEvenReachedAsync(string symbol, decimal newSL)
        {
            try
            {
                string msg =
                    $" *[본절 전환]* `{symbol}`\n" +
                    $"\n" +
                    $" ROE 10% 돌파! 손절선을 본절가로 이동\n" +
                    $" 새 SL: `{newSL:F4}`\n" +
                    $" 이제부터는 무적 매매! 져도 본전\n" +
                    $" {DateTime.Now:HH:mm:ss}";

                await SendInternalAsync($"[TradingBot]\n{msg}", true, "BreakEven", TelegramMessageType.Entry);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($" [Telegram][BreakEven] 발송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 트레일링 스탑 ROE 마일스톤 도달 알림
        /// ROE 20%+는 소리 알림, 그 미만은 무음
        /// </summary>
        public async Task SendTrailingMilestoneAsync(
            string symbol, decimal newStop, double roe, string label)
        {
            try
            {
                bool isSound = roe >= 20;
                string msg =
                    $"{label} `{symbol}`\n" +
                    $"\n" +
                    $" 현재 ROE: `{roe:F1}%` (레버리지 20)\n" +
                    $" 방어선(ATR 트레일): `{newStop:F4}`\n" +
                    $" {DateTime.Now:HH:mm:ss}";

                await SendInternalAsync($"[TradingBot]\n*[ATR 트레일링 마일스톤]*\n\n{msg}", !isSound, "Trailing", TelegramMessageType.Entry);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($" [Telegram][Trailing] 발송 실패: {ex.Message}");
            }
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
                foreach (var action in actions.Take(5))
                {
                    message += $"  • {action}\n";
                }
                if (actions.Count > 5)
                {
                    message += $"  ... 외 {actions.Count - 5}개\n";
                }
            }

            message += $"\n⏰ 완료 시간: {DateTime.Now:HH:mm:ss}";
            await SendMessageAsync(message, TelegramMessageType.Alert);
        }
    }
}


