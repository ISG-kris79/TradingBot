using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    public class PatternSnapshotInput
    {
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
        public DateTime SignalTime { get; set; } = DateTime.UtcNow;
        public decimal CurrentPrice { get; set; }
        public decimal PredictedPrice { get; set; }
        public decimal PredictedChange { get; set; }

        public double FinalScore { get; set; }
        public double AiScore { get; set; }
        public double ElliottScore { get; set; }
        public double VolumeScore { get; set; }
        public double RsiMacdScore { get; set; }
        public double BollingerScore { get; set; }
        public double ScoreGap { get; set; }

        public double AtrPercent { get; set; }
        public double HtfPenalty { get; set; }
        public double Adx { get; set; }
        public double PlusDi { get; set; }
        public double MinusDi { get; set; }
        public double Rsi { get; set; }
        public double MacdHist { get; set; }
        public double BbPosition { get; set; }
        public double VolumeRatio { get; set; }

        public string ComponentMix { get; set; } = string.Empty;
        public string ContextJson { get; set; } = string.Empty;

        public PatternMatchDecision Match { get; set; } = PatternMatchDecision.None;
    }

    public class PatternMatchDecision
    {
        public static PatternMatchDecision None { get; } = new PatternMatchDecision();

        public bool HasEnoughSamples { get; set; }
        public int SampleCount { get; set; }
        public int ProfitSamples { get; set; }
        public int LossSamples { get; set; }

        public double TopSimilarity { get; set; }
        public double EuclideanSimilarity { get; set; }
        public double CosineSimilarity { get; set; }
        public double MatchProbability { get; set; }
        public long? MatchedPatternId { get; set; }

        public bool IsSuperEntry { get; set; }
        public bool GateBypass { get; set; }
        public double ScoreBoost { get; set; }
        public double TopLossSimilarity { get; set; }
        public bool ShouldDeferEntry { get; set; }
        public string DeferReason { get; set; } = string.Empty;
        public decimal PositionSizeMultiplier { get; set; } = 1.0m;
        public decimal TakeProfitMultiplier { get; set; } = 1.0m;
    }

    public class TradePatternSnapshotRecord
    {
        public long Id { get; set; }
        public int UserId { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public DateTime EntryTime { get; set; }
        public DateTime? ExitTime { get; set; }
        public decimal EntryPrice { get; set; }

        public double FinalScore { get; set; }
        public double AiScore { get; set; }
        public double ElliottScore { get; set; }
        public double VolumeScore { get; set; }
        public double RsiMacdScore { get; set; }
        public double BollingerScore { get; set; }
        public double PredictedChangePct { get; set; }
        public double ScoreGap { get; set; }

        public double AtrPercent { get; set; }
        public double HtfPenalty { get; set; }
        public double Adx { get; set; }
        public double PlusDi { get; set; }
        public double MinusDi { get; set; }
        public double Rsi { get; set; }
        public double MacdHist { get; set; }
        public double BbPosition { get; set; }
        public double VolumeRatio { get; set; }

        public double? SimilarityScore { get; set; }
        public double? EuclideanSimilarity { get; set; }
        public double? CosineSimilarity { get; set; }
        public double? MatchProbability { get; set; }
        public long? MatchedPatternId { get; set; }
        public bool IsSuperEntry { get; set; }
        public decimal PositionSizeMultiplier { get; set; }
        public decimal TakeProfitMultiplier { get; set; }

        public byte? Label { get; set; }
        public decimal? PnL { get; set; }
        public decimal? PnLPercent { get; set; }

        public string ComponentMix { get; set; } = string.Empty;
        public string ContextJson { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class PatternMemoryService
    {
        private readonly DbManager _dbManager;
        private readonly Action<string>? _onLog;
        private readonly ConcurrentDictionary<string, (DateTime LoadedAt, List<TradePatternSnapshotRecord> Rows)> _cache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(20);

        public PatternMemoryService(DbManager dbManager, Action<string>? onLog = null)
        {
            _dbManager = dbManager;
            _onLog = onLog;
        }

        public async Task<PatternMatchDecision> EvaluateEntryAsync(PatternSnapshotInput input, TransformerSettings settings, CancellationToken token = default)
        {
            if (input == null || !settings.PatternMatchingEnabled)
                return PatternMatchDecision.None;

            string cacheKey = $"{input.Symbol}:{input.Side}";
            List<TradePatternSnapshotRecord> samples;
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.LoadedAt < CacheDuration)
            {
                samples = cached.Rows;
            }
            else
            {
                samples = await _dbManager.GetLabeledTradePatternSnapshotsAsync(input.Symbol, input.Side, settings.PatternLookbackDays, settings.PatternMaxSamples);
                _cache[cacheKey] = (DateTime.UtcNow, samples);
            }

            if (samples.Count < settings.PatternMinSamples)
                return new PatternMatchDecision
                {
                    HasEnoughSamples = false,
                    SampleCount = samples.Count,
                    ShouldDeferEntry = false,
                    DeferReason = string.Empty,
                    PositionSizeMultiplier = 1.0m,
                    TakeProfitMultiplier = 1.0m
                };

            var currentVector = BuildVector(input);
            var compared = new List<(TradePatternSnapshotRecord Row, double Combined, double Cosine, double Euclidean)>();
            foreach (var row in samples)
            {
                var rowVector = BuildVector(row);
                double cosine = ComputeCosineSimilarity(currentVector, rowVector);
                double euclidean = ComputeEuclideanSimilarity(currentVector, rowVector);
                double combined = 0.55 * cosine + 0.45 * euclidean;
                compared.Add((row, combined, cosine, euclidean));
            }

            var wins = compared.Where(x => x.Row.Label == 1).OrderByDescending(x => x.Combined).ToList();
            var losses = compared.Where(x => x.Row.Label == 0).OrderByDescending(x => x.Combined).ToList();
            int topK = Math.Clamp(settings.PatternTopK, 1, 20);

            var topWins = wins.Take(topK).ToList();
            var topLosses = losses.Take(topK).ToList();

            double winTopAvg = topWins.Count > 0 ? topWins.Average(x => x.Combined) : 0;
            double lossTopAvg = topLosses.Count > 0 ? topLosses.Average(x => x.Combined) : 0;
            double winRate = samples.Count > 0 ? (double)wins.Count / samples.Count : 0;
            double matchProbability = Clamp01(0.5 + (winTopAvg - lossTopAvg) * 0.85 + (winRate - 0.5) * 0.3);

            var bestWin = topWins.FirstOrDefault();
            var bestLoss = topLosses.FirstOrDefault();
            long? matchedPatternId = bestWin.Row?.Id;
            bool hasBestWin = matchedPatternId.HasValue && matchedPatternId.Value > 0;
            double topSimilarity = hasBestWin ? bestWin.Combined : 0;
            double topLossSimilarity = bestLoss.Row != null ? bestLoss.Combined : 0;

            bool shouldDeferEntry = false;
            string deferReason = string.Empty;
            if (settings.PatternLossBlockEnabled)
            {
                bool enoughLossSamples = losses.Count >= Math.Max(1, settings.PatternLossBlockMinSamples);
                double lossDominance = lossTopAvg - winTopAvg;
                if (enoughLossSamples
                    && topLossSimilarity >= settings.PatternLossBlockSimilarityThreshold
                    && lossDominance >= settings.PatternLossBlockDominanceGap
                    && matchProbability <= settings.PatternLossBlockProbabilityCeil)
                {
                    shouldDeferEntry = true;
                    deferReason = $"loss-sim:{topLossSimilarity:P1} dom:{lossDominance:P1} p:{matchProbability:P1}";
                }
            }

            bool isSuperEntry =
                hasBestWin &&
                topSimilarity >= settings.PatternSuperSimilarityThreshold &&
                matchProbability >= settings.PatternSuperProbabilityThreshold &&
                wins.Count >= Math.Max(8, settings.PatternMinSamples / 3);

            double scoreBoost = 0;
            decimal sizeMultiplier = 1.0m;
            decimal takeProfitMultiplier = 1.0m;

            if (hasBestWin && topSimilarity >= settings.PatternSimilarityThreshold && matchProbability >= 0.55)
            {
                double similarityFactor = (topSimilarity - settings.PatternSimilarityThreshold) / Math.Max(0.0001, 1.0 - settings.PatternSimilarityThreshold);
                scoreBoost = 2.0 + Math.Clamp(similarityFactor, 0, 1) * 4.0;
                takeProfitMultiplier = 1.05m;
            }

            if (shouldDeferEntry)
            {
                scoreBoost = 0;
                sizeMultiplier = 1.0m;
                takeProfitMultiplier = 1.0m;
                isSuperEntry = false;
            }

            if (isSuperEntry)
            {
                double superFactor = (topSimilarity - settings.PatternSuperSimilarityThreshold) / Math.Max(0.0001, 1.0 - settings.PatternSuperSimilarityThreshold);
                decimal maxExtra = Math.Max(0m, settings.PatternMaxPositionSizeMultiplier - 1.0m);
                sizeMultiplier = 1.0m + (decimal)Math.Clamp(superFactor, 0, 1) * maxExtra;
                scoreBoost = Math.Max(scoreBoost, 6.0 + Math.Clamp(superFactor, 0, 1) * 6.0);
                takeProfitMultiplier = 1.20m;
            }

            return new PatternMatchDecision
            {
                HasEnoughSamples = true,
                SampleCount = samples.Count,
                ProfitSamples = wins.Count,
                LossSamples = losses.Count,
                TopSimilarity = topSimilarity,
                EuclideanSimilarity = hasBestWin ? bestWin.Euclidean : 0,
                CosineSimilarity = hasBestWin ? bestWin.Cosine : 0,
                MatchProbability = matchProbability,
                MatchedPatternId = hasBestWin ? matchedPatternId : null,
                IsSuperEntry = isSuperEntry,
                GateBypass = isSuperEntry && !shouldDeferEntry,
                ScoreBoost = scoreBoost,
                TopLossSimilarity = topLossSimilarity,
                ShouldDeferEntry = shouldDeferEntry,
                DeferReason = deferReason,
                PositionSizeMultiplier = sizeMultiplier,
                TakeProfitMultiplier = takeProfitMultiplier
            };
        }

        public async Task<long?> SaveEntrySnapshotAsync(PatternSnapshotInput input, decimal entryPrice, DateTime entryTime, string strategy)
        {
            if (input == null)
                return null;

            input.EntryPriceFallback(entryPrice);

            var record = new TradePatternSnapshotRecord
            {
                Symbol = input.Symbol,
                Side = input.Side,
                Strategy = string.IsNullOrWhiteSpace(strategy) ? input.Strategy : strategy,
                Mode = input.Mode,
                EntryTime = entryTime,
                EntryPrice = entryPrice,

                FinalScore = input.FinalScore,
                AiScore = input.AiScore,
                ElliottScore = input.ElliottScore,
                VolumeScore = input.VolumeScore,
                RsiMacdScore = input.RsiMacdScore,
                BollingerScore = input.BollingerScore,
                PredictedChangePct = (double)(input.PredictedChange * 100m),
                ScoreGap = input.ScoreGap,

                AtrPercent = input.AtrPercent,
                HtfPenalty = input.HtfPenalty,
                Adx = input.Adx,
                PlusDi = input.PlusDi,
                MinusDi = input.MinusDi,
                Rsi = input.Rsi,
                MacdHist = input.MacdHist,
                BbPosition = input.BbPosition,
                VolumeRatio = input.VolumeRatio,

                SimilarityScore = input.Match.TopSimilarity,
                EuclideanSimilarity = input.Match.EuclideanSimilarity,
                CosineSimilarity = input.Match.CosineSimilarity,
                MatchProbability = input.Match.MatchProbability,
                MatchedPatternId = input.Match.MatchedPatternId,
                IsSuperEntry = input.Match.IsSuperEntry,
                PositionSizeMultiplier = input.Match.PositionSizeMultiplier,
                TakeProfitMultiplier = input.Match.TakeProfitMultiplier,
                ComponentMix = input.ComponentMix,
                ContextJson = input.ContextJson
            };

            var id = await _dbManager.SaveTradePatternSnapshotAsync(record);
            if (id.HasValue)
            {
                string superTag = input.Match.IsSuperEntry ? " SUPER" : string.Empty;
                _onLog?.Invoke($"🧠 [Pattern] {input.Symbol} {input.Side} 패턴 저장 완료{superTag} | sim={input.Match.TopSimilarity:P1}, p={input.Match.MatchProbability:P1}");
            }

            return id;
        }

        public async Task<bool> SaveOutcomeAsync(string symbol, DateTime entryTime, DateTime exitTime, decimal pnl, decimal pnlPercent, string? exitReason = null)
        {
            bool updated = await _dbManager.CompleteTradePatternSnapshotAsync(symbol, entryTime, exitTime, pnl, pnlPercent, exitReason);
            if (updated)
            {
                string label = pnl > 0 ? "WIN" : "LOSS";
                _onLog?.Invoke($"🧠 [Pattern] {symbol} 라벨 업데이트 완료: {label} ({pnlPercent:F2}%)");
                InvalidateSymbolCache(symbol);
            }
            return updated;
        }

        private void InvalidateSymbolCache(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            foreach (string key in _cache.Keys)
            {
                if (key.StartsWith(symbol + ":", StringComparison.OrdinalIgnoreCase))
                {
                    _cache.TryRemove(key, out _);
                }
            }
        }

        private static double[] BuildVector(PatternSnapshotInput input)
        {
            return BuildVector(
                input.FinalScore,
                input.AiScore,
                input.ElliottScore,
                input.VolumeScore,
                input.RsiMacdScore,
                input.BollingerScore,
                (double)(input.PredictedChange * 100m),
                input.ScoreGap,
                input.AtrPercent,
                input.HtfPenalty,
                input.Adx,
                input.Rsi,
                input.MacdHist,
                input.BbPosition,
                input.VolumeRatio);
        }

        private static double[] BuildVector(TradePatternSnapshotRecord row)
        {
            return BuildVector(
                row.FinalScore,
                row.AiScore,
                row.ElliottScore,
                row.VolumeScore,
                row.RsiMacdScore,
                row.BollingerScore,
                row.PredictedChangePct,
                row.ScoreGap,
                row.AtrPercent,
                row.HtfPenalty,
                row.Adx,
                row.Rsi,
                row.MacdHist,
                row.BbPosition,
                row.VolumeRatio);
        }

        private static double[] BuildVector(
            double finalScore,
            double aiScore,
            double elliottScore,
            double volumeScore,
            double rsiMacdScore,
            double bollingerScore,
            double predictedChangePct,
            double scoreGap,
            double atrPercent,
            double htfPenalty,
            double adx,
            double rsi,
            double macdHist,
            double bbPosition,
            double volumeRatio)
        {
            return new[]
            {
                Clamp01(finalScore / 100.0),
                Clamp01(aiScore / 40.0),
                Clamp01(elliottScore / 25.0),
                Clamp01(volumeScore / 15.0),
                Clamp01(rsiMacdScore / 10.0),
                Clamp01(bollingerScore / 10.0),
                Clamp01((predictedChangePct + 5.0) / 10.0),
                Clamp01((scoreGap + 30.0) / 60.0),
                Clamp01(atrPercent / 2.0),
                Clamp01((htfPenalty + 30.0) / 30.0),
                Clamp01(adx / 50.0),
                Clamp01(rsi / 100.0),
                Clamp01((macdHist + 2.0) / 4.0),
                Clamp01(bbPosition),
                Clamp01(volumeRatio / 3.0)
            };
        }

        private static double ComputeEuclideanSimilarity(double[] current, double[] sample)
        {
            double sumSq = 0;
            for (int i = 0; i < current.Length; i++)
            {
                double d = current[i] - sample[i];
                sumSq += d * d;
            }

            double distance = Math.Sqrt(sumSq);
            return 1.0 / (1.0 + distance);
        }

        private static double ComputeCosineSimilarity(double[] current, double[] sample)
        {
            double dot = 0;
            double normA = 0;
            double normB = 0;
            for (int i = 0; i < current.Length; i++)
            {
                dot += current[i] * sample[i];
                normA += current[i] * current[i];
                normB += sample[i] * sample[i];
            }

            if (normA == 0 || normB == 0)
                return 0;

            double cosine = dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
            return Clamp01((cosine + 1.0) / 2.0);
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }
    }

    internal static class PatternSnapshotInputExtensions
    {
        public static void EntryPriceFallback(this PatternSnapshotInput input, decimal entryPrice)
        {
            if (input.CurrentPrice <= 0)
                input.CurrentPrice = entryPrice;
        }
    }
}
