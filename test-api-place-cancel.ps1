# Binance API place/verify/cancel test
# Places STOP_MARKET + TAKE_PROFIT_MARKET + TRAILING_STOP_MARKET far from market
# Verifies each appears, then cancels all. No position needed, no real cost.

$ErrorActionPreference = "Stop"

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

$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()
$cmd = $cn.CreateCommand()
$cmd.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$reader = $cmd.ExecuteReader()
$apiKey = ""; $apiSecret = ""
if ($reader.Read()) {
    $apiKey = AesDecrypt $reader["BinanceApiKey"]
    $apiSecret = AesDecrypt $reader["BinanceApiSecret"]
}
$reader.Close(); $cn.Close()
Write-Host ("API Key: " + $apiKey.Substring(0,8) + "...")

function Sig($q, $secret) {
    $h = New-Object System.Security.Cryptography.HMACSHA256
    $h.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $b = $h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))
    return [BitConverter]::ToString($b).Replace("-", "").ToLower()
}
function BnReq($method, $endpoint, $params) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($params)) { "timestamp=" + $ts } else { $params + "&timestamp=" + $ts }
    $s = Sig $qs $apiSecret
    $url = "https://fapi.binance.com" + $endpoint + "?" + $qs + "&signature=" + $s
    $req = [System.Net.WebRequest]::Create($url)
    $req.Method = $method
    $req.Headers.Add("X-MBX-APIKEY", $apiKey)
    $req.ContentLength = 0
    $req.Timeout = 15000
    try {
        $resp = $req.GetResponse()
        $stream = $resp.GetResponseStream()
        $reader2 = New-Object System.IO.StreamReader($stream)
        $body = $reader2.ReadToEnd()
        $resp.Close()
        return ($body | ConvertFrom-Json)
    } catch [System.Net.WebException] {
        $errResp = $_.Exception.Response
        if ($null -ne $errResp) {
            $stream = $errResp.GetResponseStream()
            $reader2 = New-Object System.IO.StreamReader($stream)
            $body = $reader2.ReadToEnd()
            Write-Host ("ERR " + $method + " " + $endpoint + " status=" + [int]$errResp.StatusCode + " body=" + $body)
        } else {
            Write-Host ("ERR " + $method + " " + $endpoint + " NoResponse " + $_.Exception.Message)
        }
        return $null
    }
}

# 1. Get market price for DOGEUSDT (cheap symbol, tick 0.00001)
Write-Host ""
Write-Host "Step 1: Get DOGEUSDT market price"
$ticker = Invoke-RestMethod -Uri "https://fapi.binance.com/fapi/v1/ticker/price?symbol=DOGEUSDT" -Method Get
$px = [double]$ticker.price
Write-Host ("DOGEUSDT price=" + $px)

# 2. Place STOP_MARKET far below market (SELL, stop at 50% of price)
$slPrice = [Math]::Round($px * 0.5, 5)
$tpPrice = [Math]::Round($px * 2.0, 5)
$qty = 10  # 10 DOGE, smallest nominal

Write-Host ""
Write-Host "Step 0: Check account type (positionMode/multiAssetMode/accountInfo)"
$posMode = BnReq "GET" "/fapi/v1/positionSide/dual" ""
if ($null -ne $posMode) { Write-Host ("  Hedge mode: " + $posMode.dualSidePosition) }
$accInfo = BnReq "GET" "/fapi/v2/account" ""
if ($null -ne $accInfo) { Write-Host ("  canTrade: " + $accInfo.canTrade + " feeTier: " + $accInfo.feeTier + " accountType: " + $accInfo.accountType) }

Write-Host ""
Write-Host "Step 1b: Test LIMIT order (post-only, far from market — should succeed if account OK)"
$limitPx = [Math]::Round($px * 0.5, 5)
$limitParams = "symbol=DOGEUSDT&side=BUY&type=LIMIT&quantity=" + $qty + "&price=" + $limitPx + "&timeInForce=GTX"
$limitRes = BnReq "POST" "/fapi/v1/order" $limitParams
if ($null -ne $limitRes) {
    Write-Host ("  LIMIT OK orderId=" + $limitRes.orderId + " status=" + $limitRes.status)
    $cancelParams = "symbol=DOGEUSDT&orderId=" + $limitRes.orderId
    BnReq "DELETE" "/fapi/v1/order" $cancelParams | Out-Null
    Write-Host "  LIMIT cancelled"
}

Write-Host ""
Write-Host ("Step 2a: Place STOP_MARKET (no reduceOnly) SL at " + $slPrice)
$slParams = "symbol=DOGEUSDT&side=SELL&type=STOP_MARKET&quantity=" + $qty + "&stopPrice=" + $slPrice
$slRes = BnReq "POST" "/fapi/v1/order" $slParams
if ($null -ne $slRes) { Write-Host ("  OK orderId=" + $slRes.orderId + " type=" + $slRes.type + " status=" + $slRes.status) }

