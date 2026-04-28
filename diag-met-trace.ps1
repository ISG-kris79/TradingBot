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

Write-Host "[1] METUSDT 진입 시각 (첫 EntryTime)"
$sql1 = "SELECT TOP 3 CAST(EntryTime AS DATETIME2(3)) AS EntryT, Strategy, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry FROM TradeHistory WHERE Symbol='METUSDT' AND UserId=1 ORDER BY EntryTime ASC"
Q $sql1 | Format-Table -AutoSize

Write-Host "[2] 동일 시각 +-2min 봇 진입 시도 로그 (Bot_Log)"
$sql2 = "SELECT TOP 30 EventTime, Symbol, Direction, Allowed, Reason FROM Bot_Log WHERE UserId=1 AND (Symbol='METUSDT' OR Symbol LIKE 'MET%') AND EventTime >= DATEADD(MINUTE,-5, (SELECT MIN(EntryTime) FROM TradeHistory WHERE Symbol='METUSDT')) AND EventTime <= DATEADD(MINUTE,5, (SELECT MAX(EntryTime) FROM TradeHistory WHERE Symbol='METUSDT')) ORDER BY EventTime"
Q $sql2 | Format-Table -AutoSize

Write-Host "[3] 동일 시각 다른 심볼 봇 진입 (TICK_SURGE/SPIKE_FAST 전체)"
$sql3 = "SELECT TOP 40 CAST(EntryTime AS DATETIME2(0)) AS EntryT, Symbol, Strategy, Side, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry FROM TradeHistory WHERE UserId=1 AND EntryTime >= (SELECT DATEADD(MINUTE,-2, MIN(EntryTime)) FROM TradeHistory WHERE Symbol='METUSDT') AND EntryTime <= (SELECT DATEADD(MINUTE,2, MAX(EntryTime)) FROM TradeHistory WHERE Symbol='METUSDT') ORDER BY EntryTime, Symbol"
Q $sql3 | Format-Table -AutoSize

Write-Host "[4] EXTERNAL_POSITION_INCREASE_SYNC 건수 분포 24h (심볼별)"
$sql4 = "SELECT Symbol, COUNT(*) AS Cnt, COUNT(DISTINCT EntryPrice) AS UniqueEntries, CAST(MIN(EntryTime) AS DATETIME2(0)) AS FirstT, CAST(MAX(EntryTime) AS DATETIME2(0)) AS LastT FROM TradeHistory WHERE UserId=1 AND Strategy='EXTERNAL_POSITION_INCREASE_SYNC' AND EntryTime >= DATEADD(HOUR,-36,GETUTCDATE()) GROUP BY Symbol ORDER BY Cnt DESC"
Q $sql4 | Format-Table -AutoSize
