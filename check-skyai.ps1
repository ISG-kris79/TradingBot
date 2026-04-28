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

Write-Host "==== SKYAIUSDT 최근 Bot_Log (진입 승인 시점 확인) ===="
Q "SELECT TOP 15 EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST, Direction, Allowed, ML_Conf, LEFT(Reason, 120) AS Reason FROM Bot_Log WHERE Symbol='SKYAIUSDT' AND UserId=1 ORDER BY EventTime DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== SKYAIUSDT 최근 TradeHistory ===="
Q "SELECT TOP 10 EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST, Strategy, Side, EntryPrice, Quantity, PnL, PnLPercent, ExitTime, ExitReason, IsClosed FROM TradeHistory WHERE Symbol='SKYAIUSDT' AND UserId=1 ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== SKYAIUSDT 1h 5분봉 가격 흐름 (Binance) ===="
$klineUrl = "https://fapi.binance.com/fapi/v1/klines?symbol=SKYAIUSDT&interval=5m&limit=12"
try {
    $klines = Invoke-RestMethod -Uri $klineUrl -TimeoutSec 10
    foreach ($k in $klines) {
        $ts = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$k[0]).ToOffset([TimeSpan]::FromHours(9)).ToString("HH:mm")
        $o = [double]$k[1]; $h = [double]$k[2]; $l = [double]$k[3]; $c = [double]$k[4]; $v = [double]$k[5]
        $chg = (($c - $o) / $o * 100).ToString("+0.00;-0.00")
        Write-Host ("{0}  O={1,10:F6}  H={2,10:F6}  L={3,10:F6}  C={4,10:F6}  chg={5}%  vol={6,10:F0}" -f $ts, $o, $h, $l, $c, $chg, $v)
    }
    $first = [double]$klines[0][1]
    $last = [double]$klines[-1][4]
    $totalChg = (($last - $first) / $first * 100)
    Write-Host ""
    Write-Host ("1시간 누적 가격변동: {0:+0.00;-0.00}%" -f $totalChg) -ForegroundColor $(if($totalChg -ge 0){"Green"}else{"Red"})
}
catch {
    Write-Host "Kline API err: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "==== SKYAIUSDT FooterLogs (최근 30분) ===="
$f = @'
SELECT TOP 30 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS TimeKST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE, -30, GETUTCDATE())
  AND Message LIKE '%SKYAI%'
ORDER BY Timestamp DESC
'@
Q $f | Format-Table -AutoSize -Wrap
