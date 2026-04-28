[Console]::OutputEncoding = [Text.Encoding]::UTF8
$p = Get-Process TradingBot -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "Bot not running"; exit }

Write-Host "==== Bot snapshot ====" -ForegroundColor Cyan
$p | Select-Object Id,
    @{N='CPU_s';E={[int]$_.CPU}},
    @{N='WorkMB';E={[int]($_.WorkingSet64/1MB)}},
    @{N='PrivMB';E={[int]($_.PrivateMemorySize64/1MB)}},
    @{N='Threads';E={$_.Threads.Count}},
    @{N='Handles';E={$_.HandleCount}},
    @{N='RunMin';E={[int]((Get-Date) - $_.StartTime).TotalMinutes}} | Format-Table -AutoSize

Write-Host ""
Write-Host "==== Sampling CPU% over 5 seconds ===="
$c1 = (Get-Counter "\Process(TradingBot)\% Processor Time" -ErrorAction SilentlyContinue).CounterSamples
Start-Sleep -Seconds 5
$c2 = (Get-Counter "\Process(TradingBot)\% Processor Time" -ErrorAction SilentlyContinue).CounterSamples
$cores = [Environment]::ProcessorCount
Write-Host "  Logical cores: $cores"
Write-Host "  Sample1 raw: $($c1.CookedValue)  Sample2 raw: $($c2.CookedValue)"
$pct = [Math]::Round($c2.CookedValue / $cores, 1)
Write-Host "  Estimated CPU%: $pct% (normalized over $cores cores)"

Write-Host ""
Write-Host "==== Top 12 threads by CPU time ===="
$p.Threads | Sort-Object TotalProcessorTime -Descending | Select-Object -First 12 |
    Select-Object Id,
        @{N='CPU_s';E={[int]$_.TotalProcessorTime.TotalSeconds}},
        @{N='State';E={$_.ThreadState}},
        @{N='Wait';E={$_.WaitReason}},
        @{N='Prio';E={$_.PriorityLevel}},
        @{N='StartAddr';E={'0x{0:X}' -f $_.StartAddress.ToInt64()}} | Format-Table -AutoSize

Write-Host ""
Write-Host "==== GC / managed heap (best-effort via .NET counters) ===="
try {
    $proc = $p.Id
    $job = Start-Job -ScriptBlock { param($pid2) & dotnet-counters monitor --process-id $pid2 --counters System.Runtime --refresh-interval 1 } -ArgumentList $proc
    Start-Sleep -Seconds 6
    Receive-Job $job 2>&1 | Select-Object -First 50
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -ErrorAction SilentlyContinue
} catch {
    Write-Host "  dotnet-counters not available: $($_.Exception.Message)"
}
