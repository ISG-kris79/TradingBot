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

Write-Host "=== [1] 유저1 pump=3/3 최초 발생 직전 ENTRY FILLED 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id BETWEEN 6200000 AND 6210000
  AND (Message LIKE '%ENTRY%FILL%' OR Message LIKE '%ORDER%FILL%' OR Message LIKE '%진입 완료%' OR Message LIKE '%포지션 등록%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 11:50~11:55 사이 모든 ENTRY 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id BETWEEN 6205000 AND 6210000
  AND Message LIKE '%ENTRY%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] pump=3/3 차단 직전 어떤 심볼이 들어왔는지 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id BETWEEN 6195000 AND 6210000
  AND (Message LIKE '%FILLED%' OR Message LIKE '%new position%' OR Message LIKE '%activePositions%add%' OR Message LIKE '%ACCOUNT_UPDATE%' OR Message LIKE '%포지션 열림%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 유저1 UserId 확인 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-120000 FROM FooterLogs)
  AND (Message LIKE '%UserId%' OR Message LIKE '%CurrentUser%' OR Message LIKE '%로그인%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize
