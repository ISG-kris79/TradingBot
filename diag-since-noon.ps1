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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 120
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# KST 12:00 = UTC 03:00. 오늘=2026-04-22.
$cutoff = "2026-04-22 03:00:00"
Write-Host "=== UserId=1 since $cutoff UTC (=KST noon) ===" -ForegroundColor Cyan

Write-Host "`n--- [1] 트레이드 결과 (체결+청산) ---" -ForegroundColor Yellow
Q @"
SELECT TOP 100
    Symbol, Side, EntryPrice, ExitPrice, Quantity, RealizedProfit,
    DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin,
    EntryTime, ExitTime, ExitReason, AiScore, SignalSource
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff'
ORDER BY ExitTime DESC
"@ | Format-Table -AutoSize

Write-Host "`n--- [2] 요약: 승률/PnL ---" -ForegroundColor Yellow
Q @"
SELECT
    COUNT(*) AS Trades,
    SUM(CASE WHEN RealizedProfit > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN RealizedProfit < 0 THEN 1 ELSE 0 END) AS Losses,
    CAST(SUM(CASE WHEN RealizedProfit > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) * 100 AS DECIMAL(5,1)) AS WinRatePct,
    CAST(SUM(RealizedProfit) AS DECIMAL(18,2)) AS TotalPnL,
    CAST(AVG(RealizedProfit) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(AVG(CASE WHEN RealizedProfit > 0 THEN RealizedProfit END) AS DECIMAL(18,4)) AS AvgWin,
    CAST(AVG(CASE WHEN RealizedProfit < 0 THEN RealizedProfit END) AS DECIMAL(18,4)) AS AvgLoss
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff'
"@ | Format-List

Write-Host "`n--- [3] 신호 소스별 ---" -ForegroundColor Yellow
Q @"
SELECT SignalSource,
    COUNT(*) AS Cnt,
    SUM(CASE WHEN RealizedProfit > 0 THEN 1 ELSE 0 END) AS Wins,
    CAST(SUM(CASE WHEN RealizedProfit > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) * 100 AS DECIMAL(5,1)) AS WinRatePct,
    CAST(SUM(RealizedProfit) AS DECIMAL(18,2)) AS PnL,
    CAST(AVG(RealizedProfit) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff'
GROUP BY SignalSource
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "`n--- [4] 메이저 vs PUMP ---" -ForegroundColor Yellow
Q @"
SELECT
    CASE WHEN Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT') THEN 'MAJOR' ELSE 'PUMP' END AS Cat,
    COUNT(*) AS Cnt,
    CAST(SUM(CASE WHEN RealizedProfit > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) * 100 AS DECIMAL(5,1)) AS WinRatePct,
    CAST(SUM(RealizedProfit) AS DECIMAL(18,2)) AS PnL
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff'
GROUP BY CASE WHEN Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT') THEN 'MAJOR' ELSE 'PUMP' END
"@ | Format-Table -AutoSize

Write-Host "`n--- [5] 청산 사유별 ---" -ForegroundColor Yellow
Q @"
SELECT ExitReason,
    COUNT(*) AS Cnt,
    CAST(AVG(RealizedProfit) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(SUM(RealizedProfit) AS DECIMAL(18,2)) AS PnL
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff'
GROUP BY ExitReason
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "`n--- [6] AiScore 구간별 (예측 vs 실제) ---" -ForegroundColor Yellow
Q @"
SELECT
    CASE
      WHEN AiScore >= 0.80 THEN '0.80+'
      WHEN AiScore >= 0.70 THEN '0.70~0.80'
      WHEN AiScore >= 0.60 THEN '0.60~0.70'
      WHEN AiScore >= 0.50 THEN '0.50~0.60'
      ELSE '<0.50' END AS ScoreBucket,
    COUNT(*) AS Cnt,
    CAST(SUM(CASE WHEN RealizedProfit > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) * 100 AS DECIMAL(5,1)) AS WinRatePct,
    CAST(AVG(RealizedProfit) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff' AND AiScore IS NOT NULL
GROUP BY CASE
      WHEN AiScore >= 0.80 THEN '0.80+'
      WHEN AiScore >= 0.70 THEN '0.70~0.80'
      WHEN AiScore >= 0.60 THEN '0.60~0.70'
      WHEN AiScore >= 0.50 THEN '0.50~0.60'
      ELSE '<0.50' END
ORDER BY ScoreBucket DESC
"@ | Format-Table -AutoSize

Write-Host "`n--- [7] 보유시간별 (분 구간) ---" -ForegroundColor Yellow
Q @"
SELECT
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 5 THEN '00.<5'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 15 THEN '01.5-15'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 60 THEN '02.15-60'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 240 THEN '03.1h-4h'
        ELSE '04.4h+' END AS HoldBucket,
    COUNT(*) AS Cnt,
    CAST(SUM(CASE WHEN RealizedProfit > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) * 100 AS DECIMAL(5,1)) AS WinRatePct,
    CAST(AVG(RealizedProfit) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeResults
WHERE UserId = 1 AND ExitTime >= '$cutoff'
GROUP BY CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 5 THEN '00.<5'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 15 THEN '01.5-15'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 60 THEN '02.15-60'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) < 240 THEN '03.1h-4h'
        ELSE '04.4h+' END
ORDER BY HoldBucket
"@ | Format-Table -AutoSize
