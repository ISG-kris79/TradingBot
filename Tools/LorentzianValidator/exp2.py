import warnings; warnings.filterwarnings('ignore')
from research import *
data = load_all()
E = evaluate
for ptp in (None, 3):
    tagp = '' if ptp is None else ' +3ATR 50%익절'
    print(f'— 묶음2{tagp}')
    E(f'기준{tagp}', [(donchian(data, ptp=ptp, tag='4h'), 2, 3, 3000)])
    for cd in (72, 168):
        E(f'손절 후 {cd//24}일 재진입금지{tagp}', [(donchian(data, ptp=ptp, cooldown_h=cd, tag='4h'), 2, 3, 3000)])
    for mb in (0.25, 0.5):
        E(f'돌파폭 ≥{mb}ATR{tagp}', [(donchian(data, ptp=ptp, min_brk=mb, tag='4h'), 2, 3, 3000)])
    for bm in (8, 12):
        E(f'직전횡보폭 ≤{bm}ATR{tagp}', [(donchian(data, ptp=ptp, base_max=bm, tag='4h'), 2, 3, 3000)])
