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
                ("sp_LoadGeneralSettings",     sp_LoadGeneralSettings),
                ("sp_SaveGeneralSettings",     sp_SaveGeneralSettings),
                ("sp_GetTodayStatsByCategory", sp_GetTodayStatsByCategory),
                ("sp_LoadPositionStates",      sp_LoadPositionStates),
                ("sp_DeletePositionState",     sp_DeletePositionState),
                ("sp_CompleteTradePatternSnapshot", sp_CompleteTradePatternSnapshot),
                ("sp_SaveTradePatternSnapshot", sp_SaveTradePatternSnapshot),
                ("sp_GetLabeledTradePatternSnapshots", sp_GetLabeledTradePatternSnapshots),
                ("sp_SaveAiTrainingData",      sp_SaveAiTrainingData),
                ("sp_UpsertAiTrainingRun",     sp_UpsertAiTrainingRun),
                ("sp_GetOpenTrades",           sp_GetOpenTrades),
                ("sp_UpsertTradeEntry",        sp_UpsertTradeEntry),
                ("sp_GetTradeLogs",           sp_GetTradeLogs),
                ("sp_GetTradeHistory",        sp_GetTradeHistory),
                ("sp_GetHoldingOverHourStats", sp_GetHoldingOverHourStats),
                ("sp_GetRecentCandleData",    sp_GetRecentCandleData),
                ("sp_GetCandleDataByInterval", sp_GetCandleDataByInterval),
                ("sp_CompleteTrade",          sp_CompleteTrade),
                ("sp_TryCompleteOpenTrade",  sp_TryCompleteOpenTrade),
                // [v5.10.65] 인라인 SQL → SP 전환 (lock 경합 / MERGE 제거)
                ("sp_SaveAiSignalLog",            sp_SaveAiSignalLog),
                ("sp_SaveOrderError",             sp_SaveOrderError),
                ("sp_SaveArbitrageExecutionLog",  sp_SaveArbitrageExecutionLog),
                ("sp_SaveFundTransferLog",        sp_SaveFundTransferLog),
                ("sp_SaveRebalancingLog",         sp_SaveRebalancingLog),
                ("sp_SaveRebalancingAction",      sp_SaveRebalancingAction),
                ("sp_GetRecentlyClosedPositions", sp_GetRecentlyClosedPositions),
                ("sp_UpsertElliottWaveAnchor",    sp_UpsertElliottWaveAnchor),
                ("sp_LoadElliottWaveAnchors",     sp_LoadElliottWaveAnchors),
                ("sp_DeleteElliottWaveAnchor",    sp_DeleteElliottWaveAnchor),
                ("sp_ResolveCloseSymbol",         sp_ResolveCloseSymbol),
                ("sp_MirrorToTradeLogs",          sp_MirrorToTradeLogs),
                ("sp_EnsureOpenTradeForPosition", sp_EnsureOpenTradeForPosition),
                ("sp_RecordPartialClose",         sp_RecordPartialClose),
                ("sp_InsertClosedTrade",          sp_InsertClosedTrade),
                ("sp_GetAllCandleDataForTraining", sp_GetAllCandleDataForTraining),
                ("sp_GetBulkCandleData",          sp_GetBulkCandleData),
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
        // sp_SavePositionState — PositionState UPSERT (UPDATE → INSERT)
        // 고빈도 호출 (포지션 모니터 tick마다). commandTimeout=8 fast-fail 유지.
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

    UPDATE dbo.PositionState WITH (UPDLOCK, HOLDLOCK)
    SET
        TakeProfitStep       = CASE WHEN @TakeProfitStep > 0 THEN @TakeProfitStep ELSE TakeProfitStep END,
        PartialProfitStage   = CASE WHEN @PartialProfitStage > 0 THEN @PartialProfitStage ELSE PartialProfitStage END,
        BreakevenPrice       = CASE WHEN @BreakevenPrice > 0 THEN @BreakevenPrice ELSE BreakevenPrice END,
        HighestROE           = CASE WHEN @HighestROE > HighestROE THEN @HighestROE ELSE HighestROE END,
        StairStep            = CASE WHEN @StairStep > StairStep THEN @StairStep ELSE StairStep END,
        IsBreakEvenTriggered = CASE WHEN @IsBreakEvenTriggered = 1 THEN 1 ELSE IsBreakEvenTriggered END,
        HighestPrice         = CASE WHEN @HighestPrice > HighestPrice THEN @HighestPrice ELSE HighestPrice END,
        LowestPrice          = CASE WHEN @LowestPrice > 0 AND (LowestPrice = 0 OR @LowestPrice < LowestPrice) THEN @LowestPrice ELSE LowestPrice END,
        IsPumpStrategy       = @IsPumpStrategy,
        LastUpdatedAt        = SYSDATETIME()
    WHERE UserId = @UserId AND Symbol = @Symbol;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO dbo.PositionState
            (UserId, Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice,
             HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy)
        VALUES
            (@UserId, @Symbol, @TakeProfitStep, @PartialProfitStage, @BreakevenPrice,
             @HighestROE, @StairStep, @IsBreakEvenTriggered, @HighestPrice, @LowestPrice, @IsPumpStrategy);
    END
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
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleData    WITH (NOLOCK) WHERE Symbol = @Symbol AND IntervalText = @Interval AND OpenTime > '1800-01-01') AS cd,
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
        SET @sql = @sql + N'SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM dbo.CandleData WITH (NOLOCK) WHERE Symbol IS NOT NULL AND IntervalText = @Interval AND OpenTime > ''1800-01-01'' GROUP BY Symbol' + CHAR(10);
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

        // ════════════════════════════════════════════════════════════════════
        // sp_LoadGeneralSettings — 단순 SELECT
        // ════════════════════════════════════════════════════════════════════
        private const string sp_LoadGeneralSettings = @"
CREATE OR ALTER PROCEDURE dbo.sp_LoadGeneralSettings
    @UserId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT * FROM dbo.GeneralSettings WHERE Id = @UserId;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_SaveGeneralSettings — UPDATE 후 0행이면 INSERT (단일 UserId row)
        // ════════════════════════════════════════════════════════════════════
        private const string sp_SaveGeneralSettings = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveGeneralSettings
    @UserId                       INT,
    @DefaultLeverage              INT,
    @DefaultMargin                DECIMAL(18,8),
    @TargetRoe                    DECIMAL(18,8),
    @StopLossRoe                  DECIMAL(18,8),
    @TrailingStartRoe             DECIMAL(18,8),
    @TrailingDropRoe              DECIMAL(18,8),
    @PumpTp1Roe                   DECIMAL(18,8),
    @PumpTp2Roe                   DECIMAL(18,8),
    @PumpTimeStopMinutes          DECIMAL(18,8),
    @PumpStopDistanceWarnPct      DECIMAL(18,8),
    @PumpStopDistanceBlockPct     DECIMAL(18,8),
    @MajorTrendProfile            NVARCHAR(32),
    @PumpBreakEvenRoe             DECIMAL(18,8),
    @PumpTrailingStartRoe         DECIMAL(18,8),
    @PumpTrailingGapRoe           DECIMAL(18,8),
    @PumpStopLossRoe              DECIMAL(18,8),
    @PumpMargin                   DECIMAL(18,8),
    @PumpLeverage                 INT,
    @PumpFirstTakeProfitRatioPct  DECIMAL(18,8),
    @PumpStairStep1Roe            DECIMAL(18,8),
    @PumpStairStep2Roe            DECIMAL(18,8),
    @PumpStairStep3Roe            DECIMAL(18,8),
    @MajorLeverage                INT,
    @MajorMargin                  DECIMAL(18,8),
    @MajorBreakEvenRoe            DECIMAL(18,8),
    @MajorTp1Roe                  DECIMAL(18,8),
    @MajorTp2Roe                  DECIMAL(18,8),
    @MajorTrailingStartRoe        DECIMAL(18,8),
    @MajorTrailingGapRoe          DECIMAL(18,8),
    @MajorStopLossRoe             DECIMAL(18,8),
    @EnableMajorTrading           BIT,
    @MaxMajorSlots                INT,
    @MaxPumpSlots                 INT,
    @MaxDailyEntries              INT
