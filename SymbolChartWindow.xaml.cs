using System.Windows;
using TradingBot.ViewModels;

namespace TradingBot
{
    public partial class SymbolChartWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public SymbolChartWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;

            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel.RefreshLiveChart();

            // AI 손절가 실시간 연동: ViewModel → SkiaCandleChart
            _viewModel.OnAIStopPriceChanged += OnAIStopPriceChanged;
        }

        private void OnWindowClosed(object? sender, System.EventArgs e)
        {
            // 이벤트 구독 해제 (메모리 누수 방지)
            _viewModel.OnAIStopPriceChanged -= OnAIStopPriceChanged;
            SkiaCandleChartControl.ClearTrailingHistory();
        }

        /// <summary>
        /// AI 엔진 → ViewModel → SkiaCandleChart 실시간 연결
        /// Background 우선순위로 호출되므로 주문 로직에 영향 없음
        /// </summary>
        private void OnAIStopPriceChanged(double stopPrice, double currentPrice, double exitScore)
        {
            // SkiaCandleChart는 Thread-safe (Interlocked 내부 사용)
            SkiaCandleChartControl.UpdateAIDynamicStop(stopPrice, currentPrice, exitScore);
        }
    }
}
