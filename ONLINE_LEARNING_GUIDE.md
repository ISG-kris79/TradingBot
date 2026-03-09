# ✨ AI 온라인 학습 시스템 - 사용 가이드

## 🎯 개요

**적응형 온라인 학습 시스템**이 구현되었습니다. 이제 AI 모델이 **시장 변화를 실시간으로 학습**하며 자동으로 진화합니다.

---

## 🆕 핵심 기능

### 1️⃣ **슬라이딩 윈도우 학습**
- 최근 1000건의 진입 데이터만 유지 (메모리 효율)
- 오래된 패턴은 자동 제거 → 최신 시장에 집중

### 2️⃣ **주기적 재학습**
- **1시간마다** 자동 모델 업데이트
- 또는 **100건 신규 샘플** 도달 시 즉시 재학습
- 빠른 학습: Transformer 에포크 5회 (약 2~3분)

### 3️⃣ **Concept Drift 감지**
- 최근 20건 정확도 추적
- 기준선 대비 **10%p 이상 하락** 시 즉시 긴급 재학습
- 예: 정확도 70% → 60% 감지 시 자동 대응

### 4️⃣ **적응형 Confidence Threshold**
- 정확도 높으면 → Threshold 상향 (선별적 진입)
- 정확도 낮으면 → Threshold 하향 (기회 확대)
- 범위: 0.50 ~ 0.85 자동 조절

---

## 📊 동작 흐름

```
1. 진입 결정
   ↓
2. 15분 대기 (실제 수익률 관찰)
   ↓
3. 라벨링 완료 (Label=1/0)
   ↓
4. 슬라이딩 윈도우에 추가
   ↓
5. 자동 트리거 조건 확인:
   - 100건 도달?
   - 1시간 경과?
   - Concept Drift 감지?
   ↓
6. 재학습 실행 (ML.NET + Transformer)
   ↓
7. Threshold 자동 조정
   ↓
8. 다음 진입부터 새 모델 적용 ✨
```

---

## 🚀 즉시 시작 방법

### **설정 (이미 통합 완료)**

```csharp
// TradingEngine.cs 또는 MainWindow.xaml.cs에서

var _aiGate = new AIDoubleCheckEntryGate(
    _exchangeService,
    config: new DoubleCheckConfig
    {
        MinMLConfidence = 0.65f,       // 초기 ML.NET threshold
        MinTransformerConfidence = 0.60f, // 초기 Transformer threshold
        // ... 기타 설정
    },
    enableOnlineLearning: true  // ✅ 온라인 학습 활성화
);
```

**그게 전부입니다!** 나머지는 자동으로 동작합니다.

---

## 📈 성능 모니터링

### **로그 확인**

```
[OnlineLearning] 초기화 완료: 윈도우=1000, 재학습주기=1시간
[OnlineLearning] 초기 윈도우 로드 완료: 342건
---
[OnlineLearning] 샘플 추가: BTCUSDT PnL=2.34% → Label=True | 윈도우=343
[OnlineLearning] 샘플 추가: ETHUSDT PnL=-0.82% → Label=False | 윈도우=344
---
🔄 [OnlineLearning] 재학습 시작: 샘플 수 도달 | 샘플 수=400
✅ [OnlineLearning] ML.NET 재학습 완료: 정확도=74.2%, F1=0.712
✅ [OnlineLearning] Transformer 재학습 완료: 정확도=72.8%
🎚️ [OnlineLearning] Threshold 조정: ML=67% (정확도=74%), TF=62% (정확도=73%)
---
⚠️ [OnlineLearning] Concept Drift 감지! 최근=58%, 기준=70% → 긴급 재학습
🔄 [OnlineLearning] 재학습 시작: Concept Drift 감지 | 샘플 수=512
```

### **이벤트 구독 (옵션)**

```csharp
_aiGate.OnLog += msg => Console.WriteLine(msg);

// UI 알림
_onlineLearning.OnPerformanceUpdate += (reason, acc, mlThresh, tfThresh) =>
{
    MainWindow.Instance?.AddAlert(
        $"🧠 온라인 학습: {reason}\n" +
        $"정확도={acc:P1}, ML={mlThresh:P0}, TF={tfThresh:P0}");
};
```

---

## 🎛️ 고급 설정

### **커스터마이징 (필요 시)**

