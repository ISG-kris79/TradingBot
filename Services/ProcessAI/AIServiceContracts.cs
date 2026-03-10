using System;
using System.Collections.Generic;

namespace TradingBot.Services.ProcessAI
{
    /// <summary>
    /// AI 서비스 프로세스 간 통신 계약
    /// </summary>
    /// 
    // Request/Response 기본 클래스
    public class AIServiceRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
        public string Command { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    public class AIServiceResponse
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    // ML.NET 예측 요청/응답
    public class MLPredictRequest : AIServiceRequest
    {
        public CandleDataDto Candle { get; set; } = new();
    }

    public class MLPredictResponse : AIServiceResponse
    {
        public bool ShouldEnter { get; set; }
        public float Probability { get; set; }
        public float Confidence { get; set; }
    }

    // ML.NET 학습 요청/응답
    public class MLTrainRequest : AIServiceRequest
    {
        public List<MultiTimeframeEntryFeatureDto> Features { get; set; } = new();
        public int MaxEpochs { get; set; } = 100;
    }

    public class MLTrainResponse : AIServiceResponse
    {
        public double Accuracy { get; set; }
        public double F1Score { get; set; }
        public double AUC { get; set; }
        public int TrainedSamples { get; set; }
    }

    // Transformer 예측 요청/응답
    public class TransformerPredictRequest : AIServiceRequest
    {
        public List<MultiTimeframeEntryFeatureDto> Sequence { get; set; } = new();
    }

    public class TransformerPredictResponse : AIServiceResponse
    {
        public float CandlesToTarget { get; set; }
        public float Confidence { get; set; }
    }

    // Transformer 학습 요청/응답
    public class TransformerTrainRequest : AIServiceRequest
    {
        public List<MultiTimeframeEntryFeatureDto> Features { get; set; } = new();
        public int Epochs { get; set; } = 5;
        public int BatchSize { get; set; } = 16;
    }

    public class TransformerTrainResponse : AIServiceResponse
    {
        public float BestValidationLoss { get; set; }
        public float FinalTrainLoss { get; set; }
        public int TrainedEpochs { get; set; }
    }

    // 상태 체크
    public class HealthCheckRequest : AIServiceRequest
    {
    }

    public class HealthCheckResponse : AIServiceResponse
    {
        public bool ModelLoaded { get; set; }
        public string ModelPath { get; set; } = string.Empty;
        public DateTime ProcessStartTime { get; set; }
        public long MemoryUsageMB { get; set; }
    }

    // DTO 클래스들
    public class CandleDataDto
    {
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public float Volume { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public float RSI { get; set; }
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float BollingerUpper { get; set; }
        public float BollingerLower { get; set; }
        public float ATR { get; set; }
        public float Price_Change_Pct { get; set; }
        
        // [DTO 확장] MLService에서 사용하는 추가 속성
        public float Signal { get; set; }  // MACD Signal 라인 (MACD_Signal과 중복이지만 호환성 유지)
        public float VolumeMA { get; set; }  // 거래량 이동평균
        public float PriceChangePercent { get; set; }  // 가격 변화율
    }

    public class MultiTimeframeEntryFeatureDto
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public decimal EntryPrice { get; set; }
        
        // 1일 지표
        public float D1_Trend { get; set; }
        public float D1_RSI { get; set; }
        public float D1_MACD { get; set; }
        public float D1_Signal { get; set; }
        public float D1_BBPosition { get; set; }
        public float D1_Volume_Ratio { get; set; }
        public float D1_ATR { get; set; }  // [DTO 확장] TorchService용
        public float D1_VolumeMA { get; set; }  // [DTO 확장] TorchService용
        
        // 4시간 지표
        public float H4_Trend { get; set; }
        public float H4_RSI { get; set; }
        public float H4_MACD { get; set; }
        public float H4_Signal { get; set; }
        public float H4_BBPosition { get; set; }
        public float H4_Volume_Ratio { get; set; }
        public float H4_DistanceToSupport { get; set; }
        public float H4_DistanceToResist { get; set; }
        public float H4_ATR { get; set; }  // [DTO 확장] TorchService용
        public float H4_VolumeMA { get; set; }  // [DTO 확장] TorchService용
        
        // 2시간 지표
        public float H2_Trend { get; set; }
        public float H2_RSI { get; set; }
        public float H2_MACD { get; set; }
        public float H2_Signal { get; set; }
        public float H2_BBPosition { get; set; }
        public float H2_Volume_Ratio { get; set; }
        public float H2_WavePosition { get; set; }
        
        // 1시간 지표
        public float H1_Trend { get; set; }
        public float H1_RSI { get; set; }
        public float H1_MACD { get; set; }
        public float H1_Signal { get; set; }
        public float H1_BBPosition { get; set; }
        public float H1_Volume_Ratio { get; set; }
        public float H1_MomentumStrength { get; set; }
        public float H1_ATR { get; set; }  // [DTO 확장] TorchService용
        public float H1_VolumeMA { get; set; }  // [DTO 확장] TorchService용
        
        // 15분 지표
        public float M15_RSI { get; set; }
        public float M15_MACD { get; set; }
        public float M15_Signal { get; set; }
        public float M15_BBPosition { get; set; }
        public float M15_Volume_Ratio { get; set; }
        public float M15_PriceVsSMA20 { get; set; }
        public float M15_PriceVsSMA60 { get; set; }
        public float M15_ADX { get; set; }
        public float M15_PlusDI { get; set; }
        public float M15_MinusDI { get; set; }
        public float M15_ATR { get; set; }
        public float M15_OI_Change_Pct { get; set; }
        
        // 피보나치
        public float Fib_DistanceTo0382_Pct { get; set; }
        public float Fib_DistanceTo0618_Pct { get; set; }
        public float Fib_DistanceTo0786_Pct { get; set; }
        public float Fib_InEntryZone { get; set; }
        
        // [DTO 확장] 엘리엇 파동 및 피보나치 단일 속성 (TorchService용)
        public float WavePhase { get; set; }  // 파동 단계 (0~5)
        public float WaveStrength { get; set; }  // 파동 강도 (0~1)
        public float FibLevel { get; set; }  // 피보나치 레벨 (0~1, 예: 0.618)
        
        // 시간 컨텍스트
        public float IsAsianSession { get; set; }
        public float IsEuropeSession { get; set; }
        public float IsUSSession { get; set; }
        public float HourOfDay { get; set; }
        public float DayOfWeek { get; set; }
        
        // 라벨
        public bool ShouldEnter { get; set; }
        public float ActualProfitPct { get; set; }
    }
}
