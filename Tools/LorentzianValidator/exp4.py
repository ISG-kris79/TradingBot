import warnings; warnings.filterwarnings('ignore')
from research import *
data = load_all()
E = evaluate
base = donchian(data, tag='4h')

def side_filter(trades, K, thr=0.0, both=True):
    """방향별 최근 K건(이미 청산된 모든 신호, 체결 여부 무관) 평균 손익이 thr 이하이면 그 방향 신규 진입 보류"""
    import bisect
    out = []
    for side in (1, -1):
        g = sorted([t for t in trades if t[5] == side], key=lambda x: x[1])
        closed = sorted(g, key=lambda x: x[2]); ct = [t[2] for t in closed]; pn = [t[3] for t in closed]
        for t in g:
            if not both and side < 0: out.append(t); continue
            n = bisect.bisect_right(ct, t[1])            # 진입 시각 이전에 청산된 신호 수
            if n < K: out.append(t); continue
            if sum(pn[n - K:n]) / K > thr: out.append(t)
    return out

E('현행', [(base, 2, 3, 3000)])
for K in (20, 40, 80):
    E(f'방향별 최근 {K}건 성적>0 일 때만 (롱숏)', [(side_filter(base, K), 2, 3, 3000)])
    E(f'롱만 최근 {K}건 성적>0 일 때만', [(side_filter(base, K, both=False), 2, 3, 3000)])
