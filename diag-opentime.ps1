. "$PSScriptRoot\query-now.ps1" 2>$null

Write-Host '=== OpenTime 컬럼 타입 ===' -ForegroundColor Cyan
Q @"
SELECT t.name AS TableName, c.name AS ColName, tp.name AS DataType
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.types tp ON tp.user_type_id = c.user_type_id
WHERE c.name = 'OpenTime'
ORDER BY t.name
"@ | Format-Table -AutoSize

Write-Host '=== AI_GATE BLOCK 사유 최근 30분 ===' -ForegroundColor Red
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,350) AS Msg
FROM FooterLogs
WHERE (Message LIKE '%AI_GATE%' AND Message LIKE '%BLOCK%')
   OR Message LIKE '%OpenTime%'
   OR Message LIKE '%opentime%'
   OR Message LIKE '%날짜%오류%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host '=== 최근 30분 전체 진입 흐름 ===' -ForegroundColor Yellow
Q @"
SELECT TOP 40 Timestamp, LEFT(Message,300) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND (Message LIKE '%ENTRY%' OR Message LIKE '%진입%' OR Message LIKE '%BLOCK%' OR Message LIKE '%OpenTime%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
