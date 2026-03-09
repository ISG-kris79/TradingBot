using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// 엘리엇 파동 AI 학습 및 통합 관리자
    /// MainWindow 시작 시 자동으로 데이터 수집 및 학습 실행
    /// </summary>
    public class WaveAIManager
    {
        private readonly DoubleCheckEntryEngine _waveEngine;
        private readonly WaveDatasetGenerator _dataGenerator;
        private readonly IExchangeService _exchangeService;

        private const string TransformerModelPath = "TrainingData/Models/transformer_wave_navigator.dat";
        private const string MLNetModelPath = "TrainingData/Models/mlnet_wave_sniper.zip";
        private const string DatasetCachePath = "TrainingData/WaveDatasets/cached_dataset.json";

        public bool IsReady => _waveEngine.IsReady;
        public DoubleCheckEntryEngine WaveEngine => _waveEngine;

        public WaveAIManager(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
            _waveEngine = new DoubleCheckEntryEngine(TransformerModelPath, MLNetModelPath);
            _dataGenerator = new WaveDatasetGenerator(exchangeService);
        }

        /// <summary>
        /// 시작 시 자동 실행: 모델 로드 또는 학습
        /// </summary>
        public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                MainWindow.Instance?.AddLog("[WaveAI] 🚀 초기화 시작...");

                // 1단계: 모델 파일 존재 여부 확인
                bool hasModels = CheckModelsExist();

                if (hasModels)
                {
                    MainWindow.Instance?.AddLog("[WaveAI] ✅ 기존 모델 발견 - 학습 건너뛰기");
                    return true;
                }

                MainWindow.Instance?.AddLog("[WaveAI] ⚠️ 모델 없음 - 자동 학습 시작");

                // 2단계: 학습 데이터 수집
                var (tfData, mlData) = await CollectTrainingDataAsync(cancellationToken);

                if (tfData.Count < 50 || mlData.Count < 50)
                {
                    MainWindow.Instance?.AddLog($"[WaveAI] ❌ 학습 데이터 부족 (TF={tfData.Count}, ML={mlData.Count}) - 최소 50개 필요");
                    return false;
                }

                MainWindow.Instance?.AddLog($"[WaveAI] 📊 데이터 수집 완료: TF={tfData.Count}개, ML={mlData.Count}개");

                // 3단계: 모델 학습
                bool success = await TrainModelsAsync(tfData, mlData, cancellationToken);

                if (success)
                {
                    MainWindow.Instance?.AddLog("[WaveAI] ✅ 학습 완료 - 시스템 활성화");
                }
                else
                {
                    MainWindow.Instance?.AddLog("[WaveAI] ❌ 학습 실패");
                }

                return success;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"[WaveAI] ❌ 초기화 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 모델 파일 존재 여부 확인
        /// </summary>
        private bool CheckModelsExist()
        {
            bool tfExists = File.Exists(TransformerModelPath);
            bool mlExists = File.Exists(MLNetModelPath);

            return tfExists && mlExists;
        }

        /// <summary>
        /// 학습 데이터 수집
        /// </summary>
        private async Task<(List<WaveTrainingData> tfData, List<MLNetWaveSniper.SniperInput> mlData)>
            CollectTrainingDataAsync(CancellationToken cancellationToken)
        {
            MainWindow.Instance?.AddLog("[WaveAI] 📥 역사적 데이터 수집 중...");

            // 주요 심볼 선택 (데이터 품질이 좋은 코인)
            var symbols = new List<string>
            {
                "BTCUSDT",
                "ETHUSDT",
                "BNBUSDT",
                "SOLUSDT",
                "XRPUSDT"
            };

            // 최근 15일간 데이터 수집 (15분봉 1500개 = 약 15.6일)
            var endDate = DateTime.UtcNow;
            var startDate = endDate.AddDays(-15);

            try
            {
                var result = await _dataGenerator.GenerateDatasetAsync(
                    symbols,
                    startDate,
                    endDate,
                    cancellationToken);

                // CSV 저장 (백업)
                _dataGenerator.SaveToCSV(result.Item1, result.Item2);

                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"[WaveAI] ❌ 데이터 수집 실패: {ex.Message}");
                return (new List<WaveTrainingData>(), new List<MLNetWaveSniper.SniperInput>());
            }
        }

        /// <summary>
        /// 모델 학습 실행
        /// </summary>
        private async Task<bool> TrainModelsAsync(
            List<WaveTrainingData> tfData,
            List<MLNetWaveSniper.SniperInput> mlData,
            CancellationToken cancellationToken)
        {
            try
            {
                // 병렬 학습
                MainWindow.Instance?.AddLog("[WaveAI] 🧠 AI 모델 학습 시작 (병렬 실행)...");

                var tfTask = Task.Run(async () =>
                {
                    MainWindow.Instance?.AddLog("[WaveAI] [Transformer] 학습 중...");
                    return await _waveEngine.TrainTransformerAsync(tfData, cancellationToken);
                }, cancellationToken);

                var mlTask = Task.Run(() =>
                {
                    MainWindow.Instance?.AddLog("[WaveAI] [ML.NET] 학습 중...");
                    return _waveEngine.TrainMLNet(mlData);
                }, cancellationToken);

                await Task.WhenAll(tfTask, mlTask);

                bool tfSuccess = await tfTask;
                bool mlSuccess = await mlTask;

                if (tfSuccess && mlSuccess)
                {
                    MainWindow.Instance?.AddLog("[WaveAI] ✅ 모든 모델 학습 완료");

                    // 모델 디렉토리 생성
                    var modelDir = Path.GetDirectoryName(TransformerModelPath);
                    if (modelDir != null && !Directory.Exists(modelDir))
                    {
                        Directory.CreateDirectory(modelDir);
                    }

                    return true;
                }
                else
                {
                    MainWindow.Instance?.AddLog($"[WaveAI] ⚠️ 일부 모델 학습 실패 (TF={tfSuccess}, ML={mlSuccess})");
                    return false;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"[WaveAI] ❌ 학습 중 오류: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 강제 재학습 (관리자용)
        /// </summary>
        public async Task<bool> ForceRetrainAsync(CancellationToken cancellationToken)
        {
            MainWindow.Instance?.AddLog("[WaveAI] 🔄 강제 재학습 시작...");

            // 기존 모델 삭제
            if (File.Exists(TransformerModelPath))
                File.Delete(TransformerModelPath);
            if (File.Exists(MLNetModelPath))
                File.Delete(MLNetModelPath);

            return await InitializeAsync(cancellationToken);
        }

        /// <summary>
        /// 모델 상태 정보 조회
        /// </summary>
        public string GetStatus()
        {
            bool tfReady = File.Exists(TransformerModelPath);
            bool mlReady = File.Exists(MLNetModelPath);
            bool engineReady = _waveEngine.IsReady;

            return $"Transformer: {(tfReady ? "✅" : "❌")} | ML.NET: {(mlReady ? "✅" : "❌")} | Engine: {(engineReady ? "✅" : "❌")}";
        }
    }
}
