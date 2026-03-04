using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// TradingEngine을 독립적인 Worker Service로 실행하기 위한 래퍼
    /// Linux/Docker 환경에서 UI 없이 실행할 때 사용됩니다.
    /// </summary>
    public class TradingBotWorker : BackgroundService
    {
        private readonly TradingEngine _engine;

        public TradingBotWorker(TradingEngine engine)
        {
            _engine = engine;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 엔진 시작
            await _engine.StartScanningOptimizedAsync();

            // 종료 신호 대기 (서비스가 중지될 때까지 유지)
            await Task.Delay(-1, stoppingToken);

            // 엔진 정지
            _engine.StopEngine();
        }
    }
}
