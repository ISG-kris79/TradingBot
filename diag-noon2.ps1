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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 12:00 KST = 03:00 UTC. 오늘 2026-04-22.
$cutoff = "2026-04-22 03:00:00"

Write-Host "=== since $cutoff UTC (= 12:00 KST) ===" -ForegroundColor Cyan

Write-Host "`n[1] 전체 트레이드 (최신 50)" -ForegroundColor Yellow
Q @"
SELECT TOP 50
    Symbol, Side, Strategy, Category,
    CAST(PnL AS DECIMAL(18,3)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    holdingMinutes AS HoldMin,
    CAST(AiScore AS DECIMAL(5,3)) AS AiScore,
    ExitReason,
    EntryTime, ExitTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
ORDER BY ExitTime DESC
"@ | Format-Table -AutoSize

Write-Host "`n[2] 요약 승률/PnL" -ForegroundColor Yellow
Q @"
SELECT
    COUNT(*) AS Trades,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS Losses,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS TotalPnL,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(AVG(CASE WHEN PnL>0 THEN PnL END) AS DECIMAL(18,4)) AS AvgWin,
    CAST(AVG(CASE WHEN PnL<0 THEN PnL END) AS DECIMAL(18,4)) AS AvgLoss
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
"@ | Format-List

Write-Host "`n[3] Strategy별" -ForegroundColor Yellow
Q @"
SELECT Strategy,
    COUNT(*) AS Cnt,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
GROUP BY Strategy
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "`n[4] Category별 (MAJOR vs PUMP)" -ForegroundColor Yellow
Q @"
SELECT Category,
    COUNT(*) AS Cnt,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
GROUP BY Category
"@ | Format-Table -AutoSize

Write-Host "`n[5] ExitReason별" -ForegroundColor Yellow
Q @"
SELECT ExitReason,
    COUNT(*) AS Cnt,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
GROUP BY ExitReason
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "`n[6] AiScore 구간별" -ForegroundColor Yellow
Q @"
SELECT
    CASE
      WHEN AiScore >= 0.80 THEN '1.0.80+'
      WHEN AiScore >= 0.70 THEN '2.0.70-0.80'
      WHEN AiScore >= 0.60 THEN '3.0.60-0.70'
      WHEN AiScore >= 0.50 THEN '4.0.50-0.60'
      ELSE '5.<0.50' END AS Bucket,
    COUNT(*) AS Cnt,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff' AND AiScore IS NOT NULL
GROUP BY CASE
      WHEN AiScore >= 0.80 THEN '1.0.80+'
      WHEN AiScore >= 0.70 THEN '2.0.70-0.80'
      WHEN AiScore >= 0.60 THEN '3.0.60-0.70'
      WHEN AiScore >= 0.50 THEN '4.0.50-0.60'
      ELSE '5.<0.50' END
ORDER BY Bucket
"@ | Format-Table -AutoSize

Write-Host "`n[7] 보유시간 구간별" -ForegroundColor Yellow
Q @"
SELECT
    CASE
        WHEN holdingMinutes < 5 THEN '1.<5'
        WHEN holdingMinutes < 15 THEN '2.5-15'
        WHEN holdingMinutes < 60 THEN '3.15-60'
        WHEN holdingMinutes < 240 THEN '4.1-4h'
        ELSE '5.4h+' END AS Bucket,
    COUNT(*) AS Cnt,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
GROUP BY CASE
        WHEN holdingMinutes < 5 THEN '1.<5'
        WHEN holdingMinutes < 15 THEN '2.5-15'
        WHEN holdingMinutes < 60 THEN '3.15-60'
        WHEN holdingMinutes < 240 THEN '4.1-4h'
        ELSE '5.4h+' END
ORDER BY Bucket
"@ | Format-Table -AutoSize

Write-Host "`n[8] 메이저 진입 (UI에서 비활성화했다는데 들어옴?)" -ForegroundColor Yellow
Q @"
SELECT Symbol, Strategy, Category,
    CAST(PnL AS DECIMAL(18,3)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    CAST(AiScore AS DECIMAL(5,3)) AS AiScore,
    ExitReason, EntryTime, ExitTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= '$cutoff'
AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT')
ORDER BY EntryTime
"@ | Format-Table -AutoSize
