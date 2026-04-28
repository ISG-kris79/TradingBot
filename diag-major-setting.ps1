$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()

Write-Host "=========================================================="
Write-Host "  [1] GeneralSettings DB row"
Write-Host "=========================================================="
$cm = $cn.CreateCommand()
$cm.CommandTimeout = 30
$cm.CommandText = "SELECT TOP 1 EnableMajorTrading, MaxMajorSlots, MaxPumpSlots, MaxDailyEntries, UpdatedAt FROM GeneralSettings ORDER BY UpdatedAt DESC"
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
$ds = New-Object System.Data.DataSet
[void]$ap.Fill($ds)
$ds.Tables[0] | Format-List

Write-Host ""
Write-Host "=========================================================="
Write-Host "  [2] Recent SOL/BTC/ETH/XRP entries (last 6h)"
Write-Host "=========================================================="
$cm2 = $cn.CreateCommand()
$cm2.CommandTimeout = 30
$cm2.CommandText = "SELECT TOP 30 EntryTime, Symbol, Side, EntryPrice, Quantity, Source FROM TradeLogs WHERE Symbol IN ('SOLUSDT','BTCUSDT','ETHUSDT','XRPUSDT') AND EntryTime >= DATEADD(HOUR, -6, GETUTCDATE()) ORDER BY EntryTime DESC"
$ap2 = New-Object System.Data.SqlClient.SqlDataAdapter $cm2
$ds2 = New-Object System.Data.DataSet
try {
    [void]$ap2.Fill($ds2)
    $ds2.Tables[0] | Format-Table -AutoSize
} catch {
    Write-Host ("    Query failed: " + $_.Exception.Message)
}

Write-Host ""
Write-Host "=========================================================="
Write-Host "  [3] Recent FooterLogs (settings/MAJOR_DISABLED last 30m)"
Write-Host "=========================================================="
$cm3 = $cn.CreateCommand()
$cm3.CommandTimeout = 30
$cm3.CommandText = "SELECT TOP 50 LogTime, Message FROM FooterLogs WHERE LogTime >= DATEADD(MINUTE, -30, GETUTCDATE()) AND (Message LIKE '%MAJOR_DISABLED%' OR Message LIKE '%EnableMajor%' OR Message LIKE '%Settings%' OR Message LIKE '%SOLUSDT%') ORDER BY LogTime DESC"
$ap3 = New-Object System.Data.SqlClient.SqlDataAdapter $cm3
$ds3 = New-Object System.Data.DataSet
try {
    [void]$ap3.Fill($ds3)
    $ds3.Tables[0] | Format-Table -AutoSize -Wrap
} catch {
    Write-Host ("    Query failed: " + $_.Exception.Message)
}

$cn.Close()
