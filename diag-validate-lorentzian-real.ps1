$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# === Real Lorentzian Engine validation against fresh chart data (Binance API) ===
# v5.20.7 actual C# engine — not SQL proxy

$dllRoot = Join-Path $PSScriptRoot "bin\Release\net9.0-windows\win-x64"
if (-not (Test-Path "$dllRoot\TradingBot.dll")) {
    Write-Host "[ERROR] TradingBot.dll not found at $dllRoot — run dotnet build -c Release first"
    exit 1
}

# Load deps (Binance.Net first for IBinanceKline interface)
$binanceDll = Get-ChildItem "$dllRoot\Binance.Net.dll" -ErrorAction SilentlyContinue
if ($binanceDll) { [Reflection.Assembly]::LoadFrom($binanceDll.FullName) | Out-Null }
$tbAsm = [Reflection.Assembly]::LoadFrom("$dllRoot\TradingBot.dll")

# Resolve types from loaded assembly directly
$svcType     = $tbAsm.GetType("TradingBot.Services.LorentzianV2.LorentzianV2Service")
$adapterType = $tbAsm.GetType("TradingBot.Services.KlineAdapter")
$candleType  = $tbAsm.GetType("TradingBot.Models.CandleData")

if (-not $svcType -or -not $adapterType -or -not $candleType) {
    Write-Host "[ERROR] Could not resolve types from TradingBot.dll"
    Write-Host ("  svc={0}  adapter={1}  candle={2}" -f $svcType, $adapterType, $candleType)
    exit 1
}

$svc = [Activator]::CreateInstance($svcType)
Write-Host "[OK] LorentzianV2Service instantiated"
Write-Host ("    NeighborsCount={0}, MaxBarsBack={1}, FeatureCount={2}" -f $svc.NeighborsCount, $svc.MaxBarsBack, $svc.FeatureCount)

# Symbols
$symbols = @(
    'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT','ADAUSDT','TRXUSDT','AVAXUSDT','LINKUSDT',
    'APEUSDT','API3USDT','DUSDT','DYMUSDT','DYDXUSDT','ESPORTSUSDT','SPORTFUNUSDT','KGENUSDT','PLAYUSDT','MAGMAUSDT',
    'GRIFFAINUSDT','WUSDT','PUMPBTCUSDT','ZBTUSDT','GALAUSDT','SOONUSDT','OPNUSDT','ZKPUSDT','BSBUSDT','KATUSDT'
)
$DAYS = 14
$BARS = 1500
$TP_PCT = 1.5; $SL_PCT = 0.7; $WIN = 12

function Fetch5m($sym, $endMs, $limit) {
    Start-Sleep -Milliseconds 800
    for ($try = 1; $try -le 4; $try++) {
        try {
            $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=5m&limit=$limit&endTime=$endMs"
            return Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 25
        } catch {
            if ($_.Exception.Message -like '*429*' -or $_.Exception.Message -like '*1003*') {
                Start-Sleep -Seconds ($try * 5); continue
            }
            return $null
        }
    }
    return $null
}

function MakeCandles($flat) {
    # CandleData[] in ascending OpenTime
    $list = New-Object "System.Collections.Generic.List``1[$($candleType.AssemblyQualifiedName)]"
    foreach ($b in $flat) {
        $c = [Activator]::CreateInstance($candleType)
        $c.OpenTime    = [DateTimeOffset]::FromUnixTimeMilliseconds([int64]$b[0]).UtcDateTime
        $c.OpenPrice   = [decimal][double]$b[1]
        $c.HighPrice   = [decimal][double]$b[2]
        $c.LowPrice    = [decimal][double]$b[3]
        $c.ClosePrice  = [decimal][double]$b[4]
        $c.Volume      = [decimal][double]$b[5]
        [void]$list.Add($c)
    }
    return $list
}

function ToKlineList($candles) {
    $klineList = New-Object "System.Collections.Generic.List``1[Binance.Net.Interfaces.IBinanceKline]"
    foreach ($c in $candles) {
        $a = [Activator]::CreateInstance($adapterType, $c)
        [void]$klineList.Add($a)
    }
    return $klineList
}

# Total stats
$totalBaseDecided = 0; $totalBaseTP = 0
$totalGateDecided = 0; $totalGateTP = 0; $totalGated = 0
$perSym = @()

$idx = 0
foreach ($sym in $symbols) {
    $idx++
    Write-Host ("[{0}/{1}] {2}" -f $idx, $symbols.Count, $sym) -NoNewline

    # Fetch ~14 days = ~4032 bars in 3 pages
    $endMs = [int64](([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalMilliseconds)
    $allChunks = @()
    for ($p=0; $p -lt 3; $p++) {
        $resp = Fetch5m $sym $endMs $BARS
        if (-not $resp -or $resp.Count -eq 0) { break }
        $allChunks = ,$resp + $allChunks
        $endMs = [int64]$resp[0][0] - 1
        if ($resp.Count -lt $BARS) { break }
    }
    $flat = @()
    foreach ($chunk in $allChunks) { foreach ($b in $chunk) { $flat += ,$b } }
    if ($flat.Count -lt 400) { Write-Host " skip ($($flat.Count) bars)"; continue }

    $candles = MakeCandles $flat
    $klines = ToKlineList $candles

    # Backfill (train) on first 70% of bars
    $trainEnd = [int]($candles.Count * 0.7)
    $trainCandles = $candles.GetRange(0, $trainEnd)
    $trainKlines = ToKlineList $trainCandles
    $added = $svc.BackfillFromCandles($sym, $trainKlines)

    # Test on remaining 30%
    $bDec = 0; $bTP = 0
    $gDec = 0; $gTP = 0; $gated = 0
    $highs = @($flat | ForEach-Object { [double]$_[2] })
    $lows  = @($flat | ForEach-Object { [double]$_[3] })
    $closes = @($flat | ForEach-Object { [double]$_[4] })

    for ($i = $trainEnd + 50; $i -lt $candles.Count - $WIN; $i++) {
        # TP/SL label (1.5%/0.7%/1h)
        $entry = $closes[$i]
        $tpPx = $entry * (1 + $TP_PCT/100.0)
        $slPx = $entry * (1 - $SL_PCT/100.0)
        $tpFirst = $false; $slFirst = $false
        for ($k=1; $k -le $WIN; $k++) {
            $h = $highs[$i+$k]; $l = $lows[$i+$k]
            if ($h -ge $tpPx -and $l -le $slPx) { $slFirst = $true; break }
            if ($h -ge $tpPx) { $tpFirst = $true; break }
            if ($l -le $slPx) { $slFirst = $true; break }
        }
        if (-not ($tpFirst -or $slFirst)) { continue }
        $bDec++
        if ($tpFirst) { $bTP++ }

        # Lorentzian gate
        $slice = $klines.GetRange(0, $i + 1)
        $pred = $svc.Predict($sym, $slice)
        if (-not $pred.IsReady) { $gated++; continue }
        if ($pred.Prediction -le 0) { $gated++; continue }
        $gDec++
        if ($tpFirst) { $gTP++ }
    }

    $bWR = if ($bDec -gt 0) { $bTP / [double]$bDec * 100.0 } else { 0 }
    $gWR = if ($gDec -gt 0) { $gTP / [double]$gDec * 100.0 } else { 0 }
    Write-Host (" base[{0} dec, {1:N1}%] gate[{2} dec, {3:N1}%, gated {4}]" -f $bDec, $bWR, $gDec, $gWR, $gated)

    $totalBaseDecided += $bDec; $totalBaseTP += $bTP
    $totalGateDecided += $gDec; $totalGateTP += $gTP; $totalGated += $gated
    $perSym += [pscustomobject]@{ Sym=$sym; Bdec=$bDec; Bwr=[math]::Round($bWR,2); Gdec=$gDec; Gwr=[math]::Round($gWR,2); Gated=$gated; Delta=[math]::Round($gWR - $bWR, 2) }
}

$bWRAll = if ($totalBaseDecided -gt 0) { $totalBaseTP / [double]$totalBaseDecided * 100.0 } else { 0 }
$gWRAll = if ($totalGateDecided -gt 0) { $totalGateTP / [double]$totalGateDecided * 100.0 } else { 0 }

Write-Host ""
Write-Host "=========================================================="
Write-Host "  REAL Lorentzian C# Engine — chart-data validation"
Write-Host "=========================================================="
Write-Host ("  Baseline (no gate): {0} decided, TP-first = {1:N2}%" -f $totalBaseDecided, $bWRAll)
Write-Host ("  + Lorentzian gate:  {0} decided, TP-first = {1:N2}%  (gated {2})" -f $totalGateDecided, $gWRAll, $totalGated)
Write-Host ("  Δ (gate - baseline) = {0:+0.00;-0.00}%" -f ($gWRAll - $bWRAll))
Write-Host ""
Write-Host "  [per-symbol — sorted by Delta]"
$perSym | Sort-Object Delta -Descending | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
