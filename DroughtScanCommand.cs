using Telegram.Bot;
using Telegram.Bot.Types;

namespace TradingBot.TelegramCommands
{
    public class DroughtScanCommand : ITelegramCommand
    {
        public string Name => "/drought";
        public string Description => "드라이스펠 진단/복구 스캔을 즉시 1회 실행합니다.";

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            if (TelegramService.Instance.OnRequestDroughtScan == null)
            {
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: "⚠️ 봇이 실행 중이 아니거나 드라이스펠 점검 요청을 처리할 수 없습니다.",
                    cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "🔎 드라이스펠 진단/복구 스캔 요청을 접수했습니다. 결과는 엔진 로그와 알림으로 확인하세요.",
                cancellationToken: cancellationToken);

            string result;
            try
            {
                result = await TelegramService.Instance.OnRequestDroughtScan.Invoke(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = "⚠️ 드라이스펠 진단 요청이 취소되었습니다.";
            }
            catch (Exception ex)
            {
                result = $"❌ 드라이스펠 진단 요청 처리 중 오류: {ex.Message}";
            }

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: result,
                cancellationToken: cancellationToken);
        }
    }
}
