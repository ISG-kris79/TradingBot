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

Write-Host "=== [1] v5.0.1 배포 이후 GATE1/GATE2 로그 발생 여부 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE (Message LIKE '%[GATE1]%' OR Message LIKE '%[GATE2]%')
  AND Timestamp >= DATEADD(HOUR, -24, GETDATE())
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 최근 6시간 GATE1 차단 사유별 집계 ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN Message LIKE '%already_pumped%' THEN '1_already_pumped (30m >8%)'
        WHEN Message LIKE '%post_peak_falling%' THEN '2_post_peak_falling (조정 중)'
        WHEN Message LIKE '%long_upper_wick%' THEN '3_long_upper_wick'
        WHEN Message LIKE '%giant_bearish_candle%' THEN '4_giant_bearish_candle'
        WHEN Message LIKE '%midCapOverExtended%' THEN '5_midCapOverExtended (v5.0.6)'
        WHEN Message LIKE '%multipleBearishVolatility%' THEN '6_multipleBearishVolatility (v5.0.6)'
        ELSE 'OTHER'
    END AS Gate1Reason,
    COUNT(*) AS Cnt
FROM FooterLogs
WHERE Message LIKE '%[GATE1]%'
  AND Timestamp >= DATEADD(HOUR, -6, GETDATE())
GROUP BY
    CASE
        WHEN Message LIKE '%already_pumped%' THEN '1_already_pumped (30m >8%)'
        WHEN Message LIKE '%post_peak_falling%' THEN '2_post_peak_falling (조정 중)'
        WHEN Message LIKE '%long_upper_wick%' THEN '3_long_upper_wick'
        WHEN Message LIKE '%giant_bearish_candle%' THEN '4_giant_bearish_candle'
        WHEN Message LIKE '%midCapOverExtended%' THEN '5_midCapOverExtended (v5.0.6)'
        WHEN Message LIKE '%multipleBearishVolatility%' THEN '6_multipleBearishVolatility (v5.0.6)'
        ELSE 'OTHER'
    END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] GATE2 지연 진입 실행 여부 (최근 24h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE (Message LIKE '%[GATE2]%지연%' OR Message LIKE '%GATE2%조건 충족%' OR Message LIKE '%GATE2%만료%' OR Message LIKE '%_GATE2%')
  AND Timestamp >= DATEADD(HOUR, -24, GETDATE())
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 04-12 AIOT/SKYAI 진입 시점 Gate1 로그 존재 여부 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE (Message LIKE '%AIOTUSDT%' OR Message LIKE '%SKYAIUSDT%')
  AND (Message LIKE '%GATE%' OR Message LIKE '%MTF%' OR Message LIKE '%BLOCK%')
  AND Timestamp >= '2026-04-12 00:00:00' AND Timestamp < '2026-04-12 05:00:00'
ORDER BY Id DESC
"@ | Format-Table -AutoSize
