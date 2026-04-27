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

        /// <summary>[v5.21.4] 앱 전역 공유 인스턴스 — SettingsWindow 등 매번 new 방지 (Ensure DDL 반복 실행 차단)</summary>
        public static DbManager? Shared { get; private set; }
        public static DbManager GetShared(string connectionString)
        {
            if (Shared == null) Shared = new DbManager(connectionString);
            return Shared;
        }

        /// <summary>
        /// [v5.10.89] 청산 INSERT/UPDATE 성공 시점에 텔레그램 알림 중앙 처리.
        /// 사용자 지적: "API에 등록된거 바이낸스에서 처리되면 메시지 받아서 insert 할거면 거기서 메시지 보내야지"
        /// 각 caller에서 개별 NotifyProfitAsync 호출 대신 DB 저장 지점에서 한 번만 호출 → 경로 누락 방지.
        /// </summary>
        // [v5.10.93→95] 텔레그램 중복 알림 방지 — partial fill 청크 합산 + 비동기 발송
        //   v5.10.93 결함: dedup key=symbol|pnl(2dp) → Binance partial fill 청크별 PnL 다 달라 키 다름 → 18번 발송
        //   사례: HUSDT SL 1건이 40초 동안 18 청크 fill → 18번 텔레그램
        //   수정: 첫 호출 시 90초 합산 윈도우 시작, 누적 PnL 모아서 윈도우 끝에 1번만 발송
        private sealed class PendingNotify
        {
            public decimal PnL;
            public decimal PnLPercentMaxAbs;
            public int ChunkCount;
            public DateTime FirstAt;
            public string LastKind = "";
        }
        private static readonly ConcurrentDictionary<string, PendingNotify> _notifyAggregate
            = new ConcurrentDictionary<string, PendingNotify>(StringComparer.OrdinalIgnoreCase);
        private const int NOTIFY_AGGREGATE_WINDOW_SECONDS = 90;

        private static void TryNotifyProfit(string symbol, decimal pnl, decimal pnlPercent, string kind)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(symbol)) return;
                // [v5.14.1 BUGFIX] 0-PnL 팬텀 알림 전면 차단 — 기존: ENTRY/FLIP_ENTRY만 필터 → COMPLETE 류 통과
                //   증상: "💰 익절 완료 UBUSDT 0.00 USDT 0.00%" 텔레그램 발송 (EXTERNAL_CLOSE_SYNC 시 PnL 계산 실패)
                //   수정: kind 무관 모든 0-PnL + 0-PnLPct 조합 차단 (의미 없는 알림)
                if (pnl == 0 && pnlPercent == 0)
                {
                    MainWindow.Instance?.AddLog($"🧹 [Notify][{kind}] {symbol} 0-PnL 알림 스킵 (팬텀 차단)");
                    return;
                }

                var now = DateTime.UtcNow;
                bool isNew = false;
                var agg = _notifyAggregate.AddOrUpdate(
                    symbol,
                    _ =>
                    {
                        isNew = true;
                        return new PendingNotify { PnL = pnl, PnLPercentMaxAbs = Math.Abs(pnlPercent), ChunkCount = 1, FirstAt = now, LastKind = kind };
                    },
                    (_, existing) =>
                    {
                        existing.PnL += pnl;
                        if (Math.Abs(pnlPercent) > existing.PnLPercentMaxAbs) existing.PnLPercentMaxAbs = Math.Abs(pnlPercent);
                        existing.ChunkCount++;
                        existing.LastKind = kind;
                        return existing;
                    });

                if (!isNew)
                {
                    MainWindow.Instance?.AddLog($"🔁 [Notify][{kind}] {symbol} 청크 합산 중 (#{agg.ChunkCount} +PnL={pnl:+0.00;-0.00})");
                    return;
                }

                // 신규 심볼 → 90초 후 1번만 발송 (fire-and-forget)
                MainWindow.Instance?.AddLog($"📨 [Notify][{kind}] {symbol} 청산 감지 → {NOTIFY_AGGREGATE_WINDOW_SECONDS}초 합산 후 발송 예약");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(NOTIFY_AGGREGATE_WINDOW_SECONDS));
                        if (_notifyAggregate.TryRemove(symbol, out var final))
                        {
                            decimal pnlPctSign = final.PnL >= 0 ? final.PnLPercentMaxAbs : -final.PnLPercentMaxAbs;
                            string chunkSuffix = final.ChunkCount > 1 ? $" ({final.ChunkCount}청크 합산)" : "";
                            MainWindow.Instance?.AddLog($"✉️ [Notify][AGG] {symbol}{chunkSuffix} 최종 PnL={final.PnL:+0.00;-0.00} ROE={pnlPctSign:+0.0;-0.0}%");
                            _ = NotificationService.Instance.NotifyProfitAsync(symbol, final.PnL, pnlPctSign, 0m);
                        }
                    }
                    catch (Exception aex) { MainWindow.Instance?.AddLog($"⚠️ [Notify][AGG] {symbol} 합산 발송 예외: {aex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ [Notify][{kind}] {symbol} 텔레그램 호출 예외: {ex.Message}");
            }
        }

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

        public sealed class HoldingOverHourStat
        {
            public string Symbol { get; set; } = string.Empty;
            public int TotalTrades { get; set; }
            public int WinCount { get; set; }
            public int LossCount { get; set; }
            public double WinRate { get; set; }
            public decimal? AvgPnL { get; set; }
            public decimal? AvgPnLPercent { get; set; }
            public double? AvgHoldingMinutes { get; set; }
            public decimal? TotalPnL { get; set; }
        }

        // [v5.10.61] 생성자의 Ensure 메서드들은 앱당 1회만 실행 — DbManager를 여러 번 new 해도 반복 X
        private static int _schemaInitStarted = 0;

        public DbManager(string connectionString)
        {
            _connectionString = connectionString;

            // 스키마/인덱스/SP 자동 생성은 앱당 1회만 (DDL schema lock으로 인한 설정창 지연 방지)
            if (Interlocked.Exchange(ref _schemaInitStarted, 1) == 0)
            {
                _ = EnsureCategoryColumnAsync();
                _ = EnsureCandleDataIndexAsync();
                _ = EnsureOpenTimeIndexesAsync();
                _ = EnsureTradeHistoryUserIndexAsync();
                _ = TradingBot.DbProcedures.EnsureAllAsync(connectionString);
            }
        }

        /// <summary>[v5.2.0] CandleData 학습 쿼리 성능 인덱스</summary>
        private async Task EnsureCandleDataIndexAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF OBJECT_ID('dbo.CandleData') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CandleData') AND name = 'IX_CandleData_IntervalText_Symbol_OpenTime')
BEGIN
    CREATE INDEX IX_CandleData_IntervalText_Symbol_OpenTime
        ON dbo.CandleData (IntervalText, Symbol, OpenTime DESC);
END

-- [v5.20.7] Symbol leading 인덱스 — GROUP BY Symbol / WHERE Symbol=? 쿼리 타임아웃 방지
--   (diag-validator-direct.ps1 에서 SQL Fill 30초+ 타임아웃 발생 → 풀스캔이 원인)
IF OBJECT_ID('dbo.CandleData') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CandleData') AND name = 'IX_CandleData_Symbol_IntervalText_OpenTime')
BEGIN
    CREATE INDEX IX_CandleData_Symbol_IntervalText_OpenTime
        ON dbo.CandleData (Symbol, IntervalText, OpenTime DESC);
END

-- [v5.20.7] BacktestDataset 테이블 — 90일 × 100심볼 라벨링된 학습/검증용 데이터
IF OBJECT_ID('dbo.BacktestDataset', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BacktestDataset (
        Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol      NVARCHAR(40) NOT NULL,
        IntervalText NVARCHAR(8) NOT NULL,
        OpenTime    DATETIME2 NOT NULL,
        ClosePrice  DECIMAL(28,10) NOT NULL,
        EMA20_5m    DECIMAL(28,10) NULL,
        EMA50_15m   DECIMAL(28,10) NULL,
        RSI14       DECIMAL(10,4) NULL,
        ATR14       DECIMAL(28,10) NULL,
        VolMA20     DECIMAL(28,4) NULL,
        Volume      DECIMAL(28,4) NULL,
        Label_TP_First BIT NULL,
        Label_SL_First BIT NULL,
        TP_Pct      DECIMAL(8,4) NULL,
        SL_Pct      DECIMAL(8,4) NULL,
        WindowMinutes INT NULL,
        CreatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UX_BacktestDataset_Sym_Int_Time UNIQUE (Symbol, IntervalText, OpenTime)
    );
    CREATE INDEX IX_BacktestDataset_Symbol ON dbo.BacktestDataset (Symbol, OpenTime DESC);
END", commandTimeout: 120);
            }
            catch { }
        }

        /// <summary>[v5.10.42] OpenTime 조회 성능 인덱스 — MarketCandles/CandleHistory/MarketData 테이블
        /// 기존에 CandleData만 인덱스 있었고 나머지 3개 테이블은 풀스캔 → 타임아웃 → "OpenTime 조회 실패"
        /// </summary>
        private async Task EnsureOpenTimeIndexesAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF OBJECT_ID('dbo.MarketCandles') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.MarketCandles') AND name = 'IX_MarketCandles_Symbol_OpenTime')
BEGIN
    CREATE INDEX IX_MarketCandles_Symbol_OpenTime ON dbo.MarketCandles (Symbol, OpenTime DESC);
END

IF OBJECT_ID('dbo.CandleHistory') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CandleHistory') AND name = 'IX_CandleHistory_Symbol_Interval_OpenTime')
BEGIN
    CREATE INDEX IX_CandleHistory_Symbol_Interval_OpenTime ON dbo.CandleHistory (Symbol, [Interval], OpenTime DESC);
END

IF OBJECT_ID('dbo.MarketData') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.MarketData') AND name = 'IX_MarketData_Symbol_Interval_OpenTime')
BEGIN
    CREATE INDEX IX_MarketData_Symbol_Interval_OpenTime ON dbo.MarketData (Symbol, [Interval], OpenTime DESC);
END", commandTimeout: 120);
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

        /// <summary>[v5.10.46] TradeHistory UserId+IsClosed 복합 인덱스 — 미청산 포지션 조회 성능</summary>
        private async Task EnsureTradeHistoryUserIndexAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'IX_TradeHistory_UserId_IsClosed'
)
BEGIN
    CREATE INDEX IX_TradeHistory_UserId_IsClosed
        ON dbo.TradeHistory (UserId, IsClosed)
        INCLUDE (Symbol, EntryTime, EntryPrice, Quantity, Leverage, IsLong, Category);
END", commandTimeout: 60);
            }
            catch { }
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
            // 메이저 심볼 (TradingEngine.MajorSymbols 동일)
            if (!string.IsNullOrEmpty(symbol))
            {
                switch (symbol)
                {
                    case "BTCUSDT":
                    case "ETHUSDT":
                    case "SOLUSDT":
                    case "XRPUSDT":
                    case "BNBUSDT":
                        return "MAJOR";
                }
            }

            // [v5.21.3] 카테고리 분기 — SPIKE 차단 + SQUEEZE 신규 표시
            if (!string.IsNullOrEmpty(signalSource))
            {
                string s = signalSource.ToUpperInvariant();
                if (s.StartsWith("SPIKE") || s.Equals("TICK_SURGE") || s.StartsWith("CRASH"))
                    return "SPIKE";
                if (s.Contains("SQUEEZE")) return "SQUEEZE";
                if (s.Contains("BB_WALK") || s.Contains("BBWALK")) return "BB_WALK";
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

                var rows = await db.QueryAsync("EXEC dbo.sp_GetTodayStatsByCategory @todayStart, @userId", new { todayStart = todayKstStart, userId });
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
                await db.ExecuteAsync("EXEC dbo.sp_SavePositionState @UserId, @Symbol, @TakeProfitStep, @PartialProfitStage, @BreakevenPrice, @HighestROE, @StairStep, @IsBreakEvenTriggered, @HighestPrice, @LowestPrice, @IsPumpStrategy",
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
                }, commandTimeout: 8);
            }
            catch (SqlException sqlEx) when (sqlEx.Number == 2627 || sqlEx.Number == 2601)
            {
                // PK/UNIQUE 중복 — 동시 호출 race에서 다른 세션이 이미 INSERT함 → 무시
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
                var rows = await db.QueryAsync<PositionStateRow>("EXEC dbo.sp_LoadPositionStates @UserId", new { UserId = userId });

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
                await db.ExecuteAsync("EXEC dbo.sp_DeletePositionState @UserId, @Symbol",
                    new { UserId = userId, Symbol = symbol });
            }
            catch { }
        }


        private static int GetCurrentUserId()
        {
            // 로그인 후 AppConfig.CurrentUser.Id는 항상 설정됨 — 동기 DB 블로킹 호출 제거
            return AppConfig.CurrentUser?.Id ?? 0;
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

            // [v5.10.65] 인라인 3-step SELECT → sp_ResolveCloseSymbol 단일 호출
            var row = await db.QueryFirstOrDefaultAsync<(string Symbol, string MatchType)>(
                "EXEC dbo.sp_ResolveCloseSymbol @UserId, @Side, @EntryPrice, @Quantity, @EntryTime",
                new
                {
                    UserId = userId,
                    Side = inferredEntrySide,
                    EntryPrice = targetEntryPrice,
                    Quantity = targetQuantity,
                    EntryTime = targetEntryTime
                }, tx);

            string resolvedSymbol = TrimForDb(row.Symbol, 50);
            string matchType = row.MatchType ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(resolvedSymbol))
            {
                if (string.Equals(matchType, "strict", StringComparison.OrdinalIgnoreCase))
                    MainWindow.Instance?.AddLog($"ℹ️ [DB][Symbol복원] strict 매칭으로 심볼 복원: {resolvedSymbol}");
                else if (string.Equals(matchType, "side", StringComparison.OrdinalIgnoreCase))
                    MainWindow.Instance?.AddLog($"ℹ️ [DB][Symbol복원] side 매칭으로 심볼 복원: {resolvedSymbol}");
                else if (string.Equals(matchType, "open", StringComparison.OrdinalIgnoreCase))
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

                int userId = GetCurrentUserId();
                // [v5.10.65] HasColumnAsync 제거 — SP 내부에서 COL_LENGTH 체크
                // userId<=0인 경우에도 SP가 UserId 컬럼 미존재 분기로 INSERT (안전)

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                await db.ExecuteAsync(
                    "EXEC dbo.sp_MirrorToTradeLogs @UserId, @Symbol, @Side, @Strategy, @Price, @AiScore, @Time, @PnL, @PnLPercent, @EntryPrice, @ExitPrice, @Quantity, @ExitReason",
                    new
                    {
                        UserId = userId,
                        Symbol = symbolValue,
                        Side = sideValue,
                        Strategy = string.IsNullOrWhiteSpace(strategyValue) ? null : strategyValue,
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

                long id = await db.ExecuteScalarAsync<long>("EXEC dbo.sp_SaveTradePatternSnapshot @UserId, @Symbol, @Side, @Strategy, @Mode, @EntryTime, @EntryPrice, @FinalScore, @AiScore, @ElliottScore, @VolumeScore, @RsiMacdScore, @BollingerScore, @PredictedChangePct, @ScoreGap, @AtrPercent, @HtfPenalty, @Adx, @PlusDi, @MinusDi, @Rsi, @MacdHist, @BbPosition, @VolumeRatio, @SimilarityScore, @EuclideanSimilarity, @CosineSimilarity, @MatchProbability, @MatchedPatternId, @IsSuperEntry, @PositionSizeMultiplier, @TakeProfitMultiplier, @ComponentMix, @ContextJson", new
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

                int affected = await db.ExecuteAsync("EXEC dbo.sp_CompleteTradePatternSnapshot @UserId, @Symbol, @EntryTime, @ExitTime, @PnL, @PnLPercent, @ExitReason",
                    new
                    {
                        UserId = userId,
                        Symbol = symbol,
                        EntryTime = entryTime,
                        ExitTime = exitTime,
                        PnL = pnl,
                        PnLPercent = pnlPercent,
                        ExitReason = string.IsNullOrWhiteSpace(normalizedExitReason) ? null : normalizedExitReason
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

                var rows = await db.QueryAsync<TradePatternSnapshotRecord>("EXEC dbo.sp_GetLabeledTradePatternSnapshots @UserId, @Symbol, @Side, @LookbackDays, @MaxRows", new
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

                await db.ExecuteAsync("EXEC dbo.sp_SaveAiTrainingData @UserId, @Symbol, @EntryTimeUtc, @EntryPrice, @ActualProfitPct, @IsSuccess, @ShouldEnter, @LabelSource, @FeatureJson", new
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

                await db.ExecuteAsync("EXEC dbo.sp_UpsertAiTrainingRun @UserId, @ProjectName, @RunId, @Stage, @Success, @SampleCount, @Epochs, @Accuracy, @F1Score, @AUC, @BestValidationLoss, @FinalTrainLoss, @Detail", new
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

                var rows = await db.QueryAsync("EXEC dbo.sp_GetOpenTrades @UserId", new { UserId = userId });
                
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

                await db.ExecuteAsync("EXEC dbo.sp_UpsertTradeEntry @UserId, @Symbol, @Side, @Strategy, @EntryPrice, @Quantity, @AiScore, @EntryTime, @IsSimulation, @Category", new
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

                string syncCategory = ResolveTradeCategory(position.Symbol ?? string.Empty, strategy);

                // [v5.10.65] 인라인 SELECT+UPDATE/INSERT → sp_EnsureOpenTradeForPosition (OUTPUT 파라미터)
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                using var cmd = new SqlCommand("dbo.sp_EnsureOpenTradeForPosition", db)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 30
                };
                cmd.Parameters.Add("@UserId",     SqlDbType.Int).Value          = userId;
                cmd.Parameters.Add("@Symbol",     SqlDbType.NVarChar, 32).Value = position.Symbol ?? string.Empty;
                cmd.Parameters.Add("@Side",       SqlDbType.NVarChar, 8).Value  = side;
                cmd.Parameters.Add("@Strategy",   SqlDbType.NVarChar, 64).Value = strategy;
                cmd.Parameters.Add("@EntryPrice", SqlDbType.Decimal).Value      = position.EntryPrice;
                cmd.Parameters["@EntryPrice"].Precision = 18;
                cmd.Parameters["@EntryPrice"].Scale = 8;
                cmd.Parameters.Add("@Quantity",   SqlDbType.Decimal).Value      = quantity;
                cmd.Parameters["@Quantity"].Precision = 18;
                cmd.Parameters["@Quantity"].Scale = 8;
                cmd.Parameters.Add("@AiScore",    SqlDbType.Real).Value         = position.AiScore;
                cmd.Parameters.Add("@EntryTime",  SqlDbType.DateTime2).Value    = entryTime;
                cmd.Parameters.Add("@Category",   SqlDbType.NVarChar, 32).Value = (object?)syncCategory ?? DBNull.Value;

                var pOutEntryTime = cmd.Parameters.Add("@OutEntryTime", SqlDbType.DateTime2);
                pOutEntryTime.Direction = ParameterDirection.Output;
                var pOutAiScore = cmd.Parameters.Add("@OutAiScore", SqlDbType.Real);
                pOutAiScore.Direction = ParameterDirection.Output;
                var pOutCreated = cmd.Parameters.Add("@OutCreated", SqlDbType.Bit);
                pOutCreated.Direction = ParameterDirection.Output;

                await cmd.ExecuteNonQueryAsync();

                DateTime resolvedEntryTime = pOutEntryTime.Value is DateTime dt ? dt : entryTime;
                float resolvedAiScore = pOutAiScore.Value is float f ? f : position.AiScore;
                bool created = pOutCreated.Value is bool b && b;

                if (created)
                    MainWindow.Instance?.AddLog($"📝 [DB] 시작 포지션 TradeHistory 보정 insert: U{userId} {position.Symbol} {side} Qty={quantity}");

                return (true, resolvedEntryTime, resolvedAiScore, created);
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
                    await db.ExecuteAsync("EXEC dbo.sp_CompleteTrade @UserId, @Symbol, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @ExitTime, @IsSimulation",
                        new
                        {
                            UserId = userId,
                            Symbol = symbolValue,
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
                    // [v5.10.89] INSERT/UPDATE 성공 → 텔레그램 알림 (모든 청산 경로 공통)
                    TryNotifyProfit(log.Symbol, log.PnL, log.PnLPercent, "COMPLETE_UPDATE");
                    return true;
                }

                // 열린 진입건이 없을 때 INSERT로 보정
                MainWindow.Instance?.AddLog($"⚠️ [DB][TradeHistory][CloseFallback] user={userId} sym={symbolValue} openEntry=notFound action=insertRecovery");
                string entrySide = InferEntrySideFromCloseSide(sideValue);
                string fallbackStrategy = string.IsNullOrWhiteSpace(strategyValue) ? "RECOVERED_CLOSE" : strategyValue;
                string fallbackCategory = ResolveTradeCategory(symbolValue ?? string.Empty, fallbackStrategy);

                // [v5.10.65] 인라인 INSERT → sp_InsertClosedTrade
                await db.ExecuteAsync(
                    "EXEC dbo.sp_InsertClosedTrade @UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, @IsSimulation, @Category",
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
                // [v5.10.89] 보정 INSERT 성공 → 텔레그램 알림
                TryNotifyProfit(log.Symbol, log.PnL, log.PnLPercent, "COMPLETE_INSERT");
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

                    // [v5.10.65] 인라인 INSERT → sp_InsertClosedTrade
                    await db.ExecuteAsync(
                        "EXEC dbo.sp_InsertClosedTrade @UserId, @Symbol, @Side, @Strategy, @EntryPrice, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @EntryTime, @ExitTime, @IsSimulation, @Category",
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
                            IsSimulation = false,
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

                await db.ExecuteAsync("EXEC dbo.sp_TryCompleteOpenTrade @UserId, @Symbol, @ExitPrice, @Quantity, @AiScore, @PnL, @PnLPercent, @ExitReason, @ExitTime",
                    new
                    {
                        UserId = userId,
                        Symbol = log.Symbol,
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

                // [v5.10.89] DB INSERT 지점에서 중앙 텔레그램 알림 (사용자 지적 직접 반영)
                //   "api에 등록된거 바이낸스에서 처리되면 메시지 내려주는거 받아서 insert 할꺼아냐 거기서 메시지 보내줘야지"
                //   기존: 각 caller에서 개별 NotifyProfitAsync → 경로 누락 발생
                //   수정: DB 성공 insert 시점에 한 번만 호출 → 모든 경로(caller) 자동 알림
                TryNotifyProfit(log.Symbol, log.PnL, log.PnLPercent, "COMPLETE");
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
                decimal closeQty = Math.Abs(log.Quantity);
                DateTime exitTime = log.ExitTime == default ? log.Time : log.ExitTime;
                string exitReason = string.IsNullOrWhiteSpace(log.ExitReason) ? "PartialClose" : log.ExitReason;
                string inferredEntrySide = InferEntrySideFromCloseSide(log.Side);
                string logStrategy = string.IsNullOrWhiteSpace(log.Strategy) ? "PartialClose" : log.Strategy;
                string category = ResolveTradeCategory(log.Symbol ?? string.Empty, logStrategy);

                // [v5.10.65] 인라인 SELECT/UPDATE/INSERT 4단계 → sp_RecordPartialClose 단일 SP (OUTPUT 파라미터)
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                using var cmd = new SqlCommand("dbo.sp_RecordPartialClose", db)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 30
                };
                cmd.Parameters.Add("@UserId",            SqlDbType.Int).Value           = userId;
                cmd.Parameters.Add("@Symbol",            SqlDbType.NVarChar, 32).Value  = log.Symbol ?? string.Empty;
                var pCloseQty = cmd.Parameters.Add("@CloseQty", SqlDbType.Decimal);
                pCloseQty.Precision = 18; pCloseQty.Scale = 8; pCloseQty.Value = closeQty;
                var pExitPrice = cmd.Parameters.Add("@ExitPrice", SqlDbType.Decimal);
                pExitPrice.Precision = 18; pExitPrice.Scale = 8; pExitPrice.Value = exitPrice;
                cmd.Parameters.Add("@ExitTime",          SqlDbType.DateTime2).Value     = exitTime;
                cmd.Parameters.Add("@ExitReason",        SqlDbType.NVarChar, 255).Value = exitReason;
                var pPnL = cmd.Parameters.Add("@PnL", SqlDbType.Decimal);
                pPnL.Precision = 18; pPnL.Scale = 8; pPnL.Value = log.PnL;
                var pPnLPercent = cmd.Parameters.Add("@PnLPercent", SqlDbType.Decimal);
                pPnLPercent.Precision = 18; pPnLPercent.Scale = 4; pPnLPercent.Value = log.PnLPercent;
                cmd.Parameters.Add("@AiScore",           SqlDbType.Real).Value          = SanitizeFloatForDb(log.AiScore);
                cmd.Parameters.Add("@IsSimulation",      SqlDbType.Bit).Value           = log.IsSimulation;
                cmd.Parameters.Add("@InferredEntrySide", SqlDbType.NVarChar, 8).Value   = inferredEntrySide;
                cmd.Parameters.Add("@LogStrategy",       SqlDbType.NVarChar, 150).Value = logStrategy;
                cmd.Parameters.Add("@Category",          SqlDbType.NVarChar, 32).Value  = (object?)category ?? DBNull.Value;

                var pOutCase = cmd.Parameters.Add("@OutResultCase", SqlDbType.TinyInt);
                pOutCase.Direction = ParameterDirection.Output;
                var pOutEntryPrice = cmd.Parameters.Add("@OutResolvedEntryPrice", SqlDbType.Decimal);
                pOutEntryPrice.Precision = 18; pOutEntryPrice.Scale = 8;
                pOutEntryPrice.Direction = ParameterDirection.Output;
                var pOutEntryTime = cmd.Parameters.Add("@OutResolvedEntryTime", SqlDbType.DateTime2);
                pOutEntryTime.Direction = ParameterDirection.Output;
                var pOutAiScore = cmd.Parameters.Add("@OutResolvedAiScore", SqlDbType.Real);
                pOutAiScore.Direction = ParameterDirection.Output;
                var pOutEntrySide = cmd.Parameters.Add("@OutResolvedEntrySide", SqlDbType.NVarChar, 8);
                pOutEntrySide.Direction = ParameterDirection.Output;
                var pOutStrategy = cmd.Parameters.Add("@OutResolvedStrategy", SqlDbType.NVarChar, 150);
                pOutStrategy.Direction = ParameterDirection.Output;

                await cmd.ExecuteNonQueryAsync();

                byte resultCase = pOutCase.Value is byte rc ? rc : (byte)0;
                decimal resolvedEntryPrice = pOutEntryPrice.Value is decimal rep ? rep : 0m;
                DateTime resolvedEntryTime = pOutEntryTime.Value is DateTime ret ? ret : exitTime;
                float resolvedAiScore = pOutAiScore.Value is float ras ? ras : SanitizeFloatForDb(log.AiScore);

                if (resultCase == 0)
                {
                    MainWindow.Instance?.AddLog($"ℹ️ [DB] TradeHistory 부분청산 중복 감지로 스킵: U{userId} {log.Symbol} Qty={closeQty}");
                    return true;
                }

                // 미러 → TradeLogs (case 1/2/3 모두)
                await TryMirrorToTradeLogsAsync(
                    log.Symbol,
                    log.Side,
                    TrimForDb($"PARTIAL:{exitReason}", 150),
                    exitPrice,
                    resolvedAiScore,
                    exitTime,
                    log.PnL,
                    log.PnLPercent,
                    resolvedEntryPrice,
                    exitPrice,
                    closeQty,
                    exitReason);

                switch (resultCase)
                {
                    case 1:
                        MainWindow.Instance?.AddLog($"✅ [DB] TradeHistory 부분청산 잔량 0 → 전량청산 update 처리: U{userId} {log.Symbol}");
                        break;
                    case 2:
                        MainWindow.Instance?.AddLog($"✅ [DB] TradeHistory 부분청산 기록 완료: U{userId} {log.Symbol} Qty={closeQty}");
                        break;
                    case 3:
                        MainWindow.Instance?.AddLog($"⚠️ [DB] 열린 진입건 없이 부분청산 이력만 보정 insert: U{userId} {log.Symbol}");
                        break;
                }

                _ = resolvedEntryTime; // (필요 시 추가 로깅 확장)

                // [v5.10.89] 부분청산 INSERT 성공 → 텔레그램 알림 (API TP1 자동 체결 포함)
                string notifyKind = resultCase == 1 ? "PARTIAL_FINAL" : "PARTIAL";
                TryNotifyProfit(log.Symbol, log.PnL, log.PnLPercent, notifyKind);
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

            // [v5.10.68 HOTFIX] EXTERNAL_PARTIAL_CLOSE_SYNC 잔량 동기화 버그 수정
            // 기존: 정확한 "PartialClose" 문자열만 매칭 → EXTERNAL_PARTIAL_CLOSE_SYNC, EXTERNAL_PARTIAL 등은
            //       CompleteTradeAsync로 잘못 라우팅 → 활성 포지션 Quantity 미갱신 (AAVE 1.6 → 0.6 누락)
            // 수정: "Partial" 키워드 포함하면 모두 RecordPartialCloseAsync 경로로 보냄
            bool isPartialReason = !string.IsNullOrWhiteSpace(log.ExitReason)
                && log.ExitReason.IndexOf("Partial", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isPartialStrategy = !string.IsNullOrWhiteSpace(log.Strategy)
                && log.Strategy.IndexOf("Partial", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isPartialReason || isPartialStrategy)
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

                int userId = 0;
                bool hasTradeLogsUserId = await HasColumnAsync(db, "TradeLogs", "UserId");
                if (hasTradeLogsUserId)
                {
                    userId = GetCurrentUserId();
                    if (userId <= 0)
                    {
                        MainWindow.Instance?.AddLog("⚠️ [TradeLogs 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                        return new List<TradeLog>();
                    }
                }

                var result = await db.QueryAsync<TradeLog>("EXEC dbo.sp_GetTradeLogs @UserId, @Limit, @StartDate, @EndDate",
                    new { UserId = userId, Limit = limit, StartDate = startDate, EndDate = endDate });
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

                var rows = await db.QueryAsync<TradeLog>("EXEC dbo.sp_GetTradeHistory @UserId, @StartDate, @EndDate, @Limit",
                    new { UserId = userId, StartDate = startDate, EndDate = endDate, Limit = limit });
                return rows.ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB] TradeHistory 조회 실패: {ex.Message}");
                return new List<TradeLog>();
            }
        }

        public async Task<List<HoldingOverHourStat>> GetHoldingOverHourStatsAsync(int userId, DateTime? startDate = null, DateTime? endDate = null, string? symbol = null)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                var rows = await db.QueryAsync<HoldingOverHourStat>(
                    "EXEC dbo.sp_GetHoldingOverHourStats @UserId, @StartDate, @EndDate, @Symbol",
                    new { UserId = userId, StartDate = startDate, EndDate = endDate, Symbol = string.IsNullOrWhiteSpace(symbol) ? null : TrimForDb(symbol, 32) });

                return rows.ToList();
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB] 1시간 이상 보유 거래 통계 조회 실패: {ex.Message}");
                return new List<HoldingOverHourStat>();
            }
        }

        /// <summary>[ProfitRegressor] 진입 시점 캔들 지표 조회 (학습 데이터용)</summary>
        public async Task<List<TradingBot.Models.CandleData>> GetRecentCandleDataAsync(string symbol, int limit = 30)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                var result = await db.QueryAsync<TradingBot.Models.CandleData>("EXEC dbo.sp_GetRecentCandleData @Symbol, @Limit",
                    new { Symbol = symbol, Limit = limit }, commandTimeout: 60);
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
                await db.OpenAsync();
                var result = await db.QueryAsync<TradingBot.Models.CandleData>(
                    "EXEC dbo.sp_GetCandleDataByInterval @Symbol, @Interval, @Limit",
                    new { Symbol = symbol, Interval = intervalText, Limit = limit },
                    commandTimeout: 120);
                return result.ToList();
            }
            catch
            {
                return new List<TradingBot.Models.CandleData>();
            }
        }

        /// <summary>
        /// [v5.20.1] 직전 30일 동안 N봉 이상 보유한 심볼 자동 발견 — BacktestValidator 자동 fallback 용
        /// </summary>
        public async Task<List<string>> GetSymbolsWithRecentCandlesAsync(string intervalText = "5m", int minBarsLast30Days = 500)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                string sql = @"
                    SELECT Symbol
                    FROM CandleData WITH (NOLOCK)
                    WHERE IntervalText = @Interval AND OpenTime >= DATEADD(DAY, -30, GETUTCDATE())
                    GROUP BY Symbol
                    HAVING COUNT(*) >= @MinBars
                    ORDER BY COUNT(*) DESC";
                var result = await db.QueryAsync<string>(sql,
                    new { Interval = intervalText, MinBars = minBarsLast30Days },
                    commandTimeout: 120);
                return result.ToList();
            }
            catch
            {
                return new List<string>();
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
                // [v5.10.65] 인라인 CTE → sp_GetAllCandleDataForTraining
                var rows = await db.QueryAsync<TradingBot.Models.CandleData>(
                    "EXEC dbo.sp_GetAllCandleDataForTraining @Limit",
                    new { Limit = candlesPerSymbol }, commandTimeout: 60);

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
        /// - [v5.10.50] MarketCandles 로 변경 (IntervalText 파라미터 무시, 5m 고정)
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

                // [v5.10.65] 인라인 CTE+동적 IN → sp_GetBulkCandleData (CSV 파라미터)
                string? symbolsCsv = symbolFilter is { Count: > 0 }
                    ? string.Join(",", symbolFilter.Where(s => !string.IsNullOrWhiteSpace(s)))
                    : null;

                var rows = await db.QueryAsync<TradingBot.Models.CandleData>(
                    "EXEC dbo.sp_GetBulkCandleData @SymbolsCsv, @Limit",
                    new { SymbolsCsv = symbolsCsv, Limit = candlesPerSymbol },
                    commandTimeout: 120);

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
                // [v5.10.65] HasColumnAsync 제거 → SP 내부에서 COL_LENGTH 분기
                int userId = GetCurrentUserId();

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                await db.ExecuteAsync(
                    "EXEC dbo.sp_SaveArbitrageExecutionLog @UserId, @Symbol, @BuyExchange, @SellExchange, @BuyPrice, @SellPrice, @Quantity, @ProfitPercent, @BuyOrderId, @SellOrderId, @BuySuccess, @SellSuccess, @Success, @ErrorMessage, @StartTime, @EndTime",
                    new
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
                // [v5.10.65] HasColumnAsync 제거 → SP 내부에서 COL_LENGTH 분기
                int userId = GetCurrentUserId();

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                await db.ExecuteAsync(
                    "EXEC dbo.sp_SaveFundTransferLog @UserId, @FromExchange, @ToExchange, @Asset, @Amount, @WithdrawSuccess, @DepositSuccess, @Success, @ErrorMessage, @RequestTime, @StartTime, @EndTime",
                    new
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
                // [v5.10.65] HasColumnAsync 제거 → SP 내부에서 COL_LENGTH 분기
                int userId = GetCurrentUserId();

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                long logId = await db.ExecuteScalarAsync<long>(
                    "EXEC dbo.sp_SaveRebalancingLog @UserId, @TotalValue, @ActionCount, @Success, @ErrorMessage, @StartTime, @EndTime",
                    new
                    {
                        UserId = userId,
                        report.TotalValue,
                        ActionCount = report.ExecutedActions.Count,
                        report.Success,
                        report.ErrorMessage,
                        report.StartTime,
                        report.EndTime
                    });

                if (logId > 0 && report.ExecutedActions.Any())
                {
                    foreach (var action in report.ExecutedActions)
                    {
                        await db.ExecuteAsync(
                            "EXEC dbo.sp_SaveRebalancingAction @LogId, @UserId, @Asset, @CurrentPercentage, @TargetPercentage, @Deviation, @Action, @TargetValue, @Executed",
                            new
                            {
                                LogId = logId,
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
                int resolvedUserId = userId.GetValueOrDefault();
                if (resolvedUserId <= 0)
                    resolvedUserId = GetCurrentUserId();

                // [v5.3.4] UserId 필수 적용
                if (resolvedUserId <= 0)
                {
                    MainWindow.Instance?.AddLog("⚠️ [최근 종료 포지션 조회] UserId 확인 실패로 사용자별 조회를 건너뜁니다.");
                    return new List<(string, DateTime)>();
                }

                using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                // [v5.10.65] 인라인 SELECT → sp_GetRecentlyClosedPositions
                var result = await db.QueryAsync<(string Symbol, DateTime LastExitTime)>(
                    "EXEC dbo.sp_GetRecentlyClosedPositions @UserId, @WithinMinutes",
                    new
                    {
                        UserId = resolvedUserId,
                        WithinMinutes = withinMinutes
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
            // [v5.10.60] MERGE 30컬럼 Dapper → sp_SaveGeneralSettings (UPDATE 후 0행이면 INSERT)
            try
            {
                using var db = new SqlConnection(_connectionString);
                await db.ExecuteAsync("dbo.sp_SaveGeneralSettings", new
                {
                    UserId = userId,
                    settings.DefaultLeverage,
                    settings.DefaultMargin,
                    settings.TargetRoe,
                    settings.StopLossRoe,
                    settings.TrailingStartRoe,
                    settings.TrailingDropRoe,
                    settings.PumpTp1Roe,
                    settings.PumpTp2Roe,
                    settings.PumpTimeStopMinutes,
                    settings.PumpStopDistanceWarnPct,
                    settings.PumpStopDistanceBlockPct,
                    settings.MajorTrendProfile,
                    settings.PumpBreakEvenRoe,
                    settings.PumpTrailingStartRoe,
                    settings.PumpTrailingGapRoe,
                    settings.PumpStopLossRoe,
                    settings.PumpMargin,
                    settings.PumpLeverage,
                    settings.PumpFirstTakeProfitRatioPct,
                    settings.PumpStairStep1Roe,
                    settings.PumpStairStep2Roe,
                    settings.PumpStairStep3Roe,
                    settings.MajorLeverage,
                    settings.MajorMargin,
                    settings.MajorBreakEvenRoe,
                    settings.MajorTp1Roe,
                    settings.MajorTp2Roe,
                    settings.MajorTrailingStartRoe,
                    settings.MajorTrailingGapRoe,
                    settings.MajorStopLossRoe,
                    settings.EnableMajorTrading,
                    settings.MaxMajorSlots,
                    settings.MaxPumpSlots,
                    settings.MaxDailyEntries
                }, commandType: CommandType.StoredProcedure, commandTimeout: 10);

                MainWindow.Instance?.AddLog($"✅ [{userId}] GeneralSettings 저장 완료 (sp_SaveGeneralSettings)");
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
                // [v5.10.60] SELECT * → sp_LoadGeneralSettings (Dapper QuerySingle 유지, SP 호출로만 변경)
                using var db = new SqlConnection(_connectionString);
                var result = await db.QuerySingleOrDefaultAsync<TradingSettings>(
                    "dbo.sp_LoadGeneralSettings",
                    new { UserId = userId },
                    commandType: CommandType.StoredProcedure,
                    commandTimeout: 10);

                if (result != null)
                    MainWindow.Instance?.AddLog($"✅ [{userId}] GeneralSettings DB 로드 완료");
                else
                    MainWindow.Instance?.AddLog($"⚠️ [{userId}] GeneralSettings 없음 (기본값 사용)");

                return result;
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

                // [v5.10.65] 인라인 MERGE 제거 (lock 경합) → sp_UpsertElliottWaveAnchor (UPDATE → IF @@ROWCOUNT=0 INSERT)
                await db.ExecuteAsync(
                    "EXEC dbo.sp_UpsertElliottWaveAnchor @UserId, @Symbol, @CurrentPhase, @Phase1StartTime, @Phase1LowPrice, @Phase1HighPrice, @Phase1Volume, @Phase2StartTime, @Phase2LowPrice, @Phase2HighPrice, @Phase2Volume, @Fib500Level, @Fib0618Level, @Fib786Level, @Fib1618Target, @AnchorLowPoint, @AnchorHighPoint, @AnchorIsConfirmed, @AnchorIsLocked, @AnchorConfirmedAtUtc, @LowPivotStrength, @HighPivotStrength, @UpdatedAtUtc",
                    new
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

                // [v5.10.65] 인라인 SQL → sp_LoadElliottWaveAnchors (CSV 파라미터)
                string? symbolsCsv = symbolList.Count > 0 ? string.Join(",", symbolList) : null;

                var rows = await db.QueryAsync<ElliottWaveAnchorState>(
                    "EXEC dbo.sp_LoadElliottWaveAnchors @UserId, @SymbolsCsv",
                    new { UserId = userId, SymbolsCsv = symbolsCsv });

                return rows.ToList();
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

                // [v5.10.65] 인라인 DELETE → sp_DeleteElliottWaveAnchor
                await db.ExecuteAsync(
                    "EXEC dbo.sp_DeleteElliottWaveAnchor @UserId, @Symbol",
                    new
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
            try
            {
                if (!TryGetCurrentUserIdForSave($"{symbol} AI 게이트 로그", out int userId))
                    return;

                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                // [v5.10.65] 인라인 INSERT → sp_SaveAiSignalLog
                await db.ExecuteAsync(
                    "EXEC dbo.sp_SaveAiSignalLog @UserId, @Symbol, @Direction, @CoinType, @Allowed, @Reason, @ML_Conf, @TF_Conf, @TrendScore, @RSI, @BBPosition, @DecisionId",
                    new
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

                // [v5.10.65] 인라인 INSERT → sp_SaveOrderError
                await db.ExecuteAsync(
                    "EXEC dbo.sp_SaveOrderError @UserId, @Symbol, @Side, @OrderType, @Quantity, @ErrorCode, @ErrorMsg, @Resolved, @RetryCount, @Resolution",
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
