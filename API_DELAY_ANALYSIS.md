# API Delay Analysis Report - v5.9.3

## 1. 문제 요약

실계좌에서 "주문요청중" 메시지가 대량 발생하고, API 딜레이로 인해:
- 손절(SL) 실행이 5초 이상 지연
- 마진 100달러 → 1000달러 오진입
- 바이낸스 Rate Limit (429) 발생

---

## 2. 근본 원인

### 2-1. 병렬 ExecuteAutoOrder 호출 (v5.9.2 이전)

| 호출 경로 | 방식 | 문제 |
|-----------|------|------|
| TICK_SURGE (TickDensityMonitor) | `Task.Run(async () => await ExecuteAutoOrder(...))` | 10개 심볼 동시 트리거 → 10개 스레드 동시 실행 |
| BUY_PRESSURE (TickDensityMonitor) | `Task.Run(async () => await ExecuteAutoOrder(...))` | 동일 |
| PUMP_WATCH_CONFIRMED | `Task.Run(async () => await ExecuteAutoOrder(...))` | 감시 종목 다수 동시 확인 |
| SQUEEZE_BREAKOUT | `Task.Run(async () => await ExecuteAutoOrder(...))` | BB 스퀴즈 동시 감지 |

**1회 ExecuteAutoOrder 내부 API 호출:**

| 단계 | API 호출 | 소요 시간 |
|------|----------|-----------|
| GetKlinesAsync (5분봉 140봉) | 1회 | 200~1000ms |
| GetKlinesAsync (15분봉 80봉) | 1회 | 200~1000ms |
| SetLeverageAsync | 1회 | 200~500ms |
| PlaceMarketOrderAsync | 1회 | 500~5000ms |
| RegisterSL (PlaceConditionalOrderAsync) | 1회 | 200~500ms |
| RegisterTP (PlaceConditionalOrderAsync) | 1회 | 200~500ms |
| RegisterTrailing (PlaceConditionalOrderAsync) | 1회 | 200~500ms |

**1회 진입 = API 7회, 최소 1.5초 ~ 최대 9초**

10개 동시 실행 시: **API 70회/10초 → Rate Limit 300/10초의 23%를 한 번에 소모**
20개 동시 실행 시: **API 140회/10초 → Rate Limit의 47%**

### 2-2. 시뮬레이션 vs 실계좌 차이

| 항목 | 시뮬레이션 | 실계좌 |
|------|------------|--------|
| PlaceMarketOrderAsync | Mock: <50ms | Binance API: 500~5000ms |
| GetKlinesAsync | Mock/Cache: <50ms | Binance API: 200~1000ms |
| SetLeverageAsync | Mock: <50ms | Binance API: 200~500ms |
| SL/TP 등록 | Skip | 각 200~500ms |
| **1회 진입 총 소요** | **<100ms** | **1.5~9초** |
| **세마포어 체증** | 없음 (즉시 통과) | 10~100+ 대기 |

**결론: 시뮬레이션에서 "주문요청중"이 안 나오는 이유는 API 호출이 없어서 즉시 완료되기 때문**

### 2-3. 마진 금액 오류 메커니즘

```
Thread A: _settings.DefaultMargin 읽기 = $100 (정상)
    ↓ (API 3초 대기 중...)
Thread B: 사용자가 설정 변경 → _settings.DefaultMargin = $1000
    ↓
Thread C: _settings.DefaultMargin 읽기 = $1000 (변경된 값)
    ↓
Thread A: PlaceMarketOrderAsync 실행 (margin=$100 정상)
Thread C: PlaceMarketOrderAsync 실행 (margin=$1000 ← 의도하지 않은 금액!)
```

`_settings` 읽기에 락이 없어서 병렬 실행 중 설정 변경되면 서로 다른 마진값으로 진입.

---

## 3. v5.9.3 해결 방안

### 3-1. 큐 기반 직렬화 (Queue + Single Processor)

```
TICK_SURGE/BUY_PRESSURE/PUMP_WATCH
    ↓
EnqueueEntry() → ConcurrentQueue에 적재 (즉시 반환, API 호출 없음)
    ↓
ProcessEntryQueueAsync → 1개씩 꺼내서 순차 실행
    ↓
슬롯 포화 시 → 큐 전체 비움 (무의미한 대기 방지)
```

**이전 (v5.9.2 세마포어):**
- 10개 스레드 → 세마포어 5초 대기 → 9개 타임아웃 스킵
- 문제: 5초 대기 동안 API 호출 못하므로 SL 등록 지연

**이후 (v5.9.3 큐):**
- 10개 신호 → 큐에 적재 (0ms) → 프로세서가 1개씩 처리
- 슬롯 차면 남은 큐 전부 폐기
- API 호출은 항상 1개만 진행

### 3-2. 마진 스냅샷

```
EnqueueEntry 시점:
    snapDefaultMargin = _settings.DefaultMargin  ← 이 시점의 값 고정
    snapPumpMargin = _settings.PumpMargin

ExecuteAutoOrder에서:
    ctx.SnapshotDefaultMargin = snapDefaultMargin  ← 고정값 사용
    GetAdaptiveEntryMarginUsdtAsync(token, ctx.SnapshotDefaultMargin)
```

### 3-3. Stop 버튼 안전 종료

```
이전: _cts.Cancel() → _cts.Dispose() → null (즉시)
  → 진행 중인 작업에서 ObjectDisposedException 발생

이후: 
  1. 큐 비우기 (대기 진입 전부 폐기)
  2. _cts.Cancel() (취소 신호)
  3. _cts = null (새 작업 방지)
  4. 2초 후 Dispose (진행 중인 작업 종료 대기)
```

---

## 4. API 호출 최적화 권장사항

### 4-1. GetKlines 캐시 활용
- 현재: 진입마다 5분봉 140봉 + 15분봉 80봉 API 호출
- 권장: `_marketDataManager.KlineCache` 사용 (이미 WebSocket으로 실시간 업데이트)
- 절감: API 2회 → 0회 (진입당)

### 4-2. 가용잔고 캐시
- 현재: TICK_SURGE/BUY_PRESSURE 핸들러에서 `GetAvailableBalanceAsync` 호출
- 권장: 30초 간격 캐시 (잔고 급변 확률 낮음)
- 절감: 신호당 API 1회 → 0회

### 4-3. SetLeverage 스킵
- 서브계정은 레버리지 변경 불가 (항상 실패)
- 이미 v5.7.5에서 실패 무시 처리
- 권장: 한 번 실패하면 해당 심볼 레버리지 변경 시도 영구 스킵

### 4-4. Rate Limit 사용량 모니터링
- Binance 응답 헤더: `X-MBX-USED-WEIGHT-1M`
- 1200/분 한도의 80% (960) 초과 시 자동 쓰로틀링 권장

---

## 5. 영향도 요약

| 항목 | 이전 (v5.9.2) | 이후 (v5.9.3) |
|------|--------------|--------------|
| 동시 API 호출 | 10~20개 병렬 | 1개 직렬 |
| "주문요청중" 빈도 | 10~20개/이벤트 | 1개/이벤트 |
| 마진 금액 정확도 | 경합 시 오류 | 스냅샷 고정 |
| SL/TP 등록 지연 | 5~30초 | <3초 |
| Rate Limit 위험 | 높음 (47%/10초) | 낮음 (7%/10초) |
| Stop 버튼 안정성 | ObjectDisposedException | 안전 종료 |
