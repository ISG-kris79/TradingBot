using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// 파동 스나이퍼: 피보나치 매복 구간에서 방아쇠 조건 검증
    /// - RSI 다이버전스 감지
    /// - 볼린저 밴드 하단 터치 + 긴 아랫꼬리 확인
    /// - 거래량 폭발 감지
    /// </summary>
    public class WaveSniper
    {
        public class SniperSignal
        {
            public bool IsTriggerReady { get; set; }
            public string Reason { get; set; } = string.Empty;
            
            // 조건별 점수
            public bool IsInGoldenZone { get; set; }          // 피보나치 0.5~0.618 구간
            public bool HasRsiDivergence { get; set; }        // RSI 상승 다이버전스
            public bool HasBollingerTail { get; set; }        // 볼린저 하단 + 긴 꼬리
            public bool HasVolumeExplosion { get; set; }      // 거래량 2배 이상 폭발
            public bool HasPriceReversal { get; set; }        // 가격 반등 시작
            
            // 상세 정보
            public decimal CurrentPrice { get; set; }
            public decimal FibonacciLevel { get; set; }       // 현재 피보나치 레벨 (0.5, 0.618 등)
            public float RsiDivergenceStrength { get; set; }  // 다이버전스 강도 (0~1)
            public float VolumeMultiplier { get; set; }       // 거래량 배율
            public decimal TailRatio { get; set; }            // 아랫꼬리 비율
            public int ConfirmedConditions { get; set; }      // 충족된 조건 수
            
            public DateTime SignalTime { get; set; }
        }

        // 파라미터
        private const decimal GoldenZoneMin = 0.5m;         // 황금 구간 최소 (50% 되돌림)
        private const decimal GoldenZoneMax = 0.618m;       // 황금 구간 최대 (61.8% 되돌림)
        private const decimal ExtendedZoneMax = 0.786m;     // 확장 구간 최대 (78.6%)
        private const float VolumeExplosionThreshold = 2.0f; // 거래량 폭발 임계값 (평균의 2배)
        private const decimal TailMinRatio = 0.4m;          // 최소 아랫꼬리 비율 (캔들 전체 길이의 40%)
        private const int RsiDivergenceLookback = 5;        // RSI 다이버전스 확인 캔들 수
        private const int MinConfirmedConditions = 3;       // 최소 충족 조건 수 (5개 중 3개)

        /// <summary>
        /// 방아쇠 조건 종합 검증
        /// </summary>
        public SniperSignal EvaluateTrigger(
            ElliottWaveDetector.WaveState waveState,
            CandleData currentCandle,
            List<CandleData> recentCandles, // 최근 50개 캔들
            decimal currentPrice)
        {
            var signal = new SniperSignal
            {
                SignalTime = DateTime.UtcNow,
                CurrentPrice = currentPrice
            };

            // 1단계: 파동 상태 확인
            if (waveState == null || 
                (waveState.Phase != ElliottWaveDetector.WavePhase.Wave2Retracing && 
                 waveState.Phase != ElliottWaveDetector.WavePhase.Wave2Complete))
            {
                signal.Reason = "2파 조정 구간 아님";
                return signal;
            }

            // 2단계: 피보나치 구간 확인
            decimal fibLevel = waveState.Wave2RetracementRatio;
            signal.FibonacciLevel = fibLevel;
            
            if (fibLevel >= GoldenZoneMin && fibLevel <= GoldenZoneMax)
            {
                signal.IsInGoldenZone = true;
            }
            else if (fibLevel > GoldenZoneMax && fibLevel <= ExtendedZoneMax)
            {
                signal.IsInGoldenZone = true; // 확장 구간도 허용
                signal.Reason += "[확장구간] ";
            }
            else
            {
                signal.Reason = $"피보나치 구간 밖 (되돌림 {fibLevel:P1})";
                return signal;
            }

            // 3단계: RSI 다이버전스 검사
            signal.HasRsiDivergence = DetectRsiDivergence(
                recentCandles, 
                currentCandle, 
                out float divergenceStrength);
            signal.RsiDivergenceStrength = divergenceStrength;

            // 4단계: 볼린저 밴드 + 긴 아랫꼬리 검사
            signal.HasBollingerTail = CheckBollingerTail(
                currentCandle, 
                out decimal tailRatio);
            signal.TailRatio = tailRatio;

            // 5단계: 거래량 폭발 검사
            signal.HasVolumeExplosion = CheckVolumeExplosion(
                currentCandle, 
                recentCandles, 
                out float volumeMultiplier);
            signal.VolumeMultiplier = volumeMultiplier;

            // 6단계: 가격 반등 시작 확인
            signal.HasPriceReversal = CheckPriceReversal(
                currentPrice, 
                waveState.Wave2LowPrice, 
                currentCandle);

            // 최종 판정
            signal.ConfirmedConditions = 
                (signal.IsInGoldenZone ? 1 : 0) +
                (signal.HasRsiDivergence ? 1 : 0) +
                (signal.HasBollingerTail ? 1 : 0) +
                (signal.HasVolumeExplosion ? 1 : 0) +
                (signal.HasPriceReversal ? 1 : 0);

            signal.IsTriggerReady = signal.ConfirmedConditions >= MinConfirmedConditions;

            // 이유 작성
            if (signal.IsTriggerReady)
            {
                signal.Reason = $"✅ 방아쇠 조건 충족 ({signal.ConfirmedConditions}/5) | " +
                    $"Fib={fibLevel:P1} RSI다이버={signal.HasRsiDivergence} " +
                    $"볼밴꼬리={signal.HasBollingerTail} 거래량={signal.VolumeMultiplier:F1}x " +
                    $"반등={signal.HasPriceReversal}";
            }
            else
            {
                var missing = new List<string>();
                if (!signal.HasRsiDivergence) missing.Add("RSI다이버전스");
                if (!signal.HasBollingerTail) missing.Add("볼린저꼬리");
                if (!signal.HasVolumeExplosion) missing.Add("거래량폭발");
                if (!signal.HasPriceReversal) missing.Add("가격반등");
                
                signal.Reason = $"❌ 조건 부족 ({signal.ConfirmedConditions}/5) | 미충족: {string.Join(", ", missing)}";
            }

            return signal;
        }

        /// <summary>
        /// RSI 상승 다이버전스 감지
        /// 가격은 더 낮아졌는데 RSI는 오히려 올라가는 패턴
        /// </summary>
        private bool DetectRsiDivergence(
            List<CandleData> recentCandles,
            CandleData currentCandle,
            out float divergenceStrength)
        {
            divergenceStrength = 0f;

            if (recentCandles.Count < RsiDivergenceLookback + 5)
                return false;

            // 최근 N개 캔들에서 저점 2개 찾기
            var last10 = recentCandles.TakeLast(RsiDivergenceLookback + 5).ToList();
            
            // 첫 번째 저점 찾기 (이전 저점)
            int firstLowIdx = -1;
            decimal firstLowPrice = decimal.MaxValue;
            float firstLowRsi = 100f;
            
            for (int i = 0; i < last10.Count - RsiDivergenceLookback; i++)
            {
                if (last10[i].Close < firstLowPrice && last10[i].RSI > 20f && last10[i].RSI < 40f)
                {
                    firstLowPrice = last10[i].Close;
                    firstLowRsi = last10[i].RSI;
                    firstLowIdx = i;
                }
            }

            if (firstLowIdx == -1)
                return false;

            // 두 번째 저점 (현재)
            decimal secondLowPrice = currentCandle.Close;
            float secondLowRsi = currentCandle.RSI;

            // 다이버전스 조건:
            // 1. 가격은 더 낮아짐 (또는 비슷)
            // 2. RSI는 더 높아짐 (최소 0.5포인트 이상, 감도 강화됨: 3.0→0.5)
            bool priceLower = secondLowPrice <= firstLowPrice * 1.002m; // 0.2% 오차 허용
            bool rsiHigher = secondLowRsi > firstLowRsi + 0.5f;

            if (priceLower && rsiHigher)
            {
                // 다이버전스 강도 계산 (RSI 차이가 클수록 강함)
                divergenceStrength = Math.Min(1f, (secondLowRsi - firstLowRsi) / 20f);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 볼린저 밴드 하단 터치 + 긴 아랫꼬리 확인
        /// </summary>
        private bool CheckBollingerTail(CandleData currentCandle, out decimal tailRatio)
        {
            tailRatio = 0m;

            // 볼린저 밴드 하단 계산 (SMA_20 대신 BollingerLower 사용)
            decimal bollLower = (decimal)currentCandle.BollingerLower;
            
            // 조건 1: 캔들 저점이 볼린저 하단을 터치했는가
            bool touchedBollinger = currentCandle.Low <= bollLower * 1.005m; // 0.5% 오차 허용

            if (!touchedBollinger)
                return false;

            // 조건 2: 긴 아랫꼬리가 있는가
            decimal bodyHigh = Math.Max(currentCandle.Open, currentCandle.Close);
            decimal bodyLow = Math.Min(currentCandle.Open, currentCandle.Close);
            decimal candle종 = currentCandle.High - currentCandle.Low;
            decimal lowerTail = bodyLow - currentCandle.Low;

            if (candle종 <= 0)
                return false;

            tailRatio = lowerTail / candle종;

            // 아랫꼬리가 전체 캔들 길이의 40% 이상
            bool hasLongTail = tailRatio >= TailMinRatio;

            // 조건 3: 캔들이 볼린저 밴드 안으로 다시 들어왔는가 (반등)
            bool closedInside = currentCandle.Close > bollLower;

            return hasLongTail && closedInside;
        }

        /// <summary>
        /// 거래량 폭발 감지 (평균 대비 2배 이상)
        /// </summary>
        private bool CheckVolumeExplosion(
            CandleData currentCandle,
            List<CandleData> recentCandles,
            out float volumeMultiplier)
        {
            volumeMultiplier = 0f;

            if (recentCandles.Count < 20)
                return false;

            // 직전 20봉 평균 거래량
            var last20 = recentCandles.TakeLast(20).ToList();
            float avgVolume = last20.Average(c => c.Volume_Ratio);

            if (avgVolume <= 0)
                return false;

            volumeMultiplier = currentCandle.Volume_Ratio / avgVolume;

            return volumeMultiplier >= VolumeExplosionThreshold;
        }

        /// <summary>
        /// 가격 반등 시작 확인
        /// </summary>
        private bool CheckPriceReversal(
            decimal currentPrice,
            decimal wave2LowPrice,
            CandleData currentCandle)
        {
            // 조건 1: 현재 가격이 2파 저점보다 0.3% 이상 상승
            bool priceUp = currentPrice >= wave2LowPrice * 1.003m;

            // 조건 2: 양봉
            bool isGreenCandle = currentCandle.Close > currentCandle.Open;

            // 조건 3: RSI가 30 이상 (과매도 구간 탈출)
            bool rsiRecovery = currentCandle.RSI >= 30f;

            return priceUp && isGreenCandle && rsiRecovery;
        }

        /// <summary>
        /// 스나이퍼 신호를 로그 문자열로 변환
        /// </summary>
        public string FormatSignalLog(WaveSniper.SniperSignal signal)
        {
            return $"🎯 [WAVE_SNIPER] {signal.Reason}\n" +
                   $"   조건: 매복구간={signal.IsInGoldenZone} RSI다이버={signal.HasRsiDivergence}({signal.RsiDivergenceStrength:P0}) " +
                   $"볼밴꼬리={signal.HasBollingerTail}({signal.TailRatio:P0}) " +
                   $"거래량={signal.HasVolumeExplosion}({signal.VolumeMultiplier:F1}x) " +
                   $"반등={signal.HasPriceReversal}";
        }
    }
}
