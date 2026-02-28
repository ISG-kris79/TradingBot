# TradingBot v1.2.9 릴리스 노트

## 🐛 버그 수정

### StaticResourceHolder XAML 오류 해결
- **문제**: 로그인 창에서 MainWindow로 전환할 시 `System.Windows.Markup.StaticResourceHolder` 오류 발생
- **원인**: 
  - App.xaml에서 Color를 참조하는 Brush 리소스들
  - MainWindow.xaml의 `BasedOn="{StaticResource {x:Type Border}}"` 스타일
  - 창 전환 시 리소스 초기화 타이밍 문제

- **해결방법**:
  1. LoginWindow.xaml의 StaticResource 의존성 완전 제거
  2. App.xaml에서 Brush 리소스를 직접 색상 값으로 변경
  3. MainWindow.xaml에서 BasedOn 참조 제거 및 Color 직접 지정
  4. Application.ShutdownMode를 "OnExplicitShutdown"으로 설정

## ✨ 개선사항

### 로그인 UI 개선
- 회전 스피너 애니메이션 추가 (0° → 360°, 1초 주기)
- 진행률 바 시각화 (0% → 100%)
- 진행 상태 메시지 표시
- 5단계 진행 흐름:
  1. 인증 (20%)
  2. 설정 저장 (40%)
  3. 사용자 설정 로드 (60%)
  4. Telegram 초기화 (80%)
  5. 완료 (100%)

### 창 동작 변경
- **X 버튼**: 프로그램 종료 (이전: 트레이로 최소화)
- **최소화 버튼**: 트레이로 전환 (이전: 창 최소화)

## 📋 변경된 파일

- `TradingBot/LoginWindow.xaml` - StaticResource 의존성 제거, 프로그레스 UI 추가
- `TradingBot/LoginWindow.xaml.cs` - 5단계 진행률 업데이트 로직
- `TradingBot/MainWindow.xaml` - StaticResource 참조 제거
- `TradingBot/MainWindow.xaml.cs` - 창 종료/최소화 동작 변경
- `TradingBot/App.xaml` - StaticResource 기반 Brush를 직접 색상으로 변경
- `TradingBot/App.xaml.cs` - ShutdownMode 설정

## 🔧 기술 세부사항

### 빌드 정보
- 프레임워크: .NET 9.0 (net9.0-windows)
- 빌드 타입: Release (자체 포함, Single File)
- 실행 파일 크기: ~427MB

### 테스트 완료 항목
- [x] 첫 번째 로그인 성공 (오류 없음)
- [x] 프로그레스 바 애니메이션
- [x] MainWindow 전환 완료
- [x] 창 최소화/종료 동작 검증

## 📝 설치 및 실행

1. `Release_v1.2.9/TradingBot.exe` 다운로드
2. 별도 설치 불필요 (자체 포함 실행 파일)
3. 직접 실행하면 됩니다

## ⚠️ 주의사항

- 데이터베이스 연결 정보는 코드에 하드코딩되어 있으니 필요시 환경 변수로 변경 권장
- Telegram 토큰도 마찬가지로 보안 관리 필요

---
**Release Date**: 2026-02-28  
**Version**: 1.2.9  
**Status**: 안정 버전
