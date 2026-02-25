using System.Media;
using System.Runtime.Versioning;

namespace TradingBot.Services
{
    [SupportedOSPlatform("windows")]
    public class SoundService
    {
        public void PlayAlert()
        {
            try
            {
                SystemSounds.Exclamation.Play();
            }
            catch { /* 오디오 장치 없음 무시 */ }
        }

        public void PlaySuccess()
        {
            // 윈도우 기본 알림음
            SystemSounds.Asterisk.Play();
        }
    }
}