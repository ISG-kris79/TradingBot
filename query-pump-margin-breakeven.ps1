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

Write-Host "=== [1] PUMP/SPIKE $150 진입 + 잔량 $36 케이스 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Id, UserId, Symbol, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent,
    Strategy, EntryTime, ExitTime, IsClosed, LEFT(ExitReason,60) AS Reason,
    CAST(EntryPrice * ABS(Quantity) / 20 AS decimal(10,2)) AS Margin20x
FROM TradeHistory WHERE UserId=1 AND Category IN ('PUMP','SPIKE')
  AND EntryTime >= '2026-04-14 20:00:00'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] PUMP 마진 SIZE 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-30000 FROM FooterLogs)
  AND Message LIKE '%SIZE%BASE%' AND Message LIKE '%margin=150%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 본절 후 잔량 36불 케이스 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-30000 FROM FooterLogs)
  AND (Message LIKE '%본절%' OR Message LIKE '%BreakEven%' OR Message LIKE '%PARTIAL%40%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
