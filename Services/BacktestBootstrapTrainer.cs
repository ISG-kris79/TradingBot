using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.18.0] Backtest Bootstrap Trainer — DB 과거 차트로 즉시 학습 데이터 생성
    ///
    /// 동작:
    ///   1) DB CandleData 에서 각 심볼 N일치 5m / 15m / 1h kline 조회
    ///   2) sliding window 로 각 5m 봉을 가상 진입 시점으로 가정
    ///   3) 진입 후 12봉(60분) 동안 +0.8% 도달 = WIN, -1.0% 도달 = LOSS
    ///   4) 그 시점 feature 추출 (BuildFeatureFromPreloaded — look-ahead bias 제거)
    ///   5) variant 라우팅 → 라벨 샘플 추가
    ///   6) 4 variant 강제 재학습 → zip 즉시 생성
    ///
    /// 효과:
    ///   기존: 봇 진입한 trade 50건 학습 → 모델 거의 dead
    ///   신규: 60 심볼 × 30일 = 50만+ 샘플 학습 → 폭등 패턴도 학습됨
    /// </summary>
    public class BacktestBootstrapTrainer
    {
        private readonly DbManager _db;
        private readonly MultiTimeframeFeatureExtractor _extractor;
        public event Action<string>? OnLog;

        public BacktestBootstrapTrainer(DbManager db, MultiTimeframeFeatureExtractor extractor)
        {
            _db = db;
            _extractor = extractor;
        }

        /// <summary>
        /// 심볼 1개 backtest 라벨링.
        /// 반환: (positives 추가된 샘플 리스트, negatives 추가된 샘플 리스트)
        /// </summary>
        public async Task<(List<MultiTimeframeEntryFeature> positives, List<MultiTimeframeEntryFeature> negatives)>
            BacktestSymbolAsync(
                string symbol,
                int daysBack = 30,
                decimal tpPct = 0.8m,
                decimal slPct = 1.0m,
                int forwardWindowBars = 12,
                CancellationToken token = default)
        {
            var positives = new List<MultiTimeframeEntryFeature>();
            var negatives = new List<MultiTimeframeEntryFeature>();

            try
            {
                // 30일 = 5분봉 8640개 / 15분봉 2880개 / 1시간봉 720개 / 4시간봉 180개 / 1일봉 30개
                int max5m  = daysBack * 24 * 12 + 200;
                int max15m = daysBack * 24 * 4 + 200;
                int max1h  = daysBack * 24 + 200;
                int max4h  = daysBack * 6 + 200;
                int max1d  = daysBack + 50;

                var c5mRaw = await _db.GetCandleDataByIntervalAsync(symbol, "5m",  max5m);
                if (c5mRaw == null || c5mRaw.Count < 100)
                {
                    OnLog?.Invoke($"⚠️ [BACKTEST] {symbol} skip — 5m 데이터 부족 ({c5mRaw?.Count ?? 0}/100)");
                    return (positives, negatives);
                }
                var c15mRaw = await _db.GetCandleDataByIntervalAsync(symbol, "15m", max15m);
                var c1hRaw  = await _db.GetCandleDataByIntervalAsync(symbol, "1h",  max1h);
                var c4hRaw  = await _db.GetCandleDataByIntervalAsync(symbol, "4h",  max4h);
                var c1dRaw  = await _db.GetCandleDataByIntervalAsync(symbol, "1d",  max1d);

                // CandleData → KlineAdapter 변환 (OpenTime ASC 정렬)
                var c5m  = c5mRaw .OrderBy(c => c.OpenTime).Select(c => new KlineAdapter(c)).Cast<IBinanceKline>().ToList();
                var c15m = (c15mRaw ?? new()).OrderBy(c => c.OpenTime).Select(c => new KlineAdapter(c)).Cast<IBinanceKline>().ToList();
                var c1h  = (c1hRaw  ?? new()).OrderBy(c => c.OpenTime).Select(c => new KlineAdapter(c)).Cast<IBinanceKline>().ToList();
                var c4h  = (c4hRaw  ?? new()).OrderBy(c => c.OpenTime).Select(c => new KlineAdapter(c)).Cast<IBinanceKline>().ToList();
                var c1d  = (c1dRaw  ?? new()).OrderBy(c => c.OpenTime).Select(c => new KlineAdapter(c)).Cast<IBinanceKline>().ToList();

                // sliding window — 각 5m 봉을 가상 진입 시점으로 (i = 100 부터 — feature 추출용 lookback 확보)
                int lookback5m = 260;     // M15 100봉 = 5m 약 300봉
                int forward = forwardWindowBars;
                int n = c5m.Count;
                // [v5.19.0] Band Walk 대박 라벨용 확장 윈도우 (24봉 = 2시간) + 더 큰 TP
                int bandWalkWindow = Math.Max(forward, 24);
                decimal bandWalkTpPct = 3.0m;   // +3%
                decimal bandWalkSlPct = 1.5m;   // -1.5%

                for (int i = lookback5m; i < n - bandWalkWindow; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var entryBar = c5m[i];
                    decimal entryPrice = entryBar.ClosePrice;
                    if (entryPrice <= 0) continue;

                    // ── 1차 라벨: 표준 스캘핑 (+0.8% / -1% / 12봉) ──
                    decimal tpPrice = entryPrice * (1m + tpPct / 100m);
                    decimal slPrice = entryPrice * (1m - slPct / 100m);
                    bool win = false, loss = false;
                    for (int j = i + 1; j <= i + forward && j < n; j++)
                    {
                        if (c5m[j].HighPrice >= tpPrice) { win = true; break; }
                        if (c5m[j].LowPrice  <= slPrice) { loss = true; break; }
                    }

                    // [v5.19.10] 횡보 박스 NEGATIVE 라벨 강제
                    //   1차 TP/SL 무승부(=박스) 케이스 중 24봉(2시간) 내 ±0.5% 미만 횡보 → 강제 NEGATIVE 라벨
                    //   목적: AI가 "박스 진입 = 손실" 학습 → 진입 회피
                    if (!win && !loss)
                    {
                        // 24봉 윈도우 내 max-min 범위 확인
                        int boxEnd = Math.Min(i + 24, n - 1);
                        decimal boxMaxRise = 0m, boxMaxDrop = 0m;
                        for (int j = i + 1; j <= boxEnd; j++)
                        {
                            decimal risePct = (c5m[j].HighPrice - entryPrice) / entryPrice * 100m;
                            decimal dropPct = (c5m[j].LowPrice  - entryPrice) / entryPrice * 100m;
                            if (risePct > boxMaxRise) boxMaxRise = risePct;
                            if (dropPct < boxMaxDrop) boxMaxDrop = dropPct;
                        }
                        // 박스 판정: 24봉 동안 +0.5% 미달 AND -0.5% 미달
                        if (boxMaxRise < 0.5m && boxMaxDrop > -0.5m)
                        {
                            var asOfBox = entryBar.OpenTime;
                            var s5m  = SliceBefore(c5m,  asOfBox, 260);
                            var s15m = SliceBefore(c15m, asOfBox, 260);
                            var s1h  = SliceBefore(c1h,  asOfBox, 200);
                            var s4h  = SliceBefore(c4h,  asOfBox, 120);
                            var s1d  = SliceBefore(c1d,  asOfBox, 50);
                            if (s15m.Count >= 100 && s1h.Count >= 50 && s4h.Count >= 40 && s1d.Count >= 20)
                            {
                                var fBox = _extractor.BuildFeatureFromPreloaded(symbol, asOfBox, s1d, s4h, null, s1h, s15m, null, s5m);
                                if (fBox != null)
                                {
                                    fBox.ShouldEnter = false;
                                    fBox.ActualProfitPct = 0f;
                                    negatives.Add(fBox);
                                }
                            }
                        }
                        continue; // 박스 케이스는 일반 라벨 skip (대박 라벨도 skip)
                    }

                    // 각 TF 슬라이스 (asOf = entryBar.OpenTime, 그 이전까지)
                    var asOf = entryBar.OpenTime;
                    var slice5m  = SliceBefore(c5m,  asOf, 260);
                    var slice15m = SliceBefore(c15m, asOf, 260);
                    var slice1h  = SliceBefore(c1h,  asOf, 200);
                    var slice4h  = SliceBefore(c4h,  asOf, 120);
                    var slice1d  = SliceBefore(c1d,  asOf, 50);

                    // [v5.20.1] skip 사유 첫 1회만 출력 (반복 노이즈 방지)
                    if (slice5m.Count < 100 || slice15m.Count < 100 || slice1h.Count < 50 || slice4h.Count < 40 || slice1d.Count < 20)
                    {
                        if (i == lookback5m)
                            OnLog?.Invoke($"ℹ️ [BACKTEST] {symbol} TF 데이터 부족: 5m={slice5m.Count}/100 15m={slice15m.Count}/100 1h={slice1h.Count}/50 4h={slice4h.Count}/40 1d={slice1d.Count}/20");
                        continue;
                    }

                    // [v5.19.0] M5 슬라이스 전달 → Band Walk 피처 자동 추출
                    var feature = _extractor.BuildFeatureFromPreloaded(symbol, asOf, slice1d, slice4h, null, slice1h, slice15m, null, slice5m);
                    if (feature == null) continue;

                    feature.ShouldEnter = win;
                    feature.ActualProfitPct = win ? (float)tpPct : -(float)slPct;

                    if (win) positives.Add(feature);
                    else negatives.Add(feature);

                    // ── 2차 라벨: Band Walk 대박 (+3% / -1.5% / 24봉) ──
                    // 진입 시점 BB 상단 라이딩(walkCount ≥ 3 또는 streak ≥ 2)인 경우에만 추가 라벨
                    bool isBandWalk = feature.M5_BB_Walk_Count_10 >= 3f || feature.M5_Upper_Touch_Streak >= 2f;
                    if (!isBandWalk) continue;

                    decimal bigTp = entryPrice * (1m + bandWalkTpPct / 100m);
                    decimal bigSl = entryPrice * (1m - bandWalkSlPct / 100m);
                    bool bigWin = false, bigLoss = false;
                    for (int j = i + 1; j <= i + bandWalkWindow && j < n; j++)
                    {
                        if (c5m[j].HighPrice >= bigTp) { bigWin = true; break; }
                        if (c5m[j].LowPrice  <= bigSl) { bigLoss = true; break; }
                    }
                    if (!bigWin && !bigLoss) continue;

                    var bigFeature = _extractor.BuildFeatureFromPreloaded(symbol, asOf, slice1d, slice4h, null, slice1h, slice15m, null, slice5m);
                    if (bigFeature == null) continue;
                    bigFeature.ShouldEnter = bigWin;
                    bigFeature.ActualProfitPct = bigWin ? (float)bandWalkTpPct : -(float)bandWalkSlPct;
                    if (bigWin) positives.Add(bigFeature);
                    else negatives.Add(bigFeature);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [BACKTEST] {symbol} 처리 예외: {ex.Message}");
            }

            return (positives, negatives);
        }

        private static List<IBinanceKline> SliceBefore(List<IBinanceKline> all, DateTime asOf, int maxCount)
        {
            // OpenTime < asOf 만 (look-ahead bias 방지)
            var list = new List<IBinanceKline>(maxCount);
            // all 은 ASC 정렬 가정
            int upper = all.Count;
            int lo = 0, hi = upper;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (all[mid].OpenTime < asOf) lo = mid + 1;
                else hi = mid;
            }
            int end = lo;
            int start = Math.Max(0, end - maxCount);
            for (int k = start; k < end; k++) list.Add(all[k]);
            return list;
        }
    }

    /// <summary>
    /// [v5.18.0] CandleData → IBinanceKline 어댑터
    /// [v5.20.0] public 으로 변경 — LorentzianV2Service 등 다른 모듈도 사용
    /// </summary>
    public sealed class KlineAdapter : IBinanceKline
    {
        public KlineAdapter(TradingBot.Models.CandleData c)
        {
            OpenTime = c.OpenTime;
            CloseTime = c.CloseTime == default ? c.OpenTime.AddMinutes(5) : c.CloseTime;
            OpenPrice = c.Open;
            HighPrice = c.High;
            LowPrice = c.Low;
            ClosePrice = c.Close;
            Volume = (decimal)c.Volume;
        }

        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TradeCount { get; set; }
        public decimal TakerBuyBaseVolume { get; set; }
        public decimal TakerBuyQuoteVolume { get; set; }
    }
}