Write-Host ""
Write-Host ("Step 2b: Place STOP_MARKET (closePosition=true) SL at " + $slPrice)
$slParams2 = "symbol=DOGEUSDT&side=SELL&type=STOP_MARKET&stopPrice=" + $slPrice + "&closePosition=true"
$slRes2 = BnReq "POST" "/fapi/v1/order" $slParams2
if ($null -ne $slRes2) { Write-Host ("  OK orderId=" + $slRes2.orderId + " type=" + $slRes2.type + " status=" + $slRes2.status) }

Write-Host ""
Write-Host ("Step 3: Place TAKE_PROFIT_MARKET TP at " + $tpPrice)
$tpParams = "symbol=DOGEUSDT&side=SELL&type=TAKE_PROFIT_MARKET&quantity=" + $qty + "&stopPrice=" + $tpPrice
$tpRes = BnReq "POST" "/fapi/v1/order" $tpParams
if ($null -ne $tpRes) { Write-Host ("  OK orderId=" + $tpRes.orderId + " type=" + $tpRes.type + " status=" + $tpRes.status) }

Write-Host ""
Write-Host "Step 4: Place TRAILING_STOP_MARKET callback 5%"
$trParams = "symbol=DOGEUSDT&side=SELL&type=TRAILING_STOP_MARKET&quantity=" + $qty + "&callbackRate=5.0"
$trRes = BnReq "POST" "/fapi/v1/order" $trParams
if ($null -ne $trRes) { Write-Host ("  OK orderId=" + $trRes.orderId + " type=" + $trRes.type + " status=" + $trRes.status) }

# 5. Verify via openOrders
Write-Host ""
Write-Host "Step 5: Verify via openOrders"
Start-Sleep -Milliseconds 500
$opens = BnReq "GET" "/fapi/v1/openOrders" "symbol=DOGEUSDT"
if ($null -ne $opens) {
    $opens | Select-Object orderId, type, side, stopPrice, origQty, callbackRate | Format-Table -AutoSize
    Write-Host ("  Count: " + @($opens).Count)
}

# 6. Cancel all
Write-Host ""
Write-Host "Step 6: Cancel all DOGEUSDT orders"
$canRes = BnReq "DELETE" "/fapi/v1/allOpenOrders" "symbol=DOGEUSDT"
if ($null -ne $canRes) { Write-Host ("  Result: " + $canRes.msg) }

# 7. Verify cancelled
Write-Host ""
Write-Host "Step 7: Verify cancel"
Start-Sleep -Milliseconds 500
$after = BnReq "GET" "/fapi/v1/openOrders" "symbol=DOGEUSDT"
if ($null -ne $after) {
    Write-Host ("  Remaining: " + @($after).Count)
}

Write-Host ""
Write-Host "=== ALT ENDPOINTS ==="

Write-Host ""
Write-Host "A1: Portfolio Margin UM conditional (/papi/v1/um/conditional/order)"
$pmParams = "symbol=DOGEUSDT&side=SELL&strategyType=STOP&type=STOP_MARKET&quantity=" + $qty + "&stopPrice=" + $slPrice + "&reduceOnly=true"
BnReq "POST" "/papi/v1/um/conditional/order" $pmParams | Out-Null

Write-Host ""
Write-Host "A2: Algo TWAP (/sapi/v1/algo/futures/newOrderTwap)"
$twapParams = "symbol=DOGEUSDT&side=SELL&positionSide=BOTH&quantity=" + $qty + "&duration=300&clientAlgoId=testclient123"
BnReq "POST" "/sapi/v1/algo/futures/newOrderTwap" $twapParams | Out-Null

Write-Host ""
Write-Host "A3: API Trading Status"
$apiStat = BnReq "GET" "/sapi/v1/account/apiTradingStatus" ""
if ($null -ne $apiStat) { Write-Host ("  " + ($apiStat | ConvertTo-Json -Compress -Depth 5)) }

Write-Host ""
Write-Host "A4: Exchange Info orderTypes for DOGEUSDT"
$info = Invoke-RestMethod -Uri "https://fapi.binance.com/fapi/v1/exchangeInfo" -Method Get -TimeoutSec 15
$dogeInfo = $info.symbols | Where-Object { $_.symbol -eq "DOGEUSDT" }
if ($null -ne $dogeInfo) {
    Write-Host ("  orderTypes: " + ($dogeInfo.orderTypes -join ", "))
}

Write-Host ""
Write-Host "A5: Futures account commission /fapi/v1/commissionRate"
$comm = BnReq "GET" "/fapi/v1/commissionRate" "symbol=DOGEUSDT"
if ($null -ne $comm) { Write-Host ("  " + ($comm | ConvertTo-Json -Compress)) }
