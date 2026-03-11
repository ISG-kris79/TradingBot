using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.IO;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<string, DateTime> _aiGateLastSentAtUtc = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan AiGatePerSymbolCooldown = TimeSpan.FromMinutes(1);
        private readonly object _aiGateSummaryLock = new();
        private AiGateSummaryWindow _aiGateSummaryWindow = new();
        public Func<string>? OnRequestStatus { get; set; } // 상태 요청 시 호출될 콜백
        public Action? OnRequestStop { get; set; } // [추가] 정지 요청 시 호출될 콜백
        public Func<CancellationToken, Task<string>>? OnRequestTrain { get; set; } // [추가] 수동 학습 요청 콜백
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
            public Dictionary<string, int> BlockReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
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
            _commands[statusCmd.Name] = statusCmd;
            _commands[stopCmd.Name] = stopCmd;
            _commands[trainCmd.Name] = trainCmd;
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

        private async Task SendInternalAsync(string text, bool disableNotification = false, string scope = "General")
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

        public async Task SendMessageAsync(string message)
        {
            await SendInternalAsync($"[TradingBot]\n{message}", false, "General");
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

        // ─────────────────────────────────────────────────────────────────────
        // [AI 관제탑] AI Gate PASS/BLOCK 15분 집계 텔레그램 알림
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AI 이중 게이트(TF Brain + ML Filter) 결과를 15분 요약용으로 집계합니다.
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
                }
                else
                {
                    summary.BlockedCount++;
                    string blockReason = NormalizeAiGateBlockReason(reason);
                    if (summary.BlockReasons.TryGetValue(blockReason, out int currentCount))
                        summary.BlockReasons[blockReason] = currentCount + 1;
                    else
                        summary.BlockReasons[blockReason] = 1;
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
            if (snapshot.TotalCount == 0)
            {
                body = $"🕒 구간: {snapshot.WindowStartLocal:HH:mm} ~ {windowEndLocal:HH:mm}\n" +
                       "📭 최근 15분 동안 AI 게이트 판정이 없었습니다.";
            }
            else
            {
                float avgMl = snapshot.TotalCount > 0 ? snapshot.MlConfidenceSum / snapshot.TotalCount : 0f;
                float avgTf = snapshot.TotalCount > 0 ? snapshot.TfConfidenceSum / snapshot.TotalCount : 0f;
                string topReasons = snapshot.BlockReasons.Count == 0
                    ? "없음"
                    : string.Join("\n", snapshot.BlockReasons
                        .OrderByDescending(kv => kv.Value)
                        .Take(3)
                        .Select((kv, index) => $"{index + 1}. {kv.Key}: {kv.Value}건"));

                body = $"🕒 구간: {snapshot.WindowStartLocal:HH:mm} ~ {windowEndLocal:HH:mm}\n" +
                       $"📊 총 판정: {snapshot.TotalCount}건\n" +
                       $"✅ 승인: {snapshot.AllowedCount}건 (확신 {snapshot.StrongAllowedCount}건)\n" +
                       $"⛔ 차단: {snapshot.BlockedCount}건\n" +
                       $"🟢 LONG: {snapshot.LongCount}건 │ 🔴 SHORT: {snapshot.ShortCount}건\n" +
                       $"🏆 메이저: {snapshot.MajorCount} │ 🚀 펌핑: {snapshot.PumpingCount} │ 📊 일반: {snapshot.NormalCount}\n" +
                       $"🤖 평균 ML: {avgMl:P1} │ 🧠 평균 TF: {avgTf:P1}\n\n" +
                       $"📌 차단 TOP\n{topReasons}";
            }

            await SendInternalAsync($"[TradingBot]\n*[AI 관제탑 15분 요약]*\n\n{body}", true, "AI관제탑");
        }

        private static float SanitizeFinite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private static string NormalizeAiGateBlockReason(string reason)
        {
            string text = reason ?? string.Empty;
            return text switch
            {
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

        // 
        // [Smart Target] 진입 / 본절 / 트레일링 마일스톤 텔레그램 알림
        // 

        /// <summary>
        /// 진입 직후 ATR 기반 Smart TP/SL 요약 발송
        /// </summary>
        public async Task SendSmartTargetEntryAlertAsync(
            string symbol, string direction, decimal entryPrice,
            decimal sl, decimal tp, double atr)
        {
            try
            {
                string dir = direction == "LONG" ? " LONG" : " SHORT";
                double slPct = entryPrice > 0 ? (double)Math.Abs(sl - entryPrice) / (double)entryPrice * 100 : 0;
                double tpPct = entryPrice > 0 ? (double)Math.Abs(tp - entryPrice) / (double)entryPrice * 100 : 0;
                double slRoe = slPct * 20;
                double tpRoe = tpPct * 20;

                string body =
                    $" *[스마트 타겟 설정]* {dir} `{symbol}`\n" +
                    $"\n" +
                    $" 진입가: `{entryPrice:F4}`\n" +
                    $" SL: `{sl:F4}` ({slPct:F2}% | ROE -{slRoe:F0}%)\n" +
                    $" TP: `{tp:F4}` ({tpPct:F2}% | ROE +{tpRoe:F0}%)\n" +
                    $" ATR(14): `{atr:F4}` | 레버리지: 20\n" +
                    $"\n" +
                    $" 손익비 1:2 | 본절 전환: ROE 10%\n" +
                    $" {DateTime.Now:HH:mm:ss}";

                await SendMessageAsync(body);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($" [Telegram][SmartTarget] 발송 실패: {ex.Message}");
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

                await SendInternalAsync($"[TradingBot]\n{msg}", true, "BreakEven");
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

                await SendInternalAsync($"[TradingBot]\n*[ATR 트레일링 마일스톤]*\n\n{msg}", !isSound, "Trailing");
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
            await SendMessageAsync(message);
        }
    }
}


