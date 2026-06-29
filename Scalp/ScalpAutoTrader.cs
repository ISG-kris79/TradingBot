// 단타 자동매매 서비스 — ScalpEngine/PlanManager 신호로 진입+TP+SL 일괄 주문
// 기본 OFF. 테스트넷 권장. 기존 전략 파이프라인과 독립.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Services;

namespace TradingBot.Scalp;

public sealed class ScalpAutoTrader
{
    private readonly IExchangeService _ex;
    private readonly Action<string> _log;
    private readonly Func<bool> _enabled;
    private readonly string[] _symbols;
    private readonly string _interval;
    private readonly int _leverage;
    private readonly decimal _marginUsdt;
    private readonly int _maxPositions;
    private readonly Dictionary<string, DateTime> _cooldown = new();
    private CancellationTokenSource? _cts;

    public ScalpAutoTrader(IExchangeService ex, Action<string> log, Func<bool> enabled,
        string[] symbols, string interval, int leverage, decimal marginUsdt, int maxPositions)
    {
        _ex = ex; _log = log; _enabled = enabled;
        _symbols = symbols; _interval = interval; _leverage = leverage;
        _marginUsdt = marginUsdt; _maxPositions = maxPositions;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (_enabled()) await RunOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _log($"[ScalpAuto] 오류: {e.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var positions = await _ex.GetPositionsAsync(ct);
        int open = positions.Count(p => Math.Abs(p.Quantity) > 0);
        var ki = MapInterval(_interval);

        foreach (var sym in _symbols)
        {
            if (ct.IsCancellationRequested) break;
            if (open >= _maxPositions) break;
            if (positions.Any(p => string.Equals(p.Symbol, sym, StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Quantity) > 0)) continue;
            if (_cooldown.TryGetValue(sym, out var until) && DateTime.UtcNow < until) continue;

            List<IBinanceKline> kl;
            try { kl = await _ex.GetKlinesAsync(sym, ki, 500, ct); }
            catch { continue; }
            if (kl == null || kl.Count < 80) continue;

            var candles = kl.Select(x => new Candle(
                x.OpenTime, (double)x.OpenPrice, (double)x.HighPrice, (double)x.LowPrice, (double)x.ClosePrice, (double)x.Volume)).ToList();

            var r = PlanManager.Evaluate(sym, _interval, candles);
            if (r.Decision != ScalpDecision.Enter) continue;

            decimal price = (decimal)r.Price;
            if (price <= 0) continue;
            decimal qty = _marginUsdt * _leverage / price; // 스텝사이즈 반올림은 거래소 서비스가 처리
            if (qty <= 0) continue;

            string side = r.Side == TradeSide.Long ? "LONG" : "SHORT";
            _log($"[ScalpAuto] {sym} {side} 진입 시도 qty={qty:F4} entry={r.Entry:F4} SL={r.Stop:F4} TP={r.Target:F4} ({r.Trigger})");

            bool ok;
            try
            {
                ok = await _ex.ExecuteFullEntryWithAllOrdersAsync(
                    sym, side, qty, _leverage, (decimal)r.Stop, (decimal)r.Target,
                    partialProfitRoePercent: 40.0m, trailingStopCallbackRate: 0.01m, ct: ct);
            }
            catch (Exception e) { _log($"[ScalpAuto] {sym} 주문 예외: {e.Message}"); ok = false; }

            _log(ok ? $"[ScalpAuto] ✅ {sym} {side} 진입+TP/SL 등록 완료" : $"[ScalpAuto] ❌ {sym} 주문 실패");
            _cooldown[sym] = DateTime.UtcNow.AddMinutes(ok ? 30 : 5);
            if (ok) open++;
        }
    }

    private static KlineInterval MapInterval(string itv) => itv switch
    {
        "1m" => KlineInterval.OneMinute,
        "3m" => KlineInterval.ThreeMinutes,
        "5m" => KlineInterval.FiveMinutes,
        "15m" => KlineInterval.FifteenMinutes,
        "30m" => KlineInterval.ThirtyMinutes,
        "1h" => KlineInterval.OneHour,
        "4h" => KlineInterval.FourHour,
        _ => KlineInterval.FifteenMinutes
    };
}
