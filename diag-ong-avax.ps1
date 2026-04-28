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

Write-Host "[A] ONGUSDT 전체 trade history"
$a = "SELECT TOP 10 Strategy, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(ExitPrice AS DECIMAL(18,6)) AS Exit_, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(PnL AS DECIMAL(18,3)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct, holdingMinutes AS Hold, ExitReason, IsClosed, EntryTime FROM TradeHistory WHERE Symbol='ONGUSDT' AND UserId=1 ORDER BY EntryTime DESC"
Q $a | Format-Table -AutoSize -Wrap

Write-Host "`n[B] AVAXUSDT 전체 trade history"
$b = "SELECT TOP 10 Strategy, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(ExitPrice AS DECIMAL(18,6)) AS Exit_, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(PnL AS DECIMAL(18,3)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct, holdingMinutes AS Hold, ExitReason, IsClosed, EntryTime FROM TradeHistory WHERE Symbol='AVAXUSDT' AND UserId=1 ORDER BY EntryTime DESC"
Q $b | Format-Table -AutoSize -Wrap

Write-Host "`n[C] PositionState ONG/AVAX"
$c = "SELECT * FROM PositionState WHERE Symbol IN ('ONGUSDT','AVAXUSDT')"
Q $c | Format-Table -AutoSize

Write-Host "`n[D] Bot_Log ONG/AVAX 최근 24h"
$d = "SELECT TOP 30 EventTime, Symbol, Direction, Allowed, LEFT(Reason,80) AS Reason FROM Bot_Log WHERE UserId=1 AND Symbol IN ('ONGUSDT','AVAXUSDT') AND EventTime >= DATEADD(HOUR,-24,GETUTCDATE()) ORDER BY EventTime DESC"
Q $d | Format-Table -AutoSize -Wrap

Write-Host "`n[E] Bot_Log 최초 진입 시각 +- 30s ONG/AVAX (어떤 strategy로 들어왔는지)"
$e = "SELECT TOP 30 Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-26,GETUTCDATE()) AND (Message LIKE '%ONGUSDT%' OR Message LIKE '%AVAXUSDT%') AND (Message LIKE '%ENTRY%' OR Message LIKE '%진입%' OR Message LIKE '%감시%') ORDER BY Timestamp DESC"
Q $e | Format-Table -AutoSize -Wrap
