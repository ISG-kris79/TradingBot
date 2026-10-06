# 바이낸스 USDT 선물 실제 펀딩비 이력 수집 (2019-09~현재) → cache/funding_{SYM}.csv (fundingTime,rate)
import json, time, urllib.request, os
from research import UNIV

def fetch(sym):
    out = []; start = 1567296000000   # 2019-09-01
    while True:
        url = f'https://fapi.binance.com/fapi/v1/fundingRate?symbol={sym}&startTime={start}&limit=1000'
        for attempt in range(5):
            try:
                with urllib.request.urlopen(url, timeout=20) as r: rows = json.loads(r.read()); break
            except Exception as e:
                time.sleep(2 + attempt * 3)
        else:
            return out
        if not rows: break
        out += [(int(x['fundingTime']), float(x['fundingRate'])) for x in rows]
        start = rows[-1]['fundingTime'] + 1
        if len(rows) < 1000: break
        time.sleep(0.5)
    return out

if __name__ == '__main__':
    for sym in UNIV:
        f = f'cache/funding_{sym}.csv'
        if os.path.exists(f): print(sym, 'cached'); continue
        rows = fetch(sym)
        with open(f, 'w') as fh:
            for t, v in rows: fh.write(f'{t},{v}\n')
        print(sym, len(rows), flush=True)
