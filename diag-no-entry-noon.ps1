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

Write-Host "==== [1] 12시부터 진입 시도 (Bot_Log) Allowed/Reason 분포 ===="
$q1 = @'
SELECT
  Allowed,
  LEFT(Reason, 60) AS Reason,
  COUNT(*) AS Cnt,
  MIN(EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time') AS FirstKST,
  MAX(EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time') AS LastKST
FROM Bot_Log
WHERE UserId=1
  AND EventTime >= DATEADD(HOUR, -16, GETUTCDATE())
GROUP BY Allowed, LEFT(Reason, 60)
ORDER BY Cnt DESC
'@
Q $q1 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== [2] 12시 이후 ENGINE_151 / BOOTSTRAP / ALGO_CLEANUP 로그 (FooterLogs) ===="
$q2 = @'
SELECT TOP 30 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -16, GETUTCDATE())
  AND (Message LIKE '%ENGINE_151%' OR Message LIKE '%BOOTSTRAP%' OR Message LIKE '%ALGO_CLEANUP%' OR Message LIKE '%[L1]%' OR Message LIKE '%[L2]%' OR Message LIKE '%[L3]%' OR Message LIKE '%v5.17%')
ORDER BY Timestamp DESC
'@
Q $q2 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [3] 봇 시작 로그 (StartScanning) ===="
$q3 = @'
SELECT TOP 5 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -24, GETUTCDATE())
  AND (Message LIKE '%엔진 가동%' OR Message LIKE '%엔진 시작%' OR Message LIKE '%StartScanning%' OR Message LIKE '%4-variant%')
ORDER BY Timestamp DESC
'@
Q $q3 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [4] 12시 이후 PUMP/MAJOR 신호 로그 (감시 등록) ===="
$q4 = @'
SELECT TOP 20 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -16, GETUTCDATE())
  AND (Message LIKE '%감시등록%' OR Message LIKE '%CANDIDATE%' OR Message LIKE '%[SIGNAL][PUMP]%' OR Message LIKE '%[ML.NET]%')
ORDER BY Timestamp DESC
'@
Q $q4 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] LocalAppData 모델 zip 파일 상태 ===="
$ModelDir = Join-Path $env:LOCALAPPDATA "TradingBot\Models"
$req = @("EntryTimingModel.zip","EntryTimingModel_Major.zip","EntryTimingModel_Pump.zip","EntryTimingModel_Spike.zip")
foreach ($n in $req) {
    $p = Join-Path $ModelDir $n
    if (Test-Path $p) {
        $f = Get-Item $p
        Write-Host ("  ✅ {0,-35} {1,8}KB | {2}" -f $n, [math]::Round($f.Length/1024,0), $f.LastWriteTime) -ForegroundColor Green
    } else {
        Write-Host ("  ❌ {0,-35} 없음" -f $n) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "==== [6] 12시 이후 새 진입 trade (TradeHistory) ===="
$q6 = @'
SELECT
  EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EntryKST,
  Symbol, Strategy, Side,
  CAST(EntryPrice AS DECIMAL(18,6)) AS Entry,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  ExitTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS ExitKST,
  LEFT(ExitReason, 30) AS ExitReason
FROM TradeHistory
WHERE UserId=1
  AND EntryTime >= DATEADD(HOUR, -16, GETUTCDATE())
ORDER BY EntryTime DESC
'@
Q $q6 | Format-Table -AutoSize
