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
$cmd = $cn.CreateCommand(); $cmd.CommandText = "SELECT Id, Username, BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$reader = $cmd.ExecuteReader()
$apiKey = ""; $apiSecret = ""; $username = ""
if ($reader.Read()) {
    $username = $reader["Username"]
    $apiKey = AesDecrypt $reader["BinanceApiKey"]
    $apiSecret = AesDecrypt $reader["BinanceApiSecret"]
}
$reader.Close(); $cn.Close()

if ([string]::IsNullOrEmpty($apiKey)) { Write-Host "API key decrypt failed"; exit 1 }
Write-Host ("UserId=1 " + $username + " Key=" + $apiKey.Substring(0,8) + "...")

function Sign-Query($q, $secret) {
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))
    return [BitConverter]::ToString($hash).Replace("-", "").ToLower()
}
function Call-BinanceFutures($endpoint) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = "timestamp=" + $ts
    $sig = Sign-Query $qs $apiSecret
    $url = "https://fapi.binance.com" + $endpoint + "?" + $qs + "&signature=" + $sig
    try {
        $headers = @{ "X-MBX-APIKEY" = $apiKey }
        return Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec 15
    } catch {
        Write-Host ("API Error " + $endpoint + ": " + $_)
        return $null
    }
}

Write-Host ""
Write-Host "=== Binance Active Positions ==="
$positions = Call-BinanceFutures "/fapi/v2/positionRisk"
$activeList = @()
if ($null -ne $positions) {
    $activeList = @($positions | Where-Object { [double]$_.positionAmt -ne 0 })
    if ($activeList.Count -gt 0) {
        $activeList | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage | Format-Table -AutoSize
    } else { Write-Host "No active positions" }
}

Write-Host ""
Write-Host "=== Binance Open Conditional Orders ==="
$orders = Call-BinanceFutures "/fapi/v1/openOrders"
$orderList = @()
if ($null -ne $orders) {
    $orderList = @($orders)
    if ($orderList.Count -gt 0) {
        $orderList | Select-Object symbol, type, side, origQty, stopPrice, activatePrice, priceRate, reduceOnly | Format-Table -AutoSize
        Write-Host ("Total open orders: " + $orderList.Count)
    } else { Write-Host "No open orders" }
}

Write-Host ""
Write-Host "=== Position vs Orders Match ==="
foreach ($p in $activeList) {
    $sym = [string]$p.symbol
    $perSym = @($orderList | Where-Object { $_.symbol -eq $sym })
    $sl = @($perSym | Where-Object { $_.type -eq "STOP_MARKET" -or $_.type -eq "STOP" }).Count
    $tp = @($perSym | Where-Object { $_.type -eq "TAKE_PROFIT_MARKET" -or $_.type -eq "TAKE_PROFIT" }).Count
    $tr = @($perSym | Where-Object { $_.type -eq "TRAILING_STOP_MARKET" }).Count
    if ($sl -eq 0 -and $tp -eq 0 -and $tr -eq 0) { $status = "NONE" }
    elseif ($sl -gt 0 -and ($tp -gt 0 -or $tr -gt 0)) { $status = "OK" }
    else { $status = "PARTIAL" }
    $msg = $sym + " qty=" + $p.positionAmt + " entry=" + $p.entryPrice + " SL=" + $sl + " TP=" + $tp + " TR=" + $tr + " status=" + $status
    Write-Host $msg
}
