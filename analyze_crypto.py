"""
XRP/ETH 1시간봉 LONG/SHORT 양방향 분석
ThreeYearBacktestRunner.cs EvaluateEntry 로직 그대로 포팅
"""
import json, math, datetime, os

# ═══ 전략 파라미터 (ThreeYearBacktestRunner.cs 동일) ═══
LEVERAGE       = 20
MARGIN_PCT     = 0.13
FEE_RATE       = 0.0004
SL_ROE         = -0.20
BE_ROE         = 0.05
TP1_ROE        = 0.20
TP2_ROE        = 0.40
TRAIL_START    = 0.35
TRAIL_GAP      = 0.04
ENTRY_THRESHOLD = 60

RSI_PERIOD   = 14
BB_PERIOD    = 20
EMA_SHORT    = 20
EMA_LONG     = 50
VOL_MA_PERIOD= 20
ATR_PERIOD   = 14
MACD_FAST    = 12
MACD_SLOW    = 26

# ═══ 데이터 로드 ═══
def load_candles(path):
    with open(path) as f:
        raw = json.load(f)
    return [{
        'ts':    datetime.datetime.fromtimestamp(c[0]/1000),
        'open':  float(c[1]),
        'high':  float(c[2]),
        'low':   float(c[3]),
        'close': float(c[4]),
        'volume':float(c[5])
    } for c in raw]

# ═══ 지표 계산 ═══
def ema_series(values, period):
    k = 2 / (period + 1)
    out = [values[0]]
    for v in values[1:]:
        out.append(v * k + out[-1] * (1 - k))
    return out

def compute_rsi(closes, period=14):
    rsi = [50.0] * (period + 1)
    gains = [max(closes[i]-closes[i-1], 0) for i in range(1, len(closes))]
    losses= [max(closes[i-1]-closes[i], 0) for i in range(1, len(closes))]
    ag = sum(gains[:period]) / period
    al = sum(losses[:period]) / period
    for i in range(period, len(gains)):
        ag = (ag * (period-1) + gains[i]) / period
        al = (al * (period-1) + losses[i]) / period
        rs = ag / al if al > 0 else 100
        rsi.append(100 - 100/(1+rs))
    return rsi

def compute_atr(closes, highs, lows, period=14):
    """최근 period개의 ATR (ThreeYearBacktestRunner와 동일)"""
    trs = []
    for i in range(1, len(closes)):
        tr = max(highs[i]-lows[i], abs(highs[i]-closes[i-1]), abs(lows[i]-closes[i-1]))
        trs.append(tr)
    if len(trs) < period:
        return trs[-1] if trs else 0
    return sum(trs[-period:]) / period

# ═══ 진입 신호 평가 (ThreeYearBacktestRunner.EvaluateEntry 포팅) ═══
def evaluate_entry(candles, i):
    """
    C# EvaluateEntry와 동일한 로직:
    - LONG/SHORT 각각 100점 계산
    - 높은 쪽이 임계값 이상이면 진입
    - ATR > 3% 과열 구간 스킵
    반환: (score, direction, detail_dict) or None
    """
    if i < max(EMA_LONG, MACD_SLOW) + 5:
        return None

    window_c = [c['close']  for c in candles[:i+1]]
    window_h = [c['high']   for c in candles[:i+1]]
    window_l = [c['low']    for c in candles[:i+1]]
    window_v = [c['volume'] for c in candles[:i+1]]

    price     = window_c[-1]
    prev_price= window_c[-2]

    # RSI
    rsi_all = compute_rsi(window_c, RSI_PERIOD)
    rsi = rsi_all[-1]

    # MACD: emaFast - emaSlow (C#과 동일, signal 없이)
    ema_fast_all = ema_series(window_c, MACD_FAST)
    ema_slow_all = ema_series(window_c, MACD_SLOW)
    macd_now  = ema_fast_all[-1] - ema_slow_all[-1]
    macd_prev = ema_fast_all[-2] - ema_slow_all[-2]
    macd_pos    = macd_now > 0
    macd_rising = macd_now > macd_prev

    # BB (20봉)
    bb_slice = window_c[-BB_PERIOD:]
    bb_mid   = sum(bb_slice) / BB_PERIOD
    bb_std   = math.sqrt(sum((x-bb_mid)**2 for x in bb_slice) / BB_PERIOD)
    bb_upper = bb_mid + 2*bb_std
    bb_lower = bb_mid - 2*bb_std
    bb_width = bb_upper - bb_lower
    bb_pos   = (price - bb_lower) / bb_width if bb_width > 0 else 0.5

    # EMA 20 현재 / 이전
    ema20_all  = ema_series(window_c, EMA_SHORT)
    ema20      = ema20_all[-1]
    ema20_prev = ema20_all[-2]

    # 거래량
    vol_avg = sum(window_v[-VOL_MA_PERIOD:]) / VOL_MA_PERIOD
    vol_cur = window_v[-1]
    vol_ratio = vol_cur / vol_avg if vol_avg > 0 else 1.0

    # ATR 과열 필터 (C# 동일: atrPct > 3% → skip)
    atr = compute_atr(window_c, window_h, window_l, ATR_PERIOD)
    atr_pct = atr / price * 100 if price > 0 else 0
    if atr_pct > 3.0:
        return None

    # EMA50
    ema50_all = ema_series(window_c, EMA_LONG)
    ema50 = ema50_all[-1]

    # EMA20 거리 (±0.5% 보너스 판단용)
    ema20_dist = abs(price - ema20) / ema20 if ema20 > 0 else 1

    # 추세 방향: 가격 vs EMA20 (즉시 반응, EMA 크로스보다 훨씬 빠름)
    # price < EMA20 → 하락 추세 → LONG 역추세 페널티
    # price > EMA20 → 상승 추세 → SHORT 역추세 페널티
    uptrend   = price > ema20
    downtrend = price < ema20

    # ────────────────────────────────────────────
    # LONG 점수 (C# EvaluateEntry longScore와 동일)
    # ────────────────────────────────────────────
    # [1] RSI (0~25)
    rsi_l = (25 if rsi <= 25 else
             22 if rsi <= 35 else
             18 if rsi <= 45 else
             14 if rsi <= 55 else
              7 if rsi <= 65 else 0)

    # [2] 모멘텀 (0~25): MACD 방향 + EMA20 기울기
    mom_l = (25 if macd_pos and macd_rising else
             18 if macd_pos else
             12 if macd_rising else 0)
    if ema20 > ema20_prev:
        mom_l = min(25, mom_l + 5)

    # [3] 가격대 (0~25): BB 하단 근접
    price_l = (25 if bb_pos <= 0.20 else
               20 if bb_pos <= 0.35 else
               14 if bb_pos <= 0.50 else
                7 if bb_pos <= 0.65 else 2)
    if ema20_dist < 0.005:
        price_l = min(25, price_l + 3)

    # [4] 거래량 (0~25)
    vol_score = (25 if vol_ratio >= 2.5 else
                 20 if vol_ratio >= 1.8 else
                 15 if vol_ratio >= 1.2 else
                 10 if vol_ratio >= 0.8 else 5)

    long_score = rsi_l + mom_l + price_l + vol_score

    # ────────────────────────────────────────────
    # SHORT 점수 (C# EvaluateEntry shortScore와 동일)
    # ────────────────────────────────────────────
    # [1] RSI (0~25) — 하락 추세 연속성 반영 (RSI 과매도라도 하락 지속 가능)
    rsi_s = (25 if rsi >= 75 else
             22 if rsi >= 65 else
             18 if rsi >= 55 else
             14 if rsi >= 45 else
             10 if rsi >= 35 else 8)  # 기존 7→10, 0→8

    # [2] 모멘텀 (0~25)
    mom_s = (25 if not macd_pos and not macd_rising else
             18 if not macd_pos else
             12 if not macd_rising else 0)
    if ema20 < ema20_prev:
        mom_s = min(25, mom_s + 5)

    # [3] 가격대 (0~25): BB 상단 근접
    price_s = (25 if bb_pos >= 0.80 else
               20 if bb_pos >= 0.65 else
               14 if bb_pos >= 0.50 else
                7 if bb_pos >= 0.35 else 2)
    if ema20_dist < 0.005:
        price_s = min(25, price_s + 3)

    short_score = rsi_s + mom_s + price_s + vol_score

    # ────────────────────────────────────────────
    # 추세 정렬 필터 (EMA20 vs EMA50)
    # 하락 추세 시 LONG 페널티 -20pt, 상승 추세 시 SHORT 페널티 -20pt
    # ────────────────────────────────────────────
    if downtrend: long_score  = max(0, long_score  - 20)
    if uptrend:   short_score = max(0, short_score - 20)

    trend_label = f"EMA20({'>' if uptrend else '<' if downtrend else '='}EMA50)"

    # ────────────────────────────────────────────
    # 방향 선택 (C# 동일: 높은 쪽, 동점이면 LONG)
    # ────────────────────────────────────────────
    if long_score >= short_score and long_score >= ENTRY_THRESHOLD:
        detail = {
            'rsi':   (rsi_l,    f"RSI={rsi:.1f}"),
            'mom':   (mom_l,    f"MACD={'양' if macd_pos else '음'}{'↑' if macd_rising else '↓'} EMA20={'↑' if ema20>ema20_prev else '↓'}"),
            'price': (price_l,  f"BBpos={bb_pos:.2f}(하단근접)"),
            'vol':   (vol_score, f"vol={vol_ratio:.1f}x"),
            'atr':   atr_pct,
            'bb_pos': bb_pos,
            'macd_now': macd_now,
            'rsi_val': rsi,
            'trend': trend_label,
        }
        return long_score, 'LONG', detail

    if short_score > long_score and short_score >= ENTRY_THRESHOLD:
        detail = {
            'rsi':   (rsi_s,    f"RSI={rsi:.1f}"),
            'mom':   (mom_s,    f"MACD={'양' if macd_pos else '음'}{'↑' if macd_rising else '↓'} EMA20={'↑' if ema20>ema20_prev else '↓'}"),
            'price': (price_s,  f"BBpos={bb_pos:.2f}(상단근접)"),
            'vol':   (vol_score, f"vol={vol_ratio:.1f}x"),
            'atr':   atr_pct,
            'bb_pos': bb_pos,
            'macd_now': macd_now,
            'rsi_val': rsi,
            'trend': trend_label,
        }
        return short_score, 'SHORT', detail

    return None

