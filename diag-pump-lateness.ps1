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

# [1] PUMP 진입 후 5분 / 15분 / 1시간 PnL 변화 (얼마나 빨리 손실로 돌아서는지)
Write-Host "==== [1] PUMP 진입 후 손실 전환 시간 분석 (30일) ====" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '0-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '5-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '15-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '30-60min'
        ELSE 'over_60min'
    END AS HoldRange,
    COUNT(*) AS N,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    CAST(SUM(CASE WHEN PnL>0 THEN 1.0 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
    ROUND(AVG(PnL),2) AS AvgPnL,
    ROUND(SUM(PnL),2) AS TotalPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day,-30,GETDATE())
  AND IsClosed = 1 AND ExitTime IS NOT NULL
  AND Category IN ('PUMP', 'SPIKE')
GROUP BY
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '0-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '5-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '15-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '30-60min'
        ELSE 'over_60min'
    END
ORDER BY HoldRange
"@ | Format-Table -AutoSize

# [2] PUMP 손실 패턴 - "진입 직후 손실"
Write-Host "`n==== [2] PUMP 진입 5분 내 청산 사유 TOP 10 (30일) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10
    LEFT(ExitReason, 50) AS ExitReason,
    COUNT(*) AS N,
    ROUND(AVG(PnLPercent),2) AS AvgPnLPct,
    ROUND(AVG(DATEDIFF(MINUTE, EntryTime, ExitTime)),0) AS AvgHoldMin
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day,-30,GETDATE())
  AND IsClosed = 1
  AND DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5
  AND Category IN ('PUMP', 'SPIKE')
GROUP BY ExitReason
ORDER BY N DESC
"@ | Format-Table -AutoSize

# [3] PUMP 진입 후 즉시 손실 케이스 (10건 표본)
Write-Host "`n==== [3] PUMP 진입 후 5분 내 손절 표본 (최근 10건) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10
    Symbol, Side, ROUND(EntryPrice,4) AS EntryPx, ROUND(ExitPrice,4) AS ExitPx,
    ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct,
    DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
    EntryTime, LEFT(ExitReason,40) AS ExitReason
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day,-7,GETDATE())
  AND IsClosed = 1
  AND PnL < 0
  AND DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5
  AND Category IN ('PUMP', 'SPIKE')
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

# [4] 진입 후 즉시 손실 = 고점 매수 검증
# 같은 심볼, 진입 시각 +/- 5분의 캔들 고가/저가 비교
Write-Host "`n==== [4] 진입 가격이 직전 5분 고점 근처인지 (10건 표본) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10
    th.Symbol, ROUND(th.EntryPrice,4) AS EntryPx,
    ROUND(MAX(mc.HighPrice),4) AS Prev5mHigh,
    ROUND(MIN(mc.LowPrice),4) AS Prev5mLow,
    CAST((th.EntryPrice - MIN(mc.LowPrice)) / NULLIF(MAX(mc.HighPrice) - MIN(mc.LowPrice), 0) AS DECIMAL(5,2)) AS PositionInRange,
    ROUND(th.PnLPercent,2) AS PnLPct,
    th.EntryTime
FROM TradeHistory th WITH (NOLOCK)
INNER JOIN MarketCandles mc WITH (NOLOCK)
    ON mc.Symbol = th.Symbol
    AND mc.OpenTime BETWEEN DATEADD(MINUTE, -10, th.EntryTime) AND th.EntryTime
WHERE th.EntryTime >= DATEADD(day,-3,GETDATE())
  AND th.IsClosed = 1
  AND th.PnL < 0
  AND DATEDIFF(MINUTE, th.EntryTime, th.ExitTime) <= 10
  AND th.Category IN ('PUMP', 'SPIKE')
GROUP BY th.Id, th.Symbol, th.EntryPrice, th.PnLPercent, th.EntryTime
HAVING COUNT(mc.OpenTime) >= 1
ORDER BY th.EntryTime DESC
"@ | Format-Table -AutoSize

# [5] PUMP 시그널 발생 시각 vs 진입 시각 차이 (FooterLogs 분석)
Write-Host "`n==== [5] PUMP 시그널 → 진입 시간 지연 (최근 50건) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 50
    Timestamp,
    LEFT(Message, 200) AS Msg
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(hour,-12,GETDATE())
  AND (Message LIKE '%MEGA_PUMP%' OR Message LIKE '%PUMP%CANDIDATE%' OR Message LIKE '%[ENTRY]%PUMP%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap

# [6] 30일 PUMP/SPIKE 카테고리 손익
Write-Host "`n==== [6] 30일 PUMP/SPIKE 누적 ====" -ForegroundColor Cyan
Q @"
SELECT
    Category,
    COUNT(*) AS N,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    CAST(SUM(CASE WHEN PnL>0 THEN 1.0 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
    ROUND(SUM(PnL),2) AS TotalPnL,
    ROUND(AVG(PnL),2) AS AvgPnL,
    ROUND(SUM(CASE WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN PnL ELSE 0 END),2) AS Loss5minPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day,-30,GETDATE())
  AND IsClosed = 1
  AND Category IN ('PUMP', 'SPIKE')
GROUP BY Category
ORDER BY TotalPnL DESC
"@ | Format-Table -AutoSize
