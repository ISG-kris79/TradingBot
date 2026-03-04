using System;
using System.Collections.Generic;

namespace TradingBot.Services
{
    /// <summary>
    /// 거래소별 데이터 변환을 위한 표준 내부 모델 정의 (Phase 11)
    /// Adapter Pattern을 통해 Binance, Bybit, Bitget 간 타입 호환성 문제 해결
    /// </summary>
    /// 
    /// <summary>
    /// 표준화된 주문 방향 (모든 거래소에서 사용)
    /// </summary>
    public enum StandardOrderSide
    {
        Buy = 0,
        Sell = 1
    }

    /// <summary>
    /// 표준화된 캔들 기간 (모든 거래소에서 사용)
    /// </summary>
    public enum StandardKlineInterval
    {
        OneMinute,
        ThreeMinutes,
        FiveMinutes,
        FifteenMinutes,
        ThirtyMinutes,
        OneHour,
        TwoHour,
        FourHour,
        SixHour,
        EightHour,
        TwelveHour,
        OneDay,
        ThreeDay,
        OneWeek,
        OneMonth
    }

    /// <summary>
    /// 표준화된 주문 상태 (모든 거래소에서 사용)
    /// </summary>
    public enum StandardOrderStatus
    {
        New,
        PartiallyFilled,
        Filled,
        Canceled,
        Rejected,
        Expired,
        PendingCancel
    }

    /// <summary>
    /// 표준화된 포지션 정보
    /// </summary>
    public class StandardPosition
    {
        public string Symbol { get; set; } = "";
        public StandardOrderSide Side { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public decimal UnrealizedPnlPercent { get; set; }
        public int Leverage { get; set; }
        public DateTime UpdateTime { get; set; }
    }

