# Changelog

이 프로젝트의 모든 주요 변경 사항은 이 파일에 문서화됩니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [Unreleased]

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
