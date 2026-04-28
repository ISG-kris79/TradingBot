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

$pumpFilter = "Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT') AND Strategy NOT IN ('MANUAL','MarketClose')"

Write-Host "################################################################"
Write-Host "# UserId=1 04-12 PUMP/급등 자동진입만 분석"
Write-Host "################################################################"

Write-Host "=== [1] 전체 통계 ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPct,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END), 0) AS decimal(5,1)) AS WinRate
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
"@ | Format-Table -AutoSize

Write-Host "=== [2] 시간대별 추이 (3시간 단위) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN ExitTime >= DATEADD(HOUR, -3, GETDATE()) THEN 'A. 최근3h'
        WHEN ExitTime >= DATEADD(HOUR, -6, GETDATE()) THEN 'B. 3-6h'
        WHEN ExitTime >= DATEADD(HOUR, -9, GETDATE()) THEN 'C. 6-9h'
        WHEN ExitTime >= DATEADD(HOUR, -12, GETDATE()) THEN 'D. 9-12h'
        ELSE 'E. 12h+'
    END AS Period,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS WinR
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
GROUP BY CASE
    WHEN ExitTime >= DATEADD(HOUR, -3, GETDATE()) THEN 'A. 최근3h'
    WHEN ExitTime >= DATEADD(HOUR, -6, GETDATE()) THEN 'B. 3-6h'
    WHEN ExitTime >= DATEADD(HOUR, -9, GETDATE()) THEN 'C. 6-9h'
    WHEN ExitTime >= DATEADD(HOUR, -12, GETDATE()) THEN 'D. 9-12h'
    ELSE 'E. 12h+'
END
ORDER BY Period
"@ | Format-Table -AutoSize

Write-Host "=== [3] TOP 10 수익 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Symbol, PnL, PnLPercent,
    LEFT(ExitReason, 50) AS Reason,
    DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec, EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL > 0
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
ORDER BY PnL DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] TOP 10 손실 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Symbol, PnL, PnLPercent,
    LEFT(ExitReason, 50) AS Reason,
    DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec, EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] 손절 ExitReason 분류 ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN ExitReason LIKE '%실가격 손절%' THEN 'A_실가격손절'
        WHEN ExitReason LIKE '%1분봉 하락추세%' THEN 'B_1분봉조기청산'
        WHEN ExitReason LIKE '%ROE 손절%' THEN 'C_ROE손절'
        WHEN ExitReason LIKE '%계단식%' THEN 'D_계단식스탑'
        WHEN ExitReason LIKE '%PartialClose%' THEN 'E_부분청산'
        ELSE 'F_기타'
    END AS Type,
    COUNT(*) AS Cnt, SUM(PnL) AS Loss, AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
GROUP BY CASE
    WHEN ExitReason LIKE '%실가격 손절%' THEN 'A_실가격손절'
    WHEN ExitReason LIKE '%1분봉 하락추세%' THEN 'B_1분봉조기청산'
    WHEN ExitReason LIKE '%ROE 손절%' THEN 'C_ROE손절'
    WHEN ExitReason LIKE '%계단식%' THEN 'D_계단식스탑'
    WHEN ExitReason LIKE '%PartialClose%' THEN 'E_부분청산'
    ELSE 'F_기타'
END
ORDER BY Type
"@ | Format-Table -AutoSize

Write-Host "=== [6] 심볼별 성적 (2건+) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol,
    COUNT(*) AS T, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net, AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
GROUP BY Symbol HAVING COUNT(*) >= 2
ORDER BY Net DESC
"@ | Format-Table -AutoSize

Write-Host "=== [7] 익절 ExitReason 분류 ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN ExitReason LIKE '%PartialClose%' THEN 'A_부분청산'
        WHEN ExitReason LIKE '%계단식%' THEN 'B_계단식스탑'
        WHEN ExitReason LIKE '%트레일링%' OR ExitReason LIKE '%trailing%' THEN 'C_트레일링'
        WHEN ExitReason LIKE '%고점 대비%' THEN 'D_고점대비하락'
        WHEN ExitReason LIKE '%ROE%' AND ExitReason LIKE '%익절%' THEN 'E_ROE익절'
        ELSE 'F_기타'
    END AS Type,
    COUNT(*) AS Cnt, SUM(PnL) AS Profit, AVG(PnLPercent) AS AvgPct, MAX(PnLPercent) AS BestPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL > 0
  AND ExitTime >= '2026-04-12 00:00:00'
  AND $pumpFilter
GROUP BY CASE
    WHEN ExitReason LIKE '%PartialClose%' THEN 'A_부분청산'
    WHEN ExitReason LIKE '%계단식%' THEN 'B_계단식스탑'
    WHEN ExitReason LIKE '%트레일링%' OR ExitReason LIKE '%trailing%' THEN 'C_트레일링'
    WHEN ExitReason LIKE '%고점 대비%' THEN 'D_고점대비하락'
    WHEN ExitReason LIKE '%ROE%' AND ExitReason LIKE '%익절%' THEN 'E_ROE익절'
    ELSE 'F_기타'
END
ORDER BY Type
"@ | Format-Table -AutoSize