# ═══ 포지션 시뮬레이션 (LONG/SHORT 공통) ═══
def simulate_trade(candles, entry_i, direction, entry_price):
    """
    C# SimulateSymbol 포지션 관리 로직 포팅
    반환: (pnl_roe_net, result_str)
    """
    sl_price  = entry_price * (1 + SL_ROE  / LEVERAGE) if direction == 'LONG' \
                else entry_price * (1 - SL_ROE  / LEVERAGE)
    be_price  = entry_price * (1 + BE_ROE  / LEVERAGE) if direction == 'LONG' \
                else entry_price * (1 - BE_ROE  / LEVERAGE)
    tp1_price = entry_price * (1 + TP1_ROE / LEVERAGE) if direction == 'LONG' \
                else entry_price * (1 - TP1_ROE / LEVERAGE)
    tp2_price = entry_price * (1 + TP2_ROE / LEVERAGE) if direction == 'LONG' \
                else entry_price * (1 - TP2_ROE / LEVERAGE)

    qty        = 1.0
    be_on      = False
    tp1_done   = False
    trail_on   = False
    peak_price = entry_price   # LONG: highest, SHORT: lowest
    trail_stop = 0.0
    pnl_roe    = 0.0
    steps      = []

    for j in range(entry_i + 1, len(candles)):
        c    = candles[j]
        high = c['high']
        low  = c['low']

        # BE 활성화
        if not be_on:
            if direction == 'LONG' and high >= be_price:
                be_on = True; sl_price = entry_price
            elif direction == 'SHORT' and low <= be_price:
                be_on = True; sl_price = entry_price

        # peak 갱신 + 트레일링 시작 체크
        if direction == 'LONG':
            peak_price = max(peak_price, high)
            peak_roe = (peak_price - entry_price) / entry_price * LEVERAGE
        else:
            peak_price = min(peak_price, low)
            peak_roe = (entry_price - peak_price) / entry_price * LEVERAGE

        if not trail_on and peak_roe >= TRAIL_START:
            trail_on = True

        if trail_on:
            gap_pct = TRAIL_GAP / LEVERAGE
            if direction == 'LONG':
                new_trail = peak_price * (1 - gap_pct)
                trail_stop = max(trail_stop, new_trail)
            else:
                new_trail = peak_price * (1 + gap_pct)
                trail_stop = min(trail_stop, new_trail) if trail_stop != 0 else new_trail

        # TP1
        if not tp1_done:
            tp1_hit = (high >= tp1_price) if direction == 'LONG' else (low <= tp1_price)
            if tp1_hit:
                pnl_roe += 0.5 * TP1_ROE
                qty = 0.5
                tp1_done = True
                steps.append(f"    → TP1 @ {c['ts']}  +{TP1_ROE*100:.0f}% (50%청산)")

        # TP2
        tp2_hit = (high >= tp2_price) if direction == 'LONG' else (low <= tp2_price)
        if tp2_hit:
            pnl_roe += qty * TP2_ROE
            steps.append(f"    → TP2 전량청산 @ {c['ts']}  +{TP2_ROE*100:.0f}%")
            return pnl_roe, steps, sl_price, be_price, tp1_price, tp2_price

        # 트레일링 스탑
        if trail_on and trail_stop != 0:
            trail_hit = (low <= trail_stop) if direction == 'LONG' else (high >= trail_stop)
            if trail_hit:
                exit_px = trail_stop
                trail_roe = (exit_px - entry_price) / entry_price * LEVERAGE if direction == 'LONG' \
                            else (entry_price - exit_px) / entry_price * LEVERAGE
                pnl_roe += qty * trail_roe
                steps.append(f"    → 트레일링스탑 @ {c['ts']}  가격={exit_px:.4f}  ROE={trail_roe*100:.1f}%")
                return pnl_roe, steps, sl_price, be_price, tp1_price, tp2_price

        # SL/BE
        sl_hit = (low <= sl_price) if direction == 'LONG' else (high >= sl_price)
        if sl_hit:
            sl_roe = 0.0 if be_on else SL_ROE
            pnl_roe += qty * sl_roe
            label = 'BE손절' if be_on else '손절'
            steps.append(f"    → {label} @ {c['ts']}  ROE={sl_roe*100:.0f}%")
            return pnl_roe, steps, sl_price, be_price, tp1_price, tp2_price

    # 미청산
    last = candles[-1]['close']
    open_roe = (last - entry_price) / entry_price * LEVERAGE if direction == 'LONG' \
               else (entry_price - last) / entry_price * LEVERAGE
    pnl_roe += qty * open_roe
    steps.append(f"    → 미청산(현재={last:.4f})  평가ROE={open_roe*100:.1f}%")
    return pnl_roe, steps, sl_price, be_price, tp1_price, tp2_price


# ═══ 심볼 시뮬레이션 ═══
def simulate(symbol, candles):
    closes  = [c['close']  for c in candles]
    volumes = [c['volume'] for c in candles]

    # 사전 계산 (표 출력용)
    rsi_arr   = compute_rsi(closes)
    ema12_arr = ema_series(closes, MACD_FAST)
    ema26_arr = ema_series(closes, MACD_SLOW)
    macd_arr  = [a-b for a, b in zip(ema12_arr, ema26_arr)]
    vol_ma    = [sum(volumes[max(0,i-VOL_MA_PERIOD+1):i+1])/min(i+1,VOL_MA_PERIOD) for i in range(len(volumes))]

    print(f"\n{'='*70}")
    print(f"  {symbol}  |  기준: {candles[-1]['ts']} UTC  (현재가: {candles[-1]['close']:.4f})")
    print(f"{'='*70}")

    print(f"\n[최근 24시간 캔들 (LONG↑/SHORT↓ 방향 포함)]")
    print(f"{'시각':20} {'종가':>10} {'RSI':>6} {'MACD(12-26)':>13} {'BB위치':>7} {'거래량':>8}  방향")
    print("-"*75)

    start_i = max(EMA_LONG + 5, len(candles) - 24)
    for i in range(start_i, len(candles)):
        c   = candles[i]
        vr  = c['volume'] / vol_ma[i] if vol_ma[i] > 0 else 1
        # BB pos
        bb_s = closes[max(0,i-BB_PERIOD+1):i+1]
        bm = sum(bb_s)/len(bb_s)
        bs = math.sqrt(sum((x-bm)**2 for x in bb_s)/len(bb_s))
        bpos = (c['close']-(bm-2*bs))/(4*bs) if bs>0 else 0.5
        sig = evaluate_entry(candles, i)
        dir_str = f"{'LONG↑' if sig and sig[1]=='LONG' else 'SHORT↓' if sig and sig[1]=='SHORT' else ''} {sig[0]:.0f}pt" if sig else ""
        print(f"{str(c['ts']):20} {c['close']:>10.4f} {rsi_arr[i]:>6.1f} {macd_arr[i]:>13.5f} {bpos:>7.2f} {vr:>8.1f}x  {dir_str}")

    # ─ 진입 신호 수집 ─
    print(f"\n[진입 신호 스캔 - 최근 24시간]")
    entries = []
    best_long  = (0, start_i)
    best_short = (0, start_i)
    in_position = False

    for i in range(start_i, len(candles) - 1):
        if in_position:
            continue  # 실제 운용은 1포지션씩만 (단순화)
        sig = evaluate_entry(candles, i)
        if sig:
            score, direction, detail = sig
            entries.append((i, score, direction, detail, candles[i]))
            in_position = False  # 백테스트처럼 연속진입 허용 (분석 목적)
        else:
            # 최고점수 추적 (임계값 미달 경우 참고용)
            if i >= max(EMA_LONG, MACD_SLOW) + 5:
                for test_dir in ['LONG', 'SHORT']:
                    pass  # 이미 evaluate_entry에서 내부적으로 계산

    if not entries:
        print(f"  → 24시간 내 진입 신호 없음 (임계값 {ENTRY_THRESHOLD}점 미달)")
        # 참고용 최고 점수 출력
        best_l = best_s = 0
        best_li = best_si = start_i
        for i in range(start_i, len(candles) - 1):
            sig = evaluate_entry(candles, i)
            # 임계값 무시하고 원시 점수 추출 불가 (evaluate_entry가 임계값 필터 포함)
            # 대신 가장 최근 상태 출력
        print(f"  (마지막 캔들 상태: RSI={rsi_arr[-2]:.1f}  MACD={macd_arr[-2]:.5f})")

    total_pnl_roe = 0
    for (i, score, direction, detail, ec) in entries:
        ep = ec['close']

        # 방향별 TP/SL 가격
        sl  = ep * (1 + SL_ROE  / LEVERAGE) if direction == 'LONG' else ep * (1 - SL_ROE  / LEVERAGE)
        be  = ep * (1 + BE_ROE  / LEVERAGE) if direction == 'LONG' else ep * (1 - BE_ROE  / LEVERAGE)
        tp1 = ep * (1 + TP1_ROE / LEVERAGE) if direction == 'LONG' else ep * (1 - TP1_ROE / LEVERAGE)
        tp2 = ep * (1 + TP2_ROE / LEVERAGE) if direction == 'LONG' else ep * (1 - TP2_ROE / LEVERAGE)

        dir_arrow = "↑LONG " if direction == 'LONG' else "↓SHORT"
        print(f"\n  ◆ {dir_arrow} @ {ec['ts']}  점수={score}pt")
        print(f"    진입가: {ep:.4f}")
        print(f"    RSI {detail['rsi'][0]}pt: {detail['rsi'][1]}")
        print(f"    모멘텀 {detail['mom'][0]}pt: {detail['mom'][1]}")
        print(f"    가격대 {detail['price'][0]}pt: {detail['price'][1]}")
        print(f"    거래량 {detail['vol'][0]}pt: {detail['vol'][1]}")
        print(f"    ATR={detail['atr']:.2f}%  BBpos={detail['bb_pos']:.2f}  MACD={detail['macd_now']:.5f}  {detail['trend']}")
        print(f"    ──────────────────────────────────────────────────")
        if direction == 'LONG':
            print(f"    손절가: {sl:.4f}  (-1%p = ROE {SL_ROE*100:.0f}%  = ${SL_ROE*MARGIN_PCT*1000:.1f})")
            print(f"    BE가:   {be:.4f}  (+{BE_ROE/LEVERAGE*100:.2f}%p = ROE +{BE_ROE*100:.0f}%)")
            print(f"    TP1:    {tp1:.4f}  (+{TP1_ROE/LEVERAGE*100:.2f}%p = ROE +{TP1_ROE*100:.0f}%  50%청산 = +${TP1_ROE*0.5*MARGIN_PCT*1000:.1f})")
            print(f"    TP2:    {tp2:.4f}  (+{TP2_ROE/LEVERAGE*100:.2f}%p = ROE +{TP2_ROE*100:.0f}% 전청산 = +${TP2_ROE*MARGIN_PCT*1000:.1f})")
        else:
            print(f"    손절가: {sl:.4f}  (+1%p = ROE {SL_ROE*100:.0f}%  = ${SL_ROE*MARGIN_PCT*1000:.1f})")
            print(f"    BE가:   {be:.4f}  (-{BE_ROE/LEVERAGE*100:.2f}%p = ROE +{BE_ROE*100:.0f}%)")
            print(f"    TP1:    {tp1:.4f}  (-{TP1_ROE/LEVERAGE*100:.2f}%p = ROE +{TP1_ROE*100:.0f}%  50%청산 = +${TP1_ROE*0.5*MARGIN_PCT*1000:.1f})")
            print(f"    TP2:    {tp2:.4f}  (-{TP2_ROE/LEVERAGE*100:.2f}%p = ROE +{TP2_ROE*100:.0f}% 전청산 = +${TP2_ROE*MARGIN_PCT*1000:.1f})")

        pnl_roe, steps, *_ = simulate_trade(candles, i, direction, ep)
        for s in steps:
            print(s)

        fee_roe = FEE_RATE * LEVERAGE * 2
        net_roe = pnl_roe - fee_roe
        margin_pnl = net_roe * MARGIN_PCT * 1000
        total_pnl_roe += net_roe

        print(f"    총ROE: {pnl_roe*100:.1f}%  수수료: -{fee_roe*100:.2f}%  순ROE: {net_roe*100:.1f}%")
        print(f"    $1000계좌 수익: ${margin_pnl:+.2f}  (마진 ${MARGIN_PCT*1000:.0f} 투입)")

    print(f"\n  [{symbol} 합산]")
    long_cnt  = sum(1 for e in entries if e[2] == 'LONG')
    short_cnt = sum(1 for e in entries if e[2] == 'SHORT')
    if entries:
        print(f"  진입: {len(entries)}회 (LONG {long_cnt}회 / SHORT {short_cnt}회)")
        print(f"  합산순ROE: {total_pnl_roe*100:.1f}%  수익: ${total_pnl_roe*MARGIN_PCT*1000:+.2f}")
    else:
        print(f"  진입 없음 → $0")

    return total_pnl_roe, len(entries)


# ═══ 메인 ═══
tmp = os.environ.get('TEMP', '/tmp').replace('\\', '/')
xrp = load_candles(f'{tmp}/xrp.json')
eth = load_candles(f'{tmp}/eth.json')

xrp_pnl, xrp_cnt = simulate('XRPUSDT', xrp)
eth_pnl, eth_cnt = simulate('ETHUSDT', eth)

print(f"\n{'='*70}")
print(f"  종합 브리핑  ({datetime.datetime.now().strftime('%Y-%m-%d %H:%M')})")
print(f"{'='*70}")
print(f"  XRP: {xrp_cnt}회 진입  순ROE합산 {xrp_pnl*100:.1f}%  수익 ${xrp_pnl*MARGIN_PCT*1000:+.2f}")
print(f"  ETH: {eth_cnt}회 진입  순ROE합산 {eth_pnl*100:.1f}%  수익 ${eth_pnl*MARGIN_PCT*1000:+.2f}")
print(f"  $1000 계좌 총수익: ${(xrp_pnl+eth_pnl)*MARGIN_PCT*1000:+.2f}")
print(f"\n  [전략 파라미터]")
print(f"  레버리지 {LEVERAGE}x | 마진 {MARGIN_PCT*100:.0f}% | 수수료 {FEE_RATE*100:.2f}%/side")
print(f"  SL ROE {SL_ROE*100:.0f}% | BE +{BE_ROE*100:.0f}% | TP1 +{TP1_ROE*100:.0f}%(50%) | TP2 +{TP2_ROE*100:.0f}%")
print(f"  트레일링: ROE +{TRAIL_START*100:.0f}%부터 {TRAIL_GAP*100:.0f}% 갭")
