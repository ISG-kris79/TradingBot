using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Services;
using static TradingBot.Services.UnifiedLogger;

namespace TradingBot.AI
{
    /// <summary>
    /// 코인별 전문가 온라인 학습 시스템
    /// BTC/ETH/SOL/XRP 메이저 + ALT 분리 학습
    /// </summary>
    public class CoinSpecialistLearningSystem : IDisposable
    {
        // 코인별 전문가 슬라이딩 윈도우
        private readonly Dictionary<CoinGroup, AdaptiveOnlineLearningService> _specialists = new();
        private readonly Dictionary<string, CoinGroup> _symbolToGroup = new();
        
        // 메타 러닝: 신규 코인 빠른 적응
        private readonly ConcurrentQueue<MultiTimeframeEntryFeature> _globalMetaBuffer = new(new List<MultiTimeframeEntryFeature>());
        
        private readonly EntryTimingMLTrainer _metaTrainer;
        private readonly EntryTimingTransformerTrainer _metaTransformer;
        private readonly IExchangeService _exchangeService;
        
        public enum CoinGroup
        {
            BTC,      // 비트코인 (변동성 낮음, 추세 추종)
            ETH,      // 이더리움 (BTC 상관 0.9, 리드/래그)
            SOL,      // 솔라나 (변동성 높음, 급등락)
            XRP,      // 리플 (규제 민감, 횡보장 특화)
            MAJOR,    // 기타 메이저 (BNB, ADA 등)
            ALT,      // 알트코인 (펌핑 패턴)
            STABLE    // 스테이블 (USDT 페어 소액 코인)
        }
        
        public CoinSpecialistLearningSystem(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
            _metaTrainer = new EntryTimingMLTrainer("MetaLearning_Model.zip");
            _metaTransformer = new EntryTimingTransformerTrainer("MetaLearning_Transformer.pt");
            
            InitializeSpecialists();
            InitializeSymbolMapping();
            
            Info(LogCategory.AI, "[CoinSpecialist] 초기화 완료");
        }
        
        private void InitializeSpecialists()
        {
            // BTC 전문가: 추세 추종 특화
            var btcTrainer = new EntryTimingMLTrainer("BTC_Specialist.zip");
            var btcTransformer = new EntryTimingTransformerTrainer("BTC_Transformer.pt");
            _specialists[CoinGroup.BTC] = new AdaptiveOnlineLearningService(
                btcTrainer, 
                btcTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 800,  // BTC는 더 많은 히스토리
                    MinSamplesForTraining = 150,
                    TriggerEveryNSamples = 80,
                    RetrainingIntervalHours = 2.0,  // BTC는 더 긴 주기
                    TransformerFastEpochs = 3
                });
            
            // ETH 전문가: BTC 상관 학습
            var ethTrainer = new EntryTimingMLTrainer("ETH_Specialist.zip");
            var ethTransformer = new EntryTimingTransformerTrainer("ETH_Transformer.pt");
            _specialists[CoinGroup.ETH] = new AdaptiveOnlineLearningService(
                ethTrainer,
                ethTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 700,
                    MinSamplesForTraining = 140,
                    TriggerEveryNSamples = 70,
                    RetrainingIntervalHours = 1.5,
                    TransformerFastEpochs = 4
                });
            
            // SOL 전문가: 고변동성 대응
            var solTrainer = new EntryTimingMLTrainer("SOL_Specialist.zip");
            var solTransformer = new EntryTimingTransformerTrainer("SOL_Transformer.pt");
            _specialists[CoinGroup.SOL] = new AdaptiveOnlineLearningService(
                solTrainer,
                solTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 500,  // 빠른 적응
                    MinSamplesForTraining = 100,
                    TriggerEveryNSamples = 50,
                    RetrainingIntervalHours = 0.5,  // 30분마다
                    TransformerFastEpochs = 5
                });
            
            // XRP 전문가: 횡보장 특화
            var xrpTrainer = new EntryTimingMLTrainer("XRP_Specialist.zip");
            var xrpTransformer = new EntryTimingTransformerTrainer("XRP_Transformer.pt");
            _specialists[CoinGroup.XRP] = new AdaptiveOnlineLearningService(
                xrpTrainer,
                xrpTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 600,
                    MinSamplesForTraining = 120,
                    TriggerEveryNSamples = 60,
                    RetrainingIntervalHours = 1.0,
                    TransformerFastEpochs = 4
                });
            
            // ALT 전문가: 펌핑 패턴 학습
            var altTrainer = new EntryTimingMLTrainer("ALT_Specialist.zip");
            var altTransformer = new EntryTimingTransformerTrainer("ALT_Transformer.pt");
            _specialists[CoinGroup.ALT] = new AdaptiveOnlineLearningService(
                altTrainer,
                altTransformer,
                new OnlineLearningConfig
                {
                    SlidingWindowSize = 1000,  // 다양한 알트 패턴
                    MinSamplesForTraining = 200,
                    TriggerEveryNSamples = 100,
                    RetrainingIntervalHours = 1.0,
                    TransformerFastEpochs = 5
                });
            
            // 로그 이벤트 연결
            foreach (var kvp in _specialists)
            {
                kvp.Value.OnLog += msg => Info(LogCategory.AI, $"[{kvp.Key}] {msg}");
                kvp.Value.OnPerformanceUpdate += (reason, acc, mlThresh, tfThresh) =>
                {
                    Info(LogCategory.AI, 
                        $"[{kvp.Key}] 성능 업데이트: {reason} | 정확도={acc:P1}, ML={mlThresh:P0}, TF={tfThresh:P0}");
                };
            }
        }
        
        private void InitializeSymbolMapping()
        {
            // 심볼 → 그룹 매핑
            _symbolToGroup["BTCUSDT"] = CoinGroup.BTC;
            _symbolToGroup["ETHUSDT"] = CoinGroup.ETH;
            _symbolToGroup["SOLUSDT"] = CoinGroup.SOL;
            _symbolToGroup["XRPUSDT"] = CoinGroup.XRP;
            
            // 기타 메이저
            _symbolToGroup["BNBUSDT"] = CoinGroup.MAJOR;
            _symbolToGroup["ADAUSDT"] = CoinGroup.MAJOR;
            _symbolToGroup["DOGEUSDT"] = CoinGroup.MAJOR;
            _symbolToGroup["MATICUSDT"] = CoinGroup.MAJOR;
            
            // 나머지는 ALT로 처리
        }
        
        /// <summary>
        /// 심볼에 맞는 전문가 선택
        /// </summary>
        public CoinGroup GetCoinGroup(string symbol)
        {
            if (_symbolToGroup.TryGetValue(symbol.ToUpperInvariant(), out var group))
                return group;
            
            // 거래량 기반 동적 분류 (ALT vs STABLE)
            // TODO: 실시간 거래량 체크로 개선 가능
            return CoinGroup.ALT;
        }
        
        /// <summary>
        /// 라벨링된 샘플 추가 (전문가 + 메타 러닝)
        /// </summary>
        public async Task AddLabeledSampleAsync(string symbol, MultiTimeframeEntryFeature sample)
        {
            var group = GetCoinGroup(symbol);
            
            // 1. 해당 전문가에 추가
            if (_specialists.TryGetValue(group, out var specialist))
            {
                await specialist.AddLabeledSampleAsync(sample);
            }
            
            // 2. 메타 러닝 버퍼에도 추가 (신규 코인 빠른 적응용)
            _globalMetaBuffer.Enqueue(sample);
            while (_globalMetaBuffer.Count > 2000)  // 메타 버퍼 크기 제한
            {
                _globalMetaBuffer.TryDequeue(out _);
            }
            
            // 3. BTC-ETH 상관 학습 (ETH 샘플일 경우 BTC 데이터도 참조)
            if (group == CoinGroup.ETH)
            {
                await LearnEthBtcCorrelation(sample);
            }
        }
        
        /// <summary>
        /// ETH-BTC 상관 학습
        /// </summary>
        private async Task LearnEthBtcCorrelation(MultiTimeframeEntryFeature ethSample)
        {
            try
            {
                // BTC 최신 가격 가져오기 (ETH 진입 시점에 BTC 상황 학습)
                var btcPrice = await _exchangeService.GetPriceAsync("BTCUSDT", CancellationToken.None);
                // TODO: BTC 지표와 ETH 결과의 상관관계를 Feature로 추가
                // 예: ETH가 성공했을 때 BTC의 추세 방향/강도 학습
            }
            catch
            {
                // 상관 학습 실패는 무시
            }
        }
        
        /// <summary>
        /// 전문가 예측 (적응형 Threshold 적용)
        /// </summary>
        public (float mlConfidence, float tfConfidence, float mlThreshold, float tfThreshold) GetPrediction(
            string symbol, 
            MultiTimeframeEntryFeature feature)
        {
            var group = GetCoinGroup(symbol);
            
            if (!_specialists.TryGetValue(group, out var specialist))
            {
                // 전문가 없으면 기본값
                return (0.5f, 0.5f, 0.65f, 0.60f);
            }
            
            // TODO: 실제 예측 로직 연결
            return (
                specialist.CurrentMLThreshold,
                specialist.CurrentTFThreshold,
                specialist.CurrentMLThreshold,
                specialist.CurrentTFThreshold
            );
        }
        
        /// <summary>
        /// 메타 러닝: 신규 코인 빠른 적응
        /// </summary>
        public async Task<bool> AdaptToNewCoinAsync(string newSymbol, CancellationToken token = default)
        {
            try
            {
                Info(LogCategory.AI, $"[MetaLearning] 신규 코인 적응 시작: {newSymbol}");
                
                // 메타 버퍼에서 최근 2000건으로 빠른 학습
                var metaSamples = _globalMetaBuffer.ToList();
                if (metaSamples.Count < 100)
                {
                    Warn(LogCategory.AI, $"[MetaLearning] 메타 샘플 부족: {metaSamples.Count}");
                    return false;
                }
                
                // 빠른 학습 (에포크 적게)
                var metrics = await _metaTrainer.TrainAndSaveAsync(metaSamples);
                Info(LogCategory.AI, 
                    $"[MetaLearning] {newSymbol} 적응 완료: Accuracy={metrics.Accuracy:P2}");
                
                return true;
            }
            catch (Exception ex)
            {
                Error(LogCategory.AI, $"[MetaLearning] {newSymbol} 적응 실패", ex);
                return false;
            }
        }
        
        /// <summary>
        /// 모든 전문가 성능 요약
        /// </summary>
        public Dictionary<CoinGroup, (double accuracy, float mlThresh, float tfThresh, int windowSize)> GetPerformanceSummary()
        {
            var summary = new Dictionary<CoinGroup, (double, float, float, int)>();
            
            foreach (var kvp in _specialists)
            {
                summary[kvp.Key] = (
                    kvp.Value.CurrentAccuracy,
                    kvp.Value.CurrentMLThreshold,
                    kvp.Value.CurrentTFThreshold,
                    kvp.Value.WindowSize
                );
            }
            
            return summary;
        }
        
        public void Dispose()
        {
            foreach (var specialist in _specialists.Values)
            {
                specialist?.Dispose();
            }
        }
    }
}
