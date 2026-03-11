# TradingBot v2.1.2

> ⚠️ 레거시 이력 문서
> 본 문서는 해당 버전 시점의 릴리스 기록입니다.
> 현재 운영 아키텍처/절차의 기준은 `README.md`와 `CHANGELOG.md`의 `Unreleased` 섹션입니다.

## 📅 릴리스 정보
- **Version**: 2.1.2
- **Release Date**: 2026-03-04
- **Type**: Patch Release

## ✨ 주요 변경사항

### 1) 전략/시그널 고도화 (ADX 듀얼 모드)
- `TransformerStrategy`에 **ADX 기반 횡보/추세 모드 자동 분기** 추가
- 횡보장(`SIDEWAYS`) 진입 시 TP/SL 커스텀 파라미터를 신호에 포함
- 추세장(`TREND`) 진입 시 `+DI/-DI` 방향 필터 적용

### 2) 주문 실행/모니터 연동 강화
- `TradingEngine`에서 Transformer 신호의 `mode/TP/SL` 인자를 주문 경로로 전달
- `PositionMonitorService`에 횡보장 전용 청산 규칙 적용
  - 커스텀 손절 우선
  - 중단선 도달 시 50% 부분익절
  - 잔여 물량 본절 보호/청산

### 3) 설정 UI 확장 및 유효성 검증
- `SettingsWindow`에 Transformer 전용 탭 추가
  - ADX period/threshold, RSI/거래량 기준, 밴드 터치/손절 배수
- 저장 시 입력 검증 강화
  - General + Transformer 범위 검증
  - 상호 조건 검증(예: `TP2 > TP1`, `LONG RSI Max < SHORT RSI Min`)

### 4) 버전/문서 정리
- 프로젝트 버전 상향
  - `Version`: `2.1.2`
  - `AssemblyVersion`: `2.1.2.0`
  - `FileVersion`: `2.1.2.0`
- `CHANGELOG.md`에 `2.1.2` 항목 추가

## ✅ 검증 결과
- `dotnet build` 성공
- `dotnet test` 성공
- 기존 경고는 유지(이번 패치로 인한 신규 컴파일 오류 없음)

## ⚠️ 배포 전 확인사항
- `v2.1.2` 태그는 현재 생성되어 있음
- 워킹트리에 미커밋 변경사항이 있으므로, 릴리스 반영 시에는 커밋/푸시 후 패키징 권장

---

문의/이슈: GitHub Issues
