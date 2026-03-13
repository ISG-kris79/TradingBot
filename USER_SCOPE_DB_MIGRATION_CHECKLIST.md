# 사용자별 DB 스코프 마이그레이션 체크리스트

## 1) 사전 준비

- [ ] 적용 시간 공지 (봇/운영자)
- [ ] 봇 정지 (WPF 앱 + 백그라운드 프로세스)
- [ ] 운영 DB 백업 완료
- [ ] 연결 대상 DB가 맞는지 확인 (`SELECT DB_NAME();`)

## 2) 스키마 적용 순서 (권장)

- [ ] [Database/GeneralSettings_Schema.sql](Database/GeneralSettings_Schema.sql) 실행
  - 목적: `GeneralSettings` FK를 `Users(Id)` 기준으로 정합화
- [ ] [Database/TradeLogging_Schema.sql](Database/TradeLogging_Schema.sql) 실행
  - 목적: `TradeLogs`/`TradeHistory`의 `UserId` 컬럼 및 인덱스/뷰 정합화

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

## 4) 앱 동작 스모크 테스트

- [ ] 사용자 A 로그인 → 거래/로그/히스토리 조회
- [ ] 사용자 B 로그인 → 사용자 A 데이터가 섞여 보이지 않는지 확인
- [ ] 시뮬레이션 모드에서 ROI/UI 값 이상치 없는지 확인
- [ ] 앱 로그에 `UserId 확인 실패` 경고가 반복되지 않는지 확인

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
