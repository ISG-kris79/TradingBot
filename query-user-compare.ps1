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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] UserId=10 GUA/FIGHT/BLESS 매매기록 ===" -ForegroundColor Cyan
Q @"
SELECT Id, UserId, Symbol, Side, EntryPrice, Quantity, PnL, PnLPercent,
    LEFT(ExitReason,50) AS Reason, EntryTime, ExitTime, Strategy, LEFT(Category,10) AS Cat, IsClosed
FROM TradeHistory WHERE UserId=10 AND Symbol IN ('GUAUSDT','FIGHTUSDT','BLESSUSDT')
  AND EntryTime >= '2026-04-14'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] UserId=1 GUA/FIGHT/BLESS 매매기록 ===" -ForegroundColor Cyan
Q @"
SELECT Id, UserId, Symbol, Side, EntryPrice, Quantity, PnL, PnLPercent,
    LEFT(ExitReason,50) AS Reason, EntryTime, ExitTime, Strategy, LEFT(Category,10) AS Cat, IsClosed
FROM TradeHistory WHERE UserId=1 AND Symbol IN ('GUAUSDT','FIGHTUSDT','BLESSUSDT')
  AND EntryTime >= '2026-04-14'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] UserId=1 GUA/FIGHT/BLESS 진입 시도 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE (Message LIKE '%GUAUSDT%' OR Message LIKE '%FIGHTUSDT%' OR Message LIKE '%BLESSUSDT%')
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%BLOCK%' OR Message LIKE '%SLOT%' OR Message LIKE '%BYPASS%')
  AND Timestamp >= '2026-04-14'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] UserId=1 오늘 활성 포지션 수 ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS ActiveCount, STRING_AGG(Symbol, ', ') AS Symbols
FROM TradeHistory WHERE UserId=1 AND IsClosed=0
"@ | Format-Table -AutoSize

Write-Host "=== [5] UserId=1 오늘 PUMP 진입 건수 ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS PumpEntryCount
FROM TradeHistory WHERE UserId=1 AND Category IN ('PUMP','SPIKE')
  AND EntryTime >= CAST(GETDATE() AS DATE)
"@ | Format-Table -AutoSize

Write-Host "=== [6] 서킷브레이커 / 일일한도 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE (Message LIKE '%circuit%' OR Message LIKE '%daily%limit%' OR Message LIKE '%MAX_DAILY%' OR Message LIKE '%슬롯%' OR Message LIKE '%SLOT%FULL%')
  AND Timestamp >= '2026-04-14'
ORDER BY Id DESC
"@ | Format-Table -AutoSize
