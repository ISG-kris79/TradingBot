$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
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

function BatchDelete($name, $sqlTemplate) {
    $totalDeleted = 0
    $batchNum = 0
    while ($true) {
        $batchNum++
        $cm = $cn.CreateCommand()
        $cm.CommandTimeout = 300
        $cm.CommandText = $sqlTemplate
        try {
            $rows = $cm.ExecuteNonQuery()
        } catch {
            Write-Host ("[ERR] " + $name + " batch " + $batchNum + " : " + $_.Exception.Message)
            break
        }
        $totalDeleted += $rows
        Write-Host ("[BATCH " + $batchNum + "] " + $name + " : deleted " + $rows + " rows (total: " + $totalDeleted + ")")
        if ($rows -lt 50000) { break }
    }
    Write-Host ("[DONE] " + $name + " : total " + $totalDeleted + " rows deleted")
}

Write-Host "STEP 1 - CandleData (5m, 15m) older than 30 days - BATCH DELETE"
$sqlA = "DELETE TOP (50000) FROM CandleData WHERE IntervalText IN ('5m','15m') AND OpenTime < DATEADD(DAY, -30, GETUTCDATE())"
BatchDelete "CandleData" $sqlA

Write-Host ""
Write-Host "STEP 2 - MarketCandles older than 30 days - BATCH DELETE"
$sqlB = "DELETE TOP (50000) FROM MarketCandles WHERE OpenTime < DATEADD(DAY, -30, GETUTCDATE())"
BatchDelete "MarketCandles" $sqlB

Write-Host ""
Write-Host "STEP 3 - CandleHistory (5m, 15m) older than 30 days - BATCH DELETE"
$sqlC = "DELETE TOP (50000) FROM CandleHistory WHERE [Interval] IN ('5m','15m') AND OpenTime < DATEADD(DAY, -30, GETUTCDATE())"
BatchDelete "CandleHistory" $sqlC

Write-Host ""
Write-Host "STEP 4 - Verify CandleData distribution (last 30 days)"
$cm5 = $cn.CreateCommand()
$cm5.CommandTimeout = 120
$cm5.CommandText = "SELECT IntervalText, COUNT(*) AS RowsKept FROM CandleData WITH (NOLOCK) WHERE OpenTime >= DATEADD(DAY, -30, GETUTCDATE()) GROUP BY IntervalText ORDER BY RowsKept DESC"
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm5
$ds = New-Object System.Data.DataSet
[void]$ap.Fill($ds)
$ds.Tables[0] | Format-Table -AutoSize

$cn.Close()
Write-Host "DONE - 30+ day data cleaned in batches"
