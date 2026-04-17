using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.10.18] OCO 브라켓 주문 관리 — 진입 후 SL/TP/Trailing 일괄 등록 및 취소
    /// PositionMonitorService의 bot-side 주문 실행을 대체하며,
    /// 거래소에 등록된 주문이 청산 역할을 담당.
    /// </summary>
    public class OrderManager
    {
        private readonly IExchangeService _exchangeService;

        // 심볼별 브라켓 등록 여부 추적 (CancelAllOrders 중복 호출 방지)
        private readonly ConcurrentDictionary<string, DateTime> _activeBrackets = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? OnLog;

        public OrderManager(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        /// <summary>
        /// 진입 성공 후 브라켓 주문 그룹 등록.
        /// ExecuteFullEntryWithAllOrdersAsync 성공 직후 호출.
        /// </summary>
        public void RegisterBracket(string symbol)
        {
            _activeBrackets[symbol] = DateTime.Now;
            OnLog?.Invoke($"📋 [OrderManager] {symbol} 브라켓 주문 등록 (SL+TP+Trailing on exchange)");
        }

        /// <summary>
        /// 포지션 청산 감지 시 잔여 주문(SL 또는 TP 중 미체결된 것) 일괄 취소.
        /// OCO 동작: 청산 주문 하나가 체결되면 나머지를 취소.
        /// </summary>
        public async Task CancelBracketAsync(string symbol, CancellationToken ct = default)
        {
            if (!_activeBrackets.TryRemove(symbol, out _))
                return; // 이미 취소됨 또는 미등록

            try
            {
                OnLog?.Invoke($"🗑️ [OrderManager] {symbol} 잔여 SL/TP/Trailing 주문 일괄 취소");
                await _exchangeService.CancelAllOrdersAsync(symbol, ct);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OrderManager] {symbol} 주문 취소 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 브라켓 등록 여부 확인
        /// </summary>
        public bool HasActiveBracket(string symbol)
            => _activeBrackets.ContainsKey(symbol);

        /// <summary>
        /// 현재 등록된 브라켓 심볼 목록
        /// </summary>
        public IEnumerable<string> ActiveSymbols => _activeBrackets.Keys;

        /// <summary>
        /// 봇 종료 시 모든 브라켓 취소
        /// </summary>
        public async Task CancelAllAsync(CancellationToken ct = default)
        {
            var symbols = _activeBrackets.Keys.ToArray();
            foreach (var sym in symbols)
                await CancelBracketAsync(sym, ct);
        }
    }
}
