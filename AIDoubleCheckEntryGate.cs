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
    /// AI 더블체크 진입 시스템
    /// ML.NET + Transformer 둘 다 승인해야 진입 허가
    /// + 실시간 데이터 수집 (지속적 학습용)
    /// </summary>
    public class AIDoubleCheckEntryGate
    {
        private readonly IExchangeService _exchangeService;
        private readonly EntryTimingMLTrainer _mlTrainer;
        private readonly EntryTimingTransformerTrainer _transformerTrainer;
        private readonly MultiTimeframeFeatureExtractor _featureExtractor;
        private readonly BacktestEntryLabeler _labeler;
        private readonly EntryRuleValidator _ruleValidator;
        private readonly ConcurrentDictionary<string, Queue<MultiTimeframeEntryFeature>> _recentFeatureBuffers
            = new(StringComparer.OrdinalIgnoreCase);
        
        // 실시간 데이터 수집
        private readonly ConcurrentQueue<EntryDecisionRecord> _pendingRecords = new();
        private readonly string _dataCollectionPath = "TrainingData/EntryDecisions";
        private readonly Timer? _dataFlushTimer;
        
        // 설정
        private readonly DoubleCheckConfig _config;
        
        // 로깅 이벤트
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;
        
        public bool IsReady => _mlTrainer.IsModelLoaded && _transformerTrainer.IsModelReady;

        public AIDoubleCheckEntryGate(
            IExchangeService exchangeService,
            DoubleCheckConfig? config = null)
        {
            _config = config ?? new DoubleCheckConfig();
            _exchangeService = exchangeService;
            
            _mlTrainer = new EntryTimingMLTrainer();
            _transformerTrainer = new EntryTimingTransformerTrainer();
            _featureExtractor = new MultiTimeframeFeatureExtractor(exchangeService);
            _labeler = new BacktestEntryLabeler();
            _ruleValidator = new EntryRuleValidator();

            // 모델 로드
            _mlTrainer.LoadModel();
            _transformerTrainer.LoadModel();

            // 데이터 수집 폴더 생성
            Directory.CreateDirectory(_dataCollectionPath);

            // 데이터 자동 저장 타이머 (5분마다)
            _dataFlushTimer = new Timer(_ => FlushCollectedData(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// 더블체크 진입 심사
        /// </summary>
        public async Task<(bool allowEntry, string reason, AIEntryDetail detail)> EvaluateEntryAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CancellationToken token = default)
        {
            if (!IsReady)
                return (false, "AI_Models_Not_Ready", new AIEntryDetail());

            try
            {
                // 1. Multi-Timeframe Feature 추출
                var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(symbol, DateTime.UtcNow, token);
                if (feature == null)
                    return (false, "Feature_Extraction_Failed", new AIEntryDetail());

                // 2. ML.NET 예측
                var mlPrediction = _mlTrainer.Predict(feature);
                if (mlPrediction == null)
                    return (false, "MLNET_Prediction_Failed", new AIEntryDetail());

                bool mlApprove = mlPrediction.ShouldEnter;
                float mlConfidence = mlPrediction.Probability;

                // 3. Transformer 예측 (심볼별 최근 시퀀스 버퍼 사용)
                var recentFeatures = BuildTransformerSequence(symbol, feature);
                var (tfApprove, tfConfidence) = _transformerTrainer.Predict(recentFeatures);

                // 4. 더블체크 판정
                bool mlPass = mlApprove && mlConfidence >= _config.MinMLConfidence;
                bool tfPass = tfApprove && tfConfidence >= _config.MinTransformerConfidence;
                
                var detail = new AIEntryDetail
                {
                    ML_Approve = mlApprove,
                    ML_Confidence = mlConfidence,
                    TF_Approve = tfApprove,
                    TF_Confidence = tfConfidence,
                    DoubleCheckPassed = mlPass && tfPass
                };

                // 5. 데이터 수집 (실시간 학습용)
                string decisionId = RecordEntryDecision(feature, mlPrediction, tfApprove, tfConfidence, detail.DoubleCheckPassed);
                detail.DecisionId = decisionId;

                if (!mlPass)
                    return (false, $"MLNET_Reject_Conf={mlConfidence:P1}", detail);

                if (!tfPass)
                    return (false, $"Transformer_Reject_Conf={tfConfidence:P1}", detail);

                // 6. **엘리엇 파동 + 피보나치 규칙 검증** (거부 필터)
                // AI가 승인해도 규칙 위배 시 진입 거부
                var m15Candles = await _exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes, 100, token);
                if (m15Candles != null)
                {
                    bool isLong = decision.Contains("LONG", StringComparison.OrdinalIgnoreCase);
                    var ruleCheck = _ruleValidator.ValidateEntryRules(
                        m15Candles.ToList(),
                        currentPrice,
                        isLong,
                        out var waveState,
                        out var fibLevels);

                    if (!ruleCheck.passed)
                    {
                        detail.DoubleCheckPassed = false;
                        return (false, $"Rule_Violation_{ruleCheck.reason}", detail);
                    }

                    // 규칙 통과 정보 상세에 추가 (옵션)
                    detail.ElliottValid = waveState.IsValid;
                    detail.FibInEntryZone = fibLevels.InEntryZone;
                }

                // 7. **최종 승인** (AI + 규칙 모두 통과)
                return (true, $"DoubleCheck_PASS_ML={mlConfidence:P1}_TF={tfConfidence:P1}", detail);
            }
            catch (Exception ex)
            {
                return (false, $"Exception_{ex.Message}", new AIEntryDetail());
            }
        }

        /// <summary>
        /// 메이저 코인 vs 펌핑 코인 별도 처리
        /// </summary>
        public async Task<(bool allowEntry, string reason, AIEntryDetail detail)> EvaluateEntryWithCoinTypeAsync(
            string symbol,
            string decision,
            decimal currentPrice,
            CoinType coinType,
            CancellationToken token = default)
        {
            var (allow, reason, detail) = await EvaluateEntryAsync(symbol, decision, currentPrice, token);

            if (!allow)
                return (allow, reason, detail);

            // 메이저 코인: 더 보수적 (높은 threshold)
            if (coinType == CoinType.Major)
            {
                if (detail.ML_Confidence < _config.MinMLConfidenceMajor ||
                    detail.TF_Confidence < _config.MinTransformerConfidenceMajor)
                {
                    return (false, $"Major_Threshold_Not_Met_ML={detail.ML_Confidence:P1}", detail);
                }
            }
            // 펌핑 코인: 별도 모델 (TODO: 펌핑 전용 모델 학습)
            else if (coinType == CoinType.Pumping)
            {
                // 펌핑 코인은 거래량 패턴이 다르므로 별도 모델 필요
                // 현재는 기본 모델 + 약간 낮은 threshold
                if (detail.ML_Confidence < _config.MinMLConfidencePumping)
                {
                    return (false, $"Pumping_Threshold_Not_Met_ML={detail.ML_Confidence:P1}", detail);
                }
            }

            return (allow, reason, detail);
        }

        /// <summary>
        /// 진입 결정 기록 (학습 데이터 수집)
        /// </summary>
        private string RecordEntryDecision(
            MultiTimeframeEntryFeature feature,
            EntryTimingPrediction mlPred,
            bool tfApprove,
            float tfConf,
            bool finalDecision)
        {
            string decisionId = Guid.NewGuid().ToString("N");

            var record = new EntryDecisionRecord
            {
                DecisionId = decisionId,
                Timestamp = DateTime.UtcNow,
                Symbol = feature.Symbol,
                EntryPrice = feature.EntryPrice,
                ML_Approve = mlPred.ShouldEnter,
                ML_Confidence = mlPred.Probability,
                TF_Approve = tfApprove,
                TF_Confidence = tfConf,
                FinalDecision = finalDecision,
                Feature = feature,
                // ActualProfit는 15분 후 별도 업데이트
                ActualProfitPct = null,
                Labeled = false
            };

            _pendingRecords.Enqueue(record);

            // 큐 오버플로우 방지
            if (_pendingRecords.Count > 10000)
            {
                FlushCollectedData();
            }

            return decisionId;
        }

        /// <summary>
        /// 수집된 데이터를 파일로 저장
        /// </summary>
        private void FlushCollectedData()
        {
            if (_pendingRecords.IsEmpty)
                return;

            try
            {
                var records = new List<EntryDecisionRecord>();
                while (_pendingRecords.TryDequeue(out var record))
                {
                    records.Add(record);
                }

                if (records.Count == 0)
                    return;

                string filename = $"EntryDecisions_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                string filepath = Path.Combine(_dataCollectionPath, filename);

                var json = JsonSerializer.Serialize(records, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(filepath, json);

                Console.WriteLine($"[AIDoubleCheck] 데이터 저장: {records.Count}개 레코드 → {filepath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 데이터 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 진입 후 실제 수익률 레이블링 (15분 후 호출)
        /// </summary>
        public async Task LabelActualProfitAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            bool isLong,
            string? decisionId = null,
            CancellationToken token = default)
        {
            try
            {
                decimal currentPrice = entryPrice;
                try
                {
                    currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
                }
                catch
                {
                    currentPrice = entryPrice;
                }

                float markToMarketPct = 0f;
                if (entryPrice > 0)
                {
                    markToMarketPct = isLong
                        ? (float)((currentPrice - entryPrice) / entryPrice * 100m)
                        : (float)((entryPrice - currentPrice) / entryPrice * 100m);
                }

                bool updated = await UpsertLabelInRecentFilesAsync(
                    symbol,
                    entryTime,
                    entryPrice,
                    markToMarketPct,
                    decisionId,
                    labelSource: "mark_to_market_15m",
                    overwriteExisting: false,
                    token: token);

                if (updated)
                {
                    Console.WriteLine($"[AIDoubleCheck] 15분 라벨 업데이트: {symbol} pnl={markToMarketPct:F2}%");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 레이블링 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 포지션 청산 확정 결과로 실제 수익률 라벨을 덮어씁니다.
        /// </summary>
        public async Task LabelActualProfitByCloseAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            float actualProfitPct,
            string? closeReason = null,
            string? decisionId = null,
            CancellationToken token = default)
        {
            try
            {
                bool updated = await UpsertLabelInRecentFilesAsync(
                    symbol,
                    entryTime,
                    entryPrice,
                    actualProfitPct,
                    decisionId,
                    labelSource: string.IsNullOrWhiteSpace(closeReason) ? "trade_close" : $"trade_close:{closeReason}",
                    overwriteExisting: true,
                    token: token);

                if (updated)
                {
                    Console.WriteLine($"[AIDoubleCheck] 청산 라벨 반영: {symbol} pnl={actualProfitPct:F2}%");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 청산 라벨 반영 실패: {ex.Message}");
            }
        }

        private async Task<bool> UpsertLabelInRecentFilesAsync(
            string symbol,
            DateTime entryTime,
            decimal entryPrice,
            float actualProfitPct,
            string? decisionId,
            string labelSource,
            bool overwriteExisting,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            string symbolUpper = symbol.ToUpperInvariant();
            DateTime entryUtc = entryTime.Kind switch
            {
                DateTimeKind.Utc => entryTime,
                DateTimeKind.Local => entryTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(entryTime, DateTimeKind.Local).ToUniversalTime()
            };

            var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json")
                .OrderByDescending(f => f)
                .Take(30);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                var json = await File.ReadAllTextAsync(file, token);
                var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);

                if (records == null || records.Count == 0)
                    continue;

                EntryDecisionRecord? target = null;

                if (!string.IsNullOrWhiteSpace(decisionId))
                {
                    target = records.FirstOrDefault(r =>
                        string.Equals(r.DecisionId, decisionId, StringComparison.OrdinalIgnoreCase)
                        && (overwriteExisting || !r.Labeled));
                }

                target ??= records
                    .Where(r => string.Equals(r.Symbol, symbolUpper, StringComparison.OrdinalIgnoreCase))
                    .Where(r => overwriteExisting || !r.Labeled)
                    .OrderBy(r =>
                    {
                        DateTime recordUtc = r.Timestamp.Kind == DateTimeKind.Utc
                            ? r.Timestamp
                            : DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc);

                        double timeDiff = Math.Abs((recordUtc - entryUtc).TotalMinutes);
                        double priceDiffPct = entryPrice > 0m
                            ? (double)(Math.Abs(r.EntryPrice - entryPrice) / entryPrice)
                            : 1.0;

                        return priceDiffPct * 100.0 + timeDiff * 0.01;
                    })
                    .FirstOrDefault();

                if (target == null)
                    continue;

                target.ActualProfitPct = actualProfitPct;
                target.Labeled = true;
                target.LabelSource = labelSource;
                target.LabeledAt = DateTime.UtcNow;

                var updatedJson = JsonSerializer.Serialize(records, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(file, updatedJson, token);
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            _dataFlushTimer?.Dispose();
            FlushCollectedData(); // 종료 시 마지막 저장
        }

        public (int total, int labeled, int markToMarket, int tradeClose) GetRecentLabelStats(int maxFiles = 10)
        {
            try
            {
                int safeMaxFiles = Math.Max(1, maxFiles);
                var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json")
                    .OrderByDescending(f => f)
                    .Take(safeMaxFiles);

                int total = 0;
                int labeled = 0;
                int markToMarket = 0;
                int tradeClose = 0;

                foreach (var file in files)
                {
                    var json = File.ReadAllText(file);
                    var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);
                    if (records == null)
                        continue;

                    total += records.Count;

                    foreach (var record in records)
                    {
                        if (!record.Labeled)
                            continue;

                        labeled++;

                        if (string.Equals(record.LabelSource, "mark_to_market_15m", StringComparison.OrdinalIgnoreCase))
                            markToMarket++;

                        if (!string.IsNullOrWhiteSpace(record.LabelSource)
                            && record.LabelSource.StartsWith("trade_close", StringComparison.OrdinalIgnoreCase))
                        {
                            tradeClose++;
                        }
                    }
                }

                return (total, labeled, markToMarket, tradeClose);
            }
            catch
            {
                return (0, 0, 0, 0);
            }
        }

        private List<MultiTimeframeEntryFeature> BuildTransformerSequence(string symbol, MultiTimeframeEntryFeature latestFeature)
        {
            string key = string.IsNullOrWhiteSpace(symbol) ? "UNKNOWN" : symbol;
            int requiredSeqLen = _transformerTrainer.SeqLen;

            var buffer = _recentFeatureBuffers.GetOrAdd(key, _ => new Queue<MultiTimeframeEntryFeature>(requiredSeqLen));

            lock (buffer)
            {
                buffer.Enqueue(latestFeature);
                while (buffer.Count > requiredSeqLen)
                {
                    buffer.Dequeue();
                }

                var sequence = buffer.ToList();
                if (sequence.Count == 0)
                {
                    sequence.Add(latestFeature);
                }

                // 워밍업 구간: 길이가 부족하면 가장 오래된 feature로 앞쪽 패딩
                if (sequence.Count < requiredSeqLen)
                {
                    var padFeature = sequence[0];
                    int padCount = requiredSeqLen - sequence.Count;
                    for (int i = 0; i < padCount; i++)
                    {
                        sequence.Insert(0, padFeature);
                    }
                }

                return sequence;
            }
        }

        /// <summary>
        /// 심볼 목록에 대해 AI 진입 확률과 미래 진입 ETA를 배치 스캔합니다.
        /// 현재 시장 상태를 기준으로 다음 15분 슬롯들의 시간 컨텍스트를 시뮬레이션하여
        /// "언제가 가장 유리한 진입 시점인지"를 반환합니다.
        /// </summary>
        public async Task<Dictionary<string, AIEntryForecastResult>> ScanEntryProbabilitiesAsync(
            List<string> symbols,
            CancellationToken token = default)
        {
            var results = new Dictionary<string, AIEntryForecastResult>(StringComparer.OrdinalIgnoreCase);

            if (!IsReady)
                return results;

            DateTime referenceUtc = DateTime.UtcNow;
            DateTime referenceLocal = referenceUtc.ToLocalTime();

            foreach (var symbol in symbols)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var forecast = await ForecastEntryTimingAsync(symbol, referenceUtc, referenceLocal, token);
                    if (forecast != null)
                        results[symbol] = forecast;
                }
                catch
                {
                    // 개별 심볼 실패 무시
                }

                await Task.Delay(100, token); // API 부하 방지
            }

            return results;
        }

        private async Task<AIEntryForecastResult?> ForecastEntryTimingAsync(
            string symbol,
            DateTime referenceUtc,
            DateTime referenceLocal,
            CancellationToken token)
        {
            var feature = await _featureExtractor.ExtractRealtimeFeatureAsync(symbol, referenceUtc, token);
            if (feature == null)
                return null;

            var baseSequence = BuildTransformerSequence(symbol, feature);
            var candidates = new List<AIEntryForecastResult>(_config.EntryForecastSteps + 1);

            var (currentMlProb, currentTfProb, currentAvgProb) = PredictEntryProbabilities(feature, baseSequence);
            candidates.Add(new AIEntryForecastResult
            {
                Symbol = symbol,
                MLProbability = currentMlProb,
                TFProbability = currentTfProb,
                AverageProbability = currentAvgProb,
                ForecastTimeUtc = referenceUtc,
                ForecastTimeLocal = referenceLocal,
                ForecastOffsetMinutes = 0,
                GeneratedAtLocal = referenceLocal
            });

            var nextQuarterUtc = AlignToNextQuarterHourUtc(referenceUtc);
            for (int step = 1; step <= _config.EntryForecastSteps; step++)
            {
                var forecastUtc = nextQuarterUtc.AddMinutes((step - 1) * 15);
                var shiftedFeature = feature.CloneWithTimestamp(forecastUtc);
                var shiftedSequence = ReplaceLastSequenceFeature(baseSequence, shiftedFeature);
                var (mlProb, tfProb, avgProb) = PredictEntryProbabilities(shiftedFeature, shiftedSequence);

                candidates.Add(new AIEntryForecastResult
                {
                    Symbol = symbol,
                    MLProbability = mlProb,
                    TFProbability = tfProb,
                    AverageProbability = avgProb,
                    ForecastTimeUtc = forecastUtc,
                    ForecastTimeLocal = forecastUtc.ToLocalTime(),
                    ForecastOffsetMinutes = Math.Max(1, (int)Math.Round((forecastUtc - referenceUtc).TotalMinutes)),
                    GeneratedAtLocal = referenceLocal
                });
            }

            var best = SelectBestForecast(candidates);

            // 관망 구간(저확률)일 때는 4시간까지 예측 범위를 확장해서 ETA를 제시
            if (best.AverageProbability < _config.EntryForecastWatchThreshold
                && _config.EntryForecastWatchSteps > _config.EntryForecastSteps)
            {
                for (int step = _config.EntryForecastSteps + 1; step <= _config.EntryForecastWatchSteps; step++)
                {
                    var forecastUtc = nextQuarterUtc.AddMinutes((step - 1) * 15);
                    var shiftedFeature = feature.CloneWithTimestamp(forecastUtc);
                    var shiftedSequence = ReplaceLastSequenceFeature(baseSequence, shiftedFeature);
                    var (mlProb, tfProb, avgProb) = PredictEntryProbabilities(shiftedFeature, shiftedSequence);

                    candidates.Add(new AIEntryForecastResult
                    {
                        Symbol = symbol,
                        MLProbability = mlProb,
                        TFProbability = tfProb,
                        AverageProbability = avgProb,
                        ForecastTimeUtc = forecastUtc,
                        ForecastTimeLocal = forecastUtc.ToLocalTime(),
                        ForecastOffsetMinutes = Math.Max(1, (int)Math.Round((forecastUtc - referenceUtc).TotalMinutes)),
                        GeneratedAtLocal = referenceLocal
                    });
                }

                best = SelectBestForecast(candidates);
            }

            return best;
        }

        private (float mlProb, float tfProb, float avgProb) PredictEntryProbabilities(
            MultiTimeframeEntryFeature feature,
            List<MultiTimeframeEntryFeature> transformerSequence)
        {
            float mlProb = 0f;
            var mlPrediction = _mlTrainer.Predict(feature);
            if (mlPrediction != null)
                mlProb = mlPrediction.Probability;

            var (_, tfProb) = _transformerTrainer.Predict(transformerSequence);
            float avgProb = (mlProb + tfProb) / 2f;

            return (mlProb, tfProb, avgProb);
        }

        private List<MultiTimeframeEntryFeature> ReplaceLastSequenceFeature(
            List<MultiTimeframeEntryFeature> baseSequence,
            MultiTimeframeEntryFeature latestFeature)
        {
            var sequence = new List<MultiTimeframeEntryFeature>(baseSequence.Count);
            for (int i = 0; i < baseSequence.Count; i++)
            {
                sequence.Add(i == baseSequence.Count - 1 ? latestFeature : baseSequence[i]);
            }

            return sequence;
        }

        private AIEntryForecastResult SelectBestForecast(List<AIEntryForecastResult> candidates)
        {
            if (candidates.Count == 0)
                return new AIEntryForecastResult();

            var current = candidates[0];
            var best = current;
            double bestScore = GetForecastScore(best);

            foreach (var candidate in candidates.Skip(1))
            {
                double score = GetForecastScore(candidate);
                if (score > bestScore ||
                    (Math.Abs(score - bestScore) < 0.0001 && candidate.ForecastOffsetMinutes < best.ForecastOffsetMinutes))
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (current.AverageProbability >= _config.EntryForecastImmediateThreshold &&
                current.AverageProbability + _config.EntryForecastImmediateTolerance >= best.AverageProbability)
            {
                return current;
            }

            return best;
        }

        private double GetForecastScore(AIEntryForecastResult candidate)
        {
            double stepPenalty = (candidate.ForecastOffsetMinutes / 15.0) * _config.EntryForecastTimePenaltyPerStep;
            return candidate.AverageProbability - stepPenalty;
        }

        private static DateTime AlignToNextQuarterHourUtc(DateTime utcTime)
        {
            int remainder = utcTime.Minute % 15;
            if (remainder == 0 && utcTime.Second == 0 && utcTime.Millisecond == 0)
                return utcTime;

            int addMinutes = remainder == 0 ? 15 : 15 - remainder;
            var truncated = new DateTime(utcTime.Year, utcTime.Month, utcTime.Day, utcTime.Hour, utcTime.Minute, 0, DateTimeKind.Utc);
            return truncated.AddMinutes(addMinutes);
        }

        /// <summary>
        /// 초기 학습 트리거 (모델 파일이 없을 때 기본 데이터로 빠르게 학습)
        /// </summary>
        public async Task<(bool success, string message)> TriggerInitialTrainingAsync(
            IExchangeService exchangeService,
            List<string> symbols,
            CancellationToken token = default)
        {
            if (IsReady)
                return (true, "모델이 이미 준비되었습니다.");

            try
            {
                OnAlert?.Invoke("🔄 [AI 학습] 초기 학습 시작: 히스토리컬 데이터 수집 중...");

                // 1. 히스토리컬 데이터로 대량 Feature 생성 (심볼당 수십 개)
                var trainingFeatures = new List<MultiTimeframeEntryFeature>();
                int maxSymbols = Math.Min(symbols.Count, 20);
                
                foreach (var symbol in symbols.Take(maxSymbols))
                {
                    try
                    {
                        OnLog?.Invoke($"  - {symbol} 히스토리컬 데이터 수집 중...");
                        
                        // 각 타임프레임 캔들 수집
                        var d1Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneDay, 50, token);
                        var h4Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.FourHour, 120, token);
                        var h2Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.TwoHour, 120, token);
                        var h1Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.OneHour, 200, token);
                        var m15Task = exchangeService.GetKlinesAsync(symbol, Binance.Net.Enums.KlineInterval.FifteenMinutes, 260, token);
                        
                        await Task.WhenAll(d1Task, h4Task, h2Task, h1Task, m15Task);
                        
                        var d1 = d1Task.Result?.ToList();
                        var h4 = h4Task.Result?.ToList();
                        var h2 = h2Task.Result?.ToList();
                        var h1 = h1Task.Result?.ToList();
                        var m15 = m15Task.Result?.ToList();
                        
                        if (d1 != null && d1.Count >= 20 &&
                            h4 != null && h4.Count >= 40 &&
                            h2 != null && h2.Count >= 40 &&
                            h1 != null && h1.Count >= 50 &&
                            m15 != null && m15.Count >= 100)
                        {
                            // 히스토리컬 Feature 대량 생성 (슬라이딩 윈도우)
                            var historicalFeatures = _featureExtractor.ExtractHistoricalFeatures(
                                symbol, d1, h4, h2, h1, m15, _labeler, isLongStrategy: true);
                            
                            trainingFeatures.AddRange(historicalFeatures);
                            OnLog?.Invoke($"  ✓ {symbol} 완료 ({historicalFeatures.Count}개 Feature, 총 {trainingFeatures.Count}개)");
                        }
                        else
                        {
                            OnLog?.Invoke($"  ⚠ {symbol} 데이터 부족 스킵");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"  ⚠ {symbol} 실패: {ex.Message}");
                    }
                    
                    await Task.Delay(300, token); // API 제한 방지
                }

                OnAlert?.Invoke($"📊 [AI 학습] 데이터 수집 완료: {trainingFeatures.Count}개 Feature");

                if (trainingFeatures.Count < 10)
                {
                    string errMsg = $"❌ [AI 학습] 히스토리컬 데이터 부족 ({trainingFeatures.Count}개) - 기존 WaveGate로 동작합니다";
                    OnAlert?.Invoke(errMsg);
                    return (false, errMsg);
                }

                // 2. ML.NET 모델 학습
                OnAlert?.Invoke($"🧠 [AI 학습] ML.NET 모델 학습 중... (샘플: {trainingFeatures.Count}개)");
                try
                {
                    var mlMetrics = await _mlTrainer.TrainAndSaveAsync(trainingFeatures);
                    OnLog?.Invoke($"[AIDoubleCheck] ML.NET 학습 완료 - Accuracy: {mlMetrics.Accuracy:P2}, F1: {mlMetrics.F1Score:P2}");
                }
                catch (Exception mlEx)
                {
                    OnAlert?.Invoke($"❌ [AI 학습] ML.NET 학습 실패: {mlEx.Message}");
                    OnLog?.Invoke($"[AIDoubleCheck] ML.NET 학습 상세 오류:\n{mlEx}");
                }

                // 3. Transformer 모델 학습 (빠른 초기화: 1 epoch)
                OnAlert?.Invoke("🧠 [AI 학습] Transformer 모델 학습 중... (1-2분 소요)");
                try
                {
                    _transformerTrainer.InitializeModel();
                    var tfMetrics = await _transformerTrainer.TrainAsync(trainingFeatures, epochs: 1, batchSize: 16);
                    _transformerTrainer.SaveModel();
                    OnLog?.Invoke($"[AIDoubleCheck] Transformer 학습 완료 - Accuracy: {tfMetrics.BestValidationAccuracy:P2}");
                }
                catch (Exception tfEx)
                {
                    OnAlert?.Invoke($"❌ [AI 학습] Transformer 학습 실패: {tfEx.Message}");
                    OnLog?.Invoke($"[AIDoubleCheck] Transformer 학습 상세 오류:\n{tfEx}");
                }

                // 4. 모델 리로드 및 상태 확인
                _mlTrainer.LoadModel();
                _transformerTrainer.LoadModel();

                if (IsReady)
                {
                    string msg = $"✅ [AI 학습] 초기 학습 완료! ML Ready: {_mlTrainer.IsModelLoaded}, TF Ready: {_transformerTrainer.IsModelReady}";
                    OnAlert?.Invoke(msg);
                    OnLog?.Invoke(msg);
                    return (true, msg);
                }
                else
                {
                    string errMsg = $"⚠️ [AI 학습] 학습 후 모델 상태 - ML: {(_mlTrainer.IsModelLoaded ? "OK" : "FAIL")}, TF: {(_transformerTrainer.IsModelReady ? "OK" : "FAIL")}";
                    OnAlert?.Invoke(errMsg);
                    return (false, errMsg);
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"❌ [AI 학습] 초기 학습 실패: {ex.Message}";
                OnAlert?.Invoke(errorMsg);
                OnLog?.Invoke($"[AIDoubleCheck] {errorMsg}");
                return (false, errorMsg);
            }
        }

        /// <summary>
        /// 정기 재학습 (수집된 라벨링 데이터 활용)
        /// </summary>
        public async Task<(bool success, string message)> RetrainModelsAsync(CancellationToken token = default)
        {
            try
            {
                // 1. 저장된 라벨링 데이터 로드
                var labeledData = LoadLabeledDataFromFiles();
                if (labeledData.Count < 100)
                {
                    return (false, $"재학습 데이터 부족 ({labeledData.Count}/100)");
                }

                OnAlert?.Invoke($"🔄 [AI 재학습] 시작: {labeledData.Count}개 라벨링 데이터");

                // 2. ML.NET 재학습
                var mlMetrics = await _mlTrainer.TrainAndSaveAsync(labeledData);
                _mlTrainer.LoadModel();

                // 3. Transformer 재학습 (2 epochs)
                var tfMetrics = await _transformerTrainer.TrainAsync(labeledData, epochs: 2, batchSize: 32);
                _transformerTrainer.SaveModel();
                _transformerTrainer.LoadModel();

                string msg = $"✅ [AI 재학습] 완료 - ML: {mlMetrics.Accuracy:P1}, TF: {tfMetrics.BestValidationAccuracy:P1} (샘플: {labeledData.Count}개)";
                OnAlert?.Invoke(msg);
                OnLog?.Invoke(msg);
                return (true, msg);
            }
            catch (Exception ex)
            {
                string errorMsg = $"❌ [AI 재학습] 실패: {ex.Message}";
                OnAlert?.Invoke(errorMsg);
                OnLog?.Invoke($"[AIDoubleCheck] {errorMsg}");
                return (false, errorMsg);
            }
        }

        private List<MultiTimeframeEntryFeature> LoadLabeledDataFromFiles()
        {
            var result = new List<MultiTimeframeEntryFeature>();

            try
            {
                if (!Directory.Exists(_dataCollectionPath))
                    return result;

                var files = Directory.GetFiles(_dataCollectionPath, "EntryDecisions_*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var records = JsonSerializer.Deserialize<List<EntryDecisionRecord>>(json);
                        if (records != null)
                        {
                            foreach (var record in records.Where(r => r.Labeled && r.Feature != null))
                            {
                                result.Add(record.Feature!);
                            }
                        }
                    }
                    catch
                    {
                        // 개별 파일 오류 무시
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIDoubleCheck] 라벨링 데이터 로드 실패: {ex.Message}");
            }

            return result;
        }
    }
    /// 더블체크 설정
    /// </summary>
    public class DoubleCheckConfig
    {
        public float MinMLConfidence { get; set; } = 0.50f;
        public float MinTransformerConfidence { get; set; } = 0.45f;
        public float MinMLConfidenceMajor { get; set; } = 0.60f; // 메이저 코인은 더 보수적
        public float MinTransformerConfidenceMajor { get; set; } = 0.55f;
        public float MinMLConfidencePumping { get; set; } = 0.48f; // 펌핑 코인은 약간 완화
        public int EntryForecastSteps { get; set; } = 8; // 다음 2시간(15분 x 8)
        public int EntryForecastWatchSteps { get; set; } = 16; // 관망 시 4시간(15분 x 16)
        public float EntryForecastImmediateThreshold { get; set; } = 0.62f;
        public float EntryForecastImmediateTolerance { get; set; } = 0.03f;
        public float EntryForecastWatchThreshold { get; set; } = 0.35f;
        public float EntryForecastMinCandidateProbability { get; set; } = 0.35f;
        public float EntryForecastTimePenaltyPerStep { get; set; } = 0.01f;
    }

    /// <summary>
    /// AI 진입 평가 상세
    /// </summary>
    public class AIEntryDetail
    {
        public string DecisionId { get; set; } = string.Empty;
        public bool ML_Approve { get; set; }
        public float ML_Confidence { get; set; }
        public bool TF_Approve { get; set; }
        public float TF_Confidence { get; set; }
        public bool DoubleCheckPassed { get; set; }
        
        // 엘리엇 파동 & 피보나치 규칙 검증 결과
        public bool ElliottValid { get; set; }
        public bool FibInEntryZone { get; set; }
    }

    /// <summary>
    /// 진입 결정 기록 (학습 데이터)
    /// </summary>
    public class EntryDecisionRecord
    {
        public string DecisionId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public bool ML_Approve { get; set; }
        public float ML_Confidence { get; set; }
        public bool TF_Approve { get; set; }
        public float TF_Confidence { get; set; }
        public bool FinalDecision { get; set; }
        public MultiTimeframeEntryFeature? Feature { get; set; }
        public float? ActualProfitPct { get; set; } // 15분 후 업데이트
        public bool Labeled { get; set; }
        public string? LabelSource { get; set; }
        public DateTime? LabeledAt { get; set; }
    }

    public enum CoinType
    {
        Major,      // BTC, ETH, SOL, XRP
        Pumping,    // 급등주 (STEEM, MANTRA 등)
        Normal      // 일반 알트코인
    }
}
