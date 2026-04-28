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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 15
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== A. 봇 시작 후 첫 100건 로그 (v5.22.16 시작 흐름) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 100 Timestamp, LEFT(Message,180) AS Msg
FROM FooterLogs
WHERE Timestamp >= '2026-04-29 00:02:18'
  AND Timestamp <= '2026-04-29 00:03:30'
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== B. Watchdog 트리거 확인 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message,200) AS Msg
FROM FooterLogs
WHERE Timestamp >= '2026-04-29 00:02:18'
  AND (Message LIKE '%WS-WATCHDOG%' OR Message LIKE '%복구%' OR Message LIKE '%Watchdog%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== C. WebSocket 끊김 vs 복구 카운트 (5분) ====" -ForegroundColor Cyan
Q @"
SELECT
  SUM(CASE WHEN Message LIKE '%스트림%끊김%' OR Message LIKE '%스트림%연결 끊김%' THEN 1 ELSE 0 END) AS Lost,
  SUM(CASE WHEN Message LIKE '%스트림%복구%' OR Message LIKE '%스트림 복구%' THEN 1 ELSE 0 END) AS Restored
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-5,GETDATE())
"@ | Format-Table -AutoSize
