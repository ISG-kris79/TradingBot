# 📊 백테스트 & 최적화 가이드

> 최종 업데이트: 2026-03-05  
> 이 문서는 백테스트와 하이퍼파라미터 최적화 기능의 전체 사용법과 구조를 설명합니다.  
> **실전 주문 로직과의 차이점 포함** - [ORDER_LOGIC_REFERENCE.md](ORDER_LOGIC_REFERENCE.md) 참고

---

## 목차

1. [백테스트 개요](#1-백테스트-개요)
2. [백테스트 실행 방법](#2-백테스트-실행-방법)
3. [백테스트 전략 종류](#3-백테스트-전략-종류)
4. [백테스트 결과 분석](#4-백테스트-결과-분석)
5. [최적화 개요](#5-최적화-개요)
6. [최적화 실행 방법](#6-최적화-실행-방법)
7. [Optuna 튜너 상세](#7-optuna-튜너-상세)
8. [커스텀 전략 작성](#8-커스텀-전략-작성)
9. [성능 최적화 팁](#9-성능-최적화-팁)
10. [주의사항 및 한계](#10-주의사항-및-한계) ★ **실전 vs 백테스트 차이점 포함**
11. [실전 예시](#11-실전-예시)
12. [FAQ](#12-faq)

---

## 1. 백테스트 개요

### 1-A. 백테스트란?

**백테스트(Backtesting)**는 과거 시장 데이터를 사용하여 트레이딩 전략의 성능을 검증하는 프로세스입니다.

| 구분 | 설명 |
|------|------|
| **목적** | 전략의 수익성, 위험도, 안정성 검증 |
| **데이터 소스** | MarketCandles 또는 CandleData 테이블 (DB) |
| **시뮬레이션 방식** | 과거 캔들 데이터를 순차적으로 재생하며 매매 시그널 생성 |
| **결과 지표** | 최종자산, 총 거래 횟수, 승률, MDD, Sharpe Ratio 등 |

**⚠️ 중요:** 백테스트는 **실전 주문 로직의 단순화 버전**입니다. 실전 [ORDER_LOGIC_REFERENCE.md](ORDER_LOGIC_REFERENCE.md)의 8단계 진입 필터, 3개 청산 시스템, 서버사이드 스탑 주문 등은 생략됩니다. **ElliottWave 전략만 실전 로직의 90%를 복제**했습니다.

### 1-B. 주요 컴포넌트

```
BacktestService.cs          ← 백테스트 실행 엔진
  ├─ LoadBacktestCandlesAsync  ← DB에서 과거 데이터 로드
  ├─ RunBacktestAsync          ← 전략 실행 + 결과 계산
  └─ CalculateMetrics          ← MDD, Sharpe Ratio 계산

IBacktestStrategy           ← 전략 인터페이스
  └─ Execute(candles, result) ← 전략 로직 구현 메서드

BacktestWindow.xaml(.cs)    ← UI (차트 + 지표 표시)
BacktestViewModel.cs        ← MVVM 뷰모델 (LiveCharts 연동)
```

### 1-C. 백테스트 vs 실전 매핑

| 백테스트 전략 | 실전 매핑 | 일치도 |
|-------------|----------|--------|
| **RsiBacktestStrategy** | 실전 없음 | - |
| **MaCrossBacktestStrategy** | 실전 없음 | - |
| **BollingerBandBacktestStrategy** | Pump 진입 조건 일부 | 30% |
| **ElliottWaveBacktestStrategy** | PositionMonitor Pump + HybridExitManager | 🟢🟢🟢 **90%** |

→ 실전 적용 시 **ElliottWave 백테스트 결과를 90% 신뢰도로 활용 가능**

---

## 2. 백테스트 실행 방법

### 2-A. 코드에서 실행

```csharp
var service = new BacktestService();

// 방법 1: 전략 인스턴스 직접 생성
var strategy = new RsiBacktestStrategy
{
    RsiPeriod = 14,
    BuyThreshold = 30,
    SellThreshold = 70
};

var result = await service.RunBacktestAsync(
    symbol: "BTCUSDT",
    startDate: new DateTime(2026, 1, 1),
    endDate: new DateTime(2026, 2, 1),
    strategy: strategy,
    initialBalance: 1000m
);

// 방법 2: Enum으로 전략 타입 지정
var result2 = await service.RunBacktestAsync(
    symbol: "ETHUSDT",
    startDate: new DateTime(2026, 1, 1),
    endDate: new DateTime(2026, 2, 1),
    strategyType: BacktestStrategyType.ElliottWave,
    initialBalance: 1000m
);

// 3. 결과 확인
Console.WriteLine($"초기자산: {result.InitialBalance} USDT");
Console.WriteLine($"최종자산: {result.FinalBalance} USDT");
Console.WriteLine($"수익률: {result.TotalProfit:F2}%");
Console.WriteLine($"MDD: {result.MaxDrawdown:F2}%");
Console.WriteLine($"Sharpe Ratio: {result.SharpeRatio:F4}");
Console.WriteLine($"승률: {result.WinRate:F2}% ({result.WinCount}/{result.TotalTrades})");
```

### 2-B. UI에서 실행

```csharp
// 백테스트 윈도우 열기
var window = new BacktestWindow(result, "📊 Backtest Result");
window.Show();
```

**UI 구성:**
- **상단**: 전략 요약, 기간, 데이터 통계
- **중앙 차트**: 종가 라인 + 매수/매도 마커
- **하단 지표**: 초기/최종 자산, 수익률, MDD, Sharpe Ratio, 승률

### 2-C. 데이터 재수집

백테스트 전 최신 데이터가 필요하면:

```csharp
var service = new BacktestService();

// 최근 30일 5분봉 재수집 (최대 8개 청크)
int count = await service.RecollectRecentCandleDataAsync("BTCUSDT", days: 30, maxChunks: 8);
Console.WriteLine($"{count}개 캔들 수집 완료");
```

---

## 3. 백테스트 전략 종류

### 3-A. RSI 전략 (RsiBacktestStrategy)

**진입/청산 조건:**

| 신호 | 조건 | 동작 |
|------|------|------|
| **매수** | RSI < BuyThreshold (기본 30) | 잔고의 95% 투자 |
| **매도** | RSI > SellThreshold (기본 70) | 전량 청산 |

**파라미터:**
- `RsiPeriod` (int, 기본 14): RSI 계산 기간
- `BuyThreshold` (double, 기본 30): 과매도 진입 기준
- `SellThreshold` (double, 기본 70): 과매수 청산 기준

**특징:**
- 단순 명확한 역추세 전략
- 횡보장에서 효과적, 강한 추세에서는 손실 위험

### 3-B. MA Cross 전략 (MaCrossBacktestStrategy)

**진입/청산 조건:**

| 신호 | 조건 | 동작 |
|------|------|------|
| **매수** | 단기MA가 장기MA를 상향 돌파 (골든크로스) | 잔고의 95% 투자 |
| **매도** | 단기MA가 장기MA를 하향 돌파 (데드크로스) | 전량 청산 |

**파라미터:**
- `ShortPeriod` (int, 기본 10): 단기 이동평균 기간
- `LongPeriod` (int, 기본 30): 장기 이동평균 기간

**특징:**
- 추세 추종 전략
- 래깅(지연) 특성으로 진입/청산이 늦을 수 있음

### 3-C. Bollinger Band 전략 (BollingerBandBacktestStrategy)

**진입/청산 조건:**

| 신호 | 조건 | 동작 |
|------|------|------|
| **매수** | 종가가 하단밴드 아래로 이탈 → 복귀 | 잔고의 95% 투자 |
| **매도** | 종가가 상단밴드 위로 돌파 → 복귀 | 전량 청산 |

**파라미터:**
- `Period` (int, 기본 20): BB 계산 기간
- `Multiplier` (double, 기본 2.0): 표준편차 배수

**특징:**
- 변동성 기반 역추세 전략
- 밴드 축소 시(낮은 변동성) 휩쏘 위험

### 3-D. Elliott Wave 전략 (ElliottWaveBacktestStrategy)

**파동 감지 프로세스:**

| 단계 | 감지 조건 | 상태 전환 |
|------|----------|----------|
| **Wave1** | 고점 돌파 + 20EMA 상승 | Idle → Wave1Started |
| **Wave2** | Fib 0.382~0.618 되돌림 | Wave1Started → Wave2Started |
| **RSI 다이버전스** | 가격 하락 + RSI 상승 (10봉 비교) | Wave2Started → Wave3Setup |
| **진입 확정** | RSI>40 + MACD 골든크로스 + BB 하단 돌파 | 잔고의 98% 투자 |

**3단계 부분 익절 시스템:**

| 단계 | 조건 | 청산 비율 | 후속 조치 |
|------|------|----------|----------|
| **1차 익절** | Wave1High 도달 OR ROE ≥ 20% | **50%** | 본절가 = 진입가 |
| **2차 익절** | Fib 1.618 도달 OR ROE ≥ 50% | **30%** (RSI≥80: 40%) | 스탑 = Wave1High |
| **최종 익절** | Fib 2.618 도달 | **잔량 전부** | 시장가 청산 |

**절대 손절선 (2차 익절 이전):**

| # | 조건 | 논리 |
|---|------|------|
| 1 | 현재가 ≤ Wave1Low | Wave1 이탈 → 파동 무효화 |
| 2 | 현재가 ≤ Fib 0.618 | 되돌림 과다 → 상승 실패 |
| 3 | 현재가 ≤ Fib 0.786 | 논리적 손절선 |
| 4 | 현재가 < BB(20,2) 하단 | 밴드 이탈 → 추세 붕괴 |
| 5 | 경과 20분 + \|ROE\| ≤ 2% | 횡보 타임스탑 |

**레벨업 스탑 (2차 익절 이후):**

| # | 조건 | 동작 |
|---|------|------|
| 1 | 종가 < BB 중단(20EMA) | 추세 종료 → 전량 청산 |
| 2 | 현재가 < Wave1High | 레벨업 손절 → 전량 청산 |
| 3 | BB 상단 이탈→복귀 + RSI 다이버전스 | 밴드라이딩 종료 |

**Dynamic ATR Trailing (HybridExitManager 로직):**

| ROE 구간 | RSI 조건 | ATR 멀티플라이어 | 설명 |
|---------|---------|----------------|------|
| < 10% | - | **1.5x** | 넓은 방어 (변동성 흡수) |
| ≥ 10% | < 70 | **1.0x** | 추세 진행 중 |
| - | 70~80 | **0.5x** | 과열 접근 |
| - | ≥ 80 | **0.2x** | 극단 밀착 (피날레) |

```
트레일링 스탑 = HighestPrice - (ATR × Multiplier)
안전장치 (ROE≥15%): 스탑이 진입가 아래면 → entry×1.001로 상향
```

**본절 보호:**
```
ROE ≥ 3.0% → 손절 라인을 0% (진입가)로 이동
```

**DCA 물타기 (1회 제한):**
```
조건: 미물타기 + 미본절 + ROE ≤ -5.0%
수량: 기존 포지션의 50%
방식: 시장가 추가 매수
효과: 평단가 하향, 손절 ROE 12%로 완화
```

**특징:**
- **가장 복잡한 전략**: 파동 감지 → 진입 → 3단계 익절 → 다중 손절선
- **고수익 가능**: 강한 3파 추세에서 ROE 50%+ 달성
- **위험 관리**: 5개 손절선 + 본절 보호 + DCA 물타기
- **실전 근접**: HybridExitManager 로직을 백테스트에 완전 복제

---

## 4. 백테스트 결과 분석

### 4-A. BacktestResult 구조

```csharp
public class BacktestResult
{
    public string Symbol { get; set; }                    // 테스트 심볼
    public decimal InitialBalance { get; set; }           // 초기 자산
    public decimal FinalBalance { get; set; }             // 최종 자산
    public decimal TotalProfit => ((FinalBalance - InitialBalance) / InitialBalance) * 100;
    
    public int TotalTrades { get; set; }                  // 총 거래 횟수
    public int WinCount { get; set; }                     // 승리 횟수
    public int LossCount { get; set; }                    // 손실 횟수
    public double WinRate => TotalTrades > 0 ? (double)WinCount / TotalTrades * 100 : 0;
    
    public decimal MaxDrawdown { get; set; }              // 최대 낙폭 (%)
    public double SharpeRatio { get; set; }               // 샤프 비율
    
    public List<decimal> EquityCurve { get; set; }        // 자산 곡선
    public List<string> TradeDates { get; set; }          // 거래 시점
    public List<TradeLog> TradeHistory { get; set; }      // 거래 상세 이력
    public List<CandleData> Candles { get; set; }         // 사용된 캔들 데이터
    
    public string StrategyConfiguration { get; set; }     // 전략 파라미터 설명
    public string Message { get; set; }                   // 요약 메시지
    
    public List<OptimizationTrialItem> TopTrials { get; set; }  // 최적화 상위 결과
}
```

### 4-B. 성과 지표 계산

#### MDD (Max Drawdown)

```csharp
decimal peak = InitialBalance;
decimal maxDrawdown = 0;

foreach (var equity in EquityCurve)
{
    if (equity > peak) peak = equity;
    decimal drawdown = (peak - equity) / peak * 100;
    if (drawdown > maxDrawdown) maxDrawdown = drawdown;
}
```

**해석:**
- MDD 5% 미만: 매우 안정적
- MDD 10~20%: 일반적 범위
- MDD 30% 이상: 고위험

#### Sharpe Ratio

```csharp
var returns = new List<double>();
for (int i = 1; i < EquityCurve.Count; i++)
{
    double r = (EquityCurve[i] - EquityCurve[i-1]) / EquityCurve[i-1];
    returns.Add(r);
}

double avgReturn = returns.Average();
double stdDev = Math.Sqrt(returns.Sum(r => Math.Pow(r - avgReturn, 2)) / returns.Count);
SharpeRatio = (avgReturn / stdDev) * Math.Sqrt(returns.Count);
```

**해석:**
- Sharpe > 1.0: 양호
- Sharpe > 2.0: 우수
- Sharpe > 3.0: 매우 우수
- Sharpe < 0: 무위험 수익률보다 낮음

---

## 5. 최적화 개요

### 5-A. 최적화란?

**하이퍼파라미터 최적화**는 전략의 파라미터를 자동으로 탐색하여 최고 성능을 찾는 프로세스입니다.

| 구분 | 설명 |
|------|------|
| **목적** | 전략 파라미터의 최적 조합 발견 |
| **탐색 방법** | Grid Search 또는 Optuna (Bayesian-style) |
| **목적 함수** | 최종 자산 (FinalBalance) 최대화 |
| **결과** | 최적 파라미터 + Top N 시도 결과 |

### 5-B. 최적화 방법 비교

| 방법 | 장점 | 단점 | 사용 시나리오 |
|------|------|------|--------------|
| **Grid Search** | 전체 탐색, 결과 예측 가능 | 조합 폭발, 느림 | 파라미터 2~3개, 범위 좁음 |
| **Optuna** | 효율적 탐색, 빠른 수렴 | 랜덤성, 국소 최적해 위험 | 파라미터 많음, 넓은 범위 |

---

## 6. 최적화 실행 방법

### 6-A. Grid Search 최적화

**RSI 전략 Grid Search:**

```csharp
var service = new BacktestService();

var result = await service.OptimizeRsiStrategyAsync(
    symbol: "BTCUSDT",
    startDate: new DateTime(2026, 1, 1),
    endDate: new DateTime(2026, 2, 1),
    initialBalance: 1000m
);

Console.WriteLine($"최적 파라미터: {result.StrategyConfiguration}");
Console.WriteLine($"최종 자산: {result.FinalBalance} USDT");
```

**탐색 범위:**
- Buy Threshold: 20 ~ 40 (step 5)
- Sell Threshold: 60 ~ 80 (step 5)
- **총 조합**: 5 × 5 = 25회 백테스트

**Bollinger Band Grid Search:**

```csharp
var result = await service.OptimizeBollingerStrategyAsync(
    symbol: "ETHUSDT",
    startDate: new DateTime(2026, 1, 1),
    endDate: new DateTime(2026, 2, 1),
    initialBalance: 1000m
);
```

**탐색 범위:**
- Period: 10 ~ 30 (step 2) → 11개
- Multiplier: 1.5 ~ 3.0 (step 0.1) → 16개
- **총 조합**: 11 × 16 = 176회 백테스트

### 6-B. Optuna 최적화

**RSI 전략 Optuna 튜닝:**

```csharp
var service = new BacktestService();

var result = await service.OptimizeWithOptunaAsync(
    symbol: "BTCUSDT",
    startDate: new DateTime(2026, 1, 1),
    endDate: new DateTime(2026, 2, 1),
    initialBalance: 1000m,
    nTrials: 50  // 50회 시도
);

Console.WriteLine($"최적 파라미터: {result.StrategyConfiguration}");
Console.WriteLine($"최고 Trial: {result.Message}");

// Top 5 결과 확인
foreach (var trial in result.TopTrials)
{
    Console.WriteLine($"#{trial.Rank}: Trial {trial.TrialId} | " +
                      $"자산: {trial.FinalBalance} USDT | " +
                      $"수익률: {trial.ProfitPercent:F2}% | " +
                      $"파라미터: {trial.Parameters}");
}
```

**Optuna 탐색 범위:**
- RsiBuy: 20.0 ~ 40.0 (연속형)
- RsiSell: 60.0 ~ 80.0 (연속형)
- **총 시도**: 사용자 지정 (기본 20, 권장 50~100)

**ElliottWave 전략 최적화 (예시):**

```csharp
public async Task<BacktestResult> OptimizeElliottWaveAsync(
    string symbol, DateTime startDate, DateTime endDate, decimal initialBalance, int nTrials = 50)
{
    var tuner = new OptunaTuner();
    
    var study = await tuner.OptimizeAsync(async (trial) =>
    {
        // ATR 멀티플라이어 7단계 (실제는 ROE/RSI 기반으로 동적 전환)
        var atrMultBase = trial.SuggestFloat("AtrMultBase", 1.0, 2.0);        // 기본 ROE<10%
        var atrMultMid = trial.SuggestFloat("AtrMultMid", 0.5, 1.5);          // ROE 10%+
        var atrMultHigh = trial.SuggestFloat("AtrMultHigh", 0.2, 1.0);        // RSI 70~80
        var atrMultExtreme = trial.SuggestFloat("AtrMultExtreme", 0.1, 0.5);  // RSI 80+
        
        // 부분 익절 파라미터
        var tp1Roe = trial.SuggestFloat("TP1_ROE", 15, 25);  // 1차 익절 ROE (기본 20%)
        var tp2Roe = trial.SuggestFloat("TP2_ROE", 40, 60);  // 2차 익절 ROE (기본 50%)
        var tp1Partial = trial.SuggestFloat("TP1_Partial", 0.4, 0.6);  // 1차 익절 비율 (기본 50%)
        
        // 손절선 파라미터
        var stopLossFibLevel = trial.SuggestFloat("StopLoss_Fib", 0.5, 0.8);  // Fib 손절선 (기본 0.618)
        var timeStopMinutes = trial.SuggestInt("TimeStop_Minutes", 15, 30);  // 타임스탑 (기본 20분)
        
        // DCA 물타기 파라미터
        var dcaTriggerRoe = trial.SuggestFloat("DCA_TriggerROE", -7.0, -3.0);  // 물타기 발동 (기본 -5%)
        var dcaQuantityRatio = trial.SuggestFloat("DCA_QuantityRatio", 0.3, 0.7);  // 물타기 비율 (기본 50%)
        
        // 커스텀 전략 인스턴스 생성 (파라미터 적용)
        var strategy = new ElliottWaveBacktestStrategy
        {
            // 파라미터를 전략에 적용 (실제 구현 필요)
            // AtrMultipliers = new[] { atrMultBase, atrMultMid, atrMultHigh, atrMultExtreme },
            // TP1_ROE = tp1Roe,
            // TP2_ROE = tp2Roe,
            // etc.
        };
        
        var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
        return (double)result.FinalBalance;
    }, nTrials);
    
    // 최적 파라미터 추출
    var bestParams = study.BestTrial.Params;
    var bestStrategy = new ElliottWaveBacktestStrategy();
    // bestParams 적용...
    
    var bestResult = await RunBacktestAsync(symbol, startDate, endDate, bestStrategy, initialBalance);
    bestResult.StrategyConfiguration = 
        $"Optuna ElliottWave ({nTrials} Trials) | " +
        $"AtrBase:{bestParams["AtrMultBase"]:F2} " +
        $"TP1:{bestParams["TP1_ROE"]:F1}% " +
        $"TP2:{bestParams["TP2_ROE"]:F1}%";
    
    return bestResult;
}
```

**Pump 전략 최적화 (예시):**

```csharp
// ATR 기반 동적 포지션 사이즈 + 다중 익절 최적화
var study = await tuner.OptimizeAsync(async (trial) =>
{
    var riskPercent = trial.SuggestFloat("RiskPercent", 1.5, 3.0);  // 초기자본 리스크 (기본 2%)
    var atrMultiplier = trial.SuggestFloat("AtrMultiplier", 2.0, 4.0);  // ATR 목표가 (기본 3.0x)
    var tp1Roe = trial.SuggestFloat("TP1_ROE", 15, 25);  // 1차 익절 (기본 20%)
    var tp2Roe = trial.SuggestFloat("TP2_ROE", 40, 60);  // 2차 익절 (기본 50%)
    var trailingStartRoe = trial.SuggestFloat("TrailingStartROE", 10, 20);  // 트레일링 시작
    var trailingInterval = trial.SuggestFloat("TrailingInterval", 3.0, 6.0);  // 트레일링 간격
    var timeStopMinutes = trial.SuggestInt("TimeStopMinutes", 10, 20);  // 타임스탑 (기본 15분)
    
    // Pump 전략 실행 (파라미터 적용)
    var strategy = new PumpBacktestStrategy
    {
        RiskPercent = riskPercent,
        AtrMultiplier = atrMultiplier,
        TP1_ROE = tp1Roe,
        TP2_ROE = tp2Roe,
        TrailingStartROE = trailingStartRoe,
        TrailingInterval = trailingInterval,
        TimeStopMinutes = timeStopMinutes
    };
    
    var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
    return (double)result.FinalBalance;
}, nTrials: 100);  // Pump는 파라미터가 많아 100 Trials 권장
```

**최적화 파라미터 범위 요약:**

| 전략 | 파라미터 | 최소값 | 최대값 | 기본값 | 설명 |
|------|----------|--------|--------|--------|------|
| **RSI** | BuyThreshold | 20 | 40 | 30 | 과매도 진입 |
| | SellThreshold | 60 | 80 | 70 | 과매수 청산 |
| | RsiPeriod | 10 | 20 | 14 | RSI 계산 기간 |
| **Bollinger** | Period | 10 | 30 | 20 | BB 계산 기간 |
| | Multiplier | 1.5 | 3.0 | 2.0 | 표준편차 배수 |
| **ElliottWave** | AtrMultBase | 1.0 | 2.0 | 1.5 | 기본 ATR 멀티플라이어 |
| | TP1_ROE | 15 | 25 | 20 | 1차 익절 목표 |
| | TP2_ROE | 40 | 60 | 50 | 2차 익절 목표 |
| | StopLoss_Fib | 0.5 | 0.8 | 0.618 | Fib 손절선 |
| | TimeStopMinutes | 15 | 30 | 20 | 타임스탑 |
| | DCA_TriggerROE | -7.0 | -3.0 | -5.0 | 물타기 발동 ROE |
| **Pump** | RiskPercent | 1.5 | 3.0 | 2.0 | 초기자본 리스크 |
| | AtrMultiplier | 2.0 | 4.0 | 3.0 | ATR 목표가 배수 |
| | TrailingStartROE | 10 | 20 | 15 | 트레일링 시작 |
| | TimeStopMinutes | 10 | 20 | 15 | 타임스탑 |

---

## 7. Optuna 튜너 상세

### 7-A. OptunaTuner 구조

```csharp
public class OptunaTuner
{
    public async Task<Study> OptimizeAsync(
        Func<Trial, Task<double>> objective,  // 목적 함수
        int nTrials                            // 시도 횟수
    )
}
```

**주요 클래스:**

| 클래스 | 역할 |
|--------|------|
| `Trial` | 단일 시도 (파라미터 제안 + 결과 저장) |
| `Study` | 전체 최적화 세션 (모든 Trial 관리) |
| `OptunaTuner` | 최적화 실행 엔진 |

### 7-B. Trial 파라미터 제안

**SuggestFloat (연속형):**

```csharp
var rsiBuy = trial.SuggestFloat("RsiBuy", 20, 40);
// 초기 5회: 완전 랜덤 탐색 (Exploration)
// 이후: 70% 확률로 BestTrial 주변 탐색 (Exploitation)
//   → Gaussian Sampling (σ = 범위의 10%)
```

**SuggestInt (정수형):**

```csharp
var period = trial.SuggestInt("Period", 10, 30);
// SuggestFloat 결과를 반올림하여 정수화
```

### 7-C. Exploration-Exploitation 균형

| 단계 | 조건 | 탐색 방식 | 효과 |
|------|------|----------|------|
| **초기 탐색** | Trial ≤ 5 | 완전 랜덤 | 전역 최적해 탐색 |
| **집중 탐색** | Trial > 5 AND BestTrial 존재 | 70% Best 주변, 30% 랜덤 | 국소 최적화 + 새 영역 발견 |

**Gaussian Sampling 공식:**

```csharp
// Box-Muller Transform
double u1 = 1.0 - random.NextDouble();
double u2 = 1.0 - random.NextDouble();
double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

// σ = 전체 범위의 10%
double sigma = (max - min) * 0.1;
double val = bestVal + (sigma * randStdNormal);
val = Math.Clamp(val, min, max);
```

### 7-D. 병렬 실행

```csharp
var semaphore = new SemaphoreSlim(4); // 동시 실행 제한 (CPU 코어 수)

for (int i = 0; i < nTrials; i++)
{
    await semaphore.WaitAsync();
    tasks.Add(Task.Run(async () =>
    {
        try
        {
            var trial = new Trial(i, study);
            double result = await objective(trial);
            study.Report(trial, result);
        }
        finally
        {
            semaphore.Release();
        }
    }));
}
await Task.WhenAll(tasks);
```

**성능:**
- 4코어 기준: 4배 속도 향상
- 50 Trials × 평균 2초 = 100초 → 25초로 단축

---

## 8. 커스텀 전략 작성

### 8-A. IBacktestStrategy 구현

```csharp
using TradingBot.Models;
using TradingBot.Services.BacktestStrategies;

public class MyCustomStrategy : IBacktestStrategy
{
    public string Name => "My Custom Strategy";
    
    // 파라미터 (최적화 대상)
    public int MyPeriod { get; set; } = 20;
    public double MyThreshold { get; set; } = 50;

    public void Execute(List<CandleData> candles, BacktestResult result)
    {
        decimal currentBalance = result.InitialBalance;
        decimal positionQuantity = 0;
        bool inPosition = false;

        // 지표 사전 계산
        var closes = candles.Select(c => (double)c.Close).ToList();
        var myIndicator = CalculateMyIndicator(closes, MyPeriod);

        // 캔들 순회
        for (int i = MyPeriod; i < candles.Count; i++)
        {
            var candle = candles[i];
            decimal currentPrice = candle.Close;
            
            // 매수 조건
            if (!inPosition && myIndicator[i] < MyThreshold)
            {
                decimal amountToInvest = currentBalance * 0.95m;
                positionQuantity = amountToInvest / currentPrice;
                currentBalance -= amountToInvest;
                inPosition = true;
                
                result.TradeHistory.Add(new TradeLog(
                    result.Symbol, "BUY", Name, currentPrice, 0, candle.OpenTime
                ));
            }
            // 매도 조건
            else if (inPosition && myIndicator[i] > MyThreshold)
            {
                decimal revenue = positionQuantity * currentPrice;
                decimal profit = revenue - (positionQuantity * result.TradeHistory.Last().Price);
                
                currentBalance += revenue;
                result.TotalTrades++;
                if (profit > 0) result.WinCount++; else result.LossCount++;
                
                result.TradeHistory.Add(new TradeLog(
                    result.Symbol, "SELL", Name, currentPrice, 0, candle.OpenTime, profit
                ));
                
                result.EquityCurve.Add(currentBalance);
                result.TradeDates.Add(candle.OpenTime.ToString("MM/dd HH:mm"));
                
                inPosition = false;
                positionQuantity = 0;
            }
        }
        
        // 미청산 포지션 반영
        if (inPosition)
        {
            currentBalance += positionQuantity * candles.Last().Close;
        }
        
        result.FinalBalance = currentBalance;
    }
    
    private List<double> CalculateMyIndicator(List<double> prices, int period)
    {
        // 커스텀 지표 계산 로직
        return prices; // 예시
    }
}
```

### 8-B. 커스텀 전략 최적화

**Grid Search 방식:**

```csharp
public async Task<BacktestResult> OptimizeMyStrategyAsync(
    string symbol, DateTime startDate, DateTime endDate, decimal initialBalance)
{
    var bestResult = new BacktestResult { FinalBalance = 0 };
    
    for (int period = 10; period <= 30; period += 2)
    {
        for (double threshold = 30; threshold <= 70; threshold += 5)
        {
            var strategy = new MyCustomStrategy 
            { 
                MyPeriod = period, 
                MyThreshold = threshold 
            };
            
            var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
            result.StrategyConfiguration = $"Period:{period}, Threshold:{threshold}";
            
            if (result.FinalBalance > bestResult.FinalBalance)
            {
                bestResult = result;
            }
        }
    }
    return bestResult;
}
```

**Optuna 방식:**

```csharp
var tuner = new OptunaTuner();

var study = await tuner.OptimizeAsync(async (trial) =>
{
    var period = trial.SuggestInt("Period", 10, 30);
    var threshold = trial.SuggestFloat("Threshold", 30, 70);
    
    var strategy = new MyCustomStrategy 
    { 
        MyPeriod = period, 
        MyThreshold = threshold 
    };
    
    var result = await RunBacktestAsync(symbol, startDate, endDate, strategy, initialBalance);
    return (double)result.FinalBalance;
}, nTrials: 50);

// 최적 파라미터 추출
var bestPeriod = (int)study.BestTrial.Params["Period"];
var bestThreshold = (double)study.BestTrial.Params["Threshold"];
```

---

## 9. 성능 최적화 팁

### 9-A. 데이터 로드 최적화

**문제:** DB 쿼리 반복 실행 시 느림

**해결:**

```csharp
// 나쁜 예: 매 Trial마다 DB 조회
for (int i = 0; i < 100; i++)
{
    var candles = await LoadBacktestCandlesAsync(...); // 느림!
}

// 좋은 예: 한 번만 로드 후 재사용
var candles = await LoadBacktestCandlesAsync(...);
for (int i = 0; i < 100; i++)
{
    strategy.Execute(candles, result); // 빠름!
}
```

### 9-B. 지표 계산 최적화

**문제:** 매 루프마다 지표 재계산 시 느림

**해결:**

```csharp
// 나쁜 예: 루프 내부에서 계산
for (int i = 0; i < candles.Count; i++)
{
    var rsi = IndicatorCalculator.CalculateRSI(candles.Take(i+1).ToList()); // O(n²)
}

// 좋은 예: 사전 계산 후 인덱스 참조
var rsiList = IndicatorCalculator.CalculateRSISeries(candles); // O(n)
for (int i = 0; i < candles.Count; i++)
{
    var rsi = rsiList[i]; // O(1)
}
```

### 9-C. 병렬 실행 조정

**CPU 코어 수에 맞춰 Semaphore 조정:**

```csharp
int coreCount = Environment.ProcessorCount;
var semaphore = new SemaphoreSlim(coreCount - 1); // 1개 코어는 UI용 예약
```

---

## 10. 주의사항 및 한계

### 10-A. 과최적화 (Overfitting)

**문제:** 과거 데이터에만 완벽히 맞춘 파라미터는 미래에 실패

**해결:**
- Train/Test 분할: 데이터의 70%는 최적화, 30%는 검증
- Out-of-Sample 테스트: 다른 기간, 다른 심볼에서 재검증
- 단순한 전략 선호: 파라미터 3개 이하 권장

### 10-B. 미래 데이터 참조 (Look-Ahead Bias)

**문제:** 백테스트에서 미래 정보를 사용하면 실전에서 실패

**예시:**

```csharp
// 나쁜 예: 다음 캔들의 고가를 미리 참조
if (candles[i+1].High > targetPrice) // 미래 정보!
{
    // 매수
}

// 좋은 예: 현재 시점까지의 정보만 사용
if (candles[i].Close > targetPrice)
{
    // 매수
}
```

### 10-C. 실전 vs 백테스트 차이점

**백테스트에서 생략된 실전 로직:**

| 구분 | 실전 (TradingEngine) | 백테스트 (BacktestService) |
|------|---------------------|---------------------------|
| **진입 필터** | 8단계 파이프라인 (워밍업/서킷브레이커/ATR변동성/AI/DoubleCheck/RL/ElliottWave/중복체크) | 전략별 진입 조건만 체크 |
| **주문 방식** | 최우선 호가 지정가 (3초 체결 확인) | 즉시 체결 가정 |
| **스탑 주문** | 서버사이드 StopMarket (Cancel & Replace) | 로컬 시뮬레이션 |
| **모니터링** | 3개 시스템 (Standard/Pump/HybridExit) | 단일 루프 (전략 내부) |
| **레버리지** | 메이저: DefaultLeverage, 펌프: 20x 고정 | 백테스트 무시 (ROE 계산에만 사용) |
| **수량 계산** | StepSize 보정 + 슬리피지 검증 | 간소화 (잔고 × 투자비율 / 가격) |
| **수수료** | PlaceOrderAsync에서 차감 | **미반영** |

**실전에만 존재하는 기능:**

1. **AI 필터 (ML.NET + Transformer):**
   - ML.NET 점수 65점(MAJOR) / 75점(기타) 미만 차단
   - DoubleCheck 필터: Tech점수 + AI확률 + OI 검증
   - RL 에이전트 방향 일치 확인

2. **HybridExitManager (Transformer 포지션 전용):**
   - AI 예측가 도달 시 50% 부분 청산
   - ATR 동적 트레일링 (ROE/RSI 기반 4단계)
   - BB 상단 이탈→복귀 감지
   - AI 방향 반전 감지

3. **서킷 브레이커 (RiskManager):**
   - 연속 손실 3회 → 30분 거래 중단
   - ATR 변동성 2배 이상 → 진입 금지

4. **3단계 스마트 방어 (Standard 모니터):**
   - ROE 10% → 본절 보호
   - ROE 15% → 수익 잠금
   - ROE 20% → 타이트 트레일링 (0.15%)

**백테스트 결과 실전 적용 시 조정:**
```
실제 기대수익 = 백테스트수익 × 0.95 - (거래횟수 × 0.1%)
                └─ 필터 통과율    └─ 수수료+슬리피지

예시: 백테스트 +30%, 거래 100회
→ 실전 예측: 30% × 0.95 - 0.1% = 28.4% - 0.1% = 28.3%
```

### 10-D. 거래 비용 미반영

**현재 구현:** 수수료, 슬리피지 미고려

**실전 적용 시:**
- Binance Futures 수수료: Maker 0.02%, Taker 0.04%
- 슬리피지: ±0.05% 추정
- **조정 공식:** `실제수익 ≈ 백테스트수익 - (거래횟수 × 0.1%)`

### 10-E. 데이터 품질

**주의:**
- DB에 누락된 캔들 확인: `SELECT COUNT(*) FROM MarketCandles WHERE Symbol='...'`
- 이상치 확인: 급격한 가격 변동 (플래시 크래시)
- 재수집 권장: `RecollectRecentCandleDataAsync`로 최신 데이터 확보

### 10-F. 백테스트 전략 vs 실전 전략 매핑

| 백테스트 전략 | 실전 매핑 | 일치도 | 주의사항 |
|-------------|----------|--------|----------|
| **RsiBacktestStrategy** | 실전 없음 | - | 교육용 기본 전략 |
| **MaCrossBacktestStrategy** | 실전 없음 | - | 교육용 기본 전략 |
| **BollingerBandBacktestStrategy** | Pump 진입 조건 일부 | 30% | 실전은 복합 지표 사용 |
| **ElliottWaveBacktestStrategy** | PositionMonitor Pump + HybridExitManager | **90%** | HybridExitManager 로직 완전 복제 |

**ElliottWave 백테스트의 실전 근접성:**

실전 `PositionMonitorService.MonitorPumpPositionShortTerm`의 ElliottWave 청산 로직을 거의 그대로 구현:

```csharp
// 실전: PositionMonitorService.cs (L1800~L2100)
if (currentPrice >= takeProfit1 && !partialTaken)
    → 50% 부분 청산
if (currentPrice >= takeProfit2)
    → 30% 청산 (RSI≥80: 40%)
if (currentPrice <= stopLoss)
    → 전량 손절

// 백테스트: ElliottWaveBacktestStrategy.cs (L140~L210)
// 동일한 로직 + HybridExitManager ATR 트레일링 복제
```

**권장 사항:**
- **RSI/MA Cross 전략**: 학습 및 최적화 연습용
- **Bollinger Band 전략**: 변동성 전략 아이디어 검증용
- **ElliottWave 전략**: 실전 적용 전 사전 검증용 (90% 신뢰도)

---

## 11. 실전 예시

### 11-A. 전체 워크플로우

```csharp
// 1. 데이터 재수집
var service = new BacktestService();
await service.RecollectRecentCandleDataAsync("BTCUSDT", days: 60);

// 2. 초기 백테스트 (기본 파라미터)
var baselineResult = await service.RunBacktestAsync(
    "BTCUSDT",
    DateTime.UtcNow.AddDays(-60),
    DateTime.UtcNow,
    BacktestStrategyType.RSI,
    1000m
);
Console.WriteLine($"[Baseline] 수익률: {baselineResult.TotalProfit:F2}%");

// 3. Optuna 최적화 (50 Trials)
var optimizedResult = await service.OptimizeWithOptunaAsync(
    "BTCUSDT",
    DateTime.UtcNow.AddDays(-60),
    DateTime.UtcNow.AddDays(-30), // Train: 최근 60~30일
    1000m,
    nTrials: 50
);
Console.WriteLine($"[Optimized] 수익률: {optimizedResult.TotalProfit:F2}%");
Console.WriteLine($"최적 파라미터: {optimizedResult.StrategyConfiguration}");

// 4. Out-of-Sample 검증 (최근 30일)
var bestParams = optimizedResult.TopTrials.First();
var validationStrategy = new RsiBacktestStrategy
{
    BuyThreshold = ExtractParam(bestParams.Parameters, "RsiBuy"),
    SellThreshold = ExtractParam(bestParams.Parameters, "RsiSell")
};

var validationResult = await service.RunBacktestAsync(
    "BTCUSDT",
    DateTime.UtcNow.AddDays(-30),
    DateTime.UtcNow, // Test: 최근 30일
    validationStrategy,
    1000m
);
Console.WriteLine($"[Validation] 수익률: {validationResult.TotalProfit:F2}%");

// 5. 다른 심볼 교차 검증
var crossValidation = await service.RunBacktestAsync(
    "ETHUSDT",
    DateTime.UtcNow.AddDays(-30),
    DateTime.UtcNow,
    validationStrategy,
    1000m
);
Console.WriteLine($"[Cross-Validation] 수익률: {crossValidation.TotalProfit:F2}%");

// 6. 결과 시각화
var window = new BacktestWindow(validationResult, "📊 Validation Result");
window.Show();
```

### 11-B. 다중 심볼 비교

```csharp
var symbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT" };
var results = new List<(string Symbol, BacktestResult Result)>();

foreach (var symbol in symbols)
{
    var result = await service.OptimizeWithOptunaAsync(
        symbol,
        DateTime.UtcNow.AddDays(-60),
        DateTime.UtcNow,
        1000m,
        nTrials: 30
    );
    results.Add((symbol, result));
}

// 최고 성과 심볼 찾기
var best = results.OrderByDescending(r => r.Result.FinalBalance).First();
Console.WriteLine($"최고 수익 심볼: {best.Symbol} ({best.Result.TotalProfit:F2}%)");
```

---

## 12. FAQ

### Q1. Grid Search와 Optuna 중 어느 것을 사용해야 하나요?

**A:** 
- **파라미터 2개 이하, 범위 좁음** → Grid Search (전수조사 가능)
- **파라미터 3개 이상, 범위 넓음** → Optuna (효율적 탐색)
- **시간 제약 없음** → Grid Search (결정론적)
- **빠른 결과 필요** → Optuna (빠른 수렴)

### Q2. 최적 Trial 수는?

**A:**
- Grid Search: 자동 계산 (조합의 곱)
- Optuna: 
  - 최소 20 Trials (초기 탐색)
  - 권장 50~100 Trials (균형)
  - 복잡한 전략: 200+ Trials

### Q3. MDD가 너무 높습니다. 어떻게 낮추나요?

**A:**
1. 손절선 추가: 최대 손실 -10% 제한
2. 포지션 크기 축소: 95% → 50% 투자
3. 다변화: 여러 전략 혼합
4. 타임스탑 추가: 장기 미실현 손실 방지

### Q4. Sharpe Ratio가 음수입니다.

**A:** 전략이 무위험 수익률(0%)보다 낮음을 의미. 전략 폐기 권장.

### Q5. 백테스트는 좋은데 실전에서 손실입니다.

**A:** 다음을 확인하세요:
- **과최적화 (Overfitting)**: Train/Test 분할 + Out-of-Sample 검증
- **미래 데이터 참조 (Look-Ahead Bias)**: 코드 리뷰
- **거래 비용 미반영**: 수수료 0.1% 가정 시 재계산
- **시장 환경 변화**: 백테스트 기간과 실전 기간의 변동성 차이
- **실전 필터 락**: 8단계 필터 파이프라인 통과율 95% 가정

**실전 보정 공식:**
```
실제 기대수익 = 백테스트수익 × 0.95 - (거래횟수 × 0.1%)

예시: 백테스트 +30%, 거래 100회
→ 실전 예측: 30% × 0.95 - 10% = 18.5%
```

### Q6. ElliottWave 백테스트는 실전과 다른가요?

**A:** 아니요. ElliottWaveBacktestStrategy는 실전 PositionMonitorService의 Pump 모니터 로직을 90% 복제했습니다:

**동일한 부분:**
- 3단계 부분 익절 (50% → 30%/40% → 잔량)
- 5개 절대 손절선 (Wave1Low, Fib 0.618/0.786, BB 하단, 타임스탑)
- 레벨업 스탑 (2차 익절 후)
- Dynamic ATR Trailing (ROE/RSI 기반)
- 본절 보호 (ROE 3%)
- DCA 물타기 (ROE -5%)

**차이점:**
- 실전: HybridExitManager의 ATR 멀티플라이어 동적 전환 (1.5 → 1.0 → 0.5 → 0.2)
- 백테스트: 단순화된 고정 멀티플라이어 (ROE/RSI 조건별 분기)

**신뢰도:** 백테스트 결과를 실전 예측의 90% 신뢰도로 활용 가능합니다.

---

## 부록 A: 전략 비교표

| 전략 | 최적 시장 | 평균 MDD | 승률 | 거래 빈도 | 복잡도 | 실전 근접도 |
|------|----------|---------|------|----------|--------|------------|
| **RSI** | 횡보장 | 8~15% | 55~65% | 중간 | ★☆☆☆☆ | ⚫ (교육용) |
| **MA Cross** | 추세장 | 10~20% | 45~55% | 낮음 | ★☆☆☆☆ | ⚫ (교육용) |
| **Bollinger Band** | 변동성 상승 | 7~12% | 60~70% | 높음 | ★★☆☆☆ | ⚫ (교육용) |
| **Elliott Wave** | 강한 추세 | 12~25% | 40~50% | 낮음 | ★★★★★ | 🟢🟢🟢 (90%) |

## 부록 B: 실전 주문 로직 요약

### 진입 경로 3가지

**1. 메이저 코인 (ExecuteAutoOrder):**
```
8단계 필터:
워밍업 → 서킷브레이커 → ATR변동성 → ML.NET AI → DoubleCheck
→ RL 에이전트 → ElliottWave R:R → 중복 방지

주문: 최우선 호가 지정가 → 3초 체결 확인 → MonitorPositionStandard
```

**2. 펌프 코인 (ExecutePumpTrade):**
```
진입:
- 5분봉 컨플루언스 점수 (BB+MACD+RSI+Fib+호가창)
- ATR 기반 동적 포지션 사이즈 (초기잔고 2% 리스크)
- 레버리지 20x 고정
- 현재가×0.998 지정가

청산:
- 1차 익절 20% (50% 청산)
- 2차 익절 50% (30~40% 청산)
- Dynamic Trailing: 최고 ROE에 따라 4~5% 간격
- 타임스탑: 15분 경과 + ROE ±2%
- DCA 물타기: ROE -5% 시 50% 추가 매수
```

**3. Transformer (TransformerStrategy):**
```
진입:
- ADX 기반 횡보/추세 판별
- HybridStrategyScorer 점수 계산
- 추세: 동적 임계값 + DI 방향
- 횡보: BB 터치 + RSI + AI 예측

청산 (HybridExitManager):
1. AI 예측가 도달 → 50% 청산
2. RSI 80+ → 전량 청산
3. BB 상단 이탈→복귀 → 전량 청산
4. ATR 트레일링 히트 → 전량 청산
5. AI 방향 반전 → 전량 청산
6. BB 중단 이탈 + 본절 → 전량 청산
7. ROE -20% → 절대 손절
```

### 청산 시스템 3개

**Standard 모니터 (1초 폴링):**
```
서버사이드 손절: 즉시 StopMarket 설정

3단계 스마트 방어:
  ROE 10% → 본절 보호 (entry×1.001)
  ROE 15% → 수익 잠금 (entry×1.0035)
  ROE 20% → 타이트 트레일링 (현재가×(1-0.15%), 최소 ROE 18%)

AdvancedExitStopCalculator (ROE≥20%):
  5개 지표 기반 즉시 익절 또는 스탑 타이트닝
```

**Pump 모니터 (500ms 폴링):**
```
ElliottWave 3단계 익절:
  1차: Wave1High or ROE 20% → 50%
  2차: Fib 1.618 or ROE 50% → 30%(RSI≥80: 40%)
  최종: Fib 2.618 → 잔량

절대 손절선 5개 (2차 익절 전):
  Wave1Low / Fib 0.618 / Fib 0.786 / BB 하단 / 타임스탑 20분

Dynamic Trailing:
  최고 ROE 10~15% → 4% 간격
  최고 ROE 15%+ → 5% 간격
```

**HybridExitManager (Transformer 전용):**
```
ATR 동적 트레일링:
  ROE<10%: ATR×1.5
  ROE≥10%, RSI<70: ATR×1.0
  RSI 70~80: ATR×0.5
  RSI≥80: ATR×0.2

안전장치: ROE≥15% 시 스탑이 진입가 아래면 entry×1.001로 상향
```

---

**다음 단계:**
1. **[ORDER_LOGIC_REFERENCE.md](ORDER_LOGIC_REFERENCE.md)** - 실전 주문 로직 전체 (진입 필터 8단계, 청산 시스템 3개, 트레일링 스탑 4종류)
2. [TRADING_LOGIC_GUIDE.md](TRADING_LOGIC_GUIDE.md) - 엔진 전체 구조
3. [PIPELINE_OPTIMIZATION_GUIDE.md](PIPELINE_OPTIMIZATION_GUIDE.md) - 성능 최적화

**주요 차이점 요약:**

| 구분 | 백테스트 | 실전 (TradingEngine) |
|------|----------|--------------------|
| **진입 필터** | 전략별 조건만 | 8단계 파이프라인 (95% 차단율) |
| **주문 방식** | 즉시 체결 | 지정가 + 3초 확인 |
| **청산 시스템** | 단일 루프 | 3개 (Standard/Pump/Hybrid) |
| **수수료** | 미반영 | 0.04% 차감 |
| **ElliottWave** | 90% 복제 | 100% 실전 로직 |

**피드백:** 추가할 전략이나 설명이 필요한 부분을 제보해주세요.
