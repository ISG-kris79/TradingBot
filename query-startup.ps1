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

Write-Host "=== 봇 시작 3분간 전체 로그 (15:00~15:03) — 동기화/브라켓/재시작 키워드 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 60 Id, [Timestamp], LEFT(Message,220) AS Msg
FROM FooterLogs
WHERE [Timestamp] >= '2026-04-19 14:59:00'
  AND [Timestamp] <= '2026-04-19 15:05:00'
  AND (Message LIKE '%재시작%' OR Message LIKE '%동기화%' OR Message LIKE '%브라켓%'
       OR Message LIKE '%동기%' OR Message LIKE '%SYNC%' OR Message LIKE '%복원%'
       OR Message LIKE '%감시 연결%' OR Message LIKE '%폴링 감시%'
       OR Message LIKE '%Pump 감시%' OR Message LIKE '%ZEC%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap
