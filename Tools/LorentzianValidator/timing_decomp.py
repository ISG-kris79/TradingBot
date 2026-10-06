# 즉시진입 vs 15m 대기진입 — 거래별 분해 (C# 백테스트와 독립 재계산으로 교차검증)
# 각 즉시진입 거래에 대해 대기 규칙(A/B/C, 4h 대기)이 ①진입을 피했는지 ②더 싸게/비싸게 들어갔는지 계산.
# 청산가는 즉시진입 거래의 실제 청산가를 그대로 쓴 근사(대기진입 후 초기손절이 먼저 맞는 경우는 별도 집계).
import io, sys, datetime, bisect

W4 = 4 * 3600_000
NOTIONAL, FEE = 3000.0, 0.0012

def load(sym):
    rows = [l.split(',') for l in io.open(f'cache/{sym}_15m_71.csv', encoding='utf-8').read().splitlines()]
    return ([int(r[0]) for r in rows], [float(r[1]) for r in rows], [float(r[2]) for r in rows],
            [float(r[3]) for r in rows], [float(r[4]) for r in rows])

def agg4h(T, O, H, L, C):
    b = []; i = 0
    while i < len(T):
        bk = T[i] // W4; j = i; h = H[i]; l = L[i]
        while j + 1 < len(T) and T[j + 1] // W4 == bk: j += 1; h = max(h, H[j]); l = min(l, L[j])
        if j - i + 1 == 16: b.append((bk * W4, O[i], h, l, C[j]))
        i = j + 1
    return b

def atr(b, p=14):
    r = [0.0] * len(b); a = 0.0
    for i in range(1, len(b)):
        tr = max(b[i][2] - b[i][3], abs(b[i][2] - b[i - 1][4]), abs(b[i][3] - b[i - 1][4]))
        a = a + tr / p if i <= p else (a * (p - 1) + tr) / p
        r[i] = a if i >= p else 0
    return r

trades = [l.split(',') for l in io.open('trades-chase45.csv', encoding='utf-8').read().splitlines()[1:]]
taken = [x for x in trades if x[0] == 'TAKEN' and x[7] != 'END']
cache = {}
res = {m: dict(win_missed=0, win_missed_usd=0.0, loss_avoided=0, loss_avoided_usd=0.0, entered=0, delta_usd=0.0,
               stopped_before=0, stopped_before_usd=0.0) for m in 'ABCDE'}
for x in taken:
    sym = x[1]; d = 1 if x[2] == 'L' else -1; pnl = float(x[8]); e0px = float(x[5]); xpx = float(x[6])
    if sym not in cache:
        T, O, H, L, C = load(sym); b = agg4h(T, O, H, L, C)
        cache[sym] = (T, O, H, L, C, b, {r[0]: k for k, r in enumerate(b)}, atr(b))
    T, O, H, L, C, b, idx, A = cache[sym]
    tin = int(datetime.datetime.strptime(x[3], '%Y-%m-%d %H:%M').replace(tzinfo=datetime.timezone.utc).timestamp() * 1000)
    i = idx.get(tin - W4)
    if i is None: continue
    level = max(r[2] for r in b[i - 55:i]) if d > 0 else min(r[3] for r in b[i - 55:i])
    sc = b[i][4]; at = A[i]
    j0 = bisect.bisect_left(T, tin)
    for mode in 'ABCDE':
        ent = None; pulled = touched = False; broke = False; jl = j0
        for j in range(j0, min(len(T) - 1, j0 + 16)):
            jl = j
            if d * (C[j] - level) < 0: broke = True; break
            if mode in 'DE':
                # D = B(눌림 후 반등) + 4h 동안 눌림 없이 신호봉 종가 위 유지 시 진입 / E = C(재확인) + 같은 보완
                if mode == 'D':
                    if d * (sc - (L[j] if d > 0 else H[j])) >= 0.5 * at: pulled = True
                    if pulled and j > j0 and d * (C[j] - (H[j - 1] if d > 0 else L[j - 1])) > 0: ent = j + 1; break
                else:
                    if d * ((L[j] if d > 0 else H[j]) - level) <= 0: touched = True
                    if touched and d * (C[j] - level) > 0: ent = j + 1; break
            if mode == 'A' and d * (C[j] - sc) > 0: ent = j + 1; break
            if mode == 'B':
                if d * (sc - (L[j] if d > 0 else H[j])) >= 0.5 * at: pulled = True
                if pulled and j > j0 and d * (C[j] - (H[j - 1] if d > 0 else L[j - 1])) > 0: ent = j + 1; break
            if mode == 'C':
                if d * ((L[j] if d > 0 else H[j]) - level) <= 0: touched = True
                if touched and d * (C[j] - level) > 0: ent = j + 1; break
        if ent is None and mode in 'DE' and not broke and d * (C[jl] - sc) > 0: ent = jl + 1
        r = res[mode]
        if ent is None:
            if pnl > 0: r['win_missed'] += 1; r['win_missed_usd'] += pnl
            else: r['loss_avoided'] += 1; r['loss_avoided_usd'] += -pnl
            continue
        p = O[ent]; stop = p - d * 2 * at
        # 대기진입 후 원래 청산 전에 초기손절이 먼저 맞는지
        tout = int(datetime.datetime.strptime(x[4], '%Y-%m-%d %H:%M').replace(tzinfo=datetime.timezone.utc).timestamp() * 1000)
        jx = bisect.bisect_left(T, tout)
        hit = any((L[k] <= stop if d > 0 else H[k] >= stop) for k in range(ent, min(jx, ent + 16 * 6)))
        if hit:
            new = (-2 * at / p - FEE) * NOTIONAL
            r['stopped_before'] += 1; r['stopped_before_usd'] += new - pnl
        else:
            new = (d * (xpx - p) / p - FEE) * NOTIONAL
        r['entered'] += 1; r['delta_usd'] += new - pnl

print(f'즉시진입 실현 거래 {len(taken)}건 (수익 {sum(1 for x in taken if float(x[8])>0)} · 손실 {sum(1 for x in taken if float(x[8])<=0)})\n')
names = {'A': '상승확인 후 진입', 'B': '눌림 후 반등 진입', 'C': '돌파선 재확인 진입', 'D': '눌림 반등 + 4h 버티면 진입', 'E': '돌파선 재확인 + 4h 버티면 진입'}
for m in 'ABCDE':
    r = res[m]
    net = r['loss_avoided_usd'] - r['win_missed_usd'] + r['delta_usd']
    print(f'[{m} {names[m]}]')
    print(f'  피한 손실   : {r["loss_avoided"]:3}건  +${r["loss_avoided_usd"]:,.0f}')
    print(f'  놓친 수익   : {r["win_missed"]:3}건  -${r["win_missed_usd"]:,.0f}')
    print(f'  늦게 진입한 {r["entered"]}건의 손익 차이: {r["delta_usd"]:+,.0f}$ (그중 초기손절 먼저 맞음 {r["stopped_before"]}건 {r["stopped_before_usd"]:+,.0f}$)')
    print(f'  ▶ 즉시진입 대비 순효과 ≈ {net:+,.0f}$\n')
