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
                if (c5mRaw == null || c5mRaw.Count < 100) return (positives, negatives);
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
                for (int i = lookback5m; i < n - forward; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var entryBar = c5m[i];
                    decimal entryPrice = entryBar.ClosePrice;
                    if (entryPrice <= 0) continue;

                    decimal tpPrice = entryPrice * (1m + tpPct / 100m);
                    decimal slPrice = entryPrice * (1m - slPct / 100m);

                    // 다음 forward 봉 동안 TP / SL 어느 쪽 먼저 닿는지
                    bool win = false, loss = false;
                    for (int j = i + 1; j <= i + forward; j++)
                    {
                        if (c5m[j].HighPrice >= tpPrice) { win = true; break; }
                        if (c5m[j].LowPrice  <= slPrice) { loss = true; break; }
                    }
                    if (!win && !loss) continue; // 무승부 skip

                    // 각 TF 슬라이스 (asOf = entryBar.OpenTime, 그 이전까지)
                    var asOf = entryBar.OpenTime;
                    var slice5m  = SliceBefore(c5m,  asOf, 260);
                    var slice15m = SliceBefore(c15m, asOf, 260);
                    var slice1h  = SliceBefore(c1h,  asOf, 200);
                    var slice4h  = SliceBefore(c4h,  asOf, 120);
                    var slice1d  = SliceBefore(c1d,  asOf, 50);

                    if (slice5m.Count < 100 || slice15m.Count < 100 || slice1h.Count < 50 || slice4h.Count < 40 || slice1d.Count < 20) continue;

                    // M1 데이터는 DB 에 없거나 양 적음 → null 전달 (extractor 가 M1_Data_Valid=0 처리)
                    var feature = _extractor.BuildFeatureFromPreloaded(symbol, asOf, slice1d, slice4h, null, slice1h, slice15m, null);
                    if (feature == null) continue;

                    feature.ShouldEnter = win;
                    feature.ActualProfitPct = win ? (float)tpPct : -(float)slPct;

                    if (win) positives.Add(feature);
                    else negatives.Add(feature);
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
    /// [v5.18.0] CandleData → IBinanceKline 어댑터 (BacktestBootstrapTrainer 전용)
    /// MultiTimeframeFeatureExtractor 가 IBinanceKline 를 받기 때문에 brige 필요
    /// 모든 필드 구현 — 사용 안 하는 필드는 default 값
    /// </summary>
    internal sealed class KlineAdapter : IBinanceKline
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
