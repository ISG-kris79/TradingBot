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
$cm = $cn.CreateCommand(); $cm.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rd = $cm.ExecuteReader(); $apiKey=""; $apiSecret=""
if ($rd.Read()) { $apiKey = AesDecrypt $rd["BinanceApiKey"]; $apiSecret = AesDecrypt $rd["BinanceApiSecret"] }
$rd.Close(); $cn.Close()

function Get-Sig($q,$s) { $h=New-Object System.Security.Cryptography.HMACSHA256; $h.Key=[Text.Encoding]::UTF8.GetBytes($s); return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower()) }
function Get-BF($endpoint,$extra) {
    $ts=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs="timestamp=$ts"; if($extra){$qs="$extra&$qs"}
    $sig=Get-Sig $qs $apiSecret
    $url="https://fapi.binance.com$endpoint"+"?"+"$qs&signature=$sig"
    try { return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY"=$apiKey} -Method Get -TimeoutSec 15 } catch { Write-Host ("API err: " + $_.Exception.Message) -ForegroundColor Red; return $null }
}
function Post-BF($endpoint,$params) {
    $ts=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs="$params&timestamp=$ts"
    $sig=Get-Sig $qs $apiSecret
    $url="https://fapi.binance.com$endpoint"+"?"+"$qs&signature=$sig"
    try { return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY"=$apiKey} -Method Post -TimeoutSec 15 } catch { Write-Host ("POST err: " + $_.Exception.Message) -ForegroundColor Red; return $null }
}
function Delete-BF($endpoint,$params) {
    $ts=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs="$params&timestamp=$ts"
    $sig=Get-Sig $qs $apiSecret
    $url="https://fapi.binance.com$endpoint"+"?"+"$qs&signature=$sig"
    try { return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY"=$apiKey} -Method Delete -TimeoutSec 15 } catch { Write-Host ("DEL err: " + $_.Exception.Message) -ForegroundColor Red; return $null }
}

Write-Host "==== STEP 1: HUSDT 현재 포지션 확인 ===="
$pos = Get-BF "/fapi/v2/positionRisk" "symbol=HUSDT"
if (-not $pos) { Write-Host "positionRisk API 실패 → 중단"; exit 1 }
$posAmt = [double]$pos[0].positionAmt
$entryPrice = [double]$pos[0].entryPrice
$markPrice = [double]$pos[0].markPrice
$unrealized = [double]$pos[0].unRealizedProfit
Write-Host "HUSDT: amt=$posAmt entry=$entryPrice mark=$markPrice PnL=$unrealized USDT"

if ($posAmt -eq 0) {
    Write-Host "이미 청산됨 → 종료" -ForegroundColor Yellow
    exit 0
}

Write-Host ""
Write-Host "==== STEP 2: 기존 Algo 주문 취소 (SL/TP/Trailing) ===="
$algo = Get-BF "/fapi/v1/openAlgoOrders" "symbol=HUSDT"
if ($algo) {
    foreach ($a in $algo) {
        $algoId = $a.algoId
        Write-Host "Cancel algo orderType=$($a.orderType) algoId=$algoId"
        $del = Delete-BF "/fapi/v1/algoOrder" "symbol=HUSDT&algoId=$algoId"
        if ($del) { Write-Host "  ✅ 취소됨" -ForegroundColor Green }
    }
}

Write-Host ""
Write-Host "==== STEP 3: 일반 openOrders 취소 ===="
$del2 = Delete-BF "/fapi/v1/allOpenOrders" "symbol=HUSDT"
if ($del2) { Write-Host "  ✅ 모든 일반주문 취소" -ForegroundColor Green }

Write-Host ""
Write-Host "==== STEP 4: 시장가 청산 (reduceOnly) ===="
# LONG 포지션이므로 SELL 으로 청산
$qty = [math]::Abs($posAmt)
$closeSide = if ($posAmt -gt 0) { "SELL" } else { "BUY" }
$qtyStr = $qty.ToString("F0")
Write-Host "MARKET $closeSide qty=$qtyStr reduceOnly=true"
$order = Post-BF "/fapi/v1/order" "symbol=HUSDT&side=$closeSide&type=MARKET&quantity=$qtyStr&reduceOnly=true"
if ($order) {
    Write-Host "✅ 시장가 청산 주문 전송 완료" -ForegroundColor Green
    Write-Host "  orderId=$($order.orderId) status=$($order.status)"
}

Write-Host ""
Start-Sleep -Seconds 2
Write-Host "==== STEP 5: 청산 후 포지션 확인 ===="
$pos2 = Get-BF "/fapi/v2/positionRisk" "symbol=HUSDT"
if ($pos2) {
    $pos2 | Select-Object symbol, positionAmt, unRealizedProfit | Format-List
}

Write-Host ""
Write-Host "==== STEP 6: DB TradeHistory Id=4183 업데이트 ===="
$cn2 = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn2.Open()
$cm2 = $cn2.CreateCommand()
$pnlStr = $unrealized.ToString("F4")
$cm2.CommandText = @"
UPDATE TradeHistory
SET IsClosed = 1,
    ExitTime = GETUTCDATE(),
    ExitPrice = $markPrice,
    PnL = $pnlStr,
    PnLPercent = (($markPrice - $entryPrice) / $entryPrice * 100 * 5),
    ExitReason = 'MANUAL_CLAUDE_CLOSE_USER_REQUEST'
WHERE Id = 4183 AND IsClosed = 0
"@
$updated = $cm2.ExecuteNonQuery()
$cn2.Close()
Write-Host "DB 업데이트: $updated 행 수정 완료" -ForegroundColor Green
