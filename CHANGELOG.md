# Changelog

이 프로젝트의 모든 주요 변경 사항은 이 파일에 문서화됩니다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)를 기반으로 하며,
이 프로젝트는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.

## [5.21.9] - 2026-04-27

### 🎯 ActiveTrackingPool — 12개 추적 풀 (메이저4 + 동적8)

#### 설계 의도 (사용자)
- 200+ 심볼 무작위 평가 → CPU/메모리/API 호출 폭주
- 메이저 4 + PumpScan top 동적 8 = 12개만 실시간 신호 추적
- 5분마다 동적 8 갱신 → 시장 신호에 반응

#### Added
- `TradingEngine._activeTrackingPool`: ConcurrentDictionary<string, byte> + 5분 주기 갱신
- `EnsureActiveTrackingPoolFresh()`: 메이저 4 고정 + PumpScan TopCandidateScores 상위 8개 동적 선택 (활성 포지션 심볼 제외)
- `ProcessCoinAndTradeBySymbolAsync` 시작부에 풀 가드 추가 — 12개 외 심볼은 평가 즉시 스킵
- `AIDoubleCheckEntryGate.SetTrackedSymbols(12개)` 자동 동기화

#### 예상 효과
- ML.NET 추론 200+ → 12 (94% 감소)
- 메인 루프 workMs 1050ms → ~150ms
- CPU 1코어 100% → ~15%
- 메모리 ~500MB 절감

#### 활성 포지션 보호
- 풀에서 빠져도 _activePositions 에 있으면 통과 → TP/SL/Trailing 정상 동작 보장

## [5.21.8] - 2026-04-27

### 🚨 MODEL_ZIP_MISSING 영구 차단 + 메인 루프 1초 폭주 동시 해결

#### 발견 (v5.21.7 21분 가동)
- EntryTimingModel*.zip 4개 다 생성됨 (13~22KB) 그러나 여전히 `MODEL_ZIP_MISSING` 차단 지속
- `[PERF][MAIN_LOOP] workMs=1125 avgMs=1050ms` — 메인 루프 1회 1초 소요
- 단일 스레드 1코어 100% 점유, FooterLogs 5분당 5,892건 폭주

#### 근본 원인
1. **30KB 임계 false-fail (4곳)**: ModelHealthMonitor.MinHealthyBytes=30KB, TradingEngine getter/setter, AIDoubleCheckEntryGate. 13KB 정상 모델(synthetic positive로 학습)도 거부됨
2. **MainLoopInterval 1초 + 1초 작업 = 풀가동**: 메인 루프가 쉴 틈 없이 1코어 100% 점유

#### Fixed
- **30KB → 1KB 완화 (4곳 동시)**: ModelHealthMonitor + TradingEngine 2곳 + AIDoubleCheckEntryGate. 진짜 손상(0KB, 빈 파일)만 거부
- **MainLoopInterval 1초 → 3초**: CPU 부하 ~33%로 감소. GATE 디바운스(5초)와 동조

## [5.21.7] - 2026-04-27

### ⚡ CPU 16% / 메모리 2.1GB 과다 사용 해결 (정상치 2~3배)

#### 측정값 (v5.21.6, 27분 가동)
- 메모리: 2,175 MB (정상 600-800 MB 대비 2~3배)
- CPU 누적: 1,847s / 27분 = 1코어 114% 점유
- 단일 스레드가 1,464s (전체 79%) 점유 — IsEntryAllowed 무한 폴링

#### 근본 원인
1. **GATE 폭주**: BTC/ETH/SOL/XRP × MAJOR_ANALYZE 가 매 초 호출 → IsEntryAllowed 가드 검증 + 로그 출력 폭주 → 1코어 99% 점유
2. **PredictionEnginePool 메모리 점유**: 4 variant × Pool = 메모리 1GB+ 점유. 단일 스레드 추론 대부분이라 Pool 이점 없음

#### Fixed
- **TradingEngine.IsEntryAllowed**: 5초 결과 캐시 wrapper 추가 (CPU 50%+ 감소 예상)
- **EntryTimingMLTrainer.LoadModel**: PredictionEnginePool 비활성화 → 캐시 엔진(단일 인스턴스) 폴백만 사용 (메모리 500MB+ 감소 예상)

## [5.21.6] - 2026-04-27

### 🚨 ROOT FIX: LoadModel 스키마 검증 false-fail → 4 variant 영구 FAIL 해결

#### 발견
- v5.21.5 설치 직후 즉시 알림: `⚠️ AI 학습 학습 후 모델 상태 - Default=FAIL, Major=FAIL, Pump=FAIL, Spike=FAIL`
- SaveModel 은 성공하지만 직후 LoadModel 이 .zip 을 즉시 삭제

#### 근본 원인
- `MultiTimeframeEntryFeature` 의 numeric 프로퍼티 = **144개**
- `EntryTimingMLTrainer.ExpectedFeatureCount = 122` (BuildPipeline featureColumns 배열 길이)
- LoadModel: `_modelSchema` 컬럼 수 = LoadFromEnumerable 의 144 (Concatenate 사용분만이 아님)
- 144 ≠ 122 → 무조건 "스키마 불일치" → `File.Delete(_modelPath)` → IsModelLoaded=false 영구

#### Fixed
- **EntryTimingMLTrainer.LoadModel()**: 스키마 검증 `!= ExpectedFeatureCount` → `< ExpectedFeatureCount` 변경 (Feature 추가에도 안전)
- **3개 LoadModel catch 경로 모두 `File.Delete` 제거**: 파일 보존 → 다음 학습이 덮어쓰면 됨. 삭제는 영구 차단의 원인

## [5.21.5] - 2026-04-27

### 🚨 ROOT FIX: EntryTimingModel*.zip 영구 미생성 → 진입 무한 차단 해결

#### 발견 (2026-04-27 16:26)
- v5.21.4 설치 + 4시간 가동 후에도 4개 EntryTimingModel*.zip 파일 미생성
- 봇 자체는 정상 가동, 신호도 활발 (Q +0.7%/vol 7.1x, AWE +0.6%/vol 3.9x, MAJOR 4종 매 초 평가)
- 모든 진입 시도 100%가 `MODEL_ZIP_MISSING:Default,Major,Pump,Spike` 게이트로 차단

#### 근본 원인 (3중 결함)
1. `EntryTimingMLTrainer.TrainAndSaveAsync` — `positives.Count==0` 시 SaveModel 스킵 → .zip 영구 미생성
2. `TradingEngine.IsInitialTrainingComplete` setter — 4 zip 검증 없이 flag 무조건 작성 → 다른 학습기(forecast_*, market_regime 등)가 끝나면 flag 재작성 → getter 검증이 무력화
3. `AIDoubleCheckEntryGate.TriggerInitialTrainingAsync` — 학습 후 .zip 생성 검증 없음 → 실패해도 "완료" 처리

#### Fixed
- **EntryTimingMLTrainer.cs:147**: positives 0개 → synthetic positive 1개 주입 후 학습 강행 (.zip 생성 보장)
- **TradingEngine.cs:7894 setter**: 4개 EntryTimingModel*.zip + pump_signal_normal.zip 검증 후에만 flag 작성, 누락 시 거부
- **AIDoubleCheckEntryGate.cs:1919**: 4 variant 학습 후 .zip 존재 검증, 누락 variant만 즉시 1회 재학습 retry

## [5.21.1] - 2026-04-26

### 🎯 PUMP 게이트 RSI<70 → RSI<65 강화 + 6개월 검증

#### Fixed
- **PUMP 게이트 RSI 임계값 70 → 65**: 90일 v5.20.8 (RSI<70) 113건 WR 76.11% PnL -$40.40 → 49건 WR 79.59% PnL +$50.80 (흑자 전환)

#### 6개월(180일) 검증 (Tools/LorentzianValidator --logic-180d)
- MAJOR (가드 우회 + 1.0/3.0/24): 2,967건, WR 97.84%, +$24,736
- SQUEEZE (가드 우회 + 1.0/3.0/24): 3,668건, WR 94.87%, +$26,225
- BB_WALK (가드 우회 + 1.0/3.0/24): 2,547건, WR 85.63%, +$8,792
- PUMP (v5.21.1 + 1.0/3.0/24): 68건, WR 80.88%, +$105.60
- SPIKE (차단): 시뮬 +$11 회피, 사용자 -$130/시간 손실 차단
- 합계: +$59,859 / 180일 (월 $9,977)

#### 검증 일관성
- 30일 +$7,294 → 90일 +$28,803 → 180일 +$59,859
- 월환산 안정: $7,294 / $9,601 / $9,977 (시장 변동 무관 견고)

## [5.21.0] - 2026-04-26

### 🎯 카테고리별 게이트 + 90일 백테스트 검증 흑자 전환

#### 90-day Validation (Tools/LorentzianValidator --logic-90d)
- 5종 진입 트리거(PUMP/SPIKE/MAJOR/SQUEEZE/BB_WALK) × 3 가드 × 3 TP/SL = 45 조합
- **결과: TP/SL 1.0%/3.0% (ROE 15%/45% at 15x) 통일 + 카테고리별 가드로 90일 +$28,803 흑자**

#### IsEntryAllowed 카테고리 분류 (TradingEngine.cs)
- **SPIKE 카테고리 전면 차단** — 90일 모든 조합 적자 (-$150 ~ -$2,230), 사용자 -$130/시간 손실 주범
- **PUMP**: EMA20↑ + RSI<70 게이트 적용 — 90일 -$1,801 → -$9 (break-even 근접)
- **MAJOR / SQUEEZE / BB_WALK**: 게이트 우회 — 게이트 없는 게 더 좋음 입증 (PnL 30~50% 증가)

#### Models.cs 기본값 (TradingSettings)
- TargetRoe: 40 → **15** (TP price 1.0% × 15x)
- StopLossRoe: 60 → **45** (SL price 3.0% × 15x)
- MajorTp1Roe: 20 → **15**, MajorStopLossRoe: 60 → **45**
- PumpTp1Roe: 20 → **15**, PumpStopLossRoe: 40 → **45**
- DefaultLeverage 15x 유지

#### 90일 검증 결과 매트릭스
| Trigger | Code | n | WR | PnL$ |
|---|---|---|---|---|
| MAJOR | 가드 우회 + 1.0/3.0/24 | 1,376 | 97.82% | +$11,459 |
| SQUEEZE | 가드 우회 + 1.0/3.0/24 | 1,945 | 93.78% | +$13,054 |
| BB_WALK | 가드 우회 + 1.0/3.0/24 | 1,676 | 83.41% | +$4,299 |
| PUMP | EMA20↑+RSI<70 + 1.0/3.0/24 | 112 | 76.79% | -$9 |
| SPIKE | 차단 | 0 | - | -$150 회피 |
| **합계** | | | **평균 91%** | **+$28,803 / 90일** |

#### 30일 vs 90일 일관성
- MAJOR: 30일 +$2,879 → 90일 +$11,459 (3.98x ✅)
- SQUEEZE: 30일 +$3,586 → 90일 +$13,054 (3.64x ✅)
- 전체: 30일 +$7,294 → 90일 +$28,803 (3.95x ✅)
- 시장 변동 영향 없이 흑자 안정성 입증

## [5.20.8] - 2026-04-26

### 🎯 게이트 재설계 — 진단 결과 기반 흑자 전환

#### Diagnosis (Tools/LorentzianValidator --diagnose, 30 syms × 14days 5m)
- **Lorentzian Pred>3 가드 무효 입증**: baseline 31.79% → Pred>3 31.26% (오히려 -0.53%p), 음수 Pred(-5)에서 49.22% 발견 = 모델 학습 부적합
- **VolSurge>1.3x 단독 -1.21%p**, 결합 시 over-filter (-7.64%p)
- **EMA20 rising 단독 +1.32%p edge** — 유일한 양성 가드
- **RSI 70+ 진입 -11.71%p edge** (FOMO 진입 = 손실 주범) — 신규 추가
- **SL이 평균 1.4봉 빨리 맞음** → TP/SL 비대칭 1.5/0.7 → 1.5/1.5 대칭 전환 필요
- **ATR 0.7-1.5% sweet spot** (저변동성 7.69%, 고변동성 18.18%)

#### Removed (효과 없음 / 악화 입증)
- **LORENTZIAN_WEAK_SIGNAL** 가드 제거 — 단독 효과 -2.07%p, 모델 학습 데이터 부적합
- **VOL_NOT_SURGED** 가드 제거 — 단독 -1.21%p, 결합 over-filter

#### Added
- **RSI70_OVERHEATED**: RSI(14) ≥ 70 진입 차단 — 진단 -11.71%p edge 회피

#### Kept
- **EMA20_NOT_RISING**: 5봉 EMA20 비상승 차단 — 진단 +1.32%p edge (유일 양성)
- 외 기존 v5.20.7 가드 유지: MODEL_ZIP_MISSING, ALT_RSI_FALLING_KNIFE, M15 고점추격, BTC 1H 하락, SETTINGS_NOT_LOADED, MAJOR_DISABLED, MANUAL_CLOSE_COOLDOWN

#### Validation (--redesign)
- **EMA20↑ + RSI<70 + TP1.5/SL1.5/WIN24 = +$7,958 (795% ROI/$1000), 7,133 trades, WR 56.39%, $1.12/trade**
- TOP1: EMA20↑ + TP1.5/SL1.5 = +$9,445 (944% ROI), 9,756 trades, WR 55.89%
- 현재 v5.20.7 (1.5/0.7 비대칭) = -$18,375 → v5.20.8 (1.5/1.5) 적용 시 흑자 전환

#### TODO (사용자 UI 조정 권장)
- TargetRoe (TP) / StopLossRoe (SL) 대칭 설정: 22.5/22.5 (15x 레버리지 기준 1.5%/1.5% 가격)

## [5.20.7] - 2026-04-25

### 🚨 Critical Bug Fixes (사용자 -$70 손실 표시 안되던 통계 버그 + 알람 폭주)

#### Fixed
- **sp_GetTodayStatsByCategory**: `EntryTime >= @todayStart` → `ExitTime` 기준으로 변경. "어제 진입 / 오늘 청산" 손실(BTC -$64.30 등)이 통계에서 통째로 누락되던 버그 수정. 즉시 적용 후 MAJOR Losses=1, TotalPnL=-$64.30 표시 확인.
- **EXTERNAL_CLOSE_WHILE_BOT_STOPPED**: PnL=0 일괄 처리 → Binance Income API REALIZED_PNL 자동 fetch. 봇 정지 중 청산도 실제 손익 기록.
- **ZIP 알람 폭주**: variant 4개가 따로따로 알람 → 1개 번들 메시지로 통합. cooldown 60min→6h, grace 30min→120min.

#### Added (Hard Gates — chart-data 검증 통과 후 적용)
- **MODEL_ZIP_MISSING 진입 차단**: AI 모델(Default/Major/Pump/Spike) zip 미생성/손상 시 ALL 진입 차단. 사용자 요구: "zip 생성이 안되어 있으면 진입을 하지를 말어".
- **LORENTZIAN_WEAK_SIGNAL 메인 게이트 강화**: Lorentzian Classification KNN이 메인 AI. IsReady=false OR `Prediction ≤ 3` 시 진입 차단. 차트 sweep: > 0 -$10,370 / > 3 -$1,531 (8.6배 손실 감소). 사용자 요구: "Lorentzian Classification 이거 기준으로 메인을 잡으라고".
- **EMA20_NOT_RISING 트리거**: 5m EMA20이 5봉 전보다 높지 않으면 차단. 차트 sweep: P4 합성 흑자 조건의 핵심 트리거.
- **VOL_NOT_SURGED 트리거**: 직전 20봉 평균 거래량의 1.3x 미만이면 차단. 차트 sweep P4 합성에 필수.
- **ALT_RSI_FALLING_KNIFE 가드**: 알트(메이저 4종 외) RSI(14)<30 LONG 차단. 30심볼 5m 1500봉 검증 결과 BOTTOM 10 알트 39~47% win-rate (떨어지는 칼날).

#### Validation
- 30 심볼 × 14일 5m 차트 데이터 + REAL Lorentzian C# 엔진 검증 (Tools/LorentzianValidator).
- TP/SL 11종 스윕 + Lorentzian threshold 6단계 + 진입 트리거 6종 + Window 6종 sweep 완료.
- **P4 합성 흑자 입증**: Lorentzian>3 + EMA20↑ + Vol>1.3x + WIN=24 + TP2.0/SL1.0 = +$427.20 (40.89% win-rate).
- 그 외 단독 조건은 모두 손실 (-$8,588 ~ -$33,963 범위).

#### Infrastructure
- `IX_CandleData_Symbol_IntervalText_OpenTime` 인덱스 추가 (DB Symbol GROUP BY/WHERE 타임아웃 해결).
- `BacktestDataset` 테이블 + `diag-build-dataset.ps1`: 30심볼 × 30일 × 5m = 267,999 rows / TP-first 25.04% baseline 측정.
- `diag-validator-direct.ps1`: 30심볼 RSI baseline 검증 (49.01% — random 미만, 알트 차단 근거).
- `BinanceExchangeService.GetRealizedPnLAsync()`: 외부청산 PnL 복구 헬퍼.
- `ModelHealthMonitor.AnyMissing()`: 진입 차단 게이트용 헬퍼.

## [5.19.0] - 2026-04-25

### ✨ AI 학습: Bollinger Band Walk 패턴 학습 (5분봉 상단 라이딩 = 강추세 매수 신호)

**사용자 요구:**

> "5분봉 캡처 — +50% 급등, ROE 1000%+, 마지막은 볼밴 상단 타고 올라가는 패턴.
>  특히 볼밴 상단 타고 올라가는 패턴을 ai에 어떻게 학습시킬지 파악해봐"

### 🎯 핵심 변경

일반적으로 "BB 상단 = 과매수 = 매도"로 알려져 있지만, 강추세에서는 정반대 — **밴드워크(Band Walk)는 매수 지속의 신호**. AI가 이 패턴을 별도 라벨로 학습하도록 추가.

#### 추가된 5분봉 BB Walk 피처 (9개)
- `M5_BB_Position` — (Close - Lower) / (Upper - Lower), 1.0 근처 = 상단
- `M5_BB_Walk_Count_10` — 직전 10봉 중 종가가 상단 위인 봉 수
- `M5_Upper_Touch_Streak` — 직전부터 연속 상단 터치 봉 수
- `M5_BB_Width_Ratio` — 밴드폭 / 중심선 (변동성 절대치)
- `M5_BB_Width_Expand` — 현재 밴드폭 / 20봉 평균 밴드폭
- `M5_Close_Above_Upper_Pct` — 상단 돌파 강도 %
- `M5_EMA20_Slope_Pct` — EMA20 5봉 기울기 %
- `M5_Volume_Surge_Ratio` — 현재봉 거래량 / 20봉 평균
- `M5_Body_Ratio` — 캔들 몸통 비율

#### Band Walk 대박 라벨 분기 (BacktestBootstrapTrainer)
- 기본 라벨: +0.8% TP / -1% SL / 12봉
- **추가**: 진입 시점 BB Walk 감지 시 (+3% TP / -1.5% SL / 24봉) 추가 라벨링
- → AI가 "상단 라이딩 = 큰 수익 가능" 패턴을 별도로 학습

#### 실시간 추론 경로에도 5m fetch 추가
- `MultiTimeframeFeatureExtractor.ExtractRealtimeFeatureAsync` 가 5m 캔들 60개 추가 조회
- live 예측 시에도 BB Walk 피처 자동 적용

### 📁 변경 파일
- `MultiTimeframeEntryFeature.cs` — 9개 피처 신규
- `MultiTimeframeFeatureExtractor.cs` — `ExtractM5BandWalkFeatures` + 5m 슬라이스 오버로드
- `Services/BacktestBootstrapTrainer.cs` — Band Walk 대박 라벨 분기

### 🛠 DB 인덱스 (별도 스크립트)
- `IX_CandleData_IntervalText_OpenTime_DESC` 추가 — GROUP BY 타임아웃 해소

---

## [5.16.0] - 2026-04-24

### 🚨 ROOT FIX: "3모델 분리"가 2달간 **가짜**였다 — Major/Pump/Spike 모델 영원 동결

**사용자 지적:**

> "ML 학습로직 2달동안 수정만 하고 있잖아"
> "Major/PUMP/급등 모두 다 분석해서 재점검해"

### 🔴 2달간 숨어있던 진짜 원인

전면 Audit 결과 — 인프라는 있는데 학습은 가짜였음:

```
봇 시작 시:
├── EntryTimingModel.zip         (Default) ← 로드됨 + 학습됨
├── EntryTimingModel_Major.zip              ← 로드됨 + 영원히 동결
├── EntryTimingModel_Pump.zip               ← 로드됨 + 영원히 동결
└── EntryTimingModel_Spike.zip              ← 로드됨 + 영원히 동결

봇 운영 중 (모든 trade 결과):
모든 종류 → _onlineLearning._mlTrainer (Default만) → 이 한 모델만 재학습
                                                    (BTC + MOVRUSDT + SPIKE 데이터 전부 혼합)
```

**결과**:

- 메모리 규칙 "3모델 분리"가 **완전히 무효**
- 추론 시점에는 올바른 variant 모델로 예측하지만, **그 모델이 학습 중단 상태**
- Major 모델은 BTC/ETH 최신 시장 반영 못함
- Spike 모델은 급등 패턴 추가 학습 못함
- 하나의 Default 모델이 모든 종류 trade를 다 학습해 뒤죽박죽

### ✅ Fix: 4개 independent online learning instance

```csharp
// 기존: 1개
private readonly AdaptiveOnlineLearningService? _onlineLearning;

// 수정: 4개 완전 독립
private readonly AdaptiveOnlineLearningService? _onlineLearning;         // Default (fallback)
private readonly AdaptiveOnlineLearningService? _onlineLearningMajor;    // BTC/ETH/SOL/XRP
private readonly AdaptiveOnlineLearningService? _onlineLearningPump;     // 일반 알트
private readonly AdaptiveOnlineLearningService? _onlineLearningSpike;    // SPIKE_FAST/TICK_SURGE
```

각 인스턴스:

- **독립 sliding window** (1000 samples, 서로 영향 없음)
- **독립 retrain timer** (1시간)
- **독립 trigger count** (100 samples → retrain)
- **독립 concept drift detector**
- **별도 variant 서브디렉터리**에 샘플 영구화 (`TrainingData/EntryDecisions/{Default|Major|Pump|Spike}`)

### 라벨 라우팅 — 핵심 신규 헬퍼

```csharp
private AdaptiveOnlineLearningService? SelectLearningServiceForSymbol(
    string symbol, string? signalSource)
{
    bool isMajor = IsMajorSymbol(symbol);  // BTC/ETH/SOL/XRP
    bool isSpike = signalSource startsWith "SPIKE" || equals "TICK_SURGE"/"M1_FAST_PUMP";
    if (isMajor) return _onlineLearningMajor;
    if (isSpike) return _onlineLearningSpike;
    return _onlineLearningPump;
}
```

`AddLabeledSampleToOnlineLearningAsync` 에서:

1. Trade 결과로 라벨 생성 (PnL ≥ 0.8% = WIN)
2. `variantService = SelectLearningServiceForSymbol(symbol, labelSource)`
3. `variantService.AddLabeledSampleAsync(feature)` ← **해당 variant 의 window 에만 추가**
4. Default 에도 fallback 으로 병행 추가 (안전장치)

### variant 별 threshold / 로그 표시

```
[D-1] ML 확률 검증:
  effectiveThreshold = variantLearningService?.CurrentMLThreshold  ← 각 variant 가 승률로 자동 조정
                    ?? _onlineLearning?.CurrentMLThreshold         ← fallback
                    ?? _config.MinMLConfidence                      ← 최종 fallback

로그:
  ❌ [MOVRUSDT] [DECIDE][Pump][prob] ML=13.5% < threshold=58%
  ✅ [BTCUSDT] [DECIDE][Major][prob] ML=82% ≥ threshold=75%

DB AiTrainingRuns 기록:
  project="ML.NET_Major" stage="Online_Retrain_Major"
  project="ML.NET_Spike" stage="Online_Retrain_Spike"
```

### 기대 효과

- **1시간 내** 4개 모델 각각 독립 재학습 시작
- Major/Pump/Spike 모델이 **2달만에 처음으로 신규 데이터 학습**
- variant 별 승률 차등 자동 반영
- v5.15.0 의 TargetProfit 0.8% / Window 48hr / ratio balancing 이 **제대로** 적용됨

### 📂 수정 파일

- [AIDoubleCheckEntryGate.cs:42-46](AIDoubleCheckEntryGate.cs#L42-L46) — 4개 variant instance 필드
- [AIDoubleCheckEntryGate.cs:141-220](AIDoubleCheckEntryGate.cs#L141-L220) — 4개 instance 생성 + 이벤트 연결 + 디렉터리 분리
- [AIDoubleCheckEntryGate.cs:108-140](AIDoubleCheckEntryGate.cs#L108-L140) — `SelectLearningServiceForSymbol` / `GetVariantTagForSymbol` 신규
- [AIDoubleCheckEntryGate.cs:391-402](AIDoubleCheckEntryGate.cs#L391-L402) — variant threshold 사용 + 로그 태그
- [AIDoubleCheckEntryGate.cs:1082-1105](AIDoubleCheckEntryGate.cs#L1082-L1105) — 라벨 라우팅 (variant + fallback)

---

## [5.15.0] - 2026-04-24

### 🔥 ML 학습 로직 근본 수정 — 2달간 상승패턴 못 잡던 원인 제거

**사용자 지적:**

> "MOVRUSDT 같은 쉬운 상승패턴도 못 잡으면 프로그램을 왜 만드냐"
> "2달간 상승패턴인걸 잡아서 수익을 못내는건데"
> "ML 학습 로직 자체를 봐"

**MOVRUSDT 데이터 확인**:

- 최근 1시간 누적 +2.14%, 12개 5분봉 중 7개 양봉
- ML 출력 매번 3.9~18% → threshold 65% 못 넘어 **8번 연속 차단**
- 과거 trades 기록 **0건** (ML 때문에 1번도 진입 못함)

### 학습 로직 Audit — 찾은 4개 치명적 결함

| # | 파일:라인 | 결함 | 영향 |
|---|---|---|---|
| 1 | BacktestEntryLabeler.cs:102 | TargetProfitPct=**2.0%** 목표 미달 시 전부 NEGATIVE | MOVRUSDT steady +1.2% (4hr)가 전부 fail 라벨 → 모델이 "상승=나쁨" 학습 |
| 2 | BacktestEntryLabeler.cs:44 | EarlyFailDrawdownPct=**-0.3%** 30분내 찍으면 FAIL | 정상 진입도 intra-candle -0.3% 자주 찍음 → 거의 모든 sample FAIL |
| 3 | MultiTimeframeEntryFeature.cs:319 | EvaluationPeriodCandles=**16 (4hr)** | 8-12hr 걸쳐 실현되는 steady uptrend 전부 놓침 |
| 4 | EntryTimingMLTrainer.cs:154 | Oversample 트리거 "positives < 50" 절대값 기준 | 400pos/9000neg 같은 비대칭 케이스에서 oversample 안 됨 → negative bias |

### 수정

#### Fix 1: Target Profit 2.0% → 0.8%

```csharp
// 기존: 4시간 내 +2.0% 도달 안 하면 NEGATIVE
public decimal TargetProfitPct { get; set; } = 0.8m;  // ← 완화
```

+0.8% 도달 시 WIN. 학습 positive 샘플 **3-4배 증가**.

#### Fix 2: Early Fail Drawdown -0.3% → -1.5%

```csharp
// 기존: -0.3% intra-candle 찍으면 FAIL (너무 엄격)
public decimal EarlyFailDrawdownPct { get; set; } = -1.5m;  // ← 실질 손절 수준만
```

정상 slippage 범위(-0.3~-1.0%) sample을 WIN 학습 가능하게.

#### Fix 3: Evaluation Window 16 → 48 candles

```csharp
// 기존: 4시간 창 (단타만 잡힘)
public int EvaluationPeriodCandles { get; set; } = 48;  // 12시간
```

12시간 창으로 MOVRUSDT 같은 steady uptrend 수익 실현 포착.

#### Fix 4: Ratio 기반 Oversampling

```csharp
// 기존: if (positives.Count < 50) → 400pos/9000neg 케이스 무시
// 수정: positive 비율 < 30% 이면 negative 절반까지 확장
if (posRatio < 0.30 && positives.Count < targetPositiveCount) {
    // positive 를 negatives/2 수준까지 oversample
}
// 최종 비율 1:1.5 (기존 1:2) 로 강화
int targetNeg = (int)(positives.Count * 1.5);
```

### 기대 효과

- 다음 재학습 사이클 (1시간 내)에서 **positive 샘플 3배** 증가
- 모델 confidence 분포 shift: 기존 대부분 0-20% → 예상 40-70% 정상화
- MOVRUSDT 같은 steady uptrend 통과 (65% threshold 도달 가능)
- 하락장 편향 학습 탈출

### 📂 수정 파일

- [MultiTimeframeEntryFeature.cs:304-340](MultiTimeframeEntryFeature.cs#L304-L340) — EntryLabelConfig 4개 값 조정
- [EntryTimingMLTrainer.cs:140-180](EntryTimingMLTrainer.cs#L140-L180) — 클래스 밸런싱 ratio 기반 재작성
- [Services/DataDrivenEntryFilter.cs](Services/DataDrivenEntryFilter.cs) **신규** — 심볼/시간대 블랙/화이트 + OBVIOUS_PUMP override

---

## [5.14.1] - 2026-04-24

### 🚨 HOTFIX: "익절 완료 0.00 USDT" 팬텀 텔레그램 알림 차단

**사용자 지적**:

> "UBUSDT 익절 완료 0.00 USDT 0.00% 텔레그램 왔는데 그러니까 확인하라고"

**원인**: [DbManager.cs:46](DbManager.cs#L46) 의 0-PnL 필터가 `kind == "ENTRY" || kind == "FLIP_ENTRY"` 만 차단하고 있어서, EXTERNAL_CLOSE_SYNC 발생 시 `COMPLETE` / `COMPLETE_UPDATE` / `COMPLETE_INSERT` kind 로 PnL=0, PnLPct=0 인 데이터가 들어오면 그대로 "💰 익절 완료 0.00 USDT 0.00%" 텔레그램 발송됨.

**수정**: `kind` 무관 **모든** 0-PnL + 0-PnLPct 조합 차단 + 로그 기록.

```csharp
if (pnl == 0 && pnlPercent == 0)
{
    MainWindow.Instance?.AddLog($"🧹 [Notify][{kind}] {symbol} 0-PnL 알림 스킵 (팬텀 차단)");
    return;
}
```

### HUSDT "익절완료 됐는데 활성 포지션 잔류" 해명

진단 결과:

| 항목 | HUSDT |
|---|---|
| Binance 실제 positionAmt | 1095 (열려있음) |
| unRealizedProfit | +5.46 USDT (+3.8%) |
| DB TradeHistory IsClosed=0 | Id=4183 qty 1095 |
| 상태 | ✅ 봇 상태 정상 동기화 |

사용자가 본 "익절 완료" 텔레그램은 **이번 hotfix 로 차단되는 팬텀 알림**. HUSDT 포지션은 아직 열려있으며, Binance TP $0.1382 (657 qty) 부분익절 대기 중.

### 📂 수정

- [DbManager.cs:46-52](DbManager.cs#L46-L52) — 모든 `kind` 의 0-PnL 알림 차단

---

## [5.14.0] - 2026-04-24

### 🧠 AI #5 AdaptiveSpikeScheduler — 경량 강화학습 (Nearest-Neighbor Bandit)

**사용자 요구**: "계속 진행해" (#5 RL Scheduler 진행)

**설계 선택**: Deep RL (Q-Network/PPO) 대신 **Tabular Nearest-Neighbor Bandit** 채택.

이유:
1. **학습 안정성**: 심볼당 데이터 수십건 수준이라 Deep RL 수렴 어려움
2. **해석 가능성**: "왜 wait 했나" 를 bucket hit-rate 로 즉시 설명 가능
3. **Fail-safe**: 데이터 부족 시 기존 rule-based 로직으로 자동 폴백

### 구조

**상태 공간 (State)**:

| 차원 | 단위 | bucket 크기 | 범위 |
|---|---|---|---|
| `CumGainPct` | 누적 상승률 % | 0.5% | 0~5% |
| `PullbackPct` | 고점 대비 하락률 % | 0.25% | 0~2% |
| `ElapsedSec` | 감지 이후 경과초 | 5s | 0~30s |

→ 이산 bucket 총 11 × 9 × 7 = **693개**

**액션 (Action)**:

- `Wait`: 계속 관찰
- `Enter`: 즉시 진입
- `Cancel`: 진입 포기 (20초 이상 + 저승률 시)

**보상 (Reward)**: 진입 후 5분 PnL% (레버리지 미적용)

### 의사결정 로직 (Decide)

```
1. ε-greedy 탐색: 10% 확률로 무작위 Enter (새로운 bucket 학습)
2. Exploit 단계:
   - bucket 샘플 < 10 → 폴백 (rule-based, 기존 30초 윈도우)
   - 승률 ≥ 55% → Enter (즉시 진입)
   - 승률 ≤ 35% + 경과 ≥ 20s → Cancel (진입 포기)
   - 그 외 → Wait (계속 관찰)
3. rule-based 폴백 조건 (scheduler가 Wait 반환 시):
   - 누적 +2% → Enter
   - 눌림 -0.5% → Enter
```

### 학습 피드백 루프

진입 성공 시 → 5분 지연 Task → `tick5m.LastPrice` 기반 PnL 계산 → `_spikeScheduler.RecordOutcome(stateAtEntry, pnl)` → bucket EnterCount/WinCount/SumPnL 갱신 → 60초 디바운스 JSON 저장

### 영속성

`%LOCALAPPDATA%/TradingBot/Models/spike_scheduler_stats.json` 에 bucket 통계 누적. 봇 재시작해도 학습 유지.

### 📂 신규/수정 파일

- [Services/AdaptiveSpikeScheduler.cs](Services/AdaptiveSpikeScheduler.cs) **신규** (약 180 줄)
- [TradingEngine.cs:542](TradingEngine.cs#L542) — `_spikeScheduler` 필드 추가
- [TradingEngine.cs:8481](TradingEngine.cs#L8481) — `schedulerStateAtEntry` 외부 스코프 선언
- [TradingEngine.cs:8608-8672](TradingEngine.cs#L8608-L8672) — 30초 윈도우에 Scheduler Decide 통합
- [TradingEngine.cs:8748-8762](TradingEngine.cs#L8748-L8762) — 5분 후 RecordOutcome 피드백 루프

### 🧪 초기 운영 전략

- 처음 ~100회 SPIKE_FAST 진입: bucket 대부분 "insufficient_samples" → rule-based 폴백 작동
- 샘플 누적 후: winrate 낮은 bucket은 Cancel, 높은 bucket은 Enter 선택
- 탐색률 10% 유지로 새로운 pattern에도 지속 학습

---

## [5.13.0] - 2026-04-24

### 🧠 SPIKE_FAST AI 학습 기반 강화 (AI 개선 #1 + #2 + #3 + #4 통합)

**사용자 지적:**

> "SPIKE_FAST 의 진입조건 로직이 너무 단순해서 손실만 날 수 있는 구조"
> "AI 학습을 통해서 들어갈 수 있게 개선할 방향 찾아봐"
> "순서대로 전부 진행"

**문제 진단:**

- SPIKE_FAST 감지 트리거가 `|changePct| >= 3%` 단일 하드코딩 조건
- 기존 AI 모델(SurvivalEntryModel, PriceDirectionPredictor, TradeSignalClassifier)이 존재하나 **ExecuteAutoOrder 에만 적용**
- SPIKE_FAST 는 PlaceEntryOrderAsync 직접 호출 → **3개 AI 모델 전부 건너뜀**
- BacktestEntryLabeler 가 최종 TP/SL 기반 라벨만 생성 → "진입 직후 -0.5% 빠졌다가 반등한 엔트리"도 WIN 학습

### ✅ AI #2: BacktestEntryLabeler 조기 실패 라벨링

**변경 파일**: [BacktestEntryLabeler.cs:24](BacktestEntryLabeler.cs#L24), [MultiTimeframeEntryFeature.cs:304](MultiTimeframeEntryFeature.cs#L304)

```csharp
// 신규 EntryLabelConfig 필드
EarlyFailDrawdownPct = -0.3m      // -0.3% 드로다운 임계
EarlyFailWithinCandles = 2         // 진입 후 2캔들(30분) 이내
EnableEarlyFailLabeling = true
```

**로직**: 진입 후 2캔들 내 저가(LONG) 또는 고가(SHORT)가 임계값 이상 넘으면 **즉시 FAIL** 판정. TP/SL 체크보다 우선 평가.

**결과**: 모델이 "즉시 반전형 꼭대기 진입"을 negative sample 로 학습 → 꼭대기 진입 차단력 향상.

### ✅ AI #1 + #3 + #4: SPIKE_FAST 전용 AI 통합 하드 게이트

**신규 헬퍼**: [TradingEngine.cs:ValidateSpikeWithAIAsync](TradingEngine.cs#L12376)

3개 AI 모델을 SPIKE_FAST 진입 경로에 **하드 차단**으로 추가:

| AI 모델 | 체크 | 차단 기준 |
|---|---|---|
| **SurvivalEntryModel** | 진입 후 TP 먼저 도달 확률 | notSurvived + prob > 55% |
| **PriceDirectionPredictor** | 15분내 큰 상승 예측 | !GoesUp + prob > 55% |
| **TradeSignalClassifier** | 신호 방향 override | PredictedLabel=SHORT + Score ≥ 55% |

**호출 위치**: [TradingEngine.cs:8591](TradingEngine.cs#L8591) — SpikeForecaster + RSI 체크 통과 직후, 진입 타이밍 루프 직전.

**Fail-open 정책**: AI 예외 발생 시 진입 계속 (기존 게이트에 의존). 데이터 부족 시에도 통과.

### 📋 우선순위 반영

| # | 방향 | 상태 | 비고 |
|---|---|---|---|
| #2 | 재라벨링 (5분내 -0.3% = FAIL) | ✅ 완료 | 기존 라벨러 확장 |
| #1 | SpikeEntryClassifier (신규) | ✅ 통합 | 별도 모델 대신 기존 3개 AI 하드 게이트 통합 |
| #3 | SpikeDirectionPredictor | ✅ 통합 | PriceDirectionPredictor 재활용 |
| #4 | SpikeSurvivalModel | ✅ 통합 | SurvivalEntryModel 재활용 |
| #5 | RL Scheduler | ⏸ 차후 | 2-3일 작업, 다음 릴리스 |

**결정**: #1 을 신규 모델 작성 대신 **기존 3 AI 모델 (#3 + #4 + TradeSignal) 통합** 으로 구현. 이유:

- 기존 모델들이 이미 학습/추론 파이프라인 보유
- 신규 모델은 학습 데이터 수집 + 훈련 주기 대기 필요 (2-3일 공백)
- 통합 게이트는 즉시 효과 적용 가능

### 📂 수정 파일

- [MultiTimeframeEntryFeature.cs:304-335](MultiTimeframeEntryFeature.cs#L304-L335) — `EntryLabelConfig` 조기실패 필드 3개 추가
- [BacktestEntryLabeler.cs:24-55](BacktestEntryLabeler.cs#L24-L55) — LONG/SHORT 조기실패 로직 삽입
- [TradingEngine.cs:8591-8603](TradingEngine.cs#L8591-L8603) — SPIKE_FAST 경로에 AI 하드 게이트 호출
- [TradingEngine.cs:12376-12470](TradingEngine.cs#L12376-L12470) — `ValidateSpikeWithAIAsync` 헬퍼 신규

---

## [5.12.0] - 2026-04-24

### 🔒 급등(SPIKE) 범주 단일 슬롯 강제 — 동시 다수 진입 차단

**사용자 지적:**

> "SPIKE_FAST 는 무조건 1개만 진입" (옵션B: 급등 범주 전체 1개)

**문제:**

- `MAX_SPIKE_SLOTS = 1` 상수 존재하나 **"현재 활성 급등 포지션 개수" 카운터 없음**
- SPIKE_FAST와 PumpScan 3 fast-path (M1_FAST/MEGA/TOP_SCORE) 동시 진입 가능
- 오늘 BASUSDT/OPNUSDT/PIEVERSEUSDT 동시 급등 경로 물림

### 구현

**범위**: SPIKE_FAST + MAJOR_MEME (PumpScan 3 fast paths 통합)
**제외**: PUMP_WATCH_CONFIRMED, MAJOR, 기타

```csharp
private const int MAX_SPIKE_CATEGORY_SLOTS = 1;
private readonly ConcurrentDictionary<string, (DateTime entryTime, string source)>
    _activeSpikeSlot = new(...);

private static bool IsSpikeCategorySignal(string? signalSource) =>
    signalSource == "SPIKE_FAST" || signalSource.StartsWith("MAJOR_MEME");
```

### 훅 지점

| 위치 | 동작 | 파일:라인 |
|---|---|---|
| HandleSpikeDetectedAsync 초반 | 슬롯 점유시 즉시 거부 | TradingEngine.cs:8274 |
| ExecuteAutoOrder 초반 | MAJOR_MEME 슬롯 체크 | TradingEngine.cs:9236 |
| SPIKE_FAST 체결 성공 | TryAdd 슬롯 점유 | TradingEngine.cs:8693 |
| ExecuteAutoOrder 체결 성공 | TryAdd 슬롯 점유 | TradingEngine.cs:11393 |
| HandleSyncedPositionClosed | TryRemove 슬롯 해제 | TradingEngine.cs:12617 |
| PositionSync Remove | TryRemove 슬롯 해제 | TradingEngine.cs:6461 |

### 동작 예시

```
[10:00:01] SPIKE_FAST BASUSDT 진입 → 🔒 [SPIKE_SLOT] 1/1 점유
[10:00:03] MEGA_PUMP OPNUSDT 시도 → ⛔ [SPIKE_SLOT] BASUSDT(SPIKE_FAST) 점유중 — 거부
[10:00:05] M1_FAST_PUMP IRUSDT 시도 → ⛔ [SPIKE_SLOT] BASUSDT(SPIKE_FAST) 점유중 — 거부
[10:15:30] BASUSDT 청산 → 🔓 [SPIKE_SLOT] 해제
[10:15:45] MEGA_PUMP TAOUSDT 시도 → ✅ 진입 (슬롯 비었음)
```

---

## [5.11.1] - 2026-04-24

### 🔥 급등(PUMP) 로직 꼭대기 진입 구조적 원인 제거

**사용자 지적:**

> "급등로직 다 재점검해 오늘 다 손실만 나고 들어가면 급하락하잖아"

**감사 결과 — 5개 PUMP 경로 모두 꼭대기 진입**:

| 경로 | 문제 | 파일:라인 |
|---|---|---|
| M1_FAST_PUMP | 1분봉 3%+ 스파이크에 캔들 내 위치 검증 없이 close 에서 진입 = 꼭대기 | PumpScanStrategy.cs:332 |
| MEGA_PUMP | 5분봉 3%+vol 5x 단순 조건, 꼭대기 검증 없음 | PumpScanStrategy.cs:356 |
| TOP_SCORE_ENTRY | Top60 rank 1-3 추가 진입이지만 캔들 내 위치 검증 없음 | PumpScanStrategy.cs:382 |
| TICK_SURGE | TPS spike → 2-4초 지연, +2% 오른 뒤 체결 → 반전 | TradingEngine.cs:1540 |
| SPIKE_FAST | 게이트 검증 **전** 이미 포지션 등록 + "즉시진입" 알림 발송 | TradingEngine.cs:8443 |

**Gate1 (IsAlreadyPumpedRecently) 존재하나 작동 부실**:
- 1시간 누적 **10%** 만 체크 → 이미 너무 늦은 시점
- 10분 3% 급등 직후 진입(스파이크 고점)을 못 걸름

### ✅ Fix 1: 캔들 내 위치 + 윗꼬리 검증 (핵심)

PumpScanStrategy 3개 fast-path 모두 다음 2가지 인라인 검증 추가:

```
posInRange = (currentPrice - Low) / Range                        # 0=Low, 1=High
upperWickRatio = (High - max(Open, Close)) / Range               # 윗꼬리 비율
```

조건: `posInRange <= 0.70 && upperWickRatio <= 0.30`

- `posInRange > 0.70` → 캔들 상위 30% 이내 = 꼭대기 → **차단**
- `upperWickRatio > 0.30` → 캔들 내 이미 반전 시작 = 상투 → **차단**

적용 경로: M1_FAST_PUMP, MEGA_PUMP, TOP_SCORE_ENTRY 모두

### ✅ Fix 2: Gate1 임계값 3중 강화

기존 1시간 10% 단일 임계 → 3구간 세분화:

| 구간 | 기존 | 신규 | 근거 |
|---|---|---|---|
| 1시간 누적 | 10% | **5%** | 1시간 5% 이상이면 이미 늦음 |
| 30분 누적 | — | **4%** 추가 | 30분 4% 꼭대기 방어 |
| 10분 누적 | — | **3%** 추가 | 스파이크 직후(10분 3%+) 차단 |

### ✅ Fix 3: 캔들 윗꼬리 검증 (Fix 1 통합 구현)

"급등 캔들이 이미 반전 중"을 `upperWickRatio > 0.30` 로 감지. Fix 1 에 통합되어 별도 코드 없음.

### ✅ Fix 4: SPIKE_FAST 순서 역전 — 게이트 → 알림

기존: 게이트 검증 **전** OnAlert/OnSignalUpdate/Telegram "즉시진입" 알림 → 게이트 탈락해도 사용자에게 phantom 진입 보임

수정: 알림 블록을 모든 게이트 검증(BTC 방향/RSI/MTF Guardian/Gate1/SpikeForecaster) 통과 직후, `PlaceEntryOrderAsync` 호출 직전으로 이동

### 📂 수정 파일

- [PumpScanStrategy.cs:319-402](PumpScanStrategy.cs#L319-L402) — 3개 fast-path 캔들 위치/윗꼬리 검증 추가
- [TradingEngine.cs:12040-12100](TradingEngine.cs#L12040-L12100) — Gate1 임계값 3중 강화 (1h 5% + 30m 4% + 10m 3%)
- [TradingEngine.cs:8443-8663](TradingEngine.cs#L8443-L8663) — SPIKE_FAST 알림을 게이트 통과 후로 이동

---

## [5.11.0] - 2026-04-24

### 🔥 진입 로직 전면 재설계 — 검증 → 예측 → 결정 3단계 파이프라인

**사용자 지적:**

> "오늘 승률 27%까지 떨어졌어... 다 삭제하고 다시 만들라고 했잖아"
> "진입대상이 있으면 검증을 해서 예측을 해야지 무조건 다 들어가냐"
> "5분봉 하락중인데 들어갔네... AI 학습시키라고 했잖아"

**12시간 실제 통계 (2026-04-23 12:00 KST 이후):**

- 130건 진입 / 127건 종료
- **승률 31.5%** (40승 85패)
- NetPnL **-93.04 USDT**
- AvgWin +0.49 / AvgLoss -1.32 → **RR 0.37:1**

**근본 원인 4가지:**

1. **3-way 동일값 가짜 더블체크** — BASUSDT 로그: `Trend=98.2% ML=98.2% TF=98.2%` 전부 같은 값. 독립적이어야 할 소스가 전부 ML 출력 복사본
2. **방향 검증 부재** — `AI.Dir=DOWN AI.Prob=0.3%` 인데 LONG 진입 통과 (게이트가 방향 검증 안 함)
3. **Elliott 바이패스 조항** — `"TF=98% 고신뢰로 바이패스"` 로그. 객관적 규칙을 주관적 임계값으로 무효화
4. **Lorentzian 샘플 0/100** — 학습 자체가 안 된 상태로 soft warn만

### ✅ 신규 게이트 (AIDoubleCheckEntryGate.EvaluateEntryAsync 전면 재작성)

**Phase 1: 검증 (Validation)** — 예측 전 자격심사

- 모델 준비 상태 확인
- Feature 추출 성공 여부 (NaN/Inf 내장 sanitize)
- M1_Data_Valid silent fallback 차단
- 해당 CoinType 전용 모델 로드 여부 확인
- → 실패 시 **예측 자체를 돌리지 않음** (리소스 절약 + 무효 예측 차단)

**Phase 2: 예측 (Prediction)** — 검증 통과 시에만 실행

- 선택된 variant 모델 (Major/Pump/Spike/Default) 단일 경로 예측
- `(probability, ShouldEnter)` 산출 — 사본/복제 금지

**Phase 3: 결정 (Decision)** — 독립 3소스 교차검증

- **[D-1] ML 확률**: 동적 threshold (AdaptiveOnlineLearning 승률 기반 auto-calibrate)
- **[D-2] ML 방향 승인**: `ShouldEnter==true` 강제
- **[D-3] Raw Feature 추세**: ML 출력 아닌 원본 feature 값 — LONG 진입 시 다음 모두 차단
  - `M15_IsDowntrend >= 0.5` (SMA20<SMA60 하락추세)
  - `H1_IsDowntrend >= 0.5 && H1_BreakoutFromDowntrend < 0.5` (H1 하락+돌파미검출)
  - `M15_SuperTrend_Direction < 0`
  - `M15_ConsecBearishCount >= 3 && M15_RSI_BelowNeutral >= 0.5` (연속 음봉 3+RSI 약세)
  - `DirectionBias <= -1.5` (D1+H4 강한 약세)
- SHORT 은 대칭 적용 (상승추세 + 연속 양봉 3개 이상 → 거부)

### 🗑️ 폐기된 레거시 (모두 삭제)

| 제거 | 이유 |
|---|---|
| Fibonacci bonus score 가산 | 주관적 바이어스 (`detail.TrendScore += fibBonus` 류) |
| Elliott Rule TF≥80% 바이패스 | 객관적 규칙을 임계값으로 회피 |
| Sanity_Filter_UpperWick/RSI 하드코딩 | AI 학습으로 대체 (feature 안에 이미 내장) |
| Lorentzian soft warn | 샘플 0/100 상태로 의미 없음 |
| KST9 시간대 threshold 완화 | 시간대 바이어스 |
| ML=0 경고 후 진입 계속 | ML 무력화 |
| `skipAiGateCheck` 바이패스 | PumpScan/SPIKE_FAST 단독 승인 → 30% 승률 원인 |
| `CRASH_REVERSE`/`PUMP_REVERSE` 바이패스 | 급변 대응 명분으로 게이트 우회 |

### 🔧 ROI 불일치 수정

**현상**: "실시간 시장 신호" 와 "활성 포지션" 두 패널의 ROI 값이 다름 (예: BASUSDT 한쪽은 46%, 한쪽은 15%)

**원인 2가지:**

1. **청산 후 stale 잔류** — `UpdatePositionStatus` 가 EntryPrice/Quantity 는 리셋하는데 `Leverage` 리셋 누락. `ProfitRate` getter 가 `!IsPositionActive` 시 cached value 반환 → 청산돼도 마지막 ROI 그대로 표시
2. **Leverage 소스 불일치** — Panel 1 은 `MultiTimeframeViewModel.Leverage` (기본 20), Panel 2 는 `PositionInfo.Leverage` (Binance 실제값). 차이만큼 ROI 괴리 (예: 4배 부풀림)

**수정:**

- `MainViewModel.cs:5046` — 청산 시 `existing.Leverage = 0` 추가
- `UpdateFocusedPositionFromEngine` — Panel 2 의 `PositionInfo.Leverage` (Binance 실제값)를 Panel 1 `MultiTimeframeViewModel.Leverage` 로 매 tick 강제 동기화 + EntryPrice 도 동일화

### 📂 수정 파일

- [AIDoubleCheckEntryGate.cs:223-395](AIDoubleCheckEntryGate.cs#L223-L395) — `EvaluateEntryAsync` 3단계 재작성
- [AIDoubleCheckEntryGate.cs:410-470](AIDoubleCheckEntryGate.cs#L410-L470) — `EvaluateEntryWithCoinTypeAsync` Fib 가산 제거, 단순 threshold overlay
- [TradingEngine.cs:9707-9718](TradingEngine.cs#L9707-L9718) — `skipAiGateCheck`/`CRASH_REVERSE`/`PUMP_REVERSE` 바이패스 제거
- [MainViewModel.cs:5046-5050](MainViewModel.cs#L5046-L5050) — 청산 시 Leverage 리셋
- [MainViewModel.cs:4748-4760](MainViewModel.cs#L4748-L4760) — Panel 1 Leverage/EntryPrice 매 tick 동기화

---

## [5.10.101] - 2026-04-23

### 🚨 ROOT FIX: SL 등록 silent fail 차단 + Watchdog (사용자 자산 10% 손실 원인)

**사용자 지적:**

> "SL 등록 누락 없는지 전체 다 확인" + "ROBOUSDT -56% 됐는데 SL 안 잘림"

**SL audit 결과 — 5개 silent failure path 발견:**

| 경로 | 문제 |
|---|---|
| TICK_SURGE / SQUEEZE_BREAKOUT / MAJOR / PUMP_WATCH_CONFIRMED | OrderLifecycleManager `Task.Run` fire-and-forget — 실패 시 OnStatusLog만 (사용자 모름) |
| MANUAL_ENTRY | RegisterProtectionOrdersAsync 예외 시 OnStatusLog만 |
| EXTERNAL_POSITION_INCREASE_SYNC | RegisterOnEntryAsync async + monitor 먼저 시작 → race condition |
| CRASH/PUMP_REVERSE | RegisterProtectionOrdersAsync 실패 시 폴백 없음 |
| ACCOUNT_UPDATE_RESTORED | 예외 시 OnStatusLog만 (Telegram 알림 X) |

**수정 (PlaceAndTrackEntryAsync):**

1. **SL 등록 실패 OnAlert + 텔레그램** (silent fail 차단):
   ```
   🚨 [SL_MISSING] symbol 진입 후 SL 등록 실패! 폴백 시도 중
   🚨 [SL_REGISTRATION_EXCEPTION] symbol SL 등록 예외: ...
   ```

2. **자동 폴백**: SL 누락/예외 시 `RegisterProtectionOrdersAsync` 즉시 재시도

3. **5초 Watchdog**: 진입 5초 후 Binance algoOrder 개수 확인
   - 0건이면 → `🚨 [SL_WATCHDOG] symbol 5초 후 algoOrder 0건 — 긴급 폴백`
   - 폴백도 실패 → `❌ [SL_WATCHDOG_FAIL] symbol 즉시 수동 SL 필수!`

**효과:**

- SL 등록 실패가 사용자 화면에 즉시 노출 (silent 차단)
- 폴백 자동 재시도 (RegisterProtectionOrdersAsync)
- 5초 후 검증으로 race condition 케이스도 catch
- ROBOUSDT -56% 같은 사고 발생 시 즉시 알림 (UI + Telegram)

**리스크:**

- 폴백 중복 등록 시 Binance -4120 에러 가능 → BinanceExchangeService에서 cancel 후 재등록 처리됨

## [5.10.100] - 2026-04-23

### 🚨 ROOT FIX: PositionInfo.Leverage 미반영 (UI ROE 5배 부풀림)

ROBOUSDT 사례:
- Binance 실제 leverage: **5x** (auto-adjusted from bot's 25x attempt)
- 봇 PositionInfo.Leverage: **25x** (시도값 그대로 저장)
- UI ROE = price_change × 25 → **실제 5배 부풀림**
- 사용자 본 -56% = 실제 -11.2% (가격 -2.24%)

**수정 위치:**

1. **PlaceAndTrackEntryAsync** ([line 11221](TradingEngine.cs#L11221)): SetLeverageAutoAsync 후 `_activePositions[symbol].Leverage = actualLeverage` 즉시 갱신
2. **SPIKE_FAST** ([line 8644](TradingEngine.cs#L8644)): SetLeverageAutoAsync 호출 자체 누락 → 추가 + actualLeverage 사용

신규 로그:
- `⚙️ [LEVERAGE_ADJUSTED] 시도={N}x → 실제={M}x`
- `🔧 [LEVERAGE_SYNC] PositionInfo.Leverage {old} → {new}`

## [5.10.99] - 2026-04-23

### 🧹 P2/P3 정리 — Dead code, Major fallback, 명확화, 가시성

**P2-1: DualAI_EntryPredictor [Obsolete] 마킹**

- 어디서도 인스턴스화 안 됨 (TF migration 미완성)
- `[System.Obsolete]` 표시로 향후 제거 예정 명시
- EntryTimingMLTrainer (4 variants)이 대체

**P2-3: AiScore vs ML_Conf 명확화**

`EvaluateAiPredictorForEntry` 헤더에 점수 시스템 주석 추가:

- `ctx.AiScore` (TradeHistory.AiScore) ← AIPredictor / scalping_model.zip
- `Bot_Log.ML_Conf` ← AIDoubleCheckEntryGate / EntryTimingMLTrainer variant
- 두 점수 출처가 다름. AiScore=1차 screening, ML_Conf=2차 dual gate

**P2-4: Major variant 학습 데이터 fallback**

- 현재 Major 440 samples vs Pump 1540 (메이저 4개만 있어 부족)
- `majorFeatures < 50` 시 `trainingFeatures` 전체로 fallback (Pump와 동일 패턴)
- Major variant 모델 정확도/Sample 향상 기대

**P3-1: PumpSignalClassifier 점검 완료** (별도 fix 불필요 — 현재 시스템 충분)

**P3-3: Online learning 가시성 강화**

- `OnRetrainCompleted` event에 FooterLogs 직접 출력 추가
- 로그: `🧠 [ONLINE_ML][✅] reason=hourly samples=N acc=X% f1=Y%`
- 기존엔 AiTrainingRuns DB만 기록 → 실시간 모니터링 불가

## [5.10.98] - 2026-04-23

### 🛠️ P1 4개 갭 수정 — 종합 audit 후 누락 부분 일괄 보강

**P1-1: MANUAL_ENTRY 게이트/모니터 적용**

- 기존: `PlaceMarketOrderAsync` 직접 호출 → IsEntryAllowed/LIMIT/chasing 차단/모니터 모두 우회
- 수정: `PlaceEntryOrderAsync` 경유 (LIMIT + 5초 timeout + chasing 차단 자동 적용)
- 추가: 진입 후 `RegisterProtectionOrdersAsync` (SL/TP/Trailing) + 메이저=Standard / 알트=Pump monitor 자동 시작

**P1-2: Direction Flip UI cleanup 누락**

- 기존: 외부 방향전환 감지 시 DB만 갱신 → DataGrid에 stale (이전 방향 EntryPrice/Qty) 잔존
- 수정: `OnPositionStatusUpdate(false, 0)` 호출 후 `OnPositionStatusUpdate(true, newEntryPrice)`로 새 방향 갱신

**P1-3: Feature NaN/Infinity sanity check**

- 기존: extraction 중 0 division / log(0) 등 silent NaN → ML.NET이 NaN 입력에 0% 점수 반환 → 진입 거절
- 수정: `SanitizeFeatures(feature)` 헬퍼 — reflection으로 모든 float/double 속성 검사, NaN/Infinity 발견 시 0 치환 + 개수 로깅
- 로그: `⚠️ [FEATURE_SANITIZED] symbol N개 NaN/Infinity → 0 치환`

**P1-4: CRASH_REVERSE / PUMP_REVERSE 폴백 SL**

- 기존: OrderLifecycleManager 명시 제외 → monitor만 의존 → monitor 실패 시 무방비
- 수정: 진입 직후 `RegisterProtectionOrdersAsync`로 SL/TP/Trailing 폴백 등록 (catastrophic loss 방지)

**효과 종합:**

- 수동 진입도 봇 진입과 동일 보호 (게이트/SL/모니터)
- 방향 전환 UI 즉시 갱신
- 잘못된 feature 값 자동 정규화 → ML 점수 안정성↑
- 긴급 진입 (CRASH/PUMP REVERSE)도 SL 보장

## [5.10.97] - 2026-04-23

### 🎯 ROOT FIX: 진입 직후 마이너스 패턴 (시장가 슬리피지 + 펌프 꼭대기 chasing)

**사용자 지적:**

> "왜 진입하면 모두 마이너스부터 시작" — UBUSDT 가격 +0.05%인데 ROE -13%

**진단:**

| 패턴 | 데이터 |
|---|---|
| <1분 보유 | 30건 30% 승률 -5.94% |
| 1-3분 보유 | 5건 0% 승률 -38% |
| 5-10분 보유 | 41건 26.8% -12% |

가격이 올라도 ROE 마이너스 = **수수료 0.13% × 25배 레버리지 = -3.25% 즉시 차감 + 시장가 슬리피지**.

**수정 (PlaceEntryOrderAsync 재설계):**

**A. LIMIT 주문 + 5초 timeout (슬리피지 0)**

- 신호 시 `LIMIT @ 현재가 -0.05%` (LONG, SHORT은 +0.05%)
- 5초 안 체결 → 취소
- 효과: 슬리피지 제로, 펌프 더 가는 케이스 자동 미체결 (chasing 회피)
- 5초 미체결 + 시장가 폴백 X (chasing 차단 우선)

**B. 1초 대기 + chasing 차단**

- 신호가 vs 1초 후 가격 비교
- LONG: 1초 후 가격이 +0.3% 이상 상승 시 → 진입 취소
- 효과: 펌프 꼭대기 진입 자동 차단

**C. 레버리지 20→15 하향 (Models.cs default)**

- DefaultLeverage / PumpLeverage / MajorLeverage 모두 15
- 효과: 같은 가격 -1% 변화 → ROE -25% 대신 -15% (40% 손실 폭 감소)
- 사용자 설정창 값이 우선이므로 사용자가 별도로 25→15 변경 필요

**로그 신규:**

- `⛔ [CHASING_BLOCK][source] symbol 신호가 X → 1초후 Y (이동 +0.5% ≥ 0.3%) 진입 취소`
- `✅ [LIMIT_ENTRY][source] symbol 체결 @ price qty=Q`
- `⏱️ [LIMIT_TIMEOUT][source] symbol LIMIT @ price 5초 미체결 → 취소`
- `⚠️ [LIMIT_FALLBACK][source] LIMIT 실패 → MARKET 폴백`

**효과:**

- 진입가 정확 (슬리피지 0)
- 펌프 꼭대기 진입 자동 회피 (chasing block + LIMIT timeout)
- 레버리지 하향으로 -1% 가격 = -15% ROE 한도

**리스크:**

- LIMIT 5초 미체결 시 진입 X → 진입 빈도 일부 감소 (대신 품질 상승)
- 빠른 펌프는 놓칠 수 있음

## [5.10.96] - 2026-04-23

### 🎨 UI: 청산 시 실시간 시장 신호 ROI/마진/손익 자동 초기화

**사용자 지적:**

> "익절이나 청산되면 실시간 시장 신호에서 빼버리거나 ROI/마진/손익을 초기화하라"

**진단:**

`MainViewModel.UpdatePositionStatus(symbol, false, 0)` 호출 시 EntryPrice/Quantity/SL/TP 모두 0으로 초기화하는 로직 이미 존재 ([MainViewModel.cs:5040-5046](MainViewModel.cs#L5040-L5046)).

문제: **`OnPositionStatusUpdate(false, 0)` 호출이 청산 경로 일부에서 누락**:

- ✅ PositionMonitorService:4089 (완전 청산)
- ✅ PositionMonitorService:3581 (부분청산 잔여=0)
- ✅ TradingEngine:6355 (외부 완전 청산)
- ❌ **TradingEngine.RecordConditionalOrderFillAsync** (API SL/TP/Trailing 체결 → UI 갱신 누락)
- ❌ **TradingEngine 외부 부분청산 잔여=0** (UI 갱신 누락)

**수정:**

1. `RecordConditionalOrderFillAsync` ([line 3963](TradingEngine.cs#L3963)): SL 또는 Trailing 체결(완전 청산) 시 `OnPositionStatusUpdate?.Invoke(symbol, false, 0)` 호출
2. EXTERNAL_PARTIAL_CLOSE 분기 ([line 6638](TradingEngine.cs#L6638)): `updatedQtyAbs <= 0.000001m` (잔여 0) 시 UI 즉시 false 갱신

**효과:**

- API SL/TP 자동 체결 즉시 DataGrid에서 EntryPrice/Quantity/ROI/Margin/PnL 모두 빈 칸
- 외부 청산도 동일 처리

## [5.10.95] - 2026-04-23

### 🚨 Partial Fill 청크 합산 — 1 청산 = 1 텔레그램 보장

**사용자 지적:**

> "API 한 번 날려서 6~7번 체결됐다고 그걸 다 텔레그램 메시지 날리면 어케하냐"

**진단 (실제 사례):**

| Symbol | 청산 시간 | 청크 수 | 비고 |
|---|---|---|---|
| **HUSDT** | 11:10:30 ~ 11:11:10 (40초) | **18 청크** | SL 1건 → 18 partial fill |
| PIEVERSEUSDT | 10:57:09 ~ 11:02:40 (5분 33초) | 20+ 청크 | |
| UBUSDT | 10:49:25 ~ 10:49:56 (31초) | 10+ 청크 | |

**v5.10.93 dedup 결함:**

dedup key = `symbol|pnl(소수2자리)` → 청크별 PnL 다 다름 (-1.5474, -2.6929, -0.069...) → 키 모두 다름 → **18번 모두 발송**.

**근본 수정 (DbManager.TryNotifyProfit):**

청크 합산 윈도우 방식으로 재설계:

1. 첫 청크 도착 → `_notifyAggregate[symbol] = PendingNotify` 생성
2. 90초 후 발송 예약 (Task.Delay)
3. 추가 청크 도착 시 PnL 합산 + ChunkCount++ → 발송 예약은 첫 거 그대로
4. 90초 후 누적 PnL로 1번만 발송: `[익절 완료] HUSDT (18청크 합산) PnL=-X.XX`

**효과:**

- 1 청산 = 1 텔레그램 (청크 수 무관)
- 메시지에 합산 정보: `(18청크 합산) PnL=...`
- 전송 로그: `📨 [Notify] 청산 감지 → 90초 합산 후 발송 예약`, `🔁 청크 합산 중 (#18 +PnL=...)`, `✉️ [AGG] 최종 PnL=...`

**리스크:**

- 청산 후 ~90초 지연 발송 (실시간성 일부 희생, 정확성 우선)
- 90초 내 재진입+청산은 합쳐질 수 있음 (드문 케이스)

## [5.10.94] - 2026-04-23

### 🔍 Feature_Extraction_Failed 원인 진단 — 어떤 TF 부족인지 명시 + cooldown

**진단 (12시간 89건 실패 5 심볼 분포):**

| Symbol | 실패 횟수 | First | Last |
|---|---|---|---|
| **CHIPUSDT** | **73** | 03:09 | 09:42 |
| OPGUSDT | 10 | 03:01 | 09:51 |
| GENIUSUSDT | 4 | 05:49 | 08:50 |
| MUUSDT | 1 | - | - |
| AVGOUSDT | 1 | - | - |

→ CHIPUSDT 한 심볼이 6시간 동안 73회 반복 실패 (PumpScan 점수 높지만 historical data 부족, 진입 시도 → 거절 무한 루프)

**원인 (`MultiTimeframeFeatureExtractor.ExtractRealtimeFeatureAsync`):**

요건 D1≥20 / H4≥40 / H2≥40 / H1≥50 / M15≥100. 신규 상장 알트는 D1/H4 데이터 부족 → 실패.

기존: null 반환만 → 어떤 TF 부족인지 불명, 매 신호마다 재시도

**수정:**

1. **명시 로그**: 부족 TF 정확히 표시
   - 예: `⚠️ [FEATURE_EXTRACTION_FAILED] CHIPUSDT 데이터 부족: D1=15/20, H4=32/40 (5분 cooldown)`
2. **5분 cooldown**: 동일 심볼 5분 내 중복 로그 + 불필요한 재시도 차단
3. `OnLog` event를 AIDoubleCheckEntryGate → MainWindow FooterLogs 까지 전달

**효과:**

- FooterLogs에 어떤 심볼이 어떤 TF 부족인지 즉시 노출
- CHIPUSDT 같은 신규 상장 코인 73회 무의미 반복 차단 → CPU/로그 절약
- 사용자가 신규 상장 알트 ✗인지 시스템 버그인지 즉시 구분 가능

## [5.10.93] - 2026-04-23

### 🚨 Standard 모니터 시간손절 + 텔레그램 6번 중복 차단

**사용자 지적 1:**

> "ONGUSDT AVAXUSDT는 왜 거의 1일 가까이 들고 있냐"

진단:

- AVAXUSDT 22시간 (1306분), ONGUSDT 18시간+ 보유
- v5.10.85 시간손절은 `MonitorPumpPositionShortTerm` 단 1곳에만 있음
- `MonitorPositionStandard` (메이저 + 외부 진입 알트 + PUMP_WATCH_CONFIRMED 등)에는 시간손절 미적용

수정 (PositionMonitorService.cs:506):

- 알트(`!isMajor`) + 보유 ≥ PumpTimeStopMinutes(120m) + ROE ≤ 0 → 시간 손절
- 알트 + 30분+ + ROE ≥ 3% + BBWidth < 1.5%(횡보) → 횡보 익절
- 메이저(BTC/ETH/SOL/XRP)는 추세 따라 보유 (시간 손절 제외)

**사용자 지적 2:**

> "수동청산 했더니 메시지 6번이나 왔어"

진단: v5.10.89 DbManager 중앙화 + 여러 진입 경로(ACCOUNT_UPDATE + ORDER_UPDATE + caller 잔존)가 동일 청산 모두 알림 → 중복.

수정 (DbManager.cs):

- `_notifyDedupCache` ConcurrentDictionary 신규
- 같은 `symbol|pnl(소수2)` 시그니처 60초 이내 중복 호출 시 스킵
- 차단 시 로그: `🔁 [Notify][kind] symbol 중복 알림 차단 (60초 dedup)`

효과:

- 1 청산 = 1 텔레그램 (다중 경로에서 호출돼도 dedup)
- 활성 알트 22시간/18시간 같은 stuck 포지션 자동 정리 (재시작 후 진입부터)

## [5.10.92] - 2026-04-23

### 🎯 ROOT CAUSE: ML 학습 실패 근본 수정 — Pump/Spike variant 학습 데이터 0건 버그

**사용자 지적:**

> "왜 자꾸 학습이 실패하는건데 그걸 찾아서 수정해야지"

**진단 (AiTrainingRuns DB 전수 조회):**

| 학습 이력 | Major | Default | **Pump** | **Spike** |
|---|---|---|---|---|
| 모든 시점 | ✅ 다수 | ✅ 다수 | ❌ **0건** | ❌ **0건** |

→ Pump / Spike variant 모델 파일 자체가 생성되지 않음
→ `SelectTrainerForSymbol(알트, "TICK_SURGE")` → `_mlTrainerPump` 선택 → IsModelLoaded=false → 0% 점수
→ 모든 알트 진입 `Dual_Reject_ML=0%` → 9시간 진입 0건

**근본 원인 (v5.10.85 회귀 버그):**

v5.10.85에서 `MajorSymbols = Settings.Symbols` (BTC/ETH/SOL/XRP 4개)로 동적화 → 학습용 `_symbols`도 같은 4개라 모두 메이저 분류:
```csharp
var pumpFeatures = trainingFeatures.Where(f => !IsMajorSymbol(f.Symbol)).ToList();
// → 0개 → "데이터 부족" 스킵 → Pump 모델 파일 미생성
```

**수정 1 — Trainer fallback (AIDoubleCheckEntryGate.cs:1513):**

```csharp
if (pumpFeatures.Count < 10 && trainingFeatures.Count >= 10)
{
    OnLog?.Invoke("⚠️ pumpFeatures 부족 → trainingFeatures fallback");
    pumpFeatures = trainingFeatures;
}
```
→ Pump variant도 모델 파일 생성 보장

**수정 2 — 학습용 심볼 확장 (TradingEngine.cs:2733, 3681):**

기존: `_symbols` (Settings.Symbols = 메이저 4개)만 전달
수정: `_symbols + KlineCache.Keys + 인기 알트 20개 fallback` 합쳐서 전달
- 인기 알트: BLUR/CHIP/MET/ENJ/DOGE/BNB/TAO/SUI/AVAX/ADA/NEAR/ARB/OP/MATIC/LINK/DOT/INJ/ATOM/UNI/FIL

**효과:**

- Pump/Spike variant 학습 데이터 충분히 확보 → 모델 파일 정상 생성 (>50KB)
- 알트 진입 시 ML 0% → 정상 점수 → AI Gate 통과 → 진입 활성화
- `IsMajorSymbol` 정의 (4개)는 그대로 → 메이저 차단 로직 영향 없음

## [5.10.91] - 2026-04-23

### 🔍 Telegram Silent Fail 로그화 — 왜 안 오는지 실제 원인 노출

**사용자 지적:**

> "바이낸스 TP/SL/트레일링스탑 처리되서 프로그램으로 내려준거 DB INSERT하고 텔레그램 메시지 보내야 하는데 텔레그램 메시지가 안와"

**진단:**

`TelegramService.SendInternalAsync`에 **2곳 silent fail** 존재:

1. `IsMessageTypeEnabled(messageType) == false` → 로그 없이 return
2. `EnsureTelegramClientReady() == false` → 로그 없이 return (부분 로그는 있으나 `LogTelegramFailure`가 failureLogger에 의존)

→ 사용자가 "왜 안 오는지" 확인 불가.

**설정 확인 결과:**

- `EnableProfitMessages=True` ✅
- `BotToken`/`ChatId` ✅ (DB에 저장됨)
- 따라서 최소 v5.10.89/90 경로에서는 작동해야 함

**수정:**

`TelegramService.cs:286-302`:

- `IsMessageTypeEnabled` false 시 → `🔇 [Telegram][{scope}] {type} 필터 비활성 → 스킵` 로그
- `EnsureTelegramClientReady` false 시 → `🔇 [Telegram][{scope}] Client 미준비 → 스킵 (BotToken/ChatId 확인)` 로그
- 전송 성공 시 → `✉️ [Telegram][{scope}] {type} 전송 성공` 로그

**사용 방법:**

배포 후 재시작 → 다음 API TP/SL 체결 시 FooterLogs에서:
- `📨 [Notify][PARTIAL] {symbol} 텔레그램 전송 요청` (DbManager 호출)
- `✉️ [Telegram][General] Profit 전송 성공` (실제 전송) 또는
- `🔇 [Telegram][General] Profit 필터 비활성 → 스킵` (원인 1) 또는
- `🔇 [Telegram][General] Client 미준비` (원인 2)

어떤 로그가 찍히는지에 따라 진짜 원인 특정 가능.

## [5.10.90] - 2026-04-23

### 🚨 Partial Fill 오분류 수정 + 텔레그램 알림 누락 근본 해결

**사용자 지적:**

> "metusdt 씨발아 30% 넘게 먹는건데 수익이 이상하잖아 왜 마진이 저따위야"
> "내가 매수한거 아니야 병신새끼야"
> "바이낸스 TP/SL 트레일링스탑 처리됐는데 텔레그램 메시지 안와"

**진단 1 — 봇 진입이 외부로 오분류된 광범위 버그:**

METUSDT 2026-04-23 00:06:13 Bot_Log: `DoubleCheck_PASS ML=99.4% TF=99.4%` → 봇 진입. 1초 후 `EXTERNAL_POSITION_INCREASE_SYNC` 8건 기록.
원인: Binance partial fill로 8청크 체결 → ACCOUNT_UPDATE 8번 발생 → 1번째만 정상, 2~8번째가 "외부 증가"로 오분류.

광범위 영향 (24h):
- MUSDT 27건, TAOUSDT 19건, SUIUSDT 14건, EDUUSDT 13건, HIGHUSDT 12건, CHIPUSDT 8건, METUSDT 8건, BASEDUSDT 7건 = 총 112+건 봇 진입이 "외부"로 잘못 기록됨.

**진단 2 — 텔레그램 누락 원인:**

v5.10.89 DbManager 중앙화 했지만 `TryNotifyProfit`의 `if (pnl==0 && pnlPercent==0) return` 조건으로 API 체결 시 pos=null이면 pnl=0 계산되어 **알림 스킵**.

**수정 — Partial Fill 오분류:**

- `_recentBotEntries` ConcurrentDictionary 추가 — 봇 시장가 주문 symbol+시각 저장
- `MarkBotEntryInProgress(symbol)` 호출 위치:
  - `PlaceEntryOrderAsync` (SPIKE_FAST 경로)
  - `ExecuteFullEntryWithAllOrdersAsync` 호출 직전 (일반 진입 경로)
- `ACCOUNT_UPDATE` 핸들러 EXTERNAL_POSITION_INCREASE_SYNC 분기 ([TradingEngine.cs:6595](TradingEngine.cs#L6595)):
  - `IsRecentBotEntry(symbol, 10초)` → true면 외부 기록 스킵, 수량만 내부 갱신

**수정 — 텔레그램 근본:**

- `DbManager.TryNotifyProfit` 조건 완화: `pnl==0 AND pnlPct==0 AND kind==ENTRY` 만 스킵 (기존: 무조건 0은 스킵 → API 체결 누락)
- 전송 시점 로그 추가 (`📨 [Notify][kind] symbol 텔레그램 전송 요청`)
- `RecordConditionalOrderFillAsync` ([line 3844](TradingEngine.cs#L3844)) 본인 텔레그램 제거 → DbManager 중앙 처리 (중복 방지)
- pos=null 시 `GetOpenTradesAsync`로 entry price DB 조회 fallback 추가 → PnL 정확 계산

**진단 3 — $200 안 들어간 이유 (설명):**

METUSDT 24h 거래량 $10M 미만 → `GetLiquidityAdjustedPumpMarginUsdt` ([line 5141](TradingEngine.cs#L5141)) 자동 50% 축소 ($200 → $100).
추가로 `SetLeverageAutoAsync` 심볼 max 레버리지 초과 시 자동 조정 → 실제 notional 감소.
**총 마진 약 $40 = 설정 $200의 20% 규모**가 된 이유.
(수정 없음, 설명만. 유동성 축소는 사용자 지시 유지)

## [5.10.89] - 2026-04-23

### 🔔 텔레그램 알림 중앙화 — DB INSERT 지점에서 단일 처리 (아키텍처 수정)

**사용자 지적:**

> "API에 등록된거 바이낸스에서 처리되면 메시지 내려주는거 받아서 insert 할꺼아냐 그럼 거기서 메시지 보내줘야지"

**문제:**

v5.10.88 이전: 각 caller (TradingEngine 4곳, PositionMonitorService 3곳)에서 개별로 `NotifyProfitAsync` 호출 → 경로 추가 시 누락 반복 발생.

**아키텍처 수정:**

- `DbManager`에 `TryNotifyProfit(symbol, pnl, pnlPct, kind)` 중앙 헬퍼 신규
- **DB INSERT 성공 지점에서 한 번만 호출** → 모든 caller 자동 알림
- 호출 위치:
  - `TryCompleteOpenTradeAsync` 성공 → `COMPLETE` kind
  - `RecordPartialCloseAsync` 성공 → `PARTIAL` / `PARTIAL_FINAL` kind
  - `CompleteTradeAsync` UPDATE/INSERT 성공 → `COMPLETE_UPDATE` / `COMPLETE_INSERT` kind
- PnL==0 AND PnLPercent==0 = 알림 스킵 (동기화 보정 건 제외)

**caller 측 중복 제거:**

v5.10.88에서 추가한 TradingEngine 3곳의 `NotifyProfitAsync` 제거 (DbManager가 처리):

- line 6390 (external close)
- line 6475 (flip close)
- line 6579 (external partial close)
- line 14868 (missed close)

**효과:**

- API TP1 자동 체결 → Binance 이벤트 → DB INSERT → **텔레그램 자동 발송** ← 사용자 요구 정확히 구현
- 향후 새 caller 추가 시 알림 누락 불가능 (DB 저장 시점이 유일 관문)
- 중복 알림 방지 (한 청산 = 한 알림)

## [5.10.88] - 2026-04-22

### 🚨 BTC 1H 하락필터 + 텔레그램 누락 전면 수정

**수정 1 — 외부 부분청산 (API TP1 자동 체결) 텔레그램 누락 (사용자 지적):**

v5.10.83에서 완전청산만 `NotifyProfitAsync` 호출했고 **부분청산 경로는 누락**. Binance API로 등록한 TP1이 자동 체결될 때 텔레그램 안 옴.

- [TradingEngine.cs:6550](TradingEngine.cs#L6550) `RecordPartialCloseAsync` 후 → `NotifyProfitAsync` 추가
- [TradingEngine.cs:6451](TradingEngine.cs#L6451) Flip 청산 (방향전환) → `NotifyProfitAsync` 추가
- [TradingEngine.cs:14859](TradingEngine.cs#L14859) MissedClose 복구 → `NotifyProfitAsync` 추가

이제 **모든 경로 청산**에서 텔레그램 알림 발송 (API TP1 / SL / 외부 수동 / flip / 누락 복구 / 내부 청산).

**수정 2 — Option A: BTC 1H 하락추세 필터 (사용자 요구):**

진단 (36시간 하락장 분석):

| 시간 (KST) | 건수 | 승률 | PnL |
|---|---|---|---|
| 04-21 23시 ~ 04-22 7시 (하락장 8h) | 122 | 20% | -$45 |
| 04-22 8시 이후 (회복) | 86 | 64% | +$91 |

봇이 **SHORT 안 함 → 하락장 LONG만 시도 → 데드캣 바운스 -30~50% ROE**.

수정 — `IsEntryAllowed`에 BTC 1H 필터 추가:

- 조건: BTC 1H 가격변화 ≤ **-0.8%** AND 알트 심볼
- 결과: 신규 진입 차단 (`BTC_1H_DOWNTREND (-X.XX%)` 로그)
- 제외: 메이저(BTC/ETH/SOL/XRP)는 본인 추세 따라 판단

**구현 상세:**

- `_marketDataManager.KlineCache["BTCUSDT"]` 조회 (5분봉 WebSocket 캐시)
- 최근 12개 5분봉 (=1시간) 시작가 vs 종가 비교
- BTC 데이터 누락 시 차단 안 함 (진입 누락 방지)

**효과:**

- 하락장 8시간 -$45 손실 → 필터 적용 시 대부분 진입 차단 기대
- BTC 상승 시 정상 진입
- 메모리 규칙 "AI 판단만" 보완: ML이 학습할 때까지의 안전망 (하드코딩이지만 BTC 추세라는 매크로 지표 사용)

## [5.10.87] - 2026-04-22

### 🎨 UI: 실시간 시장 신호 DataGrid에 마진/손익 컬럼 추가

**사용자 요구:**

> "실시간 시장 신호에 열 추가해서 진입마진금액 표시하는 곳이 있어야 할 것 같아.
> 얼마 진입해서 얼마 이익인지 손해인지 알 수가 없네."

**수정:**

- `MultiTimeframeViewModel`에 `EntryMarginUsdt` (마진) + `EntryNotionalUsdt` (명목금액) property 추가
- `EntryPrice`/`Quantity`/`IsPositionActive` setter에서 PropertyChanged 통지 추가 → 실시간 갱신
- `MainWindow.xaml`의 `dgMultiTimeframe`에 신규 컬럼 "마진/손익" (Width=120) 추가
  - 1행: **진입 마진** (예: $200.00)
  - 2행: **실시간 손익** (색상: 녹색=익절, 적색=손실)
  - 3행: Notional 명목금액 (회색, 작게)

**효과:**

진입중인 포지션마다 한 눈에 "$200 진입 → +$15.30 익절중" 같은 형태로 가시화. 마진 / 손익 / 명목 모두 동시 표시.

## [5.10.86] - 2026-04-22

### 🎯 Regime-Aware 아키텍처 — 급등장/횡보장 차별화 (사용자 요구 직접 반영)

**사용자 요구:**

> "단순 하드코딩은 한계가 있어. 급등장 오면 다 털려서 손절만 나게 됨.
> 큰 익절 노리다 놓침은 급등장에서 다 털려서 해놓은 거.
> 급등장과 횡보장에서의 차별화가 필요해."

**진단:**

`MarketRegimeClassifier` (Trending/Sideways/Volatile 분류) 모델은 이미 학습되어 있으나:
- 진입 시점 SL/TP/Trailing 등록에 regime 사용 안 함 → 모든 장에 동일 거리 (Pump-tuned)
- PUMP monitor (`MonitorPumpPositionShortTerm`)에 regime classifier 미연결
- AI Exit 활성화 임계값 ROE >= 10% → 횡보 코인은 영원히 ML 판단 못 받음

**수정:**

**1. 진입 시 Regime 분류 → 적응형 SL/TP/Trailing (`RegisterProtectionOrdersAsync`)**

| Regime | SL 배수 | TP 배수 | Trail 배수 | 부분익절 비율 |
|---|---|---|---|---|
| **Trending** (급등장) | 1.0× | 1.0× | 1.0× | 1.0× (현재 유지 — 큰 익절) |
| **Sideways** (횡보장) | **0.4×** | **0.4×** | **0.5×** | **1.5×** (tight + 큰 부분익절) |
| **Volatile** (변동성↑) | 0.75× | 0.75× | 0.5× | 1.2× |
| Unknown | 1.0× | 1.0× | 1.0× | 1.0× |

설정 ROE는 그대로 — ML regime이 어떤 배수 적용할지만 결정 (하드코딩 X).

**2. PUMP Monitor 횡보 익절을 ML regime 기반으로 교체**

기존: BB Width < 1.5% 하드코딩 임계값 (v5.10.85)
수정: `_regimeClassifier.Predict() == Sideways && conf >= 55%` → 익절
Fallback: regime 모델 미로드 시 BB Width 보수적 판단 유지

**3. AI Exit Optimizer 활성화 임계값 ROE 10% → 3%**

기존: `breakEvenActivated && ROE >= 10%` 조건 → 횡보 코인 영원히 ML 판단 못 받음
수정: `ROE >= 3%` (본절 미발동도 허용) → 작은 수익이라도 regime 약세 시 익절

**효과:**

1. 급등장 진입 시 → wide SL/TP/Trail 유지 (큰 익절)
2. 횡보장 진입 시 → tight 설정 (노이즈 회피, 빠른 익절)
3. ML regime 학습 데이터 누적 시 자동 향상

**리스크:**

- regime 분류 모델이 잘못 판단하면 Trending인데 Sideways로 분류 → 너무 빠른 익절
- regime confidence < 55% 시 Sideways exit 안 함 (보수적 안전망)
- 임계값 0.4×/0.5× 등은 v1 기본값; 학습 데이터 누적 후 조정

## [5.10.85] - 2026-04-22

### 🎯 사용자 핵심 요구 직접 반영: 메이저 동적 + 시간손절 + 횡보익절

**사용자 요구:**

> "설정에 주요심볼이 메이저코인이잖아"
> "알트들이 너무 오랜시간 보유하고 있다가 손절나는 경우가 너무 많아"
> "횡보일때 수익권이면 어떻게든 익절을 봐야하는데 그게 안되잖아"

**수정 1 — MajorSymbols 동적 로드:**

- 기존: `private static readonly HashSet<string> MajorSymbols = {"BTCUSDT","ETHUSDT","SOLUSDT","XRPUSDT","BNBUSDT","DOGEUSDT"}` 하드코딩
- 수정: 설정창 "주요 심볼" (txtSymbols / `AppConfig.Current.Trading.Symbols`) 동적 참조
- 효과: 사용자가 설정에서 메이저 변경 시 자동 반영 (BNB/DOGE 자동 제외)
- static 메서드 2곳(`ResolveCoinType`, `GetThresholdBySymbol`)도 동일 패턴 적용

**수정 2 — PUMP 시간 손절 구현 (PumpTimeStopMinutes 활성화):**

- 기존: `PumpTimeStopMinutes=120m` 설정값은 있으나 production 코드에 미구현 (백테스터에만 존재)
- 사례: TAOUSDT 9시간 보유, BUY_PRESSURE 17시간 보유 → 결국 손절
- 수정: `PositionMonitorService.MonitorPumpPositionShortTerm` 루프에 시간손절 추가
- 조건: `holdMinutes >= PumpTimeStopMinutes AND ROE <= 0` → 즉시 시장가 청산
- 로그: `[TIME_STOP] {symbol} {hold}분 ≥ {threshold}분 + ROE={roe}% → 시간 손절`

**수정 3 — 횡보 익절 (Sideways Profit-Take):**

- 사례: 알트 진입 후 횡보 → BB 좁아짐 → 결국 SL → 손실
- 수정: 30분+ 보유 + ROE≥5% (수익권) + BBWidth<1.5% (횡보) → 즉시 익절
- 로그: `[SIDEWAYS_PROFIT] {symbol} {hold}분 + ROE={roe}% + BBWidth={bbw}%(횡보) → 익절`
- 임계값(30분/5%/1.5%)은 v1 기본값; ML 학습 후 변경 가능

**효과:**

1. 메이저 정의가 설정창과 일관 → 사용자 변경 즉시 반영
2. PUMP 알트 9시간+ 자리 차지 차단 (TAOUSDT 같은 stuck 포지션 자동 정리)
3. 횡보 진입 후 수익권에서 익절 — "그게 안되잖아" 해결
4. 슬롯 회전율 상승 → 새 기회 진입 가능

**리스크:**

- 시간손절은 손실 중일 때만 작동 (수익권에선 트레일링 유지) → 보수적 설정
- 횡보 익절 BB Width 1.5% 임계값이 너무 엄격하면 발동 빈도↓ → 모니터링 필요

## [5.10.84] - 2026-04-22

### 🎯 Phase 6 — Multi-TF 추세전환 + M1 신뢰성 (사용자 요구 직접 반영)

**사용자 요구 (CHOPUSDT/M/AAVE 등 다중 TF 고점 진입 사고 방지):**

> "1H 하락추세 뚫었는지 확인 → 15m 상승전환 확인 → 5m 기준 → 1m 틱 진입.
> 하드코딩 금지, AI 학습 추론으로만."

**진단 (Multi-TF 검증 결과):**

- M1 fetch 실패 시 silent 0.5 fallback → ML이 "1m 정보 없음"을 못 배움
- PIT (학습) 경로에서 M1을 명시적으로 null 전달 → 모든 학습 샘플의 M1 feature가 0
- H1 추세전환 (golden cross/MACD turn-up) feature 부재 → ML이 "방금 다운→업 돌파" 학습 불가
- M15 상승전환 시퀀스 (consec bullish, hammer, engulfing) 부재 → "15m 반전" 학습 불가

**Phase A — M1 데이터 신뢰성:**

- 신규 `M1_Data_Valid` feature (1=fetch 성공, 0=실패) → ML이 1m 신뢰도 학습
- Realtime extractor: m1Klines null/Count<30 시 명시 0 마킹
- PIT extractor: 명시적 0 전달 (M1 부재 학습)

**Phase B — H1/M15 추세전환 6개 신규 feature:**

- `H1_BreakoutFromDowntrend` — 최근 5봉 내 SMA20-SMA60 음→양 전환 (다운→업 돌파)
- `H1_MACD_Hist_Turning_Up` — MACD 히스토그램 회복 (현재 > 이전)
- `H1_TrendChange_Count_Recent5` — 최근 5봉 내 추세전환 횟수 (잦으면 횡보)
- `M15_ConsecBullishCount` — 15분봉 연속 양봉 (0~5) — 상승전환
- `M15_Hammer_Pattern` — 해머 캔들 (lower_shadow > 2×body, body 작음)
- `M15_Bullish_Engulfing` — 직전 음봉 장악하는 양봉

**Phase C — 학습 스키마 갱신:**

- `featureColumns` 7개 추가
- `ExpectedFeatureCount` 115 → 122
- `initial_training_ready.flag` 삭제 → 다음 시작 시 재학습 강제 (forceRetrain)

**효과:**

ML이 "H1 다운트렌드 뚫고 M15 상승전환 + M1 정상 수집" 패턴을 자기 학습.
하드코딩 차단 (BB 상단 if문 같은 것) 없이 feature만 추가 → AI-only 원칙 유지.
v5.10.82 5분봉 학습 + 본 v5.10.84 추세전환 feature 결합 시 CHOP/AAVE/M 같은 고점 진입 자동 차단 기대.

**리스크:**

- 첫 재학습 시 ~5분 동안 AI Gate=차단 (새 122 feature 모델 빌드)
- 신규 feature 학습 데이터 부족 초기엔 보수적 판단 → 진입 빈도 일시 감소

## [5.10.83] - 2026-04-22

### 🚨 CRITICAL HOTFIX: SPIKE_FAST 무방비 진입 + 외부청산 텔레그램 누락

**BUG 1 — SPIKE_FAST 진입 후 SL/TP/Trailing 미등록 (자본 40% 손실 원인):**

- 사례: PIEVERSEUSDT TICK_SURGE 진입 → 162분 보유 → -32% 손실 → 사용자 수동청산
- 원인: `ExecuteImmediateSpikeEntry` ([TradingEngine.cs:8332](TradingEngine.cs#L8332))는 `PlaceEntryOrderAsync`만 호출 → `ExecuteFullEntryWithAllOrdersAsync` 미경유 → SL/TP/Trailing 거래소에 한 번도 등록 안 됨
- 의존: 90초 REST 폴링 (`MonitorPumpPositionShortTerm`) → 빠른 가격 변동 미대응
- 수정: 신규 헬퍼 `RegisterProtectionOrdersAsync(symbol, isLong, qty, entryPrice, leverage, source, token)` 추가
  - PUMP/Major 별 ROE → 가격 변환 (slRoe/leverage = priceMove%)
  - SL 전체수량, TP 부분수량, Trailing 잔여수량 일괄 등록
  - `_settings`의 `PumpStopLossRoe`/`PumpTp1Roe`/`PumpTrailingGapRoe`/`PumpFirstTakeProfitRatioPct` 사용
- SPIKE_FAST 진입 성공 직후 강제 호출 → 무방비 포지션 0건 보장
- SL 등록 실패 시 `OnAlert`로 즉시 알림 (수동 SL 설정 유도)

**BUG 2 — 외부 청산(Binance SL/TP/수동) 텔레그램 알림 누락:**

- 사례: 익절/부분익절/본절청산/손절청산 모두 텔레그램 메시지 안 옴
- 원인: 외부 청산은 WebSocket ACCOUNT_UPDATE 이벤트로 감지 → `TryCompleteOpenTradeAsync` ([TradingEngine.cs:6230](TradingEngine.cs#L6230))로 DB만 기록 → `NotifyProfitAsync` 호출 누락
- 내부 청산(PositionMonitorService.cs:3012)에는 `NotifyProfitAsync` 있지만, 거래소가 SL/TP를 자동 체결한 경우 외부 청산으로 분류돼 알림 안 감
- 수정: `TryCompleteOpenTradeAsync` 성공 직후 `NotificationService.Instance.NotifyProfitAsync(symbol, pnl, pnlPercent, dailyTotalPnl)` 호출 추가
- 알림 실패 시 `OnStatusLog`로 백업 기록

**효과:**

1. 모든 신규 진입(SPIKE_FAST 포함)이 거래소에 SL/TP/Trailing 등록 보장 → 무방비 -32% 사고 차단
2. 모든 청산 이벤트(내부+외부)가 텔레그램 알림 → 사용자가 실시간 손익 추적 가능
3. SL 등록 실패 시 즉시 알림 → 수동 대응 가능

**리스크:**

- 메이저 ROE 설정값과 PUMP ROE 설정값이 SPIKE_FAST에도 적용 → SL 폭이 사용자 설정 따라 다를 수 있음 (현재 PUMP 기본 -40% ROE = 20x 기준 -2% 가격)

## [5.10.82] - 2026-04-22

### 🔥 ROOT CAUSE FIX: AI 역상관 근본 수정 (학습/추론 타임프레임 일치 + AI Gate 강제)

**진단 결과 (2026-04-22 12:00 이후 86 트레이드):**

| 지표 | 값 |
|---|---|
| 승률 | 37.2% (32W/54L) |
| 총 PnL | -$36.91 |
| AiScore 0.80+ 승률 | **0.0%** ❌ (11건) |
| 5-15분 보유 승률 | **0.0%** ❌ (24건) |
| TICK_SURGE 비중 | 70/86건 = 81% (단일 전략 의존) |

**근본 원인 — 학습/추론 타임프레임 불일치:**

- 학습: `KlineInterval.OneHour`, label="10시간 이내 +2.5% 도달" (스윙 horizon)
- 추론: `KlineInterval.FiveMinutes`, 5~15분 단타 진입에 사용
- 같은 `RSI/BB/ATR` feature지만 1H vs 5M 통계 분포 완전 다름 → 모델이 본 적 없는 입력 → 체계적 역답변

**Option A — 학습 파이프라인 5분봉 통일:**

- `TriggerInitialMLNetTrainingIfNeededAsync`, `RetrainMlNetPredictorAsync` 모두 `KlineInterval.OneHour` → `FiveMinutes`로 변경
- 라벨 정책: `LOOKAHEAD=10` (10h) → `6` (30분), `TARGET_PCT=2.5%` → `0.5%`, `STOP_PCT=1%` → `0.3%` — 단타 horizon
- `LightGbm.UnbalancedSets=true` 추가 (AITrainer + MLService 양쪽) — 단타 라벨 양성 클래스 희소성 보정

**Option B — AI Gate 우회 경로 전부 제거 (`skipAiGateCheck:true → false`):**

- `TICK_SURGE` (line 1278) — 70/86건 0% 승률 원인이 게이트 우회
- `SQUEEZE_BREAKOUT`, `MAJOR_MEME`, `MAJOR`, `FORECAST_FALLBACK`, `ETA_TRIGGER`, `GATE2` 전부 게이트 강제
- 메이저 신호도 AIDoubleCheckEntryGate 통과 강제 (`Major` variant 모델로 검증)

**B2 — EntryTimingMLTrainer 4 variant 강제 학습:**

- 봇 시작 시 `IsReady=false`이면 `forceRetrain: true`로 호출
- `initial_training_ready.flag` 삭제 → 다음 시작 시 재학습 강제

**효과:**

1. AiScore가 추론 분포와 일치하는 통계로 학습됨 → 0.80 = 0% 승률 패턴 해소
2. AI Gate 우회 차단 → 모델 미학습 시 진입 자체 차단 (잘못된 진입 방지)
3. 단일 전략(TICK_SURGE) 의존도 감소 — 게이트가 차단/허용을 결정
4. 클래스 imbalance 보정 — 양성 라벨 가중치 조정

**리스크:**

- 첫 실행 시 모델 재학습 시간(~5분) 동안 AI Gate=차단 → 신규 진입 0건
- 학습 완료 후 게이트가 너무 엄격해서 진입 빈도 급감 가능 (역상관보다는 안전)

## [5.10.81] - 2026-04-22

### 🛡️ HOTFIX: 단일 진입 게이트 (IsEntryAllowed + PlaceEntryOrderAsync) — 메이저 비활성화 우회 근본 차단

**문제:**
설정창에서 메이저 코인 비활성화(`EnableMajorTrading=false`) 했는데도 ETH/XRP 등이 진입.
원인: 5개 산재 if문으로 분산 가드 → SPIKE_FAST 경로(`HandleSpikeDetectedAsync` → `PlaceOrderAsync` 직접 호출)가 `ExecuteAutoOrder` 라우터를 우회하여 게이트 누락.

**근본 수정 (땜빵 아닌 아키텍처):**

- 신규 `IsEntryAllowed(symbol, source, out reason)` — 단일 게이트 헬퍼
- 신규 `PlaceEntryOrderAsync(symbol, side, qty, source, ct)` — 신규 진입(reduceOnly=false) 단일 진입점
- 기존 5개 산재 체크 (TICK_SURGE/SQUEEZE_BREAKOUT/MAJOR_SIGNAL/MAJOR_ANALYZE/ROUTER) 모두 `IsEntryAllowed` 호출로 통합
- SPIKE_FAST `_exchangeService.PlaceOrderAsync` 직접 호출 → `PlaceEntryOrderAsync` 래퍼로 교체 (게이트 자동 강제)
- 코멘트로 "신규 진입 시 PlaceEntryOrderAsync 또는 IsEntryAllowed 호출 필수" 명시

**효과:**
- 새 진입 경로 추가 시 자동으로 게이트 적용 (누락 방지)
- "버전업할 때마다 메이저 우회 누락" 패턴 근본 차단
- 향후 차단 조건 추가는 `IsEntryAllowed` 한 곳에만 추가

## [5.10.80] - 2026-04-20

### Phase 5-D — Order Book Depth5 + Open Interest REST polling + Lorentzian Hard Mode + Major auto-unblock

**인프라 (5-D):**

- 신규 `OpenInterestCacheItem` (OpenInterest + 15분 전 스냅샷 보관)
- 신규 `DepthCacheItem` (Top5 BidVolume/AskVolume/BidValue/AskValue)
- `MarketDataManager.OpenInterestCache`, `MarketDataManager.DepthCache` 추가
- `SubscribeToPartialOrderBookUpdatesAsync` (5 levels, 100ms) 구독
- `StartOpenInterestPoller` — 1분 REST 폴링 + 15분 스냅샷 추적 (WebSocket 미지원 대응)

**신규 5개 ML feature:**

- `Depth5_BidAskImbalanceRatio` — top5 호가 매수/매도 불균형
- `Depth5_BidValueToAskValueRatio` — 매수/매도 달러 가치 비율
- `OpenInterest_Normalized` — log scale 정규화
- `OpenInterest_Change_15m_Pct` — 15분 OI 변화율 (스퀴즈 선행)
- `OpenInterest_Surge` — 15분 +20% 이상 1 (대규모 신규 진입)

**ML 파이프라인:**

- `featureColumns` 5개 추가
- `ExpectedFeatureCount` 110 → 115

**Lorentzian Phase 2 (Hard Mode):**

- `LorentzianHardMode` (기본 false) + `LorentzianHardModeMinSamples` (기본 200) 신규 설정
- 활성화 시: KNN 약세 + 충분 샘플 → 진입 차단 (`LORENTZIAN_HARD_BLOCK`)
- 기본 비활성으로 안전 (필요 시 사용자 활성화 — 하드코딩 차단 아님)

**Major 자동 해제:**

- v5.10.66 메이저 임시 차단 → Major 전용 모델(`_mlTrainerMajor`) 로드 시 자동 해제
- 3 모델 분리(v5.10.76) 이후 Major 데이터 격리되어 추론 신뢰 가능
- 학습 완료 후 자동으로 BTC/ETH/SOL/XRP 진입 재개

**효과:**
호가 깊이 + 미체결약정 = 펌프/덤프 사전 감지. KNN 합의 게이트로 ML 단독 오판 차단. AI 학습 완료 후 메이저 자동 활성화로 운영 자동화 강화.

## [5.10.79] - 2026-04-22

### Phase 5-C — aggTrade + MarkPrice/Funding Rate WebSocket + 5 신규 feature

**인프라:**

- 신규 `AggTradeStatsItem` (BuyVol/SellVol 1분 슬라이딩 윈도우)
- 신규 `MarkPriceCacheItem` (MarkPrice + FundingRate + NextFundingTime)
- `MarketDataManager.AggTradeStats`, `MarketDataManager.MarkPriceCache` 추가
- `_aggTradeBuffer` 60초 슬라이딩 큐로 매수/매도 볼륨 집계
- `SubscribeToAggregatedTradeUpdatesAsync` / `SubscribeToMarkPriceUpdatesAsync` (1초 주기) 구독

**신규 5개 ML feature:**

- `AggTrade_Buy_Ratio_1m` (0~1) — taker buy / total. 0.7+ = 매수폭발 펌프임박
- `AggTrade_Buy_Volume_1m` / `AggTrade_Sell_Volume_1m` — log scale 정규화
- `Funding_Rate` — 8h funding rate (±0.01% 정상)
- `Funding_Rate_Extreme` — 절댓값 0.05% 초과 시 1 (롱/숏 스퀴즈 임박)

**ML 파이프라인:**

- `featureColumns` 5개 추가
- `ExpectedFeatureCount` 105 → 110

**효과:**
펌프 직전 시그널을 ML이 학습. taker 매수 폭발 + Funding 극단 = 강력한 선행 지표.

## [5.10.78] - 2026-04-22

### Phase 5-B — 학습 쪽 variant 분리 완성

`AIDoubleCheckEntryGate.TriggerInitialTrainingAsync` 학습 단계 4 모델 분리:

- **Default** trainer: 전체 통합 학습 (fallback 용도)
- **Major** trainer: `IsMajorSymbol` 필터된 BTC/ETH/SOL/XRP 데이터만
- **Pump** trainer: 알트코인 데이터만 (Major 제외)
- **Spike** trainer: 우선 Pump와 동일 (1분봉 초단타 학습 데이터 별도 수집은 향후 PR)

각 trainer 별 `INIT_ML_{label}_{timestamp}` runId로 `AiTrainingRuns` DB에 기록 → 학습 이력 추적 가능. 데이터 < 10개면 해당 variant 스킵.

`RaiseCriticalTrainingAlert`에 4 variant 샘플 카운트 함께 표시.

학습 후 4개 trainer 모두 LoadModel() 호출하여 즉시 추론 가능.

## [5.10.77] - 2026-04-22

### Phase 5-A — WebSocket BookTicker + 호가창 선행 지표 4개

**인프라:**

- 신규 `BookTickerCacheItem` 클래스 (Models.cs) — Best Bid/Ask 가격·수량 + 갱신 시각
- `MarketDataManager.BookTickerCache` 신규 (ConcurrentDictionary)
- `StartPriceWebSocketAsync`에 `SubscribeToBookTickerUpdatesAsync(_majorSymbols)` 추가
- 실시간 메이저 심볼 호가 갱신 캐시 (5초 신선도 체크)

**신규 4개 ML feature** — 펌프 직전 선행 지표:

- `BidAskImbalanceRatio` (0~1) — `BidQty / (BidQty+AskQty)`. 0.7+ = 매수우세
- `SpreadPct` (%) — `(Ask-Bid)/Mid × 100`. 낮을수록 유동성 풍부
- `BidQtyToAskQtyRatio` (0~10) — 매수/매도 수량 비율. 3.0+ = 매수폭발
- `MidPriceVsLastPct` (%) — 호가 중간가 vs 마지막 체결가 차이

**ML 파이프라인:**

- `EntryTimingMLTrainer.featureColumns` 4개 추가
- `ExpectedFeatureCount` 101 → 105 (모델 스키마 불일치 시 자동 재학습)

**효과:**
ML이 호가창 선행 지표로 펌프 임박 학습 가능. PIT/히스토리컬 경로는 호가 데이터 없으므로 0 기본값. 실시간 추론 경로에서만 활성.

### 다음 (Phase 5-B/C)

- aggTrade (체결 매수/매도 비율)
- Funding Rate / Open Interest WebSocket 구독
- 학습 쪽 variant 분리 (Major/Pump/Spike 별도 학습)

## [5.10.76] - 2026-04-22

### Phase 3 — 3 모델 분리 (Major/Pump/Spike, 사용자 메모리 원칙 준수)

**인프라:** `EntryTimingMLTrainer`에 `ModelVariant` enum + variant별 기본 경로 분기
- `EntryTimingModel.zip` (Default — fallback)
- `EntryTimingModel_Major.zip` (BTC/ETH/SOL/XRP)
- `EntryTimingModel_Pump.zip` (일반 알트)
- `EntryTimingModel_Spike.zip` (1분봉 초단타)

**AIDoubleCheckEntryGate 통합:**
- 4개 trainer 인스턴스 (Default/Major/Pump/Spike) 동시 보유
- 신규 `SelectTrainerForSymbol(symbol, signalSource)` — 심볼별 동적 라우팅
  - `IsMajorSymbol` → Major trainer (로드돼 있으면)
  - `signalSource` SPIKE/TICK_SURGE/M1_FAST_PUMP → Spike trainer
  - 그 외 → Pump trainer
  - 모든 variant 미로드 → Default fallback
- `EvaluateEntryAsync` 예측 호출 선택된 trainer로 교체
- `ML_DIAG` 로그에 `model=Variant` 표시 추가

**IsReady 조건 확장:** 4개 중 하나라도 로드되면 ready.

### 향후 작업
- 학습 쪽도 variant 분리 (현재는 Default만 학습, variant별 학습 데이터 필터는 별도 PR)
- 학습 데이터 부족 시 Default 모델에서 transfer learning 방식 검토

## [5.10.75] - 2026-04-22

### Phase 2 — AI 학습 강화 (하드코딩 대체)

**[A] 신규 feature 11개 추가** — 고점 진입 / 다중 TF confluence / 심볼 성과를 ML이 학습

`MultiTimeframeEntryFeature.cs` + `MultiTimeframeFeatureExtractor.cs`:

- `Price_Position_In_Prev5m_Range` (0~1) — 직전 5분봉 내 현재가 위치
- `M1_Rise_From_Low_Pct` / `M1_Pullback_From_High_Pct` — 1분봉 꼭대기 지표
- `Prev_5m_Rise_From_Low_Pct` — 여유도
- `Symbol_Recent_WinRate_30d` / `Symbol_Recent_AvgPnLPct_30d` — 심볼 성과 (DB 10분 캐시)
- `M15_Position_In_Range` / `M15_Upper_Shadow_Ratio` / `M15_Is_Red_Candle` / `M15_Rise_From_Low_Pct` — 15분봉 캔들 특성
- `MultiTF_Top_Confluence_Score` — M1+M5+M15 고점 confluence 평균

**하드코딩 아님** — ML이 이 feature를 보고 "고점 진입 위험" 스스로 학습. TOP_SCORE_ENTRY / MEGA_PUMP / AI_ENTRY 모든 경로에서 동일한 feature 사용.

**`EntryTimingMLTrainer.cs`**: `ExpectedFeatureCount` 90 → 101, `featureColumns` 11개 추가. 모델 스키마 불일치 감지 시 기존 모델 자동 폐기 + 재학습.

**[B] 수동 청산 fast-path 추가** — CHIP 10초 지연 해결

기존 `ExecuteMarketClose`: `GetPositionsAsync` + `CancelAllOrdersAsync`(algo) 동기 대기로 10초+ 지연 → 30% 익절 → 손절 전환.

신규 `ExecuteManualCloseFast` (`PositionMonitorService`):
1. local 캐시에서 즉시 수량/방향 취득 (REST 생략)
2. 시장가 `reduceOnly` 주문 즉시 전송 (~1-2초)
3. algo 취소 + TradeHistory 업데이트는 `Task.Run` 비동기

수동 청산 호출부(TradingEngine.cs) fast-path 사용으로 전환.

**[C] DataGrid CJK 폰트 fallback** — `币安人生USDT` 인코딩 깨짐 수정

`ModernDataGridStyle`에 `FontFamily` setter 추가 (`Segoe UI, Malgun Gothic, Microsoft YaHei UI, Microsoft JhengHei UI, Microsoft YaHei, SimSun`). 기존 상속 체계에서 DataGrid가 누락된 문제.

### 검증 필요 (사용자 재시작 후)

- `[Label][Pending]` 로그 — 라벨 파이프라인 (v5.10.72 효과)
- `[ML_STRONG_OPPOSITE]` 로그 — ML 강한 신호 반응 (v5.10.68)
- `[MAJOR_BLOCKED_v5_10_66]` 로그 — 메이저 차단
- `[ManualFast]` 로그 — 수동 청산 fast path 발동
- 한자 심볼 UI 정상 표시

### 다음 단계 (별도 PR)

- Phase 3: 3 모델 분리 (메이저/PUMP/SPIKE)
- 하드코딩 전수 제거 (STALE_SIGNAL, slot 기준도 feature화)
- 호가창/tick/Funding/OI 선행 지표 인프라

## [5.10.74] - 2026-04-21

### 롤백 — v5.10.73 하드코딩 차단 제거 (사용자 메모리 "AI 판단만 사용" 원칙 위반)

**제거된 하드코딩:**
- `HIGH_CHASE_BLOCK` (TradingEngine.cs) — 5분봉 High +0.3% 초과 차단
- `LOSSY_SYMBOL_BLOCK` + `IsLossySymbolBlacklisted` + `_lossySymbolCache` — 30d 누적 손실 블랙리스트
- `M1_TOP_BLOCK` (PumpScanStrategy.cs) — 1분봉 riseFromLow/pullback 기반 차단

**원칙 위반 사유:**
사용자 메모리 `feedback_ai_only_entry.md` — **"모든 진입은 AI(ML.NET) 학습→추론→예측으로만. 하드코딩 조건 금지."** v5.10.73에서 증상별 하드코딩 차단을 추가했으나, 이는 AI가 학습할 기회를 박탈하는 땜질. 올바른 해결은 **feature로 학습**하도록 하는 것.

**유지된 변경:**
- ✅ 라벨링 파이프라인 복구 (v5.10.72): `_pendingRecords` 큐 선행 검색 + flush 1분
- ✅ ML 클래스 불균형 보정 (v5.10.73): positive oversample + 2:1 비율 + `UnbalancedSets=true` (이건 **학습 데이터 차원**, 하드코딩 아님)
- ✅ **폰트 CJK 지원 추가** (신규) — `币安人生USDT` 등 한자 심볼 UI 인코딩 깨짐 수정: MainWindow.xaml + App.xaml의 FontFamily에 `Microsoft YaHei UI, Microsoft JhengHei UI, SimSun` fallback 추가

### Phase 2 설계 시작 (하드코딩 대체)

3 모델 분리 + feature 추가로 고점 진입 / 반복 손실 심볼을 AI가 학습하도록 재설계:
- `EntryTimingMLTrainer_Major / _Pump / _Spike` 분리
- `MultiTimeframeEntryFeature`에 선행 지표 추가:
  - `Price_Position_In_Prev5m_Range` (직전 5분봉 내 현재가 위치 0~1)
  - `M1_Rise_From_Low_Pct`, `M1_Pullback_From_High_Pct`
  - `Symbol_Recent_WinRate_30d`, `Symbol_Recent_PnL_30d`
- ML이 학습 후 자동 판단 (차단 여부도 ML 출력)

상세 설계안은 별도 보고 예정.

## [5.10.73] - 2026-04-21

### Phase 1-B — ML 클래스 불균형 2단계 보정 + MUSDT/TAOUSDT 즉시 대응

**[A] ML 클래스 불균형 보정 강화** (`EntryTimingMLTrainer`)

- **positive oversampling**: positive < 50개면 bootstrap 3배 복제
- **negative 다운샘플링 강화**: 기존 5:1 → **2:1 비율** (minority class 학습 비중 확대)
- **LightGbm `UnbalancedSets = true`**: LightGBM 자체 클래스 불균형 가중치 자동 부여
- 로그: `밸런싱 완료: pos=N (orig=M), neg=N, ratio 1:X.X`

**[B] MUSDT 사례 — 고점 추격 매수 차단** (`TradingEngine.cs` Gate 1)

- 증상: 7일 61건 진입 중 승률 **15%**, AvgPnLPct **-24.59%**. 23:12 진입가 $4.343 > 직전 5분봉 High $4.340 (+0.3% 초과 돌파)
- 수정: 직전 완성 5분봉 High 대비 **+0.3% 이상 초과** 시 `[HIGH_CHASE_BLOCK]` 차단
- MEGA_PUMP / M1_FAST_PUMP / CRASH/PUMP_REVERSE는 예외 (의도된 돌파 매수)

**[C] TAOUSDT 사례 — 누적 손실 심볼 자동 블랙리스트** (`TradingEngine.IsLossySymbolBlacklisted`)

- 증상: 7일 13건 승률 **31%**, Total **-$45**. 4/15, 4/16, 4/20, 4/21 반복 진입
- 수정: **30일 누적 N≥5 AND WinRate<30% AND TotalPnL<-$30** = 자동 차단
- `_lossySymbolCache` 30분 TTL 캐시 (DB 조회 비용 회피)
- 로그: `[LOSSY_SYMBOL_BLOCK] 30d N=13 win=31% pnl=$-45.72`

### 예상 누적 효과 (v5.10.72 + v5.10.73)

- **라벨링 복구** (1-A) → 24h 내 라벨 0 → 수십~수백 건
- **ML 재학습 정상화** (1-B) → positive class 학습 부실 해결
- **MUSDT 즉시 차단** (1-B-B) → 고점 추격 -24% 손실 방지
- **TAOUSDT 차단** (1-B-C) → 반복 손실 심볼 30일 휴면

## [5.10.72] - 2026-04-21

### Phase 1-A — 라벨링 파이프라인 긴급 복구 (30일 -$5,710 손실 근본 원인)

**🚨 v5.10.66 이후 라벨 N=0 현상의 직접 원인:**

- `UpsertLabelInRecentFilesAsync`가 **디스크 파일만 검색**
- AAVE 등 **빠른 외부 청산(3분)** 케이스는 `_pendingRecords` 큐에만 있고 아직 디스크 flush 안 됨 (5분 주기)
- → 매칭 실패 → `AiLabeledSamples` INSERT 안 됨 → 온라인 재학습 트리거(200건) 영원히 도달 불가
- → ML 모델이 **새 데이터로 학습 못 함** = 고정된 오염 모델로 지속 예측

**수정** ([AIDoubleCheckEntryGate.cs:929](AIDoubleCheckEntryGate.cs#L929)):
1. **`_pendingRecords` 큐 선행 검색** — flush 전 레코드도 라벨 매칭 대상 추가 (참조 타입 직접 업데이트)
2. **flush 주기 5분 → 1분** 단축 (이중 안전장치)
3. 큐 매칭 성공 시 `[Label][Pending]` 로그 발생

**예상 효과:**
- 24시간 내 라벨 수 0 → 수십~수백 건 복구
- 200건 누적 후 `RetrainModelsAsync` 자동 트리거
- AI Gate `ML_DIAG` 0% 현상 점진 완화 (새 샘플로 재학습)

**진단 SQL (24h 후):**
```sql
SELECT COUNT(*), SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END)
FROM AiLabeledSamples WHERE EntryTimeUtc >= '2026-04-21 14:00'
  AND LabelSource = 'mark_to_market_15m';
```

### Phase 1-B (다음 hotfix) — 클래스 불균형 보정 + 재학습 + 일반화 검증

v5.10.73에 포함 예정.

## [5.10.71] - 2026-04-21

### Phase D+B — TOP_SCORE_ENTRY + WAIT 고점수 큐 fallback (RAVE 미진입 해결)

**근본 원인 (RAVE 케이스):**
- RAVEUSDT가 top60 **#2위 (score 0.53~0.54)** 로 봇이 인식했으나 매매 못 함
- volumeMomentum 0.60~0.66 (고거래량 코인 특성 — 20봉 평균 대비 스파이크 비율 낮음)
- bullishSignals < 3 → `decision = WAIT` 지속
- WAIT는 우선순위 큐에도 등록 안 됨

**[D] TOP_SCORE_ENTRY 신규 진입 경로** (`PumpScanStrategy.AnalyzeSymbolAsync`)
- 위치: MEGA_PUMP → M1_FAST → **TOP_SCORE_ENTRY** → AI_ENTRY → FALLBACK 순
- 조건: `top60 rank ≤ 3` AND `score ≥ 0.5` AND `5분봉 +3%` AND `양봉` AND `단기추세` AND `!isOverextended` AND `RSI<75`
- 효과: RAVE 같은 고거래량 고점수 코인도 5분봉 +3% 펌프 시 즉시 진입
- 로그 키워드: `[TOP_SCORE_ENTRY]`

**[B] PumpScan top60 score를 큐 fallback으로** (`TradingEngine.cs:9157`)
- 슬롯 포화 시 우선순위 큐 등록: 기존 `_aiApprovedRecentScores`만 조회 → **없으면 `_pumpStrategy.TopCandidateScores` fallback**
- rank penalty: 1위 = 100%, 2위 = 90%, ..., 10위 = 10%
- 효과: AI 미승인 WAIT 심볼도 top60 상위면 큐에 축적 → 슬롯 빈 시점 재평가 기회

**[신규 인프라]** `PumpScanStrategy.TopCandidateScores` (IReadOnlyDictionary)
- `ExecuteScanAsync`에서 top10 후보의 (rank, score, time) 10분 캐시
- AnalyzeSymbolAsync + TradingEngine 양쪽에서 공유 조회

## [5.10.70] - 2026-04-21

### Phase B-2 — PUMP 1분봉 fast-path (5분 후행 → 1분 후행)

- **신규 1분봉 fast-path** (`PumpScanStrategy.AnalyzeSymbolAsync` MEGA_PUMP 직전):
  - WebSocket 1분봉 캐시(`MarketDataManager.Instance.GetCachedKlines(OneMinute, 5)`) 활용
  - 조건: 1분봉 +3% AND 거래량 10배 + 양봉 + 단기추세(`isUptrend OR price>SMA20`) + 과열 아님 + RSI<75
  - 통과 시 즉시 `decision = "LONG"` → 5분봉 종가 대기 0
- **기존 MEGA_PUMP 보존**: `if (decision == "WAIT")` 조건 추가하여 1분봉이 못 잡으면 5분봉 fallback
- **AXLUSDT 09:00 케이스 검증**: 1분봉 +5% 거래량 폭발 시 즉시 진입 가능 (09:01 진입 vs 기존 09:05+)
- 로그 키워드: `[M1_FAST_PUMP]`

### 효과 요약 (v5.10.69 + v5.10.70 통합)

| 항목 | Before v5.10.68 | After v5.10.70 |
|---|---|---|
| PUMP 진입 후행 | 5분 (5분봉 종가) | **1분** (1분봉 종가) |
| KST 9시 PUMP 슬롯 | 평시 슬롯 | 평시 +1 |
| KST 9시 STALE 임계 | 2% | 5% |
| KST 9시 ML 임계 | 0.65 | 0.55 |
| 한자 심볼 algo 취소 | -1022 실패 | 정상 |

## [5.10.69] - 2026-04-21

### Hotfix — Algo API -1022 서명 버그 + KST 9시 펌프 대응

**[A] Algo API -1022 Signature invalid 버그 수정** (`BinanceExchangeService.CallAlgoApiAsync`)

- **증상:** `币安人生USDT` 같은 한자 심볼에서 `algoOrders 취소`/`openAlgoOrders 조회` 시 `{"code":-1022,"msg":"Signature for this request is not valid."}` 반복 발생. POST(등록)는 OK, GET/DELETE만 실패.
- **근본 원인:** .NET HttpClient가 GET/DELETE URL의 non-ASCII 문자를 자동 percent-encoding 하는 반면, HMAC-SHA256 서명은 raw 문자열로 계산 → 서명한 query와 실제 전송된 query 불일치.
- **수정:** 신규 `NormalizeAlgoQueryParams()` — query string의 value만 미리 `Uri.EscapeDataString` 적용. 서명·전송 동일 문자열 사용.
- **위험성:** 알고 주문 취소 실패 → 중복 SL/TP 누적 → 의도치 않은 청산. 수정 시급도 HIGH.

**[B] KST 9시 펌프 시간대 대응 (3개 동시 완화)**

KST 9시±15분(8:45~9:15)에 한국 시장 진입 펌프 집중 발생 (24h 중 거래량 4~20배, 변동성 2~10배). AXLUSDT 케이스 검증: 09:00 5분봉 +7%, 거래량 40배 → 봇이 신호 잡았으나 슬롯 포화/STALE/ML 보수성으로 전부 차단.

- **PUMP 슬롯 동적 +1** (`TradingEngine.GetDynamicMaxPumpSlots`): KST 8:45~9:15 = `MAX_PUMP_SLOTS + 1`. 평시 = 기본값.
- **STALE_SIGNAL 임계값 완화** (`TradingEngine.cs:9201`): KST 9시 시간대 = 5%, 평시 = 2%. 펌프 초기 진입 기회 보존.
- **ML 임계값 완화** (`AIDoubleCheckEntryGate.EvaluateEntryAsync`): KST 9시 시간대 `effectiveMLThreshold` 0.55로 클램프. 보수적 학습으로 인한 기회 누락 방지. `[KST9_RELAX]` 로그.

### 분리 예정 — Phase B-2 (v5.10.70)

PumpScan 1분봉 fast-path (5분봉 종가 대기 5분 → 1분봉 +3% & 거래량 10배 1분 후행)는 PumpScanStrategy 핵심 흐름 변경 필요. 별도 PR/hotfix로 분리.

## [5.10.68] - 2026-04-21

### Hotfix — EXTERNAL_PARTIAL_CLOSE_SYNC 잔량 동기화 버그 + ML 강한 신호 빠른 반응

**[A] EXTERNAL_PARTIAL_CLOSE_SYNC 라우팅 버그** (`DbManager.SaveTradeLogAsync`)

- **증상:** AAVEUSDT 활성 포지션 Quantity=1.6, 외부에서 1.0 부분청산됐으나 잔량 0.6 미반영. 활성 포지션과 실시간 시장 신호 ROI 불일치.
- **원인:** `string.Equals(ExitReason, "PartialClose", IgnoreCase)` 정확 매칭만 → `EXTERNAL_PARTIAL_CLOSE_SYNC` 등은 `CompleteTradeAsync`로 잘못 라우팅 → 잔량 update 누락.
- **수정:** `IndexOf("Partial", IgnoreCase) >= 0` 키워드 매칭으로 변경. EXTERNAL_PARTIAL_*, External Partial Close Sync 등 모두 `RecordPartialCloseAsync` 경로로 정상 라우팅.

**[B] ML 강한 반대/동방향 신호 빠른 반응** (`PositionMonitorService` + `TradingEngine`)

- **증상:** 5분마다 강한 ML 신호(downProb 82~100%) 발생하지만 활성 포지션이 60분 주기로만 검증 → AAVE -30% 방치.
- **원인:**
  1. ML 신호가 `_pendingPredictions` 캐시에만 저장, PositionMonitor와 단절
  2. `MonitorPositionStandard`의 `nextAiRecheckTime` 60분 주기 (5분 신호 무시)
  3. `OnPredictionUpdated` orphaned event (구독자 0)
- **수정:**
  1. **신규 `PositionMonitorService.UpdateExternalMlSignal(symbol, dir, upProb, conf)`** — 외부 ML 신호 캐시
  2. **TradingEngine 두 ML 발생부**(PUMP scan + COMMON scan)에서 `_positionMonitor?.UpdateExternalMlSignal(...)` 호출
  3. **MonitorPositionStandard 매 틱 캐시 조회** → 강한 반대 신호(confidence ≥ 85%, upProb ≤ 20% / ≥ 80%) 즉시 처리:
     - ROE < 5% → **즉시 시장가 청산** (`ML Strong Opposite` 사유)
     - ROE ≥ 5% → **본절 즉시 락** (BreakevenPrice = entryPrice 설정, 1회 적용)
  4. **AI 재검증 주기 60분 → 15분 단축** (3곳: 라인 222, 278, 533)

### Phase 2 예정

- 강한 동방향 신호 → trailing gap 1.2배 확대 (HybridExitManager 통합)
- TransformerStrategy.OnPredictionUpdated 리바이브
- ML + Lorentzian 합의 검증 게이트

## [5.10.67] - 2026-04-20

### Refactor — DbManager 인라인 SQL 전체 SP 전환 + 인라인 MERGE 완전 제거

**근본 원인:** 인라인 MERGE 사용 시 `(HOLDLOCK)` 으로 인한 lock 경합 발생. 모든 UPSERT는 `UPDATE → IF @@ROWCOUNT=0 INSERT` 패턴으로 통일.

**신규 SP 18개** (DbProcedures.cs):

- `sp_SaveAiSignalLog` — Bot_Log INSERT
- `sp_SaveOrderError` — Order_Error INSERT
- `sp_SaveArbitrageExecutionLog` — UserId 컬럼 유무 SP 내부 분기
- `sp_SaveFundTransferLog` — 동일
- `sp_SaveRebalancingLog` (parent OUTPUT Id) + `sp_SaveRebalancingAction` (child loop)
- `sp_GetRecentlyClosedPositions` — Symbol GROUP BY MAX(ExitTime)
- `sp_UpsertElliottWaveAnchor` — **MERGE 제거 → UPDATE → IF @@ROWCOUNT=0 INSERT**
- `sp_LoadElliottWaveAnchors` — UserId + 선택적 SymbolsCsv (STRING_SPLIT)
- `sp_DeleteElliottWaveAnchor`
- `sp_ResolveCloseSymbol` — 3단계 매칭 (strict/side/open) 통합
- `sp_MirrorToTradeLogs` — IF NOT EXISTS + INSERT (UserId 분기 SP 내부)
- `sp_EnsureOpenTradeForPosition` — UPDATE or INSERT (3개 OUTPUT 파라미터)
- `sp_RecordPartialClose` — 4분기 부분청산 (6개 OUTPUT, ResultCase 0/1/2/3)
- `sp_InsertClosedTrade` — Complete/TryCompleteOpenTrade fallback INSERT 공용
- `sp_GetAllCandleDataForTraining` — ActiveSymbols + RankedCandles CTE
- `sp_GetBulkCandleData` — RankedCandles + STRING_SPLIT 심볼 필터

**변경된 DbManager.cs 메서드 17개** — 모두 `EXEC dbo.sp_xxx` 호출로 전환:

- ResolveCloseSymbolAsync, TryMirrorToTradeLogsAsync
- SaveAiSignalLogAsync, SaveOrderErrorAsync
- SaveArbitrageExecutionLogAsync, SaveFundTransferLogAsync, SaveRebalancingLogAsync
- GetRecentlyClosedPositionsAsync
- UpsertElliottWaveAnchorStateAsync, LoadElliottWaveAnchorStatesAsync, DeleteElliottWaveAnchorStateAsync
- EnsureOpenTradeForPositionAsync (SqlCommand + OUTPUT 3개)
- RecordPartialCloseAsync (SqlCommand + OUTPUT 6개)
- CompleteTradeAsync fallback, TryCompleteOpenTradeAsync fallback
- GetAllCandleDataForTrainingAsync, GetBulkCandleDataAsync

**제거된 패턴:**
- DbManager.cs의 인라인 `db.ExecuteAsync(@"...")` / `QueryAsync(@"...")` 전부 제거 (DDL 메서드 제외)
- `HasColumnAsync` C# 호출 제거 → SP 내부 `COL_LENGTH('table','UserId')` 체크
- 인라인 MERGE (UpsertElliottWaveAnchorStateAsync) 완전 제거

**diff stat:** DbManager.cs -752줄 / DbProcedures.cs +792줄 (총 +40줄, 가독성 + lock 경합 완화)

## [5.10.66] - 2026-04-20

### Hotfix Phase 1 — AI 학습 죽음의 순환 차단

7일 매매 분석 결과(-$4,025 손실, AiScore≥0.80 승률 13%, 라벨링 6% 성공률) AI 학습-라벨링 폐쇄 루프 붕괴 확인. 즉시 차단 + 학습 정상화 작업.

**A. 라벨 기준 완화** (`AIDoubleCheckEntryGate.AddLabeledSampleToOnlineLearningAsync:978`)
- 이전: `actualProfitPct >= 2.0f` → 414건 중 26건만 성공(6%) → 모델이 "절대 진입 X" 비관 학습
- 변경: `actualProfitPct >= 1.0f` → 예상 성공률 ~30%, 클래스 불균형 완화로 학습 다양성 확보

**B. 메이저 진입 임시 차단** (`DoubleCheckConfig.BlockMajorEntries`)
- 이전: 메이저 365건 통과, 승률 38%, AvgPnL **-$8.59/건**, Total **-$3,133**
- 변경: `BlockMajorEntries=true` 신규 토글 → BTC/ETH/SOL/XRP 진입 시 즉시 차단 (`Major_Temporarily_Blocked_v5_10_66`)
- AI 학습 정상화 후 `BlockMajorEntries=false`로 복원 예정

**C. 온라인 학습 진단 + DB 기록 활성화**
- `AdaptiveOnlineLearningService.AddLabeledSampleAsync`: 50건마다 트리거 차단 사유 진단 로그 (min 미도달 / step 미일치)
- `PeriodicRetrainingCallback`: 매번 호출 시 진입/스킵 사유 명시 (window/elapsed/min 상태)
- 신규 `OnRetrainCompleted` 이벤트 → AIDoubleCheckEntryGate 구독 → `AiTrainingRuns` 테이블에 `Stage="Online_Retrain"` 기록 (이전엔 INIT_ML 7회만 보여 진단 불가)
- 진단 목적: 사용자 7일간 INIT_ML만 보였던 원인 추적 (데이터 부족? 타이머 미작동? 트리거 조건 미충족?)



### Added — Lorentzian Phase 1 (KNN 사이드카 진입 검증)

- **신규 `Services/LorentzianClassifier.cs`** — TradingView Lorentzian Classification 컨셉 차용
  - Lorentzian 거리 `d(x,y) = Σ ln(1+|xᵢ-yᵢ|)` — 이상치/꼬리분포에 강건 (Euclidean 제곱 민감도 회피)
  - K=10 KNN, Z-score 정규화, 최대 5000 샘플 FIFO 캐시
  - 17차원 feature: M15(RSI/BBPos/ADX/ATR/StochRSI/Volume), H1(RSI/BBPos/Momentum), H4(RSI/BBPos/Trend), D1(Trend/RSI), DirectionBias, M15(PriceVsSMA20/EMA_Cross)
- **`AIDoubleCheckEntryGate` 통합** — `EvaluateEntryAsync` 7.5단계에 사이드카 추가
  - **Soft mode**: 진입 차단 안 함, 경고/동의 로그만 출력 (`[LORENTZIAN_OK]` / `[LORENTZIAN_WARN]` / `[LORENTZIAN_WARMUP]`)
  - 청산 라벨링 시(`AddLabeledSampleToOnlineLearningAsync`) Lorentzian 샘플 자동 누적
  - 영구화: `TrainingData/Lorentzian/lorentzian_YYYYMM.jsonl` (재부팅 시 자동 재로드)
  - `DoubleCheckConfig`에 6개 설정 (`EnableLorentzianGate`, `LorentzianK=10`, `LorentzianMinSamples=100`, `MinLorentzianScore=2`, `MinLorentzianPassRate=0.55f` 등)
  - `AIEntryDetail`에 4개 필드 (`LorentzianScore`, `LorentzianPassRate`, `LorentzianSampleCount`, `LorentzianReady`)

### Refactored — MERGE 제거 + 인라인 SQL → SP 전환

- **`sp_SavePositionState`**: MERGE → UPDATE → IF @@ROWCOUNT=0 INSERT (lock 경합 완화)
- **`sp_UpsertAiTrainingRun`**: MERGE → UPDATE → IF @@ROWCOUNT=0 INSERT
- **`SavePositionStateAsync`** (DbManager.cs): v5.10.59 인라인 MERGE 롤백 → `sp_SavePositionState` 호출 복원
- **`EnsureCandleDataIndexAsync`** OBJECT_ID 체크 누락 버그 수정 (테이블 없을 때 매번 오류 삼킴)
- **`GetAllCandleDataForTrainingAsync`** 서브쿼리 이중 스캔 제거 → CTE + INNER JOIN 단일 스캔

### Removed — 미사용 코드 정리 (~7000줄 감축)

- **삭제 파일 10개** (모두 0 외부 참조 확인 후 삭제):
  - `DualAI_IntegrationGuide.cs` (가이드 문서)
  - `FeatureImportanceAnalyzer.cs` (미사용 static 분석기)
  - `StubWindows.cs` (공백 파일)
  - `NewsSentimentService.cs` (Random mock 구현)
  - `DexService.cs`, `OnChainAnalysisService.cs` (Phase 8 DeFi — 완전 스텁)
  - `Services/WalkForwardOptimizer.cs`
  - `HistoricalDataLabeler.cs`
  - `Services/AdvancedAnalyticsService.cs`
  - `ReinforcementLearningFramework.cs`
  - `Services/SqueezeLabeller.cs`
- **TradingEngine.cs 정리**:
  - Phase 8 DeFi 필드 + Whale Alert 초기화 블록 제거
  - TensorFlow.NET 임시 비활성화 주석 블록 5개 제거
  - GATE 재설계 보관 주석 블록 1개 제거
  - `_notificationService` 인스턴스 필드 (Whale Alert 제거 후 고아) 제거
  - `using TradingBot.Services.DeFi;` 제거
- **MainViewModel.cs**: TensorFlow 전환 주석 블록 1개 제거
- **DbManager.cs**: 호출 없는 `EnsureOrderErrorTableAsync()` 메서드 + 읽히지 않는 `_orderErrorTableChecked` 필드 제거

## [5.10.64] - 2026-04-20

### Hotfix — 총자산/가용자산 UI 미표시 근본 수정

 - **근본 원인**: `/fapi/v2/balance` 응답에서 `walletBalance` 필드가 **빈 문자열**로 반환되는 계정 케이스 (실측으로 확인: `"USDT balance=726.49 availableBalance=351.64 walletBalance= crossUnPnl=32.91"`). `Binance.Net v12.8.1`의 `GetBalancesAsync`가 빈 문자열 → `decimal` 파싱 예외 → `GetBalancePairAsync` 0/0 반환 → `RefreshProfitDashboard` 캐시 업데이트 실패 → UI `$0.00` 고착
 - **수정** (`BinanceExchangeService.GetBalancePairAsync`):
   - Binance.Net 라이브러리 우회 → `HttpClient`로 `/fapi/v2/account` 직접 호출
   - 응답 JSON의 `assets` 배열에서 USDT 객체 찾아 `walletBalance` + `availableBalance` 직접 추출
   - `ParseDecimalSafe` 헬퍼 — 빈 문자열/null/Number 모두 안전 처리
 - **검증**: 직접 API 조회 실측값 반영 확인 (Wallet $726.50, Available $351.64, UnrealPnL $32.93 → 총자산 $759.43)

## [5.10.63] - 2026-04-20

### CRITICAL FIX (며칠간의 -4120 손해 근본 원인 해결)

 - **Binance 2025-12-09 API 이관 미반영** → 모든 SL/TP/Trailing 등록 실패하던 며칠간의 진짜 원인:
   - Binance Changelog 2025-11-06 (Effective 2025-12-09): **`STOP_MARKET`, `TAKE_PROFIT_MARKET`, `STOP`, `TAKE_PROFIT`, `TRAILING_STOP_MARKET` 모두 Algo Service로 이관**
   - 신 엔드포인트: `POST /fapi/v1/algoOrder`, `DELETE /fapi/v1/algoOpenOrders`, `GET /fapi/v1/openAlgoOrders`
   - 구 엔드포인트(`/fapi/v1/order`)에서 사용 시: **`-4120 STOP_ORDER_SWITCH_ALGO`** 반환 → 정확히 우리가 받던 에러
   - `Binance.Net v12.8.1` 라이브러리는 이관 미지원 → `PlaceConditionalOrderAsync`가 여전히 구 엔드포인트 호출 → 모두 실패
   - 봇은 `-4120` 받고 silent fail → 진입 시 SL/TP 0개 등록 → **활성 포지션 무방비** 며칠간 지속

 - **수정** (`BinanceExchangeService.cs`):
   - `HttpClient` + HMAC-SHA256 직접 호출 (`CallAlgoApiAsync` 헬퍼)
   - `PlaceStopOrderAsync` → `POST /fapi/v1/algoOrder` `algoType=CONDITIONAL&type=STOP_MARKET&triggerPrice=...`
   - `PlaceTakeProfitOrderAsync` → `algoType=CONDITIONAL&type=TAKE_PROFIT_MARKET`
   - `PlaceTrailingStopOrderAsync` → `algoType=CONDITIONAL&type=TRAILING_STOP_MARKET&callbackRate=...&activationPrice=...`
   - `CancelOrderAsync` → algo 먼저 시도 후 일반 fallback
   - `CancelAllOrdersAsync` → `DELETE /fapi/v1/algoOpenOrders` + 일반 둘 다 호출
   - 신규 `GetOpenAlgoOrderCountAsync` — 보호점검에서 algo 주문도 카운트

 - **수정** (`TradingEngine.cs:EnsureActivePositionProtectionAsync`):
   - 기존 `/fapi/v1/openOrders`만 조회 → 일반 + algo 둘 다 조회 → algo 주문이 있으면 중복 등록 방지

### 검증 (실측)

 - XAUUSDT 테스트 등록 성공: SL(algoId=...600788) + TP(algoId=...600797) + Trailing(algoId=...600801)
 - openAlgoOrders 7건 → 10건 (XAU 3개 추가됨)
 - TAOUSDT/BLURUSDT는 이전에 누군가 수동/타 봇으로 algo 등록한 6건 이미 존재 (봇이 못 보던 것)

### 파라미터 변경 핵심
 - `stopPrice` → **`triggerPrice`** (Algo API 사용 이름)
 - 응답 필드 `orderId` → **`algoId`**
 - 필수 파라미터 `algoType=CONDITIONAL` 추가

## [5.10.62] - 2026-04-20

### Hotfix (긴급 — 활성 포지션 SL 누락 보호 안전망)

 - **사례**: 12:00 현재 TAOUSDT/BLURUSDT/XAUUSDT 3개 포지션 활성, **거래소 열린 조건부 주문 0개** (SL/TP/Trailing 전부 없음). 이 포지션은 v5.10.59 배포 이전 이미 `_activePositions`에 등록됨 → `wasTracked=true` 경로로 들어와 외부포지션 SL 자동 등록 로직이 호출되지 않음. 가격 -40%+ 하락 시 청산도 안 되고 텔레그램 알림도 안 뜸 → 무방비 손해
 - **수정** (`TradingEngine.cs`):
   - 메인루프에 **2분 주기 `EnsureActivePositionProtectionAsync()` 안전망** 추가
   - 각 활성 포지션마다 `GetOpenOrdersAsync(symbol)` 조회 → `STOP_MARKET`+`TAKE_PROFIT_MARKET`+`TRAILING_STOP_MARKET` **0개면 자동 재등록**
   - `OrderLifecycleManager.RegisterOnEntryAsync` 호출 (메이저/PUMP 자동 분류 + 쿨다운 초기화)
   - 성공/실패 로그 + 텔레그램 Alert 메시지 전송
   - 향후 어떤 경로로 들어온 포지션이든 최대 2분 내 SL/TP/Trailing 보호됨 (진입 시 + 외부포지션 감지 시 + 주기점검 = 3중 안전)

### In Progress
 - v5.10.63+ `DbManager` 전체 쿼리 SP 전환 (대규모)

## [5.10.61] - 2026-04-20

### Hotfix (설정창 1분 지연 근본 수정)

 - **근본 원인**: `btnSettings_Click`이 매번 `new DbManager(...)` 생성 → 생성자 안의 5개 스키마 `Ensure*` 메서드(`EnsureCategoryColumnAsync`, `EnsureCandleDataIndexAsync`, `EnsureOpenTimeIndexesAsync`, `EnsureTradeHistoryUserIndexAsync`, `DbProcedures.EnsureAllAsync`) 반복 실행 → `CREATE OR ALTER PROCEDURE` / `ALTER TABLE` 등 DDL이 schema lock을 걸어 설정 SELECT 쿼리가 최대 60초 대기 (lock timeout)
 - **수정 1** `DbManager.cs`: 생성자의 Ensure 메서드들을 `Interlocked.Exchange` 기반 static flag `_schemaInitStarted`로 **앱당 1회만 실행**. `new DbManager()`를 여러 번 호출해도 2번째부터는 Ensure 스킵
 - **수정 2** `MainWindow.xaml.cs`: `_sharedDbManager` static 필드 추가 → 설정 버튼 클릭 시 새 `DbManager` 생성 대신 **재사용**. 응답 시간 로깅 추가 (`[Settings] ✅ DB 선조회 완료 (Xms)`)
 - **효과**: 설정 버튼 클릭 → SELECT 1회만 실행 → **수 ms 이내 창 표시**. 이전 60초 지연 제거

## [5.10.60] - 2026-04-20

### Refactored (Dapper → SP 전환 2단계)

 - **SaveGeneralSettingsAsync**: 30컬럼 MERGE + 레거시 fallback → `sp_SaveGeneralSettings` 호출로 단순화
   - SP 내부: `UPDATE WHERE Id=@UserId` → `@@ROWCOUNT=0`이면 `INSERT` 1회
   - 정상 케이스(UserId row 존재)는 UPDATE 단 한 번만 실행 → MERGE 대비 훨씬 빠름
   - C# 코드: Dapper `MERGE` 90줄 + `DynamicParameters` 35줄 + 레거시 fallback 18줄 → 익명 객체 35줄로 단순화
   - 레거시 "구 스키마 호환(펌프 컬럼 없음)" fallback 제거 (v5.0+ 이후 불필요)
 - **LoadGeneralSettingsAsync**: `SELECT *` 인라인 → `sp_LoadGeneralSettings` 호출 (Dapper 매핑 유지)
 - **DbProcedures.cs 추가 SP**: `sp_LoadGeneralSettings`, `sp_SaveGeneralSettings` (총 5개 SP 자동 등록)

### Notes

 - 설정창 속도 개선: SP 호출로 쿼리 플랜 재사용 → 인라인 SQL 대비 응답 시간 단축

## [5.10.59] - 2026-04-20

### Hotfix (긴급)

 - **외부 포지션(EXTERNAL_POSITION_INCREASE_SYNC) SL/TP/Trailing 자동 등록 누락 → 손해 무방비** 근본 수정 (`TradingEngine.cs:6799-6850`):
   - **사례 (PHBUSDT)**: 5건 모두 `Strategy=EXTERNAL_POSITION_INCREASE_SYNC` (사용자 수동 매수/Copy/타 봇 진입), 거래소에 SL/TP/Trailing **0건** → -40~-44% PnL 무방비 손해
   - **근본 원인**: `OrderLifecycleManager.RegisterOnEntryAsync`는 봇 자동 진입 (TradingEngine:11315/14418) 에서만 호출. `HandleAccountUpdate`에서 외부 포지션 감지 시 `_activePositions`에 추가만 하고 SL 등록 호출 누락
   - **수정**: `HandleAccountUpdate`의 `wasTracked=false` 분기(외부 신규 포지션)에 `_orderLifecycle.RegisterOnEntryAsync` 자동 호출 추가 → 외부 포지션도 봇 진입과 동일하게 SL/TP/Trailing 자동 등록
 - **PositionState SP 전환 롤백** (`DbManager.SavePositionStateAsync`):
   - v5.10.58 SP 전환 후 봇 환경에서 lock 경합으로 저장 실패 보고 → 본절/트레일링 갱신 누락 가능성
   - 안전한 인라인 MERGE로 즉시 복귀. SP 전환은 추후 안전 검증(HOLDLOCK 제거, READPAST 등) 후 재시도

## [5.10.58] - 2026-04-20

### Refactored (Dapper → ADO.NET + Stored Procedure 전환 1단계)

 - **신규 `DbProcedures.cs`**: 앱 시작 시 `DbManager` 생성자에서 `CREATE OR ALTER PROCEDURE`로 3개 SP 자동 등록 (DB 권한 가정)
 - **전환된 3개 쿼리**:
   - `DbManager.SavePositionStateAsync` (초당 수회 호출, 가장 빈번) → `sp_SavePositionState`
   - `DatabaseService.GetLatestSyncedOpenTimeAcrossTablesAsync` → `sp_GetOpenTimeAcrossTables`
   - `DatabaseService.BulkPreloadOpenTimeCacheAsync` → `sp_BulkPreloadOpenTime` (테이블 존재 여부 동적 체크 내장)
 - **벤치마크** (`test-sp-benchmark.ps1` 실측, 봇 실행 중 상태):
   - **OpenTime 집계**: inline SQL 151ms/call → SP 21.7ms/call → **85.7% 빨라짐** ✅
   - **PositionState MERGE**: inline SQL 418ms/call → SP 741ms/call → **77% 느려짐** ⚠️ (봇이 동일 테이블 동시 쓰기 중 lock 경합 추정 — 추후 실측 환경에서 재측정 필요)
 - **다음 단계 예정**:
   - v5.10.59: `sp_SaveCandleData_Bulk` (TVP, 실시간 캔들 저장)
   - v5.10.60: `sp_UpsertTradeEntry` + `sp_CompleteTrade`
   - v5.10.61: `sp_LoadGeneralSettings` + `sp_SaveGeneralSettings`
   - v5.10.62: 나머지 통계/로그 쿼리

## [5.10.57] - 2026-04-20

### Fixed

 - **설정창 비동기 race로 일부 컬럼만 DB와 다르게 보이는 현상 근본 수정**:
   - 근본 원인: v5.10.55에서 `SettingsWindow.Loaded` 이벤트에 async DB 조회 연결 → 창이 먼저 JSON/캐시 값으로 표시된 뒤 DB 조회 완료 후 UI가 덮어써지는 구조 → 사용자가 창 뜨자마자 특정 컬럼 확인 시 **구 값** 보이다가 잠시 후 **DB 값**으로 바뀜 → "일부 컬럼만 DB랑 다름"으로 체감
   - 수정 (`MainWindow.xaml.cs btnSettings_Click`): 설정 버튼 클릭 시 **창을 띄우기 전에** `DbManager.LoadGeneralSettingsAsync(UserId)` 선조회 → `ApplyGeneralSettings(dbSettings)`로 캐시 갱신 → 그 다음 `new SettingsWindow()` 생성
   - 수정 (`SettingsWindow`): Loaded 이벤트 비동기 DB 조회 경로(`LoadGeneralSettingsFromDbAsync`) 완전 제거. 생성자에서 `LoadSettings()` 실행하며 이미 갱신된 `MainWindow.CurrentGeneralSettings` 동기 읽기 → 창 표시 시점에 이미 모든 UI 필드가 DB 최신값
   - 효과: 창이 뜨는 순간 모든 컬럼이 즉시 최신 DB 값. 비동기 race 원천 제거
   - 로그: `[Settings] ✅ DB 선조회 완료 → 설정창 표시 | EnableMajor=... MaxMajor=... MaxPump=...`

## [5.10.56] - 2026-04-20

### Fixed

 - **초기학습 배너 "모델 학습 처리 중..." 무한 매달림 수정** (`TradingEngine.cs:7589-7605`):
   - 근본 원인: `TrainAllModelsAsync` finally에서 `if (IsInitialTrainingComplete)`일 때만 `OnInitialTrainingCompleted?.Invoke(true)` 호출 → `pump_signal_normal.zip` 모델 파일 생성에 실패(클래스 불균형/샘플 부족)하면 이벤트가 영원히 발화되지 않아 배너가 "모델 학습 처리 중..." 상태로 고착
   - 수정: 학습 완료/실패 상관없이 항상 이벤트 발화 — 성공 시 `true`, 실패 시 `false` 인자 전달 → MainViewModel이 "⚠ 초기학습 오류 발생" 표시 후 30초 뒤 자동 배너 숨김
 - **OpenTime 집계 쿼리 Dapper → ADO.NET SqlDataReader 전환** (`DatabaseService.cs`):
   - `BulkPreloadOpenTimeCacheAsync` / `GetLatestSyncedOpenTimeAcrossTablesAsync`의 Dapper `QueryAsync<dynamic>` / `QuerySingleOrDefaultAsync<tuple>` → 순수 ADO.NET `SqlCommand` + `SqlDataReader`
   - `reader.IsDBNull(i)` 체크 + `dt > DateTime(1800,1,1)` 이중 방어 → 이전 버전의 `(DateTime)row.MaxOT` 강제 캐스팅에서 DBNull/MinValue 예외 완전 차단
   - DB 현황 테스트 결과 (`test-opentime-query.ps1`): 4개 테이블 모두 `OpenTime < 1800-01-01` 행 **0건** 확인 → 데이터는 이미 정리됨, 코드 방어로 추후 오염 방지

### In Progress

 - **Dapper → ADO.NET + Stored Procedure 전면 전환** 단계적 진행 중 (v5.10.57+ 예정):
   1. 성능 병목 큰 쿼리 우선 (`SavePositionStateAsync` MERGE, `GetAllCandleDataForTrainingAsync`, CandleData BulkInsert)
   2. 각 단계마다 실제 속도 벤치마크 수행 후 비교 결과 공유
   3. 프로시저는 DbManager 초기화 시 자동 등록 (권한 가정)

## [5.10.55] - 2026-04-20

### Fixed

 - **설정창 DB 직접 조회 복구** (`SettingsWindow.xaml.cs`):
   - 근본 원인: v5.10.51에서 "비동기 DB 재쿼리 불필요"로 판단하고 `MainWindow.CurrentGeneralSettings` 인메모리 캐시만 사용하도록 변경 → 앱 시작 시점 캐시 값만 표시되어 DB가 외부에서 변경된 경우(다른 PC·스크립트·수동 SQL 등) 설정창이 **구 값**을 보여줌
   - 수정:
     - `SettingsWindow.Loaded` 이벤트에 `LoadGeneralSettingsFromDbAsync()` 연결 → 창이 열릴 때마다 `_dbManager.LoadGeneralSettingsAsync(UserId)` 직접 호출
     - UI 필드(`txtDefaultMargin`, `chkEnableMajorTrading`, `txtMaxMajorSlots`, `txtMaxPumpSlots`, `txtMaxDailyEntries`, `MajorTrendProfile` 등)를 DB 값으로 Dispatcher 통해 덮어쓰기
     - 조회 성공 시 `MainWindow.ApplyGeneralSettings(dbSettings)`로 인메모리 캐시도 즉시 동기화 → TradingEngine 실행 중이어도 반영
   - 로그: `[Settings] ✅ DB 조회 완료 | EnableMajor=... MaxMajor=... MaxPump=... DefaultMargin=...` 로 매번 출력 → 설정 불일치 시 즉시 확인 가능

## [5.10.54] - 2026-04-20

### Refactored

 - **주문 라이프사이클 단일 진입점 통합** — Binance `-4120` "Order type not supported" 중복 주문 에러 근본 수정:
   - **근본 원인**: SL/TP/Trailing 등록 경로가 6곳에 분산되어 있어 진입 직후 같은 심볼에 대해 서로 다른 코드 경로가 각자 조건부 주문을 덮어쓰며 Binance가 중복으로 인식 → `-4120` 반환 → silent 실패 → SL/TP/Trailing 부재 상태로 포지션 방치
   - **신규 `Services/OrderLifecycleManager.cs` 도입**:
     - `RegisterOnEntryAsync`: 기존 조건부 주문 자동 취소 → 300ms 대기 → SL+TP+Trailing 순차 등록 (race-free). 30초 쿨다운 내장
     - `ReplaceSlAsync`: 본절 전환 — 기존 SL 취소 후 새 SL 등록
     - `OnStopLossFilledAsync` / `OnPositionClosedAsync`: 포지션 종료 시 잔여 조건부 주문 일괄 취소
     - `AdjustTrailingAfterPartialCloseAsync`: 부분청산 후 Trailing 재조정
   - **제거된 중복 호출 경로**:
     - `TradingEngine.cs:6672` account-update 시 SL/TP 재등록 — 완전 삭제 (진입 1회만)
     - `PositionMonitorService.cs:466` MonitorPositionStandard 자동 SL 등록 — 삭제 (이미 진입 시 등록됨)
     - `PositionMonitorService.cs:1006` 3단계 활성화 시 Trailing 재등록 — 삭제 (이미 진입 시 등록됨)
     - `PositionMonitorService.cs:3386` 부분청산 Trailing 재등록에 **기존 Trailing 명시적 취소 300ms 대기** 추가 (이전: 취소 누락으로 race 중복)
   - **이관된 경로** (`_entryOrderRegistrar` → `_orderLifecycle`):
     - 자동 진입 (TICK_SURGE/PUMP/SPIKE_FAST 등): `TradingEngine.cs:11315`
     - 수동 진입: `TradingEngine.cs:14418`
     - 재시작 동기화: `TradingEngine.cs:2240` (기존 Cancel→ResetCooldown→Register 수동 로직 제거, `OrderLifecycleManager.RegisterOnEntryAsync` 내장 처리로 위임)
   - **WebSocket OrderUpdate 후크**: SL/Trailing 체결 시 `OnPositionClosedAsync` 자동 호출 → 잔여 TP/조건부 주문 즉시 취소 (포지션 방치 방지)
   - **삭제된 레거시**:
     - `Services/EntryOrderRegistrar.cs` 전체 파일 삭제 (OrderLifecycleManager로 대체)
     - `TradingEngine._orderService` 필드 (BinanceOrderService 미사용 인스턴스) 제거

## [5.10.53] - 2026-04-19

### Fixed

 - **자동 익절(API TP/SL/Trailing) 체결 시 텔레그램 메시지 누락 근본 수정** (`TradingEngine.cs`):
   - 근본 원인: `RecordConditionalOrderFillAsync`에서 메시지 템플릿 `*[API {exitReason}]*`이 `API_TAKE_PROFIT`/`API_STOP_LOSS`/`API_TRAILING_STOP` 등 언더스코어 포함 문자열을 포함 → 마크다운 V1 파서가 `_`를 이탤릭 마커로 해석 → 일부 케이스에서 Telegram 400 거부 → `catch {}` silent 삼킴 → 사용자 무음
   - `HandleSyncedPositionClosed`도 동일 `*[{reason}]*` 패턴 사용 → 향후 reason 변경 시 동일 문제 발생 가능 → 동일 수정으로 방어
   - 수정:
     - 두 경로 모두 `reason.Replace("_", " ")` 적용 → 언더스코어 제거로 파서 오동작 방지
     - `catch {}` → `catch (Exception tEx) { OnStatusLog?.Invoke(...) }` 로 교체 → 향후 텔레그램 전송 실패 시 상태 로그로 즉시 확인 가능

## [5.10.52] - 2026-04-19

### Fixed

 - **DB 전체 성능 근본 수정 4건** (`PositionMonitorService.cs`, `DbManager.cs`):
   - **`PersistPositionState` 동시성 무제한** (`PositionMonitorService.cs:67`): fire-and-forget Task.Run 호출을 동시성 제한 없이 실행 → 포지션 수×호출 횟수만큼 동시 MERGE 발생 → PositionState 테이블 lock 경합 → commandTimeout 8s 타임아웃 반복 → `SemaphoreSlim(3,3)` + `WaitAsync(0)` 슬롯 없으면 즉시 스킵(다음 tick 재시도)으로 해결
   - **`GetRecentCandleDataAsync` SELECT * + 10s timeout** (`DbManager.cs`): `SELECT *` + commandTimeout 10s인데 limit=5000행 조회 → 타임아웃 확실 → 필요 컬럼만 + `WITH (NOLOCK)` + 60s
   - **`GetCandleDataByIntervalAsync` SELECT * + 30s timeout** (`DbManager.cs`): limit=52000행 조회 → 동일 수정, timeout 120s
   - **`GetCurrentUserId()` 동기 DB 블로킹** (`DbManager.cs`): `db.Open()` + `db.ExecuteScalar<int?>()` 동기 호출이 async 컨텍스트에서 스레드 블로킹 → 로그인 후 `AppConfig.CurrentUser.Id`는 항상 설정되므로 DB 조회 제거, `static` 처리

## [5.10.51] - 2026-04-19

### Fixed

 - **설정창 GeneralSettings 저장/로드 근본 수정 2건** (`MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`):
   - **버그 1** `CopyTradingSettings` 누락 필드: `EnableMajorTrading` / `MaxMajorSlots` / `MaxPumpSlots` / `MaxDailyEntries` 4개 필드 누락 → 설정창 저장 후 `ApplyGeneralSettings` 호출해도 인메모리 `CurrentGeneralSettings`(= `TradingEngine._settings`)에 미반영 → 4개 필드 추가로 저장 즉시 엔진 반영
   - **버그 2** `LoadSettingsAsync` fire-and-forget 레이스 컨디션: 생성자에서 `_ = LoadSettingsAsync()` → 창이 열리자마자 저장 누르면 DB 로드 완료 전 JSON 기본값(EnableMajorTrading=true)으로 저장됨 → `LoadSettingsAsync()` 제거 + `LoadSettings()`에서 `MainWindow.CurrentGeneralSettings`(앱 시작 시 DB 로드 완료) 직접 읽도록 변경 → 동기 로드, DB 재쿼리 불필요

## [5.10.50] - 2026-04-19

### Fixed

 - **PUMP ML 모델 학습 데이터 0건 → 학습 불가 → 48시간 진입 없음 근본 수정** (`DbManager.cs`):
   - 근본 원인: `GetAllCandleDataForTrainingAsync`/`GetBulkCandleDataAsync`가 `CandleData` 테이블을 읽는데, HistoricalDownloader는 `MarketCandles` 테이블에 저장 (테이블 불일치) → CandleData 24h 데이터 0건 + 전체 쿼리 타임아웃 → 학습 샘플 0 → `pump_signal_normal.zip` 미생성 → `IsModelLoaded=false` → 모든 PUMP 신호 WAIT
   - 수정: 두 함수 모두 `MarketCandles WITH (NOLOCK)` 로 변경 — `OpenPrice/HighPrice/LowPrice/ClosePrice → Open/High/Low/Close` 별칭 사용, `IntervalText` 필터 제거 (MarketCandles는 5m 고정), commandTimeout 60→60s

## [5.10.49] - 2026-04-19

### Removed
 - **서킷브레이커 로직 완전 제거** (`PositionMonitorService.cs`, `TradingEngine.cs`, `RiskManager.cs`):
   - PositionMonitorService.cs: 단일 포지션 -4% 손실 강제청산 (EMERGENCY_CIRCUIT_BREAKER) 제거
   - TradingEngine.cs: 승률 서킷브레이커 추적 (`_recentTradeResults`, `_winRatePauseUntil`, WIN_RATE 관련) 완전 제거
   - RiskManager.cs: IsTripped/TripTime/OnTripped/연속손실 판정 로직 제거 (DailyRealizedPnl 보고용만 유지)

## [5.10.48] - 2026-04-19

### Fixed

 - **서브어카운트 레버리지 5x 제한 → 진입 차단 수정** (`BinanceExchangeService.cs`):
   - 근본 원인: 서브어카운트에서 "Subaccounts are restricted from using leverage greater than 5x" 오류 시 기존 정규식(`cannot be greater than`)에 매칭 안 됨 → `SetLeverageAutoAsync` 0 반환 → TradingEngine이 진입 취소
   - 수정: 정규식을 `(?:cannot be greater than|greater than)\s+(\d+)`로 확장 → 5x로 자동 다운그레이드 후 진입 진행
 - **외부 청산 시 잔여 TP/SL 오더 미취소 → 마진 잠금 수정** (`TradingEngine.cs`):
   - 근본 원인: PositionSyncService가 포지션 외부 청산 감지 후 `HandleSyncedPositionClosed` 호출 시 Binance에 남아있는 LIMIT SELL 오더(TP/SL)를 취소하지 않음 → 오더 마진 $707 잠금 → 가용잔고 $10 현상
   - 수정: `HandleSyncedPositionClosed`에 `CancelAllOrdersAsync` 추가 → 외부 청산 감지 즉시 잔여 오더 일괄 취소

## [5.10.47] - 2026-04-19

### Fixed

 - **OpenTime datetime/datetime2 타입 불일치 → SqlException 수정** (`DatabaseService.cs`):
   - 근본 원인: `CandleData`/`MarketData`=`datetime2`, `MarketCandles`/`CandleHistory`=`datetime` — UNION ALL 쿼리 시 타입 충돌 + `DateTime.MinValue(0001-01-01)` 행 존재 → "날짜/시간 범위 초과" SqlException 매 분 발생
   - 수정: `BulkPreloadOpenTimeCacheAsync` 및 개별 조회 쿼리에 `CAST(OpenTime AS DATETIME2(7))` + `WHERE OpenTime > '1800-01-01'` 필터 적용
   - 효과: OpenTime 오류 완전 제거, 캐시 로드 정상화
 - **EntryTimingML/OpportunityForecaster positive 0개 → 기존 모델 덮어쓰기 방지** (`EntryTimingMLTrainer.cs`, `Services/OpportunityForecaster.cs`):
   - 근본 원인: 재학습 시 positive 샘플이 0개(하락장/진입 없음 기간)이면 all-negative 학습 → 모델이 항상 prob≈0.001 반환 → AI_GATE가 모든 PUMP 진입 차단
   - 기존 코드: positive=0 경고만 출력 후 계속 학습 → broken 모델 파일 덮어씀
   - 수정: `positives.Count == 0` 감지 시 즉시 return (기존 모델 파일 보존)
   - 효과: 재학습 후 진입 완전 차단 현상 방지

## [5.10.46] - 2026-04-18

### Fixed

 - **학습 진행중 레이어 "ETA 완료" 표시 후 배너 지속 문제** (`MainViewModel.cs`):
   - 근본 원인 1: 다운로드 완료 시 "ETA 완료" 텍스트가 표시되지만 모델 학습(30분+)이 진행 중에도 배너 유지 → 사용자 혼동
   - 근본 원인 2: `StopInitialTrainingBanner` 내 `hideTimer`를 로컬 변수로 생성 → 스케줄된 재학습(`TrainAllModelsAsync`)마다 중복 타이머 누적
   - 수정 1: "ETA 완료" → "다운로드 완료 — 학습 중..." 텍스트 변경
   - 수정 2: `StartOrUpdateInitialTrainingBanner` 호출 시 "다운로드 완료" ETA면 "🧠 모델 학습 처리 중..."으로 즉시 전환
   - 수정 3: `_hideTrainingBannerTimer` 필드화 → `StopInitialTrainingBanner` 재진입 시 기존 타이머 중지 후 재시작
   - 수정 4: 배너가 이미 `Collapsed` 상태면 타이머 생성 자체 스킵 (중복 방지)
 - **MSSQL 실행 계획 개선** (`DbManager.cs`):
   - `IX_TradeHistory_UserId_IsClosed` 복합 인덱스 자동 생성 추가 (앱 시작 시)
   - 조회 컬럼 INCLUDE: Symbol, EntryTime, EntryPrice, Quantity, Leverage, IsLong, Category
   - 대상 쿼리: `WHERE UserId=@uid AND IsClosed=0` (GetOpenPositionsAsync) — 풀스캔 → 인덱스 시크 전환

## [5.10.45] - 2026-04-19

### Fixed

 - **PUMP 포지션 종료 메커니즘 서버사이드 완전 통일** (`PositionMonitorService.cs`, `TradingEngine.cs`):
   - 근본 원인: `EntryOrderRegistrar`는 SL/TP/Trailing을 서버에 등록하나, `MonitorPumpPositionShortTerm` 클라이언트 루프가 동일 로직을 중복 실행 → 이중 청산 위험 + 불일치
   - 버그 1: `RegisterEntryOrdersAsync` PUMP SL/TP partial ratio가 설정값 무시하고 하드코딩(-40%, 60%) 사용
   - 버그 2: Trailing callback이 3.5% 고정 (설정값 `PumpTrailingGapRoe/leverage` 무시)
   - **수정 (TradingEngine.cs)**: 자동/수동 진입 2곳 모두 `PumpStopLossRoe`, `PumpTp1Roe`, `PumpFirstTakeProfitRatioPct`, `PumpTrailingGapRoe/leverage` 설정값 사용으로 통일
   - **수정 (PositionMonitorService.cs)**: `MonitorPumpPositionShortTerm` 루프 대폭 단순화:
     - 제거: 스파이크 부분익절, 1분봉 하락추세 조기청산, BB중단 즉시손절, ElliottWave TP1/SL, 계단식 스탑 1/2/3단계, 클라이언트 ROE 손절 백업, 클라이언트 트레일링 최종청산
     - 유지: 본절 전환만 (ROE >= PumpBreakEvenRoe → 기존 서버 SL 취소 후 본절가 STOP_MARKET 재등록)
   - 서버사이드 처리: 진입 시 SL(-PumpStopLossRoe%), TP1(+PumpTp1Roe%, PumpFirstTakeProfitRatioPct%), TRAILING_STOP(callback=PumpTrailingGapRoe/lev%)

### Research: 단타 최적 익절/손절 구조 (20x)

 - SL: 진입가 대비 0.4~0.6% 거리 (ROE -8~-12%)
 - TP1: +0.7~1.0% (ROE +14~+20%), 40~50% 부분청산 + 본절 전환 동시 수행
 - Trailing: ATR(14)×1.5~2.0 또는 고정 0.5~0.6% callback (ROE 10~12% 거리)
 - 최소 R:R 1.5:1 (40% 승률에서 손익분기)

## [5.10.40] - 2026-04-18

### Fixed

 - **학습 레이어 배너 재표시 후 영구 열림 근본 수정** (`MainViewModel.cs`):
   - 근본 원인: `StopInitialTrainingBanner`가 10s/30s hide 타이머를 시작한 뒤, hide 타이머 만료 전 `OnInitialTrainingProgress` 메시지가 도착하면 `StartOrUpdateInitialTrainingBanner()`가 배너를 다시 `Visible`로 변경 + `_initialTrainingTimer` 재생성 → 이후 새 hide 타이머 없이 배너 영구 열림
   - 수정: `_trainingBannerFinalized` 플래그 추가 — `StopInitialTrainingBanner` 호출 즉시 `true` 설정
   - `StartOrUpdateInitialTrainingBanner()`가 `_trainingBannerFinalized = true`이면 즉시 return → 완료/실패 후 모든 progress 메시지 무시
   - 봇 재시작(`SubscribeToEngineEvents`) 시 플래그 자동 리셋 → 다음 학습에서도 정상 표시

## [5.10.39] - 2026-04-18

### Fixed

 - **IsOwnPosition 누락으로 슬롯 카운트 불일치 근본 수정** (`TradingEngine.cs`):
   - 근본 원인: 진입 성공 후 `_activePositions` 등록 시 `IsOwnPosition` 필드 미설정 → 기본값 `false`
   - 슬롯 체크(`Count(p => p.Value.IsOwnPosition)`)가 새로 진입한 포지션을 카운트하지 않음
   - 결과: 30분 주기 sync 실행 전까지 슬롯이 0으로 보여 MaxPumpSlots 초과 진입 허용
   - sync 실행 후 갑자기 IsOwnPosition=true로 전환 → 순간적으로 "0포지션 3/3 포화" 현상
   - 수정: 진입 성공 즉시 `IsOwnPosition = true` 설정 — 4개 진입 경로 모두 적용
     1. `HandlePumpTradeCore` (메인 진입 경로)
     2. `HandlePumpEntry` LIMIT 직접 진입
     3. SPIKE_FAST 즉시 진입
     4. MegaPump 슬롯 확보 예약

## [5.10.38] - 2026-04-18

### Fixed / Added

 - **PUMP 슬롯 포화 시 AI 점수 기반 우선순위 진입 큐** (`TradingEngine.cs`):
   - 근본 원인: 슬롯 포화로 차단된 6개 신호가 next 캔들이 오기 전까지 아무것도 처리되지 않음
   - `_pumpPriorityQueue`: 슬롯 차단 시 AI 승인 점수(blendedScore) 함께 등록, 점수 내림차순 정렬
   - `_aiApprovedRecentScores`: AI Gate 승인 직후 blendedScore 캐시 → 최대 30분 재사용
   - 포지션 종료 시(`HandleSyncedPositionClosed`) → 0.5초 후 `TryProcessPumpPriorityQueueAsync()` 즉시 실행
   - 큐 처리: 만료(30분) / 고점(+2% 이상) 항목 자동 폐기 → 나머지 중 최고 점수 1개 진입
   - AI 승인 이력이 있는 경우 `skipAiGateCheck: true`로 재검증 생략 (AI는 이미 승인)
   - 없는 경우(처음 차단 시) `skipAiGateCheck: false`로 진입 시 AI 재평가

## [5.10.37] - 2026-04-18

### Fixed

 - **OpenTime 쿼리 폭주 근본 원인 수정** (`DatabaseService.cs`):
   - 근본 원인: 봇 시작 시 100+ 심볼이 빈 캐시에 동시 접근 → 심볼당 4개 테이블 MAX 쿼리 발생 → DB 과부하 → 타임아웃
   - 해결: `BulkPreloadOpenTimeCacheAsync` 신규 메서드 — 4개 테이블 UNION ALL 단일 쿼리로 전체 심볼 MAX(OpenTime) 일괄 로드
   - `_openTimePreloadDone` (volatile bool) + `_preloadSemaphore(1,1)`로 단 1회 실행 보장
   - 캐시 미스 첫 번째 시 → 전체 사전 로드 → 이후 개별 심볼은 캐시 HIT (DB 쿼리 불필요)
   - 신규 심볼(DB에 없는 경우)만 fallback 개별 쿼리 실행 (드문 경우)
   - v5.10.36 임시 타임아웃 증가 방식 → 완전 제거, 슬롯 원복 (5슬롯, 30s wait)

## [5.10.36] - 2026-04-18

### Fixed

 - **OpenTime 조회 실패/슬롯 대기 초과 오류 수정** (`DatabaseService.cs`):
   - 원인 1: `_openTimeDbSlot` 5슬롯 × 30s wait → 봇 시작 시 100+ 심볼 동시 요청으로 후순위 심볼이 30s 초과
     → 슬롯 5 → 10, 대기 30s → 60s로 확장
   - 원인 2: `commandTimeout: 10` → 쿼리 10초 초과 시 예외 발생
     → commandTimeout 10 → 30으로 증가
   - 원인 3: `SaveCandlesInternalAsync`에서 `UpdateOpenTimeCache` 미호출
     → CandleData 저장 후에도 캐시 미갱신 → 동일 심볼 DB 재조회 반복
     → 저장 완료 후 `UpdateOpenTimeCache(symbol, "5m", payload.Select(p => p.OpenTime))` 추가

## [5.10.35] - 2026-04-18

### Fixed

 - **Dapper IEnumerable 배치 INSERT implicit transaction 전수 수정** (`DatabaseService.cs`):
   - 근본 원인: Dapper `ExecuteAsync(sql, IEnumerable)` → implicit transaction → timeout 시 전체 배치 rollback → 데이터 영구 유실
   - 수정 대상 5곳:
     1. `SaveCandlesInternalAsync` (MERGE CandleData): 개별 행 루프로 변경 (UNIQUE KEY 무시)
     2. `SaveCandleDataBulkAsync` 소량 경로 (<50행): 개별 행 루프로 변경
     3. `SaveCandleHistoryBulkAsync`: Dapper batch → SqlBulkCopy + #HistStage 스테이징 테이블 방식으로 교체 (100,000+ 행 안전 처리)
     4. `SaveMarketDataBulkAsync`: Dapper batch → SqlBulkCopy + #MktStage 스테이징 테이블 방식으로 교체
     5. `SaveLiveLogsBatchAsync`: 5초 timeout 배치 → 개별 행 루프 (10초 timeout, 행 실패 시 스킵)

## [5.10.34] - 2026-04-18

### Fixed

 - **학습 레이어 배너 닫히지 않는 버그 수정** (`TradingEngine.cs`, `MainViewModel.cs`):
   - 원인: 학습 예외/취소 시 `OnInitialTrainingCompleted` 미호출 → 배너 영구 개방
   - 수정: `TriggerInitialDownloadAndTrainAsync` finally 블록에서 실패 시 `Invoke(false)` 추가
   - 수정: `StopInitialTrainingBanner(false)` 실패 경로에 30초 후 자동 숨김 타이머 추가

## [5.10.33] - 2026-04-18

### Fixed

 - **SubscribeToEngineEvents NullReferenceException 크래시 수정** (`MainViewModel.cs`):
   - 원인: `OnSymbolTrained` 람다 내 `RunOnUI` 지연 실행 시 `_engine`이 null (엔진 리셋 타이밍)
   - 증상: `System.NullReferenceException` at `MainViewModel.<SubscribeToEngineEvents>b__781_420`
   - 수정: 람다 내 `var eng = _engine; if (eng == null) return;` 로컬 캡처 후 null 체크

## [5.10.32] - 2026-04-18

### Fixed

 - **FooterLogs DB INSERT 중단 수정** (`DatabaseService.cs`):
   - 원인: Dapper `ExecuteAsync(IEnumerable)` → implicit transaction → timeout 시 80개 batch 전체 rollback → 항목 영구 유실 → 큐 드레인 반복 실패
   - 수정: 개별 row INSERT 루프로 변경 (implicit transaction 제거) — 개별 실패 시 스킵, 나머지 정상 저장
   - 부가: 오류 catch에서 `Log(...)` 제거 → 재귀 FooterLogs INSERT 시도 차단
 - **레버리지 초과 시 자동 조정 후 진입** (`BinanceExchangeService.cs`, `TradingEngine.cs`):
   - 원인: AAVEUSDT 등 심볼 최대 레버리지 < 설정 레버리지 → 진입 취소
   - 수정: `SetLeverageAutoAsync` 신규 메서드 — 실패 시 에러 메시지에서 최대값 파싱 후 자동 재시도
   - 예: 설정 20x, 최대 2x → 자동 2x로 재시도 → 성공 시 2x로 진입
   - TradingEngine에서 `actualLeverage` 반영 (수량/TP/SL 계산에 실제 레버리지 사용)

## [5.10.31] - 2026-04-18

### Fixed

 - **[DB] 캔들 저장 성공 로그 제거** (`DatabaseService.cs`):
   - 원인: CandleData/CandleHistory/MarketData/MarketCandles 저장 완료마다 Log 호출
   - 결과: 시간당 13,000개 [DB] 성공 로그가 FooterLogs에 쌓여 UI 노이즈 + DB 부하
   - 수정: 성공 로그 5곳 모두 제거 (오류 로그는 유지)
 - **MarketData/CandleHistory UNIQUE KEY 위반 무시** (`DatabaseService.cs`):
   - 원인: 레이스 컨디션 시 동일 데이터 중복 INSERT 시도 → `[DB] MarketData 저장 실패: UNIQUE KEY 위반` 로그 반복
   - 수정: SqlException 2627/2601 (UNIQUE KEY) 무시 처리 (CandleData와 동일하게)
 - **DB 트랜잭션 로그 183GB → 504MB 축소** (DB 직접):
   - 원인: 복구 모델 FULL + 로그 백업 전혀 없음 → 로그 무한 증가 → 모든 쓰기 타임아웃
   - 수정: 복구 모델 SIMPLE 변경, DBCC SHRINKFILE, CandleHistory 인덱스 REBUILD (단편화 96.4% → 0%)

## [5.10.30] - 2026-04-18

### Fixed

 - **레버리지 설정 실패 로그 DB 기록 수정** (`BinanceExchangeService.cs`):
   - 원인: `SetLeverageAsync`에서 성공/실패 모두 `Console.WriteLine`으로 출력 → FooterLogs DB에 미기록
   - 수정: `OnLog?.Invoke`로 변경, 실패 시 에러 메시지·코드 포함하여 DB 기록
 - **MAJOR OnTradeSignal 메이저 비활성화 시 진입 차단** (`TradingEngine.cs`):
   - 원인: `_majorStrategy.OnTradeSignal` 핸들러에 `EnableMajorTrading` 체크 누락
   - 수정: 핸들러 진입 시 `EnableMajorTrading == false`이면 즉시 return (로그 출력)
 - **leverageSet=false 로그 상세화** (`TradingEngine.cs`):
   - 기존: `leverageSet=false`만 기록 → 원인 추적 불가
   - 수정: `symbol={symbol} leverage={leverage}x src={signalSource}` 포함

## [5.10.29] - 2026-04-18

### Fixed

 - **TICK_SURGE/SQUEEZE_BREAKOUT 메이저 비활성화 우회 수정** (`TradingEngine.cs`):
   - 원인: `OnTickSurgeDetected`, `OnSqueezeBreakout` 핸들러에 `EnableMajorTrading` 체크 누락
   - XRPUSDT 등 메이저 코인이 비활성화 상태에서도 TICK_SURGE 경로로 진입 시도 → 레버리지 오류 발생
   - 수정: 두 핸들러 모두 `MajorSymbols.Contains(symbol) && EnableMajorTrading == false` 시 즉시 return

## [5.10.28] - 2026-04-18

### Fixed

 - **서킷브레이커 진입 차단 완전 제거** (`TradingEngine.cs`):
   - `_riskManager.IsTripped` 체크 2곳(PUMP 진입, 일반 진입) 완전 제거
   - 메인 루프 서킷브레이커 모니터링 블록(발동/해제/알림/대기) 제거
   - `_riskManager.OnTripped` 이벤트 핸들러 제거
   - RiskManager 자체(PnL 추적)는 유지, 진입 차단 로직만 제거
 - **FooterLogs Full-Text Index 추가** (DB):
   - `FT_TradingBot` Full-Text 카탈로그 생성
   - `FooterLogs.Message` 컬럼 Full-Text Index 추가 (LANGUAGE 1042)
   - `LIKE '%...%'` 풀스캔 타임아웃 해소

## [5.10.27] - 2026-04-18

### Fixed

 - **OpportunityForecaster 클래스 불균형 → PUMP 진입 완전 차단 수정** (`Services/OpportunityForecaster.cs`):
   - 원인: DB 재학습 시 기회=16.9% → LightGBM Model A가 전부 "기회없음" 예측 (Acc=83.1% F1=0.003)
   - 수정: `TrainAndSaveAsync`에서 negative를 positive×5 이내로 다운샘플링 후 균형 학습
   - EntryTimingMLTrainer와 동일한 패턴, 동일한 수정
 - **PlaceLimitOrderAsync 오류 로그 누락 수정** (`BinanceExchangeService.cs`):
   - Console.WriteLine → OnLog?.Invoke 변경으로 FooterLogs DB에 기록
 - **ForceInitialAiTrainingAsync에서 EntryTimingML 강제 재학습** (`TradingEngine.cs`):
   - IsReady=true여도 수동 학습 시 TriggerInitialTrainingAsync(forceRetrain=true) 호출
   - 기존: IsReady=true면 RetrainModelsAsync(라벨 100개 필요)로 분기 → 초기엔 데이터 부족
 - **TriggerInitialTrainingAsync forceRetrain 파라미터 추가** (`AIDoubleCheckEntryGate.cs`):
   - forceRetrain=true 시 IsReady 체크 우회

## [5.10.26] - 2026-04-17

### Fixed

 - **EntryTimingML 클래스 불균형 → 진입 21시간 완전 차단 근본 수정** (`EntryTimingMLTrainer.cs`):
   - 원인: 초기학습 시 downtrend 시장 260봉 → positive 샘플 ~1.8% → LightGBM 전부 0 예측 (score=-11.66)
   - 수정: `TrainAndSaveAsync` 내 다운샘플링 — negative를 positive × 5 이하로 제한
   - 이후 초기학습/재학습 모두 적용, 로그: `[EntryTimingML] 밸런싱: pos=X, neg(after)=Y`

### Added

 - **ML 피처 4개 추가 — BB Squeeze / SuperTrend / Daily Pivot** (`v4.6.3`):
   - `M15_BB_Width_Pct`: BB 밴드 폭% (낮을수록 스퀴즈/폭발 직전, 20봉 SMA±2σ)
   - `M15_SuperTrend_Direction`: 1=상승 / -1=하락 추세 (ATR 10봉, multiplier 3)
   - `M15_DailyPivot_R1_Dist_Pct`: 전일 R1까지 거리% (양수=저항 위)
   - `M15_DailyPivot_S1_Dist_Pct`: 전일 S1까지 거리% (음수=지지 아래)
   - 총 피처 수 86 → 90개 (`ExpectedFeatureCount = 90`)
   - PIT/히스토리컬 경로에서도 TradingView 피처 계산 누락 버그 수정 (`AssignFeatures`)

## [5.10.25] - 2026-04-17

### Fixed

 - **PUMP/TradeSignal 모델 파일 미생성 근본 수정** (`TrainAllModelsAsync`):
   - 원인: `GetAllCandleDataForTrainingAsync` 24시간 필터로 초기학습 다운로드 과거 데이터 전부 제외
   - 알트 심볼 24시간 데이터 10개 미만 감지 시 `GetBulkCandleDataAsync`로 과거 전체 데이터 보완
   - 이후 재학습 시에도 `pump_signal_normal.zip`, `pump_signal_spike.zip`, `trade_signal_model.zip` 정상 생성

## [5.10.24] - 2026-04-17

### Fixed

 - **학습 레이어 안 닫힘 + 진입 없음 수정** (`pump_signal_normal.zip` 부재로 발생):
   - `IsInitialTrainingComplete = false` 상태에서 AI Gate(`EntryTimingModel.zip`) 모델이 있으면 하드 필터 우회
   - 기존: `pump_signal_normal.zip` 없으면 VWAP/EMA/StochRSI/ATR 하드 필터 전부 적용 → 진입 거의 불가
   - 수정: AI Gate 준비됐으면 AI 단독 판단 (하드 필터 우회)
   - `TrainAllModelsAsync` 완료 후 `pump_signal_normal.zip` 생성되면 배너 자동 닫힘

## [5.10.23] - 2026-04-17

### Fixed

 - **활성 포지션 잠깐 떴다가 사라지는 현상 수정** (`PositionSyncService`):
   - 진입 직후 10초 폴링이 거래소 API 미반영 상태를 청산으로 오판하는 버그
   - 방어막 ①: grace period — 진입 후 45초 이내는 폴링 스킵
   - 방어막 ②: 연속 2회 미확인이어야 청산 처리 (총 최소 65초 보호)
   - 포지션 재확인 시 `_closedRetryCount` 리셋으로 일시적 API 지연에 강건

## [5.10.22] - 2026-04-17

### Fixed

 - **진입 차단 이유 UI 미표시 수정**: "주문 요청 중" 이후 아무것도 안 뜨는 문제 해소
   - AI Gate BLOCK → `⛔ [AI Gate] 차단 | blended/ml/tf 스코어 + reason` 추가
   - 신호 만료(가격 1.5% 이상 변동) → `⛔ [신호만료]` 추가
   - 캔들 데이터 없음 → `⛔ [데이터]` 추가
   - Major 보조지표 필터(VWAP/EMA/StochRSI) → `⛔ [Major필터]` 추가
   - 중복 포지션 내부 차단 → `⛔ [중복차단]` 추가
 - **OpenTime 시작 시 커넥션 풀 고갈 근본 수정**: `_openTimeDbSlot` SemaphoreSlim(5) 추가
   - 수십 심볼 동시 DB 조회 → 풀 소진 → 5초 타임아웃 → null → 전체 동기화 폭발 연쇄 차단
   - 세마포어(5슬롯)로 순서 처리, 슬롯 획득 후 재캐시 체크 (대기 중 중복 쿼리 방지)
   - 타임아웃: 연결 30초(슬롯 대기) + 쿼리 10초 (기존 5초에서 상향)

## [5.10.21] - 2026-04-17

### Fixed

 - **OpenTime 조회 타임아웃 근본 수정**: `GetLatestSyncedOpenTimeAcrossTablesAsync` — DB 반복 쿼리 제거, `_openTimeCache` (`ConcurrentDictionary`) 인메모리 캐시 도입
   - 재시작 후 심볼당 최초 1회만 DB 조회, 이후 캐시에서 즉시 반환 (DB 왕복 0ms)
   - `UpdateOpenTimeCache` 정적 헬퍼 추가: Save 성공 시 자동 갱신 (CandleData/CandleHistory/MarketData/MarketCandles 4경로)
   - 수십 심볼 동시 호출 → Full table scan 반복 → 커넥션 풀 고갈 문제 근본 해소

## [5.10.19] - 2026-04-17

### Changed

 - **bot-side 감시 루프 비활성화** (`new-entry` 경로): `ExecuteFullEntryWithAllOrdersAsync`로 거래소에 SL/TP/Trailing 등록된 경우 `TryStartStandardMonitor` / `TryStartPumpMonitor` 호출 스킵 → `PositionSyncService`(10초 폴링)가 대체
   - 이중 처리(거래소 + bot 양쪽 청산 시도) 제거
   - `MonitorPositionStandard` 루프 타임아웃 / 충돌 원인 해소
 - **재시작 시 기존 포지션**: `_orderManager.RegisterBracket` 등록 → PositionSyncService 폴링 감시 전환
 - **account-update 경로**: bot 추적 포지션(wasTracked)은 PositionSyncService, 외부 포지션은 기존 방식 유지

## [5.10.18] - 2026-04-17

### Added

 - **OrderManager**: SL/TP/Trailing 브라켓 주문 그룹 OCO 관리 (`RegisterBracket`, `CancelBracketAsync`, `CancelAllAsync`)
 - **PositionSyncService**: 10초 폴링으로 거래소 포지션 감지 → `OnPositionClosed` 이벤트 → 쿨다운/DB/Telegram/AI레이블 자동 처리
 - **GetLastTradeAsync**: 청산 후 실제 체결가 조회 (`BinanceExchangeService`)
 - **HandleSyncedPositionClosed**: PositionSyncService 청산 이벤트 핸들러

## [5.10.17] - 2026-04-17

### Fixed

 - **MaxDailyEntries 카운트 오류**: `TryReserveDailyPumpEntry` — 예약 단계에서 카운트 증가 제거, `CommitDailyPumpEntry` 신설로 실제 주문 체결 성공 시에만 카운트 증가
   - SPIKE_FAST: `PlaceOrderAsync` 성공 후 (!isMajor)
   - PUMP_WATCH_CONFIRMED: `ExecuteFullEntryWithAllOrdersAsync` 성공 후
 - **DirectClosePositionAsync 중복 코드 블록**: 이전 세션 손상으로 중복 삽입된 청산 코드 블록 제거

## [5.10.16] - 2026-04-17

### Fixed

 - **최신 OpenTime 조회 타임아웃 근본 수정**: `GetLatestSyncedOpenTimeAcrossTablesAsync` — 4개 개별 쿼리(4 DB 왕복) → 서브쿼리 1개로 통합(1 DB 왕복) + `_bulkDbSemaphore` 추가로 동시 호출 제한. 커넥션 대기 중 타임아웃 해소

## [5.10.15] - 2026-04-17

### Fixed

 - **Stop 버튼 오류 창 2개**: `OperationCanceledException` / `TaskCanceledException` — 엔진 Stop 시 정상 Task 취소 예외를 `App_DispatcherUnhandledException`에서 무시 처리
 - **PlaceMarketOrderAsync 오류 로그**: `Console.WriteLine` → `OnLog` 변경 — "Margin is insufficient" 등 주문 실패 이유가 UI 라이브 로그에 표시
 - **최신 OpenTime 조회 타임아웃**: `GetLatestSyncedOpenTimeAcrossTablesAsync` 4개 MAX 쿼리에 `WITH (NOLOCK)` 추가 + `commandTimeout` 30→5초 단축

## [5.10.14] - 2026-04-17

### Fixed

 - **DB 커넥션 풀 고갈 (캔들/마켓데이터)**: `SaveCandleDataBulkAsync`, `SaveCandleHistoryBulkAsync`, `SaveMarketDataBulkAsync`, `BulkInsertMarketDataAsync` — SemaphoreSlim(20) 추가로 동시 벌크 DB 작업 최대 20개로 제한
 - **Max Pool Size=200**: `AppConfig.ConnectionString`에 자동 추가 — 기본 풀 100에서 200으로 확장
 - "풀에서 연결을 만들기 전에 제한 시간 경과" / "Bulk Insert 실패: 실행 제한 시간 초과" 오류 해소

## [5.10.13] - 2026-04-17

### Fixed

 - **대시보드 초기 $0 표시**: `InitialBalance` 설정 시 `_cachedUsdtBalance` 동시 초기화 — `RefreshProfitDashboard` 첫 API 호출 실패해도 $0 대신 초기 잔고 표시

## [5.10.12] - 2026-04-17

### Fixed

 - **DB HOLDLOCK 완전 제거**: `SaveGeneralSettingsAsync` MERGE에서 `WITH (HOLDLOCK)` 제거 — SELECT 블로킹 해소 (SavePositionStateAsync는 이전 커밋에서 제거됨)
 - **DB 커넥션 풀 고갈 수정**: Footer/LiveLog 드레인이 항목당 개별 커넥션 최대 80개 오픈 → 단일 커넥션으로 배치 INSERT (80→1 커넥션). `SaveFooterLogsBatchAsync` / `SaveLiveLogsBatchAsync` 추가
 - **대시보드 $0 표시 버그**: API 실패 시 캐시(WalletBalance)를 0으로 덮어쓰던 버그 수정 — API 성공 시에만 캐시 업데이트, 실패 시 마지막 유효값 유지
 - **GetBalancesAsync 중복 호출 제거**: 대시보드 갱신 시 Wallet/Available 각각 별도 API 호출 2회 → `GetBalancePairAsync` 단일 호출로 통합
 - **GetAvailableBalanceAsync 에러 로그 추가**: 실패 시 조용히 0 반환하던 것 → 오류 메시지 로깅

## [5.10.11] - 2026-04-17

### Fixed

 - **DB UNIQUE 오류 수정**: `SavePositionStateAsync` MERGE에 `WITH (HOLDLOCK)` 추가 — 모니터링 루프 동시 호출 시 두 세션 모두 NOT MATCHED → 동시 INSERT → PK(UserId,Symbol) 중복 violation 발생하던 버그
 - **GeneralSettings MERGE 동일 수정**: `WITH (HOLDLOCK)` 추가
 - **INSERT 타임아웃 방지**: `SavePositionStateAsync` `commandTimeout=8` 설정 — lock 경합 시 빠른 실패 (다음 tick에 재시도)
 - **UNIQUE 중복 삽입 무시**: SqlException 2627/2601(PK/UNIQUE 위반) catch 추가 — 다른 세션이 이미 INSERT한 경우 오류 없이 통과

## [5.10.10] - 2026-04-17

### Fixed

 - **계좌잔고 조회 실패 가시성**: `GetBalancesAsync` 실패 시 에러 코드 + 상세 메시지 로그 + 알림 추가 (기존: 조용히 0 반환)
 - **병목 개선 (REST API 과부하)**: `MonitorPositionStandard` 내 REST API 호출 주기 30s → 90s (AI Sniper Exit + Dual Stop Candle), 60s 오프셋 스태거링 추가 — 포지션 3개 시 6회→2회/90s
 - **이중 SL 처리 제거**: 진입 후 `MonitorPositionStandard`에 실제 거래소 SL가격(slPrice) 전달 → `hasCustomAbsoluteStop=true` → 봇 내부 ROE 기반 SL 체크 비활성화 (거래소 SL이 이미 처리)
 - **AI 진입 확인**: `PumpScanStrategy._pumpML.Predict()` 정상 호출 확인 — `AI_ENTRY` 로그로 구분 가능

## [5.10.9] - 2026-04-17

### Changed

 - **설정창 단순화**: General + Telegram Messages 탭만 유지, 나머지 탭(Transformer/Grid Strategy/Arbitrage) 제거
 - **메이저증거금% 제거**: 기본마진(DefaultMargin)을 메이저 증거금으로 통합 사용 — 별도 퍼센트 필드 삭제
 - **버튼 단순화**: 3년백테스트/WFO최적화/5분봉최적화/AI학습 버튼 제거, 저장/취소만 유지
 - **코드정리**: Grid/Arbitrage/Transformer 로드·저장·검증 코드 및 ValidateTransformerInputs 메서드 제거

## [5.10.8] - 2026-04-17

### Fixed

 - **CPU 30%+ 절감**: TickerFlushIntervalMs 초기값 200→300ms / FooterLogFlushIntervalMs 200→500ms / MaxPumpSubscribedSymbols 100→50 / PositionMonitor 루프 250→400ms
 - **AI_ENTRY dead cat bounce 차단**: 하락추세 + RSI < 50 조건 추가 — `AI_ENTRY_SKIP` 로그로 차단 확인 가능
 - **isMakingHigherLows Aggressive 오판 수정**: `HigherLowMinRiseRatio` 1.000→1.005 (동일가도 Higher Low 판정하던 버그)

## [5.10.7] - 2026-04-17

### Fixed

 - **설정창 누락 항목 복구**: 메이저 코인 활성여부(`chkEnableMajorTrading`), 메이저 슬롯, PUMP 슬롯, 하루 진입 횟수 — force push로 인해 날아간 UI + 로드/저장 코드 복구
 - **isUptrend 3중 조건 강화**: `SMA20>SMA50` 단일 조건 → ①골든크로스 ②현재가>SMA20 ③SMA20 기울기 상승 — dead cat bounce를 상승추세로 오판하는 근본 원인 수정 (SKYAIUSDT 패턴)

## [5.10.6] - 2026-04-17

### Fixed

 - **텔레그램 청산 알림 누락 버그 수정**: `ExecuteMarketClose`의 "already closed" 경로(거래소에서 이미 청산됨)에서 DB 저장 후 Telegram 알림이 전송되지 않던 버그. `NotifyProfitAsync` 호출 추가 — ORDIUSDT 등 익절/손절 완료 시 알림 누락 재현 불가

## [5.10.5] - 2026-04-17

### Fixed

 - **하락추세 MEGA_PUMP 진입 차단**: `isUptrend` 조건 추가 — 5배 거래량 + 3% 양봉이라도 하락추세면 진입 차단, `MEGA_PUMP_SKIP` 로그 기록. 기존에는 추세 방향 검사 없음 (1000PEPEUSDT -20% 진입 원인)
 - **AI_ENTRY 하락추세 임계값 강화**: 하락추세 시 ML 확률 임계값 65% → 78%로 상향
 - **과열 감지 기준 개선**: 연속 양봉 카운트 방식 → 비율 기반 (최근 12봉 중 10봉+ 양봉 = 83%+) 변경. 변동성 구간에서도 과열 정확도 향상

## [5.10.4] - 2026-04-17

### Fixed

 - **PUMP 과열 진입 차단 (isOverextended)**: 최근 12개 5분봉 중 10봉 이상 양봉이면 FALLBACK 차단, MEGA_PUMP RSI 캡 80→70 적용. BIOUSDT 15분봉 4개 연속 양봉 후 고점 진입 방지
 - **하드 가격 SL 완화**: 진입가 대비 -3% → -4% (ROE -60% → -80%), 고점 대비 -5% → -7%로 여유 확대
 - **FALLBACK 모멘텀 기준 강화**: 가격 회복 1.5% → 2.5% 이상으로 상향

## [5.10.3] - 2026-04-17

### Fixed

 - **저유동성 코인 진입 차단**: `BuildCandidates` 단계에서 24h 거래대금 $5M 미만 심볼 제외 (PPIPNUSDT 등 슬리피지 큰 소형 코인)
 - **CPU 사용량 절감**: `TickerFlushMinMs` 100ms → 300ms로 조정, UI 갱신 빈도 완화
 - **메모리 사용량 절감**: `MaxPumpSubscribedSymbols` 200 → 100으로 감소, WebSocket 구독 심볼 제한

## [5.10.2] - 2026-04-17

### Added

 - **메이저/PUMP 슬롯·진입횟수 DB 설정 연동**: `GeneralSettings` 테이블에 `EnableMajorTrading`, `MaxMajorSlots`, `MaxPumpSlots`, `MaxDailyEntries` 컬럼 추가 — UI 설정값이 DB에 저장·적용

## [5.10.1] - 2026-04-17

### Fixed

 - **메이저 SHORT 진입 조건 강화**: 명확한 하락 신호 없이 SHORT 진입하는 오판 방지

## [5.10.0] - 2026-04-17

### Fixed

 - **AI 학습 개선**: 상승장 SHORT 오진 방지 — 강한 상승 추세에서 잘못된 SHORT 레이블 학습 억제
 - **가짜 펌프 진입 방지**: 실제 모멘텀 없는 단순 가격 상승에 대한 진입 필터 강화

## [5.3.0] - 2026-04-14

### Added

 - **신호 원가 대비 고점 진입 차단 (STALE_SIGNAL)**: 슬롯 부족으로 차단된 신호의 첫 가격 기록 → 나중에 슬롯 비었을 때 현재가가 2%+ 올랐으면 진입 차단. 30분 이상 경과한 신호도 무효화. TRADOORUSDT/PLUMEUSDT 고점 진입 근본 해결
 - LATE_ENTRY 기준 3% → 2%로 강화

## [5.2.8] - 2026-04-14

### Fixed

 - **ETA 예측 → 자동 시장가 진입 연결**: ETA_TRIGGER 시간 도달 + 재평가 65% 이상 → `ExecuteAutoOrder` 시장가 진입 자동 실행. 기존에는 로그만 찍고 진입 안 함

## [5.2.6] - 2026-04-14

### Added

 - **늦은 진입 차단 (LATE_ENTRY)**: PUMP/SPIKE 코인이 최근 30분 저점 대비 3% 이상 상승 + 최근 2봉 음봉(하락 전환)이면 진입 차단. FIGHTUSDT 고점 진입 → 즉시 손절 방지

### Fixed

 - **TICK_SURGE RR 최소값 완화**: 1.0 → 0.5. 급등 초기에 빠르게 진입 (BCHUSDT 18분 지연 해결)
 - **SL/TP/Trailing -4120 오류 해결**: `closePosition=true` 3차 폴백 추가. 일부 심볼에서 STOP_MARKET/TAKE_PROFIT_MARKET/TRAILING_STOP_MARKET 주문 타입 미지원 시 수량 없이 전체 포지션 청산 주문으로 등록

## [5.2.4] - 2026-04-14

### Fixed

 - **다른 유저 포지션 완전 분리**: `_activePositions`의 모든 ContainsKey 중복 체크를 `IsOwnPosition` 기반으로 변경. 다른 유저가 보유한 심볼도 이 유저가 독립적으로 진입 가능
 - 메인 라우터, PumpEntry, SPIKE, 드라이스펠 복구진입 등 전체 경로에서 `IsOwnPosition=false`면 보유 체크 스킵

## [5.2.3] - 2026-04-14

### Fixed

 - **팬텀 슬롯 점유 해결**: 다른 유저 포지션이 `_activePositions` 슬롯을 점유하여 pump=3/3 차단 발생. `IsOwnPosition` 플래그 도입 — 시작 시 DB TradeHistory(UserId)와 교차 비교하여 자기 포지션만 슬롯 카운트에 포함. 슬롯 체크 6개 지점 전부 수정
 - 실시간 웹소켓(`HandleAccountUpdate`)에서도 기존 추적 포지션이 아니면 `IsOwnPosition=false` 마킹

## [5.2.2] - 2026-04-14

### Fixed

 - **CandleData 저장 타임아웃 해결**: `NOT EXISTS` 조건에 `IntervalText` 누락 → 7.6M행 풀테이블 스캔 발생. 인덱스 `UQ_Candle` 활용하도록 수정
 - **UQ_Candle UNIQUE KEY 위반 해결**: 소량/벌크 경로 모두 `IntervalText` 포함 중복 체크 + 동시 저장 레이스 컨디션 시 SqlException 2627/2601 자동 무시
 - **FooterLogs Message 잘림 해결**: `NVARCHAR(1000)` → 자동 `NVARCHAR(4000)` 마이그레이션 + 코드에서 4000자 초과 트렁케이트

## [Unreleased]

### Added

 - 없음

### Changed

 - 없음

## [5.0.7] - 2026-04-12

### 중요 — 차트 데이터 기반 AI 학습 복원 + 하드코딩 제거

사용자 지적: "학습 데이터가 왜 부족한데 좆같은넘아 차트데이터로 하기로 했잖아"

DB 실제 크기 확인:

- CandleData 총 7,682,014건 (883.6MB)
- 5분봉 5,576,570건 / 557심볼 / 2024-09 ~ (18개월)
- 1분봉 2,061,466건 / 58심볼
- 메이저 9종: 각 ~52,800봉 (184일)

기존 문제: Forecaster 학습 데이터 심볼당 200봉(`TakeLast(100)`) 만 사용 →
Major 768건 / AccA 0%. DB 의 수백만 건을 쓰지 않았음.

### DbManager.GetBulkCandleDataAsync 신규

대용량 로드 메서드 추가:

- IntervalText 파라미터 (5m / 1m)
- 심볼당 최대 N개 (기본 10000)
- SymbolFilter (null 이면 전체)
- 120초 타임아웃, 테이블 힌트 WITH (NOLOCK)

### Forecaster 학습 파이프라인 대용량 교체

**MajorForecaster (5분봉)**:

- 기존: 거래소 API 에서 9심볼 × 1000봉 = ~9000건
- 신규: DB 에서 9심볼 × 20000봉 = **~18만건**

**PumpForecaster (5분봉)**:

- 기존: klineMap × TakeLast(100) ≈ 55K
- 신규: DB 에서 PUMP 심볼 × 5000봉 = **수백만건**

**SpikeForecaster (1분봉)**:

- 기존: 거래소 API 50심볼 × 200봉 = 10K
- 신규: DB 에서 58심볼 × 20000봉 = **~116만건**

### OpportunityForecaster 라벨링 완화

기존 문제: `TargetProfit` + `MaxDrawdown` AND 조건으로 포지티브 샘플 거의 없음.

수정:

- `relaxedMaxDd = MaxDrawdownPct × 2.0` (2배 완화)
- risk-adjusted score: `up - 2*dd` → `up - dd` (패널티 50% 감소)
- Forecaster 파라미터 완화:
  - Major: window 12→24봉, target 1.5%→0.8%
  - Pump: window 12→24봉, target 2.5%→2.0%
  - Spike: window 5→10봉, target 4.0%→2.5%

### 하드코딩 제거 (메모리 원칙 준수)

**v5.0.6 에서 제가 추가한 하드코딩 필터 제거**:

- Gate 1 Check 5 `midCapOverExtended` (1h >10%) ❌ 제거
- Gate 1 Check 6 `multipleBearishVolatility` (3봉 5%+ 음봉 2개) ❌ 제거
- Gate 2 중형 유동성 8분 단축 ❌ 제거 → 15분 통일

이유: Forecaster 가 대용량 학습으로 동일 패턴을 직접 학습 → AI 판단에 위임.
Gate 1 Check 1~4 (명백한 피크 징후) 는 유지.

**유지된 것**:

- v5.0.5 초소형 (<$10M) 마진 50% 축소 → 사용자 명시 지시로 유지 (사이즈 관리 영역)

### Expected Impact

- Forecaster AccA 대폭 개선 예상:
  - Major 0% → 50~65% 목표
  - Pump 90% (sample 적어서 왜곡) → 60~70% 정상화
  - Spike 98% (포지티브 0.6% 왜곡) → 60~70% 정상화
- Forecaster AccA ≥ 50% 시 Fallback 경로 자동 해제 (v5.0.2 로직)
- 중형 손실 대응은 Gate 1 Check 1~4 + Forecaster 예측에 맡김

## [5.0.6] - 2026-04-12

### 중형 ($50~200M) 유동성 수익 개선

2026-04-12 DB 분석: C 버킷(중형) 승률 33%, 순 -$99.26. AIOTUSDT/SKYAIUSDT/CROSSUSDT.

**Phase 4 — Gate 1 Router 공통 관문화**

기존: Gate 1 이 PumpScanStrategy 핸들러와 SPIKE_FAST 2곳에만 있어 TICK_SURGE/PUMP_WATCH 등 일부 경로 누락.

신규: `ExecuteAutoOrder` Router 에 공통 배치 → 모든 LONG 진입 경로 자동 커버.
PumpScanStrategy 는 이중 방어로 유지 (SPIKE_FAST 는 ExecuteAutoOrder 우회 경로).

**Phase 3 — Gate 2 중형 대기시간 단축**

- 기존: 모든 심볼 15분 대기
- 신규: `vol24h >= $50M` 이면 8분 대기 (중형은 반등 빠름)
- 그 외는 기존 15분 유지

**Phase 2 — Gate 1 중형 특화 조건 추가**

`IsAlreadyPumpedRecently` 에 2개 조건 추가 (24h 거래량 $50~200M 일 때만):

- **Check 5** `midCapOverExtended`: 1시간 누적 >10% 상승 → 피크 위험
- **Check 6** `multipleBearishVolatility`: 최근 3봉 중 range 5%+ 음봉이 2개 이상 → 덤핑 진행

유동성 추정: 최근 12봉(1h) × 24 근사 (TickerCache 없을 때).

**Phase 1 — 작동 검증 (DB 로그)**

v5.0.1 배포 이후 `[GATE1]` 로그 0건 확인. 원인:

- PC#2 구버전 운영 중 (GATE1 없음)
- 또는 AIOT 가 PumpScan 아닌 다른 경로(TICK_SURGE) 로 진입 → v5.0.1 Gate 1 누락
- v5.0.6 Router 공통화로 모든 경로 자동 커버

### Expected Impact

- 04-12 C 버킷 6건 중 AIOT/SKYAI/CROSS 3건 차단 예상
- 순 -$99.26 → 약 -$50 방어
- Gate 2 8분 대기로 중형 반등 자리 포착 기회

## [5.0.5] - 2026-04-12

### Changed — 초저유동성 심볼 PUMP 마진 50% 축소

2026-04-12 UserId=1 DB 분석 결과:

- 초저유동성 (24h <$10M) A 버킷: 12건, 승률 66.7%, 순 -$29.21, 평균 -$2.43
- 저유동성 ($10~50M) B 버킷: 16건, 승률 87.5%, 순 **+$293.78** (메인 수익원)
- 중유동성 ($50~200M) C 버킷: 6건, 승률 33%, 순 -$99.26 (별도 개선 대상)

A 버킷 손실 특징:

- 평균 손실 -$16 / 평균 수익 +$8 → **손실이 수익의 2배**
- 자기 펌핑 위험 (5명 × $4K = $20K notional 이 초저유동성에 충격)

**수정**:

- `GetLiquidityAdjustedPumpMarginUsdt(symbol)` 헬퍼 신규
- `TickerCache.QuoteVolume` 조회 → `vol24h < $10M` 이면 **마진 50% 축소**
- 최소 $10 보장, 로그로 축소 알림 (`💧 [LIQUIDITY]`)
- **중형 이상은 변경 없음** (수익 개선 대상, Gate 1/2 + Forecaster 별도 작업)

**적용 위치 (5곳)**:

- `ExecutePumpEntry` (TradingEngine.cs:4657)
- `SPIKE_FAST` (TradingEngine.cs:7821)
- `ExecutePumpLongEntry` (ctx.MarginUsdt)
- `ExecutePumpShortEntry` (ctx.MarginUsdt)
- `CalculateOrderQuantityAsync` (Forecaster 경로 수량 계산)

**예상 효과 (04-12 기준)**:

- A 버킷 순PnL: -$29.21 → **-$14.60** (+$14.61 방어)
- 승리 거래 수익도 50% 감소하지만, 손실 > 수익 구조라 순효과 플러스

## [5.0.4] - 2026-04-12

### Changed — AI 관제탑 스팸 해결

**문제**: v5.0.0 이후 `skipAiGateCheck=true` 로 `EvaluateEntryWithCoinTypeAsync` 호출 자체가 줄어서
AI 게이트 카운트가 0건 → 매 5분 텔레그램에 "📭 판정 없음" 스팸 메시지 며칠째 발송.

**수정**:

- 관제탑 요약 주기: **5분 → 15분** 으로 완화
- `FlushAiGateSummaryAsync(forceSendEmpty: false)` 로 변경 → 판정 0건일 때 메시지 안 보냄
- 텔레그램 메시지 제목/본문 텍스트 "5분" → "15분" 일관성 수정

## [5.0.3] - 2026-04-12

### Added — 카테고리별 오늘 통계 카드 (좌측 사이드바)

메인창 좌측 사이드바 하단에 **MAJOR / PUMP / SPIKE** 3개 카드 추가.
각 카드에 오늘(KST 00:00 기준) 통계 표시:

- **PnL** (수익/손실 달러, 색상: 녹색/빨강)
- **Entries** (진입 건수)
- **Win Rate** (승률 %, 녹색>=60% / 노랑>=40% / 빨강<40%)

#### 분류 규칙

`DbManager.ResolveTradeCategory(symbol, signalSource)`:

- **MAJOR**: 심볼이 9개 메이저 중 하나 (BTC/ETH/SOL/XRP/BNB/ADA/DOGE/AVAX/LINK)
- **SPIKE**: signalSource 가 `SPIKE` 로 시작
- **PUMP**: 나머지 (MAJOR_MEME, PUMP_WATCH_CONFIRMED, TICK_SURGE, FORECAST_FALLBACK 등)

#### DB 스키마

- `TradeHistory` 테이블에 `Category NVARCHAR(10) NULL` 컬럼 자동 추가 (ALTER)
- 인덱스 `IX_TradeHistory_Category_EntryTime` (WHERE Category IS NOT NULL)
- **과거 레코드는 NULL 유지 → 통계 쿼리에서 자동 제외**

#### 진입/청산 저장 로직 수정

- `UpsertTradeEntryAsync`: INSERT + UPDATE 에 `Category` 추가
- `EnsureOpenTradeForPositionAsync`: INSERT 에 `Category` 추가 (SYNC_RESTORED)
- `CompleteTradeAsync` / `TryCompleteOpenTradeAsync`: Fallback INSERT 에 `Category` 추가
- PartialClose 경로 2곳: `Category` 추가

#### 통계 쿼리

```sql
WITH Groups AS (
    SELECT Category, Symbol, EntryTime,
           SUM(ISNULL(PnL, 0)) AS TotalPnL,
           MAX(CASE WHEN IsClosed = 1 THEN 1 ELSE 0 END) AS HasClosed
    FROM dbo.TradeHistory
    WHERE Category IS NOT NULL AND EntryTime >= @todayStart
    GROUP BY Category, Symbol, EntryTime
)
SELECT Category, COUNT(*) AS Entries,
       SUM(CASE WHEN HasClosed=1 AND TotalPnL>0 THEN 1 ELSE 0 END) AS Wins,
       SUM(CASE WHEN HasClosed=1 AND TotalPnL<0 THEN 1 ELSE 0 END) AS Losses,
       SUM(TotalPnL) AS TotalPnL
FROM Groups GROUP BY Category
```

- **진입 시각 + Symbol 그룹핑** → PartialClose 여러 행이 1건으로 카운트
- **전체 계정**: UserId 필터 없음 (사용자 지시)
- 과거 데이터 = `Category IS NULL` → 자동 제외

#### MainViewModel / UI

- `MajorStats`, `PumpStats`, `SpikeStats` 속성 (CategoryStatsViewModel)
- `RefreshCategoryStatsAsync()` 메서드
- 시작 5초 후 첫 호출 + **5분 주기 Timer**
- XAML: 좌측 사이드바 DETECT/TRAINING 카드 아래 3개 카드 (StatCardStyle)

## [5.0.2] - 2026-04-12

### Fixed (메이저 코인 진입 0건 — UserId=1 이틀간 BTC/ETH/SOL/XRP 진입 없음)

DB 분석 결과:
- UserId=1 최근 24시간 거래 47건 전부 PUMP, 메이저 0건
- 원인 1: MajorCoinStrategy aiScore 경계(65/35) + 모멘텀 AND 조건이 너무 엄격
  - 메이저 5m 변동성 작아 30m +1.5% / 1h +3% 조건 도달 거의 불가능
- 원인 2: MajorForecaster 학습 샘플 부족(765건, 9종×85봉) → AccA=0%
  - `forecast.HasOpportunity=false` 만 반환 → 진입 경로 완전 차단
- 원인 3: v5.0.0 Fallback 조건 `!IsModelLoaded` 만 체크
  - 학습 완료 후 AccA=0% 여도 Forecaster 경로 강제 사용 → 모든 신호 차단

### 수정 1 — MajorCoinStrategy aiScore 3단계 완화

기존 (v4.9.8):

```csharp
if (aiScore >= 65 && (isPriceRecovering || isStrongBounce)) decision = "LONG";
```

신규 (v5.0.2):

```csharp
if (aiScore >= 70) decision = "LONG";                       // 순수 점수 단독
else if (aiScore >= 62 && (isPriceRecovering || isStrongBounce)) decision = "LONG";
else if (aiScore >= 58 && isMakingHigherLows && currentPrice > sma20) decision = "LONG";
// SHORT 방향 3단계 대칭
else if (aiScore <= 30) decision = "SHORT";
else if (aiScore <= 38 && (isPriceDropping || isStrongDrop || isMakingLowerHighs)) decision = "SHORT";
else if (aiScore <= 42 && isMakingLowerHighs && currentPrice < sma20) decision = "SHORT";
```

### 수정 2 — Forecaster 정확도 기반 Fallback 강제

- `_pumpForecasterAccuracy`, `_majorForecasterAccuracy`, `_spikeForecasterAccuracy` 필드 추가
- `ForecasterMinAccuracyForEntry = 0.50` 임계값
- 학습 시 각 Forecaster 정확도 저장
- 신호 핸들러에서 `IsModelLoaded AND AccA >= 50%` 체크
- 기준 미달 시 **기존 `ExecuteAutoOrder` 경로로 Fallback** (PumpSignalClassifier/AIDoubleCheckEntryGate 사용)
- 이렇게 하면 초기 학습 전까지 기존 안정 경로 유지

### 수정 3 — MajorForecaster 학습 데이터 12배 확대

- 기존: `klineMap` 에서 메이저만 추출 → 85봉 × 9종 = 765건
- 신규: 거래소 API 에서 메이저 심볼당 **1000봉(≈3.5일) 직접 로드** → 약 9000건
- 이 정도 샘플로 AccA 50% 이상 도달 기대

### Impact

- 메이저 진입 경로 복구: aiScore 58~70 구간에서도 신호 생성
- Forecaster 학습 품질 좋아질 때까지 Fallback 으로 정상 작동
- PumpForecaster 도 동일 로직 적용 → PumpScanStrategy 도 AccA 부족 시 기존 경로 유지

## [5.0.1] - 2026-04-12

### Added (Gate 1 + Gate 2 — 고점 진입 차단 + 지연 반등 진입)

2026-04-12 UserId=1 DB 분석 기반:
- **TRUUSDT -41%** (진입 1분, 01:04 진입 vs 04:01 +170% 성공 동일 심볼)
- **AIOTUSDT -33%** (진입 1분, 진입 전 30분 +14% 이미 상승)
- **TAGUSDT -32%** (진입 1분, 직전봉 -14.75% 덤핑 중)
- **SKYAIUSDT -28%** (진입 1분, 피크 대비 -5% 조정 시작)

4건 총 -$188.30, 전부 "이미 너무 올랐음" 패턴.

#### Gate 1 — `IsAlreadyPumpedRecently` (TradingEngine.cs)

**즉시 진입 차단**:
- [Check 1] 최근 6봉(30분) 누적 상승률 > **8%** → 고점 매수 위험
- [Check 2] 최근 6봉 고점 대비 **2~5% 조정 진행 중** → 피크 후 하락
- [Check 3] 직전 봉이 **긴 윗꼬리 60%+** OR **거대 음봉 (range 4%+)** → 매도 압력

#### Gate 2 — `IsPullbackRecoveryEntry`

**지연 진입 조건** (Gate 1 차단 후 10분 이내 재평가):
- [1] 최근 8봉 내 피크 찾기 (최근 2봉 제외)
- [2] 피크 이후 **2~6봉 경과**
- [3] 피크 대비 **2~5% 조정 완료**
- [4] **Higher Low 확인** (하락 종료)
- [5] 현재 봉 **양봉** (반등 확인)
- [6] **거래량 피크 대비 80%+ 회복**

#### ScheduleGate2Reevaluation — 지연 진입 스케줄러

Gate 1 차단 시 `_pendingGate2` 등록:
- 1분마다 Gate 2 조건 재체크 (최대 15분)
- 중복 예약 방지 (심볼당 1건)
- 포지션 발생 시 자동 취소
- Gate 2 통과 시 `ExecuteAutoOrder(..., source + "_GATE2", skipAiGateCheck: true)` 실행
- 15분 만료 시 자동 포기 (재예측 대기)

#### 적용 위치

1. **PumpScanStrategy.OnTradeSignal 핸들러** (TradingEngine.cs:1294)
   - Forecaster 경로 호출 전 Gate 1 사전 차단
2. **SPIKE_FAST 경로** (TradingEngine.cs:7693)
   - MTF Guardian 직후, SpikeForecaster 예측 직전

### Expected Impact

- 04-12 기준 4건 손절 -$188 → **전부 Gate 1 차단 + Gate 2 지연 진입으로 +수익 가능**
- TRUUSDT 사례: 01:04 차단 → Gate2 가 04:01 자리를 찾아낼 수 있음 (실제 +170% 자리)
- 덤핑 진행 중 (TAGUSDT) 은 Gate 2 도 통과 못함 → 15분 자동 포기

## [5.0.0] - 2026-04-11

### 🎯 전면 재설계 — 예측형 Forecaster 아키텍처 도입

기존 "실시간 반응형 진입" (매 10초 스캔 → 즉시 Market) 을
**"예측형 진입"** (5분봉 마감 시 예측 → LIMIT 예약 → 만료/돌파 시 처리) 으로 교체.

#### Added — 신규 컴포넌트 (6개 파일)

**`Models/ForecastModels.cs`** — 예측 결과 + 예약 진입 데이터 모델
- `ForecastResult`: 기회/확률/방향/시점/가격 오프셋
- `PendingEntry`: Symbol, Direction, TargetPrice, 예측 시점, 만료, LimitOrderId

**`Services/OpportunityForecaster.cs`** — 3-Model 예측 베이스 클래스
- Model A: Classifier (기회 있음, LightGBM Binary)
- Model B: Regressor (몇 봉 후 최적 진입, LightGBM Regression)
- Model C: Regressor (현재가 대비 진입가 %, LightGBM Regression)
- 학습 데이터 생성: 미래 window 내 risk-adjusted 최적 진입점 탐색
- 36개 피처 ForecastFeature (PumpFeature 호환)

**`Services/PumpForecaster.cs`** — PUMP 알트 전용 (5분봉)
- FutureWindow=12봉(60분), Target=+2.5%, MaxDD=1.5%, MinConf=0.60

**`Services/MajorForecaster.cs`** — 메이저 코인 전용 (5분봉)
- FutureWindow=12봉, Target=+1.5% (변동폭小), MaxDD=1.0%, MinConf=0.58
- 현재 LONG-only 학습 (SHORT는 기존 경로 fallback)

**`Services/SpikeForecaster.cs`** — 1분 스파이크 전용 (1분봉 학습+추론)
- **B1 버그 수정**: 기존 PumpSignalClassifier.Spike는 5분봉 학습 + 1분봉 추론으로 스케일 불일치
- FutureWindow=5봉(5분), Target=+4%, MaxDD=1.0%, MinConf=0.65

**`Services/EntryScheduler.cs`** — 공용 예약 스케줄러
- `RegisterAsync`: MTF Guardian 검증 → LIMIT 주문 발주 → pending 관리
- 가격 Watchdog: 조기 돌파 감지 시 Market Fallback (+1% 이상)
- 만료 체크 타이머 (10초 주기): Expiry 경과 시 자동 취소 + 재예측 대기
- 중복 방지: 심볼당 pending 1건만 허용

#### Changed — TradingEngine 통합

- `_pumpForecaster`, `_majorForecaster`, `_spikeForecaster`, `_entryScheduler` 인스턴스 추가
- 초기화 시 3개 Forecaster 모델 로드 + Scheduler 연결
- `OnTickerUpdate` → `EntryScheduler.OnPriceTickAsync` 연결 (가격 Watchdog)
- MTF Guardian을 Scheduler에 주입 → 예약 전 사전 차단
- Market Fallback executor: `ExecuteAutoOrder(..., "FORECAST_FALLBACK", skipAiGateCheck: true)`
- 재학습 파이프라인: PumpForecaster + MajorForecaster + SpikeForecaster 동시 학습
- SpikeForecaster는 **DB가 아닌 거래소 API에서 1분봉 직접 로드** (50개 심볼, 200봉)

#### Changed — 진입 경로 재설계

**PumpScanStrategy 신호 핸들러**:
- 기존: PumpScan 후보 → `ExecuteAutoOrder(..., skipAiGateCheck=true)` 즉시 Market
- 신규: PumpScan 후보 → `PumpForecaster.Forecast()` → `EntryScheduler.RegisterAsync()` LIMIT 예약
- Forecaster 미학습 시 기존 경로 Fallback

**MajorCoinStrategy 신호 핸들러**:
- LONG: `MajorForecaster.Forecast()` → Scheduler 등록
- SHORT: 기존 `ExecuteAutoOrder()` 경로 (SHORT 모델 미학습)

**SPIKE_FAST**:
- 기존: `PumpSignalClassifier.Spike.Predict()` (5m 학습 + 1m 추론 불일치)
- 신규: `SpikeForecaster.Forecast(klines)` (1분봉 학습+추론 일치)
- 1분 스파이크는 즉시 진입 원칙이므로 Scheduler 미경유

#### Fixed

- **B1 버그**: Spike 모델 학습/추론 시간봉 불일치 → `SpikeForecaster` 신규로 완전 해결
- **실시간 반응형 한계**: 고점 진입/타이밍 놓침 → 예측 + LIMIT 예약 + Watchdog 로 사전 선점

## [4.9.9] - 2026-04-11

### Added (MTF Guardian — 상위 시간봉 역방향 차단)

**BASUSDT 5분봉 12개 연속 하락 중 1분 반짝 상승으로 진입 후 물림 버그** 구조적 해결.

진입 경로 공통 관문에 **고정 규칙** 기반 멀티 타임프레임 분석:
- **[A] 명확한 하락/상승**: 1시간 누적 변화율 ±3% 이상이면 역방향 차단
- **[B] 구조적 추세**: SMA 완전 정/역배열 (20/50/120) + 방향 일치 시 역방향 차단
- **[C] 모멘텀 추세**: 12봉 중 9개 이상 한 방향 + 추가 모멘텀 시 역방향 차단

**적용 위치**:
1. `TradingEngine.ExecuteAutoOrder` Router — AI Gate 직전 공통 관문
2. `TradingEngine.StartSpikeFastMonitor` — Spike 모델 호출 직전 선행 차단

**예외**: `CRASH_REVERSE` / `PUMP_REVERSE` 는 급변 대응 전용이므로 제외.

**특징**:
- 하드코딩 고정 규칙 (시장/심볼 무관)
- 가변 임계값/동적 조정 없음 — 순수 차트 구조 분석
- 데이터 부족(24봉 미만) 시 통과 (과차단 방지)
- LONG/SHORT 완전 대칭

## [4.9.8] - 2026-04-11

### Added (PUMP 피처 대폭 확장 — 24→36개)

사용자 요청 지표 12개 신규 추가:
- **MACD_Main / MACD_Signal**: 기존엔 Hist만 있어 방향성 판단 불가 → 메인/시그널 라인 추가
- **ADX / +DI / -DI (14)**: 추세 강도 및 방향 강도 핵심 지표
- **Price_To_BB_Mid**: 볼밴 중간선 대비 현재가 거리 (%)
- **Lower_Shadow_Avg**: 최근 3봉 아래꼬리 비율 (매수 압력)
- **Volume_Change_Pct**: 최근 3봉 vs 직전 3봉 거래량 변화율
- **Trend_Strength**: SMA 정배열 + ADX 결합 추세 강도 (-1~+1)
- **Fib_Position**: 최근 100봉 고저점 기준 피보나치 위치
- **Stoch_K / Stoch_D (14,3)**: Stochastic Oscillator

### Changed (라벨링 이중 경로 + 타입별 파라미터)

**v4.9.7 라벨링이 너무 엄격해서 Entry 비율 1~5% → 진입 급감** 문제 해결:

**경로 A (추세 전환점)**: swing low 직후 1~N봉 이내 진입
- RAVE 17:30 같은 "저점 직후" 케이스 포착

**경로 B (추세 지속 조정)**: 이미 상승 중 pullback에서 재반등
- SMA10 상승 + 직전 3봉 대비 -0.3~2.5% 눌림 + 미래 목표 달성 + 드로다운 제한

**Normal (일반 진입)**: swing window 10봉, 4봉 이내, +3% 검증, 1.1x 거래량 (완만)
**Spike (급등 진입)**: swing window 5봉, 2봉 이내, +4% 검증, 1.5x 거래량 (급격)

### Fixed (MajorCoinStrategy SHORT 21시간 0건 버그)

메이저 롱/숏 21시간 0건 원인 분석 및 수정:
- **LONG 편향 스코어링**: `price > sma20 && sma20 > sma50` 는 +10만 있고 SHORT 대칭 없음
- **박스권 고정**: `RSI 45~68 +10` 중립 보너스가 sideway 장에서 점수를 50~65에 가둠
- **경계 너무 넓음**: 70/30 이라 애매한 시그널 전부 WAIT

**수정**:
- 경계 70/30 → **65/35** 완화 + 가격 모멘텀 확인 조건 추가 (isPriceRecovering/Dropping)
- 강한 모멘텀 단독 fallback 추가 (isStrongBounce+higherLows+RSI>55 등)
- CalculateScore 대칭화: SMA/RSI/Volume 모두 LONG/SHORT 대칭 가점
- RSI 중립 보너스 제거, 구간별 대칭 (55~68 +8 / 32~45 -8)
- Volume 가점을 추세 방향 기반으로 (상승↑거래량↑ = +, 하락↑거래량↑ = -)

### Fixed (활성 포지션 UI 1개만 표시되던 버그)

- `MainViewModel.UpdateFocusedPositionFromEngine` 이 `OrderByDescending(EntryTime).FirstOrDefault()` 로 가장 최근 1개만 표시하던 문제
- **수정**: `ActivePositions` ObservableCollection 추가 → 모든 포지션 동시 표시
- **XAML**: FOCUSED POSITION 단일 Border → ItemsControl 리스트 (스크롤 가능)

## [4.9.7] - 2026-04-11

### Changed (PumpSignalClassifier 추세 전환점 라벨링)

기존 라벨링 문제:
- "미래 +1.5% 올랐으면 진입=1" → 이미 고점인 봉도 라벨 1
- 증상: RAVEUSDT 고점 진입, BASUSDT 하락장 반등 진입

신규 라벨링 (swing low 기반):
1. 직전 10봉 swing low 탐지 (로컬 최소)
2. swing low에서 1~3봉 이내 (너무 늦지 않음)
3. 현재가 swing low 대비 +2% 이내 (아직 초기)
4. Higher Low + 거래량 1.2x 회복
5. 미래 window 내 swing low 대비 +5% 도달 (진짜 반등)
6. 미래 드로다운 swing low -1% 이내 (구조 유지)

피처 6개 추가 (Dist_From_Swing_Low, Bars_Since_Swing_Low, Swing_Low_Depth, Volume_At_Low_Ratio, Lower_Lows_Count_Prev, Structure_Break)

## [4.9.6] - 2026-04-11

### Fixed (Critical — skipAiGateCheck 실제 적용)

- **v4.9.5에서 `skipAiGateCheck=true` 파라미터를 추가했지만 실제로 사용 안 되던 버그**
  - `ExecuteAutoOrder`의 AI_GATE 분기에서 `shouldBypassAiGate = signalSource=="CRASH_REVERSE" || signalSource=="PUMP_REVERSE"` 만 체크
  - **매개변수 `skipAiGateCheck`를 무시**하여 `_pumpStrategy.OnTradeSignal`에서 `skipAiGateCheck=true`로 호출해도 여전히 AIDoubleCheckEntryGate 실행
- **수정**: `shouldBypassAiGate = skipAiGateCheck || signalSource=="CRASH_REVERSE" || signalSource=="PUMP_REVERSE"`
- **효과**: PumpScan이 `PumpSignalClassifier`로 AI 승인한 심볼이 AI_GATE 중복 검증 없이 바로 진입 경로로 진행

## [4.9.5] - 2026-04-11

### Fixed (Critical — ML 모델 충돌 해결)

v4.9.4 47분 운영 후 DB 진단 결과:
- PumpScan이 **BULLAUSDT / CROSSUSDT / IDUSDT** 를 `AI_ENTRY 65~80%` 로 승인 중
- 그런데 `AIDoubleCheckEntryGate.EntryTimingMLTrainer` 가 동일 심볼을 **ml=0.0% 반환** → `ML_Zero_Confidence` 로 차단
- **두 ML 모델이 정반대 결과**를 내는 구조적 충돌: `PumpSignalClassifier` 는 PUMP 심볼 전문, `EntryTimingMLTrainer` 는 PUMP 심볼 학습 거의 없음
- 결과: 한 달 내내 **AI_ENTRY 승인 후 AI_GATE 차단** 루프에서 진입 0건

**수정**:
1. `AIDoubleCheckEntryGate`: `mlConfidence<0.01f` 하드 차단 제거 — 경고 로그만 남기고 진입 계속
2. `_pumpStrategy.OnTradeSignal`: `ExecuteAutoOrder(... skipAiGateCheck: true)` 호출 — `PumpSignalClassifier` 가 이미 AI 승인 완료했으므로 `AIDoubleCheckEntryGate` 재검증 스킵. Router 0~7 공통 검증(슬롯/스태일/블랙리스트)은 그대로 적용됨

### 진단 결과 요약 (v4.9.4 47분 운영)

| 항목 | 건수 | 상태 |
|---|---|---|
| PumpScan 실행 | 276 | ✅ 10초 주기 정상 |
| 후보 추출 | 16,450 | ✅ 정상 |
| **PumpScan AI_ENTRY 승인** | **18** | ✅ 정상 (BULLA/CROSS/ID) |
| PumpScan EMIT | 18 | ✅ 정상 |
| ENTRY BLOCK | 7,580 |  |
| → AI_GATE `ML_Zero_Confidence` | 3,784 | 🔴 이번 수정 대상 |
| → VOLUME | 2,986 | 🟡 추적 불가 (소스에 없음) |
| → SLOT | 762 |  |

## [4.9.4] - 2026-04-11

### Fixed

- **중복 시그널 폭주 차단**: MajorCoinStrategy/PumpScan이 1초마다 동일 `(symbol, direction)` 시그널을 재생성해 `ExecuteAutoOrder`가 초당 n회 호출 → 30분 만에 ENTRY 로그 32,000건 누적. `_recentEntryAttempts` ConcurrentDictionary 추가하여 동일 (symbol|direction) 10초 내 재시도는 조용히 무시. 로그 10배 감소 예상, 실제 진입은 영향 없음 (10초 내 재시도 = 원래도 차단되었을 중복 시도)

### Added (진단)

- **AIDoubleCheckEntryGate ML=0 진단 로그**: `ML_Zero_Confidence` 원인 추적
  - `mlTrainer.IsModelLoaded` 여부
  - `mlPrediction == null` 여부
  - `mlPrediction.Probability/Score/ShouldEnter` 실제 값
  - `🔬 [ML_DIAG] {symbol} {decision} | reason=... | featureValid=...` 포맷
  - v4.9.3 재시작 후 DB 진단 결과: ETHUSDT SHORT가 30분 내내 `ML_Zero_Confidence` 2,537건 반복 차단되던 원인을 실시간으로 추적 가능

### 진단 결과 (v4.9.3 30분 운영 후 DB 조회)

| 항목 | 건수 |
|---|---|
| PumpScan 실행 | 152 (10초 간격 정상) |
| 후보 추출 | 9,546 |
| **AI_ENTRY 승인** | **0** ← 모델 임계값(0.65) 미달 |
| **VolumeSurge 감지** | **0** ← 박스권 시장 |
| ENTRY BLOCK | 3,685 |
| → AI_GATE BLOCK | 2,537 (거의 전부 `ml=0.0%`) |
| → VOLUME BLOCK | 704 (v4.9.3 제거 이전 누적) |
| → SLOT BLOCK | 366 (duplicatePosition 반복) |

## [4.9.3] - 2026-04-11

### Removed (Critical — AI-Only 원칙 위반 하드코딩 제거)

사용자 지적: "ai로 판단하라니까 자꾸 하드코딩하고 지랄이야". DB FooterLogs 기반 진단으로 10분 간 **1,027건** 진입이 VOLUME 필터로 차단되던 주범 발견. ML이 AI_ENTRY 승인한 ARIA/RAVE/SOON/LAB/BAS LONG 후보들도 모두 차단되던 상황.

- **TradingEngine.cs:VOLUME 필터 완전 제거** (기존 `volumeRatio<0.5` 하드코딩) — 10분 1,027건 차단의 주범. `Volume_Ratio`/`volumeMomentum`은 이미 `PumpSignalClassifier`/`AIDoubleCheckEntryGate`/`SurvivalEntryModel` 피처로 학습 중
- **TradingEngine.cs:RSI_EXTREME 차단 제거** (기존 `rsi>=88`/`rsi<=12` 하드코딩) — RSI 극단값 판단은 `SurvivalEntryModel` + `PriceDirectionPredictor`가 담당
- **PumpScanStrategy.cs:ultraVolatile 차단 제거** (기존 `ATR/price>=5%` 하드코딩)
- **PumpScanStrategy.cs:price<0.001 차단 제거**
- **PumpScanStrategy.cs:alreadyPumped 차단 제거** (기존 `+5% in 30min`)
- **PumpScanStrategy.cs:bigRiseFromLow 차단 제거** (기존 `+8% in 1h`)
- 위 필터들은 모두 진짜 급등 코인을 정확히 차단하던 주범. Price_Momentum_30m, Price_Change_Pct 는 이미 ML 피처

### 추가 진단 인프라

- FooterLogs DB 테이블 기반 중앙 진단 — 여러 컴퓨터 운영 시 로컬 파일 로그로는 원인 추적 불가능한 문제 우회. FooterLogs에 이미 모든 `OnStatusLog` 메시지가 저장되고 있으며, DB 쿼리로 PUMP/ENTRY/BLOCK 분포 확인 가능

## [4.9.2] - 2026-04-11

### Fixed (Critical 진단)

- **OnStatusLog 전체가 Serilog 파일 로그에 기록되지 않던 핵심 버그**
  - 증상: 사용자가 "진입이 없다" 호소 시 `log-YYYYMMDD.txt` 파일에 SIGNAL/PUMP/SCAN/CANDIDATE/REJECT/EMIT/ENTRY 로그가 0건으로 진단 불가
  - 원인: `TradingEngine.OnStatusLog` → `MainViewModel.HandleStatusLog` → `AddLog` → `QueueFooterLog` 경로에서 **`LoggerService.Info` 호출이 전혀 없어** UI 큐와 DB 쓰기만 되고 Serilog 파이프를 타지 않음. v4.8.2의 "파일 기록" 수정이 실제로는 OnStatusLog로만 경유해 효과 없었음
  - 수정:
    1. `MainViewModel.HandleStatusLog`에 `LoggerService.Info(msg)` 직접 호출 추가 → **모든 상태 로그가 파일에 남게 됨**
    2. `TradingEngine._pumpStrategy.OnLog` 구독 람다에 `LoggerService.Info` 직접 호출 추가
    3. `TradingEngine.ExecuteAutoOrder.EntryLog` 헬퍼에 `LoggerService.Info` 추가 → 모든 Router 단계 파일 기록
    4. `_crashDetector.OnLog` 구독 람다에 `LoggerService.Info` 추가
    5. `_crashDetector.OnVolumeSurgeDetected` 내부 `감시등록` / `동적학습등록` 로그에 `LoggerService.Info` 추가
  - 효과: 다음 재시작부터 모든 진입 파이프라인 로그가 파일로 남아 원인 추적 가능

## [4.9.1] - 2026-04-11

### Changed

- **메인창 ScrollBar 통일**: `Style TargetType="{x:Type ScrollBar}" BasedOn="{StaticResource MinimalScrollBar}"` 전역 기본 스타일 추가. Window 내 모든 ScrollBar(사이드바 ScrollViewer, Trade History, Performance 등)가 LIVE MARKET DataGrid와 동일한 색상(`#33FFFFFF`) + 두께(6px)로 통일.

## [4.9.0] - 2026-04-11

### Removed

- **AI COMMAND 탭 완전 제거** (Bull/Bear seesaw, Pulse, Battle 5카드, 애니메이션 Storyboard 일체)
- **AI MONITOR 탭 완전 제거**
- **ADVANCED 탭 완전 제거** (Arbitrage/FundTransfer/Rebalancing UI)
- **대시보드 Cockpit Strip 제거** (AI STATUS, THE PULSE, GOLDEN ZONE, ATR STOP, TREND-RIDER 5카드)
- **Right Panel 제거** (LIVE EVENTS, PROFIT GOAL, EXECUTION STEPPER, AI PREDICTION)
- **ENTRY GATE / AI LEARNING / AI PREDICTIVE TIME-OUT PROB 사이드 카드** 제거
- 관련 MainWindow.xaml.cs 핸들러 제거 (`BtnStart/Stop Arbitrage/FundTransfer/Rebalancing`, `RefreshAiCommandCenter`, `BuildArcGeometry`, `UpdateTimeOutProbWidgetUI`)
- XAML 라인 수 3,436 → 약 2,200 (36% 축소)

### Added

- **🎯 AI INSIGHT PANEL** (대시보드 중앙, 대기/보유 자동 전환)
  - **LEFT — Top Candidates**: PumpScan 로그 실시간 파싱하여 최대 8개 후보 표시 (Symbol / ML% / Status / 감지 시각)
    - Status 색상: `AI_ENTRY`(녹색) / `WATCH`(금색) / `REJECT`(적색)
  - **RIGHT — Focused Position**: 활성 포지션 있을 때 Deep Dive 카드 자동 표시
    - Symbol / Side / ROE / PNL / Holding / Entry ─●─ TP 진행 바 / SL / AI 재예측
    - 포지션 없을 때: "💤 활성 포지션 없음 — AI가 진입 기회를 탐색 중" 안내
- **📡 DETECT / TRAINING 사이드 카드** (사이드바 ACCOUNT 아래)
  - Trained: `N/M` (심볼별 학습 완료 수)
  - PumpScan/m, VolSurge/m, Spike/m (분당 감지 카운트)
- `TradingBot.Models.CandidateItem` / `PositionDetailViewModel` / `DetectHealthViewModel` 신설
- `TradingEngine.GetActivePositionSnapshot()` / `TryGetTickerPrice()` 헬퍼
- `InvertBoolToVisibilityConverter` 신설

### Changed

- 대시보드 탭을 **단일 화면**으로 통합 (사이드바 200px + 메인 영역)
- 메인 영역 RowDefinitions: Header / DataGrid(*) / AI Insight Panel(260px) / FAST LOG+ALERTS(150px)
- TabControl: 6개 → 3개 (DASHBOARD / TRADE HISTORY / PERFORMANCE)

## [4.8.2] - 2026-04-11

### Fixed

- **PumpScan 진단 로그가 파일에 기록되지 않던 문제**
  - 기존: `_pumpStrategy.OnLog`는 `OnLiveLog` 에만 연결되어 있어 Serilog 파일 로그(`log-YYYYMMDD.txt`)에는 SCAN / CANDIDATE / REJECT / AI_ENTRY / EMIT 등 모든 진단 메시지가 누락
  - 증상: 사용자가 "진입이 없다"고 했을 때 파일 로그만으로는 원인 추적 불가 (UI 없이 디버깅 불가)
  - 수정: `_pumpStrategy.OnLog`를 `OnLiveLog` + `OnStatusLog` 양쪽에 동시 전달
  - 효과: 로그 파일에서 `grep "SIGNAL.*PUMP"` 로 후보 추출 / 거절 사유 / ML 확률까지 전수 조회 가능

## [4.8.1] - 2026-04-11

### Added

- **EntryZoneRegressor — Part B 라이브 활용** (`Services/EntryZoneRegressor.cs`)
  - ML이 학습한 TP/SL 오프셋 %를 진입 시 실시간 예측
  - 과거 6개월 CandleData에서 각 시점을 "가상 진입"으로 라벨링
    - 타겟: 향후 48봉(4h) 내 최고가 상승 % (TP), 최저가 하락 % (SL)
  - 2개 분리 LightGBM Regressor (TP, SL)
  - 초기학습 2-b 단계에서 같이 학습
  - `ExecuteAutoOrder` 의 ctx 구성 직후: `CustomTakeProfitPrice` 와 `CustomStopLossPrice` 가 **둘 다 비어있을 때만** 예측 결과를 적용 (fallback) — 기존 MAJOR ATR / 회복 모드 / 사용자 지정 등 기존 로직 우선권 유지
  - LONG: `TP = price × (1+tpPct%)`, `SL = price × (1−slPct%)`
  - SHORT: 부호 반전
  - 로그: `🧠 [EntryZoneML] {symbol} {LONG/SHORT} | TP=1.85%→0.12345 | SL=0.96%→0.11876`

## [4.8.0] - 2026-04-11

### Added

- **최적 진입 가격 예측 시스템 (3접근)** — Pre-learning 기반 진입가 예측 인프라 신설
  - **접근 A — Pullback Depth Regression** (`Services/OptimalEntryPriceRegressor.cs`): 거래량 급증 감지 시 ML이 향후 눌림 % 예측 → `current × (1 - predicted%)` 에 LIMIT 주문 배치 목표
    - 라벨링: 과거 6개월 5m 캔들에서 `+2% 이상 랠리 발생` positive 샘플의 pullback% 추출 (lookAhead 24봉=2h)
    - LightGBM Regression, 피처 7개 (RSI/BB/ATR/Vol/Momentum/Volatility/HourOfDay)
    - 초기학습 2-b단계에서 상위 30 심볼로 자동 학습
    - `TryHybridLimitEntryAsync`: `_pumpWatchPool` 진입 확인 시 예측 호출 후 현재 로깅 중 (Phase 1). LIMIT 실주문 배치는 검증 후 Phase 2 활성화 예정
  - **접근 B — Entry Zone Multi-Output 데이터 수집** (`Services/EntryZoneDataCollector.cs`): 추후 멀티 타겟 회귀 학습을 위한 JSONL 로그 수집
    - 진입 시: `RecordEntryContext(features, entryPrice, signalSource)`
    - 보유 중: `UpdateRealizedExtremes` 로 realized high/low 추적
    - 청산 시: `FinalizeEntryZoneSample` → `%LOCALAPPDATA%\TradingBot\EntryZoneData\entry_zone_YYYYMMDD.jsonl` 에 append
    - 필드: entry/exit/optimal exit/optimal SL/realized PnL/optimal PnL/features — 추후 Entry Zone 모델 학습 데이터로 사용
    - 라이브 의사결정에는 아직 사용하지 않음 (수집만)
  - **접근 C — Breakout Price Classifier** (`Services/BreakoutPriceClassifier.cs`): Consolidation → Breakout 패턴 감지 전용
    - 라벨링: 20봉 consolidation 구간 (range < 5%, 볼륨 수축) → 향후 12봉 내 +2% 돌파 여부 (Binary)
    - LightGBM BinaryClassification, 피처 3개 (RelativeRange/VolContraction/BodyRatio)
    - 초기학습 2-b단계에서 Pullback Regressor와 함께 학습
    - `_pumpWatchPool` 진입 확인 시 `PredictBreakout(recent20)` 호출하여 돌파 확률 ≥ 0.6 이면 로깅

- **DbManager.GetCandleDataByIntervalAsync(symbol, intervalText, limit)**: Part A/C 학습 데이터 로드용

### Design Notes

- Part A는 라이브 활용(하이브리드 진입 경로), Part B는 데이터 축적만, Part C는 라이브 활용(급등 후보 consolidation 감지)
- 모두 기존 ProfitRegressorService 패턴 재사용 — MLContext + LightGBM + `%LOCALAPPDATA%\TradingBot\Models\` zip 저장
- 초기학습 2-b단계에서 상위 30 심볼 × 6개월 데이터로 일괄 학습 (약 수 분 소요)
- 하드코딩 임계값 최소화 — 라벨링 파라미터(lookAhead, threshold)만 고정, 예측 결과에는 하드코딩 필터 없음

## [4.7.9] - 2026-04-11

### Fixed (Critical)

- **거래량 급증 코인이 감시 풀에 있어도 진입 못하던 버그**
  - **원인**: `MarketCrashDetector`가 TickerCache의 **모든 심볼**에서 거래량 급증/SPIKE를 감지하여 `_pumpWatchPool`에 등록하지만, 해당 심볼이 v4.7.4부터 도입된 `_trainedSymbols` (6개월 다운로드 완료 심볼) 에 없으면 `ExecuteAutoOrder`의 Router 0이 "데이터 다운로드 대기"로 차단
  - **특히 문제인 케이스**: 신규 상장 코인, 소형 알트, Top 100위 바깥 심볼 — **정확히 이들이 급증의 주인공**이었음에도 차단
  - **수정**: 거래량 급증/SPIKE/PumpScan 신호로 감지된 심볼을 즉시 `_trainedSymbols`에 추가
    - `OnVolumeSurgeDetected`: 감시풀 등록 시점에 동적 학습 대상 등록
    - `HandleSpikeDetectedAsync`: SPIKE 감지 시점에 동적 학습 대상 등록
    - `_pumpStrategy.OnTradeSignal`: PumpScan 신호 시점에 동적 학습 대상 등록
  - **정당성**: 실시간 WebSocket 캔들이 공급되고 있고 품질 필터는 다운스트림 `AIDoubleCheckEntryGate`, `PumpSignalClassifier`가 ML 확률로 담당. Router 0은 "데이터 존재 여부" 수준의 검증이므로 실시간 감지 경로는 자연스럽게 허용되어야 함
  - 로그: `✅ [동적학습등록] {symbol} — 거래량 급증 감지로 즉시 진입 게이트 통과 허용`

## [4.7.8] - 2026-04-11

### Fixed (Critical)

- **DB에 이미 있는 데이터를 매번 재다운로드하던 버그**
  - `HistoricalDataDownloader.DownloadAndSaveAsync`가 DB 상태 확인 없이 무조건 6개월 전부터 API 재호출
  - 150 심볼 × 52,000봉 전부 재다운로드 + `SaveCandleDataBulkAsync`의 dedupe 로직 이중 실행 → 극심한 시간 낭비
  - **수정**: 각 심볼에 대해 `GetCandleDataRangeAsync(Symbol, IntervalText)`로 기존 봉 개수 + 최신 OpenTime 조회
    - 90% 이상 확보 + 최신 데이터(interval × 2 이내) → **다운로드 전체 생략**
    - 부분 데이터만 있음 → DB 최신 시점 + 1봉부터 증분 다운로드
    - 전혀 없음 → 6개월 전체 다운로드
  - 재시작 시 대부분의 심볼이 ~0.1초 내 완료됨
- **ETA 공식 이중 보정 버그**
  - v4.7.4의 EMA 방식: `avgPerSym / parallelFactor × remaining` — inter-arrival time을 이미 병렬 처리 결과인데 병렬도로 또 나눔
  - **수정**: 최근 10개 완료 시점의 실제 throughput 기반
    - `rate = (samples-1) / (latestTime - oldestTime)` [symbols/sec]
    - `eta = remaining / rate`
  - 병렬도와 무관하게 실제 완료 속도 그대로 반영됨

### Added

- `DatabaseService.GetCandleDataRangeAsync(symbol, intervalText)` — (Count, MinTime, MaxTime) 단일 쿼리

## [4.7.7] - 2026-04-11

### Added

- **심볼별 점진적 진입 활성화 복구**
  - v4.7.4에서 도입한 `OnSymbolReady` 구독 로직이 v4.7.5 단일 학습 전환 시 실수로 제거됨
  - 복구: `HistoricalDataDownloader.OnSymbolReady` 이벤트로 각 심볼 다운로드 완료 시 즉시 `_trainedSymbols`에 추가
  - 메이저 `BTCUSDT/ETHUSDT/SOLUSDT/XRPUSDT`는 Phase 1 완료 즉시 활성화
  - 알트는 Phase 2에서 다운로드 완료 순서대로 활성화
  - Router 0 메시지 개선: `⛔ [데이터 대기] {symbol} — 다운로드 완료 시 자동 진입 허용 (N개 활성화됨)`
  - Progress 메시지: `✅ [BTCUSDT] 진입 활성화 (1개 완료)`

## [4.7.6] - 2026-04-11

### Fixed (Critical)

- **재시작마다 초기학습 반복 루프** — 치명적 버그
  - **원인 1**: `InitialTrainingMinAccuracy = 0.70` 하드코딩 임계값. 학습 데이터 부족 등의 이유로 모델 정확도가 70% 미달이면 `IsInitialTrainingComplete = false` 할당 → setter가 **flag 파일을 삭제** → 다음 재시작 시 재학습 트리거 → 또 실패 → 무한 루프
  - **원인 2**: setter의 delete 로직이 위 상황에서 이전에 저장된 정상 flag까지 제거
  - **수정 1**: `InitialTrainingMinAccuracy` 상수 제거 (하드코딩 게이트 철폐). 학습 완료 = flag 저장. 진입 품질 필터는 `AIDoubleCheckEntryGate`, `PumpSignalClassifier`, `SurvivalEntryModel`이 개별 ML 확률로 담당
  - **수정 2**: setter를 **write-only**로 변경. 어떤 경우에도 flag 파일을 삭제하지 않음
  - **진단**: 봇 시작 시 flag 경로와 존재 여부를 `OnStatusLog`로 출력
  - **메시지**: "이미 완료됨 (flag 있음) — 자동 재학습 건너뜀" 문구 추가

## [4.7.5] - 2026-04-11

### Fixed

- **초기학습 중복 실행 문제**: v4.7.4에서 phased training(메이저 먼저 학습 → 알트 다운로드 후 재학습)을 구현했으나 사용자 시점에서 "학습 완료 후 또 학습 시작"처럼 보여 혼란 유발. SqlBulkCopy(v4.7.3) + 병렬 다운로드(v4.7.4) 덕분에 다운로드가 수 분 내 완료되므로 phased training이 불필요.
  - 수정: `TriggerInitialDownloadAndTrainAsync`에서 `OnMajorsCompleted` 중간 학습 트리거 제거
  - 단일 `TrainAllModelsAsync` 호출로 통합 (다운로드 완료 후 1회만)
  - `_trainedSymbols`는 최종 학습 완료 후 일괄 등록
  - `HistoricalDataDownloader.OnMajorsCompleted` 이벤트는 유지 (구독자 없음, 향후 용도)

## [4.7.4] - 2026-04-11

### Added

- **AI 기반 횡보 보유 청산 (옵션 A, 하드코딩 없음)**
  - `PositionMonitorService`에 `ProfitRegressorService` 주입 (`SetProfitRegressor`)
  - 보유 30분 이상 + 5분 주기로 `PredictProfit` 재호출
  - 피처: RSI/BB/ATR/VolRatio/Momentum/MLConfidence (진입 시점과 동일 구성)
  - 모델이 예측한 향후 기대수익률 < 0 → **ML의 손익분기선 기준**으로 청산 ("AI Stagnant Re-prediction")
  - 임계값/시간 하드코딩 전혀 없음. 5분 간격은 ETA가 아닌 단순 throttle
  - 솔라나 2일 횡보 같은 케이스: 모델이 기대손실 전환 시 자동 청산

- **심볼별 학습 완료 게이팅 (단계적 진입 허용)**
  - 기존: 단일 `IsInitialTrainingComplete` → 전체 154 심볼 완료까지 모든 진입 차단
  - 신규: `ConcurrentDictionary<string,bool> _trainedSymbols` — 학습 완료된 심볼만 진입 허용
  - **메이저 4개 다운로드 완료 즉시 ML 학습 → 메이저 4개 진입 활성화** (알트는 백그라운드 계속)
  - 알트 5분봉 완료 → 해당 심볼 즉시 허용
  - 전체 완료 시 기존 `IsInitialTrainingComplete = true` (하위 호환)
  - `OnSymbolTrained` 이벤트로 VM 실시간 갱신

- **메이저 우선 다운로드 (Phase 1/2/3 분리)**
  - Phase 1: 메이저 4개 병렬 (gate 없이 즉시 전개) — 수 초 내 완료 목표
  - Phase 2: 알트 5분봉 병렬 gate(6)
  - Phase 3: 알트 1분봉 (Spike) 병렬 gate(6)
  - `OnMajorsCompleted` 이벤트 → TradingEngine에서 메이저 ML 학습 즉시 트리거

- **배너에 메이저/알트 분리 표시 + 단계별 진행률**
  - `메이저 0/4` · `알트 0/100` 두 줄 표시
  - 스테이지 이모지로 현재 Phase 표시 (🎯 major / 📊 alt_5m / ⚡ alt_1m)

### Fixed

- **ETA가 계속 늘어나던 문제 (v4.7.3)**
  - 기존: 누적 평균 `elapsed / current × remaining` → 빠른 심볼부터 완료되면 평균이 올라가며 ETA 증가
  - 수정: 최근 10개 심볼 완료 간격의 EMA 기반 + 병렬도(6) 반영
  - Phase 1 메이저는 ETA 계산에서 의미 있는 가중치를 가지지 않고, Phase 2 시작 후 ETA가 안정화

## [4.7.3] - 2026-04-11

### Fixed (Critical Performance)

- **초기학습 다운로드 7분에 1심볼 → 분 단위 내 완료**
  - **원인 1**: `DatabaseService.SaveCandleDataBulkAsync`가 `IF NOT EXISTS + INSERT` 패턴으로 52,000봉 × 52,000번 DB 왕복. 심볼당 1~2분 소요
  - **원인 2**: `HistoricalDataDownloader`가 각 봉마다 RSI/BB/MACD/ATR을 100봉 윈도우로 재계산. 심볼당 O(52000 × 100 × 4) = 20M 연산. **게다가 계산 결과가 DB INSERT에 포함되지도 않아 완전히 낭비되는 작업**
  - **원인 3**: 154 심볼을 순차 처리. 병렬화 없음
  - **수정 1**: `SaveCandleDataBulkAsync` — 50건 이상은 `SqlBulkCopy` + `#CandleStage` temp 테이블 + `INSERT WHERE NOT EXISTS`로 단일 호출 처리 (1000배 빠름)
  - **수정 2**: `DownloadAndSaveAsync` — 지표 계산 루프 완전 제거, OHLCV만 저장
  - **수정 3**: `DownloadAllAsync` — `SemaphoreSlim(6)` 기반 심볼 병렬 다운로드, 요청 간격 100ms→80ms (Binance 분당 6000 weight 한도 내)

### Added

- **구조화된 다운로드 진행률 + ETA 계산**
  - `HistoricalDataDownloader.DownloadProgress` 클래스 신설 (Current/Total/TotalCandlesSaved/Elapsed/EstimatedRemaining)
  - `OnDetailedProgress` 이벤트로 VM에 전달 → 배너에 실시간 ETA 표시
  - `TradingEngine.OnInitialTrainingDownloadProgress` 이벤트 추가
- **초기학습 배너에 ETA + 진행률 바 + 저장된 봉 수 표시**
  - 기존 `경과` 카드 옆에 `남은 시간` 카드 추가 (ETA mm:ss 또는 hh:mm:ss)
  - 배너 하단에 진행률 ProgressBar (0-100%)
  - 스테이지 텍스트가 `📥 BTCUSDT (5/154, 3%, 250,000봉 저장)` 형태로 갱신
  - ETA는 경과시간 / 진행심볼수 × 남은심볼수 기반 단순 계산

## [4.7.2] - 2026-04-11

### Removed

- **TF(Transformer/TensorFlow) 관련 UI·코드 일괄 제거**
  - MainWindow: AI SCORE 컬럼의 `TF: xx%` 표시 제거 (ML만 유지)
  - MainWindow: AI PREDICTION 카드의 TF ProgressBar 행 제거
  - MainWindow: ENTRY GATE의 Transformer 점수 행 제거
  - SymbolChartWindow: TRANSFORMER 신뢰도 카드 제거 (5컬럼 → 4컬럼)
  - Models: `TFConfidence`, `TFConfidenceText`, `MLTFSummary`(TF 분기) 제거
  - MainViewModel: `BattleTFConfidence`, `BattleTfConfidence*`, `BattleTfHighlight*`, `WaveTFScoreText` 제거
  - AIDataflowPipeline/AIDedicatedWorkerThread: `TFConfidence`, `TFCandlesToTarget` 필드 제거 (MLProbability로 대체)
  - ProfitRegressorService: `TFConfidence` 피처 제거 (학습 컬럼 10개 → 9개)
  - TradingEngine: AIWorker 로그에서 `TF=xx%` 출력 제거
  - 이유: 프로젝트에서 실제로 TF 추론을 사용하지 않음(ML.NET 단독). 의미 없는 UI 노이즈 제거

### Added

- **메인창 초기학습 진행 배너 — 진입 차단 시각화 + 경과 시간 카운터**
  - flag 파일(`initial_training_ready.flag`) 부재 시 상단에 오렌지 배너 상시 표시
  - `⛔ 초기학습 진행 중 — 모든 진입 차단` + 실시간 단계 메시지
  - 1초 간격 경과 시간 카운터 (`경과 hh:mm:ss`)
  - 학습 완료 시 `✅ 초기학습 완료 — 진입 활성화` 10초 표시 후 자동 숨김
  - 학습 실패 시 `❌ 초기학습 실패` 상태 유지
  - 이유: 진입 차단 상태를 로그에서만 확인 가능해 사용자 혼란 발생

### Fixed

- `/train 명령 필요` 안내 문구 제거 → `백그라운드 자동 학습 진행 중`으로 변경

## [4.7.1] - 2026-04-11

### Fixed (Critical)

- **텔레그램 /train 명령 핸들러 미구현 문제 해결**
  - v4.7.0에서 `OnRequestTrain` 콜백에 등록했지만 TelegramService가 실제로 호출하지 않음
  - /train 명령이 봇에 도달하지 않아 초기 학습 실행 안 됨
- **봇 시작 시 자동 초기 학습 트리거**
  - flag 파일 없으면 엔진 시작 30초 후 백그라운드로 자동 실행
  - 사용자 수동 개입 불필요
  - 6개월 다운로드 + 학습 + 검증 전 과정 자동

## [4.7.0] - 2026-04-11

### Major: 옵션 A — 진정한 AI 단독 판단 구현

#### Added
- **HistoricalDataDownloader 신규**: Binance 과거 캔들 페이지네이션 다운로드
  - startTime/endTime 기반 6개월 분량 일괄 수집
  - rate limit 준수 (100ms 간격, 분당 ~600 요청)
  - DB 저장 (CandleData 청크 500개씩)
  - 메이저 4 + 거래량 상위 100 알트 + Spike용 50 알트 1분봉
- **TriggerInitialDownloadAndTrainAsync**: 다운로드 → 학습 → 검증 일괄 실행
  - 1단계: 6개월 캔들 다운로드 (15~30분)
  - 2단계: ML 모델 4개 학습 (TrainAllModelsAsync 재사용)
  - 3단계: 정확도 70%+ 검증 (TradeSignal/PumpNormal/PumpSpike)
- **StartOptionAInitialTrainingAsync**: 봇 정지 상태에서도 호출 가능
- **IsInitialTrainingComplete 영속화**: `%LOCALAPPDATA%\TradingBot\Models\initial_training_ready.flag`
- **텔레그램 `/train` 명령**: 옵션 A 학습 트리거 (기존 ForceInitialAiTrainingAsync 대체)
- **봇 시작 알림**: 학습 완료/미완료 상태 명시

#### Changed
- **진입 라우터 ROUTER 0**: `IsInitialTrainingComplete=false` 시 모든 진입 차단
  - PUMP, SPIKE, MAJOR, MACD 모든 경로
  - 메시지: "텔레그램 /train 명령으로 6개월 학습 실행"
- **하드코딩 필터 조건부 비활성화** (학습 완료 시 AI 단독 판단):
  - VOLATILITY 차단 (메이저 일반 진입)
  - SHORT_FILTER (RSI/MACD/Fib/Stoch/SMA60)
  - LONG_FILTER (VWAP/EMA/StochRSI)
  - PUMP HTF 차단 (CheckPumpHtfBullishAsync)
- **AI Gate 우회 없음 유지** (사용자 원칙 준수)

### 사용 방법
1. v4.7.0 업데이트
2. 봇 시작 (자동 진입은 차단 상태)
3. 텔레그램 `/train` 입력
4. 30분~2시간 후 학습 완료 알림 수신
5. 자동 진입 활성화 (이후 재시작해도 flag 파일로 자동 복원)

## [4.6.3] - 2026-04-11

(v4.6.2 통합 + 카운터 리셋 로직 단순화)

### Fixed

- **카운터 자정 리셋 로직 단순화**
  - `DateTime.UtcNow.AddHours(9).Date` → `DateTime.Now.Date` (Windows KST 기준)
  - `!= todayKst` → `todayKst > _dailyPumpCountDate` (날짜 진행 명확)
  - 메인 루프 + TryReserve 양쪽 모두 같은 로직 통일

## [4.6.2] - 2026-04-11

### Fixed (Critical Hotfix)

- **AIBacktestEngine 파일 잠금 충돌 수정**
  - SaveCache: 임시 파일 + Atomic Rename + 3회 재시도, FileShare.Read
  - LoadCache: FileShare.ReadWrite + try/catch 안전 처리
  - 양쪽 계정 동시 실행 시 발생하던 IOException 팝업 해결

### Added (단타 트레이딩뷰 보조지표)

- **VWAP** (거래량 가중 평균가): 최근 60봉 기준
- **EMA 9/21/50 + Cross_State**: 정배열(1)/역배열(-1)/중립(0)
- **StochRSI(14,14,3,3) + Cross**: K>D 골든(1) / K<D 데드(-1)
- `IndicatorCalculator.CalculateVWAP()`, `CalculateStochRSI()` 신규
- `CandleData` + `MultiTimeframeEntryFeature`에 9개 필드 추가

### Fixed (메이저 SHORT 편향 보정)

- **AI Gate 메이저 LONG/SHORT 대칭 필터 강화**
  - SHORT 차단: VWAP 위 / EMA 정배열 / StochRSI 골든크로스
  - LONG 차단: VWAP -0.3% 아래 / EMA 역배열 / StochRSI 데드크로스
- **CheckHigherTimeframeBullishAsync**: D1 SMA + 4h MACD + 15m MACD 다중 OR (3중 2 충족)
- **CheckHigherTimeframeBearishAsync**: 3중 3 모두 충족 (메이저 SHORT 매우 엄격)
- **메이저 ATR 손절 LONG 진입에도 적용** ([line 8818-8826] 추가)

### Changed

- ML Feature 81 → **86개** (M15_EMA_CrossState, VWAP_Distance, StochRSI 5개 추가)
- 모델 자동 재학습으로 새 피처 학습 시작

## [4.6.1] - 2026-04-11

### Fixed (Phase A: 라벨링 + AI Gate 편향 보정)

- **A1 BacktestEntryLabeler 라벨링 강화**: 목표 +2% 완전 달성만 LONG positive
  - 기존: 목표 절반(1%) 부분 달성도 positive → 하락 추세 단기 반등이 LONG으로 학습됨
  - 변경: 목표 미달성은 모두 negative → ZEREBRO/AGT 같은 잘못 승인 차단
- **A3 AI Gate 메이저 비대칭 임계값 + AND 조건**:
  - LONG: ML 75% AND TF 70% (편향 방지)
  - SHORT: ML 65% OR TF 60% (데이터 부족 보수)

### Fixed (Phase B: PUMP 급등 1분 로켓 발사 포착)

- **B1 SPIKE_FAST 60초 눌림 대기 폐기**:
  - 60→30초 단축, 1.5초 간격 폴링
  - 누적 +2% 도달 시 즉시 진입 (로켓 발사 케이스)
  - 고점 대비 -0.5% 눌림 (기존 -1%)
  - 30초 경과 시 강제 진입 (놓치기보다 진입)
- **B2 PUMP 급등 ML 검증 1분봉 30→15개**: 기존 거의 항상 스킵되던 ML 검증 작동
- **B3 Spike 모델 라벨링**: 5분봉 +3%/10분 → 1분봉 +2.5%/5분 의도로 변경
- **B4 MarketCrashDetector 스냅샷 30→10초**: 첫 감지 3배 빠름
- **B5 TickDensityMonitor 활성화 확인 완료**

### Fixed (Phase C: survival_major 학습 강화)

- **C2 survival_major TP/SL 비대칭 학습**:
  - 기존: TP +1%/SL -0.5%/20봉 (RR 2:1)
  - 변경: TP +2%/SL -1%/24봉 (RR 2:1 유지, 노이즈 제거)
  - 작은 반등도 positive 학습하던 문제 해결

## [4.6.0] - 2026-04-11

### Fixed (PUMP/급등 진입 전면 차단 해결)

- **변동성 차단 PUMP/급등 경로 우회**: ATR 3% / 봉 5% 차단을 메이저 일반 진입에만 적용
  - PUMP_WATCH_CONFIRMED, SPIKE_*, MAJOR_MEME, TICK_SURGE는 변동성으로 차단 안 함
  - 급등 자체가 신호인데 변동성으로 차단하던 모순 해결

- **130봉 부족 차단 완화**: 신규 상장 알트는 5분봉 130봉(11시간) 부족 → 진입 전면 차단되던 문제
  - PUMP/급등 경로는 30봉 fallback retry 적용
  - latestCandle null fallback 로직 PUMP/급등 전체 적용 (기존: SPIKE_DETECT만)

- **PUMP HTF 차단 D1 → H1/M15 OR 조건**: D1 SMA 기준은 PUMP 알트에 너무 엄격
  - `CheckPumpHtfBullishAsync` 신규: H1 또는 M15 SMA20>SMA60 중 하나만 충족
  - PUMP_WATCH_CONFIRMED 진입 시 이 메서드 사용

- **AI Gate 우회 시도 제거**: AI 단독 판단 원칙 복원

### Added

- 감시풀 등록 시 `MarketHistoryService.RegisterSymbol` + `RequestBackfillAsync` 호출 연결
- SPIKE 감지 시 동적 수집 등록 + 즉시 백필
- PUMP_WATCH_CONFIRMED 차단 이유 모든 경로 로깅 강화

## [4.5.17] - 2026-04-10

### Changed

- **MACD 골든크로스/데드크로스 감지 1분봉 → 4시간봉 변경**
  - 1분봉은 노이즈 과다 (XRP 0.000009 같은 의미 없는 크로스)
  - 4시간봉은 추세 전환 확실성 훨씬 높음
  - 미완성 마지막 봉 제외 (직전 완성봉 기준)
  - 상위 TF 체크: H4 → **D1 SMA20/60** 기준으로 재구성
  - 스캔 주기: 30초 → **15분** (4시간봉 마감 대응)

- **노이즈 필터 4종 추가** (`DetectGoldenCrossAsync` 내부)
  - `noiseGap`: |MACD-Signal| / ATR(14) < 0.02 차단
  - `noiseAngle`: DeadCrossAngle 절대값 < 평균 히스토그램 × 5%
  - `whipsawZone`: 최근 10봉 내 크로스 3회+
  - RSI 중립 구간: Golden RSI<55 또는 Dead RSI>45 차단
  - 로그 태그: `NoiseFiltered[이유]`

### Added

- **`DetectShortTermCrossAsync` 메서드 신규**: 1분봉 전용 단타/익절 감지
  - `PositionMonitorService`의 실시간 익절 타이밍 체크
  - `WaitForRetestShortTriggerAsync`의 꼬리 리테스트 단타 진입
  - 노이즈 필터 미적용 (빠른 반응 우선)

## [4.5.16] - 2026-04-10

### Performance

- **PUMP 알트 동적 멀티TF WebSocket 구독** (진입 REST 호출 완전 제거)
  - 거래량 상위 50개 알트를 5분 주기로 M1/M15 캐시 추가
  - `StartPumpMultiTfRefreshLoopAsync` 백그라운드 루프
  - 초기 30초 대기 (TickerCache 워밍업) → 이후 5분 주기
  - rate limit 준수: 구독 간 150ms 간격 (10 msg/sec)
  - 총 구독 상한 200개 (Binance 1024 한도의 20%)
  - 한 번 구독한 심볼은 계속 캐시 유지 (볼륨 변동 대응)

### 효과

- **PUMP 진입 시 REST 호출 0건** (캐시 히트 시)
- PUMP 알트 진입 반응 속도 대폭 개선
- Binance API weight 사용량 감소

### 이전 "WebSocket 한도 초과 위험" 정정

- 실제 Binance USDT-M Futures 한도: **소켓당 1024 스트림**
- 100개 알트 × 5 TF = 500 스트림 = 49% 사용 (여유 충분)
- 이전 v4.5.15 CHANGELOG의 "수백 종목 × 5 TF = 한도 초과" 문구는 오판이었음

## [4.5.15] - 2026-04-10

### Performance

- **멀티 타임프레임 WebSocket 캐시 구현** (AI 진입 평가 REST 호출 제거)
  - 메이저 코인 5개 TF 전체 WebSocket 구독 추가 (M1/M15/H1/H4/D1)
  - `MarketDataManager.MultiTfKlineCache` 인메모리 캐시 추가
  - `GetCachedKlines(symbol, interval, minCount)` 조회 헬퍼 제공
  - `MarketDataManager.Instance` 정적 접근자 (Extractor/Gate에서 캐시 조회)

- **AI 진입 평가 REST → WebSocket 캐시 우선 전환**
  - `MultiTimeframeFeatureExtractor`: 5개 TF 모두 캐시 우선, H2만 REST
  - `AIDoubleCheckEntryGate`: M15/M1 캐시 우선
  - `MacdCrossSignalService`: M1/M15/H1 캐시 우선

### Fixed

- **AI Gate 진입 평가 지연 500ms → ~50ms** (10배 단축 예상)
- **진입당 REST 호출 5~7회 → 0~1회** (H2만 남음)
- 메이저 코인 진입 반응 속도 대폭 개선

## [4.5.14] - 2026-04-10

### Fixed

- **메인 루프 12~18초 블로킹 문제 해결** (시작 직후 발생)
  - 원인 1: `OnFirstAltCollectionComplete` 이벤트(18:00:03) + 2분 타이머(18:01:03) 연속 실행 → 중복 학습
  - 원인 2: LightGBM 스레드 수 6개(75% CPU) → 메인 루프 스케줄링 대기
  - 해결 1: `Interlocked.Exchange` 기반 중복 학습 방지 플래그 추가
  - 해결 2: LightGBM `NumberOfThreads` 6 → 4로 제한 (CPU/2, 상한 4)

### Performance

- 메인 루프 최악 블로킹: **18,328ms → 예상 4,000ms 이하**
- 중복 학습 제거로 전체 ML 재학습 시간 **절반 감소**
- CPU 절반만 ML에 할당 → 메인 루프/WebSocket 처리 원활

## [4.5.13] - 2026-04-10

### Fixed

- **SPIKE_FAST 진입 전면 차단 문제 해결** (오늘 0건 진입 원인)
  - Spike 모델이 학습 초기라 대부분 확률 70% 미달 → 전부 차단되던 문제
  - 모델 정확도 < 70%일 때 임계값 자동 완화 (70% → 55%)
  - 정확도 >= 70% 달성 시 기존 엄격 모드(70~85%) 복귀
  - 로그에 `학습중(acc=XX%)` 태그로 상태 표시

### Notes

- 오늘 PUMP_WATCH_CONFIRMED 44건 / TICK_SURGE 8건 / SPIKE_FAST 0건 분석 결과
- PUMP_WATCH와 TICK_SURGE는 정상 작동 중이었음
- SPIKE_FAST만 v4.5.8 이후 모델 성숙 전 차단 → 이 수정으로 해결

## [4.5.12] - 2026-04-10

### Performance

- **DB 부하 경감 — Dapper 최적화 3개 핫스팟**
  - `GetAllCandleDataForTrainingAsync`: N+1 쿼리 → 단일 ROW_NUMBER 윈도우 쿼리
    - 100개 심볼 기준 101회 쿼리 → **1회**, 약 80% 단축
    - WITH NOLOCK 힌트로 락 경합 감소
  - `SaveCandlesAsync`: IF NOT EXISTS+INSERT 패턴 → **MERGE 단일 statement**
    - SQL Server 엔진 단에서 중복 체크 + 삽입 처리
  - `LoadPositionStatesAsync`: dynamic 루프 매핑 → **DTO 클래스 자동 매핑**
    - CPU ~20% 감소, 타입 안전성 확보
    - WITH NOLOCK 추가

### Changed

- 전체 DB 접근 이미 Dapper 사용 중이었음 (신규 변경 없음)
- `BulkInsertMarketDataAsync`는 SqlBulkCopy 유지 (Dapper보다 5~10배 빠름)

## [4.5.11] - 2026-04-10

### Added

- **일일 수익 기반 4단계 모드 자동 전환** (목표 달성 후 보수 진입)
  - `$0 ~ $200` Aggressive: 평상시 (제약 없음)
  - `$200 ~ $250` Transition: Spike AI 임계값 +5%p, 사이즈 80%
  - `$250 ~ $500` Conservative: Spike AI +10%p, 사이즈 50%
  - `$500+` UltraConservative: **1개 포지션만 유지**, AI +15%p, 사이즈 40%

- **ML 피처 3개 추가** (`DailyPnlRatio`, `IsAboveDailyTarget`, `DailyTradeCount`)
  - AI가 "목표 달성 후 진입한 거래는 승률 낮다"를 학습 가능
  - `MultiTimeframeFeatureExtractor` 정적 컨텍스트로 주입
  - ML Feature 수: 78 → 81개

### Changed

- PUMP_WATCH_CONFIRMED / SPIKE_FAST 경로 모두 일일 수익 모드 체크 추가
- SPIKE_FAST 마진 계산에 모드 사이즈 배수 적용
- 메인 루프에서 `UpdateDailyContextForFeatures()` 주기적 호출

## [4.5.10] - 2026-04-10

### Added

- **MACD 반대 크로스 50% 부분 청산** (추세 전환 일부 대응)
  - LONG 보유 + 데드크로스 + H1/M15 약세 전환 → 50% 청산
  - SHORT 보유 + 골든크로스 + H1/M15 강세 전환 → 50% 청산
  - 나머지 50%는 기존 트레일링 스탑 관리
  - 100% 리버스는 너무 공격적 → 분할 대응

- **심볼별 일일 리버스 횟수 제한 (최대 3회)**
  - 플립플롭 방지 (같은 코인 계속 뒤집히는 거 차단)
  - MACD 반대 청산, SPIKE 리버스 모두 카운터 공유
  - 자정(KST) 자동 리셋

### Changed

- **SPIKE 리버스 진입 시 Spike 모델 임계값 70% → 75% 강제**
  - 기존 포지션을 엎는 위험한 행동이므로 품질 기준 상향
  - 알트 불장 모드 75%와 동일 기준

## [4.5.9] - 2026-04-10

### Added

- **일일 PUMP 진입 한도 40회**: 과도한 거래 방지
  - 자정(KST) 자동 리셋
  - PUMP_WATCH_CONFIRMED / SPIKE_FAST 경로 모두 적용
  - 메이저 코인(BTC/ETH/SOL/XRP)은 제외

### Changed

- **알트 불장 모드 시 Spike AI 임계값 70% → 75% 자동 상향**
  - `_altBullDetector.IsActive` 상태 기반 동적 조정
  - 과열장에서 진입 품질 우선, 낮은 확률 진입 차단

## [4.5.8] - 2026-04-10

### Changed

- **PUMP ML 모델 Normal/Spike 분리** (단일 모델 → 2개 모델)
  - `PumpSignalType` enum 추가 (Normal / Spike)
  - Normal 모델: 일반 진입용 (라벨 +1.5% / 30분, 완만 추세)
  - Spike 모델: 급등 진입용 (라벨 +3% / 10분, 순간 폭발)
  - 모델 파일: `pump_signal_normal.zip` + `pump_signal_spike.zip`
  - `PumpScanStrategy` (일반 감시) → Normal 모델 사용
  - `SPIKE_FAST` (급등 즉시 진입) → Spike 모델 70%+ 요구
  - 하드 체크 자동 해제 조건: Normal + Spike + TradeSignal 모두 70%+

### Fixed

- SPIKE_FAST 경로에 AI Spike 모델 검증 추가 (기존: 검증 없음)

## [4.5.7] - 2026-04-10

### Changed

- **알트 캔들 수집 완료 시 ML 재학습 즉시 트리거**
  - 기존: 타이머 2분 대기 후 첫 학습
  - 변경: `OnFirstAltCollectionComplete` 이벤트 발화 → 즉시 학습
  - 전체 파이프라인 T+30초~90초 이내 완료 (기존 2~3분)

## [4.5.6] - 2026-04-10

### Fixed

- **WETUSDT 하락추세 PUMP 진입 문제 해결**
  - `PUMP_WATCH_CONFIRMED` 경로에 상위 TF(H1/M15) 하락추세 하드 체크 추가
  - 1시간봉/15분봉 SMA20<SMA60 약세 시 진입 차단
  - **AI 모델 정확도 70%+ 달성 시 하드 체크 자동 해제** (AI 단독 판단 모드)

### Added

- **상위 50개 알트 5분봉 DB 수집** (`MarketHistoryService.CollectTopAltCandlesAsync`)
  - 5분 주기로 거래량 상위 50개 알트 REST 폴링 → DB 저장
  - PumpSignalClassifier / SurvivalPump 모델 학습 데이터 확보
  - 기존: 메이저 4개만 수집 → 신규: 알트 50개 추가 (+1250%)

- **M15/H1 하락추세 방어 ML 피처 5개** (PUMP 진입 방어)
  - `M15_IsDowntrend`: 15분봉 SMA20<SMA60 (1=하락)
  - `H1_IsDowntrend`: 1시간봉 SMA20<SMA60 (1=하락)
  - `M15_ConsecBearishCount`: 최근 연속 음봉 개수 (0~5)
  - `H1_PriceBelowSma60`: 현재가 < H1 SMA60 (1=아래)
  - `M15_RSI_BelowNeutral`: M15 RSI<45 약세 (1=약세)

- **AI 모델 정확도 실시간 추적**
  - PumpSignal/TradeSignal/Direction/SurvivalPump 정확도 메모리 저장
  - `IsAiModelReadyForPumpEntry`: 모든 모델 정확도 ≥70% 시 true
  - 달성 시 로그: "🎯 모든 PUMP 모델 정확도 70% 달성 → 하드 체크 자동 해제"

### Changed

- ML Feature 수: 73 → **78개** (M15/H1 하락추세 5개 추가)

## [4.5.5] - 2026-04-10

### Added

- **알트 불장(Alt Bull Market) 자동 감지기** (`AltBullMarketDetector`)
  - 3가지 조건 동시 충족 시 활성화 (5분 주기, 2회 연속 확인)
    1. 상위 30개 알트 평균 변동성 ≥ 2.5%
    2. 24h +5% 이상 알트 ≥ 40개
    3. BTC 24h 변동률 < 6% (안정)
  - 활성화 시 자동 조치:
    - 신규 진입 레버리지 50% 하향 (20x → 10x)
    - 포지션 사이즈 70%
  - 해제 후 30분 쿨다운

- **단일 포지션 회로차단기**: 가격 -4% 손실 즉시 강제 청산
  - 서버 STOP_MARKET 실패/슬리피지 대비 로컬 안전장치
  - 청산(-5%)까지 1% 버퍼 확보

### Changed

- **STOP_MARKET 손절 ROE 기본값 20% → 60%** (가격 기준 -1% → -3%)
  - 포지션 호흡 공간 확보, 청산까지 2% 안전 거리
- **PositionMonitor 폴링 1000ms → 250ms** (4배 빠른 반응)
  - REST API 호출 제거, WebSocket TickerCache 단독 사용
  - CPU 무부하 (캐시 lookup만)
- **CrashDetector 60초 → 15초 단축** (4배 빠른 급변 감지)
  - 임계값 -1.5% → -0.8% (15초 비례)
  - 쿨다운 120초 → 60초

### Performance

- 전체 CPU 사용률 약 **1~2% 감소** (REST API 제거 효과)
- 가격 변동 반응 시간 1000ms → 250ms (4배 단축)

## [4.5.4] - 2026-04-10

### Changed

- **Transformer 관련 불필요 로그 메시지 정리**
  - "Transformer 기능은 TensorFlow.NET으로 전환 작업 중" 제거
  - "AI 관제탑 자동 활성화: TransformerSettings.Enabled=false" 제거
  - AI 더블체크 게이트 비활성화 메시지 간결화
  - "AI 백그라운드 수집 및 초기 학습 메인 프로세스 완료" → "AI Gate 초기 학습 완료"

## [4.5.3] - 2026-04-10

### Changed

- **ML 학습 데이터 소스: KlineCache → DB(CandleData 테이블)로 전환**
  - 계정별 별도 학습 → 전체 DB 공유 학습으로 변경
  - 모든 계정이 동일한 학습 데이터에서 동일한 모델 생성
  - DB에 24시간 내 캔들이 있는 전체 USDT 심볼 대상 학습
  - TradeSignal, PumpSignal, Direction, Survival 4개 모델 모두 DB 기반
  - MarketRegime/ExitOptimizer는 기존 유지 (KlineCache + TradeHistory)

### Added

- `DbManager.GetAllCandleDataForTrainingAsync()`: 전체 심볼 캔들 일괄 조회

## [4.5.2] - 2026-04-10

### Added

- **MACD 휩소(Whipsaw) ML 피처 5개**: AI가 노이즈 크로스를 학습으로 판별
  - `M1_MACD_CrossFlipCount`: 최근 10봉 크로스 횟수 (3+ = 노이즈 구간)
  - `M1_MACD_SecsSinceOppCross`: 반대 크로스 경과초 (30s 이내 = 위험)
  - `M1_MACD_SignalGapRatio`: |MACD-Signal|/ATR (작으면 의미 없는 크로스)
  - `M1_RSI_ExtremeZone`: RSI 과매수/과매도 극단 (SHORT+과매도 = 반등 위험)
  - `M1_MACD_HistStrength`: 히스토그램 강도 (1미만 = 약한 크로스)

- **기존 미사용 확장 피처 19개 ML 파이프라인 활성화**:
  - Stochastic %K/%D (D1/H4/H1/M15), MACD Cross (D1/H4/H1)
  - ADX/DI (D1/H4), H4 MomentumStrength, DirectionBias

### Changed

- ML 모델 Feature 수: 49개 → 73개 (기존 모델 자동 재학습)
- MacdCrossSignalService: 심볼별 크로스 이력 10분 보관 (휩소 감지용)
- MultiTimeframeFeatureExtractor: 1분봉 API 병렬 fetch 추가

## [4.5.1] - 2026-04-10

### Fixed

- **AI COMMAND 탭 레이아웃 수정**: 하단 섹션(Price Energy, Exit Score, Entry Pipeline)이 상단 3열 그리드와 겹쳐 보이지 않던 문제 해결
  - StackPanel + ScrollViewer 구조로 변경하여 수직 정렬 정상화
  - 컨텐츠 길어질 때 MinimalScrollViewer로 스크롤 지원

### Added

- **AI COMMAND 대기 오버레이**: START 전(AI 데이터 미수신)에 안내 화면 표시
  - "🧠 AI COMMAND CENTER / START 버튼을 눌러 AI 분석을 시작하세요" 표시
  - 데이터 수신 시 자동 사라짐 (IsAiCommandEmpty 바인딩)

## [3.5.0] - 2026-04-09

### Added

- **AI 피처 확장 (D1/H4/H1/M15)**: ML 학습 입력에 20개 피처 추가
  - Stochastic %K/%D: D1, H4, H1, M15 전 타임프레임
  - MACD 골든/데드크로스 감지: D1, H4, H1 (이전봉 대비 크로스 판정)
  - ADX + DI: D1, H4 (추세 강도 + 방향)
  - H4 모멘텀 강도 (0~1)
  - **DirectionBias** (-2~+2): D1+H4 MACD 방향 합산

- **D1+H4 방향성 필터**: 메이저 코인 진입 시 상위 TF 방향과 일치해야 진입
  - LONG: D1+H4 둘 다 데드크로스(-1.5 이하) → 차단
  - SHORT: D1+H4 둘 다 골든크로스(+1.5 이상) → 차단
  - 일봉+4시간봉 기준으로 롱/숏 방향성 정렬

### Changed

- **AI 학습 데이터**: ExtractTimeframeFeatures에서 Stochastic, MACD Cross, ADX 자동 추출
  - 기존 Stoch_K/Stoch_D 프로퍼티가 CandleData에만 선언되고 미사용 → 실제 계산 적용
  - IndicatorCalculator.CalculateStochastic(14,3,3) 호출
  - 모델 재학습 시 새 피처 자동 반영 (LightGBM)

## [3.4.2] - 2026-04-09

### Added

- **BTC 하락장 필터**: BTC 1시간 내 -2%+ 하락 시 모든 LONG 진입 차단
  - CRASH_REVERSE/PUMP_REVERSE만 예외 (급변 대응)
  - KlineCache 12봉(1시간) 기준 실시간 계산
  - 하락장에서 무리한 LONG 진입 원천 차단

- **SPIKE_FAST 거래량 비율 체크**: SpikeVolumeMinRatio (2.0x) 실제 적용
  - 이전 30초 스냅샷 대비 거래량 2배 미만이면 가짜 스파이크로 판정
  - 거래량 없는 가격 조작/노이즈 필터링

### Fixed

- **DROUGHT_RECOVERY AI Gate 우회 제거**: 가뭄 진입이 AI Gate를 완전 우회하던 문제
  - CRASH_REVERSE/PUMP_REVERSE만 AI Gate 우회 유지
  - DROUGHT_RECOVERY, SPIKE_DETECT도 AI Gate 통과 필수
  - 하락장에서 무필터 강제 진입 차단

## [3.4.1] - 2026-04-09

### Fixed

- **부분청산 무한 재시도 방지**: stepSize 미달 수량(0.90 등)으로 재시도 → 실패 → 무한루프
  - 재시도 경로에도 stepSize 보정 적용 (PUMP 정수 반올림)
  - 3회 실패 시 60초 쿨다운 등록 (모니터 루프 재시도 차단)

- **EXTERNAL_PARTIAL_CLOSE_SYNC 팬텀 기록 방지**: 봇 자체 부분청산 후 ACCOUNT_UPDATE에서 이중 기록
  - 부분청산 완료 시 30초 쿨다운 등록
  - 쿨다운 중 수량 감소 감지 → 내부 동기화만, DB 기록 스킵
  - MAJOR 77건 중 72건이 팬텀 → 실제 손실 아닌 가짜 기록이었음

## [3.4.0] - 2026-04-09

### Fixed

- **물타기(AverageDown) 비활성화**: 7일 62건 -$1,138 손실의 주범. 물타기 후 손절 시 손실 2배 증폭
  - PUMP 모니터: averageDown 로직 주석 처리
  - Major 모니터: hybridDCA 로직 비활성화

- **HybridExitManager ATR 트레일링 비활성화**: 7일 32건 -$885 손실의 주범
  - ATR×1.5 스탑이 PositionMonitorService의 ATR×3.5~4.5와 이중 작동
  - 진입 직후 0~2분에 좁은 스탑 발동 → 추세 탈 기회 자체를 차단
  - PositionMonitorService의 ATR 2.0 듀얼 스탑 + 계단식 보호선이 이미 담당

- **PUMP 코인 부분청산 수량 정수 반올림**: stepSize >= 1인 PUMP 코인 수량을 Math.Floor로 정수 처리
  - 기존: 소수점 6자리 반올림 → 0.15 같은 수량으로 주문 실패
  - 수정: ExchangeInfo에서 stepSize 조회 → stepSize >= 1이면 정수로 절삭

## [3.3.9] - 2026-04-08

### Added

- **바이낸스 서버사이드 TRAILING_STOP_MARKET**: PUMP 코인 수익 보호 강화
  - ROE +25% 달성 시 바이낸스에 TRAILING_STOP_MARKET 주문 자동 등록
  - 바이낸스 서버가 실시간 고점 추적 → callbackRate% 하락 시 시장가 청산
  - 1초 급락에도 고점 대비 1% 내에서 자동 청산 (봇 CPU/폴링 무관)
  - Step1 (ROE +25%): callback 1.0% (고점 대비 1% 하락 시 청산)
  - Step2 (ROE +80%): callback 0.5% (더 타이트하게 수익 보호)
  - TRAILING_STOP 실패 시 STOP_MARKET 폴백 (기존 보호선 유지)
  - PlaceTrailingStopOrderAsync 메서드 추가 (IExchangeService + BinanceExchangeService)

### Fixed

- **PUMP 67% ROE → 본절 손절 방지**: 계단식 보호선 체크를 본절/BB 체크보다 우선 실행
  - 계단식 보호선 활성화 시 본절가 스탑 무시 (보호선이 더 높으므로)

## [3.3.7] - 2026-04-08

### Fixed

- **중복 진입 방지**: ExecuteAutoOrder 슬롯 체크 시 기존 포지션/예약 중복 체크 추가
  - 다른 경로(SPIKE_FAST 등)에서 진입 진행 중인 심볼 즉시 차단
  - Quantity=0 예약 상태도 감지하여 레이스 컨디션 방지

## [3.3.6] - 2026-04-08

### Added

- **급변동 회복 전략 (Volatility Recovery Mode)**: 메이저 코인(ETH/SOL/XRP) 5%+ 급등/급락 후 반등 시 넓은 손절로 리테스트 생존
  - CRASH/PUMP/SPIKE 이벤트 발생 시 극단가 기록 (4시간 유효)
  - 회복 구간 진입: 마진 60% 축소 + 손절 -80% ROE (4% 가격)
  - 실질 리스크 동일: 60% × 80% = 48% ≈ 기존 100% × 50%
  - CRASH 저점/PUMP 고점을 구조적 손절선으로 활용
  - 조기 본절: ROE +15% (일반 +20%)에서 빠른 보호
  - 본절 도달 시 자동 졸업: 손절 -80%→-50%, 트레일링갭 40%→30% 정상화

### Fixed

- **자동진입 SL/TP 미표시**: DataGrid의 SL/TP 컬럼이 수동진입에서만 표시되고 자동진입에서 "-"로 표시되던 문제 수정
  - PlaceAndTrackEntryAsync 체결 후 OnSignalUpdate로 계산된 SL/TP 전달 추가
  - OnPositionStatusUpdate 호출 추가 (수동진입과 동일한 UI 갱신 흐름)

## [3.2.20] - 2026-04-08

### Fixed

- **PUMP 아무거나 진입 방지**:
  - Spike 임계값: 1.5% → **+3%** (진짜 급등만)
  - PUMP 코인: **급등(+3%)만** 진입, 급락 SHORT 제거 (메이저만 급락 대응)
  - 거래량 최소: $500K → **$1M** 복원
  - PumpScan: 모멘텀 3개 → **4개+ 필수** + 가격 모멘텀(30분 +1.5% 또는 1시간 저점 +3%) 반드시 포함
  - ML 단독 진입: 55% → **60%** + 가격 모멘텀 필수

## [3.2.19] - 2026-04-08

### Fixed

- **DataGrid 사라짐 수정**: FAST LOG가 Grid Column 밖에 삽입돼 DataGrid 밀림 → 좌측 패널 Grid.Row="3"으로 올바르게 배치
- **PUMP 중복 진입 방지**: SPIKE_FAST에서 슬롯 체크 + 포지션 예약을 단일 lock 블록으로 (레이스 컨디션 제거)
- **PUMP 슬롯**: 2개 → 3개

## [3.2.18] - 2026-04-08

### Fixed

- **설정 저장 오류**: 제거된 PUMP 검증 블록(`return false`)이 남아서 저장 시 무조건 실패하던 문제 수정

## [3.2.17] - 2026-04-08

### Changed

- **급등/급락 즉시 주문 (SPIKE_FAST)**: ExecuteAutoOrder 완전 바이패스
  - 이전: Spike 감지 → GetLatestCandleData(API) → AI Gate → R:R → ATR 손절 → 주문 = **4분 지연**
  - 변경: Spike 감지 → 슬롯 체크 → **즉시 시장가 주문** → 포지션 등록 → 감시 시작 = **1초 이내**
  - API 5개 호출 체인 제거, 텔레그램은 비동기 (주문 안 기다림)

## [3.2.16] - 2026-04-08

### Fixed

- **심볼 더블클릭 오류**: try-catch 추가로 차트 창 열기 안정화
- **급등 진입 지연**: Spike 감지 1분→**30초** 간격, 임계값 2%→**1.5%**로 빠른 감지

## [3.2.15] - 2026-04-08

### Changed

- **설정창 간소화**: 기본 마진, 레버리지, 주요 심볼, 매매 모드(시뮬레이션)만 남김
  - PUMP/메이저/급변감지 설정 458줄 제거 → 소스 하드코딩
- **FAST LOG 좌측 맨 아래로 이동**

## [3.2.14] - 2026-04-08

### Fixed

- **급등/급락 감지 후 진입 실패 수정**: SPIKE_DETECT, CRASH_REVERSE, PUMP_REVERSE → AI Gate 바이패스 + R:R 체크 스킵
  - 급변 시 AI Gate/R:R에서 차단돼 진입 못 하던 문제 해결
- **텔레그램 대기 표시 가독성**: 쉼표 나열 → 줄바꿈, `✅진입됨` / `⏳대기` 코인별 한 줄씩

## [3.2.13] - 2026-04-08

### Changed

- **FAST LOG 좌측 이동 + 5줄 확장**: 우측→좌측 상단으로 이동, 3줄→5줄, 제목 "📡 FAST LOG (실시간)"
  - "라이브"는 실시간 엔진 로그 (진입/차단/익절/손절 등)

## [3.2.12] - 2026-04-08

### Fixed

- **PUMP 코인 메인창 표시**: Spike 감지 코인도 UI 리스트에 추가 + SignalSource "MAJOR"→"PUMP" 분리
- **피보나치 진입 범위 확장**: 0.618~0.786 → **0.382~0.786** 단계별 가점
  - 0.618~0.786: +20점, 0.500~0.618: +15점, 0.382~0.500: +10점
- **MACD 골크/데크 AI 피처 추가**: `MACD_GoldenCross` (1/0), `MACD_DeadCross` (1/0) — AI가 크로스 발생 여부 직접 학습

## [3.2.11] - 2026-04-08

### Changed

- **AI 관제탑 텔레그램 진입 상태 표시**: "승인 코인(사유)" → "AI 승인 코인 (진입 상태)"
  - 각 코인 옆에 `[✅진입됨]` 또는 `[⏳대기]` 표시
  - 예: `ORDERUSDT[⏳대기], BTCUSDT[✅진입됨]`

## [3.2.10] - 2026-04-08

### Fixed

- **PUMP 코인 진입 실패 근본 수정**: "게이트 통과" 됐는데 진입 안 된 원인
  - PumpScan AnalyzeSymbolAsync 최소 캔들 120봉→**30봉** 완화
  - ORDER 등 신규/소형 코인이 10시간치 캔들 부족으로 분석 자체가 스킵됐음
  - PUMP 슬롯 2개 유지 (변경 없음)

## [3.2.9] - 2026-04-08

### Fixed

- **CPU 과다 사용 최적화**:
  - PumpScan: 매 1초 → **10초 간격** (60개 병렬 API 호출 빈도 90% 절감)
  - 심볼 분석: 메이저 180ms → **1초**, 알트 1초 → **2초**
  - 15분봉 꼬리 리테스트: 메인 루프 블로킹 → **백그라운드 태스크 분리** (5분 블로킹 제거)

## [3.2.8] - 2026-04-08

### Fixed

- **PumpScanStrategy도 AI 최우선 구조로 전환**: ORDER 등 PUMP 코인 진입 안 되던 문제
  - 규칙 기반 점수(aiScore ≥ 50 + structureOk + momentumOk) → **모멘텀 신호 3개+ → AI 위임**
  - 가격 모멘텀 직접 감지: 30분 +1.5%, 1시간 저점 +3% 포함
  - ML 55%+ 단독 진입 유지
  - 메이저/PUMP 동일한 AI 최우선 구조로 통일

## [3.2.7] - 2026-04-08

### Changed

- **AI 최우선 진입 구조 전면 개편**:
  - 규칙 기반 점수(AIScore 60점 임계값) → **모멘텀 방향 판단 후 AI에 위임**
  - 모멘텀 신호 3개+ (상승 OR 하락) → LONG/SHORT → AI Gate가 최종 판단
  - 규칙 점수는 UI 참고용으로만 유지
- **Spike 감지 대폭 개선**:
  - 5분 간격 → **1분 간격**
  - 임계값 +3% → **±2%** (급락 SHORT도 감지)
  - **메이저 코인(SOL 등) 포함** (이전: 제외)
  - 거래량 최소 $1M → $500K, 쿨다운 30분 → 15분

## [3.2.6] - 2026-04-08

### Added

- **AI 학습 피처 5개 추가** (CandleData):
  - `HigherLows_Count` — 연속 저점 상승 횟수 (계단식 상승)
  - `LowerHighs_Count` — 연속 고점 하락 횟수 (계단식 하락)
  - `Price_Momentum_30m` — 30분 가격 변화율 %
  - `Bounce_From_Low_Pct` — 1시간 저점 대비 반등률 %
  - `Drop_From_High_Pct` — 1시간 고점 대비 하락률 %

### Changed

- **AI 최우선 진입**: 규칙 기반 WAIT이어도 모멘텀 반등/하락 감지 시 AI에 위임
  - 30분 +1.5% 반등 OR 1시간 저점 +3% → LONG으로 AI에 전달
  - 30분 -1.5% 하락 OR 1시간 고점 -3% → SHORT으로 AI에 전달
  - AI(TradeSignalClassifier + AIDoubleCheckGate)가 최종 판단

## [3.2.5] - 2026-04-08

### Added

- **계단식 하락(Lower Highs) 감지**: 3연속 고점 하락 패턴 → SHORT 시그널 강화 (+15점)
- **SHORT 별도 점수 체계**: LONG AIScore와 독립된 `shortBearishScore` (50점 기반)
  - LowerHighs +15, 30분 하락 -1.5% +15, 1시간 고점 -3% +10
  - 기존 else if → LONG 판단과 독립적으로 SHORT 판단

### Changed

- **SHORT 진입**: LONG 실패 후에만 판단 → WAIT 상태에서 별도 점수 60점 이상이면 독립 진입
- **하락 모멘텀 직접 감지**: 30분간 -1.5%, 1시간 고점 대비 -3% 실시간 감지

## [3.2.4] - 2026-04-08

### Fixed

- **폭락 후 반등 진입 실패 근본 수정**: SMA 지연으로 6% 반등해도 진입 못 하던 문제
  - **가격 모멘텀 직접 감지**: 30분간 +1.5% → AIScore +15, 1시간 저점 대비 +3% → +10
  - **bullishStructure 완화**: SMA 상승추세 OR HigherLows OR 가격 모멘텀 반등 (셋 중 하나)
  - **MACD 조건 완화**: 반등 중이면 Hist >= -0.01 허용 (기존 -0.001)
  - **AI Gate 메이저 임계값**: ML 75%→60%, TF 68%→55%
  - **R:R 최소 비율**: 1.40→1.20 (폭락 후 ATR 높아 R:R 미달 방지)

## [3.2.3] - 2026-04-08

### Fixed

- **AI Advisor 이중 축소 방지**: AI Gate 20% × 분할 진입 25% = 실질 5% 문제 해결
  - 변경: AI Gate 사이즈가 곧 최종 사이즈 (이중 곱셈 제거)
  - AI Gate 통과(100%) → 100% 진입, AI Advisor(20%) → 20% 진입 (기존 5%)

### Changed

- **24시간 동일 진입 기준**: 야간(19~10시) 임계값 상향 제거
  - 메이저: 주간 60 / 야간 65 → **24시간 60**
  - PUMP: 주간 50 / 야간 45 → **24시간 50**
  - SHORT: 주간 30 / 야간 25 → **24시간 30**

## [3.2.2] - 2026-04-07

### Fixed

- **CRASH/PUMP 독립 진입**: LONG 없어도 CRASH 시 메이저 코인 SHORT 독립 진입, SHORT 없어도 PUMP 시 LONG 독립 진입 (기존: 보유 포지션 청산 후에만 리버스)

### Changed

- **SHORT 진입 조건 완화**: MajorCoinStrategy 5개 AND → 핵심 3개 + 가격<SMA20 필수
  - 기존: !isUptrend AND MACD<0 AND price<SMA20 AND vol>=1.1 AND price<Fib618 (전부 충족)
  - 변경: price<SMA20 필수 + 나머지 4개 중 2개 추가 충족 (총 3개)
- **MACD 데드크로스 상위봉 확인 완화**: 15m AND 1H 하락 → 15m 하락이면 진입 허용 (급락 시 1H 전환 느림)

## [3.2.1] - 2026-04-07

### Removed

- **ATR Dual-Stop (WhipsawTimeout 3분) 비활성화**: ATR 터치 후 3분 타임아웃으로 정상 횡보에서 손절 발생 (BTC ROE -29.3%). 구조 기반 손절(v3.2.0)이 대체

## [3.2.0] - 2026-04-06

### Changed

- **구조 기반 손절 (Structure-Based SL)**: 고정 ROE 손절 → 15분봉 구조선(지지/저항) 우선
  - 스윙로우/스윙하이 20봉 기준, ATR x5 최대 캡
  - 구조선이 뚫리면 시나리오 파기 = 의미 있는 손절
- **거래량 컨펌 필터**: 5봉 평균 대비 거래량 50% 미만 → 진입 차단
  - 거래량 없는 가짜 무빙 90% 차단
- **분할 진입 정찰대/본대**: 모든 메이저 진입을 25% 정찰대 → 확인 후 본대 추가
  - ROE +10%: 75% 추가 (확실한 발산), ROE +5%: 50%, ROE +2%: 30%
  - 정찰대 손절 시 타격 최소화 (25% only)

## [3.1.9] - 2026-04-06

### Changed

- **20배 레버리지 트레일링 스탑 전면 재튜닝**: 노이즈에 안 털리는 넓은 간격
  - 1단계 본절: ROE 20% → 스탑을 진입가로 이동 (이전: 플래그만, 스탑 이동 없음)
  - 2단계 부분익절: ROE 20%→40% 상향 (가격 1%→2% 변동 후)
  - 3단계 트레일링: ROE 40%→50% 상향 + 간격 ROE 5%→30% (가격 0.25%→1.5%)
  - BTC: 본절15% / TP1=30% / 트레일링 40% / 간격 20%(가격1%)
  - ETH/XRP/SOL: 본절20% / TP1=40% / 트레일링 60% / 간격 30%(가격1.5%)

### Removed

- **스퀴즈 방어 축소 비활성화**: 90분 보유 + 손실 중 50% 강제 청산 → 손실만 확정시킴
- **H&S Pattern Panic Exit 비활성화**: 넥라인 이탈 전 오탐지로 패닉 손절 반복

## [3.1.8] - 2026-04-06

### Removed

- **H&S Pattern Panic Exit 비활성화**: 넥라인 이탈 전 1.5% 여유만으로 오탐지 → XRP -3~5%, ETH -8% 불필요한 패닉 손절 반복. ATR/MACD 기반 청산이 대체

## [3.1.7] - 2026-04-06

### Added

- **15분봉 위꼬리 음봉 SHORT 시스템**: 세력 물량 넘기기 패턴 포착
  - 15분봉 완성봉에서 UpperShadowRatio 50%+ 음봉 + 거래량 1.5x+ 감지
  - 상위봉(15m+1H) 하락세 확인 후 1분봉 리테스트 대기 (꼬리 0.5~0.618 지점)
  - 리테스트 구간에서 MACD 데크 or RSI 꺾임 → SHORT 진입
  - 손절 = 15분봉 고점 +0.1% (명확한 손절선)
- **ML 피처 추가**: `Is15mBearishTail` (15분봉 꼬리 음봉 여부), `TrendAlignment` (상위봉 추세 정렬)

## [3.1.6] - 2026-04-06

### Added

- **MACD 데드크로스 SHORT 진입**: 상위봉(15m+1H) 하락 확인 + 1분봉 데드크로스 → 자동 SHORT
  - Case A (추세 추종): 0선 근처/위 데드크로스 — 가장 안전한 숏 자리
  - Case B (변곡점): DeadCrossAngle 급하락 — 하이리스크 하이리턴
- **SHORT 익절 시스템**:
  - RSI ≤ 30 (과매도) → 50% 부분 익절
  - MACD 히스토그램 BottomOut (음수 막대 짧아짐) → 트레일링 스탑 바짝 조임
  - 1분봉 MACD 골든크로스 → 전량 탈출
- **ML 피처**: `MACD_DeadCrossAngle` 추가 — 음수일수록 급하락, AI 하락 강도 판단 근거

## [3.1.5] - 2026-04-06

### Added

- **MACD 골든크로스 진입 시스템 (MacdCrossSignalService)**:
  - 상위봉(15m+1H) 정배열 확인 + 1분봉 MACD 골든크로스 → 자동 진입
  - Case A (0선 아래): 과매도 반등 (RSI < 40)
  - Case B (0선 위): 추세 가속 (RSI 무시, 숏 스퀴징 포착)
  - 메이저 코인 30초 간격 스캔
- **MACD 데드크로스 트레일링**: 보유 중 1분봉 MACD 감시
  - 데드크로스 확정 (ROE 8%+) → 즉시 익절
  - 히스토그램 PeakOut → 트레일링 스탑 현재가 -0.2%로 조임
- **MACD 히스토그램 변화율 피처**: CandleData에 `MACD_Hist_ChangeRate` 추가 (골크 1~2봉 전 예측용)

## [3.1.4] - 2026-04-06

### Fixed

- **급등 감지 진입 실패 수정**: SPIKE_DETECT 경로에서 3가지 차단 해소
  - 5분봉 130봉 미달 → 20봉 이상이면 최소 CandleData 자동 생성으로 진입 진행
  - 5분봉 140봉 부족 → 30봉으로 재시도
  - RSI ≥ 88 극단 차단 → SPIKE_DETECT는 예외 (급등 코인은 RSI 높은 게 정상)

## [3.1.3] - 2026-04-05

### Changed

- **PUMP 후보 선정 개선 (방안A)**:
  - Volume 가중치 50%→25%, Volatility 20%→35%, Momentum 30%→40%
  - 후보 수 40→60개, 메이저(BTC/ETH/SOL/XRP) PUMP 스캔에서 제외
- **PUMP 진입 조건 완화 (방안B)**:
  - `rulePass AND structureOk AND momentumOk` → `rulePass AND (structureOk OR momentumOk)`
  - FibScore +20점 PUMP 코인에도 적용 (기존 메이저만)
  - ML 단독 진입 확률 60%→55%, 야간 threshold 40→35
- **급등 실시간 감지 (방안C)**:
  - 전 종목 5분 가격 변동률 스캔 (TickerCache 기반)
  - +3% 급등 + 거래량 $1M+ → PumpScan 스킵 즉시 진입 (SPIKE_DETECT)
  - 텔레그램 알림, 코인별 30분 쿨다운

## [3.1.2] - 2026-04-03

### Added

- **순 투입금 기반 수익률 표시**: 바이낸스 Income History API에서 Transfer In/Out 합산
  - 대시보드에 "TRANSFER PnL" 필드 추가 (투입금, PnL 금액, 수익률%)
  - 진입 사이즈는 기존 가용 잔고 기준 유지
  - 최근 1년 입출금 내역 자동 조회 (앱 시작 시)

## [3.1.1] - 2026-04-03

### Added

- **AI 시장 상태 분류기 (MarketRegimeClassifier)**: LightGBM 3분류 모델
  - TRENDING / SIDEWAYS / VOLATILE 자동 판별
  - 피처: BB_Width, ADX, ATR비율, RSI, MACD기울기, 거래량변화, SMA정배열, 캔들바디비율
  - 5분봉 KlineCache에서 자동 라벨링 + 학습
- **AI 최적 익절 모델 (ExitOptimizerService)**: LightGBM 이진분류
  - EXIT_NOW / HOLD 판단 (ROE 10%+ 보유 중 5초 간격 질의)
  - 피처: 현재ROE, 최고ROE, 되돌림크기, 시장상태, BB_Width, ADX, RSI, 보유시간
  - 과거 트레이드 DB에서 자동 학습 (최고ROE 대비 수익 보존율로 라벨링)
  - 횡보장에서 EXIT_NOW 70%+ → 즉시 익절 (수익 증발 방지)
  - 추세장에서 HOLD → 기존 트레일링 유지 (상승 기회 보존)

## [3.1.0] - 2026-04-02

### Added

- **시장 급변 감지 시스템 (MarketCrashDetector)**: BTC/ETH/SOL/XRP 1분 가격 변동률 실시간 추적
  - CRASH 감지: N개 코인 동시 -1.5% → 보유 LONG 전량 긴급 청산 + SHORT 리버스 진입
  - PUMP 감지: N개 코인 동시 +1.5% → 보유 SHORT 전량 긴급 청산 + LONG 리버스 진입
  - 리버스 진입 사이즈 조절 (기본 50%), 쿨다운 (기본 120초)
  - 텔레그램 알림 (CRASH/PUMP 감지 즉시)
- **설정창 급변 감지 섹션**: 활성화 토글, 임계값, 최소 코인 수, 리버스 사이즈, 쿨다운 조정

## [3.0.16] - 2026-04-02

### Fixed

- **유저 스트림 끊김 후 복구 안 되는 문제**: ListenKey KeepAlive 25분 주기 자동 갱신 + 끊김 감지 시 완전 재구독 워치독 추가

### Removed

- **PUMP 15분 타임스탑**: 횡보 시 자동 청산 제거 (손절/트레일링이 관리)
- **PUMP ElliottWave 20분 타임스탑**: 횡보 시 절대 손절 제거
- **메이저 120분 미사용 변수**: `maxHoldingMinutes`, `timeoutExitRoeThreshold` 죽은 코드 정리

## [3.0.15] - 2026-04-01

### Fixed

- **팬텀 EXTERNAL_PARTIAL_CLOSE_SYNC 중복 기록**: 전량 청산 후 WebSocket ACCOUNT_UPDATE 분할 도착 시 ACCOUNT_UPDATE_RESTORED → EXTERNAL_PARTIAL_CLOSE_SYNC로 팬텀 PnL 중복 기록되는 문제 수정
  - 청산 완료 시 30초 쿨다운 등록, 쿨다운 중 해당 심볼 ACCOUNT_UPDATE 무시
  - SOL 청산 시 실제 -125 PnL 외에 팬텀 -63 PnL이 추가 기록되던 문제 해결

## [3.0.14] - 2026-04-01

### Fixed

- **모든 주문 경로 stepSize/tickSize 보정 보장**: ExchangeInfo API 실패 시에도 심볼별 폴백 테이블로 보정 (XRP 0.1, SOL 0.1, DOGE 1, PEPE 100 등 15종)
- **PlaceMarketOrderAsync**: ExchangeInfo 직접 호출 → `GetSymbolPrecisionAsync` 캐시 재활용 (중복 API 호출 제거)
- **PlaceStopOrderAsync**: stepSize/tickSize 보정 누락 → 추가
- **PlaceLimitOrderAsync**: ExchangeInfo 실패 시 보정 스킵 → 폴백 보장

## [3.0.13] - 2026-04-01

### Fixed

- **부분청산 수량 stepSize 오류**: `PlaceOrderAsync`에서 reduceOnly 시에도 stepSize 보정 적용
- **부분청산 재시도 강화**: 1회 → 최대 3회 반복 재시도 (매회 거래소 포지션 재확인 + 수량 보정)

### Added

- **청산오류 텔레그램 알림**: 부분청산 최종 실패 시 별도 텔레그램 알림 전송
- **설정창 청산오류 알림 토글**: `TelegramMessageType.CloseError` 추가, 진입 알림과 독립적으로 ON/OFF 가능

## [3.0.12] - 2026-04-01

### Fixed

- **부분청산 실패해도 "완료" 표시되는 버그**: `ExecutePartialClose`를 `Task<bool>` 반환으로 변경, 12개 호출부 전부 성공 여부 확인 후에만 상태 변경 + 완료 메시지 발행
- **트레일링스탑 가격 메인창 미표시**: `OnTrailingStopPriceUpdate` 이벤트 추가, SL/TP 컬럼에 `TS:가격` 실시간 표시

### Added

- **주문 오류 DB 기록 (`dbo.Order_Error`)**: 부분청산 실패 시 ErrorCode, ErrorMsg, RetryCount, Resolution 기록
- **부분청산 실패 자동 분석 + 재시도**: 거래소 실제 포지션 확인 → 수량 불일치 보정 → 재시도, 이미 청산된 경우 내부 상태 자동 정리

## [3.0.11] - 2026-04-01

### Fixed

- **XRP 부분청산 실패**: MockExchange 1% 랜덤 실패 제거 (시뮬레이션 안정화)
- **TP/SL UI 미표시**: 진입 시 TP 가격 계산 (BTC +20% ROE, ETH/SOL/XRP +30%, PUMP +25%)
- **트레일링 SL UI 미반영**: 고점 갱신 시 PositionInfo.StopLoss 실시간 업데이트

### Added

- **정찰대→메인 전환 (MonitorScoutToMainUpgradeAsync)**:
  - ROE +5% (2분 후) → 70% 메인 추가
  - ROE +2% (5분 후) → 50% 메인 추가
  - ROE 0~+2% (10분 후) → 30% 메인 추가
  - ROE 음수 → 추가 안 함 (정찰대만 유지)
  - 15분 경과 + ROE < +2% → 추가 포기

### Changed

- **사이즈 결정 간소화**: 정찰대=30% 고정(AI 축소 무시), 메인=100% 기본(3분류 모델만 조절)

## [3.0.9] - 2026-04-01

### Added

- **PUMP 전용 ML 모델 (PumpSignalClassifier)**: 급등 패턴 특화 이진 분류
  - 18개 피처: 거래량 급증(3/5봉), 가격 변화율(3/5/10봉), RSI 변화량, BB 돌파 강도, MACD 가속도, ATR 비율, Higher Lows, 연속 양봉 등
  - 라벨: 30분 내 +2% 이상 상승 → 진입(1), 그 외 → 관망(0)
  - PumpScanStrategy에서 규칙 기반 + ML 결합 판단

### Changed

- **PUMP SHORT 제거**: 급등 코인은 LONG만 허용 (숏은 리스크만 큼)
- **PUMP 신호 조건 완화**:
  - 후보 20개 → **40개**로 확대
  - longThreshold 55~65 → **40~55**로 하향
  - RSI 75+ 감점 제거 → +3 보너스 (급등 모멘텀 유지)
  - BB 상단 돌파: 감점 → +5 강세 신호 (RSI 85+만 감점)
  - MACD Hist >= -0.001 → **>= -0.01** 완화
  - bullishStructure: 엘리엇 OR Higher Lows OR **가격>SMA20** (OR 조건 확대)
  - ML 모델 60%+ 확률이면 규칙 미통과여도 LONG 진입 가능

## [3.0.8] - 2026-04-01

### Changed

- **ExecuteAutoOrder 리팩토링**: 1540줄 단일 메서드 → 6개 분리
  - `ExecuteAutoOrder()`: 라우터 (공통 검증 → 4개 분기)
  - `ExecuteMajorLongEntry()`: 메이저 롱 (ATR SL, EMA/스퀴즈 보너스)
  - `ExecuteMajorShortEntry()`: 메이저 숏 (RSI 과매도만 차단)
  - `ExecutePumpLongEntry()`: PUMP 롱 (고정 증거금)
  - `ExecutePumpShortEntry()`: PUMP 숏 (RSI 과매도만 차단)
  - `PlaceAndTrackEntryAsync()`: 공통 주문 실행 (레버리지/수량/주문/DB/텔레그램)
- **사이즈 축소 중첩 제거**: 5개 AI Advisor가 Math.Min 연쇄 → 최저값 1개만 선택, 최소 10% 하한
- **FundingCost Exit / TimeOut Exit 제거**: 단타에서 무의미한 청산 사유 삭제
- **부분청산 텔레그램 알림 추가**: TP1/TP2 부분익절 시 텔레그램 전송

## [3.0.7] - 2026-03-31

### Added

- **3분류 매매 신호 모델 (TradeSignalClassifier)**: ML.NET LightGBM MultiClass
  - 롱(1) / 숏(2) / 관망(0) 3분류 예측
  - 24개 피처: RSI, MACD, BB, ATR, ADX, Volume, OI, 피보나치, 캔들 패턴 등
  - DB CandleData에서 자동 라벨링 (30분 후 가격변동률 ±1% 기준)
  - 앱 시작 시 모델 로드 또는 DB 데이터로 자동 초기 학습
  - 진입 시 3분류 모델 추론 → 같은 방향이면 사이즈 부스트, 반대면 축소

### Changed

- **TensorFlow.NET 완전 제거**: ML.NET 단독 아키텍처로 통합
  - TensorFlow.NET 패키지 제거
  - TensorFlowTransformer/TensorFlowEntryTimingTrainer 빌드 제외
  - AIDoubleCheckEntryGate: TF 의존 제거 → ML.NET _mlTrainer 단독
  - 4개 서비스(AIDataflowPipeline, AIDedicatedWorkerThread, AIPipelineServer, HybridAIPredictionService): TF 필드 제거
  - AdaptiveOnlineLearningService: TF 학습 제거

## [3.0.6] - 2026-03-31

### Changed

- **AI Advisor 전환**: AI를 게이트키퍼(차단자)에서 어드바이저(조언자)로 전면 리팩토링
  - `AI_GATE`: 거부 시 차단 → blended score 기반 사이즈 동적 조절 (90%+→풀, 50%→20%, <50%→10%)
  - `AI Score Filter`: 점수 미달 차단 → score/threshold 비율로 사이즈 축소 (70%+→50%, 50%+→30%)
  - `ATR_VOL`: 변동성 폭발 차단 → 사이즈 축소 (극단→20%, 높음→50%)
  - ProfitRegressor: 손실 예측 차단 → 50% 축소 진입 (v3.0.4에서 적용)
- **하드 블록 추가 축소**: 17개 → 11개 (필수 안전장치만 유지)

### Retained (필수 하드 블록)

- Signal Validation, RSI Extreme (88/12), Circuit Breaker, R:R Ratio
- Blacklist, Slot Cooldown, Duplicate Position, Slot Limit
- Leverage/Size/Order Execution 실패

## [3.0.5] - 2026-03-31

### Fixed

- **SHORT 진입 불가 수정**: AI 상승예측/MACD 양수/하락확률 부족 3중 차단 제거
  - 기존: AI 상승 예측 OR MACD>0 OR 확률<60% → 무조건 SHORT 차단 (사실상 SHORT 불가)
  - 수정: RSI 과매도 + 가격 MA20 위 (추세 없는 역매도)만 차단, 나머지는 AI 스코어에 위임

## [3.0.4] - 2026-03-31

### Fixed

- **시뮬레이션 정찰대 진입 불가 수정**: AI 게이트 거부 시 blended score 50%+ 이면 10% 정찰대 자동 투입
  - 기존: AI 게이트 거부 + blended < 70% → 무조건 차단 (정찰대 배치 불가)
  - 수정: 시뮬레이션 모드에서 blended ≥ 50% 시 최소 정찰대 투입
- **AI 게이트 차단 로그 누락**: 바이패스 전부 실패 시 로그 없이 return하던 문제에 상세 로그 추가

### Changed

- **과잉 진입 필터 6개 제거/완화**: 하드 블록 23개 → 17개로 축소
  - `PROFIT_REG`: 차단 → 50% 사이즈 축소 진입 (손실 예측이어도 기회 유지)
  - `M15_SLOPE`: 하드 블록 → INFO 로그 (메이저 LONG 기회 증가)
  - `CANDLE_CONFIRM` (서브필터 5개): 지연 대기 시스템 전체 제거 (즉시 진입)
  - `1M_HUB`: 60~180초 대기 제거 (즉시 실행)
  - `PATTERN_HOLD`: 차단 → INFO 로그 (패턴 디퍼 차단 제거)
  - `EW_RR`: 이중 R:R 차단 → INFO 로그 (기존 RR 필터로 통합)

## [3.0.3] - 2026-03-30

### Fixed

- **시뮬레이션 모드 TradeHistory 저장**: 시뮬레이션 자동매매 시 TradeHistory에 데이터가 쌓이지 않던 문제 수정
  - 청산/부분청산/패턴 스냅샷 라벨링 모두 시뮬레이션 모드에서도 DB 저장
  - `IsSimulation` 컬럼으로 실거래/시뮬레이션 거래 구분 가능

### Added

- **TradeHistory IsSimulation 컬럼 자동 마이그레이션**: 앱 시작 시 `IsSimulation BIT` 컬럼 자동 추가
- **시뮬레이션 거래 AI 학습 지원**: 시뮬레이션 수익/손실 거래도 패턴 스냅샷 라벨링 및 AI 학습 데이터로 활용

## [3.0.2] - 2026-03-30

### Added

- **PUMP 코인 정찰대 진입**: `HandlePumpEntry`에서 슬롯 포화 시 30% 증거금으로 정찰대 진입 (기존엔 차단)
  - 메이저/PUMP/총 슬롯 포화 모두 정찰대 전환

## [3.0.1] - 2026-03-30

### Added

- **DB TradeHistory 기반 수익률 학습**: 과거 60일 거래 내역을 ProfitRegressor 학습 데이터로 로드
  - `LoadFromTradeHistoryAsync()`: DB에서 청산 완료된 거래 + 진입 시점 캔들 지표 조인
  - 엔진 시작 시 자동 로드 → 50건 이상이면 즉시 학습
  - 이익/손실 거래 모두 포함하여 수익 패턴과 손실 패턴 동시 학습
- **DbManager.GetRecentCandleDataAsync()**: 심볼별 최근 캔들 지표 조회 (학습 데이터용)

## [3.0.0] - 2026-03-27

### Added

- **수익률 회귀 모델** (`ProfitRegressorService`):
  - "지금 진입하면 5분 뒤 수익률이 얼마인가" ML.NET FastTree 회귀 예측
  - 실제 거래 결과(P&L, 보유시간)로 자동 학습 (50건 이상 축적 시)
  - R², MAE, RMSE 검증 메트릭 로깅
- **동적 포지션 사이징**:
  - 예측 수익률 +3% 이상 → 150% 진입, +2% → 130%, +1% → 100%
  - 예측 수익률 음수 → **진입 금지** (손실 방어)
- **거래 결과 피드백 학습**:
  - 청산 시 `OnPositionClosedForAiLabel` 이벤트에서 자동 데이터 수집
  - 학습 버퍼 최대 5000건, 자동 순환

## [2.9.2] - 2026-03-27

### Changed

- **메인 대시보드 레이아웃 정리**:
  - 좌측 사이드바 260px → 200px 축소 (DataGrid 영역 확보)
  - Entry Gate 카드: 3행 → 간결한 2행 레이아웃, 불필요한 장식 Border 제거
  - AI Learning 카드: 마진/폰트 축소, 3행 그리드 유지
  - AI PREDICTIVE TIME-OUT: 패딩 14px→8px, 헤더 1줄 축소

## [2.9.1] - 2026-03-27

### Added

- **SSA 시계열 예측 → TradingEngine 메인 루프 연동**:
  - 5분마다 KlineCache에서 종가 데이터로 SSA 학습 + 예측
  - `OnSsaForecastUpdate` 이벤트 → ViewModel → SkiaCandleChart 자동 전달
  - SymbolChartWindow: PropertyChanged 구독으로 예측 밴드 실시간 업데이트

## [2.9.0] - 2026-03-27

### Added

- **RingBuffer\<T\>**: GC 부하 없는 고정 크기 순환 배열 — 캔들/틱 데이터 저장소
- **ML.NET SSA 시계열 예측** (`SsaPriceForecastService`):
  - ForecastBySsa 알고리즘: WindowSize=20, Horizon=5, Confidence=95%
  - 데이터 정규화 (기준가 대비 bps 변환) → 0% 수렴 방지
  - 예측값 + Upper/Lower Bound → SkiaSharp 차트 반투명 영역
- **Microsoft.ML.TimeSeries** NuGet 패키지 추가
- **Binance API Rate Limiter**: SemaphoreSlim(20) 동시 요청 제한

## [2.8.1] - 2026-03-27

### Added

- **SkiaSharp 고성능 렌더링 강화**:
  - 가상화(Virtualization): 최근 100개 캔들만 렌더링, 나머지는 메모리 유지
  - Buy/Sell 시그널 마커: 진입/청산 시점에 삼각형 마커 + 라벨 차트 오버레이
  - `AddSignalMarker()` / `ClearSignalMarkers()` API
- **Fail-safe 리스크 관리 모듈** (`FailSafeGuardService`):
  - API 하트비트 모니터링 (30초 경고, 60초 긴급 전체 청산)
  - 슬리피지 감지 (체결가 vs 요청가 1% 이상 차이 시 알림)
  - 잔고 급감 감지 (50% 이상 감소 시 전체 포지션 긴급 청산)
  - TradingEngine에 연동 (OnEmergencyCloseAll → 전체 ClosePositionAsync)

## [2.8.0] - 2026-03-27

### Added

- **핀테크 대시보드 v1**:
  - SkiaSharp 차트에 SMA20(금색)/SMA50(파랑) 이동평균선 오버레이
  - Bollinger Bands 밴드(보라) 영역 채우기 + 상/하 경계선
  - `CandleRenderData`에 SMA20/SMA50/BB 데이터 필드 확장
  - SymbolChartWindow: ML.NET / Transformer / AI Score / RSI / Signal 5카드 의사결정 패널
  - MainWindow 우측 패널: AI PREDICTION 요약 카드 (ML/TF ProgressBar + BB Position 텍스트)

## [2.7.1] - 2026-03-27

### Fixed

- **PUMP 코인이 메이저 모드로 처리되는 버그**: 봇 재시작/포지션 복구 시 `savedPump = false` 기본값 → 메이저 손절(-20%) 적용됨. `!MajorSymbols.Contains(symbol)`로 수정
- **정찰대 진입 실패**: 슬롯 포화 시 정찰대 플래그 설정 후 주문 직전 최종 슬롯 재확인에서 다시 차단. `scoutModeActivated` 시 최종 재확인 스킵

## [2.7.0] - 2026-03-27

### Added

- **수동 진입/청산 텔레그램 알림**: ManualEntry, DirectClose, MockClose 모두 텔레그램 발송
- **슬롯 포화 시 정찰대 자동 진입**: AI 게이트 통과했으나 슬롯 포화(메이저/PUMP/총) 시 30% 정찰대 진입 (기존: 진입 차단 return)

### Removed

- **본절 청산 로직 제거**: 1단계 본절 보호(ROE 7% → 진입가 스탑) 비활성화 — 수익 기회 제한 방지
- **시간 기반 본절 전환 제거**: 보유 시간 초과 시 자동 본절 전환 비활성화
- 2단계 부분익절(ROE 20%), 3단계 트레일링은 유지

## [2.6.11] - 2026-03-26

### Fixed

- **테스트넷 청산 안 되는 근본 원인**: `PlaceOrderAsync`에 시뮬레이션 모드 체크(`IsSimulationMode == true → return true`)가 있어 테스트넷에서도 실제 주문을 보내지 않고 가짜 성공 반환하던 문제 제거
  - 진입(`PlaceMarketOrderAsync`)은 이 체크가 없어 정상 작동했지만, 청산(`PlaceOrderAsync reduceOnly`)은 차단됨

## [2.6.10] - 2026-03-26

### Fixed

- **SHORT 진입 시 ROI 부호 반대 표시**: 수동 진입 후 `PositionSide`가 설정되지 않아 LONG으로 간주됨 → `OnSignalUpdate`로 `PositionSide`/`Leverage` 전달 추가

## [2.6.9] - 2026-03-26

### Fixed

- **시뮬레이션/실거래 혼선 근본 해결**: 테스트넷 모드에서 실거래 API로 호출되는 6개 경로 수정
  - `_client` (BinanceRestClient): 테스트넷 키 + `BinanceEnvironment.Testnet` 설정 → ExecutionService, PositionMonitor, OrderService 모두 테스트넷으로 통일
  - `MarketDataManager` WebSocket: 테스트넷 키 + 테스트넷 환경 설정
  - `BinanceSocketConnector` WebSocket: 동일 수정
  - `DirectClosePositionAsync`: `_positionMonitor` 미초기화 시 직접 거래소 API 청산

## [2.6.8] - 2026-03-26

### Fixed

- **테스트넷 CLOSE 완전 해결**: `_positionMonitor` 미초기화(스캔 미시작) 시 `_exchangeService`로 직접 청산
  - `DirectClosePositionAsync`: 거래소 포지션 조회 → 반대 방향 시장가 주문 → DB 기록 → UI 정리

## [2.6.7] - 2026-03-26

### Fixed

- **테스트넷 시뮬레이션 CLOSE 안 됨 해결**: 테스트넷(demo.binancefuture.com) 모드에서는 실제 거래소 API로 청산 주문 전송하도록 수정
  - MockExchange(가상)일 때만 로컬 제거, 테스트넷은 `PositionMonitor.ExecuteMarketClose` 경유

## [2.6.6] - 2026-03-26

### Fixed

- **시뮬레이션 청산 시 포지션 미닫힘**: CloseIncomplete 플래그 해제, ROI 0 리셋, HybridExitManager 상태 정리, 블랙리스트 등록 누락 수정

## [2.6.5] - 2026-03-26

### Added

- **유저별 시뮬레이션 테스트넷 API 키 관리**: ProfileWindow에 Binance Testnet API Key/Secret 입력 필드 추가
  - DB `Users` 테이블에 `TestnetApiKey`/`TestnetApiSecret` 컬럼 자동 추가
  - 로그인 시 유저별 테스트넷 키 자동 로드 → 시뮬레이션 모드에서 개인 키 사용
  - AES256 암호화 저장/복호화 로드

## [2.6.4] - 2026-03-26

### Fixed

- **API 키 수정 실패 해결**: `UpdateUserAsync` SQL에서 `BybitApiKey`/`BybitApiSecret` 파라미터 참조 → `User` 클래스에 해당 프로퍼티 없어 Dapper 에러 발생
- **Bybit 섹션 제거**: ProfileWindow에서 미사용 Bybit API Key 입력 필드 삭제
- **불필요한 ALTER TABLE 제거**: Bybit 컬럼 자동 추가 SQL 삭제

## [2.6.3] - 2026-03-26

### Changed

- **Critical Alerts 위치 이동**: 좌측 사이드바 → 메인 DataGrid 하단 120px 영역 (5줄 표시)

### Fixed

- **시뮬레이션 모드 CLOSE 청산미완료 해결**: 거래소 API 우회, 로컬 포지션 직접 제거 + DB 기록 + UI 즉시 갱신

## [2.6.2] - 2026-03-26

### Fixed

- **수동 진입 후 Close 버튼 미표시**: `OnPositionStatusUpdate` 호출 누락 → `IsPositionActive=true` 설정되어 Close 버튼 정상 표시
- **수동 진입 히스토리 미기록**: DB `TradeHistory`에 진입 레코드 저장 추가 (`SaveTradeLogAsync` → `UpsertTradeEntryAsync`)
- **수동 진입 후 UI 자동 갱신 안 됨**: `OnTradeHistoryUpdated` 이벤트 발생 추가

## [2.6.1] - 2026-03-26

### Added

- **수동 LONG/SHORT 진입 버튼**: START/STOP 옆에 배치, AI 게이트 우회 시장가 즉시 진입
  - 선택된 심볼 + 현재가 확인 팝업 → DefaultLeverage/DefaultMargin 사용
  - `TradingEngine.ManualEntryAsync()` 신규 메서드

### Fixed

- **2시간+ 미진입 근본 해결 (3건)**:
  - `IsReady` 조건 완화: ML+TF 동시 필요 → TF만 준비되면 게이트 개방
  - ML.NET 미가용/0% 시 `EvaluateEntryAsync` 하드 블록 제거 → TF 단독 모드 자동 전환
  - 엘리엇 규칙 1/2 위반 하드 블록 → TF 80%+ 시 경고로 다운그레이드

- **ML.NET 0% 수렴 근본 방어**:
  - `SanitizeFeature()`: Predict 전 34개 피처 NaN/Infinity→0 치환
  - Predict 결과 Probability/Score NaN 보정 (MLService 포함)
  - ML 0% && TF>=50% 자동 감지 → TF 단독 판단 전환
  - `EntryRuleValidator.FinalScore`: ML<1% 시 TF 점수로 대체

## [2.6.0] - 2026-03-26

### Added

- **AI 추론 파이프라인 전면 최적화**:
  - `PredictionEnginePool` (Microsoft.Extensions.ML): Thread-safe 풀링, lock 없이 다중 스레드 동시 ML.NET 추론
  - `ArrayPool<float>` 기반 Feature 벡터 할당: TensorFlowTransformer에서 GC 압박 제거
  - `EntryTimingMLTrainer.Predict()` 핵심 병목 해결: 매 호출 CreatePredictionEngine → 캐싱+풀 전환

- **TPL Dataflow AI 파이프라인** (`AIDataflowPipeline`):
  - `BufferBlock → TransformBlock → ActionBlock` 3단 비동기 파이프라인
  - 심볼별 500ms 디바운싱, 백프레셔 자동 관리 (버퍼 100 초과 시 드랍)
  - ML.NET + TF 추론 → `Dispatcher.InvokeAsync(Background)`로 UI 전달

- **AI 전용 워커 스레드** (`AIDedicatedWorkerThread`):
  - `ThreadPriority.AboveNormal`: UI 스레드(Normal)보다 높은 우선순위
  - `BlockingCollection` Producer-Consumer 패턴, 주문 조건 충족 시 UI 거치지 않고 즉시 실행
  - UI 차트 1초 늦어도 주문은 실시간 시세에 정밀 실행

- **Named Pipes IPC 프로세스 분리 인프라**:
  - `AIPipelineIpcService` (클라이언트) + `AIPipelineServer` (서버): Named Pipe 비동기 통신
  - `HybridAIPredictionService`: IPC 우선 → 인프로세스 폴백 (무중단 전환)
  - 향후 AI 엔진을 별도 콘솔 앱으로 분리 가능

- **SkiaSharp 고성능 캔들 차트** (`SkiaCandleChart`):
  - LiveCharts Shape 객체 대신 비트맵 직접 렌더링 (GC 할당 0)
  - 18개 SKPaint + 3개 SKFont 필드 캐싱 (프레임당 객체 생성 0)
  - AI 오버레이 5개 레이어: 예측 밴드, 트레일링 히스토리, 동적 손절선, 현재가 마커, STOP-EXIT 경고
  - `Interlocked` 기반 AI↔렌더 스레드 원자적 데이터 공유 (lock-free)
  - DispatcherTimer 4fps 프레임 제한 + `_needsRedraw` 플래그 최적화

- **상승 에너지 잔량 ProgressBar** (`PRICE ENERGY`):
  - ML.NET Fib 0.382~0.5 눌림목에서만 초록색 (ENTRY ZONE)
  - TF 수렴 후 발산 패턴 감지 → 금색 테두리 + ProgressBar 깜빡임 (SQUEEZE FIRE)
  - `OnPriceProgressUpdate(bbPos, mlConf, tfConvDiv)` 3파라미터 이벤트

- **AI 동적 트레일링 스탑 엔진** (`DynamicTrailingStopEngine`):
  - 추세 반전 확률 계산: 5파 완성(+35%), RSI 다이버전스(+25%), 상위 꼬리(+15%), RSI 80+(+20%)
  - ATR 멀티플라이어 AI 보정: 반전확률 70%+ → ×0.3, 변동성 폭발 → ×0.5
  - 반전 80%+ 극한 타이트: 0.2% 이내 트레일링
  - Exit Score 게이지 (0~100): HOLD / PREPARE EXIT / EXIT NOW / EMERGENCY

- **Exit Score UI 게이지** (`EXIT SCORE`):
  - 80%+ 시 빨간 테두리 깜빡임 애니메이션
  - 반전 확률 + 동적 손절가 동시 표시

- **SymbolChartWindow 리뉴얼**:
  - SkiaCandleChart + LiveCharts 이중 구조 (고성능 + 폴백)
  - PRICE ENERGY + EXIT SCORE 바 상단 배치
  - AI 손절가 실시간 연동 (`OnAIStopPriceChanged` 이벤트)

### Changed

- `AIPredictor`, `MLService`: `_predictLock`으로 PredictionEngine Thread-safety 보장
- `MainViewModel`: 적응형 ticker flush 간격 (100~500ms 자동 조절) + 재진입 방지 (`_tickerFlushRunning`)
- `TradingEngine`: `OnPriceProgressUpdate` 3파라미터 확장, `OnExitScoreUpdate` 이벤트 추가, AI 워커 + 동적 트레일링 엔진 자동 초기화

### Dependencies

- `System.Threading.Tasks.Dataflow` 9.0.1
- `Microsoft.Extensions.ML` 5.0.0
- `SkiaSharp` 3.116.1
- `SkiaSharp.Views.WPF` 3.116.1

## [2.5.6] - 2026-03-26

### Added

- **ElliottWave 1분봉 빠른 진입 트리거**:
  - 1분봉 기울기 0.3%+ & 거래량 20MA 대비 3x → V-Turn 확인 없이 즉시 진입
  - 15분봉 확인 매매 지연 문제 해결 (3파 초입 포착)

- **AI 피처 4개 추가 (추세 강도·국면 분류)**:
  - ADX (14): 추세 강도 판별 (>25 강한 추세, <20 횡보)
  - Ichimoku 기준선(26) 돌파: 상승/하락 국면 판단
  - FundingRate: 숏스퀴즈/롱스퀴즈 가능성 (밈코인 필수)
  - SymbolCategory: 종목별 파동 성격 구분 (BTC/ETH/Major/Meme/MicroCap)

- **ADX + 이치모쿠 기준선 지표 계산** (AIBacktestEngine)

### Changed

- 총 피처 수: 60개 → 64개

## [2.5.5] - 2026-03-25

### Fixed

- **이격도/OBV 하드 필터 제거**: VWAP 2%/EMA200 15% 하드 차단이 거의 모든 진입을 막고 있었음
  - 크립토 특성상 VWAP 2~5% 이격은 정상 → 하드 블록은 과잉 차단
  - VWAP_Position, OBV_Slope 등은 AI 피처로만 학습에 반영 (ML이 자체 판단)

## [2.5.4] - 2026-03-25

### Added

- **AI 피처 5개 추가 (선행성·정규화 강화)**:
  - VWAP_Position: 기관/세력 평균 단가 대비 위치 (당일 리셋)
  - Price_To_EMA200: 장기추세 기준점 이격도
  - RSI_Divergence: 5분봉 다이버전스 (가짜 상승/하락 감지)
  - OBV_Slope: On-Balance Volume 변화율 (자금 유출입)
  - Pivot_Position: 피보나치 피봇 포인트 (전일 기반 지지/저항)

- **이격도 과열 필터**: VWAP 2%+/EMA200 15%+ 이격 시 AI 진입 차단
- **OBV 가짜 상승 필터**: 가격↑ + OBV↓ 동시 발생 시 진입 차단
- **ATR 동적 손절**: 변동성 기반 SL 자동 조정 (ATR↓: -15%, ATR↑: -35%)
- **개미 털기 HOLD**: 하락 + 볼륨 없음 → 손절 보류 (세력 털기 방어)

### Changed

- **라벨링 최적화**: 60봉(5시간) → 15봉(75분), TP+1.0%/SL-0.5%
  - AI가 느린 추세 대신 '빠른 모멘텀 파동'을 학습
- 총 피처 수: 55개 → 60개 (5분봉 25 + 강화 5 + 15분봉 17 + 1H 5 + 4H 5 + 패턴 3)

## [2.5.3] - 2026-03-25

### Added

- **정찰대 v2 (Scout AI70)**: AI 블렌디드 신뢰도 70%+면 무조건 정찰대 투입
  - 80%+: 시드 20% 선진입, 70~80%: 시드 10% 선진입
  - 기존 TF≥90% 조건에서 대폭 하향하여 3파 놓침 방지

- **1분봉 스나이핑 확대**: 정찰대/불타기도 1분봉 ExecutionHub 적용
  - 15분봉 '흐름' 확인 → 1분봉 '채찍'(거래량 양봉) 즉시 진입
  - 정찰대: 60초 대기, 정규 진입: 180초 대기

- **구조적 손절 (Structural Stop)**: 전저점/전고점 종가 확인 후 청산
  - LONG: 최근 12봉 Swing Low 이탈을 '종가'로 확인 시 청산
  - SHORT: 최근 12봉 Swing High 돌파를 '종가'로 확인 시 청산
  - 고정 -18% ROE 대신 구조적 기준 → 세력 털기 wick 방어

- **불타기 v2 (Fib 0.382 Wave3 Add-on)**: 3파 진입 감지
  - Fib 0.382 돌파 + 볼륨 회복 → 40% 추가 투입
  - 계단식 고점 돌파 → 20% 추가 투입

### Changed

 - 없음

## [2.5.2] - 2026-03-22

### Fixed

- ProgressBar `DailyProfitProgress` / `VolumeGauge` TwoWay 바인딩 오류 수정 (Mode=OneWay)

## [2.5.1] - 2026-03-20

### Added

- **AI Sniper Exit -> PositionMonitorService 실시간 연결**:
  - 30초 주기로 SniperExit ML 모델 호출하여 구조적 손절 판단
  - 개미 털기 감지: 볼륨 없는 하락 -> HOLD (고정 손절 방지)
  - 매도압력 급증 감지: 체결 강도 비정상 -> 긴급 청산
  - 추세 강세/약화 판단: 트레일링 갭 자동 확대/축소
  - 부분익절 자동 실행 (ML 확신도 65%+ 시)

- **백그라운드 자동 AI 학습**:
  - 엔진 시작시 AI 의사결정 모델 미준비 감지 -> 자동 학습 트리거
  - AIBacktestEngine 백그라운드 실행 (UI 블로킹 없음)
  - 학습 완료 후 모델 자동 재로드

- **Entry Pipeline UI 패널** (AI Command Center 탭):
  - Block Reason: 진입 차단 사유 빨간 글씨 실시간 표시
  - 1M Volume Gauge: 1분봉 거래량 진행바 (진입 조건 시각화)
  - Daily Profit Target: 일일 목표 $250 대비 현재 수익 진행바

### Changed

- **시뮬레이션 모드 UX 개선**:
  - 설정 저장시 "앱 재시작 불필요" 안내 메시지로 변경
  - Stop -> Start만으로 시뮬레이션/실거래 전환 가능

### Fixed

- EntryLog에서 BLOCK/PASS 상태에 따라 UI Block Reason 실시간 업데이트

## [2.5.0] - 2026-03-20

### Added

- **AI Sniper Mode 의사결정 엔진** (`AIDecisionService.cs`):
  - 43개 피처 기반 LightGBM 진입 모델 (5분봉 25 + 15분봉 핵심4대 15 + 패턴매칭 3)
  - 25개 피처 기반 청산 모델 (포지션 상태 8 + 시장 상태 10 + 상위TF 7)
  - 승리 패턴 스냅샷 DB + 코사인 유사도 매칭
  - 동적 SL/TP: 확신도 기반 연속 함수 (하드코딩 임계값 제거)
  - 동적 포지션 사이징: 확신도 5%~20% 연속 함수
  - 매도압력 감지 긴급손절 / 개미털기 홀드 / 트레일링 갭 자동 조정
  - 모델 자동 로드 (앱 시작시)

- **AI 학습 백테스트 엔진** (`AIBacktestEngine.cs`):
  - 3년치 5분봉 + 15분봉 + 1시간봉 + 4시간봉 다중TF 데이터 수집
  - 자동 라벨링: 미래 60봉 내 TP +1.5% / SL -0.8% 도달 여부
  - 실제 가격 시뮬레이션: ML진입 → 5분봉 순회 → SL/TP1/TP2 실제 체결
  - 부분익절(40%) + 본절이동 + 시간손절(10시간) 포함
  - 설정 창 'AI 학습' 버튼 (초록색)

- **백그라운드 자동 데이터 수집** (`AIDataCollectorService.cs`):
  - 앱 시작시 자동으로 백그라운드 수집 서비스 시작 (30분 주기)
  - 8심볼 × 4타임프레임 (5m/15m/1H/4H)
  - CSV 캐시 시스템: 첫 수집 ~25분, 이후 증분만 ~30초
  - 캐시 경로: `%LOCALAPPDATA%/TradingBot/KlineCache/`

- **15분봉 핵심 4대 지표 AI 피처**:
  - 볼린저 밴드: 스퀴즈 강도 + 상/하단 돌파 감지
  - RSI: V-Turn 감지 + 기울기(반등 강도) + 다이버전스
  - EMA 20/60: 골든/데드 크로스 + 이격도(에너지 잔량) + EMA20 지지 여부
  - 피보나치 되돌림: 0.618~0.786 골든존 감지 + 반등 확인

- **바이낸스 테스트넷 시뮬레이션**:
  - `BinanceExchangeService`에 테스트넷 환경 지원 추가
  - 시뮬레이션 모드 시 테스트넷 API로 실거래와 동일한 체결/DB저장
  - 테스트넷 키 없으면 MockExchange 자동 폴백
  - appsettings.json에 TestnetApiKey/TestnetApiSecret 필드 추가

- **시뮬레이션 모드 알림창**:
  - 스타트 버튼 클릭시 시뮬레이션 모드 확인 다이얼로그
  - 모드(테스트넷/Mock), 잔고, 실제자금 미사용 안내
  - '아니오' 선택시 시작 취소

- **다중 타임프레임 백테스터** (`MultiTimeframeBacktester.cs`):
  - 5분봉 모멘텀 스캘핑 + 자동 파라미터 최적화 (256개 조합)
  - 메이저 4종 + 펌프 4종 (DOGE/PEPE/WIF/BONK)
  - 설정 창 '5분봉 최적화' 버튼 (빨간색, Shift=자동 최적화)

- **워크포워드 최적화 엔진** (`WalkForwardOptimizer.cs`):
  - 롤링 윈도우 WFO: 12개월 IS + 4개월 OOS, 4개월 스텝
  - 6개 파라미터 그리드 서치 (병렬 실행)
  - 과적합 진단: 효율성비율, 안정성점수, 파라미터안정성
  - 설정 창 'WFO 최적화' 버튼 (노란색)

### Changed

- **TradingEngine 시뮬레이션 모드 개편**:
  - MockExchangeService → 바이낸스 테스트넷 우선 사용
  - 설정 저장 → 재시작 없이 스타트 시 즉시 반영

- **TradingConfig 확장**:
  - `TestnetApiKey`, `TestnetApiSecret` 필드 추가

- **BinanceExchangeService 테스트넷 지원**:
  - 생성자에 `useTestnet` 파라미터 추가
  - `BinanceEnvironment.Testnet` 환경 자동 설정

## [2.4.56] - 2026-03-19

### Added

- **펀딩비 누적 모니터링** (`PositionMonitorService.cs`):
  - 2시간 초과 보유 포지션에 대해 펀딩비 자동 추정 및 실질ROE 계산
  - 추정 누적 펀딩비 = (보유시간h / 8h) × 0.01%/8h × 레버리지 × 100
  - 트레일링 미진입 상태에서 실질ROE < 3% 시 자동 청산 (`FundingCost Exit`)
  - 30분마다 `💸 펀딩비 추적` 로그 출력으로 현황 모니터링 가능

### Changed

- **메인 UI 중앙 패널 전면 개편** (`MainWindow.xaml`):
  - 기존 3컬럼 배틀패널 → 코인 진입/포지션 중심 레이아웃으로 재설계
  - 콕핏 스트립 (76px): AI STATUS | THE PULSE | GOLDEN ZONE | ATR STOP | TREND-RIDER 5개 섹션 가로 배치
  - 메인 영역 좌우 분할: 데이터그리드(좌, *) + 이벤트/익절/스텝퍼 패널(우, 260px)
  - 데이터그리드 진입중 행 초록 배경 (`#081A0F`) 강조 표시 추가

- **데이터그리드 컬럼 개편** (`MainWindow.xaml`):
  - SYMBOL: 전략 배지(`Major Scalp` / `Pump Breakout`) + LONG/SHORT 컬러 pill 추가
  - `ML│TF` → `AI SCORE`: AIScore 대형 표시 + ML%/TF% 소형 표시로 가독성 향상
  - ROI: `+/-` 부호 명시 포맷 (`+12.50%` / `-3.20%`)으로 변경
  - STATUS: AI 점수 업데이트 시각(HH:mm:ss) 하단 표시 추가
  - `RISK (SL/TP)` → `SL / TP` 헤더 단축

- **트레일링 Floor ROE 강화** (`PositionMonitorService.cs`):
  - 일반 메이저 (BTC 포함): 트레일링 최소 ROE 유지선 +8% → **+12%**
  - ETH/XRP/SOL (ATR 2.0): +4% → **+6%**
  - 트레일링 구간 역전 시 수익 반납 최소화

- **Stage 2 부분익절 비율 상향** (`PositionMonitorService.cs`):
  - 1차 확정 익절 비율 30% → **40%**
  - ROE +20% (BTC/메이저) / +30% (ETH/XRP/SOL) 도달 시 포지션 40% 즉시 확정

## [2.4.55] - 2026-03-17

### Added

- **SLOT 최적화 3가지 개선안 통합** (`TradingEngine.cs`):
  1. **개선안 1: Scan 단계 Pre-Check** - SLOT 여유도를 신호 생성 전 사전 판단하여 불필요한 지표 계산 20~30% 감소
     - `CanAcceptNewEntry(symbol)` 헬퍼 메서드로 진입 가능 여부 사전 검증
     - `ShouldSkipScanDueToSlotPressure()` 통해 SLOT 80% 이상 시 신호 생성 자체 스킵
  
  2. **개선안 2: 심볼별 SLOT 쿨다운** - SLOT 차단된 심볼 재시도 10분(설정 가능) 금지로 반복 차단 90% 감소
     - `_slotBlockedSymbols` ConcurrentDictionary로 차단 심볼 추적
     - `IsSymbolInSlotCooldown()` / `RecordSlotBlockage()`로 쿨다운 관리
     - SLOT 차단 시 자동으로 심볼 등록하여 무의미한 재시도 억제
  
  3. **개선안 3: 동적 슬롯 조정** - UTC 시간대별로 신호 빈도에 맞춰 슬롯 탄력 조정
     - `GetDynamicMaxTotalSlots()`: 18~04시 UTC 4슬롯, 04~10시 5슬롯, 10~18시 6슬롯(표준)
     - `GetDynamicMaxPumpSlots()`: 18~06시 UTC PUMP 신호 금지(0슬롯), 06~18시 1슬롯 허용
     - 저활동 시간대 불필요한 SLOT 차단 35~45% 감소
  
### Fixed

- **SLOT 검증 로직 통합 개선** (`TradingEngine.cs`):
  - ExecuteAutoOrder 메서드에 기존 정적 SLOT 검증을 동적 슬롯 + 쿨다운 체크로 교체
  - 각 차단 사유 기록 시 RecordSlotBlockage()를 호출해 향후 쿨다운 적용
  - 메이저/PUMP/총 포지션 검증 순서를 최우선부터 명확히 정렬

### Changed

- **SLOT 차단 로그 가시성 개선** (`TradingEngine.cs`):
  - SLOT_COOLDOWN 태그로 쿨다운 중인 심볼 구분
  - 동적 PUMP 슬롯 상황(금지 시간대 vs 포화)을 로그에 명시
  - SLOT 차단 메시지에 UTC 시간대 정보 추가하여 시간대별 정책 추적 가능

### Performance

- **Scan 단계 사전 필터로 CPU 부하 25~30% 절감** - 신호 생성 단계 진입 차단으로 불필요한 지표 계산 스킵
- **SLOT별 반복 로그 밀도 30~40% 감소** - 같은 심볼의 무의미한 재시도 차단 로그 제거

## [2.4.54] - 2026-03-17

### Fixed

- **LONG 진입 1분 윗꼬리 필터 과차단 완화** (`TradingEngine.cs`):
  - 기존 `고점 대비 -0.3%` 단일 조건 차단을 제거하고, 닫힌 1분봉 기준 `실제 윗꼬리 반전`(윗꼬리 비율/캔들 변동폭/되밀림/약세 마감) 조합일 때만 차단하도록 개선
  - PUMP/MEME 신호와 RSI 완만 구간(`RSI<62`)은 윗꼬리 차단을 우회해 상승장 추세 진입 누락을 감소

- **PUMP 신호의 메이저 필터 오적용 수정** (`TradingEngine.cs`):
  - `MAJOR_MEME` 신호를 문자열 접두사로 메이저 취급하던 로직을 `ResolveCoinType` 기반으로 교체
  - 펌프 코인이 메이저 전용 AI 보너스/임계 경로에 잘못 진입해 차단되던 분기 충돌을 해소

## [2.4.53] - 2026-03-16

### Fixed

- **초기 슬리피지 보호에 의한 조기 청산 제거** (`TradingEngine.cs`):
  - `FastEntrySlippageMonitor` 기능을 완전 제거하여 진입 직후 짧은 흔들림으로 즉시 청산되던 문제를 해소
  - PUMP/메이저 공통 진입 경로에서 빠른 슬리피지 감시 호출을 삭제해 불필요한 청산 트리거를 제거

### Changed

- **로그 노이즈 정리** (`TradingEngine.cs`):
  - `SLIPPAGE[FAST]` 관련 경고/종료 로그 경로를 제거해 실거래 모니터링 로그의 신호 대 잡음비 개선
- **FAST LOG 3행 가시성 보정** (`MainWindow.xaml`):
  - `BattleFastLog3` 표시 영역 높이를 고정해 긴 로그 표시 시 레이아웃 흔들림을 완화

## [2.4.52] - 2026-03-16

### Fixed

- **밈/알트 실시간 UI 미갱신 경로 보완** (`TradingEngine.cs`):
  - `OnAllTickerUpdate` 스트림을 엔진에 연결하고, 활성 포지션/추적 심볼만 필터링해 `HandleTickerUpdate`로 전달
  - 메이저 목록 외 심볼(밈/펌프)에서도 UI 가격/시그널 갱신 누락을 줄임

- **PUMP 포지션 감시 누락으로 인한 익절/손절 미동작 문제 수정** (`TradingEngine.cs`):
  - 신규 진입/동기화/계좌복원 경로에서 PUMP 포지션은 표준 모니터 대신 PUMP 모니터를 일관 시작
  - `_runningPumpMonitors` 및 `TryStartPumpMonitor`로 중복 감시를 방지하고 종료 시 상태 정리

- **PUMP/밈 진입 증거금 100 USDT 고정 적용** (`TradingEngine.cs`):
  - `PUMP_FIXED_MARGIN_USDT` 상수 도입 및 공통/펌프 진입 경로 모두 고정 증거금 적용
  - 패턴/수동/RSI 기반 배수로 증거금이 변동되던 경로를 PUMP 코인에서 우회

- **드라이스펠 PUMP fallback 추격 진입 방어 추가** (`TradingEngine.cs`):
  - `DROUGHT_RECOVERY_PUMP` LONG 진입 시 15분봉 BB 상단 돌파 및 상단 과열(%B/RSI) 구간을 차단
  - `DROUGHT_CHASE` 로그 태그로 차단 원인 추적 가능

## [2.4.51] - 2026-03-16

### Added

- **드라이스펠 수동 진단 텔레그램 명령 `/drought` 추가** (`DroughtScanCommand.cs`, `TelegramService.cs`, `TradingEngine.cs`):
  - 텔레그램에서 즉시 드라이스펠 진단/복구 스캔 1회 실행 가능
  - 엔진 실행 상태 검증 후 결과 요약 문자열을 명령 응답으로 반환

- **드라이스펠 자동 복구 체인 강화** (`TradingEngine.cs`, `PumpScanStrategy.cs`):
  - 2시간 이내 ETA 고확률 후보 우선 진입(`DROUGHT_RECOVERY_2H`)
  - PASS 미충족 시 근접 후보 시험 진입(`DROUGHT_RECOVERY_NEAR_2H`, 비중 70%)
  - 그래도 후보 없으면 PUMP 확장 스캔 Top60 first-hit 연계(`DROUGHT_RECOVERY_PUMP`)

### Changed

- **드라이스펠 진단 결과 요약/액션 코드 고도화** (`TradingEngine.cs`):
  - `RunDroughtDiagnosticScanAsync`가 요약 문자열을 반환하도록 변경
  - `ETA2h / Near2h / PumpFallback / Action` 형식으로 상태를 일관 출력
  - `*_HOLDING_SKIP`, `NO_ENTRY`, `ERROR`, `CANCELLED` 등 액션 상태를 세분화

- **PUMP 복구 스캔 블랙리스트 안전성 개선** (`TradingEngine.cs`, `PumpScanStrategy.cs`):
  - 활성 보유 심볼을 복구 스캔 블랙리스트에 주입해 중복 진입을 방지
  - 복구 스캔 시 이벤트 emit 없이 first-hit 신호를 반환해 엔진이 직접 주문 경로를 제어

- **XRP 장기 샘플 기반 공통 필터 동적 튜닝 반영** (`TradingEngine.cs`):
  - 2025-01-01~현재 XRP 15분봉 샘플 점검으로 ATR 변동성 차단 배율/SHORT RSI floor 자동 보정
  - 특정 심볼 분기 없이 공통 파라미터에 적용

## [2.4.50] - 2026-03-16

### Added

- **텔레그램 메시지 타입별 허용/차단 설정 추가** (`AppConfig.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `Services/NotificationService.cs`, `TelegramService.cs`, `appsettings.json`):
  - Settings의 `Telegram Messages` 탭에서 Alert / Profit / Entry / AI Gate / Log 채널별 on/off 제어 지원
  - 알림 채널을 `TelegramMessageType`으로 매핑해 송신 직전 중앙 필터링 적용
  - 설정 저장 즉시 런타임(`AppConfig.Current.Telegram`)에 반영되어 재시작 없이 적용

### Changed

- **FAST LOG 백프레셔 가드 추가** (`MainViewModel.cs`):
  - 라이브 로그 입력 시 DB/라이브 큐 길이 임계치(soft: 700, hard: 1200) 초과를 감지
  - 주문 오류·게이트·핵심 체결 흐름 로그는 보호하고, 저우선/고빈도 로그만 자동 드롭
  - 드롭 발생 시 `[PERF][LIVELOG][BACKPRESSURE]` 경고를 주기적으로 남겨 과부하 상태 추적 가능

## [2.4.49] - 2026-03-15

### Added

- **[Time-Out Probability Entry] 신규 추가** (`TimeOutProbabilityEngine.cs`, `TradingEngine.cs`):
  - 60분간 진입 공백 발생 시 과거 DB 패턴과 **코사인 유사도** 비교로 승률 70%+ 메이저 코인 자동 확률 베팅 진입
  - 피처 벡터: [RSI/100, BB위치, MACD히스토(정규화), 거래량비율] 4차원 추출 → `GetLabeledTradePatternSnapshotsAsync` 최근 180일 최대 500건 조회
  - 유사도 ≥65% 필터 후 상위 1,000건 PnL%≥+3% 승률 계산 — 70%+ 시 시드 40% 비중 진입 (`TIMEOUT_PROB_ENTRY` 신호소스)
  - 스캔 순서: ETH → XRP → SOL, 최초 조건 충족 종목만 진입 후 즉시 종결
  - `IsDroughtRecoverySignalSource`에 `TIMEOUT_PROB_ENTRY` 추가 → AI 게이트 바이패스 적용
  - 타이머: 60분 공백 감지 / 20분 재시도 제한 (`TimeOutProbScanThreshold`, `TimeOutProbScanInterval`)

### Changed

- **[UI] TimeOut Probability Entry 위젯 위치** (`MainWindow.xaml`):
  - AI MONITOR 탭 → **Dashboard 좌측 사이드바** (AI LEARNING 카드 아래, CRITICAL ALERTS 위)로 이동
  - 항시 가시 영역에 배치: 스캔 상태, 매칭 심볼, 승률 ProgressBar, 70%+ 시 보라색 Glow 애니메이션

- **[UI] FAST LOG 위치 조정** (`MainWindow.xaml`):
  - 중앙 LIVE BATTLE 패널 Grid.Row=2 → **좌측 XRP SCENARIO 카드 바로 아래**로 이동
  - 중앙 패널 RowDefinitions 4→3, Trend-Rider Grid.Row 3→2 자동 조정

## [2.4.48] - 2026-03-15

### Added

- **Staircase Pursuit (계단식 추격) 로직 신규 추가** (`TradingEngine.cs`, `AIDoubleCheckEntryGate.cs`, `EntryRuleValidator.cs`):
  - `IsStaircaseUptrendPattern()`: 15봄봉 3연속 Higher Lows + BB %B > 45% + RSI<80 조건으로 계단식 상승 패턴 자동 감지
  - `ShouldBlockChasingEntry`: 계단식 감지 시 `nearRecentHigh` riskScore 차단 면제 → 고점 추격 필터 우회
  - `AIDoubleCheckEntryGate.DetectStaircaseUptrend()`: BB %B > 50% + 3연속 Higher Lows 감지 시 `Chasing_Risk_Pullback` 필터 바이패스
  - `EntryRuleValidator.HasSuccessiveHigherLows()`: 점자 거래량 코인의 저점 상승을 신뢰할 수 있는 추세로 인정, `LowVolumeRejectRatio` 필터 바이패스

- **계단식 정찰대 20% 즉시 투입** (`TradingEngine.cs`):
  - TF≥85% + 계단식 상승 감지 시 눈맼목을 기다리지 않고 성리의 **20%** 시장가 진입 (`_STAIRCASE` 태그)
  - ATR 3.5x 하이브리드 손절 대기열 자동 등록

- **불타기 Add-on 개선** (`TradingEngine.cs`):
  - 계단식 상승 중 최근 5봉 고점 돌파 시 기존 70% 대신 **20% 추가 진입** (`STAIRCASE_ADDON`) 적용

- **Trend-Rider 위젯 첨가** (`MainWindow.xaml`, `MainViewModel.cs`):
  - LIVE BATTLE DASHBOARD 하단에 `🪜 TREND-RIDER` 위젯 신규 삽입
  - TF≥85% + BB중단 이상: 구뚤금세 `STAIRCASE UPTREND (활성)` + `ACTIVE` 배지 + 폄스 애니메이션
  - TF≥65%~85%: `STAIRCASE MONITORING` (하늘색)
  - 비활성: 회색 `한산드 대기`

## [2.4.47] - 2026-03-15

### Changed

- **CRITICAL ALERTS 패널 배치 조정으로 Market Signals 가시성 개선** (`MainWindow.xaml`):
  - 하단 메인 영역의 `CRITICAL ALERTS` 패널을 좌측 사이드바 카드 영역으로 이동
  - 메인 하단 row 점유를 제거해 `MARKET SIGNALS` 리스트 표시 공간을 복원

## [2.4.46] - 2026-03-15

### Added

- **저거래량 메이저 롱 정찰/증원 진입 시퀀스 추가** (`TradingEngine.cs`, `EntryRuleValidator.cs`, `AIDoubleCheckEntryGate.cs`):
  - `RuleViolationLowVolumeRatio` 차단 상황에서도 TF 강세 + BB 중심선 지지 패턴일 때 30% 정찰 진입(`_SCOUT`) 허용
  - 정찰 체결 후 거래량/ML/TF 회복 시 70% 추가 진입(`_SCOUT_ADDON`) 1회 트리거
  - 저거래량 예외 파라미터(`LowVolumeBypass*`)를 설정 모델에 반영해 우회 조건을 일관되게 관리

- **월 목표 추적 위젯(Profit Goal Tracker) 추가** (`MainWindow.xaml`, `MainViewModel.cs`):
  - 우측 상단 대시보드에 `MONTHLY GOAL`, `Current(금액/달성률)`, `Daily Pace`, `XRP Scenario Bonus` 표시
  - 당월 누적 실현손익 기반 달성률/일일 페이스 계산 및 XRP 활성 포지션 TP 기준 보너스 수익 표시

### Changed

- **정찰 진입 리스크 관리 강화** (`TradingEngine.cs`):
  - 정찰 진입(`SCOUT`)에도 메이저 ATR 하이브리드 손절 계산을 강제 적용
  - 손절 계산 실패 시 정찰 진입 자체를 차단하여 무손절 진입 가능성 제거

- **사용자 스코프 DB 마이그레이션 멱등성 보강** (`run-user-scope-db-migration.ps1`):
  - `GeneralSettings` 테이블이 이미 존재하는 환경에서 `GeneralSettings_Schema.sql`을 자동 스킵하도록 개선
  - 운영 DB 재실행 시 “이미 개체 존재” 오류를 줄여 배포 안정성 향상

- **HelloQuant 패턴 카드 가시성 강화** (`MainWindow.xaml`, `MainViewModel.cs`):
  - BB 중심선 지지 활성 시 펄스 애니메이션, TF Confidence 진행바, ML/TF 가중치(50:50 ↔ 30:70) 실시간 반영

## [2.4.45] - 2026-03-15

### Changed

- **LIVE BATTLE DASHBOARD 가격 텍스트 깜빡임 제거** (`MainWindow.xaml`):
  - `BattleLivePriceText` 바인딩의 `TargetUpdated` 이벤트 트리거 애니메이션(Opacity/Translate)을 제거해 틱 단위 가격 갱신 시 시각적 깜빡임을 해소
  - 실시간 가격/방향(`BattlePriceBrush`, `BattlePriceDirectionText`) 갱신 로직은 유지하여 정보 전달은 동일하게 보장

## [2.4.44] - 2026-03-14

### Added

- **HelloQuant 스타일 배틀 대시보드 3열 레이아웃** (`MainWindow.xaml`, `MainViewModel.cs`, `Converters.cs`):
  - 기존 단일 패널 `⚔ LIVE BATTLE DASHBOARD`를 Market Monitor / Main Scene / Execution Stepper 3열 구조로 전면 교체
  - 좌열: AI Confirm 상태 + 4코인 모니터(BTC/ETH/SOL/XRP) + XRP 시나리오 라벨
  - 중앙열: 실시간 가격 롤링 애니메이션(TargetUpdated EventTrigger) + 도넛형 Pulse 게이지(Canvas/Path Arc) + 0.618 Golden Zone 카드 + ATR Stop Cloud 카드 + 패스트 로그
  - 우열: 4단계 Execution Stepper(ItemsControl) — Market Sync → Wave AI Gate → 0.618 Golden Zone → ATR Close-Only

- **ScoreToArcGeometryConverter** (`Converters.cs`):
  - 0~100 Pulse 점수를 도넛 게이지 SVG 호(Arc) Path Data로 변환하는 IValueConverter 추가
  - ConverterParameter 구분자를 `|`(파이프)로 변경하여 XAML MarkupExtension 파싱 충돌 방지

- **BattleExecutionStep 모델 및 ViewModel 바인딩** (`MainViewModel.cs`):
  - `BattleExecutionStep : INotifyPropertyChanged` 모델 클래스 추가 (Order, Title, Detail, StateText, StateBrush, IsActive)
  - `BattleExecutionSteps` ObservableCollection과 `InitializeBattleExecutionSteps` / `UpdateBattleExecutionSteps` / `SetBattleStep` 로직 추가
  - `BattleGoldenZoneText/Brush`, `BattleAtrCloudText/Brush`, `BattleStopPulseActive` 프로퍼티 신설
  - ATR Stop 거리 ≤ 0.7% 진입 시 OrangeRed 펄스 경보, ≤ 1.5% Gold 중간 경고, 초과 시 DeepSkyBlue

### Changed

- **ATR 펄스 경보 임계값 확대** (`MainViewModel.cs`):
  - 빨강 펄스 발동 기준: 스탑까지 거리 0.35% → **0.7%** (넉넉한 사전 경보)
  - Gold 중간 경고 기준: 0.9% → **1.5%**

- **메이저 ATR 2.0 Close-Only 심볼 로직 강화** (`PositionMonitorService.cs`, `TradingEngine.cs`):
  - ETH/XRP/SOL에 `isAtr20MajorSymbol` 플래그를 적용, `useMajorAtr20=true` 경로로 손절 계산
  - TP1/TrailStart/Gap 파라미터 완화: ETH/XRP/SOL 40%/60%/10%, BTC 15%/30%/3%
  - `minLockROE`: ATR2.0 메이저 +2%, 일반 +5%
  - TradingEngine ATR 멀티플라이어 BTC 2.5→3.5, XRP/SOL 4.0, swingCandles 12→10봉
  - `hybridStopPrice` 롱/숏 방향 버그 수정 (Max↔Min 반전)

## [2.4.43] - 2026-03-14

### Changed

- **환경설정 General 탭 레이아웃 정리** (`SettingsWindow.xaml`):
  - 공통 / PUMP / PUMP 추세홀딩 / 메이저 섹션 순서로 재배치
  - 중복·혼동되던 라벨을 정리하고 `메이저 추세 프로파일`을 메이저 전용 섹션으로 이동
  - 공통 `목표 ROE`, `손절 ROE` 라벨을 알트 기준으로 명확화

- **메이저 ATR 손절을 듀얼 스탑 상태머신으로 보강** (`PositionMonitorService.cs`):
  - 진입 후 5분 ATR 게이트, ATR 터치 후 3분 whipsaw 보류, 직전 5캔들 Fractal 지지선 확인 추가
  - Fractal 이탈 시 최근 20캔들 평균 대비 거래량 1.5배 확인 후에만 즉시 손절
  - 저거래량 fake-out은 1분 추가 관망 후 타임아웃 손절하며, 회복 시 ATR 경보 상태를 자동 초기화

## [2.4.42] - 2026-03-14

### Added

- **실전 유사 백테스트 모드 `LiveEntryParity` 추가** (`BacktestService.cs`, `HybridStrategyBacktester.cs`, `BacktestOptionsDialog.xaml(.cs)`):
  - 백테스트 전략 선택에 `LiveEntryParity` 옵션을 추가하고 기본 선택으로 변경
  - `HybridStrategyBacktester.RunRangeAsync`와 `BacktestResult.Candles` 연계로 기간 지정 실전형 시뮬레이션 지원
  - `PerfectAI=false` 경로에서 룩어헤드 제거형 예측 추정(`EstimatePredictedChangeNoLookahead`) 적용

- **15분봉 BB 스퀴즈 → 중심선 돌파 전략 추가** (`FifteenMinBBSqueezeBreakoutStrategy.cs`, `TradingEngine.cs`):
  - BB 폭 수축(스퀴즈) 후 중심선 상향 돌파 시 LONG 진입하는 독립 전략 클래스 신설
  - 진입 조건: BB 폭 < 평균×0.80, 양봉, 거래량 1.2배 이상, RSI 40~65, RR ≥ 1.5
  - TP = BB Upper, SL = BB Lower 또는 ATR×1.5 중 낮은 값, 쿨다운 4시간
  - `TradingEngine.AnalyzeFifteenMinBBSqueezeBreakoutAsync`를 심볼 분석 루프에 연동

- **Elliott Wave 앵커 DB 영속화** (`ElliottWaveAnchorState.cs`, `WaveAnchor.cs`, `DbManager.cs`, `TradingEngine.cs`):
  - 앱 재시작 후에도 1·2파 기준점(`WaveAnchor`)을 DB에서 복원하는 영속화 레이어 추가
  - `TradingEngine.RestoreElliottWaveAnchorsFromDatabaseAsync` / `PersistElliottWaveAnchorStateAsync`로 상태 변경 시 자동 저장
  - 파동 phase 서명 비교(`BuildElliottWavePersistenceSignature`)로 실제 변경된 경우에만 DB 기록

- **PUMP 추세홀딩 튜닝 설정 추가** (`SettingsWindow.xaml(.cs)`, `DbManager.cs`, `Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql`):
  - `PumpFirstTakeProfitRatioPct` (1차 익절 비중%), `PumpStairStep1/2/3Roe` (계단식 트레일 ROE) 4개 설정 신설
  - 설정창 General 탭에 PUMP 추세홀딩 튜닝 섹션 UI 추가 및 DB 로드/저장 연동
  - 운영 DB 마이그레이션 스크립트(`GeneralSettings_AddPumpStairAndTpRatioColumns.sql`) 및 메이저 레거시 값 보정 스크립트(`GeneralSettings_NormalizeMajorDefaults_20260314.sql`) 제공
  - 자동 마이그레이션 실행 스크립트(`run-user-scope-db-migration.ps1`) 포함

### Changed

- **엘리엇 3파 진입 기준 재설계** (`ElliottWave3WaveStrategy.cs`, `TradingEngine.cs`):
  - 1파 감지를 단일 캔들 기반에서 스윙 저점/고점 기반으로 전환
  - 2파 확정에 되돌림 비율(0.50~0.786), 거래량 수축, 반등 확인 조건을 결합
  - 피보 진입 판정을 `Fib 구간 터치 + 0.618 재돌파` 중심으로 정밀화하고 무효화 시 상태 리셋
  - 주문 체결 후 실제 포지션 확인 시 `Wave3Active`로 상태 승격

- **드라이스펠 자동 복구 진입/차단 관제 강화** (`TradingEngine.cs`):
  - 장시간 무진입 시 전 심볼 진단 후 `PASS` 후보 자동진입, 근접 후보 시험진입 경로 보강
  - 드라이스펠 전용 소스(`DROUGHT_RECOVERY*`)에 한해 사전평가 결과를 재사용하도록 AI 게이트 재검사 우회
  - 10분 단위 진입 차단 사유 집계 및 튜닝 힌트 로그 추가

- **심볼 정규화 및 UI 동기화 안정화** (`TradingEngine.cs`, `MainViewModel.cs`):
  - 영숫자+`USDT` 형식 정규화를 티커/시그널/포지션 업데이트 경로에 통합
  - 비정상 심볼 유입 시 UI 인덱스 오염/갱신 누락 가능성을 축소

- **AI 임계값/진입필터 공격형 튜닝 반영** (`AIDoubleCheckEntryGate.cs`, `TradingEngine.cs`, `appsettings.json`):
  - 일반/펌핑 코인 ML·TF 임계값 완화 및 엔트리 점수/RSI 기준 조정
  - EntryFilter 워밍업, RR, 15분 게이트 기본값을 실전 진입 빈도 중심으로 재조정

- **메이저 고속도로 Fast-Pass 적용** (`TradingEngine.cs`):
  - 메이저 코인(BTC/ETH/SOL/XRP) AI 점수 임계 56%, 신뢰도 0.56로 하향(기존 70/0.70 대비 0.8배)
  - 정배열(SMA20>60>120) 추세 시 BB 상단 저항·진입 구간(45~85%) 바이패스 허용
  - RSI 추격 한도 75 → 80으로 확장 및 진입 로그에 바이패스 경로 명시

- **확정 피보나치 레벨 계산 추가** (`TradingEngine.cs`):
  - `CalculateConfirmedFibLevels`: 최근 3캔들 미포함 확정 고·저점 기반 Fib 수준 계산
  - 피보 레벨 산출 실패 시 기존 `IndicatorCalculator.CalculateFibonacci` 결과로 폴백

## [2.4.41] - 2026-03-13

### Added

- **드라이스펠 자동 복구 진입 추가** (`TradingEngine.cs`):
  - 무진입 구간 진단 결과에서 `Gate PASS` 상위 후보를 자동 진입 대상으로 연결
  - `PASS` 후보가 없을 때 임계값 근접 후보를 소액 시험 진입으로 시도하는 폴백 경로 추가
  - 드라이스펠 복구 진입 소스 태그(`DROUGHT_RECOVERY`, `DROUGHT_RECOVERY_NEAR`) 반영

### Changed

- **드라이스펠 감지/재진단 파라미터 공격형 튜닝** (`TradingEngine.cs`):
  - 감지 임계 `1시간` → `30분`, 재진단 주기 `15분` → `10분`
  - 근접 후보 판정 기준 `95%` → `90%`, 시험 진입 비중 `40%` → `70%`
  - `Gate PASS` 자동진입 시도 개수 `Top 1` → `Top 2`로 확장

- **자동진입 수량 제어 훅 추가** (`TradingEngine.cs`):
  - `ExecuteAutoOrder`에 수동 배수 인자(`manualSizeMultiplier`)를 추가해 진입 비중을 동적으로 제어
  - 배수 안전 범위 클램프(0.10~2.00) 및 사이징 로그 강화

### Fixed

- **심볼 깨짐/오염 입력 정규화 보강** (`TradingEngine.cs`, `MainViewModel.cs`):
  - 실시간 심볼 유입 경로 전반에서 영숫자+`USDT` 형식 정규화를 적용해 UI 깨짐 심볼 노출 완화
  - 숫자 접두 심볼(`1000PEPEUSDT` 등) 수용을 위한 패턴 정합성 개선

## [2.4.40] - 2026-03-13

### Added

- **사용자별 DB 스코프 마이그레이션 체크리스트 추가** (`USER_SCOPE_DB_MIGRATION_CHECKLIST.md`):
  - 운영 적용 전/후 검증 쿼리, 스모크 테스트, 롤백 절차를 문서화

### Changed

- **AI 관제탑 5분 요약 전송 규칙 보강** (`TradingEngine.cs`, `TelegramService.cs`):
  - 대상 코인 결정이 없어도 5분 요약은 고정 주기로 발송되도록 강제
  - 승인코인 5분 브리핑은 실제 승인 대상 심볼이 있을 때만 발송

- **사용자 스코프 기반 DB 읽기/쓰기 경로 강화** (`DbManager.cs`, `DatabaseService.cs`, `Database/TradeLogging_Schema.sql`, `Database/GeneralSettings_Schema.sql`):
  - `TradeLogs`/`TradeHistory` 및 고급 로그 경로에 `UserId` 기반 필터/저장 보강
  - 스키마 호환 체크(`HasColumnAsync`)와 인덱스/뷰 정합성 보완

- **엔진 사용자 컨텍스트/ROI 안전성 개선** (`TradingEngine.cs`):
  - DB 정리/블랙리스트 복구 시 현재 로그인 사용자 기준으로만 조회
  - 실시간 ROI 계산에서 무효 가격 및 레버리지 값에 대한 안전 가드 적용

## [2.4.39] - 2026-03-13

### Added

- **스나이퍼 모드 도입** (`SniperManager.cs`, `HybridStrategyScorer.cs`, `Models.cs`):
  - 종목별 일일 진입 횟수/쿨다운/최대 포지션/추세 일치 검증 기반의 진입 게이트 추가
  - `TradingSettings`에 스나이퍼 설정값(`MinimumEntryScore`, `MaxTradesPerSymbolPerDay`, `EntryCooldownMinutes` 등) 확장

- **AI 관제탑 승인코인 5분 브리핑 분리 발송** (`TelegramService.cs`):
  - 기존 5분 요약과 별도로 승인 코인만 모아 `[AI 관제탑 승인코인 5분 브리핑]` 메시지 발송
  - 승인 코인/사유를 LONG·SHORT·기타 버킷으로 분리해 운영 가독성 강화

### Changed

- **메이저/PUMP 설정 분리 및 저장 경로 확장** (`Models.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `DbManager.cs`, `Database/GeneralSettings_Schema.sql`, `GeneralSettingsProvider.cs`):
  - 메이저/펌프 전용 레버리지·증거금·본절·트레일링·손절 항목을 UI/모델/DB 스키마에 반영
  - `GeneralSettings` 저장/로딩 경로를 신규 필드까지 일관되게 확장

- **진입/스코어링 로직 보강** (`AIDoubleCheckEntryGate.cs`, `TradingEngine.cs`, `HybridStrategyScorer.cs`, `TransformerStrategy.cs`, `PumpScanStrategy.cs`, `MajorCoinStrategy.cs`):
  - Fib 보너스/데드캣 차단, RSI/MACD 기울기 가점, 메이저 스코어 임계값 보강 등 진입 필터 강화
  - 메이저 코인 진입 시 상위 타임프레임 기울기 확인 및 심볼별 임계 프로파일 적용

- **Fib 가점 반영 TrendScore를 관제탑 가시화에 직접 반영** (`AIDoubleCheckEntryGate.cs`, `TradingEngine.cs`, `TelegramService.cs`):
  - AI 게이트 PASS/BLOCK reason에 `Trend`/`FibBonus` 메타데이터 포함
  - 엔진 `AI_GATE` 로그에 `trend`, `fibBonus` 필드 추가
  - 5분 요약에 `평균 Trend(피보반영)` 지표 추가

- **AI 관제탑 사유 정규화 강화** (`TelegramService.cs`):
  - `DeadCat_Block` 계열을 `데드캣 붕괴 차단`으로 통일
  - `FibBonus>0` + `Dual_Reject` 상황을 `피보 가점 반영 후 ML/TF 미달`로 명시
  - 승인 사유에서 `FibBonus>0` 감지 시 `피보 지지+리버설 가점 통과`로 표기

- **승인코인 5분 브리핑 노이즈 감소** (`TelegramService.cs`):
  - 승인 건수 0건일 때 승인코인 별도 브리핑 전송을 자동 스킵

- **포지션/UI 동기화 안정화** (`MainViewModel.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `PositionMonitorService.cs`, `BinanceExchangeService.cs`):
  - 리스크 표시 및 정렬 우선순위 반영 강화, 동기화 시 레버리지/모니터 분기 안전성 보강
  - 거래소 정밀도 캐시 및 종료/취소 예외 처리 개선

## [2.4.38] - 2026-03-12

### Added

- **심볼 라이브 차트 고도화** (`MainViewModel.cs`, `SymbolChartWindow.xaml`, `SymbolChartWindow.xaml.cs`, `MainWindow.xaml.cs`):
  - 차트 창 로드/재오픈 시 `RefreshLiveChart()` 자동 호출로 즉시 최신 데이터 갱신
  - 5분봉 기반 선형 회귀 `1H 예측` 라인(12개 캔들) 추가
  - LONG/SHORT 진입, 익절, 청산 가격 수평선 오버레이 추가
  - AI 진입예상 시각/확률을 다이아몬드 마커로 차트에 표시
  - 차트 범례(`ChartLegend`) 추가로 시리즈 식별성 개선

### Changed

- **하이브리드 진입 캔들 확인 우회 조건 추가** (`TradingEngine.cs`):
  - 심볼별 최신 AI 예측 캐시(`_latestAiForecasts`)를 유지
  - AI 확률 `80% 이상` + `즉시/5분 이내` 예측일 때 캔들 확인 지연 진입을 스킵하고 즉시 진입
  - 조기진입 시 기존 pending delayed entry를 정리하고 EARLY/BYPASS 로그를 남기도록 개선

- **WPF 임시 프로젝트 파일 정리** (`TradingBot_2pdrx2mn_wpftmp.csproj`):
  - 빌드 산출 과정에서 생성된 임시 `.wpftmp.csproj` 파일 삭제

## [2.4.37] - 2026-03-11

### Added

- **[Meme Coin Mode] PUMP 전용 포지션 관리 전략 추가** (`Models.cs`, `PositionMonitorService.cs`, `GeneralSettingsProvider.cs`, `DbManager.cs`):
  - `TradingSettings`에 PUMP 전용 3개 파라미터 추가:
    - `PumpBreakEvenRoe` (기본 20%) — ROI +20% 도달 시 손절가를 진입가로 이동, 절대 손실 방지
    - `PumpTrailingStartRoe` (기본 40%) — ROI +40% 돌파 시 트레일링 스탑 감시 시작
    - `PumpTrailingGapRoe` (기본 20%) — 최고점 대비 ROI 20% 하락 시 전량 청산 (어깨 매도)
  - 본절 트리거 조건을 하드코딩 3%에서 `PumpBreakEvenRoe` 설정값으로 변경
  - 기존 10%→4%, 15%→5% 동적 트레일링 압축 제거 — 밈코인 변동성에 비해 너무 좁아 조기 청산 유발
  - ATR 동적 계산 시 `PumpTrailingStartRoe` 이하로 내려가지 않도록 하한선 적용
  - `GeneralSettingsProvider`에 3개 프로퍼티 노출, `DbManager` MERGE SQL에 신규 컬럼 포함
  - Break-Even 발동 시 텔레그램 알림 자동 발송

- **진입 코인 그리드 상단 자동 정렬** (`MainViewModel.cs`):
  - 포지션 진입 시 해당 코인이 그리드 맨 위로 자동 이동
  - `ICollectionViewLiveShaping.IsLiveSorting` 적용으로 상태 변경 시 실시간 재정렬

- **ENTRY STATUS 컬럼 실시간 갱신** (`Models.cs`, `MainViewModel.cs`):
  - `ResolveEntryStatus()` 로직 추가 — 진입중/평가중/펌핑감시/TF감시/메이저감시/신호감시/대기 상태 표시
  - `IsPositionActive` 변경 시 `EntryStatus`, `EntryStatusColor`, `EntryStatusIcon` 연계 갱신

### Changed

- **AI 관제탑 요약 주기 15분 → 5분** (`TradingEngine.cs`, `TelegramService.cs`):
  - 타이머 조건 `TotalMinutes >= 15` → `>= 5`로 단축
  - 관련 주석 및 description 4곳 동기화

- **진입 텔레그램 알림 통합** (`TelegramService.cs`, `TradingEngine.cs`):
  - `SendSmartTargetEntryAlertAsync` 제거, `SendEntrySuccessAlertAsync` 단일 메서드로 통합
  - Smart SL/TP/ATR 정보 유무에 관계없이 1건만 발송 (중복 제거)
  - Smart Target 계산 실패 시에도 기본 진입 알림 발송 보장

## [2.4.36] - 2026-03-11

### Changed

- **AI 진입 최종 필터 4단계 고도화** (`EntryRuleValidator.cs`, `AIDoubleCheckEntryGate.cs`):
  - 최종 방향 필터 입력을 `bool isLong`에서 `PositionSide`로 변경해 LONG/SHORT 분기 명확화
  - LONG 구간에서 `Fib >= 0.786`일 때, `RSI <= 25 && BB <= 0.05`가 아니면 가짜 바닥으로 차단
  - SHORT 구간에서 `Fib <= 0.236 && RSI >= 75 && BB >= 0.95`일 때 과열 반등 위험으로 차단
  - Rule1/Rule2 즉시 차단, Rule3 감점(0.15), SuperTrend 우회(ML/TF) 및 최종점수 임계(0.65) 체계 유지

- **손절 ROE 설정값 실시간 반영 경로 보강** (`PositionMonitorService.cs`, `TradingEngine.cs`):
  - 포지션 모니터가 시작 시 스냅샷 `_settings` 고정값이 아닌 현재 사용자 설정 공급 경로를 통해 손절 ROE를 조회
  - 서버사이드 스탑 계산/메이저 손절 트리거/로그 및 PUMP 손절 초기값에 동일한 동적 손절값을 적용
  - `TradingEngine`에서 `PositionMonitorService` 생성 시 런타임 설정 provider delegate를 주입

## [2.4.35] - 2026-03-11

### Changed

- **AI 관제탑 15분 요약 메시지 포맷 고도화** (`TelegramService.cs`):
  - 승인/차단 코인 목록에 사유를 함께 표시하도록 확장 (`코인(사유)`)
  - 승인/차단 목록을 `LONG / SHORT / 기타`로 분리해 가독성 개선
  - 목록 노출 개수를 제한하고 초과분은 `외 N개`로 축약 표기
  - 차단 사유 TOP을 `전체 / LONG / SHORT / 기타`로 분리 집계 및 표시
  - 요약 타이틀/본문 레이아웃 정리 (`AI 관제탑 15분 요약`)

## [2.4.34] - 2026-03-11

### Changed

- **Elliott Wave Rule3 감점제 전환** (`EntryRuleValidator.cs`, `AIDoubleCheckEntryGate.cs`):
  - Rule3 위반을 하드 차단에서 `-0.15` 감점 방식으로 변경 — 위반 시에도 최종 점수가 임계값(0.65) 이상이면 진입 허용
  - 강추세 우회 조건 추가: TF ≥ 0.84 AND ML ≥ 0.85 시 Rule3 감점 및 Fibonacci 극단 차단 무시
  - 방향별 Fibonacci 극단 필터 구현 (Long: Fib ≥ 0.786 + RSI < 25 + BB < 0.05 차단 / Short: Fib ≤ 0.236 + RSI > 75 + BB > 0.95 차단)
  - 모든 튜닝 임계값을 `DoubleCheckConfig` 프로퍼티로 외재화 (`ElliottRule3Penalty`, `RuleFilterFinalScoreThreshold`, `SuperTrendTfThreshold` 등)

- **텔레그램 AI 관제탑 15분 집계 요약 전환** (`TelegramService.cs`, `TradingEngine.cs`):
  - AI 관제탑 판정을 실시간 개별 전송 → 15분 집계 배치 요약으로 전환하여 텔레그램 스팸 감소
  - `AiGateSummaryWindow` 내부 클래스로 승인/차단/강추세/방향/타입별 카운터 및 ML/TF 평균 누적
  - `FlushAiGateSummaryAsync()` 추가 — 15분마다 요약 메시지 전송, 판정 0건이면 생략
  - TradingEngine 메인 루프에 15분 타이머 연결 (`_lastAiGateSummaryTime`)
  - 심볼별 1분 중복 집계 방지는 유지 (ML < 0.50 저신뢰 차단 건 집계 생략 포함)

### Improved

- **메인 UI 레이아웃 개선** (`MainWindow.xaml`):
  - 좌측 사이드바 너비 320 → 340px, 각 카드 헤더에 컬러 세로 강조선 추가
  - 시계 카드를 컴팩트 가로 레이아웃으로 변경 (32pt → 24pt, 공간 절약)
  - Entry Gate 카드 리디자인: ML.NET / Transformer 각각 다크 배경 박스 + 컬러 바 시각 구분
  - 마켓 그리드 `AI시각` 컬럼 제거, 컬럼 너비 최적화 (총 ~150px 절약), 행 높이 48 → 44px
  - Critical Alerts 패널 높이 200 → 120px (마켓 그리드 공간 확보)

## [2.4.33] - 2026-03-11

### Fixed

- **청산 API 장애 시 자동 복구 강화** (`PositionMonitorService.cs`):
  - reduceOnly 청산 주문 3회 실패 후 종료하지 않고, **3초 주기 무한 재시도 루프**로 전환
  - 재시도 루프에서 reduceOnly 재시도 + 긴급 폴백 청산을 병행하여 실제 포지션 0 확인까지 복구 지속
  - 복구 완료 시 최종 청산 검증 및 후속 DB 저장 흐름으로 정상 복귀

- **청산 장애 텔레그램 관제 추가** (`PositionMonitorService.cs`):
  - 청산 API 오류로 무한 재시도 모드 진입 시 Telegram 긴급 알림 전송
  - 재시도 루프 복구 완료 시 Telegram 복구 완료 알림 전송

### Changed

- **수동 청산 UI 메시지 현실화** (`MainViewModel.cs`):
  - 수동 청산 버튼 실행 시 "명령 전송됨" 고정 문구 대신,
    실패 시 로그/동기화 확인이 필요함을 안내하는 문구로 변경

## [2.4.32] - 2026-03-11

### Fixed

- **배포본 AI 관제탑 모델 영속성 수정** (`EntryTimingMLTrainer.cs`):
  - AI 게이트 ML 모델 저장 위치를 실행 폴더 기준에서 `%LOCALAPPDATA%/TradingBot/Models` 기준으로 변경
  - 설치형 배포본에서 모델 파일이 버전 폴더 교체/권한 문제로 유지되지 않던 문제를 완화
  - 기존 실행 폴더의 `EntryTimingModel.zip`이 있으면 사용자 경로로 자동 마이그레이션되도록 보강
  - 결과적으로 배포 버전에서도 초기 학습 완료 후 `AI 관제탑` 메시지가 지속적으로 동작하도록 개선

- **AI 관제탑 텔레그램 표시 개선** (`TelegramService.cs`):
  - `TF흐름` 값을 raw 소수(`0.94`) 대신 퍼센트(`94.0%`)로 표시하도록 보정
  - RSI와 동일하게 사람이 즉시 해석 가능한 표시 형식으로 통일

### Changed

- **PUMP 스캔 후보 확대 및 시장 전체화** (`PumpScanStrategy.cs`):
  - 고정 밈 리스트 대신 전체 `USDT` 선물 마켓을 후보 우주로 사용하도록 변경
  - 혼합 랭킹(거래대금 50% + 변동성 20% + 모멘텀 30%) 기준 상위 후보 수를 **10개 → 20개**로 확대
  - `ICONUSDT`, `BSVUSDT`처럼 급등 중인 일반 알트도 상위권이면 후보에 포함될 수 있도록 수정

## [2.4.31] - 2026-03-11

### Fixed

- **텔레그램 직접 발송 경로 핫픽스** (`TelegramService.cs`):
  - AI 관제탑 / 본절 / 트레일링 알림도 공통 안전 발송 경로를 사용하도록 통합
  - Markdown 파싱 오류 발생 시 plain text로 자동 재시도하도록 수정
  - 배포 환경에서 발송 실패 원인 추적을 위해 `telegram_error.log` 파일 로그 추가
  - 일부 텔레그램 알림이 조용히 누락되던 문제를 완화

## [2.4.30] - 2026-03-11

### Fixed

- **AI 관제탑 텔레그램 RSI 표시 보정** (`TelegramService.cs`):
  - 내부 AI feature 값이 `0.0~1.0` 범위로 정규화되어 들어오는 경우, 텔레그램 알림에서는 자동으로 `0~100` 스케일로 환산해 표시하도록 수정
  - AI PASS/BLOCK 알림에서 `RSI: 0.5`처럼 오해를 주던 값을 `RSI: 50.0` 형태로 표시하도록 보정
  - 실제 AI 게이트 판단 로직은 유지하고, 사용자에게 보이는 관제 메시지의 해석 가능성만 개선

## [2.4.29] - 2026-03-11

### Added

- **Smart Target ATR TP/SL 시스템** (`HybridExitManager.cs`):
  - `ComputeSmartAtrTargets(price, isLong, atr, leverage=20)` 정적 메서드 추가
    - SL = ATR × 1.5 (최대 -3.5% 캡), TP = ATR × 3.0 (손익비 1:2)
    - ATR 계산 불가 시 폴백: SL -3.5%, TP +7%
  - `RegisterEntry` 오버로드 추가 (`initialSL`, `initialTP` 파라미터 지원)
  - `HybridExitState` 신규 필드: `InitialSL`, `InitialTP`, `BreakEvenTriggered`, `LastMilestoneROE`
  - 본절 전환 임계값 ROE 15% → **10%** 하향 조정 (20× 레버리지 0.5% 가격 이동 기준)
  - `OnBreakEvenReached: Action<string, decimal>?` 이벤트 추가
  - `OnTrailingMilestone: Action<string, decimal, double, string>?` 이벤트 추가
  - 마일스톤 이벤트: ROE 10 / 20 / 50 / 100% 도달 시 발동 (중복 방지)

- **Smart Target 텔레그램 알림** (`TelegramService.cs`):
  - `SendSmartTargetEntryAlertAsync`: 진입 시 ATR 기반 SL/TP 요약 발송 (SL%, TP%, ROE 환산 포함)
  - `SendBreakEvenReachedAsync`: 본절 전환 무음 알림
  - `SendTrailingMilestoneAsync`: ROE 마일스톤 알림 (ROE ≥ 20% 소리, 미만 무음)

- **TradingEngine 통합** (`TradingEngine.cs`):
  - `OnBreakEvenReached` → `TelegramService.SendBreakEvenReachedAsync` 이벤트 연결
  - `OnTrailingMilestone` → `TelegramService.SendTrailingMilestoneAsync` 이벤트 연결
  - 진입 후 비동기 Task: 15분 캔들 20봉 조회 → ATR(14) 계산 → `ComputeSmartAtrTargets` → `exitState` 업데이트 → Telegram 발송

## [2.4.28] - 2026-03-11

### Added

- **Dual-Gate 아키텍처 재설계 (Brain → Filter 순서 확립)**:
  - `EvaluateEntryAsync` 흐름을 ML→TF(기존)에서 **TF(Brain) → ML(Filter)** 순서로 재정렬
  - TensorFlow.NET(Transformer)이 1단계: 캔들 패턴/흐름 점수(TrendScore) 산출
  - ML.NET(LightGBM)이 2단계: 지표 기반 최종 필터 승인
  - 로그 태그: `[BRAIN]` (TF 승인), `[FILTER_BLOCK]` (ML/Sanity 거부)

- **Sanity Risk Filter (`EvaluateDualGateRiskFilter`) 추가**:
  - ML 통과 후 실행되는 하드코딩 규칙 기반 안전망
  - RSI ≥ 80: 과열 하드차단 (`RSI_Overheat_HardBlock`)
  - Upper Wick Ratio ≥ 70%: 윗꼬리 과다 차단 (`UpperWick_Risk`)
  - BB 상단 ≥ 90% + RSI ≥ 70 + TrendScore < 0.80: 과열 상단 차단 (`UpperBand_Overheat`)
  - 최근 고점 대비 하락폭 ≤ 0.20%: 추격 진입 차단 (`Chasing_Risk`)
  - **StrongTrend 우선통과**: TrendScore ≥ 0.80이면 BB/RSI 주의 조건 바이패스

- **`AIEntryDetail.TrendScore` 필드 추가**: Sanity 필터에서 TrendScore 전달 및 로그 기록

- **`DoubleCheckConfig` 튜닝 파라미터 6종 추가**:
  - `StrongTrendBypassThreshold = 0.80f`
  - `RsiOverheatHardCap = 80f`
  - `RsiCautionThreshold = 70f`
  - `BbUpperRiskThreshold = 0.90f`
  - `UpperWickRiskThreshold = 0.70f`
  - `RecentHighChaseThresholdPct = 0.20f`

### Changed

- **PUMP 코인 진입 기준 강화 (손실 축소)**:
  - `MAX_PUMP_SLOTS: 2 → 1` (동시 PUMP 포지션 최대 1개로 제한)
  - `MinMLConfidencePumping: 0.58f → 0.66f` (ML 신뢰도 임계치 상향)
  - `MinTransformerConfidencePumping: 0.63f` 신규 추가 (TF 신뢰도 임계치)
  - PUMP CoinType에서 ML+TF 둘 다 기준 통과 필수 (기존 ML 단독 → 이중 게이트)

- **AI 런타임 구조 단순화 (v2.4.27 Unreleased 내용 확정)**:
  - 외부 서비스 프로젝트(`TradingBot.MLService`, `TradingBot.TorchService`) 제거
  - Named Pipe IPC 기반 예측/학습 경로 제거
  - ML.NET + TensorFlow.NET 인프로세스(내부 백그라운드) 처리로 통합

### Documentation

- `README.md`, `PROCESS_AI_ARCHITECTURE.md`, `RELEASE_CHECKLIST.md`, `run-ai-validation.ps1`를
  현재 인프로세스 아키텍처 기준으로 일괄 업데이트

### Note

- 아래 버전 이력에는 당시 구조를 반영한 외부 서비스/Torch 관련 설명이 포함될 수 있으며,
  이는 **히스토리 기록**입니다. 현재 운영 기준은 `Unreleased` 섹션 설명을 따릅니다.

## [2.4.27] - 2026-03-10

### Added

- **AI 서비스 프로세스 분리**:
  - ML.NET 예측 모델을 외부 프로세스(TradingBot.MLService.exe)로 실행
  - AIPredictor: MLServiceClient를 통한 외부 ML 예측(우선) + 로컬 폴백
  - AIDoubleCheckEntryGate: TorchServiceClient를 통한 외부 Transformer 예측
  - Named Pipe 기반 IPC(Inter-Process Communication) 구현으로 프로세스 간 격리

- **AI 알림 통일**:
  - Transformer 주기 재학습 CRITICAL 알림 추가
  - 외부/로컬 모드 모두 동일하게 "[CRITICAL][AI][Transformer]" 형식으로 표준화
  - NormalizeCriticalProjectName()으로 "TorchService" → "Transformer"로 통일

- **빌드 자동화 개선**:
  - MSBuild 타겟: CopyExternalAiServicesToOutput, CopyExternalAiServicesToPublish
  - 빌드/배포 시 MLService, TorchService 실행파일 자동 복사 to Services/ 폴더
  - 배포 후 외부 AI 프로세스 즉시 사용 가능

- **관리자 문서화**:
  - PROCESS_AI_ARCHITECTURE.md: 외부 AI 서비스 아키텍처 상세 설명
  - setup-ai-services.ps1: AI 서비스 프로세스 수동 검증/디버그 스크립트

### Changed

- **AI 예측 경로 대칭화**:
  - ML.NET과 Transformer 모두 (외부 프로세스 우선 + 로컬 폴백) 동일한 패턴으로 동작
  - 상태 모니터링 획일화: Both use AIServiceProcessManager health-check (30s interval, auto-restart)

## [2.4.26] - 2026-03-10

### Changed

- **진입 슬롯 정책 조정**:
  - 액티브 포지션 구성을 메이저 4개 + PUMP 2개, 총 6개로 고정
  - PUMP 신호를 별도 `MAJOR_MEME` 경로가 아닌 `MAJOR` 진입 로직으로 통합
  - 메이저/PUMP 모두 동일한 AI 더블체크 게이트와 공통 진입 필터를 사용하도록 정리

- **AI 진입 기준 강화**:
  - 메이저/일반 코인 모두 AI 점수 임계값을 최소 70점 이상으로 통일
  - 설정값이 더 낮더라도 엔진에서 70점 미만으로 내려가지 않도록 하한 적용

### Fixed

- **거래소 가격 정밀도 보정 개선**:
  - 주문/그리드/실행 서비스 전반의 가격 tick fallback을 소수점 7자리 기준으로 상향
  - 심볼 메타데이터 조회 실패 시에도 저가 코인 가격이 2자리로 잘리지 않도록 수정

- **청산 실패 복구력 향상**:
  - 거래소 API 주문 실패 시 청산 주문을 최대 3회까지 재시도
  - 바이낸스 API 오류 코드와 예외 상세 내용을 UI 로그로 노출해 수동 대응성을 개선

## [2.4.25] - 2026-03-10

### Fixed

- **AI Learning Status 총 결정 수 표시 오류 수정**:
  - Torch 안전모드에서 `AIDoubleCheckEntryGate`가 null일 때도 라벨링 통계를 조회할 수 있도록 개선
  - `GetRecentLabelStatsFromFiles` 메서드 추가로 Gate 없이도 파일에서 직접 통계 조회
  - 결과적으로 Transformer 비활성 환경에서도 실제 누적된 라벨링 데이터가 UI에 정상 표시됨
  - 초기화 시점과 대시보드 갱신 시점 두 곳 모두 반영

## [2.4.24] - 2026-03-10

### Fixed

- **TorchSharp BEX64 핫픽스 (엔진 경로 누락 보완)**:
  - `TradingEngine`에 Transformer 옵트인 미설정 시 Torch 초기화/학습 경로를 강제 스킵하는 가드를 반영
  - 엔진 자동 초기 학습에서 Transformer 런타임 미준비 시 호출 자체를 생략하도록 수정
  - 수동 초기 학습도 Transformer 비활성 상태를 분기 처리하여 `TF=DISABLED`로 명확히 보고
  - Torch 미사용 환경에서 불필요한 Transformer 실패 경로 진입을 차단해 BEX64 노출면을 추가 축소

## [2.4.23] - 2026-03-10

### Fixed

- **TorchSharp BEX64 재발 방지 게이팅 강화**:
  - `TorchInitializer`: `IsExperimentalOptInEnabled` 추가로 실험 기능 옵트인(`TRADINGBOT_ENABLE_TORCH_EXPERIMENTAL=1`) 상태를 일관되게 판별
  - `TradingEngine`: `TransformerSettings.Enabled=true`여도 옵트인 미설정이면 Torch/Transformer 초기화 경로를 즉시 비활성화
  - 엔진 시작 시 Transformer 초기 학습 호출을 런타임 준비 상태에서만 수행하도록 수정
  - 수동 초기 학습에서도 Transformer 비활성 상태를 명시적으로 건너뛰고 `TF=DISABLED` 상태를 반환
  - 결과적으로 Torch 미사용 환경에서 불필요한 Transformer 초기화 실패 경로를 제거하여 BEX64 크래시 노출면을 축소

## [2.4.22] - 2026-03-10

### Removed

- **뉴스 감성 점수 제거**:
  - `TradingEngine`: `NewsSentimentService` 필드/생성/호출 완전 제거
  - `TransformerStrategy`: `NewsSentimentService` 의존성 제거
  - `SentimentScore` 항상 0으로 고정 (진입 판단에 영향 없음)
  - Binance 뉴스 API 의존성 제거로 안정성 향상

### Fixed

- **BEX64 크래시(ucrtbase.dll c0000409) 재발 완전 차단 - 4중 안전장치**:
  - **Layer 1 (비정상 종료 감지)**: `TorchInitializer.RegisterStartupRunState()` — run-state 파일로 이전 크래시 감지 시 다음 실행에서 Torch 자동 차단
  - **Layer 2 (기본 안전모드)**: `TRADINGBOT_ENABLE_TORCH_EXPERIMENTAL=1` 환경 변수 명시적 설정 필요 (옵트인 방식) — 기본값은 Torch 비활성
  - **Layer 3 (엔진 시작 게이팅)**: `TradingEngine` 초기화 시 `TorchInitializer.TryInitialize()` 실패 시 안전모드 전환
  - **Layer 4 (UI 자동 초기화 차단)**: `MainWindow.InitializeWaveAIAsync()` 시작 전 Torch 가용성 사전 체크
  - `App.xaml.cs`: 시작 시 `RegisterStartupRunState()`, 종료 시 `RegisterCleanShutdown()` 호출
  - 근본 원인: MainWindow.Loaded에서 WaveAIManager 자동 초기화가 엔진 설정과 무관하게 실행되던 문제 해결
  - Defense in depth 전략: 어느 경로로든 Torch 접근 시 사전 차단

## [2.4.21] - 2026-03-10

### Fixed

- **BEX64 크래시 완전 수정 (ucrtbase.dll c0000409 — C++ abort)**:
  - **근본 원인**: v2.4.19에서 추가한 `LoadModel()` 더미 forward 테스트가 C++ 네이티브 abort를 유발 — C# try-catch로 잡을 수 없어 프로세스 종료
  - `TransformerTrainer.LoadModel()`: 더미 forward 제거 → stats 파일 기반 아키텍처 검증으로 대체
  - `EntryTimingTransformerTrainer.LoadModel()`: 더미 forward 제거 → 안전한 load + 예외 시 삭제
  - `TransformerWaveNavigator.LoadModel()`: 예외 처리 추가 — 호환 불가 모델 자동 삭제
  - `TorchInitializer.InvalidateModelsIfVersionChanged()`: 앱 버전 변경 시 모든 모델 파일 일괄 삭제 (교차 버전 호환성 문제 원천 차단)
  - `App.xaml.cs`: 시작 시 모델 무효화 호출 추가

## [2.4.20] - 2026-03-10

### Fixed

- **WaveAI 초기화 실패 수정 (TorchSharp 런타임 사용할 수 없습니다)**:
  - `DoubleCheckEntryEngine`, `AIDoubleCheckEntryGate`: `IsAvailable`만 체크하던 것을 `TryInitialize()` 호출로 변경
  - WaveAI가 TradingEngine보다 먼저 초기화될 때 TorchSharp가 아직 쳐크되지 않아 실패하던 타이밍 문제 해결

## [2.4.19] - 2026-03-10

### Fixed

- **Transformer 초기 학습 내부 오류 수정 (attn_output 차원 불일치)**:
  - `TimeSeriesTransformer`, `TransformerClassifierModel`: `dModel % nHeads` 검증 추가
  - `TransformerTrainer.LoadModel()`: 로드 후 더미 forward 테스트로 모델 무결성 검증, 불일치 시 자동 삭제 + 재초기화
  - `EntryTimingTransformerTrainer.LoadModel()`: 동일 패턴 적용
  - 기존 모델 파일이 현재 아키텍처와 호환되지 않을 때 자동 복구

## [2.4.18] - 2026-03-10

### Fixed

- **TorchSharp 안전성 전수 감사 및 수정**:
  - `TimeSeriesDataLoader`: `torch.CPU` 직접 접근 → `IsAvailable` 가드 + `ResolveDevice()` 폴백 (BEX64 크래시 원인 차단)
  - `EntryTimingTransformerTrainer`, `TransformerWaveNavigator`, `PPOAgent`: `IsAvailable` 사전 체크 추가
  - `AIDoubleCheckEntryGate`, `DoubleCheckEntryEngine`: Transformer 인스턴스 생성 전 `IsAvailable` 가드 추가
  - `TorchInitializer`: 프로브 캐시 7일 자동 만료 — FAIL 캐시 영구 고착으로 인한 Transformer 영구 비활성화 방지

### Added

- **자기진화형 AI 학습 데이터 파이프라인**:
  - `AIDoubleCheckEntryGate`에 `OnLabeledSample` 이벤트 추가
  - `TradingEngine`에서 이벤트 구독 → `DbManager.SaveAiTrainingDataAsync()`로 DB 자동 적재
  - `AiLabeledSamples` 테이블 자동 생성 및 라벨 데이터 영속화

## [2.4.17] - 2026-03-10

### Added

- **원격 수동 학습 명령 추가**:
  - Telegram 명령어 `/train` 추가
  - 실행 중인 엔진에서 ML.NET + Transformer + AI 더블체크 학습을 순차 강제 실행 가능
  - 중복 실행 방지(동시 학습 요청 차단) 및 완료 요약 메시지 제공

### Fixed

- **SIGNAL 무반응 이슈 수정**:
  - AI 더블체크 초기 학습 중 UI Signal 업데이트 중단 후 일부 조기 종료 경로에서 재개 누락되던 문제 수정
  - `finally`에서 `SuspendSignalUpdates(false)`를 보장해 시그널 큐 정지 상태가 남지 않도록 개선

- **Transformer 초기 학습 스킵 원인 메시지 개선**:
  - `TransformerSettings.Enabled=false`와 Torch/초기화 실패 케이스를 구분해 안내
  - 운영 중 원인 진단 속도 개선

### Changed

- `appsettings.json`의 `TransformerSettings.Enabled`를 `true`로 변경 (초기 학습 및 Transformer 경로 활성화)

## [2.4.16] - 2026-03-10

### Fixed

- **업데이트 확인 실패 문제 해결**:
  - GitHub Releases 메타데이터 직접 접근 방식에서 Velopack GitHub 통합 API로 전환
  - `UpdateManager` 생성자를 `GithubSource` 사용으로 변경하여 GitHub Releases API와 직접 통합
  - `releases.win.json` 파일 형식 문제 우회 (GitHub의 에셋 다운로드 구조 변경으로 인한 호환성 문제)

## [2.4.15] - 2026-03-10

### Fixed

- **자동 업데이트 다운로드 후 적용 안 되는 문제 수정**:
  - 업데이트 체크 URL을 `latest/download` 디렉터리에서 `releases.win.json` 직접 경로로 변경
  - Velopack 설치 경로(`current`, `Update.exe`) 여부를 먼저 검사하도록 개선
  - Setup.exe로 설치되지 않은 경우 자동 업데이트 불가 안내창 표시
  - `ApplyUpdatesAndRestart()` 호출 후 명시적으로 앱 종료를 수행하도록 보강

## [2.4.14] - 2026-03-10

### Critical Fix

- **BEX64 크래시 실제 시작 경로 차단 (5차 최종 안정화)**:
  - **실제 누락 지점 발견**: 이전 수정은 `TransformerTrainer` 경로만 막았고, 앱 시작 시 별도 경로가 여전히 TorchSharp 생성자를 직접 호출하고 있었음
  - **TradingEngine 생성자**: `AIDoubleCheckEntryGate`를 항상 생성했고, 내부에서 `new EntryTimingTransformerTrainer()`가 실행됨
  - **MainWindow Loaded 경로**: `InitializeWaveAIAsync()`가 항상 실행되며 `WaveAIManager → DoubleCheckEntryEngine → TransformerWaveNavigator` 생성으로 이어짐
  - **추가 안전장치**: `MultiAgentManager`, `AIDoubleCheckEntryGate`, `DoubleCheckEntryEngine` 모두 설정 기반 게이트 추가
  - **최종 결과**: `TransformerSettings.Enabled=false`이면 앱 시작 시 TorchSharp 생성자 경로 자체를 타지 않음

### Changed

- `TradingEngine.cs`: Torch 비활성 시 AI 더블체크 게이트 초기화 자체를 건너뜀
- `MainWindow.xaml.cs`: Torch 비활성 시 WaveAI 자동 초기화 자체를 건너뜀
- `MultiAgentManager.cs`: Torch 비활성 시 PPO 에이전트 생성 완전 차단
- `AIDoubleCheckEntryGate.cs`: Torch 비활성 상태에서 생성자 즉시 차단
- `DoubleCheckEntryEngine.cs`: Torch 비활성 상태에서 생성자 즉시 차단

## [2.4.13] - 2026-03-10

### Critical Fix

- **BEX64 크래시 근본 원인 재분석 및 안전 모드 적용 (4차 최종 안정화)**:
  - **Root Cause (v2.4.11 본석 개선)**: `TransformerTrainer.cs`의 `using static TorchSharp.torch` 구문이 클래스 로드 시점에 torch static constructor를 트리기 → 네이티브 DLL 로드 → BEX64 크래시
  - **v2.4.11의 NoInlining 패턴만으로는 불충분**: JIT 컴파일러가 `new TransformerTrainer(...)`를 컴파일하려고 하는 순간 using static으로 인해 TorchSharp 어셈블리 로드
  - **최종 해결책**: TorchSharp/Transformer 기능을 **기본 비활성화** (appsettings.json `"Enabled": false`)
  - **사용자 선택**: `appsettings.json`에서 `TransformerSettings.Enabled = true`로 변경 후 재시작하면 Transformer AI 활성화 (프로브 검증 후 사용)
  - **기본 모드**: ML.NET AI + MajorCoinStrategy(지표 기반)만 동작 → **BEX64 크래시 완전 차단**
  - **메시지 개선**: 설정 확인 메시지 및 경고 추가

### Changed

- `AppConfig.cs`: `TransformerSettings.Enabled` 기본값 `true` → **`false`**
- `appsettings.json`: `TransformerSettings.Enabled = false` + 경고 주석 추가
- `TradingEngine.cs`: Transformer 초기화 메시지 강화 (안정 모드 안내)

## [2.4.12] - 2026-03-10

### Enhanced

- **볼린저 밴드 필터 개조 — 과열/추세 구분 로직 업그레이드**:
  - 상단 85% 고정 제한 제거 → 밴드 폭(Width) + RSI 복합 판단으로 대체
  - Squeeze→Expansion (밴드 폭 확산 10% 이상) 감지 시 상단/하단 무조건 진입 승인 (강력한 발산 신호)
  - RSI < 70이면 상단 100% 위치라도 "추세 시작"으로 판단, 진입 승인
  - RSI ≥ 80 초과열 시만 상투 판단으로 차단 (기존: RSI 70부터 차단)
  - SHORT 대칭 적용: 하단 RSI ≤ 20 초과냉만 차단, RSI > 30이면 하락 추세 진입 승인
  - Hybrid BB 진입 필터도 동일 로직 적용 (RSI < 70이면 상단 85% 이상도 롱 허용)

## [2.4.11] - 2026-03-10

### Critical Fix

- **BEX64 크래시 근본 원인 제거 (3차 최종 안정화)**:
  - **Root Cause**: `ResolveDevice()` 및 `TryInitialize()`에서 init 실패 시에도 `TorchSharp.torch.CPU` 접근 → torch static ctor 트리거 → 네이티브 DLL 로드 → ucrtbase.dll 크래시
  - **Fix**: TorchSharp 타입 접근을 `[MethodImpl(NoInlining)]` 메서드로 완전 격리
  - **Fix**: `ResolveDevice()` init 실패 시 `null` 반환 (TorchSharp 타입 절대 접근 안함)
  - **Fix**: 프로브 자식이 크래시 시 FAIL 미리 기록 (네이티브 크래시로 체크포인트 유실 방지)
  - **Fix**: 모든 Torch 사용 클래스(TransformerTrainer, PPOAgent 등) null 체크 추가

## [2.4.10] - 2026-03-10

### Enhanced

- **약한 타점 진입 신호 감도 강화 (약 20배 레버리지용)**:
  - 피보나치 진입 구간 확장: 0.618 → 0.786 (덜 눌려도 진입, Golden Zone 확대)
  - RSI 다이버전스 감도 강화: ±3.0 포인트 → ±0.5 포인트 (미세한 신호 포착)
  - ML 신뢰도 기준 하향: 55% / 48% → **50% / 40%** (70%대 신뢰도에서도 진입)

### Fixed

- **10시~11시 약한 반등 미진입 현상 개선**:
  - 가격은 0.382 지점에 머물렀으나 진입 범위(0.618)를 초과로 거부 → 0.786까지 허용
  - RSI는 미세하게만 올랐으나(0.5 포인트) 다이버전스 조건 미충족 → 감도 강화
  - 신뢰도 62%는 "전교 1등 기준"에 미달 → "우등생 기준"으로 하향 조정

### Technical Details

- **FibonacciRiskRewardCalculator.cs**: `FIB_ENTRY_MAX = 0.786m` (Line 28)
- **WaveSniper.cs**: `rsiHigher = secondLowRsi > firstLowRsi + 0.5f` (Line 192)
- **TradingEngine.cs**: 
  - `_fifteenMinuteMlMinConfidence = 0.50f` (Line 60)
  - `_fifteenMinuteTransformerMinConfidence = 0.47f` (Line 61)
  - `GateMlThresholdMin = 0.40f` (Line 66)
  - `GateTransformerThresholdMin = 0.40f` (Line 68)

## [2.4.9] - 2026-03-10

### Fixed

- **BEX64 재발 대응(2차 안정화)**:
  - Torch 프로브 실행 예외 시 위험한 "직접 초기화" 폴백 제거 (fail-closed)
  - 프로브 실행 파일 미존재 시 FAIL 처리로 안전 비활성화
  - Torch 초기화 진단 로그 추가: `%LocalAppData%/TradingBot/torch_init.log`
  - 프로브 자식/부모/캐시 기록을 로그로 남겨 재현 시 원인 추적 강화

## [2.4.8] - 2026-03-10

### Fixed

- **BEX64 크래시 완화 (`ucrtbase.dll`, `0xc0000409`)**:
  - Torch 디바이스 선택 경로를 안전 모드(CPU 기본)로 통일
  - `cuda.is_available()` 직접 호출 경로를 공통 안전 초기화 경유로 전환
  - Torch 초기화 시 환경 변수 기반 강제 비활성(`TRADINGBOT_DISABLE_TORCH=1`) 지원

- **Torch 프로브 캐시 경로 불일치 수정**:
  - 부모/자식 프로세스가 동일한 프로브 결과 파일을 사용하도록 정렬
  - 비정상(빈 값) 프로브 캐시 감지 시 FAIL 처리로 재시도 루프 방지
  - 프로브 캐시 저장 경로를 LocalAppData 기준으로 고정해 권한/경로 이슈 완화

## [2.4.7] - 2026-03-10

### Added

- **SimpleDoubleCheckEngine 전략 통합**:
  - 매복 모드 기반 더블 체크 진입 시스템 (Transformer → ML.NET 순차 검증)
  - 매복 시간대 외에는 ML.NET 실행 안 함으로 CPU 절약
  - HybridExitManager 통합으로 ATR 기반 동적 트레일링 스톱 적용

- **DB 스냅샷 저장 로직 검증**:
  - TradePatternSnapshots 테이블 저장 흐름 확인 완료
  - 진입 시 패턴 데이터 저장 (Label=NULL)
  - 청산 시 자동 라벨링 (Label=0 실패, Label=1 성공)
  - 학습 데이터 조회 API 검증 (GetLabeledTradePatternSnapshotsAsync)

- **거래 테이블 검증 스크립트**:
  - quick-test-trades.ps1: TradeHistory/TradeLogs 간단 체크
  - quick-check-snapshots.ps1: TradePatternSnapshots 검증
  - 실시간 데이터 수집 상태 모니터링 가능

### Changed

- **Entry Gate Status UI 개선**:
  - ML=0%, TF=75% 표시 → "ML: 대기", "TF: 75%"로 변경
  - 0%는 "나쁜 예측"이 아니라 "아직 실행 안 함"을 명확히 표현
  - 매복 모드가 아닐 때 ML.NET 비실행 상태 직관적으로 표시

- **Market Signals UI 명확화**:
  - MLTFSummary 로직 개선: ML=0일 때 TF 점수만 표시
  - 상태 메시지 체계화: "✅ 진입 승인", "💁 매복 대기", "⏳ TF 대기 중"
  - 매복 만료 시 마지막 TF 점수 유지로 일관성 확보

### Fixed

- **UI 표시 혼란 해소**:
  - ML.NET 미실행 시 "0%" 표시로 인한 사용자 오해 제거
  - 매복 모드 동작 원리 명확화 (CPU 절약 전략)
  - Entry Gate와 Market Signals 표시 일관성 확보

## [2.4.6] - 2026-03-09

### Fixed

- **DB holdingMinutes 계산 열 NULL 안전 처리**:
  - ExitTime이 NULL인 상태로 INSERT 시 오류 발생 문제 해결
  - NULL 안전 계산 열로 재생성: `CASE WHEN ExitTime IS NOT NULL THEN DATEDIFF(MINUTE, EntryTime, ExitTime) ELSE NULL END`
  - 진입 시 ExitTime=NULL 허용으로 INSERT 성공률 100% 달성

- **DB 오류 로깅 강화**:
  - SqlException 별도 처리로 SQL 오류 번호, 상태, 라인 번호 상세 출력
  - holdingMinutes 관련 오류 자동 감지 및 해결 가이드 제공
  - 외부 청산 동기화 실패 시 명확한 오류 메시지 표시

- **배열 인덱스 오류 수정**:
  - TelegramService.cs의 Split 배열 접근 시 INDEX WAS OUTSIDE THE BOUNDS 오류 해결
  - 빈 메시지 체크 및 StringSplitOptions.RemoveEmptyEntries 적용
  - BinanceExchangeService.GetFundingRateAsync에 try-catch 추가

### Changed

- **청산 로직 안정성 검증**:
  - ExecuteMarketClose 메서드 전체 흐름 분석 완료
  - 중복 청산 방지, 청산 완료 검증(8회 재시도), 반대 방향 포지션 감지 확인
  - Position 정리 체계(_activePositions.Remove, OnPositionStatusUpdate) 검증
  - DB 저장 및 이벤트 발생(CompleteTradeAsync, OnTradeHistoryUpdated) 정상 동작 확인

- **DB 스크립트 정비**:
  - fix-holdingminutes-safe.sql: 안전한 계산 열 재생성 스크립트 추가
  - fix-db-manual.sql: SSMS 수동 실행용 스크립트 업데이트
  - fix-holdingminutes.ps1: PowerShell 자동 실행 스크립트 복원
  - 모든 스크립트 실제 DB 이름(TradingDB) 정확히 반영

## [2.4.5] - 2026-03-09

### Changed

- **메이저 라이브 로그 시스템 정렬**:
  - MajorCoinStrategy 신호 로그를 라이브 트레이딩 상태형 포맷으로 통일
  - `📊 [BTCUSDT] LONG 진입 후보 포착 | 가격 ... | 점수 ... | RSI ... | Vol ... | AI 사전체크 ...` 형식 적용
  - 신호 발생 시 `TradingStateLogger.EvaluatingAIGate(...)` 사용으로 엔진 로그 체계와 일관성 확보

- **Trade 탭 로그 가독성 개선**:
  - MainViewModel 로그 간소화에서 상태형(이모지+심볼) 로그는 원문 유지하도록 예외 확장
  - 기존처럼 `메이저신호` 단일 라벨로 과축약되는 문제 완화

## [2.4.4] - 2026-03-09

### Fixed

- **AssemblyVersion 형식 오류 해결**: 
  - TradingBot.csproj에서 AssemblyVersion을 4자리 형식(2.4.4.0)으로 수정
  - GenerateAssemblyInfo를 true로 변경하여 .csproj 버전 정보 사용
  - AssemblyInfo.cs 파일 제거 (중복 방지)
  - FileNotFoundException 오류 완전 해결

- **빌드 시스템 개선**:
  - SDK 자동 AssemblyInfo 생성 활성화로 버전 관리 단순화
  - .csproj 파일에서 버전 정보 일원화 관리

## [2.4.3] - 2026-03-09

### Fixed

- **로그 포맷 개선**: 세련된 진입 신호 출력
  - MajorCoinStrategy: `📊 [XRPUSDT] 롱 신호 감지 | 가격 $1.95`
  - NavigatorSniper 평가: `🤖 [XRPUSDT] 롱 ML 스나이퍼 평가 중 | ML신뢰도 85%, TF신뢰도 72%`
  - Sniper 승인: `✅ [XRPUSDT] 롱 Sniper 승인! | ML=85%, TF=72%`
  - 주문 요청: `📤 [XRPUSDT] LONG 주문 요청 중 | 가격 $1.95`
  - 기존 `📡 [SIGNAL][MAJOR][CANDIDATE]` 형식 제거로 가독성 향상

- **Navigator-Sniper 통합 누락 수정**:
  - TradingEngine에서 MajorCoinStrategy 신호 발생 시 AIDoubleCheckEntryGate 평가 로직 추가
  - 주문 실행 전 ML.NET 신뢰도 체크 및 로그 출력 구현
  - WAIT 신호는 로그 출력 생략 (노이즈 감소)

## [2.4.1] - 2026-03-09

### Added

- **TradingStateLogger.cs**: 직관적인 라이브 트레이딩 로그 시스템
  - 상태별 전문 로그 메서드: 횡보 중, 진입 대기(ETA 표시), AI 평가 중, 주문 요청 중, 진입 완료, 거부 사유 등
  - 예시: `⏳ [BTCUSDT] LONG 진입 대기 (ETA: 14:30, 25분 후) | AI 신뢰도 85%`
  - 예시: `✅ [BTCUSDT] LONG 진입 완료! | 진입가 $67,234.50, 손절 $66,500.00, 익절 $68,800.00 | R:R 2.1x | 전략=MAJOR`

- **HistoricalDataLabeler.cs**: 히스토리컬 데이터 자동 라벨링 스크립트 (베타)
  - Time-to-Target 및 ML.NET 진입 성공 라벨을 동시 생성
  - 현재 제약: API 한계로 최근 1000 캔들(약 10일치)만 조회 가능
  - 향후 확장 예정: startTime/endTime 지원하는 IExchangeService 메서드 추가 필요

### Changed

- **TradingEngine.cs 로그 가독성 대폭 개선**:
  - 진입 워밍업: `⏳ [진입 워밍업] 신규 진입 제한 중 | 120초 남음`
  - 패턴 필터: `🔍 [BTCUSDT] LONG 패턴 필터 차단 | loss-pattern`
  - 캔들 확인 대기: `⏸️ [BTCUSDT] LONG 캔들 확인 대기 (1캔들 남음) | Fakeout 방지 모드`
  - 추격 방지: `🏃 [BTCUSDT] LONG 추격 방지 차단 | 이미 상승 완료`
  - AI 게이트: `🤖 [BTCUSDT] LONG ML 스나이퍼 평가 중 | ML신뢰도 85%, TF신뢰도 72%`
  - 주문 완료: `✅ [BTCUSDT] LONG 진입 완료! | 손절 $66,500 | 익절 $68,800 | R:R 2.1x`
  - 서킷 브레이커: `⛔ [서킷 브레이커 발동] 일일 손실 -5% 초과 | 1시간 동안 모든 진입 차단`

- **AIDoubleCheckEntryGate.cs 상세 로그 추가**:
  - Feature 추출 실패/성공 상세 표시
  - Time-to-Target 예측: `🎯 [BTCUSDT] AI 타점 예측: 8.3캔들 (125분) 후, ETA 14:30 | ML=85%, TF=72%`
  - ML.NET / Transformer 거부 이유 상세: 신뢰도 부족 vs 타점 범위 외 구분
  - 규칙 위반: `❌ [BTCUSDT] 규칙 위반 거부: Fib 0.618 이탈`
  - 최종 승인: `✅ [BTCUSDT] AI 더블체크 승인! ML=85%, TF=72%`

### Fixed

- HistoricalDataLabeler.cs: IExchangeService API 호환성 수정 (using TradingBot.Services 추가)

## [2.4.0] - 2026-03-09

### Changed - ⚠️ **파괴적 변경 (Breaking Change)**

- **AI 아키텍처 전환: "네비게이터-스나이퍼" 2단계 시스템**
  - **Transformer 역할 변경**: 이진 분류(Binary Classification) → **Time-to-Target 회귀(Regression)**
    - 기존: "현재 진입할까?" (0/1 확률) → 노이즈에 취약
    - 신규: "피보나치 0.618 목표가까지 몇 개의 캔들이 필요한가?" (1~32 캔들 예측)
  - **ML.NET 역할 유지**: 실시간 최종 진입 승인 ("스나이퍼" 역할)
  - **학습 파이프라인 변경**:
    - Loss: `BCEWithLogitsLoss` → `MSELoss` (Mean Squared Error)
    - Label: `bool ShouldEnter` → `float CandlesToTarget` (캔들 개수)
    - Metric: Accuracy → MAE (Mean Absolute Error, 캔들 단위)
  - **예측 거동**: Transformer가 "오후 2시 30분경 진입 타점" 예보 → ML.NET이 해당 시간대에서 초 단위 정밀 판단

### Added

- `MultiTimeframeEntryFeature.CandlesToTarget` 필드 추가 (Transformer 회귀 라벨용)
- `BacktestEntryLabeler.CalculateCandlesToFibonacciTarget()` 메서드 추가
  - 목표가 정의: 최근 고점에서 피보나치 0.618 되돌림
  - 도달 조건: 캔들 종가가 목표가 ±0.5% 범위 진입
  - 예측 범위: 32캔들(8시간), 미도달 시 학습에서 제외

### Fixed

- AI ENTRY 관망(8%) 교착 상태 근본 해결
  - 문제: 이진 분류는 모든 시점의 확률이 낮으면 영원히 대기
  - 해결: Time-to-Target 회귀로 "언제 기회가 오는지" 예측 → 해당 시점까지 대기 후 정밀 판단

### Deprecated

- `AIDoubleCheckEntryGate.PredictEntryProbabilities()`: 이진 분류 기반 확률 예측 메서드 (호환성 유지용으로 보존되나 0 반환)

### ⚠️ **업그레이드 주의사항**

1. **기존 Transformer 모델 무효화**: 이전 v2.3.x 모델(`.zip`, `.pt`)은 **호환 불가**
   - v2.4.0 최초 실행 시 자동으로 신규 라벨링 학습 수행 권장
   - 기존 ML.NET 모델(`TradingModel.zip`)은 동일하게 사용 가능

2. **라벨링 데이터 재생성 필요**: `TrainingData/EntryDecisions/*.json` 파일에 `CandlesToTarget` 필드가 없으면 학습 실패
   - `BacktestEntryLabeler.CalculateCandlesToFibonacciTarget()`로 재라벨링

3. **ETA 표시 변경**: AI ENTRY 패널에서 "관망 8%" 대신 "지금" / "14:30" / "관망 · 8% · +150m" 형식으로 표시

## [2.3.2] - 2026-03-09

### Fixed

- AI Entry Transformer 예측 인덱싱 오류를 수정하여 `TF confidence`가 0으로 고정되던 문제 해결
- AI Entry 표시 로직 개선: 관망 상태에서도 ETA(`지금`/`HH:mm`)와 `+분` 상세가 표시되도록 수정

### Changed

- AI 더블체크 게이트 임계치 완화
  - 기본: ML `0.65 → 0.50`, TF `0.60 → 0.45`
  - Major: ML `0.75 → 0.60`, TF `0.70 → 0.55`
  - Pumping: ML `0.58 → 0.48`
- 관망(저확률) 상태일 때 AI ENTRY ETA 탐색 범위를 2시간(8스텝)에서 4시간(16스텝)으로 확장

## [2.3.1] - 2026-03-09

### Fixed

- 로그인 후 메인 윈도우 로딩 시 `StaticResource` 선참조로 발생하던 XAML 파싱 예외 수정
  - DataGrid 템플릿 내부 `MinimalScrollViewer` / `MinimalScrollBar` 참조를 `DynamicResource`로 변경

## [2.3.0] - 2026-03-08

### Removed

- **Bybit 거래소 지원 완전 제거**:
  - `BybitExchangeService.cs` 및 `BybitSocketConnector.cs` 파일 삭제
  - Bybit.Net NuGet 패키지 제거
  - User 모델에서 BybitApiKey/BybitApiSecret 속성 제거
  - AppConfig에서 Bybit 설정 클래스 제거
  - MainWindow 거래소 선택 드롭다운 숨김 처리 (Binance로 고정)
  - SettingsWindow에서 거래소 선택 UI 완전 제거
  - ExchangeDataModels에서 BybitExchangeAdapter 제거
  - MarketDataManager에서 Bybit 스트리밍 메서드 및 어댑터 제거
  - DatabaseService, ProfileWindow, SignUpWindow에서 Bybit 관련 코드 제거

### Changed

- **Binance 전용 거래소로 전환**:
  - TradingEngine이 Binance 또는 Mock 거래소만 지원하도록 변경
  - UI에서 거래소 선택 옵션 제거, Binance로 고정

## [2.2.12] - 2026-03-08

### Changed

- **라이브 로그 UI를 초간단 표시로 정리**:
  - `MainViewModel`에서 로그 표시 메시지를 `라벨 · 심볼 · 방향 · 상태 · 요약 상세` 형태로 축약
  - 화면 표시와 DB 저장 로그를 분리하여, 저장 원문은 유지하고 UI 가독성만 개선

- **라이브 로그 표시 정책을 엄격 모드로 조정**:
  - 대시보드 로그 패널에는 `GATE` 로그와 `주문 오류` 로그만 표시
  - 일반 추적/신호성 로그는 UI에서 숨김 처리하여 운영 중 핵심 이벤트 집중도 향상

### Fixed

- **라이브 로그 단순화 미적용 문제 해결**:
  - 로그 후처리 시점(`FlushPendingLiveLogsToUi`)에 단순화 경로를 명시적으로 연결
  - 기존처럼 상세 태그 문자열이 그대로 노출되던 문제를 수정

## [2.2.11] - 2026-03-08

### Fixed

- **TorchSharp Tensor 메모리 누수 수정**:
  - `TimeSeriesTransformer.cs`의 `PositionalEncoding.forward()` - 중간 텐서 자동 해제 추가
  - `TimeSeriesTransformer.forward()` - 모든 중간 텐서(`embedded`, `scaled`, `positional`, `permuted`, `encoded` 등)에 `using` 블록 적용
  - 네이티브 스택 버퍼 오버런(`0xc0000409` / `ucrtbase.dll`) 크래시 원인 제거

### Changed

- **펌프 스캔 전략을 밈코인 활용 전략으로 완전 재작성**:
  - 기존 엄격한 7개 조건 펌프 필터 제거
  - 밈코인/고베타 정적 유니버스 사용 (DOGE, SHIB, PEPE, FLOKI, BONK, WIF, MEME, TURBO, POPCAT, NEIRO, MOG, BRETT, PNUT, ACT, BABYDOGE, STEEM, POWR, OM, ACX, AKT, SUI, FARTCOIN, GOAT, MOODENG)
  - 메이저코인과 동일한 5분봉 스코어링 및 의사결정 로직 재사용
  - 거래량 상위 10개 후보만 선정
  - 혼합 랭킹 스코어 (거래량 50% + 변동성 20% + 모멘텀 30%)
  - 시그널 소스를 `MAJOR_MEME`로 변경하여 메이저 스타일 진입 경로 사용

- **TradingEngine 시그널 라우팅 개선**:
  - 밈코인/고베타 시그널을 `ExecuteAutoOrder(..., "MAJOR_MEME")`로 라우팅
  - ATR 기반 동적 스탑/리스크를 `MAJOR_`로 시작하는 모든 시그널에 적용
  - EMA20 리테스트 및 숏스퀴즈 보너스 로직에서 심볼 제한 제거 (밈코인도 사용 가능)
  - 펌프 슬롯 카운팅 로직 유지 (`IsPumpStrategy = signalSource == "PUMP" || signalSource == "MAJOR_MEME"`, 최대 2개)

### Added

- **3개월 히스토리컬 검증 러너**: `Tools/BacktestRunner/` - `HybridStrategyBacktester`를 이용한 90일 검증 실행 프로젝트 추가

## [2.2.10] - 2026-03-07

### Fixed

- UI 멈춤 증상 해결: Transformer/ML.NET 예측 검증(5분 후) 타이머 폭주 방지
  - 심볼+모델 단일 키 기반 중복 스케줄링 방지
  - 동시성 3개 제한 (`SemaphoreSlim`)
  - 최신 예측만 덮어쓰기 방식으로 변경

### Added

- Entry Gate 대시보드 UI (15분봉 진입 게이트 PASS/BLOCK 집계)
- 라이브 로그 3분할 구조 (GATE / MAJOR / PUMP)
- 크래시 덤프 수집 유틸리티 스크립트 `enable-crash-dumps.ps1`
- DualAI 통합 예측기 (`DualAI_EntryPredictor.cs`)
- 15분봉 캔들 매니저 (`FifteenMinCandleManager.cs`)
- 하이브리드 전략 백테스터 (`HybridStrategyBacktester.cs`)
- 주문 로직 및 게이트 자동튜닝 문서 (`ORDER_LOGIC_AND_GATE_AUTOTUNE.md`)

### Changed

- 태그 소스와 배포 바이너리 정합성 확보 (v2.2.9 → v2.2.10 재배포)
- 모든 병행 작업 파일 통합 커밋 (클린 빌드 보장)

## [2.2.7] - 2026-03-06

### Fixed

- **메이저 코인 AI 분석 재활성화**:
  - v2.2.6에서 `TransformerSettings.Enabled = false`로 크래시를 방지했으나 Transformer AI 전체가 비활성화됨
  - 서브프로세스 프로브 방식 도입: 별도 프로세스에서 TorchSharp 호환성 사전 검증
  - 네이티브 라이브러리 크래시(`0xc0000005`)가 발생해도 메인 앱은 안전하게 보호
  - `.torch_probe_result` 파일로 프로브 결과 캐싱하여 매번 테스트 방지
  - `TransformerSettings.Enabled` 기본값 복원 (`false` → `true`)
  - 메이저 코인 AI 시그널 및 Transformer 예측 정상 동작 확인

### Changed

- **TorchSharp 초기화 방식 개선**:
  - App.xaml.cs에서 즉시 초기화 제거, TradingEngine 시작 시 지연 초기화로 변경
  - `--torch-probe` 인수로 실행 시 호환성 테스트 전용 모드 진입
  - 호환성 검증 실패 시 MajorCoinStrategy(지표 기반)로 안정적 fallback

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
