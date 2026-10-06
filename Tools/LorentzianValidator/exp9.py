import warnings; warnings.filterwarnings('ignore')
from research import *
data = load_all()
E = lambda n, s: evaluate(n, s, n_mc=20)
A = donchian(data, tf=240, N=100, trail=3.0, ptp=5, cooldown_h=168, tag='A')
E('후보A: 4h N100 T3 P5 CD7', [(A, 2, 3, 3000)])
print('— 주변값 (하나씩 변경)')
for kw, nm in ((dict(N=80), 'N80'), (dict(N=120), 'N120'), (dict(trail=2.5), 'T2.5'), (dict(trail=3.5), 'T3.5'),
               (dict(ptp=4), 'P4'), (dict(ptp=6), 'P6'), (dict(cooldown_h=120), 'CD5'), (dict(cooldown_h=240), 'CD10')):
    p = dict(tf=240, N=100, trail=3.0, ptp=5, cooldown_h=168); p.update(kw)
    E(f'  {nm}', [(donchian(data, tag='A', **p), 2, 3, 3000)])
print('— 분산 조합')
B = donchian(data, tf=1440, N=55, trail=5.0, ptp=2, cooldown_h=168, tag='B')
E('후보B: 1d N55 T5 P2 CD7', [(B, 2, 3, 3000)])
E('A+B 각 슬롯2/3 · 각 $1500', [(A, 2, 3, 1500), (B, 2, 3, 1500)])
E('A+B 각 슬롯2/3 · 각 $3000', [(A, 2, 3, 3000), (B, 2, 3, 3000)])
