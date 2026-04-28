[Console]::OutputEncoding = [Text.Encoding]::UTF8
$p = Get-Process TradingBot -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "❌ BOT NOT RUNNING" -ForegroundColor Red; exit }

Write-Host "==== Bot Process ====" -ForegroundColor Cyan
$p | Select-Object Id, StartTime,
    @{N='RunMin';E={[int]((Get-Date)-$_.StartTime).TotalMinutes}},
    @{N='WorkMB';E={[int]($_.WorkingSet64/1MB)}},
    @{N='CPU_s';E={[int]$_.CPU}},
    @{N='Threads';E={$_.Threads.Count}},
    @{N='Resp';E={$_.Responding}} | Format-Table -AutoSize

Write-Host "==== Version ====" -ForegroundColor Cyan
$v = "$env:LOCALAPPDATA\TradingBot\current\sq.version"
if (Test-Path $v) { (Get-Content $v -Raw) -split "`n" | Where-Object { $_ -match "<version>" } | Write-Host }

Write-Host ""
Write-Host "==== Sampling CPU% over 5s ====" -ForegroundColor Cyan
$c1 = (Get-Counter "\Process(TradingBot)\% Processor Time" -ErrorAction SilentlyContinue).CounterSamples
Start-Sleep -Seconds 5
$c2 = (Get-Counter "\Process(TradingBot)\% Processor Time" -ErrorAction SilentlyContinue).CounterSamples
$cores = [Environment]::ProcessorCount
$pct = [Math]::Round($c2.CookedValue / $cores, 1)
Write-Host "  CPU%: $pct% (over $cores cores)"
