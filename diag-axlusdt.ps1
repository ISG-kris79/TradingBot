function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
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
    $r = $cm.ExecuteReader()
    $rows = @()
    while ($r.Read()) {
        $row = [ordered]@{}
        for ($i = 0; $i -lt $r.FieldCount; $i++) {
            $row[$r.GetName($i)] = if ($r.IsDBNull($i)) { $null } else { $r[$i] }
        }
        $rows += [PSCustomObject]$row
    }
    $r.Close(); $cn.Close()
    return $rows
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Get max Id for filter optimization
$maxId = (Q "SELECT TOP 1 Id FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC")[0].Id
$lookbackId = $maxId - 200000  # ~200k row 스캔 (며칠치)

Write-Host "==== [1] AXLUSDT 매매 이력 (30일) ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Id, Side, ROUND(EntryPrice,4) AS EntryPx, ROUND(ExitPrice,4) AS ExitPx, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, ExitTime, LEFT(ExitReason,40) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE Symbol='AXLUSDT' AND EntryTime>=DATEADD(day,-30,GETDATE()) ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host "==== [2] AXLUSDT ML 신호 발생 이력 (FooterLogs, 24h) ====" -ForegroundColor Cyan
Q "SELECT TOP 30 Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%AXLUSDT%' AND (Message LIKE '%SIGNAL%' OR Message LIKE '%PREDICT%' OR Message LIKE '%PUMP%' OR Message LIKE '%MEGA%' OR Message LIKE '%CANDIDATE%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "==== [3] AXLUSDT 9시 패턴 - 24h 시간대별 5분봉 변동 ====" -ForegroundColor Cyan
Q @"
SELECT
    DATEPART(hour, OpenTime) AS Hour,
    COUNT(*) AS NumCandles,
    ROUND(MAX(HighPrice),4) AS HighPx,
    ROUND(MIN(LowPrice),4) AS LowPx,
    ROUND(SUM(Volume),0) AS Volume,
    ROUND((MAX(HighPrice) - MIN(LowPrice)) / NULLIF(MIN(LowPrice),0) * 100, 2) AS RangePct
FROM MarketCandles WITH (NOLOCK)
WHERE Symbol = 'AXLUSDT'
  AND OpenTime >= DATEADD(hour, -24, GETUTCDATE())
GROUP BY DATEPART(hour, OpenTime)
ORDER BY Hour
"@ | Format-Table -AutoSize

Write-Host "==== [4] AXL 9시 펌프 케이스 - 5분봉 가격/거래량 (오늘 KST 09:00~10:00) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20
    OpenTime,
    ROUND(OpenPrice,4) AS [Open],
    ROUND(HighPrice,4) AS [High],
    ROUND(LowPrice,4) AS [Low],
    ROUND(ClosePrice,4) AS [Close],
    ROUND(Volume,0) AS Volume,
    ROUND((ClosePrice - OpenPrice) / NULLIF(OpenPrice,0) * 100, 2) AS ChangePct
FROM MarketCandles WITH (NOLOCK)
WHERE Symbol = 'AXLUSDT'
  AND OpenTime >= DATEADD(hour, -16, GETUTCDATE())
ORDER BY OpenTime DESC
"@ | Format-Table -AutoSize

Write-Host "==== [5] 9시 펌프 코인 패턴 (지난 7일, KST 9시 5분봉 거래량 급증 TOP 10) ====" -ForegroundColor Cyan
Q @"
WITH PumpCandidates AS (
    SELECT
        Symbol,
        OpenTime,
        ClosePrice,
        OpenPrice,
        Volume,
        (ClosePrice - OpenPrice) / NULLIF(OpenPrice,0) * 100 AS ChangePct,
        AVG(Volume) OVER (PARTITION BY Symbol ORDER BY OpenTime ROWS BETWEEN 12 PRECEDING AND 1 PRECEDING) AS AvgVol1h
    FROM MarketCandles WITH (NOLOCK)
    WHERE Symbol LIKE '%USDT'
      AND OpenTime >= DATEADD(day, -7, GETUTCDATE())
      AND DATEPART(hour, DATEADD(hour, 9, OpenTime)) = 9  -- KST 9시 (UTC+9)
)
SELECT TOP 10
    Symbol,
    OpenTime,
    ROUND(ChangePct, 2) AS ChangePct,
    ROUND(Volume, 0) AS Volume,
    ROUND(AvgVol1h, 0) AS AvgVol1h,
    ROUND(Volume / NULLIF(AvgVol1h, 0), 1) AS VolMult
FROM PumpCandidates
WHERE AvgVol1h > 0
  AND Volume / NULLIF(AvgVol1h, 0) >= 5
  AND ChangePct >= 3
ORDER BY (Volume / NULLIF(AvgVol1h, 0)) DESC
"@ | Format-Table -AutoSize

Write-Host "==== [6] AXLUSDT가 PUMP 후보에 포함됐는지 (24h) ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%top60%' AND Message LIKE '%AXL%' ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "==== [7] 9시 KST 시간대 PUMP 후보 추출 빈도 (지난 7일) ====" -ForegroundColor Cyan
Q "SELECT DATEPART(hour, DATEADD(hour, 9, Timestamp)) AS KSTHour, COUNT(*) AS NumScans FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%top60%PUMP%CANDIDATE%' GROUP BY DATEPART(hour, DATEADD(hour, 9, Timestamp)) ORDER BY KSTHour" | Format-Table -AutoSize
