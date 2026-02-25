﻿﻿using Binance.Net.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.IO;
using TradingBot.Models;

namespace TradingBot.Services
{
    public class DatabaseService
    {
        // DatabaseService.cs 내부
        private readonly string _connStr = TradingBot.AppConfig.ConnectionString;
        private readonly string _trainingDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "TradingModel.csv");
        private readonly Action<string> _logger; // 로그 대행자       
        private const int QueryTimeout = 30; // 기본 쿼리 타임아웃 (30초)

        public DatabaseService(Action<string>? logger = null)
        {
            _logger = logger;
        }

        private void Log(string msg)
        {
            _logger?.Invoke(msg); // MainWindow의 Log 메소드를 대신 실행
        }
        public async Task SaveLogAsync(TradeLog log)
        {
            try
            {
                using var db = new SqlConnection(_connStr);
                string sql = @"INSERT INTO TradeLogs (Symbol, Side, StrategyType, EntryPrice, AiScore) 
                               VALUES (@Symbol, @Side, @Strategy, @Price, @AiScore)";
                await db.ExecuteAsync(sql, log, commandTimeout: QueryTimeout);
            }
            catch (Exception ex) { Console.WriteLine($"[DB Error] {ex.Message}"); }
        }
        // 1. 데이터 대량 저장 (Bulk Insert)
        public async Task SaveCandlesAsync(string symbol, IEnumerable<IBinanceKline> klines)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = @"
            IF NOT EXISTS (SELECT 1 FROM CandleData WHERE Symbol = @Symbol AND OpenTime = @OpenTime)
            INSERT INTO CandleData (Symbol, OpenTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
            VALUES (@Symbol, @OpenTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume)";

            await conn.ExecuteAsync(sql, klines.Select(k => new
            {
                Symbol = symbol,
                OpenTime = k.OpenTime,
                OpenPrice = k.OpenPrice,
                HighPrice = k.HighPrice,
                LowPrice = k.LowPrice,
                ClosePrice = k.ClosePrice,
                Volume = k.Volume
            }), commandTimeout: QueryTimeout);
        }


        // 2. 메소드 구현
        public async Task ExportDataForTraining()
        {
            // 데이터 폴더가 없으면 생성
            string directory = Path.GetDirectoryName(_trainingDataPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            using (var conn = new SqlConnection(_connStr))
            {
                string sql = @"
            SELECT 
                CAST(EntryPrice AS REAL) AS ClosePrice, -- ML.NET은 float(REAL) 선호
                CAST(RSI AS REAL) AS RSI, 
                CAST(BollingerUpper AS REAL) AS BollingerUpper, 
                CAST(BollingerLower AS REAL) AS BollingerLower, 
                CAST(ElliottWaveState AS REAL) AS ElliottWaveState,
                CAST(CASE WHEN PnL > 0 THEN 1 ELSE 0 END AS BIT) AS Label -- 1:상승(Win), 0:하락(Loss)
            FROM TradeHistory";

                var data = await conn.QueryAsync<CandleData>(sql, commandTimeout: 120); // 대량 데이터 조회는 시간을 넉넉히 설정
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
                using (var conn = new SqlConnection(_connStr))
                {
                    string sql = @"
                INSERT INTO TradeHistory (
                    Symbol, Side, EntryPrice, ExitPrice, Quantity, 
                    PnL, PnLPercent, ExitReason, EntryTime, ExitTime
                ) VALUES (
                    @Symbol, @Side, @EntryPrice, @ExitPrice, @Quantity, 
                    @PnL, @PnLPercent, @ExitReason, @EntryTime, GETDATE()
                )";

                    var parameters = new
                    {
                        Symbol = pos.Symbol,
                        Side = pos.Side.ToString().ToUpper(), // BUY 또는 SELL
                        EntryPrice = pos.EntryPrice,
                        ExitPrice = exitPrice,
                        Quantity = pos.Quantity,
                        PnL = pnl,
                        PnLPercent = pnlPercent,
                        ExitReason = reason,
                        EntryTime = pos.EntryTime
                    };

                    // Dapper를 사용하여 비동기 실행
                    await conn.ExecuteAsync(sql, parameters, commandTimeout: QueryTimeout);

                    Log($"🗄️ [{pos.Symbol}] 거래 기록 DB 저장 완료.");
                }
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

            try { await bulkCopy.WriteToServerAsync(dt); Log($"✅ [DB] {data.Count()}건 지표 데이터 저장 완료 (MarketCandles)"); }
            catch (Exception ex) { Log($"❌ [DB] Bulk Insert 실패: {ex.Message}"); }
        }

        public async Task<bool> RegisterUserAsync(User user)
        {
            try
            {
                using var conn = new SqlConnection(_connStr);
                
                string sql = @"
                    INSERT INTO Users (Username, Email, PasswordHash, BinanceApiKey, BinanceApiSecret, TelegramBotToken, TelegramChatId, BybitApiKey, BybitApiSecret, BitgetApiKey, BitgetApiSecret, BitgetPassphrase)
                    VALUES (@Username, @Email, @PasswordHash, @BinanceApiKey, @BinanceApiSecret, @TelegramBotToken, @TelegramChatId, @BybitApiKey, @BybitApiSecret, @BitgetApiKey, @BitgetApiSecret, @BitgetPassphrase)";

                await conn.ExecuteAsync(sql, user, commandTimeout: QueryTimeout);
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 회원가입 실패: {ex.Message}");
                return false;
            }
        }

        public async Task<User> LoginUserAsync(string username, string passwordHash)
        {
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT * FROM Users WHERE Username = @Username AND PasswordHash = @PasswordHash";
            var user = await conn.QueryFirstOrDefaultAsync<User>(sql, new { Username = username, PasswordHash = passwordHash }, commandTimeout: QueryTimeout);
            
            if (user != null)
            {
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

        public async Task<User> GetUserByUsernameAsync(string username)
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

                string alterSql = @"
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'BybitApiKey')
                    BEGIN
                        ALTER TABLE Users ADD BybitApiKey NVARCHAR(MAX) NULL, BybitApiSecret NVARCHAR(MAX) NULL, BitgetApiKey NVARCHAR(MAX) NULL, BitgetApiSecret NVARCHAR(MAX) NULL, BitgetPassphrase NVARCHAR(MAX) NULL;
                    END";
                await conn.ExecuteAsync(alterSql);

                string sql = @"
                    UPDATE Users SET 
                        BinanceApiKey = @BinanceApiKey, BinanceApiSecret = @BinanceApiSecret,
                        BybitApiKey = @BybitApiKey, BybitApiSecret = @BybitApiSecret,
                        BitgetApiKey = @BitgetApiKey, BitgetApiSecret = @BitgetApiSecret, BitgetPassphrase = @BitgetPassphrase,
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
            using var conn = new SqlConnection(_connStr);
            string sql = "SELECT TOP (@Limit) * FROM TradeHistory ORDER BY ExitTime DESC";
            var result = await conn.QueryAsync<TradeHistoryModel>(sql, new { Limit = limit }, commandTimeout: QueryTimeout);
            return result.ToList();
        }
    }
}