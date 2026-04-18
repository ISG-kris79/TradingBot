using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Shared.Models;
using Binance.Net.Enums;
using Binance.Net.Interfaces;

namespace TradingBot.Services
{
    public interface IExchangeService
    {
        Task<decimal> GetBalanceAsync(string asset, CancellationToken token = default);
        Task<decimal> GetPriceAsync(string symbol, CancellationToken ct = default);
        Task<List<PositionInfo>> GetPositionsAsync(CancellationToken ct = default);
        Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default, bool reduceOnly = false);
        Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken token = default);

        // 레버리지 설정 + 심볼 최대 레버리지 자동 조정 (0=완전 실패, N=실제 설정된 레버리지)
        Task<int> SetLeverageAutoAsync(string symbol, int desiredLeverage, CancellationToken ct = default);

        Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default);

        // [추가] 거래소 정보 조회 (LotSize 등)
        Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken token = default);

        // [슬리피지 방어] 호가창 조회 (Best Bid/Ask)
        Task<(decimal bestBid, decimal bestAsk)?> GetOrderBookAsync(string symbol, CancellationToken ct = default);

        // [Phase 4] 펀딩비 조회
        Task<decimal> GetFundingRateAsync(string symbol, CancellationToken token = default);

        // [추가] 포지션 관리
        Task<(bool Success, string OrderId)> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default);
        Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default);

        /// <summary>[v5.1.8] 가용 잔고 (주문 가능 금액)</summary>
        Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken ct = default);

        /// <summary>[v5.1.0] 특정 심볼의 모든 미체결 주문 일괄 취소 (SL/TP/트레일링 전부)</summary>
        Task CancelAllOrdersAsync(string symbol, CancellationToken ct = default);

        /// <summary>[v5.1.2] TAKE_PROFIT_MARKET 주문 (바이낸스 TP/SL 시스템 연동)</summary>
        Task<(bool Success, string OrderId)> PlaceTakeProfitOrderAsync(
            string symbol, string side, decimal quantity, decimal stopPrice,
            CancellationToken ct = default);

        // [v3.3.8] 서버사이드 트레일링 스탑 (바이낸스 TRAILING_STOP_MARKET)
        Task<(bool Success, string OrderId)> PlaceTrailingStopOrderAsync(
            string symbol, string side, decimal quantity,
            decimal callbackRate, decimal? activationPrice = null,
            CancellationToken ct = default);

        // [Phase 11] Batch Order for Grid Strategy
        Task<BatchOrderResult> PlaceBatchOrdersAsync(List<BatchOrderRequest> orders, CancellationToken ct = default);

        // [Phase 11] Portfolio Margin (Binance Multi-Assets Mode)
        Task<bool> GetMultiAssetsModeAsync(CancellationToken ct = default);
        Task<bool> SetMultiAssetsModeAsync(bool enabled, CancellationToken ct = default);

        // [Phase 11] Position Mode (Hedge Mode vs One-way Mode)
        Task<bool> GetPositionModeAsync(CancellationToken ct = default);
        Task<bool> SetPositionModeAsync(bool hedgeMode, CancellationToken ct = default);

        // [Phase 12: PUMP 전략 지원] 거래소 이름
        string ExchangeName { get; }

        // [Phase 12: PUMP 전략 지원] 지정가 주문
        Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            CancellationToken ct = default);

        // [Phase 12: PUMP 전략 지원] 주문 상태 확인
        Task<(bool Filled, decimal FilledQuantity, decimal AveragePrice)> GetOrderStatusAsync(
            string symbol,
            string orderId,
            CancellationToken ct = default);

        // [시장가 주문] 즉시 체결 + 체결 정보 반환 (지정가 3초 대기 제거)
        Task<(bool Success, decimal FilledQuantity, decimal AveragePrice)> PlaceMarketOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            CancellationToken ct = default,
            bool reduceOnly = false);

        // [v5.5.2] 진입 + SL + TP + 트레일링 스탑 일괄 API 등록
        // 시장가 진입 후 체결 즉시 거래소에 모든 청산 주문을 등록 → 봇 다운타임에도 거래소가 자동 처리
        Task<bool> ExecuteFullEntryWithAllOrdersAsync(
            string symbol,
            string positionSide,            // "LONG" | "SHORT"
            decimal quantity,
            decimal leverage,
            decimal stopLossPrice,          // 손절 절대 가격
            decimal takeProfitPrice,        // 1차 익절 절대 가격 (트레일링 활성화 기준)
            decimal partialProfitRoePercent, // 1차 익절 수량 비율 (예: 40.0 = 40%)
            decimal trailingStopCallbackRate, // 트레일링 콜백율 소수 (예: 0.02 = 2%)
            CancellationToken ct = default);

        // [v5.10.18] 최근 체결 내역 조회 (포지션 청산가 추적용)
        Task<(decimal exitPrice, decimal quantity, string side, DateTime time)?> GetLastTradeAsync(
            string symbol, DateTime since, CancellationToken ct = default);
    }
}
