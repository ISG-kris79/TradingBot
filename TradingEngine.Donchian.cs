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
    /// [v5.36.0] ★돈치안 2슬리브 엔진 (A=4h N100 · B=1d N55) — 규칙/검증 근거는 Services/DonchianTrend.cs.
    ///   추적풀·틱 흐름과 무관하게 봉 마감마다 고정 30종목을 직접 스캔한다(백테스트와 같은 판단 시점).
    ///   4h 마감마다 A 처리, UTC 00시(=KST 09시) 마감이면 B(일봉)도 처리.
    ///   진입은 IsEntryAllowed 게이트 → PlaceAndTrackEntryAsync 직행(재량 필터·사이즈 배수·레거시 TP 미적용).
    ///   거래소엔 SL(보유 전량) + 50% TP(미체결일 때만). 손절선은 마감마다 재계산해 유리할 때만 옮긴다.
    /// </summary>
    public partial class TradingEngine
    {
        private DateTime _donchianLastBarOpenUtc = DateTime.MinValue;
        private const int DonchianKlineLimit = 500;               // 4h 500봉 ≈ 83일 · 1d 500봉 ≈ 16개월 — EMA50 시드 영향 제거 + 트레일 재계산 창
        private static readonly TimeSpan DonchianEntryWindow = TimeSpan.FromMinutes(30);   // 마감 후 30분 지나면 그 봉 신호로는 진입 안 함
        private int DonchianUserId => AppConfig.CurrentUser?.Id ?? 0;

        private async Task RunDonchianLoopAsync(CancellationToken token)
        {
            await LoadDonchianHoldingsAsync();
            OnStatusLog?.Invoke($"📐 [DONCHIAN] 엔진 시작 | A=4h N{DonchianTrend.A.Lookback}·트레일{DonchianTrend.A.TrailAtr}·익절50%@{DonchianTrend.A.PartialTpAtr}ATR · B=1d N{DonchianTrend.B.Lookback}·트레일{DonchianTrend.B.TrailAtr}·익절50%@{DonchianTrend.B.PartialTpAtr}ATR · 손절후 {DonchianTrend.A.CooldownHours / 24}일 재진입금지 · 1건=설정의 {DonchianTrend.SizeFraction:P0} · {DonchianTrend.Universe.Length}종목 · 보유 {DonchianTrend.Active.Count}건");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var barOpen = new DateTime(now.Year, now.Month, now.Day, now.Hour / 4 * 4, 0, 0, DateTimeKind.Utc);
                    // 새 4h 봉이 열린 뒤 15초(거래소 kline 확정 여유) — 봉당 1회. 시작 직후 첫 회는 B 도 함께(보유분 트레일 동기화)
                    if (barOpen != _donchianLastBarOpenUtc && (now - barOpen).TotalSeconds >= 15)
                    {
                        bool first = _donchianLastBarOpenUtc == DateTime.MinValue;
                        bool allowEntry = now - barOpen <= DonchianEntryWindow;
                        _donchianLastBarOpenUtc = barOpen;
                        await DonchianOnBarCloseAsync(DonchianTrend.A, allowEntry, token);
                        if (barOpen.Hour == 0 || first)
                            await DonchianOnBarCloseAsync(DonchianTrend.B, allowEntry && barOpen.Hour == 0, token);
                    }
                    await DonchianHousekeepingAsync(token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] 루프 오류: {ex.Message}"); }

                try { await Task.Delay(TimeSpan.FromSeconds(20), token); } catch { break; }
            }
        }

        /// <summary>재시작 복원 — 상태 파일(처음 수량·50%익절·신호ATR·쿨다운) + ActivePosition 표(진입 소스)로 보유분을 되찾는다.</summary>
        private async Task LoadDonchianHoldingsAsync()
        {
            try
            {
                int uid = DonchianUserId;
                if (_dbManager == null || uid <= 0) return;
                var (saved, cools) = DonchianTrend.Load(uid);
                foreach (var c in cools) if (c.Value > DateTime.UtcNow) DonchianTrend.CooldownUntil[c.Key] = c.Value;
                var rows = await _dbManager.GetActivePositionSourcesAsync(uid);
                foreach (var r in rows.Where(x => DonchianTrend.IsDonchianSource(x.SignalSource)))
                {
                    bool isLong = !string.Equals(r.Side, "SHORT", StringComparison.OrdinalIgnoreCase);
                    var sl = DonchianTrend.SleeveOf(r.SignalSource)!;
                    var h = saved.TryGetValue(r.Symbol, out var sv) && sv.IsLong == isLong
                        ? sv
                        : new DonchianTrend.Holding { SleeveId = sl.Id, IsLong = isLong, EntryPrice = r.EntryPrice, EntryUtc = r.EntryUtc };
                    DonchianTrend.Active[r.Symbol] = h;
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(r.Symbol, out var p) && string.IsNullOrEmpty(p.EntrySignalSource))
                            p.EntrySignalSource = r.SignalSource;
                    }
                    OnStatusLog?.Invoke($"📐 [DONCHIAN] {r.Symbol} 보유 복원 | 슬리브 {h.SleeveId} · {(isLong ? "LONG" : "SHORT")} 진입 {h.EntryPrice} @ {h.EntryUtc:MM-dd HH:mm}Z · 50%익절 {(h.HalfDone ? "완료" : "대기")}");
                }
                DonchianTrend.Save(uid);
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] 보유 복원 실패: {ex.Message}"); }
        }

        private async Task DonchianOnBarCloseAsync(DonchianTrend.Sleeve sl, bool allowEntry, CancellationToken token)
        {
            int trailMoved = 0, longSig = 0, shortSig = 0, entered = 0, skipped = 0;
            var nowUtc = DateTime.UtcNow;

            // ① 이 슬리브 보유분 트레일 갱신 (진입보다 먼저)
            foreach (var kv in DonchianTrend.Active.Where(x => x.Value.SleeveId == sl.Id).ToList())
            {
                try { if (await DonchianUpdateTrailAsync(kv.Key, nowUtc, token)) trailMoved++; }
                catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {kv.Key} 트레일 갱신 오류: {ex.Message}"); }
            }

            // ② 신규 진입 스캔
            if (allowEntry)
            {
                foreach (var sym in DonchianTrend.Universe)
                {
                    if (token.IsCancellationRequested) break;
                    bool hasPos;
                    lock (_posLock) { hasPos = _activePositions.ContainsKey(sym); }
                    if (hasPos || DonchianTrend.Owns(sym)) continue;                      // 코인당 1포지션(원웨이)
                    try
                    {
                        var raw = await _exchangeService.GetKlinesAsync(sym, sl.Interval, DonchianKlineLimit, token);
                        if (raw == null || raw.Count < sl.Lookback + 20) continue;
                        var k = DonchianTrend.ClosedOnly(raw, nowUtc);
                        int i = k.Count - 1;
                        int dir = DonchianTrend.Signal(k, i, sl.Lookback);
                        if (dir == 0) continue;
                        if (dir > 0) longSig++; else shortSig++;
                        var atr = DonchianTrend.Atr(k);
                        if (atr[i] <= 0) continue;
                        var ema50 = DonchianTrend.Ema(k, DonchianTrend.EmaLen);
                        string side = dir > 0 ? "롱" : "숏";
                        if (DonchianTrend.IsChasing(k, i, atr, ema50, dir, out var ext))
                        { skipped++; OnStatusLog?.Invoke($"⛔ [DONCHIAN-{sl.Id}] {sym} {side} 제외 — 과열 추격 (EMA50 대비 {ext:F1}ATR ≥ {DonchianTrend.MaxChaseAtr})"); continue; }
                        if (DonchianTrend.InCooldown(sym, sl, k[i].OpenTime))
                        { skipped++; OnStatusLog?.Invoke($"⛔ [DONCHIAN-{sl.Id}] {sym} {side} 제외 — 손절 후 재진입 금지 중 (~{DonchianTrend.CooldownUntil[$"{sym}|{sl.Id}"].AddHours(9):MM-dd HH:mm} KST)"); continue; }
                        bool major = DonchianTrend.IsMajor(sym);
                        int used = DonchianTrend.Active.Count(x => x.Value.SleeveId == sl.Id && DonchianTrend.IsMajor(x.Key) == major);
                        if (used >= (major ? sl.MajorSlots : sl.AltSlots))
                        { skipped++; OnStatusLog?.Invoke($"⛔ [DONCHIAN-{sl.Id}] {sym} {side} 제외 — 슬롯 가득 ({(major ? "메이저" : "알트")} {used}/{(major ? sl.MajorSlots : sl.AltSlots)})"); continue; }
                        if (await DonchianEnterAsync(sl, sym, dir > 0, k[i].ClosePrice, atr[i], token)) entered++;
                    }
                    catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN-{sl.Id}] {sym} 스캔 오류: {ex.Message}"); }
                    await Task.Delay(150, token);   // REST 부하 분산
                }
            }

            OnStatusLog?.Invoke($"📐 [DONCHIAN-{sl.Id}] {(sl.Id == "A" ? "4h" : "1d")} 마감 처리 {_donchianLastBarOpenUtc.AddHours(9):MM-dd HH:mm}KST | 신호 롱{longSig}·숏{shortSig} → 진입 {entered} · 제외 {skipped} | 보유 {DonchianTrend.Active.Values.Count(h => h.SleeveId == sl.Id)} · 트레일 이동 {trailMoved}{(allowEntry ? "" : " | 진입 스캔 생략(진입창 경과 또는 일봉 미마감)")}");
        }

        private async Task<bool> DonchianEnterAsync(DonchianTrend.Sleeve sl, string symbol, bool isLong, decimal signalClose, decimal atr, CancellationToken token)
        {
            string source = isLong ? sl.LongSource : sl.ShortSource;
            if (!IsEntryAllowed(symbol, source, out var gateReason))
            {
                OnStatusLog?.Invoke($"⛔ [DONCHIAN-{sl.Id}] {symbol} {(isLong ? "롱" : "숏")} 신호 차단 | reason={gateReason}");
                return false;
            }

            // 1코인 1포지션 — 거래소 실잔량 확인 (메모리 desync 대비)
            try
            {
                var live = await _exchangeService.GetPositionsAsync(token);
                if (live?.Any(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Quantity) > 0m) == true)
                {
                    OnStatusLog?.Invoke($"⛔ [DONCHIAN-{sl.Id}] {symbol} 거래소에 이미 포지션 존재 → 진입 안 함");
                    return false;
                }
            }
            catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN-{sl.Id}] {symbol} 거래소 포지션 조회 실패 → 이번 봉 진입 생략: {ex.Message}"); return false; }

            decimal price = signalClose;
            if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tk) && tk.LastPrice > 0) price = tk.LastPrice;
            decimal initStop = isLong ? price - sl.InitStopAtr * atr : price + sl.InitStopAtr * atr;
            if (initStop <= 0) return false;

            var s = _settings;
            if (s == null) return false;
            bool major = DonchianTrend.IsMajor(symbol);
            decimal margin = major ? (s.MajorMargin > 0 ? s.MajorMargin : s.DefaultMargin) : (s.PumpMargin > 0 ? s.PumpMargin : s.DefaultMargin);
            int lev = major ? (s.MajorLeverage > 0 ? s.MajorLeverage : s.DefaultLeverage) : (s.PumpLeverage > 0 ? s.PumpLeverage : s.DefaultLeverage);
            if (margin <= 0 || lev <= 0) { OnStatusLog?.Invoke($"⛔ [DONCHIAN-{sl.Id}] {symbol} 증거금/레버리지 설정 없음 → 진입 안 함"); return false; }
            margin = Math.Round(margin * DonchianTrend.SizeFraction, 2);   // [v5.36.0] 검증 구성: 1건 = 설정의 절반

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
                CustomTakeProfitPrice = 0m,      // 레거시 TP 경로 미사용 — 50% 익절은 돈치안 엔진이 별도 등록
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
            DonchianTrend.Active[symbol] = new DonchianTrend.Holding
            {
                SleeveId = sl.Id, IsLong = isLong, EntryPrice = price, EntryUtc = DateTime.UtcNow, CurrentStop = initStop, SignalAtr = atr
            };
            var hPre = DonchianTrend.Active[symbol];
            OnStatusLog?.Invoke($"✅ [DONCHIAN-{sl.Id}] {symbol} {(isLong ? "롱" : "숏")}진입 | {(sl.Id == "A" ? "4h" : "1d")} 종가 {signalClose} 가 직전 {sl.Lookback}봉 {(isLong ? "최고가 돌파" : "최저가 이탈")} | 진입≈{price} · 초기손절 {initStop:F6}({sl.InitStopAtr}ATR) · 50%익절 {hPre.TpPrice:F6}({sl.PartialTpAtr}ATR) · 증거금 ${margin} × {lev}x");

            await PlaceAndTrackEntryAsync(ctx);

            bool filled; decimal qty = 0m, fillPx = 0m;
            lock (_posLock)
            {
                filled = _activePositions.TryGetValue(symbol, out var p) && Math.Abs(p.Quantity) > 0m;
                if (filled) { qty = Math.Abs(p!.Quantity); fillPx = p.EntryPrice; }
            }
            if (!filled)
            {
                DonchianTrend.Active.TryRemove(symbol, out _);
                OnStatusLog?.Invoke($"⚠️ [DONCHIAN-{sl.Id}] {symbol} 체결 확인 실패 → 소유 해제");
                return false;
            }
            if (DonchianTrend.Active.TryGetValue(symbol, out var h))
            {
                h.EntryPrice = fillPx > 0 ? fillPx : price;
                h.CurrentStop = isLong ? h.EntryPrice - sl.InitStopAtr * atr : h.EntryPrice + sl.InitStopAtr * atr;   // 실체결가 기준(백테스트 동일)
                h.InitQty = qty;
                h.StopSynced = false;
            }
            DonchianTrend.Save(DonchianUserId);
            await DonchianSyncOrdersAsync(symbol, token);
            return true;
        }

        /// <summary>트레일 재계산 → 개선됐거나 미동기화면 거래소 주문 재등록. 이미 손절선을 지난 가격이면 시장가 청산.</summary>
        private async Task<bool> DonchianUpdateTrailAsync(string symbol, DateTime nowUtc, CancellationToken token)
        {
            if (!DonchianTrend.Active.TryGetValue(symbol, out var h)) return false;
            var sl = h.Sleeve;
            bool hasPos;
            lock (_posLock) { hasPos = _activePositions.TryGetValue(symbol, out var p) && Math.Abs(p.Quantity) > 0m; }
            if (!hasPos) return false;

            var raw = await _exchangeService.GetKlinesAsync(symbol, sl.Interval, DonchianKlineLimit, token);
            if (raw == null || raw.Count < DonchianTrend.AtrLen + 5) return false;
            var k = DonchianTrend.ClosedOnly(raw, nowUtc);
            var atr = DonchianTrend.Atr(k);
            if (h.SignalAtr <= 0)
            {
                int sig = DonchianTrend.SignalBarIndex(k, h.EntryUtc);
                if (sig >= DonchianTrend.AtrLen && atr[sig] > 0) { h.SignalAtr = atr[sig]; DonchianTrend.Save(DonchianUserId); }
            }
            decimal rec = DonchianTrend.RecomputeStop(k, atr, h.EntryUtc, h.IsLong, h.EntryPrice, sl);
            decimal prev = h.CurrentStop;
            decimal stop = prev;
            if (rec > 0 && (stop <= 0 || (h.IsLong ? rec > stop : rec < stop))) stop = rec;
            if (stop <= 0)
            {
                if (h.NoStopAlerted) return false;
                h.NoStopAlerted = true;
                OnAlert?.Invoke($"🚨 [DONCHIAN-{sl.Id}] {symbol} 손절가 계산 불가(진입 {h.EntryUtc:MM-dd HH:mm}Z 가 {DonchianKlineLimit}봉 창 밖) — 수동 손절 확인 필요");
                return false;
            }
            bool moved = prev > 0 && stop != prev;
            h.CurrentStop = stop;
            if (moved) DonchianTrend.Save(DonchianUserId);

            decimal px = k.Count > 0 ? k[^1].ClosePrice : 0m;
            if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tk) && tk.LastPrice > 0) px = tk.LastPrice;
            if (px > 0 && (h.IsLong ? px <= stop : px >= stop))
            {
                // 백테스트: 다음 봉 시가가 손절선 밖이면 시가 청산 — 거래소는 즉시 발동 SL 을 거부(-2021)하므로 시장가로 대체
                OnStatusLog?.Invoke($"📐 [DONCHIAN-{sl.Id}] {symbol} 손절선 {stop:F6} 이미 이탈(현재 {px:F6}) → 시장가 청산");
                await _positionMonitor.ExecuteMarketClose(symbol, $"돈치안{sl.Id} 트레일 {sl.TrailAtr}ATR 이탈 {stop:F6}", token);
                return true;
            }

            if (moved || !h.StopSynced)
            {
                if (moved) OnStatusLog?.Invoke($"📐 [DONCHIAN-{sl.Id}] {symbol} 트레일 이동 {prev:F6} → {stop:F6} ({(h.IsLong ? "롱" : "숏")} · 종가 ∓ {sl.TrailAtr}ATR)");
                await DonchianSyncOrdersAsync(symbol, token);
            }
            return moved;
        }

        /// <summary>거래소 주문 동기화: 기존 조건부 주문 전부 취소 → SL(보유 전량) + 50% TP(아직이면). TP 가 이미 지난 가격이면 절반 시장가 익절.</summary>
        private async Task DonchianSyncOrdersAsync(string symbol, CancellationToken token)
        {
            if (!DonchianTrend.Active.TryGetValue(symbol, out var h) || h.CurrentStop <= 0 || _orderLifecycle == null) return;
            decimal qty = 0m;
            lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) qty = Math.Abs(p.Quantity); }
            if (qty <= 0) return;
            if (h.InitQty <= 0) { h.InitQty = qty; DonchianTrend.Save(DonchianUserId); }

            decimal tpQty = 0m, tpPx = 0m;
            if (!h.HalfDone && h.TpPrice > 0)
            {
                tpPx = h.TpPrice; tpQty = Math.Min(qty, h.InitQty * 0.5m);
                decimal px = 0m;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var tk) && tk.LastPrice > 0) px = tk.LastPrice;
                if (px > 0 && (h.IsLong ? px >= tpPx : px <= tpPx))
                {
                    // 이미 익절가를 지남 → 절반 즉시 시장가 익절(감축 전용)
                    var r = await _exchangeService.PlaceMarketOrderAsync(symbol, h.IsLong ? "SELL" : "BUY", tpQty, token, reduceOnly: true);
                    if (r.Success)
                    {
                        h.HalfDone = true; DonchianTrend.Save(DonchianUserId);
                        OnStatusLog?.Invoke($"💰 [DONCHIAN-{h.SleeveId}] {symbol} 50% 익절(시장가) qty={r.FilledQuantity} @ {r.AveragePrice} — 익절가 {tpPx:F6} 경과");
                        qty = Math.Max(0m, qty - r.FilledQuantity);
                    }
                    tpQty = 0m;
                }
            }
            if (qty <= 0) return;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var (slId, tpId) = await _orderLifecycle.RegisterStopAndTpAsync(symbol, h.IsLong, qty, h.CurrentStop, h.HalfDone ? 0m : tpQty, tpPx, token);
                if (!string.IsNullOrEmpty(slId))
                {
                    h.StopSynced = true;
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var p)) { p.StopOrderId = slId; p.StopLoss = h.CurrentStop; p.TakeProfit = h.HalfDone ? 0 : tpPx; }
                    }
                    return;
                }
                try { await Task.Delay(1500 * attempt, token); } catch { return; }
            }
            OnAlert?.Invoke($"🚨 [DONCHIAN SL 실패] {symbol} 손절 {h.CurrentStop:F6} 등록 3회 실패 — 다음 마감에 재시도, 수동 확인 필요");
        }

        /// <summary>50% 익절 체결 감지 · 청산 감지(손실이면 7일 재진입 금지) · 재시작 후 미동기화 보유분 동기화.</summary>
        private async Task DonchianHousekeepingAsync(CancellationToken token)
        {
            foreach (var kv in DonchianTrend.Active.ToList())
            {
                var h = kv.Value;
                bool hasPos; decimal qty = 0m;
                lock (_posLock)
                {
                    hasPos = _activePositions.TryGetValue(kv.Key, out var p) && Math.Abs(p.Quantity) > 0m;
                    if (hasPos)
                    {
                        qty = Math.Abs(p!.Quantity);
                        if (string.IsNullOrEmpty(p.EntrySignalSource)) p.EntrySignalSource = h.IsLong ? h.Sleeve.LongSource : h.Sleeve.ShortSource;
                    }
                }
                if (hasPos)
                {
                    h.MissingChecks = 0;
                    // 50% 익절 체결: 보유 수량이 처음의 75% 이하로 줄면 완료로 보고 남은 수량 기준으로 SL 재등록
                    if (!h.HalfDone && h.InitQty > 0 && qty <= h.InitQty * 0.75m)
                    {
                        h.HalfDone = true; h.StopSynced = false; DonchianTrend.Save(DonchianUserId);
                        OnStatusLog?.Invoke($"💰 [DONCHIAN-{h.SleeveId}] {kv.Key} 50% 익절 체결 확인 (잔량 {qty}/{h.InitQty}) → 잔량 손절 재등록");
                    }
                    if (!h.StopSynced)
                    {
                        try { await DonchianUpdateTrailAsync(kv.Key, DateTime.UtcNow, token); if (!h.StopSynced) await DonchianSyncOrdersAsync(kv.Key, token); }
                        catch (Exception ex) { OnStatusLog?.Invoke($"⚠️ [DONCHIAN] {kv.Key} 주문 동기화 오류: {ex.Message}"); }
                    }
                    continue;
                }
                // 진입 직후 체결 반영 전 공백·재시작 복원 지연을 감안해 6회(≈2분) 연속 부재 시 종료 처리
                if (++h.MissingChecks >= 6)
                {
                    DonchianTrend.Active.TryRemove(kv.Key, out _);
                    // 손익 판정(근사): 잔량은 마지막 손절선에서 청산, 50% 익절 완료분은 익절가에서 실현
                    decimal d = h.IsLong ? 1m : -1m;
                    decimal frac = h.EntryPrice > 0 && h.CurrentStop > 0
                        ? (h.HalfDone ? 0.5m * d * (h.TpPrice - h.EntryPrice) / h.EntryPrice + 0.5m * d * (h.CurrentStop - h.EntryPrice) / h.EntryPrice
                                      : d * (h.CurrentStop - h.EntryPrice) / h.EntryPrice) - 0.0012m
                        : 0m;
                    if (frac < 0)
                    {
                        DonchianTrend.CooldownUntil[$"{kv.Key}|{h.SleeveId}"] = DateTime.UtcNow.AddHours(h.Sleeve.CooldownHours);
                        OnStatusLog?.Invoke($"📐 [DONCHIAN-{h.SleeveId}] {kv.Key} 손실 청산(≈{frac:P2}) → {h.Sleeve.CooldownHours / 24}일 재진입 금지");
                    }
                    else OnStatusLog?.Invoke($"📐 [DONCHIAN-{h.SleeveId}] {kv.Key} 청산(≈{frac:P2}) → 소유 해제");
                    DonchianTrend.Save(DonchianUserId);
                }
            }
        }
    }
}
