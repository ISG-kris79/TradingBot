using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;
using Binance.Net.Objects.Models.Futures.Socket;
using System.Collections.Concurrent;
using TradingBot.Models;
using PositionInfo = TradingBot.Shared.Models.PositionInfo;
using TradeLog = TradingBot.Shared.Models.TradeLog;

namespace TradingBot.Services
{
    public class PositionMonitorService
    {
        private readonly IBinanceRestClient _client;
        private readonly IExchangeService _exchangeService; // [변경]
        private readonly RiskManager _riskManager;
        private readonly MarketDataManager _marketDataManager;
        private readonly DbManager _dbManager;
        private readonly bool _isSimulationMode;
        private readonly Dictionary<string, PositionInfo> _activePositions;
        private readonly object _posLock;
        private readonly TradingSettings _settings;
        private readonly Func<TradingSettings?> _settingsProvider;
        private readonly ConcurrentDictionary<string, DateTime> _blacklistedSymbols;
        private readonly ConcurrentDictionary<string, int> _closingPositions = new();
        private readonly AdvancedExitStopCalculator _advancedExitCalculator;  // [v2.1.18] 지표 결합 익절
        private AIPredictor? _aiPredictor;

        // Events
        public event Action<string>? OnLog = delegate { };
        public event Action<string>? OnAlert = delegate { };
        public event Action<string, decimal, double?>? OnTickerUpdate = delegate { };
        public event Action<string, bool, decimal>? OnPositionStatusUpdate = delegate { };
        public event Action<string, bool, string?>? OnCloseIncompleteStatusChanged = delegate { };
        public event Action? OnTradeHistoryUpdated = delegate { };
        public event Action<string, DateTime, decimal, bool, decimal, string>? OnPositionClosedForAiLabel = delegate { };

        public PositionMonitorService(
            IBinanceRestClient client,
            IExchangeService exchangeService, // [변경]
            RiskManager riskManager,
            MarketDataManager marketDataManager,
            DbManager dbManager,
            Dictionary<string, PositionInfo> activePositions,
            object posLock,
            ConcurrentDictionary<string, DateTime> blacklistedSymbols,
            TradingSettings settings,
            AIPredictor? aiPredictor = null,
            AdvancedExitStopCalculator? advancedExitCalculator = null,
            Func<TradingSettings?>? settingsProvider = null)  // [v2.1.18] 선택적
        {
            _client = client;
            _exchangeService = exchangeService; // [변경]
            _riskManager = riskManager;
            _marketDataManager = marketDataManager;
            _dbManager = dbManager;
            _isSimulationMode = AppConfig.Current?.Trading?.IsSimulationMode ?? false;
            _activePositions = activePositions;
            _posLock = posLock;
            _blacklistedSymbols = blacklistedSymbols;
            _settings = settings;
            _settingsProvider = settingsProvider ?? (() => _settings);
            _aiPredictor = aiPredictor;
            _advancedExitCalculator = advancedExitCalculator ?? new AdvancedExitStopCalculator();  // [v2.1.18] 기본값 생성
        }

        private TradingSettings GetCurrentSettings()
        {
            return _settingsProvider.Invoke() ?? _settings;
        }

        private decimal GetCurrentStopLossRoe()
        {
            // [메이저/PUMP 완전 분리] 메이저 전용 손절 ROE 우선 사용
            var current = GetCurrentSettings();
            if (current.MajorStopLossRoe > 0m)
                return current.MajorStopLossRoe;

            if (_settings.MajorStopLossRoe > 0m)
                return _settings.MajorStopLossRoe;

            // 하위 호환 fallback
            decimal stopLossRoe = current.StopLossRoe;
            if (stopLossRoe > 0m)
                return stopLossRoe;

            if (_settings.StopLossRoe > 0m)
                return _settings.StopLossRoe;

            return 60.0m;
        }

        public void UpdateAiPredictor(AIPredictor? aiPredictor)
        {
            _aiPredictor = aiPredictor;
        }

        public bool IsCloseInProgress(string symbol)
        {
            return !string.IsNullOrWhiteSpace(symbol)
                && _closingPositions.TryGetValue(symbol, out var count)
                && count > 0;
        }

        private void MarkCloseStarted(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            _closingPositions.AddOrUpdate(symbol, 1, (_, current) => current + 1);
        }

        private void MarkCloseFinished(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return;

            while (_closingPositions.TryGetValue(symbol, out var current))
            {
                if (current <= 1)
                {
                    _closingPositions.TryRemove(symbol, out _);
                    return;
                }

                if (_closingPositions.TryUpdate(symbol, current - 1, current))
                    return;
            }
        }

        public async Task MonitorPositionStandard(
            string symbol,
            decimal entryPrice,
            bool isLong,
            CancellationToken token,
            string mode = "TREND",
            decimal customTakeProfitPrice = 0m,
            decimal customStopLossPrice = 0m)
        {
            bool isSidewaysMode = string.Equals(mode, "SIDEWAYS", StringComparison.OrdinalIgnoreCase);
            bool partialTaken = false;
            bool hybridDcaTaken = false;
            bool hybridDcaDeferredLogged = false;
            // [메이저/PUMP 완전 분리] 메이저 전용 레버리지 사용
            decimal leverage = _settings.MajorLeverage > 0 ? _settings.MajorLeverage : _settings.DefaultLeverage;
            decimal hybridDcaTriggerRoe = -5.0m;
            DateTime positionEntryTime = DateTime.Now;
            bool timeDecayBreakevenApplied = false;
            double timeDecayBreakevenMinutes = 60.0;
            double maxHoldingMinutes = 120.0;
            decimal timeoutExitRoeThreshold = 10.0m;
            DateTime nextAiRecheckTime = DateTime.Now.AddMinutes(60);
            DateTime nextHSCheckTime = DateTime.Now.AddMinutes(1);
            bool squeezeDefenseReduced = false;
            double squeezeDefenseMinutes = 90.0;
            decimal squeezeDefenseMaxRoe = 8.0m;
            decimal squeezeDefenseBbWidthThreshold = 0.60m;
            // [펀딩비 모니터링] 2시간 초과 포지션에 대한 누적 펀딩비 추적
            const decimal FundingRatePer8H = 0.0001m;   // 기본 추정치: 0.01%/8h (업비트/바이낸스 표준)
            DateTime? fundingCostLastLogTime = null;
            const double FundingMonitorStartMinutes = 120.0;  // 2시간 초과부터 모니터링 시작
            const decimal FundingExitAdjROEThreshold = 3.0m;  // 실질ROE 3% 미만이면 청산
            bool isBtcSymbol = string.Equals(symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase);
            bool isAtr20MajorSymbol =
                string.Equals(symbol, "ETHUSDT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(symbol, "XRPUSDT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(symbol, "SOLUSDT", StringComparison.OrdinalIgnoreCase);
            // [메이저/PUMP 완전 분리] 메이저 전용 최종 목표익절 ROE 사용
            decimal majorTp2Roe = _settings.MajorTp2Roe > 0 ? _settings.MajorTp2Roe : Math.Max(_settings.TargetRoe, 40.0m);
            decimal profitRunTriggerRoe = majorTp2Roe;
            bool profitRunHoldActive = false;
            DateTime nextProfitRunHoldLogTime = DateTime.MinValue;
            int pyramidingCount = 0;
            bool pyramidingArmed = false;
            bool pyramidingPullbackDetected = false;
            decimal pyramidingPeakPrice = 0m;
            decimal pyramidingPullbackPrice = 0m;
            decimal pyramidingBaseTriggerRoe = 50.0m;
            decimal pyramidingTriggerStepRoe = 15.0m;
            decimal pyramidingPullbackThresholdPct = 0.35m;
            decimal pyramidingReboundConfirmPct = 0.15m;
            decimal pyramidingAddRatio = 0.50m;
            int maxPyramidingCount = 3;
            DateTime pyramidingCooldownUntil = DateTime.MinValue;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    entryPrice = p.EntryPrice;
                    isLong = p.IsLong;
                    partialTaken = p.TakeProfitStep >= 1;
                    hybridDcaTaken = p.IsAveragedDown;
                    leverage = p.Leverage > 0 ? p.Leverage : leverage;
                    positionEntryTime = p.EntryTime == default ? positionEntryTime : p.EntryTime;
                    squeezeDefenseReduced = p.TakeProfitStep >= 1;
                    nextAiRecheckTime = positionEntryTime.AddMinutes(60);
                    pyramidingCount = p.PyramidCount > 0 ? p.PyramidCount : (p.IsPyramided ? 1 : 0);
                    if (p.HighestPrice <= 0m)
                        p.HighestPrice = p.EntryPrice;
                    if (p.LowestPrice <= 0m)
                        p.LowestPrice = p.EntryPrice;
                    profitRunHoldActive = p.IsProfitRunHoldActive;

                    if (customTakeProfitPrice <= 0 && p.TakeProfit > 0)
                        customTakeProfitPrice = p.TakeProfit;

                    if (customStopLossPrice <= 0 && p.StopLoss > 0)
                        customStopLossPrice = p.StopLoss;

                    isSidewaysMode = isSidewaysMode || (p.TakeProfit > 0 && p.StopLoss > 0);
                }
            }

            OnLog?.Invoke($"⏳ [{(isSidewaysMode ? "S" : "T")}] {symbol} 진입대기");

            // [3단계 본절 보호 & 수익 잠금] 스마트 방어 시스템
            decimal highestROE = -999m;            // 최고 ROE 추적
            decimal protectiveStopPrice = 0m;      // 방어적 스탑 가격
            bool breakEvenActivated = false;       // ROE 본절 보호 활성화
            bool profitLockActivated = false;      // ROE 수익 잠금 활성화
            bool tightTrailingActivated = false;   // ROE 타이트 트레일링 활성화
            // [틱 스파이크 필터] 1단계/2단계 본절 스탑은 30초 이상 유지돼야 청산
            // → 순간 스파이크로 본절 터치 후 가격이 회복되는 90% 케이스 방어
            DateTime breakEvenStopConfirmStart = DateTime.MinValue;
            
            // [15분봉 기준 조정] 3단계 파라미터 (기존 5분봉 기준에서 +40% 조정)
            decimal aggressiveMultiplier = 1.0m;   // 공격형 진입 배수 (1.0~2.0)
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var posInfo))
                    aggressiveMultiplier = posInfo.AggressiveMultiplier;
            }
            
            // [메이저/PUMP 완전 분리] 메이저 전용 설정값 사용 (하드코딩 제거)
            decimal majorBreakEvenBase = _settings.MajorBreakEvenRoe > 0 ? _settings.MajorBreakEvenRoe : 7.0m;
            decimal breakEvenROE = aggressiveMultiplier >= 1.5m ? Math.Max(3.0m, majorBreakEvenBase * 0.5m) : majorBreakEvenBase;  // 1단계: 공격형 시 절반, 일반 설정값
            decimal profitLockROE = _settings.MajorTp1Roe > 0 ? _settings.MajorTp1Roe : 20.0m;   // 2단계: 1차 부분익절 ROE
            decimal tightTrailingROE = _settings.MajorTrailingStartRoe > 0 ? _settings.MajorTrailingStartRoe : 40.0m;  // 3단계: 타이트 트레일링 시작 ROE
            decimal minLockROE = isAtr20MajorSymbol ? 6.0m : 12.0m;  // 3단계 진입 후 최소 ROE 유지 (4→6%, 8→12%)
            // TrailingGapRoe를 가격% 간격으로 변환
            decimal majorTrailingGap = _settings.MajorTrailingGapRoe > 0 ? _settings.MajorTrailingGapRoe : 5.0m;

            // 메이저 기본 운영값 강제: TP1=20, TP2/트레일링 시작=40, SL=-20, Gap=5~10
            profitLockROE = Math.Max(profitLockROE, 20.0m);
            tightTrailingROE = Math.Max(tightTrailingROE, 40.0m);
            majorTp2Roe = Math.Max(majorTp2Roe, 40.0m);
            majorTrailingGap = Math.Clamp(majorTrailingGap, 4.0m, 8.0m); // $250/일 목표: 최소4%~최대8% (이전 5~10%)

            // BTC 전용 슬림/초밀착 오버라이드: TP1=20, TP2/트레일링 시작=35, SL=-15, Gap=5
            if (isBtcSymbol)
            {
                profitLockROE = 20.0m;    // 15→20%: EV 개선 (50%WR 기준 +$4→+$16)
                tightTrailingROE = 35.0m;
                majorTp2Roe = 35.0m;
                majorTrailingGap = 5.0m;
            }

            // [ATR 2.0] ETH/XRP/SOL은 2차=30%, 3차=50%, Gap=6%로 수익 극대화
            if (isAtr20MajorSymbol)
            {
                profitLockROE = Math.Max(profitLockROE, 30.0m);      // 40→30%: 더 빠른 1차 부분익절
                tightTrailingROE = Math.Max(tightTrailingROE, 50.0m); // 60→50%: 트레일링 조기 시작
                majorTp2Roe = Math.Max(majorTp2Roe, 50.0m);           // 60→50%: 수익확정 기준 낮춤
                majorTrailingGap = Math.Max(majorTrailingGap, 6.0m);  // 10→6%: 수익 보호 강화
            }

            if (tightTrailingROE <= profitLockROE)
            {
                tightTrailingROE = profitLockROE + (isBtcSymbol ? 15.0m : 20.0m);
            }

            profitRunTriggerRoe = majorTp2Roe;

            decimal configuredMajorStopLossRoe = GetCurrentStopLossRoe();
            if (configuredMajorStopLossRoe <= 0m)
                configuredMajorStopLossRoe = 20.0m;

            decimal effectiveMajorStopLossRoe = isBtcSymbol
                ? Math.Min(configuredMajorStopLossRoe, 15.0m)
                : Math.Min(configuredMajorStopLossRoe, 20.0m);

            decimal tp1SafetyRoe = 5.0m; // 2→5%: TP1 이후 스탑을 +5% ROE로 상향 (본절 터치 후 날라가는 현상 방지)
            decimal tightGapPercent = majorTrailingGap / leverage / 100m;  // 3단계 간격 (ROE% → 가격%)
            decimal estimatedRoundTripCostPct = 0.0013m; // 수수료(0.08%) + 슬리피지(0.05%)
            decimal breakEvenBufferPct = estimatedRoundTripCostPct + (aggressiveMultiplier >= 1.5m ? 0.0002m : 0.0001m);
            decimal minBreakEvenRoe = breakEvenBufferPct * leverage * 100m;
            if (breakEvenROE < minBreakEvenRoe + 1.0m)
            {
                breakEvenROE = Math.Round(minBreakEvenRoe + 1.0m, 1);
            }
            double breakEvenMinHoldSeconds = aggressiveMultiplier >= 1.5m ? 90.0 : 45.0;
            bool breakEvenDeferredLogged = false;
            bool hasCustomAbsoluteStop = customStopLossPrice > 0;
            bool useMajorAtr20 = hasCustomAbsoluteStop && !isSidewaysMode && isAtr20MajorSymbol;
            // [ATR 스탑 진입 직후 wick-out 방지] 진입 후 5분간 ATR 스탑 발동 차단
            DateTime atrStopMinFireTime = positionEntryTime.AddMinutes(5.0);

            // ═══════════════════════════════════════════════════════════════
            // [하이브리드 듀얼 스탑 / ATR 2.0] 상태 변수
            //   - 기본 듀얼 스탑: ATR + Fractal + V-Stop
            //   - ATR 2.0 (ETH/XRP/SOL): 15분봉 Close-Only + 10캔들 Swing + 3.5~4.5x
            // ═══════════════════════════════════════════════════════════════
            List<IBinanceKline>? dualStopCandles = null;
            DateTime nextDualStopCandleRefresh = DateTime.MinValue;
            DateTime? atrFirstHitTime = null;
            DateTime? fractalBrokenTime = null;
            DateTime? closeOnlyWaitLoggedForCandle = null;
            bool whipsawLoggedOnce = false;
            bool atr20ActivationLogged = false;
            const double atrWhipsawMaxMinutes = 3.0;
            const double fractalVolumeMaxMinutes = 1.0;

            if (aggressiveMultiplier >= 1.5m)
            {
                OnLog?.Invoke($"🎯 {symbol} 공격형 진입 배수 {aggressiveMultiplier:F2}x 감지 → 손절 타이트 조정 (ROE {breakEvenROE:F1}%)");
            }

            OnLog?.Invoke($"📋 {symbol} [{(isBtcSymbol ? "BTC Specialist" : "Major Coin Mode")}] SL={effectiveMajorStopLossRoe:F0}% BreakEven={breakEvenROE:F1}% Tp1={profitLockROE:F0}% TrailStart={tightTrailingROE:F0}% TrailGap={majorTrailingGap:F1}% TP2={majorTp2Roe:F0}%");
            OnLog?.Invoke($"�🛡️ {symbol} 1단계 보호 조건: ROE {breakEvenROE:F1}% + 보유 {breakEvenMinHoldSeconds:F0}초, 본절 버퍼 {breakEvenBufferPct * 100m:F2}%");

            if (hasCustomAbsoluteStop && !isSidewaysMode)
            {
                OnLog?.Invoke($"🛡️ {symbol} ATR 하이브리드 손절 활성화 | 절대손절가={customStopLossPrice:F8}");
            }

            // [추가] 안전장치: 서버사이드 손절 주문 설정 (Stop Market)
            try
            {
                decimal currentStopLossRoe = effectiveMajorStopLossRoe;
                decimal stopPrice = customStopLossPrice > 0
                    ? customStopLossPrice
                    : (isLong
                        ? entryPrice * (1 - currentStopLossRoe / leverage / 100)
                        : entryPrice * (1 + currentStopLossRoe / leverage / 100));

                // 수량 조회 (락 필요)
                decimal qty = 0;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) qty = Math.Abs(p.Quantity); }

                if (qty > 0)
                {
                    if (useMajorAtr20)
                    {
                        OnLog?.Invoke($"🛡️ [ATR 2.0] {symbol} Close-Only 전략 적용 → 서버사이드 STOP_MARKET 생략");
                    }
                    else
                    {
                        var (success, orderId) = await _exchangeService.PlaceStopOrderAsync(symbol, isLong ? "SELL" : "BUY", qty, stopPrice, token);
                        if (success)
                        {
                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                    p.StopOrderId = orderId;
                            }
                            OnLog?.Invoke($"🛡️ {symbol} 손절시작");
                        }
                    }
                }
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ 손절 주문 설정 실패: {ex.Message}"); }

            // [방어 가드] PUMP 포지션은 MonitorPumpPositionShortTerm에서 전용 관리
            // Smart Protective Stop(breakEvenROE=7%)이 PUMP 전용 기준(20%)보다 낮아 조기 청산 유발
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var pCheck) && pCheck.IsPumpStrategy)
                {
                    OnLog?.Invoke($"⚠️ [{symbol}] PUMP 포지션 감지 → MonitorPositionStandard 종료 (Smart Protective Stop 미적용)");
                    return;
                }
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                    if (currentPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                        currentPrice = ticker.LastPrice;

                    if (currentPrice == 0) { await Task.Delay(2000, token); continue; }

                    // [듀얼 스탑] 15분봉 캔들 30초마다 갱신 (Fractal Low + 거래량 계산용)
                    if (hasCustomAbsoluteStop && !isSidewaysMode && DateTime.Now >= nextDualStopCandleRefresh)
                    {
                        try
                        {
                            var fetched = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 30, token);
                            if (fetched != null && fetched.Count >= 10)
                                dualStopCandles = fetched.ToList();
                        }
                        catch { /* 캔들 갱신 실패 시 이전 캐시 유지 */ }
                        nextDualStopCandleRefresh = DateTime.Now.AddSeconds(30);
                    }

                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var livePos) && livePos.Leverage > 0)
                            leverage = livePos.Leverage;
                    }

                    decimal priceChangePercent = isLong
                        ? (currentPrice - entryPrice) / entryPrice * 100
                        : (entryPrice - currentPrice) / entryPrice * 100;

                    decimal currentROE = priceChangePercent * leverage;
                    TimeSpan holdingTime = DateTime.Now - positionEntryTime;

                    OnTickerUpdate?.Invoke(symbol, 0m, (double)currentROE);

                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var livePos))
                        {
                            if (livePos.HighestPrice <= 0m || currentPrice > livePos.HighestPrice)
                                livePos.HighestPrice = currentPrice;

                            if (livePos.LowestPrice <= 0m || currentPrice < livePos.LowestPrice)
                                livePos.LowestPrice = currentPrice;
                        }
                    }

                    if (_aiPredictor != null && DateTime.Now >= nextAiRecheckTime)
                    {
                        if (TryEvaluateAiReversalExit(symbol, currentPrice, isLong, out string aiRecheckReason))
                        {
                            OnLog?.Invoke($"🧠 [AI 재검증 청산] {symbol} {aiRecheckReason}");
                            await ExecuteMarketClose(symbol, $"AI Reversal Exit ({aiRecheckReason})", token);
                            break;
                        }

                        OnLog?.Invoke($"🧠 [AI 재검증 유지] {symbol} {aiRecheckReason}");
                        nextAiRecheckTime = DateTime.Now.AddMinutes(60);
                    }

                    if (DateTime.Now >= nextHSCheckTime)
                    {
                        try
                        {
                            var hsKlines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 80, token);
                            if (hsKlines != null && hsKlines.Count >= 70)
                            {
                                var hsResult = HeadAndShouldersDetector.DetectPattern(hsKlines.ToList(), 70);
                                if (hsResult.IsDetected)
                                {
                                    if (isLong && hsResult.PatternType == "H&S")
                                    {
                                        OnLog?.Invoke($"🚨 [패턴 손절] {symbol} {hsResult.Message} -> 롱 포지션 즉시 청산");
                                        await ExecuteMarketClose(symbol, "H&S Pattern Panic Exit", token);
                                        break;
                                    }

                                    if (!isLong && hsResult.PatternType == "InverseH&S")
                                    {
                                        OnLog?.Invoke($"🔥 [패턴 손절] {symbol} {hsResult.Message} -> 숏 포지션 즉시 청산");
                                        await ExecuteMarketClose(symbol, "Inverse H&S Pattern Panic Exit", token);
                                        break;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }

                        nextHSCheckTime = DateTime.Now.AddMinutes(1);
                    }

                    if (!timeDecayBreakevenApplied && holdingTime.TotalMinutes >= timeDecayBreakevenMinutes)
                    {
                        decimal breakevenStopPrice = entryPrice;
                        bool stopTightened = false;
                        decimal previousStop = 0m;

                        if (hasCustomAbsoluteStop)
                        {
                            previousStop = customStopLossPrice;
                            if ((isLong && customStopLossPrice < breakevenStopPrice) || (!isLong && customStopLossPrice > breakevenStopPrice))
                            {
                                customStopLossPrice = breakevenStopPrice;
                                stopTightened = true;
                            }
                        }
                        else
                        {
                            previousStop = protectiveStopPrice;
                            if (protectiveStopPrice <= 0m || (isLong && protectiveStopPrice < breakevenStopPrice) || (!isLong && protectiveStopPrice > breakevenStopPrice))
                            {
                                protectiveStopPrice = breakevenStopPrice;
                                stopTightened = true;
                            }
                        }

                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                decimal storedStop = p.StopLoss;
                                if (storedStop <= 0m || (isLong && storedStop < breakevenStopPrice) || (!isLong && storedStop > breakevenStopPrice))
                                {
                                    p.StopLoss = breakevenStopPrice;
                                }
                            }
                        }

                        OnLog?.Invoke(stopTightened
                            ? $"🛡️ [시간 기반 보호] {symbol} {holdingTime.TotalMinutes:F0}분 경과 → 손절가를 본절({breakevenStopPrice:F8})로 상향 조정 (이전={previousStop:F8})"
                            : $"🛡️ [시간 기반 보호] {symbol} {holdingTime.TotalMinutes:F0}분 경과 → 기존 스탑이 본절 이상으로 유지 중입니다.");

                        timeDecayBreakevenApplied = true;
                    }

                    // [3단계 스마트 방어 시스템] 수익 보존 로직
                    
                    // 최고 ROE 추적
                    if (currentROE > highestROE)
                    {
                        highestROE = currentROE;
                        
                        // 포지션에 최고 ROE 기록
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                p.HighestROEForTrailing = highestROE;
                            }
                        }
                    }

                    // ═══════════════════════════════════════════════
                    // 1단계: ROE + 최소 보유시간 충족 시 본절 보호 활성화
                    // ═══════════════════════════════════════════════
                    bool breakEvenRoeReached = highestROE >= breakEvenROE;
                    bool breakEvenHoldSatisfied = holdingTime.TotalSeconds >= breakEvenMinHoldSeconds;

                    if (!breakEvenActivated && breakEvenRoeReached && !breakEvenHoldSatisfied)
                    {
                        if (!breakEvenDeferredLogged)
                        {
                            OnLog?.Invoke($"⏳ {symbol} 1단계 보호 대기 | ROE {highestROE:F1}% 달성, 보유시간 {holdingTime.TotalSeconds:F0}/{breakEvenMinHoldSeconds:F0}초");
                            breakEvenDeferredLogged = true;
                        }
                    }

                    if (!breakEvenActivated && breakEvenRoeReached && breakEvenHoldSatisfied)
                    {
                        breakEvenActivated = true;
                        breakEvenDeferredLogged = false;
                        
                        // 진입가 + 비용버퍼(수수료+슬리피지) + 안전마진
                        protectiveStopPrice = isLong 
                            ? entryPrice * (1 + breakEvenBufferPct)
                            : entryPrice * (1 - breakEvenBufferPct);

                        // [ATR 스탑 본절 동기화] 본절 보호 활성화 시 ATR 스탑을 최소 진입가 수준으로 올려
                        // SmartProtectiveStop이 이미 본절을 지키므로 ATR 스탑도 동기화 필요
                        if (hasCustomAbsoluteStop)
                        {
                            decimal atrBreakevenSync = isLong
                                ? entryPrice * (1 - breakEvenBufferPct)   // 진입가 - 버퍼 (LONG: 최소한 원금 근처)
                                : entryPrice * (1 + breakEvenBufferPct);
                            if (isLong && customStopLossPrice < atrBreakevenSync)
                            {
                                customStopLossPrice = atrBreakevenSync;
                                OnLog?.Invoke($"🔗 {symbol} ATR스탑 ↑ 본절 동기화: {customStopLossPrice:F8} (진입가={entryPrice:F8})");
                            }
                            else if (!isLong && customStopLossPrice > atrBreakevenSync)
                            {
                                customStopLossPrice = atrBreakevenSync;
                                OnLog?.Invoke($"🔗 {symbol} ATR스탑 ↓ 본절 동기화: {customStopLossPrice:F8} (진입가={entryPrice:F8})");
                            }
                        }

                        OnLog?.Invoke($"🛡️ {symbol} 손절대기 (ROE {highestROE:F1}%, 스탑 {protectiveStopPrice:F8})");
                    }

                    // ═══════════════════════════════════════════════
                    // 2단계: 메이저 2차 구간 진입 시 부분익절 + +2% 본절 방어
                    // ═══════════════════════════════════════════════
                    if (breakEvenActivated && !profitLockActivated && highestROE >= profitLockROE)
                    {
                        profitLockActivated = true;

                        if (!partialTaken)
                        {
                            await ExecutePartialClose(symbol, 0.40m, token);
                            partialTaken = true;

                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    p.TakeProfitStep = Math.Max(p.TakeProfitStep, 1);
                                }
                            }

                            OnAlert?.Invoke($"💰 {symbol} {(isBtcSymbol ? "BTC" : "메이저")} 1차 익절 40% 완료 (ROE {highestROE:F1}%)");
                        }
                        
                        // TP1 이후 손절선을 +2% ROE로 상향
                        decimal tp1SafetyPrice = isLong
                            ? entryPrice * (1m + tp1SafetyRoe / leverage / 100m)
                            : entryPrice * (1m - tp1SafetyRoe / leverage / 100m);

                        if (isLong)
                            protectiveStopPrice = protectiveStopPrice > 0m ? Math.Max(protectiveStopPrice, tp1SafetyPrice) : tp1SafetyPrice;
                        else
                            protectiveStopPrice = protectiveStopPrice > 0m ? Math.Min(protectiveStopPrice, tp1SafetyPrice) : tp1SafetyPrice;
                        
                        OnLog?.Invoke($"💰 {symbol} 2차 구간 방어 가동: TP30% + SL 상향(ROE +{tp1SafetyRoe:F1}%)");
                    }

                    // ═══════════════════════════════════════════════
                    // 3단계: 메이저 3차 구간 진입 시 넉넉한 트레일링 시작
                    // ═══════════════════════════════════════════════
                    if (profitLockActivated && !tightTrailingActivated && highestROE >= tightTrailingROE)
                    {
                        tightTrailingActivated = true;
                        
                        // ROE 15% = 0.75% 가격 변동 (20배 레버리지)
                        decimal minLockPriceChange = minLockROE / leverage / 100;
                        protectiveStopPrice = isLong 
                            ? entryPrice * (1 + minLockPriceChange)
                            : entryPrice * (1 - minLockPriceChange);

                        OnLog?.Invoke($"🚀 {symbol} 3차 구간 진입: SL +{minLockROE:F0}% 유지 + Wide Trailing {majorTrailingGap:F0}% 시작 (ROE {highestROE:F1}%)");

                        // [v2.1.18] 지표 기반 익절 준비: ROE 20% 도달 시 지표 모니터링 시작
                        OnLog?.Invoke($"� {symbol} 지표모니터링");
                    }

                    // ═══════════════════════════════════════════════
                    // 3단계 활성화 후: 최고가 추적 + 0.15% 타이트 트레일링
                    // ═══════════════════════════════════════════════
                    if (tightTrailingActivated)
                    {
                        decimal currentPriceForTrailing = currentPrice;
                        
                        if (isLong)
                        {
                            // 롱: 최고가 - 0.15% 간격
                            decimal newStopPrice = currentPriceForTrailing * (1 - tightGapPercent);
                            
                            // ROE 18% 아래로는 절대 내려가지 않음
                            decimal minLockPrice = entryPrice * (1 + minLockROE / leverage / 100);
                            if (newStopPrice < minLockPrice)
                                newStopPrice = minLockPrice;
                            
                            // 스탑 가격 상승만 허용 (절대 뒤로 물러나지 않음)
                            if (newStopPrice > protectiveStopPrice)
                            {
                                decimal oldStop = protectiveStopPrice;
                                protectiveStopPrice = newStopPrice;
                                OnLog?.Invoke($"📈 {symbol} 트레일링갱신 ▲");
                            }
                        }
                        else // 숏 포지션
                        {
                            // 숏: 최저가 + 0.15% 간격
                            decimal newStopPrice = currentPriceForTrailing * (1 + tightGapPercent);
                            
                            // ROE 18% 위로는 절대 올라가지 않음
                            decimal minLockPrice = entryPrice * (1 - minLockROE / leverage / 100);
                            if (newStopPrice > minLockPrice)
                                newStopPrice = minLockPrice;
                            
                            // 스탑 가격 하락만 허용 (절대 뒤로 물러나지 않음)
                            if (protectiveStopPrice == 0m || newStopPrice < protectiveStopPrice)
                            {
                                decimal oldStop = protectiveStopPrice;
                                protectiveStopPrice = newStopPrice;
                                if (oldStop > 0)
                                    OnLog?.Invoke($"📉 {symbol} 트레일링갱신 ▼");
                            }
                        }

                        // [v2.1.18] 지표 기반 익절 신호 모니터링 (3단계 타이트 트레일링 중)
                        if (highestROE >= tightTrailingROE)
                        {
                            // 지표 데이터 수집 (AdvancedIndicators 등에서 제공)
                            var tech = BuildTechnicalDataForExitSignal(symbol, currentPrice, entryPrice, isLong);
                            
                            if (tech != null)
                            {
                                // 지표 기반 익절 신호 계산
                                var exitSignal = _advancedExitCalculator.CalculateAdvancedExitStop(
                                    protectiveStopPrice,
                                    tech,
                                    isLong);

                                // [즉시 익절 1] BB 회귀 신호
                                if (exitSignal.ShouldTakeProfitNow)
                                {
                                    OnLog?.Invoke($"🟢 {symbol} 익절시작 [{exitSignal.SignalSummary}]");
                                    decimal exitROE = isLong
                                        ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                        : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                                    
                                    await ExecuteMarketClose(symbol, 
                                        $"Advanced Exit Signal: {exitSignal.SignalSummary} (ROE {exitROE:F1}%)", 
                                        token);
                                    break;
                                }

                                // [즉시 익절 2] 여러 신호 동시 발생 (3개 이상)
                                if (_advancedExitCalculator.ShouldExecuteImmediateExit(tech, exitSignal))
                                {
                                    OnLog?.Invoke($"🚨 {symbol} 익절시작 [다중신호]");
                                    decimal exitROE = isLong
                                        ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                        : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                                    
                                    await ExecuteMarketClose(symbol, 
                                        $"Multi-Signal Exit: {exitSignal.SignalSummary} (ROE {exitROE:F1}%)", 
                                        token);
                                    break;
                                }

                                // [지표 기반 스탑 갱신] 추천 스탑이 더 타이트하면 적용
                                if (isLong && exitSignal.RecommendedStopPrice > protectiveStopPrice)
                                {
                                    protectiveStopPrice = exitSignal.RecommendedStopPrice;
                                    OnLog?.Invoke($"🔧 {symbol} 지표 기반 스탑 갱신: {exitSignal.SignalSummary} | 새 스탑={protectiveStopPrice:F8}");
                                }
                                else if (!isLong && exitSignal.RecommendedStopPrice < protectiveStopPrice)
                                {
                                    protectiveStopPrice = exitSignal.RecommendedStopPrice;
                                    OnLog?.Invoke($"🔧 {symbol} 지표 기반 스탑 갱신: {exitSignal.SignalSummary} | 새 스탑={protectiveStopPrice:F8}");
                                }
                            }
                        }
                    }

                    // ═══════════════════════════════════════════════
                    // 방어적 스탑 체크
                    // 3단계(트레일링): 틱 즉시 청산 (수익 보호 최우선)
                    // 1~2단계(본절/수익잠금): 30초 이상 유지돼야 청산
                    //   → 순간 스파이크 터치 후 회복하는 90% 케이스 방어
                    // ═══════════════════════════════════════════════
                    if (protectiveStopPrice > 0)
                    {
                        bool stopHit = isLong
                            ? (currentPrice <= protectiveStopPrice)
                            : (currentPrice >= protectiveStopPrice);

                        if (stopHit)
                        {
                            if (tightTrailingActivated)
                            {
                                // 3단계: 즉시 청산
                                decimal finalROE3 = isLong
                                    ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                    : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                                OnLog?.Invoke($"🟢 {symbol} 3단계 트레일링 청산 (ROE {finalROE3:F1}%)");
                                await ExecuteMarketClose(symbol, $"Smart Protective Stop [3단계 타이트 트레일링] (ROE {finalROE3:F1}%)", token);
                                break;
                            }
                            else
                            {
                                // 1~2단계: 30초 확인 후 청산 (틱 스파이크 필터)
                                if (breakEvenStopConfirmStart == DateTime.MinValue)
                                {
                                    breakEvenStopConfirmStart = DateTime.Now;
                                    string pendingStage = profitLockActivated ? "2단계 수익잠금" : "1단계 본절";
                                    OnLog?.Invoke($"⏳ {symbol} [{pendingStage}] 스탑 터치 확인 대기 중 (30초 유지돼야 청산) | 스탑={protectiveStopPrice:F8} 현재={currentPrice:F8}");
                                }
                                else if ((DateTime.Now - breakEvenStopConfirmStart).TotalSeconds >= 30)
                                {
                                    decimal finalROE12 = isLong
                                        ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100
                                        : ((entryPrice - currentPrice) / entryPrice) * leverage * 100;
                                    string stage12 = profitLockActivated ? "2단계 수익 잠금" : "1단계 본절 보호";
                                    OnLog?.Invoke($"🟢 {symbol} {stage12} 청산 확정 (30초 유지, ROE {finalROE12:F1}%)");
                                    await ExecuteMarketClose(symbol, $"Smart Protective Stop [{stage12}] (ROE {finalROE12:F1}%)", token);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // 가격 회복 시 확인 타이머 리셋
                            if (breakEvenStopConfirmStart != DateTime.MinValue)
                            {
                                OnLog?.Invoke($"↩️ {symbol} 스탑 터치 해제 → 가격 회복, 확인 타이머 리셋 (스파이크 필터 작동)");
                                breakEvenStopConfirmStart = DateTime.MinValue;
                            }
                        }
                    }

                    if (hasCustomAbsoluteStop && !isSidewaysMode)
                    {
                        bool atrStopGateOpen = DateTime.Now >= atrStopMinFireTime;
                        if (useMajorAtr20)
                        {
                            if (atrStopGateOpen && dualStopCandles != null && dualStopCandles.Count >= 15)
                            {
                                var nowUtc = DateTime.UtcNow;
                                var latestRestCandle = dualStopCandles.LastOrDefault();
                                bool lastCandleStillOpen = latestRestCandle != null && latestRestCandle.CloseTime > nowUtc;
                                var closedCandles = lastCandleStillOpen
                                    ? dualStopCandles.Take(Math.Max(0, dualStopCandles.Count - 1)).ToList()
                                    : dualStopCandles.ToList();

                                if (closedCandles.Count >= 15)
                                {
                                    decimal atrMultiplier = holdingTime.TotalMinutes < 30.0
                                        ? 4.5m
                                        : (string.Equals(symbol, "XRPUSDT", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(symbol, "SOLUSDT", StringComparison.OrdinalIgnoreCase) ? 4.0m : 3.5m);

                                    double atrValue = IndicatorCalculator.CalculateATR(closedCandles, 14);
                                    if (atrValue > 0)
                                    {
                                        if (!atr20ActivationLogged)
                                        {
                                            OnLog?.Invoke($"🛡️ [ATR 2.0] {symbol} 15분봉 Close-Only 활성화 | ATRx{atrMultiplier:F1}, Swing=10캔들, 초기30분=4.5x");
                                            atr20ActivationLogged = true;
                                        }

                                        decimal atrDistance20 = (decimal)atrValue * atrMultiplier;
                                        decimal atrStopPrice20 = isLong
                                            ? entryPrice - atrDistance20
                                            : entryPrice + atrDistance20;

                                        var swingCandles20 = closedCandles.TakeLast(Math.Min(10, closedCandles.Count)).ToList();
                                        decimal swingStop20 = isLong
                                            ? swingCandles20.Min(c => c.LowPrice) * 0.999m
                                            : swingCandles20.Max(c => c.HighPrice) * 1.001m;

                                        decimal finalDeadLine = isLong
                                            ? Math.Min(atrStopPrice20, swingStop20)
                                            : Math.Max(atrStopPrice20, swingStop20);

                                        customStopLossPrice = finalDeadLine;
                                        lock (_posLock)
                                        {
                                            if (_activePositions.TryGetValue(symbol, out var p))
                                                p.StopLoss = finalDeadLine;
                                        }

                                        var latestClosedCandle = closedCandles.Last();
                                        decimal latestClosedPrice = latestClosedCandle.ClosePrice;
                                        bool liveTouch = isLong
                                            ? currentPrice <= finalDeadLine
                                            : currentPrice >= finalDeadLine;
                                        bool closeBroken = isLong
                                            ? latestClosedPrice <= finalDeadLine
                                            : latestClosedPrice >= finalDeadLine;

                                        if (liveTouch && !closeBroken)
                                        {
                                            if (closeOnlyWaitLoggedForCandle != latestClosedCandle.CloseTime)
                                            {
                                                TimeSpan remain = latestRestCandle != null && latestRestCandle.CloseTime > nowUtc
                                                    ? latestRestCandle.CloseTime - nowUtc
                                                    : TimeSpan.Zero;
                                                if (remain < TimeSpan.Zero)
                                                    remain = TimeSpan.Zero;

                                                OnLog?.Invoke(
                                                    $"🛡️ [WHIPSAW PROTECTION ACTIVE] {symbol} 종가 대기중 | " +
                                                    $"현재가={currentPrice:F8}, 방어선={finalDeadLine:F8}, ATRx{atrMultiplier:F1}, " +
                                                    $"Swing10={swingStop20:F8}, 남은 {remain.Minutes:D2}:{remain.Seconds:D2}");
                                                closeOnlyWaitLoggedForCandle = latestClosedCandle.CloseTime;
                                            }
                                        }
                                        else if (closeBroken)
                                        {
                                            decimal closeOnlyRoe = isLong
                                                ? ((latestClosedPrice - entryPrice) / entryPrice) * leverage * 100m
                                                : ((entryPrice - latestClosedPrice) / entryPrice) * leverage * 100m;
                                            OnLog?.Invoke(
                                                $"🔴 [ATR 2.0 CLOSE CONFIRMED] {symbol} 15분봉 종가 이탈 확정 | " +
                                                $"Close={latestClosedPrice:F8}, DeadLine={finalDeadLine:F8}, ROE={closeOnlyRoe:F1}%");
                                            await ExecuteMarketClose(symbol,
                                                $"ATR 2.0 Close-Only [{symbol}] ROE={closeOnlyRoe:F1}% (Close={latestClosedPrice:F8})",
                                                token);
                                            break;
                                        }
                                        else
                                        {
                                            closeOnlyWaitLoggedForCandle = null;
                                        }
                                    }
                                }
                            }
                        }
                        else if (atrStopGateOpen)
                        {
                            bool atrHit = (isLong  && currentPrice <= customStopLossPrice)
                                       || (!isLong && currentPrice >= customStopLossPrice);

                            if (!atrHit)
                            {
                                // 가격 회복 → 모든 경보 상태 초기화
                                if (atrFirstHitTime.HasValue)
                                {
                                    double recoveredSec = (DateTime.Now - atrFirstHitTime.Value).TotalSeconds;
                                    OnLog?.Invoke($"✅ [ATR 회복] {symbol} ATR 라인 복귀 ({recoveredSec:F0}초 버팀 성공)");
                                }
                                atrFirstHitTime   = null;
                                fractalBrokenTime = null;
                                whipsawLoggedOnce = false;
                            }
                            else
                            {
                                // ─── ATR 터치 ────────────────────────────────────────────
                                if (!atrFirstHitTime.HasValue)
                                    atrFirstHitTime = DateTime.Now;

                                double atrHeldSec = (DateTime.Now - atrFirstHitTime.Value).TotalSeconds;

                                // 1차 방어선: Fractal Low 계산 (직전 5캔들)
                                decimal fractalStop = 0m;
                                if (dualStopCandles != null && dualStopCandles.Count >= 5)
                                {
                                    var last5 = dualStopCandles.TakeLast(5).ToList();
                                    fractalStop = isLong
                                        ? last5.Min(c => c.LowPrice)  * 0.999m
                                        : last5.Max(c => c.HighPrice) * 1.001m;
                                }

                                bool fractalBroken = fractalStop > 0m
                                    && ((isLong  && currentPrice <= fractalStop)
                                    ||  (!isLong && currentPrice >= fractalStop));

                                if (!fractalBroken)
                                {
                                    if (!whipsawLoggedOnce)
                                    {
                                        OnLog?.Invoke(
                                            $"⚠️ [WHIPSAW DETECTED - HOLDING] {symbol} " +
                                            $"ATR=({customStopLossPrice:F8}) 터치, " +
                                            $"Fractal 지지선=({(fractalStop > 0 ? fractalStop.ToString("F8") : "N/A")}) 생존 → 버팀 시작");
                                        whipsawLoggedOnce = true;
                                    }

                                    if (atrHeldSec >= atrWhipsawMaxMinutes * 60)
                                    {
                                        decimal roeTmo = isLong
                                            ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100m
                                            : ((entryPrice - currentPrice) / entryPrice) * leverage * 100m;
                                        OnLog?.Invoke($"🔴 [ATR 버팀 한도초과] {symbol} {atrWhipsawMaxMinutes:F0}분 경과 → 최종 손절 (ROE={roeTmo:F1}%)");
                                        await ExecuteMarketClose(symbol,
                                            $"ATR Dual-Stop [WhipsawTimeout {atrWhipsawMaxMinutes:F0}min] ROE={roeTmo:F1}% ({currentPrice:F8})",
                                            token);
                                        break;
                                    }
                                }
                                else
                                {
                                    if (!fractalBrokenTime.HasValue)
                                    {
                                        fractalBrokenTime = DateTime.Now;
                                        OnLog?.Invoke(
                                            $"🚨 [FRACTAL BROKEN] {symbol} " +
                                            $"Fractal Low=({fractalStop:F8}) 돌파 → 거래량 필터 진입");
                                    }

                                    double fractalHeldSec = (DateTime.Now - fractalBrokenTime.Value).TotalSeconds;

                                    decimal currentVol = dualStopCandles?.LastOrDefault()?.Volume ?? 0m;
                                    decimal avgVol = (dualStopCandles != null && dualStopCandles.Count >= 20)
                                        ? (decimal)dualStopCandles.TakeLast(20).Average(c => (double)c.Volume)
                                        : 0m;
                                    bool volumeConfirmed = avgVol <= 0m || currentVol >= avgVol * 1.5m;

                                    decimal fireROE = isLong
                                        ? ((currentPrice - entryPrice) / entryPrice) * leverage * 100m
                                        : ((entryPrice - currentPrice) / entryPrice) * leverage * 100m;

                                    if (volumeConfirmed)
                                    {
                                        OnLog?.Invoke(
                                            $"🔴 [Dual-Stop 확정] {symbol} ATR+Fractal+Volume 트리플 확인 " +
                                            $"(ROE={fireROE:F1}%, Vol={currentVol:F2} ≥ Avg×1.5={avgVol * 1.5m:F2})");
                                        await ExecuteMarketClose(symbol,
                                            $"ATR 2.5x Dual-Stop [Confirmed] ROE={fireROE:F1}% ({currentPrice:F8})",
                                            token);
                                        break;
                                    }
                                    else if (fractalHeldSec >= fractalVolumeMaxMinutes * 60)
                                    {
                                        OnLog?.Invoke(
                                            $"🔴 [FakeOut 한도초과] {symbol} 거래량 미확인이나 " +
                                            $"{fractalVolumeMaxMinutes:F0}분 경과 → 손절 (ROE={fireROE:F1}%)");
                                        await ExecuteMarketClose(symbol,
                                            $"ATR 2.5x Dual-Stop [FakeOutTimeout] ROE={fireROE:F1}% ({currentPrice:F8})",
                                            token);
                                        break;
                                    }
                                    else
                                    {
                                        OnLog?.Invoke(
                                            $"⚠️ [FAKE-OUT FILTER] {symbol} Fractal 돌파했으나 거래량 미확인 " +
                                            $"({currentVol:F2} < {avgVol * 1.5m:F2}) → 대기 " +
                                            $"({fractalHeldSec:F0}s / {fractalVolumeMaxMinutes * 60:F0}s)");
                                    }
                                }
                            }
                        }
                    }

                    if (isSidewaysMode)
                    {
                        if (currentROE >= _settings.SidewaysTakeProfitRoe)
                        {
                            OnLog?.Invoke($"🟢 {symbol} 익절실행 [ROE]");
                            await ExecuteMarketClose(symbol, $"SIDEWAYS ROE 익절 달성 ({currentROE:F2}%)", token);
                            break;
                        }

                        bool customStopHit = customStopLossPrice > 0 &&
                            ((isLong && currentPrice <= customStopLossPrice) || (!isLong && currentPrice >= customStopLossPrice));

                        if (customStopHit)
                        {
                            OnLog?.Invoke($"🔴 {symbol} 손절실행 [SL]");
                            await ExecuteMarketClose(symbol, $"SIDEWAYS 커스텀 손절 ({currentPrice:F8})", token);
                            break;
                        }

                        bool customTpHit = customTakeProfitPrice > 0 &&
                            ((isLong && currentPrice >= customTakeProfitPrice) || (!isLong && currentPrice <= customTakeProfitPrice));

                        if (!partialTaken && customTpHit)
                        {
                            await ExecutePartialClose(symbol, 0.5m, token);
                            partialTaken = true;

                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    p.TakeProfitStep = 1;
                                    p.BreakevenPrice = entryPrice;
                                    p.StopLoss = entryPrice;
                                }
                            }

                            OnLog?.Invoke($"💰 {symbol} 익절실행 [부분]");
                        }

                        if (partialTaken)
                        {
                            bool breakEvenHit = (isLong && currentPrice <= entryPrice) || (!isLong && currentPrice >= entryPrice);
                            if (breakEvenHit)
                            {
                                OnLog?.Invoke($"[청산 트리거] {symbol} SIDEWAYS 잔여 본절 청산 | 현재가={currentPrice:F8}, 본절={entryPrice:F8}");
                                await ExecuteMarketClose(symbol, "SIDEWAYS 본절가 청산", token);
                                break;
                            }
                        }
                    }

                    if (!isSidewaysMode && isLong && !hybridDcaTaken && currentROE <= hybridDcaTriggerRoe)
                    {
                        if (TryShouldExecuteHybridLongDca(symbol, currentPrice, out string dcaReason))
                        {
                            OnAlert?.Invoke($"💧 {symbol} 하이브리드 DCA 시도 (ROE: {currentROE:F2}%) | {dcaReason}");
                            await ExecuteAverageDown(symbol, token);
                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    entryPrice = p.EntryPrice;
                                    hybridDcaTaken = p.IsAveragedDown;
                                }
                            }
                            hybridDcaDeferredLogged = false;
                            continue;
                        }

                        if (!hybridDcaDeferredLogged)
                        {
                            OnLog?.Invoke($"⏸️ {symbol} 하이브리드 DCA 보류 | {dcaReason}");
                            hybridDcaDeferredLogged = true;
                        }
                    }
                    else
                    {
                        hybridDcaDeferredLogged = false;
                    }

                    if (!squeezeDefenseReduced && holdingTime.TotalMinutes >= squeezeDefenseMinutes && currentROE < squeezeDefenseMaxRoe)
                    {
                        if (TryGetCurrentBbWidthPct(symbol, out decimal currentBbWidthPct) && currentBbWidthPct > 0m && currentBbWidthPct <= squeezeDefenseBbWidthThreshold)
                        {
                            await ExecutePartialClose(symbol, 0.5m, token);
                            partialTaken = true;
                            squeezeDefenseReduced = true;

                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    p.TakeProfitStep = Math.Max(p.TakeProfitStep, 1);
                                    p.BreakevenPrice = entryPrice;
                                    p.StopLoss = entryPrice;
                                }
                            }

                            OnLog?.Invoke($"📦 [스퀴즈 방어 축소] {symbol} {holdingTime.TotalMinutes:F0}분 경과 + BB폭 {currentBbWidthPct:F2}% → 50% 축소 후 본절 보호 전환");
                            continue;
                        }
                    }

                    if (!isSidewaysMode && pyramidingCount < maxPyramidingCount && DateTime.Now >= pyramidingCooldownUntil)
                    {
                        decimal stageTriggerRoe = pyramidingBaseTriggerRoe + (pyramidingCount * pyramidingTriggerStepRoe);

                        if (!pyramidingArmed && currentROE >= stageTriggerRoe)
                        {
                            pyramidingArmed = true;
                            pyramidingPullbackDetected = false;
                            pyramidingPeakPrice = currentPrice;
                            pyramidingPullbackPrice = currentPrice;
                            OnLog?.Invoke($"🔥 {symbol} {pyramidingCount + 1}/{maxPyramidingCount}차 불타기 대기 | ROE={currentROE:F2}% >= {stageTriggerRoe:F0}%");
                        }

                        if (pyramidingArmed)
                        {
                            if (isLong && currentPrice > pyramidingPeakPrice)
                                pyramidingPeakPrice = currentPrice;
                            else if (!isLong && (pyramidingPeakPrice <= 0m || currentPrice < pyramidingPeakPrice))
                                pyramidingPeakPrice = currentPrice;

                            decimal pullbackPct = 0m;
                            if (pyramidingPeakPrice > 0m)
                            {
                                pullbackPct = isLong
                                    ? ((pyramidingPeakPrice - currentPrice) / pyramidingPeakPrice) * 100m
                                    : ((currentPrice - pyramidingPeakPrice) / pyramidingPeakPrice) * 100m;
                            }

                            if (!pyramidingPullbackDetected && pullbackPct >= pyramidingPullbackThresholdPct)
                            {
                                pyramidingPullbackDetected = true;
                                pyramidingPullbackPrice = currentPrice;
                                OnLog?.Invoke($"↩️ {symbol} 불타기 눌림목 감지 | Peak={pyramidingPeakPrice:F8}, Pullback={pullbackPct:F2}%");
                            }

                            if (pyramidingPullbackDetected)
                            {
                                if (isLong && currentPrice < pyramidingPullbackPrice)
                                    pyramidingPullbackPrice = currentPrice;
                                else if (!isLong && currentPrice > pyramidingPullbackPrice)
                                    pyramidingPullbackPrice = currentPrice;

                                decimal reboundPct = 0m;
                                if (pyramidingPullbackPrice > 0m)
                                {
                                    reboundPct = isLong
                                        ? ((currentPrice - pyramidingPullbackPrice) / pyramidingPullbackPrice) * 100m
                                        : ((pyramidingPullbackPrice - currentPrice) / pyramidingPullbackPrice) * 100m;
                                }

                                if (reboundPct >= pyramidingReboundConfirmPct &&
                                    TryShouldHoldForProfitRun(symbol, currentPrice, isLong, out string pyramidTrendReason))
                                {
                                    OnAlert?.Invoke($"🔥 {symbol} {pyramidingCount + 1}차 불타기 실행 조건 충족 | 반등={reboundPct:F2}% | {pyramidTrendReason}");

                                    bool pyramidSuccess = await ExecutePyramidingAddOn(symbol, pyramidingAddRatio, maxPyramidingCount, token);
                                    if (pyramidSuccess)
                                    {
                                        lock (_posLock)
                                        {
                                            if (_activePositions.TryGetValue(symbol, out var p))
                                            {
                                                entryPrice = p.EntryPrice;
                                                pyramidingCount = p.PyramidCount;
                                                profitRunHoldActive = true;
                                                p.IsProfitRunHoldActive = true;
                                            }
                                        }

                                        decimal raisedBreakEven = isLong ? entryPrice * 1.005m : entryPrice * 0.995m;
                                        if (isLong)
                                        {
                                            protectiveStopPrice = Math.Max(protectiveStopPrice, raisedBreakEven);
                                        }
                                        else
                                        {
                                            protectiveStopPrice = protectiveStopPrice <= 0m
                                                ? raisedBreakEven
                                                : Math.Min(protectiveStopPrice, raisedBreakEven);
                                        }

                                        decimal safetyStop = await ApplySafetyStopAfterPyramidingAsync(
                                            symbol,
                                            isLong,
                                            entryPrice,
                                            customStopLossPrice,
                                            protectiveStopPrice,
                                            token);

                                        if (safetyStop > 0m)
                                        {
                                            customStopLossPrice = safetyStop;
                                            protectiveStopPrice = isLong
                                                ? Math.Max(protectiveStopPrice, safetyStop)
                                                : (protectiveStopPrice <= 0m ? safetyStop : Math.Min(protectiveStopPrice, safetyStop));
                                            hasCustomAbsoluteStop = true;
                                        }

                                        lock (_posLock)
                                        {
                                            if (_activePositions.TryGetValue(symbol, out var p))
                                            {
                                                p.BreakevenPrice = raisedBreakEven;
                                                if (p.StopLoss <= 0m || (isLong && p.StopLoss < raisedBreakEven) || (!isLong && p.StopLoss > raisedBreakEven))
                                                {
                                                    p.StopLoss = raisedBreakEven;
                                                }
                                            }
                                        }

                                        OnLog?.Invoke($"🛡️ {symbol} 불타기 후 방어 스탑 상향 | 단계={pyramidingCount}/{maxPyramidingCount} | BE={raisedBreakEven:F8}");

                                        pyramidingArmed = false;
                                        pyramidingPullbackDetected = false;
                                        pyramidingPeakPrice = currentPrice;
                                        pyramidingPullbackPrice = currentPrice;
                                        pyramidingCooldownUntil = DateTime.Now.AddSeconds(20);
                                        continue;
                                    }

                                    OnLog?.Invoke($"⚠️ {symbol} {pyramidingCount + 1}차 불타기 주문 실패");
                                    pyramidingCooldownUntil = DateTime.Now.AddSeconds(20);
                                }
                            }
                        }
                    }

                    // ═══════════════════════════════════════════════
                    // [펀딩비 누적 모니터링] 2시간 초과 포지션 실질ROE 계산
                    // 펀딩비(ROE기준) = (보유시간/8h) × 기본비율(0.01%/8h) × 레버리지 × 100
                    // ═══════════════════════════════════════════════
                    if (holdingTime.TotalMinutes >= FundingMonitorStartMinutes)
                    {
                        decimal estimatedFundingROECost = (decimal)(holdingTime.TotalHours / 8.0) * FundingRatePer8H * leverage * 100m;
                        decimal fundingAdjustedROE = currentROE - estimatedFundingROECost;

                        // 트레일링 미진입 + 실질ROE < 3% → 펀딩비가 수익 잠식, 청산
                        if (!tightTrailingActivated && fundingAdjustedROE < FundingExitAdjROEThreshold)
                        {
                            OnLog?.Invoke($"💸 [{symbol}] 펀딩비 누적 청산: 보유 {holdingTime.TotalMinutes:F0}분 | 추정누적={estimatedFundingROECost:F2}% | 현재ROE={currentROE:F2}% → 실질ROE={fundingAdjustedROE:F2}% < {FundingExitAdjROEThreshold}% → 청산");
                            await ExecuteMarketClose(symbol, $"FundingCost Exit ({holdingTime.TotalMinutes:F0}m, adjROE={fundingAdjustedROE:F2}%)", token);
                            break;
                        }

                        // 30분마다 펀딩비 현황 로그
                        if (fundingCostLastLogTime == null || (DateTime.Now - fundingCostLastLogTime.Value).TotalMinutes >= 30)
                        {
                            OnLog?.Invoke($"💸 {symbol} 펀딩비 추적 | 보유 {holdingTime.TotalMinutes:F0}분 | 누적추정={estimatedFundingROECost:F2}% ROE | 실질ROE={fundingAdjustedROE:F2}%");
                            fundingCostLastLogTime = DateTime.Now;
                        }
                    }

                    if (holdingTime.TotalMinutes >= maxHoldingMinutes && currentROE < timeoutExitRoeThreshold)
                    {
                        OnLog?.Invoke($"⏳ [시간 초과 종료] {symbol} {holdingTime.TotalMinutes:F0}분 경과 | 현재ROE={currentROE:F2}% < {timeoutExitRoeThreshold:F2}% → 추세 소멸로 포지션 정리");
                        await ExecuteMarketClose(symbol, $"TimeOut Exit ({holdingTime.TotalMinutes:F0}m, ROE {currentROE:F2}%)", token);
                        break;
                    }

                    if (currentROE >= profitRunTriggerRoe)
                    {
                        if (TryShouldHoldForProfitRun(symbol, currentPrice, isLong, out string profitRunReason, ignoreAiFloor: tightTrailingActivated))
                        {
                            if (!profitRunHoldActive || DateTime.Now >= nextProfitRunHoldLogTime)
                            {
                                OnLog?.Invoke($"🏃 {symbol} 익절 지연 유지 | 현재ROE={currentROE:F2}% | {profitRunReason}");
                                nextProfitRunHoldLogTime = DateTime.Now.AddSeconds(30);
                            }

                            profitRunHoldActive = true;
                            lock (_posLock)
                            {
                                if (_activePositions.TryGetValue(symbol, out var p))
                                {
                                    p.IsProfitRunHoldActive = true;
                                }
                            }

                            decimal holdBreakEven = isLong ? entryPrice * 1.0010m : entryPrice * 0.9990m;
                            if (isLong)
                            {
                                protectiveStopPrice = Math.Max(protectiveStopPrice, holdBreakEven);
                            }
                            else
                            {
                                protectiveStopPrice = protectiveStopPrice <= 0m
                                    ? holdBreakEven
                                    : Math.Min(protectiveStopPrice, holdBreakEven);
                            }
                        }
                        else
                        {
                            string exitTag = profitRunHoldActive ? "익절 지연 해제" : "익절 조건";
                            OnLog?.Invoke($"[청산 트리거] {symbol} {exitTag} 충족 | 현재ROE={currentROE:F2}% | {profitRunReason}");
                            await ExecuteMarketClose(symbol, $"Profit Run Exit ({profitRunReason}, ROE {currentROE:F2}%)", token);
                            break;
                        }
                    }
                    else if (currentROE >= _settings.TargetRoe && currentROE < profitRunTriggerRoe)
                    {
                        if (DateTime.Now >= nextProfitRunHoldLogTime)
                        {
                            OnLog?.Invoke($"⏳ {symbol} 익절 지연 대기 구간 | 현재ROE={currentROE:F2}% (최소 익절 {profitRunTriggerRoe:F2}% 미만)");
                            nextProfitRunHoldLogTime = DateTime.Now.AddSeconds(30);
                        }
                    }
                    decimal currentStopLossRoe = effectiveMajorStopLossRoe;

                    if (!hasCustomAbsoluteStop && currentROE <= -currentStopLossRoe)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 메이저 손절 조건 충족 | 방향={(isLong ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 손절ROE=-{currentStopLossRoe:F2}%");
                        await ExecuteMarketClose(symbol, $"메이저 손절 실행 (현재 {currentROE:F2}%, 기준 -{currentStopLossRoe:F2}%)", token);
                        break;
                    }

                    lock (_posLock)
                    {
                        if (!_activePositions.ContainsKey(symbol))
                        {
                            OnLog?.Invoke($"ℹ️ {symbol} 포지션이 외부에서 종료됨.");
                            break;
                        }
                    }

                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 모니터링 에러: {ex.Message}");
                    await Task.Delay(5000, token);
                }
            }
        }

        public async Task MonitorPumpPositionShortTerm(string symbol, decimal entryPrice, string strategyName, double atr, CancellationToken token)
        {
            bool isLongPosition;
            bool isPumpPosition;
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var pos))
                {
                    entryPrice = pos.EntryPrice;
                    isLongPosition = pos.IsLong;
                    isPumpPosition = pos.IsPumpStrategy;
                }
                else return;
            }

            OnLog?.Invoke($"🔍 {symbol} Pump 감시 시작 | 방향: {(isLongPosition ? "LONG" : "SHORT")} | 진입가: {entryPrice:F4}");

            DateTime startTime = DateTime.Now;
            decimal highestROE = -999m;
            decimal leverage = _settings.PumpLeverage;

            // [ElliottWave 3파 기반 익절/손절] 
            PositionInfo? elliotWavePos = null;
            decimal fib0618Level = 0;
            decimal wave1LowPrice = 0;
            decimal wave1HighPrice = 0;
            decimal fib0786Level = 0;
            decimal fib1618Target = 0;
            decimal fib2618Target = 0;
            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    elliotWavePos = p;
                    leverage = p.Leverage > 0 ? p.Leverage : leverage;
                    fib0618Level = p.Fib0618Level;
                    wave1LowPrice = p.Wave1LowPrice;
                    wave1HighPrice = p.Wave1HighPrice;
                    fib0786Level = p.Fib0786Level;
                    fib1618Target = p.Fib1618Target;
                    fib2618Target = p.TakeProfit;
                }
            }

            decimal stopLossROE = _settings.PumpStopLossRoe > 0 ? _settings.PumpStopLossRoe : GetCurrentStopLossRoe();  // PUMP 전용 손절 ROE (메이저 StopLossRoe와 독립)
            // ── [Meme Coin Mode] PUMP 전용 트레일링/본절 ──────────────────────────────
            decimal pumpBreakEvenRoe    = _settings.PumpBreakEvenRoe    > 0 ? _settings.PumpBreakEvenRoe    : 20.0m; // ROI +20% 시 본절
            decimal pumpTrailingStartRoe = _settings.PumpTrailingStartRoe > 0 ? _settings.PumpTrailingStartRoe : 40.0m; // ROI +40% 시 트레일링 시작
            decimal pumpTrailingGapRoe  = _settings.PumpTrailingGapRoe  > 0 ? _settings.PumpTrailingGapRoe  : 20.0m; // 최고점 대비 ROI 20% 하락 시 청산
            // ──────────────────────────────────────────────────────────────────────────
            decimal trailingStartROE = pumpTrailingStartRoe;
            decimal trailingDropROE  = pumpTrailingGapRoe;
            decimal averageDownROE = -5.0m;
            bool isBreakEvenTriggered = false;
            decimal partialTakeProfitROE = 25.0m;
            decimal pumpTp1Roe = _settings.PumpTp1Roe > 0 ? _settings.PumpTp1Roe : 25.0m;
            decimal pumpTp2Roe = _settings.PumpTp2Roe > 0 ? _settings.PumpTp2Roe : 50.0m;
            decimal pumpTimeStopMinutes = _settings.PumpTimeStopMinutes > 0 ? _settings.PumpTimeStopMinutes : 15.0m;
            decimal firstPartialCloseRatioPct = _settings.PumpFirstTakeProfitRatioPct > 0 ? _settings.PumpFirstTakeProfitRatioPct : 15.0m;
            decimal firstPartialCloseRatio = Math.Clamp(firstPartialCloseRatioPct / 100.0m, 0.05m, 0.95m);

            const decimal MemeBreathingRoe = 40.0m;
            // 1단계 25%로 앞당겨 1분봉 급등 초반에 수익 일부 확보
            decimal stairStep1TriggerRoe = _settings.PumpStairStep1Roe > 0 ? Math.Min(_settings.PumpStairStep1Roe, 25.0m) : 25.0m;
            decimal stairStep2TriggerRoe = _settings.PumpStairStep2Roe > 0 ? _settings.PumpStairStep2Roe : 80.0m;
            decimal stairStep3TriggerRoe = _settings.PumpStairStep3Roe > 0 ? _settings.PumpStairStep3Roe : 160.0m;
            const decimal StairStep1FloorRoe = 10.0m;  // 1단계 달성 후 최소 +10% 보호
            const decimal StairStep2FloorRoe = 40.0m;  // 2단계 달성 후 최소 +40% 보호 (50→40%)
            // [1분봉 급등 스파이크 감지] 이전 체크 대비 ROE가 15% 이상 급등 시 즉시 20% 부분익절
            decimal prevCheckROE = -999m;
            bool spikePartialTaken = false;

            if (stairStep2TriggerRoe <= stairStep1TriggerRoe)
                stairStep2TriggerRoe = stairStep1TriggerRoe + 10.0m;
            if (stairStep3TriggerRoe <= stairStep2TriggerRoe)
                stairStep3TriggerRoe = stairStep2TriggerRoe + 20.0m;

            int stairStep = 0;
            decimal lockedProfitFloorRoe = decimal.MinValue;
            decimal dynamicStopLossRoe = stopLossROE;

            if (pumpTp1Roe < 25.0m)
            {
                OnLog?.Invoke($"ℹ️ {symbol} PUMP 1차 익절 ROE가 {pumpTp1Roe:F1}%로 낮아 25.0%로 상향 적용");
                pumpTp1Roe = 25.0m;
            }

            partialTakeProfitROE = pumpTp1Roe;

            // [PUMP 변동성 방어] 고정 -60% 외에 구조/ATR 기반 느슨한 손절 ROE를 준비
            if (isLongPosition && entryPrice > 0)
            {
                decimal structureStop = 0m;
                if (wave1LowPrice > 0m)
                    structureStop = wave1LowPrice;
                if (fib0786Level > 0m)
                    structureStop = structureStop > 0m ? Math.Min(structureStop, fib0786Level) : fib0786Level;

                decimal atrStop = atr > 0 ? entryPrice - ((decimal)atr * 1.8m) : 0m;
                decimal chosenStop = 0m;
                if (structureStop > 0m && atrStop > 0m)
                    chosenStop = Math.Min(structureStop, atrStop);
                else if (structureStop > 0m)
                    chosenStop = structureStop;
                else if (atrStop > 0m)
                    chosenStop = atrStop;

                if (chosenStop > 0m && chosenStop < entryPrice)
                {
                    decimal lossPct = ((entryPrice - chosenStop) / entryPrice) * 100m;
                    decimal candidateRoe = lossPct * leverage;
                    if (candidateRoe > 0m)
                        dynamicStopLossRoe = Math.Max(stopLossROE, candidateRoe);

                    OnLog?.Invoke($"🛡️ {symbol} [PUMP 가변SL] 고정={stopLossROE:F1}% 동적={dynamicStopLossRoe:F1}% stop={chosenStop:F8}");
                }
            }

            if (atr > 0)
            {
                decimal targetPriceMove = (decimal)atr * 3.0m;
                decimal dynamicROE = (targetPriceMove / entryPrice) * leverage * 100;
                // [Meme Coin Mode] ATR 동적 값이 PumpTrailingStartRoe(40%) 아래로 내려가지 않도록 보정
                trailingStartROE = Math.Clamp(dynamicROE, pumpTrailingStartRoe, pumpTrailingStartRoe + 30.0m);
                OnLog?.Invoke($"🎯 {symbol} 목표가 동적 설정 (ATR:{atr:F2}): ROE {trailingStartROE:F1}% [PUMP floor={pumpTrailingStartRoe:F0}%]");
            }

            OnLog?.Invoke($"📋 {symbol} [Meme Coin Mode] SL={stopLossROE:F0}% BreakEven={pumpBreakEvenRoe:F0}% TrailStart={trailingStartROE:F0}% TrailGap={trailingDropROE:F0}%");

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token);

                decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                if (currentPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                    currentPrice = ticker.LastPrice;
                if (currentPrice == 0) continue;
                decimal priceChangePercent = isLongPosition
                    ? (currentPrice - entryPrice) / entryPrice * 100
                    : (entryPrice - currentPrice) / entryPrice * 100;
                decimal currentROE = priceChangePercent * leverage;

                // [1분봉 급등 스파이크 감지] 0.5초마다 체크 중 ROE가 15% 이상 단숨에 올라가면 즉시 20% 부분익절
                // 20~50% 1분봉 급등 시 수익 일부를 확실히 잠금
                if (!spikePartialTaken && prevCheckROE > -999m && currentROE > 0
                    && (currentROE - prevCheckROE) >= 15.0m && currentROE >= 20.0m)
                {
                    spikePartialTaken = true;
                    OnAlert?.Invoke($"⚡ {symbol} 급등 스파이크 감지! ROE {prevCheckROE:F1}%→{currentROE:F1}% (+{currentROE - prevCheckROE:F1}%) → 즉시 20% 부분익절");
                    await ExecutePartialClose(symbol, 0.20m, token);
                }
                prevCheckROE = currentROE;

                if ((DateTime.Now - startTime).TotalMinutes >= (double)pumpTimeStopMinutes && currentROE >= -2.0m && currentROE <= 2.0m)
                {
                    OnLog?.Invoke($"[청산 트리거] {symbol} PUMP 타임스탑 발동 | 기준={pumpTimeStopMinutes:F1}분, 경과={(DateTime.Now - startTime).TotalMinutes:F1}분, 현재ROE={currentROE:F2}%");
                    await ExecuteMarketClose(symbol, $"⏱️ 타임스탑 ({pumpTimeStopMinutes:F1}분 횡보)", token);
                    break;
                }

                OnTickerUpdate?.Invoke(symbol, 0m, (double)currentROE);

                bool isAveraged = false;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) isAveraged = p.IsAveragedDown; }

                bool firstTpDone = false;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                        firstTpDone = p.PartialProfitStage >= 1 || p.TakeProfitStep >= 1;
                }

                // [20배 PUMP 즉시 손절] 진입 후 다음 5분봉이 BB중단을 음봉으로 하향 돌파 마감 시 즉시 청산
                if (isPumpPosition && (DateTime.Now - startTime).TotalMinutes >= 5)
                {
                    try
                    {
                        if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                        {
                            List<IBinanceKline> snapshot;
                            lock (recentCandles)
                            {
                                snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전] 스냅샷 복사
                            }
                            
                            if (snapshot.Count < 20) continue;
                            var last = snapshot[snapshot.Count - 1];
                            var bb = IndicatorCalculator.CalculateBB(snapshot, 20, 2);
                            decimal mid = (decimal)bb.Mid;
                            bool bearishCloseBelowMid = (decimal)last.ClosePrice < (decimal)last.OpenPrice && (decimal)last.ClosePrice < mid;
                            if (bearishCloseBelowMid)
                            {
                                OnLog?.Invoke($"[청산 트리거] {symbol} 급등 추세 실패(BB중단 하향 음봉마감)");
                                await ExecuteMarketClose(symbol, "🛑 BB중단 하향 음봉마감 (즉시손절)", token);
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] BB중단 하향 분석 오류: {ex.Message}");
                    }
                }

                // ============================================================
                // [ElliottWave 3단계 부분익절] (Wave1High → Fib1618 → 잔량)
                // ============================================================
                if (elliotWavePos != null && isLongPosition &&
                    wave1HighPrice > 0 && fib1618Target > 0)
                {
                    // 1차 익절: 전고점(1.0) 도달 OR 설정 ROE 달성 시 15% 매도
                    bool tp1Condition = currentPrice >= wave1HighPrice || currentROE >= pumpTp1Roe;
                    if (elliotWavePos.PartialProfitStage == 0 && tp1Condition)
                    {
                        await ExecutePartialClose(symbol, firstPartialCloseRatio, token); // 15% 매도
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                p.PartialProfitStage = 1;
                                // [PUMP 추세홀딩] 1차 후 최소 수익선(ROE +5%) 확보
                                decimal lockRoe = 5.0m;
                                p.BreakevenPrice = entryPrice * (1m + (lockRoe / (leverage * 100m)));
                            }
                        }
                        string tp1Trigger = currentPrice >= wave1HighPrice ? $"Fib1.0(전고점) {wave1HighPrice:F8}" : $"ROE {pumpTp1Roe:F1}% 달성 ({currentROE:F1}%)";
                        OnAlert?.Invoke($"💰 {symbol} 1차 익절 ({firstPartialCloseRatio * 100m:F0}%) | 트리거: {tp1Trigger} | 생존선(ROE+5%) 설정");
                        OnLog?.Invoke($"✅ {symbol} 1차 익절 완료(Stage=1), 다음 목표: Fib1.618 {fib1618Target:F8} 또는 ROE {pumpTp2Roe:F1}%");
                    }

                    // 2차 익절: Fib1.618 도달 OR 설정 ROE 달성 시 30% 매도
                    bool tp2Condition = currentPrice >= fib1618Target || currentROE >= pumpTp2Roe;
                    if (elliotWavePos.PartialProfitStage == 1 && tp2Condition)
                    {
                        decimal secondRatio = 0.30m;
                        try
                        {
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                            {
                                List<IBinanceKline> snapshot;
                                lock (recentCandles)
                                {
                                    snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전]
                                }
                                if (snapshot.Count < 20) continue;
                                var rsiNow = IndicatorCalculator.CalculateRSI(snapshot, 14);
                                if (rsiNow >= 80) secondRatio = 0.40m; // 초과매수면 익절 강도 강화
                            }
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"⚠️ [{symbol}] ElliottWave 부분익절 RSI 계산 오류: {ex.Message}");
                        }

                        await ExecutePartialClose(symbol, secondRatio, token);
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                            {
                                p.PartialProfitStage = 2;
                                p.BreakevenPrice = wave1HighPrice > 0 ? wave1HighPrice : p.BreakevenPrice; // 마디가 추격 (Fib1.0로 상향)
                            }
                        }
                        string tp2Trigger = currentPrice >= fib1618Target ? $"Fib1.618 {fib1618Target:F8}" : $"ROE {pumpTp2Roe:F1}% 달성 ({currentROE:F1}%)";
                        OnAlert?.Invoke($"💰 {symbol} 2차 익절 ({secondRatio * 100m:F0}%) | 트리거: {tp2Trigger} | 스탑 상향: Fib1.0");
                        OnLog?.Invoke($"✅ {symbol} 2차 익절 완료(Stage=2), 잔량은 밴드라이딩/다이버전스 추격 관리");
                    }

                    // 최종 익절: Fib2.618 도달 시 잔량 정리
                    if (elliotWavePos.PartialProfitStage >= 2 && fib2618Target > 0 && currentPrice >= fib2618Target)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} Fib2.618({fib2618Target:F8}) 도달, 잔량 최종 정리");
                        await ExecuteMarketClose(symbol, "🎯 Fib2.618 최종 익절", token);
                        break;
                    }
                }

                // ============================================================
                // [ElliottWave 절대 손절선] (4가지)
                // ============================================================
                if (elliotWavePos != null && isLongPosition)
                {
                    bool shouldAbsoluteStop = false;
                    string stopReason = "";

                    // 1. Wave1LowPrice 이탈 (절대 손절선 1)
                    if (wave1LowPrice > 0 && currentPrice <= wave1LowPrice)
                    {
                        shouldAbsoluteStop = true;
                        stopReason = $"Wave1Low {wave1LowPrice:F8} 이탈";
                    }

                    // 2. Fib0.618/논리손절 이탈 (20배 핵심 손절선)
                    if (!shouldAbsoluteStop && fib0618Level > 0 && currentPrice <= fib0618Level)
                    {
                        shouldAbsoluteStop = true;
                        stopReason = $"Fib0.618 {fib0618Level:F8} 이탈";
                    }

                    if (!shouldAbsoluteStop && fib0786Level > 0 && currentPrice <= fib0786Level)
                    {
                        shouldAbsoluteStop = true;
                        stopReason = $"논리손절 {fib0786Level:F8} 이탈";
                    }

                    // 3. BB 하단 이탈 (절대 손절선 3) - 마켓 데이터 캐시에서 최신 캔들 조회
                    if (!shouldAbsoluteStop)
                    {
                        try
                        {
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                            {
                                List<IBinanceKline> snapshot;
                                lock (recentCandles)
                                {
                                    snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전]
                                }
                                if (snapshot.Count < 20) continue;
                                var bbAnalysis = IndicatorCalculator.CalculateBB(snapshot, 20, 2);
                                decimal bbLower = (decimal)bbAnalysis.Lower;
                                if (currentPrice < bbLower)
                                {
                                    shouldAbsoluteStop = true;
                                    stopReason = $"BB하단 {bbLower:F8} 이탈";
                                }
                            }
                        }
                        catch { /* BB 계산 실패 무시 */ }
                    }

                    // 4. 타임스탑: 15~25분 횡보 시 정리 (절대 손절선 4)
                    if (!shouldAbsoluteStop)
                    {
                        double timeCutMinutes = 20; // 20분 횡보
                        if ((DateTime.Now - startTime).TotalMinutes >= timeCutMinutes &&
                            Math.Abs(currentROE) <= 2.0m) // 수익/손실이 ±2% 이내
                        {
                            shouldAbsoluteStop = true;
                            stopReason = $"타임스탑 ({timeCutMinutes}분 횡보, ROE={currentROE:F2}%)";
                        }
                    }

                    if (shouldAbsoluteStop && elliotWavePos.PartialProfitStage < 2)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 절대 손절선 발동! | 이유: {stopReason} | 현재ROE: {currentROE:F2}%");
                        await ExecuteMarketClose(symbol, $"🛑 {stopReason}", token);
                        break;
                    }
                }

                // [수정] 물타기 포지션의 손절폭 완화: 20% → 12% (과도한 손로 방지)

                // ============================================================
                // [레벨업 스탑 + BB 중단 추격] 2차 익절 이후 (PartialProfitStage=2)
                // ============================================================
                if (elliotWavePos?.PartialProfitStage == 2 && wave1HighPrice > 0)
                {
                    bool shouldLevelUpStop = false;
                    string levelUpReason = "";

                    // 1. BB 중단(20EMA) 추격 스탑: 종가가 BB 중단 아래로 내려오면 즉시 청산
                    try
                    {
                        if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 20)
                        {
                            List<IBinanceKline> snapshot;
                            lock (recentCandles)
                            {
                                snapshot = recentCandles.TakeLast(20).ToList(); // [동시성 안전] 스냅샷 복사
                            }
                            
                            if (snapshot.Count < 20) continue;
                            var lastCandle = snapshot[snapshot.Count - 1];
                            decimal lastClose = (decimal)lastCandle.ClosePrice;

                            var bbAnalysis = IndicatorCalculator.CalculateBB(snapshot, 20, 2);
                            decimal bbMiddle = (decimal)bbAnalysis.Mid; // 20EMA

                            // 종가가 BB 중단 아래: 추세가 죽었다고 판단 → 즉시 청산
                            if (lastClose < bbMiddle)
                            {
                                shouldLevelUpStop = true;
                                levelUpReason = $"BB 중단({bbMiddle:F8}) 이탈 (종가: {lastClose:F8})";
                            }
                        }
                    }
                    catch { /* BB 계산 실패 무시 */ }

                    // 2. 레벨업 스탑: Wave1HighPrice(1차 익절가) 아래로 내려오면 청산
                    if (!shouldLevelUpStop && currentPrice < wave1HighPrice)
                    {
                        shouldLevelUpStop = true;
                        levelUpReason = $"레벨업 손절 (Wave1High {wave1HighPrice:F8} 이탈)";
                    }

                    // 3. 밴드라이딩 종료 + RSI 하락 다이버전스: 상단선 안쪽 복귀 + RSI 하락 시 전량 정리
                    if (!shouldLevelUpStop)
                    {
                        try
                        {
                            if (_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) && recentCandles.Count >= 30)
                            {
                                List<IBinanceKline> snapshot;
                                lock (recentCandles)
                                {
                                    snapshot = recentCandles.TakeLast(30).ToList(); // [동시성 안전]
                                }
                                
                                if (snapshot.Count < 30) continue;
                                var candles = snapshot;
                                if (candles.Count < 2)
                                {
                                    OnLog?.Invoke($"⚠️ {symbol} candles 부족 (BB 분석, Count: {candles.Count})");
                                    continue;
                                }
                                var bbNow = IndicatorCalculator.CalculateBB(candles, 20, 2);
                                var prevCandles = candles.Take(candles.Count - 1).ToList();
                                var bbPrev = IndicatorCalculator.CalculateBB(prevCandles, 20, 2);

                                decimal lastClose = (decimal)candles[^1].ClosePrice;
                                decimal prevClose = (decimal)candles[^2].ClosePrice;
                                bool upperBandBackInside = prevClose >= (decimal)bbPrev.Upper && lastClose < (decimal)bbNow.Upper;

                                double rsiNow = IndicatorCalculator.CalculateRSI(candles, 14);
                                double rsiPrev = IndicatorCalculator.CalculateRSI(prevCandles, 14);
                                bool bearishRsiDivergenceHint = lastClose > prevClose && rsiNow < rsiPrev;

                                if (upperBandBackInside && bearishRsiDivergenceHint)
                                {
                                    shouldLevelUpStop = true;
                                    levelUpReason = $"밴드라이딩 종료+RSI다이버전스 (RSI {rsiPrev:F1}->{rsiNow:F1})";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            OnLog?.Invoke($"⚠️ [{symbol}] 밴드라이딩/레벨업 스탑 분석 오류: {ex.Message}");
                        }
                    }

                    if (shouldLevelUpStop)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 레벨업/추격 스탑 발동! | 이유: {levelUpReason} | 현재ROE: {currentROE:F2}%");
                        await ExecuteMarketClose(symbol, $"🚀 {levelUpReason}", token);
                        break;
                    }
                }

                decimal effectiveStopLossRoe = dynamicStopLossRoe;
                if (isAveraged && firstTpDone)
                    effectiveStopLossRoe = Math.Min(effectiveStopLossRoe, 12.0m);

                // ============================================================
                // [본절가 스탑] ElliottWave 1차 익절 후 진입가로 손절 상향
                // ============================================================
                if (elliotWavePos?.BreakevenPrice > 0 && isLongPosition)
                {
                    if (currentPrice <= elliotWavePos.BreakevenPrice)
                    {
                        OnLog?.Invoke($"[청산 트리거] {symbol} 본절가 손절 발동 | BreakevenPrice: {elliotWavePos.BreakevenPrice:F8}, 현재가: {currentPrice:F8}");
                        await ExecuteMarketClose(symbol, "🛡️ 본절가 손절 (진입가 손절 상향)", token);
                        break;
                    }
                }

                // [PUMP 추세홀딩] 1차 익절 전에는 본절 이동 금지
                if (!isBreakEvenTriggered && firstTpDone && currentROE >= pumpBreakEvenRoe)
                {
                    isBreakEvenTriggered = true;
                    OnAlert?.Invoke($"🛡️ {symbol} Break Even 발동! (ROI {pumpBreakEvenRoe:F0}% 도달 → 손절라인 진입가로 이동, 절대 손실 없음)");
                    await TelegramService.Instance.SendBreakEvenReachedAsync(symbol, entryPrice);
                }

                // [PUMP 본절 버퍼] 진입가 정확히 0%에서 청산하면 되돌림에 바로 털림
                // → -10% ROE (0x20 기준 0.5% 가격) 여유를 줘서 PUMP 특유의 눌림을 버팀
                if (isBreakEvenTriggered)
                    effectiveStopLossRoe = Math.Min(effectiveStopLossRoe, 10.0m);

                // [초기 맷집] 1차 익절 전 + ROE 40% 미만은 느슨한 손절 유지
                if (!firstTpDone && currentROE < MemeBreathingRoe)
                    effectiveStopLossRoe = Math.Max(effectiveStopLossRoe, dynamicStopLossRoe);

                int currentTpStep = 0;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) currentTpStep = p.TakeProfitStep; }

                if (currentTpStep == 0 && currentROE >= partialTakeProfitROE)
                {
                    await ExecutePartialClose(symbol, firstPartialCloseRatio, token);
                    lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) p.TakeProfitStep = 1; }
                    OnAlert?.Invoke($"💰 {symbol} 1차 익절 ({firstPartialCloseRatio * 100m:F0}%) & 생존선 확보 (ROE: {currentROE:F2}%)");
                }

                if (!isAveraged && !isBreakEvenTriggered && currentROE <= averageDownROE)
                {
                    OnAlert?.Invoke($"💧 {symbol} 물타기 시도 (ROE: {currentROE:F2}%)");
                    await ExecuteAverageDown(symbol, token);
                    lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) entryPrice = p.EntryPrice; }
                    continue;
                }

                // [계단식 보호선] 50%/100% 구간에서 고정 수익선 확보
                if (currentROE >= stairStep1TriggerRoe && stairStep < 1)
                {
                    stairStep = 1;
                    lockedProfitFloorRoe = Math.Max(lockedProfitFloorRoe, StairStep1FloorRoe);
                    OnAlert?.Invoke($"🪜 {symbol} 계단식 스탑 1단계 | ROI {stairStep1TriggerRoe:F0}% → 보호선 ROE +{StairStep1FloorRoe:F0}%");
                }

                if (currentROE >= stairStep2TriggerRoe && stairStep < 2)
                {
                    stairStep = 2;
                    lockedProfitFloorRoe = Math.Max(lockedProfitFloorRoe, StairStep2FloorRoe);
                    OnAlert?.Invoke($"🪜 {symbol} 계단식 스탑 2단계 | ROI {stairStep2TriggerRoe:F0}% → 보호선 ROE +{StairStep2FloorRoe:F0}% (100% 먹고 50% 보존)");
                }

                if (currentROE >= stairStep3TriggerRoe && stairStep < 3)
                {
                    stairStep = 3;
                    trailingDropROE = 30.0m;
                    OnAlert?.Invoke($"🪜 {symbol} 계단식 스탑 3단계 | ROI {stairStep3TriggerRoe:F0}% → Moonshot Trailing Gap {trailingDropROE:F0}%");
                }

                if (lockedProfitFloorRoe > decimal.MinValue && currentROE <= lockedProfitFloorRoe)
                {
                    OnLog?.Invoke($"[청산 트리거] {symbol} 계단식 보호선 이탈 | 현재ROE={currentROE:F2}% <= 보호선={lockedProfitFloorRoe:F2}%");
                    await ExecuteMarketClose(symbol, $"🪜 계단식 스탑 발동 (ROE {lockedProfitFloorRoe:F1}% 보호선)", token);
                    break;
                }

                if (currentROE <= -effectiveStopLossRoe)
                {
                    string exitReason = isBreakEvenTriggered ? "🛡️ Break Even (본절)" : $"ROE 손절 (-{effectiveStopLossRoe}%)";
                    OnLog?.Invoke($"[청산 트리거] {symbol} 손절 조건 충족 | 방향={(isLongPosition ? "LONG" : "SHORT")}, 현재ROE={currentROE:F2}%, 기준ROE=-{effectiveStopLossRoe:F2}%, BreakEven={(isBreakEvenTriggered ? "ON" : "OFF")}");
                    await ExecuteMarketClose(symbol, exitReason, token);
                    break;
                }

                if (currentROE > highestROE) highestROE = currentROE;

                // ── [Meme Coin Mode] PUMP 트레일링: 고정 간격 pumpTrailingGapRoe 유지 ──────
                // 기존의 10%→4%, 15%→5% 동적 압축은 밈코인 변동성에 적합하지 않아 제거
                // 최고 ROI 대비 pumpTrailingGapRoe(20%) 이상 하락 시에만 청산
                // 예시: ROI 100% → 80%로 하락(20% drop) → 청산, 어깨에서 팔고 나옴
                // ──────────────────────────────────────────────────────────────────

                decimal effectiveTrailingStartRoe = Math.Max(trailingStartROE, stairStep3TriggerRoe);
                if (highestROE >= effectiveTrailingStartRoe)
                {
                    if (highestROE - currentROE >= trailingDropROE)
                    {
                        PositionInfo? pos = null;
                        lock (_posLock) { _activePositions.TryGetValue(symbol, out pos); }

                        if (pos != null && pos.TakeProfitStep == 0)
                        {
                            await ExecutePartialClose(symbol, firstPartialCloseRatio, token);
                            lock (_posLock) { if (_activePositions.ContainsKey(symbol)) _activePositions[symbol].TakeProfitStep = 1; }
                            OnAlert?.Invoke($"💰 {symbol} 1차 트레일링 익절 ({firstPartialCloseRatio * 100m:F0}%) | ROE: {currentROE:F1}%");
                            // [수정] highestROE 리셋 제거 - 남은 50%도 원래 최고가 기준으로 추적
                        }
                        else
                        {
                            OnLog?.Invoke($"[청산 트리거] {symbol} 트레일링 최종청산 조건 충족 | 방향={(isLongPosition ? "LONG" : "SHORT")}, 최고ROE={highestROE:F1}%, 현재ROE={currentROE:F1}%, 간격={trailingDropROE:F1}%");
                            await ExecuteMarketClose(symbol, $"ROE 트레일링 최종 익절 (최고:{highestROE:F1}% / 현재:{currentROE:F1}%)", token);
                            break;
                        }
                    }
                }

                lock (_posLock)
                {
                    if (!_activePositions.ContainsKey(symbol))
                    {
                        OnLog?.Invoke($"ℹ️ {symbol} Pump 감시 중 포지션이 외부에서 종료됨.");
                        break;
                    }
                }
            }
        }

        private bool TryShouldExecuteHybridLongDca(string symbol, decimal currentPrice, out string reason)
        {
            reason = string.Empty;

            PositionInfo? localPosition;
            lock (_posLock)
            {
                _activePositions.TryGetValue(symbol, out localPosition);
            }

            if (localPosition == null)
            {
                reason = "내부 포지션 정보 없음";
                return false;
            }

            if (!localPosition.IsLong)
            {
                reason = "SHORT 포지션은 하이브리드 DCA 비활성";
                return false;
            }

            if (!localPosition.IsHybridMidBandLongEntry)
            {
                reason = $"중단 롱 진입 포지션 아님 (EntryZone={localPosition.EntryZoneTag ?? string.Empty})";
                return false;
            }

            if (localPosition.AiConfidencePercent < 70f)
            {
                reason = $"AI 확률 {localPosition.AiConfidencePercent:F1}% < 70%";
                return false;
            }

            if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var recentCandles) || recentCandles.Count < 20)
            {
                reason = "5분봉 데이터 부족";
                return false;
            }

            List<IBinanceKline> recent20;
            lock (recentCandles)
            {
                recent20 = recentCandles.TakeLast(20).ToList(); // [동시성 안전]
            }
            if (recent20.Count < 20)
            {
                reason = "5분봉 스냅샷 부족";
                return false;
            }
            var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
            decimal bbLower = (decimal)bb.Lower;
            decimal bbUpper = (decimal)bb.Upper;
            decimal bbRange = bbUpper - bbLower;
            if (bbRange <= 0m)
            {
                reason = "BB 계산 불가";
                return false;
            }

            decimal bbPosition = (currentPrice - bbLower) / bbRange;
            if (bbPosition <= 0.10m)
            {
                reason = $"%B {bbPosition:P0} 하단 붕괴 구간에서는 DCA 금지";
                return false;
            }

            if (bbPosition > 0.15m)
            {
                reason = $"하단 DCA 대기 구간 아님 (%B {bbPosition:P0})";
                return false;
            }

            reason = $"하단 DCA 구간 도달 (%B {bbPosition:P0}, AI {localPosition.AiConfidencePercent:F1}%)";
            return true;
        }

        private bool TryEvaluateAiReversalExit(string symbol, decimal currentPrice, bool isLong, out string reason)
        {
            reason = "AI 재검증 데이터 부족";

            if (_aiPredictor == null)
                return false;

            var candle = BuildAiRecheckCandle(symbol, currentPrice);
            if (candle == null)
                return false;

            var prediction = _aiPredictor.Predict(candle);
            float confidencePct = prediction.Probability * 100f;
            bool oppositeSignal = isLong ? !prediction.Prediction : prediction.Prediction;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    p.AiScore = prediction.Score;
                    p.AiConfidencePercent = confidencePct;
                }
            }

            if (oppositeSignal && prediction.Probability >= 0.58f)
            {
                reason = $"반대 예측 감지 | 방향={(prediction.Prediction ? "LONG" : "SHORT")}, 확률={confidencePct:F1}%";
                return true;
            }

            reason = $"현재 방향 유지 | 예측={(prediction.Prediction ? "LONG" : "SHORT")}, 확률={confidencePct:F1}%";
            return false;
        }

        private CandleData? BuildAiRecheckCandle(string symbol, decimal currentPrice)
        {
            try
            {
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var candles) || candles.Count < 30)
                    return null;

                List<IBinanceKline> recent;
                lock (candles)
                {
                    recent = candles.TakeLast(120).ToList(); // [동시성 안전]
                }
                if (recent.Count < 20)
                {
                    OnLog?.Invoke($"⚠️ {symbol} recent 데이터 부족 (MarketStatus, Count: {recent.Count})");
                    return null;
                }
                var recent20 = recent.TakeLast(20).ToList();
                var latest = recent[^1];
                var previous = recent.Count >= 2 ? recent[^2] : latest;

                var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
                var macd = IndicatorCalculator.CalculateMACD(recent);
                double rsi = IndicatorCalculator.CalculateRSI(recent20, 14);
                double atr = IndicatorCalculator.CalculateATR(recent20, 14);
                double sma20 = IndicatorCalculator.CalculateSMA(recent, 20);
                double sma60 = IndicatorCalculator.CalculateSMA(recent, 60);
                double sma120 = IndicatorCalculator.CalculateSMA(recent, 120);

                decimal mid = (decimal)bb.Mid;
                decimal upper = (decimal)bb.Upper;
                decimal lower = (decimal)bb.Lower;
                decimal high = latest.HighPrice;
                decimal low = latest.LowPrice;
                decimal open = latest.OpenPrice;
                decimal close = currentPrice > 0 ? currentPrice : latest.ClosePrice;
                decimal range = Math.Max(high - low, 0.00000001m);
                float volumeRatio = 0f;
                var avgVolume = recent20.Take(recent20.Count - 1).Select(k => (double)k.Volume).DefaultIfEmpty((double)latest.Volume).Average();
                if (avgVolume > 0)
                    volumeRatio = (float)((double)latest.Volume / avgVolume);

                float volumeChangePct = 0f;
                if (previous.Volume > 0)
                    volumeChangePct = (float)(((double)latest.Volume - (double)previous.Volume) / (double)previous.Volume * 100.0);

                decimal bbWidthPct = mid > 0 ? ((upper - lower) / mid) * 100m : 0m;
                decimal priceToMidPct = mid > 0 ? ((close - mid) / mid) * 100m : 0m;
                decimal sma20Dec = (decimal)sma20;
                decimal priceToSma20Pct = sma20Dec > 0 ? ((close - sma20Dec) / sma20Dec) * 100m : 0m;
                decimal body = Math.Abs(close - open);
                decimal upperShadow = high - Math.Max(open, close);
                decimal lowerShadow = Math.Min(open, close) - low;

                return new CandleData
                {
                    Symbol = symbol,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = (float)latest.Volume,
                    OpenTime = latest.OpenTime,
                    CloseTime = latest.CloseTime,
                    RSI = (float)rsi,
                    BollingerUpper = (float)upper,
                    BollingerLower = (float)lower,
                    MACD = (float)macd.Macd,
                    MACD_Signal = (float)macd.Signal,
                    MACD_Hist = (float)macd.Hist,
                    ATR = (float)atr,
                    SMA_20 = (float)sma20,
                    SMA_60 = (float)sma60,
                    SMA_120 = (float)sma120,
                    Price_Change_Pct = open > 0 ? (float)(((close - open) / open) * 100m) : 0f,
                    Price_To_BB_Mid = (float)priceToMidPct,
                    BB_Width = (float)bbWidthPct,
                    Price_To_SMA20_Pct = (float)priceToSma20Pct,
                    Candle_Body_Ratio = (float)(body / range),
                    Upper_Shadow_Ratio = (float)(Math.Max(upperShadow, 0m) / range),
                    Lower_Shadow_Ratio = (float)(Math.Max(lowerShadow, 0m) / range),
                    Volume_Ratio = volumeRatio,
                    Volume_Change_Pct = volumeChangePct
                };
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} AI 재검증 캔들 생성 실패: {ex.Message}");
                return null;
            }
        }

        private bool TryGetCurrentBbWidthPct(string symbol, out decimal bbWidthPct)
        {
            bbWidthPct = 0m;

            try
            {
                if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var candles) || candles.Count < 20)
                    return false;

                List<IBinanceKline> recent20;
                lock (candles)
                {
                    recent20 = candles.TakeLast(20).ToList(); // [동시성 안전]
                }
                if (recent20.Count < 20)
                    return false;
                var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
                decimal mid = (decimal)bb.Mid;
                decimal upper = (decimal)bb.Upper;
                decimal lower = (decimal)bb.Lower;
                if (mid <= 0m || upper <= lower)
                    return false;

                bbWidthPct = ((upper - lower) / mid) * 100m;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryShouldHoldForProfitRun(string symbol, decimal currentPrice, bool isLong, out string reason, bool ignoreAiFloor = false)
        {
            reason = "추세 데이터 부족";

            PositionInfo? localPosition;
            lock (_posLock)
            {
                _activePositions.TryGetValue(symbol, out localPosition);
            }

            decimal confidenceScore = 0m;
            if (localPosition != null)
            {
                if (localPosition.AiConfidencePercent > 0)
                    confidenceScore = (decimal)localPosition.AiConfidencePercent;
                else
                    confidenceScore = NormalizeAiConfidence(localPosition.AiScore);
            }

            bool aiFloorIgnored = ignoreAiFloor && confidenceScore < 60m;

            if (!ignoreAiFloor && confidenceScore < 60m)
            {
                reason = $"AI 점수 부족({confidenceScore:F1}<60)";
                return false;
            }

            if (!_marketDataManager.KlineCache.TryGetValue(symbol, out var candles) || candles.Count < 20)
            {
                reason = "Kline 20봉 미만";
                return false;
            }

            List<IBinanceKline> recent20;
            lock (candles)
            {
                recent20 = candles.TakeLast(20).ToList();
            }

            if (recent20.Count < 20)
            {
                reason = "최근 20봉 확보 실패";
                return false;
            }

            var bb = IndicatorCalculator.CalculateBB(recent20, 20, 2);
            decimal bbMid = (decimal)bb.Mid;
            if (bbMid <= 0m)
            {
                reason = "BB 중단 계산 실패";
                return false;
            }

            var latest = recent20[^1];
            double avgVolume = recent20.Take(19).Select(k => (double)k.Volume).DefaultIfEmpty((double)latest.Volume).Average();
            decimal volumeRatio = avgVolume > 0d ? (decimal)((double)latest.Volume / avgVolume) : 0m;

            bool isMidBandHealthy = isLong ? currentPrice >= bbMid : currentPrice <= bbMid;
            bool hasVolumeSupport = volumeRatio >= 1.0m;

            string aiFloorNote = aiFloorIgnored ? " (트레일링 활성으로 AI 하한 무시)" : string.Empty;

            if (isMidBandHealthy && hasVolumeSupport)
            {
                reason = $"BB 중단 유지 + 거래량 {volumeRatio:F2}x + AI {confidenceScore:F1}{aiFloorNote}";
                return true;
            }

            reason = $"추세 약화(BB중단={(isMidBandHealthy ? "유지" : "이탈")}, 거래량={volumeRatio:F2}x){aiFloorNote}";
            return false;
        }

        private static decimal NormalizeAiConfidence(float rawScore)
        {
            if (rawScore <= 0f)
                return 0m;

            if (rawScore <= 1f)
                return (decimal)(rawScore * 100f);

            return (decimal)rawScore;
        }

        public async Task ExecuteMarketClose(string symbol, string reason, CancellationToken token)
        {
            MarkCloseStarted(symbol);
            try
            {
                OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);

                PositionInfo? localTrackedPosition = null;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var localPos) && Math.Abs(localPos.Quantity) > 0)
                    {
                        localTrackedPosition = new PositionInfo
                        {
                            Symbol = symbol,
                            IsLong = localPos.IsLong,
                            Quantity = Math.Abs(localPos.Quantity),
                            EntryPrice = localPos.EntryPrice,
                            Leverage = localPos.Leverage,
                            UnrealizedPnL = localPos.UnrealizedPnL,
                            Side = localPos.Side,
                            EntryTime = localPos.EntryTime,
                            AiScore = localPos.AiScore
                        };
                    }
                }

                var positions = await _exchangeService.GetPositionsAsync(ct: token);
                var position = positions.FirstOrDefault(p => p.Symbol == symbol && p.Quantity != 0);
                if (position == null)
                {
                    if (localTrackedPosition != null)
                    {
                        decimal fallbackExitPrice = 0m;
                        if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cachedTicker))
                            fallbackExitPrice = cachedTicker.LastPrice;

                        if (fallbackExitPrice <= 0)
                        {
                            try
                            {
                                fallbackExitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                            }
                            catch (Exception priceEx)
                            {
                                OnLog?.Invoke($"⚠️ {symbol} 이미 종료된 포지션의 종료가 재조회 실패: {priceEx.Message}");
                            }
                        }

                        if (fallbackExitPrice <= 0)
                            fallbackExitPrice = localTrackedPosition.EntryPrice;

                        decimal fallbackAbsQty = Math.Abs(localTrackedPosition.Quantity);
                        
                        // 순수 가격 차이
                        decimal fallbackRawPnl = localTrackedPosition.IsLong
                            ? (fallbackExitPrice - localTrackedPosition.EntryPrice) * fallbackAbsQty
                            : (localTrackedPosition.EntryPrice - fallbackExitPrice) * fallbackAbsQty;

                        // 거래 수수료 및 슬리피지 차감
                        decimal fallbackEntryFee = localTrackedPosition.EntryPrice * fallbackAbsQty * 0.0004m;
                        decimal fallbackExitFee = fallbackExitPrice * fallbackAbsQty * 0.0004m;
                        decimal fallbackSlippage = fallbackExitPrice * fallbackAbsQty * 0.0005m;
                        decimal fallbackPnl = fallbackRawPnl - fallbackEntryFee - fallbackExitFee - fallbackSlippage;

                        decimal fallbackPnlPercent = 0m;
                        if (localTrackedPosition.EntryPrice > 0 && fallbackAbsQty > 0)
                        {
                            fallbackPnlPercent = (fallbackPnl / (localTrackedPosition.EntryPrice * fallbackAbsQty)) * 100m * localTrackedPosition.Leverage;
                        }

                        var alreadyClosedLog = new TradeLog(
                            symbol,
                            localTrackedPosition.IsLong ? "SELL" : "BUY",
                            "ALREADY_CLOSED_SYNC",
                            fallbackExitPrice,
                            localTrackedPosition.AiScore,
                            DateTime.Now,
                            fallbackPnl,
                            fallbackPnlPercent)
                        {
                            EntryPrice = localTrackedPosition.EntryPrice,
                            ExitPrice = fallbackExitPrice,
                            Quantity = fallbackAbsQty,
                            EntryTime = localTrackedPosition.EntryTime == default ? DateTime.Now : localTrackedPosition.EntryTime,
                            ExitTime = DateTime.Now,
                            ExitReason = string.IsNullOrWhiteSpace(reason) ? "ALREADY_CLOSED_SYNC" : reason
                        };

                        bool dbSaved = await _dbManager.CompleteTradeAsync(alreadyClosedLog);
                        OnLog?.Invoke(dbSaved
                            ? $"📝 {symbol} 거래소상 이미 종료된 포지션을 TradeHistory에 보정 반영했습니다."
                            : $"⚠️ {symbol} 거래소상 이미 종료된 포지션이었지만 TradeHistory 보정 반영에 실패했습니다.");

                        decimal fallbackPriceMovePct = 0m;
                        if (localTrackedPosition.EntryPrice > 0m)
                        {
                            fallbackPriceMovePct = localTrackedPosition.IsLong
                                ? ((fallbackExitPrice - localTrackedPosition.EntryPrice) / localTrackedPosition.EntryPrice) * 100m
                                : ((localTrackedPosition.EntryPrice - fallbackExitPrice) / localTrackedPosition.EntryPrice) * 100m;
                        }

                        OnPositionClosedForAiLabel?.Invoke(
                            symbol,
                            alreadyClosedLog.EntryTime,
                            localTrackedPosition.EntryPrice,
                            localTrackedPosition.IsLong,
                            fallbackPriceMovePct,
                            alreadyClosedLog.ExitReason ?? "ALREADY_CLOSED_SYNC");

                        // [FIX] 청산 이력 갱신 이벤트
                        if (dbSaved) OnTradeHistoryUpdated?.Invoke();
                    }

                    CleanupPositionData(symbol);
                    return;
                }

                // [수정] 포지션 종료 전, 서버사이드 손절 주문 취소 + 실패 시 포지션 재확인
                string stopOrderId = string.Empty;
                lock (_posLock) { if (_activePositions.TryGetValue(symbol, out var p)) stopOrderId = p.StopOrderId; }

                if (!string.IsNullOrWhiteSpace(stopOrderId))
                {
                    bool cancelOk = false;
                    try { cancelOk = await _exchangeService.CancelOrderAsync(symbol, stopOrderId, token); }
                    catch (Exception cancelEx) { OnLog?.Invoke($"⚠️ [{symbol}] 손절 주문 취소 예외: {cancelEx.Message}"); }

                    if (!cancelOk)
                    {
                        // Cancel 실패 = Stop이 이미 체결됐을 가능성 → 포지션 재확인 후 청산 불필요 시 early return
                        OnLog?.Invoke($"⚠️ [{symbol}] 서버사이드 손절 주문 취소 실패 (StopOrderId={stopOrderId}) - 포지션 재확인 중");
                        try
                        {
                            var recheckList = await _exchangeService.GetPositionsAsync(ct: token);
                            var recheckPos = recheckList.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);
                            if (recheckPos == null)
                            {
                                OnLog?.Invoke($"[청산 스킵] {symbol} 서버사이드 Stop이 이미 체결되어 포지션 없음 - 청산 불필요");
                                CleanupPositionData(symbol);
                                return;
                            }
                            OnLog?.Invoke($"[청산 계속] {symbol} 포지션 잔존 확인 (수량={Math.Abs(recheckPos.Quantity)}) - 청산 주문 진행");
                            // position 갱신 (최신 수량으로)
                            position = recheckPos;
                        }
                        catch (Exception recheckEx)
                        {
                            OnLog?.Invoke($"⚠️ [{symbol}] 재확인 실패: {recheckEx.Message} - 원래 수량으로 청산 시도");
                        }
                    }
                }

                bool isLongPosition = position.IsLong;
                var side = isLongPosition ? "SELL" : "BUY";
                var absQty = Math.Abs(position.Quantity);
                var positionDirection = isLongPosition ? "LONG" : "SHORT";
                var expectedCloseDirection = isLongPosition ? "SELL(롱 청산)" : "BUY(숏 청산)";

                async Task LogExchangePositionSnapshotAsync(string phase)
                {
                    try
                    {
                        var latestPositions = await _exchangeService.GetPositionsAsync(ct: token);
                        var latest = latestPositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);

                        if (latest == null)
                        {
                            OnLog?.Invoke($"[{phase}] {symbol} 거래소 재조회 결과: 오픈 포지션 없음");
                            return;
                        }

                        string latestDirection = latest.IsLong ? "LONG" : "SHORT";
                        OnLog?.Invoke($"[{phase}] {symbol} 거래소 재조회 결과: {latestDirection}, 수량={Math.Abs(latest.Quantity)}, 진입가={latest.EntryPrice:F4}, 미실현PnL={latest.UnrealizedPnL:F2}");
                    }
                    catch (Exception snapshotEx)
                    {
                        OnLog?.Invoke($"[{phase}] {symbol} 거래소 재조회 실패: {snapshotEx.Message}");
                    }
                }

                bool IsLocalPositionClosed()
                {
                    lock (_posLock)
                    {
                        return !_activePositions.TryGetValue(symbol, out var localPos) || Math.Abs(localPos.Quantity) <= 0;
                    }
                }

                PositionInfo? GetLocalTrackedPositionSnapshot()
                {
                    lock (_posLock)
                    {
                        if (_activePositions.TryGetValue(symbol, out var localPos) && Math.Abs(localPos.Quantity) > 0)
                        {
                            return new PositionInfo
                            {
                                Symbol = symbol,
                                IsLong = localPos.IsLong,
                                Quantity = Math.Abs(localPos.Quantity),
                                EntryPrice = localPos.EntryPrice,
                                Leverage = localPos.Leverage,
                                UnrealizedPnL = localPos.UnrealizedPnL,
                                Side = localPos.Side,
                                EntryTime = localPos.EntryTime,
                                AiScore = localPos.AiScore
                            };
                        }

                        return null;
                    }
                }

                async Task<PositionInfo?> ConfirmRemainingPositionAsync()
                {
                    PositionInfo? lastSeen = null;
                    const int maxAttempts = 8;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (attempt == 1)
                            await Task.Delay(200, token);
                        else
                            await Task.Delay(300, token);

                        if (IsLocalPositionClosed())
                        {
                            OnLog?.Invoke($"[청산 재확인] {symbol} 웹소켓/내부 상태에서 포지션 종료 확인 ({attempt}/{maxAttempts})");
                            return null;
                        }

                        PositionInfo? open = null;
                        try
                        {
                            var latestPositions = await _exchangeService.GetPositionsAsync(ct: token);
                            open = latestPositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);
                        }
                        catch (Exception restEx)
                        {
                            OnLog?.Invoke($"⚠️ [청산 재확인] {symbol} REST 재조회 실패 ({attempt}/{maxAttempts}): {restEx.Message}");
                        }

                        if (open == null)
                        {
                            OnLog?.Invoke($"[청산 재확인] {symbol} REST 재조회에서 포지션 0 확인 ({attempt}/{maxAttempts})");

                            return null;
                        }

                        lastSeen = open;

                        if (attempt < maxAttempts)
                        {
                            string openDirection = open.IsLong ? "LONG" : "SHORT";
                            OnLog?.Invoke($"⏳ [청산 재확인] {symbol} {attempt}/{maxAttempts} | 잔존감지 {openDirection} {Math.Abs(open.Quantity)} -> 재조회 대기");
                        }
                    }

                    if (IsLocalPositionClosed())
                    {
                        OnLog?.Invoke($"[청산 재확인] {symbol} 최종 확인에서 웹소켓/내부 상태 기준 포지션 종료 확인");
                        return null;
                    }

                    return lastSeen ?? GetLocalTrackedPositionSnapshot();
                }

                if (absQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ [청산 스킵] {symbol} 수량이 0입니다. 포지션 데이터 정리.");
                    CleanupPositionData(symbol);
                    return;
                }

                // [FIX] 진입시간을 주문 전에 미리 가져옴 (DB 저장에 필요)
                DateTime entryTimeForHistory = DateTime.Now;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var localPosForTime) && localPosForTime.EntryTime != default)
                        entryTimeForHistory = localPosForTime.EntryTime;
                }

                OnLog?.Invoke($"[청산 검증] {symbol} 포지션={positionDirection}, 진입가={position.EntryPrice:F4}, 수량={absQty}, 청산주문={expectedCloseDirection}");
                
                // [개선] 청산 주문 재시도 로직 (최대 3회)
                bool success = false;
                int maxRetries = 3;
                for (int retry = 1; retry <= maxRetries; retry++)
                {
                    OnLog?.Invoke($"[청산 시도] {symbol} {side} {absQty} ({retry}/{maxRetries}) - 사유: {reason}");
                    try
                    {
                        success = await _exchangeService.PlaceOrderAsync(symbol, side, absQty, null, token, reduceOnly: true);
                    }
                    catch (Exception retryEx)
                    {
                        success = false;
                        OnLog?.Invoke($"⚠️ [{symbol}] 청산 주문 예외 ({retry}/{maxRetries}): {retryEx.Message}");
                    }

                    if (success)
                    {
                        if (retry > 1)
                        {
                            OnLog?.Invoke($"✅ [{symbol}] 청산 주문 성공 ({retry}회차 재시도에서 성공)");
                        }
                        break;
                    }

                    if (retry < maxRetries)
                    {
                        int delayMs = retry * 1000; // 1초, 2초, 3초 지연
                        OnLog?.Invoke($"⚠️ [{symbol}] 청산 주문 실패 ({retry}/{maxRetries}) - {delayMs}ms 후 재시도...");
                        await Task.Delay(delayMs, token);
                    }
                }

                if (!success)
                {
                    OnLog?.Invoke($"⚠️ [청산 폴백] {symbol} reduceOnly 청산 {maxRetries}회 실패 → 3초 주기 무한 재시도 모드 진입");
                    OnCloseIncompleteStatusChanged?.Invoke(symbol, true, $"API 오류 청산 재시도 중 (3초 주기, 0회)");
                    OnAlert?.Invoke($"❌ {symbol} 청산 API 오류. 3초 주기 자동 재시도를 시작합니다.");

                    try
                    {
                        await TelegramService.Instance.SendMessageAsync(
                            $"🚨 *[청산 API 오류]*\n" +
                            $"심볼: `{symbol}`\n" +
                            $"사유: {reason}\n" +
                            $"상태: 3초 주기 자동 재시도 시작 (초기 실패 {maxRetries}회)");
                    }
                    catch (Exception tgEx)
                    {
                        OnLog?.Invoke($"⚠️ [Telegram] 청산 API 오류 알림 전송 실패: {tgEx.Message}");
                    }

                    int continuousRetryCount = 0;
                    bool closeRecovered = false;

                    while (!closeRecovered)
                    {
                        continuousRetryCount++;

                        OnLog?.Invoke($"🔁 [청산 재시도 루프] {symbol} {continuousRetryCount}회차 | 3초 주기");

                        try
                        {
                            bool reduceRetrySuccess = await _exchangeService.PlaceOrderAsync(symbol, side, absQty, null, CancellationToken.None, reduceOnly: true);
                            if (reduceRetrySuccess)
                            {
                                OnLog?.Invoke($"✅ [청산 재시도 성공] {symbol} reduceOnly 재시도 주문 성공 ({continuousRetryCount}회차)");
                                closeRecovered = true;
                            }
                            else
                            {
                                bool emergencyCloseSuccess = await TryEmergencyCloseAfterRetryFailureAsync(symbol, CancellationToken.None);
                                if (emergencyCloseSuccess)
                                {
                                    OnLog?.Invoke($"✅ [청산 재시도 성공] {symbol} 긴급 폴백 청산 성공 ({continuousRetryCount}회차)");
                                    closeRecovered = true;
                                }
                            }
                        }
                        catch (Exception loopEx)
                        {
                            OnLog?.Invoke($"⚠️ [청산 재시도 루프 예외] {symbol} {continuousRetryCount}회차: {loopEx.Message}");
                        }

                        if (!closeRecovered)
                        {
                            OnCloseIncompleteStatusChanged?.Invoke(symbol, true, $"API 오류 청산 재시도 중 (3초 주기, {continuousRetryCount}회)");
                            await LogExchangePositionSnapshotAsync($"청산재시도중_{continuousRetryCount}");
                            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
                        }
                    }

                    OnAlert?.Invoke($"✅ {symbol} API 오류 청산 재시도 루프에서 복구되었습니다. 최종 청산 검증을 진행합니다.");

                    try
                    {
                        await TelegramService.Instance.SendMessageAsync(
                            $"✅ *[청산 복구 완료]*\n" +
                            $"심볼: `{symbol}`\n" +
                            $"결과: API 오류 재시도 루프에서 복구\n" +
                            $"재시도 횟수: {continuousRetryCount}회");
                    }
                    catch (Exception tgEx)
                    {
                        OnLog?.Invoke($"⚠️ [Telegram] 청산 복구 알림 전송 실패: {tgEx.Message}");
                    }
                }

                // [엄격모드] 주문 성공만으로는 청산 완료로 간주하지 않음 (거래소 포지션 0 확인 필수)
                PositionInfo? stillOpen = null;
                try
                {
                    stillOpen = await ConfirmRemainingPositionAsync();
                }
                catch (OperationCanceledException)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 청산 확인 중 취소됨 - DB 저장 시도 계속");
                    // CancellationToken 취소 시에도 DB 저장은 진행
                }

                if (stillOpen != null)
                {
                    string stillOpenDirection = stillOpen.IsLong ? "LONG" : "SHORT";

                    // [중요] 반대 방향 포지션 감지 및 재청산
                    bool isOppositeDirection = (isLongPosition && !stillOpen.IsLong) || (!isLongPosition && stillOpen.IsLong);

                    if (isOppositeDirection)
                    {
                        OnCloseIncompleteStatusChanged?.Invoke(symbol, true, $"반대 방향 포지션 잔존: {stillOpenDirection} {Math.Abs(stillOpen.Quantity)}");
                        OnAlert?.Invoke($"🚨🚨 {symbol} 반대 방향 포지션 생성됨! 원래={positionDirection}, 현재={stillOpenDirection}, 수량={Math.Abs(stillOpen.Quantity)} - 즉시 재청산 시도");
                        OnLog?.Invoke($"[긴급] {symbol} 반대 방향 포지션 감지! 원래 청산 방향={expectedCloseDirection}, 실제 포지션={stillOpenDirection}");
                        await LogExchangePositionSnapshotAsync("반대방향포지션감지");

                        // [FIX] 반대방향이라도 원래 포지션은 청산됐으므로 DB에 원래 청산 기록 저장
                        await SaveCloseTradeToDbAsync(symbol, side, position, isLongPosition, absQty, reason, entryTimeForHistory, CancellationToken.None);

                        // 재귀 호출로 반대 포지션 청산 (무한 루프 방지: reason에 "반대방향" 포함 시 1회만)
                        if (!reason.Contains("반대방향"))
                        {
                            await ExecuteMarketClose(symbol, $"반대방향 포지션 긴급 청산 (원인: {reason})", token);
                        }
                        else
                        {
                            OnAlert?.Invoke($"⚠️ {symbol} 반대방향 재청산 실패 - 무한루프 방지로 중단");
                        }
                        return;
                    }

                    // 같은 방향이면 기존 로직 (잔량 있음) - 하지만 부분 체결 가능성 → DB 저장
                    decimal closedQty = absQty - Math.Abs(stillOpen.Quantity);
                    if (closedQty > 0)
                    {
                        OnLog?.Invoke($"📝 {symbol} 부분 체결 감지: 원래={absQty}, 잔존={Math.Abs(stillOpen.Quantity)}, 체결={closedQty}");
                        await SaveCloseTradeToDbAsync(symbol, side, position, isLongPosition, closedQty, reason + " (부분체결)", entryTimeForHistory, CancellationToken.None);
                    }

                    OnCloseIncompleteStatusChanged?.Invoke(symbol, true, $"청산 주문 후 잔존 포지션: {stillOpenDirection} {Math.Abs(stillOpen.Quantity)}");
                    OnAlert?.Invoke($"🚨 {symbol} 청산 미완료! 주문은 성공했지만 포지션 잔존 ({stillOpenDirection} {Math.Abs(stillOpen.Quantity)})");
                    await LogExchangePositionSnapshotAsync("청산주문성공_잔존포지션확인");
                    return; // 잔존 포지션이 있으므로 내부 상태/DB 정리 금지
                }

                OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);
                OnLog?.Invoke($"[청산 검증완료] {symbol} 청산 후 거래소 오픈 포지션 0 확인");

                // [FIX] 헬퍼 메서드로 DB 저장 통합 (청산 완료 확인 후)
                await SaveCloseTradeToDbAsync(symbol, side, position, isLongPosition, absQty, reason, entryTimeForHistory, CancellationToken.None);

                OnAlert?.Invoke($"✅ {symbol} 청산 완료(검증됨): {reason}");

                // [수정] 모든 청산(손절/익절/지루함)에 30분 블랙리스트 적용
                // 프로그램 재시작 전에 재신호 방지 + 재시작 후 DB에서 복구
                _blacklistedSymbols[symbol] = DateTime.Now.AddMinutes(30);
                OnLog?.Invoke($"🚫 {symbol} 30분간 블랙리스트 등록 (청산 사유: {reason})");

                CleanupPositionData(symbol);
            }
            catch (Exception ex) { OnLog?.Invoke($"⚠️ {symbol} 종료 로직 에러: {ex.Message}"); }
            finally
            {
                MarkCloseFinished(symbol);
            }
        }

        private async Task<bool> TryEmergencyCloseAfterRetryFailureAsync(string symbol, CancellationToken token)
        {
            try
            {
                var positionResult = await _client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: token);
                if (!positionResult.Success || positionResult.Data == null)
                {
                    OnLog?.Invoke($"⚠️ [긴급 청산] {symbol} 포지션 재조회 실패: {positionResult.Error?.Message}");
                    return false;
                }

                var exchangePos = positionResult.Data.FirstOrDefault(p =>
                    string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Quantity) > 0);

                if (exchangePos == null)
                {
                    OnLog?.Invoke($"ℹ️ [긴급 청산] {symbol} 거래소 재조회 결과 이미 포지션이 없습니다.");
                    return true;
                }

                decimal closeQty = Math.Abs(exchangePos.Quantity);
                if (closeQty <= 0)
                {
                    OnLog?.Invoke($"ℹ️ [긴급 청산] {symbol} 청산 수량이 0으로 확인되었습니다.");
                    return true;
                }

                try
                {
                    var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: token);
                    if (exchangeInfo.Success && exchangeInfo.Data != null)
                    {
                        var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                        if (symbolData?.LotSizeFilter?.StepSize is decimal stepSize && stepSize > 0)
                        {
                            closeQty = Math.Floor(closeQty / stepSize) * stepSize;
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [긴급 청산] {symbol} 수량 정밀도 보정 중 예외: {ex.Message}");
                }

                if (closeQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ [긴급 청산] {symbol} 스텝 보정 후 청산 수량이 0입니다.");
                    return false;
                }

                bool isHedgeMode = false;
                try
                {
                    var positionModeResult = await _client.UsdFuturesApi.Account.GetPositionModeAsync(ct: token);
                    isHedgeMode = positionModeResult.Success && positionModeResult.Data.IsHedgeMode;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [긴급 청산] {symbol} 포지션 모드 조회 실패: {ex.Message}");
                }

                OrderSide closeSide = exchangePos.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
                PositionSide? hedgePositionSide = null;
                if (isHedgeMode)
                {
                    hedgePositionSide = exchangePos.Quantity > 0 ? PositionSide.Long : PositionSide.Short;
                }

                OnLog?.Invoke($"🚨 [긴급 청산 시도] {symbol} side={closeSide} qty={closeQty} hedge={isHedgeMode}");
                var closeResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    closeSide,
                    FuturesOrderType.Market,
                    closeQty,
                    reduceOnly: !isHedgeMode,
                    positionSide: hedgePositionSide,
                    ct: token);

                if (closeResult.Success)
                {
                    OnLog?.Invoke($"✅ [긴급 청산 성공] {symbol} 시장가 폴백 주문 접수 완료 (OrderId={closeResult.Data?.Id})");
                    return true;
                }

                OnLog?.Invoke($"❌ [긴급 청산 실패] {symbol} Code={closeResult.Error?.Code}, Msg={closeResult.Error?.Message}");
                return false;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [긴급 청산 예외] {symbol} {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// [FIX] 청산 시 DB TradeHistory 저장을 일원화하는 헬퍼 메서드.
        /// 모든 청산 경로(정상 청산, 반대방향 청산, 부분 체결 등)에서 호출.
        /// </summary>
        private async Task SaveCloseTradeToDbAsync(
            string symbol, string side, PositionInfo position,
            bool isLongPosition, decimal quantity, string reason,
            DateTime entryTimeForHistory, CancellationToken token)
        {
            try
            {
                // 청산가 조회 (캐시 우선, 폴백: 현재가 API)
                decimal exitPrice = 0;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached))
                {
                    exitPrice = cached.LastPrice;
                }

                if (exitPrice == 0)
                {
                    try
                    {
                        exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                    }
                    catch (Exception priceEx)
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 청산가 조회 실패, 진입가로 대체: {priceEx.Message}");
                        exitPrice = position.EntryPrice;
                    }
                }

                if (exitPrice == 0)
                    exitPrice = position.EntryPrice;

                decimal absQty = Math.Abs(quantity);
                
                // [수정] 순수 가격 차이로 PnL 계산
                decimal rawPnl = isLongPosition
                    ? (exitPrice - position.EntryPrice) * absQty
                    : (position.EntryPrice - exitPrice) * absQty;

                // [추가] 거래 수수료 차감 (Binance Taker 기준: 진입 0.04% + 청산 0.04%)
                decimal entryFee = position.EntryPrice * absQty * 0.0004m;
                decimal exitFee = exitPrice * absQty * 0.0004m;
                decimal totalFee = entryFee + exitFee;

                // [추가] 슬리피지 예상치 차감 (시장가 주문: 약 0.05% 예상)
                decimal estimatedSlippage = exitPrice * absQty * 0.0005m;

                // [최종] 실제 PnL = 순수 가격 차이 - 수수료 - 슬리피지
                decimal pnl = rawPnl - totalFee - estimatedSlippage;
                _riskManager.UpdatePnlAndCheck(pnl);

                // 수익률 계산
                decimal pnlPercent = 0;
                decimal positionLeverage = position.Leverage;
                lock (_posLock)
                {
                    if (positionLeverage <= 0 && _activePositions.TryGetValue(symbol, out var localPos) && localPos.Leverage > 0)
                        positionLeverage = localPos.Leverage;
                }

                if (position.EntryPrice > 0 && absQty > 0)
                {
                    pnlPercent = (pnl / (position.EntryPrice * absQty)) * 100 * positionLeverage;
                }

                decimal priceMovePct = 0m;
                if (position.EntryPrice > 0m)
                {
                    priceMovePct = isLongPosition
                        ? ((exitPrice - position.EntryPrice) / position.EntryPrice) * 100m
                        : ((position.EntryPrice - exitPrice) / position.EntryPrice) * 100m;
                }

                // [디버깅] 상세 내역 로그
                OnLog?.Invoke($"[PnL 계산] {symbol} | 순수: {rawPnl:F2} | 수수료: -{totalFee:F2} | 슬리피지: -{estimatedSlippage:F2} | 최종: {pnl:F2} ({pnlPercent:F2}%)");

                // DB에 매매 이력 저장
                var log = new TradeLog(symbol, side, "MarketClose", exitPrice, position.AiScore, DateTime.Now, pnl, pnlPercent)
                {
                    EntryPrice = position.EntryPrice,
                    ExitPrice = exitPrice,
                    Quantity = absQty,
                    ExitReason = reason,
                    EntryTime = entryTimeForHistory,
                    ExitTime = DateTime.Now
                };

                // [시뮬레이션 모드] DB 저장 스킵 — 가상 거래는 실제 이력에 누적되지 않아야 함
                if (!_isSimulationMode)
                {
                    bool dbSaved = await _dbManager.CompleteTradeAsync(log);
                    OnLog?.Invoke(dbSaved
                        ? $"✅ [DB 확인] {symbol} 청산 이력이 TradeHistory에 반영되었습니다. (PnL={pnl:F2}, ROE={pnlPercent:F2}%) | 사유: {reason}"
                        : $"⚠️ [DB 확인] {symbol} 청산은 완료됐지만 TradeHistory 반영에 실패했습니다. | 사유: {reason} | Side={side} | EntryTime={entryTimeForHistory:yyyy-MM-dd HH:mm:ss}");

                    if (!dbSaved)
                    {
                        OnLog?.Invoke($"   - DB 저장 실패 상세: Symbol={symbol}, Qty={absQty}, Entry={position.EntryPrice:F8}, Exit={exitPrice:F8}");
                        OnAlert?.Invoke($"⚠️ {symbol} DB 저장 실패: {reason} - 수동 확인 필요");
                    }

                    bool patternLabeled = await _dbManager.CompleteTradePatternSnapshotAsync(symbol, entryTimeForHistory, DateTime.Now, pnl, pnlPercent, reason);
                    if (patternLabeled)
                    {
                        string labelText = pnl > 0 ? "WIN" : "LOSS";
                        OnLog?.Invoke($"🧠 [Pattern] {symbol} 패턴 라벨 저장 완료: {labelText} | reason={reason}");
                    }
                }
                else
                {
                    OnLog?.Invoke($"🎮 [Simulation] {symbol} 청산 (PnL={pnl:F2}, ROE={pnlPercent:F2}%) | DB 저장 스킵");
                }

                OnLog?.Invoke($"[청산 확인] {symbol} 종료가={exitPrice:F4}, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                OnLog?.Invoke($"[종료] {symbol} | 수량: {absQty} | 사유: {reason}");

                // 텔레그램, 디스코드, 푸시 알림 통합 전송 (총 수익금 포함)
                decimal totalPnl = _riskManager?.DailyRealizedPnl ?? 0m;
                _ = NotificationService.Instance.NotifyProfitAsync(symbol, pnl, pnlPercent, totalPnl);

                // 청산 완료 이벤트 발생 (UI 자동 갱신 트리거)
                OnTradeHistoryUpdated?.Invoke();

                // AI 라벨링 이벤트 (부분체결은 제외)
                if (!reason.Contains("부분체결", StringComparison.OrdinalIgnoreCase))
                {
                    OnPositionClosedForAiLabel?.Invoke(
                        symbol,
                        entryTimeForHistory,
                        position.EntryPrice,
                        isLongPosition,
                        priceMovePct,
                        reason);
                }
            }
            catch (Exception dbEx)
            {
                OnLog?.Invoke($"❌ {symbol} DB 저장 중 치명적 오류: {dbEx.Message}");
            }
        }

        public async Task ExecutePartialClose(string symbol, decimal ratio, CancellationToken token)
        {
            MarkCloseStarted(symbol);
            try
            {
                if (ratio <= 0 || ratio >= 1)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 비율이 유효하지 않습니다: {ratio}");
                    return;
                }

                PositionInfo? localPosition;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out localPosition);
                }

                if (localPosition == null)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 실패: 내부 포지션 없음");
                    return;
                }

                var side = localPosition.IsLong ? "SELL" : "BUY";
                var currentQty = Math.Abs(localPosition.Quantity);
                var closeQty = Math.Round(currentQty * ratio, 6, MidpointRounding.AwayFromZero);

                if (closeQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 수량 계산 실패: 현재={currentQty}, 비율={ratio}");
                    return;
                }

                OnLog?.Invoke($"[부분청산 시도] {symbol} {side} {closeQty} ({ratio:P0})");
                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, closeQty, null, token, reduceOnly: true);

                if (!success)
                {
                    OnAlert?.Invoke($"❌ {symbol} 부분청산 실패 (거래소 주문 실패)");
                    return;
                }

                decimal remainingQty = Math.Max(0, currentQty - closeQty);
                try
                {
                    PositionInfo? confirmedPosition = null;
                    for (int attempt = 1; attempt <= 4; attempt++)
                    {
                        await Task.Delay(attempt == 1 ? 200 : 300, token);

                        var latestPositions = await _exchangeService.GetPositionsAsync(ct: token);
                        confirmedPosition = latestPositions.FirstOrDefault(p => p.Symbol == symbol && Math.Abs(p.Quantity) > 0);

                        if (confirmedPosition == null)
                        {
                            remainingQty = 0;
                            break;
                        }

                        decimal exchangeQty = Math.Abs(confirmedPosition.Quantity);
                        if (exchangeQty < currentQty - 0.000001m)
                        {
                            remainingQty = exchangeQty;
                            break;
                        }
                    }
                }
                catch (Exception confirmEx)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 부분청산 재확인 실패, 계산 수량으로 반영: {confirmEx.Message}");
                }

                decimal actualClosedQty = Math.Max(0, currentQty - remainingQty);
                if (actualClosedQty <= 0.000001m)
                {
                    actualClosedQty = closeQty;
                    remainingQty = Math.Max(0, currentQty - actualClosedQty);
                }

                decimal exitPrice = 0;
                if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached))
                {
                    exitPrice = cached.LastPrice;
                }

                if (exitPrice == 0)
                {
                    exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                }

                // 순수 가격 차이
                decimal rawPnl = localPosition.IsLong
                    ? (exitPrice - localPosition.EntryPrice) * actualClosedQty
                    : (localPosition.EntryPrice - exitPrice) * actualClosedQty;

                // 거래 수수료 및 슬리피지 차감
                decimal entryFee = localPosition.EntryPrice * actualClosedQty * 0.0004m;
                decimal exitFee = exitPrice * actualClosedQty * 0.0004m;
                decimal estimatedSlippage = exitPrice * actualClosedQty * 0.0005m;
                decimal pnl = rawPnl - entryFee - exitFee - estimatedSlippage;

                _riskManager.UpdatePnlAndCheck(pnl);

                decimal pnlPercent = 0;
                if (localPosition.EntryPrice > 0 && actualClosedQty > 0)
                {
                    pnlPercent = (pnl / (localPosition.EntryPrice * actualClosedQty)) * 100 * localPosition.Leverage;
                }

                DateTime now = DateTime.Now;
                if (remainingQty <= 0.000001m)
                {
                    var closeLog = new TradeLog(
                        symbol,
                        side,
                        "PartialCloseFullFill",
                        exitPrice,
                        localPosition.AiScore,
                        now,
                        pnl,
                        pnlPercent)
                    {
                        EntryPrice = localPosition.EntryPrice,
                        ExitPrice = exitPrice,
                        Quantity = currentQty,
                        EntryTime = localPosition.EntryTime,
                        ExitTime = now,
                        ExitReason = "PartialClose_FullClose"
                    };

                    if (!_isSimulationMode)
                    {
                        try
                        {
                            await _dbManager.CompleteTradeAsync(closeLog);
                            OnTradeHistoryUpdated?.Invoke();
                        }
                        catch (Exception dbEx)
                        {
                            OnLog?.Invoke($"⚠️ {symbol} 부분청산→전량청산 DB 저장 실패: {dbEx.Message}");
                        }
                    }

                    OnAlert?.Invoke($"ℹ️ {symbol} 부분청산 주문이 전량 체결되어 포지션 종료 처리됨");
                    CleanupPositionData(symbol);
                    OnLog?.Invoke($"✅ {symbol} 부분청산 주문 전량 체결: 청산={currentQty}, 잔여=0, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                    return;
                }

                var partialLog = new TradeLog(
                    symbol,
                    side,
                    "PartialClose",
                    exitPrice,
                    localPosition.AiScore,
                    now,
                    pnl,
                    pnlPercent)
                {
                    EntryPrice = localPosition.EntryPrice,
                    ExitPrice = exitPrice,
                    Quantity = actualClosedQty,
                    EntryTime = localPosition.EntryTime,
                    ExitTime = now,
                    ExitReason = "PartialClose"
                };

                if (!_isSimulationMode)
                {
                    try
                    {
                        await _dbManager.RecordPartialCloseAsync(partialLog);
                        OnTradeHistoryUpdated?.Invoke();
                    }
                    catch (Exception dbEx)
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 부분청산 DB 저장 실패: {dbEx.Message}");
                    }
                }

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                    {
                        p.Quantity = p.IsLong ? remainingQty : -remainingQty;
                    }
                }

                OnLog?.Invoke($"✅ {symbol} 부분청산 완료: 청산={actualClosedQty}, 잔여={remainingQty}, PnL={pnl:F2}, ROE={pnlPercent:F2}%");
                OnPositionStatusUpdate?.Invoke(symbol, remainingQty > 0, 0);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 부분청산 예외: {ex.Message}");
            }
            finally
            {
                MarkCloseFinished(symbol);
            }
        }

        /// <summary>
        /// [v2.1.18] 지표 기반 익절 신호를 위한 기술적 데이터 구축
        /// MarketDataManager와 AdvancedIndicators에서 필요한 모든 지표를 수집
        /// </summary>
        private TechnicalData? BuildTechnicalDataForExitSignal(string symbol, decimal currentPrice, decimal entryPrice, bool isLong)
        {
            try
            {
                var tech = new TechnicalData
                {
                    CurrentPrice = currentPrice,
                    EntryPrice = entryPrice,
                    HighestPrice = currentPrice,  // 실제로는 MarketDataManager에서 조회
                    LowestPrice = currentPrice,   // 실제로는 MarketDataManager에서 조회
                    Atr = 0.001m, // 기본값 (실제 구현 시 지표 계산기에서 제공)
                    AtrMultiplier = 1.5m,
                };

                // [TODO] 다음 정보는 AdvancedIndicators 클래스에서 읽어야 함:
                // - tech.IsWave5 (엘리엇 파동 분석)
                // - tech.Rsi (RSI 계산)
                // - tech.MacdLine, SignalLine, MacdHistogram (MACD 계산)
                // - tech.UpperBand, MidBand, LowerBand (볼린저 밴드 계산)
                // - tech.Fibo1618, Fibo2618 (피보나치 계산)

                // 현재는 MarketDataManager를 통해 기본 데이터만 수집
                // (추후 AdvancedIndicators와의 통합 시 완성)

                return tech;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 지표 데이터 수집 실패: {ex.Message}");
                return null;
            }
        }

        public async Task ExecuteAverageDown(string symbol, CancellationToken token)
        {
            try
            {
                TradeLog? averagedLog = null;

                PositionInfo? localPosition;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out localPosition);
                }

                if (localPosition == null)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 물타기 실패: 내부 포지션 없음");
                    return;
                }

                if (localPosition.IsAveragedDown)
                {
                    OnLog?.Invoke($"ℹ️ {symbol} 물타기는 이미 1회 수행됨");
                    return;
                }

                var addQty = Math.Round(Math.Abs(localPosition.Quantity) * 0.5m, 6, MidpointRounding.AwayFromZero);
                if (addQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 물타기 수량 계산 실패");
                    return;
                }

                var side = localPosition.IsLong ? "BUY" : "SELL";
                OnLog?.Invoke($"[물타기 시도] {symbol} {side} {addQty} (기존의 50%)");

                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, addQty, null, token, reduceOnly: false);
                if (!success)
                {
                    OnAlert?.Invoke($"❌ {symbol} 물타기 실패 (거래소 주문 실패)");
                    return;
                }

                var marketPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                if (marketPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                    marketPrice = ticker.LastPrice;
                if (marketPrice <= 0)
                {
                    marketPrice = localPosition.EntryPrice;
                }

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                    {
                        var oldQtyAbs = Math.Abs(p.Quantity);
                        var totalQty = oldQtyAbs + addQty;
                        if (totalQty > 0)
                        {
                            p.EntryPrice = ((p.EntryPrice * oldQtyAbs) + (marketPrice * addQty)) / totalQty;
                        }
                        p.Quantity = p.IsLong ? totalQty : -totalQty;
                        p.IsAveragedDown = true;

                        averagedLog = new TradeLog(
                            symbol,
                            p.IsLong ? "BUY" : "SELL",
                            "AverageDown",
                            p.EntryPrice,
                            p.AiScore,
                            DateTime.Now,
                            0,
                            0)
                        {
                            EntryPrice = p.EntryPrice,
                            Quantity = Math.Abs(p.Quantity),
                            EntryTime = p.EntryTime == default ? DateTime.Now : p.EntryTime
                        };
                    }
                }

                if (averagedLog != null)
                {
                    try
                    {
                        bool dbSaved = await _dbManager.UpsertTradeEntryAsync(averagedLog);
                        if (!dbSaved)
                            OnLog?.Invoke($"⚠️ {symbol} 물타기 후 TradeHistory 진입 반영 실패");
                    }
                    catch (Exception dbEx)
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 물타기 DB 저장 예외: {dbEx.Message}");
                    }
                }

                OnAlert?.Invoke($"💧 {symbol} 물타기 완료: +{addQty} @ {marketPrice:F4}");
                OnLog?.Invoke($"✅ {symbol} 물타기 반영 완료");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 물타기 예외: {ex.Message}");
            }
        }

        private async Task<decimal> GetAtrGapFrom15mAsync(string symbol, CancellationToken token)
        {
            try
            {
                var klines = await _exchangeService.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, 60, token);
                if (klines == null || klines.Count < 15)
                    return 0m;

                double atr = IndicatorCalculator.CalculateATR(klines.ToList(), 14);
                if (atr <= 0)
                    return 0m;

                return (decimal)atr * 2.5m;
            }
            catch
            {
                return 0m;
            }
        }

        private async Task<decimal> ApplySafetyStopAfterPyramidingAsync(
            string symbol,
            bool isLong,
            decimal entryPrice,
            decimal customStopLossPrice,
            decimal protectiveStopPrice,
            CancellationToken token)
        {
            decimal atrGap = await GetAtrGapFrom15mAsync(symbol, token);
            if (atrGap <= 0m)
            {
                OnLog?.Invoke($"⚠️ {symbol} 15분봉 ATR 2.5x 계산 실패로 불타기 안전 스탑 갱신을 건너뜁니다.");
                return 0m;
            }

            decimal highestPrice = entryPrice;
            decimal lowestPrice = entryPrice;
            decimal quantity = 0m;
            decimal currentStoredStop = 0m;
            string existingStopOrderId = string.Empty;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                {
                    highestPrice = p.HighestPrice > 0m ? p.HighestPrice : entryPrice;
                    lowestPrice = p.LowestPrice > 0m ? p.LowestPrice : entryPrice;
                    quantity = Math.Abs(p.Quantity);
                    currentStoredStop = p.StopLoss;
                    existingStopOrderId = p.StopOrderId;
                }
            }

            decimal baselineStop = currentStoredStop > 0m
                ? currentStoredStop
                : (customStopLossPrice > 0m ? customStopLossPrice : protectiveStopPrice);

            if (baselineStop <= 0m)
                baselineStop = entryPrice;

            decimal atrHybridStop = isLong
                ? Math.Max(baselineStop, highestPrice - atrGap)
                : Math.Min(baselineStop, lowestPrice + atrGap);

            decimal safetyLockStop = isLong
                ? entryPrice * 1.005m
                : entryPrice * 0.995m;

            decimal newStop = isLong
                ? Math.Max(atrHybridStop, safetyLockStop)
                : Math.Min(atrHybridStop, safetyLockStop);

            bool shouldUpdate = isLong ? newStop > baselineStop : newStop < baselineStop;
            if (!shouldUpdate)
                return baselineStop;

            if (quantity > 0m)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(existingStopOrderId))
                    {
                        await _exchangeService.CancelOrderAsync(symbol, existingStopOrderId, token);
                    }

                    var (stopPlaced, newStopOrderId) = await _exchangeService.PlaceStopOrderAsync(
                        symbol,
                        isLong ? "SELL" : "BUY",
                        quantity,
                        newStop,
                        token);

                    if (stopPlaced)
                    {
                        lock (_posLock)
                        {
                            if (_activePositions.TryGetValue(symbol, out var p))
                                p.StopOrderId = newStopOrderId;
                        }
                    }
                    else
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 불타기 안전 스탑 서버 주문 재설정 실패 (로컬 스탑만 갱신)");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 불타기 안전 스탑 서버 주문 갱신 오류: {ex.Message}");
                }
            }

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var p))
                    p.StopLoss = newStop;
            }

            OnLog?.Invoke($"🛡️ {symbol} 불타기 안전잠금 스탑 갱신 | ATRx2.5={atrGap:F8} | NewSL={newStop:F8} | Entry={entryPrice:F8}");
            return newStop;
        }

        public async Task<bool> ExecutePyramidingAddOn(string symbol, decimal addRatio, int maxPyramidCount, CancellationToken token)
        {
            try
            {
                TradeLog? addOnLog = null;

                if (addRatio <= 0m || addRatio > 1m)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 불타기 비율이 유효하지 않습니다: {addRatio}");
                    return false;
                }

                if (maxPyramidCount <= 0)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 최대 불타기 횟수가 유효하지 않습니다: {maxPyramidCount}");
                    return false;
                }

                PositionInfo? localPosition;
                lock (_posLock)
                {
                    _activePositions.TryGetValue(symbol, out localPosition);
                }

                if (localPosition == null)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 불타기 실패: 내부 포지션 없음");
                    return false;
                }

                if (localPosition.PyramidCount >= maxPyramidCount)
                {
                    OnLog?.Invoke($"ℹ️ {symbol} 불타기 최대 횟수 도달 ({localPosition.PyramidCount}/{maxPyramidCount})");
                    return false;
                }

                decimal baseQty = localPosition.InitialQuantity > 0m
                    ? localPosition.InitialQuantity
                    : Math.Abs(localPosition.Quantity);

                decimal addQty = Math.Round(baseQty * addRatio, 6, MidpointRounding.AwayFromZero);
                if (addQty <= 0)
                {
                    OnLog?.Invoke($"⚠️ {symbol} 불타기 수량 계산 실패");
                    return false;
                }

                string side = localPosition.IsLong ? "BUY" : "SELL";
                OnLog?.Invoke($"[불타기 시도] {symbol} {side} {addQty} (초기수량의 {addRatio:P0}, 현재단계 {localPosition.PyramidCount + 1}/{maxPyramidCount})");

                bool success = await _exchangeService.PlaceOrderAsync(symbol, side, addQty, null, token, reduceOnly: false);
                if (!success)
                {
                    OnAlert?.Invoke($"❌ {symbol} 불타기 실패 (거래소 주문 실패)");
                    return false;
                }

                decimal marketPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
                if (marketPrice == 0 && _marketDataManager.TickerCache.TryGetValue(symbol, out var ticker))
                    marketPrice = ticker.LastPrice;
                if (marketPrice <= 0)
                    marketPrice = localPosition.EntryPrice;

                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                    {
                        decimal oldQtyAbs = Math.Abs(p.Quantity);
                        decimal totalQty = oldQtyAbs + addQty;
                        if (totalQty > 0)
                        {
                            p.EntryPrice = ((p.EntryPrice * oldQtyAbs) + (marketPrice * addQty)) / totalQty;
                        }

                        p.InitialQuantity = p.InitialQuantity > 0m ? p.InitialQuantity : oldQtyAbs;
                        p.Quantity = p.IsLong ? totalQty : -totalQty;
                        p.PyramidCount += 1;
                        p.IsPyramided = p.PyramidCount > 0;
                        if (p.HighestPrice <= 0m)
                            p.HighestPrice = marketPrice;
                        if (p.LowestPrice <= 0m)
                            p.LowestPrice = marketPrice;

                        addOnLog = new TradeLog(
                            symbol,
                            p.IsLong ? "BUY" : "SELL",
                            "PyramidingAddOn",
                            p.EntryPrice,
                            p.AiScore,
                            DateTime.Now,
                            0,
                            0)
                        {
                            EntryPrice = p.EntryPrice,
                            Quantity = Math.Abs(p.Quantity),
                            EntryTime = p.EntryTime == default ? DateTime.Now : p.EntryTime
                        };
                    }
                }

                if (addOnLog != null)
                {
                    try
                    {
                        bool dbSaved = await _dbManager.UpsertTradeEntryAsync(addOnLog);
                        if (!dbSaved)
                            OnLog?.Invoke($"⚠️ {symbol} 불타기 후 TradeHistory 진입 반영 실패");
                    }
                    catch (Exception dbEx)
                    {
                        OnLog?.Invoke($"⚠️ {symbol} 불타기 DB 저장 예외: {dbEx.Message}");
                    }
                }

                int updatedPyramidCount = 0;
                lock (_posLock)
                {
                    if (_activePositions.TryGetValue(symbol, out var p))
                        updatedPyramidCount = p.PyramidCount;
                }

                OnAlert?.Invoke($"🔥 {symbol} 불타기 완료: +{addQty} @ {marketPrice:F4} (단계 {updatedPyramidCount}/{maxPyramidCount})");
                OnLog?.Invoke($"✅ {symbol} 불타기 반영 완료 (단계 {updatedPyramidCount}/{maxPyramidCount})");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ {symbol} 불타기 예외: {ex.Message}");
                return false;
            }
        }

        public void HandleOrderUpdate(BinanceFuturesStreamOrderUpdate orderUpdate)
        {
            var data = orderUpdate.UpdateData;
            string symbol = data.Symbol;

            lock (_posLock)
            {
                if (_activePositions.TryGetValue(symbol, out var pos))
                {
                    // [수정] data.Id -> data.OrderId
                    if (pos.StopOrderId == data.OrderId.ToString() && data.Status == OrderStatus.Filled)
                    {
                        OnAlert?.Invoke($"🛑 {symbol} 서버사이드 손절 주문 체결 완료! (체결가: {data.AveragePrice})");
                        OnLog?.Invoke($"[체결] {symbol} STOP_MARKET Filled at {data.AveragePrice}");
                    }
                }
            }
        }

        private void CleanupPositionData(string symbol)
        {
            lock (_posLock) _activePositions.Remove(symbol);
            OnCloseIncompleteStatusChanged?.Invoke(symbol, false, null);
            OnPositionStatusUpdate?.Invoke(symbol, false, 0);
            
            // [중요] 포지션 청산 후 ROI를 명시적으로 0으로 리셋
            // (감시 루프의 마지막 ROE 값이 UI를 덮어씌우는 것을 방지)
            OnTickerUpdate?.Invoke(symbol, 0m, 0d);
        }
    }
}
