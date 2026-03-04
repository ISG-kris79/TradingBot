# 📝 Project To-Do List (Phase 11+)

## 🚀 진행 중인 작업 (In Progress - Phase 11: 플랫폼 확장 및 웹 대시보드)

### 1. 고급 분석 도구 (Advanced Analytics)

- [x] **Walk-Forward Analysis**: 전진 분석을 통한 전략 과최적화(Overfitting) 방지 및 검증 시스템.
- [x] **Monte Carlo Simulation**: 몬테카를로 시뮬레이션을 통한 전략의 파산 확률 및 기대 수익 범위 분석.

### 2. 거래소 기능 고도화 (Exchange Enhancements)

- [x] **Bybit API 안정화**: 메서드 시그니처 검증 및 에러 핸들링 강화 → Bybit V5 API 완전 구현, HandleError 강화 완료.
- [x] **Binance Batch Orders**: 대량 주문 처리를 위한 Batch Order API 구현 (Grid Strategy 최적화) → GridStrategy를 IExchangeService 기반으로 리팩터링 완료.
- [x] **Binance Portfolio Margin**: 포트폴리오 마진 모드 지원 검토 → Multi-Assets Mode 및 Position Mode (Hedge/One-way) API 구현 완료.

### 3. 웹 서비스 고도화 (Web Dashboard Enhancements)

- [x] **실시간 포지션 종료**: 웹 대시보드 버튼을 통한 실시간 포지션 시장가 종료 기능 연결.
- [x] **멀티 심볼 차트**: BTC 외에 다른 종목들의 실시간 차트 선택 기능 추가.

### 4. Bybit 통합 완료 (Bybit Integration Complete) ✅

- [x] **API 호환성 수정**: Bybit.Net V5 API 완전 구현, 모든 IExchangeService 메서드 구현 완료.
- [x] **BybitSocketConnector**: WebSocket 실시간 데이터 수신 구현 (Ticker, Kline, Position, Order 구독).
- [x] **Batch Order 지원**: PlaceBatchOrdersAsync 구현 (순차 처리 방식).

---

## 📋 핵심 기술 부채 및 해결 과제 (Core Technical Debt)

### 1. MarketDataManager 타입 충돌 및 API 호환성 (Critical) ✅

- [x] Binance.Net과 Bybit.Net 간 `OrderSide`, `KlineInterval`, `OrderStatus` enum 충돌 해결 → using alias로 해결 완료.
- [x] Binance.Net v12.x API 변경사항 반영 → `MultiAssetMode` 속성명 변경 완료
- [x] ExchangeType 네임스페이스 충돌 해결 → TradingBot.Shared.Models로 통일 완료
- [x] Nullable ExchangeType 타입 충돌 해결 → GetValueOrDefault() 사용으로 수정 완료
- [x] Bybit.Net v6.x API 호환성 업데이트 → BybitKlineAdapter의 CloseTime 계산 완료

### 2. TradingBot.Shared 통합 완료 ✅

- [x] PositionInfo.Side 속성 추가
- [x] 중복된 모델 정의 제거 및 `TradingBot.Shared` 기반으로 통합 작업 진행
- [x] NotificationChannel, SecurityService 네임스페이스 통합
- [x] 모든 파일에 적절한 using alias 추가 (CandleData, TradeLog, ExchangeType 등)

---

## ✅ 완료된 작업 (Completed History)

- 모든 완료된 항목은 [TODO_COMPLETED.md](TODO_COMPLETED.md)에서 확인 가능합니다.

---

## 참고 문서 및 가이드

### API 문서

- **Binance Futures API**: <https://binance-docs.github.io/apidocs/futures/en/>
- **Bybit V5 API**: <https://bybit-exchange.github.io/docs/v5/intro>

### 라이브러리 문서

- **Binance.Net**: <https://github.com/JKorf/Binance.Net>
- **Bybit.Net**: <https://github.com/JKorf/Bybit.Net>
- **CryptoExchange.Net**: <https://github.com/JKorf/CryptoExchange.Net>

---

## 🎯 장기 로드맵 (Long-term Roadmap)

### Phase 12: 거래소 완전 통합 ✅

- [x] **Bybit API 완전 호환 및 안정화**
  - [x] 재시도 로직 구현 (Exponential Backoff, 최대 3회 재시도)
  - [x] Rate Limiting (동시 요청 10개 제한, 최소 100ms 간격)
  - [x] Circuit Breaker 패턴 (연속 5회 실패 시 5분 대기)
  - [x] 상세한 에러 코드 매핑 (20+ 에러 코드 설명)
  - [x] WebSocket 자동 재연결 (최대 5회, Exponential Backoff)
  - [x] Health Check 시스템 (30초마다 연결 상태 확인)
  - [x] 연결 상태 이벤트 (OnConnectionStateChanged)

### Phase 13: 고급 거래 기능 ✅

- [x] 복수 거래소 차익거래 자동화 (ArbitrageExecutionService.cs 294줄)
- [x] 거래소 간 자동 자금 이동 (FundTransferService.cs 226줄)
- [x] 통합 포트폴리오 리밸런싱 (PortfolioRebalancingService.cs 290줄)

### Phase 14: 고급 기능 통합 및 실운영 준비

- [x] **UI 통합 및 설정 구성**
  - [x] MainWindow에 고급 기능 서비스 초기화 (InitializeAdvancedFeaturesAsync)
  - [x] 이벤트 바인딩 (OnLog, OnOpportunityDetected, OnTransferCompleted, OnRebalancingCompleted)
  - [x] 서비스 시작/중지 메서드 구현 (StartAdvancedFeaturesAsync, StopAdvancedFeatures)
  - [x] AppConfig에 FundTransferSettings, PortfolioRebalancingSettings 추가
  - [x] MainWindow에 고급 기능 탭 UI 추가 (차익거래, 자금이동, 리밸런싱)
  - [x] 각 서비스의 시작/중지 버튼 및 상태 표시
  - [x] appsettings.json에 각 서비스 기본값 추가
  - [x] **아이콘 리소스 생성**: Python Pillow를 사용하여 캐릭터 이미지 및 ICO 변환 완료 (`TradingBot\trading_bot.ico`)

  - [x] **ArbitrageExecutionService**: 실제 시장가 주문 실행 경로 구현 완료 (SimulationMode 분기 및 실주문 경로 포함)
  - [~] **FundTransferService**: SimulationMode 경로 완료 (실제 출금/입금 API는 NotImplementedException으로 안전 보호)
  - [~] **PortfolioRebalancingService**: SimulationMode 경로 완료 (실제 매수/매도 API는 NotImplementedException으로 안전 보호)

- [x] **데이터베이스 로깅**
  - [x] ArbitrageExecutionLog 테이블 생성 및 저장 (SQL 스크립트)
  - [x] FundTransferLog 테이블 생성 및 저장 (SQL 스크립트)
  - [x] PortfolioRebalancingLog 테이블 생성 및 저장 (SQL 스크립트)
  - [x] RebalancingAction 테이블 생성 (부모-자식 관계)
  - [x] DbManager에 로깅 메서드 추가 (SaveArbitrageExecutionLogAsync, SaveFundTransferLogAsync, SaveRebalancingLogAsync)
  - [x] 각 서비스에 DbManager 주입 및 로깅 통합
  - [x] 통계 뷰 생성 (vw_ArbitrageStatistics, vw_FundTransferStatistics, vw_RebalancingStatistics)

- [x] **Telegram 알림 연동**
  - [x] TelegramService에 5개 알림 메서드 추가 (차익거래 기회, 실행 결과, 자금 이동, 리밸런싱)
  - [x] 각 서비스에 TelegramService 주입 및 알림 전송 로직 추가
  - [x] MainWindow에서 TelegramService 초기화 및 서비스 연결

- [ ] **안전성 및 테스트** (완료: 5/6)
  - [x] 시뮬레이션 모드 플래그 추가 (Models.cs, AppConfig.cs의 모든 설정: ArbitrageSettings, FundTransferSettings, PortfolioRebalancingSettings)
  - [x] appsettings.json 기본값 SimulationMode: true 설정 (기본값으로 안전성 확보)
  - [x] Database 설정 가이드 작성 (Database/SETUP_GUIDE.md 338줄)
  - [x] 리스크 관리 체크리스트 작성 (RISK_MANAGEMENT_CHECKLIST.md 316줄, 60+ 체크항목)
  - [x] **Unit tests 작성 완료** ✅ (TradingBot.Tests 프로젝트)
    - **26개 테스트 전부 통과** (성공률 100%, 실행 시간 1.7초)
    - **ArbitrageExecutionServiceTests**: 7개 테스트 (Constructor, AddExchange, SimulationMode, StartAsync, Stop)
    - **FundTransferServiceTests**: 8개 테스트 (Constructor, AddExchange, SimulationMode, RequestTransferAsync, MinTransferAmount)
    - **PortfolioRebalancingServiceTests**: 11개 테스트 (Constructor, AddExchange, SimulationMode, StartMonitoringAsync, RebalanceThreshold, TargetAllocation 검증)
    - xUnit 2.9.2 + Moq 4.20.72 프레임워크 사용
  - [ ] 통합 테스트 및 성능 검증 (24시간+ 실행 안정성)

---

## 🎉 최신 릴리스

### v2.0.17 (2026-03-02)

**🐛 버그 수정 (Critical)**

- fix: 청산가 조회 로직 개선 (PositionMonitorService.cs:290-304)
  - 기존: `decimal exitPrice = 0; if (exitPrice == 0)` 항상 true로 논리적 오류
  - 개선: 캐시 우선 조회 (`TickerCache`) → 0이면 API 폴백 (`GetPriceAsync`)
  - PnL 계산 정확도 향상 및 불필요한 API 호출 최소화

- fix: Race Condition 방지 (TradingEngine.cs:1287-1299)
  - 기존: 락 내 체크 → 락 해제 → 주문 실행 (동시 진입 시 중복 포지션 가능)
  - 개선: 락 내에서 임시 포지션 즉시 등록 (Quantity=0) → 주문 실행 → 성공 시 수량 업데이트
  - 증거금 초과 및 중복 진입 위험 제거

