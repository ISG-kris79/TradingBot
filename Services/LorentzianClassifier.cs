using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    /// <summary>
    /// [Lorentzian Phase 1] KNN 분류기 — Lorentzian 거리 기반 사이드카 검증.
    /// 기존 ML.NET 진입 신호에 대해 "과거 비슷한 패턴 K개의 결과"를 추가 검증.
    /// Lorentzian distance: d(x,y) = Σ ln(1 + |xᵢ - yᵢ|) — 이상치/꼬리분포에 강건.
    /// </summary>
    public sealed class LorentzianClassifier
    {
        private readonly object _lock = new();
        private readonly List<LorentzianSample> _samples = new();
        private readonly int _k;
        private readonly int _maxSamples;
        private readonly int _minSamplesForPrediction;

        private float[] _featureMeans = Array.Empty<float>();
        private float[] _featureStds = Array.Empty<float>();
        private bool _normalizationReady;
        private DateTime _lastNormalizationTime = DateTime.MinValue;

        public int SampleCount { get { lock (_lock) return _samples.Count; } }
        public bool IsReady => SampleCount >= _minSamplesForPrediction;
        public int K => _k;

        public LorentzianClassifier(int k = 10, int maxSamples = 5000, int minSamplesForPrediction = 100)
        {
            if (k < 1) k = 1;
            if (maxSamples < k * 5) maxSamples = k * 5;
            _k = k;
            _maxSamples = maxSamples;
            _minSamplesForPrediction = Math.Max(minSamplesForPrediction, k * 3);
        }

        public void AddSample(MultiTimeframeEntryFeature feature, bool wasSuccessful, string symbol)
        {
            if (feature == null) return;
            float[] vec = LorentzianFeatureMapperPublic.Extract(feature);
            var sample = new LorentzianSample
            {
                Features = vec,
                WasSuccessful = wasSuccessful,
                Symbol = symbol ?? string.Empty,
                TimestampUtc = DateTime.UtcNow
            };

            lock (_lock)
            {
                _samples.Add(sample);
                if (_samples.Count > _maxSamples)
                {
                    int removeCount = _samples.Count - _maxSamples;
                    _samples.RemoveRange(0, removeCount);
                }
                _normalizationReady = false;
            }
        }

        public LorentzianPrediction Predict(MultiTimeframeEntryFeature feature)
        {
            if (feature == null)
                return new LorentzianPrediction { IsReady = false };

            LorentzianSample[] snapshot;
            int n;
            lock (_lock)
            {
                n = _samples.Count;
                if (n < _minSamplesForPrediction)
                    return new LorentzianPrediction { IsReady = false, K = _k };

                if (!_normalizationReady || (DateTime.UtcNow - _lastNormalizationTime).TotalMinutes > 30)
                {
                    RecalculateNormalizationLocked();
                }

                snapshot = _samples.ToArray();
            }

            float[] queryRaw = LorentzianFeatureMapperPublic.Extract(feature);
            float[] queryNorm = NormalizeVector(queryRaw);

            // Top-K 최근접 이웃 찾기 — heap 대신 단순 배열 정렬 (K=10 정도면 부담 적음)
            var distances = new (double dist, bool success)[snapshot.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                float[] sampleNorm = NormalizeVector(snapshot[i].Features);
                distances[i] = (LorentzianDistance(queryNorm, sampleNorm), snapshot[i].WasSuccessful);
            }

            Array.Sort(distances, (a, b) => a.dist.CompareTo(b.dist));

            int positive = 0, negative = 0;
            int kEffective = Math.Min(_k, distances.Length);
            for (int i = 0; i < kEffective; i++)
            {
                if (distances[i].success) positive++;
                else negative++;
            }

            return new LorentzianPrediction
            {
                IsReady = true,
                K = kEffective,
                PositiveVotes = positive,
                NegativeVotes = negative,
                SampleCount = snapshot.Length
            };
        }

        public async Task LoadSamplesFromFolderAsync(string folder, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            var files = Directory.GetFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly);
            int loaded = 0;
            foreach (var path in files)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    using var reader = new StreamReader(path);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (token.IsCancellationRequested) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var sample = JsonSerializer.Deserialize<LorentzianSample>(line);
                            if (sample?.Features is { Length: > 0 })
                            {
                                lock (_lock) _samples.Add(sample);
                                loaded++;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            lock (_lock)
            {
                if (_samples.Count > _maxSamples)
                {
                    int removeCount = _samples.Count - _maxSamples;
                    _samples.RemoveRange(0, removeCount);
                }
                _normalizationReady = false;
            }
        }

        public async Task AppendSampleToFileAsync(string folder, LorentzianSample sample)
        {
            if (string.IsNullOrWhiteSpace(folder) || sample?.Features == null) return;
            try
            {
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"lorentzian_{DateTime.UtcNow:yyyyMM}.jsonl");
                string json = JsonSerializer.Serialize(sample);
                await File.AppendAllTextAsync(file, json + Environment.NewLine);
            }
            catch { }
        }

        private float[] NormalizeVector(float[] raw)
        {
            int dim = raw.Length;
            var result = new float[dim];
            if (!_normalizationReady || _featureMeans.Length != dim)
            {
                Array.Copy(raw, result, dim);
                return result;
            }
            for (int i = 0; i < dim; i++)
            {
                float std = _featureStds[i] > 1e-6f ? _featureStds[i] : 1f;
                result[i] = (raw[i] - _featureMeans[i]) / std;
            }
            return result;
        }

        private void RecalculateNormalizationLocked()
        {
            if (_samples.Count == 0) return;
            int dim = _samples[0].Features.Length;
            var sums = new double[dim];
            var sumSquares = new double[dim];

            foreach (var s in _samples)
            {
                if (s.Features.Length != dim) continue;
                for (int i = 0; i < dim; i++)
                {
                    sums[i] += s.Features[i];
                    sumSquares[i] += (double)s.Features[i] * s.Features[i];
                }
            }

            int n = _samples.Count;
            _featureMeans = new float[dim];
            _featureStds = new float[dim];
            for (int i = 0; i < dim; i++)
            {
                _featureMeans[i] = (float)(sums[i] / n);
                double variance = (sumSquares[i] / n) - (double)_featureMeans[i] * _featureMeans[i];
                _featureStds[i] = (float)Math.Sqrt(Math.Max(variance, 1e-9));
            }
            _normalizationReady = true;
            _lastNormalizationTime = DateTime.UtcNow;
        }

        private static double LorentzianDistance(float[] a, float[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            double sum = 0;
            for (int i = 0; i < len; i++)
            {
                sum += Math.Log(1.0 + Math.Abs(a[i] - b[i]));
            }
            return sum;
        }
    }

    public sealed class LorentzianSample
    {
        public float[] Features { get; set; } = Array.Empty<float>();
        public bool WasSuccessful { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string Symbol { get; set; } = string.Empty;
    }

    public sealed class LorentzianPrediction
    {
        public bool IsReady { get; set; }
        public int K { get; set; }
        public int PositiveVotes { get; set; }
        public int NegativeVotes { get; set; }
        public int SampleCount { get; set; }
        public float PassRate => K > 0 ? (float)PositiveVotes / K : 0f;
        public int Score => PositiveVotes - NegativeVotes;
    }

    /// <summary>
    /// MultiTimeframeEntryFeature → 17차원 KNN 입력 벡터.
    /// 다양한 스케일이지만 Z-score 정규화로 흡수.
    /// </summary>
    public static class LorentzianFeatureMapperPublic
    {
        public const int Dim = 17;

        public static float[] Extract(MultiTimeframeEntryFeature f)
        {
            return new float[Dim]
            {
                f.M15_RSI,
                f.M15_BBPosition,
                f.M15_ADX,
                f.M15_ATR,
                f.M15_StochRSI_K,
                f.M15_Volume_Ratio,
                f.H1_RSI,
                f.H1_BBPosition,
                f.H1_MomentumStrength,
                f.H4_RSI,
                f.H4_BBPosition,
                f.H4_Trend,
                f.D1_Trend,
                f.D1_RSI,
                f.DirectionBias,
                f.M15_PriceVsSMA20,
                f.M15_EMA_CrossState
            };
        }
    }
}
