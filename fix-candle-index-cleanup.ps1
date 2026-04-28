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

function Run($name, $sql, $timeout) {
    $cm = $cn.CreateCommand()
    $cm.CommandTimeout = $timeout
    $cm.CommandText = $sql
    $rows = $cm.ExecuteNonQuery()
    Write-Host ("[OK] " + $name + " : " + $rows + " rows affected")
}

Write-Host "STEP 1 - Add index on CandleData (IntervalText, OpenTime DESC)"
$sqlIdx = @'
IF OBJECT_ID('dbo.CandleData') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CandleData') AND name = 'IX_CandleData_IntervalText_OpenTime_DESC')
BEGIN
    CREATE INDEX IX_CandleData_IntervalText_OpenTime_DESC
        ON dbo.CandleData (IntervalText, OpenTime DESC) WITH (ONLINE = OFF);
END
'@
Run "Index" $sqlIdx 600

Write-Host ""
Write-Host "STEP 2 - Delete CandleData older than 30 days (5m, 15m only)"
$sqlA = "DELETE FROM CandleData WHERE IntervalText IN ('5m','15m') AND OpenTime < DATEADD(DAY, -30, GETUTCDATE())"
Run "CandleData cleanup" $sqlA 600

Write-Host ""
Write-Host "STEP 3 - Delete MarketCandles older than 30 days"
$sqlB = "DELETE FROM MarketCandles WHERE OpenTime < DATEADD(DAY, -30, GETUTCDATE())"
Run "MarketCandles cleanup" $sqlB 600

Write-Host ""
Write-Host "STEP 4 - Delete CandleHistory older than 30 days"
$sqlC = "DELETE FROM CandleHistory WHERE [Interval] IN ('5m','15m') AND OpenTime < DATEADD(DAY, -30, GETUTCDATE())"
Run "CandleHistory cleanup" $sqlC 600

Write-Host ""
Write-Host "STEP 5 - Verify CandleData distribution (last 30 days)"
$cm5 = $cn.CreateCommand()
$cm5.CommandTimeout = 60
$cm5.CommandText = "SELECT IntervalText, COUNT(*) AS RowsKept FROM CandleData WITH (NOLOCK) WHERE OpenTime >= DATEADD(DAY, -30, GETUTCDATE()) GROUP BY IntervalText ORDER BY RowsKept DESC"
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm5
$ds = New-Object System.Data.DataSet
[void]$ap.Fill($ds)
$ds.Tables[0] | Format-Table -AutoSize

$cn.Close()
Write-Host "DONE - Index added + 30+ day data cleaned"
