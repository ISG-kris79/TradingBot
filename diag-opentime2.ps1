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

Write-Host '=== OpenTime 컬럼 타입 (모든 테이블) ===' -ForegroundColor Cyan
Q @"
SELECT t.name AS TableName, tp.name AS DataType
FROM sys.columns c
JOIN sys.tables t ON t.object_id=c.object_id
JOIN sys.types tp ON tp.user_type_id=c.user_type_id
WHERE c.name='OpenTime'
ORDER BY t.name
"@ | Format-Table -AutoSize

Write-Host '=== 최근 AI_GATE BLOCK 전체 메시지 ===' -ForegroundColor Red
Q @"
SELECT TOP 10 Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%AI_GATE%' AND Message LIKE '%BLOCK%'
ORDER BY Id DESC
"@ | Format-List

Write-Host '=== 최근 OpenTime 오류 ===' -ForegroundColor Red
Q @"
SELECT TOP 10 Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%OpenTime%' OR Message LIKE '%opentime%'
ORDER BY Id DESC
"@ | Format-List

Write-Host '=== 최근 진입 없음 원인 (30분) ===' -ForegroundColor Yellow
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,400) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND (Message LIKE '%BLOCK%' OR Message LIKE '%차단%' OR Message LIKE '%진입 없%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
