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

Write-Host "[A] last 30 min trade exits (SL/TP fill chunks)"
$a = "SELECT TOP 50 EntryTime, ExitTime, Symbol, Side, Strategy, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(ExitPrice AS DECIMAL(18,6)) AS Exit_, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, ExitReason FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(MINUTE,-30,GETUTCDATE()) ORDER BY ExitTime DESC"
Q $a | Format-Table -AutoSize

Write-Host "`n[B] same-symbol multi-row exits (multi telegram suspect)"
$b = "SELECT Symbol, ExitReason, COUNT(*) AS Rows, COUNT(DISTINCT CAST(Quantity AS DECIMAL(18,4))) AS UniqueQty, COUNT(DISTINCT CAST(PnL AS DECIMAL(18,4))) AS UniquePnL, MIN(ExitTime) AS Start, MAX(ExitTime) AS End FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-2,GETUTCDATE()) GROUP BY Symbol, ExitReason HAVING COUNT(*) >= 3 ORDER BY MAX(ExitTime) DESC"
Q $b | Format-Table -AutoSize

Write-Host "`n[C] last 6h PnL summary"
$c = "SELECT COUNT(*) AS Trades, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS Losses, CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate, CAST(SUM(PnL) AS DECIMAL(18,2)) AS TotalPnL FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-6,GETUTCDATE())"
Q $c | Format-List

Write-Host "`n[D] FooterLogs Notify dedup activity (telegram path)"
$d = "SELECT TOP 30 Timestamp, LEFT(Message, 180) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETUTCDATE()) AND (Message LIKE '%[Notify]%' OR Message LIKE '%[Telegram]%' OR Message LIKE '%dedup%') ORDER BY Timestamp DESC"
Q $d | Format-Table -AutoSize -Wrap
