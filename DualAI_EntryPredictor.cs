using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Services.AI;

namespace TradingBot.Services
{
    /// <summary>
    /// ?�??AI ?�상�??�측 ?�스??(ML.NET + Transformer)
    /// 15분봉 120�??�이?��? 기반?�로 진입???�측
    /// </summary>
    public class DualAI_EntryPredictor : IDisposable
    {
        private readonly AIPredictor _mlNetPredictor;
        private readonly TransformerTrainer? _transformerPredictor;
        private readonly FifteenMinCandleManager _candleManager;
        
        private readonly ConcurrentDictionary<string, EntrySignal> _latestSignals = new();
        private readonly SemaphoreSlim _predictionLock = new(1, 1);
        
        private bool _disposed = false;
        private CancellationTokenSource? _retrainCts;
        private Task? _retrainTask;

        // ?�정�?
        private readonly DualAIPredictorSettings _settings;

        public event Action<string, EntrySignal>? OnEntrySignalGenerated; // ?�볼, 진입 ?�호

        public DualAI_EntryPredictor(
            FifteenMinCandleManager candleManager,
            DualAIPredictorSettings? settings = null)
        {
            _candleManager = candleManager;
            _settings = settings ?? new DualAIPredictorSettings();

            // ML.NET ?�측�?초기??
            _mlNetPredictor = new AIPredictor();

            // Transformer ?�측�?초기??(TorchSharp ?�용 가????
            if (TorchInitializer.IsAvailable)
            {
                try
                {
                    _transformerPredictor = new TransformerTrainer(
                        inputDim: 11,
                        dModel: 64,
                        nHeads: 4,
                        nLayers: 3,
                        outputDim: 1,
                        seqLen: _settings.TransformerSeqLen,
                        modelPath: "transformer_entry_model.dat");

                    LoggerService.Info("[DualAI_EntryPredictor] Transformer ?�측�?초기???�료");
                }
                catch (Exception ex)
                {
                    LoggerService.Warning($"[DualAI_EntryPredictor] Transformer 초기???�패: {ex.Message}");
                }
            }
            else
            {
                LoggerService.Warning("[DualAI_EntryPredictor] TorchSharp ?�용 불�? - ML.NET�??�용");
            }

            // 15분봉 ?�데?�트 ?�벤??구독
            _candleManager.OnCandleBufferUpdated += OnCandleBufferUpdated;
        }

        /// <summary>
        /// ?�동 ?�학???�작 (백그?�운???�스??
        /// </summary>
        public void StartAutoRetrain()
        {
            if (_retrainTask != null)
            {
                LoggerService.Warning("[DualAI_EntryPredictor] 이미 재학습 태스크 실행 중");
                return;
            }

            _retrainCts = new CancellationTokenSource();
            _retrainTask = Task.Run(() => RetrainLoopAsync(_retrainCts.Token));
            LoggerService.Info($"[DualAI_EntryPredictor] 자동 재학습 시작 (주기: {_settings.RetrainIntervalMinutes}분)");
        }

        /// <summary>
        /// ?�동 ?�학??중�?
        /// </summary>
        public void StopAutoRetrain()
        {
            _retrainCts?.Cancel();
            _retrainTask?.Wait(5000);
            _retrainTask = null;
            LoggerService.Info("[DualAI_EntryPredictor] ?�동 ?�학??중�?");
        }

        /// <summary>
        /// 15분봉 버퍼 ?�데?�트 ???�동 ?�측 ?�행
        /// </summary>
        private async void OnCandleBufferUpdated(string symbol, List<CandleData> candles)
        {
            if (candles.Count < _settings.MinCandleCount)
            {
                LoggerService.Info($"[DualAI_EntryPredictor] {symbol} 캔들 부�?({candles.Count}/{_settings.MinCandleCount})");
                return;
            }

            try
            {
                var signal = await PredictEntrySignalAsync(symbol, candles);
                
                _latestSignals[symbol] = signal;
                OnEntrySignalGenerated?.Invoke(symbol, signal);

                // 강한 ?�호 발생 ??로그
                if (signal.SignalStrength >= _settings.StrongSignalThreshold)
                {
                    LoggerService.Info(
                        $"[DualAI_EntryPredictor] ?�� {symbol} 강한 진입 ?�호 감�?! " +
                        $"강도={signal.SignalStrength:F1} 방향={signal.Direction} " +
                        $"ML.NET={signal.MLNetProbability:P1} Transformer={signal.TransformerPredictedPrice:F2}");
                }
            }
            catch (Exception ex)
            {
                LoggerService.Error($"[DualAI_EntryPredictor] {symbol} ?�측 �??�류: {ex.Message}");
            }
        }

        /// <summary>
        /// 진입 ?�그???�측 (메인 로직)
        /// </summary>
        public async Task<EntrySignal> PredictEntrySignalAsync(string symbol, List<CandleData>? candles = null)
        {
            await _predictionLock.WaitAsync();
            try
            {
                // 캔들 ?�이??준�?
                candles ??= _candleManager.GetCandlesSnapshot(symbol);
                if (candles.Count < _settings.MinCandleCount)
                {
                    return EntrySignal.CreateNoSignal(symbol);
                }

                // 지??계산
                var enrichedCandles = await Task.Run(() => EnrichWithIndicators(candles));

                // 1. ML.NET ?�측
                var latestCandle = enrichedCandles.LastOrDefault();
                float mlNetProb = 0.5f;
                bool mlNetPrediction = false;

                if (latestCandle != null && _mlNetPredictor.IsModelLoaded)
                {
                    var result = _mlNetPredictor.Predict(latestCandle);
                    mlNetPrediction = result.Prediction;
                    mlNetProb = result.Probability;
                }

                // 2. Transformer ?�측
                float transformerPredictedPrice = (float)enrichedCandles.Last().Close;
                float transformerChangePercent = 0f;

                if (_transformerPredictor?.IsModelReady == true && enrichedCandles.Count >= _settings.TransformerSeqLen)
                {
                    try
                    {
                        var sequence = enrichedCandles.Skip(enrichedCandles.Count - _settings.TransformerSeqLen).ToList();
                        transformerPredictedPrice = _transformerPredictor.Predict(sequence);
                        
                        float currentPrice = (float)enrichedCandles.Last().Close;
                        transformerChangePercent = ((transformerPredictedPrice - currentPrice) / currentPrice) * 100f;
                    }
                    catch (Exception ex)
                    {
                        LoggerService.Warning($"[DualAI_EntryPredictor] {symbol} Transformer ?�측 ?�패: {ex.Message}");
                    }
                }

                // 3. ?�상�??�합 (가�??�균)
                float mlNetScore = mlNetPrediction ? mlNetProb : (1f - mlNetProb);
                float transformerScore = Math.Clamp(transformerChangePercent / 2f + 0.5f, 0f, 1f); // -2% ~ +2% -> 0~1
                
                float ensembleScore = 
                    (mlNetScore * _settings.MLNetWeight + transformerScore * _settings.TransformerWeight) /
                    (_settings.MLNetWeight + _settings.TransformerWeight);

                // 4. 진입 방향 결정
                EntryDirection direction = EntryDirection.Neutral;
                if (ensembleScore >= 0.55f) direction = EntryDirection.Long;
                else if (ensembleScore <= 0.45f) direction = EntryDirection.Short;

                // 5. ?�호 강도 계산 (0~100)
                float signalStrength = Math.Abs(ensembleScore - 0.5f) * 200f;

                return new EntrySignal
                {
                    Symbol = symbol,
                    Timestamp = DateTime.UtcNow,
                    Direction = direction,
                    SignalStrength = signalStrength,
                    MLNetProbability = mlNetProb,
                    MLNetPrediction = mlNetPrediction,
                    TransformerPredictedPrice = transformerPredictedPrice,
                    TransformerChangePercent = transformerChangePercent,
                    EnsembleScore = ensembleScore,
                    CurrentPrice = (float)enrichedCandles.Last().Close,
                    CandleCount = enrichedCandles.Count
                };
            }
            finally
            {
                _predictionLock.Release();
            }
        }

        /// <summary>
        /// 캔들 데이터에 지표 추가
        /// </summary>
        private List<CandleData> EnrichWithIndicators(List<CandleData> candles)
        {
            if (candles.Count < 20) return candles;

            var closes = candles.Select(c => (double)c.Close).ToList();

            var rsi = IndicatorCalculator.CalculateRSISeries(closes, 14);
            var (macd, signal, hist) = IndicatorCalculator.CalculateMACDSeries(closes);
            var atr = IndicatorCalculator.CalculateATRSeries(candles, 14);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < rsi.Count) candles[i].RSI = (float)rsi[i];
                if (i < macd.Count)
                {
                    candles[i].MACD = (float)macd[i];
                    candles[i].MACD_Signal = (float)signal[i];
                    candles[i].MACD_Hist = (float)hist[i];
                }
                if (i < atr.Count) candles[i].ATR = (float)atr[i];
            }

            return candles;
        }

        /// <summary>
        /// 주기???�학??루프
        /// </summary>
        private async Task RetrainLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_settings.RetrainIntervalMinutes), ct);

                    LoggerService.Info("[DualAI_EntryPredictor] ?�학???�작...");

                    var symbols = _candleManager.GetManagedSymbols();
                    foreach (var symbol in symbols)
                    {
                        if (ct.IsCancellationRequested) break;

                        var candles = _candleManager.GetCandlesSnapshot(symbol);
                        if (candles.Count < 100) continue;

                        await RetrainModelsAsync(candles, ct);
                    }

                    LoggerService.Info("[DualAI_EntryPredictor] ?�학???�료");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LoggerService.Error($"[DualAI_EntryPredictor] ?�학??�??�류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 모델 ?�학??
        /// </summary>
        private async Task RetrainModelsAsync(List<CandleData> candles, CancellationToken ct)
        {
            if (candles.Count < 100) return;

            var enrichedCandles = EnrichWithIndicators(candles);

            // Transformer 재학습
            if (_transformerPredictor?.IsModelReady == true)
            {
                try
                {
                    await Task.Run(() => _transformerPredictor.Train(enrichedCandles, epochs: 20), ct);
                    LoggerService.Info("[DualAI_EntryPredictor] Transformer 재학습 완료");
                }
                catch (Exception ex)
                {
                    LoggerService.Error($"[DualAI_EntryPredictor] Transformer ?�학???�패: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// ?�볼??최신 진입 ?�호 조회
        /// </summary>
        public EntrySignal? GetLatestSignal(string symbol)
        {
            return _latestSignals.TryGetValue(symbol, out var signal) ? signal : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopAutoRetrain();
            _retrainCts?.Dispose();
            
            _mlNetPredictor?.Dispose();
            _transformerPredictor?.Dispose();
            _predictionLock?.Dispose();
        }
    }

    /// <summary>
    /// 진입 ?�호 방향
    /// </summary>
    public enum EntryDirection
    {
        Neutral = 0,
        Long = 1,
        Short = -1
    }

    /// <summary>
    /// 진입 ?�호 결과
    /// </summary>
    public class EntrySignal
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public EntryDirection Direction { get; set; }
        public float SignalStrength { get; set; } // 0~100
        
        // ML.NET 결과
        public float MLNetProbability { get; set; }
        public bool MLNetPrediction { get; set; }
        
        // Transformer 결과
        public float TransformerPredictedPrice { get; set; }
        public float TransformerChangePercent { get; set; }
        
        // ?�상�?결과
        public float EnsembleScore { get; set; } // 0~1 (0.5=중립)
        
        // 부가 ?�보
        public float CurrentPrice { get; set; }
        public int CandleCount { get; set; }

        public static EntrySignal CreateNoSignal(string symbol)
        {
            return new EntrySignal
            {
                Symbol = symbol,
                Timestamp = DateTime.UtcNow,
                Direction = EntryDirection.Neutral,
                SignalStrength = 0f,
                EnsembleScore = 0.5f
            };
        }

        public override string ToString()
        {
            return $"[{Symbol}] {Direction} 강도={SignalStrength:F1} " +
                   $"?�상�?{EnsembleScore:P1} ML.NET={MLNetProbability:P1} " +
                   $"Transformer={TransformerChangePercent:+0.00;-0.00}%";
        }
    }
}
