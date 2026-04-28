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

Write-Host "[A] BOIUSDT TradeHistory all"
$a = "SELECT TOP 30 EntryTime, ExitTime, Strategy, Side, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, holdingMinutes AS Hold, ExitReason, IsClosed FROM TradeHistory WHERE Symbol='BOIUSDT' AND UserId=1 ORDER BY EntryTime DESC"
Q $a | Format-Table -AutoSize -Wrap

Write-Host "`n[B] BOIUSDT Bot_Log"
$b = "SELECT TOP 20 EventTime, Direction, Allowed, LEFT(Reason,90) AS Reason FROM Bot_Log WHERE UserId=1 AND Symbol='BOIUSDT' ORDER BY EventTime DESC"
Q $b | Format-Table -AutoSize

Write-Host "`n[C] BOIUSDT FooterLogs"
$c = "SELECT TOP 30 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-6,GETUTCDATE()) AND Message LIKE '%BOIUSDT%' ORDER BY Timestamp DESC"
Q $c | Format-Table -AutoSize -Wrap

Write-Host "`n[D] PositionState BOIUSDT"
$d = "SELECT * FROM PositionState WHERE Symbol='BOIUSDT'"
Q $d | Format-List
