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
Write-Host "# UserId=1 최근 24시간 PUMP/급등 전체 분석"
Write-Host "################################################################"

Write-Host "=== [1] 전체 통계 (24h) ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPct,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS WinRate
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= DATEADD(HOUR, -24, GETDATE())
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
"@ | Format-Table -AutoSize

Write-Host "=== [2] 손절 전체 리스트 (24h) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent,
    LEFT(ExitReason, 55) AS Reason,
    DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
    EntryTime, ExitTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= DATEADD(HOUR, -24, GETDATE())
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
ORDER BY ExitTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 시간대별 추이 (24h, 3시간 단위) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN ExitTime >= DATEADD(HOUR, -3, GETDATE()) THEN 'A. 0-3h ago'
        WHEN ExitTime >= DATEADD(HOUR, -6, GETDATE()) THEN 'B. 3-6h ago'
        WHEN ExitTime >= DATEADD(HOUR, -9, GETDATE()) THEN 'C. 6-9h ago'
        WHEN ExitTime >= DATEADD(HOUR, -12, GETDATE()) THEN 'D. 9-12h ago'
        WHEN ExitTime >= DATEADD(HOUR, -18, GETDATE()) THEN 'E. 12-18h ago'
        ELSE 'F. 18-24h ago'
    END AS Period,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS NetPnL,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS WinRate
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= DATEADD(HOUR, -24, GETDATE())
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY CASE
    WHEN ExitTime >= DATEADD(HOUR, -3, GETDATE()) THEN 'A. 0-3h ago'
    WHEN ExitTime >= DATEADD(HOUR, -6, GETDATE()) THEN 'B. 3-6h ago'
    WHEN ExitTime >= DATEADD(HOUR, -9, GETDATE()) THEN 'C. 6-9h ago'
    WHEN ExitTime >= DATEADD(HOUR, -12, GETDATE()) THEN 'D. 9-12h ago'
    WHEN ExitTime >= DATEADD(HOUR, -18, GETDATE()) THEN 'E. 12-18h ago'
    ELSE 'F. 18-24h ago'
END
ORDER BY Period
"@ | Format-Table -AutoSize

Write-Host "=== [4] ExitReason 분포 (손절, 24h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 LEFT(ExitReason, 55) AS Reason,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Loss,
    AVG(PnLPercent) AS AvgPct,
    AVG(DATEDIFF(SECOND, EntryTime, ExitTime)) AS AvgHoldSec
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= DATEADD(HOUR, -24, GETDATE())
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY LEFT(ExitReason, 55)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] 심볼별 성적 (24h, 최소 2건) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= DATEADD(HOUR, -24, GETDATE())
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY Symbol
HAVING COUNT(*) >= 2
ORDER BY Net DESC
"@ | Format-Table -AutoSize

Write-Host "=== [6] 보유시간 분포 (손절, 24h) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 60 THEN 'A. 0-1min'
        WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 300 THEN 'B. 1-5min'
        WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 900 THEN 'C. 5-15min'
        WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 3600 THEN 'D. 15-60min'
        ELSE 'E. 1h+'
    END AS Bucket,
    COUNT(*) AS Cnt,
    SUM(PnL) AS Loss,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= DATEADD(HOUR, -24, GETDATE())
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY CASE
    WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 60 THEN 'A. 0-1min'
    WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 300 THEN 'B. 1-5min'
    WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 900 THEN 'C. 5-15min'
    WHEN DATEDIFF(SECOND, EntryTime, ExitTime) <= 3600 THEN 'D. 15-60min'
    ELSE 'E. 1h+'
END
ORDER BY Bucket
"@ | Format-Table -AutoSize
