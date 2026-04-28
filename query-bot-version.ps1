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

Write-Host "=== [1] Oldest vs newest VOLUME_BLOCK log ===" -ForegroundColor Cyan
Q "SELECT MIN(Timestamp) AS FirstSeen, MAX(Timestamp) AS LastSeen, COUNT(*) AS TotalCount FROM FooterLogs WHERE Message LIKE '%VOLUME%BLOCK%'" | Format-Table -AutoSize

Write-Host "=== [2] Oldest vs newest v5.0 Forecaster log ===" -ForegroundColor Cyan
Q "SELECT MIN(Timestamp) AS FirstSeen, MAX(Timestamp) AS LastSeen, COUNT(*) AS TotalCount FROM FooterLogs WHERE Message LIKE '%v5.0%'" | Format-Table -AutoSize

Write-Host "=== [3] v5.0 AND VOLUME_BLOCK in SAME 10-minute window (proof) ===" -ForegroundColor Cyan
Q @"
WITH V5Times AS (
    SELECT Timestamp FROM FooterLogs WHERE Message LIKE '%v5.0%MajorForecaster%' ORDER BY Timestamp DESC
),
VolumeBlock AS (
    SELECT Timestamp, Message FROM FooterLogs WHERE Message LIKE '%VOLUME%BLOCK%' ORDER BY Timestamp DESC
)
SELECT TOP 10 vb.Timestamp AS BlockTime, LEFT(vb.Message, 100) AS BlockMsg
FROM VolumeBlock vb
WHERE EXISTS (
    SELECT 1 FROM V5Times v5
    WHERE v5.Timestamp BETWEEN DATEADD(MINUTE, -5, vb.Timestamp) AND DATEADD(MINUTE, 5, vb.Timestamp)
)
ORDER BY vb.Timestamp DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] Forecaster 로그 최근 5건 - 같은 봇인지 확인 ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Timestamp, LEFT(Message, 150) AS Msg FROM FooterLogs WHERE Message LIKE '%Forecaster%' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [5] PumpML-Normal 학습 로그 (v4.9.8 태그 확인) ===" -ForegroundColor Cyan
Q "SELECT TOP 5 Timestamp, LEFT(Message, 200) AS Msg FROM FooterLogs WHERE Message LIKE '%PumpML%[v4.9.8]%' ORDER BY Id DESC" | Format-Table -AutoSize
