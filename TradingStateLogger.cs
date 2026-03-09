using System;
using System.Collections.Generic;
using System.Text;

namespace TradingBot
{
    /// <summary>
    /// 트레이딩 상태를 직관적으로 표시하는 로그 메시지 생성기
    /// 
    /// 사용자 친화적 로그 형태:
    /// - "횡보 중" - 시장이 횡보장
    /// - "진입 대기 (ETA: 14:30)" - AI 예측 시간까지 대기
    /// - "진입 시도 중 (ML 스나이퍼 평가)" - 실제 진입 평가 진행
    /// - "진입 완료" - 주문 성공
    /// - "진입 거부 (이유)" - 차단 사유
    /// </summary>
    public static class TradingStateLogger
    {
        // ═══════════════════════════════════════════════════════════════
        // 시장 상태 로그 (Market State Logs)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 횡보장 감지 로그
        /// </summary>
        public static string ConsolidationDetected(string symbol, string reason)
        {
            return $"📊 [{symbol}] 횡보 중 | {reason}";
        }

        /// <summary>
        /// 추세장 감지 로그
        /// </summary>
        public static string TrendingMarket(string symbol, string direction, string indicators)
        {
            string emoji = direction.ToUpperInvariant() == "LONG" ? "📈" : "📉";
            return $"{emoji} [{symbol}] {direction} 추세 진행 | {indicators}";
        }

        // ═══════════════════════════════════════════════════════════════
        // 진입 대기 로그 (Waiting for Entry)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// AI 예측 ETA까지 대기 중
        /// </summary>
        public static string WaitingForETA(string symbol, string direction, DateTime eta, float confidence)
        {
            string timeStr = eta.ToString("HH:mm");
            int minutesLeft = (int)(eta - DateTime.Now).TotalMinutes;
            
            if (minutesLeft > 60)
                return $"⏳ [{symbol}] {direction} 진입 대기 (ETA: {timeStr}, {minutesLeft / 60}시간 후) | AI 신뢰도 {confidence:P0}";
            else if (minutesLeft > 0)
                return $"⏳ [{symbol}] {direction} 진입 대기 (ETA: {timeStr}, {minutesLeft}분 후) | AI 신뢰도 {confidence:P0}";
            else
                return $"🎯 [{symbol}] {direction} 진입 타점 도달 (ETA: {timeStr}) | AI 스나이퍼 평가 시작";
        }

        /// <summary>
        /// 캔들 확인 대기 중 (Fakeout 방지)
        /// </summary>
        public static string WaitingForCandleConfirmation(string symbol, string direction, int candlesRemaining)
        {
            return $"⏸️ [{symbol}] {direction} 캔들 확인 대기 ({candlesRemaining}캔들 남음) | Fakeout 방지 모드";
        }

        /// <summary>
        /// 15분봉 웨이브 게이트 대기
        /// </summary>
        public static string WaitingFor15MinWaveGate(string symbol, string direction, string waveReason)
        {
            return $"🌊 [{symbol}] {direction} 15분봉 웨이브 분석 대기 | {waveReason}";
        }

        // ═══════════════════════════════════════════════════════════════
        // 진입 시도 로그 (Entry Attempt in Progress)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// AI 더블체크 게이트 평가 시작
        /// </summary>
        public static string EvaluatingAIGate(string symbol, string direction, decimal price)
        {
            return $"🤖 [{symbol}] {direction} AI 게이트 평가 중 | 현재가 ${price:F2}";
        }

        /// <summary>
        /// ML.NET 스나이퍼 평가 중
        /// </summary>
        public static string EvaluatingMLSniper(string symbol, string direction, float mlConfidence, float tfConfidence)
        {
            return $"🎯 [{symbol}] {direction} ML 스나이퍼 평가 중 | ML신뢰도 {mlConfidence:P0}, TF신뢰도 {tfConfidence:P0}";
        }

        /// <summary>
        /// 리스크 관리 평가 중
        /// </summary>
        public static string EvaluatingRiskManagement(string symbol, string direction, decimal marginUsdt, int leverage)
        {
            return $"💰 [{symbol}] {direction} 리스크 평가 중 | 증거금 ${marginUsdt:F2}, 레버리지 {leverage}x";
        }

        /// <summary>
        /// 거래소 주문 요청 중
        /// </summary>
        public static string PlacingOrder(string symbol, string direction, decimal price, decimal quantity)
        {
            return $"📤 [{symbol}] {direction} 주문 요청 중 | 가격 ${price:F2}, 수량 {quantity:F4}";
        }

        // ═══════════════════════════════════════════════════════════════
        // 진입 완료 로그 (Entry Completed)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 진입 성공
        /// </summary>
        public static string EntrySuccess(string symbol, string direction, decimal entryPrice, decimal stopLoss, decimal takeProfit, string source)
        {
            decimal riskReward = takeProfit > stopLoss
                ? Math.Abs(takeProfit - entryPrice) / Math.Abs(entryPrice - stopLoss)
                : 0;

            return $"✅ [{symbol}] {direction} 진입 완료! " +
                   $"| 진입가 ${entryPrice:F2}, 손절 ${stopLoss:F2}, 익절 ${takeProfit:F2} " +
                   $"| R:R {riskReward:F1}x | 전략={source}";
        }

        // ═══════════════════════════════════════════════════════════════
        // 진입 거부 로그 (Entry Rejected)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// AI 게이트 거부
        /// </summary>
        public static string RejectedByAIGate(string symbol, string direction, string reason, float mlConf, float tfConf)
        {
            return $"⛔ [{symbol}] {direction} AI 게이트 거부 | {reason} | ML={mlConf:P0}, TF={tfConf:P0}";
        }

        /// <summary>
        /// 15분봉 웨이브 게이트 거부
        /// </summary>
        public static string RejectedBy15MinWaveGate(string symbol, string direction, string waveReason)
        {
            return $"🌊 [{symbol}] {direction} 15분봉 웨이브 게이트 차단 | {waveReason}";
        }

        /// <summary>
        /// 리스크 관리 거부 (서킷 브레이커, 포지션 한도 등)
        /// </summary>
        public static string RejectedByRiskManagement(string symbol, string direction, string reason)
        {
            return $"🛡️ [{symbol}] {direction} 리스크 관리 차단 | {reason}";
        }

        /// <summary>
        /// 추격 방지 필터 거부
        /// </summary>
        public static string RejectedByChaseFilter(string symbol, string direction, string chaseReason)
        {
            return $"🏃 [{symbol}] {direction} 추격 방지 차단 | {chaseReason}";
        }

        /// <summary>
        /// 패턴 매칭 거부
        /// </summary>
        public static string RejectedByPatternFilter(string symbol, string direction, string patternReason)
        {
            return $"🔍 [{symbol}] {direction} 패턴 필터 차단 | {patternReason}";
        }

        /// <summary>
        /// 캔들 확인 실패
        /// </summary>
        public static string RejectedByCandleConfirmation(string symbol, string direction, string confirmReason)
        {
            return $"⏸️ [{symbol}] {direction} 캔들 확인 실패 | {confirmReason}";
        }

        /// <summary>
        /// 15분봉 추세 필터 거부
        /// </summary>
        public static string RejectedBy15MinTrendFilter(string symbol, string direction, string trendReason)
        {
            return $"📊 [{symbol}] {direction} 15분봉 추세 필터 차단 | {trendReason}";
        }

        /// <summary>
        /// 일반 진입 거부 (기타 사유)
        /// </summary>
        public static string RejectedByGeneral(string symbol, string direction, string reason)
        {
            return $"❌ [{symbol}] {direction} 진입 거부 | {reason}";
        }

