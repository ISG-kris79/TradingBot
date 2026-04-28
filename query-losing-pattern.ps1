function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [System.Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 90
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 4 losing trades from 04-12:
# TRUUSDT  - entry 04:01:03 KST - 실제 타임스탬프는 DB에 KST 그대로
# AIOTUSDT - entry 03:05:24
# TAGUSDT  - entry 01:51:35
# SKYAIUSDT- entry 12:44:30 (04-12 00:44)

$symbols = @(
    @{ Sym='TRUUSDT';   Entry='2026-04-12 04:01:03'; EntryPrice=0.00795 },
    @{ Sym='AIOTUSDT';  Entry='2026-04-12 03:05:24'; EntryPrice=0.06670 },
    @{ Sym='TAGUSDT';   Entry='2026-04-12 01:51:35'; EntryPrice=0.00077760 },
    @{ Sym='SKYAIUSDT'; Entry='2026-04-12 00:44:30'; EntryPrice=0.13728140 }
)

foreach ($s in $symbols) {
    Write-Host ""
    Write-Host "################################################################" -ForegroundColor Magenta
    Write-Host "# $($s.Sym) — 진입 $($s.Entry) @ $($s.EntryPrice)" -ForegroundColor Magenta
    Write-Host "################################################################" -ForegroundColor Magenta

    # 진입 시점 전 60분 ~ 후 30분 5분봉
    $sql = @"
SELECT TOP 25 IntervalText, OpenTime,
       [Open], High, Low, [Close],
       CAST(Volume AS bigint) AS Volume,
       CAST((High-Low)/[Open]*100 AS decimal(6,2)) AS RangePct
FROM CandleData
WHERE Symbol='$($s.Sym)'
  AND IntervalText='5m'
  AND OpenTime BETWEEN DATEADD(HOUR, -1, '$($s.Entry)') AND DATEADD(MINUTE, 30, '$($s.Entry)')
ORDER BY OpenTime ASC
"@
    Q $sql | Format-Table -AutoSize

    Write-Host "--- 1분봉 진입 전 15분 ~ 후 15분 ---" -ForegroundColor Cyan
    $sql1m = @"
SELECT TOP 40 OpenTime,
       [Open], High, Low, [Close],
       CAST(Volume AS bigint) AS Volume
FROM CandleData
WHERE Symbol='$($s.Sym)'
  AND IntervalText='1m'
  AND OpenTime BETWEEN DATEADD(MINUTE, -15, '$($s.Entry)') AND DATEADD(MINUTE, 15, '$($s.Entry)')
ORDER BY OpenTime ASC
"@
    Q $sql1m | Format-Table -AutoSize
}

Write-Host ""
Write-Host "################################################################" -ForegroundColor Yellow
Write-Host "# 성공 케이스 비교: TRUUSDT 승(+170%) vs 패(-41%)" -ForegroundColor Yellow
Write-Host "################################################################" -ForegroundColor Yellow
Write-Host "TRUUSDT 성공 진입 동일 시각 (04:01:03) 했는데 ROE 트레일링 +170% 으로 성공" -ForegroundColor Yellow
Write-Host "즉 같은 시각 2건 진입 — 하나는 TP 트레일링, 하나는 1분 손절" -ForegroundColor Yellow
Q @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent,
       LEFT(ExitReason, 80) AS Reason, EntryTime, ExitTime,
       DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin, Strategy
FROM TradeHistory
WHERE Symbol='TRUUSDT' AND UserId=1
  AND EntryTime >= '2026-04-12 00:00:00' AND EntryTime < '2026-04-13 00:00:00'
ORDER BY Id DESC
"@ | Format-List
