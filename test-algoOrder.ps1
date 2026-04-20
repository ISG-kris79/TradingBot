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

function PostBody($ep, $params) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = $params + "&timestamp=$ts"
    $sig = Sig $qs $apiSecret
    $fullBody = $qs + "&signature=" + $sig
    $bytes = [Text.Encoding]::UTF8.GetBytes($fullBody)
    $req = [System.Net.WebRequest]::Create("https://fapi.binance.com" + $ep)
    $req.Method = "POST"; $req.Headers.Add("X-MBX-APIKEY", $apiKey)
    $req.ContentType = "application/x-www-form-urlencoded"; $req.ContentLength = $bytes.Length; $req.Timeout = 15000
    $stream = $req.GetRequestStream(); $stream.Write($bytes, 0, $bytes.Length); $stream.Close()
    try {
        $resp = $req.GetResponse(); $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return @{ ok = $true; body = $sr.ReadToEnd() }
    } catch [System.Net.WebException] {
        $er = $_.Exception.Response
        if ($null -ne $er) {
            $s2 = New-Object System.IO.StreamReader($er.GetResponseStream())
            return @{ ok = $false; body = $s2.ReadToEnd() }
        }
        return @{ ok = $false; body = $_.Exception.Message }
    }
}

function GetReq($ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    $url = "https://fapi.binance.com" + $ep + "?" + $qs + "&signature=" + $sig
    try { return Invoke-RestMethod -Uri $url -Headers @{ "X-MBX-APIKEY" = $apiKey } -Method Get -TimeoutSec 15 } catch {
        $er = $_.Exception.Response
        if ($null -ne $er) {
            $s2 = New-Object System.IO.StreamReader($er.GetResponseStream())
            $eb = $s2.ReadToEnd()
            Write-Host ("  GET ERR: " + $eb) -ForegroundColor Red
        }
        return $null
    }
}

Write-Host "==== TEST 1: GET /fapi/v1/openAlgoOrders (list open algo) ===="
$openAlgo = GetReq "/fapi/v1/openAlgoOrders" ""
if ($null -ne $openAlgo) {
    Write-Host ("  count=" + @($openAlgo).Count)
    foreach ($o in @($openAlgo)) {
        Write-Host ("  " + ($o | ConvertTo-Json -Compress -Depth 5))
    }
}

Write-Host ""
Write-Host "==== TEST 2: XAUUSDT STOP_MARKET via algoOrder (algoType=CONDITIONAL + orderType=STOP_MARKET) ===="
$r1 = PostBody "/fapi/v1/algoOrder" "symbol=XAUUSDT&side=SELL&algoType=CONDITIONAL&type=STOP_MARKET&quantity=0.208&triggerPrice=4327&reduceOnly=true"
Write-Host ("  ok=" + $r1.ok + " body=" + $r1.body)

Write-Host ""
Write-Host "==== TEST 3: XAUUSDT TAKE_PROFIT_MARKET ===="
$r2 = PostBody "/fapi/v1/algoOrder" "symbol=XAUUSDT&side=SELL&algoType=CONDITIONAL&type=TAKE_PROFIT_MARKET&quantity=0.083&triggerPrice=5192&reduceOnly=true"
Write-Host ("  ok=" + $r2.ok + " body=" + $r2.body)

Write-Host ""
Write-Host "==== TEST 4: XAUUSDT TRAILING_STOP_MARKET ===="
$r3 = PostBody "/fapi/v1/algoOrder" "symbol=XAUUSDT&side=SELL&algoType=CONDITIONAL&type=TRAILING_STOP_MARKET&quantity=0.125&callbackRate=2.0&activationPrice=5192&reduceOnly=true"
Write-Host ("  ok=" + $r3.ok + " body=" + $r3.body)

Write-Host ""
Write-Host "==== TEST 5: GET /fapi/v1/openAlgoOrders AGAIN (verify) ===="
$openAlgo2 = GetReq "/fapi/v1/openAlgoOrders" ""
if ($null -ne $openAlgo2) {
    Write-Host ("  count=" + @($openAlgo2).Count)
    foreach ($o in @($openAlgo2)) {
        Write-Host ("  " + ($o | ConvertTo-Json -Compress -Depth 5))
    }
}
