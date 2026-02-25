using Telegram.Bot;
using Telegram.Bot.Types;
using TradingBot.Services;

namespace TradingBot.TelegramCommands
{
    public class TestCommand : ITelegramCommand
    {
        public string Name => "/test";
        public string Description => "내부 단위 테스트를 실행합니다.";

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            await botClient.SendMessage(chatId: message.Chat.Id, text: "🧪 단위 테스트를 시작합니다...", cancellationToken: cancellationToken);
            
            try 
            {
                // TradingEngineTests.RunAll(); // TODO: TradingEngineTests 클래스가 존재하지 않음
                await botClient.SendMessage(chatId: message.Chat.Id, text: "✅ 테스트 기능이 비활성화되어 있습니다.", cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                await botClient.SendMessage(chatId: message.Chat.Id, text: $"❌ 테스트 실행 중 오류: {ex.Message}", cancellationToken: cancellationToken);
            }
        }
    }
}