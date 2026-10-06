import warnings; warnings.filterwarnings('ignore')
import research as R
from research import *
data = load_all()
E = evaluate
for rf in (False, True):
    R.REAL_FUNDING = rf
    nm = '실제 펀딩비' if rf else '가정 펀딩비(0.01%/8h 양방향 차감)'
    print(f'— {nm}')
    E('현행', [(donchian(data, tag='4h'), 2, 3, 3000)])
    E('최선후보: 3ATR 50%익절 + 7일 재진입금지', [(donchian(data, ptp=3, cooldown_h=168, tag='4h'), 2, 3, 3000)])
# 실제 펀딩비 연평균 (전체 코인)
import statistics as st, datetime, collections
yr = collections.defaultdict(list)
for s in UNIV:
    try:
        for l in open(f'cache/funding_{s}.csv'):
            t, v = l.split(','); yr[datetime.datetime.utcfromtimestamp(int(t)/1000).year].append(float(v))
    except FileNotFoundError: pass
print('연도별 평균 펀딩비 연환산(롱이 내는 비용):', ' '.join(f'{y}:{st.mean(v)*3*365*100:+.1f}%' for y, v in sorted(yr.items())))
