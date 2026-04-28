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
Write-Host "  [1] CTSI recent entries (TradeLogs 6h)"
Write-Host "=========================================================="
$cm = $cn.CreateCommand()
$cm.CommandTimeout = 30
$cm.CommandText = "SELECT TOP 5 * FROM TradeLogs WHERE Symbol = 'CTSIUSDT' AND [Time] >= DATEADD(HOUR, -6, GETUTCDATE()) ORDER BY [Time] DESC"
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
$ds = New-Object System.Data.DataSet
[void]$ap.Fill($ds)
$ds.Tables[0] | Format-List

function GetKlines($sym, $interval, $limit) {
    $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=$interval&limit=$limit"
    return Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 20
}

Write-Host ""
Write-Host "=========================================================="
Write-Host "  [2] CTSIUSDT 15m last 30 candles (bull/bear)"
Write-Host "=========================================================="
try {
    $k15 = GetKlines 'CTSIUSDT' '15m' 30
    Start-Sleep -Milliseconds 500
    $rows = @()
    for ($i = 0; $i -lt $k15.Count; $i++) {
        $b = $k15[$i]
        $o = [double]$b[1]; $h = [double]$b[2]; $l = [double]$b[3]; $c = [double]$b[4]; $v = [double]$b[5]
        $tUtc = ([DateTime]::new(1970,1,1,0,0,0,[DateTimeKind]::Utc)).AddMilliseconds([int64]$b[0])
        $tKst = $tUtc.AddHours(9)
        $bull = if ($c -gt $o) { "UP" } else { "DN" }
        $changePct = if ($o -gt 0) { ($c - $o) / $o * 100 } else { 0 }
        $rows += [pscustomobject]@{
            KST = $tKst.ToString('MM-dd HH:mm')
            Bull = $bull
            Open = '{0:N5}' -f $o
            Close = '{0:N5}' -f $c
            High = '{0:N5}' -f $h
            ChgPct = '{0:N2}' -f $changePct
            Vol = '{0:N0}' -f $v
        }
    }
    $rows | Format-Table -AutoSize

    $streak = 0
    for ($i = $k15.Count - 1; $i -ge 0; $i--) {
        $o = [double]$k15[$i][1]; $c = [double]$k15[$i][4]
        if ($c -gt $o) { $streak++ } else { break }
    }
    Write-Host ("    >>> Trailing consecutive UP candles: " + $streak)

    $allLows = $k15 | ForEach-Object { [double]$_[3] }
    $allHighs = $k15 | ForEach-Object { [double]$_[2] }
    $minLow = ($allLows | Measure-Object -Minimum).Minimum
    $maxHigh = ($allHighs | Measure-Object -Maximum).Maximum
    $latestClose = [double]$k15[-1][4]
    $rangePct = if ($maxHigh -gt $minLow) { ($latestClose - $minLow) / ($maxHigh - $minLow) * 100 } else { 0 }
    Write-Host ("    >>> Position in 30bar range: " + ('{0:N1}' -f $rangePct) + "% (0=low 100=high)")
    $riseFromLow = (($latestClose - $minLow) / $minLow * 100)
    Write-Host ("    >>> Rise from 30bar low: " + ('{0:N2}' -f $riseFromLow) + "%")
    $pullbackFromHigh = (($latestClose - $maxHigh) / $maxHigh * 100)
    Write-Host ("    >>> Pullback from 30bar high: " + ('{0:N2}' -f $pullbackFromHigh) + "%")
} catch {
    Write-Host ("    Fetch fail: " + $_.Exception.Message)
}

Write-Host ""
Write-Host "=========================================================="
Write-Host "  [3] CTSIUSDT 5m last 60 candles BB Walk + position"
Write-Host "=========================================================="
try {
    $k5 = GetKlines 'CTSIUSDT' '5m' 60
    Start-Sleep -Milliseconds 500
    $closes = $k5 | ForEach-Object { [double]$_[4] }
    $highs  = $k5 | ForEach-Object { [double]$_[2] }
    $latest5 = [double]$k5[-1][4]
    $first5  = [double]$k5[0][1]
    $max5    = ($highs | Measure-Object -Maximum).Maximum
    Write-Host ("    First (5h ago): " + ('{0:N5}' -f $first5))
    Write-Host ("    Max High      : " + ('{0:N5}' -f $max5))
    Write-Host ("    Latest Close  : " + ('{0:N5}' -f $latest5))
    Write-Host ("    Total rise    : " + ('{0:N2}' -f (($max5 - $first5)/$first5*100)) + "%")
    Write-Host ("    Pullback peak : " + ('{0:N2}' -f (($latest5 - $max5)/$max5*100)) + "%")

    $walk = 0; $maxStreak = 0; $cur = 0
    for ($i = 19; $i -lt $closes.Count; $i++) {
        $win = $closes[($i-19)..$i]
        $sma = ($win | Measure-Object -Sum).Sum / 20
        $var = ($win | ForEach-Object { ($_-$sma)*($_-$sma) } | Measure-Object -Sum).Sum / 20
        $sd = [Math]::Sqrt($var)
        $upper = $sma + 2*$sd
        if ($closes[$i] -ge $upper) { $walk++; $cur++; if($cur -gt $maxStreak){$maxStreak=$cur} } else { $cur = 0 }
    }
    Write-Host ("    5m BB Walk count(40bars): " + $walk + " streak: " + $maxStreak)
} catch {
    Write-Host ("    Fetch fail: " + $_.Exception.Message)
}

$cn.Close()
