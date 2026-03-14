-- =====================================================
-- GeneralSettings 메이저 기본값 일괄 보정 스크립트 (2026-03-14)
-- 목적: 운영 DB에 남아있는 과거 기본값을 최신 메이저 운용값으로 정규화
-- 대상: dbo.GeneralSettings
--
-- 보정 대상 (레거시 -> 최신)
--   MajorTp1Roe:           15   -> 20
--   MajorTp2Roe:           25   -> 40
--   MajorTrailingStartRoe: 20/22-> 40
--   MajorTrailingGapRoe:   4    -> 5
--   MajorStopLossRoe:      60   -> 20
--
-- 주의:
-- - 사용자 커스텀 값은 최대한 보존하기 위해 '레거시 기본값/비정상값'만 업데이트합니다.
-- - 여러 번 실행해도 안전합니다(idempotent).
-- =====================================================

SET NOCOUNT ON;

IF OBJECT_ID('dbo.GeneralSettings', 'U') IS NULL
BEGIN
    RAISERROR('dbo.GeneralSettings 테이블이 존재하지 않습니다. 먼저 Database/GeneralSettings_Schema.sql을 적용하세요.', 16, 1);
    RETURN;
END;

IF COL_LENGTH('dbo.GeneralSettings', 'MajorTp1Roe') IS NULL
   OR COL_LENGTH('dbo.GeneralSettings', 'MajorTp2Roe') IS NULL
   OR COL_LENGTH('dbo.GeneralSettings', 'MajorTrailingStartRoe') IS NULL
   OR COL_LENGTH('dbo.GeneralSettings', 'MajorTrailingGapRoe') IS NULL
   OR COL_LENGTH('dbo.GeneralSettings', 'MajorStopLossRoe') IS NULL
BEGIN
    RAISERROR('필수 메이저 컬럼이 없습니다. Database/GeneralSettings_Schema.sql을 먼저 실행하세요.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRAN;

    PRINT '🔎 GeneralSettings 메이저 레거시 값 점검 중...';

    SELECT
        COUNT(*) AS TotalRows,
        SUM(CASE WHEN MajorTp1Roe IS NULL OR MajorTp1Roe <= 0 OR MajorTp1Roe = 15.0 THEN 1 ELSE 0 END) AS NeedFix_MajorTp1Roe,
        SUM(CASE WHEN MajorTp2Roe IS NULL OR MajorTp2Roe <= 0 OR MajorTp2Roe = 25.0 THEN 1 ELSE 0 END) AS NeedFix_MajorTp2Roe,
        SUM(CASE WHEN MajorTrailingStartRoe IS NULL OR MajorTrailingStartRoe <= 0 OR MajorTrailingStartRoe IN (20.0, 22.0) THEN 1 ELSE 0 END) AS NeedFix_MajorTrailingStartRoe,
        SUM(CASE WHEN MajorTrailingGapRoe IS NULL OR MajorTrailingGapRoe <= 0 OR MajorTrailingGapRoe = 4.0 THEN 1 ELSE 0 END) AS NeedFix_MajorTrailingGapRoe,
        SUM(CASE WHEN MajorStopLossRoe IS NULL OR MajorStopLossRoe <= 0 OR MajorStopLossRoe = 60.0 THEN 1 ELSE 0 END) AS NeedFix_MajorStopLossRoe
    FROM dbo.GeneralSettings;

    UPDATE dbo.GeneralSettings
    SET
        MajorTp1Roe = CASE
            WHEN MajorTp1Roe IS NULL OR MajorTp1Roe <= 0 OR MajorTp1Roe = 15.0 THEN 20.0
            ELSE MajorTp1Roe
        END,
        MajorTp2Roe = CASE
            WHEN MajorTp2Roe IS NULL OR MajorTp2Roe <= 0 OR MajorTp2Roe = 25.0 THEN 40.0
            ELSE MajorTp2Roe
        END,
        MajorTrailingStartRoe = CASE
            WHEN MajorTrailingStartRoe IS NULL OR MajorTrailingStartRoe <= 0 OR MajorTrailingStartRoe IN (20.0, 22.0) THEN 40.0
            ELSE MajorTrailingStartRoe
        END,
        MajorTrailingGapRoe = CASE
            WHEN MajorTrailingGapRoe IS NULL OR MajorTrailingGapRoe <= 0 OR MajorTrailingGapRoe = 4.0 THEN 5.0
            ELSE MajorTrailingGapRoe
        END,
        MajorStopLossRoe = CASE
            WHEN MajorStopLossRoe IS NULL OR MajorStopLossRoe <= 0 OR MajorStopLossRoe = 60.0 THEN 20.0
            ELSE MajorStopLossRoe
        END,
        UpdatedAt = CASE
            WHEN
                (MajorTp1Roe IS NULL OR MajorTp1Roe <= 0 OR MajorTp1Roe = 15.0)
                OR (MajorTp2Roe IS NULL OR MajorTp2Roe <= 0 OR MajorTp2Roe = 25.0)
                OR (MajorTrailingStartRoe IS NULL OR MajorTrailingStartRoe <= 0 OR MajorTrailingStartRoe IN (20.0, 22.0))
                OR (MajorTrailingGapRoe IS NULL OR MajorTrailingGapRoe <= 0 OR MajorTrailingGapRoe = 4.0)
                OR (MajorStopLossRoe IS NULL OR MajorStopLossRoe <= 0 OR MajorStopLossRoe = 60.0)
            THEN GETUTCDATE()
            ELSE UpdatedAt
        END
    WHERE
        (MajorTp1Roe IS NULL OR MajorTp1Roe <= 0 OR MajorTp1Roe = 15.0)
        OR (MajorTp2Roe IS NULL OR MajorTp2Roe <= 0 OR MajorTp2Roe = 25.0)
        OR (MajorTrailingStartRoe IS NULL OR MajorTrailingStartRoe <= 0 OR MajorTrailingStartRoe IN (20.0, 22.0))
        OR (MajorTrailingGapRoe IS NULL OR MajorTrailingGapRoe <= 0 OR MajorTrailingGapRoe = 4.0)
        OR (MajorStopLossRoe IS NULL OR MajorStopLossRoe <= 0 OR MajorStopLossRoe = 60.0);

    DECLARE @UpdatedRows INT = @@ROWCOUNT;
    PRINT CONCAT('✅ 보정 완료: ', @UpdatedRows, ' row(s) updated');

    COMMIT TRAN;

    PRINT '📊 보정 후 분포 확인';
    SELECT
        MajorTp1Roe,
        MajorTp2Roe,
        MajorTrailingStartRoe,
        MajorTrailingGapRoe,
        MajorStopLossRoe,
        COUNT(*) AS Cnt
    FROM dbo.GeneralSettings
    GROUP BY
        MajorTp1Roe,
        MajorTp2Roe,
        MajorTrailingStartRoe,
        MajorTrailingGapRoe,
        MajorStopLossRoe
    ORDER BY Cnt DESC;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRAN;

    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrNo INT = ERROR_NUMBER();
    DECLARE @ErrLine INT = ERROR_LINE();

    RAISERROR('GeneralSettings 메이저 보정 실패 (Error %d, Line %d): %s', 16, 1, @ErrNo, @ErrLine, @ErrMsg);
END CATCH;
