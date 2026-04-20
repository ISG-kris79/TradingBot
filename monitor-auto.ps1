# [Tier A Monitoring] 6h auto-run, save JSON
function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [System.Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
# ExecuteScalar wrapper (PS5 SqlDataAdapter compat issues)
function QScalar($sql, $default=0) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $val = $cm.ExecuteScalar()
    $cn.Close()
    if ($null -eq $val -or $val -is [DBNull]) { return $default }
    return $val
}

$result = [ordered]@{
    timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    version = "5.10.66"
    status = "OK"
    alerts = @()
    metrics = @{}
}

# [1] PnL 6h - 3 separate scalar queries
$result.metrics.pnl_6h = [double](QScalar "SELECT ROUND(ISNULL(SUM(PnL),0), 2) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-6,GETDATE()) AND IsClosed=1")
$result.metrics.trades_6h = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-6,GETDATE()) AND IsClosed=1")
$wins6h = [int](QScalar "SELECT SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-6,GETDATE()) AND IsClosed=1")
$result.metrics.winrate_6h = if ($result.metrics.trades_6h -gt 0) { [math]::Round($wins6h / $result.metrics.trades_6h, 2) } else { 0 }
if ($result.metrics.pnl_6h -lt -300) { $result.alerts += ("ALERT: 6h loss exceeds threshold (-" + [math]::Abs($result.metrics.pnl_6h) + " < -300)") }

# [2] PnL 24h
$result.metrics.pnl_24h = [double](QScalar "SELECT ROUND(ISNULL(SUM(PnL),0), 2) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-24,GETDATE()) AND IsClosed=1")
$result.metrics.trades_24h = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-24,GETDATE()) AND IsClosed=1")
if ($result.metrics.pnl_24h -lt -500) { $result.alerts += ("ALERT: 24h loss exceeds threshold (-" + [math]::Abs($result.metrics.pnl_24h) + " < -500)") }

# [3] Major block (v5.10.66 core)
$result.metrics.major_blocked_24h = [int](QScalar "SELECT COUNT(*) FROM FooterLogs WITH (NOLOCK) WHERE Timestamp>=DATEADD(hour,-24,GETDATE()) AND Message LIKE '%MAJOR_BLOCKED_v5_10_66%'")
$result.metrics.major_entries_24h = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-24,GETDATE()) AND (Symbol LIKE 'BTC%' OR Symbol LIKE 'ETH%' OR Symbol LIKE 'SOL%' OR Symbol LIKE 'XRP%')")
if ($result.metrics.major_entries_24h -gt 0) { $result.alerts += ("WARN: Major entries " + $result.metrics.major_entries_24h + " (BlockMajorEntries malfunction suspected)") }

# [4] Online learning logs
$result.metrics.online_logs_24h = [int](QScalar "SELECT COUNT(*) FROM FooterLogs WITH (NOLOCK) WHERE Timestamp>=DATEADD(hour,-24,GETDATE()) AND Message LIKE '%[OnlineLearning]%'")
if ($result.metrics.online_logs_24h -eq 0) { $result.alerts += "WARN: OnlineLearning 0 logs (service inactive)" }

# [5] Online retrain
$result.metrics.online_retrain_24h = [int](QScalar "SELECT COUNT(*) FROM AiTrainingRuns WITH (NOLOCK) WHERE Stage='Online_Retrain' AND CompletedAtUtc>=DATEADD(hour,-24,GETUTCDATE())")

# [6] Label rate 24h
$labelN = [int](QScalar "SELECT COUNT(*) FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc>=DATEADD(hour,-24,GETUTCDATE())")
$labelSuccess = [int](QScalar "SELECT SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END) FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc>=DATEADD(hour,-24,GETUTCDATE())")
$result.metrics.label_n_24h = $labelN
$result.metrics.label_success_rate_24h = if ($labelN -gt 0) { [math]::Round($labelSuccess / $labelN, 2) } else { 0 }

# [7] Bot alive (most critical)
$lastTimeRaw = QScalar "SELECT TOP 1 Timestamp FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC" $null
if ($null -eq $lastTimeRaw) {
    $result.metrics.minutes_since_last_log = -1
    $result.alerts += "CRITICAL: Bot down, no logs found in DB"
    $result.status = "DOWN"
} else {
    $lastTime = [DateTime]$lastTimeRaw
    $minutesSinceLog = [int]((Get-Date) - $lastTime).TotalMinutes
    $result.metrics.minutes_since_last_log = $minutesSinceLog
    if ($minutesSinceLog -gt 15) {
        $result.alerts += ("CRITICAL: Bot down, last log " + $minutesSinceLog + " min ago")
        $result.status = "DOWN"
    }
}

if ($result.alerts.Count -gt 0 -and $result.status -ne "DOWN") { $result.status = "ALERT" }

$jsonPath = Join-Path $PSScriptRoot "monitor-result.json"
$result | ConvertTo-Json -Depth 5 | Out-File $jsonPath -Encoding UTF8

Write-Host ""
Write-Host "===== Monitor Result =====" -ForegroundColor Yellow
$color = "Green"; if ($result.status -eq "ALERT") { $color = "Yellow" } elseif ($result.status -eq "DOWN") { $color = "Red" }
Write-Host ("Status: " + $result.status) -ForegroundColor $color
Write-Host ("PnL 6h:  $" + $result.metrics.pnl_6h + " (" + $result.metrics.trades_6h + " trades, win " + ($result.metrics.winrate_6h * 100) + "%)")
Write-Host ("PnL 24h: $" + $result.metrics.pnl_24h + " (" + $result.metrics.trades_24h + " trades)")
Write-Host ("Major blocked 24h: " + $result.metrics.major_blocked_24h + " / Major entries: " + $result.metrics.major_entries_24h)
Write-Host ("Online logs: " + $result.metrics.online_logs_24h + " / Retrain: " + $result.metrics.online_retrain_24h)
Write-Host ("Label: " + $result.metrics.label_n_24h + " samples, success " + ($result.metrics.label_success_rate_24h * 100) + "%")
Write-Host ("Last log: " + $result.metrics.minutes_since_last_log + " min ago")

if ($result.alerts.Count -gt 0) {
    Write-Host ""
    Write-Host "[ALERTS]" -ForegroundColor Red
    foreach ($a in $result.alerts) { Write-Host ("  " + $a) }
}

Write-Host ""
Write-Host ("Saved: " + $jsonPath) -ForegroundColor DarkGray
if ($result.status -eq "OK") { exit 0 } else { exit 1 }
