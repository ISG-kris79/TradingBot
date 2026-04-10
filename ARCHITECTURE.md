# CoinFF TradingBot - 시스템 아키텍처 문서

> v3.0.8 기준 | 최종 업데이트: 2026-04-01

---

## 1. 진입 파이프라인 (Entry Pipeline)

### 1.1 ExecuteAutoOrder (라우터)

신호 발생 → 공통 검증 → 4개 진입 메서드 분기.

**공통 검증 단계:**

| 순서 | 검증 | 차단? | 설명 |
|------|------|-------|------|
| 1 | Signal Validation | O | LONG/SHORT만 허용 |
| 2 | Data Fetch | - | 5분봉 140개, 15분봉 80개, H&S 패턴 감지 |
| 3 | AI Gate 평가 | X | blended score 기반 사이즈 조절 (차단 없음) |
| 4 | 3분류 ML 모델 | X | 롱/숏/관망 예측 → 사이즈 부스트 or 축소 |
| 5 | Blacklist | O | 청산 후 30분 재진입 방지 |
| 6 | Slot Cooldown | O | 거부된 심볼 3분 쿨다운 |
| 7 | Entry Warmup | O | 봇 시작 후 30초 워밍업 |
| 8 | Circuit Breaker | O | 일일 손실 한도 초과 시 전체 차단 |
| 9 | Slot Limit | - | 메이저 4개 / PUMP 2개 / 총 6개, 초과 시 30% 정찰대 |
| 10 | RSI Extreme | O | RSI >= 88 (롱) 또는 <= 12 (숏) |
| 11 | ATR Volatility | X | 변동성 폭발 시 사이즈 축소 (차단 없음) |

**사이즈 결정:** 각 Advisor의 최저값 1개만 선택, **최소 10% 하한**.

**분기:**
```
MajorSymbols (BTC/ETH/SOL/XRP) + LONG  → ExecuteMajorLongEntry()
MajorSymbols + SHORT                    → ExecuteMajorShortEntry()
나머지 + LONG                           → ExecutePumpLongEntry()
나머지 + SHORT                          → ExecutePumpShortEntry()
```

### 1.2 ExecuteMajorLongEntry

| 항목 | 값 |
|------|-----|
| AI 보너스 | EMA 리테스트 +10, 숏 스퀴즈 +15, 저거래량 특권 +10 |
| SL | ATR 2.0x 하이브리드 (BTC: -15%, ETH/SOL/XRP: -20%) |
| 사이즈 | 자산 × MajorMarginPercent (기본 10%) |
| R:R 최소 | 1.40x |

### 1.3 ExecuteMajorShortEntry

| 항목 | 값 |
|------|-----|
| AI 보너스 | 없음 |
| 차단 조건 | RSI <= 35 AND 가격 > MA20 (추세 없는 역매도) **만** 차단 |
| SL | ATR 2.0x 하이브리드 |
| 사이즈 | 자산 × MajorMarginPercent |
| R:R 최소 | 1.40x |

### 1.4 ExecutePumpLongEntry

| 항목 | 값 |
|------|-----|
| 사이즈 | **고정 $150** |
| 레버리지 | 20x |
| R:R 최소 | 1.40x |

### 1.5 ExecutePumpShortEntry

| 항목 | 값 |
|------|-----|
| 차단 조건 | RSI <= 35 AND 가격 > MA20 **만** 차단 |
| 사이즈 | **고정 $150** |
| 레버리지 | 20x |
| R:R 최소 | 1.40x |

### 1.6 PlaceAndTrackEntryAsync (공통 주문 실행)

1. 중복 포지션 확인 + 슬롯 최종 검증
2. 거래소 레버리지 설정
3. ProfitRegressor 사이즈 조정 (선택)
4. 수량 계산 + StepSize 보정
5. **시장가 주문 실행**
6. 포지션 정보 저장 (EntryPrice, Leverage, AiScore 등)
7. 텔레그램 진입 알림
8. DB TradeHistory 저장
9. 패턴 스냅샷 저장
10. 모니터링 루프 시작 (Major → Standard, Pump → ShortTerm)

---

## 2. 신호 소스 (Signal Sources)

| 소스 | 파일 | 대상 | 설명 |
|------|------|------|------|
| MAJOR | MajorCoinStrategy.cs | BTC/ETH/SOL/XRP | BB+RSI+MACD 복합 분석 |
| PUMP | PumpScanStrategy.cs | 알트코인 전체 | 급등주 실시간 스캔 (상위 20개) |
| ELLIOTT_WAVE | ElliottWave3WaveStrategy.cs | 전체 | 3파 확정 패턴 감지 |
| 15M_SQUEEZE | BB Squeeze Strategy | 전체 | 15분 볼린저 스퀴즈 돌파 |
| DROUGHT_RECOVERY | TimeOutProbabilityEngine | 전체 | 60분+ 진입 공백 시 확률 베팅 |

---

## 3. 포지션 모니터링 (Position Monitoring)

### 3.1 MonitorPositionStandard (메이저 코인)

**3단계 스마트 방어 시스템:**

| 단계 | 트리거 ROE | 동작 | BTC | ETH/SOL/XRP |
|------|-----------|------|-----|-------------|
| 1단계 | +7% | breakEvenActivated 플래그 설정 | +7% | +7% |
| 2단계 (TP1) | +20% | **40% 부분청산** + 방어 스탑 +5% | +20% | +30% |
| 3단계 (트레일링) | +35~40% | 최고 ROE 추적, 갭만큼 하락 시 전량 청산 | Gap 5% | Gap 6% |

**추가 기능:**
- **불타기 (Pyramiding):** +50% ROE부터 최대 3회 추가 진입
- **AI Sniper Exit:** ML 기반 구조적 손절 감지
- **H&S 패턴 감지:** Head & Shoulders → 즉시 전량 청산
- **손절:** BTC -15%, ETH/SOL/XRP -20%

### 3.2 MonitorPumpPositionShortTerm (PUMP 코인)

| 단계 | 트리거 ROE | 동작 |
|------|-----------|------|
| TP1 | +20% | 15~30% 부분청산 |
| TP2 | +40% | 50% 부분청산 + 트레일링 시작 |
| 트레일링 | 최고 ROE - 20% | 전량 청산 |
| 손절 | -40% | 전량 청산 |
| 시간 초과 | 120분 | 전량 청산 |
| 본절 | +25% | 스탑을 진입가로 이동 |

---

## 4. AI/ML 파이프라인

### 4.1 모델 구성

| 모델 | 라이브러리 | 유형 | 역할 |
|------|-----------|------|------|
| **AIPredictor** | ML.NET (LightGBM) | 이진 분류 | 상승/하락 예측 → AI 스코어 계산 |
| **TradeSignalClassifier** | ML.NET (LightGBM MultiClass) | 3분류 | 롱(1)/숏(2)/관망(0) 예측 |
| **EntryTimingMLTrainer** | ML.NET (LightGBM) | 이진 분류 | 49개 멀티타임프레임 피처 → 진입 타이밍 |
| **ProfitRegressorService** | ML.NET (FastTree Regression) | 회귀 | 진입 시 5~15분 후 예상 수익률 |
| **PatternMemoryService** | 커스텀 (유클리드/코사인 유사도) | 패턴 매칭 | 과거 유사 패턴 기반 승률 추정 |

### 4.2 TradeSignalClassifier (3분류 매매 신호)

```
DB CandleData (5000건/심볼, 상위 10개 심볼)
  → 라벨링: 30분 후 가격변동률 ±1% 기준
      +1% 이상 → 롱(1)
      -1% 이상 → 숏(2)
      그 외    → 관망(0)
  → LightGBM MultiClass 학습 (24개 피처)
  → trade_signal_model.zip 저장
```

**24개 피처:**
RSI, MACD, MACD_Signal, MACD_Hist, ATR, ADX, PlusDI, MinusDI,
BB_Width, Price_To_BB_Mid, Price_To_SMA20_Pct, Price_Change_Pct,
Candle_Body_Ratio, Upper_Shadow_Ratio, Lower_Shadow_Ratio,
Volume_Ratio, Volume_Change_Pct, OI_Change_Pct,
Trend_Strength, Fib_Position, Stoch_K, Stoch_D, RSI_Divergence, SentimentScore

**진입 연동:**
- 같은 방향 + 신뢰도 70%+ → 사이즈 **1.3배 부스트**
- 반대 방향 + 신뢰도 60%+ → 사이즈 **30%로 축소**

### 4.3 AIDoubleCheckEntryGate

```
실시간 캔들 데이터
  → MultiTimeframeFeatureExtractor (1m/5m/15m/1h)
  → EntryTimingMLTrainer.Predict()
  → blended ML confidence
  → 사이즈 조절 (차단 없음):
      90%+ → 100%, 80%+ → 50%, 70%+ → 30%, 50%+ → 20%, <50% → 10%
```

**온라인 학습:** 100~1000건마다 재학습, Concept Drift 감지, 1시간 주기 재학습

### 4.4 학습 데이터 수집 흐름

```
진입 실행 → PositionInfo 저장
  → 모니터링 → 청산
  → OnPositionClosedForAiLabel 이벤트
  → HandleAiCloseLabelingAsync()
      ├─ ProfitRegressorService.RecordTradeOutcome()
      ├─ AIDoubleCheckEntryGate EntryDecisionRecord → JSON
      └─ PatternMemoryService → TradePatternSnapshots DB
  → 50건 이상 축적 시 TrainAsync() 자동 학습
```

---

## 5. 리스크 관리

### 5.1 RiskManager

| 항목 | 값 |
|------|-----|
| 일일 최대 손실 | 초기 잔고 × 10% |
| 연속 손실 제한 | 5연패 시 서킷 브레이커 |
| 서킷 브레이커 해제 | 1시간 후 자동 |
| 해제 시 동작 | 전체 포지션 강제 청산 |

### 5.2 슬롯 관리

| 슬롯 | 최대 | 초과 시 |
|------|------|---------|
| 메이저 | 4개 | 30% 정찰대 진입 |
| PUMP | 2개 | 30% 정찰대 진입 |
| 총합 | 6개 | 30% 정찰대 진입 |

### 5.3 블랙리스트

- 청산 후 30분간 재진입 금지
- `_blacklistedSymbols` (ConcurrentDictionary)

---

## 6. 보조지표 (IndicatorCalculator)

Skender.Stock.Indicators 2.7.1 기반:

| 지표 | 파라미터 | 용도 |
|------|---------|------|
| RSI | 14 | 과매수/과매도 |
| Bollinger Bands | 20, 2.0σ | 변동성, 밴드 위치 |
| ATR | 14 | 변동성, SL 계산 |
| ADX | 14 | 추세 강도 (+DI/-DI) |
| MACD | 12,26,9 | 추세 방향/모멘텀 |
| Stochastic | 14,3,3 | 단기 과매수/과매도 |
| SMA | 20, 60, 120 | 추세, 정배열 |
| EMA | 12, 26 | MACD 구성 |
| Fibonacci | 0.236~0.618 | 지지/저항 |

---

## 7. 텔레그램 알림

| 이벤트 | 전송 | 메서드 |
|--------|------|--------|
| 진입 성공 | O | SendEntrySuccessAlertAsync |
| 전량 청산 (모든 사유) | O | NotifyProfitAsync |
| 부분 청산 (TP1/TP2) | O | SendMessageAsync |
| 부분청산→전량 체결 | O | NotifyProfitAsync |
| 브레이크이븐 | O | SendBreakEvenReachedAsync |
| 트레일링 마일스톤 | O | SendTrailingMilestoneAsync |
| 서킷 브레이커 | O | NotifyAsync |
| API 오류/복구 | O | SendMessageAsync |
| 하트비트 (1시간) | O | NotifyAsync |
| 일일 목표 달성 | O | NotifyAsync |

---

## 8. 데이터 흐름

### 8.1 실시간 데이터

```
Binance WebSocket
  → BinanceSocketConnector
  → MarketDataManager.TickerCache (가격)
  → MarketDataManager.KlineCache (캔들)
  → MockExchangeService.SetCurrentPrice() (시뮬레이션)
  → MajorCoinStrategy / PumpScanStrategy (신호 분석)
  → ExecuteAutoOrder (진입 실행)
```

