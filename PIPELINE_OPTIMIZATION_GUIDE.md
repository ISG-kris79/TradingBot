# 파이프라인 단계별 최적화 가이드

## 📋 개요

이 문서는 TradingBot의 신호 파이프라인 시스템에 대한 상세 가이드입니다.
**최종 진입율 0.5%**를 목표로, 수천 번의 기회 중 가장 확실한 5~10번만 선택합니다.

## 🔄 전체 파이프라인 구조

```
[1단계] 신호 생성 (MajorCoinStrategy)
   ↓
[2단계] AI 필터
   ↓
[3단계] 워밍업 (120초 + 휩소 감지) ← SignalWarmupService
   ↓
[4단계] 슬롯 제한 (최대 2개) ← SignalWarmupService
   ↓
[5단계] 리스크 계산
   ↓
[6단계] 5분봉 필터 (필수 3 + 선택 2/3) ← FiveMinuteFilterService
   ↓
[7단계] 최종 승인
   ↓
[8단계] 주문 실행 (지정가 3초 + 부분 체결) ← OptimizedOrderExecutionService
```

## 📦 구현된 서비스

### 1. SignalWarmupService.cs
**[3단계] 워밍업 & [4단계] 슬롯 제한**

#### 핵심 기능
- **120초 워밍업**: 가격 변동 표준편차 계산
- **휩소 감지**: 변동률 > 2% 시 신호 드랍
- **슬롯 관리**: 최대 2개 동시 진입 제한

#### 사용법
```csharp
var warmupService = new SignalWarmupService();

// 신호 등록
bool registered = warmupService.RegisterSignal("BTCUSDT", 50000m);

// 가격 업데이트 (1초마다)
warmupService.UpdatePrice("BTCUSDT", 50050m);

// 워밍업 완료 확인 (120초 후)
var (isReady, isWhipsaw, reason) = warmupService.CheckWarmupStatus("BTCUSDT");

if (isReady && !isWhipsaw)
{
    // 슬롯 점유
    bool occupied = warmupService.OccupySlot("BTCUSDT");
}

// 청산 시 슬롯 해제
warmupService.ReleaseSlot("BTCUSDT");
```

#### 핵심 로직: 휩소 감지
```
1. 120초 동안 모든 가격 변화 기록
2. 연속 가격 간 변동률 계산: (현재가 - 이전가) / 이전가
3. 표준편차 계산: √(Σ(변동률 - 평균)² / N)
4. 표준편차 > 2% → 휩소로 판단 → 드랍
```

**효과**: 슬리피지 발생을 사전 차단

---

### 2. FiveMinuteFilterService.cs
**[6단계] 5분봉 필터 - 유연한 가중치 설계**

#### 필수 조건 (3개 모두 통과)
1. **Fib 범위**: 0.382 ~ 0.618
2. **RSI < 80**: 과매수 방지
3. **호가창 비율 < 1.5**: 매도호가/매수호가

#### 선택 조건 (3개 중 2개 통과)
1. **BB 중단선 근처**: ±5% 이내
2. **MACD >= 0**: 상승 모멘텀
3. **RSI 상승 추세**: 최근 5봉 중 3번 이상 상승

#### 사용법
```csharp
var filterService = new FiveMinuteFilterService();

var input = new FilterInput
{
    Symbol = "BTCUSDT",
    CurrentPrice = 50000m,
    FibLevel382 = 49500m,
    FibLevel618 = 49000m,
    Rsi = 65.0,
    BidVolume = 1000,
    AskVolume = 800,
    BbMidBand = 49800m,
    MacdHistogram = 0.05,
    RsiHistory = new List<double> { 60, 61, 62, 64, 65 }
};

var result = filterService.EvaluateFilter(input);

if (result.Passed)
{
    Console.WriteLine($"✅ 통과! 선택 조건 {result.OptionalPassCount}/3");
}
else
{
    Console.WriteLine($"❌ 실패: {result.Reason}");
}
```

**효과**: 계단식 상승장에서도 진입 기회 확보 (구경만 하는 상황 해결)

---

### 3. OptimizedOrderExecutionService.cs
**[8단계] 주문 실행 최적화**

#### 핵심 로직
1. **지정가 주문 실행**
2. **3초 대기**
3. **주문 상태 확인**:
   - 전량 체결 → 완료
   - 부분 체결 → 잔량 취소 + 손절/익절 감시 활성화
   - 미체결 → 취소 + 시장가 재진입

#### 사용법
```csharp
var executionService = new OptimizedOrderExecutionService();

// 부분 체결 이벤트 구독
executionService.OnPartialFillDetected += (symbol, filledQty, isLong) =>
{
    Console.WriteLine($"⚠️ 부분 체결: {symbol} {filledQty}");
    // 손절/익절 감시 활성화
};

var result = await executionService.ExecuteOptimizedOrderAsync(
    symbol: "BTCUSDT",
    side: OrderSide.Buy,
    quantity: 0.01m,
    limitPrice: 50000m,
    placeLimitOrderFunc: PlaceLimitOrder,
    checkOrderStatusFunc: CheckOrderStatus,
    cancelOrderFunc: CancelOrder,
    placeMarketOrderFunc: PlaceMarketOrder,
    cancellationToken: CancellationToken.None
);

if (result.Success)
{
    switch (result.ExecutionType)
    {
        case ExecutionType.FullyFilled:
            Console.WriteLine("✅ 전량 체결");
            break;
        case ExecutionType.PartiallyFilled:
            Console.WriteLine($"⚠️ 부분 체결: {result.FilledQuantity}");
            break;
        case ExecutionType.MarketFallback:
            Console.WriteLine("🚀 시장가 대체");
            break;
    }
}
```

**효과**: 20배 레버리지에서 부분 체결 상태로 방치되지 않음

---

### 4. SignalPipelineService.cs
**전체 파이프라인 통합 서비스**

#### 사용법
```csharp
var warmupService = new SignalWarmupService();
var filterService = new FiveMinuteFilterService();
var executionService = new OptimizedOrderExecutionService();

var pipelineService = new SignalPipelineService(
    warmupService,
    filterService,
    executionService
);

// 이벤트 구독
pipelineService.OnPipelineStageComplete += (symbol, stage, result) =>
{
    Console.WriteLine($"[{stage}] {symbol}: {result}");
};

// 신호 처리
var signal = new SignalData
{
    Symbol = "BTCUSDT",
    CurrentPrice = 50000m,
    IsLong = true,
    Quantity = 0.01m,
    LimitPrice = 50000m,
    FilterInput = new FilterInput { /* ... */ }
};

var result = await pipelineService.ProcessSignalAsync(
    signal,
    aiFilterFunc: CheckAiFilter,
    placeLimitOrderFunc: PlaceLimitOrder,
    checkOrderStatusFunc: CheckOrderStatus,
    cancelOrderFunc: CancelOrder,
    placeMarketOrderFunc: PlaceMarketOrder,
    startMonitoringFunc: StartStopLossMonitoring,
    cancellationToken: CancellationToken.None
);

if (result.Success)
{
    Console.WriteLine("🎉 파이프라인 완료!");
}
else
{
    Console.WriteLine($"❌ 실패: [{result.FailedStage}] {result.FailReason}");
}

// 청산 시 슬롯 해제
pipelineService.OnPositionClosed("BTCUSDT");
```

---

### 5. FibonacciRiskRewardCalculator.cs
**피보나치 기반 손익비 계산기 (20배 레버리지 최적화)**

#### 손익비 설정
```
진입(Entry): Fib 0.382 ~ 0.618
손절(Stop Loss): Fib 0.786 (약 -1.3% = ROE -26%)
익절(Take Profit): Fib 1.618 (약 +1% = ROE +20%)

손익비 = 20% / 26% ≈ 0.77
```

#### 사용법
```csharp
var fibCalculator = new FibonacciRiskRewardCalculator();

// 레벨 계산
var levels = fibCalculator.CalculateLevels(
    highPrice: 51000m,
    lowPrice: 48000m,
    isLong: true
);

Console.WriteLine($"진입: ${levels.EntryMin:F2} ~ ${levels.EntryMax:F2}");
Console.WriteLine($"권장: ${levels.RecommendedEntry:F2}");
Console.WriteLine($"손절: ${levels.StopLoss:F2}");
Console.WriteLine($"익절: ${levels.TakeProfit:F2}");

// 진입 검증
bool valid = fibCalculator.ValidateEntry(50000m, levels, isLong: true);

// 손익비 계산
var ratio = fibCalculator.CalculateRiskReward(
    entryPrice: 50000m,
    stopLoss: levels.StopLoss,
    takeProfit: levels.TakeProfit,
    isLong: true
);

Console.WriteLine($"손익비: {ratio.Ratio:F2}:1");
Console.WriteLine($"리스크: {ratio.RiskPercent:F1}%");
Console.WriteLine($"보상: {ratio.RewardPercent:F1}%");

// ROE 계산 (20배 레버리지)
double roe = fibCalculator.CalculateROE(
    entryPrice: 50000m,
    currentPrice: 50500m,
    isLong: true
);
Console.WriteLine($"현재 ROE: {roe:F2}%");

// 목표 수익률 도달 확인
bool reached = fibCalculator.HasReachedTargetROE(50000m, 50500m, true);
```

**20배 레버리지 효과**:
- 가격 변동 1% = ROE 20%
- 손절 1.3% = 최대 손실 26%
- 익절 1% = ROE 20%

---

## 🎯 최종 진입율 0.5%의 의미

```
일일 신호 수: 1,000개 (가정)
   ↓
AI 필터 통과: 100개 (10%)
   ↓
워밍업 통과: 50개 (5%)
   ↓
5분봉 필터 통과: 10개 (1%)
   ↓
최종 진입: 5개 (0.5%)
```

**하루에 수천 번의 기회 중 가장 확실한 5~10번만 골라냅니다.**

이는 복리의 마법을 누리기에 충분한 횟수이며, 불필요한 손실을 줄여 자산을 우상향시키는 핵심 동력입니다.

---

## 📊 시스템 통합 예제

```csharp
public class TradingBotIntegration
{
    private readonly SignalPipelineService _pipeline;
    private readonly FibonacciRiskRewardCalculator _fibCalculator;

    public async Task OnSignalGenerated(string symbol, decimal price, bool isLong)
    {
        // [1] 피보나치 레벨 계산
        var fibLevels = _fibCalculator.CalculateLevels(
            highPrice: GetHighPrice(symbol),
            lowPrice: GetLowPrice(symbol),
            isLong: isLong
        );

        // [2] 진입 검증
        if (!_fibCalculator.ValidateEntry(price, fibLevels, isLong))
        {
            Log($"❌ [{symbol}] 진입 범위 밖. 신호 무시.");
            return;
        }

        // [3] 신호 데이터 생성
        var signal = new SignalData
        {
            Symbol = symbol,
            CurrentPrice = price,
            IsLong = isLong,
            Quantity = CalculatePositionSize(),
            LimitPrice = fibLevels.RecommendedEntry,
            FilterInput = CreateFilterInput(symbol)
        };

        // [4] 파이프라인 실행
        var result = await _pipeline.ProcessSignalAsync(
            signal,
            aiFilterFunc: CheckAiFilter,
            placeLimitOrderFunc: PlaceLimitOrder,
            checkOrderStatusFunc: CheckOrderStatus,
            cancelOrderFunc: CancelOrder,
            placeMarketOrderFunc: PlaceMarketOrder,
            startMonitoringFunc: (sym, qty, entry, isL) =>
            {
                // 손절/익절 감시 시작
                StartMonitoring(sym, qty, entry, fibLevels.StopLoss, fibLevels.TakeProfit, isL);
            },
            cancellationToken: CancellationToken.None
        );

        // [5] 결과 처리
        if (result.Success)
        {
            Log($"🎉 [{symbol}] 진입 성공!");
        }
        else
        {
            Log($"❌ [{symbol}] 실패: [{result.FailedStage}] {result.FailReason}");
        }
    }

    public void OnPositionClosed(string symbol)
    {
        // 슬롯 해제
        _pipeline.OnPositionClosed(symbol);
    }
}
```

---

## 🔧 설정 커스터마이징

### SignalWarmupService
```csharp
var warmupService = new SignalWarmupService
{
    WhipsawVolatilityThreshold = 0.03  // 3%로 변경 (더 엄격)
};
```

### FiveMinuteFilterService
```csharp
var filterService = new FiveMinuteFilterService
{
    RsiMaxLevel = 75.0,           // RSI 상한선
    OrderBookRatioMax = 1.3,      // 호가창 비율
    BbMidBandTolerancePct = 3.0,  // BB 허용 범위 축소
    RsiTrendPeriod = 7            // RSI 추세 기간 확장
};
```

### FibonacciRiskRewardCalculator
```csharp
// 상수는 변경 불가, 로직 자체가 피보나치 비율 기반
// 필요 시 클래스 상속하여 커스터마이징
```

---

## 📈 성능 모니터링

### 슬롯 상태 확인
```csharp
var (active, available) = warmupService.GetSlotStatus();
Console.WriteLine($"활성: {active}/{SignalWarmupService.MAX_CONCURRENT_SLOTS}");
Console.WriteLine($"사용 가능: {available}");
```

### 파이프라인 단계별 통계
```csharp
// 이벤트 구독으로 각 단계별 성공률 추적
pipelineService.OnPipelineStageComplete += (symbol, stage, result) =>
{
    // 데이터베이스 또는 로그에 기록
    RecordStageResult(stage, result);
};
```

---

## ⚠️ 주의사항

1. **워밍업 기간 동안 가격 업데이트 필수**
   - `UpdatePrice()` 메서드를 1초마다 호출해야 휩소 감지가 정확합니다.

2. **부분 체결 시 즉시 손절/익절 감시 활성화**
   - `OnPartialFillDetected` 이벤트를 반드시 구독하세요.

3. **청산 시 슬롯 해제 필수**
   - `OnPositionClosed()` 호출하지 않으면 슬롯이 영구 점유됩니다.

4. **20배 레버리지 리스크 인지**
   - 손절 1.3% = 최대 손실 26%
   - 반드시 손절가 준수 필요

---

## 📚 추가 자료

- [AdvancedExitStopCalculator.cs](AdvancedExitStopCalculator.cs): 동적 익절 시스템
- [MajorCoinStrategy.cs](MajorCoinStrategy.cs): 신호 생성 로직

---

## 🎓 결론

이 파이프라인 시스템은 **최종 진입율 0.5%**를 목표로 설계되었습니다.

**"하루에 수천 번의 기회 중 가장 확실한 5~10번만 골라낸다"**

이는 복리의 마법을 누리기에 충분한 횟수이며, 불필요한 손실을 줄여 자산을 우상향시키는 핵심 동력이 될 것입니다.

**행운을 빕니다! 🚀**
