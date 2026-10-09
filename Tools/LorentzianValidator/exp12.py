import warnings; warnings.filterwarnings('ignore')
from research import *
data = load_all()
A = donchian(data, tf=240, N=100, trail=3.0, ptp=5, cooldown_h=168, tag='A')
B = donchian(data, tf=1440, N=55, trail=5.0, ptp=2, cooldown_h=168, tag='B')
evaluate_multi('A+B 각 $1500 · 슬리브 간 같은 코인 허용(백테 가정)', [(A, 2, 3, 1500), (B, 2, 3, 1500)], shared=False)
evaluate_multi('A+B 각 $1500 · 코인당 1포지션(라이브 원웨이)', [(A, 2, 3, 1500), (B, 2, 3, 1500)], shared=True)
evaluate_multi('A+B 각 $3000 · 코인당 1포지션', [(A, 2, 3, 3000), (B, 2, 3, 3000)], shared=True)
