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

Write-Host "=== [1] 최근 1시간 ML_DIAG (prob=0 진단) ===" -ForegroundColor Red
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 300) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%ML_DIAG%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 최근 1시간 DUAL_BLOCK 건수 ===" -ForegroundColor Red
Q @"
SELECT COUNT(*) AS DualBlockCount
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%DUAL_BLOCK%'
"@ | Format-Table -AutoSize

Write-Host "=== [3] 최근 1시간 API 에러 ===" -ForegroundColor Yellow
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND (Message LIKE '%Exception%' OR Message LIKE '%API%오류%' OR Message LIKE '%429%'
       OR Message LIKE '%Unauthorized%' OR Message LIKE '%Timeout%' OR Message LIKE '%연결%실패%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 최근 1시간 PUMP LONG 신호 수 ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS LongSignalCount
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%side=LONG%'
"@ | Format-Table -AutoSize

Write-Host "=== [5] EntryTimingML 학습 관련 로그 (최근 3시간) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 300) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -3, GETDATE())
  AND (Message LIKE '%EntryTimingML%' OR Message LIKE '%밸런싱%' OR Message LIKE '%AI Gate%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [6] 최근 진입 시각 ===" -ForegroundColor Green
Q @"
SELECT TOP 5 Symbol, Side, Strategy, EntryTime
FROM TradeHistory WHERE UserId=1
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize
