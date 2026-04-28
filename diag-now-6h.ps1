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

Write-Host "==== A. Bot 버전 ====" -ForegroundColor Cyan
$v = "$env:LOCALAPPDATA\TradingBot\current\sq.version"
if (Test-Path $v) { (Get-Content $v -Raw) -split "`n" | Where-Object { $_ -match "<version>" } | Write-Host }

Write-Host ""
Write-Host "==== B. FooterLogs 마지막 시간 + 5분/1시간 빈도 ====" -ForegroundColor Cyan
Q @"
SELECT
  MAX(Timestamp) AS LastLog,
  SUM(CASE WHEN Timestamp >= DATEADD(MINUTE,-5,GETDATE()) THEN 1 ELSE 0 END) AS Last5min,
  SUM(CASE WHEN Timestamp >= DATEADD(HOUR,-1,GETDATE()) THEN 1 ELSE 0 END) AS Last1h
FROM FooterLogs
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== C. FooterLogs 최근 15건 ====" -ForegroundColor Cyan
Q "SELECT TOP 15 Timestamp, LEFT(Message,180) AS Msg FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== D. TradeHistory 최근 진입/청산 ====" -ForegroundColor Cyan
Q "SELECT TOP 5 Symbol, Category, EntryTime, ExitTime FROM TradeHistory WHERE EntryTime >= DATEADD(HOUR,-12,GETDATE()) OR ExitTime >= DATEADD(HOUR,-12,GETDATE()) ORDER BY ISNULL(ExitTime,EntryTime) DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== E. 차단 사유 last 1h top 10 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10 LEFT(Message,200) AS Msg, COUNT(*) AS Cnt
FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-1,GETDATE())
  AND (Message LIKE '%[GATE]%차단%' OR Message LIKE '%차단%' OR Message LIKE '%금지%' OR Message LIKE '%실패%')
GROUP BY LEFT(Message,200) ORDER BY Cnt DESC
"@ | Format-Table -AutoSize -Wrap
