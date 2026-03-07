// ====================================================================
// DualAI ?�측 ?�스???�합 가?�드
// ====================================================================
// ???�일?� TradingEngine??DualAI_EntryPredictor�??�합?�는 ?�제 코드?�니??
// ?�제 ?�합 ??TradingEngine.cs�??�정?�거?? 별도??매니?� ?�래?�로 분리?�세??
// ====================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Integration
{
    /// <summary>
    /// DualAI ?�측 ?�스???�합 ?�퍼
    /// TradingEngine ?�는 MainViewModel?�서 ?�용
    /// </summary>
    public class DualAIPredictionManager : IDisposable
    {
        private readonly FifteenMinCandleManager _candleManager;
        private readonly DualAI_EntryPredictor _predictor;
        private readonly BinanceRestClient _restClient;
        
        private bool _disposed = false;

        public DualAIPredictionManager(BinanceRestClient restClient)
        {
            _restClient = restClient;
            
            // 1. 15분봉 관리자 초기??
            _candleManager = new FifteenMinCandleManager(restClient);

            // 2. DualAI ?�측�?초기??
            var settings = AppConfig.Current?.Trading?.DualAIPredictor ?? new DualAIPredictorSettings();
            _predictor = new DualAI_EntryPredictor(_candleManager, settings);

            // 3. 진입 ?�호 ?�벤??구독
            _predictor.OnEntrySignalGenerated += OnEntrySignal;
        }

        /// <summary>
        /// ?�볼 추�? �?15분봉 초기??
        /// </summary>
        public async Task<bool> AddSymbolAsync(string symbol, CancellationToken ct = default)
        {
            if (!AppConfig.Current?.Trading?.DualAIPredictor?.EnableDualAI ?? false)
            {
                LoggerService.Info($"[DualAIPredictionManager] DualAI 비활?�화 ?�태");
                return false;
            }

            bool success = await _candleManager.InitializeSymbolAsync(symbol, ct);
            if (success)
            {
                LoggerService.Info($"[DualAIPredictionManager] {symbol} 추�? ?�료 (15분봉 120�?준비됨)");
            }
            return success;
        }

        /// <summary>
        /// ?�시�?15분봉 ?�데?�트 (Binance WebSocket?�서 ?�출)
        /// </summary>
        public async Task OnFifteenMinCandleReceived(string symbol, CandleData candle)
        {
            await _candleManager.AddCandleAsync(symbol, candle);
        }

        /// <summary>
        /// 진입 ?�호 발생 ??처리
        /// </summary>
        private void OnEntrySignal(string symbol, EntrySignal signal)
        {
            // TradingEngine ?�는 MainViewModel???�호 ?�달
            LoggerService.Info($"[DualAIPredictionManager] {signal}");

            // 강한 ?�호 발생 ???�림
            if (signal.SignalStrength >= 70f)
            {
                NotifyStrongEntrySignal(signal);
            }
        }

        /// <summary>
        /// 강한 진입 ?�호 ?�림
        /// </summary>
        private void NotifyStrongEntrySignal(EntrySignal signal)
        {
            string message = $"?�� {signal.Symbol} 강한 {signal.Direction} 진입 ?�호!\n" +
                           $"?�호 강도: {signal.SignalStrength:F1}\n" +
                           $"?�재가: ${signal.CurrentPrice:F2}\n" +
                           $"ML.NET: {signal.MLNetProbability:P1}\n" +
                           $"Transformer ?�측: ${signal.TransformerPredictedPrice:F2} ({signal.TransformerChangePercent:+0.00;-0.00}%)";

            // MainWindow???�림 추�?
            MainWindow.Instance?.AddAlert(message);

            // Telegram ?�림 (?�션)
            try
            {
                // TelegramService는 인스턴스로 사용
                var telegramSvc = new TelegramService();
                _ = telegramSvc.SendMessageAsync(message);
            }
            catch { }
        }

        /// <summary>
        /// ?�동 ?�학???�작
        /// </summary>
        public void StartAutoRetrain()
        {
            if (AppConfig.Current?.Trading?.DualAIPredictor?.AutoRetrainEnabled ?? false)
            {
                _predictor.StartAutoRetrain();
                LoggerService.Info("[DualAIPredictionManager] 자동 재학습 활성화");
            }
        }

        /// <summary>
        /// ?�동 ?�학??중�?
        /// </summary>
        public void StopAutoRetrain()
        {
            _predictor.StopAutoRetrain();
        }

        /// <summary>
        /// ?�정 ?�볼??최신 진입 ?�호 조회
        /// </summary>
        public EntrySignal? GetLatestSignal(string symbol)
        {
            return _predictor.GetLatestSignal(symbol);
        }

        /// <summary>
        /// ?�동 ?�측 ?�행
        /// </summary>
        public async Task<EntrySignal> PredictNowAsync(string symbol)
        {
            return await _predictor.PredictEntrySignalAsync(symbol);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _predictor?.Dispose();
            _candleManager?.Dispose();
        }
    }

    // ====================================================================
    // TradingEngine.cs ?�합 ?�제
    // ====================================================================
    /*
    
    // TradingEngine ?�래???�드??추�?:
    private DualAIPredictionManager? _dualAIManager;

    // TradingEngine ?�성?�에??초기??
    public TradingEngine(...)
    {
        ...
        
        // DualAI ?�측 ?�스??초기??
        try
        {
            _dualAIManager = new DualAIPredictionManager(_restClient);
            LoggerService.Info("[TradingEngine] DualAI ?�측 ?�스??초기???�료");
        }
        catch (Exception ex)
        {
            LoggerService.Warning($"[TradingEngine] DualAI ?�측 ?�스??초기???�패: {ex.Message}");
        }
    }

    // StartScanningOptimizedAsync?�서 ?�볼 추�?:
    public async Task StartScanningOptimizedAsync(...)
    {
        ...
        
        foreach (var symbol in symbols)
        {
            // 기존 WebSocket 구독 로직
            ...
            
            // DualAI ?�스?�에 ?�볼 추�?
            if (_dualAIManager != null)
            {
                await _dualAIManager.AddSymbolAsync(symbol, _cts.Token);
            }
        }
        
        // ?�동 ?�학???�작
        _dualAIManager?.StartAutoRetrain();
    }

    // 15분봉 WebSocket 콜백?�서 ?�출:
    private async void OnFifteenMinKlineUpdate(string symbol, CandleData candle)
    {
        // DualAI ?�스?�에 ?�시�??�데?�트
        if (_dualAIManager != null && candle.Interval == "15m")
        {
            await _dualAIManager.OnFifteenMinCandleReceived(symbol, candle);
        }
    }

    // Stop 메서?�에???�리:
    public void Stop()
    {
        ...
        _dualAIManager?.StopAutoRetrain();
        _dualAIManager?.Dispose();
    }

    // 진입 로직?�서 ?�호 ?�용:
    private async Task CheckEntryOpportunity(string symbol, MarketData market)
    {
        // DualAI ?�호 조회
        var aiSignal = _dualAIManager?.GetLatestSignal(symbol);
        
        if (aiSignal != null && aiSignal.SignalStrength >= 70f)
        {
            LoggerService.Info($"[TradingEngine] {symbol} DualAI 진입 ?�호: {aiSignal}");
            
            // aiSignal.Direction???�라 진입 결정
            if (aiSignal.Direction == EntryDirection.Long)
            {
                // Long 진입 로직
                await ExecuteLongEntry(symbol, market, aiSignal);
            }
            else if (aiSignal.Direction == EntryDirection.Short)
            {
                // Short 진입 로직 (?�물거래�?
                await ExecuteShortEntry(symbol, market, aiSignal);
            }
        }
    }

    */
}
