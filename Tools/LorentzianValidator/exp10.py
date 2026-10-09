# 일 단위 검증: 연속 손실일 · 최악의 날 · 일손실 방어(다음날 진입중단) · 손실 후 만회 사이징(마틴/역마틴)
#   일손익 = 그날(KST) 청산된 거래의 실현손익 합. 청산 없는 날은 0(손실일 아님).
import warnings; warnings.filterwarnings('ignore')
import random, collections, statistics as st, datetime
from research import *

def kday(ms): return datetime.datetime.fromtimestamp(ms / 1000 + 9 * 3600, datetime.timezone.utc).strftime('%Y-%m-%d')

def sim(sleeves, order, rule=None, loss_limit=None, mult=1.0, cap=3.0):
    """sleeves: [(trades, sm, sa, notional)] — 이벤트 순 시뮬. 진입 시점에 '전날 실현손익'을 보고 크기/진입 결정.
       rule: None | 'martin'(전날 손실이면 ×mult, 연속 손실마다 누적, 상한 cap) | 'anti'(전날 손실이면 ×mult, mult<1)
       loss_limit: 전날 실현손실이 이 값 이하(-$)면 오늘 신규 진입 중단"""
    ev = []
    for si, (tr, sm, sa, nt) in enumerate(sleeves):
        for t in tr: ev.append((t[1], order[t[0]], si, t))
    ev.sort()
    open_ = []; busy = {}; closed = []; daypnl = collections.defaultdict(float); streak_scale = 1.0
    last_day_seen = None
    for tin, _, si, t in ev:
        # 진입 시점 이전에 청산된 거래 반영
        still = []
        for o in open_:
            if o['tout'] <= tin: daypnl[kday(o['tout'])] += o['pnl']; closed.append(o)
            else: still.append(o)
        open_ = still
        today = kday(tin)
        prev = (datetime.date.fromisoformat(today) - datetime.timedelta(days=1)).isoformat()
        pd = daypnl.get(prev, 0.0)
        if loss_limit is not None and pd <= -loss_limit: continue
        if today != last_day_seen:
            if rule == 'martin': streak_scale = min(cap, streak_scale * mult) if pd < 0 else 1.0
            elif rule == 'anti': streak_scale = mult if pd < 0 else 1.0
            last_day_seen = today
        tr, sm, sa, nt = sleeves[si]
        key = (t[0], si)
        if busy.get(key, 0) > tin: continue
        mj = t[0] in MAJ
        if sum(1 for o in open_ if (o['sym'] in MAJ) == mj and o['si'] == si) >= (sm if mj else sa): continue
        o = dict(sym=t[0], si=si, tout=t[2], pnl=t[3] * nt * streak_scale)
        open_.append(o); busy[key] = t[2]
    for o in open_: daypnl[kday(o['tout'])] += o['pnl']
    return daypnl

def stats(name, sleeves, n=20, **kw):
    R = []
    for seed in range(n):
        random.seed(seed); sh = list(UNIV); random.shuffle(sh); order = {s: k for k, s in enumerate(sh)}
        dp = sim(sleeves, order, **kw)
        days = sorted(dp); vals = [dp[d] for d in days]
        act = [v for v in vals if v != 0]
        # 연속 손실일(청산 있는 날 기준 연속, 무청산일은 끊지 않음)
        s = mx = 0
        for v in act: s = s + 1 if v < 0 else 0; mx = max(mx, s)
        # 달력상 연속 손실일
        cal = collections.defaultdict(float)
        for d in days: cal[d] += dp[d]
        s2 = mx2 = 0; d0 = datetime.date.fromisoformat(days[0]); d1 = datetime.date.fromisoformat(days[-1]); d = d0
        while d <= d1:
            v = cal.get(d.isoformat(), 0.0); s2 = s2 + 1 if v < 0 else 0; mx2 = max(mx2, s2); d += datetime.timedelta(days=1)
        cum = pk = mdd = 0
        for v in vals: cum += v; pk = max(pk, cum); mdd = min(mdd, cum - pk)
        yr = collections.defaultdict(float)
        for d in days: yr[d[:4]] += dp[d]
        R.append(dict(tot=sum(vals), posd=sum(1 for v in act if v > 0) / len(act), mx=mx, mx2=mx2, worst=min(vals), mdd=mdd,
                      ly=sum(1 for v in yr.values() if v <= 0), y26=yr.get('2026', 0)))
    a = lambda k: st.mean(r[k] for r in R)
    print(f'{name:40} 7년 {a("tot"):9,.0f}$ | 흑자일 {a("posd"):.0%} · 연속손실(달력일) 최대 {a("mx2"):.1f}일 · 연속손실(청산일 기준) {a("mx"):.1f} · 최악의날 {a("worst"):7,.0f}$ · 낙폭 {a("mdd"):8,.0f}$ · 적자연도 {a("ly"):.1f} · 2026 {a("y26"):+7,.0f}$')

if __name__ == '__main__':
    data = load_all()
    A = donchian(data, tf=240, N=100, trail=3.0, ptp=5, cooldown_h=168, tag='A')
    B = donchian(data, tf=1440, N=55, trail=5.0, ptp=2, cooldown_h=168, tag='B')
    cur = donchian(data, tag='cur')
    AB = [(A, 2, 3, 1500), (B, 2, 3, 1500)]
    print('■ 일 단위 기준 점검')
    stats('현행 (4h N55)', [(cur, 2, 3, 3000)])
    stats('A+B 각 $1500', AB)
    print('■ 큰 손실 방어: 전날 실현손실 ≥ X 이면 오늘 신규진입 중단')
    for X in (300, 600, 1000):
        stats(f'A+B + 일손실 ${X} 이상 다음날 중단', AB, loss_limit=X)
    print('■ 만회 사이징 (전날 손실이면 다음날 크기 조정)')
    stats('마틴: 손실 다음날 ×1.5 누적(최대 3배)', AB, rule='martin', mult=1.5, cap=3.0)
    stats('마틴: 손실 다음날 ×2 누적(최대 4배)', AB, rule='martin', mult=2.0, cap=4.0)
    stats('역마틴: 손실 다음날 ×0.5', AB, rule='anti', mult=0.5)
