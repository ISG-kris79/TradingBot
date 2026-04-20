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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "`n========== v5.10.66 효과 검증 ==========" -ForegroundColor Yellow
Write-Host "(배포 시각: 2026-04-20 13:37 KST)" -ForegroundColor DarkGray

Write-Host "`n[1] 메이저 차단 작동 여부 (24h)" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS BlockedCount,
    COUNT(DISTINCT SUBSTRING(Message, CHARINDEX('[', Message, 5)+1, 12)) AS UniqueSymbols,
    MIN(Timestamp) AS FirstBlock,
    MAX(Timestamp) AS LastBlock
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(hour, -24, GETDATE())
  AND Message LIKE '%MAJOR_BLOCKED_v5_10_66%'
"@ | Format-Table -AutoSize

Write-Host "[2] 온라인 학습 진단 — window 진행 상황 (최근 50건)" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(hour, -24, GETDATE())
  AND (Message LIKE '%[OnlineLearning][진단]%'
       OR Message LIKE '%[OnlineLearning] 트리거 발화%'
       OR Message LIKE '%[OnlineLearning] 주기 콜백 스킵%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host "[3] 온라인 재학습 발생 여부 (DB 기록)" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Stage, Success, SampleCount, ROUND(Accuracy,3) AS Acc, ROUND(F1Score,3) AS F1,
    CONVERT(varchar(19), CompletedAtUtc, 120) AS CompletedUtc, LEFT(Detail, 60) AS Detail
FROM AiTrainingRuns WITH (NOLOCK)
WHERE CompletedAtUtc >= DATEADD(hour, -48, GETUTCDATE())
ORDER BY CompletedAtUtc DESC
"@ | Format-Table -AutoSize

Write-Host "[4] 일일 PnL 비교 (메이저 차단 효과)" -ForegroundColor Cyan
Q @"
SELECT
    CONVERT(date, EntryTime) AS Day,
    Category,
    COUNT(*) AS N,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
    ROUND(SUM(PnL), 2) AS TotalPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day, -3, GETDATE()) AND IsClosed = 1
GROUP BY CONVERT(date, EntryTime), Category
ORDER BY Day DESC, TotalPnL DESC
"@ | Format-Table -AutoSize

Write-Host "[5] 라벨 성공률 변화 (1.0% 기준 적용 효과)" -ForegroundColor Cyan
Q @"
SELECT
    CASE WHEN EntryTimeUtc >= DATEADD(hour, -24, GETUTCDATE()) THEN 'After (24h)' ELSE 'Before' END AS Period,
    COUNT(*) AS N,
    SUM(CASE WHEN IsSuccess = 1 THEN 1 ELSE 0 END) AS Success,
    CAST(SUM(CASE WHEN IsSuccess = 1 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS SuccessRate
FROM AiLabeledSamples WITH (NOLOCK)
WHERE LabelSource = 'mark_to_market_15m'
  AND EntryTimeUtc >= DATEADD(day, -7, GETUTCDATE())
GROUP BY CASE WHEN EntryTimeUtc >= DATEADD(hour, -24, GETUTCDATE()) THEN 'After (24h)' ELSE 'Before' END
"@ | Format-Table -AutoSize

Write-Host "`n========== 판정 기준 ==========" -ForegroundColor Yellow
Write-Host "✅ [1] BlockedCount > 0     → 메이저 차단 정상" -ForegroundColor Green
Write-Host "✅ [2] 진단 로그 있음       → 온라인 학습 작동" -ForegroundColor Green
Write-Host "✅ [3] Stage=Online_Retrain → 재학습 정상 (없으면 데이터 부족)" -ForegroundColor Green
Write-Host "✅ [4] MAJOR 0건 + Total↑   → 차단 효과 발생" -ForegroundColor Green
Write-Host "✅ [5] After SuccessRate↑   → 라벨 완화 효과 (목표 ~30%)" -ForegroundColor Green
Write-Host ""
