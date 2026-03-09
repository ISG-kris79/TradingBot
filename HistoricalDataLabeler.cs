using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using Binance.Net.Interfaces;
using Binance.Net.Enums;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// 과거 6개월 차트 데이터를 분석하여 Transformer/ML.NET 학습용 정답지를 자동 생성
    /// - Transformer: Time-to-Target (캔들 개수) 라벨
    /// - ML.NET: 진입 성공 1/0 라벨
    /// </summary>
    public class HistoricalDataLabeler
    {
        private readonly IExchangeService _exchangeService;
        private readonly BacktestEntryLabeler _labeler;
        private readonly MultiTimeframeFeatureExtractor _featureExtractor;
        private readonly string _outputDirectory;

        public event Action<string>? OnLog;
        public event Action<string>? OnProgress;

        public HistoricalDataLabeler(
            IExchangeService exchangeService,
            string outputDirectory = "TrainingData/Historical")
        {
            _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
            _outputDirectory = outputDirectory;
            _labeler = new BacktestEntryLabeler();
            _featureExtractor = new MultiTimeframeFeatureExtractor(exchangeService);

            Directory.CreateDirectory(_outputDirectory);
        }

        /// <summary>
        /// 메인 실행: 심볼 목록에 대해 6개월 데이터 라벨링
        /// </summary>
        public async Task<LabelingSummary> LabelHistoricalDataAsync(
            List<string> symbols, 
            int monthsBack = 6,
            CancellationToken token = default)
        {
            var summary = new LabelingSummary { StartTime = DateTime.Now };
            OnLog?.Invoke($"[HistoricalLabeler] 시작: {symbols.Count}개 심볼, {monthsBack}개월 데이터");

            foreach (var symbol in symbols)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    OnProgress?.Invoke($"[{symbols.IndexOf(symbol) + 1}/{symbols.Count}] {symbol} 처리 중...");
                    var symbolResult = await ProcessSymbolAsync(symbol, monthsBack, token);
                    summary.SymbolResults.Add(symbolResult);
                    summary.TotalSamples += symbolResult.TotalSamples;
                    summary.ValidSamples += symbolResult.ValidTransformerLabels;

                    // API 부하 방지
                    await Task.Delay(500, token);
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[HistoricalLabeler] ❌ {symbol} 실패: {ex.Message}");
                    summary.ErrorCount++;
                }
            }

            summary.EndTime = DateTime.Now;
            summary.Duration = summary.EndTime - summary.StartTime;

            // 요약 저장
            SaveSummary(summary);
            PrintSummary(summary);

            return summary;
        }

        /// <summary>
        /// 단일 심볼 처리: 15분봉 데이터 수집 → 라벨링 → JSON 저장
        /// </summary>
        private async Task<SymbolLabelingResult> ProcessSymbolAsync(
            string symbol, 
            int monthsBack,
            CancellationToken token)
        {
            var result = new SymbolLabelingResult { Symbol = symbol };
            OnLog?.Invoke($"[{symbol}] 15분봉 데이터 수집 중... ({monthsBack}개월)");

            // 1. 15분봉 데이터 수집 (6개월 = 약 17,280개 캔들)
            var endTime = DateTime.UtcNow;
            var startTime = endTime.AddMonths(-monthsBack);
            var candles = await FetchAllCandlesAsync(symbol, KlineInterval.FifteenMinutes, startTime, endTime, token);

            if (candles == null || candles.Count < 100)
            {
                OnLog?.Invoke($"[{symbol}] ⚠️ 데이터 부족: {candles?.Count ?? 0}개 캔들");
                return result;
            }

            OnLog?.Invoke($"[{symbol}] ✅ {candles.Count}개 캔들 수집 완료");
            result.TotalSamples = candles.Count;

            // 2. 각 캔들 시점에 대해 라벨링
            var labeledFeatures = new List<LabeledFeature>();
            int processed = 0;
            int lastProgressPercent = 0;

            for (int i = 100; i < candles.Count - 35; i++) // 앞뒤 여유 공간 확보
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var currentCandle = candles[i];
                    var historicalCandles = candles.Take(i).ToList();
                    var futureCandles = candles.Skip(i + 1).Take(32).ToList();

                    // 2-1. Multi-Timeframe Feature 추출
                    var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(
                        symbol, 
                        currentCandle.OpenTime, 
                        token);

                    if (feature == null)
                        continue;

                    // 2-2. Transformer 라벨: Time-to-Target (캔들 개수)
                    float candlesToTarget = _labeler.CalculateCandlesToFibonacciTarget(
                        historicalCandles,
                        currentCandle.ClosePrice,
                        futureCandles,
                        maxLookAhead: 32,
                        tolerancePct: 0.5f);

                    feature.CandlesToTarget = candlesToTarget;

                    // 2-3. ML.NET 라벨: 진입 성공 여부 (0/1)
                    var (shouldEnter, profitPct, reason) = _labeler.EvaluateLongEntry(
                        futureCandles,
                        currentCandle.ClosePrice);

                    feature.ShouldEnter = shouldEnter;

                    // 2-4. 저장 (유효 라벨만)
                    if (candlesToTarget >= 0f || shouldEnter) // 최소 한 라벨이라도 유효하면
                    {
                        labeledFeatures.Add(new LabeledFeature
                        {
                            Feature = feature,
                            Timestamp = currentCandle.OpenTime,
                            CandlesToTarget = candlesToTarget,
                            MLShouldEnter = shouldEnter,
                            ActualProfitPct = profitPct,
                            MLReason = reason
                        });

                        if (candlesToTarget >= 0f)
                            result.ValidTransformerLabels++;
                        if (shouldEnter)
                            result.PositiveMLLabels++;
                    }

                    processed++;

                    // 진행률 로그 (10% 단위)
                    int progressPercent = (int)((float)processed / (candles.Count - 135) * 100);
                    if (progressPercent >= lastProgressPercent + 10)
                    {
                        lastProgressPercent = progressPercent;
                        OnProgress?.Invoke($"[{symbol}] {progressPercent}% 완료 ({processed}/{candles.Count - 135})");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[{symbol}] 캔들 {i} 라벨링 실패: {ex.Message}");
                }
            }

            result.ProcessedSamples = processed;

            // 3. JSON 파일 저장
            if (labeledFeatures.Count > 0)
            {
                string fileName = $"{symbol}_{startTime:yyyyMMdd}_{endTime:yyyyMMdd}.json";
                string filePath = Path.Combine(_outputDirectory, fileName);
                
                var json = JsonSerializer.Serialize(labeledFeatures, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                await File.WriteAllTextAsync(filePath, json, token);
                result.OutputFile = fileName;

                OnLog?.Invoke($"[{symbol}] ✅ {labeledFeatures.Count}개 샘플 저장: {fileName}");
            }

            return result;
        }

        /// <summary>
        /// 히스토리컬 캔들 데이터 수집
        /// [v2.4.2] 현재는 최근 1000개 캔들만 조회 (향후 API 확장 시 6개월 지원)
        /// </summary>
        private async Task<List<IBinanceKline>> FetchAllCandlesAsync(
            string symbol,
            KlineInterval interval,
            DateTime startTime,
            DateTime endTime,
            CancellationToken token)
        {
            try
            {
                // 이 메서드는 현재 기본 GetKlinesAsync만 사용 가능
                // TODO: IExchangeService에 startTime/endTime 파라미터가 추가되면 6개월 데이터 지원 가능
                OnLog?.Invoke($"[{symbol}] 캔들 데이터 조회 중... (최근 1000개)");
                var candles = await _exchangeService.GetKlinesAsync(
                    symbol,
                    interval,
                    limit: 1000,
                    token);

                if (candles != null && candles.Count > 0)
                {
                    OnLog?.Invoke($"[{symbol}] ✅ {candles.Count:N0}개 캔들 수집 완료");
                    return candles.OrderBy(c => c.OpenTime).ToList();
                }

                OnLog?.Invoke($"[{symbol}] ⚠️ 캔들 데이터 없음");
                return new List<IBinanceKline>();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[{symbol}] ❌ 캔들 조회 실패: {ex.Message}");
                return new List<IBinanceKline>();
            }
        }

        private void SaveSummary(LabelingSummary summary)
        {
            string summaryPath = Path.Combine(_outputDirectory, "labeling_summary.json");
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(summaryPath, json);
        }

        private void PrintSummary(LabelingSummary summary)
        {
            OnLog?.Invoke("\n" + new string('=', 60));
            OnLog?.Invoke("🏁 [Historical Labeling] 완료 요약");
            OnLog?.Invoke(new string('=', 60));
            OnLog?.Invoke($"⏱️  소요 시간: {summary.Duration.TotalMinutes:F1}분");
            OnLog?.Invoke($"📊 처리 심볼: {summary.SymbolResults.Count}개");
            OnLog?.Invoke($"📈 전체 샘플: {summary.TotalSamples:N0}개");
            OnLog?.Invoke($"✅ Transformer 유효 라벨: {summary.ValidSamples:N0}개 ({(float)summary.ValidSamples / summary.TotalSamples * 100:F1}%)");
            OnLog?.Invoke($"❌ 에러 발생: {summary.ErrorCount}건");
            OnLog?.Invoke($"📁 출력 디렉토리: {_outputDirectory}");
            OnLog?.Invoke(new string('=', 60) + "\n");
        }
    }

    #region Data Models

    public class LabeledFeature
    {
        public MultiTimeframeEntryFeature Feature { get; set; } = new();
        public DateTime Timestamp { get; set; }
        public float CandlesToTarget { get; set; }
        public bool MLShouldEnter { get; set; }
        public float ActualProfitPct { get; set; }
        public string MLReason { get; set; } = string.Empty;
    }

    public class SymbolLabelingResult
    {
        public string Symbol { get; set; } = string.Empty;
        public int TotalSamples { get; set; }
        public int ProcessedSamples { get; set; }
        public int ValidTransformerLabels { get; set; }
        public int PositiveMLLabels { get; set; }
        public string OutputFile { get; set; } = string.Empty;
    }

    public class LabelingSummary
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalSamples { get; set; }
        public int ValidSamples { get; set; }
        public int ErrorCount { get; set; }
        public List<SymbolLabelingResult> SymbolResults { get; set; } = new();
    }

    #endregion
}
