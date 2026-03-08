# Quick AI Model Training Script
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  AI Model Status Check" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

$binPath = "..\bin\Debug\net9.0-windows"
if (-not (Test-Path $binPath)) {
    $binPath = "..\bin\Debug\net9.0-windows\win-x64"
}

Write-Host "Checking folder: $binPath" -ForegroundColor Green
Write-Host ""

# Check model files
$modelFiles = @(
    "EntryTimingModel.zip",
    "scalping_model.zip", 
    "model.zip"
)

$existingModels = @()
foreach ($modelFile in $modelFiles) {
    $fullPath = Join-Path $binPath $modelFile
    if (Test-Path $fullPath) {
        $sizeKB = [math]::Round((Get-Item $fullPath).Length / 1KB, 0)
        Write-Host "[OK] $modelFile ($sizeKB KB)" -ForegroundColor Green
        $existingModels += $modelFile
    } else {
        Write-Host "[MISSING] $modelFile" -ForegroundColor Red
    }
}

Write-Host ""

if ($existingModels.Count -eq 0) {
    Write-Host "================================================" -ForegroundColor Yellow
    Write-Host "  No AI Models Found" -ForegroundColor Yellow
    Write-Host "================================================" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Current Status:" -ForegroundColor Cyan
    Write-Host "  - AI Double-Check Gate: DISABLED" -ForegroundColor Red
    Write-Host "  - Traditional Strategy: ENABLED" -ForegroundColor Green
    Write-Host "  - Data Collection: AUTO RUNNING" -ForegroundColor Green
    Write-Host ""
    Write-Host "This is NORMAL! The bot will:" -ForegroundColor White
    Write-Host "  1. Trade using WaveGate strategy (technical indicators)" -ForegroundColor White
    Write-Host "  2. Collect data automatically in background" -ForegroundColor White
    Write-Host "  3. AI features can be enabled later after training" -ForegroundColor White
    Write-Host ""
    Write-Host "Recommendation: Just use it!" -ForegroundColor Green
    Write-Host "  -> Run for 1-2 weeks to collect data" -ForegroundColor Cyan
    Write-Host "  -> Then train AI models with real data" -ForegroundColor Cyan
} else {
    Write-Host "[SUCCESS] Found $($existingModels.Count) model(s)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Restart TradingBot to activate AI features!" -ForegroundColor Cyan
}

Write-Host ""
