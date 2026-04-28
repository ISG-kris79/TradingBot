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

Write-Host "[A] ROBOUSDT Binance positionRisk (현재)"
$pos = Get-BF "/fapi/v2/positionRisk" "symbol=ROBOUSDT"
if ($pos) { $pos | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit, isolatedMargin, liquidationPrice | Format-List }

Write-Host ""
Write-Host "[B] ROBOUSDT openAlgoOrders (SL/TP/Trailing)"
$algo = Get-BF "/fapi/v1/openAlgoOrders" "symbol=ROBOUSDT"
if ($algo) {
    foreach ($a in $algo) {
        Write-Host ("  algoId=$($a.algoId) type=$($a.orderType) side=$($a.side) qty=$($a.quantity) trigger=$($a.triggerPrice) act=$($a.activatePrice) callback=$($a.callbackRate) status=$($a.algoStatus)")
    }
} else { Write-Host "  algoOrder 0건 - SL 없음!" -ForegroundColor Red }

Write-Host ""
Write-Host "[C] ROBOUSDT 일반 openOrders (LIMIT/MARKET)"
$o = Get-BF "/fapi/v1/openOrders" "symbol=ROBOUSDT"
if ($o) { $o | Select-Object symbol, orderId, type, side, origQty, price, stopPrice, reduceOnly, status | Format-Table -AutoSize }

Write-Host ""
Write-Host "[D] ROBOUSDT TradeHistory recent + entry log"
$d = "SELECT TOP 15 EntryTime, ExitTime, Strategy, Side, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, ExitReason, IsClosed FROM TradeHistory WHERE Symbol='ROBOUSDT' AND UserId=1 ORDER BY EntryTime DESC"
Q $d | Format-Table -AutoSize -Wrap

Write-Host "[E] ROBOUSDT Bot_Log"
$e = "SELECT TOP 10 EventTime, Direction, Allowed, ML_Conf, LEFT(Reason,80) AS Reason FROM Bot_Log WHERE UserId=1 AND Symbol='ROBOUSDT' ORDER BY EventTime DESC"
Q $e | Format-Table -AutoSize

Write-Host "[F] ROBOUSDT FooterLogs (보호주문 + ENTRY 흔적)"
$f = "SELECT TOP 30 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-12,GETUTCDATE()) AND Message LIKE '%ROBOUSDT%' AND (Message LIKE '%PROTECT%' OR Message LIKE '%SL%' OR Message LIKE '%TP%' OR Message LIKE '%Trailing%' OR Message LIKE '%ENTRY%' OR Message LIKE '%algoOrder%' OR Message LIKE '%4120%' OR Message LIKE '%-2021%') ORDER BY Timestamp DESC"
Q $f | Format-Table -AutoSize -Wrap
