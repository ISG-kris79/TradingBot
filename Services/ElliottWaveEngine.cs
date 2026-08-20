using System;
using System.Collections.Generic;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════
    //  [v5.33.0] 엘리엇 파동 엔진 — 사용자 스펙: 진입 15분봉 · 손익비 1:3(진입가 기준)
    //
    //  RAW LOGIC SHARED BY: TradingEngine.AnalyzeElliottWaveEntryAsync (라이브)
    //                       Tools/LorentzianValidator --elliott / --elliott-final (백테)
    //
    //  구조: ZigZag(ATR14 × 배수) 인과적 피벗 → 엘리엇 규칙 검증 → 돌파 진입.
    //    · 파동2는 파동1 시작점을 하회할 수 없음
    //    · 파동3은 파동1보다 짧을 수 없음
    //    · 파동4는 파동1 가격영역을 침범할 수 없음
    //    · 진입 = 직전 파동 극점 돌파 / 손절 = 되돌림 극점 / 익절 = 진입가 ± 3×손절폭
    //
    //  ※ 피벗은 "확정봉(ConfirmIndex)" 이후에만 사용 — 미래참조 없음.
    //  ※ 3년·30코인·80만 후보 검증 결과 채택 규칙(M): WC(C파) 전체 + 숏&ADX≥25,
    //     공통 필터 = ZZ배수≥5 · 되돌림≤0.5 · 거래량≥1.2× · MACD 방향일치 · 1h EMA20 방향일치.
    //     5폴드 전부 양수(기대 +0.139R · 승률 37.4% · PF 1.23) — 유일하게 구간 안정적.
    // ═══════════════════════════════════════════════════════════════════════════════
    public static class ElliottWaveEngine
    {
        /// <summary>ZigZag 피벗 — Type +1=고점, -1=저점. ConfirmIndex = 이 피벗이 확정된 봉(그 이전엔 알 수 없음).</summary>
        public sealed class Pivot
        {
            public int Index;
            public int ConfirmIndex;
            public double Price;
            public int Type;
        }

        public sealed class Setup
        {
            public string Kind = "";        // "W3"(파동3) · "W5"(파동5) · "WC"(C파)
            public bool IsLong;
            public double TriggerPrice;     // 돌파 진입선 (직전 파동 극점)
            public double StopPrice;        // 손절선 (되돌림 극점)
            public double Retrace;          // 되돌림 비율 (0~1)
            public double WaveLen;          // 기준 파동 길이 (가격)
            public double ZzMult;           // 이 셋업을 만든 ZigZag ATR 배수
            public int ConfirmIndex;        // 되돌림 극점이 확정된 봉
            public int BarsWaited;          // 확정 후 경과 봉수
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  [v5.34.0] 15분봉 단독 2-degree 엘리엇 — 사용자 스펙: "엘리엇은 15분봉만"
        //
        //  종전(v5.33.x) 실패 원인: 방향판정에 1시간봉 EMA20 기울기를 썼다.
        //    3년·30코인 측정 → 1h 방향필터를 쓰는 한 롱은 −0.121R(PF 0.84)로 구조적 적자.
        //    1h 를 전부 제거하고 15m 단독 2-degree 로 재구축하니 롱 +0.308R 로 흑자 전환.
        //
        //  ★파동 degree 는 타임프레임이 아니라 같은 15m 시계열의 ZigZag 스케일이다.
        //    상위 degree = 15m ZigZag ATR×20  → 지금 몇 번 파동인지 카운트 (1·3·5·A·B·C)
        //    하위 degree = 15m ZigZag ATR×4   → 그 안에서 진입 지점(파동2 되돌림)을 잡는다
        //
        //  채택 규칙 (--elliott-15m, 3년·30코인·54변형 중 5폴드 전부 양수):
        //    상위ZZ20 임펄스(3파/5파) 진행 + 방향일치
        //    · 하위ZZ4 파동2 되돌림 38.2~61.8% · 파동2 미전량되돌림
        //    · 진입 = 하위 파동1 극점 돌파   (전환확인형은 54변형 전부 음수였다)
        //    · 손절 = 되돌림 극값 ∓ 0.5×ATR14 · 익절 = 진입가 ± 3×손절폭
        //    측정: 438건 · 승률 33.6% · 기대 0.165R · PF 1.24 · 폴드 0.14/0.01/0.16/0.51/0.08
        //          롱 188건 +0.308R · 숏 250건 +0.059R
        //  ※ EMA·MACD·ADX·거래량 필터는 전부 제거했다(사용자 지시 + 측정상 불필요).
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>상위 degree 파동 라벨. 0=미상 1=1파 2=3파 3=5파 5=B파 6=C파.</summary>
        public static int LabelHigherWave(List<Pivot> vp, out int legDir)
        {
            legDir = 0;
            if (vp.Count < 3) return 0;
            var p1 = vp[^1];
            legDir = p1.Type == -1 ? 1 : -1;              // 저점에서 끝났다 → 지금 상승 진행 중
            bool up = legDir > 0;
            if (vp.Count >= 5)                            // 파동5 — 1·2·3·4 완성 뒤 진행 중인 레그
            {
                var a = vp[^5]; var b = vp[^4]; var c = vp[^3]; var d = vp[^2];
                bool shape = up ? (a.Type == -1 && b.Type == 1 && c.Type == -1 && d.Type == 1)
                                : (a.Type == 1 && b.Type == -1 && c.Type == 1 && d.Type == -1);
                if (shape)
                {
                    double w1 = Math.Abs(b.Price - a.Price), w3 = Math.Abs(d.Price - c.Price);
                    bool w2ok = up ? c.Price > a.Price : c.Price < a.Price;      // 파동2 미전량되돌림
                    bool w4ok = up ? p1.Price > b.Price : p1.Price < b.Price;    // 파동4 비중첩
                    if (w1 > 0 && w3 > w1 && w2ok && w4ok) return 3;
                }
            }
            if (vp.Count >= 4)                            // C파 — 직전 큰 레그의 조정 마지막 다리
            {
                var m = vp[^4]; var a = vp[^3]; var b = vp[^2];
                bool shape = up ? (m.Type == 1 && a.Type == -1 && b.Type == 1)
                                : (m.Type == -1 && a.Type == 1 && b.Type == -1);
                if (shape)
                {
                    double prior = Math.Abs(a.Price - m.Price), wA = Math.Abs(b.Price - a.Price);
                    bool p2ok = up ? p1.Price > a.Price : p1.Price < a.Price;
                    if (prior > 0 && wA > 0 && wA < prior && p2ok) return 6;
                }
            }
            {                                             // 파동3 — 1·2 완성 뒤 진행 중인 레그
                var a = vp[^3]; var b = vp[^2];
                bool shape = up ? (a.Type == -1 && b.Type == 1) : (a.Type == 1 && b.Type == -1);
                bool w2ok = up ? p1.Price > a.Price : p1.Price < a.Price;
                if (shape && w2ok) return 2;
            }
            if (vp.Count >= 4) return 5;                  // B파 진행 중
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  [v5.34.0] 차트기반 파동 판정 (BOS) — ATR·고정수식 전면 폐기
        //
        //  이전 시도들이 실패한 이유:
        //    · 파동 마감을 "ATR×N 되돌리면 끝"이라는 고정 수식으로 판정 → 변동성 바뀌면 파동이 달라짐
        //    · 상위 degree 를 ATR×20 으로 잡음 → 15m 에서 몇 주에 한 번이라 1500봉에 안 잡힘
        //    · 매 스캔 최근 N봉을 다시 계산 → 창이 밀릴 때마다 카운트가 뒤집힘
        //      (실측: 진입의 97.7%가 96봉 뒤 카운트가 뒤집히는 자리였고, 그것들이 전부 손실)
        //
        //  차트는 파동 마감을 이렇게 말한다 — 숫자가 아니라 구조다:
        //    · 상승 파동은 직전 스윙 저점을 깨는 순간 끝난다 (구조 이탈 = BOS)
        //    · 깨지 않고 고점을 갱신하는 동안은 연장 중이다 → 기준선을 따라 올린다
        //  스윙점은 프랙탈(좌우 K봉보다 높은 고점 / 낮은 저점)로 잡는다. 크기 수식이 아니라 모양 정의다.
        //
        //  1→2→3→4→5 를 매 봉 연속 추적하고 절대규칙을 실시간 검사한다:
        //    파동2가 파동1 시작(앵커)을 깨면 무효 → 재앵커
        //    파동3이 파동1보다 짧으면 임펄스 아님 → 재앵커
        //    파동4가 파동1 가격영역을 침범하면 무효 → 재앵커
        //  하위 1~5 가 완성되면 그 전체를 상위 피벗으로 적립한다 = degree 는 중첩으로 만든다.
        //
        //  채택 규칙 (--elliott-bos, 3년·30코인, 56변형 중 5폴드 전부 양수 2개):
        //    프랙탈 K=21 · 파동2 되돌림 23.6~78.6% · 상위(중첩) 3파·5파 순행 · 파동3 진입만
        //    279건 · 승률 36.9% · 기대 0.206R · PF 1.34 · 폴드 0.02/0.38/0.50/0.05/0.14
        //    롱 108건 +0.375R · 숏 171건 +0.099R      (현행 v5.33.3: 롱 −0.121R PF 0.84)
        //  ※ 파동5 진입은 −0.178R(롱 −0.276R)로 적자라 채택하지 않는다.
        //  ※ 상위 카운트 최초 성립 중앙값 154봉·최대 1070봉 → 1500봉 윈도우로 100% 성립.
        // ═══════════════════════════════════════════════════════════════════════════
        public const int FractalK = 21;          // 프랙탈 폭 (차트 모양 정의)
        // [v5.34.1] 파동2 되돌림 38.2~78.6% · 상위게이트 해제 — '건당R'이 아니라 '총R'로 재선정.
        //   v5.34.0 은 상위 3·5파 순행 게이트로 진입의 90%를 걷어내 건당 0.218R 을 만들었으나
        //   건수가 월 3건까지 줄어 롱 총R 이 25R 에 그쳤다. 게이트를 풀면 건당 0.074R 로 얇아지는
        //   대신 854건(월 24건)이 되어 롱 총R 63R — 2.5배다. 계좌에 쌓이는 건 건당R×건수다.
        // [v5.34.3] 진입창·방향뒤집기 둘 다 측정으로 반증됨 → 검증된 v5.34.1 동작을 기본값으로 고정.
        //   진입창(롱 총R): 0봉 +63R · 4봉 +59R · 8봉 +59R · 16봉 +48R · 32봉 +50R · 64봉 +45R  → 넓힐수록 악화
        //   방향뒤집기: 전체 +71R → −110R (앵커 이탈 시 방향전환 = 역추세 추격)
        //   ※ 스위치는 남겨둔다(재검증용). 기본값을 바꾸려면 --elliott-live15 총R 로 먼저 재확인할 것.
        public const int EntryWindowBarsDefault = 0;   // 진입신호 유효 봉수 (0 = 마감봉 당봉만 · 검증 채택)
        public const double BosRetrMin = 0.382;  // 파동2 되돌림 하한 (피보)
        public const double BosRetrMax = 0.786;  // 파동2 되돌림 상한 (피보)

        public sealed class SetupBos
        {
            public bool IsLong;
            public int HigherWave;        // 2 = 상위3파 · 3 = 상위5파
            public double StopPrice;      // 구조 무효화점 = 파동2 극값 (버퍼 없음)
            public double Retrace;        // 파동2 되돌림 비율
            public double Wave1Len;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        //  [v5.34.0] ★연속 파동 카운터 — 창(window) 없음, 재계산 없음.
        //
        //  파동은 기준점(앵커)에서 시작해 앞으로 세어 나가는 것이다. 파동이 깨지면 그 지점에서
        //  다시 시작한다. "최근 N봉" 같은 고정 구간을 매번 다시 계산하면 스캔할 때마다 카운트가
        //  달라진다 — 실측으로 확인된 실패 원인이다:
        //    · 연속 카운팅 백테 +0.206R  vs  매 스캔 1500봉 재계산 −0.090R
        //    · 진입의 97.7%가 96봉 뒤 카운트가 뒤집히는 자리였고 그것들이 전부 손실
        //
        //  이 카운터는 심볼별로 상태를 들고 가며 새로 마감된 봉만 밀어 넣는다(Advance).
        //  필요한 히스토리는 프랙탈 확정용 링버퍼 2K+1봉뿐이다. 앵커·파동번호·상위 피벗은
        //  상태로 계속 유지되고, 절대규칙 위반이 나면 그 자리에서 재앵커해 이어서 센다.
        // ═══════════════════════════════════════════════════════════════════════════
        public sealed class WaveCounter
        {
            private readonly int _K;
            private readonly double _rMin, _rMax;
            private readonly List<(double h, double l, double c)> _buf = new();   // 프랙탈 확정용 링버퍼 (2K+1)
            private int _bar = -1;                                                // 전역 봉 인덱스(단조 증가)

            private int _dir = 1, _phase = 1;
            private bool _impulse = true;                                         // true=1~5 / false=A~C
            private double _anchorPx; private int _anchorBar;
            private double _w1End, _w2End, _w3End; private int _w1Bar, _w3Bar;
            private double _legExt; private int _legExtBar;
            private double _ref; private bool _hasRef;
            private readonly List<Pivot> _lvl1 = new();
            private bool _init;

            public long LastBarOpenMs { get; private set; } = -1;
            public int BarsProcessed => _bar + 1;
            public bool HigherReady => _lvl1.Count >= 3;
            public int Phase => _phase;
            public bool IsImpulse => _impulse;
            public int Dir => _dir;
            /// <summary>유효한 진입신호(없으면 null). 파동2 마감 봉에서 발생해 EntryWindowBars 동안 유지된다.</summary>
            public SetupBos? Signal { get; private set; }

            private readonly bool _reachGate;   // 3R 목표가 파동3 확장목표(1.618×파동1) 안에 들어와야 진입
            private readonly bool _specInval;   // true = 명세식 구조 재해석 / false = 단순 재앵커
            private readonly bool _higherGate;  // 상위(중첩) 3·5파 순행일 때만 진입
            // [v5.34.3] ★두 결함 수정 스위치
            //  ①진입창(EntryWindowBars): 진입 신호가 '파동2 마감 한 봉'에서만 유효해, 그 봉을 놓치면
            //    추세가 끝날 때까지 재진입 기회가 없었다. 실측(2026-08-19~20): SOL 이 엔진 스스로
            //    '임펄스 3파 상승' 으로 세는 동안 +12.1% 상승, 신호 0건. BTC 도 5파까지 진행 중 0건.
            //  ②방향고착(FlipOnW2Break): 상승장에서 파동2가 앵커를 깨면 같은 방향으로만 재앵커해
            //    '하락구조' 가 영원히 유지됐다. 실측: ETH +21.6% 상승 구간을 하락구조로 카운트.
            private readonly int _entryWindow;
            private readonly bool _flipOnW2Break;
            public WaveCounter(int k = FractalK, double rMin = BosRetrMin, double rMax = BosRetrMax,
                               bool reachGate = false, bool specInval = false, bool higherGate = false,
                               int entryWindowBars = EntryWindowBarsDefault, bool flipOnW2Break = false)
            { _K = k; _rMin = rMin; _rMax = rMax; _reachGate = reachGate; _specInval = specInval; _higherGate = higherGate;
              _entryWindow = Math.Max(0, entryWindowBars); _flipOnW2Break = flipOnW2Break; }

            private SetupBos? _pending; private int _pendingLeft;

            /// <summary>파동3 확장목표 = 파동2 종점 ± 1.618×파동1 (엘리엇 표준 투영).</summary>
            public double Wave3Target { get; private set; }

            private void ResetTo(int dir, double px, int bar, bool impulse)
            {
                _dir = dir; _anchorPx = px; _anchorBar = bar; _phase = 1; _impulse = impulse;
                _w1End = _w2End = _w3End = 0; _legExt = px; _legExtBar = bar; _hasRef = false; _ref = 0;
            }

            private void PushLvl1(double px, int bar, int type)
            {
                if (_lvl1.Count > 0 && _lvl1[^1].Type == type)
                {
                    if ((type == 1 && px > _lvl1[^1].Price) || (type == -1 && px < _lvl1[^1].Price))
                    { _lvl1[^1].Price = px; _lvl1[^1].Index = bar; _lvl1[^1].ConfirmIndex = bar; }
                    return;
                }
                _lvl1.Add(new Pivot { Price = px, Index = bar, ConfirmIndex = bar, Type = type });
                if (_lvl1.Count > 64) _lvl1.RemoveAt(0);      // 상위 카운트는 최근 몇 개만 쓰므로 무한 성장 방지
            }

            /// <summary>마감된 봉 하나를 전진 반영한다. 같은 봉을 두 번 넣지 않도록 호출측이 OpenTime 으로 관리.</summary>
            public void Advance(double high, double low, double close, long openTimeMs)
            {
                // [v5.34.3] 진입창 유지 — 구조가 살아있는 동안 신호를 EntryWindowBars 봉까지 유효하게 둔다.
                if (_pending != null)
                {
                    bool dead = _pending.IsLong ? low <= _pending.StopPrice : high >= _pending.StopPrice;
                    if (dead || --_pendingLeft < 0) _pending = null;
                }
                Signal = _pending;
                _bar++; LastBarOpenMs = openTimeMs;
                _buf.Add((high, low, close));
                if (_buf.Count > 2 * _K + 1) _buf.RemoveAt(0);

                if (!_init) { ResetTo(1, low, _bar, true); _init = true; return; }

                // ── 프랙탈 확정: 버퍼가 2K+1 이면 가운데 봉(현재−K)을 판정할 수 있다 ──
                double fHi = double.NaN, fLo = double.NaN;
                if (_buf.Count == 2 * _K + 1)
                {
                    var m = _buf[_K]; bool isH = true, isL = true;
                    for (int d = 1; d <= _K; d++)
                    {
                        if (isH && !(m.h > _buf[_K - d].h && m.h > _buf[_K + d].h)) isH = false;
                        if (isL && !(m.l < _buf[_K - d].l && m.l < _buf[_K + d].l)) isL = false;
                        if (!isH && !isL) break;
                    }
                    if (isH) fHi = m.h;
                    if (isL) fLo = m.l;
                }

                bool up = _dir > 0;
                bool impulseLeg = _impulse ? (_phase == 1 || _phase == 3 || _phase == 5) : (_phase == 1 || _phase == 3);
                bool legUp = impulseLeg ? up : !up;

                if (legUp) { if (high > _legExt) { _legExt = high; _legExtBar = _bar; } }
                else { if (low < _legExt) { _legExt = low; _legExtBar = _bar; } }

                // 기준선 갱신 = 파동 연장 추적
                if (legUp) { if (!double.IsNaN(fLo) && (!_hasRef || fLo > _ref) && fLo < _legExt) { _ref = fLo; _hasRef = true; } }
                else { if (!double.IsNaN(fHi) && (!_hasRef || fHi < _ref) && fHi > _legExt) { _ref = fHi; _hasRef = true; } }

                bool bos = _hasRef && (legUp ? low < _ref : high > _ref);   // 구조 이탈 = 파동 마감

                int hiLab = 0, hiDir = 0;
                if (_lvl1.Count >= 3) hiLab = LabelHigherWave(_lvl1, out hiDir);

                if (_impulse)
                {
                    switch (_phase)
                    {
                        case 1:
                            if (bos) { _w1End = _legExt; _w1Bar = _legExtBar; _phase = 2; _legExt = legUp ? low : high; _legExtBar = _bar; _hasRef = false; }
                            break;
                        case 2:
                            {
                                // ★2파 법칙 위반 → 재해석: "1파 상승은 단순 반등(B파)이었다"
                                //   → 지그재그 C파 진행 중으로 구조 전환 (방향 반전 + 조정 모드)
                                if (up ? _legExt <= _anchorPx : _legExt >= _anchorPx)
                                {
                                    PushLvl1(_anchorPx, _anchorBar, up ? -1 : 1);
                                    PushLvl1(_w1End, _w1Bar, up ? 1 : -1);
                                    if (_specInval)
                                    {   // 명세: 1파는 B파 반등이었다 → 지그재그 C파 진행으로 전환
                                        ResetTo(-_dir, _w1End, _w1Bar, false); _phase = 3;
                                        _legExtBar = _bar; _hasRef = false;
                                    }
                                    // [v5.34.3] ★방향고착 수정 — 파동2가 앵커를 깼다는 건 구조를 반대로 읽었다는 뜻이다.
                                    //   같은 방향으로만 재앵커하면 상승장에서 '하락구조' 가 영원히 유지된다(ETH +21.6% 실측).
                                    else ResetTo(_flipOnW2Break ? -_dir : _dir, _legExt, _legExtBar, true);
                                    break;
                                }
                                if (!bos) break;
                                _w2End = _legExt;
                                double w1len = Math.Abs(_w1End - _anchorPx);
                                double retr = w1len > 0 ? Math.Abs(_w1End - _legExt) / w1len : 9;
                                // ★파동3 확장목표 = 파동2 종점 ± 1.618×파동1
                                Wave3Target = up ? _legExt + 1.618 * w1len : _legExt - 1.618 * w1len;
                                bool bandOk = retr >= _rMin && retr <= _rMax;
                                bool hiOk = !_higherGate || ((hiLab == 2 || hiLab == 3) && hiDir == (up ? 1 : -1));
                                // ★1:3 도달가능성 — 3R 익절선이 파동3 확장목표 안에 들어와야 구조적으로 성립
                                double entryApprox = close, riskApprox = Math.Abs(entryApprox - _legExt);
                                double tp3 = up ? entryApprox + 3 * riskApprox : entryApprox - 3 * riskApprox;
                                bool reachOk = !_reachGate || riskApprox <= 0 ||
                                               (up ? tp3 <= Wave3Target : tp3 >= Wave3Target);
                                if (bandOk && hiOk && reachOk)
                                {
                                    _pending = new SetupBos { IsLong = up, HigherWave = hiLab, StopPrice = _legExt, Retrace = retr, Wave1Len = w1len };
                                    _pendingLeft = _entryWindow;
                                    Signal = _pending;
                                }
                                _phase = 3; _legExt = up ? high : low; _legExtBar = _bar; _hasRef = false;
                                break;
                            }
                        case 3:
                            if (bos)
                            {
                                double a = Math.Abs(_w1End - _anchorPx), c3 = Math.Abs(_legExt - _w2End);
                                // ★3파 법칙 위반 → 재해석: "1~2파는 더 큰 3파의 세부 1-2파였다"
                                //   → 방향을 유지한 채 degree 승격. 원래 앵커를 살리고 파동1을 이번 고점으로 다시 잡는다.
                                if (c3 <= a)
                                {
                                    if (_specInval)
                                    {   // 명세: 1~2파는 더 큰 3파의 세부 1-2파 → degree 승격, 방향 유지
                                        _w1End = _legExt; _w1Bar = _legExtBar; _phase = 2;
                                        _legExt = legUp ? low : high; _legExtBar = _bar; _hasRef = false;
                                    }
                                    else
                                    {
                                        PushLvl1(_anchorPx, _anchorBar, up ? -1 : 1);
                                        PushLvl1(_legExt, _legExtBar, up ? 1 : -1);
                                        ResetTo(-_dir, _legExt, _legExtBar, true);
                                    }
                                    break;
                                }
                                _w3End = _legExt; _w3Bar = _legExtBar; _phase = 4; _legExt = legUp ? low : high; _legExtBar = _bar; _hasRef = false;
                            }
                            break;
                        case 4:
                            {
                                // ★4파 법칙 위반 → 재해석: "추진파가 아니라 조정파(ABC/WXY)였다"
                                //   → 조정 모드로 전환. 방향은 현재 진행 방향을 유지한다.
                                if (up ? _legExt <= _w1End : _legExt >= _w1End)
                                {
                                    PushLvl1(_anchorPx, _anchorBar, up ? -1 : 1);
                                    PushLvl1(_w3End, _w3Bar, up ? 1 : -1);
                                    // 명세: 추진파 아님 → ABC 조정 / 단순: 같은 방향 임펄스 재시작
                                    ResetTo(_dir, _legExt, _legExtBar, !_specInval);
                                    break;
                                }
                                if (!bos) break;
                                _phase = 5; _legExt = up ? high : low; _legExtBar = _bar; _hasRef = false;   // 파동5 진입은 미채택(적자)
                                break;
                            }
                        case 5:
                            if (bos)
                            {   // 하위 1~5 완성 = 상위 1파 → 중첩 적립 후 A-B-C 조정으로 전환
                                PushLvl1(_anchorPx, _anchorBar, up ? -1 : 1);
                                PushLvl1(_legExt, _legExtBar, up ? 1 : -1);
                                ResetTo(-_dir, _legExt, _legExtBar, false);
                            }
                            break;
                    }
                }
                else
                {
                    switch (_phase)
                    {
                        case 1: if (bos) { _w1End = _legExt; _w1Bar = _legExtBar; _phase = 2; _legExt = legUp ? low : high; _legExtBar = _bar; _hasRef = false; } break;
                        case 2: if (bos) { _w2End = _legExt; _phase = 3; _legExt = up ? high : low; _legExtBar = _bar; _hasRef = false; } break;
                        case 3:
                            if (bos)
                            {   // C 완성 → 임펄스 재개
                                PushLvl1(_anchorPx, _anchorBar, up ? -1 : 1);
                                PushLvl1(_legExt, _legExtBar, up ? 1 : -1);
                                ResetTo(-_dir, _legExt, _legExtBar, true);
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// [구버전] 창 단위 일괄 계산. 매 스캔 재계산하면 카운트가 뒤집히므로 라이브에서는 쓰지 않는다.
        /// WaveCounter 시딩 검증용으로만 남긴다.
        /// </summary>
        public static SetupBos? DetectSetupBos(IList<IBinanceKline> k, int evalIdx)
        {
            int n = k.Count;
            if (evalIdx < FractalK * 4 || evalIdx >= n) return null;

            // ── 프랙탈 확정 (i+K 봉에서야 알 수 있다 = 인과적) ──
            var fHi = new double[n]; var fLo = new double[n];
            for (int i = 0; i < n; i++) { fHi[i] = double.NaN; fLo[i] = double.NaN; }
            for (int i = FractalK; i < n - FractalK; i++)
            {
                double h = (double)k[i].HighPrice, l = (double)k[i].LowPrice;
                bool isH = true, isL = true;
                for (int d = 1; d <= FractalK; d++)
                {
                    if (isH && !(h > (double)k[i - d].HighPrice && h > (double)k[i + d].HighPrice)) isH = false;
                    if (isL && !(l < (double)k[i - d].LowPrice && l < (double)k[i + d].LowPrice)) isL = false;
                    if (!isH && !isL) break;
                }
                if (isH) fHi[i + FractalK] = h;
                if (isL) fLo[i + FractalK] = l;
            }

            // ── 전진 상태기계 ──
            int dir = 1, phase = 1;
            double anchorPx = (double)k[FractalK + 1].LowPrice; int anchorBar = FractalK + 1;
            double w1End = 0, w2End = 0, w3End = 0;
            double legExt = anchorPx; int legExtBar = anchorBar;
            double refLevel = 0; bool hasRef = false;
            var lvl1 = new List<Pivot>();
            SetupBos? fired = null;

            void PushLvl1(double px, int bar, int type)
            {
                if (lvl1.Count > 0 && lvl1[^1].Type == type)
                {
                    if ((type == 1 && px > lvl1[^1].Price) || (type == -1 && px < lvl1[^1].Price))
                    { lvl1[^1].Price = px; lvl1[^1].Index = bar; lvl1[^1].ConfirmIndex = bar; }
                    return;
                }
                lvl1.Add(new Pivot { Price = px, Index = bar, ConfirmIndex = bar, Type = type });
            }
            void ResetTo(int d, double px, int bar)
            { dir = d; anchorPx = px; anchorBar = bar; phase = 1; w1End = w2End = w3End = 0; legExt = px; legExtBar = bar; hasRef = false; refLevel = 0; }

            for (int j = FractalK + 2; j <= evalIdx; j++)
            {
                double hj = (double)k[j].HighPrice, lj = (double)k[j].LowPrice;
                bool up = dir > 0;
                bool legUp = (phase == 1 || phase == 3 || phase == 5) ? up : !up;

                if (legUp) { if (hj > legExt) { legExt = hj; legExtBar = j; } }
                else { if (lj < legExt) { legExt = lj; legExtBar = j; } }

                // 기준선 갱신 = 파동 연장 추적
                if (legUp) { double v = fLo[j]; if (!double.IsNaN(v) && (!hasRef || v > refLevel) && v < legExt) { refLevel = v; hasRef = true; } }
                else { double v = fHi[j]; if (!double.IsNaN(v) && (!hasRef || v < refLevel) && v > legExt) { refLevel = v; hasRef = true; } }

                bool bos = hasRef && (legUp ? lj < refLevel : hj > refLevel);   // 구조 이탈 = 파동 마감

                int hiLab = 0, hiDir = 0;
                if (lvl1.Count >= 3) hiLab = LabelHigherWave(lvl1, out hiDir);

                switch (phase)
                {
                    case 1:
                        if (bos) { w1End = legExt; phase = 2; legExt = legUp ? lj : hj; legExtBar = j; hasRef = false; }
                        break;

                    case 2:
                        {
                            bool broke = up ? legExt <= anchorPx : legExt >= anchorPx;      // ★파동2 절대규칙
                            if (broke)
                            {
                                PushLvl1(anchorPx, anchorBar, up ? -1 : 1);
                                PushLvl1(w1End, legExtBar, up ? 1 : -1);
                                ResetTo(dir, legExt, legExtBar); break;
                            }
                            if (!bos) break;
                            double w1len = Math.Abs(w1End - anchorPx);
                            double retr = w1len > 0 ? Math.Abs(w1End - legExt) / w1len : 9;
                            w2End = legExt;
                            // ★진입 판정 — 평가봉(마지막 마감봉)에서 일어난 구조이탈만 신호로 낸다
                            if (j == evalIdx && retr >= BosRetrMin && retr <= BosRetrMax
                                && (hiLab == 2 || hiLab == 3) && hiDir == (up ? 1 : -1))
                            {
                                fired = new SetupBos
                                { IsLong = up, HigherWave = hiLab, StopPrice = legExt, Retrace = retr, Wave1Len = w1len };
                            }
                            phase = 3; legExt = up ? hj : lj; legExtBar = j; hasRef = false;
                            break;
                        }
                    case 3:
                        if (bos)
                        {
                            double w1 = Math.Abs(w1End - anchorPx), w3 = Math.Abs(legExt - w2End);
                            if (w3 <= w1)                                                    // ★파동3 최단 불가
                            {
                                PushLvl1(anchorPx, anchorBar, up ? -1 : 1);
                                PushLvl1(legExt, legExtBar, up ? 1 : -1);
                                ResetTo(-dir, legExt, legExtBar); break;
                            }
                            w3End = legExt; phase = 4; legExt = legUp ? lj : hj; legExtBar = j; hasRef = false;
                        }
                        break;

                    case 4:
                        {
                            bool overlap = up ? legExt <= w1End : legExt >= w1End;           // ★파동4 비중첩
                            if (overlap)
                            {
                                PushLvl1(anchorPx, anchorBar, up ? -1 : 1);
                                PushLvl1(w3End, legExtBar, up ? 1 : -1);
                                ResetTo(dir, legExt, legExtBar); break;
                            }
                            if (!bos) break;
                            phase = 5; legExt = up ? hj : lj; legExtBar = j; hasRef = false;  // 파동5 진입은 미채택(적자)
                            break;
                        }
                    case 5:
                        if (bos)
                        {   // 하위 1~5 완성 = 상위 1파. 중첩으로 degree 생성 후 방향 반전.
                            PushLvl1(anchorPx, anchorBar, up ? -1 : 1);
                            PushLvl1(legExt, legExtBar, up ? 1 : -1);
                            ResetTo(-dir, legExt, legExtBar);
                        }
                        break;
                }
            }
            return fired;
        }

        /// <summary>15m 2-degree 채택 규칙의 셋업.</summary>
        public sealed class Setup15
        {
            public bool IsLong;
            public int HigherWave;        // 2=상위3파 3=상위5파
            public double TriggerPrice;   // 하위 파동1 극점 (돌파 진입선)
            public double StopPrice;      // 되돌림 극값 ∓ 0.5×ATR
            public double Retrace;        // 하위 파동2 되돌림 비율
            public double Wave1Len;
        }

        public const double Hi15Mult = 20.0;      // 상위 degree ZigZag ATR 배수
        public const double Lo15Mult = 4.0;       // 하위 degree ZigZag ATR 배수
        public const double Retr15Min = 0.382;    // 파동2 되돌림 하한 (피보)
        public const double Retr15Max = 0.618;    // 파동2 되돌림 상한 (피보)
        public const double StopBufAtr15 = 0.5;   // 손절 버퍼 (ATR14 배수)

        /// <summary>
        /// 15분봉만으로 상위/하위 degree 를 동시에 세어 진입 셋업을 낸다. 1시간봉 사용 안 함.
        /// evalIdx = 마지막 마감봉. 되돌림 극값은 피벗 확정을 기다리지 않고 실시간 추적한다
        /// (확정 대기가 곧 늦은 진입이라 v5.33.x 에서 꼭대기 매수를 유발했다).
        /// </summary>
        public static Setup15? DetectSetup15m(IList<IBinanceKline> k, double[] atr, int evalIdx)
        {
            if (evalIdx < 300 || evalIdx >= k.Count) return null;

            // ── 상위 degree: 지금 몇 번 파동인가 ──
            var hiPiv = ZigZag(k, atr, Hi15Mult);
            var hiVis = new List<Pivot>();
            foreach (var p in hiPiv) { if (p.ConfirmIndex > evalIdx) break; hiVis.Add(p); }
            int hiLab = LabelHigherWave(hiVis, out int hiDir);
            if (!(hiLab == 2 || hiLab == 3) || hiDir == 0) return null;   // 임펄스 3파·5파 진행 중일 때만
            bool isLong = hiDir > 0;

            // ── 하위 degree: 파동1 → 파동2 되돌림 ──
            var loPiv = ZigZag(k, atr, Lo15Mult);
            var loVis = new List<Pivot>();
            foreach (var p in loPiv) { if (p.ConfirmIndex > evalIdx) break; loVis.Add(p); }
            if (loVis.Count < 3) return null;
            var s1 = loVis[^1];                       // 하위 파동1 끝
            if ((isLong ? s1.Type != 1 : s1.Type != -1)) return null;   // 방향과 맞는 극점이어야 함
            var s0 = loVis[^2];
            if ((isLong ? s0.Type != -1 : s0.Type != 1)) return null;
            double w1 = Math.Abs(s1.Price - s0.Price);
            if (w1 <= 0 || w1 / s1.Price < 0.003) return null;

            // 되돌림 극값 실시간 추적 (피벗 확정 대기 없음)
            double ext = isLong ? double.MaxValue : double.MinValue; int extBar = s1.Index;
            for (int e = s1.Index + 1; e <= evalIdx; e++)
            {
                double h = (double)k[e].HighPrice, l = (double)k[e].LowPrice;
                if (isLong) { if (l < ext) { ext = l; extBar = e; } }
                else { if (h > ext) { ext = h; extBar = e; } }
            }
            if (extBar <= s1.Index) return null;
            if (isLong ? ext <= s0.Price : ext >= s0.Price) return null;   // ★파동2 미전량되돌림(절대규칙)
            double retr = Math.Abs(s1.Price - ext) / w1;
            if (retr < Retr15Min || retr > Retr15Max) return null;         // ★피보 되돌림대

            double stop = isLong ? ext - StopBufAtr15 * atr[evalIdx] : ext + StopBufAtr15 * atr[evalIdx];
            return new Setup15
            {
                IsLong = isLong, HigherWave = hiLab, TriggerPrice = s1.Price,
                StopPrice = stop, Retrace = retr, Wave1Len = w1
            };
        }

        /// <summary>Wilder ATR(14) 배열 — ZigZag 임계값 산출용.</summary>
        public static double[] BuildAtr(IList<IBinanceKline> k, int period = 14)
        {
            int n = k.Count;
            var atr = new double[n];
            double a = 0;
            for (int j = 1; j < n; j++)
            {
                double hi = (double)k[j].HighPrice, lo = (double)k[j].LowPrice, pc = (double)k[j - 1].ClosePrice;
                double tr = Math.Max(hi - lo, Math.Max(Math.Abs(hi - pc), Math.Abs(lo - pc)));
                if (j <= period) { a += tr; if (j == period) a /= period; }
                else a = (a * (period - 1) + tr) / period;
                atr[j] = a;
            }
            return atr;
        }

        /// <summary>인과적 ZigZag — 극점에서 (ATR×mult) 만큼 되돌리는 순간 그 봉에서 피벗 확정.</summary>
        public static List<Pivot> ZigZag(IList<IBinanceKline> k, double[] atr, double mult, int start = 20)
        {
            var piv = new List<Pivot>();
            int n = k.Count;
            if (start >= n) return piv;
            int dir = 1;
            double ext = (double)k[start].HighPrice;
            int extIdx = start;
            for (int j = start + 1; j < n; j++)
            {
                double thr = mult * atr[j];
                if (thr <= 0) continue;
                double hi = (double)k[j].HighPrice, lo = (double)k[j].LowPrice;
                if (dir > 0)
                {
                    if (hi >= ext) { ext = hi; extIdx = j; }
                    else if (ext - lo >= thr)
                    { piv.Add(new Pivot { Index = extIdx, ConfirmIndex = j, Price = ext, Type = 1 }); dir = -1; ext = lo; extIdx = j; }
                }
                else
                {
                    if (lo <= ext) { ext = lo; extIdx = j; }
                    else if (hi - ext >= thr)
                    { piv.Add(new Pivot { Index = extIdx, ConfirmIndex = j, Price = ext, Type = -1 }); dir = 1; ext = hi; extIdx = j; }
                }
            }
            return piv;
        }

        /// <summary>
        /// evalIdx(마지막 마감봉) 시점에서 "아직 살아있고 아직 돌파되지 않은" 최신 엘리엇 셋업 탐지.
        /// 우선순위 WC → W5 → W3 (검증 규칙 M이 WC를 주축으로 삼음).
        /// </summary>
        public static Setup? DetectSetup(IList<IBinanceKline> k, double[] atr, double mult, int evalIdx,
                                         double retraceMin = 0.15, double retraceMax = 0.50, int maxWaitBars = 96)
            => DetectSetupFromPivots(k, ZigZag(k, atr, mult), mult, evalIdx, retraceMin, retraceMax, maxWaitBars);

        /// <summary>
        /// 피벗을 외부에서 1회만 계산해 재사용하는 오버로드(백테 재생용). 판정 로직은 DetectSetup 과 완전 동일.
        /// ZigZag 은 전진 인과 패스라 "ConfirmIndex ≤ evalIdx 인 피벗 집합" = "evalIdx 까지의 데이터로 계산한 피벗 집합".
        /// </summary>
        public static Setup? DetectSetupFromPivots(IList<IBinanceKline> k, List<Pivot> piv, double mult, int evalIdx,
                                                   double retraceMin = 0.15, double retraceMax = 0.50, int maxWaitBars = 96)
        {
            if (piv.Count < 4) return null;

            // 되돌림 극점 = 가장 최근 피벗. 확정봉이 평가봉보다 뒤면 아직 모르는 정보.
            Pivot? p2 = null; int p2i = -1;
            for (int i = piv.Count - 1; i >= 0; i--)
            {
                if (piv[i].ConfirmIndex <= evalIdx) { p2 = piv[i]; p2i = i; break; }
            }
            if (p2 == null) return null;
            int waited = evalIdx - p2.ConfirmIndex;
            if (waited < 0 || waited > maxWaitBars) return null;

            bool isLong = p2.Type == -1;                 // 저점에서 끝난 되돌림 → 상방 진행
            double minWavePct = 0.004;                   // 파동1 최소 0.4% (노이즈 제거)

            // 후보 3종 (WC → W5 → W3)
            for (int kind = 0; kind < 3; kind++)
            {
                double trig, stop, waveLen, retr;
                string name;
                if (kind == 0)   // WC — 직전 추세레그의 조정(A-B-C) 중 C파
                {
                    if (p2i < 3) continue;
                    var pm = piv[p2i - 3]; var p0 = piv[p2i - 2]; var p1 = piv[p2i - 1];
                    if (isLong ? !(pm.Type == 1 && p0.Type == -1 && p1.Type == 1)
                               : !(pm.Type == -1 && p0.Type == 1 && p1.Type == -1)) continue;
                    double prior = Math.Abs(p0.Price - pm.Price);
                    waveLen = Math.Abs(p1.Price - p0.Price);
                    if (prior <= 0 || waveLen <= 0 || waveLen >= prior) continue;      // A파는 직전 레그보다 작아야 '조정'
                    if (waveLen / p1.Price < minWavePct) continue;
                    retr = Math.Abs(p1.Price - p2.Price) / waveLen;
                    if (isLong ? p2.Price <= p0.Price : p2.Price >= p0.Price) continue;
                    trig = p1.Price; stop = p2.Price; name = "WC";
                }
                else if (kind == 1)   // W5 — 1·2·3·4 완성 후 파동5
                {
                    if (p2i < 4) continue;
                    var p0 = piv[p2i - 4]; var p1 = piv[p2i - 3]; var pw2 = piv[p2i - 2]; var p3 = piv[p2i - 1];
                    if (isLong ? !(p0.Type == -1 && p1.Type == 1 && pw2.Type == -1 && p3.Type == 1)
                               : !(p0.Type == 1 && p1.Type == -1 && pw2.Type == 1 && p3.Type == -1)) continue;
                    double w1 = Math.Abs(p1.Price - p0.Price), w3 = Math.Abs(p3.Price - pw2.Price);
                    if (w1 <= 0 || w3 <= w1) continue;                                  // ★파동3은 파동1보다 짧을 수 없음
                    if (w1 / p1.Price < minWavePct) continue;
                    if (isLong ? pw2.Price <= p0.Price : pw2.Price >= p0.Price) continue; // ★파동2 미전량되돌림
                    if (isLong ? p2.Price <= p1.Price : p2.Price >= p1.Price) continue;   // ★파동4 비중첩
                    waveLen = w3; retr = Math.Abs(p3.Price - p2.Price) / w3;
                    trig = p3.Price; stop = p2.Price; name = "W5";
                }
                else               // W3 — 파동1 후 파동2 되돌림 완료 → 파동3
                {
                    if (p2i < 2) continue;
                    var p0 = piv[p2i - 2]; var p1 = piv[p2i - 1];
                    if (isLong ? !(p0.Type == -1 && p1.Type == 1)
                               : !(p0.Type == 1 && p1.Type == -1)) continue;
                    waveLen = Math.Abs(p1.Price - p0.Price);
                    if (waveLen <= 0 || waveLen / p1.Price < minWavePct) continue;
                    retr = Math.Abs(p1.Price - p2.Price) / waveLen;
                    if (isLong ? p2.Price <= p0.Price : p2.Price >= p0.Price) continue;   // ★파동2 미전량되돌림
                    trig = p1.Price; stop = p2.Price; name = "W3";
                }

                if (retr < retraceMin || retr > retraceMax) continue;

                // 확정봉 다음 봉부터 평가봉까지: ①손절선 이탈(무효) ②이미 돌파(늦은진입) 둘 다 배제
                bool dead = false, already = false;
                for (int e = p2.ConfirmIndex + 1; e <= evalIdx; e++)
                {
                    double hi = (double)k[e].HighPrice, lo = (double)k[e].LowPrice;
                    if (isLong) { if (lo <= stop) { dead = true; break; } if (hi >= trig) { already = true; break; } }
                    else { if (hi >= stop) { dead = true; break; } if (lo <= trig) { already = true; break; } }
                }
                if (dead || already) continue;

                return new Setup
                {
                    Kind = name, IsLong = isLong, TriggerPrice = trig, StopPrice = stop,
                    Retrace = retr, WaveLen = waveLen, ZzMult = mult,
                    ConfirmIndex = p2.ConfirmIndex, BarsWaited = waited
                };
            }
            return null;
        }
    }
}
