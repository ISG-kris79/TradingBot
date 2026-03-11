# 🚀 TradingBot v1.2.7 Release Notes

> ⚠️ Legacy History Document
> This file records release details at that specific version point in time.
> For current runtime architecture/procedures, use `README.md` and the `Unreleased` section of `CHANGELOG.md`.

## ✨ 새로운 기능

### 🔽 트레이 숨기기 기능

- **창 최소화 → 트레이로 숨김**: 작업 표시줄에서 사라지고 시스템 트레이에 아이콘 표시
- **닫기(X) → 백그라운드 실행**: 프로그램 종료가 아닌 트레이로 이동하여 백그라운드에서 계속 실행
- **트레이 아이콘 더블클릭**: 창 복원 및 활성화
- **트레이 컨텍스트 메뉴**: 우클릭으로 "열기" 또는 "종료" 선택 가능
- **알림 풍선**: 트레이 이동 시 사용자 안내 메시지 표시

## 🔒 보안 (v1.2.0 계승)

### AES256 암호화

- **ConnectionString 암호화**: 데이터베이스 연결 정보 보호
- **API 키 암호화**: Binance, Telegram 등 API 키 안전 저장
- **범용 복호화**: 모든 PC에서 동일한 암호화 키로 복호화 가능
- **User Secrets 지원**: 개발 환경에서 안전한 설정 관리

## 🐛 버그 수정

### 버전 표시 오류 해결

- **문제**: v1.2.3이 표시되던 문제
- **원인**: 게시 폴더의 DLL이 이전 버전으로 잠겨있었음
- **해결**: 완전히 새로운 경로로 빌드 및 패키징
- **결과**: 로그인창과 메인창에 정확한 v1.2.7 표시

### 데이터베이스 연결 안정성

- **ConnectionString 복호화 실패 방지**: User Secrets와 appsettings.json 동기화
- **암호화 설정 통일**: IsEncrypted 플래그 일관성 유지
- **오류 메시지 개선**: "데이터베이스를 복호화할 수 없습니다" 오류 근본 해결

## 🎨 UI/UX 개선

### 트레이 아이콘 통합

- 시스템 트레이에서 앱 제어 가능
- 백그라운드 실행 중에도 빠른 접근
- 불필요한 작업 표시줄 아이콘 제거 옵션

### 사용자 피드백

- 트레이 이동 시 알림 풍선으로 명확한 안내
- 더블클릭으로 즉시 복원 가능

## 🔧 기술적 변경사항

### 의존성 업데이트

- **Hardcodet.NotifyIcon.Wpf 2.0.0** 추가: 트레이 아이콘 라이브러리
- 기존 패키지 버전 유지 (안정성)

### 아키텍처 개선

- **트레이 라이프사이클 관리**: 창 닫기와 실제 종료 분리
- **상태 관리 강화**: `_isRealClose` 플래그로 종료 의도 구분
- **이벤트 핸들러 최적화**: StateChanged, Closing 이벤트 처리

## 📦 다운로드

- **TradingBot-win-Setup.exe** (146.1 MB) - 권장
- **TradingBot-win-Portable.zip** (143.6 MB) - 휴대용
- **TradingBot-1.2.7-full.nupkg** (143.6 MB) - Velopack 패키지
- **TradingBot-1.2.7-delta.nupkg** (0.2 MB) - v1.2.6 → v1.2.7 업데이트 패치

## 🔄 업데이트 방법

### 기존 사용자 (v1.2.x)

1. 앱 실행 시 **자동 업데이트 알림** 확인
2. "업데이트" 버튼 클릭하여 자동 설치
3. 재시작 후 v1.2.7로 업그레이드 완료

### 기존 사용자 (v1.1.x)

1. 앱 실행 시 자동 업데이트 또는
2. Setup.exe 다운로드 후 수동 설치

### 신규 설치

1. **TradingBot-win-Setup.exe** 다운로드
2. 실행하여 설치 진행
3. 설치 완료 후 로그인

## 📋 시스템 요구사항

### 필수

- **운영체제**: Windows 10 (1809+) / Windows 11 x64
- **.NET Runtime**: 9.0 (설치 프로그램에 포함)
- **메모리**: 최소 4GB RAM (권장 8GB+)
- **디스크**: 최소 500MB 여유 공간

### 선택

- **Visual C++ Redistributable**: Reinforcement Learning 기능 사용 시 필요
- **인터넷 연결**: Binance API, 텔레그램 알림 사용 시 필수

## 🎯 사용 팁

### 트레이 기능 활용

- **매매 중 모니터링**: 창을 최소화하고 트레이 알림으로 상태 확인
- **멀티태스킹**: 작업 표시줄 공간 절약하며 백그라운드 실행
- **빠른 접근**: 더블클릭 한 번으로 즉시 창 복원

### 완전 종료 방법

1. 시스템 트레이에서 TradingBot 아이콘 우클릭
2. "종료" 메뉴 선택
3. 또는 메인 화면에서 로그아웃 → 프로그램 종료

## ⚠️ 알려진 이슈

- **첫 실행 시 Windows Defender 경고**: 서명되지 않은 앱으로 인식될 수 있음 (정상)
- **TorchSharp 초기화 실패**: Visual C++ Redistributable 미설치 시 RL 기능 제한 (ML.NET은 정상 작동)

## 📝 다음 버전 계획 (v1.2.8)

- [ ] 멀티 거래소 지원 확대 (Bybit 통합)
- [ ] 실시간 차트 개선
- [ ] 백테스트 결과 내보내기 (CSV, PDF)
- [ ] 텔레그램 봇 명령어 확장

---

**Full Changelog**: [v1.2.6...v1.2.7](https://github.com/YourRepo/TradingBot/compare/v1.2.6...v1.2.7)

## 💬 피드백 및 버그 제보

- **이슈 등록**: [GitHub Issues](https://github.com/YourRepo/TradingBot/issues)
- **텔레그램**: @your_telegram_channel
- **이메일**: <support@tradingbot.com>

---

**개발**: TradingBot Team  
**릴리즈 날짜**: 2026-02-27  
**빌드**: 1.2.7+8bc280cc
