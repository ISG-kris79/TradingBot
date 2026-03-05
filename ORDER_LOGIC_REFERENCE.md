# 📋 주문 로직 전체 레퍼런스

> 최종 업데이트: 2026-03-05  
> 이 문서는 진입, 익절, 손절, 트레일링 스탑, 부분 청산, 물타기 등 모든 주문 관련 로직을 정리한 파일입니다.

---

## 목차

1. [진입 경로 3가지](#1-진입-경로)
2. [주문 실행 방식](#2-주문-실행-방식)
3. [청산 시스템 (3개)](#3-청산-시스템)
4. [트레일링 스탑](#4-트레일링-스탑)
5. [AdvancedExitStopCalculator (지표 기반)](#5-advancedexitstopcalculator)
6. [수량/마진/레버리지 계산](#6-수량마진레버리지-계산)
7. [수정 이력](#7-수정-이력)

---

## 1. 진입 경로

### 1-A. 메이저 코인 — `ExecuteAutoOrder` (TradingEngine.cs)

**트리거 소스:**
| 소스 | 설명 |
|------|------|
| `MajorCoinStrategy.OnTradeSignal` | 메이저 코인(BTC/ETH/SOL/XRP) 전략 |
| `TransformerStrategy.OnTradeSignal` | Transformer AI 예측 + 기술 합산 |
| `ElliottWave3WaveStrategy` | 엘리엇 3파 확정 시그널 |

**완전한 필터 파이프라인 (8단계):**

```
1. 워밍업 확인 (엔진 시작 후 30초간 차단)
2. 서킷 브레이커 확인 (RiskManager.IsTripped)
3. 5분봉 200개 로드 + ATR 변동성 체크
   └ ATR비율 > 2.0x → "흔들기 구간" 진입 금지
   └ ATR비율 > 1.5x → 경고 로그
4. ML.NET AI 필터
   ├ 기본점수 = Probability × 100
   ├ 보너스: EMA20 눌림목 +10, 숏스퀴즈 +15
   └ 임계값: MAJOR=65점, 기타=75점 미만 → 차단
5. Double Check 필터 (Transformer 확률 + OI + 기술점수)
   ├ Strong Trend: Tech≥80 + AI≥0.50
   ├ Sideways: Tech≥50 + AI≥0.75 + OI확인
   ├ Normal: Tech≥65 + AI≥0.60
   └ SHORT: Tech≥70 + downProb≥0.65
6. RL 에이전트 방향 확인 (반대 방향 시 차단)
7. ElliottWave 손익비 검증 (R:R < 1:2 → 차단)
8. 중복 진입 방지 (lock + _activePositions 체크)
```

**주문 실행:**
```
레버리지 설정 → 수량 = (마진 × 레버리지) / 현재가
→ StepSize 보정 → 슬리피지 ±0.05% 검증
→ 최우선 호가 지정가 주문
→ 3초 후 체결 확인 (미체결 시 취소 + 포지션 해제)
→ MonitorPositionStandard 감시 시작
```

### 1-B. 펌프 코인 — `ExecutePumpTrade` (TradingEngine.cs)

**트리거:** `PumpScanStrategy.OnPumpDetected` → `HandlePumpEntry`

```
1. 5분봉 컨플루언스 점수 (BB+MACD+RSI+Fib+호가창)
2. 피보나치 0.323~0.618 범위 확인
3. AI 확률 60% 미만 차단
4. ATR 기반 동적 포지션 사이즈 (초기잔고 2% 리스크)
5. 레버리지 20x 고정
6. 현재가×0.998 지정가 주문
7. 3초 후 체결 확인 (미체결 시 취소)
8. MonitorPumpPositionShortTerm 감시 시작 (500ms)
```

### 1-C. Transformer — `TransformerStrategy`

```
1. 5분봉 240개 로드 → CandleData 변환
2. ADX 기반 횡보/추세 모드 판별
3. HybridStrategyScorer 점수 계산 (LONG/SHORT)
4. 추세: 동적 임계값 + DI 방향 확인
5. 횡보: BB 터치 + RSI 조건 + AI 예측
6. 시그널 → ExecuteAutoOrder로 전달
7. HybridExitManager에 등록 (SIDEWAYS 제외)
```

---

## 2. 주문 실행 방식

### 2-1. BinanceExchangeService.PlaceOrderAsync

| 구분 | 값 |
|------|-----|
| price 있음 | `FuturesOrderType.Limit` (지정가) |
| price = null | `FuturesOrderType.Market` (시장가) |
| 시뮬레이션 | `return true` (주문 미실행) |
| 정밀도 | `Floor(qty / stepSize) * stepSize` |

### 2-2. PlaceStopOrderAsync

```csharp
FuturesOrderType.StopMarket, stopPrice: stopPrice, reduceOnly: true
```
→ 서버사이드 손절 주문 (시뮬레이션 체크 없음)

### 2-3. 청산 주문 (ExecuteMarketClose)

```csharp
PlaceOrderAsync(side, quantity, price: null, reduceOnly: true)
→ 시장가 + ReduceOnly
```

### 2-4. 긴급 청산 (EmergencyClosePositionAsync)

```csharp
PlaceOrderAsync(Market, quantity: null, closePosition: true)
→ 전체 포지션 시장가 종료 (수량 미지정)
```

---

## 3. 청산 시스템

### 3-A. Standard 모니터링 (PositionMonitorService, 1초 폴링)

메이저 코인 + Transformer 포지션 공통.

#### 서버사이드 손절 (즉시 설정)

```
TREND 모드:
  롱: stopPrice = entryPrice × (1 - StopLossRoe / leverage / 100)
  숏: stopPrice = entryPrice × (1 + StopLossRoe / leverage / 100)

SIDEWAYS 모드:
  customStopLossPrice 직접 사용
```

#### 3단계 스마트 방어 시스템

| 단계 | ROE 조건 | 동작 | 스탑 가격 |
|------|---------|------|-----------|
| **1단계** | ≥ 10% | 본절 보호 | `entry × 1.001` (롱) / `entry × 0.999` (숏) |
| **2단계** | ≥ 15% | 수익 잠금 | `entry × 1.0035` (롱) / `entry × 0.9965` (숏) |
| **3단계** | ≥ 20% | 타이트 트레일링 | `현재가 × (1 - 0.15%)`, **최소 ROE 18% 보장** |

```
3단계 트레일링 공식:
  롱: newStop = currentPrice × (1 - 0.0015)
       최소선 = entry × (1 + 18/leverage/100)
       스탑은 상승만 (절대 뒤로 안 감)

  숏: newStop = currentPrice × (1 + 0.0015)
       최소선 = entry × (1 - 18/leverage/100)
       스탑은 하락만
```

#### 지표 기반 익절 (ROE ≥ 20% 시)

`AdvancedExitStopCalculator` 호출 → 5개 지표 검사
- 즉시 익절: BB 회귀 또는 3개 이상 신호 동시
- 지표 스탑이 더 타이트하면 기존 스탑 갱신

#### SIDEWAYS 모드 특수 처리

- 커스텀 TP 도달 시 → **50% 부분 익절** → 잔여분 본절가 보호
- 커스텀 SL 도달 시 → 전량 청산

#### 최종 청산 트리거

| 조건 | 동작 |
|------|------|
| ROE ≥ TargetRoe (설정값) | 전량 익절 |
| ROE ≤ -StopLossRoe (설정값) | 전량 손절 |

---

### 3-B. Pump 모니터링 (PositionMonitorService, 500ms 폴링)

#### 파라미터

| 항목 | 기본값 |
|------|--------|
| 손절 ROE | `_settings.StopLossRoe` |
| 물타기 기준 | -5.0% |
| 1차 익절 | 20.0% (설정가능) |
| 2차 익절 | 50.0% (설정가능) |
| 타임스탑 | 15분 (설정가능) |

#### ATR 기반 동적 목표가

```
targetPriceMove = ATR × 3.0
dynamicROE = (targetPriceMove / entryPrice) × leverage × 100
trailingStartROE = Clamp(dynamicROE, 15.0, 60.0)
```

#### 타임스탑

```
경과시간 ≥ pumpTimeStopMinutes AND ROE가 -2% ~ +2% 범위
→ 횡보로 판단 → 즉시 시장가 청산
```

#### BB 중단 하향 음봉 손절

```
진입 후 5분 경과 + 마지막 캔들 음봉 + 종가 < BB(20,2) 중단
→ 급등 추세 실패 → 즉시 손절
```

#### ElliottWave 3단계 부분 익절

| 단계 | 조건 | 청산 비율 | 후속 조치 |
|------|------|----------|-----------|
| **1차** | Wave1High 도달 or ROE ≥ TP1(20%) | **50%** | 본절가 = 진입가 |
| **2차** | Fib 1.618 도달 or ROE ≥ TP2(50%) | **30%** (RSI≥80이면 40%) | 스탑 = Wave1High |
| **최종** | Fib 2.618 도달 | **잔량 전부** | 시장가 청산 |

#### ElliottWave 절대 손절선 (2차 익절 이전에만)

1. `현재가 ≤ Wave1Low` → Wave1 이탈
2. `현재가 ≤ Fib 0.618` → 되돌림 실패
3. `현재가 ≤ Fib 0.786` → 논리 손절
4. `현재가 < BB 하단` → 밴드 이탈
5. 경과 20분 AND |ROE| ≤ 2% → 타임스탑

#### 레벨업 스탑 (2차 익절 이후)

1. 종가 < BB 중단(20EMA) → 추세 사망 → 전량 청산
2. `현재가 < Wave1High` → 레벨업 손절 → 전량 청산
3. BB 상단 밖→안 복귀 + RSI 다이버전스 → 밴드라이딩 종료

#### Dynamic Trailing

| 최고 ROE | 트레일링 간격 | 시작 ROE |
|---------|-------------|---------|
| 10% ~ 15% | 4.0% | min(현재, 10%) |
| ≥ 15% | 5.0% | 유지 |

- 첫 발동 → **50% 부분 청산**
- 이후 발동 → 전량 시장가 청산

#### 본절 보호

```
ROE ≥ 3.0% → 손절 라인을 0%로 이동
```

#### DCA 물타기 (1회 제한)

```
조건: 미물타기 + 미본절 + ROE ≤ -5.0%
수량: 기존 포지션의 50%
방식: 시장가 추가 매수 (ReduceOnly=false)
평단 재계산: 가중평균
물타기 후: 손절 ROE를 12%로 완화
```

---

### 3-C. HybridExitManager (Transformer 포지션 전용)

`TransformerStrategy.OnTradeSignal` → `HybridExitManager.RegisterEntry`

#### 이탈 조건 (우선순위)

| # | 조건 | 동작 |
|---|------|------|
| 1 | AI 예측가(PredictedPrice) 도달 | **50% 부분 청산** |
| 2 | RSI ≥ 80 (롱) / RSI ≤ 20 (숏) | **전량 청산** |
| 3 | BB 상단 이탈→복귀 (롱) / 하단 이탈→반등 (숏) | **전량 청산** |
| 4 | ATR 트레일링 스탑 히트 | **전량 청산** |
| 5 | AI 예측 방향 반전 (1차 익절 이후) | **전량 청산** |
| 6 | BB 중단 이탈 + HighestROE ≥ 10% + 본절 도달 | **전량 청산** |
| 7 | ROE ≤ -20% | **전량 청산** (절대 손절) |

---

## 4. 트레일링 스탑

### 4-A. Standard 트레일링 (3단계 스마트 방어)

위 [3-A](#3-a-standard-모니터링) 참고. ROE 20% 이후 `현재가 × (1 - 0.15%)` 방식.

### 4-B. Pump 트레일링 (Dynamic)

| 최고 ROE | 간격 |
|---------|------|
| 10%~15% | ROE 4% |
| 15%+ | ROE 5% |

### 4-C. HybridExitManager ATR 동적 트레일링

| ROE 구간 | ATR 멀티플라이어 | 설명 |
|---------|-----------------|------|
| < 10% | **1.5** | 넓은 방어 (변동성 흡수) |
| ≥ 10%, RSI < 70 | **1.0** | 추세 진행 중 |
| RSI 70~80 | **0.5** | 과열 접근 |
| RSI ≥ 80 | **0.2** | 극단 밀착 (피날레) |

```
트레일링 스탑 공식:
  trailingDistance = ATR × multiplier
  롱: stopPrice = HighestPriceSinceEntry - trailingDistance
  숏: stopPrice = LowestPriceSinceEntry + trailingDistance

안전장치 (ROE ≥ 15%):
  스탑이 진입가 아래이면 → entry + 0.1% 으로 상향
```

### 4-D. BinanceExecutionService (서버사이드 갱신)

Cancel & Replace 방식:
1. 기존 StopMarket 취소
2. 새 가격으로 StopMarket 재등록
3. 최소 변동폭: 0.1% 이상일 때만 갱신
4. 방향 검증: 롱→상향만, 숏→하향만

---

## 5. AdvancedExitStopCalculator

ROE ≥ 20% 시 Standard 모니터에서 호출.

### 5개 지표 스코어링

| 지표 | 조건 | tightModifier | 효과 |
|------|------|--------------|------|
| 엘리엇 5파 or RSI 극단(80+) | IsWave5 or IsRsiExtreme | ×0.4 | 스탑 간격 60% 축소 |
| MACD 히스토 감소 or 데드크로스 | MACD 약화 | ×0.7 | 30% 추가 축소 |
| Fib 1.618 도달 | 목표가 도달 | 수익보장선 상향 | 최소 Fib1.618 확보 |
| BB 상단 이탈→복귀 | 밴드 회귀 | **즉시 익절** | |
| RSI 과매수(70+) | 과열 | ×0.85 | 15% 축소 |

**최종 스탑 = max(ATR 트레일링 × tightModifier, 수익보장선)**

**즉시 익절 조건:** BB 회귀 또는 신호 3개 이상 동시 발생

---

## 6. 수량/마진/레버리지 계산

| 항목 | 메이저 코인 | 펌프 코인 |
|------|-----------|----------|
| **마진** | `DefaultMargin` (고정, 기본 200U) | ATR 기반 동적 (초기잔고 2% 리스크) |
| **레버리지** | `DefaultLeverage` (설정값) | 20x 고정 |
| **수량** | `(margin × leverage) / currentPrice` | `(margin × leverage) / limitPrice` |
| **주문 타입** | 최우선 호가 지정가 | 현재가 × 0.998 지정가 |
| **체결 확인** | ✅ 3초 후 확인 + 미체결 취소 | ✅ 3초 후 확인 + 미체결 취소 |
| **StepSize 보정** | ✅ | ✅ |

### 펌프 코인 동적 포지션 사이즈 공식

```
riskPerTrade = InitialBalance × 2%
positionSizeCoins = riskPerTrade / (ATR × 2)
marginUsdt = (coins × price) / leverage
```

### StepSize 보정

```csharp
decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0.001m;
quantity = Math.Floor(quantity / stepSize) * stepSize;
```

---

## 7. 수정 이력

### 2026-03-05 수정사항

1. **ISSUE-1 좀비 포지션 수정** — `PlaceOrderAsync` 실패 시 `_activePositions.Remove(symbol)` 추가
2. **ISSUE-2 미체결 방치 수정** — `ExecuteAutoOrder`에 3초 체결 확인 + 미체결 주문 취소 로직 추가
3. **ISSUE-7 Quantity 누락 수정** — `SyncCurrentPositionsAsync`에서 `Quantity = Math.Abs(pos.Quantity)` 설정
4. **ISSUE-10 NullRef 수정** — `_transformerStrategy.OnTradeSignal` 구독을 `if (_transformerStrategy != null)` 블록 안으로 이동

### 미해결 사항 (향후 고려)

| # | 문제 | 심각도 |
|---|------|--------|
| ISSUE-3 | `BinanceExecutionService.ExecuteEntryWithStopAsync` 미사용 (진입+스탑 동시 설정) | Medium |
| ISSUE-4 | Transformer 포지션 3중 모니터링 레이스 컨디션 | Medium |
| ISSUE-5 | 서버사이드 스탑 주문 ID 미추적 → 고아 스탑 잔존 가능 | Medium |
| ISSUE-6 | `BuildTechnicalDataForExitSignal` 지표값 하드코딩 TODO | Medium |
| ISSUE-8 | `_activePositions` → `ConcurrentDictionary` 권장 | Low |
| ISSUE-9 | `BinanceExchangeService.PlaceOrderAsync` 재시도 없음 | Low |
