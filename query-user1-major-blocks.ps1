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

# FooterLogs 에는 UserId 필드 없음 → 로그는 섞여 있음
# 대신 최근 1시간만 보고 v5.0 태그 유무로 구분

Write-Host "=== [1] FooterLogs 최근 1h 메이저 ENTRY 로그 Type별 집계 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20
  CASE
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN 'VOLUME_BLOCK'
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN 'AI_GATE_BLOCK'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN 'MTF_GUARDIAN'
    WHEN Message LIKE '%GATE1%' THEN 'GATE1'
    WHEN Message LIKE '%GATE2%' THEN 'GATE2'
    WHEN Message LIKE '%AI_GATE%BYPASS%' THEN 'AI_GATE_BYPASS'
    WHEN Message LIKE '%SCHED%' THEN 'SCHEDULER'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN 'VOLATILITY_BLOCK'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN 'SLOT_BLOCK'
    WHEN Message LIKE '%DeadCat%' THEN 'DEADCAT'
    WHEN Message LIKE '%Sanity%' THEN 'SANITY'
    WHEN Message LIKE '%RSI_Overheat%' THEN 'RSI_OVERHEAT'
    WHEN Message LIKE '%Major_LONG_Threshold%' THEN 'MAJOR_LONG_THRESHOLD'
    WHEN Message LIKE '%FORECAST%' THEN 'FORECAST'
    ELSE 'OTHER'
  END AS Kind,
  COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%ENTRY%'
  AND (Message LIKE '%BTCUSDT%' OR Message LIKE '%ETHUSDT%'
       OR Message LIKE '%SOLUSDT%' OR Message LIKE '%XRPUSDT%' OR Message LIKE '%BNBUSDT%')
GROUP BY
  CASE
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN 'VOLUME_BLOCK'
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN 'AI_GATE_BLOCK'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN 'MTF_GUARDIAN'
    WHEN Message LIKE '%GATE1%' THEN 'GATE1'
    WHEN Message LIKE '%GATE2%' THEN 'GATE2'
    WHEN Message LIKE '%AI_GATE%BYPASS%' THEN 'AI_GATE_BYPASS'
    WHEN Message LIKE '%SCHED%' THEN 'SCHEDULER'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN 'VOLATILITY_BLOCK'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN 'SLOT_BLOCK'
    WHEN Message LIKE '%DeadCat%' THEN 'DEADCAT'
    WHEN Message LIKE '%Sanity%' THEN 'SANITY'
    WHEN Message LIKE '%RSI_Overheat%' THEN 'RSI_OVERHEAT'
    WHEN Message LIKE '%Major_LONG_Threshold%' THEN 'MAJOR_LONG_THRESHOLD'
    WHEN Message LIKE '%FORECAST%' THEN 'FORECAST'
    ELSE 'OTHER'
  END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 최근 30min FOREAST/SCHED 로그 (v5.0 경로 전용) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE())
  AND (Message LIKE '%FORECAST%' OR Message LIKE '%[SCHED]%' OR Message LIKE '%[v5.0]%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] MajorCoinStrategy 신호 로그 (최근 30m) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE())
  AND Message LIKE '%MAJOR%'
  AND (Message LIKE '%LONG%' OR Message LIKE '%SHORT%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] TickDensityMonitor / 틱 급증 로그 (최근 30m) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE())
  AND (Message LIKE '%틱급증%' OR Message LIKE '%SQUEEZE%' OR Message LIKE '%TICK_SURGE%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] MajorCoinStrategy aiScore 로그 (최근 30m) — AIScore 분포 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE())
  AND Message LIKE '%aiScore%'
  AND (Message LIKE '%BTCUSDT%' OR Message LIKE '%ETHUSDT%' OR Message LIKE '%SOLUSDT%' OR Message LIKE '%XRPUSDT%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
