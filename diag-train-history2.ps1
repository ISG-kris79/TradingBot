function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "==== Models folder ====" -ForegroundColor Cyan
$mdir = "$env:LOCALAPPDATA\TradingBot\Models"
Get-ChildItem $mdir -File | Sort-Object Name | Select-Object Name, @{N='KB';E={[int]($_.Length/1KB)}}, LastWriteTime | Format-Table -AutoSize

Write-Host ""
Write-Host "==== flag content ====" -ForegroundColor Cyan
$flag = "$mdir\initial_training_ready.flag"
if (Test-Path $flag) { Get-Content $flag | Write-Host } else { Write-Host "  no flag" }

Write-Host ""
Write-Host "==== FooterLogs since bot start (12:10) - AI training ====" -ForegroundColor Cyan
Q @"
SELECT TOP 80 Timestamp, LEFT(Message,260) AS Msg
FROM FooterLogs
WHERE Timestamp >= '2026-04-27 12:10:00'
  AND (Message LIKE '%AI 학습%' OR Message LIKE '%EntryTimingML%' OR Message LIKE '%초기학습%' OR Message LIKE '%TrainAndSave%' OR Message LIKE '%positive%' OR Message LIKE '%재학습 스킵%' OR Message LIKE '%variant%' OR Message LIKE '%Default%' OR Message LIKE '%Major%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== AI_TrainingRuns recent ====" -ForegroundColor Cyan
$x = Q "SELECT COUNT(*) AS C FROM sys.tables WHERE name='AI_TrainingRuns'"
if ($x.Rows[0].C -gt 0) {
    Q @"
SELECT TOP 30 RunStartUtc, ProjectName, Stage, Success, SampleCount, Accuracy, AUC, LEFT(Detail,160) AS Detail
FROM AI_TrainingRuns
WHERE RunStartUtc >= DATEADD(HOUR,-12,GETUTCDATE())
ORDER BY RunStartUtc DESC
"@ | Format-Table -AutoSize -Wrap
} else { Write-Host "  AI_TrainingRuns table missing" }
