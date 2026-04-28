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

Write-Host "=== [1] 04-13 PUMP 전체 통계 (UserId=1) ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS NetPnL, AVG(PnLPercent) AS AvgPct,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS decimal(5,1)) AS WinR
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-13 00:00:00'
  AND Symbol NOT IN ($majors) AND Strategy <> 'MANUAL'
"@  | Format-Table -AutoSize

Write-Host "=== [2] 전체 거래 리스트 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, PnL, PnLPercent, LEFT(ExitReason,50) AS Reason,
    DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec, EntryTime, Strategy
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-13 00:00:00'
  AND Symbol NOT IN ($majors) AND Strategy <> 'MANUAL'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 심볼별 성적 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, COUNT(*) AS T,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net, AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-13 00:00:00'
  AND Symbol NOT IN ($majors) AND Strategy <> 'MANUAL'
GROUP BY Symbol
ORDER BY Net DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] ExitReason 분류 ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN ExitReason LIKE '%실가격 손절%' THEN 'A_실가격손절'
        WHEN ExitReason LIKE '%1분봉%' THEN 'B_1분봉조기'
        WHEN ExitReason LIKE '%계단식%' THEN 'C_계단식'
        WHEN ExitReason LIKE '%PartialClose%' THEN 'D_부분청산'
        WHEN ExitReason LIKE '%ROE%손절%' THEN 'E_ROE손절'
        WHEN ExitReason LIKE '%고점%' THEN 'F_고점하락'
        WHEN ExitReason LIKE '%트레일%' OR ExitReason LIKE '%trailing%' THEN 'G_트레일링'
        ELSE 'H_기타'
    END AS Type,
    COUNT(*) AS Cnt, SUM(PnL) AS PnL, AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-13 00:00:00'
  AND Symbol NOT IN ($majors) AND Strategy <> 'MANUAL'
GROUP BY CASE
    WHEN ExitReason LIKE '%실가격 손절%' THEN 'A_실가격손절'
    WHEN ExitReason LIKE '%1분봉%' THEN 'B_1분봉조기'
    WHEN ExitReason LIKE '%계단식%' THEN 'C_계단식'
    WHEN ExitReason LIKE '%PartialClose%' THEN 'D_부분청산'
    WHEN ExitReason LIKE '%ROE%손절%' THEN 'E_ROE손절'
    WHEN ExitReason LIKE '%고점%' THEN 'F_고점하락'
    WHEN ExitReason LIKE '%트레일%' OR ExitReason LIKE '%trailing%' THEN 'G_트레일링'
    ELSE 'H_기타'
END
ORDER BY Type
"@ | Format-Table -AutoSize

Write-Host "=== [5] 보유시간 분포 ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 60 THEN 'A.0-1m'
        WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 300 THEN 'B.1-5m'
        WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 900 THEN 'C.5-15m'
        WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 3600 THEN 'D.15-60m'
        ELSE 'E.1h+'
    END AS Bucket,
    COUNT(*) AS Cnt, SUM(PnL) AS PnL,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-13 00:00:00'
  AND Symbol NOT IN ($majors) AND Strategy <> 'MANUAL'
GROUP BY CASE
    WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 60 THEN 'A.0-1m'
    WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 300 THEN 'B.1-5m'
    WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 900 THEN 'C.5-15m'
    WHEN DATEDIFF(SECOND,EntryTime,ExitTime) <= 3600 THEN 'D.15-60m'
    ELSE 'E.1h+'
END
ORDER BY Bucket
"@ | Format-Table -AutoSize
