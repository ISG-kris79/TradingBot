function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
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
function QScalar($sql, $default = 0) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $val = $cm.ExecuteScalar()
    $cn.Close()
    if ($null -eq $val -or $val -is [DBNull]) { return $default }
    return $val
}

# [a] 7-day trend
Write-Host "==== [a] 7-day Trend ====" -ForegroundColor Cyan
$pnl7d = [double](QScalar "SELECT ROUND(ISNULL(SUM(PnL),0),2) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1")
$n7d = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1")
$wins7d = [int](QScalar "SELECT SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1")
Write-Host "7d: N=$n7d, TotalPnL=`$$pnl7d, WinRate=$([math]::Round($wins7d/[math]::Max($n7d,1),2))"

# [b] Label rate: After vs Before v5.10.66 (2026-04-20 04:37 UTC)
Write-Host "`n==== [b] Label Success Rate ====" -ForegroundColor Cyan
$labelAfterN = [int](QScalar "SELECT COUNT(*) FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc>='2026-04-20 04:37'")
$labelAfterSuccess = [int](QScalar "SELECT SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END) FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc>='2026-04-20 04:37'")
$labelAfterRate = if ($labelAfterN -gt 0) { [math]::Round($labelAfterSuccess / $labelAfterN, 4) } else { 0 }
$labelBeforeN = [int](QScalar "SELECT COUNT(*) FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc<'2026-04-20 04:37' AND EntryTimeUtc>=DATEADD(day,-7,GETUTCDATE())")
$labelBeforeSuccess = [int](QScalar "SELECT SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END) FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc<'2026-04-20 04:37' AND EntryTimeUtc>=DATEADD(day,-7,GETUTCDATE())")
$labelBeforeRate = if ($labelBeforeN -gt 0) { [math]::Round($labelBeforeSuccess / $labelBeforeN, 4) } else { 0 }
Write-Host "Before v5.10.66: N=$labelBeforeN, SuccessRate=$labelBeforeRate"
Write-Host "After v5.10.66:  N=$labelAfterN, SuccessRate=$labelAfterRate"

# [c] Category breakdown (7d)
Write-Host "`n==== [c] Category 7d ====" -ForegroundColor Cyan
$majorN = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1 AND Category='MAJOR'")
$majorPnL = [double](QScalar "SELECT ROUND(ISNULL(SUM(PnL),0),2) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1 AND Category='MAJOR'")
$pumpN = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1 AND Category='PUMP'")
$pumpPnL = [double](QScalar "SELECT ROUND(ISNULL(SUM(PnL),0),2) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1 AND Category='PUMP'")
$spikeN = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1 AND Category='SPIKE'")
$spikePnL = [double](QScalar "SELECT ROUND(ISNULL(SUM(PnL),0),2) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1 AND Category='SPIKE'")
Write-Host "MAJOR: N=$majorN, PnL=`$$majorPnL"
Write-Host "PUMP:  N=$pumpN, PnL=`$$pumpPnL"
Write-Host "SPIKE: N=$spikeN, PnL=`$$spikePnL"

# [d] Major entries after v5.10.66 deploy
Write-Host "`n==== [d] Major entries after deploy ====" -ForegroundColor Cyan
$majorAfterDeploy = [int](QScalar "SELECT COUNT(*) FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>='2026-04-20 13:37' AND (Symbol LIKE 'BTC%' OR Symbol LIKE 'ETH%' OR Symbol LIKE 'SOL%' OR Symbol LIKE 'XRP%')"
)
Write-Host "Major entries since 2026-04-20 13:37 KST: $majorAfterDeploy (expected: 0 if BlockMajorEntries working)"

# Decision (JSON output)
$action = "NONE"
if ($labelAfterN -ge 100 -and $labelAfterRate -ge 0.25) { $action = "MAJOR_UNBLOCK" }
elseif ($labelAfterN -ge 200 -and $labelAfterRate -lt 0.15) { $action = "TIGHTEN_THRESHOLD" }
elseif ($pnl7d -lt -2000) { $action = "EMERGENCY_REDUCE" }

Write-Host "`n==== Decision ====" -ForegroundColor Yellow
Write-Host "Action: $action"

$meta = [ordered]@{
    timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    pnl_7d = $pnl7d
    n_7d = $n7d
    label_after_n = $labelAfterN
    label_after_rate = $labelAfterRate
    label_before_n = $labelBeforeN
    label_before_rate = $labelBeforeRate
    major_n_7d = $majorN
    major_pnl_7d = $majorPnL
    major_entries_after_deploy = $majorAfterDeploy
    pump_n_7d = $pumpN
    pump_pnl_7d = $pumpPnL
    spike_n_7d = $spikeN
    spike_pnl_7d = $spikePnL
    action = $action
}
$meta | ConvertTo-Json | Out-File (Join-Path $PSScriptRoot "tier-b-result.json") -Encoding UTF8
Write-Host "Saved: tier-b-result.json" -ForegroundColor DarkGray
