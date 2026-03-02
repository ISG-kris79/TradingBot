# 바이비트 호환성 분석 보고서

**작성일**: 2026-03-02  
**버전**: v2.0.17

---

## 📊 요약

v2.0.17의 주요 수정사항 중 **4개는 바이비트 호환**, **1개는 바이낸스 전용**입니다.

| 수정사항 | 바이낸스 | 바이비트 | 비고 |
|---------|---------|---------|------|
| **1. Race Condition 방지** | ✅ | ✅ | `_exchangeService` 사용 (거래소 무관) |
| **2. 청산가 정확도** | ✅ | ✅ | `_exchangeService.GetPriceAsync` 사용 |
| **3. PUMP 타임아웃** | ✅ | ❌ | `_client.UsdFuturesApi` 직접 사용 (바이낸스 전용) |
| **4. Quantity 업데이트** | ✅ | ✅ | `_exchangeService` 사용 (거래소 무관) |
| **5. 4개 테이블 저장** | ✅ | ✅ | 거래소 무관 (DB 저장) |

---

## ✅ 바이비트 호환 항목 (4개)

### 1. Race Condition 방지

- **파일**: `TradingEngine.cs:1287-1299`
- **사용 API**: `_exchangeService.PlaceOrderAsync` (IExchangeService 인터페이스)
- **바이비트 구현**: `BybitExchangeService.PlaceOrderAsync` (Line 82)
- **호환 여부**: ✅ **완전 호환**
- **검증 방법**:

  ```csharp
  // BybitExchangeService.cs:82-106
  public async Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default)
  {
      var orderSide = side.ToUpper() == "BUY" ? Bybit.Net.Enums.OrderSide.Buy : Bybit.Net.Enums.OrderSide.Sell;
      var orderType = price.HasValue ? Bybit.Net.Enums.NewOrderType.Limit : Bybit.Net.Enums.NewOrderType.Market;
      
      var apiResult = await _client.V5Api.Trading.PlaceOrderAsync(
          Bybit.Net.Enums.Category.Linear,
          symbol,
          orderSide,
          orderType,
          quantity,
          price: price,
          ct: ct
      );
      // ...
  }
  ```

### 2. 청산가 조회 개선

- **파일**: `PositionMonitorService.cs:290-304`
- **사용 API**:
  - `_marketDataManager.TickerCache` (거래소 무관)
  - `_exchangeService.GetPriceAsync` (IExchangeService)
- **바이비트 구현**: `BybitExchangeService.GetPriceAsync` (Line 72)
- **호환 여부**: ✅ **완전 호환**
- **검증 방법**:

  ```csharp
  // BybitExchangeService.cs:72-80
  public async Task<decimal> GetPriceAsync(string symbol, CancellationToken ct = default)
  {
      var apiResult = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(
          Bybit.Net.Enums.Category.Linear, 
          symbol, 
          ct: ct
      );
      // ...
  }
  ```

### 4. Quantity 업데이트

- **파일**: `TradingEngine.cs:1318-1344` (메이저 전략)
- **사용 API**: `_exchangeService.PlaceOrderAsync`
- **바이비트 구현**: 위와 동일
- **호환 여부**: ✅ **완전 호환**

### 5. 4개 테이블 자동 저장

- **파일**: `MarketHistoryService.cs`, `DatabaseService.cs`
- **사용 API**: 없음 (DB 저장 로직)
- **바이비트 구현**: 거래소 무관
- **호환 여부**: ✅ **완전 호환**

---

## ❌ 바이비트 비호환 항목 (1개)

### 3. PUMP 전략 타임아웃 최적화

#### 문제점

- **파일**: `TradingEngine.cs:1028-1130` (`ExecutePumpTrade` 메서드)
- **사용 API**: 바이낸스 클라이언트 직접 사용

  ```csharp
  // Line 1051: 거래소 정보 조회
  var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: token);
  
  // Line 1066: 지정가 주문
  var orderResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
      symbol,
      OrderSide.Buy,
      FuturesOrderType.Limit,
      quantity: quantity,
      price: limitPrice,
      timeInForce: TimeInForce.GoodTillCanceled,
      ct: token);
  
  // Line 1080: 주문 상태 확인
  var orderCheck = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, orderId, ct: token);
  
  // Line 1086, 1116: 주문 취소
  await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId, ct: token);
  ```

- **바이비트 구현**: 없음 (IExchangeService에 지정가 주문 + 상태 확인 메서드 없음)
- **호환 여부**: ❌ **비호환**

#### 영향도 분석

1. **PUMP 전략 설명**:
   - 급등 코인 전용 전략
   - 지정가 주문 (현재가 -0.2%)
   - 3초 대기 후 체결 확인
   - 미체결 시 주문 취소

2. **바이비트에서의 동작**:
   - `ExecutePumpTrade` 호출 시 **런타임 에러 발생 가능**
   - `_client`가 바이비트 클라이언트가 아니므로 `UsdFuturesApi` 접근 불가
   - **PUMP 전략은 바이낸스에서만 작동**

3. **실제 영향**:
   - 바이비트 사용 시: **메이저 전략만 동작** (시장가 주문)
   - PUMP 전략은 **자동으로 스킹되거나 에러 발생**

#### 현재 상황 확인

```csharp
// TradingEngine.cs:133-171
case ExchangeType.Bybit:
    _exchangeService = new BybitExchangeService(AppConfig.BybitApiKey, AppConfig.BybitApiSecret);
    break;
```

- `_exchangeService`는 바이비트로 설정됨
- 하지만 `_client`는 여전히 바이낸스 클라이언트 (`BinanceRestClient`)
- **PUMP 전략 호출 시 바이낸스 API 사용** → 바이비트에서 오류

---

## 🔧 해결 방안

### 방안 1: IExchangeService 확장 (권장)

**장점**: 완전한 거래소 추상화, 유지보수 용이  
**단점**: 인터페이스 수정 필요

#### 1단계: IExchangeService에 메서드 추가

```csharp
public interface IExchangeService
{
    // 기존 메서드...
    
    // 추가: 지정가 주문 (PUMP 전략용)
    Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
        string symbol, 
        string side, 
        decimal quantity, 
        decimal price, 
        CancellationToken ct = default);
    
    // 추가: 주문 상태 확인
    Task<(bool Filled, decimal FilledQuantity)> GetOrderStatusAsync(
        string symbol, 
        string orderId, 
        CancellationToken ct = default);
    
    // 추가: 주문 취소
    Task<bool> CancelOrderAsync(
        string symbol, 
        string orderId, 
        CancellationToken ct = default);
}
```

#### 2단계: BybitExchangeService 구현

```csharp
public async Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
    string symbol, 
    string side, 
    decimal quantity, 
    decimal price, 
    CancellationToken ct = default)
{
    var orderSide = side.ToUpper() == "BUY" ? Bybit.Net.Enums.OrderSide.Buy : Bybit.Net.Enums.OrderSide.Sell;
    
    var apiResult = await _client.V5Api.Trading.PlaceOrderAsync(
        Bybit.Net.Enums.Category.Linear,
        symbol,
        orderSide,
        Bybit.Net.Enums.NewOrderType.Limit,
        quantity,
        price: price,
        ct: ct
    );
    
    if (apiResult.Success)
    {
        return (true, apiResult.Data.OrderId);
    }
    
    HandleError(apiResult);
    return (false, string.Empty);
}

public async Task<(bool Filled, decimal FilledQuantity)> GetOrderStatusAsync(
    string symbol, 
    string orderId, 
    CancellationToken ct = default)
{
    var result = await _client.V5Api.Trading.GetOrdersAsync(
        Bybit.Net.Enums.Category.Linear,
        symbol: symbol,
        orderId: orderId,
        ct: ct
    );
    
    if (!result.Success || result.Data?.List == null)
    {
        HandleError(result);
        return (false, 0);
    }
    
    var order = result.Data.List.FirstOrDefault();
    if (order == null) return (false, 0);
    
    bool isFilled = order.Status == Bybit.Net.Enums.V5.OrderStatus.Filled;
    bool isPartiallyFilled = order.Status == Bybit.Net.Enums.V5.OrderStatus.PartiallyFilled;
    
    return (isFilled || isPartiallyFilled, order.QuantityFilled ?? 0);
}

public async Task<bool> CancelOrderAsync(
    string symbol, 
    string orderId, 
    CancellationToken ct = default)
{
    var result = await _client.V5Api.Trading.CancelOrderAsync(
        Bybit.Net.Enums.Category.Linear, 
        symbol, 
        orderId, 
        ct: ct
    );
    
    if (result.Success) return true;
    HandleError(result);
    return false;
}
```

#### 3단계: TradingEngine.cs 수정

```csharp
public async Task ExecutePumpTrade(string symbol, decimal marginUsdt, float aiScore, CancellationToken token)
{
    try
    {
        // 1. 레버리지 설정
        int leverage = _settings.PumpLeverage;
        await _exchangeService.SetLeverageAsync(symbol, leverage, token);
        
        // 2. 현재가 조회
        decimal currentPrice = await _exchangeService.GetPriceAsync(symbol, token);
        if (currentPrice == 0) return;
        
        decimal limitPrice = currentPrice * 0.998m;
        
        // 3. 수량 계산
        decimal quantity = (marginUsdt * leverage) / limitPrice;
        
        // 4. 거래소 규격 보정
        var exchangeInfo = await _exchangeService.GetExchangeInfoAsync(token);
        var symbolData = exchangeInfo?.Symbols.FirstOrDefault(s => s.Name == symbol);
        if (symbolData != null)
        {
            decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
            quantity = Math.Floor(quantity / stepSize) * stepSize;
            
            decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0.01m;
            limitPrice = Math.Floor(limitPrice / tickSize) * tickSize;
        }
        
        if (quantity <= 0) return;
        
        // 5. 지정가 주문 (IExchangeService 사용)
        var (success, orderId) = await _exchangeService.PlaceLimitOrderAsync(
            symbol,
            "BUY",
            quantity,
            limitPrice,
            token);
        
        if (success)
        {
            OnStatusLog?.Invoke($"⏳ {symbol} 지정가 주문 대기 (가: {limitPrice}, 3초)");
            
            // 6. 3초 대기
            await Task.Delay(3000, token);
            
            // 7. 주문 상태 확인 (IExchangeService 사용)
            var (filled, filledQty) = await _exchangeService.GetOrderStatusAsync(symbol, orderId, token);
            
            if (filled && filledQty > 0)
            {
                // 포지션 등록
                lock (_posLock)
                {
                    _activePositions[symbol] = new PositionInfo
                    {
                        EntryPrice = limitPrice,
                        IsLong = true,
                        Side = OrderSide.Buy,
                        IsPumpStrategy = true,
                        AiScore = aiScore,
                        Leverage = leverage,
                        Quantity = filledQty
                    };
                }
                
                OnAlert?.Invoke($"🚀 {symbol} 진입 성공 (지정가) | 수량: {filledQty}");
            }
            else
            {
                // 미체결 시 취소 (IExchangeService 사용)
                await _exchangeService.CancelOrderAsync(symbol, orderId, token);
                OnStatusLog?.Invoke($"🚫 {symbol} 3초 미체결로 주문 취소");
            }
        }
    }
    catch (Exception ex)
    {
        OnStatusLog?.Invoke($"⚠️ 진입 에러: {ex.Message}");
    }
}
```

---

### 방안 2: 거래소별 분기 처리 (임시 방편)

**장점**: 빠른 구현  
**단점**: 코드 중복, 유지보수 어려움

```csharp
public async Task ExecutePumpTrade(string symbol, decimal marginUsdt, float aiScore, CancellationToken token)
{
    if (_currentExchangeType == ExchangeType.Bybit)
    {
        // 바이비트는 PUMP 전략 지원 안 함
        OnStatusLog?.Invoke($"⚠️ {symbol} PUMP 전략은 바이낸스 전용입니다. (바이비트 미지원)");
        return;
    }
    
    // 기존 바이낸스 코드...
    var orderResult = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(...);
}
```

---

### 방안 3: PUMP 전략 비활성화 (가장 간단)

**장점**: 즉시 적용 가능  
**단점**: 기능 제한

```csharp
// MainWindow 또는 TradingEngine 초기화 시
if (_currentExchangeType == ExchangeType.Bybit)
{
    // PUMP 전략 비활성화
    _pumpStrategyEnabled = false;
    AddLog("⚠️ 바이비트는 메이저 전략만 지원합니다. (PUMP 전략 비활성화)");
}
```

---

## 📋 권장 조치사항

### 즉시 조치 (v2.0.17.1)

1. **방안 3 적용**: PUMP 전략 바이비트 비활성화
   - 런타임 에러 방지
   - 사용자에게 명확한 안내

### 중기 조치 (v2.1.0)

2. **방안 1 적용**: IExchangeService 확장
   - 완전한 거래소 추상화
   - PUMP 전략 바이비트 지원

---

## 🧪 검증 방법

### 바이낸스 환경

```
✅ Race Condition: 동시 진입 방지 확인
✅ 청산가 정확도: PnL 계산 확인
✅ PUMP 타임아웃: 3초 체결 확인
✅ Quantity 업데이트: 포지션 수량 일치
✅ 데이터 저장: 4개 테이블 저장 확인
```

### 바이비트 환경

```
✅ Race Condition: 동시 진입 방지 확인
✅ 청산가 정확도: PnL 계산 확인
❌ PUMP 타임아웃: 미지원 (메이저 전략만 사용)
✅ Quantity 업데이트: 포지션 수량 일치
✅ 데이터 저장: 4개 테이블 저장 확인
```

---

## 📊 테스트 시나리오

### 시나리오 1: 바이낸스 → 바이비트 전환

1. 바이낸스 환경에서 PUMP + 메이저 전략 정상 동작 확인
2. 설정에서 거래소를 바이비트로 변경
3. 엔진 재시작
4. **예상 결과**: 메이저 전략만 동작, PUMP 전략 스킵 또는 에러

### 시나리오 2: 바이비트 신규 설정

1. 처음부터 바이비트로 설정
2. 엔진 시작
3. **예상 결과**:
   - Race Condition 방지 정상 동작 ✅
   - 청산가 정확도 정상 ✅
   - Quantity 업데이트 정상 ✅
   - 데이터 저장 정상 ✅
   - PUMP 전략 미동작 ❌

---

## 📝 결론

**v2.0.17의 5가지 수정사항 중 4개는 바이비트에서 정상 작동하나, PUMP 전략 타임아웃 최적화는 바이낸스 전용 코드이므로 바이비트에서는 비호환입니다.**

즉시 조치로 PUMP 전략을 바이비트에서 비활성화하고, 중기적으로 IExchangeService 인터페이스를 확장하여 완전한 거래소 추상화를 구현하는 것을 권장합니다.