### 8.2 DB 테이블

| 테이블 | 용도 |
|--------|------|
| TradeHistory | 매매 이력 (진입/청산/PnL/IsSimulation) |
| CandleData | 과거 캔들 + 보조지표 (학습용) |
| TradePatternSnapshots | 패턴 스냅샷 (진입 시 지표, 청산 시 라벨) |
| Users | 사용자 인증 + API 키 |

### 8.3 모델 파일

| 파일 | 경로 | 용도 |
|------|------|------|
| scalping_model.zip | 실행 디렉토리 | AIPredictor 이진 분류 |
| EntryTimingModel.zip | %LocalAppData%/TradingBot/Models/ | 진입 타이밍 이진 분류 |
| trade_signal_model.zip | %LocalAppData%/TradingBot/Models/ | 3분류 매매 신호 |

---

## 9. 설정 (Configuration)

### 9.1 메이저 코인 설정

| 설정 | 기본값 | BTC | ETH/SOL/XRP |
|------|--------|-----|-------------|
| Leverage | 20x | 20x | 20x |
| MarginPercent | 10% | 10% | 10% |
| TP1 ROE | 20% | 20% | 30% |
| TP2 ROE / 트레일링 시작 | 40% | 35% | 50% |
| Trailing Gap | 5% | 5% | 6% |
| Stop Loss ROE | -20% | -15% | -20% |
| BreakEven ROE | 7% | 7% | 7% |

### 9.2 PUMP 코인 설정

| 설정 | 기본값 |
|------|--------|
| Leverage | 20x |
| Margin | $150 고정 |
| TP1 ROE | 20% |
| TP2 ROE | 40% |
| Trailing Gap | 20% |
| Stop Loss ROE | -40% |
| BreakEven ROE | 25% |
| 시간 제한 | 120분 |

### 9.3 시뮬레이션 모드

| 항목 | 동작 |
|------|------|
| 거래소 | MockExchangeService (가상) 또는 바이낸스 테스트넷 |
| 초기 잔고 | SimulationInitialBalance (기본 $10,000) |
| DB 저장 | IsSimulation=1로 구분 저장 |
| 학습 데이터 | 시뮬레이션 거래도 AI 학습에 활용 |
| 가격 데이터 | 실제 바이낸스 WebSocket 사용 |

---

## 10. 파일 구조

```
TradingBot/
├── TradingEngine.cs           # 메인 엔진 + 진입 라우터 + 4개 진입 메서드
├── PositionMonitorService.cs  # 포지션 모니터링 + TP/SL + 부분청산
├── AIDoubleCheckEntryGate.cs  # AI 더블체크 게이트 (ML.NET 단독)
├── AIPredictor.cs             # 이진 분류 예측기
├── EntryTimingMLTrainer.cs    # 진입 타이밍 ML 학습기
├── AdaptiveOnlineLearningService.cs  # 온라인 학습
├── PatternMemoryService.cs    # 패턴 매칭
├── IndicatorCalculator.cs     # 보조지표 계산 (Skender)
├── DbManager.cs               # DB CRUD
├── DatabaseService.cs         # DB 초기화 + 학습 데이터
├── Models.cs                  # CandleData, PositionInfo 등
├── AppConfig.cs               # 설정 관리
├── Services/
│   ├── TradeSignalClassifier.cs      # 3분류 매매 신호 모델
│   ├── ProfitRegressorService.cs     # 수익률 회귀 예측
│   ├── MockExchangeService.cs        # 시뮬레이션 거래소
│   ├── BinanceExchangeService.cs     # 실거래 거래소
│   ├── NotificationService.cs        # 알림 통합
│   ├── TelegramService.cs            # 텔레그램 전송
│   └── RiskManager.cs                # 리스크 관리
└── Strategies/
    ├── MajorCoinStrategy.cs          # 메이저 코인 신호
    ├── PumpScanStrategy.cs           # 급등주 스캔
    └── ElliottWave3WaveStrategy.cs   # 엘리엇 파동
```
