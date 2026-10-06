import warnings; warnings.filterwarnings('ignore')
import research as R
from research import *
data = load_all()
E = evaluate
E('현행', [(donchian(data, tag='4h'), 2, 3, 3000)])
for ba in (1.5, 2, 3, 4):
    for bl in (0.0, 0.5):
        if bl >= ba: continue
        E(f'본절: +{ba}ATR 도달 시 손절→진입+{bl}ATR', [(donchian(data, be_after=ba, be_lock=bl, tag='4h'), 2, 3, 3000)])
