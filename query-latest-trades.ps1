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
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 90
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] UserId=1 최근 3시간 전체 거래 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, PnL, PnLPercent,
       LEFT(ExitReason, 55) AS Reason,
       DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
       EntryTime, ExitTime, IsClosed, Strategy
FROM TradeHistory
WHERE UserId=1
  AND EntryTime >= DATEADD(HOUR, -3, GETDATE())
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 최근 3시간 통계 ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(CASE WHEN IsClosed = 0 THEN 1 ELSE 0 END) AS Open,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPct,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END), 0) AS decimal(5,1)) AS WinRate
FROM TradeHistory
WHERE UserId=1 AND EntryTime >= DATEADD(HOUR, -3, GETDATE())
"@ | Format-Table -AutoSize

Write-Host "=== [3] 손절 내역 (최근 3h) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent,
       LEFT(ExitReason, 55) AS Reason,
       DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
       EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= DATEADD(HOUR, -3, GETDATE())
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 익절 내역 (최근 3h) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent,
       LEFT(ExitReason, 55) AS Reason,
       DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
       EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL > 0
  AND ExitTime >= DATEADD(HOUR, -3, GETDATE())
ORDER BY PnL DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] ExitReason 분포 (최근 3h, 손절만) ===" -ForegroundColor Cyan
Q @"
SELECT LEFT(ExitReason, 50) AS Reason,
       COUNT(*) AS Cnt, SUM(PnL) AS Loss, AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= DATEADD(HOUR, -3, GETDATE())
GROUP BY LEFT(ExitReason, 50)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [6] 심볼별 성적 (최근 3h) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= DATEADD(HOUR, -3, GETDATE())
GROUP BY Symbol
ORDER BY Net DESC
"@ | Format-Table -AutoSize

Write-Host "=== [7] 활성 포지션 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, Quantity, EntryTime, Strategy
FROM TradeHistory
WHERE UserId=1 AND IsClosed=0
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [8] 오늘 누적 (04-12 전체) ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS TotalPnL,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END), 0) AS decimal(5,1)) AS WinRate
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= '2026-04-12 00:00:00'
"@ | Format-Table -AutoSize
