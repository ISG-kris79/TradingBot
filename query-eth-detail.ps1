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

Write-Host "=== [A] ETH SHORT 포지션 진입/청산 기록 ===" -ForegroundColor Cyan
Q @"
SELECT Id, UserId, Side, EntryPrice, Quantity, PnL, PnLPercent,
    LEFT(ExitReason,60) AS Reason, EntryTime, ExitTime,
    Strategy, LEFT(Category,10) AS Cat
FROM TradeHistory WHERE Symbol='ETHUSDT'
  AND EntryTime >= '2026-04-12' AND EntryTime <= '2026-04-14 12:00'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [B] ETH 로그 21:00~06:00 (급등 구간) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE Message LIKE '%ETHUSDT%'
  AND Timestamp BETWEEN '2026-04-13 21:00:00' AND '2026-04-14 06:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [C] MajorCoinStrategy 전체 LONG 시그널 04-13~04-14 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs WHERE Message LIKE '%ETHUSDT%' AND Message LIKE '%side=LONG%'
  AND Timestamp BETWEEN '2026-04-13 00:00:00' AND '2026-04-14 12:00:00'
ORDER BY Id ASC
"@ | Format-Table -AutoSize
