# 진입 로직 전체 보고서 (v3.5.3)

> 작성일: 2026-04-09 | 전체 진입 경로 16개, 차단 조건 22개, 필터 갭 5개 식별

---

## 1. 진입 경로 전체 맵

### 1.1 경로별 필터 적용 현황

| # | 경로 | signalSource | AI Gate | BTC 필터 | D1+H4 방향 | 중복 체크 | R:R | 거래량 |
|---|------|-------------|---------|---------|-----------|---------|-----|--------|
| 1 | MajorCoinStrategy LONG | `MAJOR` | O | O | O | O | O | O |
| 2 | MajorCoinStrategy SHORT | `MAJOR` | O | - | O | O | O | O |
| 3 | PumpScanStrategy | `MAJOR_MEME` | O | O | **X**(PUMP) | O | O | O |
| 4 | **SPIKE_FAST** | `SPIKE_FAST` | **X** | **X** | **X** | 부분 | **X** | **X** |
| 5 | CRASH_REVERSE | `CRASH_REVERSE` | **X** | X(의도) | O | O | **X** | X(의도) |
| 6 | PUMP_REVERSE | `PUMP_REVERSE` | **X** | X(의도) | O | O | **X** | X(의도) |
| 7 | MACD 골든크로스 | `MACD_GOLDEN_CASE*` | O | O | O | O | O | O |
| 8 | MACD 데드크로스 | `MACD_DEAD_CASE*` | O | - | O | O | O | O |
| 9 | 15m 베어리시 테일 | `TAIL_RETEST_SHORT` | O | - | O | O | O | O |
| 10 | BB 스퀴즈 | `BB_SQUEEZE_15M` | O | O | O | O | O | O |
| 11 | 엘리엇 3파 | `ElliottWave3Wave` | O | O | O | O | O | O |
| 12 | Drought 2H | `DROUGHT_RECOVERY_2H` | **X**(사전검증) | O | O | O | O | O |
| 13 | Drought Near | `DROUGHT_RECOVERY_NEAR_2H` | **X**(사전검증) | O | O | O | O | O |
| 14 | Drought PUMP | `DROUGHT_RECOVERY_PUMP` | **X**(사전검증) | O | **X** | O | O | O |
| 15 | Timeout Prob | `TIMEOUT_PROB_*` | **X**(사전검증) | O | O | O | O | O |
| 16 | 수동 진입 | `MANUAL` | **X** | **X** | **X** | **X** | **X** | **X** |

---

## 2. ExecuteAutoOrder 차단 조건 (순서대로)

```
[1] 시그널 검증 ─── decision이 LONG/SHORT인지
[2] 블랙리스트 ──── 심볼이 블랙리스트에 있는지 (30분 차단)
[3] 쿨다운 ──────── 심볼별 진입 쿨다운 중인지
[4] 워밍업 ──────── 부팅 후 워밍업 기간인지
[5] 서킷브레이커 ── 리스크 매니저 서킷브레이커 발동 중
[6] BTC 하락장 ──── LONG && BTC 1시간 -2%+ (CRASH/PUMP_REVERSE 제외)
[7] 캔들 데이터 ─── latestCandle이 null인지
[8] 거래량 ──────── Volume_Ratio < 0.5 (SPIKE/CRASH/PUMP 제외)
[9] 중복 진입 ───── 이미 해당 심볼에 포지션/예약 존재
[10] 메이저 슬롯 ── majorCount >= 4
[11] PUMP 슬롯 ──── pumpCount >= 3
[12] 총 슬롯 ────── totalPositions >= 7
[13] AI Gate ─────── ML+TF 블렌드 스코어 미달 (CRASH/PUMP_REVERSE 우회)
[14] D1+H4 방향 ─── LONG && D1+H4 둘 다 데드크로스 (메이저만)
                     SHORT && D1+H4 둘 다 골든크로스 (메이저만)
[15] RSI 극단 ───── LONG && RSI >= 88, SHORT && RSI <= 12
[16] PUMP SHORT ─── PUMP 코인은 SHORT 불가
[17] SHORT RSI ──── SHORT && RSI <= 35 && 가격 > MA20
[18] ATR 손절 계산 ─ ATR 하이브리드 손절 계산 실패 (메이저)
[19] R:R 체크 ───── 리스크/리워드 비율 미달 (SPIKE/CRASH/PUMP 제외)
```

---

## 3. 진입 경로별 상세

### 3.1 MajorCoinStrategy (MAJOR)

**신호 생성**: MajorCoinStrategy.AnalyzeAsync()
- 5분봉 기반, 매 tick마다 분석
- **LONG 조건**: bullishSignals >= 3 && bullish > bearish
  - 가격 회복 +1.5% (30분): +2점
  - 강한 반등 +3% (1시간): +2점
  - 엘리엇 상승추세: +1점
  - Higher Lows: +1점
  - 가격 > SMA20: +1점
  - MACD 히스토그램 > 0: +1점
