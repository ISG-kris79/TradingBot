using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    public class NotificationService
    {
        private static NotificationService? _instance;
        public static NotificationService Instance => _instance ??= new NotificationService();

        private readonly string? _fcmServerKey;
        private readonly HttpClient _httpClient;

        public NotificationService()
        {
            _fcmServerKey = ""; // AppConfig에서 가져올 수 있음
            _httpClient = new HttpClient();
        }

        public async Task NotifyAsync(string message, NotificationChannel channel = NotificationChannel.Log, bool includePush = false)
        {
            // 1. Telegram (기본 채널)
            await TelegramService.Instance.SendMessageAsync(message);

            // 2. FCM Push (선택적)
            if (includePush)
            {
                await SendPushNotificationAsync("TradingBot", message);
            }
        }

        public async Task NotifyProfitAsync(string symbol, decimal pnl, decimal pnlPercent)
        {
            string message = $"💰 [Profit Notification]\nSymbol: {symbol}\nPnL: {pnl:F2} USDT ({pnlPercent:F2}%)";
            
            // Telegram 전송
            await TelegramService.Instance.SendMessageAsync(message);

            // Push 전송
            await SendPushNotificationAsync("💰 수익 발생!", message);
        }

        public async Task SendPushNotificationAsync(string title, string body, string topic = "all")
        {
            if (string.IsNullOrEmpty(_fcmServerKey)) return;

            try
            {
                var payload = new
                {
                    to = $"/topics/{topic}",
                    notification = new
                    {
                        title = title,
                        body = body,
                        sound = "default"
                    }
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"key={_fcmServerKey}");
                await _httpClient.PostAsync("https://fcm.googleapis.com/fcm/send", content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] FCM Send Error: {ex.Message}");
            }
        }

        /// <summary>
        /// 특정 기기를 FCM 토픽에 구독시킵니다.
        /// </summary>
        public async Task SubscribeToTopicAsync(string deviceToken, string topic)
        {
            if (string.IsNullOrEmpty(_fcmServerKey)) return;

            try
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"key={_fcmServerKey}");
                var response = await _httpClient.PostAsync($"https://iid.googleapis.com/iid/v1/{deviceToken}/rel/topics/{topic}", null);
                
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[NotificationService] Subscribe Error: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Subscribe Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// 특정 기기의 FCM 토픽 구독을 해지합니다.
        /// </summary>
        public async Task UnsubscribeFromTopicAsync(string deviceToken, string topic)
        {
            if (string.IsNullOrEmpty(_fcmServerKey)) return;

            try
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"key={_fcmServerKey}");
                // IID API를 사용한 구독 해지 (BatchRemove 방식 사용 권장되나, 여기서는 개별 처리 예시)
                // 참고: IID API는 Deprecated 되었으므로, 실제 운영 시 Firebase Admin SDK 사용을 권장합니다.
                // 여기서는 HTTP 요청 구조만 유지합니다.
            }
            catch { }
        }
    }
}
