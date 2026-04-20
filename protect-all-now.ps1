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
        if ($null -ne $er) { $s2 = New-Object System.IO.StreamReader($er.GetResponseStream()); return @{ ok = $false; body = $s2.ReadToEnd() } }
        return @{ ok = $false; body = $_.Exception.Message }
    }
}
function GetReq($ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    try { return Invoke-RestMethod -Uri ("https://fapi.binance.com$ep" + "?" + $qs + "&signature=" + $sig) -Headers @{ "X-MBX-APIKEY" = $apiKey } -Method Get -TimeoutSec 15 } catch { return $null }
}

$majors = @("BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT", "ADAUSDT", "DOGEUSDT", "AVAXUSDT", "LINKUSDT")

# Step 1: 활성 포지션
Write-Host "==== Active Positions ====" -ForegroundColor Cyan
$pos = GetReq "/fapi/v2/positionRisk" ""
$activeList = @()
if ($null -ne $pos) {
    $activeList = @($pos | Where-Object { [double]$_.positionAmt -ne 0 })
}
foreach ($p in $activeList) {
    Write-Host ("  " + $p.symbol + " amt=" + $p.positionAmt + " entry=" + $p.entryPrice + " mark=" + $p.markPrice + " lev=" + $p.leverage)
}
if ($activeList.Count -eq 0) { Write-Host "  no active positions"; exit 0 }

# Step 2: 기존 algoOrders
Write-Host ""
Write-Host "==== Existing Algo Orders ====" -ForegroundColor Cyan
$algoAll = GetReq "/fapi/v1/openAlgoOrders" ""
if ($null -ne $algoAll) {
    @($algoAll) | Group-Object symbol | ForEach-Object {
        $sym = $_.Name
        $sl = @($_.Group | Where-Object { $_.orderType -eq "STOP_MARKET" }).Count
        $tp = @($_.Group | Where-Object { $_.orderType -eq "TAKE_PROFIT_MARKET" }).Count
        $tr = @($_.Group | Where-Object { $_.orderType -eq "TRAILING_STOP_MARKET" }).Count
        Write-Host ("  " + $sym + " SL=" + $sl + " TP=" + $tp + " TR=" + $tr)
    }
}

# Step 3: 부족한 것만 등록
Write-Host ""
Write-Host "==== Register Missing Protection ====" -ForegroundColor Yellow
foreach ($p in $activeList) {
    $sym = [string]$p.symbol
    $isLong = [double]$p.positionAmt -gt 0
    $entry = [double]$p.entryPrice
    $qtyAbs = [Math]::Abs([double]$p.positionAmt)
    $lev = [int]$p.leverage
    $isMajor = $majors -contains $sym

    $existing = if ($null -ne $algoAll) { @(@($algoAll) | Where-Object { $_.symbol -eq $sym }) } else { @() }
    $hasSl = @($existing | Where-Object { $_.orderType -eq "STOP_MARKET" }).Count -gt 0
    $hasTp = @($existing | Where-Object { $_.orderType -eq "TAKE_PROFIT_MARKET" }).Count -gt 0
    $hasTr = @($existing | Where-Object { $_.orderType -eq "TRAILING_STOP_MARKET" }).Count -gt 0

    if ($hasSl -and $hasTp -and $hasTr) {
        Write-Host ("  " + $sym + " : 이미 SL+TP+TR 모두 등록됨 SKIP") -ForegroundColor Green
        continue
    }

    Write-Host ("  " + $sym + " : 등록 시작 (SL=" + $hasSl + " TP=" + $hasTp + " TR=" + $hasTr + ")")
    $closeSide = if ($isLong) { "SELL" } else { "BUY" }
    $slRoe = if ($isMajor) { -50.0 } else { -40.0 }
    $tpRoe = if ($isMajor) { 40.0 } else { 25.0 }
    $tpPartialRatio = if ($isMajor) { 0.4 } else { 0.6 }
    $callback = if ($isMajor) { 2.0 } else { [Math]::Max(0.1, [Math]::Min(5.0, 20.0 / $lev)) }

    # ROE -> price change
    $slPriceChange = $slRoe * $entry / ($lev * 100.0)
    $slPrice = if ($isLong) { $entry + $slPriceChange } else { $entry - $slPriceChange }
    $tpPriceChange = $tpRoe * $entry / ($lev * 100.0)
    $tpPrice = if ($isLong) { $entry + $tpPriceChange } else { $entry - $tpPriceChange }

    # 가격 정밀도 (단순화: tick size 5자리)
    $slPrice = [Math]::Round($slPrice, 5)
    $tpPrice = [Math]::Round($tpPrice, 5)

    $tpQty = [Math]::Floor($qtyAbs * $tpPartialRatio * 1000) / 1000
    if ($tpQty -le 0) { $tpQty = $qtyAbs }
    $trailQty = [Math]::Round($qtyAbs - $tpQty, 5)

    Write-Host ("    SL=$slPrice TP=$tpPrice TPqty=$tpQty TRqty=$trailQty callback=$callback% activation=$tpPrice")

    if (-not $hasSl) {
        $r = PostBody "/fapi/v1/algoOrder" "symbol=$sym&side=$closeSide&algoType=CONDITIONAL&type=STOP_MARKET&quantity=$qtyAbs&triggerPrice=$slPrice&reduceOnly=true"
        if ($r.ok) { Write-Host "    OK SL" -ForegroundColor Green } else { Write-Host ("    FAIL SL: " + $r.body) -ForegroundColor Red }
    }
    if (-not $hasTp) {
        $r = PostBody "/fapi/v1/algoOrder" "symbol=$sym&side=$closeSide&algoType=CONDITIONAL&type=TAKE_PROFIT_MARKET&quantity=$tpQty&triggerPrice=$tpPrice&reduceOnly=true"
        if ($r.ok) { Write-Host "    OK TP" -ForegroundColor Green } else { Write-Host ("    FAIL TP: " + $r.body) -ForegroundColor Red }
    }
    if (-not $hasTr -and $trailQty -gt 0) {
        $r = PostBody "/fapi/v1/algoOrder" "symbol=$sym&side=$closeSide&algoType=CONDITIONAL&type=TRAILING_STOP_MARKET&quantity=$trailQty&callbackRate=$callback&activationPrice=$tpPrice&reduceOnly=true"
        if ($r.ok) { Write-Host "    OK Trailing" -ForegroundColor Green } else { Write-Host ("    FAIL Trailing: " + $r.body) -ForegroundColor Red }
    }
}

Write-Host ""
Write-Host "==== Final Verification ====" -ForegroundColor Cyan
$algoAfter = GetReq "/fapi/v1/openAlgoOrders" ""
if ($null -ne $algoAfter) {
    @($algoAfter) | Group-Object symbol | ForEach-Object {
        $sym = $_.Name
        $sl = @($_.Group | Where-Object { $_.orderType -eq "STOP_MARKET" }).Count
        $tp = @($_.Group | Where-Object { $_.orderType -eq "TAKE_PROFIT_MARKET" }).Count
        $tr = @($_.Group | Where-Object { $_.orderType -eq "TRAILING_STOP_MARKET" }).Count
        $status = if ($sl -gt 0 -and $tp -gt 0 -and $tr -gt 0) { "OK" } else { "INCOMPLETE" }
        Write-Host ("  " + $sym + " SL=" + $sl + " TP=" + $tp + " TR=" + $tr + " [" + $status + "]")
    }
}
