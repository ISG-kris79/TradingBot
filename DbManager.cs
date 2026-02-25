using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;
using TradingBot.Models;
using TradingBot; // [수정] MainWindow 접근을 위해 추가

namespace TradingBot.Services
{
    public class DbManager
    {
        private readonly string _connectionString;

        public DbManager(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task SaveTradeLogAsync(TradeLog log)
        {
            try
            {
                using (IDbConnection db = new SqlConnection(_connectionString))
                {

                    // [추가] 기존 테이블에 컬럼이 없을 경우 추가 (마이그레이션)
                    string alterTableSql = @"
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('TradeLogs') AND name = 'PnL')
                        ALTER TABLE TradeLogs ADD PnL DECIMAL(18, 8) DEFAULT 0, PnLPercent DECIMAL(18, 2) DEFAULT 0;";
                    await db.ExecuteAsync(alterTableSql);

                    string insertSql = @"
                        INSERT INTO TradeLogs (Symbol, Side, Strategy, Price, AiScore, Time, PnL, PnLPercent)
                        VALUES (@Symbol, @Side, @Strategy, @Price, @AiScore, @Time, @PnL, @PnLPercent)";

                    await db.ExecuteAsync(insertSql, log);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"⚠️ DB 저장 실패: {ex.Message}");
            }
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

        // [추가] 학습용 데이터 추출 (예시)
        public async Task TrainNeuralNetworkModel()
        {
            // 실제 구현 시 ML.NET 파이프라인 호출
            await Task.CompletedTask;
        }
    }
}