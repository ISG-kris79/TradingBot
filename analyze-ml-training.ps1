$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection)
    $cn.Open()
    $cm = $cn.CreateCommand()
    $cm.CommandText = $sql
    $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet
    [void]$ap.Fill($ds)
    $cn.Close()
    return $ds.Tables[0]
}

Write-Host "==== AiTrainingRuns 최근 학습 이력 ===="
$q1 = @'
SELECT TOP 5 COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='AiTrainingRuns'
'@
Q $q1 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== 모델 파일 목록 + 수정 시각 ===="
$modelDir = Join-Path $env:LOCALAPPDATA "TradingBot\Models"
if (Test-Path $modelDir) {
    Get-ChildItem $modelDir -Filter "*.zip" | Sort-Object LastWriteTime -Descending | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Host "$modelDir not found"
}

Write-Host ""
Write-Host "==== TrainingData 파일 개수 ===="
$tdDir = "e:\PROJECT\CoinFF\TradingBot\TradingBot\TrainingData\EntryDecisions"
if (Test-Path $tdDir) {
    $files = Get-ChildItem $tdDir -Filter "*.jsonl" -ErrorAction SilentlyContinue
    Write-Host "Files: $($files.Count)"
    if ($files.Count -gt 0) {
        $recent = $files | Sort-Object LastWriteTime -Descending | Select-Object -First 5
        foreach ($f in $recent) {
            $lines = (Get-Content $f.FullName -ErrorAction SilentlyContinue).Count
            Write-Host ("  {0}  lines={1}  modified={2}" -f $f.Name, $lines, $f.LastWriteTime)
        }
    }
}

Write-Host ""
Write-Host "==== 최근 24시간 진입 vs 차단 비율 ===="
$q2 = @'
SELECT
  CAST(Allowed AS INT) AS Allowed,
  COUNT(*) AS Cnt
FROM Bot_Log
WHERE UserId=1 AND EventTime >= DATEADD(HOUR,-24,GETUTCDATE())
GROUP BY Allowed
'@
Q $q2 | Format-Table -AutoSize

Write-Host ""
Write-Host "==== ML_Conf 분포 히스토그램 (최근 1000건) ===="
$q3 = @'
SELECT
  CASE
    WHEN ML_Conf < 0.1 THEN 'A: 0-10%'
    WHEN ML_Conf < 0.2 THEN 'B: 10-20%'
    WHEN ML_Conf < 0.3 THEN 'C: 20-30%'
    WHEN ML_Conf < 0.4 THEN 'D: 30-40%'
    WHEN ML_Conf < 0.5 THEN 'E: 40-50%'
    WHEN ML_Conf < 0.6 THEN 'F: 50-60%'
    WHEN ML_Conf < 0.7 THEN 'G: 60-70%'
    WHEN ML_Conf < 0.8 THEN 'H: 70-80%'
    WHEN ML_Conf < 0.9 THEN 'I: 80-90%'
    ELSE 'J: 90-100%'
  END AS Bucket,
  COUNT(*) AS Cnt
FROM (SELECT TOP 1000 ML_Conf FROM Bot_Log WHERE UserId=1 ORDER BY EventTime DESC) t
GROUP BY CASE
    WHEN ML_Conf < 0.1 THEN 'A: 0-10%'
    WHEN ML_Conf < 0.2 THEN 'B: 10-20%'
    WHEN ML_Conf < 0.3 THEN 'C: 20-30%'
    WHEN ML_Conf < 0.4 THEN 'D: 30-40%'
    WHEN ML_Conf < 0.5 THEN 'E: 40-50%'
    WHEN ML_Conf < 0.6 THEN 'F: 50-60%'
    WHEN ML_Conf < 0.7 THEN 'G: 60-70%'
    WHEN ML_Conf < 0.8 THEN 'H: 70-80%'
    WHEN ML_Conf < 0.9 THEN 'I: 80-90%'
    ELSE 'J: 90-100%'
  END
ORDER BY Bucket
'@
Q $q3 | Format-Table -AutoSize
