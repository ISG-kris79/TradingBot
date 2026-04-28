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
Write-Host "  [1] LABUSDT TradeLogs (latest)"
Write-Host "=========================================================="
$cm = $cn.CreateCommand()
$cm.CommandTimeout = 30
$cm.CommandText = "SELECT TOP 5 * FROM TradeLogs WHERE Symbol LIKE 'LAB%' ORDER BY [Time] DESC"
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
$ds = New-Object System.Data.DataSet
[void]$ap.Fill($ds)
$ds.Tables[0] | Format-List

Write-Host ""
Write-Host "=========================================================="
Write-Host "  [2] ActivePositions table (LAB*)"
Write-Host "=========================================================="
$cm2 = $cn.CreateCommand()
$cm2.CommandTimeout = 30
try {
    $cm2.CommandText = "SELECT * FROM ActivePositions WHERE Symbol LIKE 'LAB%'"
    $ap2 = New-Object System.Data.SqlClient.SqlDataAdapter $cm2
    $ds2 = New-Object System.Data.DataSet
    [void]$ap2.Fill($ds2)
    $ds2.Tables[0] | Format-List
} catch {
    Write-Host ("    Query fail: " + $_.Exception.Message)
}

$cn.Close()
