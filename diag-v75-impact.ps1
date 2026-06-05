function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $f = [Convert]::FromBase64String($enc); $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length); [Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length); $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key,$a.IV); $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 90
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== 0. v5.23.75 배포 시점 추정 (Bot_Log 첫 등장) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 MIN(EventTime) AS FirstSeen, MAX(EventTime) AS LastSeen, COUNT(*) AS N
FROM Bot_Log WITH (NOLOCK)
WHERE Reason LIKE '%5.23.75%' OR Reason LIKE '%BBW=%<2.0%' OR Reason LIKE '%5봉중 3%'
"@ | Format-Table -AutoSize

Write-Host "=== A. 일자별 손익 (최근 8일) — 변곡점 찾기 ===" -ForegroundColor Yellow
Q @"
SELECT CAST(EntryTime AS DATE) AS D,
  COUNT(*) AS N,
  CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR,
  ROUND(SUM(PnL),2) AS NetPnL,
  ROUND(AVG(PnL),2) AS AvgPnL
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=1 AND IsClosed=1 AND PnL <> 0
  AND EntryTime >= DATEADD(DAY,-8,GETDATE())
GROUP BY CAST(EntryTime AS DATE)
ORDER BY D
"@ | Format-Table -AutoSize

Write-Host "=== B. 카테고리별 — 배포 전(6/4 이전) vs 후 ===" -ForegroundColor Yellow
Q @"
SELECT
  CASE WHEN EntryTime < '2026-06-04' THEN '1_배포전' ELSE '2_배포후' END AS Period,
  ISNULL(Category, Strategy) AS Cat,
  COUNT(*) AS N,
  CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR,
  ROUND(SUM(PnL),2) AS NetPnL,
  ROUND(AVG(CASE WHEN PnL>0 THEN PnL END),2) AS AvgWin,
  ROUND(AVG(CASE WHEN PnL<=0 THEN PnL END),2) AS AvgLoss
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=1 AND IsClosed=1 AND PnL <> 0
  AND EntryTime >= DATEADD(DAY,-30,GETDATE())
GROUP BY CASE WHEN EntryTime < '2026-06-04' THEN '1_배포전' ELSE '2_배포후' END, ISNULL(Category, Strategy)
ORDER BY Cat, Period
"@ | Format-Table -AutoSize

Write-Host "=== C. BB_WALK/SQUEEZE 배포후 개별 트레이드 (실제 진입 확인) ===" -ForegroundColor Yellow
Q @"
SELECT EntryTime, Symbol, ISNULL(Category,Strategy) AS Cat, ROUND(PnL,2) AS PnL,
  ROUND(PnLPercent,1) AS RoePct, holdingMinutes AS HoldMin, LEFT(ExitReason,30) AS ExitR
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=1 AND IsClosed=1 AND PnL <> 0
  AND EntryTime >= '2026-06-04'
  AND (ISNULL(Category,Strategy) LIKE '%BB%' OR ISNULL(Category,Strategy) LIKE '%SQUEEZE%' OR ISNULL(Category,Strategy) LIKE '%WALK%')
ORDER BY EntryTime
"@ | Format-Table -AutoSize

Write-Host "=== D. 배포후 전체 합계 vs 배포전 30일 일평균 ===" -ForegroundColor Yellow
Q @"
SELECT '배포후(6/4~)' AS Period, COUNT(*) AS N,
  CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR,
  ROUND(SUM(PnL),2) AS NetPnL
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=1 AND IsClosed=1 AND PnL <> 0 AND EntryTime >= '2026-06-04'
UNION ALL
SELECT '배포전(5/5~6/3)', COUNT(*),
  CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)),
  ROUND(SUM(PnL),2)
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=1 AND IsClosed=1 AND PnL <> 0
  AND EntryTime >= DATEADD(DAY,-30,GETDATE()) AND EntryTime < '2026-06-04'
"@ | Format-Table -AutoSize
