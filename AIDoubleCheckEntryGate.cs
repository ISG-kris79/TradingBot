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
    }

    /// <summary>
    /// 더블체크 설정
    /// </summary>
    public class DoubleCheckConfig
    {
        public float MinMLConfidence { get; set; } = 0.65f;
        public float MinTransformerConfidence { get; set; } = 0.60f;
        public float MinMLConfidenceMajor { get; set; } = 0.75f; // 메이저 코인은 더 보수적
        public float MinTransformerConfidenceMajor { get; set; } = 0.70f;
        public float MinMLConfidencePumping { get; set; } = 0.58f; // 펌핑 코인은 약간 완화
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
