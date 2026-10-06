# 목표: 연속 적자월 ≤1 · 적자연도 0 — 넓은 격자 탐색 (7년, 실제 펀딩비, 순서 무작위 10회)
import warnings; warnings.filterwarnings('ignore')
import itertools, statistics as st
from research import *
data = load_all()
rows = []
grid = list(itertools.product((240, 1440), (20, 55, 100), (3.0, 5.0, 8.0), (None, 2, 3, 5), (0, 168)))
for k, (tf, N, tr, ptp, cd) in enumerate(grid):
    t = donchian(data, tf=tf, N=N, trail=tr, ptp=ptp, cooldown_h=cd, tag='x')
    r = evaluate('', [(t, 2, 3, 3000)], n_mc=10, show=False)
    a = lambda key: st.mean(x[key] for x in r)
    yrs = sorted(r[0]['yr']); ym = {y: st.mean(x['yr'].get(y, 0) for x in r) for y in yrs}
    rows.append((a('maxstreak'), a('lossyears'), a('pos'), a('tot'), a('mdd'), tf, N, tr, ptp, cd, ym))
    print(f'[{k+1}/{len(grid)}] {"4h" if tf==240 else "1d"} N{N} 트레일{tr} 부분익절{ptp} 재진입금지{cd//24}일 → 연속적자 {a("maxstreak"):.1f} 적자연도 {a("lossyears"):.1f} 흑자월 {a("pos"):.0%} 7년 {a("tot"):,.0f}$', flush=True)
rows.sort(key=lambda x: (x[0], x[1], -x[3]))
print('\n■ 목표 근접 상위 15 (연속적자월↑ → 적자연도↑ → 수익↓ 순)')
for r in rows[:15]:
    print(f'  {"4h" if r[5]==240 else "1d"} N{r[6]} 트레일{r[7]} 부분익절{r[8]} 재진입금지{r[9]//24}일 | 연속적자월 {r[0]:.1f} 적자연도 {r[1]:.1f} 흑자월 {r[2]:.0%} 7년 {r[3]:,.0f}$ 낙폭 {r[4]:,.0f}$ | ' + ' '.join(f'{y}:{v/1000:+.1f}k' for y, v in r[10].items()))
ok = [r for r in rows if r[0] <= 1.0 and r[1] == 0]
print(f'\n목표 충족(연속적자월≤1 & 적자연도 0): {len(ok)}개')
