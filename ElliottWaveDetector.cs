using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// 엘리엇 파동 1-2-3파 패턴 감지 및 상태 추적
    /// 목표: 1파 확인 → 2파 조정 추적 → 3파 진입 타점 알림
    /// </summary>
    public class ElliottWaveDetector
    {
        public enum WavePhase
        {
            None,           // 파동 없음 (관망)
            Wave1Forming,   // 1파 형성 중 (충격파 의심)
            Wave1Confirmed, // 1파 확정 (고점 형성 후 하락 시작)
            Wave2Retracing, // 2파 조정 중 (매복 준비)
            Wave2Complete,  // 2파 완료 (3파 진입 대기)
            Invalid         // 파동 무효화 (2파가 1파 시작점 밑으로 돌파)
        }

        public class WaveState
        {
            public string Symbol { get; set; } = string.Empty;
            public WavePhase Phase { get; set; } = WavePhase.None;
            
            // 1파 정보
            public decimal Wave1StartPrice { get; set; }
            public DateTime Wave1StartTime { get; set; }
            public decimal Wave1PeakPrice { get; set; }
            public DateTime Wave1PeakTime { get; set; }
            public decimal Wave1Height => Wave1PeakPrice - Wave1StartPrice; // 1파 상승폭
            
            // 2파 정보
            public decimal Wave2LowPrice { get; set; }
            public DateTime Wave2LowTime { get; set; }
            public decimal Wave2RetracementRatio => 
                Wave1Height > 0 ? (Wave1PeakPrice - Wave2LowPrice) / Wave1Height : 0m;
            
            // 피보나치 구간
            public decimal Fib_0382 => Wave1PeakPrice - (Wave1Height * 0.382m);
            public decimal Fib_0500 => Wave1PeakPrice - (Wave1Height * 0.500m);
            public decimal Fib_0618 => Wave1PeakPrice - (Wave1Height * 0.618m);
            public decimal Fib_0786 => Wave1PeakPrice - (Wave1Height * 0.786m);
            
            public DateTime LastUpdateTime { get; set; }
            public int CandlesSinceWave1Peak { get; set; } // 1파 고점 이후 경과 캔들 수
        }

        private readonly Dictionary<string, WaveState> _waveStates = new();
        private readonly object _lock = new();

        // 파라미터
        private const decimal Wave1MinHeightPercent = 1.5m; // 1파 최소 상승폭 1.5%
        private const decimal Wave1VolumeMultiplier = 1.8m; // 1파 거래량 배율 1.8x
        private const int Wave1MinCandles = 3; // 1파 최소 지속 캔들 수
        private const int Wave2MaxCandles = 20; // 2파 최대 지속 캔들 수 (초과 시 무효)
        private const decimal Wave2InvalidationBuffer = 0.995m; // 1파 시작 가격의 99.5% 밑으로 가면 무효

        public WaveState? GetWaveState(string symbol)
        {
            lock (_lock)
            {
                return _waveStates.TryGetValue(symbol, out var state) ? state : null;
            }
        }

        /// <summary>
        /// 실시간 캔들 데이터로 파동 상태 업데이트
        /// </summary>
        public WaveState UpdateWaveDetection(
            string symbol,
            CandleData currentCandle,
            List<CandleData> recentCandles, // 최근 50개 캔들
            decimal currentPrice)
        {
            lock (_lock)
            {
                if (!_waveStates.TryGetValue(symbol, out var state))
                {
                    state = new WaveState { Symbol = symbol };
                    _waveStates[symbol] = state;
                }

                state.LastUpdateTime = DateTime.UtcNow;

                // 현재 페이즈별 로직 실행
                switch (state.Phase)
                {
                    case WavePhase.None:
                    case WavePhase.Invalid:
                        TryDetectWave1Start(state, currentCandle, recentCandles);
                        break;

                    case WavePhase.Wave1Forming:
                        UpdateWave1Formation(state, currentCandle, currentPrice, recentCandles);
                        break;

                    case WavePhase.Wave1Confirmed:
                    case WavePhase.Wave2Retracing:
                        UpdateWave2Retracement(state, currentCandle, currentPrice);
                        break;

                    case WavePhase.Wave2Complete:
                        // 3파 진입 대기 상태 - 외부에서 처리
                        break;
                }

                return state;
            }
        }

        /// <summary>
        /// 1파 시작 감지: 바닥에서 거래량 폭발 + 강한 상승
        /// </summary>
        private void TryDetectWave1Start(WaveState state, CandleData currentCandle, List<CandleData> recentCandles)
        {
            if (recentCandles.Count < 30)
                return;

            // 최근 20봉 평균 거래량
            var last20 = recentCandles.TakeLast(20).ToList();
            float avgVolume = last20.Average(c => c.Volume_Ratio);
            
            // 조건 1: 현재 캔들 거래량이 평균의 1.8배 이상
            if (currentCandle.Volume_Ratio < avgVolume * (float)Wave1VolumeMultiplier)
                return;

            // 조건 2: 양봉이고 상승폭이 0.5% 이상
            decimal bodyPercent = currentCandle.Close > 0 
                ? Math.Abs((currentCandle.Close - (decimal)currentCandle.Open) / currentCandle.Close * 100m)
                : 0m;
            
            if (currentCandle.Close <= (decimal)currentCandle.Open || bodyPercent < 0.5m)
                return;

            // 조건 3: RSI가 40~70 범위 (과매수/과매도 아님)
            if (currentCandle.RSI < 40f || currentCandle.RSI > 70f)
                return;

            // 1파 시작 확정
            state.Phase = WavePhase.Wave1Forming;
            state.Wave1StartPrice = (decimal)currentCandle.Open;
            state.Wave1StartTime = currentCandle.OpenTime;
            state.Wave1PeakPrice = currentCandle.Close;
            state.Wave1PeakTime = currentCandle.CloseTime;
            state.CandlesSinceWave1Peak = 0;
        }

        /// <summary>
        /// 1파 상승 추적 및 고점 확정
        /// </summary>
        private void UpdateWave1Formation(WaveState state, CandleData currentCandle, decimal currentPrice, List<CandleData> recentCandles)
        {
            state.CandlesSinceWave1Peak++;

            // 신고점 갱신
            if (currentCandle.Close > state.Wave1PeakPrice)
            {
                state.Wave1PeakPrice = currentCandle.Close;
                state.Wave1PeakTime = currentCandle.CloseTime;
                state.CandlesSinceWave1Peak = 0;
            }

            // 1파 확정 조건: 고점 이후 2개 캔들 연속 하락
            if (state.CandlesSinceWave1Peak >= 2 && currentPrice < state.Wave1PeakPrice * 0.995m)
            {
                // 1파 최소 높이 검증
                decimal wave1HeightPercent = state.Wave1Height / state.Wave1StartPrice * 100m;
                if (wave1HeightPercent >= Wave1MinHeightPercent)
                {
                    state.Phase = WavePhase.Wave1Confirmed;
                    state.Wave2LowPrice = currentPrice;
                    state.Wave2LowTime = currentCandle.CloseTime;
                    state.CandlesSinceWave1Peak = 0;
                }
                else
                {
                    // 1파 높이 부족 - 리셋
                    state.Phase = WavePhase.Invalid;
                }
            }

            // 타임아웃: 1파가 너무 길면 무효
            if (state.CandlesSinceWave1Peak > Wave1MinCandles * 3)
            {
                state.Phase = WavePhase.Invalid;
            }
        }

        /// <summary>
        /// 2파 조정 추적 및 매복 구간 진입 감지
        /// </summary>
        private void UpdateWave2Retracement(WaveState state, CandleData currentCandle, decimal currentPrice)
        {
            state.CandlesSinceWave1Peak++;
            state.Phase = WavePhase.Wave2Retracing;

            // 2파 저점 갱신
            if (currentPrice < state.Wave2LowPrice)
            {
                state.Wave2LowPrice = currentPrice;
                state.Wave2LowTime = currentCandle.CloseTime;
            }

            // ⚠️ 무효화 조건: 2파가 1파 시작점 밑으로 돌파
            if (currentPrice < state.Wave1StartPrice * Wave2InvalidationBuffer)
            {
                state.Phase = WavePhase.Invalid;
                return;
            }

            // 타임아웃: 2파가 너무 길면 무효
            if (state.CandlesSinceWave1Peak > Wave2MaxCandles)
            {
                state.Phase = WavePhase.Invalid;
                return;
            }

            // ✅ 2파 완료 조건: 피보나치 0.5~0.618 구간 도달 + 반등 시작
            decimal retracementRatio = state.Wave2RetracementRatio;
            if (retracementRatio >= 0.5m && retracementRatio <= 0.786m)
            {
                // 반등 확인: 현재 가격이 저점보다 0.3% 이상 상승
                if (currentPrice > state.Wave2LowPrice * 1.003m)
                {
                    state.Phase = WavePhase.Wave2Complete;
                }
            }
        }

        /// <summary>
        /// 특정 심볼의 파동 상태 리셋
        /// </summary>
        public void ResetWave(string symbol)
        {
            lock (_lock)
            {
                if (_waveStates.ContainsKey(symbol))
                {
                    _waveStates[symbol].Phase = WavePhase.None;
                }
            }
        }

        /// <summary>
        /// 파동이 진입 가능한 상태인지 확인
        /// </summary>
        public bool IsReadyForWave3Entry(string symbol)
        {
            var state = GetWaveState(symbol);
            return state?.Phase == WavePhase.Wave2Complete;
        }

        /// <summary>
        /// 현재 피보나치 구간 정보
        /// </summary>
        public string GetFibonacciZoneInfo(string symbol)
        {
            var state = GetWaveState(symbol);
            if (state == null || state.Phase < WavePhase.Wave1Confirmed)
                return "파동 미감지";

            return $"Wave1: {state.Wave1StartPrice:F8}→{state.Wave1PeakPrice:F8} (+{state.Wave1Height/state.Wave1StartPrice*100m:F2}%)\n" +
                   $"Fib 0.382: {state.Fib_0382:F8}\n" +
                   $"Fib 0.500: {state.Fib_0500:F8} ⭐\n" +
                   $"Fib 0.618: {state.Fib_0618:F8} ⭐⭐ (Golden Ratio)\n" +
                   $"Fib 0.786: {state.Fib_0786:F8}\n" +
                   $"Wave2 Low: {state.Wave2LowPrice:F8} (되돌림 {state.Wave2RetracementRatio:P1})";
        }
    }
}
