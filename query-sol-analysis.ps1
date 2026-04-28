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

Write-Host "=== [1] SOL 최근 매매기록 (유저1) ===" -ForegroundColor Cyan
Q @"
SELECT Id, Side, EntryPrice, PnL, PnLPercent, Strategy,
    EntryTime, ExitTime, IsClosed, LEFT(ExitReason,50) AS Reason
FROM TradeHistory WHERE UserId=1 AND Symbol='SOLUSDT'
  AND EntryTime >= '2026-04-13'
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] SOL 5분봉 최근 3시간 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 36 OpenTime, [Open], High, Low, [Close],
    CAST(([Close]-[Open])/[Open]*100 AS decimal(6,2)) AS ChgPct
FROM CandleData WHERE Symbol='SOLUSDT' AND IntervalText='5m'
ORDER BY OpenTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] MajorCoinStrategy SOL 시그널 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-30000 FROM FooterLogs)
  AND Message LIKE '%SOLUSDT%'
  AND (Message LIKE '%side=SHORT%' OR Message LIKE '%side=LONG%' OR Message LIKE '%MAJOR%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] ML.NET SOL 예측 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-30000 FROM FooterLogs)
  AND Message LIKE '%SOLUSDT%' AND Message LIKE '%ML.NET%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] MajorForecaster 학습 로그 ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Id, Timestamp, LEFT(Message, 280) AS Msg
FROM FooterLogs WHERE Id > (SELECT MAX(Id)-100000 FROM FooterLogs)
  AND (Message LIKE '%MajorForecaster%' OR Message LIKE '%Major%학습%' OR Message LIKE '%Major%AccA%' OR Message LIKE '%major_forecast%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
