# Database Setup Guide (Phase 14 고급 기능)

## 📋 개요

Phase 14 고급 거래 기능(차익거래, 자금 이동, 포트폴리오 리밸런싱)을 위한 데이터베이스 스키마 설치 가이드입니다.

## ⚠️ 사전 요구사항

- SQL Server 2019 이상 설치
- 데이터베이스 생성 권한
- 기존 TradingBot 데이터베이스 백업 완료

## 🚀 설치 단계

### 1. 데이터베이스 연결 확인

```sql
-- SQL Server Management Studio (SSMS)에서 실행
USE TradingBot;
GO

-- 연결 테스트
SELECT @@VERSION;
```

### 2. Phase 14 스키마 실행

```powershell
# PowerShell에서 실행 (경로 확인 필수)
cd E:\PROJECT\CoinFF\TradingBot\Database

sqlcmd -S localhost -d TradingBot -i Phase14_AdvancedFeatures_Schema.sql -E
```

**또는 SSMS에서 직접 실행:**

1. SSMS 열기
2. `File` → `Open` → `File...`
3. `Phase14_AdvancedFeatures_Schema.sql` 선택
4. `Execute (F5)` 클릭

### 3. 설치 확인

```sql
-- 테이블 생성 확인
SELECT TABLE_NAME 
FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_NAME IN (
    'ArbitrageExecutionLog',
    'FundTransferLog', 
    'PortfolioRebalancingLog',
    'RebalancingAction'
);
-- 결과: 4개 테이블 표시

-- 통계 뷰 확인
SELECT TABLE_NAME 
FROM INFORMATION_SCHEMA.VIEWS 
WHERE TABLE_NAME LIKE 'vw_%Statistics';
-- 결과: 3개 뷰 표시

-- 인덱스 확인
SELECT 
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
WHERE t.name IN (
    'ArbitrageExecutionLog',
    'FundTransferLog',
    'PortfolioRebalancingLog',
    'RebalancingAction'
)
AND i.name IS NOT NULL
ORDER BY t.name, i.name;
```

## 📊 생성되는 객체

### 테이블 (4개)

1. **ArbitrageExecutionLog**: 차익거래 실행 로그
   - 심볼, 거래소, 가격, 수익률, 성공 여부
   - 17개 컬럼

2. **FundTransferLog**: 자금 이동 로그
   - 출발/도착 거래소, 자산, 수량, 트랜잭션 ID
   - 14개 컬럼

3. **PortfolioRebalancingLog**: 리밸런싱 로그
   - 총 가치, 액션 개수, 성공 여부
   - 7개 컬럼

4. **RebalancingAction**: 리밸런싱 액션 상세
   - 부모-자식 관계 (FK → PortfolioRebalancingLog)
   - 11개 컬럼

### 인덱스 (7개)

- `IX_ArbitrageExecutionLog_Symbol_CreatedAt`
- `IX_ArbitrageExecutionLog_Success`
- `IX_FundTransferLog_FromExchange_ToExchange`
- `IX_FundTransferLog_CreatedAt`
- `IX_PortfolioRebalancingLog_CreatedAt`
- `IX_RebalancingAction_RebalancingLogId`
- `IX_RebalancingAction_Asset`

### 뷰 (3개)

1. **vw_ArbitrageStatistics**: 차익거래 통계
   - 총 실행 횟수, 성공률, 평균/총 수익

2. **vw_FundTransferStatistics**: 자금 이동 통계
   - 총 이동 횟수, 성공률, 총 이동 금액

3. **vw_RebalancingStatistics**: 리밸런싱 통계
   - 총 실행 횟수, 성공률, 평균 액션 개수

## 🔍 샘플 쿼리

### 최근 차익거래 실행 내역

```sql
SELECT TOP 10
    Symbol,
    BuyExchange,
    SellExchange,
    BuyPrice,
    SellPrice,
    ProfitPercent,
    Success,
    CreatedAt
FROM ArbitrageExecutionLog
ORDER BY CreatedAt DESC;
```

### 차익거래 통계 조회

```sql
SELECT * FROM vw_ArbitrageStatistics;
```

### 최근 자금 이동 내역

```sql
SELECT TOP 10
    FromExchange,
    ToExchange,
    Asset,
    Amount,
    WithdrawSuccess,
    DepositSuccess,
    CreatedAt
FROM FundTransferLog
ORDER BY CreatedAt DESC;
```

### 리밸런싱 상세 내역 (액션 포함)

```sql
SELECT 
    r.Id AS RebalancingId,
    r.TotalValue,
    r.ActionCount,
    r.CreatedAt,
    a.Asset,
    a.Action,
    a.CurrentQuantity,
    a.TargetQuantity,
    a.CurrentPrice
FROM PortfolioRebalancingLog r
LEFT JOIN RebalancingAction a ON r.Id = a.RebalancingLogId
ORDER BY r.CreatedAt DESC, a.Asset;
```

## 🛡️ 안전성 체크

### 데이터 무결성

```sql
-- Foreign Key 확인
SELECT 
    fk.name AS ForeignKey,
    OBJECT_NAME(fk.parent_object_id) AS TableName,
    COL_NAME(fc.parent_object_id, fc.parent_column_id) AS ColumnName,
    OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable
FROM sys.foreign_keys AS fk
INNER JOIN sys.foreign_key_columns AS fc 
    ON fk.object_id = fc.constraint_object_id
WHERE OBJECT_NAME(fk.parent_object_id) = 'RebalancingAction';
```

### 용량 모니터링

```sql
-- 테이블 행 수 및 용량
SELECT 
    t.name AS TableName,
    p.rows AS RowCount,
    SUM(a.total_pages) * 8 AS TotalSpaceKB,
    SUM(a.used_pages) * 8 AS UsedSpaceKB
FROM sys.tables t
INNER JOIN sys.indexes i ON t.object_id = i.object_id
INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE t.name IN (
    'ArbitrageExecutionLog',
    'FundTransferLog',
    'PortfolioRebalancingLog',
    'RebalancingAction'
)
GROUP BY t.name, p.rows
ORDER BY TotalSpaceKB DESC;
```

## 🔄 롤백 (필요 시)

```sql
-- ⚠️ 주의: 모든 Phase 14 데이터가 삭제됩니다!
DROP VIEW IF EXISTS vw_RebalancingStatistics;
DROP VIEW IF EXISTS vw_FundTransferStatistics;
DROP VIEW IF EXISTS vw_ArbitrageStatistics;
DROP TABLE IF EXISTS RebalancingAction;
DROP TABLE IF EXISTS PortfolioRebalancingLog;
DROP TABLE IF EXISTS FundTransferLog;
DROP TABLE IF EXISTS ArbitrageExecutionLog;
```

## 📌 문제 해결

### 오류: "Database 'TradingBot' does not exist"

```sql
-- 데이터베이스 생성
CREATE DATABASE TradingBot;
GO
```

### 오류: "Object already exists"

- 스크립트에 `IF NOT EXISTS` 가드가 포함되어 있어 재실행해도 안전함
- 기존 데이터는 유지됨

### 권한 오류

```sql
-- 현재 사용자 권한 확인
SELECT 
    USER_NAME() AS CurrentUser,
    HAS_PERMS_BY_NAME('TradingBot', 'DATABASE', 'CREATE TABLE') AS CanCreateTable;

-- 권한 부여 (관리자 계정으로 실행)
USE TradingBot;
GRANT CREATE TABLE TO [YourUsername];
GRANT CREATE VIEW TO [YourUsername];
```

## 📞 지원

- 문제 발생 시: GitHub Issues
- 문서 업데이트: 이 파일 수정 후 PR

---
**최종 업데이트**: 2026-02-28  
**작성자**: TradingBot Development Team