AS
BEGIN
    SET NOCOUNT ON;

    -- 정상 경로: UserId row 이미 존재 → UPDATE
    UPDATE dbo.GeneralSettings SET
        DefaultLeverage             = @DefaultLeverage,
        DefaultMargin               = @DefaultMargin,
        TargetRoe                   = @TargetRoe,
        StopLossRoe                 = @StopLossRoe,
        TrailingStartRoe            = @TrailingStartRoe,
        TrailingDropRoe             = @TrailingDropRoe,
        PumpTp1Roe                  = @PumpTp1Roe,
        PumpTp2Roe                  = @PumpTp2Roe,
        PumpTimeStopMinutes         = @PumpTimeStopMinutes,
        PumpStopDistanceWarnPct     = @PumpStopDistanceWarnPct,
        PumpStopDistanceBlockPct    = @PumpStopDistanceBlockPct,
        MajorTrendProfile           = @MajorTrendProfile,
        PumpBreakEvenRoe            = @PumpBreakEvenRoe,
        PumpTrailingStartRoe        = @PumpTrailingStartRoe,
        PumpTrailingGapRoe          = @PumpTrailingGapRoe,
        PumpStopLossRoe             = @PumpStopLossRoe,
        PumpMargin                  = @PumpMargin,
        PumpLeverage                = @PumpLeverage,
        PumpFirstTakeProfitRatioPct = @PumpFirstTakeProfitRatioPct,
        PumpStairStep1Roe           = @PumpStairStep1Roe,
        PumpStairStep2Roe           = @PumpStairStep2Roe,
        PumpStairStep3Roe           = @PumpStairStep3Roe,
        MajorLeverage               = @MajorLeverage,
        MajorMargin                 = @MajorMargin,
        MajorBreakEvenRoe           = @MajorBreakEvenRoe,
        MajorTp1Roe                 = @MajorTp1Roe,
        MajorTp2Roe                 = @MajorTp2Roe,
        MajorTrailingStartRoe       = @MajorTrailingStartRoe,
        MajorTrailingGapRoe         = @MajorTrailingGapRoe,
        MajorStopLossRoe            = @MajorStopLossRoe,
        EnableMajorTrading          = @EnableMajorTrading,
        MaxMajorSlots               = @MaxMajorSlots,
        MaxPumpSlots                = @MaxPumpSlots,
        MaxDailyEntries             = @MaxDailyEntries,
        UpdatedAt                   = GETUTCDATE()
    WHERE Id = @UserId;

    -- 첫 호출(없을 때)만 INSERT 1회
    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO dbo.GeneralSettings (
            Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe,
            PumpTp1Roe, PumpTp2Roe, PumpTimeStopMinutes, PumpStopDistanceWarnPct, PumpStopDistanceBlockPct, MajorTrendProfile,
            PumpBreakEvenRoe, PumpTrailingStartRoe, PumpTrailingGapRoe,
            PumpStopLossRoe, PumpMargin, PumpLeverage,
            PumpFirstTakeProfitRatioPct, PumpStairStep1Roe, PumpStairStep2Roe, PumpStairStep3Roe,
            MajorLeverage, MajorMargin, MajorBreakEvenRoe, MajorTp1Roe, MajorTp2Roe,
            MajorTrailingStartRoe, MajorTrailingGapRoe, MajorStopLossRoe,
            EnableMajorTrading, MaxMajorSlots, MaxPumpSlots, MaxDailyEntries, UpdatedAt
        ) VALUES (
            @UserId, @DefaultLeverage, @DefaultMargin, @TargetRoe, @StopLossRoe, @TrailingStartRoe, @TrailingDropRoe,
            @PumpTp1Roe, @PumpTp2Roe, @PumpTimeStopMinutes, @PumpStopDistanceWarnPct, @PumpStopDistanceBlockPct, @MajorTrendProfile,
            @PumpBreakEvenRoe, @PumpTrailingStartRoe, @PumpTrailingGapRoe,
            @PumpStopLossRoe, @PumpMargin, @PumpLeverage,
            @PumpFirstTakeProfitRatioPct, @PumpStairStep1Roe, @PumpStairStep2Roe, @PumpStairStep3Roe,
            @MajorLeverage, @MajorMargin, @MajorBreakEvenRoe, @MajorTp1Roe, @MajorTp2Roe,
            @MajorTrailingStartRoe, @MajorTrailingGapRoe, @MajorStopLossRoe,
            @EnableMajorTrading, @MaxMajorSlots, @MaxPumpSlots, @MaxDailyEntries, GETUTCDATE()
        );
    END
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetTodayStatsByCategory — 카테고리별 오늘 통계
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetTodayStatsByCategory = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetTodayStatsByCategory
    @todayStart DATETIME2,
    @userId     INT
AS
BEGIN
    SET NOCOUNT ON;

    WITH Groups AS (
        SELECT
            Category,
            Symbol,
            EntryTime,
            SUM(ISNULL(PnL, 0)) AS TotalPnL,
            MAX(CASE WHEN IsClosed = 1 THEN 1 ELSE 0 END) AS HasClosed
        FROM dbo.TradeHistory
        WHERE Category IS NOT NULL
          AND EntryTime >= @todayStart
          AND (@userId = 0 OR UserId = @userId)
        GROUP BY Category, Symbol, EntryTime
    )
    SELECT
        Category,
        COUNT(*) AS Entries,
        SUM(CASE WHEN HasClosed = 1 AND TotalPnL > 0 THEN 1 ELSE 0 END) AS Wins,
        SUM(CASE WHEN HasClosed = 1 AND TotalPnL < 0 THEN 1 ELSE 0 END) AS Losses,
        SUM(TotalPnL) AS TotalPnL
    FROM Groups
    GROUP BY Category;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_LoadPositionStates — 포지션 상태 복원
        // ════════════════════════════════════════════════════════════════════
        private const string sp_LoadPositionStates = @"
CREATE OR ALTER PROCEDURE dbo.sp_LoadPositionStates
    @UserId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice,
           HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy
    FROM dbo.PositionState WITH (NOLOCK)
    WHERE UserId = @UserId;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_DeletePositionState — 포지션 상태 삭제
        // ════════════════════════════════════════════════════════════════════
        private const string sp_DeletePositionState = @"
CREATE OR ALTER PROCEDURE dbo.sp_DeletePositionState
    @UserId INT,
    @Symbol NVARCHAR(32)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.PositionState WHERE UserId = @UserId AND Symbol = @Symbol;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_CompleteTradePatternSnapshot — 패턴 스냅샷 완성
        // ════════════════════════════════════════════════════════════════════
        private const string sp_CompleteTradePatternSnapshot = @"
CREATE OR ALTER PROCEDURE dbo.sp_CompleteTradePatternSnapshot
    @UserId      INT,
    @Symbol      NVARCHAR(50),
    @EntryTime   DATETIME2,
    @ExitTime    DATETIME2,
    @PnL         DECIMAL(18,8),
    @PnLPercent  DECIMAL(18,8),
    @ExitReason  NVARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Label TINYINT = CASE WHEN @PnL > 0 THEN 1 ELSE 0 END;
    DECLARE @ExitType NVARCHAR(20) = CASE WHEN @PnL > 0 THEN 'TAKEPROFIT' ELSE 'STOPLOSS' END;

    IF @ExitReason LIKE '%손절%' OR @ExitReason LIKE '%stop%' OR @ExitReason LIKE '%sl%'
    BEGIN
        SET @Label = 0;
        SET @ExitType = 'STOPLOSS';
    END
    ELSE IF @ExitReason LIKE '%익절%' OR @ExitReason LIKE '%takeprofit%' OR @ExitReason LIKE '%take profit%' OR @ExitReason LIKE '%tp%' OR @ExitReason LIKE '%profit run%'
    BEGIN
        SET @Label = 1;
        SET @ExitType = 'TAKEPROFIT';
    END

    ;WITH TargetRow AS
    (
        SELECT TOP (1) Id
        FROM dbo.TradePatternSnapshots WITH (UPDLOCK, HOLDLOCK)
        WHERE UserId = @UserId
          AND Symbol = @Symbol
          AND Label IS NULL
        ORDER BY ABS(DATEDIFF(SECOND, EntryTime, @EntryTime)), Id DESC
    )
    UPDATE t
    SET
        ExitTime = @ExitTime,
        PnL = @PnL,
        PnLPercent = @PnLPercent,
        Label = @Label,
        ExitReason = @ExitReason,
        ExitType = @ExitType,
        UpdatedAt = SYSUTCDATETIME()
    FROM dbo.TradePatternSnapshots t
    INNER JOIN TargetRow x ON x.Id = t.Id;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_SaveTradePatternSnapshot — 패턴 스냅샷 저장
        // ════════════════════════════════════════════════════════════════════
        private const string sp_SaveTradePatternSnapshot = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveTradePatternSnapshot
    @UserId                  INT,
    @Symbol                  NVARCHAR(50),
    @Side                    NVARCHAR(10),
    @Strategy                NVARCHAR(150),
    @Mode                    NVARCHAR(50),
    @EntryTime               DATETIME2,
    @EntryPrice              DECIMAL(18,8),
    @FinalScore              FLOAT,
    @AiScore                 FLOAT,
    @ElliottScore            FLOAT,
    @VolumeScore             FLOAT,
    @RsiMacdScore            FLOAT,
    @BollingerScore          FLOAT,
    @PredictedChangePct      FLOAT,
    @ScoreGap                FLOAT,
    @AtrPercent              FLOAT,
    @HtfPenalty              FLOAT,
    @Adx                     FLOAT,
    @PlusDi                  FLOAT,
    @MinusDi                 FLOAT,
    @Rsi                     FLOAT,
    @MacdHist                FLOAT,
    @BbPosition              FLOAT,
    @VolumeRatio             FLOAT,
    @SimilarityScore         FLOAT,
    @EuclideanSimilarity     FLOAT,
    @CosineSimilarity        FLOAT,
    @MatchProbability        FLOAT,
    @MatchedPatternId        BIGINT,
    @IsSuperEntry            BIT,
    @PositionSizeMultiplier  FLOAT,
    @TakeProfitMultiplier    FLOAT,
    @ComponentMix            NVARCHAR(MAX),
    @ContextJson             NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.TradePatternSnapshots
    (
        UserId, Symbol, Side, Strategy, Mode, EntryTime, EntryPrice,
        FinalScore, AiScore, ElliottScore, VolumeScore, RsiMacdScore, BollingerScore,
        PredictedChangePct, ScoreGap,
        AtrPercent, HtfPenalty, Adx, PlusDi, MinusDi, Rsi, MacdHist, BbPosition, VolumeRatio,
        SimilarityScore, EuclideanSimilarity, CosineSimilarity, MatchProbability, MatchedPatternId,
        IsSuperEntry, PositionSizeMultiplier, TakeProfitMultiplier,
        ComponentMix, ContextJson, UpdatedAt
    )
    OUTPUT INSERTED.Id
    VALUES
    (
        @UserId, @Symbol, @Side, @Strategy, @Mode, @EntryTime, @EntryPrice,
        @FinalScore, @AiScore, @ElliottScore, @VolumeScore, @RsiMacdScore, @BollingerScore,
        @PredictedChangePct, @ScoreGap,
        @AtrPercent, @HtfPenalty, @Adx, @PlusDi, @MinusDi, @Rsi, @MacdHist, @BbPosition, @VolumeRatio,
        @SimilarityScore, @EuclideanSimilarity, @CosineSimilarity, @MatchProbability, @MatchedPatternId,
        @IsSuperEntry, @PositionSizeMultiplier, @TakeProfitMultiplier,
        @ComponentMix, @ContextJson, SYSUTCDATETIME()
    );
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetLabeledTradePatternSnapshots — 라벨링된 패턴 스냅샷 조회
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetLabeledTradePatternSnapshots = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetLabeledTradePatternSnapshots
    @UserId      INT,
    @Symbol      NVARCHAR(50),
    @Side        NVARCHAR(10),
    @LookbackDays INT,
    @MaxRows     INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@MaxRows)
        Id, UserId, Symbol, Side, Strategy, Mode, EntryTime, ExitTime, EntryPrice,
        FinalScore, AiScore, ElliottScore, VolumeScore, RsiMacdScore, BollingerScore,
        PredictedChangePct, ScoreGap,
        AtrPercent, HtfPenalty, Adx, PlusDi, MinusDi, Rsi, MacdHist, BbPosition, VolumeRatio,
        SimilarityScore, EuclideanSimilarity, CosineSimilarity, MatchProbability, MatchedPatternId,
        IsSuperEntry, PositionSizeMultiplier, TakeProfitMultiplier,
        Label, PnL, PnLPercent,
        ComponentMix, ContextJson,
        CreatedAt, UpdatedAt
    FROM dbo.TradePatternSnapshots
    WHERE UserId = @UserId
      AND Symbol = @Symbol
      AND Side = @Side
      AND Label IN (0, 1)
      AND EntryTime >= DATEADD(DAY, -@LookbackDays, SYSUTCDATETIME())
    ORDER BY EntryTime DESC;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_SaveAiTrainingData — AI 학습 데이터 저장
        // ════════════════════════════════════════════════════════════════════
        private const string sp_SaveAiTrainingData = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveAiTrainingData
    @UserId          INT,
    @Symbol          NVARCHAR(50),
    @EntryTimeUtc    DATETIME2,
    @EntryPrice      DECIMAL(18,8),
    @ActualProfitPct FLOAT,
    @IsSuccess       BIT,
    @ShouldEnter     BIT,
    @LabelSource     NVARCHAR(120),
    @FeatureJson     NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.AiLabeledSamples
    (
        UserId,
        Symbol,
        EntryTimeUtc,
        EntryPrice,
        ActualProfitPct,
        IsSuccess,
        ShouldEnter,
        LabelSource,
        FeatureJson
    )
    VALUES
    (
        @UserId,
        @Symbol,
        @EntryTimeUtc,
        @EntryPrice,
        @ActualProfitPct,
        @IsSuccess,
        @ShouldEnter,
        @LabelSource,
        @FeatureJson
    );
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_UpsertAiTrainingRun — AI 학습 이력 UPSERT (UPDATE → INSERT)
        // ════════════════════════════════════════════════════════════════════
        private const string sp_UpsertAiTrainingRun = @"
CREATE OR ALTER PROCEDURE dbo.sp_UpsertAiTrainingRun
    @UserId             INT,
    @ProjectName        NVARCHAR(50),
    @RunId              NVARCHAR(80),
    @Stage              NVARCHAR(50),
    @Success            BIT,
    @SampleCount        INT,
    @Epochs             INT,
    @Accuracy           FLOAT,
    @F1Score            FLOAT,
    @AUC                FLOAT,
    @BestValidationLoss REAL,
    @FinalTrainLoss     REAL,
    @Detail             NVARCHAR(500)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.AiTrainingRuns
    SET Stage              = @Stage,
        Success            = @Success,
        SampleCount        = @SampleCount,
        Epochs             = @Epochs,
        Accuracy           = @Accuracy,
        F1Score            = @F1Score,
        AUC                = @AUC,
        BestValidationLoss = @BestValidationLoss,
        FinalTrainLoss     = @FinalTrainLoss,
        Detail             = @Detail,
        CompletedAtUtc     = SYSUTCDATETIME(),
        UpdatedAtUtc       = SYSUTCDATETIME()
    WHERE UserId = @UserId AND ProjectName = @ProjectName AND RunId = @RunId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO dbo.AiTrainingRuns
            (UserId, ProjectName, RunId, Stage, Success, SampleCount, Epochs,
             Accuracy, F1Score, AUC, BestValidationLoss, FinalTrainLoss, Detail,
             CompletedAtUtc, UpdatedAtUtc)
        VALUES
            (@UserId, @ProjectName, @RunId, @Stage, @Success, @SampleCount, @Epochs,
             @Accuracy, @F1Score, @AUC, @BestValidationLoss, @FinalTrainLoss, @Detail,
             SYSUTCDATETIME(), SYSUTCDATETIME());
    END
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetOpenTrades — 오픈 포지션 조회
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetOpenTrades = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetOpenTrades
    @UserId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Symbol, Side, EntryPrice, Quantity, EntryTime
    FROM dbo.TradeHistory WITH (NOLOCK)
    WHERE UserId = @UserId AND IsClosed = 0
    ORDER BY EntryTime DESC;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_UpsertTradeEntry — TradeHistory 진입 UPSERT
        // ════════════════════════════════════════════════════════════════════
        private const string sp_UpsertTradeEntry = @"
CREATE OR ALTER PROCEDURE dbo.sp_UpsertTradeEntry
    @UserId     INT,
    @Symbol     NVARCHAR(32),
    @Side       NVARCHAR(8),
    @Strategy   NVARCHAR(64),
    @EntryPrice DECIMAL(18,8),
    @Quantity   DECIMAL(18,8),
    @AiScore    REAL,
    @EntryTime  DATETIME2(7),
    @IsSimulation BIT,
    @Category   NVARCHAR(32)
