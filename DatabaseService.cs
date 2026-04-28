﻿using Binance.Net.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.IO;
using TradingBot.Models;
using CandleData = TradingBot.Models.CandleData;
using TradeLog = TradingBot.Shared.Models.TradeLog;
using PositionInfo = TradingBot.Shared.Models.PositionInfo;

namespace TradingBot.Services
{
    public class DatabaseService
    {
        // DatabaseService.cs 내부
        private string _connStr => TradingBot.AppConfig.ConnectionString; // 지연 평가로 변경
        private readonly string _trainingDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "TradingModel.csv");
        private readonly Action<string>? _logger; // 로그 대행자
        private const int QueryTimeout = 30; // 기본 쿼리 타임아웃 (30초)
        private bool _footerLogColumnExpanded; // [v5.2.2] FooterLogs Message 컬럼 확장 완료 플래그

        // [v5.10.14] 벌크 DB 동시 작업 제한 — 다수 심볼 동시 저장 시 커넥션 풀 고갈 방지
        // 최대 20개 동시 실행 (풀 200 중 벌크용 20 예약, 나머지는 읽기/쓰기용)
        private static readonly SemaphoreSlim _bulkDbSemaphore = new(20, 20);

        // [v5.10.20] OpenTime 인메모리 캐시 — DB 반복 조회 완전 제거
        // key: "{Symbol}|{Interval}", value: 해당 심볼+인터벌의 최신 OpenTime
        // 재시작 시 최초 1회만 DB 조회, 이후 Save 시 자동 갱신
        private static readonly ConcurrentDictionary<string, DateTime> _openTimeCache
            = new(StringComparer.OrdinalIgnoreCase);

        // [v5.10.22] 시작 시 동시 DB 조회 제한 — 신규 심볼 개별 조회용 (일괄 로드 후 드문 케이스)
        private static readonly SemaphoreSlim _openTimeDbSlot = new SemaphoreSlim(5, 5);
        // [v5.10.37] 전체 심볼 일괄 캐시 로드 동시 실행 방지
        private static readonly SemaphoreSlim _preloadSemaphore = new SemaphoreSlim(1, 1);
        private static volatile bool _openTimePreloadDone = false;

        public DatabaseService(Action<string>? logger = null)
        {
            _logger = logger;

            // ConnectionString 유효성 검사
            if (string.IsNullOrEmpty(_connStr))
            {
                throw new InvalidOperationException("DB 연결 문자열(ConnectionString)이 설정되지 않았습니다.");
            }

            // [추가] 연결 문자열 형식 유효성 검사
            try
            {
                var builder = new SqlConnectionStringBuilder(_connStr);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"DB 연결 문자열 형식이 올바르지 않습니다.\n(오류: {ex.Message})\n\n암호화된 문자열이라면 'IsEncrypted'가 true인지 확인하세요.");
            }
        }

        private void Log(string msg)
        {
            _logger?.Invoke(msg); // MainWindow의 Log 메소드를 대신 실행
        }

        private static async Task<bool> HasColumnAsync(SqlConnection conn, string tableName, string columnName)
        {
            const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(@FullTableName)
      AND name = @ColumnName
) THEN 1 ELSE 0 END";

            int exists = await conn.ExecuteScalarAsync<int>(sql, new
            {
                FullTableName = $"dbo.{tableName}",
                ColumnName = columnName
            });

            return exists == 1;
        }

        private static async Task<int> ResolveCurrentUserIdAsync(SqlConnection conn)
        {
            int currentUserId = TradingBot.AppConfig.CurrentUser?.Id ?? 0;
            if (currentUserId > 0)
                return currentUserId;

            string? username = TradingBot.AppConfig.CurrentUser?.Username;
            if (string.IsNullOrWhiteSpace(username))
                username = TradingBot.AppConfig.CurrentUsername;

            if (string.IsNullOrWhiteSpace(username))
                return 0;

            int? resolvedUserId = await conn.ExecuteScalarAsync<int?>(
                "SELECT TOP (1) Id FROM dbo.Users WHERE Username = @Username",
                new { Username = username.Trim() });

            if (resolvedUserId is > 0)
            {
                if (TradingBot.AppConfig.CurrentUser != null && TradingBot.AppConfig.CurrentUser.Id <= 0)
                    TradingBot.AppConfig.CurrentUser.Id = resolvedUserId.Value;

                return resolvedUserId.Value;
            }

            return 0;
        }

        private static TradeHistoryModel ToTradeHistoryModel(TradeLog row)
        {
            return new TradeHistoryModel
            {
                Id = row.Id,
                Symbol = row.Symbol ?? string.Empty,
                Side = row.Side ?? string.Empty,
                EntryPrice = row.EntryPrice,
                ExitPrice = row.ExitPrice,
                Quantity = row.Quantity,
                PnL = row.PnL,
                PnLPercent = row.PnLPercent,
                ExitReason = row.ExitReason ?? string.Empty,
                ExitTime = row.ExitTime == default ? row.Time : row.ExitTime
            };
        }

        public async Task SaveLogAsync(TradeLog log)
        {
            try
            {
                var dbManager = new DbManager(_connStr);
                await dbManager.SaveTradeLogAsync(log);
            }
            catch (Exception ex) { Console.WriteLine($"[DB Error] {ex.Message}"); }
        }

        /// <summary>
        /// [v4.7.8] 초기학습 증분 다운로드용 — (Symbol, IntervalText) 조합의 기존 데이터 범위 조회
        /// </summary>
        public async Task<(int Count, DateTime? MinTime, DateTime? MaxTime)> GetCandleDataRangeAsync(string symbol, string intervalText)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);
                var row = await conn.QuerySingleOrDefaultAsync<(int cnt, DateTime? mn, DateTime? mx)>(
                    @"SELECT COUNT(*) AS cnt, MIN(OpenTime) AS mn, MAX(OpenTime) AS mx
                      FROM CandleData WHERE Symbol = @Symbol AND IntervalText = @Interval",
                    new { Symbol = symbol, Interval = intervalText },
                    commandTimeout: QueryTimeout);
                return (row.cnt, row.mn, row.mx);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [DB] {symbol} {intervalText} 범위 조회 실패: {ex.Message}");
                return (0, null, null);
            }
        }

        /// <summary>
        /// [v5.10.42] 전체 심볼 OpenTime 일괄 로드 — 봇 시작 시 1회 실행
        /// 실패 시 false 반환 → _openTimePreloadDone 미설정 → 다음 호출 시 재시도
        /// </summary>
        private async Task<bool> BulkPreloadOpenTimeCacheAsync(string interval)
        {
            try
            {
                // [v5.10.58] 동적 SQL + Dapper → sp_BulkPreloadOpenTime (SP + ADO.NET SqlDataReader)
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                var grouped = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
                await using (var cmd = new SqlCommand("dbo.sp_BulkPreloadOpenTime", conn)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 120
                })
                {
                    cmd.Parameters.Add("@Interval", SqlDbType.NVarChar, 8).Value = interval;
                    await using var reader = await cmd.ExecuteReaderAsync();
                    var minDate = new DateTime(1800, 1, 1);
                    while (await reader.ReadAsync())
                    {
                        if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                        string sym = reader.GetString(0);
                        DateTime ot = reader.GetDateTime(1);
                        if (ot < minDate) continue;
                        if (!grouped.TryGetValue(sym, out var list))
                            grouped[sym] = list = new List<DateTime>();
                        list.Add(ot);
                    }
                }

                foreach (var kv in grouped)
                {
                    var key = $"{kv.Key}|{interval}";
                    var minMax = kv.Value.Min();
                    _openTimeCache.AddOrUpdate(key, minMax,
                        (_, existing) => minMax < existing ? minMax : existing);
                }

                Log($"✅ [DB] OpenTime 캐시 일괄 로드 완료 ({grouped.Count}개 심볼)");
                return grouped.Count > 0;
            }
            catch (Exception ex)
            {
                Log($"⚠️ [DB] OpenTime 캐시 일괄 로드 실패 (다음 호출 시 재시도): {ex.Message}");
                return false;
            }
        }

        public async Task<DateTime?> GetLatestSyncedOpenTimeAcrossTablesAsync(string symbol, string interval = "5m")
        {
            string cacheKey = $"{symbol}|{interval}";
            if (_openTimeCache.TryGetValue(cacheKey, out var cached))
                return cached;

            // [v5.22.4] BulkPreloadOpenTime 호출 비활성화 — SP 60-120초+ timeout 발생
            //   원인: 4개 candle 테이블 UNION ALL + GROUP BY → 봇 시작 시 매번 timeout
            //   영향: 봇 시작 60-120초 멈춤 + 재시도 반복 → CPU 낭비
            //   해결: SP 호출 우회, lazy 개별 조회만 사용 (아래 _openTimeDbSlot 경로)
            //         첫 OpenTime 조회 시 약간 느려지지만 캐시 채워지면 동일 성능
            _openTimePreloadDone = true;

            // 일괄 로드 후에도 없는 심볼 = 신규 심볼 (DB에 데이터 없음) → 개별 조회
            bool acquired = await _openTimeDbSlot.WaitAsync(TimeSpan.FromSeconds(30));
            if (!acquired)
            {
                Log($"⚠️ [DB] OpenTime 슬롯 대기 초과 ({symbol}) → null (전체 동기화 수행)");
                return null;
            }
            try
            {
                if (_openTimeCache.TryGetValue(cacheKey, out var cachedAfterWait))
                    return cachedAfterWait;

                // [v5.10.58] 인라인 SQL → sp_GetOpenTimeAcrossTables (SP + ADO.NET)
                await using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                var candidates = new List<DateTime>();
                await using (var cmd = new SqlCommand("dbo.sp_GetOpenTimeAcrossTables", conn)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 15
                })
                {
                    cmd.Parameters.Add("@Symbol",   SqlDbType.NVarChar, 32).Value = symbol;
                    cmd.Parameters.Add("@Interval", SqlDbType.NVarChar, 8).Value  = interval;
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var minDate = new DateTime(1800, 1, 1);
                        for (int i = 0; i < 4; i++)
                        {
                            if (reader.IsDBNull(i)) continue;
                            var dt = reader.GetDateTime(i);
                            if (dt > minDate) candidates.Add(dt);
                        }
                    }
                }

                DateTime? result = candidates.Count > 0 ? candidates.Min() : null;
                if (result.HasValue)
                    _openTimeCache.AddOrUpdate(cacheKey, result.Value,
                        (_, existing) => result.Value < existing ? result.Value : existing);
                return result;
            }
            catch (Exception ex)
            {
                Log($"⚠️ [DB] OpenTime 조회 실패 ({symbol}): {ex.Message}");
                return null;
            }
            finally
            {
                _openTimeDbSlot.Release();
            }
        }

        /// <summary>
        /// [v5.10.20] 캔들 저장 시 OpenTime 캐시 갱신 — DB 재조회 없이 항상 최신값 유지
        /// </summary>
        private static void UpdateOpenTimeCache(string symbol, string interval, IEnumerable<DateTime> openTimes)
        {
            if (string.IsNullOrEmpty(symbol)) return;
            string key = $"{symbol}|{interval}";
            foreach (var t in openTimes)
            {
                _openTimeCache.AddOrUpdate(key, t, (_, existing) => t > existing ? t : existing);
            }
        }

        // [v4.5.12] MERGE 문으로 단일 statement 처리 (기존: IF NOT EXISTS + INSERT per row)
        // - 중복 방지 + 배치 처리 모두 SQL Server 엔진에서 처리
        // - Dapper가 payload를 배치로 전송 (1 트립 × N row)
        public async Task SaveCandlesAsync(string symbol, IEnumerable<IBinanceKline> klines)
        {
            await _bulkDbSemaphore.WaitAsync();
            try
            {
            await SaveCandlesInternalAsync(symbol, klines);
            }
            finally { _bulkDbSemaphore.Release(); }
        }

        private async Task SaveCandlesInternalAsync(string symbol, IEnumerable<IBinanceKline> klines)
        {
            using var conn = new SqlConnection(_connStr);
            var payload = klines.Select(k => new
            {
                Symbol = symbol,
                IntervalText = "5m",
                OpenTime = k.OpenTime,
                OpenPrice = k.OpenPrice,
                HighPrice = k.HighPrice,
                LowPrice = k.LowPrice,
                ClosePrice = k.ClosePrice,
                Volume = k.Volume
            }).ToList();

            if (payload.Count == 0) return;

            try
            {
                // MERGE: 존재하지 않을 때만 INSERT (더 가벼운 단일 statement)
                string mergeSql = @"
                MERGE CandleData AS target
                USING (SELECT @Symbol AS Symbol, @OpenTime AS OpenTime) AS src
                ON target.Symbol = src.Symbol AND target.OpenTime = src.OpenTime
                WHEN NOT MATCHED THEN
                    INSERT (Symbol, IntervalText, OpenTime, [Open], [High], [Low], [Close], Volume)
                    VALUES (@Symbol, @IntervalText, @OpenTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume);";

                foreach (var row in payload)
                {
                    try { await conn.ExecuteAsync(mergeSql, row, commandTimeout: QueryTimeout); }
                    catch (SqlException sqlex) when (sqlex.Number == 2627 || sqlex.Number == 2601) { }
                }
            }
            catch (SqlException ex) when (ex.Message.Contains("Invalid column name 'IntervalText'"))
            {
                string legacySql = @"
                MERGE CandleData AS target
                USING (SELECT @Symbol AS Symbol, @OpenTime AS OpenTime) AS src
                ON target.Symbol = src.Symbol AND target.OpenTime = src.OpenTime
                WHEN NOT MATCHED THEN
                    INSERT (Symbol, OpenTime, [Open], [High], [Low], [Close], Volume)
                    VALUES (@Symbol, @OpenTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume);";

                foreach (var row in payload)
                {
                    try { await conn.ExecuteAsync(legacySql, row, commandTimeout: QueryTimeout); }
                    catch (SqlException sqlex) when (sqlex.Number == 2627 || sqlex.Number == 2601) { }
                }
            }

            // CandleData 저장 후 OpenTime 캐시 갱신 — DB 재조회 방지
            UpdateOpenTimeCache(symbol, "5m", payload.Select(p => p.OpenTime));
        }

        // CandleData 테이블 대량 저장 (Models.CandleData 사용)
        public async Task SaveCandleDataBulkAsync(IEnumerable<CandleData> data)
        {
            var list = data as IList<CandleData> ?? data.ToList();
            if (list.Count == 0) return;

            await _bulkDbSemaphore.WaitAsync();
            try
            {
            // [v4.7.3] 소량(< 50)은 기존 경로, 대량은 SqlBulkCopy + MERGE로 1000배 빠르게
            if (list.Count < 50)
            {
                try
                {
                    using var conn = new SqlConnection(_connStr);
                    var payload = list.Select(d => new
                    {
                        d.Symbol,
                        Interval = string.IsNullOrWhiteSpace(d.Interval) ? "5m" : d.Interval,
                        d.OpenTime,
                        d.Open,
                        d.High,
                        d.Low,
                        d.Close,
                        d.Volume
                    }).ToList();

                    // [v5.2.2] IntervalText 포함 → UQ_Candle 인덱스 활용 (풀스캔 방지)
                    string sql = @"
                    IF NOT EXISTS (SELECT 1 FROM CandleData WHERE Symbol = @Symbol AND IntervalText = @Interval AND OpenTime = @OpenTime)
                    INSERT INTO CandleData (Symbol, IntervalText, OpenTime, [Open], [High], [Low], [Close], Volume)
                    VALUES (@Symbol, @Interval, @OpenTime, @Open, @High, @Low, @Close, @Volume)";

                    foreach (var row in payload)
                    {
                        try { await conn.ExecuteAsync(sql, row, commandTimeout: QueryTimeout); }
                        catch (SqlException sqlex) when (sqlex.Number == 2627 || sqlex.Number == 2601) { }
                    }
                    UpdateOpenTimeCache(
                        list[0].Symbol,
                        string.IsNullOrWhiteSpace(list[0].Interval) ? "5m" : list[0].Interval,
                        list.Select(d => d.OpenTime));
                }
                catch (SqlException sqlex) when (sqlex.Number == 2627 || sqlex.Number == 2601)
                {
                    // [v5.2.2] UNIQUE KEY 위반 — 레이스 컨디션 무시 (정상)
                }
                catch (Exception ex) { Log($"❌ [DB] CandleData 저장 실패: {ex.Message}"); }
                return;
            }

            // 대량 경로: staging temp 테이블 → SqlBulkCopy → INSERT 중복 제거
            try
            {
                using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                const string createTempSql = @"
                    CREATE TABLE #CandleStage (
                        Symbol NVARCHAR(32) NOT NULL,
                        IntervalText NVARCHAR(8) NOT NULL,
                        OpenTime DATETIME NOT NULL,
                        [Open] DECIMAL(18,8) NOT NULL,
                        [High] DECIMAL(18,8) NOT NULL,
                        [Low]  DECIMAL(18,8) NOT NULL,
                        [Close] DECIMAL(18,8) NOT NULL,
                        Volume FLOAT NOT NULL
                    );";
                using (var cmd = new SqlCommand(createTempSql, conn)) { await cmd.ExecuteNonQueryAsync(); }

                var table = new DataTable();
                table.Columns.Add("Symbol", typeof(string));
                table.Columns.Add("IntervalText", typeof(string));
                table.Columns.Add("OpenTime", typeof(DateTime));
                table.Columns.Add("Open", typeof(decimal));
                table.Columns.Add("High", typeof(decimal));
                table.Columns.Add("Low", typeof(decimal));
                table.Columns.Add("Close", typeof(decimal));
                table.Columns.Add("Volume", typeof(double));

                foreach (var d in list)
                {
                    table.Rows.Add(
                        d.Symbol,
                        string.IsNullOrWhiteSpace(d.Interval) ? "5m" : d.Interval,
                        d.OpenTime,
                        d.Open,
                        d.High,
                        d.Low,
                        d.Close,
                        (double)d.Volume);
                }

                using (var bulk = new SqlBulkCopy(conn)
                {
                    DestinationTableName = "#CandleStage",
                    BulkCopyTimeout = 120,
                    BatchSize = 5000
                })
                {
                    bulk.ColumnMappings.Add("Symbol", "Symbol");
                    bulk.ColumnMappings.Add("IntervalText", "IntervalText");
                    bulk.ColumnMappings.Add("OpenTime", "OpenTime");
                    bulk.ColumnMappings.Add("Open", "Open");
                    bulk.ColumnMappings.Add("High", "High");
                    bulk.ColumnMappings.Add("Low", "Low");
                    bulk.ColumnMappings.Add("Close", "Close");
                    bulk.ColumnMappings.Add("Volume", "Volume");
                    await bulk.WriteToServerAsync(table);
                }

                // [v5.2.2] IntervalText 포함 → UQ_Candle 인덱스 활용 + 중복 무시
                const string mergeSql = @"
                    INSERT INTO CandleData (Symbol, IntervalText, OpenTime, [Open], [High], [Low], [Close], Volume)
                    SELECT s.Symbol, s.IntervalText, s.OpenTime, s.[Open], s.[High], s.[Low], s.[Close], s.Volume
                    FROM #CandleStage s
                    WHERE NOT EXISTS (
                        SELECT 1 FROM CandleData c
                        WHERE c.Symbol = s.Symbol AND c.IntervalText = s.IntervalText AND c.OpenTime = s.OpenTime
                    );
                    DROP TABLE #CandleStage;";
                int inserted;
                using (var cmd = new SqlCommand(mergeSql, conn) { CommandTimeout = 120 })
                {
                    inserted = await cmd.ExecuteNonQueryAsync();
                }

                UpdateOpenTimeCache(
                    list[0].Symbol,
                    string.IsNullOrWhiteSpace(list[0].Interval) ? "5m" : list[0].Interval,
                    list.Select(d => d.OpenTime));
            }
            catch (Exception ex) { Log($"❌ [DB] CandleData 벌크 저장 실패: {ex.Message}"); }
            } // end outer try
            finally { _bulkDbSemaphore.Release(); }
        }

        // CandleHistory 테이블 대량 저장
        public async Task SaveCandleHistoryBulkAsync(IEnumerable<CandleData> data)
        {
            var list = data as IList<CandleData> ?? data.ToList();
            if (list.Count == 0) return;
            await _bulkDbSemaphore.WaitAsync();
            try
            {
                using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                const string createTempSql = @"
                    CREATE TABLE #HistStage (
                        Symbol      NVARCHAR(32)  NOT NULL,
                        [Interval]  NVARCHAR(8)   NOT NULL,
                        OpenTime    DATETIME      NOT NULL,
                        OpenPrice   DECIMAL(18,8) NOT NULL,
                        HighPrice   DECIMAL(18,8) NOT NULL,
                        LowPrice    DECIMAL(18,8) NOT NULL,
                        ClosePrice  DECIMAL(18,8) NOT NULL,
                        Volume      FLOAT         NOT NULL,
                        IsPriceUp   BIT           NOT NULL
                    );";
                using (var cmd = new SqlCommand(createTempSql, conn)) { await cmd.ExecuteNonQueryAsync(); }

                var table = new DataTable();
                table.Columns.Add("Symbol",     typeof(string));
                table.Columns.Add("Interval",   typeof(string));
                table.Columns.Add("OpenTime",   typeof(DateTime));
                table.Columns.Add("OpenPrice",  typeof(decimal));
                table.Columns.Add("HighPrice",  typeof(decimal));
                table.Columns.Add("LowPrice",   typeof(decimal));
                table.Columns.Add("ClosePrice", typeof(decimal));
                table.Columns.Add("Volume",     typeof(double));
                table.Columns.Add("IsPriceUp",  typeof(bool));

                foreach (var d in list)
                    table.Rows.Add(d.Symbol, d.Interval ?? "5m", d.OpenTime,
                        d.Open, d.High, d.Low, d.Close, (double)d.Volume, d.Close >= d.Open);

                using (var bulk = new SqlBulkCopy(conn)
                    { DestinationTableName = "#HistStage", BulkCopyTimeout = 120, BatchSize = 5000 })
                {
                    bulk.ColumnMappings.Add("Symbol",     "Symbol");
                    bulk.ColumnMappings.Add("Interval",   "Interval");
                    bulk.ColumnMappings.Add("OpenTime",   "OpenTime");
                    bulk.ColumnMappings.Add("OpenPrice",  "OpenPrice");
                    bulk.ColumnMappings.Add("HighPrice",  "HighPrice");
                    bulk.ColumnMappings.Add("LowPrice",   "LowPrice");
                    bulk.ColumnMappings.Add("ClosePrice", "ClosePrice");
                    bulk.ColumnMappings.Add("Volume",     "Volume");
                    bulk.ColumnMappings.Add("IsPriceUp",  "IsPriceUp");
                    await bulk.WriteToServerAsync(table);
                }

                const string insertSql = @"
                    INSERT INTO CandleHistory (Symbol, [Interval], OpenTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, IsPriceUp)
                    SELECT s.Symbol, s.[Interval], s.OpenTime, s.OpenPrice, s.HighPrice, s.LowPrice, s.ClosePrice, s.Volume, s.IsPriceUp
                    FROM #HistStage s
                    WHERE NOT EXISTS (
                        SELECT 1 FROM CandleHistory c
                        WHERE c.Symbol = s.Symbol AND c.[Interval] = s.[Interval] AND c.OpenTime = s.OpenTime
                    );
                    DROP TABLE #HistStage;";
                using (var cmd = new SqlCommand(insertSql, conn) { CommandTimeout = 120 })
                    { await cmd.ExecuteNonQueryAsync(); }

                UpdateOpenTimeCache(list[0].Symbol, list[0].Interval ?? "5m", list.Select(d => d.OpenTime));
            }
            catch (Exception ex) { Log($"❌ [DB] CandleHistory 벌크 저장 실패: {ex.Message}"); }
            finally { _bulkDbSemaphore.Release(); }
        }

        // MarketData 테이블 대량 저장
        public async Task SaveMarketDataBulkAsync(IEnumerable<CandleData> data)
        {
            var list = data as IList<CandleData> ?? data.ToList();
            if (list.Count == 0) return;
            await _bulkDbSemaphore.WaitAsync();
            try
            {
                using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                const string createTempSql = @"
                    CREATE TABLE #MktStage (
                        Symbol         NVARCHAR(32)  NOT NULL,
                        [Interval]     NVARCHAR(8)   NOT NULL,
                        OpenTime       DATETIME      NOT NULL,
                        OpenPrice      DECIMAL(18,8) NOT NULL,
                        HighPrice      DECIMAL(18,8) NOT NULL,
                        LowPrice       DECIMAL(18,8) NOT NULL,
                        ClosePrice     DECIMAL(18,8) NOT NULL,
                        Volume         FLOAT         NOT NULL,
                        RSI            FLOAT         NULL,
                        BB_Upper       FLOAT         NULL,
                        BB_Lower       FLOAT         NULL,
                        ElliottWaveNum INT           NULL,
                        TrendScore     FLOAT         NULL
                    );";
                using (var cmd = new SqlCommand(createTempSql, conn)) { await cmd.ExecuteNonQueryAsync(); }

                var table = new DataTable();
                table.Columns.Add("Symbol",         typeof(string));
                table.Columns.Add("Interval",        typeof(string));
                table.Columns.Add("OpenTime",        typeof(DateTime));
                table.Columns.Add("OpenPrice",       typeof(decimal));
                table.Columns.Add("HighPrice",       typeof(decimal));
                table.Columns.Add("LowPrice",        typeof(decimal));
                table.Columns.Add("ClosePrice",      typeof(decimal));
                table.Columns.Add("Volume",          typeof(double));
                table.Columns.Add("RSI",             typeof(double));
                table.Columns.Add("BB_Upper",        typeof(double));
                table.Columns.Add("BB_Lower",        typeof(double));
                table.Columns.Add("ElliottWaveNum",  typeof(int));
                table.Columns.Add("TrendScore",      typeof(double));

                foreach (var d in list)
                    table.Rows.Add(d.Symbol, d.Interval ?? "5m", d.OpenTime,
                        d.Open, d.High, d.Low, d.Close, (double)d.Volume,
                        (double)d.RSI, (double)d.BollingerUpper, (double)d.BollingerLower,
                        (int)d.ElliottWaveState, (double)d.Trend_Strength);

                using (var bulk = new SqlBulkCopy(conn)
                    { DestinationTableName = "#MktStage", BulkCopyTimeout = 120, BatchSize = 5000 })
                {
                    bulk.ColumnMappings.Add("Symbol",        "Symbol");
                    bulk.ColumnMappings.Add("Interval",      "Interval");
                    bulk.ColumnMappings.Add("OpenTime",      "OpenTime");
                    bulk.ColumnMappings.Add("OpenPrice",     "OpenPrice");
                    bulk.ColumnMappings.Add("HighPrice",     "HighPrice");
                    bulk.ColumnMappings.Add("LowPrice",      "LowPrice");
                    bulk.ColumnMappings.Add("ClosePrice",    "ClosePrice");
                    bulk.ColumnMappings.Add("Volume",        "Volume");
                    bulk.ColumnMappings.Add("RSI",           "RSI");
                    bulk.ColumnMappings.Add("BB_Upper",      "BB_Upper");
                    bulk.ColumnMappings.Add("BB_Lower",      "BB_Lower");
                    bulk.ColumnMappings.Add("ElliottWaveNum","ElliottWaveNum");
                    bulk.ColumnMappings.Add("TrendScore",    "TrendScore");
                    await bulk.WriteToServerAsync(table);
                }

                const string insertSql = @"
                    INSERT INTO MarketData (Symbol, [Interval], OpenTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, RSI, BB_Upper, BB_Lower, ElliottWaveNum, TrendScore)
                    SELECT s.Symbol, s.[Interval], s.OpenTime, s.OpenPrice, s.HighPrice, s.LowPrice, s.ClosePrice, s.Volume,
                           s.RSI, s.BB_Upper, s.BB_Lower, s.ElliottWaveNum, s.TrendScore
                    FROM #MktStage s
                    WHERE NOT EXISTS (
                        SELECT 1 FROM MarketData c
                        WHERE c.Symbol = s.Symbol AND c.[Interval] = s.[Interval] AND c.OpenTime = s.OpenTime
                    );
                    DROP TABLE #MktStage;";
                using (var cmd = new SqlCommand(insertSql, conn) { CommandTimeout = 120 })
                    { await cmd.ExecuteNonQueryAsync(); }

                UpdateOpenTimeCache(list[0].Symbol, list[0].Interval ?? "5m", list.Select(d => d.OpenTime));
            }
            catch (Exception ex) { Log($"❌ [DB] MarketData 벌크 저장 실패: {ex.Message}"); }
            finally { _bulkDbSemaphore.Release(); }
        }


        // 2. 메소드 구현
        public async Task ExportDataForTraining()
        {
            // 데이터 폴더가 없으면 생성
            string? directory = Path.GetDirectoryName(_trainingDataPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var conn = new SqlConnection(_connStr))
            {
                await conn.OpenAsync();

                int userId = await ResolveCurrentUserIdAsync(conn);
                // [v5.3.4] UserId 필수 적용
                if (userId <= 0)
                {
                    Log("⚠️ ExportDataForTraining: UserId 확인 실패로 사용자별 학습 데이터 추출을 건너뜁니다.");
                    return;
                }

                string sql = @"
            SELECT
                CAST(EntryPrice AS REAL) AS ClosePrice,
                CAST(RSI AS REAL) AS RSI,
                CAST(BollingerUpper AS REAL) AS BollingerUpper,
                CAST(BollingerLower AS REAL) AS BollingerLower,
                CAST(ElliottWaveState AS REAL) AS ElliottWaveState,
                CAST(CASE WHEN PnL > 0 THEN 1 ELSE 0 END AS BIT) AS Label
            FROM TradeHistory
            WHERE UserId = @UserId";

                var data = await conn.QueryAsync<CandleData>(
                    sql,
                    new { UserId = userId },
                    commandTimeout: 120); // 대량 데이터 조회는 시간을 넉넉히 설정
                var list = data.ToList();

                if (list.Count == 0)
                {
                    Log("⚠️ 추출할 데이터가 없습니다.");
                    return;
                }

                // CSV 파일 저장
                using (var writer = new StreamWriter(_trainingDataPath))
                using (var csv = new CsvHelper.CsvWriter(writer, CultureInfo.InvariantCulture))
                {
                    await csv.WriteRecordsAsync(list);
                }

                Log($"✅ 학습 데이터 추출 완료: {_trainingDataPath} (총 {list.Count}건)");
            }
        }
        // 2. 학습용 데이터 조회
        public async Task<List<CandleData>> GetTrainingDataAsync(string symbol, int limit = 10000)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT TOP (@limit) * FROM CandleData WHERE Symbol = @symbol ORDER BY OpenTime DESC";
            var result = await conn.QueryAsync<CandleData>(sql, new { symbol, limit }, commandTimeout: QueryTimeout);
            return result.Reverse().ToList(); // 시간순 정렬
        }
        public async Task SaveTradeResultToSql(PositionInfo pos, decimal exitPrice, decimal pnl, decimal pnlPercent, string reason)
        {

            try
            {
                var dbManager = new DbManager(_connStr);
                var tradeLog = new TradeLog(
                    pos.Symbol,
                    pos.IsLong ? "SELL" : "BUY",
                    "DatabaseService_LegacyClose",
                    exitPrice,
                    pos.AiScore,
                    DateTime.Now,
                    pnl,
                    pnlPercent)
                {
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = exitPrice,
                    Quantity = Math.Abs(pos.Quantity),
                    EntryTime = pos.EntryTime,
                    ExitTime = DateTime.Now,
                    ExitReason = reason
                };

                await dbManager.CompleteTradeAsync(tradeLog);
                Log($"🗄️ [{pos.Symbol}] 거래 기록 DB 저장 완료.");
            }
            catch (Exception ex)
            {
                // DB 저장 실패가 실제 매매 로직을 멈추지 않도록 로그만 남깁니다.
                Log($"❌ DB 기록 중 오류 발생: {ex.Message}");
            }
        }

        // 3. 학습용 캔들 데이터 + 지표 대량 저장 (Bulk Insert)
        public async Task BulkInsertMarketDataAsync(IEnumerable<CandleData> data)
        {
            if (data == null || !data.Any()) return;
            await _bulkDbSemaphore.WaitAsync();

            // DataTable 생성 (SqlBulkCopy용)
            var dt = new DataTable();
            dt.Columns.Add("Symbol", typeof(string));
            dt.Columns.Add("OpenTime", typeof(DateTime));
            dt.Columns.Add("OpenPrice", typeof(decimal));
            dt.Columns.Add("HighPrice", typeof(decimal));
            dt.Columns.Add("LowPrice", typeof(decimal));
            dt.Columns.Add("ClosePrice", typeof(decimal));
            dt.Columns.Add("Volume", typeof(decimal));

            // 보조지표 컬럼
            dt.Columns.Add("RSI", typeof(float));
            dt.Columns.Add("MACD", typeof(float));
            dt.Columns.Add("MACD_Signal", typeof(float));
            dt.Columns.Add("MACD_Hist", typeof(float));
            dt.Columns.Add("ATR", typeof(float));
            dt.Columns.Add("BollingerUpper", typeof(float));
            dt.Columns.Add("BollingerLower", typeof(float));
            dt.Columns.Add("Fib_236", typeof(float));
            dt.Columns.Add("Fib_382", typeof(float));
            dt.Columns.Add("Fib_500", typeof(float));
            dt.Columns.Add("Fib_618", typeof(float));
            dt.Columns.Add("ElliottWaveState", typeof(float));
            dt.Columns.Add("Label", typeof(bool));

            foreach (var item in data)
            {
                dt.Rows.Add(
                    item.Symbol,
                    item.OpenTime,
                    (decimal)item.Open,
                    (decimal)item.High,
                    (decimal)item.Low,
                    (decimal)item.Close,
                    (decimal)item.Volume,
                    item.RSI,
                    item.MACD,
                    item.MACD_Signal,
                    item.MACD_Hist,
                    item.ATR,
                    item.BollingerUpper,
                    item.BollingerLower,
                    item.Fib_236,
                    item.Fib_382,
                    item.Fib_500,
                    item.Fib_618,
                    item.ElliottWaveState,
                    item.Label
                );
            }

            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();

            using var bulkCopy = new SqlBulkCopy(conn);
            bulkCopy.DestinationTableName = "MarketCandles";
            bulkCopy.BulkCopyTimeout = 60; // 대량 삽입 타임아웃 (60초)
            foreach (DataColumn col in dt.Columns)
            {
                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            }

            try
            {
                await bulkCopy.WriteToServerAsync(dt);
                UpdateOpenTimeCache(data.First().Symbol, "5m", data.Select(d => d.OpenTime));
            }
            catch (Exception ex) { Log($"❌ [DB] Bulk Insert 실패: {ex.Message}"); }
            finally { _bulkDbSemaphore.Release(); }
        }

        public async Task<bool> RegisterUserAsync(User user)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);


                // 첫 번째 사용자인지 확인 (시스템에 사용자가 없으면)
                string countSql = "SELECT COUNT(*) FROM Users";
                int userCount = await conn.ExecuteScalarAsync<int>(countSql);

                bool isFirstUser = userCount == 0;

                // 회원가입 시 미승인 상태(IsApproved = 0)로 저장
                // 단, 첫 번째 사용자는 자동 승인 및 관리자 권한 부여
                string sql = @"
                    INSERT INTO Users (Username, Email, PasswordHash, BinanceApiKey, BinanceApiSecret, TelegramBotToken, TelegramChatId, IsApproved, IsAdmin, ApprovedBy, ApprovedAt)
                    VALUES (@Username, @Email, @PasswordHash, @BinanceApiKey, @BinanceApiSecret, @TelegramBotToken, @TelegramChatId, @IsApproved, @IsAdmin, @ApprovedBy, @ApprovedAt)";

                var parameters = new
                {
                    user.Username,
                    user.Email,
                    user.PasswordHash,
                    user.BinanceApiKey,
                    user.BinanceApiSecret,
                    user.TelegramBotToken,
                    user.TelegramChatId,
                    IsApproved = isFirstUser ? 1 : 0, // 첫 사용자는 자동 승인
                    IsAdmin = isFirstUser ? 1 : 0,     // 첫 사용자는 관리자 권한
                    ApprovedBy = isFirstUser ? "SYSTEM" : (string?)null,
                    ApprovedAt = isFirstUser ? (DateTime?)DateTime.Now : null
                };

                await conn.ExecuteAsync(sql, parameters, commandTimeout: QueryTimeout);

                if (isFirstUser)
                {
                    Log($"✅ 첫 번째 사용자 [{user.Username}] 등록: 자동 승인 + 관리자 권한 부여");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 회원가입 실패: {ex.Message}");
                return false;
            }
        }

        public async Task<User?> LoginUserAsync(string username, string passwordHash)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT * FROM Users WHERE Username = @Username AND PasswordHash = @PasswordHash";
            var user = await conn.QueryFirstOrDefaultAsync<User>(sql, new { Username = username, PasswordHash = passwordHash }, commandTimeout: QueryTimeout);

            if (user != null)
            {
                // 승인되지 않은 사용자는 null 반환 (로그인 거부)
                if (!user.IsApproved)
                {
                    Log($"⚠️ [{user.Username}] 승인 대기 중인 계정입니다.");
                    return null;
                }

                string updateSql = "UPDATE Users SET LastLogin = GETDATE() WHERE Id = @Id";
                await conn.ExecuteAsync(updateSql, new { Id = user.Id }, commandTimeout: QueryTimeout);
            }
            return user;
        }

        public async Task<bool> IsEmailExistsAsync(string email)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT COUNT(1) FROM Users WHERE Email = @Email";
            int count = await conn.ExecuteScalarAsync<int>(sql, new { Email = email }, commandTimeout: QueryTimeout);
            return count > 0;
        }

        public async Task<bool> IsUsernameExistsAsync(string username)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT COUNT(1) FROM Users WHERE Username = @Username";
            int count = await conn.ExecuteScalarAsync<int>(sql, new { Username = username }, commandTimeout: QueryTimeout);
            return count > 0;
        }

        public async Task<User?> GetUserByUsernameAsync(string username)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT * FROM Users WHERE Username = @Username";
            return await conn.QueryFirstOrDefaultAsync<User>(sql, new { Username = username }, commandTimeout: QueryTimeout);
        }

        public async Task UpdatePasswordByEmailAsync(string email, string passwordHash)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "UPDATE Users SET PasswordHash = @PasswordHash WHERE Email = @Email";
            await conn.ExecuteAsync(sql, new { Email = email, PasswordHash = passwordHash }, commandTimeout: QueryTimeout);
        }

        public async Task<bool> UpdateUserAsync(User user)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);

                // 테스트넷 키 컬럼이 없으면 자동 추가
                string alterSql = @"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'TestnetApiKey')
                    BEGIN
                        ALTER TABLE Users ADD TestnetApiKey NVARCHAR(MAX) NULL, TestnetApiSecret NVARCHAR(MAX) NULL;
                    END";
                await conn.ExecuteAsync(alterSql);

                string sql = @"
                    UPDATE Users SET
                        BinanceApiKey = @BinanceApiKey, BinanceApiSecret = @BinanceApiSecret,
                        TestnetApiKey = @TestnetApiKey, TestnetApiSecret = @TestnetApiSecret,
                        TelegramBotToken = @TelegramBotToken, TelegramChatId = @TelegramChatId
                    WHERE Username = @Username";

                await conn.ExecuteAsync(sql, user, commandTimeout: QueryTimeout);
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 정보 수정 실패: {ex.Message}");
                return false;
            }
        }

        public async Task<List<TradeHistoryModel>> GetTradeHistoryAsync(int limit = 100)
        {
            int userId = TradingBot.AppConfig.CurrentUser?.Id ?? 0;
            if (userId <= 0)
                return new List<TradeHistoryModel>();

            var dbManager = new DbManager(_connStr);
            var result = await dbManager.GetTradeHistoryAsync(userId, DateTime.MinValue, DateTime.MaxValue, limit);
            return result.Select(ToTradeHistoryModel).ToList();
        }

        public async Task<List<TradeHistoryModel>> GetTradeHistoryAsync(DateTime start, DateTime end, int limit = 1000)
        {
            int userId = TradingBot.AppConfig.CurrentUser?.Id ?? 0;
            if (userId <= 0)
                return new List<TradeHistoryModel>();

            var dbManager = new DbManager(_connStr);
            var result = await dbManager.GetTradeHistoryAsync(userId, start, end, limit);
            return result.Select(ToTradeHistoryModel).ToList();
        }

        // [추가] 매매 이력 CSV 내보내기
        public async Task ExportTradeHistoryToCsvAsync(string filePath)
        {
            int userId = TradingBot.AppConfig.CurrentUser?.Id ?? 0;
            if (userId <= 0)
                return;

            var dbManager = new DbManager(_connStr);
            await dbManager.ExportTradeHistoryToCsvAsync(filePath, userId, DateTime.MinValue, DateTime.MaxValue, 10000);
        }

        // ========== 관리자 기능 (User Management) ==========

        /// <summary>
        /// 모든 사용자 목록 조회 (관리자용)
        /// </summary>
        public async Task<List<User>> GetAllUsersAsync()
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT * FROM Users ORDER BY IsApproved ASC, Id DESC";
            var result = await conn.QueryAsync<User>(sql, commandTimeout: QueryTimeout);
            return result.ToList();
        }

        /// <summary>
        /// 사용자 승인 처리
        /// </summary>
        public async Task<bool> ApproveUserAsync(int userId, string adminUsername)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);
                string sql = @"
                    UPDATE Users 
                    SET IsApproved = 1, ApprovedBy = @ApprovedBy, ApprovedAt = GETDATE() 
                    WHERE Id = @UserId";

                await conn.ExecuteAsync(sql, new { UserId = userId, ApprovedBy = adminUsername }, commandTimeout: QueryTimeout);
                Log($"✅ 사용자 ID {userId} 승인 완료 (승인자: {adminUsername})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 사용자 승인 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 사용자 삭제 (거부)
        /// </summary>
        public async Task<bool> DeleteUserAsync(int userId)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);
                string sql = "DELETE FROM Users WHERE Id = @UserId";
                await conn.ExecuteAsync(sql, new { UserId = userId }, commandTimeout: QueryTimeout);
                Log($"✅ 사용자 ID {userId} 삭제 완료");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 사용자 삭제 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 승인 대기 중인 사용자 수 조회
        /// </summary>
        public async Task<int> GetPendingApprovalCountAsync()
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT COUNT(1) FROM Users WHERE IsApproved = 0";
            return await conn.ExecuteScalarAsync<int>(sql, commandTimeout: QueryTimeout);
        }

        /// <summary>
        /// 첫 번째 사용자인지 확인
        /// </summary>
        public async Task<bool> IsFirstUserAsync()
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT COUNT(*) FROM Users";
            int userCount = await conn.ExecuteScalarAsync<int>(sql, commandTimeout: QueryTimeout);
            return userCount == 0;
        }

        /// <summary>
        /// 라이브 로그 배치 저장 — 단일 커넥션으로 다수 항목 처리 (커넥션 풀 절약)
        /// </summary>
        public async Task SaveLiveLogsBatchAsync(IEnumerable<(string Category, string Message, string? Symbol)> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            try
            {
                using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                const string insertSql = @"
                INSERT INTO LiveLogs (Timestamp, Category, Symbol, Message)
                VALUES (@Timestamp, @Category, @Symbol, @Message)";

                foreach (var x in list)
                {
                    try
                    {
                        await conn.ExecuteAsync(insertSql, new
                        {
                            Timestamp = DateTime.Now,
                            x.Category,
                            x.Symbol,
                            Message = x.Message?.Length > 4000 ? x.Message[..4000] : x.Message
                        }, commandTimeout: 10);
                    }
                    catch { /* 개별 행 실패 시 스킵 */ }
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [DB] 로그 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 라이브 로그 저장 (단건 — 하위 호환)
        /// </summary>
        public async Task SaveLiveLogAsync(string category, string message, string? symbol = null)
        {
            await SaveLiveLogsBatchAsync(new[] { (category, message, symbol) });
        }

        /// <summary>
        /// Footer 로그 배치 저장 — 단일 커넥션으로 다수 항목 처리 (커넥션 풀 절약)
        /// </summary>
        public async Task SaveFooterLogsBatchAsync(IEnumerable<(DateTime Timestamp, string Message)> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            try
            {
                using var conn = new SqlConnection(_connStr);
                await conn.OpenAsync();

                // [v5.2.2] 컬럼 크기 자동 확장 (1000 → 4000)
                if (!_footerLogColumnExpanded)
                {
                    try
                    {
                        await conn.ExecuteAsync(@"
                            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.FooterLogs') AND name = 'Message' AND max_length = 2000)
                            ALTER TABLE dbo.FooterLogs ALTER COLUMN [Message] NVARCHAR(4000) NOT NULL;",
                            commandTimeout: 10);
                        _footerLogColumnExpanded = true;
                    }
                    catch { _footerLogColumnExpanded = true; }
                }

                const string insertSql = @"
                INSERT INTO FooterLogs (Timestamp, Message)
                VALUES (@Timestamp, @Message)";

                // 개별 INSERT — Dapper IEnumerable batch의 implicit transaction 롤백 방지
                foreach (var item in list)
                {
                    try
                    {
                        await conn.ExecuteAsync(insertSql, new
                        {
                            item.Timestamp,
                            Message = item.Message?.Length > 4000 ? item.Message[..4000] : item.Message
                        }, commandTimeout: 10);
                    }
                    catch { /* 개별 행 실패 시 스킵 */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] Footer 로그 저장 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// Footer 로그 저장 (단건 — 하위 호환)
        /// </summary>
        public async Task SaveFooterLogAsync(DateTime timestamp, string message)
        {
            await SaveFooterLogsBatchAsync(new[] { (timestamp, message) });
        }

        /// <summary>
        /// 라이브 로그 조회
        /// </summary>
        public async Task<List<(DateTime Timestamp, string Category, string? Symbol, string Message)>> GetLiveLogsAsync(
            string? category = null,
            int limit = 100)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);

                string sql = category != null
                    ? "SELECT TOP(@Limit) Timestamp, Category, Symbol, Message FROM LiveLogs WHERE Category = @Category ORDER BY Timestamp DESC"
                    : "SELECT TOP(@Limit) Timestamp, Category, Symbol, Message FROM LiveLogs ORDER BY Timestamp DESC";

                var results = await conn.QueryAsync<(DateTime, string, string?, string)>(sql, new
                {
                    Limit = limit,
                    Category = category
                }, commandTimeout: QueryTimeout);

                return results.ToList();
            }
            catch (Exception ex)
            {
                Log($"❌ [DB] 로그 조회 실패: {ex.Message}");
                return new List<(DateTime, string, string?, string)>();
            }
        }
    }
}
