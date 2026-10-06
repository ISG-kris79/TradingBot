import warnings; warnings.filterwarnings('ignore')
from research import *
data = load_all()
E = evaluate
E('현행', [(donchian(data, tag='4h'), 2, 3, 3000)])
E('3ATR 50%익절 + 7일 재진입금지', [(donchian(data, ptp=3, cooldown_h=168, tag='4h'), 2, 3, 3000)])
E('3ATR 50%익절', [(donchian(data, ptp=3, tag='4h'), 2, 3, 3000)])
E('1d N20', [(donchian(data, tf=1440, N=20, tag='1d'), 2, 3, 3000)])
