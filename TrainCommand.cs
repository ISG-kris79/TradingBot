using Telegram.Bot;
using Telegram.Bot.Types;

namespace TradingBot.TelegramCommands
{
    public class TrainCommand : ITelegramCommand
    {
        public string Name => "/train";
        public string Description => "ML.NET + Transformer 수동 초기 학습을 실행합니다.";

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            if (TelegramService.Instance.OnRequestTrain == null)
            {
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: "⚠️ 봇이 실행 중이 아니거나 학습 요청을 처리할 수 없습니다.",
                    cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "🧠 수동 초기 학습 요청을 접수했습니다. 진행 결과를 다시 알려드리겠습니다.",
                cancellationToken: cancellationToken);

            string result;
            try
            {
                result = await TelegramService.Instance.OnRequestTrain.Invoke(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = "⚠️ 수동 초기 학습이 취소되었습니다.";
            }
            catch (Exception ex)
            {
                result = $"❌ 수동 초기 학습 중 오류: {ex.Message}";
            }

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: result,
                cancellationToken: cancellationToken);
        }
    }
}
