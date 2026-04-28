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

Write-Host "=== [1] UserId=1 12:00 시점 활성 포지션 (BLESS 진입 시점) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryTime, ExitTime, IsClosed, Strategy, LEFT(Category,10) AS Cat
FROM TradeHistory WHERE UserId=1
  AND (IsClosed=0 OR (ExitTime >= '2026-04-14 12:00:00'))
  AND EntryTime <= '2026-04-14 12:00:00'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] UserId=1 PUMP/SPIKE 전체 (4/14) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryTime, ExitTime, IsClosed, Strategy, LEFT(Category,10) AS Cat, PnL
FROM TradeHistory WHERE UserId=1 AND Category IN ('PUMP','SPIKE')
  AND EntryTime >= '2026-04-14'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] UserId=10 PUMP/SPIKE 전체 (4/14) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryTime, ExitTime, IsClosed, Strategy, LEFT(Category,10) AS Cat, PnL
FROM TradeHistory WHERE UserId=10 AND Category IN ('PUMP','SPIKE')
  AND EntryTime >= '2026-04-14'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] bull0 로그 vs TICK_SURGE 로그 시간대 비교 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Id, Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%BLESSUSDT%' AND Message LIKE '%TICK_SURGE%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] BLESSUSDT ML.NET 예측 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%BLESSUSDT%' AND Message LIKE '%ML.NET%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [6] BLESSUSDT bull 판정 상세 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%BLESSUSDT%' AND (Message LIKE '%bull%' OR Message LIKE '%PREDICT%' OR Message LIKE '%confidence%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [7] FIGHTUSDT 유저1 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%FIGHTUSDT%'
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%BLOCK%' OR Message LIKE '%bull%' OR Message LIKE '%SIGNAL%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize
