using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.IO;
using TradingBot.Models;
using TradingBot; // [수정] MainWindow 접근을 위해 추가
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    public class DbManager
    {
        private readonly string _connectionString;

        private sealed class TradeHistoryOpenRow
        {
            public int Id { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public string Strategy { get; set; } = string.Empty;
            public decimal EntryPrice { get; set; }
            public decimal Quantity { get; set; }
            public float AiScore { get; set; }
            public DateTime EntryTime { get; set; }
        }

        public DbManager(string connectionString)
        {
            _connectionString = connectionString;
        }

        private static int GetCurrentUserId() => AppConfig.CurrentUser?.Id ?? 0;

        private static string TrimForDb(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private static float SanitizeFloatForDb(float value, float fallback = 0f)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return fallback;

            return value;
        }

        private static string InferEntrySideFromCloseSide(string? closeSide)
        {
            return string.Equals(closeSide, "SELL", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
        }

        private async Task<string> ResolveCloseSymbolAsync(
            SqlConnection db,
            SqlTransaction tx,
            int userId,
            TradeLog log,
            string closeSide)
        {
            string directSymbol = TrimForDb(log.Symbol, 50);
            if (!string.IsNullOrWhiteSpace(directSymbol))
                return directSymbol;

            string inferredEntrySide = InferEntrySideFromCloseSide(closeSide);
            decimal targetEntryPrice = log.EntryPrice > 0 ? log.EntryPrice : 0m;
            decimal targetQuantity = Math.Abs(log.Quantity);
            DateTime? targetEntryTime = log.EntryTime == default ? null : log.EntryTime;

            string? symbolByStrictMatch = await db.QueryFirstOrDefaultAsync<string>(@"
SELECT TOP (1) Symbol
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId
  AND IsClosed = 0
  AND Side = @Side
  AND (@EntryPrice <= 0 OR ABS(CAST(EntryPrice AS FLOAT) - CAST(@EntryPrice AS FLOAT)) <= ABS(CAST(@EntryPrice AS FLOAT)) * 0.02)
  AND (@Quantity <= 0 OR ABS(CAST(Quantity AS FLOAT) - CAST(@Quantity AS FLOAT)) <= ABS(CAST(@Quantity AS FLOAT)) * 0.30)
ORDER BY
  CASE WHEN @EntryTime IS NULL THEN 0 ELSE ABS(DATEDIFF(SECOND, EntryTime, @EntryTime)) END,
  Id DESC;",
                new
                {
                    UserId = userId,
                    Side = inferredEntrySide,
                    EntryPrice = targetEntryPrice,
                    Quantity = targetQuantity,
                    EntryTime = targetEntryTime
                }, tx);

            string resolvedSymbol = TrimForDb(symbolByStrictMatch, 50);
            if (!string.IsNullOrWhiteSpace(resolvedSymbol))
            {
                MainWindow.Instance?.AddLog($"ℹ️ [DB][Symbol복원] strict 매칭으로 심볼 복원: {resolvedSymbol}");
                return resolvedSymbol;
            }

            string? symbolBySide = await db.QueryFirstOrDefaultAsync<string>(@"
SELECT TOP (1) Symbol
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId
  AND IsClosed = 0
  AND Side = @Side
ORDER BY EntryTime DESC, Id DESC;",
                new
                {
                    UserId = userId,
                    Side = inferredEntrySide
                }, tx);

            resolvedSymbol = TrimForDb(symbolBySide, 50);
            if (!string.IsNullOrWhiteSpace(resolvedSymbol))
            {
                MainWindow.Instance?.AddLog($"ℹ️ [DB][Symbol복원] side 매칭으로 심볼 복원: {resolvedSymbol}");
                return resolvedSymbol;
            }

            var onlyOneOpen = await db.QueryFirstOrDefaultAsync<string>(@"
SELECT TOP (1) Symbol
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId
  AND IsClosed = 0
ORDER BY EntryTime DESC, Id DESC;",
                new { UserId = userId }, tx);

            resolvedSymbol = TrimForDb(onlyOneOpen, 50);
            if (!string.IsNullOrWhiteSpace(resolvedSymbol))
            {
                MainWindow.Instance?.AddLog($"ℹ️ [DB][Symbol복원] open 포지션 최신건으로 심볼 복원: {resolvedSymbol}");
                return resolvedSymbol;
            }

            return string.Empty;
        }

        private static bool IsStopLossReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            string text = reason.Trim().ToLowerInvariant();
            return text.Contains("손절")
                || text.Contains("stop")
                || text.Contains("sl");
        }

        private static bool IsTakeProfitReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            string text = reason.Trim().ToLowerInvariant();
            return text.Contains("익절")
                || text.Contains("takeprofit")
                || text.Contains("take profit")
                || text.Contains("tp")
                || text.Contains("profit run");
        }

        private async Task EnsureTradeHistorySchemaAsync(SqlConnection db, SqlTransaction? tx = null)
        {
            string sql = @"
IF OBJECT_ID('dbo.TradeHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TradeHistory (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        UserId INT NOT NULL CONSTRAINT DF_TradeHistory_UserId DEFAULT 0,
        Symbol NVARCHAR(50) NOT NULL,
        Side NVARCHAR(10) NOT NULL,
        Strategy NVARCHAR(150) NULL,
        EntryPrice DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeHistory_EntryPrice DEFAULT 0,
        ExitPrice DECIMAL(18,8) NULL,
        Quantity DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeHistory_Quantity DEFAULT 0,
        AiScore REAL NOT NULL CONSTRAINT DF_TradeHistory_AiScore DEFAULT 0,
        PnL DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeHistory_PnL DEFAULT 0,
        PnLPercent DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeHistory_PnLPercent DEFAULT 0,
        ExitReason NVARCHAR(255) NULL,
        EntryTime DATETIME2 NOT NULL CONSTRAINT DF_TradeHistory_EntryTime DEFAULT GETDATE(),
        ExitTime DATETIME2 NULL,
        IsClosed BIT NOT NULL CONSTRAINT DF_TradeHistory_IsClosed DEFAULT 1,
        CloseVerified BIT NOT NULL CONSTRAINT DF_TradeHistory_CloseVerified DEFAULT 1,
        LastUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_TradeHistory_LastUpdatedAt DEFAULT GETDATE()
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'UserId')
    ALTER TABLE dbo.TradeHistory ADD UserId INT NOT NULL CONSTRAINT DF_TradeHistory_UserId_Legacy DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'Strategy')
    ALTER TABLE dbo.TradeHistory ADD Strategy NVARCHAR(150) NULL;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'Symbol'
      AND max_length <> -1
      AND max_length < 100)
BEGIN
    ALTER TABLE dbo.TradeHistory ALTER COLUMN Symbol NVARCHAR(50) NOT NULL;
END

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'Side'
      AND max_length <> -1
      AND max_length < 20)
BEGIN
    ALTER TABLE dbo.TradeHistory ALTER COLUMN Side NVARCHAR(10) NOT NULL;
END

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'Strategy'
      AND max_length <> -1
      AND max_length < 300)
BEGIN
    ALTER TABLE dbo.TradeHistory ALTER COLUMN Strategy NVARCHAR(150) NULL;
END

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'ExitReason'
      AND max_length <> -1
      AND max_length < 510)
BEGIN
    ALTER TABLE dbo.TradeHistory ALTER COLUMN ExitReason NVARCHAR(255) NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'AiScore')
    ALTER TABLE dbo.TradeHistory ADD AiScore REAL NOT NULL CONSTRAINT DF_TradeHistory_AiScore_Legacy DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'IsClosed')
    ALTER TABLE dbo.TradeHistory ADD IsClosed BIT NOT NULL CONSTRAINT DF_TradeHistory_IsClosed_Legacy DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'CloseVerified')
    ALTER TABLE dbo.TradeHistory ADD CloseVerified BIT NOT NULL CONSTRAINT DF_TradeHistory_CloseVerified_Legacy DEFAULT 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'LastUpdatedAt')
    ALTER TABLE dbo.TradeHistory ADD LastUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_TradeHistory_LastUpdatedAt_Legacy DEFAULT GETDATE();

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'ExitPrice'
      AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.TradeHistory ALTER COLUMN ExitPrice DECIMAL(18,8) NULL;
END

IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'ExitTime'
      AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.TradeHistory ALTER COLUMN ExitTime DATETIME2 NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TradeHistory')
      AND name = 'IX_TradeHistory_UserId_IsClosed_Symbol_EntryTime')
BEGIN
    CREATE INDEX IX_TradeHistory_UserId_IsClosed_Symbol_EntryTime
    ON dbo.TradeHistory(UserId, IsClosed, Symbol, EntryTime DESC);
END";

            await db.ExecuteAsync(sql, transaction: tx);
        }

        private async Task EnsureTradeLogsSchemaAsync(SqlConnection db, SqlTransaction? tx = null)
        {
            string sql = @"
IF OBJECT_ID('dbo.TradeLogs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TradeLogs (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Symbol NVARCHAR(50) NOT NULL,
        Side NVARCHAR(10) NOT NULL,
        Strategy NVARCHAR(150) NULL,
        Price DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeLogs_Price DEFAULT 0,
        AiScore FLOAT NOT NULL CONSTRAINT DF_TradeLogs_AiScore DEFAULT 0,
        [Time] DATETIME2 NOT NULL CONSTRAINT DF_TradeLogs_Time DEFAULT GETDATE(),
        PnL DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeLogs_PnL DEFAULT 0,
        PnLPercent DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradeLogs_PnLPercent DEFAULT 0,
        EntryPrice DECIMAL(18,8) NULL,
        ExitPrice DECIMAL(18,8) NULL,
        Quantity DECIMAL(18,8) NULL,
        ExitReason NVARCHAR(255) NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeLogs') AND name = 'EntryPrice')
    ALTER TABLE dbo.TradeLogs ADD EntryPrice DECIMAL(18,8) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeLogs') AND name = 'ExitPrice')
    ALTER TABLE dbo.TradeLogs ADD ExitPrice DECIMAL(18,8) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeLogs') AND name = 'Quantity')
    ALTER TABLE dbo.TradeLogs ADD Quantity DECIMAL(18,8) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeLogs') AND name = 'ExitReason')
    ALTER TABLE dbo.TradeLogs ADD ExitReason NVARCHAR(255) NULL;

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeLogs')
      AND name = 'Symbol'
      AND max_length <> -1
      AND max_length < 100)
BEGIN
    ALTER TABLE dbo.TradeLogs ALTER COLUMN Symbol NVARCHAR(50) NOT NULL;
END

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeLogs')
      AND name = 'Side'
      AND max_length <> -1
      AND max_length < 20)
BEGIN
    ALTER TABLE dbo.TradeLogs ALTER COLUMN Side NVARCHAR(10) NOT NULL;
END

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeLogs')
      AND name = 'Strategy'
      AND max_length <> -1
      AND max_length < 300)
BEGIN
    ALTER TABLE dbo.TradeLogs ALTER COLUMN Strategy NVARCHAR(150) NULL;
END

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeLogs')
      AND name = 'ExitReason'
      AND max_length <> -1
      AND max_length < 510)
BEGIN
    ALTER TABLE dbo.TradeLogs ALTER COLUMN ExitReason NVARCHAR(255) NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TradeLogs')
      AND name = 'IX_TradeLogs_Symbol_Time')
BEGIN
    CREATE INDEX IX_TradeLogs_Symbol_Time
    ON dbo.TradeLogs(Symbol, [Time] DESC);
END";

            await db.ExecuteAsync(sql, transaction: tx);
        }

        private async Task TryMirrorToTradeLogsAsync(
            string? symbol,
            string? side,
            string? strategy,
            decimal price,
            float aiScore,
            DateTime time,
            decimal pnl,
            decimal pnlPercent,
            decimal entryPrice,
            decimal exitPrice,
            decimal quantity,
            string? exitReason)
        {
            try
            {
                string symbolValue = TrimForDb(symbol, 50);
                if (string.IsNullOrWhiteSpace(symbolValue))
                    return;

                string sideValue = TrimForDb(side, 10);
                string strategyValue = TrimForDb(strategy, 150);
                string exitReasonValue = TrimForDb(exitReason, 255);
                float aiScoreValue = SanitizeFloatForDb(aiScore);
                DateTime timeValue = time == default ? DateTime.Now : time;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await EnsureTradeLogsSchemaAsync(db);

                string sql = @"
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
END";

                await db.ExecuteAsync(sql, new
                {
                    Symbol = symbolValue,
                    Side = sideValue,
                    Strategy = strategyValue,
                    Price = price,
                    AiScore = aiScoreValue,
                    Time = timeValue,
                    PnL = pnl,
                    PnLPercent = pnlPercent,
                    EntryPrice = entryPrice,
                    ExitPrice = exitPrice > 0 ? exitPrice : (decimal?)null,
                    Quantity = quantity > 0 ? quantity : (decimal?)null,
                    ExitReason = string.IsNullOrWhiteSpace(exitReasonValue) ? null : exitReasonValue
                });
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] TradeLogs 미러 저장 실패: {ex.Message}");
            }
        }

        private async Task EnsureTradePatternSchemaAsync(SqlConnection db, SqlTransaction? tx = null)
        {
            string sql = @"
IF OBJECT_ID('dbo.TradePatternSnapshots', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TradePatternSnapshots (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        UserId INT NOT NULL CONSTRAINT DF_TradePatternSnapshots_UserId DEFAULT 0,
        Symbol NVARCHAR(30) NOT NULL,
        Side NVARCHAR(10) NOT NULL,
        Strategy NVARCHAR(120) NULL,
        Mode NVARCHAR(20) NULL,
        EntryTime DATETIME2 NOT NULL,
        ExitTime DATETIME2 NULL,
        EntryPrice DECIMAL(18,8) NOT NULL CONSTRAINT DF_TradePatternSnapshots_EntryPrice DEFAULT 0,

        FinalScore FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_FinalScore DEFAULT 0,
        AiScore FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_AiScore DEFAULT 0,
        ElliottScore FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_ElliottScore DEFAULT 0,
        VolumeScore FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_VolumeScore DEFAULT 0,
        RsiMacdScore FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_RsiMacdScore DEFAULT 0,
        BollingerScore FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_BollingerScore DEFAULT 0,
        PredictedChangePct FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_PredictedChangePct DEFAULT 0,
        ScoreGap FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_ScoreGap DEFAULT 0,

        AtrPercent FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_AtrPercent DEFAULT 0,
        HtfPenalty FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_HtfPenalty DEFAULT 0,
        Adx FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_Adx DEFAULT 0,
        PlusDi FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_PlusDi DEFAULT 0,
        MinusDi FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_MinusDi DEFAULT 0,
        Rsi FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_Rsi DEFAULT 0,
        MacdHist FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_MacdHist DEFAULT 0,
        BbPosition FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_BbPosition DEFAULT 0,
        VolumeRatio FLOAT NOT NULL CONSTRAINT DF_TradePatternSnapshots_VolumeRatio DEFAULT 0,

        SimilarityScore FLOAT NULL,
        EuclideanSimilarity FLOAT NULL,
        CosineSimilarity FLOAT NULL,
        MatchProbability FLOAT NULL,
        MatchedPatternId BIGINT NULL,
        IsSuperEntry BIT NOT NULL CONSTRAINT DF_TradePatternSnapshots_IsSuperEntry DEFAULT 0,
        PositionSizeMultiplier DECIMAL(10,4) NOT NULL CONSTRAINT DF_TradePatternSnapshots_PositionSizeMultiplier DEFAULT 1.0,
        TakeProfitMultiplier DECIMAL(10,4) NOT NULL CONSTRAINT DF_TradePatternSnapshots_TakeProfitMultiplier DEFAULT 1.0,

        Label TINYINT NULL,
        PnL DECIMAL(18,8) NULL,
        PnLPercent DECIMAL(18,8) NULL,
        ExitReason NVARCHAR(255) NULL,
        ExitType NVARCHAR(20) NULL,

        ComponentMix NVARCHAR(500) NULL,
        ContextJson NVARCHAR(MAX) NULL,

        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_TradePatternSnapshots_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_TradePatternSnapshots_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TradePatternSnapshots')
      AND name = 'IX_TradePatternSnapshots_User_Symbol_Side_Label_Entry')
BEGIN
    CREATE INDEX IX_TradePatternSnapshots_User_Symbol_Side_Label_Entry
    ON dbo.TradePatternSnapshots(UserId, Symbol, Side, Label, EntryTime DESC);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TradePatternSnapshots')
      AND name = 'IX_TradePatternSnapshots_User_Symbol_Entry')
BEGIN
    CREATE INDEX IX_TradePatternSnapshots_User_Symbol_Entry
    ON dbo.TradePatternSnapshots(UserId, Symbol, EntryTime DESC);
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradePatternSnapshots') AND name = 'ExitReason')
    ALTER TABLE dbo.TradePatternSnapshots ADD ExitReason NVARCHAR(255) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradePatternSnapshots') AND name = 'ExitType')
    ALTER TABLE dbo.TradePatternSnapshots ADD ExitType NVARCHAR(20) NULL;
";

            await db.ExecuteAsync(sql, transaction: tx);
        }

        public async Task<long?> SaveTradePatternSnapshotAsync(TradePatternSnapshotRecord snapshot)
        {
            try
            {
                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ 패턴 저장: 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradePatternSchemaAsync(db, tx);

                string sql = @"
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
);";

                long id = await db.ExecuteScalarAsync<long>(sql, new
                {
                    UserId = userId,
                    snapshot.Symbol,
                    snapshot.Side,
                    snapshot.Strategy,
                    snapshot.Mode,
                    snapshot.EntryTime,
                    snapshot.EntryPrice,
                    snapshot.FinalScore,
                    snapshot.AiScore,
                    snapshot.ElliottScore,
                    snapshot.VolumeScore,
                    snapshot.RsiMacdScore,
                    snapshot.BollingerScore,
                    snapshot.PredictedChangePct,
                    snapshot.ScoreGap,
                    snapshot.AtrPercent,
                    snapshot.HtfPenalty,
                    snapshot.Adx,
                    snapshot.PlusDi,
                    snapshot.MinusDi,
                    snapshot.Rsi,
                    snapshot.MacdHist,
                    snapshot.BbPosition,
                    snapshot.VolumeRatio,
                    snapshot.SimilarityScore,
                    snapshot.EuclideanSimilarity,
                    snapshot.CosineSimilarity,
                    snapshot.MatchProbability,
                    snapshot.MatchedPatternId,
                    snapshot.IsSuperEntry,
                    snapshot.PositionSizeMultiplier,
                    snapshot.TakeProfitMultiplier,
                    snapshot.ComponentMix,
                    snapshot.ContextJson
                }, tx);

                await tx.CommitAsync();
                return id;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 패턴 스냅샷 저장 실패: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> CompleteTradePatternSnapshotAsync(string symbol, DateTime entryTime, DateTime exitTime, decimal pnl, decimal pnlPercent, string? exitReason = null)
        {
            try
            {
                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ 패턴 완성: 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradePatternSchemaAsync(db, tx);

                string normalizedExitReason = TrimForDb(exitReason, 255);
                bool isStopLoss = IsStopLossReason(normalizedExitReason);
                bool isTakeProfit = IsTakeProfitReason(normalizedExitReason);

                byte label = pnl > 0 ? (byte)1 : (byte)0;
                string exitType = pnl > 0 ? "TAKEPROFIT" : "STOPLOSS";

                if (isStopLoss)
                {
                    label = 0;
                    exitType = "STOPLOSS";
                }
                else if (isTakeProfit)
                {
                    label = 1;
                    exitType = "TAKEPROFIT";
                }

                string sql = @"
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
INNER JOIN TargetRow x ON x.Id = t.Id;";

                int affected = await db.ExecuteAsync(sql, new
                {
                    UserId = userId,
                    Symbol = symbol,
                    EntryTime = entryTime,
                    ExitTime = exitTime,
                    PnL = pnl,
                    PnLPercent = pnlPercent,
                    Label = label,
                    ExitReason = string.IsNullOrWhiteSpace(normalizedExitReason) ? null : normalizedExitReason,
                    ExitType = exitType
                }, tx);

                await tx.CommitAsync();
                return affected > 0;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 패턴 라벨 업데이트 실패: {ex.Message}");
                return false;
            }
        }

        public async Task<List<TradePatternSnapshotRecord>> GetLabeledTradePatternSnapshotsAsync(string symbol, string side, int lookbackDays = 120, int maxRows = 600)
        {
            try
            {
                int userId = GetCurrentUserId();
                if (userId <= 0 || string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(side))
                    return new List<TradePatternSnapshotRecord>();

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                await EnsureTradePatternSchemaAsync(db);

                string sql = @"
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
ORDER BY EntryTime DESC;";

                var rows = await db.QueryAsync<TradePatternSnapshotRecord>(sql, new
                {
                    UserId = userId,
                    Symbol = symbol,
                    Side = side,
                    LookbackDays = Math.Max(1, lookbackDays),
                    MaxRows = Math.Clamp(maxRows, 50, 3000)
                });

                return rows.ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 패턴 학습 조회 실패: {ex.Message}");
                return new List<TradePatternSnapshotRecord>();
            }
        }

        /// <summary>
        /// DB에서 IsClosed=0인 모든 오픈 포지션 조회 (봇 시작 시 거래소와 비교용)
        /// </summary>
        public async Task<List<(string Symbol, string Side, decimal EntryPrice, decimal Quantity, DateTime EntryTime)>> GetOpenTradesAsync(int userId)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                await EnsureTradeHistorySchemaAsync(db);

                string sql = @"
SELECT Symbol, Side, EntryPrice, Quantity, EntryTime
FROM dbo.TradeHistory
WHERE UserId = @UserId AND IsClosed = 0
ORDER BY EntryTime DESC;";

                var rows = await db.QueryAsync(sql, new { UserId = userId });
                
                var result = new List<(string Symbol, string Side, decimal EntryPrice, decimal Quantity, DateTime EntryTime)>();
                foreach (var row in rows)
                {
                    result.Add((
                        Symbol: (string)row.Symbol,
                        Side: (string)row.Side,
                        EntryPrice: (decimal)row.EntryPrice,
                        Quantity: (decimal)row.Quantity,
                        EntryTime: (DateTime)row.EntryTime
                    ));
                }

                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 오픈 포지션 조회 실패: {ex.Message}");
                return new List<(string Symbol, string Side, decimal EntryPrice, decimal Quantity, DateTime EntryTime)>();
            }
        }

        public async Task<bool> UpsertTradeEntryAsync(TradeLog log)
        {
            try
            {
                if (log == null)
                {
                    MainWindow.Instance?.AddLog("⚠️ UpsertTradeEntryAsync: log 객체가 null입니다");
                    return false;
                }

                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ [{log.Symbol}] 진입 이력: 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                decimal entryPrice = log.EntryPrice > 0 ? log.EntryPrice : log.Price;
                decimal quantity = Math.Abs(log.Quantity);
                DateTime entryTime = log.EntryTime == default ? log.Time : log.EntryTime;
                string strategy = string.IsNullOrWhiteSpace(log.Strategy) ? "UNKNOWN" : log.Strategy;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradeHistorySchemaAsync(db, tx);

                string updateSql = @"
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
    LastUpdatedAt = GETDATE(),
    CloseVerified = 0;";

                int affected = await db.ExecuteAsync(updateSql, new
                {
                    UserId = userId,
                    log.Symbol,
                    Side = log.Side,
                    Strategy = strategy,
                    EntryPrice = entryPrice,
                    Quantity = quantity,
                    log.AiScore,
                    EntryTime = entryTime
                }, tx);

                if (affected == 0)
                {
                    string insertSql = @"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, CloseVerified, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime, NULL, 0, 0, NULL, 0, 0, GETDATE());";

                    await db.ExecuteAsync(insertSql, new
                    {
                        UserId = userId,
                        log.Symbol,
                        Side = log.Side,
                        Strategy = strategy,
                        EntryPrice = entryPrice,
                        Quantity = quantity,
                        log.AiScore,
                        EntryTime = entryTime
                    }, tx);
                }

                await tx.CommitAsync();
                await TryMirrorToTradeLogsAsync(
                    log.Symbol,
                    log.Side,
                    strategy,
                    entryPrice,
                    log.AiScore,
                    entryTime,
                    0m,
                    0m,
                    entryPrice,
                    0m,
                    quantity,
                    null);
                MainWindow.Instance?.AddLog($"✅ [DB][TradeHistory][EntryUpsert] user={userId} sym={log.Symbol} side={log.Side} qty={quantity}");
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 진입 저장 실패] {ex.GetType().Name}: {ex.Message}");
                MainWindow.Instance?.AddAlert($"❌ [DB] 진입 저장 실패: {ex.Message}");
                return false;
            }
        }

        public async Task<(bool Success, DateTime EntryTime, float AiScore, bool Created)> EnsureOpenTradeForPositionAsync(PositionInfo position, string? fallbackStrategy = null)
        {
            try
            {
                if (position == null || string.IsNullOrWhiteSpace(position.Symbol))
                    return (false, DateTime.Now, 0f, false);

                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ [{position.Symbol}] 시작 포지션 보정: 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                string side = position.Side?.ToString()?.ToUpperInvariant() ?? string.Empty;
                if (side != "BUY" && side != "SELL")
                    side = position.IsLong ? "BUY" : "SELL";

                DateTime entryTime = position.EntryTime == default ? DateTime.Now : position.EntryTime;
                string strategy = string.IsNullOrWhiteSpace(fallbackStrategy) ? "SYNC_RESTORED" : fallbackStrategy;
                decimal quantity = Math.Abs(position.Quantity);

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradeHistorySchemaAsync(db, tx);

                var openTrade = await db.QueryFirstOrDefaultAsync<TradeHistoryOpenRow>(@"
SELECT TOP (1) Id, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
ORDER BY EntryTime DESC, Id DESC;",
                    new { UserId = userId, position.Symbol }, tx);

                if (openTrade != null)
                {
                    await db.ExecuteAsync(@"
UPDATE dbo.TradeHistory
SET Side = @Side,
    Strategy = CASE WHEN NULLIF(LTRIM(RTRIM(Strategy)), '') IS NULL THEN @Strategy ELSE Strategy END,
    EntryPrice = @EntryPrice,
    Quantity = @Quantity,
    AiScore = CASE WHEN AiScore = 0 AND @AiScore <> 0 THEN @AiScore ELSE AiScore END,
    LastUpdatedAt = GETDATE()
WHERE Id = @Id;",
                        new
                        {
                            Id = openTrade.Id,
                            Side = side,
                            Strategy = strategy,
                            EntryPrice = position.EntryPrice,
                            Quantity = quantity,
                            AiScore = position.AiScore
                        }, tx);

                    await tx.CommitAsync();
                    DateTime resolvedEntryTime = openTrade.EntryTime == default ? entryTime : openTrade.EntryTime;
                    float resolvedAiScore = openTrade.AiScore != 0 ? openTrade.AiScore : position.AiScore;
                    return (true, resolvedEntryTime, resolvedAiScore, false);
                }

                await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, CloseVerified, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime, NULL, 0, 0, NULL, 0, 0, GETDATE());",
                    new
                    {
                        UserId = userId,
                        position.Symbol,
                        Side = side,
                        Strategy = strategy,
                        EntryPrice = position.EntryPrice,
                        Quantity = quantity,
                        AiScore = position.AiScore,
                        EntryTime = entryTime
                    }, tx);

                await tx.CommitAsync();
                MainWindow.Instance?.AddLog($"📝 [DB] 시작 포지션 TradeHistory 보정 insert: U{userId} {position.Symbol} {side} Qty={quantity}");
                return (true, entryTime, position.AiScore, true);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 시작 포지션 보정 실패] {position?.Symbol}: {ex.Message}");
                DateTime fallbackEntryTime = position is { EntryTime: var posEntryTime } && posEntryTime != default
                    ? posEntryTime
                    : DateTime.Now;
                return (false, fallbackEntryTime, position?.AiScore ?? 0f, false);
            }
        }

        public async Task<bool> CompleteTradeAsync(TradeLog log)
        {
            try
            {
                if (log == null)
                {
                    MainWindow.Instance?.AddLog("⚠️ CompleteTradeAsync: log 객체가 null입니다");
                    return false;
                }

                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ [{log.Symbol}] 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                decimal exitPrice = log.ExitPrice > 0 ? log.ExitPrice : log.Price;
                decimal entryPrice = log.EntryPrice > 0 ? log.EntryPrice : 0m;
                decimal quantity = Math.Abs(log.Quantity);
                DateTime entryTime = log.EntryTime == default ? log.Time : log.EntryTime;
                DateTime exitTime = log.ExitTime == default ? log.Time : log.ExitTime;
                string sideValue = TrimForDb(log.Side, 10);
                if (string.IsNullOrWhiteSpace(sideValue))
                    sideValue = "SELL";

                string strategyValue = TrimForDb(log.Strategy, 150);
                string exitReason = string.IsNullOrWhiteSpace(log.ExitReason)
                    ? (string.IsNullOrWhiteSpace(strategyValue) ? "MarketClose" : strategyValue)
                    : TrimForDb(log.ExitReason, 255);
                float aiScoreValue = SanitizeFloatForDb(log.AiScore);

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradeHistorySchemaAsync(db, tx);

                string symbolValue = await ResolveCloseSymbolAsync(db, tx, userId, log, sideValue);
                if (string.IsNullOrWhiteSpace(symbolValue))
                {
                    MainWindow.Instance?.AddLog("⚠️ CompleteTradeAsync: Symbol 복원 실패로 저장을 진행할 수 없습니다");
                    MainWindow.Instance?.AddLog($"   - Side={sideValue}, EntryPrice={entryPrice:F8}, Qty={quantity}, EntryTime={entryTime:yyyy-MM-dd HH:mm:ss}");
                    MainWindow.Instance?.AddAlert($"❌ [DB] 청산 저장 실패: Symbol 복원 실패 (Side={sideValue}, Qty={quantity})");
                    await tx.RollbackAsync();
                    return false;
                }

                var openTrade = await db.QueryFirstOrDefaultAsync<TradeHistoryOpenRow>(@"
SELECT TOP (1) Id, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
ORDER BY EntryTime DESC, Id DESC;",
                    new { UserId = userId, Symbol = symbolValue }, tx);

                if (openTrade != null)
                {
                    string closeReasonStrategy = TrimForDb($"CLOSE:{exitReason}", 150);
                    await db.ExecuteAsync(@"
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
    LastUpdatedAt = GETDATE()
WHERE Id = @Id;",
                        new
                        {
                            Id = openTrade.Id,
                            ExitPrice = exitPrice,
                            Quantity = quantity,
                            AiScore = aiScoreValue,
                            log.PnL,
                            log.PnLPercent,
                            ExitReason = exitReason,
                            ExitTime = exitTime
                        }, tx);

                    await tx.CommitAsync();
                    await TryMirrorToTradeLogsAsync(
                        symbolValue,
                        sideValue,
                        closeReasonStrategy,
                        exitPrice,
                        aiScoreValue,
                        exitTime,
                        log.PnL,
                        log.PnLPercent,
                        openTrade.EntryPrice > 0 ? openTrade.EntryPrice : entryPrice,
                        exitPrice,
                        quantity > 0 ? quantity : openTrade.Quantity,
                        exitReason);
                    MainWindow.Instance?.AddLog($"✅ [DB][TradeHistory][CloseUpdate] user={userId} sym={symbolValue} exit={exitPrice:F4} pnl={log.PnL:F2} reason={exitReason}");
                    return true;
                }

                // 열린 진입건이 없을 때 INSERT로 보정
                MainWindow.Instance?.AddLog($"⚠️ [DB][TradeHistory][CloseFallback] user={userId} sym={symbolValue} openEntry=notFound action=insertRecovery");
                string entrySide = InferEntrySideFromCloseSide(sideValue);
                string fallbackStrategy = string.IsNullOrWhiteSpace(strategyValue) ? "RECOVERED_CLOSE" : strategyValue;

                await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, GETDATE());",
                    new
                    {
                        UserId = userId,
                        Symbol = symbolValue,
                        Side = entrySide,
                        Strategy = fallbackStrategy,
                        EntryPrice = entryPrice,
                        ExitPrice = exitPrice,
                        Quantity = quantity,
                        AiScore = aiScoreValue,
                        log.PnL,
                        log.PnLPercent,
                        ExitReason = exitReason,
                        EntryTime = entryTime,
                        ExitTime = exitTime
                    }, tx);

                await tx.CommitAsync();
                await TryMirrorToTradeLogsAsync(
                    symbolValue,
                    sideValue,
                    TrimForDb($"CLOSE:{exitReason}", 150),
                    exitPrice,
                    aiScoreValue,
                    exitTime,
                    log.PnL,
                    log.PnLPercent,
                    entryPrice,
                    exitPrice,
                    quantity,
                    exitReason);
                MainWindow.Instance?.AddLog($"⚠️ [DB][TradeHistory][CloseInserted] user={userId} sym={symbolValue} reason={exitReason}");
                return true;
            }
            catch (Exception ex)
            {
                string detailMsg = $"Type={ex.GetType().Name}, Msg={ex.Message}";
                if (ex.InnerException != null)
                    detailMsg += $", Inner={ex.InnerException.Message}";
                MainWindow.Instance?.AddLog($"❌ [DB 청산 저장 실패] {log?.Symbol ?? "Unknown"} | {detailMsg}");
                MainWindow.Instance?.AddLog($"   - ExitReason: {log?.ExitReason ?? "N/A"}, PnL: {log?.PnL ?? 0}");
                MainWindow.Instance?.AddLog($"   - ExitReasonLen: {log?.ExitReason?.Length ?? 0}, AiScore: {(log == null ? 0 : log.AiScore)}");
                MainWindow.Instance?.AddAlert($"❌ [DB] 청산 저장 실패: {log?.Symbol ?? "Unknown"} | {ex.Message}");
                return false;
            }
        }

        public async Task<bool> TryCompleteOpenTradeAsync(TradeLog log)
        {
            try
            {
                if (log == null)
                {
                    MainWindow.Instance?.AddLog("⚠️ TryCompleteOpenTradeAsync: log 객체가 null입니다");
                    return false;
                }

                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ [{log.Symbol}] 외부 청산 동기화: 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                decimal exitPrice = log.ExitPrice > 0 ? log.ExitPrice : log.Price;
                decimal quantity = Math.Abs(log.Quantity);
                DateTime exitTime = log.ExitTime == default ? log.Time : log.ExitTime;
                string exitReason = string.IsNullOrWhiteSpace(log.ExitReason) ? log.Strategy ?? "EXTERNAL_CLOSE_SYNC" : log.ExitReason;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradeHistorySchemaAsync(db, tx);

                var openTrade = await db.QueryFirstOrDefaultAsync<TradeHistoryOpenRow>(@"
SELECT TOP (1) Id, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
ORDER BY EntryTime DESC, Id DESC;",
                    new { UserId = userId, log.Symbol }, tx);

                if (openTrade == null)
                {
                    string entrySide = InferEntrySideFromCloseSide(log.Side);
                    string fallbackStrategy = string.IsNullOrWhiteSpace(log.Strategy) ? "EXTERNAL_CLOSE_SYNC" : log.Strategy;
                    decimal entryPrice = log.EntryPrice > 0 ? log.EntryPrice : exitPrice;
                    DateTime entryTime = log.EntryTime == default ? exitTime : log.EntryTime;

                    await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, GETDATE());",
                        new
                        {
                            UserId = userId,
                            log.Symbol,
                            Side = entrySide,
                            Strategy = fallbackStrategy,
                            EntryPrice = entryPrice,
                            ExitPrice = exitPrice,
                            Quantity = quantity,
                            log.AiScore,
                            log.PnL,
                            log.PnLPercent,
                            ExitReason = exitReason,
                            EntryTime = entryTime,
                            ExitTime = exitTime
                        }, tx);

                    await tx.CommitAsync();
                    await TryMirrorToTradeLogsAsync(
                        log.Symbol,
                        log.Side,
                        TrimForDb($"CLOSE:{exitReason}", 150),
                        exitPrice,
                        log.AiScore,
                        exitTime,
                        log.PnL,
                        log.PnLPercent,
                        entryPrice,
                        exitPrice,
                        quantity,
                        exitReason);
                    MainWindow.Instance?.AddLog($"⚠️ [DB] 외부 청산 동기화: 열린 진입건 미발견 → 청산 insert 보정(U{userId}, {log.Symbol})");
                    return true;
                }

                decimal resolvedQuantity = quantity > 0 ? quantity : openTrade.Quantity;
                float resolvedAiScore = log.AiScore != 0 ? log.AiScore : openTrade.AiScore;

                await db.ExecuteAsync(@"
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
WHERE Id = @Id;",
                    new
                    {
                        Id = openTrade.Id,
                        ExitPrice = exitPrice,
                        Quantity = resolvedQuantity,
                        AiScore = resolvedAiScore,
                        log.PnL,
                        log.PnLPercent,
                        ExitReason = exitReason,
                        ExitTime = exitTime
                    }, tx);

                await tx.CommitAsync();
                await TryMirrorToTradeLogsAsync(
                    log.Symbol,
                    log.Side,
                    TrimForDb($"CLOSE:{exitReason}", 150),
                    exitPrice,
                    resolvedAiScore,
                    exitTime,
                    log.PnL,
                    log.PnLPercent,
                    openTrade.EntryPrice,
                    exitPrice,
                    resolvedQuantity,
                    exitReason);
                MainWindow.Instance?.AddLog($"✅ [DB] 외부 청산 동기화 완료: U{userId} {log.Symbol} Exit={exitPrice:F4}");
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 외부 청산 동기화 실패] {ex.GetType().Name}: {ex.Message}");
                MainWindow.Instance?.AddAlert($"❌ [DB] 외부 청산 동기화 실패: {log?.Symbol ?? "Unknown"} | {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RecordPartialCloseAsync(TradeLog log)
        {
            try
            {
                if (log == null)
                {
                    MainWindow.Instance?.AddLog("⚠️ RecordPartialCloseAsync: log 객체가 null입니다");
                    return false;
                }

                int userId = GetCurrentUserId();
                if (userId <= 0)
                {
                    userId = 1; // [개발용] 기본 사용자 ID 할당
                    MainWindow.Instance?.AddLog($"⚠️ [{log.Symbol}] 로그인 사용자 없음 → 기본 UserId=1로 저장 (개발 모드)");
                }

                decimal exitPrice = log.ExitPrice > 0 ? log.ExitPrice : log.Price;
                decimal entryPrice = log.EntryPrice > 0 ? log.EntryPrice : 0m;
                decimal closeQty = Math.Abs(log.Quantity);
                DateTime entryTime = log.EntryTime == default ? log.Time : log.EntryTime;
                DateTime exitTime = log.ExitTime == default ? log.Time : log.ExitTime;
                string exitReason = string.IsNullOrWhiteSpace(log.ExitReason) ? "PartialClose" : log.ExitReason;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                await EnsureTradeHistorySchemaAsync(db, tx);

                var duplicatedPartial = await db.ExecuteScalarAsync<int?>(@"
SELECT TOP (1) Id
FROM dbo.TradeHistory
WHERE UserId = @UserId
  AND Symbol = @Symbol
  AND IsClosed = 1
  AND CloseVerified = 1
  AND ExitReason = @ExitReason
  AND ABS(Quantity - @Quantity) < 0.000001
  AND ABS(ExitPrice - @ExitPrice) < 0.000001
  AND ExitTime >= DATEADD(SECOND, -5, @ExitTime)
  AND ExitTime <= DATEADD(SECOND, 5, @ExitTime)
ORDER BY Id DESC;",
                    new
                    {
                        UserId = userId,
                        log.Symbol,
                        ExitReason = exitReason,
                        Quantity = closeQty,
                        ExitPrice = exitPrice,
                        ExitTime = exitTime
                    }, tx);

                if (duplicatedPartial.HasValue)
                {
                    await tx.CommitAsync();
                    MainWindow.Instance?.AddLog($"ℹ️ [DB] TradeHistory 부분청산 중복 감지로 스킵: U{userId} {log.Symbol} Qty={closeQty}");
                    return true;
                }

                var openTrade = await db.QueryFirstOrDefaultAsync<TradeHistoryOpenRow>(@"
SELECT TOP (1) Id, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime
FROM dbo.TradeHistory WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @UserId AND Symbol = @Symbol AND IsClosed = 0
ORDER BY EntryTime DESC, Id DESC;",
                    new { UserId = userId, log.Symbol }, tx);

                string entrySide = openTrade?.Side ?? InferEntrySideFromCloseSide(log.Side);
                string strategy = !string.IsNullOrWhiteSpace(openTrade?.Strategy) ? openTrade!.Strategy : (log.Strategy ?? "PartialClose");
                float aiScore = openTrade?.AiScore ?? log.AiScore;
                decimal resolvedEntryPrice = openTrade is { EntryPrice: > 0 } ? openTrade.EntryPrice : entryPrice;
                DateTime resolvedEntryTime = openTrade is { EntryTime: var openEntryTime } && openEntryTime != default ? openEntryTime : entryTime;

                if (openTrade != null)
                {
                    decimal remainingQty = Math.Max(0m, openTrade.Quantity - closeQty);

                    if (remainingQty <= 0.000001m)
                    {
                        await db.ExecuteAsync(@"
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
WHERE Id = @Id;",
                            new
                            {
                                Id = openTrade.Id,
                                ExitPrice = exitPrice,
                                Quantity = closeQty,
                                AiScore = aiScore,
                                log.PnL,
                                log.PnLPercent,
                                ExitReason = exitReason,
                                ExitTime = exitTime
                            }, tx);

                        await tx.CommitAsync();
                        await TryMirrorToTradeLogsAsync(
                            log.Symbol,
                            log.Side,
                            TrimForDb($"PARTIAL:{exitReason}", 150),
                            exitPrice,
                            aiScore,
                            exitTime,
                            log.PnL,
                            log.PnLPercent,
                            resolvedEntryPrice,
                            exitPrice,
                            closeQty,
                            exitReason);
                        MainWindow.Instance?.AddLog($"✅ [DB] TradeHistory 부분청산 잔량 0 → 전량청산 update 처리: U{userId} {log.Symbol}");
                        return true;
                    }

                    await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, GETDATE());",
                        new
                        {
                            UserId = userId,
                            log.Symbol,
                            Side = entrySide,
                            Strategy = strategy,
                            EntryPrice = resolvedEntryPrice,
                            ExitPrice = exitPrice,
                            Quantity = closeQty,
                            AiScore = aiScore,
                            log.PnL,
                            log.PnLPercent,
                            ExitReason = exitReason,
                            EntryTime = resolvedEntryTime,
                            ExitTime = exitTime
                        }, tx);

                    await db.ExecuteAsync(@"
UPDATE dbo.TradeHistory
SET Quantity = @RemainingQty,
    LastUpdatedAt = GETDATE()
WHERE Id = @Id;",
                        new { Id = openTrade.Id, RemainingQty = remainingQty }, tx);
                }
                else
                {
                    await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, GETDATE());",
                        new
                        {
                            UserId = userId,
                            log.Symbol,
                            Side = entrySide,
                            Strategy = strategy,
                            EntryPrice = resolvedEntryPrice,
                            ExitPrice = exitPrice,
                            Quantity = closeQty,
                            AiScore = aiScore,
                            log.PnL,
                            log.PnLPercent,
                            ExitReason = exitReason,
                            EntryTime = resolvedEntryTime,
                            ExitTime = exitTime
                        }, tx);

                    MainWindow.Instance?.AddLog($"⚠️ [DB] 열린 진입건 없이 부분청산 이력만 보정 insert: U{userId} {log.Symbol}");
                }

                await tx.CommitAsync();
                await TryMirrorToTradeLogsAsync(
                    log.Symbol,
                    log.Side,
                    TrimForDb($"PARTIAL:{exitReason}", 150),
                    exitPrice,
                    aiScore,
                    exitTime,
                    log.PnL,
                    log.PnLPercent,
                    resolvedEntryPrice,
                    exitPrice,
                    closeQty,
                    exitReason);
                MainWindow.Instance?.AddLog($"✅ [DB] TradeHistory 부분청산 기록 완료: U{userId} {log.Symbol} Qty={closeQty}");
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 부분청산 저장 실패] {ex.GetType().Name}: {ex.Message}");
                MainWindow.Instance?.AddAlert($"❌ [DB] 부분청산 저장 실패: {log?.Symbol ?? "Unknown"} | {ex.Message}");
                return false;
            }
        }

        public async Task SaveTradeLogAsync(TradeLog log)
        {
            if (log == null)
            {
                MainWindow.Instance?.AddLog("⚠️ SaveTradeLogAsync: log 객체가 null입니다");
                return;
            }

            if (string.Equals(log.ExitReason, "PartialClose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(log.Strategy, "PartialClose", StringComparison.OrdinalIgnoreCase))
            {
                await RecordPartialCloseAsync(log);
                return;
            }

            if (!string.IsNullOrWhiteSpace(log.ExitReason)
                || string.Equals(log.Strategy, "MarketClose", StringComparison.OrdinalIgnoreCase)
                || log.PnL != 0
                || log.ExitPrice > 0)
            {
                await CompleteTradeAsync(log);
                return;
            }

            await UpsertTradeEntryAsync(log);
        }

        // ============================================================================
        // AI 예측 검증 서비스 호환 메서드
        // ============================================================================

        public async Task<List<AIPrediction>> GetPendingValidationsAsync()
        {
            await Task.CompletedTask;
            return new List<AIPrediction>();
        }

        public async Task UpdatePredictionValidationAsync(long predictionId, decimal actualPrice, bool isCorrect)
        {
            await Task.CompletedTask;
        }

        public async Task<Dictionary<string, (int total, int correct, double accuracy, double avgConf)>> GetModelAccuracyStatsAsync()
        {
            await Task.CompletedTask;
            return new Dictionary<string, (int total, int correct, double accuracy, double avgConf)>();
        }

        public async Task<List<TradeLog>> GetTradeLogsAsync(int limit = 100, DateTime? startDate = null, DateTime? endDate = null)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    var p = new DynamicParameters();
                    p.Add("@Limit", limit);

                    string sql = "SELECT TOP (@Limit) * FROM TradeLogs WHERE 1=1";

                    if (startDate.HasValue)
                    {
                        sql += " AND Time >= @StartDate";
                        p.Add("@StartDate", startDate.Value);
                    }
                    if (endDate.HasValue)
                    {
                        sql += " AND Time <= @EndDate";
                        p.Add("@EndDate", endDate.Value);
                    }

                    sql += " ORDER BY Time DESC";
                    var result = await db.QueryAsync<TradeLog>(sql, p);
                    return result.ToList();
                }
            }
            catch (Exception)
            {
                return new List<TradeLog>();
            }
        }

        public async Task<List<TradeLog>> GetTradeHistoryAsync(int userId, DateTime startDate, DateTime endDate, int limit = 1000)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await EnsureTradeHistorySchemaAsync(db);

                string sql = @"
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
FROM dbo.TradeHistory
WHERE UserId = @UserId
    AND (
                (IsClosed = 1 AND CloseVerified = 1 AND ExitTime >= @StartDate AND ExitTime <= @EndDate)
                OR
                (IsClosed = 0 AND EntryTime >= @StartDate AND EntryTime <= @EndDate)
            )
ORDER BY CASE WHEN IsClosed = 0 THEN EntryTime ELSE COALESCE(ExitTime, EntryTime) END DESC, Id DESC";

                var rows = await db.QueryAsync<TradeLog>(sql, new { UserId = userId, StartDate = startDate, EndDate = endDate, Limit = limit });
                return rows.ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB] TradeHistory 조회 실패: {ex.Message}");
                return new List<TradeLog>();
            }
        }

        public async Task ExportTradeHistoryToCsvAsync(string filePath, int userId, DateTime startDate, DateTime endDate, int limit = 10000)
        {
            var rows = await GetTradeHistoryAsync(userId, startDate, endDate, limit);

            using var writer = new StreamWriter(filePath);
            await writer.WriteLineAsync("Id,UserId,Time,Symbol,Side,Price,Strategy,AiScore,PnL,PnLPercent,Quantity,EntryPrice,ExitPrice,EntryTime,ExitTime,ExitReason");

            foreach (var row in rows)
            {
                string line = string.Join(",",
                    row.Id,
                    userId,
                    row.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    CsvEscape(row.Symbol),
                    CsvEscape(row.Side),
                    row.Price.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(row.Strategy),
                    row.AiScore.ToString(CultureInfo.InvariantCulture),
                    row.PnL.ToString(CultureInfo.InvariantCulture),
                    row.PnLPercent.ToString(CultureInfo.InvariantCulture),
                    row.Quantity.ToString(CultureInfo.InvariantCulture),
                    row.EntryPrice.ToString(CultureInfo.InvariantCulture),
                    row.ExitPrice.ToString(CultureInfo.InvariantCulture),
                    row.EntryTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    row.ExitTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    CsvEscape(row.ExitReason));

                await writer.WriteLineAsync(line);
            }
        }

        private static string CsvEscape(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            string escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        // [추가] 학습용 데이터 추출 (예시)
        public async Task TrainNeuralNetworkModel()
        {
            // 실제 구현 시 ML.NET 파이프라인 호출
            await Task.CompletedTask;
        }

        // ============================================================================
        // [Phase 14] 고급 거래 기능 로깅 메서드
        // ============================================================================

        /// <summary>
        /// 차익거래 실행 로그 저장
        /// </summary>
        public async Task SaveArbitrageExecutionLogAsync(ArbitrageExecution execution)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = @"
                        INSERT INTO ArbitrageExecutionLog 
                        (Symbol, BuyExchange, SellExchange, BuyPrice, SellPrice, Quantity, 
                         ProfitPercent, BuyOrderId, SellOrderId, BuySuccess, SellSuccess, 
                         Success, ErrorMessage, StartTime, EndTime)
                        VALUES 
                        (@Symbol, @BuyExchange, @SellExchange, @BuyPrice, @SellPrice, @Quantity, 
                         @ProfitPercent, @BuyOrderId, @SellOrderId, @BuySuccess, @SellSuccess, 
                         @Success, @ErrorMessage, @StartTime, @EndTime)";

                    await db.ExecuteAsync(sql, new
                    {
                        execution.Opportunity.Symbol,
                        BuyExchange = execution.Opportunity.BuyExchange.ToString(),
                        SellExchange = execution.Opportunity.SellExchange.ToString(),
                        execution.Opportunity.BuyPrice,
                        execution.Opportunity.SellPrice,
                        execution.Quantity,
                        execution.Opportunity.ProfitPercent,
                        execution.BuyOrderId,
                        execution.SellOrderId,
                        execution.BuySuccess,
                        execution.SellSuccess,
                        execution.Success,
                        execution.ErrorMessage,
                        execution.StartTime,
                        execution.EndTime
                    });
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 차익거래 로그 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 자금 이동 로그 저장
        /// </summary>
        public async Task SaveFundTransferLogAsync(FundTransferResult result)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = @"
                        INSERT INTO FundTransferLog 
                        (FromExchange, ToExchange, Asset, Amount, WithdrawSuccess, DepositSuccess, 
                         Success, ErrorMessage, RequestTime, StartTime, EndTime)
                        VALUES 
                        (@FromExchange, @ToExchange, @Asset, @Amount, @WithdrawSuccess, @DepositSuccess, 
                         @Success, @ErrorMessage, @RequestTime, @StartTime, @EndTime)";

                    await db.ExecuteAsync(sql, new
                    {
                        FromExchange = result.Request.FromExchange.ToString(),
                        ToExchange = result.Request.ToExchange.ToString(),
                        result.Request.Asset,
                        result.Request.Amount,
                        result.WithdrawSuccess,
                        result.DepositSuccess,
                        result.Success,
                        result.ErrorMessage,
                        RequestTime = result.Request.Timestamp,
                        result.StartTime,
                        result.EndTime
                    });
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 자금 이동 로그 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 포트폴리오 리밸런싱 로그 저장
        /// </summary>
        public async Task SaveRebalancingLogAsync(RebalancingReport report)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    // 부모 로그 저장
                    string insertLogSql = @"
                        INSERT INTO PortfolioRebalancingLog 
                        (TotalValue, ActionCount, Success, ErrorMessage, StartTime, EndTime)
                        OUTPUT INSERTED.Id
                        VALUES 
                        (@TotalValue, @ActionCount, @Success, @ErrorMessage, @StartTime, @EndTime)";

                    long logId = await db.ExecuteScalarAsync<long>(insertLogSql, new
                    {
                        report.TotalValue,
                        ActionCount = report.ExecutedActions.Count,
                        report.Success,
                        report.ErrorMessage,
                        report.StartTime,
                        report.EndTime
                    });

                    // 자식 액션 저장
                    if (report.ExecutedActions.Any())
                    {
                        string insertActionSql = @"
                            INSERT INTO RebalancingAction 
                            (RebalancingLogId, Asset, CurrentPercentage, TargetPercentage, 
                             Deviation, Action, TargetValue, Executed)
                            VALUES 
                            (@RebalancingLogId, @Asset, @CurrentPercentage, @TargetPercentage, 
                             @Deviation, @Action, @TargetValue, @Executed)";

                        foreach (var action in report.ExecutedActions)
                        {
                            await db.ExecuteAsync(insertActionSql, new
                            {
                                RebalancingLogId = logId,
                                action.Asset,
                                action.CurrentPercentage,
                                action.TargetPercentage,
                                action.Deviation,
                                action.Action,
                                action.TargetValue,
                                Executed = true
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 리밸런싱 로그 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 차익거래 통계 조회
        /// </summary>
        public async Task<Dictionary<string, object>?> GetArbitrageStatisticsAsync()
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = "SELECT * FROM vw_ArbitrageStatistics";
                    var result = await db.QueryFirstOrDefaultAsync<dynamic>(sql);

                    if (result != null)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var prop in result)
                        {
                            dict[prop.Key] = prop.Value;
                        }
                        return dict;
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 차익거래 통계 조회 실패: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 최근 차익거래 로그 조회
        /// </summary>
        public async Task<List<dynamic>> GetRecentArbitrageLogsAsync(int limit = 50)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = @"
                        SELECT TOP (@Limit) *
                        FROM ArbitrageExecutionLog
                        ORDER BY CreatedAt DESC";

                    var result = await db.QueryAsync<dynamic>(sql, new { Limit = limit });
                    return result.ToList();
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 차익거래 로그 조회 실패: {ex.Message}");
                return new List<dynamic>();
            }
        }

        /// <summary>
        /// 최근 자금 이동 로그 조회
        /// </summary>
        public async Task<List<dynamic>> GetRecentFundTransferLogsAsync(int limit = 50)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = @"
                        SELECT TOP (@Limit) *
                        FROM FundTransferLog
                        ORDER BY CreatedAt DESC";

                    var result = await db.QueryAsync<dynamic>(sql, new { Limit = limit });
                    return result.ToList();
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 자금 이동 로그 조회 실패: {ex.Message}");
                return new List<dynamic>();
            }
        }

        /// <summary>
        /// 최근 리밸런싱 로그 조회 (액션 포함)
        /// </summary>
        public async Task<List<dynamic>> GetRecentRebalancingLogsAsync(int limit = 20)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = @"
                        SELECT TOP (@Limit) 
                            l.*, 
                            (SELECT COUNT(*) FROM RebalancingAction WHERE RebalancingLogId = l.Id) AS ActionCount
                        FROM PortfolioRebalancingLog l
                        ORDER BY l.CreatedAt DESC";

                    var result = await db.QueryAsync<dynamic>(sql, new { Limit = limit });
                    return result.ToList();
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 리밸런싱 로그 조회 실패: {ex.Message}");
                return new List<dynamic>();
            }
        }

        // ============================================================================
        // [Phase 15] GeneralSettings DB 관리
        // ============================================================================

        /// <summary>
        /// GeneralSettings을 DB에 저장 (사용자별)
        /// </summary>
        public async Task SaveGeneralSettingsAsync(int userId, TradingSettings settings)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = @"
                        MERGE dbo.GeneralSettings AS target
                        USING (SELECT @UserId AS Id, @DefaultLeverage, @DefaultMargin, @TargetRoe, @StopLossRoe, @TrailingStartRoe, @TrailingDropRoe,
                                      @PumpTp1Roe, @PumpTp2Roe, @PumpTimeStopMinutes, @PumpStopDistanceWarnPct, @PumpStopDistanceBlockPct, @MajorTrendProfile) 
                            AS source (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe,
                                      PumpTp1Roe, PumpTp2Roe, PumpTimeStopMinutes, PumpStopDistanceWarnPct, PumpStopDistanceBlockPct, MajorTrendProfile)
                        ON target.Id = source.Id
                        WHEN MATCHED THEN
                            UPDATE SET 
                                target.DefaultLeverage = source.DefaultLeverage,
                                target.DefaultMargin = source.DefaultMargin,
                                target.TargetRoe = source.TargetRoe,
                                target.StopLossRoe = source.StopLossRoe,
                                target.TrailingStartRoe = source.TrailingStartRoe,
                                target.TrailingDropRoe = source.TrailingDropRoe,
                                target.PumpTp1Roe = source.PumpTp1Roe,
                                target.PumpTp2Roe = source.PumpTp2Roe,
                                target.PumpTimeStopMinutes = source.PumpTimeStopMinutes,
                                target.PumpStopDistanceWarnPct = source.PumpStopDistanceWarnPct,
                                target.PumpStopDistanceBlockPct = source.PumpStopDistanceBlockPct,
                                target.MajorTrendProfile = source.MajorTrendProfile,
                                target.UpdatedAt = GETUTCDATE()
                        WHEN NOT MATCHED THEN
                            INSERT (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe,
                                    PumpTp1Roe, PumpTp2Roe, PumpTimeStopMinutes, PumpStopDistanceWarnPct, PumpStopDistanceBlockPct, MajorTrendProfile)
                            VALUES (@UserId, @DefaultLeverage, @DefaultMargin, @TargetRoe, @StopLossRoe, @TrailingStartRoe, @TrailingDropRoe,
                                    @PumpTp1Roe, @PumpTp2Roe, @PumpTimeStopMinutes, @PumpStopDistanceWarnPct, @PumpStopDistanceBlockPct, @MajorTrendProfile);";

                    var parameters = new DynamicParameters();
                    parameters.Add("@UserId", userId);
                    parameters.Add("@DefaultLeverage", settings.DefaultLeverage);
                    parameters.Add("@DefaultMargin", settings.DefaultMargin);
                    parameters.Add("@TargetRoe", settings.TargetRoe);
                    parameters.Add("@StopLossRoe", settings.StopLossRoe);
                    parameters.Add("@TrailingStartRoe", settings.TrailingStartRoe);
                    parameters.Add("@TrailingDropRoe", settings.TrailingDropRoe);
                    parameters.Add("@PumpTp1Roe", settings.PumpTp1Roe);
                    parameters.Add("@PumpTp2Roe", settings.PumpTp2Roe);
                    parameters.Add("@PumpTimeStopMinutes", settings.PumpTimeStopMinutes);
                    parameters.Add("@PumpStopDistanceWarnPct", settings.PumpStopDistanceWarnPct);
                    parameters.Add("@PumpStopDistanceBlockPct", settings.PumpStopDistanceBlockPct);
                    parameters.Add("@MajorTrendProfile", settings.MajorTrendProfile);

                    try
                    {
                        await db.ExecuteAsync(sql, parameters);
                    }
                    catch (SqlException ex) when (ex.Message.Contains("PumpTp1Roe") || ex.Message.Contains("PumpTp2Roe") || ex.Message.Contains("PumpTimeStopMinutes") || ex.Message.Contains("PumpStopDistanceWarnPct") || ex.Message.Contains("PumpStopDistanceBlockPct") || ex.Message.Contains("MajorTrendProfile"))
                    {
                        // 하위 호환: 구 스키마(펌프 컬럼 없음)에서는 기본 필드만 저장
                        string fallbackSql = @"
                            MERGE dbo.GeneralSettings AS target
                            USING (SELECT @UserId AS Id, @DefaultLeverage, @DefaultMargin, @TargetRoe, @StopLossRoe, @TrailingStartRoe, @TrailingDropRoe)
                                AS source (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe)
                            ON target.Id = source.Id
                            WHEN MATCHED THEN
                                UPDATE SET
                                    target.DefaultLeverage = source.DefaultLeverage,
                                    target.DefaultMargin = source.DefaultMargin,
                                    target.TargetRoe = source.TargetRoe,
                                    target.StopLossRoe = source.StopLossRoe,
                                    target.TrailingStartRoe = source.TrailingStartRoe,
                                    target.TrailingDropRoe = source.TrailingDropRoe,
                                    target.UpdatedAt = GETUTCDATE()
                            WHEN NOT MATCHED THEN
                                INSERT (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe)
                                VALUES (@UserId, @DefaultLeverage, @DefaultMargin, @TargetRoe, @StopLossRoe, @TrailingStartRoe, @TrailingDropRoe);";

                        await db.ExecuteAsync(fallbackSql, parameters);
                        MainWindow.Instance?.AddLog("ℹ️ GeneralSettings 저장: 구 스키마 호환 모드로 저장됨(펌프 전용 컬럼 없음)");
                    }
                    MainWindow.Instance?.AddLog($"✅ [{userId}] GeneralSettings 저장 완료");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ GeneralSettings 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// DB에서 GeneralSettings 로드 (사용자별)
        /// </summary>
        public async Task<TradingSettings?> LoadGeneralSettingsAsync(int userId)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    string sql = "SELECT * FROM dbo.GeneralSettings WHERE Id = @UserId";
                    var result = await db.QuerySingleOrDefaultAsync<TradingSettings>(sql, new { UserId = userId });

                    if (result != null)
                    {
                        MainWindow.Instance?.AddLog($"✅ [{userId}] GeneralSettings DB에서 로드 완료");
                    }
                    else
                    {
                        MainWindow.Instance?.AddLog($"⚠️ [{userId}] GeneralSettings를 찾을 수 없음 (기본값 사용)");
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ GeneralSettings 로드 실패: {ex.Message}");
                return null;
            }
        }
    }
}