- **SHORT 조건**: bearishSignals >= 3 && bearish > bullish
  - 동일 구조 (반대 방향)

**문제점**: 개별 코인 기술지표만 분석, 시장 전체 방향 인식 없음

---

### 3.2 PumpScanStrategy (MAJOR_MEME)

**신호 생성**: PumpScanStrategy.ExecuteScanAsync() (10초 간격)
- Top 60 후보 (거래량 25% + 변동성 35% + 모멘텀 40%)
- **LONG 조건**: hasPriceMomentum && (bullishSignals >= 4 OR ML >= 60%)
  - 가격 모멘텀: +1.5% (30분) 또는 +3% (1시간 반등)
  - 불리시 시그널 4개+ 또는 ML 모델 60%+

**문제점**: D1+H4 방향 필터가 PUMP 코인에 미적용

---

### 3.3 SPIKE_FAST (즉시 진입)

**완전히 ExecuteAutoOrder를 우회하는 유일한 자동 경로**

```
감지: 30초간 +3% 변동 + $1M 거래량 + 2x 거래량 비율
 ↓
슬롯 체크 + 포지션 예약 (lock 내부)
 ↓
RSI >= 80 체크 (LONG만) → 차단
 ↓
+5% 추가 상승 체크 → 차단 (타이밍 만료)
 ↓
60초 풀백 대기 (-1% 하락 시 진입)
 ↓
시장가 주문 직접 실행
```

**미적용 필터**: AI Gate, BTC 하락장, D1+H4 방향, R:R 체크, 거래량 비율

---

### 3.4 CRASH_REVERSE / PUMP_REVERSE

**트리거**: MarketCrashDetector — BTC/ETH/SOL/XRP 중 2개+ 동시 ±1.5% (1분)
- CRASH 감지 → 보유 LONG 전량 청산 + SHORT 리버스
- PUMP 감지 → 보유 SHORT 전량 청산 + LONG 리버스
- **AI Gate 우회** (의도적: 긴급 대응)
- **R:R 체크 우회**
- sizeMultiplier: 50% (CrashReverseSizeRatio)

---

### 3.5 MACD 골든/데드크로스

**스캔**: 30초 간격, 메이저 코인만
- **골든크로스 LONG**: Case B(즉시) 또는 Case A(RSI < 40)
  - 상위 TF 불리시 확인 필수 (15m + 1H SMA 정렬)
- **데드크로스 SHORT**: Case A(즉시) 또는 Case B(각도 < -0.00001)
  - 상위 TF 베어리시 확인 필수

---

### 3.6 15분 베어리시 테일 (SHORT)

**스캔**: 1분 간격, 메이저 코인만
- 15분봉 윗꼬리 >= 50% + 거래량 >= 1.5x
- 15분 MACD 베어리시
- 1분봉 리테스트 대기 (50%~61.8% 구간, 최대 5분)
- 손절: 캔들 고점 + 0.1%

---

### 3.7 Drought Recovery (가뭄 진입)

**트리거**: 30분+ 미진입 시
- **Tier 1**: AI Gate 통과 + 2시간 내 예측 → 100% 진입
- **Tier 2**: AI Gate 90% 기준 → 70% 진입
- **Tier 3**: PUMP 스캔 폴백 → 70% 진입
- 사전검증 후 `skipAiGateCheck=true` (v3.4.1: 일반 우회는 제거)

---

## 4. 메이저 SHORT 진입 필터 현황

### 4.1 현재 차단 조건

| 조건 | SHORT 차단 여부 | 비고 |
|------|---------------|------|
| RSI <= 35 && 가격 > MA20 | **차단** | 유일한 하드 블록 |
| D1+H4 둘 다 골든크로스 | **차단** (v3.5.0) | dirBias >= 1.5 |
| RSI <= 12 | **차단** | 극단 RSI |
| PUMP 코인 | **차단** | SHORT 불가 |

### 4.2 미적용 (차단 안 함)

| 조건 | 현재 | 권장 |
|------|------|------|
| MACD 골든크로스 활성 | 참고 로그만 | **차단 추가 필요** |
| 피보나치 38.2~61.8% 진입구간 | 미체크 | **차단 추가 필요** |
| AI 상승 예측 | 참고 로그만 | 경고 유지 |
| MACD > 0 | 참고 로그만 | 경고 유지 |
| Stochastic 과매도 | 미체크 | **차단 추가 고려** |

---

## 5. 진입 사이징 로직

### 5.1 AI Gate 블렌드 스코어 기반

```
blendedScore >= 80%  → 100% 본진입
blendedScore 70~80% → 25% 정찰대 (ROE 확인 후 본대)
blendedScore < 70%  → AI Gate에서 차단됨 (도달 불가)
```

### 5.2 BB 센터 서포트 가중치

```
BB 센터 서포트 O → ML 30% + TF 70% (TF 가중)
BB 센터 서포트 X → ML 50% + TF 50% (균등)
```

### 5.3 마진 계산

| 구분 | 메이저 | PUMP |
|------|--------|------|
| 마진 | Equity × MajorMarginPercent% | 고정 $100~150 |
| 레버리지 | MajorLeverage (20x) | PumpLeverage (20x) |
| 회복 모드 | × 0.6 (60%) | - |
| 정찰대 | × 0.25 (25%) | × 0.25 |

---

## 6. 포지션 모니터링

### 6.1 메이저 (MonitorPositionStandard)

```
본절 이동:     ROE +20% (BTC +15%, ETH/SOL/XRP +20%)
1차 부분익절:  ROE +40% (BTC +30%) → 40% 청산
트레일링 시작: ROE +50% (BTC +40%, ETH/SOL/XRP +60%)
트레일링 갭:   ROE 30% (BTC 20%, ETH/SOL/XRP 30%)
손절 ROE:      -50% (회복 모드 -80%)
```

### 6.2 PUMP (MonitorPumpPositionShortTerm)

```
스파이크 익절:  ROE 15%+ 급등 → 즉시 20% 청산
1차 부분익절:   ROE +25% → 15% 청산
계단식 1단계:   ROE +25% → 바닥 +10% 보호 + 서버 TRAILING_STOP
계단식 2단계:   ROE +80% → 바닥 +40% 보호 + 타이트 TRAILING
계단식 3단계:   ROE +160% → Moonshot 트레일링 (갭 30%)
트레일링 시작:  ROE +40% → 고점 대비 20% 하락 시 청산
본절 이동:      ROE +25% (1차 익절 후)
손절 ROE:       -40%
```

---

## 7. 식별된 문제점 및 개선 필요 항목

### 7.1 HIGH (즉시 수정 필요)

| # | 문제 | 현재 | 권장 |
|---|------|------|------|
| 1 | SHORT 진입 시 골든크로스 미체크 | 참고 로그만 | **MACD 골든크로스 활성 시 SHORT 차단** |
| 2 | SHORT 진입 시 피보나치 구간 미체크 | 미체크 | **38.2~61.8% 구간에서 SHORT 차단** |
| 3 | SPIKE_FAST 모든 필터 우회 | AI/BTC/D1H4 없음 | **최소 BTC 필터 + RSI 체크 추가** |

### 7.2 MEDIUM (개선 권장)

| # | 문제 | 현재 | 권장 |
|---|------|------|------|
| 4 | PUMP 코인에 D1+H4 방향 필터 미적용 | 메이저만 | BTC D1+H4로 대체 적용 |
| 5 | Drought Recovery AI Gate 우회 | skipAiGateCheck=true | 사전검증 강화 또는 AI Gate 필수 |
| 6 | MajorCoinStrategy 시장 전체 인식 없음 | 개별 코인만 | BTC 방향 참조 추가 |
| 7 | MACD 크로스 지연 (후행 지표) | 1분봉 MACD | 5분봉 + 볼린저 확인 추가 |

### 7.3 LOW (장기 개선)

| # | 문제 | 현재 | 권장 |
|---|------|------|------|
| 8 | 거래량 비율 0 허용 | Volume_Ratio=0 통과 | >= 0이면 차단 |
| 9 | D1+H4 일부 데이터만 있을 때 | 전체 스킵 | 가용 TF만으로 체크 |
| 10 | Scout add-on 추적 만료 없음 | 무기한 | TTL 추가 |

---

## 8. 최근 수정 이력 (v3.3.5 → v3.5.3)

| 버전 | 변경 | 영향 |
|------|------|------|
| v3.3.6 | 급변동 회복 전략 | 마진 60%, 손절 -80% |
| v3.3.7 | ExecuteAutoOrder 중복 체크 | 레이스 컨디션 차단 |
| v3.3.8 | 계단식 보호선 우선 처리 | 본절 전에 보호선 체크 |
| v3.3.9 | TRAILING_STOP_MARKET API | 서버사이드 트레일링 |
| v3.4.0 | 물타기 비활성화 + HybridExit ATR 제거 | -$2,023 손실원 제거 |
| v3.4.1 | 부분청산 무한루프 + 팬텀 기록 방지 | stepSize 보정 + 쿨다운 |
| v3.4.2 | BTC 하락장 필터 + SPIKE 거래량 + DROUGHT 우회 제거 | 하락장 방어 |
| v3.5.0 | AI 피처 확장 (Stochastic/MACD Cross/ADX) + D1+H4 필터 | 방향성 정렬 |
| v3.5.2 | 중복 진입 완전 차단 | scoutAddOnEligible 제거 |
| v3.5.3 | 포지션 상태 DB 영속화 | 재시작 시 복원 |
