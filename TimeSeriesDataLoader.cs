using System;
using System.Collections.Generic;
using System.Linq;
using TorchSharp;
using TradingBot.Models;
using static TorchSharp.torch;

namespace TradingBot.Services.AI
{
    /// <summary>
    /// 시계열 데이터 전처리 및 배치 로더 (TorchSharp 최적화)
    /// 기능: Feature 추출, 정규화(Z-Score), 슬라이딩 윈도우, 배치 생성, GPU 전송
    /// </summary>
    public class TimeSeriesDataLoader : IDisposable
    {
        private readonly int _seqLen;
        private readonly int _inputDim;
        private readonly int _batchSize;
        private readonly bool _shuffle;
        private readonly bool _useCache;
        private readonly Device _device;

        private float[,]? _rawFeatures;
        private float[]? _rawTargets;
        private int _sampleCount;

        public float[]? Means { get; set; }
        public float[]? Stds { get; set; }
        public int TotalBatches => _sampleCount > 0 ? (int)Math.Ceiling((double)_sampleCount / _batchSize) : 0;

        private List<(Tensor, Tensor)>? _cachedBatches;

        public TimeSeriesDataLoader(int sequenceLength, int inputDim, int batchSize, bool shuffle = true, bool useCache = false, Device? device = null)
        {
            _seqLen = sequenceLength;
            _inputDim = inputDim;
            _batchSize = batchSize;
            _shuffle = shuffle;
            _useCache = useCache;
            _device = device ?? CPU;
        }

        public void LoadData(List<CandleData> data)
        {
            if (data == null || data.Count <= _seqLen)
                throw new ArgumentException("데이터가 부족합니다.");

            int count = data.Count;
            _rawFeatures = new float[count, _inputDim];
            _rawTargets = new float[count];

            // 1. Feature Extraction (병렬 처리 가능하지만 순차 처리로 안정성 확보)
            for (int i = 0; i < count; i++)
            {
                var c = data[i];
                // Feature Mapping (17 features)
                _rawFeatures[i, 0] = (float)c.Open;
                _rawFeatures[i, 1] = (float)c.High;
                _rawFeatures[i, 2] = (float)c.Low;
                _rawFeatures[i, 3] = (float)c.Close;
                _rawFeatures[i, 4] = (float)c.Volume;
                _rawFeatures[i, 5] = c.RSI;
                _rawFeatures[i, 6] = c.BollingerUpper;
                _rawFeatures[i, 7] = c.BollingerLower;
                _rawFeatures[i, 8] = c.MACD;
                _rawFeatures[i, 9] = c.MACD_Signal;
                _rawFeatures[i, 10] = c.MACD_Hist;
                _rawFeatures[i, 11] = c.ATR;
                _rawFeatures[i, 12] = c.Fib_236;
                _rawFeatures[i, 13] = c.Fib_382;
                _rawFeatures[i, 14] = c.Fib_500;
                _rawFeatures[i, 15] = c.Fib_618;
                _rawFeatures[i, 16] = c.SentimentScore;

                // Target: Close Price
                _rawTargets[i] = (float)c.Close;
            }

            // 2. Normalization (Z-Score)
            if (Means == null || Stds == null)
            {
                CalculateStats(count);
            }
            ApplyNormalization(count);

            // 3. Prepare Samples count
            _sampleCount = count - _seqLen;

            // 캐시 초기화
            if (_useCache) _cachedBatches = new List<(Tensor, Tensor)>();
        }

        private void CalculateStats(int count)
        {
            if (_rawFeatures == null)
                throw new InvalidOperationException("데이터가 로드되지 않았습니다.");

            Means = new float[_inputDim];
            Stds = new float[_inputDim];

            for (int j = 0; j < _inputDim; j++)
            {
                float sum = 0;
                for (int i = 0; i < count; i++) sum += _rawFeatures[i, j];
                Means[j] = sum / count;

                float sumSq = 0;
                for (int i = 0; i < count; i++)
                {
                    float diff = _rawFeatures[i, j] - Means[j];
                    sumSq += diff * diff;
                }
                Stds[j] = (float)Math.Sqrt(sumSq / count);
                if (Stds[j] == 0) Stds[j] = 1;
            }
        }

        private void ApplyNormalization(int count)
        {
            if (_rawFeatures == null || _rawTargets == null || Means == null || Stds == null)
                throw new InvalidOperationException("정규화에 필요한 데이터가 준비되지 않았습니다.");

            // [FIX] 배열 인덱스 범위 체크 추가
            if (Means == null || Stds == null || Means.Length < 4 || Stds.Length < 4)
            {
                throw new InvalidOperationException($"정규화 파라미터 배열 크기 부족 (means: {Means?.Length ?? 0}, stds: {Stds?.Length ?? 0}, 필요: 4)");
            }
            
            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < _inputDim && j < Means.Length && j < Stds.Length; j++)
                {
                    if (Math.Abs(Stds[j]) > 1e-8f)
                    {
                        _rawFeatures[i, j] = (_rawFeatures[i, j] - Means[j]) / Stds[j];
                    }
                }
                // Target 정규화 (Close Price index = 3)
                if (Math.Abs(Stds[3]) > 1e-8f)
                {
                    _rawTargets[i] = (_rawTargets[i] - Means[3]) / Stds[3];
                }
            }
        }

        public IEnumerable<(Tensor, Tensor)> GetBatches()
        {
            if (_rawFeatures == null || _rawTargets == null)
                yield break;

            if (_useCache && _cachedBatches != null && _cachedBatches.Count > 0)
            {
                foreach (var batch in _cachedBatches) yield return batch;
                yield break;
            }

            var indices = Enumerable.Range(0, _sampleCount).ToArray();
            if (_shuffle)
            {
                var rng = new Random();
                indices = indices.OrderBy(x => rng.Next()).ToArray();
            }

            for (int i = 0; i < _sampleCount; i += _batchSize)
            {
                int size = Math.Min(_batchSize, _sampleCount - i);
                var batchIndices = indices.Skip(i).Take(size).ToArray();

                // 배치 텐서 생성
                float[] xBatchFlat = new float[size * _seqLen * _inputDim];
                float[] yBatchFlat = new float[size]; // OutputDim = 1 가정

                for (int b = 0; b < size; b++)
                {
                    int startIdx = batchIndices[b];
                    // Input Sequence
                    for (int t = 0; t < _seqLen; t++)
                    {
                        for (int f = 0; f < _inputDim; f++)
                        {
                            xBatchFlat[(b * _seqLen + t) * _inputDim + f] = _rawFeatures[startIdx + t, f];
                        }
                    }
                    // Target (Next Step)
                    yBatchFlat[b] = _rawTargets[startIdx + _seqLen];
                }

                var xTensor = torch.tensor(xBatchFlat, new long[] { size, _seqLen, _inputDim }, device: _device);
                var yTensor = torch.tensor(yBatchFlat, new long[] { size, 1 }, device: _device);

                if (_useCache) _cachedBatches.Add((xTensor, yTensor));

                yield return (xTensor, yTensor);
            }
        }

        public Tensor CreateInferenceTensor(List<CandleData> data)
        {
            // 추론용 단일 배치 생성
            if (Means == null || Stds == null)
                throw new InvalidOperationException("정규화 파라미터가 없습니다. 먼저 LoadData를 호출하세요.");

            float[] xFlat = new float[_seqLen * _inputDim];

            for (int t = 0; t < _seqLen; t++)
            {
                var c = data[t];
                float[] feats = { (float)c.Open, (float)c.High, (float)c.Low, (float)c.Close, (float)c.Volume, c.RSI, c.BollingerUpper, c.BollingerLower, c.MACD, c.MACD_Signal, c.MACD_Hist, c.ATR, c.Fib_236, c.Fib_382, c.Fib_500, c.Fib_618, c.SentimentScore };

                for (int f = 0; f < _inputDim; f++)
                {
                    xFlat[t * _inputDim + f] = (feats[f] - Means[f]) / Stds[f];
                }
            }

            return torch.tensor(xFlat, new long[] { 1, _seqLen, _inputDim }, device: _device);
        }

        public float DenormalizeTarget(float value)
        {
            // [FIX] 배열 인덱스 범위 체크 추가
            if (Means == null || Stds == null)
                throw new InvalidOperationException("정규화 파라미터가 없습니다.");
            
            if (Means.Length < 4 || Stds.Length < 4)
                throw new InvalidOperationException($"정규화 파라미터 배열 크기 부족 (means: {Means.Length}, stds: {Stds.Length}, 필요: 4)");
            
            if (Math.Abs(Stds[3]) < 1e-8f)
                return value; // 표준편차가 0이면 원본 반환
            
            return (value * Stds[3]) + Means[3];
        }

        public void Dispose()
        {
            if (_cachedBatches != null)
            {
                foreach (var (x, y) in _cachedBatches) { x.Dispose(); y.Dispose(); }
                _cachedBatches.Clear();
            }
        }
    }
}
