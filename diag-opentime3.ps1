$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$enc = $json.ConnectionStrings.DefaultConnection
$k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
$f=[Convert]::FromBase64String($enc);$a=[System.Security.Cryptography.Aes]::Create();$a.Key=$k
$iv=New-Object byte[] $a.IV.Length;$c=New-Object byte[] ($f.Length-$a.IV.Length)
[Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length);[Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length)
$a.IV=$iv;$d=$a.CreateDecryptor($a.Key,$a.IV)
$cs=[Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length));$a.Dispose();$d.Dispose()

function Q($sql,$timeout=30) {
    $cn=New-Object System.Data.SqlClient.SqlConnection $cs;$cn.Open()
    $cm=$cn.CreateCommand();$cm.CommandText=$sql;$cm.CommandTimeout=$timeout
    $ap=New-Object System.Data.SqlClient.SqlDataAdapter $cm;$ds=New-Object System.Data.DataSet;[void]$ap.Fill($ds);$cn.Close()
    return $ds.Tables[0]
}

Write-Host '=== CandleData 이상 OpenTime (< 1753-01-01) ===' -ForegroundColor Red
Q @"
SELECT COUNT(*) AS BadCount FROM CandleData
WHERE OpenTime < '1753-01-01' OR OpenTime IS NULL
"@ | Format-Table -AutoSize

Write-Host '=== MarketData 이상 OpenTime ===' -ForegroundColor Red
Q @"
SELECT COUNT(*) AS BadCount FROM MarketData
WHERE OpenTime < '1753-01-01' OR OpenTime IS NULL
"@ | Format-Table -AutoSize

Write-Host '=== CandleData MIN/MAX OpenTime ===' -ForegroundColor Cyan
Q @"
SELECT MIN(CAST(OpenTime AS DATETIME2)) AS MinOT, MAX(CAST(OpenTime AS DATETIME2)) AS MaxOT, COUNT(*) AS Total
FROM CandleData
"@ | Format-Table -AutoSize

Write-Host '=== MarketData MIN/MAX OpenTime ===' -ForegroundColor Cyan
Q @"
SELECT MIN(CAST(OpenTime AS DATETIME2)) AS MinOT, MAX(CAST(OpenTime AS DATETIME2)) AS MaxOT, COUNT(*) AS Total
FROM MarketData
"@ | Format-Table -AutoSize

Write-Host '=== AI prob 0% 원인 - model file 상태 ===' -ForegroundColor Yellow
Q @"
SELECT TOP 5 Timestamp, LEFT(Message,350) AS Msg
FROM FooterLogs
WHERE Message LIKE '%pump_signal%' OR Message LIKE '%model%' OR Message LIKE '%학습%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize
