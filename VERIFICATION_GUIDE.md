# v2.0.17 검증 가이드

## 검증 개요

이 문서는 v2.0.17 릴리스의 5가지 Critical 수정사항을 검증하는 방법을 설명합니다.

---

## ✅ 검증 항목 1: Race Condition 방지

### 목표

동시에 여러 매수 신호가 발생해도 중복 진입이 발생하지 않는지 확인

### 수정 내역

- **파일**: `TradingEngine.cs:1287-1299`
- **변경**: 락 내에서 임시 포지션 즉시 등록 (Quantity=0) → 주문 실행 → 성공 시 수량 업데이트

### 검증 방법

#### 코드 확인 ✅

```csharp
// TradingEngine.cs:1287-1299
lock (_posLock)
{
    if (_activePositions.ContainsKey(symbol)) return;  // 중복 체크
    
    // 임시 등록 (0 수량)
    _activePositions[symbol] = new PositionInfo
    {
        EntryPrice = currentPrice,
        IsLong = (decision == "LONG"),
        Side = (decision == "LONG") ? OrderSide.Buy : OrderSide.Sell,
        IsPumpStrategy = false,
        AiScore = aiScore,
        Leverage = leverage,
        Quantity = 0  // 주문 성공 후 업데이트
    };
}

// 락 해제 후 주문 실행
bool success = await _exchangeService.PlaceOrderAsync(...);

if (success)
{
    lock (_posLock)
    {
        if (_activePositions.TryGetValue(symbol, out var pos))
        {
            pos.Quantity = quantity;  // 수량 업데이트
        }
    }
}
```

**✅ 코드 확인 완료**: 락 내에서 즉시 등록되므로 동시 진입 불가능

#### 실전 테스트

1. **시나리오**: 변동성이 큰 시간대 (뉴스, 이벤트)에 엔진 실행
2. **관찰 지표**:
   - UI 로그에서 같은 심볼에 대한 "이미 포지션 진행 중" 메시지 확인
   - Binance API에서 실제 포지션 수 확인 (중복 없어야 함)
3. **예상 결과**:

   ```
   📊 BTCUSDT LONG 신호 감지
   ⚠️ BTCUSDT 이미 포지션 진행 중 (스킵)
   ```

---

## ✅ 검증 항목 2: 청산가 정확도

### 목표

청산 시 PnL 계산에 사용되는 가격이 실제 시장가와 일치하는지 확인

### 수정 내역

- **파일**: `PositionMonitorService.cs:290-304`
- **변경**: 캐시 우선 조회 (`TickerCache`) → 0이면 API 폴백 (`GetPriceAsync`)

### 검증 방법

#### 코드 확인 ✅

```csharp
// PositionMonitorService.cs:293-302
decimal exitPrice = 0;
if (_marketDataManager.TickerCache.TryGetValue(symbol, out var cached))
{
    exitPrice = cached.LastPrice;
}

if (exitPrice == 0)
{
    exitPrice = await _exchangeService.GetPriceAsync(symbol, ct: token);
}

decimal pnl = position.Quantity > 0 
    ? (exitPrice - position.EntryPrice) * absQty 
    : (position.EntryPrice - exitPrice) * absQty;
```

**✅ 코드 확인 완료**: 캐시 우선, 0이면 API 폴백 로직 정상

#### 실전 테스트

1. **시나리오**: 포지션 청산 발생 시
2. **확인 항목**:

   ```sql
   -- TradeLogs 테이블에서 최근 청산 데이터 조회
   SELECT TOP 10 
       Symbol, 
       Price AS ExitPrice,  -- DB 기록 가격
       PnL, 
       PnLPercent,
       Time
   FROM TradeLogs
   WHERE Strategy = 'MarketClose'
   ORDER BY Time DESC
   ```

3. **비교 대상**: Binance UI 또는 API에서 실제 청산가 확인
4. **예상 결과**: DB의 Price와 Binance 청산가가 1% 이내 오차

---

## ✅ 검증 항목 3: PUMP 전략 3초 타임아웃

### 목표

지정가 주문 대기 시간 단축으로 체결률이 향상되었는지 확인

### 수정 내역

- **파일**: `TradingEngine.cs:1074`
- **변경**: `await Task.Delay(5000)` → `await Task.Delay(3000)`

### 검증 방법

#### 코드 확인 ✅

```csharp
// TradingEngine.cs:1074
await Task.Delay(3000, token);  // 5초 → 3초
```

**✅ 코드 확인 완료**: 3초로 변경됨

#### 실전 테스트

1. **모니터링 기간**: 24시간
2. **수집 데이터**:

   ```sql
   -- PUMP 전략 체결률 통계
   SELECT 
       COUNT(*) AS TotalSignals,
       SUM(CASE WHEN PnL IS NOT NULL THEN 1 ELSE 0 END) AS FilledOrders,
       CAST(SUM(CASE WHEN PnL IS NOT NULL THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*) * 100 AS FillRate
   FROM TradeLogs
   WHERE Strategy LIKE '%PUMP%'
       AND Time >= DATEADD(HOUR, -24, GETDATE())
   ```

3. **UI 로그 확인**:

   ```
   ⏳ SOLUSDT 지정가 주문 대기 (가: 120.50, 3초)
   🚀 SOLUSDT 진입 성공 (지정가) | 수량: 0.5
   ```

   또는

   ```
   🚫 SOLUSDT 3초 미체결로 주문 취소
   ```

4. **목표**: 체결률 70% 이상

---

## ✅ 검증 항목 4: 부분 체결 처리

### 목표

낮은 유동성 코인에서 부분 체결 발생 시 정상 처리되는지 확인

### 수정 내역

- **파일**: `TradingEngine.cs:1086-1090, 1335-1341`
- **변경**: 부분 체결 감지 및 잔량 취소, 수량 업데이트 로직 추가

### 검증 방법

#### 코드 확인 ✅

```csharp
// PUMP 전략 (TradingEngine.cs:1086-1090)
if (status == OrderStatus.PartiallyFilled)
{
    await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, orderId, ct: token);
    OnStatusLog?.Invoke($"✂️ {symbol} 부분 체결 후 잔량 취소");
}

// 메이저 전략 (TradingEngine.cs:1335-1341)
if (success)
{
    lock (_posLock)
    {
        if (_activePositions.TryGetValue(symbol, out var pos))
        {
            pos.Quantity = quantity;  // 실제 체결 수량으로 업데이트
        }
    }
}
```

**✅ 코드 확인 완료**: PUMP 전략은 잔량 취소, 메이저 전략은 수량 업데이트

#### 실전 테스트

1. **테스트 대상**: 유동성 낮은 코인 (예: 시가총액 100위 이하)
2. **시나리오**:
   - PUMP 전략으로 급등 코인 진입 시도
   - 주문량 > 실제 체결량 상황 유도
3. **확인 항목**:

   ```
   ✂️ LOWLIQUIDCOIN 부분 체결 후 잔량 취소
   ```

4. **DB 확인**:

   ```sql
   SELECT Symbol, Side, Price, PnL, Time
   FROM TradeLogs
   WHERE Symbol = 'LOWLIQUIDCOIN'
   ORDER BY Time DESC
   ```

5. **예상 결과**:
   - PUMP: 부분 체결 수량만 포지션 등록, 잔량 취소
   - 메이저: 실제 체결 수량과 DB 일치

---

## ✅ 검증 항목 5: 4개 테이블 데이터 저장

### 목표

MarketCandles, CandleData, CandleHistory, MarketData 4개 테이블에 데이터가 정상 저장되는지 확인

### 수정 내역

- **파일**:
  - `DatabaseService.cs`: 3개 메서드 추가 (SaveCandleDataBulkAsync, SaveCandleHistoryBulkAsync, SaveMarketDataBulkAsync)
  - `MarketHistoryService.cs`: 4개 테이블 병렬 저장

### 검증 방법

#### 코드 확인 ✅

```csharp
// MarketHistoryService.cs:52-59
var tasks = new List<Task>
{
    _databaseService.BulkInsertMarketDataAsync(candleDataList),  // MarketCandles
    _databaseService.SaveCandleDataBulkAsync(candleDataList),    // CandleData
    _databaseService.SaveCandleHistoryBulkAsync(candleDataList), // CandleHistory
    _databaseService.SaveMarketDataBulkAsync(candleDataList)     // MarketData
};
await Task.WhenAll(tasks);
```

**✅ 코드 확인 완료**: 4개 테이블 병렬 저장 로직 정상

#### 실전 테스트

1. **엔진 시작 후 즉시**: 첫 저장 실행 확인

   ```
   📊 [MarketHistory] 캔들 데이터 자동 저장 시작 (5분 주기)
   ✅ [DB] 20건 저장 완료 (CandleData)
   ✅ [DB] 20건 저장 완료 (CandleHistory)
   ✅ [DB] 20건 저장 완료 (MarketData)
   ✅ [DB] 20건 지표 데이터 저장 완료 (MarketCandles)
   ✅ [MarketHistory] 80건 × 4개 테이블 저장 완료 (...)
   ```

2. **5분 후 DB 조회**:

   ```sql
   -- 각 테이블 데이터 확인
   SELECT COUNT(*) AS MarketCandlesCount FROM MarketCandles
   SELECT COUNT(*) AS CandleDataCount FROM CandleData
   SELECT COUNT(*) AS CandleHistoryCount FROM CandleHistory
   SELECT COUNT(*) AS MarketDataCount FROM MarketData
   
   -- 최근 저장 데이터 샘플 (BTCUSDT)
   SELECT TOP 5 Symbol, OpenTime, ClosePrice, Volume
   FROM MarketCandles
   WHERE Symbol = 'BTCUSDT'
   ORDER BY OpenTime DESC
   ```

3. **예상 결과**:
   - 각 테이블에 동일한 수량의 데이터 존재
   - OpenTime, Symbol 조합이 중복되지 않음
   - 5분마다 증가하는 데이터 확인

4. **10분 후 재확인**: 데이터가 계속 추가되는지 확인

---

## 📋 검증 체크리스트

### Race Condition 방지

- [x] 코드 확인: 락 내 즉시 등록 로직 존재
- [ ] 실전 테스트: 동시 신호 발생 시나리오
- [ ] 결과 확인: Binance API 포지션 수 = 앱 내 포지션 수

### 청산가 정확도

- [x] 코드 확인: 캐시 우선, API 폴백 로직 존재
- [ ] 실전 테스트: 청산 발생 후 DB vs Binance 비교
- [ ] 결과 확인: 오차율 1% 이내

### PUMP 타임아웃

- [x] 코드 확인: 3초로 변경됨
- [ ] 실전 테스트: 24시간 모니터링
- [ ] 결과 확인: 체결률 70% 이상

### 부분 체결

- [x] 코드 확인: PUMP 잔량 취소, 메이저 수량 업데이트
- [ ] 실전 테스트: 낮은 유동성 코인 진입
- [ ] 결과 확인: 포지션 수량 = 실제 체결 수량

### 4개 테이블 저장

- [x] 코드 확인: 병렬 저장 로직 존재
- [ ] 실전 테스트: 엔진 시작 후 5분 대기
- [ ] 결과 확인: 4개 테이블 데이터 동일 수량

---

## 🚨 문제 발생 시 대응

### Race Condition 중복 진입 발생

- **증상**: 같은 심볼에 2개 이상 포지션 존재
- **확인**: `_activePositions` 딕셔너리 로깅 추가
- **해결**: 락 범위 확장 또는 ConcurrentDictionary 사용 검토

### 청산가 오차 큼

- **증상**: DB 가격과 Binance 청산가 차이 5% 이상
- **확인**: TickerCache 업데이트 주기 확인
- **해결**: API 직접 조회로 우선순위 변경

### PUMP 체결률 낮음

- **증상**: 체결률 50% 미만
- **확인**: 지정가 계산 로직 (currentPrice - 0.2%) 검토
- **해결**: 0.3~0.5%로 범위 확대 또는 타임아웃 3초 → 5초 복원

### 부분 체결 미처리

- **증상**: 포지션 수량 != 실제 체결 수량
- **확인**: OrderStatus 체크 로직 확인
- **해결**: GetOrderAsync 응답 로깅 추가

### 테이블 저장 실패

- **증상**: UI에 "❌ [DB] 저장 실패" 표시
- **확인**: DB 연결 문자열, 테이블 스키마 확인
- **해결**:
  1. 테이블 존재 여부 확인

     ```sql
     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
     WHERE TABLE_NAME IN ('MarketCandles', 'CandleData', 'CandleHistory', 'MarketData')
     ```

  2. 컬럼 매칭 확인 (Symbol, OpenTime, Open, High, Low, Close, Volume)

---

## 📊 모니터링 대시보드 SQL

```sql
-- 1. Race Condition 체크 (중복 포지션)
SELECT Symbol, COUNT(*) AS PositionCount
FROM (
    SELECT DISTINCT Symbol, EntryTime
    FROM TradeLogs
    WHERE Time >= DATEADD(HOUR, -24, GETDATE())
) AS Positions
GROUP BY Symbol
HAVING COUNT(*) > 1

-- 2. 청산가 정확도 (편차 확인)
SELECT 
    Symbol,
    Price AS ExitPrice,
    PnL,
    PnLPercent,
    Time
FROM TradeLogs
WHERE Strategy = 'MarketClose'
    AND Time >= DATEADD(HOUR, -24, GETDATE())
ORDER BY Time DESC

-- 3. PUMP 전략 통계
SELECT 
    COUNT(*) AS TotalSignals,
    SUM(CASE WHEN PnL IS NOT NULL THEN 1 ELSE 0 END) AS FilledOrders,
    AVG(PnL) AS AvgPnL,
    AVG(PnLPercent) AS AvgPnLPercent
FROM TradeLogs
WHERE Strategy LIKE '%PUMP%'
    AND Time >= DATEADD(HOUR, -24, GETDATE())

-- 4. 데이터 저장 현황
SELECT 
    'MarketCandles' AS TableName, COUNT(*) AS RowCount FROM MarketCandles
UNION ALL
SELECT 'CandleData', COUNT(*) FROM CandleData
UNION ALL
SELECT 'CandleHistory', COUNT(*) FROM CandleHistory
UNION ALL
SELECT 'MarketData', COUNT(*) FROM MarketData

-- 5. 최근 1시간 저장 추이
SELECT 
    DATEPART(MINUTE, OpenTime) / 5 * 5 AS MinuteBucket,
    COUNT(*) AS SavedRecords
FROM MarketCandles
WHERE OpenTime >= DATEADD(HOUR, -1, GETDATE())
GROUP BY DATEPART(MINUTE, OpenTime) / 5
ORDER BY MinuteBucket DESC
```

---

## ✅ 검증 완료 기준

**모든 항목이 다음 조건을 만족해야 합니다:**

1. ✅ **Race Condition**: 24시간 동안 중복 진입 0건
2. ✅ **청산가 정확도**: 평균 오차율 1% 이내
3. ✅ **PUMP 타임아웃**: 체결률 70% 이상
4. ✅ **부분 체결**: 포지션 수량 = 체결 수량 (100% 일치)
5. ✅ **데이터 저장**: 4개 테이블 데이터 일관성 유지, 5분마다 정상 증가

---

## 📝 검증 보고서 양식

```markdown
# v2.0.17 검증 보고서

**검증 기간**: 2026-03-02 ~ 2026-03-03 (24시간)
**검증자**: [이름]
**환경**: Production / Testnet

## 검증 결과

### 1. Race Condition 방지
- [ ] 통과 / [ ] 실패
- 중복 진입 건수: __건
- 비고: 

### 2. 청산가 정확도
- [ ] 통과 / [ ] 실패
- 평균 오차율: __%
- 비고:

### 3. PUMP 타임아웃
- [ ] 통과 / [ ] 실패
- 체결률: __%
- 비고:

### 4. 부분 체결
- [ ] 통과 / [ ] 실패
- 테스트 건수: __건
- 비고:

### 5. 데이터 저장
- [ ] 통과 / [ ] 실패
- 저장 건수: __건
- 비고:

## 종합 평가
- [ ] 전체 통과 (운영 배포 승인)
- [ ] 일부 실패 (수정 필요)

## 권고사항
(추가 개선사항 또는 주의사항 기재)
```
