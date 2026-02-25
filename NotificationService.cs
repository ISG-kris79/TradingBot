using System;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    // [Agent 4] 알림 서비스 고도화 (FCM 지원)
    public class NotificationService
    {
        private readonly string _fcmServerKey;

        public NotificationService(string fcmServerKey)
        {
            _fcmServerKey = fcmServerKey;
        }

        // 기존 텔레그램 알림 (TelegramService 위임)
        public async Task SendTelegramAsync(string message)
        {
            await TelegramService.Instance.SendMessageAsync(message);
        }

        // [New] 모바일 앱 푸시 알림 (Firebase Cloud Messaging)
        public async Task SendPushNotificationAsync(string title, string body)
        {
            if (string.IsNullOrEmpty(_fcmServerKey)) return;

            try
            {
                // 실제 FCM HTTP v1 API 호출 로직 구현 필요
                // 여기서는 로그만 남기고 실제 전송은 생략 (라이브러리 의존성 최소화)
                // 예: HttpClient로 https://fcm.googleapis.com/fcm/send 호출
                
                await Task.Run(() => 
                {
                    // Console.WriteLine($"[FCM Push] {title}: {body}");
                    // TODO: FirebaseAdmin SDK 또는 HTTP Request 구현
                });
            }
            catch (Exception ex)
            {
                // 푸시 실패가 봇 작동을 멈추면 안 됨
                Console.WriteLine($"FCM Send Error: {ex.Message}");
            }
        }
    }
}