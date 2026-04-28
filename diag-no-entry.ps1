$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection)
    $cn.Open()
    $cm = $cn.CreateCommand()
    $cm.CommandText = $sql
    $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet
    [void]$ap.Fill($ds)
    $cn.Close()
    return $ds.Tables[0]
}

Write-Host "==== Bot_Log recent 60 min - block reasons ===="
$r1 = @'
SELECT TOP 30 EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  Symbol, Direction, Allowed, ML_Conf, LEFT(Reason, 130) AS Reason
FROM Bot_Log
WHERE UserId=1 AND EventTime >= DATEADD(MINUTE, -60, GETUTCDATE())
ORDER BY EventTime DESC
'@
Q $r1 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== Block reason count (last 60 min) ===="
$r2 = @'
SELECT LEFT(Reason, 60) AS Reason, COUNT(*) AS Cnt
FROM Bot_Log
WHERE UserId=1 AND EventTime >= DATEADD(MINUTE, -60, GETUTCDATE()) AND Allowed = 0
GROUP BY LEFT(Reason, 60)
ORDER BY Cnt DESC
'@
Q $r2 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== Last 3 trades (any, last 6 hours) ===="
$r3 = "SELECT TOP 5 EntryTime, Symbol, Strategy, Side, PnL, ExitReason, IsClosed FROM TradeHistory WHERE UserId=1 AND EntryTime >= DATEADD(HOUR, -6, GETUTCDATE()) ORDER BY EntryTime DESC"
Q $r3 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== FooterLogs SPIKE_SLOT / VALIDATE / DECIDE (last 30 min) ===="
$r4 = @'
SELECT TOP 40 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 180) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETUTCDATE())
  AND (Message LIKE '%SPIKE_SLOT%' OR Message LIKE '%VALIDATE%' OR Message LIKE '%DECIDE%' OR Message LIKE '%AI_GATE%' OR Message LIKE '%GATE1%' OR Message LIKE '%꼭대기%' OR Message LIKE '%posInRange%')
ORDER BY Timestamp DESC
'@
Q $r4 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== Currently running bot version (FooterLogs) ===="
$r5 = @'
SELECT TOP 3 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Message LIKE '%v5.%' AND (Message LIKE '%버전%' OR Message LIKE '%version%' OR Message LIKE '%TradingBot%')
ORDER BY Timestamp DESC
'@
Q $r5 | Format-Table -AutoSize -Wrap
