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

# UserId=1, 2026-04-12 00:00 ~ 23:59 (로컬 시간 KST)
$dateFilter = "UserId=1 AND EntryTime >= '2026-04-12 00:00:00' AND EntryTime < '2026-04-13 00:00:00'"
$exitFilter = "UserId=1 AND ExitTime >= '2026-04-12 00:00:00' AND ExitTime < '2026-04-13 00:00:00'"

Write-Host "################################################################"
Write-Host "# UserId=1, 2026-04-12 거래 분석"
Write-Host "################################################################"
Write-Host ""

Write-Host "=== [0] 전체 통계 (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS TotalClosed,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(CASE WHEN PnL = 0 THEN 1 ELSE 0 END) AS Breakeven,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPnLPct,
    MIN(PnL) AS WorstLoss,
    MAX(PnL) AS BestWin
FROM TradeHistory
WHERE IsClosed = 1 AND $exitFilter
"@ | Format-Table -AutoSize

Write-Host "=== [A1] 메이저 진입 내역 (04-12) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, Strategy, EntryTime, ExitTime,
       EntryPrice, ExitPrice, PnL, PnLPercent, LEFT(ExitReason, 50) AS Reason
FROM TradeHistory
WHERE Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND $dateFilter
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [A2] 메이저 마지막 진입 시각 (UserId=1 전체) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, MAX(EntryTime) AS LastEntry,
       DATEDIFF(HOUR, MAX(EntryTime), GETDATE()) AS HoursAgo,
       COUNT(*) AS TotalCount
FROM TradeHistory
WHERE UserId=1
  AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY Symbol
ORDER BY LastEntry DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [B1] PUMP 손절 통계 (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS LossCount,
    SUM(PnL) AS TotalLoss,
    AVG(PnLPercent) AS AvgPnLPct,
    MIN(PnLPercent) AS WorstPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin,
    MIN(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS MinHoldMin
FROM TradeHistory
WHERE IsClosed = 1 AND PnL < 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND $exitFilter
"@ | Format-Table -AutoSize

Write-Host "=== [B2] PUMP 손절 ExitReason (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20
    LEFT(ExitReason, 60) AS Reason,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Loss,
    AVG(PnLPercent) AS AvgPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin
FROM TradeHistory
WHERE IsClosed = 1 AND PnL < 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND $exitFilter
GROUP BY LEFT(ExitReason, 60)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [B3] PUMP 손절 보유시간 분포 (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '0-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '6-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '16-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '31-60min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 180 THEN '1-3h'
        ELSE '3h+'
    END AS HoldBucket,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Loss,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE IsClosed = 1 AND PnL < 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND $exitFilter
GROUP BY
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '0-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '6-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '16-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '31-60min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 180 THEN '1-3h'
        ELSE '3h+'
    END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [C1] PUMP 익절 통계 (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS WinCount,
    SUM(PnL) AS TotalProfit,
    AVG(PnLPercent) AS AvgPnLPct,
    MAX(PnLPercent) AS BestPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin
FROM TradeHistory
WHERE IsClosed = 1 AND PnL > 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND $exitFilter
"@ | Format-Table -AutoSize

Write-Host "=== [C2] PUMP 익절 ExitReason (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20
    LEFT(ExitReason, 60) AS Reason,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Profit,
    AVG(PnLPercent) AS AvgPct,
    MAX(PnLPercent) AS BestPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin
FROM TradeHistory
WHERE IsClosed = 1 AND PnL > 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND $exitFilter
GROUP BY LEFT(ExitReason, 60)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [C3] TOP 10 수익 거래 (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Symbol, PnL, PnLPercent, LEFT(ExitReason, 50) AS Reason,
    DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin, EntryTime
FROM TradeHistory
WHERE IsClosed = 1 AND PnL > 0
  AND $exitFilter
ORDER BY PnL DESC
"@ | Format-Table -AutoSize

Write-Host "=== [C4] TOP 10 손실 거래 (UserId=1, 04-12) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Symbol, Side, PnL, PnLPercent, LEFT(ExitReason, 50) AS Reason,
    DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin, EntryTime
FROM TradeHistory
WHERE IsClosed = 1 AND PnL < 0
  AND $exitFilter
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [D] 심볼별 승률 (UserId=1, 04-12, 최소 3건) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS NetPnL,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE IsClosed = 1 AND $exitFilter
GROUP BY Symbol
HAVING COUNT(*) >= 3
ORDER BY NetPnL DESC
"@ | Format-Table -AutoSize
