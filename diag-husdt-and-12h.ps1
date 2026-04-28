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

$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()
$cm = $cn.CreateCommand(); $cm.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rd = $cm.ExecuteReader(); $apiKey=""; $apiSecret=""
if ($rd.Read()) { $apiKey = AesDecrypt $rd["BinanceApiKey"]; $apiSecret = AesDecrypt $rd["BinanceApiSecret"] }
$rd.Close(); $cn.Close()

function Get-Sig($q,$s) { $h=New-Object System.Security.Cryptography.HMACSHA256; $h.Key=[Text.Encoding]::UTF8.GetBytes($s); return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower()) }
function Get-BF($endpoint,$extra) {
    $ts=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs="timestamp=$ts"; if($extra){$qs="$extra&$qs"}
    $sig=Get-Sig $qs $apiSecret
    $url="https://fapi.binance.com$endpoint"+"?"+"$qs&signature=$sig"
    try { return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY"=$apiKey} -Method Get -TimeoutSec 15 } catch { Write-Host "API err: $($_.Exception.Message)" -ForegroundColor Red; return $null }
}

function Get-Symbol-Diag($sym) {
    Write-Host "==== [$sym] DB TradeHistory ===="
    $h1 = "SELECT EntryTime, Strategy, Side, EntryPrice, Quantity, PnL, PnLPercent, ExitTime, ExitReason, IsClosed FROM TradeHistory WHERE Symbol='$sym' AND UserId=1 AND (IsClosed=0 OR EntryTime >= DATEADD(DAY,-2,GETUTCDATE())) ORDER BY EntryTime DESC"
    Q $h1 | Format-Table -AutoSize -Wrap

    Write-Host "==== [$sym] Binance positionRisk ===="
    $pos = Get-BF "/fapi/v2/positionRisk" "symbol=$sym"
    if ($pos) { $pos | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit, isolatedMargin | Format-List }

    Write-Host "==== [$sym] Binance openOrders + algoOrders ===="
    $o = Get-BF "/fapi/v1/openOrders" "symbol=$sym"
    if ($o) { $o | Select-Object orderId, type, side, origQty, price, stopPrice, reduceOnly, status | Format-Table -AutoSize }
    $algo = Get-BF "/fapi/v1/openAlgoOrders" "symbol=$sym"
    if ($algo) { $algo | Format-Table -AutoSize } else { Write-Host "  algoOrder NONE" }
    Write-Host ""
}

Get-Symbol-Diag "HUSDT"
Get-Symbol-Diag "BASEUSDT"
Get-Symbol-Diag "BASUSDT"

Write-Host "==== [4] 12:00 KST ihu jinip (UTC 03:00 ihu) ===="
$t1 = "SELECT EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EntryKST, Symbol, Strategy, Side, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, ExitReason, IsClosed FROM TradeHistory WHERE UserId=1 AND EntryTime >= '2026-04-23 03:00:00' ORDER BY EntryTime DESC"
Q $t1 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] 12:00 ihu seungryul ===="
$s1 = @'
SELECT
  COUNT(*) AS TotalTrades,
  SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END) AS ClosedTrades,
  SUM(CASE WHEN IsClosed=1 AND PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN IsClosed=1 AND PnL < 0 THEN 1 ELSE 0 END) AS Losses,
  CAST(SUM(CASE WHEN IsClosed=1 AND PnL > 0 THEN 1.0 ELSE 0 END) * 100.0 / NULLIF(SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END),0) AS DECIMAL(5,2)) AS WinRate,
  CAST(SUM(CASE WHEN IsClosed=1 THEN PnL ELSE 0 END) AS DECIMAL(18,4)) AS NetPnL,
  CAST(AVG(CASE WHEN IsClosed=1 AND PnL > 0 THEN PnL ELSE NULL END) AS DECIMAL(18,4)) AS AvgWin,
  CAST(AVG(CASE WHEN IsClosed=1 AND PnL < 0 THEN PnL ELSE NULL END) AS DECIMAL(18,4)) AS AvgLoss
FROM TradeHistory WHERE UserId=1 AND EntryTime >= '2026-04-23 03:00:00'
'@
Q $s1 | Format-List

Write-Host "==== [6] 12:00 ihu jongmokbyeol ===="
$s2 = @'
SELECT Symbol,
  COUNT(*) AS Trades,
  SUM(CASE WHEN IsClosed=1 AND PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN IsClosed=1 AND PnL < 0 THEN 1 ELSE 0 END) AS Losses,
  CAST(SUM(CASE WHEN IsClosed=1 THEN PnL ELSE 0 END) AS DECIMAL(18,4)) AS Net,
  MAX(ExitReason) AS LastReason
FROM TradeHistory WHERE UserId=1 AND EntryTime >= '2026-04-23 03:00:00'
GROUP BY Symbol ORDER BY Net ASC
'@
Q $s2 | Format-Table -AutoSize

Write-Host "==== [7] Bot_Log ML signal ===="
$ml = @'
SELECT TOP 50 EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EventKST,
  Symbol, Direction, Allowed, ML_Conf, LEFT(Reason,120) AS Reason
FROM Bot_Log WHERE UserId=1 AND EventTime >= '2026-04-23 03:00:00'
ORDER BY EventTime DESC
'@
Q $ml | Format-Table -AutoSize -Wrap

Write-Host "==== [8] HUSDT FooterLogs ===="
$f = @'
SELECT TOP 30 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS TimeKST,
  LEFT(Message,200) AS Msg
FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-24,GETUTCDATE())
  AND Message LIKE '%HUSDT%' ORDER BY Timestamp DESC
'@
Q $f | Format-Table -AutoSize -Wrap

Write-Host "==== [9] BASEUSDT/BASUSDT FooterLogs ===="
$f2 = @'
SELECT TOP 30 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS TimeKST,
  LEFT(Message,200) AS Msg
FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-24,GETUTCDATE())
  AND (Message LIKE '%BASEUSDT%' OR Message LIKE '%BASUSDT%') ORDER BY Timestamp DESC
'@
Q $f2 | Format-Table -AutoSize -Wrap
