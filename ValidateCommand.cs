using Telegram.Bot;
using Telegram.Bot.Types;

namespace TradingBot.TelegramCommands
{
    /// <summary>
    /// [v5.19.12 Phase 1] 백테스트 검증 — 4 variant precision/recall/win-rate 측정
    /// 사용법: /validate (기본 7일, threshold=0.5)
    /// </summary>
    public class ValidateCommand : ITelegramCommand
    {
        public string Name => "/validate";
        public string Description => "AI 모델 백테스트 검증 (precision/recall/win-rate 측정)";

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            if (TelegramService.Instance.OnRequestValidate == null)
            {
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: "⚠️ 봇이 실행 중이 아니거나 검증 요청을 처리할 수 없습니다.",
                    cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "📊 백테스트 검증 시작 (7일치 차트, 4 variant 모두) — 1-3분 소요 예상...",
                cancellationToken: cancellationToken);

            string result;
            try
            {
                result = await TelegramService.Instance.OnRequestValidate.Invoke(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = "⚠️ 검증이 취소되었습니다.";
            }
            catch (Exception ex)
            {
                result = $"❌ 검증 중 오류: {ex.Message}";
            }

            // Telegram 메시지 길이 제한 (~4096) → 분할 전송
            const int chunkSize = 3500;
            for (int i = 0; i < result.Length; i += chunkSize)
            {
                int len = System.Math.Min(chunkSize, result.Length - i);
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: "```\n" + result.Substring(i, len) + "\n```",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    cancellationToken: cancellationToken);
            }
        }
    }
}
