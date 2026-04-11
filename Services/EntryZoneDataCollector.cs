using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.8.0] Entry Zone Multi-Output Data Collector (접근 B — 데이터 수집 전용)
    ///
    /// 목적:
    ///   - 향후 Entry Zone Multi-Output Regression 모델 학습에 필요한 raw 데이터를 미리 축적
    ///   - 현재 라이브 의사결정에는 사용하지 않음 (데이터 축적 기간 필요)
    ///
    /// 수집 대상:
    ///   - 각 포지션 진입 시 context 스냅샷 (features)
    ///   - 포지션 보유 중 실현된 고점/저점/최적 청산 시점
    ///   - 최종 결과 (ProfitPct, HoldingMinutes, ExitReason)
    ///
    /// 저장 방식:
    ///   - JSONL (line-delimited JSON) 형태로 파일에 append
    ///   - %LOCALAPPDATA%\TradingBot\EntryZoneData\entry_zone_YYYYMMDD.jsonl
    ///   - 일별 롤오버 — 파일당 크기 관리 용이
    /// </summary>
    public class EntryZoneDataCollector
    {
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "EntryZoneData");

        private readonly object _fileLock = new();
        public event Action<string>? OnLog;

        // 진입 시점 스냅샷을 청산 시점까지 유지
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EntryContextSnapshot> _activeSnapshots = new();

        public EntryZoneDataCollector()
        {
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>포지션 진입 시 호출 — 진입 시점 상태 스냅샷 저장</summary>
        public void RecordEntryContext(
            string symbol,
            bool isLong,
            decimal entryPrice,
            float rsi, float bbPosition, float atr, float volumeRatio,
            float momentum, float volatility, float mlConfidence,
            string signalSource)
        {
            try
            {
                var snapshot = new EntryContextSnapshot
                {
                    Symbol = symbol,
                    IsLong = isLong,
                    EntryPrice = entryPrice,
                    EntryTime = DateTime.UtcNow,
                    Features = new FeatureSnapshot
                    {
                        RSI = rsi,
                        BBPosition = bbPosition,
                        ATR = atr,
                        VolumeRatio = volumeRatio,
                        Momentum = momentum,
                        Volatility = volatility,
                        MLConfidence = mlConfidence,
                        HourOfDay = DateTime.Now.Hour,
                        DayOfWeek = (int)DateTime.Now.DayOfWeek
                    },
                    SignalSource = signalSource,
                    RealizedHigh = entryPrice,
                    RealizedLow = entryPrice,
                    HighReachedAt = DateTime.UtcNow,
                    LowReachedAt = DateTime.UtcNow
                };
                _activeSnapshots[symbol] = snapshot;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [EntryZoneCollector] {symbol} 진입 기록 실패: {ex.Message}");
            }
        }

        /// <summary>포지션 보유 중 틱 업데이트 — 고점/저점 추적</summary>
        public void UpdateRealizedExtremes(string symbol, decimal currentPrice)
        {
            if (!_activeSnapshots.TryGetValue(symbol, out var snap)) return;

            if (currentPrice > snap.RealizedHigh)
            {
                snap.RealizedHigh = currentPrice;
                snap.HighReachedAt = DateTime.UtcNow;
            }
            if (currentPrice < snap.RealizedLow || snap.RealizedLow == 0m)
            {
                snap.RealizedLow = currentPrice;
                snap.LowReachedAt = DateTime.UtcNow;
            }
        }

        /// <summary>포지션 청산 시 호출 — 레이블 완성 후 JSONL 파일에 append</summary>
        public void FinalizeEntryZoneSample(
            string symbol,
            decimal exitPrice,
            decimal pnlPercent,
            string exitReason)
        {
            try
            {
                if (!_activeSnapshots.TryRemove(symbol, out var snap)) return;

                decimal optimalExitPrice = snap.IsLong ? snap.RealizedHigh : snap.RealizedLow;
                decimal optimalStopLossPrice = snap.IsLong ? snap.RealizedLow : snap.RealizedHigh;
                double holdingMinutes = (DateTime.UtcNow - snap.EntryTime).TotalMinutes;

                var sample = new EntryZoneSample
                {
                    Symbol = snap.Symbol,
                    IsLong = snap.IsLong,
                    EntryPrice = snap.EntryPrice,
                    ExitPrice = exitPrice,
                    OptimalExitPrice = optimalExitPrice,
                    OptimalStopLossPrice = optimalStopLossPrice,
                    EntryTime = snap.EntryTime,
                    ExitTime = DateTime.UtcNow,
                    HoldingMinutes = holdingMinutes,
                    RealizedPnLPercent = (double)pnlPercent,
                    OptimalExitPnLPercent = snap.IsLong
                        ? (double)((optimalExitPrice - snap.EntryPrice) / snap.EntryPrice * 100m)
                        : (double)((snap.EntryPrice - optimalExitPrice) / snap.EntryPrice * 100m),
                    ExitReason = exitReason,
                    SignalSource = snap.SignalSource,
                    Features = snap.Features
                };

                AppendToJsonl(sample);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [EntryZoneCollector] {symbol} 청산 레이블 기록 실패: {ex.Message}");
            }
        }

        /// <summary>청산 없이 스냅샷 제거 (포지션 취소 등)</summary>
        public void DiscardSnapshot(string symbol)
        {
            _activeSnapshots.TryRemove(symbol, out _);
        }

        private void AppendToJsonl(EntryZoneSample sample)
        {
            string fileName = $"entry_zone_{DateTime.UtcNow:yyyyMMdd}.jsonl";
            string filePath = Path.Combine(DataDir, fileName);
            string jsonLine = JsonSerializer.Serialize(sample);

            lock (_fileLock)
            {
                File.AppendAllText(filePath, jsonLine + Environment.NewLine);
            }
        }

        /// <summary>수집된 샘플 파일 수 + 총 라인 수 통계</summary>
        public (int FileCount, long TotalSamples) GetStatistics()
        {
            try
            {
                if (!Directory.Exists(DataDir)) return (0, 0);
                var files = Directory.GetFiles(DataDir, "entry_zone_*.jsonl");
                long total = 0;
                foreach (var f in files)
                {
                    try { total += File.ReadLines(f).LongCount(); } catch { }
                }
                return (files.Length, total);
            }
            catch
            {
                return (0, 0);
            }
        }

        // ─── 내부 데이터 클래스 ──────────────────────────────

        private class EntryContextSnapshot
        {
            public string Symbol { get; set; } = "";
            public bool IsLong { get; set; }
            public decimal EntryPrice { get; set; }
            public DateTime EntryTime { get; set; }
            public FeatureSnapshot Features { get; set; } = new();
            public string SignalSource { get; set; } = "";
            public decimal RealizedHigh { get; set; }
            public decimal RealizedLow { get; set; }
            public DateTime HighReachedAt { get; set; }
            public DateTime LowReachedAt { get; set; }
        }
    }

    public class FeatureSnapshot
    {
        public float RSI { get; set; }
        public float BBPosition { get; set; }
        public float ATR { get; set; }
        public float VolumeRatio { get; set; }
        public float Momentum { get; set; }
        public float Volatility { get; set; }
        public float MLConfidence { get; set; }
        public int HourOfDay { get; set; }
        public int DayOfWeek { get; set; }
    }

    public class EntryZoneSample
    {
        public string Symbol { get; set; } = "";
        public bool IsLong { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal OptimalExitPrice { get; set; }
        public decimal OptimalStopLossPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public double HoldingMinutes { get; set; }
        public double RealizedPnLPercent { get; set; }
        public double OptimalExitPnLPercent { get; set; }
        public string ExitReason { get; set; } = "";
        public string SignalSource { get; set; } = "";
        public FeatureSnapshot Features { get; set; } = new();
    }
}
