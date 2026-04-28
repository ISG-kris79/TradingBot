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

Write-Host "==== 1. Bot_Log: last 3h Allowed/Blocked count ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE WHEN Allowed=1 THEN 'ALLOW' ELSE 'BLOCK' END AS Result,
  COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR, -3, GETDATE())
GROUP BY Allowed
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== 2. Block reasons (last 3h) top 20 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Reason, COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR, -3, GETDATE()) AND Allowed=0
GROUP BY Reason ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== 3. ALLOWED entries last 3h ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 EventTime, Symbol, CoinType, Reason, ML_Conf, TF_Conf
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR, -3, GETDATE()) AND Allowed=1
ORDER BY EventTime DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== 4. Symbols evaluated last 3h (top 20) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Symbol, COUNT(*) AS Evaluations,
  SUM(CASE WHEN Allowed=1 THEN 1 ELSE 0 END) AS Allows
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR, -3, GETDATE())
GROUP BY Symbol ORDER BY Evaluations DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== 5. ML_Conf=1E-15 sentinel (model-missing signature) count ====" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS ModelMissingHits
FROM Bot_Log
WHERE EventTime >= DATEADD(HOUR, -3, GETDATE())
  AND (ML_Conf < 1E-9 OR Reason LIKE '%MODEL_ZIP_MISSING%')
"@ | Format-Table -AutoSize
