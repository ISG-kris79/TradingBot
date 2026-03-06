using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Services
{
    public static class AdvancedIndicators
    {
        // 일목균형표 (Ichimoku Cloud)
        public static (decimal Tenkan, decimal Kijun, decimal SenkouA, decimal SenkouB, decimal Chikou) CalculateIchimoku(List<IBinanceKline> candles)
        {
            if (candles == null || candles.Count < 52) return (0, 0, 0, 0, 0);

            // 전환선 (9일)
            var high9 = candles.TakeLast(9).Max(c => c.HighPrice);
            var low9 = candles.TakeLast(9).Min(c => c.LowPrice);
            var tenkan = (high9 + low9) / 2;

            // 기준선 (26일)
            var high26 = candles.TakeLast(26).Max(c => c.HighPrice);
            var low26 = candles.TakeLast(26).Min(c => c.LowPrice);
            var kijun = (high26 + low26) / 2;

            // 선행스팬 A (26일 앞)
            var senkouA = (tenkan + kijun) / 2;

            // 선행스팬 B (52일 고저평균, 26일 앞)
            var high52 = candles.TakeLast(52).Max(c => c.HighPrice);
            var low52 = candles.TakeLast(52).Min(c => c.LowPrice);
            var senkouB = (high52 + low52) / 2;

            // 후행스팬 (현재 종가를 26일 뒤로 미룸 - 여기서는 현재값만 반환)
            var chikou = candles[candles.Count - 1].ClosePrice;

            return (tenkan, kijun, senkouA, senkouB, chikou);
        }

        // VWAP (Volume Weighted Average Price)
        public static decimal CalculateVWAP(List<IBinanceKline> candles)
        {
            if (candles == null || !candles.Any()) return 0;

            decimal cumulativeTPV = 0; // Typical Price * Volume
            decimal cumulativeVolume = 0;

            foreach (var candle in candles)
            {
                decimal typicalPrice = (candle.HighPrice + candle.LowPrice + candle.ClosePrice) / 3;
                cumulativeTPV += typicalPrice * candle.Volume;
                cumulativeVolume += candle.Volume;
            }

            return cumulativeVolume == 0 ? 0 : cumulativeTPV / cumulativeVolume;
        }
    }
}
