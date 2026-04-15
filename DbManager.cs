using Dapper;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TradingBot.Models;
using TradingBot; // [수정] MainWindow 접근을 위해 추가
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    public class DbManager
    {
        private readonly string _connectionString;
        private static readonly ConcurrentDictionary<string, bool> _columnExistsCache = new(StringComparer.OrdinalIgnoreCase);

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
            _ = EnsureCategoryColumnAsync();
            _ = EnsureCandleDataIndexAsync();
        }

        /// <summary>[v5.2.0] CandleData 학습 쿼리 성능 인덱스</summary>
        private async Task EnsureCandleDataIndexAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CandleData') AND name = 'IX_CandleData_IntervalText_Symbol_OpenTime')
BEGIN
    CREATE INDEX IX_CandleData_IntervalText_Symbol_OpenTime
        ON dbo.CandleData (IntervalText, Symbol, OpenTime DESC);
END", commandTimeout: 60);
            }
            catch { }
        }

        private async Task EnsureIsSimulationColumnAsync()
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.ExecuteAsync(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'IsSimulation'
)
BEGIN
    ALTER TABLE dbo.TradeHistory ADD IsSimulation BIT NOT NULL DEFAULT 0;
END");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] IsSimulation 컬럼 자동 추가 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// [v5.0.3] TradeHistory.Category 컬럼 자동 추가 (MAJOR/PUMP/SPIKE)
        /// 기존 레코드는 NULL 유지 → 통계 쿼리에서 자동 제외
        /// </summary>
        private async Task EnsureCategoryColumnAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'Category'
)
BEGIN
    ALTER TABLE dbo.TradeHistory ADD Category NVARCHAR(10) NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'IX_TradeHistory_Category_EntryTime'
)
BEGIN
    CREATE INDEX IX_TradeHistory_Category_EntryTime
        ON dbo.TradeHistory (Category, EntryTime)
        WHERE Category IS NOT NULL;
END");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] Category 컬럼 자동 추가 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// [v5.0.3] signalSource + signal symbol 로부터 카테고리 결정
        /// 규칙 (단순):
        /// - MAJOR: 9개 메이저 심볼 중 하나
        /// - SPIKE: signalSource 가 SPIKE_FAST / SPIKE_* 로 시작
        /// - PUMP:  나머지 (MAJOR_MEME, PUMP_WATCH_CONFIRMED, TICK_SURGE, 기본값)
        /// </summary>
        public static string ResolveTradeCategory(string symbol, string? signalSource)
        {
            // 9개 메이저 심볼 (TradingEngine.MajorSymbols 동일)
            if (!string.IsNullOrEmpty(symbol))
            {
                switch (symbol)
                {
                    case "BTCUSDT":
                    case "ETHUSDT":
                    case "SOLUSDT":
                    case "XRPUSDT":
                    case "BNBUSDT":
                    case "ADAUSDT":
                    case "DOGEUSDT":
                    case "AVAXUSDT":
                    case "LINKUSDT":
                        return "MAJOR";
                }
            }

            if (!string.IsNullOrEmpty(signalSource) &&
                (signalSource.StartsWith("SPIKE", StringComparison.OrdinalIgnoreCase)
                 || signalSource.Equals("TICK_SURGE", StringComparison.OrdinalIgnoreCase)
                 || signalSource.StartsWith("CRASH", StringComparison.OrdinalIgnoreCase)))
            {
                return "SPIKE";
            }

            return "PUMP";
        }

        /// <summary>
        /// [v5.0.3] 카테고리별 오늘(KST 00:00 기준) 통계
        /// Category IS NOT NULL 로 과거 데이터 자동 제외
        /// 진입 시각 + Symbol 그룹핑으로 PartialClose 중복 제거
        /// </summary>
        public async Task<Dictionary<string, (int entries, int wins, int losses, decimal totalPnL)>>
            GetTodayStatsByCategoryAsync()
        {
            var result = new Dictionary<string, (int, int, int, decimal)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DateTime todayKstStart = DateTime.Today;
                int userId = AppConfig.CurrentUser?.Id ?? 0;

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                // [v5.3.2] UserId 필터 추가 — 이 유저의 매매만 집계
                const string sql = @"
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
GROUP BY Category";

                var rows = await db.QueryAsync(sql, new { todayStart = todayKstStart, userId });
                foreach (var row in rows)
                {
                    string cat = (string)row.Category;
                    int entries = (int)(row.Entries ?? 0);
                    int wins = (int)(row.Wins ?? 0);
                    int losses = (int)(row.Losses ?? 0);
                    decimal totalPnL = (decimal)(row.TotalPnL ?? 0m);
                    result[cat] = (entries, wins, losses, totalPnL);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] GetTodayStatsByCategoryAsync 오류: {ex.Message}");
            }
            return result;
        }

        // [v3.5.2] 포지션 상태 영속화 — 재시작 시 부분청산/본절/계단식 상태 복원
        private async Task EnsurePositionStateTableAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PositionState' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PositionState (
        UserId              INT            NOT NULL,
        Symbol              NVARCHAR(20)   NOT NULL,
        TakeProfitStep      INT            NOT NULL DEFAULT 0,
        PartialProfitStage  INT            NOT NULL DEFAULT 0,
        BreakevenPrice      DECIMAL(18,8)  NOT NULL DEFAULT 0,
        HighestROE          DECIMAL(18,4)  NOT NULL DEFAULT 0,
        StairStep           INT            NOT NULL DEFAULT 0,
        IsBreakEvenTriggered BIT           NOT NULL DEFAULT 0,
        HighestPrice        DECIMAL(18,8)  NOT NULL DEFAULT 0,
        LowestPrice         DECIMAL(18,8)  NOT NULL DEFAULT 0,
        IsPumpStrategy      BIT            NOT NULL DEFAULT 0,
        LastUpdatedAt       DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
        CONSTRAINT PK_PositionState PRIMARY KEY (UserId, Symbol)
    );
