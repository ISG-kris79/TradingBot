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

Write-Host "=== [A] BLESSUSDT TICK_SURGE 발동 but BLACKLIST 차단 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%BLESSUSDT%' AND (Message LIKE '%TICK_SURGE%' OR Message LIKE '%BLACKLIST%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [B] GUAUSDT 진입 시도 전체 흐름 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%GUAUSDT%' AND Message LIKE '%ENTRY%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [C] bull0 차단 — 어떤 심볼이든 TICK_SURGE 성공 케이스 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-80000 FROM FooterLogs)
  AND Message LIKE '%TICK_SURGE%' AND Message LIKE '%ENTRY%FILL%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [D] UserId=1 TICK_SURGE 진입 성공 전체 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, EntryTime, Strategy, PnL
FROM TradeHistory WHERE UserId=1 AND Strategy='TICK_SURGE'
  AND EntryTime >= '2026-04-14'
ORDER BY EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [E] pump_signal 모델 학습 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-100000 FROM FooterLogs)
  AND (Message LIKE '%pump_signal%학습%' OR Message LIKE '%PumpSignal%Train%' OR Message LIKE '%pump_signal%zip%' OR Message LIKE '%학습완료%428%' OR Message LIKE '%학습완료%' OR Message LIKE '%학습 완료%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
