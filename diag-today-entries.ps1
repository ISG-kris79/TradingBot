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

Write-Host "==== A. TradeHistory today entries ====" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS TotalToday,
       SUM(CASE WHEN EntryTime >= CAST(GETDATE() AS DATE) THEN 1 ELSE 0 END) AS NewEntriesToday
FROM TradeHistory
WHERE EntryTime >= CAST(GETDATE() AS DATE) OR ExitTime >= CAST(GETDATE() AS DATE)
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== B. Bot_Log today block reasons (top 20) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Reason, COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= CAST(GETDATE() AS DATE) AND Allowed=0
GROUP BY Reason ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== C. Bot_Log today Allow vs Block ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE WHEN Allowed=1 THEN 'ALLOW' ELSE 'BLOCK' END AS Result,
  COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= CAST(GETDATE() AS DATE)
GROUP BY Allowed
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== D. Last allowed entry attempt (any time) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 5 EventTime, Symbol, ML_Conf, TF_Conf, Reason
FROM Bot_Log
WHERE Allowed=1
ORDER BY EventTime DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== E. ML_Conf today distribution (decile) ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE
    WHEN ML_Conf >= 0.65 THEN '01_>=65%'
    WHEN ML_Conf >= 0.50 THEN '02_50-65%'
    WHEN ML_Conf >= 0.30 THEN '03_30-50%'
    WHEN ML_Conf >= 0.10 THEN '04_10-30%'
    WHEN ML_Conf >= 0.01 THEN '05_1-10%'
    ELSE '06_<1%'
  END AS Bucket,
  COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= CAST(GETDATE() AS DATE)
GROUP BY
  CASE
    WHEN ML_Conf >= 0.65 THEN '01_>=65%'
    WHEN ML_Conf >= 0.50 THEN '02_50-65%'
    WHEN ML_Conf >= 0.30 THEN '03_30-50%'
    WHEN ML_Conf >= 0.10 THEN '04_10-30%'
    WHEN ML_Conf >= 0.01 THEN '05_1-10%'
    ELSE '06_<1%'
  END
ORDER BY Bucket
"@ | Format-Table -AutoSize
