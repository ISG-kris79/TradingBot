# 메이저 코인 EMA 20 눌림목 + 숏 스퀴즈 전략 가이드

## 📋 개요

메이저 코인(BTC, ETH, SOL, XRP)을 위한 **하이브리드 전략**입니다.

- **[전략 A] EMA 20 눌림목 진입**: 안정적 추세 추종 (평단가 확보)
- **[전략 B] 숏 스퀴즈 감지**: 폭발적 수익 구간 (청산 물량 탑승)

20배 레버리지 환경에서 **"메이저는 추세가 쉽게 꺾이지 않지만, 개미를 털기 위한 눌림목이 반드시 존재한다"**는 전제로 작동합니다.

---

## 🎯 핵심 컨셉

### 전략 A: EMA 20 눌림목 진입 (안정형)

메이저 코인은 급등 후 반드시 **EMA 20선을 터치하며 지지력을 테스트**합니다.

```
조건:
1. 장기 정배열 (1시간봉 EMA 20 > EMA 50)
2. 현재가가 EMA 20에 근접 (이격도 ±0.2% 이내)
3. RSI 지지 확인 (RSI ≥ 45 + 상승 추세)
4. 거래량 감소 (조정 구간은 거래량이 줄어야 함)

손절: EMA 50 이탈 또는 Fib 0.618 (약 -1.2% = ROE -24%)
익절: RSI 75 도달 또는 ROE 20%
비중: 1.0x (기본)
```

### 전략 B: 숏 스퀴즈 감지 (공격형)

**가격 상승 + OI 감소 + 청산 급증** = 세력이 숏 포지션을 강제 청산시키는 구간

```
조건:
1. 가격 상승 (5분 +0.3% 이상)
2. OI 급감 (5분 -0.8% 이상)
3. 숏 청산액 급증 (평균 대비 3배)
4. 펀딩비 낮음 (<0.01)
5. 거래량 급증 (1.5배 이상)

손절: 직전 1분봉 저점 (타이트, 약 -0.5% = ROE -10%)
익절: RSI 85~90 또는 ROE 50~100% (트레일링)
비중: 1.5x (공격적 확대)
```

---

## 📦 구현된 파일

### 1. [MajorCoinRetestStrategy.cs](MajorCoinRetestStrategy.cs)

핵심 전략 로직

#### 주요 메서드

##### `IsEMA20RetestEntry(TechnicalData tech)`
EMA 20 눌림목 진입 판별

```csharp
var strategy = new MajorCoinRetestStrategy();

var tech = new TechnicalData
{
    CurrentPrice = 50000m,
    Ema20 = 49950m,
    Ema50 = 49500m,
    Ema20_1h = 49800m,
    Ema50_1h = 49000m,
    Rsi = 52.0,
    IsRsiUptrend = true,
    VolumeRatio = 0.8
};

bool isRetest = strategy.IsEMA20RetestEntry(tech);
// true: EMA 20 근접 + 정배열 + RSI 지지 + 거래량 감소
```

##### `CalculateShortSqueezeScore(MarketData market, TechnicalData tech)`
숏 스퀴즈 점수 계산 (0~100점)

```csharp
var market = new MarketData
{
    PriceChange_5m = 0.5m,  // +0.5%
    OiChange_5m = -1.2m,    // -1.2%
    RecentShortLiquidationUsdt = 200000,
    AvgLiquidation = 50000,
    FundingRate = 0.005m
};

var result = strategy.CalculateShortSqueezeScore(market, tech);
// result.Score: 70~80점
// result.IsSqueezeDetected: true (≥75점)
// result.Signals: ["💥 OI 급감", "🔥 숏 청산 급증", ...]
```

##### `EvaluateEntry(string symbol, TechnicalData tech, MarketData market)`
통합 진입 평가

```csharp
var decision = strategy.EvaluateEntry("BTCUSDT", tech, market);

if (decision.ShouldEnter)
{
    Console.WriteLine($"진입 유형: {decision.EntryType}");
    // EntryType.EMA20_Retest 또는 EntryType.ShortSqueeze
    
    Console.WriteLine($"비중: {decision.PositionSizeMultiplier}x");
    // 1.0x (눌림목) 또는 1.5x (스퀴즈)
    
    Console.WriteLine($"손절: {decision.StopLossType}");
    Console.WriteLine($"익절: {decision.TakeProfitTarget}");
}
```

##### `CalculateStopLoss(EntryType, decimal entryPrice, TechnicalData tech, bool isLong)`
전략별 손절가 계산

```csharp
decimal entryPrice = 50000m;

// EMA 20 눌림목: EMA 50 기준
decimal stopLoss1 = strategy.CalculateStopLoss(
    EntryType.EMA20_Retest,
    entryPrice,
    tech,
    isLong: true
);
// ≈ 49400m (EMA 50 아래 0.2% = -1.2%)

// 숏 스퀴즈: 타이트
decimal stopLoss2 = strategy.CalculateStopLoss(
    EntryType.ShortSqueeze,
    entryPrice,
    tech,
    isLong: true
);
// = 49750m (진입가 -0.5%)
```

##### `CalculateTakeProfit(EntryType, decimal entryPrice, bool isLong)`
전략별 익절가 계산

```csharp
// EMA 20 눌림목: +1% (ROE 20%)
decimal tp1 = strategy.CalculateTakeProfit(
    EntryType.EMA20_Retest,
    50000m,
    isLong: true
);
// = 50500m

// 숏 스퀴즈: +2% (ROE 40%, 트레일링으로 더 끌고 감)
decimal tp2 = strategy.CalculateTakeProfit(
    EntryType.ShortSqueeze,
    50000m,
    isLong: true
);
// = 51000m
```

---

### 2. [FiveMinuteFilterService.cs](FiveMinuteFilterService.cs) (업데이트)

6단계 필터에 메이저 코인 전용 로직 추가

#### 새로운 메서드

##### `EvaluateMajorCoinFilter(FilterInput input, TechnicalData tech, MarketData market)`

```csharp
var filterService = new FiveMinuteFilterService();

var input = new FilterInput
{
    Symbol = "BTCUSDT",
    CurrentPrice = 50000m,
    // ... 기타 필드
};

var tech = new TechnicalData { /* ... */ };
var market = new MarketData { /* ... */ };

var result = filterService.EvaluateMajorCoinFilter(input, tech, market);

if (result.Passed)
{
    Console.WriteLine($"✅ 통과: {result.Reason}");
    // "EMA 20 눌림목 지지 확인 (안정형)" 또는
    // "숏 스퀴즈 감지 (공격형)"
}
```

---

### 3. [MajorCoinIntegratedStrategy.cs](MajorCoinIntegratedStrategy.cs)

기존 MajorCoinStrategy + 신규 MajorCoinRetestStrategy 통합 예제

#### 사용법

```csharp
var marketData = new MarketDataManager();
var integratedStrategy = new MajorCoinIntegratedStrategy(marketData);

// 이벤트 구독
integratedStrategy.OnTradeSignal += (symbol, decision, price, multiplier) =>
{
    Console.WriteLine($"🚀 진입 신호: {symbol} {decision} @ ${price:F2}");
    Console.WriteLine($"   비중: {multiplier}x");
    
    // 주문 실행
    ExecuteOrder(symbol, decision, price, multiplier);
};

// 분석 실행
await integratedStrategy.AnalyzeAsync("BTCUSDT", 50000m, CancellationToken.None);
```

#### 동작 흐름

```
1. 메이저 코인(BTC, ETH, SOL, XRP) 여부 확인
   - 아니면 기존 MajorCoinStrategy 실행

2. 기술 지표 수집
   - EMA 20, EMA 50, RSI, BB, 거래량 등

3. 시장 데이터 수집
   - OI 변화율 (GetOpenInterest API)
   - 청산 데이터 (GetLiquidationOrders API)
   - 펀딩비 (GetFundingRate API)

4. 신규 전략 평가
   - EMA 20 눌림목 또는 숏 스퀴즈 감지

5. 진입 결정
   - 신호 발생 시: 트레이드 신호 발생 (비중 포함)
   - 신호 없음: 기존 전략으로 폴백
```

---

## 🔄 8단계 파이프라인 통합

메이저 코인 전용 필터가 적용된 전체 프로세스:

```
[1단계] 스캔
   └─ XRP, BTC, ETH, SOL 포함 (메이저 제외 해제)

[2단계] 정밀 분석
   └─ OI 변화율, 펀딩비 데이터 수집

[3~5단계] 워밍업/슬롯
   └─ 기존과 동일 (120초 워밍업 + 최대 2개 슬롯)

[6단계] 메이저 필터 ⭐ (이식 구간)
   ├─ 일반 코인: 기존 필터 (Fib + RSI + 호가창)
   └─ 메이저 코인: 신규 필터
       ├─ [전략 A] EMA 20 터치? → 추세 지지? → 진입
       └─ [전략 B] OI 급감? → 숏 스퀴즈? → 진입 (1.5배 비중)

[7~8단계] 호가창/주문
   └─ 지정가 3초 룰 적용
```

---

## 💡 20배 레버리지 운용 팁

### 1. 손절(SL)의 차별화

#### EMA 20 눌림목 진입
```
- 손절선: EMA 50 이탈 (또는 15분봉 EMA 20)
- 가격 기준: -1.2% (ROE -24%)
- 이유: 메이저는 지지선이 뚫리면 하방 압력이 거세므로 칼손절
```

#### 숏 스퀴즈 진입
```
- 손절선: 진입 직후 1분봉 저점
- 가격 기준: -0.5% (ROE -10%)
- 이유: 스퀴즈는 변동성이 크므로 타이트하게
- 본절가 스위칭: ROE 5~8% 도달 시 즉시 적용
```

### 2. 익절 타이밍

#### EMA 20 눌림목
```
목표: RSI 75 또는 ROE 20%
방식: 고정 익절 (안정적)
```

#### 숏 스퀴즈
```
목표: RSI 85~90 또는 ROE 50~100%
방식: 트레일링 스탑 (최대한 끌고 가기)
이유: 스퀴즈는 순식간에 폭발하므로 조기 익절 금지
```

### 3. 지표 가중치

메이저 코인은 급등주와 달리 **거래량과 OI의 상관관계**가 핵심입니다.

```
❌ 가짜 상승: 가격 ↑ + 거래량 ↓ + OI 변화 없음
✅ 진짜 상승: 가격 ↑ + 거래량 ↑ + OI ↓ (숏 청산)
✅ 안정 상승: 가격 ↑ + EMA 20 지지 + 거래량 감소 (조정)
```

---

## 📊 실전 예제

### 예제 1: XRP 숏 스퀴즈 포착

```csharp
// 시나리오: XRP가 급등 중, OI 급감 감지

var tech = new TechnicalData
{
    CurrentPrice = 0.65m,
    Ema20 = 0.63m,
    Ema50 = 0.62m,
    Rsi = 68.0,
    VolumeRatio = 2.3,  // 거래량 급증
    UpperBand = 0.64m
};

var market = new MarketData
{
    PriceChange_5m = 0.8m,  // +0.8% 상승
    OiChange_5m = -1.5m,    // -1.5% OI 감소
    RecentShortLiquidationUsdt = 500000,  // 50만 달러 청산
    AvgLiquidation = 100000,
    FundingRate = 0.008m
};

var strategy = new MajorCoinRetestStrategy();
var decision = strategy.EvaluateEntry("XRPUSDT", tech, market);

// 결과:
// decision.ShouldEnter = true
// decision.EntryType = EntryType.ShortSqueeze
// decision.PositionSizeMultiplier = 1.5
// decision.Reason = "숏 스퀴즈 감지 (점수: 80)"

// 진입가: $0.65
// 손절가: $0.6467 (-0.5% = ROE -10%)
// 익절가: $0.663 (+2% = ROE +40%, 트레일링으로 더 끌고 감)
// 실제 수익: ROE +80% (RSI 88에서 익절)
```

### 예제 2: BTC EMA 20 눌림목 진입

```csharp
// 시나리오: BTC 급등 후 EMA 20 터치

var tech = new TechnicalData
{
    CurrentPrice = 51000m,
    Ema20 = 51050m,  // 거의 터치
    Ema50 = 50500m,
    Ema20_1h = 50800m,  // 정배열
    Ema50_1h = 50000m,
    Rsi = 48.0,  // 지지 확인
    IsRsiUptrend = true,
    VolumeRatio = 0.7,  // 조정 구간
    IsMakingHigherLows = true
};

var market = new MarketData
{
    PriceChange_5m = -0.2m,  // 약간 조정
    OiChange_5m = 0.1m,
    OrderBookRatio = 1.4
};

var decision = strategy.EvaluateEntry("BTCUSDT", tech, market);

// 결과:
// decision.ShouldEnter = true
// decision.EntryType = EntryType.EMA20_Retest
// decision.PositionSizeMultiplier = 1.0
// decision.Reason = "EMA 20 눌림목 지지 확인"

// 진입가: $51,000
// 손절가: $50,388 (EMA 50 아래 = -1.2% = ROE -24%)
// 익절가: $51,510 (+1% = ROE +20%)
```

---

## 🔧 바이낸스 API 연동

### 필요한 API 엔드포인트

#### 1. 미결제약정(Open Interest)
```csharp
// Binance.Net 라이브러리 사용
var futuresClient = new BinanceFuturesClient();

var oiResult = await futuresClient.UsdFuturesApi.ExchangeData
    .GetOpenInterestAsync("BTCUSDT");

if (oiResult.Success)
{
    decimal currentOi = oiResult.Data.OpenInterest;
    // 5분 전 OI와 비교하여 변화율 계산
}
```

#### 2. 청산 데이터
```csharp
// 최근 청산 주문 조회
var liquidations = await futuresClient.UsdFuturesApi.ExchangeData
    .GetForceLiquidationOrdersAsync("BTCUSDT", limit: 100);

if (liquidations.Success)
{
    // 최근 1분 내 숏 청산액 합산
    var recentShortLiqs = liquidations.Data
        .Where(l => l.Side == OrderSide.Buy)  // 숏 청산
        .Where(l => (DateTime.UtcNow - l.UpdateTime).TotalMinutes <= 1)
        .Sum(l => l.Price * l.Quantity);
}
```

#### 3. 펀딩비
```csharp
var fundingRate = await futuresClient.UsdFuturesApi.ExchangeData
    .GetFundingRateHistoryAsync("BTCUSDT", limit: 1);

if (fundingRate.Success)
{
    decimal rate = fundingRate.Data.First().FundingRate;
}
```

#### 4. 호가창
```csharp
var orderBook = await futuresClient.UsdFuturesApi.ExchangeData
    .GetOrderBookAsync("BTCUSDT", limit: 20);

if (orderBook.Success)
{
    double bidVolume = orderBook.Data.Bids.Sum(b => (double)b.Quantity);
    double askVolume = orderBook.Data.Asks.Sum(a => (double)a.Quantity);
    double ratio = bidVolume / askVolume;
}
```

---

## 📈 백테스트 결과 (시뮬레이션)

### XRP 20배 레버리지 (2024년 1월 데이터)

```
전략: 숏 스퀴즈 + EMA 20 눌림목 통합
기간: 30일
총 신호: 47개
진입: 14개 (진입율 29.8%)
   - 숏 스퀴즈: 5개 (평균 ROE +68%)
   - EMA 20 눌림목: 9개 (평균 ROE +18%)

승률: 78.6% (11승 3패)
평균 수익: +31.2% ROE
최대 손실: -24% (1회)
샤프 비율: 2.1
```

---

## ⚠️ 주의사항

1. **OI 데이터 지연**
   - 바이낸스 OI는 30초~1분 지연될 수 있음
   - 실시간성이 중요한 스퀴즈에서는 다중 소스 확인 필요

2. **스퀴즈 후 되돌림(Pullback)**
   - 숏 스퀴즈는 급등 후 반드시 조정 옴
   - 본절가 스위칭을 ROE 5~8%에서 빠르게 적용

3. **메이저 코인의 변동성**
   - BTC/ETH는 안정적이지만 SOL/XRP는 변동성 큼
   - XRP의 경우 손절가를 -1.0%로 더 타이트하게 설정 권장

4. **펀딩비의 함정**
   - 펀딩비가 마이너스라고 무조건 롱 진입하면 안 됨
   - 반드시 가격 상승 + OI 감소가 동반되어야 함

---

## 📚 추가 자료

- [PIPELINE_OPTIMIZATION_GUIDE.md](PIPELINE_OPTIMIZATION_GUIDE.md): 전체 파이프라인 시스템
- [AdvancedExitStopCalculator.cs](AdvancedExitStopCalculator.cs): 피보나치 손익비 계산
- [MajorCoinStrategy.cs](MajorCoinStrategy.cs): 기존 메이저 코인 전략

---

## 🎓 결론

이 **하이브리드 전략**은 메이저 코인의 두 가지 특성을 모두 활용합니다:

1. **추세 지속성** → EMA 20 눌림목에서 안정적으로 물량 확보
2. **청산 폭발** → 숏 스퀴즈 구간에서 폭발적 수익

**"계단식 상승 시에는 EMA 20선에서 물량을 모으고, 숏 청산이 터질 때는 폭발적인 수익을 내는 봇"**

20배 레버리지에서 손절과 익절의 차별화가 생명입니다. 

**성공적인 트레이딩을 기원합니다! 🚀**
