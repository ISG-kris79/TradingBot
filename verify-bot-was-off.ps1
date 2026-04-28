$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection)
    $cn.Open()
    $cm = $cn.CreateCommand()
    $cm.CommandText = $sql
    $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet
    [void]$ap.Fill($ds)
    $cn.Close()
    return $ds.Tables[0]
}

Write-Host "==== [1] 1:40 ~ 2:00 KST FooterLogs (봇 동작 여부) ===="
$q1 = @'
SELECT TOP 50 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 180) AS Msg
FROM FooterLogs
WHERE Timestamp >= '2026-04-23 16:40:00'
  AND Timestamp <= '2026-04-23 17:00:00'
ORDER BY Timestamp ASC
'@
Q $q1 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [2] BASUSDT 1:48 진입 직전/직후 로그 (1:47:50 ~ 1:48:30) ===="
$q2 = @'
SELECT Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= '2026-04-23 16:47:50'
  AND Timestamp <= '2026-04-23 16:48:30'
ORDER BY Timestamp ASC
'@
Q $q2 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [3] BASUSDT TradeHistory 진입 ExitReason 상세 ===="
$q3 = @'
SELECT TOP 20 Id,
  EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EntryKST,
  ExitTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS ExitKST,
  Strategy, Side, EntryPrice,
  CAST(Quantity AS DECIMAL(18,4)) AS Qty,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  ExitReason
FROM TradeHistory
WHERE Symbol='BASUSDT' AND UserId=1
  AND EntryTime BETWEEN '2026-04-23 15:00:00' AND '2026-04-23 17:00:00'
ORDER BY EntryTime, ExitTime
'@
Q $q3 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [4] 봇 시작/종료 이벤트 검색 (12시간 내) ===="
$q4 = @'
SELECT TOP 30 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -12, GETUTCDATE())
  AND (Message LIKE '%엔진 시작%' OR Message LIKE '%엔진 가동%' OR Message LIKE '%엔진 종료%'
    OR Message LIKE '%봇 시작%' OR Message LIKE '%봇 종료%' OR Message LIKE '%StartScanning%'
    OR Message LIKE '%재시작%' OR Message LIKE '%shutdown%' OR Message LIKE '%종료%')
ORDER BY Timestamp DESC
'@
Q $q4 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] 1시대 GRASSUSDT/BASUSDT/UBUSDT EntryReason 정확 분류 ===="
Write-Host "    EXTERNAL_POSITION_INCREASE_SYNC = 봇이 매수 안 함, Binance 외부 (수동/알고)" -ForegroundColor Yellow
Write-Host "    PUMP_WATCH_CONFIRMED / TICK_SURGE / MAJOR_MEME_STAIRCASE = 봇 자체 매수" -ForegroundColor Yellow
$q5 = @'
SELECT
  Symbol, Strategy,
  COUNT(*) AS Records
FROM TradeHistory
WHERE UserId=1
  AND EntryTime BETWEEN '2026-04-23 16:00:00' AND '2026-04-23 17:00:00'
GROUP BY Symbol, Strategy
ORDER BY Symbol, Strategy
'@
Q $q5 | Format-Table -AutoSize
