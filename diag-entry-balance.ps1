$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$enc = $json.ConnectionStrings.DefaultConnection
$k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
$f=[Convert]::FromBase64String($enc);$a=[System.Security.Cryptography.Aes]::Create();$a.Key=$k
$iv=New-Object byte[] $a.IV.Length;$c=New-Object byte[] ($f.Length-$a.IV.Length)
[Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length);[Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length)
$a.IV=$iv;$d=$a.CreateDecryptor($a.Key,$a.IV)
$cs=[Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length));$a.Dispose();$d.Dispose()

function Q($sql,$timeout=30) {
    $cn=New-Object System.Data.SqlClient.SqlConnection $cs;$cn.Open()
    $cm=$cn.CreateCommand();$cm.CommandText=$sql;$cm.CommandTimeout=$timeout
    $ap=New-Object System.Data.SqlClient.SqlDataAdapter $cm;$ds=New-Object System.Data.DataSet;[void]$ap.Fill($ds);$cn.Close()
    return $ds.Tables[0]
}

Write-Host '=== [1] 유저별 가용잔고 / 설정 ===' -ForegroundColor Cyan
Q @"
SELECT UserId, MaxConcurrentPositions, MaxMarginPerTrade, TotalBudget,
       AvailableBalance, ReservedBalance, IsActive
FROM UserSettings
ORDER BY UserId
"@ | Format-Table -AutoSize

Write-Host '=== [2] 현재 열린 포지션 (UserId별) ===' -ForegroundColor Cyan
Q @"
SELECT UserId, COUNT(*) AS OpenCount,
       SUM(CAST(Quantity*EntryPrice/Leverage AS DECIMAL(18,2))) AS UsedMargin
FROM TradeHistory
WHERE IsClosed=0
GROUP BY UserId
"@ | Format-Table -AutoSize

Write-Host '=== [3] 최근 24시간 진입 차단 사유 TOP ===' -ForegroundColor Red
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,400) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR,-24,GETDATE())
  AND (Message LIKE '%BLOCK%' OR Message LIKE '%차단%' OR Message LIKE '%잔고%'
       OR Message LIKE '%balance%' OR Message LIKE '%Budget%' OR Message LIKE '%슬롯%'
       OR Message LIKE '%slot%' OR Message LIKE '%margin%' OR Message LIKE '%증거금%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host '=== [4] 최근 진입 시도 흐름 (START→BLOCK 전체) ===' -ForegroundColor Yellow
Q @"
SELECT TOP 40 Timestamp, LEFT(Message,400) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR,-2,GETDATE())
  AND Message LIKE '%ENTRY%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host '=== [5] 잔고 관련 로그 최근 ===' -ForegroundColor Yellow
Q @"
SELECT TOP 20 Timestamp, LEFT(Message,350) AS Msg
FROM FooterLogs
WHERE Message LIKE '%잔고%' OR Message LIKE '%AvailableBalance%'
   OR Message LIKE '%available%' OR Message LIKE '%10%달러%' OR Message LIKE '%budget%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host '=== [6] 학습 완료 여부 + 진입 워밍업 ===' -ForegroundColor Magenta
Q @"
SELECT TOP 10 Timestamp, LEFT(Message,300) AS Msg
FROM FooterLogs
WHERE Message LIKE '%초기학습%' OR Message LIKE '%warming%' OR Message LIKE '%warmup%'
   OR Message LIKE '%진입 활성%' OR Message LIKE '%IsInitialTraining%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host '=== [7] 서킷브레이커 / 긴급차단 상태 ===' -ForegroundColor Red
Q @"
SELECT TOP 10 Timestamp, LEFT(Message,300) AS Msg
FROM FooterLogs
WHERE Message LIKE '%서킷%' OR Message LIKE '%circuit%' OR Message LIKE '%긴급%'
   OR Message LIKE '%emergency%' OR Message LIKE '%드로우다운%' OR Message LIKE '%drawdown%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize
