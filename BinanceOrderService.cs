﻿using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    public class BinanceOrderService
    {
        private readonly IBinanceRestClient _client;

        public BinanceOrderService(IBinanceRestClient client)
        {
            _client = client;
        }

        public async Task<WebCallResult<BinanceUsdFuturesOrder>> PlaceOrderAsync(
            string symbol,
            OrderSide side,
            FuturesOrderType type,
            decimal quantity,
            decimal? price = null,
            bool reduceOnly = false,
            CancellationToken ct = default)
        {
            return await ExecuteWithRetryAsync(async () => 
                await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol, side, type, quantity, price, reduceOnly: reduceOnly, ct: ct), 
                ct);
        }

        // [추가] 서버사이드 손절 주문 (STOP_MARKET)
        public async Task<WebCallResult<BinanceUsdFuturesOrder>> PlaceStopMarketOrderAsync(
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal stopPrice,
            CancellationToken ct = default)
        {
            return await ExecuteWithRetryAsync(async () =>
                await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol, 
                    side, 
                    FuturesOrderType.StopMarket, 
                    quantity, 
                    stopPrice: stopPrice, 
                    reduceOnly: true, // 손절은 포지션 감소 전용
                    ct: ct),
                ct);
        }

        // [추가] 주문 취소
        public async Task<WebCallResult<BinanceUsdFuturesOrder>> CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default)
        {
            return await ExecuteWithRetryAsync(async () =>
                await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId, ct: ct),
                ct);
        }

        // [추가] 레버리지 설정
        public async Task<WebCallResult<BinanceFuturesInitialLeverageChangeResult>> SetLeverageAsync(
            string symbol,
            int leverage,
            CancellationToken ct = default)
        {
            return await _client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: ct);
        }

        // [추가] 마진 타입 설정 (ISOLATED/CROSSED)
        public async Task<WebCallResult<BinanceFuturesChangeMarginTypeResult>> SetMarginTypeAsync(
            string symbol,
            FuturesMarginType marginType,
            CancellationToken ct = default)
        {
            return await _client.UsdFuturesApi.Account.ChangeMarginTypeAsync(symbol, marginType, ct: ct);
        }

        // [추가] 모든 열린 주문 취소
        public async Task<WebCallResult<BinanceFuturesCancelAllOrders>> CancelAllOrdersAsync(
            string symbol,
            CancellationToken ct = default)
        {
            return await _client.UsdFuturesApi.Trading.CancelAllOrdersAsync(symbol, ct: ct);
        }

        // [추가] 현재 포지션 조회
        public async Task<WebCallResult<IEnumerable<BinancePositionDetailsUsdt>>> GetPositionsAsync(string? symbol = null, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: ct);
            if (!result.Success || result.Data == null)
                return result.As<IEnumerable<BinancePositionDetailsUsdt>>(null);
            return result.As<IEnumerable<BinancePositionDetailsUsdt>>(result.Data.AsEnumerable());
        }

        // [추가] 계좌 잔고 조회
        public async Task<WebCallResult<IEnumerable<BinanceUsdFuturesAccountBalance>>> GetBalancesAsync(CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (!result.Success || result.Data == null)
                return result.As<IEnumerable<BinanceUsdFuturesAccountBalance>>(null);
            return result.As<IEnumerable<BinanceUsdFuturesAccountBalance>>(result.Data.AsEnumerable());
        }

        // [추가] 지수 백오프 재시도 로직 (최대 3회)
        private async Task<WebCallResult<T>> ExecuteWithRetryAsync<T>(Func<Task<WebCallResult<T>>> action, CancellationToken ct)
        {
            int retryCount = 0;
            int maxRetries = 3;
            
            while (true)
            {
                var result = await action();
                if (result.Success || retryCount >= maxRetries) return result;

                // 재시도 가능한 에러인지 확인 (네트워크 오류, 타임아웃 등)
                // -1002: Unauthorized, -2010: Insufficient Balance 등은 재시도 의미 없음
                // 여기서는 간단히 모든 실패에 대해 재시도하되, 실제로는 에러 코드 필터링 권장
                
                retryCount++;
                int delay = (int)Math.Pow(2, retryCount) * 200; // 400ms, 800ms, 1600ms
                
                // 로그는 상위 서비스에서 처리하거나 여기서 이벤트를 발생시켜야 함
                await Task.Delay(delay, ct);
            }
        }
    }
}