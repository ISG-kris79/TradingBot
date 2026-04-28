function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [System.Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] PLAYUSDT 거래 내역 ===" -ForegroundColor Cyan
Q @"
SELECT Id, EntryPrice, ExitPrice, PnL, PnLPercent,
       LEFT(ExitReason, 60) AS Reason, EntryTime, ExitTime,
       DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec, Strategy
FROM TradeHistory WHERE Symbol='PLAYUSDT' AND UserId=1
  AND EntryTime >= DATEADD(HOUR, -6, GETDATE())
ORDER BY EntryTime ASC
"@ | Format-List

Write-Host "=== [2] PLAYUSDT SL/TP/TRAILING 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%PLAYUSDT%'
  AND (Message LIKE '%[SL]%' OR Message LIKE '%[TP]%' OR Message LIKE '%TRAILING%'
       OR Message LIKE '%EntryOrderReg%' OR Message LIKE '%STOP%' OR Message LIKE '%api%'
       OR Message LIKE '%손절%' OR Message LIKE '%등록%')
  AND Timestamp >= DATEADD(HOUR, -6, GETDATE())
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [3] PLAYUSDT 진입/청산 전후 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 40 Timestamp, LEFT(Message, 220) AS Msg
FROM FooterLogs
WHERE Message LIKE '%PLAYUSDT%'
  AND Timestamp >= DATEADD(HOUR, -3, GETDATE())
ORDER BY Id ASC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 손절 직후 재진입 패턴 확인 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, COUNT(*) AS EntryCount,
    MIN(EntryTime) AS FirstEntry, MAX(EntryTime) AS LastEntry,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(PnL) AS NetPnL
FROM TradeHistory WHERE UserId=1
  AND EntryTime >= DATEADD(HOUR, -3, GETDATE())
GROUP BY Symbol HAVING COUNT(*) >= 2
ORDER BY EntryCount DESC
"@ | Format-Table -AutoSize
