# Windows Task Scheduler installation for monitor-auto.ps1 (Tier A backup)
# Run as Administrator. Registers a 6-hour recurring task.

$taskName = "TradingBot-Monitor-Tier-A"
$scriptPath = Join-Path $PSScriptRoot "monitor-auto.ps1"
$logPath = Join-Path $PSScriptRoot "monitor-task.log"

if (-not (Test-Path $scriptPath)) {
    Write-Host "ERROR: monitor-auto.ps1 not found at $scriptPath" -ForegroundColor Red
    exit 1
}

# Trigger: every 6h, starting today at the next :17 minute mark
$now = Get-Date
$startAt = $now.Date.AddHours($now.Hour).AddMinutes(17)
if ($startAt -le $now) { $startAt = $startAt.AddHours(1) }

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NonInteractive -ExecutionPolicy Bypass -File `"$scriptPath`" *>> `"$logPath`""

$trigger = New-ScheduledTaskTrigger `
    -Once -At $startAt `
    -RepetitionInterval (New-TimeSpan -Hours 6)

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RunOnlyIfNetworkAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Limited

# Remove existing if present
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing task: $taskName" -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "TradingBot v5.10.66+ Tier A monitoring (6-hourly health check, logs to monitor-task.log)" | Out-Null

Write-Host "Task registered successfully:" -ForegroundColor Green
Get-ScheduledTask -TaskName $taskName | Select-Object TaskName, State, @{N="NextRun";E={(Get-ScheduledTaskInfo -TaskName $_.TaskName).NextRunTime}} | Format-List

Write-Host ""
Write-Host "Manual test command:" -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$taskName'"
Write-Host "Log file:" -ForegroundColor Cyan
Write-Host "  $logPath"
Write-Host ""
Write-Host "Uninstall command:" -ForegroundColor DarkGray
Write-Host "  Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false"
