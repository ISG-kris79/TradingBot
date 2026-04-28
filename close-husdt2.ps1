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

$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr
$cn.Open()
$cm = $cn.CreateCommand()
$cm.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rd = $cm.ExecuteReader()
$apiKey = ""
$apiSecret = ""
if ($rd.Read()) {
    $apiKey = AesDecrypt $rd["BinanceApiKey"]
    $apiSecret = AesDecrypt $rd["BinanceApiSecret"]
}
$rd.Close()
$cn.Close()

function Get-Sig($q, $s) {
    $h = New-Object System.Security.Cryptography.HMACSHA256
    $h.Key = [Text.Encoding]::UTF8.GetBytes($s)
    return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-", "").ToLower())
}

function CallBF($method, $endpoint, $extra) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = ""
    if ($extra) { $qs = "$extra" + "&" + "timestamp=$ts" } else { $qs = "timestamp=$ts" }
    $sig = Get-Sig $qs $apiSecret
    $url = "https://fapi.binance.com" + $endpoint + "?" + $qs + "&signature=$sig"
    try {
        return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY" = $apiKey } -Method $method -TimeoutSec 15
    }
    catch {
        Write-Host ("API err [" + $method + " " + $endpoint + "]: " + $_.Exception.Message) -ForegroundColor Red
        return $null
    }
}

Write-Host "STEP 1 - HUSDT positionRisk"
$pos = CallBF "Get" "/fapi/v2/positionRisk" "symbol=HUSDT"
if (-not $pos) { Write-Host "positionRisk fail" -ForegroundColor Red; exit 1 }
$posAmt = [double]$pos[0].positionAmt
$entryPrice = [double]$pos[0].entryPrice
$markPrice = [double]$pos[0].markPrice
$unrealized = [double]$pos[0].unRealizedProfit
Write-Host ("  amt=" + $posAmt + " entry=" + $entryPrice + " mark=" + $markPrice + " PnL=" + $unrealized)

if ($posAmt -eq 0) {
    Write-Host "Already closed on Binance. Updating DB only." -ForegroundColor Yellow
    $cn2 = New-Object System.Data.SqlClient.SqlConnection $connStr
    $cn2.Open()
    $cm2 = $cn2.CreateCommand()
    $cm2.CommandText = "UPDATE TradeHistory SET IsClosed=1, ExitTime=GETUTCDATE(), ExitReason='MANUAL_CLAUDE_CLOSE_BINANCE_ALREADY_ZERO' WHERE Id=4183 AND IsClosed=0"
    $n = $cm2.ExecuteNonQuery()
    $cn2.Close()
    Write-Host ("DB rows updated: " + $n)
    exit 0
}

Write-Host ""
Write-Host "STEP 2 - Cancel algo orders"
$algo = CallBF "Get" "/fapi/v1/openAlgoOrders" "symbol=HUSDT"
if ($algo) {
    foreach ($a in $algo) {
        $aid = $a.algoId
        Write-Host ("  Cancel algo " + $a.orderType + " id=" + $aid)
        $d = CallBF "Delete" "/fapi/v1/algoOrder" ("symbol=HUSDT" + "&" + "algoId=" + $aid)
        if ($d) { Write-Host "    OK" -ForegroundColor Green }
    }
}

Write-Host ""
Write-Host "STEP 3 - Cancel all open orders"
$d2 = CallBF "Delete" "/fapi/v1/allOpenOrders" "symbol=HUSDT"
if ($d2) { Write-Host "  OK" -ForegroundColor Green }

Write-Host ""
Write-Host "STEP 4 - Market close order"
$qty = [math]::Abs($posAmt).ToString("F0")
$closeSide = "SELL"
if ($posAmt -lt 0) { $closeSide = "BUY" }
$orderParams = "symbol=HUSDT" + "&" + "side=" + $closeSide + "&" + "type=MARKET" + "&" + "quantity=" + $qty + "&" + "reduceOnly=true"
Write-Host ("  MARKET " + $closeSide + " qty=" + $qty + " reduceOnly=true")
$order = CallBF "Post" "/fapi/v1/order" $orderParams
if ($order) {
    Write-Host ("  OK orderId=" + $order.orderId + " status=" + $order.status) -ForegroundColor Green
}
else {
    Write-Host "  Order FAILED" -ForegroundColor Red
    exit 1
}

Start-Sleep -Seconds 3

Write-Host ""
Write-Host "STEP 5 - Verify position closed"
$pos2 = CallBF "Get" "/fapi/v2/positionRisk" "symbol=HUSDT"
if ($pos2) {
    $a2 = [double]$pos2[0].positionAmt
    $u2 = [double]$pos2[0].unRealizedProfit
    Write-Host ("  positionAmt=" + $a2 + " unrealizedPnL=" + $u2)
}

Write-Host ""
Write-Host "STEP 6 - Update DB"
$cn3 = New-Object System.Data.SqlClient.SqlConnection $connStr
$cn3.Open()
$cm3 = $cn3.CreateCommand()
$pnlStr = $unrealized.ToString("F4")
$mkStr = $markPrice.ToString("F8")
$roe = (($markPrice - $entryPrice) / $entryPrice * 100 * 5)
$roeStr = $roe.ToString("F4")
$cm3.CommandText = "UPDATE TradeHistory SET IsClosed=1, ExitTime=GETUTCDATE(), ExitPrice=$mkStr, PnL=$pnlStr, PnLPercent=$roeStr, ExitReason='MANUAL_CLAUDE_CLOSE_USER_REQUEST' WHERE Id=4183 AND IsClosed=0"
$n = $cm3.ExecuteNonQuery()
$cn3.Close()
Write-Host ("  DB rows updated: " + $n) -ForegroundColor Green
Write-Host ""
Write-Host ("DONE. Realized PnL approx: " + $unrealized + " USDT") -ForegroundColor Cyan
