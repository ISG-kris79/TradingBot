using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Interfaces.Clients;
using TradingBot.Services;
using static TradingBot.Services.UnifiedLogger;

namespace TradingBot.AI
{
    /// <summary>
    /// 오더북 Feature 추출기
    /// 시장 미시구조 분석 (대형 세력 움직임 감지)
    /// </summary>
    public class OrderBookFeatureExtractor
    {
        private readonly IBinanceRestClient _binanceClient;
        
        public OrderBookFeatureExtractor(IBinanceRestClient binanceClient)
        {
            _binanceClient = binanceClient;
        }
        
        /// <summary>
        /// 오더북 Feature 추출
        /// </summary>
        public async Task<OrderBookFeatures?> ExtractAsync(string symbol, CancellationToken token = default)
        {
            try
            {
                // Binance 오더북 조회 (20 레벨)
                var orderBookResult = await _binanceClient.SpotApi.ExchangeData.GetOrderBookAsync(
                    symbol,
                    limit: 20,
                    ct: token);
                
                if (!orderBookResult.Success || orderBookResult.Data == null)
                {
                    return null;
                }
                
                var orderBook = orderBookResult.Data;
                var bids = orderBook.Bids.ToList();
                var asks = orderBook.Asks.ToList();
                
                if (bids.Count == 0 || asks.Count == 0)
                    return null;
                
                // 1. 매수/매도 총 물량
                decimal totalBidVolume = bids.Sum(b => b.Quantity);
                decimal totalAskVolume = asks.Sum(a => a.Quantity);
                
                // 2. 불균형 비율 (-1 ~ +1, 양수=매수 우세)
                decimal volumeImbalance = totalBidVolume + totalAskVolume > 0
                    ? (totalBidVolume - totalAskVolume) / (totalBidVolume + totalAskVolume)
                    : 0m;
                
                // 3. 스프레드
                decimal bestBid = bids[0].Price;
                decimal bestAsk = asks[0].Price;
                decimal spread = bestAsk - bestBid;
                decimal spreadPercent = bestBid > 0 ? (spread / bestBid * 100m) : 0m;
                
                // 4. 중간 가격 (Mid Price)
                decimal midPrice = (bestBid + bestAsk) / 2m;
                
                // 5. 깊이 분석 (5단계 물량)
                decimal bid5Depth = bids.Take(5).Sum(b => b.Quantity);
                decimal ask5Depth = asks.Take(5).Sum(a => a.Quantity);
                decimal depth5Imbalance = bid5Depth + ask5Depth > 0
                    ? (bid5Depth - ask5Depth) / (bid5Depth + ask5Depth)
                    : 0m;
                
                // 6. 벽(Wall) 감지 (큰 주문)
                decimal avgBidSize = bids.Average(b => b.Quantity);
                decimal avgAskSize = asks.Average(a => a.Quantity);
                
                var bidWalls = bids.Where(b => b.Quantity > avgBidSize * 5).ToList();  // 평균 5배 이상
                var askWalls = asks.Where(a => a.Quantity > avgAskSize * 5).ToList();
                
                bool hasBidWall = bidWalls.Count > 0;
                bool hasAskWall = askWalls.Count > 0;
                
                decimal bidWallDistance = hasBidWall ? (midPrice - bidWalls.Max(w => w.Price)) / midPrice * 100m : 0m;
                decimal askWallDistance = hasAskWall ? (askWalls.Min(w => w.Price) - midPrice) / midPrice * 100m : 0m;
                
                // 7. 가중 평균 가격 (Volume Weighted Avg Price - VWAP)
                decimal bidVWAP = bids.Sum(b => b.Price * b.Quantity) / (totalBidVolume > 0 ? totalBidVolume : 1m);
                decimal askVWAP = asks.Sum(a => a.Price * a.Quantity) / (totalAskVolume > 0 ? totalAskVolume : 1m);
                
                // 8. 대형 주문 비율 (Top 3 vs 전체)
                decimal top3BidVolume = bids.Take(3).Sum(b => b.Quantity);
                decimal top3AskVolume = asks.Take(3).Sum(a => a.Quantity);
                
                float largeOrderRatioBid = totalBidVolume > 0 ? (float)(top3BidVolume / totalBidVolume) : 0f;
                float largeOrderRatioAsk = totalAskVolume > 0 ? (float)(top3AskVolume / totalAskVolume) : 0f;
                
                return new OrderBookFeatures
                {
                    // 기본 정보
                    BestBid = (float)bestBid,
                    BestAsk = (float)bestAsk,
                    MidPrice = (float)midPrice,
                    SpreadPercent = (float)spreadPercent,
                    
                    // 불균형 지표 (-1 ~ +1)
                    VolumeImbalance = (float)volumeImbalance,
                    Depth5Imbalance = (float)depth5Imbalance,
                    
                    // 물량 정보
                    TotalBidVolume = (float)totalBidVolume,
                    TotalAskVolume = (float)totalAskVolume,
                    Bid5Depth = (float)bid5Depth,
                    Ask5Depth = (float)ask5Depth,
                    
                    // 벽 정보
                    HasBidWall = hasBidWall,
                    HasAskWall = hasAskWall,
                    BidWallDistancePercent = (float)bidWallDistance,
                    AskWallDistancePercent = (float)askWallDistance,
                    
                    // VWAP
                    BidVWAP = (float)bidVWAP,
                    AskVWAP = (float)askVWAP,
                    
                    // 대형 주문 비율
                    LargeOrderRatioBid = largeOrderRatioBid,
                    LargeOrderRatioAsk = largeOrderRatioAsk,
                    
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                Error(LogCategory.Exchange, $"[OrderBook] {symbol} Feature 추출 실패", ex);
                return null;
            }
        }
        
        /// <summary>
        /// 시장 미시구조 분석결과
        /// </summary>
        public enum MarketMicrostructure
        {
            BuyPressure,      // 매수 압력 (벽이 위에, 불균형 양수)
            SellPressure,     // 매도 압력 (벽이 아래, 불균형 음수)
            Balanced,         // 균형 (불균형 작음)
            SpoofingRisk,     // 스푸핑 의심 (벽이 양쪽에 큼)
            LowLiquidity      // 낮은 유동성 (총 물량 작음)
        }
        
        /// <summary>
        /// 오더북 기반 시장 상태 판단
        /// </summary>
        public static MarketMicrostructure AnalyzeMarket(OrderBookFeatures features)
        {
            // 1. 유동성 체크
            if (features.TotalBidVolume + features.TotalAskVolume < 100f)
            {
                return MarketMicrostructure.LowLiquidity;
            }
            
            // 2. 스푸핑 의심 (양쪽 다 벽)
            if (features.HasBidWall && features.HasAskWall)
            {
                return MarketMicrostructure.SpoofingRisk;
            }
            
            // 3. 매수 압력
            if (features.VolumeImbalance > 0.3f || (features.HasBidWall && !features.HasAskWall))
            {
                return MarketMicrostructure.BuyPressure;
            }
            
            // 4. 매도 압력
            if (features.VolumeImbalance < -0.3f || (!features.HasBidWall && features.HasAskWall))
            {
                return MarketMicrostructure.SellPressure;
            }
            
            // 5. 균형
            return MarketMicrostructure.Balanced;
        }
    }
    
