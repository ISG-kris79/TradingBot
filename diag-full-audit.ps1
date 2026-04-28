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
function Exec-Query {
    param([string]$sql, [int]$timeout = 30)
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = $timeout
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

Write-Host ""
Write-Host "=============================================" -ForegroundColor Yellow
Write-Host " FULL AUDIT - AI 학습/추론/예측 전면 재검증" -ForegroundColor Yellow
Write-Host "=============================================" -ForegroundColor Yellow

Write-Host "`n[1] 라벨링 파이프라인 효과 (v5.10.72 배포: 2026-04-21 14:09 KST)" -ForegroundColor Cyan
$q1 = @'
SELECT CASE WHEN EntryTimeUtc>='2026-04-21 05:09' THEN 'After_v5.10.72'
           WHEN EntryTimeUtc>='2026-04-20 04:37' THEN 'Between'
           ELSE 'Before_v5.10.66' END AS Period,
       COUNT(*) AS N,
       SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END) AS Success,
       CAST(SUM(CASE WHEN IsSuccess=1 THEN 1.0 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS Rate
FROM AiLabeledSamples WITH (NOLOCK)
WHERE LabelSource='mark_to_market_15m'
  AND EntryTimeUtc>=DATEADD(day,-7,GETUTCDATE())
GROUP BY CASE WHEN EntryTimeUtc>='2026-04-21 05:09' THEN 'After_v5.10.72'
              WHEN EntryTimeUtc>='2026-04-20 04:37' THEN 'Between'
              ELSE 'Before_v5.10.66' END
'@
Exec-Query -sql $q1 | Format-Table -AutoSize

Write-Host "`n[2] 온라인 재학습 이력 (3일)" -ForegroundColor Cyan
$q2 = @'
SELECT TOP 20 Stage, Success, SampleCount,
       ROUND(Accuracy,3) AS Acc, ROUND(F1Score,3) AS F1,
       CONVERT(varchar(19), CompletedAtUtc, 120) AS Utc,
       LEFT(Detail,60) AS Detail
FROM AiTrainingRuns WITH (NOLOCK)
WHERE CompletedAtUtc>=DATEADD(day,-3,GETUTCDATE())
ORDER BY CompletedAtUtc DESC
'@
Exec-Query -sql $q2 | Format-Table -AutoSize

Write-Host "`n[3] 진입 경로별 성과 (30일)" -ForegroundColor Cyan
$q3 = @'
SELECT LEFT(Strategy,30) AS EntryPath, COUNT(*) AS N,
       SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
       CAST(SUM(CASE WHEN PnL>0 THEN 1.0 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate,
       ROUND(SUM(PnL),2) AS TotalPnL, ROUND(AVG(PnL),2) AS AvgPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime>=DATEADD(day,-30,GETDATE()) AND IsClosed=1
GROUP BY LEFT(Strategy,30)
HAVING COUNT(*) >= 5
ORDER BY TotalPnL ASC
'@
Exec-Query -sql $q3 | Format-Table -AutoSize

Write-Host "`n[4] 현재 활성 포지션 상태" -ForegroundColor Cyan
$q4 = @'
SELECT th.Symbol, th.Side, ROUND(th.EntryPrice,6) AS EntryPx, th.Quantity,
       ROUND(th.AiScore,2) AS AiScore,
       ROUND(ps.HighestROE,2) AS HighROE,
       ps.IsBreakEvenTriggered AS BeTrig,
       th.EntryTime,
       DATEDIFF(MINUTE,th.EntryTime,GETDATE()) AS HoldMin
FROM TradeHistory th WITH (NOLOCK)
LEFT JOIN PositionState ps WITH (NOLOCK)
  ON ps.Symbol=th.Symbol AND ps.UserId=th.UserId
WHERE th.IsClosed=0
ORDER BY th.EntryTime DESC
'@
Exec-Query -sql $q4 | Format-Table -AutoSize

Write-Host "`n[5] 수동/외부 청산 지연 패턴 (24h)" -ForegroundColor Cyan
$q5 = @'
SELECT TOP 15 Symbol, EntryTime, ExitTime,
       DATEDIFF(SECOND,EntryTime,ExitTime) AS HoldSec,
       ROUND(PnLPercent,2) AS PnLPct,
       LEFT(ExitReason,50) AS ExitReason
FROM TradeHistory WITH (NOLOCK)
WHERE ExitTime>=DATEADD(hour,-24,GETDATE())
  AND (ExitReason LIKE N'%수동%' OR ExitReason LIKE '%EXTERNAL%')
ORDER BY ExitTime DESC
'@
Exec-Query -sql $q5 | Format-Table -AutoSize

$maxIdRow = Exec-Query -sql 'SELECT TOP 1 Id FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC'
$maxId = $maxIdRow[0].Id
$lookbackId = $maxId - 100000

Write-Host "`n[6] AI Gate 통계 (~24h)" -ForegroundColor Cyan
$q6Tmpl = @'
SELECT CASE
         WHEN Message LIKE '%[ENTRY][AI_GATE][BLOCK]%' THEN 'AI_BLOCK'
         WHEN Message LIKE N'%AI 더블체크 승인%' THEN 'AI_PASS'
         WHEN Message LIKE '%[MAJOR_BLOCKED_v5_10_66]%' THEN 'MAJOR_BLOCK'
         WHEN Message LIKE '%[DUAL_BLOCK]%' THEN 'DUAL_BLOCK'
         WHEN Message LIKE '%[FILTER_BLOCK]%' THEN 'FILTER_BLOCK'
         ELSE 'OTHER' END AS GateResult,
       COUNT(*) AS N
FROM FooterLogs WITH (NOLOCK)
WHERE Id >= {0}
  AND (Message LIKE '%AI_GATE%' OR Message LIKE N'%AI 더블체크%' OR Message LIKE '%MAJOR_BLOCKED_v5_10_66%' OR Message LIKE '%DUAL_BLOCK%' OR Message LIKE '%FILTER_BLOCK%')
GROUP BY CASE
         WHEN Message LIKE '%[ENTRY][AI_GATE][BLOCK]%' THEN 'AI_BLOCK'
         WHEN Message LIKE N'%AI 더블체크 승인%' THEN 'AI_PASS'
         WHEN Message LIKE '%[MAJOR_BLOCKED_v5_10_66]%' THEN 'MAJOR_BLOCK'
         WHEN Message LIKE '%[DUAL_BLOCK]%' THEN 'DUAL_BLOCK'
         WHEN Message LIKE '%[FILTER_BLOCK]%' THEN 'FILTER_BLOCK'
         ELSE 'OTHER' END
ORDER BY N DESC
'@
$q6 = [string]::Format($q6Tmpl, $lookbackId)
Exec-Query -sql $q6 | Format-Table -AutoSize

Write-Host "`n[7] 진입 경로 발화 (~6h)" -ForegroundColor Cyan
$q7Tmpl = @'
SELECT CASE
         WHEN Message LIKE '%MEGA_PUMP%' THEN 'MEGA_PUMP'
         WHEN Message LIKE '%M1_FAST_PUMP%' THEN 'M1_FAST_PUMP'
         WHEN Message LIKE '%TOP_SCORE_ENTRY%' THEN 'TOP_SCORE_ENTRY'
         WHEN Message LIKE '%AI_ENTRY%' THEN 'AI_ENTRY'
         WHEN Message LIKE '%FALLBACK_ENTRY%' THEN 'FALLBACK_ENTRY'
         WHEN Message LIKE '%SPIKE_FAST%' THEN 'SPIKE_FAST'
         ELSE 'OTHER' END AS EntryPath,
       COUNT(*) AS N
FROM FooterLogs WITH (NOLOCK)
WHERE Id >= {0}
  AND (Message LIKE '%MEGA_PUMP%' OR Message LIKE '%M1_FAST_PUMP%' OR Message LIKE '%TOP_SCORE_ENTRY%' OR Message LIKE '%AI_ENTRY%' OR Message LIKE '%FALLBACK_ENTRY%' OR Message LIKE '%SPIKE_FAST%')
GROUP BY CASE
         WHEN Message LIKE '%MEGA_PUMP%' THEN 'MEGA_PUMP'
         WHEN Message LIKE '%M1_FAST_PUMP%' THEN 'M1_FAST_PUMP'
         WHEN Message LIKE '%TOP_SCORE_ENTRY%' THEN 'TOP_SCORE_ENTRY'
         WHEN Message LIKE '%AI_ENTRY%' THEN 'AI_ENTRY'
         WHEN Message LIKE '%FALLBACK_ENTRY%' THEN 'FALLBACK_ENTRY'
         WHEN Message LIKE '%SPIKE_FAST%' THEN 'SPIKE_FAST'
         ELSE 'OTHER' END
ORDER BY N DESC
'@
$q7 = [string]::Format($q7Tmpl, $lookbackId)
Exec-Query -sql $q7 | Format-Table -AutoSize

Write-Host "`n[8] AiLabeledSamples 전체 통계" -ForegroundColor Cyan
$q8 = @'
SELECT COUNT(*) AS Total,
       SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END) AS Success,
       SUM(CASE WHEN IsSuccess=0 THEN 1 ELSE 0 END) AS Fail,
       ROUND(AVG(ActualProfitPct),2) AS AvgPct,
       MIN(EntryTimeUtc) AS Oldest,
       MAX(EntryTimeUtc) AS Newest
FROM AiLabeledSamples WITH (NOLOCK)
'@
Exec-Query -sql $q8 | Format-Table -AutoSize

Write-Host "`n[9] ML 모델 파일 정보" -ForegroundColor Cyan
$modelPaths = @("$PSScriptRoot\Models\EntryTimingModel.zip", "$PSScriptRoot\bin\Debug\net9.0-windows\Models\EntryTimingModel.zip", "$PSScriptRoot\bin\Release\net9.0-windows\win-x64\Models\EntryTimingModel.zip")
foreach ($p in $modelPaths) {
    if (Test-Path $p) {
        $info = Get-Item $p
        Write-Host ("  {0} ({1} KB, {2})" -f $p, [math]::Round($info.Length/1KB,1), $info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")) -ForegroundColor Green
    }
}

Write-Host "`n[10] EntryDecisions JSON 최근 5개" -ForegroundColor Cyan
$entryDecPath = "$PSScriptRoot\TrainingData\EntryDecisions"
if (Test-Path $entryDecPath) {
    $allFiles = Get-ChildItem -Path $entryDecPath -Filter "EntryDecisions_*.json" -ErrorAction SilentlyContinue
    Write-Host ("  총 파일 수: {0}" -f $allFiles.Count)
    $files = $allFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 5
    foreach ($f in $files) {
        Write-Host ("  {0,-40} {1,10:N1} KB  {2}" -f $f.Name, ($f.Length/1KB), $f.LastWriteTime.ToString("MM-dd HH:mm:ss"))
    }
}

Write-Host ""
Write-Host "=== FULL AUDIT 완료 ===" -ForegroundColor Yellow
