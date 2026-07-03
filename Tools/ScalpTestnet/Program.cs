using System.Globalization;
using System.Text.Json;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using TradingBot.Scalp;

// ───────────────────────── 인자 파싱 ─────────────────────────
string symbol = "BTCUSDT", interval = "15m", cfgPath = "appsettings.json";
decimal margin = 50m; int leverage = 5; bool enter = false, force = false, protectOnly = false, closePos = false, doTelegram = false, doStats = false, doIncome = false, doRaw = false, doBackfill = false, doRecent = false, doKlines = false; int backfillDays = 3; string sinceKst = "";
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : "";
    switch (a)
    {
        case "--symbol": symbol = Next().ToUpperInvariant(); break;
        case "--interval": interval = Next(); break;
        case "--margin": margin = decimal.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--leverage": leverage = int.Parse(Next()); break;
        case "--config": cfgPath = Next(); break;
        case "--enter": enter = true; break;
        case "--force": force = true; break;
        case "--protect-only": protectOnly = true; break;
        case "--close": closePos = true; break;
        case "--telegram": doTelegram = true; break;
        case "--stats": doStats = true; break;
        case "--income": doIncome = true; break;
        case "--raw": doRaw = true; break;
        case "--backfill": doBackfill = true; break;
        case "--days": backfillDays = int.Parse(Next()); break;
        case "--recent": doRecent = true; break;
        case "--since": sinceKst = Next(); break;
        case "--klines": doKlines = true; break;
    }
}

// ───────────────────────── 캔들 구조 검증 (--klines --symbol X --since "KST" --interval 5m) ─────────────────────────
if (doKlines)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    string tk = doc.RootElement.GetProperty("Trading").GetProperty("TestnetApiKey").GetString() ?? "";
    string tsc = doc.RootElement.GetProperty("Trading").GetProperty("TestnetApiSecret").GetString() ?? "";
    var cli = new BinanceRestClient(o => { o.ApiCredentials = new ApiCredentials(tk, tsc); o.Environment = BinanceEnvironment.Testnet; });
    var iv = interval == "15m" ? KlineInterval.FifteenMinutes : interval == "1m" ? KlineInterval.OneMinute : KlineInterval.FiveMinutes;
    var startKst = DateTime.ParseExact(string.IsNullOrWhiteSpace(sinceKst) ? "2026-07-02 23:30" : sinceKst, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    var startUtc = startKst.AddHours(-9);
    var kr = await cli.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, iv, startTime: startUtc, endTime: startUtc.AddHours(4), limit: 100);
    Console.WriteLine($"── {symbol} {interval} 캔들 (KST {startKst:MM-dd HH:mm}부터) ──");
    Console.WriteLine("   KST시각 | 시가 | 고가 | 저가 | 종가 | 방향 | 변동%");
    if (kr.Success && kr.Data != null)
        foreach (var k in kr.Data)
        {
            var kkst = k.OpenTime.AddHours(9);
            decimal chg = k.OpenPrice > 0 ? (k.ClosePrice - k.OpenPrice) / k.OpenPrice * 100 : 0;
            string dir = k.ClosePrice >= k.OpenPrice ? "양" : "음";
            Console.WriteLine($"   {kkst:MM-dd HH:mm} | {k.OpenPrice} | {k.HighPrice} | {k.LowPrice} | {k.ClosePrice} | {dir} | {chg:F2}%");
        }
    else Console.WriteLine("   조회 실패: " + kr.Error?.Message);
    return;
}

// ───────────────────────── 특정 시점 이후 청산 조회 (--recent --since "yyyy-MM-dd HH:mm" KST) ─────────────────────────
if (doRecent)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var csn = doc.RootElement.GetProperty("ConnectionStrings");
    string conn2 = csn.GetProperty("DefaultConnection").GetString() ?? "";
    if (csn.TryGetProperty("IsEncrypted", out var e2) && e2.GetBoolean())
        conn2 = TradingBot.Shared.Services.SecurityService.DecryptString(conn2);
    using var db = new Microsoft.Data.SqlClient.SqlConnection(conn2);
    db.Open();

    string kst = string.IsNullOrWhiteSpace(sinceKst) ? "2026-07-02 20:00" : sinceKst;
    Console.WriteLine($"── 기준 시각(KST): {kst} 이후 청산 ──");

    void Dump(string title, string sql)
    {
        Console.WriteLine($"\n── {title} ──");
        try
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = sql;
            var p = cmd.CreateParameter(); p.ParameterName = "@kst"; p.Value = kst; cmd.Parameters.Add(p);
            using var rd = cmd.ExecuteReader();
            var cols = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToArray();
            Console.WriteLine("   " + string.Join(" | ", cols));
            int n = 0;
            while (rd.Read() && n++ < 100)
                Console.WriteLine("   " + string.Join(" | ", Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "" : rd.GetValue(i)!.ToString())));
        }
        catch (Exception ex) { Console.WriteLine("   (오류) " + ex.Message); }
    }

    // BPH: CloseTime 은 UTC → KST 기준 비교 위해 +9h. 표시도 KST 로.
    Dump("BinancePositionHistory (KST 기준 이후, 청산순)", @"
SELECT UserId, Symbol, PositionSide AS Side,
       FORMAT(DATEADD(hour,9,OpenTime),'MM-dd HH:mm') AS Open_KST,
       FORMAT(DATEADD(hour,9,CloseTime),'MM-dd HH:mm') AS Close_KST,
       CAST(NetPnl AS DECIMAL(18,2)) AS NetPnl, CAST(RoePct AS DECIMAL(10,1)) AS RoePct, Category
FROM dbo.BinancePositionHistory
WHERE DATEADD(hour,9,CloseTime) >= CONVERT(datetime,@kst)
ORDER BY CloseTime DESC");

    Dump("BPH 합계 (KST 기준 이후)", @"
SELECT UserId, COUNT(*) AS Cnt,
       SUM(CASE WHEN NetPnl>0 THEN 1 ELSE 0 END) AS Wins,
       SUM(CASE WHEN NetPnl<0 THEN 1 ELSE 0 END) AS Losses,
       CAST(SUM(NetPnl) AS DECIMAL(18,2)) AS SumNetPnl
FROM dbo.BinancePositionHistory
WHERE DATEADD(hour,9,CloseTime) >= CONVERT(datetime,@kst)
GROUP BY UserId");

    // TradeHistory: ExitTime 은 KST(로컬) 로 저장돼 있으므로 그대로 비교.
    Dump("TradeHistory (ExitTime 기준 이후, 청산순)", @"
SELECT TOP 100 Id, Symbol, Side,
       FORMAT(ExitTime,'MM-dd HH:mm') AS Exit_KST,
       CAST(PnL AS DECIMAL(18,2)) AS PnL, CAST(PnLPercent AS DECIMAL(10,1)) AS Roe,
       ExitReason, UserId, Strategy, IsSimulation
FROM dbo.TradeHistory
WHERE ExitTime >= CONVERT(datetime,@kst) AND IsClosed=1
ORDER BY ExitTime DESC");
    return;
}

// ───────────────────────── BPH 백필 (--backfill) — 실제 봇 sync 로직 재사용 ─────────────────────────
if (doBackfill)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var csn = doc.RootElement.GetProperty("ConnectionStrings");
    string conn2 = csn.GetProperty("DefaultConnection").GetString() ?? "";
    if (csn.TryGetProperty("IsEncrypted", out var e2) && e2.GetBoolean())
        conn2 = TradingBot.Shared.Services.SecurityService.DecryptString(conn2);

    // 유저별 테스트넷 키 조회 (봇 실제 거래 계정)
    var users = new List<(int id, string k, string s)>();
    using (var db = new Microsoft.Data.SqlClient.SqlConnection(conn2))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, TestnetApiKey, TestnetApiSecret FROM Users WHERE TestnetApiKey IS NOT NULL AND LEN(TestnetApiKey) > 0";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string k = TradingBot.Shared.Services.SecurityService.DecryptString(rd["TestnetApiKey"] as string ?? "");
            string s = TradingBot.Shared.Services.SecurityService.DecryptString(rd["TestnetApiSecret"] as string ?? "");
            if (!string.IsNullOrWhiteSpace(k)) users.Add(((int)rd["Id"], k, s));
        }
    }

    var sinceUtc = DateTime.UtcNow.AddDays(-backfillDays);
    Console.WriteLine($"── BPH 백필 시작 (최근 {backfillDays}일, since {sinceUtc:yyyy-MM-dd HH:mm} UTC) · 유저 {users.Count}명 ──");
    foreach (var (id, k, s) in users)
    {
        var cli = new BinanceRestClient(o => { o.ApiCredentials = new ApiCredentials(k, s); o.Environment = BinanceEnvironment.Testnet; });
        var sync = new TradingBot.Services.BinancePositionHistorySync(cli, conn2, id);
        sync.OnLog += m => Console.WriteLine($"   [U{id}] {m}");
        try { await sync.RunOnceAsync(sinceUtc); }
        catch (Exception ex) { Console.WriteLine($"   [U{id}] ❌ {ex.Message}"); }
    }
    Console.WriteLine("── 백필 완료 ──");
    return;
}

// ───────────────────────── DB raw 덤프 (--raw) ─────────────────────────
if (doRaw)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var csn = doc.RootElement.GetProperty("ConnectionStrings");
    string conn2 = csn.GetProperty("DefaultConnection").GetString() ?? "";
    if (csn.TryGetProperty("IsEncrypted", out var e2) && e2.GetBoolean())
        conn2 = TradingBot.Shared.Services.SecurityService.DecryptString(conn2);
    using var db = new Microsoft.Data.SqlClient.SqlConnection(conn2);
    db.Open();

    void Dump(string title, string sql)
    {
        Console.WriteLine($"\n── {title} ──");
        try
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = sql;
            using var rd = cmd.ExecuteReader();
            var cols = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToArray();
            Console.WriteLine("   " + string.Join(" | ", cols));
            int n = 0;
            while (rd.Read() && n++ < 40)
                Console.WriteLine("   " + string.Join(" | ", Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "" : rd.GetValue(i)!.ToString())));
        }
        catch (Exception ex) { Console.WriteLine("   (오류) " + ex.Message); }
    }

    Dump("TradeHistory 컬럼", "SELECT TOP 1 * FROM dbo.TradeHistory");
    Dump("TradeHistory 최근 20 (ExitTime desc)", "SELECT TOP 20 * FROM dbo.TradeHistory ORDER BY ExitTime DESC");
    Dump("TradeHistory 오늘합계(여러 TZ 해석)", @"
SELECT
  SUM(CASE WHEN CONVERT(date,ExitTime)=CONVERT(date,SYSUTCDATETIME()) THEN PnL ELSE 0 END) AS Pnl_UTCdate,
  COUNT(CASE WHEN CONVERT(date,ExitTime)=CONVERT(date,SYSUTCDATETIME()) THEN 1 END) AS Cnt_UTCdate,
  SUM(CASE WHEN CONVERT(date,DATEADD(hour,9,ExitTime))=CONVERT(date,DATEADD(hour,9,SYSUTCDATETIME())) THEN PnL ELSE 0 END) AS Pnl_KST,
  COUNT(CASE WHEN CONVERT(date,DATEADD(hour,9,ExitTime))=CONVERT(date,DATEADD(hour,9,SYSUTCDATETIME())) THEN 1 END) AS Cnt_KST,
  SUM(CASE WHEN CONVERT(date,ExitTime)=CONVERT(date,GETDATE()) THEN PnL ELSE 0 END) AS Pnl_LocalDate,
  COUNT(CASE WHEN CONVERT(date,ExitTime)=CONVERT(date,GETDATE()) THEN 1 END) AS Cnt_LocalDate
FROM dbo.TradeHistory");
    Dump("BinancePositionHistory 최근 20 (CloseTime desc)", "SELECT TOP 20 UserId, Symbol, PositionSide, OpenTime, CloseTime, NetPnl FROM dbo.BinancePositionHistory ORDER BY CloseTime DESC");
    return;
}

// ───────────────────────── 바이낸스 수익내역 진단 (--income, 라이브 키) ─────────────────────────
if (doIncome)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var csn = doc.RootElement.GetProperty("ConnectionStrings");
    string conn2 = csn.GetProperty("DefaultConnection").GetString() ?? "";
    if (csn.TryGetProperty("IsEncrypted", out var e2) && e2.GetBoolean())
        conn2 = TradingBot.Shared.Services.SecurityService.DecryptString(conn2);
    using var db = new Microsoft.Data.SqlClient.SqlConnection(conn2);
    db.Open();
    var users = new List<(int id, string k, string s)>();
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT Id, BinanceApiKey, BinanceApiSecret FROM Users WHERE BinanceApiKey IS NOT NULL AND LEN(BinanceApiKey) > 0";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string k = TradingBot.Shared.Services.SecurityService.DecryptString(rd["BinanceApiKey"] as string ?? "");
            string s = TradingBot.Shared.Services.SecurityService.DecryptString(rd["BinanceApiSecret"] as string ?? "");
            if (!string.IsNullOrWhiteSpace(k)) users.Add(((int)rd["Id"], k, s));
        }
    }
    var kstNow = DateTime.UtcNow.AddHours(9);
    var startUtc = kstNow.Date.AddHours(-9); // 오늘 KST 00:00 → UTC
    Console.WriteLine($"── 바이낸스 REALIZED_PNL (오늘 KST {kstNow:MM-dd}) ──");
    foreach (var (id, k, s) in users)
    {
        var cli = new BinanceRestClient(o => o.ApiCredentials = new ApiCredentials(k, s));
        var inc = await cli.UsdFuturesApi.Account.GetIncomeHistoryAsync(incomeType: "REALIZED_PNL", startTime: startUtc, limit: 1000);
        if (!inc.Success) { Console.WriteLine($"  User {id}: 조회 실패 {inc.Error?.Message}"); continue; }
        var rows = inc.Data.ToList();
        decimal tot = rows.Sum(x => x.Income);
        var bySym = rows.GroupBy(x => x.Symbol).Select(g => (Sym: g.Key, Cnt: g.Count(), Pnl: g.Sum(x => x.Income))).OrderByDescending(x => Math.Abs(x.Pnl)).ToList();
        Console.WriteLine($"  User {id}: REALIZED_PNL 합계 {tot:N2} · 청산 이벤트 {rows.Count}건 · 심볼 {bySym.Count}개");
        foreach (var b in bySym) Console.WriteLine($"      {b.Sym} : {b.Cnt}건 {b.Pnl:N2}");
    }

    // 유저별 테스트넷 계정(Users.TestnetApiKey) 오늘 실현손익
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT Id, TestnetApiKey, TestnetApiSecret FROM Users WHERE TestnetApiKey IS NOT NULL AND LEN(TestnetApiKey) > 0";
        var utn = new List<(int id, string k, string s)>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read())
            {
                string k = TradingBot.Shared.Services.SecurityService.DecryptString(rd["TestnetApiKey"] as string ?? "");
                string s = TradingBot.Shared.Services.SecurityService.DecryptString(rd["TestnetApiSecret"] as string ?? "");
                if (!string.IsNullOrWhiteSpace(k)) utn.Add(((int)rd["Id"], k, s));
            }
        Console.WriteLine("── 유저별 테스트넷 REALIZED_PNL (오늘 KST) ──");
        foreach (var (id, k, s) in utn)
        {
            var cli = new BinanceRestClient(o => { o.ApiCredentials = new ApiCredentials(k, s); o.Environment = BinanceEnvironment.Testnet; });
            var inc2 = await cli.UsdFuturesApi.Account.GetIncomeHistoryAsync(incomeType: "REALIZED_PNL", startTime: startUtc, limit: 1000);
            if (!inc2.Success) { Console.WriteLine($"  User {id} 테스트넷: 실패 {inc2.Error?.Message}"); continue; }
            var rows2 = inc2.Data.ToList();
            Console.WriteLine($"  User {id} 테스트넷: 합계 {rows2.Sum(x => x.Income):N2} · {rows2.Count}건 · 심볼 {rows2.Select(x => x.Symbol).Distinct().Count()}개 [{string.Join(",", rows2.Select(x => x.Symbol).Distinct().Take(12))}]");
        }
    }

    // 테스트넷 계정 오늘 실현손익 (appsettings 테스트넷 키)
    string tk = doc.RootElement.GetProperty("Trading").GetProperty("TestnetApiKey").GetString() ?? "";
    string ts = doc.RootElement.GetProperty("Trading").GetProperty("TestnetApiSecret").GetString() ?? "";
    if (!string.IsNullOrWhiteSpace(tk))
    {
        var tcli = new BinanceRestClient(o => { o.ApiCredentials = new ApiCredentials(tk, ts); o.Environment = BinanceEnvironment.Testnet; });
        var inc = await tcli.UsdFuturesApi.Account.GetIncomeHistoryAsync(incomeType: "REALIZED_PNL", startTime: startUtc, limit: 1000);
        Console.WriteLine("── 테스트넷 REALIZED_PNL (오늘 KST) ──");
        if (inc.Success)
        {
            var rows = inc.Data.ToList();
            var bySym = rows.GroupBy(x => x.Symbol).Select(g => (Sym: g.Key, Cnt: g.Count(), Pnl: g.Sum(x => x.Income))).OrderByDescending(x => Math.Abs(x.Pnl)).ToList();
            Console.WriteLine($"  테스트넷: REALIZED_PNL 합계 {rows.Sum(x => x.Income):N2} · 청산 이벤트 {rows.Count}건 · 심볼 {bySym.Count}개");
            foreach (var b in bySym) Console.WriteLine($"      {b.Sym} : {b.Cnt}건 {b.Pnl:N2}");
        }
        else Console.WriteLine($"  테스트넷 조회 실패: {inc.Error?.Message}");
    }
    return;
}

