# 돈치안 손실 해부 — 진입 시점 조건별 성과 (두 기간 비교). 사후 분석용 스크립트.
import csv, io, sys, math, collections, datetime

W = 4 * 3600_000

def load4h(path):
    rows = [l.split(',') for l in io.open(path, encoding='utf-8').read().splitlines()]
    out = []; i = 0
    while i < len(rows):
        t0 = int(rows[i][0]); bk = t0 // W; j = i
        h = float(rows[i][2]); l = float(rows[i][3]); v = float(rows[i][5])
        while j + 1 < len(rows) and int(rows[j + 1][0]) // W == bk:
            j += 1; h = max(h, float(rows[j][2])); l = min(l, float(rows[j][3])); v += float(rows[j][5])
        if j - i + 1 == 16:
            out.append((bk * W, float(rows[i][1]), h, l, float(rows[j][4]), v))
        i = j + 1
    return out

def ema(x, p):
    r = []; a = 2 / (p + 1)
    for i, v in enumerate(x): r.append(v if i == 0 else a * v + (1 - a) * r[-1])
    return r

def atr(b, p=14):
    r = [0.0] * len(b); a = 0.0
    for i in range(1, len(b)):
        tr = max(b[i][2] - b[i][3], abs(b[i][2] - b[i - 1][4]), abs(b[i][3] - b[i - 1][4]))
        a = a + tr / p if i <= p else (a * (p - 1) + tr) / p
        r[i] = a if i >= p else 0
    return r

def analyze(trades_csv, suffix, label):
    tr = [l.split(',') for l in io.open(trades_csv, encoding='utf-8').read().splitlines()[1:]]
    allsig = collections.Counter(x[3] for x in tr)                     # 같은 4h 마감에 난 신호 수(체결/거절 포함)
    taken = [x for x in tr if x[0] == 'TAKEN']
    cache = {}
    feats = []
    for x in taken:
        sym = x[1]
        if sym not in cache:
            b = load4h(f'cache/{sym}_{suffix}.csv')
            c = [r[4] for r in b]
            # 일봉: 4h 6개 묶음(UTC 00시 기준)
            day = collections.OrderedDict()
            for r in b: day.setdefault(r[0] // 86400000, []).append(r)
            dk = [k for k, v in day.items() if len(v) == 6]
            dc = [day[k][-1][4] for k in dk]
            cache[sym] = (b, {r[0]: i for i, r in enumerate(b)}, atr(b), ema(c, 50), ema(c, 200), dk, ema(dc, 50), [sum(r[5] for r in b[max(0, i - 20):i]) / 20 if i >= 20 else 0 for i in range(len(b))])
        b, idx, A, E50, E200, dk, DE50, VAVG = cache[sym]
        tin = int(datetime.datetime.strptime(x[3], '%Y-%m-%d %H:%M').replace(tzinfo=datetime.timezone.utc).timestamp() * 1000)
        i = idx.get(tin - W)
        if i is None or i < 60 or A[i] <= 0: continue
        d = 1 if x[2] == 'L' else -1
        c = b[i][4]
        hi = max(r[2] for r in b[i - 55:i]); lo = min(r[3] for r in b[i - 55:i])
        brk = ((c - hi) if d > 0 else (lo - c)) / A[i]
        ext = d * (c - E50[i]) / A[i]
        trend200 = d * (c - E200[i]) / A[i]
        rng = (b[i][2] - b[i][3]) / A[i]
        atrp = A[i] / c * 100
        vol = b[i][5] / VAVG[i] if VAVG[i] > 0 else 0
        feats.append(dict(sym=sym, d=d, pnl=float(x[8]), how=x[7], tin=x[3], tout=x[4], brk=brk, ext=ext, t200=trend200,
                          rng=rng, atrp=atrp, vol=vol, clu=allsig[x[3]], hold=(datetime.datetime.strptime(x[4], '%Y-%m-%d %H:%M') - datetime.datetime.strptime(x[3], '%Y-%m-%d %H:%M')).total_seconds() / 3600))
    return feats

def bucket(feats, key, edges, label):
    out = []
    for lo, hi in zip(edges[:-1], edges[1:]):
        g = [f for f in feats if lo <= f[key] < hi]
        if not g: out.append(f'{lo:>6}~{hi:<6} n=0'); continue
        tot = sum(f['pnl'] for f in g); w = sum(1 for f in g if f['pnl'] > 0)
        out.append(f'{lo:>6}~{hi:<6} n={len(g):4} 승률 {100*w/len(g):4.0f}% 합계 {tot:9.0f}$ 평균 {tot/len(g):6.0f}$')
    return out

if __name__ == '__main__':
    R = analyze('trades-recent.csv', '15m_71', '2023~2026')
    P = analyze('trades-pre.csv', '15m_170', '2019~2023')
    print(f'분석 거래: 2023~2026 {len(R)}건 · 2019~2023 {len(P)}건\n')
    specs = [
        ('brk', [-1, 0.1, 0.3, 0.6, 1.0, 2.0, 99], '돌파 강도 (종가-55봉고점)/ATR'),
        ('ext', [-99, 2, 4, 6, 8, 99], '추격 정도 (종가-EMA50)/ATR'),
        ('t200', [-99, 0, 5, 10, 20, 99], '장기추세 (종가-EMA200)/ATR, +면 같은 방향'),
        ('rng', [0, 1, 1.5, 2, 3, 99], '돌파봉 크기 (고저폭/ATR)'),
        ('atrp', [0, 1, 1.5, 2, 3, 99], '변동성 ATR/가격 %'),
        ('vol', [0, 0.8, 1.2, 2, 3, 999], '돌파봉 거래량 / 직전20봉 평균'),
        ('clu', [1, 2, 4, 7, 999], '같은 4h 마감에 동시 신호 수'),
    ]
    for key, edges, name in specs:
        print(f'■ {name}')
        a = bucket(R, key, edges, ''); b = bucket(P, key, edges, '')
        for x, y in zip(a, b): print(f'  2023~26 {x:58} | 2019~23 {y[14:]}')
        print()
    for nm, F in (('2023~2026', R), ('2019~2023', P)):
        print(f'■ {nm} 방향별')
        for d, s in ((1, '롱'), (-1, '숏')):
            g = [f for f in F if f['d'] == d]; print(f'  {s} n={len(g)} 합계 {sum(f["pnl"] for f in g):.0f}$ 승률 {100*sum(1 for f in g if f["pnl"]>0)/max(1,len(g)):.0f}%')
    print('\n■ 2023~2026 최대 손실 15건')
    for f in sorted(R, key=lambda f: f['pnl'])[:15]:
        print(f"  {f['sym']:9} {'롱' if f['d']>0 else '숏'} {f['tin']} → {f['tout']} {f['pnl']:7.0f}$ 보유{f['hold']:5.0f}h 돌파{f['brk']:4.2f} 추격{f['ext']:4.1f} 장기{f['t200']:5.1f} 봉크기{f['rng']:4.1f} ATR{f['atrp']:4.1f}% 거래량{f['vol']:4.1f}x 동시{f['clu']}")
