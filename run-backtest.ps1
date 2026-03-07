<#
.SYNOPSIS
    하이브리드 전략 백테스트 실행 스크립트
.DESCRIPTION
    Binance API에서 최근 N일 데이터를 가져와 현재 주문 로직
    (HybridScorer + 컴포넌트 게이트 + HTF 페널티 + ATR 동적 임계값)을
    시뮬레이션합니다.
.PARAMETER Days
    백테스트 기간 (기본: 3일)
.PARAMETER Symbols
    백테스트 심볼 (쉼표 구분, 기본: BTCUSDT,ETHUSDT,XRPUSDT,SOLUSDT)
.EXAMPLE
    .\run-backtest.ps1
    .\run-backtest.ps1 -Days 7 -Symbols "BTCUSDT,ETHUSDT"
#>
param(
    [int]$Days = 3,
    [string]$Symbols = "BTCUSDT,ETHUSDT,XRPUSDT,SOLUSDT"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Hybrid Strategy Backtest Runner" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# 1. 빌드
Write-Host "[1/3] Building project..." -ForegroundColor Yellow
$buildOutput = dotnet build TradingBot.csproj -c Debug --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    $buildOutput | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host "  Build OK" -ForegroundColor Green

# 2. DLL 경로 확인
$dllPath = "bin\Debug\net9.0-windows\win-x64\TradingBot.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "DLL not found: $dllPath" -ForegroundColor Red
    exit 1
}

# 3. 인라인 C# 코드로 백테스트 실행
# dotnet-script가 없으므로, 임시 콘솔 프로젝트를 생성하여 실행
$tempDir = Join-Path $env:TEMP "TradingBotBacktest_$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

# 3.1 csproj
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
    <Compile Include="$($PWD.Path.Replace('\','\\'))\\HybridStrategyScorer.cs" Link="HybridStrategyScorer.cs" />
    <Compile Include="$($PWD.Path.Replace('\','\\'))\\IndicatorCalculator.cs" Link="IndicatorCalculator.cs" />
    <Compile Include="$($PWD.Path.Replace('\','\\'))\\HybridStrategyBacktester.cs" Link="HybridStrategyBacktester.cs" />
  </ItemGroup>
</Project>
"@

# BBResult + CandleData 최소 구조체 (Models.cs 전체를 참조하면 WPF 의존성 문제 발생)
$bbResultShim = @"
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
        public float MACD_Hist { get; set; }
        public float ATR { get; set; }
        public float Fib_618 { get; set; }
        public float SentimentScore { get; set; }
        public bool Label { get; set; }
    }
}
"@
Set-Content -Path (Join-Path $tempDir "BBResultShim.cs") -Value $bbResultShim -Encoding UTF8

$symbolArray = $Symbols -split "," | ForEach-Object { $_.Trim() }
$symbolsCode = ($symbolArray | ForEach-Object { "`"$_`"" }) -join ", "

# 단일 인용 here-string으로 C# $"..." 보호, 후처리로 치환
$program = @'
using TradingBot.Services.Backtest;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var symbols = new[] { __SYMBOLS__ };
int days = __DAYS__;

// A: Gate OFF
Console.WriteLine("=== [A] Gate OFF - ATR+HTF only ===");
Console.WriteLine();

var btOff = new HybridStrategyBacktester
{
    InitialBalance = 1000m, Leverage = 20m, PositionSizePercent = 0.10m,
    TakeProfitPct = 0.025m, StopLossPct = 0.010m, FeeRate = 0.0004m,
    PerfectAI = true, AdxPeriod = 14, AdxSidewaysThreshold = 17.0,
    EnableComponentGate = false
};
var resultOff = await btOff.RunMultiAsync(symbols, days: days, onLog: Console.WriteLine);

Console.WriteLine();

// B: Gate ON
Console.WriteLine("=== [B] Gate ON - AI>=30 + Adaptive Gate (R:EW3/Vol2/RSI1/BB2) ===");
Console.WriteLine();

var btOn = new HybridStrategyBacktester
{
    InitialBalance = 1000m, Leverage = 20m, PositionSizePercent = 0.10m,
    TakeProfitPct = 0.025m, StopLossPct = 0.010m, FeeRate = 0.0004m,
    PerfectAI = true, AdxPeriod = 14, AdxSidewaysThreshold = 17.0,
    EnableComponentGate = true
};
var resultOn = await btOn.RunMultiAsync(symbols, days: days, onLog: Console.WriteLine);

// Compare
Console.WriteLine();
Console.WriteLine("========== GATE COMPARISON ==========");

int offTrades = resultOff.Sum(r => r.TotalTrades);
int offWins   = resultOff.Sum(r => r.WinCount);
decimal offPnL = resultOff.Sum(r => r.TotalPnL);
decimal offWR  = offTrades > 0 ? (decimal)offWins / offTrades * 100 : 0;

int onTrades = resultOn.Sum(r => r.TotalTrades);
int onWins   = resultOn.Sum(r => r.WinCount);
int onGateRej = resultOn.Sum(r => r.GateRejections);
decimal onPnL = resultOn.Sum(r => r.TotalPnL);
decimal onWR  = onTrades > 0 ? (decimal)onWins / onTrades * 100 : 0;

Console.WriteLine($"  [A] Gate OFF: {offTrades} trades | WR {offWR:F1}% | PnL {offPnL:F2} USDT");
Console.WriteLine($"  [B] Gate ON : {onTrades} trades | WR {onWR:F1}% | PnL {onPnL:F2} USDT");
Console.WriteLine($"  Gate rejected: {onGateRej}");
Console.WriteLine($"  Filtered out : {offTrades - onTrades} trades");
if (offTrades > 0 && onTrades > 0)
{
    Console.WriteLine($"  WR change: {offWR:F1}% -> {onWR:F1}%");
    Console.WriteLine($"  PnL change: {offPnL:F2} -> {onPnL:F2} USDT");
}
Console.WriteLine("=====================================");
'@

$program = $program -replace '__SYMBOLS__', $symbolsCode -replace '__DAYS__', $Days

Set-Content -Path (Join-Path $tempDir "BacktestRunner.csproj") -Value $csproj -Encoding UTF8
Set-Content -Path (Join-Path $tempDir "Program.cs") -Value $program -Encoding UTF8

Write-Host "[2/3] Restoring packages..." -ForegroundColor Yellow
Push-Location $tempDir
try {
    $restoreOutput = dotnet restore --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Restore failed!" -ForegroundColor Red
        $restoreOutput | ForEach-Object { Write-Host $_ }
        exit 1
    }
    Write-Host "  Restore OK" -ForegroundColor Green

    Write-Host "[3/3] Running backtest ($Days days, $($symbolArray.Count) symbols)..." -ForegroundColor Yellow
    Write-Host ""
    
    dotnet run --nologo -c Release 2>&1 | ForEach-Object { Write-Host $_ }
}
finally {
    Pop-Location
    # 정리
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
