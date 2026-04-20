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

function PostWithBody($ep, $params) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = $params + "&timestamp=$ts"
    $sig = Sig $qs $apiSecret
    $fullBody = $qs + "&signature=" + $sig
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($fullBody)

    $url = "https://fapi.binance.com" + $ep
    $req = [System.Net.WebRequest]::Create($url)
    $req.Method = "POST"
    $req.Headers.Add("X-MBX-APIKEY", $apiKey)
    $req.ContentType = "application/x-www-form-urlencoded"
    $req.ContentLength = $bodyBytes.Length
    $req.Timeout = 15000
    $stream = $req.GetRequestStream()
    $stream.Write($bodyBytes, 0, $bodyBytes.Length)
    $stream.Close()
    try {
        $resp = $req.GetResponse(); $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $body = $sr.ReadToEnd(); $resp.Close()
        Write-Host ("  OK: " + $body) -ForegroundColor Green
        return $body
    } catch [System.Net.WebException] {
        $er = $_.Exception.Response
        if ($null -ne $er) {
            $s2 = New-Object System.IO.StreamReader($er.GetResponseStream()); $eb = $s2.ReadToEnd()
            Write-Host ("  ERR [" + [int]$er.StatusCode + "]: " + $eb) -ForegroundColor Red
        }
        return $null
    }
}

Write-Host "==== TAOUSDT STOP_MARKET via POST BODY ===="
PostWithBody "/fapi/v1/order" "symbol=TAOUSDT&side=SELL&type=STOP_MARKET&quantity=4.119&stopPrice=218.4&reduceOnly=true"

Write-Host ""
Write-Host "==== TAOUSDT STOP_MARKET with closePosition=true ===="
PostWithBody "/fapi/v1/order" "symbol=TAOUSDT&side=SELL&type=STOP_MARKET&stopPrice=218.4&closePosition=true"

Write-Host ""
Write-Host "==== TAOUSDT STOP_MARKET with workingType=MARK_PRICE ===="
PostWithBody "/fapi/v1/order" "symbol=TAOUSDT&side=SELL&type=STOP_MARKET&quantity=4.119&stopPrice=218.4&reduceOnly=true&workingType=MARK_PRICE"

Write-Host ""
Write-Host "==== TAOUSDT STOP_MARKET with priceProtect=true + timeInForce=GTE_GTC ===="
PostWithBody "/fapi/v1/order" "symbol=TAOUSDT&side=SELL&type=STOP_MARKET&quantity=4.119&stopPrice=218.4&reduceOnly=true&priceProtect=true&timeInForce=GTE_GTC"

Write-Host ""
Write-Host "==== TAOUSDT STOP_MARKET with positionSide=BOTH ===="
PostWithBody "/fapi/v1/order" "symbol=TAOUSDT&side=SELL&positionSide=BOTH&type=STOP_MARKET&quantity=4.119&stopPrice=218.4&reduceOnly=true"

Write-Host ""
Write-Host "==== LIMIT order (sanity - already known to work) ===="
PostWithBody "/fapi/v1/order" "symbol=DOGEUSDT&side=BUY&type=LIMIT&quantity=70&price=0.05&timeInForce=GTX"
