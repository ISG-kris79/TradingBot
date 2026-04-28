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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] RAVEUSDT 전체 거래내역 (4/12~) ===" -ForegroundColor Cyan
Q @"
SELECT Id, UserId, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent, Strategy,
    EntryTime, ExitTime, IsClosed, LEFT(ExitReason,50) AS Reason,
    CAST(EntryPrice * ABS(Quantity) / 20 AS decimal(10,2)) AS Margin20x
FROM TradeHistory WHERE Symbol='RAVEUSDT'
  AND EntryTime >= '2026-04-12'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] RAVEUSDT 5분봉 가격 범위 (4/12~) ===" -ForegroundColor Cyan
Q @"
SELECT CAST(OpenTime AS DATE) AS Day,
    MIN([Low]) AS DayLow, MAX(High) AS DayHigh,
    CAST((MAX(High)-MIN([Low]))/MIN([Low])*100 AS decimal(8,2)) AS DayRange,
    COUNT(*) AS Candles
FROM CandleData WHERE Symbol='RAVEUSDT' AND IntervalText='5m'
  AND OpenTime >= '2026-04-12'
GROUP BY CAST(OpenTime AS DATE)
ORDER BY Day ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] RAVEUSDT 수익 합계 ===" -ForegroundColor Cyan
Q @"
SELECT UserId,
    COUNT(*) AS TotalTrades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS TotalPnL,
    MAX(PnLPercent) AS MaxROE,
    MIN(PnLPercent) AS MinROE
FROM TradeHistory WHERE Symbol='RAVEUSDT' AND EntryTime >= '2026-04-12' AND IsClosed=1
GROUP BY UserId
"@ | Format-Table -AutoSize
