# Changelog

이 프로젝트의 모든 주요 변경 사항은 이 파일에 문서화됩니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

### Added

 - 없음

### Changed

 - 없음

## [3.2.20] - 2026-04-08

### Fixed

- **PUMP 아무거나 진입 방지**:
  - Spike 임계값: 1.5% → **+3%** (진짜 급등만)
  - PUMP 코인: **급등(+3%)만** 진입, 급락 SHORT 제거 (메이저만 급락 대응)
  - 거래량 최소: $500K → **$1M** 복원
  - PumpScan: 모멘텀 3개 → **4개+ 필수** + 가격 모멘텀(30분 +1.5% 또는 1시간 저점 +3%) 반드시 포함
  - ML 단독 진입: 55% → **60%** + 가격 모멘텀 필수

## [3.2.19] - 2026-04-08

### Fixed

- **DataGrid 사라짐 수정**: FAST LOG가 Grid Column 밖에 삽입돼 DataGrid 밀림 → 좌측 패널 Grid.Row="3"으로 올바르게 배치
- **PUMP 중복 진입 방지**: SPIKE_FAST에서 슬롯 체크 + 포지션 예약을 단일 lock 블록으로 (레이스 컨디션 제거)
- **PUMP 슬롯**: 2개 → 3개

## [3.2.18] - 2026-04-08

### Fixed

- **설정 저장 오류**: 제거된 PUMP 검증 블록(`return false`)이 남아서 저장 시 무조건 실패하던 문제 수정

## [3.2.17] - 2026-04-08

### Changed

- **급등/급락 즉시 주문 (SPIKE_FAST)**: ExecuteAutoOrder 완전 바이패스
  - 이전: Spike 감지 → GetLatestCandleData(API) → AI Gate → R:R → ATR 손절 → 주문 = **4분 지연**
  - 변경: Spike 감지 → 슬롯 체크 → **즉시 시장가 주문** → 포지션 등록 → 감시 시작 = **1초 이내**
  - API 5개 호출 체인 제거, 텔레그램은 비동기 (주문 안 기다림)

## [3.2.16] - 2026-04-08

### Fixed

- **심볼 더블클릭 오류**: try-catch 추가로 차트 창 열기 안정화
- **급등 진입 지연**: Spike 감지 1분→**30초** 간격, 임계값 2%→**1.5%**로 빠른 감지

## [3.2.15] - 2026-04-08

### Changed

- **설정창 간소화**: 기본 마진, 레버리지, 주요 심볼, 매매 모드(시뮬레이션)만 남김
  - PUMP/메이저/급변감지 설정 458줄 제거 → 소스 하드코딩
- **FAST LOG 좌측 맨 아래로 이동**

## [3.2.14] - 2026-04-08

### Fixed

- **급등/급락 감지 후 진입 실패 수정**: SPIKE_DETECT, CRASH_REVERSE, PUMP_REVERSE → AI Gate 바이패스 + R:R 체크 스킵
  - 급변 시 AI Gate/R:R에서 차단돼 진입 못 하던 문제 해결
- **텔레그램 대기 표시 가독성**: 쉼표 나열 → 줄바꿈, `✅진입됨` / `⏳대기` 코인별 한 줄씩

## [3.2.13] - 2026-04-08

### Changed

- **FAST LOG 좌측 이동 + 5줄 확장**: 우측→좌측 상단으로 이동, 3줄→5줄, 제목 "📡 FAST LOG (실시간)"
  - "라이브"는 실시간 엔진 로그 (진입/차단/익절/손절 등)

## [3.2.12] - 2026-04-08

### Fixed

- **PUMP 코인 메인창 표시**: Spike 감지 코인도 UI 리스트에 추가 + SignalSource "MAJOR"→"PUMP" 분리
- **피보나치 진입 범위 확장**: 0.618~0.786 → **0.382~0.786** 단계별 가점
  - 0.618~0.786: +20점, 0.500~0.618: +15점, 0.382~0.500: +10점
- **MACD 골크/데크 AI 피처 추가**: `MACD_GoldenCross` (1/0), `MACD_DeadCross` (1/0) — AI가 크로스 발생 여부 직접 학습

## [3.2.11] - 2026-04-08

### Changed

- **AI 관제탑 텔레그램 진입 상태 표시**: "승인 코인(사유)" → "AI 승인 코인 (진입 상태)"
  - 각 코인 옆에 `[✅진입됨]` 또는 `[⏳대기]` 표시
  - 예: `ORDERUSDT[⏳대기], BTCUSDT[✅진입됨]`

## [3.2.10] - 2026-04-08

### Fixed

- **PUMP 코인 진입 실패 근본 수정**: "게이트 통과" 됐는데 진입 안 된 원인
  - PumpScan AnalyzeSymbolAsync 최소 캔들 120봉→**30봉** 완화
  - ORDER 등 신규/소형 코인이 10시간치 캔들 부족으로 분석 자체가 스킵됐음
  - PUMP 슬롯 2개 유지 (변경 없음)

## [3.2.9] - 2026-04-08

### Fixed

- **CPU 과다 사용 최적화**:
  - PumpScan: 매 1초 → **10초 간격** (60개 병렬 API 호출 빈도 90% 절감)
  - 심볼 분석: 메이저 180ms → **1초**, 알트 1초 → **2초**
  - 15분봉 꼬리 리테스트: 메인 루프 블로킹 → **백그라운드 태스크 분리** (5분 블로킹 제거)

## [3.2.8] - 2026-04-08

### Fixed

- **PumpScanStrategy도 AI 최우선 구조로 전환**: ORDER 등 PUMP 코인 진입 안 되던 문제
  - 규칙 기반 점수(aiScore ≥ 50 + structureOk + momentumOk) → **모멘텀 신호 3개+ → AI 위임**
  - 가격 모멘텀 직접 감지: 30분 +1.5%, 1시간 저점 +3% 포함
  - ML 55%+ 단독 진입 유지
  - 메이저/PUMP 동일한 AI 최우선 구조로 통일

## [3.2.7] - 2026-04-08

### Changed

- **AI 최우선 진입 구조 전면 개편**:
  - 규칙 기반 점수(AIScore 60점 임계값) → **모멘텀 방향 판단 후 AI에 위임**
  - 모멘텀 신호 3개+ (상승 OR 하락) → LONG/SHORT → AI Gate가 최종 판단
  - 규칙 점수는 UI 참고용으로만 유지
- **Spike 감지 대폭 개선**:
  - 5분 간격 → **1분 간격**
  - 임계값 +3% → **±2%** (급락 SHORT도 감지)
  - **메이저 코인(SOL 등) 포함** (이전: 제외)
  - 거래량 최소 $1M → $500K, 쿨다운 30분 → 15분

## [3.2.6] - 2026-04-08

### Added

- **AI 학습 피처 5개 추가** (CandleData):
  - `HigherLows_Count` — 연속 저점 상승 횟수 (계단식 상승)
  - `LowerHighs_Count` — 연속 고점 하락 횟수 (계단식 하락)
  - `Price_Momentum_30m` — 30분 가격 변화율 %
  - `Bounce_From_Low_Pct` — 1시간 저점 대비 반등률 %
  - `Drop_From_High_Pct` — 1시간 고점 대비 하락률 %

### Changed

- **AI 최우선 진입**: 규칙 기반 WAIT이어도 모멘텀 반등/하락 감지 시 AI에 위임
  - 30분 +1.5% 반등 OR 1시간 저점 +3% → LONG으로 AI에 전달
  - 30분 -1.5% 하락 OR 1시간 고점 -3% → SHORT으로 AI에 전달
  - AI(TradeSignalClassifier + AIDoubleCheckGate)가 최종 판단

## [3.2.5] - 2026-04-08

### Added

- **계단식 하락(Lower Highs) 감지**: 3연속 고점 하락 패턴 → SHORT 시그널 강화 (+15점)
- **SHORT 별도 점수 체계**: LONG AIScore와 독립된 `shortBearishScore` (50점 기반)
  - LowerHighs +15, 30분 하락 -1.5% +15, 1시간 고점 -3% +10
  - 기존 else if → LONG 판단과 독립적으로 SHORT 판단

### Changed

- **SHORT 진입**: LONG 실패 후에만 판단 → WAIT 상태에서 별도 점수 60점 이상이면 독립 진입
- **하락 모멘텀 직접 감지**: 30분간 -1.5%, 1시간 고점 대비 -3% 실시간 감지

## [3.2.4] - 2026-04-08

### Fixed

- **폭락 후 반등 진입 실패 근본 수정**: SMA 지연으로 6% 반등해도 진입 못 하던 문제
  - **가격 모멘텀 직접 감지**: 30분간 +1.5% → AIScore +15, 1시간 저점 대비 +3% → +10
  - **bullishStructure 완화**: SMA 상승추세 OR HigherLows OR 가격 모멘텀 반등 (셋 중 하나)
  - **MACD 조건 완화**: 반등 중이면 Hist >= -0.01 허용 (기존 -0.001)
  - **AI Gate 메이저 임계값**: ML 75%→60%, TF 68%→55%
  - **R:R 최소 비율**: 1.40→1.20 (폭락 후 ATR 높아 R:R 미달 방지)

## [3.2.3] - 2026-04-08

### Fixed

- **AI Advisor 이중 축소 방지**: AI Gate 20% × 분할 진입 25% = 실질 5% 문제 해결
  - 변경: AI Gate 사이즈가 곧 최종 사이즈 (이중 곱셈 제거)
  - AI Gate 통과(100%) → 100% 진입, AI Advisor(20%) → 20% 진입 (기존 5%)

### Changed

- **24시간 동일 진입 기준**: 야간(19~10시) 임계값 상향 제거
  - 메이저: 주간 60 / 야간 65 → **24시간 60**
  - PUMP: 주간 50 / 야간 45 → **24시간 50**
  - SHORT: 주간 30 / 야간 25 → **24시간 30**

## [3.2.2] - 2026-04-07

### Fixed

- **CRASH/PUMP 독립 진입**: LONG 없어도 CRASH 시 메이저 코인 SHORT 독립 진입, SHORT 없어도 PUMP 시 LONG 독립 진입 (기존: 보유 포지션 청산 후에만 리버스)

### Changed

- **SHORT 진입 조건 완화**: MajorCoinStrategy 5개 AND → 핵심 3개 + 가격<SMA20 필수
  - 기존: !isUptrend AND MACD<0 AND price<SMA20 AND vol>=1.1 AND price<Fib618 (전부 충족)
  - 변경: price<SMA20 필수 + 나머지 4개 중 2개 추가 충족 (총 3개)
- **MACD 데드크로스 상위봉 확인 완화**: 15m AND 1H 하락 → 15m 하락이면 진입 허용 (급락 시 1H 전환 느림)

## [3.2.1] - 2026-04-07

### Removed

- **ATR Dual-Stop (WhipsawTimeout 3분) 비활성화**: ATR 터치 후 3분 타임아웃으로 정상 횡보에서 손절 발생 (BTC ROE -29.3%). 구조 기반 손절(v3.2.0)이 대체

## [3.2.0] - 2026-04-06

### Changed

- **구조 기반 손절 (Structure-Based SL)**: 고정 ROE 손절 → 15분봉 구조선(지지/저항) 우선
  - 스윙로우/스윙하이 20봉 기준, ATR x5 최대 캡
  - 구조선이 뚫리면 시나리오 파기 = 의미 있는 손절
- **거래량 컨펌 필터**: 5봉 평균 대비 거래량 50% 미만 → 진입 차단
  - 거래량 없는 가짜 무빙 90% 차단
- **분할 진입 정찰대/본대**: 모든 메이저 진입을 25% 정찰대 → 확인 후 본대 추가
  - ROE +10%: 75% 추가 (확실한 발산), ROE +5%: 50%, ROE +2%: 30%
  - 정찰대 손절 시 타격 최소화 (25% only)

## [3.1.9] - 2026-04-06

### Changed

- **20배 레버리지 트레일링 스탑 전면 재튜닝**: 노이즈에 안 털리는 넓은 간격
  - 1단계 본절: ROE 20% → 스탑을 진입가로 이동 (이전: 플래그만, 스탑 이동 없음)
  - 2단계 부분익절: ROE 20%→40% 상향 (가격 1%→2% 변동 후)
  - 3단계 트레일링: ROE 40%→50% 상향 + 간격 ROE 5%→30% (가격 0.25%→1.5%)
  - BTC: 본절15% / TP1=30% / 트레일링 40% / 간격 20%(가격1%)
  - ETH/XRP/SOL: 본절20% / TP1=40% / 트레일링 60% / 간격 30%(가격1.5%)

### Removed

- **스퀴즈 방어 축소 비활성화**: 90분 보유 + 손실 중 50% 강제 청산 → 손실만 확정시킴
- **H&S Pattern Panic Exit 비활성화**: 넥라인 이탈 전 오탐지로 패닉 손절 반복

## [3.1.8] - 2026-04-06

### Removed

- **H&S Pattern Panic Exit 비활성화**: 넥라인 이탈 전 1.5% 여유만으로 오탐지 → XRP -3~5%, ETH -8% 불필요한 패닉 손절 반복. ATR/MACD 기반 청산이 대체

## [3.1.7] - 2026-04-06

### Added

- **15분봉 위꼬리 음봉 SHORT 시스템**: 세력 물량 넘기기 패턴 포착
  - 15분봉 완성봉에서 UpperShadowRatio 50%+ 음봉 + 거래량 1.5x+ 감지
  - 상위봉(15m+1H) 하락세 확인 후 1분봉 리테스트 대기 (꼬리 0.5~0.618 지점)
  - 리테스트 구간에서 MACD 데크 or RSI 꺾임 → SHORT 진입
  - 손절 = 15분봉 고점 +0.1% (명확한 손절선)
- **ML 피처 추가**: `Is15mBearishTail` (15분봉 꼬리 음봉 여부), `TrendAlignment` (상위봉 추세 정렬)

## [3.1.6] - 2026-04-06

### Added

- **MACD 데드크로스 SHORT 진입**: 상위봉(15m+1H) 하락 확인 + 1분봉 데드크로스 → 자동 SHORT
  - Case A (추세 추종): 0선 근처/위 데드크로스 — 가장 안전한 숏 자리
  - Case B (변곡점): DeadCrossAngle 급하락 — 하이리스크 하이리턴
- **SHORT 익절 시스템**:
  - RSI ≤ 30 (과매도) → 50% 부분 익절
  - MACD 히스토그램 BottomOut (음수 막대 짧아짐) → 트레일링 스탑 바짝 조임
  - 1분봉 MACD 골든크로스 → 전량 탈출
- **ML 피처**: `MACD_DeadCrossAngle` 추가 — 음수일수록 급하락, AI 하락 강도 판단 근거

## [3.1.5] - 2026-04-06

### Added

- **MACD 골든크로스 진입 시스템 (MacdCrossSignalService)**:
  - 상위봉(15m+1H) 정배열 확인 + 1분봉 MACD 골든크로스 → 자동 진입
  - Case A (0선 아래): 과매도 반등 (RSI < 40)
  - Case B (0선 위): 추세 가속 (RSI 무시, 숏 스퀴징 포착)
  - 메이저 코인 30초 간격 스캔
- **MACD 데드크로스 트레일링**: 보유 중 1분봉 MACD 감시
  - 데드크로스 확정 (ROE 8%+) → 즉시 익절
  - 히스토그램 PeakOut → 트레일링 스탑 현재가 -0.2%로 조임
- **MACD 히스토그램 변화율 피처**: CandleData에 `MACD_Hist_ChangeRate` 추가 (골크 1~2봉 전 예측용)

## [3.1.4] - 2026-04-06

### Fixed

- **급등 감지 진입 실패 수정**: SPIKE_DETECT 경로에서 3가지 차단 해소
  - 5분봉 130봉 미달 → 20봉 이상이면 최소 CandleData 자동 생성으로 진입 진행
  - 5분봉 140봉 부족 → 30봉으로 재시도
  - RSI ≥ 88 극단 차단 → SPIKE_DETECT는 예외 (급등 코인은 RSI 높은 게 정상)

## [3.1.3] - 2026-04-05

### Changed

- **PUMP 후보 선정 개선 (방안A)**:
  - Volume 가중치 50%→25%, Volatility 20%→35%, Momentum 30%→40%
  - 후보 수 40→60개, 메이저(BTC/ETH/SOL/XRP) PUMP 스캔에서 제외
- **PUMP 진입 조건 완화 (방안B)**:
  - `rulePass AND structureOk AND momentumOk` → `rulePass AND (structureOk OR momentumOk)`
  - FibScore +20점 PUMP 코인에도 적용 (기존 메이저만)
  - ML 단독 진입 확률 60%→55%, 야간 threshold 40→35
- **급등 실시간 감지 (방안C)**:
  - 전 종목 5분 가격 변동률 스캔 (TickerCache 기반)
  - +3% 급등 + 거래량 $1M+ → PumpScan 스킵 즉시 진입 (SPIKE_DETECT)
  - 텔레그램 알림, 코인별 30분 쿨다운

## [3.1.2] - 2026-04-03

### Added

- **순 투입금 기반 수익률 표시**: 바이낸스 Income History API에서 Transfer In/Out 합산
  - 대시보드에 "TRANSFER PnL" 필드 추가 (투입금, PnL 금액, 수익률%)
  - 진입 사이즈는 기존 가용 잔고 기준 유지
  - 최근 1년 입출금 내역 자동 조회 (앱 시작 시)

## [3.1.1] - 2026-04-03

### Added

- **AI 시장 상태 분류기 (MarketRegimeClassifier)**: LightGBM 3분류 모델
  - TRENDING / SIDEWAYS / VOLATILE 자동 판별
  - 피처: BB_Width, ADX, ATR비율, RSI, MACD기울기, 거래량변화, SMA정배열, 캔들바디비율
  - 5분봉 KlineCache에서 자동 라벨링 + 학습
- **AI 최적 익절 모델 (ExitOptimizerService)**: LightGBM 이진분류
  - EXIT_NOW / HOLD 판단 (ROE 10%+ 보유 중 5초 간격 질의)
  - 피처: 현재ROE, 최고ROE, 되돌림크기, 시장상태, BB_Width, ADX, RSI, 보유시간
  - 과거 트레이드 DB에서 자동 학습 (최고ROE 대비 수익 보존율로 라벨링)
  - 횡보장에서 EXIT_NOW 70%+ → 즉시 익절 (수익 증발 방지)
  - 추세장에서 HOLD → 기존 트레일링 유지 (상승 기회 보존)

## [3.1.0] - 2026-04-02

### Added

- **시장 급변 감지 시스템 (MarketCrashDetector)**: BTC/ETH/SOL/XRP 1분 가격 변동률 실시간 추적
  - CRASH 감지: N개 코인 동시 -1.5% → 보유 LONG 전량 긴급 청산 + SHORT 리버스 진입
  - PUMP 감지: N개 코인 동시 +1.5% → 보유 SHORT 전량 긴급 청산 + LONG 리버스 진입
  - 리버스 진입 사이즈 조절 (기본 50%), 쿨다운 (기본 120초)
  - 텔레그램 알림 (CRASH/PUMP 감지 즉시)
- **설정창 급변 감지 섹션**: 활성화 토글, 임계값, 최소 코인 수, 리버스 사이즈, 쿨다운 조정

## [3.0.16] - 2026-04-02

### Fixed

- **유저 스트림 끊김 후 복구 안 되는 문제**: ListenKey KeepAlive 25분 주기 자동 갱신 + 끊김 감지 시 완전 재구독 워치독 추가

### Removed

- **PUMP 15분 타임스탑**: 횡보 시 자동 청산 제거 (손절/트레일링이 관리)
- **PUMP ElliottWave 20분 타임스탑**: 횡보 시 절대 손절 제거
- **메이저 120분 미사용 변수**: `maxHoldingMinutes`, `timeoutExitRoeThreshold` 죽은 코드 정리

## [3.0.15] - 2026-04-01

### Fixed

- **팬텀 EXTERNAL_PARTIAL_CLOSE_SYNC 중복 기록**: 전량 청산 후 WebSocket ACCOUNT_UPDATE 분할 도착 시 ACCOUNT_UPDATE_RESTORED → EXTERNAL_PARTIAL_CLOSE_SYNC로 팬텀 PnL 중복 기록되는 문제 수정
  - 청산 완료 시 30초 쿨다운 등록, 쿨다운 중 해당 심볼 ACCOUNT_UPDATE 무시
  - SOL 청산 시 실제 -125 PnL 외에 팬텀 -63 PnL이 추가 기록되던 문제 해결

## [3.0.14] - 2026-04-01

### Fixed

- **모든 주문 경로 stepSize/tickSize 보정 보장**: ExchangeInfo API 실패 시에도 심볼별 폴백 테이블로 보정 (XRP 0.1, SOL 0.1, DOGE 1, PEPE 100 등 15종)
- **PlaceMarketOrderAsync**: ExchangeInfo 직접 호출 → `GetSymbolPrecisionAsync` 캐시 재활용 (중복 API 호출 제거)
- **PlaceStopOrderAsync**: stepSize/tickSize 보정 누락 → 추가
- **PlaceLimitOrderAsync**: ExchangeInfo 실패 시 보정 스킵 → 폴백 보장

## [3.0.13] - 2026-04-01

### Fixed

- **부분청산 수량 stepSize 오류**: `PlaceOrderAsync`에서 reduceOnly 시에도 stepSize 보정 적용
- **부분청산 재시도 강화**: 1회 → 최대 3회 반복 재시도 (매회 거래소 포지션 재확인 + 수량 보정)

### Added

- **청산오류 텔레그램 알림**: 부분청산 최종 실패 시 별도 텔레그램 알림 전송
- **설정창 청산오류 알림 토글**: `TelegramMessageType.CloseError` 추가, 진입 알림과 독립적으로 ON/OFF 가능

## [3.0.12] - 2026-04-01

### Fixed

- **부분청산 실패해도 "완료" 표시되는 버그**: `ExecutePartialClose`를 `Task<bool>` 반환으로 변경, 12개 호출부 전부 성공 여부 확인 후에만 상태 변경 + 완료 메시지 발행
- **트레일링스탑 가격 메인창 미표시**: `OnTrailingStopPriceUpdate` 이벤트 추가, SL/TP 컬럼에 `TS:가격` 실시간 표시

### Added

- **주문 오류 DB 기록 (`dbo.Order_Error`)**: 부분청산 실패 시 ErrorCode, ErrorMsg, RetryCount, Resolution 기록
- **부분청산 실패 자동 분석 + 재시도**: 거래소 실제 포지션 확인 → 수량 불일치 보정 → 재시도, 이미 청산된 경우 내부 상태 자동 정리

## [3.0.11] - 2026-04-01

### Fixed

- **XRP 부분청산 실패**: MockExchange 1% 랜덤 실패 제거 (시뮬레이션 안정화)
- **TP/SL UI 미표시**: 진입 시 TP 가격 계산 (BTC +20% ROE, ETH/SOL/XRP +30%, PUMP +25%)
- **트레일링 SL UI 미반영**: 고점 갱신 시 PositionInfo.StopLoss 실시간 업데이트

### Added

- **정찰대→메인 전환 (MonitorScoutToMainUpgradeAsync)**:
  - ROE +5% (2분 후) → 70% 메인 추가
  - ROE +2% (5분 후) → 50% 메인 추가
  - ROE 0~+2% (10분 후) → 30% 메인 추가
  - ROE 음수 → 추가 안 함 (정찰대만 유지)
  - 15분 경과 + ROE < +2% → 추가 포기

### Changed

- **사이즈 결정 간소화**: 정찰대=30% 고정(AI 축소 무시), 메인=100% 기본(3분류 모델만 조절)

## [3.0.9] - 2026-04-01

### Added

- **PUMP 전용 ML 모델 (PumpSignalClassifier)**: 급등 패턴 특화 이진 분류
  - 18개 피처: 거래량 급증(3/5봉), 가격 변화율(3/5/10봉), RSI 변화량, BB 돌파 강도, MACD 가속도, ATR 비율, Higher Lows, 연속 양봉 등
  - 라벨: 30분 내 +2% 이상 상승 → 진입(1), 그 외 → 관망(0)
  - PumpScanStrategy에서 규칙 기반 + ML 결합 판단

### Changed

- **PUMP SHORT 제거**: 급등 코인은 LONG만 허용 (숏은 리스크만 큼)
- **PUMP 신호 조건 완화**:
  - 후보 20개 → **40개**로 확대
  - longThreshold 55~65 → **40~55**로 하향
  - RSI 75+ 감점 제거 → +3 보너스 (급등 모멘텀 유지)
  - BB 상단 돌파: 감점 → +5 강세 신호 (RSI 85+만 감점)
  - MACD Hist >= -0.001 → **>= -0.01** 완화
  - bullishStructure: 엘리엇 OR Higher Lows OR **가격>SMA20** (OR 조건 확대)
  - ML 모델 60%+ 확률이면 규칙 미통과여도 LONG 진입 가능

## [3.0.8] - 2026-04-01

### Changed

- **ExecuteAutoOrder 리팩토링**: 1540줄 단일 메서드 → 6개 분리
  - `ExecuteAutoOrder()`: 라우터 (공통 검증 → 4개 분기)
  - `ExecuteMajorLongEntry()`: 메이저 롱 (ATR SL, EMA/스퀴즈 보너스)
  - `ExecuteMajorShortEntry()`: 메이저 숏 (RSI 과매도만 차단)
  - `ExecutePumpLongEntry()`: PUMP 롱 (고정 증거금)
  - `ExecutePumpShortEntry()`: PUMP 숏 (RSI 과매도만 차단)
  - `PlaceAndTrackEntryAsync()`: 공통 주문 실행 (레버리지/수량/주문/DB/텔레그램)
- **사이즈 축소 중첩 제거**: 5개 AI Advisor가 Math.Min 연쇄 → 최저값 1개만 선택, 최소 10% 하한
- **FundingCost Exit / TimeOut Exit 제거**: 단타에서 무의미한 청산 사유 삭제
- **부분청산 텔레그램 알림 추가**: TP1/TP2 부분익절 시 텔레그램 전송

## [3.0.7] - 2026-03-31

### Added

- **3분류 매매 신호 모델 (TradeSignalClassifier)**: ML.NET LightGBM MultiClass
  - 롱(1) / 숏(2) / 관망(0) 3분류 예측
  - 24개 피처: RSI, MACD, BB, ATR, ADX, Volume, OI, 피보나치, 캔들 패턴 등
  - DB CandleData에서 자동 라벨링 (30분 후 가격변동률 ±1% 기준)
  - 앱 시작 시 모델 로드 또는 DB 데이터로 자동 초기 학습
  - 진입 시 3분류 모델 추론 → 같은 방향이면 사이즈 부스트, 반대면 축소

### Changed

- **TensorFlow.NET 완전 제거**: ML.NET 단독 아키텍처로 통합
  - TensorFlow.NET 패키지 제거
  - TensorFlowTransformer/TensorFlowEntryTimingTrainer 빌드 제외
  - AIDoubleCheckEntryGate: TF 의존 제거 → ML.NET _mlTrainer 단독
  - 4개 서비스(AIDataflowPipeline, AIDedicatedWorkerThread, AIPipelineServer, HybridAIPredictionService): TF 필드 제거
  - AdaptiveOnlineLearningService: TF 학습 제거

## [3.0.6] - 2026-03-31

### Changed

- **AI Advisor 전환**: AI를 게이트키퍼(차단자)에서 어드바이저(조언자)로 전면 리팩토링
  - `AI_GATE`: 거부 시 차단 → blended score 기반 사이즈 동적 조절 (90%+→풀, 50%→20%, <50%→10%)
  - `AI Score Filter`: 점수 미달 차단 → score/threshold 비율로 사이즈 축소 (70%+→50%, 50%+→30%)
  - `ATR_VOL`: 변동성 폭발 차단 → 사이즈 축소 (극단→20%, 높음→50%)
  - ProfitRegressor: 손실 예측 차단 → 50% 축소 진입 (v3.0.4에서 적용)
- **하드 블록 추가 축소**: 17개 → 11개 (필수 안전장치만 유지)

### Retained (필수 하드 블록)

- Signal Validation, RSI Extreme (88/12), Circuit Breaker, R:R Ratio
- Blacklist, Slot Cooldown, Duplicate Position, Slot Limit
- Leverage/Size/Order Execution 실패

## [3.0.5] - 2026-03-31

### Fixed

- **SHORT 진입 불가 수정**: AI 상승예측/MACD 양수/하락확률 부족 3중 차단 제거
  - 기존: AI 상승 예측 OR MACD>0 OR 확률<60% → 무조건 SHORT 차단 (사실상 SHORT 불가)
  - 수정: RSI 과매도 + 가격 MA20 위 (추세 없는 역매도)만 차단, 나머지는 AI 스코어에 위임

## [3.0.4] - 2026-03-31

### Fixed

- **시뮬레이션 정찰대 진입 불가 수정**: AI 게이트 거부 시 blended score 50%+ 이면 10% 정찰대 자동 투입
  - 기존: AI 게이트 거부 + blended < 70% → 무조건 차단 (정찰대 배치 불가)
  - 수정: 시뮬레이션 모드에서 blended ≥ 50% 시 최소 정찰대 투입
- **AI 게이트 차단 로그 누락**: 바이패스 전부 실패 시 로그 없이 return하던 문제에 상세 로그 추가

### Changed

- **과잉 진입 필터 6개 제거/완화**: 하드 블록 23개 → 17개로 축소
  - `PROFIT_REG`: 차단 → 50% 사이즈 축소 진입 (손실 예측이어도 기회 유지)
  - `M15_SLOPE`: 하드 블록 → INFO 로그 (메이저 LONG 기회 증가)
  - `CANDLE_CONFIRM` (서브필터 5개): 지연 대기 시스템 전체 제거 (즉시 진입)
  - `1M_HUB`: 60~180초 대기 제거 (즉시 실행)
  - `PATTERN_HOLD`: 차단 → INFO 로그 (패턴 디퍼 차단 제거)
  - `EW_RR`: 이중 R:R 차단 → INFO 로그 (기존 RR 필터로 통합)

## [3.0.3] - 2026-03-30

### Fixed

- **시뮬레이션 모드 TradeHistory 저장**: 시뮬레이션 자동매매 시 TradeHistory에 데이터가 쌓이지 않던 문제 수정
  - 청산/부분청산/패턴 스냅샷 라벨링 모두 시뮬레이션 모드에서도 DB 저장
  - `IsSimulation` 컬럼으로 실거래/시뮬레이션 거래 구분 가능

### Added

- **TradeHistory IsSimulation 컬럼 자동 마이그레이션**: 앱 시작 시 `IsSimulation BIT` 컬럼 자동 추가
- **시뮬레이션 거래 AI 학습 지원**: 시뮬레이션 수익/손실 거래도 패턴 스냅샷 라벨링 및 AI 학습 데이터로 활용

## [3.0.2] - 2026-03-30

### Added

- **PUMP 코인 정찰대 진입**: `HandlePumpEntry`에서 슬롯 포화 시 30% 증거금으로 정찰대 진입 (기존엔 차단)
  - 메이저/PUMP/총 슬롯 포화 모두 정찰대 전환

## [3.0.1] - 2026-03-30

### Added

- **DB TradeHistory 기반 수익률 학습**: 과거 60일 거래 내역을 ProfitRegressor 학습 데이터로 로드
  - `LoadFromTradeHistoryAsync()`: DB에서 청산 완료된 거래 + 진입 시점 캔들 지표 조인
  - 엔진 시작 시 자동 로드 → 50건 이상이면 즉시 학습
  - 이익/손실 거래 모두 포함하여 수익 패턴과 손실 패턴 동시 학습
- **DbManager.GetRecentCandleDataAsync()**: 심볼별 최근 캔들 지표 조회 (학습 데이터용)

## [3.0.0] - 2026-03-27

### Added

- **수익률 회귀 모델** (`ProfitRegressorService`):
  - "지금 진입하면 5분 뒤 수익률이 얼마인가" ML.NET FastTree 회귀 예측
  - 실제 거래 결과(P&L, 보유시간)로 자동 학습 (50건 이상 축적 시)
  - R², MAE, RMSE 검증 메트릭 로깅
- **동적 포지션 사이징**:
  - 예측 수익률 +3% 이상 → 150% 진입, +2% → 130%, +1% → 100%
  - 예측 수익률 음수 → **진입 금지** (손실 방어)
- **거래 결과 피드백 학습**:
  - 청산 시 `OnPositionClosedForAiLabel` 이벤트에서 자동 데이터 수집
  - 학습 버퍼 최대 5000건, 자동 순환

## [2.9.2] - 2026-03-27

### Changed

- **메인 대시보드 레이아웃 정리**:
  - 좌측 사이드바 260px → 200px 축소 (DataGrid 영역 확보)
  - Entry Gate 카드: 3행 → 간결한 2행 레이아웃, 불필요한 장식 Border 제거
  - AI Learning 카드: 마진/폰트 축소, 3행 그리드 유지
  - AI PREDICTIVE TIME-OUT: 패딩 14px→8px, 헤더 1줄 축소

## [2.9.1] - 2026-03-27

### Added

- **SSA 시계열 예측 → TradingEngine 메인 루프 연동**:
  - 5분마다 KlineCache에서 종가 데이터로 SSA 학습 + 예측
  - `OnSsaForecastUpdate` 이벤트 → ViewModel → SkiaCandleChart 자동 전달
  - SymbolChartWindow: PropertyChanged 구독으로 예측 밴드 실시간 업데이트

## [2.9.0] - 2026-03-27

### Added

- **RingBuffer\<T\>**: GC 부하 없는 고정 크기 순환 배열 — 캔들/틱 데이터 저장소
- **ML.NET SSA 시계열 예측** (`SsaPriceForecastService`):
  - ForecastBySsa 알고리즘: WindowSize=20, Horizon=5, Confidence=95%
  - 데이터 정규화 (기준가 대비 bps 변환) → 0% 수렴 방지
  - 예측값 + Upper/Lower Bound → SkiaSharp 차트 반투명 영역
- **Microsoft.ML.TimeSeries** NuGet 패키지 추가
- **Binance API Rate Limiter**: SemaphoreSlim(20) 동시 요청 제한

## [2.8.1] - 2026-03-27

### Added

- **SkiaSharp 고성능 렌더링 강화**:
  - 가상화(Virtualization): 최근 100개 캔들만 렌더링, 나머지는 메모리 유지
  - Buy/Sell 시그널 마커: 진입/청산 시점에 삼각형 마커 + 라벨 차트 오버레이
  - `AddSignalMarker()` / `ClearSignalMarkers()` API
- **Fail-safe 리스크 관리 모듈** (`FailSafeGuardService`):
  - API 하트비트 모니터링 (30초 경고, 60초 긴급 전체 청산)
  - 슬리피지 감지 (체결가 vs 요청가 1% 이상 차이 시 알림)
  - 잔고 급감 감지 (50% 이상 감소 시 전체 포지션 긴급 청산)
  - TradingEngine에 연동 (OnEmergencyCloseAll → 전체 ClosePositionAsync)

## [2.8.0] - 2026-03-27

### Added

- **핀테크 대시보드 v1**:
  - SkiaSharp 차트에 SMA20(금색)/SMA50(파랑) 이동평균선 오버레이
  - Bollinger Bands 밴드(보라) 영역 채우기 + 상/하 경계선
  - `CandleRenderData`에 SMA20/SMA50/BB 데이터 필드 확장
  - SymbolChartWindow: ML.NET / Transformer / AI Score / RSI / Signal 5카드 의사결정 패널
  - MainWindow 우측 패널: AI PREDICTION 요약 카드 (ML/TF ProgressBar + BB Position 텍스트)

## [2.7.1] - 2026-03-27

### Fixed

- **PUMP 코인이 메이저 모드로 처리되는 버그**: 봇 재시작/포지션 복구 시 `savedPump = false` 기본값 → 메이저 손절(-20%) 적용됨. `!MajorSymbols.Contains(symbol)`로 수정
- **정찰대 진입 실패**: 슬롯 포화 시 정찰대 플래그 설정 후 주문 직전 최종 슬롯 재확인에서 다시 차단. `scoutModeActivated` 시 최종 재확인 스킵

## [2.7.0] - 2026-03-27

### Added

- **수동 진입/청산 텔레그램 알림**: ManualEntry, DirectClose, MockClose 모두 텔레그램 발송
- **슬롯 포화 시 정찰대 자동 진입**: AI 게이트 통과했으나 슬롯 포화(메이저/PUMP/총) 시 30% 정찰대 진입 (기존: 진입 차단 return)

### Removed

- **본절 청산 로직 제거**: 1단계 본절 보호(ROE 7% → 진입가 스탑) 비활성화 — 수익 기회 제한 방지
- **시간 기반 본절 전환 제거**: 보유 시간 초과 시 자동 본절 전환 비활성화
- 2단계 부분익절(ROE 20%), 3단계 트레일링은 유지

## [2.6.11] - 2026-03-26

### Fixed

- **테스트넷 청산 안 되는 근본 원인**: `PlaceOrderAsync`에 시뮬레이션 모드 체크(`IsSimulationMode == true → return true`)가 있어 테스트넷에서도 실제 주문을 보내지 않고 가짜 성공 반환하던 문제 제거
  - 진입(`PlaceMarketOrderAsync`)은 이 체크가 없어 정상 작동했지만, 청산(`PlaceOrderAsync reduceOnly`)은 차단됨

## [2.6.10] - 2026-03-26

### Fixed

- **SHORT 진입 시 ROI 부호 반대 표시**: 수동 진입 후 `PositionSide`가 설정되지 않아 LONG으로 간주됨 → `OnSignalUpdate`로 `PositionSide`/`Leverage` 전달 추가

## [2.6.9] - 2026-03-26

### Fixed

- **시뮬레이션/실거래 혼선 근본 해결**: 테스트넷 모드에서 실거래 API로 호출되는 6개 경로 수정
  - `_client` (BinanceRestClient): 테스트넷 키 + `BinanceEnvironment.Testnet` 설정 → ExecutionService, PositionMonitor, OrderService 모두 테스트넷으로 통일
  - `MarketDataManager` WebSocket: 테스트넷 키 + 테스트넷 환경 설정
  - `BinanceSocketConnector` WebSocket: 동일 수정
  - `DirectClosePositionAsync`: `_positionMonitor` 미초기화 시 직접 거래소 API 청산

## [2.6.8] - 2026-03-26

### Fixed

- **테스트넷 CLOSE 완전 해결**: `_positionMonitor` 미초기화(스캔 미시작) 시 `_exchangeService`로 직접 청산
  - `DirectClosePositionAsync`: 거래소 포지션 조회 → 반대 방향 시장가 주문 → DB 기록 → UI 정리

## [2.6.7] - 2026-03-26

### Fixed

- **테스트넷 시뮬레이션 CLOSE 안 됨 해결**: 테스트넷(demo.binancefuture.com) 모드에서는 실제 거래소 API로 청산 주문 전송하도록 수정
  - MockExchange(가상)일 때만 로컬 제거, 테스트넷은 `PositionMonitor.ExecuteMarketClose` 경유

## [2.6.6] - 2026-03-26

### Fixed

- **시뮬레이션 청산 시 포지션 미닫힘**: CloseIncomplete 플래그 해제, ROI 0 리셋, HybridExitManager 상태 정리, 블랙리스트 등록 누락 수정

## [2.6.5] - 2026-03-26

### Added

- **유저별 시뮬레이션 테스트넷 API 키 관리**: ProfileWindow에 Binance Testnet API Key/Secret 입력 필드 추가
  - DB `Users` 테이블에 `TestnetApiKey`/`TestnetApiSecret` 컬럼 자동 추가
  - 로그인 시 유저별 테스트넷 키 자동 로드 → 시뮬레이션 모드에서 개인 키 사용
  - AES256 암호화 저장/복호화 로드

## [2.6.4] - 2026-03-26

### Fixed

- **API 키 수정 실패 해결**: `UpdateUserAsync` SQL에서 `BybitApiKey`/`BybitApiSecret` 파라미터 참조 → `User` 클래스에 해당 프로퍼티 없어 Dapper 에러 발생
- **Bybit 섹션 제거**: ProfileWindow에서 미사용 Bybit API Key 입력 필드 삭제
- **불필요한 ALTER TABLE 제거**: Bybit 컬럼 자동 추가 SQL 삭제

## [2.6.3] - 2026-03-26

### Changed

- **Critical Alerts 위치 이동**: 좌측 사이드바 → 메인 DataGrid 하단 120px 영역 (5줄 표시)

### Fixed

- **시뮬레이션 모드 CLOSE 청산미완료 해결**: 거래소 API 우회, 로컬 포지션 직접 제거 + DB 기록 + UI 즉시 갱신

## [2.6.2] - 2026-03-26

### Fixed

- **수동 진입 후 Close 버튼 미표시**: `OnPositionStatusUpdate` 호출 누락 → `IsPositionActive=true` 설정되어 Close 버튼 정상 표시
- **수동 진입 히스토리 미기록**: DB `TradeHistory`에 진입 레코드 저장 추가 (`SaveTradeLogAsync` → `UpsertTradeEntryAsync`)
- **수동 진입 후 UI 자동 갱신 안 됨**: `OnTradeHistoryUpdated` 이벤트 발생 추가

## [2.6.1] - 2026-03-26

### Added

- **수동 LONG/SHORT 진입 버튼**: START/STOP 옆에 배치, AI 게이트 우회 시장가 즉시 진입
  - 선택된 심볼 + 현재가 확인 팝업 → DefaultLeverage/DefaultMargin 사용
  - `TradingEngine.ManualEntryAsync()` 신규 메서드

### Fixed

- **2시간+ 미진입 근본 해결 (3건)**:
  - `IsReady` 조건 완화: ML+TF 동시 필요 → TF만 준비되면 게이트 개방
  - ML.NET 미가용/0% 시 `EvaluateEntryAsync` 하드 블록 제거 → TF 단독 모드 자동 전환
  - 엘리엇 규칙 1/2 위반 하드 블록 → TF 80%+ 시 경고로 다운그레이드

- **ML.NET 0% 수렴 근본 방어**:
  - `SanitizeFeature()`: Predict 전 34개 피처 NaN/Infinity→0 치환
  - Predict 결과 Probability/Score NaN 보정 (MLService 포함)
  - ML 0% && TF>=50% 자동 감지 → TF 단독 판단 전환
  - `EntryRuleValidator.FinalScore`: ML<1% 시 TF 점수로 대체

## [2.6.0] - 2026-03-26

### Added

- **AI 추론 파이프라인 전면 최적화**:
  - `PredictionEnginePool` (Microsoft.Extensions.ML): Thread-safe 풀링, lock 없이 다중 스레드 동시 ML.NET 추론
  - `ArrayPool<float>` 기반 Feature 벡터 할당: TensorFlowTransformer에서 GC 압박 제거
  - `EntryTimingMLTrainer.Predict()` 핵심 병목 해결: 매 호출 CreatePredictionEngine → 캐싱+풀 전환

- **TPL Dataflow AI 파이프라인** (`AIDataflowPipeline`):
  - `BufferBlock → TransformBlock → ActionBlock` 3단 비동기 파이프라인
  - 심볼별 500ms 디바운싱, 백프레셔 자동 관리 (버퍼 100 초과 시 드랍)
  - ML.NET + TF 추론 → `Dispatcher.InvokeAsync(Background)`로 UI 전달

- **AI 전용 워커 스레드** (`AIDedicatedWorkerThread`):
  - `ThreadPriority.AboveNormal`: UI 스레드(Normal)보다 높은 우선순위
  - `BlockingCollection` Producer-Consumer 패턴, 주문 조건 충족 시 UI 거치지 않고 즉시 실행
  - UI 차트 1초 늦어도 주문은 실시간 시세에 정밀 실행

- **Named Pipes IPC 프로세스 분리 인프라**:
  - `AIPipelineIpcService` (클라이언트) + `AIPipelineServer` (서버): Named Pipe 비동기 통신
  - `HybridAIPredictionService`: IPC 우선 → 인프로세스 폴백 (무중단 전환)
  - 향후 AI 엔진을 별도 콘솔 앱으로 분리 가능

- **SkiaSharp 고성능 캔들 차트** (`SkiaCandleChart`):
  - LiveCharts Shape 객체 대신 비트맵 직접 렌더링 (GC 할당 0)
  - 18개 SKPaint + 3개 SKFont 필드 캐싱 (프레임당 객체 생성 0)
  - AI 오버레이 5개 레이어: 예측 밴드, 트레일링 히스토리, 동적 손절선, 현재가 마커, STOP-EXIT 경고
  - `Interlocked` 기반 AI↔렌더 스레드 원자적 데이터 공유 (lock-free)
  - DispatcherTimer 4fps 프레임 제한 + `_needsRedraw` 플래그 최적화

- **상승 에너지 잔량 ProgressBar** (`PRICE ENERGY`):
  - ML.NET Fib 0.382~0.5 눌림목에서만 초록색 (ENTRY ZONE)
  - TF 수렴 후 발산 패턴 감지 → 금색 테두리 + ProgressBar 깜빡임 (SQUEEZE FIRE)
  - `OnPriceProgressUpdate(bbPos, mlConf, tfConvDiv)` 3파라미터 이벤트

- **AI 동적 트레일링 스탑 엔진** (`DynamicTrailingStopEngine`):
  - 추세 반전 확률 계산: 5파 완성(+35%), RSI 다이버전스(+25%), 상위 꼬리(+15%), RSI 80+(+20%)
  - ATR 멀티플라이어 AI 보정: 반전확률 70%+ → ×0.3, 변동성 폭발 → ×0.5
  - 반전 80%+ 극한 타이트: 0.2% 이내 트레일링
  - Exit Score 게이지 (0~100): HOLD / PREPARE EXIT / EXIT NOW / EMERGENCY

- **Exit Score UI 게이지** (`EXIT SCORE`):
  - 80%+ 시 빨간 테두리 깜빡임 애니메이션
  - 반전 확률 + 동적 손절가 동시 표시

- **SymbolChartWindow 리뉴얼**:
  - SkiaCandleChart + LiveCharts 이중 구조 (고성능 + 폴백)
  - PRICE ENERGY + EXIT SCORE 바 상단 배치
  - AI 손절가 실시간 연동 (`OnAIStopPriceChanged` 이벤트)

### Changed

- `AIPredictor`, `MLService`: `_predictLock`으로 PredictionEngine Thread-safety 보장
- `MainViewModel`: 적응형 ticker flush 간격 (100~500ms 자동 조절) + 재진입 방지 (`_tickerFlushRunning`)
- `TradingEngine`: `OnPriceProgressUpdate` 3파라미터 확장, `OnExitScoreUpdate` 이벤트 추가, AI 워커 + 동적 트레일링 엔진 자동 초기화

### Dependencies

- `System.Threading.Tasks.Dataflow` 9.0.1
- `Microsoft.Extensions.ML` 5.0.0
- `SkiaSharp` 3.116.1
- `SkiaSharp.Views.WPF` 3.116.1

## [2.5.6] - 2026-03-26

### Added

- **ElliottWave 1분봉 빠른 진입 트리거**:
  - 1분봉 기울기 0.3%+ & 거래량 20MA 대비 3x → V-Turn 확인 없이 즉시 진입
  - 15분봉 확인 매매 지연 문제 해결 (3파 초입 포착)

- **AI 피처 4개 추가 (추세 강도·국면 분류)**:
  - ADX (14): 추세 강도 판별 (>25 강한 추세, <20 횡보)
  - Ichimoku 기준선(26) 돌파: 상승/하락 국면 판단
  - FundingRate: 숏스퀴즈/롱스퀴즈 가능성 (밈코인 필수)
  - SymbolCategory: 종목별 파동 성격 구분 (BTC/ETH/Major/Meme/MicroCap)

- **ADX + 이치모쿠 기준선 지표 계산** (AIBacktestEngine)

### Changed

- 총 피처 수: 60개 → 64개

## [2.5.5] - 2026-03-25

### Fixed

- **이격도/OBV 하드 필터 제거**: VWAP 2%/EMA200 15% 하드 차단이 거의 모든 진입을 막고 있었음
  - 크립토 특성상 VWAP 2~5% 이격은 정상 → 하드 블록은 과잉 차단
  - VWAP_Position, OBV_Slope 등은 AI 피처로만 학습에 반영 (ML이 자체 판단)

## [2.5.4] - 2026-03-25

### Added

- **AI 피처 5개 추가 (선행성·정규화 강화)**:
  - VWAP_Position: 기관/세력 평균 단가 대비 위치 (당일 리셋)
  - Price_To_EMA200: 장기추세 기준점 이격도
  - RSI_Divergence: 5분봉 다이버전스 (가짜 상승/하락 감지)
  - OBV_Slope: On-Balance Volume 변화율 (자금 유출입)
  - Pivot_Position: 피보나치 피봇 포인트 (전일 기반 지지/저항)

- **이격도 과열 필터**: VWAP 2%+/EMA200 15%+ 이격 시 AI 진입 차단
- **OBV 가짜 상승 필터**: 가격↑ + OBV↓ 동시 발생 시 진입 차단
- **ATR 동적 손절**: 변동성 기반 SL 자동 조정 (ATR↓: -15%, ATR↑: -35%)
- **개미 털기 HOLD**: 하락 + 볼륨 없음 → 손절 보류 (세력 털기 방어)

### Changed

- **라벨링 최적화**: 60봉(5시간) → 15봉(75분), TP+1.0%/SL-0.5%
  - AI가 느린 추세 대신 '빠른 모멘텀 파동'을 학습
- 총 피처 수: 55개 → 60개 (5분봉 25 + 강화 5 + 15분봉 17 + 1H 5 + 4H 5 + 패턴 3)

## [2.5.3] - 2026-03-25

### Added

- **정찰대 v2 (Scout AI70)**: AI 블렌디드 신뢰도 70%+면 무조건 정찰대 투입
  - 80%+: 시드 20% 선진입, 70~80%: 시드 10% 선진입
  - 기존 TF≥90% 조건에서 대폭 하향하여 3파 놓침 방지

- **1분봉 스나이핑 확대**: 정찰대/불타기도 1분봉 ExecutionHub 적용
  - 15분봉 '흐름' 확인 → 1분봉 '채찍'(거래량 양봉) 즉시 진입
  - 정찰대: 60초 대기, 정규 진입: 180초 대기

- **구조적 손절 (Structural Stop)**: 전저점/전고점 종가 확인 후 청산
  - LONG: 최근 12봉 Swing Low 이탈을 '종가'로 확인 시 청산
  - SHORT: 최근 12봉 Swing High 돌파를 '종가'로 확인 시 청산
  - 고정 -18% ROE 대신 구조적 기준 → 세력 털기 wick 방어

- **불타기 v2 (Fib 0.382 Wave3 Add-on)**: 3파 진입 감지
  - Fib 0.382 돌파 + 볼륨 회복 → 40% 추가 투입
  - 계단식 고점 돌파 → 20% 추가 투입

### Changed

 - 없음

## [2.5.2] - 2026-03-22

### Fixed

- ProgressBar `DailyProfitProgress` / `VolumeGauge` TwoWay 바인딩 오류 수정 (Mode=OneWay)

## [2.5.1] - 2026-03-20

### Added

- **AI Sniper Exit -> PositionMonitorService 실시간 연결**:
  - 30초 주기로 SniperExit ML 모델 호출하여 구조적 손절 판단
  - 개미 털기 감지: 볼륨 없는 하락 -> HOLD (고정 손절 방지)
  - 매도압력 급증 감지: 체결 강도 비정상 -> 긴급 청산
  - 추세 강세/약화 판단: 트레일링 갭 자동 확대/축소
  - 부분익절 자동 실행 (ML 확신도 65%+ 시)

- **백그라운드 자동 AI 학습**:
  - 엔진 시작시 AI 의사결정 모델 미준비 감지 -> 자동 학습 트리거
  - AIBacktestEngine 백그라운드 실행 (UI 블로킹 없음)
  - 학습 완료 후 모델 자동 재로드

- **Entry Pipeline UI 패널** (AI Command Center 탭):
  - Block Reason: 진입 차단 사유 빨간 글씨 실시간 표시
  - 1M Volume Gauge: 1분봉 거래량 진행바 (진입 조건 시각화)
  - Daily Profit Target: 일일 목표 $250 대비 현재 수익 진행바

### Changed

- **시뮬레이션 모드 UX 개선**:
  - 설정 저장시 "앱 재시작 불필요" 안내 메시지로 변경
  - Stop -> Start만으로 시뮬레이션/실거래 전환 가능

### Fixed

- EntryLog에서 BLOCK/PASS 상태에 따라 UI Block Reason 실시간 업데이트

## [2.5.0] - 2026-03-20

### Added

- **AI Sniper Mode 의사결정 엔진** (`AIDecisionService.cs`):
  - 43개 피처 기반 LightGBM 진입 모델 (5분봉 25 + 15분봉 핵심4대 15 + 패턴매칭 3)
  - 25개 피처 기반 청산 모델 (포지션 상태 8 + 시장 상태 10 + 상위TF 7)
  - 승리 패턴 스냅샷 DB + 코사인 유사도 매칭
  - 동적 SL/TP: 확신도 기반 연속 함수 (하드코딩 임계값 제거)
  - 동적 포지션 사이징: 확신도 5%~20% 연속 함수
  - 매도압력 감지 긴급손절 / 개미털기 홀드 / 트레일링 갭 자동 조정
  - 모델 자동 로드 (앱 시작시)

- **AI 학습 백테스트 엔진** (`AIBacktestEngine.cs`):
  - 3년치 5분봉 + 15분봉 + 1시간봉 + 4시간봉 다중TF 데이터 수집
  - 자동 라벨링: 미래 60봉 내 TP +1.5% / SL -0.8% 도달 여부
  - 실제 가격 시뮬레이션: ML진입 → 5분봉 순회 → SL/TP1/TP2 실제 체결
  - 부분익절(40%) + 본절이동 + 시간손절(10시간) 포함
  - 설정 창 'AI 학습' 버튼 (초록색)

- **백그라운드 자동 데이터 수집** (`AIDataCollectorService.cs`):
  - 앱 시작시 자동으로 백그라운드 수집 서비스 시작 (30분 주기)
  - 8심볼 × 4타임프레임 (5m/15m/1H/4H)
  - CSV 캐시 시스템: 첫 수집 ~25분, 이후 증분만 ~30초
  - 캐시 경로: `%LOCALAPPDATA%/TradingBot/KlineCache/`

- **15분봉 핵심 4대 지표 AI 피처**:
  - 볼린저 밴드: 스퀴즈 강도 + 상/하단 돌파 감지
  - RSI: V-Turn 감지 + 기울기(반등 강도) + 다이버전스
  - EMA 20/60: 골든/데드 크로스 + 이격도(에너지 잔량) + EMA20 지지 여부
  - 피보나치 되돌림: 0.618~0.786 골든존 감지 + 반등 확인

- **바이낸스 테스트넷 시뮬레이션**:
  - `BinanceExchangeService`에 테스트넷 환경 지원 추가
  - 시뮬레이션 모드 시 테스트넷 API로 실거래와 동일한 체결/DB저장
  - 테스트넷 키 없으면 MockExchange 자동 폴백
  - appsettings.json에 TestnetApiKey/TestnetApiSecret 필드 추가

- **시뮬레이션 모드 알림창**:
  - 스타트 버튼 클릭시 시뮬레이션 모드 확인 다이얼로그
  - 모드(테스트넷/Mock), 잔고, 실제자금 미사용 안내
  - '아니오' 선택시 시작 취소

- **다중 타임프레임 백테스터** (`MultiTimeframeBacktester.cs`):
  - 5분봉 모멘텀 스캘핑 + 자동 파라미터 최적화 (256개 조합)
  - 메이저 4종 + 펌프 4종 (DOGE/PEPE/WIF/BONK)
  - 설정 창 '5분봉 최적화' 버튼 (빨간색, Shift=자동 최적화)

- **워크포워드 최적화 엔진** (`WalkForwardOptimizer.cs`):
  - 롤링 윈도우 WFO: 12개월 IS + 4개월 OOS, 4개월 스텝
  - 6개 파라미터 그리드 서치 (병렬 실행)
  - 과적합 진단: 효율성비율, 안정성점수, 파라미터안정성
  - 설정 창 'WFO 최적화' 버튼 (노란색)

### Changed

- **TradingEngine 시뮬레이션 모드 개편**:
  - MockExchangeService → 바이낸스 테스트넷 우선 사용
  - 설정 저장 → 재시작 없이 스타트 시 즉시 반영

- **TradingConfig 확장**:
  - `TestnetApiKey`, `TestnetApiSecret` 필드 추가

- **BinanceExchangeService 테스트넷 지원**:
  - 생성자에 `useTestnet` 파라미터 추가
  - `BinanceEnvironment.Testnet` 환경 자동 설정

## [2.4.56] - 2026-03-19

### Added

- **펀딩비 누적 모니터링** (`PositionMonitorService.cs`):
  - 2시간 초과 보유 포지션에 대해 펀딩비 자동 추정 및 실질ROE 계산
  - 추정 누적 펀딩비 = (보유시간h / 8h) × 0.01%/8h × 레버리지 × 100
  - 트레일링 미진입 상태에서 실질ROE < 3% 시 자동 청산 (`FundingCost Exit`)
  - 30분마다 `💸 펀딩비 추적` 로그 출력으로 현황 모니터링 가능

### Changed

- **메인 UI 중앙 패널 전면 개편** (`MainWindow.xaml`):
  - 기존 3컬럼 배틀패널 → 코인 진입/포지션 중심 레이아웃으로 재설계
  - 콕핏 스트립 (76px): AI STATUS | THE PULSE | GOLDEN ZONE | ATR STOP | TREND-RIDER 5개 섹션 가로 배치
  - 메인 영역 좌우 분할: 데이터그리드(좌, *) + 이벤트/익절/스텝퍼 패널(우, 260px)
  - 데이터그리드 진입중 행 초록 배경 (`#081A0F`) 강조 표시 추가

- **데이터그리드 컬럼 개편** (`MainWindow.xaml`):
  - SYMBOL: 전략 배지(`Major Scalp` / `Pump Breakout`) + LONG/SHORT 컬러 pill 추가
  - `ML│TF` → `AI SCORE`: AIScore 대형 표시 + ML%/TF% 소형 표시로 가독성 향상
  - ROI: `+/-` 부호 명시 포맷 (`+12.50%` / `-3.20%`)으로 변경
  - STATUS: AI 점수 업데이트 시각(HH:mm:ss) 하단 표시 추가
  - `RISK (SL/TP)` → `SL / TP` 헤더 단축

- **트레일링 Floor ROE 강화** (`PositionMonitorService.cs`):
  - 일반 메이저 (BTC 포함): 트레일링 최소 ROE 유지선 +8% → **+12%**
  - ETH/XRP/SOL (ATR 2.0): +4% → **+6%**
  - 트레일링 구간 역전 시 수익 반납 최소화

- **Stage 2 부분익절 비율 상향** (`PositionMonitorService.cs`):
  - 1차 확정 익절 비율 30% → **40%**
  - ROE +20% (BTC/메이저) / +30% (ETH/XRP/SOL) 도달 시 포지션 40% 즉시 확정

## [2.4.55] - 2026-03-17

### Added

- **SLOT 최적화 3가지 개선안 통합** (`TradingEngine.cs`):
  1. **개선안 1: Scan 단계 Pre-Check** - SLOT 여유도를 신호 생성 전 사전 판단하여 불필요한 지표 계산 20~30% 감소
     - `CanAcceptNewEntry(symbol)` 헬퍼 메서드로 진입 가능 여부 사전 검증
     - `ShouldSkipScanDueToSlotPressure()` 통해 SLOT 80% 이상 시 신호 생성 자체 스킵
  
  2. **개선안 2: 심볼별 SLOT 쿨다운** - SLOT 차단된 심볼 재시도 10분(설정 가능) 금지로 반복 차단 90% 감소
     - `_slotBlockedSymbols` ConcurrentDictionary로 차단 심볼 추적
     - `IsSymbolInSlotCooldown()` / `RecordSlotBlockage()`로 쿨다운 관리
     - SLOT 차단 시 자동으로 심볼 등록하여 무의미한 재시도 억제
  
  3. **개선안 3: 동적 슬롯 조정** - UTC 시간대별로 신호 빈도에 맞춰 슬롯 탄력 조정
     - `GetDynamicMaxTotalSlots()`: 18~04시 UTC 4슬롯, 04~10시 5슬롯, 10~18시 6슬롯(표준)
     - `GetDynamicMaxPumpSlots()`: 18~06시 UTC PUMP 신호 금지(0슬롯), 06~18시 1슬롯 허용
     - 저활동 시간대 불필요한 SLOT 차단 35~45% 감소
  
### Fixed

- **SLOT 검증 로직 통합 개선** (`TradingEngine.cs`):
  - ExecuteAutoOrder 메서드에 기존 정적 SLOT 검증을 동적 슬롯 + 쿨다운 체크로 교체
  - 각 차단 사유 기록 시 RecordSlotBlockage()를 호출해 향후 쿨다운 적용
  - 메이저/PUMP/총 포지션 검증 순서를 최우선부터 명확히 정렬

### Changed

- **SLOT 차단 로그 가시성 개선** (`TradingEngine.cs`):
  - SLOT_COOLDOWN 태그로 쿨다운 중인 심볼 구분
  - 동적 PUMP 슬롯 상황(금지 시간대 vs 포화)을 로그에 명시
  - SLOT 차단 메시지에 UTC 시간대 정보 추가하여 시간대별 정책 추적 가능

### Performance

- **Scan 단계 사전 필터로 CPU 부하 25~30% 절감** - 신호 생성 단계 진입 차단으로 불필요한 지표 계산 스킵
- **SLOT별 반복 로그 밀도 30~40% 감소** - 같은 심볼의 무의미한 재시도 차단 로그 제거

## [2.4.54] - 2026-03-17

### Fixed

- **LONG 진입 1분 윗꼬리 필터 과차단 완화** (`TradingEngine.cs`):
  - 기존 `고점 대비 -0.3%` 단일 조건 차단을 제거하고, 닫힌 1분봉 기준 `실제 윗꼬리 반전`(윗꼬리 비율/캔들 변동폭/되밀림/약세 마감) 조합일 때만 차단하도록 개선
  - PUMP/MEME 신호와 RSI 완만 구간(`RSI<62`)은 윗꼬리 차단을 우회해 상승장 추세 진입 누락을 감소

- **PUMP 신호의 메이저 필터 오적용 수정** (`TradingEngine.cs`):
  - `MAJOR_MEME` 신호를 문자열 접두사로 메이저 취급하던 로직을 `ResolveCoinType` 기반으로 교체
  - 펌프 코인이 메이저 전용 AI 보너스/임계 경로에 잘못 진입해 차단되던 분기 충돌을 해소

## [2.4.53] - 2026-03-16

### Fixed

- **초기 슬리피지 보호에 의한 조기 청산 제거** (`TradingEngine.cs`):
  - `FastEntrySlippageMonitor` 기능을 완전 제거하여 진입 직후 짧은 흔들림으로 즉시 청산되던 문제를 해소
  - PUMP/메이저 공통 진입 경로에서 빠른 슬리피지 감시 호출을 삭제해 불필요한 청산 트리거를 제거

### Changed

- **로그 노이즈 정리** (`TradingEngine.cs`):
  - `SLIPPAGE[FAST]` 관련 경고/종료 로그 경로를 제거해 실거래 모니터링 로그의 신호 대 잡음비 개선
- **FAST LOG 3행 가시성 보정** (`MainWindow.xaml`):
  - `BattleFastLog3` 표시 영역 높이를 고정해 긴 로그 표시 시 레이아웃 흔들림을 완화

## [2.4.52] - 2026-03-16

### Fixed

- **밈/알트 실시간 UI 미갱신 경로 보완** (`TradingEngine.cs`):
  - `OnAllTickerUpdate` 스트림을 엔진에 연결하고, 활성 포지션/추적 심볼만 필터링해 `HandleTickerUpdate`로 전달
  - 메이저 목록 외 심볼(밈/펌프)에서도 UI 가격/시그널 갱신 누락을 줄임

- **PUMP 포지션 감시 누락으로 인한 익절/손절 미동작 문제 수정** (`TradingEngine.cs`):
  - 신규 진입/동기화/계좌복원 경로에서 PUMP 포지션은 표준 모니터 대신 PUMP 모니터를 일관 시작
  - `_runningPumpMonitors` 및 `TryStartPumpMonitor`로 중복 감시를 방지하고 종료 시 상태 정리

- **PUMP/밈 진입 증거금 100 USDT 고정 적용** (`TradingEngine.cs`):
  - `PUMP_FIXED_MARGIN_USDT` 상수 도입 및 공통/펌프 진입 경로 모두 고정 증거금 적용
  - 패턴/수동/RSI 기반 배수로 증거금이 변동되던 경로를 PUMP 코인에서 우회

- **드라이스펠 PUMP fallback 추격 진입 방어 추가** (`TradingEngine.cs`):
  - `DROUGHT_RECOVERY_PUMP` LONG 진입 시 15분봉 BB 상단 돌파 및 상단 과열(%B/RSI) 구간을 차단
  - `DROUGHT_CHASE` 로그 태그로 차단 원인 추적 가능

## [2.4.51] - 2026-03-16

### Added

- **드라이스펠 수동 진단 텔레그램 명령 `/drought` 추가** (`DroughtScanCommand.cs`, `TelegramService.cs`, `TradingEngine.cs`):
  - 텔레그램에서 즉시 드라이스펠 진단/복구 스캔 1회 실행 가능
  - 엔진 실행 상태 검증 후 결과 요약 문자열을 명령 응답으로 반환

- **드라이스펠 자동 복구 체인 강화** (`TradingEngine.cs`, `PumpScanStrategy.cs`):
  - 2시간 이내 ETA 고확률 후보 우선 진입(`DROUGHT_RECOVERY_2H`)
  - PASS 미충족 시 근접 후보 시험 진입(`DROUGHT_RECOVERY_NEAR_2H`, 비중 70%)
  - 그래도 후보 없으면 PUMP 확장 스캔 Top60 first-hit 연계(`DROUGHT_RECOVERY_PUMP`)

### Changed

- **드라이스펠 진단 결과 요약/액션 코드 고도화** (`TradingEngine.cs`):
  - `RunDroughtDiagnosticScanAsync`가 요약 문자열을 반환하도록 변경
  - `ETA2h / Near2h / PumpFallback / Action` 형식으로 상태를 일관 출력
  - `*_HOLDING_SKIP`, `NO_ENTRY`, `ERROR`, `CANCELLED` 등 액션 상태를 세분화

- **PUMP 복구 스캔 블랙리스트 안전성 개선** (`TradingEngine.cs`, `PumpScanStrategy.cs`):
  - 활성 보유 심볼을 복구 스캔 블랙리스트에 주입해 중복 진입을 방지
  - 복구 스캔 시 이벤트 emit 없이 first-hit 신호를 반환해 엔진이 직접 주문 경로를 제어

- **XRP 장기 샘플 기반 공통 필터 동적 튜닝 반영** (`TradingEngine.cs`):
  - 2025-01-01~현재 XRP 15분봉 샘플 점검으로 ATR 변동성 차단 배율/SHORT RSI floor 자동 보정
  - 특정 심볼 분기 없이 공통 파라미터에 적용

## [2.4.50] - 2026-03-16

### Added

- **텔레그램 메시지 타입별 허용/차단 설정 추가** (`AppConfig.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `Services/NotificationService.cs`, `TelegramService.cs`, `appsettings.json`):
  - Settings의 `Telegram Messages` 탭에서 Alert / Profit / Entry / AI Gate / Log 채널별 on/off 제어 지원
  - 알림 채널을 `TelegramMessageType`으로 매핑해 송신 직전 중앙 필터링 적용
  - 설정 저장 즉시 런타임(`AppConfig.Current.Telegram`)에 반영되어 재시작 없이 적용

### Changed

- **FAST LOG 백프레셔 가드 추가** (`MainViewModel.cs`):
  - 라이브 로그 입력 시 DB/라이브 큐 길이 임계치(soft: 700, hard: 1200) 초과를 감지
  - 주문 오류·게이트·핵심 체결 흐름 로그는 보호하고, 저우선/고빈도 로그만 자동 드롭
  - 드롭 발생 시 `[PERF][LIVELOG][BACKPRESSURE]` 경고를 주기적으로 남겨 과부하 상태 추적 가능

## [2.4.49] - 2026-03-15

### Added

- **[Time-Out Probability Entry] 신규 추가** (`TimeOutProbabilityEngine.cs`, `TradingEngine.cs`):
  - 60분간 진입 공백 발생 시 과거 DB 패턴과 **코사인 유사도** 비교로 승률 70%+ 메이저 코인 자동 확률 베팅 진입
  - 피처 벡터: [RSI/100, BB위치, MACD히스토(정규화), 거래량비율] 4차원 추출 → `GetLabeledTradePatternSnapshotsAsync` 최근 180일 최대 500건 조회
  - 유사도 ≥65% 필터 후 상위 1,000건 PnL%≥+3% 승률 계산 — 70%+ 시 시드 40% 비중 진입 (`TIMEOUT_PROB_ENTRY` 신호소스)
  - 스캔 순서: ETH → XRP → SOL, 최초 조건 충족 종목만 진입 후 즉시 종결
  - `IsDroughtRecoverySignalSource`에 `TIMEOUT_PROB_ENTRY` 추가 → AI 게이트 바이패스 적용
  - 타이머: 60분 공백 감지 / 20분 재시도 제한 (`TimeOutProbScanThreshold`, `TimeOutProbScanInterval`)

### Changed

- **[UI] TimeOut Probability Entry 위젯 위치** (`MainWindow.xaml`):
  - AI MONITOR 탭 → **Dashboard 좌측 사이드바** (AI LEARNING 카드 아래, CRITICAL ALERTS 위)로 이동
  - 항시 가시 영역에 배치: 스캔 상태, 매칭 심볼, 승률 ProgressBar, 70%+ 시 보라색 Glow 애니메이션

- **[UI] FAST LOG 위치 조정** (`MainWindow.xaml`):
  - 중앙 LIVE BATTLE 패널 Grid.Row=2 → **좌측 XRP SCENARIO 카드 바로 아래**로 이동
  - 중앙 패널 RowDefinitions 4→3, Trend-Rider Grid.Row 3→2 자동 조정

## [2.4.48] - 2026-03-15

### Added

- **Staircase Pursuit (계단식 추격) 로직 신규 추가** (`TradingEngine.cs`, `AIDoubleCheckEntryGate.cs`, `EntryRuleValidator.cs`):
  - `IsStaircaseUptrendPattern()`: 15봄봉 3연속 Higher Lows + BB %B > 45% + RSI<80 조건으로 계단식 상승 패턴 자동 감지
  - `ShouldBlockChasingEntry`: 계단식 감지 시 `nearRecentHigh` riskScore 차단 면제 → 고점 추격 필터 우회
  - `AIDoubleCheckEntryGate.DetectStaircaseUptrend()`: BB %B > 50% + 3연속 Higher Lows 감지 시 `Chasing_Risk_Pullback` 필터 바이패스
  - `EntryRuleValidator.HasSuccessiveHigherLows()`: 점자 거래량 코인의 저점 상승을 신뢰할 수 있는 추세로 인정, `LowVolumeRejectRatio` 필터 바이패스

- **계단식 정찰대 20% 즉시 투입** (`TradingEngine.cs`):
  - TF≥85% + 계단식 상승 감지 시 눈맼목을 기다리지 않고 성리의 **20%** 시장가 진입 (`_STAIRCASE` 태그)
  - ATR 3.5x 하이브리드 손절 대기열 자동 등록

- **불타기 Add-on 개선** (`TradingEngine.cs`):
  - 계단식 상승 중 최근 5봉 고점 돌파 시 기존 70% 대신 **20% 추가 진입** (`STAIRCASE_ADDON`) 적용

- **Trend-Rider 위젯 첨가** (`MainWindow.xaml`, `MainViewModel.cs`):
  - LIVE BATTLE DASHBOARD 하단에 `🪜 TREND-RIDER` 위젯 신규 삽입
  - TF≥85% + BB중단 이상: 구뚤금세 `STAIRCASE UPTREND (활성)` + `ACTIVE` 배지 + 폄스 애니메이션
  - TF≥65%~85%: `STAIRCASE MONITORING` (하늘색)
  - 비활성: 회색 `한산드 대기`

## [2.4.47] - 2026-03-15

### Changed

- **CRITICAL ALERTS 패널 배치 조정으로 Market Signals 가시성 개선** (`MainWindow.xaml`):
  - 하단 메인 영역의 `CRITICAL ALERTS` 패널을 좌측 사이드바 카드 영역으로 이동
  - 메인 하단 row 점유를 제거해 `MARKET SIGNALS` 리스트 표시 공간을 복원

## [2.4.46] - 2026-03-15

### Added

- **저거래량 메이저 롱 정찰/증원 진입 시퀀스 추가** (`TradingEngine.cs`, `EntryRuleValidator.cs`, `AIDoubleCheckEntryGate.cs`):
  - `RuleViolationLowVolumeRatio` 차단 상황에서도 TF 강세 + BB 중심선 지지 패턴일 때 30% 정찰 진입(`_SCOUT`) 허용
  - 정찰 체결 후 거래량/ML/TF 회복 시 70% 추가 진입(`_SCOUT_ADDON`) 1회 트리거
  - 저거래량 예외 파라미터(`LowVolumeBypass*`)를 설정 모델에 반영해 우회 조건을 일관되게 관리

- **월 목표 추적 위젯(Profit Goal Tracker) 추가** (`MainWindow.xaml`, `MainViewModel.cs`):
  - 우측 상단 대시보드에 `MONTHLY GOAL`, `Current(금액/달성률)`, `Daily Pace`, `XRP Scenario Bonus` 표시
  - 당월 누적 실현손익 기반 달성률/일일 페이스 계산 및 XRP 활성 포지션 TP 기준 보너스 수익 표시

### Changed

- **정찰 진입 리스크 관리 강화** (`TradingEngine.cs`):
  - 정찰 진입(`SCOUT`)에도 메이저 ATR 하이브리드 손절 계산을 강제 적용
  - 손절 계산 실패 시 정찰 진입 자체를 차단하여 무손절 진입 가능성 제거

- **사용자 스코프 DB 마이그레이션 멱등성 보강** (`run-user-scope-db-migration.ps1`):
  - `GeneralSettings` 테이블이 이미 존재하는 환경에서 `GeneralSettings_Schema.sql`을 자동 스킵하도록 개선
  - 운영 DB 재실행 시 “이미 개체 존재” 오류를 줄여 배포 안정성 향상

- **HelloQuant 패턴 카드 가시성 강화** (`MainWindow.xaml`, `MainViewModel.cs`):
  - BB 중심선 지지 활성 시 펄스 애니메이션, TF Confidence 진행바, ML/TF 가중치(50:50 ↔ 30:70) 실시간 반영

## [2.4.45] - 2026-03-15

### Changed

- **LIVE BATTLE DASHBOARD 가격 텍스트 깜빡임 제거** (`MainWindow.xaml`):
  - `BattleLivePriceText` 바인딩의 `TargetUpdated` 이벤트 트리거 애니메이션(Opacity/Translate)을 제거해 틱 단위 가격 갱신 시 시각적 깜빡임을 해소
  - 실시간 가격/방향(`BattlePriceBrush`, `BattlePriceDirectionText`) 갱신 로직은 유지하여 정보 전달은 동일하게 보장

## [2.4.44] - 2026-03-14

### Added

- **HelloQuant 스타일 배틀 대시보드 3열 레이아웃** (`MainWindow.xaml`, `MainViewModel.cs`, `Converters.cs`):
  - 기존 단일 패널 `⚔ LIVE BATTLE DASHBOARD`를 Market Monitor / Main Scene / Execution Stepper 3열 구조로 전면 교체
  - 좌열: AI Confirm 상태 + 4코인 모니터(BTC/ETH/SOL/XRP) + XRP 시나리오 라벨
  - 중앙열: 실시간 가격 롤링 애니메이션(TargetUpdated EventTrigger) + 도넛형 Pulse 게이지(Canvas/Path Arc) + 0.618 Golden Zone 카드 + ATR Stop Cloud 카드 + 패스트 로그
  - 우열: 4단계 Execution Stepper(ItemsControl) — Market Sync → Wave AI Gate → 0.618 Golden Zone → ATR Close-Only

- **ScoreToArcGeometryConverter** (`Converters.cs`):
  - 0~100 Pulse 점수를 도넛 게이지 SVG 호(Arc) Path Data로 변환하는 IValueConverter 추가
  - ConverterParameter 구분자를 `|`(파이프)로 변경하여 XAML MarkupExtension 파싱 충돌 방지

- **BattleExecutionStep 모델 및 ViewModel 바인딩** (`MainViewModel.cs`):
  - `BattleExecutionStep : INotifyPropertyChanged` 모델 클래스 추가 (Order, Title, Detail, StateText, StateBrush, IsActive)
  - `BattleExecutionSteps` ObservableCollection과 `InitializeBattleExecutionSteps` / `UpdateBattleExecutionSteps` / `SetBattleStep` 로직 추가
  - `BattleGoldenZoneText/Brush`, `BattleAtrCloudText/Brush`, `BattleStopPulseActive` 프로퍼티 신설
  - ATR Stop 거리 ≤ 0.7% 진입 시 OrangeRed 펄스 경보, ≤ 1.5% Gold 중간 경고, 초과 시 DeepSkyBlue

### Changed

- **ATR 펄스 경보 임계값 확대** (`MainViewModel.cs`):
  - 빨강 펄스 발동 기준: 스탑까지 거리 0.35% → **0.7%** (넉넉한 사전 경보)
  - Gold 중간 경고 기준: 0.9% → **1.5%**

- **메이저 ATR 2.0 Close-Only 심볼 로직 강화** (`PositionMonitorService.cs`, `TradingEngine.cs`):
  - ETH/XRP/SOL에 `isAtr20MajorSymbol` 플래그를 적용, `useMajorAtr20=true` 경로로 손절 계산
  - TP1/TrailStart/Gap 파라미터 완화: ETH/XRP/SOL 40%/60%/10%, BTC 15%/30%/3%
  - `minLockROE`: ATR2.0 메이저 +2%, 일반 +5%
  - TradingEngine ATR 멀티플라이어 BTC 2.5→3.5, XRP/SOL 4.0, swingCandles 12→10봉
  - `hybridStopPrice` 롱/숏 방향 버그 수정 (Max↔Min 반전)

## [2.4.43] - 2026-03-14

### Changed

- **환경설정 General 탭 레이아웃 정리** (`SettingsWindow.xaml`):
  - 공통 / PUMP / PUMP 추세홀딩 / 메이저 섹션 순서로 재배치
  - 중복·혼동되던 라벨을 정리하고 `메이저 추세 프로파일`을 메이저 전용 섹션으로 이동
  - 공통 `목표 ROE`, `손절 ROE` 라벨을 알트 기준으로 명확화

- **메이저 ATR 손절을 듀얼 스탑 상태머신으로 보강** (`PositionMonitorService.cs`):
  - 진입 후 5분 ATR 게이트, ATR 터치 후 3분 whipsaw 보류, 직전 5캔들 Fractal 지지선 확인 추가
  - Fractal 이탈 시 최근 20캔들 평균 대비 거래량 1.5배 확인 후에만 즉시 손절
  - 저거래량 fake-out은 1분 추가 관망 후 타임아웃 손절하며, 회복 시 ATR 경보 상태를 자동 초기화

## [2.4.42] - 2026-03-14

### Added

- **실전 유사 백테스트 모드 `LiveEntryParity` 추가** (`BacktestService.cs`, `HybridStrategyBacktester.cs`, `BacktestOptionsDialog.xaml(.cs)`):
  - 백테스트 전략 선택에 `LiveEntryParity` 옵션을 추가하고 기본 선택으로 변경
  - `HybridStrategyBacktester.RunRangeAsync`와 `BacktestResult.Candles` 연계로 기간 지정 실전형 시뮬레이션 지원
  - `PerfectAI=false` 경로에서 룩어헤드 제거형 예측 추정(`EstimatePredictedChangeNoLookahead`) 적용

- **15분봉 BB 스퀴즈 → 중심선 돌파 전략 추가** (`FifteenMinBBSqueezeBreakoutStrategy.cs`, `TradingEngine.cs`):
  - BB 폭 수축(스퀴즈) 후 중심선 상향 돌파 시 LONG 진입하는 독립 전략 클래스 신설
  - 진입 조건: BB 폭 < 평균×0.80, 양봉, 거래량 1.2배 이상, RSI 40~65, RR ≥ 1.5
  - TP = BB Upper, SL = BB Lower 또는 ATR×1.5 중 낮은 값, 쿨다운 4시간
  - `TradingEngine.AnalyzeFifteenMinBBSqueezeBreakoutAsync`를 심볼 분석 루프에 연동

- **Elliott Wave 앵커 DB 영속화** (`ElliottWaveAnchorState.cs`, `WaveAnchor.cs`, `DbManager.cs`, `TradingEngine.cs`):
  - 앱 재시작 후에도 1·2파 기준점(`WaveAnchor`)을 DB에서 복원하는 영속화 레이어 추가
  - `TradingEngine.RestoreElliottWaveAnchorsFromDatabaseAsync` / `PersistElliottWaveAnchorStateAsync`로 상태 변경 시 자동 저장
  - 파동 phase 서명 비교(`BuildElliottWavePersistenceSignature`)로 실제 변경된 경우에만 DB 기록

- **PUMP 추세홀딩 튜닝 설정 추가** (`SettingsWindow.xaml(.cs)`, `DbManager.cs`, `Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql`):
  - `PumpFirstTakeProfitRatioPct` (1차 익절 비중%), `PumpStairStep1/2/3Roe` (계단식 트레일 ROE) 4개 설정 신설
  - 설정창 General 탭에 PUMP 추세홀딩 튜닝 섹션 UI 추가 및 DB 로드/저장 연동
  - 운영 DB 마이그레이션 스크립트(`GeneralSettings_AddPumpStairAndTpRatioColumns.sql`) 및 메이저 레거시 값 보정 스크립트(`GeneralSettings_NormalizeMajorDefaults_20260314.sql`) 제공
  - 자동 마이그레이션 실행 스크립트(`run-user-scope-db-migration.ps1`) 포함

### Changed

- **엘리엇 3파 진입 기준 재설계** (`ElliottWave3WaveStrategy.cs`, `TradingEngine.cs`):
  - 1파 감지를 단일 캔들 기반에서 스윙 저점/고점 기반으로 전환
  - 2파 확정에 되돌림 비율(0.50~0.786), 거래량 수축, 반등 확인 조건을 결합
  - 피보 진입 판정을 `Fib 구간 터치 + 0.618 재돌파` 중심으로 정밀화하고 무효화 시 상태 리셋
  - 주문 체결 후 실제 포지션 확인 시 `Wave3Active`로 상태 승격

- **드라이스펠 자동 복구 진입/차단 관제 강화** (`TradingEngine.cs`):
  - 장시간 무진입 시 전 심볼 진단 후 `PASS` 후보 자동진입, 근접 후보 시험진입 경로 보강
  - 드라이스펠 전용 소스(`DROUGHT_RECOVERY*`)에 한해 사전평가 결과를 재사용하도록 AI 게이트 재검사 우회
  - 10분 단위 진입 차단 사유 집계 및 튜닝 힌트 로그 추가

- **심볼 정규화 및 UI 동기화 안정화** (`TradingEngine.cs`, `MainViewModel.cs`):
  - 영숫자+`USDT` 형식 정규화를 티커/시그널/포지션 업데이트 경로에 통합
  - 비정상 심볼 유입 시 UI 인덱스 오염/갱신 누락 가능성을 축소

- **AI 임계값/진입필터 공격형 튜닝 반영** (`AIDoubleCheckEntryGate.cs`, `TradingEngine.cs`, `appsettings.json`):
  - 일반/펌핑 코인 ML·TF 임계값 완화 및 엔트리 점수/RSI 기준 조정
  - EntryFilter 워밍업, RR, 15분 게이트 기본값을 실전 진입 빈도 중심으로 재조정

- **메이저 고속도로 Fast-Pass 적용** (`TradingEngine.cs`):
  - 메이저 코인(BTC/ETH/SOL/XRP) AI 점수 임계 56%, 신뢰도 0.56로 하향(기존 70/0.70 대비 0.8배)
  - 정배열(SMA20>60>120) 추세 시 BB 상단 저항·진입 구간(45~85%) 바이패스 허용
  - RSI 추격 한도 75 → 80으로 확장 및 진입 로그에 바이패스 경로 명시

- **확정 피보나치 레벨 계산 추가** (`TradingEngine.cs`):
  - `CalculateConfirmedFibLevels`: 최근 3캔들 미포함 확정 고·저점 기반 Fib 수준 계산
  - 피보 레벨 산출 실패 시 기존 `IndicatorCalculator.CalculateFibonacci` 결과로 폴백

## [2.4.41] - 2026-03-13

### Added

- **드라이스펠 자동 복구 진입 추가** (`TradingEngine.cs`):
  - 무진입 구간 진단 결과에서 `Gate PASS` 상위 후보를 자동 진입 대상으로 연결
  - `PASS` 후보가 없을 때 임계값 근접 후보를 소액 시험 진입으로 시도하는 폴백 경로 추가
  - 드라이스펠 복구 진입 소스 태그(`DROUGHT_RECOVERY`, `DROUGHT_RECOVERY_NEAR`) 반영

### Changed

- **드라이스펠 감지/재진단 파라미터 공격형 튜닝** (`TradingEngine.cs`):
  - 감지 임계 `1시간` → `30분`, 재진단 주기 `15분` → `10분`
  - 근접 후보 판정 기준 `95%` → `90%`, 시험 진입 비중 `40%` → `70%`
  - `Gate PASS` 자동진입 시도 개수 `Top 1` → `Top 2`로 확장

- **자동진입 수량 제어 훅 추가** (`TradingEngine.cs`):
  - `ExecuteAutoOrder`에 수동 배수 인자(`manualSizeMultiplier`)를 추가해 진입 비중을 동적으로 제어
  - 배수 안전 범위 클램프(0.10~2.00) 및 사이징 로그 강화

### Fixed

- **심볼 깨짐/오염 입력 정규화 보강** (`TradingEngine.cs`, `MainViewModel.cs`):
  - 실시간 심볼 유입 경로 전반에서 영숫자+`USDT` 형식 정규화를 적용해 UI 깨짐 심볼 노출 완화
  - 숫자 접두 심볼(`1000PEPEUSDT` 등) 수용을 위한 패턴 정합성 개선

## [2.4.40] - 2026-03-13

### Added

- **사용자별 DB 스코프 마이그레이션 체크리스트 추가** (`USER_SCOPE_DB_MIGRATION_CHECKLIST.md`):
  - 운영 적용 전/후 검증 쿼리, 스모크 테스트, 롤백 절차를 문서화

### Changed

- **AI 관제탑 5분 요약 전송 규칙 보강** (`TradingEngine.cs`, `TelegramService.cs`):
  - 대상 코인 결정이 없어도 5분 요약은 고정 주기로 발송되도록 강제
  - 승인코인 5분 브리핑은 실제 승인 대상 심볼이 있을 때만 발송

- **사용자 스코프 기반 DB 읽기/쓰기 경로 강화** (`DbManager.cs`, `DatabaseService.cs`, `Database/TradeLogging_Schema.sql`, `Database/GeneralSettings_Schema.sql`):
  - `TradeLogs`/`TradeHistory` 및 고급 로그 경로에 `UserId` 기반 필터/저장 보강
  - 스키마 호환 체크(`HasColumnAsync`)와 인덱스/뷰 정합성 보완

- **엔진 사용자 컨텍스트/ROI 안전성 개선** (`TradingEngine.cs`):
  - DB 정리/블랙리스트 복구 시 현재 로그인 사용자 기준으로만 조회
  - 실시간 ROI 계산에서 무효 가격 및 레버리지 값에 대한 안전 가드 적용

## [2.4.39] - 2026-03-13

### Added

- **스나이퍼 모드 도입** (`SniperManager.cs`, `HybridStrategyScorer.cs`, `Models.cs`):
  - 종목별 일일 진입 횟수/쿨다운/최대 포지션/추세 일치 검증 기반의 진입 게이트 추가
  - `TradingSettings`에 스나이퍼 설정값(`MinimumEntryScore`, `MaxTradesPerSymbolPerDay`, `EntryCooldownMinutes` 등) 확장

- **AI 관제탑 승인코인 5분 브리핑 분리 발송** (`TelegramService.cs`):
  - 기존 5분 요약과 별도로 승인 코인만 모아 `[AI 관제탑 승인코인 5분 브리핑]` 메시지 발송
  - 승인 코인/사유를 LONG·SHORT·기타 버킷으로 분리해 운영 가독성 강화

### Changed

- **메이저/PUMP 설정 분리 및 저장 경로 확장** (`Models.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `DbManager.cs`, `Database/GeneralSettings_Schema.sql`, `GeneralSettingsProvider.cs`):
  - 메이저/펌프 전용 레버리지·증거금·본절·트레일링·손절 항목을 UI/모델/DB 스키마에 반영
  - `GeneralSettings` 저장/로딩 경로를 신규 필드까지 일관되게 확장

- **진입/스코어링 로직 보강** (`AIDoubleCheckEntryGate.cs`, `TradingEngine.cs`, `HybridStrategyScorer.cs`, `TransformerStrategy.cs`, `PumpScanStrategy.cs`, `MajorCoinStrategy.cs`):
  - Fib 보너스/데드캣 차단, RSI/MACD 기울기 가점, 메이저 스코어 임계값 보강 등 진입 필터 강화
  - 메이저 코인 진입 시 상위 타임프레임 기울기 확인 및 심볼별 임계 프로파일 적용

- **Fib 가점 반영 TrendScore를 관제탑 가시화에 직접 반영** (`AIDoubleCheckEntryGate.cs`, `TradingEngine.cs`, `TelegramService.cs`):
  - AI 게이트 PASS/BLOCK reason에 `Trend`/`FibBonus` 메타데이터 포함
  - 엔진 `AI_GATE` 로그에 `trend`, `fibBonus` 필드 추가
  - 5분 요약에 `평균 Trend(피보반영)` 지표 추가

- **AI 관제탑 사유 정규화 강화** (`TelegramService.cs`):
  - `DeadCat_Block` 계열을 `데드캣 붕괴 차단`으로 통일
  - `FibBonus>0` + `Dual_Reject` 상황을 `피보 가점 반영 후 ML/TF 미달`로 명시
  - 승인 사유에서 `FibBonus>0` 감지 시 `피보 지지+리버설 가점 통과`로 표기

- **승인코인 5분 브리핑 노이즈 감소** (`TelegramService.cs`):
  - 승인 건수 0건일 때 승인코인 별도 브리핑 전송을 자동 스킵

- **포지션/UI 동기화 안정화** (`MainViewModel.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `PositionMonitorService.cs`, `BinanceExchangeService.cs`):
  - 리스크 표시 및 정렬 우선순위 반영 강화, 동기화 시 레버리지/모니터 분기 안전성 보강
  - 거래소 정밀도 캐시 및 종료/취소 예외 처리 개선

## [2.4.38] - 2026-03-12

### Added

- **심볼 라이브 차트 고도화** (`MainViewModel.cs`, `SymbolChartWindow.xaml`, `SymbolChartWindow.xaml.cs`, `MainWindow.xaml.cs`):
  - 차트 창 로드/재오픈 시 `RefreshLiveChart()` 자동 호출로 즉시 최신 데이터 갱신
  - 5분봉 기반 선형 회귀 `1H 예측` 라인(12개 캔들) 추가
  - LONG/SHORT 진입, 익절, 청산 가격 수평선 오버레이 추가
  - AI 진입예상 시각/확률을 다이아몬드 마커로 차트에 표시
  - 차트 범례(`ChartLegend`) 추가로 시리즈 식별성 개선

### Changed

- **하이브리드 진입 캔들 확인 우회 조건 추가** (`TradingEngine.cs`):
  - 심볼별 최신 AI 예측 캐시(`_latestAiForecasts`)를 유지
  - AI 확률 `80% 이상` + `즉시/5분 이내` 예측일 때 캔들 확인 지연 진입을 스킵하고 즉시 진입
  - 조기진입 시 기존 pending delayed entry를 정리하고 EARLY/BYPASS 로그를 남기도록 개선

- **WPF 임시 프로젝트 파일 정리** (`TradingBot_2pdrx2mn_wpftmp.csproj`):
  - 빌드 산출 과정에서 생성된 임시 `.wpftmp.csproj` 파일 삭제

## [2.4.37] - 2026-03-11

### Added

- **[Meme Coin Mode] PUMP 전용 포지션 관리 전략 추가** (`Models.cs`, `PositionMonitorService.cs`, `GeneralSettingsProvider.cs`, `DbManager.cs`):
  - `TradingSettings`에 PUMP 전용 3개 파라미터 추가:
    - `PumpBreakEvenRoe` (기본 20%) — ROI +20% 도달 시 손절가를 진입가로 이동, 절대 손실 방지
    - `PumpTrailingStartRoe` (기본 40%) — ROI +40% 돌파 시 트레일링 스탑 감시 시작
    - `PumpTrailingGapRoe` (기본 20%) — 최고점 대비 ROI 20% 하락 시 전량 청산 (어깨 매도)
  - 본절 트리거 조건을 하드코딩 3%에서 `PumpBreakEvenRoe` 설정값으로 변경
  - 기존 10%→4%, 15%→5% 동적 트레일링 압축 제거 — 밈코인 변동성에 비해 너무 좁아 조기 청산 유발
  - ATR 동적 계산 시 `PumpTrailingStartRoe` 이하로 내려가지 않도록 하한선 적용
  - `GeneralSettingsProvider`에 3개 프로퍼티 노출, `DbManager` MERGE SQL에 신규 컬럼 포함
  - Break-Even 발동 시 텔레그램 알림 자동 발송

- **진입 코인 그리드 상단 자동 정렬** (`MainViewModel.cs`):
  - 포지션 진입 시 해당 코인이 그리드 맨 위로 자동 이동
  - `ICollectionViewLiveShaping.IsLiveSorting` 적용으로 상태 변경 시 실시간 재정렬

- **ENTRY STATUS 컬럼 실시간 갱신** (`Models.cs`, `MainViewModel.cs`):
  - `ResolveEntryStatus()` 로직 추가 — 진입중/평가중/펌핑감시/TF감시/메이저감시/신호감시/대기 상태 표시
  - `IsPositionActive` 변경 시 `EntryStatus`, `EntryStatusColor`, `EntryStatusIcon` 연계 갱신

### Changed

- **AI 관제탑 요약 주기 15분 → 5분** (`TradingEngine.cs`, `TelegramService.cs`):
  - 타이머 조건 `TotalMinutes >= 15` → `>= 5`로 단축
  - 관련 주석 및 description 4곳 동기화

- **진입 텔레그램 알림 통합** (`TelegramService.cs`, `TradingEngine.cs`):
  - `SendSmartTargetEntryAlertAsync` 제거, `SendEntrySuccessAlertAsync` 단일 메서드로 통합
  - Smart SL/TP/ATR 정보 유무에 관계없이 1건만 발송 (중복 제거)
  - Smart Target 계산 실패 시에도 기본 진입 알림 발송 보장

## [2.4.36] - 2026-03-11

### Changed

- **AI 진입 최종 필터 4단계 고도화** (`EntryRuleValidator.cs`, `AIDoubleCheckEntryGate.cs`):
  - 최종 방향 필터 입력을 `bool isLong`에서 `PositionSide`로 변경해 LONG/SHORT 분기 명확화
  - LONG 구간에서 `Fib >= 0.786`일 때, `RSI <= 25 && BB <= 0.05`가 아니면 가짜 바닥으로 차단
  - SHORT 구간에서 `Fib <= 0.236 && RSI >= 75 && BB >= 0.95`일 때 과열 반등 위험으로 차단
  - Rule1/Rule2 즉시 차단, Rule3 감점(0.15), SuperTrend 우회(ML/TF) 및 최종점수 임계(0.65) 체계 유지

- **손절 ROE 설정값 실시간 반영 경로 보강** (`PositionMonitorService.cs`, `TradingEngine.cs`):
  - 포지션 모니터가 시작 시 스냅샷 `_settings` 고정값이 아닌 현재 사용자 설정 공급 경로를 통해 손절 ROE를 조회
  - 서버사이드 스탑 계산/메이저 손절 트리거/로그 및 PUMP 손절 초기값에 동일한 동적 손절값을 적용
  - `TradingEngine`에서 `PositionMonitorService` 생성 시 런타임 설정 provider delegate를 주입

## [2.4.35] - 2026-03-11

### Changed

- **AI 관제탑 15분 요약 메시지 포맷 고도화** (`TelegramService.cs`):
  - 승인/차단 코인 목록에 사유를 함께 표시하도록 확장 (`코인(사유)`)
  - 승인/차단 목록을 `LONG / SHORT / 기타`로 분리해 가독성 개선
  - 목록 노출 개수를 제한하고 초과분은 `외 N개`로 축약 표기
  - 차단 사유 TOP을 `전체 / LONG / SHORT / 기타`로 분리 집계 및 표시
  - 요약 타이틀/본문 레이아웃 정리 (`AI 관제탑 15분 요약`)

## [2.4.34] - 2026-03-11

### Changed

- **Elliott Wave Rule3 감점제 전환** (`EntryRuleValidator.cs`, `AIDoubleCheckEntryGate.cs`):
  - Rule3 위반을 하드 차단에서 `-0.15` 감점 방식으로 변경 — 위반 시에도 최종 점수가 임계값(0.65) 이상이면 진입 허용
  - 강추세 우회 조건 추가: TF ≥ 0.84 AND ML ≥ 0.85 시 Rule3 감점 및 Fibonacci 극단 차단 무시
  - 방향별 Fibonacci 극단 필터 구현 (Long: Fib ≥ 0.786 + RSI < 25 + BB < 0.05 차단 / Short: Fib ≤ 0.236 + RSI > 75 + BB > 0.95 차단)
  - 모든 튜닝 임계값을 `DoubleCheckConfig` 프로퍼티로 외재화 (`ElliottRule3Penalty`, `RuleFilterFinalScoreThreshold`, `SuperTrendTfThreshold` 등)

- **텔레그램 AI 관제탑 15분 집계 요약 전환** (`TelegramService.cs`, `TradingEngine.cs`):
  - AI 관제탑 판정을 실시간 개별 전송 → 15분 집계 배치 요약으로 전환하여 텔레그램 스팸 감소
  - `AiGateSummaryWindow` 내부 클래스로 승인/차단/강추세/방향/타입별 카운터 및 ML/TF 평균 누적
  - `FlushAiGateSummaryAsync()` 추가 — 15분마다 요약 메시지 전송, 판정 0건이면 생략
  - TradingEngine 메인 루프에 15분 타이머 연결 (`_lastAiGateSummaryTime`)
  - 심볼별 1분 중복 집계 방지는 유지 (ML < 0.50 저신뢰 차단 건 집계 생략 포함)

### Improved

- **메인 UI 레이아웃 개선** (`MainWindow.xaml`):
  - 좌측 사이드바 너비 320 → 340px, 각 카드 헤더에 컬러 세로 강조선 추가
  - 시계 카드를 컴팩트 가로 레이아웃으로 변경 (32pt → 24pt, 공간 절약)
  - Entry Gate 카드 리디자인: ML.NET / Transformer 각각 다크 배경 박스 + 컬러 바 시각 구분
  - 마켓 그리드 `AI시각` 컬럼 제거, 컬럼 너비 최적화 (총 ~150px 절약), 행 높이 48 → 44px
  - Critical Alerts 패널 높이 200 → 120px (마켓 그리드 공간 확보)

## [2.4.33] - 2026-03-11

### Fixed

- **청산 API 장애 시 자동 복구 강화** (`PositionMonitorService.cs`):
  - reduceOnly 청산 주문 3회 실패 후 종료하지 않고, **3초 주기 무한 재시도 루프**로 전환
  - 재시도 루프에서 reduceOnly 재시도 + 긴급 폴백 청산을 병행하여 실제 포지션 0 확인까지 복구 지속
  - 복구 완료 시 최종 청산 검증 및 후속 DB 저장 흐름으로 정상 복귀

- **청산 장애 텔레그램 관제 추가** (`PositionMonitorService.cs`):
  - 청산 API 오류로 무한 재시도 모드 진입 시 Telegram 긴급 알림 전송
  - 재시도 루프 복구 완료 시 Telegram 복구 완료 알림 전송

### Changed

- **수동 청산 UI 메시지 현실화** (`MainViewModel.cs`):
  - 수동 청산 버튼 실행 시 "명령 전송됨" 고정 문구 대신,
    실패 시 로그/동기화 확인이 필요함을 안내하는 문구로 변경

## [2.4.32] - 2026-03-11

### Fixed

- **배포본 AI 관제탑 모델 영속성 수정** (`EntryTimingMLTrainer.cs`):
  - AI 게이트 ML 모델 저장 위치를 실행 폴더 기준에서 `%LOCALAPPDATA%/TradingBot/Models` 기준으로 변경
  - 설치형 배포본에서 모델 파일이 버전 폴더 교체/권한 문제로 유지되지 않던 문제를 완화
  - 기존 실행 폴더의 `EntryTimingModel.zip`이 있으면 사용자 경로로 자동 마이그레이션되도록 보강
  - 결과적으로 배포 버전에서도 초기 학습 완료 후 `AI 관제탑` 메시지가 지속적으로 동작하도록 개선

- **AI 관제탑 텔레그램 표시 개선** (`TelegramService.cs`):
  - `TF흐름` 값을 raw 소수(`0.94`) 대신 퍼센트(`94.0%`)로 표시하도록 보정
  - RSI와 동일하게 사람이 즉시 해석 가능한 표시 형식으로 통일

### Changed

- **PUMP 스캔 후보 확대 및 시장 전체화** (`PumpScanStrategy.cs`):
  - 고정 밈 리스트 대신 전체 `USDT` 선물 마켓을 후보 우주로 사용하도록 변경
  - 혼합 랭킹(거래대금 50% + 변동성 20% + 모멘텀 30%) 기준 상위 후보 수를 **10개 → 20개**로 확대
  - `ICONUSDT`, `BSVUSDT`처럼 급등 중인 일반 알트도 상위권이면 후보에 포함될 수 있도록 수정

## [2.4.31] - 2026-03-11

### Fixed

- **텔레그램 직접 발송 경로 핫픽스** (`TelegramService.cs`):
  - AI 관제탑 / 본절 / 트레일링 알림도 공통 안전 발송 경로를 사용하도록 통합
  - Markdown 파싱 오류 발생 시 plain text로 자동 재시도하도록 수정
  - 배포 환경에서 발송 실패 원인 추적을 위해 `telegram_error.log` 파일 로그 추가
  - 일부 텔레그램 알림이 조용히 누락되던 문제를 완화

## [2.4.30] - 2026-03-11

### Fixed

- **AI 관제탑 텔레그램 RSI 표시 보정** (`TelegramService.cs`):
  - 내부 AI feature 값이 `0.0~1.0` 범위로 정규화되어 들어오는 경우, 텔레그램 알림에서는 자동으로 `0~100` 스케일로 환산해 표시하도록 수정
  - AI PASS/BLOCK 알림에서 `RSI: 0.5`처럼 오해를 주던 값을 `RSI: 50.0` 형태로 표시하도록 보정
  - 실제 AI 게이트 판단 로직은 유지하고, 사용자에게 보이는 관제 메시지의 해석 가능성만 개선

## [2.4.29] - 2026-03-11

### Added

- **Smart Target ATR TP/SL 시스템** (`HybridExitManager.cs`):
  - `ComputeSmartAtrTargets(price, isLong, atr, leverage=20)` 정적 메서드 추가
    - SL = ATR × 1.5 (최대 -3.5% 캡), TP = ATR × 3.0 (손익비 1:2)
    - ATR 계산 불가 시 폴백: SL -3.5%, TP +7%
  - `RegisterEntry` 오버로드 추가 (`initialSL`, `initialTP` 파라미터 지원)
  - `HybridExitState` 신규 필드: `InitialSL`, `InitialTP`, `BreakEvenTriggered`, `LastMilestoneROE`
  - 본절 전환 임계값 ROE 15% → **10%** 하향 조정 (20× 레버리지 0.5% 가격 이동 기준)
  - `OnBreakEvenReached: Action<string, decimal>?` 이벤트 추가
  - `OnTrailingMilestone: Action<string, decimal, double, string>?` 이벤트 추가
  - 마일스톤 이벤트: ROE 10 / 20 / 50 / 100% 도달 시 발동 (중복 방지)

- **Smart Target 텔레그램 알림** (`TelegramService.cs`):
  - `SendSmartTargetEntryAlertAsync`: 진입 시 ATR 기반 SL/TP 요약 발송 (SL%, TP%, ROE 환산 포함)
  - `SendBreakEvenReachedAsync`: 본절 전환 무음 알림
  - `SendTrailingMilestoneAsync`: ROE 마일스톤 알림 (ROE ≥ 20% 소리, 미만 무음)

- **TradingEngine 통합** (`TradingEngine.cs`):
  - `OnBreakEvenReached` → `TelegramService.SendBreakEvenReachedAsync` 이벤트 연결
  - `OnTrailingMilestone` → `TelegramService.SendTrailingMilestoneAsync` 이벤트 연결
  - 진입 후 비동기 Task: 15분 캔들 20봉 조회 → ATR(14) 계산 → `ComputeSmartAtrTargets` → `exitState` 업데이트 → Telegram 발송

## [2.4.28] - 2026-03-11

### Added

- **Dual-Gate 아키텍처 재설계 (Brain → Filter 순서 확립)**:
  - `EvaluateEntryAsync` 흐름을 ML→TF(기존)에서 **TF(Brain) → ML(Filter)** 순서로 재정렬
  - TensorFlow.NET(Transformer)이 1단계: 캔들 패턴/흐름 점수(TrendScore) 산출
  - ML.NET(LightGBM)이 2단계: 지표 기반 최종 필터 승인
  - 로그 태그: `[BRAIN]` (TF 승인), `[FILTER_BLOCK]` (ML/Sanity 거부)

- **Sanity Risk Filter (`EvaluateDualGateRiskFilter`) 추가**:
  - ML 통과 후 실행되는 하드코딩 규칙 기반 안전망
  - RSI ≥ 80: 과열 하드차단 (`RSI_Overheat_HardBlock`)
  - Upper Wick Ratio ≥ 70%: 윗꼬리 과다 차단 (`UpperWick_Risk`)
  - BB 상단 ≥ 90% + RSI ≥ 70 + TrendScore < 0.80: 과열 상단 차단 (`UpperBand_Overheat`)
  - 최근 고점 대비 하락폭 ≤ 0.20%: 추격 진입 차단 (`Chasing_Risk`)
  - **StrongTrend 우선통과**: TrendScore ≥ 0.80이면 BB/RSI 주의 조건 바이패스

- **`AIEntryDetail.TrendScore` 필드 추가**: Sanity 필터에서 TrendScore 전달 및 로그 기록

- **`DoubleCheckConfig` 튜닝 파라미터 6종 추가**:
  - `StrongTrendBypassThreshold = 0.80f`
  - `RsiOverheatHardCap = 80f`
  - `RsiCautionThreshold = 70f`
  - `BbUpperRiskThreshold = 0.90f`
  - `UpperWickRiskThreshold = 0.70f`
  - `RecentHighChaseThresholdPct = 0.20f`

### Changed

- **PUMP 코인 진입 기준 강화 (손실 축소)**:
  - `MAX_PUMP_SLOTS: 2 → 1` (동시 PUMP 포지션 최대 1개로 제한)
  - `MinMLConfidencePumping: 0.58f → 0.66f` (ML 신뢰도 임계치 상향)
  - `MinTransformerConfidencePumping: 0.63f` 신규 추가 (TF 신뢰도 임계치)
  - PUMP CoinType에서 ML+TF 둘 다 기준 통과 필수 (기존 ML 단독 → 이중 게이트)

- **AI 런타임 구조 단순화 (v2.4.27 Unreleased 내용 확정)**:
  - 외부 서비스 프로젝트(`TradingBot.MLService`, `TradingBot.TorchService`) 제거
  - Named Pipe IPC 기반 예측/학습 경로 제거
  - ML.NET + TensorFlow.NET 인프로세스(내부 백그라운드) 처리로 통합

### Documentation

- `README.md`, `PROCESS_AI_ARCHITECTURE.md`, `RELEASE_CHECKLIST.md`, `run-ai-validation.ps1`를
  현재 인프로세스 아키텍처 기준으로 일괄 업데이트

### Note

- 아래 버전 이력에는 당시 구조를 반영한 외부 서비스/Torch 관련 설명이 포함될 수 있으며,
  이는 **히스토리 기록**입니다. 현재 운영 기준은 `Unreleased` 섹션 설명을 따릅니다.

## [2.4.27] - 2026-03-10

### Added

- **AI 서비스 프로세스 분리**:
  - ML.NET 예측 모델을 외부 프로세스(TradingBot.MLService.exe)로 실행
  - AIPredictor: MLServiceClient를 통한 외부 ML 예측(우선) + 로컬 폴백
  - AIDoubleCheckEntryGate: TorchServiceClient를 통한 외부 Transformer 예측
  - Named Pipe 기반 IPC(Inter-Process Communication) 구현으로 프로세스 간 격리

- **AI 알림 통일**:
  - Transformer 주기 재학습 CRITICAL 알림 추가
  - 외부/로컬 모드 모두 동일하게 "[CRITICAL][AI][Transformer]" 형식으로 표준화
  - NormalizeCriticalProjectName()으로 "TorchService" → "Transformer"로 통일

- **빌드 자동화 개선**:
  - MSBuild 타겟: CopyExternalAiServicesToOutput, CopyExternalAiServicesToPublish
  - 빌드/배포 시 MLService, TorchService 실행파일 자동 복사 to Services/ 폴더
  - 배포 후 외부 AI 프로세스 즉시 사용 가능

- **관리자 문서화**:
  - PROCESS_AI_ARCHITECTURE.md: 외부 AI 서비스 아키텍처 상세 설명
  - setup-ai-services.ps1: AI 서비스 프로세스 수동 검증/디버그 스크립트

### Changed

- **AI 예측 경로 대칭화**:
  - ML.NET과 Transformer 모두 (외부 프로세스 우선 + 로컬 폴백) 동일한 패턴으로 동작
  - 상태 모니터링 획일화: Both use AIServiceProcessManager health-check (30s interval, auto-restart)

## [2.4.26] - 2026-03-10

### Changed

- **진입 슬롯 정책 조정**:
  - 액티브 포지션 구성을 메이저 4개 + PUMP 2개, 총 6개로 고정
  - PUMP 신호를 별도 `MAJOR_MEME` 경로가 아닌 `MAJOR` 진입 로직으로 통합
  - 메이저/PUMP 모두 동일한 AI 더블체크 게이트와 공통 진입 필터를 사용하도록 정리

- **AI 진입 기준 강화**:
  - 메이저/일반 코인 모두 AI 점수 임계값을 최소 70점 이상으로 통일
  - 설정값이 더 낮더라도 엔진에서 70점 미만으로 내려가지 않도록 하한 적용

### Fixed

- **거래소 가격 정밀도 보정 개선**:
  - 주문/그리드/실행 서비스 전반의 가격 tick fallback을 소수점 7자리 기준으로 상향
  - 심볼 메타데이터 조회 실패 시에도 저가 코인 가격이 2자리로 잘리지 않도록 수정

- **청산 실패 복구력 향상**:
  - 거래소 API 주문 실패 시 청산 주문을 최대 3회까지 재시도
  - 바이낸스 API 오류 코드와 예외 상세 내용을 UI 로그로 노출해 수동 대응성을 개선

## [2.4.25] - 2026-03-10

### Fixed

- **AI Learning Status 총 결정 수 표시 오류 수정**:
  - Torch 안전모드에서 `AIDoubleCheckEntryGate`가 null일 때도 라벨링 통계를 조회할 수 있도록 개선
  - `GetRecentLabelStatsFromFiles` 메서드 추가로 Gate 없이도 파일에서 직접 통계 조회
  - 결과적으로 Transformer 비활성 환경에서도 실제 누적된 라벨링 데이터가 UI에 정상 표시됨
  - 초기화 시점과 대시보드 갱신 시점 두 곳 모두 반영

## [2.4.24] - 2026-03-10

### Fixed

- **TorchSharp BEX64 핫픽스 (엔진 경로 누락 보완)**:
  - `TradingEngine`에 Transformer 옵트인 미설정 시 Torch 초기화/학습 경로를 강제 스킵하는 가드를 반영
  - 엔진 자동 초기 학습에서 Transformer 런타임 미준비 시 호출 자체를 생략하도록 수정
  - 수동 초기 학습도 Transformer 비활성 상태를 분기 처리하여 `TF=DISABLED`로 명확히 보고
  - Torch 미사용 환경에서 불필요한 Transformer 실패 경로 진입을 차단해 BEX64 노출면을 추가 축소

## [2.4.23] - 2026-03-10

### Fixed

- **TorchSharp BEX64 재발 방지 게이팅 강화**:
  - `TorchInitializer`: `IsExperimentalOptInEnabled` 추가로 실험 기능 옵트인(`TRADINGBOT_ENABLE_TORCH_EXPERIMENTAL=1`) 상태를 일관되게 판별
  - `TradingEngine`: `TransformerSettings.Enabled=true`여도 옵트인 미설정이면 Torch/Transformer 초기화 경로를 즉시 비활성화
  - 엔진 시작 시 Transformer 초기 학습 호출을 런타임 준비 상태에서만 수행하도록 수정
  - 수동 초기 학습에서도 Transformer 비활성 상태를 명시적으로 건너뛰고 `TF=DISABLED` 상태를 반환
  - 결과적으로 Torch 미사용 환경에서 불필요한 Transformer 초기화 실패 경로를 제거하여 BEX64 크래시 노출면을 축소

## [2.4.22] - 2026-03-10

### Removed

- **뉴스 감성 점수 제거**:
  - `TradingEngine`: `NewsSentimentService` 필드/생성/호출 완전 제거
  - `TransformerStrategy`: `NewsSentimentService` 의존성 제거
  - `SentimentScore` 항상 0으로 고정 (진입 판단에 영향 없음)
  - Binance 뉴스 API 의존성 제거로 안정성 향상

### Fixed

- **BEX64 크래시(ucrtbase.dll c0000409) 재발 완전 차단 - 4중 안전장치**:
  - **Layer 1 (비정상 종료 감지)**: `TorchInitializer.RegisterStartupRunState()` — run-state 파일로 이전 크래시 감지 시 다음 실행에서 Torch 자동 차단
  - **Layer 2 (기본 안전모드)**: `TRADINGBOT_ENABLE_TORCH_EXPERIMENTAL=1` 환경 변수 명시적 설정 필요 (옵트인 방식) — 기본값은 Torch 비활성
  - **Layer 3 (엔진 시작 게이팅)**: `TradingEngine` 초기화 시 `TorchInitializer.TryInitialize()` 실패 시 안전모드 전환
  - **Layer 4 (UI 자동 초기화 차단)**: `MainWindow.InitializeWaveAIAsync()` 시작 전 Torch 가용성 사전 체크
  - `App.xaml.cs`: 시작 시 `RegisterStartupRunState()`, 종료 시 `RegisterCleanShutdown()` 호출
  - 근본 원인: MainWindow.Loaded에서 WaveAIManager 자동 초기화가 엔진 설정과 무관하게 실행되던 문제 해결
  - Defense in depth 전략: 어느 경로로든 Torch 접근 시 사전 차단

## [2.4.21] - 2026-03-10

### Fixed

- **BEX64 크래시 완전 수정 (ucrtbase.dll c0000409 — C++ abort)**:
  - **근본 원인**: v2.4.19에서 추가한 `LoadModel()` 더미 forward 테스트가 C++ 네이티브 abort를 유발 — C# try-catch로 잡을 수 없어 프로세스 종료
  - `TransformerTrainer.LoadModel()`: 더미 forward 제거 → stats 파일 기반 아키텍처 검증으로 대체
  - `EntryTimingTransformerTrainer.LoadModel()`: 더미 forward 제거 → 안전한 load + 예외 시 삭제
  - `TransformerWaveNavigator.LoadModel()`: 예외 처리 추가 — 호환 불가 모델 자동 삭제
  - `TorchInitializer.InvalidateModelsIfVersionChanged()`: 앱 버전 변경 시 모든 모델 파일 일괄 삭제 (교차 버전 호환성 문제 원천 차단)
  - `App.xaml.cs`: 시작 시 모델 무효화 호출 추가

## [2.4.20] - 2026-03-10

### Fixed

- **WaveAI 초기화 실패 수정 (TorchSharp 런타임 사용할 수 없습니다)**:
  - `DoubleCheckEntryEngine`, `AIDoubleCheckEntryGate`: `IsAvailable`만 체크하던 것을 `TryInitialize()` 호출로 변경
  - WaveAI가 TradingEngine보다 먼저 초기화될 때 TorchSharp가 아직 쳐크되지 않아 실패하던 타이밍 문제 해결

## [2.4.19] - 2026-03-10

### Fixed

- **Transformer 초기 학습 내부 오류 수정 (attn_output 차원 불일치)**:
  - `TimeSeriesTransformer`, `TransformerClassifierModel`: `dModel % nHeads` 검증 추가
  - `TransformerTrainer.LoadModel()`: 로드 후 더미 forward 테스트로 모델 무결성 검증, 불일치 시 자동 삭제 + 재초기화
  - `EntryTimingTransformerTrainer.LoadModel()`: 동일 패턴 적용
  - 기존 모델 파일이 현재 아키텍처와 호환되지 않을 때 자동 복구

## [2.4.18] - 2026-03-10

### Fixed

- **TorchSharp 안전성 전수 감사 및 수정**:
  - `TimeSeriesDataLoader`: `torch.CPU` 직접 접근 → `IsAvailable` 가드 + `ResolveDevice()` 폴백 (BEX64 크래시 원인 차단)
  - `EntryTimingTransformerTrainer`, `TransformerWaveNavigator`, `PPOAgent`: `IsAvailable` 사전 체크 추가
  - `AIDoubleCheckEntryGate`, `DoubleCheckEntryEngine`: Transformer 인스턴스 생성 전 `IsAvailable` 가드 추가
  - `TorchInitializer`: 프로브 캐시 7일 자동 만료 — FAIL 캐시 영구 고착으로 인한 Transformer 영구 비활성화 방지

### Added

- **자기진화형 AI 학습 데이터 파이프라인**:
  - `AIDoubleCheckEntryGate`에 `OnLabeledSample` 이벤트 추가
  - `TradingEngine`에서 이벤트 구독 → `DbManager.SaveAiTrainingDataAsync()`로 DB 자동 적재
  - `AiLabeledSamples` 테이블 자동 생성 및 라벨 데이터 영속화

## [2.4.17] - 2026-03-10

### Added

- **원격 수동 학습 명령 추가**:
  - Telegram 명령어 `/train` 추가
  - 실행 중인 엔진에서 ML.NET + Transformer + AI 더블체크 학습을 순차 강제 실행 가능
  - 중복 실행 방지(동시 학습 요청 차단) 및 완료 요약 메시지 제공

### Fixed

- **SIGNAL 무반응 이슈 수정**:
  - AI 더블체크 초기 학습 중 UI Signal 업데이트 중단 후 일부 조기 종료 경로에서 재개 누락되던 문제 수정
  - `finally`에서 `SuspendSignalUpdates(false)`를 보장해 시그널 큐 정지 상태가 남지 않도록 개선

- **Transformer 초기 학습 스킵 원인 메시지 개선**:
  - `TransformerSettings.Enabled=false`와 Torch/초기화 실패 케이스를 구분해 안내
  - 운영 중 원인 진단 속도 개선

### Changed

- `appsettings.json`의 `TransformerSettings.Enabled`를 `true`로 변경 (초기 학습 및 Transformer 경로 활성화)

## [2.4.16] - 2026-03-10

### Fixed

- **업데이트 확인 실패 문제 해결**:
  - GitHub Releases 메타데이터 직접 접근 방식에서 Velopack GitHub 통합 API로 전환
  - `UpdateManager` 생성자를 `GithubSource` 사용으로 변경하여 GitHub Releases API와 직접 통합
  - `releases.win.json` 파일 형식 문제 우회 (GitHub의 에셋 다운로드 구조 변경으로 인한 호환성 문제)

## [2.4.15] - 2026-03-10

### Fixed

- **자동 업데이트 다운로드 후 적용 안 되는 문제 수정**:
  - 업데이트 체크 URL을 `latest/download` 디렉터리에서 `releases.win.json` 직접 경로로 변경
  - Velopack 설치 경로(`current`, `Update.exe`) 여부를 먼저 검사하도록 개선
  - Setup.exe로 설치되지 않은 경우 자동 업데이트 불가 안내창 표시
  - `ApplyUpdatesAndRestart()` 호출 후 명시적으로 앱 종료를 수행하도록 보강

## [2.4.14] - 2026-03-10

### Critical Fix

- **BEX64 크래시 실제 시작 경로 차단 (5차 최종 안정화)**:
  - **실제 누락 지점 발견**: 이전 수정은 `TransformerTrainer` 경로만 막았고, 앱 시작 시 별도 경로가 여전히 TorchSharp 생성자를 직접 호출하고 있었음
  - **TradingEngine 생성자**: `AIDoubleCheckEntryGate`를 항상 생성했고, 내부에서 `new EntryTimingTransformerTrainer()`가 실행됨
  - **MainWindow Loaded 경로**: `InitializeWaveAIAsync()`가 항상 실행되며 `WaveAIManager → DoubleCheckEntryEngine → TransformerWaveNavigator` 생성으로 이어짐
  - **추가 안전장치**: `MultiAgentManager`, `AIDoubleCheckEntryGate`, `DoubleCheckEntryEngine` 모두 설정 기반 게이트 추가
  - **최종 결과**: `TransformerSettings.Enabled=false`이면 앱 시작 시 TorchSharp 생성자 경로 자체를 타지 않음

### Changed

- `TradingEngine.cs`: Torch 비활성 시 AI 더블체크 게이트 초기화 자체를 건너뜀
- `MainWindow.xaml.cs`: Torch 비활성 시 WaveAI 자동 초기화 자체를 건너뜀
- `MultiAgentManager.cs`: Torch 비활성 시 PPO 에이전트 생성 완전 차단
- `AIDoubleCheckEntryGate.cs`: Torch 비활성 상태에서 생성자 즉시 차단
- `DoubleCheckEntryEngine.cs`: Torch 비활성 상태에서 생성자 즉시 차단

## [2.4.13] - 2026-03-10

### Critical Fix

- **BEX64 크래시 근본 원인 재분석 및 안전 모드 적용 (4차 최종 안정화)**:
  - **Root Cause (v2.4.11 본석 개선)**: `TransformerTrainer.cs`의 `using static TorchSharp.torch` 구문이 클래스 로드 시점에 torch static constructor를 트리기 → 네이티브 DLL 로드 → BEX64 크래시
  - **v2.4.11의 NoInlining 패턴만으로는 불충분**: JIT 컴파일러가 `new TransformerTrainer(...)`를 컴파일하려고 하는 순간 using static으로 인해 TorchSharp 어셈블리 로드
  - **최종 해결책**: TorchSharp/Transformer 기능을 **기본 비활성화** (appsettings.json `"Enabled": false`)
  - **사용자 선택**: `appsettings.json`에서 `TransformerSettings.Enabled = true`로 변경 후 재시작하면 Transformer AI 활성화 (프로브 검증 후 사용)
  - **기본 모드**: ML.NET AI + MajorCoinStrategy(지표 기반)만 동작 → **BEX64 크래시 완전 차단**
  - **메시지 개선**: 설정 확인 메시지 및 경고 추가

### Changed

- `AppConfig.cs`: `TransformerSettings.Enabled` 기본값 `true` → **`false`**
- `appsettings.json`: `TransformerSettings.Enabled = false` + 경고 주석 추가
- `TradingEngine.cs`: Transformer 초기화 메시지 강화 (안정 모드 안내)

## [2.4.12] - 2026-03-10

### Enhanced

- **볼린저 밴드 필터 개조 — 과열/추세 구분 로직 업그레이드**:
  - 상단 85% 고정 제한 제거 → 밴드 폭(Width) + RSI 복합 판단으로 대체
  - Squeeze→Expansion (밴드 폭 확산 10% 이상) 감지 시 상단/하단 무조건 진입 승인 (강력한 발산 신호)
  - RSI < 70이면 상단 100% 위치라도 "추세 시작"으로 판단, 진입 승인
  - RSI ≥ 80 초과열 시만 상투 판단으로 차단 (기존: RSI 70부터 차단)
  - SHORT 대칭 적용: 하단 RSI ≤ 20 초과냉만 차단, RSI > 30이면 하락 추세 진입 승인
  - Hybrid BB 진입 필터도 동일 로직 적용 (RSI < 70이면 상단 85% 이상도 롱 허용)

## [2.4.11] - 2026-03-10

### Critical Fix

- **BEX64 크래시 근본 원인 제거 (3차 최종 안정화)**:
  - **Root Cause**: `ResolveDevice()` 및 `TryInitialize()`에서 init 실패 시에도 `TorchSharp.torch.CPU` 접근 → torch static ctor 트리거 → 네이티브 DLL 로드 → ucrtbase.dll 크래시
  - **Fix**: TorchSharp 타입 접근을 `[MethodImpl(NoInlining)]` 메서드로 완전 격리
  - **Fix**: `ResolveDevice()` init 실패 시 `null` 반환 (TorchSharp 타입 절대 접근 안함)
  - **Fix**: 프로브 자식이 크래시 시 FAIL 미리 기록 (네이티브 크래시로 체크포인트 유실 방지)
  - **Fix**: 모든 Torch 사용 클래스(TransformerTrainer, PPOAgent 등) null 체크 추가

## [2.4.10] - 2026-03-10

### Enhanced

- **약한 타점 진입 신호 감도 강화 (약 20배 레버리지용)**:
  - 피보나치 진입 구간 확장: 0.618 → 0.786 (덜 눌려도 진입, Golden Zone 확대)
  - RSI 다이버전스 감도 강화: ±3.0 포인트 → ±0.5 포인트 (미세한 신호 포착)
  - ML 신뢰도 기준 하향: 55% / 48% → **50% / 40%** (70%대 신뢰도에서도 진입)

### Fixed

- **10시~11시 약한 반등 미진입 현상 개선**:
  - 가격은 0.382 지점에 머물렀으나 진입 범위(0.618)를 초과로 거부 → 0.786까지 허용
  - RSI는 미세하게만 올랐으나(0.5 포인트) 다이버전스 조건 미충족 → 감도 강화
  - 신뢰도 62%는 "전교 1등 기준"에 미달 → "우등생 기준"으로 하향 조정

### Technical Details

- **FibonacciRiskRewardCalculator.cs**: `FIB_ENTRY_MAX = 0.786m` (Line 28)
- **WaveSniper.cs**: `rsiHigher = secondLowRsi > firstLowRsi + 0.5f` (Line 192)
- **TradingEngine.cs**: 
  - `_fifteenMinuteMlMinConfidence = 0.50f` (Line 60)
  - `_fifteenMinuteTransformerMinConfidence = 0.47f` (Line 61)
  - `GateMlThresholdMin = 0.40f` (Line 66)
  - `GateTransformerThresholdMin = 0.40f` (Line 68)

## [2.4.9] - 2026-03-10

### Fixed

- **BEX64 재발 대응(2차 안정화)**:
  - Torch 프로브 실행 예외 시 위험한 "직접 초기화" 폴백 제거 (fail-closed)
  - 프로브 실행 파일 미존재 시 FAIL 처리로 안전 비활성화
  - Torch 초기화 진단 로그 추가: `%LocalAppData%/TradingBot/torch_init.log`
  - 프로브 자식/부모/캐시 기록을 로그로 남겨 재현 시 원인 추적 강화

## [2.4.8] - 2026-03-10

### Fixed

- **BEX64 크래시 완화 (`ucrtbase.dll`, `0xc0000409`)**:
  - Torch 디바이스 선택 경로를 안전 모드(CPU 기본)로 통일
  - `cuda.is_available()` 직접 호출 경로를 공통 안전 초기화 경유로 전환
  - Torch 초기화 시 환경 변수 기반 강제 비활성(`TRADINGBOT_DISABLE_TORCH=1`) 지원

- **Torch 프로브 캐시 경로 불일치 수정**:
  - 부모/자식 프로세스가 동일한 프로브 결과 파일을 사용하도록 정렬
  - 비정상(빈 값) 프로브 캐시 감지 시 FAIL 처리로 재시도 루프 방지
  - 프로브 캐시 저장 경로를 LocalAppData 기준으로 고정해 권한/경로 이슈 완화

## [2.4.7] - 2026-03-10

### Added

- **SimpleDoubleCheckEngine 전략 통합**:
  - 매복 모드 기반 더블 체크 진입 시스템 (Transformer → ML.NET 순차 검증)
  - 매복 시간대 외에는 ML.NET 실행 안 함으로 CPU 절약
  - HybridExitManager 통합으로 ATR 기반 동적 트레일링 스톱 적용

- **DB 스냅샷 저장 로직 검증**:
  - TradePatternSnapshots 테이블 저장 흐름 확인 완료
  - 진입 시 패턴 데이터 저장 (Label=NULL)
  - 청산 시 자동 라벨링 (Label=0 실패, Label=1 성공)
  - 학습 데이터 조회 API 검증 (GetLabeledTradePatternSnapshotsAsync)

- **거래 테이블 검증 스크립트**:
  - quick-test-trades.ps1: TradeHistory/TradeLogs 간단 체크
  - quick-check-snapshots.ps1: TradePatternSnapshots 검증
  - 실시간 데이터 수집 상태 모니터링 가능

### Changed

- **Entry Gate Status UI 개선**:
  - ML=0%, TF=75% 표시 → "ML: 대기", "TF: 75%"로 변경
  - 0%는 "나쁜 예측"이 아니라 "아직 실행 안 함"을 명확히 표현
  - 매복 모드가 아닐 때 ML.NET 비실행 상태 직관적으로 표시

- **Market Signals UI 명확화**:
  - MLTFSummary 로직 개선: ML=0일 때 TF 점수만 표시
  - 상태 메시지 체계화: "✅ 진입 승인", "💁 매복 대기", "⏳ TF 대기 중"
  - 매복 만료 시 마지막 TF 점수 유지로 일관성 확보

### Fixed

- **UI 표시 혼란 해소**:
  - ML.NET 미실행 시 "0%" 표시로 인한 사용자 오해 제거
  - 매복 모드 동작 원리 명확화 (CPU 절약 전략)
  - Entry Gate와 Market Signals 표시 일관성 확보

## [2.4.6] - 2026-03-09

### Fixed

- **DB holdingMinutes 계산 열 NULL 안전 처리**:
  - ExitTime이 NULL인 상태로 INSERT 시 오류 발생 문제 해결
  - NULL 안전 계산 열로 재생성: `CASE WHEN ExitTime IS NOT NULL THEN DATEDIFF(MINUTE, EntryTime, ExitTime) ELSE NULL END`
  - 진입 시 ExitTime=NULL 허용으로 INSERT 성공률 100% 달성

- **DB 오류 로깅 강화**:
  - SqlException 별도 처리로 SQL 오류 번호, 상태, 라인 번호 상세 출력
  - holdingMinutes 관련 오류 자동 감지 및 해결 가이드 제공
  - 외부 청산 동기화 실패 시 명확한 오류 메시지 표시

- **배열 인덱스 오류 수정**:
  - TelegramService.cs의 Split 배열 접근 시 INDEX WAS OUTSIDE THE BOUNDS 오류 해결
  - 빈 메시지 체크 및 StringSplitOptions.RemoveEmptyEntries 적용
  - BinanceExchangeService.GetFundingRateAsync에 try-catch 추가

### Changed

- **청산 로직 안정성 검증**:
  - ExecuteMarketClose 메서드 전체 흐름 분석 완료
  - 중복 청산 방지, 청산 완료 검증(8회 재시도), 반대 방향 포지션 감지 확인
  - Position 정리 체계(_activePositions.Remove, OnPositionStatusUpdate) 검증
  - DB 저장 및 이벤트 발생(CompleteTradeAsync, OnTradeHistoryUpdated) 정상 동작 확인

- **DB 스크립트 정비**:
  - fix-holdingminutes-safe.sql: 안전한 계산 열 재생성 스크립트 추가
  - fix-db-manual.sql: SSMS 수동 실행용 스크립트 업데이트
  - fix-holdingminutes.ps1: PowerShell 자동 실행 스크립트 복원
  - 모든 스크립트 실제 DB 이름(TradingDB) 정확히 반영

## [2.4.5] - 2026-03-09

### Changed

- **메이저 라이브 로그 시스템 정렬**:
  - MajorCoinStrategy 신호 로그를 라이브 트레이딩 상태형 포맷으로 통일
  - `📊 [BTCUSDT] LONG 진입 후보 포착 | 가격 ... | 점수 ... | RSI ... | Vol ... | AI 사전체크 ...` 형식 적용
  - 신호 발생 시 `TradingStateLogger.EvaluatingAIGate(...)` 사용으로 엔진 로그 체계와 일관성 확보

- **Trade 탭 로그 가독성 개선**:
  - MainViewModel 로그 간소화에서 상태형(이모지+심볼) 로그는 원문 유지하도록 예외 확장
  - 기존처럼 `메이저신호` 단일 라벨로 과축약되는 문제 완화

## [2.4.4] - 2026-03-09

### Fixed

- **AssemblyVersion 형식 오류 해결**: 
  - TradingBot.csproj에서 AssemblyVersion을 4자리 형식(2.4.4.0)으로 수정
  - GenerateAssemblyInfo를 true로 변경하여 .csproj 버전 정보 사용
  - AssemblyInfo.cs 파일 제거 (중복 방지)
  - FileNotFoundException 오류 완전 해결

- **빌드 시스템 개선**:
  - SDK 자동 AssemblyInfo 생성 활성화로 버전 관리 단순화
  - .csproj 파일에서 버전 정보 일원화 관리

## [2.4.3] - 2026-03-09

### Fixed

- **로그 포맷 개선**: 세련된 진입 신호 출력
  - MajorCoinStrategy: `📊 [XRPUSDT] 롱 신호 감지 | 가격 $1.95`
  - NavigatorSniper 평가: `🤖 [XRPUSDT] 롱 ML 스나이퍼 평가 중 | ML신뢰도 85%, TF신뢰도 72%`
  - Sniper 승인: `✅ [XRPUSDT] 롱 Sniper 승인! | ML=85%, TF=72%`
  - 주문 요청: `📤 [XRPUSDT] LONG 주문 요청 중 | 가격 $1.95`
  - 기존 `📡 [SIGNAL][MAJOR][CANDIDATE]` 형식 제거로 가독성 향상

- **Navigator-Sniper 통합 누락 수정**:
  - TradingEngine에서 MajorCoinStrategy 신호 발생 시 AIDoubleCheckEntryGate 평가 로직 추가
  - 주문 실행 전 ML.NET 신뢰도 체크 및 로그 출력 구현
  - WAIT 신호는 로그 출력 생략 (노이즈 감소)

## [2.4.1] - 2026-03-09

### Added

- **TradingStateLogger.cs**: 직관적인 라이브 트레이딩 로그 시스템
  - 상태별 전문 로그 메서드: 횡보 중, 진입 대기(ETA 표시), AI 평가 중, 주문 요청 중, 진입 완료, 거부 사유 등
  - 예시: `⏳ [BTCUSDT] LONG 진입 대기 (ETA: 14:30, 25분 후) | AI 신뢰도 85%`
  - 예시: `✅ [BTCUSDT] LONG 진입 완료! | 진입가 $67,234.50, 손절 $66,500.00, 익절 $68,800.00 | R:R 2.1x | 전략=MAJOR`

- **HistoricalDataLabeler.cs**: 히스토리컬 데이터 자동 라벨링 스크립트 (베타)
  - Time-to-Target 및 ML.NET 진입 성공 라벨을 동시 생성
  - 현재 제약: API 한계로 최근 1000 캔들(약 10일치)만 조회 가능
  - 향후 확장 예정: startTime/endTime 지원하는 IExchangeService 메서드 추가 필요

### Changed

- **TradingEngine.cs 로그 가독성 대폭 개선**:
  - 진입 워밍업: `⏳ [진입 워밍업] 신규 진입 제한 중 | 120초 남음`
  - 패턴 필터: `🔍 [BTCUSDT] LONG 패턴 필터 차단 | loss-pattern`
  - 캔들 확인 대기: `⏸️ [BTCUSDT] LONG 캔들 확인 대기 (1캔들 남음) | Fakeout 방지 모드`
  - 추격 방지: `🏃 [BTCUSDT] LONG 추격 방지 차단 | 이미 상승 완료`
  - AI 게이트: `🤖 [BTCUSDT] LONG ML 스나이퍼 평가 중 | ML신뢰도 85%, TF신뢰도 72%`
  - 주문 완료: `✅ [BTCUSDT] LONG 진입 완료! | 손절 $66,500 | 익절 $68,800 | R:R 2.1x`
  - 서킷 브레이커: `⛔ [서킷 브레이커 발동] 일일 손실 -5% 초과 | 1시간 동안 모든 진입 차단`

- **AIDoubleCheckEntryGate.cs 상세 로그 추가**:
  - Feature 추출 실패/성공 상세 표시
  - Time-to-Target 예측: `🎯 [BTCUSDT] AI 타점 예측: 8.3캔들 (125분) 후, ETA 14:30 | ML=85%, TF=72%`
  - ML.NET / Transformer 거부 이유 상세: 신뢰도 부족 vs 타점 범위 외 구분
  - 규칙 위반: `❌ [BTCUSDT] 규칙 위반 거부: Fib 0.618 이탈`
  - 최종 승인: `✅ [BTCUSDT] AI 더블체크 승인! ML=85%, TF=72%`

### Fixed

- HistoricalDataLabeler.cs: IExchangeService API 호환성 수정 (using TradingBot.Services 추가)

## [2.4.0] - 2026-03-09

### Changed - ⚠️ **파괴적 변경 (Breaking Change)**

- **AI 아키텍처 전환: "네비게이터-스나이퍼" 2단계 시스템**
  - **Transformer 역할 변경**: 이진 분류(Binary Classification) → **Time-to-Target 회귀(Regression)**
    - 기존: "현재 진입할까?" (0/1 확률) → 노이즈에 취약
    - 신규: "피보나치 0.618 목표가까지 몇 개의 캔들이 필요한가?" (1~32 캔들 예측)
  - **ML.NET 역할 유지**: 실시간 최종 진입 승인 ("스나이퍼" 역할)
  - **학습 파이프라인 변경**:
    - Loss: `BCEWithLogitsLoss` → `MSELoss` (Mean Squared Error)
    - Label: `bool ShouldEnter` → `float CandlesToTarget` (캔들 개수)
    - Metric: Accuracy → MAE (Mean Absolute Error, 캔들 단위)
  - **예측 거동**: Transformer가 "오후 2시 30분경 진입 타점" 예보 → ML.NET이 해당 시간대에서 초 단위 정밀 판단

### Added

- `MultiTimeframeEntryFeature.CandlesToTarget` 필드 추가 (Transformer 회귀 라벨용)
- `BacktestEntryLabeler.CalculateCandlesToFibonacciTarget()` 메서드 추가
  - 목표가 정의: 최근 고점에서 피보나치 0.618 되돌림
  - 도달 조건: 캔들 종가가 목표가 ±0.5% 범위 진입
  - 예측 범위: 32캔들(8시간), 미도달 시 학습에서 제외

### Fixed

- AI ENTRY 관망(8%) 교착 상태 근본 해결
  - 문제: 이진 분류는 모든 시점의 확률이 낮으면 영원히 대기
  - 해결: Time-to-Target 회귀로 "언제 기회가 오는지" 예측 → 해당 시점까지 대기 후 정밀 판단

### Deprecated

- `AIDoubleCheckEntryGate.PredictEntryProbabilities()`: 이진 분류 기반 확률 예측 메서드 (호환성 유지용으로 보존되나 0 반환)

### ⚠️ **업그레이드 주의사항**

1. **기존 Transformer 모델 무효화**: 이전 v2.3.x 모델(`.zip`, `.pt`)은 **호환 불가**
   - v2.4.0 최초 실행 시 자동으로 신규 라벨링 학습 수행 권장
   - 기존 ML.NET 모델(`TradingModel.zip`)은 동일하게 사용 가능

2. **라벨링 데이터 재생성 필요**: `TrainingData/EntryDecisions/*.json` 파일에 `CandlesToTarget` 필드가 없으면 학습 실패
   - `BacktestEntryLabeler.CalculateCandlesToFibonacciTarget()`로 재라벨링

3. **ETA 표시 변경**: AI ENTRY 패널에서 "관망 8%" 대신 "지금" / "14:30" / "관망 · 8% · +150m" 형식으로 표시

## [2.3.2] - 2026-03-09

### Fixed

- AI Entry Transformer 예측 인덱싱 오류를 수정하여 `TF confidence`가 0으로 고정되던 문제 해결
- AI Entry 표시 로직 개선: 관망 상태에서도 ETA(`지금`/`HH:mm`)와 `+분` 상세가 표시되도록 수정

### Changed

- AI 더블체크 게이트 임계치 완화
  - 기본: ML `0.65 → 0.50`, TF `0.60 → 0.45`
  - Major: ML `0.75 → 0.60`, TF `0.70 → 0.55`
  - Pumping: ML `0.58 → 0.48`
- 관망(저확률) 상태일 때 AI ENTRY ETA 탐색 범위를 2시간(8스텝)에서 4시간(16스텝)으로 확장

## [2.3.1] - 2026-03-09

### Fixed

- 로그인 후 메인 윈도우 로딩 시 `StaticResource` 선참조로 발생하던 XAML 파싱 예외 수정
  - DataGrid 템플릿 내부 `MinimalScrollViewer` / `MinimalScrollBar` 참조를 `DynamicResource`로 변경

## [2.3.0] - 2026-03-08

### Removed

- **Bybit 거래소 지원 완전 제거**:
  - `BybitExchangeService.cs` 및 `BybitSocketConnector.cs` 파일 삭제
  - Bybit.Net NuGet 패키지 제거
  - User 모델에서 BybitApiKey/BybitApiSecret 속성 제거
  - AppConfig에서 Bybit 설정 클래스 제거
  - MainWindow 거래소 선택 드롭다운 숨김 처리 (Binance로 고정)
  - SettingsWindow에서 거래소 선택 UI 완전 제거
  - ExchangeDataModels에서 BybitExchangeAdapter 제거
  - MarketDataManager에서 Bybit 스트리밍 메서드 및 어댑터 제거
  - DatabaseService, ProfileWindow, SignUpWindow에서 Bybit 관련 코드 제거

### Changed

- **Binance 전용 거래소로 전환**:
  - TradingEngine이 Binance 또는 Mock 거래소만 지원하도록 변경
  - UI에서 거래소 선택 옵션 제거, Binance로 고정

## [2.2.12] - 2026-03-08

### Changed

- **라이브 로그 UI를 초간단 표시로 정리**:
  - `MainViewModel`에서 로그 표시 메시지를 `라벨 · 심볼 · 방향 · 상태 · 요약 상세` 형태로 축약
  - 화면 표시와 DB 저장 로그를 분리하여, 저장 원문은 유지하고 UI 가독성만 개선

- **라이브 로그 표시 정책을 엄격 모드로 조정**:
  - 대시보드 로그 패널에는 `GATE` 로그와 `주문 오류` 로그만 표시
  - 일반 추적/신호성 로그는 UI에서 숨김 처리하여 운영 중 핵심 이벤트 집중도 향상

### Fixed

- **라이브 로그 단순화 미적용 문제 해결**:
  - 로그 후처리 시점(`FlushPendingLiveLogsToUi`)에 단순화 경로를 명시적으로 연결
  - 기존처럼 상세 태그 문자열이 그대로 노출되던 문제를 수정

## [2.2.11] - 2026-03-08

### Fixed

- **TorchSharp Tensor 메모리 누수 수정**:
  - `TimeSeriesTransformer.cs`의 `PositionalEncoding.forward()` - 중간 텐서 자동 해제 추가
  - `TimeSeriesTransformer.forward()` - 모든 중간 텐서(`embedded`, `scaled`, `positional`, `permuted`, `encoded` 등)에 `using` 블록 적용
  - 네이티브 스택 버퍼 오버런(`0xc0000409` / `ucrtbase.dll`) 크래시 원인 제거

### Changed

- **펌프 스캔 전략을 밈코인 활용 전략으로 완전 재작성**:
  - 기존 엄격한 7개 조건 펌프 필터 제거
  - 밈코인/고베타 정적 유니버스 사용 (DOGE, SHIB, PEPE, FLOKI, BONK, WIF, MEME, TURBO, POPCAT, NEIRO, MOG, BRETT, PNUT, ACT, BABYDOGE, STEEM, POWR, OM, ACX, AKT, SUI, FARTCOIN, GOAT, MOODENG)
  - 메이저코인과 동일한 5분봉 스코어링 및 의사결정 로직 재사용
  - 거래량 상위 10개 후보만 선정
  - 혼합 랭킹 스코어 (거래량 50% + 변동성 20% + 모멘텀 30%)
  - 시그널 소스를 `MAJOR_MEME`로 변경하여 메이저 스타일 진입 경로 사용

- **TradingEngine 시그널 라우팅 개선**:
  - 밈코인/고베타 시그널을 `ExecuteAutoOrder(..., "MAJOR_MEME")`로 라우팅
  - ATR 기반 동적 스탑/리스크를 `MAJOR_`로 시작하는 모든 시그널에 적용
  - EMA20 리테스트 및 숏스퀴즈 보너스 로직에서 심볼 제한 제거 (밈코인도 사용 가능)
  - 펌프 슬롯 카운팅 로직 유지 (`IsPumpStrategy = signalSource == "PUMP" || signalSource == "MAJOR_MEME"`, 최대 2개)

### Added

- **3개월 히스토리컬 검증 러너**: `Tools/BacktestRunner/` - `HybridStrategyBacktester`를 이용한 90일 검증 실행 프로젝트 추가

## [2.2.10] - 2026-03-07

### Fixed

- UI 멈춤 증상 해결: Transformer/ML.NET 예측 검증(5분 후) 타이머 폭주 방지
  - 심볼+모델 단일 키 기반 중복 스케줄링 방지
  - 동시성 3개 제한 (`SemaphoreSlim`)
  - 최신 예측만 덮어쓰기 방식으로 변경

### Added

- Entry Gate 대시보드 UI (15분봉 진입 게이트 PASS/BLOCK 집계)
- 라이브 로그 3분할 구조 (GATE / MAJOR / PUMP)
- 크래시 덤프 수집 유틸리티 스크립트 `enable-crash-dumps.ps1`
- DualAI 통합 예측기 (`DualAI_EntryPredictor.cs`)
- 15분봉 캔들 매니저 (`FifteenMinCandleManager.cs`)
- 하이브리드 전략 백테스터 (`HybridStrategyBacktester.cs`)
- 주문 로직 및 게이트 자동튜닝 문서 (`ORDER_LOGIC_AND_GATE_AUTOTUNE.md`)

### Changed

- 태그 소스와 배포 바이너리 정합성 확보 (v2.2.9 → v2.2.10 재배포)
- 모든 병행 작업 파일 통합 커밋 (클린 빌드 보장)

## [2.2.7] - 2026-03-06

### Fixed

- **메이저 코인 AI 분석 재활성화**:
  - v2.2.6에서 `TransformerSettings.Enabled = false`로 크래시를 방지했으나 Transformer AI 전체가 비활성화됨
  - 서브프로세스 프로브 방식 도입: 별도 프로세스에서 TorchSharp 호환성 사전 검증
  - 네이티브 라이브러리 크래시(`0xc0000005`)가 발생해도 메인 앱은 안전하게 보호
  - `.torch_probe_result` 파일로 프로브 결과 캐싱하여 매번 테스트 방지
  - `TransformerSettings.Enabled` 기본값 복원 (`false` → `true`)
  - 메이저 코인 AI 시그널 및 Transformer 예측 정상 동작 확인

### Changed

- **TorchSharp 초기화 방식 개선**:
  - App.xaml.cs에서 즉시 초기화 제거, TradingEngine 시작 시 지연 초기화로 변경
  - `--torch-probe` 인수로 실행 시 호환성 테스트 전용 모드 진입
  - 호환성 검증 실패 시 MajorCoinStrategy(지표 기반)로 안정적 fallback

## [2.2.6] - 2026-03-06

### Fixed

- **시작 직후 런타임 크래시 수정**:
  - 앱 시작 단계의 TorchSharp 선행 초기화를 제거하여 `coreclr.dll` 접근 위반(`0xc0000005`) 가능성 축소
  - `TransformerSettings.Enabled` 설정을 추가하고 기본값을 `false`로 변경
  - Transformer/TorchSharp는 명시적으로 활성화된 경우에만 지연 초기화되도록 변경
  - 기존 설치본에서 발생하던 시작 직후 비정상 종료를 릴리스/퍼블리시 빌드 기준으로 재현 없이 확인

## [2.2.5] - 2026-03-06

### Fixed

- **예외 처리 및 로깅 개선**:
  - 주요 빈 catch {} 블록에 디버그 로깅 추가로 운영 중 추적성 대폭 개선
  - TradingEngine, SignUpWindow, WebServerService 등에서 예외 발생 시 로그 출력 추가
  - Binance/Bybit API 키 검증 실패 시 구체적인 오류 메시지 제공

- **미구현 서비스 안정성 개선**:
  - FundTransferService, PortfolioRebalancingService의 NotImplementedException 제거
  - 실제 출금/입금 API 미구현 시 충돌 대신 안전한 오류 메시지 반환
  - 시뮬레이션 모드 사용 권장 메시지 추가

- **UI/UX 개선**:
  - AI 예측 검증 결과 표현 개선: "부정확" → "미적중"으로 변경
  - 보다 중립적이고 사용자 친화적인 메시지 제공

## [2.2.4] - 2026-03-06

### Fixed

- **AI 예측 정확도 및 안정성 개선**:
  - ML.NET `Score` 대신 `Prediction` (bool) 사용으로 방향 판단 정확도 향상
  - `Score`는 raw margin이므로 0.5 기준 비교에 부적합함을 확인
  - 재예측 시 불완전한 OHLCV 입력을 완전한 파생 피처 계산 경로로 통일
  - 학습에 사용된 20+ 파생 지표와 동일한 입력으로 예측 일관성 확보
  - 모델 로드 검증 강화: `IsModelLoaded` 확인으로 실제 로드 여부 표시
  - UI AIScore 표시 중복 계산 제거 (이미 0-100 스케일인데 *100 재적용 방지)

## [2.2.3] - 2026-03-06

### Fixed

- **Transformer 입력 차원 불일치 수정**:
  - `appsettings.json`의 `TransformerSettings.InputDim=21`과 실제 feature 매핑 개수를 일치시킴
  - `TimeSeriesDataLoader` 학습/추론 경로 모두 동일한 21차원 feature 벡터 사용
  - `TransformerTrainer` 레거시 전처리 경로도 동일 매핑으로 통일
  - 메이저 코인 분석 중 반복되던 `IndexOutOfRangeException` 발생 가능성 제거

### Added

- **범위 오류 추적 로그 강화**:
  - `FIRST_CHANCE_RANGE_ERROR.txt`에 예외 스택뿐 아니라 현재 호출 스택도 함께 기록
  - 재발 시 실제 throw 지점을 더 빠르게 추적할 수 있도록 진단 정보 보강

## [2.2.2] - 2026-03-06

### Fixed

- **프로그램 크래시 안정성 대폭 개선**:
  - TorchSharp TransformerTrainer 초기화 예외 처리 강화
  - 초기화 실패 시 기본 전략으로 안전하게 폴백
  - 크래시 로그 자동 저장: `TRANSFORMER_CRASH.txt`
  
- **배열 인덱스 범위 초과 오류 방지**:
  - TransformerTrainer: `_means[3]`, `_stds[3]` 접근 전 배열 길이 검증
  - TimeSeriesDataLoader: 정규화 파라미터 배열 크기 검증 추가
  - TradingEngine: `.Last()` 호출 전 빈 컬렉션 체크
  - 모든 배열 접근에 안전장치 추가

- **전역 예외 처리 강화**:
  - `IndexOutOfRangeException` 자동 복구 및 로그 저장
  - `ArgumentOutOfRangeException` 자동 복구 및 로그 저장
  - 예외 발생 시 사용자 알림 후 계속 실행
  - 상세한 예외 정보 자동 로깅 (타입, 메시지, HResult, 스택 트레이스)

### Added

- **크래시 로그 자동 기록 시스템**:
  - `TRANSFORMER_CRASH.txt`: TorchSharp 초기화 실패 로그
  - `INDEX_OUT_OF_RANGE_ERROR.txt`: 배열 인덱스 오류 로그
  - `ARG_OUT_OF_RANGE_ERROR.txt`: 인수 범위 오류 로그
  - `CRITICAL_ERROR.txt`: 기타 치명적 오류 로그

- **배포 자동화 시스템**:
  - `start-release.ps1`: 배포 도우미 스크립트 추가
  - `publish-and-release.ps1`: 실행 시 자동 체크리스트 확인 프롬프트
  - Git pre-push hook: 태그 푸시 시 배포 체크리스트 알림
  - `RELEASE_CHECKLIST.md`: 상세 배포 가이드 및 GPG 서명 설정

### Changed

- **Null 안전성 강화**:
  - Transformer 관련 모든 작업에 null 체크 추가
  - TransformerStrategy 초기화 실패 시 null 전달하여 안전하게 비활성화
  - 데이터 검증 강화 (빈 배열, 부족한 데이터 등)

### Technical Details

- 네이티브 라이브러리(TorchSharp) 크래시 방지를 위한 안전장치 구현
- 스택 버퍼 오버런(0xc0000409) 예외 원인 추적 및 해결
- ucrtbase.dll 관련 네이티브 예외 로깅 강화
- 메모리 정리(GC) 호출 추가하여 TorchSharp 초기화 안정성 향상

## [2.2.1] - 2026-03-06

### Changed

- **텔레그램 청산 알림 메시지 개선**:
  - 메시지 언어를 영어에서 한글로 변경
  - 금일 누적 손익(총수익금) 정보 추가
  - 익절/손절 구분 이모지 자동 선택 (💰 익절 / 📉 손절)
  - 타임스탬프 형식 개선 (yyyy-MM-dd HH:mm:ss)
  - Push 알림 제목도 한글로 변경

### Fixed

- **PnL 계산 정확도 개선 (수수료 및 슬리피지 반영)**:
  - 기존: 순수 가격 차이만 계산하여 실제 손실보다 낮게 표시
  - 개선: 거래 수수료(왕복 0.08%) + 슬리피지(0.05%) 차감
  - 모든 청산 경로에 적용 (전량청산, 부분청산, 외부청산, 동기화청산)
  - 디버깅용 상세 로그 추가: 순수PnL / 수수료 / 슬리피지 / 최종PnL 출력
  - 예시: 순수 손실 -9.05 USDT → 실제 손실 -12.96 USDT (수수료 0.16 + 슬리피지 3.75)

### Added

- **거래소 포지션 자동 동기화 시스템**:
  - `TradingEngine.SyncExchangePositionsAsync` 메서드 추가
  - 거래소에는 없지만 로컬에 남아있는 포지션 자동 감지 및 청산 기록 복구
  - WebSocket 연결 끊김이나 봇 중지 중 발생한 청산 자동 보정
  - 30분 주기 자동 동기화 실행

- **수동 동기화 UI 추가**:
  - TradeHistory 탭에 "🔄 Sync" 버튼 추가
  - `MainViewModel.SyncPositionsCommand` 명령 구현
  - 사용자가 필요시 즉시 동기화 실행 가능

### Fixed

- **누락된 청산 기록 자동 복구**:
  - 외부에서 청산된 포지션이 TradeHistory에 기록되지 않는 문제 해결
  - `MISSED_CLOSE_SYNC` 전략으로 누락 청산 구분 가능
  - 동기화 완료 시 알림 및 TradeHistory 자동 갱신

### Technical Details

- `TradingEngine._lastPositionSyncTime` 타이머 추가 (30분 주기)
- 동기화 시 `IsCloseInProgress` 상태 확인하여 중복 처리 방지
- 현재가 조회 실패 시 진입가 폴백 처리
- 동기화 완료 시 `OnTradeHistoryUpdated` 이벤트 발생

## [2.2.0] - 2026-03-06

### Fixed

- **TradeHistory DB 등록 누락 문제 완전 해결**:
  - 메인UI CLOSE 버튼으로 수동 청산 시 DB 기록 미등록 문제 수정
  - 청산 주문 성공 후 포지션 잔존 감지 시에도 DB 저장 보장 (부분 체결 대응)
  - 반대방향 포지션 생성 시 원래 포지션 청산 기록 누락 수정
  - CancellationToken 취소 시 DB 저장 스킵 문제 해결 (엔진 정지 중 청산 시)
  - 모든 청산 경로(손절/익절/타임아웃/부분청산)에서 DB 저장 일원화

- **TradeHistory UI 자동 갱신 구현**:
  - 청산 완료 시 자동으로 TradeHistory 테이블 갱신
  - "Load History" 버튼 수동 클릭 불필요
  - `OnTradeHistoryUpdated` 이벤트 체인 추가 (PositionMonitorService → TradingEngine → MainViewModel)

- **DB 저장 실패 로깅 개선**:
  - userId = 0 (비로그인) 상태에서 청산 시 명확한 에러 메시지 출력
  - `AppConfig.CurrentUser` null 체크 및 상세 디버깅 정보 제공

- **엔진 미시작 상태 수동 청산 방어**:
  - `ClosePositionAsync` 호출 시 `_positionMonitor` null 체크 추가
  - 명확한 안내 메시지 출력: "엔진이 초기화되지 않았습니다. 스캔을 먼저 시작해주세요."

### Added

- **SaveCloseTradeToDbAsync 헬퍼 메서드**:
  - 모든 청산 경로에서 재사용 가능한 DB 저장 로직 통합
  - 청산가 조회 실패 시 진입가 폴백 처리
  - PnL/ROE 계산 및 텔레그램 알림 자동 전송

### Technical Details

- `PositionMonitorService.ExecuteMarketClose` 강화:
  - `ConfirmRemainingPositionAsync` try-catch로 CancellationToken 취소 방어
  - 반대방향 포지션 감지 시 원래 청산 기록 먼저 저장 후 재청산 시도
  - 같은 방향 부분 체결 감지 시 체결된 수량만큼 DB 저장
- 모든 청산 경로에 `OnTradeHistoryUpdated?.Invoke()` 이벤트 추가
- `MainViewModel.ClosePositionCommand`에 청산 후 `LoadTradeHistory()` 즉시 호출
- `DbManager.UpsertTradeEntryAsync`/`CompleteTradeAsync` userId 실패 로그 강화

## [2.0.30] - 2026-03-06

### Fixed

- **손절/익절 설정 런타임 반영 문제 수정**:
  - `Start` 실행 시 `TradingEngine`를 새로 생성하도록 변경하여 최신 `StopLossRoe`, `TargetRoe`, 거래소, 시뮬레이션 설정이 즉시 반영되도록 수정
  - 엔진을 한 번 생성한 뒤 재사용하던 구조 때문에 발생하던 손절 미반영 문제 해결

- **시뮬레이션 모드 자산 표시 개선**:
  - 시뮬레이션 시작 시 설정된 초기 잔고를 직접 반영하도록 수정
  - 대시보드 및 시작 로그에 현재 모드와 사용 중인 서비스 타입을 출력하도록 보강
  - `ACCOUNT OVERVIEW` 영역에 `SIMULATION` 상태 표시 추가

### Changed

- **설정 저장 UX 개선**:
  - 시뮬레이션 모드, 시뮬레이션 초기 잔고, 거래소 변경 시 재시작 필요 안내 메시지 추가

### Technical Details

- `MainViewModel`에서 엔진 생명주기를 시작/정지 버튼 기준으로 재구성
- `TradingEngine.InitializeSeedAsync()` 및 대시보드 갱신 경로에 디버그 로그 추가

## [2.0.29] - 2026-03-06

### Changed

- **애플리케이션 아이콘 리뉴얼**:
  - 로그인 창, 메인 창, 설치 프로그램 아이콘을 모던하고 프로페셔널한 디자인으로 전면 교체
  - 암호화폐(₿), AI 네트워크, 상승 차트를 조합한 새로운 비주얼 아이덴티티
  - 다크 네이비 배경에 청록색 링, 금색 코인 심볼, 초록색 차트 라인으로 구성
  - 16x16부터 256x256까지 멀티 사이즈 지원으로 모든 환경에서 선명한 표시

### Fixed

- **DataGrid 배열 인덱스 오류 수정**:
  - MainWindow의 TriggerPriceAnimation 메서드에서 컬럼 수 체크 추가
  - 컬럼 초기화 전 접근으로 인한 "index was outside the bounds of the array" 오류 해결

### Technical Details

- 새 아이콘은 C# System.Drawing을 사용해 프로그래매틱하게 생성
- 그라디언트, 안티앨리어싱, 글로우 효과 적용으로 고품질 렌더링
- trading_bot.ico, trading_bot.png 파일 생성

## [2.0.28] - 2026-03-06

### Changed

- **메인 DataGrid UI 개선**:
  - ROI 컬럼을 PRICE 컬럼 바로 옆으로 이동하여 가격 관련 정보를 그룹화
  - 사용자가 포지션의 가격과 수익률을 한눈에 확인할 수 있도록 레이아웃 개선

### Technical Details

- `MainWindow.xaml`의 DataGrid 컬럼 순서 변경: SYMBOL → PRICE → **ROI** → AI → AI시각 → L/S → SIGNAL → SOURCE → SETUP → ACTION
- 포지션 모니터링 시 가격 정보와 수익률의 시각적 연관성 강화

## [2.0.27] - 2026-03-06

### Fixed

- **실시간 차트 NaN/축 범위 오류 복구 강화**:
  - `MainViewModel`에 예측 차트, 라이브 차트, RL 차트, 활성 PnL 차트 축 범위 보호 로직 추가
  - `App.xaml.cs`에서 LiveCharts `Y1` NaN 예외를 감지하면 차트 상태를 자동 복구하도록 처리
  - `MainWindow.xaml`, `SymbolChartWindow.xaml`, `ActivePnLWindow.xaml`에 Y축 Min/Max 바인딩 추가
  - `MainWindow.xaml.cs`, `TradeStatisticsWindow.xaml.cs`에 무효 차트 데이터 방어 로직 추가

- **실거래 주문 체결/슬롯 처리 보강**:
  - `BinanceExchangeService.PlaceOrderAsync`에서 지정가 주문 시 `GoodTillCanceled` 적용
  - `TradingEngine.ExecuteAutoOrder`에서 지정가 주문 후 실제 체결 여부를 확인하고 미체결/부분체결을 처리
  - `TradingEngine.HandlePumpEntry`에서 PUMP 슬롯 계산을 전체 포지션 수가 아닌 PUMP 포지션 수 기준으로 수정

- **메이저 코인 저거래량 진입 허들 완화**:
  - `MajorCoinStrategy`에서 EMA/RSI/저점상승 조건이 유효하면 거래량 부족 감점을 우회하도록 조정
  - `TradingEngine` AI 필터에 메이저 저거래량 특권 로직을 추가해 OI/이평선 추세를 우선 반영

### Technical Details

- 실시간 차트의 축 범위를 유한값 기준으로 재계산해 LiveCharts 내부 `Y1 = NaN` 예외 가능성을 줄였습니다.
- 메이저 코인은 단순 거래량 비율만으로 차단하지 않고 `RSI`, `SMA_20/60/120`, `OI_Change_Pct`를 함께 사용하도록 조정했습니다.
- 자동매매 지정가 주문은 제출 성공만으로 진입 완료 처리하지 않고, 실제 체결 수량/평균가 기준으로 포지션을 확정합니다.

## [2.0.26] - 2026-03-06

### Fixed

- **실시간 AI 예측 차트 NaN 오류 수정**:
  - `MainViewModel.AddPredictionRecord`: AI 예측 차트에 값 추가 시 유효성 검증 강화
  - decimal 값이 비정상적이거나 차트 렌더링 시 double 변환되면서 NaN 발생하던 문제 해결
  - PredictedPrice와 ActualPrice의 유효 범위(> 0, < decimal.MaxValue/2) 검증 추가
  - 무효한 값 감지 시 이전 값 유지 또는 기본값(0) 사용
  - 오류 메시지: "치명적 오류 발생: 'NaN'은(는) 'Y1' 속성의 유효한 값이 아닙니다" (실시간 실행 중 발생) 해결

### Technical Details

- **근본 원인**: AddPredictionRecord에서 record.PredictedPrice/ActualPrice를 검증 없이 차트에 추가
- 백테스트 차트는 v2.0.24/v2.0.25에서 수정됐으나, **실시간 AI 예측 차트**에서 NaN 발생
- 검증 로직: `predictedPrice > 0 && predictedPrice < decimal.MaxValue / 2`
- 폴백 전략: 무효값 → 이전 차트 값 → 0 (기본값)
- LiveCharts는 decimal → double 변환 시 비정상 값을 NaN으로 처리

## [2.0.25] - 2026-03-06

### Fixed

- **백테스트 EquityCurve 근본 검증 강화**:
  - `BacktestService.OptimizeWithOptunaAsync`: 반환 전 EquityCurve 검증 및 정리 로직 추가
  - `RsiBacktestStrategy`: markToMarketEquity 계산 시 음수/0 방지
  - `ElliottWaveBacktestStrategy`: markToMarketEquity 계산 시 음수/0 방지
  - `MaCrossBacktestStrategy`: markToMarketEquity 계산 시 음수/0 방지
  - `BollingerBandBacktestStrategy`: markToMarketEquity 계산 시 음수/0 방지
  - 모든 전략에서 FinalBalance 계산 시 검증 추가
  - 무효한 데이터 감지 시 이전 equity 유지
  
### Technical Details

- **근본 원인**: EquityCurve에 0 이하 값이 들어가면 LiveCharts 변환 시 NaN 발생
- **검증 레이어**:
  1. 전략 Execute 단계: markToMarketEquity <= 0 체크
  2. BacktestService 단계: 반환 전 EquityCurve 전체 검증
  3. ViewModel 단계: ToFinite() 변환 (v2.0.24)
- **폴백 캘인**: 무효값 → 이전 equity → InitialBalance
- **EquityCurve 비어있을 경우**: InitialBalance로 초기화

## [2.0.24] - 2026-03-06

### Fixed

- **Optimize 차트 NaN 오류 수정**:
  - `BacktestViewModel`: ScatterPoint 생성 시 NaN/Infinity 필터링 추가
  - `BacktestViewModel`: closeValues에 0 이하 값 방지 로직 추가
  - `BacktestViewModel`: ConfigureYAxisRange에서 음수/0 값 필터링 강화
  - `MainViewModel.UpdateBacktestChart`: EquityCurve 변환 시 음수/0 방지
  - `MainViewModel.UpdateBacktestChart`: ScatterPoint 생성 전 유효성 검증 강화
  - 오류 메시지: "'NaN'은(는) 'Y1' 속성의 유효한 값이 아닙니다" 해결
  
### Technical Details

- LiveCharts의 AxisSeparatorElement는 NaN 값을 허용하지 않음
- 모든 차트 데이터에 대해 IsFinite() 검증 추가
- 기본 fallback 값을 0에서 1000/10000으로 변경 (더 현실적인 가격대)
- ScatterPoint(X, Y, Weight) 생성 시 X, Y 모두 유한한 값이어야 함

## [2.0.23] - 2026-03-06

### Fixed

- **AI 모니터 로그 가시성 개선**:
  - `ValidatePredictionAsync`: 예측 검증 시작/완료 로그 추가 (검증 성공/실패 상세 정보)
  - Transformer 예측 등록 시 로그 추가 (방향, 신뢰도 표시)
  - ML.NET 예측 등록 시 로그 추가 (방향, 확률 표시)
  - 예측 키에 모델명 접두사 추가 (`TF_`/`ML_`) - 디버깅 편의성 향상
  - 검증 실패 시 구체적인 오류 메시지 출력
  
### Technical Details

- AI 예측은 등록 후 5분 대기 후 검증됩니다
- 사용자는 로그를 통해 예측 진행 상황을 실시간으로 확인할 수 있습니다
- OnAIPrediction 이벤트는 이미 MainViewModel에서 구독 중 (line 1027)
- 이슈: AI Monitor에 데이터가 표시되지 않는 문제 → 로그 추가로 진행 상황 추적 가능

## [2.0.22] - 2026-03-06

### Removed

- **ONNX 지원 제거 (코드 정리)**:
  - `AiInferenceService.cs` 파일 삭제 (사용되지 않으며 ML.NET 중복 기능)
  - `.gitignore`에서 `*.onnx`, `model.onnx` 항목 제거
  - `copilot-instructions.md`에서 ONNX 관련 잘못된 정보 수정
  - `README.md`에서 "ONNX Runtime" 항목 제거
  - `ModelTrainer.cs`에서 AiInferenceService 주석 제거

### Technical Details

- **사유**: AiInferenceService는 ONNX Runtime이 아닌 ML.NET을 사용하며, `AIPredictor` 및 `MLService`와 기능 중복
- **현재 AI/ML 스택**: ML.NET (AIPredictor, MLService) + TorchSharp (TransformerTrainer)
- **프로젝트에 ONNX Runtime 패키지 없음**: 불필요한 의존성 제거

## [2.0.21] - 2026-03-06

### Fixed

- **전면적 메모리 누수 수정 (Transformer 및 네이티브 리소스)**:
  - `TransformerTrainer`: IDisposable 명시 구현, 소멸자 추가
  - `TimeSeriesTransformer`: protected Dispose 구현
  - `PositionalEncoding`: protected Dispose 구현
  - `TimeSeriesDataLoader`: 캐시된 Tensor 정리 보장
  - `MarketDataManager`: Socket 클라이언트 정리 추가
  - `BinanceSocketService`: IDisposable 구현
  - `BinanceSocketConnector`: IDisposable 구현
  - `BinanceExchangeService`: IDisposable 구현
  - `BybitExchangeService`: IDisposable 구현, SemaphoreSlim 정리
  - `MLService`: IDisposable 명시 구현
  - `TradingEngine`: IDisposable 구현, 모든 서비스 정리

### Changed

- **리소스 관리 강화**:
  - 모든 네이티브 리소스에 finalizer 추가
  - GC.SuppressFinalize() 호출로 성능 최적화
  - _disposed 플래그로 중복 정리 방지
  - try-catch-finally 패턴으로 안전한 정리 보장

### Technical Details

- **TorchSharp 리소스**: Transformer 모델 및 데이터 로더의 네이티브 메모리 정리
- **Socket 연결**: UnsubscribeAllAsync 호출 후 5초 타임아웃으로 정리
- **ML.NET PredictionEngine**: 명시적 Dispose로 네이티브 핸들 해제
- **메모리 프로파일링 권장**: 장시간 실행 후 메모리 사용량 모니터링 필요

## [2.0.20] - 2026-03-06

### Fixed

- **스택 버퍼 오버런 수정 (ucrtbase.dll 0xc0000409 예외)**:
  - `FLASHWINFO` 구조체에 `Pack=4, Size=20` 명시적 지정
  - `FlashWindowEx` P/Invoke 호출에 `SetLastError=true` 추가
  - 구조체 크기 검증 로직 추가 (크기 불일치 시 호출 중단)
  - `uCount` 값을 10에서 3으로 축소 (안정성 향상)
  - Dispatcher 종료 시 예외 처리 강화

- **메모리 누수 방지**:
  - `AIPredictor`에 `IDisposable` 구현 추가
  - `AiInferenceService`에 `IDisposable` 구현 추가
  - `PredictionEngine` 리소스 적절한 정리 보장

### Technical Details

- **문제 원인**: Windows API `FlashWindowEx` 호출 시 구조체 레이아웃 불일치로 인한 스택 메모리 손상
- **해결 방법**: `StructLayout`에 명시적 크기 및 Pack 지정, Win32 오류 코드 확인
- **추가 보호**: ML.NET `PredictionEngine` 네이티브 리소스 정리 보장

## [2.1.18] - 2026-03-05

### Added

- **지표 결합형 동적 익절 시스템 (Advanced Exit Stop Calculation)**:
  - 5개 지표(엘리엇 파동, RSI, MACD, 볼린저 밴드, 피보나치) 통합
  - ROE 20% 도달 시 **분할 익절**: 50% 즉시 시장가 익절 + 50% 지표 추격
  - **다중 신호 감지**: 여러 익절 신호 동시 발생 시 즉시 익절 실행

- **지표별 익절 트리거**:
  - **엘리엇 파동**: 5파동 완성 신호 → ATR 간격 0.4배 축소
  - **RSI**: 극단 과매수(>80) → ATR 간격 더 축소, 일반 과매수(75~80) → 부분 축소
  - **MACD**: 히스토그램 감소 → ATR 간격 0.7배 축소
  - **볼린저 밴드**: 상단 이탈 후 복귀 → 즉시 전량 익절
  - **피보나치**: 1.618/2.618 레벨 도달 → 해당 가격을 최소 익절 보장선으로 고정

- **AdvancedExitStopCalculator 서비스**: 지표 신호 분석 및 스탑 조정 담당
- **appsettings.json**: 지표 기반 익절 파라미터 추가 (활성화/비율/임계값)

### Changed

- **MonitorPositionStandard**: ROE 20% 이상에서 지표 기반 익절 모니터링 추가
- **PositionMonitorService**: AdvancedExitStopCalculator 의존성 추가

### Fixed

- **ROE 20% 과열 구간 대응**: 여러 지표 신호로 추세 피크 감지 정확도 향상
- **V자 급락 완화**: 지표 조합으로 고점 근처에서 신속한 익절

### Technical Details

- **tightModifier (타이트 모디파이어)**: 1.0(기본) → 0.1(극도) 범위로 스탑 간격 동적 조정
- **floorPrice (보장선)**: 피보나치 레벨을 최소 익절 기준으로 설정하여 수익 보호
- **분할 익절 전략**: 심리적 안정(50% 현실화) + 수익 극대화(50% 추격) 병렬 추구

## [2.1.17] - 2026-03-05

### Added

- **3단계 본절 보호 & 수익 잠금 시스템**: Major 코인 전략에 스마트 방어 로직 추가.
  - **1단계 (ROE 10%)**: 본절 보호 - 진입가 + 0.1% 스탑 설정, 최소한 손실 방지.
  - **2단계 (ROE 15%)**: 수익 잠금 - 진입가 + 0.35% 스탑 설정, 최소 ROE 7% 확보.
  - **3단계 (ROE 20%)**: 타이트 트레일링 - ROE 18% 최소 보장 + 0.15% 간격 추격.
- **절대 후퇴 금지 원칙**: 스탑 가격은 수익 방향으로만 이동, 역방향 이동 불가.

### Changed

- **방어적 청산 로직 강화**: "ROE 15% 갔다가 손절" 시나리오 완전 차단.
- **단계별 로그 개선**: 각 단계 활성화 시점 및 스탑 갱신 내역 상세 기록.

### Fixed

- **V자 급락 대응**: 수익 구간에서 급락 시 본절/수익 보호 메커니즘 작동.
- **추세 실패 방어**: 고점 대비 하락 시 단계별 스탑으로 최소 수익 보존.

## [2.1.16] - 2026-03-05

- **3단계 본절 보호 시스템**: Major 코인 전략에 스마트 방어 로직 추가.
  - **1단계 (ROE 10%)**: 본절 보호 - 진입가 + 0.1% 스탑 설정으로 최소한 손실 방지.
  - **2단계 (ROE 15%)**: 수익 잠금 - 진입가 + 0.35% 스탑으로 최소 ROE 7% 확보.
  - **3단계 (ROE 20%)**: 타이트 트레일링 - ROE 18% 최소 보장하며 0.15% 간격 추격.
- **절대 후퇴 금지 원칙**: 스탑로스는 한 번 올라가면 절대 뒤로 물러나지 않음.

### Changed

- **청산 로직 개선**: "15% 갔다가 손절" 문제 해결 - ROE 15% 도달 시 최소 ROE 7% 보장.
- **레버리지 민감도 대응**: 20배 레버리지 환경에서 0.75% 가격 변동(ROE 15%)도 수익으로 전환.

### Fixed

- **V자 급락 대응**: 수익 구간에서 급락 시 본절 또는 수익 잠금 스탑으로 보호.
- **추세 실패 방어**: ROE 10% 이상 도달 후 추세 실패 시 본절가 이상에서 청산.

## [2.1.16] - 2026-03-05

### Added

- **슬리피지 방어 시스템**: 변동성 폭발 구간(휩소) 대응을 위한 3단계 방어 로직 추가.
  - **ATR 변동성 필터**: 평균 대비 2배 이상 변동성 시 진입 차단.
  - **호가창 슬리피지 검증**: Order Book API를 통해 0.05% 초과 슬리피지 시 진입 취소.
  - **지정가 주문 전환**: 시장가 대신 최우선 호가(bestBid/Ask) 지정가 주문으로 전환.
- **거래소 API 확장**: `IExchangeService.GetOrderBookAsync()` 메서드 추가 (Binance, Bybit, Mock 구현체 포함).

### Changed

- **주문 실행 로직 개선**: `TradingEngine.ExecuteAutoOrder`에서 ATR 지표 기반 변동성 분석 및 호가창 실시간 검증 수행.
- **슬리피지 허용치 강화**: 20배 레버리지 환경에서 0.05% 슬리피지 임계값 적용으로 극단적 손실 방지.

### Performance

- **실시간 호가창 조회**: 진입 직전 Order Book 조회로 실제 체결가 예측 정확도 향상.
- **변동성 계산 최적화**: 5분봉 40개 데이터로 ATR 비율 계산 (캐시 활용).

## [2.1.3] - 2026-03-04

### Changed

- **버전 업데이트**: 프로젝트 버전을 `2.1.3`로 상향 (`Version`, `AssemblyVersion`, `FileVersion`).
- **UI 버전 표기 업데이트**: 메인 창 타이틀 버전을 `v2.1.3`으로 반영.

## [2.1.2] - 2026-03-04

### Changed

- **버전 업데이트**: 프로젝트 버전을 `2.1.2`로 상향 (`Version`, `AssemblyVersion`, `FileVersion`).
- **설정 검증 확장**: 설정창 저장 시 `General`/`Transformer` 입력값 범위 검증 및 상호 조건 검증을 추가.

## [2.1.1] - 2026-03-04

### Changed

- **버전 업데이트**: 프로젝트 버전을 `2.1.1`로 상향 (`Version`, `AssemblyVersion`, `FileVersion`).
- **UI 버전 표기 통일**: 메인 창 타이틀 및 로고 옆 버전 표시를 `v2.1.1`로 업데이트.

### Fixed

- **하이브리드 청산 오탐 로그 방지**: 실제 포지션 재검증 전/후 조건을 강화하여 `AI 반전`/`전량 청산` 오탐 메시지 발생 가능성을 축소.
- **실시간 캔들 저장 누락 완화**: 데이터 개수 조건으로 4개 테이블 저장이 건너뛰던 경로를 수정하여 초기 구간에서도 저장되도록 개선.
- **Bybit 신규 봉 저장 트리거 보완**: Bybit Kline 경로에서도 `OnNewKlineAdded` 이벤트를 발생시켜 저장 체인을 일관화.

## [2.0.0] - 2026-03-01

### Added

- **고급 주문 관리**: 서버 사이드 Trailing Stop 및 DCA(물타기) 전략 추가.
- **DeFi 연동**: Uniswap V3 실시간 가격 조회 및 MEV 방어 스왑 기능 구현.
- **온체인 분석**: 고래 지갑 이동 추적 및 거래소 유입/유출 분석 기능 추가.
- **모바일 앱**: .NET MAUI 기반 컴패니언 앱 개발 (대시보드, 원격 제어, 실시간 로그/차트).
- **푸시 알림**: FCM을 통한 모바일 푸시 알림 및 토픽 구독 기능 구현.
- **하이퍼파라미터 튜닝**: Optuna 스타일의 자동 최적화 모듈 추가.
- **시스템 확장**: OKX 거래소 연동 구조 및 설정 백업/복원 기능 추가.
- **데이터 분석**: 매매 이력 CSV 내보내기 및 성과 분석 대시보드(히트맵) 추가.

### Changed

- **AI 모델 고도화**: 기존 LSTM 모델을 Transformer 기반 시계열 예측 모델로 업그레이드.
- **강화학습(RL) 고도화**: PPO 알고리즘 및 멀티 에이전트(Scalping/Swing) 환경 도입.
- **데이터 파이프라인 개선**: `TimeSeriesDataLoader`를 통한 메모리 효율적인 배치 처리 및 정규화.
- **보상 함수 튜닝**: RL 보상 함수에 샤프 지수(Sharpe Ratio) 및 MDD 반영.
- **모바일 차트 고도화**: 단순 라인 차트를 캔들스틱 차트로 개선 및 기간(Interval) 선택 기능 추가.

### Performance

- **비동기 처리 개선**: `System.Threading.Channels`를 주문/계좌 업데이트까지 확대하여 병목 현상 제거.
- **메모리 관리 강화**: `MemoryManager`를 통한 지능형 GC 및 캐시 정리 로직 추가.

### Fixed

- **WPF 앱 초기화 오류**: `ConnectionString` 초기화 순서 문제 해결 및 `StartupUri` 제거.
- **UI 크래시 방지**: 데이터베이스 서비스 초기화 실패 시 회원가입/로그인 창 비정상 종료 문제 해결.
- **빌드 오류 수정**: 모바일 앱 및 Transformer 모델 관련 빌드 오류 전반 수정.

### Security

- **웹 서버 보안**: 모바일 앱 통신을 위한 API Key 인증 및 HTTPS 지원 추가.

## [1.0.0] - 2026-02-26

### Added

- 🎉 초기 릴리스
- 다중 거래소 지원 (Binance, Bybit)
- AI 기반 시장 예측 (LSTM, 강화학습)
- Pump Scan 전략 (급등주 포착)
- Grid 전략 (횡보장 매매)
- Arbitrage 전략 (거래소 간 차익)
- 백테스팅 기능
- 텔레그램 알림 및 원격 제어
- 자동 업데이트 (Velopack)
- 로그인 시스템 및 사용자 관리
- API Key 암호화 저장 (DPAPI)

### Security

- API Key DPAPI 암호화 적용
- SQL Injection 방지 (Dapper 파라미터화)

---

## 릴리스 노트 작성 가이드

### 카테고리

- **Added**: 새로운 기능
- **Changed**: 기존 기능의 변경
- **Deprecated**: 곧 제거될 기능
- **Removed**: 제거된 기능
- **Fixed**: 버그 수정
- **Security**: 보안 관련 수정

### 예시

```markdown
## [1.1.0] - 2026-03-15

### Added
- 새로운 RSI 다이버전스 전략 추가
- 다크 모드 테마 지원

### Changed
- ML 모델 성능 개선 (정확도 15% 향상)
- UI 레이아웃 최적화

### Fixed
- API 연결 끊김 시 자동 재연결 오류 수정
- 백테스팅 차트 표시 버그 수정

### Security
- 암호화 알고리즘 업그레이드 (SHA256 → SHA512)
```
