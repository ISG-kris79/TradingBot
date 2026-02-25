﻿﻿﻿﻿using Binance.Net.Enums;
using System.Collections.Generic;
using Microsoft.ML.Data;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TradingBot.Models
{
    public enum StrategyType { Major, Scanner, Listing }
    public enum ExchangeType { Binance, Bybit, Bitget }

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


    public record TradeLog(string? Symbol, string? Side, string? Strategy, decimal Price, float AiScore, DateTime Time, decimal PnL = 0, decimal PnLPercent = 0);
    public class CandleModel
    {
        public string? Symbol { get; set; }
        public DateTime OpenTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
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
        public DateTime CloseTime { get; set; } // [추가] Bitget 캔들 데이터 호환성

        // 목표 (Label): 다음 봉 상승 여부

        // 보조지표 Feature (AI가 학습할 핵심 데이터)
        public float RSI { get; set; }
        public float BollingerUpper { get; set; }
        public float BollingerLower { get; set; }
        public float ElliottWaveState { get; set; } // 1~5파동 상태 수치화
        public int ElliottWaveStep { get; set; } // 1, 2, 3, 4, 5 파동 번호
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float MACD_Hist { get; set; }
        public float ATR { get; set; }
        public float Fib_236 { get; set; }
        public float Fib_382 { get; set; }
        public float Fib_500 { get; set; }
        public float Fib_618 { get; set; }
        public bool Label { get; set; }
        public double BB_Upper { get; set; }
        public double BB_Lower { get; set; }
        public float SentimentScore { get; set; } // [Agent 2] 뉴스 감성 점수 추가
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

    public class PositionInfo
    {
        public decimal HighestPrice { get; set; } // 추적용 고점 (숏일 경우 최저가)
        public bool IsPumpStrategy { get; set; } // 급등주 전략인지 여부
        // [추가] 익절 단계 추적 (0: 미실행, 1: 1단계 완료...)
        public int TakeProfitStep { get; set; } = 0;
        public bool IsLong { get; set; } // Long(매수) 포지션 여부
        public string? Symbol { get; set; }
        public OrderSide Side { get; set; } // Buy(Long) 또는 Sell(Short)
        public decimal EntryPrice { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Quantity { get; set; } // Re-added
        public DateTime EntryTime { get; set; }

        // [추가] 오류 해결을 위해 필요한 필드
        public int Leverage { get; set; }
        public bool IsAveragedDown { get; set; } = false; // [추가] 물타기(추가 매수) 실행 여부
        public float AiScore { get; set; } // [추가] 진입 시점 AI 점수
        public long StopOrderId { get; set; } = 0; // [추가] 서버사이드 손절 주문 ID
        public decimal UnrealizedPnl { get; set; } // [추가] 미실현 손익
    }
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
            set { _entryPrice = value; OnPropertyChanged(nameof(EntryPrice)); OnPropertyChanged(nameof(ProfitRate)); OnPropertyChanged(nameof(ProfitColor)); OnPropertyChanged(nameof(PriceColor)); }
        }
        private bool _isPositionActive;
        public bool IsPositionActive
        {
            get => _isPositionActive;
            set { _isPositionActive = value; OnPropertyChanged(nameof(IsPositionActive)); OnPropertyChanged(nameof(PriceColor)); }
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
            }
        }
        // 1. 실제 데이터를 저장할 변수
        private double _profitPercent;

        // 2. UI에서 바인딩해서 사용하는 속성
        public double ProfitPercent
        {
            get => _profitPercent;
            set
            {
                if (_profitPercent != value)
                {
                    _profitPercent = value;
                    OnPropertyChanged(nameof(ProfitPercent));

                    // ProfitPercent가 변할 때 연관된 UI 속성들도 같이 새로고침
                    OnPropertyChanged(nameof(ProfitColor));
                    OnPropertyChanged(nameof(ChartStroke));
                    OnPropertyChanged(nameof(ChartFill));
                    OnPropertyChanged(nameof(Status)); // 🔍, 💰, ⚠️ 아이콘 갱신
                }
            }
        }

        // 3. (중요) 기존에 ProfitRate를 참조하던 로직들을 ProfitPercent로 통합
        public double ProfitRate
        {
            get => ProfitPercent;
            set => ProfitPercent = value;
        }

        // 4. 수익률에 따른 색상 로직
        public Brush ProfitColor => ProfitPercent > 0 ? Brushes.LimeGreen :
                                    ProfitPercent < 0 ? Brushes.Crimson :
                                    Brushes.White;

        // [추가] 현재가 vs 진입가 비교 색상 (실시간)
        public Brush PriceColor
        {
            get
            {
                if (!IsPositionActive || EntryPrice == 0) return Brushes.White;
                if (LastPrice > EntryPrice) return Brushes.LimeGreen;
                if (LastPrice < EntryPrice) return Brushes.Tomato;
                return Brushes.White;
            }
        }

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
            set { _decision = value; OnPropertyChanged(nameof(Decision)); }
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