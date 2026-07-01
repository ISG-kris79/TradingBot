# SYSTEM_MAP.md — 트레이딩 봇 전체 로직 지도

> **작업 시작 전 필수 정독.** 이 파일은 봇의 진입/청산/추적/모드/데이터 전 구조를 한 곳에 모은 지도다.
> 코드 수정·진단 전에 여기서 해당 서브시스템을 먼저 확인해서 "어디 있는지 몰라 헤매는" 것을 막는다.
> (line 번호는 대략 앵커 — 드리프트 가능. 심볼/메서드명으로 grep해서 확정할 것.)
> 최초 작성 2026-07-01 (5개 서브시스템 전수 조사 기반).

---

## 0. ⚠️ 자주 틀리는 지점 (READ FIRST)

1. **모드 = 바이낸스 테스트넷** ($10,000 테스트머니). 진짜 돈 아님. 시작 로그: `🎮 [Start] 시뮬레이션 모드 (바이낸스 테스트넷)`.
   - **함정:** `appsettings.json`의 `IsSimulationMode:false`(메인넷처럼 보임)와 **런타임 실제값(true=테스트넷)이 불일치.** 실제 구동은 테스트넷. appsettings만 보고 "메인넷 실거래"라 판단하지 말 것.
2. **"ACCOUNT_UPDATE_RESTORED / EXTERNAL_POSITION_INCREASE_SYNC / EXTERNAL_CLOSE" = 외부/수동 아님.** 수동매매는 0건. 전부 **봇 자신의 테스트넷 거래**가 추적 지연·재시작으로 "미추적"으로 오라벨된 것. → 이걸 "외부 포지션"으로 보고 입양 중단하면 봇 자기 포지션이 방치됨(v5.25.3 실수 → v5.25.5 롤백).
3. **손실 원인 = 봇 전략 자체**(모멘텀). 외부/표시 문제 아님. OOS 백테스트상 모멘텀·추세·MACD 전부 적자, 역추세(MeanRev)만 흑자 → v5.25.4 MeanRev 주력.
4. **성과 숫자는 BPH(BinancePositionHistory)가 유일 권위.** TradeHistory.PnL은 봇 추정(부정확). 성과 3화면 전부 BPH로 통일(v5.24.5).
5. **설정 필드 추가 시 4곳 동시 수정 필수** (안 하면 저장 안 됨): ①GeneralSettings 테이블 컬럼 ②sp_SaveGeneralSettings ③SaveGeneralSettingsAsync 파라미터 ④CopyTradingSettings. (로드는 sp_LoadGeneralSettings=SELECT * → Dapper 자동매핑이라 컬럼만 있으면 됨.)
6. **폐기된 진입 로직 복원 금지:** ENGINE_151(MACD모멘텀), BB_SQUEEZE, BB_WALK, H1M1, MemeKnn. 메서드는 남아있어도 스캔에서 영구 미호출.

---

## 1. 모드 (테스트넷/시뮬/실거래)

- **결정 코드:** `TradingEngine.cs` ~1865 `isSimulation = AppConfig.Current?.Trading?.IsSimulationMode`; `useTestnet = isSimulation && testnetKey && testnetSecret`.
- **분기:**
  - `isSimulation=true` + 테스트넷 키 있음 → `BinanceExchangeService(testnet)` → `testnet.binancefuture.com` **실제 테스트넷 주문**. TradeHistory `IsSimulation=True`.
  - `isSimulation=true` + 키 없음 → `MockExchangeService` (주문 안 냄, 순수 시뮬).
  - `isSimulation=false` → `BinanceExchangeService(real)` → `fapi.binance.com` **메인넷 실거래**. `IsSimulation=False`.
- **현재 구동:** 테스트넷 (시작 로그로 확정).
- **알려진 버그 A — IsSimulation 라벨 불일치:** 같은 세션인데 `LORENTZIAN` 진입은 `sim=True`, `ACCOUNT_UPDATE_RESTORED`는 `sim=False`. 진입 경로마다 IsSimulation 읽는 소스가 달라 생기는 불일치(거래소init는 런타임 true, TradeHistory 기록은 AppConfig false를 읽는 듯). 돈 위험은 아니나 데이터 신뢰도 저하 → 진입 기록의 IsSimulation 소스를 한 곳으로 통일 필요.
- 관련 파일: `AppConfig.cs`(IsSimulationMode/Testnet키), `BinanceExchangeService.cs`(FuturesBase testnet/real).

---

## 2. 진입 파이프라인

### 2.1 구동 경로
`ProcessTickerChannelAsync`(웹소켓 틱) → `_pendingAnalysisPrices[sym]` → `TryStartSymbolAnalysisWorker`(심볼당 1워커) → **`ProcessCoinAndTradeBySymbolAsync`**(핵심).

### 2.2 ProcessCoinAndTradeBySymbolAsync 순서 (= 우선순위)
1. 추적풀 가드: `EnsureActiveTrackingPoolFresh()`, 풀에 없으면 스킵(활성포지션은 면제)
2. 분석 간격 throttle: 메이저 1000ms / 알트 2000ms
3. warmup / ETA_TRIGGER 체크
4. **AnalyzeMeanRevEntryAsync** (1순위, 주력)
5. **AnalyzeRsi2ReversalEntryAsync** (2순위)
6. **AnalyzeLorentzianEntryAsync** (3순위, 보조) → 펜딩 등록
7. **CheckLorentzianPendingEntriesAsync** — 5m 양봉 마감 확인 후 실제 진입
8. 청산: CheckHybridExitAsync, CheckBearishReversalExitAsync

### 2.3 활성 진입 메서드 (TradingEngine.cs)
| 메서드 | source | TF | 트리거 요약 | SL | 쿨다운 |
|---|---|---|---|---|---|
| AnalyzeMeanRevEntryAsync | `MEANREV` | 5m | 1h내 −2%↓ + 종가>BB(20,2)중심 + ADX(14)>20 + 1h종가>SMA200(칼날회피) | 1.5×ATR(5m) | 2h |
| AnalyzeRsi2ReversalEntryAsync | `RSI2_REVERSAL` | 1h | RSI(2)<5 + 종가>SMA200 | 고정 −5% | 2h |
| AnalyzeLorentzianEntryAsync | `LORENTZIAN` | 15m | jdehorty KNN 신호 '전환'(1500봉 학습) → 펜딩 | 기본 | 15m |
| CheckLorentzianPendingEntriesAsync | `LORENTZIAN` | 5m | 신호 후 새 5m봉 양봉+현재가>시가 → 진입 | — | — |

- **가드 로직 공유:** LCC 신호 판단은 `Services/LorentzianV2/LorentzianGuard.cs`(`EvaluateEntry`). 현재 활성 게이트: KNN net≥4, NW커널 상승, DBB 과열아님. (REGIME/VOLATILITY는 OFF.) 이 파일은 백테스트(LorentzianValidator)와 **공유 컴파일**.
- **진입 실행:** 모두 `ExecuteAutoOrder(...)` → `PlaceEntryOrderAsync` → `IsEntryAllowed` 통과 시 체결.

### 2.4 폐기(복원금지) — 스캔 미호출
ENGINE_151(MACD모멘텀, v5.23.87 폐기, BTC/ETH -$159), BB_SQUEEZE/BB_WALK(v5.23.84, XMR -15% 사고), H1M1(메서드·쿨다운만 보존), AnalyzeMemeKnnEntryAsync(v5.23.86), SIMPLE-AI KNN 게이트(180일 -$21,374).

---

## 3. IsEntryAllowed 게이트 (IsEntryAllowedCore)

- **디바운스:** 동일 (symbol, source) 5초 캐시.
- **우회 규칙:** `isMeanRev = src.Contains("MEANREV")||src.Contains("LORENTZIAN")` → 모멘텀/고점 게이트 우회(눌림 진입이라 설계상). RSI2도 LORENTZIAN과 동행 우회.
- **주요 차단 사유 문자열** (`⛔ [GATE] {sym} {src} 차단 | reason=`):
  - 카테고리: `SPIKE_DISABLED`, `PUMP_DISABLED:360d_backtest_loss`, `SETTINGS_NOT_LOADED`
  - 슬롯/풀: `SLOT_FULL:{key}={n}/{max}`, `NOT_IN_TRACKING_POOL`, `MANUAL_CLOSE_COOLDOWN:{m}m`
  - 메이저: `MAJOR_DISABLED`
  - 시총: `MCAP_NOT_READY`, `MCAP_OUT_OF_TOP30`
  - **자가학습: `SCORECARD_BLOCKED (30d WR≤30% PnL≤-$30)`** ← 심볼 성과 부진 자동차단
  - 추세: `BELOW_1H_EMA20`, `BTC_1H_DOWNTREND`(MAJOR −0.5%/PUMP −0.3%), `RSI5M_DOWN`, `M15_BB_BELOW_0.7`, `M5_UPPER_WICK`, `PREV_15M_BEARISH`, `M15_RANGE_TOP`(우회없음), `ALT_RSI_FALLING_KNIFE`
  - LCC 전용: `LCC_RANGE_TOP`(유지), `LCC_BELOW_1H_EMA20`(v5.24.2 OFF), `LCC_BTC_1H_DOWNTREND`(v5.24.1 OFF)
- **카테고리 분류:** `DbManager.ResolveTradeCategory(symbol, signalSource)` — BTC/ETH/SOL/XRP/BNB→`MAJOR`; source에 H1M1/MEANREV/SQUEEZE/BB_WALK→해당; 기본 `LORENTZIAN`. (MEANREV/RSI2는 슬롯상 GENERIC/PUMP 통합 슬롯 사용.)

### 3.1 추적풀 (_activeTrackingPool)
- = **FixedMajorPool(BTC/ETH/SOL/XRP 4개) + 동적 알트**.
- 동적 조건: 시총 Top30(`MarketCapTracker.IsTopN`) + `PriceChangePercent>0`(상승중) + 메이저·활성포지션 제외. score=변동률×log10(거래대금). DynamicPoolSize=30.
- **함정:** 약세장이면 상승 알트가 적어 동적풀이 비고, 메이저만 평가됨(알트 미진입의 주원인 — 막힌 게 아니라 신호 약함).

---

## 4. 포지션 추적 & 라벨링 ⚠️ (버그 핫스팟)

### 4.1 _activePositions / IsOwnPosition
- 봇 진입 성공 즉시 `IsOwnPosition=true` 설정(슬롯 즉시 차감). 재시작 시 DB `GetOpenTradesAsync` 기반 복원.
- **슬롯 카운트는 IsOwnPosition=true만** 셈. 모니터/SL·TP는 IsOwnPosition 무관하게 붙음.
- account-update로 미추적 감지 시 `IsOwnPosition=false` + `EnsureOpenTradeForPositionAsync(...,"ACCOUNT_UPDATE_RESTORED")` 기록.

### 4.2 왜 봇 자기 테스트넷 거래가 "외부(ACCOUNT_UPDATE_RESTORED)"로 라벨되나 — 근본원인
1. **예약등록 ↔ DB저장 시간차 레이스:** 봇이 `_activePositions`에 즉시 넣지만 DB 오픈행 저장은 별도 async. 그 사이 account-update 도착 → `wasTracked=false` → RESTORED 기록.
2. **재시작 시 진행중 주문 누락:** 재시작 후 `GetOpenTradesAsync`엔 방금 낸 주문이 아직 없음 → 다음 account-update에서 미추적.
3. **`IsRecentBotEntry` 10초 창 부족:** 다청크 partial fill(예: METUSDT 8청크)이 10초 넘으면 후반 청크가 `EXTERNAL_POSITION_INCREASE_SYNC`로 오분류. (`_recentBotEntries`, `MarkBotEntryInProgress`, `IsRecentBotEntry(withinSeconds=10)`)
4. **같은 라벨 남용:** `ACCOUNT_UPDATE_RESTORED`가 (진짜외부 / 봇미추적 / 재시작복원) 3경우에 다 쓰여 분석 불가.
- **개선 방향(미적용):** IsRecentBotEntry 10→30초, 재시작 시 in-flight 주문 포함, 봇 진입 시 DB 오픈행 동기 저장 후 account-update 수락, 라벨 3종 분리.
- **금지:** 이 "외부" 라벨을 근거로 입양 중단/보호 해제 하지 말 것(=봇 자기 포지션 방치). v5.25.5에서 항상 보호로 롤백됨.

---

## 5. 청산 / SL·TP / 주문 수명주기

- **진입 직후 등록:** `Services/OrderLifecycleManager.cs` `RegisterOnEntryAsync` → 기존주문 취소 + SL + 부분TP + Trailing 등록(ROE→가격 변환). 등록 쿨다운 30초.
- **모니터:** `PositionMonitorService.cs` `MonitorPositionStandard`(메이저/일반) / `MonitorPumpPositionShortTerm`(PUMP). 400ms 루프.
  - 3단계 보호: ①본절(highestROE≥breakEven) ②부분익절(≥profitLock, 40%) ③타이트 트레일링(≥tightTrailing). 메이저 backstop 손절.
  - 진입소스별 청산: `RSI2` → 1h 종가>EMA10 회복 익절; `LORENTZIAN` → 8봉(2h) 시간정지; 반전캔들 청산(5m); MACD 데드/골든크로스.
