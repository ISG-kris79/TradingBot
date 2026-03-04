# Changelog

이 프로젝트의 모든 주요 변경 사항은 이 파일에 문서화됩니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

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
