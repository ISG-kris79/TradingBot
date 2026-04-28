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

Write-Host "`n=== [A] 최근 7일 매매 결과 요약 ===" -ForegroundColor Cyan
Q @"
SELECT
    CONVERT(date, EntryTime) AS Day,
    COUNT(*) AS Trades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
    ROUND(SUM(PnL), 2) AS TotalPnL,
    ROUND(AVG(PnL), 2) AS AvgPnL,
    ROUND(AVG(AiScore), 4) AS AvgAiScore
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day, -7, GETDATE()) AND IsClosed = 1
GROUP BY CONVERT(date, EntryTime)
ORDER BY Day DESC
"@ | Format-Table -AutoSize

Write-Host "`n=== [B] AI 점수 분포 (최근 7일 청산건) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN AiScore = 0 THEN '0.00'
        WHEN AiScore < 0.50 THEN '<0.50'
        WHEN AiScore < 0.60 THEN '0.50-0.60'
        WHEN AiScore < 0.65 THEN '0.60-0.65'
        WHEN AiScore < 0.70 THEN '0.65-0.70'
        WHEN AiScore < 0.80 THEN '0.70-0.80'
        ELSE '>=0.80'
    END AS ScoreBin,
    COUNT(*) AS N,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
    ROUND(AVG(PnL), 2) AS AvgPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day, -7, GETDATE()) AND IsClosed = 1
GROUP BY
    CASE
        WHEN AiScore = 0 THEN '0.00'
        WHEN AiScore < 0.50 THEN '<0.50'
        WHEN AiScore < 0.60 THEN '0.50-0.60'
        WHEN AiScore < 0.65 THEN '0.60-0.65'
        WHEN AiScore < 0.70 THEN '0.65-0.70'
        WHEN AiScore < 0.80 THEN '0.70-0.80'
        ELSE '>=0.80'
    END
ORDER BY ScoreBin
"@ | Format-Table -AutoSize

Write-Host "`n=== [C] 카테고리별 성과 (Major/Pump/Spike) ===" -ForegroundColor Cyan
Q @"
SELECT
    ISNULL(Category, '(none)') AS Category,
    COUNT(*) AS N,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
    ROUND(AVG(PnL), 2) AS AvgPnL,
    ROUND(SUM(PnL), 2) AS TotalPnL,
    ROUND(AVG(AiScore), 4) AS AvgScore
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day, -7, GETDATE()) AND IsClosed = 1
GROUP BY Category
ORDER BY TotalPnL DESC
"@ | Format-Table -AutoSize

Write-Host "`n=== [D] AiTrainingRuns - 최근 학습 이력 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15
    UserId, ProjectName, RunId, Stage, Success, SampleCount, Epochs,
    ROUND(Accuracy, 4) AS Acc, ROUND(F1Score, 4) AS F1, ROUND(AUC, 4) AS AUC,
    CONVERT(varchar(19), CompletedAtUtc, 120) AS CompletedUtc, LEFT(Detail, 80) AS Detail
FROM AiTrainingRuns WITH (NOLOCK)
ORDER BY CompletedAtUtc DESC
"@ | Format-Table -AutoSize

Write-Host "`n=== [E] AiLabeledSamples - 라벨링 통계 ===" -ForegroundColor Cyan
Q @"
SELECT
    LabelSource,
    COUNT(*) AS N,
    SUM(CASE WHEN IsSuccess = 1 THEN 1 ELSE 0 END) AS Success,
    CAST(SUM(CASE WHEN IsSuccess = 1 THEN 1.0 ELSE 0 END) / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS SuccessRate,
    ROUND(AVG(ActualProfitPct), 2) AS AvgProfit,
    MIN(EntryTimeUtc) AS Oldest,
    MAX(EntryTimeUtc) AS Newest
FROM AiLabeledSamples WITH (NOLOCK)
GROUP BY LabelSource
ORDER BY N DESC
"@ | Format-Table -AutoSize

Write-Host "`n=== [F] AI Gate 차단/통과 로그 (최근 24h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20
    pattern,
    COUNT(*) AS N
FROM (
    SELECT
        CASE
            WHEN Message LIKE '%DUAL_BLOCK%' THEN 'DUAL_BLOCK (ML+TF 동시 미달)'
            WHEN Message LIKE '%FILTER_BLOCK%' THEN 'FILTER_BLOCK (리스크 필터)'
            WHEN Message LIKE '%DEADCAT_BLOCK%' THEN 'DEADCAT_BLOCK (피보나치)'
            WHEN Message LIKE '%Rule_Violation%' THEN 'Rule_Violation (엘리엇)'
            WHEN Message LIKE '%ML_DIAG%' THEN 'ML_DIAG (ML=0)'
            WHEN Message LIKE '%LORENTZIAN_WARN%' THEN 'LORENTZIAN_WARN'
            WHEN Message LIKE '%LORENTZIAN_OK%' THEN 'LORENTZIAN_OK'
            WHEN Message LIKE '%LORENTZIAN_WARMUP%' THEN 'LORENTZIAN_WARMUP'
            WHEN Message LIKE '%[BRAIN_SOFT_PASS]%' THEN 'BRAIN_SOFT_PASS (TF 미달, ML 통과)'
            WHEN Message LIKE '%[FILTER_SOFT_PASS]%' THEN 'FILTER_SOFT_PASS (ML 미달, TF 통과)'
            WHEN Message LIKE '%더블체크 승인%' THEN 'DoubleCheck_PASS'
            ELSE NULL
        END AS pattern
    FROM FooterLogs WITH (NOLOCK)
    WHERE Timestamp >= DATEADD(hour, -24, GETDATE())
) x
WHERE pattern IS NOT NULL
GROUP BY pattern
ORDER BY N DESC
"@ | Format-Table -AutoSize

Write-Host "`n=== [G] ML 신뢰도 0% 패턴 (최근 24h, 표본 10건) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(hour, -24, GETDATE())
  AND Message LIKE '%ML_DIAG%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host "`n=== [H] 최근 진입 거부 사유 분석 (최근 6h, 표본 30건) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(hour, -6, GETDATE())
  AND (Message LIKE '%DUAL_BLOCK%' OR Message LIKE '%FILTER_BLOCK%' OR Message LIKE '%Rule_Violation%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host "`n=== [I] EntryDecisions JSON 라벨링 정확도 (최근 100건 추정) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 1
    COUNT(*) AS TotalDecisions,
    SUM(CASE WHEN Message LIKE '%[OnlineLearning] 샘플 추가%' THEN 1 ELSE 0 END) AS LabeledFlushed,
    MAX(Timestamp) AS LastLabel
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(day, -7, GETDATE())
  AND (Message LIKE '%[OnlineLearning] 샘플%' OR Message LIKE '%AI 더블체크 승인%')
"@ | Format-Table -AutoSize
