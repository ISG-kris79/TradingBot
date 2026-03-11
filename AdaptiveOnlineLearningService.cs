using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// 적응형 온라인 학습 서비스
    /// - 슬라이딩 윈도우로 최근 데이터 유지
    /// - 주기적 재학습 (1시간마다 또는 데이터 N건 도달 시)
    /// - Concept Drift 감지 (성능 저하 시 긴급 재학습)
    /// - 적응형 Confidence Threshold (최근 승률 기반 자동 조정)
    /// </summary>
    public class AdaptiveOnlineLearningService : IDisposable
    {
        private readonly EntryTimingMLTrainer _mlTrainer;
        private readonly TensorFlowEntryTimingTrainer _transformerTrainer;
        
        // 슬라이딩 윈도우 버퍼 (최근 N건)
        private readonly ConcurrentQueue<MultiTimeframeEntryFeature> _slidingWindow = new();
        private readonly int _maxWindowSize;
        private readonly int _minTrainingSamples;
        
        // 성능 모니터링
        private readonly Queue<bool> _recentPredictions = new(20); // 최근 20건 성능 추적
        private double _currentAccuracy = 0.70; // 초기 기대 정확도
        private double _baselineAccuracy = 0.70; // 기준 정확도 (Drift 감지용)
        
        // 적응형 Threshold
        private float _currentMLThreshold = 0.65f;
        private float _currentTFThreshold = 0.60f;
        private readonly float _minThreshold = 0.50f;
        private readonly float _maxThreshold = 0.85f;
        
        // 학습 스케줄
        private readonly Timer? _retrainingTimer;
        private readonly TimeSpan _retrainingInterval;
        private DateTime _lastTrainingTime = DateTime.UtcNow;
        private bool _isTraining = false;
        
        // 설정
        private readonly OnlineLearningConfig _config;
        
        // 이벤트
        public event Action<string>? OnLog;
        public event Action<string, double, float, float>? OnPerformanceUpdate; // stage, accuracy, mlThresh, tfThresh
        
        public double CurrentAccuracy => _currentAccuracy;
        public float CurrentMLThreshold => _currentMLThreshold;
        public float CurrentTFThreshold => _currentTFThreshold;
        public int WindowSize => _slidingWindow.Count;

        public AdaptiveOnlineLearningService(
            EntryTimingMLTrainer mlTrainer,
            TensorFlowEntryTimingTrainer transformerTrainer,
            OnlineLearningConfig? config = null)
        {
            _mlTrainer = mlTrainer ?? throw new ArgumentNullException(nameof(mlTrainer));
            _transformerTrainer = transformerTrainer ?? throw new ArgumentNullException(nameof(transformerTrainer));
            _config = config ?? new OnlineLearningConfig();
            
            _maxWindowSize = _config.SlidingWindowSize;
            _minTrainingSamples = _config.MinSamplesForTraining;
            _retrainingInterval = TimeSpan.FromHours(_config.RetrainingIntervalHours);
            
            // 주기적 재학습 타이머
            if (_config.EnablePeriodicRetraining)
            {
                _retrainingTimer = new Timer(
                    _ => _ = PeriodicRetrainingCallback(),
                    null,
                    _retrainingInterval,
                    _retrainingInterval);
            }
            
            OnLog?.Invoke($"[OnlineLearning] 초기화 완료: 윈도우={_maxWindowSize}, 재학습주기={_config.RetrainingIntervalHours}시간");
        }

        /// <summary>
        /// 새로운 라벨링된 샘플 추가
        /// </summary>
        public async Task AddLabeledSampleAsync(MultiTimeframeEntryFeature sample)
        {
            if (sample == null)
                return;

            _slidingWindow.Enqueue(sample);

            // 윈도우 크기 제한 (오래된 샘플 제거)
            while (_slidingWindow.Count > _maxWindowSize)
            {
                _slidingWindow.TryDequeue(out _);
            }

            // 성능 추적 (최근 20건)
            TrackPredictionAccuracy(sample);

            // Concept Drift 감지
            if (_config.EnableConceptDriftDetection)
            {
                await DetectAndReactToDrift();
            }

            // 트리거 조건 1: 일정 샘플 수 도달
            if (_slidingWindow.Count >= _minTrainingSamples &&
                _slidingWindow.Count % _config.TriggerEveryNSamples == 0)
            {
                OnLog?.Invoke($"[OnlineLearning] 트리거: {_slidingWindow.Count}건 도달 → 재학습 시작");
                await RetrainModelsAsync("샘플 수 도달");
            }
        }

        /// <summary>
        /// 주기적 재학습 콜백 (타이머)
        /// </summary>
        private async Task PeriodicRetrainingCallback()
        {
            if (_isTraining)
            {
                OnLog?.Invoke("[OnlineLearning] 학습 중복 방지: 기존 학습 진행 중");
                return;
            }

            var elapsed = DateTime.UtcNow - _lastTrainingTime;
            if (elapsed < _retrainingInterval)
                return;

            OnLog?.Invoke($"[OnlineLearning] 주기적 재학습 트리거: {elapsed.TotalHours:F1}시간 경과");
            await RetrainModelsAsync("주기적 재학습");
        }

        /// <summary>
        /// 모델 재학습 (ML.NET + Transformer)
        /// </summary>
        private async Task RetrainModelsAsync(string reason)
        {
            if (_isTraining)
            {
                OnLog?.Invoke("[OnlineLearning] 학습 중복 방지");
                return;
            }

            if (_slidingWindow.Count < _minTrainingSamples)
            {
                OnLog?.Invoke($"[OnlineLearning] 학습 데이터 부족: {_slidingWindow.Count}/{_minTrainingSamples}");
                return;
            }

            _isTraining = true;
            _lastTrainingTime = DateTime.UtcNow;

            try
            {
                OnLog?.Invoke($"🔄 [OnlineLearning] 재학습 시작: {reason} | 샘플 수={_slidingWindow.Count}");

                // 슬라이딩 윈도우 복사 (학습 중 데이터 변경 방지)
                var trainingData = _slidingWindow.ToList();

                // 1. ML.NET 재학습
                var mlMetrics = await _mlTrainer.TrainAndSaveAsync(trainingData);
                OnLog?.Invoke($"✅ [OnlineLearning] ML.NET 재학습 완료: 정확도={mlMetrics.Accuracy:P2}, F1={mlMetrics.F1Score:F3}");

                // 2. Transformer 재학습 (에포크 수 줄임 - 빠른 적응)
                var tfMetrics = await _transformerTrainer.TrainAsync(
                    trainingData,
                    _config.TransformerFastEpochs,
                    32,
                    0.001f);
                OnLog?.Invoke($"✅ [OnlineLearning] Transformer 재학습 완료: Loss={tfMetrics.BestValidationLoss:F4}");

                // 3. 성능 업데이트
                _currentAccuracy = mlMetrics.Accuracy;
                _baselineAccuracy = Math.Max(_baselineAccuracy * 0.95, mlMetrics.Accuracy); // 서서히 기준선 갱신

                // 4. Threshold 자동 조정 (Transformer의 경우 Loss 기반 정확도 추정: 1 - normalized loss)
                double estimatedTFAccuracy = Math.Max(0.5, 1.0 - Math.Min(tfMetrics.BestValidationLoss, 0.5));
                AdjustThresholds(mlMetrics.Accuracy, estimatedTFAccuracy);

                OnPerformanceUpdate?.Invoke(reason, _currentAccuracy, _currentMLThreshold, _currentTFThreshold);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [OnlineLearning] 재학습 실패: {ex.Message}");
            }
            finally
            {
                _isTraining = false;
            }
        }

        /// <summary>
        /// Concept Drift 감지 및 대응
        /// </summary>
        private async Task DetectAndReactToDrift()
        {
            if (_recentPredictions.Count < 20)
                return; // 충분한 샘플 필요

            // 최근 20건 정확도 계산
            double recentAccuracy = _recentPredictions.Count(correct => correct) / (double)_recentPredictions.Count;

            // Drift 감지: 최근 정확도가 기준선보다 10%p 이상 하락
            double driftThreshold = _baselineAccuracy - 0.10;
            if (recentAccuracy < driftThreshold)
            {
                OnLog?.Invoke($"⚠️ [OnlineLearning] Concept Drift 감지! 최근={recentAccuracy:P2}, 기준={_baselineAccuracy:P2} → 긴급 재학습");
                await RetrainModelsAsync("Concept Drift 감지");
            }
        }

        /// <summary>
        /// 예측 정확도 추적 (최근 20건)
        /// </summary>
        private void TrackPredictionAccuracy(MultiTimeframeEntryFeature sample)
        {
            // ML.NET 예측과 실제 라벨 비교
            var prediction = _mlTrainer.Predict(sample);
            if (prediction == null)
                return;

            bool correct = prediction.ShouldEnter == sample.ShouldEnter;

            _recentPredictions.Enqueue(correct);
            while (_recentPredictions.Count > 20)
            {
                _recentPredictions.Dequeue();
            }
        }

        /// <summary>
        /// 적응형 Confidence Threshold 조정
        /// 정확도 높으면 threshold 올리고 (선별적 진입)
        /// 정확도 낮으면 threshold 내리고 (기회 확대)
        /// </summary>
        private void AdjustThresholds(double mlAccuracy, double tfAccuracy)
        {
            // ML.NET Threshold 조정
            if (mlAccuracy >= 0.75)
            {
                // 높은 정확도 → threshold 상향 (품질 우선)
                _currentMLThreshold = Math.Min(_currentMLThreshold + 0.02f, _maxThreshold);
            }
            else if (mlAccuracy < 0.65)
            {
                // 낮은 정확도 → threshold 하향 (기회 확대)
                _currentMLThreshold = Math.Max(_currentMLThreshold - 0.03f, _minThreshold);
            }

            // Transformer Threshold 조정
            if (tfAccuracy >= 0.72)
            {
                _currentTFThreshold = Math.Min(_currentTFThreshold + 0.02f, _maxThreshold);
            }
            else if (tfAccuracy < 0.60)
            {
                _currentTFThreshold = Math.Max(_currentTFThreshold - 0.03f, _minThreshold);
            }

            OnLog?.Invoke($"🎚️ [OnlineLearning] Threshold 조정: ML={_currentMLThreshold:P0} (정확도={mlAccuracy:P2}), TF={_currentTFThreshold:P0} (정확도={tfAccuracy:P2})");
        }

        /// <summary>
        /// 저장된 데이터에서 초기 윈도우 로드 (서버 재시작 시)
        /// </summary>
        public async Task LoadInitialWindowAsync(string dataPath)
        {
            try
            {
                if (!Directory.Exists(dataPath))
                    return;

                var files = Directory.GetFiles(dataPath, "*.json")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .Take(10); // 최근 10개 파일

                int loadedCount = 0;
                foreach (var file in files)
                {
                    var json = await File.ReadAllTextAsync(file);
                    var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);
                    
                    if (records != null)
                    {
                        foreach (var record in records.Where(r => r.Labeled && r.Feature != null))
                        {
                            await AddLabeledSampleAsync(record.Feature!);
                            loadedCount++;
                            
                            if (_slidingWindow.Count >= _maxWindowSize)
                                break;
                        }
                    }
                    
                    if (_slidingWindow.Count >= _maxWindowSize)
                        break;
                }

                OnLog?.Invoke($"[OnlineLearning] 초기 윈도우 로드 완료: {loadedCount}건");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OnlineLearning] 초기 윈도우 로드 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 현재 상태 저장 (체크포인트)
        /// </summary>
        public async Task SaveCheckpointAsync(string path)
        {
            try
            {
                var checkpoint = new OnlineLearningCheckpoint
                {
                    Timestamp = DateTime.UtcNow,
                    WindowSize = _slidingWindow.Count,
                    CurrentAccuracy = _currentAccuracy,
                    BaselineAccuracy = _baselineAccuracy,
                    CurrentMLThreshold = _currentMLThreshold,
                    CurrentTFThreshold = _currentTFThreshold,
                    LastTrainingTime = _lastTrainingTime,
                    RecentPredictions = _recentPredictions.ToList()
                };

                var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
                
                OnLog?.Invoke($"[OnlineLearning] 체크포인트 저장: {path}");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OnlineLearning] 체크포인트 저장 실패: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _retrainingTimer?.Dispose();
        }
    }

    /// <summary>
    /// 온라인 학습 설정
    /// </summary>
    public class OnlineLearningConfig
    {
        /// <summary>슬라이딩 윈도우 최대 크기 (최근 N건 유지)</summary>
        public int SlidingWindowSize { get; set; } = 1000;

        /// <summary>최소 학습 샘플 수 (이 이하면 학습 안 함)</summary>
        public int MinSamplesForTraining { get; set; } = 200;

        /// <summary>N건마다 자동 재학습 트리거</summary>
        public int TriggerEveryNSamples { get; set; } = 100;

        /// <summary>주기적 재학습 간격 (시간)</summary>
        public double RetrainingIntervalHours { get; set; } = 1.0;

        /// <summary>주기적 재학습 활성화</summary>
        public bool EnablePeriodicRetraining { get; set; } = true;

        /// <summary>Concept Drift 감지 활성화</summary>
        public bool EnableConceptDriftDetection { get; set; } = true;

        /// <summary>Transformer 빠른 학습 에포크 (온라인 학습용)</summary>
        public int TransformerFastEpochs { get; set; } = 5;
    }

    /// <summary>
    /// 온라인 학습 체크포인트 (상태 저장)
    /// </summary>
    public class OnlineLearningCheckpoint
    {
        public DateTime Timestamp { get; set; }
        public int WindowSize { get; set; }
        public double CurrentAccuracy { get; set; }
        public double BaselineAccuracy { get; set; }
        public float CurrentMLThreshold { get; set; }
        public float CurrentTFThreshold { get; set; }
        public DateTime LastTrainingTime { get; set; }
        public List<bool> RecentPredictions { get; set; } = new();
    }
}
