using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [Fail-safe] API 연결 끊김 및 슬리피지 감지 → 긴급 포지션 정리
    ///
    /// 기능:
    /// 1. API 하트비트 모니터링 (30초 무응답 → 경고, 60초 → 긴급 청산)
    /// 2. 슬리피지 감지 (체결가 vs 요청가 1% 이상 차이 → 알림)
    /// 3. 거래소 잔고 급감 감지 (잔고 50% 이상 감소 → 전체 청산)
    /// </summary>
    public class FailSafeGuardService
    {
        private DateTime _lastApiResponseTime = DateTime.Now;
        private decimal _lastKnownBalance;
        private readonly ConcurrentDictionary<string, DateTime> _symbolLastUpdate = new();

        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;
        public event Action<string>? OnEmergencyClose;  // 긴급 청산 요청 (symbol)
        public event Action? OnEmergencyCloseAll;         // 전체 긴급 청산

        private const int WarningTimeoutSeconds = 30;
        private const int EmergencyTimeoutSeconds = 60;
        private const decimal SlippageThresholdPct = 1.0m;
        private const decimal BalanceDropThresholdPct = 50.0m;

        /// <summary>API 응답 수신 시 호출 (하트비트)</summary>
        public void RecordApiResponse()
        {
            _lastApiResponseTime = DateTime.Now;
        }

        /// <summary>심볼별 시세 수신 시 호출</summary>
        public void RecordTickerUpdate(string symbol)
        {
            _symbolLastUpdate[symbol] = DateTime.Now;
        }

        /// <summary>잔고 업데이트</summary>
        public void UpdateBalance(decimal currentBalance)
        {
            if (_lastKnownBalance > 0 && currentBalance > 0)
            {
                decimal dropPct = (_lastKnownBalance - currentBalance) / _lastKnownBalance * 100m;
                if (dropPct >= BalanceDropThresholdPct)
                {
                    OnAlert?.Invoke($"🚨 [FAIL-SAFE] 잔고 급감 감지! {_lastKnownBalance:F2} → {currentBalance:F2} ({dropPct:F1}% 감소)");
                    OnEmergencyCloseAll?.Invoke();
                }
            }
            _lastKnownBalance = currentBalance;
        }

        /// <summary>슬리피지 체크 (주문 체결 후 호출)</summary>
        public void CheckSlippage(string symbol, decimal requestedPrice, decimal filledPrice)
        {
            if (requestedPrice <= 0 || filledPrice <= 0) return;

            decimal slippagePct = Math.Abs(filledPrice - requestedPrice) / requestedPrice * 100m;
            if (slippagePct >= SlippageThresholdPct)
            {
                OnAlert?.Invoke($"⚠️ [SLIPPAGE] {symbol} 슬리피지 {slippagePct:F2}% 감지 | 요청={requestedPrice:F4} 체결={filledPrice:F4}");
            }
        }

        /// <summary>주기적 헬스체크 (메인 루프에서 1초마다 호출)</summary>
        public void HealthCheck()
        {
            var elapsed = (DateTime.Now - _lastApiResponseTime).TotalSeconds;

            if (elapsed >= EmergencyTimeoutSeconds)
            {
                OnAlert?.Invoke($"🚨 [FAIL-SAFE] API 무응답 {elapsed:F0}초 — 긴급 전체 청산 요청!");
                OnEmergencyCloseAll?.Invoke();
                _lastApiResponseTime = DateTime.Now; // 무한 반복 방지
            }
            else if (elapsed >= WarningTimeoutSeconds)
            {
                OnLog?.Invoke($"⚠️ [FAIL-SAFE] API 응답 지연 {elapsed:F0}초 — 모니터링 중");
            }
        }
    }
}
