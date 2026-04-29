using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Dapper;
using Binance.Net.Interfaces.Clients;

namespace TradingBot.Services
{
    /// <summary>
    /// [v5.22.27] 바이낸스 포지션 히스토리 동기화 서비스
    ///
    /// 데이터 흐름:
    ///   Binance UsdFuturesApi.Trading.GetUserTradesAsync (체결 단위)
    ///   → (Symbol, PositionSide) 별 시간순 정렬
    ///   → qty 누적이 0 되는 구간을 1 포지션으로 그룹핑
    ///   → BinancePositionHistory 테이블 INSERT
    ///
    /// 권위 데이터: 바이낸스 직접 계산한 RealizedPnl/Commission → 봇 추정 PnL 오차 0
    /// 외부 진입 포착: 사용자 수동 진입도 자동 통계 반영
    /// </summary>
    public class BinancePositionHistorySync
    {
        private readonly IBinanceRestClient _client;
        private readonly string _connectionString;
        private readonly int _userId;

        public event Action<string>? OnLog;

        private CancellationTokenSource? _cts;
        private Task? _runner;

        public BinancePositionHistorySync(IBinanceRestClient client, string connectionString, int userId)
        {
            _client = client;
            _connectionString = connectionString;
            _userId = userId;
        }

        public async Task EnsureSchemaAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                await db.ExecuteAsync(@"
IF OBJECT_ID('dbo.BinancePositionHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BinancePositionHistory (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        UserId          INT NOT NULL,
        Symbol          NVARCHAR(40) NOT NULL,
        PositionSide    NVARCHAR(10) NOT NULL,  -- LONG / SHORT
        OpenTime        DATETIME2 NOT NULL,
        CloseTime       DATETIME2 NOT NULL,
        AvgEntryPrice   DECIMAL(28,10) NOT NULL,
        AvgExitPrice    DECIMAL(28,10) NOT NULL,
        TotalQuantity   DECIMAL(28,10) NOT NULL,
        RealizedPnl     DECIMAL(28,10) NOT NULL,
        Commission      DECIMAL(28,10) NOT NULL,
        NetPnl          DECIMAL(28,10) NOT NULL,
        RoePct          DECIMAL(18,6) NULL,
        FillCount       INT NOT NULL,
        Leverage        INT NULL,
        Category        NVARCHAR(20) NULL,  -- MAJOR/SQUEEZE/BB_WALK/GENERIC/EXTERNAL
        CreatedAt       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UX_BPH_User_Sym_Side_Open UNIQUE (UserId, Symbol, PositionSide, OpenTime)
    );
    CREATE INDEX IX_BPH_User_Close ON dbo.BinancePositionHistory (UserId, CloseTime DESC);
    CREATE INDEX IX_BPH_User_Cat_Close ON dbo.BinancePositionHistory (UserId, Category, CloseTime DESC);
END", commandTimeout: 60);
                OnLog?.Invoke("✅ [BPH] BinancePositionHistory 테이블 준비 완료");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [BPH] 스키마 생성 실패: {ex.Message}");
            }
        }

        public Task StartAsync(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _runner = Task.Run(() => RunLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            // 봇 부팅 후 5초 대기 (다른 초기화 우선)
            await Task.Delay(TimeSpan.FromSeconds(5), token);

            // 첫 실행: 90일 백필 (초기 1회만)
            try
            {
                var lastClose = await GetLastCloseTimeAsync();
                if (lastClose == null)
                {
                    OnLog?.Invoke("📦 [BPH] 첫 동기화 — 최근 90일 백필 시작 (5분 ~ 10분 소요)");
                    await SyncSinceAsync(DateTime.UtcNow.AddDays(-90), token);
                }
                else
                {
                    OnLog?.Invoke($"📦 [BPH] 증분 동기화 — 마지막 동기화 {lastClose.Value:yyyy-MM-dd HH:mm} 이후");
                    await SyncSinceAsync(lastClose.Value.AddSeconds(-60), token);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [BPH] 첫 동기화 오류: {ex.Message}");
            }

            // 5분 주기 증분 동기화
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(5), token); }
                catch (OperationCanceledException) { return; }

                try
                {
                    var lastClose = await GetLastCloseTimeAsync();
                    var since = lastClose?.AddSeconds(-60) ?? DateTime.UtcNow.AddDays(-1);
                    await SyncSinceAsync(since, token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [BPH] 증분 동기화 오류: {ex.Message}");
                }
            }
        }

