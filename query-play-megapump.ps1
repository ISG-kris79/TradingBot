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

Write-Host "=== [1] PLAYUSDT 5분봉 전체 (13:00~15:30) ===" -ForegroundColor Cyan
Q @"
SELECT OpenTime, [Open], High, Low, [Close],
       CAST((High-Low)/[Open]*100 AS decimal(6,2)) AS RangePct,
       CAST(Volume AS bigint) AS Vol,
       CAST(([Close] - (SELECT TOP 1 [Open] FROM CandleData WHERE Symbol='PLAYUSDT' AND IntervalText='5m' AND OpenTime >= '2026-04-12 13:00:00' ORDER BY OpenTime ASC)) /
            (SELECT TOP 1 [Open] FROM CandleData WHERE Symbol='PLAYUSDT' AND IntervalText='5m' AND OpenTime >= '2026-04-12 13:00:00' ORDER BY OpenTime ASC) * 100 AS decimal(6,2)) AS CumPctFrom13h
FROM CandleData
WHERE Symbol='PLAYUSDT' AND IntervalText='5m'
  AND OpenTime BETWEEN '2026-04-12 13:00:00' AND '2026-04-12 15:30:00'
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] PLAYUSDT 최고점/최저점 (13:00~15:30) ===" -ForegroundColor Cyan
Q @"
SELECT
    MIN([Low]) AS SessionLow,
    MAX(High) AS SessionHigh,
    CAST((MAX(High) - MIN([Low])) / MIN([Low]) * 100 AS decimal(6,2)) AS MaxRangePct,
    CAST((MAX(High) - MIN([Low])) / MIN([Low]) * 100 * 20 AS decimal(8,2)) AS MaxROE_20x
FROM CandleData
WHERE Symbol='PLAYUSDT' AND IntervalText='5m'
  AND OpenTime BETWEEN '2026-04-12 13:00:00' AND '2026-04-12 15:30:00'
"@ | Format-Table -AutoSize

Write-Host "=== [3] PLAYUSDT 봇 진입 로그 시점 분석 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%PLAYUSDT%'
  AND (Message LIKE '%AI_ENTRY%' OR Message LIKE '%mlProb%' OR Message LIKE '%CANDIDATE%side=LONG%'
       OR Message LIKE '%CANDIDATE%side=WAIT%' OR Message LIKE '%FALLBACK%')
  AND Timestamp BETWEEN '2026-04-12 13:00:00' AND '2026-04-12 15:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] PLAYUSDT 감시등록/진입시도/차단 시간대 ===" -ForegroundColor Cyan
Q @"
SELECT
    DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, Timestamp) / 10) * 10, 0) AS Slot10m,
    SUM(CASE WHEN Message LIKE '%감시등록%' THEN 1 ELSE 0 END) AS WatchReg,
    SUM(CASE WHEN Message LIKE '%감시확인%' THEN 1 ELSE 0 END) AS WatchConfirm,
    SUM(CASE WHEN Message LIKE '%일일한도%' THEN 1 ELSE 0 END) AS DailyLimit,
    SUM(CASE WHEN Message LIKE '%side=WAIT%' THEN 1 ELSE 0 END) AS WaitSignal,
    SUM(CASE WHEN Message LIKE '%side=LONG%' THEN 1 ELSE 0 END) AS LongSignal
FROM FooterLogs
WHERE Message LIKE '%PLAYUSDT%'
  AND Timestamp BETWEEN '2026-04-12 09:00:00' AND '2026-04-12 15:30:00'
GROUP BY DATEADD(MINUTE, (DATEDIFF(MINUTE, 0, Timestamp) / 10) * 10, 0)
HAVING COUNT(*) > 0
ORDER BY Slot10m ASC
"@ | Format-Table -AutoSize
