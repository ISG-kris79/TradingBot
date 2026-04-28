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

Write-Host "==== [1] EntryTime >= 새벽 1시 (KST) 인 trade 만 - 봇이 자동 진입한것 ===="
Write-Host "    KST 01:00 = UTC 16:00 전날" -ForegroundColor Gray
Write-Host ""
$q1 = @'
SELECT
  EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EntryKST,
  ExitTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS ExitKST,
  Symbol, Strategy, Side,
  CAST(EntryPrice AS DECIMAL(18,6)) AS Entry,
  CAST(ExitPrice AS DECIMAL(18,6)) AS Exit_,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  CAST(PnLPercent AS DECIMAL(8,2)) AS Roi,
  DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin,
  LEFT(ExitReason, 40) AS ExitReason
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-23 16:00:00'
ORDER BY EntryTime ASC
'@
Q $q1 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [2] 종합 통계 (1시 이후 진입한 봇 자동 trade) ===="
$q2 = @'
SELECT
  COUNT(*) AS TotalTrades,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(10,4)) AS NetPnL_Bot,
  CAST(SUM(CASE WHEN PnL > 0 THEN PnL ELSE 0 END) AS DECIMAL(10,4)) AS GrossWin,
  CAST(SUM(CASE WHEN PnL < 0 THEN PnL ELSE 0 END) AS DECIMAL(10,4)) AS GrossLoss
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-23 16:00:00'
'@
Q $q2 | Format-List

Write-Host ""
Write-Host "==== [3] Strategy 별 손익 (1시 이후 진입) ===="
$q3 = @'
SELECT
  Strategy,
  COUNT(*) AS Trades,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
  CAST(SUM(PnL) AS DECIMAL(10,4)) AS NetPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-23 16:00:00'
GROUP BY Strategy
ORDER BY NetPnL ASC
'@
Q $q3 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== [4] 1시 ~ 1시 30분 (수면 직후) 진입된 코인들 ===="
$q4 = @'
SELECT
  EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EntryKST,
  Symbol, Strategy, Side,
  CAST(EntryPrice AS DECIMAL(18,6)) AS Entry,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  ExitTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS ExitKST,
  LEFT(ExitReason, 40) AS ExitReason
FROM TradeHistory
WHERE UserId=1
  AND EntryTime >= '2026-04-23 16:00:00'
  AND EntryTime <= '2026-04-23 17:00:00'
ORDER BY EntryTime
'@
Q $q4 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] 새 진입이 가장 많이 일어난 시간대 (시간별) ===="
$q5 = @'
SELECT
  DATEPART(HOUR, EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time') AS HourKST,
  COUNT(*) AS Entries,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
  CAST(SUM(PnL) AS DECIMAL(10,4)) AS Net
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-23 16:00:00'
GROUP BY DATEPART(HOUR, EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time')
ORDER BY HourKST
'@
Q $q5 | Format-Table -AutoSize
