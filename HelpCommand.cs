using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TradingBot.TelegramCommands
{
    public class HelpCommand : ITelegramCommand
    {
        public string Name => "/help";
        public string Description => "사용 가능한 명령어 목록을 보여줍니다.";
        private readonly IEnumerable<ITelegramCommand> _commands;

        public HelpCommand(IEnumerable<ITelegramCommand> commands)
        {
            _commands = commands;
        }

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🤖 *사용 가능한 명령어*");
            foreach (var cmd in _commands)
            {
                sb.AppendLine($"{cmd.Name} - {cmd.Description}");
            }

            await botClient.SendMessage(chatId: message.Chat.Id, text: sb.ToString(), parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        }
    }
}