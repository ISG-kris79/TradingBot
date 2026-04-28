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

Write-Host "=== [1] UserId=10 RAVEUSDT 거래 (04-13) ===" -ForegroundColor Cyan
Q @"
SELECT Id, EntryPrice, ExitPrice, PnL, PnLPercent,
    LEFT(ExitReason,50) AS Reason,
    DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec,
    EntryTime, ExitTime, Strategy, IsClosed
FROM TradeHistory WHERE UserId=10 AND Symbol='RAVEUSDT'
  AND EntryTime >= '2026-04-13 00:00:00'
ORDER BY EntryTime ASC
"@ | Format-List

Write-Host "=== [2] UserId=10 04-13 TOP 수익 전체 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Symbol, PnL, PnLPercent, LEFT(ExitReason,45) AS Reason, EntryTime
FROM TradeHistory WHERE UserId=10 AND IsClosed=1 AND PnL > 0
  AND EntryTime >= '2026-04-13 00:00:00'
ORDER BY PnLPercent DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] UserId=10 04-13 30% 이상 수익 거래 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent, LEFT(ExitReason,45) AS Reason,
    DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec, EntryTime
FROM TradeHistory WHERE UserId=10 AND IsClosed=1 AND PnLPercent >= 30
  AND EntryTime >= '2026-04-13 00:00:00'
ORDER BY PnLPercent DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] UserId=10 현재 활성 포지션 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, Quantity, EntryTime, Strategy,
    DATEDIFF(MINUTE,EntryTime,GETDATE()) AS HoldMin
FROM TradeHistory WHERE UserId=10 AND IsClosed=0
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] UserId=10 04-13 시간대별 진입 수 ===" -ForegroundColor Cyan
Q @"
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, EntryTime), 0) AS HourSlot,
    COUNT(*) AS Entries,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L
FROM TradeHistory WHERE UserId=10 AND IsClosed=1
  AND EntryTime >= '2026-04-13 00:00:00'
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, EntryTime), 0)
ORDER BY HourSlot
"@ | Format-Table -AutoSize

Write-Host "=== [6] UserId=10 PUMP 슬롯 상태 (지금 열린 PUMP 포지션 수) ===" -ForegroundColor Cyan
$majors = "'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT'"
Q "SELECT COUNT(*) AS OpenPump FROM TradeHistory WHERE UserId=10 AND IsClosed=0 AND Symbol NOT IN ($majors)" | Format-Table -AutoSize
Q "SELECT COUNT(*) AS OpenMajor FROM TradeHistory WHERE UserId=10 AND IsClosed=0 AND Symbol IN ($majors)" | Format-Table -AutoSize
