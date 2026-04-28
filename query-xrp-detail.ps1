function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    if (-not $json.ConnectionStrings.IsEncrypted) { return $enc }
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create()
    $a.Key = $k
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
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS)
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

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[1] XRP 12:00-12:30 KST = 03:00-03:30 UTC 5분봉" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
# OpenTime은 UTC (DB 로그 기준 서버시간)
Q @"
SELECT Symbol, IntervalText, OpenTime, [Open], High, Low, [Close], Volume
FROM CandleData
WHERE Symbol='XRPUSDT'
  AND IntervalText='5m'
  AND OpenTime >= '2026-04-12 02:30:00'
  AND OpenTime <= '2026-04-12 04:00:00'
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[2] XRP 1분봉 동시간대" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Q @"
SELECT TOP 40 Symbol, IntervalText, OpenTime, [Open], High, Low, [Close]
FROM CandleData
WHERE Symbol='XRPUSDT'
  AND IntervalText='1m'
  AND OpenTime >= '2026-04-12 02:30:00'
  AND OpenTime <= '2026-04-12 04:00:00'
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[3] FooterLogs 스키마 확인" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Q "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='FooterLogs' ORDER BY ORDINAL_POSITION" | Format-Table -AutoSize

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[4] XRP 관련 FooterLogs 12시 근처 (KST 12:00 = UTC 03:00)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
# 컬럼명 모름 → wildcard + 최근
Q "SELECT TOP 3 * FROM FooterLogs ORDER BY Id DESC" | Format-List

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[5] TradeLogs XRP 관련 (최근 24h)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Id, Symbol, Side, Strategy, Price, AiScore, [Time], PnL
FROM TradeLogs
WHERE Symbol='XRPUSDT'
  AND [Time] >= DATEADD(HOUR, -24, GETDATE())
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "[6] LiveLogs XRP 최근 2시간" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Q "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='LiveLogs' ORDER BY ORDINAL_POSITION" | Format-Table -AutoSize
