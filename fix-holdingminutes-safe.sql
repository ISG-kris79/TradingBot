-- =====================================================
-- holdingMinutes 계산 열 안전 재생성
-- =====================================================

USE [TradingDB];
GO

PRINT '🔧 holdingMinutes 열 재생성 시작...';
GO

-- 1단계: 기존 열 제거 (있다면)
IF EXISTS (
    SELECT 1 
    FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') 
      AND name = 'holdingMinutes'
)
BEGIN
    PRINT '   - 기존 holdingMinutes 열 발견, 제거 중...';
    ALTER TABLE dbo.TradeHistory DROP COLUMN holdingMinutes;
    PRINT '   ✅ holdingMinutes 열 제거 완료';
END
ELSE
BEGIN
    PRINT '   ℹ️ holdingMinutes 열 없음 (신규 생성 진행)';
END
GO

-- 2단계: HoldingMinutes (대문자) 변형도 확인 및 제거
IF EXISTS (
    SELECT 1 
    FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') 
      AND name = 'HoldingMinutes'
)
BEGIN
    PRINT '   - 기존 HoldingMinutes 열 발견, 제거 중...';
    ALTER TABLE dbo.TradeHistory DROP COLUMN HoldingMinutes;
    PRINT '   ✅ HoldingMinutes 열 제거 완료';
END
GO

-- 3단계: NULL 안전 계산 열 생성
PRINT '   - holdingMinutes 계산 열 생성 중 (NULL 안전)...';
ALTER TABLE dbo.TradeHistory ADD holdingMinutes AS 
    CASE WHEN ExitTime IS NOT NULL 
         THEN DATEDIFF(MINUTE, EntryTime, ExitTime) 
         ELSE NULL 
    END;
PRINT '   ✅ holdingMinutes 계산 열 생성 완료';
GO

-- 4단계: ExitTime NULL 허용 확인
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'ExitTime'
      AND is_nullable = 0
)
BEGIN
    PRINT '   - ExitTime을 NULL 허용으로 변경 중...';
    ALTER TABLE dbo.TradeHistory ALTER COLUMN ExitTime DATETIME2 NULL;
    PRINT '   ✅ ExitTime NULL 허용 설정 완료';
END
ELSE
BEGIN
    PRINT '   ℹ️ ExitTime은 이미 NULL 허용';
END
GO

-- 5단계: 검증
PRINT '';
PRINT '📊 최종 검증:';
SELECT 
    name AS ColumnName,
    TYPE_NAME(user_type_id) AS DataType,
    is_nullable AS AllowNull,
    is_computed AS IsComputed,
    CASE WHEN is_computed = 1 THEN definition ELSE NULL END AS ComputedDefinition
FROM sys.columns c
LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
WHERE c.object_id = OBJECT_ID('dbo.TradeHistory')
  AND c.name IN ('ExitTime', 'holdingMinutes', 'HoldingMinutes')
ORDER BY c.column_id;
GO

-- 6단계: 샘플 데이터 테스트
PRINT '';
PRINT '📝 샘플 데이터 테스트:';
PRINT '   - ExitTime NULL인 레코드에서 holdingMinutes = NULL 확인';
SELECT TOP 3
    Symbol,
    EntryTime,
    ExitTime,
    holdingMinutes,
    IsClosed
FROM dbo.TradeHistory
WHERE ExitTime IS NULL
ORDER BY EntryTime DESC;

PRINT '';
PRINT '   - ExitTime이 있는 레코드에서 holdingMinutes 계산 확인';
SELECT TOP 3
    Symbol,
    EntryTime,
    ExitTime,
    holdingMinutes,
    IsClosed
FROM dbo.TradeHistory
WHERE ExitTime IS NOT NULL
ORDER BY EntryTime DESC;
GO

PRINT '';
PRINT '✅ holdingMinutes 계산 열 재생성 완료!';
PRINT '   - ExitTime이 NULL이면 holdingMinutes도 NULL';
PRINT '   - ExitTime이 있으면 자동으로 분 단위 보유 시간 계산';
GO