```csharp
var _aiGate = new AIDoubleCheckEntryGate(
    _exchangeService,
    config: new DoubleCheckConfig { /* ... */ },
    enableOnlineLearning: true
);

// 내부 OnlineLearningConfig 수정 (선택)
// AdaptiveOnlineLearningService.cs 생성자 참조
// - SlidingWindowSize: 1000 → 2000 (더 많은 히스토리)
// - RetrainingIntervalHours: 1.0 → 0.5 (30분마다)
// - TriggerEveryNSamples: 100 → 50 (더 빠른 반응)
```

---

## 🔍 디버깅 팁

### **1. 학습이 실행되지 않음**
```csharp
// 체크리스트:
// ✅ enableOnlineLearning: true?
// ✅ 최소 샘플 수 200건 이상?
// ✅ 라벨링이 정상 동작? (15분 후 LabelActualProfitAsync 호출)
```

### **2. Threshold가 변하지 않음**
```csharp
// 정확도 변화가 작으면 threshold도 작게 변함
// AdjustThresholds 로직:
// - 정확도 >= 75% → +2%
// - 정확도 < 65% → -3%
```

### **3. Concept Drift 너무 자주 발생**
```csharp
// 노이즈가 많은 시장에서 발생 가능
// 해결책: Drift threshold 완화
// DetectAndReactToDrift에서:
double driftThreshold = _baselineAccuracy - 0.15; // 10% → 15%로 완화
```

---

## 📦 체크포인트 저장/복원

### **상태 저장**
```csharp
// 서버 종료 전 또는 주기적으로
await _onlineLearning.SaveCheckpointAsync("OnlineLearningState.json");

// 저장 내용:
// - 현재 정확도
// - Threshold 설정
// - 최근 20건 예측 결과
// - 마지막 학습 시각
```

### **상태 복원**
```csharp
// 재시작 시 자동으로 초기 윈도우 로드됨
// 추가 복원 로직은 향후 추가 예정
```

---

## 💡 기대 효과

### **Before (기존 배치 학습)**
- ❌ 하루 1회 GitHub Actions 학습 (느림)
- ❌ 시장 변화에 12~24시간 지연 반응
- ❌ 고정 Threshold (시장 상황 무시)

### **After (온라인 학습)**
- ✅ 1시간마다 자동 재학습 (빠름)
- ✅ Concept Drift 즉시 감지 및 대응
- ✅ 적응형 Threshold (시장 맞춤)
- ✅ 최신 1000건에만 집중 (노이즈 제거)

---

## 🧪 실험적 기능 (추후 추가 가능)

### **1. 코인별 전문가 모델**
```csharp
// BTC 전용, ETH 전용, ALT 전용 슬라이딩 윈도우
var btcWindow = new AdaptiveOnlineLearningService(
    btcTrainer, btcTransformer, new OnlineLearningConfig { ... });
```

### **2. 시간대별 학습**
```csharp
// 미국 시간 / 아시아 시간 별도 학습
if (DateTime.UtcNow.Hour >= 13 && DateTime.UtcNow.Hour <= 21) 
{
    usWindow.AddLabeledSampleAsync(...);
} else {
    asiaWindow.AddLabeledSampleAsync(...);
}
```

### **3. 강화학습 통합**
```csharp
// Reward = PnL × (1 - MDD) × Sharpe
// PPO 에이전트로 장기 수익 최적화
```

---

## 📚 관련 파일

- `AdaptiveOnlineLearningService.cs` - 핵심 온라인 학습 서비스
- `AIDoubleCheckEntryGate.cs` - 통합 진입 게이트 (라벨링 → 학습 파이프라인)
- `EntryTimingMLTrainer.cs` - ML.NET 재학습
- `EntryTimingTransformerTrainer.cs` - Transformer 재학습

---

## 🎯 결론

이제 AI 모델이 **살아있는 유기체**처럼 시장을 학습합니다:
- 🌅 아침: 어제 실패한 패턴 학습
- ☀️ 낮: 실시간 시장 변화 추적
- 🌙 밤: Concept Drift 감지 및 대응
- 🔄 항상: 적응형 Threshold로 최적 진입 타이밍 포착

**더 이상 수동 재학습 필요 없음. 시장이 변하면 AI도 변합니다.** 🚀
