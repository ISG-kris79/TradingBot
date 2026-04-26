$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()

Write-Host "=========================================================="
Write-Host "  [1] Top 30 symbols (하드코딩 - GROUP BY 인덱스 부족 우회)"
Write-Host "=========================================================="
$top = @(
    'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT','ADAUSDT','TRXUSDT','AVAXUSDT','LINKUSDT',
    'APEUSDT','API3USDT','KATUSDT','DUSDT','ESPORTSUSDT','SPORTFUNUSDT','DYMUSDT','KGENUSDT','PLAYUSDT','DYDXUSDT',
    'MAGMAUSDT','GRIFFAINUSDT','WUSDT','PUMPBTCUSDT','ZBTUSDT','GALAUSDT','SOONUSDT','OPNUSDT','ZKPUSDT','BSBUSDT'
)
Write-Host ("    심볼 " + $top.Count + "개 (메이저 10 + 알트 20)")

# RSI/SMA/EMA 헬퍼
function CalcRSI($closes, $period) {
    if ($closes.Count -lt $period + 1) { return 50.0 }
    $gain = 0.0; $loss = 0.0
    for ($i = 1; $i -le $period; $i++) {
        $diff = $closes[$i] - $closes[$i-1]
        if ($diff -gt 0) { $gain += $diff } else { $loss -= $diff }
    }
    $avgG = $gain / $period; $avgL = $loss / $period
    for ($i = $period + 1; $i -lt $closes.Count; $i++) {
        $diff = $closes[$i] - $closes[$i-1]
        $g = if ($diff -gt 0) { $diff } else { 0 }
        $l = if ($diff -lt 0) { -$diff } else { 0 }
        $avgG = ($avgG * ($period - 1) + $g) / $period
        $avgL = ($avgL * ($period - 1) + $l) / $period
    }
    if ($avgL -lt 1e-12) { return 100.0 }
    $rs = $avgG / $avgL
    return 100.0 - (100.0 / (1.0 + $rs))
}

Write-Host ""
Write-Host "=========================================================="
Write-Host "  [2] per-symbol win-rate (4-bar forward, RSI-based baseline)"
Write-Host "  Rule: RSI(14) < 30 → 4봉 후 가격 상승 예측"
Write-Host "       RSI(14) > 70 → 4봉 후 가격 하락 예측"
Write-Host "=========================================================="

$report = @()
foreach ($sym in $top) {
    # [DB Symbol 인덱스 부재 → Binance API 직접 사용]
    Start-Sleep -Milliseconds 1200
    $resp = $null
    for ($try = 1; $try -le 5; $try++) {
        try {
            $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=5m&limit=1500"
            $resp = Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 20
            break
        } catch {
            if ($_.Exception.Message -like '*429*' -or $_.Exception.Message -like '*1003*') {
                Start-Sleep -Seconds ($try * 5)
                continue
            }
            break
        }
    }
    if (-not $resp -or $resp.Count -lt 30) { continue }
    $closes = @($resp | ForEach-Object { [double]$_[4] })

    $signals = 0; $correct = 0
    for ($i = 14; $i -lt $closes.Count - 4; $i++) {
        $window = $closes[($i-14)..$i]
        $rsi = CalcRSI $window 14
        $signal = 0
        if ($rsi -lt 30) { $signal = 1 }
        elseif ($rsi -gt 70) { $signal = -1 }
        if ($signal -eq 0) { continue }
        $signals++
        $future = $closes[$i + 4]
        $cur = $closes[$i]
        $actual = if ($future -gt $cur) { 1 } elseif ($future -lt $cur) { -1 } else { 0 }
        if (($signal -eq 1 -and $actual -eq 1) -or ($signal -eq -1 -and $actual -eq -1)) { $correct++ }
    }

    if ($signals -ge 5) {
        $wr = $correct / [double]$signals * 100.0
        $report += [pscustomobject]@{
            Symbol  = $sym
            Bars    = $closes.Count
            Signals = $signals
            Correct = $correct
            WinRate = '{0:N2}' -f $wr
        }
    }
}

Write-Host ""
Write-Host "  [TOP 15 RSI baseline win-rate]"
$report | Sort-Object @{Expression={[double]$_.WinRate}; Descending=$true} | Select-Object -First 15 | Format-Table -AutoSize
Write-Host ""
Write-Host "  [BOTTOM 10]"
$report | Sort-Object @{Expression={[double]$_.WinRate}} | Select-Object -First 10 | Format-Table -AutoSize

# 글로벌 평균
$totalSig = ($report | ForEach-Object { $_.Signals } | Measure-Object -Sum).Sum
$totalCor = ($report | ForEach-Object { $_.Correct } | Measure-Object -Sum).Sum
$avg = if ($totalSig -gt 0) { $totalCor / [double]$totalSig * 100.0 } else { 0 }
Write-Host ""
Write-Host ("  >>> 전체 RSI baseline: " + $totalSig + " 신호 / " + $totalCor + " 정답 / win-rate = " + ('{0:N2}' -f $avg) + "%")
Write-Host "      (50% = 랜덤, 55%+ = 의미있는 baseline, 60%+ = 좋은 모델 가능)"

$cn.Close()
