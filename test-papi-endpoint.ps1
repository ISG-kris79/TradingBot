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
function BnReq($method, $hostName, $ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    $url = "https://" + $hostName + $ep + "?" + $qs + "&signature=" + $sig
    $req = [System.Net.WebRequest]::Create($url)
    $req.Method = $method; $req.Headers.Add("X-MBX-APIKEY", $apiKey); $req.ContentLength = 0; $req.Timeout = 15000
    try {
        $resp = $req.GetResponse(); $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $body = $sr.ReadToEnd(); $resp.Close()
        Write-Host ("OK " + $method + " " + $hostName + $ep + " -> " + $body.Substring(0, [Math]::Min(200, $body.Length))) -ForegroundColor Green
        return $true
    } catch [System.Net.WebException] {
        $er = $_.Exception.Response
        if ($null -ne $er) {
            $s2 = New-Object System.IO.StreamReader($er.GetResponseStream()); $eb = $s2.ReadToEnd()
            Write-Host ("ERR " + $method + " " + $hostName + $ep + " [" + [int]$er.StatusCode + "]: " + $eb) -ForegroundColor Red
        }
        return $false
    }
}

Write-Host "--- Portfolio Margin (PAPI) tests ---"
# Portfolio Margin 계정 정보
BnReq "GET" "papi.binance.com" "/papi/v1/account" ""

Write-Host ""
Write-Host "--- UM (USDM Futures on PM) positions ---"
BnReq "GET" "papi.binance.com" "/papi/v1/um/positionRisk" ""

Write-Host ""
Write-Host "--- Try PM Conditional Order (TAOUSDT STOP_MARKET) ---"
BnReq "POST" "papi.binance.com" "/papi/v1/um/conditional/order" "symbol=TAOUSDT&side=SELL&strategyType=STOP&type=STOP_MARKET&quantity=4.119&stopPrice=218.4&reduceOnly=true"

Write-Host ""
Write-Host "--- Classic futures commonFuturesAccount (account type check) ---"
BnReq "GET" "fapi.binance.com" "/fapi/v1/account" ""

Write-Host ""
Write-Host "--- feature/permission check ---"
BnReq "GET" "fapi.binance.com" "/fapi/v1/apiTradingStatus" ""

Write-Host ""
Write-Host "--- Testnet sanity - /fapi/v1/exchangeInfo - order types for TAOUSDT ---"
try {
    $info = Invoke-RestMethod -Uri "https://fapi.binance.com/fapi/v1/exchangeInfo" -TimeoutSec 15
    $tao = $info.symbols | Where-Object { $_.symbol -eq "TAOUSDT" }
    if ($null -ne $tao) {
        Write-Host ("TAOUSDT orderTypes: " + ($tao.orderTypes -join ", "))
        Write-Host ("TAOUSDT status: " + $tao.status)
        Write-Host ("TAOUSDT contractType: " + $tao.contractType)
    }
} catch { Write-Host ("exchangeInfo err: " + $_.Exception.Message) }
