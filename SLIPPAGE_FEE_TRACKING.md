# 슬리피지 & 수수료 추적 개선 가이드

## 현재 문제

사용자 보고: SOLUSDT 청산 시 예상 손실 vs 실제 손실 차이 발생
- **시스템 계산**: -9.05 USDT (-4.53%)
- **실제 거래소**: -13.05 USDT
- **차이**: 약 4.00 USDT (30.6% 오차)

## 원인 분석

### 1. 거래 수수료 미반영
```
Binance Futures 수수료 구조:
- Maker: 0.02%
- Taker: 0.04%
- 시장가 주문 왕복: 0.08% (진입 0.04% + 청산 0.04%)
```

### 2. 슬리피지 미추적
```
현재 청산가 조회 순서:
1. TickerCache (마지막 체결가)
2. GetPriceAsync API (현재가)
3. EntryPrice (폴백)

⚠️ 문제: 실제 시장가 주문 체결가와 다를 수 있음
```

### 3. 펀딩비 미추적
- 포지션 보유 중 8시간마다 발생
- 현재 시스템에서 추적하지 않음

## 개선 방안

### Phase 1: 데이터베이스 스키마 확장

```sql
-- TradeHistory 테이블에 컬럼 추가
ALTER TABLE dbo.TradeHistory ADD Fee DECIMAL(18,8) NULL;
ALTER TABLE dbo.TradeHistory ADD EstimatedExitPrice DECIMAL(18,8) NULL;
ALTER TABLE dbo.TradeHistory ADD ActualExitPrice DECIMAL(18,8) NULL;
ALTER TABLE dbo.TradeHistory ADD Slippage DECIMAL(18,8) NULL;
ALTER TABLE dbo.TradeHistory ADD SlippagePct DECIMAL(18,8) NULL;
ALTER TABLE dbo.TradeHistory ADD FundingFee DECIMAL(18,8) NULL;
```

### Phase 2: 모델 확장

**TradingBot.Shared.Models.TradeLog**에 추가:
```csharp
public decimal Fee { get; set; } = 0m;
public decimal EstimatedExitPrice { get; set; } = 0m;
public decimal ActualExitPrice { get; set; } = 0m;
public decimal Slippage { get; set; } = 0m;
public decimal SlippagePct { get; set; } = 0m;
public decimal FundingFee { get; set; } = 0m;
```

### Phase 3: API 응답 확장

**IExchangeService.PlaceOrderAsync 반환값 변경**:
```csharp
// Before
Task<bool> PlaceOrderAsync(...);

// After
Task<(bool Success, decimal ExecutedPrice, decimal ExecutedQty, decimal Fee)> PlaceOrderAsync(...);
```

**BinanceExchangeService 구현**:
```csharp
public async Task<(bool Success, decimal ExecutedPrice, decimal ExecutedQty, decimal Fee)> PlaceOrderAsync(...)
{
    var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(...);
    
    if (!result.Success) return (false, 0, 0, 0);
    
    // 실제 체결 정보 조회
    var order = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, result.Data.Id);
    
    return (
        true,
        order.Data.AvgPrice ?? 0,      // 실제 체결가
        order.Data.QuantityFilled,      // 실제 체결량
        order.Data.CommissionAmount ?? 0 // 수수료
    );
}
```

### Phase 4: PnL 계산 개선

**PositionMonitorService.SaveCloseTradeToDbAsync**:
```csharp
private async Task SaveCloseTradeToDbAsync(...)
{
    // 청산 주문 실행 및 실제 체결 정보 수집
    var orderResult = await _exchangeService.PlaceOrderAsync(...);
    
    decimal actualExitPrice = orderResult.ExecutedPrice;
    decimal fee = orderResult.Fee;
    
    // 슬리피지 계산
    decimal estimatedExitPrice = cached?.LastPrice ?? position.EntryPrice;
    decimal slippage = actualExitPrice - estimatedExitPrice;
    decimal slippagePct = (slippage / estimatedExitPrice) * 100m;
    
    // 실제 PnL 계산 (수수료 포함)
    decimal pnl = isLongPosition
        ? (actualExitPrice - position.EntryPrice) * absQty - fee - entryFee
        : (position.EntryPrice - actualExitPrice) * absQty - fee - entryFee;
    
    var log = new TradeLog(...)
    {
        Fee = fee,
        EstimatedExitPrice = estimatedExitPrice,
        ActualExitPrice = actualExitPrice,
        Slippage = slippage,
        SlippagePct = slippagePct
    };
}
```

### Phase 5: 진입 수수료 추적

**포지션 진입 시 수수료 저장**:
```csharp
public class PositionInfo
{
    public decimal EntryFee { get; set; } = 0m;
    // ... 기존 필드들
}

// 진입 시
var entryResult = await _exchangeService.PlaceOrderAsync(...);
position.EntryFee = entryResult.Fee;
```

## 임시 회피 방법 (즉시 적용 가능)

**수수료 및 슬리피지 예상치 적용**:
```csharp
// SaveCloseTradeToDbAsync 수정
decimal estimatedFee = (position.EntryPrice * absQty * 0.0004m) + (exitPrice * absQty * 0.0004m);
decimal estimatedSlippage = exitPrice * 0.001m; // 0.1% 예상 슬리피지

decimal adjustedPnl = pnl - estimatedFee - (estimatedSlippage * absQty);
```

## 검증 방법

1. **Binance 거래 내역에서 확인**:
   - Account → Transaction History → Realized PnL
   - 실제 수수료 및 체결가 확인

2. **로그 비교**:
   ```
   [청산 확인] SOLUSDT
   - 예상 청산가: 230.50 (TickerCache)
   - 실제 체결가: 229.80 (API 응답)
   - 슬리피지: -0.70 (-0.30%)
   - 진입 수수료: 0.08 USDT
   - 청산 수수료: 0.08 USDT
   - 총 비용: 0.86 USDT
   ```

## 우선순위

1. ✅ **즉시**: 임시 회피 방법 적용 (예상치 기반)
2. 🔨 **단기**: API 응답 확장 및 실제 체결가 추적
3. 📊 **중기**: DB 스키마 확장 및 완전한 추적
4. 🎯 **장기**: 펀딩비 통합 추적

## 참고 링크

- [Binance Fee Structure](https://www.binance.com/en/fee/futureFee)
- [Binance API - Order Response](https://binance-docs.github.io/apidocs/futures/en/#new-order-trade)
