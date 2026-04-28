$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'

# 13:39 KST = 04:39 UTC. Binance 5m + 15m (가장 가까운 봉)
$entryUtc = [DateTime]::new(2026,4,25,4,39,0,[DateTimeKind]::Utc)
$endMs = [int64]($entryUtc.AddMinutes(5).Subtract([DateTime]::new(1970,1,1,0,0,0,[DateTimeKind]::Utc))).TotalMilliseconds

function GetKlines($sym, $interval, $endMs, $limit) {
    $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=$interval&endTime=$endMs&limit=$limit"
    return Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 20
}

Write-Host "=========================================================="
Write-Host "  TRADOORUSDT @ entry (KST 13:39 = UTC 04:39) M15 30bars"
Write-Host "=========================================================="
$k15 = GetKlines 'TRADOORUSDT' '15m' $endMs 30
Start-Sleep -Milliseconds 500
$lows = $k15 | ForEach-Object { [double]$_[3] }
$highs = $k15 | ForEach-Object { [double]$_[2] }
$closes = $k15 | ForEach-Object { [double]$_[4] }

$minLow = ($lows | Measure-Object -Minimum).Minimum
$maxHigh = ($highs | Measure-Object -Maximum).Maximum
$entryClose = $closes[-1]

$posPct = if ($maxHigh -gt $minLow) { ($entryClose - $minLow) / ($maxHigh - $minLow) * 100 } else { 0 }
$rise = if ($minLow -gt 0) { ($entryClose - $minLow) / $minLow * 100 } else { 0 }

Write-Host ("  M15 30bar low: " + ('{0:N5}' -f $minLow))
Write-Host ("  M15 30bar high:" + ('{0:N5}' -f $maxHigh))
Write-Host ("  Entry close:   " + ('{0:N5}' -f $entryClose))
Write-Host ("  posPct={0:N2}% riseFromLow={1:N2}%" -f $posPct, $rise)
Write-Host ""
Write-Host "  v5.19.6+ 가드 판정:"
Write-Host ("    HIGH_TOP_CHASING (pos>=90 AND rise>=5): " + (($posPct -ge 90 -and $rise -ge 5)))

# 직전 5봉 변동
$last5 = $closes | Select-Object -Last 5
$hi5 = ($last5 | Measure-Object -Maximum).Maximum
$lo5 = ($last5 | Measure-Object -Minimum).Minimum
$avg5 = ($last5 | Measure-Object -Average).Average
$range5 = if ($avg5 -gt 0) { ($hi5 - $lo5) / $avg5 * 100 } else { 0 }
$peakDist = if ($maxHigh -gt 0) { ($maxHigh - $entryClose) / $maxHigh * 100 } else { 0 }
Write-Host ("    TOP_DISTRIBUTION (rise>=5 AND range5<0.8 AND peakDist<1): rise={0:N2}% range5={1:N2}% peakDist={2:N2}% → {3}" -f $rise, $range5, $peakDist, (($rise -ge 5) -and ($range5 -lt 0.8) -and ($peakDist -lt 1.0)))

# 20봉 박스 검사
$last20 = $closes | Select-Object -Last 20
$hi20 = ($last20 | Measure-Object -Maximum).Maximum
$lo20 = ($last20 | Measure-Object -Minimum).Minimum
$avg20 = ($last20 | Measure-Object -Average).Average
$range20 = if ($avg20 -gt 0) { ($hi20 - $lo20) / $avg20 * 100 } else { 0 }
$sma20 = $avg20
$var20 = ($last20 | ForEach-Object { ($_-$sma20)*($_-$sma20) } | Measure-Object -Sum).Sum / 20
$sd20 = [Math]::Sqrt($var20)
$bbw = if ($sma20 -gt 0) { ($sd20 * 4) / $sma20 * 100 } else { 0 }
Write-Host ("    SIDEWAYS_BOX (range20<0.5 AND bbw<1.0): range20={0:N2}% bbw={1:N2}% → {2}" -f $range20, $bbw, (($range20 -lt 0.5) -and ($bbw -lt 1.0)))

Write-Host ""
Write-Host "=========================================================="
Write-Host "  Recent 30 candles (M15)"
Write-Host "=========================================================="
$rows = @()
for ($i = 0; $i -lt $k15.Count; $i++) {
    $b = $k15[$i]
    $tUtc = ([DateTime]::new(1970,1,1,0,0,0,[DateTimeKind]::Utc)).AddMilliseconds([int64]$b[0])
    $tKst = $tUtc.AddHours(9)
    $rows += [pscustomobject]@{
        KST = $tKst.ToString('MM-dd HH:mm')
        O = '{0:N5}' -f [double]$b[1]
        H = '{0:N5}' -f [double]$b[2]
        L = '{0:N5}' -f [double]$b[3]
        C = '{0:N5}' -f [double]$b[4]
        V = '{0:N0}' -f [double]$b[5]
    }
}
$rows | Format-Table -AutoSize
