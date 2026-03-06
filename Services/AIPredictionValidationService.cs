using Binance.Net.Clients;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// AI 예측 검증 서비스
    /// - 주기적으로 검증 대기 중인 예측을 조회
    /// - 실제 가격 변동과 비교하여 정확도 계산
    /// - ViewModel에 통계 업데이트 통지
    /// </summary>
    public class AIPredictionValidationService
    {
        private readonly DbManager _dbManager;
        private readonly BinanceExchangeService _exchangeService;
        private CancellationTokenSource? _cts;
        private Task? _validationTask;

        public event Action<string, double, int, int, double>? OnAccuracyUpdated; // ModelName, Accuracy, Total, Correct, AvgConfidence

        public AIPredictionValidationService(DbManager dbManager, BinanceExchangeService exchangeService)
        {
            _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
            _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        }

        /// <summary>
        /// 검증 루프 시작 (5분마다 실행)
        /// </summary>
        public void Start()
        {
            if (_validationTask != null && !_validationTask.IsCompleted)
            {
                MainWindow.Instance?.AddLog("⚠️ AI 검증 서비스가 이미 실행 중입니다");
                return;
            }

            _cts = new CancellationTokenSource();
            _validationTask = Task.Run(() => ValidationLoopAsync(_cts.Token), _cts.Token);
            MainWindow.Instance?.AddLog("🎯 AI 예측 검증 서비스 시작 (5분 주기)");
        }

        /// <summary>
        /// 검증 루프 중지
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            MainWindow.Instance?.AddLog("🛑 AI 예측 검증 서비스 중지");
        }

        private async Task ValidationLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ValidatePendingPredictionsAsync();
                    await Task.Delay(TimeSpan.FromMinutes(5), token); // 5분마다 실행
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.AddLog($"❌ [AI 검증 루프 오류] {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(30), token); // 오류 시 30초 대기
                }
            }
        }

        /// <summary>
        /// 검증 대기 중인 예측들을 확인하고 정확도 계산
        /// </summary>
        private async Task ValidatePendingPredictionsAsync()
        {
            try
            {
                var pendingPredictions = await _dbManager.GetPendingValidationsAsync();
                if (pendingPredictions.Count == 0)
                    return;

                MainWindow.Instance?.AddLog($"🔍 검증 대기 중인 예측 {pendingPredictions.Count}개 발견");

                foreach (var prediction in pendingPredictions)
                {
                    await ValidateSinglePredictionAsync(prediction);
                }

                // 검증 완료 후 전체 통계 업데이트
                await UpdateAllModelStatisticsAsync();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [예측 검증 실패] {ex.Message}");
            }
        }

        /// <summary>
        /// 단일 예측 검증
        /// </summary>
        private async Task ValidateSinglePredictionAsync(AIPrediction prediction)
        {
            try
            {
                // 현재 가격 조회
                var client = new BinanceRestClient();
                var tickerResult = await client.SpotApi.ExchangeData.GetTickerAsync(prediction.Symbol);
                
                if (!tickerResult.Success || tickerResult.Data == null)
                {
                    MainWindow.Instance?.AddLog($"⚠️ {prediction.Symbol} 가격 조회 실패 (검증 스킵)");
                    return;
                }

                decimal actualPrice = tickerResult.Data.LastPrice;
                
                // 가격 변동률 계산
                decimal priceChange = actualPrice - prediction.PriceAtPrediction;
                decimal changePercent = (priceChange / prediction.PriceAtPrediction) * 100;

                // 예측 정확도 판단 (방향 일치 여부)
                bool isCorrect = false;
                if (prediction.PredictedDirection == "UP" && changePercent > 0.1m)
                    isCorrect = true;
                else if (prediction.PredictedDirection == "DOWN" && changePercent < -0.1m)
                    isCorrect = true;

                // DB 업데이트
                await _dbManager.UpdatePredictionValidationAsync(prediction.Id, actualPrice, isCorrect);

                string result = isCorrect ? "✅ 정확" : "❌ 미적중";
                MainWindow.Instance?.AddLog($"{result} [{prediction.ModelName}] {prediction.Symbol}: {prediction.PredictedDirection} 예측, 실제 {changePercent:F2}%");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [{prediction.Symbol}] 검증 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 모든 모델의 통계를 재계산하고 ViewModel에 통지
        /// </summary>
        private async Task UpdateAllModelStatisticsAsync()
        {
            try
            {
                var stats = await _dbManager.GetModelAccuracyStatsAsync();

                foreach (var kvp in stats)
                {
                    string modelName = kvp.Key;
                    var (total, correct, accuracy, avgConf) = kvp.Value;

                    // ViewModel에 통지
                    OnAccuracyUpdated?.Invoke(modelName, accuracy, total, correct, avgConf);

                    MainWindow.Instance?.AddLog($"📊 [{modelName}] 정확도: {accuracy:F1}% ({correct}/{total}), 평균 신뢰도: {avgConf:F2}%");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [모델 통계 업데이트 실패] {ex.Message}");
            }
        }

        /// <summary>
        /// 즉시 모든 대기 중인 예측 검증 실행 (수동 트리거)
        /// </summary>
        public async Task ValidateNowAsync()
        {
            MainWindow.Instance?.AddLog("🔄 AI 예측 수동 검증 시작...");
            await ValidatePendingPredictionsAsync();
        }
    }
}
