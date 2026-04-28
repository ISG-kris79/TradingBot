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

Write-Host "=== [1] SKYAIUSDT 04-12 거래 내역 ===" -ForegroundColor Cyan
Q @"
SELECT Id, EntryPrice, ExitPrice, PnL, PnLPercent,
       LEFT(ExitReason, 60) AS Reason,
       EntryTime, ExitTime,
       DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
       Strategy
FROM TradeHistory
WHERE Symbol='SKYAIUSDT' AND UserId=1
  AND EntryTime >= '2026-04-12 00:00:00'
ORDER BY EntryTime ASC
"@ | Format-List

Write-Host "=== [2] SKYAIUSDT 5분봉 14:00~15:00 (진입 타이밍 확인) ===" -ForegroundColor Cyan
Q @"
SELECT OpenTime, [Open], High, Low, [Close],
       CAST((High-Low)/[Open]*100 AS decimal(6,2)) AS RangePct,
       CAST(Volume AS bigint) AS Vol
FROM CandleData
WHERE Symbol='SKYAIUSDT' AND IntervalText='5m'
  AND OpenTime BETWEEN '2026-04-12 13:50:00' AND '2026-04-12 15:10:00'
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] SKYAIUSDT FooterLogs 14:00~15:00 (진입 시도/차단 로그) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 40 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%SKYAIUSDT%'
  AND Timestamp BETWEEN '2026-04-12 14:00:00' AND '2026-04-12 15:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] SKYAIUSDT GATE1/MTF/BLOCK 로그 14:00~15:00 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%SKYAIUSDT%'
  AND (Message LIKE '%BLOCK%' OR Message LIKE '%GATE%' OR Message LIKE '%MTF%' OR Message LIKE '%BYPASS%')
  AND Timestamp BETWEEN '2026-04-12 14:00:00' AND '2026-04-12 15:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] SKYAIUSDT SL/TP API 등록 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%SKYAIUSDT%'
  AND (Message LIKE '%[SL]%' OR Message LIKE '%[TP]%' OR Message LIKE '%TRAILING%' OR Message LIKE '%STOP_MARKET%')
  AND Timestamp >= '2026-04-12 14:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize
