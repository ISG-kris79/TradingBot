param(
    [int]$Days = 3,
    [string]$Symbols = "BTCUSDT,ETHUSDT,XRPUSDT,SOLUSDT"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  AI Runtime Validation (In-Process)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "[1/3] Building project..." -ForegroundColor Yellow
$buildOutput = dotnet build TradingBot.csproj -c Debug --no-incremental --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    $buildOutput | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host "  Build OK" -ForegroundColor Green

Write-Host "[2/3] Checking required runtime files..." -ForegroundColor Yellow
$requiredFiles = @(
    "AIPredictor.cs",
    "TensorFlowTransformer.cs",
    "TensorFlowEntryTimingTrainer.cs",
    "AIDoubleCheckEntryGate.cs"
)

$missing = @()
foreach ($file in $requiredFiles) {
    if (-not (Test-Path $file)) {
        $missing += $file
    }
}

if ($missing.Count -gt 0) {
    Write-Host "  Missing files:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "   - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "  Runtime file check OK" -ForegroundColor Green

Write-Host "[3/3] Legacy external service check..." -ForegroundColor Yellow
$legacyDirs = @(
    "..\TradingBot.MLService",
    "..\TradingBot.TorchService"
)

$foundLegacy = @()
foreach ($dir in $legacyDirs) {
    if (Test-Path $dir) {
        $foundLegacy += $dir
    }
}

if ($foundLegacy.Count -gt 0) {
    Write-Host "  Legacy external-service directories found:" -ForegroundColor Yellow
    $foundLegacy | ForEach-Object { Write-Host "   - $_" -ForegroundColor Yellow }
    Write-Host "  (삭제 권장: 외부 서비스 아키텍처는 사용하지 않음)" -ForegroundColor Yellow
} else {
    Write-Host "  No legacy external-service directories found" -ForegroundColor Green
}

Write-Host ""
Write-Host "Validation complete." -ForegroundColor Green
Write-Host "- Symbols input: $Symbols" -ForegroundColor Gray
Write-Host "- Days input: $Days (현재 스크립트에서 정보성 출력만 사용)" -ForegroundColor Gray
