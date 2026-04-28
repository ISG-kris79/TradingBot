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

Write-Host "################################################################"
Write-Host "# A. Major Entries Investigation"
Write-Host "################################################################"

Write-Host "=== A1: Major last entry per symbol (30 days) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, MAX(EntryTime) AS LastEntry,
       DATEDIFF(HOUR, MAX(EntryTime), GETDATE()) AS HoursAgo,
       COUNT(*) AS Total30d
FROM TradeHistory
WHERE Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
  AND EntryTime >= DATEADD(DAY, -30, GETDATE())
GROUP BY Symbol
ORDER BY LastEntry DESC
"@ | Format-Table -AutoSize

Write-Host "=== A2: Last 48h - Major ENTRY BLOCK reasons ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10
  CASE
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN 'VOLUME_BLOCK'
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN 'AI_GATE_BLOCK'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN 'MTF_GUARDIAN_BLOCK'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN 'VOLATILITY_BLOCK'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN 'SLOT_BLOCK'
    WHEN Message LIKE '%DeadCat%' THEN 'DEADCAT_BLOCK'
    WHEN Message LIKE '%Sanity%' THEN 'SANITY_BLOCK'
    WHEN Message LIKE '%RSI_Overheat%' THEN 'RSI_OVERHEAT'
    WHEN Message LIKE '%Rule_Violation%' THEN 'RULE_BLOCK'
    ELSE 'OTHER'
  END AS BlockType,
  COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -48, GETDATE())
  AND Message LIKE '%ENTRY%BLOCK%'
  AND (Message LIKE '%BTCUSDT%' OR Message LIKE '%ETHUSDT%' OR Message LIKE '%SOLUSDT%'
       OR Message LIKE '%XRPUSDT%' OR Message LIKE '%BNBUSDT%')
GROUP BY
  CASE
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN 'VOLUME_BLOCK'
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN 'AI_GATE_BLOCK'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN 'MTF_GUARDIAN_BLOCK'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN 'VOLATILITY_BLOCK'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN 'SLOT_BLOCK'
    WHEN Message LIKE '%DeadCat%' THEN 'DEADCAT_BLOCK'
    WHEN Message LIKE '%Sanity%' THEN 'SANITY_BLOCK'
    WHEN Message LIKE '%RSI_Overheat%' THEN 'RSI_OVERHEAT'
    WHEN Message LIKE '%Rule_Violation%' THEN 'RULE_BLOCK'
    ELSE 'OTHER'
  END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== A3: Major ENTRY BLOCK per symbol (48h) ===" -ForegroundColor Cyan
Q @"
SELECT
  CASE
    WHEN Message LIKE '%BTCUSDT%' THEN 'BTCUSDT'
    WHEN Message LIKE '%ETHUSDT%' THEN 'ETHUSDT'
    WHEN Message LIKE '%SOLUSDT%' THEN 'SOLUSDT'
    WHEN Message LIKE '%XRPUSDT%' THEN 'XRPUSDT'
    WHEN Message LIKE '%BNBUSDT%' THEN 'BNBUSDT'
    WHEN Message LIKE '%ADAUSDT%' THEN 'ADAUSDT'
    WHEN Message LIKE '%DOGEUSDT%' THEN 'DOGEUSDT'
    ELSE 'OTHER'
  END AS Symbol,
  COUNT(*) AS BlockCount
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -48, GETDATE())
  AND Message LIKE '%ENTRY%BLOCK%'
GROUP BY
  CASE
    WHEN Message LIKE '%BTCUSDT%' THEN 'BTCUSDT'
    WHEN Message LIKE '%ETHUSDT%' THEN 'ETHUSDT'
    WHEN Message LIKE '%SOLUSDT%' THEN 'SOLUSDT'
    WHEN Message LIKE '%XRPUSDT%' THEN 'XRPUSDT'
    WHEN Message LIKE '%BNBUSDT%' THEN 'BNBUSDT'
    WHEN Message LIKE '%ADAUSDT%' THEN 'ADAUSDT'
    WHEN Message LIKE '%DOGEUSDT%' THEN 'DOGEUSDT'
    ELSE 'OTHER'
  END
HAVING COUNT(*) > 10
ORDER BY BlockCount DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "################################################################"
Write-Host "# B. PUMP Stop Loss Analysis (excluding majors)"
Write-Host "################################################################"

Write-Host "=== B1: PUMP stop loss stats (72h) ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS LossCount,
    SUM(PnL) AS TotalLoss,
    AVG(PnLPercent) AS AvgPnLPct,
    MIN(PnLPercent) AS WorstPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin,
    MIN(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS MinHoldMin,
    MAX(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS MaxHoldMin
FROM TradeHistory
WHERE IsClosed = 1
  AND PnL < 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
"@ | Format-Table -AutoSize

Write-Host "=== B2: PUMP 손절 ExitReason TOP 10 (72h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15
    LEFT(ExitReason, 70) AS Reason,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Loss,
    AVG(PnLPercent) AS AvgPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin
FROM TradeHistory
WHERE IsClosed = 1 AND PnL < 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
GROUP BY LEFT(ExitReason, 70)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== B3: PUMP hold-time distribution for stop loss (72h) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '0-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '6-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '16-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '31-60min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 180 THEN '1-3h'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 720 THEN '3-12h'
        ELSE '12h+'
    END AS HoldBucket,
    COUNT(*) AS Cnt,
    SUM(PnL) AS TotalLoss,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE IsClosed = 1 AND PnL < 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
GROUP BY
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '0-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '6-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '16-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '31-60min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 180 THEN '1-3h'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 720 THEN '3-12h'
        ELSE '12h+'
    END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "################################################################"
Write-Host "# C. PUMP Take Profit Analysis"
Write-Host "################################################################"

Write-Host "=== C1: PUMP profit stats (72h) ===" -ForegroundColor Cyan
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
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
"@ | Format-Table -AutoSize

Write-Host "=== C2: PUMP 익절 ExitReason TOP 15 (72h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15
    LEFT(ExitReason, 70) AS Reason,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Profit,
    AVG(PnLPercent) AS AvgPct,
    MAX(PnLPercent) AS BestPct,
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)) AS AvgHoldMin
FROM TradeHistory
WHERE IsClosed = 1 AND PnL > 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
GROUP BY LEFT(ExitReason, 70)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== C3: Top 10 wins (72h, non-major) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10
    Symbol, PnL, PnLPercent, LEFT(ExitReason, 50) AS Reason,
    DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin,
    EntryTime
FROM TradeHistory
WHERE IsClosed = 1 AND PnL > 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
ORDER BY PnL DESC
"@ | Format-Table -AutoSize

Write-Host "=== C4: Profit bucket distribution (72h) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN PnLPercent < 5 THEN '0-5%'
        WHEN PnLPercent < 10 THEN '5-10%'
        WHEN PnLPercent < 20 THEN '10-20%'
        WHEN PnLPercent < 50 THEN '20-50%'
        WHEN PnLPercent < 100 THEN '50-100%'
        ELSE '100%+'
    END AS PctBucket,
    COUNT(*) AS Cnt,
    SUM(PnL) AS TotalPnL
FROM TradeHistory
WHERE IsClosed = 1 AND PnL > 0
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
GROUP BY
    CASE
        WHEN PnLPercent < 5 THEN '0-5%'
        WHEN PnLPercent < 10 THEN '5-10%'
        WHEN PnLPercent < 20 THEN '10-20%'
        WHEN PnLPercent < 50 THEN '20-50%'
        WHEN PnLPercent < 100 THEN '50-100%'
        ELSE '100%+'
    END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "################################################################"
Write-Host "# D. Matched entries (same symbol: TP+SL pairs)"
Write-Host "################################################################"
Write-Host "=== D1: Win/Loss ratio per symbol (72h, min 3 trades) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 25 Symbol,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS NetPnL,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE IsClosed = 1
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT','SUIUSDT')
  AND ExitTime >= DATEADD(HOUR, -72, GETDATE())
GROUP BY Symbol
HAVING COUNT(*) >= 3
ORDER BY NetPnL DESC
"@ | Format-Table -AutoSize
