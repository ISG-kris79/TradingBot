using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;
using TradingBot.Models;
using TradingBot; // [수정] MainWindow 접근을 위해 추가
using TradingBot.Shared.Models;

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
                if (log == null)
                {
                    MainWindow.Instance?.AddLog($"⚠️ SaveTradeLogAsync: log 객체가 null입니다");
                    return;
                }

                using (IDbConnection db = new SqlConnection(_connectionString))
                {
                    db.Open(); // [중요] DB 연결 명시화

                    string insertSql = @"
                        INSERT INTO TradeLogs (Symbol, Side, Strategy, Price, AiScore, Time, PnL, PnLPercent)
                        VALUES (@Symbol, @Side, @Strategy, @Price, @AiScore, @Time, @PnL, @PnLPercent)";

                    MainWindow.Instance?.AddLog($"📝 [DB] TradeLogs 저장 시도: {log.Symbol} {log.Side} @ {log.Price:F4} ROE={log.PnLPercent:F2}%");

                    int result = await db.ExecuteAsync(insertSql, log);

                    if (result > 0)
                    {
                        MainWindow.Instance?.AddLog($"✅ [DB] TradeLogs 저장 성공: {log.Symbol} PnL={log.PnL:F2} USDT");
                    }
                    else
                    {
                        MainWindow.Instance?.AddLog($"⚠️ [DB] TradeLogs 저장 미실행: ExecuteAsync result={result}");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [DB 저장 실패] {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    MainWindow.Instance?.AddLog($"  내부 오류: {ex.InnerException.Message}");
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
