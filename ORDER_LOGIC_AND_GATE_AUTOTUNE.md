# 주문 로직 점검 문서 (ExecuteAutoOrder 기준)

## 1) 개요
이 문서는 `TradingEngine.ExecuteAutoOrder(...)` 기준으로 실제 진입 파이프라인을 단계별로 정리하고,
최근 추가된 **15분봉 Top-down 게이트 + 심볼별 임계값 자동튜닝** 동작을 설명합니다.

---

## 2) 주문 실행 전체 흐름

### 2.1 진입 전 가드 (초기 차단)
1. **진입 방향 검증**: `LONG/SHORT`가 아니면 즉시 종료
2. **패턴 보류 조건**: `patternSnapshot.Match.ShouldDeferEntry`이면 차단
3. **워밍업 가드**: 엔진 시작 직후 워밍업 구간이면 차단
4. **서킷브레이커 가드**: `RiskManager` 트립 상태면 차단
5. **최신 캔들 데이터 확보 실패** 시 차단

### 2.2 하이브리드/추세/변동성 검증
6. **캔들 확인 지연 진입**(Hybrid 신호): Fakeout 방지용 다음 캔들 확인
7. **15분 추세 필터**: SMA20/SMA60 추세 역행 차단
8. **ATR 변동성 필터**: 과열 구간(ATR 비율 과다) 차단
9. **하이브리드 BB 필터**: %B/캔들 조건으로 과추격 진입 차단
10. **MAJOR ATR 기반 손절 미리 산출**: 실패 시 차단
11. **추격 방지 필터**: 급등/급락 추격 진입 차단

### 2.3 15분봉 구조 게이트 (신규 강화)
12. **Top-down 바이어스**: 1h/4h SMA50/200 기반 방향성 판정
13. **구조 시나리오 검증**
   - 추세 연장: 2파 되돌림 (0.5~0.618) + 트리거 조건
   - 반전: 5파 종료 규칙 + RSI/거래량 다이버전스
14. **ML.NET 방향 합의 + 임계값 통과**
15. **Transformer 방향 합의 + 임계값 통과**
16. 통과 시 TP/SL 후보를 주문 파이프라인으로 전달, 실패 시 차단

### 2.4 AI/RL/손익비 검증
17. 기존 ML 보너스/필터(메이저 특권 포함) 검증
18. RL 액션 반대일 경우 진입 차단
19. 공통 손익비(RR) 필터 검증 (SIDEWAYS 제외)
20. ElliottWave 전용 손익비 보조 검증

### 2.5 주문 제출 및 체결 후 처리
21. 중복 진입 방지용 포지션 예약
22. 레버리지 설정
23. 수량 계산 + 거래소 StepSize 보정
24. 슬리피지 검증
25. **지정가 주문 제출** → 3초 대기 → 미체결 취소 / 부분체결 처리
26. 체결 시 포지션 확정, DB 진입 로그 저장
27. 표준 모니터 + FastEntrySlippage 모니터 시작

---

## 3) 15M 게이트 임계값 자동튜닝 (심볼별)

## 3.1 기본값
- ML.NET 임계값 기본: **55%**
- Transformer 임계값 기본: **52%**
- 심볼별로 개별 관리 (`BTCUSDT`, `ETHUSDT` 등)

## 3.2 튜닝 입력 데이터
- 입력은 `15M_GATE`의 **PASS/BLOCK 결과 로그**
- 심볼별로 샘플 누적 후 자동조정

## 3.3 튜닝 규칙
- 샘플 윈도우: **24건/심볼**
- `passRate >= 62%` → 임계값 상향(더 엄격)
- `passRate <= 20%` → 임계값 하향(덜 엄격)
- 조정 스텝: **1%p**
- 하한/상한:
  - ML: 48% ~ 72%
  - Transformer: 47% ~ 70%

## 3.4 로그 포맷
- 자동튜닝 로그: `[GATE][AUTO_TUNE] ... mlThr=old->new tfThr=old->new`
- 게이트 통과 로그: `[ENTRY][15M_GATE][PASS] ... mlTh=.. tfTh=..`
- 게이트 차단 로그: `[ENTRY][15M_GATE][BLOCK] ...`

---

## 4) 메인 UI 재정리 반영 내용

대시보드 기준으로 다음 항목을 추가/정리했습니다.

1. **ENTRY GATE STATUS 카드**
   - PASS/Block 누적 카운트
   - 현재 임계값 요약(ML/TF)
   - 최근 AUTO_TUNE 상태

2. **라이브 로그 패널 3분할**
   - MAJOR / PUMP / GATE
   - 게이트 관련 로그를 전용 컬럼으로 분리하여 원인 파악 속도 개선

---

## 5) 운영 체크리스트

1. 게이트 차단이 과도하면
   - GATE 컬럼에서 `BLOCK` 사유와 임계값 확인
   - AUTO_TUNE 로그가 `HOLD`/`ADJUST` 중 무엇인지 확인

2. 진입이 너무 느슨하면
   - `passRate`가 높은지 확인
   - 임계값이 상한 근처로 점진 상승하는지 확인

3. 체결 품질 확인
   - `ORDER SUBMIT/PENDING/FILLED/CANCEL` 로그 순서 확인
   - 미체결 취소 비율이 급증하면 슬리피지/호가 조건 재점검

---

## 6) 참고 메서드
- `TradingEngine.ExecuteAutoOrder(...)`
- `TradingEngine.EvaluateFifteenMinuteWaveGateAsync(...)`
- `TradingEngine.RecordGateDecisionAndAutoTune(...)`
- `TradingEngine.GetSymbolGateThresholds(...)`
- `MainViewModel.AddLiveLog(...)`
- `MainViewModel.FlushPendingLiveLogsToUi(...)`
