param(
    [int]$Days = 3,
    [string]$Symbols = "BTCUSDT,ETHUSDT,XRPUSDT,SOLUSDT"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  AI Model Prediction Validation" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# 1. Build
Write-Host "[1/4] Building project..." -ForegroundColor Yellow
$buildOutput = dotnet build TradingBot.csproj -c Debug --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    $buildOutput | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host "  Build OK" -ForegroundColor Green

# 2. Model file check
$modelDir = "bin\x64\Debug\net9.0-windows\win-x64"
$modelFile = Join-Path $modelDir "transformer_model.dat"
$statsFile = Join-Path $modelDir "transformer_model.stats.json"

if (-not (Test-Path $modelFile)) {
    $modelDir = "bin\Debug\net9.0-windows\win-x64"
    $modelFile = Join-Path $modelDir "transformer_model.dat"
    $statsFile = Join-Path $modelDir "transformer_model.stats.json"
}

Write-Host "[2/4] Checking model files..." -ForegroundColor Yellow
if (Test-Path $modelFile) {
    $size = [math]::Round((Get-Item $modelFile).Length / 1KB, 1)
    $date = (Get-Item $modelFile).LastWriteTime.ToString("yyyy-MM-dd HH:mm")
    Write-Host "  Model: $modelFile ($size KB, $date)" -ForegroundColor Green
} else {
    Write-Host "  Model NOT FOUND: $modelFile" -ForegroundColor Red
    exit 1
}
if (Test-Path $statsFile) {
    Write-Host "  Stats: $statsFile" -ForegroundColor Green
} else {
    Write-Host "  Stats NOT FOUND: $statsFile" -ForegroundColor Red
    exit 1
}

# 3. Temp project
$tempDir = Join-Path $env:TEMP "TradingBotAIValid_$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$pwdEsc = $PWD.Path.Replace('\','\\')
$modelPathResolved = (Resolve-Path $modelFile).Path

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Binance.Net" Version="12.8.1" />
    <PackageReference Include="Skender.Stock.Indicators" Version="2.7.1" />
    <PackageReference Include="TorchSharp" Version="0.105.0" />
    <PackageReference Include="TorchSharp-cpu" Version="0.105.0" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$pwdEsc\\HybridStrategyScorer.cs" Link="HybridStrategyScorer.cs" />
    <Compile Include="$pwdEsc\\IndicatorCalculator.cs" Link="IndicatorCalculator.cs" />
    <Compile Include="$pwdEsc\\HybridStrategyBacktester.cs" Link="HybridStrategyBacktester.cs" />
    <Compile Include="$pwdEsc\\TransformerTrainer.cs" Link="TransformerTrainer.cs" />
    <Compile Include="$pwdEsc\\TimeSeriesTransformer.cs" Link="TimeSeriesTransformer.cs" />
    <Compile Include="$pwdEsc\\TimeSeriesDataLoader.cs" Link="TimeSeriesDataLoader.cs" />
  </ItemGroup>
</Project>
"@

$shim = @'
namespace TradingBot.Models
{
    public struct BBResult { public double Upper; public double Mid; public double Lower; }

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
        public decimal ClosePrice => Close;
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
'@

$symbolArray = $Symbols -split "," | ForEach-Object { $_.Trim() }
$symbolsCode = ($symbolArray | ForEach-Object { "`"$_`"" }) -join ", "

# Write the model path as a C# constant literal
$modelPathLiteral = '@"' + $modelPathResolved + '"'

# Program.cs using string concatenation (no C# $"..." to avoid PS conflicts)
$programLines = @()
$programLines += 'using System;'
$programLines += 'using System.Collections.Generic;'
$programLines += 'using System.Linq;'
$programLines += 'using Binance.Net.Clients;'
$programLines += 'using Binance.Net.Enums;'
$programLines += 'using Binance.Net.Interfaces;'
$programLines += 'using Skender.Stock.Indicators;'
$programLines += 'using TradingBot.Models;'
$programLines += 'using TradingBot.Services.AI;'
$programLines += 'using TradingBot.Strategies;'
$programLines += ''
$programLines += 'Console.OutputEncoding = System.Text.Encoding.UTF8;'
$programLines += ''
$programLines += "var symbols = new[] { $symbolsCode };"
$programLines += "int days = $Days;"
$programLines += "string modelPath = $modelPathLiteral;"
$programLines += ''
$programLines += 'Console.WriteLine("===================================================");'
$programLines += 'Console.WriteLine("  AI Model Prediction Validation");'
$programLines += 'Console.WriteLine("===================================================");'
$programLines += 'Console.WriteLine("Model: " + modelPath);'
$programLines += 'Console.WriteLine("Symbols: " + string.Join(", ", symbols) + " | Days: " + days);'
$programLines += 'Console.WriteLine();'
$programLines += ''
$programLines += 'Console.WriteLine("[*] Loading model...");'
$programLines += 'TransformerTrainer? trainer = null;'
$programLines += 'try'
$programLines += '{'
$programLines += '    trainer = new TransformerTrainer('
$programLines += '        inputDim: 21, dModel: 128, nHeads: 8, nLayers: 4,'
$programLines += '        outputDim: 1, seqLen: 60, modelPath: modelPath);'
$programLines += '    trainer.OnLog += msg => Console.WriteLine("  " + msg);'
$programLines += '    trainer.LoadModel();'
$programLines += '    if (!trainer.IsModelReady) { Console.WriteLine("[X] Model not ready"); return; }'
$programLines += '    Console.WriteLine("[OK] Model loaded");'
$programLines += '}'
$programLines += 'catch (Exception ex) { Console.WriteLine("[X] " + ex.Message); return; }'
$programLines += 'Console.WriteLine();'
$programLines += ''
$programLines += 'var allResults = new List<(string Sym, int Total, int DirOK, int DirNG, double AvgErr, double AvgMove, List<double> AiS, int SigCnt, int LSig, int SSig)>();'
$programLines += 'using var client = new BinanceRestClient();'
$programLines += 'int seqLen = trainer.SeqLen;'
$programLines += ''
$programLines += 'foreach (var symbol in symbols)'
$programLines += '{'
$programLines += '    Console.WriteLine("======== " + symbol + " ========");'
$programLines += '    Console.WriteLine("[*] Fetching data...");'
$programLines += '    var endUtc = DateTime.UtcNow;'
$programLines += '    var startUtc = endUtc.AddDays(-days - 1);'
$programLines += '    var allKlines = new List<IBinanceKline>();'
$programLines += '    var tempStart = startUtc;'
$programLines += '    while (tempStart < endUtc)'
$programLines += '    {'
$programLines += '        var resp = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, tempStart, endUtc, 1000);'
$programLines += '        if (!resp.Success || resp.Data == null || !resp.Data.Any()) break;'
$programLines += '        allKlines.AddRange(resp.Data);'
$programLines += '        tempStart = resp.Data.Last().OpenTime.AddMinutes(5);'
$programLines += '        if (resp.Data.Count() < 1000) break;'
$programLines += '    }'
$programLines += '    await Task.Delay(300);'
$programLines += '    Console.WriteLine("  5min candles: " + allKlines.Count);'
$programLines += '    if (allKlines.Count < seqLen + 100) { Console.WriteLine("  [X] Not enough data"); continue; }'
$programLines += ''
$programLines += '    var quotes = allKlines.Select(k => new Skender.Stock.Indicators.Quote { Date = k.OpenTime, Open = k.OpenPrice, High = k.HighPrice, Low = k.LowPrice, Close = k.ClosePrice, Volume = k.Volume }).ToList();'
$programLines += '    var rsi = quotes.GetRsi(14).ToList();'
$programLines += '    var bb = quotes.GetBollingerBands(20, 2).ToList();'
$programLines += '    var macd = quotes.GetMacd(12, 26, 9).ToList();'
$programLines += '    var atr = quotes.GetAtr(14).ToList();'
$programLines += '    var s20 = quotes.GetSma(20).ToList();'
$programLines += '    var s60 = quotes.GetSma(60).ToList();'
$programLines += '    var s120 = quotes.GetSma(120).ToList();'
$programLines += ''
$programLines += '    var cdList = new List<CandleData>();'
$programLines += '    for (int i = 0; i < allKlines.Count; i++)'
$programLines += '    {'
$programLines += '        var k = allKlines[i];'
$programLines += '        cdList.Add(new CandleData {'
$programLines += '            Symbol = symbol, Open = k.OpenPrice, High = k.HighPrice, Low = k.LowPrice, Close = k.ClosePrice,'
$programLines += '            Volume = (float)k.Volume, OpenTime = k.OpenTime, CloseTime = k.CloseTime,'
$programLines += '            RSI = (float)(rsi[i].Rsi ?? 50), BollingerUpper = (float)(bb[i].UpperBand ?? (double)k.ClosePrice),'
$programLines += '            BollingerLower = (float)(bb[i].LowerBand ?? (double)k.ClosePrice),'
$programLines += '            MACD = (float)(macd[i].Macd ?? 0), MACD_Signal = (float)(macd[i].Signal ?? 0), MACD_Hist = (float)(macd[i].Histogram ?? 0),'
$programLines += '            ATR = (float)(atr[i].Atr ?? 0), SMA_20 = (float)(s20[i].Sma ?? (double)k.ClosePrice),'
$programLines += '            SMA_60 = (float)(s60[i].Sma ?? (double)k.ClosePrice), SMA_120 = (float)(s120[i].Sma ?? (double)k.ClosePrice),'
$programLines += '        });'
$programLines += '    }'
$programLines += ''
$programLines += '    int lookAhead = 10;'
$programLines += '    int si = Math.Max(seqLen, 120);'
$programLines += '    int ei = cdList.Count - lookAhead;'
$programLines += '    int total = 0, dirOK = 0, dirNG = 0;'
$programLines += '    double totErr = 0, totMove = 0;'
$programLines += '    var aiScores = new List<double>();'
$programLines += '    int sigCnt = 0, lSig = 0, sSig = 0;'
$programLines += '    var scorer = new HybridStrategyScorer();'
$programLines += ''
$programLines += '    for (int i = si; i < ei; i += 5)'
$programLines += '    {'
$programLines += '        var seq = cdList.Skip(i - seqLen).Take(seqLen).ToList();'
$programLines += '        decimal cp = cdList[i].Close;'
$programLines += '        decimal fp = cdList[i + lookAhead].Close;'
$programLines += '        decimal ac = cp > 0 ? (fp - cp) / cp : 0;'
$programLines += '        float ppf;'
$programLines += '        try { ppf = trainer.Predict(seq); } catch { continue; }'
$programLines += '        decimal pp = (decimal)ppf;'
$programLines += '        if (pp <= 0) continue;'
$programLines += '        decimal pc = cp > 0 ? (pp - cp) / cp : 0;'
$programLines += '        total++;'
$programLines += '        bool dm = (pc > 0 && ac > 0) || (pc < 0 && ac < 0);'
$programLines += '        if (dm) dirOK++; else dirNG++;'
$programLines += '        double ep = (double)Math.Abs(pc - ac) * 100;'
$programLines += '        totErr += ep;'
$programLines += '        totMove += (double)Math.Abs(ac) * 100;'
$programLines += ''
$programLines += '        var ctx = new HybridStrategyScorer.TechnicalContext {'
$programLines += '            CurrentPrice = cp, RSI = cdList[i].RSI, MacdHist = cdList[i].MACD_Hist,'
$programLines += '            MacdLine = cdList[i].MACD, MacdSignal = cdList[i].MACD_Signal,'
$programLines += '            BbUpper = cdList[i].BollingerUpper, BbLower = cdList[i].BollingerLower,'
$programLines += '            BbMid = (cdList[i].BollingerUpper + cdList[i].BollingerLower) / 2.0,'
$programLines += '            BbWidth = cdList[i].BollingerUpper > cdList[i].BollingerLower ?'
$programLines += '                (cdList[i].BollingerUpper - cdList[i].BollingerLower) / ((cdList[i].BollingerUpper + cdList[i].BollingerLower) / 2.0) * 100 : 0,'
$programLines += '            Sma20 = cdList[i].SMA_20, Sma50 = cdList[i].SMA_60, Sma200 = cdList[i].SMA_120,'
$programLines += '            IsElliottUptrend = cdList[i].SMA_20 > cdList[i].SMA_60,'
$programLines += '            VolumeRatio = 1.0, VolumeMomentum = 1.0,'
$programLines += '        };'
$programLines += '        var lr = scorer.EvaluateLong(symbol, pc, pp, ctx);'
$programLines += '        var sr = scorer.EvaluateShort(symbol, pc, pp, ctx);'
$programLines += '        double bs = Math.Max(lr.FinalScore, sr.FinalScore);'
$programLines += '        string bd = lr.FinalScore > sr.FinalScore ? "LONG" : "SHORT";'
$programLines += '        double bai = Math.Max(lr.AiPredictionScore, sr.AiPredictionScore);'
$programLines += '        aiScores.Add(bai);'
$programLines += '        if (bs >= 65) { sigCnt++; if (bd == "LONG") lSig++; else sSig++; }'
$programLines += ''
$programLines += '        if (total <= 30)'
$programLines += '        {'
$programLines += '            string ar = dm ? "[O]" : "[X]";'
$programLines += '            Console.WriteLine("  " + ar + " " + cdList[i].OpenTime.ToString("MM/dd HH:mm")'
$programLines += '                + " | Now:" + cp.ToString("F2") + " | Pred:" + pp.ToString("F2") + "(" + ((double)pc*100).ToString("+0.00;-0.00") + "%)"'
$programLines += '                + " | Real:" + fp.ToString("F2") + "(" + ((double)ac*100).ToString("+0.00;-0.00") + "%)"'
$programLines += '                + " | AI:" + bai.ToString("F1") + "/40 | Score:" + bs.ToString("F1"));'
$programLines += '        }'
$programLines += '    }'
$programLines += ''
$programLines += '    Console.WriteLine();'
$programLines += '    Console.WriteLine("  --- " + symbol + " Results ---");'
$programLines += '    Console.WriteLine("  Predictions: " + total);'
$programLines += '    double da = total > 0 ? (double)dirOK / total * 100 : 0;'
$programLines += '    Console.WriteLine("  Direction accuracy: " + dirOK + "/" + total + " (" + da.ToString("F1") + "%)");'
$programLines += '    Console.WriteLine("  Avg error: " + (total > 0 ? totErr / total : 0).ToString("F3") + "%");'
$programLines += '    Console.WriteLine("  Avg actual move: " + (total > 0 ? totMove / total : 0).ToString("F3") + "%");'
$programLines += '    if (aiScores.Count > 0)'
$programLines += '    {'
$programLines += '        Console.WriteLine("  AI Score: Avg=" + aiScores.Average().ToString("F1") + "/40 Max=" + aiScores.Max().ToString("F1") + " Min=" + aiScores.Min().ToString("F1"));'
$programLines += '        Console.WriteLine("    >=35(entry): " + aiScores.Count(s => s >= 35) + " (" + (aiScores.Count(s => s >= 35) * 100.0 / aiScores.Count).ToString("F1") + "%)");'
$programLines += '        Console.WriteLine("    >=38(strong): " + aiScores.Count(s => s >= 38) + " (" + (aiScores.Count(s => s >= 38) * 100.0 / aiScores.Count).ToString("F1") + "%)");'
$programLines += '        Console.WriteLine("    25~35(weak): " + aiScores.Count(s => s >= 25 && s < 35) + " (" + (aiScores.Count(s => s >= 25 && s < 35) * 100.0 / aiScores.Count).ToString("F1") + "%)");'
$programLines += '        Console.WriteLine("    <25(unfit): " + aiScores.Count(s => s < 25) + " (" + (aiScores.Count(s => s < 25) * 100.0 / aiScores.Count).ToString("F1") + "%)");'
$programLines += '    }'
$programLines += '    Console.WriteLine("  Entry signals(>=65pt): " + sigCnt + " (L:" + lSig + " S:" + sSig + ")");'
$programLines += '    Console.WriteLine();'
$programLines += '    allResults.Add((symbol, total, dirOK, dirNG, total > 0 ? totErr / total : 0, total > 0 ? totMove / total : 0, aiScores, sigCnt, lSig, sSig));'
$programLines += '}'
$programLines += ''
$programLines += 'Console.WriteLine("===================================================");'
$programLines += 'Console.WriteLine("  GRAND AI PREDICTION ANALYSIS");'
$programLines += 'Console.WriteLine("===================================================");'
$programLines += 'int gT = allResults.Sum(r => r.Total);'
$programLines += 'int gC = allResults.Sum(r => r.DirOK);'
$programLines += 'var gS = allResults.SelectMany(r => r.AiS).ToList();'
$programLines += 'double oA = gT > 0 ? (double)gC / gT * 100 : 0;'
$programLines += 'Console.WriteLine("  Total: " + gT + " predictions (" + days + " days, " + symbols.Length + " symbols)");'
$programLines += 'Console.WriteLine("  Direction accuracy: " + gC + "/" + gT + " (" + oA.ToString("F1") + "%)");'
$programLines += 'Console.WriteLine("  " + (oA >= 55 ? "[OK] Above random(50%)" : "[!] Near random level"));'
$programLines += 'Console.WriteLine();'
$programLines += 'if (gS.Count > 0)'
$programLines += '{'
$programLines += '    Console.WriteLine("  AI Score distribution (all):");'
$programLines += '    Console.WriteLine("    Avg: " + gS.Average().ToString("F1") + "/40");'
$programLines += '    Console.WriteLine("    >=35: " + (gS.Count(s => s >= 35) * 100.0 / gS.Count).ToString("F1") + "%");'
$programLines += '    Console.WriteLine("    >=38: " + (gS.Count(s => s >= 38) * 100.0 / gS.Count).ToString("F1") + "%");'
$programLines += '    Console.WriteLine("    25~35: " + (gS.Count(s => s >= 25 && s < 35) * 100.0 / gS.Count).ToString("F1") + "%");'
$programLines += '    Console.WriteLine("    <25: " + (gS.Count(s => s < 25) * 100.0 / gS.Count).ToString("F1") + "%");'
$programLines += '}'
$programLines += 'Console.WriteLine();'
$programLines += 'Console.WriteLine("  Per-symbol:");'
$programLines += 'foreach (var r in allResults.OrderByDescending(r => r.DirOK * 1.0 / Math.Max(1, r.Total)))'
$programLines += '{'
$programLines += '    double wr = r.Total > 0 ? (double)r.DirOK / r.Total * 100 : 0;'
$programLines += '    Console.WriteLine("    " + r.Sym.PadRight(10) + " | DirAcc:" + wr.ToString("F1") + "% | AvgErr:" + r.AvgErr.ToString("F3") + "% | Sig:" + r.SigCnt + "(L:" + r.LSig + "/S:" + r.SSig + ")");'
$programLines += '}'
$programLines += 'Console.WriteLine();'
$programLines += 'Console.WriteLine("===================================================");'
$programLines += 'double avgAi = gS.Count > 0 ? gS.Average() : 0;'
$programLines += 'double p35 = gS.Count > 0 ? gS.Count(s => s >= 35) * 100.0 / gS.Count : 0;'
$programLines += 'if (oA >= 60 && p35 >= 30) Console.WriteLine("  [PASS] AI model GOOD - DirAcc 60%+, entries 30%+");'
$programLines += 'else if (oA >= 55 && p35 >= 15) Console.WriteLine("  [FAIR] AI model OK - Some predictive power");'
$programLines += 'else if (oA >= 50) Console.WriteLine("  [WEAK] AI model POOR - Near random");'
$programLines += 'else Console.WriteLine("  [FAIL] AI model INVERSE - Retrain needed");'
$programLines += 'Console.WriteLine("  (DirAcc:" + oA.ToString("F1") + "% AvgAI:" + avgAi.ToString("F1") + "/40 EntryPct:" + p35.ToString("F1") + "%)");'
$programLines += 'Console.WriteLine("===================================================");'
$programLines += 'trainer?.Dispose();'

$program = $programLines -join "`n"

Set-Content -Path (Join-Path $tempDir "AIValidation.csproj") -Value $csproj -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Shim.cs") -Value $shim -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Program.cs") -Value $program -Encoding UTF8

Write-Host "[3/4] Restoring packages (TorchSharp)..." -ForegroundColor Yellow
Push-Location $tempDir
try {
    $restoreOutput = dotnet restore --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Restore failed!" -ForegroundColor Red
        $restoreOutput | ForEach-Object { Write-Host $_ }
        exit 1
    }
    Write-Host "  Restore OK" -ForegroundColor Green

    Write-Host "[4/4] Running AI validation ($Days days, $($symbolArray.Count) symbols)..." -ForegroundColor Yellow
    Write-Host ""

    dotnet run --nologo -c Release 2>&1 | ForEach-Object { Write-Host $_ }
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