    /// <summary>
    /// 오더북 Feature 데이터 구조
    /// </summary>
    public class OrderBookFeatures
    {
        // 기본 정보
        public float BestBid { get; set; }
        public float BestAsk { get; set; }
        public float MidPrice { get; set; }
        public float SpreadPercent { get; set; }
        
        // 불균형 지표 (-1 = 매도 우세, +1 = 매수 우세)
        public float VolumeImbalance { get; set; }
        public float Depth5Imbalance { get; set; }
        
        // 물량 정보
        public float TotalBidVolume { get; set; }
        public float TotalAskVolume { get; set; }
        public float Bid5Depth { get; set; }
        public float Ask5Depth { get; set; }
        
        // 벽 정보
        public bool HasBidWall { get; set; }
        public bool HasAskWall { get; set; }
        public float BidWallDistancePercent { get; set; }
        public float AskWallDistancePercent { get; set; }
        
        // VWAP
        public float BidVWAP { get; set; }
        public float AskVWAP { get; set; }
        
        // 대형 주문 비율
        public float LargeOrderRatioBid { get; set; }
        public float LargeOrderRatioAsk { get; set; }
        
        public DateTime Timestamp { get; set; }
        
        /// <summary>
        /// ML Feature로 변환 (14차원)
        /// </summary>
        public float[] ToMLFeatures()
        {
            return new[]
            {
                SpreadPercent / 0.1f,  // 정규화 (0.1% 기준)
                VolumeImbalance,  // 이미 -1~1
                Depth5Imbalance,  // 이미 -1~1
                Math.Min(TotalBidVolume / 1000f, 1f),  // 최대 1000 기준
                Math.Min(TotalAskVolume / 1000f, 1f),
                HasBidWall ? 1f : 0f,
                HasAskWall ? 1f : 0f,
                BidWallDistancePercent / 5f,  // 5% 기준
                AskWallDistancePercent / 5f,
                (BidVWAP - MidPrice) / MidPrice * 100f / 0.5f,  // 0.5% 기준
                (AskVWAP - MidPrice) / MidPrice * 100f / 0.5f,
                LargeOrderRatioBid,
                LargeOrderRatioAsk,
                Math.Min(Bid5Depth / Ask5Depth, 2f) / 2f  // 비율 0~2 → 0~1
            };
        }
    }
}