END");
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] PositionState 테이블 생성 실패: {ex.Message}");
            }
        }

        /// <summary>포지션 상태 저장/업데이트 (부분청산, 본절, 계단식 등)</summary>
        /// <summary>
        /// [v3.5.2 / v5.0.8] PositionState 저장 — 부분 merge 로 기존 값 덮어쓰기 방지
        /// v5.0.8: 파라미터가 0/false 면 기존 DB 값 유지 (CASE WHEN)
        /// 예: PersistPositionState(symbol, highestROE: 259) 호출 시
        ///     StairStep=0, IsBreakEvenTriggered=false 를 전달해도 기존 값 유지
        /// </summary>
        public async Task SavePositionStateAsync(int userId, string symbol, PositionInfo pos, int stairStep = 0, bool isBreakEvenTriggered = false, decimal highestROE = 0)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.ExecuteAsync(@"
MERGE dbo.PositionState AS target
USING (SELECT @UserId AS UserId, @Symbol AS Symbol) AS source
ON target.UserId = source.UserId AND target.Symbol = source.Symbol
WHEN MATCHED THEN
    UPDATE SET
        TakeProfitStep = CASE WHEN @TakeProfitStep > 0 THEN @TakeProfitStep ELSE target.TakeProfitStep END,
        PartialProfitStage = CASE WHEN @PartialProfitStage > 0 THEN @PartialProfitStage ELSE target.PartialProfitStage END,
        BreakevenPrice = CASE WHEN @BreakevenPrice > 0 THEN @BreakevenPrice ELSE target.BreakevenPrice END,
        HighestROE = CASE WHEN @HighestROE > target.HighestROE THEN @HighestROE ELSE target.HighestROE END,
        StairStep = CASE WHEN @StairStep > target.StairStep THEN @StairStep ELSE target.StairStep END,
        IsBreakEvenTriggered = CASE WHEN @IsBreakEvenTriggered = 1 THEN 1 ELSE target.IsBreakEvenTriggered END,
        HighestPrice = CASE WHEN @HighestPrice > target.HighestPrice THEN @HighestPrice ELSE target.HighestPrice END,
        LowestPrice = CASE WHEN @LowestPrice > 0 AND (target.LowestPrice = 0 OR @LowestPrice < target.LowestPrice) THEN @LowestPrice ELSE target.LowestPrice END,
        IsPumpStrategy = @IsPumpStrategy,
        LastUpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (UserId, Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice,
            HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy)
    VALUES (@UserId, @Symbol, @TakeProfitStep, @PartialProfitStage, @BreakevenPrice,
            @HighestROE, @StairStep, @IsBreakEvenTriggered, @HighestPrice, @LowestPrice, @IsPumpStrategy);",
                new
                {
                    UserId = userId,
                    Symbol = symbol,
                    TakeProfitStep = pos.TakeProfitStep,
                    PartialProfitStage = pos.PartialProfitStage,
                    BreakevenPrice = pos.BreakevenPrice,
                    HighestROE = highestROE,
                    StairStep = stairStep,
                    IsBreakEvenTriggered = isBreakEvenTriggered,
                    HighestPrice = pos.HighestPrice,
                    LowestPrice = pos.LowestPrice,
                    IsPumpStrategy = pos.IsPumpStrategy
                });
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] PositionState 저장 실패 [{symbol}]: {ex.Message}");
            }
        }

        /// <summary>[v4.5.12] PositionState DTO — Dapper 자동 매핑용</summary>
        private class PositionStateRow
        {
            public string Symbol { get; set; } = string.Empty;
            public int TakeProfitStep { get; set; }
            public int PartialProfitStage { get; set; }
            public decimal BreakevenPrice { get; set; }
            public decimal HighestROE { get; set; }
            public int StairStep { get; set; }
            public bool IsBreakEvenTriggered { get; set; }
            public decimal HighestPrice { get; set; }
            public decimal LowestPrice { get; set; }
            public bool IsPumpStrategy { get; set; }
        }

        /// <summary>재시작 시 포지션 상태 복원 (Dapper 타입 매핑)</summary>
        public async Task<Dictionary<string, (int TakeProfitStep, int PartialProfitStage, decimal BreakevenPrice,
            decimal HighestROE, int StairStep, bool IsBreakEvenTriggered, decimal HighestPrice, decimal LowestPrice, bool IsPumpStrategy)>>
            LoadPositionStatesAsync(int userId)
        {
            var result = new Dictionary<string, (int, int, decimal, decimal, int, bool, decimal, decimal, bool)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var db = new SqlConnection(_connectionString);
                // [v4.5.12] Dapper가 PositionStateRow로 자동 매핑 (기존 dynamic 루프 대비 CPU ~20% 감소)
                var rows = await db.QueryAsync<PositionStateRow>(@"
SELECT Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice,
       HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy
FROM dbo.PositionState WITH (NOLOCK)
WHERE UserId = @UserId", new { UserId = userId });

                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row.Symbol)) continue;
                    result[row.Symbol] = (
                        row.TakeProfitStep,
                        row.PartialProfitStage,
                        row.BreakevenPrice,
                        row.HighestROE,
                        row.StairStep,
                        row.IsBreakEvenTriggered,
                        row.HighestPrice,
                        row.LowestPrice,
                        row.IsPumpStrategy);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] PositionState 복원 실패: {ex.Message}");
            }
            return result;
        }

        /// <summary>포지션 청산 시 상태 삭제</summary>
        public async Task DeletePositionStateAsync(int userId, string symbol)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.ExecuteAsync("DELETE FROM dbo.PositionState WHERE UserId = @UserId AND Symbol = @Symbol",
                    new { UserId = userId, Symbol = symbol });
            }
            catch { }
        }

        private async Task EnsureOrderErrorTableAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Order_Error' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Order_Error (
        Id          BIGINT         IDENTITY(1,1) PRIMARY KEY,
        UserId      INT            NOT NULL DEFAULT 0,
        EventTime   DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
        Symbol      NVARCHAR(20)   NOT NULL,
        Side        NVARCHAR(10)   NOT NULL,
        OrderType   NVARCHAR(30)   NOT NULL,
        Quantity    DECIMAL(18,8)  NOT NULL,
        ErrorCode   INT            NULL,
        ErrorMsg    NVARCHAR(500)  NOT NULL,
        Resolved    BIT            NOT NULL DEFAULT 0,
        RetryCount  INT            NOT NULL DEFAULT 0,
        Resolution  NVARCHAR(200)  NULL
    );
END");
                _orderErrorTableChecked = true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] Order_Error 테이블 자동 생성 실패: {ex.Message}");
            }
        }

        private int GetCurrentUserId()
        {
            int currentUserId = AppConfig.CurrentUser?.Id ?? 0;
            if (currentUserId > 0)
                return currentUserId;

            string? username = AppConfig.CurrentUser?.Username;
            if (string.IsNullOrWhiteSpace(username))
                username = AppConfig.CurrentUsername;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(_connectionString))
                return 0;

            try
            {
                using var db = new SqlConnection(_connectionString);
                db.Open();

                int? resolvedUserId = db.ExecuteScalar<int?>(
                    "SELECT TOP (1) Id FROM dbo.Users WHERE Username = @Username",
                    new { Username = username.Trim() });

                if (resolvedUserId is > 0)
                {
                    if (AppConfig.CurrentUser != null && AppConfig.CurrentUser.Id <= 0)
                        AppConfig.CurrentUser.Id = resolvedUserId.Value;

                    return resolvedUserId.Value;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] Users 기준 UserId 조회 실패: {ex.Message}");
            }

            return 0;
        }

        private bool TryGetCurrentUserIdForSave(string operation, out int userId)
        {
            userId = GetCurrentUserId();
            if (userId > 0)
                return true;

            MainWindow.Instance?.AddLog($"⚠️ [{operation}] Users 기준 UserId 확인 실패로 DB 저장을 건너뜁니다.");
            return false;
        }

        private async Task<bool> HasColumnAsync(SqlConnection db, string tableName, string columnName)
        {
            string cacheKey = $"{tableName}.{columnName}";
            if (_columnExistsCache.TryGetValue(cacheKey, out bool cached))
                return cached;

            const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(@FullTableName)
      AND name = @ColumnName
) THEN 1 ELSE 0 END";

            int exists = await db.ExecuteScalarAsync<int>(sql, new
            {
                FullTableName = $"dbo.{tableName}",
                ColumnName = columnName
            });

            bool result = exists == 1;
            _columnExistsCache[cacheKey] = result;
            return result;
        }

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
                bool hasTradeLogsUserId = await HasColumnAsync(db, "TradeLogs", "UserId");
                int userId = GetCurrentUserId();

                if (hasTradeLogsUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [TradeLogs 미러링] UserId 확인 실패로 사용자별 로그 저장을 건너뜁니다.");
                    return;
                }

                string sql = hasTradeLogsUserId
                    ? @"
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
END"
                    : @"
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
                    UserId = userId,
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

        public async Task<long?> SaveTradePatternSnapshotAsync(TradePatternSnapshotRecord snapshot)
        {
            try
            {
                if (!TryGetCurrentUserIdForSave("패턴 저장", out int userId))
                    return null;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();                

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
                if (!TryGetCurrentUserIdForSave("패턴 완성", out int userId))
                    return false;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

                

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

        public async Task<bool> SaveAiTrainingDataAsync(AiLabeledSample sample)
        {
            try
            {
                if (sample == null)
                    return false;

                if (!TryGetCurrentUserIdForSave("AI 라벨 샘플 저장", out int userId))
                    return false;

                string symbol = TrimForDb(sample.Symbol, 50);
                if (string.IsNullOrWhiteSpace(symbol))
                    return false;

                DateTime entryTimeUtc = sample.EntryTimeUtc == default ? DateTime.UtcNow : sample.EntryTimeUtc;
                decimal entryPrice = sample.EntryPrice < 0 ? 0m : sample.EntryPrice;
                float actualProfitPct = SanitizeFloatForDb(sample.ActualProfitPct);
                string labelSource = TrimForDb(sample.LabelSource, 120);
                if (string.IsNullOrWhiteSpace(labelSource))
                    labelSource = "unknown";

                bool shouldEnter = sample.Feature?.ShouldEnter ?? sample.IsSuccess;
                string featureJson = JsonSerializer.Serialize(sample.Feature ?? new MultiTimeframeEntryFeature());

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                string sql = @"


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
);";

                await db.ExecuteAsync(sql, new
                {
                    UserId = userId,
                    Symbol = symbol,
                    EntryTimeUtc = entryTimeUtc,
                    EntryPrice = entryPrice,
                    ActualProfitPct = actualProfitPct,
                    IsSuccess = sample.IsSuccess,
                    ShouldEnter = shouldEnter,
                    LabelSource = labelSource,
                    FeatureJson = featureJson
                });

                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [AI][DB] 라벨 샘플 저장 실패: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> UpsertAiTrainingRunAsync(
            string projectName,
            string runId,
            string stage,
            bool success,
            int sampleCount,
            int epochs,
            double? accuracy = null,
            double? f1Score = null,
            double? auc = null,
            float? bestValidationLoss = null,
            float? finalTrainLoss = null,
            string? detail = null)
        {
            try
            {
                string normalizedProject = TrimForDb(projectName, 50);
                if (string.IsNullOrWhiteSpace(normalizedProject))
                    return false;

                string normalizedRunId = TrimForDb(runId, 80);
                if (string.IsNullOrWhiteSpace(normalizedRunId))
                    normalizedRunId = Guid.NewGuid().ToString("N");

                string normalizedStage = TrimForDb(stage, 50);
                if (string.IsNullOrWhiteSpace(normalizedStage))
                    normalizedStage = "unknown";

                if (!TryGetCurrentUserIdForSave("AI 학습 이력 저장", out int userId))
                    return false;

                double? accValue = accuracy.HasValue && !double.IsNaN(accuracy.Value) && !double.IsInfinity(accuracy.Value)
                    ? accuracy.Value
                    : null;
                double? f1Value = f1Score.HasValue && !double.IsNaN(f1Score.Value) && !double.IsInfinity(f1Score.Value)
                    ? f1Score.Value
                    : null;
                double? aucValue = auc.HasValue && !double.IsNaN(auc.Value) && !double.IsInfinity(auc.Value)
                    ? auc.Value
                    : null;
                float? bestLossValue = bestValidationLoss.HasValue && !float.IsNaN(bestValidationLoss.Value) && !float.IsInfinity(bestValidationLoss.Value)
                    ? bestValidationLoss.Value
                    : null;
                float? finalLossValue = finalTrainLoss.HasValue && !float.IsNaN(finalTrainLoss.Value) && !float.IsInfinity(finalTrainLoss.Value)
                    ? finalTrainLoss.Value
                    : null;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                string sql = @"

MERGE dbo.AiTrainingRuns AS target
USING
(
    SELECT
        @UserId AS UserId,
        @ProjectName AS ProjectName,
        @RunId AS RunId
) AS source
ON target.UserId = source.UserId
   AND target.ProjectName = source.ProjectName
   AND target.RunId = source.RunId
WHEN MATCHED THEN
    UPDATE SET
        Stage = @Stage,
        Success = @Success,
        SampleCount = @SampleCount,
        Epochs = @Epochs,
        Accuracy = @Accuracy,
        F1Score = @F1Score,
        AUC = @AUC,
        BestValidationLoss = @BestValidationLoss,
        FinalTrainLoss = @FinalTrainLoss,
        Detail = @Detail,
        CompletedAtUtc = SYSUTCDATETIME(),
        UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT
    (
        UserId,
        ProjectName,
        RunId,
        Stage,
        Success,
        SampleCount,
        Epochs,
        Accuracy,
        F1Score,
        AUC,
        BestValidationLoss,
        FinalTrainLoss,
        Detail,
        CompletedAtUtc,
        UpdatedAtUtc
    )
    VALUES
    (
        @UserId,
        @ProjectName,
        @RunId,
        @Stage,
        @Success,
        @SampleCount,
        @Epochs,
        @Accuracy,
        @F1Score,
        @AUC,
        @BestValidationLoss,
        @FinalTrainLoss,
        @Detail,
        SYSUTCDATETIME(),
        SYSUTCDATETIME()
    );";

                await db.ExecuteAsync(sql, new
                {
                    UserId = userId,
                    ProjectName = normalizedProject,
                    RunId = normalizedRunId,
                    Stage = normalizedStage,
                    Success = success,
                    SampleCount = Math.Max(0, sampleCount),
                    Epochs = Math.Max(0, epochs),
                    Accuracy = accValue,
                    F1Score = f1Value,
                    AUC = aucValue,
                    BestValidationLoss = bestLossValue,
                    FinalTrainLoss = finalLossValue,
                    Detail = TrimForDb(detail, 500)
                });

                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [AI][DB] 학습 이력 저장 실패: {ex.Message}");
                return false;
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

                if (!TryGetCurrentUserIdForSave($"{log.Symbol} 진입 이력", out int userId))
                    return false;

                decimal entryPrice = log.EntryPrice > 0 ? log.EntryPrice : log.Price;
                decimal quantity = Math.Abs(log.Quantity);
                DateTime entryTime = log.EntryTime == default ? log.Time : log.EntryTime;
                string strategy = string.IsNullOrWhiteSpace(log.Strategy) ? "UNKNOWN" : log.Strategy;
                // [v5.0.3] signalSource(=Strategy) + Symbol 로 카테고리 결정
                string category = ResolveTradeCategory(log.Symbol ?? string.Empty, strategy);

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();


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
    Category = @Category,
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
                    EntryTime = entryTime,
                    Category = category
                }, tx);

                if (affected == 0)
                {
                    string insertSql = @"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime, NULL, 0, 0, NULL, 0, 0, @IsSimulation, @Category, GETDATE());";

                    await db.ExecuteAsync(insertSql, new
                    {
                        UserId = userId,
                        log.Symbol,
                        Side = log.Side,
                        Strategy = strategy,
                        EntryPrice = entryPrice,
                        Quantity = quantity,
                        log.AiScore,
                        EntryTime = entryTime,
                        log.IsSimulation,
                        Category = category
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

                if (!TryGetCurrentUserIdForSave($"{position.Symbol} 시작 포지션 보정", out int userId))
                {
                    DateTime fallbackEntryTime = position.EntryTime == default ? DateTime.Now : position.EntryTime;
                    return (false, fallbackEntryTime, position.AiScore, false);
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

                string syncCategory = ResolveTradeCategory(position.Symbol ?? string.Empty, strategy);
                await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, Quantity, AiScore, EntryTime, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, CloseVerified, Category, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime, NULL, 0, 0, NULL, 0, 0, @Category, GETDATE());",
                    new
                    {
                        UserId = userId,
                        position.Symbol,
                        Side = side,
                        Strategy = strategy,
                        EntryPrice = position.EntryPrice,
                        Quantity = quantity,
                        AiScore = position.AiScore,
                        EntryTime = entryTime,
                        Category = syncCategory
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

                if (!TryGetCurrentUserIdForSave($"{log.Symbol} 청산 이력", out int userId))
                    return false;

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
    IsSimulation = @IsSimulation,
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
                            ExitTime = exitTime,
                            log.IsSimulation
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
                string fallbackCategory = ResolveTradeCategory(symbolValue ?? string.Empty, fallbackStrategy);

                await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, @IsSimulation, @Category, GETDATE());",
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
                        ExitTime = exitTime,
                        log.IsSimulation,
                        Category = fallbackCategory
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
            catch (SqlException sqlEx)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 청산 저장 실패] SQL 오류: {sqlEx.Message}");
                MainWindow.Instance?.AddLog($"   - SQL 오류 번호: {sqlEx.Number}, 상태: {sqlEx.State}, 라인: {sqlEx.LineNumber}");
                MainWindow.Instance?.AddLog($"   - Symbol: {log?.Symbol}, ExitReason: {log?.ExitReason}, PnL: {log?.PnL ?? 0}");
                if (sqlEx.Message.Contains("holdingMinutes") || sqlEx.Message.Contains("HoldingMinutes"))
                {
                    MainWindow.Instance?.AddAlert($"⚠️ [DB] holdingMinutes 계산 열 문제 감지! 봇을 재시작하거나 fix-db-manual.sql을 실행하세요.");
                }
                else
                {
                    MainWindow.Instance?.AddAlert($"❌ [DB] 청산 저장 실패: {log?.Symbol ?? "Unknown"} | {sqlEx.Message}");
                }
                return false;
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

                if (!TryGetCurrentUserIdForSave($"{log.Symbol} 외부 청산 동기화", out int userId))
                    return false;

                decimal exitPrice = log.ExitPrice > 0 ? log.ExitPrice : log.Price;
                decimal quantity = Math.Abs(log.Quantity);
                DateTime exitTime = log.ExitTime == default ? log.Time : log.ExitTime;
                string exitReason = string.IsNullOrWhiteSpace(log.ExitReason) ? log.Strategy ?? "EXTERNAL_CLOSE_SYNC" : log.ExitReason;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

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
                    string fallbackCategory = ResolveTradeCategory(log.Symbol ?? string.Empty, fallbackStrategy);

                    await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, Category, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, @Category, GETDATE());",
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
                            ExitTime = exitTime,
                            Category = fallbackCategory
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
            catch (SqlException sqlEx)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 외부 청산 동기화 실패] SQL 오류: {sqlEx.Message}");
                MainWindow.Instance?.AddLog($"   - SQL 오류 번호: {sqlEx.Number}, 상태: {sqlEx.State}, 라인: {sqlEx.LineNumber}");
                if (sqlEx.Message.Contains("holdingMinutes") || sqlEx.Message.Contains("HoldingMinutes"))
                {
                    MainWindow.Instance?.AddAlert($"⚠️ [DB] holdingMinutes 계산 열 문제 감지! 봇을 재시작하거나 fix-db-manual.sql을 실행하세요.");
                }
                else
                {
                    MainWindow.Instance?.AddAlert($"❌ [DB] 외부 청산 동기화 실패: {log?.Symbol ?? "Unknown"} | {sqlEx.Message}");
                }
                return false;
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

                if (!TryGetCurrentUserIdForSave($"{log.Symbol} 부분청산", out int userId))
                    return false;

                decimal exitPrice = log.ExitPrice > 0 ? log.ExitPrice : log.Price;
                decimal entryPrice = log.EntryPrice > 0 ? log.EntryPrice : 0m;
                decimal closeQty = Math.Abs(log.Quantity);
                DateTime entryTime = log.EntryTime == default ? log.Time : log.EntryTime;
                DateTime exitTime = log.ExitTime == default ? log.Time : log.ExitTime;
                string exitReason = string.IsNullOrWhiteSpace(log.ExitReason) ? "PartialClose" : log.ExitReason;

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                using var tx = db.BeginTransaction();

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
    IsSimulation = @IsSimulation,
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
                                ExitTime = exitTime,
                                log.IsSimulation
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

                    string partialCategory = ResolveTradeCategory(log.Symbol ?? string.Empty, strategy);
                    await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, @IsSimulation, @Category, GETDATE());",
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
                            ExitTime = exitTime,
                            log.IsSimulation,
                            Category = partialCategory
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
                    string partialCategory = ResolveTradeCategory(log.Symbol ?? string.Empty, strategy);
                    await db.ExecuteAsync(@"
INSERT INTO dbo.TradeHistory
    (UserId, Symbol, Side, Strategy, EntryPrice, ExitPrice, Quantity, AiScore, PnL, PnLPercent, ExitReason, EntryTime, ExitTime, IsClosed, CloseVerified, IsSimulation, Category, LastUpdatedAt)
VALUES
    (@UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, 1, 1, @IsSimulation, @Category, GETDATE());",
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
                            ExitTime = exitTime,
                            log.IsSimulation,
                            Category = partialCategory
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                var p = new DynamicParameters();
                p.Add("@Limit", limit);

                string sql = "SELECT TOP (@Limit) * FROM dbo.TradeLogs WHERE 1=1";
                bool hasTradeLogsUserId = await HasColumnAsync(db, "TradeLogs", "UserId");
                if (hasTradeLogsUserId)
                {
                    int userId = GetCurrentUserId();
                    if (userId <= 0)
                    {
                        MainWindow.Instance?.AddLog("⚠️ [TradeLogs 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                        return new List<TradeLog>();
                    }

                    sql += " AND UserId = @UserId";
                    p.Add("@UserId", userId);
                }

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
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [TradeLogs 조회] 실패: {ex.Message}");
                return new List<TradeLog>();
            }
        }

        public async Task<List<TradeLog>> GetTradeHistoryAsync(int userId, DateTime startDate, DateTime endDate, int limit = 1000)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

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

        /// <summary>[ProfitRegressor] 진입 시점 캔들 지표 조회 (학습 데이터용)</summary>
        public async Task<List<TradingBot.Models.CandleData>> GetRecentCandleDataAsync(string symbol, int limit = 30)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                var sql = "SELECT TOP (@Limit) * FROM CandleData WHERE Symbol = @Symbol ORDER BY OpenTime DESC";
                var result = await db.QueryAsync<TradingBot.Models.CandleData>(sql, new { Symbol = symbol, Limit = limit }, commandTimeout: 10);
                return result.Reverse().ToList();
            }
            catch
            {
                return new List<TradingBot.Models.CandleData>();
            }
        }

        /// <summary>
        /// [v4.8.0] (Symbol, IntervalText) 필터로 최근 N봉 조회
        /// OptimalEntryPriceRegressor 학습 데이터 생성용
        /// </summary>
        public async Task<List<TradingBot.Models.CandleData>> GetCandleDataByIntervalAsync(
            string symbol, string intervalText, int limit = 52000)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                var sql = @"SELECT TOP (@Limit) *
                            FROM CandleData
                            WHERE Symbol = @Symbol AND IntervalText = @Interval
                            ORDER BY OpenTime ASC";
                var result = await db.QueryAsync<TradingBot.Models.CandleData>(
                    sql, new { Symbol = symbol, Interval = intervalText, Limit = limit },
                    commandTimeout: 30);
                return result.ToList();
            }
            catch
            {
                return new List<TradingBot.Models.CandleData>();
            }
        }

        /// <summary>
        /// [v4.5.2] ML 학습용: 전체 심볼의 최근 캔들 데이터를 DB에서 조회 (계정 무관, 공유 데이터)
        /// </summary>
        /// <summary>
        /// [v4.5.12] 단일 쿼리로 전체 심볼 캔들 일괄 조회 (ROW_NUMBER 윈도우 함수)
        /// - 기존: N+1 쿼리 (심볼 개수만큼 REST 왕복)
        /// - 변경: 단일 쿼리로 심볼당 최근 N봉만 추출
        /// - 성능: 100개 심볼 기준 101회 → 1회 (약 80% 단축)
        /// </summary>
        public async Task<Dictionary<string, List<TradingBot.Models.CandleData>>> GetAllCandleDataForTrainingAsync(
            int candlesPerSymbol = 200, CancellationToken token = default)
        {
            var result = new Dictionary<string, List<TradingBot.Models.CandleData>>();
            try
            {
                using var db = new SqlConnection(_connectionString);
                // 단일 쿼리: 24시간 내 활성 심볼의 심볼별 최근 N봉 (ROW_NUMBER PARTITION BY)
                var sql = @"
WITH RankedCandles AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY Symbol ORDER BY OpenTime DESC) AS rn
    FROM CandleData WITH (NOLOCK)
    WHERE Symbol LIKE '%USDT'
      AND Symbol IN (
          SELECT DISTINCT Symbol FROM CandleData WITH (NOLOCK)
          WHERE OpenTime >= DATEADD(HOUR, -24, GETUTCDATE()) AND Symbol LIKE '%USDT'
      )
)
SELECT * FROM RankedCandles WHERE rn <= @Limit
ORDER BY Symbol, OpenTime ASC";

                var rows = await db.QueryAsync<TradingBot.Models.CandleData>(
                    sql, new { Limit = candlesPerSymbol }, commandTimeout: 30);

                // 심볼별 그룹핑 (이미 OpenTime ASC 정렬됨)
                foreach (var group in rows.GroupBy(r => r.Symbol ?? string.Empty))
                {
                    if (token.IsCancellationRequested) break;
                    if (string.IsNullOrEmpty(group.Key)) continue;
                    var list = group.ToList();
                    if (list.Count >= 30)
                        result[group.Key] = list;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetAllCandleDataForTraining 오류: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// [v5.0.7] Forecaster 대용량 학습용 — DB 의 전체 봉을 심볼당 N개까지 로드
        /// - 기본값: 심볼당 10000봉 (약 35일 × 288봉/일 5분봉)
        /// - 활성 심볼 필터 없음 (과거 데이터도 활용)
        /// - IntervalText 파라미터로 5m/1m 선택
        /// </summary>
        public async Task<Dictionary<string, List<TradingBot.Models.CandleData>>> GetBulkCandleDataAsync(
            string intervalText = "5m",
            int candlesPerSymbol = 10000,
            List<string>? symbolFilter = null,
            CancellationToken token = default)
        {
            var result = new Dictionary<string, List<TradingBot.Models.CandleData>>();
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync(token);

                string symbolFilterClause = symbolFilter is { Count: > 0 }
                    ? "AND Symbol IN @Symbols"
                    : "AND Symbol LIKE '%USDT'";

                var sql = $@"
WITH RankedCandles AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY Symbol ORDER BY OpenTime DESC) AS rn
    FROM CandleData WITH (NOLOCK)
    WHERE IntervalText = @IntervalText
      {symbolFilterClause}
)
SELECT * FROM RankedCandles WHERE rn <= @Limit
ORDER BY Symbol, OpenTime ASC";

                var param = new Dictionary<string, object?>
                {
                    ["IntervalText"] = intervalText,
                    ["Limit"] = candlesPerSymbol
                };
                if (symbolFilter is { Count: > 0 })
                {
                    param["Symbols"] = symbolFilter;
                }

                var rows = await db.QueryAsync<TradingBot.Models.CandleData>(
                    sql, param, commandTimeout: 120);

                foreach (var group in rows.GroupBy(r => r.Symbol ?? string.Empty))
                {
                    if (token.IsCancellationRequested) break;
                    if (string.IsNullOrEmpty(group.Key)) continue;
                    var list = group.ToList();
                    if (list.Count >= 30)
                        result[group.Key] = list;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] GetBulkCandleData 오류: {ex.Message}");
            }
            return result;
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasUserId = await HasColumnAsync(db, "ArbitrageExecutionLog", "UserId");
                int userId = GetCurrentUserId();
                if (hasUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [차익거래 로그] UserId 확인 실패로 사용자별 저장을 건너뜁니다.");
                    return;
                }

                string sql = hasUserId
                    ? @"
                        INSERT INTO ArbitrageExecutionLog 
                        (UserId, Symbol, BuyExchange, SellExchange, BuyPrice, SellPrice, Quantity, 
                         ProfitPercent, BuyOrderId, SellOrderId, BuySuccess, SellSuccess, 
                         Success, ErrorMessage, StartTime, EndTime)
                        VALUES 
                        (@UserId, @Symbol, @BuyExchange, @SellExchange, @BuyPrice, @SellPrice, @Quantity, 
                         @ProfitPercent, @BuyOrderId, @SellOrderId, @BuySuccess, @SellSuccess, 
                         @Success, @ErrorMessage, @StartTime, @EndTime)"
                    : @"
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
                    UserId = userId,
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasUserId = await HasColumnAsync(db, "FundTransferLog", "UserId");
                int userId = GetCurrentUserId();
                if (hasUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [자금 이동 로그] UserId 확인 실패로 사용자별 저장을 건너뜁니다.");
                    return;
                }

                string sql = hasUserId
                    ? @"
                        INSERT INTO FundTransferLog 
                        (UserId, FromExchange, ToExchange, Asset, Amount, WithdrawSuccess, DepositSuccess, 
                         Success, ErrorMessage, RequestTime, StartTime, EndTime)
                        VALUES 
                        (@UserId, @FromExchange, @ToExchange, @Asset, @Amount, @WithdrawSuccess, @DepositSuccess, 
                         @Success, @ErrorMessage, @RequestTime, @StartTime, @EndTime)"
                    : @"
                        INSERT INTO FundTransferLog 
                        (FromExchange, ToExchange, Asset, Amount, WithdrawSuccess, DepositSuccess, 
                         Success, ErrorMessage, RequestTime, StartTime, EndTime)
                        VALUES 
                        (@FromExchange, @ToExchange, @Asset, @Amount, @WithdrawSuccess, @DepositSuccess, 
                         @Success, @ErrorMessage, @RequestTime, @StartTime, @EndTime)";

                await db.ExecuteAsync(sql, new
                {
                    UserId = userId,
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasParentUserId = await HasColumnAsync(db, "PortfolioRebalancingLog", "UserId");
                bool hasActionUserId = await HasColumnAsync(db, "RebalancingAction", "UserId");
                int userId = GetCurrentUserId();
                if ((hasParentUserId || hasActionUserId) && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [리밸런싱 로그] UserId 확인 실패로 사용자별 저장을 건너뜁니다.");
                    return;
                }

                // 부모 로그 저장
                string insertLogSql = hasParentUserId
                    ? @"
                        INSERT INTO PortfolioRebalancingLog 
                        (UserId, TotalValue, ActionCount, Success, ErrorMessage, StartTime, EndTime)
                        OUTPUT INSERTED.Id
                        VALUES 
                        (@UserId, @TotalValue, @ActionCount, @Success, @ErrorMessage, @StartTime, @EndTime)"
                    : @"
                        INSERT INTO PortfolioRebalancingLog 
                        (TotalValue, ActionCount, Success, ErrorMessage, StartTime, EndTime)
                        OUTPUT INSERTED.Id
                        VALUES 
                        (@TotalValue, @ActionCount, @Success, @ErrorMessage, @StartTime, @EndTime)";

                long logId = await db.ExecuteScalarAsync<long>(insertLogSql, new
                {
                    UserId = userId,
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
                    string insertActionSql = hasActionUserId
                        ? @"
                            INSERT INTO RebalancingAction 
                            (RebalancingLogId, UserId, Asset, CurrentPercentage, TargetPercentage, 
                             Deviation, Action, TargetValue, Executed)
                            VALUES 
                            (@RebalancingLogId, @UserId, @Asset, @CurrentPercentage, @TargetPercentage, 
                             @Deviation, @Action, @TargetValue, @Executed)"
                        : @"
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
                            UserId = userId,
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasUserId = await HasColumnAsync(db, "vw_ArbitrageStatistics", "UserId");
                int userId = GetCurrentUserId();
                if (hasUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [차익거래 통계] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                    return null;
                }

                string sql = hasUserId
                    ? "SELECT * FROM vw_ArbitrageStatistics WHERE UserId = @UserId"
                    : "SELECT * FROM vw_ArbitrageStatistics";

                var result = await db.QueryFirstOrDefaultAsync<dynamic>(sql, new { UserId = userId });

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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasUserId = await HasColumnAsync(db, "ArbitrageExecutionLog", "UserId");
                int userId = GetCurrentUserId();
                if (hasUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [차익거래 로그 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                    return new List<dynamic>();
                }

                string sql = @"
                        SELECT TOP (@Limit) *
                        FROM ArbitrageExecutionLog";

                if (hasUserId)
                    sql += " WHERE UserId = @UserId";

                sql += " ORDER BY CreatedAt DESC";

                var result = await db.QueryAsync<dynamic>(sql, new { Limit = limit, UserId = userId });
                return result.ToList();
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasUserId = await HasColumnAsync(db, "FundTransferLog", "UserId");
                int userId = GetCurrentUserId();
                if (hasUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [자금 이동 로그 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                    return new List<dynamic>();
                }

                string sql = @"
                        SELECT TOP (@Limit) *
                        FROM FundTransferLog";

                if (hasUserId)
                    sql += " WHERE UserId = @UserId";

                sql += " ORDER BY CreatedAt DESC";

                var result = await db.QueryAsync<dynamic>(sql, new { Limit = limit, UserId = userId });
                return result.ToList();
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
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasUserId = await HasColumnAsync(db, "PortfolioRebalancingLog", "UserId");
                int userId = GetCurrentUserId();
                if (hasUserId && userId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [리밸런싱 로그 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                    return new List<dynamic>();
                }

                string sql = @"
                        SELECT TOP (@Limit) 
                            l.*, 
                            (SELECT COUNT(*) FROM RebalancingAction WHERE RebalancingLogId = l.Id) AS ActionCount
                        FROM PortfolioRebalancingLog l";

                if (hasUserId)
                    sql += " WHERE l.UserId = @UserId";

                sql += " ORDER BY l.CreatedAt DESC";

                var result = await db.QueryAsync<dynamic>(sql, new { Limit = limit, UserId = userId });
                return result.ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 리밸런싱 로그 조회 실패: {ex.Message}");
                return new List<dynamic>();
            }
        }

        /// <summary>
        /// [블랙리스트 복구] 최근 1시간 이내에 종료된 포지션 조회
        /// 엔진 시작 시 메모리 블랙리스트를 DB에서 복구하기 위함
        /// </summary>
        public async Task<List<(string Symbol, DateTime LastExitTime)>> GetRecentlyClosedPositionsAsync(int withinMinutes = 60, int? userId = null)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                bool hasTradeHistoryUserId = await HasColumnAsync(db, "TradeHistory", "UserId");
                int resolvedUserId = userId.GetValueOrDefault();
                if (resolvedUserId <= 0)
                    resolvedUserId = GetCurrentUserId();

                // [v5.3.4] UserId 필수 적용
                if (resolvedUserId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [최근 종료 포지션 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                    return new List<(string, DateTime)>();
                }

                string sql = @"
                        SELECT DISTINCT Symbol, MAX(ExitTime) AS LastExitTime
                        FROM dbo.TradeHistory
                        WHERE ExitTime IS NOT NULL
                          AND ExitTime > DATEADD(MINUTE, -@WithinMinutes, GETDATE())
                          AND UserId = @UserId";

                sql += @"
                        GROUP BY Symbol";

                var result = await db.QueryAsync<(string Symbol, DateTime LastExitTime)>(sql, new
                {
                    WithinMinutes = withinMinutes,
                    UserId = resolvedUserId
                });
                return result.ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB] 최근 종료 포지션 조회 실패: {ex.Message}");
                return new List<(string, DateTime)>();
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
                                      @PumpTp1Roe, @PumpTp2Roe, @PumpTimeStopMinutes, @PumpStopDistanceWarnPct, @PumpStopDistanceBlockPct, @MajorTrendProfile,
                                      @PumpBreakEvenRoe, @PumpTrailingStartRoe, @PumpTrailingGapRoe,
                                      @PumpStopLossRoe, @PumpMargin, @PumpLeverage,
                                      @PumpFirstTakeProfitRatioPct, @PumpStairStep1Roe, @PumpStairStep2Roe, @PumpStairStep3Roe,
                                      @MajorLeverage, @MajorMargin, @MajorBreakEvenRoe, @MajorTp1Roe, @MajorTp2Roe,
                                      @MajorTrailingStartRoe, @MajorTrailingGapRoe, @MajorStopLossRoe,
                                      @EnableMajorTrading)
                            AS source (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe,
                                      PumpTp1Roe, PumpTp2Roe, PumpTimeStopMinutes, PumpStopDistanceWarnPct, PumpStopDistanceBlockPct, MajorTrendProfile,
                                      PumpBreakEvenRoe, PumpTrailingStartRoe, PumpTrailingGapRoe,
                                      PumpStopLossRoe, PumpMargin, PumpLeverage,
                                      PumpFirstTakeProfitRatioPct, PumpStairStep1Roe, PumpStairStep2Roe, PumpStairStep3Roe,
                                      MajorLeverage, MajorMargin, MajorBreakEvenRoe, MajorTp1Roe, MajorTp2Roe,
                                      MajorTrailingStartRoe, MajorTrailingGapRoe, MajorStopLossRoe,
                                      EnableMajorTrading)
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
                                target.PumpBreakEvenRoe = source.PumpBreakEvenRoe,
                                target.PumpTrailingStartRoe = source.PumpTrailingStartRoe,
                                target.PumpTrailingGapRoe = source.PumpTrailingGapRoe,
                                target.PumpStopLossRoe = source.PumpStopLossRoe,
                                target.PumpMargin = source.PumpMargin,
                                target.PumpLeverage = source.PumpLeverage,
                                target.PumpFirstTakeProfitRatioPct = source.PumpFirstTakeProfitRatioPct,
                                target.PumpStairStep1Roe = source.PumpStairStep1Roe,
                                target.PumpStairStep2Roe = source.PumpStairStep2Roe,
                                target.PumpStairStep3Roe = source.PumpStairStep3Roe,
                                target.MajorLeverage = source.MajorLeverage,
                                target.MajorMargin = source.MajorMargin,
                                target.MajorBreakEvenRoe = source.MajorBreakEvenRoe,
                                target.MajorTp1Roe = source.MajorTp1Roe,
                                target.MajorTp2Roe = source.MajorTp2Roe,
                                target.MajorTrailingStartRoe = source.MajorTrailingStartRoe,
                                target.MajorTrailingGapRoe = source.MajorTrailingGapRoe,
                                target.MajorStopLossRoe = source.MajorStopLossRoe,
                                target.EnableMajorTrading = source.EnableMajorTrading,
                                target.UpdatedAt = GETUTCDATE()
                        WHEN NOT MATCHED THEN
                            INSERT (Id, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, TrailingStartRoe, TrailingDropRoe,
                                    PumpTp1Roe, PumpTp2Roe, PumpTimeStopMinutes, PumpStopDistanceWarnPct, PumpStopDistanceBlockPct, MajorTrendProfile,
                                    PumpBreakEvenRoe, PumpTrailingStartRoe, PumpTrailingGapRoe,
                                    PumpStopLossRoe, PumpMargin, PumpLeverage,
                                        PumpFirstTakeProfitRatioPct, PumpStairStep1Roe, PumpStairStep2Roe, PumpStairStep3Roe,
                                    MajorLeverage, MajorMargin, MajorBreakEvenRoe, MajorTp1Roe, MajorTp2Roe,
                                    MajorTrailingStartRoe, MajorTrailingGapRoe, MajorStopLossRoe, EnableMajorTrading)
                            VALUES (@UserId, @DefaultLeverage, @DefaultMargin, @TargetRoe, @StopLossRoe, @TrailingStartRoe, @TrailingDropRoe,
                                    @PumpTp1Roe, @PumpTp2Roe, @PumpTimeStopMinutes, @PumpStopDistanceWarnPct, @PumpStopDistanceBlockPct, @MajorTrendProfile,
                                    @PumpBreakEvenRoe, @PumpTrailingStartRoe, @PumpTrailingGapRoe,
                                    @PumpStopLossRoe, @PumpMargin, @PumpLeverage,
                                        @PumpFirstTakeProfitRatioPct, @PumpStairStep1Roe, @PumpStairStep2Roe, @PumpStairStep3Roe,
                                    @MajorLeverage, @MajorMargin, @MajorBreakEvenRoe, @MajorTp1Roe, @MajorTp2Roe,
                                    @MajorTrailingStartRoe, @MajorTrailingGapRoe, @MajorStopLossRoe, @EnableMajorTrading);";

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
                    parameters.Add("@PumpBreakEvenRoe", settings.PumpBreakEvenRoe);
                    parameters.Add("@PumpTrailingStartRoe", settings.PumpTrailingStartRoe);
                    parameters.Add("@PumpTrailingGapRoe", settings.PumpTrailingGapRoe);
                    parameters.Add("@PumpStopLossRoe", settings.PumpStopLossRoe);
                    parameters.Add("@PumpMargin", settings.PumpMargin);
                    parameters.Add("@PumpLeverage", settings.PumpLeverage);
                    parameters.Add("@PumpFirstTakeProfitRatioPct", settings.PumpFirstTakeProfitRatioPct);
                    parameters.Add("@PumpStairStep1Roe", settings.PumpStairStep1Roe);
                    parameters.Add("@PumpStairStep2Roe", settings.PumpStairStep2Roe);
                    parameters.Add("@PumpStairStep3Roe", settings.PumpStairStep3Roe);
                    parameters.Add("@MajorLeverage", settings.MajorLeverage);
                    parameters.Add("@MajorMargin", settings.MajorMargin);
                    parameters.Add("@MajorBreakEvenRoe", settings.MajorBreakEvenRoe);
                    parameters.Add("@MajorTp1Roe", settings.MajorTp1Roe);
                    parameters.Add("@MajorTp2Roe", settings.MajorTp2Roe);
                    parameters.Add("@MajorTrailingStartRoe", settings.MajorTrailingStartRoe);
                    parameters.Add("@MajorTrailingGapRoe", settings.MajorTrailingGapRoe);
                    parameters.Add("@MajorStopLossRoe", settings.MajorStopLossRoe);
                    parameters.Add("@EnableMajorTrading", settings.EnableMajorTrading);

                    try
                    {
                        await db.ExecuteAsync(sql, parameters);
                    }
                    catch (SqlException ex) when (ex.Message.Contains("PumpTp1Roe") || ex.Message.Contains("PumpTp2Roe") || ex.Message.Contains("PumpTimeStopMinutes") || ex.Message.Contains("PumpStopDistanceWarnPct") || ex.Message.Contains("PumpStopDistanceBlockPct") || ex.Message.Contains("MajorTrendProfile") || ex.Message.Contains("PumpBreakEvenRoe") || ex.Message.Contains("PumpTrailingStartRoe") || ex.Message.Contains("PumpTrailingGapRoe") || ex.Message.Contains("PumpStopLossRoe") || ex.Message.Contains("PumpMargin") || ex.Message.Contains("PumpLeverage") || ex.Message.Contains("PumpFirstTakeProfitRatioPct") || ex.Message.Contains("PumpStairStep1Roe") || ex.Message.Contains("PumpStairStep2Roe") || ex.Message.Contains("PumpStairStep3Roe") || ex.Message.Contains("MajorLeverage") || ex.Message.Contains("MajorMargin") || ex.Message.Contains("MajorBreakEvenRoe") || ex.Message.Contains("MajorTp1Roe") || ex.Message.Contains("MajorTp2Roe") || ex.Message.Contains("MajorTrailingStartRoe") || ex.Message.Contains("MajorTrailingGapRoe") || ex.Message.Contains("MajorStopLossRoe"))
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

       
        public async Task<bool> UpsertElliottWaveAnchorStateAsync(ElliottWaveAnchorState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Symbol))
                return false;

            try
            {
                if (!TryGetCurrentUserIdForSave($"{state.Symbol} ElliottAnchor 저장", out int userId))
                    return false;

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                const string mergeSql = @"
MERGE dbo.ElliottWaveAnchors AS target
USING (
    SELECT
        @UserId AS UserId,
        @Symbol AS Symbol,
        @CurrentPhase AS CurrentPhase,
        @Phase1StartTime AS Phase1StartTime,
        @Phase1LowPrice AS Phase1LowPrice,
        @Phase1HighPrice AS Phase1HighPrice,
        @Phase1Volume AS Phase1Volume,
        @Phase2StartTime AS Phase2StartTime,
        @Phase2LowPrice AS Phase2LowPrice,
        @Phase2HighPrice AS Phase2HighPrice,
        @Phase2Volume AS Phase2Volume,
        @Fib500Level AS Fib500Level,
        @Fib0618Level AS Fib0618Level,
        @Fib786Level AS Fib786Level,
        @Fib1618Target AS Fib1618Target,
        @AnchorLowPoint AS AnchorLowPoint,
        @AnchorHighPoint AS AnchorHighPoint,
        @AnchorIsConfirmed AS AnchorIsConfirmed,
        @AnchorIsLocked AS AnchorIsLocked,
        @AnchorConfirmedAtUtc AS AnchorConfirmedAtUtc,
        @LowPivotStrength AS LowPivotStrength,
        @HighPivotStrength AS HighPivotStrength,
        @UpdatedAtUtc AS UpdatedAtUtc
) AS source
ON target.UserId = source.UserId AND target.Symbol = source.Symbol
WHEN MATCHED THEN
    UPDATE SET
        CurrentPhase = source.CurrentPhase,
        Phase1StartTime = source.Phase1StartTime,
        Phase1LowPrice = source.Phase1LowPrice,
        Phase1HighPrice = source.Phase1HighPrice,
        Phase1Volume = source.Phase1Volume,
        Phase2StartTime = source.Phase2StartTime,
        Phase2LowPrice = source.Phase2LowPrice,
        Phase2HighPrice = source.Phase2HighPrice,
        Phase2Volume = source.Phase2Volume,
        Fib500Level = source.Fib500Level,
        Fib0618Level = source.Fib0618Level,
        Fib786Level = source.Fib786Level,
        Fib1618Target = source.Fib1618Target,
        AnchorLowPoint = source.AnchorLowPoint,
        AnchorHighPoint = source.AnchorHighPoint,
        AnchorIsConfirmed = source.AnchorIsConfirmed,
        AnchorIsLocked = source.AnchorIsLocked,
        AnchorConfirmedAtUtc = source.AnchorConfirmedAtUtc,
        LowPivotStrength = source.LowPivotStrength,
        HighPivotStrength = source.HighPivotStrength,
        UpdatedAtUtc = source.UpdatedAtUtc
WHEN NOT MATCHED THEN
    INSERT
    (
        UserId, Symbol, CurrentPhase, Phase1StartTime, Phase1LowPrice, Phase1HighPrice, Phase1Volume,
        Phase2StartTime, Phase2LowPrice, Phase2HighPrice, Phase2Volume,
        Fib500Level, Fib0618Level, Fib786Level, Fib1618Target,
        AnchorLowPoint, AnchorHighPoint, AnchorIsConfirmed, AnchorIsLocked, AnchorConfirmedAtUtc,
        LowPivotStrength, HighPivotStrength, UpdatedAtUtc
    )
    VALUES
    (
        source.UserId, source.Symbol, source.CurrentPhase, source.Phase1StartTime, source.Phase1LowPrice, source.Phase1HighPrice, source.Phase1Volume,
        source.Phase2StartTime, source.Phase2LowPrice, source.Phase2HighPrice, source.Phase2Volume,
        source.Fib500Level, source.Fib0618Level, source.Fib786Level, source.Fib1618Target,
        source.AnchorLowPoint, source.AnchorHighPoint, source.AnchorIsConfirmed, source.AnchorIsLocked, source.AnchorConfirmedAtUtc,
        source.LowPivotStrength, source.HighPivotStrength, source.UpdatedAtUtc
    );";

                await db.ExecuteAsync(mergeSql, new
                {
                    UserId = userId,
                    Symbol = TrimForDb(state.Symbol, 50),
                    state.CurrentPhase,
                    Phase1StartTime = state.Phase1StartTime,
                    state.Phase1LowPrice,
                    state.Phase1HighPrice,
                    Phase1Volume = SanitizeFloatForDb(state.Phase1Volume),
                    Phase2StartTime = state.Phase2StartTime,
                    state.Phase2LowPrice,
                    state.Phase2HighPrice,
                    Phase2Volume = SanitizeFloatForDb(state.Phase2Volume),
                    state.Fib500Level,
                    state.Fib0618Level,
                    state.Fib786Level,
                    state.Fib1618Target,
                    state.AnchorLowPoint,
                    state.AnchorHighPoint,
                    state.AnchorIsConfirmed,
                    state.AnchorIsLocked,
                    AnchorConfirmedAtUtc = state.AnchorConfirmedAtUtc,
                    state.LowPivotStrength,
                    state.HighPivotStrength,
                    UpdatedAtUtc = DateTime.UtcNow
                });

                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB][ElliottAnchor] 저장 실패: {state.Symbol} | {ex.Message}");
                return false;
            }
        }

        public async Task<List<ElliottWaveAnchorState>> LoadElliottWaveAnchorStatesAsync(IEnumerable<string>? symbols = null)
        {
            try
            {
                int userId = GetCurrentUserId();
                if (userId <= 0)
                    return new List<ElliottWaveAnchorState>();

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                var symbolList = symbols?
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => TrimForDb(s, 50))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                if (symbolList.Count == 0)
                {
                    const string sqlAll = @"
SELECT UserId, Symbol, CurrentPhase,
       Phase1StartTime, Phase1LowPrice, Phase1HighPrice, Phase1Volume,
       Phase2StartTime, Phase2LowPrice, Phase2HighPrice, Phase2Volume,
       Fib500Level, Fib0618Level, Fib786Level, Fib1618Target,
       AnchorLowPoint, AnchorHighPoint, AnchorIsConfirmed, AnchorIsLocked,
       AnchorConfirmedAtUtc, LowPivotStrength, HighPivotStrength, UpdatedAtUtc
FROM dbo.ElliottWaveAnchors
WHERE UserId = @UserId";

                    return (await db.QueryAsync<ElliottWaveAnchorState>(sqlAll, new { UserId = userId })).ToList();
                }

                const string sqlBySymbols = @"
SELECT UserId, Symbol, CurrentPhase,
       Phase1StartTime, Phase1LowPrice, Phase1HighPrice, Phase1Volume,
       Phase2StartTime, Phase2LowPrice, Phase2HighPrice, Phase2Volume,
       Fib500Level, Fib0618Level, Fib786Level, Fib1618Target,
       AnchorLowPoint, AnchorHighPoint, AnchorIsConfirmed, AnchorIsLocked,
       AnchorConfirmedAtUtc, LowPivotStrength, HighPivotStrength, UpdatedAtUtc
FROM dbo.ElliottWaveAnchors
WHERE UserId = @UserId
  AND Symbol IN @Symbols";

                return (await db.QueryAsync<ElliottWaveAnchorState>(sqlBySymbols, new { UserId = userId, Symbols = symbolList })).ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB][ElliottAnchor] 로드 실패: {ex.Message}");
                return new List<ElliottWaveAnchorState>();
            }
        }

        public async Task<bool> DeleteElliottWaveAnchorStateAsync(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return false;

            try
            {
                int userId = GetCurrentUserId();
                if (userId <= 0)
                    return false;

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                const string deleteSql = @"
DELETE FROM dbo.ElliottWaveAnchors
WHERE UserId = @UserId
  AND Symbol = @Symbol";

                await db.ExecuteAsync(deleteSql, new
                {
                    UserId = userId,
                    Symbol = TrimForDb(symbol, 50)
                });

                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB][ElliottAnchor] 삭제 실패: {symbol} | {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // [AI 관제탑] Bot_Log — AI 게이트 시그널 기록
        // ─────────────────────────────────────────────────────────────────────
        // DDL (최초 1회 실행):
        // CREATE TABLE dbo.Bot_Log (
        //     Id          BIGINT         IDENTITY(1,1) PRIMARY KEY,
        //     UserId      INT            NOT NULL DEFAULT 0,
        //     EventTime   DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
        //     Symbol      NVARCHAR(20)   NOT NULL,
        //     Direction   NVARCHAR(10)   NOT NULL,   -- LONG / SHORT
        //     CoinType    NVARCHAR(20)   NOT NULL,
        //     Allowed     BIT            NOT NULL,
        //     Reason      NVARCHAR(200)  NOT NULL,
        //     ML_Conf     REAL           NOT NULL DEFAULT 0,
        //     TF_Conf     REAL           NOT NULL DEFAULT 0,
        //     TrendScore  REAL           NOT NULL DEFAULT 0,
        //     RSI         REAL           NULL,
        //     BBPosition  REAL           NULL,
        //     DecisionId  NVARCHAR(50)   NULL
        // );
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// AI 게이트 결과를 dbo.Bot_Log 테이블에 저장합니다.
        /// 테이블이 없으면 자동 생성 후 삽입합니다.
        /// </summary>
        public async Task SaveAiSignalLogAsync(
            string symbol, string direction, string coinType,
            bool allowed, string reason,
            float mlConf, float tfConf, float trendScore,
            float rsi = 0f, float bbPos = 0f,
            string? decisionId = null)
        {
          

            const string insertSql = @"
INSERT INTO dbo.Bot_Log
    (UserId, Symbol, Direction, CoinType, Allowed, Reason,
     ML_Conf, TF_Conf, TrendScore, RSI, BBPosition, DecisionId)
VALUES
    (@UserId, @Symbol, @Direction, @CoinType, @Allowed, @Reason,
     @ML_Conf, @TF_Conf, @TrendScore, @RSI, @BBPosition, @DecisionId)";

            try
            {
                if (!TryGetCurrentUserIdForSave($"{symbol} AI 게이트 로그", out int userId))
                    return;

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(insertSql, new
                {
                    UserId    = userId,
                    Symbol    = TrimForDb(symbol, 20),
                    Direction = TrimForDb(direction, 10),
                    CoinType  = TrimForDb(coinType, 20),
                    Allowed   = allowed,
                    Reason    = TrimForDb(reason, 200),
                    ML_Conf   = SanitizeFloatForDb(mlConf),
                    TF_Conf   = SanitizeFloatForDb(tfConf),
                    TrendScore= SanitizeFloatForDb(trendScore),
                    RSI       = (object?)SanitizeFloatForDb(rsi) ?? DBNull.Value,
                    BBPosition= (object?)SanitizeFloatForDb(bbPos) ?? DBNull.Value,
                    DecisionId= string.IsNullOrWhiteSpace(decisionId) ? (object)DBNull.Value : decisionId
                });
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB][Bot_Log] 저장 실패: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // [주문 오류 기록] Order_Error — 부분청산/진입 주문 실패 기록
        // ─────────────────────────────────────────────────────────────────────
        // DDL (최초 1회 자동 생성):
        // CREATE TABLE dbo.Order_Error (
        //     Id          BIGINT         IDENTITY(1,1) PRIMARY KEY,
        //     UserId      INT            NOT NULL DEFAULT 0,
        //     EventTime   DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
        //     Symbol      NVARCHAR(20)   NOT NULL,
        //     Side        NVARCHAR(10)   NOT NULL,
        //     OrderType   NVARCHAR(30)   NOT NULL,
        //     Quantity    DECIMAL(18,8)  NOT NULL,
        //     ErrorCode   INT            NULL,
        //     ErrorMsg    NVARCHAR(500)  NOT NULL,
        //     Resolved    BIT            NOT NULL DEFAULT 0,
        //     RetryCount  INT            NOT NULL DEFAULT 0,
        //     Resolution  NVARCHAR(200)  NULL
        // );
        // ─────────────────────────────────────────────────────────────────────

        private static bool _orderErrorTableChecked = false;

        public async Task SaveOrderErrorAsync(
            string symbol, string side, string orderType,
            decimal quantity, int? errorCode, string errorMsg,
            bool resolved = false, int retryCount = 0, string? resolution = null)
        {
            try
            {
                if (!TryGetCurrentUserIdForSave($"{symbol} 주문오류", out int userId))
                    return;

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                await db.ExecuteAsync(@"
INSERT INTO dbo.Order_Error
    (UserId, Symbol, Side, OrderType, Quantity, ErrorCode, ErrorMsg, Resolved, RetryCount, Resolution)
VALUES
    (@UserId, @Symbol, @Side, @OrderType, @Quantity, @ErrorCode, @ErrorMsg, @Resolved, @RetryCount, @Resolution)",
                    new
                    {
                        UserId = userId,
                        Symbol = TrimForDb(symbol, 20),
                        Side = TrimForDb(side, 10),
                        OrderType = TrimForDb(orderType, 30),
                        Quantity = quantity,
                        ErrorCode = errorCode.HasValue ? (object)errorCode.Value : DBNull.Value,
                        ErrorMsg = TrimForDb(errorMsg, 500),
                        Resolved = resolved,
                        RetryCount = retryCount,
                        Resolution = string.IsNullOrWhiteSpace(resolution) ? (object)DBNull.Value : TrimForDb(resolution, 200)
                    });
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [DB][Order_Error] 저장 실패: {ex.Message}");
            }
        }
    }
}
