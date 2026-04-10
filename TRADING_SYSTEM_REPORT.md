# CoinFF TradingBot v3.2.0 - 전체 로직 보고서

> **버전**: v3.2.0 (2026-04-06)
> **프레임워크**: C# .NET 9 WPF + ML.NET + Binance.Net
> **거래소**: Binance USDT-M Futures (20x 레버리지)

---

## 1. 진입 전략 (Entry Strategies)

### 1.1 메이저 코인 전략 (MajorCoinStrategy)

| 항목 | 값 |
|------|-----|
| **대상 심볼** | BTCUSDT, ETHUSDT, SOLUSDT, XRPUSDT |
| **타임프레임** | 5분봉 (120봉 최소) |
| **주간 LONG 임계값** | AIScore >= 60 |
| **야간 LONG 임계값** | AIScore >= 65 |

**진입 조건 (3개 AND)**:
```
1. AIScore >= Threshold (0~100점 기반)
2. bullishStructure = 엘리엇 상승 OR (HigherLows AND 가격 > SMA20)
3. longConfirm = MACD Hist >= -0.001 AND (거래량 모멘텀 >= 1.02 OR 저볼륨 우회)
```

**AI Score 산출 (50점 기반)**:

| 조건 | 가점/감점 |
|------|-----------|
| 엘리엇 상승 추세 | +12 |
| SMA 정배열 (20>60, 60>120) | +10, +8 |
| RSI 45~68 | +10 |
| MACD 히스토그램 > 0 | +10 |
| Fib 0.382 이상 | +6 |
| BB 중단 이상 | +6 |
| 거래량 모멘텀 1.10x+ | +10 |
| Higher Lows 패턴 | +12 |
| **피보나치 황금 반등 (0.618~0.786)** | **+20** |

---

### 1.2 PUMP 스캔 전략 (PumpScanStrategy)

| 항목 | 값 |
|------|-----|
| **대상** | 전 USDT 종목 (메이저 4개 제외) |
| **후보 수** | Top 60 (혼합 점수 기준) |
| **주간 LONG 임계값** | AIScore >= 50 |
| **야간 LONG 임계값** | AIScore >= 45 |
| **최소 임계값** | 35 |

**혼합 점수 (후보 선정)**:
```
Score = Volume(25%) + Volatility(35%) + Momentum(40%)
```

**진입 조건 (완화됨)**:
```
ENTRY = (rulePass AND (structureOk OR momentumOk))
     OR (ML확률 >= 55%)
```

---

### 1.3 MACD 크로스 시스템 (MacdCrossSignalService)

#### 골든크로스 LONG 진입
| Case | 조건 | 설명 |
|------|------|------|
| **Case A** | 0선 아래 골크 + RSI < 40 | 과매도 반등 |
| **Case B** | 0선 위 골크 (RSI 무관) | 추세 가속, 숏 스퀴징 |

- **상위봉 확인**: 15분봉 + 1시간봉 SMA20 > SMA60 (정배열)
- **스캔 주기**: 30초

#### 데드크로스 SHORT 진입
| Case | 조건 | 설명 |
|------|------|------|
| **Case A** | 0선 근처/위 데드크로스 | 추세 추종 (가장 안전) |
| **Case B** | DeadCrossAngle < -0.00001 | 변곡점 포착 (하이리스크) |

- **상위봉 확인**: 15분봉 + 1시간봉 SMA20 < SMA60 (역배열)

---

### 1.4 15분봉 위꼬리 음봉 SHORT

```
[1] 15분봉 완성봉 감지
    ├─ 위꼬리 비율 >= 50%
    ├─ 음봉 (Close < Open)
    └─ 거래량 >= 10봉 평균 x1.5

[2] 상위봉 하락세 확인 (15m + 1H)

[3] 1분봉 리테스트 대기 (최대 5분)
    └─ 꼬리의 50%~61.8% 지점까지 반등 대기

[4] 트리거 조건 (하나 충족 시 SHORT)
    ├─ 1분봉 MACD 데드크로스
    ├─ RSI < 45 + 히스토그램 감소
    └─ 리테스트 후 하락 재개

[5] 손절 = 15분봉 고점 +0.1%
```

