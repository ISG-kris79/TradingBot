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

Write-Host "=== [1] UserId=1 최근 진입 시각 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Symbol, Side, Strategy, EntryTime, IsClosed
FROM TradeHistory WHERE UserId=1
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 최근 3h ENTRY BLOCK 분포 (PUMP) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15
  CASE
    WHEN Message LIKE '%GATE1%BLOCK%' THEN '01_GATE1'
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN '02_AI_GATE'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN '03_MTF'
    WHEN Message LIKE '%COOLDOWN%' THEN '04_COOLDOWN'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN '05_SLOT'
    WHEN Message LIKE '%일일한도%' THEN '06_DAILY_LIMIT'
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN '07_VOLUME'
    WHEN Message LIKE '%BLACKLIST%BLOCK%' THEN '08_BLACKLIST'
    WHEN Message LIKE '%GUARD%BLOCK%' THEN '09_GUARD'
    WHEN Message LIKE '%side=WAIT%' THEN '10_WAIT'
    WHEN Message LIKE '%side=LONG%' THEN '11_LONG_SIGNAL'
    WHEN Message LIKE '%ENTRY%START%' THEN '12_ENTRY_START'
    WHEN Message LIKE '%ORDER%FILLED%' THEN '13_FILLED'
    ELSE '99_OTHER'
  END AS Kind,
  COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -3, GETDATE())
  AND Message LIKE '%ENTRY%'
  AND Message NOT LIKE '%BTCUSDT%' AND Message NOT LIKE '%ETHUSDT%'
  AND Message NOT LIKE '%SOLUSDT%' AND Message NOT LIKE '%XRPUSDT%'
GROUP BY CASE
    WHEN Message LIKE '%GATE1%BLOCK%' THEN '01_GATE1'
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN '02_AI_GATE'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN '03_MTF'
    WHEN Message LIKE '%COOLDOWN%' THEN '04_COOLDOWN'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN '05_SLOT'
    WHEN Message LIKE '%일일한도%' THEN '06_DAILY_LIMIT'
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN '07_VOLUME'
    WHEN Message LIKE '%BLACKLIST%BLOCK%' THEN '08_BLACKLIST'
    WHEN Message LIKE '%GUARD%BLOCK%' THEN '09_GUARD'
    WHEN Message LIKE '%side=WAIT%' THEN '10_WAIT'
    WHEN Message LIKE '%side=LONG%' THEN '11_LONG_SIGNAL'
    WHEN Message LIKE '%ENTRY%START%' THEN '12_ENTRY_START'
    WHEN Message LIKE '%ORDER%FILLED%' THEN '13_FILLED'
    ELSE '99_OTHER'
  END
ORDER BY Kind
"@ | Format-Table -AutoSize

Write-Host "=== [3] PumpScan LONG 신호 vs WAIT 비율 (최근 3h) ===" -ForegroundColor Cyan
Q @"
SELECT
    SUM(CASE WHEN Message LIKE '%side=LONG%' THEN 1 ELSE 0 END) AS LongSignals,
    SUM(CASE WHEN Message LIKE '%side=WAIT%' THEN 1 ELSE 0 END) AS WaitSignals,
    SUM(CASE WHEN Message LIKE '%MEGA_PUMP%' THEN 1 ELSE 0 END) AS MegaPump
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -3, GETDATE())
  AND Message LIKE '%[SIGNAL][PUMP]%'
"@ | Format-Table -AutoSize

Write-Host "=== [4] 최근 ENTRY START 로그 (최근 30분) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE())
  AND Message LIKE '%ENTRY%START%'
  AND Message NOT LIKE '%BTCUSDT%' AND Message NOT LIKE '%ETHUSDT%'
  AND Message NOT LIKE '%SOLUSDT%' AND Message NOT LIKE '%XRPUSDT%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] GATE1/COOLDOWN/BLACKLIST 차단 샘플 (최근 30분) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE())
  AND (Message LIKE '%GATE1%' OR Message LIKE '%COOLDOWN%' OR Message LIKE '%BLACKLIST%BLOCK%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [6] 일일한도 현재 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Message LIKE '%일일%카운터%' OR Message LIKE '%일일한도%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [7] 현재 활성 포지션 수 ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS OpenPositions
FROM TradeHistory WHERE UserId=1 AND IsClosed=0
"@ | Format-Table -AutoSize

Write-Host "=== [8] 봇 버전 확인 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 3 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Message LIKE '%v5.1%' OR Message LIKE '%v5.0%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize
