# GeneralSettings DB 저장 및 UI 관리 구현 가이드

## 📋 작업 완료 내용

GeneralSettings를 MSSQL 2019 데이터베이스에 저장하고, UI 창에서 설정할 수 있도록 구현했습니다.

## 🗄️ 1. DB 스키마 (MSSQL 2019)

파일: [Database/GeneralSettings_Schema.sql](Database/GeneralSettings_Schema.sql)

```sql
-- GeneralSettings 테이블이 생성되었습니다
CREATE TABLE dbo.GeneralSettings (
    Id INT PRIMARY KEY DEFAULT 1,
    DefaultLeverage INT NOT NULL DEFAULT 20,
    DefaultMargin DECIMAL(18, 4) NOT NULL DEFAULT 200.0,
    TargetRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0,
    StopLossRoe DECIMAL(18, 4) NOT NULL DEFAULT 15.0,
    TrailingStartRoe DECIMAL(18, 4) NOT NULL DEFAULT 20.0,
    TrailingDropRoe DECIMAL(18, 4) NOT NULL DEFAULT 5.0,
    UpdatedAt DATETIME2 DEFAULT GETUTCDATE(),
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
);
```

**⚠️ 필수 단계: SQL Server Management Studio에서 위 스크립트를 실행하여 테이블을 생성하세요.**

### 2026-03-14 증분 마이그레이션 (PUMP 추세홀딩 튜닝)

운영 DB가 이미 존재하는 경우 아래 증분 스크립트만 추가 실행하면 됩니다.

- [Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql](Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql)

추가 컬럼(기본값):

- `PumpFirstTakeProfitRatioPct` (15.0)
- `PumpStairStep1Roe` (50.0)
- `PumpStairStep2Roe` (100.0)
- `PumpStairStep3Roe` (200.0)

검증 쿼리:

```sql
SELECT c.name AS ColumnName, t.name AS DataType, c.precision, c.scale, c.is_nullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.GeneralSettings')
    AND c.name IN ('PumpFirstTakeProfitRatioPct', 'PumpStairStep1Roe', 'PumpStairStep2Roe', 'PumpStairStep3Roe')
ORDER BY c.column_id;
```

### 2026-03-14 운영값 보정 (메이저 레거시 기본값 정규화)

운영 DB에 과거 기본값이 남아 있으면 아래 스크립트를 추가 실행하세요.

- [Database/GeneralSettings_NormalizeMajorDefaults_20260314.sql](Database/GeneralSettings_NormalizeMajorDefaults_20260314.sql)

보정 규칙:

- `MajorTp1Roe`: `15` → `20`
- `MajorTp2Roe`: `25` → `40`
- `MajorTrailingStartRoe`: `20/22` → `40`
- `MajorTrailingGapRoe`: `4` → `5`
- `MajorStopLossRoe`: `60` → `20`

※ 사용자 커스텀 값은 유지하고, 레거시 기본값/비정상값만 업데이트합니다.

## 📝 2. DbManager.cs 변경 사항

[TradingBot/DbManager.cs](TradingBot/DbManager.cs#L339-L414)에 2개 메서드가 추가되었습니다:

### SaveGeneralSettingsAsync()

```csharp
public async Task SaveGeneralSettingsAsync(TradingSettings settings)
```

- GeneralSettings를 DB에 저장
- MERGE 문으로 데이터 없으면 삽입, 있으면 업데이트

### LoadGeneralSettingsAsync()

```csharp
public async Task<TradingSettings?> LoadGeneralSettingsAsync()
```

- DB에서 GeneralSettings 로드
- 반환값: TradingSettings 객체 또는 null

## 🖼️ 3. SettingsWindow.xaml 개선

[TradingBot/SettingsWindow.xaml](TradingBot/SettingsWindow.xaml)에 새 필드 추가:

- **기본 마진 (USDT)**: `txtDefaultMargin` - 기본값 200.0

**UI 변경 사항:**

- General 탭에 "기본 마진" 입력 필드 추가
- Grid RowDefinitions 8개 → 9개 증가

## 💾 4. SettingsWindow.xaml.cs 개선

[TradingBot/SettingsWindow.xaml.cs](TradingBot/SettingsWindow.xaml.cs)의 주요 변경:

### 초기화

```csharp
private DbManager? _dbManager;

public SettingsWindow()
{
    InitializeComponent();
    // DbManager 자동 초기화
    if (!string.IsNullOrEmpty(AppConfig.ConnectionString))
    {
        _dbManager = new DbManager(AppConfig.ConnectionString);
    }
    LoadSettings();
}
```

### 저장 로직 (btnSave_Click)

- **기존**: appsettings.json 파일에만 저장
- **새로운**: 파일 + DB 동시 저장
- TradingSettings 객체 생성 후 `SaveGeneralSettingsAsync()` 호출
- 저장 결과에 따라 다른 메시지 표시

```csharp
if (_dbManager != null)
{
    await _dbManager.SaveGeneralSettingsAsync(generalSettings);
    MessageBox.Show("✅ 설정이 파일과 데이터베이스에 저장되었습니다.");
}
```

## 🚀 5. App.xaml.cs 통합

[TradingBot/App.xaml.cs](TradingBot/App.xaml.cs)의 변경:

### 앱 시작 시 DB 로드

```csharp
// OnStartup 메서드에서
AppConfig.Load();
_ = LoadGeneralSettingsFromDbAsync();  // ← 새로 추가
```

### LoadGeneralSettingsFromDbAsync() 메서드

```csharp
private async Task LoadGeneralSettingsFromDbAsync()
{
    if (string.IsNullOrEmpty(AppConfig.ConnectionString))
        return;
    
    var dbManager = new DbManager(AppConfig.ConnectionString);
    var dbSettings = await dbManager.LoadGeneralSettingsAsync();
    
    if (dbSettings != null && AppConfig.Current != null)
    {
        AppConfig.Current.Trading.GeneralSettings = dbSettings;
        // DB의 설정이 appsettings.json을 덮어씀
    }
}
```

## 🔄 동작 흐름

### 1️⃣ 앱 시작

```
App.xaml.cs OnStartup
└─ AppConfig.Load()         [appsettings.json 읽음]
└─ LoadGeneralSettingsFromDbAsync()  [DB에서 최신 설정 읽음]
   └─ DbManager.LoadGeneralSettingsAsync()
      └─ AppConfig 업데이트
```

### 2️⃣ 사용자가 설정 변경

```
SettingsWindow 열기
└─ LoadSettings()       [appsettings.json + DB에서 로드]
└─ 사용자 입력
└─ btnSave_Click
   ├─ appsettings.json에 저장
   └─ DbManager.SaveGeneralSettingsAsync()  [DB에 저장]
      └─ MessageBox: "✅ 파일과 DB에 저장됨"
      └─ 앱 재시작 권장
```

### 3️⃣ 엔진 실행

```
TradingEngine._settings 초기화
└─ AppConfig.Current.Trading.GeneralSettings 사용
   (DB에서 로드된 최신 값)
```

## 📊 저장되는 항목

| 항목 | 타입 | 기본값 |
|------|------|--------|
| DefaultLeverage | int | 20 |
| DefaultMargin | decimal | 200.0 |
| TargetRoe | decimal | 20.0 |
| StopLossRoe | decimal | 15.0 |
| TrailingStartRoe | decimal | 20.0 |
| TrailingDropRoe | decimal | 5.0 |

### PUMP 추세홀딩 튜닝 항목 (추가)

| 항목 | 타입 | 기본값 |
|------|------|--------|
| PumpFirstTakeProfitRatioPct | decimal | 15.0 |
| PumpStairStep1Roe | decimal | 50.0 |
| PumpStairStep2Roe | decimal | 100.0 |
| PumpStairStep3Roe | decimal | 200.0 |

## ✅ 빌드 결과

```
✅ 빌드 성공 (경고만 있음 - 기존 코드 스타일 관련)
✅ 모든 새 메서드 추가 완료
✅ UI 컨트롤 추가 완료
```

## 🚨 주의사항

### 1. DB 테이블 생성 필수

```sql
-- SQL Server Management Studio에서 실행
-- 파일: Database/GeneralSettings_Schema.sql
```

### 2. 앱 재시작 권장

- DB 저장 후 `MessageBox`에서 "재시작 권장" 메시지 표시
- TradingEngine이 새로운 설정으로 시작되도록 설정

### 3. 연결 문자열 필수

- `AppConfig.ConnectionString`이 비어있으면 DB 로드 스킵
- 설정 창에서 DB 연결 문자열 입력 필요

### 4. 암호화된 연결 문자열

- appsettings.json의 `IsEncrypted: true`인 경우 자동 복호화
- 평문 저장 필요 시 `IsEncrypted: false`로 변경

## 📱 UI 사용 방법

1. **메뉴 → 설정** 클릭 (또는 앱 시작 시 DB 연결 안 됐을 때)
2. **General 탭** 선택
3. 다음 항목 입력:
   - 기본 마진 (USDT): 200.0
   - 레버리지 (x): 20
   - 목표 ROE (%): 20.0
   - 손절 ROE (%): 15.0
4. **저장** 클릭
5. MessageBox 확인 후 앱 재시작

## 🔍 디버깅 팁

### DB 저장 안 됨

```csharp
// App.xaml.cs의 LoadGeneralSettingsFromDbAsync에 로그 추가
Debug.WriteLine($"[DB] 로드 결과: {dbSettings != null}");
```

### 설정이 적용 안 됨

```csharp
// TradingEngine 초기화 시
Debug.WriteLine($"[Engine] DefaultLeverage: {_settings.DefaultLeverage}");
```

### DB 연결 실패

```
설정 창 → DB 연결 문자열 확인
→ SQL Server가 실행 중인지 확인
→ MSSQL 2019 GeneralSettings 테이블 생성 여부 확인
```

## 📂 수정된 파일 목록

1. ✅ [Database/GeneralSettings_Schema.sql](Database/GeneralSettings_Schema.sql) - **새로 생성**
2. ✅ [Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql](Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql) - **증분 마이그레이션 추가**
3. ✅ [Database/GeneralSettings_NormalizeMajorDefaults_20260314.sql](Database/GeneralSettings_NormalizeMajorDefaults_20260314.sql) - **메이저 운영값 보정 스크립트 추가**
4. ✅ [TradingBot/DbManager.cs](TradingBot/DbManager.cs) - SaveGeneralSettingsAsync, LoadGeneralSettingsAsync 메서드 추가
5. ✅ [TradingBot/SettingsWindow.xaml](TradingBot/SettingsWindow.xaml) - txtDefaultMargin 필드 추가
6. ✅ [TradingBot/SettingsWindow.xaml.cs](TradingBot/SettingsWindow.xaml.cs) - DbManager 통합, 저장 로직 개선
7. ✅ [TradingBot/App.xaml.cs](TradingBot/App.xaml.cs) - LoadGeneralSettingsFromDbAsync 메서드, 시작 시 DB 로드

## 🎯 다음 단계

### 선택사항 (추가 개선)

- [ ] TradingEngine에서 저장 후 자동 재로드 기능
- [ ] 설정 변경 시 실시간 반영 (앱 재시작 없음)
- [ ] 다중 프로필 지원 (기존 설정으로 저장/복구)
- [ ] 설정 내역 감시 및 로깅

---

**작업 날짜**: 2026년 2월 28일  
**문서 업데이트**: 2026년 3월 14일 (PUMP 추세홀딩 튜닝 마이그레이션 반영)  
**빌드 상태**: ✅ 성공  
**테스트 필요**: SQL Server MSSQL 2019에서 테이블 생성 후 전체 테스트
