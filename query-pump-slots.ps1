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

Write-Host "=== [1] UserId=1 IsClosed=0 전체 (DB기준 활성 포지션) ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, Quantity, Strategy, LEFT(Category,10) AS Cat, EntryTime, IsClosed
FROM TradeHistory WHERE UserId=1 AND IsClosed=0
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] UserId=1 오늘 PUMP/SPIKE 거래 전체 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, Strategy, LEFT(Category,10) AS Cat, EntryTime, ExitTime, IsClosed, PnL
FROM TradeHistory WHERE UserId=1 AND Category IN ('PUMP','SPIKE')
  AND EntryTime >= '2026-04-13'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] pump=3/3 시점 전후 슬롯 카운트 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-100000 FROM FooterLogs)
  AND (Message LIKE '%pump=%' OR Message LIKE '%pumpSlot%' OR Message LIKE '%PUMP 슬롯%' OR Message LIKE '%activePos%pump%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] pump=3/3 차단 첫 발생 시점 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-100000 FROM FooterLogs)
  AND Message LIKE '%pump=3/3%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] 슬롯 점유 로그 (CanAcceptNewEntry) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-100000 FROM FooterLogs)
  AND (Message LIKE '%CanAccept%' OR Message LIKE '%슬롯 현황%' OR Message LIKE '%slot count%' OR Message LIKE '%major=%' OR Message LIKE '%Active:%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize
