import warnings; warnings.filterwarnings('ignore')
from exp10 import *
data = load_all()
for tp, sl in ((1.0, 3.0), (0.5, 2.0), (1.0, 2.0)):
    t = donchian(data, tf=240, N=55, init=sl, trail=99, tp_atr=tp, tag='H')   # 트레일 무력화(99ATR) · 고정 익절/손절
    stats(f'고승률형 4h 돌파 · 익절 {tp}ATR / 손절 {sl}ATR', [(t, 2, 3, 3000)])
