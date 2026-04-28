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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] GIGGLEUSDT 최근 거래 내역 ===" -ForegroundColor Cyan
Q @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, Quantity,
       PnL, PnLPercent, LEFT(ExitReason, 80) AS Reason,
       EntryTime, ExitTime,
       DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin,
       IsClosed, Strategy
FROM TradeHistory
WHERE Symbol='GIGGLEUSDT'
  AND EntryTime >= DATEADD(HOUR, -12, GETDATE())
ORDER BY Id ASC
"@ | Format-List

Write-Host "=== [2] GIGGLEUSDT 최근 진입 후 5분봉 가격 추이 ===" -ForegroundColor Cyan
$entry = Q "SELECT TOP 1 EntryTime FROM TradeHistory WHERE Symbol='GIGGLEUSDT' AND EntryTime >= DATEADD(HOUR,-12,GETDATE()) ORDER BY EntryTime DESC"
if ($entry.Rows.Count -gt 0) {
    $et = $entry.Rows[0].EntryTime
    Write-Host "진입 시각: $et" -ForegroundColor Yellow
    Q @"
SELECT OpenTime, [Open], High, Low, [Close],
       CAST((High-Low)/[Open]*100 AS decimal(6,2)) AS RangePct
FROM CandleData
WHERE Symbol='GIGGLEUSDT'
  AND IntervalText='5m'
  AND OpenTime BETWEEN DATEADD(MINUTE, -10, '$et') AND DATEADD(HOUR, 3, '$et')
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize
}

Write-Host "=== [3] GIGGLEUSDT 1분봉 진입 후 추이 (최대 피크 확인) ===" -ForegroundColor Cyan
if ($entry.Rows.Count -gt 0) {
    $et = $entry.Rows[0].EntryTime
    Q @"
SELECT TOP 50 OpenTime, [Open], High, Low, [Close]
FROM CandleData
WHERE Symbol='GIGGLEUSDT'
  AND IntervalText='1m'
  AND OpenTime BETWEEN '$et' AND DATEADD(HOUR, 3, '$et')
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize
}

Write-Host "=== [4] GIGGLEUSDT 포지션 관련 FooterLogs (최대 ROE / 청산 이유) ===" -ForegroundColor Cyan
if ($entry.Rows.Count -gt 0) {
    $et = $entry.Rows[0].EntryTime
    Q @"
SELECT TOP 50 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%GIGGLEUSDT%'
  AND (Message LIKE '%ROE%' OR Message LIKE '%trailing%' OR Message LIKE '%stair%' OR Message LIKE '%Partial%' OR Message LIKE '%청산%' OR Message LIKE '%익절%' OR Message LIKE '%계단%' OR Message LIKE '%TP%' OR Message LIKE '%최고%')
  AND Timestamp >= DATEADD(MINUTE, -10, '$et')
ORDER BY Id ASC
"@ | Format-Table -AutoSize
}
