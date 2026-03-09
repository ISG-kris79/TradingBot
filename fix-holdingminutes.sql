-- =====================================================
-- [긴급] holdingMinutes 계산 열 제거 스크립트
-- 문제: ExitTime이 NULL인 상태로 INSERT 시도 시 오류 발생
-- 해결: 계산 열을 제거하고 필요 시 쿼리에서 DATEDIFF 사용
-- =====================================================

USE TradingDB;
GO

PRINT '🔧 holdingMinutes 계산 열 제거 시작...';

-- 1단계: 계산 열인 경우 제거
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
    PRINT '   ℹ️ holdingMinutes 열이 존재하지 않음 (이미 제거됨)';
END

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

-- 3단계: ExitTime이 NULL 허용인지 확인
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
    PRINT '   ℹ️ ExitTime은 이미 NULL 허용으로 설정되어 있음';
END

PRINT '';
PRINT '✅ 마이그레이션 완료!';
PRINT '   이제 진입 시 ExitTime=NULL인 TradeHistory 레코드를 INSERT할 수 있습니다.';
PRINT '   보유 시간이 필요한 경우 쿼리에서 다음과 같이 계산하세요:';
PRINT '   SELECT DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldingMinutes FROM TradeHistory WHERE ExitTime IS NOT NULL;';
GO
