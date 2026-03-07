param(
    [string]$Symbol = "XRPUSDT",
    [int]$Days = 2,
    [string]$Date = "2026-03-06",
    [string]$Targets = "02:30,05:30,20:15",
    [switch]$UseUtc
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  XRP Point Check (Hybrid Logic)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

$tempDir = Join-Path $env:TEMP "TradingBotPointCheck_$(Get-Random)"
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

$program = @"
using System.Reflection;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using TradingBot.Services;
using TradingBot.Services.Backtest;
using TradingBot.Strategies;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string symbol = "__SYMBOL__";
int days = __DAYS__;
string baseDate = "__DATE__";
string targetsCsv = "__TARGETS__";
bool useUtc = __USEUTC__;

DateTime ParseTarget(string hhmm)
{
    var dt = DateTime.Parse(baseDate + " " + hhmm + ":00");
    return useUtc
        ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        : dt.ToUniversalTime();
}

var targetsUtc = targetsCsv
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(ParseTarget)
    .ToList();

if (!targetsUtc.Any())
{
    Console.WriteLine("No target times provided.");
    return;
}

Console.WriteLine("Symbol: " + symbol + " | Days: " + days);
Console.WriteLine("Target times mode: " + (useUtc ? "UTC as-is" : "LOCAL -> UTC"));
Console.WriteLine("Target times (UTC):");
foreach (var t in targetsUtc) Console.WriteLine("  - " + t.ToString("yyyy-MM-dd HH:mm") + " UTC");
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
var miBuildContext = type.GetMethod("BuildContext", BindingFlags.NonPublic | BindingFlags.Instance);
var miCalcDyn = type.GetMethod("CalculateDynamicThreshold", BindingFlags.NonPublic | BindingFlags.Instance);
var miCalcHtf = type.GetMethod("CalculateHTFPenaltyLocal", BindingFlags.NonPublic | BindingFlags.Instance);

if (miBuildContext == null || miCalcDyn == null || miCalcHtf == null)
{
    Console.WriteLine("Failed to access private methods via reflection");
    return;
}

using var client = new BinanceRestClient();
var endUtc = DateTime.UtcNow;
var startUtc = endUtc.AddDays(-days);

async Task<List<IBinanceKline>> Fetch(Binance.Net.Enums.KlineInterval interval)
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
    return list;
}

var klines5m = await Fetch(KlineInterval.FiveMinutes);
var klines15m = await Fetch(KlineInterval.FifteenMinutes);
var klines1h = await Fetch(KlineInterval.OneHour);

Console.WriteLine("5m candles: " + klines5m.Count + " | range: " + klines5m.First().OpenTime.ToString("MM-dd HH:mm") + " ~ " + klines5m.Last().OpenTime.ToString("MM-dd HH:mm") + " UTC");
Console.WriteLine();

var scorer = new HybridStrategyScorer();

foreach (var targetUtc in targetsUtc)
{
    Console.WriteLine("================================================");
    Console.WriteLine("Target: " + targetUtc.ToString("yyyy-MM-dd HH:mm") + " UTC");

    int i = klines5m.FindIndex(k => k.OpenTime == targetUtc);
    if (i < 0)
    {
        // nearest candle
        i = klines5m.Select((k, idx) => (idx, diff: Math.Abs((k.OpenTime - targetUtc).TotalMinutes)))
                    .OrderBy(x => x.diff)
                    .First().idx;
        Console.WriteLine("Exact candle not found. Nearest: " + klines5m[i].OpenTime.ToString("yyyy-MM-dd HH:mm") + " UTC");
    }

    if (i < 240 || i >= klines5m.Count - bt.FutureLookAhead)
    {
        Console.WriteLine("Insufficient index range for warmup/lookahead. index=" + i);
        continue;
    }

    var currentKlines = klines5m.GetRange(0, i + 1);
    var current = klines5m[i];
    decimal currentPrice = current.ClosePrice;

    double atr = IndicatorCalculator.CalculateATR(currentKlines, 14);
    double dynamicThreshold = (double)miCalcDyn.Invoke(bt, new object[] { currentPrice, atr })!;

    var adxTuple = IndicatorCalculator.CalculateADX(currentKlines, bt.AdxPeriod);
    double adx = adxTuple.Item1;
    double plusDi = adxTuple.Item2;
    double minusDi = adxTuple.Item3;
    bool isSideways = adx < bt.AdxSidewaysThreshold;

    var ctx = (HybridStrategyScorer.TechnicalContext)miBuildContext.Invoke(bt, new object[] { currentKlines, currentPrice })!;

    decimal futurePrice = klines5m[i + bt.FutureLookAhead].ClosePrice;
    decimal predictedChange = currentPrice > 0 ? (futurePrice - currentPrice) / currentPrice : 0;
    decimal predictedPrice = currentPrice * (1 + predictedChange);

    var longResult = scorer.EvaluateLong(symbol, predictedChange, predictedPrice, ctx);
    var shortResult = scorer.EvaluateShort(symbol, predictedChange, predictedPrice, ctx);

    if (HybridStrategyScorer.IsMajorCoin(symbol))
    {
        double longBonus = HybridStrategyScorer.GetMajorCoinBonus(symbol, "LONG", ctx);
        double shortBonus = HybridStrategyScorer.GetMajorCoinBonus(symbol, "SHORT", ctx);
        longResult.FinalScore = Math.Clamp(longResult.FinalScore + longBonus, 0, 100);
        shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + shortBonus, 0, 100);
    }

    string candidateDir = longResult.FinalScore > shortResult.FinalScore ? "LONG" : "SHORT";
    int rangeStart = Math.Max(0, i - 29);
    int rangeCount = Math.Min(30, i + 1);
    var recent30 = klines5m.GetRange(rangeStart, rangeCount);
    double htfPenalty = (double)miCalcHtf.Invoke(bt, new object[] { symbol, currentPrice, candidateDir, current.OpenTime, klines15m, klines1h, recent30 })!;

    longResult.FinalScore = Math.Clamp(longResult.FinalScore + htfPenalty, 0, 100);
    shortResult.FinalScore = Math.Clamp(shortResult.FinalScore + htfPenalty, 0, 100);

    bool longStrongOverride = longResult.FinalScore >= (dynamicThreshold + 3.0) && predictedChange >= 0.0035m;
    bool shortStrongOverride = shortResult.FinalScore >= (dynamicThreshold + 3.0) && predictedChange <= -0.0035m;
    bool xrpTrendLongOverride =
        symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase) &&
        predictedChange >= 0.0035m &&
        longResult.AiPredictionScore >= 30.0 &&
        longResult.BollingerScore >= 8.0 &&
        longResult.RsiMacdScore >= 5.0 &&
        longResult.FinalScore >= 45.0 &&
        (longResult.FinalScore - shortResult.FinalScore) >= 12.0;

    bool safeToLong = plusDi >= minusDi || longStrongOverride || xrpTrendLongOverride;
    bool safeToShort = minusDi > plusDi || shortStrongOverride;

    bool longThresholdPassed = longResult.FinalScore >= dynamicThreshold || xrpTrendLongOverride;
    bool longPrimary = longThresholdPassed && longResult.FinalScore > shortResult.FinalScore && safeToLong;
    bool shortPrimary = shortResult.FinalScore >= dynamicThreshold && shortResult.FinalScore > longResult.FinalScore && safeToShort;

    bool longGatePass = longResult.PassesComponentGate(out var longGateFail);
    bool shortGatePass = shortResult.PassesComponentGate(out var shortGateFail);
    bool longGateEffective = longGatePass || xrpTrendLongOverride;

    bool isXrpSymbol = symbol.StartsWith("XRP", StringComparison.OrdinalIgnoreCase);
    double bbRange = ctx.BbUpper - ctx.BbLower;
    double bbPos = bbRange > 0 ? ((double)currentPrice - ctx.BbLower) / bbRange : 0.5;
    bbPos = Math.Clamp(bbPos, 0.0, 1.0);
    bool xrpLongRelaxed = false;
    bool xrpShortRelaxed = false;
    bool xrpLongRelaxedLegacy = false;
    bool xrpShortRelaxedLegacy = false;

    bool sideLong =
        currentPrice <= (decimal)ctx.BbLower * 1.001m &&
        ctx.RSI <= 35.0 &&
        predictedChange > 0 &&
        ctx.VolumeRatio < 1.5;

    bool sideShort =
        currentPrice >= (decimal)ctx.BbUpper * 0.999m &&
        ctx.RSI >= 65.0 &&
        predictedChange < 0 &&
        ctx.VolumeRatio < 1.5;

    if (isXrpSymbol)
    {
        xrpLongRelaxedLegacy =
            predictedChange >= 0.001m &&
            ctx.RSI <= 55.0 &&
            currentPrice <= (decimal)ctx.BbMid * 1.003m &&
            ctx.VolumeRatio < 1.8;

        xrpLongRelaxed =
            xrpLongRelaxedLegacy &&
            bbPos <= 0.45 &&
            ctx.VolumeRatio < 1.8;

        xrpShortRelaxedLegacy =
            predictedChange <= -0.001m &&
            ctx.RSI >= 35.0 &&
            currentPrice >= (decimal)ctx.BbMid * 0.997m &&
            ctx.VolumeRatio < 1.8;

        xrpShortRelaxed =
            xrpShortRelaxedLegacy &&
            bbPos >= 0.55 &&
            ctx.VolumeRatio < 1.8;

        sideLong = sideLong || xrpLongRelaxed;
        sideShort = sideShort || xrpShortRelaxed;
    }

    string finalDecision = "WAIT";
    if (isSideways)
    {
        if (sideLong) finalDecision = "LONG";
        else if (sideShort) finalDecision = "SHORT";
    }
    else
    {
        if (longPrimary && longGateEffective) finalDecision = "LONG";
        else if (shortPrimary && shortGatePass) finalDecision = "SHORT";
    }

    Console.WriteLine("Candle: " + current.OpenTime.ToString("MM-dd HH:mm") + " UTC | Price:" + currentPrice.ToString("F6"));
    Console.WriteLine("PredictedChange(PerfectAI): " + ((double)predictedChange * 100).ToString("+0.000;-0.000") + "% | Future(50m): " + futurePrice.ToString("F6"));
    Console.WriteLine("ADX: " + adx.ToString("F1") + " (+DI:" + plusDi.ToString("F1") + " / -DI:" + minusDi.ToString("F1") + ") | Sideways:" + isSideways);
    Console.WriteLine("Threshold: " + dynamicThreshold.ToString("F0") + " | HTF penalty: " + htfPenalty.ToString("F0"));

    Console.WriteLine("LONG  Score=" + longResult.FinalScore.ToString("F1") + " | AI:" + longResult.AiPredictionScore.ToString("F0") + " EW:" + longResult.ElliottWaveScore.ToString("F0") + " Vol:" + longResult.VolumeMomentumScore.ToString("F0") + " RSI:" + longResult.RsiMacdScore.ToString("F0") + " BB:" + longResult.BollingerScore.ToString("F0"));
    Console.WriteLine("SHORT Score=" + shortResult.FinalScore.ToString("F1") + " | AI:" + shortResult.AiPredictionScore.ToString("F0") + " EW:" + shortResult.ElliottWaveScore.ToString("F0") + " Vol:" + shortResult.VolumeMomentumScore.ToString("F0") + " RSI:" + shortResult.RsiMacdScore.ToString("F0") + " BB:" + shortResult.BollingerScore.ToString("F0"));

    Console.WriteLine("Primary pass -> LONG:" + longPrimary + " / SHORT:" + shortPrimary);
    Console.WriteLine("Sideways pass -> LONG:" + sideLong + " / SHORT:" + sideShort);
    Console.WriteLine("BB pos: " + (bbPos * 100).ToString("F1") + "% (0%=Lower, 100%=Upper)");
    Console.WriteLine("XRP relaxed  -> LONG:" + xrpLongRelaxed + " / SHORT:" + xrpShortRelaxed +
                      " | RSI:" + ctx.RSI.ToString("F1") + " Vol:" + ctx.VolumeRatio.ToString("F2") + "x");
    Console.WriteLine("Legacy(no bbPos) -> LONG:" + xrpLongRelaxedLegacy + " / SHORT:" + xrpShortRelaxedLegacy);
    Console.WriteLine("XRP trend override: " + xrpTrendLongOverride);
    Console.WriteLine("Gate pass    -> LONG:" + longGatePass + (longGatePass ? "" : " (" + longGateFail + ")") + " / SHORT:" + shortGatePass + (shortGatePass ? "" : " (" + shortGateFail + ")"));
    Console.WriteLine("FINAL DECISION: " + finalDecision);
}
"@

$useUtcLiteral = if ($UseUtc.IsPresent) { "true" } else { "false" }
$program = $program.Replace("__SYMBOL__", $Symbol).Replace("__DAYS__", "$Days").Replace("__DATE__", $Date).Replace("__TARGETS__", $Targets).Replace("__USEUTC__", $useUtcLiteral)

Set-Content -Path (Join-Path $tempDir "PointCheck.csproj") -Value $csproj -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Shim.cs") -Value $shim -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Program.cs") -Value $program -Encoding UTF8

Push-Location $tempDir
try {
    dotnet restore --nologo -v q | Out-Null
    dotnet run --nologo -c Release
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
