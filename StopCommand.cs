using Telegram.Bot;
using Telegram.Bot.Types;

namespace TradingBot.TelegramCommands
{
    public class StopCommand : ITelegramCommand
    {
        public string Name => "/stop";
        public string Description => "봇을 긴급 정지합니다.";

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            if (TelegramService.Instance.OnRequestStop != null)
            {
                TelegramService.Instance.OnRequestStop.Invoke();
                await botClient.SendMessage(chatId: message.Chat.Id, text: "🛑 텔레그램 명령어로 봇을 긴급 정지했습니다.", cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendMessage(chatId: message.Chat.Id, text: "⚠️ 봇이 실행 중이 아니거나 이미 정지되었습니다.", cancellationToken: cancellationToken);
            }
        }
    }
}