        private async Task<DateTime?> GetLastCloseTimeAsync()
        {
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                var v = await db.QueryFirstOrDefaultAsync<DateTime?>(
                    "SELECT MAX(CloseTime) FROM dbo.BinancePositionHistory WHERE UserId = @UserId",
                    new { UserId = _userId });
                return v;
            }
            catch { return null; }
        }

        private async Task SyncSinceAsync(DateTime sinceUtc, CancellationToken token)
        {
            // 1) 활성 추적 심볼 + 최근 거래 심볼 = 폴링 대상.
            //    바이낸스는 GetAllUserTradesAsync 가 없어서 심볼별로 조회 필요.
            //    USDT 페어 전체 200+ 는 너무 많음 → 기존 TradeHistory + PositionState 의 심볼 + 메이저 5개로 한정.
            var targetSymbols = await GetTargetSymbolsAsync();

            int totalNewPositions = 0;
            foreach (var symbol in targetSymbols)
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var positions = await FetchAndGroupAsync(symbol, sinceUtc, token);
                    if (positions.Count > 0)
                    {
                        int saved = await SavePositionsAsync(positions);
                        totalNewPositions += saved;
                    }
                    // throttle: 100ms × 50 심볼 = 5초 (Binance weight 한도 내)
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"⚠️ [BPH] {symbol} 동기화 오류: {ex.Message}");
                }
            }

            if (totalNewPositions > 0)
                OnLog?.Invoke($"✅ [BPH] 신규 포지션 {totalNewPositions}건 저장 완료 ({targetSymbols.Count} 심볼 폴링)");
        }

        private async Task<HashSet<string>> GetTargetSymbolsAsync()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT"
            };
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();
                var rows = await db.QueryAsync<string>(@"
SELECT DISTINCT Symbol FROM dbo.TradeHistory
WHERE EntryTime >= DATEADD(DAY, -120, SYSUTCDATETIME())
UNION
SELECT DISTINCT Symbol FROM dbo.PositionState WHERE UserId = @UserId
UNION
SELECT DISTINCT Symbol FROM dbo.BinancePositionHistory WHERE UserId = @UserId AND CloseTime >= DATEADD(DAY, -30, SYSUTCDATETIME())
", new { UserId = _userId });
                foreach (var s in rows) if (!string.IsNullOrEmpty(s)) set.Add(s);
            }
            catch { }
            return set;
        }

        private record GroupedPosition(
            string Symbol,
            string PositionSide,
            DateTime OpenTime,
            DateTime CloseTime,
            decimal AvgEntryPrice,
            decimal AvgExitPrice,
            decimal TotalQuantity,
            decimal RealizedPnl,
            decimal Commission,
            int FillCount);

        private async Task<List<GroupedPosition>> FetchAndGroupAsync(string symbol, DateTime sinceUtc, CancellationToken token)
        {
            var result = new List<GroupedPosition>();
            try
            {
                // GetUserTradesAsync — 페이지네이션 (limit=1000)
                var trades = new List<Binance.Net.Objects.Models.Futures.BinanceFuturesUsdtTrade>();
                DateTime cursor = sinceUtc;
                while (true)
                {
                    var pr = await _client.UsdFuturesApi.Trading.GetUserTradesAsync(symbol, startTime: cursor, limit: 1000, ct: token);
                    if (!pr.Success || pr.Data == null) break;
                    var batch = pr.Data.OrderBy(t => t.Timestamp).ToList();
                    if (batch.Count == 0) break;
                    trades.AddRange(batch);
                    if (batch.Count < 1000) break;
                    cursor = batch.Last().Timestamp.AddMilliseconds(1);
                    if (cursor > DateTime.UtcNow) break;
                }

                if (trades.Count == 0) return result;

                // (PositionSide) 별 그룹핑 — qty 누적 0 도달 시 1 포지션 종료
                foreach (var sideGroup in trades.GroupBy(t => t.PositionSide.ToString().ToUpperInvariant()))
                {
                    string posSide = sideGroup.Key == "LONG" ? "LONG" : (sideGroup.Key == "SHORT" ? "SHORT" : "BOTH");
                    var ordered = sideGroup.OrderBy(t => t.Timestamp).ToList();

                    decimal openQty = 0m;
                    decimal entryQty = 0m, entryNotional = 0m;
                    decimal exitQty = 0m, exitNotional = 0m;
                    decimal realized = 0m, commission = 0m;
                    DateTime openTime = DateTime.MinValue;
                    int fillCount = 0;

                    foreach (var t in ordered)
                    {
                        decimal q = t.Quantity;
                        decimal p = t.Price;
                        decimal pnl = t.RealizedPnl;
                        decimal fee = t.Fee;

                        // BUY = LONG 진입 / SHORT 청산. SELL = SHORT 진입 / LONG 청산.
                        // PositionSide=LONG: BUY 진입, SELL 청산
                        // PositionSide=SHORT: SELL 진입, BUY 청산
                        bool isEntry;
                        string sideStr = t.Side.ToString().ToUpperInvariant();
                        if (posSide == "LONG")
                            isEntry = sideStr == "BUY";
                        else if (posSide == "SHORT")
                            isEntry = sideStr == "SELL";
                        else
                            // ONE-WAY mode (BOTH): RealizedPnl=0 이면 진입, !=0 이면 청산
                            isEntry = pnl == 0;

                        if (isEntry)
                        {
                            if (openQty == 0)
                                openTime = t.Timestamp;
                            openQty += q;
                            entryQty += q;
                            entryNotional += q * p;
                        }
                        else
                        {
                            openQty -= q;
                            exitQty += q;
                            exitNotional += q * p;
                            realized += pnl;
                        }
                        commission += fee;
                        fillCount++;

                        // qty=0 도달 → 1 포지션 종료
                        if (openQty == 0 && entryQty > 0)
                        {
                            decimal avgEntry = entryQty > 0 ? entryNotional / entryQty : 0;
                            decimal avgExit = exitQty > 0 ? exitNotional / exitQty : 0;
                            result.Add(new GroupedPosition(
                                Symbol: symbol,
                                PositionSide: posSide == "BOTH" ? (avgExit >= avgEntry ? "LONG" : "SHORT") : posSide,
                                OpenTime: openTime,
                                CloseTime: t.Timestamp,
                                AvgEntryPrice: avgEntry,
                                AvgExitPrice: avgExit,
                                TotalQuantity: entryQty,
                                RealizedPnl: realized,
                                Commission: commission,
                                FillCount: fillCount));

                            // 상태 리셋
                            entryQty = 0; entryNotional = 0;
                            exitQty = 0; exitNotional = 0;
                            realized = 0; commission = 0;
                            fillCount = 0;
                            openTime = DateTime.MinValue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [BPH] {symbol} fetch 오류: {ex.Message}");
            }
            return result;
        }

        private async Task<int> SavePositionsAsync(List<GroupedPosition> positions)
        {
            if (positions == null || positions.Count == 0) return 0;
            int saved = 0;
            try
            {
                await using var db = new SqlConnection(_connectionString);
                await db.OpenAsync();

                // 카테고리 채움 — TradeHistory 의 Category 와 OpenTime 매치 (없으면 EXTERNAL)
                var trMap = await db.QueryAsync<(string Symbol, DateTime EntryTime, string Category)>(@"
SELECT Symbol, EntryTime, Category FROM dbo.TradeHistory
WHERE EntryTime >= DATEADD(DAY, -120, SYSUTCDATETIME()) AND Category IS NOT NULL
");
                var trList = trMap.ToList();

                foreach (var p in positions)
                {
                    string category = MatchCategory(trList, p.Symbol, p.OpenTime);
                    decimal netPnl = p.RealizedPnl - p.Commission;
                    decimal margin = (p.AvgEntryPrice * p.TotalQuantity) / 15m; // assume 15x; fixed for display
                    decimal? roe = margin > 0 ? netPnl / margin * 100 : (decimal?)null;

                    int rows = await db.ExecuteAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.BinancePositionHistory
               WHERE UserId=@UserId AND Symbol=@Symbol AND PositionSide=@PositionSide AND OpenTime=@OpenTime)
INSERT INTO dbo.BinancePositionHistory
  (UserId, Symbol, PositionSide, OpenTime, CloseTime, AvgEntryPrice, AvgExitPrice, TotalQuantity,
   RealizedPnl, Commission, NetPnl, RoePct, FillCount, Leverage, Category)
VALUES
  (@UserId, @Symbol, @PositionSide, @OpenTime, @CloseTime, @AvgEntryPrice, @AvgExitPrice, @TotalQuantity,
   @RealizedPnl, @Commission, @NetPnl, @RoePct, @FillCount, NULL, @Category);
",
                        new
                        {
                            UserId = _userId,
                            p.Symbol,
                            p.PositionSide,
                            p.OpenTime,
                            p.CloseTime,
                            p.AvgEntryPrice,
                            p.AvgExitPrice,
                            p.TotalQuantity,
                            p.RealizedPnl,
                            p.Commission,
                            NetPnl = netPnl,
                            RoePct = roe,
                            p.FillCount,
                            Category = category
                        }, commandTimeout: 30);

                    if (rows > 0) saved++;
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [BPH] 저장 오류: {ex.Message}");
            }
            return saved;
        }

        private static string MatchCategory(List<(string Symbol, DateTime EntryTime, string Category)> trList, string symbol, DateTime openTime)
        {
            // 진입 시각 ±5분 이내 매칭
            var win = TimeSpan.FromMinutes(5);
            var match = trList.FirstOrDefault(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                                                  && Math.Abs((t.EntryTime - openTime).TotalSeconds) <= win.TotalSeconds);
            if (!string.IsNullOrEmpty(match.Category)) return match.Category;

            // 메이저 심볼 → MAJOR
            if (symbol == "BTCUSDT" || symbol == "ETHUSDT" || symbol == "SOLUSDT" || symbol == "XRPUSDT" || symbol == "BNBUSDT")
                return "MAJOR";

            return "EXTERNAL";
        }
    }

    /// <summary>[v5.22.27] BinancePositionHistory 행 (UI 바인딩용 DTO)</summary>
    public class PositionHistoryRow
    {
        public long Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string PositionSide { get; set; } = string.Empty;
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal AvgEntryPrice { get; set; }
        public decimal AvgExitPrice { get; set; }
        public decimal TotalQuantity { get; set; }
        public decimal RealizedPnl { get; set; }
        public decimal Commission { get; set; }
        public decimal NetPnl { get; set; }
        public decimal? RoePct { get; set; }
        public int FillCount { get; set; }
        public string Category { get; set; } = string.Empty;

        public string OpenTimeText => OpenTime.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string CloseTimeText => CloseTime.ToLocalTime().ToString("MM-dd HH:mm:ss");
        public string DurationText
        {
            get
            {
                var d = CloseTime - OpenTime;
                if (d.TotalDays >= 1) return $"{d.TotalHours:F1}h";
                if (d.TotalHours >= 1) return $"{d.TotalHours:F1}h";
                if (d.TotalMinutes >= 1) return $"{d.TotalMinutes:F0}m";
                return $"{d.TotalSeconds:F0}s";
            }
        }
        public string SideKr => PositionSide == "LONG" ? "롱" : (PositionSide == "SHORT" ? "숏" : PositionSide);
    }
}