AS
BEGIN
    SET NOCOUNT ON;

    -- 먼저 UPDATE 시도 (기존 오픈 포지션 업데이트)
    DECLARE @AffectedRows INT;
    
    ;WITH LatestOpen AS (
        SELECT TOP (1) *
        FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
        WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
        ORDER BY EntryTime DESC, Id DESC
    )
    UPDATE LatestOpen
    SET Side = @Side,
        Strategy = @Strategy,
        EntryPrice = @EntryPrice,
        Quantity = @Quantity,
        AiScore = @AiScore,
        EntryTime = @EntryTime,
        Category = @Category,
        LastUpdatedAt = GETDATE(),
        CloseVerified = 0;
    
    SET @AffectedRows = @@ROWCOUNT;

    -- UPDATE 실패 시 INSERT
    IF @AffectedRows = 0
    BEGIN
        INSERT INTO dbo.TradeHistory
            (UserId, Symbol, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
        VALUES
            (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime, NULL, 0, 0, NULL, 0, 0, @IsSimulation, @Category, GETDATE());
    END
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetTradeLogs — TradeLogs 조회 (동적 필터)
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetTradeLogs = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetTradeLogs
    @UserId    INT,
    @Limit     INT,
    @StartDate DATETIME2(7) = NULL,
    @EndDate   DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @sql NVARCHAR(MAX) = N'SELECT TOP (@Limit) * FROM dbo.TradeLogs WHERE 1=1';

    IF @UserId > 0
        SET @sql = @sql + N' AND UserId = @UserId';

    IF @StartDate IS NOT NULL
        SET @sql = @sql + N' AND Time >= @StartDate';

    IF @EndDate IS NOT NULL
        SET @sql = @sql + N' AND Time <= @EndDate';

    SET @sql = @sql + N' ORDER BY Time DESC';

    EXEC sp_executesql @sql, N'@Limit INT, @UserId INT, @StartDate DATETIME2(7), @EndDate DATETIME2(7)',
                       @Limit = @Limit, @UserId = @UserId, @StartDate = @StartDate, @EndDate = @EndDate;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetTradeHistory — TradeHistory 조회
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetTradeHistory = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetTradeHistory
    @UserId    INT,
    @StartDate DATETIME2(7),
    @EndDate   DATETIME2(7),
    @Limit     INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Limit)
        Id,
        Symbol,
        Side,
        Strategy,
        AiScore,
        CASE WHEN IsClosed = 0 THEN EntryTime ELSE COALESCE(ExitTime, EntryTime) END AS Time,
        CASE WHEN IsClosed = 0 THEN EntryPrice ELSE COALESCE(ExitPrice, EntryPrice) END AS Price,
        PnL,
        PnLPercent,
        EntryPrice,
        CASE WHEN IsClosed = 0 THEN 0 ELSE COALESCE(ExitPrice, 0) END AS ExitPrice,
        Quantity,
        CASE WHEN IsClosed = 0 THEN N'OPEN_POSITION' ELSE COALESCE(ExitReason, N'') END AS ExitReason,
        EntryTime,
        CASE WHEN IsClosed = 0 THEN EntryTime ELSE COALESCE(ExitTime, EntryTime) END AS ExitTime
    FROM dbo.TradeHistory WITH (NOLOCK)
    WHERE UserId = @UserId
        AND (
            (IsClosed = 1 AND CloseVerified = 1 AND ExitTime >= @StartDate AND ExitTime <= @EndDate)
            OR
            (IsClosed = 0 AND EntryTime >= @StartDate AND EntryTime <= @EndDate)
        )
    ORDER BY CASE WHEN IsClosed = 0 THEN EntryTime ELSE COALESCE(ExitTime, EntryTime) END DESC, Id DESC;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetHoldingOverHourStats — 1시간 이상 보유한 청산 거래 통계
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetHoldingOverHourStats = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetHoldingOverHourStats
    @UserId    INT,
    @StartDate DATETIME2(7) = NULL,
    @EndDate   DATETIME2(7) = NULL,
    @Symbol    NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Symbol,
        COUNT(*) AS TotalTrades,
        SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS WinCount,
        SUM(CASE WHEN PnL <= 0 THEN 1 ELSE 0 END) AS LossCount,
        CASE WHEN COUNT(*) = 0 THEN 0 ELSE CAST(SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*) END AS WinRate,
        AVG(PnL) AS AvgPnL,
        AVG(PnLPercent) AS AvgPnLPercent,
        AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldingMinutes,
        SUM(PnL) AS TotalPnL
    FROM dbo.TradeHistory WITH (NOLOCK)
    WHERE UserId = @UserId
      AND IsClosed = 1
      AND CloseVerified = 1
      AND ExitTime IS NOT NULL
      AND EntryTime IS NOT NULL
      AND DATEDIFF(MINUTE, EntryTime, ExitTime) >= 60
      AND (@Symbol IS NULL OR Symbol = @Symbol)
      AND (@StartDate IS NULL OR ExitTime >= @StartDate)
      AND (@EndDate IS NULL OR ExitTime <= @EndDate)
    GROUP BY Symbol
    ORDER BY WinRate DESC, TotalTrades DESC;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetRecentCandleData — 최근 캔들 데이터 조회
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetRecentCandleData = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetRecentCandleData
    @Symbol NVARCHAR(32),
    @Limit  INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Limit) Symbol, OpenTime, [Open], [High], [Low], [Close], Volume
    FROM dbo.CandleData WITH (NOLOCK)
    WHERE Symbol = @Symbol
    ORDER BY OpenTime DESC;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_GetCandleDataByInterval — 인터벌별 캔들 데이터 조회
        // ════════════════════════════════════════════════════════════════════
        private const string sp_GetCandleDataByInterval = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetCandleDataByInterval
    @Symbol  NVARCHAR(32),
    @Interval NVARCHAR(8),
    @Limit   INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Limit) Symbol, OpenTime, [Open], [High], [Low], [Close], Volume
    FROM dbo.CandleData WITH (NOLOCK)
    WHERE Symbol = @Symbol AND IntervalText = @Interval
    ORDER BY OpenTime ASC;
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_CompleteTrade — TradeHistory 청산 처리
        // ════════════════════════════════════════════════════════════════════
        private const string sp_CompleteTrade = @"
CREATE OR ALTER PROCEDURE dbo.sp_CompleteTrade
    @UserId     INT,
    @Symbol     NVARCHAR(32),
    @ExitPrice  DECIMAL(18,8),
    @Quantity   DECIMAL(18,8),
    @AiScore    REAL,
    @PnL        DECIMAL(18,8),
    @PnLPercent DECIMAL(18,4),
    @ExitReason NVARCHAR(255),
    @ExitTime   DATETIME2(7),
    @IsSimulation BIT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OpenTradeId INT;

    -- 오픈 트레이드 찾기
    SELECT TOP (1) @OpenTradeId = Id
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
    ORDER BY EntryTime DESC, Id DESC;

    -- 찾으면 청산 처리
    IF @OpenTradeId IS NOT NULL
    BEGIN
        UPDATE dbo.TradeHistory
        SET ExitPrice = @ExitPrice,
            Quantity = CASE WHEN @Quantity > 0 THEN @Quantity ELSE Quantity END,
            AiScore = CASE WHEN @AiScore <> 0 THEN @AiScore ELSE AiScore END,
            PnL = @PnL,
            PnLPercent = @PnLPercent,
            ExitReason = @ExitReason,
            ExitTime = @ExitTime,
            IsClosed = 1,
            CloseVerified = 1,
            IsSimulation = @IsSimulation,
            LastUpdatedAt = GETDATE()
        WHERE Id = @OpenTradeId;
    END
END";

        // ════════════════════════════════════════════════════════════════════
        // sp_TryCompleteOpenTrade — 외부 청산 동기화
        // ════════════════════════════════════════════════════════════════════
        private const string sp_TryCompleteOpenTrade = @"
CREATE OR ALTER PROCEDURE dbo.sp_TryCompleteOpenTrade
    @UserId     INT,
    @Symbol     NVARCHAR(32),
    @ExitPrice  DECIMAL(18,8),
    @Quantity   DECIMAL(18,8),
    @AiScore    REAL,
    @PnL        DECIMAL(18,8),
    @PnLPercent DECIMAL(18,4),
    @ExitReason NVARCHAR(255),
    @ExitTime   DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OpenTradeId INT;

    -- 오픈 트레이드 찾기
    SELECT TOP (1) @OpenTradeId = Id
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
    ORDER BY EntryTime DESC, Id DESC;

    -- 찾으면 청산 처리
    IF @OpenTradeId IS NOT NULL
    BEGIN
        UPDATE dbo.TradeHistory
        SET ExitPrice = @ExitPrice,
            Quantity = @Quantity,
            AiScore = CASE WHEN @AiScore <> 0 THEN @AiScore ELSE AiScore END,
            PnL = @PnL,
            PnLPercent = @PnLPercent,
            ExitReason = @ExitReason,
            ExitTime = @ExitTime,
            IsClosed = 1,
            CloseVerified = 1,
            LastUpdatedAt = GETDATE()
        WHERE Id = @OpenTradeId;
    END
END";

        // ════════════════════════════════════════════════════════════════════
        // [v5.10.65] 신규 SP — 인라인 SQL → SP 전환
        // ════════════════════════════════════════════════════════════════════

        // sp_SaveAiSignalLog — Bot_Log INSERT
        private const string sp_SaveAiSignalLog = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveAiSignalLog
    @UserId     INT,
    @Symbol     NVARCHAR(20),
    @Direction  NVARCHAR(10),
    @CoinType   NVARCHAR(20),
    @Allowed    BIT,
    @Reason     NVARCHAR(200),
    @ML_Conf    REAL,
    @TF_Conf    REAL,
    @TrendScore REAL,
    @RSI        REAL = NULL,
    @BBPosition REAL = NULL,
    @DecisionId NVARCHAR(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Bot_Log
        (UserId, Symbol, Direction, CoinType, Allowed, Reason,
         ML_Conf, TF_Conf, TrendScore, RSI, BBPosition, DecisionId)
    VALUES
        (@UserId, @Symbol, @Direction, @CoinType, @Allowed, @Reason,
         @ML_Conf, @TF_Conf, @TrendScore, @RSI, @BBPosition, @DecisionId);
END";

        // sp_SaveOrderError — Order_Error INSERT
        private const string sp_SaveOrderError = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveOrderError
    @UserId     INT,
    @Symbol     NVARCHAR(20),
    @Side       NVARCHAR(10),
    @OrderType  NVARCHAR(30),
    @Quantity   DECIMAL(18,8),
    @ErrorCode  INT = NULL,
    @ErrorMsg   NVARCHAR(500),
    @Resolved   BIT = 0,
    @RetryCount INT = 0,
    @Resolution NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Order_Error
        (UserId, Symbol, Side, OrderType, Quantity, ErrorCode, ErrorMsg, Resolved, RetryCount, Resolution)
    VALUES
        (@UserId, @Symbol, @Side, @OrderType, @Quantity, @ErrorCode, @ErrorMsg, @Resolved, @RetryCount, @Resolution);
END";

        // sp_SaveArbitrageExecutionLog — UserId 컬럼 유무에 따른 동적 INSERT
        private const string sp_SaveArbitrageExecutionLog = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveArbitrageExecutionLog
    @UserId         INT,
    @Symbol         NVARCHAR(50),
    @BuyExchange    NVARCHAR(50),
    @SellExchange   NVARCHAR(50),
    @BuyPrice       DECIMAL(18,8),
    @SellPrice      DECIMAL(18,8),
    @Quantity       DECIMAL(18,8),
    @ProfitPercent  DECIMAL(18,8),
    @BuyOrderId     NVARCHAR(100) = NULL,
    @SellOrderId    NVARCHAR(100) = NULL,
    @BuySuccess     BIT,
    @SellSuccess    BIT,
    @Success        BIT,
    @ErrorMessage   NVARCHAR(500) = NULL,
    @StartTime      DATETIME2(7),
    @EndTime        DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('dbo.ArbitrageExecutionLog', 'U') IS NULL RETURN;

    IF COL_LENGTH('dbo.ArbitrageExecutionLog', 'UserId') IS NOT NULL
    BEGIN
        INSERT INTO dbo.ArbitrageExecutionLog
            (UserId, Symbol, BuyExchange, SellExchange, BuyPrice, SellPrice, Quantity,
             ProfitPercent, BuyOrderId, SellOrderId, BuySuccess, SellSuccess,
             Success, ErrorMessage, StartTime, EndTime)
        VALUES
            (@UserId, @Symbol, @BuyExchange, @SellExchange, @BuyPrice, @SellPrice, @Quantity,
             @ProfitPercent, @BuyOrderId, @SellOrderId, @BuySuccess, @SellSuccess,
             @Success, @ErrorMessage, @StartTime, @EndTime);
    END
    ELSE
    BEGIN
        INSERT INTO dbo.ArbitrageExecutionLog
            (Symbol, BuyExchange, SellExchange, BuyPrice, SellPrice, Quantity,
             ProfitPercent, BuyOrderId, SellOrderId, BuySuccess, SellSuccess,
             Success, ErrorMessage, StartTime, EndTime)
        VALUES
            (@Symbol, @BuyExchange, @SellExchange, @BuyPrice, @SellPrice, @Quantity,
             @ProfitPercent, @BuyOrderId, @SellOrderId, @BuySuccess, @SellSuccess,
             @Success, @ErrorMessage, @StartTime, @EndTime);
    END
END";

        // sp_SaveFundTransferLog — UserId 컬럼 유무에 따른 동적 INSERT
        private const string sp_SaveFundTransferLog = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveFundTransferLog
    @UserId          INT,
    @FromExchange    NVARCHAR(50),
    @ToExchange      NVARCHAR(50),
    @Asset           NVARCHAR(20),
    @Amount          DECIMAL(18,8),
    @WithdrawSuccess BIT,
    @DepositSuccess  BIT,
    @Success         BIT,
    @ErrorMessage    NVARCHAR(500) = NULL,
    @RequestTime     DATETIME2(7),
    @StartTime       DATETIME2(7),
    @EndTime         DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('dbo.FundTransferLog', 'U') IS NULL RETURN;

    IF COL_LENGTH('dbo.FundTransferLog', 'UserId') IS NOT NULL
    BEGIN
        INSERT INTO dbo.FundTransferLog
            (UserId, FromExchange, ToExchange, Asset, Amount, WithdrawSuccess, DepositSuccess,
             Success, ErrorMessage, RequestTime, StartTime, EndTime)
        VALUES
            (@UserId, @FromExchange, @ToExchange, @Asset, @Amount, @WithdrawSuccess, @DepositSuccess,
             @Success, @ErrorMessage, @RequestTime, @StartTime, @EndTime);
    END
    ELSE
    BEGIN
        INSERT INTO dbo.FundTransferLog
            (FromExchange, ToExchange, Asset, Amount, WithdrawSuccess, DepositSuccess,
             Success, ErrorMessage, RequestTime, StartTime, EndTime)
        VALUES
            (@FromExchange, @ToExchange, @Asset, @Amount, @WithdrawSuccess, @DepositSuccess,
             @Success, @ErrorMessage, @RequestTime, @StartTime, @EndTime);
    END
END";

        // sp_SaveRebalancingLog — Parent INSERT, OUTPUT Id
        private const string sp_SaveRebalancingLog = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveRebalancingLog
    @UserId        INT,
    @TotalValue    DECIMAL(18,8),
    @ActionCount   INT,
    @Success       BIT,
    @ErrorMessage  NVARCHAR(500) = NULL,
    @StartTime     DATETIME2(7),
    @EndTime       DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('dbo.PortfolioRebalancingLog', 'U') IS NULL
    BEGIN
        SELECT CAST(0 AS BIGINT) AS Id;
        RETURN;
    END

    IF COL_LENGTH('dbo.PortfolioRebalancingLog', 'UserId') IS NOT NULL
    BEGIN
        INSERT INTO dbo.PortfolioRebalancingLog
            (UserId, TotalValue, ActionCount, Success, ErrorMessage, StartTime, EndTime)
        OUTPUT INSERTED.Id
        VALUES
            (@UserId, @TotalValue, @ActionCount, @Success, @ErrorMessage, @StartTime, @EndTime);
    END
    ELSE
    BEGIN
        INSERT INTO dbo.PortfolioRebalancingLog
            (TotalValue, ActionCount, Success, ErrorMessage, StartTime, EndTime)
        OUTPUT INSERTED.Id
        VALUES
            (@TotalValue, @ActionCount, @Success, @ErrorMessage, @StartTime, @EndTime);
    END
END";

        // sp_SaveRebalancingAction — Child INSERT (loop)
        private const string sp_SaveRebalancingAction = @"
CREATE OR ALTER PROCEDURE dbo.sp_SaveRebalancingAction
    @LogId             BIGINT,
    @UserId            INT,
    @Asset             NVARCHAR(20),
    @CurrentPercentage DECIMAL(18,8),
    @TargetPercentage  DECIMAL(18,8),
    @Deviation         DECIMAL(18,8),
    @Action            NVARCHAR(20),
    @TargetValue       DECIMAL(18,8),
    @Executed          BIT
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('dbo.RebalancingAction', 'U') IS NULL RETURN;

    IF COL_LENGTH('dbo.RebalancingAction', 'UserId') IS NOT NULL
    BEGIN
        INSERT INTO dbo.RebalancingAction
            (RebalancingLogId, UserId, Asset, CurrentPercentage, TargetPercentage,
             Deviation, Action, TargetValue, Executed)
        VALUES
            (@LogId, @UserId, @Asset, @CurrentPercentage, @TargetPercentage,
             @Deviation, @Action, @TargetValue, @Executed);
    END
    ELSE
    BEGIN
        INSERT INTO dbo.RebalancingAction
            (RebalancingLogId, Asset, CurrentPercentage, TargetPercentage,
             Deviation, Action, TargetValue, Executed)
        VALUES
            (@LogId, @Asset, @CurrentPercentage, @TargetPercentage,
             @Deviation, @Action, @TargetValue, @Executed);
    END
END";

        // sp_GetRecentlyClosedPositions — DISTINCT MAX(ExitTime) GROUP BY Symbol
        private const string sp_GetRecentlyClosedPositions = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetRecentlyClosedPositions
    @UserId        INT,
    @WithinMinutes INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Symbol, MAX(ExitTime) AS LastExitTime
    FROM dbo.TradeHistory WITH (NOLOCK)
    WHERE ExitTime IS NOT NULL
      AND ExitTime > DATEADD(MINUTE, -@WithinMinutes, GETDATE())
      AND UserId = @UserId
    GROUP BY Symbol;
END";

        // sp_UpsertElliottWaveAnchor — 인라인 MERGE 제거 → UPDATE+INSERT
        private const string sp_UpsertElliottWaveAnchor = @"
CREATE OR ALTER PROCEDURE dbo.sp_UpsertElliottWaveAnchor
    @UserId               INT,
    @Symbol               NVARCHAR(50),
    @CurrentPhase         INT,
    @Phase1StartTime      DATETIME2(7) = NULL,
    @Phase1LowPrice       DECIMAL(18,8),
    @Phase1HighPrice      DECIMAL(18,8),
    @Phase1Volume         REAL,
    @Phase2StartTime      DATETIME2(7) = NULL,
    @Phase2LowPrice       DECIMAL(18,8),
    @Phase2HighPrice      DECIMAL(18,8),
    @Phase2Volume         REAL,
    @Fib500Level          DECIMAL(18,8),
    @Fib0618Level         DECIMAL(18,8),
    @Fib786Level          DECIMAL(18,8),
    @Fib1618Target        DECIMAL(18,8),
    @AnchorLowPoint       DECIMAL(18,8),
    @AnchorHighPoint      DECIMAL(18,8),
    @AnchorIsConfirmed    BIT,
    @AnchorIsLocked       BIT,
    @AnchorConfirmedAtUtc DATETIME2(7) = NULL,
    @LowPivotStrength     INT,
    @HighPivotStrength    INT,
    @UpdatedAtUtc         DATETIME2(7)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.ElliottWaveAnchors WITH (UPDLOCK, HOLDLOCK)
    SET CurrentPhase         = @CurrentPhase,
        Phase1StartTime      = @Phase1StartTime,
        Phase1LowPrice       = @Phase1LowPrice,
        Phase1HighPrice      = @Phase1HighPrice,
        Phase1Volume         = @Phase1Volume,
        Phase2StartTime      = @Phase2StartTime,
        Phase2LowPrice       = @Phase2LowPrice,
        Phase2HighPrice      = @Phase2HighPrice,
        Phase2Volume         = @Phase2Volume,
        Fib500Level          = @Fib500Level,
        Fib0618Level         = @Fib0618Level,
        Fib786Level          = @Fib786Level,
        Fib1618Target        = @Fib1618Target,
        AnchorLowPoint       = @AnchorLowPoint,
        AnchorHighPoint      = @AnchorHighPoint,
        AnchorIsConfirmed    = @AnchorIsConfirmed,
        AnchorIsLocked       = @AnchorIsLocked,
        AnchorConfirmedAtUtc = @AnchorConfirmedAtUtc,
        LowPivotStrength     = @LowPivotStrength,
        HighPivotStrength    = @HighPivotStrength,
        UpdatedAtUtc         = @UpdatedAtUtc
    WHERE UserId = @UserId AND Symbol = @Symbol;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO dbo.ElliottWaveAnchors
            (UserId, Symbol, CurrentPhase,
             Phase1StartTime, Phase1LowPrice, Phase1HighPrice, Phase1Volume,
             Phase2StartTime, Phase2LowPrice, Phase2HighPrice, Phase2Volume,
             Fib500Level, Fib0618Level, Fib786Level, Fib1618Target,
             AnchorLowPoint, AnchorHighPoint, AnchorIsConfirmed, AnchorIsLocked, AnchorConfirmedAtUtc,
             LowPivotStrength, HighPivotStrength, UpdatedAtUtc)
        VALUES
            (@UserId, @Symbol, @CurrentPhase,
             @Phase1StartTime, @Phase1LowPrice, @Phase1HighPrice, @Phase1Volume,
             @Phase2StartTime, @Phase2LowPrice, @Phase2HighPrice, @Phase2Volume,
             @Fib500Level, @Fib0618Level, @Fib786Level, @Fib1618Target,
             @AnchorLowPoint, @AnchorHighPoint, @AnchorIsConfirmed, @AnchorIsLocked, @AnchorConfirmedAtUtc,
             @LowPivotStrength, @HighPivotStrength, @UpdatedAtUtc);
    END
END";

        // sp_LoadElliottWaveAnchors — UserId + 선택적 Symbols CSV 필터
        private const string sp_LoadElliottWaveAnchors = @"
CREATE OR ALTER PROCEDURE dbo.sp_LoadElliottWaveAnchors
    @UserId     INT,
    @SymbolsCsv NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT UserId, Symbol, CurrentPhase,
           Phase1StartTime, Phase1LowPrice, Phase1HighPrice, Phase1Volume,
           Phase2StartTime, Phase2LowPrice, Phase2HighPrice, Phase2Volume,
           Fib500Level, Fib0618Level, Fib786Level, Fib1618Target,
           AnchorLowPoint, AnchorHighPoint, AnchorIsConfirmed, AnchorIsLocked,
           AnchorConfirmedAtUtc, LowPivotStrength, HighPivotStrength, UpdatedAtUtc
    FROM dbo.ElliottWaveAnchors WITH (NOLOCK)
    WHERE UserId = @UserId
      AND (@SymbolsCsv IS NULL OR LEN(@SymbolsCsv) = 0
           OR Symbol IN (SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@SymbolsCsv, ',')));
END";

        // sp_DeleteElliottWaveAnchor
        private const string sp_DeleteElliottWaveAnchor = @"
CREATE OR ALTER PROCEDURE dbo.sp_DeleteElliottWaveAnchor
    @UserId INT,
    @Symbol NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.ElliottWaveAnchors
    WHERE UserId = @UserId AND Symbol = @Symbol;
END";

        // sp_ResolveCloseSymbol — 3단계 매칭 (strict / side / open)
        private const string sp_ResolveCloseSymbol = @"
CREATE OR ALTER PROCEDURE dbo.sp_ResolveCloseSymbol
    @UserId     INT,
    @Side       NVARCHAR(10),
    @EntryPrice DECIMAL(18,8),
    @Quantity   DECIMAL(18,8),
    @EntryTime  DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Symbol    NVARCHAR(50) = NULL;
    DECLARE @MatchType NVARCHAR(16) = N'';

    -- 1) strict 매칭
    SELECT TOP (1) @Symbol = Symbol
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId
      AND IsClosed = 0
      AND Side = @Side
      AND (@EntryPrice <= 0 OR ABS(CAST(EntryPrice AS FLOAT) - CAST(@EntryPrice AS FLOAT)) <= ABS(CAST(@EntryPrice AS FLOAT)) * 0.02)
      AND (@Quantity   <= 0 OR ABS(CAST(Quantity   AS FLOAT) - CAST(@Quantity   AS FLOAT)) <= ABS(CAST(@Quantity   AS FLOAT)) * 0.30)
    ORDER BY
      CASE WHEN @EntryTime IS NULL THEN 0 ELSE ABS(DATEDIFF(SECOND, EntryTime, @EntryTime)) END,
      Id DESC;

    IF @Symbol IS NOT NULL AND LEN(@Symbol) > 0
    BEGIN
        SET @MatchType = N'strict';
        SELECT @Symbol AS Symbol, @MatchType AS MatchType;
        RETURN;
    END

    -- 2) side 매칭
    SELECT TOP (1) @Symbol = Symbol
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId
      AND IsClosed = 0
      AND Side = @Side
    ORDER BY EntryTime DESC, Id DESC;

    IF @Symbol IS NOT NULL AND LEN(@Symbol) > 0
    BEGIN
        SET @MatchType = N'side';
        SELECT @Symbol AS Symbol, @MatchType AS MatchType;
        RETURN;
    END

    -- 3) open 최신
    SELECT TOP (1) @Symbol = Symbol
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId
      AND IsClosed = 0
    ORDER BY EntryTime DESC, Id DESC;

    IF @Symbol IS NOT NULL AND LEN(@Symbol) > 0
    BEGIN
        SET @MatchType = N'open';
        SELECT @Symbol AS Symbol, @MatchType AS MatchType;
        RETURN;
    END

    SELECT CAST(N'' AS NVARCHAR(50)) AS Symbol, CAST(N'' AS NVARCHAR(16)) AS MatchType;
END";

        // sp_MirrorToTradeLogs — IF NOT EXISTS + INSERT (UserId 유무에 따라 동적)
        private const string sp_MirrorToTradeLogs = @"
CREATE OR ALTER PROCEDURE dbo.sp_MirrorToTradeLogs
    @UserId      INT,
    @Symbol      NVARCHAR(50),
    @Side        NVARCHAR(10),
    @Strategy    NVARCHAR(150) = NULL,
    @Price       DECIMAL(18,8),
    @AiScore     REAL,
    @Time        DATETIME2(7),
    @PnL         DECIMAL(18,8),
    @PnLPercent  DECIMAL(18,8),
    @EntryPrice  DECIMAL(18,8),
    @ExitPrice   DECIMAL(18,8) = NULL,
    @Quantity    DECIMAL(18,8) = NULL,
    @ExitReason  NVARCHAR(255) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('dbo.TradeLogs', 'U') IS NULL RETURN;

    IF COL_LENGTH('dbo.TradeLogs', 'UserId') IS NOT NULL
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM dbo.TradeLogs
            WHERE UserId = @UserId
              AND Symbol = @Symbol
              AND Side = @Side
              AND ISNULL(Strategy, '') = ISNULL(@Strategy, '')
              AND ABS(CAST(Price AS FLOAT) - CAST(@Price AS FLOAT)) < 0.0000001
              AND ABS(CAST(PnL AS FLOAT) - CAST(@PnL AS FLOAT)) < 0.0000001
              AND [Time] >= DATEADD(SECOND, -3, @Time)
              AND [Time] <= DATEADD(SECOND, 3, @Time)
        )
        BEGIN
            INSERT INTO dbo.TradeLogs
                (UserId, Symbol, Side, Strategy, Price, AiScore, [Time], PnL, PnLPercent, EntryPrice, ExitPrice, Quantity, ExitReason)
            VALUES
                (@UserId, @Symbol, @Side, @Strategy, @Price, @AiScore, @Time, @PnL, @PnLPercent, @EntryPrice, @ExitPrice, @Quantity, @ExitReason);
        END
    END
    ELSE
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM dbo.TradeLogs
            WHERE Symbol = @Symbol
              AND Side = @Side
              AND ISNULL(Strategy, '') = ISNULL(@Strategy, '')
              AND ABS(CAST(Price AS FLOAT) - CAST(@Price AS FLOAT)) < 0.0000001
              AND ABS(CAST(PnL AS FLOAT) - CAST(@PnL AS FLOAT)) < 0.0000001
              AND [Time] >= DATEADD(SECOND, -3, @Time)
              AND [Time] <= DATEADD(SECOND, 3, @Time)
        )
        BEGIN
            INSERT INTO dbo.TradeLogs
                (Symbol, Side, Strategy, Price, AiScore, [Time], PnL, PnLPercent, EntryPrice, ExitPrice, Quantity, ExitReason)
            VALUES
                (@Symbol, @Side, @Strategy, @Price, @AiScore, @Time, @PnL, @PnLPercent, @EntryPrice, @ExitPrice, @Quantity, @ExitReason);
        END
    END
END";

        // sp_EnsureOpenTradeForPosition — UPDATE existing or INSERT new (OUTPUT 파라미터)
        private const string sp_EnsureOpenTradeForPosition = @"
CREATE OR ALTER PROCEDURE dbo.sp_EnsureOpenTradeForPosition
    @UserId        INT,
    @Symbol        NVARCHAR(32),
    @Side          NVARCHAR(8),
    @Strategy      NVARCHAR(64),
    @EntryPrice    DECIMAL(18,8),
    @Quantity      DECIMAL(18,8),
    @AiScore       REAL,
    @EntryTime     DATETIME2(7),
    @Category      NVARCHAR(32) = NULL,
    @OutEntryTime  DATETIME2(7) OUTPUT,
    @OutAiScore    REAL OUTPUT,
    @OutCreated    BIT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @TradeId           INT          = NULL;
    DECLARE @ExistingEntryTime DATETIME2(7) = NULL;
    DECLARE @ExistingAiScore   REAL         = 0;

    SELECT TOP (1)
           @TradeId           = Id,
           @ExistingEntryTime = EntryTime,
           @ExistingAiScore   = AiScore
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
    ORDER BY EntryTime DESC, Id DESC;

    IF @TradeId IS NOT NULL
    BEGIN
        UPDATE dbo.TradeHistory
        SET Side       = @Side,
            Strategy   = CASE WHEN NULLIF(LTRIM(RTRIM(Strategy)), '') IS NULL THEN @Strategy ELSE Strategy END,
            EntryPrice = @EntryPrice,
            Quantity   = @Quantity,
            AiScore    = CASE WHEN AiScore = 0 AND @AiScore <> 0 THEN @AiScore ELSE AiScore END,
            LastUpdatedAt = GETDATE()
        WHERE Id = @TradeId;

        SET @OutEntryTime = CASE WHEN @ExistingEntryTime IS NULL OR @ExistingEntryTime <= '0001-01-02' THEN @EntryTime ELSE @ExistingEntryTime END;
        SET @OutAiScore   = CASE WHEN @ExistingAiScore <> 0 THEN @ExistingAiScore ELSE @AiScore END;
        SET @OutCreated   = 0;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.TradeHistory
            (UserId, Symbol, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime,
             ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, CloseVerified, Category, LastUpdatedAt)
        VALUES
            (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime,
             NULL, 0, 0, NULL, 0, 0, @Category, GETDATE());

        SET @OutEntryTime = @EntryTime;
        SET @OutAiScore   = @AiScore;
        SET @OutCreated   = 1;
    END
END";

        // sp_RecordPartialClose — 복잡 부분청산 (OUTPUT)
        // OutResultCase: 0=duplicate skip, 1=full close (update existing), 2=partial (insert+update remaining), 3=no open trade fallback insert
        private const string sp_RecordPartialClose = @"
CREATE OR ALTER PROCEDURE dbo.sp_RecordPartialClose
    @UserId               INT,
    @Symbol               NVARCHAR(32),
    @CloseQty             DECIMAL(18,8),
    @ExitPrice            DECIMAL(18,8),
    @ExitTime             DATETIME2(7),
    @ExitReason           NVARCHAR(255),
    @PnL                  DECIMAL(18,8),
    @PnLPercent           DECIMAL(18,4),
    @AiScore              REAL,
    @IsSimulation         BIT,
    @InferredEntrySide    NVARCHAR(8),
    @LogStrategy          NVARCHAR(150),
    @Category             NVARCHAR(32),
    @OutResultCase        TINYINT       OUTPUT,
    @OutResolvedEntryPrice DECIMAL(18,8) OUTPUT,
    @OutResolvedEntryTime  DATETIME2(7)  OUTPUT,
    @OutResolvedAiScore    REAL          OUTPUT,
    @OutResolvedEntrySide  NVARCHAR(8)   OUTPUT,
    @OutResolvedStrategy   NVARCHAR(150) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @OutResultCase = 0;
    SET @OutResolvedEntryPrice = 0;
    SET @OutResolvedEntryTime  = @ExitTime;
    SET @OutResolvedAiScore    = @AiScore;
    SET @OutResolvedEntrySide  = @InferredEntrySide;
    SET @OutResolvedStrategy   = @LogStrategy;

    -- 1) 중복 체크
    IF EXISTS (
        SELECT 1
        FROM dbo.TradeHistory
        WHERE UserId = @UserId
          AND Symbol = @Symbol
          AND IsClosed = 1
          AND CloseVerified = 1
          AND ExitReason = @ExitReason
          AND ABS(Quantity   - @CloseQty)   < 0.000001
          AND ABS(ExitPrice  - @ExitPrice)  < 0.000001
          AND ExitTime >= DATEADD(SECOND, -5, @ExitTime)
          AND ExitTime <= DATEADD(SECOND,  5, @ExitTime)
    )
    BEGIN
        SET @OutResultCase = 0;
        RETURN;
    END

    -- 2) open trade 조회
    DECLARE @OpenId         INT          = NULL;
    DECLARE @OpenSide       NVARCHAR(8)  = NULL;
    DECLARE @OpenStrategy   NVARCHAR(150) = NULL;
    DECLARE @OpenEntryPrice DECIMAL(18,8) = 0;
    DECLARE @OpenQuantity   DECIMAL(18,8) = 0;
    DECLARE @OpenAiScore    REAL          = 0;
    DECLARE @OpenEntryTime  DATETIME2(7)  = NULL;

    SELECT TOP (1)
           @OpenId         = Id,
           @OpenSide       = Side,
           @OpenStrategy   = Strategy,
           @OpenEntryPrice = EntryPrice,
           @OpenQuantity   = Quantity,
           @OpenAiScore    = AiScore,
           @OpenEntryTime  = EntryTime
    FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
    ORDER BY EntryTime DESC, Id DESC;

    DECLARE @ResolvedSide       NVARCHAR(8)   = ISNULL(NULLIF(@OpenSide, ''), @InferredEntrySide);
    DECLARE @ResolvedStrategy   NVARCHAR(150) = ISNULL(NULLIF(LTRIM(RTRIM(@OpenStrategy)), ''), @LogStrategy);
    DECLARE @ResolvedAiScore    REAL          = CASE WHEN @OpenAiScore <> 0 THEN @OpenAiScore ELSE @AiScore END;
    DECLARE @ResolvedEntryPrice DECIMAL(18,8) = CASE WHEN @OpenEntryPrice > 0 THEN @OpenEntryPrice ELSE 0 END;
    DECLARE @ResolvedEntryTime  DATETIME2(7)  = CASE WHEN @OpenEntryTime IS NOT NULL AND @OpenEntryTime > '0001-01-02' THEN @OpenEntryTime ELSE @ExitTime END;

    -- 3) open 없을 때 fallback insert (case 3)
    IF @OpenId IS NULL
    BEGIN
        INSERT INTO dbo.TradeHistory
            (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
        VALUES
            (@UserId, @Symbol, @ResolvedSide, @ResolvedStrategy, @ResolvedEntryPrice, @ExitPrice, @CloseQty, @ResolvedAiScore, @PnL, @PnLPercent, @ExitReason, @ResolvedEntryTime, @ExitTime, 1, 1, @IsSimulation, @Category, GETDATE());

        SET @OutResultCase         = 3;
        SET @OutResolvedEntryPrice = @ResolvedEntryPrice;
        SET @OutResolvedEntryTime  = @ResolvedEntryTime;
        SET @OutResolvedAiScore    = @ResolvedAiScore;
        SET @OutResolvedEntrySide  = @ResolvedSide;
        SET @OutResolvedStrategy   = @ResolvedStrategy;
        RETURN;
    END

    DECLARE @RemainingQty DECIMAL(18,8) = CASE WHEN (@OpenQuantity - @CloseQty) > 0 THEN (@OpenQuantity - @CloseQty) ELSE 0 END;

    -- 4) 잔량 0 → 기존 row 전량 청산 update (case 1)
    IF @RemainingQty <= 0.000001
    BEGIN
        UPDATE dbo.TradeHistory
        SET ExitPrice    = @ExitPrice,
            Quantity     = @CloseQty,
            AiScore      = CASE WHEN @ResolvedAiScore <> 0 THEN @ResolvedAiScore ELSE AiScore END,
            PnL          = @PnL,
            PnLPercent   = @PnLPercent,
            ExitReason   = @ExitReason,
            ExitTime     = @ExitTime,
            IsClosed     = 1,
            CloseVerified = 1,
            IsSimulation = @IsSimulation,
            LastUpdatedAt = GETDATE()
        WHERE Id = @OpenId;

        SET @OutResultCase         = 1;
        SET @OutResolvedEntryPrice = @ResolvedEntryPrice;
        SET @OutResolvedEntryTime  = @ResolvedEntryTime;
        SET @OutResolvedAiScore    = @ResolvedAiScore;
        SET @OutResolvedEntrySide  = @ResolvedSide;
        SET @OutResolvedStrategy   = @ResolvedStrategy;
        RETURN;
    END

    -- 5) 부분청산: 새 closed row insert + 기존 open row 잔량 update (case 2)
    INSERT INTO dbo.TradeHistory
        (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
    VALUES
        (@UserId, @Symbol, @ResolvedSide, @ResolvedStrategy, @ResolvedEntryPrice, @ExitPrice, @CloseQty, @ResolvedAiScore, @PnL, @PnLPercent, @ExitReason, @ResolvedEntryTime, @ExitTime, 1, 1, @IsSimulation, @Category, GETDATE());

    UPDATE dbo.TradeHistory
    SET Quantity      = @RemainingQty,
        LastUpdatedAt = GETDATE()
    WHERE Id = @OpenId;

    SET @OutResultCase         = 2;
    SET @OutResolvedEntryPrice = @ResolvedEntryPrice;
    SET @OutResolvedEntryTime  = @ResolvedEntryTime;
    SET @OutResolvedAiScore    = @ResolvedAiScore;
    SET @OutResolvedEntrySide  = @ResolvedSide;
    SET @OutResolvedStrategy   = @ResolvedStrategy;
END";

        // sp_InsertClosedTrade — Complete fallback INSERT (open 없을 때 1줄 closed row)
        private const string sp_InsertClosedTrade = @"
CREATE OR ALTER PROCEDURE dbo.sp_InsertClosedTrade
    @UserId       INT,
    @Symbol       NVARCHAR(32),
    @Side         NVARCHAR(8),
    @Strategy     NVARCHAR(150) = NULL,
    @EntryPrice   DECIMAL(18,8),
    @ExitPrice    DECIMAL(18,8),
    @Quantity     DECIMAL(18,8),
    @AiScore      REAL,
    @PnL          DECIMAL(18,8),
    @PnLPercent   DECIMAL(18,4),
    @ExitReason   NVARCHAR(255) = NULL,
    @EntryTime    DATETIME2(7),
    @ExitTime     DATETIME2(7),
    @IsSimulation BIT = 0,
    @Category     NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.TradeHistory
        (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
    VALUES
        (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, @IsSimulation, @Category, GETDATE());
END";

        // sp_GetAllCandleDataForTraining — ActiveSymbols CTE + RankedCandles
        private const string sp_GetAllCandleDataForTraining = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetAllCandleDataForTraining
    @Limit INT
AS
BEGIN
    SET NOCOUNT ON;

    WITH ActiveSymbols AS (
        SELECT Symbol
        FROM dbo.MarketCandles WITH (NOLOCK)
        WHERE Symbol LIKE '%USDT'
        GROUP BY Symbol
        HAVING MAX(OpenTime) >= DATEADD(HOUR, -24, GETUTCDATE())
    ),
    RankedCandles AS (
        SELECT
            mc.Symbol,
            mc.OpenTime,
            mc.OpenPrice  AS [Open],
            mc.HighPrice  AS [High],
            mc.LowPrice   AS [Low],
            mc.ClosePrice AS [Close],
            CAST(mc.Volume AS float) AS Volume,
            mc.RSI, mc.MACD, mc.MACD_Signal, mc.MACD_Hist, mc.ATR, mc.BollingerUpper, mc.BollingerLower,
            ROW_NUMBER() OVER (PARTITION BY mc.Symbol ORDER BY mc.OpenTime DESC) AS rn
        FROM dbo.MarketCandles mc WITH (NOLOCK)
        INNER JOIN ActiveSymbols a ON mc.Symbol = a.Symbol
    )
    SELECT *
    FROM RankedCandles
    WHERE rn <= @Limit
    ORDER BY Symbol, OpenTime ASC;
END";

        // sp_GetBulkCandleData — RankedCandles + 선택적 Symbols CSV 필터
        private const string sp_GetBulkCandleData = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetBulkCandleData
    @SymbolsCsv NVARCHAR(MAX) = NULL,
    @Limit      INT
AS
BEGIN
    SET NOCOUNT ON;

    WITH RankedCandles AS (
        SELECT
            Symbol,
            OpenTime,
            OpenPrice  AS [Open],
            HighPrice  AS [High],
            LowPrice   AS [Low],
            ClosePrice AS [Close],
            CAST(Volume AS float) AS Volume,
            RSI, MACD, MACD_Signal, MACD_Hist, ATR, BollingerUpper, BollingerLower,
            ROW_NUMBER() OVER (PARTITION BY Symbol ORDER BY OpenTime DESC) AS rn
        FROM dbo.MarketCandles WITH (NOLOCK)
        WHERE Symbol LIKE '%USDT'
          AND (@SymbolsCsv IS NULL OR LEN(@SymbolsCsv) = 0
               OR Symbol IN (SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@SymbolsCsv, ',')))
    )
    SELECT *
    FROM RankedCandles
    WHERE rn <= @Limit
    ORDER BY Symbol, OpenTime ASC;
END";
    }
}
