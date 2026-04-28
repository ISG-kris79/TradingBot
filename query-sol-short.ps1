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

Write-Host "=== [1] SOL 최근 진입 내역 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Id, Side, Strategy, EntryPrice, ExitPrice, PnL, PnLPercent,
       LEFT(ExitReason, 50) AS Reason, EntryTime, ExitTime, IsClosed
FROM TradeHistory WHERE Symbol='SOLUSDT' AND UserId=1
ORDER BY EntryTime DESC
"@ | Format-List

Write-Host "=== [2] SOL 5분봉 최근 2시간 ===" -ForegroundColor Cyan
Q @"
SELECT OpenTime, [Open], High, Low, [Close],
       CAST(([Close]-[Open])/[Open]*100 AS decimal(6,2)) AS ChgPct,
       CAST(Volume AS bigint) AS Vol
FROM CandleData WHERE Symbol='SOLUSDT' AND IntervalText='5m'
  AND OpenTime >= DATEADD(HOUR, -2, GETDATE())
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] SOL 진입 시점 FooterLogs ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%SOLUSDT%'
  AND (Message LIKE '%ENTRY%START%' OR Message LIKE '%AI_GATE%' OR Message LIKE '%ORDER%FILLED%'
       OR Message LIKE '%aiScore%' OR Message LIKE '%decision%')
  AND Timestamp >= DATEADD(HOUR, -1, GETDATE())
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] SOL SMA/RSI 현재 상태 (최근 봉 기준) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 1
    [Close] AS CurrentClose,
    (SELECT AVG([Close]) FROM (SELECT TOP 20 [Close] FROM CandleData WHERE Symbol='SOLUSDT' AND IntervalText='5m' ORDER BY OpenTime DESC) t) AS SMA20,
    (SELECT AVG([Close]) FROM (SELECT TOP 50 [Close] FROM CandleData WHERE Symbol='SOLUSDT' AND IntervalText='5m' ORDER BY OpenTime DESC) t) AS SMA50
FROM CandleData WHERE Symbol='SOLUSDT' AND IntervalText='5m'
ORDER BY OpenTime DESC
"@ | Format-Table -AutoSize
