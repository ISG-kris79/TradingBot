# Binance 활성 포지션 + algoOrder(SL/TP/Trailing) 실시간 감사
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

if ([string]::IsNullOrEmpty($apiKey)) { Write-Host "API key decrypt 실패"; exit 1 }
Write-Host ("API key loaded: " + $apiKey.Substring(0,8) + "... (UserId=1)") -ForegroundColor Cyan

function Sign-Query($q, $secret) {
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))
    return [BitConverter]::ToString($hash).Replace("-", "").ToLower()
}
function Call-BinanceFutures($endpoint, $extraQuery) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = "timestamp=" + $ts
    if (-not [string]::IsNullOrEmpty($extraQuery)) { $qs = $extraQuery + "&" + $qs }
    $sig = Sign-Query $qs $apiSecret
    $url = "https://fapi.binance.com" + $endpoint + "?" + $qs + "&signature=" + $sig
    try {
        $headers = @{ "X-MBX-APIKEY" = $apiKey }
        return Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec 15
    } catch {
        Write-Host ("API Error " + $endpoint + ": " + $_.Exception.Message) -ForegroundColor Red
        return $null
    }
}

Write-Host ""
Write-Host "=== [1] Binance Active Positions ===" -ForegroundColor Yellow
$positions = Call-BinanceFutures "/fapi/v2/positionRisk" ""
$activeList = @()
if ($null -ne $positions) {
    $activeList = @($positions | Where-Object { [double]$_.positionAmt -ne 0 })
    if ($activeList.Count -gt 0) {
        $activeList | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit | Format-Table -AutoSize
        Write-Host ("Total active positions: " + $activeList.Count) -ForegroundColor Cyan
    } else {
        Write-Host "활성 포지션 없음" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "=== [2] Open Orders (일반 LIMIT/MARKET) ===" -ForegroundColor Yellow
$openOrders = Call-BinanceFutures "/fapi/v1/openOrders" ""
$openList = @()
if ($null -ne $openOrders) {
    $openList = @($openOrders)
    if ($openList.Count -gt 0) {
        $openList | Select-Object symbol, type, side, origQty, price, reduceOnly | Format-Table -AutoSize
        Write-Host ("Total open orders: " + $openList.Count) -ForegroundColor Cyan
    } else {
        Write-Host "일반 주문 없음" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "=== [3] Active algoOrders per Symbol (SL/TP/Trailing) ===" -ForegroundColor Yellow
$algoBySymbol = @{}
foreach ($p in $activeList) {
    $sym = [string]$p.symbol
    $algo = Call-BinanceFutures "/fapi/v1/openAlgoOrders" ("symbol=" + $sym)
    if ($null -ne $algo -and $algo.Count -gt 0) {
        $algoBySymbol[$sym] = @($algo)
        Write-Host ""
        Write-Host ("  " + $sym + " (algoOrder " + $algo.Count + "건):") -ForegroundColor Cyan
        $algo | Select-Object algoType, side, origQty, stopPrice, activatePrice, priceRate, reduceOnly, algoId | Format-Table -AutoSize
    } else {
        $algoBySymbol[$sym] = @()
        Write-Host ("  " + $sym + ": algoOrder 0건 (무방비)") -ForegroundColor Red
    }
    Start-Sleep -Milliseconds 150
}

Write-Host ""
Write-Host "=== [4] Orphan algoOrder Scan (포지션 없는데 SL/TP 잔존) ===" -ForegroundColor Yellow
$cn2 = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn2.Open()
$c2 = $cn2.CreateCommand()
$c2.CommandText = @'
SELECT DISTINCT Symbol FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND ExitTime >= DATEADD(HOUR,-24,GETUTCDATE())
'@
$rd2 = $c2.ExecuteReader()
$recentClosedSymbols = @()
while ($rd2.Read()) { $recentClosedSymbols += [string]$rd2["Symbol"] }
$rd2.Close(); $cn2.Close()

$activeSyms = @($activeList | ForEach-Object { [string]$_.symbol })
$orphanCount = 0
foreach ($sym in $recentClosedSymbols) {
    if ($activeSyms -contains $sym) { continue }
    $algo = Call-BinanceFutures "/fapi/v1/openAlgoOrders" ("symbol=" + $sym)
    if ($null -ne $algo -and $algo.Count -gt 0) {
        Write-Host ("  ORPHAN: " + $sym + " 포지션=0 이지만 algoOrder " + $algo.Count + "건 잔존!") -ForegroundColor Red
        $algo | Select-Object algoType, side, origQty, stopPrice, activatePrice, algoId | Format-Table -AutoSize
        $orphanCount++
    }
    Start-Sleep -Milliseconds 150
}
if ($orphanCount -eq 0) {
    Write-Host "[OK] Orphan algoOrder 0건 (정리 정상)" -ForegroundColor Green
} else {
    Write-Host ("[ALERT] Orphan " + $orphanCount + "건 발견 - 즉시 cleanup 필요") -ForegroundColor Red
}

Write-Host ""
Write-Host "=== [5] Naked Position Scan (포지션 있는데 SL 미등록) ===" -ForegroundColor Yellow
$nakedCount = 0
$partialCount = 0
$okCount = 0
foreach ($p in $activeList) {
    $sym = [string]$p.symbol
    $algos = $algoBySymbol[$sym]
    $hasSL = $false; $hasTP = $false; $hasTrail = $false
    if ($algos -and $algos.Count -gt 0) {
        foreach ($a in $algos) {
            $t = [string]$a.algoType
            if ($t -eq "STOP_MARKET" -or $t -eq "STOP") { $hasSL = $true }
            if ($t -eq "TAKE_PROFIT_MARKET" -or $t -eq "TAKE_PROFIT") { $hasTP = $true }
            if ($t -eq "TRAILING_STOP_MARKET") { $hasTrail = $true }
        }
    }
    if ($hasSL -and ($hasTP -or $hasTrail)) { $status = "OK"; $okCount++ }
    elseif ($hasSL -or $hasTP -or $hasTrail) { $status = "PARTIAL"; $partialCount++ }
    else { $status = "NAKED"; $nakedCount++ }
    $msg = "  " + $sym + " qty=" + $p.positionAmt + " entry=" + $p.entryPrice + " SL=" + $hasSL + " TP=" + $hasTP + " Trail=" + $hasTrail + " => " + $status
    if ($status -eq "NAKED") { Write-Host $msg -ForegroundColor Red }
    elseif ($status -eq "PARTIAL") { Write-Host $msg -ForegroundColor Yellow }
    else { Write-Host $msg -ForegroundColor Green }
}
Write-Host ""
Write-Host ("Summary: OK=" + $okCount + "  PARTIAL=" + $partialCount + "  NAKED=" + $nakedCount) -ForegroundColor Cyan
if ($nakedCount -gt 0) {
    Write-Host ("[ALERT] 무방비 포지션 " + $nakedCount + "건 - 즉시 수동 SL 설정 필요") -ForegroundColor Red
}
