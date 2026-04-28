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

$cutoff = "2026-04-23 03:00:00"
Write-Host "[A] entries since $cutoff UTC (= KST noon)"
$a = "SELECT EntryTime, Symbol, Side, Strategy, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(Quantity AS DECIMAL(18,4)) AS Qty FROM TradeHistory WHERE UserId=1 AND IsSimulation=0 AND EntryTime >= '$cutoff' ORDER BY EntryTime DESC"
Q $a | Format-Table -AutoSize

Write-Host "[B] entry COUNT by hour - last 12h"
$b = "SELECT DATEPART(HOUR, EntryTime) AS Hr, COUNT(*) AS N, COUNT(DISTINCT Symbol) AS UniqSym, STRING_AGG(DISTINCT Strategy, ',') AS Strats FROM TradeHistory WHERE UserId=1 AND IsSimulation=0 AND EntryTime >= DATEADD(HOUR,-12,GETUTCDATE()) GROUP BY DATEPART(HOUR, EntryTime) ORDER BY Hr"
Q $b | Format-Table -AutoSize -Wrap

Write-Host "[C] Bot_Log decisions hourly - last 12h (Allowed counts)"
$c = "SELECT DATEPART(HOUR, EventTime) AS Hr, SUM(CASE WHEN Allowed=1 THEN 1 ELSE 0 END) AS AllowCnt, SUM(CASE WHEN Allowed=0 THEN 1 ELSE 0 END) AS RejectCnt, COUNT(*) AS Total FROM Bot_Log WHERE UserId=1 AND EventTime >= DATEADD(HOUR,-12,GETUTCDATE()) GROUP BY DATEPART(HOUR, EventTime) ORDER BY Hr"
Q $c | Format-Table -AutoSize

Write-Host "[D] Bot_Log Allowed=True since noon (실제 진입 승인)"
$d = "SELECT TOP 50 EventTime, Symbol, Direction, Reason FROM Bot_Log WHERE UserId=1 AND Allowed=1 AND EventTime >= '$cutoff' ORDER BY EventTime DESC"
Q $d | Format-Table -AutoSize -Wrap

Write-Host "[E] Bot_Log top reject reasons since noon"
$e = "SELECT TOP 20 LEFT(Reason, 80) AS RejectReason, COUNT(*) AS Cnt FROM Bot_Log WHERE UserId=1 AND Allowed=0 AND EventTime >= '$cutoff' GROUP BY LEFT(Reason, 80) ORDER BY Cnt DESC"
Q $e | Format-Table -AutoSize
