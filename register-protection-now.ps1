$ErrorActionPreference = "Stop"
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection); $cn.Open()
$cmdU = $cn.CreateCommand(); $cmdU.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rdr = $cmdU.ExecuteReader(); $apiKey=""; $apiSecret=""
if ($rdr.Read()) { $apiKey = AesDecrypt $rdr["BinanceApiKey"]; $apiSecret = AesDecrypt $rdr["BinanceApiSecret"] }
$rdr.Close(); $cn.Close()

function Sig($q, $s) { $h = New-Object System.Security.Cryptography.HMACSHA256; $h.Key = [Text.Encoding]::UTF8.GetBytes($s); [BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower() }
function BnReq($method, $ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    $url = "https://fapi.binance.com$ep" + "?" + $qs + "&signature=" + $sig
    $req = [System.Net.WebRequest]::Create($url)
    $req.Method = $method; $req.Headers.Add("X-MBX-APIKEY", $apiKey); $req.ContentLength = 0; $req.Timeout = 15000
    try {
        $resp = $req.GetResponse(); $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $body = $sr.ReadToEnd(); $resp.Close(); return ($body | ConvertFrom-Json)
    } catch [System.Net.WebException] {
        $er = $_.Exception.Response
        if ($null -ne $er) {
            $s2 = New-Object System.IO.StreamReader($er.GetResponseStream()); $eb = $s2.ReadToEnd()
            return @{ error = $eb; status = [int]$er.StatusCode }
        }
        return @{ error = $_.Exception.Message }
    }
}

function Protect($sym, $entry, $qty, $lev, $isLong, $tickPrec, $qtyPrec) {
    Write-Host ""
    Write-Host ("===== " + $sym + " entry=" + $entry + " qty=" + $qty + " lev=" + $lev + "x =====")
    $closeSide = if ($isLong) { "SELL" } else { "BUY" }

    # SL: ROE -50% -> 가격 (lev=5 기준 -10%)
    $slRoe = -50.0
    $slPriceChange = $slRoe * $entry / ($lev * 100.0)
    $slPrice = if ($isLong) { $entry + $slPriceChange } else { $entry - $slPriceChange }
    $slPrice = [Math]::Round($slPrice, $tickPrec)

    # TP: ROE +40% -> 가격 +8%
    $tpRoe = 40.0
    $tpPriceChange = $tpRoe * $entry / ($lev * 100.0)
    $tpPrice = if ($isLong) { $entry + $tpPriceChange } else { $entry - $tpPriceChange }
    $tpPrice = [Math]::Round($tpPrice, $tickPrec)

    # TP 수량 (전체의 40%)
    $tpQty = [Math]::Floor($qty * 0.4 * [Math]::Pow(10, $qtyPrec)) / [Math]::Pow(10, $qtyPrec)
    if ($tpQty -le 0) { $tpQty = $qty }
    $trailQty = [Math]::Round($qty - $tpQty, $qtyPrec)

    Write-Host ("  SL=$slPrice qty=$qty")
    Write-Host ("  TP=$tpPrice qty=$tpQty")
    Write-Host ("  Trailing qty=$trailQty callback=2% activation=$tpPrice")

    # 1. SL
    $r1 = BnReq "POST" "/fapi/v1/order" ("symbol=" + $sym + "&side=" + $closeSide + "&type=STOP_MARKET&quantity=" + $qty + "&stopPrice=" + $slPrice + "&reduceOnly=true")
    if ($null -ne $r1.orderId) { Write-Host ("  OK SL orderId=" + $r1.orderId) -ForegroundColor Green }
    else { Write-Host ("  FAIL SL: " + $r1.error) -ForegroundColor Red }

    # 2. TP
    $r2 = BnReq "POST" "/fapi/v1/order" ("symbol=" + $sym + "&side=" + $closeSide + "&type=TAKE_PROFIT_MARKET&quantity=" + $tpQty + "&stopPrice=" + $tpPrice + "&reduceOnly=true")
    if ($null -ne $r2.orderId) { Write-Host ("  OK TP orderId=" + $r2.orderId) -ForegroundColor Green }
    else { Write-Host ("  FAIL TP: " + $r2.error) -ForegroundColor Red }

    # 3. Trailing (부분청산 후 잔여분)
    if ($trailQty -gt 0) {
        $r3 = BnReq "POST" "/fapi/v1/order" ("symbol=" + $sym + "&side=" + $closeSide + "&type=TRAILING_STOP_MARKET&quantity=" + $trailQty + "&callbackRate=2.0&activationPrice=" + $tpPrice + "&reduceOnly=true")
        if ($null -ne $r3.orderId) { Write-Host ("  OK Trailing orderId=" + $r3.orderId) -ForegroundColor Green }
        else { Write-Host ("  FAIL Trailing: " + $r3.error) -ForegroundColor Red }
    }
}

# 현재 3개 포지션에 등록
Protect "TAOUSDT"  242.67                   4.119  5  $true  2  3
Protect "BLURUSDT" 0.0311364829942          32195  5  $true  5  0
Protect "XAUUSDT"  4808.06                  0.208  5  $true  2  3

Write-Host ""
Write-Host "===== verify =====" -ForegroundColor Cyan
$ord = BnReq "GET" "/fapi/v1/openOrders" ""
if ($null -ne $ord -and $ord.Count -gt 0) {
    foreach ($o in $ord) {
        Write-Host ("  " + $o.symbol + " " + $o.type + " " + $o.side + " qty=" + $o.origQty + " stop=" + $o.stopPrice + " cb=" + $o.priceRate)
    }
} else { Write-Host "  no open orders" }
