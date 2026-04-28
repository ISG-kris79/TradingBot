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

function Run($sql, $title) {
    Write-Host ""; Write-Host "=== $title ==="
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60
    $rdr = $cmd.ExecuteReader()
    $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
    $tbl | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
}

$sql1 = @'
SELECT TOP 20 Id, Symbol, Category, EntryTime, ExitTime, IsClosed,
  CAST(EntryPrice AS DECIMAL(18,8)) AS EntryPx,
  CAST(ExitPrice AS DECIMAL(18,8)) AS ExitPx,
  CAST((ExitPrice - EntryPrice) / NULLIF(EntryPrice, 0) * 100 AS DECIMAL(8,4)) AS PriceChgPct,
  CAST(PnL AS DECIMAL(12,4)) AS PnL,
  ExitReason
FROM dbo.TradeHistory
WHERE ExitTime >= DATEADD(HOUR, -3, SYSDATETIME())
ORDER BY ExitTime DESC
'@
Run $sql1 "S1_Recent3hClosed"

$sql2 = @'
SELECT Id, Symbol, Category, EntryTime,
  CAST(EntryPrice AS DECIMAL(18,8)) AS EntryPx,
  CAST(Quantity AS DECIMAL(18,8)) AS Qty, Side
FROM dbo.TradeHistory
WHERE IsClosed = 0
ORDER BY EntryTime DESC
'@
Run $sql2 "S2_OpenPositions"

$sql3 = @'
SELECT TOP 30 LogTime, LogLevel, Component, Message
FROM dbo.Bot_Log
WHERE LogTime >= DATEADD(HOUR, -3, SYSDATETIME())
  AND (Message LIKE N'%v5.20%' OR Message LIKE N'%RSI70%' OR Message LIKE N'%EMA20_NOT_RISING%' OR Message LIKE N'%LORENTZIAN%')
ORDER BY LogTime DESC
'@
Run $sql3 "S3_v5_20_8_GateLogs"

$sql4 = @'
SELECT TOP 30 LogTime, Message
FROM dbo.Bot_Log
WHERE LogTime >= DATEADD(HOUR, -3, SYSDATETIME())
  AND Message LIKE N'%[GATE]%'
ORDER BY LogTime DESC
'@
Run $sql4 "S4_GateBlockLogs"

$sql5 = "SELECT TOP 5 UserId, DefaultLeverage, DefaultMargin, TargetRoe, StopLossRoe, MajorLeverage, MajorTp1Roe, MajorStopLossRoe, PumpLeverage, PumpTp1Roe, PumpStopLossRoe FROM dbo.GeneralSettings"
Run $sql5 "S5_Settings"

$cn.Close()
