using System;
using System.Collections.Generic;
using Binance.Net.Interfaces;

namespace TradingBot.Services.LorentzianV2
{
    // jdehorty Lorentzian Classification 공식 필터 풀세트 (Pine Script v5)
    //   RAW LOGIC SHARED BY: TradingEngine.AnalyzeLorentzianEntryAsync (라이브)
    //                       Tools/LorentzianValidator backtest
    //   라이브와 백테스트 1:1 동일 코드 보장.
    public static class LorentzianGuard
    {
        public const float DefaultGuardWinRate = 0.70f;
        public const int   MinBarsHeldForExit = 4;
        public const int   MaxHoldBars        = 96;

        public sealed class FilterResult
        {
            public bool   Passed         { get; set; }
            public string BlockReason    { get; set; } = "";
            public int    KnnPrediction  { get; set; }
            public int    KnnK           { get; set; }
            public int    KnnPositive    { get; set; }
            public float  KnnWinRate     { get; set; }
            public double Atr1           { get; set; }
            public double Atr10          { get; set; }
            public double Adx            { get; set; }
            public double Ema200         { get; set; }
            public double Sma200         { get; set; }
            public double RegimeSlope    { get; set; }
            public double NwKernel       { get; set; }
            public double NwKernelPrev2  { get; set; }
        }

        // 진입 가드 평가 — 마지막 봉 (kl[^1]) 기준
        // engine 은 호출자가 미리 학습 (walk-forward 라벨 = 4봉 후 close 방향)
        public static FilterResult EvaluateEntry(
            List<IBinanceKline> kl,
            LorentzianAnnEngine engine,
            float guardWinRate = DefaultGuardWinRate)
        {
            var r = new FilterResult();
            if (kl == null || kl.Count < 250) { r.BlockReason = "KLINE_TOO_FEW"; return r; }

            int idx = kl.Count - 1;

            // 1) KNN
            var feats = LorentzianFeatures.Extract(kl);
            if (feats == null) { r.BlockReason = "FEATS_NULL"; return r; }
            var pred = engine.Predict(feats);
            r.KnnPrediction = pred.Prediction;
            r.KnnK = pred.K;
            r.KnnPositive = pred.PositiveVotes;
            r.KnnWinRate = pred.K > 0 ? (float)pred.PositiveVotes / pred.K : 0f;
            if (!pred.IsReady || pred.K == 0) { r.BlockReason = "KNN_NOT_READY"; return r; }
            if (pred.Prediction <= 0) { r.BlockReason = "KNN_NOT_LONG"; return r; }
            // [v5.24.4] 강신호 하한 6→4 완화 — v5.23.98의 pred>=6(8이웃 중 7+ LONG)이 진입 0건의 지배 게이트.
            //   라이브 약세장에선 대부분 코인 net -6~-8(숏)이라 net≥6은 사실상 통과 불가 → 3일 진입 0건.
            //   net≥4(=8이웃 중 5+ LONG)로 funnel 개방. 약신호(1~3)는 여전히 차단. 배포 후 카나리 관찰.
            //   (이전 OOS: 강신호6+ +0.214% vs 전체 +0.285% — 6+가 더 좋았으나 거래빈도 극저라 실거래 무의미했음.)
            if (pred.Prediction < 4) { r.BlockReason = $"KNN_WEAK (pred={pred.Prediction}<4)"; return r; }

            // [v5.23.91] 순수 TradingView LCC (jdehorty 기본 필터셋) — KNN + Volatility + Regime + Kernel 만.
            //   봇 커스텀 필터(ADX/EMA200/SMA200/BB중심선/BB워크/박스돌파)는 LCC가 아니므로 전부 제거.
            //   jdehorty 기본값: Volatility=ON, Regime=ON(-0.1), Kernel=ON, ADX=OFF, EMA=OFF, SMA=OFF.
            // 2) Volatility: ATR(1) > ATR(10) — [v5.23.93 OFF] 사용자 "눌림 매수" 전략과 모순.
            //   이 필터는 변동성 폭발(돌파봉)만 통과 → 조용한 눌림목 차단 + 저변동 메이저(XRP) 차단.
            //   사용자 전략은 "1h 대세 + 하단 지지 눌림" 이라 변동성수축 구간 진입이 정상 → 끔.
            //   (하락방향 보호는 REGIME + 1h 대세필터(IsEntryAllowed)가 담당.)
            r.Atr1  = CalcTR(kl, idx);
            r.Atr10 = CalcATR(kl, idx, 10);
            // [v5.24.4] OFF — v5.23.98 재활성화가 진입 0건의 주범(라이브 24h VOLATILITY 차단 다수). "눌림 매수" 전략과 모순:
            //   ATR1>ATR10은 변동성 폭발(돌파봉)만 통과 → 조용한 눌림목·저변동 메이저(XRP) 차단. v5.23.93서 OFF했던 것 재차 끔.
            // if (r.Atr1 <= r.Atr10) { r.BlockReason = "VOLATILITY"; return r; }

            // 3) Regime: [v5.23.95 OFF] jdehorty KLMF 원본대로 짰으나 실거래서 가드통과 심볼 ~100% 차단
            //   (6/20 00시 이후 진입 0건, REGIME 차단 157k = 압도적 1위). 충실 포팅이지만 이 봇 환경(15m 1500봉)
            //   에선 거의 항상 normalized_slope_decline < -0.1 → 전면차단. 하락방향 보호는 1h 대세필터
            //   (LCC_BELOW_1H_EMA20 + LCC_BTC_1H_DOWNTREND) + DBB 과열 + RANGE_TOP 이 담당하므로 중복. 끔.
            r.RegimeSlope = CalcRegimeSlope(kl, idx);
            // [v5.24.4] OFF (재차) — v5.23.98이 jdehorty 충실복원하며 ON했으나, v5.23.95와 동일 증상 재발: 라이브 24h
            //   REGIME 차단 78,499건 = 압도적 1위, 진입 0건(마지막 6/25). 이 봇 환경(15m 1500봉)에선 slope가 거의 항상
            //   -0.3~-0.9로 찍혀 < -0.1 전면차단(캘리브레이션 미스매치). 하락방향 보호는 1h 대세필터 + DBB 과열차단이 담당.
            // if (r.RegimeSlope < -0.1) { r.BlockReason = $"REGIME ({r.RegimeSlope:F3})"; return r; }

            // 4) ADX(14) ≥ 30  — [v5.25.28 재활성화] 3년 백테(--lcc-tune) 결과:
            //   ADX 게이트 OFF(≤20 통과)=브레이크이븐(총 -$1.76, 건당 -$0.006).
            //   ADX≥30 게이트 ON = 총 +$402, 건당 +$2.39, 승률 66%→69%, 흑자월 33/48.
            //   메커니즘: "진입량 줄이기"가 아니라 KNN 엣지가 실재하는 강추세 구간만 선별
            //   (건당손익이 음→양으로 뒤집힘 = 거래 '질' 개선). 횡보(저ADX) 구간의 KNN=노이즈 → 차단.
            //   ADX는 jdehorty 원본의 핵심 필터. K=8·1h·BTC상승·반대신호청산 스펙 기준 최적.
            r.Adx = CalcADX(kl, idx, 14);
            if (r.Adx < 30.0) { r.BlockReason = $"ADX_WEAK ({r.Adx:F1}<30, 저추세 횡보구간 차단)"; return r; }

            // 5) close > EMA(200)  — [v5.23.90 비활성화: jdehorty 기본 OFF, 1h EMA20 게이트가 추세 담당]
            r.Ema200 = CalcEMA(kl, idx, 200);
            // if ((double)kl[idx].ClosePrice <= r.Ema200) { r.BlockReason = "EMA200"; return r; }

            // 6) close > SMA(200)  — [v5.23.90 비활성화: jdehorty 기본 OFF]
            r.Sma200 = CalcSMA(kl, idx, 200);
            // if ((double)kl[idx].ClosePrice <= r.Sma200) { r.BlockReason = "SMA200"; return r; }

            // 7) NW Kernel bullish = yhat1 >= yhat1[1] (jdehorty 원본: 추정치가 직전봉 대비 상승)
            r.NwKernel = CalcNWKernel(kl, idx);
            r.NwKernelPrev2 = idx >= 1 ? CalcNWKernel(kl, idx - 1) : r.NwKernel;
            if (r.NwKernel < r.NwKernelPrev2) { r.BlockReason = "NW_KERNEL"; return r; }

            // 8) [v5.23.94] 더블 볼린저밴드 진입 확인 필터 (사용자 지정) — LCC LONG 의 "위치" 확인.
            //   BB(20)의 1σ/2σ 두 밴드로 존 구분. close > mid+1σ = Kathy Lien '매수존 상단'(과열/고점) → 추격 차단.
            //   허용: close ≤ mid+1σ (중립~하단 지지 눌림). "고점 추격 금지 + 하단 지지 눌림" 원칙 반영.
            //   σ 임계 조정 가능: 더 빡세게(눌림만)=mid 기준, 더 느슨=mid+2σ 기준.
            CalcBB(kl, idx, 20, 1.0, out double dbbMid, out double dbbUp1, out _);
            double dbbClose = (double)kl[idx].ClosePrice;
            if (dbbMid > 0 && dbbClose > dbbUp1)
            {
                r.BlockReason = $"DBB_OVEREXTENDED (close={dbbClose:F6} > +1σ={dbbUp1:F6} — 고점 추격 차단)";
                return r;
            }

            // 8.5) [v5.25.11] higher-low 확인 — 눌림 '하락 중'(저점 갱신 중) 진입 차단. 사용자 지시:
            //   "횡보→상승→눌림목인데 눌림목 내려갈 때 계속 손실" = 데드캣 양봉 바운스마다 롱 → 칼날 잡기.
            //   원인: 남은 게이트(KNN/커널/DBB/5m양봉)가 하락 중 작은 반등에도 전부 통과 → 바닥 미확인 진입.
            //   해결: 최근 lookback 창의 스윙저점 이후 '마지막 두 봉의 저점이 모두 그 저점 위'일 때만 진입
            //         (= higher-low 구조 확인, 방금 신저점/1봉짜리 반등은 차단, 확립된 반등만 통과).
            //   실측 검산(XRP 7/3): 23:45·00:15 하락중 진입 차단, 01:00 실제 바닥반등은 통과.
            {
                const int hlLook = 10;
                int hlStart = Math.Max(0, idx - hlLook);
                double swingLow = double.MaxValue;
                for (int b = hlStart; b <= idx; b++)
                {
                    double lo = (double)kl[b].LowPrice;
                    if (lo < swingLow) swingLow = lo;
                }
                double lowNow = (double)kl[idx].LowPrice;
                double lowPrev = idx >= 1 ? (double)kl[idx - 1].LowPrice : lowNow;
                // 마지막 두 봉이 모두 스윙저점보다 높게 유지 = 눌림 바닥 확인 후 반등. 아니면(=최근봉이 신저점) 차단.
                if (!(lowNow > swingLow && lowPrev > swingLow))
                {
                    r.BlockReason = $"NO_HIGHER_LOW (저점갱신중 눌림바닥 미확인, swingLow={swingLow:F6} low={lowNow:F6})";
                    return r;
                }
            }

            // 9) [v5.24.3] 캔들 형태 진입금지 제거 (사용자 지정 A) — 검증 jdehorty 구성엔 없던 봇 추가 필터.
            //   펌핑 돌파봉은 꼬리가 길어 PREV_LONG_TAIL/BEARISH_REVERSAL에 자주 걸려 상승추세 LCC 진입을 막던 문제.
            //   (반전캔들 '청산'(PositionMonitorService)은 유지 — 진입만 푼다.)
            var lastCandle = kl[idx];
            if (false && IsLongTail(lastCandle)) { r.BlockReason = "PREV_LONG_TAIL (꼬리>몸통 거부캔들)"; return r; }
            if (false && IsBearishReversalCandle(lastCandle)) { r.BlockReason = "BEARISH_REVERSAL (음봉 작은몸통 긴꼬리)"; return r; }

            // [v5.23.90] 아래 3개 커스텀 가드(BB_MID_BELOW / BB_WALK_BROKEN / CONSOL) 비활성화 —
            //   전부 "close가 중심선 위 / 박스 상단 돌파"를 요구 = 눌림(하단 지지) 매수와 정면 충돌, 오히려 꼭대기 추격 강요.
            //   사용자 지시(하단 지지 눌림 매수 + 고점 추격 금지)와 반대라 제거. 고점/눌림/추세는 IsEntryAllowed 게이트 + 1m 확인이 담당.
#if false
            // 9) [v5.23.5] BB(20,2) 중심선 유지 확인 — 진입 시 close > BB mid 필수
            CalcBB(kl, idx, 20, 2.0, out double bbMid9, out _, out _);
            if (bbMid9 > 0 && (double)kl[idx].ClosePrice <= bbMid9)
            {
                r.BlockReason = $"BB_MID_BELOW (close={kl[idx].ClosePrice:F6} <= mid={bbMid9:F6})";
                return r;
            }

            // 10) [v5.23.6] BB upper walking 깨짐 차단 (사용자 캡처 진단)
            //   직전 7봉 중 high >= upper 인 정점봉 발견
            //   정점봉 close < upper (BB 안 거부 마감) AND 그 후 모든 봉 high < upper (회복 못함)
            //   → walking the band 끝 + mean reversion 시작 → 차단
            //   허용: walking 중 (정점봉 close >= upper) OR 음봉 후 회복 (이후 봉 high >= upper)
            int peakIdx10 = -1;
            for (int b = idx; b >= Math.Max(0, idx - 6); b--)
            {
                CalcBB(kl, b, 20, 2.0, out _, out double upperB, out _);
                if (upperB > 0 && (double)kl[b].HighPrice >= upperB) { peakIdx10 = b; break; }
            }
            if (peakIdx10 >= 0 && peakIdx10 < idx)
            {
                CalcBB(kl, peakIdx10, 20, 2.0, out _, out double peakUpper, out _);
                bool peakRejected = peakUpper > 0 && (double)kl[peakIdx10].ClosePrice < peakUpper;
                if (peakRejected)
                {
                    bool recovered = false;
                    for (int b = peakIdx10 + 1; b <= idx; b++)
                    {
                        CalcBB(kl, b, 20, 2.0, out _, out double upperC, out _);
                        if (upperC > 0 && (double)kl[b].HighPrice >= upperC) { recovered = true; break; }
                    }
                    if (!recovered)
                    {
                        r.BlockReason = $"BB_WALK_BROKEN (peak {idx - peakIdx10}봉전 close<upper, 회복실패)";
                        return r;
                    }
                }
            }

            // 8) [v5.23.5] 횡보 → 방향 돌파 확인 (사용자 지시)
            //    "옆으로 횡보하다 하락하는지 옆으로 와서 올라가는지를 봐야"
            //    최근 5봉 range < 2.5% (횡보 구간) → 현재 close 가
            //      consol 상단 돌파 → ✅ 진입 허용
            //      consol 하단 이탈 → ❌ 차단 (하락 방향)
            //      구간 내 → ❌ 차단 (방향 미확정)
            if (idx >= 5)
            {
                double consolHigh = 0, consolLow = double.MaxValue;
                for (int b = idx - 5; b <= idx - 1; b++)
                {
                    double h = (double)kl[b].HighPrice;
                    double l = (double)kl[b].LowPrice;
                    if (h > consolHigh) consolHigh = h;
                    if (l < consolLow) consolLow = l;
                }
                if (consolLow > 0)
                {
                    double consolRangePct = (consolHigh - consolLow) / consolLow * 100.0;
                    // 횡보 정의: 5봉 range < 2.5% (좁은 박스)
                    if (consolRangePct < 2.5)
                    {
                        double curClose = (double)kl[idx].ClosePrice;
                        if (curClose < consolHigh)
                        {
                            // 상단 돌파 안 함 → 횡보 내 OR 하락
                            string sub = curClose < consolLow ? "DOWN_BREAK" : "INSIDE_RANGE";
                            r.BlockReason = $"CONSOL_{sub} (range {consolRangePct:F2}%, [{consolLow:F4}~{consolHigh:F4}], close {curClose:F4})";
                            return r;
                        }
                        // curClose > consolHigh → 위로 돌파 → 통과
                    }
                    // 횡보 아닌 경우 (range >= 2.5%) → 일반 흐름, 통과
                }
            }
#endif

            r.Passed = true;
            return r;
        }

        // BB(period, multiplier) — 마지막 봉 기준 mid/upper/lower 반환
        public static void CalcBB(List<IBinanceKline> kl, int idx, int period, double mult,
            out double mid, out double upper, out double lower)
        {
            mid = 0; upper = 0; lower = 0;
            if (idx < period - 1) return;
            double sum = 0;
            for (int q = idx - period + 1; q <= idx; q++) sum += (double)kl[q].ClosePrice;
            mid = sum / period;
            double var = 0;
            for (int q = idx - period + 1; q <= idx; q++)
            {
                double d = (double)kl[q].ClosePrice - mid;
                var += d * d;
            }
            double std = Math.Sqrt(var / period);
            upper = mid + mult * std;
            lower = mid - mult * std;
        }

        // 청산 신호 평가 (barsHeld ≥ MinBarsHeldForExit 후만 호출)
        // 반환: ("KNN_FLIP" | "KERNEL_FLIP" | "" if no exit signal)
        public static string EvaluateExit(
            List<IBinanceKline> kl,
            int barsHeld,
            int knnSignal,
            double nwkNow,
            double nwkPrev1,
            double nwkPrev2,
            double nwkPrev3)
        {
            if (barsHeld < MinBarsHeldForExit) return "";
            bool kernelWasUp = nwkPrev1 > nwkPrev3;
            bool kernelNowDown = nwkNow < nwkPrev2;
            if (kernelWasUp && kernelNowDown) return "KERNEL_FLIP";
            if (knnSignal == -1) return "KNN_FLIP";
            return "";
        }

        // ── 필터 계산 함수 (라이브/백테스트 공유) ──
        public static double CalcEMA(List<IBinanceKline> kl, int idx, int period)
        {
            if (idx < period - 1) return (double)kl[idx].ClosePrice;
            double k = 2.0 / (period + 1);
            double ema = (double)kl[idx - period + 1].ClosePrice;
            for (int q = idx - period + 2; q <= idx; q++)
                ema = (double)kl[q].ClosePrice * k + ema * (1 - k);
            return ema;
        }

        public static double CalcSMA(List<IBinanceKline> kl, int idx, int period)
        {
            if (idx < period - 1) return (double)kl[idx].ClosePrice;
            double sum = 0;
            for (int q = idx - period + 1; q <= idx; q++) sum += (double)kl[q].ClosePrice;
            return sum / period;
        }

        // [v5.23.96] 캔들 형태 (사용자 지정 규칙용). 라이브/백테스트 공유.
        //   꼬리(상단+하단) 가 몸통보다 길면 = 롱윅/거부 캔들.
        public static bool IsLongTail(IBinanceKline k)
        {
            decimal body = Math.Abs(k.ClosePrice - k.OpenPrice);
            decimal wick = (k.HighPrice - Math.Max(k.OpenPrice, k.ClosePrice)) + (Math.Min(k.OpenPrice, k.ClosePrice) - k.LowPrice);
            return wick > body;
        }
        //   음봉 + 작은몸통(≤range 40%) + 긴꼬리(꼬리>몸통) = 반전·소진 캔들.
        public static bool IsBearishReversalCandle(IBinanceKline k)
        {
            decimal range = k.HighPrice - k.LowPrice;
            if (range <= 0) return false;
            decimal body = Math.Abs(k.ClosePrice - k.OpenPrice);
            decimal wick = range - body;
            return k.ClosePrice < k.OpenPrice && body <= range * 0.4m && wick > body;
        }

