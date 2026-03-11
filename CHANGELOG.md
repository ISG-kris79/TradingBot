# Changelog

이 프로젝트의 모든 주요 변경 사항은 이 파일에 문서화됩니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

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