// ───────────────────────── DB 매매기록 통계 진단 (--stats) ─────────────────────────
if (doStats)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var cs = doc.RootElement.GetProperty("ConnectionStrings");
    string conn = cs.GetProperty("DefaultConnection").GetString() ?? "";
    if (cs.TryGetProperty("IsEncrypted", out var enc) && enc.GetBoolean())
        conn = TradingBot.Shared.Services.SecurityService.DecryptString(conn);
    using var db = new Microsoft.Data.SqlClient.SqlConnection(conn);
    db.Open();
    Console.WriteLine("── BinancePositionHistory 진단 (CloseTime · KST) ──");
    // 유저별 최근 8일 일자별 합계/건수
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"
SELECT UserId,
       CONVERT(date, DATEADD(hour, 9, CloseTime)) AS D,
       COUNT(*) AS Cnt,
       SUM(CASE WHEN NetPnl>0 THEN 1 ELSE 0 END) AS Wins,
       SUM(NetPnl) AS Pnl
FROM dbo.BinancePositionHistory
WHERE CloseTime >= DATEADD(day,-8, SYSUTCDATETIME())
GROUP BY UserId, CONVERT(date, DATEADD(hour, 9, CloseTime))
ORDER BY UserId, D DESC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            Console.WriteLine($"  User {rd["UserId"]} | {((DateTime)rd["D"]):yyyy-MM-dd} | 건수 {rd["Cnt"]} | 승 {rd["Wins"]} | NetPnl합 {Convert.ToDecimal(rd["Pnl"]):N2}");
    }
    // 오늘(KST) 거래 상세
    Console.WriteLine("── 오늘(KST) 청산 거래 상세 ──");
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"
SELECT UserId, Symbol, PositionSide, CloseTime, NetPnl, RealizedPnl, Commission, RoePct
FROM dbo.BinancePositionHistory
WHERE CONVERT(date, DATEADD(hour,9,CloseTime)) = CONVERT(date, DATEADD(hour,9,SYSUTCDATETIME()))
ORDER BY CloseTime";
        using var rd = cmd.ExecuteReader();
        int n = 0; decimal sum = 0;
        while (rd.Read())
        {
            n++; decimal net = Convert.ToDecimal(rd["NetPnl"]); sum += net;
            Console.WriteLine($"  U{rd["UserId"]} {rd["Symbol"]} {rd["PositionSide"]} close={((DateTime)rd["CloseTime"]).AddHours(9):MM-dd HH:mm} Net={net:N2} Realized={Convert.ToDecimal(rd["RealizedPnl"]):N2} Fee={Convert.ToDecimal(rd["Commission"]):N2}");
        }
        Console.WriteLine($"  → 오늘 거래 {n}건, NetPnl 합계 = {sum:N2}");
    }
    return;
}

// ───────────────────────── 테스트넷 키 로드 ─────────────────────────
string key = Environment.GetEnvironmentVariable("BINANCE_TESTNET_KEY") ?? "";
string secret = Environment.GetEnvironmentVariable("BINANCE_TESTNET_SECRET") ?? "";
if (string.IsNullOrWhiteSpace(key) && File.Exists(cfgPath))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    if (doc.RootElement.TryGetProperty("Trading", out var t))
    {
        if (t.TryGetProperty("TestnetApiKey", out var k)) key = k.GetString() ?? "";
        if (t.TryGetProperty("TestnetApiSecret", out var s)) secret = s.GetString() ?? "";
    }
}
if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
{
    Console.WriteLine("❌ 테스트넷 키를 찾지 못했습니다. appsettings.json(Trading.TestnetApiKey/Secret) 또는 환경변수 BINANCE_TESTNET_KEY/SECRET 필요.");
    return;
}
Console.WriteLine($"🔑 테스트넷 키 로드 완료 (key …{key[^4..]}) · 심볼 {symbol} · {interval} · margin ${margin} · {leverage}x · enter={enter} force={force}");

var client = new BinanceRestClient(o =>
{
    o.ApiCredentials = new ApiCredentials(key, secret);
    o.Environment = BinanceEnvironment.Testnet;
});

// ───────────────────────── 잔고 ─────────────────────────
var bal = await client.UsdFuturesApi.Account.GetBalancesAsync();
if (bal.Success)
{
    var usdt = bal.Data.FirstOrDefault(b => b.Asset == "USDT");
    Console.WriteLine($"💰 테스트넷 USDT 잔고: {usdt?.AvailableBalance:N2} (총 {usdt?.WalletBalance:N2})");
}
else Console.WriteLine($"⚠ 잔고 조회 실패: {bal.Error?.Message}");

// ───────────────────────── 캔들 → 판정 ─────────────────────────
var ki = MapInterval(interval);
var kl = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, ki, limit: 500);
if (!kl.Success) { Console.WriteLine($"❌ 캔들 조회 실패: {kl.Error?.Message}"); return; }
var candles = kl.Data.Select(x => new Candle(x.OpenTime, (double)x.OpenPrice, (double)x.HighPrice, (double)x.LowPrice, (double)x.ClosePrice, (double)x.Volume)).ToList();
Console.WriteLine($"📊 캔들 {candles.Count}개 (마지막 종가 {candles[^1].Close})");

var r = ScalpEngine.Evaluate(symbol, interval, candles);
Console.WriteLine("──────────── 단타 판정 ────────────");
Console.WriteLine($"  판정: {r.DecisionText}  (품질 {r.Quality})");
Console.WriteLine($"  트리거: {r.Trigger}");
Console.WriteLine($"  진입 {r.Entry:0.######} · 목표 {r.Target:0.######} · 손절 {r.Stop:0.######} · 손익비 1:{r.RiskReward:F1}");
if (!string.IsNullOrEmpty(r.Warning)) Console.WriteLine($"  {r.Warning}");
Console.WriteLine($"  근거: {string.Join(", ", r.Reasons)}");
Console.WriteLine("───────────────────────────────────");

// ───────────────────────── 텔레그램 (DB에서 토큰 조회) ─────────────────────────
string tgToken = "", tgChat = "";
if (doTelegram || enter)
{
    try
    {
        (tgToken, tgChat) = GetTelegramFromDb(cfgPath);
        Console.WriteLine(string.IsNullOrEmpty(tgToken) ? "⚠ DB에 텔레그램 토큰이 없습니다." : $"📨 텔레그램 로드 완료 (chatId {tgChat}, token …{tgToken[^4..]})");
    }
    catch (Exception e) { Console.WriteLine($"⚠ 텔레그램 DB 조회 실패: {e.Message}"); }
}
if (doTelegram && !string.IsNullOrEmpty(tgToken))
{
    await SendTelegram(tgToken, tgChat, $"🤖 [ScalpTestnet] {symbol} {interval}\n판정: {r.DecisionText} (품질 {r.Quality})\n{r.Trigger}\n진입 {r.Entry:0.##} · 목표 {r.Target:0.##} · 손절 {r.Stop:0.##}");
    Console.WriteLine("📨 텔레그램 테스트 메시지 전송 완료 (텔레그램 앱에서 확인)");
}

if (!enter && !protectOnly && !closePos) { Console.WriteLine(doTelegram ? "ℹ️ 텔레그램 테스트 완료." : "ℹ️ dry-run (주문 안 함). --enter / --protect-only / --close / --telegram"); return; }

// 틱/스텝 사이즈
var info = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
var symInfo = info.Data.Symbols.First(s => s.Name == symbol);
decimal step = symInfo.LotSizeFilter?.StepSize ?? 0.001m;
decimal tick = symInfo.PriceFilter?.TickSize ?? 0.01m;
decimal RoundTick(double v) => tick > 0 ? Math.Floor((decimal)v / tick) * tick : (decimal)v;

var http = new HttpClient();

// 현재 포지션
async Task<(bool open, decimal qty, bool isLong)> GetPos()
{
    var pr = await client.UsdFuturesApi.Account.GetPositionInformationAsync();
    var p = pr.Success ? pr.Data.FirstOrDefault(x => x.Symbol == symbol && x.Quantity != 0) : null;
    if (p == null) return (false, 0, false);
    return (true, Math.Abs(p.Quantity), p.Quantity > 0);
}

// ── 청산 모드 ──
if (closePos)
{
    var (open0, q0, long0) = await GetPos();
    if (!open0) { Console.WriteLine("청산할 포지션 없음."); return; }
    var closeSide = long0 ? OrderSide.Sell : OrderSide.Buy;
    var cRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(symbol, closeSide, FuturesOrderType.Market, quantity: q0, reduceOnly: true);
    Console.WriteLine(cRes.Success ? $"✅ 포지션 청산 완료 ({closeSide} {q0})" : $"❌ 청산 실패: {cRes.Error?.Message}");
    return;
}

bool isLong; decimal qty;

if (protectOnly)
{
    var (open1, q1, long1) = await GetPos();
    if (!open1) { Console.WriteLine("보호할 포지션이 없습니다."); return; }
    isLong = long1; qty = q1;
    Console.WriteLine($"🛡 기존 포지션 보호: {(isLong ? "롱" : "숏")} qty={qty}");
}
else
{
    if (r.Decision != ScalpDecision.Enter && !force) { Console.WriteLine("⏸ 진입 신호 아님. --force로 강제 가능."); return; }
    Console.WriteLine("🚀 테스트넷 진입 주문...");
    await client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage);
    var priceRes = await client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol);
    decimal price = priceRes.Data.Price;
    qty = margin * leverage / price;
    if (step > 0) qty = Math.Floor(qty / step) * step;
    isLong = r.Side == TradeSide.Long;
    var side = isLong ? OrderSide.Buy : OrderSide.Sell;
    Console.WriteLine($"  진입: {side} {symbol} qty={qty} @시장가 (~{price})");
    var entryRes = await client.UsdFuturesApi.Trading.PlaceOrderAsync(symbol, side, FuturesOrderType.Market, quantity: qty);
    if (!entryRes.Success) { Console.WriteLine($"❌ 진입 실패: {entryRes.Error?.Code} {entryRes.Error?.Message}"); return; }
    Console.WriteLine($"  ✅ 진입 체결 OrderId={entryRes.Data.Id}");
}

// ── TP/SL: Algo Order API (CONDITIONAL) — 메인 봇과 동일 방식 ──
string oppStr = isLong ? "SELL" : "BUY";
await AlgoCond(oppStr, "STOP_MARKET", qty, RoundTick(r.Stop), "손절(SL)");
await AlgoCond(oppStr, "TAKE_PROFIT_MARKET", qty, RoundTick(r.Target), "익절(TP)");

var posF = await client.UsdFuturesApi.Account.GetPositionInformationAsync();
if (posF.Success)
{
    var p = posF.Data.FirstOrDefault(x => x.Symbol == symbol && x.Quantity != 0);
    if (p != null) Console.WriteLine($"📈 포지션: {symbol} qty={p.Quantity} entry={p.EntryPrice} uPnL={p.UnrealizedPnl}");
}

// 진입 텔레그램 알림
if (!string.IsNullOrEmpty(tgToken) && !protectOnly)
{
    await SendTelegram(tgToken, tgChat, $"✅ [테스트넷 진입] {symbol} {(isLong ? "롱" : "숏")} qty={qty}\n진입 ~{r.Entry:0.##} · 익절 {r.Target:0.##} · 손절 {r.Stop:0.##} ({interval})");
    Console.WriteLine("📨 진입 텔레그램 알림 전송 완료");
}

Console.WriteLine("✅ 완료. 바이낸스 테스트넷 선물에서 포지션/조건부주문 확인하세요.");

async Task<bool> AlgoCond(string sideStr, string type, decimal q, decimal trigger, string label)
{
    long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    string qs = $"symbol={symbol}&side={sideStr}&algoType=CONDITIONAL&type={type}&quantity={q.ToString(CultureInfo.InvariantCulture)}&triggerPrice={trigger.ToString(CultureInfo.InvariantCulture)}&reduceOnly=true&timestamp={ts}";
    string sig = Sign(qs, secret);
    var req = new HttpRequestMessage(HttpMethod.Post, "https://testnet.binancefuture.com/fapi/v1/algoOrder");
    req.Content = new StringContent($"{qs}&signature={sig}", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
    req.Headers.Add("X-MBX-APIKEY", key);
    var resp = await http.SendAsync(req);
    string body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine(resp.IsSuccessStatusCode ? $"  ✅ {label} 등록 @ {trigger} | {body}" : $"  ❌ {label} 실패 HTTP{(int)resp.StatusCode}: {body}");
    return resp.IsSuccessStatusCode;
}

static string Sign(string q, string sec)
{
    using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(sec));
    var hash = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(q));
    var sb = new System.Text.StringBuilder(hash.Length * 2);
    foreach (var b in hash) sb.Append(b.ToString("x2"));
    return sb.ToString();
}

// DB에서 텔레그램 토큰/챗ID 조회 (연결문자열·토큰 모두 SecurityService로 복호화)
static (string token, string chatId) GetTelegramFromDb(string cfgPath)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var cs = doc.RootElement.GetProperty("ConnectionStrings");
    string conn = cs.GetProperty("DefaultConnection").GetString() ?? "";
    bool enc = cs.TryGetProperty("IsEncrypted", out var e) && e.GetBoolean();
    if (enc) conn = TradingBot.Shared.Services.SecurityService.DecryptString(conn);
    if (string.IsNullOrWhiteSpace(conn)) return ("", "");

    using var db = new Microsoft.Data.SqlClient.SqlConnection(conn);
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT TOP 1 TelegramBotToken, TelegramChatId FROM Users WHERE TelegramBotToken IS NOT NULL AND LEN(TelegramBotToken) > 0 ORDER BY Id";
    using var rd = cmd.ExecuteReader();
    if (!rd.Read()) return ("", "");
    string tok = rd["TelegramBotToken"] as string ?? "";
    string chat = rd["TelegramChatId"] as string ?? "";
    tok = TradingBot.Shared.Services.SecurityService.DecryptString(tok);
    chat = TradingBot.Shared.Services.SecurityService.DecryptString(chat);
    return (tok, chat);
}

static async Task SendTelegram(string token, string chatId, string text)
{
    using var http = new HttpClient();
    string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={Uri.EscapeDataString(chatId)}&text={Uri.EscapeDataString(text)}";
    try { var resp = await http.GetAsync(url); if (!resp.IsSuccessStatusCode) Console.WriteLine($"⚠ 텔레그램 전송 실패 HTTP{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}"); }
    catch (Exception e) { Console.WriteLine($"⚠ 텔레그램 전송 예외: {e.Message}"); }
}

static KlineInterval MapInterval(string itv) => itv switch
{
    "1m" => KlineInterval.OneMinute,
    "3m" => KlineInterval.ThreeMinutes,
    "5m" => KlineInterval.FiveMinutes,
    "15m" => KlineInterval.FifteenMinutes,
    "30m" => KlineInterval.ThirtyMinutes,
    "1h" => KlineInterval.OneHour,
    "4h" => KlineInterval.FourHour,
    _ => KlineInterval.FifteenMinutes
};
