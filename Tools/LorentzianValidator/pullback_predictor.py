# 4h 돈치안 신호 직후 "눌림이 올지" 예측 → 눌림 예측이면 지정가 대기(4h 안 체결 안 되면 시장가), 아니면 즉시 진입
#   학습 2019-09~2023-09 (15m_170) · 검증 2023-09~2026-10 (15m_71, 학습 미사용)
import io, datetime, math
import numpy as np
from decision_logic import UNIV, SPLIT, W4, load, agg, atr_s, ema_s, sim

K_DIP = 0.5      # 눌림 기준: 신호봉 종가 대비 0.5×ATR(4h)
WIN = 16         # 대기 4h (15m × 16)

CHASE = True
def build(suffix, lo, hi):
    rows = []
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
            if CHASE and d * (c - E50[i]) / at >= 4.5: continue
            e0 = end[i] + 1
            if e0 + WIN >= len(T) - 1: continue
            level = hi55 if d > 0 else lo55
            imm = sim(T, O, H, L, C, b, end, A, i, e0, d, O[e0])
            if imm is None: continue
            # 지정가(종가∓0.5ATR) 4h 대기 → 미체결이면 4h 후 시장가
            lim = c - d * K_DIP * at; fill = None
            for j in range(e0, e0 + WIN):
                if (d > 0 and L[j] <= lim) or (d < 0 and H[j] >= lim):
                    px = min(lim, O[j]) if d > 0 else max(lim, O[j]); fill = sim(T, O, H, L, C, b, end, A, i, j, d, px); break
            dip = fill is not None
            ein = j if dip else e0 + WIN
            if fill is None: fill = sim(T, O, H, L, C, b, end, A, i, e0 + WIN, d, O[e0 + WIN])
            if fill is None: continue
            vavg = sum(x[5] for x in b[i - 20:i]) / 20; rngb = b[i][2] - b[i][3]
            j1 = end[i]                                               # 신호봉 마지막 15m
            f = [d * (c - level) / at,
                 ((c - b[i][3]) / rngb if d > 0 else (b[i][2] - c) / rngb) if rngb > 0 else 0.5,
                 rngb / at, math.log(max(1e-3, b[i][5] / vavg)) if vavg > 0 else 0,
                 d * (c - E50[i]) / at, (hi55 - lo55) / at, d * (c - b[i - 6][4]) / at, at / c * 100, d,
                 d * (C[j1] - C[j1 - 4]) / at,                         # 신호봉 마지막 1h 흐름
                 ((C[j1] - L[j1]) / (H[j1] - L[j1]) if d > 0 else (H[j1] - C[j1]) / (H[j1] - L[j1])) if H[j1] > L[j1] else 0.5,  # 마지막 15m 마감위치
                 d * (b[i][4] - b[i][1]) / at]                         # 신호봉 몸통
            rows.append(dict(f=f, dip=dip, imm=imm[0], lim=fill[0], d=d, sym=sym,
                             imm_in=T[e0], imm_out=T[imm[1]] + 900000, lim_in=T[ein], lim_out=T[fill[1]] + 900000))
    return rows

def fit_logreg(X, y, l2=1.0, it=3000, lr=0.1):
    w = np.zeros(X.shape[1]); b0 = 0.0
    for _ in range(it):
        z = X @ w + b0; p = 1 / (1 + np.exp(-z))
        g = X.T @ (p - y) / len(y) + l2 * w / len(y); gb = (p - y).mean()
        w -= lr * g; b0 -= lr * gb
    return w, b0

def auc(p, y):
    o = np.argsort(p); r = np.empty(len(p)); r[o] = np.arange(1, len(p) + 1)
    pos = y.sum(); neg = len(y) - pos
    return (r[y == 1].sum() - pos * (pos + 1) / 2) / (pos * neg)