    /// <summary>
    /// 표준화된 주문 정보
    /// </summary>
    public class StandardOrder
    {
        public string OrderId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public StandardOrderSide Side { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public decimal QuantityFilled { get; set; }
        public decimal QuantityRemaining => Quantity - QuantityFilled;
        public StandardOrderStatus Status { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public string ExchangeOrderId { get; set; } = "";
    }

    /// <summary>
    /// 표준화된 Ticker 정보
    /// </summary>
    public class StandardTicker
    {
        public string Symbol { get; set; } = "";
        public decimal LastPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public decimal PriceChange { get; set; }
        public decimal PriceChangePercent { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// 표준화된 캔들 정보 (Kline)
    /// </summary>
    public class StandardKline
    {
        public string Symbol { get; set; } = "";
        public StandardKlineInterval Interval { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal LowPrice { get; set; }
        public decimal ClosePrice { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; }
        public int TradeCount { get; set; }
        public decimal TakerBuyBaseAssetVolume { get; set; }
        public decimal TakerBuyQuoteAssetVolume { get; set; }
    }

    /// <summary>
    /// 표준화된 계좌 정보
    /// </summary>
    public class StandardAccountInfo
    {
        public decimal TotalBalance { get; set; }
        public decimal Available { get; set; }
        public decimal Margin { get; set; }
        public decimal UsedMargin { get; set; }
        public List<StandardPosition> Positions { get; set; } = new();
    }

    /// <summary>
    /// Binance 데이터 어댑터
    /// Binance.Net 타입을 표준 모델로 변환
    /// </summary>
    public static class BinanceExchangeAdapter
    {
        /// <summary>
        /// 문자열을 표준 KlineInterval로 변환
        /// </summary>
        public static StandardKlineInterval ConvertStringToKlineInterval(string intervalStr)
        {
            return intervalStr switch
            {
                "1m" or "OneMinute" => StandardKlineInterval.OneMinute,
                "3m" or "ThreeMinutes" => StandardKlineInterval.ThreeMinutes,
                "5m" or "FiveMinutes" => StandardKlineInterval.FiveMinutes,
                "15m" or "FifteenMinutes" => StandardKlineInterval.FifteenMinutes,
                "30m" or "ThirtyMinutes" => StandardKlineInterval.ThirtyMinutes,
                "1h" or "OneHour" => StandardKlineInterval.OneHour,
                "2h" or "TwoHour" => StandardKlineInterval.TwoHour,
                "4h" or "FourHour" => StandardKlineInterval.FourHour,
                "6h" or "SixHour" => StandardKlineInterval.SixHour,
                "8h" or "EightHour" => StandardKlineInterval.EightHour,
                "12h" or "TwelveHour" => StandardKlineInterval.TwelveHour,
                "1d" or "OneDay" => StandardKlineInterval.OneDay,
                "3d" or "ThreeDay" => StandardKlineInterval.ThreeDay,
                "1w" or "OneWeek" => StandardKlineInterval.OneWeek,
                "1M" or "OneMonth" => StandardKlineInterval.OneMonth,
                _ => StandardKlineInterval.OneHour
            };
        }

        /// <summary>
        /// 표준 KlineInterval를 문자열로 변환
        /// </summary>
        public static string ConvertKlineIntervalToString(StandardKlineInterval interval)
        {
            return interval switch
            {
                StandardKlineInterval.OneMinute => "1m",
                StandardKlineInterval.ThreeMinutes => "3m",
                StandardKlineInterval.FiveMinutes => "5m",
                StandardKlineInterval.FifteenMinutes => "15m",
                StandardKlineInterval.ThirtyMinutes => "30m",
                StandardKlineInterval.OneHour => "1h",
                StandardKlineInterval.TwoHour => "2h",
                StandardKlineInterval.FourHour => "4h",
                StandardKlineInterval.SixHour => "6h",
                StandardKlineInterval.EightHour => "8h",
                StandardKlineInterval.TwelveHour => "12h",
                StandardKlineInterval.OneDay => "1d",
                StandardKlineInterval.ThreeDay => "3d",
                StandardKlineInterval.OneWeek => "1w",
                StandardKlineInterval.OneMonth => "1M",
                _ => "1h"
            };
        }

        /// <summary>
        /// Binance OrderSide를 표준 타입으로 변환
        /// </summary>
        public static StandardOrderSide ConvertOrderSide(Binance.Net.Enums.OrderSide side)
        {
            return side switch
            {
                Binance.Net.Enums.OrderSide.Buy => StandardOrderSide.Buy,
                Binance.Net.Enums.OrderSide.Sell => StandardOrderSide.Sell,
                _ => StandardOrderSide.Buy
            };
        }

        /// <summary>
        /// 표준 OrderSide를 Binance 타입으로 변환
        /// </summary>
        public static Binance.Net.Enums.OrderSide ConvertToBinanceOrderSide(StandardOrderSide side)
        {
            return side switch
            {
                StandardOrderSide.Buy => Binance.Net.Enums.OrderSide.Buy,
                StandardOrderSide.Sell => Binance.Net.Enums.OrderSide.Sell,
                _ => Binance.Net.Enums.OrderSide.Buy
            };
        }

        /// <summary>
        /// Binance KlineInterval를 표준 타입으로 변환
        /// </summary>
        public static StandardKlineInterval ConvertKlineInterval(Binance.Net.Enums.KlineInterval interval)
        {
            return interval switch
            {
                Binance.Net.Enums.KlineInterval.OneMinute => StandardKlineInterval.OneMinute,
                Binance.Net.Enums.KlineInterval.ThreeMinutes => StandardKlineInterval.ThreeMinutes,
                Binance.Net.Enums.KlineInterval.FiveMinutes => StandardKlineInterval.FiveMinutes,
                Binance.Net.Enums.KlineInterval.FifteenMinutes => StandardKlineInterval.FifteenMinutes,
                Binance.Net.Enums.KlineInterval.ThirtyMinutes => StandardKlineInterval.ThirtyMinutes,
                Binance.Net.Enums.KlineInterval.OneHour => StandardKlineInterval.OneHour,
                Binance.Net.Enums.KlineInterval.TwoHour => StandardKlineInterval.TwoHour,
                Binance.Net.Enums.KlineInterval.FourHour => StandardKlineInterval.FourHour,
                Binance.Net.Enums.KlineInterval.SixHour => StandardKlineInterval.SixHour,
                Binance.Net.Enums.KlineInterval.EightHour => StandardKlineInterval.EightHour,
                Binance.Net.Enums.KlineInterval.TwelveHour => StandardKlineInterval.TwelveHour,
                Binance.Net.Enums.KlineInterval.OneDay => StandardKlineInterval.OneDay,
                Binance.Net.Enums.KlineInterval.ThreeDay => StandardKlineInterval.ThreeDay,
                Binance.Net.Enums.KlineInterval.OneWeek => StandardKlineInterval.OneWeek,
                Binance.Net.Enums.KlineInterval.OneMonth => StandardKlineInterval.OneMonth,
                _ => StandardKlineInterval.OneHour
            };
        }

        /// <summary>
        /// 표준 KlineInterval를 Binance 타입으로 변환
        /// </summary>
        public static Binance.Net.Enums.KlineInterval ConvertToBinanceKlineInterval(StandardKlineInterval interval)
        {
            return interval switch
            {
                StandardKlineInterval.OneMinute => Binance.Net.Enums.KlineInterval.OneMinute,
                StandardKlineInterval.ThreeMinutes => Binance.Net.Enums.KlineInterval.ThreeMinutes,
                StandardKlineInterval.FiveMinutes => Binance.Net.Enums.KlineInterval.FiveMinutes,
                StandardKlineInterval.FifteenMinutes => Binance.Net.Enums.KlineInterval.FifteenMinutes,
                StandardKlineInterval.ThirtyMinutes => Binance.Net.Enums.KlineInterval.ThirtyMinutes,
                StandardKlineInterval.OneHour => Binance.Net.Enums.KlineInterval.OneHour,
                StandardKlineInterval.TwoHour => Binance.Net.Enums.KlineInterval.TwoHour,
                StandardKlineInterval.FourHour => Binance.Net.Enums.KlineInterval.FourHour,
                StandardKlineInterval.SixHour => Binance.Net.Enums.KlineInterval.SixHour,
                StandardKlineInterval.EightHour => Binance.Net.Enums.KlineInterval.EightHour,
                StandardKlineInterval.TwelveHour => Binance.Net.Enums.KlineInterval.TwelveHour,
                StandardKlineInterval.OneDay => Binance.Net.Enums.KlineInterval.OneDay,
                StandardKlineInterval.ThreeDay => Binance.Net.Enums.KlineInterval.ThreeDay,
                StandardKlineInterval.OneWeek => Binance.Net.Enums.KlineInterval.OneWeek,
                StandardKlineInterval.OneMonth => Binance.Net.Enums.KlineInterval.OneMonth,
                _ => Binance.Net.Enums.KlineInterval.OneHour
            };
        }

        /// <summary>
        /// Binance OrderStatus를 표준 타입으로 변환
        /// </summary>
        public static StandardOrderStatus ConvertOrderStatus(Binance.Net.Enums.OrderStatus status)
        {
            return status switch
            {
                Binance.Net.Enums.OrderStatus.New => StandardOrderStatus.New,
                Binance.Net.Enums.OrderStatus.PartiallyFilled => StandardOrderStatus.PartiallyFilled,
                Binance.Net.Enums.OrderStatus.Filled => StandardOrderStatus.Filled,
                Binance.Net.Enums.OrderStatus.Canceled => StandardOrderStatus.Canceled,
                Binance.Net.Enums.OrderStatus.Rejected => StandardOrderStatus.Rejected,
                Binance.Net.Enums.OrderStatus.Expired => StandardOrderStatus.Expired,
                Binance.Net.Enums.OrderStatus.PendingCancel => StandardOrderStatus.PendingCancel,
                _ => StandardOrderStatus.New
            };
        }

        /// <summary>
        /// 표준 OrderStatus를 Binance 타입으로 변환
        /// </summary>
        public static Binance.Net.Enums.OrderStatus ConvertToBinanceOrderStatus(StandardOrderStatus status)
        {
            return status switch
            {
                StandardOrderStatus.New => Binance.Net.Enums.OrderStatus.New,
                StandardOrderStatus.PartiallyFilled => Binance.Net.Enums.OrderStatus.PartiallyFilled,
                StandardOrderStatus.Filled => Binance.Net.Enums.OrderStatus.Filled,
                StandardOrderStatus.Canceled => Binance.Net.Enums.OrderStatus.Canceled,
                StandardOrderStatus.Rejected => Binance.Net.Enums.OrderStatus.Rejected,
                StandardOrderStatus.Expired => Binance.Net.Enums.OrderStatus.Expired,
                StandardOrderStatus.PendingCancel => Binance.Net.Enums.OrderStatus.PendingCancel,
                _ => Binance.Net.Enums.OrderStatus.New
            };
        }
    }

    /// <summary>
    /// Bybit 데이터 어댑터
    /// Bybit.Net 타입을 표준 모델로 변환
    /// </summary>
    public static class BybitExchangeAdapter
    {
        /// <summary>
        /// Bybit OrderSide를 표준 타입으로 변환
        /// </summary>
        public static StandardOrderSide ConvertOrderSide(Bybit.Net.Enums.OrderSide side)
        {
            return side switch
            {
                Bybit.Net.Enums.OrderSide.Buy => StandardOrderSide.Buy,
                Bybit.Net.Enums.OrderSide.Sell => StandardOrderSide.Sell,
                _ => StandardOrderSide.Buy
            };
        }

        /// <summary>
        /// 표준 OrderSide를 Bybit 타입으로 변환
        /// </summary>
        public static Bybit.Net.Enums.OrderSide ConvertToBybitOrderSide(StandardOrderSide side)
        {
            return side switch
            {
                StandardOrderSide.Buy => Bybit.Net.Enums.OrderSide.Buy,
                StandardOrderSide.Sell => Bybit.Net.Enums.OrderSide.Sell,
                _ => Bybit.Net.Enums.OrderSide.Buy
            };
        }

        /// <summary>
        /// Bybit KlineInterval을 표준 타입으로 변환
        /// Bybit.Net 지원: OneMinute, ThreeMinutes, FiveMinutes, FifteenMinutes, 
        ///               ThirtyMinutes, OneHour, FourHours, OneDay, OneWeek, OneMonth
        /// </summary>
        public static StandardKlineInterval ConvertKlineInterval(Bybit.Net.Enums.KlineInterval interval)
        {
            return interval switch
            {
                Bybit.Net.Enums.KlineInterval.OneMinute => StandardKlineInterval.OneMinute,
                Bybit.Net.Enums.KlineInterval.ThreeMinutes => StandardKlineInterval.ThreeMinutes,
                Bybit.Net.Enums.KlineInterval.FiveMinutes => StandardKlineInterval.FiveMinutes,
                Bybit.Net.Enums.KlineInterval.FifteenMinutes => StandardKlineInterval.FifteenMinutes,
                Bybit.Net.Enums.KlineInterval.ThirtyMinutes => StandardKlineInterval.ThirtyMinutes,
                Bybit.Net.Enums.KlineInterval.OneHour => StandardKlineInterval.OneHour,
                Bybit.Net.Enums.KlineInterval.FourHours => StandardKlineInterval.FourHour,
                Bybit.Net.Enums.KlineInterval.OneDay => StandardKlineInterval.OneDay,
                Bybit.Net.Enums.KlineInterval.OneWeek => StandardKlineInterval.OneWeek,
                Bybit.Net.Enums.KlineInterval.OneMonth => StandardKlineInterval.OneMonth,
                _ => StandardKlineInterval.OneHour
            };
        }

        /// <summary>
        /// 표준 KlineInterval을 Bybit 타입으로 변환
        /// Bybit에서 지원하지 않는 간격은 가장 가까운 값으로 매핑
        /// </summary>
        public static Bybit.Net.Enums.KlineInterval ConvertToBybitKlineInterval(StandardKlineInterval interval)
        {
            return interval switch
            {
                StandardKlineInterval.OneMinute => Bybit.Net.Enums.KlineInterval.OneMinute,
                StandardKlineInterval.ThreeMinutes => Bybit.Net.Enums.KlineInterval.ThreeMinutes,
                StandardKlineInterval.FiveMinutes => Bybit.Net.Enums.KlineInterval.FiveMinutes,
                StandardKlineInterval.FifteenMinutes => Bybit.Net.Enums.KlineInterval.FifteenMinutes,
                StandardKlineInterval.ThirtyMinutes => Bybit.Net.Enums.KlineInterval.ThirtyMinutes,
                StandardKlineInterval.OneHour => Bybit.Net.Enums.KlineInterval.OneHour,
                StandardKlineInterval.TwoHour => Bybit.Net.Enums.KlineInterval.OneHour, // Bybit 미지원, 1시간으로 매핑
                StandardKlineInterval.FourHour => Bybit.Net.Enums.KlineInterval.FourHours,
                StandardKlineInterval.SixHour => Bybit.Net.Enums.KlineInterval.FourHours, // Bybit 미지원, 4시간으로 매핑
                StandardKlineInterval.EightHour => Bybit.Net.Enums.KlineInterval.FourHours, // Bybit 미지원, 4시간으로 매핑
                StandardKlineInterval.TwelveHour => Bybit.Net.Enums.KlineInterval.OneDay, // Bybit 미지원, 1일로 매핑
                StandardKlineInterval.OneDay => Bybit.Net.Enums.KlineInterval.OneDay,
                StandardKlineInterval.ThreeDay => Bybit.Net.Enums.KlineInterval.OneDay, // Bybit 미지원, 1일로 매핑
                StandardKlineInterval.OneWeek => Bybit.Net.Enums.KlineInterval.OneWeek,
                StandardKlineInterval.OneMonth => Bybit.Net.Enums.KlineInterval.OneMonth,
                _ => Bybit.Net.Enums.KlineInterval.OneHour
            };
        }
    }

    /// <summary>
    /// Bitget 데이터 어댑터
    /// Bitget.Net 타입을 표준 모델로 변환
    /// </summary>
    public static class BitgetExchangeAdapter
    {
        /// <summary>
        /// Bitget OrderSide를 표준 타입으로 변환
        /// Bitget API: buy, sell (소문자)
        /// </summary>
        public static StandardOrderSide ConvertOrderSide(string side)
        {
            return side switch
            {
                "buy" or "Buy" => StandardOrderSide.Buy,
                "sell" or "Sell" => StandardOrderSide.Sell,
                _ => StandardOrderSide.Buy
            };
        }

        /// <summary>
        /// 표준 OrderSide를 Bitget 문자열 타입으로 변환
        /// </summary>
        public static string ConvertToBitgetOrderSide(StandardOrderSide side)
        {
            return side switch
            {
                StandardOrderSide.Buy => "buy",
                StandardOrderSide.Sell => "sell",
                _ => "buy"
            };
        }

        /// <summary>
        /// Bitget KlineInterval을 표준 타입으로 변환
        /// Bitget API: 1m, 5m, 15m, 30m, 1h, 4h, 12h, 1d, 1w, 1M 등
        /// </summary>
        public static StandardKlineInterval ConvertKlineInterval(string interval)
        {
            return interval switch
            {
                "1m" or "OneMinute" => StandardKlineInterval.OneMinute,
                "3m" or "ThreeMinutes" => StandardKlineInterval.ThreeMinutes,
                "5m" or "FiveMinutes" => StandardKlineInterval.FiveMinutes,
                "15m" or "FifteenMinutes" => StandardKlineInterval.FifteenMinutes,
                "30m" or "ThirtyMinutes" => StandardKlineInterval.ThirtyMinutes,
                "1h" or "OneHour" => StandardKlineInterval.OneHour,
                "2h" or "TwoHour" => StandardKlineInterval.TwoHour,
                "4h" or "FourHour" => StandardKlineInterval.FourHour,
                "6h" or "SixHour" => StandardKlineInterval.SixHour,
                "8h" or "EightHour" => StandardKlineInterval.EightHour,
                "12h" or "TwelveHour" => StandardKlineInterval.TwelveHour,
                "1d" or "OneDay" => StandardKlineInterval.OneDay,
                "1w" or "OneWeek" => StandardKlineInterval.OneWeek,
                "1M" or "OneMonth" => StandardKlineInterval.OneMonth,
                _ => StandardKlineInterval.OneHour
            };
        }

        /// <summary>
        /// 표준 KlineInterval을 Bitget 문자열 타입으로 변환
        /// </summary>
        public static string ConvertToBitgetKlineInterval(StandardKlineInterval interval)
        {
            return interval switch
            {
                StandardKlineInterval.OneMinute => "1m",
                StandardKlineInterval.ThreeMinutes => "3m",
                StandardKlineInterval.FiveMinutes => "5m",
                StandardKlineInterval.FifteenMinutes => "15m",
                StandardKlineInterval.ThirtyMinutes => "30m",
                StandardKlineInterval.OneHour => "1h",
                StandardKlineInterval.TwoHour => "2h",
                StandardKlineInterval.FourHour => "4h",
                StandardKlineInterval.SixHour => "6h",
                StandardKlineInterval.EightHour => "8h",
                StandardKlineInterval.TwelveHour => "12h",
                StandardKlineInterval.OneDay => "1d",
                StandardKlineInterval.OneWeek => "1w",
                StandardKlineInterval.OneMonth => "1M",
                _ => "1h"
            };
        }
    }
}
