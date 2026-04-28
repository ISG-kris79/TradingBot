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

Write-Host "=== [1] TRAILING 관련 모든 로그 (최근 3h) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message, 250) AS Msg
FROM FooterLogs
WHERE (Message LIKE '%TRAILING%' OR Message LIKE '%trailing%' OR Message LIKE '%[SL]%' OR Message LIKE '%[TP]%'
       OR Message LIKE '%EntryOrderReg%' OR Message LIKE '%STOP_MARKET%' OR Message LIKE '%TAKE_PROFIT%')
  AND Timestamp >= DATEADD(HOUR, -3, GETDATE())
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] SL/TP/TRAILING 성공/실패 분포 (최근 6h) ===" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN Message LIKE '%[SL]%성공%' OR Message LIKE '%[SL]%STOP_MARKET 등록%' THEN 'SL_OK'
        WHEN Message LIKE '%[SL]%실패%' OR Message LIKE '%[SL]%STOP_MARKET 실패%' THEN 'SL_FAIL'
        WHEN Message LIKE '%[TP]%TAKE_PROFIT%등록%' OR Message LIKE '%[TP]%성공%' THEN 'TP_OK'
        WHEN Message LIKE '%[TP]%실패%' THEN 'TP_FAIL'
        WHEN Message LIKE '%TRAILING%등록 완료%' OR Message LIKE '%TRAILING%성공%' THEN 'TRAIL_OK'
        WHEN Message LIKE '%TRAILING%실패%' THEN 'TRAIL_FAIL'
        WHEN Message LIKE '%TRAILING_STOP%errCode%' THEN 'TRAIL_ERR'
        ELSE 'OTHER'
    END AS Kind,
    COUNT(*) AS Cnt
FROM FooterLogs
WHERE (Message LIKE '%[SL]%' OR Message LIKE '%[TP]%' OR Message LIKE '%TRAILING%')
  AND Timestamp >= DATEADD(HOUR, -6, GETDATE())
GROUP BY CASE
    WHEN Message LIKE '%[SL]%성공%' OR Message LIKE '%[SL]%STOP_MARKET 등록%' THEN 'SL_OK'
    WHEN Message LIKE '%[SL]%실패%' OR Message LIKE '%[SL]%STOP_MARKET 실패%' THEN 'SL_FAIL'
    WHEN Message LIKE '%[TP]%TAKE_PROFIT%등록%' OR Message LIKE '%[TP]%성공%' THEN 'TP_OK'
    WHEN Message LIKE '%[TP]%실패%' THEN 'TP_FAIL'
    WHEN Message LIKE '%TRAILING%등록 완료%' OR Message LIKE '%TRAILING%성공%' THEN 'TRAIL_OK'
    WHEN Message LIKE '%TRAILING%실패%' THEN 'TRAIL_FAIL'
    WHEN Message LIKE '%TRAILING_STOP%errCode%' THEN 'TRAIL_ERR'
    ELSE 'OTHER'
END
ORDER BY Kind
"@ | Format-Table -AutoSize