if __name__ == '__main__':
    TR = build('15m_170', 0, SPLIT); TE = build('15m_71', SPLIT, 1 << 62)
    Xtr = np.array([r['f'] for r in TR]); Xte = np.array([r['f'] for r in TE])
    mu, sd = Xtr.mean(0), Xtr.std(0) + 1e-9; Xtr = (Xtr - mu) / sd; Xte = (Xte - mu) / sd
    ytr = np.array([r['dip'] for r in TR], float); yte = np.array([r['dip'] for r in TE], float)
    w, b0 = fit_logreg(Xtr, ytr)
    ptr = 1 / (1 + np.exp(-(Xtr @ w + b0))); pte = 1 / (1 + np.exp(-(Xte @ w + b0)))
    print(f'신호: 학습 {len(TR)} · 검증 {len(TE)}  | 4h 안 0.5ATR 눌림 발생률: 학습 {ytr.mean():.0%} · 검증 {yte.mean():.0%}')
    print(f'눌림 예측 정확도 AUC: 학습 {auc(ptr, ytr):.3f} · 검증 {auc(pte, yte):.3f}  (0.5=동전던지기, 1.0=완벽)')
    names = ['돌파강도','마감위치','봉크기','거래량(log)','과열','횡보폭','24h모멘텀','ATR%','방향','마지막1h흐름','마지막15m마감위치','몸통']
    print('  가중치(+면 눌림 가능성↑): ' + ' · '.join(f'{n} {v:+.2f}' for n, v in sorted(zip(names, w), key=lambda t: -abs(t[1]))))
    for nm, R, P in (('학습 2019~23', TR, ptr), ('검증 2023~26', TE, pte)):
        imm = sum(r['imm'] for r in R); lim = sum(r['lim'] for r in R)
        print(f'\n[{nm}] (슬롯 없이 신호별 합계) 항상즉시 {imm:,.0f}$ · 항상 지정가대기→4h후 시장가 {lim:,.0f}$ ({lim-imm:+,.0f})')
        for tau in (0.4, 0.5, 0.6, 0.7):
            pol = sum(r['lim'] if p >= tau else r['imm'] for r, p in zip(R, P))
            n = sum(1 for p in P if p >= tau)
            print(f'   예측기: 눌림확률≥{tau:.1f} 이면 지정가대기({n}건) 아니면 즉시 → {pol:,.0f}$ ({pol-imm:+,.0f})')


MAJ = {"BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"}
import random
ORDER = {s: k for k, s in enumerate(UNIV)}
def portfolio(trades):
    """trades: (sym, tin, tout, pnl) — C# StrategyLab.Portfolio 와 같은 규칙(심볼당 1 · 메이저2/알트3)"""
    taken = []; open_ = []; busy = {}
    for t in sorted(trades, key=lambda x: (x[1], ORDER[x[0]])):
        open_ = [o for o in open_ if o[2] > t[1]]
        if busy.get(t[0], 0) > t[1]: continue
        mj = t[0] in MAJ
        if sum(1 for o in open_ if (o[0] in MAJ) == mj) >= (2 if mj else 3): continue
        open_.append(t); taken.append(t); busy[t[0]] = t[2]
    return taken

def report(nm, taken, lastT):
    import collections
    tot = sum(t[3] for t in taken)
    real = [t for t in taken if t[2] <= lastT]
    m = collections.defaultdict(float)
    for t in real: m[datetime.datetime.utcfromtimestamp(t[2] / 1000 + 9 * 3600).strftime('%Y-%m')] += t[3]
    cut = lastT - 21 * 30 * 86400000
    r21 = sum(t[3] for t in real if t[2] >= cut)
    cum = pk = mdd = 0
    for t in sorted(taken, key=lambda x: x[2]): cum += t[3]; pk = max(pk, cum); mdd = min(mdd, cum - pk)
    print(f'   {nm:34} 체결 {len(taken):4} · 합계 {tot:9,.0f}$ · 낙폭 {mdd:8,.0f}$ · 흑자월 {sum(1 for v in m.values() if v > 0)}/{len(m)} · 최근21개월 {r21:+8,.0f}$')

if __name__ == '__main__':
    print('■ 슬롯 포트폴리오 (메이저2 · 알트3)')
    for nm, R, P in (('학습 2019~23', TR, ptr), ('검증 2023~26', TE, pte)):
        lastT = max(r['imm_out'] for r in R)
        print(f'[{nm}]')
        report('항상 즉시 (현행, C# 순서)', portfolio([(r['sym'], r['imm_in'], r['imm_out'], r['imm']) for r in R]), lastT)
        saved = dict(ORDER)
        for seed in range(5):
            random.seed(seed); sh = list(UNIV); random.shuffle(sh); ORDER.clear(); ORDER.update({s: k for k, s in enumerate(sh)})
            report(f'항상 즉시 · 동시신호 순서 무작위 #{seed}', portfolio([(r['sym'], r['imm_in'], r['imm_out'], r['imm']) for r in R]), lastT)
        ORDER.clear(); ORDER.update(saved)
        report('항상 지정가대기→4h후 시장가', portfolio([(r['sym'], r['lim_in'], r['lim_out'], r['lim']) for r in R]), lastT)
        for tau in (0.45, 0.5, 0.55):
            tr = [(r['sym'], r['lim_in'], r['lim_out'], r['lim']) if p >= tau else (r['sym'], r['imm_in'], r['imm_out'], r['imm']) for r, p in zip(R, P)]
            report(f'예측기 눌림확률≥{tau}', portfolio(tr), lastT)

def mc(trades, lastT, n=50):
    import statistics as st
    tots, r21s, mdds, posm = [], [], [], []
    saved = dict(ORDER)
    for seed in range(n):
        random.seed(1000 + seed); sh = list(UNIV); random.shuffle(sh); ORDER.clear(); ORDER.update({s: k for k, s in enumerate(sh)})
        tk = portfolio(trades)
        real = [t for t in tk if t[2] <= lastT]; cut = lastT - 21 * 30 * 86400000
        tots.append(sum(t[3] for t in real)); r21s.append(sum(t[3] for t in real if t[2] >= cut))
        cum = pk = mdd = 0
        for t in sorted(tk, key=lambda x: x[2]): cum += t[3]; pk = max(pk, cum); mdd = min(mdd, cum - pk)
        mdds.append(mdd)
        import collections
        m = collections.defaultdict(float)
        for t in real: m[t[2] // (30 * 86400000)] += t[3]
        posm.append(sum(1 for v in m.values() if v > 0) / max(1, len(m)))
    ORDER.clear(); ORDER.update(saved)
    return st.mean(tots), st.pstdev(tots), st.mean(r21s), st.mean(mdds), st.mean(posm)

if __name__ == '__main__':
    print('\n■ 동시신호 순서 무작위 50회 평균 (실현손익 · 표준편차 · 최근21개월 · 낙폭 · 흑자월 비율)')
    for nm, R, P in (('학습 2019~23', TR, ptr), ('검증 2023~26', TE, pte)):
        lastT = max(r['imm_out'] for r in R)
        print(f'[{nm}]')
        pols = [('항상 즉시 (현행)', lambda r, p: 'imm'), ('항상 지정가대기→4h후 시장가', lambda r, p: 'lim')]
        for tau in (0.45, 0.5, 0.55, 0.6): pols.append((f'예측기 눌림확률≥{tau}', (lambda t: (lambda r, p: 'lim' if p >= t else 'imm'))(tau)))
        for pn, fn in pols:
            tr = []
            for r, p in zip(R, P):
                k = fn(r, p); tr.append((r['sym'], r[k + '_in'], r[k + '_out'], r[k]))
            a, sd, r21, mdd, pm = mc(tr, lastT)
            print(f'   {pn:30} 실현 {a:9,.0f}$ (±{sd:6,.0f}) · 최근21개월 {r21:+8,.0f}$ · 낙폭 {mdd:8,.0f}$ · 흑자월 {pm:.0%}')
