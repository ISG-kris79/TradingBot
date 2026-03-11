## 빠른 개요

이 레포는 Windows WPF 기반의 트레이딩 봇입니다. UI 엔트리포인트는 `TradingBot/MainWindow.xaml(.cs)`이며, 실제 매매/분석 로직은 `TradingBot/TradingEngine.cs`에 집중되어 있습니다. AI·ML 관련 코드는 `MLService.cs`, `AITrainer.cs`, `AIPredictor.cs`, `TensorFlowTransformer.cs`에 흩어져 있으며 ML.NET + TensorFlow.NET 기반 추론·학습(인프로세스)을 사용합니다.

## 빠른 시작 (개발자용)
- 빌드: `dotnet build TradingBot.sln -c Debug` 또는 `dotnet build TradingBot/TradingBot.csproj`
- 실행(디버그): Visual Studio에서 솔루션 열기(권장, WPF) 또는 생성된 exe 실행: `TradingBot/bin/Debug/net9.0-windows/TradingBot.exe`
- 출력 폴더에 ML.NET `.zip` 모델 파일이 있으면 추론이 동작합니다 (없어도 실시간 학습 가능).

## 아키텍처(핵심 컴포넌트)
- UI: `MainWindow.xaml(.cs)` — 전역 싱글톤 `MainWindow.Instance`를 통해 로그/UI 갱신 호출
- 엔진: `TradingEngine.cs` — WebSocket/REST로 시장 데이터 수집, 지표 계산, AI 점수 산출, 주문 실행 흐름
- 소켓: `BinanceSocketService.cs`, `BinanceSocketConnector.cs` — Binance.Net 기반 실시간 가격 스트림
- 주문: `BinanceOrderService.cs` (미완성/플레이스홀더) 및 `TradingEngine` 내 `PlaceOrder` 호출
- DB: `DbManager.cs` — Dapper + `Microsoft.Data.SqlClient` 사용, 데이터 조회/저장/대량삽입/BulkCopy 구현
- ML/AI:
  - ML.NET 학습/추론: `MLService.cs`, `AITrainer.cs`, `AIPredictor.cs` (zip 모델 사용)
  - TensorFlow 경로: `TensorFlowTransformer.cs`, `TensorFlowEntryTimingTrainer.cs` (인프로세스 백그라운드)
- 통합(알림): `TelegramService.cs` — Telegram.Bot 이용, 봇 토큰/채팅 ID 하드코딩됨

## 데이터 흐름(요약)
1. Binance WebSocket에서 실시간 캔들/틱 데이터 수신 (`BinanceSocketClient`)
2. `TradingEngine`이 지표(RSI, Bollinger 등) 계산 및 `AIPredictor`/`TensorFlowEntryTimingTrainer` 호출
3. 결정 결과는 UI(`MainWindow.Instance.RefreshSignalUI`)와 DB(`DbManager`)에 반영
4. 주문 실행은 `TradingEngine`에서 Binance REST/Trading API 호출

## 프로젝트·운영상의 중요 관찰/규칙
- 모델 파일명: `AIPredictor`는 `Model.zip`, `MLService`는 `TradingModel.zip`을 사용합니다. TensorFlow 경로는 인프로세스 초기화 기반으로 동작합니다.
- DB/토큰 하드코딩: `DbManager.cs`, `TelegramService.cs`, `TradingEngine.cs`에 민감 정보(API 키, DB 비밀번호, Telegram 토큰)가 코드에 직접 포함되어 있습니다. PR/변경 시 반드시 비밀관리(환경변수/secret manager)로 대체하세요.
- UI 로그 관례: 로그/알림은 대부분 `MainWindow.Instance?.AddLog` 또는 `AddAlert`로 출력됩니다. 에러/상태 노출 방식 통일성 참고하세요.
- 비동기 패턴: 엔진은 WebSocket 콜백 내에서 `Task.Run`으로 무거운 작업을 오프로드합니다. 장기 작업(학습 등)은 CancellationToken을 받아야 합니다.

## 의존성(주요 NuGet)
- `Binance.Net`, `Microsoft.ML`, `TensorFlow.NET`, `Microsoft.Data.SqlClient`, `Dapper`, `Telegram.Bot`, `LiveCharts.Wpf` — 확인: `TradingBot/TradingBot.csproj`

## 개발자용 팁 & 예시
- ML.NET 학습 재현: `AITrainer.TrainAndSave(List<CandleData>)` 또는 `MLService.Train(...)`을 호출해 로컬에서 `.zip` 모델 생성 가능.
- Transformer 학습: `TensorFlowEntryTimingTrainer.TrainAsync(...)` 경로로 백그라운드 학습.
- 스케줄링: `MLService.RunHourlyLearningTask`는 별도 CancellationToken으로 동작 — 시작/중지를 `MainWindow` 또는 `TradingEngine`에서 관리함.
- DB 스키마 기대: `DbManager` 쿼리(예: `TradeLogs`, `MarketData`, `MarketCandles`)에 맞춘 테이블이 필요합니다. 로컬 개발 시 더미 데이터 삽입 권장.

## 어디서 시작하면 좋을지 (작업 카테고리)
- 빠른 버그 확인/실행: `MainWindow.xaml.cs` → `TradingEngine.StartScanningOptimizedAsync` 흐름 추적
- ML/모델 작업: `MLService.cs`/`AITrainer.cs`(ML.NET 학습 파이프라인), `TensorFlowTransformer.cs`(TensorFlow 기반 경로)
- DB 관련: `DbManager.cs`에서 연결 문자열과 주요 쿼리 확인
- 통합/배포: `TradingBot/TradingBot.csproj`에서 Output 설정 확인

## 제한 및 미확인 사항 (AI 에이전트에게 알려둘 것)
- 테스트 스위트가 없습니다. 코드 변경 후 수동 실행과 Visual Studio 디버깅으로 검증해야 합니다.
- 일부 서비스(예: `BinanceOrderService`)는 구현이 비어있거나 미완성입니다. 주문 로직 변경 시 `TradingEngine` 내 `Execute*` 메서드와 연계 검토 필요.

피드백: 이 파일에 더 포함할 세부사항(예: 특정 함수 설명, 추가 진입점, DB 스키마 샘플)을 알려주시면 바로 반영하겠습니다.
