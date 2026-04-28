function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 15
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== A. FooterLogs 마지막 시간 + 빈도 ====" -ForegroundColor Cyan
Q @"
SELECT
  MAX(Timestamp) AS LastLog,
  SUM(CASE WHEN Timestamp >= DATEADD(MINUTE,-5,GETDATE()) THEN 1 ELSE 0 END) AS Last5min,
  SUM(CASE WHEN Timestamp >= DATEADD(MINUTE,-30,GETDATE()) THEN 1 ELSE 0 END) AS Last30min
FROM FooterLogs
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== B. FooterLogs 최근 20건 ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Timestamp, LEFT(Message,170) AS Msg FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== C. WebSocket 끊김/복구 last 30min ====" -ForegroundColor Cyan
Q @"
SELECT
  SUM(CASE WHEN Message LIKE '%끊김%' THEN 1 ELSE 0 END) AS LostCount,
  SUM(CASE WHEN Message LIKE '%복구%' THEN 1 ELSE 0 END) AS RestoredCount,
  SUM(CASE WHEN Message LIKE '%WS-WATCHDOG%' THEN 1 ELSE 0 END) AS WatchdogTrigger
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== D. 진입 흐름 (GATE-CHECK / GATE-PASS / 차단 / 진입 / FILLED) last 5min ====" -ForegroundColor Cyan
Q @"
SELECT
  SUM(CASE WHEN Message LIKE '%[GATE-CHECK]%' THEN 1 ELSE 0 END) AS GateCheck,
  SUM(CASE WHEN Message LIKE '%[GATE-PASS]%' THEN 1 ELSE 0 END) AS GatePass,
  SUM(CASE WHEN Message LIKE '%[GATE]%차단%' THEN 1 ELSE 0 END) AS GateBlock,
  SUM(CASE WHEN Message LIKE '%주문%FILLED%' THEN 1 ELSE 0 END) AS Filled,
  SUM(CASE WHEN Message LIKE '%ExecuteAutoOrder%' OR Message LIKE '%진입 시도%' THEN 1 ELSE 0 END) AS EntryAttempt
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-5,GETDATE())
"@ | Format-Table -AutoSize
