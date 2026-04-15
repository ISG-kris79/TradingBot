using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using Binance.Net.Enums; // For KlineInterval
using Binance.Net.Interfaces; // For IBinanceKline

namespace TradingBot.Services
{
    // [추가] 거래소 확장성을 위한 인터페이스 정의
    public interface IExchangeService
    {
        string ExchangeName { get; }

        Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default);

        Task<decimal> GetPriceAsync(string symbol, CancellationToken ct = default);

        Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default, bool reduceOnly = false);

        Task<bool> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default);

        Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default);

        Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken ct = default);

        /// <summary>[v5.9.6] 심볼별 실제 거래소 레버리지 조회</summary>
        Task<int> GetSymbolLeverageAsync(string symbol, CancellationToken ct = default);

        Task<List<PositionInfo>> GetPositionsAsync(CancellationToken ct = default);

        // [추가] 캔들 데이터 조회
        Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default);

        // [v2.4.2 HistoricalDataLabeler] 날짜 범위 기반 캔들 조회 (6개월 데이터 수집용)
        Task<List<IBinanceKline>> GetKlinesAsync(
            string symbol,
            KlineInterval interval,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 1000,
            CancellationToken ct = default);

        // [추가] 거래소 정보 조회 (심볼 필터 등)
        Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken ct = default);

        // [슬리피지 방어] 호가창 조회 (Best Bid/Ask)
        Task<(decimal bestBid, decimal bestAsk)?> GetOrderBookAsync(string symbol, CancellationToken ct = default);

        // [Phase 12: PUMP 전략 지원] 지정가 주문 (주문 ID 반환)
        Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            CancellationToken ct = default);

        // [Phase 12: PUMP 전략 지원] 주문 상태 확인 (체결 여부 및 수량 반환)
        Task<(bool Filled, decimal FilledQuantity, decimal AveragePrice)> GetOrderStatusAsync(
            string symbol,
            string orderId,
            CancellationToken ct = default);
    }
}
