function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
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
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] FooterLogs columns ===" -ForegroundColor Cyan
Q "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='FooterLogs'" | Format-Table -AutoSize

Write-Host "=== [2] Latest FooterLogs record time ===" -ForegroundColor Cyan
Q "SELECT TOP 3 Id, Timestamp, LEFT(Message, 150) AS Msg FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [3] ML_Zero_Confidence in last 1h ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Id, Timestamp, LEFT(Message, 150) AS Msg FROM FooterLogs WHERE Message LIKE '%Zero_Confidence%' AND Timestamp >= DATEADD(HOUR, -1, GETDATE()) ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [4] VOLUME BLOCK in last 1h ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Id, Timestamp, LEFT(Message, 150) AS Msg FROM FooterLogs WHERE Message LIKE '%VOLUME%BLOCK%' AND Timestamp >= DATEADD(HOUR, -1, GETDATE()) ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [5] Drought / ML_Zero_Confidence last seen ===" -ForegroundColor Cyan
Q "SELECT TOP 1 Id, Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Message LIKE '%Zero_Confidence%' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [6] Version related logs ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Id, Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Message LIKE '%v5.0%' OR Message LIKE '%v4.9%' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [7] v5.0.0 Forecaster log ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Id, Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Message LIKE '%Forecaster%' OR Message LIKE '%SCHED%' OR Message LIKE '%v5.0%' ORDER BY Id DESC" | Format-Table -AutoSize