---

### 1.5 급등 실시간 감지 (Spike Detection)

| 항목 | 값 |
|------|-----|
| **임계값** | 5분 +3% |
| **최소 거래량** | $1M (QuoteVolume) |
| **대상 제외** | BTC/ETH/SOL/XRP (메이저 전략에서 관리) |
| **쿨다운** | 코인당 30분 |
| **특별 처리** | RSI 88+ 차단 면제, 20봉 최소 CandleData 폴백 |

---

### 1.6 시장 급변 감지 (CRASH/PUMP Reverse)

| 이벤트 | 조건 | 동작 |
|--------|------|------|
| **CRASH** | BTC/ETH/SOL/XRP 중 2개+ 동시 1분 -1.5% | LONG 전량 청산 → SHORT 리버스 (50%) |
| **PUMP** | BTC/ETH/SOL/XRP 중 2개+ 동시 1분 +1.5% | SHORT 전량 청산 → LONG 리버스 (50%) |

- **쿨다운**: 120초
- **텔레그램 즉시 알림**

---

## 2. 포지션 관리 (Position Management)

### 2.1 3단계 스마트 트레일링 (v3.2.0 재튜닝)

#### BTC

| 단계 | 트리거 ROE | 가격 변동 | 동작 |
|------|-----------|-----------|------|
| 1단계 본절 | 15% | 0.75% | 스탑 → 진입가+수수료 |
| 2단계 부분익절 | 30% | 1.5% | 40% 청산 + 스탑 ROE+5% |
| 3단계 트레일링 | 40% | 2.0% | 고점 대비 ROE 20% (가격 1%) 하락 시 청산 |

#### ETH / SOL / XRP

| 단계 | 트리거 ROE | 가격 변동 | 동작 |
|------|-----------|-----------|------|
| 1단계 본절 | 20% | 1.0% | 스탑 → 진입가+수수료 |
| 2단계 부분익절 | 40% | 2.0% | 40% 청산 + 스탑 ROE+5% |
| 3단계 트레일링 | 60% | 3.0% | 고점 대비 ROE 30% (가격 1.5%) 하락 시 청산 |

---

### 2.2 MACD 기반 익절/조임

**LONG 보유 시:**

| 신호 | 동작 |
|------|------|
| 1분봉 데드크로스 (ROE 8%+) | 즉시 전량 익절 |
| 히스토그램 PeakOut | 트레일링 스탑 → 현재가 -0.2% |

**SHORT 보유 시:**

| 신호 | 동작 |
|------|------|
| RSI <= 30 (ROE 10%+) | 50% 부분 익절 |
| 히스토그램 BottomOut | 트레일링 스탑 → 현재가 +0.2% |
| 1분봉 골든크로스 (ROE 5%+) | 전량 탈출 |

---

### 2.3 AI Exit Optimizer

```
포지션 보유 중 (ROE 10%+, 5초 간격)
    ↓
[1] MarketRegimeClassifier → TRENDING / SIDEWAYS / VOLATILE
    ↓
[2] ExitOptimizer(currentROE, highestROE, regime, BB, ADX, RSI, ...)
    ↓
    EXIT_NOW 70%+ → 즉시 익절
    HOLD → 기존 트레일링 유지
```

**학습 데이터**: KlineCache (시장 상태) + TradeHistory DB (익절 타이밍)

---

### 2.4 ATR 동적 트레일링 (HybridExitManager)

| ROE 구간 | RSI 구간 | ATR 배수 | 의미 |
|----------|----------|----------|------|
| < 10% | - | 1.5x | 넓은 간격 (숨구멍) |
| 10~20% | < 70 | 1.0x | 표준 추세 추적 |
| - | 70~80 | 0.5x | 과열 근접, 타이트 |
| - | 80+ | 0.2x | 극단 과열, 초밀착 |

---

## 3. 리스크 관리 (Risk Management)

### 3.1 구조 기반 손절 (v3.2.0)

```
기존: ATR x3.5 고정 → 더 가까운 쪽 선택 (노이즈에 걸림)
변경: 15분봉 구조선(스윙로우/하이 20봉) 우선 + ATR x5 최대 캡

LONG 손절 = Min(15분봉 20봉 최저가 -0.2%, 진입가 - ATR×5)
SHORT 손절 = Max(15분봉 20봉 최고가 +0.2%, 진입가 + ATR×5)
```

---

### 3.2 거래량 컨펌 필터

```
진입 시 Volume_Ratio < 0.50 (5봉 평균의 절반 미만)
→ 진입 차단 (거래량 없는 가짜 무빙 90% 차단)

예외: SPIKE_DETECT, CRASH_REVERSE, PUMP_REVERSE
```

---

### 3.3 분할 진입 (정찰대/본대)

```
신호 발생 → 정찰대 25% 진입
    ↓ 5초마다 ROE 체크 (최대 15분)
    
ROE +10% (2분 후) → 본대 75% 추가
ROE +5%  (3분 후) → 본대 50% 추가
ROE +2%  (5분 후) → 본대 30% 추가
ROE 음수          → 추가 안 함 (25%만 유지)
15분 경과         → 추가 포기
```

**효과**: 정찰대 손절 시 25%만 손실 (기존 100% 대비 75% 절감)

---

### 3.4 서킷 브레이커

| 조건 | 동작 |
|------|------|
| 일일 손실 한도 초과 | 신규 진입 차단 |
| 5연속 손실 | 신규 진입 차단 |
| 1시간 후 자동 해제 | 또는 수동 리셋 |

---

### 3.5 슬롯 제한

| 종류 | 최대 |
|------|------|
| 메이저 (BTC/ETH/SOL/XRP) | 3개 |
| PUMP (알트코인) | 2~5개 (동적) |
| 총합 | 5~8개 (동적) |

---

### 3.6 블랙리스트 / 쿨다운

| 트리거 | 기간 |
|--------|------|
| 외부 청산 감지 | 30분 |
| 심볼당 재진입 | 3분 쿨다운 |
| 청산 후 ACCOUNT_UPDATE | 30초 쿨다운 (팬텀 방지) |

---

## 4. AI/ML 모델

### 4.1 모델 목록

| 모델 | 유형 | 목적 | 최소 데이터 |
|------|------|------|-------------|
| **MLService** | LightGBM 이진 | 진입 타이밍 예측 | 200건 |
| **TradeSignalClassifier** | LightGBM 3분류 | LONG/SHORT/HOLD | 50건 |
| **PumpSignalClassifier** | LightGBM 이진 | PUMP 진입 여부 | 100건 |
| **MarketRegimeClassifier** | LightGBM 3분류 | TRENDING/SIDEWAYS/VOLATILE | 100건 |
| **ExitOptimizerService** | LightGBM 이진 | EXIT_NOW/HOLD | 50건 |
| **AI Double Check Gate** | 규칙+ML 복합 | 진입 최종 승인 | - |

### 4.2 ML 피처 (CandleData)

**기본 지표 (17개)**:
RSI, MACD, MACD_Signal, MACD_Hist, ATR, ADX, PlusDI, MinusDI,
BB_Width, Stoch_K, Stoch_D, SMA_20, SMA_60, SMA_120,
Fib_236, Fib_382, Fib_500, Fib_618, ElliottWaveState

**파생 피처**:
Price_Change_Pct, Price_To_BB_Mid, Price_To_SMA20_Pct,
Candle_Body_Ratio, Upper_Shadow_Ratio, Lower_Shadow_Ratio,
Volume_Ratio, Volume_Change_Pct, Fib_Position, Trend_Strength,
RSI_Divergence, SentimentScore

**v3.1.5+ 추가 피처**:
- `MACD_Hist_ChangeRate` — 히스토그램 기울기 (골크 1~2봉 전 예측)
- `MACD_DeadCrossAngle` — 데드크로스 각도 (음수→급하락 강도)
- `Is15mBearishTail` — 15분봉 위꼬리 음봉 여부
- `TrendAlignment` — 상위봉 추세 정렬 (1=하락, 0=중립, -1=상승)

---

## 5. 인프라

### 5.1 WebSocket 스트림

| 스트림 | 용도 | 갱신 주기 |
|--------|------|-----------|
| User Data Stream | 포지션/주문 실시간 감시 | 실시간 |
| All Ticker Stream | 전 종목 시세 (TickerCache) | 1초 |
| Kline Stream | 5분봉 캔들 (KlineCache) | 봉 마감 시 |

**ListenKey 워치독**: 25분마다 KeepAlive + 끊김 시 완전 재구독

---

### 5.2 주문 실행 (BinanceExchangeService)

**stepSize 폴백 테이블 (15종)**:

| 심볼 | stepSize | 심볼 | stepSize |
|------|----------|------|----------|
| BTCUSDT | 0.001 | DOGEUSDT | 1 |
| ETHUSDT | 0.001 | ADAUSDT | 1 |
| XRPUSDT | 0.1 | PEPEUSDT | 100 |
| SOLUSDT | 0.1 | SHIBUSDT | 1 |
| BNBUSDT | 0.01 | 기본 폴백 | 0.001 |

**부분청산 재시도**: 최대 3회 (매회 거래소 포지션 재확인 + 수량 보정)

---

### 5.3 텔레그램 알림

| 메시지 타입 | 설명 | 기본 |
|-------------|------|------|
| Alert | 시스템 경보/CRASH/PUMP | ON |
| Profit | 익절/손절/수익 보고 | ON |
| Entry | 진입/본절/트레일링 | ON |
| AiGate | AI 관제탑 요약 | ON |
| Log | 저중요 로그/하트비트 | ON |
| CloseError | 청산오류/부분청산 실패 | ON |

---

### 5.4 데이터베이스

| 테이블 | 용도 |
|--------|------|
| TradeHistory | 매매 기록 (진입/청산/PnL) |
| Order_Error | 주문 오류 기록 (재시도/해결 여부) |
| Bot_Log | AI 게이트 시그널 기록 |
| CandleData | ML 학습용 캔들 데이터 |

---

### 5.5 대시보드

| 필드 | 내용 |
|------|------|
| EQUITY | 현재 총 자산 (WalletBalance + 미실현PnL) |
| AVAILABLE | 가용 잔고 |
| TRANSFER PnL | 순 투입금 대비 수익률 (Transfer In - Out 기준) |
| ROI | 평균 ROE |
| SL/TP | 손절/익절 가격 + 트레일링스탑 가격 (TS:가격) |

---

## 6. 비활성화된 기능

| 기능 | 버전 | 사유 |
|------|------|------|
| H&S Pattern Panic Exit | v3.1.8 | 넥라인 이탈 전 오탐지 → 패닉 손절 반복 |
| 스퀴즈 방어 축소 (90분) | v3.1.9 | 손실 중 50% 강제 청산 → 손실만 확정 |
| PUMP 타임스탑 (15분/20분) | v3.0.16 | 횡보 시 자동 청산 → 수익 기회 손실 |
| 시간 기반 본절 전환 | v3.0.7 | 본절 청산이 수익 기회 제한 |

---

## 7. 최근 변경 이력

| 버전 | 날짜 | 주요 변경 |
|------|------|-----------|
| v3.2.0 | 04-06 | 구조 기반 손절 + 거래량 필터 + 분할 진입 |
| v3.1.9 | 04-06 | 트레일링 재튜닝 (ROE 40~60%, 간격 1~1.5%) |
| v3.1.7 | 04-06 | 15분봉 위꼬리 SHORT |
| v3.1.6 | 04-06 | MACD 데드크로스 SHORT + 숏 익절 |
| v3.1.5 | 04-06 | MACD 골든크로스 LONG + 히스토그램 피처 |
| v3.1.3 | 04-05 | PUMP 진입률 개선 (후보 60개, 조건 완화, 급등 감지) |
| v3.1.1 | 04-03 | AI 시장 상태 분류 + 최적 익절 모델 |
| v3.1.0 | 04-02 | 시장 급변 감지 (CRASH/PUMP 자동 청산 + 리버스) |
