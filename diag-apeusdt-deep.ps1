$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
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
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection)
    $cn.Open()
    $cm = $cn.CreateCommand()
    $cm.CommandText = $sql
    $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet
    [void]$ap.Fill($ds)
    $cn.Close()
    return $ds.Tables[0]
}

Write-Host "==== [1] APE 5m 24h - basic entry candidates ===="
$klines = Invoke-RestMethod -Uri "https://fapi.binance.com/fapi/v1/klines?symbol=APEUSDT&interval=5m&limit=288" -TimeoutSec 15
$volumes = @()
$opps = @()
for ($i = 0; $i -lt $klines.Count; $i++) {
    $k = $klines[$i]
    $vol = [double]$k[5]
    $volumes += $vol
    if ($i -lt 11) { continue }
    $avgVol10 = ($volumes[($i-11)..($i-1)] | Measure-Object -Average).Average
    $o = [double]$k[1]; $c = [double]$k[4]
    $chg = if ($o -gt 0) { ($c - $o) / $o * 100 } else { 0 }
    $vr = if ($avgVol10 -gt 0) { $vol / $avgVol10 } else { 0 }
    $bull = $c -gt $o
    $ts = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$k[0]).ToOffset([TimeSpan]::FromHours(9))
    if ($bull -and $chg -ge 1.5 -and $vr -ge 2.0) {
        $opps += [PSCustomObject]@{
            Time = $ts.ToString("MM-dd HH:mm")
            Open = $o
            Close = $c
            Chg = [math]::Round($chg, 2)
            VolMul = [math]::Round($vr, 2)
        }
    }
}
Write-Host ("Found candidates (5m bullish, +1.5%+, vol 2x+): " + $opps.Count)
Write-Host ""
$opps | Sort-Object Chg -Descending | Select-Object -First 30 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== [2] APE FooterLogs 24h ===="
$q2 = "SELECT TOP 50 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST, LEFT(Message, 220) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR, -24, GETUTCDATE()) AND Message LIKE '%APE%' ORDER BY Timestamp DESC"
Q $q2 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [3] APE Bot_Log 24h ===="
$q3 = "SELECT EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST, Direction, Allowed, ML_Conf, LEFT(Reason, 120) AS Reason FROM Bot_Log WHERE Symbol='APEUSDT' AND UserId IN (1,10) AND EventTime >= DATEADD(HOUR, -24, GETUTCDATE()) ORDER BY EventTime ASC"
Q $q3 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [4] Daily limit logs ===="
$q4 = "SELECT TOP 20 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR, -24, GETUTCDATE()) AND (Message LIKE '%일일한도%' OR Message LIKE '%/60%' OR Message LIKE '%/500%' OR Message LIKE '%MAX_DAILY%') ORDER BY Timestamp DESC"
Q $q4 | Format-Table -AutoSize -Wrap
