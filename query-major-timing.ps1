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

$majors = "'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT'"

Write-Host "=== [1] 메이저 최근 30일 전체 성적 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, COUNT(*) AS T,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net, AVG(PnLPercent) AS AvgPct,
    AVG(DATEDIFF(MINUTE,EntryTime,ExitTime)) AS AvgHoldMin
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND Symbol IN ($majors) AND Strategy NOT IN ('MANUAL')
  AND EntryTime >= DATEADD(DAY, -30, GETDATE())
GROUP BY Symbol, Side
ORDER BY Symbol, Side
"@ | Format-Table -AutoSize

Write-Host "=== [2] 메이저 LONG vs SHORT 전체 비교 ===" -ForegroundColor Cyan
Q @"
SELECT Side,
    COUNT(*) AS T,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    AVG(PnLPercent) AS AvgPct,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS decimal(5,1)) AS WinR
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND Symbol IN ($majors) AND Strategy NOT IN ('MANUAL')
  AND EntryTime >= DATEADD(DAY, -30, GETDATE())
GROUP BY Side
"@ | Format-Table -AutoSize

Write-Host "=== [3] 메이저 ExitReason 분류 (손절만) ===" -ForegroundColor Cyan
Q @"
SELECT LEFT(ExitReason,50) AS Reason, COUNT(*) AS Cnt, SUM(PnL) AS Loss
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
  AND Symbol IN ($majors) AND Strategy NOT IN ('MANUAL')
  AND EntryTime >= DATEADD(DAY, -30, GETDATE())
GROUP BY LEFT(ExitReason,50)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 현재 보유 메이저 포지션 상세 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, Quantity, EntryTime, Strategy,
    DATEDIFF(HOUR, EntryTime, GETDATE()) AS HoldHours
FROM TradeHistory
WHERE UserId=1 AND IsClosed=0 AND Symbol IN ($majors)
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] 현재 보유 메이저 vs 현재가 비교 ===" -ForegroundColor Cyan
Q @"
SELECT th.Symbol, th.Side, th.EntryPrice,
    cd.[Close] AS CurrentPrice,
    CASE WHEN th.Side='SELL'
        THEN CAST((th.EntryPrice - cd.[Close])/th.EntryPrice*100*20 AS decimal(8,2))
        ELSE CAST((cd.[Close] - th.EntryPrice)/th.EntryPrice*100*20 AS decimal(8,2))
    END AS EstROE
FROM TradeHistory th
OUTER APPLY (
    SELECT TOP 1 [Close] FROM CandleData
    WHERE Symbol=th.Symbol AND IntervalText='5m'
    ORDER BY OpenTime DESC
) cd
WHERE th.UserId=1 AND th.IsClosed=0 AND th.Symbol IN ($majors)
"@ | Format-Table -AutoSize

Write-Host "=== [6] 메이저 최근 10건 거래 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Symbol, Side, PnL, PnLPercent,
    LEFT(ExitReason,40) AS Reason, EntryTime, ExitTime,
    DATEDIFF(MINUTE,EntryTime,ExitTime) AS HoldMin
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND Symbol IN ($majors) AND Strategy NOT IN ('MANUAL')
ORDER BY ExitTime DESC
"@ | Format-Table -AutoSize
