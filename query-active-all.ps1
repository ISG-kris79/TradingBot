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

Write-Host "=== [1] IsClosed=0 전체 유저 (DB 기준) ===" -ForegroundColor Cyan
Q @"
SELECT UserId, Symbol, Side, EntryPrice, Strategy, LEFT(Category,10) AS Cat, EntryTime
FROM TradeHistory WHERE IsClosed=0
ORDER BY UserId, EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] pump=3/3 차단 직전 포지션 열린 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id BETWEEN (SELECT MAX(Id)-120000 FROM FooterLogs) AND (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND (Message LIKE '%ENTRY%FILL%' OR Message LIKE '%ORDER%FILL%' OR Message LIKE '%포지션 열림%' OR Message LIKE '%진입 완료%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] _activePositions 로그 (몇개 있는지) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-120000 FROM FooterLogs)
  AND (Message LIKE '%Active:%' OR Message LIKE '%포지Active%' OR Message LIKE '%active position%' OR Message LIKE '%[POSITION]%' OR Message LIKE '%_activePositions%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 11:50~12:00 진입 성공 로그 (pump=3/3 첫 발생 직전) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-120000 FROM FooterLogs)
  AND Message LIKE '%ENTRY%FILL%'
  AND Timestamp BETWEEN '2026-04-14 11:00:00' AND '2026-04-14 12:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] 거래소 동기화 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-120000 FROM FooterLogs)
  AND (Message LIKE '%EXTERNAL%SYNC%' OR Message LIKE '%외부 포지션%' OR Message LIKE '%거래소 포지션%동기화%')
  AND Timestamp >= '2026-04-14 11:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize
