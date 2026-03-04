using Telegram.Bot;
using Telegram.Bot.Types;

namespace TradingBot.TelegramCommands
{
    public interface ITelegramCommand
    {
        string Name { get; }
        string Description { get; }
        Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken);
    }
}