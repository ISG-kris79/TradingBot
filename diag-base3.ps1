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

Write-Host "[A] BASE pattern in TradeHistory"
$a = "SELECT DISTINCT Symbol FROM TradeHistory WHERE Symbol LIKE '%BASE%'"
Q $a | Format-Table -AutoSize

Write-Host "[B] BASE pattern in PositionState"
$b = "SELECT * FROM PositionState WHERE Symbol LIKE '%BASE%'"
Q $b | Format-Table -AutoSize

Write-Host "[C] zero-qty entries 24h"
$c = "SELECT TOP 30 Symbol, Strategy, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(Quantity AS DECIMAL(18,6)) AS Qty, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct, ExitReason, EntryTime FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND ExitTime >= DATEADD(HOUR,-24,GETUTCDATE()) AND Quantity < 0.001 ORDER BY EntryTime DESC"
Q $c | Format-Table -AutoSize

Write-Host "[D] all 24h entries qty + notional"
$d = "SELECT TOP 50 Symbol, CAST(Quantity AS DECIMAL(18,6)) AS Qty, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(EntryPrice*Quantity AS DECIMAL(18,2)) AS NotionalUSDT, Strategy, EntryTime FROM TradeHistory WHERE UserId=1 AND IsSimulation=0 AND EntryTime >= DATEADD(HOUR,-24,GETUTCDATE()) ORDER BY EntryTime DESC"
Q $d | Format-Table -AutoSize
