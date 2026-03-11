using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot
{
    /// <summary>
    /// TensorFlow.NET 기반 Entry Timing Transformer Trainer
    /// 기존 EntryTimingTransformerTrainer (TorchSharp) 완전 대체
    /// </summary>
    public class TensorFlowEntryTimingTrainer : IDisposable
    {
        private readonly TensorFlowTransformer _transformer;
        private bool _disposed;

        public bool IsModelReady => _transformer.IsModelReady;
        public int SeqLen => _transformer.SeqLen;

        public event Action<string>? OnLog;
        public event Action<int, int, float>? OnEpochCompleted;

        public TensorFlowEntryTimingTrainer(int seqLen = 8, int featureDim = 20)
        {
            _transformer = new TensorFlowTransformer(seqLen, featureDim);
            _transformer.OnLog += msg => OnLog?.Invoke(msg);
            _transformer.OnEpochCompleted += (epoch, total, loss) => OnEpochCompleted?.Invoke(epoch, total, loss);
        }

        public void InitializeModel()
        {
            _transformer.InitializeModel();
        }

        public async Task<(float BestValidationLoss, float FinalTrainLoss, int TrainedEpochs)> TrainAsync(
            List<MultiTimeframeEntryFeature> trainingData,
            int epochs = 5,
            int batchSize = 16,
            float learningRate = 0.001f,
            CancellationToken token = default)
        {
            return await Task.Run(() => _transformer.Train(trainingData, epochs, batchSize, learningRate), token);
        }

        public (float BestValidationLoss, float FinalTrainLoss, int TrainedEpochs) Train(
            List<MultiTimeframeEntryFeature> trainingData,
            int epochs = 5,
            int batchSize = 16,
            float learningRate = 0.001f)
        {
            return _transformer.Train(trainingData, epochs, batchSize, learningRate);
        }

        public (float candlesToTarget, float confidence) Predict(List<MultiTimeframeEntryFeature> sequence)
        {
            return _transformer.PredictWithConfidence(sequence);
        }

        public void SaveModel(string path = "tensorflow_entry_timing_model")
        {
            _transformer.SaveModel(path);
        }

        public void LoadModel(string path = "tensorflow_entry_timing_model")
        {
            _transformer.LoadModel(path);
            if (!_transformer.IsModelReady)
            {
                OnLog?.Invoke("[TensorFlowEntryTimingTrainer] 저장된 모델이 없거나 로드 실패 - 새 모델을 초기화하세요.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _transformer?.Dispose();
            _disposed = true;
        }
    }
}
