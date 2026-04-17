using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using PositionInfo = TradingBot.Shared.Models.PositionInfo;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.10.18] 거래소 폴링 기반 포지션 동기화 서비스.
    /// PositionMonitorService의 bot-side SL/TP 실행을 대체:
    ///  - 10초마다 GetPositionsAsync 호출
    ///  - 거래소에서 사라진 포지션 = SL/TP/청산 체결 감지
    ///  - OnPositionClosed 이벤트 발생 → TradingEngine이 정리 수행
    ///  - OrderManager.CancelBracketAsync로 잔여 SL/TP 주문 취소 (OCO 효과)
    /// </summary>
    public class PositionSyncService
    {
        private readonly IExchangeService _exchangeService;
        private readonly Dictionary<string, PositionInfo> _activePositions;
        private readonly object _posLock;
        private readonly OrderManager _orderManager;

        // 폴링 간격 (기본 10초)
        private readonly int _pollIntervalMs;

        // 포지션이 거래소에서 사라진 후 청산가 조회 시도 횟수 제한
        private readonly ConcurrentDictionary<string, int> _closedRetryCount = new();

        private CancellationTokenSource? _cts;
        private Task? _pollingTask;

        // ─── 이벤트 ────────────────────────────────────────────────────────────────

        /// <summary>포지션이 거래소에서 감지되지 않아 청산 확인됨</summary>
        public event Action<string, decimal, decimal, bool, string>? OnPositionClosed;
        // (symbol, entryPrice, exitPrice, isProfit, reason)

        /// <summary>봇 로그 출력용</summary>
        public event Action<string>? OnLog;

        // ─── 생성자 ────────────────────────────────────────────────────────────────

        public PositionSyncService(
            IExchangeService exchangeService,
            Dictionary<string, PositionInfo> activePositions,
            object posLock,
            OrderManager orderManager,
            int pollIntervalMs = 10_000)
        {
            _exchangeService = exchangeService;
            _activePositions = activePositions;
            _posLock = posLock;
            _orderManager = orderManager;
            _pollIntervalMs = pollIntervalMs;
        }

        // ─── 시작 / 중지 ────────────────────────────────────────────────────────────

        public void Start(CancellationToken externalToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _pollingTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
            OnLog?.Invoke($"✅ [PositionSync] 폴링 시작 (간격={_pollIntervalMs / 1000}s)");
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_pollingTask != null)
            {
                try { await _pollingTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            OnLog?.Invoke("⏹️ [PositionSync] 폴링 중지");
        }

        // ─── 폴링 루프 ──────────────────────────────────────────────────────────────

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SyncOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [PositionSync] 폴링 오류: {ex.Message}");
                }

                try { await Task.Delay(_pollIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // ─── 단일 동기화 ────────────────────────────────────────────────────────────

        private async Task SyncOnceAsync(CancellationToken ct)
        {
            // 1. 거래소에서 현재 열린 포지션 조회
            List<PositionInfo>? exchangePositions = null;
            try
            {
                exchangePositions = await _exchangeService.GetPositionsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [PositionSync] GetPositions 실패: {ex.Message}");
                return;
            }

            var exchSet = new HashSet<string>(
                exchangePositions
                    ?.Where(p => Math.Abs(p.Quantity) > 0)
                    .Select(p => p.Symbol)
                    ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // 2. 로컬 활성 포지션 스냅샷 (OrderManager 등록 심볼만 감시)
            List<(string Symbol, PositionInfo Info)> localSnap;
            lock (_posLock)
            {
                localSnap = _activePositions
                    .Where(kv => kv.Value.IsOwnPosition && _orderManager.HasActiveBracket(kv.Key))
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }

            foreach (var (sym, pos) in localSnap)
            {
                if (exchSet.Contains(sym))
                {
                    // 포지션 확인됨 → 미확인 카운터 리셋
                    _closedRetryCount.TryRemove(sym, out _);
                    continue;
                }

                // [v5.10.22] 오판 방지 ① grace period: 진입 후 45초 이내 → 거래소 미반영 가능
                if (pos.EntryTime != default && (DateTime.Now - pos.EntryTime).TotalSeconds < 45)
                    continue;

                // [v5.10.22] 오판 방지 ② 연속 2회 미확인이어야 청산 처리 (10초 폴링 × 2 = 20초 추가 대기)
                int missCount = _closedRetryCount.AddOrUpdate(sym, 1, (_, c) => c + 1);
                if (missCount < 2)
                {
                    OnLog?.Invoke($"⚠️ [PositionSync] {sym} 거래소 미확인 {missCount}/2 → 재확인 대기");
                    continue;
                }
                _closedRetryCount.TryRemove(sym, out _);

                // 3. 거래소에서 사라짐 → 청산 감지
                await HandlePositionClosedAsync(sym, pos, ct).ConfigureAwait(false);
            }
        }

        // ─── 청산 처리 ──────────────────────────────────────────────────────────────

        private async Task HandlePositionClosedAsync(string symbol, PositionInfo pos, CancellationToken ct)
        {
            OnLog?.Invoke($"🔔 [PositionSync] {symbol} 거래소 포지션 사라짐 → 청산 확인");

            // 4. 실제 청산가 조회 (최근 체결 내역)
            decimal exitPrice = 0m;
            string closeReason = "SL/TP (exchange)";
            try
            {
                var lastTrade = await _exchangeService.GetLastTradeAsync(symbol, pos.EntryTime, ct)
                    .ConfigureAwait(false);
                if (lastTrade.HasValue)
                {
                    exitPrice = lastTrade.Value.exitPrice;
                    string tradeSide = lastTrade.Value.side;
                    bool isSellClose = string.Equals(tradeSide, "Sell", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(tradeSide, "SELL", StringComparison.OrdinalIgnoreCase);

                    // 방향으로 SL vs TP 추정
                    if (pos.IsLong && exitPrice < pos.EntryPrice * 1.001m)
                        closeReason = "SL (exchange)";
                    else if (!pos.IsLong && exitPrice > pos.EntryPrice * 0.999m)
                        closeReason = "SL (exchange)";
                    else
                        closeReason = "TP/Trailing (exchange)";
                }
            }
            catch { }

            if (exitPrice <= 0) exitPrice = pos.EntryPrice; // 조회 실패 시 fallback

            // 5. 잔여 SL/TP 주문 일괄 취소 (OCO 효과)
            await _orderManager.CancelBracketAsync(symbol, ct).ConfigureAwait(false);

            // 6. 로컬 포지션 제거
            lock (_posLock)
            {
                _activePositions.Remove(symbol);
            }

            // 7. PnL 계산
            decimal priceDiff = pos.IsLong ? (exitPrice - pos.EntryPrice) : (pos.EntryPrice - exitPrice);
            decimal pnlPct = pos.EntryPrice > 0
                ? (priceDiff / pos.EntryPrice) * pos.Leverage * 100m
                : 0m;
            bool isProfit = pnlPct > 0;

            OnLog?.Invoke($"{(isProfit ? "✅" : "❌")} [PositionSync] {symbol} 청산 확인 | reason={closeReason} entry={pos.EntryPrice:F4} exit={exitPrice:F4} ROE={pnlPct:+0.0;-0.0}%");

            // 8. TradingEngine 이벤트 발생
            OnPositionClosed?.Invoke(symbol, pos.EntryPrice, exitPrice, isProfit, closeReason);
        }
    }
}
