using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// [v5.35.0] ★돈치안 추세추종 엔진 (사용자 승인 2026-10-06) — 규칙/검증 근거는 Services/DonchianTrend.cs.
    ///   추적풀·틱 흐름과 무관하게 4h 봉 마감마다 고정 30종목을 직접 스캔한다(백테스트와 동일한 판단 시점).
    ///   진입은 IsEntryAllowed 게이트 → PlaceAndTrackEntryAsync 직행. ExecuteAutoOrderInner 의 재량 필터·
    ///   사이즈 배수(ATR/레짐/알트불장/리스크사이징)·부분익절·기본 TP 를 전부 건너뛴다 — 백테스트에 없던 것들이다.
    /// </summary>
    public partial class TradingEngine
    {
        private DateTime _donchianLastBarOpenUtc = DateTime.MinValue;
        private const int DonchianKlineLimit = 500;               // 4h × 500 ≈ 83일 — 트레일 재계산 창
        private static readonly TimeSpan DonchianEntryWindow = TimeSpan.FromMinutes(30);   // 마감 후 30분 지나면 그 봉 신호로는 진입 안 함

        private async Task RunDonchianLoopAsync(CancellationToken token)
        {
            await LoadDonchianHoldingsAsync();
            OnStatusLog?.Invoke($"📐 [DONCHIAN] 엔진 시작 | 4h N{DonchianTrend.Lookback} 돌파 · 초기손절 {DonchianTrend.InitStopAtr}ATR · 트레일 {DonchianTrend.TrailAtr}ATR · {DonchianTrend.Universe.Length}종목 · 보유 {DonchianTrend.Active.Count}건");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var barOpen = new DateTime(now.Year, now.Month, now.Day, now.Hour / 4 * 4, 0, 0, DateTimeKind.Utc);
                    // 새 4h 봉이 열린 뒤 15초(거래소 kline 확정 여유) — 봉당 1회
                    if (barOpen != _donchianLastBarOpenUtc && (now - barOpen).TotalSeconds >= 15)
                    {
                        bool allowEntry = now - barOpen <= DonchianEntryWindow;
                        _donchianLastBarOpenUtc = barOpen;
                        await DonchianOnBarCloseAsync(allowEntry, token);
                    }
                    await DonchianHousekeepingAsync(token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] 루프 오류: {ex.Message}"); }

                try { await Task.Delay(TimeSpan.FromSeconds(20), token); } catch { break; }
            }
        }

        /// <summary>재시작 복원 — ActivePosition 표에서 돈치안 소스 포지션을 되찾아 등록(레거시 감시가 붙기 전에).</summary>
        private async Task LoadDonchianHoldingsAsync()
        {
            try
            {
                int uid = AppConfig.CurrentUser?.Id ?? 0;
                if (_dbManager == null || uid <= 0) return;
                var rows = await _dbManager.GetActivePositionSourcesAsync(uid);
                foreach (var r in rows.Where(x => DonchianTrend.IsDonchianSource(x.SignalSource)))
                {
                    bool isLong = !string.Equals(r.Side, "SHORT", StringComparison.OrdinalIgnoreCase);
                    DonchianTrend.Active[r.Symbol] = new DonchianTrend.Holding { IsLong = isLong, EntryPrice = r.EntryPrice, EntryUtc = r.EntryUtc };
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(r.Symbol, out var p) && string.IsNullOrEmpty(p.EntrySignalSource))
                            p.EntrySignalSource = r.SignalSource;
                    }
                    OnStatusLog?.Invoke($"📐 [DONCHIAN] {r.Symbol} 보유 복원 | {(isLong ? "LONG" : "SHORT")} 진입 {r.EntryPrice} @ {r.EntryUtc:MM-dd HH:mm}Z");
                }
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] 보유 복원 실패: {ex.Message}"); }
        }

        private async Task DonchianOnBarCloseAsync(bool allowEntry, CancellationToken token)
        {
            int trailMoved = 0, longSig = 0, shortSig = 0, entered = 0;
            var nowUtc = DateTime.UtcNow;

            // ① 보유 포지션 트레일 갱신 (진입보다 먼저 — 슬롯/리스크 상태 확정)
            foreach (var sym in DonchianTrend.Active.Keys.ToList())
            {
                try { if (await DonchianUpdateTrailAsync(sym, nowUtc, token)) trailMoved++; }
                catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {sym} 트레일 갱신 오류: {ex.Message}"); }
            }

            // ② 신규 진입 스캔
            if (allowEntry)
            {
                foreach (var sym in DonchianTrend.Universe)
                {
                    if (token.IsCancellationRequested) break;
                    bool hasPos;
                    lock (_posLock) { hasPos = _activePositions.ContainsKey(sym); }
                    if (hasPos || DonchianTrend.Owns(sym)) continue;
                    try
                    {
                        var raw = await _exchangeService.GetKlinesAsync(sym, KlineInterval.FourHour, 120, token);
                        if (raw == null || raw.Count < DonchianTrend.Lookback + 20) continue;
                        var k = DonchianTrend.ClosedOnly(raw, nowUtc);
                        int i = k.Count - 1;
                        int dir = DonchianTrend.Signal(k, i);
                        if (dir == 0) continue;
                        if (dir > 0) longSig++; else shortSig++;
                        var atr = DonchianTrend.Atr(k);
                        if (atr[i] <= 0) continue;
                        if (await DonchianEnterAsync(sym, dir > 0, k[i].ClosePrice, atr[i], token)) entered++;
                    }
                    catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {sym} 스캔 오류: {ex.Message}"); }
                    await Task.Delay(150, token);   // REST 부하 분산
                }
            }

            OnStatusLog?.Invoke($"📐 [DONCHIAN] 4h 마감 처리 {_donchianLastBarOpenUtc.AddHours(9):MM-dd HH:mm}KST | 신호 롱{longSig}·숏{shortSig} → 진입 {entered} | 보유 {DonchianTrend.Active.Count} · 트레일 이동 {trailMoved}{(allowEntry ? "" : " | 진입창(30분) 경과 — 진입 스캔 생략")}");
        }

        private async Task<bool> DonchianEnterAsync(string symbol, bool isLong, decimal signalClose, decimal atr, CancellationToken token)
        {
            string source = isLong ? DonchianTrend.LongSource : DonchianTrend.ShortSource;
            if (!IsEntryAllowed(symbol, source, out var gateReason))
            {
                OnStatusLog?.Invoke($"⛔ [DONCHIAN] {symbol} {(isLong ? "롱" : "숏")} 신호 차단 | reason={gateReason}");
                return false;
            }

            // 1코인 1포지션 — 거래소 실잔량 확인 (메모리 desync 대비)
            try
            {
                var live = await _exchangeService.GetPositionsAsync(token);
                if (live?.Any(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Quantity) > 0m) == true)
                {
                    OnStatusLog?.Invoke($"⛔ [DONCHIAN] {symbol} 거래소에 이미 포지션 존재 → 진입 안 함");
                    return false;
                }
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {symbol} 거래소 포지션 조회 실패 → 이번 봉 진입 생략: {ex.Message}"); return false; }

            decimal price = signalClose;
            if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tk) && tk.LastPrice > 0) price = tk.LastPrice;
            decimal initStop = isLong ? price - DonchianTrend.InitStopAtr * atr : price + DonchianTrend.InitStopAtr * atr;
            if (initStop <= 0) return false;

            var s = _settings;
            if (s == null) return false;
            bool major = DonchianTrend.IsMajor(symbol);
            decimal margin = major ? (s.MajorMargin > 0 ? s.MajorMargin : s.DefaultMargin) : (s.PumpMargin > 0 ? s.PumpMargin : s.DefaultMargin);
            int lev = major ? (s.MajorLeverage > 0 ? s.MajorLeverage : s.DefaultLeverage) : (s.PumpLeverage > 0 ? s.PumpLeverage : s.DefaultLeverage);
            if (margin <= 0 || lev <= 0) { OnStatusLog?.Invoke($"⛔ [DONCHIAN] {symbol} 증거금/레버리지 설정 없음 → 진입 안 함"); return false; }

            string decision = isLong ? "LONG" : "SHORT";
            string flowTag = $"src={source} sym={symbol} side={decision}";
            var ctx = new EntryContext
            {
                Symbol = symbol,
                Decision = decision,
                CurrentPrice = price,
                Token = token,
                SignalSource = source,
                Mode = "TREND",
                CustomStopLossPrice = initStop,
                CustomTakeProfitPrice = 0m,      // 익절 없음 — 트레일만
                MarginUsdt = margin,
                Leverage = lev,
                IsPumpStrategy = !major,
                SizeMultiplier = 1.0m,
                FlowTag = flowTag,
                EntryLog = (stage, status, detail) =>
                {
                    string line = $"🧭 [ENTRY][{stage}][{status}] {flowTag} | {detail}";
                    OnStatusLog?.Invoke(line);
                    LoggerService.Info(line);
                },
            };

            // 레거시 감시가 붙지 않도록 체결 전에 소유 등록 → 실패 시 해제
            DonchianTrend.Active[symbol] = new DonchianTrend.Holding { IsLong = isLong, EntryPrice = price, EntryUtc = DateTime.UtcNow, CurrentStop = initStop };
            OnStatusLog?.Invoke($"✅ [DONCHIAN] {symbol} {(isLong ? "롱" : "숏")}진입 | 4h 종가 {signalClose} 가 직전 {DonchianTrend.Lookback}봉 {(isLong ? "최고가 돌파" : "최저가 이탈")} | 진입≈{price} 초기손절 {initStop:F6}({DonchianTrend.InitStopAtr}ATR) · 증거금 ${margin} × {lev}x");

            await PlaceAndTrackEntryAsync(ctx);

            bool filled;
            lock (_posLock) { filled = _activePositions.TryGetValue(symbol, out var p) && Math.Abs(p.Quantity) > 0m; }
            if (!filled)
            {
                DonchianTrend.Active.TryRemove(symbol, out _);
                OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {symbol} 체결 확인 실패 → 소유 해제");
                return false;
            }
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p) && DonchianTrend.Active.TryGetValue(symbol, out var h))
                {
                    h.EntryPrice = p.EntryPrice > 0 ? p.EntryPrice : price;
                    h.StopSynced = false;   // 다음 단계에서 SL 단독 등록
                }
            }
            await DonchianSyncStopAsync(symbol, force: true, token);
            return true;
        }

        /// <summary>트레일 재계산 → 개선됐거나 미동기화면 거래소 SL 재등록. 이미 손절선을 지난 가격이면 시장가 청산.</summary>
        private async Task<bool> DonchianUpdateTrailAsync(string symbol, DateTime nowUtc, CancellationToken token)
        {
            if (!DonchianTrend.Active.TryGetValue(symbol, out var h)) return false;
            bool hasPos; decimal qty = 0m;
            lock (_posLock) { hasPos = _activePositions.TryGetValue(symbol, out var p) && Math.Abs(p.Quantity) > 0m; if (hasPos) qty = Math.Abs(_activePositions[symbol].Quantity); }
            if (!hasPos) return false;

            var raw = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FourHour, DonchianKlineLimit, token);
            if (raw == null || raw.Count < DonchianTrend.AtrLen + 5) return false;
            var k = DonchianTrend.ClosedOnly(raw, nowUtc);
            var atr = DonchianTrend.Atr(k);
            decimal rec = DonchianTrend.RecomputeStop(k, atr, h.EntryUtc, h.IsLong, h.EntryPrice);
            decimal prev = h.CurrentStop;
            decimal stop = prev;
            if (rec > 0 && (stop <= 0 || (h.IsLong ? rec > stop : rec < stop))) stop = rec;
            if (stop <= 0)
            {
                if (h.NoStopAlerted) return false;
                h.NoStopAlerted = true;
                OnAlert?.Invoke($"🚨 [DONCHIAN] {symbol} 트레일 손절가 계산 불가(진입 {h.EntryUtc:MM-dd HH:mm}Z 가 4h {DonchianKlineLimit}봉 창 밖) — 수동 손절 확인 필요");
                return false;
            }
            bool moved = prev > 0 && stop != prev;
            h.CurrentStop = stop;

            decimal px = k.Count > 0 ? k[^1].ClosePrice : 0m;
            if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tk) && tk.LastPrice > 0) px = tk.LastPrice;
            if (px > 0 && (h.IsLong ? px <= stop : px >= stop))
            {
                // 백테스트: 다음 봉 시가가 손절선 밖이면 시가 청산 — 거래소는 즉시 발동 SL 을 거부(-2021)하므로 시장가로 대체
                OnStatusLog?.Invoke($"📐 [DONCHIAN] {symbol} 트레일 손절선 {stop:F6} 이미 이탈(현재 {px:F6}) → 시장가 청산");
                await _positionMonitor.ExecuteMarketClose(symbol, $"돈치안 트레일 {DonchianTrend.TrailAtr}ATR 이탈 {stop:F6}", token);
                return true;
            }

            if (moved || !h.StopSynced)
            {
                if (moved) OnStatusLog?.Invoke($"📐 [DONCHIAN] {symbol} 트레일 이동 {prev:F6} → {stop:F6} ({(h.IsLong ? "롱" : "숏")} · 4h 종가 ∓ {DonchianTrend.TrailAtr}ATR)");
                await DonchianSyncStopAsync(symbol, force: true, token, qty);
            }
            return moved;
        }

        private async Task DonchianSyncStopAsync(string symbol, bool force, CancellationToken token, decimal qty = 0m)
        {
            if (!DonchianTrend.Active.TryGetValue(symbol, out var h) || h.CurrentStop <= 0) return;
            if (!force && h.StopSynced) return;
            if (qty <= 0) lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) qty = Math.Abs(p.Quantity); }
            if (qty <= 0 || _orderLifecycle == null) return;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                string id = await _orderLifecycle.RegisterStopOnlyAsync(symbol, h.IsLong, qty, h.CurrentStop, token);
                if (!string.IsNullOrEmpty(id))
                {
                    h.StopSynced = true;
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var p)) { p.StopOrderId = id; p.StopLoss = h.CurrentStop; p.TakeProfit = 0; }
                    }
                    return;
                }
                try { await Task.Delay(1500 * attempt, token); } catch { return; }
            }
            OnAlert?.Invoke($"🚨 [DONCHIAN SL 실패] {symbol} 손절 {h.CurrentStop:F6} 등록 3회 실패 — 다음 4h 마감에 재시도, 수동 확인 필요");
        }

        /// <summary>청산된 포지션 정리 + 재시작 후 아직 SL 미동기화 보유분 동기화.</summary>
        private async Task DonchianHousekeepingAsync(CancellationToken token)
        {
            foreach (var kv in DonchianTrend.Active.ToList())
            {
                bool hasPos; string? src = null;
                lock (_posLock)
                {
                    hasPos = _activePositions.TryGetValue(kv.Key, out var p) && Math.Abs(p.Quantity) > 0m;
                    if (hasPos) { src = p!.EntrySignalSource; if (string.IsNullOrEmpty(src)) p.EntrySignalSource = kv.Value.IsLong ? DonchianTrend.LongSource : DonchianTrend.ShortSource; }
                }
                if (hasPos)
                {
                    kv.Value.MissingChecks = 0;
                    // 재시작 복원분은 포지션이 메모리에 올라온 직후 SL 을 바로 맞춘다(다음 4h 마감까지 기다리지 않음)
                    if (!kv.Value.StopSynced)
                    {
                        try { await DonchianUpdateTrailAsync(kv.Key, DateTime.UtcNow, token); }
                        catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {kv.Key} SL 동기화 오류: {ex.Message}"); }
                    }
                    continue;
                }
                // 진입 직후 체결 반영 전 공백·재시작 복원 지연을 감안해 6회(≈2분) 연속 부재 시 해제
                if (++kv.Value.MissingChecks >= 6)
                {
                    DonchianTrend.Active.TryRemove(kv.Key, out _);
                    OnStatusLog?.Invoke($"📐 [DONCHIAN] {kv.Key} 포지션 종료 확인 → 소유 해제");
                }
            }
        }
    }
}
