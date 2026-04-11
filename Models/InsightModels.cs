using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TradingBot.Models
{
    /// <summary>
    /// [v4.9.0] 대기 상태 — PumpScan/MarketCrash가 보는 Top 후보 카드
    /// PumpSignalLog 파싱 결과를 담아서 AI Insight Panel에 바인딩.
    /// </summary>
    public class CandidateItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));

        private string _symbol = "";
        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(); } }

        private double _mlProbability;
        public double MLProbability { get => _mlProbability; set { _mlProbability = value; OnPropertyChanged(); OnPropertyChanged(nameof(MLProbabilityText)); } }
        public string MLProbabilityText => _mlProbability > 0 ? $"{_mlProbability:P0}" : "--";

        private string _status = "";  // AI_ENTRY / REJECT(...) / WATCH(+0.5%) / CANDIDATE
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusColor)); } }

        public Brush StatusColor => _status switch
        {
            var s when s.StartsWith("AI_ENTRY") => Brushes.LimeGreen,
            var s when s.StartsWith("WATCH")    => Brushes.Gold,
            var s when s.StartsWith("REJECT")   => Brushes.Tomato,
            _                                   => Brushes.Gray,
        };

        // 예측 (EntryZoneRegressor / OptimalEntryPriceRegressor 결과가 있을 때)
        private double? _predictedTpPct;
        public double? PredictedTpPct { get => _predictedTpPct; set { _predictedTpPct = value; OnPropertyChanged(); OnPropertyChanged(nameof(PredictedTpSlText)); } }

        private double? _predictedSlPct;
        public double? PredictedSlPct { get => _predictedSlPct; set { _predictedSlPct = value; OnPropertyChanged(); OnPropertyChanged(nameof(PredictedTpSlText)); } }

        public string PredictedTpSlText
        {
            get
            {
                if (!_predictedTpPct.HasValue && !_predictedSlPct.HasValue) return "";
                string tp = _predictedTpPct.HasValue ? $"TP +{_predictedTpPct:F2}%" : "TP --";
                string sl = _predictedSlPct.HasValue ? $"SL -{_predictedSlPct:F2}%" : "SL --";
                return $"{tp} | {sl}";
            }
        }

        private DateTime _detectedAt = DateTime.Now;
        public DateTime DetectedAt { get => _detectedAt; set { _detectedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetectedAtText)); } }
        public string DetectedAtText => _detectedAt.ToString("HH:mm:ss");
    }

    /// <summary>
    /// [v4.9.0] 보유 상태 — 활성 포지션 Deep Dive
    /// </summary>
    public class PositionDetailViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));

        private string _symbol = "";
        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(); } }

        private string _side = "";      // LONG / SHORT
        public string Side { get => _side; set { _side = value; OnPropertyChanged(); OnPropertyChanged(nameof(SideColor)); } }

        public Brush SideColor => _side == "LONG" ? Brushes.LimeGreen : (_side == "SHORT" ? Brushes.Tomato : Brushes.Gray);

        private decimal _entryPrice;
        public decimal EntryPrice { get => _entryPrice; set { _entryPrice = value; OnPropertyChanged(); } }

        private decimal _currentPrice;
        public decimal CurrentPrice { get => _currentPrice; set { _currentPrice = value; OnPropertyChanged(); } }

        private decimal _roePct;
        public decimal RoePct { get => _roePct; set { _roePct = value; OnPropertyChanged(); OnPropertyChanged(nameof(RoeText)); OnPropertyChanged(nameof(RoeColor)); } }

        public string RoeText => $"{_roePct:+0.00;-0.00}%";
        public Brush RoeColor => _roePct >= 0 ? Brushes.LimeGreen : Brushes.Tomato;

        private decimal _unrealizedPnlUsd;
        public decimal UnrealizedPnlUsd { get => _unrealizedPnlUsd; set { _unrealizedPnlUsd = value; OnPropertyChanged(); OnPropertyChanged(nameof(PnlText)); } }
        public string PnlText => $"${_unrealizedPnlUsd:+0.00;-0.00}";

        private TimeSpan _holdingTime;
        public TimeSpan HoldingTime { get => _holdingTime; set { _holdingTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(HoldingTimeText)); } }
        public string HoldingTimeText => _holdingTime.TotalHours >= 1
            ? $"{(int)_holdingTime.TotalHours}h {_holdingTime.Minutes}m"
            : $"{_holdingTime.Minutes}m {_holdingTime.Seconds}s";

        private decimal _tpPrice;
        public decimal TpPrice { get => _tpPrice; set { _tpPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(TpText)); } }
        public string TpText => _tpPrice > 0 ? $"TP {_tpPrice:F4}" : "TP --";

        private decimal _slPrice;
        public decimal SlPrice { get => _slPrice; set { _slPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(SlText)); } }
        public string SlText => _slPrice > 0 ? $"SL {_slPrice:F4}" : "SL --";

        private double _progressToTpPct;
        public double ProgressToTpPct { get => _progressToTpPct; set { _progressToTpPct = value; OnPropertyChanged(); } }

        private string _aiReassessText = "AI 재예측: 대기 중";
        public string AIReassessText { get => _aiReassessText; set { _aiReassessText = value; OnPropertyChanged(); } }

        private Brush _aiReassessBrush = Brushes.Gray;
        public Brush AIReassessBrush { get => _aiReassessBrush; set { _aiReassessBrush = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// [v4.9.0] 사이드바 DETECT 카드 — 감지 파이프라인 헬스
    /// </summary>
    public class DetectHealthViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));

        private int _pumpScanPerMin;
        public int PumpScanPerMin { get => _pumpScanPerMin; set { _pumpScanPerMin = value; OnPropertyChanged(); } }

        private int _volumeSurgePerMin;
        public int VolumeSurgePerMin { get => _volumeSurgePerMin; set { _volumeSurgePerMin = value; OnPropertyChanged(); } }

        private int _spikePerMin;
        public int SpikePerMin { get => _spikePerMin; set { _spikePerMin = value; OnPropertyChanged(); } }

        private int _candidatesNow;
        public int CandidatesNow { get => _candidatesNow; set { _candidatesNow = value; OnPropertyChanged(); } }

        private int _trainedCount;
        public int TrainedCount { get => _trainedCount; set { _trainedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrainedText)); } }

        private int _totalCount = 150;
        public int TotalCount { get => _totalCount; set { _totalCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrainedText)); } }

        public string TrainedText => $"{_trainedCount}/{_totalCount}";
    }

    /// <summary>
    /// [v5.0.3] 카테고리별 오늘 통계 카드 (MAJOR/PUMP/SPIKE)
    /// 진입 시각 + Symbol 그룹핑, KST 00:00 기준
    /// </summary>
    public class CategoryStatsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));

        public string Category { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";

        private decimal _todayPnL;
        public decimal TodayPnL
        {
            get => _todayPnL;
            set { _todayPnL = value; OnPropertyChanged(); OnPropertyChanged(nameof(TodayPnLText)); OnPropertyChanged(nameof(TodayPnLColor)); }
        }

        private int _entries;
        public int Entries
        {
            get => _entries;
            set { _entries = value; OnPropertyChanged(); OnPropertyChanged(nameof(EntriesText)); OnPropertyChanged(nameof(WinRateText)); OnPropertyChanged(nameof(WinRateColor)); }
        }

        private int _wins;
        public int Wins
        {
            get => _wins;
            set { _wins = value; OnPropertyChanged(); OnPropertyChanged(nameof(WinRateText)); OnPropertyChanged(nameof(WinRateColor)); }
        }

        private int _losses;
        public int Losses
        {
            get => _losses;
            set { _losses = value; OnPropertyChanged(); }
        }

        public string TodayPnLText
        {
            get
            {
                string sign = _todayPnL >= 0 ? "+" : "";
                return $"{sign}${_todayPnL:F2}";
            }
        }

        public Brush TodayPnLColor
        {
            get
            {
                if (_todayPnL > 0) return new SolidColorBrush(Color.FromRgb(16, 185, 129));  // green #10B981
                if (_todayPnL < 0) return new SolidColorBrush(Color.FromRgb(239, 68, 68));   // red #EF4444
                return new SolidColorBrush(Color.FromRgb(156, 163, 175));                    // gray #9CA3AF
            }
        }

        public string EntriesText => $"{_entries}건";

        public double WinRate
        {
            get
            {
                int closed = _wins + _losses;
                return closed > 0 ? (double)_wins / closed * 100 : 0;
            }
        }

        public string WinRateText
        {
            get
            {
                int closed = _wins + _losses;
                if (closed == 0) return "--";
                return $"{WinRate:F1}%";
            }
        }

        public Brush WinRateColor
        {
            get
            {
                int closed = _wins + _losses;
                if (closed == 0) return new SolidColorBrush(Color.FromRgb(156, 163, 175));
                if (WinRate >= 60) return new SolidColorBrush(Color.FromRgb(16, 185, 129));
                if (WinRate >= 40) return new SolidColorBrush(Color.FromRgb(251, 191, 36));
                return new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }

        public void Update(int entries, int wins, int losses, decimal pnl)
        {
            TodayPnL = pnl;
            Entries = entries;
            Wins = wins;
            Losses = losses;
        }
    }
}
