using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 하이브리드 AI 기반 이탈 관리자 (ATR 동적 트레일링 스톱 적용)
    /// ────────────────────────────────────
    /// 멀티 전략 시스템에서 포지션 익절/손절을 총괄합니다.
    ///
    /// 1) AI 기반 익절: Transformer PredictedPrice 도달 시 50% 부분 익절
    /// 2) 지표 기반 익절: RSI 80+ 또는 BB 상단 이탈 후 재진입 시 잔량 익절
    /// 3) ATR 동적 트레일링 스톱:
    ///    - ROE < 10%: ATR * 1.5 (변동성에 털리지 않게)
    ///    - ROE 10~20% & RSI < 70: ATR * 1.0 (추세 진행)
    ///    - RSI 70~80: ATR * 0.5 (고점 근접)
    ///    - RSI 80+: ATR * 0.2 (극단적 근접 트레일링)
    /// </summary>
    public class HybridExitManager
    {
        private const double MinFirstPartialRoePercent = 25.0;

        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;
        public event Func<string, decimal, Task>? OnTrailingStopUpdate; // 스탑로스 갱신 이벤트

        // 포지션별 예측 가격 추적
        private readonly ConcurrentDictionary<string, HybridExitState> _exitStates = new();

        /// <summary>
        /// 포지션 진입 시 이탈 상태 등록
        /// </summary>
        public void RegisterEntry(string symbol, string direction, decimal entryPrice, decimal predictedPrice)
        {
            _exitStates[symbol] = new HybridExitState
            {
                Symbol = symbol,
                Direction = direction,
                EntryPrice = entryPrice,
                PredictedPrice = predictedPrice,
                EntryTime = DateTime.Now,
                Stage = ExitStage.Watching,
                HighestROE = 0,
                HighestPriceSinceEntry = entryPrice,  // 롱 기준 최고가 초기화
                LowestPriceSinceEntry = entryPrice,   // 숏 기준 최저가 초기화
                CurrentTrailingStopPrice = 0,        // 트레일링 스톱 미설정
            };
            OnLog?.Invoke($"📋 [Hybrid Exit] {symbol} {direction} 등록 | Entry: {entryPrice:F8} | Target: {predictedPrice:F8}");
        }

        /// <summary>
        /// 실시간 가격 업데이트 시 이탈 조건 확인 (ATR 기반 동적 트레일링 스톱)
        /// </summary>
        /// <returns>실행할 액션 (null이면 유지)</returns>
        public ExitAction? CheckExit(
            string symbol,
            decimal currentPrice,
            double rsi,
            double bbUpper,
            double bbMid,
            double bbLower,
            double currentAtr,  // ATR 추가
            decimal? newPredictedPrice = null,
            bool emitAlerts = true)
        {
            if (!_exitStates.TryGetValue(symbol, out var state)) return null;

            // [실시간 추적] 지표 캐시 업데이트 (5분봉 CheckExit 호출 시)
            state.LastRSI = rsi;
            state.LastBBUpper = bbUpper;
            state.LastBBMid = bbMid;
            state.LastBBLower = bbLower;
            state.LastATR = currentAtr;

            // AI 예측 업데이트 (실시간 재예측 시)
            if (newPredictedPrice.HasValue && newPredictedPrice.Value > 0)
            {
                state.PreviousPredictedPrice = state.PredictedPrice;
                state.PredictedPrice = newPredictedPrice.Value;
            }

            bool isLong = state.Direction == "LONG";

            // 최고가/최저가 갱신 (트레일링 스톱 기준)
            if (isLong)
            {
                if (currentPrice > state.HighestPriceSinceEntry)
                    state.HighestPriceSinceEntry = currentPrice;
            }
            else
            {
                if (currentPrice < state.LowestPriceSinceEntry)
                    state.LowestPriceSinceEntry = currentPrice;
            }

            decimal priceChangeRatio = isLong
                ? (currentPrice - state.EntryPrice) / state.EntryPrice
                : (state.EntryPrice - currentPrice) / state.EntryPrice;
            double currentROE = (double)(priceChangeRatio * 20 * 100); // 20배 레버리지 ROE%
            state.HighestROE = Math.Max(state.HighestROE, currentROE);

            // [새로추가] ATR 기반 동적 트레일링 스톱 계산
            decimal newTrailingStopPrice = CalculateDynamicTrailingStop(
                state.EntryPrice,
                isLong ? state.HighestPriceSinceEntry : state.LowestPriceSinceEntry,
                currentAtr,
                rsi,
                isLong
            );

            // 트레일링 스톱은 한 방향으로만 이동 (롱: 상승만, 숏: 하락만)
            if (state.CurrentTrailingStopPrice == 0)
            {
                state.CurrentTrailingStopPrice = newTrailingStopPrice;
            }
            else
            {
                if (isLong)
                {
                    if (newTrailingStopPrice > state.CurrentTrailingStopPrice)
                        state.CurrentTrailingStopPrice = newTrailingStopPrice;
                }
                else
                {
                    if (newTrailingStopPrice < state.CurrentTrailingStopPrice)
                        state.CurrentTrailingStopPrice = newTrailingStopPrice;
                }
            }

            // ═══════════════════════════════════════════════
            // 1. AI 기반 1차 익절: PredictedPrice 도달 + ROE 25% 이상 → 50% 청산
            // ═══════════════════════════════════════════════
            if (state.Stage == ExitStage.Watching)
            {
                bool reachedTarget = isLong
                    ? currentPrice >= state.PredictedPrice
                    : currentPrice <= state.PredictedPrice;

                bool passedMinRoe = currentROE >= MinFirstPartialRoePercent;

                if (reachedTarget && passedMinRoe)
                {
                    state.Stage = ExitStage.PartialTaken;
                    state.BreakevenPrice = state.EntryPrice; // 본절가 설정
                    if (emitAlerts)
                        OnAlert?.Invoke($"💰 [Hybrid AI TP] {symbol} 1차 익절 시그널 | PredictedPrice({state.PredictedPrice:F8}) 도달 + ROE {MinFirstPartialRoePercent:F0}% 이상 ({currentROE:F1}%)");
                    return new ExitAction
                    {
                        Symbol = symbol,
                        ActionType = ExitActionType.PartialClose50Pct,
                        Reason = $"AI PredictedPrice 도달 + ROE {MinFirstPartialRoePercent:F0}% 이상 ({state.PredictedPrice:F8})",
                        ROE = currentROE,
                    };
                }

                if (reachedTarget && !passedMinRoe && emitAlerts)
                {
                    OnLog?.Invoke($"⏳ [Hybrid AI TP 보류] {symbol} PredictedPrice 도달했지만 ROE {currentROE:F1}% < {MinFirstPartialRoePercent:F0}%");
                }
            }

            // ═══════════════════════════════════════════════
            // 2. 지표 기반 2차 익절: RSI 80+ 또는 BB 상단/하단 이탈
            // ═══════════════════════════════════════════════
            if (state.Stage == ExitStage.PartialTaken)
            {
                bool indicatorExit = false;
                string reason = "";

                if (isLong)
                {
                    // RSI 80+ → 과매수 극한, 전량 익절
                    if (rsi >= 80)
                    {
                        indicatorExit = true;
                        reason = $"RSI 과매수 ({rsi:F1})";
                    }
                    // BB 상단 강하게 이탈 후 다시 내려옴 (상단 터치 후 되돌림)
                    else if ((double)currentPrice < bbUpper && state.PreviousHitBBUpper)
                    {
                        indicatorExit = true;
                        reason = $"BB 상단 이탈 후 재진입 ({currentPrice:F8} < {bbUpper:F4})";
                    }
                    if ((double)currentPrice >= bbUpper) state.PreviousHitBBUpper = true;
                }
                else // SHORT
                {
                    // RSI 20- → 과매도 극한
                    if (rsi <= 20)
                    {
                        indicatorExit = true;
                        reason = $"RSI 과매도 ({rsi:F1})";
                    }
                    // BB 하단 이탈 후 반등
                    else if ((double)currentPrice > bbLower && state.PreviousHitBBLower)
                    {
                        indicatorExit = true;
                        reason = $"BB 하단 이탈 후 반등 ({currentPrice:F8} > {bbLower:F4})";
                    }
                    if ((double)currentPrice <= bbLower) state.PreviousHitBBLower = true;
                }

                if (indicatorExit)
                {
                    state.Stage = ExitStage.FullyExited;
                    if (emitAlerts)
                        OnAlert?.Invoke($"🎯 [Hybrid Indicator TP] {symbol} 잔량 익절 | {reason} | ROE: {currentROE:F1}%");
                    return new ExitAction
                    {
                        Symbol = symbol,
                        ActionType = ExitActionType.FullClose,
                        Reason = $"지표 기반 전량 익절: {reason}",
                        ROE = currentROE,
                    };
                }
            }

            // ═══════════════════════════════════════════════
            // 3. ATR 동적 트레일링 스톱: 실시간 가격이 스톱 가격 돌파하면 즉시 청산
            // ═══════════════════════════════════════════════
            if (state.CurrentTrailingStopPrice > 0)
            {
                bool trailingStopHit = isLong
                    ? currentPrice <= state.CurrentTrailingStopPrice
                    : currentPrice >= state.CurrentTrailingStopPrice;

                if (trailingStopHit)
                {
                    state.Stage = ExitStage.FullyExited;
                    string atmMultiplier = GetAtrMultiplierDescription(currentROE, rsi);
                    if (emitAlerts)
                        OnAlert?.Invoke($"⚡ [ATR 트레일링 스톱] {symbol} | 현재가: {currentPrice:F8} | 스톱: {state.CurrentTrailingStopPrice:F8} | ROE: {currentROE:F1}% | {atmMultiplier}");
                    return new ExitAction
                    {
                        Symbol = symbol,
                        ActionType = ExitActionType.FullClose,
                        Reason = $"ATR 동적 트레일링 스톱 ({atmMultiplier})",
                        ROE = currentROE,
                    };
                }
            }

            // ═══════════════════════════════════════════════
            // 4. 트레일링 스톱 (기존 로직): AI 예측 반전 or BB 중단 이탈
            // ═══════════════════════════════════════════════
            if (state.Stage >= ExitStage.PartialTaken)
            {
                // 3-1. AI 예측 방향 반전
                if (state.PreviousPredictedPrice > 0)
                {
                    bool aiDirectionReversed = isLong
                        ? state.PredictedPrice < state.EntryPrice   // 롱인데 AI가 진입가 아래 예측
                        : state.PredictedPrice > state.EntryPrice;  // 숏인데 AI가 진입가 위 예측

                    if (aiDirectionReversed)
                    {
                        state.Stage = ExitStage.FullyExited;
                        if (emitAlerts)
                            OnAlert?.Invoke($"🛑 [AI 반전] {symbol} | AI 예측 방향 바뀜 → 전량 청산 | ROE: {currentROE:F1}%");
                        return new ExitAction
                        {
                            Symbol = symbol,
                            ActionType = ExitActionType.FullClose,
                            Reason = "AI 예측 방향 반전 (트레일링 스탑)",
                            ROE = currentROE,
                        };
                    }
                }

                // 3-2. BB 중단 이탈 → 본절/스탑
                bool bbMidBroken = isLong
                    ? (double)currentPrice < bbMid
                    : (double)currentPrice > bbMid;

                if (bbMidBroken && state.HighestROE >= 10) // 최소 ROE 10% 달성 후에만
                {
                    // 본절가 보호: 진입가 이하로 떨어지면 즉시 청산
                    bool belowBreakeven = isLong
                        ? currentPrice <= state.BreakevenPrice
                        : currentPrice >= state.BreakevenPrice;

                    if (belowBreakeven)
                    {
                        state.Stage = ExitStage.FullyExited;
                        OnLog?.Invoke($"🛑 [BB 중단 이탈 + 본절] {symbol} | 본절가({state.BreakevenPrice:F8}) 도달 → 청산");
                        return new ExitAction
                        {
                            Symbol = symbol,
                            ActionType = ExitActionType.FullClose,
                            Reason = $"BB 중단 이탈 + 본절가 도달",
                            ROE = currentROE,
                        };
                    }
                }
            }

            // ═══════════════════════════════════════════════
            // 5. 절대 손절: ROE -20% (20x → -1.0% 가격 변동)
            // ═══════════════════════════════════════════════
            if (currentROE <= -20)
            {
                state.Stage = ExitStage.FullyExited;
                if (emitAlerts)
                    OnAlert?.Invoke($"🚨 [절대 손절] {symbol} | ROE: {currentROE:F1}% → 전량 청산");
                return new ExitAction
                {
                    Symbol = symbol,
                    ActionType = ExitActionType.FullClose,
                    Reason = $"절대 손절 (ROE {currentROE:F1}%)",
                    ROE = currentROE,
                };
            }

            // ═══════════════════════════════════════════════
            // 6. [실시간 API 갱신] ATR 트레일링 스탑 업데이트 이벤트 발생
            // ═══════════════════════════════════════════════
            if (state.CurrentTrailingStopPrice > 0 && OnTrailingStopUpdate != null)
            {
                // 비동기 이벤트 호출 (Fire-and-forget 방식)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await OnTrailingStopUpdate.Invoke(symbol, state.CurrentTrailingStopPrice);
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] 트레일링 스탑 API 갱신 실패: {ex.Message}");
                    }
                });
            }

            return null;
        }

        /// <summary>
        /// [새로추가] 상황에 따라 스톱 간격을 조절하는 동적 ATR 트레일링 스톱 계산기
        /// </summary>
        private decimal CalculateDynamicTrailingStop(
            decimal entryPrice,
            decimal extremePriceSinceEntry,  // 롱: 최고가, 숏: 최저가
            double currentAtr,
            double currentRsi,
            bool isLongPosition)
        {
            // 1. 현재 수익률(ROE) 계산 (레버리지 20배 기준)
            decimal priceChangeRate = isLongPosition
                ? (extremePriceSinceEntry - entryPrice) / entryPrice
                : (entryPrice - extremePriceSinceEntry) / entryPrice;
            decimal roe = priceChangeRate * 20 * 100; // % 단위

            // 2. 상황별 ATR 멀티플라이어(승수) 결정
            double atrMultiplier;

            if (roe < 10)
            {
                // [1단계: 진입 직후] 변동성(휩소)에 털리지 않도록 넓넓하게 방어
                atrMultiplier = 1.5;
            }
            else if (roe >= 10 && currentRsi < 70)
            {
                // [2단계: 추세 진행 중] ROE 10% 이상 수익권, 엘리엇 3파 진행 중
                atrMultiplier = 1.0;
            }
            else if (currentRsi >= 70 && currentRsi < 80)
            {
                // [3단계: 과열 진입] RSI 70 돌파. 3파동 고점이 가까워짐
                atrMultiplier = 0.5;
            }
            else
            {
                // [4단계: 극단적 과열 / 피날레] RSI 80 이상. 거래량 폭발 후 윙꼬리 위험
                atrMultiplier = 0.2;
            }

            // 3. 트레일링 스톱 가격 계산
            decimal trailingDistance = (decimal)(currentAtr * atrMultiplier);
            decimal calculatedStopPrice;

            if (isLongPosition)
            {
                // 롱 포지션: 최고가에서 트레일링 거리를 뾄 가격
                calculatedStopPrice = extremePriceSinceEntry - trailingDistance;

                // [안전장치] ROE가 15%를 넘었다면, 스톱로스는 절대 본절가(EntryPrice) 아래로 내려가지 않음
                if (roe >= 15 && calculatedStopPrice < entryPrice)
                {
                    calculatedStopPrice = entryPrice + (entryPrice * 0.001m); // 수수료 포함 본절가
                }
            }
            else
            {
                // 숏 포지션 (가장 낮았던 가격 기준)
                calculatedStopPrice = extremePriceSinceEntry + trailingDistance;

                if (roe >= 15 && calculatedStopPrice > entryPrice)
                {
                    calculatedStopPrice = entryPrice - (entryPrice * 0.001m);
                }
            }

            return calculatedStopPrice;
        }

        /// <summary>
        /// ATR 멀티플라이어 설명 텍스트 (로그 출력용)
        /// </summary>
        private string GetAtrMultiplierDescription(double roe, double rsi)
        {
            if (roe < 10) return "ATR*1.5 (방어)";
            if (roe >= 10 && rsi < 70) return "ATR*1.0 (추세)";
            if (rsi >= 70 && rsi < 80) return "ATR*0.5 (과열)";
            return "ATR*0.2 (피날레)";
        }

        /// <summary>
        /// 실시간 Tick 가격 추적 (WebSocket 1초 간격)
        /// 포지션이 있으면 최고가/최저가를 추적하고 트레일링 스탑 재계산
        /// </summary>
        public void UpdateRealtimePriceTracking(string symbol, decimal currentPrice)
        {
            if (!_exitStates.TryGetValue(symbol, out var state)) return;

            bool isLong = state.Direction == "LONG";

            // 최고가/최저가 갱신
            if (isLong)
            {
                if (currentPrice > state.HighestPriceSinceEntry)
                    state.HighestPriceSinceEntry = currentPrice;
            }
            else
            {
                if (currentPrice < state.LowestPriceSinceEntry)
                    state.LowestPriceSinceEntry = currentPrice;
            }

            // 캐시된 지표가 없으면 스킵 (첫 5분봉 대기 중)
            if (state.LastATR <= 0) return;

            // ATR 기반 동적 트레일링 스탑 재계산
            decimal newTrailingStopPrice = CalculateDynamicTrailingStop(
                state.EntryPrice,
                isLong ? state.HighestPriceSinceEntry : state.LowestPriceSinceEntry,
                state.LastATR,
                state.LastRSI,
                isLong
            );

            // 트레일링 스탑은 한 방향으로만 이동 (롱: 상승만, 숏: 하락만)
            bool stopPriceChanged = false;
            if (state.CurrentTrailingStopPrice == 0)
            {
                state.CurrentTrailingStopPrice = newTrailingStopPrice;
                stopPriceChanged = true;
            }
            else
            {
                if (isLong)
                {
                    if (newTrailingStopPrice > state.CurrentTrailingStopPrice)
                    {
                        state.CurrentTrailingStopPrice = newTrailingStopPrice;
                        stopPriceChanged = true;
                    }
                }
                else
                {
                    if (newTrailingStopPrice < state.CurrentTrailingStopPrice)
                    {
                        state.CurrentTrailingStopPrice = newTrailingStopPrice;
                        stopPriceChanged = true;
                    }
                }
            }

            // 스탑 가격이 변경되었고 이벤트가 등록되어 있으면 API 갱신
            if (stopPriceChanged && state.CurrentTrailingStopPrice > 0 && OnTrailingStopUpdate != null)
            {
                // 비동기 이벤트 호출 (Fire-and-forget)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await OnTrailingStopUpdate.Invoke(symbol, state.CurrentTrailingStopPrice);
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [{symbol}] 실시간 트레일링 스탑 API 갱신 실패: {ex.Message}");
                    }
                });
            }
        }

        /// <summary>포지션 청산 완료 시 상태 제거</summary>
        public void RemoveState(string symbol)
        {
            _exitStates.TryRemove(symbol, out _);
        }

        /// <summary>포지션 존재 여부</summary>
        public bool HasState(string symbol) => _exitStates.ContainsKey(symbol);

        public HybridExitState? GetState(string symbol)
        {
            _exitStates.TryGetValue(symbol, out var state);
            return state;
        }
    }

    // ════════════════ 데이터 클래스 ════════════════

    public enum ExitStage
    {
        Watching,       // 진입 후 감시 중
        PartialTaken,   // 1차 부분 익절 완료 (50%)
        FullyExited,    // 전량 청산 완료
    }

    public enum ExitActionType
    {
        PartialClose50Pct,  // 50% 부분 청산
        FullClose,          // 전량 청산
    }

    public class ExitAction
    {
        public string Symbol { get; set; } = string.Empty;
        public ExitActionType ActionType { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double ROE { get; set; }
    }

    public class HybridExitState
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public decimal PredictedPrice { get; set; }
        public decimal PreviousPredictedPrice { get; set; }
        public decimal BreakevenPrice { get; set; }
        public DateTime EntryTime { get; set; }
        public ExitStage Stage { get; set; }
        public double HighestROE { get; set; }
        public bool PreviousHitBBUpper { get; set; }
        public bool PreviousHitBBLower { get; set; }

        // ATR 동적 트레일링 스톱 추가 필드
        public decimal HighestPriceSinceEntry { get; set; }  // 롱 기준 최고가
        public decimal LowestPriceSinceEntry { get; set; }   // 숏 기준 최저가
        public decimal CurrentTrailingStopPrice { get; set; } // 현재 트레일링 스톱 가격

        // 실시간 tick 처리를 위한 지표 캐시 (마지막 5분봉 기준 값)
        public double LastRSI { get; set; }
        public double LastBBUpper { get; set; }
        public double LastBBMid { get; set; }
        public double LastBBLower { get; set; }
        public double LastATR { get; set; }
    }
}
