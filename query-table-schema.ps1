function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== TradeHistory 컬럼 ===" -ForegroundColor Cyan
Q @"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='TradeHistory'
ORDER BY ORDINAL_POSITION
"@ | Format-Table -AutoSize

Write-Host "=== TradeLogs 컬럼 ===" -ForegroundColor Cyan
Q @"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='TradeLogs'
ORDER BY ORDINAL_POSITION
"@ | Format-Table -AutoSize

Write-Host "=== TradeHistory 샘플 (청산건) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 3 Id, UserId, Symbol, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent,
    Strategy, LEFT(ExitReason,40) AS ExitReason, EntryTime, ExitTime, IsClosed, LEFT(Category,10) AS Cat
FROM TradeHistory WHERE IsClosed=1 AND ExitTime > '2026-04-14'
ORDER BY ExitTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== TradeLogs 샘플 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 3 * FROM TradeLogs ORDER BY Id DESC
"@ | Format-Table -AutoSize
