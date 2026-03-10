using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TorchSharp;
using TradingBot.Models;
using TradingBot.Services;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;
using static TorchSharp.torch.optim;

namespace TradingBot.Services.AI
{
    public class TransformerTrainer : IDisposable
    {
        private readonly int _inputDim;
        private readonly int _dModel;
        private readonly int _nHeads;
        private readonly int _nLayers;
        private readonly int _outputDim;
        private readonly int _seqLen;
        private readonly string _modelPath;
        private readonly string _statsPath;

        private TimeSeriesTransformer? _model;
        private readonly Device _device;
        private TimeSeriesDataLoader? _dataLoader; // 최적화된 데이터 로더
        private readonly ReaderWriterLockSlim _modelLock = new();

        // [추가] 외부에서 시퀀스 길이 참조 가능하도록 공개
        public int SeqLen => _seqLen;

        // [추가] 추론 준비 상태 공개
        public bool IsModelReady =>
            _model != null &&
            _dataLoader != null &&
            _means != null && _stds != null &&
            _means.Length == _inputDim &&
            _stds.Length == _inputDim &&
            _stds.All(s => Math.Abs(s) > 1e-8f);

        // 정규화 파라미터 (DataLoader에서 관리)
        private float[]? _means;
        private float[]? _stds;

        // [추가] 학습 상태 모니터링을 위한 이벤트
        public event Action<int, int, float>? OnEpochCompleted; // epoch, totalEpochs, loss
        public event Action<string>? OnLog; // 로그 메시지

