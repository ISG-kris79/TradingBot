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

# Binance 24h ticker 에서 상승률 10%+ 코인 추출
Write-Host "==== [1] Binance 24h 상승률 +10% 이상 USDT 선물 코인 ====" -ForegroundColor Cyan
$tickers = Invoke-RestMethod -Uri "https://fapi.binance.com/fapi/v1/ticker/24hr" -TimeoutSec 15
$uptrend = $tickers | Where-Object { $_.symbol.EndsWith("USDT") -and [double]$_.priceChangePercent -ge 10 } | Sort-Object { [double]$_.priceChangePercent } -Descending
$count = $uptrend.Count
Write-Host "총 $count 개 발견" -ForegroundColor Yellow
$uptrend | Select-Object -First 25 | ForEach-Object {
    Write-Host ("  {0,-20} 24h={1,7:F2}% lastPrice={2,12} volQuote={3,15:N0}" -f $_.symbol, [double]$_.priceChangePercent, $_.lastPrice, [double]$_.quoteVolume)
}

Write-Host ""
Write-Host "==== [2] 위 상승 코인들에 대한 Bot_Log 차단 사유 (최근 6시간) ====" -ForegroundColor Cyan
$symbolList = ($uptrend | Select-Object -First 25 | ForEach-Object { "'$($_.symbol)'" }) -join ","
if ($symbolList) {
    $q2 = @"
SELECT
  Symbol,
  COUNT(*) AS Attempts,
  SUM(CASE WHEN Allowed = 1 THEN 1 ELSE 0 END) AS Allowed,
  SUM(CASE WHEN Allowed = 0 THEN 1 ELSE 0 END) AS Blocked,
  MAX(LEFT(Reason, 80)) AS LastBlockReason,
  CAST(MAX(ML_Conf) AS DECIMAL(6,4)) AS MaxMLConf
FROM Bot_Log
WHERE UserId IN (1,10) AND Symbol IN ($symbolList)
  AND EventTime >= DATEADD(HOUR, -6, GETUTCDATE())
GROUP BY Symbol
ORDER BY Attempts DESC
"@
    Q $q2 | Format-Table -AutoSize -Wrap
}

Write-Host ""
Write-Host "==== [3] 차단 사유별 통계 (전체, 최근 6시간) ====" -ForegroundColor Cyan
$q3 = @'
SELECT
  LEFT(Reason, 60) AS Reason,
  COUNT(*) AS Cnt,
  COUNT(DISTINCT Symbol) AS Symbols,
  CAST(MAX(ML_Conf) AS DECIMAL(6,4)) AS MaxConf
FROM Bot_Log
WHERE UserId IN (1,10) AND Allowed = 0
  AND EventTime >= DATEADD(HOUR, -6, GETUTCDATE())
GROUP BY LEFT(Reason, 60)
ORDER BY Cnt DESC
'@
Q $q3 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== [4] FooterLogs 일일한도/슬롯/감시진입차단 (최근 6시간) ====" -ForegroundColor Cyan
$q4 = @'
SELECT TOP 30 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -6, GETUTCDATE())
  AND (Message LIKE '%일일한도%' OR Message LIKE '%감시진입차단%' OR Message LIKE '%일일 PUMP%' OR Message LIKE '%슬롯 포화%' OR Message LIKE '%PUMP_BLOCKED%' OR Message LIKE '%차단%')
ORDER BY Timestamp DESC
'@
Q $q4 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] 현재 활성 포지션 + 슬롯 사용 ====" -ForegroundColor Cyan
$q5 = @'
SELECT
  Symbol, Strategy, Side, EntryPrice,
  CAST(PnL AS DECIMAL(10,4)) AS PnL,
  EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS EntryKST
FROM TradeHistory
WHERE UserId IN (1,10) AND IsClosed=0
ORDER BY EntryTime DESC
'@
Q $q5 | Format-Table -AutoSize
