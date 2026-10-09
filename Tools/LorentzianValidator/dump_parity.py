# 라이브 대조용 덤프: 슬리브 A/B 의 (최근 15m_71 구간) 모든 신호 + 손절 청산가 — DonchianParity(C#) 가 읽는다
import warnings; warnings.filterwarnings('ignore')
import io, datetime
from research import *
data = load_all()
for tag, kw in (('A', dict(tf=240, N=100, trail=3.0, ptp=5, cooldown_h=0)), ('B', dict(tf=1440, N=55, trail=5.0, ptp=2, cooldown_h=0))):
    tr = donchian(data, tag=tag, **kw)
    with open(f'parity_{tag}.csv', 'w') as f:
        f.write('sym,dir,sigOpenMs,entryMs,exitMs,entry,exitPx\n')
        for t in tr:
            if t[9] < SPLIT: continue
            f.write(f'{t[0]},{t[5]},{t[9]},{t[1]},{t[2]},{t[6]!r},{t[7]!r}\n')
    print(tag, sum(1 for t in tr if t[9] >= SPLIT))
