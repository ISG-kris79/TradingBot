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

Write-Host "[A] hourly trade summary - yesterday + today (UTC)"
$a = "SELECT CAST(ExitTime AS DATE) AS Day, DATEPART(HOUR, ExitTime) AS Hr, COUNT(*) AS N, CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate, CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL, CAST(AVG(PnLPercent) AS DECIMAL(8,2)) AS AvgRoi FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-36,GETUTCDATE()) GROUP BY CAST(ExitTime AS DATE), DATEPART(HOUR, ExitTime) ORDER BY Day, Hr"
Q $a | Format-Table -AutoSize

Write-Host "[B] BTC schema check"
$b = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='CandleHistory' ORDER BY ORDINAL_POSITION"
Q $b | Format-Table -AutoSize

Write-Host "[C] strategy breakdown - yesterday vs today UTC noon split"
$c = "SELECT CASE WHEN ExitTime < DATEADD(HOUR,-12,GETUTCDATE()) THEN 'Earlier' ELSE 'Recent12h' END AS Period, Strategy, COUNT(*) AS N, CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate, CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-36,GETUTCDATE()) GROUP BY CASE WHEN ExitTime < DATEADD(HOUR,-12,GETUTCDATE()) THEN 'Earlier' ELSE 'Recent12h' END, Strategy ORDER BY Period, PnL ASC"
Q $c | Format-Table -AutoSize

Write-Host "[D] LONG vs SHORT trades - last 36h (downtrend = SHORT 유리)"
$d = "SELECT Side, COUNT(*) AS N, CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate, CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-36,GETUTCDATE()) GROUP BY Side"
Q $d | Format-Table -AutoSize

Write-Host "[E] top 10 lossers last 36h"
$e = "SELECT TOP 10 Symbol, Side, Strategy, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(ExitPrice AS DECIMAL(18,6)) AS Exit_, CAST(PnL AS DECIMAL(18,3)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct, holdingMinutes AS Hold, ExitReason, EntryTime FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-36,GETUTCDATE()) ORDER BY PnL ASC"
Q $e | Format-Table -AutoSize -Wrap

Write-Host "[F] PnL distribution by hour group"
$f = "SELECT CASE WHEN ExitTime < DATEADD(HOUR,-12,GETUTCDATE()) THEN 'Earlier24h' ELSE 'Recent12h' END AS Period, CASE WHEN PnLPercent < -10 THEN '01.<-10pct' WHEN PnLPercent < -5 THEN '02.-10to-5' WHEN PnLPercent < -1 THEN '03.-5to-1' WHEN PnLPercent < 1 THEN '04.flat' WHEN PnLPercent < 5 THEN '05.+1to+5' WHEN PnLPercent < 10 THEN '06.+5to+10' ELSE '07.>+10pct' END AS Bucket, COUNT(*) AS Cnt, CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-36,GETUTCDATE()) GROUP BY CASE WHEN ExitTime < DATEADD(HOUR,-12,GETUTCDATE()) THEN 'Earlier24h' ELSE 'Recent12h' END, CASE WHEN PnLPercent < -10 THEN '01.<-10pct' WHEN PnLPercent < -5 THEN '02.-10to-5' WHEN PnLPercent < -1 THEN '03.-5to-1' WHEN PnLPercent < 1 THEN '04.flat' WHEN PnLPercent < 5 THEN '05.+1to+5' WHEN PnLPercent < 10 THEN '06.+5to+10' ELSE '07.>+10pct' END ORDER BY Period, Bucket"
Q $f | Format-Table -AutoSize