        public static double CalcTR(List<IBinanceKline> kl, int idx)
        {
            if (idx < 1) return (double)(kl[idx].HighPrice - kl[idx].LowPrice);
            double h = (double)kl[idx].HighPrice, l = (double)kl[idx].LowPrice, pc = (double)kl[idx - 1].ClosePrice;
            return Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }

        public static double CalcATR(List<IBinanceKline> kl, int idx, int period)
        {
            if (idx < period) return CalcTR(kl, idx);
            double sum = 0;
            for (int q = idx - period + 1; q <= idx; q++) sum += CalcTR(kl, q);
            return sum / period;
        }

        // ADX(14) Wilder smoothing — 마지막 봉 smoothed ADX 반환
        public static double CalcADX(List<IBinanceKline> kl, int idx, int period)
        {
            if (idx < period * 2) return 0.0;
            double[] tr = new double[idx + 1], pdm = new double[idx + 1], ndm = new double[idx + 1];
            for (int i = 1; i <= idx; i++)
            {
                double high = (double)kl[i].HighPrice, low = (double)kl[i].LowPrice, prevClose = (double)kl[i - 1].ClosePrice;
                tr[i] = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                double upMove = high - (double)kl[i - 1].HighPrice;
                double downMove = (double)kl[i - 1].LowPrice - low;
                pdm[i] = upMove > downMove && upMove > 0 ? upMove : 0;
                ndm[i] = downMove > upMove && downMove > 0 ? downMove : 0;
            }
            double atr = 0, pdmS = 0, ndmS = 0;
            for (int i = 1; i <= period; i++) { atr += tr[i]; pdmS += pdm[i]; ndmS += ndm[i]; }
            double adx = 0;
            bool adxInit = false;
            for (int i = period + 1; i <= idx; i++)
            {
                atr  = atr  - (atr  / period) + tr[i];
                pdmS = pdmS - (pdmS / period) + pdm[i];
                ndmS = ndmS - (ndmS / period) + ndm[i];
                if (atr < 1e-12) continue;
                double pdi = 100.0 * pdmS / atr;
                double ndi = 100.0 * ndmS / atr;
                double dx = (pdi + ndi) > 1e-12 ? 100.0 * Math.Abs(pdi - ndi) / (pdi + ndi) : 0;
                if (!adxInit) { adx = dx; adxInit = true; }
                else adx = (adx * (period - 1) + dx) / period;
            }
            return adx;
        }

        // [v5.23.93] jdehorty regime_filter 원본 충실 포팅 (이전 EMA50-기울기 근사는 오류 — 트뷰 신호 차단 원인).
        //   src = ohlc4. value1 := 0.2*(src-src[1]) + 0.8*value1[1];  value2 := 0.1*(high-low) + 0.8*value2[1]
        //   omega = |value1/value2|;  alpha = (-omega^2 + sqrt(omega^4 + 16*omega^2)) / 8
        //   klmf := alpha*src + (1-alpha)*klmf[1];  absCurveSlope = |klmf - klmf[1]|
        //   normalized_slope_decline = (absCurveSlope - EMA(absCurveSlope,200)) / EMA(absCurveSlope,200)
        //   통과조건: normalized_slope_decline >= threshold(-0.1).
        public static double CalcRegimeSlope(List<IBinanceKline> kl, int idx)
        {
            if (idx < 2) return 0.0;
            double value1 = 0.0, value2 = 0.0, klmf = 0.0;
            double emaAbsSlope = 0.0;          // EMA(absCurveSlope, 200)
            double kEma = 2.0 / (200.0 + 1.0);
            double absSlopeNow = 0.0;
            for (int i = 0; i <= idx; i++)
            {
                double src = (double)(kl[i].OpenPrice + kl[i].HighPrice + kl[i].LowPrice + kl[i].ClosePrice) / 4.0;
                double srcPrev = i > 0
                    ? (double)(kl[i - 1].OpenPrice + kl[i - 1].HighPrice + kl[i - 1].LowPrice + kl[i - 1].ClosePrice) / 4.0
                    : src;
                double hl = (double)(kl[i].HighPrice - kl[i].LowPrice);
                value1 = 0.2 * (src - srcPrev) + 0.8 * value1;
                value2 = 0.1 * hl + 0.8 * value2;
                double omega = Math.Abs(value2) > 1e-12 ? Math.Abs(value1 / value2) : 0.0;
                double alpha = (-(omega * omega) + Math.Sqrt(omega * omega * omega * omega + 16.0 * omega * omega)) / 8.0;
                double newKlmf = alpha * src + (1.0 - alpha) * klmf;
                absSlopeNow = i > 0 ? Math.Abs(newKlmf - klmf) : 0.0;
                klmf = newKlmf;
                emaAbsSlope = i == 0 ? absSlopeNow : absSlopeNow * kEma + emaAbsSlope * (1.0 - kEma);
            }
            if (emaAbsSlope < 1e-12) return 0.0;
            return (absSlopeNow - emaAbsSlope) / emaAbsSlope;
        }

        // Nadaraya-Watson Rational Quadratic kernel estimator (h=8, r=8, x=25)
        public static double CalcNWKernel(List<IBinanceKline> kl, int idx, int h = 8, double r = 8.0, int x = 25)
        {
            int look = Math.Min(idx, 200);
            double num = 0, den = 0;
            for (int i = 0; i < look; i++)
            {
                int srcIdx = idx - i;
                if (srcIdx < 0) break;
                double w = Math.Pow(1.0 + (i * i) / (h * h * 2.0 * r), -r);
                num += (double)kl[srcIdx].ClosePrice * w;
                den += w;
            }
            return den > 1e-12 ? num / den : (double)kl[idx].ClosePrice;
        }

        // [v5.23.59 fix] jdehorty 학습 라벨 = trailing 4봉 방향:
        //   y_train_series = src[4] < src[0] ? short : src[4] > src[0] ? long : neutral
        //   src[0]=현재봉(idx) close, src[4]=4봉 전(idx-4) close.
        //   라벨(idx) = sign(close[idx] - close[idx-4]).  +1=long, -1=short, 0=neutral.
        //   (이전: forward close[idx+4] vs close[idx] — Pine 원본과 다른 비표준 라벨,
        //    KNN 이 "현재와 닮은 과거 봉들의 *그 시점* 4봉 추세"를 합산하는 jdehorty 설계와 불일치 → 예측 랜덤화 원인)
        //   futureBars 인자명은 호환 유지(=lookback 봉수).
        public static int LabelForBar(List<IBinanceKline> kl, int idx, int futureBars = 4)
        {
            if (idx - futureBars < 0) return 0;
            decimal nowC  = kl[idx].ClosePrice;
            decimal prevC = kl[idx - futureBars].ClosePrice;
            return nowC > prevC ? 1 : (nowC < prevC ? -1 : 0);
        }

        // 봉 idx 의 KNN 신호 (예측만, 학습 안 함)
        public static (int sig, float winRate, bool ready) PredictAtBar(
            List<IBinanceKline> kl, int idx, LorentzianAnnEngine engine, int featureWindow = 500)
        {
            if (idx < 60) return (0, 0f, false);
            int wStart = Math.Max(0, idx - (featureWindow - 1));
            var win = kl.GetRange(wStart, idx - wStart + 1);
            var feats = LorentzianFeatures.Extract(win);
            if (feats == null) return (0, 0f, false);
            var p = engine.Predict(feats);
            if (!p.IsReady || p.K == 0) return (0, 0f, false);
            int s = p.Prediction > 0 ? 1 : (p.Prediction < 0 ? -1 : 0);
            float wr = (float)p.PositiveVotes / p.K;
            return (s, wr, true);
        }
    }
}
