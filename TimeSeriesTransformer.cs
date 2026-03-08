using System;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace TradingBot.Services.AI
{
    /// <summary>
    /// 시계열 데이터 예측을 위한 Transformer 모델 (TorchSharp 기반)
    /// 구조: Input Embedding -> Positional Encoding -> Transformer Encoder -> Output Layer
    /// </summary>
    public class TimeSeriesTransformer : Module<Tensor, Tensor>
    {
        private bool _disposed = false;
        private readonly int _dModel;
        private readonly Module<Tensor, Tensor> _inputEmbedding;
        private readonly PositionalEncoding _positionalEncoding;
        private readonly TorchSharp.Modules.TransformerEncoder _transformerEncoder;
        private readonly Module<Tensor, Tensor> _outputLayer;

        /// <summary>
        /// 생성자
        /// </summary>
        /// <param name="inputDim">입력 피처 차원 (예: OHLCV + 지표 = 15)</param>
        /// <param name="dModel">모델 내부 차원 (예: 64, 128)</param>
        /// <param name="nHeads">Attention Head 개수 (dModel의 약수여야 함)</param>
        /// <param name="nLayers">Encoder Layer 개수</param>
        /// <param name="outputDim">출력 차원 (예: 1 - 다음 봉 종가 또는 등락 확률)</param>
        /// <param name="maxSeqLen">최대 시퀀스 길이</param>
        /// <param name="dropout">Dropout 비율</param>
        public TimeSeriesTransformer(int inputDim, int dModel, int nHeads, int nLayers, int outputDim, int maxSeqLen = 100, double dropout = 0.1)
            : base("TimeSeriesTransformer")
        {
            _dModel = dModel;

            // 1. Input Embedding: 입력 피처를 d_model 차원으로 투영
            _inputEmbedding = Linear(inputDim, dModel);

            // 2. Positional Encoding: 시계열 순서 정보 주입
            _positionalEncoding = new PositionalEncoding(dModel, maxSeqLen, dropout);

            // 3. Transformer Encoder
            // batch_first=false가 기본값이므로 forward에서 차원 변경 필요 (SeqLen, Batch, Feature)
            var encoderLayer = TransformerEncoderLayer(dModel, nHeads, dim_feedforward: dModel * 4, dropout: dropout);
            _transformerEncoder = TransformerEncoder(encoderLayer, nLayers);

            // 4. Output Layer
            _outputLayer = Linear(dModel, outputDim);

            RegisterComponents();
        }

        public override Tensor forward(Tensor input)
        {
            // input: [batch_size, seq_len, input_dim]

            // 1. Embedding
            using var embedded = _inputEmbedding.forward(input); // -> [batch_size, seq_len, d_model]
            
            // Scaling (Attention is All You Need 논문 참조)
            using var scaled = embedded * Math.Sqrt(_dModel);

            // 2. Positional Encoding
            using var withPe = _positionalEncoding.forward(scaled);

            // 3. Transformer Encoder
            // TorchSharp Transformer는 [seq_len, batch_size, feature] 입력을 기대함
            using var permuted1 = withPe.permute(1, 0, 2); // -> [seq_len, batch_size, d_model]
            
            using var encoded = _transformerEncoder.forward(permuted1, null, null);
            
            using var permuted2 = encoded.permute(1, 0, 2); // -> [batch_size, seq_len, d_model]

            // 4. Output
            // Many-to-One: 마지막 시점(t)의 hidden state만 사용하여 예측
            using var lastStep = permuted2.index(TensorIndex.Colon, TensorIndex.Single(-1), TensorIndex.Colon); // -> [batch_size, d_model]
            
            var output = _outputLayer.forward(lastStep); // -> [batch_size, output_dim]

            return output;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // Module의 Dispose가 자동으로 하위 레이어 정리
                        // 추가 정리가 필요한 리소스만 여기서 처리
                    }
                    catch { }
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Positional Encoding 모듈
    /// </summary>
    public class PositionalEncoding : Module<Tensor, Tensor>
    {
        private bool _peDisposed = false;
        private readonly Tensor _pe;
        private readonly Module<Tensor, Tensor> _dropout;

        public PositionalEncoding(int dModel, int maxLen, double dropout) : base("PositionalEncoding")
        {
            _dropout = Dropout(dropout);

            // PE 행렬 계산 (Sin/Cos)
            _pe = zeros(maxLen, dModel);
            var position = arange(0, maxLen, 1.0).unsqueeze(1);
            var divTerm = exp(arange(0, dModel, 2.0) * (-Math.Log(10000.0) / dModel));

            using (var sinTensor = sin(position * divTerm))
            using (var cosTensor = cos(position * divTerm))
            {
                for (int i = 0; i < maxLen; i++)
                {
                    for (int j = 0; j < dModel / 2; j++)
                    {
                        _pe[i, j * 2] = sinTensor[i, j];
                        _pe[i, j * 2 + 1] = cosTensor[i, j];
                    }
                }
            }

            _pe = _pe.unsqueeze(0); // [1, maxLen, dModel]
            
            // 학습 파라미터가 아니므로 buffer로 등록
            register_buffer("pe", _pe);
        }

        public override Tensor forward(Tensor x)
        {
            // x: [batch_size, seq_len, d_model]
            var seqLen = x.shape[1];
            
            // 입력 길이에 맞춰 PE 자르기 및 디바이스 이동
            using var peSlice = _pe.index(TensorIndex.Colon, TensorIndex.Slice(0, seqLen), TensorIndex.Colon);
            using var peOnDevice = peSlice.to(x.device);
            using var xWithPe = x + peOnDevice;

            return _dropout.forward(xWithPe);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_peDisposed && disposing)
            {
                try
                {
                    // _pe는 register_buffer로 등록되어 Module.Dispose가 자동 처리
                    // 추가 정리 불필요
                }
                catch { }
                _peDisposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
