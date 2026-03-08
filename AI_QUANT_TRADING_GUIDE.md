# 🤖 AI 퀀트 트레이딩 시스템 - 완전 가이드

## 📋 시스템 개요

기존 **가격 예측 (Regression)** 방식에서 **진입 타이밍 분류 (Binary Classification)** 방식으로 완전히 재설계된 AI 시스템입니다.

### **핵심 변화**

| 항목 | 기존 시스템 | 새로운 AI 시스템 |
|------|------------|-----------------|
| **예측 목표** | 다음 가격 예측 | "지금 진입하면 수익날까?" |
| **출력** | 가격 (Regression) | 진입 1 / 대기 0 (Binary) |
| **입력** | 15분봉 단일 타임프레임 | 1D/4H/2H/1H/15M 멀티 타임프레임 |
| **학습 데이터** | 과거 가격 변화 | 실제 수익/손실 백테스트 결과 |
| **진입 판단** | ML.NET 단독 | ML.NET + Transformer 더블체크 |
| **학습 주기** | 수동 | GitHub Actions 매일 자동 |

---

## 🏗️ 아키텍처

```
┌─────────────────────────────────────────────────────────────┐
│               📊 Multi-Timeframe Data                        │
├─────────────────────────────────────────────────────────────┤
│  1일봉 (대세 추세) → 4시간봉 (중기) → 2시간봉 (파동)        │
│  → 1시간봉 (모멘텀) → 15분봉 (진입 시점)                   │
└────────────────┬────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────┐
│        🧠 AI Double-Check Entry Gate                         │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌─────────────┐           ┌──────────────────┐             │
│  │  ML.NET     │           │  Transformer     │             │
│  │  LightGBM   │           │  TorchSharp      │             │
│  │             │           │                  │             │
│  │  진입: 65%+ │◄──AND──►  │  진입: 60%+      │             │
│  └─────────────┘           └──────────────────┘             │
│         │                           │                        │
│         └───────────┬───────────────┘                        │
│                     ▼                                        │
│          ✅ 둘 다 승인 → 진입                                 │
│          ❌ 하나라도 거부 → 대기                              │
└─────────────────────────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│          📝 실시간 데이터 수집 (학습용)                       │
├─────────────────────────────────────────────────────────────┤
│  • 진입 결정 + Feature 저장                                  │
│  • 15분 후 실제 수익률 레이블링                              │
│  • JSON 파일로 자동 저장 → GitHub Actions 입력               │
└─────────────────────────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│       🔄 GitHub Actions - 지속적 학습 (매일 자동)             │
├─────────────────────────────────────────────────────────────┤
│  1. 서버에서 학습 데이터 다운로드                            │
│  2. ML.NET + Transformer 재학습                              │
│  3. 모델 검증 (정확도 65% 이상)                              │
│  4. 새 모델 자동 배포                                        │
│  5. Telegram 알림                                            │
└─────────────────────────────────────────────────────────────┘
```

---

## 🚀 사용 방법

### **1단계: 모델 초기 학습 (백테스트 데이터)**

```csharp
// 과거 데이터로 초기 모델 학습
var featureExtractor = new MultiTimeframeFeatureExtractor(_exchangeService);
var labeler = new BacktestEntryLabeler(new EntryLabelConfig
{
    TargetProfitPct = 2.0m,  // 목표 2% 수익
    StopLossPct = -1.0m,     // 손절 1%
    EvaluationPeriodCandles = 16  // 4시간 평가 (15분봉 16개)
});

// BTCUSDT 과거 데이터 수집
var d1Klines = await _exchangeService.GetKlinesAsync("BTCUSDT", KlineInterval.OneDay, 200, token);
var h4Klines = await _exchangeService.GetKlinesAsync("BTCUSDT", KlineInterval.FourHour, 500, token);
// ... (다른 타임프레임 생략)

// Feature + Label 생성
var trainingData = featureExtractor.ExtractHistoricalFeatures(
    "BTCUSDT", d1Klines, h4Klines, h2Klines, h1Klines, m15Klines, labeler, isLongStrategy: true);

// ML.NET 학습
var mlTrainer = new EntryTimingMLTrainer();
var mlMetrics = await mlTrainer.TrainAndSaveAsync(trainingData);
Console.WriteLine($"ML.NET 정확도: {mlMetrics.Accuracy:P2}");

// Transformer 학습
var tfTrainer = new EntryTimingTransformerTrainer();
var tfMetrics = await tfTrainer.TrainAsync(trainingData, epochs: 30);
Console.WriteLine($"Transformer 정확도: {tfMetrics.BestValidationAccuracy:P2}");
```

### **2단계: 실시간 진입 판단 (TradingEngine 통합)**

```csharp
// TradingEngine에 AI Gate 추가
private AIDoubleCheckEntryGate? _aiGate;

public async Task InitializeAsync()
{
    // 기존 초기화 코드...
    
    // AI Gate 초기화
    _aiGate = new AIDoubleCheckEntryGate(_exchangeService, new DoubleCheckConfig
    {
        MinMLConfidence = 0.65f,
        MinTransformerConfidence = 0.60f,
        MinMLConfidenceMajor = 0.75f,  // 메이저 코인은 더 보수적
        MinMLConfidencePumping = 0.58f // 펌핑 코인은 약간 완화
    });
    
    Console.WriteLine($"[TradingEngine] AI Gate Ready: {_aiGate.IsReady}");
}

// ExecuteAutoOrder 메서드 내 Gate 로직 대체
if (latestCandle != null && _aiGate != null && _aiGate.IsReady)
{
    // 코인 타입 판별
    CoinType coinType = MajorSymbols.Contains(symbol) ? CoinType.Major :
                       symbol.EndsWith("USDT") && volumeRatio > 3.0f ? CoinType.Pumping :
                       CoinType.Normal;

    // AI 더블체크 진입 심사
    var (allowEntry, reason, detail) = await _aiGate.EvaluateEntryWithCoinTypeAsync(
        symbol, decision, currentPrice, coinType, token);

    if (!allowEntry)
    {
        OnStatusLog?.Invoke($"⛔ [AI_GATE] {symbol} {decision} 차단 | {reason}");
        EntryLog("AI_GATE", "BLOCK", reason);
        return;
    }

    OnStatusLog?.Invoke(
        $"✅ [AI_GATE] {symbol} {decision} 승인 | ML={detail.ML_Confidence:P1} TF={detail.TF_Confidence:P1}");
    EntryLog("AI_GATE", "PASS", reason);
    
    // 진입 후 15분 뒤 수익률 레이블링 스케줄링
    _ = Task.Delay(TimeSpan.FromMinutes(15), token).ContinueWith(async _ =>
    {
        await _aiGate.LabelActualProfitAsync(symbol, DateTime.UtcNow, currentPrice, decision == "LONG", token);
    }, token);
}
```

### **3단계: 데이터 수집 확인**

```powershell
# 수집된 학습 데이터 확인
Get-ChildItem -Path "TradingBot\TrainingData\EntryDecisions" -Filter "*.json"

# 데이터 샘플 출력
Get-Content "TradingBot\TrainingData\EntryDecisions\EntryDecisions_20260308_120000.json" | ConvertFrom-Json | Select-Object -First 1 | Format-List
```

### **4단계: GitHub Actions 자동 학습 활성화**

1. **Secrets 설정** (Repository Settings → Secrets and variables → Actions)
   - `TELEGRAM_BOT_TOKEN`: Telegram 봇 토큰
   - `TELEGRAM_CHAT_ID`: 알림받을 채팅 ID

2. **워크플로우 수동 실행 테스트**
   ```
   GitHub → Actions 탭 → "AI Model Continuous Learning" → Run workflow
   ```

3. **매일 자동 실행 확인**
   - 매일 오전 3시 (UTC) = 한국 시간 정오에 자동 실행
   - 실행 로그는 Actions 탭에서 확인

---

## 📊 기대 효과

### **1. 뇌동매매 방지**
- **기존**: RSI 30 이하면 무조건 진입
- **새로운**: AI가 "과거 이 시간대 RSI 30은 함정이었어" → 거부 (0)

### **2. 정밀 타격 (초 단위 타이밍)**
- **기존**: 15분봉 종가 기준 진입 (최대 15분 지연)
- **새로운**: AI가 "지금 이 순간" 진입 승인 → 14분 55초 진입 가능

### **3. 펌핑 코인 별도 대응**
- **기존**: 메이저 코인 기준으로 모든 코인 판단
- **새로운**: 스팀/만트라 같은 펌핑 코인은 별도 모델로 판단

### **4. 지속적 개선**
- **기존**: 수동 재학습 필요
- **새로운**: 매일 밤 "어제 실패" 학습 → 내일은 안 속음

---

## 🔧 설정

### **appsettings.json** 추가

```json
{
  "AI": {
    "EnableDoubleCheck": true,
    "MLNetModelPath": "EntryTimingModel.zip",
    "TransformerModelPath": "EntryTimingTransformer.pt",
    "MinMLConfidence": 0.65,
    "MinTransformerConfidence": 0.60,
    "DataCollectionEnabled": true,
    "DataCollectionPath": "TrainingData/EntryDecisions"
  }
}
```

---

## 🐛 트러블슈팅

### Q: 모델이 로드되지 않음
```
[EntryTimingML] 모델 파일 없음: EntryTimingModel.zip
```
**A**: 초기 학습 필요 → 1단계 실행

### Q: 진입이 전부 차단됨 (pass 0 / block 100%)
**A**: Confidence threshold가 너무 높음 → `MinMLConfidence` 0.5로 낮춤

### Q: GitHub Actions가 실행되지 않음
**A**: 
1. `.github/workflows/` 폴더가 레포지토리 루트에 있는지 확인
2. Actions 탭에서 워크플로우 Enable 확인
3. Secrets 설정 확인

---

## 📈 성능 모니터링

### **실시간 지표 (MainWindow UI)**
```
🧠 ENTRY GATE STATUS
PASS: 0
BLOCK: 18140
ML 65.0% / TF 60.0%
AUTO_TUNE 대기
```

### **로그 확인**
```
✅ [AI_GATE] BTCUSDT LONG 승인 | ML=72.3% TF=68.1%
⛔ [AI_GATE] ETHUSDT SHORT 차단 | MLNET_Reject_Conf=48.2%
```

---

## 🎯 다음 단계

1. **펌핑 코인 전용 모델** 학습 (거래량 패턴 특화)
2. **강화학습 (RL)** 도입 (reward = 실제 수익)
3. **앙상블** 추가 (XGBoost, CatBoost)
4. **실시간 모델 업데이트** (5분마다 incremental learning)

---

**최종 목표: AI가 직접 시장을 배우고, 스스로 개선하는 완전 자동 퀀트 트레이딩 시스템 🚀**
