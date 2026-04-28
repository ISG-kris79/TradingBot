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

Write-Host "=== [1] TRADOORUSDT 매매기록 ===" -ForegroundColor Cyan
Q @"
SELECT UserId, Symbol, Side, EntryPrice, PnL, PnLPercent, Strategy, LEFT(Category,10) AS Cat,
    EntryTime, ExitTime, IsClosed, LEFT(ExitReason,60) AS Reason
FROM TradeHistory WHERE Symbol='TRADOORUSDT' AND EntryTime >= '2026-04-14'
ORDER BY UserId, EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [2] PLUMEUSDT 매매기록 ===" -ForegroundColor Cyan
Q @"
SELECT UserId, Symbol, Side, EntryPrice, PnL, PnLPercent, Strategy, LEFT(Category,10) AS Cat,
    EntryTime, ExitTime, IsClosed, LEFT(ExitReason,60) AS Reason
FROM TradeHistory WHERE Symbol='PLUMEUSDT' AND EntryTime >= '2026-04-14'
ORDER BY UserId, EntryTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] TRADOORUSDT 5분봉 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 OpenTime, [Open], High, Low, [Close],
    CAST(([Close]-[Open])/[Open]*100 AS decimal(6,2)) AS ChgPct
FROM CandleData WHERE Symbol='TRADOORUSDT' AND IntervalText='5m'
  AND OpenTime >= DATEADD(HOUR, -2, GETDATE())
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] PLUMEUSDT 5분봉 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 OpenTime, [Open], High, Low, [Close],
    CAST(([Close]-[Open])/[Open]*100 AS decimal(6,2)) AS ChgPct
FROM CandleData WHERE Symbol='PLUMEUSDT' AND IntervalText='5m'
  AND OpenTime >= DATEADD(HOUR, -2, GETDATE())
ORDER BY OpenTime ASC
"@ | Format-Table -AutoSize

Write-Host "=== [5] TRADOORUSDT 진입 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-50000 FROM FooterLogs)
  AND Message LIKE '%TRADOORUSDT%'
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%FILLED%' OR Message LIKE '%BLOCK%' OR Message LIKE '%RR%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [6] PLUMEUSDT 진입 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 15 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-50000 FROM FooterLogs)
  AND Message LIKE '%PLUMEUSDT%'
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%FILLED%' OR Message LIKE '%BLOCK%' OR Message LIKE '%RR%')
ORDER BY Id ASC
"@ | Format-Table -AutoSize
