function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create()
    $a.Key = $k
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

Write-Host "=== [1] XRP FooterLogs 03:00~03:30 UTC (KST 12:00~12:30) 전체 ===" -ForegroundColor Cyan
Q @"
SELECT Id, Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%XRPUSDT%'
  AND Timestamp >= '2026-04-12 02:50:00'
  AND Timestamp <= '2026-04-12 03:40:00'
ORDER BY Id ASC
"@ | Format-List

Write-Host ""
Write-Host "=== [2] XRP AI_GATE BLOCK 최근 24h — 전부 ML_Zero_Confidence 인가? ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%XRPUSDT%AI_GATE%'
  AND Timestamp >= DATEADD(HOUR, -24, GETDATE())
ORDER BY Id DESC
"@ | Format-List

Write-Host ""
Write-Host "=== [3] XRP 관련 ENTRY 관문 로그 (최근 12h, AI_GATE 제외) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 40 Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%XRPUSDT%ENTRY%'
  AND Timestamp >= DATEADD(HOUR, -12, GETDATE())
  AND Message NOT LIKE '%AI_GATE%'
  AND Message NOT LIKE '%AI_BLEND%'
ORDER BY Id DESC
"@ | Format-List

Write-Host ""
Write-Host "=== [4] ML_Zero_Confidence 발생 건수 (최근 48h, 심볼별) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN Message LIKE '%BTCUSDT%' THEN 'BTCUSDT'
        WHEN Message LIKE '%ETHUSDT%' THEN 'ETHUSDT'
        WHEN Message LIKE '%XRPUSDT%' THEN 'XRPUSDT'
        WHEN Message LIKE '%SOLUSDT%' THEN 'SOLUSDT'
        WHEN Message LIKE '%BNBUSDT%' THEN 'BNBUSDT'
        ELSE 'OTHER'
    END AS Symbol,
    COUNT(*) AS BlockCount
FROM FooterLogs
WHERE Message LIKE '%ML_Zero_Confidence%'
  AND Timestamp >= DATEADD(HOUR, -48, GETDATE())
GROUP BY
    CASE
        WHEN Message LIKE '%BTCUSDT%' THEN 'BTCUSDT'
        WHEN Message LIKE '%ETHUSDT%' THEN 'ETHUSDT'
        WHEN Message LIKE '%XRPUSDT%' THEN 'XRPUSDT'
        WHEN Message LIKE '%SOLUSDT%' THEN 'SOLUSDT'
        WHEN Message LIKE '%BNBUSDT%' THEN 'BNBUSDT'
        ELSE 'OTHER'
    END
ORDER BY BlockCount DESC
"@ | Format-Table -AutoSize

Write-Host ""
Write-Host "=== [5] MajorCoinStrategy AI_BLEND ml=? tf=? 추이 (XRP) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%XRPUSDT%AI_BLEND%'
  AND Timestamp >= DATEADD(HOUR, -24, GETDATE())
ORDER BY Id DESC
"@ | Format-List
