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

Write-Host "[A] AiTrainingRuns last 12h - Pump/Spike variant 학습 확인"
$a = "SELECT TOP 30 ProjectName, Stage, Success, SampleCount, Accuracy, F1Score, AUC, CompletedAtUtc FROM AiTrainingRuns WHERE CompletedAtUtc >= DATEADD(HOUR,-12,GETUTCDATE()) ORDER BY CompletedAtUtc DESC"
Q $a | Format-Table -AutoSize

Write-Host "[B] Variant 별 학습 통계"
$b = "SELECT Stage, COUNT(*) AS TrainRuns, MAX(SampleCount) AS LastSamples, MAX(CompletedAtUtc) AS LastTrain, AVG(Accuracy) AS AvgAcc FROM AiTrainingRuns WHERE CompletedAtUtc >= DATEADD(HOUR,-24,GETUTCDATE()) GROUP BY Stage"
Q $b | Format-Table -AutoSize

Write-Host "[C] 최근 1시간 Bot_Log ML/TF score distribution"
$c = "SELECT CASE WHEN ML_Conf >= 0.80 THEN '01.GE80pct' WHEN ML_Conf >= 0.50 THEN '02.50_80pct' WHEN ML_Conf >= 0.20 THEN '03.20_50pct' WHEN ML_Conf >= 0.05 THEN '04.5_20pct' WHEN ML_Conf > 0 THEN '05.LT5pct' ELSE '06.zero' END AS MLBucket, COUNT(*) AS Cnt, SUM(CASE WHEN Allowed=1 THEN 1 ELSE 0 END) AS Allowed FROM Bot_Log WHERE EventTime >= DATEADD(HOUR,-1,GETUTCDATE()) AND UserId=1 GROUP BY CASE WHEN ML_Conf >= 0.80 THEN '01.GE80pct' WHEN ML_Conf >= 0.50 THEN '02.50_80pct' WHEN ML_Conf >= 0.20 THEN '03.20_50pct' WHEN ML_Conf >= 0.05 THEN '04.5_20pct' WHEN ML_Conf > 0 THEN '05.LT5pct' ELSE '06.zero' END ORDER BY MLBucket"
Q $c | Format-Table -AutoSize

Write-Host "[D] 최근 1시간 진입 (Allowed=True) Symbol + Reason"
$d = "SELECT TOP 30 EventTime, Symbol, ML_Conf, TF_Conf, TrendScore, LEFT(Reason,90) AS Reason FROM Bot_Log WHERE EventTime >= DATEADD(HOUR,-1,GETUTCDATE()) AND UserId=1 AND Allowed=1 ORDER BY EventTime DESC"
Q $d | Format-Table -AutoSize -Wrap

Write-Host "[E] 최근 1시간 TradeHistory 결과 vs AiScore (역상관 해소?)"
$e = "SELECT CASE WHEN AiScore >= 80 THEN '01.GE80' WHEN AiScore >= 60 THEN '02.60_80' WHEN AiScore >= 40 THEN '03.40_60' WHEN AiScore >= 20 THEN '04.20_40' ELSE '05.LT20' END AS ScoreBucket, COUNT(*) AS Trades, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,1)) AS WinPct, CAST(AVG(PnLPercent) AS DECIMAL(8,2)) AS AvgRoi FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-3,GETUTCDATE()) AND AiScore IS NOT NULL AND AiScore > 0 GROUP BY CASE WHEN AiScore >= 80 THEN '01.GE80' WHEN AiScore >= 60 THEN '02.60_80' WHEN AiScore >= 40 THEN '03.40_60' WHEN AiScore >= 20 THEN '04.20_40' ELSE '05.LT20' END ORDER BY ScoreBucket"
Q $e | Format-Table -AutoSize

Write-Host "[F] 최근 1시간 진입 후 결과 (CHASING_BLOCK / LIMIT 효과 확인)"
$f = "SELECT TOP 30 Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-2,GETUTCDATE()) AND (Message LIKE '%CHASING_BLOCK%' OR Message LIKE '%LIMIT_ENTRY%' OR Message LIKE '%LIMIT_TIMEOUT%' OR Message LIKE '%LIMIT_FALLBACK%') ORDER BY Timestamp DESC"
Q $f | Format-Table -AutoSize -Wrap
