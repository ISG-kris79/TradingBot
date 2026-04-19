using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace TradingBot
{
    /// <summary>
    /// [v5.10.58] Stored Procedure 자동 등록 모듈
    /// 앱 시작 시 CREATE OR ALTER PROCEDURE로 SP를 DB에 배포 → 코드와 DB 스키마 동기화 자동 유지.
    /// 단계별로 SP 추가해가며 Dapper → ADO.NET + SP 전환.
    /// </summary>
    public static class DbProcedures
    {
        /// <summary>
        /// 모든 SP를 DB에 등록 (CREATE OR ALTER).
        /// DbManager 초기화 시 1회 호출.
        /// </summary>
        public static async Task EnsureAllAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return;

            var scripts = new List<(string Name, string Body)>
            {
                ("sp_SavePositionState",       sp_SavePositionState),
                ("sp_GetOpenTimeAcrossTables", sp_GetOpenTimeAcrossTables),
                ("sp_BulkPreloadOpenTime",     sp_BulkPreloadOpenTime),
            };

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            foreach (var (name, body) in scripts)
            {
                try
                {
                    using var cmd = new SqlCommand(body, conn) { CommandTimeout = 30 };
                    await cmd.ExecuteNonQueryAsync();
                    MainWindow.Instance?.AddLog($"[SP] ✅ {name} 등록 완료");
                }
                catch (Exception ex)
                {
                    MainWindow.Instance?.AddLog($"⚠️ [SP] {name} 등록 실패: {ex.Message}");
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // sp_SavePositionState — PositionState UPSERT (MERGE)
        // 고빈도 호출 (포지션 모니터 tick마다). 기존 commandTimeout=8 fast-fail 유지.
        // ════════════════════════════════════════════════════════════════════
        private const string sp_SavePositionState = @"
CREATE OR ALTER PROCEDURE dbo.sp_SavePositionState
    @UserId               INT,
    @Symbol               NVARCHAR(32),
    @TakeProfitStep       INT,
    @PartialProfitStage   INT,
    @BreakevenPrice       DECIMAL(18,8),
    @HighestROE           DECIMAL(18,4),
    @StairStep            INT,
    @IsBreakEvenTriggered BIT,
    @HighestPrice         DECIMAL(18,8),
    @LowestPrice          DECIMAL(18,8),
    @IsPumpStrategy       BIT
AS
BEGIN
    SET NOCOUNT ON;

    MERGE dbo.PositionState WITH (HOLDLOCK) AS target
    USING (SELECT @UserId AS UserId, @Symbol AS Symbol) AS source
    ON target.UserId = source.UserId AND target.Symbol = source.Symbol
    WHEN MATCHED THEN
        UPDATE SET
            TakeProfitStep       = CASE WHEN @TakeProfitStep > 0 THEN @TakeProfitStep ELSE target.TakeProfitStep END,
            PartialProfitStage   = CASE WHEN @PartialProfitStage > 0 THEN @PartialProfitStage ELSE target.PartialProfitStage END,
            BreakevenPrice       = CASE WHEN @BreakevenPrice > 0 THEN @BreakevenPrice ELSE target.BreakevenPrice END,
            HighestROE           = CASE WHEN @HighestROE > target.HighestROE THEN @HighestROE ELSE target.HighestROE END,
            StairStep            = CASE WHEN @StairStep > target.StairStep THEN @StairStep ELSE target.StairStep END,
            IsBreakEvenTriggered = CASE WHEN @IsBreakEvenTriggered = 1 THEN 1 ELSE target.IsBreakEvenTriggered END,
            HighestPrice         = CASE WHEN @HighestPrice > target.HighestPrice THEN @HighestPrice ELSE target.HighestPrice END,
            LowestPrice          = CASE WHEN @LowestPrice > 0 AND (target.LowestPrice = 0 OR @LowestPrice < target.LowestPrice) THEN @LowestPrice ELSE target.LowestPrice END,
            IsPumpStrategy       = @IsPumpStrategy,
            LastUpdatedAt        = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (UserId, Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice,
                HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy)
        VALUES (@UserId, @Symbol, @TakeProfitStep, @PartialProfitStage, @BreakevenPrice,
                @HighestROE, @StairStep, @IsBreakEvenTriggered, @HighestPrice, @LowestPrice, @IsPumpStrategy);
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetOpenTimeAcrossTables — 단일 심볼의 4개 테이블 OpenTime 집계
        // DBNull/MinValue 방어는 호출 측 SqlDataReader에서 수행.
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetOpenTimeAcrossTables = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetOpenTimeAcrossTables
    @Symbol   NVARCHAR(32),
    @Interval NVARCHAR(8)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.MarketCandles WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS mc,
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleData    WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS cd,
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleHistory WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS ch,
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.MarketData    WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS md;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_BulkPreloadOpenTime — 전체 심볼 OpenTime 일괄 로드 (봇 시작 시 1회)
        // 테이블 존재 여부 체크 후 UNION ALL — 테이블 미존재로 인한 전체 쿼리 실패 방지.
        // ════════════════════════════════════════════════════════════════════
        private const string sp_BulkPreloadOpenTime = @"
CREATE OR ALTER PROCEDURE dbo.sp_BulkPreloadOpenTime
    @Interval NVARCHAR(8)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @hasMC BIT = CASE WHEN OBJECT_ID('dbo.MarketCandles',  'U') IS NOT NULL THEN 1 ELSE 0 END;
    DECLARE @hasCD BIT = CASE WHEN OBJECT_ID('dbo.CandleData',     'U') IS NOT NULL THEN 1 ELSE 0 END;
    DECLARE @hasCH BIT = CASE WHEN OBJECT_ID('dbo.CandleHistory',  'U') IS NOT NULL THEN 1 ELSE 0 END;
    DECLARE @hasMD BIT = CASE WHEN OBJECT_ID('dbo.MarketData',     'U') IS NOT NULL THEN 1 ELSE 0 END;

    DECLARE @sql NVARCHAR(MAX) = N'';

    IF @hasMC = 1
        SET @sql = @sql + N'SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM dbo.MarketCandles WITH (NOLOCK) WHERE Symbol IS NOT NULL AND OpenTime > ''1800-01-01'' GROUP BY Symbol' + CHAR(10);
    IF @hasCD = 1
    BEGIN
        IF LEN(@sql) > 0 SET @sql = @sql + N'UNION ALL' + CHAR(10);
        SET @sql = @sql + N'SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM dbo.CandleData WITH (NOLOCK) WHERE Symbol IS NOT NULL AND OpenTime > ''1800-01-01'' GROUP BY Symbol' + CHAR(10);
    END
    IF @hasCH = 1
    BEGIN
        IF LEN(@sql) > 0 SET @sql = @sql + N'UNION ALL' + CHAR(10);
        SET @sql = @sql + N'SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM dbo.CandleHistory WITH (NOLOCK) WHERE [Interval] = @Interval AND OpenTime > ''1800-01-01'' GROUP BY Symbol' + CHAR(10);
    END
    IF @hasMD = 1
    BEGIN
        IF LEN(@sql) > 0 SET @sql = @sql + N'UNION ALL' + CHAR(10);
        SET @sql = @sql + N'SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM dbo.MarketData WITH (NOLOCK) WHERE [Interval] = @Interval AND OpenTime > ''1800-01-01'' GROUP BY Symbol' + CHAR(10);
    END

    IF LEN(@sql) = 0
    BEGIN
        SELECT TOP 0 CAST(NULL AS NVARCHAR(32)) AS Symbol, CAST(NULL AS DATETIME2(7)) AS MaxOT;
        RETURN;
    END

    EXEC sp_executesql @sql, N'@Interval NVARCHAR(8)', @Interval = @Interval;
END";
    }
}
