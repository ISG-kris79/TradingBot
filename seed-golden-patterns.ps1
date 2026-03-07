param(
    [string]$Symbol = "XRPUSDT",
    [int]$Days = 3,
    [string]$LongDate = "2026-03-07",
    [string]$ShortDate = "2026-03-06",
    [switch]$UseUtc,
    [switch]$Insert,
    [int]$UserId = 0,
    [string]$ConnectionString = ""
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Golden Pattern Seeder" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Symbol: $Symbol | Days: $Days | Insert: $($Insert.IsPresent)" -ForegroundColor Gray

if ($Insert.IsPresent -and [string]::IsNullOrWhiteSpace($ConnectionString)) {
    if (-not (Test-Path ".\appsettings.json")) {
        throw "ConnectionString 미지정 + appsettings.json 없음. -ConnectionString 값을 전달하세요."
    }

    $app = Get-Content ".\appsettings.json" -Raw | ConvertFrom-Json
    $conn = $app.ConnectionStrings.DefaultConnection
    $isEncrypted = $app.ConnectionStrings.IsEncrypted

    if ($isEncrypted -eq $true) {
        try {
            Add-Type -AssemblyName System.Security
            $cipherBytes = [Convert]::FromBase64String($conn)
            $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
                $cipherBytes,
                $null,
                [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
            $decrypted = [System.Text.Encoding]::UTF8.GetString($plainBytes)

            if ([string]::IsNullOrWhiteSpace($decrypted)) {
                throw "복호화 결과가 비어 있습니다."
            }

            $ConnectionString = $decrypted
            Write-Host "Encrypted ConnectionString 복호화 성공 (DPAPI/CurrentUser)" -ForegroundColor Green
        }
        catch {
            try {
                # AES-256 fallback (SharedServicesCompat.SecurityService와 동일 키/포맷)
                Add-Type -AssemblyName System.Security
                $fullCipher = [Convert]::FromBase64String($conn)
                [byte[]]$aesKey = 0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42

                $aes = [System.Security.Cryptography.Aes]::Create()
                $aes.Key = $aesKey
                $ivLength = $aes.IV.Length

                if ($fullCipher.Length -le $ivLength) {
                    throw "암호문 길이가 유효하지 않습니다."
                }

                $iv = New-Object byte[] $ivLength
                $cipher = New-Object byte[] ($fullCipher.Length - $ivLength)
                [Buffer]::BlockCopy($fullCipher, 0, $iv, 0, $ivLength)
                [Buffer]::BlockCopy($fullCipher, $ivLength, $cipher, 0, $cipher.Length)

                $aes.IV = $iv
                $decryptor = $aes.CreateDecryptor($aes.Key, $aes.IV)
                $plainBytes = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
                $decrypted = [System.Text.Encoding]::UTF8.GetString($plainBytes)

                if ([string]::IsNullOrWhiteSpace($decrypted)) {
                    throw "AES 복호화 결과가 비어 있습니다."
                }

                $ConnectionString = $decrypted
                Write-Host "Encrypted ConnectionString 복호화 성공 (AES/Compat)" -ForegroundColor Green
            }
            catch {
                throw "연결문자열 자동 복호화 실패(DPAPI+AES): $($_.Exception.Message)  (필요 시 -ConnectionString 직접 전달)"
            }
        }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($conn)) {
            throw "appsettings.json에서 ConnectionStrings.DefaultConnection을 찾지 못했습니다."
        }
        $ConnectionString = $conn
    }
}

if ($Insert.IsPresent -and $UserId -le 0) {
    try {
        Add-Type -AssemblyName System.Data
        $connObj = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
        $connObj.Open()

        $cmd = $connObj.CreateCommand()
        $cmd.CommandText = "SELECT TOP (1) Id FROM dbo.Users ORDER BY Id"
        $val = $cmd.ExecuteScalar()

        if ($null -ne $val -and [int]$val -gt 0) {
            $UserId = [int]$val
            Write-Host "UserId 자동탐지: $UserId" -ForegroundColor Green
        }
        else {
            $UserId = 1
            Write-Host "UserId 자동탐지 실패 → 기본값 1 사용" -ForegroundColor Yellow
        }
    }
    catch {
        $UserId = 1
        Write-Host "UserId 조회 실패 → 기본값 1 사용 ($($_.Exception.Message))" -ForegroundColor Yellow
    }
    finally {
        if ($connObj) { $connObj.Dispose() }
    }
}

$tempDir = Join-Path $env:TEMP "TradingBotGoldenSeed_$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$pwdEsc = $PWD.Path.Replace('\\','\\\\')

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Binance.Net" Version="12.8.1" />
    <PackageReference Include="Skender.Stock.Indicators" Version="2.7.1" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$pwdEsc\\HybridStrategyScorer.cs" Link="HybridStrategyScorer.cs" />
    <Compile Include="$pwdEsc\\IndicatorCalculator.cs" Link="IndicatorCalculator.cs" />
    <Compile Include="$pwdEsc\\HybridStrategyBacktester.cs" Link="HybridStrategyBacktester.cs" />
  </ItemGroup>
</Project>
"@

$shim = @"
namespace TradingBot.Models
{
    public struct BBResult
    {
        public double Upper;
        public double Mid;
        public double Lower;
    }

    public class CandleData
    {
        public string? Symbol { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public float Volume { get; set; }
        public string? Interval { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public float RSI { get; set; }
        public float BollingerUpper { get; set; }
        public float BollingerLower { get; set; }
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float MACD_Hist { get; set; }
        public float ATR { get; set; }
        public float Fib_236 { get; set; }
        public float Fib_382 { get; set; }
        public float Fib_500 { get; set; }
        public float Fib_618 { get; set; }
        public float SentimentScore { get; set; }
        public float ElliottWaveState { get; set; }
        public float SMA_20 { get; set; }
        public float SMA_60 { get; set; }
        public float SMA_120 { get; set; }
        public bool Label { get; set; }
    }
}

namespace TradingBot.Services
{
    public static class TorchInitializer
    {
        public static bool IsAvailable => true;
        public static string ErrorMessage => "";
    }
}
"@

$program = @'
using System.Reflection;
using System.Text.Json;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Microsoft.Data.SqlClient;
using TradingBot.Services;
using TradingBot.Services.Backtest;
using TradingBot.Strategies;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string symbol = "__SYMBOL__";
int days = __DAYS__;
string longDate = "__LONGDATE__";
string shortDate = "__SHORTDATE__";
bool useUtc = __USEUTC__;
bool doInsert = __INSERT__;
int userId = __USERID__;
string connectionString = @"__CONNSTR__";

DateTime ParseTarget(string date, string hhmm)
{
    var dt = DateTime.Parse(date + " " + hhmm + ":00");
    return useUtc ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
}

var targets = new[]
{
    new { TimeUtc = ParseTarget(longDate, "02:30"), Side = "LONG", Roe = 8.0m, Name = "XRP_LONG_0230" },
    new { TimeUtc = ParseTarget(longDate, "05:30"), Side = "LONG", Roe = 18.0m, Name = "XRP_LONG_0530" },
    new { TimeUtc = ParseTarget(shortDate, "20:15"), Side = "SHORT", Roe = 12.0m, Name = "XRP_SHORT_2015" },
};

Console.WriteLine("Target mode: " + (useUtc ? "UTC" : "LOCAL->UTC"));
foreach (var t in targets)
    Console.WriteLine($"- {t.Name}: {t.TimeUtc:yyyy-MM-dd HH:mm} UTC");
Console.WriteLine();

var bt = new HybridStrategyBacktester
{
    PerfectAI = true,
    EnableComponentGate = true,
    AdxPeriod = 14,
    AdxSidewaysThreshold = 17.0,
    FutureLookAhead = 10,
};

var type = typeof(HybridStrategyBacktester);
var miBuildContext = type.GetMethod("BuildContext", BindingFlags.NonPublic | BindingFlags.Instance)
    ?? throw new Exception("BuildContext reflection 실패");
var miCalcDyn = type.GetMethod("CalculateDynamicThreshold", BindingFlags.NonPublic | BindingFlags.Instance)
    ?? throw new Exception("CalculateDynamicThreshold reflection 실패");
var miCalcHtf = type.GetMethod("CalculateHTFPenaltyLocal", BindingFlags.NonPublic | BindingFlags.Instance)
    ?? throw new Exception("CalculateHTFPenaltyLocal reflection 실패");

using var client = new BinanceRestClient();
var endUtc = DateTime.UtcNow;
var startUtc = endUtc.AddDays(-days);

async Task<List<IBinanceKline>> Fetch(KlineInterval interval)
{
    var list = new List<IBinanceKline>();
    var cursor = startUtc;
    while (cursor < endUtc)
    {
        var resp = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, cursor, endUtc, 1000);
        if (!resp.Success || resp.Data == null || !resp.Data.Any()) break;
        var chunk = resp.Data.ToList();
        list.AddRange(chunk);
        cursor = chunk.Last().OpenTime.AddMinutes(interval == KlineInterval.FiveMinutes ? 5 : interval == KlineInterval.FifteenMinutes ? 15 : 60);
        if (chunk.Count < 1000) break;
        await Task.Delay(200);
    }

    return list
        .GroupBy(k => k.OpenTime)
        .Select(g => g.First())
        .OrderBy(k => k.OpenTime)
        .ToList();
}

var klines5m = await Fetch(KlineInterval.FiveMinutes);
var klines15m = await Fetch(KlineInterval.FifteenMinutes);
var klines1h = await Fetch(KlineInterval.OneHour);

if (klines5m.Count < 260)
    throw new Exception("5분봉 데이터가 부족합니다.");

Console.WriteLine($"5m candles: {klines5m.Count} ({klines5m.First().OpenTime:MM-dd HH:mm} ~ {klines5m.Last().OpenTime:MM-dd HH:mm} UTC)");
Console.WriteLine();

if (doInsert && string.IsNullOrWhiteSpace(connectionString))
    throw new Exception("Insert 모드인데 ConnectionString이 비어 있습니다.");

if (doInsert)
{
    using var db = new SqlConnection(connectionString);
    await db.OpenAsync();
    string ensureSql = @"
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
        ComponentMix NVARCHAR(500) NULL,
        ContextJson NVARCHAR(MAX) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_TradePatternSnapshots_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_TradePatternSnapshots_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END";
    await new SqlCommand(ensureSql, db).ExecuteNonQueryAsync();
}

var scorer = new HybridStrategyScorer();

foreach (var target in targets)
{
    int i = klines5m.FindIndex(k => k.OpenTime == target.TimeUtc);
    if (i < 0)
    {
        i = klines5m.Select((k, idx) => new { idx, diff = Math.Abs((k.OpenTime - target.TimeUtc).TotalMinutes) })
            .OrderBy(x => x.diff)
            .First().idx;
    }

    if (i < 240 || i >= klines5m.Count - bt.FutureLookAhead)
    {
        Console.WriteLine($"[SKIP] {target.Name} 인덱스 부족: {i}");
        continue;
    }

    var current = klines5m[i];
    var currentKlines = klines5m.GetRange(0, i + 1);
    decimal currentPrice = current.ClosePrice;

    double atr = IndicatorCalculator.CalculateATR(currentKlines, 14);
    double dyn = (double)miCalcDyn.Invoke(bt, new object[] { currentPrice, atr })!;

    var adxTuple = IndicatorCalculator.CalculateADX(currentKlines, bt.AdxPeriod);
    double adx = adxTuple.Item1;
    double plusDi = adxTuple.Item2;
    double minusDi = adxTuple.Item3;

    var ctx = (HybridStrategyScorer.TechnicalContext)miBuildContext.Invoke(bt, new object[] { currentKlines, currentPrice })!;

    decimal futurePrice = klines5m[i + bt.FutureLookAhead].ClosePrice;
    decimal predictedChange = currentPrice > 0 ? (futurePrice - currentPrice) / currentPrice : 0;
    decimal predictedPrice = currentPrice * (1 + predictedChange);

    var longResult = scorer.EvaluateLong(symbol, predictedChange, predictedPrice, ctx);
    var shortResult = scorer.EvaluateShort(symbol, predictedChange, predictedPrice, ctx);

    if (HybridStrategyScorer.IsMajorCoin(symbol))
    {
        longResult.FinalScore = Math.Clamp(longResult.FinalScore + HybridStrategyScorer.GetMajorCoinBonus(symbol, "LONG", ctx), 0, 100);
        shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + HybridStrategyScorer.GetMajorCoinBonus(symbol, "SHORT", ctx), 0, 100);
    }

    string candidateDir = longResult.FinalScore > shortResult.FinalScore ? "LONG" : "SHORT";
    int rangeStart = Math.Max(0, i - 29);
    int rangeCount = Math.Min(30, i + 1);
    var recent30 = klines5m.GetRange(rangeStart, rangeCount);
    double htfPenalty = (double)miCalcHtf.Invoke(bt, new object[] { symbol, currentPrice, candidateDir, current.OpenTime, klines15m, klines1h, recent30 })!;

    longResult.FinalScore = Math.Clamp(longResult.FinalScore + htfPenalty, 0, 100);
    shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + htfPenalty, 0, 100);

    bool forLong = string.Equals(target.Side, "LONG", StringComparison.OrdinalIgnoreCase);
    var primary = forLong ? longResult : shortResult;
    var opposite = forLong ? shortResult : longResult;

    double bbPos = 0.5;
    double bbRange = ctx.BbUpper - ctx.BbLower;
    if (bbRange > 0)
        bbPos = Math.Clamp(((double)currentPrice - ctx.BbLower) / bbRange, 0, 1);

    string mode = adx < 17.0 ? "SIDEWAYS" : "TREND";
    string componentMix = $"AI({primary.AiPredictionScore:F1}),EW({primary.ElliottWaveScore:F1}),Vol({primary.VolumeMomentumScore:F1}),RSI/M({primary.RsiMacdScore:F1}),BB({primary.BollingerScore:F1})";
    string contextJson = JsonSerializer.Serialize(new
    {
        atrPct = currentPrice > 0 ? atr / (double)currentPrice * 100 : 0,
        htfPenalty,
        adx,
        plusDi,
        minusDi,
        bbPos,
        rsi = ctx.RSI,
        volumeRatio = ctx.VolumeRatio,
        dynThreshold = dyn,
        target = target.Name,
        seedType = "MANUAL_GOLDEN_SEED"
    });

    Console.WriteLine($"[SNAPSHOT] {target.Name} @ {current.OpenTime:yyyy-MM-dd HH:mm} UTC");
    Console.WriteLine($"  Price={currentPrice:F6} Side={target.Side} Mode={mode} Final={primary.FinalScore:F1} Gap={primary.FinalScore - opposite.FinalScore:F1}");
    Console.WriteLine($"  Pred={predictedChange * 100m:+0.000;-0.000}% HTF={htfPenalty:F1} ADX={adx:F1}");

    if (!doInsert)
    {
        Console.WriteLine("  Preview only (DB insert skipped)");
        Console.WriteLine();
        continue;
    }

    using var db = new SqlConnection(connectionString);
    await db.OpenAsync();

    string upsertSql = @"
UPDATE dbo.TradePatternSnapshots
SET
    Mode = @Mode,
    ExitTime = @ExitTime,
    EntryPrice = @EntryPrice,
    FinalScore = @FinalScore,
    AiScore = @AiScore,
    ElliottScore = @ElliottScore,
    VolumeScore = @VolumeScore,
    RsiMacdScore = @RsiMacdScore,
    BollingerScore = @BollingerScore,
    PredictedChangePct = @PredictedChangePct,
    ScoreGap = @ScoreGap,
    AtrPercent = @AtrPercent,
    HtfPenalty = @HtfPenalty,
    Adx = @Adx,
    PlusDi = @PlusDi,
    MinusDi = @MinusDi,
    Rsi = @Rsi,
    MacdHist = @MacdHist,
    BbPosition = @BbPosition,
    VolumeRatio = @VolumeRatio,
    SimilarityScore = 1.0,
    EuclideanSimilarity = 1.0,
    CosineSimilarity = 1.0,
    MatchProbability = 0.95,
    MatchedPatternId = NULL,
    IsSuperEntry = 1,
    PositionSizeMultiplier = 2.0,
    TakeProfitMultiplier = 1.2,
    Label = 1,
    PnL = @PnL,
    PnLPercent = @PnLPercent,
    ComponentMix = @ComponentMix,
    ContextJson = @ContextJson,
    UpdatedAt = SYSUTCDATETIME()
WHERE
    UserId = @UserId
    AND Symbol = @Symbol
    AND Side = @Side
    AND EntryTime = @EntryTime
    AND (ISNULL(Strategy, '') = 'MANUAL_GOLDEN' OR ISNULL(Strategy, '') = ISNULL(@Strategy, ''));

IF @@ROWCOUNT = 0
BEGIN
INSERT INTO dbo.TradePatternSnapshots
(
    UserId, Symbol, Side, Strategy, Mode, EntryTime, ExitTime, EntryPrice,
    FinalScore, AiScore, ElliottScore, VolumeScore, RsiMacdScore, BollingerScore,
    PredictedChangePct, ScoreGap,
    AtrPercent, HtfPenalty, Adx, PlusDi, MinusDi, Rsi, MacdHist, BbPosition, VolumeRatio,
    SimilarityScore, EuclideanSimilarity, CosineSimilarity, MatchProbability, MatchedPatternId,
    IsSuperEntry, PositionSizeMultiplier, TakeProfitMultiplier,
    Label, PnL, PnLPercent,
    ComponentMix, ContextJson, UpdatedAt
)
VALUES
(
    @UserId, @Symbol, @Side, @Strategy, @Mode, @EntryTime, @ExitTime, @EntryPrice,
    @FinalScore, @AiScore, @ElliottScore, @VolumeScore, @RsiMacdScore, @BollingerScore,
    @PredictedChangePct, @ScoreGap,
    @AtrPercent, @HtfPenalty, @Adx, @PlusDi, @MinusDi, @Rsi, @MacdHist, @BbPosition, @VolumeRatio,
    1.0, 1.0, 1.0, 0.95, NULL,
    1, 2.0, 1.2,
    1, @PnL, @PnLPercent,
    @ComponentMix, @ContextJson, SYSUTCDATETIME()
);
END";

    using var cmd = new SqlCommand(upsertSql, db);
    cmd.Parameters.AddWithValue("@UserId", userId);
    cmd.Parameters.AddWithValue("@Symbol", symbol);
    cmd.Parameters.AddWithValue("@Side", target.Side);
    cmd.Parameters.AddWithValue("@Strategy", "MANUAL_GOLDEN_SEED");
    cmd.Parameters.AddWithValue("@Mode", mode);
    cmd.Parameters.AddWithValue("@EntryTime", current.OpenTime);
    cmd.Parameters.AddWithValue("@ExitTime", current.OpenTime.AddMinutes(50));
    cmd.Parameters.AddWithValue("@EntryPrice", currentPrice);
    cmd.Parameters.AddWithValue("@FinalScore", primary.FinalScore);
    cmd.Parameters.AddWithValue("@AiScore", primary.AiPredictionScore);
    cmd.Parameters.AddWithValue("@ElliottScore", primary.ElliottWaveScore);
    cmd.Parameters.AddWithValue("@VolumeScore", primary.VolumeMomentumScore);
    cmd.Parameters.AddWithValue("@RsiMacdScore", primary.RsiMacdScore);
    cmd.Parameters.AddWithValue("@BollingerScore", primary.BollingerScore);
    cmd.Parameters.AddWithValue("@PredictedChangePct", (double)(predictedChange * 100m));
    cmd.Parameters.AddWithValue("@ScoreGap", primary.FinalScore - opposite.FinalScore);
    cmd.Parameters.AddWithValue("@AtrPercent", currentPrice > 0 ? atr / (double)currentPrice * 100 : 0);
    cmd.Parameters.AddWithValue("@HtfPenalty", htfPenalty);
    cmd.Parameters.AddWithValue("@Adx", adx);
    cmd.Parameters.AddWithValue("@PlusDi", plusDi);
    cmd.Parameters.AddWithValue("@MinusDi", minusDi);
    cmd.Parameters.AddWithValue("@Rsi", ctx.RSI);
    cmd.Parameters.AddWithValue("@MacdHist", ctx.MacdHist);
    cmd.Parameters.AddWithValue("@BbPosition", bbPos);
    cmd.Parameters.AddWithValue("@VolumeRatio", ctx.VolumeRatio);
    cmd.Parameters.AddWithValue("@PnL", target.Roe);
    cmd.Parameters.AddWithValue("@PnLPercent", target.Roe);
    cmd.Parameters.AddWithValue("@ComponentMix", componentMix);
    cmd.Parameters.AddWithValue("@ContextJson", contextJson);

    await cmd.ExecuteNonQueryAsync();
    Console.WriteLine("  Upserted as GOLDEN_SEED sample");
    Console.WriteLine();
}

