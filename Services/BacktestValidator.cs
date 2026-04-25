using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.19.12 Phase 1] AI 모델 예측 정확도 백테스트 측정 하네스
    ///
    /// 목적:
    ///   현재 모델이 "실제로 미래 가격 상승을 예측하는가" 정량 측정
    ///   사용자 요구: "차트 데이터로 미리 예측을 못하고 있어 — 차트 데이터로 테스트하면서 로직을 계속 수정"
    ///
    /// 동작:
    ///   1) BacktestBootstrapTrainer 로 최근 N일치 5m 캔들 → 라벨 데이터 생성 (실제 미래 가격 기반)
    ///   2) 각 variant 모델(Default/Major/Pump/Spike) Predict 호출
    ///   3) 예측 점수 vs 실제 라벨 비교 → 혼동행렬
    ///   4) precision/recall/F1/win-rate 계산
    ///
    /// 출력:
    ///   precision = 모델이 "진입" 예측한 케이스 중 실제 +0.8% 도달 비율 (60% 이상이 목표)
    ///   recall    = 실제 상승 케이스 중 모델이 잡아낸 비율
    ///   win-rate  = 진입 신호 전체 중 승리 비율
    /// </summary>
    public class BacktestValidator
    {
        private readonly DbManager _db;
        private readonly MultiTimeframeFeatureExtractor _extractor;
        public event Action<string>? OnLog;

        public BacktestValidator(DbManager db, MultiTimeframeFeatureExtractor extractor)
        {
            _db = db;
            _extractor = extractor;
        }

        public class ValidationResult
        {
            public string VariantTag { get; set; } = "";
            public int TotalFeatures { get; set; }
            public int ActualPositives { get; set; }       // 실제 상승 도달 케이스
            public int ActualNegatives { get; set; }       // 실제 미상승/하락 케이스
            public int PredictedPositives { get; set; }    // 모델이 진입하라고 한 케이스
            public int TruePositives { get; set; }         // 진입 예측 + 실제 상승
            public int FalsePositives { get; set; }        // 진입 예측 + 실제 미상승 (손실)
            public int FalseNegatives { get; set; }        // 진입 안함 예측 + 실제 상승 (놓친 기회)
            public int TrueNegatives { get; set; }         // 진입 안함 + 실제 미상승 (정상 회피)
            public double Precision => PredictedPositives > 0 ? (double)TruePositives / PredictedPositives : 0;
            public double Recall    => ActualPositives > 0 ? (double)TruePositives / ActualPositives : 0;
            public double F1        => (Precision + Recall) > 0 ? 2 * Precision * Recall / (Precision + Recall) : 0;
            public double WinRate   => PredictedPositives > 0 ? (double)TruePositives / PredictedPositives : 0;
            public double AvgScoreOnPositives { get; set; }  // 실제 상승 케이스에서 모델 평균 점수
            public double AvgScoreOnNegatives { get; set; }  // 실제 미상승 케이스에서 모델 평균 점수

            public string Format()
            {
                return $"[{VariantTag}] N={TotalFeatures} actual+={ActualPositives}/-={ActualNegatives} | " +
                       $"pred+={PredictedPositives} TP={TruePositives} FP={FalsePositives} FN={FalseNegatives} | " +
                       $"precision={Precision:P1} recall={Recall:P1} F1={F1:P1} | " +
                       $"avgScore(actual+)={AvgScoreOnPositives:F3} avgScore(actual-)={AvgScoreOnNegatives:F3}";
            }
        }

        /// <summary>
        /// 단일 variant 모델 검증
        /// </summary>
        public ValidationResult Validate(
            string variantTag,
            EntryTimingMLTrainer trainer,
            List<MultiTimeframeEntryFeature> testFeatures,
            float threshold = 0.5f)
        {
            var r = new ValidationResult { VariantTag = variantTag, TotalFeatures = testFeatures.Count };

            double sumPosScore = 0, sumNegScore = 0;
            int posCnt = 0, negCnt = 0;

            foreach (var f in testFeatures)
            {
                bool actualPositive = f.ShouldEnter;
                if (actualPositive) r.ActualPositives++; else r.ActualNegatives++;

                EntryTimingPrediction? pred = null;
                try { pred = trainer.Predict(f); } catch { /* 모델 미로드 등 */ }
                if (pred == null) continue;

                float score = pred.Probability;
                bool predictedPositive = score >= threshold;
                if (predictedPositive) r.PredictedPositives++;

                if (actualPositive)  { sumPosScore += score; posCnt++; }
                else                 { sumNegScore += score; negCnt++; }

                if (predictedPositive && actualPositive)   r.TruePositives++;
                else if (predictedPositive && !actualPositive) r.FalsePositives++;
                else if (!predictedPositive && actualPositive) r.FalseNegatives++;
                else                                            r.TrueNegatives++;
            }

            r.AvgScoreOnPositives = posCnt > 0 ? sumPosScore / posCnt : 0;
            r.AvgScoreOnNegatives = negCnt > 0 ? sumNegScore / negCnt : 0;
            return r;
        }

        /// <summary>
        /// 전체 4 variant 검증 + 텍스트 보고서 생성
        /// </summary>
        public async Task<string> ValidateAllVariantsAsync(
            EntryTimingMLTrainer defaultTrainer,
            EntryTimingMLTrainer majorTrainer,
            EntryTimingMLTrainer pumpTrainer,
            EntryTimingMLTrainer spikeTrainer,
            IEnumerable<string> symbols,
            HashSet<string> majorSymbols,
            int daysBack = 7,
            float threshold = 0.5f,
            CancellationToken token = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📊 [BACKTEST_VALIDATOR] {daysBack}일 walk-forward 검증 시작 (threshold={threshold:F2})");

            var bootstrap = new BacktestBootstrapTrainer(_db, _extractor);
            var allDefault = new List<MultiTimeframeEntryFeature>();
            var allMajor   = new List<MultiTimeframeEntryFeature>();
            var allPump    = new List<MultiTimeframeEntryFeature>();
            var allSpike   = new List<MultiTimeframeEntryFeature>();

            int symbolCount = 0;
            int idx = 0;
            int total = symbols.Count();
            foreach (var sym in symbols)
            {
                idx++;
                if (token.IsCancellationRequested) break;
                try
                {
                    var (positives, negatives) = await bootstrap.BacktestSymbolAsync(sym, daysBack: daysBack, token: token);
                    var combined = positives.Concat(negatives).ToList();
                    if (combined.Count == 0)
                    {
                        if (idx % 5 == 0) OnLog?.Invoke($"📊 [ML 검증] {idx}/{total} | 누적 처리={symbolCount} 샘플={allDefault.Count}");
                        continue;
                    }

                    bool isMajor = majorSymbols.Contains(sym);
                    allDefault.AddRange(combined);
                    if (isMajor) allMajor.AddRange(combined);
                    else        allPump.AddRange(combined);

                    foreach (var f in combined)
                    {
                        if (f.ShouldEnter && f.ActualProfitPct >= 1.5f) allSpike.Add(f);
                    }

                    symbolCount++;
                    if (symbolCount % 5 == 0)
                        OnLog?.Invoke($"📊 [ML 검증] {idx}/{total} | 처리={symbolCount} D={allDefault.Count} M={allMajor.Count} P={allPump.Count} S={allSpike.Count}");
                }
                catch (Exception ex) { sb.AppendLine($"⚠️ {sym} 데이터 로드 실패: {ex.Message}"); }
            }

            sb.AppendLine($"  심볼 처리: {symbolCount}개 / 누적 샘플: Default={allDefault.Count} Major={allMajor.Count} Pump={allPump.Count} Spike={allSpike.Count}");
            sb.AppendLine();

            var rDefault = Validate("Default", defaultTrainer, allDefault, threshold);
            var rMajor   = Validate("Major",   majorTrainer,   allMajor,   threshold);
            var rPump    = Validate("Pump",    pumpTrainer,    allPump,    threshold);
            var rSpike   = Validate("Spike",   spikeTrainer,   allSpike,   threshold);

            sb.AppendLine(rDefault.Format());
            sb.AppendLine(rMajor.Format());
            sb.AppendLine(rPump.Format());
            sb.AppendLine(rSpike.Format());
            sb.AppendLine();
            sb.AppendLine("판정 기준: precision ≥ 60% = 양호, ≥ 50% = 보통, < 50% = 모델 결함");
            sb.AppendLine("avgScore 차이 (positives - negatives) > 0.15 = 모델 식별력 있음");

            string report = sb.ToString();
            OnLog?.Invoke(report);
            return report;
        }

        /// <summary>
        /// [v5.20.0] LorentzianV2 per-symbol 검증 — Pine 스타일 정확도 측정
        /// 5m 캔들 sliding window: feature 추출 + 4봉 후 실제 가격 변화 vs Engine 예측 sign 일치 여부
        /// </summary>
        public async Task<string> ValidateLorentzianV2Async(
            LorentzianV2.LorentzianV2Service v2,
            IEnumerable<string> symbols,
            int daysBack = 7,
            CancellationToken token = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📊 [LorentzianV2] per-symbol {daysBack}일 검증 시작");

            int globalTotal = 0, globalCorrect = 0;
            int globalLong = 0, globalShort = 0, globalNeutral = 0;
            var perSym = new List<(string sym, int n, int correct, double winRate)>();

            int v2idx = 0;
            int v2total = symbols.Count();
            foreach (var sym in symbols)
            {
                v2idx++;
                if (token.IsCancellationRequested) break;
                if (v2idx % 5 == 0)
                    OnLog?.Invoke($"📊 [V2 검증] {v2idx}/{v2total} | 누적 {globalTotal}건 정답 {globalCorrect}");
                try
                {
                    int max5m = daysBack * 24 * 12 + 100;
                    var raw = await _db.GetCandleDataByIntervalAsync(sym, "5m", max5m);
                    if (raw == null || raw.Count < 100) continue;
                    var asc = raw.OrderBy(c => c.OpenTime)
                                 .Select(c => new TradingBot.Services.KlineAdapter(c))
                                 .Cast<Binance.Net.Interfaces.IBinanceKline>()
                                 .ToList();
                    if (asc.Count < 65) continue;

                    int symN = 0, symCorrect = 0;
                    for (int i = 60; i < asc.Count - 4; i++)
                    {
                        var slice = asc.GetRange(0, i + 1);
                        var pred = v2.Predict(sym, slice);
                        if (!pred.IsReady || pred.Prediction == 0) continue;

                        decimal nowClose = asc[i].ClosePrice;
                        decimal future4 = asc[i + 4].ClosePrice;
                        int actualSign = future4 > nowClose ? 1 : future4 < nowClose ? -1 : 0;
                        if (actualSign == 0) continue;

                        symN++;
                        bool correct = (pred.Prediction > 0 && actualSign > 0) || (pred.Prediction < 0 && actualSign < 0);
                        if (correct) symCorrect++;
                        if (pred.Prediction > 0) globalLong++; else globalShort++;
                    }
                    if (symN > 0)
                    {
                        double wr = (double)symCorrect / symN * 100.0;
                        perSym.Add((sym, symN, symCorrect, wr));
                        globalTotal += symN;
                        globalCorrect += symCorrect;
                    }
                }
                catch (Exception ex) { sb.AppendLine($"⚠️ {sym} 검증 실패: {ex.Message}"); }
            }

            // 글로벌 win rate
            double overallWr = globalTotal > 0 ? (double)globalCorrect / globalTotal * 100.0 : 0;
            sb.AppendLine($"  전체: {globalTotal}건 / 정답 {globalCorrect}건 / win-rate={overallWr:F2}% (LONG={globalLong} SHORT={globalShort})");
            sb.AppendLine();
            // [v5.20.2] N>=30 필터로 통계 신뢰도 확보
            var filtered = perSym.Where(p => p.n >= 30).ToList();
            sb.AppendLine($"  유효 심볼 (N>=30): {filtered.Count} / 전체 {perSym.Count}");
            sb.AppendLine();
            sb.AppendLine("  [TOP 15 per-symbol win-rate]");
            foreach (var s in filtered.OrderByDescending(p => p.winRate).Take(15))
                sb.AppendLine($"    {s.sym,-14} N={s.n,5} 정답={s.correct,5} win-rate={s.winRate,6:F2}%");
            sb.AppendLine();
            sb.AppendLine("  [BOTTOM 10 per-symbol win-rate]");
            foreach (var s in filtered.OrderBy(p => p.winRate).Take(10))
                sb.AppendLine($"    {s.sym,-14} N={s.n,5} 정답={s.correct,5} win-rate={s.winRate,6:F2}%");

            string report = sb.ToString();
            OnLog?.Invoke(report);
            return report;
        }
    }
}
