# Binance algoOrder 감사 v2: 전체 필드 검사 + 정확한 type 분류
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
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()
$cmd = $cn.CreateCommand(); $cmd.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$reader = $cmd.ExecuteReader()
$apiKey = ""; $apiSecret = ""
if ($reader.Read()) {
    $apiKey = AesDecrypt $reader["BinanceApiKey"]
    $apiSecret = AesDecrypt $reader["BinanceApiSecret"]
}
$reader.Close(); $cn.Close()

function Get-Sig($q, $secret) {
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))
    return [BitConverter]::ToString($hash).Replace("-", "").ToLower()
}
function Get-BinanceFutures($endpoint, $extraQuery) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = "timestamp=" + $ts
    if (-not [string]::IsNullOrEmpty($extraQuery)) { $qs = $extraQuery + "&" + $qs }
    $sig = Get-Sig $qs $apiSecret
    $url = "https://fapi.binance.com" + $endpoint + "?" + $qs + "&signature=" + $sig
    try {
        $headers = @{ "X-MBX-APIKEY" = $apiKey }
        return Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec 15
    } catch {
        return $null
    }
}

$positions = Get-BinanceFutures "/fapi/v2/positionRisk" ""
$activeList = @($positions | Where-Object { [double]$_.positionAmt -ne 0 })
Write-Host ""
Write-Host ("Active positions: " + $activeList.Count) -ForegroundColor Cyan

foreach ($p in $activeList) {
    $sym = [string]$p.symbol
    $algo = Get-BinanceFutures "/fapi/v1/openAlgoOrders" ("symbol=" + $sym)
    Write-Host ""
    Write-Host ("===== " + $sym + " =====") -ForegroundColor Yellow
    Write-Host ("  Position: qty=" + $p.positionAmt + " entry=" + $p.entryPrice + " mark=" + $p.markPrice + " UnPnL=" + $p.unRealizedProfit)
    if ($null -eq $algo -or $algo.Count -eq 0) {
        Write-Host "  algoOrder 0건 - NAKED" -ForegroundColor Red
        continue
    }
    Write-Host ("  algoOrder " + $algo.Count + "건 raw:")
    # 모든 필드 출력
    foreach ($a in $algo) {
        $line = "    "
        foreach ($prop in $a.PSObject.Properties) {
            if ($null -ne $prop.Value -and "$($prop.Value)" -ne "") {
                $line += $prop.Name + "=" + $prop.Value + " "
            }
        }
        Write-Host $line -ForegroundColor Gray
    }
    Start-Sleep -Milliseconds 200
}
