using Microsoft.ML;
using System.IO;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class AiInferenceService
    {
        private readonly MLContext _mlContext;
        private readonly PredictionEngine<CandleData, PredictionResult> _predictionEngine;
        private PredictionEngine<CandleData, PredictionResult> _predictionEngineInstance;
        private readonly string _modelPath;
        private FileSystemWatcher _watcher;
        private readonly object _lock = new object();

        public AiInferenceService()
        {
            _mlContext = new MLContext();
            // ML.NET 모델 파일 경로 (model.zip)
            _modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model.zip");

            LoadModel();
            InitializeWatcher();
        }

        private void InitializeWatcher()
        {
            var directory = Path.GetDirectoryName(_modelPath);
            if (string.IsNullOrEmpty(directory)) return;

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_modelPath));
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
            _watcher.Changed += OnModelChanged;
            _watcher.Created += OnModelChanged;
            _watcher.EnableRaisingEvents = true;
        }

        private void OnModelChanged(object sender, FileSystemEventArgs e)
        {
            // 파일 쓰기가 완료될 때까지 잠시 대기
            Thread.Sleep(1000);
            LoadModel();
        }

        private void LoadModel()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_modelPath))
                    {
                        // 파일 잠금 방지를 위해 FileShare.Read 사용
                        using (var stream = File.Open(_modelPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            ITransformer trainedModel = _mlContext.Model.Load(stream, out var modelInputSchema);
                            _predictionEngineInstance = _mlContext.Model.CreatePredictionEngine<CandleData, PredictionResult>(trainedModel);
                        }
                        System.Diagnostics.Debug.WriteLine($"✅ [ML.NET] 모델 로드 완료: {_modelPath}");
                        MainWindow.Instance?.AddLog("✅ AI 모델이 갱신되어 다시 로드되었습니다.");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("⚠️ [ML.NET] model.zip 파일을 찾을 수 없습니다.");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ [ML.NET] 모델 로드 실패: {ex.Message}");
                }
            }
        }

        public PredictionResult Predict(CandleData input)
        {
            lock (_lock)
            {
                if (_predictionEngineInstance == null)
                {
                    return new PredictionResult { Prediction = false, Score = 0.0f, Probability = 0.0f };
                }
                return _predictionEngineInstance.Predict(input);
            }
        }
    }
}