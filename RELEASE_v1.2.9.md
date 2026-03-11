# TradingBot v1.2.9

> ⚠️ 레거시 이력 문서
> 본 문서는 해당 버전 시점의 릴리스 기록입니다.
> 현재 운영 아키텍처/절차의 기준은 `README.md`와 `CHANGELOG.md`의 `Unreleased` 섹션입니다.

## 🗑️ 주요 변경사항

### 웹서버 기능 제거

- ApiServerService 완전 제거
- 웹 API 엔드포인트 제거 (포트 8080 사용 중단)
- TradingBot.Web 프로젝트 솔루션에서 제외
- wwwroot 디렉터리 제거

### 프로젝트 구조 개선

- `Microsoft.NET.Sdk.Web` → `Microsoft.NET.Sdk`로 변경
- 순수 WPF 데스크톱 애플리케이션으로 단순화
- `Microsoft.Extensions.Hosting` 패키지 추가 (Worker Service 지원)

### 빌드 최적화

- 불필요한 웹 관련 의존성 제거
- 빌드 시간 단축
- 출력 파일 크기 최적화

## 📦 설치 방법

### Windows 설치 프로그램 (권장)

1. `TradingBot-win-Setup.exe` 다운로드
2. 실행하여 자동 설치
3. 자동 업데이트 지원

### 포터블 버전

1. `TradingBot-win-Portable.zip` 다운로드
2. 압축 해제
3. `TradingBot.exe` 실행

## 💡 업그레이드 안내

기존 v1.2.8 사용자는 자동 업데이트를 통해 v1.2.9로 업그레이드됩니다.

**⚠️ 주의사항:**

- 웹 API 기능(포트 8080)을 사용하던 경우, 해당 기능이 제거되었습니다.
- 모든 기능은 WPF UI를 통해서만 사용 가능합니다.

## 🔧 시스템 요구사항

- Windows 10/11 (64-bit)
- .NET 9.0 Runtime (설치 파일에 포함)
- 최소 4GB RAM
- 500MB 디스크 공간

## 📝 전체 변경 로그

- 🗑️ WebServer/API 기능 완전 제거
- 🔧 프로젝트 SDK 변경 (Web → Standard)
- 📦 Microsoft.Extensions.Hosting 패키지 추가
- 🧹 TradingBot.Web 프로젝트 솔루션 제외
- ✨ 코드 베이스 단순화 및 정리

---

**다운로드:** Setup.exe (권장) | Portable.zip  
**릴리스 날짜:** 2026-02-28  
**버전:** 1.2.9
