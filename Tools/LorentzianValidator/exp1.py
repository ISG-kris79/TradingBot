import warnings; warnings.filterwarnings('ignore')
from research import *
data = load_all()
base = donchian(data, tag='4h')
E = evaluate
E('기준: 4h · 5건×$3000', [(base, 2, 3, 3000)])
print('— 가설1 분산 (총 노출 $15,000 동일)')
E('4h · 10건×$1500 (4/6)', [(base, 4, 6, 1500)])
E('4h · 15건×$1000 (6/9)', [(base, 6, 9, 1000)])
print('— 가설2 이익 확정(수익 xATR 이후 트레일 좁힘)')
for ta, tt in ((3, 2), (5, 3), (8, 3)):
    E(f'4h · 수익 {ta}ATR 후 트레일 {tt}ATR', [(donchian(data, tight_after=ta, tight=tt, tag='4h'), 2, 3, 3000)])
print('— 가설4 부분익절 50%')
for p in (3, 5):
    E(f'4h · {p}ATR 에서 50% 익절', [(donchian(data, ptp=p, tag='4h'), 2, 3, 3000)])
print('— 가설3 타임프레임 병행 (슬리브별 독립 슬롯, 총 노출 동일)')
h1 = donchian(data, tf=60, tag='1h'); d1 = donchian(data, tf=1440, N=20, tag='1d')
E('1h 단독 · 5건×$3000', [(h1, 2, 3, 3000)])
E('1d N20 단독 · 5건×$3000', [(d1, 2, 3, 3000)])
E('1h+4h+1d 각 5건×$1000', [(h1, 2, 3, 1000), (base, 2, 3, 1000), (d1, 2, 3, 1000)])
E('1h+4h+1d 각 10건×$500', [(h1, 4, 6, 500), (base, 4, 6, 500), (d1, 4, 6, 500)])
