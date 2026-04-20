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
function GetReq($ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    try { return Invoke-RestMethod -Uri ("https://fapi.binance.com$ep" + "?" + $qs + "&signature=" + $sig) -Headers @{ "X-MBX-APIKEY" = $apiKey } -Method Get -TimeoutSec 15 } catch { return $null }
}

Write-Host "======================================================"
Write-Host "  [A] Active Positions (/fapi/v2/positionRisk)"
Write-Host "======================================================" -ForegroundColor Cyan
$pos = GetReq "/fapi/v2/positionRisk" ""
if ($null -ne $pos) {
    $active = @($pos | Where-Object { [double]$_.positionAmt -ne 0 })
    foreach ($p in $active) {
        Write-Host ("  " + $p.symbol + " amt=" + $p.positionAmt + " entry=" + $p.entryPrice + " mark=" + $p.markPrice + " pnl=" + $p.unRealizedProfit + " lev=" + $p.leverage + " liq=" + $p.liquidationPrice)
    }
    if ($active.Count -eq 0) { Write-Host "  (no active positions)" }
}

Write-Host ""
Write-Host "======================================================"
Write-Host "  [B] /fapi/v1/openOrders  ← Binance 웹 '일반 Open Orders' 탭"
Write-Host "======================================================" -ForegroundColor Cyan
$ord = GetReq "/fapi/v1/openOrders" ""
if ($null -ne $ord -and @($ord).Count -gt 0) {
    foreach ($o in @($ord)) {
        Write-Host ("  [" + $o.orderId + "] " + $o.symbol + " type=" + $o.type + " side=" + $o.side + " qty=" + $o.origQty + " stop=" + $o.stopPrice + " reduceOnly=" + $o.reduceOnly)
    }
} else { Write-Host "  EMPTY - 0 orders" -ForegroundColor Yellow }

Write-Host ""
Write-Host "======================================================"
Write-Host "  [C] /fapi/v1/openAlgoOrders  ← Binance 웹 'TP/SL', 'Trigger Orders' 탭"
Write-Host "======================================================" -ForegroundColor Cyan
$algo = GetReq "/fapi/v1/openAlgoOrders" ""
if ($null -ne $algo -and @($algo).Count -gt 0) {
    foreach ($a in @($algo)) {
        $trigger = if ($a.orderType -eq "TRAILING_STOP_MARKET") { ("cb=" + $a.callbackRate + "% activate=" + $a.activatePrice) } else { ("trigger=" + $a.triggerPrice) }
        Write-Host ("  [" + $a.algoId + "] " + $a.symbol + " type=" + $a.orderType + " side=" + $a.side + " qty=" + $a.quantity + " " + $trigger + " status=" + $a.algoStatus + " reduceOnly=" + $a.reduceOnly)
    }
    Write-Host ("  ---- Total: " + @($algo).Count + " algo orders ----") -ForegroundColor Green
} else { Write-Host "  ---- EMPTY ----" -ForegroundColor Yellow }

Write-Host ""
Write-Host "======================================================"
Write-Host "  [D] Symbol Match (Position vs Algo Orders)"
Write-Host "======================================================" -ForegroundColor Cyan
if ($null -ne $pos) {
    $active = @($pos | Where-Object { [double]$_.positionAmt -ne 0 })
    foreach ($p in $active) {
        $sym = [string]$p.symbol
        $perSym = if ($null -ne $algo) { @(@($algo) | Where-Object { $_.symbol -eq $sym }) } else { @() }
        $sl = @($perSym | Where-Object { $_.orderType -eq "STOP_MARKET" }).Count
        $tp = @($perSym | Where-Object { $_.orderType -eq "TAKE_PROFIT_MARKET" }).Count
        $tr = @($perSym | Where-Object { $_.orderType -eq "TRAILING_STOP_MARKET" }).Count
        $status = if ($sl -gt 0 -and $tp -gt 0 -and $tr -gt 0) { "OK" } else { "INCOMPLETE" }
        Write-Host ("  " + $sym + "   SL=" + $sl + " TP=" + $tp + " TR=" + $tr + "   [" + $status + "]")
    }
}

Write-Host ""
Write-Host "NOTE: Binance 웹/앱 UI 에서 'Open Orders' 탭은 [B] 일반 주문만 보여줌."
Write-Host "      SL/TP/Trailing 은 별도 탭('Triggers', 'TP/SL', 'Conditional' 등)에서 보임."
Write-Host "      [C] openAlgoOrders 결과가 UI 의 조건부 주문 탭에 해당."
