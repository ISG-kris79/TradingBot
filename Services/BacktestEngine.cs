using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Strategies;

namespace TradingBot.Services
{
    public class BacktestEngine
    {
        private readonly MockExchangeService _mockExchange;
        private readonly DatabaseService _dbService;
        
        // [Phase 5] 진행 상황 이벤트 추가
        public event Action<int>? OnProgress;

        public BacktestEngine()
        {
            _mockExchange = new MockExchangeService(10000); // 초기 자본 10,000 USDT
            _dbService = new DatabaseService();
        }

        public async Task<BacktestResult> RunBacktestAsync(string symbol, DateTime startTime, DateTime endTime)
        {
            // 1. 데이터 로드 (DB에서 과거 캔들 조회)
            var candles = await _dbService.GetTrainingDataAsync(symbol, 5000); // 최근 5000개
            var targetCandles = candles.Where(c => c.OpenTime >= startTime && c.OpenTime <= endTime).OrderBy(c => c.OpenTime).ToList();

            if (!targetCandles.Any()) return new BacktestResult { Message = "데이터 부족" };

            // 2. 전략 초기화 (여기서는 GridStrategy 예시)
            // 실제로는 IStrategy 인터페이스를 통해 주입받아야 함
            // var strategy = new GridStrategy(null, null); // Mock 클라이언트 필요

            decimal initialBalance = await _mockExchange.GetBalanceAsync("USDT");
            int tradeCount = 0;

            // 3. 시뮬레이션 루프
            int totalCandles = targetCandles.Count;
            foreach (var candle in targetCandles)
            {
                // 현재가 업데이트
                _mockExchange.SetCurrentPrice(symbol, (decimal)candle.Close);

                // 전략 실행 로직 (간소화)
                // 예: RSI가 30 미만이면 매수, 70 초과면 매도
                if (candle.RSI < 30)
                {
                    await _mockExchange.PlaceOrderAsync(symbol, "BUY", 0.1m, (decimal)candle.Close);
                    tradeCount++;
                }
                else if (candle.RSI > 70)
                {
                    await _mockExchange.PlaceOrderAsync(symbol, "SELL", 0.1m, (decimal)candle.Close);
                    tradeCount++;
                }

                // 진행률 업데이트 (100개 단위)
                int index = targetCandles.IndexOf(candle);
                if (index % 100 == 0) OnProgress?.Invoke((int)((double)index / totalCandles * 100));
            }

            // 4. 결과 계산
            decimal finalBalance = await _mockExchange.GetBalanceAsync("USDT");
            var positions = await _mockExchange.GetPositionsAsync();
            
            // 미청산 포지션 가치 반영
            foreach(var pos in positions)
            {
                decimal currentPrice = (decimal)targetCandles.Last().Close;
                decimal pnl = pos.IsLong ? (currentPrice - pos.EntryPrice) * pos.Quantity 
                                         : (pos.EntryPrice - currentPrice) * pos.Quantity;
                finalBalance += pnl;
            }

            return new BacktestResult
            {
                Symbol = symbol,
                InitialBalance = initialBalance,
                FinalBalance = finalBalance,
                TotalTrades = tradeCount
            };
        }
    }
}