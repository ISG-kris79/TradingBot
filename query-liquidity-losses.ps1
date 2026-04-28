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

Write-Host "##################################################################"
Write-Host "# 초소형/저유동성 손실 분석 (UserId=1, 04-12)"
Write-Host "##################################################################"
Write-Host ""

Write-Host "=== [1] 가격대별 진입 분포 (04-12 PUMP 전용) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN EntryPrice < 0.0001 THEN '01. 초초소형 < 0.0001 (페니)'
        WHEN EntryPrice < 0.001 THEN '02. 초소형 0.0001~0.001'
        WHEN EntryPrice < 0.01 THEN '03. 소형 0.001~0.01'
        WHEN EntryPrice < 0.1 THEN '04. 중소형 0.01~0.1'
        WHEN EntryPrice < 1 THEN '05. 중형 0.1~1'
        WHEN EntryPrice < 10 THEN '06. 대형 1~10'
        ELSE '07. 초대형 10+'
    END AS PriceBucket,
    COUNT(*) AS Entries,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPct
FROM TradeHistory
WHERE UserId=1
  AND IsClosed = 1
  AND EntryTime >= '2026-04-12 00:00:00'
  AND EntryTime < '2026-04-13 00:00:00'
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY
    CASE
        WHEN EntryPrice < 0.0001 THEN '01. 초초소형 < 0.0001 (페니)'
        WHEN EntryPrice < 0.001 THEN '02. 초소형 0.0001~0.001'
        WHEN EntryPrice < 0.01 THEN '03. 소형 0.001~0.01'
        WHEN EntryPrice < 0.1 THEN '04. 중소형 0.01~0.1'
        WHEN EntryPrice < 1 THEN '05. 중형 0.1~1'
        WHEN EntryPrice < 10 THEN '06. 대형 1~10'
        ELSE '07. 초대형 10+'
    END
ORDER BY PriceBucket
"@ | Format-Table -AutoSize

Write-Host "=== [2] 1분내 즉시 손절 + 가격대 (04-12) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, EntryPrice, ExitPrice, PnL, PnLPercent,
       DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
       LEFT(ExitReason, 40) AS Reason,
       CASE
           WHEN EntryPrice < 0.001 THEN '초소형'
           WHEN EntryPrice < 0.01 THEN '소형'
           WHEN EntryPrice < 0.1 THEN '중소형'
           ELSE '중대형'
       END AS Bucket
FROM TradeHistory
WHERE UserId=1
  AND IsClosed = 1 AND PnL < 0
  AND EntryTime >= '2026-04-12 00:00:00' AND EntryTime < '2026-04-13 00:00:00'
  AND DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5
ORDER BY PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 진입가 대비 체결가 슬리피지 추정 (1분 내 손실) ===" -ForegroundColor Cyan
Q @"
-- 5분봉 첫 봉의 Open 과 진입가 비교 → 슬리피지 추정
SELECT
    th.Symbol,
    th.EntryPrice,
    cd.[Open] AS CandleOpen,
    cd.High AS Candle5mHigh,
    cd.Low AS Candle5mLow,
    cd.Volume AS Candle5mVolume,
    CAST(((th.EntryPrice - cd.[Open]) / cd.[Open] * 100) AS decimal(8,3)) AS SlippagePct,
    th.PnL,
    th.PnLPercent,
    DATEDIFF(SECOND, th.EntryTime, th.ExitTime) AS HoldSec
FROM TradeHistory th
OUTER APPLY (
    SELECT TOP 1 [Open], High, Low, Volume
    FROM CandleData
    WHERE Symbol = th.Symbol AND IntervalText='5m'
      AND OpenTime <= th.EntryTime
    ORDER BY OpenTime DESC
) cd
WHERE th.UserId=1
  AND th.IsClosed=1 AND th.PnL < 0
  AND th.EntryTime >= '2026-04-12 00:00:00' AND th.EntryTime < '2026-04-13 00:00:00'
  AND DATEDIFF(MINUTE, th.EntryTime, th.ExitTime) <= 2
ORDER BY th.PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 초소형/소형 진입 성공률 (04-12) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN EntryPrice < 0.001 THEN 'A. 초소형 (<0.001)'
        WHEN EntryPrice < 0.01 THEN 'B. 소형 (0.001~0.01)'
        WHEN EntryPrice < 0.1 THEN 'C. 중소형 (0.01~0.1)'
        ELSE 'D. 중대형 (0.1+)'
    END AS Bucket,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS WinRatePct,
    SUM(PnL) AS TotalPnL,
    AVG(CASE WHEN PnL > 0 THEN PnLPercent END) AS AvgWinPct,
    AVG(CASE WHEN PnL < 0 THEN PnLPercent END) AS AvgLossPct
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
  AND EntryTime >= '2026-04-12 00:00:00' AND EntryTime < '2026-04-13 00:00:00'
  AND Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY
    CASE
        WHEN EntryPrice < 0.001 THEN 'A. 초소형 (<0.001)'
        WHEN EntryPrice < 0.01 THEN 'B. 소형 (0.001~0.01)'
        WHEN EntryPrice < 0.1 THEN 'C. 중소형 (0.01~0.1)'
        ELSE 'D. 중대형 (0.1+)'
    END
