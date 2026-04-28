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

Write-Host "==== [1] 일별 PnL 30일 ====" -ForegroundColor Cyan
$dailyPnl = Q "SELECT CONVERT(date, EntryTime) AS Day, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ROUND(SUM(PnL),2) AS TotalPnL FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-30,GETDATE()) AND IsClosed=1 GROUP BY CONVERT(date, EntryTime) ORDER BY Day"

Write-Host "==== [2] AiScore 분포 vs 승률 ====" -ForegroundColor Cyan
$scoreDist = Q @"
SELECT
    CASE
        WHEN AiScore = 0 THEN '0.00'
        WHEN AiScore < 0.30 THEN '<0.30'
        WHEN AiScore < 0.50 THEN '0.30-0.50'
        WHEN AiScore < 0.65 THEN '0.50-0.65'
        WHEN AiScore < 0.80 THEN '0.65-0.80'
        ELSE '>=0.80'
    END AS ScoreBin,
    COUNT(*) AS N,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    ROUND(AVG(PnL),2) AS AvgPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day,-30,GETDATE()) AND IsClosed = 1
GROUP BY
    CASE
        WHEN AiScore = 0 THEN '0.00'
        WHEN AiScore < 0.30 THEN '<0.30'
        WHEN AiScore < 0.50 THEN '0.30-0.50'
        WHEN AiScore < 0.65 THEN '0.50-0.65'
        WHEN AiScore < 0.80 THEN '0.65-0.80'
        ELSE '>=0.80'
    END
ORDER BY ScoreBin
"@

Write-Host "==== [3] 카테고리별 30일 ====" -ForegroundColor Cyan
$byCategory = Q "SELECT ISNULL(Category,'(none)') AS Category, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ROUND(SUM(PnL),2) AS TotalPnL, ROUND(AVG(PnL),2) AS AvgPnL FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-30,GETDATE()) AND IsClosed=1 GROUP BY Category ORDER BY TotalPnL DESC"

Write-Host "==== [4] 보유 시간별 (5-60min) ====" -ForegroundColor Cyan
$holdTime = Q @"
SELECT
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 2 THEN '0-2min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '2-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '5-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '15-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '30-60min'
        ELSE 'over_60min'
    END AS HoldRange,
    COUNT(*) AS N,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins,
    ROUND(SUM(PnL),2) AS TotalPnL,
    ROUND(AVG(PnL),2) AS AvgPnL
FROM TradeHistory WITH (NOLOCK)
WHERE EntryTime >= DATEADD(day,-30,GETDATE()) AND IsClosed = 1 AND ExitTime IS NOT NULL
GROUP BY
    CASE
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 2 THEN '0-2min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 5 THEN '2-5min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 15 THEN '5-15min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 30 THEN '15-30min'
        WHEN DATEDIFF(MINUTE, EntryTime, ExitTime) <= 60 THEN '30-60min'
        ELSE 'over_60min'
    END
ORDER BY HoldRange
"@

Write-Host "==== [5] 청산 사유 TOP 10 ====" -ForegroundColor Cyan
$exitReasons = Q "SELECT TOP 10 LEFT(ExitReason,40) AS Reason, COUNT(*) AS N, ROUND(SUM(PnL),2) AS TotalPnL, ROUND(AVG(PnLPercent),2) AS AvgPnLPct FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-30,GETDATE()) AND IsClosed=1 GROUP BY LEFT(ExitReason,40) ORDER BY N DESC"

Write-Host "==== [6] AI 학습 이력 (30일) ====" -ForegroundColor Cyan
$trainingRuns = Q "SELECT TOP 30 Stage, Success, SampleCount, ROUND(Accuracy,3) AS Acc, ROUND(F1Score,3) AS F1, CONVERT(varchar(19), CompletedAtUtc, 120) AS CompletedUtc FROM AiTrainingRuns WITH (NOLOCK) WHERE CompletedAtUtc>=DATEADD(day,-30,GETUTCDATE()) ORDER BY CompletedAtUtc DESC"

Write-Host "==== [7] 시간대별 진입 (KST) ====" -ForegroundColor Cyan
$hourDist = Q "SELECT DATEPART(hour, EntryTime) AS Hour, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ROUND(SUM(PnL),2) AS TotalPnL FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(day,-30,GETDATE()) AND IsClosed=1 GROUP BY DATEPART(hour, EntryTime) ORDER BY Hour"

Write-Host "==== [8] 라벨 성공률 추이 (일별) ====" -ForegroundColor Cyan
$labelDaily = Q "SELECT CONVERT(date, EntryTimeUtc) AS Day, COUNT(*) AS N, SUM(CASE WHEN IsSuccess=1 THEN 1 ELSE 0 END) AS Success FROM AiLabeledSamples WITH (NOLOCK) WHERE LabelSource='mark_to_market_15m' AND EntryTimeUtc>=DATEADD(day,-30,GETUTCDATE()) GROUP BY CONVERT(date, EntryTimeUtc) ORDER BY Day"

$data = [ordered]@{
    generatedAt = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    dailyPnl = $dailyPnl
    scoreDist = $scoreDist
    byCategory = $byCategory
    holdTime = $holdTime
    exitReasons = $exitReasons
    trainingRuns = $trainingRuns
    hourDist = $hourDist
    labelDaily = $labelDaily
}
$jsonPath = Join-Path $PSScriptRoot "ai-report-data.json"
$data | ConvertTo-Json -Depth 5 -Compress | Out-File $jsonPath -Encoding UTF8

# Summary metrics
$total30dPnl = ($dailyPnl | Measure-Object -Property TotalPnL -Sum).Sum
$total30dTrades = ($dailyPnl | Measure-Object -Property N -Sum).Sum
$total30dWins = ($dailyPnl | Measure-Object -Property Wins -Sum).Sum
$winRate = if ($total30dTrades -gt 0) { [math]::Round($total30dWins / $total30dTrades, 3) } else { 0 }

$summary = [ordered]@{
    total30dPnl = $total30dPnl
    total30dTrades = $total30dTrades
    total30dWins = $total30dWins
    winRate30d = $winRate
}
$summaryPath = Join-Path $PSScriptRoot "ai-report-summary.json"
$summary | ConvertTo-Json | Out-File $summaryPath -Encoding UTF8

Write-Host ""
Write-Host "30d Summary: PnL=`$$total30dPnl, Trades=$total30dTrades, Wins=$total30dWins, WinRate=$winRate"
Write-Host "Data saved: $jsonPath"
Write-Host ""
Write-Host "Generating HTML report..." -ForegroundColor Yellow
