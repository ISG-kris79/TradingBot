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
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$majors = "'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT'"

Write-Host "=== [1] UserId=10 열린 포지션 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, Quantity, EntryTime, Strategy,
    DATEDIFF(HOUR, EntryTime, GETDATE()) AS HoldH
FROM TradeHistory WHERE UserId=10 AND IsClosed=0
ORDER BY EntryTime
"@ | Format-Table -AutoSize

Write-Host "=== [2] UserId=10 새벽~현재 전체 통계 (04-14) ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS Total,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END),0) AS decimal(5,1)) AS WinR
FROM TradeHistory WHERE UserId=10 AND IsClosed=1
  AND EntryTime >= '2026-04-14 00:00:00'
"@ | Format-Table -AutoSize

Write-Host "=== [3] UserId=10 04-14 PUMP 거래 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent, LEFT(ExitReason,50) AS Reason,
    DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec, EntryTime, Strategy
FROM TradeHistory WHERE UserId=10 AND IsClosed=1
  AND EntryTime >= '2026-04-14 00:00:00'
  AND Symbol NOT IN ($majors) AND Strategy NOT IN ('MANUAL')
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] UserId=10 04-14 TOP 수익 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Symbol, PnL, PnLPercent, LEFT(ExitReason,45) AS Reason, EntryTime
FROM TradeHistory WHERE UserId=10 AND IsClosed=1 AND PnL > 0
  AND EntryTime >= '2026-04-14 00:00:00'
ORDER BY PnL DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] UserId=10 04-14 손절 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent, LEFT(ExitReason,55) AS Reason,
    DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec, EntryTime
FROM TradeHistory WHERE UserId=10 AND IsClosed=1 AND PnL < 0
  AND EntryTime >= '2026-04-14 00:00:00'
ORDER BY PnL ASC
"@ | Format-Table -AutoSize
