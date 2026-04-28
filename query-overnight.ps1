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

Write-Host "##### UserId=1 새벽~현재 (04-13 18:00 ~ 04-14) #####"

Write-Host "=== [1] 전체 통계 ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS Total,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END),0) AS decimal(5,1)) AS WinR
FROM TradeHistory WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= '2026-04-13 18:00:00'
"@ | Format-Table -AutoSize

Write-Host "=== [2] PUMP 전체 거래 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent, LEFT(ExitReason,55) AS Reason,
    DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec, EntryTime, Strategy
FROM TradeHistory WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= '2026-04-13 18:00:00'
  AND Symbol NOT IN ($majors) AND Strategy NOT IN ('MANUAL')
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 손절 리스트 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent, LEFT(ExitReason,55) AS Reason,
    DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec, EntryTime
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= '2026-04-13 18:00:00'
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 서킷 브레이커 / RISK BLOCK ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Timestamp, LEFT(Message,150) AS Msg FROM FooterLogs WHERE (Message LIKE '%circuit%' OR Message LIKE '%RISK%BLOCK%') AND Timestamp >= '2026-04-13 18:00:00' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [5] 봇 버전 + 마지막 로그 ===" -ForegroundColor Cyan
Q "SELECT TOP 1 Timestamp FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [6] PUMP LONG 신호 발생 수 ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Message LIKE '%side=LONG%' AND Message LIKE '%PUMP%' AND Timestamp >= '2026-04-13 18:00:00' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [7] SLOT BLOCK 최근 ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Message LIKE '%SLOT%BLOCK%' AND Timestamp >= '2026-04-13 18:00:00' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [8] 활성 포지션 ===" -ForegroundColor Cyan
Q "SELECT Symbol, Side, EntryPrice, EntryTime, Strategy, IsClosed FROM TradeHistory WHERE UserId=1 AND IsClosed=0 ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host "=== [9] UserId=10 같은 기간 비교 ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS Total,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net
FROM TradeHistory WHERE UserId=10 AND IsClosed=1
  AND ExitTime >= '2026-04-13 18:00:00'
  AND Symbol NOT IN ($majors)
"@ | Format-Table -AutoSize