        public TransformerTrainer(int inputDim, int dModel, int nHeads, int nLayers, int outputDim, int seqLen, string modelPath = "transformer_model.dat")
        {
            // TorchSharp 사용 가능 여부 확인
            if (!TradingBot.Services.TorchInitializer.IsAvailable)
            {
                throw new InvalidOperationException(
                    "TorchSharp를 사용할 수 없습니다. TransformerTrainer를 초기화할 수 없습니다.\n" +
                    TradingBot.Services.TorchInitializer.ErrorMessage);
            }

            _inputDim = inputDim;
            _dModel = dModel;
            _nHeads = nHeads;
            _nLayers = nLayers;
            _outputDim = outputDim;
            _seqLen = seqLen;
            _modelPath = modelPath;
            _statsPath = Path.ChangeExtension(modelPath, ".stats.json");

            try
            {
                var resolved = TorchInitializer.ResolveDevice();
                if (resolved == null)
                    throw new InvalidOperationException("TorchSharp 디바이스를 확인할 수 없습니다.");
                _device = resolved;
                Console.WriteLine($"[TransformerTrainer] Device: {_device}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"[TransformerTrainer] TorchSharp 초기화 실패: {ex.Message}", ex);
            }

            InitializeModel();
        }

        private void InitializeModel()
        {
            _model = new TimeSeriesTransformer(_inputDim, _dModel, _nHeads, _nLayers, _outputDim, _seqLen);
            _model.to(_device);
        }

        public void Train(List<CandleData> data, int epochs = 10, int batchSize = 32, double learningRate = 0.001)
        {
            try
            {
                if (data == null || data.Count <= _seqLen)
                {
                    OnLog?.Invoke("[TransformerTrainer] 데이터가 부족하여 학습할 수 없습니다.");
                    return;
                }

                OnLog?.Invoke($"[TransformerTrainer] 최적화된 데이터 로더로 전처리 중... (Count: {data.Count})");

                // 최적화된 DataLoader 사용
                _dataLoader = new TimeSeriesDataLoader(
                    sequenceLength: _seqLen,
                    inputDim: _inputDim,
                    batchSize: batchSize,
                    shuffle: true,
                    useCache: true,
                    device: _device
                );

                try
                {
                    _dataLoader.LoadData(data);
                }
                catch (ArgumentException ex)
                {
                    OnLog?.Invoke($"⚠️ [TransformerTrainer] {ex.Message}");
                    return;
                }

                // 정규화 파라미터 저장
                _means = _dataLoader.Means;
                _stds = _dataLoader.Stds;

                if (_model == null)
                {
                    OnLog?.Invoke("❌ [TransformerTrainer] 모델 초기화 실패");
                    return;
                }

                _modelLock.EnterWriteLock();
                try
                {
                    using var optimizer = Adam(_model.parameters(), learningRate);
                    using var lossFunc = MSELoss();

                    _model.train();

                    OnLog?.Invoke($"🚀 [TransformerTrainer] 학습 시작: {epochs} epochs, {_dataLoader.TotalBatches} batches/epoch");

                    for (int epoch = 0; epoch < epochs; epoch++)
                    {
                        float totalLoss = 0;
                        int batchCount = 0;

                        // 배치별 학습 (메모리 효율적)
                        foreach (var (xBatch, yBatch) in _dataLoader.GetBatches())
                        {
                            using (xBatch)
                            using (yBatch)
                            {
                                optimizer.zero_grad();

                                using var output = _model.forward(xBatch);
                                using var loss = lossFunc.forward(output, yBatch);

                                loss.backward();
                                optimizer.step();

                                totalLoss += loss.item<float>();
                                batchCount++;
                            }
                        }

                        float avgLoss = totalLoss / batchCount;

                        // [수정] 이벤트 발생 (UI 업데이트용)
                        OnEpochCompleted?.Invoke(epoch + 1, epochs, avgLoss);
                        OnLog?.Invoke($"📉 [Transformer] Epoch {epoch + 1}/{epochs} | Loss: {avgLoss:F6}");

                        // 주기적 저장 (10 epoch마다)
                        if ((epoch + 1) % 10 == 0)
                        {
                            SaveModel();
                        }
                    }

                    SaveModel();
                    OnLog?.Invoke("✅ [TransformerTrainer] 학습 완료!");
                }
                finally
                {
                    _modelLock.ExitWriteLock();
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [TransformerTrainer] 학습 중 심각한 오류 발생: {ex.Message}");
                OnLog?.Invoke($"Stack Trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    OnLog?.Invoke($"Inner Exception: {ex.InnerException.Message}");
                }
                throw; // 예외를 다시 던져서 호출자가 처리하도록 함
            }
        }

        /// <summary>
        /// [Agent 1] 온라인 학습 (점진적 업데이트)
        /// 새로운 데이터가 들어올 때마다 모델을 미세 조정합니다.
        /// </summary>
        public void TrainOnline(List<CandleData> newData, int epochs = 1, double learningRate = 0.0001)
        {
            if (_model == null || _dataLoader == null)
            {
                OnLog?.Invoke("⚠️ 모델이 초기화되지 않아 온라인 학습을 건너뜁니다.");
                return;
            }
            if (newData == null || newData.Count <= _seqLen) return;

            // 기존 정규화 파라미터 유지를 위해 DataLoader 재사용
            var onlineLoader = new TimeSeriesDataLoader(
                sequenceLength: _seqLen,
                inputDim: _inputDim,
                batchSize: 16, // 작은 배치
                shuffle: true, // 온라인 학습도 셔플링
                useCache: false, // 매번 다른 데이터이므로 캐시 사용 안함
                device: _device
            );

            // 기존 Mean/Std 주입
            onlineLoader.Means = _dataLoader.Means;
            onlineLoader.Stds = _dataLoader.Stds;

            onlineLoader.LoadData(newData);

            _modelLock.EnterWriteLock();
            try
            {
                using var optimizer = Adam(_model.parameters(), learningRate);
                using var lossFunc = MSELoss();

                _model.train();

                for (int epoch = 0; epoch < epochs; epoch++)
                {
                    foreach (var (xBatch, yBatch) in onlineLoader.GetBatches())
                    {
                        using (xBatch)
                        using (yBatch)
                        {
                            optimizer.zero_grad();
                            using var output = _model.forward(xBatch);
                            using var loss = lossFunc.forward(output, yBatch);
                            loss.backward();
                            optimizer.step();
                        }
                    }
                }
            }
            finally
            {
                _modelLock.ExitWriteLock();
                onlineLoader.Dispose();
            }
            OnLog?.Invoke($"✨ [Transformer] 온라인 학습 완료 ({newData.Count}건)");
        }

        /// <summary>
        /// 실시간 추론 (최근 시퀀스 기반 예측)
        /// </summary>
        public float Predict(List<CandleData> recentSequence)
        {
            if (recentSequence == null || recentSequence.Count != _seqLen)
            {
                throw new ArgumentException($"시퀀스 길이가 맞지 않습니다. 필요: {_seqLen}, 현재: {recentSequence?.Count ?? 0}");
            }

            // [수정] 런타임에서 아직 준비되지 않았다면 1회 자동 로드 시도
            if (!IsModelReady)
            {
                LoadModel();
            }

            if (!IsModelReady)
            {
                throw new InvalidOperationException(
                    $"모델이 학습되지 않았거나 정규화 파라미터가 없습니다. 모델 파일({_modelPath})/통계 파일({_statsPath})을 확인하세요.");
            }

            if (_model == null || _dataLoader == null)
            {
                throw new InvalidOperationException("모델 또는 데이터 로더가 초기화되지 않았습니다.");
            }

            _modelLock.EnterReadLock();
            try
            {
                _model.eval();

                using (torch.no_grad())
                {
                    using var inputTensor = _dataLoader.CreateInferenceTensor(recentSequence);
                    using var output = _model.forward(inputTensor);

                    float predictedPriceChangeRate = output[0, 0].item<float>();
                    // [FIX] DenormalizeTarget은 변화율을 그대로 반환
                    float denormalizedChangeRate = _dataLoader.DenormalizeTarget(predictedPriceChangeRate);
                    
                    // [FIX] 변화율을 실제 가격으로 변환: 현재가 * (1 + 변화율)
                    float currentPrice = (float)recentSequence[^1].Close;
                    float predictedPrice = currentPrice * (1f + denormalizedChangeRate);

                    return predictedPrice;
                }
            }
            finally
            {
                _modelLock.ExitReadLock();
            }
        }

        /// <summary>
        /// [레거시] 이전 방식의 데이터 전처리 (호환성 유지)
        /// </summary>
        [Obsolete("Use TimeSeriesDataLoader instead for better performance")]
        private (Tensor features, Tensor labels) PrepareData(List<CandleData> data)
        {
            int count = data.Count;
            float[,] rawFeatures = new float[count, _inputDim];
            float[] rawTargets = new float[count];

            // 1. 데이터 추출
            for (int i = 0; i < count; i++)
            {
                var c = data[i];
                var features = TransformerFeatureMapper.CreateFeatureVector(c, _inputDim);
                for (int j = 0; j < features.Length; j++)
                {
                    rawFeatures[i, j] = features[j];
                }

                // [FIX] Target: 가격 변화율 (레거시 호환)
                if (i < count - 1)
                {
                    decimal priceChange = (data[i + 1].Close - c.Close) / c.Close;
                    rawTargets[i] = (float)priceChange;
                }
                else
                {
                    rawTargets[i] = 0f;
                }
            }

            // 2. 정규화 (Standardization) 계산 및 적용
            _means = new float[_inputDim];
            _stds = new float[_inputDim];

            for (int j = 0; j < _inputDim; j++)
            {
                float sum = 0;
                for (int i = 0; i < count; i++) sum += rawFeatures[i, j];
                _means[j] = sum / count;

                float sumSq = 0;
                for (int i = 0; i < count; i++) sumSq += (rawFeatures[i, j] - _means[j]) * (rawFeatures[i, j] - _means[j]);
                _stds[j] = (float)Math.Sqrt(sumSq / count);
                if (_stds[j] == 0) _stds[j] = 1;

                for (int i = 0; i < count; i++)
                {
                    rawFeatures[i, j] = (rawFeatures[i, j] - _means[j]) / _stds[j];
                }
            }

            // [FIX] Target은 가격 변화율이므로 정규화하지 않음 (이미 작은 범위 -1~1)

            // 3. 슬라이딩 윈도우 생성
            int samples = count - _seqLen;
            if (samples <= 0) return (torch.empty(0), torch.empty(0));

            float[] xFlat = new float[samples * _seqLen * _inputDim];
            float[] yFlat = new float[samples * _outputDim];

            for (int i = 0; i < samples; i++)
            {
                // Input Sequence: i ~ i + seqLen
                for (int t = 0; t < _seqLen; t++)
                {
                    for (int f = 0; f < _inputDim; f++)
                    {
                        xFlat[(i * _seqLen + t) * _inputDim + f] = rawFeatures[i + t, f];
                    }
                }

                // Target: i + seqLen (시퀀스 바로 다음 시점)
                yFlat[i] = rawTargets[i + _seqLen];
            }

            var xTensor = torch.tensor(xFlat, new long[] { samples, _seqLen, _inputDim }, device: _device);
            var yTensor = torch.tensor(yFlat, new long[] { samples, _outputDim }, device: _device);

            return (xTensor, yTensor);
        }

        public void SaveModel()
        {
            if (_model == null)
            {
                OnLog?.Invoke("⚠️ [TransformerTrainer] 저장할 모델이 없습니다.");
                return;
            }

            _model.save(_modelPath);

            // 정규화 파라미터 저장
            var stats = new { Means = _means, Stds = _stds };
            File.WriteAllText(_statsPath, JsonSerializer.Serialize(stats));

            OnLog?.Invoke($"💾 [TransformerTrainer] 모델 저장 완료: {_modelPath}");
        }

        public void LoadModel()
        {
            if (!File.Exists(_modelPath))
            {
                OnLog?.Invoke($"⚠️ [TransformerTrainer] 모델 파일이 없습니다: {_modelPath}");
                return;
            }

            if (!File.Exists(_statsPath))
            {
                OnLog?.Invoke($"⚠️ [TransformerTrainer] 정규화 통계 파일이 없습니다: {_statsPath}");
                return;
            }

            try
            {
                if (_model == null)
                {
                    OnLog?.Invoke("⚠️ [TransformerTrainer] 모델 인스턴스가 초기화되지 않았습니다.");
                    return;
                }

                _model.load(_modelPath);
                _model.eval();

                var statsJson = File.ReadAllText(_statsPath);
                var stats = JsonSerializer.Deserialize<ModelStats>(statsJson);
                _means = stats?.Means ?? Array.Empty<float>();
                _stds = stats?.Stds ?? Array.Empty<float>();

                if (_means.Length != _inputDim || _stds.Length != _inputDim)
                {
                    OnLog?.Invoke($"⚠️ [TransformerTrainer] 통계 파라미터 길이 불일치 (Means: {_means.Length}, Stds: {_stds.Length}, Expected: {_inputDim})");
                    _means = Array.Empty<float>();
                    _stds = Array.Empty<float>();
                    _dataLoader = null;
                    return;
                }

                // DataLoader 초기화 (추론용)
                _dataLoader = new TimeSeriesDataLoader(
                    sequenceLength: _seqLen,
                    inputDim: _inputDim,
                    batchSize: 1,
                    shuffle: false,
                    useCache: false,
                    device: _device
                );

                // 정규화 파라미터 복원
                _dataLoader.Means = _means;
                _dataLoader.Stds = _stds;

                OnLog?.Invoke($"📂 [TransformerTrainer] 모델 로드 완료: {_modelPath}");
            }
            catch (Exception ex)
            {
                _dataLoader = null;
                _means = Array.Empty<float>();
                _stds = Array.Empty<float>();
                OnLog?.Invoke($"❌ [TransformerTrainer] 모델 로드 실패: {ex.Message}");
            }
        }

        private volatile bool _disposed = false;

        /// <summary>
        /// 리소스 정리
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                _dataLoader?.Dispose();
                _dataLoader = null;
                
                _model?.Dispose();
                _model = null;
                
                _means = null;
                _stds = null;

                _modelLock?.Dispose();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[TransformerTrainer] Dispose 오류: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
            
            GC.SuppressFinalize(this);
        }

        ~TransformerTrainer()
        {
            Dispose();
        }

        private class ModelStats
        {
            public float[] Means { get; set; } = Array.Empty<float>();
            public float[] Stds { get; set; } = Array.Empty<float>();
        }
    }
}
