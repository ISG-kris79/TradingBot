-- =====================================================
-- GeneralSettings 마이그레이션
-- 목적: PUMP 1차 익절 비중/계단식 ROE 컬럼 추가
-- 대상: MSSQL (dbo.GeneralSettings)
-- 안전성: idempotent (여러 번 실행 가능)
-- =====================================================

SET NOCOUNT ON;

IF OBJECT_ID('dbo.GeneralSettings', 'U') IS NULL
BEGIN
    RAISERROR('dbo.GeneralSettings 테이블이 존재하지 않습니다. 먼저 Database/GeneralSettings_Schema.sql을 적용하세요.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRAN;

    -- 1) 1차 부분익절 비중(%)
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID('dbo.GeneralSettings')
          AND name = 'PumpFirstTakeProfitRatioPct'
    )
    BEGIN
        ALTER TABLE dbo.GeneralSettings
        ADD PumpFirstTakeProfitRatioPct DECIMAL(18, 4) NOT NULL
            CONSTRAINT DF_GeneralSettings_PumpFirstTakeProfitRatioPct DEFAULT (15.0) WITH VALUES;

        PRINT '✅ Added: PumpFirstTakeProfitRatioPct (default 15.0)';
    END
    ELSE
    BEGIN
        PRINT 'ℹ️ Exists: PumpFirstTakeProfitRatioPct';
    END;

    -- 2) 계단식 1단계 트리거 ROE
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID('dbo.GeneralSettings')
          AND name = 'PumpStairStep1Roe'
    )
    BEGIN
        ALTER TABLE dbo.GeneralSettings
        ADD PumpStairStep1Roe DECIMAL(18, 4) NOT NULL
            CONSTRAINT DF_GeneralSettings_PumpStairStep1Roe DEFAULT (50.0) WITH VALUES;

        PRINT '✅ Added: PumpStairStep1Roe (default 50.0)';
    END
    ELSE
    BEGIN
        PRINT 'ℹ️ Exists: PumpStairStep1Roe';
    END;

    -- 3) 계단식 2단계 트리거 ROE
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID('dbo.GeneralSettings')
          AND name = 'PumpStairStep2Roe'
    )
    BEGIN
        ALTER TABLE dbo.GeneralSettings
        ADD PumpStairStep2Roe DECIMAL(18, 4) NOT NULL
            CONSTRAINT DF_GeneralSettings_PumpStairStep2Roe DEFAULT (100.0) WITH VALUES;

        PRINT '✅ Added: PumpStairStep2Roe (default 100.0)';
    END
    ELSE
    BEGIN
        PRINT 'ℹ️ Exists: PumpStairStep2Roe';
    END;

    -- 4) 계단식 3단계 트리거 ROE
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID('dbo.GeneralSettings')
          AND name = 'PumpStairStep3Roe'
    )
    BEGIN
        ALTER TABLE dbo.GeneralSettings
        ADD PumpStairStep3Roe DECIMAL(18, 4) NOT NULL
            CONSTRAINT DF_GeneralSettings_PumpStairStep3Roe DEFAULT (200.0) WITH VALUES;

        PRINT '✅ Added: PumpStairStep3Roe (default 200.0)';
    END
    ELSE
    BEGIN
        PRINT 'ℹ️ Exists: PumpStairStep3Roe';
    END;

    COMMIT TRAN;
    PRINT '🎉 GeneralSettings PUMP 튜닝 컬럼 마이그레이션 완료';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRAN;

    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrNo INT = ERROR_NUMBER();
    DECLARE @ErrLine INT = ERROR_LINE();

    RAISERROR('GeneralSettings 마이그레이션 실패 (Error %d, Line %d): %s', 16, 1, @ErrNo, @ErrLine, @ErrMsg);
END CATCH;

-- 검증
SELECT
    c.name AS ColumnName,
    t.name AS DataType,
    c.precision,
    c.scale,
    c.is_nullable,
    dc.definition AS DefaultDefinition
FROM sys.columns c
JOIN sys.types t
  ON c.user_type_id = t.user_type_id
LEFT JOIN sys.default_constraints dc
  ON c.default_object_id = dc.object_id
WHERE c.object_id = OBJECT_ID('dbo.GeneralSettings')
  AND c.name IN (
      'PumpFirstTakeProfitRatioPct',
      'PumpStairStep1Roe',
      'PumpStairStep2Roe',
      'PumpStairStep3Roe'
  )
ORDER BY c.column_id;
