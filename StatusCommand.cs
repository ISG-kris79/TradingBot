using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TradingBot.TelegramCommands
{
    public class StatusCommand : ITelegramCommand
    {
        public string Name => "/status";
        public string Description => "현재 봇의 상태와 보유 포지션을 확인합니다.";

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            string response = "⚠️ 봇 엔진이 정지 상태이거나 응답할 수 없습니다.";

            if (TelegramService.Instance.OnRequestStatus != null)
            {
                response = TelegramService.Instance.OnRequestStatus.Invoke();
            }

            await botClient.SendMessage(chatId: message.Chat.Id, text: response, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        }
    }
}