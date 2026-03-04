using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Services;
using TradingBot.Models;

namespace TradingBot.Strategies
{
    /// <summary>
    /// Grid Strategy: 현재가 기준으로 상단 매도 그리드와 하단 매수 그리드를 배치
    /// Batch Order를 활용하여 여러 주문을 한 번에 전송 (API 호출 최소화)
    /// </summary>
    public class GridStrategy
    {
        private readonly IExchangeService _exchangeService;
        private decimal _gridStepPercent = 0.01m; // 1% 간격
        private int _gridLevels = 5;

        public GridStrategy(IExchangeService exchangeService)
        {
            _exchangeService = exchangeService;
        }

        /// <summary>
        /// 그리드 전략 실행: 상/하단 그리드 주문을 배치 형태로 전송
        /// </summary>
        public async Task ExecuteAsync(string symbol, decimal currentPrice, CancellationToken token)
        {
            try
            {
                // 1. 거래소 정보 조회 (Tick Size, Lot Size)
                var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
                if (exchangeInfo == null)
                {
                    Console.WriteLine($"[GridStrategy] Unable to fetch exchange info for {symbol}");
                    return;
                }

                var symbolInfo = exchangeInfo.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolInfo == null)
                {
                    Console.WriteLine($"[GridStrategy] Symbol {symbol} not found in exchange info");
                    return;
                }

                decimal priceTick = symbolInfo.PriceFilter?.TickSize ?? 0.01m;
                decimal quantityStep = symbolInfo.LotSizeFilter?.StepSize ?? 0.001m;

                // 최소 주문 수량 (실제 운영 시 계좌 잔고/리스크 기반 계산 필요)
                decimal quantity = 0.001m;
                quantity = Math.Floor(quantity / quantityStep) * quantityStep;
                if (quantity == 0) quantity = quantityStep;

                // 2. 배치 주문 목록 생성
                var batchOrders = new List<BatchOrderRequest>();

                // 상단 매도 그리드 (현재가 위로 _gridLevels개)
                for (int i = 1; i <= _gridLevels; i++)
                {
                    decimal price = currentPrice * (1 + (_gridStepPercent * i));
                    price = Math.Floor(price / priceTick) * priceTick;

                    batchOrders.Add(new BatchOrderRequest
                    {
                        Symbol = symbol,
                        Side = "Sell",
                        Quantity = quantity,
                        Price = price,
                        OrderType = "Limit"
                    });
                }

                // 하단 매수 그리드 (현재가 아래로 _gridLevels개)
                for (int i = 1; i <= _gridLevels; i++)
                {
                    decimal price = currentPrice * (1 - (_gridStepPercent * i));
                    price = Math.Floor(price / priceTick) * priceTick;

                    batchOrders.Add(new BatchOrderRequest
                    {
                        Symbol = symbol,
                        Side = "Buy",
                        Quantity = quantity,
                        Price = price,
                        OrderType = "Limit"
                    });
                }

                // 3. 배치 주문 실행 (Binance: 최대 5개씩, Bybit: 순차 처리)
                var result = await _exchangeService.PlaceBatchOrdersAsync(batchOrders, token);
                
                Console.WriteLine($"[GridStrategy] Placed {result.SuccessCount}/{batchOrders.Count} orders for {symbol}");
                if (result.FailureCount > 0)
                {
                    Console.WriteLine($"[GridStrategy] Failed orders: {string.Join(", ", result.Errors)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GridStrategy] Error executing grid strategy: {ex.Message}");
            }
        }

        /// <summary>
        /// 그리드 간격(%) 설정
        /// </summary>
        public void SetGridStepPercent(decimal percent)
        {
            _gridStepPercent = percent;
        }

        /// <summary>
        /// 그리드 레벨(개수) 설정
        /// </summary>
        public void SetGridLevels(int levels)
        {
            _gridLevels = levels;
        }
    }
}