ORDER BY Bucket
"@ | Format-Table -AutoSize

Write-Host "=== [5] 손실 TOP 5 심볼의 진입 시점 5분봉 거래량/range ===" -ForegroundColor Cyan
Q @"
SELECT
    th.Symbol,
    th.EntryPrice,
    th.PnLPercent,
    DATEDIFF(SECOND, th.EntryTime, th.ExitTime) AS HoldSec,
    cd.Volume AS Candle5mVolume,
    CAST(cd.Volume * cd.[Close] / 1000000 AS decimal(10,2)) AS VolumeMillionUsdt,
    CAST((cd.High - cd.Low) / cd.[Open] * 100 AS decimal(6,2)) AS RangePct,
    LEFT(th.ExitReason, 40) AS Reason
FROM TradeHistory th
OUTER APPLY (
    SELECT TOP 1 [Open], High, Low, [Close], Volume
    FROM CandleData
    WHERE Symbol = th.Symbol AND IntervalText='5m'
      AND OpenTime <= th.EntryTime
    ORDER BY OpenTime DESC
) cd
WHERE th.UserId=1 AND th.IsClosed=1 AND th.PnL < 0
  AND th.EntryTime >= '2026-04-12 00:00:00' AND th.EntryTime < '2026-04-13 00:00:00'
ORDER BY th.PnL ASC
OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY
"@ | Format-Table -AutoSize

Write-Host "=== [6] 손실 심볼의 24h 거래량 추정 (5분봉 288개 합) ===" -ForegroundColor Cyan
Q @"
SELECT
    th.Symbol,
    th.EntryPrice,
    th.PnLPercent,
    CAST(v.Vol24hUsdt / 1000000 AS decimal(10,2)) AS Vol24hM,
    CASE
        WHEN v.Vol24hUsdt < 10000000 THEN '초저유동성 (<$10M)'
        WHEN v.Vol24hUsdt < 50000000 THEN '저유동성 ($10~50M)'
        WHEN v.Vol24hUsdt < 200000000 THEN '중유동성 ($50~200M)'
        ELSE '고유동성 ($200M+)'
    END AS LiqBucket
FROM TradeHistory th
OUTER APPLY (
    SELECT SUM(Volume * [Close]) AS Vol24hUsdt
    FROM CandleData
    WHERE Symbol = th.Symbol AND IntervalText='5m'
      AND OpenTime BETWEEN DATEADD(HOUR, -24, th.EntryTime) AND th.EntryTime
) v
WHERE th.UserId=1 AND th.IsClosed=1 AND th.PnL < 0
  AND th.EntryTime >= '2026-04-12 00:00:00' AND th.EntryTime < '2026-04-13 00:00:00'
ORDER BY th.PnL ASC
"@ | Format-Table -AutoSize

Write-Host "=== [7] 유동성 대 승률/손실 상관관계 ===" -ForegroundColor Cyan
Q @"
;WITH LiqEnriched AS (
    SELECT
        th.Id,
        th.Symbol,
        th.PnL,
        v.Vol24hUsdt,
        CASE
            WHEN v.Vol24hUsdt < 10000000 THEN 'A_Ultra'
            WHEN v.Vol24hUsdt < 50000000 THEN 'B_Low'
            WHEN v.Vol24hUsdt < 200000000 THEN 'C_Mid'
            ELSE 'D_High'
        END AS LiqBucket
    FROM TradeHistory th
    OUTER APPLY (
        SELECT SUM(Volume * [Close]) AS Vol24hUsdt
        FROM CandleData
        WHERE Symbol = th.Symbol AND IntervalText='5m'
          AND OpenTime BETWEEN DATEADD(HOUR, -24, th.EntryTime) AND th.EntryTime
    ) v
    WHERE th.UserId=1 AND th.IsClosed=1
      AND th.EntryTime >= '2026-04-12 00:00:00' AND th.EntryTime < '2026-04-13 00:00:00'
      AND th.Symbol NOT IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
)
SELECT
    LiqBucket,
    COUNT(*) AS Trades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / NULLIF(COUNT(*), 0) AS decimal(5,1)) AS WinRate,
    SUM(PnL) AS NetPnL,
    AVG(PnL) AS AvgPnL
FROM LiqEnriched
GROUP BY LiqBucket
ORDER BY LiqBucket
"@ | Format-Table -AutoSize
