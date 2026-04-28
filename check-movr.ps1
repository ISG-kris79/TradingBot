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

Write-Host "==== MOVRUSDT 과거 매매기록 ===="
Q "SELECT EntryTime, Strategy, Side, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(PnL AS DECIMAL(10,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, ExitReason FROM TradeHistory WHERE Symbol='MOVRUSDT' AND UserId=1 ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== MOVRUSDT 최근 Bot_Log 차단 사유 ===="
Q "SELECT TOP 20 EventTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST, Direction, Allowed, ML_Conf, LEFT(Reason,100) AS Reason FROM Bot_Log WHERE Symbol='MOVRUSDT' AND UserId=1 ORDER BY EventTime DESC" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== Binance MOVRUSDT 최근 1시간 5분봉 ===="
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$klineUrl = "https://fapi.binance.com/fapi/v1/klines?symbol=MOVRUSDT&interval=5m&limit=12"
try {
    $klines = Invoke-RestMethod -Uri $klineUrl -TimeoutSec 10
    foreach ($k in $klines) {
        $ts = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$k[0]).ToOffset([TimeSpan]::FromHours(9)).ToString("HH:mm")
        $o = [double]$k[1]; $h = [double]$k[2]; $l = [double]$k[3]; $c = [double]$k[4]; $v = [double]$k[5]
        $chg = (($c - $o) / $o * 100).ToString("+0.00;-0.00")
        Write-Host ("{0} O={1,8:F4} H={2,8:F4} L={3,8:F4} C={4,8:F4} chg={5}% vol={6,10:F0}" -f $ts, $o, $h, $l, $c, $chg, $v)
    }

    $first = [double]$klines[0][1]
    $last = [double]$klines[-1][4]
    $totalChg = (($last - $first) / $first * 100)
    Write-Host ""
    Write-Host ("1시간 누적 가격변동: {0:+0.00;-0.00}%" -f $totalChg)
}
catch {
    Write-Host "Kline API err: $($_.Exception.Message)"
}
