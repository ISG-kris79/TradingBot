using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace TradingBot.Services
{
    /// <summary>
    /// [3단계] 워밍업 & [4단계] 슬롯 제한
    /// 
    /// 120초 워밍업 기간 동안 가격 변동 표준편차를 계산하여
    /// 휩소(Whipsaw) 상황을 사전 감지하고 불안정한 신호를 드랍합니다.
    /// 
    /// 슬롯: 최대 2개 동시 진입 제한으로 리스크 분산
    /// </summary>
    public class SignalWarmupService
    {
        private readonly ILogger _logger;
        public const int WARMUP_DURATION_SECONDS = 120;
        public const int MAX_CONCURRENT_SLOTS = 2;
        
        // 신호별 워밍업 데이터 저장 (symbol → WarmupData)
        private readonly ConcurrentDictionary<string, WarmupData> _warmupCache = new();
        
        // 현재 활성 슬롯 추적 (symbol 리스트)
        private readonly ConcurrentBag<string> _activeSlots = new();
        
        public event Action<string>? OnLog;

        // 휩소 감지 임계값 (가격 변동률이 이 값을 초과하면 불안정으로 판단)
        public double WhipsawVolatilityThreshold { get; set; } = 0.02; // 2% 표준편차

        public SignalWarmupService()
        {
            _logger = Log.ForContext<SignalWarmupService>();
        }

        /// <summary>
        /// 신호 등록 (워밍업 시작)
        /// </summary>
        public bool RegisterSignal(string symbol, decimal initialPrice)
        {
            // 슬롯 체크
            if (_activeSlots.Count >= MAX_CONCURRENT_SLOTS)
            {
                LogInfo($"❌ [{symbol}] 슬롯 제한 도달 ({_activeSlots.Count}/{MAX_CONCURRENT_SLOTS}). 신호 거부.");
                return false;
            }

            var warmupData = _warmupCache.GetOrAdd(symbol, _ => new WarmupData
            {
                Symbol = symbol,
                StartTime = DateTime.UtcNow,
                InitialPrice = initialPrice,
                PriceHistory = new List<(DateTime Timestamp, decimal Price)>()
            });

            warmupData.PriceHistory.Add((DateTime.UtcNow, initialPrice));
            
            LogInfo($"🔵 [{symbol}] 워밍업 시작 (${initialPrice:F2}). 120초 모니터링...");
            return true;
        }

        /// <summary>
        /// 가격 업데이트 (워밍업 중 가격 추적)
        /// </summary>
        public void UpdatePrice(string symbol, decimal currentPrice)
        {
            if (!_warmupCache.TryGetValue(symbol, out var data))
                return;

            data.PriceHistory.Add((DateTime.UtcNow, currentPrice));
            
            // 오래된 데이터 정리 (120초 이상 지난 데이터 제거)
            data.PriceHistory.RemoveAll(p => (DateTime.UtcNow - p.Timestamp).TotalSeconds > WARMUP_DURATION_SECONDS);
        }

        /// <summary>
        /// 워밍업 완료 여부 확인 + 휩소 검증
        /// </summary>
        public (bool IsReady, bool IsWhipsaw, string Reason) CheckWarmupStatus(string symbol)
        {
            if (!_warmupCache.TryGetValue(symbol, out var data))
            {
                return (false, false, "워밍업 데이터 없음");
            }

            var elapsed = (DateTime.UtcNow - data.StartTime).TotalSeconds;
            
            // 120초 미경과
            if (elapsed < WARMUP_DURATION_SECONDS)
            {
                return (false, false, $"워밍업 중... ({elapsed:F0}/{WARMUP_DURATION_SECONDS}초)");
            }

            // 120초 경과 → 휩소 검증
            var volatility = CalculateVolatility(data.PriceHistory);
            bool isWhipsaw = volatility > WhipsawVolatilityThreshold;

            if (isWhipsaw)
            {
                LogWarning($"⚠️ [{symbol}] 휩소 감지! 가격 변동률 {volatility:P2} > {WhipsawVolatilityThreshold:P2}. 신호 드랍.");
                return (true, true, $"휩소 감지 (변동률 {volatility:P2})");
            }

            LogInfo($"✅ [{symbol}] 워밍업 완료. 변동률 {volatility:P2}. 5단계 진입 가능.");
            return (true, false, "워밍업 통과");
        }

        /// <summary>
        /// 슬롯 점유 (진입 시 호출)
        /// </summary>
        public bool OccupySlot(string symbol)
        {
            if (_activeSlots.Count >= MAX_CONCURRENT_SLOTS)
            {
                LogWarning($"❌ [{symbol}] 슬롯 점유 실패. 이미 {_activeSlots.Count}개 활성.");
                return false;
            }

            _activeSlots.Add(symbol);
            LogInfo($"🔒 [{symbol}] 슬롯 점유. 활성 슬롯: {_activeSlots.Count}/{MAX_CONCURRENT_SLOTS}");
            return true;
        }

        /// <summary>
        /// 슬롯 해제 (청산 시 호출)
        /// </summary>
        public void ReleaseSlot(string symbol)
        {
            // ConcurrentBag에서 제거
            var temp = _activeSlots.ToList();
            temp.Remove(symbol);
            _activeSlots.Clear();
            temp.ForEach(s => _activeSlots.Add(s));

            _warmupCache.TryRemove(symbol, out _);
            
            LogInfo($"🔓 [{symbol}] 슬롯 해제. 활성 슬롯: {_activeSlots.Count}/{MAX_CONCURRENT_SLOTS}");
        }

        /// <summary>
        /// 가격 변동 표준편차 계산 (휩소 감지 핵심 로직)
        /// </summary>
        private double CalculateVolatility(List<(DateTime Timestamp, decimal Price)> priceHistory)
        {
            if (priceHistory.Count < 2)
                return 0.0;

            // 가격 변동률 계산 (연속 가격 간 변화율)
            var returns = new List<double>();
            for (int i = 1; i < priceHistory.Count; i++)
            {
                decimal prevPrice = priceHistory[i - 1].Price;
                decimal currPrice = priceHistory[i].Price;
                
                if (prevPrice == 0) continue;
                
                double changeRate = (double)((currPrice - prevPrice) / prevPrice);
                returns.Add(changeRate);
            }

            if (returns.Count == 0)
                return 0.0;

            // 표준편차 계산
            double mean = returns.Average();
            double sumSquaredDiff = returns.Sum(r => Math.Pow(r - mean, 2));
            double variance = sumSquaredDiff / returns.Count;
            double stdDev = Math.Sqrt(variance);

            return stdDev;
        }

        /// <summary>
        /// 현재 슬롯 상태 조회
        /// </summary>
        public (int ActiveSlots, int AvailableSlots) GetSlotStatus()
        {
            int active = _activeSlots.Count;
            int available = MAX_CONCURRENT_SLOTS - active;
            return (active, available);
        }

        private void LogInfo(string message)
        {
            _logger.Information(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void LogWarning(string message)
        {
            _logger.Warning(message);
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        /// <summary>
        /// 워밍업 데이터 저장 클래스
        /// </summary>
        private class WarmupData
        {
            public string Symbol { get; set; } = "";
            public DateTime StartTime { get; set; }
            public decimal InitialPrice { get; set; }
            public List<(DateTime Timestamp, decimal Price)> PriceHistory { get; set; } = new();
        }
    }
}