Console.WriteLine(doInsert
    ? "✅ Golden pattern seed insert 완료"
    : "✅ Golden pattern preview 완료 (-Insert 옵션으로 실제 저장)");
'@

$insertLiteral = if ($Insert.IsPresent) { "true" } else { "false" }
$useUtcLiteral = if ($UseUtc.IsPresent) { "true" } else { "false" }
$safeConn = $ConnectionString.Replace('"','""')
$program = $program.Replace("__SYMBOL__", $Symbol)
$program = $program.Replace("__DAYS__", "$Days")
$program = $program.Replace("__LONGDATE__", $LongDate)
$program = $program.Replace("__SHORTDATE__", $ShortDate)
$program = $program.Replace("__USEUTC__", $useUtcLiteral)
$program = $program.Replace("__INSERT__", $insertLiteral)
$program = $program.Replace("__USERID__", "$UserId")
$program = $program.Replace("__CONNSTR__", $safeConn)

Set-Content -Path (Join-Path $tempDir "GoldenSeed.csproj") -Value $csproj -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Shim.cs") -Value $shim -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Program.cs") -Value $program -Encoding UTF8

Push-Location $tempDir
try {
    Write-Host "[1/2] Restoring packages..." -ForegroundColor Yellow
    dotnet restore --nologo -v q | Out-Null
    Write-Host "[2/2] Running seeder..." -ForegroundColor Yellow
    dotnet run --nologo -c Release
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
