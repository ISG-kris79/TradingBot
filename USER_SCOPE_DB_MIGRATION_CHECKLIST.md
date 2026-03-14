# 사용자별 DB 스코프 마이그레이션 체크리스트

## 1) 사전 준비

- [ ] 적용 시간 공지 (봇/운영자)
- [ ] 봇 정지 (WPF 앱 + 백그라운드 프로세스)
- [ ] 운영 DB 백업 완료
- [ ] 연결 대상 DB가 맞는지 확인 (`SELECT DB_NAME();`)

## 2) 스키마 적용 순서 (권장)

- [ ] [Database/GeneralSettings_Schema.sql](Database/GeneralSettings_Schema.sql) 실행
  - 목적: `GeneralSettings` FK를 `Users(Id)` 기준으로 정합화
- [ ] [Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql](Database/GeneralSettings_AddPumpStairAndTpRatioColumns.sql) 실행
  - 목적: PUMP 추세홀딩 튜닝 컬럼(`PumpFirstTakeProfitRatioPct`, `PumpStairStep1Roe`, `PumpStairStep2Roe`, `PumpStairStep3Roe`) 증분 반영
  - 참고: 최신 `GeneralSettings_Schema.sql`을 전체 재적용하는 환경이면 생략 가능
- [ ] [Database/GeneralSettings_NormalizeMajorDefaults_20260314.sql](Database/GeneralSettings_NormalizeMajorDefaults_20260314.sql) 실행
  - 목적: 과거 메이저 기본값(`15/25/22/4/60`)을 최신 운용값(`20/40/40/5/20`)으로 일괄 보정
  - 참고: 사용자 커스텀 값은 유지하고 레거시/비정상값만 업데이트
- [ ] [Database/TradeLogging_Schema.sql](Database/TradeLogging_Schema.sql) 실행
  - 목적: `TradeLogs`/`TradeHistory`의 `UserId` 컬럼 및 인덱스/뷰 정합화

### 2-1) 터미널 실행본 (PowerShell + sqlcmd)

아래 스크립트 1회 실행으로 **적용 + 검증 쿼리**까지 연속 수행됩니다.

- 실행 스크립트: [run-user-scope-db-migration.ps1](run-user-scope-db-migration.ps1)

Windows 인증:

```powershell
./run-user-scope-db-migration.ps1 -ServerInstance "localhost" -Database "TradingBot"
```

SQL 인증:

```powershell
./run-user-scope-db-migration.ps1 -ServerInstance "localhost" -Database "TradingBot" -UseSqlAuth -SqlUser "sa" -SqlPassword "<PASSWORD>"
```

실패 시 즉시 중단(`-b`)되며, 성공 시 체크리스트 3번의 핵심 검증 쿼리 결과가 함께 출력됩니다.

## 3) 스키마 검증 쿼리

- [ ] `UserId` 컬럼 확인

```sql
SELECT
    t.name AS TableName,
    c.name AS ColumnName
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
WHERE t.name IN ('TradeLogs', 'TradeHistory', 'FundTransferLog', 'PortfolioRebalancingLog', 'RebalancingAction', 'ArbitrageExecutionLog')
  AND c.name = 'UserId'
ORDER BY t.name;
```

- [ ] 인덱스 확인

```sql
SELECT name, object_name(object_id) AS TableName
FROM sys.indexes
WHERE name IN ('IX_TradeLogs_UserId_Time', 'IX_TradeHistory_UserId_ExitTime', 'IX_TradeHistory_UserId_EntryTime')
ORDER BY TableName, name;
```

- [ ] `GeneralSettings` FK 확인 (`Users.Id`)

```sql
SELECT fk.name, OBJECT_NAME(fk.parent_object_id) AS TableName, OBJECT_NAME(fk.referenced_object_id) AS RefTable
FROM sys.foreign_keys fk
WHERE fk.name = 'FK_GeneralSettings_Users_Id';
```

- [ ] `GeneralSettings` 신규 PUMP 튜닝 컬럼 확인

```sql
SELECT c.name AS ColumnName, t.name AS DataType, c.precision, c.scale, c.is_nullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.GeneralSettings')
  AND c.name IN ('PumpFirstTakeProfitRatioPct', 'PumpStairStep1Roe', 'PumpStairStep2Roe', 'PumpStairStep3Roe')
ORDER BY c.column_id;
```

## 4) 앱 동작 스모크 테스트

- [ ] 사용자 A 로그인 → 거래/로그/히스토리 조회
- [ ] 사용자 B 로그인 → 사용자 A 데이터가 섞여 보이지 않는지 확인
- [ ] 시뮬레이션 모드에서 ROI/UI 값 이상치 없는지 확인
- [ ] 앱 로그에 `UserId 확인 실패` 경고가 반복되지 않는지 확인
- [ ] 설정창 General 탭에서 PUMP 튜닝 4개 항목 저장 후 재오픈 시 값 유지 확인

## 5) 운영 검증 포인트 (필수)

- [ ] 블랙리스트 복구가 현재 로그인 사용자 기준으로만 동작하는지 확인
- [ ] 최근 TradeLogs/TradeHistory 신규 데이터에 `UserId`가 채워지는지 확인

```sql
SELECT TOP 20 Id, UserId, Symbol, [Time]
FROM dbo.TradeLogs
ORDER BY Id DESC;

SELECT TOP 20 Id, UserId, Symbol, EntryTime, ExitTime, IsClosed
FROM dbo.TradeHistory
ORDER BY Id DESC;
```

## 6) 문제 발생 시 롤백

- [ ] 앱 즉시 중지
- [ ] 백업 DB 복원
- [ ] 복원 후 앱 재기동
- [ ] 롤백 시점/원인 기록

## 7) 완료 체크

- [ ] 운영 적용 완료 시각 기록
- [ ] 적용자/검증자 서명(이름)
- [ ] 다음 배포 노트에 반영
