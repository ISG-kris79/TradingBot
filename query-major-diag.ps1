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

Write-Host "=== [1] 최근 1h 메이저 ENTRY 관련 로그 (Type별 집계) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15
  CASE
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN '01_AI_GATE_BLOCK'
    WHEN Message LIKE '%AI_GATE%BYPASS%' THEN '02_AI_GATE_BYPASS'
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN '03_VOLUME_BLOCK'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN '04_MTF_GUARDIAN'
    WHEN Message LIKE '%GATE1%BLOCK%' THEN '05_GATE1_BLOCK'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN '06_SLOT_BLOCK'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN '07_VOLATILITY'
    WHEN Message LIKE '%Major_LONG_Threshold%' THEN '08_MAJOR_THRESHOLD'
    WHEN Message LIKE '%Major_SHORT_Threshold%' THEN '09_MAJOR_SHORT_TH'
    WHEN Message LIKE '%Dual_Reject%' THEN '10_DUAL_REJECT'
    WHEN Message LIKE '%ORDER%FILLED%' THEN '11_ORDER_FILLED'
    WHEN Message LIKE '%FORECAST%' THEN '12_FORECAST'
    WHEN Message LIKE '%Fallback%' THEN '13_FALLBACK'
    WHEN Message LIKE '%ENTRY%START%' THEN '14_ENTRY_START'
    ELSE '99_OTHER'
  END AS Kind,
  COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND (Message LIKE '%BTCUSDT%' OR Message LIKE '%ETHUSDT%'
       OR Message LIKE '%SOLUSDT%' OR Message LIKE '%XRPUSDT%')
  AND Message LIKE '%ENTRY%'
GROUP BY
  CASE
    WHEN Message LIKE '%AI_GATE%BLOCK%' THEN '01_AI_GATE_BLOCK'
    WHEN Message LIKE '%AI_GATE%BYPASS%' THEN '02_AI_GATE_BYPASS'
    WHEN Message LIKE '%VOLUME%BLOCK%' THEN '03_VOLUME_BLOCK'
    WHEN Message LIKE '%MTF_GUARDIAN%BLOCK%' THEN '04_MTF_GUARDIAN'
    WHEN Message LIKE '%GATE1%BLOCK%' THEN '05_GATE1_BLOCK'
    WHEN Message LIKE '%SLOT%BLOCK%' THEN '06_SLOT_BLOCK'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN '07_VOLATILITY'
    WHEN Message LIKE '%Major_LONG_Threshold%' THEN '08_MAJOR_THRESHOLD'
    WHEN Message LIKE '%Major_SHORT_Threshold%' THEN '09_MAJOR_SHORT_TH'
    WHEN Message LIKE '%Dual_Reject%' THEN '10_DUAL_REJECT'
    WHEN Message LIKE '%ORDER%FILLED%' THEN '11_ORDER_FILLED'
    WHEN Message LIKE '%FORECAST%' THEN '12_FORECAST'
    WHEN Message LIKE '%Fallback%' THEN '13_FALLBACK'
    WHEN Message LIKE '%ENTRY%START%' THEN '14_ENTRY_START'
    ELSE '99_OTHER'
  END
ORDER BY Kind
"@ | Format-Table -AutoSize

Write-Host "=== [2] MajorCoinStrategy LONG/SHORT 신호 발생 여부 (최근 1h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%MAJOR%'
  AND (Message LIKE '%LONG%AI%' OR Message LIKE '%SHORT%AI%' OR Message LIKE '%aiScore%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] Forecaster 관련 로그 (최근 1h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND (Message LIKE '%FORECAST%MAJOR%' OR Message LIKE '%MajorForecaster%' OR Message LIKE '%Fallback%AccA%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 최근 1h 메이저 ENTRY START 신호 (MajorCoinStrategy 방향 결정) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%ENTRY%START%'
  AND Message LIKE '%src=MAJOR%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] 최근 3시간 droughts 진단 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -3, GETDATE())
  AND Message LIKE '%드라이스펠%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [6] v5.0.7 MajorForecaster 최근 학습 결과 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs
WHERE Message LIKE '%MajorForecaster%학습%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [7] UserId=1 메이저 마지막 진입 확인 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, MAX(EntryTime) AS LastEntry,
       DATEDIFF(HOUR, MAX(EntryTime), GETDATE()) AS HoursAgo
FROM TradeHistory
WHERE UserId=1
  AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT')
GROUP BY Symbol
ORDER BY LastEntry DESC
"@ | Format-Table -AutoSize
