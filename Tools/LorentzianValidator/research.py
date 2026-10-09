# 7년(2019-09~2026-10) 월별 흑자 연구 하네스
#   데이터: 15m_170(2019-09~2023-09) + 15m_71(2023-09~) 이어붙임 · 30종목
#   평가: 슬롯 포트폴리오 + 동시신호 순서 무작위 N회 평균 · 월별 흑자비율(전체/전반/후반) · 최악월 · 낙폭 · 연도별 실현
import io, sys, math, random, collections, datetime, statistics as st, pickle, os

UNIV = ["BTCUSDT","ETHUSDT","XRPUSDT","BNBUSDT","SOLUSDT","DOGEUSDT","ADAUSDT","TRXUSDT","AVAXUSDT","LINKUSDT",
        "DOTUSDT","LTCUSDT","BCHUSDT","NEARUSDT","UNIUSDT","APTUSDT","ICPUSDT","ETCUSDT","FILUSDT","ARBUSDT",
        "OPUSDT","ATOMUSDT","SUIUSDT","AAVEUSDT","XLMUSDT","INJUSDT","ALGOUSDT","HBARUSDT","SEIUSDT","VETUSDT"]
MAJ = {"BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"}
SPLIT = int(datetime.datetime(2023, 9, 22, tzinfo=datetime.timezone.utc).timestamp() * 1000)
FEE, FUND8H = 0.0012, 0.0001
REAL_FUNDING = True    # True: 실제 펀딩비(롱 지불·숏 수취, 부호 반영) / False: 방향 무관 0.01%/8h 차감(보수적)
_FUND = {}
def funding_sum(sym, t0, t1):
    import bisect, os
    if sym not in _FUND:
        f = f'cache/funding_{sym}.csv'
        rows = [l.split(',') for l in open(f)] if os.path.exists(f) else []
        ts = [int(r[0]) for r in rows]; pref = [0.0]
        for r in rows: pref.append(pref[-1] + float(r[1]))
        _FUND[sym] = (ts, pref)
    ts, pref = _FUND[sym]
    if not ts: return None
    a = bisect.bisect_right(ts, t0); b = bisect.bisect_right(ts, t1)
    return pref[b] - pref[a]

M15 = 900_000

def load_all():
    pk = 'research_cache.pkl'
    if os.path.exists(pk): return pickle.load(open(pk, 'rb'))
    data = {}
    for sym in UNIV:
        rows = []
        for suf, lo, hi in (('15m_170', 0, SPLIT), ('15m_71', SPLIT, 1 << 62)):
            f = f'cache/{sym}_{suf}.csv'
            if os.path.exists(f):
                rows += [r for r in (l.split(',') for l in io.open(f, encoding='utf-8')) if lo <= int(r[0]) < hi]
        if len(rows) < 20000: continue
        data[sym] = tuple([int(r[0]) for r in rows]), tuple(float(r[1]) for r in rows), tuple(float(r[2]) for r in rows), \
                    tuple(float(r[3]) for r in rows), tuple(float(r[4]) for r in rows)
    pickle.dump(data, open(pk, 'wb'))
    return data

def agg(T, O, H, L, C, mins):
    W = mins * 60000; need = mins // 15
    b = []; end = []; i = 0; n = len(T)
    while i < n:
        bk = T[i] // W; j = i; h = H[i]; l = L[i]
        while j + 1 < n and T[j + 1] // W == bk: j += 1; h = max(h, H[j]); l = min(l, L[j])
        if j - i + 1 == need: b.append((bk * W, O[i], h, l, C[j])); end.append(j)
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

def donchian(data, tf=240, N=55, init=2.0, trail=5.0, chase=4.5, tight_after=None, tight=None, ptp=None, tag='', cooldown_h=0, min_brk=0.0, base_max=None, be_after=None, be_lock=0.0, tp_atr=None):
    """돈치안 돌파 거래 생성. tight_after=(수익 xATR 도달 시) 트레일을 tight×ATR 로 좁힘. ptp=(x ATR 도달 시 50% 익절).
       반환: [(sym, tin, tout, pnl_frac_net, tag)] — pnl 은 명목 1.0 기준 비율(수수료·펀딩 반영)."""
    out = []
    for sym, (T, O, H, L, C) in data.items():
        b, end = agg(T, O, H, L, C, tf); A = atr_s(b); E = ema_s([x[4] for x in b], 50)
        cool_until = {}
        lastclosed = []  # 15m j → 마지막 마감 tf 봉
        k = -1
        for j in range(len(T)):
            while k + 1 < len(end) and end[k + 1] < j: k += 1
            lastclosed.append(k)
        for i in range(N + 2, len(b) - 1):
            hi = max(x[2] for x in b[i - N:i]); lo = min(x[3] for x in b[i - N:i])
            hiP = max(x[2] for x in b[i - N - 1:i - 1]); loP = min(x[3] for x in b[i - N - 1:i - 1])
            c = b[i][4]
            d = 1 if (c > hi and b[i - 1][4] <= hiP) else -1 if (c < lo and b[i - 1][4] >= loP) else 0
            if d == 0 or A[i] <= 0: continue
            if chase and d * (c - E[i]) / A[i] >= chase: continue
            if min_brk and d * (c - (hi if d > 0 else lo)) / A[i] < min_brk: continue
            if base_max and (hi - lo) / A[i] > base_max: continue
            if cooldown_h and b[i][0] < cool_until.get(sym, 0): continue
            e0 = end[i] + 1
            if e0 >= len(T): continue
            entry = O[e0]; at = A[i]; stop = entry - d * init * at; kk = i; half = False; realized = 0.0; size = 1.0
            px = None
            for j in range(e0, len(T)):
                while kk < lastclosed[j]:
                    kk += 1
                    if A[kk] <= 0: continue
                    t_mult = trail
                    if tight_after and d * (b[kk][4] - entry) >= tight_after * at: t_mult = tight
                    ns = b[kk][4] - d * t_mult * A[kk]
                    if (d > 0 and ns > stop) or (d < 0 and ns < stop): stop = ns
                if tp_atr:
                    tpx = entry + d * tp_atr * at
                    if (d > 0 and O[j] >= tpx) or (d < 0 and O[j] <= tpx): px = O[j]; break
                    if (d > 0 and H[j] >= tpx) or (d < 0 and L[j] <= tpx):
                        if not ((d > 0 and L[j] <= stop) or (d < 0 and H[j] >= stop)): px = tpx; break
                if ptp and not half:
                    tp = entry + d * ptp * at
                    if (d > 0 and H[j] >= tp) or (d < 0 and L[j] <= tp):
                        realized += 0.5 * d * (tp - entry) / entry; size = 0.5; half = True
                if (d > 0 and O[j] <= stop) or (d < 0 and O[j] >= stop): px = O[j]; break
                if (d > 0 and L[j] <= stop) or (d < 0 and H[j] >= stop): px = stop; break
                # 본절 전환: 이 15m 봉에서 수익이 be_after×ATR 도달 → 다음 봉부터 손절선 = 진입 ± be_lock×ATR
                if be_after and d * ((H[j] if d > 0 else L[j]) - entry) >= be_after * at:
                    bs = entry + d * be_lock * at
                    if (d > 0 and bs > stop) or (d < 0 and bs < stop): stop = bs
            if px is None: continue                                             # 미청산은 제외(실현만)
            hours = (T[j] - T[e0]) / 3600000
            fund = FUND8H * hours / 8
            if REAL_FUNDING:
                fs = funding_sum(sym, T[e0], T[j])
                if fs is not None: fund = d * fs * (0.5 + 0.5 * size if half else 1.0)   # 롱은 +rate 지불, 숏은 수취
            pnl = realized + size * d * (px - entry) / entry - FEE - fund
            out.append((sym, T[e0], T[j] + M15, pnl, tag, d))
            if cooldown_h and pnl < 0: cool_until[sym] = T[j] + cooldown_h * 3600000
    return out

def portfolio(trades, slots_maj, slots_alt, notional, order):
    taken = []; open_ = []; busy = {}
    for t in sorted(trades, key=lambda x: (x[1], order[x[0]])):
        open_ = [o for o in open_ if o[2] > t[1]]
        key = (t[0], t[4])
        if busy.get(key, 0) > t[1]: continue
        mj = t[0] in MAJ
        if sum(1 for o in open_ if (o[0] in MAJ) == mj and o[4] == t[4]) >= (slots_maj if mj else slots_alt): continue
        open_.append(t); taken.append((t[0], t[1], t[2], t[3] * notional, t[4]) + tuple(t[5:])); busy[key] = t[2]
    return taken

def month_key(ms): return datetime.datetime.fromtimestamp(ms / 1000 + 9 * 3600, datetime.timezone.utc).strftime('%Y-%m')

def evaluate(name, sleeves, n_mc=20, show=True):
    """sleeves: [(trades, slots_maj, slots_alt, notional)] — 슬리브별 슬롯 독립, 합산 평가"""
    res = []
    for seed in range(n_mc):
        random.seed(seed); sh = list(UNIV); random.shuffle(sh); order = {s: k for k, s in enumerate(sh)}
        tk = []
        for tr, sm, sa, nt in sleeves: tk += portfolio(tr, sm, sa, nt, order)
        m = collections.defaultdict(float)
        for t in tk: m[month_key(t[2])] += t[3]
        months = sorted(m)
        allm = [m[k] for k in months]
        first = [m[k] for k in months if k < '2023-10']; second = [m[k] for k in months if k >= '2023-10']
        last3 = [m[k] for k in months if k >= '2023-10']; last5 = [m[k] for k in months if k >= '2021-10']
        cum = pk = mdd = 0
        for t in sorted(tk, key=lambda x: x[2]): cum += t[3]; pk = max(pk, cum); mdd = min(mdd, cum - pk)
        yr = collections.defaultdict(float)
        for k in months: yr[k[:4]] += m[k]
        streak = mx = 0
        for v in allm:
            streak = streak + 1 if v <= 0 else 0; mx = max(mx, streak)
        res.append(dict(maxstreak=mx, lossyears=sum(1 for v in yr.values() if v <= 0), tot=sum(allm), pos=sum(1 for v in allm if v > 0) / len(allm), pos1=sum(1 for v in first if v > 0) / max(1, len(first)),
                        pos2=sum(1 for v in second if v > 0) / max(1, len(second)), t3=sum(last3), t5=sum(last5), worst=min(allm), mdd=mdd,
                        n=len(tk), yr=yr, nm=len(allm)))
    a = lambda k: st.mean(r[k] for r in res)
    yrs = sorted(res[0]['yr'])
    if show:
        print(f'{name:44} 7년 {a("tot"):9,.0f}$(±{st.pstdev(r["tot"] for r in res):6,.0f}) 5년 {a("t5"):8,.0f}$ 3년 {a("t3"):8,.0f}$ | '
              f'연속적자월 최대 {a("maxstreak"):.1f} · 적자연도 {a("lossyears"):.1f} · 흑자월 {a("pos"):.0%} 최악월 {a("worst"):7,.0f}$ 낙폭 {a("mdd"):8,.0f}$ 체결 {a("n"):.0f} | '
              + ' '.join(f'{y}:{st.mean(r["yr"].get(y, 0) for r in res)/1000:+.1f}k' for y in yrs))
    return res

if __name__ == '__main__':
    data = load_all()
    print(f'데이터 {len(data)}종목\n')
    base = donchian(data, tag='4h')
    evaluate('기준: 4h N55 · 슬롯2/3 · $3000', [(base, 2, 3, 3000)])