- fix: PUMP 전략 타임아웃 최적화 (TradingEngine.cs:1074, 1123)
  - 지정가 주문 대기 시간 5초 → 3초로 단축
  - 급등/급락 시 체결률 향상 및 기회 손실 최소화
  - 로그 메시지 일관성 유지 ("5초" → "3초")

- fix: 주문 성공 시 Quantity 업데이트 로직 추가 (TradingEngine.cs:1318-1344)
  - 메이저 전략에서 주문 성공 후 실제 체결 수량으로 포지션 업데이트
  - 부분 체결 대응 및 포지션 정확도 향상

- fix: 로그 출력 오류 수정 (TradingEngine.cs:1341)
  - 미정의 이벤트 `OnLog` → `MainWindow.Instance.AddLog`로 수정
  - 컴파일 에러 해결 (CS0103: 'OnLog' 이름이 현재 컨텍스트에 없습니다)

**✨ 개선사항**

- feat: 시장 데이터 자동 저장 확장 (MarketHistoryService, DatabaseService)
  - 4개 테이블 동시 저장: MarketCandles, CandleData, CandleHistory, MarketData
  - BTCUSDT 고정 제거 → 모든 추적 심볼 데이터 자동 저장
  - 저장 주기: 5분 (첫 저장은 엔진 시작 즉시)
  - 병렬 처리: Task.WhenAll로 4개 테이블 동시 저장 (성능 최적화)
  - UI 로그 추가: Console.WriteLine → MainWindow.Instance.AddLog
  - 저장 대상: KlineCache의 최근 20개 캔들 (심볼당)

**📝 영향도 분석**

- **청산가 조회**: PnL 통계의 정확성 향상, API 부하 감소
- **Race Condition**: 동시 신호 발생 시 안정성 대폭 향상 (증거금 초과 위험 제거)
- **타임아웃 최적화**: PUMP 전략 진입 성공률 향상 (특히 변동성 큰 구간)
- **Quantity 업데이트**: 부분 체결 시나리오에서 포지션 추적 정확도 향상
- **시장 데이터 저장**: ML 학습 데이터 자동 축적, 백테스트 데이터 확보

**🧪 권장 검증 항목**

- [ ] 동시 다발적 매수 신호 발생 시 중복 진입 방지 확인
- [ ] 청산 시 PnL 계산이 시장가와 일치하는지 확인
- [ ] PUMP 전략 3초 타임아웃 체결률 모니터링 (24시간)
- [ ] 부분 체결 시나리오 테스트 (낮은 유동성 코인)
- [ ] 4개 테이블 데이터 저장 확인 (5분 후 DB 조회)

---

### v2.0.16 (2026-03-02)

**🐛 버그 수정 (Critical)**

- fix: 실시간 ROI 계산 레버리지 누락 수정 (TradingEngine.cs)
  - UpdateRealtimeProfit에서 가격변동률만 계산하고 레버리지 미적용 오류
  - ROE = 가격변동률 × 레버리지 공식 추가
  - 메인창 DataGrid에 비정상적인 ROI 표시 문제 해결 (예: -7% → -27% 잘못 표시)
  - 예: 1% 가격 변동 × 20배 레버리지 = 20% ROE (이전: 1%로 잘못 표시)

- fix: 포지션 청산 로그 타입 오류 수정 (PositionMonitorService.cs)
  - TradeLog 생성자 파라미터 오해로 인한 컴파일 오류 해결
  - 5번째 파라미터는 Quantity가 아닌 AiScore (float)
  - hardcoded 0 → position.AiScore로 수정
  - DB 스키마: (Symbol, Side, Strategy, Price, AiScore, Time, PnL, PnLPercent)

**📝 참고사항**

- PUMP 전략의 "타임컷" 로직: 진입 후 1분 경과 + 손실권일 때 조기 청산
- 손실 최소화를 위한 설계로 Stop Loss -15%까지 기다리지 않음

### v2.0.15 (2026-03-02)

**🐛 버그 수정**

- fix: TransformerStrategy 시퀀스 길이 불일치 오류 수정 (필요 60 / 현재 150)
  - 예측 시 최근 SeqLen(60) 데이터만 전달하도록 수정
  - 모델 미준비 시 예측 스킵 및 안내 로그 개선
  - 모델/정규화 파라미터 로드 검증 강화

- fix: Telegram 메시지 미전송 이슈 대응
  - 로그인 시 암호화된 사용자 자격증명 로드 경로 개선 (DecryptOrUseRaw)
  - SendMessageAsync에 자동 재초기화, ChatId 검증, Markdown fallback 추가
  - 전송 실패 원인 로그 강화

**✨ 개선사항**

- feat: 엔진 시작 시 Transformer 초기 학습 1회 자동 트리거
  - 모델 미준비 상태에서 자동으로 학습/저장/로드 수행
  - 정기 재학습(1시간 주기)과 연계하여 모델 생성 누락 방지

**📦 다운로드**: [GitHub Releases](https://github.com/ISG-kris79/TradingBot/releases/tag/v2.0.15)
