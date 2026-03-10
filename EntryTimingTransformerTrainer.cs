using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TorchSharp;
using TradingBot.Services;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace TradingBot
{
    /// <summary>
    /// TorchSharp Transformer 기반 Entry Timing Binary Classifier
    /// 목표: Multi-Timeframe 시퀀스 데이터로 "진입 여부" 예측 (0/1)
    /// </summary>
    public class EntryTimingTransformerTrainer
    {
        private readonly int _seqLen = 60; // 15분봉 60개 = 15시간 히스토리
        private readonly int _featureDim = 49; // Feature 개수 (45 base + 4 Fib)
        private readonly int _hiddenDim = 128;
        private readonly int _numLayers = 4;
        private readonly int _numHeads = 8;
        private readonly Device _device;
        private TransformerClassifierModel? _model;
        private bool _isModelReady = false;
        private readonly string _modelPath;

        public bool IsModelReady => _isModelReady;
        public int SeqLen => _seqLen;

        public EntryTimingTransformerTrainer(string modelPath = "EntryTimingTransformer.pt")
        {
            if (!TorchInitializer.IsAvailable)
                throw new InvalidOperationException(
                    $"TorchSharp를 사용할 수 없습니다.\n{TorchInitializer.ErrorMessage}");

            _modelPath = modelPath;
            var resolved = TorchInitializer.ResolveDevice();
            _device = resolved ?? throw new InvalidOperationException("TorchSharp 디바이스 확인 실패");
            Console.WriteLine($"[TransformerBinary] Device: {_device.type}");
        }

        /// <summary>
        /// 모델 초기화
        /// </summary>
        public void InitializeModel()
        {
            _model = new TransformerClassifierModel(
                _featureDim,
                _hiddenDim,
                _numLayers,
                _numHeads,
                dropoutRate: 0.1);
            _model.to(_device);
            _isModelReady = true;
            Console.WriteLine("[TransformerBinary] 모델 초기화 완료");
        }

        /// <summary>
        /// 학습 실행
        /// </summary>
        public async Task<TransformerMetrics> TrainAsync(
            List<MultiTimeframeEntryFeature> trainingData,
            int epochs = 30,
            int batchSize = 32,
            float learningRate = 0.001f)
        {
            return await Task.Run(() =>
            {
                if (trainingData == null || trainingData.Count < _seqLen * 2)
                    throw new ArgumentException($"학습 데이터 부족 (최소 {_seqLen * 2}개 필요)");

                if (_model == null)
                    InitializeModel();

                Console.WriteLine($"[TransformerBinary] 학습 시작: {trainingData.Count}개 샘플, {epochs} epochs");

                var watch = System.Diagnostics.Stopwatch.StartNew();

                // 시퀀스 데이터 생성
                var sequences = CreateSequences(trainingData);
                Console.WriteLine($"[TransformerBinary] 시퀀스 생성: {sequences.Count}개");

                // 학습/검증 분할
                int trainSize = (int)(sequences.Count * 0.8);
                var trainSeq = sequences.Take(trainSize).ToList();
                var valSeq = sequences.Skip(trainSize).ToList();

                // Optimizer & Loss (회귀 모델: Time-to-Target 예측)
                var optimizer = optim.Adam(_model!.parameters(), lr: learningRate);
                var criterion = nn.MSELoss(); // Mean Squared Error for regression

                float bestValLoss = float.MaxValue;

                for (int epoch = 0; epoch < epochs; epoch++)
                {
                    // Training
                    _model.train();
                    float trainLoss = 0f;
                    float trainMAE = 0f; // Mean Absolute Error

                    for (int i = 0; i < trainSeq.Count; i += batchSize)
                    {
                        var batch = trainSeq.Skip(i).Take(batchSize).ToList();
                        var (inputs, labels) = PrepareBatch(batch);

                        optimizer.zero_grad();
                        var outputs = _model.forward(inputs);
                        var loss = criterion.forward(outputs, labels);
                        loss.backward();
                        optimizer.step();

                        trainLoss += loss.item<float>();

                        // MAE 계산 (예측 - 실제의 절대값 평균)
                        using var absDiff = torch.abs(outputs - labels);
                        trainMAE += absDiff.mean().item<float>() * batch.Count;

                        inputs.Dispose();
                        labels.Dispose();
                        outputs.Dispose();
                        loss.Dispose();
                    }

                    float avgTrainLoss = trainLoss / (trainSeq.Count / batchSize);
                    float avgTrainMAE = trainMAE / trainSeq.Count;

                    // Validation
                    _model.eval();
                    float valLoss = 0f;
                    float valMAE = 0f;

                    using (torch.no_grad())
                    {
                        for (int i = 0; i < valSeq.Count; i += batchSize)
                        {
                            var batch = valSeq.Skip(i).Take(batchSize).ToList();
                            var (inputs, labels) = PrepareBatch(batch);

                            var outputs = _model.forward(inputs);
                            var loss = criterion.forward(outputs, labels);
                            valLoss += loss.item<float>();

                            using var absDiff = torch.abs(outputs - labels);
                            valMAE += absDiff.mean().item<float>() * batch.Count;

                            inputs.Dispose();
                            labels.Dispose();
                            outputs.Dispose();
                            loss.Dispose();
                        }
                    }

                    float avgValLoss = valSeq.Count > 0 ? valLoss / (valSeq.Count / batchSize) : 0f;
                    float avgValMAE = valSeq.Count > 0 ? valMAE / valSeq.Count : 0f;

                    if ((epoch + 1) % 5 == 0 || epoch == 0)
                    {
                        Console.WriteLine($"[TransformerRegression] Epoch {epoch + 1}/{epochs} | " +
                            $"Train Loss: {avgTrainLoss:F4}, MAE: {avgTrainMAE:F2} candles | " +
                            $"Val Loss: {avgValLoss:F4}, MAE: {avgValMAE:F2} candles");
                    }

                    // 검증 손실이 개선되면 모델 저장
                    if (avgValLoss < bestValLoss)
                    {
                        bestValLoss = avgValLoss;
                        SaveModel();
                    }
                }

                watch.Stop();
                criterion.Dispose();

                Console.WriteLine($"[TransformerRegression] 학습 완료: {watch.Elapsed.TotalSeconds:F1}초, Best Val Loss: {bestValLoss:F4}");

                return new TransformerMetrics
                {
                    BestValidationLoss = bestValLoss,
                    Epochs = epochs,
                    TrainingTimeSeconds = watch.Elapsed.TotalSeconds
                };
            });
        }

        /// <summary>
        /// 실시간 예측: Time-to-Target 회귀 (목표가 도달까지 몇 개의 캔들?)
        /// </summary>
        /// <returns>(목표가 도달까지 캔들 개수, 신뢰도 점수 0~1)</returns>
        public (float candlesToTarget, float confidence) Predict(List<MultiTimeframeEntryFeature> recentFeatures)
        {
            if (!_isModelReady || _model == null)
                return (-1f, 0f);

            if (recentFeatures == null || recentFeatures.Count < _seqLen)
                return (-1f, 0f);

            try
            {
                _model.eval();

                var sequence = recentFeatures.TakeLast(_seqLen).ToList();
                var inputTensor = FeaturesToTensor(sequence).unsqueeze(0).to(_device); // [1, seqLen, features]

                using (torch.no_grad())
                {
                    var output = _model.forward(inputTensor); // [1, 1]
                    float rawPrediction = output[0, 0].item<float>();

                    // NaN/Infinity 방어
                    if (float.IsNaN(rawPrediction) || float.IsInfinity(rawPrediction))
                        rawPrediction = -1f;

                    // 예측값 클램핑 (0~32 캔들 범위)
                    float clampedCandles = Math.Clamp(rawPrediction, 0f, 32f);

                    // 신뢰도 계산: 예측이 유효 범위 내에 있으면 높은 점수
                    // 1~32 범위 내면 confidence=1.0, 범위 밖이면 감소
                    float confidence = (rawPrediction >= 1f && rawPrediction <= 32f) ? 1.0f : 0.5f;

                    inputTensor.Dispose();
                    output.Dispose();

                    return (clampedCandles, confidence);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransformerRegression] 예측 실패: {ex.Message}");
                return (-1f, 0f);
            }
        }

        private List<SequenceData> CreateSequences(List<MultiTimeframeEntryFeature> data)
        {
            var sequences = new List<SequenceData>();

            for (int i = _seqLen; i < data.Count; i++)
            {
                var seq = data.Skip(i - _seqLen).Take(_seqLen).ToList();
                float candlesLabel = data[i].CandlesToTarget;

                // Time-to-Target 라벨이 유효한 경우만 학습 샘플로 추가
                // -1은 "32캔들 내 목표가 미도달"을 의미하므로 제외
                if (candlesLabel >= 0f)
                {
                    sequences.Add(new SequenceData
                    {
                        Features = seq,
                        CandlesToTargetLabel = candlesLabel
                    });
                }
            }

            return sequences;
        }

        private (Tensor inputs, Tensor labels) PrepareBatch(List<SequenceData> batch)
        {
            int batchSize = batch.Count;
            float[,,] inputArray = new float[batchSize, _seqLen, _featureDim];
            float[,] labelArray = new float[batchSize, 1]; // 모델 출력 shape [batch, 1]과 일치

            for (int b = 0; b < batchSize; b++)
            {
                // Time-to-Target 회귀 라벨 (캔들 개수)
                labelArray[b, 0] = batch[b].CandlesToTargetLabel;

                for (int t = 0; t < _seqLen; t++)
                {
                    var feature = batch[b].Features[t];
                    inputArray[b, t, 0] = feature.D1_Trend;
                    inputArray[b, t, 1] = feature.D1_RSI;
                    inputArray[b, t, 2] = feature.D1_MACD;
                    inputArray[b, t, 3] = feature.D1_Signal;
                    inputArray[b, t, 4] = feature.D1_BBPosition;
                    inputArray[b, t, 5] = feature.D1_Volume_Ratio;
                    inputArray[b, t, 6] = feature.H4_Trend;
                    inputArray[b, t, 7] = feature.H4_RSI;
                    inputArray[b, t, 8] = feature.H4_MACD;
                    inputArray[b, t, 9] = feature.H4_Signal;
                    inputArray[b, t, 10] = feature.H4_BBPosition;
                    inputArray[b, t, 11] = feature.H4_Volume_Ratio;
                    inputArray[b, t, 12] = feature.H4_DistanceToSupport;
                    inputArray[b, t, 13] = feature.H4_DistanceToResist;
                    inputArray[b, t, 14] = feature.H2_Trend;
                    inputArray[b, t, 15] = feature.H2_RSI;
                    inputArray[b, t, 16] = feature.H2_MACD;
                    inputArray[b, t, 17] = feature.H2_Signal;
                    inputArray[b, t, 18] = feature.H2_BBPosition;
                    inputArray[b, t, 19] = feature.H2_Volume_Ratio;
                    inputArray[b, t, 20] = feature.H2_WavePosition;
                    inputArray[b, t, 21] = feature.H1_Trend;
                    inputArray[b, t, 22] = feature.H1_RSI;
                    inputArray[b, t, 23] = feature.H1_MACD;
                    inputArray[b, t, 24] = feature.H1_Signal;
                    inputArray[b, t, 25] = feature.H1_BBPosition;
                    inputArray[b, t, 26] = feature.H1_Volume_Ratio;
                    inputArray[b, t, 27] = feature.H1_MomentumStrength;
                    inputArray[b, t, 28] = feature.M15_RSI;
                    inputArray[b, t, 29] = feature.M15_MACD;
                    inputArray[b, t, 30] = feature.M15_Signal;
                    inputArray[b, t, 31] = feature.M15_BBPosition;
                    inputArray[b, t, 32] = feature.M15_Volume_Ratio;
                    inputArray[b, t, 33] = feature.M15_PriceVsSMA20;
                    inputArray[b, t, 34] = feature.M15_PriceVsSMA60;
                    inputArray[b, t, 35] = feature.M15_ADX;
                    inputArray[b, t, 36] = feature.M15_PlusDI;
                    inputArray[b, t, 37] = feature.M15_MinusDI;
                    inputArray[b, t, 38] = feature.M15_ATR;
                    inputArray[b, t, 39] = feature.M15_OI_Change_Pct;
                    inputArray[b, t, 40] = feature.HourOfDay / 24f;
                    inputArray[b, t, 41] = feature.DayOfWeek / 7f;
                    inputArray[b, t, 42] = feature.IsAsianSession;
                    inputArray[b, t, 43] = feature.IsEuropeSession;
                    inputArray[b, t, 44] = feature.IsUSSession;
                    inputArray[b, t, 45] = feature.Fib_DistanceTo0382_Pct;
                    inputArray[b, t, 46] = feature.Fib_DistanceTo0618_Pct;
                    inputArray[b, t, 47] = feature.Fib_DistanceTo0786_Pct;
                    inputArray[b, t, 48] = feature.Fib_InEntryZone;
                }
            }

            var inputs = tensor(inputArray).to(_device);
            var labels = tensor(labelArray).to(_device);

            return (inputs, labels);
        }

        private Tensor FeaturesToTensor(List<MultiTimeframeEntryFeature> features)
        {
            float[,] array = new float[features.Count, _featureDim];
            for (int i = 0; i < features.Count; i++)
            {
                var f = features[i];
                array[i, 0] = f.D1_Trend;
                array[i, 1] = f.D1_RSI;
                array[i, 2] = f.D1_MACD;
                array[i, 3] = f.D1_Signal;
                array[i, 4] = f.D1_BBPosition;
                array[i, 5] = f.D1_Volume_Ratio;
                array[i, 6] = f.H4_Trend;
                array[i, 7] = f.H4_RSI;
                array[i, 8] = f.H4_MACD;
                array[i, 9] = f.H4_Signal;
                array[i, 10] = f.H4_BBPosition;
                array[i, 11] = f.H4_Volume_Ratio;
                array[i, 12] = f.H4_DistanceToSupport;
                array[i, 13] = f.H4_DistanceToResist;
                array[i, 14] = f.H2_Trend;
                array[i, 15] = f.H2_RSI;
                array[i, 16] = f.H2_MACD;
                array[i, 17] = f.H2_Signal;
                array[i, 18] = f.H2_BBPosition;
                array[i, 19] = f.H2_Volume_Ratio;
                array[i, 20] = f.H2_WavePosition;
                array[i, 21] = f.H1_Trend;
                array[i, 22] = f.H1_RSI;
                array[i, 23] = f.H1_MACD;
                array[i, 24] = f.H1_Signal;
                array[i, 25] = f.H1_BBPosition;
                array[i, 26] = f.H1_Volume_Ratio;
                array[i, 27] = f.H1_MomentumStrength;
                array[i, 28] = f.M15_RSI;
                array[i, 29] = f.M15_MACD;
                array[i, 30] = f.M15_Signal;
                array[i, 31] = f.M15_BBPosition;
                array[i, 32] = f.M15_Volume_Ratio;
                array[i, 33] = f.M15_PriceVsSMA20;
                array[i, 34] = f.M15_PriceVsSMA60;
                array[i, 35] = f.M15_ADX;
                array[i, 36] = f.M15_PlusDI;
                array[i, 37] = f.M15_MinusDI;
                array[i, 38] = f.M15_ATR;
                array[i, 39] = f.M15_OI_Change_Pct;
                array[i, 40] = f.HourOfDay / 24f;
                array[i, 41] = f.DayOfWeek / 7f;
                array[i, 42] = f.IsAsianSession;
                array[i, 43] = f.IsEuropeSession;
                array[i, 44] = f.IsUSSession;
                array[i, 45] = f.Fib_DistanceTo0382_Pct;
                array[i, 46] = f.Fib_DistanceTo0618_Pct;
                array[i, 47] = f.Fib_DistanceTo0786_Pct;
                array[i, 48] = f.Fib_InEntryZone;
            }
            return tensor(array);
        }

        public void SaveModel()
        {
            if (_model == null)
                return;

            try
            {
                _model.save(_modelPath);
                Console.WriteLine($"[TransformerBinary] 모델 저장: {_modelPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransformerBinary] 저장 실패: {ex.Message}");
            }
        }

        public bool LoadModel()
        {
            try
            {
                if (!File.Exists(_modelPath))
                {
                    Console.WriteLine($"[TransformerBinary] 모델 파일 없음: {_modelPath}");
                    return false;
                }

                if (_model == null)
                    InitializeModel();

                _model!.load(_modelPath);
                _isModelReady = true;
                Console.WriteLine($"[TransformerBinary] 모델 로드 성공: {_modelPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransformerBinary] 로드 실패: {ex.Message}");
                return false;
            }
        }

        private class SequenceData
        {
            public List<MultiTimeframeEntryFeature> Features { get; set; } = new();
            /// <summary>
            /// Time-to-Target 회귀 라벨: 목표가 도달까지의 캔들 개수 (예: 12.0 = 3시간 후)
            /// </summary>
            public float CandlesToTargetLabel { get; set; }
        }
    }

    public class TransformerMetrics
    {
        /// <summary>
        /// 최적 검증 손실 (회귀 모델이므로 MAE 또는 MSE)
        /// </summary>
        public float BestValidationLoss { get; set; }
        public int Epochs { get; set; }
        public double TrainingTimeSeconds { get; set; }
    }

    /// <summary>
    /// Transformer Binary Classifier 모델 아키텍처
    /// </summary>
    internal class TransformerClassifierModel : Module<Tensor, Tensor>
    {
        private readonly TorchSharp.Modules.TransformerEncoder _encoder;
        private readonly Module<Tensor, Tensor> _inputEmbedding;
        private readonly Module<Tensor, Tensor> _outputClassifier;
        private readonly int _hiddenDim;

        public TransformerClassifierModel(
            int featureDim,
            int hiddenDim,
            int numLayers,
            int numHeads,
            double dropoutRate = 0.1)
            : base("TransformerClassifier")
        {
            _hiddenDim = hiddenDim;
            
            // 1. Input Embedding: featureDim -> hiddenDim
            _inputEmbedding = Linear(featureDim, hiddenDim);

            // 2. Transformer Encoder
            var encoderLayer = TransformerEncoderLayer(hiddenDim, numHeads, dim_feedforward: hiddenDim * 4, dropout: dropoutRate);
            _encoder = TransformerEncoder(encoderLayer, numLayers);

            // 3. Output Classifier: hiddenDim -> 1 (binary)
            _outputClassifier = Sequential(
                ("fc", Linear(hiddenDim, 1)),
                ("dropout", Dropout(dropoutRate))
            );

            RegisterComponents();
        }

        public override Tensor forward(Tensor input)
        {
            // input: [batch, seq, featureDim]

            // 1. Embedding
            using var embedded = _inputEmbedding.forward(input); // [batch, seq, hiddenDim]
            using var scaled = embedded * Math.Sqrt(_hiddenDim);

            // 2. Transformer Encoder (requires [seq, batch, hiddenDim])
            using var permuted1 = scaled.permute(1, 0, 2); // [seq, batch, hiddenDim]
            using var encoded = _encoder.forward(permuted1, null, null);
            using var permuted2 = encoded.permute(1, 0, 2); // [batch, seq, hiddenDim]

            // 3. Take last sequence output for classification
            using var lastOutput = permuted2.select(1, -1); // [batch, hiddenDim]
            
            // 4. Output classifier
            var output = _outputClassifier.forward(lastOutput); // [batch, 1]
            return output;
        }
    }
}
