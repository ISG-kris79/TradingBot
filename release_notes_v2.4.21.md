## 🚀 TradingBot v2.4.21

릴리스 날짜: 2026-03-10

### 🔧 수정사항

- **BEX64 크래시 완전 수정 (ucrtbase.dll c0000409 — C++ abort)**
  - 근본 원인: v2.4.19의 LoadModel() 더미 forward 테스트가 C++ 네이티브 abort 유발 (C# try-catch 불가)
  - TransformerTrainer.LoadModel(): 더미 forward 제거 → stats 파일 기반 아키텍처 검증
  - EntryTimingTransformerTrainer.LoadModel(): 더미 forward 제거 → 안전한 load + 예외 시 삭제
  - TransformerWaveNavigator.LoadModel(): 예외 처리 추가 — 호환 불가 모델 자동 삭제
  - TorchInitializer.InvalidateModelsIfVersionChanged(): 앱 버전 변경 시 모든 모델 파일 일괄 삭제
  - App.xaml.cs: 시작 시 모델 무효화 호출 추가

### 📦 다운로드

| 파일 | 설명 | 크기 |
|------|------|------|
| TradingBot-win-Setup.exe | Windows 설치 파일 (권장) | ~159MB |
| TradingBot-win-Portable.zip | 포터블 버전 | ~156MB |
| TradingBot-2.4.21-full.nupkg | Velopack 업데이트 패키지 | ~156MB |

### 📋 시스템 요구사항

- 운영체제: Windows 10/11 (64-bit)
- 프레임워크: .NET 9.0 Runtime (설치 파일에 포함)
- 권장 메모리: 4GB 이상
- 디스크 공간: 500MB 이상

### 🔧 설치 방법

1. `TradingBot-win-Setup.exe` 다운로드
2. 실행 파일을 열고 설치 마법사 진행
3. 설치 완료 후 바탕화면 또는 시작 메뉴에서 실행
4. 첫 실행 시 Settings에서 API Key 설정

### 🔄 업데이트 방법

- Velopack 자동 업데이트가 활성화되어 있어 새 버전 출시 시 자동 알림
- 또는 [Releases 페이지](https://github.com/ISG-kris79/TradingBot/releases)에서 최신 Setup.exe 다운로드

### ⚠️ 주의사항

- 처음 사용 시 시뮬레이션 모드로 충분히 테스트 권장
- API Key는 반드시 IP 제한 설정하여 사용
- 거래소 API Key 권한: 선물 거래 활성화 필요
- 텔레그램 알림을 받으려면 Bot Token과 Chat ID 설정 필요

### 📞 지원

- 이슈 제보: [GitHub Issues](https://github.com/ISG-kris79/TradingBot/issues)
- 문서: [README.md](https://github.com/ISG-kris79/TradingBot/blob/main/README.md)
- 변경 로그: [CHANGELOG.md](https://github.com/ISG-kris79/TradingBot/blob/main/CHANGELOG.md)
