$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'

# 봇과 IP 공유로 ticker API 막힘 → 직전 스캔에서 본 top30 하드코딩
$top30 = @(
    [pscustomobject]@{ symbol='APEUSDT';      priceChangePercent='99.71' },
    [pscustomobject]@{ symbol='API3USDT';     priceChangePercent='48.75' },
    [pscustomobject]@{ symbol='KATUSDT';      priceChangePercent='48.63' },
    [pscustomobject]@{ symbol='DUSDT';        priceChangePercent='28.43' },
    [pscustomobject]@{ symbol='ESPORTSUSDT';  priceChangePercent='27.02' },
    [pscustomobject]@{ symbol='SPORTFUNUSDT'; priceChangePercent='20.87' },
    [pscustomobject]@{ symbol='DYMUSDT';      priceChangePercent='19.81' },
    [pscustomobject]@{ symbol='KGENUSDT';     priceChangePercent='18.85' },
    [pscustomobject]@{ symbol='PLAYUSDT';     priceChangePercent='17.26' },
    [pscustomobject]@{ symbol='DYDXUSDT';     priceChangePercent='16.83' },
    [pscustomobject]@{ symbol='MAGMAUSDT';    priceChangePercent='16.68' },
    [pscustomobject]@{ symbol='GRIFFAINUSDT'; priceChangePercent='16.31' },
    [pscustomobject]@{ symbol='WUSDT';        priceChangePercent='16.23' },
    [pscustomobject]@{ symbol='PUMPBTCUSDT';  priceChangePercent='15.44' },
    [pscustomobject]@{ symbol='ZBTUSDT';      priceChangePercent='14.69' },
    [pscustomobject]@{ symbol='GALAUSDT';     priceChangePercent='14.33' },
    [pscustomobject]@{ symbol='SOONUSDT';     priceChangePercent='13.65' },
    [pscustomobject]@{ symbol='OPNUSDT';      priceChangePercent='13.41' },
    [pscustomobject]@{ symbol='ZKPUSDT';      priceChangePercent='12.88' },
    [pscustomobject]@{ symbol='BSBUSDT';      priceChangePercent='12.14' },
    [pscustomobject]@{ symbol='SKRUSDT';      priceChangePercent='12.12' },
    [pscustomobject]@{ symbol='EPICUSDT';     priceChangePercent='11.98' },
    [pscustomobject]@{ symbol='QUSDT';        priceChangePercent='11.94' },
    [pscustomobject]@{ symbol='CHRUSDT';      priceChangePercent='11.27' },
    [pscustomobject]@{ symbol='FLUXUSDT';     priceChangePercent='11.11' },
    [pscustomobject]@{ symbol='NFPUSDT';      priceChangePercent='11.02' },
    [pscustomobject]@{ symbol='HAEDALUSDT';   priceChangePercent='10.50' },
    [pscustomobject]@{ symbol='INITUSDT';     priceChangePercent='10.17' },
    [pscustomobject]@{ symbol='IOUSDT';       priceChangePercent='9.99'  },
    [pscustomobject]@{ symbol='PIXELUSDT';    priceChangePercent='9.86'  }
)

Write-Host "=========================================================="
Write-Host "  TOP 30 GAINERS (24h, hardcoded from prior scan @ 02:24 UTC)"
Write-Host ("  Now (UTC): " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))
Write-Host "=========================================================="

# KST 00:00 today = UTC -9h yesterday 15:00
$nowKst = (Get-Date).ToUniversalTime().AddHours(9)
$midKst = [DateTime]::new($nowKst.Year, $nowKst.Month, $nowKst.Day, 0, 0, 0, [DateTimeKind]::Unspecified)
$startUtc = $midKst.AddHours(-9)
$startMs = [int64]($startUtc.Subtract([DateTime]::new(1970,1,1,0,0,0,[DateTimeKind]::Utc))).TotalMilliseconds

Write-Host ""
Write-Host "=========================================================="
Write-Host ("  5m candle scan since KST 00:00 = UTC " + $startUtc.ToString('yyyy-MM-dd HH:mm'))
Write-Host "  BB Walk pattern (upper band riding) = v5.19.0 training target"
Write-Host "=========================================================="

$report = @()
foreach ($t in $top30) {
    $sym = $t.symbol

    $resp = $null
    $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=5m&startTime=$startMs&limit=500"
    for ($try = 1; $try -le 5; $try++) {
        try {
            $resp = Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 20
            Start-Sleep -Milliseconds 1500
            break
        } catch {
            $msg = $_.Exception.Message
            if ($msg -like '*429*' -or $msg -like '*Too Many*' -or $msg -like '*-1003*') {
                $wait = $try * 8
                Write-Host ("    [429] " + $sym + " backoff " + $wait + "s (try " + $try + "/5)")
                Start-Sleep -Seconds $wait
                continue
            }
            break
        }
    }
    if (-not $resp) {
        $report += [pscustomobject]@{ Symbol=$sym; Bars=0; Pump24h=('{0:N2}' -f [double]$t.priceChangePercent); MaxRise='-'; BBWalk='ERR'; Verdict='FETCH-ERR (429 5x)' }
        continue
    }
    if (-not $resp -or $resp.Count -lt 25) {
        $report += [pscustomobject]@{ Symbol=$sym; Bars=$resp.Count; Pump24h=('{0:N2}' -f [double]$t.priceChangePercent); MaxRise='-'; BBWalk='-'; Verdict='Insufficient' }
        continue
    }

    $closes = $resp | ForEach-Object { [double]$_[4] }
    $highs  = $resp | ForEach-Object { [double]$_[2] }

    $walkCount = 0
    $maxStreak = 0
    $curStreak = 0
    for ($i = 19; $i -lt $closes.Count; $i++) {
        $window = $closes[($i-19)..$i]
        $sma = ($window | Measure-Object -Sum).Sum / 20
        $var = ($window | ForEach-Object { ($_ - $sma) * ($_ - $sma) } | Measure-Object -Sum).Sum / 20
        $sd = [Math]::Sqrt($var)
        $upper = $sma + 2 * $sd
        if ($closes[$i] -ge $upper) {
            $walkCount++
            $curStreak++
            if ($curStreak -gt $maxStreak) { $maxStreak = $curStreak }
        } else {
            $curStreak = 0
        }
    }

    $first = [double]$resp[0][1]
    $maxHigh = ($highs | Measure-Object -Maximum).Maximum
    $rise = if ($first -gt 0) { ($maxHigh - $first) / $first * 100.0 } else { 0 }

    $bbWalkDetected = ($walkCount -ge 3 -or $maxStreak -ge 2)
    $verdict = if ($bbWalkDetected) { 'BB-WALK -> v5.19.0 TRAINS' } else { 'plain (no walk)' }

    $report += [pscustomobject]@{
        Symbol  = $sym
        Bars    = $resp.Count
        Pump24h = '{0:N2}' -f ([double]$t.priceChangePercent)
        MaxRise = '{0:N1}' -f $rise
        BBWalk  = ("cnt=" + $walkCount + " streak=" + $maxStreak)
        Verdict = $verdict
    }
}

$report | Format-Table -AutoSize -Wrap

$walkHits = ($report | Where-Object { $_.Verdict -like '*BB-WALK*' }).Count
$noData   = ($report | Where-Object { $_.Verdict -like 'Insufficient*' -or $_.Verdict -like 'FETCH-ERR*' }).Count
$plain    = $report.Count - $walkHits - $noData
Write-Host ""
Write-Host "=========================================================="
Write-Host "  SUMMARY"
Write-Host "=========================================================="
Write-Host ("  BB Walk detected (v5.19.0 training target) : " + $walkHits + " / " + $report.Count)
Write-Host ("  No / insufficient data                     : " + $noData)
Write-Host ("  Plain pump (no walk)                       : " + $plain)
Write-Host "=========================================================="
