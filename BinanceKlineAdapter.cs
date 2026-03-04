using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class BinanceKlineAdapter : IBinanceKline
    {
        public KlineInterval Interval { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TradeCount { get; set; }
        public decimal TakerBuyBaseVolume { get; set; }
        public decimal TakerBuyQuoteVolume { get; set; }

        public BinanceKlineAdapter(CandleData candle, KlineInterval interval = KlineInterval.OneMinute)
        {
            Interval = interval;
            OpenTime = candle.OpenTime;
            CloseTime = candle.CloseTime;
            OpenPrice = candle.Open;
            HighPrice = candle.High;
            LowPrice = candle.Low;
            ClosePrice = candle.Close;
            Volume = (decimal)candle.Volume;
            QuoteVolume = 0; // Default or derive if possible
            TradeCount = 0; // Default
            TakerBuyBaseVolume = 0; // Default
            TakerBuyQuoteVolume = 0; // Default
        }
    }
}
