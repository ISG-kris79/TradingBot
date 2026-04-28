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

Write-Host "=== [1] 최근 차단 로그 TOP 15 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-5000 FROM FooterLogs)
  AND Message LIKE '%BLOCK%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 최근 진입 시도 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-5000 FROM FooterLogs)
  AND (Message LIKE '%AI_ENTRY%' OR Message LIKE '%TICK_SURGE%ENTRY%START%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 슬롯 상태 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-5000 FROM FooterLogs)
  AND (Message LIKE '%pump=%' OR Message LIKE '%SLOT%' OR Message LIKE '%ENTRY BLOCK%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 유저1 활성 포지션 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, Strategy, LEFT(Category,10) AS Cat, EntryTime
FROM TradeHistory WHERE UserId=1 AND IsClosed=0
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] bull0 최근 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Id, Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-5000 FROM FooterLogs)
  AND Message LIKE '%bull0%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize
