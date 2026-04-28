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

# 진입 03:19 ~ 마지막 청산 10:36
$startTime = "2026-04-12 03:10:00"
$endTime = "2026-04-12 11:00:00"
$entryPrice = 29.57

Write-Host "=== [A] GIGGLEUSDT 5분봉 진입 후 전체 (03:10 ~ 11:00) ===" -ForegroundColor Cyan
$sql5m = "SELECT OpenTime, [Open], High, Low, [Close], CAST((High-29.57)/29.57*100 AS decimal(8,2)) AS MaxUpFromEntry FROM CandleData WHERE Symbol='GIGGLEUSDT' AND IntervalText='5m' AND OpenTime BETWEEN '$startTime' AND '$endTime' ORDER BY OpenTime ASC"
Q $sql5m | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [B] GIGGLEUSDT 최고 고점 (진입 후 전체) ===" -ForegroundColor Cyan
$sqlHigh = "SELECT MAX(High) AS PeakHigh, CAST((MAX(High)-29.57)/29.57*100 AS decimal(8,2)) AS PeakPctFromEntry, CAST((MAX(High)-29.57)/29.57*100*20 AS decimal(8,2)) AS PeakRoePct_x20 FROM CandleData WHERE Symbol='GIGGLEUSDT' AND IntervalText='5m' AND OpenTime BETWEEN '2026-04-12 03:19:00' AND '2026-04-12 11:00:00'"
Q $sqlHigh | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [C] GIGGLEUSDT 고점 발생 시점 ===" -ForegroundColor Cyan
$sqlPeakTime = "SELECT TOP 5 OpenTime, High, CAST((High-29.57)/29.57*100 AS decimal(8,2)) AS PctFromEntry, CAST((High-29.57)/29.57*100*20 AS decimal(8,2)) AS RoeX20 FROM CandleData WHERE Symbol='GIGGLEUSDT' AND IntervalText='5m' AND OpenTime BETWEEN '2026-04-12 03:19:00' AND '2026-04-12 11:00:00' ORDER BY High DESC"
Q $sqlPeakTime | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [D] FooterLogs GIGGLE 트레일링/계단식/ROE 최고 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 40 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%GIGGLEUSDT%'
  AND Timestamp BETWEEN '2026-04-12 03:19:00' AND '2026-04-12 11:00:00'
  AND (Message LIKE '%ROE%' OR Message LIKE '%trailing%' OR Message LIKE '%stair%' OR Message LIKE '%Partial%' OR Message LIKE '%청산%' OR Message LIKE '%익절%' OR Message LIKE '%계단%' OR Message LIKE '%최고%' OR Message LIKE '%고점%' OR Message LIKE '%트레일%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [E] FooterLogs GIGGLE 최근 30개 전체 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%GIGGLEUSDT%'
  AND Timestamp BETWEEN '2026-04-12 03:19:00' AND '2026-04-12 11:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize
