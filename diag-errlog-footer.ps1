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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== A. ErrorLog 테이블 last 30min top 20 ====" -ForegroundColor Cyan
$tbl = Q "SELECT name FROM sys.tables WHERE name LIKE '%Error%' OR name LIKE '%Log%'"
$tbl | Format-Table -AutoSize

Write-Host ""
Write-Host "==== B. FooterLogs last 30min - error/실패/예외 키워드 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 20 LEFT(Message, 250) AS Msg, COUNT(*) AS Cnt
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND (Message LIKE '%error%' OR Message LIKE '%ERROR%' OR Message LIKE '%실패%'
    OR Message LIKE '%예외%' OR Message LIKE '%Exception%' OR Message LIKE '%-1003%'
    OR Message LIKE '%-1021%' OR Message LIKE '%-2015%' OR Message LIKE '%REJECT%'
    OR Message LIKE '%timeout%' OR Message LIKE '%❌%' OR Message LIKE '%⚠%')
GROUP BY LEFT(Message, 250)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== C. FooterLogs last 30min - 진입/주문/GATE-PASS 통계 ====" -ForegroundColor Cyan
Q @"
SELECT
  CASE
    WHEN Message LIKE '%[GATE-PASS]%'   THEN '01_GATE-PASS'
    WHEN Message LIKE '%[GATE]%차단%'  THEN '02_GATE-BLOCK'
    WHEN Message LIKE '%주문%'         THEN '03_주문'
    WHEN Message LIKE '%진입%'         THEN '04_진입'
    WHEN Message LIKE '%FILLED%'       THEN '05_FILLED'
    WHEN Message LIKE '%-1003%'        THEN '99_RATELIMIT'
    ELSE '00_OTHER'
  END AS Type, COUNT(*) AS Cnt
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
GROUP BY
  CASE
    WHEN Message LIKE '%[GATE-PASS]%'   THEN '01_GATE-PASS'
    WHEN Message LIKE '%[GATE]%차단%'  THEN '02_GATE-BLOCK'
    WHEN Message LIKE '%주문%'         THEN '03_주문'
    WHEN Message LIKE '%진입%'         THEN '04_진입'
    WHEN Message LIKE '%FILLED%'       THEN '05_FILLED'
    WHEN Message LIKE '%-1003%'        THEN '99_RATELIMIT'
    ELSE '00_OTHER'
  END
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize
