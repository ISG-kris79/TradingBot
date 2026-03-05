﻿using Binance.Net.Enums;
using System.Collections.Generic;
using Microsoft.ML.Data;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot.Models
{
    public enum StrategyType { Major, Scanner, Listing }

    public class PumpScanSettings
    {
        public decimal MinPriceChangePercentage { get; set; } = 1.0m;
        public double MinVolumeRatio { get; set; } = 2.5;
        public double MinOrderBookRatio { get; set; } = 0.8;
        public double MinTakerBuyRatio { get; set; } = 1.2;
        public double MinVolumeRatio5m { get; set; } = 2.0;
    }

    public class TradingSettings
    {
        public int DefaultLeverage { get; set; } = 20;
        public decimal DefaultMargin { get; set; } = 200.0m;
        public decimal TargetRoe { get; set; } = 20.0m;
        public decimal StopLossRoe { get; set; } = 15.0m;
        public decimal TrailingStartRoe { get; set; } = 20.0m;
        public decimal TrailingDropRoe { get; set; } = 5.0m;
        public string MajorTrendProfile { get; set; } = string.Empty;

        // [Phase 12: PUMP 전략 지원] PUMP 전략 전용 레버리지
        public int PumpLeverage { get; set; } = 20;

        // [PUMP 20x 수동 매뉴얼 튜닝값]
        public decimal PumpTp1Roe { get; set; } = 20.0m;          // 1차 익절 ROE
        public decimal PumpTp2Roe { get; set; } = 50.0m;          // 2차 익절 ROE
        public decimal PumpTimeStopMinutes { get; set; } = 15.0m; // 시간 손절(분)
        public decimal PumpStopDistanceWarnPct { get; set; } = 1.0m; // 손절거리 경고(비중축소)
        public decimal PumpStopDistanceBlockPct { get; set; } = 1.3m; // 손절거리 차단(진입취소)
    }

    public class GridStrategySettings
    {
        public int GridLevels { get; set; } = 10;
        public decimal GridSpacingPercentage { get; set; } = 0.5m;
        public decimal AmountPerGrid { get; set; } = 20.0m;
    }

    public class ArbitrageSettings
    {
        public decimal MinSpreadPercentage { get; set; } = 0.5m;
        public bool AutoHedge { get; set; } = true;
        // [Phase 13] ArbitrageExecutionService를 위한 추가 속성
        public decimal MinProfitPercent { get; set; } = 0.2m;  // 최소 수익률
        public bool AutoExecute { get; set; } = false;         // 자동 실행 여부
        public int ScanIntervalSeconds { get; set; } = 60;     // 스캔 간격
        public decimal DefaultQuantity { get; set; } = 100m;   // 기본 수량 (USDT)
        public bool SimulationMode { get; set; } = true;       // [Phase 14] 시뮬레이션 모드 (안전)
    }

    // [Phase 14] 자금 이동 서비스 설정
    public class FundTransferSettings
    {
        public int CheckIntervalMinutes { get; set; } = 60;    // 체크 간격 (분)
        public decimal MinTransferAmount { get; set; } = 100m;  // 최소 이동 금액 (USDT)
        public decimal TargetBalanceRatio { get; set; } = 0.5m; // 목표 잔고 비율
        public bool SimulationMode { get; set; } = true;        // [Phase 14] 시뮬레이션 모드 (안전)
    }

    // [Phase 14] 포트폴리오 리밸런싱 설정
    public class PortfolioRebalancingSettings
    {
        public int CheckIntervalHours { get; set; } = 24;       // 체크 간격 (시간)
        public decimal RebalanceThreshold { get; set; } = 5.0m; // 리밸런싱 임계값 (%)
        public bool SimulationMode { get; set; } = true;        // [Phase 14] 시뮬레이션 모드 (안전)
        public Dictionary<string, decimal> TargetAllocations { get; set; } = new()
        {
            { "BTC", 40m },
            { "ETH", 30m },
            { "SOL", 20m },
            { "USDT", 10m }
        };
    }

    // [이동] TradingEngine에서 사용하던 캐시 아이템
    public class TickerCacheItem
    {
        public string? Symbol { get; set; }
        public decimal LastPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal QuoteVolume { get; set; }
    }

    // [이동] 볼린저 밴드 결과 구조체
    public struct BBResult
    {
        public double Upper; public double Mid; public double Lower;
    }


    // Removed TradeLog - use TradingBot.Shared.Models.TradeLog instead
    // Removed CandleModel - use TradingBot.Shared.Models.CandleModel instead

    /// <summary>
    /// Batch Order Request Model for Grid Strategy
    /// </summary>
    public class BatchOrderRequest
    {
        public required string Symbol { get; set; }
        public required string Side { get; set; } // "BUY" or "SELL"
        public decimal Quantity { get; set; }
        public decimal? Price { get; set; }
        public string? OrderType { get; set; } = "LIMIT"; // "LIMIT" or "MARKET"
    }

    public class BatchOrderResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> OrderIds { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    public class CandleData
    {
        public string? Symbol { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public float Volume { get; set; }
        public string? Interval { get; set; } // 1m, 5m, 15m, 1h, 2h, 4h, 1d
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }

        // OI / Funding / Squeeze (통합 호환 필드)
        public float OpenInterest { get; set; }
        public float OI_Change_Pct { get; set; }
        public float FundingRate { get; set; }
        public int SqueezeLabel { get; set; }

        // ──────────────────── 보조지표 Features ────────────────────
        // 1. 기본 보조지표
        public float RSI { get; set; }
        public float BollingerUpper { get; set; }
        public float BollingerLower { get; set; }
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float MACD_Hist { get; set; }
        public float ATR { get; set; }

        // 2. 피보나치 레벨
        public float Fib_236 { get; set; }
        public float Fib_382 { get; set; }
        public float Fib_500 { get; set; }
        public float Fib_618 { get; set; }

        // 3. 엘리엇 파동
        public float ElliottWaveState { get; set; } // 1~5파동 상태 수치화
        public int ElliottWaveStep { get; set; } // 1, 2, 3, 4, 5 파동 번호

        // 4. 이동평균 정렬 상태
        public float SMA_20 { get; set; }
        public float SMA_60 { get; set; }
        public float SMA_120 { get; set; }

        // ──────────────────── 파생 Features (AI 핵심) ────────────────────
        // 5. 정규화된 가격 파생 지표
        public float Price_Change_Pct { get; set; }        // (Close - Open) / Open * 100
        public float Price_To_BB_Mid { get; set; }          // (Close - BB_Mid) / BB_Mid * 100 (볼린저 밴드 이격도)
        public float BB_Width { get; set; }                  // (BB_Upper - BB_Lower) / BB_Mid * 100 (변동성)
        public float Price_To_SMA20_Pct { get; set; }       // (Close - SMA20) / SMA20 * 100 (MA 이격도)
        public float Candle_Body_Ratio { get; set; }        // |Close - Open| / (High - Low) (캔들 실체 비율)
        public float Upper_Shadow_Ratio { get; set; }       // 윗꼬리 비율
        public float Lower_Shadow_Ratio { get; set; }       // 아랫꼬리 비율

        // 6. 거래량 분석
        public float Volume_Ratio { get; set; }              // 현재 거래량 / 20봉 평균 거래량
        public float Volume_Change_Pct { get; set; }         // (현재 거래량 - 이전 거래량) / 이전 거래량 * 100

        // 7. 피보나치 포지션
        public float Fib_Position { get; set; }              // 0~1 사이 (현재 가격이 Fib 0.236~0.618 어디인지)

        // 8. 추세 강도
        public float Trend_Strength { get; set; }            // SMA 정렬 상태 기반 (-1 ~ +1)
        public float RSI_Divergence { get; set; }           // RSI와 가격 방향 괴리

        // 뉴스 감성
        public float SentimentScore { get; set; }

        // ──────────────────── Labels ────────────────────
        public bool Label { get; set; }                      // 기존 호환용 (다음 봉 상승 여부)

        // [NEW] 레버리지 기반 레이블 (핵심!)
        // 진입 후 10봉(50분) 이내: 목표가(+2.5%=ROE+50%)에 먼저 도달 → 1(Long Success)
        //                          손절가(-1.0%=ROE-20%)에 먼저 도달 → 0(Fail)
        public float LabelLong { get; set; }                 // LONG 성공 확률 (0 or 1)
        public float LabelShort { get; set; }                // SHORT 성공 확률 (0 or 1)
        public float LabelHold { get; set; }                 // HOLD (진입하지 않는 게 나은 구간, 1 or 0)

        // 기존 호환
        public double BB_Upper { get; set; }
        public double BB_Lower { get; set; }
    }

    public class ElliottPoints
    {
        public decimal P1 { get; set; } // 1파 고점
        public decimal P2 { get; set; } // 2파 저점
        public decimal P3 { get; set; } // 3파 고점 (진행 중일 땐 현재가 후보)
    }
    public class PredictionResult
    {
        [ColumnName("PredictedLabel")]
        public bool Prediction { get; set; } // 상승(True) / 하락(False)

        public float Probability { get; set; } // 확신도 (0.0 ~ 1.0)
        public float Score { get; set; }
    }

    // AI 예측 검증 대기 항목 (AIPredictionValidationService 호환)
    public class AIPrediction
    {
        public long Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public decimal PriceAtPrediction { get; set; }
        public string PredictedDirection { get; set; } = string.Empty; // "UP" / "DOWN"
        public string ModelName { get; set; } = string.Empty;
        public float Confidence { get; set; }
    }

    // AI 모니터링: 예측 추적 레코드
    public class AIPredictionRecord
    {
        public DateTime Timestamp { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public decimal PredictedPrice { get; set; }
        public decimal ActualPrice { get; set; }
        public bool PredictedDirection { get; set; } // true=상승, false=하락
        public bool ActualDirection { get; set; }
        public float Confidence { get; set; }
        public bool IsCorrect { get; set; }
    }

    // AI 모델 성능 통계
    public class AIModelPerformance : INotifyPropertyChanged
    {
        private string _modelName = string.Empty;
        public string ModelName
        {
            get => _modelName;
            set { _modelName = value; OnPropertyChanged(); }
        }

        private double _accuracy;
        public double Accuracy
        {
            get => _accuracy;
            set { _accuracy = value; OnPropertyChanged(); }
        }

        private int _totalPredictions;
        public int TotalPredictions
        {
            get => _totalPredictions;
            set { _totalPredictions = value; OnPropertyChanged(); }
        }

        private int _correctPredictions;
        public int CorrectPredictions
        {
            get => _correctPredictions;
            set { _correctPredictions = value; OnPropertyChanged(); }
        }

        private double _avgConfidence;
        public double AvgConfidence
        {
            get => _avgConfidence;
            set { _avgConfidence = value; OnPropertyChanged(); }
        }

        private Brush _statusColor = Brushes.White;
        public Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Removed PositionInfo - use TradingBot.Shared.Models.PositionInfo instead
    public class ExchangeInfo
    {
        public List<SymbolInfo> Symbols { get; set; } = new List<SymbolInfo>();
    }

    public class SymbolInfo
    {
        public string Name { get; set; } = string.Empty;
        public SymbolFilter? LotSizeFilter { get; set; }
        public SymbolFilter? PriceFilter { get; set; }
    }

    public class SymbolFilter
    {
        public decimal StepSize { get; set; }
        public decimal TickSize { get; set; }
    }
    // UI 업데이트를 위한 클래스
    public class StrategySignal
    {
        public string? Symbol { get; set; }
        public double RSI { get; set; }
        public float AIScore { get; set; }
        public string? Wave { get; set; }
        public string? BBStatus { get; set; }
        public string? Decision { get; set; } // 진입 가능 여부
    }
    // 상태 아이콘 열거형
    public enum PositionStatus { None, Monitoring, TakeProfitReady, Danger }

    public class MultiTimeframeViewModel : INotifyPropertyChanged
    {
        private decimal _entryPrice;
        public decimal EntryPrice
        {
            get => _entryPrice;
            set { _entryPrice = value; OnPropertyChanged(nameof(EntryPrice)); OnPropertyChanged(nameof(ProfitRate)); OnPropertyChanged(nameof(ProfitColor)); OnPropertyChanged(nameof(PriceColor)); OnPropertyChanged(nameof(Status)); }
        }
        private bool _isPositionActive;
        public bool IsPositionActive
        {
            get => _isPositionActive;
            set { _isPositionActive = value; OnPropertyChanged(nameof(IsPositionActive)); OnPropertyChanged(nameof(PriceColor)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(DisplayDecision)); }
        }

        private bool _isInPosition;
        public bool IsInPosition
        {
            get => _isInPosition;
            set { _isInPosition = value; OnPropertyChanged(nameof(IsInPosition)); OnPropertyChanged(nameof(SymbolColor)); }
        }
        private decimal _quantity;
        public decimal Quantity
        {
            get => _quantity;
            set
            {
                _quantity = value;
                OnPropertyChanged(nameof(Quantity));
                // 수량이 바뀌면 수익률 계산 방식(롱/숏)도 바뀔 수 있으므로 함께 통지
                OnPropertyChanged(nameof(ProfitRate));
                OnPropertyChanged(nameof(Status)); // 상태 아이콘도 갱신
            }
        }

        // 포지션 방향 ("LONG", "SHORT", "")
        private string _positionSide = "";
        public string PositionSide
        {
            get => _positionSide;
            set
            {
                if (_positionSide != value)
                {
                    _positionSide = value;
                    OnPropertyChanged(nameof(PositionSide));
                    OnPropertyChanged(nameof(PositionSideColor)); // 포지션 타입 색상도 갱신
                    OnPropertyChanged(nameof(ProfitRate)); // 방향이 바뀌면 ROI 재계산
                    OnPropertyChanged(nameof(Status)); // 상태 아이콘도 갱신
                }
            }
        }

        // 레버리지 (기본값 20배)
        public int Leverage { get; set; } = 20;
        // 1. 실제 데이터를 저장할 변수
        private double _profitPercent;

        // 2. UI에서 바인딩해서 사용하는 속성
        public double ProfitPercent
        {
            get => _profitPercent;
            set
            {
                value = SanitizeProfitPercent(value);

                if (_profitPercent != value)
                {
                    _profitPercent = value;
                    OnPropertyChanged(nameof(ProfitPercent));
                    OnPropertyChanged(nameof(ProfitRate)); // ProfitRate와 동기화

                    // ProfitPercent가 변할 때 연관된 UI 속성들도 같이 새로고침
                    OnPropertyChanged(nameof(ProfitColor));
                    OnPropertyChanged(nameof(ChartStroke));
                    OnPropertyChanged(nameof(ChartFill));
                    OnPropertyChanged(nameof(Status)); // 🔍, 💰, ⚠️ 아이콘 갱신
                }
            }
        }

        // 3. (중요) 기존에 ProfitRate를 참조하던 로직들을 ProfitPercent로 통합
        // 바이낸스/바이비트 표준 ROI 계산 (앱 표시 방식과 동일)
        public double ProfitRate
        {
            get
            {
                // 포지션이 활성화되어 있고 진입가가 있을 때만 계산
                if (!IsPositionActive || EntryPrice == 0 || LastPrice == 0)
                    return SanitizeProfitPercent(_profitPercent);

                // 바이낸스/바이비트 표준 ROI 계산:
                // LONG: ROI% = (Mark Price - Entry Price) / Entry Price × Leverage × 100
                // SHORT: ROI% = (Entry Price - Mark Price) / Entry Price × Leverage × 100

                decimal priceChange;
                if (string.Equals(PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase))
                {
                    // 숏: 진입가 - 현재가 (가격이 내려가면 이익)
                    priceChange = EntryPrice - LastPrice;
                }
                else if (string.Equals(PositionSide, "LONG", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(PositionSide))
                {
                    // 롱: 현재가 - 진입가 (가격이 올라가면 이익)
                    priceChange = LastPrice - EntryPrice;
                }
                else
                {
                    // 예상치 못한 값이면 기본값 반환
                    return SanitizeProfitPercent(_profitPercent);
                }

                double roi = (double)(priceChange / EntryPrice) * Leverage * 100;
                if (double.IsNaN(roi) || double.IsInfinity(roi))
                    return SanitizeProfitPercent(_profitPercent);

                // 계산된 값을 내부 필드에도 저장 (UI 업데이트를 위해)
                if (Math.Abs(_profitPercent - roi) > 0.001)
                {
                    _profitPercent = SanitizeProfitPercent(roi);
                }

                return SanitizeProfitPercent(roi);
            }
            set => ProfitPercent = value;
        }

        private static double SanitizeProfitPercent(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;
        }

        // 4. 수익률에 따른 색상 로직
        public Brush ProfitColor => ProfitPercent > 0 ? Brushes.LimeGreen :
                                    ProfitPercent < 0 ? Brushes.Crimson :
                                    Brushes.White;

        // [추가] 현재가 vs 진입가 비교 색상 (실시간) - LONG/SHORT 포지션 방향에 따라 색상 변경
        public Brush PriceColor
        {
            get
            {
                if (!IsPositionActive || EntryPrice == 0) return Brushes.White;

                // SHORT 포지션: 가격이 내려가면 수익 (녹색), 올라가면 손실 (빨강)
                if (string.Equals(PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase))
                {
                    if (LastPrice < EntryPrice) return Brushes.LimeGreen;  // 가격 하락 = 수익
                    if (LastPrice > EntryPrice) return Brushes.Tomato;     // 가격 상승 = 손실
                }
                // LONG 포지션: 가격이 올라가면 수익 (녹색), 내려가면 손실 (빨강)
                else
                {
                    if (LastPrice > EntryPrice) return Brushes.LimeGreen;  // 가격 상승 = 수익
                    if (LastPrice < EntryPrice) return Brushes.Tomato;     // 가격 하락 = 손실
                }

                return Brushes.White;
            }
        }

        // [추가] 포지션 타입 색상 (LONG=파랑, SHORT=오렌지)
        public Brush PositionSideColor
        {
            get
            {
                if (!IsPositionActive) return Brushes.Gray;

                if (string.Equals(PositionSide, "LONG", StringComparison.OrdinalIgnoreCase))
                    return Brushes.DeepSkyBlue;  // 파랑 계열
                else if (string.Equals(PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase))
                    return Brushes.Orange;       // 오렌지 계열

                return Brushes.Gray;
            }
        }

        // [Phase 7] Transformer 예측 결과
        private decimal _transformerPrice;
        public decimal TransformerPrice
        {
            get => _transformerPrice;
            set { _transformerPrice = value; OnPropertyChanged(nameof(TransformerPrice)); }
        }

        private double _transformerChange;
        public double TransformerChange
        {
            get => _transformerChange;
            set { _transformerChange = value; OnPropertyChanged(nameof(TransformerChange)); OnPropertyChanged(nameof(TransformerChangeColor)); }
        }

        public Brush TransformerChangeColor => TransformerChange > 0 ? Brushes.LimeGreen : (TransformerChange < 0 ? Brushes.Tomato : Brushes.Gray);

        // 5. 차트 색상 (ProfitPercent 기준)
        public Brush ChartStroke => ProfitPercent >= 0
            ? new SolidColorBrush(Color.FromRgb(0, 230, 118)) // Green
            : new SolidColorBrush(Color.FromRgb(255, 82, 82)); // Red

        public Brush ChartFill => ProfitPercent >= 0
            ? new SolidColorBrush(Color.FromArgb(30, 0, 230, 118))
            : new SolidColorBrush(Color.FromArgb(30, 255, 82, 82));

        // 6. 상태 아이콘 (ProfitPercent 기준)

        // 포지션 보유 중이면 심볼 색상을 다르게 표시 (예: 금색)
        public Brush SymbolColor => IsInPosition ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(0, 230, 118)); // 형광 초록
        public string? Symbol { get; set; }
        private decimal _lastPrice;
        public decimal LastPrice
        {
            get => _lastPrice;
            set
            {
                if (_lastPrice != value)
                {
                    _lastPrice = value;
                    OnPropertyChanged(nameof(LastPrice));
                    // 가격이 변하면 수익률과 관련 UI 요소들도 함께 갱신되어야 함
                    OnPropertyChanged(nameof(ProfitRate));
                    OnPropertyChanged(nameof(ProfitColor));
                    OnPropertyChanged(nameof(Status)); // 상태 아이콘(Danger 등) 갱신
                    OnPropertyChanged(nameof(PriceColor)); // [추가] 가격 색상 갱신
                }
            }
        }

        // 모니터링 강화 항목
        public string? Trend4H { get; set; }       // 예: "UPSTREAK", "DOWNSTREAK"
        public SolidColorBrush TrendColor4H => Trend4H == "UP" ? Brushes.LimeGreen : Brushes.Red;

        public string? BBPosition { get; set; } // 예: "Upper", "Lower", "Mid"
        public string? VolumeRatio { get; set; }   // 예: "3.5x" (평균대비 거래량)

        private string _signalSource = "-";
        public string SignalSource
        {
            get => _signalSource;
            set { _signalSource = value; OnPropertyChanged(nameof(SignalSource)); }
        }

        private double _shortLongScore;
        public double ShortLongScore
        {
            get => _shortLongScore;
            set { _shortLongScore = value; OnPropertyChanged(nameof(ShortLongScore)); OnPropertyChanged(nameof(LsScoreText)); }
        }

        private double _shortShortScore;
        public double ShortShortScore
        {
            get => _shortShortScore;
            set { _shortShortScore = value; OnPropertyChanged(nameof(ShortShortScore)); OnPropertyChanged(nameof(LsScoreText)); }
        }

        private double _macdHist;
        public double MacdHist
        {
            get => _macdHist;
            set { _macdHist = value; OnPropertyChanged(nameof(MacdHist)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private string _elliottTrend = "-";
        public string ElliottTrend
        {
            get => _elliottTrend;
            set { _elliottTrend = value; OnPropertyChanged(nameof(ElliottTrend)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private string _maState = "-";
        public string MAState
        {
            get => _maState;
            set { _maState = value; OnPropertyChanged(nameof(MAState)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private string _fibPosition = "-";
        public string FibPosition
        {
            get => _fibPosition;
            set { _fibPosition = value; OnPropertyChanged(nameof(FibPosition)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private double _volumeRatioValue;
        public double VolumeRatioValue
        {
            get => _volumeRatioValue;
            set { _volumeRatioValue = value; OnPropertyChanged(nameof(VolumeRatioValue)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        public string LsScoreText => $"L:{ShortLongScore:F0} / S:{ShortShortScore:F0}";

        public string ShortTermContext =>
            $"E:{ElliottTrend} | MA:{MAState} | Fib:{FibPosition} | MACD:{MacdHist:F3} | Vol:{VolumeRatioValue:F2}x";

        public string? StrategyName { get; set; }  // "Major Scalp" or "Pump Breakout"
        public Brush StrategyBg
        {
            get
            {
                if (StrategyName == "Major Scalp") return new SolidColorBrush(Color.FromRgb(37, 99, 235)); // 진한 파랑
                if (StrategyName == "Pump Breakout") return new SolidColorBrush(Color.FromRgb(220, 38, 38)); // 진한 빨강
                return Brushes.Gray;
            }
        }
        public double RSI_4H { get; set; }
        private double _rsi1h;
        public double RSI_1H
        {
            get => _rsi1h;
            set { _rsi1h = value; OnPropertyChanged(nameof(RSI_1H)); }
        }
        private float _aiScore; // Changed from int to float
        public float AIScore
        {
            get => _aiScore;
            set
            {
                if (_aiScore != value)
                {
                    _aiScore = value;
                    // 1. 점수 자체를 업데이트
                    OnPropertyChanged(nameof(AIScore));

                    // 2. 중요: 점수에 따라 결정(Decision)과 배경색도 변하므로 함께 갱신 알림
                    OnPropertyChanged(nameof(Decision));
                    OnPropertyChanged(nameof(DecisionBg));
                }
            }
        }

        private DateTime? _aiScoreUpdatedAt;
        public DateTime? AIScoreUpdatedAt
        {
            get => _aiScoreUpdatedAt;
            set
            {
                if (_aiScoreUpdatedAt != value)
                {
                    _aiScoreUpdatedAt = value;
                    OnPropertyChanged(nameof(AIScoreUpdatedAt));
                    OnPropertyChanged(nameof(AIScoreUpdatedText));
                }
            }
        }

        public string AIScoreUpdatedText => AIScoreUpdatedAt?.ToString("HH:mm:ss") ?? "-";

        public void TouchAIScoreUpdatedAt()
        {
            AIScoreUpdatedAt = DateTime.Now;
        }

        public Brush ScoreColor
        {
            get
            {
                if (AIScore >= 90) return Brushes.Crimson;    // 90점 이상: 진한 빨강
                if (AIScore >= 75) return Brushes.OrangeRed;  // 75점 이상: 주황색
                if (AIScore >= 50) return Brushes.Gold;       // 50점 이상: 금색
                return Brushes.White;                         // 기본: 흰색
            }
        }
        public string? BB_Status { get; set; }
        public string AIScoreText => $"{(AIScore * 100):F1}%";

        private string? _decision;
        public string? Decision
        {
            get => _decision;
            set { _decision = value; OnPropertyChanged(nameof(Decision)); OnPropertyChanged(nameof(DisplayDecision)); OnPropertyChanged(nameof(DecisionBg)); }
        }

        // RSI 값에 따른 동적 색상
        public Brush RSI_Color_4H => RSI_4H <= 30 ? Brushes.SkyBlue : (RSI_4H >= 70 ? Brushes.OrangeRed : Brushes.LightGray);

        // 결정 사항에 따른 배경색 (BUY=초록, SELL=빨강)
        public Brush DecisionBg
        {
            get
            {
                if (string.IsNullOrEmpty(Decision)) return Brushes.Transparent;

                if (Decision.Contains("LONG"))
                    return new SolidColorBrush(Color.FromRgb(37, 99, 235)); // 진한 블루 (Royal Blue)

                if (Decision.Contains("SHORT"))
                    return new SolidColorBrush(Color.FromRgb(220, 38, 38)); // 진한 레드 (Crimson Red)

                return new SolidColorBrush(Color.FromRgb(51, 65, 85)); // WAIT 상태 (슬레이트 그레이)
            }
        }

        // Display용 Decision (포지션 활성화 시 "진행중" 표시)
        public string DisplayDecision => IsPositionActive ? "진행중" : (Decision ?? "-");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // 전체 행의 기본 배경색 (매우 어두운 네이비/블랙)
        public Brush RowBackground => new SolidColorBrush(Color.FromRgb(22, 25, 37));
        private decimal _targetPrice;
        public decimal TargetPrice
        {
            get => _targetPrice;
            set { _targetPrice = value; OnPropertyChanged(nameof(TargetPrice)); OnPropertyChanged(nameof(ExitStrategySummary)); }
        }

        private decimal _stopLossPrice;
        public decimal StopLossPrice
        {
            get => _stopLossPrice;
            set { _stopLossPrice = value; OnPropertyChanged(nameof(StopLossPrice)); OnPropertyChanged(nameof(ExitStrategySummary)); }
        }
        // 1. 감시 가격 요약 (예: "TP: 2.5% | SL: -1.5%")
        public string ExitStrategySummary => IsPositionActive
            ? $"TP: {TargetPrice:F2} | SL: {StopLossPrice:F2}"
            : "-";

        // 2. 상태 아이콘 결정 로직
        public PositionStatus Status
        {
            get
            {
                if (!IsPositionActive) return PositionStatus.None;
                if (ProfitRate <= -1.0) return PositionStatus.Danger; // 손절 임박
                if (ProfitRate >= 2.0) return PositionStatus.TakeProfitReady; // 익절 구간 진입
                return PositionStatus.Monitoring; // 일반 감시 중
            }
        }

        // TargetPrice, StopLossPrice 등 값이 바뀔 때 OnPropertyChanged("ExitStrategySummary") 호출 필요
    }


    public class SymbolModel : INotifyPropertyChanged
    {
        private double _profitRate;
        public double ProfitRate
        {
            get => _profitRate;
            set
            {
                if (_profitRate != value)
                {
                    _profitRate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ProfitColor)); // 수익률이 변하면 색상도 변해야 함
                }
            }
        }

        // [추가] UI에서 바인딩할 색상 프로퍼티
        public Brush ProfitColor
        {
            get
            {
                if (ProfitRate > 0) return Brushes.LimeGreen;
                if (ProfitRate < 0) return Brushes.Tomato;
                return Brushes.White; // 0이거나 초기값일 때
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }

    /// <summary>
    /// [v2.1.18] 지표 결합형 동적 익절 시스템
    /// 5개 지표(엘리엇, 피보나치, RSI, BB, MACD)의 신호를 통합하여 익절 스탑을 동적으로 조정
    /// </summary>
    public class TechnicalData
    {
        // 기본 가격 데이터
        public decimal CurrentPrice { get; set; }
        public decimal HighestPrice { get; set; }
        public decimal LowestPrice { get; set; }

        // ATR (Average True Range)
        public decimal Atr { get; set; }
        public decimal AtrMultiplier { get; set; } = 1.5m;

        // 엘리엇 파동
        public bool IsWave5 { get; set; }              // 5파동 완성 신호
        public bool IsWaveExtended { get; set; }       // 파동 연장 중
        public int CurrentWaveCount { get; set; }

        // RSI (Relative Strength Index)
        public double Rsi { get; set; }
        public bool IsRsiOverbought => Rsi > 75;       // 과매수 (75 이상)
        public bool IsRsiExtreme => Rsi > 80;          // 극단적 과매수 (80 이상)

        // MACD (Moving Average Convergence Divergence)
        public double MacdLine { get; set; }
        public double SignalLine { get; set; }
        public double MacdHistogram { get; set; }
        public double PrevMacdHistogram { get; set; }
        public bool IsMacdHistogramDecreasing => MacdHistogram < PrevMacdHistogram;
        public bool IsMacdDeadCross => (MacdLine > SignalLine && MacdHistogram < 0);

        // 볼린저 밴드 (Bollinger Bands)
        public decimal MidBand { get; set; }           // 20일 SMA
        public decimal UpperBand { get; set; }         // SMA + 2*StdDev
        public decimal LowerBand { get; set; }         // SMA - 2*StdDev
        public bool HighWasAboveUpperBand { get; set; }
        public bool IsAboveUpperBand => CurrentPrice > UpperBand;
        public bool IsReturningToMidBand => HighWasAboveUpperBand && CurrentPrice < UpperBand;

        // 피보나치 (Fibonacci Extensions)
        public decimal EntryPrice { get; set; }
        public decimal Fibo1618 { get; set; }          // 1.618
        public decimal Fibo2618 { get; set; }          // 2.618
        public bool IsFibo1618Hit => CurrentPrice >= Fibo1618;
    }

    /// <summary>
    /// [v2.1.18] 고급 익절 신호
    /// 여러 지표의 신호를 종합하여 익절 추천 여부를 판단
    /// </summary>
    public class AdvancedExitSignal
    {
        public decimal RecommendedStopPrice { get; set; }
        public double TightModifier { get; set; } = 1.0;
        public bool ShouldTakeProfitNow { get; set; }
        public bool ShouldExecutePartialExit { get; set; }

        // 활성화된 신호 (로그용)
        public List<string> ActiveSignals { get; set; } = new();

        public string SignalSummary => string.Join(", ", ActiveSignals);
    }

    public class MarketData : INotifyPropertyChanged
    {
        public string? Symbol { get; set; }
        public decimal EntryPrice { get; set; }
        public bool IsPositionActive { get; set; }

        private double _profitRate;
        public double ProfitRate
        {
            get => _profitRate;
            set { _profitRate = value; OnPropertyChanged(); }
        }

        // UI에서 수익률에 따라 색상을 바꿀 때 사용 (예: 양수면 Green, 음수면 Red)
        public string ProfitColor => ProfitRate >= 0 ? "#00FF00" : "#FF0000";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }
}
