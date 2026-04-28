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

Write-Host "=== ZEC '감시 연결' '외부복원' '브라켓 등록' 로그 ===" -ForegroundColor Cyan
Q @"
SELECT Id, [Timestamp], LEFT(Message,230) AS Msg
FROM FooterLogs
WHERE Message LIKE '%ZECUSDT%'
  AND (Message LIKE '%감시 연결%' OR Message LIKE '%PUMP 감시%' OR Message LIKE '%Pump 감시%' OR Message LIKE '%표준 포지션%'
       OR Message LIKE '%외부복원%' OR Message LIKE '%브라켓%' OR Message LIKE '%account-update%'
       OR Message LIKE '%EntryOrderReg%' OR Message LIKE '%external-position%' OR Message LIKE '%ACCOUNT_UPDATE%'
       OR Message LIKE '%Meme Coin Mode%' OR Message LIKE '%Major Coin Mode%' OR Message LIKE '%복원%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap

Write-Host "=== ZEC 청산 직전 5분 로그 (23:54 ~ 23:59) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 50 Id, [Timestamp], LEFT(Message,200) AS Msg
FROM FooterLogs
WHERE [Timestamp] >= '2026-04-19 23:54:00'
  AND [Timestamp] <= '2026-04-19 23:59:30'
  AND Message LIKE '%ZECUSDT%'
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap

Write-Host "=== 봇 시작 직후 15:00 ~ 15:05 로그 (전체) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Id, [Timestamp], LEFT(Message,210) AS Msg
FROM FooterLogs
WHERE [Timestamp] >= '2026-04-19 14:59:00'
  AND [Timestamp] <= '2026-04-19 15:02:00'
  AND (Message LIKE '%감시%' OR Message LIKE '%external%' OR Message LIKE '%ACCOUNT_UPDATE%'
       OR Message LIKE '%복원%' OR Message LIKE '%브라켓%' OR Message LIKE '%동기%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap
