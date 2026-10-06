# 4h 돈치안 신호 → "즉시 진입" vs "눌림 기다려 진입" 판단 로직 학습/검증
#   학습: 2019-09~2023-09 (cache/*_15m_170) · 검증: 2023-09~2026-10 (cache/*_15m_71, 학습에 미사용)
#   각 신호를 두 방식으로 끝까지 시뮬(초기손절 2ATR · 4h 종가 ∓5ATR 트레일 · 비용 0.12%+펀딩) → 신호 시점 특징으로 규칙 학습.
import io, sys, datetime, bisect, math, itertools

W4 = 4 * 3600_000
NOTIONAL, FEE, FUND8H = 3000.0, 0.0012, 0.0001
UNIV = ["BTCUSDT","ETHUSDT","XRPUSDT","BNBUSDT","SOLUSDT","DOGEUSDT","ADAUSDT","TRXUSDT","AVAXUSDT","LINKUSDT",
        "DOTUSDT","LTCUSDT","BCHUSDT","NEARUSDT","UNIUSDT","APTUSDT","ICPUSDT","ETCUSDT","FILUSDT","ARBUSDT",
        "OPUSDT","ATOMUSDT","SUIUSDT","AAVEUSDT","XLMUSDT","INJUSDT","ALGOUSDT","HBARUSDT","SEIUSDT","VETUSDT"]
SPLIT = int(datetime.datetime(2023, 9, 22, tzinfo=datetime.timezone.utc).timestamp() * 1000)

def load(sym, suffix, lo=0, hi=1 << 62):
    try: rows = [l.split(',') for l in io.open(f'cache/{sym}_{suffix}.csv', encoding='utf-8').read().splitlines()]
    except FileNotFoundError: return None
    rows = [r for r in rows if lo <= int(r[0]) < hi]
    if len(rows) < 5000: return None
    return ([int(r[0]) for r in rows], [float(r[1]) for r in rows], [float(r[2]) for r in rows],
            [float(r[3]) for r in rows], [float(r[4]) for r in rows], [float(r[5]) for r in rows])

def agg(T, O, H, L, C, V):
    b = []; end = []; i = 0
    while i < len(T):
        bk = T[i] // W4; j = i; h = H[i]; l = L[i]; v = V[i]
        while j + 1 < len(T) and T[j + 1] // W4 == bk: j += 1; h = max(h, H[j]); l = min(l, L[j]); v += V[j]
        if j - i + 1 == 16: b.append((bk * W4, O[i], h, l, C[j], v)); end.append(j)
        i = j + 1
    return b, end

def atr_s(b, p=14):
    r = [0.0] * len(b); a = 0.0
    for i in range(1, len(b)):
        tr = max(b[i][2] - b[i][3], abs(b[i][2] - b[i - 1][4]), abs(b[i][3] - b[i - 1][4]))
        a = a + tr / p if i <= p else (a * (p - 1) + tr) / p
        r[i] = a if i >= p else 0
    return r

def ema_s(x, p):
    r = []; a = 2 / (p + 1)
    for i, v in enumerate(x): r.append(v if i == 0 else a * v + (1 - a) * r[-1])
    return r

def sim(T, O, H, L, C, b, end, A, sig, e, d, entry):
    """15m 봉 e 시가에 entry 로 진입 → 초기손절 2ATR(신호봉) · 이후 4h 마감마다 종가∓5ATR 트레일. 반환: (손익$, 청산 15m idx)"""
    stop = entry - d * 2 * A[sig]; k = sig
    for j in range(e, len(T)):
        while k + 1 < len(end) and end[k + 1] < j:
            k += 1
            if A[k] > 0:
                ns = b[k][4] - d * 5 * A[k]
                if (d > 0 and ns > stop) or (d < 0 and ns < stop): stop = ns
        if d > 0:
            if O[j] <= stop: px = O[j]; break
            if L[j] <= stop: px = stop; break
        else:
            if O[j] >= stop: px = O[j]; break
            if H[j] >= stop: px = stop; break
    else:
        return None
    hours = (T[j] - T[e]) / 3600000
    return ((d * (px - entry) / entry - FEE - FUND8H * hours / 8) * NOTIONAL, j)

def build(suffix, lo, hi):
    out = []
    for sym in UNIV:
        D = load(sym, suffix, lo, hi)
        if not D: continue
        T, O, H, L, C, V = D
        b, end = agg(T, O, H, L, C, V); A = atr_s(b); E50 = ema_s([x[4] for x in b], 50)
        for i in range(70, len(b) - 1):
            hi55 = max(x[2] for x in b[i - 55:i]); lo55 = min(x[3] for x in b[i - 55:i])
            hiP = max(x[2] for x in b[i - 56:i - 1]); loP = min(x[3] for x in b[i - 56:i - 1])
            c = b[i][4]; d = 1 if (c > hi55 and b[i - 1][4] <= hiP) else -1 if (c < lo55 and b[i - 1][4] >= loP) else 0
            if d == 0 or A[i] <= 0: continue
            at = A[i]
            if d * (c - E50[i]) / at >= 4.5: continue                       # v5.35.2 과열추격 제외
            e0 = end[i] + 1
            if e0 >= len(T) - 1: continue
            level = hi55 if d > 0 else lo55
            imm = sim(T, O, H, L, C, b, end, A, i, e0, d, O[e0])
            # 눌림 기다림(4h): 0.5ATR 눌린 뒤 15m 종가가 직전봉 고가(숏:저가) 돌파 시 진입 · 돌파선 밖 종가면 폐기
            wait = None; pulled = False
            for j in range(e0, min(len(T) - 1, e0 + 16)):
                if d * (C[j] - level) < 0: break
                if d * (c - (L[j] if d > 0 else H[j])) >= 0.5 * at: pulled = True
                if pulled and j > e0 and d * (C[j] - (H[j - 1] if d > 0 else L[j - 1])) > 0:
                    wait = sim(T, O, H, L, C, b, end, A, i, j + 1, d, O[j + 1]); break
            if imm is None: continue
            vavg = sum(x[5] for x in b[i - 20:i]) / 20
            rngb = b[i][2] - b[i][3]
            f = dict(
                brk=d * (c - level) / at,                                      # 돌파 강도
                clv=((c - b[i][3]) / rngb if d > 0 else (b[i][2] - c) / rngb) if rngb > 0 else 0.5,   # 마감 위치(1=봉 끝에서 마감)
                rng=rngb / at,                                                 # 돌파봉 크기
                vol=b[i][5] / vavg if vavg > 0 else 1,                         # 거래량 배수
                ext=d * (c - E50[i]) / at,                                     # 과열
                base=(hi55 - lo55) / at,                                       # 직전 55봉 횡보폭
                mom=d * (c - b[i - 6][4]) / at,                                # 24h 모멘텀
                atrp=at / c * 100, side=d)
            out.append(dict(sym=sym, t=b[i][0], d=d, imm=imm[0], wait=(wait[0] if wait else 0.0), waited=wait is not None, **f))
    return out

def rule_pnl(S, feat, thr, imm_if_ge):
    """규칙: feat >= thr 이면 (imm_if_ge 면 즉시, 아니면 대기) / 나머지는 반대"""
    tot = 0.0
    for s in S:
        use_imm = (s[feat] >= thr) == imm_if_ge
        tot += s['imm'] if use_imm else s['wait']
    return tot

if __name__ == '__main__':
    print('신호 시뮬 중 (학습 2019~2023 · 검증 2023~2026)...', flush=True)
    TR = build('15m_170', 0, SPLIT)
    TE = build('15m_71', SPLIT, 1 << 62)
    for nm, S in (('학습 2019~2023', TR), ('검증 2023~2026', TE)):
        print(f'{nm}: 신호 {len(S)} · 항상즉시 {sum(s["imm"] for s in S):,.0f}$ · 항상대기 {sum(s["wait"] for s in S):,.0f}$ · 신호별 최선 선택(상한) {sum(max(s["imm"], s["wait"]) for s in S):,.0f}$')
    feats = ['brk', 'clv', 'rng', 'vol', 'ext', 'base', 'mom', 'atrp', 'side']
    cands = []
    for f in feats:
        vals = sorted(s[f] for s in TR)
        for q in range(1, 20):
            thr = vals[int(len(vals) * q / 20)]
            for ig in (True, False):
                n_imm = sum(1 for s in TR if (s[f] >= thr) == ig)
                if n_imm < 0.1 * len(TR) or n_imm > 0.95 * len(TR): continue
                cands.append((rule_pnl(TR, f, thr, ig), f, thr, ig))
    cands.sort(reverse=True)
    base_tr = sum(s['imm'] for s in TR); base_te = sum(s['imm'] for s in TE)
    print(f'\n■ 학습기간 상위 규칙 10개 → 검증기간 적용 (기준: 항상즉시 학습 {base_tr:,.0f}$ / 검증 {base_te:,.0f}$)')
    for p, f, thr, ig in cands[:10]:
        te = rule_pnl(TE, f, thr, ig)
        print(f'  {f}≥{thr:.3f} → {"즉시" if ig else "대기"}, 그 외 {"대기" if ig else "즉시"} | 학습 {p:,.0f}$ ({p-base_tr:+,.0f}) | 검증 {te:,.0f}$ ({te-base_te:+,.0f})')
    # 특징별 "즉시가 나은 신호" 비율 — 어떤 특징이 판단에 쓸모 있는지
    print('\n■ 특징 구간별: 즉시진입 − 대기진입 평균 손익차 (학습 | 검증)')
    for f in feats:
        vals = sorted(s[f] for s in TR); qs = [vals[int(len(vals) * k / 4)] for k in range(1, 4)]
        edges = [-1e9] + qs + [1e9]; line = []
        for lo, hi in zip(edges[:-1], edges[1:]):
            a = [s['imm'] - s['wait'] for s in TR if lo <= s[f] < hi]; bb = [s['imm'] - s['wait'] for s in TE if lo <= s[f] < hi]
            line.append(f'{sum(a)/max(1,len(a)):+6.0f}|{sum(bb)/max(1,len(bb)):+6.0f}')
        print(f'  {f:5} 4분위: ' + '  '.join(line))

    # ── 2단계: 방향별로 나눠서 (숏 생략 vs 숏 대기 구분 + 방향 내부 규칙)
    print('\n■ 방향별 합계 (학습 | 검증)')
    for side, nm in ((1, '롱'), (-1, '숏')):
        a = [s for s in TR if s['d'] == side]; bb = [s for s in TE if s['d'] == side]
        print(f'  {nm}: 즉시 {sum(s["imm"] for s in a):,.0f} | {sum(s["imm"] for s in bb):,.0f}  ·  대기 {sum(s["wait"] for s in a):,.0f} | {sum(s["wait"] for s in bb):,.0f}  ·  생략 0 | 0  (대기 체결률 {100*sum(s["waited"] for s in a)/max(1,len(a)):.0f}% | {100*sum(s["waited"] for s in bb)/max(1,len(bb)):.0f}%)')
    for side, nm in ((1, '롱'), (-1, '숏')):
        a = [s for s in TR if s['d'] == side]; bb = [s for s in TE if s['d'] == side]
        base_a = sum(s['imm'] for s in a) if side > 0 else sum(s['wait'] for s in a)
        base_b = sum(s['imm'] for s in bb) if side > 0 else sum(s['wait'] for s in bb)
        cs = []
        for f in ['brk', 'clv', 'rng', 'vol', 'ext', 'base', 'mom', 'atrp']:
            vals = sorted(s[f] for s in a)
            for q in range(2, 19):
                thr = vals[int(len(vals) * q / 20)]
                for ig in (True, False):
                    n = sum(1 for s in a if (s[f] >= thr) == ig)
                    if n < 0.15 * len(a) or n > 0.85 * len(a): continue
                    cs.append((rule_pnl(a, f, thr, ig), f, thr, ig))
        cs.sort(reverse=True)
        print(f'\n■ {nm} 내부 규칙 상위 5 (기준 {nm}{"즉시" if side>0 else "대기"}: 학습 {base_a:,.0f} | 검증 {base_b:,.0f})')
        for p, f, thr, ig in cs[:5]:
            te = rule_pnl(bb, f, thr, ig)
            print(f'  {f}≥{thr:.3f} → {"즉시" if ig else "대기"} 그 외 반대 | 학습 {p:,.0f} ({p-base_a:+,.0f}) | 검증 {te:,.0f} ({te-base_b:+,.0f})')
