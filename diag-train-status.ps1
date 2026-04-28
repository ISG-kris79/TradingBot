$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== 1. Models folder ===="
$modelDir = "$env:LOCALAPPDATA\TradingBot\Models"
if (Test-Path $modelDir) {
    Get-ChildItem $modelDir -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Host "  NO Models folder!"
}

Write-Host ""
Write-Host "==== 2. Velopack log (last 80) ===="
$vlog = "$env:LOCALAPPDATA\TradingBot\velopack.log"
if (Test-Path $vlog) {
    Get-Content $vlog -Tail 80 | Out-String -Width 250 | Write-Host
} else {
    Write-Host "  NO velopack.log"
}

Write-Host ""
Write-Host "==== 3. Currently running TradingBot processes ===="
Get-Process TradingBot, Update -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, StartTime, Path | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== 4. Installed version (sq.version) ===="
$verPath = "$env:LOCALAPPDATA\TradingBot\current\sq.version"
if (Test-Path $verPath) {
    Get-Content $verPath -Raw | Write-Host
} else {
    Write-Host "  NO sq.version"
}

Write-Host ""
Write-Host "==== 5. Bot logs (today, last 60 lines) ===="
$logCandidates = @(
    "$env:LOCALAPPDATA\TradingBot\Logs",
    "$env:LOCALAPPDATA\TradingBot\current\Logs",
    "$env:LOCALAPPDATA\TradingBot\current",
    "$env:APPDATA\TradingBot"
)
foreach ($p in $logCandidates) {
    if (Test-Path $p) {
        $files = Get-ChildItem $p -Recurse -File -Include *.log,*.txt -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 5
        foreach ($f in $files) {
            Write-Host "--- $($f.FullName) (last 30) ---"
            Get-Content $f.FullName -Tail 30 -ErrorAction SilentlyContinue | Out-String -Width 250 | Write-Host
        }
    }
}

Write-Host ""
Write-Host "==== 6. Crash dump / first chance error ===="
$dumpDir = "$env:LOCALAPPDATA\TradingBot\current"
if (Test-Path $dumpDir) {
    $crashFiles = Get-ChildItem $dumpDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*ERROR*" -or $_.Name -like "*CRASH*" -or $_.Name -like "*FIRST_CHANCE*" }
    foreach ($cf in $crashFiles) {
        Write-Host "--- $($cf.Name) ---"
        Get-Content $cf.FullName -Tail 30 | Out-String -Width 250 | Write-Host
    }
}
