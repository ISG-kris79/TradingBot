# Changelog

이 프로젝트의 모든 주요 변경 사항은 이 파일에 문서화됩니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

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
