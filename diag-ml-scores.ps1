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

Write-Host "==== A. Bot_Log ML_Conf / TF_Conf distribution (last 30 min) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 EventTime, Symbol, ML_Conf, TF_Conf, Allowed, LEFT(Reason,100) AS Reason
FROM Bot_Log
WHERE EventTime >= DATEADD(MINUTE,-30,GETDATE())
ORDER BY EventTime DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== B. ML_Conf unique values (last 60 min) ====" -ForegroundColor Cyan
Q @"
SELECT ML_Conf, COUNT(*) AS Cnt
FROM Bot_Log
WHERE EventTime >= DATEADD(MINUTE,-60,GETDATE())
GROUP BY ML_Conf
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "==== C. ML.NET PREDICT log (FooterLogs, last 30 min) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,260) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND Message LIKE '%ML.NET%PREDICT%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap
