using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Services.LorentzianV2
{
    /// <summary>
    /// [v5.20.0] Pine 의 Approximate Nearest Neighbors (ANN) 알고리즘 정통 포팅
    ///
    /// 핵심 차이점 (vs 표준 KNN):
    ///   1. 시간 간격 보장 (i%4) — 4봉 이상 떨어진 이웃만 선정 (overfitting 회피)
    ///   2. 슬라이딩 lastDistance — 새 이웃은 기존 거리 ≥ 일 때만 추가 (단조성)
    ///   3. K 초과 시 lastDistance 를 하위 25% 분위수로 갱신 → 정확도 부스트
    ///   4. 점수 = Σ votes (-K ~ +K), 부호로 신호 판단 (양수=long, 음수=short)
    ///
    /// per-symbol 별도 인스턴스 → 각 코인 고유 패턴 학습 (API3USDT 72% 같은 특화 효과)
    /// </summary>
    public sealed class LorentzianAnnEngine
    {
        private readonly object _lock = new();
        private readonly int _neighborsCount;
        private readonly int _maxBarsBack;
        private readonly int _featureCount;

        // 시간 ASC 순 — index = 봉 시퀀스
        private readonly List<float[]> _featureHistory = new();
        private readonly List<int> _labelHistory = new();   // -1=short, 0=neutral, +1=long (4봉 후 가격 비교)

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

        /// <summary>샘플 추가 — feature 와 4봉 후 결과 라벨 (Pine: src[4] vs src[0])</summary>
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

        /// <summary>
        /// Pine ANN 알고리즘 정통 포팅
        ///   prediction = Σ votes (범위: -K ~ +K)
        ///   양수 = LONG 신호, 음수 = SHORT 신호, 0 = 중립
        /// </summary>
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
                if (i % 4 != 0) continue;  // Pine: 4봉 간격 보장
                double d = LorentzianDistance(queryFeatures, feats[i]);
                if (d < lastDistance) continue;  // 거리 ≥ lastDistance 만 후보
                lastDistance = d;
                distances.Add(d);
                predictions.Add(labels[i]);
                if (predictions.Count > _neighborsCount)
                {
                    // K 초과 → 하위 25% 분위수로 lastDistance 갱신 (Pine 의 정확도 부스트)
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
        public int Prediction { get; set; }       // -K ~ +K (Pine 의 prediction)
        public int PositiveVotes { get; set; }
        public int NegativeVotes { get; set; }
        public int SampleCount { get; set; }
        public string Signal => Prediction > 0 ? "LONG" : Prediction < 0 ? "SHORT" : "NEUTRAL";
        public float Confidence => K > 0 ? (float)Math.Abs(Prediction) / K : 0f;  // 0~1
    }
}
