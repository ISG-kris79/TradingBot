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

Write-Host "=== [1] PUMP LONG 신호 발생 수 (최근 1시간) ===" -ForegroundColor Cyan
Q @"
SELECT COUNT(*) AS LongSignalCount
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETDATE())
  AND Message LIKE '%side=LONG%'
"@ | Format-Table -AutoSize

Write-Host "=== [2] EntryScheduler LIMIT 주문 발주 로그 (최근 2시간) ===" -ForegroundColor Yellow
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 300) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -2, GETDATE())
  AND Message LIKE '%[SCHED]%'
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 바이낸스 API 주문 성공/실패 로그 (최근 2시간) ===" -ForegroundColor Yellow
Q @"
SELECT TOP 20 Timestamp, LEFT(Message, 300) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -2, GETDATE())
  AND (Message LIKE '%Binance%주문%' OR Message LIKE '%ORDER%FILLED%'
       OR Message LIKE '%주문 성공%' OR Message LIKE '%주문 실패%'
       OR Message LIKE '%[SL]%' OR Message LIKE '%[TP]%'
       OR Message LIKE '%LIMIT 발주%' OR Message LIKE '%체결%완료%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] FORECAST 차단 로그 (최근 2시간) ===" -ForegroundColor Magenta
Q @"
SELECT TOP 15 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -2, GETDATE())
  AND (Message LIKE '%기회 없음%' OR Message LIKE '%FORECAST%' OR Message LIKE '%prob<%'
       OR Message LIKE '%MTF 차단%' OR Message LIKE '%예약 만료%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [5] ENTRY 단계별 BLOCK 분포 (최근 2시간) ===" -ForegroundColor Red
Q @"
SELECT
  CASE
    WHEN Message LIKE '%GATE1%BLOCK%'     THEN '1_GATE1_고점재진입차단'
    WHEN Message LIKE '%GUARD%BLOCK%'     THEN '2_GUARD_워밍업/서킷'
    WHEN Message LIKE '%COOLDOWN%'        THEN '3_COOLDOWN_손절후쿨다운'
    WHEN Message LIKE '%SLOT%BLOCK%'      THEN '4_SLOT_슬롯가득'
    WHEN Message LIKE '%일일한도%'        THEN '5_DAILY_일일한도초과'
    WHEN Message LIKE '%DUAL_BLOCK%'      THEN '6_AI_GATE_ML차단'
    WHEN Message LIKE '%AI_GATE%BLOCK%'   THEN '7_AI_GATE_기타차단'
    WHEN Message LIKE '%기회 없음%'       THEN '8_FORECAST_기회없음'
    WHEN Message LIKE '%예약 만료%'       THEN '9_SCHED_LIMIT미체결만료'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN 'A_변동성차단'
    WHEN Message LIKE '%MTF 차단%'        THEN 'B_MTF차단'
    ELSE 'Z_기타'
  END AS BlockType,
  COUNT(*) AS Cnt
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -2, GETDATE())
  AND (Message LIKE '%BLOCK%' OR Message LIKE '%기회 없음%' OR Message LIKE '%예약 만료%' OR Message LIKE '%MTF 차단%')
GROUP BY
  CASE
    WHEN Message LIKE '%GATE1%BLOCK%'     THEN '1_GATE1_고점재진입차단'
    WHEN Message LIKE '%GUARD%BLOCK%'     THEN '2_GUARD_워밍업/서킷'
    WHEN Message LIKE '%COOLDOWN%'        THEN '3_COOLDOWN_손절후쿨다운'
    WHEN Message LIKE '%SLOT%BLOCK%'      THEN '4_SLOT_슬롯가득'
    WHEN Message LIKE '%일일한도%'        THEN '5_DAILY_일일한도초과'
    WHEN Message LIKE '%DUAL_BLOCK%'      THEN '6_AI_GATE_ML차단'
    WHEN Message LIKE '%AI_GATE%BLOCK%'   THEN '7_AI_GATE_기타차단'
    WHEN Message LIKE '%기회 없음%'       THEN '8_FORECAST_기회없음'
    WHEN Message LIKE '%예약 만료%'       THEN '9_SCHED_LIMIT미체결만료'
    WHEN Message LIKE '%VOLATILITY%BLOCK%' THEN 'A_변동성차단'
    WHEN Message LIKE '%MTF 차단%'        THEN 'B_MTF차단'
    ELSE 'Z_기타'
  END
ORDER BY BlockType
"@ | Format-Table -AutoSize

Write-Host "=== [6] API 에러 코드 (최근 2시간) ===" -ForegroundColor Red
Q @"
SELECT TOP 10 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -2, GETDATE())
  AND (Message LIKE '%Code=-%' OR Message LIKE '%-2019%' OR Message LIKE '%-1021%'
       OR Message LIKE '%-1003%' OR Message LIKE '%오류 상세%' OR Message LIKE '%API Rate%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize
