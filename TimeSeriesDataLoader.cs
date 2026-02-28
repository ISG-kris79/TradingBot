using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TorchSharp;
using TradingBot.Models;
using static TorchSharp.torch;

namespace TradingBot.Services.AI
{
    /// <summary>
    /// Transformer 모델을 위한 최적화된 시계열 데이터 로더
    /// - 메모리 효율적인 배치 생성
    /// - 슬라이딩 윈도우 최적화
    /// - 병렬 처리 및 캐싱
    /// </summary>
    public class TimeSeriesDataLoader
    {
        private readonly int _sequenceLength;
        private readonly int _inputDim;
        private readonly int _batchSize;
        private readonly bool _shuffle;
        private readonly bool _useCache;
        private readonly Device _device;

        // 정규화 파라미터
        public float[] Means { get; private set; }
        public float[] Stds { get; private set; }

        // 데이터 캐시
        private ConcurrentDictionary<int, (Tensor X, Tensor Y)> _cache;
        private List<CandleData> _rawData;
        private float[,] _normalizedFeatures;
        private float[] _normalizedTargets;

        public int TotalSamples { get; private set; }
        public int TotalBatches { get; private set; }

        public TimeSeriesDataLoader(
            int sequenceLength = 60,
            int inputDim = 17,
            int batchSize = 32,
            bool shuffle = true,
            bool useCache = true,
            Device device = null)
        {
            _sequenceLength = sequenceLength;
            _inputDim = inputDim;
            _batchSize = batchSize;
            _shuffle = shuffle;
            _useCache = useCache;
            _device = device ?? (torch.cuda.is_available() ? torch.CUDA : torch.CPU);

            if (_useCache)
                _cache = new ConcurrentDictionary<int, (Tensor, Tensor)>();
        }

        /// <summary>
        /// 원시 데이터를 로드하고 전처리 수행
        /// </summary>
        public void LoadData(List<CandleData> data)
        {
            if (data == null || data.Count <= _sequenceLength)
            {
                throw new ArgumentException($"데이터가 부족합니다. 최소 {_sequenceLength + 1}개 필요, 현재: {data?.Count ?? 0}개");
            }

            _rawData = data;
            TotalSamples = data.Count - _sequenceLength;
            TotalBatches = (int)Math.Ceiling((double)TotalSamples / _batchSize);

            Console.WriteLine($"[DataLoader] 데이터 로드 완료: {data.Count}개 캔들, {TotalSamples}개 샘플, {TotalBatches}개 배치");

            // 전처리 수행 (병렬)
            PreprocessData();
        }

        /// <summary>
        /// 데이터 전처리: Feature 추출 및 정규화
        /// </summary>
        private void PreprocessData()
        {
            int count = _rawData.Count;
            _normalizedFeatures = new float[count, _inputDim];
            _normalizedTargets = new float[count];

            // 1단계: Feature 추출 (병렬 처리)
            Parallel.For(0, count, i =>
            {
                var c = _rawData[i];
                ExtractFeatures(c, i);
                _normalizedTargets[i] = (float)c.Close; // Target: 다음 Close
            });

            // 2단계: 정규화 파라미터 계산
            ComputeNormalizationStats();

            // 3단계: 정규화 적용 (병렬)
            Parallel.For(0, count, i =>
            {
                for (int j = 0; j < _inputDim; j++)
                {
                    _normalizedFeatures[i, j] = (_normalizedFeatures[i, j] - Means[j]) / Stds[j];
                }
            });

            // Target 정규화 (Close Price 기준, feature 인덱스 3)
            float targetMean = Means[3];
            float targetStd = Stds[3];
            for (int i = 0; i < count; i++)
            {
                _normalizedTargets[i] = (_normalizedTargets[i] - targetMean) / targetStd;
            }

            Console.WriteLine($"[DataLoader] 전처리 완료 (Mean: {Means[3]:F2}, Std: {Stds[3]:F2})");
        }

        /// <summary>
        /// CandleData에서 Feature 추출
        /// </summary>
        private void ExtractFeatures(CandleData c, int index)
        {
            _normalizedFeatures[index, 0] = (float)c.Open;
            _normalizedFeatures[index, 1] = (float)c.High;
            _normalizedFeatures[index, 2] = (float)c.Low;
            _normalizedFeatures[index, 3] = (float)c.Close;
            _normalizedFeatures[index, 4] = c.Volume;
            _normalizedFeatures[index, 5] = c.RSI;
            _normalizedFeatures[index, 6] = c.BollingerUpper;
            _normalizedFeatures[index, 7] = c.BollingerLower;
            _normalizedFeatures[index, 8] = c.MACD;
            _normalizedFeatures[index, 9] = c.MACD_Signal;
            _normalizedFeatures[index, 10] = c.MACD_Hist;
            _normalizedFeatures[index, 11] = c.ATR;
            _normalizedFeatures[index, 12] = c.Fib_236;
            _normalizedFeatures[index, 13] = c.Fib_382;
            _normalizedFeatures[index, 14] = c.Fib_500;
            _normalizedFeatures[index, 15] = c.Fib_618;
            _normalizedFeatures[index, 16] = c.SentimentScore;
        }

        /// <summary>
        /// 정규화 통계 계산 (Mean, Std)
        /// </summary>
        private void ComputeNormalizationStats()
        {
            Means = new float[_inputDim];
            Stds = new float[_inputDim];

            int count = _normalizedFeatures.GetLength(0);

            for (int j = 0; j < _inputDim; j++)
            {
                // Mean 계산
                double sum = 0;
                for (int i = 0; i < count; i++)
                    sum += _normalizedFeatures[i, j];
                Means[j] = (float)(sum / count);

                // Std 계산
                double sumSq = 0;
                for (int i = 0; i < count; i++)
                {
                    double diff = _normalizedFeatures[i, j] - Means[j];
                    sumSq += diff * diff;
                }
                Stds[j] = (float)Math.Sqrt(sumSq / count);

                // Zero division 방지
                if (Stds[j] < 1e-6f)
                    Stds[j] = 1f;
            }
        }

        /// <summary>
        /// 배치 생성 (메모리 효율적)
        /// </summary>
        public IEnumerable<(Tensor X, Tensor Y)> GetBatches()
        {
            // 셔플 인덱스 생성
            int[] indices = _shuffle
                ? Enumerable.Range(0, TotalSamples).OrderBy(_ => Guid.NewGuid()).ToArray()
                : Enumerable.Range(0, TotalSamples).ToArray();

            for (int batchIdx = 0; batchIdx < TotalBatches; batchIdx++)
            {
                int start = batchIdx * _batchSize;
                int end = Math.Min(start + _batchSize, TotalSamples);
                int currentBatchSize = end - start;

                // 캐시 확인
                if (_useCache && _cache.TryGetValue(batchIdx, out var cached))
                {
                    yield return cached;
                    continue;
                }

                // 배치 생성
                float[] xFlat = new float[currentBatchSize * _sequenceLength * _inputDim];
                float[] yFlat = new float[currentBatchSize];

                Parallel.For(0, currentBatchSize, i =>
                {
                    int sampleIdx = indices[start + i];
                    CreateSequenceSample(sampleIdx, i, xFlat, yFlat);
                });

                var xTensor = torch.tensor(xFlat, new long[] { currentBatchSize, _sequenceLength, _inputDim }, device: _device);
                var yTensor = torch.tensor(yFlat, new long[] { currentBatchSize, 1 }, device: _device);

                // 캐시 저장
                if (_useCache)
                    _cache[batchIdx] = (xTensor, yTensor);

                yield return (xTensor, yTensor);
            }
        }

        /// <summary>
        /// 슬라이딩 윈도우로 시퀀스 샘플 생성
        /// </summary>
        private void CreateSequenceSample(int sampleIdx, int batchOffset, float[] xFlat, float[] yFlat)
        {
            // X: [sampleIdx ... sampleIdx + seqLen - 1] 시퀀스
            for (int t = 0; t < _sequenceLength; t++)
            {
                int srcIdx = sampleIdx + t;
                for (int f = 0; f < _inputDim; f++)
                {
                    int dstIdx = (batchOffset * _sequenceLength + t) * _inputDim + f;
                    xFlat[dstIdx] = _normalizedFeatures[srcIdx, f];
                }
            }

            // Y: sampleIdx + seqLen 시점의 Target
            yFlat[batchOffset] = _normalizedTargets[sampleIdx + _sequenceLength];
        }

        /// <summary>
        /// 단일 시퀀스를 Tensor로 변환 (추론용)
        /// </summary>
        public Tensor CreateInferenceTensor(List<CandleData> sequence)
        {
            if (sequence.Count != _sequenceLength)
                throw new ArgumentException($"시퀀스 길이가 맞지 않습니다. 필요: {_sequenceLength}, 현재: {sequence.Count}");

            float[] features = new float[_sequenceLength * _inputDim];

            for (int t = 0; t < _sequenceLength; t++)
            {
                var c = sequence[t];
                float[] rawFeatures = new float[_inputDim];

                // Feature 추출
                rawFeatures[0] = (float)c.Open;
                rawFeatures[1] = (float)c.High;
                rawFeatures[2] = (float)c.Low;
                rawFeatures[3] = (float)c.Close;
                rawFeatures[4] = c.Volume;
                rawFeatures[5] = c.RSI;
                rawFeatures[6] = c.BollingerUpper;
                rawFeatures[7] = c.BollingerLower;
                rawFeatures[8] = c.MACD;
                rawFeatures[9] = c.MACD_Signal;
                rawFeatures[10] = c.MACD_Hist;
                rawFeatures[11] = c.ATR;
                rawFeatures[12] = c.Fib_236;
                rawFeatures[13] = c.Fib_382;
                rawFeatures[14] = c.Fib_500;
                rawFeatures[15] = c.Fib_618;
                rawFeatures[16] = c.SentimentScore;

                // 정규화 적용
                for (int f = 0; f < _inputDim; f++)
                {
                    features[t * _inputDim + f] = (rawFeatures[f] - Means[f]) / Stds[f];
                }
            }

            return torch.tensor(features, new long[] { 1, _sequenceLength, _inputDim }, device: _device);
        }

        /// <summary>
        /// 정규화된 출력을 원래 스케일로 복원
        /// </summary>
        public float DenormalizeTarget(float normalizedValue)
        {
            // Close Price 기준 (feature index 3)
            return normalizedValue * Stds[3] + Means[3];
        }

        /// <summary>
        /// 캐시 초기화
        /// </summary>
        public void ClearCache()
        {
            if (_cache != null)
            {
                foreach (var (x, y) in _cache.Values)
                {
                    x?.Dispose();
                    y?.Dispose();
                }
                _cache.Clear();
            }
        }

        /// <summary>
        /// 데이터 증강: 가격에 노이즈 추가 (선택적)
        /// </summary>
        public void ApplyDataAugmentation(float noiseLevel = 0.01f)
        {
            var random = new Random();
            int count = _normalizedFeatures.GetLength(0);

            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < 5; j++) // OHLCV에만 노이즈 추가
                {
                    float noise = (float)(random.NextDouble() * 2 - 1) * noiseLevel;
                    _normalizedFeatures[i, j] += noise;
                }
            }

            Console.WriteLine($"[DataLoader] 데이터 증강 완료 (Noise Level: {noiseLevel})");
        }

        /// <summary>
        /// 메모리 정리
        /// </summary>
        public void Dispose()
        {
            ClearCache();
            _normalizedFeatures = null;
            _normalizedTargets = null;
            _rawData = null;
        }
    }
}
