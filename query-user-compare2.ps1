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

Write-Host "=== [A] UserId=10 BLESS/FIGHT/GUA 진입시간 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, EntryTime, Strategy
FROM TradeHistory WHERE UserId=10 AND Symbol IN ('GUAUSDT','FIGHTUSDT','BLESSUSDT')
  AND EntryTime >= '2026-04-14'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [B] UserId=1 활성 포지션 + 슬롯 점유 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, IsClosed, Strategy, EntryTime, LEFT(Category,10) AS Cat
FROM TradeHistory WHERE UserId=1 AND IsClosed=0
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [C] UserId=1 오늘 PUMP/SPIKE 기록 전체 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, PnL, IsClosed, Strategy, EntryTime, LEFT(Category,10) AS Cat
FROM TradeHistory WHERE UserId=1 AND Category IN ('PUMP','SPIKE')
  AND EntryTime >= '2026-04-13'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [D] BLESSUSDT 진입 로그 (Timestamp 범위 좁힘) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-50000 FROM FooterLogs)
  AND Message LIKE '%BLESSUSDT%'
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%BLOCK%' OR Message LIKE '%SIGNAL%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [E] GUAUSDT 진입 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-50000 FROM FooterLogs)
  AND Message LIKE '%GUAUSDT%'
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%BLOCK%' OR Message LIKE '%SIGNAL%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [F] 슬롯 차단 로그 최근 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Id, Timestamp, LEFT(Message, 200) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-50000 FROM FooterLogs)
  AND (Message LIKE '%SLOT%FULL%' OR Message LIKE '%SLOT%BLOCK%' OR Message LIKE '%MAX_DAILY%' OR Message LIKE '%pumpSlot%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
