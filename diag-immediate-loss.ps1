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

Write-Host "[A] Last 6h closed trades - hold time + outcome (즉시손실 패턴 검증)"
$a = "SELECT TOP 60 Symbol, Strategy, holdingMinutes AS Hold_min, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_, CAST(((ExitPrice-EntryPrice)/EntryPrice*100) AS DECIMAL(8,3)) AS PriceChgPct, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, ExitReason FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-6,GETUTCDATE()) AND holdingMinutes <= 5 ORDER BY ExitTime DESC"
Q $a | Format-Table -AutoSize

Write-Host "[B] Hold buckets - 6h"
$b = "SELECT CASE WHEN holdingMinutes < 1 THEN '01.lt_1min' WHEN holdingMinutes < 3 THEN '02.1_3min' WHEN holdingMinutes < 5 THEN '03.3_5min' WHEN holdingMinutes < 10 THEN '04.5_10min' WHEN holdingMinutes < 30 THEN '05.10_30min' ELSE '06.over30' END AS Bucket, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinPct, CAST(AVG(PnLPercent) AS DECIMAL(8,2)) AS AvgRoi FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-6,GETUTCDATE()) GROUP BY CASE WHEN holdingMinutes < 1 THEN '01.lt_1min' WHEN holdingMinutes < 3 THEN '02.1_3min' WHEN holdingMinutes < 5 THEN '03.3_5min' WHEN holdingMinutes < 10 THEN '04.5_10min' WHEN holdingMinutes < 30 THEN '05.10_30min' ELSE '06.over30' END ORDER BY Bucket"
Q $b | Format-Table -AutoSize

Write-Host "[C] entries within 6h - first close ROE distribution per symbol (entry quality)"
$c = "WITH FirstCloses AS (SELECT Symbol, EntryTime, MIN(ExitTime) AS FirstExit, MIN(PnLPercent) AS WorstRoeImmediate FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-6,GETUTCDATE()) GROUP BY Symbol, EntryTime) SELECT Symbol, EntryTime, FirstExit, DATEDIFF(SECOND, EntryTime, FirstExit) AS Sec_to_first_exit, CAST(WorstRoeImmediate AS DECIMAL(8,2)) AS WorstRoeImmediate FROM FirstCloses ORDER BY EntryTime DESC"
Q $c | Format-Table -AutoSize -Wrap
