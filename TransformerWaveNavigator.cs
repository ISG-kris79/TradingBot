using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TorchSharp;
using TradingBot.Services;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace TradingBot
{
    /// <summary>
    /// Transformer Wave Navigator (네비게이터 - 우뇌)
    /// 역할: 시계열 파동 패턴 분석하여 "지금이 3파 진입 타이밍인가?" 예측
    /// 학습 목표: 엘리엇 3파 시작 시점 포착 (Binary Classification)
    /// </summary>
    public class TransformerWaveNavigator : IDisposable
    {
        private readonly string _modelPath;
        private readonly int _seqLen = 60;      // 시퀀스 길이 (60개 15분봉 = 15시간)
        private readonly int _featureCount = 18; // 특징 개수 (기존 + 파동 특징)
        private readonly Device _device;
        private WaveTransformerModel? _model;
        private bool _isReady;

        // 하이퍼파라미터
        private const int HiddenSize = 128;
        private const int NumLayers = 3;
        private const int NumHeads = 4;
        private const float Dropout = 0.15f;
        private const int Epochs = 50;
        private const int BatchSize = 32;
        private const float LearningRate = 0.001f;

        public bool IsReady => _isReady;
        public int SeqLen => _seqLen;

        public TransformerWaveNavigator(string modelPath = "transformer_wave_navigator.dat")
        {
            _modelPath = modelPath;
            var resolved = TorchInitializer.ResolveDevice();
            _device = resolved ?? throw new InvalidOperationException("TorchSharp 초기화 실패: 디바이스를 확인할 수 없습니다.");
            
            try
            {
                LoadModel();
            }
            catch
            {
                _isReady = false;
            }
        }

        /// <summary>
        /// Transformer 모델 정의 (Binary Classification)
        /// </summary>
        private class WaveTransformerModel : Module<Tensor, Tensor>
        {
            private readonly Module<Tensor, Tensor> _embedding;
            private readonly Module<Tensor, Tensor> _fc1;
            private readonly Module<Tensor, Tensor> _fc2;
            private readonly Module<Tensor, Tensor> _output;
            private readonly Module<Tensor, Tensor> _dropout;

            public WaveTransformerModel(int featureCount, int hiddenSize, int numLayers, int numHeads, float dropout)
                : base("WaveTransformerModel")
            {
                _embedding = Linear(featureCount, hiddenSize);
                _fc1 = Linear(hiddenSize, hiddenSize / 2);
                _fc2 = Linear(hiddenSize / 2, hiddenSize / 4);
                _output = Linear(hiddenSize / 4, 1); // Binary output (0~1)
                _dropout = Dropout(dropout);

                RegisterComponents();
            }

            public override Tensor forward(Tensor x)
            {
                using var scope = torch.NewDisposeScope();
                
                // x: (batch, seq, features)
                var embedded = functional.relu(_embedding.forward(x)); // (batch, seq, hidden)
                
                // 시퀀스 평균 pooling (간소화)
                var pooled = embedded.mean(new long[] { 1 }); // (batch, hidden)
                
                var fc1Out = functional.relu(_fc1.forward(pooled));
                var dropped1 = _dropout.forward(fc1Out);
                var fc2Out = functional.relu(_fc2.forward(dropped1));
                var dropped2 = _dropout.forward(fc2Out);
                
                var output = functional.sigmoid(_output.forward(dropped2)); // (batch, 1)
                
                return output.MoveToOuterDisposeScope();
            }
        }

        /// <summary>
        /// 모델 학습
        /// </summary>
        public async Task<(float bestAccuracy, float finalLoss)> TrainAsync(
            List<WaveTrainingData> trainingData,
            CancellationToken token)
        {
            if (trainingData.Count < 100)
                throw new ArgumentException("최소 100개 이상의 학습 데이터 필요");

            await Task.Run(() =>
            {
                // 모델 초기화
                _model?.Dispose();
                _model = new WaveTransformerModel(_featureCount, HiddenSize, NumLayers, NumHeads, Dropout);
                _model.to(_device);

                var optimizer = optim.Adam(_model.parameters(), LearningRate);
                var criterion = functional.binary_cross_entropy;

                // 데이터 준비
                var (xTrain, yTrain, xVal, yVal) = PrepareTrainingData(trainingData);

                float bestAccuracy = 0f;
                float finalLoss = 0f;

                for (int epoch = 1; epoch <= Epochs; epoch++)
                {
                    if (token.IsCancellationRequested)
                        break;

                    _model.train();
                    float epochLoss = 0f;
                    int batchCount = 0;

                    // 미니배치 학습
                    for (int i = 0; i < xTrain.Count; i += BatchSize)
                    {
                        if (token.IsCancellationRequested)
                            break;

                        int batchSize = Math.Min(BatchSize, xTrain.Count - i);
                        var batchX = xTrain.Skip(i).Take(batchSize).ToList();
                        var batchY = yTrain.Skip(i).Take(batchSize).ToList();

                        using var xTensor = CreateSequenceTensor(batchX);
                        using var yTensor = torch.tensor(batchY.ToArray(), dtype: torch.float32, device: _device).reshape(-1, 1);

                        optimizer.zero_grad();
                        using var predictions = _model.forward(xTensor);
                        using var loss = criterion(predictions, yTensor);
                        
                        loss.backward();
                        optimizer.step();

                        epochLoss += loss.item<float>();
                        batchCount++;
                    }

                    // Validation 평가
                    _model.eval();
                    using (var _ = torch.no_grad())
                    {
                        using var valXTensor = CreateSequenceTensor(xVal);
                        using var valYTensor = torch.tensor(yVal.ToArray(), dtype: torch.float32, device: _device).reshape(-1, 1);
                        using var valPredictions = _model.forward(valXTensor);
                        
                        // 정확도 계산 (threshold 0.5)
                        var predicted = (valPredictions > 0.5f).to(torch.float32);
                        var correct = (predicted == valYTensor).sum().item<long>();
                        float accuracy = (float)correct / yVal.Count;

                        if (accuracy > bestAccuracy)
                            bestAccuracy = accuracy;

                        if (epoch % 5 == 0 || epoch == 1)
                        {
                            Console.WriteLine($"[TransformerWave] Epoch {epoch}/{Epochs} | Loss={epochLoss/batchCount:F4} | ValAcc={accuracy:P1}");
                        }
                    }

                    finalLoss = epochLoss / batchCount;
                }

                // 모델 저장
                _model.save(_modelPath);
                _isReady = true;

                return (bestAccuracy, finalLoss);

            }, token);

            return (0f, 0f);
        }

        /// <summary>
        /// 학습 데이터 준비 (80% 학습, 20% 검증)
        /// </summary>
        private (List<List<float[]>> xTrain, List<float> yTrain, List<List<float[]>> xVal, List<float> yVal) 
            PrepareTrainingData(List<WaveTrainingData> data)
        {
            // 시퀀스 길이 필터링
            var validData = data.Where(d => d.Sequence.Count >= _seqLen).ToList();
            
            // 셔플
            var random = new Random(42);
            validData = validData.OrderBy(_ => random.Next()).ToList();

            // 분할
            int trainSize = (int)(validData.Count * 0.8f);
            var trainData = validData.Take(trainSize).ToList();
            var valData = validData.Skip(trainSize).ToList();

            var xTrain = trainData.Select(d => d.Sequence.TakeLast(_seqLen).ToList()).ToList();
            var yTrain = trainData.Select(d => d.IsWave3Entry ? 1f : 0f).ToList();
            
            var xVal = valData.Select(d => d.Sequence.TakeLast(_seqLen).ToList()).ToList();
            var yVal = valData.Select(d => d.IsWave3Entry ? 1f : 0f).ToList();

            return (xTrain, yTrain, xVal, yVal);
        }

        /// <summary>
        /// 시퀀스 데이터를 Tensor로 변환
        /// </summary>
        private Tensor CreateSequenceTensor(List<List<float[]>> sequences)
        {
            int batchSize = sequences.Count;
            var array = new float[batchSize, _seqLen, _featureCount];

            for (int b = 0; b < batchSize; b++)
            {
                var seq = sequences[b];
                for (int s = 0; s < _seqLen && s < seq.Count; s++)
                {
                    for (int f = 0; f < _featureCount && f < seq[s].Length; f++)
                    {
                        array[b, s, f] = seq[s][f];
                    }
                }
            }

            return torch.tensor(array, dtype: torch.float32, device: _device);
        }

        /// <summary>
        /// 실시간 예측: 현재가 3파 진입 타이밍인가?
        /// </summary>
        public float PredictWave3EntryProbability(List<float[]> sequence)
        {
            if (!_isReady || _model == null)
                return 0.5f; // 모델 미준비 시 중립

            if (sequence.Count < _seqLen)
                return 0f; // 시퀀스 부족

            _model.eval();
            using var _ = torch.no_grad();
            
            var input = sequence.TakeLast(_seqLen).ToList();
            using var xTensor = CreateSequenceTensor(new List<List<float[]>> { input });
            using var prediction = _model.forward(xTensor);
            
            return prediction[0, 0].item<float>();
        }

        /// <summary>
        /// 모델 로드
        /// </summary>
        private void LoadModel()
        {
            if (!System.IO.File.Exists(_modelPath))
            {
                _isReady = false;
                return;
            }

            _model = new WaveTransformerModel(_featureCount, HiddenSize, NumLayers, NumHeads, Dropout);
            _model.load(_modelPath);
            _model.to(_device);
            _model.eval();
            _isReady = true;
        }

        public void Dispose()
        {
            _model?.Dispose();
        }
    }

    /// <summary>
    /// Transformer 학습용 데이터 구조
    /// </summary>
    public class WaveTrainingData
    {
        public List<float[]> Sequence { get; set; } = new(); // 시계열 특징 시퀀스
        public bool IsWave3Entry { get; set; }                // 라벨: 3파 진입 타이밍인가?
        public string Symbol { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}
