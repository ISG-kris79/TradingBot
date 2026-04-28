$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}

Write-Host "===== 종합 점검 보고서 데이터 ===" -ForegroundColor Cyan

Write-Host "`n[1] 30일 전체 / 7일 / 1일 비교" -ForegroundColor Yellow
$sql1 = @'
SELECT '30d' AS Period, COUNT(*) AS N,
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(AVG(PnLPercent) AS DECIMAL(8,2)) AS AvgRoi,
    AVG(holdingMinutes) AS AvgHold
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-30,GETUTCDATE())
UNION ALL
SELECT '7d', COUNT(*),
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)),
    CAST(SUM(PnL) AS DECIMAL(18,2)),
    CAST(AVG(PnL) AS DECIMAL(18,4)),
    CAST(AVG(PnLPercent) AS DECIMAL(8,2)),
    AVG(holdingMinutes)
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-7,GETUTCDATE())
UNION ALL
SELECT '1d', COUNT(*),
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)),
    CAST(SUM(PnL) AS DECIMAL(18,2)),
    CAST(AVG(PnL) AS DECIMAL(18,4)),
    CAST(AVG(PnLPercent) AS DECIMAL(8,2)),
    AVG(holdingMinutes)
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-1,GETUTCDATE())
'@
Q $sql1 | Format-Table -AutoSize

Write-Host "`n[2] PnL 분포 — 사용자 지적 (-1~1% 횡보 진입 비중)" -ForegroundColor Yellow
$sql2 = @'
SELECT
    CASE
        WHEN PnLPercent < -10 THEN '01.<-10%'
        WHEN PnLPercent < -5  THEN '02.-10~-5%'
        WHEN PnLPercent < -1  THEN '03.-5~-1%'
        WHEN PnLPercent < 1   THEN '04.-1~+1pct sideways'
        WHEN PnLPercent < 5   THEN '05.+1~+5%'
        WHEN PnLPercent < 10  THEN '06.+5~+10%'
        ELSE '07.>+10%' END AS Bucket,
    COUNT(*) AS Cnt,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY CASE
        WHEN PnLPercent < -10 THEN '01.<-10%'
        WHEN PnLPercent < -5  THEN '02.-10~-5%'
        WHEN PnLPercent < -1  THEN '03.-5~-1%'
        WHEN PnLPercent < 1   THEN '04.-1~+1pct sideways'
        WHEN PnLPercent < 5   THEN '05.+1~+5%'
        WHEN PnLPercent < 10  THEN '06.+5~+10%'
        ELSE '07.>+10%' END
ORDER BY Bucket
'@
Q $sql2 | Format-Table -AutoSize

Write-Host "`n[3] 진입 후 즉시 청산 분석 — 1분 이내 종료" -ForegroundColor Yellow
$sql3 = @'
SELECT
    CASE
        WHEN holdingMinutes < 1   THEN '1.<1min'
        WHEN holdingMinutes < 3   THEN '2.1-3min'
        WHEN holdingMinutes < 10  THEN '3.3-10min'
        WHEN holdingMinutes < 30  THEN '4.10-30min'
        WHEN holdingMinutes < 120 THEN '5.30-120min'
        ELSE '6.>2h' END AS HoldBucket,
    COUNT(*) AS Cnt,
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY CASE
        WHEN holdingMinutes < 1   THEN '1.<1min'
        WHEN holdingMinutes < 3   THEN '2.1-3min'
        WHEN holdingMinutes < 10  THEN '3.3-10min'
        WHEN holdingMinutes < 30  THEN '4.10-30min'
        WHEN holdingMinutes < 120 THEN '5.30-120min'
        ELSE '6.>2h' END
ORDER BY HoldBucket
'@
Q $sql3 | Format-Table -AutoSize

Write-Host "`n[4] 진입 전략별 (Strategy/SignalSource) 7일 성과" -ForegroundColor Yellow
$sql4 = @'
SELECT Strategy, COUNT(*) AS Cnt,
    CAST(100.0 * SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(AVG(PnLPercent) AS DECIMAL(8,2)) AS AvgRoi
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY Strategy
ORDER BY PnL ASC
'@
Q $sql4 | Format-Table -AutoSize

Write-Host "`n[5] ExitReason - 7d" -ForegroundColor Yellow
$sql5 = @'
SELECT TOP 20 ExitReason, COUNT(*) AS Cnt,
    CAST(AVG(PnL) AS DECIMAL(18,4)) AS AvgPnL,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS PnL
FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-7,GETUTCDATE())
GROUP BY ExitReason ORDER BY Cnt DESC
'@
Q $sql5 | Format-Table -AutoSize -Wrap
