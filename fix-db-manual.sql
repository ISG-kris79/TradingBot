-- =====================================================
-- [긴급] holdingMinutes 열 안전 재생성 (SSMS에서 실행)
-- 사용법: SQL Server Management Studio에서 실행
-- =====================================================

-- 현재 DB 선택 (필요시 수정)
USE [TradingDB];
GO

PRINT '🔧 holdingMinutes 열 안전 재생성 시작...';
GO

-- 1단계: holdingMinutes 계산 열 제거
IF EXISTS (
    SELECT 1 
    FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') 
      AND name = 'holdingMinutes'
      AND is_computed = 1
)
BEGIN
    PRINT '   - holdingMinutes 계산 열 발견, 제거 중...';
    ALTER TABLE dbo.TradeHistory DROP COLUMN holdingMinutes;
    PRINT '   ✅ holdingMinutes 계산 열 제거 완료';
END
ELSE IF EXISTS (
    SELECT 1 
    FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') 
      AND name = 'holdingMinutes'
)
BEGIN
    PRINT '   - holdingMinutes 일반 열 발견, 제거 중...';
    ALTER TABLE dbo.TradeHistory DROP COLUMN holdingMinutes;
    PRINT '   ✅ holdingMinutes 일반 열 제거 완료';
END
ELSE
BEGIN
    PRINT '   ℹ️ holdingMinutes 열이 존재하지 않음';
END
GO

-- 2단계: HoldingMinutes (대문자) 변형도 확인
IF EXISTS (
    SELECT 1 
    FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') 
      AND name = 'HoldingMinutes'
)
BEGIN
    PRINT '   - HoldingMinutes 열 발견, 제거 중...';
    ALTER TABLE dbo.TradeHistory DROP COLUMN HoldingMinutes;
    PRINT '   ✅ HoldingMinutes 열 제거 완료';
END
GO

-- 3단계: NULL 안전 계산 열 생성
IF NOT EXISTS (
    SELECT 1 
    FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') 
      AND name = 'holdingMinutes'
)
BEGIN
    PRINT '   - holdingMinutes 계산 열 생성 중 (NULL 안전)...';
    ALTER TABLE dbo.TradeHistory ADD holdingMinutes AS 
        CASE WHEN ExitTime IS NOT NULL 
             THEN DATEDIFF(MINUTE, EntryTime, ExitTime) 
             ELSE NULL 
        END;
    PRINT '   ✅ holdingMinutes 계산 열 생성 완료';
END
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
    is_computed AS IsComputed
FROM sys.columns
WHERE object_id = OBJECT_ID('dbo.TradeHistory')
  AND name IN ('ExitTime', 'holdingMinutes', 'HoldingMinutes')
ORDER BY column_id;
GO

PRINT '';
PRINT '✅ 마이그레이션 완료!';
PRINT '   - ExitTime이 NULL이면 holdingMinutes도 NULL';
PRINT '   - ExitTime이 있으면 자동으로 분 단위 보유 시간 계산';
PRINT 'TradingBot을 재시작하세요.';
GO
