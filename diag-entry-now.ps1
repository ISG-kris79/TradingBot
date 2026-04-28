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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== A. Bot version + uptime ====" -ForegroundColor Cyan
$p = Get-Process TradingBot -ErrorAction SilentlyContinue
if ($p) { $p | Select-Object Id, StartTime, @{N='RunMin';E={[int]((Get-Date)-$_.StartTime).TotalMinutes}}, @{N='WorkMB';E={[int]($_.WorkingSet64/1MB)}} | Format-Table -AutoSize }
else { Write-Host "  Bot NOT RUNNING" -ForegroundColor Red }
$v = "$env:LOCALAPPDATA\TradingBot\current\sq.version"
if (Test-Path $v) { (Get-Content $v -Raw) -split "`n" | Where-Object { $_ -match "<version>" } | Write-Host }

Write-Host ""
Write-Host "==== B. FooterLogs last 30min - GATE-PASS / GATE-CHECK / 차단 빈도 ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE
    WHEN Message LIKE '%[GATE-PASS]%'  THEN '01_GATE-PASS_(통과)'
    WHEN Message LIKE '%[GATE-CHECK]%' THEN '02_GATE-CHECK_(평가시작)'
    WHEN Message LIKE '%[GATE]%차단%' THEN '03_GATE-BLOCK_(차단)'
    WHEN Message LIKE '%[감시진입%' THEN '04_WATCH'
    WHEN Message LIKE '%[SIGNAL]%' THEN '05_SIGNAL'
    ELSE '99_OTHER'
  END AS Type, COUNT(*) AS Cnt
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
GROUP BY
  CASE
    WHEN Message LIKE '%[GATE-PASS]%'  THEN '01_GATE-PASS_(통과)'
    WHEN Message LIKE '%[GATE-CHECK]%' THEN '02_GATE-CHECK_(평가시작)'
    WHEN Message LIKE '%[GATE]%차단%' THEN '03_GATE-BLOCK_(차단)'
    WHEN Message LIKE '%[감시진입%' THEN '04_WATCH'
    WHEN Message LIKE '%[SIGNAL]%' THEN '05_SIGNAL'
    ELSE '99_OTHER'
  END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== C. 차단 사유 last 30min top 10 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10 LEFT(Message, 200) AS Msg, COUNT(*) AS Cnt
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND Message LIKE '%[GATE]%차단%'
GROUP BY LEFT(Message, 200)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== D. TradeHistory today ====" -ForegroundColor Cyan
Q "SELECT COUNT(*) AS NewToday FROM TradeHistory WHERE EntryTime >= CAST(GETDATE() AS DATE)" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== E. ActiveTrackingPool 로그 ====" -ForegroundColor Cyan
Q "SELECT TOP 5 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Message LIKE '%추적풀%' ORDER BY Id DESC" | Format-Table -AutoSize -Wrap
