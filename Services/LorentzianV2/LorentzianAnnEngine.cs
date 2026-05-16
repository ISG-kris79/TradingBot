using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Services.LorentzianV2
{
    // Pine Script Approximate Nearest Neighbors (jdehorty Lorentzian Classification) 정통 포팅
    //   - 4봉 간격 보장으로 overfitting 회피
    //   - 슬라이딩 lastDistance 단조 증가
    //   - K 초과 시 하위 25% 분위수로 lastDistance 갱신
    //   - 점수 = Σ votes (-K ~ +K), 부호로 신호 판단
    public sealed class LorentzianAnnEngine
    {
        private readonly object _lock = new();
        private readonly int _neighborsCount;
        private readonly int _maxBarsBack;
        private readonly int _featureCount;

        private readonly List<float[]> _featureHistory = new();
        private readonly List<int> _labelHistory = new();   // -1=down, 0=neutral, +1=up (4봉 후 close 비교)

        public string Symbol { get; }
        public int SampleCount { get { lock (_lock) return _featureHistory.Count; } }
        public bool IsReady => SampleCount >= 200;

        public LorentzianAnnEngine(string symbol, int neighborsCount = 8, int maxBarsBack = 2000, int featureCount = 7)
        {
            Symbol = symbol;
            _neighborsCount = Math.Max(2, neighborsCount);
            _maxBarsBack = Math.Max(neighborsCount * 10, maxBarsBack);
            _featureCount = featureCount;
        }

        public void AddSample(float[] features, int label)
        {
            if (features == null || features.Length != _featureCount) return;
            lock (_lock)
            {
                _featureHistory.Add(features);
                _labelHistory.Add(label);
                if (_featureHistory.Count > _maxBarsBack)
                {
                    int remove = _featureHistory.Count - _maxBarsBack;
                    _featureHistory.RemoveRange(0, remove);
                    _labelHistory.RemoveRange(0, remove);
                }
            }
        }

        public LorentzianAnnPrediction Predict(float[] queryFeatures)
        {
            if (queryFeatures == null || queryFeatures.Length != _featureCount)
                return new LorentzianAnnPrediction { Symbol = Symbol, IsReady = false, K = _neighborsCount };

            float[][] feats; int[] labels;
            lock (_lock)
            {
                if (_featureHistory.Count < 200)
                    return new LorentzianAnnPrediction { Symbol = Symbol, IsReady = false, K = _neighborsCount, SampleCount = _featureHistory.Count };
                feats = _featureHistory.ToArray();
                labels = _labelHistory.ToArray();
            }

            int sizeLoop = Math.Min(_maxBarsBack - 1, feats.Length - 1);
            double lastDistance = -1.0;
            var distances = new List<double>(_neighborsCount + 1);
            var predictions = new List<int>(_neighborsCount + 1);

            for (int i = 0; i <= sizeLoop; i++)
            {
                // [v5.23.59 fix] jdehorty 원본: `if d >= lastDistance and i%4`
                //   Pine 에서 i%4 는 i%4!=0 일 때 truthy → i%4==0 봉만 SKIP.
                //   (이전 C#: `if(i%4!=0) continue` = i%4==0 만 처리 — 정확히 반대, 후보이웃 반대표본 + 1/4 희소)
                if (i % 4 == 0) continue;
                double d = LorentzianDistance(queryFeatures, feats[i]);
                if (d < lastDistance) continue;
                lastDistance = d;
                distances.Add(d);
                predictions.Add(labels[i]);
                if (predictions.Count > _neighborsCount)
                {
                    int q = (int)Math.Round(_neighborsCount * 3.0 / 4.0);
                    lastDistance = distances[Math.Min(q, distances.Count - 1)];
                    distances.RemoveAt(0);
                    predictions.RemoveAt(0);
                }
            }

            int prediction = predictions.Sum();
            int positive = predictions.Count(v => v > 0);
            int negative = predictions.Count(v => v < 0);

            return new LorentzianAnnPrediction
            {
                Symbol = Symbol,
                IsReady = true,
                K = predictions.Count,
                Prediction = prediction,
                PositiveVotes = positive,
                NegativeVotes = negative,
                SampleCount = feats.Length,
            };
        }

        private static double LorentzianDistance(float[] a, float[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            double sum = 0;
            for (int i = 0; i < len; i++) sum += Math.Log(1.0 + Math.Abs(a[i] - b[i]));
            return sum;
        }
    }

    public sealed class LorentzianAnnPrediction
    {
        public string Symbol { get; set; } = "";
        public bool IsReady { get; set; }
        public int K { get; set; }
        public int Prediction { get; set; }
        public int PositiveVotes { get; set; }
        public int NegativeVotes { get; set; }
        public int SampleCount { get; set; }
        public string Signal => Prediction > 0 ? "LONG" : Prediction < 0 ? "SHORT" : "NEUTRAL";
        public float PositiveRate => K > 0 ? (float)PositiveVotes / K : 0f;
    }
}
