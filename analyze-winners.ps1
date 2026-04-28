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

Write-Host "============================================================"
Write-Host "DATA-DRIVEN ENTRY LOGIC ANALYSIS"
Write-Host "Based on full TradeHistory (UserId=1)"
Write-Host "============================================================"

Write-Host ""
Write-Host "[1] OVERALL STATS (last 7 days, closed trades)"
$q1 = @'
SELECT
  COUNT(*) AS Total,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
  SUM(CASE WHEN PnL = 0 THEN 1 ELSE 0 END) AS Zero,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS NetPnL,
  CAST(AVG(CASE WHEN PnL > 0 THEN PnL ELSE NULL END) AS DECIMAL(10,4)) AS AvgWin,
  CAST(AVG(CASE WHEN PnL < 0 THEN PnL ELSE NULL END) AS DECIMAL(10,4)) AS AvgLoss
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
'@
Q $q1 | Format-List

Write-Host ""
Write-Host "[2] WIN RATE BY STRATEGY"
$q2 = @'
SELECT
  Strategy,
  COUNT(*) AS Trades,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net,
  CAST(AVG(PnL) AS DECIMAL(10,4)) AS AvgPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY Strategy
ORDER BY Net DESC
'@
Q $q2 | Format-Table -AutoSize

Write-Host ""
Write-Host "[3] WIN RATE BY HOUR OF DAY (KST)"
$q3 = @'
SELECT
  DATEPART(HOUR, EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time') AS HourKST,
  COUNT(*) AS Trades,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY DATEPART(HOUR, EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time')
ORDER BY HourKST
'@
Q $q3 | Format-Table -AutoSize

Write-Host ""
Write-Host "[4] WIN RATE BY SIDE (LONG vs SHORT)"
$q4 = @'
SELECT
  Side,
  COUNT(*) AS Trades,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY Side
ORDER BY Net DESC
'@
Q $q4 | Format-Table -AutoSize

Write-Host ""
Write-Host "[5] TOP 20 WINNERS (by total PnL)"
$q5 = @'
SELECT TOP 20
  Symbol, COUNT(*) AS N,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY Symbol
HAVING COUNT(*) >= 2
ORDER BY Net DESC
'@
Q $q5 | Format-Table -AutoSize

Write-Host ""
Write-Host "[6] WORST 20 LOSERS"
$q6 = @'
SELECT TOP 20
  Symbol, COUNT(*) AS N,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY Symbol
HAVING COUNT(*) >= 2
ORDER BY Net ASC
'@
Q $q6 | Format-Table -AutoSize

Write-Host ""
Write-Host "[7] EXIT REASON BREAKDOWN"
$q7 = @'
SELECT
  LEFT(ExitReason, 40) AS ExitReason,
  COUNT(*) AS N,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net,
  CAST(AVG(PnL) AS DECIMAL(10,4)) AS AvgPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY LEFT(ExitReason, 40)
ORDER BY Net DESC
'@
Q $q7 | Format-Table -AutoSize

Write-Host ""
Write-Host "[8] HOLDING DURATION vs WIN RATE"
$q8 = @'
SELECT
  CASE
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN 'A: 0-5min'
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN 'B: 5-15min'
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN 'C: 15-60min'
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 240 THEN 'D: 1-4hr'
    ELSE 'E: 4hr+'
  END AS Bucket,
  COUNT(*) AS N,
  CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinPct,
  CAST(SUM(PnL) AS DECIMAL(18,4)) AS Net,
  CAST(AVG(PnL) AS DECIMAL(10,4)) AS AvgPnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND EntryTime >= DATEADD(DAY,-7,GETUTCDATE()) AND ExitTime IS NOT NULL
GROUP BY CASE
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN 'A: 0-5min'
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN 'B: 5-15min'
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN 'C: 15-60min'
    WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 240 THEN 'D: 1-4hr'
    ELSE 'E: 4hr+'
  END
ORDER BY Bucket
'@
Q $q8 | Format-Table -AutoSize

Write-Host ""
Write-Host "[9] LARGEST WINS (top 10 single trades)"
$q9 = @'
SELECT TOP 10 EntryTime, Symbol, Strategy, Side,
  CAST(EntryPrice AS DECIMAL(18,8)) AS EntryP,
  CAST(ExitPrice AS DECIMAL(18,8)) AS ExitP,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  CAST(PnLPercent AS DECIMAL(8,2)) AS Roi,
  DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL > 0
ORDER BY PnL DESC
'@
Q $q9 | Format-Table -AutoSize

Write-Host ""
Write-Host "[10] LARGEST LOSSES (top 10 single trades)"
$q10 = @'
SELECT TOP 10 EntryTime, Symbol, Strategy, Side,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  CAST(PnLPercent AS DECIMAL(8,2)) AS Roi,
  DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin,
  LEFT(ExitReason, 30) AS ExitReason
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND PnL < 0
ORDER BY PnL ASC
'@
Q $q10 | Format-Table -AutoSize
