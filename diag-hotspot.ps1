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

Write-Host "==== A. Last 2 min FooterLogs - msg type frequency ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30
  CASE
    WHEN Message LIKE '%[GATE-CHECK]%' THEN '01_GATE-CHECK'
    WHEN Message LIKE '%[GATE]%차단%'   THEN '02_GATE-BLOCK'
    WHEN Message LIKE '%[SIGNAL]%'    THEN '03_SIGNAL'
    WHEN Message LIKE '%[감시%'        THEN '04_WATCH'
    WHEN Message LIKE '%피보나치%'      THEN '05_FIB'
    WHEN Message LIKE '%bull0%'       THEN '06_BULL0'
    WHEN Message LIKE '%[AI%'         THEN '07_AI'
    WHEN Message LIKE '%[ML%'         THEN '08_ML'
    WHEN Message LIKE '%학습%'         THEN '09_TRAIN'
    WHEN Message LIKE '%KlineCache%'  THEN '10_KLINECACHE'
    WHEN Message LIKE '%Predict%'     THEN '11_PREDICT'
    WHEN Message LIKE '%WebSocket%'   THEN '12_WS'
    ELSE '99_OTHER'
  END AS MsgType,
  COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-2,GETDATE())
GROUP BY
  CASE
    WHEN Message LIKE '%[GATE-CHECK]%' THEN '01_GATE-CHECK'
    WHEN Message LIKE '%[GATE]%차단%'   THEN '02_GATE-BLOCK'
    WHEN Message LIKE '%[SIGNAL]%'    THEN '03_SIGNAL'
    WHEN Message LIKE '%[감시%'        THEN '04_WATCH'
    WHEN Message LIKE '%피보나치%'      THEN '05_FIB'
    WHEN Message LIKE '%bull0%'       THEN '06_BULL0'
    WHEN Message LIKE '%[AI%'         THEN '07_AI'
    WHEN Message LIKE '%[ML%'         THEN '08_ML'
    WHEN Message LIKE '%학습%'         THEN '09_TRAIN'
    WHEN Message LIKE '%KlineCache%'  THEN '10_KLINECACHE'
    WHEN Message LIKE '%Predict%'     THEN '11_PREDICT'
    WHEN Message LIKE '%WebSocket%'   THEN '12_WS'
    ELSE '99_OTHER'
  END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== B. Total log volume / sec (last 2 min) ====" -ForegroundColor Cyan
Q @"
SELECT
  COUNT(*) AS TotalRows,
  COUNT(*) / 120.0 AS RowsPerSec,
  MIN(Timestamp) AS Start_,
  MAX(Timestamp) AS End_
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-2,GETDATE())
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== C. Top 20 message prefixes (60 chars) last 2 min ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20 LEFT(Message,60) AS Prefix, COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-2,GETDATE())
GROUP BY LEFT(Message,60)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== D. Training in progress? ====" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message,200) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-5,GETDATE())
  AND (Message LIKE '%학습%' OR Message LIKE '%train%' OR Message LIKE '%다운로드%' OR Message LIKE '%히스토리%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap
