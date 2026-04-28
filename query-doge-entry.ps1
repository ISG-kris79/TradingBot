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

Write-Host "=== DOGE 5분봉 (23:00~23:40) ===" -ForegroundColor Cyan
Q @"
SELECT OpenTime, [Open], High, Low, [Close],
    CAST(([Close]-[Open])/[Open]*100 AS decimal(6,2)) AS ChgPct
FROM CandleData WHERE Symbol='DOGEUSDT' AND IntervalText='5m'
  AND OpenTime BETWEEN '2026-04-14 22:30:00' AND '2026-04-15 00:00:00'
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== DOGE 진입 경로 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-50000 FROM FooterLogs)
  AND Message LIKE '%DOGEUSDT%'
  AND (Message LIKE '%ENTRY%START%' OR Message LIKE '%FILLED%' OR Message LIKE '%TICK_SURGE%')
  AND Timestamp BETWEEN '2026-04-14 23:25:00' AND '2026-04-14 23:40:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== DOGE 24시간 최저~최고 ===" -ForegroundColor Cyan
Q @"
SELECT MIN([Low]) AS DayLow, MAX(High) AS DayHigh,
    CAST((MAX(High)-MIN([Low]))/MIN([Low])*100 AS decimal(6,2)) AS DayRange
FROM CandleData WHERE Symbol='DOGEUSDT' AND IntervalText='5m'
  AND OpenTime >= '2026-04-14 12:00:00'
"@ | Format-Table -AutoSize
