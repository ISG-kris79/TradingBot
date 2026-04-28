$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}

Write-Host "[1] TAOUSDT 30일 전체 트레이드 통계" -ForegroundColor Yellow
$sql1 = @'
SELECT
    COUNT(*) AS Trades,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS Losses,
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS TotalPnL,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(AVG(PnLPercent) AS DECIMAL(8,2)) AS AvgRoiPct,
    AVG(holdingMinutes) AS AvgHoldMin
FROM TradeHistory
WHERE Symbol='TAOUSDT' AND UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-30,GETUTCDATE())
'@
Q $sql1 | Format-List

Write-Host "`n[2] TAOUSDT 진입 횟수 (Strategy/SignalSource별) 30일" -ForegroundColor Yellow
$sql2 = @'
SELECT Strategy, COUNT(*) AS Cnt,
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL
FROM TradeHistory
WHERE Symbol='TAOUSDT' AND UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-30,GETUTCDATE())
GROUP BY Strategy ORDER BY Cnt DESC
'@
Q $sql2 | Format-Table -AutoSize

Write-Host "`n[3] TAOUSDT 30일 일별 진입 횟수" -ForegroundColor Yellow
$sql3 = @'
SELECT CAST(EntryTime AS DATE) AS Day, COUNT(*) AS Entries,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS DayPnL
FROM TradeHistory
WHERE Symbol='TAOUSDT' AND UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-30,GETUTCDATE())
GROUP BY CAST(EntryTime AS DATE)
ORDER BY Day DESC
'@
Q $sql3 | Format-Table -AutoSize

Write-Host "`n[4] TAOUSDT 최근 20건" -ForegroundColor Yellow
$sql4 = @'
SELECT TOP 20 Strategy,
    CAST(EntryPrice AS DECIMAL(18,4)) AS Entry,
    CAST(ExitPrice AS DECIMAL(18,4)) AS Exit_,
    CAST(PnL AS DECIMAL(18,3)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    holdingMinutes AS HoldMin,
    ExitReason, EntryTime
FROM TradeHistory
WHERE Symbol='TAOUSDT' AND UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-30,GETUTCDATE())
ORDER BY ExitTime DESC
'@
Q $sql4 | Format-Table -AutoSize

Write-Host "`n[5] TAOUSDT vs 다른 심볼 (30일 누적 손실 TOP 10)" -ForegroundColor Yellow
$sql5 = @'
SELECT TOP 10 Symbol, COUNT(*) AS Cnt,
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-30,GETUTCDATE())
GROUP BY Symbol
ORDER BY PnL ASC
'@
Q $sql5 | Format-Table -AutoSize