        // ═══════════════════════════════════════════════════════════════
        // 청산 로그 (Exit Logs)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 익절 청산
        /// </summary>
        public static string ExitTakeProfit(string symbol, string direction, decimal entryPrice, decimal exitPrice, decimal pnl, decimal pnlPercent)
        {
            return $"💰 [{symbol}] {direction} 익절 청산! | 진입 ${entryPrice:F2} → 청산 ${exitPrice:F2} | PnL ${pnl:F2} ({pnlPercent:+0.00}%)";
        }

        /// <summary>
        /// 손절 청산
        /// </summary>
        public static string ExitStopLoss(string symbol, string direction, decimal entryPrice, decimal exitPrice, decimal pnl, decimal pnlPercent)
        {
            return $"🛑 [{symbol}] {direction} 손절 청산 | 진입 ${entryPrice:F2} → 청산 ${exitPrice:F2} | PnL ${pnl:F2} ({pnlPercent:+0.00}%)";
        }

        /// <summary>
        /// AI 이탈 신호로 청산
        /// </summary>
        public static string ExitAISignal(string symbol, string direction, decimal entryPrice, decimal exitPrice, decimal pnl, decimal pnlPercent, string aiReason)
        {
            return $"🤖 [{symbol}] {direction} AI 이탈 청산 | 진입 ${entryPrice:F2} → 청산 ${exitPrice:F2} | PnL ${pnl:F2} ({pnlPercent:+0.00}%) | {aiReason}";
        }

        // ═══════════════════════════════════════════════════════════════
        // 시스템 상태 로그 (System State Logs)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 서킷 브레이커 발동
        /// </summary>
        public static string CircuitBreakerTripped(string reason, int hoursLocked)
        {
            return $"⛔ [서킷 브레이커 발동] {reason} | {hoursLocked}시간 동안 모든 진입 차단";
        }

        /// <summary>
        /// 서킷 브레이커 해제
        /// </summary>
        public static string CircuitBreakerReleased(int closedPositions)
        {
            return $"♻️ [서킷 브레이커 해제] 매매 재개 | {closedPositions}개 포지션 청산 완료";
        }

        /// <summary>
        /// 진입 워밍업 활성화
        /// </summary>
        public static string EntryWarmupActive(int secondsRemaining)
        {
            return $"⏳ [진입 워밍업] 신규 진입 제한 중 | {secondsRemaining}초 남음 (시장 안정화 대기)";
        }

        // ═══════════════════════════════════════════════════════════════
        // 복합 상태 로그 (Composite State Logs)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// AI 진입 확률 스캔 요약
        /// </summary>
        public static string AIProbabilityScanSummary(List<(string symbol, float longProb, float shortProb, DateTime eta)> topSignals)
        {
            if (topSignals == null || topSignals.Count == 0)
                return "🔍 AI 진입 확률 스캔 완료 | 유효 신호 없음";

            var sb = new StringBuilder();
            sb.AppendLine("🔍 [AI 진입 확률 스캔 요약]");
            foreach (var (symbol, longProb, shortProb, eta) in topSignals)
            {
                string direction = longProb > shortProb ? "LONG" : "SHORT";
                float prob = Math.Max(longProb, shortProb);
                string etaStr = eta > DateTime.Now ? $"ETA {eta:HH:mm}" : "즉시";
                sb.AppendLine($"  • {symbol}: {direction} {prob:P0} | {etaStr}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 심볼별 트레이딩 상태 요약
        /// </summary>
        public static string SymbolTradingSummary(
            string symbol,
            string marketState, // "횡보", "상승추세", "하락추세"
            string entryStatus, // "대기", "평가중", "차단", "진입완료"
            string reason,
            DateTime? eta = null)
        {
            string statusEmoji = entryStatus switch
            {
                "대기" => "⏳",
                "평가중" => "🔍",
                "차단" => "⛔",
                "진입완료" => "✅",
                _ => "📊"
            };

            string etaInfo = eta.HasValue && eta.Value > DateTime.Now
                ? $" | ETA {eta.Value:HH:mm}"
                : string.Empty;

            return $"{statusEmoji} [{symbol}] {marketState} | {entryStatus}: {reason}{etaInfo}";
        }
    }
}