- **청산 실행 통일:** `ExecuteMarketClose` → 미체결주문 취소 → reduceOnly 시장가 → 체결폴링(최대 8회) → PnL계산 + DB저장. 부분청산 `ExecutePartialClose` → Trailing 재등록.
- **TP/SL 기본값:** `Models.cs` `TradingSettings`(Major*/Pump*/기본 *Roe). **현재값은 Models.cs에서 직접 확인**(과거 문서값과 다를 수 있음). DB `GeneralSettings`(유저 저장값)가 코드 기본값을 오버라이드. 사용자 설정 임의변경 금지.
- **외부청산 동기화:** 로컬 있고 거래소 없음 → `EXTERNAL_CLOSE_SYNC` 기록.

---

## 6. 데이터 모델 & 설정 영속화

### 6.1 3테이블
| 테이블 | 내용 | PnL | 권위 |
|---|---|---|---|
| TradeHistory | 봇 진입/청산 쌍 | 봇 추정 `(exit−entry)×qty` | ⭐ 부정확 |
| TradeLogs | 모든 이벤트 미러(감사) | 전달값 | ⭐ |
| **BinancePositionHistory (BPH)** | 바이낸스 체결→포지션 그룹핑 | **RealizedPnl − Commission = NetPnl** | ⭐⭐⭐ 권위 |
- BPH: `Services/BinancePositionHistorySync.cs` — `GetUserTradesAsync`→qty누적0 그룹핑, 5분주기 동기화(첫 90일 백필). UNIQUE(UserId,Symbol,PositionSide,OpenTime).
- 중복차단: TradeHistory는 진입시각±90초 + symbol dedup(v5.24.0). BPH는 UNIQUE 제약.

### 6.2 성과 3화면 (전부 BPH 통일, v5.24.5)
- 좌측 메이저/알트 일별: `RefreshCategoryStatsAsync`/`GetTodayStatsFromBphAsync` — BPH NetPnl, OpenTime(진입시간) 기준, UserId 필터.
- 매매기록 탭: `RefreshPositionHistoryFromDbAsync` — BPH TOP500.
- 성과분석: `LoadPerformanceDataAsync` — BPH, OpenTime 그룹.
- 진단 스크립트: `query-daily-perf.ps1`(일별 SUM by 진입시간).

### 6.3 GeneralSettings 영속화 — **4곳 동시 수정 필수**
1. 테이블 컬럼 (`GeneralSettings`) — 없으면 마이그레이션(예: `DbProcedures.cs`의 `migrate_GeneralSettings_Scalp` 패턴, sp_Save보다 먼저 실행)
2. `sp_SaveGeneralSettings` (DbProcedures.cs) — 파라미터 + UPDATE SET + INSERT
3. `SaveGeneralSettingsAsync` (DbManager.cs ~2214) — 익명객체 필드
4. `CopyTradingSettings` (MainWindow.xaml.cs ~148) — 인메모리 복사
- 로드: `sp_LoadGeneralSettings` = `SELECT *` → Dapper 자동매핑(컬럼만 있으면 OK).
- 저장 흐름: SettingsWindow 저장 → appsettings.json 기록 + `ApplyGeneralSettings`(CopyTradingSettings) + `SaveGeneralSettingsAsync`(DB) → 즉시 reload.
- **함정 실사례(v5.25.2):** ScalpAuto 필드가 4곳 중 3곳 누락→저장 안 됨. 4곳 다 채워야 함.

---

## 7. 배포

- 원격: `Tradingbot`(origin 아님). `RELEASE_CHECKLIST.md` 필수.
- 파이프라인: 버전 bump(csproj) + CHANGELOG → commit/push → `dotnet publish` → `publish-and-release.ps1` → **Portable.zip 수동 업로드**(publish-and-release가 매번 누락) → `gh release edit --latest` → 태그(gh release create가 원격태그 자동생성) → **릴리스 10개 유지**(초과분 `gh release delete --cleanup-tag`).
- 봇 강제종료 금지(Velopack 자동 업데이트). commit/push 자동OK, release/publish는 명시승인.

---

## 8. 진단 도구 (Tools/LorentzianValidator, `dotnet run -c Release -- <mode>`)
- `--near-entry`: 라이브 가드 기준 진입 임박도 스캔
- `--macdrsi`: MACD+RSI 매매법 검증(클릭베이트 적자 확인)
- `--meanrev-folds` / `--trend1h-folds`: K폴드 OOS 검증(MeanRev만 흑자)
- `--logic-180d`/`--final`/`--diagnose` 등 (CLAUDE.md 참조)
- 루트 `query-*.ps1` / `diag-*.ps1`: 라이브 DB 진단(AES 복호화 패턴, ASCII 라벨 권장).
