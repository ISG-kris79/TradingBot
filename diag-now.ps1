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
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== 1. Bot version + uptime ====" -ForegroundColor Cyan
$p = Get-Process TradingBot -ErrorAction SilentlyContinue
if ($p) {
    $p | Select-Object Id, StartTime, @{N='RunMin';E={[int]((Get-Date) - $_.StartTime).TotalMinutes}}, @{N='WorkMB';E={[int]($_.WorkingSet64/1MB)}} | Format-Table -AutoSize
} else { Write-Host "  Bot NOT RUNNING" -ForegroundColor Red }
$verPath = "$env:LOCALAPPDATA\TradingBot\current\sq.version"
if (Test-Path $verPath) { Write-Host ("  Version file: " + (Get-Content $verPath -Raw)) }

Write-Host ""
Write-Host "==== 2. Bot_Log last 12h - block reasons (top 10) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Reason, COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR,-12,GETDATE())
GROUP BY Reason ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== 3. Bot_Log last 12h - Allow vs Block ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE WHEN Allowed=1 THEN 'ALLOW' ELSE 'BLOCK' END AS Result,
  COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR,-12,GETDATE())
GROUP BY Allowed
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== 4. ML_Conf distribution last 12h ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE
    WHEN ML_Conf >= 0.65 THEN '01_>=65%'
    WHEN ML_Conf >= 0.005 THEN '02_0.5%-65%'
    WHEN ML_Conf >= 0.001 THEN '03_0.1-0.5%'
    ELSE '04_<0.1%'
  END AS Bucket,
  COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR,-12,GETDATE())
GROUP BY
  CASE
    WHEN ML_Conf >= 0.65 THEN '01_>=65%'
    WHEN ML_Conf >= 0.005 THEN '02_0.5%-65%'
    WHEN ML_Conf >= 0.001 THEN '03_0.1-0.5%'
    ELSE '04_<0.1%'
  END
ORDER BY Bucket
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== 5. FooterLogs last 30 - any GATE/AI/MAJOR ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND (Message LIKE '%GATE%' OR Message LIKE '%AI%' OR Message LIKE '%MAJOR%' OR Message LIKE '%감시%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap
