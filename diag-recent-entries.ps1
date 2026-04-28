$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'
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
Write-Host "  Recent BUY/LONG entries (last 3h, all users)"
Write-Host "=========================================================="
$cm = $cn.CreateCommand()
$cm.CommandTimeout = 30
$cm.CommandText = @"
SELECT TOP 30 [Time], Symbol, Side, Strategy, EntryPrice, Quantity, ExitReason, AiScore, UserId
FROM TradeLogs
WHERE Side IN ('BUY','LONG') AND Strategy NOT LIKE 'PARTIAL%' AND [Time] >= DATEADD(HOUR, -3, GETUTCDATE())
ORDER BY [Time] DESC
"@
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
$ds = New-Object System.Data.DataSet
[void]$ap.Fill($ds)
$rows = $ds.Tables[0].Rows
Write-Host ("    Found: " + $rows.Count + " entries")
$ds.Tables[0] | Format-Table -AutoSize -Wrap

# 각 심볼의 진입 직전 5m + 15m 상태 분석
Write-Host ""
Write-Host "=========================================================="
Write-Host "  Per-symbol post-entry analysis"
Write-Host "=========================================================="
function GetKlines($sym, $interval, $limit) {
    $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=$interval&limit=$limit"
    return Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 20
}

$report = @()
$seen = @{}
foreach ($r in $rows) {
    $sym = [string]$r.Symbol
    if ($seen.ContainsKey($sym)) { continue }
    $seen[$sym] = $true
    $entryPx = [decimal]$r.EntryPrice
    $entryTime = [DateTime]$r.Time
    $strat = [string]$r.Strategy

    try {
        $k15 = GetKlines $sym '15m' 30
        Start-Sleep -Milliseconds 800
        if ($k15.Count -lt 25) { continue }
        $lows15 = $k15 | ForEach-Object { [double]$_[3] }
        $highs15 = $k15 | ForEach-Object { [double]$_[2] }
        $minLow = ($lows15 | Measure-Object -Minimum).Minimum
        $maxHigh = ($highs15 | Measure-Object -Maximum).Maximum
        $latestClose = [double]$k15[-1][4]
        $posPct = if ($maxHigh -gt $minLow) { ($latestClose - $minLow) / ($maxHigh - $minLow) * 100 } else { 0 }
        $riseFromLow = if ($minLow -gt 0) { ($latestClose - $minLow) / $minLow * 100 } else { 0 }
        $entryVsCur = if ($entryPx -gt 0) { (($latestClose - [double]$entryPx) / [double]$entryPx) * 100 } else { 0 }

        # 5m walk
        $k5 = GetKlines $sym '5m' 60
        Start-Sleep -Milliseconds 800
        $closes5 = $k5 | ForEach-Object { [double]$_[4] }
        $walk = 0; $maxStreak = 0; $cur = 0
        for ($i = 19; $i -lt $closes5.Count; $i++) {
            $win = $closes5[($i-19)..$i]
            $sma = ($win | Measure-Object -Sum).Sum / 20
            $var = ($win | ForEach-Object { ($_-$sma)*($_-$sma) } | Measure-Object -Sum).Sum / 20
            $sd = [Math]::Sqrt($var)
            $upper = $sma + 2*$sd
            if ($closes5[$i] -ge $upper) { $walk++; $cur++; if($cur -gt $maxStreak){$maxStreak=$cur} } else { $cur = 0 }
        }

        $verdict = if ($posPct -ge 90 -and $riseFromLow -ge 5) {
            'V5.19.6 GATE WOULD BLOCK (high-top)'
        } elseif ($posPct -ge 85 -and $riseFromLow -ge 3) {
            'OLD V5.19.5 WOULD BLOCK (now passes)'
        } else {
            'OK position'
        }

        $report += [pscustomobject]@{
            Symbol = $sym
            Strategy = $strat
            Entry = '{0:N5}' -f [double]$entryPx
            NowPct = '{0:N2}' -f $entryVsCur
            M15PosPct = '{0:N1}' -f $posPct
            M15Rise = '{0:N2}' -f $riseFromLow
            M5Walk = ('cnt=' + $walk + '/streak=' + $maxStreak)
            Verdict = $verdict
        }
    } catch {
        $report += [pscustomobject]@{
            Symbol = $sym; Strategy = $strat; Entry = '{0:N5}' -f [double]$entryPx
            NowPct = '-'; M15PosPct = '-'; M15Rise = '-'; M5Walk = 'ERR'
            Verdict = ('FETCH-ERR: ' + $_.Exception.Message.Substring(0, [Math]::Min(50, $_.Exception.Message.Length)))
        }
    }
}
$report | Format-Table -AutoSize -Wrap

$cn.Close()
